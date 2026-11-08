using System;
using ColossalFramework;
using MoreZoneBlocks.Core128K;
using UnityEngine;

namespace MoreZoneBlocks
{
    public static class ZoneGarbageCollector
    {
        private static int s_currentScanIndex = 1;
        private const int BATCH_SIZE = 16;
        private static uint s_lastTickCount = 0;

        /// <summary>
        /// Invoked strictly on the Simulation Thread from ZoneManager.SimulationStepImpl Postfix.
        /// Thread-safe and zero frame-drop impact (scans 16 segments per frame).
        /// </summary>
        public static void OnSimulationStep()
        {
            try
            {
                s_lastTickCount++;
                // Run every 4 simulation frames to keep CPU overhead virtually zero
                if ((s_lastTickCount & 3) != 0) return;

                NetManager nm = Singleton<NetManager>.instance;
                ZoneManager zm = Singleton<ZoneManager>.instance;
                if (nm == null || zm == null || nm.m_segments.m_buffer == null || zm.m_blocks.m_buffer == null)
                    return;

                NetSegment[] segments = nm.m_segments.m_buffer;
                ZoneBlock[] blocks = zm.m_blocks.m_buffer;
                int maxSegments = segments.Length;

                for (int count = 0; count < BATCH_SIZE; count++)
                {
                    s_currentScanIndex++;
                    if (s_currentScanIndex >= maxSegments)
                    {
                        s_currentScanIndex = 1;
                    }

                    int segID = s_currentScanIndex;
                    if ((segments[segID].m_flags & NetSegment.Flags.Created) == 0)
                        continue;

                    NetInfo info = segments[segID].Info;
                    if (info == null) continue;

                    // Detect non-zonable networks:
                    // 1. RoadAI with m_enableZoning == false (Highways, motorways, ramps)
                    // 2. TrainTrackAI, MetroTrackAI, Monorail, or PublicTransport service
                    bool isNonZonable = false;
                    if (info.m_netAI is RoadAI roadAI)
                    {
                        if (!roadAI.m_enableZoning)
                        {
                            isNonZonable = true;
                        }
                    }
                    else if (info.m_netAI is TrainTrackAI || info.m_netAI is MetroTrackAI)
                    {
                        isNonZonable = true;
                    }
                    else if (info.m_class != null && info.m_class.m_service == ItemClass.Service.PublicTransport)
                    {
                        isNonZonable = true;
                    }

                    if (isNonZonable)
                    {
                        uint b1 = BitStash128K.GetStartLeft32(ref segments[segID]);
                        if (b1 != 0) CheckAndReleaseEmptyBlock32(zm, blocks, b1, segID, 0);

                        uint b2 = BitStash128K.GetStartRight32(ref segments[segID]);
                        if (b2 != 0) CheckAndReleaseEmptyBlock32(zm, blocks, b2, segID, 1);

                        uint b3 = BitStash128K.GetEndLeft32(ref segments[segID]);
                        if (b3 != 0) CheckAndReleaseEmptyBlock32(zm, blocks, b3, segID, 2);

                        uint b4 = BitStash128K.GetEndRight32(ref segments[segID]);
                        if (b4 != 0) CheckAndReleaseEmptyBlock32(zm, blocks, b4, segID, 3);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "ZoneGarbageCollector.OnSimulationStep");
            }
        }

        private static void CheckAndReleaseEmptyBlock32(ZoneManager zm, ZoneBlock[] blocks, uint blockID, int segID, byte slot)
        {
            if (blockID == 0 || blockID >= blocks.Length)
                return;

            ref NetSegment seg = ref NetManager.instance.m_segments.m_buffer[segID];

            if ((blocks[blockID].m_flags & ZoneBlock.FLAG_CREATED) == 0)
            {
                // Stale reference to already released block, clean pointer
                ClearSegmentSlot(ref seg, slot);
                return;
            }

            // A block is completely dead/empty if no zones are painted and no buildings exist
            if (blocks[blockID].m_zone1 == 0 &&
                blocks[blockID].m_zone2 == 0 &&
                blocks[blockID].m_occupied1 == 0 &&
                blocks[blockID].m_occupied2 == 0)
            {
                // Safe release with 128K relocation & dual-pool return
                ZoneGridLinker128K.ReleaseBlock32(zm, blockID, ref blocks[blockID]);
                ClearSegmentSlot(ref seg, slot);
            }
        }

        private static void ClearSegmentSlot(ref NetSegment seg, byte slot)
        {
            switch (slot)
            {
                case 0: BitStash128K.SetStartLeft32(ref seg, 0u); break;
                case 1: BitStash128K.SetStartRight32(ref seg, 0u); break;
                case 2: BitStash128K.SetEndLeft32(ref seg, 0u); break;
                case 3: BitStash128K.SetEndRight32(ref seg, 0u); break;
            }
        }
    }
}
