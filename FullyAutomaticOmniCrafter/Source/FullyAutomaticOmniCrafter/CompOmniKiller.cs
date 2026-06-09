using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public class CompProperties_CompOmniKiller : CompProperties
    {
        public CompProperties_CompOmniKiller()
        {
            // 绑定对应的 ThingComp 类
            this.compClass = typeof(CompOmniKiller);
        }
    }

    [StaticConstructorOnStartup]
    public static class CompOmniKillerTex
    {
        public static readonly Texture2D IconKillerUI =
            ContentFinder<Texture2D>.Get("UI/Commands/IconKillerUI", true) ?? BaseContent.WhiteTex;
    }
    
    /// <summary>
    /// 一个防御建筑，通过破坏敌人所有身体部件，堆叠所有负面hediff，并kill杀死敌人的方式来杀死敌人。
    /// 提供一个控制界面，可以按照筛选条件列出地图上的所有pawn，并添加到处死列表中，来杀死指定的pawn。
    /// 这个控制界面由左中左中右右四个栏组成，左栏是筛选条件，中左栏是筛选出的pawn列表，中右栏是处死列表，右栏是操作栏。
    /// 双击可以将选中的pawn添加到处死列表中，或从处死列表中移除。
    /// 筛选功能参见 OmniPhantomWall2_PassabilitySettings、OmniAutoSurgeonSurgery 的筛选条件，需要附加拼音搜索
    /// 操作栏包括如下的几个功能按钮：
    /// 1 施加 +Infinity 点 damage
    /// 2 剥夺身上的所有可剥夺的任何物品，包括穿戴、武器、防具、药剂、食物、资源等。放置在对象周边的地上。
    /// 3 摘取所有可以摘取的器官和身体部件，放置在对象周边的地上。
    /// 4 直接对对象使用 kill 指令
    /// 5 将所有负面hediff堆叠到对象身上，并且将所有正面hediff移除
    /// </summary>
    public class CompOmniKiller : ThingComp
    {
        public OmniPhantomWall2_PassabilitySettings filterSettings = new OmniPhantomWall2_PassabilitySettings();

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Deep.Look(ref filterSettings, "filterSettings");
            if (filterSettings == null)
            {
                filterSettings = new OmniPhantomWall2_PassabilitySettings();
            }
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            yield return new Command_Action
            {
                defaultLabel = "OpenOmniKillerUI".Translate(),
                defaultDesc = "OpenOmniKillerUIDesc".Translate(),
                icon = CompOmniKillerTex.IconKillerUI,
                action = () => Find.WindowStack.Add(new Dialog_CompOmniKiller(this))
            };
        }
    }
}