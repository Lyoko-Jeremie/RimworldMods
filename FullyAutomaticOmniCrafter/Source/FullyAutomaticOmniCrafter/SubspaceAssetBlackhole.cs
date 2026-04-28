using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    // ─── 亚空间资产黑洞 (Subspace Asset Blackhole) ────────────────────────────────

    /// <summary>
    /// 亚空间资产黑洞：通过亚空间技术将殖民地资产"虚化"，
    /// 在游戏财富统计层面降低殖民地总财富，从而减少袭击规模。
    /// 需要通电才能生效；财富扣减量由玩家自行调节。
    /// </summary>
    public class Building_SubspaceAssetBlackHole : Building
    {
        // ── 每实例储存的扣减量 ────────────────────────────────────────────────
        private float _wealthReduction = 10000f;

        public const float MinReduction = 0f;
        public const float MaxReduction = 100_000_000_000f;

        private CompPowerTrader _powerComp;

        // ── 实际生效的扣减量（断电时为 0） ────────────────────────────────────
        public float WealthReduction
        {
            get
            {
                if (_powerComp != null && !_powerComp.PowerOn)
                    return 0f;
                return _wealthReduction;
            }
        }

        /// <summary>读取或设置原始扣减量（不受供电状态影响）。</summary>
        public float RawWealthReduction
        {
            get => _wealthReduction;
            set => _wealthReduction = Mathf.Clamp(value, MinReduction, MaxReduction);
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
            Scribe_Values.Look(ref _wealthReduction, "wealthReduction", 10000f);
        }

        // ── Gizmos ─────────────────────────────────────────────────────────────
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            // 快速减少 × 10000
            yield return new Command_Action
            {
                defaultLabel = "SAB_DecreaseBy10k".Translate(),
                defaultDesc = "SAB_DecreaseBy10k_Desc".Translate(),
                icon = SubspaceAssetBlackHoleTex.IconDecrease,
                action = () => RawWealthReduction -= 10000f
            };

            // 打开精确调节界面
            yield return new Command_Action
            {
                defaultLabel = "SAB_Configure".Translate(),
                defaultDesc = "SAB_Configure_Desc".Translate(WealthReduction.ToString("N0")),
                icon = SubspaceAssetBlackHoleTex.IconConfigure,
                action = () => Find.WindowStack.Add(new Dialog_SAB_Configure(this))
            };

            // 快速增加 × 10000
            yield return new Command_Action
            {
                defaultLabel = "SAB_IncreaseBy10k".Translate(),
                defaultDesc = "SAB_IncreaseBy10k_Desc".Translate(),
                icon = SubspaceAssetBlackHoleTex.IconIncrease,
                action = () => RawWealthReduction += 10000f
            };
        }

        // ── 信息栏 ─────────────────────────────────────────────────────────────
        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            bool powered = _powerComp == null || _powerComp.PowerOn;

            string line = powered
                ? "SAB_Inspect_Active".Translate(WealthReduction.ToString("N0"))
                : "SAB_Inspect_Unpowered".Translate(_wealthReduction.ToString("N0"));

            if (!s.NullOrEmpty()) s += "\n";
            s += line;
            return s;
        }
    }

    // ─── 精确调节对话框 ────────────────────────────────────────────────────────
    public class Dialog_SAB_Configure : Window
    {
        private readonly Building_SubspaceAssetBlackHole _building;
        private float  _tempValue;
        private string _inputBuffer;

        public override Vector2 InitialSize => new Vector2(560f, 240f);

        public Dialog_SAB_Configure(Building_SubspaceAssetBlackHole building)
        {
            _building    = building;
            _tempValue   = building.RawWealthReduction;
            _inputBuffer = _tempValue.ToString("F0");

            doCloseButton        = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside   = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            const float labelH = 32f;
            const float sliderH = 32f;
            const float fieldH  = 30f;
            const float gap     = 10f;
            const float btnH    = 36f;
            const float btnW    = 120f;

            float y = inRect.y;

            // ── 标题 ──────────────────────────────────────────────────────
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(inRect.x, y, inRect.width, labelH),
                "SAB_DialogTitle".Translate());
            y += labelH + gap;

            // ── 滑块 ──────────────────────────────────────────────────────
            float newVal = Widgets.HorizontalSlider(
                new Rect(inRect.x, y, inRect.width, sliderH),
                _tempValue,
                Building_SubspaceAssetBlackHole.MinReduction,
                Building_SubspaceAssetBlackHole.MaxReduction,
                middleAlignment: false,
                label: _tempValue.ToString("N0"),
                leftAlignedLabel: "0",
                rightAlignedLabel: "10,000,000");

            if (Mathf.Abs(newVal - _tempValue) > 0.5f)
            {
                _tempValue   = Mathf.Round(newVal);
                _inputBuffer = _tempValue.ToString("F0");
            }
            y += sliderH + gap;

            // ── 精确输入框 ────────────────────────────────────────────────
            Rect fieldRow = new Rect(inRect.x, y, inRect.width, fieldH);
            Widgets.Label(new Rect(fieldRow.x, fieldRow.y, 130f, fieldRow.height),
                "SAB_ExactInput".Translate());

            string edited = Widgets.TextField(
                new Rect(fieldRow.x + 138f, fieldRow.y, 180f, fieldRow.height),
                _inputBuffer);

            if (edited != _inputBuffer)
            {
                _inputBuffer = edited;
                if (float.TryParse(edited, out float parsed))
                    _tempValue = Mathf.Clamp(parsed,
                        Building_SubspaceAssetBlackHole.MinReduction,
                        Building_SubspaceAssetBlackHole.MaxReduction);
            }
            y += fieldH + gap * 2f;

            // ── 确认按钮 ───────────────────────────────────────────────────
            if (Widgets.ButtonText(
                    new Rect(inRect.x + inRect.width - btnW - 4f, y, btnW, btnH),
                    "SAB_Confirm".Translate()))
            {
                _building.RawWealthReduction = _tempValue;
                Close();
            }
        }
    }

    // ─── Harmony Patch：从 WealthWatcher.WealthTotal 中扣除黑洞影响 ────────────
    [HarmonyPatch(typeof(WealthWatcher), "WealthTotal", MethodType.Getter)]
    public static class Patch_WealthWatcher_SubspaceBlackHole
    {
        // 通过反射缓存私有字段，避免每次 Postfix 都用 Traverse（性能更高）
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
            {
                if (all[i] is Building_SubspaceAssetBlackHole sink)
                    totalSink += sink.WealthReduction;
            }

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
