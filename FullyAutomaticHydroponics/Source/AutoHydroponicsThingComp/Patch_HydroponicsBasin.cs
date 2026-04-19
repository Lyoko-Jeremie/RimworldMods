using Verse;
using RimWorld;
using System.Linq; // 引入 Linq 用于便捷查询
using System.Collections.Generic;
// 注意：你需要引入包含 CompProperties_FullyAutoHydroponics 的命名空间
using FullyAutoHydroponicsThingComp;

// 为了解决某些mod会篡改原版 水栽培植物盆 HydroponicsBasin 导致此 mod 无效的问题，使用dll在xml patch之后再进行一次检查
// 等价xml实现
//    <Operation Class="PatchOperationConditional">
//        <xpath>Defs/ThingDef[defName="HydroponicsBasin"]/comps/li[@Class="FullyAutoHydroponicsThingComp.CompProperties_FullyAutoHydroponics"]</xpath>
//
//        <nomatch Class="PatchOperationAdd">
//            <xpath>Defs/ThingDef[defName="HydroponicsBasin"]/comps</xpath>
//            <value>
//                <li Class="FullyAutoHydroponicsThingComp.CompProperties_FullyAutoHydroponics">
//                    <defaultAutoHarvest>false</defaultAutoHarvest>
//                    <defaultAutoSow>false</defaultAutoSow>
//                </li>
//            </value>
//        </nomatch>
//    </Operation>

//    <Operation Class="PatchOperationAdd">
//        <xpath>Defs/ThingDef[thingClass="Building_PlantGrower"]/comps</xpath>
//        <success>Always</success>
//        <value>
//            <li Class="FullyAutoHydroponicsThingComp.CompProperties_FullyAutoHydroponics">
//                <defaultAutoHarvest>false</defaultAutoHarvest>
//                <defaultAutoSow>false</defaultAutoSow>
//            </li>
//        </value>
//    </Operation>
//    
//    <Operation Class="PatchOperationAdd">
//        <xpath>Defs/ThingDef[thingClass="Building_PlantGrower"][not(comps)]</xpath>
//        <success>Always</success>
//        <value>
//            <comps>
//                <li Class="FullyAutoHydroponicsThingComp.CompProperties_FullyAutoHydroponics">
//                    <defaultAutoHarvest>false</defaultAutoHarvest>
//                    <defaultAutoSow>false</defaultAutoSow>
//                </li>
//            </comps>
//        </value>
//    </Operation>


// 采用如下代码来阻止自动添加标签
//      <comps>
//          <li Class="FullyAutoHydroponicsThingComp.CompProperties_No_FullyAutoHydroponics" />
//     </comps> 



namespace FullyAutoHydroponicsThingComp
{
    [StaticConstructorOnStartup]
    public static class LateGrowerPatcher
    {
        static LateGrowerPatcher()
        {
            int patchedCount = 0;

            // 抓取所有继承自 Building_PlantGrower 的建筑
            var targetDefs = DefDatabase<ThingDef>.AllDefs.Where(def =>
                def.thingClass != null &&
                typeof(Building_PlantGrower).IsAssignableFrom(def.thingClass));

            foreach (ThingDef def in targetDefs)
            {
                // 【核心修改点】
                // 1. 检查 comps 列表中是否带有“拒绝注入”的专属组件标签
                // 注意：必须先检查 def.comps != null，防止报错
                bool hasExcludeComp = def.comps != null &&
                                      def.comps.Any(c => c is CompProperties_No_FullyAutoHydroponics);

                if (hasExcludeComp)
                {
                    // 可选：在日志里输出一下谁被跳过了，方便你排查兼容性
                    Log.Message($"[FullyAutoHydroponics] [{def.defName}] 带有拒绝标签，跳过。");
                    continue;
                }

                // 2. 如果没有任何拒绝标签，再确保 comps 列表被初始化
                if (def.comps == null)
                {
                    def.comps = new List<CompProperties>();
                }

                // 3. 检查是否已经存在全自动组件 (防止重复添加)
                bool hasAutoComp = def.comps.Any(c => c is CompProperties_FullyAutoHydroponics);

                if (!hasAutoComp)
                {
                    // 注入组件并赋予默认值
                    def.comps.Add(new CompProperties_FullyAutoHydroponics
                    {
                        defaultAutoHarvest = false,
                        defaultAutoSow = false
                    });
                    
                    Log.Message($"[FullyAutoHydroponics] 成功为 [{def.defName}] 注入了全自动组件。");

                    patchedCount++;
                }
                else
                {
                    Log.Message($"[FullyAutoHydroponics] [{def.defName}] 已经有全自动组件，跳过。");
                }
            }

            Log.Message($"[FullyAutoHydroponics] 扫描完毕。成功为 [{patchedCount}] 个植物盆注入了全自动组件。");
        }
    }

    // 这是一个空的占位类，仅用于作为“拒绝注入”的标签
    public class CompProperties_No_FullyAutoHydroponics : CompProperties
    {
        public CompProperties_No_FullyAutoHydroponics()
        {
            // 如果你需要绑定一个真正的 ThingComp 处理逻辑，可以写在这里
            // this.compClass = typeof(你可能需要的空Comp类); 

            // 但如果仅仅是作为属性标签(Property)，这里什么都不写也可以
        }
    }
}
