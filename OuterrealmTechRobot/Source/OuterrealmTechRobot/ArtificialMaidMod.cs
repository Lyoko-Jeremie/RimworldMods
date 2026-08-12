using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace OuterrealmTechRobot
{
    [DefOf]
    public static class ArtificialMaidDefOf
    {
        public static ThingDef ArtificialMaid;
        public static TraitDef ArtificialMaidTrait_EmotionalSynchrony;
        public static TraitDef ArtificialMaidTrait_MasterProtocol;
        public static ThoughtDef MaidEmotionalSupport;
        public static ThoughtDef ArtificialMaidMasterProtocol_Mood;
        public static HediffDef ArtificialMaidRecovery;
        public static HediffDef ArtificialMaidNonLethalSubdue;
        public static TraitDef ArtificialMaidTrait_Subdued;
        public static ThoughtDef ArtificialMaidSubdued_Mood;
        public static HediffDef ArtificialMaidAura;
        public static ThoughtDef ArtificialMaidAura_Mood;
        public static ThingDef ArtificialMaidDisplayCase;
        public static JobDef EnterDisplayCase;
        public static ThingDef ArtificialMaidTransportBox;
        public static JobDef PackArtificialMaid;
        public static JobDef UnpackArtificialMaid;
        public static JobDef DismissArtificialMaidEscort;
        public static PathGridDef AM_HighDimPathGrid;

        // —— 侍奉系统（Servitude）——
        public static JobDef AM_Job_FollowMaster;
        public static JobDef AM_Job_FeedMaster;
        public static JobDef AM_Job_LapPillow;
        public static ThoughtDef AM_Thought_HasServant;
        public static ThinkTreeDef AM_ServitudeAI;

        [MayRequireBiotech] public static XenotypeDef ArtificialMaidXenotype;
        [MayRequireBiotech] public static GeneDef ArtificialMaid_Core;
    }

    [StaticConstructorOnStartup]
    public static class ArtificialMaidMod
    {
        public static BackstoryDef MaidChildhood;
        public static BackstoryDef MaidAdulthood;

        static ArtificialMaidMod()
        {
            var harmony = new Harmony("Jeremie.Outerrealm.Tech.ArtificialMaid");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            DSFICompatibility.Initialize(harmony);

            LongEventHandler.ExecuteWhenFinished(() =>
            {
                foreach (var b in DefDatabase<BackstoryDef>.AllDefs)
                {
                    if (b.slot == BackstorySlot.Childhood && b.spawnCategories != null)
                    {
                        bool found = false;
                        for (int i = 0; i < b.spawnCategories.Count; i++)
                        {
                            if (b.spawnCategories[i] == "ArtificialMaidBackstory")
                            {
                                found = true;
                                break;
                            }
                        }

                        if (found)
                        {
                            MaidChildhood = b;
                            break;
                        }
                    }
                }

                foreach (var b in DefDatabase<BackstoryDef>.AllDefs)
                {
                    if (b.slot == BackstorySlot.Adulthood && b.spawnCategories != null)
                    {
                        bool found = false;
                        for (int i = 0; i < b.spawnCategories.Count; i++)
                        {
                            if (b.spawnCategories[i] == "ArtificialMaidBackstory")
                            {
                                found = true;
                                break;
                            }
                        }

                        if (found)
                        {
                            MaidAdulthood = b;
                            break;
                        }
                    }
                }
            });

            Log.Message("ArtificialMaidMod initialized.");
        }
    }

    [StaticConstructorOnStartup]
    public static class ArtificialMaidTex
    {
        public static readonly Color PaleSkinColor = new Color(250f / 255f, 240f / 255f, 240f / 255f);

        public static readonly Texture2D IconModifyMaid =
            ContentFinder<Texture2D>.Get("UI/Commands/ArtificialMaidTerminal_ModifyMaid", false) ??
            BaseContent.WhiteTex;

        public static readonly Texture2D IconAutoHibernate =
            ContentFinder<Texture2D>.Get("UI/Commands/ArtificialMaidTerminal_AutoHibernate", false) ?? 
            BaseContent.WhiteTex;

        public static readonly Texture2D IconImmediateHibernate =
            ContentFinder<Texture2D>.Get("UI/Commands/ArtificialMaidTerminal_ImmediateHibernate", false) ??
            IconAutoHibernate;

        public static readonly Texture2D IconHealingProtocol =
            ContentFinder<Texture2D>.Get("UI/Commands/ArtificialMaidTerminal_HealingProtocol", false) ??
            BaseContent.WhiteTex;

        public static readonly Texture2D IconAutoWake =
            ContentFinder<Texture2D>.Get("UI/Commands/ArtificialMaidTerminal_AutoWake", false) ?? 
            BaseContent.WhiteTex;

        public static readonly Texture2D IconImmediateWake =
            ContentFinder<Texture2D>.Get("UI/Commands/ArtificialMaidTerminal_ImmediateWake", false) ??
            IconAutoWake;

        public static readonly Texture2D IconPodEject =
            ContentFinder<Texture2D>.Get("UI/Commands/ArtificialMaidTerminal_PodEject", false) ??
            BaseContent.WhiteTex;

        public static readonly Texture2D IconHuntMode =
            ContentFinder<Texture2D>.Get("UI/Commands/ArtificialMaidTerminal_HuntMode", false) ??
            BaseContent.WhiteTex;

        public static readonly Texture2D IconBackupCloud =
            ContentFinder<Texture2D>.Get("UI/Commands/ArtificialMaidTerminal_BackupCloud", false) ??
            IconModifyMaid;

        public static readonly Texture2D IconAura =
            ContentFinder<Texture2D>.Get("UI/Commands/ArtificialMaidTerminal_ModifyMaid", false) ??
            BaseContent.WhiteTex;

        public static readonly Texture2D IconNonLethalMode =
            ContentFinder<Texture2D>.Get("UI/Commands/ArtificialMaidTerminal_NonLethalMode", false) ??
            IconHuntMode;

        public static readonly Texture2D IconReleaseSubdued =
            ContentFinder<Texture2D>.Get("UI/Commands/ArtificialMaidTerminal_ReleaseSubdued", false) ??
            IconHealingProtocol;

        public static readonly Texture2D IconHighDim =
            ContentFinder<Texture2D>.Get("UI/Commands/ArtificialMaidTerminal_HighDim", false) ??
            BaseContent.WhiteTex;

        // —— 侍奉系统（Servitude）——
        public static readonly Texture2D IconServitudeBond =
            ContentFinder<Texture2D>.Get("UI/Commands/ServitudeBond", false) ??
            BaseContent.WhiteTex;

        public static readonly Texture2D IconStandby =
            ContentFinder<Texture2D>.Get("UI/Commands/ArtificialMaidTerminal_Standby", false) ??
            IconAutoHibernate;
    }
}
