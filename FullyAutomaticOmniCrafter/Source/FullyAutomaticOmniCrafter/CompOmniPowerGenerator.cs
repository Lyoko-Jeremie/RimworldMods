using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 无限电能发电机。
    /// 有如下几种模式
    /// 1 完全自由调节功率输出。
    /// 2 自动调节功率输出，以绝对满足电网需求（使得电网耗电量等于电网发电量）。
    /// 3 在平衡电网功率的基础上设置一个超出的值，以实现给其他电池充电。
    /// 4 提供无限大的电量（Infinite），使得电网中所有电池都瞬间充满电。（注意，需要避免OmniCrafterSmartInfiniteBattery超载）
    /// </summary>
    public class CompOmniPowerGenerator
    {
        
    }
}
