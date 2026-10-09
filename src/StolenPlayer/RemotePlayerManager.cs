using System;
using System.Collections.Generic;
using StolenPlayer.Protocol;
using StolenPlayer.Sessions;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StolenPlayer;

/// <summary>
/// Owns scene-scoped avatar objects. Peer identity and the latest pose remain session state;
/// this component reconciles those inputs into at most one visual object per remote peer.
/// </summary>
internal sealed class RemotePlayerManager : MonoBehaviour
{
  private readonly Dictionary<ulong, RemotePlayerAvatar> _avatars = new Dictionary<ulong, RemotePlayerAvatar>();
  private readonly Dictionary<ulong, PlayerPoseData> _latestPoses = new Dictionary<ulong, PlayerPoseData>();
  private readonly Dictionary<ulong, int> _failedSpawnSceneHandles = new Dictionary<ulong, int>();
  private readonly HashSet<ulong> _roster = new HashSet<ulong>();
  private readonly Dictionary<ulong, string> _playerNames = new Dictionary<ulong, string>();
  private MultiplayerSession? _session;
  private PluginConfig? _config;
  private int _sceneHandle = -1;

  internal void Initialize(MultiplayerSession session, PluginConfig config)
  {
    _session = session;
    _config = config;
    session.RosterChanged += OnRosterChanged;
    session.PlayerPoseReceived += OnPlayerPoseReceived;
    session.SceneReadinessChanged += OnSceneReadinessChanged;
    SceneManager.sceneLoaded += OnSceneLoaded;
    SceneManager.sceneUnloaded += OnSceneUnloaded;
    ReconcileRemotePlayers();
  }

  private void OnDestroy()
  {
    SceneManager.sceneLoaded -= OnSceneLoaded;
    SceneManager.sceneUnloaded -= OnSceneUnloaded;
    if (_session != null)
    {
      _session.RosterChanged -= OnRosterChanged;
      _session.PlayerPoseReceived -= OnPlayerPoseReceived;
      _session.SceneReadinessChanged -= OnSceneReadinessChanged;
    }

    DestroyAllRepresentations("ManagerDestroyed");
  }

  private void OnRosterChanged(PlayerIdentityData[] players)
  {
    _roster.Clear();
    _playerNames.Clear();
    _failedSpawnSceneHandles.Clear();
    foreach (var player in players)
    {
      if (player.PeerId != 0 && player.PeerId != _session?.LocalPeerId)
      {
        _roster.Add(player.PeerId);
        _playerNames[player.PeerId] = player.PlayerName;
      }
    }

    var stalePoses = new List<ulong>();
    foreach (var peerId in _latestPoses.Keys)
    {
      if (!_roster.Contains(peerId)) stalePoses.Add(peerId);
    }

    foreach (var peerId in stalePoses) _latestPoses.Remove(peerId);
    Plugin.Log.LogInfo($"RemotePlayerLifecycle event=RosterChanged peers={_roster.Count} scene={ActiveSceneName()}");
    ReconcileRemotePlayers();
  }

  private void OnPlayerPoseReceived(ulong peerId, PlayerPoseData pose)
  {
    if (peerId == 0 || peerId == _session?.LocalPeerId) return;

    // Keep a pose that beats its roster notification. Reconciliation only materializes
    // peers in the authoritative roster and only poses for the ready local scene.
    _latestPoses[peerId] = pose;
    ReconcileRemotePlayers();
  }

  private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
  {
    if (mode == LoadSceneMode.Additive && SceneManager.GetActiveScene().handle != scene.handle) return;
    _sceneHandle = scene.handle;
    _failedSpawnSceneHandles.Clear();
    DestroyAllRepresentations("SceneLoaded");
    DiscardPosesForOtherScenes(scene.name);
    Plugin.Log.LogInfo($"RemotePlayerLifecycle event=SceneLoaded scene={scene.name} handle={scene.handle} roster={_roster.Count}");
    ReconcileRemotePlayers();
  }

  private void OnSceneUnloaded(Scene scene)
  {
    var stale = new List<ulong>();
    foreach (var pair in _avatars)
    {
      var avatar = pair.Value;
      if (avatar == null || avatar.gameObject == null || avatar.gameObject.scene.handle == scene.handle)
      {
        if (avatar != null && avatar.gameObject != null) Destroy(avatar.gameObject);
        Plugin.Log.LogInfo($"RemotePlayerLifecycle peer={pair.Key} action=RepresentationDestroyed reason=SceneUnloaded scene={scene.name}");
        stale.Add(pair.Key);
      }
    }

    foreach (var peerId in stale) _avatars.Remove(peerId);
    if (_sceneHandle == scene.handle) _sceneHandle = -1;
    ReconcileRemotePlayers();
  }

  private void OnSceneReadinessChanged()
  {
    _failedSpawnSceneHandles.Clear();
    ReconcileRemotePlayers();
  }

  private void ReconcileRemotePlayers()
  {
    if (_session == null) return;
    var scene = SceneManager.GetActiveScene();
    if (!scene.IsValid() || !scene.isLoaded)
    {
      DestroyAllRepresentations("NoLoadedScene");
      return;
    }

    if (_sceneHandle != scene.handle)
    {
      _sceneHandle = scene.handle;
      DiscardPosesForOtherScenes(scene.name);
    }

    var sceneReady = _session.CanMaterializeRemotePlayers(scene.name);
    var stalePeers = new List<ulong>();
    foreach (var pair in _avatars)
    {
      var avatar = pair.Value;
      if (!_roster.Contains(pair.Key) || avatar == null || avatar.gameObject == null
          || avatar.gameObject.scene.handle != scene.handle || !sceneReady
          || !_latestPoses.TryGetValue(pair.Key, out var pose)
          || !string.Equals(pose.SceneName, scene.name, StringComparison.Ordinal))
      {
        DestroyRepresentation(pair.Key, "ReconcileStale");
        stalePeers.Add(pair.Key);
      }
    }

    foreach (var peerId in stalePeers) _avatars.Remove(peerId);
    if (!sceneReady) return;

    foreach (var peerId in _roster)
    {
      if (!_latestPoses.TryGetValue(peerId, out var pose)
          || !string.Equals(pose.SceneName, scene.name, StringComparison.Ordinal)) continue;

      if (_failedSpawnSceneHandles.TryGetValue(peerId, out var failedSceneHandle)
          && failedSceneHandle == scene.handle) continue;

      if (_avatars.TryGetValue(peerId, out var current) && current != null && current.gameObject != null)
      {
        current.SetPose(pose);
        continue;
      }

      if (_config == null)
      {
        _failedSpawnSceneHandles[peerId] = scene.handle;
        Plugin.Log.LogError($"RemotePlayerLifecycle peer={peerId} action=SpawnFailed scene={scene.name} reason=PluginConfigUnavailable");
        continue;
      }

      var displayName = _playerNames.TryGetValue(peerId, out var knownName) ? knownName : $"Player {peerId}";
      if (!RemotePlayerVisualPreview.TryCreateAt(new Vector3(pose.X, pose.Y, pose.Z), pose.Yaw,
          $"{displayName} ({peerId})", _config, out var root, out var error))
      {
        _failedSpawnSceneHandles[peerId] = scene.handle;
        Plugin.Log.LogWarning($"RemotePlayerLifecycle peer={peerId} action=SpawnFailed scene={scene.name} reason={error}");
        continue;
      }

      var avatar = root!.GetComponent<RemotePlayerAvatar>();
      if (avatar == null)
      {
        Destroy(root);
        _failedSpawnSceneHandles[peerId] = scene.handle;
        Plugin.Log.LogError($"RemotePlayerLifecycle peer={peerId} action=SpawnFailed scene={scene.name} reason=MissingAvatarComponent");
        continue;
      }

      _avatars[peerId] = avatar;
      _failedSpawnSceneHandles.Remove(peerId);
      avatar.SetPose(pose);
      Plugin.Log.LogInfo($"RemotePlayerLifecycle peer={peerId} name='{displayName}' action=Spawn reason=Reconcile scene={scene.name} sceneHandle={scene.handle}");
    }
  }

  private void DiscardPosesForOtherScenes(string sceneName)
  {
    var stale = new List<ulong>();
    foreach (var pair in _latestPoses)
    {
      if (!string.Equals(pair.Value.SceneName, sceneName, StringComparison.Ordinal)) stale.Add(pair.Key);
    }

    foreach (var peerId in stale) _latestPoses.Remove(peerId);
  }

  private void DestroyAllRepresentations(string reason)
  {
    foreach (var peerId in new List<ulong>(_avatars.Keys)) DestroyRepresentation(peerId, reason);
    _avatars.Clear();
  }

  private void DestroyRepresentation(ulong peerId, string reason)
  {
    if (!_avatars.TryGetValue(peerId, out var avatar)) return;
    if (avatar != null && avatar.gameObject != null) Destroy(avatar.gameObject);
    Plugin.Log.LogInfo($"RemotePlayerLifecycle peer={peerId} action=RepresentationDestroyed reason={reason}");
  }

  private static string ActiveSceneName()
  {
    var scene = SceneManager.GetActiveScene();
    return scene.IsValid() ? scene.name : "<none>";
  }
}
