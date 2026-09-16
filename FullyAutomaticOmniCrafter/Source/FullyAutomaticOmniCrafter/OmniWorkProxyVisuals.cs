using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 工作代理的专用视觉资源。球形机体贴图在启动阶段一次性构建为 Graphic 并长期持有，
    /// 渲染节点补丁只返回这个共享引用，因此运行时零分配，也不进 GraphicDatabase 查表。
    ///
    /// 这里固定使用 Cutout 着色与白色：全部代理共用同一份 Graphic 与材质。代理数量上限为
    /// 1024，若沿用原版 humanlike 身体的皮肤着色（GraphicDatabase 以含颜色的键缓存），
    /// 每个皮肤色都会派生一份 Graphic 与材质变体，反而比现在更占内存。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class OmniWorkProxyVisuals
    {
        /// <summary>
        /// 球形机体贴图。需要 Textures/Things/Pawn/FAOC_OmniWorkProxy/ 下的
        /// OmniWorkProxySphere_south.png / _east.png / _north.png 三张（缺 _west 时会镜像 _east）。
        /// </summary>
        public static readonly Graphic SphereGraphic = GraphicDatabase.Get<Graphic_Multi>(
            "Things/Pawn/FAOC_OmniWorkProxy/OmniWorkProxySphere",
            ShaderDatabase.Cutout,
            Vector2.one,
            Color.white);
    }
}
