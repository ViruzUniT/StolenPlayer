using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StolenPlayer.World;

/// <summary>Builds and collision-checks identities for Door components in loaded scenes.</summary>
internal sealed class StaticWorldIdentityScanner : MonoBehaviour
{
  private readonly List<DoorIdentity> _doors = new List<DoorIdentity>();
  private StableObjectRegistry<Door> _registry = new StableObjectRegistry<Door>();

  private void OnEnable()
  {
    SceneManager.sceneLoaded += OnSceneLoaded;
    SceneManager.sceneUnloaded += OnSceneUnloaded;
    ScanLoadedScenes();
  }

  private void OnDisable()
  {
    SceneManager.sceneLoaded -= OnSceneLoaded;
    SceneManager.sceneUnloaded -= OnSceneUnloaded;
    _doors.Clear();
    _registry = new StableObjectRegistry<Door>();
  }

  internal bool TryGetKey(Door door, out StableObjectKey key)
  {
    foreach (var entry in _doors)
    {
      if (ReferenceEquals(entry.Door, door))
      {
        key = entry.Key;
        return true;
      }
    }

    key = default;
    return false;
  }

  private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => ScanLoadedScenes();
  private void OnSceneUnloaded(Scene scene) => ScanLoadedScenes();

  private void ScanLoadedScenes()
  {
    _doors.Clear();
    _registry = new StableObjectRegistry<Door>();

    var loadedSceneCount = 0;
    foreach (var door in Resources.FindObjectsOfTypeAll<Door>())
    {
      if (door == null || !door.gameObject.scene.IsValid() || !door.gameObject.scene.isLoaded)
      {
        continue;
      }

      var scene = door.gameObject.scene;
      var sceneIdentity = string.Concat(
        scene.buildIndex.ToString(CultureInfo.InvariantCulture), ":", scene.name, ":", scene.path);
      if (!StableObjectKey.TryCreate(GetBuildIdentity(), sceneIdentity, GetHierarchyPath(door.transform),
          door.GetType().FullName ?? nameof(Door), out var key, out var keyError))
      {
        Plugin.Log.LogWarning($"Could not identify door in scene '{scene.name}': {keyError}");
        continue;
      }

      if (!_registry.TryRegister(key, door, out var registrationError))
      {
        Plugin.Log.LogError($"Door identity rejected in scene '{scene.name}' at '{GetHierarchyPath(door.transform)}': {registrationError}");
        continue;
      }

      _doors.Add(new DoorIdentity(door, key));
    }

    for (var i = 0; i < SceneManager.sceneCount; i++)
    {
      if (SceneManager.GetSceneAt(i).isLoaded) loadedSceneCount++;
    }

    Plugin.Log.LogInfo($"Static world identity scan covered {loadedSceneCount} loaded scene(s) and registered {_doors.Count} door(s); ambiguous identities were rejected.");
  }

  private static string GetBuildIdentity()
  {
    var version = string.IsNullOrWhiteSpace(Application.version) ? Application.unityVersion : Application.version;
    return string.Concat(Application.productName, "/", version);
  }

  private static string GetHierarchyPath(Transform target)
  {
    var names = new List<string>();
    for (var current = target; current != null; current = current.parent)
    {
      names.Add(current.name);
    }

    names.Reverse();
    var path = new StringBuilder();
    foreach (var name in names)
    {
      // Length-prefix each segment so a slash in an object name cannot alias a child boundary.
      path.Append(name.Length).Append(':').Append(name);
    }

    return path.ToString();
  }

  private sealed class DoorIdentity
  {
    internal DoorIdentity(Door door, StableObjectKey key)
    {
      Door = door;
      Key = key;
    }

    internal Door Door { get; }
    internal StableObjectKey Key { get; }
  }
}
