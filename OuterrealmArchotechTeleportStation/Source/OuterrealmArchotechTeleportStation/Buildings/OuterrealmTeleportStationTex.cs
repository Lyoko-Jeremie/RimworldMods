using UnityEngine;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    /// <summary>
    /// 传送站命令图标缓存。
    /// 使用 StaticConstructorOnStartup 让贴图在游戏启动加载 Def 后预先解析，避免每次打开 gizmo 时查找资源。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class OuterrealmTeleportStationTex
    {
        /// <summary>
        /// “选择传送目的地”命令图标；资源缺失时回退到白图，保证按钮仍可显示。
        /// </summary>
        public static readonly Texture2D Teleport2Site =
            ContentFinder<Texture2D>.Get("UI/Commands/OuterrealmTeleport2Site", false) ?? BaseContent.WhiteTex;
        
        /// <summary>
        /// “选择世界投送位置”命令图标；资源缺失时回退到白图，保证按钮仍可显示。
        /// </summary>
        public static readonly Texture2D Teleport2Tile =
            ContentFinder<Texture2D>.Get("UI/Commands/OuterrealmTeleport2Tile", false) ?? BaseContent.WhiteTex;

        /// <summary>
        /// “追加传送站”命令图标；资源缺失时回退到白图。
        /// </summary>
        public static readonly Texture2D AddStation =
            ContentFinder<Texture2D>.Get("UI/Commands/OuterrealmAddTeleportStation", false) ?? BaseContent.WhiteTex;
    }
}
