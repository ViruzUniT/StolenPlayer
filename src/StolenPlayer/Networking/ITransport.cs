using System;

namespace StolenPlayer.Networking;

internal interface ITransport : IDisposable
{
  event Action<TransportPeer>? PeerConnected;
  event Action<TransportPeer, byte[]>? PacketReceived;
  event Action<TransportPeer, string>? PeerDisconnected;

  bool StartListening(int port, int maximumPeers, out string error);
  bool Connect(string address, int port, out string error);
  bool Send(long connectionId, byte[] packet, DeliveryMode deliveryMode, out string error);
  void Disconnect(long connectionId, string reason);
}
