using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.Sound;
using UnityEngine;

namespace FullyAutomaticOmniCrafter
{
    // 1. 建筑基础类：仅保留无敌判定
    public class Building_OmniPhantomWall : Building
    {
        public override void PreApplyDamage(ref DamageInfo dinfo, out bool absorbed)
        {
            absorbed = true; // 绝对无敌
        }
    }

    // 2. Harmony 补丁：拦截寻路核心
    [HarmonyPatch(typeof(PathFinder), "FindPath")]
    // 注意：FindPath 方法有很多重载，实际开发中需要精确指定参数类型数组 (Type[])
    public static class PathFinder_FindPath_Patch
    {
        // 这是一个我们将通过 Transpiler 注入到 A* 寻路循环中的辅助方法
        public static int ModifyPathCost(int originalCost, Pawn pathingPawn, IntVec3 cell, Map map)
        {
            // 获取该格子上的建筑物
            Building edifice = map.edificeGrid[cell];

            if (edifice != null && edifice is Building_OmniPhantomWall)
            {
                // 如果是玩家派系（或者是玩家的动物/机甲），原价通行（代价为0）
                if (pathingPawn.Faction == Faction.OfPlayer || pathingPawn.HostFaction == Faction.OfPlayer)
                {
                    return originalCost;
                }

                // 如果是敌人/野生动物，返回一个极高的惩罚代价（10000）
                // 这样敌人的寻路大脑会认为这里是绝对不可逾越的死胡同
                return originalCost + 10000;
            }

            return originalCost;
        }

        // Transpiler 逻辑（概念示范）
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
            ILGenerator generator)
        {
            /*
             * 在这里，你使用 Harmony 的 IL 操控技术，寻找 PathFinder 中计算
             * "cost += pathGrid[index]" 的代码段。
             * 然后插入对上述 ModifyPathCost() 方法的调用。
             *
             * 这样，底层 A* 算法读取到的这堵墙的代价：
             * 对你的小人 = 0
             * 对敌人 = 10000 -> 敌人自动绕路去砸你的死亡通道
             */

            // 具体的 IL 注入代码视 RimWorld 1.4/1.5 版本而定
            return instructions;
        }
    }

    [HarmonyPatch(typeof(Projectile), "CanHit")]
    public static class Projectile_CanHit_Patch
    {
        public static void Postfix(Projectile __instance, Thing thing, ref bool __result)
        {
            // 如果子弹即将击中的是我们的幻影墙
            if (thing is Building_OmniPhantomWall)
            {
                // 检查开枪者的派系
                Thing launcher = __instance.EquipmentDef != null ? __instance.Launcher : null;
                if (launcher != null && launcher.Faction == Faction.OfPlayer)
                {
                    // 玩家发射的子弹？穿过去！（不触发击中判定）
                    __result = false;
                }
                else
                {
                    // 敌人发射的子弹？原版逻辑（被这堵墙挡住并吸收）
                    __result = true;
                }
            }
        }
    }
}