using UnityEngine;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    [StaticConstructorOnStartup]
    public static class OuterrealmTeleportStationTex
    {
        public static readonly Texture2D Teleport =
            ContentFinder<Texture2D>.Get("UI/Commands/OuterrealmTeleport", false) ?? BaseContent.WhiteTex;

        public static readonly Texture2D AddStation =
            ContentFinder<Texture2D>.Get("UI/Commands/OuterrealmAddTeleportStation", false) ?? BaseContent.WhiteTex;
    }
}
