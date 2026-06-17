using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Verse;

namespace OuterrealmTechRobot
{
    public class ArtificialMaidMapComponent : MapComponent
    {
        private static readonly ConditionalWeakTable<Map, ArtificialMaidMapComponent> comps = new ConditionalWeakTable<Map, ArtificialMaidMapComponent>();

        public static ArtificialMaidMapComponent Get(Map map)
        {
            if (map == null) return null;
            comps.TryGetValue(map, out var comp);
            return comp;
        }

        private HashSet<Pawn> registeredMaids = new HashSet<Pawn>();
        private HashSet<Building_ArtificialMaidDisplayCase> registeredDisplayCases = new HashSet<Building_ArtificialMaidDisplayCase>();

        public int MaidCount => registeredMaids.Count;
        public int DisplayCaseCount => registeredDisplayCases.Count;

        public ArtificialMaidMapComponent(Map map) : base(map)
        {
            comps.Remove(map);
            comps.Add(map, this);
        }

        public void RegisterMaid(Pawn pawn)
        {
            if (pawn != null)
            {
                registeredMaids.Add(pawn);
            }
        }

        public void UnregisterMaid(Pawn pawn)
        {
            if (pawn != null)
            {
                registeredMaids.Remove(pawn);
            }
        }

        public void RegisterDisplayCase(Building_ArtificialMaidDisplayCase building)
        {
            if (building != null)
            {
                registeredDisplayCases.Add(building);
            }
        }

        public void UnregisterDisplayCase(Building_ArtificialMaidDisplayCase building)
        {
            if (building != null)
            {
                registeredDisplayCases.Remove(building);
            }
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();

            // 每 2000 tick (约 33 秒) 进行一次深度清理和同步，保证数据准确
            if (Find.TickManager.TicksGame % 2000 == 0)
            {
                // 1. 清理女仆引用
                registeredMaids.RemoveWhere(p => p == null || !p.Spawned || p.Map != this.map || p.Dead);

                // 2. 清理展示柜引用
                registeredDisplayCases.RemoveWhere(b => b == null || !b.Spawned || b.Map != this.map);

                // 3. 补充漏掉的女仆注册
                var allPawns = this.map.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < allPawns.Count; i++)
                {
                    Pawn p = allPawns[i];
                    if (p.def == ArtificialMaidDefOf.ArtificialMaid)
                    {
                        RegisterMaid(p);
                    }
                }
            }
        }

        public override void MapRemoved()
        {
            base.MapRemoved();
            comps.Remove(this.map);
            registeredMaids.Clear();
            registeredDisplayCases.Clear();
        }
    }
}
