using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreZoneBlocks
{
    public static class LoopGuardPatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                Log.Info("Applying 49152 inlined loop guard transpilers...");

                HarmonyMethod transpiler = new HarmonyMethod(typeof(LoopGuardPatches).GetMethod(nameof(Replace49152With65535Transpiler), BindingFlags.Public | BindingFlags.Static));

                // ZoneManager loop guards
                PatchAllMatching(harmony, typeof(ZoneManager), "ReleaseBlockImplementation", transpiler);
                PatchAllMatching(harmony, typeof(ZoneManager), "UpdateBlocks", transpiler);
                PatchAllMatching(harmony, typeof(ZoneManager), "CheckSpace", transpiler);
                PatchAllMatching(harmony, typeof(ZoneManager), "TerrainUpdated", transpiler);

                // ZoneBlock loop guards
                PatchAllMatching(harmony, typeof(ZoneBlock), "CalculateBlock2", transpiler);
                PatchAllMatching(harmony, typeof(ZoneBlock), "CalculateBlock3", transpiler);
                PatchAllMatching(harmony, typeof(ZoneBlock), "IsGoodPlace", transpiler);
                PatchAllMatching(harmony, typeof(ZoneBlock), "SimulationStep", transpiler);

                // Building zonable validation
                PatchAllMatching(harmony, typeof(Building), "CheckZoning", transpiler);

                // Tool loop guards
                PatchAllMatching(harmony, typeof(BuildingTool), "GetAllZoneBlocks", transpiler);
                PatchAllMatching(harmony, typeof(DefaultTool), "RenderOverlay", transpiler);
                PatchAllMatching(harmony, typeof(ZoneTool), "ApplyZoning", transpiler);
                PatchAllMatching(harmony, typeof(ZoneTool), "ApplyBrush", transpiler);
                PatchAllMatching(harmony, typeof(ZoneTool), "CalculateFillBuffer", transpiler);

                Log.Info("All loop guard transpilers applied successfully.");
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "LoopGuardPatches.Apply");
            }
        }

        private static void PatchAllMatching(Harmony harmony, Type type, string methodName, HarmonyMethod transpiler)
        {
            if (type == null) return;

            MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name == methodName && !m.IsAbstract && m.GetMethodBody() != null)
                {
                    try
                    {
                        harmony.Patch(m, transpiler: transpiler);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(string.Format("Skipping {0}::{1} ({2}): {3}", type.Name, m.Name, m.GetParameters().Length, ex.Message));
                    }
                }
            }
        }

        public static IEnumerable<CodeInstruction> Replace49152With65535Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction ins in instructions)
            {
                // Replace ldc.i4 49152 (0xC000) with 65535 (0xFFFF)
                if (ins.opcode == OpCodes.Ldc_I4 && Convert.ToInt32(ins.operand) == ZoneCustomizer.VANILLA_BLOCK_COUNT)
                {
                    ins.operand = ZoneCustomizer.EXPANDED_LIMIT;
                }
                yield return ins;
            }
        }
    }
}
