using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using Verse;

namespace FullyAutoHydroponicsThingComp
{
    /// <summary>
    /// Optional compatibility for Vanilla Expanded Framework dual-crop plants.
    ///
    /// This class deliberately does not reference VEF at compile time. When VEF is
    /// loaded, its DefModExtension is identified by its fully-qualified type name
    /// and its public data fields are read through cached reflection metadata.
    /// </summary>
    internal static class VefDualCropCompatibility
    {
        private const string ExtensionTypeName = "VEF.Plants.DualCropExtension";

        private static readonly Dictionary<Type, ExtensionFields> fieldCache =
            new Dictionary<Type, ExtensionFields>();

        private static readonly HashSet<Type> warnedExtensionTypes = new HashSet<Type>();

        public static IEnumerable<ThingDefCountClass> GetAdditionalHarvestYield(Plant plant)
        {
            if (plant?.def?.modExtensions == null || !plant.CanYieldNow())
                yield break;

            foreach (DefModExtension extension in plant.def.modExtensions)
            {
                if (extension == null || extension.GetType().FullName != ExtensionTypeName)
                    continue;

                if (TryReadProduct(extension, plant.Growth, out ThingDef productDef, out int productCount))
                    yield return new ThingDefCountClass(productDef, productCount);

                // VEF's GetModExtension<DualCropExtension>() also uses only the first match.
                yield break;
            }
        }

        private static bool TryReadProduct(
            DefModExtension extension,
            float growth,
            out ThingDef productDef,
            out int productCount)
        {
            productDef = null;
            productCount = 0;

            Type extensionType = extension.GetType();
            try
            {
                ExtensionFields fields = GetFields(extensionType);
                if (!fields.IsUsable)
                {
                    WarnOnce(extensionType, "required fields were not found");
                    return false;
                }

                object amountValue = fields.OutputAmount.GetValue(extension);
                if (amountValue == null)
                    return false;

                int baseAmount = Convert.ToInt32(amountValue);
                productCount = (int)(baseAmount * growth);
                if (productCount <= 0)
                    return false;

                bool useRandomOutput = fields.RandomOutput != null &&
                                       fields.RandomOutput.GetValue(extension) is bool randomOutput &&
                                       randomOutput;

                if (useRandomOutput && fields.RandomSecondaryOutput != null)
                {
                    List<ThingDef> choices = ReadThingDefs(fields.RandomSecondaryOutput.GetValue(extension));
                    if (choices.Count > 0)
                        productDef = choices[Rand.Range(0, choices.Count)];
                }

                // This matches VEF's behavior: an empty random-output list falls
                // back to secondaryOutput when one is configured.
                if (productDef == null && fields.SecondaryOutput != null)
                    productDef = fields.SecondaryOutput.GetValue(extension) as ThingDef;

                return productDef != null;
            }
            catch (Exception exception)
            {
                WarnOnce(extensionType, exception.GetType().Name + ": " + exception.Message);
                productDef = null;
                productCount = 0;
                return false;
            }
        }

        private static ExtensionFields GetFields(Type extensionType)
        {
            if (!fieldCache.TryGetValue(extensionType, out ExtensionFields fields))
            {
                fields = new ExtensionFields(extensionType);
                fieldCache.Add(extensionType, fields);
            }

            return fields;
        }

        private static List<ThingDef> ReadThingDefs(object value)
        {
            List<ThingDef> result = new List<ThingDef>();
            if (!(value is IEnumerable enumerable))
                return result;

            foreach (object item in enumerable)
            {
                if (item is ThingDef thingDef)
                    result.Add(thingDef);
            }

            return result;
        }

        private static void WarnOnce(Type extensionType, string reason)
        {
            if (!warnedExtensionTypes.Add(extensionType))
                return;

            Log.Warning(
                "[FullyAutomaticHydroponics] Could not read optional VEF dual-crop extension " +
                extensionType.FullName + "; secondary harvest output will be skipped. " + reason);
        }

        private sealed class ExtensionFields
        {
            private const BindingFlags FieldFlags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            public readonly FieldInfo SecondaryOutput;
            public readonly FieldInfo OutputAmount;
            public readonly FieldInfo RandomOutput;
            public readonly FieldInfo RandomSecondaryOutput;

            public bool IsUsable => OutputAmount != null &&
                                    (SecondaryOutput != null || RandomSecondaryOutput != null);

            public ExtensionFields(Type extensionType)
            {
                SecondaryOutput = extensionType.GetField("secondaryOutput", FieldFlags);
                OutputAmount = extensionType.GetField("outPutAmount", FieldFlags);
                RandomOutput = extensionType.GetField("randomOutput", FieldFlags);
                RandomSecondaryOutput = extensionType.GetField("randomSecondaryOutput", FieldFlags);
            }
        }
    }
}
