using System;
using System.Collections.Generic;
using System.Globalization;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public class Dialog_PersonalTemperature : Window
    {
        private enum PawnFilter { All, Colonists, Prisoners, Slaves, Animals, Mechanoids, Other }
        private enum StateFilter { All, Enabled, Disabled }
        private readonly CompPersonalTemperature comp;
        private readonly List<Pawn> pawns = new List<Pawn>();
        private readonly List<Pawn> filtered = new List<Pawn>();
        private readonly Dictionary<Pawn, string> buffers = new Dictionary<Pawn, string>();
        private readonly Dictionary<Pawn, float> savedTargets = new Dictionary<Pawn, float>();
        private int revision = -1;
        private Vector2 scroll;
        private string search = "";
        private PawnFilter pawnFilter;
        private StateFilter stateFilter;
        public override Vector2 InitialSize => new Vector2(980f, 700f);
        public Dialog_PersonalTemperature(CompPersonalTemperature comp)
        {
            this.comp = comp;
            doCloseX = true;
            doCloseButton = true;
            absorbInputAroundWindow = true;
            RefreshPawns();
        }
        private void RefreshPawns()
        {
            pawns.Clear();
            var spawned = comp.parent.Map.mapPawns.AllPawns;
            for (int i = 0; i < spawned.Count; i++) if (!spawned[i].Dead) pawns.Add(spawned[i]);
            // 已配置且暂时离图的角色仍可管理，回到地图后自动恢复效果。
            for (int i = 0; i < comp.Settings.Count; i++)
            {
                Pawn pawn = comp.Settings[i].pawn;
                if (pawn != null && !pawn.Dead && !pawn.Destroyed && !pawns.Contains(pawn)) pawns.Add(pawn);
            }
            pawns.Sort((a, b) => string.Compare(a.LabelShort, b.LabelShort, StringComparison.CurrentCulture));
            for (int i = 0; i < pawns.Count; i++)
            {
                comp.TryGetSetting(pawns[i], out float value);
                if (!savedTargets.TryGetValue(pawns[i], out float previous) || previous != value)
                {
                    buffers[pawns[i]] = value.ToString(CultureInfo.InvariantCulture);
                    savedTargets[pawns[i]] = value;
                }
            }
            revision = comp.SettingsRevision;
            Refilter();
        }
        private void Refilter()
        {
            filtered.Clear();
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn.Dead || pawn.Destroyed) continue;
                bool enabled = comp.IsEnabledFor(pawn);
                if (stateFilter == StateFilter.Enabled && !enabled || stateFilter == StateFilter.Disabled && enabled) continue;
                if (pawnFilter != PawnFilter.All && Category(pawn) != pawnFilter) continue;
                if (search.Length != 0 && pawn.LabelShort.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) < 0
                    && pawn.def.label.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) < 0
                    && (pawn.Faction == null || pawn.Faction.Name.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) < 0)) continue;
                filtered.Add(pawn);
            }
        }
        private static PawnFilter Category(Pawn pawn)
        {
            if (pawn.IsPrisoner) return PawnFilter.Prisoners;
            if (pawn.IsSlave) return PawnFilter.Slaves;
            if (pawn.IsColonist) return PawnFilter.Colonists;
            if (pawn.RaceProps.Animal) return PawnFilter.Animals;
            if (pawn.RaceProps.IsMechanoid) return PawnFilter.Mechanoids;
            return PawnFilter.Other;
        }
        public override void DoWindowContents(Rect inRect)
        {
            if (!comp.parent.Spawned) { Close(); return; }
            if (revision != comp.SettingsRevision) RefreshPawns();
            Widgets.Label(new Rect(0f, 0f, inRect.width, 30f), "PersonalTemperature_Settings".Translate());
            Widgets.Label(new Rect(0f, 32f, inRect.width, 65f), "PersonalTemperature_Help".Translate());
            Widgets.Label(new Rect(0f, 102f, 65f, 30f), "PersonalTemperature_Search".Translate());
            string nextSearch = Widgets.TextField(new Rect(65f, 102f, 300f, 30f), search);
            if (nextSearch != search) { search = nextSearch; scroll = Vector2.zero; Refilter(); }
            if (Widgets.ButtonText(new Rect(380f, 102f, 160f, 30f), ("PersonalTemperature_Filter_" + pawnFilter).Translate()))
            {
                var options = new List<FloatMenuOption>();
                foreach (PawnFilter value in Enum.GetValues(typeof(PawnFilter)))
                {
                    PawnFilter choice = value;
                    options.Add(new FloatMenuOption(("PersonalTemperature_Filter_" + value).Translate(), () => { pawnFilter = choice; scroll = Vector2.zero; Refilter(); }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            if (Widgets.ButtonText(new Rect(550f, 102f, 160f, 30f), ("PersonalTemperature_State_" + stateFilter).Translate()))
            {
                var options = new List<FloatMenuOption>();
                foreach (StateFilter value in Enum.GetValues(typeof(StateFilter)))
                {
                    StateFilter choice = value;
                    options.Add(new FloatMenuOption(("PersonalTemperature_State_" + value).Translate(), () => { stateFilter = choice; scroll = Vector2.zero; Refilter(); }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            if (Widgets.ButtonText(new Rect(725f, 102f, 190f, 30f), "PersonalTemperature_Refresh".Translate())) RefreshPawns();
            Widgets.Label(new Rect(0f, 139f, inRect.width, 30f), "PersonalTemperature_Status".Translate(filtered.Count, pawns.Count,
                (comp.MapOperational ? "PersonalTemperature_Online" : "PersonalTemperature_Offline").Translate()));
            Widgets.Label(new Rect(0f, 174f, 205f, 30f), "PersonalTemperature_Pawn".Translate());
            Widgets.Label(new Rect(210f, 174f, 115f, 30f), "PersonalTemperature_Current".Translate());
            Widgets.Label(new Rect(330f, 174f, 70f, 30f), "PersonalTemperature_On".Translate());
            Widgets.Label(new Rect(405f, 174f, 145f, 30f), "PersonalTemperature_Input".Translate());
            Rect outer = new Rect(0f, 205f, inRect.width, inRect.height - 260f);
            Rect view = new Rect(0f, 0f, outer.width - 20f, filtered.Count * 70f);
            Widgets.BeginScrollView(outer, ref scroll, view);
            bool changed = false;
            for (int i = 0; i < filtered.Count; i++)
            {
                float y = i * 70f;
                if (y + 70f < scroll.y || y > scroll.y + outer.height) continue;
                Pawn pawn = filtered[i];
                comp.TryGetSetting(pawn, out float target);
                bool enabled = comp.IsEnabledFor(pawn);
                Widgets.Label(new Rect(0f, y, 205f, 30f), pawn.LabelShortCap);
                var range = pawn.ComfortableTemperatureRange();
                Widgets.Label(new Rect(0f, y + 30f, 205f, 30f), range.min.ToStringTemperature() + " ~ " + range.max.ToStringTemperature());
                Widgets.Label(new Rect(210f, y, 115f, 30f), pawn.MapHeld == comp.parent.Map ? pawn.AmbientTemperature.ToStringTemperature() : "PersonalTemperature_Away".Translate().ToString());
                bool nextEnabled = enabled;
                Widgets.Checkbox(new Vector2(340f, y + 3f), ref nextEnabled);
                if (nextEnabled != enabled) { comp.SetEnabledFor(pawn, nextEnabled); changed = true; }
                buffers[pawn] = Widgets.TextField(new Rect(405f, y, 105f, 30f), buffers[pawn]);
                bool valid = float.TryParse(buffers[pawn], NumberStyles.Float, CultureInfo.InvariantCulture, out float input)
                    && CompPersonalTemperature.ValidTemperature(input);
                if (Widgets.ButtonText(new Rect(520f, y, 90f, 30f), "PersonalTemperature_Apply".Translate()) && valid)
                    comp.SetTemperature(pawn, input);
                if (Widgets.ButtonText(new Rect(620f, y, 125f, 30f), "PersonalTemperature_Comfort".Translate()))
                {
                    float value = (range.min + range.max) * 0.5f;
                    if (CompPersonalTemperature.ValidTemperature(value))
                    {
                        comp.SetTemperature(pawn, value);
                        buffers[pawn] = value.ToString(CultureInfo.InvariantCulture);
                    }
                }
                if (Widgets.ButtonText(new Rect(755f, y, 145f, 30f), "PersonalTemperature_Reset".Translate()))
                {
                    comp.SetTemperature(pawn, null);
                    buffers[pawn] = "21";
                    changed = true;
                }
                string status = !valid ? "PersonalTemperature_Invalid".Translate().ToString()
                    : "PersonalTemperature_Target".Translate(target.ToStringTemperature()).ToString();
                Widgets.Label(new Rect(405f, y + 32f, 495f, 30f), status);
            }
            if (filtered.Count == 0) Widgets.Label(new Rect(0f, 0f, view.width, 40f), "PersonalTemperature_Empty".Translate());
            Widgets.EndScrollView();
            if (changed) Refilter();
        }
    }
}

