using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
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

  internal bool TryGetDoor(StableObjectKey key, out Door? door) => _registry.TryGet(key, out door);
  internal bool TryGetDoor(Guid key, out Door? door) => _registry.TryGet(key, out door);

  private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => ScanLoadedScenes();
  private void OnSceneUnloaded(Scene scene) => ScanLoadedScenes();

  private void ScanLoadedScenes()
  {
    _doors.Clear();
    _registry = new StableObjectRegistry<Door>();

    var loadedSceneCount = 0;
    var sceneDoorKeys = new Dictionary<string, SceneDoorKeys>(StringComparer.Ordinal);
    foreach (var door in Resources.FindObjectsOfTypeAll<Door>())
    {
      if (door == null || !door.gameObject.scene.IsValid() || !door.gameObject.scene.isLoaded)
      {
        continue;
      }

      var scene = door.gameObject.scene;
      var sceneIdentity = string.Concat(
        scene.buildIndex.ToString(CultureInfo.InvariantCulture), ":", scene.name, ":", scene.path);
      if (!StableObjectKey.TryCreate(GetBuildIdentity(), sceneIdentity, GetHierarchyPath(door),
          door.GetType().FullName ?? nameof(Door), out var key, out var keyError))
      {
        Plugin.Log.LogWarning($"Could not identify door in scene '{scene.name}': {keyError}");
        continue;
      }

      if (!_registry.TryRegister(key, door, out var registrationError))
      {
        Plugin.Log.LogError($"Door identity rejected in scene '{scene.name}' at '{GetHierarchyPath(door)}': {registrationError}");
        continue;
      }

      _doors.Add(new DoorIdentity(door, key));
      if (!sceneDoorKeys.TryGetValue(sceneIdentity, out var sceneKeys))
      {
        sceneKeys = new SceneDoorKeys(scene.name, scene.path);
        sceneDoorKeys.Add(sceneIdentity, sceneKeys);
      }

      sceneKeys.Keys.Add(key.ToString());
    }

    for (var i = 0; i < SceneManager.sceneCount; i++)
    {
      if (SceneManager.GetSceneAt(i).isLoaded) loadedSceneCount++;
    }

    Plugin.Log.LogInfo($"Static world identity scan covered {loadedSceneCount} loaded scene(s) and registered {_doors.Count} door(s); ambiguous identities were rejected.");
    foreach (var scene in sceneDoorKeys.Values)
    {
      scene.Keys.Sort(StringComparer.Ordinal);
      var canonicalKeys = string.Join("\n", scene.Keys.ToArray());
      using (var sha256 = SHA256.Create())
      {
        var fingerprint = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(canonicalKeys))).Replace("-", string.Empty);
        Plugin.Log.LogInfo($"Static door identity fingerprint scene='{scene.Name}' path='{scene.Path}' count={scene.Keys.Count} sha256={fingerprint}.");
      }
    }
  }

  private static string GetBuildIdentity()
  {
    var version = string.IsNullOrWhiteSpace(Application.version) ? Application.unityVersion : Application.version;
    return string.Concat(Application.productName, "/", version);
  }

  private static string GetHierarchyPath(Door target)
  {
    var names = new List<string>();
    for (var current = target.transform; current != null; current = current.parent)
    {
      names.Add(string.Concat(current.GetSiblingIndex().ToString(CultureInfo.InvariantCulture), "#", current.name));
    }

    names.Reverse();
    var path = new StringBuilder();
    foreach (var name in names)
    {
      // Length-prefix each segment so a slash in an object name cannot alias a child boundary.
      path.Append(name.Length).Append(':').Append(name);
    }

    var componentOrdinal = 0;
    foreach (var component in target.GetComponents<Door>())
    {
      if (ReferenceEquals(component, target))
      {
        break;
      }

      componentOrdinal++;
    }

    path.Append("component:").Append(componentOrdinal.ToString(CultureInfo.InvariantCulture));

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

  private sealed class SceneDoorKeys
  {
    internal SceneDoorKeys(string name, string path)
    {
      Name = name;
      Path = path;
    }

    internal string Name { get; }
    internal string Path { get; }
    internal List<string> Keys { get; } = new List<string>();
  }
}
