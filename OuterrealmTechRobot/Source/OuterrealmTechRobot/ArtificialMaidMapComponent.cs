using System.Collections.Generic;
using Verse;

namespace OuterrealmTechRobot
{
    public class ArtificialMaidMapComponent : MapComponent
    {
        private HashSet<Pawn> registeredMaids = new HashSet<Pawn>();

        public int MaidCount => registeredMaids.Count;

        public ArtificialMaidMapComponent(Map map) : base(map)
        {
        }

        public void RegisterMaid(Pawn pawn)
        {
            if (pawn != null && !registeredMaids.Contains(pawn))
            {
                registeredMaids.Add(pawn);
            }
        }

        public void UnregisterMaid(Pawn pawn)
        {
            if (pawn != null && registeredMaids.Contains(pawn))
            {
                registeredMaids.Remove(pawn);
            }
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();

            // 每 2000 tick (约 33 秒) 进行一次深度清理和同步，保证数据准确
            if (Find.TickManager.TicksGame % 2000 == 0)
            {
                // 1. 清理无效引用
                registeredMaids.RemoveWhere(p => p == null || !p.Spawned || p.Map != this.map || p.Dead);

                // 2. 补充漏掉的注册（例如中途加载或特殊生成逻辑）
                var allPawns = this.map.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < allPawns.Count; i++)
                {
                    Pawn p = allPawns[i];
                    if (p.def == ArtificialMaidDefOf.ArtificialMaid && !registeredMaids.Contains(p))
                    {
                        RegisterMaid(p);
                    }
                }
            }
        }

        public override void MapRemoved()
        {
            base.MapRemoved();
            registeredMaids.Clear();
        }
    }
}
