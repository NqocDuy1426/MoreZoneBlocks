using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using ColossalFramework;
using UnityEngine;

namespace MoreZoneBlocks
{
    public static class SaveSanitizer
    {
        public const string SIDECAR_DATA_KEY = "MoreZoneBlocks_Extended";
        public const uint SIDECAR_MAGIC = 0x4D5A4231; // "MZB1"
        public const uint SIDECAR_VERSION = 1;

        public const string SIDECAR_DATA_KEY_128K = "MoreZoneBlocks_Extended_128K";
        public const uint SIDECAR_MAGIC_128K = 0x4D5A4232; // "MZB2"

        private delegate void InitializeBlockInvoker(ZoneManager instance, ushort block, ref ZoneBlock data);
        private static InitializeBlockInvoker s_initializeBlock;

        static SaveSanitizer()
        {
            try
            {
                MethodInfo target = typeof(ZoneManager).GetMethod("InitializeBlock", BindingFlags.NonPublic | BindingFlags.Instance);
                if (target != null)
                {
                    DynamicMethod dm = new DynamicMethod("MZB_FastInitializeBlock", null,
                        new Type[] { typeof(ZoneManager), typeof(ushort), typeof(ZoneBlock).MakeByRefType() },
                        typeof(SaveSanitizer).Module, true);
                    ILGenerator il = dm.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Callvirt, target);
                    il.Emit(OpCodes.Ret);
                    s_initializeBlock = (InitializeBlockInvoker)dm.CreateDelegate(typeof(InitializeBlockInvoker));
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "SaveSanitizer.cctor InitializeBlockInvoker");
            }
        }

        public struct SegmentBlockMapping
        {
            public ushort SegmentID;
            public byte Slot; // 0 = StartLeft, 1 = StartRight, 2 = EndLeft, 3 = EndRight
            public ushort HighBlockID;

            public SegmentBlockMapping(ushort segmentID, byte slot, ushort highBlockID)
            {
                SegmentID = segmentID;
                Slot = slot;
                HighBlockID = highBlockID;
            }
        }

        private static List<SegmentBlockMapping> s_maskedSegmentPointers = new List<SegmentBlockMapping>();
        private static bool s_isMasked = false;

        // Pending data loaded during Deserialize Prefix
        public static byte[] s_pendingSidecarBytes = null;

        // ====================================================================
        // SAVE WORKFLOW (Sanitization during Save)
        // ====================================================================

        /// <summary>
        /// Called at NetManager.Data.Serialize Prefix:
        /// Masks any NetSegment block pointer >= 49152 to 0 and records it in memory.
        /// Guarantees the vanilla save stream only sees block IDs < 49152.
        /// </summary>
        public static void SanitizeHighSegmentBlockPointers()
        {
            try
            {
                if (s_isMasked)
                {
                    Log.Warning("SanitizeHighSegmentBlockPointers called while already masked! Skipping.");
                    return;
                }

                s_maskedSegmentPointers.Clear();
                NetManager nm = Singleton<NetManager>.instance;
                if (nm == null || nm.m_segments.m_buffer == null)
                    return;

                NetSegment[] segments = nm.m_segments.m_buffer;
                int maxSegments = segments.Length;

                for (int i = 1; i < maxSegments; i++)
                {
                    if ((segments[i].m_flags & NetSegment.Flags.Created) == 0)
                        continue;

                    ushort segID = (ushort)i;

                    if (segments[i].m_blockStartLeft >= ZoneCustomizer.VANILLA_BLOCK_COUNT)
                    {
                        s_maskedSegmentPointers.Add(new SegmentBlockMapping(segID, 0, segments[i].m_blockStartLeft));
                        segments[i].m_blockStartLeft = 0;
                    }
                    if (segments[i].m_blockStartRight >= ZoneCustomizer.VANILLA_BLOCK_COUNT)
                    {
                        s_maskedSegmentPointers.Add(new SegmentBlockMapping(segID, 1, segments[i].m_blockStartRight));
                        segments[i].m_blockStartRight = 0;
                    }
                    if (segments[i].m_blockEndLeft >= ZoneCustomizer.VANILLA_BLOCK_COUNT)
                    {
                        s_maskedSegmentPointers.Add(new SegmentBlockMapping(segID, 2, segments[i].m_blockEndLeft));
                        segments[i].m_blockEndLeft = 0;
                    }
                    if (segments[i].m_blockEndRight >= ZoneCustomizer.VANILLA_BLOCK_COUNT)
                    {
                        s_maskedSegmentPointers.Add(new SegmentBlockMapping(segID, 3, segments[i].m_blockEndRight));
                        segments[i].m_blockEndRight = 0;
                    }
                }

                s_isMasked = true;
                if (s_maskedSegmentPointers.Count > 0)
                {
                    Log.Info(string.Format("Sanitized {0} high segment block pointers before Vanilla NetManager serialize.",
                        s_maskedSegmentPointers.Count));
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "SanitizeHighSegmentBlockPointers");
            }
        }

        /// <summary>
        /// Called at ZoneManager.Data.Serialize Postfix:
        /// Packages extended blocks (>= 49152) and masked segment mappings into sidecar stream,
        /// then restores original pointers in RAM.
        /// </summary>
        public static void PersistSidecarDataAndRestorePointers()
        {
            try
            {
                // 1. Serialize sidecar data
                byte[] sidecarBytes = SerializeSidecarData();
                if (sidecarBytes != null && sidecarBytes.Length > 0)
                {
                    SimulationManager sm = Singleton<SimulationManager>.instance;
                    if (sm != null && sm.m_serializableDataStorage != null)
                    {
                        sm.m_serializableDataStorage[SIDECAR_DATA_KEY] = sidecarBytes;
                        Log.Info(string.Format("Saved {0} bytes of MoreZoneBlocks sidecar data into savegame storage.",
                            sidecarBytes.Length));
                    }
                }
                else
                {
                    // No extended data, remove key if exists
                    SimulationManager sm = Singleton<SimulationManager>.instance;
                    if (sm != null && sm.m_serializableDataStorage != null && sm.m_serializableDataStorage.ContainsKey(SIDECAR_DATA_KEY))
                    {
                        sm.m_serializableDataStorage.Remove(SIDECAR_DATA_KEY);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "PersistSidecarData");
            }
            finally
            {
                // 2. ALWAYS Unmask in RAM
                RestoreSegmentPointersInRam();
            }
        }

        private static void RestoreSegmentPointersInRam()
        {
            try
            {
                if (!s_isMasked) return;

                NetManager nm = Singleton<NetManager>.instance;
                if (nm != null && nm.m_segments.m_buffer != null)
                {
                    NetSegment[] segments = nm.m_segments.m_buffer;
                    for (int i = 0; i < s_maskedSegmentPointers.Count; i++)
                    {
                        SegmentBlockMapping mapping = s_maskedSegmentPointers[i];
                        if (mapping.SegmentID < segments.Length)
                        {
                            switch (mapping.Slot)
                            {
                                case 0: segments[mapping.SegmentID].m_blockStartLeft = mapping.HighBlockID; break;
                                case 1: segments[mapping.SegmentID].m_blockStartRight = mapping.HighBlockID; break;
                                case 2: segments[mapping.SegmentID].m_blockEndLeft = mapping.HighBlockID; break;
                                case 3: segments[mapping.SegmentID].m_blockEndRight = mapping.HighBlockID; break;
                            }
                        }
                    }
                }

                s_maskedSegmentPointers.Clear();
                s_isMasked = false;
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "RestoreSegmentPointersInRam");
            }
        }

        private static byte[] SerializeSidecarData()
        {
            ZoneManager zm = Singleton<ZoneManager>.instance;
            if (zm == null || zm.m_blocks.m_buffer == null)
                return null;

            ZoneBlock[] blocks = zm.m_blocks.m_buffer;
            List<ushort> activeHighBlocks = new List<ushort>();

            int maxBlocks = Math.Min(blocks.Length, ZoneCustomizer.EXPANDED_BLOCK_COUNT);
            for (int i = ZoneCustomizer.VANILLA_BLOCK_COUNT; i < maxBlocks; i++)
            {
                if ((blocks[i].m_flags & ZoneBlock.FLAG_CREATED) != 0)
                {
                    activeHighBlocks.Add((ushort)i);
                }
            }

            if (activeHighBlocks.Count == 0 && s_maskedSegmentPointers.Count == 0)
            {
                return null; // Clean vanilla state, no extended data to save
            }

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(SIDECAR_MAGIC);
                w.Write(SIDECAR_VERSION);

                // Write Extended Blocks
                w.Write(activeHighBlocks.Count);
                for (int i = 0; i < activeHighBlocks.Count; i++)
                {
                    ushort id = activeHighBlocks[i];
                    ZoneBlock b = blocks[id];

                    w.Write(id);
                    w.Write(b.m_flags);
                    w.Write(b.m_buildIndex);
                    w.Write(b.m_position.x);
                    w.Write(b.m_position.y);
                    w.Write(b.m_position.z);
                    w.Write(b.m_angle);
                    w.Write(b.m_valid);
                    w.Write(b.m_shared);
                    w.Write(b.m_occupied1);
                    w.Write(b.m_occupied2);
                    w.Write(b.m_zone1);
                    w.Write(b.m_zone2);
                    w.Write(b.m_segment);
                }

                // Write Masked Segment Mappings
                w.Write(s_maskedSegmentPointers.Count);
                for (int i = 0; i < s_maskedSegmentPointers.Count; i++)
                {
                    SegmentBlockMapping m = s_maskedSegmentPointers[i];
                    w.Write(m.SegmentID);
                    w.Write(m.Slot);
                    w.Write(m.HighBlockID);
                }

                return ms.ToArray();
            }
        }

        // ====================================================================
        // LOAD WORKFLOW (Restoration during Load)
        // ====================================================================

        /// <summary>
        /// Called at ZoneManager.Data.Deserialize Prefix:
        /// Loads the sidecar byte array from SimulationManager before vanilla deserialization,
        /// and resets m_blocks to 49,152 to prevent vanilla from overreading the stream!
        /// </summary>
        public static void PrepareEarlySidecarData()
        {
            try
            {
                Log.Info("[SaveSanitizer] PrepareEarlySidecarData CALLED!");
                s_pendingSidecarBytes = null;
                SimulationManager sm = Singleton<SimulationManager>.instance;
                Log.Info("[SaveSanitizer] SimulationManager: " + (sm != null ? "OK" : "NULL") + 
                    ", storage: " + (sm != null && sm.m_serializableDataStorage != null ? sm.m_serializableDataStorage.Count + " keys" : "NULL"));
                if (sm != null && sm.m_serializableDataStorage != null)
                {
                    byte[] data;
                    if (sm.m_serializableDataStorage.TryGetValue(SIDECAR_DATA_KEY, out data))
                    {
                        s_pendingSidecarBytes = data;
                        Log.Info(string.Format("Found MoreZoneBlocks sidecar data ({0} bytes) in savegame storage.", data.Length));
                    }
                    else if (sm.m_serializableDataStorage.TryGetValue(SIDECAR_DATA_KEY_128K, out data))
                    {
                        s_pendingSidecarBytes = data;
                        Log.Info(string.Format("Found MoreZoneBlocks 128K sidecar data ({0} bytes) in savegame storage. Backward compatible restore.", data.Length));
                    }
                }

                // CRITICAL STREAM PROTECTION:
                // In Cities: Skylines, the main simulation stream for ZoneManager ALWAYS contains exactly 49,152 blocks.
                // Reset m_blocks to 49,152 before deserialization so vanilla doesn't overread the stream by 16,384 elements (461 KB),
                // which destroys stream alignment for PropManager, TreeManager, WeatherManager, etc.
                ZoneManager zm = Singleton<ZoneManager>.instance;
                if (zm != null)
                {
                    zm.m_blocks = new Array16<ZoneBlock>((uint)ZoneCustomizer.VANILLA_BLOCK_COUNT);
                    Log.Info("ZoneManager.Data.Deserialize Prefix: Set buffer to 49,152 to protect stream alignment.");
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "PrepareEarlySidecarData");
            }
        }

        /// <summary>
        /// Called at ZoneManager.Data.AfterDeserialize Postfix:
        /// Expands memory to 65,536, restores extended blocks and segment pointers,
        /// rebuilds spatial hash grid for extended blocks, and updates block count.
        /// </summary>
        public static void RestoreSidecarData()
        {
            try
            {
                ZoneManager zm = Singleton<ZoneManager>.instance;
                if (zm == null) return;

                // 1. Expand buffer to 64K
                ZoneCustomizer.EnsureExpanded(zm);

                if (s_pendingSidecarBytes == null || s_pendingSidecarBytes.Length == 0)
                {
                    Log.Info("No extended sidecar data to restore (vanilla save format).");
                    ZoneCustomizer.UpdateBlockCount(zm);
                    return;
                }

                int loadedBlocksCount = 0;
                int loadedMappingsCount = 0;

                using (MemoryStream ms = new MemoryStream(s_pendingSidecarBytes))
                using (BinaryReader r = new BinaryReader(ms))
                {
                    uint magic = r.ReadUInt32();
                    uint version = r.ReadUInt32();

                    if (magic == SIDECAR_MAGIC && version <= SIDECAR_VERSION)
                    {
                        // Restore Extended Blocks (v1 64K format)
                        loadedBlocksCount = r.ReadInt32();
                        ZoneBlock[] blocks = zm.m_blocks.m_buffer;

                        for (int i = 0; i < loadedBlocksCount; i++)
                        {
                            ushort id = r.ReadUInt16();
                            if (id < blocks.Length)
                            {
                                blocks[id].m_flags = r.ReadUInt32();
                                blocks[id].m_buildIndex = r.ReadUInt32();
                                blocks[id].m_position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                                blocks[id].m_angle = r.ReadSingle();
                                blocks[id].m_valid = r.ReadUInt64();
                                blocks[id].m_shared = r.ReadUInt64();
                                blocks[id].m_occupied1 = r.ReadUInt64();
                                blocks[id].m_occupied2 = r.ReadUInt64();
                                blocks[id].m_zone1 = r.ReadUInt64();
                                blocks[id].m_zone2 = r.ReadUInt64();
                                blocks[id].m_segment = r.ReadUInt16();
                            }
                            else
                            {
                                r.ReadBytes(4 + 4 + 12 + 4 + 8 + 8 + 8 + 8 + 8 + 8 + 2);
                            }
                        }

                        // Restore Segment Mappings
                        loadedMappingsCount = r.ReadInt32();
                        NetManager nm = Singleton<NetManager>.instance;
                        NetSegment[] segments = nm != null ? nm.m_segments.m_buffer : null;

                        for (int i = 0; i < loadedMappingsCount; i++)
                        {
                            ushort segID = r.ReadUInt16();
                            byte slot = r.ReadByte();
                            ushort highID = r.ReadUInt16();

                            if (segments != null && segID < segments.Length)
                            {
                                switch (slot)
                                {
                                    case 0: segments[segID].m_blockStartLeft = highID; break;
                                    case 1: segments[segID].m_blockStartRight = highID; break;
                                    case 2: segments[segID].m_blockEndLeft = highID; break;
                                    case 3: segments[segID].m_blockEndRight = highID; break;
                                }
                            }
                        }
                    }
                    else if (magic == SIDECAR_MAGIC_128K)
                    {
                        // Restore Extended Blocks from 128K format
                        loadedBlocksCount = r.ReadInt32();
                        ZoneBlock[] blocks = zm.m_blocks.m_buffer;

                        for (int i = 0; i < loadedBlocksCount; i++)
                        {
                            uint id32 = r.ReadUInt32();
                            uint flags = r.ReadUInt32();
                            uint buildIndex = r.ReadUInt32();
                            float px = r.ReadSingle();
                            float py = r.ReadSingle();
                            float pz = r.ReadSingle();
                            float angle = r.ReadSingle();
                            ushort segment = r.ReadUInt16();
                            ulong zone1 = r.ReadUInt64();
                            ulong zone2 = r.ReadUInt64();
                            ulong occupied1 = r.ReadUInt64();
                            ulong occupied2 = r.ReadUInt64();
                            ulong valid = r.ReadUInt64();
                            ulong shared = r.ReadUInt64();

                            if (id32 < (uint)blocks.Length)
                            {
                                ushort id = (ushort)id32;
                                blocks[id].m_flags = flags | ZoneBlock.FLAG_CREATED;
                                blocks[id].m_buildIndex = buildIndex;
                                blocks[id].m_position = new Vector3(px, py, pz);
                                blocks[id].m_angle = angle;
                                blocks[id].m_valid = valid;
                                blocks[id].m_shared = shared;
                                blocks[id].m_occupied1 = occupied1;
                                blocks[id].m_occupied2 = occupied2;
                                blocks[id].m_zone1 = zone1;
                                blocks[id].m_zone2 = zone2;
                                blocks[id].m_segment = segment;
                            }
                        }

                        loadedMappingsCount = r.ReadInt32();
                        NetManager nm = Singleton<NetManager>.instance;
                        NetSegment[] segments = nm != null ? nm.m_segments.m_buffer : null;

                        for (int i = 0; i < loadedMappingsCount; i++)
                        {
                            ushort segID = r.ReadUInt16();
                            byte slot = r.ReadByte();
                            uint highID32 = r.ReadUInt32();

                            if (segments != null && segID < segments.Length && highID32 < (uint)blocks.Length)
                            {
                                ushort highID = (ushort)highID32;
                                switch (slot)
                                {
                                    case 0: segments[segID].m_blockStartLeft = highID; break;
                                    case 1: segments[segID].m_blockStartRight = highID; break;
                                    case 2: segments[segID].m_blockEndLeft = highID; break;
                                    case 3: segments[segID].m_blockEndRight = highID; break;
                                }
                            }
                        }
                    }
                    else
                    {
                        Log.Error(string.Format("Unsupported sidecar magic: 0x{0:X8}, version: {1}", magic, version));
                        return;
                    }
                }

                // 2. Rebuild Free List for new expanded items
                ZoneCustomizer.RebuildFreeList(zm.m_blocks);

                // 3. Re-insert extended blocks into spatial hash grid m_zoneGrid & m_nextGridBlock
                // ONLY for blocks 49152..65535 using integer loop counter to prevent ushort wrap-around trap
                if (s_initializeBlock != null)
                {
                    for (int blockID = ZoneCustomizer.VANILLA_BLOCK_COUNT; blockID < ZoneCustomizer.EXPANDED_BLOCK_COUNT; blockID++)
                    {
                        if ((zm.m_blocks.m_buffer[blockID].m_flags & ZoneBlock.FLAG_CREATED) != 0)
                        {
                            s_initializeBlock(zm, (ushort)blockID, ref zm.m_blocks.m_buffer[blockID]);
                        }
                    }
                }
                else
                {
                    Log.Error("s_initializeBlock is null! Cannot re-insert extended blocks into spatial grid.");
                }

                // 4. Update m_blockCount accurately
                ZoneCustomizer.UpdateBlockCount(zm);

                Log.Info(string.Format("Restored {0} extended ZoneBlocks and {1} segment mappings successfully! Total block count: {2}",
                    loadedBlocksCount, loadedMappingsCount, zm.m_blockCount));
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "RestoreSidecarData");
            }
            finally
            {
                s_pendingSidecarBytes = null;
            }
        }
    }
}
