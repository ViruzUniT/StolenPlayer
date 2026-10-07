using StolenPlayer.Protocol;
using UnityEngine;

namespace StolenPlayer;

/// <summary>Visual-only, interpolated representation of one remote peer.</summary>
internal sealed class RemotePlayerAvatar : MonoBehaviour
{
  private const float InterpolationSeconds = 0.12f;
  private Animation? _animation;
  private string _idleClipName = "Bored";
  private string? _activeClip;
  private Vector3 _fromPosition;
  private Vector3 _targetPosition;
  private Quaternion _fromRotation;
  private Quaternion _targetRotation;
  private float _targetTime;
  private bool _hasTarget;

  internal void Initialize(Animation animation)
  {
    _animation = animation;
    if (!RemotePlayerVisualPreview.TryGetIdleClip(animation, out _idleClipName, out var availableClips))
    {
      Plugin.Log.LogWarning($"Remote player animation has no recognized idle state. Available states: {availableClips}. Using '{_idleClipName}'.");
      return;
    }

    var idleState = animation[_idleClipName];
    if (idleState != null)
    {
      // StandUp gives this NPC a natural upright pose. ClampForever holds its final frame
      // instead of looping the arm-swinging Bored clip or returning to a seated pose.
      idleState.wrapMode = WrapMode.ClampForever;
    }

    _activeClip = _idleClipName;
    Plugin.Log.LogInfo($"Remote player idle animation selected: '{_idleClipName}' (final frame held). Available states: {availableClips}.");
  }

  internal void SetPose(PlayerPoseData pose)
  {
    _fromPosition = transform.position;
    _fromRotation = transform.rotation;
    _targetPosition = new Vector3(pose.X, pose.Y, pose.Z);
    _targetRotation = Quaternion.Euler(0, pose.Yaw, 0);
    _targetTime = Time.unscaledTime;
    if (!_hasTarget)
    {
      transform.SetPositionAndRotation(_targetPosition, _targetRotation);
      _fromPosition = _targetPosition;
      _fromRotation = _targetRotation;
      _hasTarget = true;
    }

    var clipName = (pose.StateFlags & PlayerPoseData.Moving) == 0
      ? _idleClipName
      : (pose.StateFlags & PlayerPoseData.Running) != 0 ? "Running" : "Walk";
    if (_animation != null && _activeClip != clipName && _animation.GetClip(clipName) != null)
    {
      _animation.CrossFade(clipName, 0.15f);
      _activeClip = clipName;
    }
  }

  private void Update()
  {
    if (!_hasTarget)
    {
      return;
    }

    var amount = Mathf.Clamp01((Time.unscaledTime - _targetTime) / InterpolationSeconds);
    transform.SetPositionAndRotation(
      Vector3.Lerp(_fromPosition, _targetPosition, amount),
      Quaternion.Slerp(_fromRotation, _targetRotation, amount));
  }
}
