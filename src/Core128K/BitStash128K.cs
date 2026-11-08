namespace MoreZoneBlocks.Core128K
{
    public static class BitStash128K
    {
        public const int MAX_BLOCK_COUNT = 131072; // 128K (131,072)
        public const uint HIGH_BIT_VALUE = 0x00010000u; // 65536
        public const uint LOW_16_MASK    = 0x0000FFFFu; // 65535

        // Cờ Bit thứ 17 trong ZoneBlock.m_flags dành cho m_nextGridBlock
        public const uint ZB_NEXT_HIGH_BIT = 0x00010000u; // Bit 16 (Giá trị đúng bằng 65536)

        // Cờ Bit thứ 17 trong NetSegment.m_flags2 dành cho 4 slot ZoneBlock
        public const NetSegment.Flags2 SEG_START_LEFT_HIGH  = (NetSegment.Flags2)0x10000000; // Bit 28
        public const NetSegment.Flags2 SEG_START_RIGHT_HIGH = (NetSegment.Flags2)0x20000000; // Bit 29
        public const NetSegment.Flags2 SEG_END_LEFT_HIGH    = (NetSegment.Flags2)0x40000000; // Bit 30
        public const NetSegment.Flags2 SEG_END_RIGHT_HIGH   = (NetSegment.Flags2)unchecked((int)0x80000000u); // Bit 31
        public const NetSegment.Flags2 SEG_ALL_HIGH_MASK    = (NetSegment.Flags2)unchecked((int)0xF0000000u);

        // =====================================================================
        // 1. ĐỌC / GHI CON TRỎ 17-BIT TRONG ZONEBLOCK (m_nextGridBlock)
        // =====================================================================

        public static uint GetNextGridBlock32(ref ZoneBlock block)
        {
            return (uint)block.m_nextGridBlock | (block.m_flags & ZB_NEXT_HIGH_BIT);
        }

        public static void SetNextGridBlock32(ref ZoneBlock block, uint nextBlockID)
        {
            block.m_nextGridBlock = (ushort)(nextBlockID & LOW_16_MASK);
            if (nextBlockID >= HIGH_BIT_VALUE)
                block.m_flags |= ZB_NEXT_HIGH_BIT;
            else
                block.m_flags &= ~ZB_NEXT_HIGH_BIT;
        }

        // =====================================================================
        // 2. ĐỌC / GHI 4 CON TRỎ 17-BIT TRONG NETSEGMENT (CHỐNG TRÔI BIT DẤU SIGN-EXTENSION)
        // =====================================================================

        public static uint GetStartLeft32(ref NetSegment seg)
        {
            return (uint)seg.m_blockStartLeft | ((((uint)seg.m_flags2) & 0x10000000u) >> 12);
        }

        public static void SetStartLeft32(ref NetSegment seg, uint blockID)
        {
            seg.m_blockStartLeft = (ushort)(blockID & LOW_16_MASK);
            if (blockID >= HIGH_BIT_VALUE) seg.m_flags2 |= SEG_START_LEFT_HIGH;
            else seg.m_flags2 = (NetSegment.Flags2)((uint)seg.m_flags2 & ~0x10000000u);
        }

        public static uint GetStartRight32(ref NetSegment seg)
        {
            return (uint)seg.m_blockStartRight | ((((uint)seg.m_flags2) & 0x20000000u) >> 13);
        }

        public static void SetStartRight32(ref NetSegment seg, uint blockID)
        {
            seg.m_blockStartRight = (ushort)(blockID & LOW_16_MASK);
            if (blockID >= HIGH_BIT_VALUE) seg.m_flags2 |= SEG_START_RIGHT_HIGH;
            else seg.m_flags2 = (NetSegment.Flags2)((uint)seg.m_flags2 & ~0x20000000u);
        }

        public static uint GetEndLeft32(ref NetSegment seg)
        {
            return (uint)seg.m_blockEndLeft | ((((uint)seg.m_flags2) & 0x40000000u) >> 14);
        }

        public static void SetEndLeft32(ref NetSegment seg, uint blockID)
        {
            seg.m_blockEndLeft = (ushort)(blockID & LOW_16_MASK);
            if (blockID >= HIGH_BIT_VALUE) seg.m_flags2 |= SEG_END_LEFT_HIGH;
            else seg.m_flags2 = (NetSegment.Flags2)((uint)seg.m_flags2 & ~0x40000000u);
        }

        public static uint GetEndRight32(ref NetSegment seg)
        {
            // BẮT BUỘC ép kiểu (uint)seg.m_flags2 trước khi AND 0x80000000u để tránh Sign-Extension
            // trên kiểu enum có underlying type là int (signed 32-bit)!
            return (uint)seg.m_blockEndRight | ((((uint)seg.m_flags2) & 0x80000000u) >> 15);
        }

        public static void SetEndRight32(ref NetSegment seg, uint blockID)
        {
            seg.m_blockEndRight = (ushort)(blockID & LOW_16_MASK);
            if (blockID >= HIGH_BIT_VALUE) seg.m_flags2 |= SEG_END_RIGHT_HIGH;
            else seg.m_flags2 = (NetSegment.Flags2)((uint)seg.m_flags2 & ~0x80000000u);
        }
    }
}
