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
                int minX = Mathf.FloorToInt(myPos.x - halfW + 0.001f);
                int maxX = Mathf.FloorToInt(myPos.x + halfW + 0.001f);
                int minZ = Mathf.FloorToInt(myPos.z - halfH + 0.001f);
                int maxZ = Mathf.FloorToInt(myPos.z + halfH + 0.001f);
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
            Vector3 myPos = parent.Position.ToVector3Shifted();
            // 使用与 RebuildCache 相同的边界逻辑
            float halfW = Width / 2f;
            float halfH = Height / 2f;
            return (float)cell.x >= (myPos.x - halfW - 0.001f) && (float)cell.x <= (myPos.x + halfW + 0.001f) &&
                   (float)cell.z >= (myPos.z - halfH - 0.001f) && (float)cell.z <= (myPos.z + halfH + 0.001f);
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
            if (!shieldEnabled) return;

            float currentAlpha = GetCurrentAlpha();
            if (currentAlpha <= 0f) return;

            if (Find.Selector.IsSelected(parent) || alwaysVisible)
            {
                GenDraw.DrawFieldEdges(new List<IntVec3>(OccupiedRect.Cells), Color.white * currentAlpha);
            }

            Vector3 drawPos = parent.DrawPos;
            drawPos.y = AltitudeLayer.MoteOverhead.AltitudeFor();

            Color color = Props.color;
            color.a *= currentAlpha * 0.3f; // 进一步降低填充透明度，使边缘更明显

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
            idleAlpha *= idleAlphaMultiplier;
            
            if (Find.Selector.IsSelected(parent))
            {
                float pulseSpeed = Mathf.Max(2f, Props.idlePulseSpeed);
                float selectedAlpha = Mathf.Lerp(0.2f, 0.62f, (Mathf.Sin((float)(Gen.HashCombineInt(parent.thingIDNumber, 35990913) % 100) + Time.realtimeSinceStartup * pulseSpeed) + 1f) / 2f);
                return Mathf.Max(idleAlpha, selectedAlpha);
            }

            return Mathf.Max(idleAlpha, Mathf.Max(Props.minAlpha, 0.05f));
        }
    }

    public class Dialog_OmniRectangleInterceptorSettings : Window
    {
        private CompOmniRectangleProjectileInterceptor comp;
        private float width;
        private float height;

        public override Vector2 InitialSize => new Vector2(400f, 300f);

        public Dialog_OmniRectangleInterceptorSettings(CompOmniRectangleProjectileInterceptor comp)
        {
            this.comp = comp;
            this.width = comp.Width;
            this.height = comp.Height;
            this.doCloseButton = true;
            this.doCloseX = true;
            this.forcePause = true;
            this.absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.Label("OmniInterceptor_Width".Translate() + ": " + width.ToString("0.0"));
            width = listing.Slider(width, 1f, 256f);

            listing.Label("OmniInterceptor_Height".Translate() + ": " + height.ToString("0.0"));
            height = listing.Slider(height, 1f, 256f);

            if (GUI.changed)
            {
                comp.SetSize(width, height);
            }

            listing.End();
        }
    }
}