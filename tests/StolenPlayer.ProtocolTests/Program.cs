using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using StolenPlayer.Networking;
using StolenPlayer.Protocol;
using StolenPlayer.World;

ProtocolCodecTests.RunAll();

internal static class ProtocolCodecTests
{
  internal static void RunAll()
  {
    RoundTripsEnvelopeAndPayload();
    RejectsMalformedHeaders();
    RejectsUnsupportedAndUnknownMessages();
    EnforcesFrameSize();
    HandlesSequenceWraparound();
    RoundTripsHandshakePayload();
    RejectsMalformedHandshakePayloads();
    RoundTripsPlayerPoseAndRoster();
    UdpPoseDatagramsRoundTripAndRejectMalformedPackets();
    RoundTripsSceneNamePayload();
    DoorInteractionPayloadRoundTripsAndRejectsMalformedData();
    StableObjectIdentityTests.RunAll();
    TcpTransportConnectsAndFramesPackets();
    UdpPoseTransportExchangesDatagrams();
    Console.WriteLine("Protocol codec checks passed.");
  }

  private static void RoundTripsEnvelopeAndPayload()
  {
    var payload = new byte[] { 4, 8, 15, 16, 23, 42 };
    var source = new ProtocolMessage(ProtocolCodec.CurrentVersion, MessageKind.Hello, 76561198000000001, 19, payload);
    var encoded = ProtocolCodec.Encode(source);

    Assert(ProtocolCodec.TryDecode(encoded, out var decoded, out var error), $"Round trip failed: {error}");
    Assert(decoded.Version == source.Version, "Protocol version changed during round trip.");
    Assert(decoded.Kind == source.Kind, "Message kind changed during round trip.");
    Assert(decoded.SenderPeerId == source.SenderPeerId, "Sender identity changed during round trip.");
    Assert(decoded.Sequence == source.Sequence, "Sequence changed during round trip.");
    Assert(decoded.Payload.SequenceEqual(payload), "Payload changed during round trip.");
  }

  private static void RejectsMalformedHeaders()
  {
    Assert(!ProtocolCodec.TryDecode(null!, out _, out _), "Null packet was accepted.");
    Assert(!ProtocolCodec.TryDecode(Array.Empty<byte>(), out _, out _), "Empty packet was accepted.");
    Assert(!ProtocolCodec.TryDecode(new byte[ProtocolCodec.HeaderLength - 1], out _, out _), "Truncated header was accepted.");

    var valid = ProtocolCodec.Encode(new ProtocolMessage(ProtocolCodec.CurrentVersion, MessageKind.Heartbeat, 10, 1, Array.Empty<byte>()));
    var badMagic = (byte[])valid.Clone();
    badMagic[0] ^= 0xFF;
    Assert(!ProtocolCodec.TryDecode(badMagic, out _, out _), "Packet with invalid magic was accepted.");

    var zeroSender = (byte[])valid.Clone();
    Array.Clear(zeroSender, 7, sizeof(ulong));
    Assert(!ProtocolCodec.TryDecode(zeroSender, out _, out _), "Packet with a zero sender was accepted.");

    var zeroSequence = (byte[])valid.Clone();
    Array.Clear(zeroSequence, 15, sizeof(uint));
    Assert(!ProtocolCodec.TryDecode(zeroSequence, out _, out _), "Packet with a zero sequence was accepted.");
  }

  private static void RejectsUnsupportedAndUnknownMessages()
  {
    var valid = ProtocolCodec.Encode(new ProtocolMessage(ProtocolCodec.CurrentVersion, MessageKind.Heartbeat, 10, 1, Array.Empty<byte>()));
    var unsupportedVersion = (byte[])valid.Clone();
    unsupportedVersion[4] = (byte)(ProtocolCodec.CurrentVersion + 1);
    Assert(!ProtocolCodec.TryDecode(unsupportedVersion, out _, out _), "Unsupported protocol version was accepted.");

    var unknownKind = (byte[])valid.Clone();
    unknownKind[6] = byte.MaxValue;
    Assert(!ProtocolCodec.TryDecode(unknownKind, out _, out _), "Unknown message kind was accepted.");
  }

  private static void EnforcesFrameSize()
  {
    var payload = new byte[ProtocolCodec.MaximumMessageSize - ProtocolCodec.HeaderLength];
    var encoded = ProtocolCodec.Encode(new ProtocolMessage(ProtocolCodec.CurrentVersion, MessageKind.Hello, 10, 1, payload));
    Assert(encoded.Length == ProtocolCodec.MaximumMessageSize, "Maximum-sized frame length was incorrect.");
    Assert(ProtocolCodec.TryDecode(encoded, out _, out _), "Maximum-sized frame was rejected.");

    var oversized = new byte[ProtocolCodec.MaximumMessageSize + 1];
    Assert(!ProtocolCodec.TryDecode(oversized, out _, out _), "Oversized incoming frame was accepted.");

    var threw = false;
    try
    {
      ProtocolCodec.Encode(new ProtocolMessage(ProtocolCodec.CurrentVersion, MessageKind.Hello, 10, 1, new byte[payload.Length + 1]));
    }
    catch (ArgumentOutOfRangeException)
    {
      threw = true;
    }

    Assert(threw, "Oversized outgoing frame was not rejected.");
  }

  private static void HandlesSequenceWraparound()
  {
    Assert(ProtocolCodec.IsSequenceNewer(2, 1), "Increasing sequence was not newer.");
    Assert(!ProtocolCodec.IsSequenceNewer(1, 1), "Duplicate sequence was treated as newer.");
    Assert(!ProtocolCodec.IsSequenceNewer(1, 2), "Older sequence was treated as newer.");
    Assert(ProtocolCodec.IsSequenceNewer(1, uint.MaxValue), "Sequence wraparound was not handled.");
  }

  private static void RoundTripsHandshakePayload()
  {
    const string gameVersion = "1.2.3-beta";
    const string pluginVersion = "0.3.0";
    const string playerName = "Stealing Astronaut";
    var payload = HandshakePayload.EncodeHello(gameVersion, pluginVersion, playerName);
    Assert(HandshakePayload.TryDecodeHello(payload, out var decodedVersion, out var decodedPluginVersion, out var decodedPlayerName), "Valid handshake payload was rejected.");
    Assert(decodedVersion == gameVersion, "Handshake game version changed during round trip.");
    Assert(decodedPluginVersion == pluginVersion, "Handshake plugin version changed during round trip.");
    Assert(decodedPlayerName == playerName, "Handshake player name changed during round trip.");
  }

  private static void RejectsMalformedHandshakePayloads()
  {
    Assert(!HandshakePayload.TryDecodeHello(null!, out _, out _, out _), "Null handshake payload was accepted.");
    Assert(!HandshakePayload.TryDecodeHello(new byte[6], out _, out _, out _), "Empty handshake versions were accepted.");

    var valid = HandshakePayload.EncodeHello("1.0", "0.8.0", "Player");
    var truncated = valid[..^1];
    Assert(!HandshakePayload.TryDecodeHello(truncated, out _, out _, out _), "Truncated handshake payload was accepted.");

    var invalidUtf8 = HandshakePayload.EncodeHello("1.0", "0.8.0", "Player");
    invalidUtf8[^1] = 0xFF;
    Assert(!HandshakePayload.TryDecodeHello(invalidUtf8, out _, out _, out _), "Invalid UTF-8 handshake name was accepted.");

    var invalidLength = HandshakePayload.EncodeHello("1.0", "0.8.0", "Player");
    invalidLength[0] = 0xFF;
    invalidLength[1] = 0xFF;
    Assert(!HandshakePayload.TryDecodeHello(invalidLength, out _, out _, out _), "Invalid handshake string length was accepted.");
  }

  private static void RoundTripsPlayerPoseAndRoster()
  {
    var pose = new PlayerPoseData(12.5f, -0.25f, 99.0f, 270.0f, PlayerPoseData.Moving | PlayerPoseData.Crouching, "level1");
    var encoded = PlayerPosePayload.Encode(pose);
    Assert(PlayerPosePayload.TryDecode(encoded, out var decoded), "Valid player pose was rejected.");
    Assert(decoded.X == pose.X && decoded.Y == pose.Y && decoded.Z == pose.Z && decoded.Yaw == pose.Yaw,
      "Player pose coordinates changed during round trip.");
    Assert(decoded.StateFlags == pose.StateFlags && decoded.SceneName == pose.SceneName, "Player pose state changed during round trip.");

    var worldEncoded = PlayerPosePayload.EncodeWorldPose(1234, pose);
    Assert(PlayerPosePayload.TryDecodeWorldPose(worldEncoded, out var peerId, out decoded), "Valid world pose was rejected.");
    Assert(peerId == 1234 && decoded.SceneName == pose.SceneName, "World pose identity or scene changed during round trip.");

    var roster = new[]
    {
      new PlayerIdentityData(1234, "Host"),
      new PlayerIdentityData(5678, "Player Two"),
      new PlayerIdentityData(9012, "Player Three")
    };
    var rosterBytes = PlayerRosterPayload.Encode(roster);
    Assert(PlayerRosterPayload.TryDecode(rosterBytes, out var decodedRoster), "Valid player roster was rejected.");
    Assert(decodedRoster.Length == roster.Length
      && decodedRoster.Zip(roster, (actual, expected) => actual.PeerId == expected.PeerId && actual.PlayerName == expected.PlayerName).All(equal => equal),
      "Player roster changed during round trip.");
    var duplicateRoster = PlayerRosterPayload.Encode(new[] { new PlayerIdentityData(1, "A"), new PlayerIdentityData(2, "B") });
    Array.Copy(duplicateRoster, 1, duplicateRoster, 11, sizeof(ulong));
    Assert(!PlayerRosterPayload.TryDecode(duplicateRoster, out _), "Roster with duplicate identities was accepted.");
    var duplicateNames = PlayerRosterPayload.Encode(new[] { new PlayerIdentityData(1, "A"), new PlayerIdentityData(2, "B") });
    duplicateNames[20] = duplicateNames[10];
    Assert(!PlayerRosterPayload.TryDecode(duplicateNames, out _), "Roster with duplicate names was accepted.");

    var invalidFlags = (byte[])encoded.Clone();
    invalidFlags[sizeof(float) * 4] = 0x80;
    Assert(!PlayerPosePayload.TryDecode(invalidFlags, out _), "Player pose with unknown state flags was accepted.");
  }

  private static void RoundTripsSceneNamePayload()
  {
    var payload = SceneNamePayload.Encode("level1");
    Assert(SceneNamePayload.TryDecode(payload, out var sceneName) && sceneName == "level1", "Host scene name did not round trip.");
    Assert(!SceneNamePayload.TryDecode(new byte[] { 3, (byte)'a', (byte)'/', (byte)'b' }, out _), "Scene path was accepted as a scene name.");
    Assert(!SceneNamePayload.TryDecode(new byte[] { 4, (byte)'a' }, out _), "Truncated scene name was accepted.");
  }

  private static void DoorInteractionPayloadRoundTripsAndRejectsMalformedData()
  {
    var key = Guid.NewGuid();
    foreach (var action in new byte[] { 0, 1, 2, 3, 4, 5 })
    {
      var payload = DoorInteractionPayload.Encode(key, "MainScene", action);
      Assert(DoorInteractionPayload.TryDecode(payload, out var decoded), "Valid door interaction payload was rejected.");
      Assert(decoded.Key == key && decoded.SceneName == "MainScene" && decoded.Action == action,
        "Door interaction payload changed fields during round trip.");
      Assert(decoded.IsSlow == (action == 3 || action == 4 || action == 5),
        "Door interaction payload changed the slow interaction mode.");
      Assert(decoded.IsOpen == (action == 2 || action == 5),
        "Door interaction payload changed the authoritative open state.");
    }

    var malformed = DoorInteractionPayload.Encode(key, "MainScene", 0);
    malformed[16] = 6;
    Assert(!DoorInteractionPayload.TryDecode(malformed, out _), "Unknown door interaction action was accepted.");
    malformed = DoorInteractionPayload.Encode(key, "MainScene", 0);
    malformed[17]++;
    Assert(!DoorInteractionPayload.TryDecode(malformed, out _), "Mismatched door interaction length was accepted.");
    malformed = DoorInteractionPayload.Encode(key, "MainScene", 0);
    malformed[18] = (byte)'/';
    Assert(!DoorInteractionPayload.TryDecode(malformed, out _), "Door interaction scene path was accepted.");
    Assert(!DoorInteractionPayload.TryDecode(Array.Empty<byte>(), out _), "Empty door interaction payload was accepted.");
  }

  private static void UdpPoseDatagramsRoundTripAndRejectMalformedPackets()
  {
    var token = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
    var payload = PlayerPosePayload.Encode(new PlayerPoseData(1, 2, 3, 90, PlayerPoseData.Moving, "MainScene"));
    var packet = UdpPoseDatagramCodec.Encode(UdpPoseKind.ClientPose, 1234, 7, token, payload);
    Assert(UdpPoseDatagramCodec.TryDecode(packet, out var decoded), "Valid UDP pose datagram was rejected.");
    Assert(decoded.Kind == UdpPoseKind.ClientPose && decoded.SenderPeerId == 1234 && decoded.Sequence == 7,
      "UDP pose header changed during round trip.");
    Assert(decoded.Token.SequenceEqual(token) && decoded.Payload.SequenceEqual(payload), "UDP token or pose payload changed during round trip.");
    Assert(!UdpPoseDatagramCodec.TryDecode(packet[..^1], out _), "Truncated UDP pose datagram was accepted.");
    Assert(!UdpPoseDatagramCodec.TryDecode(new byte[UdpPoseDatagramCodec.MaximumDatagramLength + 1], out _), "Oversized UDP pose datagram was accepted.");

    var accepted = WelcomePayload.EncodeAccepted(token);
    Assert(WelcomePayload.TryDecode(accepted, out var acceptedFlag, out var decodedToken, out var rejectionReason) && acceptedFlag
      && decodedToken.SequenceEqual(token), "Accepted welcome token did not round trip.");
    const string rejection = "Player name is already in use.";
    Assert(WelcomePayload.TryDecode(WelcomePayload.EncodeRejected(rejection), out acceptedFlag, out _, out rejectionReason)
      && !acceptedFlag && rejectionReason == rejection, "Rejected welcome reason did not round trip.");
    Assert(!WelcomePayload.TryDecode(new byte[] { 1 }, out _, out _, out _), "Accepted welcome without a UDP token was accepted.");
  }

  private static void TcpTransportConnectsAndFramesPackets()
  {
    var portProbe = new TcpListener(IPAddress.Loopback, 0);
    portProbe.Start();
    var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
    portProbe.Stop();

    using var host = CreateTransport();
    using var client = CreateTransport();
    long hostConnection = 0;
    long clientConnection = 0;
    byte[]? received = null;
    var disconnected = false;
    host.PeerConnected += peer => hostConnection = peer.ConnectionId;
    client.PeerConnected += peer => clientConnection = peer.ConnectionId;
    host.PacketReceived += (_, packet) => received = packet;
    host.PeerDisconnected += (_, _) => disconnected = true;

    Assert(host.StartListening(port, 1, out var listenError), $"TCP listener failed: {listenError}");
    Assert(client.Connect("127.0.0.1", port, out var connectError), $"TCP connect failed: {connectError}");
    PumpUntil(() => hostConnection != 0 && clientConnection != 0, host, client, 3000, "TCP peers did not connect.");

    var expectedPacket = ProtocolCodec.Encode(new ProtocolMessage(
      ProtocolCodec.CurrentVersion,
      MessageKind.Heartbeat,
      42,
      1,
      new byte[] { 1, 3, 3, 7 }));
    Assert(client.Send(clientConnection, expectedPacket, DeliveryMode.Reliable, out var sendError), $"TCP send failed: {sendError}");
    PumpUntil(() => received != null, host, client, 3000, "Host did not receive the framed TCP packet.");
    Assert(received!.SequenceEqual(expectedPacket), "TCP frame payload changed in transit.");

    client.Disconnect(clientConnection, "test complete");
    PumpUntil(() => disconnected, host, client, 3000, "Host did not observe TCP disconnect.");
  }

  private static void UdpPoseTransportExchangesDatagrams()
  {
    int port;
    using (var probe = new UdpClient(0))
    {
      port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
    using var host = CreateUdpTransport();
    using var client = CreateUdpTransport();
    IPEndPoint? clientEndpoint = null;
    byte[]? hostReceived = null;
    byte[]? clientReceived = null;
    host.DatagramReceived += (endpoint, packet) => { clientEndpoint = endpoint; hostReceived = packet; };
    client.DatagramReceived += (_, packet) => clientReceived = packet;

    Assert(host.StartHost(port, out var hostError), $"UDP listener failed: {hostError}");
    Assert(client.StartClient("127.0.0.1", port, out var clientError), $"UDP client failed: {clientError}");
    var outbound = new byte[] { 1, 2, 3, 4 };
    Assert(client.Send(outbound), "UDP client send failed.");
    PumpUdpUntil(() => hostReceived != null && clientEndpoint != null, host, client, 3000, "UDP host did not receive a datagram.");
    Assert(hostReceived!.SequenceEqual(outbound), "UDP payload changed in transit.");

    var response = new byte[] { 9, 8, 7 };
    Assert(host.Send(response, clientEndpoint), "UDP host reply failed.");
    PumpUdpUntil(() => clientReceived != null, host, client, 3000, "UDP client did not receive the host response.");
    Assert(clientReceived!.SequenceEqual(response), "UDP host response changed in transit.");
  }

  private static TcpTransport CreateTransport() => new TcpTransport(
    false,
    message => Console.WriteLine($"INFO: {message}"),
    message => Console.WriteLine($"WARN: {message}"),
    message => Console.WriteLine($"ERROR: {message}"));

  private static UdpPoseTransport CreateUdpTransport() => new UdpPoseTransport(
    message => Console.WriteLine($"WARN: {message}"),
    message => Console.WriteLine($"ERROR: {message}"));

  private static void PumpUdpUntil(Func<bool> condition, UdpPoseTransport first, UdpPoseTransport second, int timeoutMilliseconds, string error)
  {
    var timer = Stopwatch.StartNew();
    while (!condition() && timer.ElapsedMilliseconds < timeoutMilliseconds)
    {
      first.Update();
      second.Update();
      Thread.Sleep(2);
    }

    Assert(condition(), error);
  }

  private static void PumpUntil(Func<bool> condition, TcpTransport first, TcpTransport second, int timeoutMilliseconds, string error)
  {
    var timer = Stopwatch.StartNew();
    while (!condition() && timer.ElapsedMilliseconds < timeoutMilliseconds)
    {
      first.Update();
      second.Update();
      Thread.Sleep(2);
    }

    Assert(condition(), error);
  }

  private static void Assert(bool condition, string message)
  {
    if (!condition)
    {
      throw new InvalidOperationException(message);
    }
  }
}

internal static class StableObjectIdentityTests
{
  internal static void RunAll()
  {
    var first = Create("ts2-build", "MainScene", "DOMY/Interior/Cabinet/Door", "Door");
    var repeated = Create("ts2-build", "MainScene", "DOMY/Interior/Cabinet/Door", "Door");
    Assert(first.Equals(repeated), "Identical static-object paths produced different keys.");
    Assert(first.ToString().Length == 32, "Static-object key is not serialized as a 128-bit hex identifier.");
    Assert(!first.Equals(Create("other-build", "MainScene", "DOMY/Interior/Cabinet/Door", "Door")), "Build identity was not included in the key.");
    Assert(!first.Equals(Create("ts2-build", "OtherScene", "DOMY/Interior/Cabinet/Door", "Door")), "Scene name was not included in the key.");
    Assert(!first.Equals(Create("ts2-build", "MainScene", "DOMY/Interior/Other/Door", "Door")), "Hierarchy path was not included in the key.");
    Assert(!first.Equals(Create("ts2-build", "MainScene", "DOMY/Interior/Cabinet/Door", "Pickupable")), "Component type was not included in the key.");

    var registry = new StableObjectRegistry<object>();
    var localObject = new object();
    Assert(registry.TryRegister(first, localObject, out var error), $"Initial static-object registration failed: {error}");
    Assert(registry.TryRegister(repeated, localObject, out error), $"Idempotent static-object registration failed: {error}");
    Assert(registry.Count == 1, "Idempotent registration created a duplicate registry entry.");
    Assert(registry.TryGet(first, out var resolved) && ReferenceEquals(localObject, resolved), "Static-object registry did not resolve its registered instance.");
    Assert(!registry.TryRegister(first, new object(), out error) && error.Contains("Ambiguous"), "Duplicate local hierarchy identity was not rejected.");
    Assert(registry.Remove(first, localObject), "Registered static-object instance could not be removed.");
    Assert(!registry.TryGet(first, out _), "Removed static-object identity remained resolvable.");
    Assert(!StableObjectKey.TryCreate("", "scene", "path", "Door", out _, out _), "Empty build identity was accepted.");
    Assert(!StableObjectKey.TryCreate("build", " ", "path", "Door", out _, out _), "Whitespace scene name was accepted.");
    Assert(!StableObjectKey.TryCreate("build", "scene", "", "Door", out _, out _), "Empty hierarchy path was accepted.");
    Assert(!StableObjectKey.TryCreate("build", "scene", "path", "\t", out _, out _), "Whitespace component type was accepted.");
  }

  private static StableObjectKey Create(string build, string scene, string path, string component)
  {
    Assert(StableObjectKey.TryCreate(build, scene, path, component, out var key, out var error), $"Static-object key creation failed: {error}");
    return key;
  }

  private static void Assert(bool condition, string message)
  {
    if (!condition) throw new InvalidOperationException(message);
  }
}
