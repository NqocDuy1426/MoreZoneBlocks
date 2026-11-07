using System;
using ICities;
using CitiesHarmony.API;
using HarmonyLib;

namespace MoreZoneBlocks
{
    public sealed class ModInfo : IUserMod
    {
        public const string HARMONY_ID = "com.ngocduy.morezoneblocks";
        public const string MOD_NAME = "More Zone Blocks v1.0 by NgocDuy";
        public const string MOD_DESCRIPTION = "Expands Zone Block capacity from 48K to 64K (65,535 blocks) with Two-Stream Vanilla Save Preservation, 64-block simulation pacing, and Simulation-thread safe GC. By NgocDuy.";

        public string Name => MOD_NAME;
        public string Description => MOD_DESCRIPTION;

        public void OnEnabled()
        {
            HarmonyHelper.DoOnHarmonyReady(() =>
            {
                Patcher.PatchAll();
            });
        }

        public void OnDisabled()
        {
            if (HarmonyHelper.IsHarmonyInstalled)
            {
                Patcher.UnpatchAll();
            }
        }
    }

    public sealed class LoadingExtension : LoadingExtensionBase
    {
        public override void OnLevelLoaded(LoadMode mode)
        {
            base.OnLevelLoaded(mode);

            if (mode != LoadMode.LoadGame && mode != LoadMode.NewGame && mode != LoadMode.NewGameFromScenario && mode != LoadMode.LoadScenario)
            {
                return;
            }

            Log.Info("Game level loaded. Verifying MoreZoneBlocks expansion state...");
            ZoneCustomizer.EnsureExpanded(ColossalFramework.Singleton<ZoneManager>.instance);
        }
    }

    internal static class Patcher
    {
        private static bool s_isPatched = false;

        public static void PatchAll()
        {
            if (s_isPatched) return;

            try
            {
                Log.Info("Applying MoreZoneBlocks Harmony patches...");
                Harmony harmony = new Harmony(ModInfo.HARMONY_ID);

                ZonePatches.Apply(harmony);
                LoopGuardPatches.Apply(harmony);

                s_isPatched = true;
                Log.Info("MoreZoneBlocks Harmony patches applied successfully.");
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "Patcher.PatchAll");
            }
        }

        public static void UnpatchAll()
        {
            if (!s_isPatched) return;

            try
            {
                Harmony harmony = new Harmony(ModInfo.HARMONY_ID);
                harmony.UnpatchAll(ModInfo.HARMONY_ID);
                s_isPatched = false;
                Log.Info("MoreZoneBlocks unpatched successfully.");
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "Patcher.UnpatchAll");
            }
        }
    }
}
