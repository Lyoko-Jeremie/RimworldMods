using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    
    /// <summary>
    /// 负责合并所有相邻的立场穹顶，将相邻或重叠的立场穹顶合并产生一个大的房间区域
    /// </summary>
    public class OmniForceFieldDomeNetworkManager : MapComponent
    {
        public OmniForceFieldDomeNetworkManager(Map map) : base(map)
        {
        }
    }
    
    public class CompProperties_OmniForceFieldDome : CompProperties
    {
        public CompProperties_OmniForceFieldDome()
        {
            this.compClass = typeof(CompOmniForceFieldDome);
        }
    }

    /// <summary>
    /// 一种力场穹顶建筑，用于在荒野或太空环境下建立一个房间区域。
    /// 一个方形立场区域，会产生带有边界的穹顶。
    ///
    /// 穹顶覆盖范围形成一个房间区域
    /// 我方单位在穹顶内自动附加“超人”hediff，移动速度加快
    /// 敌方无法进入穹顶，即使在穹顶内也难以移动
    /// 在穹顶内的我方单位可以任意射击和攻击，其他敌人和其他单位无法瞄准和攻击
    /// 穹顶外的单位无法瞄准和攻击穹顶内的单位
    /// 穹顶外的单位看不到内部单位
    /// 激光无法从外瞄准穹顶内
    /// 穹顶内阻断燃烧、爆炸
    /// 外部的爆炸不会影响到穹顶内
    /// 轨道攻击不会影响到穹顶内
    /// 穹顶拦截高低角射击
    /// 目标是穹顶区域的空投会被丢到穹顶外，如果穹顶外没有空地，会直接销毁
    /// 穹顶恒温、抵抗真空、没有气体污染、自动清理污渍
    /// 相重叠的穹顶会合并为一个更大的穹顶
    /// 穹顶自动生成 护盾屋顶 ，护盾屋顶 透光（允许植物生长）、不可塌方
    ///
    /// 
    /// </summary>
    public class CompOmniForceFieldDome : CompProjectileInterceptor
    {
        public new CompProperties_OmniForceFieldDome Props => (CompProperties_OmniForceFieldDome)props;
    }

}