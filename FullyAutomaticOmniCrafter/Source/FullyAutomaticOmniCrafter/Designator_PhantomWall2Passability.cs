using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace FullyAutomaticOmniCrafter
{
    
    [StaticConstructorOnStartup]
    public static class PhantomWall2DesignatorTex
    {
        public static readonly Texture2D IconSelectPreset =
            ContentFinder<Texture2D>.Get("UI/Designators/PhantomWallPassability", true) ?? BaseContent.WhiteTex;
    }
    
    /// <summary>
    /// 幻影墙通行规则批量设置工具
    /// 使用方式：在建筑菜单中选择此工具，然后框选/点击幻影墙应用预设规则
    ///
    /// 批量框选墙体
    /// 应用预设规则模板
    /// TODO 可视反馈 鼠标显示当前预设名称
    /// 墙体高亮显示
    /// TODO 支持"复制/粘贴"规则
    /// 自定义模式 支持自定义规则组合（需要额外 UI）
    /// 区域重建 规则改变后自动触发区域系统重建
    /// </summary>
    public class Designator_PhantomWall2Passability : Designator
    {
        private static readonly Dictionary<int, List<IntVec3>> tmpHighlightGroups = new Dictionary<int, List<IntVec3>>();
        private static readonly Dictionary<int, Color> tmpGroupColors = new Dictionary<int, Color>();
        private static readonly List<IntVec3> tmpNoSettingsCells = new List<IntVec3>();

        private Building_OmniPhantomWall2 mouseOverWall;

        /// <summary>
        /// 当前选中的规则预设
        /// </summary>
        public static PassabilityPreset currentPreset = PassabilityPreset.AllowFriendly;
        
        /// <summary>
        /// 自定义规则（当 currentPreset 为 Custom 时使用）
        /// </summary>
        public static OmniPhantomWall2_PassabilitySettings customSettings => OmniCrafterMod.Settings.customPassabilitySettings;

        /// <summary>
        /// 启用二维拖拽（区域框选）
        /// </summary>
        public override DrawStyleCategoryDef DrawStyleCategory => DrawStyleCategoryDefOf.FilledRectangle;

        public Designator_PhantomWall2Passability()
        {
            this.defaultLabel = "OPW_SetPassability".Translate();
            this.defaultDesc = "OPW_SetPassabilityDesc".Translate();
            this.icon = PhantomWall2DesignatorTex.IconSelectPreset;
            this.soundDragSustain = SoundDefOf.Designate_DragStandard;
            this.soundDragChanged = SoundDefOf.Designate_DragStandard_Changed;
            this.soundSucceeded = SoundDefOf.Designate_Claim;
            this.useMouseIcon = true;
            this.hotKey = KeyBindingDefOf.Misc11; // 可以改成其他快捷键
        }

        /// <summary>
        /// 判断某个格子是否可以被此 Designator 作用
        /// </summary>
        public override AcceptanceReport CanDesignateCell(IntVec3 c)
        {
            if (!c.InBounds(Map))
                return false;
            if (c.Fogged(Map))
                return false;

            // 检查该格子是否有幻影墙
            Building_OmniPhantomWall2 wall = c.GetEdifice(Map) as Building_OmniPhantomWall2;
            if (wall == null)
                return "OPW_NotPhantomWall".Translate();

            return true;
        }

        /// <summary>
        /// 判断某个 Thing 是否可以被此 Designator 作用
        /// </summary>
        public override AcceptanceReport CanDesignateThing(Thing t)
        {
            if (t is Building_OmniPhantomWall2)
                return true;
            return false;
        }

        /// <summary>
        /// 对单个格子执行操作
        /// </summary>
        public override void DesignateSingleCell(IntVec3 c)
        {
            Building_OmniPhantomWall2 wall = c.GetEdifice(Map) as Building_OmniPhantomWall2;
            if (wall != null)
            {
                ApplyPresetToWall(wall);
            }
        }

        /// <summary>
        /// 对单个 Thing 执行操作
        /// </summary>
        public override void DesignateThing(Thing t)
        {
            if (t is Building_OmniPhantomWall2 wall)
            {
                ApplyPresetToWall(wall);
            }
        }

        /// <summary>
        /// 批量框选时调用（支持拖拽框选）
        /// </summary>
        public override void DesignateMultiCell(IEnumerable<IntVec3> cells)
        {
            int count = 0;
            Map map = Map;
            foreach (IntVec3 cell in cells)
            {
                if (CanDesignateCell(cell).Accepted)
                {
                    Building_OmniPhantomWall2 wall = cell.GetEdifice(map) as Building_OmniPhantomWall2;
                    if (wall != null)
                    {
                        OmniPhantomWall2_PassabilitySettings newSettings = GetSettingsFromPreset(currentPreset);
                        wall.ApplySettings(newSettings, false);
                        count++;
                    }
                }
            }

            if (count > 0)
            {
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                
                Messages.Message(
                    "OPW_AppliedToWalls".Translate(count, GetPresetLabel(currentPreset)),
                    MessageTypeDefOf.TaskCompletion,
                    false
                );
            }

            Finalize(count > 0);
        }

        /// <summary>
        /// 将预设规则应用到墙体
        /// </summary>
        private void ApplyPresetToWall(Building_OmniPhantomWall2 wall)
        {
            OmniPhantomWall2_PassabilitySettings newSettings = GetSettingsFromPreset(currentPreset);
            wall.ApplySettings(newSettings);
        }

        /// <summary>
        /// 根据预设获取实际的规则设置
        /// </summary>
        public static OmniPhantomWall2_PassabilitySettings GetSettingsFromPreset(PassabilityPreset preset)
        {
            switch (preset)
            {
                case PassabilityPreset.AllowAll:
                    return new OmniPhantomWall2_PassabilitySettings
                    {
                        allowColonists = true,
                        allowPets = true,
                        allowDryad = true,
                        allowTraders = true,
                        allowPrisoners = true,
                        allowWildAnimals = true,
                        allowEntities = true,
                        allowHostiles = true,
                        allowMechanoids = true,
                        allowInsectoids = true,
                        allowHumanlikes = true,
                        allowFactioned = true,
                        allowLords = true,
                        allowToolUsers = true,
                        allowUnfactions = true,
                        allowColonyPrisoners = true,
                    };

                case PassabilityPreset.AllowFriendly:
                    return new OmniPhantomWall2_PassabilitySettings
                    {
                        allowColonists = true,
                        allowPets = true,
                        allowTraders = true,
                        allowColonyPrisoners = true,
                        allowPrisoners = true,
                        allowWildAnimals = true,
                        allowEntities = true,
                        allowHostiles = false,
                        allowMechanoids = true,
                        allowInsectoids = true,
                    };

                case PassabilityPreset.ColonistsOnly:
                    return new OmniPhantomWall2_PassabilitySettings
                    {
                        allowColonists = true,
                        allowPets = false,
                        allowTraders = false,
                        allowPrisoners = false,
                        allowWildAnimals = false,
                        allowEntities = false,
                        allowHostiles = false,
                        allowMechanoids = false,
                        allowInsectoids = false
                    };

                case PassabilityPreset.BlockAll:
                    return new OmniPhantomWall2_PassabilitySettings
                    {
                        allowColonists = false,
                        allowPets = false,
                        allowDryad = false,
                        allowTraders = false,
                        allowPrisoners = false,
                        allowWildAnimals = false,
                        allowEntities = false,
                        allowHostiles = false,
                        allowMechanoids = false,
                        allowInsectoids = false,
                        allowHumanlikes = false,
                        allowFactioned = false,
                        allowLords = false,
                        allowToolUsers = false,
                        allowUnfactions = false,
                        allowColonyPrisoners = false,
                    };

                case PassabilityPreset.Prison:
                    return new OmniPhantomWall2_PassabilitySettings
                    {
                        allowColonists = true,
                        allowPets = false,
                        allowDryad = false,
                        allowTraders = false,
                        allowColonyPrisoners = false,
                        allowPrisoners = false,
                        allowWildAnimals = false,
                        allowEntities = false,
                        allowHostiles = false
                    };

                case PassabilityPreset.KillBox:
                    return new OmniPhantomWall2_PassabilitySettings
                    {
                        allowColonists = true,
                        allowPets = false,
                        allowTraders = false,
                        allowPrisoners = false,
                        allowWildAnimals = false,
                        allowEntities = false,
                        allowHostiles = true // 敌人不能通过，只能走缺口
                    };

                case PassabilityPreset.Custom:
                    return customSettings.Clone();

                default:
                    return new OmniPhantomWall2_PassabilitySettings();
            }
        }

        public static string GetPresetLabel(PassabilityPreset preset)
        {
            return $"OPW_Preset_{preset}".Translate();
        }

        /// <summary>
        /// 鼠标附加显示（显示当前选中的预设）
        /// </summary>
        public override void DrawMouseAttachments()
        {
            string label = GetPresetLabel(currentPreset);
            GenUI.DrawMouseAttachment(icon, label);
        }

        /// <summary>
        /// 选中时更新（高亮显示可作用的墙体）
        /// </summary>
        public override void SelectedUpdate()
        {
            GenUI.RenderMouseoverBracket();
            
            // 检测鼠标下的墙
            mouseOverWall = UI.MouseCell().GetEdifice(Map) as Building_OmniPhantomWall2;

            // 高亮所有幻影墙 - 同时支持OmniPhantomWall和OmniPhantomWall2
            // 按设置的签名分组以使用不同颜色
            foreach (var list in tmpHighlightGroups.Values)
            {
                list.Clear();
            }
            tmpHighlightGroups.Clear();
            tmpGroupColors.Clear();
            tmpNoSettingsCells.Clear();

            List<Thing> allThings = Map.listerThings.AllThings;
            for (int i = 0; i < allThings.Count; i++)
            {
                if (allThings[i] is Building_OmniPhantomWall2 wall)
                {
                    if (wall.settings != null)
                    {
                        int sig = wall.settings.GetSignature();
                        if (!tmpHighlightGroups.TryGetValue(sig, out List<IntVec3> cells))
                        {
                            cells = new List<IntVec3>();
                            tmpHighlightGroups[sig] = cells;
                            tmpGroupColors[sig] = wall.settings.GetColor();
                        }
                        cells.Add(wall.Position);
                    }
                    else
                    {
                        tmpNoSettingsCells.Add(wall.Position);
                    }
                }
            }

            // 绘制按设置分组的墙
            foreach (var kvp in tmpHighlightGroups)
            {
                int sig = kvp.Key;
                List<IntVec3> cells = kvp.Value;
                Color color = tmpGroupColors[sig];

                GenDraw.DrawFieldEdges(cells, color);
            }

            // 绘制没有设置的墙（理论上不应该出现）
            if (tmpNoSettingsCells.Count > 0)
            {
                GenDraw.DrawFieldEdges(tmpNoSettingsCells, Color.cyan);
            }
        }

        private void DrawMouseOverWallInfo()
        {
            Building_OmniPhantomWall2 localWall = mouseOverWall;
            if (localWall == null) return;

            var settings = localWall.settings;
            if (settings == null) return;

            var allFilters = settings.GetAllFilters();
            if (allFilters == null || allFilters.Count == 0) return;

            List<string> allowed = new List<string>();
            List<string> denied = new List<string>();

            foreach (var kvp in allFilters)
            {
                if (kvp.Value) allowed.Add(kvp.Key);
                else denied.Add(kvp.Key);
            }

            float width = 250f;
            float rowHeight = 20f;
            float titleHeight = 30f;
            float sectionHeaderHeight = 25f;
            
            // 计算高度：标题 + (允许列表标题 + 允许项) + (拒绝列表标题 + 拒绝项) + 间距
            float height = titleHeight + 10f;
            if (allowed.Count > 0) height += sectionHeaderHeight + allowed.Count * rowHeight;
            if (denied.Count > 0) height += sectionHeaderHeight + denied.Count * rowHeight;
            height += 10f;

            // 绘制在屏幕左侧中间偏下的位置，避开资源栏
            Rect rect = new Rect(20f, (UI.screenHeight / 2f) - (height / 2f), width, height);

            Find.WindowStack.ImmediateWindow(89237410, rect, WindowLayer.GameUI, () =>
            {
                if (localWall == null || localWall.Destroyed || settings == null) return;

                Rect innerRect = rect.AtZero().ContractedBy(10f);
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;

                // 标题：预设名称
                string presetLabel = localWall.GetPassabilitySummary();
                GUI.color = settings.GetColor();
                Widgets.Label(new Rect(innerRect.x, innerRect.y, innerRect.width, titleHeight), "OPW_CurrentPreset".Translate() + ": " + presetLabel);
                GUI.color = Color.white;

                float curY = innerRect.y + titleHeight;

                // 绘制允许列表
                if (allowed.Count > 0)
                {
                    Text.Font = GameFont.Tiny;
                    GUI.color = Color.green;
                    Widgets.Label(new Rect(innerRect.x, curY, innerRect.width, sectionHeaderHeight), "OPW_AllowedList".Translate() + ":");
                    GUI.color = Color.white;
                    curY += sectionHeaderHeight;

                    foreach (string filter in allowed)
                    {
                        Widgets.Label(new Rect(innerRect.x + 10f, curY, innerRect.width - 10f, rowHeight), "• " + filter);
                        curY += rowHeight;
                    }
                }

                // 绘制拒绝列表
                if (denied.Count > 0)
                {
                    Text.Font = GameFont.Tiny;
                    GUI.color = Color.red;
                    Widgets.Label(new Rect(innerRect.x, curY, innerRect.width, sectionHeaderHeight), "OPW_DeniedList".Translate() + ":");
                    GUI.color = Color.white;
                    curY += sectionHeaderHeight;

                    foreach (string filter in denied)
                    {
                        GUI.color = new Color(0.8f, 0.8f, 0.8f);
                        Widgets.Label(new Rect(innerRect.x + 10f, curY, innerRect.width - 10f, rowHeight), "• " + filter);
                        curY += rowHeight;
                    }
                    GUI.color = Color.white;
                }
                
                Text.Font = GameFont.Small;
            }, true, false, 0.7f);
        }

        /// <summary>
        /// 底部额外 GUI 控件（用于切换预设）
        /// </summary>
        public override void DoExtraGuiControls(float leftX, float bottomY)
        {
            // 绘制鼠标指向墙体的信息
            DrawMouseOverWallInfo();

            float width = 220f;
            float height = 230f;
            
            bool showCustom = currentPreset == PassabilityPreset.Custom;
            if (showCustom)
            {
                width = 460f; // 增加宽度以显示详细设置
                // 16 个选项行 + 标题 + 底部保存按钮，避免控件重叠
                const int customOptionCount = 16;
                const float customRowStride = 24f; // 22f 行高 + 2f 间距
                const float topHeaderHeight = 28f;
                const float saveButtonBlockHeight = 36f;
                const float windowPadding = 10f; // ContractedBy(5f) 的上下边距
                height = topHeaderHeight + customOptionCount * customRowStride + saveButtonBlockHeight + windowPadding;
            }

            Rect winRect = new Rect(leftX, bottomY - height, width, height);
            
            Find.WindowStack.ImmediateWindow(73625891, winRect, WindowLayer.GameUI, () =>
            {
                Rect rect = winRect.AtZero().ContractedBy(5f);
                
                // 左侧预设列表
                Rect leftRect = rect;
                if (showCustom)
                {
                    leftRect.width = 210f;
                }

                Text.Font = GameFont.Small;
                Widgets.Label(leftRect.TopPartPixels(24f), "OPW_SelectPreset".Translate());
                
                Rect buttonArea = leftRect;
                buttonArea.yMin += 28f;
                
                float buttonHeight = 24f;
                float y = buttonArea.y;
                
                foreach (PassabilityPreset preset in System.Enum.GetValues(typeof(PassabilityPreset)))
                {
                    Rect buttonRect = new Rect(buttonArea.x, y, buttonArea.width, buttonHeight);
                    
                    bool isSelected = currentPreset == preset;
                    if (Widgets.RadioButtonLabeled(buttonRect, GetPresetLabel(preset), isSelected))
                    {
                        currentPreset = preset;
                        SoundDefOf.Click.PlayOneShotOnCamera();
                    }
                    
                    y += buttonHeight + 2f;
                }

                // 右侧自定义设置
                if (showCustom)
                {
                    Rect rightRect = new Rect(rect.x + 220f, rect.y, rect.width - 220f, rect.height);
                    Widgets.DrawLineVertical(rect.x + 214f, rect.y, rect.height);
                    
                    Widgets.Label(rightRect.TopPartPixels(24f), "OPW_CustomSettings".Translate());
                    
                    Rect settingsArea = rightRect;
                    settingsArea.yMin += 28f;
                    
                    float rowHeight = 22f;
                    float curY = settingsArea.y;

                    void DrawCheckbox(string labelKey, ref bool value, string defaultLabel)
                    {
                        Rect rowRect = new Rect(settingsArea.x, curY, settingsArea.width, rowHeight);
                        string label = labelKey.CanTranslate() ? (string)labelKey.Translate() : defaultLabel;
                        Widgets.CheckboxLabeled(rowRect, label, ref value);
                        curY += rowHeight + 2f;
                    }

                    DrawCheckbox("OPW_AllowColonists", ref customSettings.allowColonists, "Colonists");
                    DrawCheckbox("OPW_AllowPets", ref customSettings.allowPets, "Pets");
                    DrawCheckbox("OPW_AllowDryad", ref customSettings.allowDryad, "Dryads");
                    DrawCheckbox("OPW_AllowTraders", ref customSettings.allowTraders, "Traders/Visitors");
                    DrawCheckbox("OPW_AllowPrisoners", ref customSettings.allowPrisoners, "Prisoners (Generic)");
                    DrawCheckbox("OPW_AllowColonyPrisoners", ref customSettings.allowColonyPrisoners, "Colony Prisoners");
                    DrawCheckbox("OPW_AllowWildAnimals", ref customSettings.allowWildAnimals, "Wild Animals");
                    DrawCheckbox("OPW_AllowEntities", ref customSettings.allowEntities, "Entities (Anomaly)");
                    DrawCheckbox("OPW_AllowHostiles", ref customSettings.allowHostiles, "Hostiles");
                    DrawCheckbox("OPW_AllowMechanoids", ref customSettings.allowMechanoids, "Mechanoids");
                    DrawCheckbox("OPW_AllowInsectoids", ref customSettings.allowInsectoids, "Insectoids");
                    DrawCheckbox("OPW_AllowFactioned", ref customSettings.allowFactioned, "Has Faction");
                    DrawCheckbox("OPW_AllowLords", ref customSettings.allowLords, "In Lord Group");
                    DrawCheckbox("OPW_AllowHumanlikes", ref customSettings.allowHumanlikes, "Humanlikes");
                    DrawCheckbox("OPW_AllowToolUsers", ref customSettings.allowToolUsers, "Tool Users");
                    DrawCheckbox("OPW_AllowUnfactions", ref customSettings.allowUnfactions, "Unfactions");
                    
                    if (Widgets.ButtonText(new Rect(settingsArea.x, settingsArea.yMax - 30f, settingsArea.width, 30f), "OPW_SaveSettings".Translate()))
                    {
                        OmniCrafterMod.Instance.WriteSettings();
                        Messages.Message("OPW_SettingsSaved".Translate(), MessageTypeDefOf.PositiveEvent, false);
                    }
                }
            });
        }

        public override bool AlwaysDoGuiControls => true;
    }

    /// <summary>
    /// 通行规则预设枚举
    /// </summary>
    public enum PassabilityPreset
    {
        /// <summary>允许所有单位通过</summary>
        AllowAll,
        /// <summary>允许友方通过（殖民者、宠物、商人）</summary>
        AllowFriendly,
        /// <summary>只允许殖民者通过</summary>
        ColonistsOnly,
        /// <summary>阻止所有单位</summary>
        BlockAll,
        /// <summary>监狱模式（殖民者可过，囚犯不可过）</summary>
        Prison,
        /// <summary>猎场模式（友方可过，敌人不可过）</summary>
        KillBox,
        /// <summary>自定义规则</summary>
        Custom
    }
}