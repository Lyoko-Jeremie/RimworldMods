using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace FullyAutomaticOmniCrafter
{
    // 现实编织器 (Reality Weaver)
    // 将指定活动区内的待建造的建筑蓝图立即建造成建筑
    // 以工作量消耗电力，优先扣除CompMatterEnergyConverterBattery的能量
    // 断电时停止工作，停止工作时无任何性能影响
    // 用类似于开发者模式（God Mode）的逻辑
    public class Building_RealityWeaver : Building
    {
        // ── 内部状态 ──────────────────────────────────────────────────────────
        private CompPowerTrader _powerComp;

        /// <summary>激活区域（null = 全图）</summary>
        private Area _targetArea;

        /// <summary>是否启用具现功能（需要用户手动打开）</summary>
        private bool _enabled = false;

        /// <summary>每单位工作量消耗的电量（Wd），可由 XML def 中的自定义 Stat 覆盖。默认 1 Wd / 1 Work</summary>
        private const float DefaultWorkToEnergyFactor = 1f;

        // ── TickRare 分帧防抖：同一地图上所有编织器共用同一个全局时间戳 ─────────
        private const int TickRareInterval = 250;
        private int _lastWeaveTickGame = -9999;

        // ── 初始化 ────────────────────────────────────────────────────────────
        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            _powerComp = GetComp<CompPowerTrader>();
        }

        // ── 电源状态 ───────────────────────────────────────────────────────────
        private bool IsPowered => _powerComp == null || _powerComp.PowerOn;

        // ── 存档 ──────────────────────────────────────────────────────────────
        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref _targetArea, "targetArea");
            Scribe_Values.Look(ref _enabled, "realityWeaverEnabled", false);
        }

        // ── Tick ──────────────────────────────────────────────────────────────
        public override void TickRare()
        {
            base.TickRare();

            // 断电或未启用：直接返回，无任何性能开销
            if (!_enabled || !IsPowered) return;

            // 防抖：每 TickRare 只执行一次（即使地图上有多个编织器）
            int now = Find.TickManager.TicksGame;
            if (now - _lastWeaveTickGame < TickRareInterval) return;
            _lastWeaveTickGame = now;

            TryWeaveAll();
        }

        // ── 核心逻辑 ──────────────────────────────────────────────────────────
        private void TryWeaveAll()
        {
            if (!Spawned) return;

            // 收集区域内的所有蓝图（Blueprint_Build）
            List<Blueprint_Build> blueprints = CollectBlueprints();
            // 收集区域内所有未完工的施工框（Frame）
            List<Frame> frames = CollectFrames();

            if (blueprints.Count == 0 && frames.Count == 0) return;

            // 计算总工作量
            float totalWork = 0f;
            foreach (Blueprint_Build bp in blueprints)
                totalWork += GetBlueprintWork(bp);
            foreach (Frame frame in frames)
                totalWork += Mathf.Max(0f, frame.WorkLeft);

            float energyCost = totalWork * DefaultWorkToEnergyFactor;

            // 尝试扣除电量
            PowerNet net = _powerComp?.PowerNet;
            if (!TryDrainPowerForWeaver(net, energyCost)) return;

            // 具现：先处理蓝图（Blueprint → 建筑），再处理施工框（Frame → 建筑）
            int count = 0;
            foreach (Blueprint_Build bp in blueprints)
            {
                if (bp == null || bp.Destroyed || !bp.Spawned) continue;
                if (RealizeBlueprint(bp)) count++;
            }
            foreach (Frame frame in frames)
            {
                if (frame == null || frame.Destroyed || !frame.Spawned) continue;
                if (CompleteFrame(frame)) count++;
            }

            if (count > 0)
            {
                FleckMaker.ThrowLightningGlow(DrawPos, Map, 1.5f);
                SoundDefOf.Building_Complete?.PlayOneShot(new TargetInfo(Position, Map));
            }
        }

        // ── 收集区域内的蓝图 ───────────────────────────────────────────────────
        private List<Blueprint_Build> CollectBlueprints()
        {
            Faction playerFaction = Faction.OfPlayerSilentFail;
            if (playerFaction == null) return new List<Blueprint_Build>();

            List<Thing> all = Map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint);
            var result = new List<Blueprint_Build>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] is Blueprint_Build bp
                    && !bp.Destroyed
                    && bp.Spawned
                    && bp.Faction == playerFaction
                    && (_targetArea == null || _targetArea[bp.Position]))
                {
                    result.Add(bp);
                }
            }
            return result;
        }

        // ── 收集区域内的施工框 ─────────────────────────────────────────────────
        private List<Frame> CollectFrames()
        {
            Faction playerFaction = Faction.OfPlayerSilentFail;
            if (playerFaction == null) return new List<Frame>();

            List<Thing> all = Map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame);
            var result = new List<Frame>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] is Frame frame
                    && !frame.Destroyed
                    && frame.Spawned
                    && frame.Faction == playerFaction
                    && (_targetArea == null || _targetArea[frame.Position]))
                {
                    result.Add(frame);
                }
            }
            return result;
        }

        // ── 工作量估算 ─────────────────────────────────────────────────────────
        private static float GetBlueprintWork(Blueprint_Build bp)
        {
            if (bp?.def?.entityDefToBuild == null) return 0f;
            return Mathf.Max(0f, bp.def.entityDefToBuild.GetStatValueAbstract(StatDefOf.WorkToBuild, bp.stuffToUse));
        }

        // ── 电量扣除：优先 MEC 专属电池 ────────────────────────────────────────
        private static bool TryDrainPowerForWeaver(PowerNet net, float amountWd)
        {
            if (amountWd <= 0f) return true;
            if (net == null) return false;

            // 无限功率/电量直接放行
            float gain = net.CurrentEnergyGainRate();
            if (float.IsInfinity(gain) || float.IsNaN(gain) || gain >= 1_000_000f) return true;

            // 统计总可用电量
            float available = 0f;
            if (net.batteryComps != null)
                foreach (CompPowerBattery bat in net.batteryComps)
                    available += bat.StoredEnergy;

            if (available < amountWd) return false;

            float remaining = amountWd;

            // 第一优先：MEC 专属电池（CompMatterEnergyConverterBattery）
            if (net.batteryComps != null)
            {
                foreach (CompPowerBattery bat in net.batteryComps)
                {
                    if (remaining <= 0f) break;
                    if (bat is CompMatterEnergyConverterBattery)
                    {
                        float draw = Mathf.Min(bat.StoredEnergy, remaining);
                        if (draw > 1e-6f) { bat.DrawPower(draw); remaining -= draw; }
                    }
                }
            }

            // 第二优先：OmniCrafter 智能无限电池
            if (remaining > 1e-6f && net.batteryComps != null)
            {
                foreach (CompPowerBattery bat in net.batteryComps)
                {
                    if (remaining <= 0f) break;
                    if (bat is CompOmniCrafterSmartInfiniteBattery)
                    {
                        float draw = Mathf.Min(bat.StoredEnergy, remaining);
                        if (draw > 1e-6f) { bat.DrawPower(draw); remaining -= draw; }
                    }
                }
            }

            // 第三优先：普通电池
            if (remaining > 1e-6f && net.batteryComps != null)
            {
                foreach (CompPowerBattery bat in net.batteryComps)
                {
                    if (remaining <= 0f) break;
                    if (bat is CompMatterEnergyConverterBattery) continue;
                    if (bat is CompOmniCrafterSmartInfiniteBattery) continue;
                    float actualStored = bat.StoredEnergy;
                    float draw = Mathf.Min(actualStored, remaining);
                    if (draw > 1e-6f) { bat.DrawPower(draw); remaining -= draw; }
                }
            }

            return true;
        }

        // ── 检查格子是否存在蓝图或施工框 ─────────────────────────────────────────
        private static bool HasBlueprintOrFrame(IntVec3 cell, Map map)
        {
            List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is Blueprint || things[i] is Frame)
                    return true;
            }
            return false;
        }

        // ── 将阻挡物移动到附近无蓝图/框架的空闲格子 ─────────────────────────────
        private static bool TryMoveBlockingThing(Thing blocker, Map map)
        {
            IntVec3 originalPos = blocker.Position;

            IntVec3 targetCell = IntVec3.Invalid;
            for (int radius = 1; radius <= 15; radius++)
            {
                foreach (IntVec3 cell in GenRadial.RadialCellsAround(originalPos, radius, false))
                {
                    if (cell == originalPos) continue;
                    if (!cell.InBounds(map)) continue;
                    if (!cell.Walkable(map)) continue;
                    if (HasBlueprintOrFrame(cell, map)) continue;
                    targetCell = cell;
                    break;
                }
                if (targetCell.IsValid) break;
            }

            if (!targetCell.IsValid) return false;

            blocker.DeSpawn();
            if (GenPlace.TryPlaceThing(blocker, targetCell, map, ThingPlaceMode.Direct))
                return true;
            return GenPlace.TryPlaceThing(blocker, targetCell, map, ThingPlaceMode.Near);
        }

        // ── 联动 VoidDeleter：拆除占位建筑并就地返还材料 ──────────────────────
        /// <summary>
        /// 遍历新建筑占地格，找出所有会被 SpawningWipes 擦除的现存建筑，
        /// 调用 VoidDeleter 的拆除返还计算将其静默销毁，并把材料掉落在编织器旁。
        /// 蓝图 / 施工框 等临时物体直接静默销毁，不产生任何返还。
        /// </summary>
        private void DemolishAndLootBlockers(IntVec3 pos, Rot4 rot, BuildableDef newDef, Map map)
        {
            foreach (IntVec3 cell in GenAdj.CellsOccupiedBy(pos, rot, newDef.Size))
            {
                // 使用内部列表引用，倒序遍历，Destroy 从列表末尾移除时不影响前面的索引
                List<Thing> thingsAt = map.thingGrid.ThingsListAtFast(cell);
                for (int i = thingsAt.Count - 1; i >= 0; i--)
                {
                    Thing thing = thingsAt[i];
                    if (thing == null || thing.Destroyed) continue;
                    if (!GenSpawn.SpawningWipes(newDef, thing.def)) continue;

                    // 蓝图 / 施工框：静默销毁，不返还（材料尚未投入或已处理）
                    if (thing is Blueprint || thing is Frame)
                    {
                        if (!thing.Destroyed) thing.Destroy(DestroyMode.Vanish);
                        continue;
                    }

                    // 普通建筑：通过 VoidDeleter 计算拆除材料返还并在编织器处掉落
                    if (thing is Building)
                    {
                        var products = Building_VoidDeleter.ComputeDeconstructLeavings(thing);
                        if (!thing.Destroyed) thing.Destroy(DestroyMode.Vanish);
                        DropProductsNear(products, Position, map);
                        SoundDefOf.Building_Deconstructed.PlayOneShot(new TargetInfo(cell, map));
                        continue;
                    }

                    // 植物、物品等：静默移除
                    if (!thing.Destroyed) thing.Destroy(DestroyMode.Vanish);
                }
            }
        }

        /// <summary>
        /// 将拆除产物就近掉落在 <paramref name="dropPos"/> 附近。
        /// </summary>
        private static void DropProductsNear(
            List<(ThingDef def, int count)> products,
            IntVec3 dropPos,
            Map map)
        {
            foreach (var (def, total) in products)
            {
                int remaining = total;
                while (remaining > 0)
                {
                    int stackMax  = def.stackLimit > 0 ? def.stackLimit : 1;
                    int stackSize = Mathf.Min(remaining, stackMax);
                    Thing item = ThingMaker.MakeThing(def);
                    item.stackCount = stackSize;
                    GenPlace.TryPlaceThing(item, dropPos, map, ThingPlaceMode.Near);
                    remaining -= stackSize;
                }
            }
        }

        // ── 具现单个蓝图（Blueprint_Build → 建筑/地形） ────────────────────────
        private bool RealizeBlueprint(Blueprint_Build bp)
        {
            try
            {
                // 二次防护：仅处理属于己方阵营的蓝图
                Faction playerFaction = Faction.OfPlayerSilentFail;
                if (playerFaction == null || bp.Faction != playerFaction) return false;

                IntVec3 pos = bp.Position;
                Rot4 rot = bp.Rotation;
                Map map = bp.Map;

                // 检查是否有无法移走的障碍物（可拾取物品），尝试将其移动到附近空闲位置
                Thing blocker = bp.BlockingHaulableOnTop();
                if (blocker != null)
                {
                    if (!TryMoveBlockingThing(blocker, map))
                        return false;
                }

                ThingDef buildDef = bp.def.entityDefToBuild as ThingDef;

                if (buildDef != null)
                {
                    // ── 建筑类型 ──────────────────────────────────────────────
                    ThingDef stuffToUse = bp.stuffToUse;

                    // 拆除占位建筑（联动 VoidDeleter 计算返还材料，然后静默移除）
                    DemolishAndLootBlockers(pos, rot, buildDef, map);

                    // 销毁蓝图
                    if (!bp.Destroyed) bp.Destroy(DestroyMode.Vanish);

                    // 创建建筑实体
                    Thing thing = ThingMaker.MakeThing(buildDef, buildDef.MadeFromStuff ? stuffToUse : null);
                    thing.SetFactionDirect(Faction.OfPlayer);

                    // 品质：默认 Normal
                    thing.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, ArtGenerationContext.Colony);

                    // 艺术初始化（Normal 级通常不需要，但保留接口）
                    CompArt art = thing.TryGetComp<CompArt>();
                    if (art != null && !art.Active && thing.TryGetComp<CompQuality>() == null)
                        art.InitializeArt(ArtGenerationContext.Colony);

                    // 继承样式（保留蓝图的风格定义）
                    if (bp.StyleDef != null && bp.StyleSourcePrecept == null)
                        thing.StyleDef = bp.StyleDef;

                    // 占位物已由 DemolishAndLootBlockers 清理，直接静默生成
                    GenSpawn.Spawn(thing, pos, map, rot, WipeMode.Vanish);
                    return true;
                }
                else
                {
                    // ── 地形类型 ──────────────────────────────────────────────
                    TerrainDef terrainDef = bp.def.entityDefToBuild as TerrainDef;
                    if (terrainDef == null) return false;

                    if (!bp.Destroyed) bp.Destroy(DestroyMode.Vanish);

                    if (terrainDef.temporary)
                        map.terrainGrid.SetTempTerrain(pos, terrainDef);
                    else
                        map.terrainGrid.SetTerrain(pos, terrainDef);

                    FilthMaker.RemoveAllFilth(pos, map);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RealityWeaver] RealizeBlueprint failed for '{bp?.def?.defName}': {ex.Message}");
                return false;
            }
        }

        // ── 完成单个施工框（Frame → 建筑/地形） ────────────────────────────────
        private bool CompleteFrame(Frame frame)
        {
            try
            {
                // 二次防护：仅处理属于己方阵营的施工框
                Faction playerFaction = Faction.OfPlayerSilentFail;
                if (playerFaction == null || frame.Faction != playerFaction) return false;

                IntVec3 pos = frame.Position;
                Rot4 rot = frame.Rotation;
                Map map = frame.Map;

                ThingDef buildDef = frame.def.entityDefToBuild as ThingDef;

                if (buildDef != null)
                {
                    // 缓存 Stuff（在 Destroy 前读取）
                    ThingDef stuffDef = frame.Stuff;

                    // 清理框架内的材料容器
                    frame.resourceContainer?.ClearAndDestroyContents();
                    map = frame.Map; // 再次读取，防止意外
                    bool wasSelected = Find.Selector.IsSelected(frame);

                    frame.Destroy(DestroyMode.Vanish);

                    // 拆除框架占位之外、仍阻挡生成的其他建筑（联动 VoidDeleter）
                    DemolishAndLootBlockers(pos, rot, buildDef, map);

                    Thing thing = ThingMaker.MakeThing(buildDef, stuffDef);
                    thing.SetFactionDirect(Faction.OfPlayer);

                    thing.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, ArtGenerationContext.Colony);

                    CompArt art2 = thing.TryGetComp<CompArt>();
                    if (art2 != null && !art2.Active && thing.TryGetComp<CompQuality>() == null)
                        art2.InitializeArt(ArtGenerationContext.Colony);

                    if (frame.StyleDef != null)
                        thing.StyleDef = frame.StyleDef;

                    thing.HitPoints = thing.MaxHitPoints;

                    Thing spawned = GenSpawn.Spawn(thing, pos, map, rot, WipeMode.Vanish);

                    if (wasSelected && spawned != null)
                        Find.Selector.Select(spawned, false, false);

                    return true;
                }
                else
                {
                    TerrainDef terrainDef = frame.def.entityDefToBuild as TerrainDef;
                    if (terrainDef == null) return false;

                    frame.resourceContainer?.ClearAndDestroyContents();
                    frame.Destroy(DestroyMode.Vanish);

                    if (terrainDef.temporary)
                        map.terrainGrid.SetTempTerrain(pos, terrainDef);
                    else
                        map.terrainGrid.SetTerrain(pos, terrainDef);

                    FilthMaker.RemoveAllFilth(pos, map);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RealityWeaver] CompleteFrame failed for '{frame?.def?.defName}': {ex.Message}");
                return false;
            }
        }

        // ── Gizmos ───────────────────────────────────────────────────────────
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            // 开关按钮
            yield return new Command_Toggle
            {
                defaultLabel = "RealityWeaver_Toggle".Translate(),
                defaultDesc = "RealityWeaver_ToggleDesc".Translate(),
                icon = RealityWeaverTex.IconToggle,
                isActive = () => _enabled,
                toggleAction = () => _enabled = !_enabled
            };

            // 选择激活区域
            string areaLabel = _targetArea?.Label ?? (string)"RealityWeaver_EntireMap".Translate();
            yield return new Command_Action
            {
                defaultLabel = "RealityWeaver_SelectArea".Translate(),
                defaultDesc = "RealityWeaver_SelectAreaDesc".Translate(areaLabel),
                icon = RealityWeaverTex.IconSelectArea,
                action = () =>
                {
                    var options = new List<FloatMenuOption>();
                    options.Add(new FloatMenuOption("RealityWeaver_EntireMap".Translate(), () =>
                        _targetArea = null));
                    foreach (Area area in Map.areaManager.AllAreas)
                    {
                        Area captured = area;
                        options.Add(new FloatMenuOption(area.Label, () =>
                            _targetArea = captured));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            };
        }

        // ── 信息栏 ────────────────────────────────────────────────────────────
        public override string GetInspectString()
        {
            string s = base.GetInspectString();

            if (!_enabled)
            {
                if (!s.NullOrEmpty()) s += "\n";
                s += "RealityWeaver_Disabled".Translate();
                return s;
            }

            if (!IsPowered)
            {
                if (!s.NullOrEmpty()) s += "\n";
                s += "RealityWeaver_NoPower".Translate();
                return s;
            }

            // 统计待具现数量
            int bpCount = 0, frameCount = 0;
            float totalWork = 0f;

            Faction playerFaction = Faction.OfPlayerSilentFail;

            List<Thing> bpList = Map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint);
            for (int i = 0; i < bpList.Count; i++)
            {
                if (bpList[i] is Blueprint_Build bp
                    && bp.Faction == playerFaction
                    && (_targetArea == null || _targetArea[bp.Position]))
                {
                    bpCount++;
                    totalWork += GetBlueprintWork(bp);
                }
            }

            List<Thing> frameList = Map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame);
            for (int i = 0; i < frameList.Count; i++)
            {
                if (frameList[i] is Frame frame
                    && frame.Faction == playerFaction
                    && !frame.IsCompleted()
                    && (_targetArea == null || _targetArea[frame.Position]))
                {
                    frameCount++;
                    totalWork += Mathf.Max(0f, frame.WorkLeft);
                }
            }

            if (!s.NullOrEmpty()) s += "\n";
            s += "RealityWeaver_Status".Translate(
                bpCount + frameCount,
                (totalWork * DefaultWorkToEnergyFactor).ToString("N1"),
                _targetArea?.Label ?? (string)"RealityWeaver_EntireMap".Translate());

            return s;
        }
    }

    // ── 材质缓存 ──────────────────────────────────────────────────────────────
    [StaticConstructorOnStartup]
    public static class RealityWeaverTex
    {
        public static readonly Texture2D IconToggle =
            ContentFinder<Texture2D>.Get("UI/Commands/RealityWeaver_Toggle", false)
            ?? ContentFinder<Texture2D>.Get("UI/Commands/OmniCrafter_LaunchReport", false)
            ?? BaseContent.WhiteTex;

        public static readonly Texture2D IconSelectArea =
            ContentFinder<Texture2D>.Get("UI/Commands/RealityWeaver_SelectArea", false)
            ?? ContentFinder<Texture2D>.Get("UI/Commands/UltimateAutoRepair_SelectArea", false)
            ?? BaseContent.WhiteTex;
    }
}

