using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using StolenPlayer.Protocol;
using StolenPlayer.Sessions;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StolenPlayer.World;

/// <summary>Host-authoritative adapter for ordinary unlocked pedestrian door toggles.</summary>
internal sealed class DoorInteractionReplicator : MonoBehaviour
{
  private const float InteractionRange = 2.75f;
  private const float DuplicateRequestSeconds = 3f;
  private static readonly FieldInfo? OpenSpeedField = typeof(Door).GetField("openSpeed", BindingFlags.Instance | BindingFlags.NonPublic);
  private readonly Dictionary<Guid, float> _lastRequestedAt = new Dictionary<Guid, float>();
  private MultiplayerSession? _session;
  private StaticWorldIdentityScanner? _identities;

  internal static DoorInteractionReplicator? Current { get; private set; }

  internal void Initialize(MultiplayerSession session, StaticWorldIdentityScanner identities)
  {
    _session = session;
    _identities = identities;
    Current = this;
    session.DoorIntentReceived += OnDoorIntent;
    session.DoorStateReceived += OnDoorState;
    if (OpenSpeedField == null)
      Plugin.Log.LogError("Could not find Door.openSpeed; replicated door animations will be disabled.");
  }

  private void OnDestroy()
  {
    if (_session != null)
    {
      _session.DoorIntentReceived -= OnDoorIntent;
      _session.DoorStateReceived -= OnDoorState;
    }
    if (Current == this) Current = null;
    _lastRequestedAt.Clear();
  }

  internal bool BeforeDoorUpdate(Door door)
  {
    if (_session == null || _session.IsHost || _session.State != SessionState.Connected
        || !door.load_slow || door.openingSlowly
        || door.useTim + Time.deltaTime < 0.15f) return true;

    door.load_slow = false;
    door.useTim = 0f;
    if (!_session.IsClientWorldReady)
    {
      Plugin.Log.LogWarning("Blocked a client door interaction until initial world synchronization is complete.");
      return true;
    }
    if (!IsOrdinaryUnlockedDoor(door))
    {
      Plugin.Log.LogWarning($"Blocked unsupported client door interaction at '{door.name}' (special, locked, or unavailable door).");
      return true;
    }

    TryRequestToggle(door);
    return true;
  }

  internal bool BeforeDoorUse(Door door)
  {
    if (_session == null || _session.IsHost || _session.State != SessionState.Connected) return true;
    if (!_session.IsClientWorldReady)
    {
      Plugin.Log.LogWarning("Blocked Door.Use on a client before initial world synchronization completed.");
      return false;
    }
    if (IsOrdinaryUnlockedDoor(door)) TryRequestToggle(door);
    else Plugin.Log.LogWarning($"Blocked unsupported client Door.Use at '{door.name}'.");
    return false;
  }

  internal void AfterDoorStateTick(Door door, bool previousIsOpen)
  {
    if (_session == null || !_session.IsHost || previousIsOpen == door.isOpen
        || !IsStandardDoorKind(door) || _identities == null
        || !_identities.TryGetKey(door, out var key)) return;
    _session.BroadcastDoorState(key.Value, SceneManager.GetActiveScene().name, door.isOpen);
  }

  private void TryRequestToggle(Door door)
  {
    if (_session == null || _identities == null || !IsOrdinaryUnlockedDoor(door)
        || !_identities.TryGetKey(door, out var key))
    {
      Plugin.Log.LogWarning($"Could not submit door intent for '{door.name}': no safe static identity or door is not eligible.");
      return;
    }

    var now = Time.unscaledTime;
    if (_lastRequestedAt.TryGetValue(key.Value, out var last) && now - last < DuplicateRequestSeconds) return;
    if (_session.RequestDoorToggle(key.Value, SceneManager.GetActiveScene().name))
    {
      _lastRequestedAt[key.Value] = now;
      Plugin.Log.LogInfo($"Submitted host-authoritative door toggle request for {key.Value:N}.");
    }
    else
    {
      Plugin.Log.LogWarning($"Door toggle request for {key.Value:N} was not sent because the multiplayer world is not ready.");
    }
  }

  private void OnDoorIntent(long connectionId, Guid key, string sceneName)
  {
    if (_session == null || _identities == null || !_session.IsHost
        || !string.Equals(SceneManager.GetActiveScene().name, sceneName, StringComparison.Ordinal)
        || !_identities.TryGetDoor(key, out var door)
        || door == null || !IsOrdinaryUnlockedDoor(door))
    {
      Plugin.Log.LogWarning($"Rejected door intent {key:N} from connection {connectionId}: target is unavailable or not eligible.");
      return;
    }

    var pose = _session.TryGetPeerPose(connectionId, out var remotePose) ? remotePose : (PlayerPoseData?)null;
    if (!pose.HasValue)
    {
      Plugin.Log.LogWarning($"Rejected door intent {key:N} from connection {connectionId}: no recent authoritative player pose.");
      return;
    }

    var playerPosition = new Vector3(pose.Value.X, pose.Value.Y, pose.Value.Z) + Vector3.up * 1.0f;
    var collider = door.GetComponent<Collider>();
    var doorPosition = collider != null ? collider.bounds.center : door.transform.position;
    if (Vector3.Distance(playerPosition, doorPosition) > InteractionRange)
    {
      Plugin.Log.LogWarning($"Rejected out-of-range door intent {key:N} from peer {connectionId}.");
      return;
    }

    if (!ApplyState(door, !door.isOpen))
    {
      Plugin.Log.LogError($"Host could not apply authoritative door state for {key:N}; no state was broadcast.");
      return;
    }
    _session.BroadcastDoorState(key, sceneName, door.isOpen);
    Plugin.Log.LogInfo($"Host applied door toggle {key:N}; open={door.isOpen}.");
  }

  private void OnDoorState(Guid key, string sceneName, bool isOpen)
  {
    if (_session == null || _session.IsHost || _identities == null
        || !string.Equals(SceneManager.GetActiveScene().name, sceneName, StringComparison.Ordinal)
        || !_identities.TryGetDoor(key, out var door) || door == null
        || !IsStandardDoorKind(door))
    {
      Plugin.Log.LogWarning($"Could not apply replicated door state {key:N} in scene '{sceneName}'.");
      return;
    }

    if (door.isOpen != isOpen && !ApplyState(door, isOpen))
      Plugin.Log.LogError($"Client could not apply replicated door state for {key:N}.");
  }

  private static bool ApplyState(Door door, bool isOpen)
  {
    if (OpenSpeedField == null) return false;
    try
    {
      door.Resett();
      door.load_slow = false;
      door.useTim = 0f;
      door.openingSlowly = false;
      door.isOpen = isOpen;
      door.openedByPlayer = isOpen;
      door.canUse = false;
      door.opening = true;
      door.npcUsing = false;
      door.DoorSound?.Stop();
      OpenSpeedField.SetValue(door, 150f * door.speedMultiplier);
      return true;
    }
    catch (Exception exception)
    {
      Plugin.Log.LogError($"Failed to apply authoritative door state on '{door.name}': {exception}");
      return false;
    }
  }

  private static bool IsStandardDoorKind(Door door)
  {
    return door != null && door.gameObject.scene.IsValid() && door.gameObject.scene.isLoaded
      && string.Equals(door.gameObject.tag, "Door", StringComparison.Ordinal)
      && !door.brokeHinge
      && !door.requires_key && !door.only_with_key && !door.was_locked
      && !door.isWindow && !door.isCarDoor && !door.isDrawer && !door.isGate
      && !door.isCupboard && !door.isFridge && !door.isFileCabinet
      && !door.isGlassCabinet && !door.isContainer;
  }

  private static bool IsOrdinaryUnlockedDoor(Door door)
  {
    return IsStandardDoorKind(door) && door.canUse && !door.npcUsing && !door.opening;
  }
}

[HarmonyPatch(typeof(Door), "Update")]
internal static class DoorUpdateReplicationPatch
{
  private static bool Prefix(Door __instance, out DoorUpdatePatchState __state)
  {
    __state = new DoorUpdatePatchState(__instance.isOpen, __instance.load_slow);
    return DoorInteractionReplicator.Current?.BeforeDoorUpdate(__instance) ?? true;
  }

  private static void Postfix(Door __instance, DoorUpdatePatchState __state)
  {
    if (__state.LoadSlow && __state.IsOpen != __instance.isOpen)
      DoorInteractionReplicator.Current?.AfterDoorStateTick(__instance, __state.IsOpen);
  }
}

internal readonly struct DoorUpdatePatchState
{
  internal DoorUpdatePatchState(bool isOpen, bool loadSlow)
  {
    IsOpen = isOpen;
    LoadSlow = loadSlow;
  }

  internal bool IsOpen { get; }
  internal bool LoadSlow { get; }
}

[HarmonyPatch(typeof(Door), nameof(Door.Use))]
internal static class DoorUseReplicationPatch
{
  private static bool Prefix(Door __instance, out bool __state)
  {
    __state = __instance.isOpen;
    return DoorInteractionReplicator.Current?.BeforeDoorUse(__instance) ?? true;
  }

  private static void Postfix(Door __instance, bool __state)
  {
    DoorInteractionReplicator.Current?.AfterDoorStateTick(__instance, __state);
  }
}
