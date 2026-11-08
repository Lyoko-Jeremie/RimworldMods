using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// Replace Stuff 可选兼容：让 OverWallDef.IsWall 把万能幻影墙识别为“墙”。
    ///
    /// Replace Stuff 用
    ///   coversFloor &amp;&amp; holdsRoof &amp;&amp; passability == Impassable
    /// 判定“可承载壁挂件的墙”，而幻影墙为保证寻路必须是 Standable，
    /// 因此不会被识别，其 BlocksConstruction / SpawningWipes / 替换材质等判定随之失效。
    ///
    /// 本补丁只对“万能幻影墙”这一类 Def 把返回值改为 true，不影响其它 Def；
    /// 未安装 Replace Stuff（或版本不含 IsWall）时整个补丁类直接跳过。
    /// </summary>
    [HarmonyPatch]
    internal static class Patch_Optional_ReplaceStuff_PhantomWall
    {
        private const string OverWallDefTypeName = "Replace_Stuff.OverWallDef";
        private const string IsWallMethodName = "IsWall";

        private static bool _resolved;
        private static Type _overWallDefType;
        private static MethodInfo _isWallMethod;

        /// <summary>
        /// 反射解析 Replace Stuff 的类型与方法，避免编译期硬依赖其程序集。
        /// </summary>
        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            _overWallDefType = AccessTools.TypeByName(OverWallDefTypeName);
            if (_overWallDefType != null)
                _isWallMethod = AccessTools.Method(_overWallDefType, IsWallMethodName, new[] { typeof(BuildableDef) });
        }

        /// <summary>
        /// 未安装 Replace Stuff 时返回 false，Harmony 会跳过本补丁且不报错。
        /// </summary>
        [HarmonyPrepare]
        private static bool Prepare()
        {
            Resolve();
            if (_isWallMethod == null) return false;

            Log.Message("[OuterrealmStorage] 检测到 Replace Stuff，已启用万能幻影墙的 IsWall 兼容。");
            return true;
        }

        /// <summary>
        /// 动态目标方法：替代 [HarmonyPatch(typeof(...))]，从而不引用 Replace Stuff 程序集。
        /// </summary>
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            Resolve();
            return _isWallMethod;
        }

        [HarmonyPostfix]
        private static void Postfix(BuildableDef bdef, ref bool __result)
        {
            if (__result) return;
            if (IsPhantomWall(bdef))
                __result = true;
        }

        /// <summary>
        /// 判断 Def 是否指向万能幻影墙；蓝图/框架 Def 先解析到目标建筑 Def。
        /// </summary>
        private static bool IsPhantomWall(BuildableDef bdef)
        {
            ThingDef def = bdef as ThingDef;
            if (def != null && def.entityDefToBuild is ThingDef built)
                def = built;

            return def != null
                && def.thingClass != null
                && typeof(Building_OmniPhantomWall2).IsAssignableFrom(def.thingClass);
        }
    }
}
