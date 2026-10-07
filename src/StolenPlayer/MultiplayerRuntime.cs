using System;
using StolenPlayer.Protocol;
using StolenPlayer.Sessions;
using StolenPlayer.World;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StolenPlayer;

internal sealed class MultiplayerRuntime : MonoBehaviour
{
  private const int WindowId = 17562026;
  private readonly Rect _initialWindow = new Rect(24, 80, 400, 285);
  private PluginConfig? _config;
  private MultiplayerSession? _session;
  private Rect _window;
  private string _address = "127.0.0.1";
  private string _portInput = string.Empty;
  private bool _windowOpen = true;
  private GameObject? _visualPreview;
  private readonly PlayerStateReader _playerStateReader = new PlayerStateReader();
  private RemotePlayerManager? _remotePlayers;
  private float _nextPosePublishTime;
  private AsyncOperation? _sceneLoadOperation;
  private PlayerPoseData? _pendingHostSpawnPose;
  private bool _requiresHostSpawn;

  internal void Initialize(PluginConfig config)
  {
    _config = config;
    _session = new MultiplayerSession(config, Application.version, Plugin.PluginVersion);
    _session.HostSceneReceived += OnHostSceneReceived;
    _session.HostPoseReceived += OnHostPoseReceived;
    _remotePlayers = gameObject.AddComponent<RemotePlayerManager>();
    _remotePlayers.Initialize(_session);
    gameObject.AddComponent<StaticWorldIdentityScanner>();
    _window = _initialWindow;
    _portInput = config.ListenPort.Value.ToString();
  }

  private void Update()
  {
    if (Input.GetKeyDown(KeyCode.F8))
    {
      _windowOpen = !_windowOpen;
    }

    if (Input.GetKeyDown(KeyCode.F10))
    {
      ToggleVisualPreview();
    }

    _session?.Update();
    UpdateClientSceneSynchronization();
    ApplyPendingHostSpawn();
    if (_session != null && Time.unscaledTime >= _nextPosePublishTime
        && (_session.IsHost || _session.ReadyPeerCount > 0)
        && _playerStateReader.TryRead(out var pose))
    {
      _nextPosePublishTime = Time.unscaledTime + 0.1f;
      _session.PublishLocalPose(pose);
    }
  }

  private void OnGUI()
  {
    if (!_windowOpen)
    {
      GUI.Label(new Rect(12, 12, 150, 24), "F8: Multiplayer");
      return;
    }

    _window = GUI.Window(WindowId, _window, DrawWindow, "StolenPlayer - Direct IP Co-op");
  }

  private void OnApplicationQuit()
  {
    DisposeSession();
    DestroyVisualPreview();
  }

  private void OnDestroy()
  {
    DisposeSession();
    DestroyVisualPreview();
  }

  private void ToggleVisualPreview()
  {
    if (_visualPreview != null)
    {
      DestroyVisualPreview();
      Plugin.Log.LogInfo("Removed local NPC visual preview.");
      return;
    }

    if (RemotePlayerVisualPreview.TryCreate(Camera.main, out var preview, out var error))
    {
      _visualPreview = preview;
      return;
    }

    Plugin.Log.LogWarning(error);
  }

  private void DestroyVisualPreview()
  {
    if (_visualPreview != null)
    {
      Destroy(_visualPreview);
    }

    _visualPreview = null;
  }

  private void DrawWindow(int id)
  {
    GUILayout.BeginVertical();
    GUILayout.Label(_session?.Status ?? "Network session unavailable.");
    GUILayout.Space(8);

    var state = _session?.State ?? SessionState.Error;
    if (state == SessionState.Error)
    {
      if (GUILayout.Button("Reset session"))
      {
        _session?.Leave();
      }
    }
    else if (state == SessionState.Offline)
    {
      GUILayout.Label("TCP port");
      _portInput = GUILayout.TextField(_portInput, 5);
      var validPort = int.TryParse(_portInput, out var port) && port >= 1024 && port <= 65535;
      GUI.enabled = validPort;
      if (GUILayout.Button("Host direct IP session"))
      {
        _session?.Host(port);
      }

      GUILayout.BeginHorizontal();
      _address = GUILayout.TextField(_address, 64);
      var canJoin = validPort && !string.IsNullOrWhiteSpace(_address);
      GUI.enabled = canJoin;
      if (GUILayout.Button("Join IP", GUILayout.Width(80)))
      {
        _session?.Join(_address.Trim(), port);
      }

      GUILayout.EndHorizontal();
      GUI.enabled = true;
      GUILayout.Label("Clients enter the host's IP and TCP port.");
      GUILayout.Label("Internet play may need port forwarding and firewall access.");
    }
    else
    {
      if (_session?.IsHost == true)
      {
        GUILayout.Label($"Listening on TCP port {_session.ListenPort}");
      }

      GUILayout.Label($"Ready peers: {_session?.ReadyPeerCount ?? 0}");
      if (GUILayout.Button("Leave session"))
      {
        _session?.Leave();
      }
    }

    GUILayout.EndVertical();
    GUI.DragWindow(new Rect(0, 0, 10000, 24));
  }

  private void DisposeSession()
  {
    if (_session != null)
    {
      _session.HostSceneReceived -= OnHostSceneReceived;
      _session.HostPoseReceived -= OnHostPoseReceived;
    }

    _session?.Dispose();
    _session = null;
  }

  private void OnHostSceneReceived(string sceneName)
  {
    _requiresHostSpawn = true;
    _pendingHostSpawnPose = null;
    Plugin.Log.LogInfo($"Loading host scene '{sceneName}' on this client.");
  }

  private void OnHostPoseReceived(PlayerPoseData pose)
  {
    if (_requiresHostSpawn)
    {
      _pendingHostSpawnPose = pose;
    }
  }

  private void ApplyPendingHostSpawn()
  {
    if (!_requiresHostSpawn || !_pendingHostSpawnPose.HasValue || _session == null
        || !_session.HasClientRoster || !_session.IsClientSceneReady)
    {
      return;
    }

    var hostPose = _pendingHostSpawnPose.Value;
    var yawRadians = hostPose.Yaw * Mathf.Deg2Rad;
    var spawnPosition = new Vector3(
      hostPose.X + Mathf.Cos(yawRadians) * 1.5f,
      hostPose.Y,
      hostPose.Z - Mathf.Sin(yawRadians) * 1.5f);
    if (!_playerStateReader.TryTeleportToFeet(spawnPosition, hostPose.Yaw, _session.ExpectedHostScene))
    {
      return;
    }

    _requiresHostSpawn = false;
    _pendingHostSpawnPose = null;
    _session.CompleteClientInitialSynchronization();
  }

  private void UpdateClientSceneSynchronization()
  {
    if (_session == null || _session.IsHost || _session.State != SessionState.Connected
        || string.IsNullOrEmpty(_session.ExpectedHostScene))
    {
      return;
    }

    var expectedScene = _session.ExpectedHostScene;
    var activeScene = SceneManager.GetActiveScene();
    if (activeScene.IsValid() && activeScene.isLoaded && string.Equals(activeScene.name, expectedScene, StringComparison.Ordinal))
    {
      _sceneLoadOperation = null;
      _session.MarkClientSceneReady(expectedScene);
      return;
    }

    if (_sceneLoadOperation != null && !_sceneLoadOperation.isDone)
    {
      return;
    }

#pragma warning disable CS0618
    if (!Application.CanStreamedLevelBeLoaded(expectedScene))
    {
      _session.FailSceneSynchronization($"'{expectedScene}' is not present in this game's build settings.");
      return;
    }
#pragma warning restore CS0618

    try
    {
      _sceneLoadOperation = SceneManager.LoadSceneAsync(expectedScene, LoadSceneMode.Single);
      if (_sceneLoadOperation == null)
      {
        _session.FailSceneSynchronization($"Unity did not start loading '{expectedScene}'.");
        return;
      }

      Plugin.Log.LogInfo($"Client scene load started for host scene '{expectedScene}'.");
    }
    catch (Exception exception)
    {
      _session.FailSceneSynchronization(exception.Message);
    }
  }
}
