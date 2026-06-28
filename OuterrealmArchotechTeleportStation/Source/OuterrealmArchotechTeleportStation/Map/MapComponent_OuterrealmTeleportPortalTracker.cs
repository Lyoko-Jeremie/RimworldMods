using System.Collections.Generic;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    /// <summary>
    /// 追踪单张地图上已生成的传送门建筑。
    /// 建筑生成、拆除、重装和读档都会经过 SpawnSetup/DeSpawn，因此不需要额外存档。
    /// </summary>
    public class MapComponent_OuterrealmTeleportPortalTracker : MapComponent
    {
        private readonly List<Building_OuterrealmArchotechTeleportPortal> portals =
            new List<Building_OuterrealmArchotechTeleportPortal>();

        public MapComponent_OuterrealmTeleportPortalTracker(Map map) : base(map)
        {
        }

        public void Register(Building_OuterrealmArchotechTeleportPortal portal)
        {
            if (portal == null || portals.Contains(portal))
            {
                return;
            }

            portals.Add(portal);
        }

        public void Unregister(Building_OuterrealmArchotechTeleportPortal portal)
        {
            if (portal == null)
            {
                return;
            }

            portals.Remove(portal);
        }

        public void AppendActiveDestinations(List<Building_OuterrealmArchotechTeleportPortal> result)
        {
            for (int i = portals.Count - 1; i >= 0; i--)
            {
                Building_OuterrealmArchotechTeleportPortal portal = portals[i];
                if (portal == null || portal.Destroyed || !portal.Spawned || portal.Map != map)
                {
                    portals.RemoveAt(i);
                    continue;
                }

                if (portal.CanUseAsTeleportDestination(out _))
                {
                    result.Add(portal);
                }
            }
        }
    }
}
