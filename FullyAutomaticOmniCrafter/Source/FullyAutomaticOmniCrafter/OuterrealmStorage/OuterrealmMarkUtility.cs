using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 标记机制（§7 扩展）：携带"量子链路植入体"（FAOC_QuantumLinkImplant）的 pawn
    /// 与超维存储网络直连——制作产物直接存入超维空间（A3），原料可原地取用（A1/A2，后续）。
    /// 植入体当前通过 dev mode / 存档注入获得；未来可加手术配方。
    /// </summary>
    public static class OuterrealmMarkUtility
    {
        private static HediffDef cachedMarkDef;

        public static HediffDef MarkHediffDef
        {
            get
            {
                if (cachedMarkDef == null)
                {
                    cachedMarkDef = DefDatabase<HediffDef>.GetNamedSilentFail("FAOC_QuantumLinkImplant");
                }
                return cachedMarkDef;
            }
        }

        /// <summary>该 pawn 是否携带量子链路植入体（§7）。</summary>
        public static bool IsMarked(Pawn pawn)
        {
            return pawn != null
                && MarkHediffDef != null
                && pawn.health != null
                && pawn.health.hediffSet != null
                && pawn.health.hediffSet.HasHediff(MarkHediffDef);
        }
    }
}
