using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ColossalFramework.IO;

namespace MoreZoneBlocks
{
    /// <summary>
    /// StreamTagShield: Protects the loading pipeline from fatal tag mismatch exceptions
    /// in LoadingProfiler.BeginDeserialize / EndDeserialize (both 1-string and 2-string overloads).
    /// Dynamically realigns stream pointers if upstream mods (e.g. TreeUnlimiter) misalign the stream,
    /// and unconditionally suppresses exceptions to prevent simulation thread crashes and infinite loading screen hangs.
    /// </summary>
    [HarmonyPatch]
    public static class StreamTagShield
    {
        [HarmonyPatch(typeof(LoadingProfiler), "BeginDeserialize", new Type[] { typeof(DataSerializer), typeof(string) })]
        [HarmonyPrefix]
        public static bool BeginDeserialize_1String_Prefix(LoadingProfiler __instance, DataSerializer s, string name)
        {
            RecordEvent(__instance, true, name);
            return GuardTag(s, name, null, true);
        }

        [HarmonyPatch(typeof(LoadingProfiler), "EndDeserialize", new Type[] { typeof(DataSerializer), typeof(string) })]
        [HarmonyPrefix]
        public static bool EndDeserialize_1String_Prefix(LoadingProfiler __instance, DataSerializer s, string name)
        {
            RecordEvent(__instance, false, name);
            return GuardTag(s, name, null, false);
        }

        [HarmonyPatch(typeof(LoadingProfiler), "BeginDeserialize", new Type[] { typeof(DataSerializer), typeof(string), typeof(string) })]
        [HarmonyPrefix]
        public static bool BeginDeserialize_2Strings_Prefix(LoadingProfiler __instance, DataSerializer s, string name1, string name2)
        {
            RecordEvent(__instance, true, name1);
            return GuardTag(s, name1, name2, true);
        }

        [HarmonyPatch(typeof(LoadingProfiler), "EndDeserialize", new Type[] { typeof(DataSerializer), typeof(string), typeof(string) })]
        [HarmonyPrefix]
        public static bool EndDeserialize_2Strings_Prefix(LoadingProfiler __instance, DataSerializer s, string name1, string name2)
        {
            RecordEvent(__instance, false, name1);
            return GuardTag(s, name1, name2, false);
        }

        private static void RecordEvent(LoadingProfiler profiler, bool isBegin, string name)
        {
            try
            {
                if (profiler == null) return;
                var fiEvents = typeof(LoadingProfiler).GetField("m_events", BindingFlags.NonPublic | BindingFlags.Instance);
                var fiStopwatch = typeof(LoadingProfiler).GetField("m_stopwatch", BindingFlags.NonPublic | BindingFlags.Instance);
                if (fiEvents != null && fiStopwatch != null)
                {
                    var sw = fiStopwatch.GetValue(profiler) as System.Diagnostics.Stopwatch;
                    long elapsed = (sw != null) ? sw.ElapsedMilliseconds : 0L;
                    var events = fiEvents.GetValue(profiler);
                    if (events != null)
                    {
                        Type evType = typeof(LoadingProfiler).GetNestedType("Event", BindingFlags.NonPublic | BindingFlags.Public);
                        MethodInfo miAdd = events.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
                        if (evType != null && miAdd != null)
                        {
                            object ev = Activator.CreateInstance(evType, new object[] { isBegin ? 6 : 7, isBegin ? name : null, elapsed });
                            miAdd.Invoke(events, new object[] { ev });
                        }
                    }
                }
            }
            catch { }
        }

        private static bool GuardTag(DataSerializer s, string name1, string name2, bool isBegin)
        {
            try
            {
                if (s != null && s.version >= 130u)
                {
                    var fiStrings = typeof(DataSerializer).GetField("m_stringList", BindingFlags.NonPublic | BindingFlags.Instance);
                    var fiStream = typeof(DataSerializer).GetField("m_stream", BindingFlags.NonPublic | BindingFlags.Instance);
                    Stream st = fiStream != null ? (Stream)fiStream.GetValue(s) : null;
                    List<string> strList = fiStrings != null ? (List<string>)fiStrings.GetValue(s) : null;

                    if (st != null && st.CanSeek && strList != null)
                    {
                        int tagIdx1 = strList.IndexOf(name1);
                        int tagIdx2 = !string.IsNullOrEmpty(name2) ? strList.IndexOf(name2) : -1;

                        long curPos = st.Position;
                        int p1 = st.ReadByte();
                        int p2 = st.ReadByte();
                        st.Seek(curPos, SeekOrigin.Begin);

                        bool match1 = tagIdx1 >= 0 && p1 == (byte)(tagIdx1 >> 8) && p2 == (byte)(tagIdx1 & 0xFF);
                        bool match2 = tagIdx2 >= 0 && p1 == (byte)(tagIdx2 >> 8) && p2 == (byte)(tagIdx2 & 0xFF);

                        // If it's a known index match or unique string marker (254)
                        if (match1 || match2 || p1 == 254)
                        {
                            string readTag = s.ReadSharedString();
                            if (readTag == name1 || (!string.IsNullOrEmpty(name2) && readTag == name2))
                            {
                                return false; // Clean clean match (no throw, no false alarm log)
                            }
                            Log.Warning($"[StreamTagShield] Suppressed {(isBegin ? "Begin" : "End")} tag mismatch ({name1}/{name2}, read '{readTag}'). Proceeding safely ✓");
                            return false;
                        }

                        // Stream misaligned: search forward up to 2MB for tag1 or tag2
                        long foundPos = -1;
                        long maxSearch = Math.Min(st.Length - 4, curPos + 2 * 1024 * 1024);
                        for (long pos = curPos; pos < maxSearch; pos++)
                        {
                            int s1 = st.ReadByte();
                            if ((tagIdx1 >= 0 && s1 == (byte)(tagIdx1 >> 8)) || (tagIdx2 >= 0 && s1 == (byte)(tagIdx2 >> 8)))
                            {
                                int s2 = st.ReadByte();
                                bool isMatch1 = tagIdx1 >= 0 && s1 == (byte)(tagIdx1 >> 8) && s2 == (byte)(tagIdx1 & 0xFF);
                                bool isMatch2 = tagIdx2 >= 0 && s1 == (byte)(tagIdx2 >> 8) && s2 == (byte)(tagIdx2 & 0xFF);

                                if (isMatch1 || isMatch2)
                                {
                                    foundPos = pos;
                                    break;
                                }
                                st.Seek(pos + 1, SeekOrigin.Begin);
                            }
                        }

                        if (foundPos != -1)
                        {
                            st.Seek(foundPos, SeekOrigin.Begin);
                            s.ReadSharedString(); // Cleanly consume found tag
                            Log.Info($"[StreamTagShield] Aligned {(isBegin ? "Begin" : "End")} tag ({name1}/{name2}) from {curPos} to {foundPos} ✓");
                            return false;
                        }

                        // Not found within 2MB: reset position
                        st.Seek(curPos, SeekOrigin.Begin);
                    }

                    // Fallback safe read without throwing
                    try
                    {
                        string readTag = s.ReadSharedString();
                        if (readTag != name1 && (string.IsNullOrEmpty(name2) || readTag != name2))
                        {
                            Log.Warning($"[StreamTagShield] Suppressed {(isBegin ? "Begin" : "End")} tag mismatch ({name1}/{name2}, read '{readTag}'). Proceeding safely ✓");
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[StreamTagShield] Guard error on {name1}/{name2}: {ex.Message}");
            }
            return false; // Always prevent Vanilla from throwing and killing simulation loading!
        }
    }
}
