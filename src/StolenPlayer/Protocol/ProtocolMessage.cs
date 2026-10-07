using System;
using System.Text;

namespace StolenPlayer.Protocol;

internal enum MessageKind : byte
{
  Hello = 1,
  Welcome = 2,
  Heartbeat = 3,
  HeartbeatAck = 4,
  Disconnect = 5,
  ClientPlayerPose = 6,
  WorldPlayerPose = 7,
  PlayerRoster = 8,
  HostScene = 9,
  ClientSceneReady = 10
}

internal static class SceneNamePayload
{
  private const int MaximumSceneNameBytes = 128;
  private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

  internal static byte[] Encode(string sceneName)
  {
    if (string.IsNullOrWhiteSpace(sceneName)) throw new ArgumentException("Scene name is required.", nameof(sceneName));
    var bytes = Encoding.UTF8.GetBytes(sceneName);
    if (bytes.Length == 0 || bytes.Length > MaximumSceneNameBytes) throw new ArgumentOutOfRangeException(nameof(sceneName));
    var payload = new byte[bytes.Length + 1];
    payload[0] = (byte)bytes.Length;
    Array.Copy(bytes, 0, payload, 1, bytes.Length);
    return payload;
  }

  internal static bool TryDecode(byte[] payload, out string sceneName)
  {
    sceneName = string.Empty;
    if (payload == null || payload.Length < 2 || payload.Length > MaximumSceneNameBytes + 1 || payload[0] != payload.Length - 1) return false;
    try
    {
      sceneName = StrictUtf8.GetString(payload, 1, payload.Length - 1);
      return !string.IsNullOrWhiteSpace(sceneName) && !sceneName.Contains("..")
        && !sceneName.Contains("/") && !sceneName.Contains("\\");
    }
    catch (DecoderFallbackException)
    {
      return false;
    }
  }
}

internal readonly struct PlayerPoseData
{
  internal const byte Moving = 1;
  internal const byte Running = 2;
  internal const byte Crouching = 4;

  internal PlayerPoseData(float x, float y, float z, float yaw, byte stateFlags, string sceneName)
  {
    X = x;
    Y = y;
    Z = z;
    Yaw = yaw;
    StateFlags = stateFlags;
    SceneName = sceneName;
  }

  internal float X { get; }
  internal float Y { get; }
  internal float Z { get; }
  internal float Yaw { get; }
  internal byte StateFlags { get; }
  internal string SceneName { get; }
}

internal static class PlayerPosePayload
{
  private const int FixedLength = sizeof(float) * 4 + sizeof(byte) * 2;
  private const int MaximumSceneNameBytes = 128;
  private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

  internal static byte[] Encode(PlayerPoseData pose)
  {
    var sceneBytes = Encoding.UTF8.GetBytes(pose.SceneName ?? string.Empty);
    if (!IsFinite(pose.X) || !IsFinite(pose.Y) || !IsFinite(pose.Z) || !IsFinite(pose.Yaw)
        || (pose.StateFlags & ~(PlayerPoseData.Moving | PlayerPoseData.Running | PlayerPoseData.Crouching)) != 0
        || sceneBytes.Length == 0 || sceneBytes.Length > MaximumSceneNameBytes)
    {
      throw new ArgumentException("Player pose contains invalid values.", nameof(pose));
    }

    var payload = new byte[FixedLength + sceneBytes.Length];
    WriteSingle(payload, 0, pose.X);
    WriteSingle(payload, sizeof(float), pose.Y);
    WriteSingle(payload, sizeof(float) * 2, pose.Z);
    WriteSingle(payload, sizeof(float) * 3, pose.Yaw);
    payload[sizeof(float) * 4] = pose.StateFlags;
    payload[sizeof(float) * 4 + 1] = (byte)sceneBytes.Length;
    Array.Copy(sceneBytes, 0, payload, FixedLength, sceneBytes.Length);
    return payload;
  }

  internal static bool TryDecode(byte[] payload, out PlayerPoseData pose)
  {
    pose = default;
    if (payload == null || payload.Length < FixedLength || payload.Length > FixedLength + MaximumSceneNameBytes)
    {
      return false;
    }

    var flags = payload[sizeof(float) * 4];
    var sceneLength = payload[sizeof(float) * 4 + 1];
    if ((flags & ~(PlayerPoseData.Moving | PlayerPoseData.Running | PlayerPoseData.Crouching)) != 0
        || sceneLength == 0 || sceneLength > MaximumSceneNameBytes || payload.Length != FixedLength + sceneLength)
    {
      return false;
    }

    try
    {
      var sceneName = StrictUtf8.GetString(payload, FixedLength, sceneLength);
      var x = ReadSingle(payload, 0);
      var y = ReadSingle(payload, sizeof(float));
      var z = ReadSingle(payload, sizeof(float) * 2);
      var yaw = ReadSingle(payload, sizeof(float) * 3);
      if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z) || !IsFinite(yaw))
      {
        return false;
      }

      pose = new PlayerPoseData(x, y, z, yaw, flags, sceneName);
      return true;
    }
    catch (DecoderFallbackException)
    {
      return false;
    }
  }

  internal static byte[] EncodeWorldPose(ulong peerId, PlayerPoseData pose)
  {
    if (peerId == 0)
    {
      throw new ArgumentOutOfRangeException(nameof(peerId));
    }

    var encodedPose = Encode(pose);
    var payload = new byte[sizeof(ulong) + encodedPose.Length];
    WriteUInt64(payload, 0, peerId);
    Array.Copy(encodedPose, 0, payload, sizeof(ulong), encodedPose.Length);
    return payload;
  }

  internal static bool TryDecodeWorldPose(byte[] payload, out ulong peerId, out PlayerPoseData pose)
  {
    peerId = 0;
    pose = default;
    if (payload == null || payload.Length <= sizeof(ulong))
    {
      return false;
    }

    peerId = ReadUInt64(payload, 0);
    return peerId != 0 && TryDecodeSlice(payload, sizeof(ulong), out pose);
  }

  private static bool TryDecodeSlice(byte[] payload, int offset, out PlayerPoseData pose)
  {
    var slice = new byte[payload.Length - offset];
    Array.Copy(payload, offset, slice, 0, slice.Length);
    return TryDecode(slice, out pose);
  }

  private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

  private static void WriteSingle(byte[] bytes, int offset, float value)
  {
    var raw = BitConverter.GetBytes(value);
    Array.Copy(raw, 0, bytes, offset, sizeof(float));
  }

  private static float ReadSingle(byte[] bytes, int offset) => BitConverter.ToSingle(bytes, offset);

  private static void WriteUInt64(byte[] bytes, int offset, ulong value)
  {
    for (var index = 0; index < sizeof(ulong); index++)
    {
      bytes[offset + index] = (byte)(value >> (index * 8));
    }
  }

  private static ulong ReadUInt64(byte[] bytes, int offset)
  {
    ulong value = 0;
    for (var index = 0; index < sizeof(ulong); index++)
    {
      value |= (ulong)bytes[offset + index] << (index * 8);
    }

    return value;
  }
}

internal static class PlayerRosterPayload
{
  private const int MaximumPeerCount = 4;

  internal static byte[] Encode(IReadOnlyList<ulong> peerIds)
  {
    if (peerIds == null || peerIds.Count == 0 || peerIds.Count > MaximumPeerCount)
    {
      throw new ArgumentOutOfRangeException(nameof(peerIds));
    }

    var payload = new byte[sizeof(byte) + sizeof(ulong) * peerIds.Count];
    payload[0] = (byte)peerIds.Count;
    var unique = new HashSet<ulong>();
    for (var index = 0; index < peerIds.Count; index++)
    {
      var id = peerIds[index];
      if (id == 0 || !unique.Add(id))
      {
        throw new ArgumentException("Roster peer IDs must be nonzero and unique.", nameof(peerIds));
      }

      WriteUInt64(payload, sizeof(byte) + sizeof(ulong) * index, id);
    }

    return payload;
  }

  internal static bool TryDecode(byte[] payload, out ulong[] peerIds)
  {
    peerIds = Array.Empty<ulong>();
    if (payload == null || payload.Length < sizeof(byte))
    {
      return false;
    }

    var count = payload[0];
    if (count == 0 || count > MaximumPeerCount || payload.Length != sizeof(byte) + sizeof(ulong) * count)
    {
      return false;
    }

    var ids = new ulong[count];
    var unique = new HashSet<ulong>();
    for (var index = 0; index < count; index++)
    {
      var id = ReadUInt64(payload, sizeof(byte) + sizeof(ulong) * index);
      if (id == 0 || !unique.Add(id))
      {
        return false;
      }

      ids[index] = id;
    }

    peerIds = ids;
    return true;
  }

  private static void WriteUInt64(byte[] bytes, int offset, ulong value)
  {
    for (var index = 0; index < sizeof(ulong); index++)
    {
      bytes[offset + index] = (byte)(value >> (index * 8));
    }
  }

  private static ulong ReadUInt64(byte[] bytes, int offset)
  {
    ulong value = 0;
    for (var index = 0; index < sizeof(ulong); index++)
    {
      value |= (ulong)bytes[offset + index] << (index * 8);
    }

    return value;
  }
}

internal readonly struct ProtocolMessage
{
  internal ProtocolMessage(ushort version, MessageKind kind, ulong senderPeerId, uint sequence, byte[] payload)
  {
    Version = version;
    Kind = kind;
    SenderPeerId = senderPeerId;
    Sequence = sequence;
    Payload = payload;
  }

  internal ushort Version { get; }
  internal MessageKind Kind { get; }
  internal ulong SenderPeerId { get; }
  internal uint Sequence { get; }
  internal byte[] Payload { get; }
}

internal static class ProtocolCodec
{
  internal const uint Magic = 0x43504C53; // "SLPC" in little-endian bytes.
  internal const ushort CurrentVersion = 3;
  internal const int HeaderLength = 19;
  internal const int MaximumMessageSize = 64 * 1024;

  internal static byte[] Encode(ProtocolMessage message)
  {
    if (message.Payload == null)
    {
      throw new ArgumentNullException(nameof(message.Payload));
    }

    if (message.Payload.Length > MaximumMessageSize - HeaderLength)
    {
      throw new ArgumentOutOfRangeException(nameof(message), "Protocol message exceeds the maximum frame size.");
    }

    var bytes = new byte[HeaderLength + message.Payload.Length];
    WriteUInt32(bytes, 0, Magic);
    WriteUInt16(bytes, 4, message.Version);
    bytes[6] = (byte)message.Kind;
    WriteUInt64(bytes, 7, message.SenderPeerId);
    WriteUInt32(bytes, 15, message.Sequence);
    Array.Copy(message.Payload, 0, bytes, HeaderLength, message.Payload.Length);
    return bytes;
  }

  internal static bool TryDecode(byte[] bytes, out ProtocolMessage message, out string error)
  {
    message = default;
    error = string.Empty;

    if (bytes == null || bytes.Length < HeaderLength || bytes.Length > MaximumMessageSize)
    {
      error = "Invalid protocol frame size.";
      return false;
    }

    if (ReadUInt32(bytes, 0) != Magic)
    {
      error = "Protocol magic did not match.";
      return false;
    }

    var version = ReadUInt16(bytes, 4);
    if (version != CurrentVersion)
    {
      error = $"Unsupported protocol version {version}.";
      return false;
    }

    var kind = (MessageKind)bytes[6];
    if (!Enum.IsDefined(typeof(MessageKind), kind))
    {
      error = $"Unknown protocol message kind {(byte)kind}.";
      return false;
    }

    var sender = ReadUInt64(bytes, 7);
    var sequence = ReadUInt32(bytes, 15);
    if (sender == 0 || sequence == 0)
    {
      error = "Protocol sender or sequence was invalid.";
      return false;
    }

    var payload = new byte[bytes.Length - HeaderLength];
    Array.Copy(bytes, HeaderLength, payload, 0, payload.Length);
    message = new ProtocolMessage(version, kind, sender, sequence, payload);
    return true;
  }

  internal static bool IsSequenceNewer(uint candidate, uint previous)
  {
    return unchecked((int)(candidate - previous)) > 0;
  }

  private static void WriteUInt16(byte[] bytes, int offset, ushort value)
  {
    bytes[offset] = (byte)value;
    bytes[offset + 1] = (byte)(value >> 8);
  }

  private static ushort ReadUInt16(byte[] bytes, int offset)
  {
    return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
  }

  private static void WriteUInt32(byte[] bytes, int offset, uint value)
  {
    bytes[offset] = (byte)value;
    bytes[offset + 1] = (byte)(value >> 8);
    bytes[offset + 2] = (byte)(value >> 16);
    bytes[offset + 3] = (byte)(value >> 24);
  }

  private static uint ReadUInt32(byte[] bytes, int offset)
  {
    return (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
  }

  private static void WriteUInt64(byte[] bytes, int offset, ulong value)
  {
    for (var i = 0; i < sizeof(ulong); i++)
    {
      bytes[offset + i] = (byte)(value >> (i * 8));
    }
  }

  private static ulong ReadUInt64(byte[] bytes, int offset)
  {
    ulong value = 0;
    for (var i = 0; i < sizeof(ulong); i++)
    {
      value |= (ulong)bytes[offset + i] << (i * 8);
    }

    return value;
  }
}

internal static class HandshakePayload
{
  private const int FixedLength = sizeof(ushort) * 2;
  private const int MaximumVersionBytes = 64;
  private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

  internal static byte[] EncodeHello(string gameVersion, string pluginVersion)
  {
    if (string.IsNullOrEmpty(gameVersion) || string.IsNullOrEmpty(pluginVersion))
    {
      throw new ArgumentException("Game and plugin versions are required.");
    }

    var gameVersionBytes = Encoding.UTF8.GetBytes(gameVersion);
    var pluginVersionBytes = Encoding.UTF8.GetBytes(pluginVersion);
    if (gameVersionBytes.Length > MaximumVersionBytes || pluginVersionBytes.Length > MaximumVersionBytes)
    {
      throw new ArgumentOutOfRangeException(nameof(gameVersion), "Version string is too long for the handshake.");
    }

    var payload = new byte[FixedLength + gameVersionBytes.Length + pluginVersionBytes.Length];
    WriteUInt16(payload, 0, (ushort)gameVersionBytes.Length);
    WriteUInt16(payload, sizeof(ushort), (ushort)pluginVersionBytes.Length);
    Array.Copy(gameVersionBytes, 0, payload, FixedLength, gameVersionBytes.Length);
    Array.Copy(pluginVersionBytes, 0, payload, FixedLength + gameVersionBytes.Length, pluginVersionBytes.Length);
    return payload;
  }

  internal static bool TryDecodeHello(byte[] payload, out string gameVersion, out string pluginVersion)
  {
    gameVersion = string.Empty;
    pluginVersion = string.Empty;
    if (payload == null || payload.Length < FixedLength)
    {
      return false;
    }

    var gameVersionLength = ReadUInt16(payload, 0);
    var pluginVersionLength = ReadUInt16(payload, sizeof(ushort));
    if (gameVersionLength == 0 || gameVersionLength > MaximumVersionBytes
        || pluginVersionLength == 0 || pluginVersionLength > MaximumVersionBytes
        || payload.Length != FixedLength + gameVersionLength + pluginVersionLength)
    {
      return false;
    }

    try
    {
      gameVersion = StrictUtf8.GetString(payload, FixedLength, gameVersionLength);
      pluginVersion = StrictUtf8.GetString(payload, FixedLength + gameVersionLength, pluginVersionLength);
      return gameVersion.Length > 0 && pluginVersion.Length > 0;
    }
    catch (DecoderFallbackException)
    {
      return false;
    }
  }

  private static void WriteUInt16(byte[] bytes, int offset, ushort value)
  {
    bytes[offset] = (byte)value;
    bytes[offset + 1] = (byte)(value >> 8);
  }

  private static ushort ReadUInt16(byte[] bytes, int offset)
  {
    return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
  }
}
