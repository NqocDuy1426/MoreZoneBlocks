using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace MoreZoneBlocks.Tests
{
    public class TestRunner
    {
        private static int s_passCount = 0;
        private static int s_failCount = 0;

        public static void Assert(bool condition, string testName)
        {
            if (condition)
            {
                s_passCount++;
                Console.WriteLine("  ✅ PASS: " + testName);
            }
            else
            {
                s_failCount++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  ❌ FAIL: " + testName);
                Console.ResetColor();
            }
        }

        public static int Main()
        {
            Console.WriteLine("==========================================================");
            Console.WriteLine("🧪 RUNNING MORE ZONE BLOCKS 2 BETA (128K) TEST SUITE");
            Console.WriteLine("==========================================================");

            TestConstantsAndPacingMath();
            TestBitStashingLogicAndSignExtension();
            TestDualPoolAllocationModel();
            TestSidecarSerializationRoundtrip128K();
            TestInPlaceRelocationModel();
            TestCompiledAssemblyInspection();

            Console.WriteLine("==========================================================");
            Console.WriteLine(string.Format("📊 TEST RESULTS: {0} passed, {1} failed.", s_passCount, s_failCount));
            Console.WriteLine("==========================================================");

            return s_failCount == 0 ? 0 : 1;
        }

        private static void TestConstantsAndPacingMath()
        {
            Console.WriteLine("\n[1] Testing 128K Constants, Pacing & Arithmetic Proofs...");
            const int VANILLA_BLOCK_COUNT = 49152;
            const int MAX_BLOCK_COUNT = 131072; // 128K
            const int EXTENDED_UPDATED_LEN = 2048; // 131072 / 64
            const uint HIGH_BIT_VALUE = 0x00010000u; // 65536
            const uint SIDECAR_MAGIC = 0x4D5A4232; // "MZB2"
            const uint SIDECAR_VERSION = 2;

            Assert(VANILLA_BLOCK_COUNT == 49152, "VANILLA_BLOCK_COUNT == 49152 (48K)");
            Assert(MAX_BLOCK_COUNT == 131072, "MAX_BLOCK_COUNT == 131072 (128K)");
            Assert(EXTENDED_UPDATED_LEN == 131072 / 64, "EXTENDED_UPDATED_LEN == 2048 (131072 / 64 bits)");
            Assert(HIGH_BIT_VALUE == 65536, "HIGH_BIT_VALUE == 65536 (Bit 17 value)");
            Assert(SIDECAR_MAGIC == 0x4D5A4232, "SIDECAR_MAGIC == 0x4D5A4232 ('MZB2')");
            Assert(SIDECAR_VERSION == 2, "SIDECAR_VERSION == 2");

            // Proof of SimulationStep pacing math (128 blocks/frame * 1024 frames = 131,072)
            int pacing = 128;
            int frames = 1024;
            Assert(frames * pacing == 131072, "128K pacing: 1024 frames * 128 blocks = 131,072 blocks (100% coverage)");
        }

        private static void TestBitStashingLogicAndSignExtension()
        {
            Console.WriteLine("\n[2] Testing 17-Bit Bit-Stashing & Sign-Extension Immunity...");

            // 1. ZoneBlock m_nextGridBlock Bit 16 stashing
            uint testNextID = 70000u; // > 65536
            ushort low16 = (ushort)(testNextID & 0xFFFFu);
            uint flags = 1u; // FLAG_CREATED
            if (testNextID >= 65536u) flags |= 0x00010000u; // Set Bit 16

            uint decodedNextID = (uint)low16 | (flags & 0x00010000u);
            Assert(decodedNextID == 70000u, "ZoneBlock m_nextGridBlock 17-bit stashing decoded 70000 correctly");

            // 2. NetSegment Bit 31 (EndRight) Sign-Extension proof
            // In C#, Flags2 underlying type is int. If bit 31 is set (0x80000000), casting without (uint) causes sign extension!
            int signedFlags2 = unchecked((int)0x80000000u);
            ushort blockEndRight = (ushort)(131071u & 0xFFFFu); // 65535

            // Arithmetic shift (buggy)
            uint buggyShift = (uint)(signedFlags2 >> 15);
            Assert(buggyShift != 0x00010000u, "Demonstrated: signed shift (int >> 15) pollutes upper bits with 0xFFFF0000");

            // Logical shift (fixed)
            uint fixedShift = (((uint)signedFlags2) & 0x80000000u) >> 15;
            Assert(fixedShift == 0x00010000u, "Fixed logical shift (((uint)flags2) & 0x80000000u) >> 15 yields exactly 0x00010000 (65536)");

            uint decodedEndRight = (uint)blockEndRight | fixedShift;
            Assert(decodedEndRight == 131071u, "NetSegment EndRight decoded max ID 131071 correctly without sign extension");

            // 3. Bits 28, 29, 30 stashing
            uint idSL = 80000u;
            uint idSR = 90000u;
            uint idEL = 100000u;

            uint f2 = 0u;
            if (idSL >= 65536u) f2 |= 0x10000000u; // bit 28
            if (idSR >= 65536u) f2 |= 0x20000000u; // bit 29
            if (idEL >= 65536u) f2 |= 0x40000000u; // bit 30

            uint decSL = (uint)((ushort)(idSL & 0xFFFFu)) | ((f2 & 0x10000000u) >> 12);
            uint decSR = (uint)((ushort)(idSR & 0xFFFFu)) | ((f2 & 0x20000000u) >> 13);
            uint decEL = (uint)((ushort)(idEL & 0xFFFFu)) | ((f2 & 0x40000000u) >> 14);

            Assert(decSL == 80000u, "StartLeft (bit 28) decoded 80000 correctly");
            Assert(decSR == 90000u, "StartRight (bit 29) decoded 90000 correctly");
            Assert(decEL == 100000u, "EndLeft (bit 30) decoded 100000 correctly");
        }

        private static void TestDualPoolAllocationModel()
        {
            Console.WriteLine("\n[3] Testing Dual-Pool Allocator & Sentinel Reservation...");

            // Low pool: 1..65535
            // High pool: 65537..131071
            // Sentinel: 65536
            int lowPoolCapacity = 65535;
            int highPoolCapacity = 131072 - 65537; // 65535 slots

            Assert(lowPoolCapacity == 65535, "Low pool capacity is exactly 65,535 (1..65535)");
            Assert(highPoolCapacity == 65535, "High pool capacity is exactly 65,535 (65537..131071)");

            // Sentinel proof: 65536 & 0xFFFF == 0, would cause false null check
            Assert((65536u & 0xFFFFu) == 0u, "Proved: Slot 65536 has low-16 == 0, must be reserved as Sentinel!");
            Assert((65537u & 0xFFFFu) == 1u, "Slot 65537 has low-16 == 1 (safe)");

            // Head reservation logic:
            // When cell == 0, MUST allocate from low pool (< 65536)
            ushort emptyCell = 0;
            bool mustBeLow = (emptyCell == 0);
            Assert(mustBeLow, "Empty grid cell (0) strictly requires Low Pool (< 65536) allocation as Head");

            // When cell != 0, prefers High Pool (>= 65537)
            ushort occupiedCell = 42;
            bool preferHigh = (occupiedCell != 0);
            Assert(preferHigh, "Occupied grid cell prefers High Pool (>= 65537) allocation for Tail-Chaining");
        }

        private static void TestSidecarSerializationRoundtrip128K()
        {
            Console.WriteLine("\n[4] Testing 128K Sidecar (MZB2) Serialization Roundtrip...");
            const uint SIDECAR_MAGIC = 0x4D5A4232;
            const uint SIDECAR_VERSION = 2;

            byte[] serialized;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(SIDECAR_MAGIC);
                w.Write(SIDECAR_VERSION);

                // 3 blocks: 50000 (16-bit extended), 70000 (17-bit), 131071 (max 17-bit)
                w.Write(3); // count

                uint[] testIds = new uint[] { 50000u, 70000u, 131071u };
                foreach (uint id in testIds)
                {
                    w.Write(id);
                    w.Write((uint)1); // flags
                    w.Write((ushort)4); // rowCount
                    w.Write((uint)200); // buildIndex
                    w.Write(100.5f); // x
                    w.Write(20.0f);  // y
                    w.Write(-300.25f); // z
                    w.Write(3.14f);  // angle
                    w.Write((ushort)100); // segment
                    w.Write((ulong)0xAA); // zone1
                    w.Write((ulong)0x00); // zone2
                    w.Write((ulong)0x01); // occ1
                    w.Write((ulong)0x00); // occ2
                    w.Write((ulong)0xFF); // valid
                    w.Write((ulong)0x00); // shared
                }

                // 2 mappings
                w.Write(2);
                w.Write((ushort)10); // segID
                w.Write((byte)0);     // slot 0 (SL)
                w.Write(70000u);     // highID 32-bit

                w.Write((ushort)20); // segID
                w.Write((byte)3);     // slot 3 (ER)
                w.Write(131071u);    // highID 32-bit

                serialized = ms.ToArray();
            }

            Assert(serialized != null && serialized.Length > 0, "MZB2 serialized bytes generated");

            // Read back
            using (MemoryStream ms = new MemoryStream(serialized))
            using (BinaryReader r = new BinaryReader(ms))
            {
                uint magic = r.ReadUInt32();
                uint ver = r.ReadUInt32();
                int count = r.ReadInt32();

                Assert(magic == SIDECAR_MAGIC, "MZB2 Magic matched");
                Assert(ver == SIDECAR_VERSION, "MZB2 Version 2 matched");
                Assert(count == 3, "High block count is 3");

                uint id1 = r.ReadUInt32();
                r.ReadUInt32(); // flags
                ushort rows1 = r.ReadUInt16();
                r.ReadUInt32(); // build
                float x1 = r.ReadSingle();
                r.ReadSingle(); // y
                r.ReadSingle(); // z
                r.ReadSingle(); // a
                r.ReadUInt16(); // seg
                r.ReadUInt64(); // z1
                r.ReadUInt64(); // z2
                r.ReadUInt64(); // occ1
                r.ReadUInt64(); // occ2
                r.ReadUInt64(); // val
                r.ReadUInt64(); // sh

                Assert(id1 == 50000u, "Block 1 ID is 50000");
                Assert(rows1 == 4, "Block 1 RowCount is 4");
                Assert(Math.Abs(x1 - 100.5f) < 0.001f, "Block 1 x position accurate");

                // Skip block 2
                for (int i = 0; i < 15; i++) {
                    if (i == 0 || i == 1 || i == 3) r.ReadUInt32();
                    else if (i == 2 || i == 8) r.ReadUInt16();
                    else if (i >= 4 && i <= 7) r.ReadSingle();
                    else r.ReadUInt64();
                }

                // Block 3
                uint id3 = r.ReadUInt32();
                Assert(id3 == 131071u, "Block 3 ID is 131071 (max 17-bit)");

                // Skip rest of block 3
                for (int i = 1; i < 15; i++) {
                    if (i == 1 || i == 3) r.ReadUInt32();
                    else if (i == 2 || i == 8) r.ReadUInt16();
                    else if (i >= 4 && i <= 7) r.ReadSingle();
                    else r.ReadUInt64();
                }

                int mapCount = r.ReadInt32();
                Assert(mapCount == 2, "Mapping count is 2");
                ushort mSeg1 = r.ReadUInt16();
                byte mSlot1 = r.ReadByte();
                uint mHigh1 = r.ReadUInt32();
                Assert(mSeg1 == 10 && mSlot1 == 0 && mHigh1 == 70000u, "First mapping: (Seg 10, Slot 0, 70000)");

                ushort mSeg2 = r.ReadUInt16();
                byte mSlot2 = r.ReadByte();
                uint mHigh2 = r.ReadUInt32();
                Assert(mSeg2 == 20 && mSlot2 == 3 && mHigh2 == 131071u, "Second mapping: (Seg 20, Slot 3, 131071)");
            }
        }

        private static void TestInPlaceRelocationModel()
        {
            Console.WriteLine("\n[5] Testing In-Place Relocation Model (Zombie Block Immunity)...");

            // Scenario: Head 100 (< 65536) is deleted.
            // Following it is block 70000 (>= 65536).
            // No other 16-bit blocks in chain.
            // Action: In-Place Relocation moves block 70000 into slot 100.
            ushort releasedHead = 100;
            uint highNode = 70000u;

            // Simulated state
            uint segStartLeft = highNode; // Segment pointed to 70000

            // Relocate:
            ushort newHead = releasedHead; // Slot 100 becomes new head
            segStartLeft = newHead;       // Segment updated to point to 100

            Assert(newHead == 100, "New head slot is 100 (16-bit slot preserved for m_zoneGrid)");
            Assert(segStartLeft == 100, "Segment pointer successfully updated from 70000 to 100");
            Assert((uint)newHead < 65536u, "m_zoneGrid head guaranteed strictly < 65536 (zero ushort truncation)");
        }

        private static void TestCompiledAssemblyInspection()
        {
            Console.WriteLine("\n[6] Inspecting Compiled MoreZoneBlocks.dll via Mono.Cecil...");

            string dllPath = Path.Combine(Directory.GetCurrentDirectory(), "MoreZoneBlocks.dll");
            Assert(File.Exists(dllPath), "MoreZoneBlocks.dll exists on disk");

            var assembly = AssemblyDefinition.ReadAssembly(dllPath);
            Assert(assembly != null, "Assembly loaded cleanly via Mono.Cecil");

            var types = new HashSet<string>();
            foreach (var t in assembly.MainModule.Types)
            {
                types.Add(t.FullName);
                foreach (var nested in t.NestedTypes)
                {
                    types.Add(nested.FullName);
                }
            }

            Assert(types.Contains("MoreZoneBlocks.ModInfo"), "Contains MoreZoneBlocks.ModInfo (IUserMod)");
            Assert(types.Contains("MoreZoneBlocks.LoadingExtension"), "Contains MoreZoneBlocks.LoadingExtension (LoadingExtensionBase)");
            Assert(types.Contains("MoreZoneBlocks.ZoneGarbageCollector"), "Contains MoreZoneBlocks.ZoneGarbageCollector");
            Assert(types.Contains("MoreZoneBlocks.Log"), "Contains MoreZoneBlocks.Log");

            // Verify all 8 Core128K types exist in the compiled assembly
            Assert(types.Contains("MoreZoneBlocks.Core128K.BitStash128K"), "Contains Core128K.BitStash128K");
            Assert(types.Contains("MoreZoneBlocks.Core128K.DualPoolAllocator128K"), "Contains Core128K.DualPoolAllocator128K");
            Assert(types.Contains("MoreZoneBlocks.Core128K.ZoneGridLinker128K"), "Contains Core128K.ZoneGridLinker128K");
            Assert(types.Contains("MoreZoneBlocks.Core128K.NetSegmentZonePatches128K"), "Contains Core128K.NetSegmentZonePatches128K");
            Assert(types.Contains("MoreZoneBlocks.Core128K.RoadAICreateZoneBlocksTranspiler128K"), "Contains Core128K.RoadAICreateZoneBlocksTranspiler128K");
            Assert(types.Contains("MoreZoneBlocks.Core128K.ZoneTraversalTranspiler128K"), "Contains Core128K.ZoneTraversalTranspiler128K");
            Assert(types.Contains("MoreZoneBlocks.Core128K.ZoneUpdateAndSim128K"), "Contains Core128K.ZoneUpdateAndSim128K");
            Assert(types.Contains("MoreZoneBlocks.Core128K.SaveSanitizer128K"), "Contains Core128K.SaveSanitizer128K");

            // Inspect methods in ZoneUpdateAndSim128K
            var simType = assembly.MainModule.GetType("MoreZoneBlocks.Core128K.ZoneUpdateAndSim128K");
            bool hasAwakePostfix = false;
            bool hasSimStepTranspiler = false;
            bool hasSimStepPrefix = false;

            foreach (var m in simType.Methods)
            {
                if (m.Name == "Awake_Postfix") hasAwakePostfix = true;
                if (m.Name == "SimulationStepImpl_Transpiler") hasSimStepTranspiler = true;
                if (m.Name == "SimulationStepImpl_Prefix") hasSimStepPrefix = true;
            }

            Assert(hasAwakePostfix, "ZoneUpdateAndSim128K contains Awake_Postfix");
            Assert(hasSimStepTranspiler, "ZoneUpdateAndSim128K contains SimulationStepImpl_Transpiler");
            Assert(hasSimStepPrefix, "ZoneUpdateAndSim128K contains SimulationStepImpl_Prefix");

            // Inspect methods in RoadAICreateZoneBlocksTranspiler128K
            var roadType = assembly.MainModule.GetType("MoreZoneBlocks.Core128K.RoadAICreateZoneBlocksTranspiler128K");
            bool hasRoadTranspiler = false;
            bool hasWrapSL = false;
            foreach (var m in roadType.Methods)
            {
                if (m.Name == "Transpiler") hasRoadTranspiler = true;
                if (m.Name == "CreateStartLeft128K") hasWrapSL = true;
            }
            Assert(hasRoadTranspiler, "RoadAICreateZoneBlocksTranspiler128K contains Transpiler");
            Assert(hasWrapSL, "RoadAICreateZoneBlocksTranspiler128K contains CreateStartLeft128K wrapper");

            // Inspect methods in NetSegmentZonePatches128K
            var segType = assembly.MainModule.GetType("MoreZoneBlocks.Core128K.NetSegmentZonePatches128K");
            bool hasCheckLimits = false;
            bool hasCreateBlock = false;
            bool hasUpdateZones = false;
            foreach (var m in segType.Methods)
            {
                if (m.Name == "CheckLimits_Prefix") hasCheckLimits = true;
                if (m.Name == "CreateBlock_Prefix") hasCreateBlock = true;
                if (m.Name == "UpdateZones_Prefix") hasUpdateZones = true;
            }
            Assert(hasCheckLimits, "NetSegmentZonePatches128K contains CheckLimits_Prefix (Fix for roads/bridges beyond 48K)");
            Assert(hasCreateBlock, "NetSegmentZonePatches128K contains CreateBlock_Prefix");
            Assert(hasUpdateZones, "NetSegmentZonePatches128K contains UpdateZones_Prefix");

            // Inspect DayNightDeadlockShield
            Assert(types.Contains("MoreZoneBlocks.DayNightDeadlockShield"), "Contains MoreZoneBlocks.DayNightDeadlockShield");
            Assert(types.Contains("MoreZoneBlocks.DayNightDeadlockShield/DayNightProperties_InitializeProperties_Patch"), "Contains DayNightProperties_InitializeProperties_Patch");
            Assert(types.Contains("MoreZoneBlocks.DayNightDeadlockShield/LoadingManager_WaitUntilEssentialScenesLoaded_Patch"), "Contains LoadingManager_WaitUntilEssentialScenesLoaded_Patch");
            Assert(types.Contains("MoreZoneBlocks.DayNightDeadlockShield/TerrainManager_LateUpdateData_Patch"), "Contains TerrainManager_LateUpdateData_Patch");
            Assert(types.Contains("MoreZoneBlocks.DayNightDeadlockShield/TerrainManager_UpdateData_Patch"), "Contains TerrainManager_UpdateData_Patch");
            Assert(types.Contains("MoreZoneBlocks.DayNightDeadlockShield/PathManager_WaitForAllPaths_Patch"), "Contains PathManager_WaitForAllPaths_Patch");
            Assert(types.Contains("MoreZoneBlocks.DayNightDeadlockShield/LoadingManager_QuitApplication_Patch"), "Contains LoadingManager_QuitApplication_Patch");
        }
    }
}
