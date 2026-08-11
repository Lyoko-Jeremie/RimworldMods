using System.Collections.Generic;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace OuterrealmTechRobot
{
    public static class ArtificialMaidCaravanUtility
    {
        public const int MinWorldTicksPerMove = 1;
        // 原版 RimWorld 用来把“地图格移动速度”折算成“世界格移动速度”的比例常量。
        private const float CellToTilesConversionRatio = 340f;

        [System.ThreadStatic]
        private static int ignoreWorldPathCostsDepth;

        public static bool IgnoreWorldPathCosts => ignoreWorldPathCostsDepth > 0;

        public static void PushIgnoreWorldPathCosts(bool enabled, out bool pushed)
        {
            pushed = enabled;
            if (enabled)
            {
                ignoreWorldPathCostsDepth++;
            }
        }

        public static void PopIgnoreWorldPathCosts(bool pushed)
        {
            if (pushed && ignoreWorldPathCostsDepth > 0)
            {
                ignoreWorldPathCostsDepth--;
            }
        }

        public static bool ContainsArtificialMaid(Caravan caravan)
        {
            return caravan != null && ContainsArtificialMaid(caravan.PawnsListForReading);
        }

        public static bool ContainsArtificialMaid(List<TransferableOneWay> transferables)
        {
            if (transferables == null)
            {
                return false;
            }

            for (int i = 0; i < transferables.Count; i++)
            {
                TransferableOneWay transferable = transferables[i];
                if (transferable == null || !transferable.HasAnyThing)
                {
                    continue;
                }

                for (int j = 0; j < transferable.things.Count; j++)
                {
                    if (transferable.things[j] is Pawn pawn && pawn.def == ArtificialMaidDefOf.ArtificialMaid)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool ContainsArtificialMaid(List<Pawn> pawns)
        {
            if (pawns == null)
            {
                return false;
            }

            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn != null && pawn.def == ArtificialMaidDefOf.ArtificialMaid)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool TryGetMoveSpeedTicksPerMove(
            List<Pawn> pawns,
            float massUsage,
            float massCapacity,
            out int ticksPerMove)
        {
            ticksPerMove = 0;
            if (pawns == null)
            {
                return false;
            }

            float humanMoveSpeed = ThingDefOf.Human.GetStatValueAbstract(StatDefOf.MoveSpeed);
            if (humanMoveSpeed <= 0f)
            {
                return false;
            }

            float bestSpeedFactor = 1f;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn == null || pawn.def != ArtificialMaidDefOf.ArtificialMaid)
                {
                    continue;
                }

                float moveSpeed = pawn.GetStatValue(StatDefOf.MoveSpeed);
                if (moveSpeed > humanMoveSpeed)
                {
                    bestSpeedFactor = UnityEngine.Mathf.Max(bestSpeedFactor, moveSpeed / humanMoveSpeed);
                }
            }

            if (bestSpeedFactor <= 1f)
            {
                return false;
            }

            int baseHumanTicksPerCell = UnityEngine.Mathf.RoundToInt(1f / (humanMoveSpeed / 60f));
            float baseWorldTicksPerMove = baseHumanTicksPerCell * CellToTilesConversionRatio;
            float massFactor = GetMoveSpeedFactorFromMass(massUsage, massCapacity);
            ticksPerMove = UnityEngine.Mathf.Max(
                UnityEngine.Mathf.RoundToInt(baseWorldTicksPerMove / (massFactor * bestSpeedFactor)),
                MinWorldTicksPerMove);
            return true;
        }

        public static bool TryApplyMoveSpeedTicksPerMove(
            List<Pawn> pawns,
            float massUsage,
            float massCapacity,
            StringBuilder explanation,
            ref int result)
        {
            if (!TryGetMoveSpeedTicksPerMove(pawns, massUsage, massCapacity, out int ticksPerMove) &&
                result >= MinWorldTicksPerMove)
            {
                return false;
            }

            int safeResult = UnityEngine.Mathf.Max(result, MinWorldTicksPerMove);
            int targetTicksPerMove = ticksPerMove > 0
                ? UnityEngine.Mathf.Min(safeResult, ticksPerMove)
                : safeResult;
            if (targetTicksPerMove == result)
            {
                return false;
            }

            result = targetTicksPerMove;

            if (explanation != null)
            {
                explanation.AppendLine();
                explanation.Append("  " + "ArtificialMaidCaravanWorldPathingLimit".Translate(
                    (60000f / result).ToString("0.#")));
            }

            return true;
        }

        private static float GetMoveSpeedFactorFromMass(float massUsage, float massCapacity)
        {
            return massCapacity <= 0f ? 1f : UnityEngine.Mathf.Lerp(2f, 1f, massUsage / massCapacity);
        }
    }

    [HarmonyPatch(typeof(MassUtility), nameof(MassUtility.Capacity))]
    public static class Patch_MassUtility_Capacity
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn p, ref float __result, StringBuilder explanation)
        {
            if (p != null && p.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                if (__result < 999999f)
                {
                    __result = 999999f;
                    if (explanation != null)
                    {
                        if (explanation.Length > 0)
                            explanation.AppendLine();
                        explanation.Append($"  - {p.LabelShortCap} (ArtificialMaid): {999999f.ToStringMassOffset()}");
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
    public static class Patch_Pawn_SpawnSetup
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn __instance, Map map)
        {
            if (map != null && __instance.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                var mapComp = ArtificialMaidMapComponent.Get(map);
                mapComp?.RegisterMaid(__instance);
                ArtificialMaidBackupCloud.NotifyMaidSpawned(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(PawnUtility), nameof(PawnUtility.ForceWait))]
    public static class Patch_PawnUtility_ForceWait
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn pawn, ref int ticks)
        {
            if (ticks <= 0 && pawn != null && pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                ticks = 1;
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.DeSpawn))]
    public static class Patch_Pawn_DeSpawn
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn __instance)
        {
            if (__instance.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                // 收纳/离开地图前强制退出高维（不传送，位置由外部流程管理），
                // 避免女仆在容器或新地图中保持高维状态
                ArtificialMaidHighDimUtility.ExitHighDim(__instance, force: true);
                ArtificialMaidBackupCloud.NotifyMaidDespawned(__instance);
                if (__instance.Map != null)
                {
                    var mapComp = ArtificialMaidMapComponent.Get(__instance.Map);
                    mapComp?.UnregisterMaid(__instance);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Discard))]
    public static class Patch_Pawn_Discard
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn __instance)
        {
            if (__instance.def == ArtificialMaidDefOf.ArtificialMaid && !__instance.Destroyed)
            {
                ArtificialMaidBackupCloud.NotifyMaidDiscarding(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Destroy))]
    public static class Patch_Pawn_Destroy
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn __instance)
        {
            if (__instance.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                ArtificialMaidBackupCloud.NotifyMaidDestroyed(__instance);
                if (__instance.Map != null)
                {
                    var mapComp = ArtificialMaidMapComponent.Get(__instance.Map);
                    mapComp?.UnregisterMaid(__instance);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), "ShouldHaveIdeo", MethodType.Getter)]
    public static class Patch_Pawn_ShouldHaveIdeo
    {
        public static void Postfix(Pawn __instance, ref bool __result)
        {
            if (__instance.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), "PreApplyDamage")]
    public static class Patch_Pawn_PreApplyDamage
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn __instance, ref DamageInfo dinfo, out bool absorbed)
        {
            absorbed = false;

            // 女仆本体：完全免疫伤害
            if (__instance.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                absorbed = true;
                return false;
            }

            // 非致命制服：已被制服的目标完全免伤，保证倒地后不会因后续攻击而意外死亡
            if (NonLethalSubdueUtility.IsSubdued(__instance))
            {
                absorbed = true;
                // 兜底：若女仆在非致命模式下仍对已制服目标发起攻击（原版倒地检查会因异种
                // canAttackWhileCrawling 等场景失效，导致攻击 Job 无法自然结束），立即终止攻击 Job，
                // 保证“攻击到目标倒地即停”，不持续站桩攻击已倒地的目标。
                if (dinfo.Instigator is Pawn maid && maid.def == ArtificialMaidDefOf.ArtificialMaid &&
                    maid.jobs != null && maid.CurJob != null &&
                    (maid.CurJob.def == JobDefOf.AttackMelee || maid.CurJob.def == JobDefOf.AttackStatic))
                {
                    CompArtificialMaid comp = CompArtificialMaid.GetCompCached(maid);
                    if (comp != null && comp.enableNonLethalMode)
                    {
                        maid.jobs.EndCurrentJob(JobCondition.Incompletable);
                    }
                }
                return false;
            }

            // 非致命制服：女仆开启非致命模式时，其攻击不造成真实伤害，而是对目标施加制服状态
            if (dinfo.Instigator is Pawn instigator && instigator.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                CompArtificialMaid comp = CompArtificialMaid.GetCompCached(instigator);
                if (comp != null && comp.enableNonLethalMode)
                {
                    NonLethalSubdueUtility.ApplySubdue(__instance);
                    absorbed = true;
                    return false;
                }
            }

            return true;
        }
    }

    // 拦截最终死亡：只修复有害健康状态，不清空全部 Hediff。
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    public static class Patch_Pawn_Kill
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn __instance)
        {
            if (__instance.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                ArtificialMaidHealthUtility.RepairHarmfulHealthConditions(__instance);
                CompArtificialMaid.GetCompCached(__instance)?.EnsureRecoveryHediff();
                Messages.Message("ArtificialMaid_RepairMessage".Translate(__instance.LabelShort), __instance,
                    MessageTypeDefOf.NeutralEvent);
                return false;
            }

            return true;
        }
    }


    // Method 2 Supplemental: Patch Corpse to trigger recovery if dead
    [HarmonyPatch(typeof(Corpse), nameof(Corpse.TickRare))]
    public static class Patch_Corpse_TickRare
    {
        [HarmonyPostfix]
        public static void Postfix(Corpse __instance)
        {
            Pawn pawn = __instance.InnerPawn;
            if (pawn != null && pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                var hediff = pawn.health.hediffSet.GetFirstHediff<Hediff_ArtificialMaidRecovery>();
                hediff?.ManualTickRare();
            }
        }
    }

    // 在原版写入倒地或死亡状态前消除有害状态，保留全部良性 Hediff。
    [HarmonyPatch(typeof(Pawn_HealthTracker), "CheckForStateChange")]
    public static class Patch_Pawn_HealthTracker_CheckForStateChange
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn_HealthTracker __instance, Pawn ___pawn)
        {
            if (___pawn != null && ___pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                // 修复过程中 RemoveHediff 会递归调用本方法；中间态不应再次结算倒地或死亡。
                if (ArtificialMaidHealthUtility.IsRepairing(___pawn))
                {
                    return false;
                }

                if (__instance.ShouldBeDead() || __instance.ShouldBeDowned())
                {
                    ArtificialMaidHealthUtility.RepairHarmfulHealthConditions(___pawn);
                    return false;
                }
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(Pawn), "CombinedDisabledWorkTags", MethodType.Getter)]
    public static class Patch_Pawn_CombinedDisabledWorkTags
    {
        public static void Postfix(Pawn __instance, ref WorkTags __result)
        {
            if (__instance.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                __result = WorkTags.None;
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetDisabledWorkTypes))]
    public static class Patch_Pawn_GetDisabledWorkTypes
    {
        public static void Postfix(Pawn __instance, List<WorkTypeDef> __result)
        {
            if (__instance.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                __result.Clear();
            }
        }
    }

    [HarmonyPatch(typeof(PawnCapacityUtility), nameof(PawnCapacityUtility.CalculateCapacityLevel))]
    public static class Patch_PawnCapacityUtility_CalculateCapacityLevel
    {
        public static void Postfix(HediffSet diffSet, ref float __result)
        {
            if (diffSet.pawn != null && diffSet.pawn.def == ArtificialMaidDefOf.ArtificialMaid && !diffSet.pawn.Dead)
            {
                if (__result < 2.0f)
                {
                    __result = 2.0f;
                }
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_GeneTracker), "AddGene", new System.Type[] { typeof(GeneDef), typeof(bool) })]
    public static class Patch_Pawn_GeneTracker_AddGene
    {
        public static bool Prefix(Pawn_GeneTracker __instance, GeneDef geneDef)
        {
            if (geneDef == ArtificialMaidDefOf.ArtificialMaid_Core)
            {
                if (__instance.pawn != null && __instance.pawn.def != ArtificialMaidDefOf.ArtificialMaid)
                {
                    return false;
                }
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(Pawn_GeneTracker), "RemoveGene")]
    public static class Patch_Pawn_GeneTracker_RemoveGene
    {
        public static bool Prefix(Pawn_GeneTracker __instance, Gene gene)
        {
            if (gene.def == ArtificialMaidDefOf.ArtificialMaid_Core)
            {
                if (__instance.pawn != null && __instance.pawn.def == ArtificialMaidDefOf.ArtificialMaid)
                {
                    return false;
                }
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(StaggerHandler), nameof(StaggerHandler.StaggerFor))]
    public static class Patch_StaggerHandler_StaggerFor
    {
        [HarmonyPrefix]
        public static bool Prefix(StaggerHandler __instance, ref bool __result)
        {
            if (__instance.parent != null && __instance.parent.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                __result = false;
                return false;
            }

            return true;
        }
    }
    
    [HarmonyPatch(typeof(Pawn_PathFollower), "CostToMoveIntoCell", typeof(Pawn), typeof(IntVec3))]
    public static class Patch_Pawn_PathFollower_CostToMoveIntoCell
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn pawn, IntVec3 c, ref float __result)
        {
            if (pawn != null && pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                // 人造人女仆移动不受地形影响
                // 直接根据是直线还是斜角移动返回基础消耗
                __result = (c.x == pawn.Position.x || c.z == pawn.Position.z)
                    ? pawn.TicksPerMoveCardinal
                    : pawn.TicksPerMoveDiagonal;

                // 处理移动紧迫性（LocomotionUrgency）
                if (pawn.CurJob != null)
                {
                    var locomotionUrgencySameAs = pawn.jobs.curDriver.locomotionUrgencySameAs;
                    if (locomotionUrgencySameAs != null && locomotionUrgencySameAs != pawn &&
                        locomotionUrgencySameAs.Spawned)
                    {
                        // 如果跟随其他 Pawn，取两者之间的最大值，保持队形
                        // 这里我们递归调用原始方法或模拟逻辑，但既然我们要“不受地形影响”，
                        // 逻辑上跟随者也应该按照自己的不受影响的速度走，或者按照被跟随者的速度走。
                        // 原版逻辑是： a = Mathf.Max(a, CostToMoveIntoCell(locomotionUrgencySameAs, c))
                        // 为了简单和符合“不受地形影响”，我们这里只处理基本的急迫性倍率。
                    }
                    else
                    {
                        switch (pawn.jobs.curJob.locomotionUrgency)
                        {
                            case LocomotionUrgency.Amble:
                                __result *= 3f;
                                if (__result < 60f) __result = 60f;
                                break;
                            case LocomotionUrgency.Walk:
                                __result *= 2f;
                                if (__result < 50f) __result = 50f;
                                break;
                            case LocomotionUrgency.Jog:
                                break;
                            case LocomotionUrgency.Sprint:
                                __result = UnityEngine.Mathf.RoundToInt(__result * 0.75f);
                                break;
                        }
                    }
                }

                __result = UnityEngine.Mathf.Max(__result, 1f);
                return false; // 跳过原方法
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(CaravanTicksPerMoveUtility), nameof(CaravanTicksPerMoveUtility.GetTicksPerMove),
        new System.Type[] { typeof(List<Pawn>), typeof(float), typeof(float), typeof(bool), typeof(StringBuilder) })]
    public static class Patch_CaravanTicksPerMoveUtility_GetTicksPerMove
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(
            List<Pawn> pawns,
            float massUsage,
            float massCapacity,
            bool isShuttle,
            StringBuilder explanation,
            ref int __result)
        {
            if (isShuttle || !ArtificialMaidCaravanUtility.ContainsArtificialMaid(pawns))
            {
                return;
            }

            ArtificialMaidCaravanUtility.TryApplyMoveSpeedTicksPerMove(
                pawns,
                massUsage,
                massCapacity,
                explanation,
                ref __result);
        }
    }

    // Caravan Speed 已修复此问题
    // [HarmonyPatch(typeof(CaravanTicksPerMoveUtility), nameof(CaravanTicksPerMoveUtility.GetTicksPerMove),
    //     new System.Type[] { typeof(Caravan), typeof(StringBuilder) })]
    // [HarmonyAfter("rimworld.ktk_CaravanSpeedPatch")]
    // public static class Patch_CaravanTicksPerMoveUtility_GetTicksPerMove_Caravan
    // {
    //     [HarmonyPostfix]
    //     [HarmonyPriority(Priority.Last)]
    //     public static void Postfix(Caravan caravan, StringBuilder explanation, ref int __result)
    //     {
    //         if (caravan == null || caravan.Shuttle != null || !ArtificialMaidCaravanUtility.ContainsArtificialMaid(caravan))
    //         {
    //             return;
    //         }
    //
    //         // 兼容 Caravan Speed Patch：它会按当前 MoveSpeed 再次缩放，极端速度下可能把结果压到 0。
    //         ArtificialMaidCaravanUtility.TryApplyMoveSpeedTicksPerMove(
    //             caravan.PawnsListForReading,
    //             caravan.MassUsage,
    //             caravan.MassCapacity,
    //             explanation,
    //             ref __result);
    //     }
    // }

    [HarmonyPatch(typeof(WorldPathing), nameof(WorldPathing.FindPath))]
    public static class Patch_WorldPathing_FindPath
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            var roadMethod = AccessTools.Method(typeof(WorldGrid), nameof(WorldGrid.GetRoadMovementDifficultyMultiplier),
                new System.Type[] { typeof(PlanetTile), typeof(PlanetTile), typeof(StringBuilder) });
            var terrainMethod = AccessTools.Method(typeof(Patch_WorldPathing_FindPath),
                nameof(ArtificialMaidMovementDifficulty));
            var roadReplacementMethod = AccessTools.Method(typeof(Patch_WorldPathing_FindPath),
                nameof(ArtificialMaidRoadMovementDifficultyMultiplier));

            bool patchedTerrain = false;
            bool patchedRoad = false;

            for (int i = 0; i < codes.Count; i++)
            {
                if (!patchedRoad && codes[i].Calls(roadMethod))
                {
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (codes[j].opcode == OpCodes.Ldelem_R4)
                        {
                            codes.Insert(j + 1, new CodeInstruction(OpCodes.Ldarg_3));
                            codes.Insert(j + 2, new CodeInstruction(OpCodes.Call, terrainMethod));
                            i += 2;
                            patchedTerrain = true;
                            break;
                        }
                    }

                    CodeInstruction loadCaravan = new CodeInstruction(OpCodes.Ldarg_3);
                    loadCaravan.labels.AddRange(codes[i].labels);
                    codes[i].labels.Clear();
                    codes.Insert(i, loadCaravan);
                    codes[i + 1].operand = roadReplacementMethod;
                    patchedRoad = true;
                    i++;
                }
            }

            if (!patchedTerrain || !patchedRoad)
            {
                Log.Warning("[OuterrealmTechRobot] Failed to patch WorldPathing.FindPath movement cost.");
            }

            return codes;
        }

        private static float ArtificialMaidMovementDifficulty(float originalDifficulty, Caravan caravan)
        {
            return ArtificialMaidCaravanUtility.IgnoreWorldPathCosts ||
                   ArtificialMaidCaravanUtility.ContainsArtificialMaid(caravan)
                ? 1f
                : originalDifficulty;
        }

        private static float ArtificialMaidRoadMovementDifficultyMultiplier(
            WorldGrid grid,
            PlanetTile fromTile,
            PlanetTile toTile,
            StringBuilder explanation,
            Caravan caravan)
        {
            return ArtificialMaidCaravanUtility.IgnoreWorldPathCosts ||
                   ArtificialMaidCaravanUtility.ContainsArtificialMaid(caravan)
                ? 1f
                : grid.GetRoadMovementDifficultyMultiplier(fromTile, toTile, explanation);
        }
    }

    [HarmonyPatch(typeof(WorldRoutePlanner), "RecreatePaths")]
    public static class Patch_WorldRoutePlanner_RecreatePaths
    {
        private static readonly AccessTools.FieldRef<WorldRoutePlanner, Dialog_FormCaravan> CurrentFormCaravanDialogRef =
            AccessTools.FieldRefAccess<WorldRoutePlanner, Dialog_FormCaravan>("currentFormCaravanDialog");

        private static readonly AccessTools.FieldRef<WorldRoutePlanner, CaravanTicksPerMoveUtility.CaravanInfo?>
            CaravanInfoFromFormCaravanDialogRef =
                AccessTools.FieldRefAccess<WorldRoutePlanner, CaravanTicksPerMoveUtility.CaravanInfo?>(
                    "caravanInfoFromFormCaravanDialog");

        [HarmonyPrefix]
        public static void Prefix(WorldRoutePlanner __instance, out bool __state)
        {
            ArtificialMaidCaravanUtility.PushIgnoreWorldPathCosts(ShouldIgnoreWorldPathCosts(__instance), out __state);
        }

        [HarmonyPostfix]
        public static void Postfix(bool __state)
        {
            ArtificialMaidCaravanUtility.PopIgnoreWorldPathCosts(__state);
        }

        private static bool ShouldIgnoreWorldPathCosts(WorldRoutePlanner planner)
        {
            if (planner == null)
            {
                return false;
            }

            if (CurrentFormCaravanDialogRef(planner) != null)
            {
                CaravanTicksPerMoveUtility.CaravanInfo? caravanInfo = CaravanInfoFromFormCaravanDialogRef(planner);
                return caravanInfo.HasValue &&
                       ArtificialMaidCaravanUtility.ContainsArtificialMaid(caravanInfo.Value.pawns);
            }

            if (planner.waypoints.NullOrEmpty())
            {
                return false;
            }

            Caravan caravan = Find.WorldObjects.PlayerControlledCaravanAt(planner.waypoints[0].Tile);
            return ArtificialMaidCaravanUtility.ContainsArtificialMaid(caravan);
        }
    }

    [HarmonyPatch(typeof(Dialog_FormCaravan), "get_DaysWorthOfFood")]
    public static class Patch_Dialog_FormCaravan_DaysWorthOfFood
    {
        [HarmonyPrefix]
        public static void Prefix(Dialog_FormCaravan __instance, out bool __state)
        {
            ArtificialMaidCaravanUtility.PushIgnoreWorldPathCosts(
                ArtificialMaidCaravanUtility.ContainsArtificialMaid(__instance.transferables), out __state);
        }

        [HarmonyPostfix]
        public static void Postfix(bool __state)
        {
            ArtificialMaidCaravanUtility.PopIgnoreWorldPathCosts(__state);
        }
    }

    [HarmonyPatch(typeof(Dialog_FormCaravan), "get_TicksToArrive")]
    public static class Patch_Dialog_FormCaravan_TicksToArrive
    {
        [HarmonyPrefix]
        public static void Prefix(Dialog_FormCaravan __instance, out bool __state)
        {
            ArtificialMaidCaravanUtility.PushIgnoreWorldPathCosts(
                ArtificialMaidCaravanUtility.ContainsArtificialMaid(__instance.transferables), out __state);
        }

        [HarmonyPostfix]
        public static void Postfix(bool __state)
        {
            ArtificialMaidCaravanUtility.PopIgnoreWorldPathCosts(__state);
        }
    }

    [HarmonyPatch(typeof(Dialog_FormCaravan), "SelectApproximateBestTravelSupplies")]
    public static class Patch_Dialog_FormCaravan_SelectApproximateBestTravelSupplies
    {
        [HarmonyPrefix]
        public static void Prefix(Dialog_FormCaravan __instance, out bool __state)
        {
            ArtificialMaidCaravanUtility.PushIgnoreWorldPathCosts(
                ArtificialMaidCaravanUtility.ContainsArtificialMaid(__instance.transferables), out __state);
        }

        [HarmonyPostfix]
        public static void Postfix(bool __state)
        {
            ArtificialMaidCaravanUtility.PopIgnoreWorldPathCosts(__state);
        }
    }

    [HarmonyPatch(typeof(Caravan_PathFollower), nameof(Caravan_PathFollower.CostToMove),
        new System.Type[] { typeof(Caravan), typeof(PlanetTile), typeof(PlanetTile), typeof(int?) })]
    public static class Patch_Caravan_PathFollower_CostToMove_Caravan
    {
        [HarmonyPrefix]
        public static bool Prefix(Caravan caravan, PlanetTile start, PlanetTile end, ref int __result)
        {
            if (!ArtificialMaidCaravanUtility.ContainsArtificialMaid(caravan))
            {
                return true;
            }

            // 与世界寻路保持一致：人造人女仆远行队不受世界地形和道路移动倍率影响。
            __result = start == end ? 0 : UnityEngine.Mathf.Clamp(caravan.TicksPerMove, 1, 30000);
            return false;
        }
    }

    [HarmonyPatch(typeof(ThoughtHandler), nameof(ThoughtHandler.GetAllMoodThoughts))]
    public static class Patch_ThoughtHandler_GetAllMoodThoughts
    {
        public static void Postfix(ThoughtHandler __instance, List<Thought> outThoughts)
        {
            if (__instance.pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                outThoughts.RemoveAll(t => t.MoodOffset() < 0);
            }
        }
    }

    [HarmonyPatch(typeof(DamageWorker_Extinguish), nameof(DamageWorker_Extinguish.Apply))]
    public static class Patch_DamageWorker_Extinguish_Apply
    {
        [HarmonyPrefix]
        public static void Prefix(ref DamageInfo dinfo)
        {
            if (dinfo.Instigator is Pawn pawn && pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                // 人造人女仆灭火速度极大提升
                dinfo.SetAmount(999999f);
            }
        }
    }

    [HarmonyPatch(typeof(JobDriver_Wear), nameof(JobDriver_Wear.Notify_Starting))]
    public static class Patch_JobDriver_Wear_Notify_Starting
    {
        private static readonly AccessTools.FieldRef<JobDriver_Wear, int> DurationRef =
            AccessTools.FieldRefAccess<JobDriver_Wear, int>("duration");

        [HarmonyPostfix]
        public static void Postfix(JobDriver_Wear __instance)
        {
            if (__instance.pawn != null && __instance.pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                // 穿戴物品时间缩短为 1 tick
                DurationRef(__instance) = 1;
            }
        }
    }

    [HarmonyPatch(typeof(JobDriver_RemoveApparel), nameof(JobDriver_RemoveApparel.Notify_Starting))]
    public static class Patch_JobDriver_RemoveApparel_Notify_Starting
    {
        private static readonly AccessTools.FieldRef<JobDriver_RemoveApparel, int> DurationRef =
            AccessTools.FieldRefAccess<JobDriver_RemoveApparel, int>("duration");

        [HarmonyPostfix]
        public static void Postfix(JobDriver_RemoveApparel __instance)
        {
            if (__instance.pawn != null && __instance.pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                // 脱掉物品时间缩短为 1 tick
                DurationRef(__instance) = 1;
            }
        }
    }

    [HarmonyPatch(typeof(JobDriver_TakeInventory), nameof(JobDriver_TakeInventory.TryMakePreToilReservations))]
    public static class Patch_JobDriver_TakeInventory_TryMakePreToilReservations
    {
        [HarmonyPostfix]
        public static void Postfix(JobDriver_TakeInventory __instance)
        {
            if (__instance.pawn != null && __instance.pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                // 拾取物品延迟缩短为 0
                if (__instance.job != null)
                {
                    __instance.job.takeInventoryDelay = 0;
                }
            }
        }
    }

    [HarmonyPatch(typeof(StatWorker), nameof(StatWorker.GetValueUnfinalized))]
    public static class Patch_StatWorker_GetValueUnfinalized
    {
        private static readonly AccessTools.FieldRef<StatWorker, StatDef> StatRef =
            AccessTools.FieldRefAccess<StatWorker, StatDef>("stat");

        [HarmonyPostfix]
        public static void Postfix(StatWorker __instance, StatRequest req, ref float __result)
        {
            if (req.HasThing && req.Thing is Pawn pawn && pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                StatDef stat = StatRef(__instance);
                if (stat == null) return;

                string defName = stat.defName;
                if (defName == "EntityStudyRate" ||
                    defName == "StudyEfficiency" ||
                    defName == "ActivitySuppressionRate" ||
                    defName == "PsychicRitualQuality" ||
                    defName == "PsychicRitualQualityOffset")
                {
                    __result *= 1000000f;
                }
            }
        }
    }

    [HarmonyPatch(typeof(StudyManager), nameof(StudyManager.Study))]
    public static class Patch_StudyManager_Study
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn studier, ref float studyAmount)
        {
            if (studier != null && studier.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                studyAmount *= 1000000f;
            }
        }
    }

    [HarmonyPatch(typeof(StudyManager), nameof(StudyManager.StudyAnomaly))]
    public static class Patch_StudyManager_StudyAnomaly
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn studier, ref float knowledgeAmount)
        {
            if (studier != null && studier.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                knowledgeAmount *= 1000000f;
            }
        }
    }

    [HarmonyPatch(typeof(PsychicRitualToil_InvokeHorax), nameof(PsychicRitualToil_InvokeHorax.Start))]
    public static class Patch_PsychicRitualToil_InvokeHorax_Start
    {
        [HarmonyPrefix]
        public static void Prefix(PsychicRitualToil_InvokeHorax __instance, PsychicRitual psychicRitual)
        {
            Pawn invoker = psychicRitual.assignments.FirstAssignedPawn(__instance.invokerRole);
            if (invoker != null && invoker.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                __instance.hoursUntilOutcome *= 0.000001f;
                __instance.hoursUntilHoraxEffect *= 0.000001f;
            }
        }
    }

    [HarmonyPatch(typeof(TraitSet), nameof(TraitSet.GainTrait))]
    public static class Patch_TraitSet_GainTrait_ExclusiveCheck
    {
        private static readonly AccessTools.FieldRef<TraitSet, Pawn> PawnField =
            AccessTools.FieldRefAccess<TraitSet, Pawn>("pawn");

        [HarmonyPrefix]
        public static bool Prefix(TraitSet __instance, Trait trait)
        {
            if (trait?.def == ArtificialMaidDefOf.ArtificialMaidTrait_MasterProtocol)
            {
                Pawn pawn = PawnField(__instance);
                if (pawn == null) return true;

                bool isMaid = pawn.def == ArtificialMaidDefOf.ArtificialMaid || pawn.GetComp<CompArtificialMaid>() != null;
                bool isPlayerFaction = pawn.Faction == Faction.OfPlayer;

                if (!isMaid || !isPlayerFaction)
                {
                    return false; // 拦截非法持有
                }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
    public static class Patch_Pawn_SpawnSetup_ExclusiveTraitCheck
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn __instance)
        {
            if (__instance.story?.traits != null)
            {
                var traitDef = ArtificialMaidDefOf.ArtificialMaidTrait_MasterProtocol;
                if (traitDef != null && __instance.story.traits.HasTrait(traitDef))
                {
                    bool isMaid = __instance.def == ArtificialMaidDefOf.ArtificialMaid || __instance.GetComp<CompArtificialMaid>() != null;
                    bool isPlayerFaction = __instance.Faction == Faction.OfPlayer;

                    if (!isMaid || !isPlayerFaction)
                    {
                        var trait = __instance.story.traits.GetTrait(traitDef);
                        if (trait != null)
                        {
                            __instance.story.traits.RemoveTrait(trait);
                        }
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(KidnapAIUtility), nameof(KidnapAIUtility.TryFindGoodKidnapVictim))]
    public static class Patch_KidnapAIUtility_TryFindGoodKidnapVictim
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result, ref Pawn victim)
        {
            if (__result && victim != null && victim.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                victim = null;
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(KidnapAIUtility), nameof(KidnapAIUtility.ReachableWoundedGuest))]
    public static class Patch_KidnapAIUtility_ReachableWoundedGuest
    {
        [HarmonyPostfix]
        public static void Postfix(ref Pawn __result)
        {
            if (__result != null && __result.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                __result = null;
            }
        }
    }

    [HarmonyPatch(typeof(KidnappedPawnsTracker), nameof(KidnappedPawnsTracker.Kidnap))]
    public static class Patch_KidnappedPawnsTracker_Kidnap
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn pawn)
        {
            if (pawn != null && pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(CompDevourer), nameof(CompDevourer.StartDigesting))]
    public static class Patch_CompDevourer_StartDigesting
    {
        [HarmonyPostfix]
        public static void Postfix(CompDevourer __instance, LocalTargetInfo target)
        {
            if (target.Pawn != null && target.Pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                __instance.Pawn?.Kill(null);
            }
        }
    }

    [HarmonyPatch(typeof(JobDriver_Mine), "DoDamage")]
    public static class Patch_JobDriver_Mine_DoDamage
    {
        [HarmonyPrefix]
        public static bool Prefix(Thing target, Toil mine, Pawn actor, IntVec3 mineablePos)
        {
            if (actor == null || actor.def != ArtificialMaidDefOf.ArtificialMaid)
            {
                return true;
            }

            if (!(target is Mineable mineable))
            {
                DamageInfo damageInfo = new DamageInfo(
                    DamageDefOf.Mining,
                    UnityEngine.Mathf.Max(1, target.HitPoints),
                    instigator: mine.actor);
                target.TakeDamage(damageInfo);
                return false;
            }

            // 直接执行原版最终一击流程，完整结算挖掘产量。
            Map map = actor.Map;
            bool mineVein = map.designationManager.DesignationAt(
                mineable.Position,
                DesignationDefOf.MineVein) != null;

            mineable.Notify_TookMiningDamage(target.HitPoints, actor);
            mineable.HitPoints = 0;
            mineable.DestroyMined(actor);

            if (mineVein)
            {
                foreach (IntVec3 adjacentCell in GenAdj.AdjacentCells)
                {
                    Designator_MineVein.FloodFillDesignations(
                        mineablePos + adjacentCell,
                        map,
                        mineable.def);
                }
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(JobDriver_ActivateMonolith), "MakeNewToils")]
    public static class Patch_JobDriver_ActivateMonolith_MakeNewToils
    {
        [HarmonyPostfix]
        public static void Postfix(JobDriver_ActivateMonolith __instance, ref IEnumerable<Toil> __result)
        {
            if (__instance.pawn != null && __instance.pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                __result = ModifyToils(__result);
            }
        }

        private static IEnumerable<Toil> ModifyToils(IEnumerable<Toil> toils)
        {
            foreach (var toil in toils)
            {
                if (toil.defaultDuration > 1)
                {
                    toil.defaultDuration = 1;
                }
                yield return toil;
            }
        }
    }

    [HarmonyPatch(typeof(JobDriver_InteractThing), "MakeNewToils")]
    public static class Patch_JobDriver_InteractThing_MakeNewToils
    {
        [HarmonyPostfix]
        public static void Postfix(JobDriver_InteractThing __instance, ref IEnumerable<Toil> __result)
        {
            if (__instance.pawn != null && __instance.pawn.def == ArtificialMaidDefOf.ArtificialMaid)
            {
                __result = ModifyToils(__result);
            }
        }

        private static IEnumerable<Toil> ModifyToils(IEnumerable<Toil> toils)
        {
            foreach (var toil in toils)
            {
                if (toil.defaultDuration > 10)
                {
                    toil.defaultDuration = 10;
                }
                yield return toil;
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.DynamicDrawPhaseAt))]
    public static class Patch_Pawn_DynamicDrawPhaseAt
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn __instance)
        {
            // 性能优化：首先进行快速的 def 检查，避免所有非女仆 Pawn 进入组件查找逻辑
            if (__instance.def != ArtificialMaidDefOf.ArtificialMaid)
            {
                return true;
            }

            // 如果女仆正处于工作检测的伪造状态中，则跳过本次绘制（防止在 InteractionCell 产生闪烁）
            // 注意：由于展示柜内部是通过直接调用 PawnRenderer 绘制的，因此不会受到此拦截的影响
            var comp = CompArtificialMaid.GetCompCached(__instance);
            if (comp != null && comp.isFaking)
            {
                return false;
            }
            return true;
        }
    }
}
