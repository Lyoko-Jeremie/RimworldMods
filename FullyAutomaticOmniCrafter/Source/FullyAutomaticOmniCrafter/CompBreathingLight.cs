using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    // ========================================================
    // 1. 属性类 (用于读取 XML 中的配置)
    // ========================================================
    public class CompProperties_BreathingLight : CompProperties
    {
        // 必填：灯光图层(透明底+灯条)的贴图路径
        public string texPath;

        // 选填：可以通过 XML 修改灯光颜色，默认为纯白 (不改变贴图颜色)
        public Color color = Color.white;

        // 选填：呼吸速度
        public float speed = 3.0f;

        // 选填：呼吸到最暗时的透明度 (0为完全不可见)
        public float minAlpha = 0.2f;

        // 选填：呼吸到最亮时的透明度 (1为完全不透明)
        public float maxAlpha = 1.0f;

        public CompProperties_BreathingLight()
        {
            // 绑定对应的逻辑类
            this.compClass = typeof(CompBreathingLight);
        }
    }

    // ========================================================
    // 2. 逻辑类 (用于在游戏中实际执行渲染逻辑)
    // ========================================================
    public class CompBreathingLight : ThingComp
    {
        // 方便获取关联的 XML 属性
        public CompProperties_BreathingLight Props => (CompProperties_BreathingLight)props;

        // 缓存贴图数据
        private Graphic overlayGraphic;

        // 核心性能优化：全局唯一的属性块，避免每帧产生 GC 垃圾
        private static MaterialPropertyBlock matPropertyBlock = new MaterialPropertyBlock();

        // 建筑生成时调用一次
        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);

            LongEventHandler.ExecuteWhenFinished(() =>
            {
                if (string.IsNullOrEmpty(Props.texPath))
                {
                    Log.Error($"[{parent.def.defName}] CompBreathingLight: texPath 不能为空！");
                    return;
                }

                // 获取我们加载的 Shader（如果没加载成功，为了防崩溃退回到普通透明材质）
                // 前提是你之前写了 ModAssets.BreathingLightShader 的静态加载类
                Shader shader = ModAssets.BreathingLightShader ?? ShaderDatabase.Transparent;

                // 初始化图层 Graphic
                overlayGraphic = GraphicDatabase.Get<Graphic_Single>(
                    Props.texPath,
                    shader,
                    parent.def.graphicData.drawSize,
                    Props.color
                );
            });
        }

        // 每一帧都会被游戏引擎调用来渲染模型
        public override void PostDraw()
        {
            base.PostDraw();
            if (overlayGraphic == null) return;

            // 1. 计算图层高度，确保它正好叠在建筑基底的正上方，防止Z轴闪烁
            Vector3 drawPos = parent.DrawPos;
            drawPos.y += Altitudes.AltInc;

            // 2. 准备材质属性块
            matPropertyBlock.Clear();

            // 这里的字符串 "_Speed" 必须与你 ShaderLab 里的 Properties 名字一模一样
            matPropertyBlock.SetFloat("_Speed", Props.speed);
            matPropertyBlock.SetFloat("_MinAlpha", Props.minAlpha);
            matPropertyBlock.SetFloat("_MaxAlpha", Props.maxAlpha);

            // 3. 构造旋转和缩放矩阵 (跟随建筑本体的旋转和大小)
            Matrix4x4 matrix = Matrix4x4.TRS(
                drawPos,
                parent.Rotation.AsQuat,
                new Vector3(parent.def.graphicData.drawSize.x, 1f, parent.def.graphicData.drawSize.y)
            );

            // 4. 调用极低开销的底层 API 进行绘制，并传入我们的属性块
            Graphics.DrawMesh(
                MeshPool.plane10,
                matrix,
                overlayGraphic.MatSingle,
                0,
                null,
                0,
                matPropertyBlock
            );
        }
    }
}