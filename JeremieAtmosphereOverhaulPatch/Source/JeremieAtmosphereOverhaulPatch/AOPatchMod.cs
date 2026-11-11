using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch
{
    /// <summary>
    /// Mod 入口。
    ///
    /// 本版是「推倒重写」后的骨架：只保留 Mod 实例、Harmony 实例与设置页，
    /// 不安装任何补丁，也不引用任何 AO 内部类型。
    /// 后续功能按模块逐个加回，每个模块自带安装/卸载。
    ///
    /// 工程约定（沿用旧版，仍然有效）：
    /// 构造函数里只建 Harmony 实例、读取设置，**绝不安装补丁**。
    /// 此时 DefDatabase 尚未填充，Harmony 建立 detour 会提前触发被 patch 类型的静态构造，
    /// 把其 DefDatabase&lt;T&gt;.GetNamed 字段永久钉成 null。
    /// 补丁安装延后到 StaticConstructorOnStartup 阶段，或游戏内首次需要时。
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
        }

        public override string SettingsCategory()
        {
            return "AOPatch_SettingsCategory".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard list = new Listing_Standard();
            list.Begin(inRect);

            list.Label("AOPatch_SkeletonNotice".Translate());
            list.GapLine();

            list.CheckboxLabeled(
                "AOPatch_Master".Translate(),
                ref Settings.MasterEnabled,
                "AOPatch_MasterDesc".Translate());

            list.End();
        }
    }
}
