namespace StolenPlayer.Networking;

internal enum DeliveryMode
{
  Reliable
}

internal readonly struct TransportPeer
{
  internal TransportPeer(long connectionId)
  {
    ConnectionId = connectionId;
  }

  internal long ConnectionId { get; }
}
