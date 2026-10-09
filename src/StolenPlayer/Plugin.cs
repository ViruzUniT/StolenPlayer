using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace StolenPlayer
{
  [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
  public sealed class Plugin : BaseUnityPlugin
  {
    public const string PluginGuid = "dev.viruzunit.stolenplayer";
    public const string PluginName = "StolenPlayer";
    public const string PluginVersion = "0.8.1";

    internal static ManualLogSource Log { get; private set; } = null!;

    private Harmony? _harmony;
    private PluginConfig? _config;
    private GameObject? _runtimeObject;

    private void Awake()
    {
      Log = Logger;
      _config = PluginConfig.Bind(Config);

      try
      {
        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll();

        _runtimeObject = new GameObject("StolenPlayer.Runtime");
        UnityEngine.Object.DontDestroyOnLoad(_runtimeObject);
        _runtimeObject.AddComponent<MultiplayerRuntime>().Initialize(_config);

        Log.LogInfo($"{PluginName} {PluginVersion} initialized.");
        Log.LogInfo($"Configured direct IP session capacity: {_config.MaxPlayers.Value}; TCP/UDP port: {_config.ListenPort.Value}.");
      }
      catch (Exception exception)
      {
        _harmony?.UnpatchSelf();
        _harmony = null;
        Log.LogError($"Plugin initialization failed: {exception}");
        throw;
      }
    }

    private void OnDestroy()
    {
      if (_harmony == null)
      {
        if (_runtimeObject != null)
        {
          Destroy(_runtimeObject);
          _runtimeObject = null;
        }

        return;
      }

      if (_runtimeObject != null)
      {
        Destroy(_runtimeObject);
        _runtimeObject = null;
      }

      _harmony.UnpatchSelf();
      _harmony = null;
      Log.LogInfo($"{PluginName} Harmony patches removed.");
    }
  }
}
