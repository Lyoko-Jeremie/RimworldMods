using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// Omni机械体修复立场的配置。
    /// </summary>
    public class CompProperties_OmniMechRepairField : CompProperties
    {
        public bool repairWithinShieldByDefault = true;
        public bool repairWholeMapByDefault;
        public int checkIntervalTicks = 250;

        public CompProperties_OmniMechRepairField()
        {
            compClass = typeof(CompOmniMechRepairField);
        }
    }

    [StaticConstructorOnStartup]
    public static class OmniMechRepairFieldTex
    {
        public static readonly Texture2D IconRepairWithinShield =
            ContentFinder<Texture2D>.Get("UI/Commands/UltimateAutoRepair_Toggle", false)
            ?? ContentFinder<Texture2D>.Get("UI/Gizmos/AutoRepair", false)
            ?? BaseContent.WhiteTex;

        public static readonly Texture2D IconRepairWholeMap =
            ContentFinder<Texture2D>.Get("UI/Commands/UltimateAutoRepair_SelectArea", false)
            ?? ContentFinder<Texture2D>.Get("UI/Gizmos/AutoRepair", false)
            ?? BaseContent.WhiteTex;
    }

    /// <summary>
    /// 依附于全能护盾的机械体修复立场。
    /// 开启范围模式时立即修复护盾半径内的我方机械体；开启全图模式时立即修复本图全部我方机械体。
    /// </summary>
    public class CompOmniMechRepairField : ThingComp
    {
        private bool repairWithinShield;
        private bool repairWholeMap;
        private bool initialized;
        private CompOmniProjectileInterceptor shieldComp;

        public CompProperties_OmniMechRepairField Props =>
            (CompProperties_OmniMechRepairField)props;

        public bool RepairWithinShield => repairWithinShield;

        public bool RepairWholeMap => repairWholeMap;

        public override void PostPostMake()
        {
            base.PostPostMake();
            InitializeDefaultsIfNeeded();
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            shieldComp = parent.TryGetComp<CompOmniProjectileInterceptor>();
            InitializeDefaultsIfNeeded();
            RepairEligibleMechs();
        }

        public override void CompTick()
        {
            base.CompTick();

            if (!repairWithinShield && !repairWholeMap)
            {
                return;
            }

            // 立即完成一次完整维修，但将目标扫描错开到短间隔执行，避免每 tick 为每台机械体遍历 Hediff。
            int interval = Props.checkIntervalTicks > 0 ? Props.checkIntervalTicks : 1;
            if (!parent.IsHashIntervalTick(interval))
            {
                return;
            }

            RepairEligibleMechs();
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            yield return new Command_Toggle
            {
                defaultLabel = "OmniMechRepairField_RepairWithinShield".Translate(),
                defaultDesc = "OmniMechRepairField_RepairWithinShieldDesc".Translate(),
                icon = OmniMechRepairFieldTex.IconRepairWithinShield,
                isActive = () => repairWithinShield,
                toggleAction = delegate
                {
                    repairWithinShield = !repairWithinShield;
                    if (repairWithinShield)
                    {
                        RepairEligibleMechs();
                    }
                }
            };

            yield return new Command_Toggle
            {
                defaultLabel = "OmniMechRepairField_RepairWholeMap".Translate(),
                defaultDesc = "OmniMechRepairField_RepairWholeMapDesc".Translate(),
                icon = OmniMechRepairFieldTex.IconRepairWholeMap,
                isActive = () => repairWholeMap,
                toggleAction = delegate
                {
                    repairWholeMap = !repairWholeMap;
                    if (repairWholeMap)
                    {
                        RepairEligibleMechs();
                    }
                }
            };
        }

        public override string CompInspectStringExtra()
        {
            if (repairWholeMap)
            {
                return "OmniMechRepairField_InspectWholeMap".Translate();
            }

            return repairWithinShield
                ? (string)"OmniMechRepairField_InspectWithinShield".Translate()
                : (string)"OmniMechRepairField_InspectDisabled".Translate();
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref repairWithinShield, "repairWithinShield");
            Scribe_Values.Look(ref repairWholeMap, "repairWholeMap");
            Scribe_Values.Look(ref initialized, "initialized");

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                InitializeDefaultsIfNeeded();
            }
        }

        private void InitializeDefaultsIfNeeded()
        {
            if (initialized)
            {
                return;
            }

            repairWithinShield = Props.repairWithinShieldByDefault;
            repairWholeMap = Props.repairWholeMapByDefault;
            initialized = true;
        }

        private void RepairEligibleMechs()
        {
            Map map = parent.Map;
            if (map == null || !parent.Spawned || !ModsConfig.BiotechActive)
            {
                return;
            }

            if (shieldComp == null)
            {
                shieldComp = parent.TryGetComp<CompOmniProjectileInterceptor>();
            }

            // 范围模式必须能读取宿主护盾的实际半径；全图模式不依赖护盾组件。
            bool canRepairWithinShield = repairWithinShield && shieldComp != null;
            if (!repairWholeMap && !canRepairWithinShield)
            {
                return;
            }

            List<Pawn> pawns = map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
            int count = pawns.Count;
            for (int i = 0; i < count; i++)
            {
                Pawn mech = pawns[i];
                if (mech == null || !mech.Spawned || mech.Dead || !mech.IsColonyMech)
                {
                    continue;
                }

                if (!repairWholeMap && !shieldComp.IsCellInside(mech.Position))
                {
                    continue;
                }

                RepairImmediately(mech);
            }
        }

        private static void RepairImmediately(Pawn mech)
        {
            if (!MechRepairUtility.CanRepair(mech))
            {
                return;
            }

            // 每次原版 RepairTick 只处理一个伤势、一个缺失部件或一件丢失武器。
            // 以现有 Hediff 数量加一次武器恢复作为上限，既能立即完全维修，也能防止异常机械体无限循环。
            int maxRepairSteps = mech.health.hediffSet.hediffs.Count + 1;
            for (int i = 0; i < maxRepairSteps && MechRepairUtility.CanRepair(mech); i++)
            {
#pragma warning disable CS0612 // RimWorld 1.6 将该方法标为过时，但它仍是原版统一处理机械体伤势、缺失部件和武器恢复的入口。
                MechRepairUtility.RepairTick(mech, int.MaxValue);
#pragma warning restore CS0612
            }
        }
    }
}
