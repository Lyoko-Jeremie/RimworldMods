using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    public class CompProperties_OmniProjectileInterceptor : CompProperties_ProjectileInterceptor
    {
        public bool? isStatic;

        public CompProperties_OmniProjectileInterceptor()
        {
            compClass = typeof(CompOmniProjectileInterceptor);
        }
    }

    /// <summary>
    /// 一个能量盾，阻挡任何形式的攻击，且阻止敌人通过但允许我方通过，敌人不会主动攻击能量盾内的目标 
    /// </summary>
    public class CompOmniProjectileInterceptor : CompProjectileInterceptor
    {
        public new CompProperties_OmniProjectileInterceptor Props => (CompProperties_OmniProjectileInterceptor)props;

        public bool IsStatic => Props.isStatic ?? (parent is Building);

        public override void PostPreApplyDamage(ref DamageInfo dinfo, out bool absorbed)
        {
            // 护盾本体无敌，不吸收伤害（因为它是能量场的一部分，不应该被损毁）
            absorbed = true;
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            parent.Map.GetComponent<OmniInterceptorTracker>().Register(this);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            map.GetComponent<OmniInterceptorTracker>()?.Deregister(this);
            base.PostDeSpawn(map, mode);
        }

        public override void CompTick()
        {
            base.CompTick();
            // 确保状态始终处于激活，无视任何损伤
            if (Active && currentHitPoints < HitPointsMax)
            {
                currentHitPoints = HitPointsMax;
            }

            if (IsStatic && parent.IsHashIntervalTick(60))
            {
                // 检查激活状态变化或半径变化。由于 currentHitPoints 总是被重设为满，Active 通常稳定。
                // 暂时只在 Register/Deregister 时刷新，这里作为安全网
                // parent.Map.GetComponent<OmniInterceptorTracker>().DirtyCache();
            }
        }

        public bool IsEnemy(Pawn pawn)
        {
            if (pawn == null) return false;
            if (parent.Faction == null) return pawn.HostileTo(Faction.OfPlayer);
            return pawn.HostileTo(parent.Faction);
        }

        // 拦截逻辑
        public new bool CheckIntercept(Projectile projectile, Vector3 lastExactPos, Vector3 newExactPos)
        {
            if (!Active) return false;

            // 只要不是己方或盟友，且不在白名单内，就拦截
            bool hostile = false;
            if (projectile.Launcher != null)
            {
                hostile = IsEnemy(projectile.Launcher as Pawn) || (projectile.Launcher.Faction != null && parent.Faction != null && projectile.Launcher.Faction.HostileTo(parent.Faction));
            }
            else
            {
                hostile = true; // 无主投影物默认拦截（除非配置了不拦截非敌对）
            }

            if (!hostile && !Props.interceptNonHostileProjectiles) return false;

            // 距离检查
            float radius = Radius;
            Vector3 myPos = parent.Position.ToVector3Shifted();
            if ((newExactPos - myPos).MagnitudeHorizontalSquared() > (radius + 1f) * (radius + 1f))
            {
                return false;
            }

            return base.CheckIntercept(projectile, lastExactPos, newExactPos);
        }

        public new bool CheckBombardmentIntercept(Bombardment bombardment, Bombardment.BombardmentProjectile projectile)
        {
            if (!Active) return false;
            return base.CheckBombardmentIntercept(bombardment, projectile);
        }

        public new bool BombardmentCanStartFireAt(Bombardment bombardment, IntVec3 cell)
        {
            if (!Active) return true; // 返回 true 表示拦截（阻止开火）
            return base.BombardmentCanStartFireAt(bombardment, cell);
        }
    }

    public class OmniInterceptorTracker : MapComponent
    {
        private List<CompOmniProjectileInterceptor> staticInterceptors = new List<CompOmniProjectileInterceptor>();
        private List<CompOmniProjectileInterceptor> mobileInterceptors = new List<CompOmniProjectileInterceptor>();
        private CompOmniProjectileInterceptor[] cellCache;
        private bool cacheDirty = true;

        public OmniInterceptorTracker(Map map) : base(map)
        {
            cellCache = new CompOmniProjectileInterceptor[map.cellIndices.NumGridCells];
        }

        public void Register(CompOmniProjectileInterceptor interceptor)
        {
            if (interceptor.IsStatic)
            {
                if (!staticInterceptors.Contains(interceptor))
                {
                    staticInterceptors.Add(interceptor);
                    cacheDirty = true;
                }
            }
            else
            {
                if (!mobileInterceptors.Contains(interceptor))
                {
                    mobileInterceptors.Add(interceptor);
                }
            }
        }

        public void Deregister(CompOmniProjectileInterceptor interceptor)
        {
            if (interceptor.IsStatic)
            {
                if (staticInterceptors.Remove(interceptor))
                {
                    cacheDirty = true;
                }
            }
            else
            {
                mobileInterceptors.Remove(interceptor);
            }
        }

        public void DirtyCache()
        {
            cacheDirty = true;
        }

        private void RebuildCache()
        {
            for (int i = 0; i < cellCache.Length; i++)
            {
                cellCache[i] = null;
            }

            foreach (var inter in staticInterceptors)
            {
                if (!inter.Active) continue;

                int radius = Mathf.CeilToInt(inter.Radius);
                IntVec3 center = inter.parent.Position;
                int minX = Mathf.Max(0, center.x - radius);
                int maxX = Mathf.Min(map.Size.x - 1, center.x + radius);
                int minZ = Mathf.Max(0, center.z - radius);
                int maxZ = Mathf.Min(map.Size.z - 1, center.z + radius);

                float radiusSq = inter.Radius * inter.Radius;

                for (int x = minX; x <= maxX; x++)
                {
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        IntVec3 c = new IntVec3(x, 0, z);
                        if (c.InHorDistOf(center, inter.Radius))
                        {
                            int idx = map.cellIndices.CellToIndex(c);
                            // 如果有多个护盾覆盖，这里只保存一个。对于目前逻辑足够，因为只要有一个保护就行。
                            if (cellCache[idx] == null)
                            {
                                cellCache[idx] = inter;
                            }
                        }
                    }
                }
            }

            cacheDirty = false;
        }

        public bool IsCellProtected(IntVec3 c, Pawn forPawn, out CompOmniProjectileInterceptor protector)
        {
            protector = null;
            if (!c.InBounds(map)) return false;

            if (cacheDirty)
            {
                RebuildCache();
            }

            // 1. 检查固定护盾缓存 O(1)
            var staticInter = cellCache[map.cellIndices.CellToIndex(c)];
            if (staticInter != null && staticInter.Active && staticInter.IsEnemy(forPawn))
            {
                protector = staticInter;
                return true;
            }

            // 2. 检查移动护盾 O(N_mobile)
            for (int i = 0; i < mobileInterceptors.Count; i++)
            {
                var inter = mobileInterceptors[i];
                if (inter.Active && c.InHorDistOf(inter.parent.Position, inter.Radius))
                {
                    if (inter.IsEnemy(forPawn))
                    {
                        protector = inter;
                        return true;
                    }
                }
            }

            return false;
        }

        public bool IsTargetProtected(Thing target, Pawn searcher)
        {
            if (target == null || !target.Spawned) return false;
            return IsCellProtected(target.Position, searcher, out _);
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), "CostToMoveIntoCell", typeof(Pawn), typeof(IntVec3))]
    public static class Patch_Pawn_PathFollower_CostToMoveIntoCell
    {
        public static void Postfix(Pawn pawn, IntVec3 c, ref float __result)
        {
            if (__result >= 10000f || pawn?.Map == null) return;

            var tracker = pawn.Map.GetComponent<OmniInterceptorTracker>();
            if (tracker != null && tracker.IsCellProtected(c, pawn, out _))
            {
                __result = 10000f;
            }
        }
    }

    [HarmonyPatch(typeof(AttackTargetFinder), "BestAttackTarget")]
    public static class Patch_AttackTargetFinder_BestAttackTarget
    {
        public static void Postfix(ref IAttackTarget __result, IAttackTargetSearcher searcher)
        {
            if (__result == null || searcher?.Thing?.Map == null) return;
            
            var tracker = searcher.Thing.Map.GetComponent<OmniInterceptorTracker>();
            if (tracker != null && tracker.IsTargetProtected(__result.Thing, searcher.Thing as Pawn))
            {
                __result = null;
            }
        }
    }
}