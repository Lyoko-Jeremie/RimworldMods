using System;
using System.Collections.Generic;
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
    // TODO 在以下几种情况下可以自动制造和手动制造：
    //      1 本地电量足够（扣除本地电量）
    //      2 本地电量+外部电网电量足够（优先扣除本地电量，不足的部分从外部电网扣除）
    //      3 外部电网功率大于所需电量（直接可以制造，不扣除任何电量）
    //      4 外部电网电量无限（直接可以制造，不扣除任何电量）
    //      5 处于 God 模式（直接可以制造，不扣除任何电量）

    public static class OmniPowerCost
    {
        private static readonly float[] QualityMult = { 0.5f, 0.8f, 1.0f, 2.0f, 4.0f, 8.0f, 16.0f };

        // RimWorld's CurrentEnergyGainRate only counts PowerOn traders.
        // On no-battery nets, some comps may be temporarily PowerOff while still wanting power,
        // so we provide a conservative fallback based on desired PowerOutput.
        private static float EffectiveEnergyGainRate(PowerNet net)
        {
            if (net == null) return 0f;

            float gain = net.CurrentEnergyGainRate();
            if (gain > 1e-6f || net.batteryComps == null || net.batteryComps.Count > 0)
                return gain;

            if (net.powerComps == null || net.powerComps.Count == 0)
                return gain;

            float fallback = 0f;
            foreach (CompPowerTrader comp in net.powerComps)
            {
                if (comp == null || !FlickUtility.WantsToBeOn(comp.parent) || comp.parent.IsBrokenDown())
                    continue;
                fallback += comp.PowerOutput * CompPower.WattsToWattDaysPerTick;
            }

            return Mathf.Max(gain, fallback);
        }

        public static float CostWd(ThingDef def, ThingDef stuff, QualityCategory quality, int count)
        {
            if (def == null) return 0f;

            OmniCrafterSettings s = OmniCrafterMod.Settings;

            // Compose X from market value, optionally adding mass and/or max hit points
            float x = def.GetStatValueAbstract(StatDefOf.MarketValue, stuff);
            if (x < 1f) x = 1f;

            if (s?.xIncludeMass == true)
            {
                float mass = def.GetStatValueAbstract(StatDefOf.Mass, stuff);
                if (mass > 0f) x += mass;
            }

            if (s?.xIncludeHitPoints == true)
            {
                float hp = def.BaseMaxHitPoints;
                if (hp > 0f) x += hp;
            }

            // Y = a + b*X + c*X^2 + d*X^3 + e*X^4 + g*log10(X) + n*ln(X)
            float a = s?.powerCostA ?? 0f;
            float b = s?.powerCostB ?? 1f;
            float c = s?.powerCostC ?? 0f;
            float d = s?.powerCostD ?? 0f;
            float e = s?.powerCostE ?? 0f;
            float g = s?.powerCostG ?? 0f;
            float n = s?.powerCostN ?? 0f;
            float x2 = x * x;
            float x3 = x2 * x;
            float x4 = x3 * x;
            float log10x = (float)Math.Log10(x);
            float lnx    = (float)Math.Log(x);
            float y = a + b * x + c * x2 + d * x3 + e * x4 + g * log10x + n * lnx;
            if (y < 0f) y = 0f;

            return y * QualityMult[(int)quality] * count;
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

        /// <summary>
        /// 返回所有开启状态的 MEC 专属电池（CompMatterEnergyConverterBattery）的储能总量（Wd）。
        /// 注意：直接读取底层字段，与 bat.StoredEnergy 通过 Harmony Patch 遮蔽关闭状态一致。
        /// </summary>
        public static float MecStoredEnergy(PowerNet net)
        {
            if (net == null) return 0f;
            float energy = 0f;
            if (net.batteryComps != null)
            {
                foreach (CompPowerBattery bat in net.batteryComps)
                {
                    if (bat is CompMatterEnergyConverterBattery mecBat)
                    {
                        // bat.StoredEnergy 已通过 Patch_MecBattery_StoredEnergy 处理关闭遮蔽，直接使用即可
                        energy += bat.StoredEnergy;
                    }
                }
            }
            return energy;
        }

        public static float ExternalStoredEnergy(PowerNet net)
        {
            if (net == null) return 0f;
            float gain = EffectiveEnergyGainRate(net);
            if (float.IsInfinity(gain) || float.IsNaN(gain) || gain >= 1000000f)
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
            float gain = EffectiveEnergyGainRate(net);
            if (float.IsInfinity(gain) || float.IsNaN(gain) || gain >= 1000000f)
            {
                return float.PositiveInfinity;
            }

            // Fix: In RimWorld, a net with 0 batteries will return 0 from CurrentStoredEnergy().
            // But if EffectiveEnergyGainRate returns a positive gain, it means there's live power.
            // For OmniCrafter logic, we treat it as having enough energy if the power net is live and strong enough.
            // Here we just return the sum of batteries. The power-based bypass is handled in TryDrainPower.
            return ExternalStoredEnergy(net) + InternalStoredEnergy(net);
        }

        public static float SurplusEnergyWdPerTick(PowerNet net)
        {
            if (net == null) return 0f;
            float gain = EffectiveEnergyGainRate(net);
            if (float.IsInfinity(gain) || float.IsNaN(gain) || gain >= 1000000f)
            {
                return float.PositiveInfinity;
            }

            return Mathf.Max(0f, gain);
        }

        public static bool TryDrainPower(PowerNet net, float amountWd)
        {
            // 5 处于 God 模式且打开debug开关（直接可以制造，不扣除任何电量）
            if (Building_OmniCrafter.debugNoPowerRequired && DebugSettings.godMode)
            {
                return true;
            }

            if (net == null) return false;

            // 4 外部电网电量无限（直接可以制造，不扣除任何电量）
            float surplusWdPerTick = SurplusEnergyWdPerTick(net);
            if (float.IsInfinity(surplusWdPerTick))
            {
                return true;
            }

            // 3 外部电网功率充足（直接可以制造，不扣除任何电量）
            // 设计要求：供电功率W的数字，大于所需电量Wd的数字时，即可生产
            // surplusWdPerTick * 60000f 得到的是功率 W
            if (surplusWdPerTick * 60000f >= amountWd)
            {
                return true;
            }

            // 1 本地电量足够（扣除本地电量）
            // 2 本地电量+外部电网电量足够（优先扣除本地电量，不足的部分从外部电网扣除）
            float totalStored = TotalStoredEnergy(net);
            if (float.IsInfinity(totalStored) || totalStored >= amountWd)
            {
                // 扣除逻辑
                float remaining = amountWd;

                // 第一步：优先从 Smart Infinite Batteries（内部电池）扣除
                if (net.batteryComps != null)
                {
                    foreach (CompPowerBattery bat in net.batteryComps)
                    {
                        if (remaining <= 0f) break;
                        if (bat is CompOmniCrafterSmartInfiniteBattery smartBattery)
                        {
                            float realStored = Traverse.Create(smartBattery).Field("storedEnergy").GetValue<float>();
                            float draw = Mathf.Min(realStored, remaining);
                            if (draw > 1e-6f)
                            {
                                bat.DrawPower(draw);
                                remaining -= draw;
                            }
                        }
                    }
                }

                // 第二步：从 MEC 专属电池（CompMatterEnergyConverterBattery）扣除
                // bat.StoredEnergy 已通过 Patch_MecBattery_StoredEnergy 处理：关闭时为 0，开启时为真实储量
                if (remaining > 1e-6f && net.batteryComps != null)
                {
                    foreach (CompPowerBattery bat in net.batteryComps)
                    {
                        if (remaining <= 0f) break;
                        if (bat is CompMatterEnergyConverterBattery)
                        {
                            float draw = Mathf.Min(bat.StoredEnergy, remaining);
                            if (draw > 1e-6f)
                            {
                                bat.DrawPower(draw);
                                remaining -= draw;
                            }
                        }
                    }
                }

                // 第三步：最后从普通电池扣除
                if (remaining > 1e-6f && net.batteryComps != null)
                {
                    foreach (CompPowerBattery bat in net.batteryComps)
                    {
                        if (remaining <= 0f) break;
                        if (!(bat is CompOmniCrafterSmartInfiniteBattery) &&
                            !(bat is CompMatterEnergyConverterBattery))
                        {
                            float draw = Mathf.Min(bat.StoredEnergy, remaining);
                            if (draw > 1e-6f)
                            {
                                bat.DrawPower(draw);
                                remaining -= draw;
                            }
                        }
                    }
                }

                return true;
            }

            return false;
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
        private CompPowerTrader powerTraderComp;
        private CompFlickable flickComp;

        private bool _pendingSettingsWrite = false;

        /// <summary>[DEBUG] 仅在 God 模式下生效：跳过所有电力检查与消耗，直接生产。</summary>
        public static bool debugNoPowerRequired = false;

        // 分帧处理：通过 _processOrderIndex 记录上次处理到的位置，下次从该位置继续滚动，
        // 避免大量订单集中在单帧执行。
        // TargetCycleRareTicks 控制"完整轮询所有订单一次"所期望消耗的 TickRare 次数。
        // 每次 TickRare 的批量大小 = ceil(订单数 / TargetCycleRareTicks)，随订单数自动伸缩：
        //   - 订单少时每次只处理 1 条，减少不必要开销；
        //   - 订单多时自动增大批量，保证仍能在目标时间内完成一轮。
        // TargetCycleRareTicks = 10  →  完整一轮约 10 × 250 = 2500 ticks ≈ 41 秒（正常游戏速度）
        private int _processOrderIndex = 0;
        private const int TargetCycleRareTicks = 10;
        // 单次最小处理数量：即使订单数很少，每次 TickRare 也至少处理这么多条，
        // 避免批量过小导致轮询响应太慢。
        private const int MinBatchSize = 5;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            powerTraderComp = GetComp<CompPowerTrader>();
            powerComp = (CompPower)powerTraderComp ?? GetComp<CompPower>();
            flickComp = GetComp<CompFlickable>();
            if (_pendingSettingsWrite)
            {
                _pendingSettingsWrite = false;
                OmniCrafterMod.Settings.Write();
            }
        }

        public PowerNet GetWorkingPowerNet()
        {
            return powerTraderComp?.PowerNet ?? powerComp?.PowerNet;
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
            ProcessAutoOrders();
        }

        private void ProcessAutoOrders()
        {
            if (autoOrders.Count == 0) return;

            // Log.Message($"[OmniCrafter] 处理 {autoOrders.Count} 自动订单...");
            bool godDebug = debugNoPowerRequired && DebugSettings.godMode;
            if (!godDebug)
            {
                bool isOn = (powerTraderComp != null || powerComp != null);
                if (!isOn) return;
            }

            PowerNet net = godDebug ? null : GetWorkingPowerNet();
            if (!godDebug && net == null) return;

            // 防止订单被删除后索引越界，回绕到头部
            if (_processOrderIndex >= autoOrders.Count)
                _processOrderIndex = 0;

            // 自动计算本次批量大小：ceil(订单数 / TargetCycleRareTicks)，但不低于 MinBatchSize
            int batchSize = Mathf.Max(MinBatchSize, Mathf.CeilToInt((float)autoOrders.Count / TargetCycleRareTicks));
            int batchEnd = Mathf.Min(_processOrderIndex + batchSize, autoOrders.Count);

            for (int i = _processOrderIndex; i < batchEnd; i++)
            {
                AutoOrder order = autoOrders[i];
                if (order.paused) continue;
                // Log.Message($"[OmniCrafter] 处理自动订单: {order?.thingDef?.defName} x {order?.targetCount}");
                try
                {
                    if (order.thingDef == null) continue;
                    int current = order.storageOnly
                        ? OmniCrafterCache.CountInStorage(order.thingDef, Map)
                        : OmniCrafterCache.CountOnMap(order.thingDef, Map);
                    // Log.Message($"[OmniCrafter] 当前地图上 {order.thingDef.defName} 数量: {current}");
                    if (current >= order.targetCount) continue;
                    int needed = order.targetCount - current;

                    // Log.Message($"[OmniCrafter] 已有: {current}, 需要: {needed}");

                    // 计算单件电力消耗，按当前可用电量推算最多能制造的数量
                    // 避免「一次性要求全部电力，不足则跳过」导致自动订单永远无法执行
                    // 扣电优先级：内部电池(SmartInfiniteBattery) > MEC电池 > 普通电池
                    float unitCost = OmniPowerCost.CostWd(order.thingDef, order.stuffDef, order.quality, 1);
                    // Log.Message($"[OmniCrafter] 单件电力消耗: {order.thingDef.defName}: {unitCost} Wd");
                    // TotalStoredEnergy = 内部电池 + MEC电池(开启时) + 普通电池
                    float available = OmniPowerCost.TotalStoredEnergy(net);
                    // Log.Message($"[OmniCrafter] 总电量(内+MEC+普通): {available} Wd");

                    float surplusWdPerTick = OmniPowerCost.SurplusEnergyWdPerTick(net);
                    // Log.Message($"[OmniCrafter] 剩余电量每秒: {surplusWdPerTick} Wd");

                    float toCraft = 0;
                    if (godDebug || unitCost <= 0f || float.IsInfinity(available) || float.IsNaN(available))
                    {
                        // Log.Message($"[OmniCrafter] 无电力消耗或无限电量，制造全部需求: {needed}");
                        toCraft = needed;
                    }
                    else if (float.IsInfinity(surplusWdPerTick) || surplusWdPerTick * 60000f >= unitCost)
                    {
                        // 功率充足模式：单件电量需求小于等于当前功率，可视为瞬时完成，不占储能
                        // Log.Message($"[OmniCrafter] 剩余电量充足，制造全部需求: {needed}");
                        toCraft = needed;
                    }
                    else if (available >= unitCost)
                    {
                        // 储能消耗模式：按现有储能计算最多可制造数量
                        // Log.Message($"[OmniCrafter] 总电量充足，制造 {toCraft} 个 {order.thingDef.defName} (需要: {needed})");
                        float canAfford = Mathf.Floor(available / unitCost);
                        toCraft = Mathf.Min(needed, canAfford);
                    }

                    // Log.Message($"[OmniCrafter] 计划制造数量: {toCraft}");
                    if (toCraft <= 0) continue;

                    // 再次检查总电量消耗是否能被支付（功率或储能）
                    float totalCost = unitCost * toCraft;
                    if (!godDebug && !OmniPowerCost.TryDrainPower(net, totalCost))
                    {
                        // 如果一次性扣除 totalCost 失败（例如储能不足以支付全部），尝试降级为单件生产
                        // Log.Message($"[OmniCrafter] 无法扣除 {totalCost} Wd，尝试单件生产...");
                        if (toCraft > 1)
                        {
                            toCraft = 1;
                            totalCost = unitCost;
                            // Log.Message($"[OmniCrafter] 单件生产成本: {totalCost} Wd");
                            if (!OmniPowerCost.TryDrainPower(net, totalCost)) continue;
                            // Log.Message($"[OmniCrafter] 单件生产成功，制造 1 个");
                        }
                        else
                        {
                            continue;
                        }
                    }

                    // Log.Message($"[OmniCrafter] 计算制造数量: {toCraft} , 总成本: {totalCost} Wd");
                    
                    int realToCraft;
                    // (float)int.MaxValue 的值是 2147483648f
                    if (toCraft >= (float)int.MaxValue)
                    {
                        realToCraft = int.MaxValue;
                    }
                    else if (toCraft <= (float)0)
                    {
                        realToCraft = 0;
                        continue;
                    }
                    else
                    {
                        realToCraft = Mathf.FloorToInt(toCraft);
                    }
                    
                    // Log.Message($"[OmniCrafter] 真实制造数量: {realToCraft} , 总成本: {totalCost} Wd");
                    SpawnItems(order.thingDef, order.stuffDef, order.quality, realToCraft, order.outputMode);
                }
                catch (Exception ex)
                {
                    Log.Error(
                        $"[OmniCrafter] ProcessAutoOrders failed for '{order?.thingDef?.defName}': {ex.Message}");
                    Log.Error(ex.StackTrace);
                }
            }

            // 推进索引：下次 TickRare 从 batchEnd 继续；到达末尾则回绕到 0
            _processOrderIndex = (batchEnd >= autoOrders.Count) ? 0 : batchEnd;
        }

        public void AddRecent(ThingDef def)
        {
            recentCrafted.Remove(def.defName);
            recentCrafted.Insert(0, def.defName);
            if (recentCrafted.Count > 20) recentCrafted.RemoveAt(recentCrafted.Count - 1);
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

                            // 检查目标格子上是否已有同类物品且可以合堆（需 def 与 Stuff 均匹配）
                            // 注意：GetFirstThing 只按 ThingDef 匹配，不检查 Stuff；
                            //       若 existingStack 材质不同，CanStackWith 返回 false，
                            //       TryAbsorbStack 会静默失败，直接跳过进入后续生成逻辑。
                            Thing existingStack = storeCell.GetFirstThing(Map, placedThing.def);
                            if (existingStack != null && existingStack.CanStackWith(placedThing))
                            {
                                existingStack.TryAbsorbStack(placedThing, true);
                            }

                            // 第四步：若物品仍未入库（合堆跳过/失败/部分吸收后格子有余量），
                            //         直接在目标格生成。
                            //         仅当格子确实还有空位时才调用 GenSpawn，
                            //         避免把刚填满的 existingStack 顶走。
                            if (!placedThing.Destroyed && placedThing.stackCount > 0 && !placedThing.Spawned)
                            {
                                if (storeCell.GetItemCount(Map) < storeCell.GetMaxItemsAllowedInCell(Map))
                                {
                                    GenSpawn.Spawn(placedThing, storeCell, Map);
                                }
                            }

                            // 最终兜底：若仍未入库（如格子已满溢出），扔回建筑旁边
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