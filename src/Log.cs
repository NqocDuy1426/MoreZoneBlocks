using System;
using UnityEngine;

namespace MoreZoneBlocks
{
    public static class Log
    {
        private const string PREFIX = "[MoreZoneBlocks] ";

        public static void Info(string message)
        {
            Debug.Log(PREFIX + message);
        }

        public static void Warning(string message)
        {
            Debug.LogWarning(PREFIX + message);
        }

        public static void Error(string message)
        {
            Debug.LogError(PREFIX + message);
        }

        public static void Exception(Exception ex, string context = null)
        {
            if (string.IsNullOrEmpty(context))
            {
                Debug.LogError(PREFIX + "Exception: " + ex);
            }
            else
            {
                Debug.LogError(PREFIX + "Exception in " + context + ": " + ex);
            }
        }
    }
}
