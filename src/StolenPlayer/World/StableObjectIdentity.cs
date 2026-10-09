using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace StolenPlayer.World;

/// <summary>A deterministic identity for a static object in a particular game scene/build.</summary>
internal readonly struct StableObjectKey : IEquatable<StableObjectKey>
{
  private StableObjectKey(Guid value, string canonicalIdentity)
  {
    Value = value;
    CanonicalIdentity = canonicalIdentity;
  }

  internal Guid Value { get; }
  internal string CanonicalIdentity { get; }

  internal static bool TryCreate(
    string buildIdentity,
    string sceneName,
    string hierarchyPath,
    string componentType,
    out StableObjectKey key,
    out string error)
  {
    key = default;
    if (string.IsNullOrWhiteSpace(buildIdentity)
        || string.IsNullOrWhiteSpace(sceneName)
        || string.IsNullOrWhiteSpace(hierarchyPath)
        || string.IsNullOrWhiteSpace(componentType))
    {
      error = "Build, scene, hierarchy path, and component type are all required for a static object identity.";
      return false;
    }

    var canonicalIdentity = EncodeParts(buildIdentity, sceneName, hierarchyPath, componentType);
    using (var sha256 = SHA256.Create())
    {
      var digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonicalIdentity));
      var idBytes = new byte[16];
      Buffer.BlockCopy(digest, 0, idBytes, 0, idBytes.Length);
      key = new StableObjectKey(new Guid(idBytes), canonicalIdentity);
    }

    error = string.Empty;
    return true;
  }

  public bool Equals(StableObjectKey other) => Value.Equals(other.Value);
  public override bool Equals(object? obj) => obj is StableObjectKey other && Equals(other);
  public override int GetHashCode() => Value.GetHashCode();
  public override string ToString() => Value.ToString("N");

  private static string EncodeParts(params string[] parts)
  {
    var builder = new StringBuilder();
    foreach (var part in parts)
    {
      builder.Append(part.Length).Append(':').Append(part);
    }

    return builder.ToString();
  }
}

/// <summary>Rejects ambiguous local mappings before static objects are exposed to replication.</summary>
internal sealed class StableObjectRegistry<T> where T : class
{
  private readonly Dictionary<Guid, Registration> _registrations = new Dictionary<Guid, Registration>();

  internal int Count => _registrations.Count;

  internal bool TryRegister(StableObjectKey key, T instance, out string error)
  {
    if (key.Value == Guid.Empty)
    {
      error = "An empty static object identity cannot be registered.";
      return false;
    }

    if (instance == null)
    {
      error = "A static object identity cannot be registered without a local instance.";
      return false;
    }

    if (_registrations.TryGetValue(key.Value, out var existing))
    {
      if (!string.Equals(existing.CanonicalIdentity, key.CanonicalIdentity, StringComparison.Ordinal))
      {
        error = $"Static object identity hash collision for key {key.Value:N}.";
        return false;
      }

      if (!ReferenceEquals(existing.Instance, instance))
      {
        error = $"Ambiguous static object identity maps multiple local objects to key {key.Value:N}.";
        return false;
      }

      error = string.Empty;
      return true;
    }

    _registrations.Add(key.Value, new Registration(key.CanonicalIdentity, instance));
    error = string.Empty;
    return true;
  }

  internal bool TryGet(StableObjectKey key, out T? instance)
  {
    if (_registrations.TryGetValue(key.Value, out var registration)
        && string.Equals(registration.CanonicalIdentity, key.CanonicalIdentity, StringComparison.Ordinal))
    {
      instance = registration.Instance;
      return true;
    }

    instance = null;
    return false;
  }

  internal bool TryGet(Guid value, out T? instance)
  {
    if (_registrations.TryGetValue(value, out var registration))
    {
      instance = registration.Instance;
      return true;
    }

    instance = null;
    return false;
  }

  internal bool Remove(StableObjectKey key, T instance)
  {
    if (!_registrations.TryGetValue(key.Value, out var registration)
        || !ReferenceEquals(registration.Instance, instance)
        || !string.Equals(registration.CanonicalIdentity, key.CanonicalIdentity, StringComparison.Ordinal))
    {
      return false;
    }

    return _registrations.Remove(key.Value);
  }

  private sealed class Registration
  {
    internal Registration(string canonicalIdentity, T instance)
    {
      CanonicalIdentity = canonicalIdentity;
      Instance = instance;
    }

    internal string CanonicalIdentity { get; }
    internal T Instance { get; }
  }
}
