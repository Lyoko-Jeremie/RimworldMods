using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    
    /// <summary>
    /// TODO
    /// 自动捕捉器
    /// 能够自动捕捉地图上所有的指定类型的pawn（不包括当前所在的room区域），将其移动到建筑当前所在的room区域中（包括建筑本身所在的格子）
    /// 用于实现自动捕捉功能，包括将敌人捕捉到KillBox，或是将动物捕捉到动物圈养区，将囚犯捕捉待监狱，将攻击者从基地中传送到基地外，等等
    ///
    /// 为用户提供一个设置界面，设置需要捕获的Pawn条件（参考 Designator_PhantomWall2Passability ），以及捕捉后的附加效果（例如：击晕、转化为囚犯/驯服状态等）
    /// 设置界面分左中右三栏，左栏为Pawn条件选择，中栏为预览哪些Pawn现在被筛选并会被捕获，右栏为捕获后的附加效果选择
    ///
    /// 提供三个Gizmo按钮，一个是打开设置界面，一个是开启/关闭自动捕捉功能，一个是手动执行一次捕捉（不受自动捕捉开关影响）
    ///
    /// 将建筑设计为 无敌的，无法被攻击或破坏（可以参考 Building_OmniPhantomWall2 的无敌判定部分代码），以保证捕捉功能的稳定性和可靠性
    /// </summary>
    public class CompAutomatedCapturer : ThingComp
    {
        // 玩家选中的目标类型（示例：以 PawnKindDef 为准）
        public PawnKindDef targetKind; 
        public bool isActive = true;

        public override void CompTickRare()
        {
            base.CompTickRare();
            if (!isActive || targetKind == null || !parent.Spawned || parent.Map == null) return;

            Map map = parent.Map;
        
            // 1. 获取建筑本身所占用的格子（假设建筑本身是可站立或可通过的）
            
            // 2. 扫描地图上所有符合条件的 Pawn
            var targets = map.mapPawns.AllPawnsSpawned
                .Where(p => p.KindDef == targetKind && IsValidTarget(p));

            foreach (var pawn in targets)
            {
                // 3. 放入可用格
            }
        }

        private bool IsValidTarget(Pawn p)
        {
            // 过滤掉已经在外围圈内的、死亡的、或者处于世界图层的 Pawn
            return !p.Dead && p.Spawned/* && TODO 不在建筑所在的区域的（需要考虑建筑不在任何房间里的情况（在地图上）） */;
        }

        private void ExecuteCapture(Pawn pawn, IntVec3 targetCell)
        {
            Map map = parent.Map;
            // 此函数需要注意异常的捕获和处理
        
            // 安全的传送方式：先解除生成，再重新生成（可避免很多底层位置判定 Bug）
            pawn.DeSpawn();
            GenSpawn.Spawn(pawn, targetCell, map);
        
            // 视觉与音效特效

            // 根据用户设置，执行不同的捕捉效果（例如：击晕、转化为囚犯/驯服状态等）
        }
    }
}
