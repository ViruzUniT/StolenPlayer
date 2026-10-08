# StolenPlayer

StolenPlayer is a BepInEx plugin for cooperative multiplayer in Thief Simulator 2. Direct IPv4 host/join over TCP, a versioned peer handshake, host scene synchronization, and visual remote-player movement replication are implemented. World interactions, persistent world snapshots, and shared saves are not active yet.

## Build

The project targets `netstandard2.0` and references assemblies from the installed game. Set `TS2Path` to the game installation before building:

```powershell
$env:TS2Path = 'C:\Path\To\Thief Simulator 2'
dotnet build .\StolenPlayer.slnx
```

The plugin assembly is produced at `src/StolenPlayer/bin/Debug/netstandard2.0/StolenPlayer.dll`. BepInEx, Harmony, Unity, and game assemblies are reference-only and are not copied into the plugin output.

Run the protocol codec checks with `dotnet run --project .\tests\StolenPlayer.ProtocolTests\StolenPlayer.ProtocolTests.csproj`.

## Deploy

Close the game, then run the deployment script from the repository root:

```powershell
.\scripts\deploy.ps1
```

It builds the selected configuration, backs up any installed `StolenPlayer.dll`, replaces it, and verifies the deployed file hash. Use `-GamePath 'C:\Path\To\Thief Simulator 2'` to select another installation, or `-Configuration Release` to deploy a Release build.

Set `TS2Path` before running the script to use it as the default game installation path, or pass `-GamePath` explicitly.

## Architecture notes

- [Verified game architecture and reverse-engineering findings](docs/game-architecture.md)
- [Multiplayer architecture and milestone gates](docs/multiplayer-architecture.md)
- [Direct IP connection test plan](docs/ip-connection-test-plan.md)

Open the in-game multiplayer panel with **F8**. Choose a session port and host; TCP carries the handshake, session control, and initial snapshots, while UDP carries frequent movement poses. Clients enter the host's numeric IPv4 address and the same port. On a LAN, the host can find its local IPv4 address with `ipconfig`; clients on the same network use that address. After connecting, the client automatically loads the scene currently active on the host, moves to a position beside the host, and waits for the initial player roster before movement replication starts. For internet play, allow and forward the selected port for **both TCP and UDP**. If UDP movement cannot be established or later times out, pose replication temporarily falls back to TCP. The current transport does not use Steam APIs or provide encryption/authentication, so use it with trusted peers on a trusted network. The current prototype sends host-relayed player position, facing, and movement flags and renders remote players from the inspected NPC model. This is visual synchronization only; world interactions, saves, and object state snapshots are not synchronized. Press **F10** to toggle a local-only NPC preview for checking the model.

Remote avatar animation names can be changed in `BepInEx/config/dev.viruzunit.stolenplayer.cfg` under `[PlayerAnimations]`. `IdleAnimationNames` is a comma-separated list of exact names tried in order. The default prefers `BreatheIdle`, then `Idle`, followed by fallback idle states. `WalkAnimationName` and `RunAnimationName` set the movement clips. Restart the game after editing the config; selected clips play and loop normally.

A local multi-instance harness with isolated test profiles and a host-plus-N launcher is the next planned milestone, followed by host-authoritative world interactions and state replication.
