using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ColossalFramework;
using HarmonyLib;

namespace MoreZoneBlocks
{
    public static class ZonePatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                Log.Info("Applying core ZoneManager & Serialization patches...");

                // 1. Serialization lifecycle hooks
                MethodInfo netSerialize = typeof(NetManager.Data).GetMethod("Serialize", BindingFlags.Public | BindingFlags.Instance);
                MethodInfo netSerializePrefix = typeof(ZonePatches).GetMethod(nameof(NetManager_Data_Serialize_Prefix), BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(netSerialize, prefix: new HarmonyMethod(netSerializePrefix));

                MethodInfo zoneSerialize = typeof(ZoneManager.Data).GetMethod("Serialize", BindingFlags.Public | BindingFlags.Instance);
                MethodInfo zoneSerializePostfix = typeof(ZonePatches).GetMethod(nameof(ZoneManager_Data_Serialize_Postfix), BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(zoneSerialize, postfix: new HarmonyMethod(zoneSerializePostfix));

                MethodInfo zoneDeserialize = typeof(ZoneManager.Data).GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Instance);
                MethodInfo zoneDeserializePrefix = typeof(ZonePatches).GetMethod(nameof(ZoneManager_Data_Deserialize_Prefix), BindingFlags.Public | BindingFlags.Static);
                MethodInfo zoneDeserializePostfix = typeof(ZonePatches).GetMethod(nameof(ZoneManager_Data_Deserialize_Postfix), BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(zoneDeserialize, prefix: new HarmonyMethod(zoneDeserializePrefix), postfix: new HarmonyMethod(zoneDeserializePostfix));

                MethodInfo zoneAfterDeserialize = typeof(ZoneManager.Data).GetMethod("AfterDeserialize", BindingFlags.Public | BindingFlags.Instance);
                MethodInfo zoneAfterDeserializePostfix = typeof(ZonePatches).GetMethod(nameof(ZoneManager_Data_AfterDeserialize_Postfix), BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(zoneAfterDeserialize, postfix: new HarmonyMethod(zoneAfterDeserializePostfix));

                // 2. ZoneManager.Awake (Transpiler + Postfix defense-in-depth)
                MethodInfo zmAwake = typeof(ZoneManager).GetMethod("Awake", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo zmAwakeTranspiler = typeof(ZonePatches).GetMethod(nameof(ZoneManager_Awake_Transpiler), BindingFlags.Public | BindingFlags.Static);
                MethodInfo zmAwakePostfix = typeof(ZonePatches).GetMethod(nameof(ZoneManager_Awake_Postfix), BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(zmAwake, transpiler: new HarmonyMethod(zmAwakeTranspiler), postfix: new HarmonyMethod(zmAwakePostfix));

                // 3. ZoneManager.CheckLimits (Transpiler)
                MethodInfo zmCheckLimits = typeof(ZoneManager).GetMethod("CheckLimits", BindingFlags.Public | BindingFlags.Instance);
                MethodInfo zmCheckLimitsTranspiler = typeof(ZonePatches).GetMethod(nameof(ZoneManager_CheckLimits_Transpiler), BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(zmCheckLimits, transpiler: new HarmonyMethod(zmCheckLimitsTranspiler));

                // 4. ZoneManager.SimulationStepImpl (Transpiler + Postfix for Auto-GC)
                MethodInfo zmSimStep = typeof(ZoneManager).GetMethod("SimulationStepImpl", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo zmSimStepTranspiler = typeof(ZonePatches).GetMethod(nameof(ZoneManager_SimulationStepImpl_Transpiler), BindingFlags.Public | BindingFlags.Static);
                MethodInfo zmSimStepPostfix = typeof(ZonePatches).GetMethod(nameof(ZoneManager_SimulationStepImpl_Postfix), BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(zmSimStep, transpiler: new HarmonyMethod(zmSimStepTranspiler), postfix: new HarmonyMethod(zmSimStepPostfix));

                Log.Info("Core ZoneManager & Serialization patches applied successfully.");
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "ZonePatches.Apply");
            }
        }

        // ====================================================================
        // SERIALIZATION HOOKS
        // ====================================================================

        public static void NetManager_Data_Serialize_Prefix()
        {
            SaveSanitizer.SanitizeHighSegmentBlockPointers();
        }

        public static void ZoneManager_Data_Serialize_Postfix()
        {
            SaveSanitizer.PersistSidecarDataAndRestorePointers();
        }

        public static void ZoneManager_Data_Deserialize_Prefix()
        {
            SaveSanitizer.PrepareEarlySidecarData();
        }

        public static void ZoneManager_Data_Deserialize_Postfix()
        {
            // Expand buffer to 64K immediately so no subsequent deserialization step sees 48K
            ZoneManager zm = Singleton<ZoneManager>.instance;
            if (zm != null)
            {
                ZoneCustomizer.EnsureExpanded(zm);
            }
        }

        public static void ZoneManager_Data_AfterDeserialize_Postfix()
        {
            // Restore sidecar blocks and NetSegment mappings after all essential scenes are loaded
            SaveSanitizer.RestoreSidecarData();
        }

        // ====================================================================
        // ZONEMANAGER.AWAKE PATCHES
        // ====================================================================

        public static IEnumerable<CodeInstruction> ZoneManager_Awake_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int replacedCount = 0;
            foreach (CodeInstruction ins in instructions)
            {
                // Replace ldc.i4 49152 (0xC000) with 65536 (0x10000)
                if (ins.opcode == OpCodes.Ldc_I4 && Convert.ToInt32(ins.operand) == ZoneCustomizer.VANILLA_BLOCK_COUNT)
                {
                    ins.operand = ZoneCustomizer.EXPANDED_BLOCK_COUNT;
                    replacedCount++;
                    Log.Info("Transpiled ZoneManager.Awake: 49152 -> 65536");
                }
                // Replace ldc.i4 768 with 1024 (m_updatedBlocks array size)
                else if (ins.opcode == OpCodes.Ldc_I4 && Convert.ToInt32(ins.operand) == ZoneCustomizer.VANILLA_UPDATED_BLOCKS)
                {
                    ins.operand = ZoneCustomizer.EXPANDED_UPDATED_BLOCKS;
                    replacedCount++;
                    Log.Info("Transpiled ZoneManager.Awake: 768 -> 1024");
                }
                yield return ins;
            }

            if (replacedCount < 2)
            {
                Log.Warning(string.Format("ZoneManager.Awake transpiler expected 2 replacements but made {0}", replacedCount));
            }
        }

        public static void ZoneManager_Awake_Postfix(ZoneManager __instance)
        {
            ZoneCustomizer.EnsureExpanded(__instance);
        }

        // ====================================================================
        // ZONEMANAGER.CHECKLIMITS PATCH
        // ====================================================================

        public static IEnumerable<CodeInstruction> ZoneManager_CheckLimits_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int replacedCount = 0;
            foreach (CodeInstruction ins in instructions)
            {
                // Replace ldc.i4 48640 with 65024 (warning threshold)
                if (ins.opcode == OpCodes.Ldc_I4 && Convert.ToInt32(ins.operand) == 48640)
                {
                    ins.operand = ZoneCustomizer.WARNING_BLOCK_THRESHOLD;
                    replacedCount++;
                    Log.Info("Transpiled ZoneManager.CheckLimits: 48640 -> 65024");
                }
                yield return ins;
            }

            if (replacedCount < 1)
            {
                Log.Warning("ZoneManager.CheckLimits transpiler did not find 48640 threshold to replace.");
            }
        }

        // ====================================================================
        // ZONEMANAGER.SIMULATIONSTEPIMPL PATCH
        // ====================================================================

        public static IEnumerable<CodeInstruction> ZoneManager_SimulationStepImpl_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int replacedCount = 0;
            foreach (CodeInstruction ins in instructions)
            {
                // Replace ldc.i4.s 48 with ldc.i4.s 64
                // Critical: OpCodes.Ldc_I4_S boxed operand is signed sbyte, must cast to (sbyte)64!
                if (ins.opcode == OpCodes.Ldc_I4_S && Convert.ToInt32(ins.operand) == 48)
                {
                    ins.operand = (sbyte)64;
                    replacedCount++;
                    Log.Info("Transpiled ZoneManager.SimulationStepImpl: ldc.i4.s 48 -> 64");
                }
                yield return ins;
            }

            if (replacedCount < 2)
            {
                Log.Warning(string.Format("ZoneManager.SimulationStepImpl transpiler expected 2 replacements for 48, made {0}", replacedCount));
            }
        }

        public static void ZoneManager_SimulationStepImpl_Postfix(int subStep)
        {
            if (subStep == 0)
            {
                ZoneGarbageCollector.OnSimulationStep();
            }
        }
    }
}

