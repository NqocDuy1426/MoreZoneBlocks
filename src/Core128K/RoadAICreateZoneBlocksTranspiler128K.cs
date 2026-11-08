using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ColossalFramework.Math;
using HarmonyLib;
using UnityEngine;

namespace MoreZoneBlocks.Core128K
{
    /// <summary>
    /// Mã Nguồn Lõi 5: Chuyển hướng các lệnh gọi CreateBlock trong RoadAI.CreateZoneBlocks
    /// sang hệ thống 17-bit Bit-Stashing mà giữ nguyên 100% tương thích với Zoning Adjuster!
    /// </summary>
    [HarmonyPatch(typeof(RoadAI), "CreateZoneBlocks")]
    public static class RoadAICreateZoneBlocksTranspiler128K
    {
        // 4 hàm bọc (Wrappers) chuẩn khớp 100% với 9 giá trị trên IL Evaluation Stack của Vanilla:
        public static bool CreateStartLeft128K(
            ZoneManager zm, ref NetSegment seg, ref Randomizer r,
            ushort segmentID, Vector3 pos, float angle, int rows, int dist, uint buildIdx)
        {
            if (NetSegmentZonePatches128K.CreateBlock128K(zm, out uint id32, ref r, segmentID, pos, angle, rows, dist, buildIdx))
            {
                BitStash128K.SetStartLeft32(ref seg, id32);
                return true;
            }
            return false;
        }

        public static bool CreateStartRight128K(
            ZoneManager zm, ref NetSegment seg, ref Randomizer r,
            ushort segmentID, Vector3 pos, float angle, int rows, int dist, uint buildIdx)
        {
            if (NetSegmentZonePatches128K.CreateBlock128K(zm, out uint id32, ref r, segmentID, pos, angle, rows, dist, buildIdx))
            {
                BitStash128K.SetStartRight32(ref seg, id32);
                return true;
            }
            return false;
        }

        public static bool CreateEndLeft128K(
            ZoneManager zm, ref NetSegment seg, ref Randomizer r,
            ushort segmentID, Vector3 pos, float angle, int rows, int dist, uint buildIdx)
        {
            if (NetSegmentZonePatches128K.CreateBlock128K(zm, out uint id32, ref r, segmentID, pos, angle, rows, dist, buildIdx))
            {
                BitStash128K.SetEndLeft32(ref seg, id32);
                return true;
            }
            return false;
        }

        public static bool CreateEndRight128K(
            ZoneManager zm, ref NetSegment seg, ref Randomizer r,
            ushort segmentID, Vector3 pos, float angle, int rows, int dist, uint buildIdx)
        {
            if (NetSegmentZonePatches128K.CreateBlock128K(zm, out uint id32, ref r, segmentID, pos, angle, rows, dist, buildIdx))
            {
                BitStash128K.SetEndRight32(ref seg, id32);
                return true;
            }
            return false;
        }

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);

            FieldInfo fStartLeft  = AccessTools.Field(typeof(NetSegment), nameof(NetSegment.m_blockStartLeft));
            FieldInfo fStartRight = AccessTools.Field(typeof(NetSegment), nameof(NetSegment.m_blockStartRight));
            FieldInfo fEndLeft    = AccessTools.Field(typeof(NetSegment), nameof(NetSegment.m_blockEndLeft));
            FieldInfo fEndRight   = AccessTools.Field(typeof(NetSegment), nameof(NetSegment.m_blockEndRight));

            MethodInfo mOrigCreate = AccessTools.Method(typeof(ZoneManager), nameof(ZoneManager.CreateBlock));

            MethodInfo mWrapSL = AccessTools.Method(typeof(RoadAICreateZoneBlocksTranspiler128K), nameof(CreateStartLeft128K));
            MethodInfo mWrapSR = AccessTools.Method(typeof(RoadAICreateZoneBlocksTranspiler128K), nameof(CreateStartRight128K));
            MethodInfo mWrapEL = AccessTools.Method(typeof(RoadAICreateZoneBlocksTranspiler128K), nameof(CreateEndLeft128K));
            MethodInfo mWrapER = AccessTools.Method(typeof(RoadAICreateZoneBlocksTranspiler128K), nameof(CreateEndRight128K));

            MethodInfo pendingReplacement = null;

            for (int i = 0; i < codes.Count; i++)
            {
                var inst = codes[i];

                // Khi gặp lệnh lấy địa chỉ trường ushort (ldflda m_blockStartLeft...),
                // ta đổi nó thành Nop để giữ nguyên con trỏ `ref NetSegment data` (ldarg.2) trên đỉnh Stack!
                if (inst.opcode == OpCodes.Ldflda)
                {
                    if (Equals(inst.operand, fStartLeft))
                    {
                        inst.opcode = OpCodes.Nop;
                        inst.operand = null;
                        pendingReplacement = mWrapSL;
                    }
                    else if (Equals(inst.operand, fStartRight))
                    {
                        inst.opcode = OpCodes.Nop;
                        inst.operand = null;
                        pendingReplacement = mWrapSR;
                    }
                    else if (Equals(inst.operand, fEndLeft))
                    {
                        inst.opcode = OpCodes.Nop;
                        inst.operand = null;
                        pendingReplacement = mWrapEL;
                    }
                    else if (Equals(inst.operand, fEndRight))
                    {
                        inst.opcode = OpCodes.Nop;
                        inst.operand = null;
                        pendingReplacement = mWrapER;
                    }
                }
                else if (pendingReplacement != null && 
                         (inst.opcode == OpCodes.Call || inst.opcode == OpCodes.Callvirt) && 
                         Equals(inst.operand, mOrigCreate))
                {
                    // Thay thế lời gọi ZoneManager.CreateBlock bằng Wrapper 17-bit tương ứng
                    inst.opcode = OpCodes.Call;
                    inst.operand = pendingReplacement;
                    pendingReplacement = null;
                }

                yield return inst;
            }
        }
    }
}
