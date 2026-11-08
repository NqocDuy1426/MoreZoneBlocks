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
    /// in LoadingProfiler.BeginDeserialize / EndDeserialize (2-string overload: WeatherManager / WindManager).
    /// Dynamically realigns stream pointers if upstream mods (e.g. TreeUnlimiter) misalign the stream,
    /// and unconditionally suppresses exceptions to prevent simulation thread crashes and infinite loading screen hangs.
    /// </summary>
    [HarmonyPatch]
    public static class StreamTagShield
    {
        [HarmonyPatch(typeof(LoadingProfiler), "BeginDeserialize", new Type[] { typeof(DataSerializer), typeof(string), typeof(string) })]
        [HarmonyPrefix]
        public static bool BeginDeserialize_2Strings_Prefix(LoadingProfiler __instance, DataSerializer s, string name1, string name2)
        {
            return GuardTag(s, name1, name2, true);
        }

        [HarmonyPatch(typeof(LoadingProfiler), "EndDeserialize", new Type[] { typeof(DataSerializer), typeof(string), typeof(string) })]
        [HarmonyPrefix]
        public static bool EndDeserialize_2Strings_Prefix(LoadingProfiler __instance, DataSerializer s, string name1, string name2)
        {
            return GuardTag(s, name1, name2, false);
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

                        if (match1 || match2)
                        {
                            s.ReadSharedString(); // Clean match at current pos!
                            return false; // Skip vanilla throw
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
                        Log.Warning($"[StreamTagShield] Suppressed {(isBegin ? "Begin" : "End")} tag mismatch ({name1}/{name2}, read '{readTag}'). Proceeding safely ✓");
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
