using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 兼容 BetterArchitect（Steam 3563882422）的"按材料显示地板 / 默认材料"逻辑。
    /// 该 mod 复制了原版 Designator_Build.ProcessInput 的"用 map.listerThings.ThingsOfDef 判断材料是否存在"
    /// 的写法：PopulateMaterials（地板按材料分组）与 GetStuffFrom（stuff 建筑默认材料）都通过
    /// listerThings.ThingsOfDef 判断材料是否可及。vault 的视图副本未 Spawned，不在 listerThings 中，
    /// 因此地板/材料仅存于 vault 时不会被显示。
    ///
    /// 这里在 BetterArchitect 已加载的前提下，用 Transpiler 把这两个私有方法内的 ThingsOfDef 调用
    /// 替换为 ThingsOfDefWithVault（listerThings 为空时回退查询 vault），从而在不修改 BetterArchitect
    /// 源码的前提下让 vault 材料被识别。
    /// </summary>
    internal static class BetterArchitectCompat
    {
        private const string TypeName = "BetterArchitect.ArchitectCategoryTab_DesignationTabOnGUI_Patch";

        /// <summary>复用列表：BetterArchitect 仅在调用点立即消费（.Any()/.Count），不会跨帧持有引用。</summary>
        private static readonly List<Thing> VaultSentinel = new List<Thing>();

        public static bool TryPatch(Harmony harmony)
        {
            if (harmony == null)
            {
                return false;
            }
            System.Type baType = AccessTools.TypeByName(TypeName);
            if (baType == null)
            {
                return false; // BetterArchitect 未加载
            }
            MethodInfo pop = AccessTools.Method(baType, "PopulateMaterials");
            MethodInfo stuff = AccessTools.Method(baType, "GetStuffFrom");
            MethodInfo transpiler = AccessTools.Method(typeof(BetterArchitectCompat), nameof(Transpiler));
            if (transpiler == null)
            {
                return false;
            }
            bool any = false;
            if (pop != null)
            {
                harmony.Patch(pop, transpiler: new HarmonyMethod(transpiler));
                any = true;
            }
            if (stuff != null)
            {
                harmony.Patch(stuff, transpiler: new HarmonyMethod(transpiler));
                any = true;
            }
            return any;
        }

        /// <summary>
        /// Transpiler 替换点：listerThings 有材料时原样返回；为空但 vault 中有该 def 材料时，
        /// 返回一个含 vault 视图副本的非空列表，使 BetterArchitect 的 .Any()/.Count&gt;0 检查通过。
        /// </summary>
        public static List<Thing> ThingsOfDefWithVault(ListerThings listerThings, ThingDef def)
        {
            List<Thing> list = listerThings.ThingsOfDef(def);
            if (list == null || list.Count > 0)
            {
                return list;
            }
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            Map map = Find.CurrentMap;
            if (gs == null || map == null || def == null || !gs.HasVaultOnMap(map))
            {
                return list;
            }
            if (gs.TotalCountOf(def) <= 0)
            {
                return list;
            }
            Thing copy = FindVaultCopy(gs, map, def);
            if (copy == null)
            {
                return list;
            }
            VaultSentinel.Clear();
            VaultSentinel.Add(copy);
            return VaultSentinel;
        }

        private static Thing FindVaultCopy(GameComponent_OuterrealmStorage gs, Map map, ThingDef def)
        {
            List<Building_OuterrealmVault> vaults = gs.VaultsForReading;
            for (int i = 0; i < vaults.Count; i++)
            {
                Building_OuterrealmVault v = vaults[i];
                if (v == null || !v.Spawned || v.Map != map || v.view == null)
                {
                    continue;
                }
                List<Thing> copies = v.view.InnerListForReading;
                for (int j = 0; j < copies.Count; j++)
                {
                    Thing c = copies[j];
                    if (c == null)
                    {
                        continue;
                    }
                    Thing inner = c.GetInnerIfMinified();
                    if (inner != null && inner.def == def)
                    {
                        OuterrealmEntry e = gs.FindEntry(OuterrealmEntryKey.From(c));
                        if (e != null && e.Count > 0)
                        {
                            return c;
                        }
                    }
                }
            }
            return null;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo original = AccessTools.Method(typeof(ListerThings), "ThingsOfDef", new[] { typeof(ThingDef) });
            MethodInfo replacement = AccessTools.Method(typeof(BetterArchitectCompat), nameof(ThingsOfDefWithVault));
            foreach (CodeInstruction code in instructions)
            {
                if (original != null && replacement != null && code.Calls(original))
                {
                    yield return new CodeInstruction(OpCodes.Call, replacement);
                }
                else
                {
                    yield return code;
                }
            }
        }
    }
}
