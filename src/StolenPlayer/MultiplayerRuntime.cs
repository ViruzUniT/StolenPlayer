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
  private string _playerNameInput = string.Empty;
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
    SceneManager.activeSceneChanged += OnActiveSceneChanged;
    _remotePlayers = gameObject.AddComponent<RemotePlayerManager>();
    _remotePlayers.Initialize(_session, config);
    var identities = gameObject.AddComponent<StaticWorldIdentityScanner>();
    gameObject.AddComponent<DoorInteractionReplicator>().Initialize(_session, identities);
    SceneManager.sceneLoaded += OnSceneLoaded;
    SceneManager.sceneUnloaded += OnSceneUnloaded;
    _window = _initialWindow;
    _portInput = config.ListenPort.Value.ToString();
    _playerNameInput = config.PlayerName.Value;
  }

  private void Start()
  {
    if (_config == null || _session == null)
    {
      return;
    }

    var role = _config.StartupRole.Value.Trim();
    if (string.Equals(role, "Host", StringComparison.OrdinalIgnoreCase))
    {
      Plugin.Log.LogInfo($"Local test profile auto-starting host on port {_config.StartupPort.Value}.");
      _session.Host(_config.StartupPort.Value);
    }
    else if (string.Equals(role, "Client", StringComparison.OrdinalIgnoreCase))
    {
      Plugin.Log.LogInfo($"Local test profile auto-joining {_config.StartupAddress.Value}:{_config.StartupPort.Value}.");
      _session.Join(_config.StartupAddress.Value.Trim(), _config.StartupPort.Value);
    }
    else if (!string.Equals(role, "None", StringComparison.OrdinalIgnoreCase))
    {
      Plugin.Log.LogWarning($"Unknown LocalTest.StartupRole '{role}'; expected None, Host, or Client.");
    }
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
    if (!_requiresHostSpawn) _session?.CompleteClientInitialSynchronization();
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

    if (_config == null)
    {
      Plugin.Log.LogWarning("Cannot create the visual preview because plugin configuration is unavailable.");
      return;
    }

    if (RemotePlayerVisualPreview.TryCreate(Camera.main, _config, out var preview, out var error))
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
    GUILayout.BeginHorizontal();
    GUILayout.Label("Player name", GUILayout.Width(85));
    GUI.enabled = _session?.State == SessionState.Offline || _session?.State == SessionState.Error;
    _playerNameInput = GUILayout.TextField(_playerNameInput, 24);
    if (GUILayout.Button("Save", GUILayout.Width(52))) SavePlayerName();
    GUI.enabled = true;
    GUILayout.EndHorizontal();
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
        GUILayout.Label("Session port (TCP + UDP movement)");
      _portInput = GUILayout.TextField(_portInput, 5);
      var validPort = int.TryParse(_portInput, out var port) && port >= 1024 && port <= 65535;
      GUI.enabled = validPort;
      if (GUILayout.Button("Host direct IP session"))
      {
        if (SavePlayerName()) _session?.Host(port);
      }

      GUILayout.BeginHorizontal();
      _address = GUILayout.TextField(_address, 64);
      var canJoin = validPort && !string.IsNullOrWhiteSpace(_address);
      GUI.enabled = canJoin;
      if (GUILayout.Button("Join IP", GUILayout.Width(80)))
      {
        if (SavePlayerName()) _session?.Join(_address.Trim(), port);
      }

      GUILayout.EndHorizontal();
      GUI.enabled = true;
      GUILayout.Label("Clients enter the host's IP and session port.");
      GUILayout.Label("Internet play may need TCP and UDP port forwarding/firewall rules.");
    }
    else
    {
      if (_session?.IsHost == true)
      {
        GUILayout.Label($"Listening on TCP + UDP port {_session.ListenPort}");
      }

      GUILayout.Label($"Ready peers: {_session?.ReadyPeerCount ?? 0}");
      if (_session != null)
      {
        foreach (var player in _session.CurrentRoster)
        {
          GUILayout.Label($"• {player.PlayerName}");
        }
      }
      if (GUILayout.Button("Leave session"))
      {
        _session?.Leave();
      }
    }

    GUILayout.EndVertical();
    GUI.DragWindow(new Rect(0, 0, 10000, 24));
  }

  private bool SavePlayerName()
  {
    if (_config == null || _session == null
        || (_session.State != SessionState.Offline && _session.State != SessionState.Error))
    {
      return false;
    }

    if (!HandshakePayload.TryNormalizePlayerName(_playerNameInput, out var normalizedName))
    {
      Plugin.Log.LogWarning("Player name must contain 1–24 non-control characters.");
      return false;
    }

    _playerNameInput = normalizedName;
    _config.PlayerName.Value = normalizedName;
    _session.UpdateLocalPlayerName(normalizedName);
    return true;
  }

  private void DisposeSession()
  {
    SceneManager.activeSceneChanged -= OnActiveSceneChanged;
    SceneManager.sceneLoaded -= OnSceneLoaded;
    SceneManager.sceneUnloaded -= OnSceneUnloaded;
    if (_session != null)
    {
      _session.HostSceneReceived -= OnHostSceneReceived;
      _session.HostPoseReceived -= OnHostPoseReceived;
    }

    _session?.Dispose();
    _session = null;
  }

  private void OnActiveSceneChanged(Scene previous, Scene current)
  {
    _session?.NotifyLocalSceneChanged();
  }

  private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => _session?.NotifyLocalSceneChanged();
  private void OnSceneUnloaded(Scene scene) => _session?.NotifyLocalSceneChanged();

  private void OnHostSceneReceived(HostSceneData sceneData)
  {
    _requiresHostSpawn = true;
    _pendingHostSpawnPose = null;
    Plugin.Log.LogInfo($"Loading host scene set: active='{sceneData.ActiveScene}', loaded={string.Join(", ", sceneData.LoadedScenes)}.");
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
    if (_sceneLoadOperation != null && !_sceneLoadOperation.isDone)
    {
      return;
    }
    _sceneLoadOperation = null;

    if (!activeScene.IsValid() || !activeScene.isLoaded || !string.Equals(activeScene.name, expectedScene, StringComparison.Ordinal))
    {
      LoadClientScene(expectedScene, LoadSceneMode.Single);
      return;
    }

    if (!ReconcileClientLoadedScenes(_session.ExpectedHostScenes)) return;
    _session.MarkClientSceneReady(expectedScene);
  }

  private void LoadClientScene(string sceneName, LoadSceneMode mode)
  {
#pragma warning disable CS0618
    if (!Application.CanStreamedLevelBeLoaded(sceneName))
    {
      _session?.FailSceneSynchronization($"'{sceneName}' is not present in this game's build settings.");
      return;
    }
#pragma warning restore CS0618

    try
    {
      _sceneLoadOperation = SceneManager.LoadSceneAsync(sceneName, mode);
      if (_sceneLoadOperation == null)
      {
        _session?.FailSceneSynchronization($"Unity did not start loading '{sceneName}' ({mode}).");
        return;
      }

      Plugin.Log.LogInfo($"Client scene load started for '{sceneName}' ({mode}).");
    }
    catch (Exception exception)
    {
      _session?.FailSceneSynchronization(exception.Message);
    }
  }

  private bool ReconcileClientLoadedScenes(string[] expectedScenes)
  {
    var expected = new System.Collections.Generic.HashSet<string>(expectedScenes, StringComparer.Ordinal);
    var loaded = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
    var activeName = SceneManager.GetActiveScene().name;

    for (var index = 0; index < SceneManager.sceneCount; index++)
    {
      var scene = SceneManager.GetSceneAt(index);
      if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrWhiteSpace(scene.path)) continue;
      if (!loaded.Add(scene.name))
      {
        if (!string.Equals(scene.name, activeName, StringComparison.Ordinal))
        {
          Plugin.Log.LogWarning($"Unloading duplicate additive scene '{scene.name}' before multiplayer synchronization.");
          _sceneLoadOperation = SceneManager.UnloadSceneAsync(scene);
          return false;
        }
      }
      else if (!expected.Contains(scene.name) && !string.Equals(scene.name, activeName, StringComparison.Ordinal))
      {
        Plugin.Log.LogInfo($"Unloading client-only scene '{scene.name}' to match the host.");
        _sceneLoadOperation = SceneManager.UnloadSceneAsync(scene);
        return false;
      }
    }

    foreach (var sceneName in expectedScenes)
    {
      if (loaded.Contains(sceneName)) continue;
      LoadClientScene(sceneName, LoadSceneMode.Additive);
      return false;
    }

    return loaded.SetEquals(expected);
  }
}
