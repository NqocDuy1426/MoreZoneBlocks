using System;
using System.Reflection;
using ICities;
using CitiesHarmony.API;
using HarmonyLib;
using MoreZoneBlocks.Core128K;

namespace MoreZoneBlocks
{
    public sealed class ModInfo : IUserMod
    {
        public const string HARMONY_ID = "com.ngocduy.morezoneblocks2";
        public const string MOD_NAME = "More Zone Blocks 2 (128K) by NgocDuy";
        public const string MOD_DESCRIPTION = "Doubles Zone Block capacity to 128K (131,072 blocks) with 17-Bit Bit-Stashing, Tail-Chained m_zoneGrid, and Sidecar Stream save preservation. By NgocDuy.";

        public string Name => MOD_NAME;
        public string Description => MOD_DESCRIPTION;

        public void OnEnabled()
        {
            HarmonyHelper.DoOnHarmonyReady(() =>
            {
                Patcher.PatchAll();
            });

            try
            {
                var go = new UnityEngine.GameObject("MZB_DirectAutoTestBoot");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<DirectAutoTestBootBehaviour>();
            }
            catch (Exception ex)
            {
                Log.Warning("Could not create MZB_DirectAutoTestBoot: " + ex.Message);
            }
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

            Log.Info("Game level loaded. Verifying MoreZoneBlocks 128K expansion state...");
            if (ColossalFramework.Singleton<ZoneManager>.exists)
            {
                ZoneManager zm = ColossalFramework.Singleton<ZoneManager>.instance;
                if (zm.m_blocks.m_buffer == null || zm.m_blocks.m_buffer.Length < BitStash128K.MAX_BLOCK_COUNT)
                {
                    ZoneUpdateAndSim128K.Awake_Postfix(zm);
                }
            }

            // Kích hoạt SimTester nếu đang trong phiên kiểm thử tự động
            try
            {
                var go = new UnityEngine.GameObject("MZB128KSimTester");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<MZB128KSimTester>();
                Log.Info("Spawned MZB128KSimTester component.");
            }
            catch (Exception ex)
            {
                Log.Warning("Could not spawn MZB128KSimTester: " + ex.Message);
            }
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
                Log.Info("Applying MoreZoneBlocks 2 (128K) Harmony patches...");
                Harmony harmony = new Harmony(ModInfo.HARMONY_ID);

                // Tự động quét và kích hoạt toàn bộ các patch Harmony trong Assembly (các module Core128K)
                harmony.PatchAll(Assembly.GetExecutingAssembly());

                s_isPatched = true;
                Log.Info("MoreZoneBlocks 2 (128K) Harmony patches applied successfully.");
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
