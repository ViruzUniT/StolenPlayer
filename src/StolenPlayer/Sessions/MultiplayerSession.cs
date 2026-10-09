using System;
using System.Collections.Generic;
using System.Net;
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
  private const double UdpPathTimeoutSeconds = 3.0;
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
  private readonly Dictionary<ulong, PlayerPoseData> _pendingWorldPoses = new Dictionary<ulong, PlayerPoseData>();
  private TcpTransport? _transport;
  private UdpPoseTransport? _udpTransport;
  private SessionState _state;
  private double _operationDeadline;
  private double _nextHeartbeat;
  private uint _sendSequence;
  private uint _udpSendSequence;
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
  private bool _clientUdpPathReady;
  private bool _clientUdpPathPending;
  private uint _lastHostUdpSequence;
  private double _lastHostUdpReceiveTime;

  internal event Action<PlayerIdentityData[]>? RosterChanged;
  internal event Action<ulong, PlayerPoseData>? PlayerPoseReceived;
  internal event Action? SceneReadinessChanged;
  internal event Action<string>? HostSceneReceived;
  internal event Action<PlayerPoseData>? HostPoseReceived;

  internal MultiplayerSession(PluginConfig config, string gameVersion, string pluginVersion)
  {
    _config = config;
    _gameVersion = string.IsNullOrEmpty(gameVersion) ? "unknown" : gameVersion;
    _pluginVersion = pluginVersion;
    if (!HandshakePayload.TryNormalizePlayerName(config.PlayerName.Value, out var playerName))
    {
      playerName = "Player";
      Plugin.Log.LogWarning("Configured player name was invalid; using 'Player' for this session. Set a name in F8.");
    }

    LocalPlayerName = playerName;
    _localPeerId = CreatePeerId();
  }

  internal SessionState State => _state;
  internal string Status => _status;
  internal bool IsHost => _isHost;
  internal ulong LocalPeerId => _localPeerId;
  internal string LocalPlayerName { get; private set; }
  internal PlayerIdentityData[] CurrentRoster { get; private set; } = Array.Empty<PlayerIdentityData>();
  internal int ListenPort => _listenPort;
  internal string ExpectedHostScene => _expectedHostScene;
  internal bool IsClientSceneReady => _clientSceneReady;
  internal bool IsClientWorldReady => _clientWorldReady;
  internal bool HasClientRoster => _clientRosterReceived;
  internal bool CanMaterializeRemotePlayers(string sceneName)
  {
    if (string.IsNullOrEmpty(sceneName)) return false;
    return _isHost
      ? _lastLocalPose.HasValue && string.Equals(_hostScene, sceneName, StringComparison.Ordinal)
          && string.Equals(_lastLocalPose.Value.SceneName, sceneName, StringComparison.Ordinal)
      : _clientWorldReady && _clientSceneReady && string.Equals(_expectedHostScene, sceneName, StringComparison.Ordinal);
  }

  internal void UpdateLocalPlayerName(string playerName)
  {
    if (_state != SessionState.Offline && _state != SessionState.Error) return;
    if (!HandshakePayload.TryNormalizePlayerName(playerName, out var normalizedName)) return;
    LocalPlayerName = normalizedName;
  }
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
    {
      SendClientPoseUdp(pose);
      if (!_clientUdpPathReady)
      {
        Send(connectionId, MessageKind.ClientPlayerPose, PlayerPosePayload.Encode(pose), DeliveryMode.Reliable);
      }
    }
  }

  internal void Host(int port)
  {
    if (_disposed || _state != SessionState.Offline)
    {
      return;
    }

    EnsureTransport();
    _isHost = true;
    SetState(SessionState.StartingHost, $"Opening TCP and UDP port {port}...");
    if (!_udpTransport!.StartHost(port, out var udpError))
    {
      Plugin.Log.LogWarning($"UDP movement unavailable; session will use TCP pose fallback: {udpError}");
    }
    else
    {
      Plugin.Log.LogInfo($"UDP movement listener opened on port {port}.");
    }

    var playerLimit = Math.Min(_config.MaxPlayers.Value, MaximumSupportedPlayers);
    if (!_transport!.StartListening(port, playerLimit - 1, out var error))
    {
      SetError(error);
      return;
    }

    _listenPort = port;
    SetState(SessionState.Listening, $"Listening for direct IP clients on TCP and UDP port {port}. Share your IP address and port.");
    Plugin.Log.LogInfo($"Direct IP host listening on TCP and UDP port {port}; capacity {playerLimit} total players.");
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
    if (!_udpTransport!.StartClient(address, port, out var udpError))
    {
      Plugin.Log.LogWarning($"UDP movement unavailable; session will use TCP pose fallback: {udpError}");
    }
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
    _udpTransport?.Update();
    var now = Now;
    if (!_isHost && _clientUdpPathReady && now - _lastHostUdpReceiveTime > UdpPathTimeoutSeconds)
    {
      _clientUdpPathReady = false;
      _clientUdpPathPending = false;
      SendHostControl(MessageKind.UdpPathReady, new byte[] { 0 });
      Plugin.Log.LogWarning("UDP host-pose path timed out; temporarily returning to TCP pose fallback.");
    }
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

      if (_isHost && peer.UdpPathReady && now - peer.LastUdpReceiveTime > UdpPathTimeoutSeconds)
      {
        peer.UdpPathReady = false;
        Send(peerId, MessageKind.UdpPathReady, new byte[] { 0 }, DeliveryMode.Reliable);
        Plugin.Log.LogWarning($"UDP movement path to peer {peer.RemotePeerId} timed out; returning to TCP pose fallback.");
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
    _udpTransport = new UdpPoseTransport(
      message => Plugin.Log.LogWarning(message),
      message => Plugin.Log.LogError(message));
    _udpTransport.DatagramReceived += OnUdpDatagramReceived;
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

    Send(peer.ConnectionId, MessageKind.Hello,
      HandshakePayload.EncodeHello(_gameVersion, _pluginVersion, LocalPlayerName), DeliveryMode.Reliable);
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
      case MessageKind.UdpPathReady:
        HandleUdpPathReady(peer.ConnectionId, state, message);
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
    if (state.ReceivedHello || !HandshakePayload.TryDecodeHello(message.Payload,
        out var gameVersion, out var pluginVersion, out var playerName))
    {
      _transport?.Disconnect(connectionId, "Malformed or duplicate protocol hello.");
      return;
    }

    if (!string.Equals(gameVersion, _gameVersion, StringComparison.Ordinal)
        || !string.Equals(pluginVersion, _pluginVersion, StringComparison.Ordinal))
    {
      Send(connectionId, MessageKind.Welcome, WelcomePayload.EncodeRejected(
        $"Peer game/plugin version {gameVersion}/{pluginVersion} is incompatible with {_gameVersion}/{_pluginVersion}."), DeliveryMode.Reliable);
      state.DisconnectAt = Now + RejectedPeerCloseDelaySeconds;
      state.DisconnectReason = $"Peer game/plugin version {gameVersion}/{pluginVersion} is incompatible with {_gameVersion}/{_pluginVersion}.";
      return;
    }

    if (_isHost && (string.Equals(playerName, LocalPlayerName, StringComparison.OrdinalIgnoreCase)
        || HasPeerName(playerName, connectionId)))
    {
      Plugin.Log.LogWarning($"Rejected peer {state.RemotePeerId}: player name '{playerName}' is already in use.");
      Send(connectionId, MessageKind.Welcome, WelcomePayload.EncodeRejected(
        $"Player name '{playerName}' is already in use in this session."), DeliveryMode.Reliable);
      state.DisconnectAt = Now + RejectedPeerCloseDelaySeconds;
      state.DisconnectReason = $"Player name '{playerName}' is already in use in this session.";
      return;
    }

    state.ReceivedHello = true;
    state.RemotePlayerName = playerName;
    if (_isHost)
    {
      state.Ready = true;
      state.UdpToken = CreateSessionToken();
      state.SceneSyncDeadline = Now + SceneSynchronizationTimeoutSeconds;
      Send(connectionId, MessageKind.Welcome, WelcomePayload.EncodeAccepted(state.UdpToken), DeliveryMode.Reliable);
      Plugin.Log.LogInfo($"TCP peer {state.RemotePeerId} ({state.RemotePlayerName}) passed the protocol handshake.");
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

    if (state.UdpPathReady)
    {
      return;
    }

    AcceptClientPose(state, pose);
  }

  private void AcceptClientPose(PeerState state, PlayerPoseData pose)
  {
    if (!state.Ready || !IsPlausiblePose(pose) || _lastLocalPose == null
        || !string.Equals(pose.SceneName, _lastLocalPose.Value.SceneName, StringComparison.Ordinal)) return;

    if (!state.SceneReady)
    {
      if (!state.HasPendingPose && _config.VerboseNetworking.Value)
        Plugin.Log.LogDebug($"Queued first scene-pending pose for peer {state.RemotePeerId} in '{pose.SceneName}'.");
      state.PendingPose = pose;
      state.HasPendingPose = true;
      return;
    }

    var now = Now;
    if (now - state.LastPoseReceiveTime < MinimumPoseIntervalSeconds) return;

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
    if (state.UdpEndPoint != null)
    {
      state.LastUdpReceiveTime = now;
    }

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

    if (_clientUdpPathReady)
    {
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
      _pendingWorldPoses[peerId] = pose;
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
    if (_isHost || !state.Ready || !PlayerRosterPayload.TryDecode(message.Payload, out var players))
    {
      _transport?.Disconnect(connectionId, "Unexpected or malformed player roster.");
      return;
    }

    var containsLocal = false;
    var containsHost = false;
    foreach (var player in players)
    {
      containsLocal |= player.PeerId == _localPeerId && string.Equals(player.PlayerName, LocalPlayerName, StringComparison.Ordinal);
      containsHost |= player.PeerId == state.RemotePeerId
        && string.Equals(player.PlayerName, state.RemotePlayerName, StringComparison.Ordinal);
    }

    if (!containsLocal || !containsHost)
    {
      _transport?.Disconnect(connectionId, "Host roster did not contain the local player and host.");
      return;
    }

    _knownPeerIds.Clear();
    foreach (var player in players)
    {
      _knownPeerIds.Add(player.PeerId);
    }

    RemovePendingWorldPosesOutsideRoster();

    _clientRosterReceived = true;
    CurrentRoster = players;
    SetState(SessionState.Connected, "Host scene loaded. Applying host spawn position and player roster.");
    RosterChanged?.Invoke(players);
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
    _pendingWorldPoses.Clear();
    _knownPeerIds.Clear();
    CurrentRoster = Array.Empty<PlayerIdentityData>();
    RosterChanged?.Invoke(CurrentRoster);
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
    // A previous scene's last pose is not a valid spawn snapshot for this scene.
    state.HasPose = false;
    state.LastPoseReceiveTime = 0;
    var hasPendingPose = state.HasPendingPose;
    var pendingPose = state.PendingPose;
    state.HasPendingPose = false;
    Plugin.Log.LogInfo($"Peer {state.RemotePeerId} loaded host scene '{sceneName}'. Sending its initial player snapshot.");
    RefreshRosterAndSnapshots();
    if (hasPendingPose) AcceptClientPose(state, pendingPose);
    SceneReadinessChanged?.Invoke();
  }

  private void HandleUdpPathReady(long connectionId, PeerState state, ProtocolMessage message)
  {
    if (!state.Ready)
    {
      _transport?.Disconnect(connectionId, "Unexpected UDP path acknowledgement.");
      return;
    }

    if (_isHost)
    {
      if (message.Payload.Length == 1 && message.Payload[0] == 0)
      {
        state.UdpPathReady = false;
        Send(connectionId, MessageKind.UdpPathReady, new byte[] { 0 }, DeliveryMode.Reliable);
        Plugin.Log.LogWarning($"Peer {state.RemotePeerId} reported UDP movement loss; returning to TCP pose fallback.");
        return;
      }

      if (message.Payload.Length != 0 || state.UdpEndPoint == null)
      {
        _transport?.Disconnect(connectionId, "Unexpected UDP path acknowledgement.");
        return;
      }

      if (!state.UdpPathReady)
      {
        state.UdpPathReady = true;
        state.LastUdpReceiveTime = Now;
        Send(connectionId, MessageKind.UdpPathReady, new byte[] { 1 }, DeliveryMode.Reliable);
        Plugin.Log.LogInfo($"UDP movement path established with peer {state.RemotePeerId}.");
      }

      return;
    }

    if (message.Payload.Length == 1 && message.Payload[0] == 1)
    {
      _clientUdpPathPending = false;
      if (!_clientUdpPathReady)
      {
        _clientUdpPathReady = true;
        _lastHostUdpReceiveTime = Now;
        Plugin.Log.LogInfo("UDP movement path confirmed by the host.");
      }
    }
    else if (message.Payload.Length == 1 && message.Payload[0] == 0)
    {
      _clientUdpPathReady = false;
      _clientUdpPathPending = false;
      Plugin.Log.LogWarning("Host reported UDP movement loss; returning to TCP pose fallback.");
    }
    else
    {
      _transport?.Disconnect(connectionId, "Malformed UDP path control message.");
    }
  }

  private void OnUdpDatagramReceived(IPEndPoint endpoint, byte[] packet)
  {
    if (!UdpPoseDatagramCodec.TryDecode(packet, out var datagram))
    {
      if (_config.VerboseNetworking.Value)
      {
        Plugin.Log.LogDebug("Ignored malformed UDP movement datagram.");
      }

      return;
    }

    if (_isHost)
    {
      HandleClientPoseDatagram(endpoint, datagram);
    }
    else
    {
      HandleWorldPoseDatagram(datagram);
    }
  }

  private void HandleClientPoseDatagram(IPEndPoint endpoint, UdpPoseDatagram datagram)
  {
    if (datagram.Kind != UdpPoseKind.ClientPose || !PlayerPosePayload.TryDecode(datagram.Payload, out var pose))
    {
      return;
    }

    PeerState? state = null;
    foreach (var peer in _peers.Values)
    {
      if (peer.RemotePeerId == datagram.SenderPeerId)
      {
        state = peer;
        break;
      }
    }

    if (state == null || !state.Ready || !TokensEqual(state.UdpToken, datagram.Token)
        || !ProtocolCodec.IsSequenceNewer(datagram.Sequence, state.LastUdpSequence))
    {
      return;
    }

    if (!IsPlausiblePose(pose) || _lastLocalPose == null
        || !string.Equals(pose.SceneName, _lastLocalPose.Value.SceneName, StringComparison.Ordinal))
    {
      return;
    }

    state.LastUdpSequence = datagram.Sequence;
    state.UdpEndPoint = endpoint;
    AcceptClientPose(state, pose);
  }

  private void HandleWorldPoseDatagram(UdpPoseDatagram datagram)
  {
    PeerState? host = null;
    long connectionId = 0;
    foreach (var peer in _peers)
    {
      if (peer.Value.Ready && peer.Value.RemotePeerId == datagram.SenderPeerId)
      {
        host = peer.Value;
        connectionId = peer.Key;
        break;
      }
    }

    if (datagram.Kind != UdpPoseKind.WorldPose || host == null || !TokensEqual(host.UdpToken, datagram.Token)
        || !ProtocolCodec.IsSequenceNewer(datagram.Sequence, _lastHostUdpSequence)
        || !PlayerPosePayload.TryDecodeWorldPose(datagram.Payload, out var peerId, out var pose)
        || !IsPlausiblePose(pose) || !_clientSceneReady
        || _lastLocalPose == null || !string.Equals(pose.SceneName, _lastLocalPose.Value.SceneName, StringComparison.Ordinal)
        || !IsInCurrentRoster(peerId))
    {
      return;
    }

    _lastHostUdpSequence = datagram.Sequence;
    _lastHostUdpReceiveTime = Now;
    if (!_clientUdpPathReady && !_clientUdpPathPending)
    {
      _clientUdpPathPending = true;
      Send(connectionId, MessageKind.UdpPathReady, Array.Empty<byte>(), DeliveryMode.Reliable);
    }

    if (!_clientWorldReady)
    {
      _pendingWorldPoses[peerId] = pose;
      if (peerId == host.RemotePeerId) HostPoseReceived?.Invoke(pose);
      return;
    }

    if (peerId != _localPeerId)
    {
      PlayerPoseReceived?.Invoke(peerId, pose);
    }
  }

  private void SendClientPoseUdp(PlayerPoseData pose)
  {
    if (_udpTransport == null || !_udpTransport.IsRunning)
    {
      return;
    }

    foreach (var peer in _peers.Values)
    {
      if (peer.Ready && peer.UdpToken != null)
      {
        SendUdp(null, UdpPoseKind.ClientPose, _localPeerId, peer.UdpToken, PlayerPosePayload.Encode(pose));
        return;
      }
    }
  }

  private void SendHostControl(MessageKind kind, byte[] payload)
  {
    if (_isHost) return;
    foreach (var peer in _peers)
    {
      if (peer.Value.Ready)
      {
        Send(peer.Key, kind, payload, DeliveryMode.Reliable);
        return;
      }
    }
  }

  private bool SendUdp(IPEndPoint? endpoint, UdpPoseKind kind, ulong senderPeerId, byte[]? token, byte[] payload)
  {
    if (_udpTransport == null || !_udpTransport.IsRunning || token == null)
    {
      return false;
    }

    _udpSendSequence++;
    if (_udpSendSequence == 0) _udpSendSequence = 1;
    try
    {
      var packet = UdpPoseDatagramCodec.Encode(kind, senderPeerId, _udpSendSequence, token, payload);
      return _udpTransport.Send(packet, endpoint);
    }
    catch (Exception exception)
    {
      Plugin.Log.LogWarning($"Could not encode UDP movement datagram: {exception.Message}");
      return false;
    }
  }

  private static bool TokensEqual(byte[]? expected, byte[] received)
  {
    if (expected == null || received == null || expected.Length != received.Length)
    {
      return false;
    }

    var difference = 0;
    for (var index = 0; index < expected.Length; index++) difference |= expected[index] ^ received[index];
    return difference == 0;
  }

  private static byte[] CreateSessionToken()
  {
    var token = new byte[16];
    using (var random = RandomNumberGenerator.Create()) random.GetBytes(token);
    return token;
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
    SceneReadinessChanged?.Invoke();
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
    foreach (var pair in _pendingWorldPoses)
    {
      if (pair.Key != _localPeerId && _knownPeerIds.Contains(pair.Key)
          && string.Equals(pair.Value.SceneName, _expectedHostScene, StringComparison.Ordinal))
      {
        PlayerPoseReceived?.Invoke(pair.Key, pair.Value);
      }
    }
    _pendingWorldPoses.Clear();
    SceneReadinessChanged?.Invoke();
    return true;
  }

  private void RemovePendingWorldPosesOutsideRoster()
  {
    var stale = new List<ulong>();
    foreach (var peerId in _pendingWorldPoses.Keys)
    {
      if (!_knownPeerIds.Contains(peerId)) stale.Add(peerId);
    }

    foreach (var peerId in stale) _pendingWorldPoses.Remove(peerId);
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
      {
        var worldPayload = PlayerPosePayload.EncodeWorldPose(peerId, pose);
        var sentUdp = peer.UdpEndPoint != null
          && SendUdp(peer.UdpEndPoint, UdpPoseKind.WorldPose, _localPeerId, peer.UdpToken, worldPayload);
        if (!peer.UdpPathReady || !sentUdp)
        {
          Send(connectionId, MessageKind.WorldPlayerPose, worldPayload, DeliveryMode.Reliable);
        }
      }

      if (_rosterRevision != revision) return;
    }
  }

  private void RefreshRosterAndSnapshots()
  {
    var revision = _rosterRevision;
    var players = new List<PlayerIdentityData> { new PlayerIdentityData(_localPeerId, LocalPlayerName) };
    foreach (var peer in _peers.Values)
    {
      if (peer.Ready && peer.SceneReady)
      {
        players.Add(new PlayerIdentityData(peer.RemotePeerId, peer.RemotePlayerName));
      }
    }

    var roster = players.ToArray();
    CurrentRoster = roster;
    _knownPeerIds.Clear();
    _pendingWorldPoses.Clear();
    foreach (var player in roster)
    {
      _knownPeerIds.Add(player.PeerId);
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
    if (_isHost || !state.ReceivedHello
        || !WelcomePayload.TryDecode(message.Payload, out var accepted, out var token, out var rejectionReason))
    {
      _transport?.Disconnect(connectionId, "Unexpected or malformed protocol welcome.");
      return;
    }

    if (!accepted)
    {
      _transport?.Disconnect(connectionId, "Host rejected the protocol handshake.");
      SetState(SessionState.Error, string.IsNullOrWhiteSpace(rejectionReason)
        ? "Host rejected the protocol handshake."
        : rejectionReason);
      return;
    }

    state.Ready = true;
    state.UdpToken = token;
    _clientSceneReady = false;
    _clientUdpPathReady = false;
    _clientUdpPathPending = false;
    _clientUdpPathPending = false;
    _lastHostUdpSequence = 0;
    _clientSceneSyncDeadline = Now + SceneSynchronizationTimeoutSeconds;
    _clientRosterReceived = false;
    _nextHeartbeat = Now + HeartbeatIntervalSeconds;
    SetState(SessionState.Connected, $"Connected to host {state.RemotePlayerName}. Waiting for world synchronization.");
    Plugin.Log.LogInfo($"Direct IP protocol handshake completed with host {state.RemotePlayerName} ({state.RemotePeerId}).");
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

  private bool HasPeerName(string playerName, long exceptConnection)
  {
    foreach (var pair in _peers)
    {
      if (pair.Key != exceptConnection && pair.Value.RemotePeerId != 0
          && string.Equals(pair.Value.RemotePlayerName, playerName, StringComparison.OrdinalIgnoreCase)) return true;
    }

    return false;
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
    _udpSendSequence = 0;
    _lastLocalPose = null;
    _clientUdpPathReady = false;
    _lastHostUdpSequence = 0;
    _lastHostUdpReceiveTime = 0;
    _localPeerId = CreatePeerId();
    CurrentRoster = Array.Empty<PlayerIdentityData>();
    RosterChanged?.Invoke(CurrentRoster);
    SceneReadinessChanged?.Invoke();
    SetState(SessionState.Offline, reason);
  }

  private void DisposeTransport()
  {
    if (_transport != null)
    {
      _transport.PeerConnected -= OnPeerConnected;
      _transport.PacketReceived -= OnPacketReceived;
      _transport.PeerDisconnected -= OnPeerDisconnected;
      _transport.Dispose();
      _transport = null;
    }

    if (_udpTransport != null)
    {
      _udpTransport.DatagramReceived -= OnUdpDatagramReceived;
      _udpTransport.Dispose();
      _udpTransport = null;
    }
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
    internal string RemotePlayerName = string.Empty;
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
    internal bool HasPendingPose;
    internal PlayerPoseData PendingPose;
    internal double LastPoseReceiveTime;
    internal byte[]? UdpToken;
    internal IPEndPoint? UdpEndPoint;
    internal bool UdpPathReady;
    internal uint LastUdpSequence;
    internal double LastUdpReceiveTime;
  }

  private void BeginHostScene(string sceneName)
  {
    _hostScene = sceneName;
    _rosterRevision++;
    _pendingWorldPoses.Clear();
    _knownPeerIds.Clear();
    _knownPeerIds.Add(_localPeerId);
    var recipients = new List<long>();
    foreach (var pair in _peers)
    {
      if (pair.Value.Ready)
      {
        pair.Value.SceneReady = false;
        pair.Value.HasPose = false;
        pair.Value.HasPendingPose = false;
        pair.Value.LastPoseReceiveTime = 0;
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
    SceneReadinessChanged?.Invoke();
  }

  internal void NotifyLocalSceneChanged()
  {
    if (!_isHost) return;
    _hostScene = string.Empty;
    _lastLocalPose = null;
    _pendingWorldPoses.Clear();
    foreach (var peer in _peers.Values)
    {
      if (!peer.Ready) continue;
      peer.SceneReady = false;
      peer.HasPose = false;
      peer.HasPendingPose = false;
      peer.LastPoseReceiveTime = 0;
    }

    SceneReadinessChanged?.Invoke();
  }

  private readonly struct RemotePoseSnapshot
  {
    internal RemotePoseSnapshot(ulong peerId, PlayerPoseData pose) { PeerId = peerId; Pose = pose; }
    internal ulong PeerId { get; }
    internal PlayerPoseData Pose { get; }
  }
}
