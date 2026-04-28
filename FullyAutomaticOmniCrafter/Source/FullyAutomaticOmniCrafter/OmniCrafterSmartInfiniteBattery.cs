using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public class CompOmniCrafterSmartInfiniteBattery : CompPowerBattery
    {
        // ── 精度安全常量 ──────────────────────────────────────────────────────────
        // float 在 2^23 以内时 ULP=0.5，加减数个Wd不会被吞噬
        public const float SafeMaxFloatEnergy = 8_000_000f; // 8,388,608 Wd = 1 << 23

        // 维持UI美观的基础容量下限
        private const float BaseCapacity = 1000f;

        // ── 双桶字段 ──────────────────────────────────────────────────────────────
        // 溢出桶：超出 SafeMax 的电量以 ulong Wd 整数存储，无精度损失
        public ulong overflowEnergy = 0UL;

        // 是否正在吸收溢出电量
        private bool isAbsorbing = false;

        // ── 对外总电量（float 截断版，供 OmniCrafter.cs 使用）─────────────────────
        public float TotalFloatStoredEnergy
        {
            get
            {
                float stored = Traverse.Create(this).Field("storedEnergy").GetValue<float>();
                if (overflowEnergy == 0UL) return stored;
                double total = (double)overflowEnergy + stored;
                return total >= float.MaxValue / 2.0 ? float.MaxValue / 2f : (float)total;
            }
        }

        // ── 存档 ─────────────────────────────────────────────────────────────────
        public override void PostExposeData()
        {
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                // 在数据加载之前设置超大容量上限，兼容旧存档（旧存档 storedEnergy 可能很大）
                // 防止 CompPowerBattery.PostExposeData() 末尾将 storedEnergy 钳制到原始 XML 容量
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

            // overflowEnergy 在 base 之后保存/加载（旧存档缺少此键时默认为 0）
            Scribe_Values.Look(ref overflowEnergy, "overflowEnergy", 0UL);
        }

        // ── 初始化 ───────────────────────────────────────────────────────────────
        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);

            // 【克隆独立属性】剥离全局 XML 配置，防止膨胀污染其他同类电池
            var originalProps = this.Props;
            this.props = new CompProperties_Battery
            {
                compClass = originalProps.compClass,
                storedEnergyMax = SafeMaxFloatEnergy,
                efficiency = 1.0f,   // 100% 转化率，无废热损耗
                shortCircuitInRain = false,
                transmitsPower = originalProps.transmitsPower
            };

            // 【旧存档迁移】旧版 storedEnergy 可能远超 SafeMax，将超出部分转入溢出桶
            var storedField = Traverse.Create(this).Field("storedEnergy");
            float realStored = storedField.GetValue<float>();
            if (realStored > SafeMaxFloatEnergy)
            {
                ulong migrate = (ulong)(realStored - SafeMaxFloatEnergy);
                overflowEnergy += migrate;
                storedField.SetValue(SafeMaxFloatEnergy);
            }

            // 确保 storedEnergyMax 不低于已存储电量
            ((CompProperties_Battery)this.props).storedEnergyMax =
                Math.Max(SafeMaxFloatEnergy, storedField.GetValue<float>());
        }

        // ── Tick ──────────────────────────────────────────────────────────────────
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

        // ── 容量自适应 ────────────────────────────────────────────────────────────
        public void UpdateCapacity()
        {
            if (this.PowerNet == null) return;

            if (!FlickUtility.WantsToBeOn(this.parent))
            {
                this.isAbsorbing = false;
                return;
            }

            float surplusWdPerTick = this.PowerNet.CurrentEnergyGainRate();

            if (surplusWdPerTick > 1e-6f)
            {
                this.isAbsorbing = true;

                if (float.IsInfinity(surplusWdPerTick) || float.IsNaN(surplusWdPerTick))
                    surplusWdPerTick = SafeMaxFloatEnergy; // 足够大，保证缝隙

                // 容量 = SafeMax + 下一 tick 盈余 + 1（缓冲）
                // AddEnergy Postfix 会立即把超出 SafeMax 的部分转入溢出桶
                float newMax = SafeMaxFloatEnergy + surplusWdPerTick + 1f;
                ((CompProperties_Battery)this.props).storedEnergyMax =
                    Math.Max(BaseCapacity, newMax);
            }
            else
            {
                this.isAbsorbing = false;
                // 无盈余时保持 SafeMax，AmountCanAccept = 0，电网不再充电
                float stored = Traverse.Create(this).Field("storedEnergy").GetValue<float>();
                ((CompProperties_Battery)this.props).storedEnergyMax =
                    Math.Max(BaseCapacity, Math.Max(SafeMaxFloatEnergy, stored));
            }
        }

        // ── Gizmo ─────────────────────────────────────────────────────────────────
        [StaticConstructorOnStartup]
        public static class CompOmniCrafterSmartInfiniteBatteryTex
        {
            public static readonly Texture2D dischargeIcon =
                ContentFinder<Texture2D>.Get("UI/Commands/OmniCrafter_BatteryDischarge");
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
                yield return g;

            Command_Action dischargeBtn = new Command_Action
            {
                defaultLabel = "CompOmniCrafterSmartInfiniteBattery_DischargeAll".Translate(),
                defaultDesc  = "CompOmniCrafterSmartInfiniteBattery_DischargeAll_Desc".Translate(),
                icon = CompOmniCrafterSmartInfiniteBatteryTex.dischargeIcon,
                action = () =>
                {
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        "CompOmniCrafterSmartInfiniteBattery_DischargeAll_Confirm".Translate(),
                        () =>
                        {
                            // 先清空溢出桶，再清空 storedEnergy
                            this.overflowEnergy = 0UL;
                            float realStored = Traverse.Create(this).Field("storedEnergy").GetValue<float>();
                            if (realStored > 0f)
                                this.DrawPower(realStored);
                        }
                    ));
                }
            };
            yield return dischargeBtn;
        }

        // ── 检查面板文本 ──────────────────────────────────────────────────────────
        public override string CompInspectStringExtra()
        {
            string status = this.isAbsorbing
                ? "CompOmniCrafterSmartInfiniteBattery_AutoCoreRunning".Translate()
                : "CompOmniCrafterSmartInfiniteBattery_AutoCoreReady".Translate();

            if (!FlickUtility.WantsToBeOn(this.parent))
                status = "CompOmniCrafterSmartInfiniteBattery_AutoCoreDisconnect".Translate();

            float realStoredEnergy = Traverse.Create(this).Field("storedEnergy").GetValue<float>();

            if (overflowEnergy > 0UL)
            {
                // 双桶显示：活跃层 + 储备层
                double totalWd = (double)overflowEnergy + realStoredEnergy;
                string totalStr = totalWd.ToString("N0");
                return "CompOmniCrafterSmartInfiniteBattery_Fulfill".Translate() +
                       totalStr + " Wd" +
                       $"\n= ({realStoredEnergy:N0} Wd + {overflowEnergy:N0} Wd)\n" +
                       status;
            }

            return base.CompInspectStringExtra() + "\n" + status;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // Patch 1：AddEnergy Postfix — 充电溢出转桶
    // ═════════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(CompPowerBattery), nameof(CompPowerBattery.AddEnergy))]
    public static class Patch_SmartBattery_AddEnergy
    {
        [HarmonyPostfix]
        public static void Postfix(CompPowerBattery __instance)
        {
            if (!(__instance is CompOmniCrafterSmartInfiniteBattery smart)) return;

            var storedField = Traverse.Create(__instance).Field("storedEnergy");
            float current = storedField.GetValue<float>();

            if (current > CompOmniCrafterSmartInfiniteBattery.SafeMaxFloatEnergy)
            {
                // 超出 SafeMax 的部分转入溢出桶（ulong，无精度损失）
                float excess = current - CompOmniCrafterSmartInfiniteBattery.SafeMaxFloatEnergy;
                if (excess > 0f)
                    smart.overflowEnergy += (ulong)excess;

                // ✅ 必须设为 storedEnergyMax（而非 SafeMax）
                // 使 AmountCanAccept = 0，防止 Infinity 发电机导致 DistributeEnergyAmongBatteries 死循环
                storedField.SetValue(((CompProperties_Battery)__instance.props).storedEnergyMax);
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // Patch 2：DrawPower Prefix — 放电优先从溢出桶扣除
    // ═════════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(CompPowerBattery), nameof(CompPowerBattery.DrawPower))]
    public static class Patch_SmartBattery_DrawPower
    {
        [HarmonyPrefix]
        public static bool Prefix(CompPowerBattery __instance, float amount)
        {
            if (!(__instance is CompOmniCrafterSmartInfiniteBattery smart)) return true;
            if (smart.overflowEnergy == 0UL) return true; // 无溢出桶，走原生 float 逻辑

            var storedField = Traverse.Create(__instance).Field("storedEnergy");

            // 拆分为整数部分（从溢出桶扣）和小数部分（从 storedEnergy 扣）
            ulong wholeAmount = (ulong)amount;          // 向下取整
            float fracAmount  = amount - (float)wholeAmount; // 小数余量

            if (smart.overflowEnergy >= wholeAmount)
            {
                // 溢出桶足够承担整数部分
                smart.overflowEnergy -= wholeAmount;
                // 小数部分从 storedEnergy 扣（量极小，float 精度完全够用）
                if (fracAmount > 1e-7f)
                {
                    float stored = storedField.GetValue<float>();
                    storedField.SetValue(Math.Max(0f, stored - fracAmount));
                }
            }
            else
            {
                // 溢出桶不足：先清空桶，剩余从 storedEnergy 扣
                float fromOverflow = (float)smart.overflowEnergy;
                smart.overflowEnergy = 0UL;
                float remaining = amount - fromOverflow;
                float stored = storedField.GetValue<float>();
                storedField.SetValue(Math.Max(0f, stored - remaining));
            }

            return false; // 完全接管，跳过原生 DrawPower
        }
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // Patch 3：AmountCanAccept Prefix — 开关关闭时阻止充电
    // ═════════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(CompPowerBattery), "AmountCanAccept", MethodType.Getter)]
    public static class Patch_SmartBattery_AmountCanAccept
    {
        [HarmonyPrefix]
        public static bool Prefix(CompPowerBattery __instance, ref float __result)
        {
            if (__instance is CompOmniCrafterSmartInfiniteBattery smartBattery)
            {
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

    // ═════════════════════════════════════════════════════════════════════════════
    // Patch 4：StoredEnergy Getter Prefix — 开关关闭时对外伪装为 0 阻止放电
    // ═════════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(CompPowerBattery), "StoredEnergy", MethodType.Getter)]
    public static class Patch_SmartBattery_StoredEnergy
    {
        [HarmonyPrefix]
        public static bool Prefix(CompPowerBattery __instance, ref float __result)
        {
            if (__instance is CompOmniCrafterSmartInfiniteBattery smartBattery)
            {
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

