using BepInEx.Configuration;

namespace StolenPlayer;

internal sealed class PluginConfig
{
  private PluginConfig(
    ConfigEntry<int> listenPort,
    ConfigEntry<int> maxPlayers,
    ConfigEntry<bool> verboseNetworking)
  {
    ListenPort = listenPort;
    MaxPlayers = maxPlayers;
    VerboseNetworking = verboseNetworking;
  }

  internal ConfigEntry<int> ListenPort { get; }

  internal ConfigEntry<int> MaxPlayers { get; }

  internal ConfigEntry<bool> VerboseNetworking { get; }

  internal static PluginConfig Bind(ConfigFile config)
  {
    var listenPort = config.Bind(
      "Network",
      "Port",
      27960,
      new ConfigDescription(
        "TCP port used for direct IP multiplayer. Internet clients may require a router firewall/port-forward rule.",
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

    return new PluginConfig(listenPort, maxPlayers, verboseNetworking);
  }
}
