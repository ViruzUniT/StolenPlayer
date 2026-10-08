using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace StolenPlayer;

internal static class RemotePlayerVisualPreview
{
  private const string VerifiedTemplateSuffix = "NPCs/101/NPC";
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
      var templates = Resources.FindObjectsOfTypeAll<GameObject>()
        .Where(gameObject => gameObject != null && gameObject.scene.IsValid() && gameObject.scene.isLoaded && gameObject.name == "NPC")
        .Where(gameObject => gameObject.GetComponent<Animation>() != null)
        .Where(gameObject => GetHierarchyPath(gameObject.transform).EndsWith(VerifiedTemplateSuffix, StringComparison.Ordinal))
        .Select(gameObject => new
        {
          GameObject = gameObject,
          RendererCount = gameObject.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Count(renderer => renderer != null && renderer.enabled && IsActiveBelowRoot(renderer.transform, gameObject.transform))
        })
        .Where(candidate => candidate.RendererCount > 0)
        .OrderByDescending(candidate => candidate.RendererCount)
        .ThenBy(candidate => candidate.GameObject.scene.name, StringComparer.Ordinal)
        .ThenBy(candidate => GetHierarchyPath(candidate.GameObject.transform), StringComparer.Ordinal)
        .ToArray();

      if (templates.Length == 0)
      {
        error = "No scene NPC with the inspected hierarchy, legacy Animation, and enabled skinned meshes is loaded.";
        return false;
      }

      var template = templates[0];
      var templatePath = GetHierarchyPath(template.GameObject.transform);
      if (templates.Length > 1
          && templates[1].RendererCount == template.RendererCount
          && string.Equals(templates[1].GameObject.scene.name, template.GameObject.scene.name, StringComparison.Ordinal)
          && string.Equals(GetHierarchyPath(templates[1].GameObject.transform), templatePath, StringComparison.Ordinal))
      {
        error = $"Multiple equally ranked inactive NPC templates were found at '{template.GameObject.scene.name}:{templatePath}'. Preview was not spawned.";
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
      var visibleParts = template.GameObject.GetComponentsInChildren<SkinnedMeshRenderer>(true)
        .Where(renderer => renderer != null && renderer.enabled && IsActiveBelowRoot(renderer.transform, template.GameObject.transform))
        .Select(renderer => $"{GetHierarchyPath(renderer.transform)} ({renderer.sharedMesh?.name ?? "no mesh"})")
        .ToArray();
      Plugin.Log.LogInfo($"Spawned local visual-only NPC preview from '{template.GameObject.scene.name}:{templatePath}' using idle '{idleClipName}' with {template.RendererCount} skinned mesh parts: {string.Join("; ", visibleParts)}.");
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
