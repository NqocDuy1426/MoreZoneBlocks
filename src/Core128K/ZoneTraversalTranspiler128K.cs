using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace MoreZoneBlocks.Core128K
{
    /// <summary>
    /// Mã Nguồn Lõi 6: Universal 17-Bit Linked-List Traversal Transpiler.
    /// Tự động biến mọi phép đọc `.m_nextGridBlock` (16-bit) thành `BitStash128K.GetNextGridBlock32(ref block)` (17-bit)
    /// và nâng mọi Loop Guard 49152 lên 131072 trên toàn bộ hệ thống!
    /// </summary>
    [HarmonyPatch]
    public static class ZoneTraversalTranspiler128K
    {
        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo m = null;

            // 1. ZoneBlock.CalculateBlock2(ushort)
            m = AccessTools.Method(typeof(ZoneBlock), "CalculateBlock2", new Type[] { typeof(ushort) });
            if (m != null) yield return m;

            // 2. ZoneBlock.SimulationStep(ushort)
            m = AccessTools.Method(typeof(ZoneBlock), "SimulationStep", new Type[] { typeof(ushort) });
            if (m != null) yield return m;

            // 3. ZoneManager.ReleaseBlockImplementation(ushort, ref ZoneBlock)
            m = AccessTools.Method(typeof(ZoneManager), "ReleaseBlockImplementation", new Type[] { typeof(ushort), typeof(ZoneBlock).MakeByRefType() });
            if (m != null) yield return m;

            // 4. ZoneManager.UpdateBlocks(Quad2)
            m = AccessTools.Method(typeof(ZoneManager), "UpdateBlocks", new Type[] { typeof(ColossalFramework.Math.Quad2) });
            if (m != null) yield return m;

            // 5. ZoneManager.CheckSpace(Vector3, float, int, int, out int)
            m = AccessTools.Method(typeof(ZoneManager), "CheckSpace", new Type[] { typeof(Vector3), typeof(float), typeof(int), typeof(int), typeof(int).MakeByRefType() });
            if (m != null) yield return m;

            // 6. ZoneManager.TerrainUpdated(TerrainArea, TerrainArea, TerrainArea)
            m = AccessTools.Method(typeof(ZoneManager), "TerrainUpdated", new Type[] { typeof(TerrainArea), typeof(TerrainArea), typeof(TerrainArea) });
            if (m != null) yield return m;

            // 7. Building.CheckZoning(ItemClass.Zone, ItemClass.Zone, bool)
            m = AccessTools.Method(typeof(Building), "CheckZoning", new Type[] { typeof(ItemClass.Zone), typeof(ItemClass.Zone), typeof(bool) });
            if (m != null) yield return m;

            // 8. BuildingTool.SimulationStep()
            m = AccessTools.Method(typeof(BuildingTool), "SimulationStep", Type.EmptyTypes);
            if (m != null) yield return m;

            // 9. ZoneTool.CalculateFillBuffer(Vector3, Vector3, ItemClass.Zone, bool, bool)
            m = AccessTools.Method(typeof(ZoneTool), "CalculateFillBuffer", new Type[] { typeof(Vector3), typeof(Vector3), typeof(ItemClass.Zone), typeof(bool), typeof(bool) });
            if (m != null) yield return m;

            // 10. ZoneTool.ApplyFill()
            m = AccessTools.Method(typeof(ZoneTool), "ApplyFill", Type.EmptyTypes);
            if (m != null) yield return m;

            // 11. ZoneTool.ApplyZoning()
            m = AccessTools.Method(typeof(ZoneTool), "ApplyZoning", Type.EmptyTypes);
            if (m != null) yield return m;

            // 12. ZoneTool.ApplyBrush()
            m = AccessTools.Method(typeof(ZoneTool), "ApplyBrush", Type.EmptyTypes);
            if (m != null) yield return m;
        }

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            FieldInfo fNextGridBlock = AccessTools.Field(typeof(ZoneBlock), nameof(ZoneBlock.m_nextGridBlock));
            MethodInfo mGetNext32 = AccessTools.Method(typeof(BitStash128K), nameof(BitStash128K.GetNextGridBlock32));
            MethodInfo mGetCurrentID32 = AccessTools.Method(typeof(ZoneUpdateAndSim128K), nameof(ZoneUpdateAndSim128K.GetCurrentBlockID32));

            bool isCalculateBlock2 = original != null && original.Name == "CalculateBlock2";

            foreach (var inst in instructions)
            {
                // 1. Thay thế phép đọc trường 16-bit `block.m_nextGridBlock`
                //    bằng lời gọi 17-bit `BitStash128K.GetNextGridBlock32(ref block)`
                if (inst.opcode == OpCodes.Ldfld && Equals(inst.operand, fNextGridBlock))
                {
                    inst.opcode = OpCodes.Call;
                    inst.operand = mGetNext32;
                    yield return inst;
                }
                // 2. Nâng toàn bộ chốt chặn vòng lặp vô tận (Infinite Loop Guard) từ 49152 lên 131072
                else if (inst.opcode == OpCodes.Ldc_I4 && Convert.ToInt32(inst.operand) == 49152)
                {
                    inst.operand = BitStash128K.MAX_BLOCK_COUNT; // 131072
                    yield return inst;
                }
                // 3. Trong CalculateBlock2: sửa so sánh ldarg.1 để nhận diện đúng ID 32-bit từ CurrentExecutingBlockID32
                else if (isCalculateBlock2 && inst.opcode == OpCodes.Ldarg_1)
                {
                    yield return inst; // Đẩy ushort arg1 lên stack
                    yield return new CodeInstruction(OpCodes.Call, mGetCurrentID32); // Biến đổi thành uint32 đầy đủ 17-bit
                }
                else
                {
                    yield return inst;
                }
            }
        }
    }
}
