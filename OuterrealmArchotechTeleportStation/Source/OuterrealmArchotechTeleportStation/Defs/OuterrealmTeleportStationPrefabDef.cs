using System.Collections.Generic;
using RimWorld;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    public class OuterrealmTeleportStationPrefabDef : Def
    {
        public PrefabDef prefab;
        public float weight = 1f;
        public IntVec2 portalOffset = IntVec2.Invalid;
        public IntVec2 playerStartOffset = IntVec2.Invalid;
        public RotEnum allowedRotations = RotEnum.All;
        public bool fallback;
        public List<BiomeDef> allowedBiomes;
        public List<BiomeDef> disallowedBiomes;

        public bool AllowsBiome(BiomeDef biome)
        {
            if (biome == null)
            {
                return true;
            }

            if (!allowedBiomes.NullOrEmpty() && !allowedBiomes.Contains(biome))
            {
                return false;
            }

            return disallowedBiomes.NullOrEmpty() || !disallowedBiomes.Contains(biome);
        }
    }
}
