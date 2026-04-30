using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace FullyAutomaticOmniCrafter
{
    // 蓝图具现器 (Blueprint Realizer)
    // 将指定活动区内的待建造的建筑蓝图立即建造成建筑
    // 以工作量消耗电力，优先扣除CompMatterEnergyConverterBattery的能量
    // 断电时停止工作，停止工作时无任何性能影响
    // 用类似于开发者模式（God Mode）的逻辑
    public class Building_BlueprintRealizer : Building
    {
        // ── 内部状态 ──────────────────────────────────────────────────────────
        private CompPowerTrader _powerComp;

        /// <summary>激活区域（null = 全图）</summary>
        private Area _targetArea;

        /// <summary>是否启用具现功能（需要用户手动打开）</summary>
        private bool _enabled = false;

        /// <summary>每单位工作量消耗的电量（Wd），可由 XML def 中的自定义 Stat 覆盖。默认 1 Wd / 1 Work</summary>
        private const float DefaultWorkToEnergyFactor = 1f;

        // ── TickRare 分帧防抖：同一地图上所有具现器共用同一个全局时间戳 ─────────
        private const int TickRareInterval = 250;
        private int _lastRealizeTickGame = -9999;

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
            Scribe_Values.Look(ref _enabled, "blueprintRealizerEnabled", false);
        }

        // ── Tick ──────────────────────────────────────────────────────────────
        public override void TickRare()
        {
            base.TickRare();

            // 断电或未启用：直接返回，无任何性能开销
            if (!_enabled || !IsPowered) return;

            // 防抖：每 TickRare 只执行一次（即使地图上有多个具现器）
            int now = Find.TickManager.TicksGame;
            if (now - _lastRealizeTickGame < TickRareInterval) return;
            _lastRealizeTickGame = now;

            TryRealizeAll();
        }

        // ── 核心逻辑 ──────────────────────────────────────────────────────────
        private void TryRealizeAll()
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
            if (!TryDrainPowerForRealizer(net, energyCost)) return;

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
            List<Thing> all = Map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint);
            var result = new List<Blueprint_Build>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] is Blueprint_Build bp
                    && !bp.Destroyed
                    && bp.Spawned
                    && bp.Faction == Faction.OfPlayer
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
            List<Thing> all = Map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame);
            var result = new List<Frame>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] is Frame frame
                    && !frame.Destroyed
                    && frame.Spawned
                    && frame.Faction == Faction.OfPlayer
                    && !frame.IsCompleted()
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
        private static bool TryDrainPowerForRealizer(PowerNet net, float amountWd)
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

        // ── 具现单个蓝图（Blueprint_Build → 建筑/地形） ────────────────────────
        private bool RealizeBlueprint(Blueprint_Build bp)
        {
            try
            {
                IntVec3 pos = bp.Position;
                Rot4 rot = bp.Rotation;
                Map map = bp.Map;

                // 检查是否有无法移走的障碍物（可拾取物品）
                if (bp.BlockingHaulableOnTop() != null) return false;

                ThingDef buildDef = bp.def.entityDefToBuild as ThingDef;

                if (buildDef != null)
                {
                    // ── 建筑类型 ──────────────────────────────────────────────
                    ThingDef stuffToUse = bp.stuffToUse;

                    // 清除障碍建筑（使用 FullRefund 以免产生废料提示）
                    GenSpawn.WipeExistingThings(pos, rot, bp.def.entityDefToBuild, map, DestroyMode.Deconstruct);

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

                    GenSpawn.Spawn(thing, pos, map, rot, WipeMode.FullRefund);
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
                Log.Error($"[BlueprintRealizer] RealizeBlueprint failed for '{bp?.def?.defName}': {ex.Message}");
                return false;
            }
        }

        // ── 完成单个施工框（Frame → 建筑/地形） ────────────────────────────────
        private bool CompleteFrame(Frame frame)
        {
            try
            {
                IntVec3 pos = frame.Position;
                Rot4 rot = frame.Rotation;
                Map map = frame.Map;

                ThingDef buildDef = frame.def.entityDefToBuild as ThingDef;

                if (buildDef != null)
                {
                    // 清理框架内的材料容器
                    frame.resourceContainer?.ClearAndDestroyContents();
                    map = frame.Map; // 再次读取，防止意外
                    bool wasSelected = Find.Selector.IsSelected(frame);

                    frame.Destroy(DestroyMode.Vanish);

                    Thing thing = ThingMaker.MakeThing(buildDef, frame.Stuff);
                    thing.SetFactionDirect(Faction.OfPlayer);

                    thing.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, ArtGenerationContext.Colony);

                    CompArt art2 = thing.TryGetComp<CompArt>();
                    if (art2 != null && !art2.Active && thing.TryGetComp<CompQuality>() == null)
                        art2.InitializeArt(ArtGenerationContext.Colony);

                    if (frame.StyleDef != null)
                        thing.StyleDef = frame.StyleDef;

                    thing.HitPoints = thing.MaxHitPoints;

                    Thing spawned = GenSpawn.Spawn(thing, pos, map, rot, WipeMode.FullRefund);

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
                Log.Error($"[BlueprintRealizer] CompleteFrame failed for '{frame?.def?.defName}': {ex.Message}");
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
                defaultLabel = "BlueprintRealizer_Toggle".Translate(),
                defaultDesc = "BlueprintRealizer_ToggleDesc".Translate(),
                icon = BlueprintRealizerTex.IconToggle,
                isActive = () => _enabled,
                toggleAction = () => _enabled = !_enabled
            };

            // 选择激活区域
            string areaLabel = _targetArea?.Label ?? (string)"BlueprintRealizer_EntireMap".Translate();
            yield return new Command_Action
            {
                defaultLabel = "BlueprintRealizer_SelectArea".Translate(),
                defaultDesc = "BlueprintRealizer_SelectAreaDesc".Translate(areaLabel),
                icon = BlueprintRealizerTex.IconSelectArea,
                action = () =>
                {
                    var options = new List<FloatMenuOption>();
                    options.Add(new FloatMenuOption("BlueprintRealizer_EntireMap".Translate(), () =>
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
                s += "BlueprintRealizer_Disabled".Translate();
                return s;
            }

            if (!IsPowered)
            {
                if (!s.NullOrEmpty()) s += "\n";
                s += "BlueprintRealizer_NoPower".Translate();
                return s;
            }

            // 统计待具现数量
            int bpCount = 0, frameCount = 0;
            float totalWork = 0f;

            List<Thing> bpList = Map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint);
            for (int i = 0; i < bpList.Count; i++)
            {
                if (bpList[i] is Blueprint_Build bp
                    && bp.Faction == Faction.OfPlayer
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
                    && frame.Faction == Faction.OfPlayer
                    && !frame.IsCompleted()
                    && (_targetArea == null || _targetArea[frame.Position]))
                {
                    frameCount++;
                    totalWork += Mathf.Max(0f, frame.WorkLeft);
                }
            }

            if (!s.NullOrEmpty()) s += "\n";
            s += "BlueprintRealizer_Status".Translate(
                bpCount + frameCount,
                (totalWork * DefaultWorkToEnergyFactor).ToString("N1"),
                _targetArea?.Label ?? (string)"BlueprintRealizer_EntireMap".Translate());

            return s;
        }
    }

    // ── 材质缓存 ──────────────────────────────────────────────────────────────
    [StaticConstructorOnStartup]
    public static class BlueprintRealizerTex
    {
        public static readonly Texture2D IconToggle =
            ContentFinder<Texture2D>.Get("UI/Commands/BlueprintRealizer_Toggle", false)
            ?? ContentFinder<Texture2D>.Get("UI/Commands/OmniCrafter_LaunchReport", false)
            ?? BaseContent.WhiteTex;

        public static readonly Texture2D IconSelectArea =
            ContentFinder<Texture2D>.Get("UI/Commands/BlueprintRealizer_SelectArea", false)
            ?? ContentFinder<Texture2D>.Get("UI/Commands/UltimateAutoRepair_SelectArea", false)
            ?? BaseContent.WhiteTex;
    }
}