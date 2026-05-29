using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public class CompProperties_WeaponCreator : CompProperties
    {
        public CompProperties_WeaponCreator()
        {
            this.compClass = typeof(CompWeaponCreator);
        }
    }
    
    /// <summary>
    /// 特化武器和人格武器制作台
    /// CompBladelinkWeapon
    /// CompUniqueWeapon
    /// 打开一个窗口，显示 左、中左、中右、右 四栏，
    /// 左侧是类似Dialog_OmniCrafter左侧的树分类表（只包含武器），
    /// 中左间是类似Dialog_OmniCrafter的搜索和筛选以及武器查看列表（只包含武器），
    /// 中右侧是武器制作界面，查看当前武器的状态，上半部分是武器基本介绍，下半部分是当前添加的组件列表，可以点击删除列表中的组件，最下方是生成武器按钮，武器直接生成在建筑所在附近
    /// 右可以选择所有可用的特化组件和人格，并在选中后显示对应组件可以设置的的参数并设置，然后按键添加到中右栏武器界面的组件列表中
    /// </summary>
    public class CompWeaponCreator : ThingComp
    {
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra()) yield return g;
            yield return new Command_Action
            {
                defaultLabel = "WeaponCreator_OpenUI".Translate(),
                defaultDesc = "WeaponCreator_OpenUIDesc".Translate(),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/CompWeaponCreator_LaunchReport", true) ?? BaseContent.WhiteTex,
                action = () => Find.WindowStack.Add(new Dialog_WeaponCreator(this))
            };
        }

        public void CreateWeapon(ThingDef def, ThingDef stuff, QualityCategory quality, List<WeaponTraitDef> traits)
        {
            Thing weapon = ThingMaker.MakeThing(def, stuff);
            
            // Set Quality
            CompQuality compQuality = weapon.TryGetComp<CompQuality>();
            if (compQuality != null)
            {
                compQuality.SetQuality(quality, ArtGenerationContext.Colony);
            }

            // Set Traits
            if (!traits.NullOrEmpty())
            {
                // CompBladelinkWeapon
                CompBladelinkWeapon bladelink = weapon.TryGetComp<CompBladelinkWeapon>();
                if (bladelink != null)
                {
                    var traitsField = typeof(CompBladelinkWeapon).GetField("traits", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (traitsField != null)
                    {
                        traitsField.SetValue(bladelink, traits.ToList());
                    }
                }

                // CompUniqueWeapon
                CompUniqueWeapon unique = weapon.TryGetComp<CompUniqueWeapon>();
                if (unique != null)
                {
                    var traitsField = typeof(CompUniqueWeapon).GetField("traits", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (traitsField != null)
                    {
                        traitsField.SetValue(unique, traits.ToList());
                        unique.Setup(false);
                        
                        // Set Name for Unique Weapon
                        var nameField = typeof(CompUniqueWeapon).GetField("name", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (nameField != null)
                        {
                            nameField.SetValue(unique, "Custom " + def.label);
                        }
                    }
                }
            }

            GenPlace.TryPlaceThing(weapon, parent.Position, parent.Map, ThingPlaceMode.Near);
        }
    }

    public class Dialog_WeaponCreator : Window
    {
        private CompWeaponCreator comp;
        
        private ThingCategoryDef selectedCategory;
        private string searchText = "";
        private Vector2 leftScroll;
        private Vector2 middleScroll;
        private Vector2 midRightScroll;
        private Vector2 farRightScroll;
        
        private ThingDef selectedDef;
        private ThingDef selectedStuff;
        private QualityCategory selectedQuality = QualityCategory.Normal;
        private List<WeaponTraitDef> selectedTraits = new List<WeaponTraitDef>();
        
        private List<ThingDef> weaponDefs;
        private List<WeaponTraitDef> availableTraits;

        public override Vector2 InitialSize => new Vector2(1350f, 750f);

        public Dialog_WeaponCreator(CompWeaponCreator comp)
        {
            this.comp = comp;
            this.doCloseButton = true;
            this.doCloseX = true;
            this.forcePause = true;
            this.absorbInputAroundWindow = false;
            this.draggable = true;
            
            // Initializing data
            weaponDefs = DefDatabase<ThingDef>.AllDefs
                .Where(d => d.IsWeapon) 
                .OrderBy(d => d.label)
                .ToList();
                
            availableTraits = DefDatabase<WeaponTraitDef>.AllDefs.OrderBy(t => t.label).ToList();
            
            // Log for debugging
            Log.Message($"[WeaponCreator] Loaded {weaponDefs.Count} weapons and {availableTraits.Count} traits.");
        }

        public override void DoWindowContents(Rect inRect)
        {
            // Title
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 35f), "WeaponCreator_Title".Translate());
            Text.Font = GameFont.Small;

            Rect bodyRect = new Rect(inRect.x, inRect.y + 40f, inRect.width, inRect.height - 90f);
            
            float leftW = 200f;
            float farRightW = 350f;
            float midRightW = 350f;
            float midLeftW = bodyRect.width - leftW - midRightW - farRightW - 12f;

            float x0 = bodyRect.x;
            float x1 = x0 + leftW + 4f;
            float x2 = x1 + midLeftW + 4f;
            float x3 = x2 + midRightW + 4f;

            DrawLeftPanel(new Rect(x0, bodyRect.y, leftW, bodyRect.height));
            DrawMiddlePanel(new Rect(x1, bodyRect.y, midLeftW, bodyRect.height));
            DrawMidRightPanel(new Rect(x2, bodyRect.y, midRightW, bodyRect.height));
            DrawFarRightPanel(new Rect(x3, bodyRect.y, farRightW, bodyRect.height));
        }

        private void DrawLeftPanel(Rect rect)
        {
            Widgets.DrawMenuSection(rect);
            // Weapon Categories
            var categories = weaponDefs.SelectMany(d => d.thingCategories ?? new List<ThingCategoryDef>()).Distinct().OrderBy(c => c.label).ToList();
            
            Rect viewRect = new Rect(0, 0, rect.width - 16f, categories.Count * 24f + 30f);
            Widgets.BeginScrollView(rect, ref leftScroll, viewRect);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);
            if (listing.RadioButton("All", selectedCategory == null)) selectedCategory = null;
            foreach (var cat in categories)
            {
                if (listing.RadioButton(cat.LabelCap, selectedCategory == cat)) selectedCategory = cat;
            }
            listing.End();
            Widgets.EndScrollView();
        }

        private void DrawMiddlePanel(Rect rect)
        {
            Widgets.DrawMenuSection(rect);
            searchText = Widgets.TextField(new Rect(rect.x + 5, rect.y + 5, rect.width - 10, 25), searchText);
            
            var filtered = weaponDefs
                .Where(d => selectedCategory == null || (d.thingCategories != null && d.thingCategories.Contains(selectedCategory)))
                .Where(d => searchText.NullOrEmpty() || d.label.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            Rect outRect = new Rect(rect.x, rect.y + 35, rect.width, rect.height - 35);
            Rect viewRect = new Rect(0, 0, outRect.width - 16f, filtered.Count * 30f);
            
            Widgets.BeginScrollView(outRect, ref middleScroll, viewRect);
            float curY = 0;
            foreach (var def in filtered)
            {
                Rect rowRect = new Rect(0, curY, viewRect.width, 28f);
                if (selectedDef == def) Widgets.DrawHighlightSelected(rowRect);
                else Widgets.DrawHighlightIfMouseover(rowRect);
                
                Widgets.Label(new Rect(5, curY, viewRect.width - 10, 28f), def.LabelCap);
                if (Widgets.ButtonInvisible(rowRect))
                {
                    selectedDef = def;
                    selectedStuff = def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null;
                }
                curY += 30f;
            }
            Widgets.EndScrollView();
        }

        private void DrawMidRightPanel(Rect rect)
        {
            Widgets.DrawMenuSection(rect);
            if (selectedDef == null)
            {
                Widgets.Label(rect.ContractedBy(10f), "Select a weapon");
                return;
            }

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect.ContractedBy(10f));
            listing.Label(selectedDef.LabelCap, 30f);
            listing.Label(selectedDef.description.Truncate(200f));
            listing.Gap();

            if (selectedDef.MadeFromStuff)
            {
                if (listing.ButtonText("Stuff: " + (selectedStuff?.LabelCap ?? "None")))
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    foreach (var stuff in GenStuff.AllowedStuffsFor(selectedDef))
                    {
                        options.Add(new FloatMenuOption(stuff.LabelCap, () => selectedStuff = stuff));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            }

            if (listing.ButtonText("Quality: " + selectedQuality))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (QualityCategory q in Enum.GetValues(typeof(QualityCategory)))
                {
                    options.Add(new FloatMenuOption(q.ToString(), () => selectedQuality = q));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            listing.Gap();
            listing.Label("Selected Traits:");
            listing.End();

            Rect traitsRect = new Rect(rect.x + 10, rect.y + 220, rect.width - 20, rect.height - 300);
            Widgets.DrawBoxSolid(traitsRect, new Color(0, 0, 0, 0.2f));
            
            Rect traitsViewRect = new Rect(0, 0, traitsRect.width - 16f, selectedTraits.Count * 24f);
            Widgets.BeginScrollView(traitsRect, ref midRightScroll, traitsViewRect);
            float tY = 0;
            for (int i = 0; i < selectedTraits.Count; i++)
            {
                var trait = selectedTraits[i];
                Rect tRect = new Rect(0, tY, traitsViewRect.width, 22f);
                Widgets.Label(tRect, trait.LabelCap);
                if (Widgets.ButtonImage(new Rect(tRect.width - 20, tY, 18, 18), Widgets.CheckboxOffTex))
                {
                    selectedTraits.RemoveAt(i);
                    break;
                }
                tY += 24f;
            }
            Widgets.EndScrollView();

            if (Widgets.ButtonText(new Rect(rect.x + 10, rect.y + rect.height - 40, rect.width - 20, 30), "Generate Weapon"))
            {
                comp.CreateWeapon(selectedDef, selectedStuff, selectedQuality, selectedTraits);
                Messages.Message("Weapon generated: " + selectedDef.label, MessageTypeDefOf.PositiveEvent);
            }
        }

        private void DrawFarRightPanel(Rect rect)
        {
            Widgets.DrawMenuSection(rect);
            listing_Traits(rect.ContractedBy(5f));
        }

        private void listing_Traits(Rect rect)
        {
            Rect outRect = new Rect(rect.x, rect.y, rect.width, rect.height);
            Rect viewRect = new Rect(0, 0, outRect.width - 16f, availableTraits.Count * 28f);
            
            Widgets.BeginScrollView(outRect, ref farRightScroll, viewRect);
            float curY = 0;
            foreach (var trait in availableTraits)
            {
                Rect tRect = new Rect(0, curY, viewRect.width, 26f);
                Widgets.DrawHighlightIfMouseover(tRect);
                Widgets.Label(new Rect(5, curY, viewRect.width - 30, 26f), trait.LabelCap);
                
                if (Widgets.ButtonImage(new Rect(viewRect.width - 25, curY + 2, 20, 20), Widgets.CheckboxOnTex))
                {
                    if (!selectedTraits.Contains(trait))
                        selectedTraits.Add(trait);
                }
                
                TooltipHandler.TipRegion(tRect, trait.description);
                curY += 28f;
            }
            Widgets.EndScrollView();
        }
    }
}