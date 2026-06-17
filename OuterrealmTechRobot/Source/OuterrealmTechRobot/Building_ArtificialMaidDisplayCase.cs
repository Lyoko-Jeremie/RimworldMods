using System;
using System.Collections.Generic;
using System.Linq;
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
    public class Building_ArtificialMaidDisplayCase : Building_Casket
    {
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

        protected override void Tick()
        {
            base.Tick();
            // 每 250 Tick（约 4 秒）检查一次是否需要自动唤醒
            if (this.IsHashIntervalTick(250))
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
        /// 检测当前地图上是否有女仆可执行的工作。
        /// </summary>
        private bool AnyJobFor(Pawn pawn)
        {
            if (pawn.MapHeld == null) return false;

            // 临时设置女仆位置到柜子处，以便思维树能正确检索附近的工作（很多 JobGiver 依赖位置）
            IntVec3 oldPos = pawn.Position;
            pawn.SetPositionDirect(this.Position);

            try
            {
                // 我们使用女仆的思维树来查看她是否会执行除“等待”或“漫步”之外的任何动作。
                // 注意：我们使用的是 MainThinkNodeRoot，它包含了所有的工作提供者（WorkGiver）。
                ThinkResult thinkResult = pawn.thinker.MainThinkNodeRoot.TryIssueJobPackage(pawn, new JobIssueParams());
                
                if (thinkResult.IsValid && thinkResult.Job != null)
                {
                    JobDef def = thinkResult.Job.def;
                    // 检查这是否是一个“真实的”工作（排除空闲和进入展示柜的任务）
                    if (def != JobDefOf.Wait && 
                        def != JobDefOf.Wait_MaintainPosture && 
                        def != JobDefOf.Wait_SafeTemperature && 
                        def != JobDefOf.Wait_Wander &&
                        !def.defName.Contains("Wander") &&
                        (ArtificialMaidDefOf.EnterDisplayCase == null || def != ArtificialMaidDefOf.EnterDisplayCase))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                // 如果女仆处于无效状态，寻找工作可能会抛出错误，虽然在这里不太可能发生。
                Log.ErrorOnce("Error during auto-wake job search for " + pawn.LabelShort + ": " + ex.Message, 6654321);
            }
            finally
            {
                // 无论如何都要恢复位置
                pawn.SetPositionDirect(oldPos);
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
            // 仅在 Draw 阶段且柜内有物时绘制
            if (phase == DrawPhase.Draw && HasAnyContents)
            {
                Pawn pawn = ContainedThing as Pawn;
                if (pawn != null)
                {
                    Vector3 pawnDrawLoc = drawLoc;
                    pawnDrawLoc.y += 0.04054054f; // 将绘制层级稍微抬高，使其显示在建筑上方
                    
                    // 调用女仆渲染器的 DynamicDrawPhaseAt 来进行绘制
                    pawn.Drawer.renderer.DynamicDrawPhaseAt(phase, pawnDrawLoc, new Rot4?(Rot4.South), true);
                }
            }
        }

        /// <summary>
        /// 获取建筑的操作按钮（Gizmos）。
        /// </summary>
        public override IEnumerable<Gizmo> GetGizmos()
        {
            // 返回父类的默认按钮（如拆除等）
            foreach (var g in base.GetGizmos()) yield return g;

            // 自动休眠切换开关
            yield return new Command_Toggle
            {
                defaultLabel = "AutoHibernateLabel".Translate(),
                defaultDesc = "AutoHibernateDesc".Translate(),
                isActive = () => autoHibernate,
                toggleAction = () => autoHibernate = !autoHibernate,
                icon = ContentFinder<Texture2D>.Get("UI/Commands/AutoHibernate", false) ?? BaseContent.WhiteTex
            };

            // 自动唤醒切换开关
            yield return new Command_Toggle
            {
                defaultLabel = "AutoWakeLabel".Translate(),
                defaultDesc = "AutoWakeDesc".Translate(),
                isActive = () => autoWake,
                toggleAction = () => autoWake = !autoWake,
                icon = ContentFinder<Texture2D>.Get("UI/Commands/AutoWake", false) ?? BaseContent.WhiteTex
            };
            
            // 如果柜内有物且属于玩家派系，显示手动弹出按钮
            if (HasAnyContents && Faction == Faction.OfPlayer)
            {
                yield return new Command_Action
                {
                    defaultLabel = "CommandPodEject".Translate(),
                    defaultDesc = "CommandPodEjectDesc".Translate(),
                    icon = ContentFinder<Texture2D>.Get("UI/Commands/PodEject"),
                    action = () => EjectContents()
                };
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
                    // 展示柜必须开启了自动休眠、当前为空且属于同一派系
                    return dc.autoHibernate && !dc.HasAnyContents && dc.Faction == pawn.Faction;
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
