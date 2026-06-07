#nullable disable
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace SolarExpanse.WindowManager
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class WindowManagerPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.mod.solarexpanse.windowmanager";
        public const string PluginName = "Solar Expanse Window Manager";
        public const string PluginVersion = "1.4.0";

        internal static ManualLogSource WindowManagerLog;

        private Harmony _harmony;

        private void Awake()
        {
            WindowManagerLog = Logger;
            SolarExpanseWindowManager.SetLog(Logger);

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();
            PauseScreenEscPatch.Apply(_harmony, Logger);

            Logger.LogInfo("Solar Expanse Window Manager loaded");
        }

        private void Update() => SolarExpanseWindowManager.InternalUpdate();

        private void LateUpdate() => PauseScreenEscPatch.LateUpdateTick();
    }
}
