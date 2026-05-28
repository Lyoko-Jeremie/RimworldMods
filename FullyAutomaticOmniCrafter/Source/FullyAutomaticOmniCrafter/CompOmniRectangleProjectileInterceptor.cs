using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter
{
    public class CompProperties_CompOmniRectangleProjectileInterceptor : CompProperties_OmniProjectileInterceptor
    {
        public float? width;
        public float? height;

        public CompProperties_CompOmniRectangleProjectileInterceptor()
        {
            compClass = typeof(CompOmniRectangleProjectileInterceptor);
        }
    }
    
    [StaticConstructorOnStartup]
    public static class CompOmniRectangleProjectileInterceptorTex
    {
        public static readonly Texture2D IconShieldEnabled =
            ContentFinder<Texture2D>.Get("UI/Commands/OmniProjectileInterceptor_ShieldEnabled", false)
            ?? BaseContent.WhiteTex;
        public static readonly Texture2D IconRangeSlider =
            ContentFinder<Texture2D>.Get("UI/Commands/OmniProjectileInterceptor_RangeSlider", false)
            ?? BaseContent.WhiteTex;
        public static readonly Texture2D IconInterceptSkyfaller =
            ContentFinder<Texture2D>.Get("UI/Commands/OmniProjectileInterceptor_InterceptSkyfaller", false)
            ?? BaseContent.WhiteTex;
        public static readonly Texture2D IconAlwaysVisible =
            ContentFinder<Texture2D>.Get("UI/Commands/OmniProjectileInterceptor_AlwaysVisible", false)
            ?? BaseContent.WhiteTex;
    }
    
    /// <summary>
    /// 一个方形的 CompOmniProjectileInterceptor 
    /// </summary>
    public class CompOmniRectangleProjectileInterceptor : CompOmniProjectileInterceptor
    {
        public new CompProperties_CompOmniRectangleProjectileInterceptor Props => (CompProperties_CompOmniRectangleProjectileInterceptor)props;

        private float? widthOverride;
        private float? heightOverride;

        public float Width => widthOverride ?? Props.width ?? 1f;
        public float Height => heightOverride ?? Props.height ?? 1f;

        public override float Radius => Mathf.Max(Width, Height) / 2f; // 给基类一个参考半径，虽然我们重写了判定

        public CellRect OccupiedRect
        {
            get
            {
                Vector3 myPos = parent.Position.ToVector3Shifted();
                float halfW = Width / 2f;
                float halfH = Height / 2f;
                // 使用更精确的浮点数边界判定，确保细微变化能反映在格子上
                int minX = Mathf.RoundToInt(myPos.x - halfW);
                int maxX = Mathf.RoundToInt(myPos.x + halfW - 1f);
                int minZ = Mathf.RoundToInt(myPos.z - halfH);
                int maxZ = Mathf.RoundToInt(myPos.z + halfH - 1f);
                
                // 确保至少占一格，且处理宽度小于1的情况
                if (maxX < minX) maxX = minX;
                if (maxZ < minZ) maxZ = minZ;
                
                return new CellRect(minX, minZ, maxX - minX + 1, maxZ - minZ + 1);
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref widthOverride, "widthOverride");
            Scribe_Values.Look(ref heightOverride, "heightOverride");
        }

        public override bool IsInside(Vector3 pos)
        {
            Vector3 myPos = parent.Position.ToVector3Shifted();
            return Mathf.Abs(pos.x - myPos.x) <= Width / 2f + 0.01f && Mathf.Abs(pos.z - myPos.z) <= Height / 2f + 0.01f;
        }

        public override bool IsCellInside(IntVec3 cell)
        {
            return OccupiedRect.Contains(cell);
        }

        public override bool Intersects(IntVec3 center, float radius)
        {
            // 圆与矩形的相交检测
            Vector3 myPos = parent.Position.ToVector3Shifted();
            float dx = Mathf.Abs(center.x - myPos.x);
            float dz = Mathf.Abs(center.z - myPos.z);

            float halfW = Width / 2f;
            float halfH = Height / 2f;

            if (dx > (halfW + radius)) return false;
            if (dz > (halfH + radius)) return false;

            if (dx <= halfW) return true;
            if (dz <= halfH) return true;

            float cornerDistanceSq = Mathf.Pow(dx - halfW, 2) + Mathf.Pow(dz - halfH, 2);
            return cornerDistanceSq <= radius * radius;
        }

        public override void DrawShield()
        {
            if (!Active) return;

            float currentAlpha = GetCurrentAlpha();
            if (currentAlpha <= 0f) return;

            if (Find.Selector.IsSelected(parent))
            {
                // 绘制选中时的白框，使用固定的透明度（由 GetCurrentAlpha 计算，但保持原始逻辑）
                GenDraw.DrawFieldEdges(new List<IntVec3>(OccupiedRect.Cells), Color.white * currentAlpha);
                // 强制重绘选中的范围圈，即使是在暂停时
                parent.Map.GetComponent<OmniInterceptorTracker>()?.DirtyCache();
            }

            Vector3 drawPos = parent.DrawPos;
            drawPos.y = AltitudeLayer.MoteOverhead.AltitudeFor();

            Color color = Props.color;
            // 将 idleAlphaMultiplier 应用到颜色上，这样它会影响填充效果的亮度
            color.a *= currentAlpha * 0.3f; 

            MaterialPropertyBlock matPropertyBlock = new MaterialPropertyBlock();
            matPropertyBlock.SetColor(ShaderPropertyIDs.Color, color);

            Matrix4x4 matrix = default;
            // MeshPool.plane10 是 10x10 的，所以缩放需要除以 10
            matrix.SetTRS(drawPos, Quaternion.identity, new Vector3(Width / 10f, 1f, Height / 10f));
            
            Graphics.DrawMesh(MeshPool.plane10, matrix, MaterialPool.MatFrom("Things/Mote/Transparent", ShaderDatabase.MoteGlow), 0, null, 0, matPropertyBlock);
        }

        public void SetSize(float width, float height)
        {
            widthOverride = width;
            heightOverride = height;
            parent.Map?.GetComponent<OmniInterceptorTracker>()?.DirtyCache();
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (var gizmo in base.CompGetGizmosExtra())
            {
                // 移除基类的半径设置，替换为宽高设置
                if (gizmo is Command_Action action && action.defaultLabel == "OmniInterceptor_SetRadius".Translate())
                    continue;
                yield return gizmo;
            }

            yield return new Command_Action
            {
                defaultLabel = "OmniInterceptor_SetSize".Translate(),
                defaultDesc = "OmniInterceptor_SetSizeDesc".Translate(),
                icon = CompOmniRectangleProjectileInterceptorTex.IconRangeSlider,
                action = () => Find.WindowStack.Add(new Dialog_OmniRectangleInterceptorSettings(this))
            };
        }
        
        // 辅助方法，因为基类是私有的
        private float GetCurrentAlpha()
        {
            if (!alwaysVisible && !Find.Selector.IsSelected(parent)) return 0f;
            float baseMinIdleAlpha = Mathf.Max(0.05f, Props.minIdleAlpha);
            float idleAlpha = Mathf.Lerp(baseMinIdleAlpha, 0.11f, (Mathf.Sin((float)(Gen.HashCombineInt(parent.thingIDNumber, 96804938) % 100) + Time.realtimeSinceStartup * Props.idlePulseSpeed) + 1f) / 2f);
            
            if (Find.Selector.IsSelected(parent))
            {
                float pulseSpeed = Mathf.Max(2f, Props.idlePulseSpeed);
                float selectedAlpha = Mathf.Lerp(0.2f, 0.62f, (Mathf.Sin((float)(Gen.HashCombineInt(parent.thingIDNumber, 35990913) % 100) + Time.realtimeSinceStartup * pulseSpeed) + 1f) / 2f);
                return Mathf.Max(idleAlpha * idleAlphaMultiplier, selectedAlpha);
            }

            return Mathf.Max(idleAlpha * idleAlphaMultiplier, Mathf.Max(Props.minAlpha, 0.05f));
        }
    }

    public class Dialog_OmniRectangleInterceptorSettings : Window
    {
        private CompOmniRectangleProjectileInterceptor comp;
        private float width;
        private float height;
        private string widthBuffer;
        private string heightBuffer;
        private float idleAlphaMultiplier;

        public override Vector2 InitialSize => new Vector2(400f, 350f);

        public Dialog_OmniRectangleInterceptorSettings(CompOmniRectangleProjectileInterceptor comp)
        {
            this.comp = comp;
            this.width = comp.Width;
            this.height = comp.Height;
            this.widthBuffer = width.ToString("0.0");
            this.heightBuffer = height.ToString("0.0");
            this.idleAlphaMultiplier = comp.idleAlphaMultiplier;
            this.doCloseButton = true;
            this.doCloseX = true;
            this.forcePause = true;
            this.absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            // 宽度设置
            listing.Label("OmniInterceptor_Width".Translate() + ": " + width.ToString("0.0"));
            float newWidth = listing.Slider(width, 1f, 256f);
            if (newWidth != width)
            {
                width = newWidth;
                widthBuffer = width.ToString("0.0");
                comp.SetSize(width, height);
            }

            Rect widthInputRect = listing.GetRect(24f);
            Widgets.Label(widthInputRect.LeftPart(0.4f), "OmniInterceptor_WidthInput".Translate());
            string wBuffer = Widgets.TextField(widthInputRect.RightPart(0.6f), widthBuffer);
            if (wBuffer != widthBuffer)
            {
                widthBuffer = wBuffer;
                if (float.TryParse(widthBuffer, out float parsed) && parsed >= 1f && parsed <= 256f)
                {
                    width = parsed;
                    comp.SetSize(width, height);
                }
            }

            listing.Gap();

            // 高度设置
            listing.Label("OmniInterceptor_Height".Translate() + ": " + height.ToString("0.0"));
            float newHeight = listing.Slider(height, 1f, 256f);
            if (newHeight != height)
            {
                height = newHeight;
                heightBuffer = height.ToString("0.0");
                comp.SetSize(width, height);
            }

            Rect heightInputRect = listing.GetRect(24f);
            Widgets.Label(heightInputRect.LeftPart(0.4f), "OmniInterceptor_HeightInput".Translate());
            string hBuffer = Widgets.TextField(heightInputRect.RightPart(0.6f), heightBuffer);
            if (hBuffer != heightBuffer)
            {
                heightBuffer = hBuffer;
                if (float.TryParse(heightBuffer, out float parsed) && parsed >= 1f && parsed <= 256f)
                {
                    height = parsed;
                    comp.SetSize(width, height);
                }
            }

            listing.Gap();

            // 亮度设置
            listing.Label("OmniInterceptor_IdleAlphaMultiplier".Translate() + ": " + idleAlphaMultiplier.ToString("P0"));
            float newIdleAlphaMultiplier = listing.Slider(idleAlphaMultiplier, 0f, 10f);
            if (newIdleAlphaMultiplier != idleAlphaMultiplier)
            {
                idleAlphaMultiplier = newIdleAlphaMultiplier;
                comp.idleAlphaMultiplier = idleAlphaMultiplier;
            }

            listing.End();
        }
    }
}