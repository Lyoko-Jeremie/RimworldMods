using System.Collections.Generic;
using ToolGood.Words;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 高性能拼音搜索索引。
    /// 在 OmniCrafterCache 构建完成后调用 BuildIndex 预处理所有 ThingDef 的拼音数据，
    /// 后续每次搜索只做字典查找 + 字符串比较，无额外堆内存分配。
    ///
    /// 搜索规则（关键词已小写）：
    ///   · Initials.StartsWith(keyword)     首字母缩写前缀匹配，如 "zg" 命中 "中国"
    ///   · FullPinyin.Contains(keyword)     全拼子串匹配，如   "guo" 命中 "中国"
    /// </summary>
    public static class PinyinSearchEngine
    {
        // ── 内部数据结构 ────────────────────────────────────────────────────────

        /// <summary>
        /// 每个 ThingDef 对应的拼音预处理结果（struct，避免额外堆分配）。
        /// </summary>
        private struct PinyinEntry
        {
            /// <summary>全拼，小写，无声调，无分隔符。如"中国"→"zhongguo"</summary>
            public string FullPinyin;
            /// <summary>首字母缩写，小写。如"中国"→"zg"</summary>
            public string Initials;
        }

        // 使用 ThingDef 引用作 key——比 string key 少一次哈希计算
        // 初始容量预留 4096，游戏内通常几千到数万条目
        private static readonly Dictionary<ThingDef, PinyinEntry> _index =
            new Dictionary<ThingDef, PinyinEntry>(4096);

        private static bool _isReady;

        // ── 公开接口 ────────────────────────────────────────────────────────────

        /// <summary>索引已就绪（BuildIndex 成功执行后为 true）。</summary>
        public static bool IsReady => _isReady;

        /// <summary>
        /// 重建拼音索引。在 OmniCrafterCache.BuildCache 结束后调用。
        /// 此方法在游戏加载时执行一次，允许产生较多 GC，搜索阶段不再分配。
        /// </summary>
        public static void BuildIndex(List<ThingDef> defs)
        {
            _index.Clear();
            _isReady = false;

            if (defs == null || defs.Count == 0) return;

            for (int i = 0; i < defs.Count; i++)
            {
                ThingDef def = defs[i];
                if (def == null) continue;

                string rawLabel = def.label ?? def.defName ?? "";
                string fullPinyin = string.Empty;
                string initials   = string.Empty;

                try
                {
                    if (WordsHelper.HasChinese(rawLabel))
                    {
                        // GetPinyin(text, separator, tone=false) → 无声调全拼，分隔符为 ""
                        // 例："中国" → "zhongguo"
                        string raw = WordsHelper.GetPinyin(rawLabel, "", false);
                        fullPinyin = raw != null ? raw.ToLower() : string.Empty;

                        // GetFirstPinyin → 大写首字母串，例 "中国" → "ZG"
                        string ini = WordsHelper.GetFirstPinyin(rawLabel);
                        initials = ini != null ? ini.ToLower() : string.Empty;
                    }
                }
                catch
                {
                    // 个别 def 转换失败时，保留空字符串，搜索时跳过拼音匹配即可
                }

                _index[def] = new PinyinEntry
                {
                    FullPinyin = fullPinyin,
                    Initials   = initials
                };
            }

            _isReady = true;
            Log.Message($"[OmniCrafter] PinyinSearchEngine: indexed {_index.Count} items.");
        }

        /// <summary>
        /// 使索引失效（OmniCrafterCache 失效时一并调用）。
        /// </summary>
        public static void Invalidate()
        {
            _isReady = false;
            // 不 Clear _index，保留内存以备下次 BuildIndex 复用桶数组
        }

        /// <summary>
        /// 判断 def 是否在拼音维度上匹配给定关键词。
        /// <param name="keyword">已转小写的搜索关键词</param>
        /// 调用前应确认 IsReady == true。
        /// </summary>
        public static bool MatchesPinyin(ThingDef def, string keyword)
        {
            if (def == null || string.IsNullOrEmpty(keyword)) return false;

            PinyinEntry entry;
            if (!_index.TryGetValue(def, out entry)) return false;

            // 1. 首字母前缀匹配（最短路径，优先检测）
            if (entry.Initials.Length > 0 &&
                entry.Initials.StartsWith(keyword, System.StringComparison.Ordinal))
                return true;

            // 2. 全拼子串匹配（允许搜索任意音节片段）
            if (entry.FullPinyin.Length > 0 &&
                entry.FullPinyin.IndexOf(keyword, System.StringComparison.Ordinal) >= 0)
                return true;

            return false;
        }
    }
}