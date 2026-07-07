using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace OuterrealmTechRobot
{
    public class IncidentWorker_ArtificialMaidEscort: IncidentWorker_NeutralGroup
    {
        // 检查事件是否可以发生，额外确认地图边缘有可用入口。
        protected override bool CanFireNowSub(IncidentParms parms)
        {
            if (!base.CanFireNowSub(parms)) return false;
            Map map = (Map)parms.target;
            return parms.spawnCenter.IsValid ||
                   RCellFinder.TryFindRandomPawnEntryCell(out IntVec3 _, map, CellFinder.EdgeRoadChance_Neutral);
        }

        // 事件的核心执行逻辑
        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            Map map = (Map)parms.target;

            // 1. 按原版中立访问者逻辑寻找派系和出生点
            if (!TryResolveParms(parms)) return false;

            // 2. 生成护卫队
            List<Pawn> escorts = SpawnPawns(parms);
            if (escorts.Count == 0) return false;

            // 3. 生成人造人女仆
            PawnKindDef maidKind = PawnKindDef.Named("ArtificialMaidKind");
            PawnGenerationRequest request = new PawnGenerationRequest(
                maidKind,
                parms.faction, // 初始派系设为护送派系
                PawnGenerationContext.NonPlayer,
                map.Tile,
                forceGenerateNewPawn: true
            );
            Pawn maid = PawnGenerator.GeneratePawn(request);

            // 4. 将女仆投入地图
            // 【注意】这一步执行的瞬间，你之前写的 Harmony 补丁（SpawnSetup）就会触发！
            // 她会瞬间变成 Faction.OfPlayer，所以不需要额外给她分配 AI，玩家可以直接控制她。
            IntVec3 maidSpawnSpot = CellFinder.RandomClosewalkCellNear(parms.spawnCenter, map, 5);
            GenSpawn.Spawn(maid, maidSpawnSpot, map);

            // 5. 给护卫队分配 AI：让他们访问殖民地，待一段时间后离开
            if (!RCellFinder.TryFindRandomSpotJustOutsideColony(escorts[0], out IntVec3 chillSpot))
            {
                chillSpot = parms.spawnCenter;
            }

            // 让他们去基地外围逛逛
            Pawn leader = FindEscortLeader(escorts);
            LordJob_ArtificialMaidEscortVisit lordJob = new LordJob_ArtificialMaidEscortVisit(parms.faction, chillSpot, leader);
            LordMaker.MakeNewLord(parms.faction, lordJob, map, escorts);

            // 6. 发送事件信件给玩家
            TaggedString text = "ArtificialMaidEscortLetterText".Translate(parms.faction.Name);
            SendStandardLetter("ArtificialMaidEscortLetterLabel".Translate(), text, LetterDefOf.PositiveEvent, parms, new TargetInfo(maidSpawnSpot, map));

            return true;
        }

        // 护卫队规模固定为一次小型中立访问队。
        protected override void ResolveParmsPoints(IncidentParms parms)
        {
            if (parms.points < 0f)
            {
                parms.points = 500f;
            }
        }

        // 优先选择成年可交流的人形单位作为护卫队领队。
        private static Pawn FindEscortLeader(List<Pawn> escorts)
        {
            for (int i = 0; i < escorts.Count; i++)
            {
                Pawn pawn = escorts[i];
                if (pawn.RaceProps.Humanlike && pawn.DevelopmentalStage.Adult())
                {
                    return pawn;
                }
            }

            return escorts[0];
        }
    }
}
