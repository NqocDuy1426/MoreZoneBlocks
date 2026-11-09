using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ColossalFramework;

namespace MoreZoneBlocks
{
    /// <summary>
    /// DayNightDeadlockShield:
    /// Protects the game loading pipeline from deadlocks and infinite freezes:
    /// 1. DayNightProperties.InitializeProperties: Replaces with safe coroutine wrapped in try-catch so MapTheme/asset faults never abandon the loading profiler.
    /// 2. LoadingManager.WaitUntilEssentialScenesLoaded: Replaces infinite Monitor.Wait on m_loadingLock with bounded timeout so simulation thread never hangs at EventManager.AfterDeserialize.
    /// 3. TerrainManager.LateUpdateData: Bounded timeout on m_lastUpdatedPatch spin-wait loop so terrain thread faults never deadlock the simulation thread.
    /// 4. TerrainManager.UpdateData: Finalizer armor suppressing crashes during terrain patch setup.
    /// 5. PathManager.WaitForAllPaths: Bounded timeout guard preventing pathfinder hangs.
    /// 6. LoadingManager.QuitApplication: Immediate wake-up pulse on all locks so process never hangs when quitting.
    /// </summary>
    public static class DayNightDeadlockShield
    {
        // ====================================================================
        // 1. DayNightProperties.InitializeProperties
        // ====================================================================
        [HarmonyPatch(typeof(DayNightProperties), "InitializeProperties")]
        public static class DayNightProperties_InitializeProperties_Patch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static bool Prefix(DayNightProperties __instance, ref IEnumerator __result)
            {
                __result = SafeInitializePropertiesCoroutine(__instance);
                return false;
            }

            private static IEnumerator SafeInitializePropertiesCoroutine(DayNightProperties dnp)
            {
                var lm = Singleton<LoadingManager>.instance;
                LoadingProfiler lp = (lm != null) ? lm.m_loadingProfilerMain : null;
                if (lp != null)
                {
                    try { lp.BeginLoading("Daynight Properties"); } catch { }
                }

                try
                {
                    if (dnp != null && Singleton<SimulationManager>.exists)
                    {
                        var meta = Singleton<SimulationManager>.instance.m_metaData;
                        var mtmd = (meta != null) ? meta.m_MapThemeMetaData : null;
                        if (mtmd != null)
                        {
                            try { dnp.m_Longitude = mtmd.longitude; } catch { }
                            try { dnp.m_Latitude = mtmd.latitude; } catch { }
                            try { dnp.m_SunSize = mtmd.sunSize; } catch { }
                            try { dnp.m_SunAnisotropyFactor = mtmd.sunAnisotropy; } catch { }
                            try
                            {
                                if (mtmd.moonTexture != null)
                                {
                                    dnp.m_MoonTexture = mtmd.moonTexture.Instantiate() as Texture;
                                    if (dnp.m_MoonTexture != null)
                                    {
                                        dnp.m_MoonTexture.wrapMode = TextureWrapMode.Clamp;
                                    }
                                }
                            }
                            catch { }
                            try { dnp.m_MoonSize = mtmd.moonSize; } catch { }
                            try { dnp.m_MoonInnerCorona = mtmd.moonInnerCorona; } catch { }
                            try { dnp.m_MoonOuterCorona = mtmd.moonOuterCorona; } catch { }
                            try { dnp.m_RayleighScattering = mtmd.rayleight; } catch { }
                            try { dnp.m_MieScattering = mtmd.mie; } catch { }
                            try { dnp.m_Exposure = mtmd.exposure; } catch { }
                            try { dnp.m_StarsIntensity = mtmd.starsIntensity; } catch { }
                            try { dnp.m_OuterSpaceIntensity = mtmd.outerSpaceIntensity; } catch { }
                            try { dnp.m_SkyTint = mtmd.skyTint; } catch { }
                            try { dnp.m_NightHorizonColor = mtmd.nightHorizonColor; } catch { }
                            try
                            {
                                if (dnp.m_NightZenithColor != null)
                                {
                                    var colorKeys = dnp.m_NightZenithColor.colorKeys;
                                    var alphaKeys = dnp.m_NightZenithColor.alphaKeys;
                                    if (colorKeys != null && colorKeys.Length >= 4)
                                    {
                                        colorKeys[0].color = colorKeys[3].color = mtmd.lateNightZenithColor;
                                        colorKeys[1].color = colorKeys[2].color = mtmd.earlyNightZenithColor;
                                        dnp.m_NightZenithColor.SetKeys(colorKeys, alphaKeys);
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    Log.Info("[DayNightDeadlockShield] DayNightProperties initialized safely ✓");
                }
                catch (Exception ex)
                {
                    Log.Warning("[DayNightDeadlockShield] DayNightProperties exception caught and guarded: " + ex.Message);
                }
                finally
                {
                    if (lp != null)
                    {
                        try { lp.EndLoading(); } catch { }
                    }
                }
                yield return 0;
            }
        }

        // ====================================================================
        // 2. LoadingManager.WaitUntilEssentialScenesLoaded
        // ====================================================================
        [HarmonyPatch(typeof(LoadingManager), "WaitUntilEssentialScenesLoaded")]
        public static class LoadingManager_WaitUntilEssentialScenesLoaded_Patch
        {
            private static FieldInfo s_fLoadingLock;
            private static FieldInfo s_fTerminated;

            private static bool IsTerminated(LoadingManager lm)
            {
                if (lm == null) return false;
                if (s_fTerminated == null)
                {
                    s_fTerminated = typeof(LoadingManager).GetField("m_terminated", BindingFlags.NonPublic | BindingFlags.Instance);
                }
                return (bool)(s_fTerminated?.GetValue(lm) ?? false);
            }

            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static bool Prefix(LoadingManager __instance)
            {
                if (__instance == null) return true;
                try
                {
                    if (__instance.m_essentialScenesLoaded) return false;

                    int waited = 0;
                    const int maxWait = 4000; // 4 seconds bounded timeout
                    while (!__instance.m_essentialScenesLoaded && !IsTerminated(__instance) && waited < maxWait)
                    {
                        System.Threading.Thread.Sleep(20);
                        waited += 20;
                    }

                    if (!__instance.m_essentialScenesLoaded)
                    {
                        Log.Warning(string.Format("[DayNightDeadlockShield] WaitUntilEssentialScenesLoaded timeout ({0}ms). Forcing m_essentialScenesLoaded=true to break deadlock ✓", waited));
                        __instance.m_essentialScenesLoaded = true;

                        if (s_fLoadingLock == null)
                        {
                            s_fLoadingLock = typeof(LoadingManager).GetField("m_loadingLock", BindingFlags.NonPublic | BindingFlags.Instance);
                        }
                        if (s_fLoadingLock != null)
                        {
                            object lockObj = s_fLoadingLock.GetValue(__instance);
                            if (lockObj != null)
                            {
                                lock (lockObj)
                                {
                                    System.Threading.Monitor.PulseAll(lockObj);
                                }
                            }
                        }
                    }
                    return false; // Skip the vanilla infinite while loop!
                }
                catch (Exception ex)
                {
                    Log.Warning("[DayNightDeadlockShield] Error in WaitUntilEssentialScenesLoaded_Prefix: " + ex.Message);
                    return true;
                }
            }
        }

        // ====================================================================
        // 3. TerrainManager.LateUpdateData
        // ====================================================================
        [HarmonyPatch(typeof(TerrainManager), "LateUpdateData", new Type[] { typeof(SimulationManager.UpdateMode) })]
        public static class TerrainManager_LateUpdateData_Patch
        {
            private static FieldInfo s_fLastUpdatedPatch;
            private static FieldInfo s_fUpdateLock;

            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static bool Prefix(TerrainManager __instance, SimulationManager.UpdateMode mode)
            {
                if (__instance == null) return true;
                try
                {
                    if (s_fLastUpdatedPatch == null)
                    {
                        s_fLastUpdatedPatch = typeof(TerrainManager).GetField("m_lastUpdatedPatch", BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                    if (s_fUpdateLock == null)
                    {
                        s_fUpdateLock = typeof(TerrainManager).GetField("m_updateLock", BindingFlags.NonPublic | BindingFlags.Instance)
                                     ?? typeof(TerrainManager).GetField("m_terrainLock", BindingFlags.NonPublic | BindingFlags.Instance);
                    }

                    if (s_fLastUpdatedPatch != null)
                    {
                        int lastPatch = (int)s_fLastUpdatedPatch.GetValue(__instance);
                        if (lastPatch != 80)
                        {
                            int waited = 0;
                            const int maxWait = 2500; // 2.5s bounded wait
                            while (waited < maxWait && (int)s_fLastUpdatedPatch.GetValue(__instance) != 80 && (Singleton<SimulationManager>.exists && !Singleton<SimulationManager>.instance.Terminated))
                            {
                                System.Threading.Thread.Sleep(20);
                                waited += 20;
                            }

                            lastPatch = (int)s_fLastUpdatedPatch.GetValue(__instance);
                            if (lastPatch != 80)
                            {
                                Log.Warning(string.Format("[DayNightDeadlockShield] TerrainManager.m_lastUpdatedPatch={0} timed out after {1}ms. Forcing m_lastUpdatedPatch=80 to eliminate spin deadlock ✓", lastPatch, waited));
                                s_fLastUpdatedPatch.SetValue(__instance, 80);

                                if (s_fUpdateLock != null)
                                {
                                    object lockObj = s_fUpdateLock.GetValue(__instance);
                                    if (lockObj != null)
                                    {
                                        lock (lockObj)
                                        {
                                            System.Threading.Monitor.PulseAll(lockObj);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("[DayNightDeadlockShield] Error in TerrainManager_LateUpdateData_Prefix: " + ex.Message);
                    try
                    {
                        if (s_fLastUpdatedPatch != null) s_fLastUpdatedPatch.SetValue(__instance, 80);
                    }
                    catch { }
                }
                return true; // Let vanilla finish enabling water flow
            }
        }

        // ====================================================================
        // 4. TerrainManager.UpdateData Finalizer
        // ====================================================================
        [HarmonyPatch(typeof(TerrainManager), "UpdateData", new Type[] { typeof(SimulationManager.UpdateMode) })]
        public static class TerrainManager_UpdateData_Patch
        {
            [HarmonyFinalizer]
            public static Exception Finalizer(Exception __exception, TerrainManager __instance)
            {
                if (__exception != null)
                {
                    Log.Warning("[DayNightDeadlockShield] Handled TerrainManager.UpdateData exception safely: " + __exception.Message);
                }
                return null; // Suppress crash
            }
        }

        // ====================================================================
        // 5. PathManager.WaitForAllPaths
        // ====================================================================
        [HarmonyPatch(typeof(PathManager), "WaitForAllPaths")]
        public static class PathManager_WaitForAllPaths_Patch
        {
            private static FieldInfo s_fPathfinds;
            private static FieldInfo s_fQueuedPathCount;
            private static FieldInfo s_fBufferLock;

            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static bool Prefix(PathManager __instance)
            {
                if (__instance == null) return true;
                try
                {
                    if (s_fPathfinds == null)
                    {
                        s_fPathfinds = typeof(PathManager).GetField("m_pathfinds", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                    if (s_fPathfinds != null)
                    {
                        PathFind[] pathfinds = s_fPathfinds.GetValue(__instance) as PathFind[];
                        if (pathfinds != null)
                        {
                            for (int i = 0; i < pathfinds.Length; i++)
                            {
                                var pf = pathfinds[i];
                                if (pf == null) continue;

                                if (s_fQueuedPathCount == null)
                                {
                                    s_fQueuedPathCount = typeof(PathFind).GetField("m_queuedPathCount", BindingFlags.NonPublic | BindingFlags.Instance);
                                }
                                if (s_fBufferLock == null)
                                {
                                    s_fBufferLock = typeof(PathFind).GetField("m_bufferLock", BindingFlags.NonPublic | BindingFlags.Instance);
                                }

                                if (s_fQueuedPathCount != null && s_fBufferLock != null)
                                {
                                    int queued = (int)s_fQueuedPathCount.GetValue(pf);
                                    if (queued > 0)
                                    {
                                        int waited = 0;
                                        const int maxWait = 1500;
                                        while ((int)s_fQueuedPathCount.GetValue(pf) > 0 && waited < maxWait)
                                        {
                                            System.Threading.Thread.Sleep(20);
                                            waited += 20;
                                        }

                                        if ((int)s_fQueuedPathCount.GetValue(pf) > 0)
                                        {
                                            Log.Warning(string.Format("[DayNightDeadlockShield] PathFind #{0} timed out waiting for paths ({1} paths remain). Releasing lock ✓", i, s_fQueuedPathCount.GetValue(pf)));
                                            s_fQueuedPathCount.SetValue(pf, 0);
                                            object bLock = s_fBufferLock.GetValue(pf);
                                            if (bLock != null)
                                            {
                                                lock (bLock) { System.Threading.Monitor.PulseAll(bLock); }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("[DayNightDeadlockShield] Error in PathManager_WaitForAllPaths_Prefix: " + ex.Message);
                }
                return true;
            }
        }

        // ====================================================================
        // 6. LoadingManager.QuitApplication Safety Armor
        // ====================================================================
        [HarmonyPatch(typeof(LoadingManager), "QuitApplication")]
        public static class LoadingManager_QuitApplication_Patch
        {
            private static FieldInfo s_fTerminated;
            private static FieldInfo s_fLoadingLock;

            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static void Prefix(LoadingManager __instance)
            {
                try
                {
                    if (__instance != null)
                    {
                        if (s_fTerminated == null)
                        {
                            s_fTerminated = typeof(LoadingManager).GetField("m_terminated", BindingFlags.NonPublic | BindingFlags.Instance);
                        }
                        s_fTerminated?.SetValue(__instance, true);
                        __instance.m_essentialScenesLoaded = true;

                        if (s_fLoadingLock == null)
                        {
                            s_fLoadingLock = typeof(LoadingManager).GetField("m_loadingLock", BindingFlags.NonPublic | BindingFlags.Instance);
                        }
                        if (s_fLoadingLock != null)
                        {
                            object lockObj = s_fLoadingLock.GetValue(__instance);
                            if (lockObj != null)
                            {
                                lock (lockObj) { System.Threading.Monitor.PulseAll(lockObj); }
                            }
                        }
                    }

                    if (Singleton<TerrainManager>.exists && Singleton<TerrainManager>.instance != null)
                    {
                        var tm = Singleton<TerrainManager>.instance;
                        var fLock = typeof(TerrainManager).GetField("m_updateLock", BindingFlags.NonPublic | BindingFlags.Instance)
                                 ?? typeof(TerrainManager).GetField("m_terrainLock", BindingFlags.NonPublic | BindingFlags.Instance);
                        var fPatch = typeof(TerrainManager).GetField("m_lastUpdatedPatch", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (fPatch != null) fPatch.SetValue(tm, 80);
                        if (fLock != null)
                        {
                            object lockObj = fLock.GetValue(tm);
                            if (lockObj != null)
                            {
                                lock (lockObj) { System.Threading.Monitor.PulseAll(lockObj); }
                            }
                        }
                    }
                }
                catch { }
            }
        }
    }
}
