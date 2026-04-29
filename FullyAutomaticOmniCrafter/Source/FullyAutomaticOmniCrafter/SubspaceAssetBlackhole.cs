using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    // ─── 亚空间资产黑洞发生器 (Subspace Asset Blackhole) ────────────────────────────────

    /// <summary>
    /// 亚空间资产黑洞发生器：通过亚空间技术将殖民地资产"虚化"，
    /// 在游戏财富统计层面分别降低物品、建筑、人员财富，从而减少袭击规模。
    /// 需要通电才能生效；各类财富扣减量由玩家自行调节。
    /// </summary>
    public class Building_SubspaceAssetBlackHole : Building
    {
        // ── 每实例储存的三类扣减量 ─────────────────────────────────────────────
        private float _itemsReduction     = 0f;
        private float _buildingsReduction = 0f;
        private float _pawnsReduction     = 0f;

        public const float MinReduction = 0f;
        public const float MaxReduction = 100_000_000_000f;

        private CompPowerTrader _powerComp;

        private bool IsPowered => _powerComp == null || _powerComp.PowerOn;

        // ── 生效值（断电时为 0） ───────────────────────────────────────────────
        public float ItemsReduction     => IsPowered ? _itemsReduction     : 0f;
        public float BuildingsReduction => IsPowered ? _buildingsReduction : 0f;
        public float PawnsReduction     => IsPowered ? _pawnsReduction     : 0f;

        // ── 原始值（不受供电影响） ─────────────────────────────────────────────
        public float RawItemsReduction
        {
            get => _itemsReduction;
            set => _itemsReduction = Mathf.Clamp(value, MinReduction, MaxReduction);
        }
        public float RawBuildingsReduction
        {
            get => _buildingsReduction;
            set => _buildingsReduction = Mathf.Clamp(value, MinReduction, MaxReduction);
        }
        public float RawPawnsReduction
        {
            get => _pawnsReduction;
            set => _pawnsReduction = Mathf.Clamp(value, MinReduction, MaxReduction);
        }

        // ── 初始化 ────────────────────────────────────────────────────────────
        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            _powerComp = GetComp<CompPowerTrader>();
        }

        // ── 存档 ──────────────────────────────────────────────────────────────
        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref _itemsReduction,     "itemsReduction",     0f);
            Scribe_Values.Look(ref _buildingsReduction, "buildingsReduction", 0f);
            Scribe_Values.Look(ref _pawnsReduction,     "pawnsReduction",     0f);
        }

        // ── Gizmos ─────────────────────────────────────────────────────────────
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            // 打开精确调节界面
            yield return new Command_Action
            {
                defaultLabel = "SAB_Configure".Translate(),
                defaultDesc  = "SAB_Configure_Desc".Translate(
                    ItemsReduction.ToString("N0"),
                    BuildingsReduction.ToString("N0"),
                    PawnsReduction.ToString("N0")),
                icon   = SubspaceAssetBlackHoleTex.IconConfigure,
                action = () => Find.WindowStack.Add(new Dialog_SAB_Configure(this))
            };
        }

        // ── 信息栏 ─────────────────────────────────────────────────────────────
        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            if (!s.NullOrEmpty()) s += "\n";

            if (!IsPowered)
            {
                s += "SAB_Inspect_Unpowered".Translate(
                    _itemsReduction.ToString("N0"),
                    _buildingsReduction.ToString("N0"),
                    _pawnsReduction.ToString("N0"));
            }
            else
            {
                s += "SAB_Inspect_Active".Translate(
                    ItemsReduction.ToString("N0"),
                    BuildingsReduction.ToString("N0"),
                    PawnsReduction.ToString("N0"));
            }
            return s;
        }
    }

    // ─── 精确调节对话框 ────────────────────────────────────────────────────────
    public class Dialog_SAB_Configure : Window
    {
        private readonly Building_SubspaceAssetBlackHole _building;

        private float  _items,     _buildings,     _pawns;
        private string _bufItems,  _bufBuildings,  _bufPawns;

        public override Vector2 InitialSize => new Vector2(600f, 380f);

        public Dialog_SAB_Configure(Building_SubspaceAssetBlackHole building)
        {
            _building    = building;
            _items       = building.RawItemsReduction;
            _buildings   = building.RawBuildingsReduction;
            _pawns       = building.RawPawnsReduction;
            _bufItems     = _items.ToString("F0");
            _bufBuildings = _buildings.ToString("F0");
            _bufPawns     = _pawns.ToString("F0");

            doCloseButton           = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside   = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            const float labelH  = 28f;
            const float sliderH = 26f;
            const float fieldH  = 28f;
            const float gap     = 8f;
            const float secGap  = 16f;
            const float btnH    = 36f;
            const float btnW    = 120f;
            const float inputLabelW = 140f;
            const float inputFieldW = 180f;

            Text.Font = GameFont.Small;
            float y = inRect.y;

            // ── 标题 ──────────────────────────────────────────────────────
            Widgets.Label(new Rect(inRect.x, y, inRect.width, labelH), "SAB_DialogTitle".Translate());
            y += labelH + gap;

            // ── 绘制一个滑块+输入框行 ──────────────────────────────────────
            DrawCategory(inRect, ref y, sliderH, fieldH, gap, inputLabelW, inputFieldW,
                "SAB_Category_Items".Translate(), ref _items, ref _bufItems);
            y += secGap;

            DrawCategory(inRect, ref y, sliderH, fieldH, gap, inputLabelW, inputFieldW,
                "SAB_Category_Buildings".Translate(), ref _buildings, ref _bufBuildings);
            y += secGap;

            DrawCategory(inRect, ref y, sliderH, fieldH, gap, inputLabelW, inputFieldW,
                "SAB_Category_Pawns".Translate(), ref _pawns, ref _bufPawns);
            y += secGap;

            // ── 确认按钮 ───────────────────────────────────────────────────
            if (Widgets.ButtonText(
                    new Rect(inRect.x + inRect.width - btnW - 4f, y, btnW, btnH),
                    "SAB_Confirm".Translate()))
            {
                _building.RawItemsReduction     = _items;
                _building.RawBuildingsReduction = _buildings;
                _building.RawPawnsReduction     = _pawns;
                Close();
            }
        }

        private static void DrawCategory(
            Rect inRect, ref float y,
            float sliderH, float fieldH, float gap,
            float inputLabelW, float inputFieldW,
            string label, ref float value, ref string buffer)
        {
            // 分类标签
            Widgets.Label(new Rect(inRect.x, y, inRect.width, fieldH), label);
            y += fieldH;

            // 滑块（最大显示 100,000,000,000 以保持可用性）
            const float sliderMax = 100_000_000_000f;
            float sliderVal = Mathf.Min(value, sliderMax);
            float newSliderVal = Widgets.HorizontalSlider(
                new Rect(inRect.x, y, inRect.width, sliderH),
                sliderVal,
                Building_SubspaceAssetBlackHole.MinReduction,
                sliderMax,
                middleAlignment: false,
                label: value.ToString("N0"),
                leftAlignedLabel: "0",
                rightAlignedLabel: "100,000,000,000");

            if (Mathf.Abs(newSliderVal - sliderVal) > 0.5f)
            {
                value  = Mathf.Round(newSliderVal);
                buffer = value.ToString("F0");
            }
            y += sliderH + gap;

            // 精确输入框
            Widgets.Label(new Rect(inRect.x, y, inputLabelW, fieldH), "SAB_ExactInput".Translate());
            string edited = Widgets.TextField(
                new Rect(inRect.x + inputLabelW + 8f, y, inputFieldW, fieldH),
                buffer);

            if (edited != buffer)
            {
                buffer = edited;
                if (float.TryParse(edited, out float parsed))
                    value = Mathf.Clamp(parsed,
                        Building_SubspaceAssetBlackHole.MinReduction,
                        Building_SubspaceAssetBlackHole.MaxReduction);
            }
            y += fieldH + gap;
        }
    }

    // ─── Harmony Patches：分别从 WealthItems / WealthBuildings / WealthPawns 中扣除 ───
    [HarmonyPatch(typeof(WealthWatcher), "WealthItems", MethodType.Getter)]
    public static class Patch_WealthWatcher_Items
    {
        private static readonly FieldInfo MapField =
            typeof(WealthWatcher).GetField("map",
                BindingFlags.NonPublic | BindingFlags.Instance);

        [HarmonyPostfix]
        public static void Postfix(WealthWatcher __instance, ref float __result)
        {
            Map map = MapField?.GetValue(__instance) as Map;
            if (map == null) return;

            float totalSink = 0f;
            List<Building> all = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < all.Count; i++)
                if (all[i] is Building_SubspaceAssetBlackHole sink)
                    totalSink += sink.ItemsReduction;

            if (totalSink > 0f)
                __result = Mathf.Max(0f, __result - totalSink);
        }
    }

    [HarmonyPatch(typeof(WealthWatcher), "WealthBuildings", MethodType.Getter)]
    public static class Patch_WealthWatcher_Buildings
    {
        private static readonly FieldInfo MapField =
            typeof(WealthWatcher).GetField("map",
                BindingFlags.NonPublic | BindingFlags.Instance);

        [HarmonyPostfix]
        public static void Postfix(WealthWatcher __instance, ref float __result)
        {
            Map map = MapField?.GetValue(__instance) as Map;
            if (map == null) return;

            float totalSink = 0f;
            List<Building> all = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < all.Count; i++)
                if (all[i] is Building_SubspaceAssetBlackHole sink)
                    totalSink += sink.BuildingsReduction;

            if (totalSink > 0f)
                __result = Mathf.Max(0f, __result - totalSink);
        }
    }

    [HarmonyPatch(typeof(WealthWatcher), "WealthPawns", MethodType.Getter)]
    public static class Patch_WealthWatcher_Pawns
    {
        private static readonly FieldInfo MapField =
            typeof(WealthWatcher).GetField("map",
                BindingFlags.NonPublic | BindingFlags.Instance);

        [HarmonyPostfix]
        public static void Postfix(WealthWatcher __instance, ref float __result)
        {
            Map map = MapField?.GetValue(__instance) as Map;
            if (map == null) return;

            float totalSink = 0f;
            List<Building> all = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < all.Count; i++)
                if (all[i] is Building_SubspaceAssetBlackHole sink)
                    totalSink += sink.PawnsReduction;

            if (totalSink > 0f)
                __result = Mathf.Max(0f, __result - totalSink);
        }
    }

    // ─── 纹理资源（启动时静态加载） ─────────────────────────────────────────────
    [StaticConstructorOnStartup]
    public static class SubspaceAssetBlackHoleTex
    {
        public static readonly Texture2D IconConfigure =
            ContentFinder<Texture2D>.Get("UI/Commands/SAB_Configure", true)
            ?? BaseContent.WhiteTex;

        public static readonly Texture2D IconIncrease =
            ContentFinder<Texture2D>.Get("UI/Commands/SAB_Increase", true)
            ?? BaseContent.WhiteTex;

        public static readonly Texture2D IconDecrease =
            ContentFinder<Texture2D>.Get("UI/Commands/SAB_Decrease", true)
            ?? BaseContent.WhiteTex;
    }
}
