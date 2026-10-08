using StolenPlayer.Protocol;
using UnityEngine;

namespace StolenPlayer;

/// <summary>Visual-only, interpolated representation of one remote peer.</summary>
internal sealed class RemotePlayerAvatar : MonoBehaviour
{
  private const float InterpolationSeconds = 0.12f;
  private Animation? _animation;
  private string _idleClipName = "Bored";
  private string _walkAnimationName = "Walk";
  private string _runAnimationName = "Running";
  private string? _activeClip;
  private Vector3 _fromPosition;
  private Vector3 _targetPosition;
  private Quaternion _fromRotation;
  private Quaternion _targetRotation;
  private float _targetTime;
  private bool _hasTarget;

  internal void Initialize(Animation animation, PluginConfig config)
  {
    _animation = animation;
    _walkAnimationName = config.WalkAnimationName.Value.Trim();
    _runAnimationName = config.RunAnimationName.Value.Trim();
    if (!RemotePlayerVisualPreview.TryGetIdleClip(animation, config.IdleAnimationNames.Value,
        out _idleClipName, out var availableClips))
    {
      Plugin.Log.LogWarning($"Remote player animation has no recognized idle state. Available states: {availableClips}. Using '{_idleClipName}'.");
      return;
    }

    _activeClip = null;
    PlayClip(_idleClipName, 0);
    Plugin.Log.LogInfo($"Remote player is playing idle clip '{_idleClipName}' normally. Available states: {availableClips}.");
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
      : (pose.StateFlags & PlayerPoseData.Running) != 0 ? _runAnimationName : _walkAnimationName;
    if (_animation == null)
    {
      return;
    }

    if (clipName == _idleClipName)
    {
      if (_activeClip != clipName) PlayClip(clipName, 0.15f);
      return;
    }

    if (_activeClip != clipName) PlayClip(clipName, 0.15f);
  }

  private void PlayClip(string clipName, float fadeSeconds)
  {
    if (_animation == null) return;
    var state = _animation[clipName];
    if (state == null || _animation.GetClip(clipName) == null) return;

    state.wrapMode = WrapMode.Loop;
    _animation.enabled = true;
    if (fadeSeconds <= 0) _animation.Play(clipName);
    else _animation.CrossFade(clipName, fadeSeconds);
    _activeClip = clipName;
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
