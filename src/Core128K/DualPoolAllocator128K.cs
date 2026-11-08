using System;
using UnityEngine;

namespace MoreZoneBlocks.Core128K
{
    public static class DualPoolAllocator128K
    {
        // Ngăn 1: ID 1 .. 65535 (Làm Head cho m_zoneGrid & tương thích hàm ushort cũ)
        private static readonly ushort[] s_lowFreeList = new ushort[65536];
        private static int s_lowFreeCount = 0;

        // Ngăn 2: ID 65537 .. 131071 (Nối vào chuỗi liên kết sau Head; Slot 65536 làm Sentinel)
        private static readonly uint[] s_highFreeList = new uint[65536];
        private static int s_highFreeCount = 0;

        public static int LowFreeCount => s_lowFreeCount;
        public static int HighFreeCount => s_highFreeCount;
        public static int TotalFreeCount => s_lowFreeCount + s_highFreeCount;

        public static void RebuildFreeLists(ZoneBlock[] buffer)
        {
            if (buffer == null) return;

            s_lowFreeCount = 0;
            s_highFreeCount = 0;
            int activeCount = 0;

            // Quét dải thấp: 65535 ngược về 1
            int lowLimit = Math.Min(buffer.Length, 65536);
            for (int i = lowLimit - 1; i >= 1; i--)
            {
                if ((buffer[i].m_flags & ZoneBlock.FLAG_CREATED) == 0)
                    s_lowFreeList[s_lowFreeCount++] = (ushort)i;
                else
                    activeCount++;
            }

            // Quét dải cao: 131071 ngược về 65537 (Giữ slot 65536 làm Sentinel để low-16-bit không bao giờ bằng 0!)
            int highLimit = Math.Min(buffer.Length, BitStash128K.MAX_BLOCK_COUNT);
            for (int i = highLimit - 1; i >= 65537; i--)
            {
                if ((buffer[i].m_flags & ZoneBlock.FLAG_CREATED) == 0)
                    s_highFreeList[s_highFreeCount++] = (uint)i;
                else
                    activeCount++;
            }

            if (ZoneManager.exists)
            {
                ZoneManager.instance.m_blockCount = activeCount;
            }
        }

        /// <summary>
        /// Cấp phát thông minh:
        /// - Nếu ô lưới m_zoneGrid tại vị trí (position) đang trống (== 0): Bắt buộc lấy từ Low Pool (1..65535) để làm Head 16-bit!
        /// - Nếu ô lưới đã có Head (!= 0): Ưu tiên lấy từ High Pool (65537..131071) trước để dành Low Pool làm Head cho các ô đất khác!
        /// </summary>
        public static bool CreateBlockSmart32(out uint blockID, Vector3 position)
        {
            ZoneManager zm = ZoneManager.instance;
            if (zm == null || zm.m_zoneGrid == null)
            {
                blockID = 0u;
                return false;
            }

            int gridRes = (int)Mathf.Sqrt(zm.m_zoneGrid.Length); // 150 (Vanilla) hoặc 270 (81 Tiles 2)
            float halfGrid = gridRes * 0.5f;

            int gridX = Mathf.Clamp((int)(position.x / 64f + halfGrid), 0, gridRes - 1);
            int gridZ = Mathf.Clamp((int)(position.z / 64f + halfGrid), 0, gridRes - 1);
            int cellIndex = gridZ * gridRes + gridX;
            ushort currentHead = zm.m_zoneGrid[cellIndex];

            if (currentHead == 0)
            {
                // Ô lưới chưa có Head -> Bắt buộc lấy ID 16-bit (1..65535)
                if (s_lowFreeCount > 0)
                {
                    blockID = s_lowFreeList[--s_lowFreeCount];
                    zm.m_blockCount++;
                    return true;
                }
            }
            else
            {
                // Ô lưới đã có Head -> Ưu tiên rút cạn High Pool (65537..131071) trước!
                if (s_highFreeCount > 0)
                {
                    blockID = s_highFreeList[--s_highFreeCount];
                    zm.m_blockCount++;
                    return true;
                }
                if (s_lowFreeCount > 0)
                {
                    blockID = s_lowFreeList[--s_lowFreeCount];
                    zm.m_blockCount++;
                    return true;
                }
            }

            blockID = 0u;
            return false;
        }

        public static void ReleaseBlockId32(uint blockID)
        {
            if (blockID == 0 || blockID == 65536u) return; // Bảo lưu slot 0 và Sentinel 65536

            if (blockID < 65536u)
            {
                if (s_lowFreeCount < s_lowFreeList.Length)
                {
                    s_lowFreeList[s_lowFreeCount++] = (ushort)blockID;
                }
            }
            else if (blockID < (uint)BitStash128K.MAX_BLOCK_COUNT)
            {
                if (s_highFreeCount < s_highFreeList.Length)
                {
                    s_highFreeList[s_highFreeCount++] = blockID;
                }
            }

            if (ZoneManager.exists && ZoneManager.instance.m_blockCount > 0)
            {
                ZoneManager.instance.m_blockCount--;
            }
        }
    }
}
