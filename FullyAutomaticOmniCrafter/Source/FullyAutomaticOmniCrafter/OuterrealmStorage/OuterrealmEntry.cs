using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 条目分组键：属性同质性分组（§3.1）。
    /// 同一分组内的物品属性完全一致（def/stuff/品质/耐久段/样式/颜色），
    /// 因此可合并为一条全局条目（OuterrealmEntry）并共享同一代表 Thing。
    /// 注意：此处为 P1 基础维度；关键 comp 状态签名（如附魔、充电量等）留待后续扩展，
    /// 需要扩展时在此处追加维度并同步更新 GetHashCode/Equals。
    /// </summary>
    public readonly struct OuterrealmEntryKey : IEquatable<OuterrealmEntryKey>
    {
        public readonly ThingDef Def;
        public readonly ThingDef Stuff;
        /// <summary>-1 = 无品质；否则为 (int)QualityCategory（0-6）。</summary>
        public readonly int Quality;
        /// <summary>-1 = 不用耐久分段；0-9 = HitPoints 的 10% 段。</summary>
        public readonly int HpBucket;
        public readonly ThingStyleDef Style;
        /// <summary>-1 = 无颜色；否则为 Color 的 ARGB 压缩值。</summary>
        public readonly int ColorArgb;
        /// <summary>唯一实体标识（默认 -1）。Corpse 用 InnerPawn.thingIDNumber——保证每具尸体独立条目
        /// （尸体为唯一实体不可合并/复制，见 §3.2 注释与 GameComponent_OuterrealmStorage.Withdraw）。</summary>
        public readonly int UniqueId;

        public OuterrealmEntryKey(ThingDef def, ThingDef stuff, int quality, int hpBucket, ThingStyleDef style, int colorArgb, int uniqueId = -1)
        {
            Def = def;
            Stuff = stuff;
            Quality = quality;
            HpBucket = hpBucket;
            Style = style;
            ColorArgb = colorArgb;
            UniqueId = uniqueId;
        }

        /// <summary>从真实 Thing 提取分组键（未 Spawned 亦可，属性取自实例本身）。
        /// MinifiedThing（打包建筑）：用 InnerThing 的属性做分组（def/stuff/品质/耐久），
        /// 使不同打包建筑分属不同条目，且与 Materialize 生成的视图副本 key 一致。
        /// Corpse：UniqueId = InnerPawn.thingIDNumber——每具尸体独立条目（尸体为唯一实体）。</summary>
        public static OuterrealmEntryKey From(Thing t)
        {
            Thing src = t;
            if (t is MinifiedThing minified && minified.InnerThing != null)
            {
                src = minified.InnerThing;
            }
            int uniqueId = -1;
            if (src is Corpse corpse && corpse.InnerPawn != null)
            {
                uniqueId = corpse.InnerPawn.thingIDNumber;
            }
            int quality = -1;
            CompQuality cq = src.TryGetComp<CompQuality>();
            if (cq != null)
            {
                quality = (int)cq.Quality;
            }
            int hpBucket = -1;
            if (src.def.useHitPoints)
            {
                int max = src.MaxHitPoints;
                if (max > 0)
                {
                    hpBucket = Mathf.Clamp(src.HitPoints * 10 / max, 0, 9);
                }
            }
            int colorArgb = -1;
            CompColorable cc = src.TryGetComp<CompColorable>();
            if (cc != null && cc.Active)
            {
                Color c = cc.Color;
                colorArgb = ((int)(c.r * 255f) << 24) | ((int)(c.g * 255f) << 16) | ((int)(c.b * 255f) << 8) | (int)(c.a * 255f);
            }
            return new OuterrealmEntryKey(src.def, src.Stuff, quality, hpBucket, src.StyleDef, colorArgb, uniqueId);
        }

        public bool Equals(OuterrealmEntryKey other)
        {
            return Def == other.Def
                && Stuff == other.Stuff
                && Quality == other.Quality
                && HpBucket == other.HpBucket
                && Style == other.Style
                && ColorArgb == other.ColorArgb
                && UniqueId == other.UniqueId;
        }

        public override bool Equals(object obj)
        {
            return obj is OuterrealmEntryKey key && Equals(key);
        }

        public static bool operator ==(OuterrealmEntryKey a, OuterrealmEntryKey b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(OuterrealmEntryKey a, OuterrealmEntryKey b)
        {
            return !a.Equals(b);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Def != null ? Def.shortHash : 0;
                h = h * 397 ^ (Stuff != null ? Stuff.shortHash : 0);
                h = h * 397 ^ Quality;
                h = h * 397 ^ HpBucket;
                h = h * 397 ^ (Style != null ? Style.shortHash : 0);
                h = h * 397 ^ ColorArgb;
                h = h * 397 ^ UniqueId;
                return h;
            }
        }

        public override string ToString()
        {
            return (Def != null ? Def.defName : "null") + (Stuff != null ? "+" + Stuff.defName : "") + " q" + Quality + " hp" + HpBucket + " s" + (Style != null ? Style.defName : "-") + " c" + ColorArgb + " u" + UniqueId;
        }

        /// <summary>从 ToString 格式恢复分组键（放行列表存档用）。解析失败返回 false（跳过该放行项，无害）。</summary>
        public static bool TryParse(string s, out OuterrealmEntryKey key)
        {
            key = default(OuterrealmEntryKey);
            if (string.IsNullOrEmpty(s))
            {
                return false;
            }
            string[] parts = s.Split(' ');
            if (parts.Length < 3)
            {
                return false;
            }
            string[] bs = parts[0].Split('+');
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(bs[0]);
            if (def == null)
            {
                return false;
            }
            ThingDef stuff = bs.Length > 1 ? DefDatabase<ThingDef>.GetNamedSilentFail(bs[1]) : null;
            int quality = -1;
            int hpBucket = -1;
            int colorArgb = -1;
            int uniqueId = -1;
            ThingStyleDef style = null;
            for (int i = 1; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p.StartsWith("q"))
                {
                    int.TryParse(p.Substring(1), out quality);
                }
                else if (p.StartsWith("hp"))
                {
                    int.TryParse(p.Substring(2), out hpBucket);
                }
                else if (p.StartsWith("s"))
                {
                    string styleName = p.Substring(1);
                    style = styleName == "-" ? null : DefDatabase<ThingStyleDef>.GetNamedSilentFail(styleName);
                }
                else if (p.StartsWith("c"))
                {
                    int.TryParse(p.Substring(1), out colorArgb);
                }
                else if (p.StartsWith("u"))
                {
                    int.TryParse(p.Substring(1), out uniqueId);
                }
            }
            key = new OuterrealmEntryKey(def, stuff, quality, hpBucket, style, colorArgb, uniqueId);
            return true;
        }
    }

    /// <summary>
    /// 全局层聚合条目（§3.1）：权威 Thing 堆（未 Spawned，携带完整状态）+ 真实 long 计数。
    /// 每个属性同质分组一条；条目数与个体总量无关（组合级）。
    /// </summary>
    public class OuterrealmEntry : IExposable
    {
        /// <summary>分组键（不序列化，读档后由 Proto 重建）。</summary>
        public OuterrealmEntryKey Key;

        /// <summary>
        /// 第一权威堆：未 Spawned、无持有者（holdingOwner == null），保存 Thing 子类字段、
        /// 全部 Comp 状态及物品身份；同时作为 UI 投影的展示来源，但绝不只是可丢弃模板。
        /// </summary>
        public Thing Proto;

        /// <summary>
        /// 权威附加堆。Proto 是第一权威堆；当同质物品总量超过单个 Thing 的 int 容量时，
        /// 其余真实堆保存在这里。所有权威堆均保留完整 Thing/Comp 状态，取出时只允许
        /// 转移原实例或调用原版 SplitOff，禁止重新 MakeThing。
        /// </summary>
        public List<Thing> AdditionalProtos;

        /// <summary>真实数量（long，可远超 int.MaxValue）。</summary>
        public long Count;

        /// <summary>视图刷新用：建筑上次看到的全局版本号。</summary>
        public int LastSeenVersion;

        // 投影同步队列的非持久运行时游标。直接挂在条目上可避免每次内容变化分配工作对象；
        // ExposeData 不写入这些字段，读档重建索引时会显式归零。
        internal bool ProjectionSyncQueued;
        internal int ProjectionSyncNextVaultIndex;
        internal int ProjectionSyncGeneration;
        internal int ProjectionSyncProcessingGeneration;
        internal int ProjectionSyncVaultTopologyVersion;

        /// <summary>
        /// 唯一物品的默认存储仓。它只描述查询锚点的默认归位位置，不代表库存所有权；
        /// 全局层仍是唯一真相。搜索期临时出口不写入此字段，避免一次就近搜索永久改变归属。
        /// </summary>
        public Building_OuterrealmVault HomeVault;

        /// <summary>
        /// 默认仓所在地图的持久兜底。HomeVault 被摧毁或跨引用无法恢复时，优先在原地图
        /// 选择新的默认仓。Map.uniqueID 是存档内稳定标识，不依赖 Find.Maps 的列表顺序。
        /// </summary>
        public int HomeMapId = -1;

        public void ExposeData()
        {
            Scribe_Deep.Look(ref Proto, "proto");
            Scribe_Collections.Look(ref AdditionalProtos, "additionalProtos", LookMode.Deep);
            Scribe_Values.Look(ref Count, "count", 0L);
            Scribe_References.Look(ref HomeVault, "homeVault");
            Scribe_Values.Look(ref HomeMapId, "homeMapId", -1);
        }
    }
}
