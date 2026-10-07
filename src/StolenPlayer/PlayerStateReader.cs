using System;
using System.Reflection;
using HarmonyLib;
using StolenPlayer.Protocol;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StolenPlayer;

/// <summary>Reads only local-player pose fields confirmed in the installed TS2 assemblies.</summary>
internal sealed class PlayerStateReader
{
  private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
  private readonly Type? _playerScriptType = AccessTools.TypeByName("PlayerScript");
  private Component? _player;
  private FieldInfo? _controllerField;
  private FieldInfo? _motorField;
  private FieldInfo? _movingField;
  private FieldInfo? _runningField;
  private FieldInfo? _crouchingField;
  private float _nextLookupTime;

  internal bool TryRead(out PlayerPoseData pose)
  {
    pose = default;
    var sceneName = SceneManager.GetActiveScene().name;
    if (string.IsNullOrEmpty(sceneName))
    {
      return false;
    }

    if (_player != null && (!_player.gameObject.scene.IsValid() || !_player.gameObject.scene.isLoaded
        || !_player.gameObject.activeInHierarchy || !string.Equals(_player.gameObject.scene.name, sceneName, StringComparison.Ordinal)))
    {
      InvalidateCache();
    }

    if (!TryGetPlayer())
    {
      return false;
    }

    var playerTransform = _player!.transform;
    var position = playerTransform.position;
    var yaw = playerTransform.eulerAngles.y;
    var flags = (byte)0;
    try
    {
      var motor = _motorField?.GetValue(_player);
      if (motor != null)
      {
        if (ReadBool(_movingField, motor)) flags |= PlayerPoseData.Moving;
        if (ReadBool(_runningField, motor)) flags |= PlayerPoseData.Running;
        if (ReadBool(_crouchingField, motor)) flags |= PlayerPoseData.Crouching;
      }

      var controller = _controllerField?.GetValue(_player) as CharacterController;
      if (controller != null)
      {
        position = controller.transform.position;
        // The controller transform can sit at capsule center in TS2; report the collider's foot point.
        position.y = controller.bounds.min.y;
        yaw = controller.transform.eulerAngles.y;
      }
    }
    catch (Exception exception)
    {
      Plugin.Log.LogWarning($"Could not read local player movement state: {exception.Message}");
      InvalidateCache();
      return false;
    }

    pose = new PlayerPoseData(position.x, position.y, position.z, yaw, flags, sceneName);
    return true;
  }

  internal bool TryTeleportToFeet(Vector3 feetPosition, float yaw, string expectedScene)
  {
    var activeScene = SceneManager.GetActiveScene();
    if (!activeScene.IsValid() || !activeScene.isLoaded || !string.Equals(activeScene.name, expectedScene, StringComparison.Ordinal))
      return false;

    if (!TryGetPlayer()) return false;
    try
    {
      var controller = _controllerField?.GetValue(_player) as CharacterController;
      if (controller == null) return false;

      var target = controller.transform.position;
      var feetOffset = target.y - controller.bounds.min.y;
      target.x = feetPosition.x;
      target.y = feetPosition.y + feetOffset;
      target.z = feetPosition.z;
      var wasEnabled = controller.enabled;
      controller.enabled = false;
      controller.transform.SetPositionAndRotation(target, Quaternion.Euler(0, yaw, 0));
      controller.enabled = wasEnabled;
      Plugin.Log.LogInfo($"Moved joining player to host-authoritative spawn near ({feetPosition.x:F1}, {feetPosition.y:F1}, {feetPosition.z:F1}).");
      return true;
    }
    catch (Exception exception)
    {
      Plugin.Log.LogError($"Could not apply the host spawn position: {exception}");
      InvalidateCache();
      return false;
    }
  }

  private bool TryGetPlayer()
  {
    var activeScene = SceneManager.GetActiveScene();
    if (_player != null)
    {
      if (_player.gameObject.scene.IsValid() && _player.gameObject.scene.isLoaded && _player.gameObject.activeInHierarchy
          && _player.gameObject.scene.handle == activeScene.handle)
      {
        return true;
      }

      InvalidateCache();
    }

    if (_playerScriptType == null || Time.realtimeSinceStartup < _nextLookupTime)
    {
      return false;
    }

    _nextLookupTime = Time.realtimeSinceStartup + 2.0f;
    try
    {
      foreach (var candidate in Resources.FindObjectsOfTypeAll(_playerScriptType))
      {
        var component = candidate as Component;
        if (component == null || !component.gameObject.scene.IsValid() || !component.gameObject.scene.isLoaded
            || component.gameObject.scene.handle != activeScene.handle
            || !component.gameObject.activeInHierarchy)
        {
          continue;
        }

        var controllerField = _playerScriptType.GetField("CC", InstanceFields);
        var motorField = _playerScriptType.GetField("PMB", InstanceFields);
        if (controllerField == null || motorField == null)
        {
          Plugin.Log.LogError("PlayerScript no longer exposes the inspected CC and PMB fields; local pose replication is disabled.");
          return false;
        }

        var motor = motorField.GetValue(component);
        if (motor == null)
        {
          continue;
        }

        _player = component;
        _controllerField = controllerField;
        _motorField = motorField;
        var motorType = motor.GetType();
        _movingField = motorType.GetField("isMoving", InstanceFields);
        _runningField = motorType.GetField("isRunning", InstanceFields);
        _crouchingField = motorType.GetField("isCrouching", InstanceFields);
        if (_movingField == null || _runningField == null || _crouchingField == null)
        {
          Plugin.Log.LogWarning("PlayerMotorBehavior movement fields changed; position will replicate without movement animation flags.");
        }

        Plugin.Log.LogInfo($"Local player pose source found in scene '{component.gameObject.scene.name}'.");
        return true;
      }
    }
    catch (Exception exception)
    {
      Plugin.Log.LogError($"Could not locate the local PlayerScript: {exception}");
    }

    return false;
  }

  private static bool ReadBool(FieldInfo? field, object instance)
  {
    return field != null && field.FieldType == typeof(bool) && field.GetValue(instance) is bool value && value;
  }

  private void InvalidateCache()
  {
    _player = null;
    _controllerField = null;
    _motorField = null;
    _movingField = null;
    _runningField = null;
    _crouchingField = null;
    _nextLookupTime = 0;
  }
}
