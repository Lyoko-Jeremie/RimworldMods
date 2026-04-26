using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace FullyAutomaticOmniCrafter
{
    // ─── 品质乘数表 ─────────────────────────────────────────────────────────────
    public static class MecEnergyCalc
    {
        private static readonly float[] QualityMult =
        {
            0.5f,  // Awful
            0.8f,  // Poor
            1.0f,  // Normal
            1.2f,  // Good
            1.5f,  // Excellent
            2.0f,  // Masterwork
            3.0f   // Legendary
        };

        /// <summary>
        /// 计算一件物品转化产生的能量（Wd）。
        /// E = (MarketValue + Mass + MaxHitPoints) × QualityMultiplier
        /// </summary>
        public static float CalcEnergy(Thing thing)
        {
            if (thing == null) return 0f;

            float marketValue = thing.GetStatValue(StatDefOf.MarketValue);
            float mass = thing.GetStatValue(StatDefOf.Mass);
            float maxHp = thing.MaxHitPoints;

            float qualMult = 1.0f;
            if (thing.TryGetQuality(out QualityCategory qc))
                qualMult = QualityMult[(int)qc];

            float perItem = (marketValue + mass + maxHp) * qualMult;
            return perItem * thing.stackCount;
        }
    }

    // ─── 主建筑类 ──────────────────────────────────────────────────────────────
    /// <summary>
    /// 物质-能量转化仪：将物品分解为电能，注入已连接的无限电容（CompOmniCrafterSmartInfiniteBattery）。
    /// 支持三种转化模式：
    ///   A - 载入模式（通过 CompTransporter 装载物品）
    ///   B - 存储区批量模式（建筑作为存储区，一键转化）
    ///   C - 光标直接点选模式（瞄准并即刻转化目标）
    /// </summary>
    public class Building_MatterEnergyConverter : Building_Storage
    {
        // ── 内部状态 ──────────────────────────────────────────────────────────
        private CompPowerTrader powerComp;
        private CompTransporter transporterComp;

        // ── 常量 ──────────────────────────────────────────────────────────────
        private static readonly SoundDef ConvertSound = SoundDefOf.EnergyShield_AbsorbDamage;

        // ── 初始化 ────────────────────────────────────────────────────────────
        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            powerComp = GetComp<CompPowerTrader>();
            transporterComp = GetComp<CompTransporter>();
        }

        // ── 核心：将物品转为电能 ───────────────────────────────────────────────
        /// <summary>
        /// 销毁物品，计算能量并注入电池。返回产生的能量（Wd）。
        /// </summary>
        public float RecycleThing(Thing thing)
        {
            if (thing == null || thing.Destroyed) return 0f;
            // 安全黑名单：禁止直接转化Pawn
            if (thing is Pawn) return 0f;

            float energy = MecEnergyCalc.CalcEnergy(thing);

            // 特效
            if (thing.Spawned)
            {
                FleckMaker.ThrowLightningGlow(thing.DrawPos, Map, 1.5f);
                FleckMaker.ThrowSmoke(thing.DrawPos, Map, 1.0f);
            }

            // 销毁物品
            thing.Destroy(DestroyMode.Vanish);

            // 注入电能
            if (energy > 0f)
                InjectEnergy(energy);

            // 播放音效
            if (Spawned)
                ConvertSound?.PlayOneShot(new TargetInfo(Position, Map));

            return energy;
        }

        /// <summary>
        /// 将电能注入本建筑所在电网中的 CompOmniCrafterSmartInfiniteBattery，
        /// 若不存在则注入普通电池，最后才直接充给 self（若有 CompPowerBattery）。
        /// </summary>
        private void InjectEnergy(float energyWd)
        {
            PowerNet net = powerComp?.PowerNet;
            if (net == null) return;

            float remaining = energyWd;
            // 优先本 mod 的无限电池
            if (net.batteryComps != null)
            {
                foreach (CompPowerBattery bat in net.batteryComps)
                {
                    if (remaining <= 0f) break;
                    if (bat is CompOmniCrafterSmartInfiniteBattery)
                    {
                        bat.AddEnergy(remaining);
                        remaining = 0f;
                    }
                }
            }
            // 然后普通电池
            if (remaining > 0f && net.batteryComps != null)
            {
                foreach (CompPowerBattery bat in net.batteryComps)
                {
                    if (remaining <= 0f) break;
                    if (bat is CompOmniCrafterSmartInfiniteBattery) continue;
                    float canAccept = bat.AmountCanAccept;
                    float toAdd = Mathf.Min(canAccept, remaining);
                    if (toAdd > 0f)
                    {
                        bat.AddEnergy(toAdd);
                        remaining -= toAdd;
                    }
                }
            }
        }

        // ── 方式 B：批量转化存储区中的所有物品 ──────────────────────────────────
        private void BatchConvertStoredItems()
        {
            if (!Spawned) return;
            List<Thing> toConvert = new List<Thing>();
            foreach (IntVec3 cell in AllSlotCells())
            {
                List<Thing> things = Map.thingGrid.ThingsListAt(cell);
                // 复制列表防止迭代中修改
                foreach (Thing t in things.ToList())
                {
                    if (t == this) continue;
                    if (t is Pawn) continue;
                    if (t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Building)
                        toConvert.Add(t);
                }
            }

            if (toConvert.Count == 0)
            {
                Messages.Message("MEC_NothingToConvert".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            float totalEnergy = 0f;
            foreach (Thing t in toConvert)
                totalEnergy += RecycleThing(t);

            Messages.Message(
                "MEC_BatchConverted".Translate(toConvert.Count, totalEnergy.ToString("F1")),
                MessageTypeDefOf.PositiveEvent, false);
        }

        // ── 方式 A：载入模式 — 将 CompTransporter 容器中已载入的物品全部转化 ──────
        private void ConvertLoadedItems()
        {
            if (transporterComp == null) return;
            ThingOwner container = transporterComp.innerContainer;
            if (container == null || container.Count == 0)
            {
                Messages.Message("MEC_NothingToConvert".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            List<Thing> items = container.ToList();
            float totalEnergy = 0f;
            foreach (Thing t in items)
            {
                if (t is Pawn) continue;
                float energy = MecEnergyCalc.CalcEnergy(t);
                totalEnergy += energy;
                // 特效（容器中的物品未 spawned，特效放在建筑位置）
                FleckMaker.ThrowLightningGlow(DrawPos, Map, 1.5f);
                t.Destroy(DestroyMode.Vanish);
            }

            if (totalEnergy > 0f)
                InjectEnergy(totalEnergy);

            if (Spawned)
                ConvertSound?.PlayOneShot(new TargetInfo(Position, Map));

            Messages.Message(
                "MEC_BatchConverted".Translate(items.Count, totalEnergy.ToString("F1")),
                MessageTypeDefOf.PositiveEvent, false);
        }

        // ── 方式 C：光标瞄准模式 ───────────────────────────────────────────────
        private void BeginDirectTargeting()
        {
            TargetingParameters parms = new TargetingParameters
            {
                canTargetPawns = false,      // 安全黑名单：不允许选中 Pawn
                canTargetItems = true,
                canTargetBuildings = true,
                canTargetAnimals = false,
                mapObjectTargetsMustBeAutoAttackable = false,
                validator = targ =>
                {
                    if (!targ.HasThing) return false;
                    Thing t = targ.Thing;
                    if (t == this) return false;
                    if (t is Pawn) return false;
                    // 禁止拆除不可摧毁建筑（PlayerCannotDestroy = true）
                    if (t.def.category == ThingCategory.Building && !t.def.destroyable) return false;
                    return t.def.category == ThingCategory.Item
                           || t.def.category == ThingCategory.Building;
                }
            };

            Find.Targeter.BeginTargeting(parms, target =>
            {
                if (target.HasThing)
                {
                    float e = RecycleThing(target.Thing);
                    if (e > 0f)
                        Messages.Message(
                            "MEC_DirectConverted".Translate(target.Thing.LabelShort, e.ToString("F1")),
                            MessageTypeDefOf.PositiveEvent, false);
                }
            });
        }

        // ── Gizmos ─────────────────────────────────────────────────────────────
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            // 方式 B：批量转化存储区
            yield return new Command_Action
            {
                defaultLabel = "MEC_BatchConvert".Translate(),
                defaultDesc = "MEC_BatchConvert_Desc".Translate(),
                icon = TexButton.ReorderUp,
                action = BatchConvertStoredItems
            };

            // 方式 A：转化已载入物品（仅当有 CompTransporter 时显示）
            if (transporterComp != null)
            {
                yield return new Command_Action
                {
                    defaultLabel = "MEC_ConvertLoaded".Translate(),
                    defaultDesc = "MEC_ConvertLoaded_Desc".Translate(),
                    icon = TexButton.Drop,
                    action = ConvertLoadedItems
                };
            }

            // 方式 C：光标直接点选
            yield return new Command_Action
            {
                defaultLabel = "MEC_DirectConvert".Translate(),
                defaultDesc = "MEC_DirectConvert_Desc".Translate(),
                icon = TexButton.OpenStatsReport,
                action = BeginDirectTargeting
            };
        }

        // ── 信息栏显示 ──────────────────────────────────────────────────────────
        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            // 统计存储区物品总潜在能量
            float potentialEnergy = 0f;
            int itemCount = 0;
            if (Spawned)
            {
                foreach (IntVec3 cell in AllSlotCells())
                {
                    foreach (Thing t in Map.thingGrid.ThingsListAt(cell))
                    {
                        if (t == this || t is Pawn) continue;
                        if (t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Building)
                        {
                            potentialEnergy += MecEnergyCalc.CalcEnergy(t);
                            itemCount++;
                        }
                    }
                }
            }

            if (itemCount > 0)
            {
                if (!s.NullOrEmpty()) s += "\n";
                s += "MEC_PotentialEnergy".Translate(itemCount, potentialEnergy.ToString("F1"));
            }

            return s;
        }
    }
}