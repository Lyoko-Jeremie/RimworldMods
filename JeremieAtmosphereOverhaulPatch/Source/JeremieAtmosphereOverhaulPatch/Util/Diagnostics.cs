using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Jeremie.AtmosphereOverhaulPatch.Api;
using Jeremie.AtmosphereOverhaulPatch.Compat;
using Jeremie.AtmosphereOverhaulPatch.Util;
using Verse;

namespace Jeremie.AtmosphereOverhaulPatch.Util
{
    /// <summary>
    /// 一键导出诊断快照。玩家反馈兼容问题时，让他点一下「导出诊断」并把这个文件发过来，
    /// 比翻 Player.log 快得多。
    /// </summary>
    public static class Diagnostics
    {
        public const string FileName = "AOPatchDiagnostics.txt";

        public static string Build()
        {
            StringBuilder builder = new StringBuilder();

            builder.AppendLine("Atmosphere Overhaul Patch - diagnostics");
            builder.AppendLine("time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            builder.AppendLine("mod: " + (AOPatchMod.Instance != null ? "loaded" : "not created"));
            builder.AppendLine("harmony: " + (AOPatchMod.HarmonyInstance != null ? AOPatchMod.HarmonyInstance.Id : "<null>"));
            builder.AppendLine();

            builder.AppendLine("[AO]");
            builder.AppendLine("available: " + AtmosphereApi.Available);
            builder.AppendLine("capabilities: " + AoCapabilities.Describe(AoCapabilities.Available));
            builder.AppendLine("simulationIntervalTicks: " + AtmosphereApi.SimulationIntervalTicks);
            builder.AppendLine("solverPressureToPascals: " + AtmosphereApi.SolverPressureToPascals);
            builder.AppendLine("hasDeltas: " + AtmosphereApi.HasDeltas);
            builder.AppendLine("hasContinuousSources: " + AtmosphereApi.HasContinuousSources);
            builder.AppendLine("hasGasApi: " + AtmosphereApi.HasGasApi);
            builder.AppendLine();

            AOPatchSettings settings = AOPatchMod.Settings;
            builder.AppendLine("[settings]");
            if (settings == null)
            {
                builder.AppendLine("<null>");
            }
            else
            {
                builder.AppendLine("master: " + settings.MasterEnabled);
                builder.AppendLine("forceField: " + settings.EnableForceField
                                   + " / interval " + settings.ForceFieldIntervalTicks
                                   + " / budget " + settings.ForceFieldCellsPerInterval
                                   + " / killWind " + settings.ForceFieldKillWind);
                builder.AppendLine("forceOverrideBuiltIn: " + settings.ForceOverrideBuiltInCompat);
                builder.AppendLine("cccLocalizedVent: " + settings.EnableCccLocalizedVent);
                builder.AppendLine("verbose: " + settings.VerboseLogging);
            }

            builder.AppendLine();
            builder.AppendLine("[modules]");

            IReadOnlyList<CompatModuleInfo> modules = CompatRegistry.AllModules;
            for (int i = 0; i < modules.Count; i++)
            {
                CompatModuleInfo module = modules[i];
                builder.AppendLine(
                    module.PackageId
                    + " | loaded=" + module.Loaded
                    + " | enabled=" + module.EnabledByUser
                    + " | state=" + module.State
                    + " | folder=" + module.RelativeFolder
                    + " | detail=" + (module.Detail ?? string.Empty));
            }

            builder.AppendLine();
            builder.AppendLine("[mapComponents]");
            if (Find.Maps != null)
            {
                for (int i = 0; i < Find.Maps.Count; i++)
                {
                    Map map = Find.Maps[i];
                    ForceFieldStabilizer stabilizer = map.GetComponent<ForceFieldStabilizer>();

                    int compatComponents = 0;
                    List<MapComponent> components = map.components;
                    if (components != null)
                    {
                        for (int j = 0; j < components.Count; j++)
                        {
                            if (components[j] != null
                                && components[j].GetType().Assembly != typeof(AOPatchMod).Assembly)
                            {
                                compatComponents++;
                            }
                        }
                    }

                    builder.AppendLine(
                        map.uniqueID + " " + map.Parent
                        + " | regions=" + (stabilizer != null ? stabilizer.RegionCount.ToString() : "null")
                        + " | compatComponents=" + compatComponents);
                }
            }

            return builder.ToString();
        }

        public static string Dump()
        {
            string path = Path.Combine(GenFilePaths.ConfigFolderPath, FileName);
            File.WriteAllText(path, Build(), new UTF8Encoding(false));
            return path;
        }
    }
}
