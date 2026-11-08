using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace MoreZoneBlocks.Core128K
{
    /// <summary>
    /// Mã Nguồn Lõi 7: Điều phối viên 128K cho ZoneManager.Awake và SimulationStepImpl.
    /// Đảm bảo toàn bộ 131.072 ZoneBlock đều được tính toán hình học, cập nhật lưới và mọc nhà!
    /// </summary>
    [HarmonyPatch]
    public static class ZoneUpdateAndSim128K
    {
        public const int VANILLA_MAX_BLOCKS = 49152;
        public const int VANILLA_UPDATED_LEN = 768;
        public const int EXTENDED_UPDATED_LEN = 2048; // 131072 / 64

        // Lưu trữ ID 32-bit thực sự của block đang được thực thi trong luồng hiện tại
        // Giúp bên trong CalculateBlock2 biết chính xác ID 17-bit của `this` mà không sợ bị cắt 16-bit!
        [ThreadStatic]
        public static uint CurrentExecutingBlockID32;

        public static uint GetCurrentBlockID32(ushort fallbackID)
        {
            return CurrentExecutingBlockID32 != 0 ? CurrentExecutingBlockID32 : (uint)fallbackID;
        }

        /// <summary>
        /// 1. Postfix ZoneManager.Awake: Nâng cấp m_blocks.m_buffer lên 131.072 và m_updatedBlocks lên 2048
        /// </summary>
        [HarmonyPatch(typeof(ZoneManager), "Awake")]
        [HarmonyPostfix]
        public static void Awake_Postfix(ZoneManager __instance)
        {
            if (__instance == null || __instance.m_blocks == null) return;

            // Mở rộng m_buffer bên trong Array16<ZoneBlock> lên 131.072 phần tử
            ZoneBlock[] newBuffer = new ZoneBlock[BitStash128K.MAX_BLOCK_COUNT];
            if (__instance.m_blocks.m_buffer != null)
            {
                Array.Copy(
                    __instance.m_blocks.m_buffer,
                    newBuffer,
                    Math.Min(__instance.m_blocks.m_buffer.Length, BitStash128K.MAX_BLOCK_COUNT)
                );
            }
            __instance.m_blocks.m_buffer = newBuffer;

            // Cập nhật m_size nội bộ của Array16
            FieldInfo fSize = AccessTools.Field(typeof(Array16<ZoneBlock>), "m_size");
            fSize?.SetValue(__instance.m_blocks, (uint)BitStash128K.MAX_BLOCK_COUNT);

            // Mở rộng m_updatedBlocks lên 2048 ulongs (131.072 bits)
            __instance.m_updatedBlocks = new ulong[EXTENDED_UPDATED_LEN];

            // Khởi tạo Dual-Pool Free Lists (1..65535 và 65537..131071, bỏ qua Sentinel 65536)
            DualPoolAllocator128K.RebuildFreeLists(newBuffer);
        }

        /// <summary>
        /// 2. Prefix SimulationStepImpl:
        /// Thực thi cập nhật 4 pha 128K cho m_updatedBlocks trước, sau đó xóa m_blocksUpdated = false
        /// để vanilla không bao giờ chạy vòng lặp 16-bit bị cắt số của nó!
        /// </summary>
        [HarmonyPatch(typeof(ZoneManager), "SimulationStepImpl")]
        [HarmonyPrefix]
        public static void SimulationStepImpl_Prefix(ZoneManager __instance)
        {
            if (__instance == null) return;

            if (__instance.m_blocksUpdated)
            {
                UpdateAllBlocks128K(__instance);
                __instance.m_blocksUpdated = false; // Ngăn vanilla chạy vòng lặp 16-bit
            }
        }

        /// <summary>
        /// Quét trọn vẹn 2048 ulongs (131.072 blocks) qua 4 pha tính toán hình học chuẩn 32-bit
        /// </summary>
        public static void UpdateAllBlocks128K(ZoneManager zm)
        {
            ZoneBlock[] buffer = zm.m_blocks.m_buffer;
            ulong[] updated = zm.m_updatedBlocks;
            if (buffer == null || updated == null) return;

            int len = updated.Length;

            // Pha 1: CalculateBlock1
            for (int i = 0; i < len; i++)
            {
                ulong mask = updated[i];
                if (mask != 0uL)
                {
                    for (int bit = 0; bit < 64; bit++)
                    {
                        if ((mask & (1UL << bit)) != 0uL)
                        {
                            uint blockID = (uint)((i << 6) | bit);
                            if (blockID < buffer.Length)
                            {
                                CurrentExecutingBlockID32 = blockID;
                                buffer[blockID].CalculateBlock1((ushort)blockID);
                            }
                        }
                    }
                }
            }

            // Pha 2: CalculateBlock2 (Tính va chạm chồng lấn giữa các block trong cùng ô lưới)
            for (int i = 0; i < len; i++)
            {
                ulong mask = updated[i];
                if (mask != 0uL)
                {
                    for (int bit = 0; bit < 64; bit++)
                    {
                        if ((mask & (1UL << bit)) != 0uL)
                        {
                            uint blockID = (uint)((i << 6) | bit);
                            if (blockID < buffer.Length)
                            {
                                CurrentExecutingBlockID32 = blockID;
                                buffer[blockID].CalculateBlock2((ushort)blockID);
                            }
                        }
                    }
                }
            }

            // Pha 3: CalculateBlock3
            for (int i = 0; i < len; i++)
            {
                ulong mask = updated[i];
                if (mask != 0uL)
                {
                    for (int bit = 0; bit < 64; bit++)
                    {
                        if ((mask & (1UL << bit)) != 0uL)
                        {
                            uint blockID = (uint)((i << 6) | bit);
                            if (blockID < buffer.Length)
                            {
                                CurrentExecutingBlockID32 = blockID;
                                buffer[blockID].CalculateBlock3((ushort)blockID);
                            }
                        }
                    }
                }
            }

            // Pha 4: UpdateBlock & xóa cờ m_updatedBlocks
            for (int i = 0; i < len; i++)
            {
                ulong mask = updated[i];
                if (mask != 0uL)
                {
                    updated[i] = 0uL;
                    for (int bit = 0; bit < 64; bit++)
                    {
                        if ((mask & (1UL << bit)) != 0uL)
                        {
                            uint blockID = (uint)((i << 6) | bit);
                            if (blockID < buffer.Length)
                            {
                                CurrentExecutingBlockID32 = blockID;
                                buffer[blockID].UpdateBlock((ushort)blockID);
                            }
                        }
                    }
                }
            }

            CurrentExecutingBlockID32 = 0u;
        }

        /// <summary>
        /// 3. Transpiler cho ZoneManager.SimulationStepImpl:
        /// Đổi bước nhảy từ 48 (ldc.i4.s 48) sang 128 (ldc.i4 128) để quét đủ 131.072 blocks trong 1024 frames!
        /// </summary>
        [HarmonyPatch(typeof(ZoneManager), "SimulationStepImpl")]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> SimulationStepImpl_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var inst in instructions)
            {
                // Bắt buộc đổi cả OpCode từ Ldc_I4_S (sbyte) sang Ldc_I4 (int32) vì 128 tràn số dương sbyte!
                if (inst.opcode == OpCodes.Ldc_I4_S && Convert.ToInt32(inst.operand) == 48)
                {
                    inst.opcode = OpCodes.Ldc_I4;
                    inst.operand = 128; // 128 blocks/frame * 1024 frames = 131.072 blocks
                }
                yield return inst;
            }
        }
        /// <summary>
        /// 4. Postfix cho SimulationStepImpl:
        /// Kích hoạt Auto-GC định kỳ trên Simulation Thread
        /// </summary>
        [HarmonyPatch(typeof(ZoneManager), "SimulationStepImpl")]
        [HarmonyPostfix]
        public static void SimulationStepImpl_Postfix(int subStep)
        {
            if (subStep == 0)
            {
                ZoneGarbageCollector.OnSimulationStep();
            }
        }
    }
}
