using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;
using Verse.Sound;

namespace FullyAutomaticOmniCrafter
{

    /// <summary>
    /// 幻影墙建筑类。
    ///
    /// 设计
    /// 1所有新建的墙体方块预设一套“默认通行规则”
    /// 例如，默认设置可以如下：
    /// 宠物：允许通行
    /// 商人：允许通行
    /// 其他实体：禁止通行
    /// 囚犯：禁止通行
    /// 野生动物：禁止通行
    /// 2 墙体建成后，玩家可以选中单个方块，或同时选中多个方块（使用 SHIFT+点击 进行多选），随后点击相应的交互按钮（Gizmo），即可切换或调整允许/禁止特定类型单位通行的规则
    /// 3设计一个标记工具 (Designator) 用来批量设置墙的通过性属性/规则
    /// 4通过性规则的可视化，让玩家可以直接看出哪个墙体的规则是什么 TODO 绘制规则可视化
    /// 5保持当前的room特性不变，特别是墙体包围的区域能够围成房间，以及连续的墙体会自成房间
    /// 6让规则不同的墙各自生成不连续的房间，即使这些墙连接在一起
    ///
    /// 可视化方案（优缺点分析）：
    /// 颜色叠加 重写 DrawColor 根据规则返回不同颜色 简单直观，但颜色有限且可能与材质颜色冲突
    /// Overlay 系统 类似电力网络的覆盖层显示 可切换显示/隐藏
    /// 图标覆盖 在 Draw() 中绘制小图标 信息丰富，但可能视觉杂乱
    ///
    /// -----------------------------------------------------------------
    /// 
    /// 寻路机制说明（RimWorld 1.6 Unity Jobs 架构）：
    ///
    /// 寻路管线分三阶段：
    ///   ① PathGridJob.CostForCell  [BurstCompile, IJobParallelFor]
    ///      — 预计算与小人无关的基础代价网格 grid[]
    ///      — 此阶段由 ThingDef.passability 决定：需设为 Standable，
    ///        否则 grid[index] = 10000，对所有人封路。
    ///        （IPathFindCostProvider 仅在阶段②写入 providerCost，
    ///          阶段③取 max(grid, providerCost)，无法下调已为 10000 的 grid）
    ///
    ///   ② PathGridDoorsBlockedJob.Execute  [普通托管 IJob]
    ///      — 遍历地图上所有 IPathFindCostProvider，调用 PathFindCostFor(pawn)
    ///      — 结果存入 providerCost[index]（每个小人独立计算）
    ///
    ///   ③ PathFinderJob.IndexCost(index)  [BurstCompile, IJob，A* 主循环]
    ///      — providerCost[index] == ushort.MaxValue → 返回 10000（不可通行）
    ///      — 否则: max(grid[index], providerCost[index])
    ///
    /// 真空隔离机制：
    ///   RimWorld 的真空系统分两层：
    ///   A) 房间层 (Room/VacuumComponent)：
    ///      — 真空在房间之间流动，前提是两侧是「不同的房间」。
    ///      — 房间边界由 Region 系统决定：passability=Standable 会被
    ///        RegionTypeUtility.GetExpectedRegionType 判定为 RegionType.Normal，
    ///        不形成房间边界，两侧合并成同一房间——真空无法隔离。
    ///      — 修复：Harmony patch 将幻影墙格返回自定义 RegionType(18)，
    ///        连续幻影墙合并为一个多格区域，室内/室外各成独立房间，
    ///        不产生大量无用 Portal 单格房间。
    ///   B) 单格层 (VacuumUtility.EverInVacuum)：
    ///      — 检查单个格子是否暴露于真空。
    ///      — 需要 IsAirtight=true → ExchangeVacuum=false，才会返回 false
    ///        （即幻影墙格本身不存在真空，穿越的小人不受真空伤害）。
    ///      — 修复：覆写 IsAirtight / ExchangeVacuum。
    /// </summary>
    public class Building_OmniPhantomWall2 : Building, IPathFindCostProvider
    {
        // private static readonly Action<RegionDirtyer, IntVec3, bool> NotifyWalkabilityChangedInvoker = CreateNotifyWalkabilityChangedInvoker();
        //
        // private static Action<RegionDirtyer, IntVec3, bool> CreateNotifyWalkabilityChangedInvoker()
        // {
        //     try
        //     {
        //         var method = AccessTools.Method(typeof(RegionDirtyer), "Notify_WalkabilityChanged");
        //         if (method == null)
        //             return null;
        //
        //         return AccessTools.MethodDelegate<Action<RegionDirtyer, IntVec3, bool>>(method);
        //     }
        //     catch (Exception ex)
        //     {
        //         Log.Warning($"[OmniPhantomWall2] Failed to bind RegionDirtyer.Notify_WalkabilityChanged: {ex}");
        //         return null;
        //     }
        // }

        /// <summary>
        /// 重写绘制颜色：保留材料的基础颜色，但强制将透明度改为 0.3
        /// </summary>
        public override Color DrawColor
        {
            get
            {
                var alpha = 0.5f;
                
                // base.DrawColor 会自动计算并返回当前材料(Stuff)的颜色，或者派系的颜色
                Color originalColor = base.DrawColor;
                
                // 返回 R, G, B 不变，强制将 Alpha 通道设为 0.3f (30% 不透明度)
                return new Color(originalColor.r, originalColor.g, originalColor.b, alpha);
            }
        }
        
        // ── 无敌判定 ──────────────────────────────────────────────────

        /// <summary>
        /// 始终视为满血，不显示血条损耗。
        /// Thing.HitPoints 是 virtual property，覆写 getter 后游戏 UI 和逻辑
        /// 均读取此值，血条永远满格，彻底隐去"耐久"概念。
        /// setter 维持原始存储（不影响序列化等内部流程）。
        /// </summary>
        public override int HitPoints
        {
            get => MaxHitPoints;
            set { /* 忽略任何写入，维持无耐久状态 */ }
        }

        /// <summary>
        /// 吸收所有伤害，作为双重保险（防止绕过 HitPoints 直接调用 TakeDamage 的情况）。
        /// </summary>
        public override void PreApplyDamage(ref DamageInfo dinfo, out bool absorbed)
        {
            absorbed = true; // 绝对无敌
        }

        /// <summary>
        /// VoidDeleter 等外部系统设置此标志后，
        /// 下一次 Destroy(Vanish) 调用将被放行（且自动清除标志）。
        /// </summary>
        [ThreadStatic]
        internal static bool _authorizedVanish = false;
        
        /// <summary>
        /// 合法销毁途径：
        ///   • DestroyMode.Deconstruct  — 玩家主动下达拆除指令（唯一正常移除方式）
        ///   • allowDestroyNonDestroyable — 系统级强制销毁（地图卸载等内部流程）
        ///
        /// 阻断途径（静默忽略）：
        ///   • Vanish / KillFinalize / KillFinalizeLeavingsOnly — 战斗/脚本销毁
        ///   • WillReplace — 玩家通过"在上面直接建造"
        ///   • Cancel / Refund / FailConstruction — 仅作用于蓝图/框架阶段，
        ///     对已建成建筑实际上不会触发，阻断无害，统一处理以防万一
        /// </summary>
        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            // 玩家主动拆除指令
            if (mode == DestroyMode.Deconstruct)
            {
                base.Destroy(mode);
                return;
            }
            // mod脚本拆除
            if (mode == DestroyMode.Vanish)
            {
                if (!_authorizedVanish)
                {
                    Log.Message($"[OmniPhantomWall2] 阻止销毁：{this}，DestroyMode={mode}");
                    return;
                }
                _authorizedVanish = false;
                base.Destroy(mode);
                return;
            }
            // 在上面替换建造其他建筑导致的销毁
            if (mode == DestroyMode.WillReplace)
            {
                base.Destroy(mode);
                return;
            }
            // 系统级强制销毁（地图卸载、开发者 allowDestroyNonDestroyable）
            if (Thing.allowDestroyNonDestroyable)
            {
                base.Destroy(mode);
                return;
            }
            // 其余所有途径静默阻断
        }

        public void DestroyByScript(DestroyMode mode = DestroyMode.Vanish)
        {
            base.Destroy(mode);
        }

        // ── 真空隔离 ──────────────────────────────────────────────────
        /// <summary>
        /// 始终视为气密（不依赖材质）。
        /// Building.IsAirtight 原逻辑：isAirtight || (isStuffableAirtight && stuff.isAirtight)
        /// 这里直接返回 true，确保任何材质都能完全隔离真空。
        /// </summary>
        public override bool IsAirtight => true;

        /// <summary>
        /// 不与任何相邻房间交换真空。
        /// VacuumComponent.MergeRoomsIntoGroups 检查此属性来决定是否在房间组之间
        /// 建立真空交流通道。返回 false → 幻影墙彻底切断真空传播。
        /// </summary>
        public override bool ExchangeVacuum => false;

        // ── Passable ──────────────────────────────────────────────────
        
        public OmniPhantomWall2_PassabilitySettings settings = new OmniPhantomWall2_PassabilitySettings();

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref settings, "settings");
            if (settings == null)
                settings = new OmniPhantomWall2_PassabilitySettings();
        }
        
        
        /// <summary>
        /// 修改规则后触发区域重建
        /// </summary>
        public void ApplySettings(OmniPhantomWall2_PassabilitySettings newSettings)
        {
            int oldSig = settings.GetSignature();
            
            // 复制新设置
            settings.CopyFrom(newSettings);
            
            int newSig = settings.GetSignature();
            
            // 规则变化时只脏化当前格附近的区域，再重建 dirty 部分
            if (oldSig != newSig && Spawned)
            {
                // TODO not work
                Map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                // if (NotifyWalkabilityChangedInvoker != null)
                // {
                //     NotifyWalkabilityChangedInvoker(Map.regionDirtyer, Position, true);
                //     Map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                // }
                // else
                // {
                //     Map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                // }
            }
        }
        
        /// <summary>
        /// 实例级通行判断（替代原静态方法）
        /// </summary>
        public bool CanPawnPassInstance(Pawn pawn)
        {
            if (pawn == null) return false;
            
            // 玩家殖民者
            if (pawn.Faction == Faction.OfPlayer && pawn.RaceProps.Humanlike)
                return settings.allowColonists;
            
            // 玩家宠物
            if (pawn.Faction == Faction.OfPlayer && pawn.RaceProps.Animal)
                return settings.allowPets;
            
            // 玩家的囚犯
            if (pawn.IsPrisonerOfColony)
                return settings.allowColonyPrisoners;
            
            // 囚犯
            if (pawn.IsPrisoner)
                return settings.allowPrisoners;
            
            // 实体
            if (pawn.RaceProps.IsAnomalyEntity)
                return settings.allowEntities;
            
            // 敌对单位
            if (pawn.HostileTo(Faction.OfPlayer))
                return settings.allowHostiles;
            
            // 商人/访客（非敌对的有派系人形）
            if (!pawn.HostileTo(Faction.OfPlayer) && pawn.Faction != null && pawn.RaceProps.Humanlike)
                return settings.allowTraders;
            
            // 野生动物
            if (pawn.Faction == null && pawn.RaceProps.Animal)
                return settings.allowWildAnimals;
            
            // 默认：玩家殖民者总是可以通过
            if (pawn.Faction == Faction.OfPlayer)
                return true;
                
            return false;
        }

        // ── IPathFindCostProvider ─────────────────────────────────────
        /// <summary>
        /// 寻路代价：能通过返回 0，不能通过返回 ushort.MaxValue。
        /// </summary>
        public ushort PathFindCostFor(Pawn pawn)
        {
            return CanPawnPassInstance(pawn) ? (ushort)0 : ushort.MaxValue;
        }

        public CellRect GetOccupiedRect() => this.OccupiedRect();

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }

            // 只有在选中且有 Map 时才显示
            if (!Spawned) yield break;

            // 这里我们可以考虑是否需要一个 Gizmo 来弹出更详细的设置
            // 但根据需求，“像 Designator_PhantomWall2Passability 一样在左侧有可配置的界面”
            // 这通常意味着重写 SelectionDoNextFrame 或在某个地方调用 DoExtraGuiControls
            // 不过在 Building 中，最接近的是 DoExtraGuiControls (如果它是继承自某个支持它的类)
            // 或者我们可以模仿 Designator 的做法。
        }

        public override string GetInspectString()
        {
            string str = base.GetInspectString();
            if (Spawned)
            {
                if (!string.IsNullOrEmpty(str))
                {
                    str += "\n";
                }

                str += "OPW_CurrentPassability".Translate() + ": " + GetPassabilitySummary();
                
                // 绘制左侧 UI
                DrawPassabilitySettingsUI();
            }
            return str;
        }

        private string GetPassabilitySummary()
        {
            // 这里根据 settings 生成简短描述
            // 模仿 Designator 的预设判断
            foreach (PassabilityPreset preset in (PassabilityPreset[])Enum.GetValues(typeof(PassabilityPreset)))
            {
                if (preset == PassabilityPreset.Custom) continue;
                var presetSettings = Designator_PhantomWall2Passability.GetSettingsFromPreset(preset);
                if (settings.Equals(presetSettings))
                {
                    return Designator_PhantomWall2Passability.GetPresetLabel(preset);
                }
            }
            return Designator_PhantomWall2Passability.GetPresetLabel(PassabilityPreset.Custom);
        }

        private void DrawPassabilitySettingsUI()
        {
            // 模仿 Designator_PhantomWall2Passability.DoExtraGuiControls
            // 使用与 Designator 相同的布局，通常在左侧
            float leftX = 200f; // 默认左侧起始位置
            float bottomY = (float)UI.screenHeight - 35f;
            
            // RimWorld 1.6 中获取信息面板高度的方法
            // 如果信息面板可见，它通常是在底部的
            float paneHeight = 165f; // 默认高度
            bottomY -= paneHeight;

            Rect winRect = new Rect(leftX, bottomY - 140f, 220f, 140f);
            
            Find.WindowStack.ImmediateWindow(73625892, winRect, WindowLayer.GameUI, () =>
            {
                Rect rect = winRect.AtZero().ContractedBy(5f);
                
                Text.Font = GameFont.Small;
                Widgets.Label(rect.TopPartPixels(24f), "OPW_SelectPreset".Translate());
                
                Rect buttonArea = rect;
                buttonArea.yMin += 28f;
                
                float buttonHeight = 24f;
                float y = buttonArea.y;
                
                PassabilityPreset currentPreset = GetCurrentPreset();

                foreach (PassabilityPreset preset in (PassabilityPreset[])Enum.GetValues(typeof(PassabilityPreset)))
                {
                    Rect buttonRect = new Rect(buttonArea.x, y, buttonArea.width, buttonHeight);
                    
                    bool isSelected = (currentPreset == preset);
                    if (Widgets.RadioButtonLabeled(buttonRect, Designator_PhantomWall2Passability.GetPresetLabel(preset), isSelected))
                    {
                        if (!isSelected)
                        {
                            ApplyPresetToSelectedWalls(preset);
                            SoundDefOf.Click.PlayOneShotOnCamera();
                        }
                    }
                    
                    y += buttonHeight + 2f;
                }
            });
        }

        private PassabilityPreset GetCurrentPreset()
        {
            foreach (PassabilityPreset preset in (PassabilityPreset[])Enum.GetValues(typeof(PassabilityPreset)))
            {
                if (preset == PassabilityPreset.Custom) continue;
                var presetSettings = Designator_PhantomWall2Passability.GetSettingsFromPreset(preset);
                if (settings.Equals(presetSettings))
                {
                    return preset;
                }
            }
            return PassabilityPreset.Custom;
        }

        private void ApplyPresetToSelectedWalls(PassabilityPreset preset)
        {
            if (preset == PassabilityPreset.Custom) return;

            var newSettings = Designator_PhantomWall2Passability.GetSettingsFromPreset(preset);
            
            // 处理当前选中的所有幻影墙
            foreach (object obj in Find.Selector.SelectedObjects)
            {
                if (obj is Building_OmniPhantomWall2 wall)
                {
                    wall.ApplySettings(newSettings);
                }
            }
        }

        // ── 自定义区域类型常量 ─────────────────────────────────────────────
        /// <summary>
        /// 幻影墙专用 RegionType，值 = 18 = 0b10010 = Normal(2) | 高位标志(0x10)。
        ///
        /// 关键特性（RegionTypeUtility 中无逐 bit 处理，均为精确值比较）：
        ///   • (18 & Set_Passable=0xE) = 2 ≠ 0 → Passable()=true：BFS 可达性可穿越 ✓
        ///   • 18 ≠ Portal(4) → IsOneCellRegion()=false：洪水填充，连续幻影墙 = 一个区域 ✓
        ///   • 18 ≠ Normal(2)/ImpassableFreeAirExchange(1)/Fence(8)：
        ///     ShouldBeInTheSameRoom 返回 false → 不与 Normal 房间合并 ✓
        ///   • 幻影墙区域 door=null → IsDoorway=false → 真空隔离走 ExchangeVacuum=false ✓
        /// </summary>
        internal const RegionType PhantomWallRegionType = (RegionType)18;
    }

}