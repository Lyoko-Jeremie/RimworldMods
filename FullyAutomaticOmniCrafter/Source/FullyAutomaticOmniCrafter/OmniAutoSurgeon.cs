using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public class SurgeryTemplate
    {
        public string templateName;

        // 记录部位 DefName 和对应的 义体 HediffDef
        public Dictionary<string, string> partToBionicMap = new Dictionary<string, string>();
    }

    /// <summary>
    /// 全自动医疗改造舱 FullyAutoOmniSurgeon
    /// 一个类似医疗床或休眠舱的建筑，可以快速为特定对象快速批量添加删除身体部位和义肢等增强部件、以及修复损伤和医疗受伤的建筑。
    /// 支持按模板安装、拆解。
    /// 支持手动按部位编辑和安装。
    /// 忽略材料限制。 
    /// </summary>
    public class Building_FullyAutoOmniSurgeon : Building
    {
        // 获取当前躺在里面的小人（如果你使用的是类似床或舱体的逻辑）
        public Pawn Occupant => ...;

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;

            if (this.Occupant != null)
            {
                Command_Action openUI = new Command_Action
                {
                    defaultLabel = "打开改造面板",
                    defaultDesc = "编辑小人的身体部位、安装义体或应用模板。",
                    icon = ContentFinder<Texture2D>.Get("UI/YourIcon"),
                    action = () => { Find.WindowStack.Add(new Window_OmniAutoSurgeonUI(this.Occupant, this)); }
                };
                yield return openUI;
            }
        }

        public void InstallBionic(Pawn pawn, BodyPartRecord part, HediffDef bionicDef, bool ignoreMaterials)
        {
            // 1. 检查材料 (如果需要)
            if (!ignoreMaterials)
            {
                // 查找全图或相邻储物区是否有该义体的物品 (ThingDef)
                // 如果有，则扣除 (Destroy)；如果没有，则返回错误提示
            }

            // 2. 移除该部位已有的其他冲突义体 (可选逻辑)

            // 3. 安装新义体
            pawn.health.AddHediff(bionicDef, part);

            // 4. 处理原版逻辑：有些义体安装后会产生“已移除的原始器官”肉块，可根据需求生成
        }

        public void RemoveBionic(Pawn pawn, BodyPartRecord part, Hediff hediffToRemove)
        {
            // 1. 移除义体 Hediff
            pawn.health.RemoveHediff(hediffToRemove);

            // 2. 恢复原部位 (如果拆除的是替换型义体，比如仿生臂，拆除后默认会变成“缺失”状态)
            // 如果你的设定是拆除后恢复原样，可以使用：
            pawn.health.RestorePart(part);

            // 3. 生成拆下来的义体物品掉落在地上 (GenSpawn.Spawn)
        }
    }
}