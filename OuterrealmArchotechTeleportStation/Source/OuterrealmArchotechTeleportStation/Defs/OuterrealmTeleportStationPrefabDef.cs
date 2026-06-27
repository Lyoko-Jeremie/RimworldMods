using System.Collections.Generic;
using RimWorld;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    /// <summary>
    /// 对原版 <see cref="PrefabDef"/> 的轻量包装。
    /// 原版 PrefabDef 只描述地形和物件本身，不知道“哪个格子是主传送门”或“玩家应从哪里进入”。
    /// 这些传送站专用语义放在此 Def 中，方便 XML 侧扩展多种布局。
    /// </summary>
    public class OuterrealmTeleportStationPrefabDef : Def
    {
        /// <summary>
        /// 实际要生成到地图中的原版预制体。
        /// </summary>
        public PrefabDef prefab;

        /// <summary>
        /// 随机选择该布局时使用的权重。
        /// </summary>
        public float weight = 1f;

        /// <summary>
        /// 主传送门在 prefab 根坐标下的二维偏移。
        /// 如果 prefab 生成后没有记录 spawnedThings，可用它反查主建筑。
        /// </summary>
        public IntVec2 portalOffset = IntVec2.Invalid;

        /// <summary>
        /// 玩家进入传送站地图后的推荐出生点偏移。
        /// 生成器会再次校验该格是否可站立，不可用时会回退到传送门附近可站立格。
        /// </summary>
        public IntVec2 playerStartOffset = IntVec2.Invalid;

        /// <summary>
        /// 预留给后续支持随机旋转布局使用；当前第一版生成器使用原版 PrefabUtility 校验 North 方向。
        /// </summary>
        public RotEnum allowedRotations = RotEnum.All;

        /// <summary>
        /// 标记该布局可作为保底布局。当前 C# 兜底逻辑会直接手工生成平台，
        /// 该字段保留给后续“从 XML fallback prefab 回退”的实现。
        /// </summary>
        public bool fallback;

        /// <summary>
        /// 允许生成此布局的生态群系白名单；为空表示不限制。
        /// </summary>
        public List<BiomeDef> allowedBiomes;

        /// <summary>
        /// 禁止生成此布局的生态群系黑名单。
        /// </summary>
        public List<BiomeDef> disallowedBiomes;

        /// <summary>
        /// 检查当前地图生态群系是否允许使用该布局。
        /// 白名单优先限制可用范围，黑名单用于排除少量特殊生态。
        /// </summary>
        public bool AllowsBiome(BiomeDef biome)
        {
            if (biome == null)
            {
                // 没有生态信息时不阻断生成，避免极端地图/兼容 Mod 导致传送站无法生成。
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
