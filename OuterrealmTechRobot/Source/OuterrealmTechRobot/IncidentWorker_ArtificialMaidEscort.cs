using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace OuterrealmTechRobot
{
    
    public class IncidentWorker_ArtificialMaidEscort: IncidentWorker
    {
        // 检查事件是否可以发生（例如：地图上是否有合法的出生点，是否有中立派系）
        protected override bool CanFireNowSub(IncidentParms parms)
        {
            if (!base.CanFireNowSub(parms)) return false;
            Map map = (Map)parms.target;
            return TryFindFaction(out Faction faction) && RCellFinder.TryFindRandomPawnEntryCell(out IntVec3 cell, map, CellFinder.EdgeRoadChance_Neutral);
        }

        // 事件的核心执行逻辑
        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            Map map = (Map)parms.target;

            // 1. 寻找友方/中立派系和出生点
            if (!TryFindFaction(out Faction faction)) return false;
            if (!RCellFinder.TryFindRandomPawnEntryCell(out IntVec3 spawnSpot, map, CellFinder.EdgeRoadChance_Neutral)) return false;

            // 2. 生成护卫队（点数可以根据你的需求写死或者随殖民地财富变化）
            PawnGroupMakerParms groupParms = new PawnGroupMakerParms
            {
                groupKind = PawnGroupKindDefOf.Peaceful,
                tile = map.Tile,
                faction = faction,
                points = 500f // 500点大概会生成 3-5 个普通守卫
            };
            List<Pawn> escorts = PawnGroupMakerUtility.GeneratePawns(groupParms).ToList();

            // 3. 生成人造人女仆
            PawnKindDef maidKind = PawnKindDef.Named("ArtificialMaidKind");
            PawnGenerationRequest request = new PawnGenerationRequest(
                maidKind, 
                faction, // 初始派系设为护送派系
                PawnGenerationContext.NonPlayer, 
                -1, 
                forceGenerateNewPawn: true
            );
            Pawn maid = PawnGenerator.GeneratePawn(request);

            // 4. 将护卫队投入地图
            foreach (Pawn guard in escorts)
            {
                GenSpawn.Spawn(guard, spawnSpot, map);
            }

            // 5. 将女仆投入地图 
            // 【注意】这一步执行的瞬间，你之前写的 Harmony 补丁（SpawnSetup）就会触发！
            // 她会瞬间变成 Faction.OfPlayer，所以不需要额外给她分配 AI，玩家可以直接控制她。
            GenSpawn.Spawn(maid, spawnSpot, map);

            // 6. 给护卫队分配 AI：让他们访问殖民地，待一段时间后离开
            if (RCellFinder.TryFindRandomSpotJustOutsideColony(escorts[0], out IntVec3 chillSpot))
            {
                // 让他们去基地外围逛逛
                LordJob_VisitColony lordJob = new LordJob_VisitColony(faction, chillSpot);
                LordMaker.MakeNewLord(faction, lordJob, map, escorts);
            }

            // 7. 发送事件信件给玩家
            string text = $"一支来自 {faction.Name} 的护卫队抵达了。\n\n按照约定（或是巧合），他们带来了一名无主的人造人女仆。当女仆踏入这片土地的瞬间，她的底层协议已自动将您的殖民地识别为最高优先级。她现在归您指挥了。";
            SendStandardLetter("女仆护送队", text, LetterDefOf.PositiveEvent, parms, new TargetInfo(spawnSpot, map));

            return true;
        }

        // 辅助方法：寻找一个既不是玩家，也不敌对的可见派系
        private bool TryFindFaction(out Faction faction)
        {
            return Find.FactionManager.AllFactions
                .Where(f => !f.IsPlayer && !f.HostileTo(Faction.OfPlayer) && !f.Hidden)
                .TryRandomElement(out faction);
        }
    }
}