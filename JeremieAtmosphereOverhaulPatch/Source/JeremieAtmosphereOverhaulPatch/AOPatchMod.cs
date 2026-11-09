using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Api;
using Jeremie.AtmosphereOverhaulPatch.Compat;
using Jeremie.AtmosphereOverhaulPatch.Util;
using RimWorld;
using UnityEngine;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch
{
    /// <summary>
    /// Mod 入口。
    ///
    /// 关键工程约定（与超维科技主 Mod 一致）：
    /// 构造函数里只建 Harmony 实例、读取设置，**绝不安装补丁**。
    /// 此时 DefDatabase 尚未填充，Harmony 建立 detour 会提前触发被 patch 类型的静态构造，
    /// 把其 DefDatabase&lt;T&gt;.GetNamed 字段永久钉成 null。
    /// 补丁安装全部延后到 AOPatchBootstrap（StaticConstructorOnStartup）。
    /// </summary>
    public class AOPatchMod : Mod
    {
        public const string HarmonyId = "Jeremie.AtmosphereOverhaulPatch";

        public static AOPatchMod Instance;
        public static Harmony HarmonyInstance;
        public static AOPatchSettings Settings;

        public AOPatchMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<AOPatchSettings>();
            HarmonyInstance = new Harmony(HarmonyId);

            PatchLog.Info("实例已创建；补丁安装延后到 StaticConstructorOnStartup。");
        }

        public override string SettingsCategory()
        {
            return "AOPatch_SettingsCategory".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard list = new Listing_Standard();
            list.Begin(inRect);

            list.Label("AOPatch_Intro".Translate());
            list.GapLine();

            IReadOnlyList<CompatModuleInfo> modules = CompatRegistry.AllModules;
            bool changed = false;

            bool masterBefore = Settings.MasterEnabled;
            list.CheckboxLabeled(
                "AOPatch_Master".Translate(),
                ref Settings.MasterEnabled,
                "AOPatch_MasterDesc".Translate());
            if (masterBefore != Settings.MasterEnabled)
            {
                changed = true;
            }

            list.GapLine();
            list.Label("AOPatch_TargetsHeader".Translate());

            for (int i = 0; i < modules.Count; i++)
            {
                CompatModuleInfo module = modules[i];
                bool enabled = module.EnabledByUser;

                string state = ("AOPatch_State_" + module.State).Translate();
                string label = module.LabelKey.Translate() + " [" + state + "]";

                list.CheckboxLabeled(label, ref enabled, module.HintKey.Translate());
                if (enabled != module.EnabledByUser)
                {
                    module.SetEnabledByUser(enabled);
                    changed = true;
                }

                if (!module.Loaded)
                {
                    // 目标 Mod 不在时兼容 DLL 不会被加载；明确告诉玩家原因
                    list.Label("    " + "AOPatch_CompatNotLoaded".Translate(module.RelativeFolder), -1f);
                }
                else if (!string.IsNullOrEmpty(module.Detail))
                {
                    list.Label("    " + module.Detail, -1f);
                }
            }

            list.GapLine();

            list.CheckboxLabeled(
                "AOPatch_VerboseLogging".Translate(),
                ref Settings.VerboseLogging,
                "AOPatch_VerboseLoggingDesc".Translate());

            if (changed)
            {
                list.Label("AOPatch_NeedReapply".Translate());
            }

            if (list.ButtonText("AOPatch_Reapply".Translate()))
            {
                CompatRegistry.Reapply();
                Messages.Message("AOPatch_ReapplyDone".Translate(), MessageTypeDefOf.TaskCompletion, false);
            }

            list.GapLine();
            list.Label("AOPatch_Footer".Translate());

            list.End();
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            // 不在此处重装补丁：WriteSettings 也会在退出游戏时调用。
            // 用户若要立即生效，请在设置窗口点「立即重装补丁」。
        }
    }

    /// <summary>
    /// 启动引导：等所有 Def 与程序集就绪后再安装补丁。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class AOPatchBootstrap
    {
        static AOPatchBootstrap()
        {
            try
            {
                Harmony harmony = AOPatchMod.HarmonyInstance;
                if (harmony == null)
                {
                    harmony = new Harmony(AOPatchMod.HarmonyId);
                    AOPatchMod.HarmonyInstance = harmony;
                    PatchLog.Warn("Harmony 实例为空，已在引导阶段补建。");
                }

                if (!AtmosphereApi.Available)
                {
                    PatchLog.Warn("Atmosphere Overhaul 未就绪，所有 AO 相关操作将为空操作。");
                }

                CompatRegistry.Initialize();
            }
            catch (Exception exception)
            {
                PatchLog.Error("引导阶段整体失败（不阻断游戏启动）", exception);
            }
        }
    }
}
