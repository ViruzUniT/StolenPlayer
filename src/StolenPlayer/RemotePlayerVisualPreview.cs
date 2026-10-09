using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace StolenPlayer;

internal static class RemotePlayerVisualPreview
{
  private const string VerifiedTemplateSuffix = "NPCs/101/NPC";
  private static GameObject? _cachedVisualTemplate;
  internal static bool TryCreate(Camera? camera, PluginConfig config, out GameObject? preview, out string error)
  {
    preview = null;
    error = string.Empty;
    if (camera == null)
    {
      error = "Could not find the local gameplay camera.";
      return false;
    }

    var forward = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up);
    if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
    forward.Normalize();
    var position = camera.transform.position + forward * 2.5f - Vector3.up * 1.25f;
    return TryCreateAt(position, Quaternion.LookRotation(forward, Vector3.up).eulerAngles.y,
      "StolenPlayer Remote Player Visual Preview", config, out preview, out error);
  }

  internal static bool TryCreateAt(Vector3 position, float yaw, string objectName, PluginConfig config,
    out GameObject? avatar, out string error)
  {
    avatar = null;
    error = string.Empty;
    GameObject? clone = null;
    GameObject? staging = null;

    try
    {
      var templates = new List<TemplateCandidate>();
      if (_cachedVisualTemplate != null)
      {
        var cachedRendererCount = CountVisibleMeshes(_cachedVisualTemplate);
        if (cachedRendererCount > 0 && _cachedVisualTemplate.GetComponent<Animation>() != null)
          templates.Add(new TemplateCandidate(_cachedVisualTemplate, cachedRendererCount, "Persistent visual cache", "NPCs/101/NPC"));
        else
          _cachedVisualTemplate = null;
      }

      if (templates.Count == 0)
      {
        foreach (var candidate in Resources.FindObjectsOfTypeAll<GameObject>())
        {
          if (candidate == null || !candidate.scene.IsValid() || !candidate.scene.isLoaded
              || candidate.name != "NPC" || candidate.GetComponent<Animation>() == null)
            continue;
          var path = GetHierarchyPath(candidate.transform);
          if (!path.EndsWith(VerifiedTemplateSuffix, StringComparison.Ordinal)) continue;
          var rendererCount = CountVisibleMeshes(candidate);
          if (rendererCount > 0)
            templates.Add(new TemplateCandidate(candidate, rendererCount, candidate.scene.name, path));
        }

        templates.Sort((left, right) =>
        {
          var rank = right.RendererCount.CompareTo(left.RendererCount);
          if (rank != 0) return rank;
          var scene = string.Compare(left.SceneName, right.SceneName, StringComparison.Ordinal);
          return scene != 0 ? scene : string.Compare(left.Path, right.Path, StringComparison.Ordinal);
        });
      }

      if (templates.Count == 0)
      {
        error = "No scene NPC with the inspected hierarchy, legacy Animation, and enabled skinned meshes is loaded.";
        return false;
      }

      var template = templates[0];
      var templatePath = template.Path;
      if (templates.Count > 1
          && templates[1].RendererCount == template.RendererCount
          && string.Equals(templates[1].SceneName, template.SceneName, StringComparison.Ordinal)
          && string.Equals(GetHierarchyPath(templates[1].GameObject.transform), templatePath, StringComparison.Ordinal))
      {
        error = $"Multiple equally ranked inactive NPC templates were found at '{template.SceneName}:{templatePath}'. Preview was not spawned.";
        return false;
      }

      staging = new GameObject("StolenPlayer Inactive NPC Staging");
      staging.SetActive(false);
      clone = UnityEngine.Object.Instantiate(template.GameObject, staging.transform, true);
      if (clone.activeInHierarchy)
      {
        UnityEngine.Object.Destroy(clone);
        clone = null;
        error = "The selected NPC did not instantiate under the inactive staging parent; preview was aborted before removing gameplay components.";
        return false;
      }

      clone.SetActive(false);
      clone.name = objectName;

      Animation? legacyAnimation = null;
      foreach (var component in clone.GetComponentsInChildren<Component>(true))
      {
        if (component == null || component is Transform)
        {
          continue;
        }

        if (component is Animation animation)
        {
          animation.enabled = false;
          animation.playAutomatically = false;
          if (legacyAnimation == null)
          {
            legacyAnimation = animation;
          }
          else
          {
            UnityEngine.Object.Destroy(component);
          }

          continue;
        }

        if (component is SkinnedMeshRenderer || component is MeshRenderer || component is MeshFilter || component is LODGroup)
        {
          continue;
        }

        if (component is Behaviour behaviour)
        {
          behaviour.enabled = false;
        }

        if (component is Collider collider)
        {
          collider.enabled = false;
        }

        if (component is Rigidbody rigidbody)
        {
          rigidbody.isKinematic = true;
          rigidbody.detectCollisions = false;
        }

        UnityEngine.Object.Destroy(component);
      }

      if (legacyAnimation == null)
      {
        UnityEngine.Object.Destroy(clone);
        clone = null;
        error = "The cloned NPC visual has no legacy Animation component.";
        return false;
      }

      var hasIdleClip = TryGetIdleClip(legacyAnimation, config.IdleAnimationNames.Value,
        out var idleClipName, out var availableClips);
      var idleClip = idleClipName == null ? null : legacyAnimation.GetClip(idleClipName);
      var idleState = idleClipName == null ? null : legacyAnimation[idleClipName];
      if (!hasIdleClip || idleClip == null || idleState == null)
      {
        UnityEngine.Object.Destroy(clone);
        clone = null;
        error = $"The inspected NPC template has no recognized idle animation state. Available states: {availableClips}.";
        return false;
      }

      if (_cachedVisualTemplate == null)
      {
        _cachedVisualTemplate = UnityEngine.Object.Instantiate(clone, (Transform?)null, true);
        _cachedVisualTemplate.name = "StolenPlayer Cached NPC Visual Template";
        _cachedVisualTemplate.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(_cachedVisualTemplate);
        Plugin.Log.LogInfo("Cached the verified NPC visual template persistently for later peer spawns and reconnects.");
      }

      clone.transform.SetParent(null, true);
      UnityEngine.Object.Destroy(staging);
      staging = null;
      clone.transform.position = position;
      clone.transform.rotation = Quaternion.Euler(0, yaw, 0);
      legacyAnimation.cullingType = AnimationCullingType.AlwaysAnimate;
      legacyAnimation.wrapMode = WrapMode.Loop;
      clone.AddComponent<RemotePlayerAvatar>().Initialize(legacyAnimation, config);
      clone.SetActive(true);

      avatar = clone;
      var visibleParts = clone.GetComponentsInChildren<SkinnedMeshRenderer>(true)
        .Where(renderer => renderer != null && renderer.enabled && IsActiveBelowRoot(renderer.transform, clone.transform))
        .Select(renderer => $"{GetHierarchyPath(renderer.transform)} ({renderer.sharedMesh?.name ?? "no mesh"})")
        .ToArray();
      Plugin.Log.LogInfo($"Spawned local visual-only NPC preview from '{template.SceneName}:{templatePath}' using idle '{idleClipName}' with {template.RendererCount} skinned mesh parts: {string.Join("; ", visibleParts)}.");
      return true;
    }
    catch (Exception exception)
    {
      if (clone != null)
      {
        UnityEngine.Object.Destroy(clone);
      }

      error = $"Could not create the NPC visual preview: {exception.Message}";
      Plugin.Log.LogError($"NPC visual preview failed: {exception}");
      return false;
    }
    finally
    {
      if (staging != null)
      {
        UnityEngine.Object.Destroy(staging);
      }
    }
  }

  internal static bool TryGetIdleClip(Animation animation, string configuredCandidates,
    out string clipName, out string availableClips)
  {
    var stateNames = new List<string>();
    var enumerator = animation.GetEnumerator();
    while (enumerator.MoveNext())
    {
      if (enumerator.Current is AnimationState state && !string.IsNullOrEmpty(state.name))
      {
        stateNames.Add(state.name);
      }
    }

    availableClips = stateNames.Count == 0 ? "<none>" : string.Join(", ", stateNames);
    stateNames.Sort(StringComparer.OrdinalIgnoreCase);
    var candidates = (configuredCandidates ?? string.Empty).Split(',');
    foreach (var rawCandidate in candidates)
    {
      var candidate = rawCandidate.Trim();
      if (candidate.Length == 0) continue;
      var matches = new List<string>();
      foreach (var stateName in stateNames)
      {
        if (string.Equals(candidate, stateName, StringComparison.OrdinalIgnoreCase))
        {
          matches.Add(stateName);
        }
      }

      if (matches.Count > 0)
      {
        clipName = matches[0];
        return true;
      }
    }

    clipName = "Bored";
    return false;
  }

  private static bool IsActiveBelowRoot(Transform target, Transform root)
  {
    var current = target;
    while (current != null && current != root)
    {
      if (!current.gameObject.activeSelf)
      {
        return false;
      }

      current = current.parent;
    }

    return current == root;
  }

  private static int CountVisibleMeshes(GameObject root)
  {
    return root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
      .Count(renderer => renderer != null && renderer.enabled && IsActiveBelowRoot(renderer.transform, root.transform));
  }

  private sealed class TemplateCandidate
  {
    internal TemplateCandidate(GameObject gameObject, int rendererCount, string sceneName, string path)
    {
      GameObject = gameObject;
      RendererCount = rendererCount;
      SceneName = sceneName;
      Path = path;
    }

    internal GameObject GameObject { get; }
    internal int RendererCount { get; }
    internal string SceneName { get; }
    internal string Path { get; }
  }

  private static string GetHierarchyPath(Transform transform)
  {
    var names = new List<string>();
    var current = transform;
    while (current != null)
    {
      names.Add(current.name);
      current = current.parent;
    }

    names.Reverse();
    return string.Join("/", names);
  }
}
