using HarmonyLib;
using Verse;

namespace EggSafeBox
{
    internal class EggSafeBoxMod : Mod
    {
        public EggSafeBoxMod(ModContentPack content) : base(content)
        {
            new Harmony(nameof(EggSafeBoxMod)).PatchAll();
        }
    }
}