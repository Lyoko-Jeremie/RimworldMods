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
        public static ThoughtDef MaidEmotionalSupport;
        public static HediffDef ArtificialMaidRecovery;
        public static ThingDef ArtificialMaidDisplayCase;
        public static JobDef EnterDisplayCase;

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
    }
}