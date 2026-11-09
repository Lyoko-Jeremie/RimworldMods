using System;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Util
{
    /// <summary>
    /// 统一日志前缀，便于在 Player.log 中过滤本 Mod 的输出。
    /// </summary>
    public static class PatchLog
    {
        public const string Prefix = "[AO Patch] ";

        public static void Info(string message)
        {
            Log.Message(Prefix + message);
        }

        public static void Warn(string message)
        {
            Log.Warning(Prefix + message);
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
    }
}
