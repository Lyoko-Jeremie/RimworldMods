using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.Sound;
using UnityEngine;

namespace FullyAutomaticOmniCrafter
{
    // ─── Enums & Data ─────────────────────────────────────────────────────────
    public enum OutputMode
    {
        DropNear,
        SendToStorage
    }

    public enum ProductionMode
    {
        FixedCount,
        MaintainStock
    }

    public class AutoOrder : IExposable
    {
        public ThingDef thingDef;
        public ThingDef stuffDef;
        public QualityCategory quality = QualityCategory.Normal;
        public int targetCount = 10;
        public OutputMode outputMode = OutputMode.DropNear;
        public bool storageOnly = false; // 仅统计存储区中的物品
        public bool paused = false;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref thingDef, "thingDef");
            Scribe_Defs.Look(ref stuffDef, "stuffDef");
            Scribe_Values.Look(ref quality, "quality", QualityCategory.Normal);
            Scribe_Values.Look(ref targetCount, "targetCount", 10);
            Scribe_Values.Look(ref outputMode, "outputMode", OutputMode.DropNear);
            Scribe_Values.Look(ref storageOnly, "storageOnly", false);
            Scribe_Values.Look(ref paused, "paused", false);
        }
    }

    // ─── Power Cost ───────────────────────────────────────────────────────────
    public static class OmniPowerCost
    {
        private static readonly float[] QualityMult = { 0.5f, 0.8f, 1.0f, 2.0f, 4.0f, 8.0f, 16.0f };

        public static float CostWd(ThingDef def, ThingDef stuff, QualityCategory quality, int count)
        {
            if (def == null) return 0f;
            float baseValue = def.GetStatValueAbstract(StatDefOf.MarketValue, stuff);
            if (baseValue < 1f) baseValue = 1f;
            return baseValue * QualityMult[(int)quality] * count;
        }

        public static float InternalStoredEnergy(PowerNet net)
        {
            if (net == null) return 0f;
            float energy = 0f;
            if (net.batteryComps != null)
            {
                foreach (CompPowerBattery bat in net.batteryComps)
                {
                    if (bat is CompOmniCrafterSmartInfiniteBattery smartBattery)
                    {
                        energy += Traverse.Create(smartBattery).Field("storedEnergy").GetValue<float>();
                    }
                }
            }

            return energy;
        }

        public static float ExternalStoredEnergy(PowerNet net)
        {
            if (net == null) return 0f;
            if (float.IsInfinity(net.CurrentEnergyGainRate()) || float.IsNaN(net.CurrentEnergyGainRate()) ||
                net.CurrentEnergyGainRate() >= 1000000f)
            {
                return float.PositiveInfinity;
            }

            float internalActive = 0f;
            if (net.batteryComps != null)
            {
                foreach (CompPowerBattery bat in net.batteryComps)
                {
                    if (bat is CompOmniCrafterSmartInfiniteBattery smartBattery &&
                        FlickUtility.WantsToBeOn(smartBattery.parent))
                    {
                        internalActive += Traverse.Create(smartBattery).Field("storedEnergy").GetValue<float>();
                    }
                }
            }

            return Mathf.Max(0f, net.CurrentStoredEnergy() - internalActive);
        }

        public static float TotalStoredEnergy(PowerNet net)
        {
            if (net == null) return 0f;
            if (float.IsInfinity(net.CurrentEnergyGainRate()) || float.IsNaN(net.CurrentEnergyGainRate()) ||
                net.CurrentEnergyGainRate() >= 1000000f)
            {
                return float.PositiveInfinity;
            }

            return ExternalStoredEnergy(net) + InternalStoredEnergy(net);
        }

        public static float SurplusPowerW(PowerNet net)
        {
            if (net == null) return 0f;
            return net.CurrentEnergyGainRate() * 60000f; // Wd/tick 转换为 瓦特(W)
        }

        public static bool TryDrainPower(PowerNet net, float amountWd)
        {
            if (net == null) return false;

            if (float.IsInfinity(net.CurrentEnergyGainRate()) || float.IsNaN(net.CurrentEnergyGainRate()) ||
                net.CurrentEnergyGainRate() >= 1000000f)
            {
                return true;
            }

            if (SurplusPowerW(net) >= amountWd)
            {
                return true; // 实时盈余功率大于所需，直接返回 true，不扣除电量
            }

            if (TotalStoredEnergy(net) < amountWd) return false;

            float remaining = amountWd;

            // First deduct from Smart Infinite Batteries
            foreach (CompPowerBattery bat in net.batteryComps)
            {
                if (remaining <= 0f) break;
                if (bat is CompOmniCrafterSmartInfiniteBattery smartBattery)
                {
                    float realStored = Traverse.Create(smartBattery).Field("storedEnergy").GetValue<float>();
                    float draw = Mathf.Min(realStored, remaining);
                    if (draw > 0f)
                    {
                        bat.DrawPower(draw);
                        remaining -= draw;
                    }
                }
            }

            // Then from normal batteries
            if (remaining > 0f)
            {
                foreach (CompPowerBattery bat in net.batteryComps)
                {
                    if (remaining <= 0f) break;
                    if (!(bat is CompOmniCrafterSmartInfiniteBattery))
                    {
                        float draw = Mathf.Min(bat.StoredEnergy, remaining);
                        bat.DrawPower(draw);
                        remaining -= draw;
                    }
                }
            }

            return true;
        }
    }

    // ─── Building ─────────────────────────────────────────────────────────────
    public class Building_OmniCrafter : Building
    {
        // Legacy per-building favorites kept only for one-time migration to global settings.
        private List<string> _legacyFavorites = new List<string>();
        public List<string> recentCrafted = new List<string>();
        public List<AutoOrder> autoOrders = new List<AutoOrder>();

        private CompPower powerComp;
        private CompFlickable flickComp;

        private int rareTickCounter = 0;
        private bool _pendingSettingsWrite = false;

        /// <summary>[DEBUG] 仅在 God 模式下生效：跳过所有电力检查与消耗，直接生产。</summary>
        public static bool debugNoPowerRequired = false;

        // TickRare = every 250 ticks; we want ~every 1000 ticks (4 rare ticks)
        private const int RareTicksPerCheck = 3;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            powerComp = GetComp<CompPower>();
            flickComp = GetComp<CompFlickable>();
            if (_pendingSettingsWrite)
            {
                _pendingSettingsWrite = false;
                OmniCrafterMod.Settings.Write();
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            // Load legacy per-building favorites for one-time migration
            Scribe_Collections.Look(ref _legacyFavorites, "favorites", LookMode.Value);
            Scribe_Collections.Look(ref recentCrafted, "recentCrafted", LookMode.Value);
            Scribe_Collections.Look(ref autoOrders, "autoOrders", LookMode.Deep);
            if (_legacyFavorites == null) _legacyFavorites = new List<string>();
            if (recentCrafted == null) recentCrafted = new List<string>();
            if (autoOrders == null) autoOrders = new List<AutoOrder>();

            // One-time migration: move old per-building favorites into global settings
            if (Scribe.mode == LoadSaveMode.PostLoadInit && _legacyFavorites.Count > 0)
            {
                var global = OmniCrafterMod.Settings.globalFavorites;
                foreach (string fav in _legacyFavorites)
                    if (!global.Contains(fav))
                        global.Add(fav);
                _legacyFavorites.Clear();
                _pendingSettingsWrite = true; // defer Write() until after PostLoadInit
            }
        }

        public override void TickRare()
        {
            base.TickRare();
            rareTickCounter++;
            if (rareTickCounter >= RareTicksPerCheck)
            {
                rareTickCounter = 0;
                ProcessAutoOrders();
            }
            // ProcessAutoOrders();
        }

        private void ProcessAutoOrders()
        {
            // Log.Message($"[OmniCrafter] Processing {autoOrders.Count} auto orders...");
            bool godDebug = debugNoPowerRequired && DebugSettings.godMode;
            if (!godDebug)
            {
                bool isOn = (flickComp == null || flickComp.SwitchIsOn) && powerComp != null;
                if (!isOn) return;
            }

            PowerNet net = godDebug ? null : powerComp?.PowerNet;
            if (!godDebug && net == null) return;
            foreach (AutoOrder order in autoOrders)
            {
                if (order.paused) continue;
                // Log.Message($"[OmniCrafter] Processing auto order: {order?.thingDef?.defName} x {order?.targetCount}");
                try
                {
                    if (order.thingDef == null) continue;
                    int current = order.storageOnly
                        ? OmniCrafterCache.CountInStorage(order.thingDef, Map)
                        : OmniCrafterCache.CountOnMap(order.thingDef, Map);
                    // Log.Message($"[OmniCrafter] Current count of {order.thingDef.defName} on map: {current}");
                    if (current >= order.targetCount) continue;
                    int needed = order.targetCount - current;

                    // Log.Message($"[OmniCrafter] Current count: {current}, Needed: {needed}");

                    // 计算单件电力消耗，按当前可用电量推算最多能制造的数量
                    // 避免「一次性要求全部电力，不足则跳过」导致自动订单永远无法执行
                    float unitCost = OmniPowerCost.CostWd(order.thingDef, order.stuffDef, order.quality, 1);
                    float available = OmniPowerCost.TotalStoredEnergy(net);

                    float surplusW = OmniPowerCost.SurplusPowerW(net);

                    int toCraft;
                    if (godDebug || unitCost <= 0f || float.IsInfinity(available) || float.IsNaN(available))
                    {
                        toCraft = needed;
                    }
                    else if (surplusW >= unitCost)
                    {
                        // 如果有盈余能量足够单件制造
                        int canAfford = Mathf.Max(Mathf.FloorToInt(surplusW / unitCost),
                            Mathf.FloorToInt(available / unitCost));
                        toCraft = Mathf.Min(needed, canAfford);
                    }
                    else
                    {
                        int canAfford = Mathf.FloorToInt(available / unitCost);
                        toCraft = Mathf.Min(needed, canAfford);
                    }

                    // Log.Message($"[OmniCrafter] Unit cost: {unitCost}, Available: {available}, Craft: {toCraft}");

                    if (toCraft <= 0) continue;

                    float totalCost = unitCost * toCraft;
                    if (!godDebug && !OmniPowerCost.TryDrainPower(net, totalCost)) continue;
                    // Log.Message(
                    //     $"[OmniCrafter] Attempting to craft {toCraft} {order.thingDef?.defName} with total cost {totalCost}");
                    SpawnItems(order.thingDef, order.stuffDef, order.quality, toCraft, order.outputMode);
                }
                catch (Exception ex)
                {
                    Log.Error(
                        $"[OmniCrafter] ProcessAutoOrders failed for '{order?.thingDef?.defName}': {ex.Message}");
                    Log.Error(ex.StackTrace);
                }
            }
        }

        public void AddRecent(ThingDef def)
        {
            recentCrafted.Remove(def.defName);
            recentCrafted.Insert(0, def.defName);
            if (recentCrafted.Count > 10) recentCrafted.RemoveAt(recentCrafted.Count - 1);
        }

        public void SpawnItems(ThingDef def, ThingDef stuff, QualityCategory quality, int count, OutputMode mode)
        {
            int remaining = count;
            while (remaining > 0)
            {
                // 建筑打包为 MinifiedThing，每次只能生成 1 个
                int stackMax = (def.category == ThingCategory.Building)
                    ? 1
                    : (def.stackLimit > 0 ? def.stackLimit : 1);
                int stackSize = Mathf.Min(remaining, stackMax);
                Thing thing = MakeThing(def, stuff, quality, stackSize);
                if (thing == null)
                {
                    remaining--;
                    continue;
                }

                // 第一步：先将物品安全地生成在建筑附近，使其拥有合法的 Map 和 Position，
                // 避免存储 Mod（如 ASF）在计算距离时因 Position 无效而抛出 NullReferenceException。
                if (GenPlace.TryPlaceThing(thing, Position, Map, ThingPlaceMode.Near, out Thing placedThing))
                {
                    if (mode == OutputMode.SendToStorage)
                    {
                        // 第二步：物品已在地图上，尝试寻找最优存储格
                        IntVec3 storeCell;
                        if (StoreUtility.TryFindBestBetterStoreCellFor(
                                placedThing, null, Map, StoragePriority.Unstored, Faction.OfPlayer, out storeCell))
                        {
                            // 第三步：找到目标仓库，先将其从地面"捡起"（脱离物理地面）
                            placedThing.DeSpawn();

                            // 检查目标格子上是否已有同类物品，有则尝试合堆
                            Thing existingStack = storeCell.GetFirstThing(Map, placedThing.def);
                            if (existingStack != null)
                            {
                                existingStack.TryAbsorbStack(placedThing, true);
                            }
                            else
                            {
                                // 格子为空，直接生成到目标格
                                GenSpawn.Spawn(placedThing, storeCell, Map);
                            }

                            // 第四步：处理合堆后未被吸收的剩余物品，扔回建筑旁边
                            if (!placedThing.Destroyed && placedThing.stackCount > 0 && !placedThing.Spawned)
                            {
                                GenPlace.TryPlaceThing(placedThing, Position, Map, ThingPlaceMode.Near);
                            }
                        }
                        // 若全图无合适存储格，物品已通过第一步落在建筑附近，逻辑自然闭环
                    }
                }

                remaining -= stackSize;
            }
        }

        private Thing MakeThing(ThingDef def, ThingDef stuff, QualityCategory quality, int count)
        {
            try
            {
                if (def.category == ThingCategory.Building)
                {
                    if (!def.Minifiable) return null;
                    ThingDef stuffToUse = def.MadeFromStuff ? (stuff ?? GenStuff.DefaultStuffFor(def)) : null;
                    Thing inner = ThingMaker.MakeThing(def, stuffToUse);
                    SetQuality(inner, quality);
                    SetArt(inner, quality);
                    MinifiedThing minified = (MinifiedThing)ThingMaker.MakeThing(def.minifiedDef);
                    minified.InnerThing = inner;
                    return minified;
                }
                else
                {
                    ThingDef stuffToUse = def.MadeFromStuff ? (stuff ?? GenStuff.DefaultStuffFor(def)) : null;
                    Thing thing = ThingMaker.MakeThing(def, stuffToUse);
                    thing.stackCount = Mathf.Clamp(count, 1, def.stackLimit > 0 ? def.stackLimit : 1);
                    SetQuality(thing, quality);
                    SetArt(thing, quality);
                    return thing;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[OmniCrafter] Failed to make {def?.defName}: {ex.Message}");
                return null;
            }
        }

        private static void SetQuality(Thing thing, QualityCategory quality)
        {
            thing.TryGetComp<CompQuality>()?.SetQuality(quality, ArtGenerationContext.Colony);
        }

        private static void SetArt(Thing thing, QualityCategory quality)
        {
            if (quality >= QualityCategory.Excellent)
            {
                CompArt art = thing.TryGetComp<CompArt>();
                if (art != null && !art.Active) art.InitializeArt(ArtGenerationContext.Colony);
            }
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos()) yield return g;
            yield return new Command_Action
            {
                defaultLabel = "OmniCrafter_OpenUI".Translate(),
                defaultDesc = "OmniCrafter_OpenUIDesc".Translate(),
                icon = FullyAutomaticOmniCrafterTex.IconLaunchReport,
                action = () => Find.WindowStack.Add(new Dialog_OmniCrafter(this))
            };
        }
    }

    [StaticConstructorOnStartup]
    public static class FullyAutomaticOmniCrafterTex
    {
        public static readonly Texture2D IconLaunchReport =
            ContentFinder<Texture2D>.Get("UI/Commands/OmniCrafter_LaunchReport", true) ?? BaseContent.WhiteTex;
    }

}