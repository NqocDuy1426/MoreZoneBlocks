using System;
using System.IO;
using System.Linq;
using ICities;
using UnityEngine;
using HarmonyLib;
using ColossalFramework;
using ColossalFramework.UI;
using ColossalFramework.Packaging;
using MoreZoneBlocks.Core128K;

namespace MoreZoneBlocks
{
    [HarmonyPatch(typeof(RadioPanel), "OnLevelLoaded")]
    public static class RadioPanelPatch
    {
        [HarmonyFinalizer]
        public static Exception Finalizer(Exception __exception)
        {
            if (__exception != null)
            {
                Log.Warning("[MoreZoneBlocks] Handled RadioPanel.OnLevelLoaded safely: " + __exception.Message);
            }
            return null; // Suppress exception to let loading sequence finish cleanly!
        }
    }

    [HarmonyPatch(typeof(GuideManager), "SimulationStepImpl")]
    public static class GuideManagerPatch
    {
        [HarmonyFinalizer]
        public static Exception Finalizer(Exception __exception)
        {
            if (__exception != null)
            {
                Log.Warning("[MoreZoneBlocks] Handled GuideManager.SimulationStepImpl safely: " + __exception.Message);
            }
            return null; // Suppress guide null reference safely to keep simulation running cleanly!
        }
    }

    [HarmonyPatch(typeof(UIDynamicPanels))]
    public static class UIDynamicPanels_Hide_Patch
    {
        [HarmonyPatch("Hide", new Type[] { typeof(string), typeof(int) })]
        [HarmonyPrefix]
        public static void PrefixInt(UIDynamicPanels __instance, string panelName)
        {
            ReconcileModal(__instance, panelName);
        }

        [HarmonyPatch("Hide", new Type[] { typeof(string) })]
        [HarmonyPrefix]
        public static void PrefixSingle(UIDynamicPanels __instance, string panelName)
        {
            ReconcileModal(__instance, panelName);
        }

        private static void ReconcileModal(UIDynamicPanels instance, string panelName)
        {
            try
            {
                if (instance == null || string.IsNullOrEmpty(panelName)) return;
                var cachedPanelsField = AccessTools.Field(typeof(UIDynamicPanels), "m_CachedPanels");
                var cached = cachedPanelsField?.GetValue(instance) as System.Collections.IDictionary;
                if (cached != null && cached.Contains(panelName))
                {
                    object panelInfo = cached[panelName];
                    var isModalProp = AccessTools.Property(panelInfo.GetType(), "isModal");
                    var instProp = AccessTools.Property(panelInfo.GetType(), "instance");
                    bool isModal = (bool)(isModalProp?.GetValue(panelInfo, null) ?? false);
                    UIComponent comp = instProp?.GetValue(panelInfo, null) as UIComponent;

                    if (isModal && comp != null)
                    {
                        var top = UIView.GetModalComponent();
                        if (top != comp)
                        {
                            // Modal stack mismatch guard: Push comp to top of modal stack so Hide() never throws InvalidOperationException!
                            UIView.PushModal(comp);
                        }
                    }
                }
            }
            catch { }
        }
    }

    public class MZBAutoTestLoadingExtension : LoadingExtensionBase
    {
        public override void OnLevelLoaded(LoadMode mode)
        {
            Log.Info("[MZBAutoTestLoadingExtension] OnLevelLoaded fired with mode: " + mode);
            DirectAutoTestBootBehaviour.OnGameLevelLoaded();
        }
    }

    public class DirectAutoTestBootBehaviour : MonoBehaviour
    {
        public static bool s_levelLoadedViaExtension = false;
        private bool loadTriggered = false;
        private bool testerSpawned = false;
        private float postLoadWait = 0f;

        public static void OnGameLevelLoaded()
        {
            s_levelLoadedViaExtension = true;
        }

        void Update()
        {
            if (!File.Exists("/Users/nqocduy/autotest_pending.txt")) return;
            if (!Singleton<LoadingManager>.exists) return;

            var lm = Singleton<LoadingManager>.instance;
            if (lm == null) return;

            if (!loadTriggered)
            {
                if (lm.m_currentlyLoading) return;

                // Continually destroy all modal dialogs, popups and news panels
                SuppressAllPopups();

                var panels = UnityEngine.Object.FindObjectsOfType<UIPanel>();
                if (panels == null || panels.Length < 2) return;

                Log.Info("[MZB_DirectAutoTest] MainMenu UI detected! Suppressing popups and jumping directly into savegame...");
                DoDirectLoad();
                return;
            }

            // Once load was triggered, detect when level is loaded and spawn tester directly
            if (!testerSpawned)
            {
                bool isLevelReady = (s_levelLoadedViaExtension || lm.m_loadingComplete || !lm.m_currentlyLoading) 
                                 && Singleton<ZoneManager>.exists 
                                 && Singleton<ZoneManager>.instance.m_blocks.m_buffer != null;

                if (isLevelReady)
                {
                    postLoadWait += Time.unscaledDeltaTime;
                    if (postLoadWait >= 2f)
                    {
                        testerSpawned = true;
                        Log.Info("[MZB_DirectAutoTest] In-game ready state detected! Verifying 128K buffer & spawning MZB128KSimTester...");
                        
                        var zm = Singleton<ZoneManager>.instance;
                        if (zm.m_blocks.m_buffer.Length < BitStash128K.MAX_BLOCK_COUNT)
                        {
                            ZoneUpdateAndSim128K.Awake_Postfix(zm);
                        }

                        if (UnityEngine.Object.FindObjectOfType<MZB128KSimTester>() == null)
                        {
                            var go = new GameObject("MZB128KSimTester_Direct");
                            DontDestroyOnLoad(go);
                            go.AddComponent<MZB128KSimTester>();
                        }
                    }
                }
            }
        }

        private void SuppressAllPopups()
        {
            try
            {
                var allComponents = UnityEngine.Object.FindObjectsOfType<UIComponent>();
                foreach (var comp in allComponents)
                {
                    if (comp != null && (
                        comp.name.Contains("WhatsNew") || 
                        comp.name.Contains("DLC") || comp.name.Contains("Paradox") || 
                        comp.name.Contains("News") || comp.name.Contains("WorkshopAd") ||
                        comp.name.Contains("FPS") || comp.name.Contains("Booster")))
                    {
                        comp.Hide();
                        comp.isVisible = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[MZB_DirectAutoTest] Suppress popups warning: " + ex.Message);
            }
        }

        private void DoDirectLoad()
        {
            loadTriggered = true;

            try
            {
                string savesDir = "/Users/nqocduy/Library/Application Support/Colossal Order/Cities_Skylines/Saves";
                string[] files = Directory.GetFiles(savesDir, "*.crp");
                
                string requestedSave = null;
                if (File.Exists("/Users/nqocduy/autotest_pending.txt"))
                {
                    string txt = File.ReadAllText("/Users/nqocduy/autotest_pending.txt").Trim();
                    if (!string.IsNullOrEmpty(txt) && txt != "pending" && txt.EndsWith(".crp", StringComparison.OrdinalIgnoreCase))
                    {
                        requestedSave = txt;
                    }
                }

                string saveFile = null;
                if (!string.IsNullOrEmpty(requestedSave))
                {
                    saveFile = files.FirstOrDefault(f => f.EndsWith(requestedSave, StringComparison.OrdinalIgnoreCase) || f.Contains(requestedSave));
                }

                if (saveFile == null)
                {
                    saveFile = files.FirstOrDefault(f => f.EndsWith("backup7.crp", StringComparison.OrdinalIgnoreCase))
                            ?? files.FirstOrDefault(f => f.EndsWith("backup6_Rescued.crp", StringComparison.OrdinalIgnoreCase))
                            ?? files.FirstOrDefault(f => f.EndsWith("backup5.crp", StringComparison.OrdinalIgnoreCase))
                            ?? files.FirstOrDefault(f => f.EndsWith("dreamcity30.crp", StringComparison.OrdinalIgnoreCase))
                            ?? files.FirstOrDefault();
                }

                if (saveFile == null)
                {
                    File.WriteAllText("/Users/nqocduy/autotest_result.log", "STATUS=FAIL\nERROR=No save files found");
                    return;
                }

                Log.Info("[MZB_DirectAutoTest] >>> DIRECT JUMP INTO SAVE: " + saveFile);
                var package = new Package("autotest", saveFile);
                var asset = package.Find(package.packageMainAsset);
                
                var saveMetaData = asset.Instantiate<SaveGameMetaData>();
                if (saveMetaData != null && saveMetaData.mods != null)
                {
                    foreach (var mod in saveMetaData.mods)
                    {
                        if (mod.modName == "MoreZoneBlocks" || mod.modWorkshopID == 3835327455UL)
                        {
                            SaveSanitizer128K.s_isLegacy64KSave = true;
                            Log.Info("[MZB_DirectAutoTest] Detected legacy 64K save (MoreZoneBlocks v1.0) in saveMetaData!");
                            break;
                        }
                    }
                }

                string environment = saveMetaData != null ? saveMetaData.environment : null;
                if (string.IsNullOrEmpty(environment))
                    environment = "Sunny";

                string cityName = (saveMetaData != null && !string.IsNullOrEmpty(saveMetaData.cityName)) ? saveMetaData.cityName : "AutoTestCity";
                
                var meta = new SimulationMetaData();
                meta.m_CityName = cityName;
                meta.m_updateMode = SimulationManager.UpdateMode.LoadGame;
                meta.m_environment = environment;
                
                // IMPORTANT: Scene 1 is "Game", Scene 2 (UI) MUST be "InGame"
                var targetAsset = (saveMetaData != null && saveMetaData.assetRef != null) ? saveMetaData.assetRef : asset;
                SaveSanitizer128K.DetectLegacySave(targetAsset);

                Singleton<LoadingManager>.instance.LoadLevel(targetAsset, "Game", "InGame", meta, false);
                Log.Info("[MZB_DirectAutoTest] >>> LoadLevel(..., 'Game', 'InGame') invoked directly! Bypassed all popups.");
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "MZB_DirectAutoTest.DoDirectLoad");
                File.WriteAllText("/Users/nqocduy/autotest_result.log", "STATUS=FAIL\nLOAD_ERROR=" + ex.ToString());
            }
        }
    }

    public class MZB128KSimTester : MonoBehaviour
    {
        private float elapsed = 0f;
        private int frames = 0;
        private string fatalError = null;
        private bool testsCompleted = false;

        void OnEnable()
        {
            Application.logMessageReceived += OnLogMessageReceived;
        }

        void OnDisable()
        {
            Application.logMessageReceived -= OnLogMessageReceived;
        }

        private void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Exception || type == LogType.Error)
            {
                bool isZoneRelated = condition.Contains("Zone") || condition.Contains("MoreZoneBlocks") ||
                                     condition.Contains("BitStash") || condition.Contains("DualPool") ||
                                     (stackTrace != null && (stackTrace.Contains("Zone") || stackTrace.Contains("MoreZoneBlocks")));

                if (isZoneRelated)
                {
                    // Ignore non-zone benign errors from unrelated mods like FPSCamera or TMPE localization
                    if (!condition.Contains("FPSCamera") && !condition.Contains("translation") && !condition.Contains("locale"))
                    {
                        if (fatalError == null)
                        {
                            fatalError = $"[{type}] {condition}\n{stackTrace}";
                            Log.Error("[MZB128KSimTester] Fatal error detected: " + fatalError);
                        }
                    }
                }
            }
        }

        void Start()
        {
            Log.Info("[MZB128KSimTester] Simulation tester started. Unpausing simulation...");
            try
            {
                SimulationManager.instance.SimulationPaused = false;
                SimulationManager.instance.SelectedSimulationSpeed = 1;
            }
            catch (Exception ex)
            {
                Log.Warning("[MZB128KSimTester] Failed to unpause: " + ex.Message);
            }
        }

        void Update()
        {
            if (testsCompleted) return;

            elapsed += Time.unscaledDeltaTime;
            frames++;

            if (fatalError != null)
            {
                testsCompleted = true;
                string failResult = $"STATUS=FAIL\n"
                                  + $"FATAL_ERROR={fatalError}\n"
                                  + $"ELAPSED={elapsed:F1}s\n"
                                  + $"FRAMES={frames}\n";
                File.WriteAllText("/Users/nqocduy/autotest_result.log", failResult);
                Log.Error("[MZB128KSimTester] Wrote FAIL result:\n" + failResult);
                Application.Quit();
                return;
            }

            // Run active simulation for 30 seconds
            if (elapsed >= 30f)
            {
                testsCompleted = true;
                RunDiagnosticsAndFinish();
            }
        }

        private void RunDiagnosticsAndFinish()
        {
            try
            {
                var zm = ZoneManager.instance;
                if (zm == null || zm.m_blocks == null || zm.m_blocks.m_buffer == null)
                {
                    File.WriteAllText("/Users/nqocduy/autotest_result.log", "STATUS=FAIL\nERROR=ZoneManager or m_blocks is null");
                    Application.Quit();
                    return;
                }

                int bufferLen = zm.m_blocks.m_buffer.Length;
                int updatedLen = zm.m_updatedBlocks != null ? zm.m_updatedBlocks.Length : 0;

                // 1. Buffer Size Verification (Must be 131,072)
                bool bufferOk = (bufferLen == BitStash128K.MAX_BLOCK_COUNT);
                bool updatedOk = (updatedLen == ZoneUpdateAndSim128K.EXTENDED_UPDATED_LEN);

                // 2. Active Zone Blocks Count
                int activeBlocks = 0;
                int lowPoolActive = 0;
                int highPoolActive = 0;
                for (int i = 1; i < bufferLen; i++)
                {
                    if (zm.m_blocks.m_buffer[i].m_flags != 0)
                    {
                        activeBlocks++;
                        if (i <= 65535) lowPoolActive++;
                        else highPoolActive++;
                    }
                }

                // 3. Boundary Access Verification (up to 131,071)
                bool boundaryOk = false;
                try
                {
                    var b0 = zm.m_blocks.m_buffer[0];
                    var b65535 = zm.m_blocks.m_buffer[65535];
                    var b65536 = zm.m_blocks.m_buffer[65536];
                    var b65537 = zm.m_blocks.m_buffer[65537];
                    var bMax = zm.m_blocks.m_buffer[131071];
                    boundaryOk = true;
                }
                catch (IndexOutOfRangeException)
                {
                    boundaryOk = false;
                }

                // 4. DualPoolAllocator128K Verification
                int totalFree = DualPoolAllocator128K.TotalFreeCount;
                int lowFree = DualPoolAllocator128K.LowFreeCount;
                int highFree = DualPoolAllocator128K.HighFreeCount;
                uint testId = 0;
                bool allocTestOk = DualPoolAllocator128K.CreateBlockSmart32(out testId, Vector3.zero);
                if (allocTestOk && testId > 0)
                {
                    DualPoolAllocator128K.ReleaseBlockId32(testId);
                }

                // 5. Grid Heads & Pointer Traversal Integrity (45x45 grid)
                int gridChains = 0;
                bool gridOk = true;
                if (zm.m_zoneGrid != null)
                {
                    for (int c = 0; c < zm.m_zoneGrid.Length; c++)
                    {
                        ushort head = zm.m_zoneGrid[c];
                        if (head >= bufferLen) { gridOk = false; break; }
                        int count = 0;
                        uint curr = head;
                        while (curr != 0)
                        {
                            gridChains++;
                            count++;
                            if (count > BitStash128K.MAX_BLOCK_COUNT) { gridOk = false; break; }
                            curr = BitStash128K.GetNextGridBlock32(ref zm.m_blocks.m_buffer[curr]);
                        }
                        if (!gridOk) break;
                    }
                }

                bool overallSuccess = bufferOk && updatedOk && boundaryOk && gridOk && allocTestOk && (fatalError == null);

                string result = $"STATUS={(overallSuccess ? "SUCCESS" : "FAIL")}\n"
                              + $"BUFFER_LENGTH={bufferLen} (Expected 131072: {(bufferOk ? "OK" : "FAIL")})\n"
                              + $"UPDATED_BLOCKS_LENGTH={updatedLen} (Expected 2048: {(updatedOk ? "OK" : "FAIL")})\n"
                              + $"ACTIVE_BLOCKS_TOTAL={activeBlocks}\n"
                              + $"ACTIVE_LOW_POOL={lowPoolActive}\n"
                              + $"ACTIVE_HIGH_POOL={highPoolActive}\n"
                              + $"BOUNDARY_ACCESS={(boundaryOk ? "OK" : "FAIL")}\n"
                              + $"GRID_INTEGRITY={(gridOk ? "OK" : "FAIL")}, ChainedBlocks={gridChains}\n"
                              + $"ALLOCATOR=TotalFree={totalFree}, LowFree={lowFree}, HighFree={highFree}, TestAllocId={testId}, AllocTest={(allocTestOk ? "OK" : "FAIL")}\n"
                              + $"SIMULATED_FRAMES={frames}\n"
                              + $"SIMULATED_SECONDS={elapsed:F1}\n";

                File.WriteAllText("/Users/nqocduy/autotest_result.log", result);
                Log.Info("[MZB128KSimTester] TEST COMPLETE:\n" + result);

                // Delete trigger
                try { File.Delete("/Users/nqocduy/autotest_pending.txt"); } catch { }
            }
            catch (Exception ex)
            {
                File.WriteAllText("/Users/nqocduy/autotest_result.log", "STATUS=FAIL\nEXCEPTION=" + ex.ToString());
                Log.Exception(ex, "MZB128KSimTester.RunDiagnosticsAndFinish");
            }
            finally
            {
                // Do not auto-quit if keep_open requested, allowing user to view the interface live!
                if (File.Exists("/Users/nqocduy/autotest_auto_quit.txt"))
                {
                    Application.Quit();
                }
            }
        }
    }
}
