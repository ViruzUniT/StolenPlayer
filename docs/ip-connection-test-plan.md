# Direct IP connection checks

The current milestone verifies connection setup and protocol compatibility only. Players will not appear in one another's worlds until remote-player replication is implemented.

## Same-computer loopback

1. Install the same plugin build in two isolated TS2 copies or run two game instances with separate user profiles.
2. Start one instance, open the F8 panel, choose port `27960`, and select **Host direct IP session**.
3. Start the client instance, enter `127.0.0.1` and port `27960`, then select **Join IP**.
4. Confirm the host shows one ready peer and the client reports the compatible host handshake.
5. Leave from either side and confirm the other side detects the disconnect.

## Local network

1. Use the same plugin and game versions on both PCs.
2. On the host, run `ipconfig` and note the active adapter's IPv4 address.
3. Allow the selected TCP port through the host firewall on the private network.
4. Host a session. On the client, enter the host's LAN IPv4 address and the same port.
5. Confirm the handshake succeeds, then test leaving and host shutdown.

## Failure cases

- Enter a malformed IP or invalid port; the UI should prevent the attempt or show a clear error.
- Join an unused port; the connection should fail or time out without freezing the game.
- Fill the configured player capacity; extra peers should be rejected.
- Use a mismatched game or plugin version; the protocol handshake should reject the peer.
- Send malformed/oversized frames in a protocol test; the transport should close that connection without crashing the plugin.

The transport currently has no encryption or peer authentication. Run these checks on loopback or a trusted LAN; do not expose it to untrusted networks yet.
