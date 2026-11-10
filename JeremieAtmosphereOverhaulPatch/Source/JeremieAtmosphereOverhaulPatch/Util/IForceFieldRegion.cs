using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Util
{
    /// <summary>
    /// 「强制稳定区」的抽象。生物圈、穹顶发生器这类建筑把自身覆盖范围声明为该区域，
    /// 由 <see cref="ForceFieldStabilizer"/> 周期性把区域内的 AO 物理场拉回目标值。
    ///
    /// 实现方负责：缓存自己的格子列表（不要在每次访问时重新枚举 Area），
    /// 并在 Active 为 false 时让 <see cref="Cells"/> 返回空列表。
    /// </summary>
    public interface IForceFieldRegion
    {
        /// <summary>用于日志的名字。</summary>
        string RegionName { get; }

        /// <summary>是否应当生效（建筑已生成、已通电等）。</summary>
        bool Active { get; }

        /// <summary>当前覆盖的格子（缓存列表，允许被调用方只读遍历）。</summary>
        System.Collections.Generic.List<IntVec3> Cells { get; }

        /// <summary>区域外接矩形；有值时大区域可以走批量写入。</summary>
        CellRect? BulkRect { get; }

        /// <summary>目标温度（摄氏度）。</summary>
        float TargetTemperature { get; }

        /// <summary>是否把气压拉回常压。</summary>
        bool StabilizePressure { get; }

        /// <summary>是否把湿度拉回一个安全区间。</summary>
        bool StabilizeHumidity { get; }

        /// <summary>是否尝试消除区域内的风。（逐格写，代价较高，默认关闭。）</summary>
        bool KillWind { get; }
    }
}
