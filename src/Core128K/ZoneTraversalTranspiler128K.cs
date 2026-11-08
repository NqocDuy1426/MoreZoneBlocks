using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

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
            // 1. Nhóm hàm tính toán va chạm & mọc nhà của ZoneBlock
            MethodInfo m = AccessTools.Method(typeof(ZoneBlock), "CalculateBlock2");
            if (m != null) yield return m;
            m = AccessTools.Method(typeof(ZoneBlock), "CalculateBlock3");
            if (m != null) yield return m;
            m = AccessTools.Method(typeof(ZoneBlock), "IsGoodPlace");
            if (m != null) yield return m;
            m = AccessTools.Method(typeof(ZoneBlock), "SimulationStep");
            if (m != null) yield return m;

            // 2. Nhóm hàm kiểm tra không gian, lưới & địa hình của ZoneManager
            m = AccessTools.Method(typeof(ZoneManager), "CheckSpace");
            if (m != null) yield return m;
            m = AccessTools.Method(typeof(ZoneManager), "TerrainUpdated");
            if (m != null) yield return m;
            m = AccessTools.Method(typeof(ZoneManager), "UpdateBlocks", new Type[] { typeof(ColossalFramework.Math.Quad2) });
            if (m != null) yield return m;
            m = AccessTools.Method(typeof(ZoneManager), "ReleaseBlockImplementation", new Type[] { typeof(ushort), typeof(ZoneBlock).MakeByRefType() });
            if (m != null) yield return m;

            // 3. Nhóm hàm kiểm tra quy hoạch của Building & BuildingTool
            m = AccessTools.Method(typeof(Building), nameof(Building.CheckZoning));
            if (m != null) yield return m;
            m = AccessTools.Method(typeof(BuildingTool), "GetAllZoneBlocks");
            if (m != null) yield return m;
            m = AccessTools.Method(typeof(BuildingTool), "SimulationStep");
            if (m != null) yield return m;

            // 4. Nhóm hàm công cụ tô màu quy hoạch & hiển thị lưới (ZoneTool / DefaultTool)
            m = AccessTools.Method(typeof(ZoneTool), "ApplyZoning");
            if (m != null) yield return m;
            m = AccessTools.Method(typeof(ZoneTool), "ApplyBrush");
            if (m != null) yield return m;
            m = AccessTools.Method(typeof(ZoneTool), "ApplyFill");
            if (m != null) yield return m;
            m = AccessTools.Method(typeof(ZoneTool), "CalculateFillBuffer");
            if (m != null) yield return m;
            m = AccessTools.Method(typeof(DefaultTool), nameof(DefaultTool.RenderOverlay));
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
