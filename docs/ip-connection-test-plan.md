# Direct IP connection checks

The current prototype verifies direct-IP connection, host-scene loading, and visual remote-player synchronization. Door identities are scanned for diagnostics but are not used to authorize or replicate interactions yet.

## Remote-player lifecycle regression checks

The current game logs show the working late-join path but do not contain the reported menu-to-game transition. After installing this build, capture fresh host and client logs for each case and correlate `RemotePlayerLifecycle`, `Host selected scene`, `Client loaded authoritative host scene`, and peer scene-ready messages.

1. **Connected before gameplay:** host starts in the menu; client connects; host enters gameplay. Confirm the host logs the peer rejoining the scene-ready roster and `action=Spawn reason=Reconcile`, and both sides see one another.
2. **Late join:** host enters gameplay first; client joins. Confirm the same `action=Spawn reason=Reconcile` path on both peers.
3. **Connected scene transition:** transition while connected. Confirm old scene representations are destroyed, the peer identity remains connected, and one current-scene representation is recreated.
4. **Repeated transitions:** perform at least three transitions; confirm no duplicate avatars and each peer has at most one `action=Spawn` between matching destruction events.
5. **Disconnect/reconnect:** leave and rejoin; confirm the old representation is destroyed on disconnect and one new representation is materialized.

The build/test checks do not substitute for these in-game checks. If the menu path still fails, keep both logs from the same attempt so host and client sequence numbers, scene readiness, and peer roster order can be compared.

## Same-computer loopback

1. Install the same plugin build in two isolated TS2 copies or run two game instances with separate user profiles.
2. Start one instance, open the F8 panel, choose port `27960`, and select **Host direct IP session**.
3. Start the client instance, enter `127.0.0.1` and port `27960`, then select **Join IP**.
4. Confirm the host shows one ready peer and the client loads the host's current scene and spawn position.
5. Confirm both players can see the other move; check that walking/running animations follow movement and the idle pose remains upright.
6. In each BepInEx log, compare the `Static world identity scan` counts for the same loaded scenes. The scanner identity now includes serialized sibling and same-object component ordinals to distinguish repeated names. Investigate any remaining `Door identity rejected` messages; object keys are diagnostic only and do not yet drive gameplay replication.
7. Confirm the host logs `UDP movement listener opened` and both sides log `UDP movement path established/confirmed`. Move on both sides and verify poses stay responsive. Test with UDP blocked locally or by firewall and confirm TCP pose fallback is logged and movement still works. For internet play, forward/allow the session port for both TCP and UDP.
7. Leave from either side and confirm the other side detects the disconnect and removes its avatar.

## Local network

1. Use the same plugin and game versions on both PCs.
2. On the host, run `ipconfig` and note the active adapter's IPv4 address.
3. Allow the selected TCP and UDP port through the host firewall on the private network.
4. Host a session. On the client, enter the host's LAN IPv4 address and the same port.
5. Confirm the handshake succeeds, then test leaving and host shutdown.

## Failure cases

- Enter a malformed IP or invalid port; the UI should prevent the attempt or show a clear error.
- Join an unused port; the connection should fail or time out without freezing the game.
- Fill the configured player capacity; extra peers should be rejected.
- Use a mismatched game or plugin version; the protocol handshake should reject the peer.
- Send malformed/oversized frames in a protocol test; the transport should close that connection without crashing the plugin.

The transport currently has no encryption or peer authentication. Run these checks on loopback or a trusted LAN; do not expose it to untrusted networks yet.
