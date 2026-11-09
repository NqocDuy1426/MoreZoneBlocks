using System;
using ColossalFramework;
using ColossalFramework.Math;
using HarmonyLib;
using UnityEngine;

namespace MoreZoneBlocks.Core128K
{
    [HarmonyPatch]
    public static class NetSegmentZonePatches128K
    {
        /// <summary>
        /// 1. Tạo ZoneBlock 17-bit (1..131071) cho đoạn đường mới và lưu bit thứ 17 vào NetSegment.m_flags2
        /// </summary>
        public static bool CreateBlock128K(
            ZoneManager zm,
            out uint blockID,
            ref Randomizer r,
            ushort segmentID,
            Vector3 position,
            float angle,
            int rows,
            int distance,
            uint buildIndex)
        {
            if (!DualPoolAllocator128K.CreateBlockSmart32(out blockID, position))
            {
                return false;
            }

            if (rows > 8) rows = 8;

            ref ZoneBlock block = ref zm.m_blocks.m_buffer[blockID];
            block.m_flags = ZoneBlock.FLAG_CREATED;
            block.RowCount = rows;
            block.Distance = distance;
            block.m_buildIndex = buildIndex;
            block.m_position = position;
            block.m_angle = angle;
            block.m_segment = segmentID;
            block.m_valid = 0uL;
            block.m_shared = 0uL;
            block.m_occupied1 = 0uL;
            block.m_occupied2 = 0uL;
            block.m_zone1 = 0uL;
            block.m_zone2 = 0uL;
            BitStash128K.SetNextGridBlock32(ref block, 0u);

            // Chèn vào m_zoneGrid thông qua cơ chế Dual-Pool + Tail-Chaining
            ZoneGridLinker128K.InitializeBlock32(zm, blockID, ref block);

            // Đánh dấu bitmask cập nhật (m_updatedBlocks[2048])
            zm.m_updatedBlocks[blockID >> 6] |= 1UL << (int)(blockID & 63);
            zm.m_blocksUpdated = true;

            // Cập nhật thống kê khu vực nếu DistrictManager tồn tại
            if (DistrictManager.exists)
            {
                DistrictManager.instance.ZonesUpdated(ref block);
            }

            return true;
        }

        /// <summary>
        /// 2. Khi xóa đoạn đường (Bulldoze): Giải phóng cả 4 ZoneBlock 17-bit và xóa sạch 4 bit cao trong m_flags2
        /// Chú ý: Hàm này thuộc NetManager trong Assembly-CSharp.dll
        /// </summary>
        [HarmonyPatch(typeof(NetManager), "ReleaseSegmentImplementation", new Type[] { typeof(ushort), typeof(NetSegment), typeof(bool) }, new ArgumentType[] { ArgumentType.Normal, ArgumentType.Ref, ArgumentType.Normal })]
        [HarmonyPrefix]
        public static void ReleaseSegmentImplementation_Prefix(ushort segment, ref NetSegment data, bool keepNodes)
        {
            ZoneManager zm = ZoneManager.instance;
            if (zm == null || zm.m_blocks.m_buffer == null) return;

            uint b1 = BitStash128K.GetStartLeft32(ref data);
            if (b1 != 0u && b1 < zm.m_blocks.m_buffer.Length)
            {
                ZoneGridLinker128K.ReleaseBlock32(zm, b1, ref zm.m_blocks.m_buffer[b1]);
                BitStash128K.SetStartLeft32(ref data, 0u);
            }

            uint b2 = BitStash128K.GetStartRight32(ref data);
            if (b2 != 0u && b2 < zm.m_blocks.m_buffer.Length)
            {
                ZoneGridLinker128K.ReleaseBlock32(zm, b2, ref zm.m_blocks.m_buffer[b2]);
                BitStash128K.SetStartRight32(ref data, 0u);
            }

            uint b3 = BitStash128K.GetEndLeft32(ref data);
            if (b3 != 0u && b3 < zm.m_blocks.m_buffer.Length)
            {
                ZoneGridLinker128K.ReleaseBlock32(zm, b3, ref zm.m_blocks.m_buffer[b3]);
                BitStash128K.SetEndLeft32(ref data, 0u);
            }

            uint b4 = BitStash128K.GetEndRight32(ref data);
            if (b4 != 0u && b4 < zm.m_blocks.m_buffer.Length)
            {
                ZoneGridLinker128K.ReleaseBlock32(zm, b4, ref zm.m_blocks.m_buffer[b4]);
                BitStash128K.SetEndRight32(ref data, 0u);
            }
        }

        /// <summary>
        /// 3. Định tuyến toàn bộ các lệnh gọi ZoneManager.ReleaseBlockImplementation sang ReleaseBlock32
        /// </summary>
        [HarmonyPatch(typeof(ZoneManager), "ReleaseBlockImplementation", new Type[] { typeof(ushort), typeof(ZoneBlock) }, new ArgumentType[] { ArgumentType.Normal, ArgumentType.Ref })]
        [HarmonyPrefix]
        public static bool ReleaseBlockImplementation_Prefix(ZoneManager __instance, ushort block, ref ZoneBlock data)
        {
            if (__instance != null)
            {
                ZoneGridLinker128K.ReleaseBlock32(__instance, block, ref data);
            }
            return false; // Chặn hàm 16-bit gốc
        }

        /// <summary>
        /// 3b. Mở rộng giới hạn ZoneBlock kiểm tra bởi RoadAI.CheckBuildPosition từ 48.640 lên 131.072 (128K)
        /// Ngăn chặn triệt để lỗi không thể đặt đường và cầu khi số lượng ZoneBlock kế thừa từ save cũ vượt quá 48.640!
        /// </summary>
        [HarmonyPatch(typeof(ZoneManager), nameof(ZoneManager.CheckLimits))]
        [HarmonyPrefix]
        public static bool CheckLimits_Prefix(ZoneManager __instance, ref bool __result)
        {
            if (__instance == null)
            {
                __result = false;
                return false;
            }
            // 128K: Cho phép xây đường khi tổng số slot trống > 0 và blockCount chưa chạm trần 130.560
            __result = DualPoolAllocator128K.TotalFreeCount > 0 && __instance.m_blockCount < (BitStash128K.MAX_BLOCK_COUNT - 512);
            return false; // Bỏ qua hoàn toàn giới hạn 48.640 cũ của Vanilla
        }

        /// <summary>
        /// 3c. Định tuyến mọi lời gọi ZoneManager.CreateBlock truyền thống (16-bit) sang bộ cấp phát DualPool 128K
        /// </summary>
        [HarmonyPatch(typeof(ZoneManager), nameof(ZoneManager.CreateBlock))]
        [HarmonyPrefix]
        public static bool CreateBlock_Prefix(
            ZoneManager __instance,
            out ushort block,
            ref Randomizer randomizer,
            ushort segmentID,
            Vector3 position,
            float angle,
            int rows,
            int distance,
            uint buildIndex,
            ref bool __result)
        {
            if (__instance != null && CreateBlock128K(__instance, out uint id32, ref randomizer, segmentID, position, angle, rows, distance, buildIndex))
            {
                block = (ushort)id32;
                __result = true;
                return false;
            }
            block = 0;
            __result = false;
            return false;
        }

        /// <summary>
        /// 4. Khi cập nhật vùng quy hoạch quanh đoạn đường (NetSegment.UpdateZones):
        /// Đánh dấu cập nhật đúng vị trí trên mảng m_updatedBlocks[2048] cho cả các block >= 65536
        /// Đồng thời đảm bảo dọn dẹp Prop và Tree bên dưới đoạn đường/cầu
        /// </summary>
        [HarmonyPatch(typeof(NetSegment), nameof(NetSegment.UpdateZones))]
        [HarmonyPrefix]
        public static bool UpdateZones_Prefix(ushort segmentID, ref NetSegment __instance)
        {
            if ((__instance.m_flags & NetSegment.Flags.Created) == 0) return false;

            NetInfo info = __instance.Info;
            if (info == null || info.m_class == null || info.m_class.m_layer != ItemClass.Layer.Default) return false;

            ZoneManager zm = ZoneManager.instance;
            if (zm == null || zm.m_blocks.m_buffer == null) return false;

            Vector3 min = __instance.m_bounds.min;
            Vector3 max = __instance.m_bounds.max;

            // 1. Cập nhật các khối quy hoạch 128K trong phạm vi đoạn đường
            UpdateBlocksInArea128K(zm, min.x - 46f, min.z - 46f, max.x + 46f, max.z + 46f);

            // 2. Bảo toàn việc dọn dẹp Props và Trees bên dưới đường và cầu
            if (PropManager.exists)
            {
                PropManager.instance.UpdateProps(min.x, min.z, max.x, max.z);
            }
            if (TreeManager.exists)
            {
                TreeManager.instance.UpdateTrees(min.x, min.z, max.x, max.z);
            }

            return false; // Chặn hàm 16-bit gốc
        }

        private static void AccumulateBlockBounds(ZoneManager zm, uint blockID, ref Vector3 min, ref Vector3 max, ref bool hasBlocks)
        {
            if (blockID == 0u || blockID >= zm.m_blocks.m_buffer.Length) return;
            Vector3 pos = zm.m_blocks.m_buffer[blockID].m_position;
            min = Vector3.Min(min, pos);
            max = Vector3.Max(max, pos);
            hasBlocks = true;
        }

        /// <summary>
        /// Duyệt chuỗi liên kết 17-bit trong vùng không gian (minX, minZ, maxX, maxZ) và bật cờ m_updatedBlocks
        /// </summary>
        public static void UpdateBlocksInArea128K(ZoneManager zm, float minX, float minZ, float maxX, float maxZ)
        {
            if (zm == null || zm.m_zoneGrid == null) return;

            int gridRes = (int)Mathf.Sqrt(zm.m_zoneGrid.Length);
            float halfGrid = gridRes * 0.5f;

            int startX = Mathf.Max((int)(minX / 64f + halfGrid), 0);
            int startZ = Mathf.Max((int)(minZ / 64f + halfGrid), 0);
            int endX   = Mathf.Min((int)(maxX / 64f + halfGrid), gridRes - 1);
            int endZ   = Mathf.Min((int)(maxZ / 64f + halfGrid), gridRes - 1);

            for (int z = startZ; z <= endZ; z++)
            {
                for (int x = startX; x <= endX; x++)
                {
                    uint currID = zm.m_zoneGrid[z * gridRes + x];
                    int guard = 0;

                    while (currID != 0u)
                    {
                        if (currID >= zm.m_blocks.m_buffer.Length) break;

                        ref ZoneBlock b = ref zm.m_blocks.m_buffer[currID];
                        Vector3 p = b.m_position;

                        float dist = Mathf.Max(
                            Mathf.Max(minX - 42f - p.x, minZ - 42f - p.z),
                            Mathf.Max(p.x - maxX - 42f, p.z - maxZ - 42f)
                        );

                        if (dist < 0f)
                        {
                            zm.m_updatedBlocks[currID >> 6] |= 1UL << (int)(currID & 63);
                            zm.m_blocksUpdated = true;
                        }

                        currID = BitStash128K.GetNextGridBlock32(ref b);
                        if (++guard > BitStash128K.MAX_BLOCK_COUNT) break;
                    }
                }
            }
        }
    }
}
