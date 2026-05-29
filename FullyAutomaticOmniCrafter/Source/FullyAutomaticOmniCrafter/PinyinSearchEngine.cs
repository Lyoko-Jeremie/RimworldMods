using System.Collections.Generic;
using ToolGood.Words;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public enum PinyinSource
    {
        Thing,
        Hediff,
        Incident,
        Recipe,
        PawnKind
    }

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

        private static readonly Dictionary<Def, PinyinEntry> _index =
            new Dictionary<Def, PinyinEntry>(8192);

        private static readonly HashSet<PinyinSource> _indexedSources = new HashSet<PinyinSource>();

        private static bool _isReady;

        // ── 公开接口 ────────────────────────────────────────────────────────────

        /// <summary>索引已就绪（至少有一种类型的 Def 已索引）。</summary>
        public static bool IsReady => _isReady;

        /// <summary>
        /// 检查特定来源的 Def 是否已建立拼音索引。
        /// </summary>
        public static bool IsSourceIndexed(PinyinSource source)
        {
            return _indexedSources.Contains(source);
        }

        /// <summary>
        /// 全量重建拼音索引。会清空现有所有类型的索引。
        /// </summary>
        public static void BuildIndex<T>(List<T> defs, PinyinSource source) where T : Def
        {
            _index.Clear();
            _indexedSources.Clear();
            _isReady = false;
            IndexDefs(defs, source);

            _isReady = true;
            Log.Message($"[OmniCrafter] PinyinSearchEngine: Rebuilt index with {defs.Count} items of source {source}. Total: {_index.Count}");
        }

        /// <summary>
        /// 追加/更新指定 Def 列表的拼音索引，不清空已有索引。
        /// 适合多个窗口按需引入不同 Def 类型（RecipeDef/HediffDef/PawnKindDef 等）。
        /// </summary>
        public static void EnsureIndexed<T>(List<T> defs, PinyinSource source) where T : Def
        {
            if (defs == null || defs.Count == 0) return;
            if (IsSourceIndexed(source)) return;

            IndexDefs(defs, source);
            _isReady = true;
            Log.Message($"[OmniCrafter] PinyinSearchEngine: Appended {defs.Count} items of source {source}. Total: {_index.Count}");
        }

        /// <summary>
        /// 使索引失效（OmniCrafterCache 失效时一并调用）。
        /// </summary>
        public static void Invalidate()
        {
            _isReady = false;
            _index.Clear();
            _indexedSources.Clear();
        }

        /// <summary>
        /// 判断 def 是否在拼音维度上匹配给定关键词。
        /// <param name="keyword">已转小写的搜索关键词</param>
        /// 调用前应确认 IsReady == true。
        /// </summary>
        public static bool MatchesPinyin(Def def, string keyword)
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

        private static void IndexDefs<T>(List<T> defs, PinyinSource source) where T : Def
        {
            if (defs == null || defs.Count == 0) return;

            for (int i = 0; i < defs.Count; i++)
            {
                T def = defs[i];
                if (def == null) continue;

                string rawLabel = def.label ?? def.defName ?? "";
                string fullPinyin = string.Empty;
                string initials = string.Empty;

                try
                {
                    if (WordsHelper.HasChinese(rawLabel))
                    {
                        string raw = WordsHelper.GetPinyin(rawLabel, "", false);
                        fullPinyin = raw != null ? raw.ToLower() : string.Empty;

                        string ini = WordsHelper.GetFirstPinyin(rawLabel);
                        initials = ini != null ? ini.ToLower() : string.Empty;
                    }
                }
                catch
                {
                    // Ignore conversion failure for a single def; keep empty pinyin fields.
                }

                _index[def] = new PinyinEntry
                {
                    FullPinyin = fullPinyin,
                    Initials = initials
                };
            }

            _indexedSources.Add(source);
        }
    }
}