using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ColossalFramework;
using HarmonyLib;
using UnityEngine;

namespace MoreZoneBlocks.Core128K
{
    /// <summary>
    /// Mã Nguồn Lõi 8 (Đã vá chuẩn Lifecycle & Tương thích ngược v1.0):
    /// Hệ thống lưu/nạp Two-Stream 128K.
    /// Đảm bảo Vanilla Stream luôn chỉ nhìn thấy đúng 49.152 phần tử sạch sẽ.
    /// </summary>
    [HarmonyPatch]
    public static class SaveSanitizer128K
    {
        public const string SIDECAR_KEY = "MoreZoneBlocks_Extended_128K";
        public const uint SIDECAR_MAGIC = 0x4D5A4232; // "MZB2"
        public const uint SIDECAR_VERSION = 2;

        // Tương thích ngược với Savegame v1.0 (64K)
        public const string LEGACY_SIDECAR_KEY = "MoreZoneBlocks_Extended";
        public const uint LEGACY_SIDECAR_MAGIC = 0x4D5A4231; // "MZB1"

        public struct SegmentBlockMapping128K
        {
            public ushort SegmentID;
            public byte Slot; // 0=SL, 1=SR, 2=EL, 3=ER
            public uint HighBlockID;
        }

        private static readonly List<SegmentBlockMapping128K> s_savedMappings = new List<SegmentBlockMapping128K>(8192);
        private static ZoneBlock[] s_fullBufferBackup128K;

        // =====================================================================
        // GIAI ĐOẠN LƯU GAME (SAVE PIPELINE)
        // =====================================================================

        /// <summary>
        /// S1. Prefix NetManager.Data.Serialize:
        /// Thu thập các slot >= 49.152 và tạm xóa về 0 (kèm xóa 4 cờ bit cao trong m_flags2)
        /// </summary>
        [HarmonyPatch(typeof(NetManager.Data), "Serialize")]
        [HarmonyPrefix]
        public static void NetManager_Serialize_Prefix()
        {
            s_savedMappings.Clear();
            NetManager nm = NetManager.instance;
            if (nm?.m_segments?.m_buffer == null) return;

            NetSegment[] segs = nm.m_segments.m_buffer;
            int count = segs.Length;

            for (int i = 1; i < count; i++)
            {
                ushort segID = (ushort)i;
                if ((segs[segID].m_flags & NetSegment.Flags.Created) == 0) continue;

                uint sl = BitStash128K.GetStartLeft32(ref segs[segID]);
                if (sl >= 49152u)
                {
                    s_savedMappings.Add(new SegmentBlockMapping128K { SegmentID = segID, Slot = 0, HighBlockID = sl });
                    BitStash128K.SetStartLeft32(ref segs[segID], 0u);
                }

                uint sr = BitStash128K.GetStartRight32(ref segs[segID]);
                if (sr >= 49152u)
                {
                    s_savedMappings.Add(new SegmentBlockMapping128K { SegmentID = segID, Slot = 1, HighBlockID = sr });
                    BitStash128K.SetStartRight32(ref segs[segID], 0u);
                }

                uint el = BitStash128K.GetEndLeft32(ref segs[segID]);
                if (el >= 49152u)
                {
                    s_savedMappings.Add(new SegmentBlockMapping128K { SegmentID = segID, Slot = 2, HighBlockID = el });
                    BitStash128K.SetEndLeft32(ref segs[segID], 0u);
                }

                uint er = BitStash128K.GetEndRight32(ref segs[segID]);
                if (er >= 49152u)
                {
                    s_savedMappings.Add(new SegmentBlockMapping128K { SegmentID = segID, Slot = 3, HighBlockID = er });
                    BitStash128K.SetEndRight32(ref segs[segID], 0u);
                }
            }
        }

        /// <summary>
        /// S2. Prefix ZoneManager.Data.Serialize:
        /// Tráo tạm m_blocks.m_buffer về mảng 49.152 phần tử (và xóa cờ ZB_NEXT_HIGH_BIT)
        /// để Vanilla Serialize chỉ ghi đúng 49.151 flags chuẩn!
        /// </summary>
        [HarmonyPatch(typeof(ZoneManager.Data), "Serialize")]
        [HarmonyPrefix]
        public static void ZoneManager_Serialize_Prefix()
        {
            ZoneManager zm = ZoneManager.instance;
            if (zm == null || zm.m_blocks.m_buffer == null) return;

            s_fullBufferBackup128K = zm.m_blocks.m_buffer;

            ZoneBlock[] vanillaBuffer = new ZoneBlock[49152];
            Array.Copy(s_fullBufferBackup128K, vanillaBuffer, Math.Min(s_fullBufferBackup128K.Length, 49152));

            // Xóa bit thứ 17 (0x00010000u) trong m_flags của bản sao 48K trước khi ghi xuống Vanilla Stream
            for (int i = 1; i < 49152; i++)
            {
                vanillaBuffer[i].m_flags &= ~BitStash128K.ZB_NEXT_HIGH_BIT;
            }

            zm.m_blocks.m_buffer = vanillaBuffer;
        }

        /// <summary>
        /// S3. Postfix ZoneManager.Data.Serialize:
        /// Khôi phục ngay buffer 128K gốc, đóng gói các block 49.152..131.071 vào Dictionary m_SerializableDataStorage,
        /// và trả lại nguyên trạng các con trỏ NetSegment trong RAM.
        /// </summary>
        [HarmonyPatch(typeof(ZoneManager.Data), "Serialize")]
        [HarmonyPostfix]
        public static void ZoneManager_Serialize_Postfix()
        {
            ZoneManager zm = ZoneManager.instance;
            if (s_fullBufferBackup128K != null && zm != null)
            {
                zm.m_blocks.m_buffer = s_fullBufferBackup128K;
                s_fullBufferBackup128K = null;
            }

            try
            {
                if (zm == null || zm.m_blocks.m_buffer == null) return;

                ZoneBlock[] blocks = zm.m_blocks.m_buffer;
                using (var ms = new MemoryStream())
                using (var bw = new BinaryWriter(ms))
                {
                    bw.Write(SIDECAR_MAGIC);
                    bw.Write(SIDECAR_VERSION);

                    int activeHighCount = 0;
                    int maxCount = Math.Min(blocks.Length, BitStash128K.MAX_BLOCK_COUNT);
                    for (uint i = 49152u; i < maxCount; i++)
                    {
                        if ((blocks[i].m_flags & ZoneBlock.FLAG_CREATED) != 0) activeHighCount++;
                    }
                    bw.Write(activeHighCount);

                    for (uint i = 49152u; i < maxCount; i++)
                    {
                        if ((blocks[i].m_flags & ZoneBlock.FLAG_CREATED) == 0) continue;
                        bw.Write(i);
                        bw.Write(blocks[i].m_flags & ~BitStash128K.ZB_NEXT_HIGH_BIT);
                        bw.Write((ushort)blocks[i].RowCount);
                        bw.Write(blocks[i].m_buildIndex);
                        bw.Write(blocks[i].m_position.x);
                        bw.Write(blocks[i].m_position.y);
                        bw.Write(blocks[i].m_position.z);
                        bw.Write(blocks[i].m_angle);
                        bw.Write(blocks[i].m_segment);
                        bw.Write(blocks[i].m_zone1);
                        bw.Write(blocks[i].m_zone2);
                        bw.Write(blocks[i].m_occupied1);
                        bw.Write(blocks[i].m_occupied2);
                        bw.Write(blocks[i].m_valid);
                        bw.Write(blocks[i].m_shared);
                    }

                    bw.Write(s_savedMappings.Count);
                    for (int m = 0; m < s_savedMappings.Count; m++)
                    {
                        bw.Write(s_savedMappings[m].SegmentID);
                        bw.Write(s_savedMappings[m].Slot);
                        bw.Write(s_savedMappings[m].HighBlockID);
                    }

                    SimulationManager sm = Singleton<SimulationManager>.instance;
                    if (sm != null && sm.m_serializableDataStorage != null)
                    {
                        sm.m_serializableDataStorage[SIDECAR_KEY] = ms.ToArray();
                    }
                }
            }
            finally
            {
                if (NetManager.exists && NetManager.instance.m_segments.m_buffer != null)
                {
                    NetSegment[] segs = NetManager.instance.m_segments.m_buffer;
                    for (int m = 0; m < s_savedMappings.Count; m++)
                    {
                        ushort segID = s_savedMappings[m].SegmentID;
                        if (segID >= segs.Length) continue;

                        uint highID = s_savedMappings[m].HighBlockID;
                        switch (s_savedMappings[m].Slot)
                        {
                            case 0: BitStash128K.SetStartLeft32(ref segs[segID], highID); break;
                            case 1: BitStash128K.SetStartRight32(ref segs[segID], highID); break;
                            case 2: BitStash128K.SetEndLeft32(ref segs[segID], highID); break;
                            case 3: BitStash128K.SetEndRight32(ref segs[segID], highID); break;
                        }
                    }
                }
                s_savedMappings.Clear();
            }
        }

        // =====================================================================
        // GIAI ĐOẠN NẠP GAME (LOAD PIPELINE)
        // =====================================================================

        /// <summary>
        /// L1. Prefix ZoneManager.Data.Deserialize:
        /// Thu tạm m_blocks.m_buffer về 49.152 phần tử để Vanilla Deserialize đọc khớp 100% luồng 48K gốc!
        /// </summary>
        [HarmonyPatch(typeof(ZoneManager.Data), "Deserialize")]
        [HarmonyPrefix]
        public static void ZoneManager_Deserialize_Prefix()
        {
            ZoneManager zm = ZoneManager.instance;
            if (zm == null) return;

            zm.m_blocks.m_buffer = new ZoneBlock[49152];
            FieldInfo fSize = AccessTools.Field(typeof(Array16<ZoneBlock>), "m_size");
            fSize?.SetValue(zm.m_blocks, 49152u);
        }

        /// <summary>
        /// L2. Postfix ZoneManager.Data.AfterDeserialize:
        /// Chạy SAU KHI Vanilla đã nạp xong 1..49151 và đã khởi tạo m_zoneGrid cho 48K block đầu.
        /// Lúc này mới bung mảng lên 131.072, nạp Sidecar 49.152..131.071 và nối Tail-Chaining!
        /// </summary>
        [HarmonyPatch(typeof(ZoneManager.Data), "AfterDeserialize")]
        [HarmonyPostfix]
        public static void ZoneManager_AfterDeserialize_Postfix()
        {
            ZoneManager zm = ZoneManager.instance;
            if (zm == null) return;

            // 1. Nâng m_blocks.m_buffer từ 49.152 lên 131.072 (bảo toàn 49.151 block vừa nạp!)
            ZoneUpdateAndSim128K.Awake_Postfix(zm);

            // 2. Kiểm tra và đọc Sidecar Stream
            SimulationManager sm = Singleton<SimulationManager>.instance;
            if (sm != null && sm.m_serializableDataStorage != null)
            {
                byte[] data;
                byte[] legacyData;
                if (sm.m_serializableDataStorage.TryGetValue(SIDECAR_KEY, out data) && data != null && data.Length > 0)
                {
                    LoadSidecarData128K(zm, data);
                }
                else if (sm.m_serializableDataStorage.TryGetValue(LEGACY_SIDECAR_KEY, out legacyData) && legacyData != null && legacyData.Length > 0)
                {
                    LoadLegacySidecarData64K(zm, legacyData);
                }
            }

            // 3. Tái thiết lập Dual-Pool Free Lists và đồng bộ m_blockCount chính xác 100%
            DualPoolAllocator128K.RebuildFreeLists(zm.m_blocks.m_buffer);
        }

        private static void LoadSidecarData128K(ZoneManager zm, byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                uint magic = br.ReadUInt32();
                uint version = br.ReadUInt32();
                if (magic != SIDECAR_MAGIC || version != SIDECAR_VERSION) return;

                int highCount = br.ReadInt32();
                ZoneBlock[] blocks = zm.m_blocks.m_buffer;

                // Giai đoạn 2a: Đọc dữ liệu block và chèn các block 16-bit (49152..65535) trước để làm Head nếu cần
                var pending17BitBlocks = new List<uint>(highCount);
                for (int i = 0; i < highCount; i++)
                {
                    uint id = br.ReadUInt32();
                    if (id >= blocks.Length) break;

                    blocks[id].m_flags = br.ReadUInt32() | ZoneBlock.FLAG_CREATED;
                    blocks[id].RowCount = br.ReadUInt16();
                    blocks[id].m_buildIndex = br.ReadUInt32();
                    blocks[id].m_position = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    blocks[id].m_angle = br.ReadSingle();
                    blocks[id].m_segment = br.ReadUInt16();
                    blocks[id].m_zone1 = br.ReadUInt64();
                    blocks[id].m_zone2 = br.ReadUInt64();
                    blocks[id].m_occupied1 = br.ReadUInt64();
                    blocks[id].m_occupied2 = br.ReadUInt64();
                    blocks[id].m_valid = br.ReadUInt64();
                    blocks[id].m_shared = br.ReadUInt64();
                    BitStash128K.SetNextGridBlock32(ref blocks[id], 0u);

                    if (id < 65536u)
                        ZoneGridLinker128K.InitializeBlock32(zm, id, ref blocks[id]);
                    else
                        pending17BitBlocks.Add(id);
                }

                // Giai đoạn 2b: Chèn toàn bộ các block 17-bit (65537..131071) bằng Tail-Chaining (sau khi mọi Head 16-bit đã yên vị!)
                for (int i = 0; i < pending17BitBlocks.Count; i++)
                {
                    uint id17 = pending17BitBlocks[i];
                    ZoneGridLinker128K.InitializeBlock32(zm, id17, ref blocks[id17]);
                }

                // Giai đoạn 2c: Phục hồi 4 slot 17-bit trên NetSegment
                int mapCount = br.ReadInt32();
                if (NetManager.exists && NetManager.instance.m_segments.m_buffer != null)
                {
                    NetSegment[] segs = NetManager.instance.m_segments.m_buffer;
                    for (int m = 0; m < mapCount; m++)
                    {
                        ushort segID = br.ReadUInt16();
                        byte slot = br.ReadByte();
                        uint highID = br.ReadUInt32();
                        if (segID >= segs.Length) continue;

                        switch (slot)
                        {
                            case 0: BitStash128K.SetStartLeft32(ref segs[segID], highID); break;
                            case 1: BitStash128K.SetStartRight32(ref segs[segID], highID); break;
                            case 2: BitStash128K.SetEndLeft32(ref segs[segID], highID); break;
                            case 3: BitStash128K.SetEndRight32(ref segs[segID], highID); break;
                        }
                    }
                }
            }
        }

        private static void LoadLegacySidecarData64K(ZoneManager zm, byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                uint magic = br.ReadUInt32();
                uint version = br.ReadUInt32();
                if (magic != LEGACY_SIDECAR_MAGIC || version != 1) return;

                int highCount = br.ReadInt32();
                ZoneBlock[] blocks = zm.m_blocks.m_buffer;

                for (int i = 0; i < highCount; i++)
                {
                    ushort id = br.ReadUInt16();
                    if (id >= blocks.Length) break;

                    blocks[id].m_flags = br.ReadUInt32() | ZoneBlock.FLAG_CREATED;
                    blocks[id].m_buildIndex = br.ReadUInt32();
                    blocks[id].m_position = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    blocks[id].m_angle = br.ReadSingle();
                    blocks[id].m_valid = br.ReadUInt64();
                    blocks[id].m_shared = br.ReadUInt64();
                    blocks[id].m_occupied1 = br.ReadUInt64();
                    blocks[id].m_occupied2 = br.ReadUInt64();
                    blocks[id].m_zone1 = br.ReadUInt64();
                    blocks[id].m_zone2 = br.ReadUInt64();
                    blocks[id].m_segment = br.ReadUInt16();
                    BitStash128K.SetNextGridBlock32(ref blocks[id], 0u);

                    ZoneGridLinker128K.InitializeBlock32(zm, id, ref blocks[id]);
                }

                int mapCount = br.ReadInt32();
                if (NetManager.exists && NetManager.instance.m_segments.m_buffer != null)
                {
                    NetSegment[] segs = NetManager.instance.m_segments.m_buffer;
                    for (int m = 0; m < mapCount; m++)
                    {
                        ushort segID = br.ReadUInt16();
                        byte slot = br.ReadByte();
                        ushort highID = br.ReadUInt16();
                        if (segID >= segs.Length) continue;

                        switch (slot)
                        {
                            case 0: BitStash128K.SetStartLeft32(ref segs[segID], highID); break;
                            case 1: BitStash128K.SetStartRight32(ref segs[segID], highID); break;
                            case 2: BitStash128K.SetEndLeft32(ref segs[segID], highID); break;
                            case 3: BitStash128K.SetEndRight32(ref segs[segID], highID); break;
                        }
                    }
                }
            }
        }
    }
}
