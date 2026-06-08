using System;
using UnityEngine;
using Verse;
using RimWorld;

namespace FullyAutomaticOmniCrafter
{
    public class Dialog_OmniPowerAdjust : Window
    {
        private string title;
        private float value;
        private float min;
        private float max;
        private Action<float> confirmedAction;
        private string buffer;

        public override Vector2 InitialSize => new Vector2(400f, 200f);

        public Dialog_OmniPowerAdjust(string title, float initialValue, float min, float max, Action<float> confirmedAction)
        {
            this.title = title;
            this.value = initialValue;
            this.min = min;
            this.max = max;
            this.confirmedAction = confirmedAction;
            this.buffer = initialValue.ToString("F0");
            this.doCloseX = true;
            this.forcePause = true;
            this.absorbInputAroundWindow = true;
            this.closeOnClickedOutside = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            Text.Font = GameFont.Medium;
            listing.Label(title);
            Text.Font = GameFont.Small;
            listing.Gap();

            // 数值输入框
            Rect textRect = listing.GetRect(30f);
            Widgets.Label(textRect.LeftPart(0.4f), "OmniPower_InputValue".Translate() + ":");
            buffer = Widgets.TextField(textRect.RightPart(0.6f), buffer);
            if (float.TryParse(buffer, out float parsed))
            {
                value = parsed;
            }

            listing.Gap();

            // 滑动条 (限制在 min 和 max 之间，但如果手动输入的更大也没关系)
            float sliderValue = Mathf.Clamp(value, min, max);
            float newSliderValue = listing.Slider(sliderValue, min, max);
            if (newSliderValue != sliderValue)
            {
                value = newSliderValue;
                buffer = value.ToString("F0");
            }

            listing.Gap();

            // 确认按钮
            if (listing.ButtonText("Confirm".Translate()))
            {
                confirmedAction(value);
                Close();
            }

            listing.End();
        }
    }
}
