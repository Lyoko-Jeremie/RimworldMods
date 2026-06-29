using System.Collections;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace OuterrealmTechRoadProject.World
{
    /// <summary>
    /// 专门绘制水面上的超维链路。
    /// 原版 <see cref="WorldDrawLayer_Roads"/> 在生成道路 mesh 时会跳过 WaterCovered tile，
    /// 因此直接写入 <see cref="SurfaceTile.potentialRoads"/> 的海上道路虽然可以参与寻路，
    /// 却不会出现在世界地图上。本图层只补绘水面 tile 上的超维链路，陆地道路仍由原版图层绘制。
    /// </summary>
    public class WorldDrawLayer_OuterrealmLinkOnWater : WorldDrawLayer_Paths
    {
        /// <summary>
        /// 与原版道路图层一致的轻微扰动，让超维链路在世界球面上不会显得完全机械。
        /// 这些噪声模块是实例字段，避免在多层星球或重建图层时共享可变状态。
        /// </summary>
        private readonly ModuleBase roadDisplacementX = new Perlin(1.0, 2.0, 0.5, 3, 74173887, Verse.Noise.QualityMode.Medium);

        private readonly ModuleBase roadDisplacementY = new Perlin(1.0, 2.0, 0.5, 3, 67515931, Verse.Noise.QualityMode.Medium);

        private readonly ModuleBase roadDisplacementZ = new Perlin(1.0, 2.0, 0.5, 3, 87116801, Verse.Noise.QualityMode.Medium);

        /// <summary>
        /// 复用的节点列表，避免每个 tile 都分配新的 List。
        /// Regenerate 在主线程执行，不需要为这个实例字段加锁。
        /// </summary>
        private readonly List<OutputDirection> nodes = new List<OutputDirection>();

        public override bool VisibleWhenLayerNotSelected => false;

        public override bool VisibleInBackground => false;

        /// <summary>
        /// 重新生成水面超维链路 mesh。
        /// 逻辑基本等价于原版道路图层，但只处理 WaterCovered tile，且只收集超维链路 RoadDef。
        /// </summary>
        public override IEnumerable Regenerate()
        {
            foreach (object item in base.Regenerate())
            {
                yield return item;
            }

            LayerSubMesh subMesh = GetSubMesh(WorldMaterials.Roads);
            List<RoadWorldLayerDef> roadLayerDefs = DefDatabase<RoadWorldLayerDef>.AllDefs
                .OrderBy(def => def.order)
                .ToList();

            for (int tileIndex = 0; tileIndex < planetLayer.TilesCount; tileIndex++)
            {
                if (tileIndex % 1000 == 0)
                {
                    yield return null;
                }

                if (subMesh.verts.Count > 60000)
                {
                    subMesh = GetSubMesh(WorldMaterials.Roads);
                }

                SurfaceTile surfaceTile = planetLayer[tileIndex] as SurfaceTile;
                if (surfaceTile == null || !surfaceTile.WaterCovered || surfaceTile.potentialRoads == null)
                {
                    continue;
                }

                PlanetTile tile = new PlanetTile(tileIndex, planetLayer);
                bool allowSmoothTransition = AllowsSmoothTransition(surfaceTile.potentialRoads);
                for (int layerIndex = 0; layerIndex < roadLayerDefs.Count; layerIndex++)
                {
                    RoadWorldLayerDef layerDef = roadLayerDefs[layerIndex];
                    bool hasVisibleNode = false;
                    nodes.Clear();

                    // 每个 RoadWorldLayerDef 单独生成一遍节点，才能得到 RoadDef 中 outline/glow 等多层宽度。
                    for (int roadIndex = 0; roadIndex < surfaceTile.potentialRoads.Count; roadIndex++)
                    {
                        SurfaceTile.RoadLink roadLink = surfaceTile.potentialRoads[roadIndex];
                        RoadDef road = roadLink.road;
                        if (!OuterrealmLinkUtility.IsOuterrealmLinkRoad(road))
                        {
                            continue;
                        }

                        float layerWidth = road.GetLayerWidth(layerDef);
                        if (layerWidth > 0f)
                        {
                            hasVisibleNode = true;
                        }

                        nodes.Add(new OutputDirection
                        {
                            neighbor = roadLink.neighbor,
                            width = layerWidth,
                            distortionFrequency = road.distortionFrequency,
                            distortionIntensity = road.distortionIntensity
                        });
                    }

                    if (hasVisibleNode)
                    {
                        GeneratePaths(subMesh, tile, nodes, layerDef.color, allowSmoothTransition);
                    }
                }
            }

            FinalizeMesh(MeshParts.All);
        }

        /// <summary>
        /// 如果同一 tile 上存在不同过渡组，按原版规则关闭平滑过渡，避免不同道路类型在中心处错误融合。
        /// 这里虽然目前只有一种超维链路，但保留该判断可以兼容之后增加同类 RoadDef。
        /// </summary>
        private static bool AllowsSmoothTransition(List<SurfaceTile.RoadLink> roadLinks)
        {
            RoadDef previousRoad = null;
            for (int i = 0; i < roadLinks.Count; i++)
            {
                RoadDef road = roadLinks[i].road;
                if (!OuterrealmLinkUtility.IsOuterrealmLinkRoad(road))
                {
                    continue;
                }

                if (previousRoad != null && previousRoad.worldTransitionGroup != road.worldTransitionGroup)
                {
                    return false;
                }

                previousRoad = road;
            }

            return true;
        }

        /// <summary>
        /// 将路径点抬离星球表面并施加轻微噪声。
        /// 抬高量比原版道路略大，避免水面 mesh 在某些观察角度盖住海上超维链路。
        /// </summary>
        public override Vector3 FinalizePoint(Vector3 inp, float distortionFrequency, float distortionIntensity)
        {
            Vector3 coordinate = inp * distortionFrequency;
            float magnitude = inp.magnitude;
            Vector3 displacement = new Vector3(
                roadDisplacementX.GetValue(coordinate),
                roadDisplacementY.GetValue(coordinate),
                roadDisplacementZ.GetValue(coordinate));

            if (displacement.magnitude > 0.0001f)
            {
                float strength = (1f / (1f + Mathf.Exp(-(displacement.magnitude / 1f) * 2f)) * 2f - 1f) * 1f;
                displacement = displacement.normalized * strength;
            }

            inp = (inp + displacement * distortionIntensity).normalized * magnitude;
            return inp + inp.normalized * 0.045f;
        }
    }
}
