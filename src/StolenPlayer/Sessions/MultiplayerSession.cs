using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using StolenPlayer.Networking;
using StolenPlayer.Protocol;
using UnityEngine;

namespace StolenPlayer.Sessions;

internal enum SessionState
{
  Offline,
  StartingHost,
  Listening,
  Connecting,
  Handshaking,
  Connected,
  Error
}

internal sealed class MultiplayerSession : IDisposable
{
  private const double ConnectionTimeoutSeconds = 20.0;
  private const double HandshakeTimeoutSeconds = 15.0;
  private const double PeerTimeoutSeconds = 15.0;
  private const double SceneSynchronizationTimeoutSeconds = 120.0;
  private const double HeartbeatIntervalSeconds = 2.0;
  private const double RejectedPeerCloseDelaySeconds = 0.5;
  private const double MinimumPoseIntervalSeconds = 0.04;
  private const double MaximumMovementValidationIntervalSeconds = 0.5;
  private const int MaximumSupportedPlayers = 4;
  private const float MaximumMovementSpeed = 12.0f;
  private const float MovementAllowanceMeters = 0.8f;
  private const float MaximumCoordinateMagnitude = 10000.0f;

  private readonly PluginConfig _config;
  private readonly string _gameVersion;
  private readonly string _pluginVersion;
  private readonly Dictionary<long, PeerState> _peers = new Dictionary<long, PeerState>();
  private readonly List<long> _peerScratch = new List<long>();
  private readonly HashSet<ulong> _knownPeerIds = new HashSet<ulong>();
  private TcpTransport? _transport;
  private SessionState _state;
  private double _operationDeadline;
  private double _nextHeartbeat;
  private uint _sendSequence;
  private uint _rosterRevision;
  private ulong _localPeerId;
  private int _listenPort;
  private bool _isHost;
  private bool _disposed;
  private string? _pendingError;
  private string _status = "Ready for direct IP connections.";
  private PlayerPoseData? _lastLocalPose;
  private string _hostScene = string.Empty;
  private string _expectedHostScene = string.Empty;
  private double _clientSceneSyncDeadline;
  private bool _clientSceneReady;
  private bool _clientWorldReady;
  private bool _clientRosterReceived;

  internal event Action<ulong[]>? RosterChanged;
  internal event Action<ulong, PlayerPoseData>? PlayerPoseReceived;
  internal event Action<string>? HostSceneReceived;
  internal event Action<PlayerPoseData>? HostPoseReceived;

  internal MultiplayerSession(PluginConfig config, string gameVersion, string pluginVersion)
  {
    _config = config;
    _gameVersion = string.IsNullOrEmpty(gameVersion) ? "unknown" : gameVersion;
    _pluginVersion = pluginVersion;
    _localPeerId = CreatePeerId();
  }

  internal SessionState State => _state;
  internal string Status => _status;
  internal bool IsHost => _isHost;
  internal ulong LocalPeerId => _localPeerId;
  internal int ListenPort => _listenPort;
  internal string ExpectedHostScene => _expectedHostScene;
  internal bool IsClientSceneReady => _clientSceneReady;
  internal bool IsClientWorldReady => _clientWorldReady;
  internal bool HasClientRoster => _clientRosterReceived;
  internal ulong HostPeerId
  {
    get
    {
      if (_isHost) return 0;
      foreach (var peer in _peers.Values)
      {
        if (peer.Ready) return peer.RemotePeerId;
      }

      return 0;
    }
  }
  internal int ReadyPeerCount
  {
    get
    {
      var count = 0;
      foreach (var peer in _peers.Values)
      {
        if (peer.Ready)
        {
          count++;
        }
      }

      return count;
    }
  }

  internal void PublishLocalPose(PlayerPoseData pose)
  {
    if (_disposed || _localPeerId == 0 || string.IsNullOrEmpty(pose.SceneName))
    {
      return;
    }

    if (_isHost)
    {
      _lastLocalPose = pose;
      if (!string.Equals(_hostScene, pose.SceneName, StringComparison.Ordinal))
      {
        BeginHostScene(pose.SceneName);
      }

      PublishWorldPose(_localPeerId, pose);
      return;
    }

    _lastLocalPose = pose;
    if (_state != SessionState.Connected || !_clientWorldReady)
    {
      return;
    }

    long connectionId = 0;
    foreach (var pair in _peers)
    {
      if (pair.Value.Ready)
      {
        connectionId = pair.Key;
        break;
      }
    }

    if (connectionId != 0)
      Send(connectionId, MessageKind.ClientPlayerPose, PlayerPosePayload.Encode(pose), DeliveryMode.Reliable);
  }

  internal void Host(int port)
  {
    if (_disposed || _state != SessionState.Offline)
    {
      return;
    }

    EnsureTransport();
    _isHost = true;
    SetState(SessionState.StartingHost, $"Opening TCP port {port}...");
    var playerLimit = Math.Min(_config.MaxPlayers.Value, MaximumSupportedPlayers);
    if (!_transport!.StartListening(port, playerLimit - 1, out var error))
    {
      SetError(error);
      return;
    }

    _listenPort = port;
    SetState(SessionState.Listening, $"Listening for direct IP clients on TCP port {port}. Share your IP address and port.");
    Plugin.Log.LogInfo($"Direct IP host listening on TCP port {port}; capacity {playerLimit} total players.");
  }

  internal void Join(string address, int port)
  {
    if (_disposed || _state != SessionState.Offline)
    {
      Plugin.Log.LogWarning("Leave the current multiplayer session before joining another host.");
      return;
    }

    EnsureTransport();
    _isHost = false;
    SetState(SessionState.Connecting, $"Connecting to {address}:{port}...");
    _operationDeadline = Now + ConnectionTimeoutSeconds;
    if (!_transport!.Connect(address, port, out var error))
    {
      SetError(error);
    }
  }

  internal void Leave()
  {
    ResetSession("Left multiplayer session.");
  }

  internal void Update()
  {
    if (_disposed)
    {
      return;
    }

    if (_pendingError != null)
    {
      var reason = _pendingError;
      _pendingError = null;
      SetError(reason);
      return;
    }

    _transport?.Update();
    var now = Now;
    if (_state == SessionState.Connecting && now >= _operationDeadline)
    {
      SetError("TCP connection timed out.");
      return;
    }

    _peerScratch.Clear();
    foreach (var peerId in _peers.Keys)
    {
      _peerScratch.Add(peerId);
    }

    foreach (var peerId in _peerScratch)
    {
      if (!_peers.TryGetValue(peerId, out var peer))
      {
        continue;
      }

      if (peer.DisconnectAt > 0 && now >= peer.DisconnectAt)
      {
        _transport?.Disconnect(peerId, peer.DisconnectReason ?? "Peer rejected.");
        peer.DisconnectAt = 0;
        continue;
      }

      var synchronized = _isHost ? peer.SceneReady : _clientWorldReady;
      var timeout = !peer.Ready
        ? HandshakeTimeoutSeconds
        : synchronized ? PeerTimeoutSeconds : SceneSynchronizationTimeoutSeconds;
      if (_isHost && peer.Ready && !peer.SceneReady && peer.SceneSyncDeadline > 0 && now >= peer.SceneSyncDeadline)
      {
        _transport?.Disconnect(peerId, "Client scene synchronization timed out.");
        continue;
      }

      if (!_isHost && peer.Ready && !_clientWorldReady && _clientSceneSyncDeadline > 0 && now >= _clientSceneSyncDeadline)
      {
        SetError("Timed out while synchronizing the host scene, spawn point, or player roster.");
        return;
      }
      if (now - peer.LastReceiveTime > timeout)
      {
        _transport?.Disconnect(peerId, peer.Ready ? "Peer timed out." : "Handshake timed out.");
        continue;
      }

      if (peer.Ready && now >= _nextHeartbeat)
      {
        Send(peerId, MessageKind.Heartbeat, Array.Empty<byte>(), DeliveryMode.Reliable);
      }
    }

    if (now >= _nextHeartbeat)
    {
      _nextHeartbeat = now + HeartbeatIntervalSeconds;
    }
  }

  public void Dispose()
  {
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    DisposeTransport();
  }

  private static double Now => Time.realtimeSinceStartup;

  private static ulong CreatePeerId()
  {
    var bytes = new byte[sizeof(ulong)];
    using (var random = RandomNumberGenerator.Create())
    {
      random.GetBytes(bytes);
    }

    var peerId = BitConverter.ToUInt64(bytes, 0);
    return peerId == 0 ? 1UL : peerId;
  }

  private void EnsureTransport()
  {
    if (_transport != null)
    {
      return;
    }

    _transport = new TcpTransport(
      _config.VerboseNetworking.Value,
      message => Plugin.Log.LogInfo(message),
      message => Plugin.Log.LogWarning(message),
      message => Plugin.Log.LogError(message));
    _transport.PeerConnected += OnPeerConnected;
    _transport.PacketReceived += OnPacketReceived;
    _transport.PeerDisconnected += OnPeerDisconnected;
  }

  private void OnPeerConnected(TransportPeer peer)
  {
    if (!_isHost && _peers.Count > 0)
    {
      _transport?.Disconnect(peer.ConnectionId, "The client can join only one host at a time.");
      return;
    }

    if (_isHost && _peers.Count >= Math.Min(_config.MaxPlayers.Value, MaximumSupportedPlayers) - 1)
    {
      _transport?.Disconnect(peer.ConnectionId, "Session is full.");
      return;
    }

    var state = new PeerState(Now);
    _peers[peer.ConnectionId] = state;
    if (!_isHost)
    {
      SetState(SessionState.Handshaking, "Verifying host protocol and game version...");
    }

    Send(peer.ConnectionId, MessageKind.Hello, HandshakePayload.EncodeHello(_gameVersion, _pluginVersion), DeliveryMode.Reliable);
    if (_config.VerboseNetworking.Value)
    {
      Plugin.Log.LogDebug($"Started protocol handshake for TCP connection {peer.ConnectionId}.");
    }
  }

  private void OnPacketReceived(TransportPeer peer, byte[] packet)
  {
    if (!_peers.TryGetValue(peer.ConnectionId, out var state))
    {
      _transport?.Disconnect(peer.ConnectionId, "Packet received before protocol handshake began.");
      return;
    }

    if (!ProtocolCodec.TryDecode(packet, out var message, out var decodeError))
    {
      Plugin.Log.LogWarning($"Rejected packet on connection {peer.ConnectionId}: {decodeError}");
      _transport?.Disconnect(peer.ConnectionId, decodeError);
      return;
    }

    if (state.RemotePeerId == 0)
    {
      if (message.Kind != MessageKind.Hello || HasPeerIdentity(message.SenderPeerId, peer.ConnectionId))
      {
        _transport?.Disconnect(peer.ConnectionId, "First packet was not a unique peer hello.");
        return;
      }

      state.RemotePeerId = message.SenderPeerId;
    }
    else if (message.SenderPeerId != state.RemotePeerId)
    {
      _transport?.Disconnect(peer.ConnectionId, "Packet peer identity changed during the session.");
      return;
    }

    if (!ProtocolCodec.IsSequenceNewer(message.Sequence, state.LastReceivedSequence))
    {
      _transport?.Disconnect(peer.ConnectionId, "Stale or replayed packet sequence.");
      return;
    }

    state.LastReceivedSequence = message.Sequence;
    state.LastReceiveTime = Now;
    switch (message.Kind)
    {
      case MessageKind.Hello:
        HandleHello(peer.ConnectionId, state, message);
        break;
      case MessageKind.Welcome:
        HandleWelcome(peer.ConnectionId, state, message);
        break;
      case MessageKind.Heartbeat:
        if (!state.Ready || message.Payload.Length != 0)
        {
          _transport?.Disconnect(peer.ConnectionId, "Unexpected heartbeat packet.");
          return;
        }

        Send(peer.ConnectionId, MessageKind.HeartbeatAck, Array.Empty<byte>(), DeliveryMode.Reliable);
        break;
      case MessageKind.HeartbeatAck:
        if (!state.Ready || message.Payload.Length != 0)
        {
          _transport?.Disconnect(peer.ConnectionId, "Unexpected heartbeat acknowledgement.");
        }

        break;
      case MessageKind.ClientPlayerPose:
        HandleClientPlayerPose(peer.ConnectionId, state, message);
        break;
      case MessageKind.WorldPlayerPose:
        HandleWorldPlayerPose(peer.ConnectionId, state, message);
        break;
      case MessageKind.PlayerRoster:
        HandlePlayerRoster(peer.ConnectionId, state, message);
        break;
      case MessageKind.HostScene:
        HandleHostScene(peer.ConnectionId, state, message);
        break;
      case MessageKind.ClientSceneReady:
        HandleClientSceneReady(peer.ConnectionId, state, message);
        break;
      case MessageKind.Disconnect:
        _transport?.Disconnect(peer.ConnectionId, "Peer closed the session.");
        break;
      default:
        _transport?.Disconnect(peer.ConnectionId, "Unsupported protocol message.");
        break;
    }
  }

  private void HandleHello(long connectionId, PeerState state, ProtocolMessage message)
  {
    if (state.ReceivedHello || !HandshakePayload.TryDecodeHello(message.Payload, out var gameVersion, out var pluginVersion))
    {
      _transport?.Disconnect(connectionId, "Malformed or duplicate protocol hello.");
      return;
    }

    if (!string.Equals(gameVersion, _gameVersion, StringComparison.Ordinal)
        || !string.Equals(pluginVersion, _pluginVersion, StringComparison.Ordinal))
    {
      Send(connectionId, MessageKind.Welcome, new byte[] { 0 }, DeliveryMode.Reliable);
      state.DisconnectAt = Now + RejectedPeerCloseDelaySeconds;
      state.DisconnectReason = $"Peer game/plugin version {gameVersion}/{pluginVersion} is incompatible with {_gameVersion}/{_pluginVersion}.";
      return;
    }

    state.ReceivedHello = true;
    if (_isHost)
    {
      state.Ready = true;
      state.SceneSyncDeadline = Now + SceneSynchronizationTimeoutSeconds;
      Send(connectionId, MessageKind.Welcome, new byte[] { 1 }, DeliveryMode.Reliable);
      Plugin.Log.LogInfo($"TCP peer {state.RemotePeerId} passed the protocol handshake.");
      if (!string.IsNullOrEmpty(_hostScene))
      {
        Send(connectionId, MessageKind.HostScene, SceneNamePayload.Encode(_hostScene), DeliveryMode.Reliable);
      }
    }
  }

  private void HandleClientPlayerPose(long connectionId, PeerState state, ProtocolMessage message)
  {
    if (!_isHost || !state.Ready || !PlayerPosePayload.TryDecode(message.Payload, out var pose))
    {
      _transport?.Disconnect(connectionId, "Unexpected or malformed client player pose.");
      return;
    }

    var now = Now;
    if (now - state.LastPoseReceiveTime < MinimumPoseIntervalSeconds)
    {
      return;
    }

    if (!state.SceneReady || !IsPlausiblePose(pose) || _lastLocalPose == null
        || !string.Equals(pose.SceneName, _lastLocalPose.Value.SceneName, StringComparison.Ordinal))
    {
      return;
    }

    if (state.HasPose)
    {
      var elapsed = Math.Min(MaximumMovementValidationIntervalSeconds,
        Math.Max(MinimumPoseIntervalSeconds, now - state.LastPoseReceiveTime));
      var dx = pose.X - state.LastPose.X;
      var dy = pose.Y - state.LastPose.Y;
      var dz = pose.Z - state.LastPose.Z;
      var maximumDistance = MaximumMovementSpeed * (float)elapsed + MovementAllowanceMeters;
      if ((dx * dx) + (dy * dy) + (dz * dz) > maximumDistance * maximumDistance)
      {
        if (_config.VerboseNetworking.Value)
        {
          Plugin.Log.LogDebug($"Rejected excessive movement request from peer {state.RemotePeerId}.");
        }

        return;
      }
    }

    state.LastPose = pose;
    state.HasPose = true;
    state.LastPoseReceiveTime = now;
    PlayerPoseReceived?.Invoke(state.RemotePeerId, pose);
    PublishWorldPose(state.RemotePeerId, pose);
  }

  private void HandleWorldPlayerPose(long connectionId, PeerState state, ProtocolMessage message)
  {
    if (_isHost || !state.Ready || !PlayerPosePayload.TryDecodeWorldPose(message.Payload, out var peerId, out var pose))
    {
      _transport?.Disconnect(connectionId, "Unexpected or malformed authoritative player pose.");
      return;
    }

    if (_lastLocalPose == null || !IsPlausiblePose(pose)
        || !_clientSceneReady || !string.Equals(pose.SceneName, _lastLocalPose.Value.SceneName, StringComparison.Ordinal))
    {
      return;
    }

    if (!IsInCurrentRoster(peerId))
    {
      _transport?.Disconnect(connectionId, "Host sent a pose for an unknown player identity.");
      return;
    }

    if (!_clientWorldReady)
    {
      if (peerId == state.RemotePeerId && _clientRosterReceived)
      {
        HostPoseReceived?.Invoke(pose);
      }

      return;
    }

    if (peerId != _localPeerId)
    {
      PlayerPoseReceived?.Invoke(peerId, pose);
    }
  }

  private void HandlePlayerRoster(long connectionId, PeerState state, ProtocolMessage message)
  {
    if (_isHost || !state.Ready || !PlayerRosterPayload.TryDecode(message.Payload, out var peerIds))
    {
      _transport?.Disconnect(connectionId, "Unexpected or malformed player roster.");
      return;
    }

    var containsLocal = false;
    var containsHost = false;
    foreach (var peerId in peerIds)
    {
      containsLocal |= peerId == _localPeerId;
      containsHost |= peerId == state.RemotePeerId;
    }

    if (!containsLocal || !containsHost)
    {
      _transport?.Disconnect(connectionId, "Host roster did not contain the local player and host.");
      return;
    }

    _knownPeerIds.Clear();
    foreach (var peerId in peerIds)
    {
      _knownPeerIds.Add(peerId);
    }

    _clientRosterReceived = true;
    SetState(SessionState.Connected, "Host scene loaded. Applying host spawn position and player roster.");
    RosterChanged?.Invoke(peerIds);
  }

  private void HandleHostScene(long connectionId, PeerState state, ProtocolMessage message)
  {
    if (_isHost || !state.Ready || !SceneNamePayload.TryDecode(message.Payload, out var sceneName))
    {
      _transport?.Disconnect(connectionId, "Unexpected or malformed host scene message.");
      return;
    }

    _expectedHostScene = sceneName;
    _clientSceneSyncDeadline = Now + SceneSynchronizationTimeoutSeconds;
    _clientSceneReady = false;
    _clientWorldReady = false;
    _clientRosterReceived = false;
    _knownPeerIds.Clear();
    RosterChanged?.Invoke(Array.Empty<ulong>());
    SetState(SessionState.Connected, $"Host scene: {sceneName}. Loading host game scene...");
    Plugin.Log.LogInfo($"Host selected scene '{sceneName}'; waiting for the client scene load.");
    HostSceneReceived?.Invoke(sceneName);
  }

  private void HandleClientSceneReady(long connectionId, PeerState state, ProtocolMessage message)
  {
    if (!_isHost || !state.Ready || !SceneNamePayload.TryDecode(message.Payload, out var sceneName))
    {
      _transport?.Disconnect(connectionId, "Unexpected or malformed client scene-ready message.");
      return;
    }

    if (!string.Equals(sceneName, _hostScene, StringComparison.Ordinal))
    {
      if (!string.IsNullOrEmpty(_hostScene))
        Send(connectionId, MessageKind.HostScene, SceneNamePayload.Encode(_hostScene), DeliveryMode.Reliable);
      return;
    }

    if (state.SceneReady) return;
    state.SceneReady = true;
    state.SceneSyncDeadline = 0;
    _rosterRevision++;
    Plugin.Log.LogInfo($"Peer {state.RemotePeerId} loaded host scene '{sceneName}'. Sending its initial player snapshot.");
    RefreshRosterAndSnapshots();
  }

  internal bool MarkClientSceneReady(string sceneName)
  {
    if (_isHost || _state != SessionState.Connected || string.IsNullOrEmpty(_expectedHostScene)
        || !string.Equals(sceneName, _expectedHostScene, StringComparison.Ordinal))
    {
      return false;
    }

    if (_clientSceneReady) return true;
    _clientSceneReady = true;
    foreach (var pair in _peers)
    {
      if (pair.Value.Ready)
      {
        Send(pair.Key, MessageKind.ClientSceneReady, SceneNamePayload.Encode(sceneName), DeliveryMode.Reliable);
        Plugin.Log.LogInfo($"Client loaded authoritative host scene '{sceneName}'. Waiting for the host's initial player roster.");
        return true;
      }
    }

    _clientSceneReady = false;
    _clientWorldReady = false;
    return false;
  }

  internal bool CompleteClientInitialSynchronization()
  {
    if (_isHost || !_clientSceneReady || !_clientRosterReceived)
    {
      return false;
    }

    _clientWorldReady = true;
    _clientSceneSyncDeadline = 0;
    SetState(SessionState.Connected, "Host scene, spawn position, and player roster synchronized.");
    Plugin.Log.LogInfo("Client initial synchronization complete; enabling player pose replication.");
    return true;
  }

  internal void FailSceneSynchronization(string reason)
  {
    SetError($"Could not load the host scene: {reason}");
  }

  private bool IsInCurrentRoster(ulong peerId)
  {
    if (peerId == _localPeerId)
    {
      return true;
    }

    return _isHost
      ? ContainsReadyPeer(peerId)
      : _knownPeerIds.Contains(peerId);
  }

  private bool ContainsReadyPeer(ulong peerId)
  {
    foreach (var peer in _peers.Values)
    {
      if (peer.Ready && peer.RemotePeerId == peerId)
      {
        return true;
      }
    }

    return false;
  }

  private static bool IsPlausiblePose(PlayerPoseData pose)
  {
    return Math.Abs(pose.X) <= MaximumCoordinateMagnitude
      && Math.Abs(pose.Y) <= MaximumCoordinateMagnitude
      && Math.Abs(pose.Z) <= MaximumCoordinateMagnitude
      && Math.Abs(pose.Yaw) <= 36000.0f;
  }

  private void PublishWorldPose(ulong peerId, PlayerPoseData pose)
  {
    var payload = PlayerPosePayload.EncodeWorldPose(peerId, pose);
    var revision = _rosterRevision;
    var recipients = new List<long>();
    foreach (var pair in _peers)
    {
      if (pair.Value.Ready && (_isHost ? pair.Value.SceneReady : true))
      {
        recipients.Add(pair.Key);
      }
    }

    foreach (var connectionId in recipients)
    {
      if (_peers.TryGetValue(connectionId, out var peer) && peer.Ready && (!_isHost || peer.SceneReady))
        Send(connectionId, MessageKind.WorldPlayerPose, payload, DeliveryMode.Reliable);
      if (_rosterRevision != revision) return;
    }
  }

  private void RefreshRosterAndSnapshots()
  {
    var revision = _rosterRevision;
    var peerIds = new List<ulong> { _localPeerId };
    foreach (var peer in _peers.Values)
    {
      if (peer.Ready && peer.SceneReady)
      {
        peerIds.Add(peer.RemotePeerId);
      }
    }

    var roster = peerIds.ToArray();
    _knownPeerIds.Clear();
    foreach (var peerId in roster)
    {
      _knownPeerIds.Add(peerId);
    }

    RosterChanged?.Invoke(roster);
    var rosterPayload = PlayerRosterPayload.Encode(roster);
    var recipientIds = new List<long>();
    foreach (var pair in _peers)
    {
      if (pair.Value.Ready && pair.Value.SceneReady) recipientIds.Add(pair.Key);
    }

    var remotePoses = new List<RemotePoseSnapshot>();
    foreach (var remote in _peers.Values)
    {
      if (remote.Ready && remote.SceneReady && remote.HasPose)
        remotePoses.Add(new RemotePoseSnapshot(remote.RemotePeerId, remote.LastPose));
    }

    foreach (var connectionId in recipientIds)
    {
      if (!_peers.TryGetValue(connectionId, out var recipient) || !recipient.Ready || !recipient.SceneReady) continue;
      Send(connectionId, MessageKind.PlayerRoster, rosterPayload, DeliveryMode.Reliable);
      if (_rosterRevision != revision) return;
      if (_lastLocalPose.HasValue)
      {
        Send(connectionId, MessageKind.WorldPlayerPose, PlayerPosePayload.EncodeWorldPose(_localPeerId, _lastLocalPose.Value), DeliveryMode.Reliable);
        if (_rosterRevision != revision) return;
      }

      foreach (var remote in remotePoses)
      {
        if (_peers.TryGetValue(connectionId, out recipient) && recipient.Ready && recipient.SceneReady)
          Send(connectionId, MessageKind.WorldPlayerPose, PlayerPosePayload.EncodeWorldPose(remote.PeerId, remote.Pose), DeliveryMode.Reliable);
        if (_rosterRevision != revision) return;
      }
    }
  }

  private void HandleWelcome(long connectionId, PeerState state, ProtocolMessage message)
  {
    if (_isHost || message.Payload.Length != 1 || !state.ReceivedHello)
    {
      _transport?.Disconnect(connectionId, "Unexpected or malformed protocol welcome.");
      return;
    }

    if (message.Payload[0] != 1)
    {
      _transport?.Disconnect(connectionId, "Host rejected the protocol handshake.");
      SetState(SessionState.Error, "Host rejected the protocol handshake.");
      return;
    }

    state.Ready = true;
    _clientSceneReady = false;
    _clientSceneSyncDeadline = Now + SceneSynchronizationTimeoutSeconds;
    _clientRosterReceived = false;
    _nextHeartbeat = Now + HeartbeatIntervalSeconds;
    SetState(SessionState.Connected, $"Connected to host peer {state.RemotePeerId}. Waiting for world synchronization.");
    Plugin.Log.LogInfo($"Direct IP protocol handshake completed with peer {state.RemotePeerId}.");
  }

  private bool HasPeerIdentity(ulong peerId, long exceptConnection)
  {
    foreach (var pair in _peers)
    {
      if (pair.Key != exceptConnection && pair.Value.RemotePeerId == peerId)
      {
        return true;
      }
    }

    return peerId == _localPeerId;
  }

  private void OnPeerDisconnected(TransportPeer peer, string reason)
  {
    _rosterRevision++;
    var hadState = _peers.TryGetValue(peer.ConnectionId, out var state);
    _peers.Remove(peer.ConnectionId);
    if (_isHost)
    {
      Plugin.Log.LogInfo($"Session peer on connection {peer.ConnectionId} left: {reason}");
      if (hadState && state!.Ready)
      {
        RefreshRosterAndSnapshots();
      }

      return;
    }

    if (_state != SessionState.Offline && _state != SessionState.Error)
    {
      _pendingError = $"Host connection ended: {reason}";
    }
  }

  private void Send(long connectionId, MessageKind kind, byte[] payload, DeliveryMode deliveryMode)
  {
    if (_transport == null || _localPeerId == 0)
    {
      return;
    }

    _sendSequence++;
    if (_sendSequence == 0)
    {
      _sendSequence = 1;
    }

    var message = new ProtocolMessage(ProtocolCodec.CurrentVersion, kind, _localPeerId, _sendSequence, payload);
    try
    {
      var packet = ProtocolCodec.Encode(message);
      if (!_transport.Send(connectionId, packet, deliveryMode, out var error))
      {
        Plugin.Log.LogWarning(error);
      }
    }
    catch (Exception exception)
    {
      Plugin.Log.LogError($"Could not encode {kind} message: {exception}");
      _transport.Disconnect(connectionId, "Local protocol encoding failed.");
    }
  }

  private void SetError(string error)
  {
    Plugin.Log.LogError(error);
    ResetSession(error);
    SetState(SessionState.Error, error);
  }

  private void ResetSession(string reason)
  {
    DisposeTransport();
    _peers.Clear();
    _knownPeerIds.Clear();
    _hostScene = string.Empty;
    _expectedHostScene = string.Empty;
    _clientSceneSyncDeadline = 0;
    _clientSceneReady = false;
    _clientWorldReady = false;
    _clientRosterReceived = false;
    _pendingError = null;
    _isHost = false;
    _operationDeadline = 0;
    _listenPort = 0;
    _nextHeartbeat = 0;
    _sendSequence = 0;
    _lastLocalPose = null;
    _localPeerId = CreatePeerId();
    RosterChanged?.Invoke(Array.Empty<ulong>());
    SetState(SessionState.Offline, reason);
  }

  private void DisposeTransport()
  {
    if (_transport == null)
    {
      return;
    }

    _transport.PeerConnected -= OnPeerConnected;
    _transport.PacketReceived -= OnPacketReceived;
    _transport.PeerDisconnected -= OnPeerDisconnected;
    _transport.Dispose();
    _transport = null;
  }

  private void SetState(SessionState state, string status)
  {
    if (_state != state)
    {
      Plugin.Log.LogInfo($"Session state: {_state} -> {state}. {status}");
    }

    _state = state;
    _status = status;
  }

  private sealed class PeerState
  {
    internal PeerState(double now)
    {
      LastReceiveTime = now;
    }

    internal ulong RemotePeerId;
    internal bool ReceivedHello;
    internal bool Ready;
    internal bool SceneReady;
    internal double SceneSyncDeadline;
    internal uint LastReceivedSequence;
    internal double LastReceiveTime;
    internal double DisconnectAt;
    internal string? DisconnectReason;
    internal bool HasPose;
    internal PlayerPoseData LastPose;
    internal double LastPoseReceiveTime;
  }

  private void BeginHostScene(string sceneName)
  {
    _hostScene = sceneName;
    _rosterRevision++;
    _knownPeerIds.Clear();
    _knownPeerIds.Add(_localPeerId);
    var recipients = new List<long>();
    foreach (var pair in _peers)
    {
      if (pair.Value.Ready)
      {
        pair.Value.SceneReady = false;
        pair.Value.SceneSyncDeadline = Now + SceneSynchronizationTimeoutSeconds;
        recipients.Add(pair.Key);
      }
    }

    foreach (var connectionId in recipients)
    {
      if (_peers.TryGetValue(connectionId, out var peer) && peer.Ready)
      {
        Send(connectionId, MessageKind.HostScene, SceneNamePayload.Encode(sceneName), DeliveryMode.Reliable);
      }
    }

    Plugin.Log.LogInfo($"Host scene is now '{sceneName}'. Waiting for connected clients to load it.");
    RefreshRosterAndSnapshots();
  }

  private readonly struct RemotePoseSnapshot
  {
    internal RemotePoseSnapshot(ulong peerId, PlayerPoseData pose) { PeerId = peerId; Pose = pose; }
    internal ulong PeerId { get; }
    internal PlayerPoseData Pose { get; }
  }
}
