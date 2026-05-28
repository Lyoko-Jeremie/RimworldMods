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

        public CellRect OccupiedRect => CellRect.CenteredOn(parent.Position, Mathf.CeilToInt(Width), Mathf.CeilToInt(Height));

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref widthOverride, "widthOverride");
            Scribe_Values.Look(ref heightOverride, "heightOverride");
        }

        public override bool IsInside(Vector3 pos)
        {
            Vector3 myPos = parent.Position.ToVector3Shifted();
            return Mathf.Abs(pos.x - myPos.x) <= Width / 2f && Mathf.Abs(pos.z - myPos.z) <= Height / 2f;
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

            if (dx > (Width / 2f + radius)) return false;
            if (dz > (Height / 2f + radius)) return false;

            if (dx <= (Width / 2f)) return true;
            if (dz <= (Height / 2f)) return true;

            float cornerDistanceSq = Mathf.Pow(dx - Width / 2f, 2) + Mathf.Pow(dz - Height / 2f, 2);
            return cornerDistanceSq <= radius * radius;
        }

        public override void DrawShield()
        {
            if (!shieldEnabled) return;

            if (Find.Selector.IsSelected(parent))
            {
                GenDraw.DrawFieldEdges(new List<IntVec3>(OccupiedRect.Cells), Color.white);
            }

            Vector3 drawPos = parent.DrawPos;
            drawPos.y = AltitudeLayer.MoteOverhead.AltitudeFor();

            float currentAlpha = GetCurrentAlpha();
            if (currentAlpha > 0.0f)
            {
                Color color = Props.color;
                color.a *= currentAlpha;

                MaterialPropertyBlock matPropertyBlock = new MaterialPropertyBlock();
                matPropertyBlock.SetColor(ShaderPropertyIDs.Color, color);

                Matrix4x4 matrix = default;
                matrix.SetTRS(drawPos, Quaternion.identity, new Vector3(Width * 1.15f, 1f, Height * 1.15f));
                
                Graphics.DrawMesh(MeshPool.plane10, matrix, MaterialPool.MatFrom("Other/ForceField", ShaderDatabase.MoteGlow), 0, null, 0, matPropertyBlock);
            }
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