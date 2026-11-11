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
    /// in LoadingProfiler.BeginDeserialize / EndDeserialize 2-string overload (WeatherManager/WindManager).
    /// The 1-string overload is already handled by MoreNetworkCapacityRenew Universal Stream Armor.
    /// </summary>
    public static class StreamTagShield
    {
        public static void ApplyManual(Harmony harmony)
        {
            try
            {
                Log.Info("[StreamTagShield] ApplyManual starting...");
                var t = typeof(StreamTagShield);

                // ONLY patch the 2-string overloads (WeatherManager/WindManager).
                // The 1-string overloads are handled by MoreNetworkCapacityRenew — patching them here
                // would cause double-consumption of stream tags and corrupt the save stream.
                var beginDes2 = typeof(LoadingProfiler).GetMethod("BeginDeserialize", BindingFlags.Public | BindingFlags.Instance, null,
                    new Type[] { typeof(DataSerializer), typeof(string), typeof(string) }, null);
                Log.Info("[StreamTagShield] BeginDeserialize 2-string lookup: " + (beginDes2 != null ? beginDes2.ToString() : "NULL"));
                if (beginDes2 != null)
                {
                    var prefixMethod = t.GetMethod(nameof(BeginDeserialize_2Strings_Prefix), BindingFlags.Public | BindingFlags.Static);
                    Log.Info("[StreamTagShield] Prefix method: " + (prefixMethod != null ? prefixMethod.ToString() : "NULL"));
                    harmony.Patch(beginDes2, prefix: new HarmonyMethod(prefixMethod));
                    Log.Info("[StreamTagShield] Patched BeginDeserialize(s, string, string) ✓");
                }

                var endDes2 = typeof(LoadingProfiler).GetMethod("EndDeserialize", BindingFlags.Public | BindingFlags.Instance, null,
                    new Type[] { typeof(DataSerializer), typeof(string), typeof(string) }, null);
                Log.Info("[StreamTagShield] EndDeserialize 2-string lookup: " + (endDes2 != null ? endDes2.ToString() : "NULL"));
                if (endDes2 != null)
                {
                    var prefixMethod = t.GetMethod(nameof(EndDeserialize_2Strings_Prefix), BindingFlags.Public | BindingFlags.Static);
                    harmony.Patch(endDes2, prefix: new HarmonyMethod(prefixMethod));
                    Log.Info("[StreamTagShield] Patched EndDeserialize(s, string, string) ✓");
                }

                Log.Info("[StreamTagShield] ApplyManual completed.");
            }
            catch (Exception ex)
            {
                Log.Error("[StreamTagShield] ApplyManual FAILED: " + ex);
            }
        }

        public static bool BeginDeserialize_2Strings_Prefix(LoadingProfiler __instance, DataSerializer s, string name1, string name2)
        {
            RecordEvent(__instance, true, name1);
            return GuardTag(s, name1, name2, true);
        }

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

                        if (match1 || match2 || p1 == 254)
                        {
                            string readTag = s.ReadSharedString();
                            if (readTag == name1 || (!string.IsNullOrEmpty(name2) && readTag == name2))
                            {
                                return false; // Clean match
                            }
                            Log.Warning($"[StreamTagShield] Suppressed {(isBegin ? "Begin" : "End")} tag mismatch ({name1}/{name2}, read '{readTag}'). Proceeding safely ✓");
                            return false;
                        }

                        // Stream misaligned: search forward up to 2MB
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
                            s.ReadSharedString();
                            Log.Info($"[StreamTagShield] Aligned {(isBegin ? "Begin" : "End")} tag ({name1}/{name2}) from {curPos} to {foundPos} ✓");
                            return false;
                        }

                        st.Seek(curPos, SeekOrigin.Begin);
                    }

                    // Fallback safe read
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
            return false;
        }
    }
}
