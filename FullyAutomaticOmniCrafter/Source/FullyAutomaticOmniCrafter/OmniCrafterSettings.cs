using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.Sound;
using UnityEngine;

namespace FullyAutomaticOmniCrafter
{
    // ─── Global Settings (cross-save favorites) ───────────────────────────────
    public class OmniCrafterSettings : ModSettings
    {
        public List<string> globalFavorites = new List<string>();

        /// <summary>
        /// 是否启用拼音搜索（支持全拼和首字母缩写）。
        /// 可在搜索栏旁的"拼"按钮中切换，也可在 Mod 设置页面切换。
        /// </summary>
        public bool enablePinyinSearch = false;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref globalFavorites, "globalFavorites", LookMode.Value);
            if (globalFavorites == null) globalFavorites = new List<string>();
            Scribe_Values.Look(ref enablePinyinSearch, "enablePinyinSearch", false);
        }
    }

    public class OmniCrafterMod : Mod
    {
        public static OmniCrafterSettings Settings;

        public OmniCrafterMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<OmniCrafterSettings>();

            HarmonyLib.Harmony harmony = new HarmonyLib.Harmony("Jeremie.Fully.Automatic.OmniCrafter");
            harmony.PatchAll();
        }

        public override string SettingsCategory() => "FullyAutomaticOmniCrafter";
    }

}
