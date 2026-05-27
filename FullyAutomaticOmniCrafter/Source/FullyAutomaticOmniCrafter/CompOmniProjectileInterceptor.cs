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
        public static readonly Texture2D IconInterceptSkyfaller =
            ContentFinder<Texture2D>.Get("UI/Commands/OmniProjectileInterceptor_InterceptSkyfaller", false)
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

        public bool interceptSkyfallers = true;

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref radiusOverride, "radiusOverride");
            Scribe_Values.Look(ref interceptSkyfallers, "interceptSkyfallers", true);
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

            yield return new Command_Toggle
            {
                defaultLabel = "OmniInterceptor_InterceptSkyfallers".Translate(),
                defaultDesc = "OmniInterceptor_InterceptSkyfallersDesc".Translate(),
                icon = OmniProjectileInterceptorTex.IconInterceptSkyfaller,
                isActive = () => interceptSkyfallers,
                toggleAction = () => interceptSkyfallers = !interceptSkyfallers
            };

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
            if (pawn == null) return true; // 无主也视为拦截对象（保守策略）
            // 无论护盾属于谁，只要该 Pawn 对玩家敌对，就视为“敌人”
            return pawn.HostileTo(Faction.OfPlayer);
        }

        public bool IsEnemy(Thing thing)
        {
            if (thing == null) return true;
            if (thing is Pawn pawn) return IsEnemy(pawn);
            // 检查派系是否对玩家敌对
            if (thing.Faction == null) return true; // 无派系通常视为拦截对象（如无主投影物）
            return thing.Faction.HostileTo(Faction.OfPlayer);
        }

        // 拦截逻辑
        public new bool CheckIntercept(Projectile projectile, Vector3 lastExactPos, Vector3 newExactPos)
        {
            if (!Active) return false;

            // 检查来源
            if (!IsEnemy(projectile.Launcher))
            {
                // 如果不是敌人发射的，且配置为不拦截非敌对投影物，则放行
                if (!Props.interceptNonHostileProjectiles) return false;
            }

            // 距离检查
            float radius = Radius;
            Vector3 myPos = parent.Position.ToVector3Shifted();
            if ((newExactPos - myPos).MagnitudeHorizontalSquared() > (radius + 1f) * (radius + 1f))
            {
                return false;
            }

            // 如果已经进入了护盾内部（相对于中心），拦截它
            // 基类逻辑通常处理这种穿过边界的情况
            return base.CheckIntercept(projectile, lastExactPos, newExactPos);
        }

        public new bool CheckBombardmentIntercept(Bombardment bombardment, Bombardment.BombardmentProjectile projectile)
        {
            if (!Active) return false;
            // 拦截敌人或无主的轰炸
            if (IsEnemy(bombardment.instigator)) return true;
            return base.CheckBombardmentIntercept(bombardment, projectile);
        }

        public new bool BombardmentCanStartFireAt(Bombardment bombardment, IntVec3 cell)
        {
            if (!Active) return true; 
            // 如果轰炸者是敌人，且目标在护盾半径内，拦截（阻止开火）
            if (IsEnemy(bombardment.instigator) && cell.InHorDistOf(parent.Position, Radius))
            {
                return false; // 返回 false 表示不能开火（即拦截）
            }
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

        public bool IsCellProtected(IntVec3 c, Thing searcher, out CompOmniProjectileInterceptor protector)
        {
            protector = null;
            if (!c.InBounds(map)) return false;

            if (cacheDirty)
            {
                RebuildCache();
            }

            // 1. 检查固定护盾缓存 O(1)
            var staticInter = cellCache[map.cellIndices.CellToIndex(c)];
            if (staticInter != null && staticInter.Active && staticInter.IsEnemy(searcher))
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
                    if (inter.IsEnemy(searcher))
                    {
                        protector = inter;
                        return true;
                    }
                }
            }

            return false;
        }

        public bool IsAreaProtected(IntVec3 center, float radius, Thing searcher, out CompOmniProjectileInterceptor protector)
        {
            protector = null;

            if (cacheDirty)
            {
                RebuildCache();
            }

            // 1. 检查固定护盾
            // 只要 (护盾中心和攻击中心之间的距离) < (护盾半径 + 攻击半径)，两个圆就相交
            foreach (var inter in staticInterceptors)
            {
                if (inter.Active && inter.IsEnemy(searcher))
                {
                    float combinedRadius = inter.Radius + radius;
                    if (center.InHorDistOf(inter.parent.Position, combinedRadius))
                    {
                        protector = inter;
                        return true;
                    }
                }
            }

            // 2. 检查移动护盾
            for (int i = 0; i < mobileInterceptors.Count; i++)
            {
                var inter = mobileInterceptors[i];
                if (inter.Active && inter.IsEnemy(searcher))
                {
                    float combinedRadius = inter.Radius + radius;
                    if (center.InHorDistOf(inter.parent.Position, combinedRadius))
                    {
                        protector = inter;
                        return true;
                    }
                }
            }

            return false;
        }

        public bool IsTargetProtected(Thing target, Thing searcher)
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
            if (searcher?.Thing?.Map == null) return;

            var tracker = searcher.Thing.Map.GetComponent<OmniInterceptorTracker>();
            if (tracker == null) return;

            // 如果已经有了目标，检查目标是否受保护（如果是敌人在攻击，则拦截）
            if (__result != null && tracker.IsTargetProtected(__result.Thing, searcher.Thing))
            {
                __result = null;
            }

            // 额外逻辑：如果攻击者自己就在某个敌方护盾内，他也不能攻击任何人
            if (tracker.IsCellProtected(searcher.Thing.Position, searcher.Thing, out _))
            {
                __result = null;
            }
        }
    }

    [HarmonyPatch(typeof(Explosion), "StartExplosion")]
    public static class Patch_Explosion_StartExplosion
    {
        public static bool Prefix(Explosion __instance)
        {
            if (__instance.Map == null) return true;
            var tracker = __instance.Map.GetComponent<OmniInterceptorTracker>();
            if (tracker == null) return true;

            // 如果爆炸范围涉及受保护区域（且来源是敌人/无主），则抑制
            if (tracker.IsAreaProtected(__instance.Position, __instance.radius, __instance.instigator, out _))
            {
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(Skyfaller), "Tick")]
    public static class Patch_Skyfaller_Tick
    {
        public static void Postfix(Skyfaller __instance)
        {
            if (!__instance.Spawned || __instance.Map == null) return;
            var tracker = __instance.Map.GetComponent<OmniInterceptorTracker>();
            if (tracker == null) return;

            // 检查落点是否受保护
            if (tracker.IsCellProtected(__instance.Position, null, out var protector))
            {
                if (protector.interceptSkyfallers)
                {
                    RedirectOrDestroy(__instance, protector);
                }
            }
        }

        private static void RedirectOrDestroy(Skyfaller skyfaller, CompOmniProjectileInterceptor protector)
        {
            Map map = skyfaller.Map;
            float radius = protector.Radius;
            IntVec3 center = protector.parent.Position;

            // 尝试在护盾半径外寻找最近的有效空地
            // 简单逻辑：沿着从中心到落点的向量往外推，直到超出半径
            Vector3 direction = (skyfaller.Position - center).ToVector3();
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.forward;
            }
            direction.Normalize();

            IntVec3 targetCell = center + (direction * (radius + 2f)).ToIntVec3();

            // 如果超出地图边界，尝试在地图边缘寻找
            if (!targetCell.InBounds(map))
            {
                targetCell = skyfaller.Position; // 重置，准备进行更广泛的搜索
            }

            // 在目标位置附近寻找最近的落点
            var trackerLocal = map.GetComponent<OmniInterceptorTracker>();
            if (CellFinder.TryFindRandomCellNear(targetCell, map, Mathf.CeilToInt(radius) + 10,
                    c => c.InBounds(map) && (trackerLocal == null || !trackerLocal.IsCellProtected(c, null, out _)), out var foundCell))
            {
                skyfaller.Position = foundCell;
                Messages.Message("OmniInterceptor_SkyfallerRedirected".Translate(), skyfaller, MessageTypeDefOf.NeutralEvent);
            }
            else
            {
                // 实在找不到地方落（可能是全图盾），直接销毁
                Messages.Message("OmniInterceptor_SkyfallerDestroyed".Translate(skyfaller.LabelCap), MessageTypeDefOf.CautionInput);
                skyfaller.Destroy();
            }
        }
    }

    [HarmonyPatch(typeof(OrbitalStrike), "Tick")]
    public static class Patch_OrbitalStrike_Tick
    {
        public static void Postfix(OrbitalStrike __instance)
        {
            if (!__instance.Spawned || __instance.Map == null) return;
            var tracker = __instance.Map.GetComponent<OmniInterceptorTracker>();
            if (tracker == null) return;

            float radius = 0f;
            if (__instance is PowerBeam) radius = 15f;
            else if (__instance is Bombardment b) radius = b.impactAreaRadius + 8f; // 加上爆炸半径

            if (tracker.IsAreaProtected(__instance.Position, radius, __instance.instigator, out _))
            {
                __instance.Destroy();
            }
        }
    }

    [HarmonyPatch(typeof(Verb_LaunchProjectile), "CanHitTargetFrom")]
    public static class Patch_Verb_LaunchProjectile_CanHitTargetFrom
    {
        public static void Postfix(Verb_LaunchProjectile __instance, IntVec3 root, LocalTargetInfo targ, ref bool __result)
        {
            if (!__result || __instance.caster?.Map == null) return;

            var tracker = __instance.caster.Map.GetComponent<OmniInterceptorTracker>();
            if (tracker == null) return;

            // 如果攻击者是敌人，且目标位置受保护，拦截
            if (tracker.IsTargetProtected(targ.Thing, __instance.caster) || 
                (targ.HasThing == false && tracker.IsCellProtected(targ.Cell, __instance.caster, out _)))
            {
                __result = false;
                return;
            }

            // 如果攻击者自己就在受保护位置（敌方护盾内），也不能向外射击
            if (tracker.IsCellProtected(root, __instance.caster, out _))
            {
                __result = false;
                return;
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