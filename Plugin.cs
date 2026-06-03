#nullable disable
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace SolarExpanse.UIFramework
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class UiFrameworkPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.mod.solarexpanse.uiframework";
        public const string PluginName = "Solar Expanse UI Framework";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource FrameworkLog;

        private Harmony _harmony;

        private void Awake()
        {
            FrameworkLog = Logger;
            SolarExpanseUi.SetLog(Logger);

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();
            PauseScreenEscPatch.Apply(_harmony, Logger);

            Logger.LogInfo("Solar Expanse UI Framework loaded");
        }

        private void Update() => SolarExpanseUi.InternalUpdate();

        private void LateUpdate() => PauseScreenEscPatch.LateUpdateTick();
    }
}
