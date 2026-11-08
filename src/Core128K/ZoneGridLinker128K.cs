using UnityEngine;

namespace MoreZoneBlocks.Core128K
{
    public static class ZoneGridLinker128K
    {
        public static void InitializeBlock32(ZoneManager zm, uint blockID, ref ZoneBlock data)
        {
            if (zm == null || zm.m_zoneGrid == null) return;

            int gridRes = (int)Mathf.Sqrt(zm.m_zoneGrid.Length); // Tự động nhận diện 150 (Vanilla) hoặc 270 (81 Tiles 2)
            float halfGrid = gridRes * 0.5f;

            int gridX = Mathf.Clamp((int)(data.m_position.x / 64f + halfGrid), 0, gridRes - 1);
            int gridZ = Mathf.Clamp((int)(data.m_position.z / 64f + halfGrid), 0, gridRes - 1);
            int cellIndex = gridZ * gridRes + gridX;

            ushort headID = zm.m_zoneGrid[cellIndex];

            if (blockID < 65536u)
            {
                // Block 16-bit: Chèn thẳng lên làm Head như Vanilla
                BitStash128K.SetNextGridBlock32(ref data, headID);
                zm.m_zoneGrid[cellIndex] = (ushort)blockID;
            }
            else
            {
                // Block 17-bit (65537..131071): Chèn ngay SAU Head (Tail-Chaining)
                // Để m_zoneGrid[cellIndex] luôn giữ nguyên kiểu ushort!
                if (headID != 0 && headID < zm.m_blocks.m_buffer.Length)
                {
                    ref ZoneBlock headBlock = ref zm.m_blocks.m_buffer[headID];
                    uint oldSecond = BitStash128K.GetNextGridBlock32(ref headBlock);
                    BitStash128K.SetNextGridBlock32(ref data, oldSecond);
                    BitStash128K.SetNextGridBlock32(ref headBlock, blockID);
                }
            }
        }

        /// <summary>
        /// Hàm ReleaseBlock32 hoàn chỉnh bọc ngoài UnlinkFromGrid32 (Chống lỗi Double-Clear)
        /// </summary>
        public static void ReleaseBlock32(ZoneManager zm, uint blockID, ref ZoneBlock data)
        {
            if (blockID == 0 || blockID >= BitStash128K.MAX_BLOCK_COUNT) return;
            if ((data.m_flags & ZoneBlock.FLAG_CREATED) == 0) return;

            // Nếu UnlinkFromGrid32 báo đã thực hiện In-Place Relocation vào chính slot blockID:
            // Slot blockID hiện đang chứa dữ liệu sống của node >= 65536 vừa chuyển về làm Head!
            // -> Thoát ngay, KHÔNG xóa data và KHÔNG trả blockID về FreeList!
            bool relocatedInPlace = UnlinkFromGrid32(zm, blockID, ref data);
            if (relocatedInPlace)
            {
                // Đánh dấu cập nhật lại block ở địa chỉ mới
                zm.m_updatedBlocks[blockID >> 6] |= 1UL << (int)(blockID & 63);
                zm.m_blocksUpdated = true;
                return;
            }

            // Trường hợp xóa bình thường:
            data = default(ZoneBlock);
            DualPoolAllocator128K.ReleaseBlockId32(blockID);
        }

        /// <summary>
        /// Tháo gỡ block khỏi lưới ô đất.
        /// Trả về true nếu đã thực hiện In-Place Relocation (di dời node 17-bit về slot 16-bit blockID).
        /// Khi trả về true, caller KHÔNG ĐƯỢC xóa data và KHÔNG ĐƯỢC giải phóng blockID về Free List!
        /// </summary>
        public static bool UnlinkFromGrid32(ZoneManager zm, uint blockID, ref ZoneBlock data)
        {
            if (zm == null || zm.m_zoneGrid == null) return false;

            int gridRes = (int)Mathf.Sqrt(zm.m_zoneGrid.Length);
            float halfGrid = gridRes * 0.5f;

            int gridX = Mathf.Clamp((int)(data.m_position.x / 64f + halfGrid), 0, gridRes - 1);
            int gridZ = Mathf.Clamp((int)(data.m_position.z / 64f + halfGrid), 0, gridRes - 1);
            int cellIndex = gridZ * gridRes + gridX;

            uint prevID = 0u;
            uint currID = zm.m_zoneGrid[cellIndex];
            int guard = 0;

            while (currID != 0u)
            {
                if (currID >= zm.m_blocks.m_buffer.Length) break;

                ref ZoneBlock currBlock = ref zm.m_blocks.m_buffer[currID];
                uint nextID = BitStash128K.GetNextGridBlock32(ref currBlock);

                if (currID == blockID)
                {
                    if (prevID == 0u)
                    {
                        // Xóa đúng Head của ô lưới
                        if (nextID < 65536u)
                        {
                            // Node kế tiếp là 16-bit: Gán thẳng làm Head mới
                            zm.m_zoneGrid[cellIndex] = (ushort)nextID;
                        }
                        else
                        {
                            // Node kế tiếp >= 65536: Thử tìm xem có node 16-bit nào ở sâu trong chuỗi không
                            ushort promotedLowID = PromoteLowNodeToHead(zm, nextID);
                            if (promotedLowID != 0)
                            {
                                zm.m_zoneGrid[cellIndex] = promotedLowID;
                            }
                            else
                            {
                                // Phía sau KHÔNG CÒN node 16-bit nào!
                                // Cứu chuỗi tức thì bằng In-Place Relocation:
                                // Di dời node nextID (>= 65536) thế chỗ vào đúng slot 16-bit vừa giải phóng (blockID)!
                                zm.m_zoneGrid[cellIndex] = RelocateHighNodeIntoReleasedLowHead(zm, (ushort)blockID, nextID);
                                return true; // Đã di dời thành công vào slot blockID, báo caller giữ nguyên slot!
                            }
                        }
                    }
                    else
                    {
                        BitStash128K.SetNextGridBlock32(ref zm.m_blocks.m_buffer[prevID], nextID);
                    }
                    break;
                }

                prevID = currID;
                currID = nextID;
                if (++guard > BitStash128K.MAX_BLOCK_COUNT) break;
            }

            BitStash128K.SetNextGridBlock32(ref data, 0u);
            return false;
        }

        /// <summary>
        /// Trường hợp tìm node 16-bit sâu trong chuỗi để đôn lên làm Head mới.
        /// </summary>
        private static ushort PromoteLowNodeToHead(ZoneManager zm, uint startHighID)
        {
            uint prev = startHighID;
            uint curr = BitStash128K.GetNextGridBlock32(ref zm.m_blocks.m_buffer[startHighID]);
            int guard = 0;

            while (curr != 0u && guard++ < BitStash128K.MAX_BLOCK_COUNT)
            {
                if (curr >= zm.m_blocks.m_buffer.Length) break;

                if (curr < 65536u)
                {
                    // Rút curr lên làm Head, nối đuôi của curr vào startHighID
                    uint afterCurr = BitStash128K.GetNextGridBlock32(ref zm.m_blocks.m_buffer[curr]);
                    BitStash128K.SetNextGridBlock32(ref zm.m_blocks.m_buffer[prev], afterCurr);
                    BitStash128K.SetNextGridBlock32(ref zm.m_blocks.m_buffer[curr], startHighID);
                    return (ushort)curr;
                }
                prev = curr;
                curr = BitStash128K.GetNextGridBlock32(ref zm.m_blocks.m_buffer[curr]);
            }
            return 0;
        }

        /// <summary>
        /// Giải pháp chống mồ côi chuỗi 17-bit khi xóa Head 16-bit duy nhất trong ô lưới:
        /// Di dời (Relocate) block >= 65536 kế tiếp vào thẳng slot 16-bit vừa giải phóng!
        /// </summary>
        private static ushort RelocateHighNodeIntoReleasedLowHead(ZoneManager zm, ushort releasedLowHeadID, uint highNextID)
        {
            ref ZoneBlock highBlock = ref zm.m_blocks.m_buffer[highNextID];
            uint afterHighID = BitStash128K.GetNextGridBlock32(ref highBlock);

            // 1. Copy toàn bộ dữ liệu của highBlock sang vị trí releasedLowHeadID
            zm.m_blocks.m_buffer[releasedLowHeadID] = highBlock;
            BitStash128K.SetNextGridBlock32(ref zm.m_blocks.m_buffer[releasedLowHeadID], afterHighID);

            // 2. Cập nhật lại con trỏ trên NetSegment đang sở hữu highNextID sang địa chỉ mới (releasedLowHeadID)
            ushort segID = highBlock.m_segment;
            if (segID != 0 && NetManager.exists && segID < NetManager.instance.m_segments.m_buffer.Length)
            {
                ref NetSegment seg = ref NetManager.instance.m_segments.m_buffer[segID];
                if (BitStash128K.GetStartLeft32(ref seg) == highNextID)  BitStash128K.SetStartLeft32(ref seg, releasedLowHeadID);
                if (BitStash128K.GetStartRight32(ref seg) == highNextID) BitStash128K.SetStartRight32(ref seg, releasedLowHeadID);
                if (BitStash128K.GetEndLeft32(ref seg) == highNextID)    BitStash128K.SetEndLeft32(ref seg, releasedLowHeadID);
                if (BitStash128K.GetEndRight32(ref seg) == highNextID)   BitStash128K.SetEndRight32(ref seg, releasedLowHeadID);
            }

            // 3. Xóa trắng slot highNextID cũ và trả highNextID về High Pool
            highBlock = default(ZoneBlock);
            DualPoolAllocator128K.ReleaseBlockId32(highNextID);

            // Trả về chính releasedLowHeadID làm Head mới của ô lưới!
            return releasedLowHeadID;
        }
    }
}
