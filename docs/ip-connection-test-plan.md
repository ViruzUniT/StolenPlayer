# Direct IP connection checks

The current prototype verifies direct-IP connection, host-scene loading, and visual remote-player synchronization. Door identities are scanned for diagnostics but are not used to authorize or replicate interactions yet.

## Same-computer loopback

1. Install the same plugin build in two isolated TS2 copies or run two game instances with separate user profiles.
2. Start one instance, open the F8 panel, choose port `27960`, and select **Host direct IP session**.
3. Start the client instance, enter `127.0.0.1` and port `27960`, then select **Join IP**.
4. Confirm the host shows one ready peer and the client loads the host's current scene and spawn position.
5. Confirm both players can see the other move; check that walking/running animations follow movement and the idle pose remains upright.
6. In each BepInEx log, compare the `Static world identity scan` door counts for the loaded scene. Investigate any `Door identity rejected` messages; object keys are diagnostic only and do not yet drive gameplay replication.
7. Leave from either side and confirm the other side detects the disconnect and removes its avatar.

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
