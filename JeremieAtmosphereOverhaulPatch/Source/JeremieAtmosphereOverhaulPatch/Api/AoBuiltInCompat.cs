using System;
using System.Collections.Generic;
using Jeremie.AtmosphereOverhaulPatch.Util;

namespace Jeremie.AtmosphereOverhaulPatch.Api
{
    /// <summary>
    /// 检测 AO 自身是否已经内置了某个目标 Mod 的兼容。
    ///
    /// AO 的兼容是一个独立程序集（例如 Dubs Bad Hygiene 的 AtmosphereOverhaul.DBH），
    /// 并靠 LoadFolders 的 IfModActive 条件加载。因此「AO 已内置兼容」等价于
    /// 「AO 程序集目录里出现了对应的兼容类型」。
    ///
    /// 这里只做类型存在性探测，不依赖 AO 版本号——AO 更新后只要类型名不变就仍然有效；
    /// 类型名变了则会误判为「未内置」，此时用户在设置里手动关掉对应模块即可。
    /// </summary>
    public static class AoBuiltInCompat
    {
        private static readonly Dictionary<string, string[]> Candidates =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "GOS.GoodClimate",
                    new[]
                    {
                        "AtmosphereOverhaul.Compat.GoodClimateCompat",
                        "AtmosphereOverhaul.Patches.GoodClimatePatch",
                    }
                },
                {
                    "VanillaExpanded.Temperature",
                    new[]
                    {
                        "AtmosphereOverhaul.Compat.VanillaTemperatureExpandedCompat",
                        "AtmosphereOverhaul.Compat.VteCompat",
                    }
                },
                {
                    "Mlie.CentralizedClimateControl",
                    new[]
                    {
                        "AtmosphereOverhaul.Compat.CentralizedClimateControlCompat",
                        "AtmosphereOverhaul.Compat.CccCompat",
                    }
                },
                {
                    "Jeremie.Fully.Automatic.OmniCrafter",
                    new[]
                    {
                        "AtmosphereOverhaul.Compat.OmniCrafterCompat",
                        "AtmosphereOverhaul.Compat.OmniCompat",
                    }
                },
                {
                    "Dubwise.DubsBadHygiene",
                    new[]
                    {
                        "AtmosphereOverhaul.Pawns.HydrationHooks",
                        "AtmosphereOverhaul.Compat.DbhCompat",
                    }
                },
            };

        private static readonly Dictionary<string, string> Cache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 若 AO 已内置该 packageId 的兼容，返回命中的类型全名；否则返回 null。
        /// </summary>
        public static string Evidence(string packageId)
        {
            if (string.IsNullOrEmpty(packageId))
            {
                return null;
            }

            string cached;
            if (Cache.TryGetValue(packageId, out cached))
            {
                return cached;
            }

            string found = null;
            string[] names;
            if (Candidates.TryGetValue(packageId, out names) && names != null)
            {
                for (int i = 0; i < names.Length; i++)
                {
                    if (ReflectionCache.Type(names[i]) != null)
                    {
                        found = names[i];
                        PatchLog.Info("检测到 AO 已内置 " + packageId + " 兼容（" + found + "），对应模块将自动跳过。");
                        break;
                    }
                }
            }

            Cache[packageId] = found;
            return found;
        }

        /// <summary>设置变更后清空缓存，让「强制覆盖」开关能立即生效。</summary>
        public static void ClearCache()
        {
            Cache.Clear();
        }
    }
}
