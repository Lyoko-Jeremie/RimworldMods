using System.Collections.Generic;
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

    [StaticConstructorOnStartup]
    public static class OmniProjectileInterceptorTex
    {
        public static readonly Texture2D IconRangeSlider =
            ContentFinder<Texture2D>.Get("UI/Commands/OmniProjectileInterceptor_RangeSlider", false)
            ?? BaseContent.WhiteTex;
    }

    /// <summary>
    /// 一个能量盾，阻挡任何形式的攻击，且阻止敌人通过但允许我方通过，敌人不会主动攻击能量盾内的目标 
    /// </summary>
    public class CompOmniProjectileInterceptor : CompProjectileInterceptor
    {
        public new CompProperties_OmniProjectileInterceptor Props => (CompProperties_OmniProjectileInterceptor)props;

        public bool IsStatic => Props.isStatic ?? (parent is Building);

        private float? radiusOverride;

        public float Radius => radiusOverride ?? Props.radius;

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

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref radiusOverride, "radiusOverride");
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

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (var gizmo in base.CompGetGizmosExtra())
            {
                yield return gizmo;
            }

            yield return new Command_Action
            {
                defaultLabel = "OmniInterceptor_SetRadius".Translate(),
                defaultDesc = "OmniInterceptor_SetRadiusDesc".Translate(),
                icon = OmniProjectileInterceptorTex.IconRangeSlider,
                action = () => Find.WindowStack.Add(new Dialog_OmniInterceptorSettings(this))
            };
        }

        public void SetRadius(float newRadius)
        {
            radiusOverride = newRadius;
            var map = parent.Map;
            if (map != null)
            {
                map.GetComponent<OmniInterceptorTracker>()?.DirtyCache();
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
                hostile = IsEnemy(projectile.Launcher as Pawn)
                          || (projectile.Launcher.Faction != null && parent.Faction != null &&
                              projectile.Launcher.Faction.HostileTo(parent.Faction));
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
            if (cellCache == null || cellCache.Length != map.cellIndices.NumGridCells)
            {
                cellCache = new CompOmniProjectileInterceptor[map.cellIndices.NumGridCells];
            }

            for (int i = 0; i < cellCache.Length; i++)
            {
                cellCache[i] = null;
            }

            foreach (var inter in staticInterceptors)
            {
                if (!inter.Active) continue;

                float currentRadius = inter.Radius;
                int radiusInt = Mathf.CeilToInt(currentRadius);
                IntVec3 center = inter.parent.Position;

                int minX = Mathf.Max(0, center.x - radiusInt);
                int maxX = Mathf.Min(map.Size.x - 1, center.x + radiusInt);
                int minZ = Mathf.Max(0, center.z - radiusInt);
                int maxZ = Mathf.Min(map.Size.z - 1, center.z + radiusInt);

                for (int x = minX; x <= maxX; x++)
                {
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        IntVec3 c = new IntVec3(x, 0, z);
                        // 使用平面的欧几里得距离平方检查
                        float dx = (float)(x - center.x);
                        float dz = (float)(z - center.z);
                        if (dx * dx + dz * dz <= currentRadius * currentRadius)
                        {
                            int idx = map.cellIndices.CellToIndex(c);
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
    
    public class Dialog_OmniInterceptorSettings : Window
    {
        private CompOmniProjectileInterceptor comp;
        private float radius;
        private string radiusBuffer;

        public override Vector2 InitialSize => new Vector2(400f, 150f);

        public Dialog_OmniInterceptorSettings(CompOmniProjectileInterceptor comp)
        {
            this.comp = comp;
            this.radius = comp.Radius;
            this.radiusBuffer = radius.ToString("0.0");
            this.doCloseButton = true;
            this.doCloseX = true;
            this.forcePause = true;
            this.absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.Label("OmniInterceptor_Radius".Translate() + ": " + radius.ToString("0.0"));

            float newRadius = listing.Slider(radius, 1f, 256f);
            if (newRadius != radius)
            {
                radius = newRadius;
                radiusBuffer = radius.ToString("0.0");
                comp.SetRadius(radius);
            }

            Rect textRect = listing.GetRect(24f);
            Widgets.Label(textRect.LeftPart(0.4f), "OmniInterceptor_RadiusInput".Translate());
            string buffer = Widgets.TextField(textRect.RightPart(0.6f), radiusBuffer);
            if (buffer != radiusBuffer)
            {
                radiusBuffer = buffer;
                if (float.TryParse(radiusBuffer, out float parsed) && parsed >= 1f && parsed <= 256f)
                {
                    radius = parsed;
                    comp.SetRadius(radius);
                }
            }

            listing.End();
        }
    }
    
}