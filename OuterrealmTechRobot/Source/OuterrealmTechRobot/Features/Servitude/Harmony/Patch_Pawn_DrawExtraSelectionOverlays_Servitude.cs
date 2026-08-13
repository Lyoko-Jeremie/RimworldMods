using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 主仆连线：选中主人或侍奉者时，绘制粉色连线 + 箭头（箭头指向主人）。
    /// 仅同图双方已生成时绘制；留守模式（standbyMode）下不绘制。
    /// 渲染仅在选中时触发（DrawExtraSelectionOverlays 机制），无持续开销。
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.DrawExtraSelectionOverlays))]
    [StaticConstructorOnStartup]
    public static class Patch_Pawn_DrawExtraSelectionOverlays_Servitude
    {
        private static readonly Material ConnectionLineMat =
            MaterialPool.MatFrom(GenDraw.LineTexPath, ShaderDatabase.Transparent, new Color(1f, 0.4f, 0.8f, 0.8f));

        private static readonly Material ArrowMat =
            MaterialPool.MatFrom("UI/Overlays/Arrow", ShaderDatabase.CutoutFlying01, new Color(1f, 0.4f, 0.8f, 0.8f));

        [HarmonyPostfix]
        public static void Postfix(Pawn __instance)
        {
            if (__instance == null || !__instance.Spawned || __instance.Dead || __instance.Map == null)
            {
                return;
            }

            CompArtificialMaid comp = CompArtificialMaid.GetCompCached(__instance);
            if (comp != null && comp.standbyMode)
            {
                return; // 留守模式：不绘制连线
            }

            ArtificialMaidServitudeManager mgr = ArtificialMaidServitudeManager.Get();
            if (mgr == null)
            {
                return;
            }

            if (mgr.IsMaster(__instance))
            {
                foreach (Pawn servant in mgr.GetServants(__instance))
                {
                    if (servant != null && servant.Spawned && servant.Map == __instance.Map)
                    {
                        DrawServitudePointer(servant, __instance);
                    }
                }
            }
            else if (mgr.IsServant(__instance))
            {
                Pawn master = mgr.GetMaster(__instance);
                if (master != null && master.Spawned && master.Map == __instance.Map)
                {
                    DrawServitudePointer(__instance, master);
                }
            }
        }

        /// <summary>绘制从侍奉者指向主人的连线与箭头。</summary>
        private static void DrawServitudePointer(Pawn servant, Pawn master)
        {
            Vector3 start = servant.TrueCenter();
            Vector3 end = master.TrueCenter();
            GenDraw.DrawLineBetween(start, end, ConnectionLineMat);

            Vector3 delta = end - start;
            if (delta.magnitude <= 0.5f)
            {
                return;
            }

            float angle = delta.AngleFlat();
            Vector3 mid = start + delta * 0.5f;
            mid.y = AltitudeLayer.MetaOverlays.AltitudeFor();
            Matrix4x4 matrix = Matrix4x4.TRS(mid, Quaternion.AngleAxis(angle, Vector3.up), new Vector3(1.5f, 1f, 1.5f));
            Graphics.DrawMesh(MeshPool.plane10, matrix, ArrowMat, 0);
        }
    }
}
