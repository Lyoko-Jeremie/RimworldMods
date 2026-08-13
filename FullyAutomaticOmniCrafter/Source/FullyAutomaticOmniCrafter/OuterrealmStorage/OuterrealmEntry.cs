using System;
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

        public OuterrealmEntryKey(ThingDef def, ThingDef stuff, int quality, int hpBucket, ThingStyleDef style, int colorArgb)
        {
            Def = def;
            Stuff = stuff;
            Quality = quality;
            HpBucket = hpBucket;
            Style = style;
            ColorArgb = colorArgb;
        }

        /// <summary>从真实 Thing 提取分组键（未 Spawned 亦可，属性取自实例本身）。</summary>
        public static OuterrealmEntryKey From(Thing t)
        {
            int quality = -1;
            CompQuality cq = t.TryGetComp<CompQuality>();
            if (cq != null)
            {
                quality = (int)cq.Quality;
            }
            int hpBucket = -1;
            if (t.def.useHitPoints)
            {
                int max = t.MaxHitPoints;
                if (max > 0)
                {
                    hpBucket = Mathf.Clamp(t.HitPoints * 10 / max, 0, 9);
                }
            }
            int colorArgb = -1;
            CompColorable cc = t.TryGetComp<CompColorable>();
            if (cc != null && cc.Active)
            {
                Color c = cc.Color;
                colorArgb = ((int)(c.r * 255f) << 24) | ((int)(c.g * 255f) << 16) | ((int)(c.b * 255f) << 8) | (int)(c.a * 255f);
            }
            return new OuterrealmEntryKey(t.def, t.Stuff, quality, hpBucket, t.StyleDef, colorArgb);
        }

        public bool Equals(OuterrealmEntryKey other)
        {
            return Def == other.Def
                && Stuff == other.Stuff
                && Quality == other.Quality
                && HpBucket == other.HpBucket
                && Style == other.Style
                && ColorArgb == other.ColorArgb;
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
                return h;
            }
        }

        public override string ToString()
        {
            return (Def != null ? Def.defName : "null") + (Stuff != null ? "+" + Stuff.defName : "") + " q" + Quality + " hp" + HpBucket + " s" + (Style != null ? Style.defName : "-") + " c" + ColorArgb;
        }
    }

    /// <summary>
    /// 全局层聚合条目（§3.1）：代表 Thing（未 Spawned，携带完整属性）+ 真实 long 计数。
    /// 每个属性同质分组一条；条目数与个体总量无关（组合级）。
    /// </summary>
    public class OuterrealmEntry : IExposable
    {
        /// <summary>分组键（不序列化，读档后由 Proto 重建）。</summary>
        public OuterrealmEntryKey Key;

        /// <summary>
        /// 代表 Thing：未 Spawned、无持有者（holdingOwner == null），携带该组物品的完整属性
        /// （def/stuff/品质/耐久/样式/颜色）。只作为属性模板与 UI 代表，不参与视图、不 tick。
        /// </summary>
        public Thing Proto;

        /// <summary>真实数量（long，可远超 int.MaxValue）。</summary>
        public long Count;

        /// <summary>视图刷新用：建筑上次看到的全局版本号。</summary>
        public int LastSeenVersion;

        public void ExposeData()
        {
            Scribe_Deep.Look(ref Proto, "proto");
            Scribe_Values.Look(ref Count, "count", 0L);
        }
    }
}
