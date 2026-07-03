using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 人造人女仆展示柜类。
    /// 继承自 Building_Casket（容器建筑），用于收纳并展示人造人女仆。
    /// 具有自动休眠和自动唤醒功能。
    /// </summary>
    public class Building_ArtificialMaidDisplayCase : Building_Casket, IThingHolderWithDrawnPawn
    {
        private static readonly AccessTools.FieldRef<Thing, sbyte> MapIndexOrStateRef = 
            AccessTools.FieldRefAccess<Thing, sbyte>("mapIndexOrState");

        // 实现 IThingHolderWithDrawnPawn 接口，使渲染器能获取正确的渲染参数
        public float HeldPawnDrawPos_Y => this.def.Altitude + 0.04054054f;
        public float HeldPawnBodyAngle => 0f;
        public PawnPosture HeldPawnPosture => PawnPosture.Standing;

        public override int OpenTicks => 1;
        
        // 是否开启自动休眠功能（空闲女仆自动寻找该柜子）
        public bool autoHibernate = false;
        // 是否开启自动唤醒功能（有工作时女仆自动离开柜子）
        public bool autoWake = false;

        public override void ExposeData()
        {
            base.ExposeData();
            // 保存和加载设置数据
            Scribe_Values.Look(ref autoHibernate, "autoHibernate", false);
            Scribe_Values.Look(ref autoWake, "autoWake", false);
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            ArtificialMaidMapComponent.Get(map)?.RegisterDisplayCase(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = this.Map;
            base.DeSpawn(mode);
            ArtificialMaidMapComponent.Get(map)?.UnregisterDisplayCase(this);
        }

        /// <summary>
        /// 确定该容器是否接受指定的物品。
        /// 仅限人造人女仆进入。
        /// </summary>
        public override bool Accepts(Thing thing)
        {
            if (!base.Accepts(thing)) return false;
            if (thing is Pawn p && p.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                return true;
            }
            return false;
        }

        public override bool TryAcceptThing(Thing thing, bool allowSpecialEffects = true)
        {
            if (base.TryAcceptThing(thing, allowSpecialEffects))
            {
                if (thing is Pawn pawn)
                {
                    pawn.Drawer.renderer.SetAllGraphicsDirty();
                }
                return true;
            }
            return false;
        }

        protected override void Tick()
        {
            base.Tick();
            // 性能优化：动态调整自动唤醒的检查间隔。
            // 根据地图上展示柜的数量增加间隔，以平滑大规模部署时的 CPU 负载。
            if (this.IsHashIntervalTick(GetDynamicTickInterval()))
            {
                if (autoWake && HasAnyContents)
                {
                    Pawn pawn = ContainedThing as Pawn;
                    // 如果容器内有女仆，且检测到其有可用工作，则将其弹出
                    if (pawn != null && AnyJobFor(pawn))
                    {
                        EjectContents();
                    }
                }
            }
        }

        /// <summary>
        /// 根据地图上同类展示柜的数量获取动态 Tick 间隔。
        /// 建筑越多，单个建筑检查工作的频率越低，从而保证总体性能。
        /// </summary>
        private int GetDynamicTickInterval()
        {
            Map map = this.Map;
            if (map == null) return 1000;

            int count = 1;
            var comp = ArtificialMaidMapComponent.Get(map);
            if (comp != null)
            {
                count = comp.DisplayCaseCount;
            }
            else
            {
                // 回退逻辑：如果组件未初始化，则使用原有的统计方式
                count = map.listerThings.ThingsOfDef(this.def).Count;
            }

            // 基础间隔 250 Ticks (约 4s)
            // 每个额外建筑增加 50 Ticks
            // 最大上限 2500 Ticks (约 42s)
            return Mathf.Clamp(250 + (count - 1) * 50, 250, 2500);
        }

        /// <summary>
        /// 检测当前地图上是否有女仆可执行的工作。
        /// </summary>
        private bool AnyJobFor(Pawn pawn)
        {
            // 确保建筑已生成且 Pawn 所在环境有效
            Map map = this.Map;
            if (pawn == null || map == null)
            {
                return false;
            }

            // 优化：如果女仆没有任何启用的工作类型，且基本需求（食物、休息等）都在安全阈值内，则跳过重型的思维树检查。
            // 大多数女仆在柜子里时，玩家更关心的是她们是否有“工作”。
            
            bool hasWork = pawn.workSettings != null && pawn.workSettings.EverWork && 
                           (pawn.workSettings.WorkGiversInOrderNormal.Any() || pawn.workSettings.WorkGiversInOrderEmergency.Any());

            // 检查需求。如果极其饥饿或极其疲劳，无论是否有工作都应该考虑唤醒
            bool needImmediateAttention = false;
            if (pawn.needs != null)
            {
                if (pawn.needs.food != null && pawn.needs.food.CurLevelPercentage < 0.1f) needImmediateAttention = true;
                if (pawn.needs.rest != null && pawn.needs.rest.CurLevelPercentage < 0.1f) needImmediateAttention = true;
            }

            // 如果既没有开启的工作设置，也不急需照顾（饥饿/疲劳），则直接跳过昂贵的思维树扫描
            if (!hasWork && !needImmediateAttention)
            {
                return false;
            }

            // 临时设置女仆位置到柜子的交互格，以便思维树能正确检索附近的工作（很多 JobGiver 依赖位置）
            // 使用交互格（InteractionCell）比使用建筑中心位置更可靠，因为中心位置可能是不可通行的，会导致可达性检查失败。
            CompArtificialMaid maidComp = CompArtificialMaid.GetCompCached(pawn);
            if (maidComp != null) maidComp.isFaking = true;
            IntVec3 oldPos = pawn.Position;
            IntVec3 searchPos = this.InteractionCell;
            if (!searchPos.InBounds(map)) searchPos = this.Position;
            pawn.SetPositionDirect(searchPos);
            
            // 备份并临时修改 Spawned 状态和 Map 引用，以绕过 Reachability 和 Map 检查
            sbyte oldMapIndex = MapIndexOrStateRef(pawn);
            MapIndexOrStateRef(pawn) = (sbyte)map.Index;

            // 确保 Pawn 的必要组件已初始化。在 RimWorld 1.6 中，某些 JobGiver（如 FoodUtility）会访问 roping 等组件，
            // 如果女仆从未在地图上真正生成过（例如从旧版本加载或直接在容器中创建），这些组件可能为 null，导致 faked 状态下发生空指针异常。
            // 优化：只有在关键组件缺失时才尝试添加组件。
            if (pawn.health == null || pawn.mindState == null || pawn.roping == null)
            {
                try
                {
                    PawnComponentsUtility.AddComponentsForSpawn(pawn);
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[OuterrealmTech] Failed to add components for " + pawn.LabelShort + ": " + ex, 6654322);
                }
            }

            try
            {
                // 我们使用女仆的思维树来查看她是否会执行除“等待”或“漫步”之外的任何动作。
                // 注意：我们使用的是 MainThinkNodeRoot，它包含了所有的工作提供者（WorkGiver）。
                if (pawn.thinker != null && pawn.thinker.MainThinkNodeRoot != null)
                {
                    ThinkResult thinkResult = pawn.thinker.MainThinkNodeRoot.TryIssueJobPackage(pawn, new JobIssueParams());
                    
                    if (thinkResult.IsValid && thinkResult.Job != null)
                    {
                        JobDef def = thinkResult.Job.def;
                        // 检查这是否是一个“真实的”工作（排除空闲和进入展示柜的任务）
                        if (def != JobDefOf.Wait && 
                            def != JobDefOf.Wait_MaintainPosture && 
                            def != JobDefOf.Wait_SafeTemperature && 
                            def != JobDefOf.Wait_Wander &&
                            def != JobDefOf.GotoWander &&
                            (ArtificialMaidDefOf.EnterDisplayCase == null || def != ArtificialMaidDefOf.EnterDisplayCase))
                        {
                            // Log.Message("AnyJobFor true");
                            return true;
                        }
                        // Log.Message("AnyJobFor false");
                    }
                    // Log.Message("AnyJobFor !(thinkResult.IsValid && thinkResult.Job != null)");
                }
                // else
                // {
                //     Log.Message("AnyJobFor !(pawn.thinker != null && pawn.thinker.MainThinkNodeRoot != null)");
                // }
            }
            catch (Exception ex)
            {
                // 如果女仆处于无效状态，寻找工作可能会抛出错误。
                Log.ErrorOnce("[OuterrealmTech] Error during auto-wake job search for " + pawn.LabelShort + ": " + ex, 6654321);
            }
            finally
            {
                // 恢复状态
                if (maidComp != null) maidComp.isFaking = false;
                MapIndexOrStateRef(pawn) = oldMapIndex;
                pawn.SetPositionDirect(oldPos);
                // Log.Message("AnyJobFor finally");
            }

            return false;
        }

        /// <summary>
        /// 处理建筑的动态绘制。
        /// 这里用于在展示柜上方绘制内部女仆的视觉形象。
        /// </summary>
        public override void DynamicDrawPhaseAt(DrawPhase phase, Vector3 drawLoc, bool flip = false)
        {
            base.DynamicDrawPhaseAt(phase, drawLoc, flip);
            // 只要柜内有物就进行绘制，渲染器内部会处理不同的 phase
            if (HasAnyContents)
            {
                Pawn pawn = ContainedThing as Pawn;
                if (pawn != null)
                {
                    Vector3 pawnDrawLoc = drawLoc;
                    pawnDrawLoc.y = HeldPawnDrawPos_Y;
                    
                    // 调用女仆渲染器的 DynamicDrawPhaseAt 来进行绘制
                    // 传递 phase 让渲染器能处理 ParallelPreDraw 等阶段
                    pawn.Drawer.renderer.DynamicDrawPhaseAt(phase, pawnDrawLoc, new Rot4?(Rot4.South), true);
                }
            }
        }

        /// <summary>
        /// 获取建筑的操作按钮（Gizmos）。
        /// </summary>
        public override IEnumerable<Gizmo> GetGizmos()
        {
            // 返回父类的默认按钮
            foreach (var g in base.GetGizmos()) yield return g;

            // 自动休眠切换开关
            yield return new Command_Toggle
            {
                defaultLabel = "AutoHibernateLabel".Translate(),
                defaultDesc = "AutoHibernateDesc".Translate(),
                isActive = () => autoHibernate,
                toggleAction = () => autoHibernate = !autoHibernate,
                icon = ArtificialMaidTex.IconAutoHibernate
            };

            // 自动唤醒切换开关
            yield return new Command_Toggle
            {
                defaultLabel = "AutoWakeLabel".Translate(),
                defaultDesc = "AutoWakeDesc".Translate(),
                isActive = () => autoWake,
                toggleAction = () => autoWake = !autoWake,
                icon = ArtificialMaidTex.IconAutoWake
            };
            
            // 如果柜内有物且属于玩家派系，显示手动弹出按钮
            if (HasAnyContents && Faction == Faction.OfPlayer)
            {
                yield return new Command_Action
                {
                    defaultLabel = "ArtificialMaidDisplayCaseEject".Translate(),
                    defaultDesc = "ArtificialMaidDisplayCaseEjectDesc".Translate(),
                    icon = ArtificialMaidTex.IconPodEject,
                    action = delegate
                    {
                        WakeContainedMaid(true);
                    }
                };
            }
        }

        /// <summary>
        /// 立即唤醒柜内的人造人女仆。
        /// </summary>
        public void WakeContainedMaid(bool disableDisplayCaseAutoHibernate)
        {
            if (ContainedThing is Pawn pawn && pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                CompArtificialMaid comp = CompArtificialMaid.GetCompCached(pawn);
                if (comp != null)
                {
                    comp.allowAutoHibernate = false;
                }
            }

            if (disableDisplayCaseAutoHibernate)
            {
                autoHibernate = false;
            }

            EjectContents();
        }

        /// <summary>
        /// 获取选中女仆时的右键菜单选项。
        /// </summary>
        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn selPawn)
        {
            foreach (var opt in base.GetFloatMenuOptions(selPawn)) yield return opt;

            // 仅限人造人女仆
            if (selPawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                // 展示柜必须为空
                if (!HasAnyContents)
                {
                    // 检查路径是否可达
                    if (!selPawn.CanReach(this, PathEndMode.InteractionCell, Danger.Deadly))
                    {
                        yield return new FloatMenuOption("CannotEnterDisplayCase".Translate() + ": " + "NoPath".Translate().CapitalizeFirst(), null);
                    }
                    else
                    {
                        // 添加进入展示柜的选项
                        yield return FloatMenuUtility.DecoratePrioritizedTask(new FloatMenuOption("EnterDisplayCase".Translate(), () =>
                        {
                            Job job = JobMaker.MakeJob(ArtificialMaidDefOf.EnterDisplayCase, this);
                            selPawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                        }), selPawn, this);
                    }
                }
            }
        }

        /// <summary>
        /// 弹出容器内的所有内容。
        /// </summary>
        public override void EjectContents()
        {
            foreach (Thing thing in (IEnumerable<Thing>) this.innerContainer)
            {
                if (thing is Pawn pawn)
                {
                    // 确保女仆弹出时恢复必要的组件（如果是之前为了性能被暂时禁用的）
                    PawnComponentsUtility.AddComponentsForSpawn(pawn);
                }
            }
            base.EjectContents();
        }
    }

    /// <summary>
    /// 女仆进入展示柜的任务驱动类。
    /// </summary>
    public class JobDriver_EnterDisplayCase : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            // 预留目标建筑
            return pawn.Reserve(TargetA, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // 目标消失或失效时任务失败
            this.FailOnDespawnedOrNull(TargetIndex.A);
            
            // 移动到展示柜的交互格
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell);
            
            // 执行进入操作
            Toil prepare = new Toil();
            prepare.initAction = delegate
            {
                Pawn actor = prepare.actor;
                Building_ArtificialMaidDisplayCase pod = (Building_ArtificialMaidDisplayCase)actor.CurJob.targetA.Thing;
                if (!pod.HasAnyContents)
                {
                    // 将女仆存入容器
                    actor.DeSpawnOrDeselect();
                    pod.TryAcceptThing(actor);
                }
            };
            prepare.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return prepare;
        }
    }

    /// <summary>
    /// 女仆自动进入休眠的任务提供者。
    /// 该节点通常应放置在思维树（ThinkTree）的空闲部分。
    /// </summary>
    public class JobGiver_ArtificialMaidHibernate : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            // 仅适用于人造人女仆
            if (pawn.def != ArtificialMaidDefOf.ArtificialMaid) return null;

            // 检查女仆自身的自动休眠设置
            CompArtificialMaid comp = CompArtificialMaid.GetCompCached(pawn);
            if (comp != null && !comp.allowAutoHibernate)
            {
                return null;
            }

            // 仅在女仆空闲且有展示柜开启了自动休眠功能时，才寻找并进入展示柜。

            // 寻找最近的可用展示柜
            Building_ArtificialMaidDisplayCase displayCase = (Building_ArtificialMaidDisplayCase)GenClosest.ClosestThingReachable(
                pawn.Position, pawn.Map, 
                ThingRequest.ForDef(ArtificialMaidDefOf.ArtificialMaidDisplayCase), 
                PathEndMode.InteractionCell, 
                TraverseParms.For(pawn), 
                9999f, 
                t => 
                {
                    var dc = (Building_ArtificialMaidDisplayCase)t;
                    // 展示柜必须开启了自动休眠、当前为空且属于同一派系，并且可以被该女仆预留
                    return dc.autoHibernate && !dc.HasAnyContents && dc.Faction == pawn.Faction && pawn.CanReserve(dc);
                }
            );

            if (displayCase != null)
            {
                // 返回进入展示柜的任务
                return JobMaker.MakeJob(ArtificialMaidDefOf.EnterDisplayCase, displayCase);
            }

            return null;
        }
    }
}
