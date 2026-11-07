using System;
using System.Reflection;
using ColossalFramework;

namespace MoreZoneBlocks
{
    public static class ZoneCustomizer
    {
        public const int VANILLA_BLOCK_COUNT = 49152;
        public const int EXPANDED_BLOCK_COUNT = 65536;
        public const int EXPANDED_LIMIT = 65535;
        public const int VANILLA_UPDATED_BLOCKS = 768;
        public const int EXPANDED_UPDATED_BLOCKS = 1024;
        public const int WARNING_BLOCK_THRESHOLD = 65024; // 65536 - 512

        private static readonly FieldInfo s_unusedCountField = typeof(Array16<ZoneBlock>).GetField("m_unusedCount", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo s_unusedItemsField = typeof(Array16<ZoneBlock>).GetField("m_unusedItems", BindingFlags.NonPublic | BindingFlags.Instance);

        public static bool IsExpanded(ZoneManager zm)
        {
            if (zm == null || zm.m_blocks == null || zm.m_blocks.m_buffer == null)
                return false;
            return zm.m_blocks.m_buffer.Length >= EXPANDED_BLOCK_COUNT;
        }

        public static bool EnsureExpanded(ZoneManager zm)
        {
            if (zm == null)
            {
                Log.Error("EnsureExpanded: ZoneManager is null!");
                return false;
            }

            if (IsExpanded(zm))
            {
                return true;
            }

            Log.Info("Expanding ZoneManager memory buffer from 48K to 64K (65,536 elements)...");
            return ExpandPreservingLoadedData(zm);
        }

        public static bool ExpandPreservingLoadedData(ZoneManager zm)
        {
            try
            {
                if (zm == null) return false;
                if (IsExpanded(zm)) return true;

                Array16<ZoneBlock> oldBlocks = zm.m_blocks;
                int oldLength = oldBlocks != null && oldBlocks.m_buffer != null ? oldBlocks.m_buffer.Length : 0;

                // 1. Create new expanded Array16
                Array16<ZoneBlock> newBlocks = new Array16<ZoneBlock>((uint)EXPANDED_BLOCK_COUNT);

                // 2. Copy existing blocks
                if (oldBlocks != null && oldBlocks.m_buffer != null)
                {
                    int copyCount = Math.Min(oldLength, EXPANDED_BLOCK_COUNT);
                    Array.Copy(oldBlocks.m_buffer, newBlocks.m_buffer, copyCount);
                }

                // 3. Rebuild free list (m_unusedItems)
                RebuildFreeList(newBlocks);

                // 4. Expand m_updatedBlocks (ulong[1024])
                ulong[] newUpdated = new ulong[EXPANDED_UPDATED_BLOCKS];
                if (zm.m_updatedBlocks != null)
                {
                    int copyUpdated = Math.Min(zm.m_updatedBlocks.Length, EXPANDED_UPDATED_BLOCKS);
                    Array.Copy(zm.m_updatedBlocks, newUpdated, copyUpdated);
                }

                // 5. Assign back to ZoneManager
                zm.m_blocks = newBlocks;
                zm.m_updatedBlocks = newUpdated;

                // 6. Update m_blockCount accurately
                UpdateBlockCount(zm);

                uint freeCount = GetUnusedCount(newBlocks);
                Log.Info(string.Format("Successfully expanded ZoneManager to 65,536 blocks! Active count: {0}, Free slots: {1}",
                    zm.m_blockCount, freeCount));
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "ExpandPreservingLoadedData");
                return false;
            }
        }

        public static void RebuildFreeList(Array16<ZoneBlock> blocks)
        {
            if (blocks == null || blocks.m_buffer == null)
                return;

            ushort[] unusedItems = GetUnusedItems(blocks);
            if (unusedItems == null) return;

            uint freeCount = 0;
            // Iterate from 1 to 65535 using integer to avoid ushort wrap-around trap
            for (int i = 1; i < EXPANDED_BLOCK_COUNT; i++)
            {
                if ((blocks.m_buffer[i].m_flags & ZoneBlock.FLAG_CREATED) == 0)
                {
                    unusedItems[freeCount] = (ushort)i;
                    freeCount++;
                }
            }

            SetUnusedCount(blocks, freeCount);
            if (freeCount < unusedItems.Length)
            {
                unusedItems[freeCount] = 0;
            }
        }

        public static ushort[] GetUnusedItems(Array16<ZoneBlock> blocks)
        {
            if (blocks == null || s_unusedItemsField == null) return null;
            return s_unusedItemsField.GetValue(blocks) as ushort[];
        }

        public static uint GetUnusedCount(Array16<ZoneBlock> blocks)
        {
            if (blocks == null || s_unusedCountField == null) return 0;
            return (uint)s_unusedCountField.GetValue(blocks);
        }

        public static void SetUnusedCount(Array16<ZoneBlock> blocks, uint count)
        {
            if (blocks == null || s_unusedCountField == null) return;
            s_unusedCountField.SetValue(blocks, count);
        }

        public static void UpdateBlockCount(ZoneManager zm)
        {
            if (zm == null || zm.m_blocks == null) return;
            // ItemCount() returns m_size - m_unusedCount. Slot 0 is reserved, so subtract 1.
            uint count = zm.m_blocks.ItemCount();
            zm.m_blockCount = count > 0 ? (int)(count - 1) : 0;
        }
    }
}
