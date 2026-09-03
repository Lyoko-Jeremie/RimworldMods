using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using Verse;

namespace FullyAutomaticGrowingZone
{
    /// <summary>
    /// Vanilla Expanded Framework 双作物的可选兼容层。
    /// 不直接引用 VEF 程序集；VEF 未加载时会缓存“不适用”结果并直接返回。
    /// </summary>
    internal static class VefDualCropCompatibility
    {
        private const string ExtensionTypeName = "VEF.Plants.DualCropExtension";

        // Def 在加载完成后不会改变。按 Def 缓存解析结果，避免每次收获都执行反射。
        private static readonly ConcurrentDictionary<ThingDef, DualCropData> dataCache =
            new ConcurrentDictionary<ThingDef, DualCropData>();

        private static readonly ConcurrentDictionary<Type, byte> warnedExtensionTypes =
            new ConcurrentDictionary<Type, byte>();

        public static bool TryGetHarvestYield(Plant plant, out ThingDef productDef, out int productCount)
        {
            productDef = null;
            productCount = 0;

            // 与 VEF 的 PlantCollected Prefix 保持一致：枯萎或不可收获时没有副产物。
            if (plant?.def == null || !plant.CanYieldNow())
                return false;

            DualCropData data = dataCache.GetOrAdd(plant.def, ResolveData);
            if (!data.IsValid)
                return false;

            productCount = (int)(data.OutputAmount * plant.Growth);
            if (productCount <= 0)
                return false;

            if (data.RandomOutput && data.RandomSecondaryOutput != null &&
                data.RandomSecondaryOutput.Count > 0)
            {
                productDef = data.RandomSecondaryOutput[Rand.Range(0, data.RandomSecondaryOutput.Count)];
            }
            else
            {
                // 与 VEF 一致：随机列表为空时回退到固定副产物。
                productDef = data.SecondaryOutput;
            }

            return productDef != null;
        }

        private static DualCropData ResolveData(ThingDef plantDef)
        {
            if (plantDef.modExtensions == null)
                return DualCropData.None;

            for (int i = 0; i < plantDef.modExtensions.Count; i++)
            {
                DefModExtension extension = plantDef.modExtensions[i];
                if (extension == null || extension.GetType().FullName != ExtensionTypeName)
                    continue;

                Type extensionType = extension.GetType();
                try
                {
                    const BindingFlags flags =
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                    FieldInfo secondaryOutputField = extensionType.GetField("secondaryOutput", flags);
                    FieldInfo outputAmountField = extensionType.GetField("outPutAmount", flags);
                    FieldInfo randomOutputField = extensionType.GetField("randomOutput", flags);
                    FieldInfo randomSecondaryOutputField =
                        extensionType.GetField("randomSecondaryOutput", flags);

                    if (outputAmountField == null ||
                        (secondaryOutputField == null && randomSecondaryOutputField == null))
                    {
                        WarnOnce(extensionType, "缺少必要字段");
                        return DualCropData.None;
                    }

                    object amountValue = outputAmountField.GetValue(extension);
                    if (amountValue == null)
                        return DualCropData.None;

                    ThingDef secondaryOutput = secondaryOutputField?.GetValue(extension) as ThingDef;
                    bool randomOutput = randomOutputField != null &&
                                        randomOutputField.GetValue(extension) is bool enabled && enabled;
                    List<ThingDef> randomSecondaryOutput = randomSecondaryOutputField == null
                        ? null
                        : ReadThingDefs(randomSecondaryOutputField.GetValue(extension));

                    int outputAmount = Convert.ToInt32(amountValue);
                    if (outputAmount <= 0 ||
                        (secondaryOutput == null &&
                         (randomSecondaryOutput == null || randomSecondaryOutput.Count == 0)))
                    {
                        return DualCropData.None;
                    }

                    return new DualCropData(
                        secondaryOutput,
                        outputAmount,
                        randomOutput,
                        randomSecondaryOutput);
                }
                catch (Exception exception)
                {
                    WarnOnce(extensionType, exception.GetType().Name + ": " + exception.Message);
                    return DualCropData.None;
                }
            }

            return DualCropData.None;
        }

        private static List<ThingDef> ReadThingDefs(object value)
        {
            if (!(value is IEnumerable enumerable))
                return null;

            List<ThingDef> result = new List<ThingDef>();
            foreach (object item in enumerable)
            {
                if (item is ThingDef thingDef)
                    result.Add(thingDef);
            }

            return result;
        }

        private static void WarnOnce(Type extensionType, string reason)
        {
            if (!warnedExtensionTypes.TryAdd(extensionType, 0))
                return;

            Log.Warning(
                "[FullyAutomaticGrowingZone] 无法读取可选的 VEF 双作物扩展 " +
                extensionType.FullName + "，将跳过副产物。" + reason);
        }

        private sealed class DualCropData
        {
            public static readonly DualCropData None =
                new DualCropData(null, 0, false, null);

            public readonly ThingDef SecondaryOutput;
            public readonly int OutputAmount;
            public readonly bool RandomOutput;
            public readonly List<ThingDef> RandomSecondaryOutput;

            public bool IsValid => OutputAmount > 0 &&
                                   (SecondaryOutput != null ||
                                    (RandomSecondaryOutput != null && RandomSecondaryOutput.Count > 0));

            public DualCropData(
                ThingDef secondaryOutput,
                int outputAmount,
                bool randomOutput,
                List<ThingDef> randomSecondaryOutput)
            {
                SecondaryOutput = secondaryOutput;
                OutputAmount = outputAmount;
                RandomOutput = randomOutput;
                RandomSecondaryOutput = randomSecondaryOutput;
            }
        }
    }
}
