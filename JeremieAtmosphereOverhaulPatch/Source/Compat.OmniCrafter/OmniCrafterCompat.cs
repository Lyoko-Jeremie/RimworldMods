using System;
using System.Reflection;
using HarmonyLib;
using Jeremie.AtmosphereOverhaulPatch.Api;

namespace Jeremie.AtmosphereOverhaulPatch.Compat
{
    /// <summary>
    /// 超维科技（Jeremie.Fully.Automatic.OmniCrafter）兼容。
    ///
    /// 现状：本 Mod 的 OmniBiosphere（CompBiosphere）目前用
    /// GenTemperature.PushHeat(cell, map, 0.01f) 象征性推热，再靠 Harmony 补丁强制读温。
    /// 在 AO 地图上，这条路径只改「读取值」，不会改 AO 的物理温度场，
    /// 所以生物圈内的空气读数仍由 AO 求解器决定。
    ///
    /// 正确接入方式（待实现）：在 CompBiosphere.ApplyEffects 里改为
    /// 通过 AtmosphereInterface 注册持续源（RegisterContinuousSource），
    /// 由 AO 按 tick 把温度写进气场；并且只在 AO 存在时走这条分支，
    /// AO 不存在时保留现有的 GenTemperature 路径。
    ///
    /// 本模块先只做存在性检测与类型诊断，确保任意子集下都能安全启动。
    /// </summary>
    public sealed class OmniCrafterCompat : CompatModuleBase
    {
        private const string AssemblyName = "FullyAutomaticOmniCrafter";
        private const string BiosphereTypeName = "FullyAutomaticOmniCrafter.CompBiosphere";

        public override string PackageId
        {
            get { return "Jeremie.Fully.Automatic.OmniCrafter"; }
        }

        public override string LabelKey
        {
            get { return "AOPatch_Module_Omni"; }
        }

        public override string HintKey
        {
            get { return "AOPatch_Module_OmniDesc"; }
        }

        protected override bool ReadSetting()
        {
            return AOPatchMod.Settings != null && AOPatchMod.Settings.EnableOmniCrafter;
        }

        protected override void WriteSetting(bool value)
        {
            if (AOPatchMod.Settings != null)
            {
                AOPatchMod.Settings.EnableOmniCrafter = value;
            }
        }

        protected override bool TryApply(Harmony harmony)
        {
            Assembly assembly = FindAssembly(AssemblyName);
            if (assembly == null)
            {
                Detail = "assembly not loaded";
                return false;
            }

            Type biosphere = ReflectionCache.Type(BiosphereTypeName);
            if (biosphere == null)
            {
                Detail = "CompBiosphere missing";
                return false;
            }

            Detail = "CompBiosphere found, methods " + SafeMethodCount(biosphere)
                     + ", AO api " + (AtmosphereApi.Available ? "available" : "unavailable")
                     + ", behavior patch pending";
            return false;
        }

        private static int SafeMethodCount(Type type)
        {
            try
            {
                return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Length;
            }
            catch
            {
                return 0;
            }
        }
    }
}
