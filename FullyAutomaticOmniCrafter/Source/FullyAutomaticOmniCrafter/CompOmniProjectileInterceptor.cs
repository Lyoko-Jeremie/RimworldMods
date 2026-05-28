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
        public static readonly Texture2D IconShieldEnabled =
            ContentFinder<Texture2D>.Get("UI/Commands/OmniProjectileInterceptor_ShieldEnabled", false)
            ?? BaseContent.WhiteTex;
        public static readonly Texture2D IconRangeSlider =
            ContentFinder<Texture2D>.Get("UI/Commands/OmniProjectileInterceptor_RangeSlider", false)
            ?? BaseContent.WhiteTex;
        public static readonly Texture2D IconInterceptSkyfaller =
            ContentFinder<Texture2D>.Get("UI/Commands/OmniProjectileInterceptor_InterceptSkyfaller", false)
            ?? BaseContent.WhiteTex;
        public static readonly Texture2D IconAlwaysVisible =
            ContentFinder<Texture2D>.Get("UI/Commands/OmniProjectileInterceptor_AlwaysVisible", false)
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

        public virtual float Radius => radiusOverride ?? Props.radius;

        public virtual bool IsInside(Vector3 pos)
        {
            return (pos - parent.Position.ToVector3Shifted()).MagnitudeHorizontalSquared() <= Radius * Radius;
        }

        public virtual bool IsCellInside(IntVec3 cell)
        {
            return cell.InHorDistOf(parent.Position, Radius);
        }

        public virtual bool Intersects(IntVec3 center, float radius)
        {
            float combinedRadius = Radius + radius;
            return center.InHorDistOf(parent.Position, combinedRadius);
        }

        public override void PostPreApplyDamage(ref DamageInfo dinfo, out bool absorbed)
        {
            // 护盾本体无敌，不吸收伤害（因为它是能量场的一部分，不应该被损毁）
            absorbed = true;
        }

        // 注意：RimWorld 1.5 中护盾崩溃是在 PostPreApplyDamage 之后由原生逻辑处理的
        // 或者在 Notify_DamageApplied 中。如果 Notify_DamageApplied 无法 override，
        // 我们通过在 CompTick 中强制重置 lastShieldBreakTick 来抵消它。

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
        public bool shieldEnabled = true;
        public new bool Active => shieldEnabled && parent.Spawned;

        public bool alwaysVisible = true;
        public float idleAlphaMultiplier = 1f;

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref radiusOverride, "radiusOverride");
            Scribe_Values.Look(ref interceptSkyfallers, "interceptSkyfallers", true);
            Scribe_Values.Look(ref shieldEnabled, "shieldEnabled", true);
            Scribe_Values.Look(ref alwaysVisible, "alwaysVisible", true);
            Scribe_Values.Look(ref idleAlphaMultiplier, "idleAlphaMultiplier", 1f);
        }

        public override void CompTick()
        {
            if (!shieldEnabled)
            {
                currentHitPoints = 0;
                return;
            }

            base.CompTick();

            // 确保状态始终处于激活，无视任何损伤
            if (currentHitPoints < HitPointsMax)
            {
                currentHitPoints = HitPointsMax;
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
                defaultLabel = "OmniInterceptor_ShieldEnabled".Translate(),
                defaultDesc = "OmniInterceptor_ShieldEnabledDesc".Translate(),
                icon = OmniProjectileInterceptorTex.IconShieldEnabled,
                isActive = () => shieldEnabled,
                toggleAction = () =>
                {
                    shieldEnabled = !shieldEnabled;
                    if (parent.Map != null)
                    {
                        parent.Map.GetComponent<OmniInterceptorTracker>()?.DirtyCache();
                    }
                }
            };

            yield return new Command_Toggle
            {
                defaultLabel = "OmniInterceptor_InterceptSkyfallers".Translate(),
                defaultDesc = "OmniInterceptor_InterceptSkyfallersDesc".Translate(),
                icon = OmniProjectileInterceptorTex.IconInterceptSkyfaller,
                isActive = () => interceptSkyfallers,
                toggleAction = () => interceptSkyfallers = !interceptSkyfallers
            };

            yield return new Command_Toggle
            {
                defaultLabel = "OmniInterceptor_AlwaysVisible".Translate(),
                defaultDesc = "OmniInterceptor_AlwaysVisibleDesc".Translate(),
                icon = OmniProjectileInterceptorTex.IconAlwaysVisible,
                isActive = () => alwaysVisible,
                toggleAction = () => alwaysVisible = !alwaysVisible
            };

            yield return new Command_Action
            {
                defaultLabel = "OmniInterceptor_SetRadius".Translate(),
                defaultDesc = "OmniInterceptor_SetRadiusDesc".Translate(),
                icon = OmniProjectileInterceptorTex.IconRangeSlider,
                action = () => Find.WindowStack.Add(new Dialog_OmniInterceptorSettings(this))
            };
        }

        public virtual void SetRadius(float newRadius)
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

        public override void PostDraw()
        {
            // 不再在 PostDraw 中绘制，改为由 OmniInterceptorTracker.MapComponentUpdate 统一绘制，
            // 这样即使物体不在视口范围内，光效圈仍然可见。
        }

        private static readonly MaterialPropertyBlock matPropertyBlock = new MaterialPropertyBlock();

        public virtual void DrawShield()
        {
            if (!Active) return;

            // 只有选中时才绘制半径圆圈（白色边框）
            if (Find.Selector.IsSelected(parent))
            {
                DrawRadiusRing(parent.Position, Radius);
            }

            Vector3 drawPos = parent.DrawPos;
            drawPos.y = AltitudeLayer.MoteOverhead.AltitudeFor();
            
            float currentAlpha = GetCurrentAlpha();
            if (currentAlpha > 0.0f)
            {
                Color color = Props.color;
                color.a *= currentAlpha;

                // 使用静态缓存的材质属性块，避免每帧分配
                matPropertyBlock.Clear();
                matPropertyBlock.SetColor(ShaderPropertyIDs.Color, color);

                // 护盾纹理实际大小因子 (297/256 来自原版)
                float sizeFactor = 2.3203125f; // (297.0 / 256.0) * 2
                float drawSize = Radius * sizeFactor;

                Matrix4x4 matrix = default;
                matrix.SetTRS(drawPos, Quaternion.identity, new Vector3(drawSize, 1f, drawSize));
                
                // 使用基类的 ForceFieldMat
                // 注意：由于 ForceFieldMat 是私有的，我们需要通过反射获取或者使用相同的路径
                // 原版路径是 "Other/ForceField"
                Graphics.DrawMesh(MeshPool.plane10, matrix, MaterialPool.MatFrom("Other/ForceField", ShaderDatabase.MoteGlow), 0, null, 0, matPropertyBlock);
            }
        }

        private static List<IntVec3> ringDrawCells = new List<IntVec3>();

        private void DrawRadiusRing(IntVec3 center, float radius)
        {
            if (radius < 50f)
            {
                GenDraw.DrawRadiusRing(center, radius);
                return;
            }

            ringDrawCells.Clear();
            int num = Mathf.CeilToInt(radius);
            for (int x = -num; x <= num; x++)
            {
                // 仅检查大概在圆周附近的点
                float xSq = x * x;
                // 如果 x 的位置已经超过了半径，直接跳过
                if (xSq > radius * radius) continue;

                // 确定 z 的范围
                // z^2 <= r^2 - x^2  =>  |z| <= sqrt(r^2 - x^2)
                float maxZ = Mathf.Sqrt(radius * radius - xSq);
                float minZ = 0;
                float innerRadiusSq = (radius - 1f) * (radius - 1f);
                if (xSq < innerRadiusSq)
                {
                    minZ = Mathf.Sqrt(innerRadiusSq - xSq);
                }

                int zStart = Mathf.CeilToInt(minZ);
                int zEnd = Mathf.FloorToInt(maxZ);

                for (int z = zStart; z <= zEnd; z++)
                {
                    ringDrawCells.Add(center + new IntVec3(x, 0, z));
                    if (z != 0) ringDrawCells.Add(center + new IntVec3(x, 0, -z));
                }
            }
            if (ringDrawCells.Count > 0)
            {
                GenDraw.DrawFieldEdges(ringDrawCells, Color.white);
            }
        }

        private float GetCurrentAlpha()
        {
            // 如果没开启始终可见，且没被选中，则不显示
            if (!alwaysVisible && !Find.Selector.IsSelected(parent))
            {
                return 0f;
            }

            // 始终显示护盾，即使没有被选中。
            // 使用 Mathf.Max 确保 minIdleAlpha 不会导致 alpha 变成负数（原版 minIdleAlpha 默认为 -1.7）
            float baseMinIdleAlpha = Mathf.Max(0.05f, Props.minIdleAlpha);
            float idleAlpha = Mathf.Lerp(baseMinIdleAlpha, 0.11f, (Mathf.Sin((float)(Gen.HashCombineInt(parent.thingIDNumber, 96804938) % 100) + Time.realtimeSinceStartup * Props.idlePulseSpeed) + 1f) / 2f);
            idleAlpha *= idleAlphaMultiplier;
            
            if (Find.Selector.IsSelected(parent))
            {
                float pulseSpeed = Mathf.Max(2f, Props.idlePulseSpeed);
                float selectedAlpha = Mathf.Lerp(0.2f, 0.62f, (Mathf.Sin((float)(Gen.HashCombineInt(parent.thingIDNumber, 35990913) % 100) + Time.realtimeSinceStartup * pulseSpeed) + 1f) / 2f);
                return Mathf.Max(idleAlpha, selectedAlpha);
            }

            return Mathf.Max(idleAlpha, Mathf.Max(Props.minAlpha, 0.05f));
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
            if (!IsInside(newExactPos))
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
            if (IsEnemy(bombardment.instigator) && IsCellInside(cell))
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

        public override void MapComponentUpdate()
        {
            base.MapComponentUpdate();
            
            // 获取当前相机的视口范围（以格子为单位）
            // 如果不在游戏运行状态，或者地图不是当前地图，则不执行视口检查绘制（安全保护）
            if (Find.CurrentMap != map) return;
            
            CellRect viewRect = Find.CameraDriver.CurrentViewRect;
            
            // 统一绘制所有护盾，但增加视口裁剪检查以优化性能
            for (int i = 0; i < staticInterceptors.Count; i++)
            {
                var inter = staticInterceptors[i];
                if (!inter.Active) continue;
                // 增加护盾半径作为缓冲区，确保边缘不会被突然截断
                if (viewRect.ExpandedBy(Mathf.CeilToInt(inter is CompOmniRectangleProjectileInterceptor r ? Mathf.Max(r.Width, r.Height) : inter.Radius)).Contains(inter.parent.Position))
                {
                    inter.DrawShield();
                }
            }
            for (int i = 0; i < mobileInterceptors.Count; i++)
            {
                var inter = mobileInterceptors[i];
                if (!inter.Active) continue;
                if (viewRect.ExpandedBy(Mathf.CeilToInt(inter is CompOmniRectangleProjectileInterceptor r ? Mathf.Max(r.Width, r.Height) : inter.Radius)).Contains(inter.parent.Position))
                {
                    inter.DrawShield();
                }
            }
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
                if (!inter.Active || !inter.shieldEnabled) continue;

                if (inter is CompOmniRectangleProjectileInterceptor rectInter)
                {
                    CellRect rect = rectInter.OccupiedRect;

                    for (int x = rect.minX; x <= rect.maxX; x++)
                    {
                        for (int z = rect.minZ; z <= rect.maxZ; z++)
                        {
                            IntVec3 c = new IntVec3(x, 0, z);
                            if (c.InBounds(map))
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
                else
                {
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
            // 只有当存在移动护盾时才进行检查
            int mobileCount = mobileInterceptors.Count;
            if (mobileCount > 0)
            {
                for (int i = 0; i < mobileCount; i++)
                {
                    var inter = mobileInterceptors[i];
                    // 先检查 Active 属性（通常比几何判定快）
                    if (inter.Active && inter.IsCellInside(c) && inter.IsEnemy(searcher))
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

            // 1. 优先通过 CellCache 快速检查中心点（如果半径较小）
            // 如果中心点已经被保护，则整个区域肯定被保护
            if (radius <= 1.5f)
            {
                if (IsCellProtected(center, searcher, out protector)) return true;
            }
            else
            {
                // 对于大半径，先通过 CellCache 检查中心点也是一个很好的启发式优化
                var staticInter = cellCache[map.cellIndices.CellToIndex(center)];
                if (staticInter != null && staticInter.Active && staticInter.IsEnemy(searcher))
                {
                    protector = staticInter;
                    return true;
                }
            }

            // 2. 检查固定护盾
            int staticCount = staticInterceptors.Count;
            for (int i = 0; i < staticCount; i++)
            {
                var inter = staticInterceptors[i];
                if (inter.Active && inter.IsEnemy(searcher))
                {
                    if (inter.Intersects(center, radius))
                    {
                        protector = inter;
                        return true;
                    }
                }
            }

            // 3. 检查移动护盾
            int mobileCount = mobileInterceptors.Count;
            for (int i = 0; i < mobileCount; i++)
            {
                var inter = mobileInterceptors[i];
                if (inter.Active && inter.IsEnemy(searcher))
                {
                    if (inter.Intersects(center, radius))
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

    public class Dialog_OmniInterceptorSettings : Window
    {
        private CompOmniProjectileInterceptor comp;
        private float radius;
        private string radiusBuffer;
        private float idleAlphaMultiplier;

        public override Vector2 InitialSize => new Vector2(400f, 250f);

        public Dialog_OmniInterceptorSettings(CompOmniProjectileInterceptor comp)
        {
            this.comp = comp;
            this.radius = comp.Radius;
            this.radiusBuffer = radius.ToString("0.0");
            this.idleAlphaMultiplier = comp.idleAlphaMultiplier;
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

            listing.Gap();
            listing.Label("OmniInterceptor_IdleAlphaMultiplier".Translate() + ": " + idleAlphaMultiplier.ToString("P0"));
            float newIdleAlphaMultiplier = listing.Slider(idleAlphaMultiplier, 0f, 10f);
            if (newIdleAlphaMultiplier != idleAlphaMultiplier)
            {
                idleAlphaMultiplier = newIdleAlphaMultiplier;
                comp.idleAlphaMultiplier = idleAlphaMultiplier;
            }

            listing.End();
        }
    }
    
}