using System;
using System.Collections.Generic;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Util
{
    /// <summary>
    /// 统一日志前缀，便于在 Player.log 中过滤本 Mod 的输出。
    /// </summary>
    public static class PatchLog
    {
        public const string Prefix = "[AO Patch] ";

        /// <summary>WarnOnce 的去重键集合。只在主线程写，无需加锁。</summary>
        private static readonly HashSet<string> WarnedKeys = new HashSet<string>();

        public static void Info(string message)
        {
            Log.Message(Prefix + message);
        }

        public static void Warn(string message)
        {
            Log.Warning(Prefix + message);
        }

        /// <summary>同一条消息在本次会话里只警告一次，避免每 tick 刷屏。</summary>
        public static void WarnOnce(string key, string message)
        {
            if (key == null)
            {
                Warn(message);
                return;
            }

            if (WarnedKeys.Add(key))
            {
                Warn(message);
            }
        }

        public static void Error(string message)
        {
            Log.Error(Prefix + message);
        }

        public static void Error(string message, Exception exception)
        {
            Log.Error(Prefix + message + "\n" + exception);
        }

        /// <summary>仅在设置里开启详细日志时输出，避免刷屏。</summary>
        public static void Debug(string message)
        {
            if (AOPatchMod.Settings != null && AOPatchMod.Settings.VerboseLogging)
            {
                Log.Message(Prefix + "[debug] " + message);
            }
        }

        /// <summary>带 i18n 的日志：找不到翻译时退回 key 本身。</summary>
        public static string T(string key)
        {
            try
            {
                return key.Translate();
            }
            catch
            {
                return key;
            }
        }
    }
}
