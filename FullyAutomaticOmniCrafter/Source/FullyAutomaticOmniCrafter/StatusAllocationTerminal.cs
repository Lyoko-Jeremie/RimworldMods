using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace FullyAutomaticOmniCrafter
{
    public class CompProperties_StatusAllocationTerminal : CompProperties
    {
        public CompProperties_StatusAllocationTerminal()
        {
            this.compClass = typeof(StatusAllocationTerminal);
        }
    }
    
    [StaticConstructorOnStartup]
    public static class StatusAllocationTerminalTex
    {
        public static readonly Texture2D IconOpenDialog =
            ContentFinder<Texture2D>.Get("UI/Commands/StatusAllocationTerminal_OpenDialog", true) ?? BaseContent.WhiteTex;
    }
    
    /// <summary>
    /// 一个可以手动为我方殖民者调整hediff的建筑
    /// 有两个功能模式：
    /// 1 自动光环模式
    /// 2 手动赋予移除模式
    ///
    /// 手动赋予移除模式：
    /// 建筑弹出一个菜单界面，并在菜单上为指定的我方pawn手动添加或移除状态，使得pawn即使离开地图也有这个这状态
    /// 从左到右ABCD四栏，A是pawn名字和头像，B是选中的pawn的当前状态列表（像原版一样以部位分组），C是状态模板，D是可用状态列表。
    /// 从D中选择状态，点击每个状态右侧的按钮弹出部位的下拉菜单，选择部位添加到C中，C中以部位为组分组显示，双击C中的条目移除状态。
    ///
    /// 自动光环模式：
    /// 目的：设置赋予模板（包括hediff和对于身体部位）和移除列表，自动对当前地图上的我方人员和动物及机器人和race进行自动赋予和自动移除。
    /// 建筑弹出一个菜单界面，在界面上设置自动光环模式的赋予参数，以及自动移除列表。
    /// 从左到右分ABC三栏，A是自动赋予模板列表（像原版一样以部位分组），B是自动移除列表，C是可选状态列表，C中每个条目尾部有两个按钮，分别用于添加和移除状态，添加按钮点击同样弹出部位的下拉菜单，选择部位添加到C中。
    /// 
    /// </summary>
    public class StatusAllocationTerminal : ThingComp
    {
        public CompProperties_StatusAllocationTerminal Props => (CompProperties_StatusAllocationTerminal)this.props;

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
            {
                yield return g;
            }

            if (parent.Faction == Faction.OfPlayer)
            {
                yield return new Command_Action
                {
                    defaultLabel = "管理人员状态",
                    defaultDesc = "打开终端面板，为我方人员分配或移除状态。",
                    icon = StatusAllocationTerminalTex.IconOpenDialog,
                    action = delegate ()
                    {
                        // 点击按钮后，打开我们自定义的 UI 窗口
                        Find.WindowStack.Add(new Dialog_StatusAllocationTerminal(parent.Map));
                    }
                };
            }
        }
    }
    
    // 3. 自定义 UI 窗口类 (绘制人员列表)
    public class Dialog_StatusAllocationTerminal : Window
    {
        private Map map;
        private Vector2 scrollPosition = Vector2.zero;

        // 窗口初始化设置
        public Dialog_StatusAllocationTerminal(Map map)
        {
            this.map = map;
            this.doCloseX = true;           // 右上角关闭按钮
            this.doCloseButton = true;      // 底部关闭按钮
            this.forcePause = true;         // 打开时暂停游戏
            this.absorbInputAroundWindow = true;
        }

        // 定义窗口大小
        public override Vector2 InitialSize => new Vector2(500f, 600f);

        // 核心：绘制窗口内容
        public override void DoWindowContents(Rect inRect)
        {
            // 标题
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 35f), "状态人员管理");
            Text.Font = GameFont.Small;

            // 获取地图上所有我方 Pawn (包括小人、机器人、动物等)
            List<Pawn> playerPawns = map.mapPawns.AllPawnsSpawned
                .Where(p => p.Faction == Faction.OfPlayer && !p.Dead)
                .ToList();

            if (playerPawns.NullOrEmpty())
            {
                Widgets.Label(new Rect(0f, 40f, inRect.width, 30f), "未找到我方单位。");
                return;
            }

            // 设置滚动视图区域
            Rect outRect = new Rect(0f, 40f, inRect.width, inRect.height - 100f);
            Rect viewRect = new Rect(0f, 0f, inRect.width - 16f, playerPawns.Count * 40f);

            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);

            float y = 0f;
            foreach (Pawn pawn in playerPawns)
            {
                Rect rowRect = new Rect(0f, y, viewRect.width, 35f);
                
                // 画背景交替色增加可读性
                if (playerPawns.IndexOf(pawn) % 2 == 0)
                {
                    Widgets.DrawAltRect(rowRect);
                }

                // 画头像
                Rect portraitRect = new Rect(0f, y, 35f, 35f);
                GUI.DrawTexture(portraitRect, PortraitsCache.Get(pawn, new Vector2(35f, 35f), Rot4.South));

                // 画名字
                Rect nameRect = new Rect(45f, y + 5f, 200f, 30f);
                Widgets.Label(nameRect, pawn.Name?.ToStringShort ?? pawn.LabelShort);

                // 检查是否已经拥有该 Hediff
                Hediff existingHediff = pawn.health.hediffSet.GetFirstHediffOfDef(hediffDef);
                bool hasBuff = existingHediff != null;

                // 画操作按钮
                Rect buttonRect = new Rect(viewRect.width - 100f, y + 5f, 90f, 25f);
                if (hasBuff)
                {
                    if (Widgets.ButtonText(buttonRect, "移除状态"))
                    {
                        pawn.health.RemoveHediff(existingHediff);
                        SoundDefOf.Tick_Low.PlayOneShotOnCamera(); // 播放UI音效
                    }
                }
                else
                {
                    if (Widgets.ButtonText(buttonRect, "添加状态"))
                    {
                        Hediff newHediff = HediffMaker.MakeHediff(hediffDef, pawn, null);
                        pawn.health.AddHediff(newHediff, null, null, null);
                        SoundDefOf.Tick_High.PlayOneShotOnCamera();
                    }
                }

                y += 40f;
            }

            Widgets.EndScrollView();
        }
    }
}