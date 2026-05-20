using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 幻影墙通行规则批量设置工具
    /// 使用方式：在建筑菜单中选择此工具，然后框选/点击幻影墙应用预设规则
    ///
    /// 批量框选墙体
    /// 应用预设规则模板
    /// TODO 可视反馈 鼠标显示当前预设名称，墙体高亮显示
    /// TODO 支持"复制/粘贴"规则
    /// TODO 自定义模式 支持自定义规则组合（需要额外 UI）
    /// 区域重建 规则改变后自动触发区域系统重建
    /// </summary>
    public class Designator_PhantomWall2Passability : Designator
    {
        private static readonly List<IntVec3> tmpHighlightCells = new List<IntVec3>();

        /// <summary>
        /// 当前选中的规则预设
        /// </summary>
        public static PassabilityPreset currentPreset = PassabilityPreset.AllowFriendly;
        
        /// <summary>
        /// 自定义规则（当 currentPreset 为 Custom 时使用）
        /// </summary>
        public static OmniPhantomWall2_PassabilitySettings customSettings = new OmniPhantomWall2_PassabilitySettings();

        public Designator_PhantomWall2Passability()
        {
            this.defaultLabel = "OPW_SetPassability".Translate();
            this.defaultDesc = "OPW_SetPassabilityDesc".Translate();
            this.icon = ContentFinder<Texture2D>.Get("UI/Designators/PhantomWallPassability", true) 
                        ?? ContentFinder<Texture2D>.Get("UI/Designators/Claim", true); // 备用图标
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
            foreach (IntVec3 cell in cells)
            {
                if (CanDesignateCell(cell).Accepted)
                {
                    DesignateSingleCell(cell);
                    count++;
                }
            }

            if (count > 0)
            {
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
                        allowTraders = true,
                        allowPrisoners = true,
                        allowWildAnimals = true,
                        allowEntities = true,
                        allowHostiles = true
                    };

                case PassabilityPreset.AllowFriendly:
                    return new OmniPhantomWall2_PassabilitySettings
                    {
                        allowColonists = true,
                        allowPets = true,
                        allowTraders = true,
                        allowPrisoners = false,
                        allowWildAnimals = false,
                        allowEntities = false,
                        allowHostiles = false
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
                        allowHostiles = false
                    };

                case PassabilityPreset.BlockAll:
                    return new OmniPhantomWall2_PassabilitySettings
                    {
                        allowColonists = false,
                        allowPets = false,
                        allowTraders = false,
                        allowPrisoners = false,
                        allowWildAnimals = false,
                        allowEntities = false,
                        allowHostiles = false
                    };

                case PassabilityPreset.Prison:
                    return new OmniPhantomWall2_PassabilitySettings
                    {
                        allowColonists = true,
                        allowPets = false,
                        allowTraders = false,
                        allowPrisoners = false, // 囚犯不能通过
                        allowWildAnimals = false,
                        allowEntities = false,
                        allowHostiles = false
                    };

                case PassabilityPreset.KillBox:
                    return new OmniPhantomWall2_PassabilitySettings
                    {
                        allowColonists = true,
                        allowPets = true,
                        allowTraders = true,
                        allowPrisoners = false,
                        allowWildAnimals = false,
                        allowEntities = false,
                        allowHostiles = false // 敌人不能通过，只能走缺口
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
            
            // 高亮所有幻影墙 - 同时支持OmniPhantomWall和OmniPhantomWall2
            tmpHighlightCells.Clear();
            List<Thing> allThings = Map.listerThings.AllThings;
            for (int i = 0; i < allThings.Count; i++)
            {
                if (allThings[i] is Building_OmniPhantomWall2 wall)
                {
                    tmpHighlightCells.Add(wall.Position);
                }
            }

            if (tmpHighlightCells.Count > 0)
            {
                GenDraw.DrawFieldEdges(tmpHighlightCells, Color.cyan);
            }
        }

        /// <summary>
        /// 底部额外 GUI 控件（用于切换预设）
        /// </summary>
        public override void DoExtraGuiControls(float leftX, float bottomY)
        {
            Rect winRect = new Rect(leftX, bottomY - 140f, 220f, 140f);
            
            Find.WindowStack.ImmediateWindow(73625891, winRect, WindowLayer.GameUI, () =>
            {
                Rect rect = winRect.AtZero().ContractedBy(5f);
                
                Text.Font = GameFont.Small;
                Widgets.Label(rect.TopPartPixels(24f), "OPW_SelectPreset".Translate());
                
                Rect buttonArea = rect;
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