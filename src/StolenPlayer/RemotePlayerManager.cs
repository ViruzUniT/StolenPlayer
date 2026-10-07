using System.Collections.Generic;
using StolenPlayer.Protocol;
using StolenPlayer.Sessions;
using UnityEngine;

namespace StolenPlayer;

internal sealed class RemotePlayerManager : MonoBehaviour
{
  private readonly Dictionary<ulong, RemotePlayerAvatar> _avatars = new Dictionary<ulong, RemotePlayerAvatar>();
  private readonly Dictionary<ulong, float> _nextSpawnAttempt = new Dictionary<ulong, float>();
  private MultiplayerSession? _session;

  internal void Initialize(MultiplayerSession session)
  {
    _session = session;
    session.RosterChanged += OnRosterChanged;
    session.PlayerPoseReceived += OnPlayerPoseReceived;
  }

  private void OnDestroy()
  {
    if (_session != null)
    {
      _session.RosterChanged -= OnRosterChanged;
      _session.PlayerPoseReceived -= OnPlayerPoseReceived;
    }

    ClearAvatars();
  }

  private void OnRosterChanged(ulong[] peerIds)
  {
    var keep = new HashSet<ulong>(peerIds);
    var remove = new List<ulong>();
    foreach (var pair in _avatars)
    {
      if (!keep.Contains(pair.Key) || pair.Key == _session?.LocalPeerId || pair.Value == null)
      {
        if (pair.Value != null) Destroy(pair.Value.gameObject);
        remove.Add(pair.Key);
      }
    }

    foreach (var peerId in remove) _avatars.Remove(peerId);
    _nextSpawnAttempt.Clear();
  }

  private void OnPlayerPoseReceived(ulong peerId, PlayerPoseData pose)
  {
    if (peerId == 0 || peerId == _session?.LocalPeerId)
    {
      return;
    }

    if (!_avatars.TryGetValue(peerId, out var avatar) || avatar == null)
    {
      if (_nextSpawnAttempt.TryGetValue(peerId, out var retryAt) && Time.unscaledTime < retryAt)
      {
        return;
      }

      _nextSpawnAttempt[peerId] = Time.unscaledTime + 3.0f;
      if (!RemotePlayerVisualPreview.TryCreateAt(new Vector3(pose.X, pose.Y, pose.Z), pose.Yaw,
          $"StolenPlayer Remote Player {peerId}", out var root, out var error))
      {
        Plugin.Log.LogWarning($"Could not create remote avatar for peer {peerId}: {error}");
        return;
      }

      avatar = root!.GetComponent<RemotePlayerAvatar>();
      if (avatar == null)
      {
        Destroy(root);
        Plugin.Log.LogError($"Remote avatar for peer {peerId} was created without its interpolation component.");
        return;
      }

      _avatars[peerId] = avatar;
      _nextSpawnAttempt.Remove(peerId);
      Plugin.Log.LogInfo($"Created visual-only remote avatar for peer {peerId}.");
    }

    avatar.SetPose(pose);
  }

  private void ClearAvatars()
  {
    foreach (var avatar in _avatars.Values)
    {
      if (avatar != null) Destroy(avatar.gameObject);
    }

    _avatars.Clear();
    _nextSpawnAttempt.Clear();
  }
}
