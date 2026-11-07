using System;
using ColossalFramework;
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
                        CheckAndReleaseEmptyBlock(zm, blocks, ref segments[segID].m_blockStartLeft);
                        CheckAndReleaseEmptyBlock(zm, blocks, ref segments[segID].m_blockStartRight);
                        CheckAndReleaseEmptyBlock(zm, blocks, ref segments[segID].m_blockEndLeft);
                        CheckAndReleaseEmptyBlock(zm, blocks, ref segments[segID].m_blockEndRight);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "ZoneGarbageCollector.OnSimulationStep");
            }
        }

        private static void CheckAndReleaseEmptyBlock(ZoneManager zm, ZoneBlock[] blocks, ref ushort blockRef)
        {
            ushort blockID = blockRef;
            if (blockID == 0 || blockID >= blocks.Length)
                return;

            if ((blocks[blockID].m_flags & ZoneBlock.FLAG_CREATED) == 0)
            {
                // Stale reference to already released block, clean pointer
                blockRef = 0;
                return;
            }

            // A block is completely dead/empty if no zones are painted and no buildings exist
            if (blocks[blockID].m_zone1 == 0 &&
                blocks[blockID].m_zone2 == 0 &&
                blocks[blockID].m_occupied1 == 0 &&
                blocks[blockID].m_occupied2 == 0)
            {
                // Safe release on simulation thread
                zm.ReleaseBlock(blockID);
                blockRef = 0;
            }
        }
    }
}
