using System;
using System.Net;
using System.Net.Sockets;

namespace StolenPlayer.Networking;

/// <summary>Non-blocking IPv4 datagrams used only for frequent movement snapshots.</summary>
internal sealed class UdpPoseTransport : IDisposable
{
  private const int MaximumDatagramsPerUpdate = 64;
  private readonly Action<string> _warningLog;
  private readonly Action<string> _errorLog;
  private UdpClient? _socket;
  private IPEndPoint? _hostEndpoint;
  private bool _disposed;
  private bool _sendWarningLogged;

  internal UdpPoseTransport(Action<string> warningLog, Action<string> errorLog)
  {
    _warningLog = warningLog;
    _errorLog = errorLog;
  }

  internal event Action<IPEndPoint, byte[]>? DatagramReceived;
  internal bool IsRunning => _socket != null && !_disposed;

  internal bool StartHost(int port, out string error)
  {
    return Start(new UdpClient(AddressFamily.InterNetwork), new IPEndPoint(IPAddress.Any, port), out error);
  }

  internal bool StartClient(string hostAddress, int port, out string error)
  {
    if (!IPAddress.TryParse(hostAddress, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
    {
      error = "UDP currently requires the host's numeric IPv4 address.";
      return false;
    }

    UdpClient? socket = null;
    try
    {
      socket = new UdpClient(AddressFamily.InterNetwork);
      socket.Connect(new IPEndPoint(address, port));
      _hostEndpoint = new IPEndPoint(address, port);
      var started = Start(socket, null, out error);
      if (!started) _hostEndpoint = null;
      return started;
    }
    catch (Exception exception)
    {
      socket?.Close();
      _hostEndpoint = null;
      error = $"Could not open the UDP movement socket: {exception.Message}";
      return false;
    }
  }

  internal void Update()
  {
    if (_socket == null || _disposed)
    {
      return;
    }

    for (var count = 0; count < MaximumDatagramsPerUpdate; count++)
    {
      try
      {
        if (_socket.Available <= 0)
        {
          break;
        }

        var endpoint = new IPEndPoint(IPAddress.Any, 0);
        var packet = _socket.Receive(ref endpoint);
        DatagramReceived?.Invoke(endpoint, packet);
      }
      catch (SocketException exception) when (exception.SocketErrorCode == SocketError.WouldBlock
          || exception.SocketErrorCode == SocketError.IOPending)
      {
        break;
      }
      catch (ObjectDisposedException)
      {
        break;
      }
      catch (Exception exception)
      {
        _errorLog($"UDP movement receive failed: {exception}");
        break;
      }
    }
  }

  internal bool Send(byte[] packet, IPEndPoint? endpoint = null)
  {
    if (_socket == null || _disposed || packet == null || packet.Length == 0)
    {
      return false;
    }

    try
    {
      if (_hostEndpoint != null)
      {
        _socket.Send(packet, packet.Length);
      }
      else if (endpoint != null)
      {
        _socket.Send(packet, packet.Length, endpoint);
      }
      else
      {
        return false;
      }

      return true;
    }
    catch (Exception exception)
    {
      if (!_sendWarningLogged)
      {
        _warningLog($"UDP movement send failed: {exception.Message}");
        _sendWarningLogged = true;
      }

      return false;
    }
  }

  public void Dispose()
  {
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    _socket?.Close();
    _socket = null;
    _hostEndpoint = null;
  }

  private bool Start(UdpClient socket, IPEndPoint? bindEndpoint, out string error)
  {
    if (_disposed || _socket != null)
    {
      socket.Close();
      error = "UDP movement transport is already started or disposed.";
      return false;
    }

    try
    {
      if (bindEndpoint != null)
      {
        socket.Client.Bind(bindEndpoint);
      }

      socket.Client.Blocking = false;
      _socket = socket;
      error = string.Empty;
      return true;
    }
    catch (Exception exception)
    {
      socket.Close();
      error = $"Could not start the UDP movement transport: {exception.Message}";
      return false;
    }
  }
}
