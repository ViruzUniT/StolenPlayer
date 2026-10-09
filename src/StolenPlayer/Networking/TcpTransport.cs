using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using StolenPlayer.Protocol;

namespace StolenPlayer.Networking;

/// <summary>Length-prefixed reliable TCP transport. All socket polling and events are main-thread driven.</summary>
internal sealed class TcpTransport : ITransport
{
  private const int PrefixLength = sizeof(int);
  private const int MaximumFrameLength = ProtocolCodec.MaximumMessageSize;
  // A late-join door snapshot can contain 110 individually framed entries.
  // Keep the byte cap as the primary flood bound, but leave enough packet
  // headroom for the snapshot plus scene/roster traffic in the same second.
  private const int MaximumMessagesPerPeerPerSecond = 512;
  private const int MaximumBytesPerPeerPerSecond = 1024 * 1024;
  private const int MaximumQueuedBytesPerPeer = 1024 * 1024;
  private const int MaximumAcceptsPerUpdate = 8;
  private const int MaximumFramesPerUpdate = 64;
  private const int MaximumIoBytesPerUpdate = 256 * 1024;

  private readonly Dictionary<long, PeerConnection> _peers = new Dictionary<long, PeerConnection>();
  private readonly List<long> _peerScratch = new List<long>();
  private readonly Stopwatch _clock = Stopwatch.StartNew();
  private readonly bool _verbose;
  private readonly Action<string> _infoLog;
  private readonly Action<string> _warningLog;
  private readonly Action<string> _errorLog;
  private TcpListener? _listener;
  private long _nextConnectionId;
  private int _maximumPeers;
  private bool _disposed;

  internal TcpTransport(bool verbose, Action<string> infoLog, Action<string> warningLog, Action<string> errorLog)
  {
    _verbose = verbose;
    _infoLog = infoLog;
    _warningLog = warningLog;
    _errorLog = errorLog;
  }

  public event Action<TransportPeer>? PeerConnected;
  public event Action<TransportPeer, byte[]>? PacketReceived;
  public event Action<TransportPeer, string>? PeerDisconnected;

  public bool StartListening(int port, int maximumPeers, out string error)
  {
    error = string.Empty;
    if (_disposed || _listener != null)
    {
      error = "Transport is disposed or already listening.";
      return false;
    }

    if (port < 1 || port > 65535 || maximumPeers < 1)
    {
      error = "The listen port or peer limit is invalid.";
      return false;
    }

    try
    {
      _maximumPeers = maximumPeers;
      _listener = new TcpListener(IPAddress.Any, port);
      _listener.Start(maximumPeers);
      _listener.Server.Blocking = false;
      return true;
    }
    catch (Exception exception)
    {
      _listener?.Stop();
      _listener = null;
      error = $"Could not listen on TCP port {port}: {exception.Message}";
      return false;
    }
  }

  public bool Connect(string address, int port, out string error)
  {
    error = string.Empty;
    if (_disposed || string.IsNullOrWhiteSpace(address) || port < 1 || port > 65535)
    {
      error = "Transport is disposed or the host address/port is invalid.";
      return false;
    }

    if (!IPAddress.TryParse(address.Trim(), out var remoteAddress)
        || remoteAddress.AddressFamily != AddressFamily.InterNetwork)
    {
      error = "Enter a numeric IPv4 address (for example 192.168.1.25).";
      return false;
    }

    var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
    client.SendTimeout = 1000;
    var connectionId = NextConnectionId();
    try
    {
      var pending = client.BeginConnect(remoteAddress, port, null, null);
      var peer = new PeerConnection(connectionId, client, pending);
      _peers.Add(connectionId, peer);
      LogDebug($"Connecting to {remoteAddress}:{port}...");
      return true;
    }
    catch (Exception exception)
    {
      client.Close();
      error = $"Could not start a connection to {address}:{port}: {exception.Message}";
      return false;
    }
  }

  public bool Send(long connectionId, byte[] packet, DeliveryMode deliveryMode, out string error)
  {
    error = string.Empty;
    if (_disposed || packet == null || packet.Length == 0 || packet.Length > MaximumFrameLength)
    {
      error = "Transport is disposed or outgoing packet size is invalid.";
      return false;
    }

    if (!_peers.TryGetValue(connectionId, out var peer) || !peer.Connected)
    {
      error = $"No connected TCP peer exists for connection {connectionId}.";
      return false;
    }

    var frame = new byte[PrefixLength + packet.Length];
    WriteInt32(frame, 0, packet.Length);
    Array.Copy(packet, 0, frame, PrefixLength, packet.Length);
    if (frame.Length > MaximumQueuedBytesPerPeer - peer.PendingBytes)
    {
      error = $"TCP send queue for {peer.RemoteEndPoint} is full.";
      Disconnect(connectionId, error);
      return false;
    }

    peer.PendingWrites.Enqueue(frame);
    peer.PendingBytes += frame.Length;
    return true;
  }

  public void Disconnect(long connectionId, string reason)
  {
    if (!_peers.TryGetValue(connectionId, out var peer))
    {
      return;
    }

    RemovePeer(peer);
    RaiseDisconnected(peer, reason);
  }

  internal void Update()
  {
    if (_disposed)
    {
      return;
    }

    AcceptPendingConnections();
    var frameBudget = MaximumFramesPerUpdate;
    var readByteBudget = MaximumIoBytesPerUpdate;
    var writeByteBudget = MaximumIoBytesPerUpdate;
    _peerScratch.Clear();
    foreach (var connectionId in _peers.Keys)
    {
      _peerScratch.Add(connectionId);
    }

    foreach (var connectionId in _peerScratch)
    {
      if (!_peers.TryGetValue(connectionId, out var peer))
      {
        continue;
      }

      if (!peer.Connected)
      {
        CompleteConnect(peer);
      }

      if (peer.Connected && _peers.ContainsKey(connectionId))
      {
        if (!FlushWrites(peer, ref writeByteBudget))
        {
          continue;
        }

        if (peer.Client.Client.Poll(0, SelectMode.SelectRead) && peer.Client.Client.Available == 0)
        {
          Disconnect(connectionId, "Remote peer closed the TCP connection.");
          continue;
        }

        ReadAvailable(peer, ref frameBudget, ref readByteBudget);
      }
    }
  }

  public void Dispose()
  {
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    _listener?.Stop();
    _listener = null;
    var ids = new List<long>(_peers.Keys);
    foreach (var id in ids)
    {
      Disconnect(id, "Transport stopped.");
    }
  }

  private void AcceptPendingConnections()
  {
    if (_listener == null)
    {
      return;
    }

    var acceptedCount = 0;
    while (acceptedCount < MaximumAcceptsPerUpdate && _listener.Pending())
    {
      TcpClient client;
      try
      {
        client = _listener.AcceptTcpClient();
      }
      catch (Exception exception)
      {
        if (exception is SocketException socketException && socketException.SocketErrorCode == SocketError.WouldBlock)
        {
          return;
        }

        LogWarning($"Could not accept incoming TCP connection: {exception.Message}");
        return;
      }

      if (_peers.Count >= _maximumPeers)
      {
        client.Close();
        continue;
      }

      client.NoDelay = true;
      client.Client.Blocking = false;
      acceptedCount++;
      var peer = new PeerConnection(NextConnectionId(), client, null);
      try
      {
        MarkConnected(peer);
        _peers.Add(peer.ConnectionId, peer);
        LogInfo($"Accepted TCP peer {peer.RemoteEndPoint}.");
        RaiseConnected(peer);
      }
      catch (Exception exception)
      {
        client.Close();
        LogWarning($"Could not initialize accepted TCP peer: {exception.Message}");
      }
    }
  }

  private void CompleteConnect(PeerConnection peer)
  {
    if (peer.ConnectResult == null || !peer.ConnectResult.IsCompleted)
    {
      return;
    }

    try
    {
      peer.Client.EndConnect(peer.ConnectResult);
      MarkConnected(peer);
      LogInfo($"Connected to TCP peer {peer.RemoteEndPoint}.");
      RaiseConnected(peer);
    }
    catch (Exception exception)
    {
      Disconnect(peer.ConnectionId, $"TCP connection failed: {exception.Message}");
    }
  }

  private void MarkConnected(PeerConnection peer)
  {
    peer.Client.Client.Blocking = false;
    peer.SetRemoteEndPoint(peer.Client.Client.RemoteEndPoint?.ToString() ?? "unknown endpoint");
    peer.Connected = true;
    peer.WindowStarted = Now;
  }

  private void ReadAvailable(PeerConnection peer, ref int frameBudget, ref int byteBudget)
  {
    try
    {
      if (!ParseFrames(peer, ref frameBudget) || !_peers.ContainsKey(peer.ConnectionId))
      {
        return;
      }

      while (peer.Client.Client.Available > 0 && frameBudget > 0 && byteBudget > 0)
      {
        if (peer.BufferCount == peer.Buffer.Length)
        {
          Disconnect(peer.ConnectionId, "TCP receive buffer limit exceeded.");
          return;
        }

        var available = peer.Client.Client.Available;
        var count = Math.Min(Math.Min(available, peer.Buffer.Length - peer.BufferCount), byteBudget);
        var read = peer.Client.Client.Receive(peer.Buffer, peer.BufferCount, count, SocketFlags.None);
        if (read == 0)
        {
          Disconnect(peer.ConnectionId, "Remote peer closed the TCP connection.");
          return;
        }

        peer.BufferCount += read;
        byteBudget -= read;
        if (!ParseFrames(peer, ref frameBudget))
        {
          return;
        }
      }
    }
    catch (SocketException exception) when (exception.SocketErrorCode == SocketError.WouldBlock)
    {
      // The socket became temporarily unavailable after the Available check.
    }
    catch (Exception exception)
    {
      Disconnect(peer.ConnectionId, $"TCP receive failed: {exception.Message}");
    }
  }

  private bool FlushWrites(PeerConnection peer, ref int byteBudget)
  {
    while (peer.PendingWrites.Count > 0 && byteBudget > 0)
    {
      var frame = peer.PendingWrites.Peek();
      var remaining = Math.Min(frame.Length - peer.PendingWriteOffset, byteBudget);
      try
      {
        var sent = peer.Client.Client.Send(frame, peer.PendingWriteOffset, remaining, SocketFlags.None);
        if (sent <= 0)
        {
          Disconnect(peer.ConnectionId, "TCP socket stopped accepting data.");
          return false;
        }

        peer.PendingWriteOffset += sent;
        peer.PendingBytes -= sent;
        byteBudget -= sent;
        if (peer.PendingWriteOffset == frame.Length)
        {
          peer.PendingWrites.Dequeue();
          peer.PendingWriteOffset = 0;
        }
      }
      catch (SocketException exception) when (exception.SocketErrorCode == SocketError.WouldBlock
          || exception.SocketErrorCode == SocketError.IOPending
          || exception.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
      {
        return true;
      }
      catch (Exception exception)
      {
        Disconnect(peer.ConnectionId, $"TCP send to {peer.RemoteEndPoint} failed: {exception.Message}");
        return false;
      }
    }

    return true;
  }

  private bool ParseFrames(PeerConnection peer, ref int frameBudget)
  {
    var consumed = 0;
    while (frameBudget > 0 && peer.BufferCount - consumed >= PrefixLength)
    {
      var frameLength = ReadInt32(peer.Buffer, consumed);
      if (frameLength < 1 || frameLength > MaximumFrameLength)
      {
        Disconnect(peer.ConnectionId, $"Received invalid TCP frame length {frameLength}.");
        return false;
      }

      if (peer.BufferCount - consumed < PrefixLength + frameLength)
      {
        break;
      }

      if (!peer.TryConsume(frameLength, Now))
      {
        Disconnect(peer.ConnectionId, "Peer exceeded the incoming TCP packet rate limit.");
        return false;
      }

      var packet = new byte[frameLength];
      Array.Copy(peer.Buffer, consumed + PrefixLength, packet, 0, frameLength);
      consumed += PrefixLength + frameLength;
      frameBudget--;
      try
      {
        PacketReceived?.Invoke(new TransportPeer(peer.ConnectionId), packet);
      }
      catch (Exception exception)
      {
        LogError($"Packet handler failed for {peer.RemoteEndPoint}: {exception}");
        Disconnect(peer.ConnectionId, "Local packet handler failed.");
        return false;
      }

      if (!_peers.ContainsKey(peer.ConnectionId))
      {
        return false;
      }
    }

    if (consumed > 0)
    {
      var remaining = peer.BufferCount - consumed;
      Array.Copy(peer.Buffer, consumed, peer.Buffer, 0, remaining);
      peer.BufferCount = remaining;
    }

    return true;
  }

  private void RaiseConnected(PeerConnection peer)
  {
    try
    {
      PeerConnected?.Invoke(new TransportPeer(peer.ConnectionId));
    }
    catch (Exception exception)
    {
      LogError($"Connected handler failed for {peer.RemoteEndPoint}: {exception}");
      Disconnect(peer.ConnectionId, "Local connection handler failed.");
    }
  }

  private void RaiseDisconnected(PeerConnection peer, string reason)
  {
    try
    {
      PeerDisconnected?.Invoke(new TransportPeer(peer.ConnectionId), reason);
    }
    catch (Exception exception)
    {
      LogError($"Disconnected handler failed for {peer.RemoteEndPoint}: {exception}");
    }
  }

  private void RemovePeer(PeerConnection peer)
  {
    _peers.Remove(peer.ConnectionId);
    try
    {
      peer.Client.Close();
    }
    catch (Exception exception)
    {
      LogWarning($"TCP socket cleanup for {peer.RemoteEndPoint} failed: {exception.Message}");
    }
  }

  private long NextConnectionId()
  {
    _nextConnectionId++;
    if (_nextConnectionId <= 0)
    {
      _nextConnectionId = 1;
    }

    return _nextConnectionId;
  }

  private static int ReadInt32(byte[] buffer, int offset)
  {
    return (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
  }

  private static void WriteInt32(byte[] buffer, int offset, int value)
  {
    buffer[offset] = (byte)(value >> 24);
    buffer[offset + 1] = (byte)(value >> 16);
    buffer[offset + 2] = (byte)(value >> 8);
    buffer[offset + 3] = (byte)value;
  }

  private double Now => _clock.Elapsed.TotalSeconds;
  private void LogInfo(string message) => _infoLog(message);
  private void LogWarning(string message) => _warningLog(message);
  private void LogError(string message) => _errorLog(message);
  private void LogDebug(string message)
  {
    if (_verbose)
    {
      _infoLog($"[network] {message}");
    }
  }

  private sealed class PeerConnection
  {
    internal PeerConnection(long connectionId, TcpClient client, IAsyncResult? connectResult)
    {
      ConnectionId = connectionId;
      Client = client;
      ConnectResult = connectResult;
      Buffer = new byte[MaximumFrameLength + PrefixLength];
      RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown endpoint";
    }

    internal long ConnectionId { get; }
    internal TcpClient Client { get; }
    internal IAsyncResult? ConnectResult { get; }
    internal string RemoteEndPoint { get; private set; }
    internal Queue<byte[]> PendingWrites { get; } = new Queue<byte[]>();
    internal int PendingWriteOffset { get; set; }
    internal int PendingBytes { get; set; }
    internal byte[] Buffer { get; }
    internal int BufferCount { get; set; }
    internal bool Connected { get; set; }
    internal double WindowStarted { get; set; }
    private int MessagesInWindow { get; set; }
    private int BytesInWindow { get; set; }

    internal void SetRemoteEndPoint(string endpoint) => RemoteEndPoint = endpoint;

    internal bool TryConsume(int bytes, double now)
    {
      if (now - WindowStarted >= 1.0)
      {
        WindowStarted = now;
        MessagesInWindow = 0;
        BytesInWindow = 0;
      }

      if (MessagesInWindow >= MaximumMessagesPerPeerPerSecond
          || bytes > MaximumBytesPerPeerPerSecond - BytesInWindow)
      {
        return false;
      }

      MessagesInWindow++;
      BytesInWindow += bytes;
      return true;
    }
  }
}
