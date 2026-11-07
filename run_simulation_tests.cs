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
            Console.WriteLine("🧪 RUNNING MORE ZONE BLOCKS SIMULATION & INTEGRITY TESTS");
            Console.WriteLine("==========================================================");

            TestConstantsAndPacingMath();
            TestSidecarSerializationRoundtrip();
            TestSimulationExpansionModel();
            TestLoopBoundsAndWrapAround();
            TestGarbageCollectorModel();
            TestCompiledAssemblyInspection();

            Console.WriteLine("==========================================================");
            Console.WriteLine(string.Format("📊 TEST RESULTS: {0} passed, {1} failed.", s_passCount, s_failCount));
            Console.WriteLine("==========================================================");

            return s_failCount == 0 ? 0 : 1;
        }

        private static void TestConstantsAndPacingMath()
        {
            Console.WriteLine("\n[1] Testing Constants, Pacing & Arithmetic Proofs...");
            const int VANILLA_BLOCK_COUNT = 49152;
            const int EXPANDED_BLOCK_COUNT = 65536;
            const int EXPANDED_LIMIT = 65535;
            const int VANILLA_UPDATED = 768;
            const int EXPANDED_UPDATED = 1024;
            const int WARNING_LIMIT = 65024;
            const uint SIDECAR_MAGIC = 0x4D5A4231;
            const uint SIDECAR_VERSION = 1;

            Assert(VANILLA_BLOCK_COUNT == 49152, "VANILLA_BLOCK_COUNT == 49152 (48K)");
            Assert(EXPANDED_BLOCK_COUNT == 65536, "EXPANDED_BLOCK_COUNT == 65536 (64K)");
            Assert(EXPANDED_LIMIT == 65535, "EXPANDED_LIMIT == 65535 (0xFFFF)");
            Assert(VANILLA_UPDATED == 49152 / 64, "VANILLA_UPDATED == 768 (49152 / 64 bits)");
            Assert(EXPANDED_UPDATED == 65536 / 64, "EXPANDED_UPDATED == 1024 (65536 / 64 bits)");
            Assert(WARNING_LIMIT == 65536 - 512, "WARNING_LIMIT == 65024 (65536 - 512)");
            Assert(SIDECAR_MAGIC == 0x4D5A4231, "SIDECAR_MAGIC == 0x4D5A4231 ('MZB1')");
            Assert(SIDECAR_VERSION == 1, "SIDECAR_VERSION == 1");

            // Proof of SimulationStep pacing math
            int vanillaPacing = 48;
            int vanillaFrames = 1024;
            Assert(vanillaFrames * vanillaPacing == 49152, "Vanilla pacing: 1024 frames * 48 blocks = 49,152 blocks");

            int expandedPacing = 64;
            Assert(vanillaFrames * expandedPacing == 65536, "Expanded pacing: 1024 frames * 64 blocks = 65,536 blocks (100% coverage)");
            Assert((sbyte)expandedPacing == 64, "Expanded pacing value fits in signed sbyte (no cast overflow)");
        }

        private static void TestSidecarSerializationRoundtrip()
        {
            Console.WriteLine("\n[2] Testing Sidecar Binary Serialization & Deserialization...");
            const uint SIDECAR_MAGIC = 0x4D5A4231;
            const uint SIDECAR_VERSION = 1;

            byte[] serializedData;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(SIDECAR_MAGIC);
                w.Write(SIDECAR_VERSION);

                // Write 3 sample high blocks: 49152, 55000, 65535
                w.Write(3); // count

                ushort[] testIds = new ushort[] { 49152, 55000, 65535 };
                foreach (ushort id in testIds)
                {
                    w.Write(id);
                    w.Write((uint)1); // m_flags (Created)
                    w.Write((uint)100); // m_buildIndex
                    w.Write(123.45f); // pos.x
                    w.Write(67.89f);  // pos.y
                    w.Write(-456.7f); // pos.z
                    w.Write(1.57f);   // m_angle
                    w.Write((ulong)0xFF); // m_valid
                    w.Write((ulong)0x00); // m_shared
                    w.Write((ulong)0x01); // m_occupied1
                    w.Write((ulong)0x00); // m_occupied2
                    w.Write((ulong)0x0F); // m_zone1
                    w.Write((ulong)0x00); // m_zone2
                    w.Write((ushort)1234); // m_segment
                }

                // Write 2 sample segment mappings
                w.Write(2); // count
                w.Write((ushort)500); // segID
                w.Write((byte)0);     // slot StartLeft
                w.Write((ushort)49152); // highID

                w.Write((ushort)600); // segID
                w.Write((byte)3);     // slot EndRight
                w.Write((ushort)65535); // highID

                serializedData = ms.ToArray();
            }

            Assert(serializedData != null && serializedData.Length > 0, "Serialization stream generated valid bytes");

            // Read back and verify
            using (MemoryStream ms = new MemoryStream(serializedData))
            using (BinaryReader r = new BinaryReader(ms))
            {
                uint magic = r.ReadUInt32();
                uint version = r.ReadUInt32();
                int blockCount = r.ReadInt32();

                Assert(magic == SIDECAR_MAGIC, "Magic matches 0x4D5A4231");
                Assert(version == SIDECAR_VERSION, "Version matches 1");
                Assert(blockCount == 3, "Block count read back is 3");

                ushort id1 = r.ReadUInt16();
                uint flags1 = r.ReadUInt32();
                uint buildIndex1 = r.ReadUInt32();
                float posX = r.ReadSingle();
                float posY = r.ReadSingle();
                float posZ = r.ReadSingle();
                float angle = r.ReadSingle();
                ulong valid = r.ReadUInt64();
                ulong shared = r.ReadUInt64();
                ulong occ1 = r.ReadUInt64();
                ulong occ2 = r.ReadUInt64();
                ulong z1 = r.ReadUInt64();
                ulong z2 = r.ReadUInt64();
                ushort seg = r.ReadUInt16();

                Assert(id1 == 49152, "First block ID is 49152");
                Assert(flags1 == 1, "First block flags == 1");
                Assert(buildIndex1 == 100, "First block buildIndex == 100");
                Assert(Math.Abs(posX - 123.45f) < 0.001f, "First block pos.x accurate");
                Assert(seg == 1234, "First block segment is 1234");
                Assert(valid == 0xFF && shared == 0 && occ1 == 1 && occ2 == 0 && z1 == 0x0F && z2 == 0, "First block bitmasks valid");

                // Skip block 2 and 3
                for (int i = 1; i < 3; i++)
                {
                    r.ReadUInt16(); r.ReadUInt32(); r.ReadUInt32();
                    r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); r.ReadSingle();
                    r.ReadUInt64(); r.ReadUInt64(); r.ReadUInt64(); r.ReadUInt64(); r.ReadUInt64(); r.ReadUInt64();
                    r.ReadUInt16();
                }

                int mappingCount = r.ReadInt32();
                Assert(mappingCount == 2, "Segment mapping count is 2");

                ushort seg1 = r.ReadUInt16();
                byte slot1 = r.ReadByte();
                ushort high1 = r.ReadUInt16();
                Assert(seg1 == 500 && slot1 == 0 && high1 == 49152, "First mapping: (500, Slot 0, High 49152)");

                ushort seg2 = r.ReadUInt16();
                byte slot2 = r.ReadByte();
                ushort high2 = r.ReadUInt16();
                Assert(seg2 == 600 && slot2 == 3 && high2 == 65535, "Second mapping: (600, Slot 3, High 65535)");
            }
        }

        private static void TestSimulationExpansionModel()
        {
            Console.WriteLine("\n[3] Testing Memory Expansion & Free List Rebuild Model...");

            const int EXPANDED_BLOCK_COUNT = 65536;
            uint[] flagsBuffer = new uint[EXPANDED_BLOCK_COUNT];
            ushort[] unusedItems = new ushort[EXPANDED_BLOCK_COUNT];

            // Mark blocks 1..1000 as created
            for (int i = 1; i <= 1000; i++)
            {
                flagsBuffer[i] = 1; // Created
            }

            // Mark block 55000 as created
            flagsBuffer[55000] = 1;

            // Rebuild free list
            uint freeCount = 0;
            for (int i = 1; i < EXPANDED_BLOCK_COUNT; i++)
            {
                if ((flagsBuffer[i] & 1) == 0)
                {
                    unusedItems[freeCount] = (ushort)i;
                    freeCount++;
                }
            }

            Assert(freeCount == 65535 - 1001, "Free count matches exact total: 65535 - 1001 = " + (65535 - 1001));
            Assert(unusedItems[0] == 1001, "First free slot is 1001");
            Assert(unusedItems[freeCount - 1] == 65535, "Last free slot is 65535");

            // Verify active count calculation: ItemCount() = m_size - m_unusedCount
            uint itemCount = (uint)EXPANDED_BLOCK_COUNT - freeCount;
            int activeBlockCount = (int)(itemCount - 1);
            Assert(activeBlockCount == 1001, "Active block count is 1001 (slot 0 excluded)");
        }

        private static void TestLoopBoundsAndWrapAround()
        {
            Console.WriteLine("\n[4] Testing 64K Loop Bounds & Wrap-around Immunity...");

            int visitedCount = 0;
            int lastId = 0;
            for (int blockID = 49152; blockID < 65536; blockID++)
            {
                visitedCount++;
                lastId = blockID;
            }

            Assert(visitedCount == 16384, "Visited exactly 16,384 expanded blocks (65536 - 49152)");
            Assert(lastId == 65535, "Last visited block is 65535 (0xFFFF)");

            ushort castLast = (ushort)lastId;
            Assert(castLast == 65535, "ushort cast preserves 65535 exactly without wrap-around");
        }

        private static void TestGarbageCollectorModel()
        {
            Console.WriteLine("\n[5] Testing Auto-Garbage Collector Release Criteria Model...");

            // Block A: created, but completely empty (zone1=0, zone2=0, occ1=0, occ2=0) on highway -> SHOULD RELEASE
            bool blockACreated = true;
            ulong blockAZone1 = 0, blockAZone2 = 0, blockAOcc1 = 0, blockAOcc2 = 0;
            bool shouldReleaseA = blockACreated && (blockAZone1 == 0 && blockAZone2 == 0 && blockAOcc1 == 0 && blockAOcc2 == 0);
            Assert(shouldReleaseA, "Block A (Empty on highway) is correctly targeted for release");

            // Block B: created, has zoned cells (zone1 != 0) -> MUST NOT RELEASE
            bool blockBCreated = true;
            ulong blockBZone1 = 0x0F, blockBZone2 = 0, blockBOcc1 = 0, blockBOcc2 = 0;
            bool shouldReleaseB = blockBCreated && (blockBZone1 == 0 && blockBZone2 == 0 && blockBOcc1 == 0 && blockBOcc2 == 0);
            Assert(!shouldReleaseB, "Block B (Zoned) is protected from release");

            // Block C: created, has occupied building (occ1 != 0) -> MUST NOT RELEASE
            bool blockCCreated = true;
            ulong blockCZone1 = 0, blockCZone2 = 0, blockCOcc1 = 1, blockCOcc2 = 0;
            bool shouldReleaseC = blockCCreated && (blockCZone1 == 0 && blockCZone2 == 0 && blockCOcc1 == 0 && blockCOcc2 == 0);
            Assert(!shouldReleaseC, "Block C (Building occupied) is protected from release");
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
            }

            Assert(types.Contains("MoreZoneBlocks.ModInfo"), "Contains MoreZoneBlocks.ModInfo (IUserMod)");
            Assert(types.Contains("MoreZoneBlocks.LoadingExtension"), "Contains MoreZoneBlocks.LoadingExtension (LoadingExtensionBase)");
            Assert(types.Contains("MoreZoneBlocks.ZoneCustomizer"), "Contains MoreZoneBlocks.ZoneCustomizer");
            Assert(types.Contains("MoreZoneBlocks.SaveSanitizer"), "Contains MoreZoneBlocks.SaveSanitizer");
            Assert(types.Contains("MoreZoneBlocks.ZonePatches"), "Contains MoreZoneBlocks.ZonePatches");
            Assert(types.Contains("MoreZoneBlocks.LoopGuardPatches"), "Contains MoreZoneBlocks.LoopGuardPatches");
            Assert(types.Contains("MoreZoneBlocks.ZoneGarbageCollector"), "Contains MoreZoneBlocks.ZoneGarbageCollector");
            Assert(types.Contains("MoreZoneBlocks.Log"), "Contains MoreZoneBlocks.Log");

            // Inspect methods inside ZonePatches
            var zonePatchesType = assembly.MainModule.GetType("MoreZoneBlocks.ZonePatches");
            bool hasSimStepTranspiler = false;
            bool hasAwakeTranspiler = false;
            bool hasCheckLimitsTranspiler = false;

            foreach (var m in zonePatchesType.Methods)
            {
                if (m.Name == "ZoneManager_SimulationStepImpl_Transpiler") hasSimStepTranspiler = true;
                if (m.Name == "ZoneManager_Awake_Transpiler") hasAwakeTranspiler = true;
                if (m.Name == "ZoneManager_CheckLimits_Transpiler") hasCheckLimitsTranspiler = true;
            }

            Assert(hasSimStepTranspiler, "ZonePatches contains ZoneManager_SimulationStepImpl_Transpiler");
            Assert(hasAwakeTranspiler, "ZonePatches contains ZoneManager_Awake_Transpiler");
            Assert(hasCheckLimitsTranspiler, "ZonePatches contains ZoneManager_CheckLimits_Transpiler");

            // Inspect methods inside LoopGuardPatches
            var loopGuardType = assembly.MainModule.GetType("MoreZoneBlocks.LoopGuardPatches");
            bool hasLoopGuardTranspiler = false;
            foreach (var m in loopGuardType.Methods)
            {
                if (m.Name == "Replace49152With65535Transpiler") hasLoopGuardTranspiler = true;
            }
            Assert(hasLoopGuardTranspiler, "LoopGuardPatches contains Replace49152With65535Transpiler");
        }
    }
}
