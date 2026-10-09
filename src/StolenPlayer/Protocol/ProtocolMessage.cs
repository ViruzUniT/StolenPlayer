using System;
using System.Linq;
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
  ClientSceneReady = 10,
  UdpPathReady = 11,
  DoorIntent = 12,
  DoorState = 13,
  DoorSnapshotBegin = 14,
  DoorSnapshotEntry = 15,
  DoorSnapshotComplete = 16
}

internal readonly struct DoorStateSnapshot
{
  internal DoorStateSnapshot(Guid key, bool isOpen, bool isSlow)
  {
    Key = key;
    IsOpen = isOpen;
    IsSlow = isSlow;
  }

  internal Guid Key { get; }
  internal bool IsOpen { get; }
  internal bool IsSlow { get; }
}

internal readonly struct DoorSnapshotMarker
{
  internal DoorSnapshotMarker(string sceneName, int entryCount)
  {
    SceneName = sceneName;
    EntryCount = entryCount;
  }

  internal string SceneName { get; }
  internal int EntryCount { get; }
}

internal static class DoorSnapshotPayload
{
  internal const int MaximumEntries = 4096;

  internal static byte[] Encode(string sceneName, int entryCount)
  {
    if (entryCount < 0 || entryCount > MaximumEntries)
      throw new ArgumentOutOfRangeException(nameof(entryCount));
    var scenePayload = SceneNamePayload.Encode(sceneName);
    var payload = new byte[sizeof(ushort) + scenePayload.Length];
    payload[0] = (byte)entryCount;
    payload[1] = (byte)(entryCount >> 8);
    Array.Copy(scenePayload, 0, payload, sizeof(ushort), scenePayload.Length);
    return payload;
  }

  internal static bool TryDecode(byte[] payload, out DoorSnapshotMarker marker)
  {
    marker = default;
    if (payload == null || payload.Length < sizeof(ushort) + 2) return false;
    var count = payload[0] | (payload[1] << 8);
    if (count > MaximumEntries) return false;
    var scenePayload = new byte[payload.Length - sizeof(ushort)];
    Array.Copy(payload, sizeof(ushort), scenePayload, 0, scenePayload.Length);
    if (!SceneNamePayload.TryDecode(scenePayload, out var sceneName)) return false;
    marker = new DoorSnapshotMarker(sceneName, count);
    return true;
  }
}

internal readonly struct DoorInteractionData
{
  internal DoorInteractionData(Guid key, string sceneName, byte action)
  {
    Key = key;
    SceneName = sceneName;
    Action = action;
  }

  internal Guid Key { get; }
  internal string SceneName { get; }
  // Actions: 0 normal toggle intent, 1/2 normal closed/open state,
  // 3 slow toggle intent, 4/5 slow closed/open state.
  internal byte Action { get; }
  internal bool IsSlow => Action == 3 || Action == 4 || Action == 5;
  internal bool IsOpen => Action == 2 || Action == 5;
}

internal static class DoorInteractionPayload
{
  private const int FixedLength = 18;
  private const int MaximumSceneNameBytes = 128;
  private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

  internal static byte[] Encode(Guid key, string sceneName, byte action)
  {
    var sceneBytes = Encoding.UTF8.GetBytes(sceneName ?? string.Empty);
    if (key == Guid.Empty || sceneBytes.Length == 0 || sceneBytes.Length > MaximumSceneNameBytes
        || action > 5)
      throw new ArgumentException("Door interaction fields are invalid.");

    var payload = new byte[FixedLength + sceneBytes.Length];
    Array.Copy(key.ToByteArray(), payload, 16);
    payload[16] = action;
    payload[17] = (byte)sceneBytes.Length;
    Array.Copy(sceneBytes, 0, payload, FixedLength, sceneBytes.Length);
    return payload;
  }

  internal static bool TryDecode(byte[] payload, out DoorInteractionData interaction)
  {
    interaction = default;
    if (payload == null || payload.Length < FixedLength + 1
        || payload.Length > FixedLength + MaximumSceneNameBytes
        || payload[16] > 5 || payload[17] == 0
        || payload.Length != FixedLength + payload[17]) return false;
    var key = new Guid(payload.Take(16).ToArray());
    if (key == Guid.Empty) return false;
    try
    {
      var sceneName = StrictUtf8.GetString(payload, FixedLength, payload[17]);
      if (string.IsNullOrWhiteSpace(sceneName) || sceneName.Contains("..")
          || sceneName.Contains("/") || sceneName.Contains("\\")) return false;
      interaction = new DoorInteractionData(key, sceneName, payload[16]);
      return true;
    }
    catch (DecoderFallbackException) { return false; }
  }
}

internal enum UdpPoseKind : byte
{
  ClientPose = 1,
  WorldPose = 2
}

internal readonly struct UdpPoseDatagram
{
  internal UdpPoseDatagram(UdpPoseKind kind, ulong senderPeerId, uint sequence, byte[] token, byte[] payload)
  {
    Kind = kind;
    SenderPeerId = senderPeerId;
    Sequence = sequence;
    Token = token;
    Payload = payload;
  }

  internal UdpPoseKind Kind { get; }
  internal ulong SenderPeerId { get; }
  internal uint Sequence { get; }
  internal byte[] Token { get; }
  internal byte[] Payload { get; }
}

/// <summary>Small versioned datagrams with a per-connection token and an independent sequence stream.</summary>
internal static class UdpPoseDatagramCodec
{
  private const uint Magic = 0x44505553; // "SUPD" in little-endian bytes.
  private const int TokenLength = 16;
  private const int HeaderLength = sizeof(uint) + sizeof(ushort) + sizeof(byte) + sizeof(ulong) + sizeof(uint) + TokenLength;
  internal const int MaximumDatagramLength = 512;

  internal static byte[] Encode(UdpPoseKind kind, ulong senderPeerId, uint sequence, byte[] token, byte[] payload)
  {
    if (!Enum.IsDefined(typeof(UdpPoseKind), kind) || senderPeerId == 0 || sequence == 0
        || token == null || token.Length != TokenLength || payload == null
        || payload.Length == 0 || HeaderLength + payload.Length > MaximumDatagramLength)
    {
      throw new ArgumentException("UDP pose datagram fields are invalid.");
    }

    var packet = new byte[HeaderLength + payload.Length];
    WriteUInt32(packet, 0, Magic);
    WriteUInt16(packet, sizeof(uint), ProtocolCodec.CurrentVersion);
    packet[sizeof(uint) + sizeof(ushort)] = (byte)kind;
    WriteUInt64(packet, sizeof(uint) + sizeof(ushort) + sizeof(byte), senderPeerId);
    WriteUInt32(packet, sizeof(uint) + sizeof(ushort) + sizeof(byte) + sizeof(ulong), sequence);
    Array.Copy(token, 0, packet, HeaderLength - TokenLength, TokenLength);
    Array.Copy(payload, 0, packet, HeaderLength, payload.Length);
    return packet;
  }

  internal static bool TryDecode(byte[] packet, out UdpPoseDatagram datagram)
  {
    datagram = default;
    if (packet == null || packet.Length <= HeaderLength || packet.Length > MaximumDatagramLength
        || ReadUInt32(packet, 0) != Magic
        || ReadUInt16(packet, sizeof(uint)) != ProtocolCodec.CurrentVersion)
        return false;

    var kind = (UdpPoseKind)packet[sizeof(uint) + sizeof(ushort)];
    var peerId = ReadUInt64(packet, sizeof(uint) + sizeof(ushort) + sizeof(byte));
    var sequenceOffset = sizeof(uint) + sizeof(ushort) + sizeof(byte) + sizeof(ulong);
    var sequence = ReadUInt32(packet, sequenceOffset);
    if (!Enum.IsDefined(typeof(UdpPoseKind), kind) || peerId == 0 || sequence == 0)
    {
      return false;
    }

    var token = new byte[TokenLength];
    var payload = new byte[packet.Length - HeaderLength];
    Array.Copy(packet, HeaderLength - TokenLength, token, 0, TokenLength);
    Array.Copy(packet, HeaderLength, payload, 0, payload.Length);
    if (kind == UdpPoseKind.ClientPose && !PlayerPosePayload.TryDecode(payload, out _)
        || kind == UdpPoseKind.WorldPose && !PlayerPosePayload.TryDecodeWorldPose(payload, out _, out _))
    {
      return false;
    }

    datagram = new UdpPoseDatagram(kind, peerId, sequence, token, payload);
    return true;
  }

  private static void WriteUInt16(byte[] bytes, int offset, ushort value)
  {
    bytes[offset] = (byte)value;
    bytes[offset + 1] = (byte)(value >> 8);
  }

  private static ushort ReadUInt16(byte[] bytes, int offset) => (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

  private static void WriteUInt32(byte[] bytes, int offset, uint value)
  {
    for (var index = 0; index < sizeof(uint); index++) bytes[offset + index] = (byte)(value >> (index * 8));
  }

  private static uint ReadUInt32(byte[] bytes, int offset)
  {
    uint value = 0;
    for (var index = 0; index < sizeof(uint); index++) value |= (uint)bytes[offset + index] << (index * 8);
    return value;
  }

  private static void WriteUInt64(byte[] bytes, int offset, ulong value)
  {
    for (var index = 0; index < sizeof(ulong); index++) bytes[offset + index] = (byte)(value >> (index * 8));
  }

  private static ulong ReadUInt64(byte[] bytes, int offset)
  {
    ulong value = 0;
    for (var index = 0; index < sizeof(ulong); index++) value |= (ulong)bytes[offset + index] << (index * 8);
    return value;
  }
}

internal static class WelcomePayload
{
  private const int TokenLength = 16;
  private const int MaximumRejectionBytes = 128;
  private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

  internal static byte[] EncodeAccepted(byte[] token)
  {
    if (token == null || token.Length != TokenLength) throw new ArgumentException("UDP session token must be 16 bytes.", nameof(token));
    var payload = new byte[1 + TokenLength];
    payload[0] = 1;
    Array.Copy(token, 0, payload, 1, TokenLength);
    return payload;
  }

  internal static byte[] EncodeRejected(string reason)
  {
    var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "Host rejected the connection." : reason.Trim();
    var reasonBytes = Encoding.UTF8.GetBytes(normalizedReason);
    while (reasonBytes.Length > MaximumRejectionBytes && normalizedReason.Length > 0)
    {
      normalizedReason = normalizedReason.Substring(0, normalizedReason.Length - 1);
      reasonBytes = Encoding.UTF8.GetBytes(normalizedReason);
    }
    if (reasonBytes.Length == 0) throw new ArgumentException("A rejection reason is required.", nameof(reason));
    var payload = new byte[sizeof(byte) * 2 + reasonBytes.Length];
    payload[0] = 0;
    payload[1] = (byte)reasonBytes.Length;
    Array.Copy(reasonBytes, 0, payload, 2, reasonBytes.Length);
    return payload;
  }

  internal static bool TryDecode(byte[] payload, out bool accepted, out byte[] token, out string rejectionReason)
  {
    accepted = false;
    token = Array.Empty<byte>();
    rejectionReason = string.Empty;
    if (payload == null || payload.Length < 1) return false;
    if (payload[0] == 0)
    {
      if (payload.Length < sizeof(byte) * 2 || payload[1] == 0 || payload[1] > MaximumRejectionBytes
          || payload.Length != sizeof(byte) * 2 + payload[1]) return false;
      try
      {
        rejectionReason = StrictUtf8.GetString(payload, 2, payload[1]);
        return !string.IsNullOrWhiteSpace(rejectionReason);
      }
      catch (DecoderFallbackException)
      {
        return false;
      }
    }
    if (payload[0] != 1 || payload.Length != 1 + TokenLength) return false;
    token = new byte[TokenLength];
    Array.Copy(payload, 1, token, 0, TokenLength);
    accepted = true;
    return true;
  }
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
  private const int MaximumNameBytes = 48;

  internal static byte[] Encode(IReadOnlyList<PlayerIdentityData> players)
  {
    if (players == null || players.Count == 0 || players.Count > MaximumPeerCount)
    {
      throw new ArgumentOutOfRangeException(nameof(players));
    }

    var encodedNames = new byte[players.Count][];
    var payloadLength = sizeof(byte);
    for (var index = 0; index < players.Count; index++)
    {
      if (!HandshakePayload.TryNormalizePlayerName(players[index].PlayerName, out var name))
        throw new ArgumentException("Roster player name is invalid.", nameof(players));
      encodedNames[index] = Encoding.UTF8.GetBytes(name);
      payloadLength += sizeof(ulong) + sizeof(byte) + encodedNames[index].Length;
    }

    var payload = new byte[payloadLength];
    payload[0] = (byte)players.Count;
    var unique = new HashSet<ulong>();
    var uniqueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var offset = sizeof(byte);
    for (var index = 0; index < players.Count; index++)
    {
      var player = players[index];
      if (player.PeerId == 0 || !unique.Add(player.PeerId) || !uniqueNames.Add(player.PlayerName.Trim()))
      {
        throw new ArgumentException("Roster peer IDs and names must be nonzero and unique.", nameof(players));
      }

      WriteUInt64(payload, offset, player.PeerId);
      offset += sizeof(ulong);
      payload[offset++] = (byte)encodedNames[index].Length;
      Array.Copy(encodedNames[index], 0, payload, offset, encodedNames[index].Length);
      offset += encodedNames[index].Length;
    }

    return payload;
  }

  internal static bool TryDecode(byte[] payload, out PlayerIdentityData[] players)
  {
    players = Array.Empty<PlayerIdentityData>();
    if (payload == null || payload.Length < sizeof(byte))
    {
      return false;
    }

    var count = payload[0];
    if (count == 0 || count > MaximumPeerCount)
    {
      return false;
    }

    var entries = new PlayerIdentityData[count];
    var unique = new HashSet<ulong>();
    var uniqueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var offset = sizeof(byte);
    for (var index = 0; index < count; index++)
    {
      if (payload.Length - offset < sizeof(ulong) + sizeof(byte)) return false;
      var id = ReadUInt64(payload, offset);
      offset += sizeof(ulong);
      var nameLength = payload[offset++];
      if (id == 0 || !unique.Add(id) || nameLength == 0 || nameLength > MaximumNameBytes
          || payload.Length - offset < nameLength)
      {
        return false;
      }

      string name;
      try
      {
        name = new UTF8Encoding(false, true).GetString(payload, offset, nameLength);
      }
      catch (DecoderFallbackException)
      {
        return false;
      }

      if (!HandshakePayload.TryNormalizePlayerName(name, out var normalizedName)
          || !string.Equals(name, normalizedName, StringComparison.Ordinal)
          || !uniqueNames.Add(normalizedName)) return false;

      entries[index] = new PlayerIdentityData(id, normalizedName);
      offset += nameLength;
    }

    if (offset != payload.Length) return false;
    players = entries;
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

internal readonly struct PlayerIdentityData
{
  internal PlayerIdentityData(ulong peerId, string playerName)
  {
    PeerId = peerId;
    PlayerName = playerName;
  }

  internal ulong PeerId { get; }
  internal string PlayerName { get; }
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
  internal const ushort CurrentVersion = 8;
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
  private const int FixedLength = sizeof(ushort) * 3;
  private const int MaximumVersionBytes = 64;
  private const int MaximumNameBytes = 48;
  private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

  internal static byte[] EncodeHello(string gameVersion, string pluginVersion, string playerName)
  {
    if (string.IsNullOrEmpty(gameVersion) || string.IsNullOrEmpty(pluginVersion))
    {
      throw new ArgumentException("Game and plugin versions are required.");
    }

    var gameVersionBytes = Encoding.UTF8.GetBytes(gameVersion);
    var pluginVersionBytes = Encoding.UTF8.GetBytes(pluginVersion);
    if (!TryNormalizePlayerName(playerName, out var normalizedPlayerName))
      throw new ArgumentException("Player name must contain 1–24 non-control characters.", nameof(playerName));
    var playerNameBytes = Encoding.UTF8.GetBytes(normalizedPlayerName);
    if (gameVersionBytes.Length > MaximumVersionBytes || pluginVersionBytes.Length > MaximumVersionBytes)
    {
      throw new ArgumentOutOfRangeException(nameof(gameVersion), "Version string is too long for the handshake.");
    }

    var payload = new byte[FixedLength + gameVersionBytes.Length + pluginVersionBytes.Length + playerNameBytes.Length];
    WriteUInt16(payload, 0, (ushort)gameVersionBytes.Length);
    WriteUInt16(payload, sizeof(ushort), (ushort)pluginVersionBytes.Length);
    WriteUInt16(payload, sizeof(ushort) * 2, (ushort)playerNameBytes.Length);
    Array.Copy(gameVersionBytes, 0, payload, FixedLength, gameVersionBytes.Length);
    Array.Copy(pluginVersionBytes, 0, payload, FixedLength + gameVersionBytes.Length, pluginVersionBytes.Length);
    Array.Copy(playerNameBytes, 0, payload, FixedLength + gameVersionBytes.Length + pluginVersionBytes.Length, playerNameBytes.Length);
    return payload;
  }

  internal static bool TryDecodeHello(byte[] payload, out string gameVersion, out string pluginVersion, out string playerName)
  {
    gameVersion = string.Empty;
    pluginVersion = string.Empty;
    playerName = string.Empty;
    if (payload == null || payload.Length < FixedLength)
    {
      return false;
    }

    var gameVersionLength = ReadUInt16(payload, 0);
    var pluginVersionLength = ReadUInt16(payload, sizeof(ushort));
    var playerNameLength = ReadUInt16(payload, sizeof(ushort) * 2);
    if (gameVersionLength == 0 || gameVersionLength > MaximumVersionBytes
        || pluginVersionLength == 0 || pluginVersionLength > MaximumVersionBytes
        || playerNameLength == 0 || playerNameLength > MaximumNameBytes
        || payload.Length != FixedLength + gameVersionLength + pluginVersionLength + playerNameLength)
    {
      return false;
    }

    try
    {
      gameVersion = StrictUtf8.GetString(payload, FixedLength, gameVersionLength);
      pluginVersion = StrictUtf8.GetString(payload, FixedLength + gameVersionLength, pluginVersionLength);
      playerName = StrictUtf8.GetString(payload, FixedLength + gameVersionLength + pluginVersionLength, playerNameLength);
      return gameVersion.Length > 0 && pluginVersion.Length > 0
        && TryNormalizePlayerName(playerName, out var normalizedName)
        && string.Equals(playerName, normalizedName, StringComparison.Ordinal);
    }
    catch (DecoderFallbackException)
    {
      return false;
    }
  }

  internal static bool TryNormalizePlayerName(string? playerName, out string normalizedName)
  {
    normalizedName = (playerName ?? string.Empty).Trim();
    if (normalizedName.Length == 0 || normalizedName.Length > 24) return false;
    foreach (var character in normalizedName)
    {
      if (char.IsControl(character)) return false;
    }

    try
    {
      return StrictUtf8.GetByteCount(normalizedName) <= MaximumNameBytes;
    }
    catch (EncoderFallbackException)
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
