using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
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
            0.5f, // Awful
            0.8f, // Poor
            1.0f, // Normal
            1.2f, // Good
            1.5f, // Excellent
            2.0f, // Masterwork
            3.0f // Legendary
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

    // ─── 专属电池组件 ──────────────────────────────────────────────────────────
    /// <summary>
    /// 物质-能量转化仪专属电池：
    ///   - 向电网正常放电（输出侧行为与标准电池一致）
    ///   - AmountCanAccept 恒为 0，完全阻断电网充电
    ///   - 容量自动扩张，永不因上限钳制损失电量
    ///   - 只能通过 AddEnergyDirect() 充入，由物质转化逻辑调用
    /// </summary>
    public class CompMatterEnergyConverterBattery : CompPowerBattery
    {
        private const float BaseCapacity = 1000f;

        // ── 初始化：克隆 props 防止污染全局 XML 配置 ─────────────────────────────
        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            
            // 安全获取初始最大能量
            float initialMax = BaseCapacity;
            if (this.props is CompProperties_Battery bProps)
                initialMax = bProps.storedEnergyMax;

            CloneProps(initialMax);

            // 重载后将容量与储存电量同步（高于 BaseCapacity 时容量 == 储量，低于则保持地板）
            float realStored = Traverse.Create(this).Field("storedEnergy").GetValue<float>();
            if (this.props is CompProperties_Battery currentProps)
                currentProps.storedEnergyMax = Mathf.Max(BaseCapacity, realStored);
        }

        public override void PostExposeData()
        {
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                // 加载前先撑开容量，防止 base 末尾将 storedEnergy 钳制到 XML 原始值
                CloneProps(float.MaxValue / 8f);
            }

            base.PostExposeData();
        }

        private void CloneProps(float maxEnergy)
        {
            if (!(this.props is CompProperties_Power orig)) return;

            this.props = new CompProperties_Battery
            {
                compClass = orig.compClass,
                storedEnergyMax = maxEnergy,
                efficiency = 1.0f,
                shortCircuitInRain = false,
                transmitsPower = orig.transmitsPower
            };
        }

        // ── Tick：棘轮维护（容量始终 ≥ 已存储量） ───────────────────────────────
        public override void CompTick()
        {
            base.CompTick();
            EnsureCapacity();
        }

        public override void CompTickRare()
        {
            base.CompTickRare();
            EnsureCapacity();
        }

        private void EnsureCapacity()
        {
            float realStored = Traverse.Create(this).Field("storedEnergy").GetValue<float>();
            // 容量始终与已存储电量同步：高于 BaseCapacity 时跟随缩减，低于则保持 BaseCapacity 地板
            ((CompProperties_Battery)this.props).storedEnergyMax = Mathf.Max(BaseCapacity, realStored);
        }

        // ── 物质转化专用注能接口 ─────────────────────────────────────────────────
        /// <summary>
        /// 绕过 AmountCanAccept 检查，直接将 energy (Wd) 充入本电池。
        /// 注意：不能调用 AddEnergy()，因为其内部会读取 AmountCanAccept，
        /// 而我们的 Harmony Patch 已将其强制为 0，会导致能量被钳制归零。
        /// 因此直接通过 Traverse 写 storedEnergy 字段。
        /// </summary>
        public void AddEnergyDirect(float energy)
        {
            if (energy <= 0f) return;
            var storedField = Traverse.Create(this).Field("storedEnergy");
            float realStored = storedField.GetValue<float>();
            float newStored = realStored + energy;
            // 同步扩展容量，使容量 == 新储量（始终满电）
            ((CompProperties_Battery)this.props).storedEnergyMax = Mathf.Max(BaseCapacity, newStored);
            // 直接写字段，绕过 AmountCanAccept 钳制
            storedField.SetValue(newStored);
        }

        // // ── 信息栏 ───────────────────────────────────────────────────────────────
        // public override string CompInspectStringExtra()
        // {
        //     float stored = Traverse.Create(this).Field("storedEnergy").GetValue<float>();
        //     string energyStr = stored >= 1_000_000_000f
        //         ? stored.ToString("N0") + " Wd"
        //         : base.CompInspectStringExtra()
        //               .Replace(this.StoredEnergy.ToString("F0"), stored.ToString("F0")); // 修正显示
        //     return energyStr;
        // }
    }

    // ─── Harmony：阻断电网充电 ─────────────────────────────────────────────────
    [HarmonyPatch(typeof(CompPowerBattery), "AmountCanAccept", MethodType.Getter)]
    public static class Patch_MecBattery_AmountCanAccept
    {
        [HarmonyPrefix]
        public static bool Prefix(CompPowerBattery __instance, ref float __result)
        {
            if (__instance is CompMatterEnergyConverterBattery)
            {
                __result = 0f; // 完全阻断电网充电
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(CompPowerBattery), "StoredEnergy", MethodType.Getter)]
    public static class Patch_MecBattery_StoredEnergy
    {
        [HarmonyPrefix]
        public static bool Prefix(CompPowerBattery __instance, ref float __result)
        {
            if (__instance is CompMatterEnergyConverterBattery smartBattery)
            {
                // 如果开关关闭，对外伪装电量为0，阻止能量输出
                if (!FlickUtility.WantsToBeOn(smartBattery.parent))
                {
                    __result = 0f;
                    return false;
                }
            }

            return true;
        }
    }

    // ─── Harmony：短路防护（防核弹级爆炸） ────────────────────────────────────
    // 已将逻辑整合到 OmniCrafterSmartInfiniteBattery.cs 中的 Patch_DoShortCircuit_Unified

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
        private CompMatterEnergyConverterBattery mecBattery;

        // ── 常量 ──────────────────────────────────────────────────────────────
        private static readonly SoundDef ConvertSound = SoundDefOf.EnergyShield_AbsorbDamage;

        // ── 初始化 ────────────────────────────────────────────────────────────
        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            powerComp = GetComp<CompPowerTrader>();
            transporterComp = GetComp<CompTransporter>();
            mecBattery = GetComp<CompMatterEnergyConverterBattery>();
        }

        // ── 核心：将物品转为电能 ───────────────────────────────────────────────
        /// <summary>
        /// 销毁物品，计算能量并注入电池。返回产生的能量（Wd）。
        /// </summary>
        public float RecycleThing(Thing thing)
        {
            if (thing == null || thing.Destroyed) return 0f;
            // 安全黑名单：禁止转化玩家派系的Pawn（殖民者、宠物等）
            if (thing is Pawn p && p.Faction == Faction.OfPlayer) return 0f;

            // 特殊处理：囚禁台——有实体则回收实体，无实体则回收囚禁台本身
            if (thing is Building_HoldingPlatform holdingPlatform)
            {
                Pawn heldPawn = holdingPlatform.HeldPawn;
                // 有实体：回收实体，不触碰建筑
                if (heldPawn != null && !heldPawn.Destroyed)
                {
                    if (heldPawn.Faction == Faction.OfPlayer) return 0f;
                    float heldEnergy = MecEnergyCalc.CalcEnergy(heldPawn);
                    FleckMaker.ThrowLightningGlow(holdingPlatform.DrawPos, Map, 1.5f);
                    FleckMaker.ThrowSmoke(holdingPlatform.DrawPos, Map, 1.0f);
                    KillAndVanishPawn(heldPawn);
                    if (heldEnergy > 0f) InjectEnergy(heldEnergy);
                    if (Spawned) ConvertSound?.PlayOneShot(new TargetInfo(Position, Map));
                    return heldEnergy;
                }
                // 无实体：跳出此分支，由下方通用逻辑回收囚禁台建筑本身
            }

            float energy = MecEnergyCalc.CalcEnergy(thing);

            // 特效
            if (thing.Spawned)
            {
                FleckMaker.ThrowLightningGlow(thing.DrawPos, Map, 1.5f);
                FleckMaker.ThrowSmoke(thing.DrawPos, Map, 1.0f);
            }

            // 销毁：Pawn/尸体走 Kill→销毁尸体 路径以正确触发社交关系清理和悲伤情绪；
            // 普通物品直接 Vanish。
            if (thing is Pawn pawnThing)
            {
                KillAndVanishPawn(pawnThing);
            }
            else if (thing is Corpse corpse)
            {
                // 尸体：先确保内部 Pawn 走过了死亡通知，再销毁尸体本体
                corpse.Destroy(DestroyMode.Vanish);
            }
            else
            {
                thing.Destroy(DestroyMode.Vanish);
            }

            // 注入电能
            if (energy > 0f)
                InjectEnergy(energy);

            // 播放音效
            if (Spawned)
                ConvertSound?.PlayOneShot(new TargetInfo(Position, Map));

            return energy;
        }

        /// <summary>
        /// 正确销毁一个 Pawn：
        ///   1. 若尚未死亡，先 Kill()（触发 Notify_PawnKilled → 悲伤情绪、羁绊动物反应、
        ///      配偶 Thought 清理、removeOnDeath 关系移除）。
        ///   2. 再销毁尸体实体（跳过产出尸体堆）。
        /// 使用 Vanish 直接 Destroy 会跳过 Notify_PawnKilled，导致社交关系不完整处理。
        /// </summary>
        private static void KillAndVanishPawn(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed) return;
            if (!pawn.Dead)
                pawn.Kill(null); // 触发完整的死亡社交通知；会在原位生成尸体
            // Kill() 会生成一个 Corpse Thing，我们直接销毁它
            if (!pawn.Destroyed)
                pawn.Destroy(DestroyMode.Vanish);
            // 若 Kill() 产生了尸体，则销毁尸体
            Corpse corpse = pawn.Corpse;
            if (corpse != null && !corpse.Destroyed)
                corpse.Destroy(DestroyMode.Vanish);
        }

        /// <summary>
        /// 将电能直接充入本建筑的专属电池组件。
        /// 绕过 AmountCanAccept，不依赖电网中的其他电池。
        /// </summary>
        private void InjectEnergy(float energyWd)
        {
            if (mecBattery != null)
            {
                mecBattery.AddEnergyDirect(energyWd);
                return;
            }

            // 回退：若 XML 未配置本 mod 电池，则尝试注入电网中的无限电池或普通电池
            PowerNet net = powerComp?.PowerNet;
            if (net == null) return;

            float remaining = energyWd;
            // 优先本电池
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
                    float toAdd = Mathf.Min(bat.AmountCanAccept, remaining);
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
                foreach (Thing t in Map.thingGrid.ThingsListAt(cell).ToList())
                {
                    if (t == this || t is Pawn) continue;
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
                "MEC_BatchConverted".Translate(toConvert.Count, totalEnergy.ToString("N1")),
                MessageTypeDefOf.PositiveEvent, false);
        }

        // ── 方式 A：将 CompTransporter 容器中已载入的物品全部转化 ──────────────
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
                "MEC_BatchConverted".Translate(items.Count, totalEnergy.ToString("N1")),
                MessageTypeDefOf.PositiveEvent, false);
        }

        // ── 方式 C：光标瞄准模式 ───────────────────────────────────────────────
        private void BeginDirectTargeting()
        {
            TargetingParameters parms = new TargetingParameters
            {
                canTargetPawns = true,
                canTargetItems = true,
                canTargetBuildings = true,
                canTargetAnimals = true,
                canTargetPlants = true,
                canTargetMechs = true,
                canTargetSubhumans = true,
                canTargetEntities = true,
                mapObjectTargetsMustBeAutoAttackable = false,
                validator = targ =>
                {
                    if (!targ.HasThing) return false;
                    Thing t = targ.Thing;
                    if (t == this) return false;
                    // 玩家派系的Pawn（殖民者、宠物等）不允许选中
                    if (t is Pawn pawn && pawn.Faction == Faction.OfPlayer)
                        return false;
                    // 囚禁台：持有非玩家派系实体时允许（回收实体）；空台也允许（回收建筑）
                    // 但持有玩家派系实体的囚禁台不允许选中
                    if (t is Building_HoldingPlatform hp)
                        return hp.HeldPawn == null || hp.HeldPawn.Faction != Faction.OfPlayer;
                    return t.def.category == ThingCategory.Item
                           || t.def.category == ThingCategory.Building
                           || t.def.category == ThingCategory.Plant
                           || t.def.category == ThingCategory.Pawn;
                }
            };

            Find.Targeter.BeginTargeting(parms, target =>
            {
                if (target.HasThing)
                {
                    Thing t = target.Thing;
                    string label = t.LabelShort;
                    float e = RecycleThing(t);
                    if (e > 0f)
                        Messages.Message(
                            "MEC_DirectConverted".Translate(label, e.ToString("N1")),
                            MessageTypeDefOf.PositiveEvent, false);
                }
                // 转化完毕后自动重新进入瞄准模式，实现连续点选
                BeginDirectTargeting();
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
                icon = MatterEnergyConverterTex.IconStorage,
                action = BatchConvertStoredItems
            };

            // 方式 A：转化已载入物品（仅当有 CompTransporter 时显示）
            if (transporterComp != null)
            {
                yield return new Command_Action
                {
                    defaultLabel = "MEC_ConvertLoaded".Translate(),
                    defaultDesc = "MEC_ConvertLoaded_Desc".Translate(),
                    icon = MatterEnergyConverterTex.IconDrop,
                    action = ConvertLoadedItems
                };
            }

            // 方式 C：光标直接点选
            yield return new Command_Action
            {
                defaultLabel = "MEC_DirectConvert".Translate(),
                defaultDesc = "MEC_DirectConvert_Desc".Translate(),
                icon = MatterEnergyConverterTex.IconPicker,
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
                s += "MEC_PotentialEnergy".Translate(itemCount, potentialEnergy.ToString("N1"));
            }

            return s;
        }
    }
    
    [StaticConstructorOnStartup]
    public static class MatterEnergyConverterTex
    {
        public static readonly Texture2D IconDrop =
            ContentFinder<Texture2D>.Get("UI/Commands/MatterEnergyConverter_Loader", true) ?? BaseContent.WhiteTex;
        public static readonly Texture2D IconPicker =
            ContentFinder<Texture2D>.Get("UI/Commands/MatterEnergyConverter_Picker", true) ?? BaseContent.WhiteTex;
        public static readonly Texture2D IconStorage =
            ContentFinder<Texture2D>.Get("UI/Commands/MatterEnergyConverter_Storage", true) ?? BaseContent.WhiteTex;
    }
    
}