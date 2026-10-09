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
  // Door.Update and Door.SlowFailed can both report the same single interaction.
  // Suppress only that near-simultaneous duplicate; a multi-second lockout makes
  // legitimate consecutive toggles feel unresponsive after the door is usable.
  private const float DuplicateRequestSeconds = 0.25f;
  private static readonly FieldInfo? OpenSpeedField = typeof(Door).GetField("openSpeed", BindingFlags.Instance | BindingFlags.NonPublic);
  private static readonly FieldInfo? SlowOpenSpeedMultiplierField = typeof(Door).GetField("slowOpenSpeedMulti", BindingFlags.Instance | BindingFlags.Public);
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
    session.PeerSceneReady += OnPeerSceneReady;
    if (OpenSpeedField == null)
      Plugin.Log.LogError("Could not find Door.openSpeed; replicated door animations will be disabled.");
  }

  private void OnDestroy()
  {
    if (_session != null)
    {
      _session.DoorIntentReceived -= OnDoorIntent;
      _session.DoorStateReceived -= OnDoorState;
      _session.PeerSceneReady -= OnPeerSceneReady;
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

    TryRequestToggle(door, isSlow: true);
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
    if (IsOrdinaryUnlockedDoor(door)) TryRequestToggle(door, isSlow: false);
    else Plugin.Log.LogWarning($"Blocked unsupported client Door.Use at '{door.name}'.");
    return false;
  }

  internal void AfterDoorStateTick(Door door, bool previousIsOpen)
  {
    if (_session == null || !_session.IsHost || previousIsOpen == door.isOpen
        || !IsStandardDoorKind(door) || _identities == null
        || !_identities.TryGetKey(door, out var key)) return;
    _session.BroadcastDoorState(key.Value, SceneManager.GetActiveScene().name, door.isOpen, door.openingSlowly);
  }

  private void TryRequestToggle(Door door, bool isSlow)
  {
    if (_session == null || _identities == null || !IsOrdinaryUnlockedDoor(door)
        || !_identities.TryGetKey(door, out var key))
    {
      Plugin.Log.LogWarning($"Could not submit door intent for '{door.name}': no safe static identity or door is not eligible.");
      return;
    }

    var now = Time.unscaledTime;
    if (_lastRequestedAt.TryGetValue(key.Value, out var last) && now - last < DuplicateRequestSeconds) return;
    if (_session.RequestDoorToggle(key.Value, SceneManager.GetActiveScene().name, isSlow))
    {
      _lastRequestedAt[key.Value] = now;
      Plugin.Log.LogInfo($"Submitted host-authoritative door toggle request for {key.Value:N}.");
    }
    else
    {
      Plugin.Log.LogWarning($"Door toggle request for {key.Value:N} was not sent because the multiplayer world is not ready.");
    }
  }

  private void OnDoorIntent(long connectionId, Guid key, string sceneName, bool isSlow)
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

    if (!ApplyState(door, !door.isOpen, isSlow))
    {
      Plugin.Log.LogError($"Host could not apply authoritative door state for {key:N}; no state was broadcast.");
      return;
    }
    _session.BroadcastDoorState(key, sceneName, door.isOpen, isSlow);
    Plugin.Log.LogInfo($"Host applied door toggle {key:N}; open={door.isOpen}.");
  }

  private void OnPeerSceneReady(long connectionId, string sceneName)
  {
    if (_session == null || !_session.IsHost || _identities == null
        || !string.Equals(SceneManager.GetActiveScene().name, sceneName, StringComparison.Ordinal)) return;

    var snapshot = new List<DoorStateSnapshot>();
    for (var index = 0; index < _identities.DoorCount; index++)
    {
      if (!_identities.TryGetDoorAt(index, out var key, out var door) || door == null
          || !IsStandardDoorKind(door)) continue;
      snapshot.Add(new DoorStateSnapshot(key, door.isOpen, door.openingSlowly));
    }

    if (!_session.SendDoorSnapshot(connectionId, sceneName, snapshot.ToArray()))
      Plugin.Log.LogWarning($"Could not send initial door snapshot to connection {connectionId} for '{sceneName}'.");
  }

  private void OnDoorState(Guid key, string sceneName, bool isOpen, bool isSlow)
  {
    if (_session == null || _session.IsHost || _identities == null
        || !string.Equals(SceneManager.GetActiveScene().name, sceneName, StringComparison.Ordinal)
        || !_identities.TryGetDoor(key, out var door) || door == null
        || !IsStandardDoorKind(door))
    {
      Plugin.Log.LogWarning($"Could not apply replicated door state {key:N} in scene '{sceneName}'.");
      return;
    }

    if ((door.isOpen != isOpen || door.openingSlowly != isSlow) && !ApplyState(door, isOpen, isSlow))
      Plugin.Log.LogError($"Client could not apply replicated door state for {key:N}.");
  }

  private static bool ApplyState(Door door, bool isOpen, bool isSlow)
  {
    if (OpenSpeedField == null) return false;
    try
    {
      door.Resett();
      door.load_slow = false;
      door.useTim = 0f;
      door.openingSlowly = isSlow;
      door.isOpen = isOpen;
      door.openedByPlayer = isOpen;
      // TS2's native slow transition needs canUse=true until its 0.8-second
      // handle phase completes; its own Update then disables interaction and
      // raises the animation speed. Normal transitions lock immediately.
      door.canUse = isSlow;
      door.opening = true;
      door.npcUsing = false;
      door.DoorSound?.Stop();
      var speed = isSlow ? 17f * door.speedMultiplier : 150f * door.speedMultiplier;
      if (isSlow && SlowOpenSpeedMultiplierField != null)
        speed *= (float)SlowOpenSpeedMultiplierField.GetValue(door)!;
      OpenSpeedField.SetValue(door, speed);
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
