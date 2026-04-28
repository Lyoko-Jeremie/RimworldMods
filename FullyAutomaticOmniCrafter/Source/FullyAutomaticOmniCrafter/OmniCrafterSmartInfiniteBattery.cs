using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public class CompOmniCrafterSmartInfiniteBattery : CompPowerBattery
    {
        // 维持UI美观的基础容量下限
        private const float BaseCapacity = 1000f;

        // 是否正在吸收溢出电量
        private bool isAbsorbing = false;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);

            // 【克隆独立属性】剥离全局XML配置，防止膨胀污染其他同类电池
            var originalProps = this.Props;
            this.props = new CompProperties_Battery
            {
                compClass = originalProps.compClass,
                storedEnergyMax = originalProps.storedEnergyMax,
                efficiency = 1.0f, // 100% 转化率，无废热损耗
                shortCircuitInRain = false, // 关闭雨天短路
                transmitsPower = originalProps.transmitsPower
            };

            // 重新加载后，确保 storedEnergyMax 不低于已存储电量
            // 防止 UpdateCapacity 在首次 Tick 前期间因容量上限过低而触发意外钳制
            float realStored = Traverse.Create(this).Field("storedEnergy").GetValue<float>();
            if (realStored > ((CompProperties_Battery)this.props).storedEnergyMax)
            {
                ((CompProperties_Battery)this.props).storedEnergyMax = realStored;
            }
        }

        public override void PostExposeData()
        {
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                // 在数据加载之前，先将props克隆并设置超大容量上限，
                // 防止 CompPowerBattery.PostExposeData() 的末尾将 storedEnergy 钳制到原始XML容量
                var originalProps = this.Props;
                this.props = new CompProperties_Battery
                {
                    compClass = originalProps.compClass,
                    storedEnergyMax = float.MaxValue / 8f,
                    efficiency = 1.0f,
                    shortCircuitInRain = false,
                    transmitsPower = originalProps.transmitsPower
                };
            }

            base.PostExposeData();
        }

        public override void CompTick()
        {
            base.CompTick();
            UpdateCapacity();
        }

        public override void CompTickRare()
        {
            base.CompTickRare();
            UpdateCapacity();
        }

        public void UpdateCapacity()
        {
            if (this.PowerNet == null) return;

            // 如果开关关闭，停止一切自适应行为
            if (!FlickUtility.WantsToBeOn(this.parent))
            {
                this.isAbsorbing = false;
                return;
            }

            // CurrentEnergyGainRate返回当tick产生或消耗的净能量 (Wd per tick)
            float surplusWdPerTick = this.PowerNet.CurrentEnergyGainRate();

            if (surplusWdPerTick > 1e-6f)
            {
                this.isAbsorbing = true;

                // 如果遇到极其变态的发电机，赋予极大的充电量，不再限制 100Wd
                if (float.IsInfinity(surplusWdPerTick) || float.IsNaN(surplusWdPerTick))
                {
                    surplusWdPerTick = 1000000000f; // 一瞬间充满极大的数值 (10亿 Wd)
                }

                // 【完美100%机制】：精准计算出下个 PowerNetTick 将要塞进来的盈余电量。
                // 提前把我们的容量上限撑开正好这么大的缝隙，原版电网就会原封不动全塞进来，达成100%满载状态！
                // 修正：确保不会因为浮点精度问题导致没能撑开足够的容量，额外加一点点缓冲容差
                float potentialNewEnergy = this.StoredEnergy + surplusWdPerTick + 1f;
                float maxAllowedCapacity = float.MaxValue / (2f * 2f * 2f);
                potentialNewEnergy = Mathf.Min(potentialNewEnergy, maxAllowedCapacity);
                ((CompProperties_Battery)this.props).storedEnergyMax = Mathf.Max(BaseCapacity, potentialNewEnergy);
            }
            else
            {
                // 电网平衡或缺电时，停止主动吸收，并在放电时触发【棘轮机制】（容量不缩小，以发出电量预警）
                this.isAbsorbing = false;

                // 我们仍然要确保最大容量不会低于已存电量，以及维持基本值
                float currentMax = Mathf.Max(BaseCapacity, this.StoredEnergy);
                ((CompProperties_Battery)this.props).storedEnergyMax = currentMax;
            }
        }

        [StaticConstructorOnStartup]
        public static class CompOmniCrafterSmartInfiniteBatteryTex
        {
            public static readonly Texture2D dischargeIcon = ContentFinder<Texture2D>.Get("UI/Commands/OmniCrafter_BatteryDischarge");
        }

        // 放电按钮：一键将存储电量清零
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
                yield return g;

            Command_Action dischargeBtn = new Command_Action
            {
                defaultLabel = "CompOmniCrafterSmartInfiniteBattery_DischargeAll".Translate(),
                defaultDesc = "CompOmniCrafterSmartInfiniteBattery_DischargeAll_Desc".Translate(),
                icon = CompOmniCrafterSmartInfiniteBatteryTex.dischargeIcon,
                action = () =>
                {
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        "CompOmniCrafterSmartInfiniteBattery_DischargeAll_Confirm".Translate(),
                        () =>
                        {
                            // 直接读取私有字段，绕过开关拦截补丁，确保无论开关状态都能清空
                            float realStored = Traverse.Create(this).Field("storedEnergy").GetValue<float>();
                            if (realStored > 0f)
                                this.DrawPower(realStored);
                        }
                    ));
                }
            };
            yield return dischargeBtn;
        }

        // UI 文本优化：超过百万电量时显示无穷大，防止文字重叠
        public override string CompInspectStringExtra()
        {
            string status = this.isAbsorbing
                ? "CompOmniCrafterSmartInfiniteBattery_AutoCoreRunning".Translate()
                : "CompOmniCrafterSmartInfiniteBattery_AutoCoreReady".Translate();

            // 开关关闭时附加提示
            if (!FlickUtility.WantsToBeOn(this.parent))
            {
                status = "CompOmniCrafterSmartInfiniteBattery_AutoCoreDisconnect".Translate();
            }

            // 获取真实储电量，绕过Harmony补丁的隐藏效果
            float realStoredEnergy = Traverse.Create(this).Field("storedEnergy").GetValue<float>();

            if (realStoredEnergy >= 1000000000f)
            {
                return "CompOmniCrafterSmartInfiniteBattery_Fulfill".Translate() +
                       realStoredEnergy.ToString("N0") + "Wd\n" +
                       status;
            }

            return base.CompInspectStringExtra() + "\n" + status;
        }
    }

    [HarmonyPatch(typeof(CompPowerBattery), "AmountCanAccept", MethodType.Getter)]
    public static class Patch_SmartBattery_AmountCanAccept
    {
        [HarmonyPrefix]
        public static bool Prefix(CompPowerBattery __instance, ref float __result)
        {
            if (__instance is CompOmniCrafterSmartInfiniteBattery smartBattery)
            {
                // 如果开关关闭，阻止能量输入
                if (!FlickUtility.WantsToBeOn(smartBattery.parent))
                {
                    __result = 0f;
                    return false;
                }

                smartBattery.UpdateCapacity();
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(CompPowerBattery), "StoredEnergy", MethodType.Getter)]
    public static class Patch_SmartBattery_StoredEnergy
    {
        [HarmonyPrefix]
        public static bool Prefix(CompPowerBattery __instance, ref float __result)
        {
            if (__instance is CompOmniCrafterSmartInfiniteBattery smartBattery)
            {
                // 如果开关关闭，对外伪装电量为0，阻止能量输出
                if (!FlickUtility.WantsToBeOn(smartBattery.parent))
                {
                    __result = 0f;
                    return false;
                }
            }

            return true;
        }
    }

}