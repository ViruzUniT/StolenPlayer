using BepInEx.Configuration;

namespace StolenPlayer;

internal sealed class PluginConfig
{
  private PluginConfig(
    ConfigEntry<int> listenPort,
    ConfigEntry<int> maxPlayers,
    ConfigEntry<bool> verboseNetworking,
    ConfigEntry<string> idleAnimationNames,
    ConfigEntry<string> walkAnimationName,
    ConfigEntry<string> runAnimationName,
    ConfigEntry<string> playerName,
    ConfigEntry<string> startupRole,
    ConfigEntry<string> startupAddress,
    ConfigEntry<int> startupPort)
  {
    ListenPort = listenPort;
    MaxPlayers = maxPlayers;
    VerboseNetworking = verboseNetworking;
    IdleAnimationNames = idleAnimationNames;
    WalkAnimationName = walkAnimationName;
    RunAnimationName = runAnimationName;
    PlayerName = playerName;
    StartupRole = startupRole;
    StartupAddress = startupAddress;
    StartupPort = startupPort;
  }

  internal ConfigEntry<int> ListenPort { get; }

  internal ConfigEntry<int> MaxPlayers { get; }

  internal ConfigEntry<bool> VerboseNetworking { get; }

  internal ConfigEntry<string> IdleAnimationNames { get; }

  internal ConfigEntry<string> WalkAnimationName { get; }

  internal ConfigEntry<string> RunAnimationName { get; }

  internal ConfigEntry<string> PlayerName { get; }

  internal ConfigEntry<string> StartupRole { get; }

  internal ConfigEntry<string> StartupAddress { get; }

  internal ConfigEntry<int> StartupPort { get; }

  internal static PluginConfig Bind(ConfigFile config)
  {
    var listenPort = config.Bind(
      "Network",
      "Port",
      27960,
      new ConfigDescription(
        "TCP control and UDP movement use this port. Internet clients may need a router firewall/port-forward rule for both protocols.",
        new AcceptableValueRange<int>(1024, 65535)));

    var maxPlayers = config.Bind(
      "Session",
      "MaxPlayers",
      4,
      new ConfigDescription(
        "Maximum players in a session, including the host. Four is the initial supported target.",
        new AcceptableValueRange<int>(2, 4)));

    var verboseNetworking = config.Bind(
      "Diagnostics",
      "VerboseNetworking",
      false,
      "Enables detailed diagnostics for direct IP networking.");

    var idleAnimationNames = config.Bind(
      "PlayerAnimations",
      "IdleAnimationNames",
      "Idle,BreatheIdle,Idle_Stand,IdleStanding,StandingIdle,Stand,Bored",
      "Comma-separated exact legacy Animation clip names in preference order. The first matching state is played as the remote player's idle.");

    var walkAnimationName = config.Bind(
      "PlayerAnimations",
      "WalkAnimationName",
      "Walk",
      "Animation state used for a remote player who is walking or crouch-moving.");

    var runAnimationName = config.Bind(
      "PlayerAnimations",
      "RunAnimationName",
      "Running",
      "Animation state used for a remote player who is running.");

    var playerName = config.Bind(
      "Player",
      "Name",
      "Player",
      "Name shown to other players and saved in the local BepInEx config. Keep names unique within a session.");

    var startupRole = config.Bind(
      "LocalTest",
      "StartupRole",
      "None",
      "Optional local harness startup role: None, Host, or Client. Leave as None for normal play.");

    var startupAddress = config.Bind(
      "LocalTest",
      "StartupAddress",
      "127.0.0.1",
      "Host address used by a local harness client.");

    var startupPort = config.Bind(
      "LocalTest",
      "StartupPort",
      27960,
      new ConfigDescription("Session TCP and UDP port used by the local harness.",
        new AcceptableValueRange<int>(1024, 65535)));

    return new PluginConfig(listenPort, maxPlayers, verboseNetworking,
      idleAnimationNames, walkAnimationName, runAnimationName, playerName,
      startupRole, startupAddress, startupPort);
  }
}
