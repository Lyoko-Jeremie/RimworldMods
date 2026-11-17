using System.Globalization;
using UnityEngine;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Compat.OmniCrafter
{
    /// <summary>
    /// 地图级大气目标值设置面板。
    ///
    /// 目标值属于整张地图，不是单个穹顶：同一地图上所有穹顶与生物圈共用一份，
    /// 所以面板从任意一个穹顶或生物圈打开，改的都是同一组数字。
    /// 地温不单独设，按需求跟随气温。
    ///
    /// 每一行同时给滑条与数字输入框：滑条适合快速扫，输入框适合精确填。
    /// 气压用 AO 对外的 hPa，并可一键「跟随地图大气」，避免手填值与环境压差过大。
    /// </summary>
    public class Dialog_OmniAtmosphereTargets : Window
    {
        private readonly Map map;
        private readonly MapComponent_OmniAtmosphereTargets targets;

        private float temperature;
        private float humidity;
        private float oxygen;
        private float pressureHpa;
        private bool pressureFollowsAmbient;

        private string bufferTemperature;
        private string bufferHumidity;
        private string bufferOxygen;
        private string bufferPressure;

        private const float LabelWidth = 130f;
        private const float InputWidth = 110f;
        private const float UnitWidth = 70f;
        private const float RowStep = 44f;

        public override Vector2 InitialSize
        {
            get { return new Vector2(880f, 430f); }
        }

        public Dialog_OmniAtmosphereTargets(Map map)
        {
            this.map = map;
            targets = MapComponent_OmniAtmosphereTargets.Get(map);
            if (targets != null)
            {
                temperature = targets.TargetTemperature;
                humidity = targets.TargetHumidity;
                oxygen = targets.TargetOxygenPercent;
                pressureHpa = targets.TargetPressureHpa;
                pressureFollowsAmbient = targets.PressureFollowsAmbient;
            }

            bufferTemperature = Format(temperature, "0.#");
            bufferHumidity = Format(humidity, "0");
            bufferOxygen = Format(oxygen, "0.#");
            bufferPressure = Format(pressureHpa, "0.#");

            doCloseX = true;
            closeOnAccept = true;
            closeOnCancel = true;
            absorbInputAroundWindow = true;
            forcePause = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), "AOPatch_Omni_Targets".Translate());
            Text.Font = GameFont.Small;

            float y = inRect.y + 44f;

            DrawRow(inRect, y, "AOPatch_Omni_TargetTemperature".Translate(),
                ref temperature, ref bufferTemperature, -100f, 120f, "°C", "0.#", false);
            y += RowStep;

            DrawRow(inRect, y, "AOPatch_Omni_TargetHumidity".Translate(),
                ref humidity, ref bufferHumidity, 0f, 100f, "%", "0", false);
            y += RowStep;

            DrawRow(inRect, y, "AOPatch_Omni_TargetOxygen".Translate(),
                ref oxygen, ref bufferOxygen, 0f, 100f, "%", "0.#", false);
            y += RowStep;

            // 气压：自动跟随地图大气环境，避免手填值与环境压差过大。
            // 真空图或引擎未就绪时环境基准为 0，此时退回玩家设定值，面板上显示的也是这个退路值。
            float ambientHpa = targets != null
                ? targets.AmbientPressureHpa
                : MapComponent_OmniAtmosphereTargets.DefaultPressureHpa;
            if (ambientHpa <= 0f)
            {
                ambientHpa = targets != null ? targets.TargetPressureHpa : MapComponent_OmniAtmosphereTargets.DefaultPressureHpa;
            }

            Widgets.CheckboxLabeled(new Rect(inRect.x, y, 300f, 26f),
                "AOPatch_Omni_PressureAuto".Translate(), ref pressureFollowsAmbient);
            string ambientText = "AOPatch_Omni_PressureAmbient".Translate(ambientHpa.ToString("0.#"));
            Widgets.Label(new Rect(inRect.x + 320f, y + 3f, inRect.width - 330f, 24f), ambientText);
            y += 32f;

            DrawRow(inRect, y, "AOPatch_Omni_TargetPressure".Translate(),
                ref pressureHpa, ref bufferPressure, 0f, 2000f, "hPa", "0.#", pressureFollowsAmbient,
                pressureFollowsAmbient ? ambientHpa.ToString("0.#") : null);
            y += RowStep;

            // 地温只在 AO 场里跟随气温，没有独立目标值，这里给一行只读说明。
            Widgets.Label(new Rect(inRect.x, y, inRect.width - 12f, 24f),
                "AOPatch_Omni_GroundFollows".Translate());

            float buttonY = inRect.yMax - 36f;
            if (Widgets.ButtonText(new Rect(inRect.center.x - 122f, buttonY, 116f, 30f), "OK".Translate()))
            {
                Apply();
                Close();
            }
            if (Widgets.ButtonText(new Rect(inRect.center.x + 6f, buttonY, 116f, 30f), "Cancel".Translate()))
            {
                Close();
            }
        }

        private void DrawRow(Rect inRect, float y, string label,
            ref float value, ref string buffer, float min, float max, string unit, string format, bool disabled,
            string disabledText = null)
        {
            Widgets.Label(new Rect(inRect.x, y + 3f, LabelWidth, 24f), label);

            float sliderX = inRect.x + LabelWidth;
            float sliderWidth = inRect.width - LabelWidth - InputWidth - UnitWidth - 24f;
            Rect sliderRect = new Rect(sliderX, y, sliderWidth, 24f);
            Rect inputRect = new Rect(sliderX + sliderWidth + 12f, y, InputWidth, 24f);
            Rect unitRect = new Rect(inputRect.xMax + 6f, y + 3f, UnitWidth, 24f);

            bool prevEnabled = GUI.enabled;
            GUI.enabled = !disabled;

            float newValue = Widgets.HorizontalSlider(sliderRect, value, min, max, false, null);
            if (Mathf.Abs(newValue - value) > 0.0001f)
            {
                value = newValue;
                buffer = Format(value, format);
            }

            if (!disabled)
            {
                string text = Widgets.TextField(inputRect, buffer);
                if (text != buffer)
                {
                    buffer = text;
                    float parsed;
                    if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    {
                        value = Mathf.Clamp(parsed, min, max);
                    }
                }
            }

            GUI.enabled = prevEnabled;

            if (disabled)
            {
                Widgets.Label(inputRect, disabledText ?? value.ToString(format));
                Widgets.Label(unitRect, unit);
            }
            else
            {
                Widgets.Label(unitRect, unit);
            }
        }

        private static string Format(float value, string format)
        {
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        private void Apply()
        {
            if (targets == null)
            {
                return;
            }
            targets.TargetTemperature = temperature;
            targets.TargetHumidity = humidity;
            targets.TargetOxygenPercent = oxygen;
            targets.TargetPressureHpa = pressureHpa;
            targets.PressureFollowsAmbient = pressureFollowsAmbient;
        }
    }
}
