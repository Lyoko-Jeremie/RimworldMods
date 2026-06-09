using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public class CompProperties_CompBiosphere : CompProperties
    {
        public CompProperties_CompBiosphere()
        {
            this.compClass = typeof(CompBiosphere);
        }
    }

    [StaticConstructorOnStartup]
    public static class CompBiosphereTex
    {
    }
    
    /// <summary>
    /// 生物圈控制组件
    /// 通过选择生效的活动区（而不是房间），可以对选定的区域进行以下的控制：
    /// 1 活动区生长控制
    ///     * 强制让所有植物生长进度到至少100%
    ///     * 强制停止所有植物的生长，保持当前生长进度
    ///     * 强制清除所有植物，并阻止任何植物在该区域生长
    /// 2 温度控制
    ///     * 强制将活动区内的温度保持在一个选定的值（使用类似篝火的周围加热区）
    /// 3 强制确保活动区内无真空，无论是否有房间
    /// 4 强制让活动区内与照明，无论是否有房顶
    /// 5 强制让活动区内有阳光，无论是否有房顶
    /// </summary>
    public class CompBiosphere : ThingComp
    {
        
    }
}