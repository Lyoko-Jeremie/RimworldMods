using System;

namespace Jeremie.AtmosphereOverhaulPatch.ProxyHeat
{
    /// <summary>
    /// 逐建筑的范围效果通道。
    ///
    /// 三个模式互不排斥，可以同时开：
    /// - 空气：把目标温差注入 AO 的气温场；
    /// - 地暖：注入 AO 的地温场；
    /// - 喷流：沿建筑朝向做短距离定向注入。
    /// </summary>
    [Flags]
    public enum ProxyHeatMode
    {
        None = 0,
        Air = 1,
        Soil = 2,
        Jet = 4,
        All = Air | Soil | Jet,
    }
}
