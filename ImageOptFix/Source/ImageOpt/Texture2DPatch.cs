using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ImageOpt;

[HarmonyPatch]
public static class Texture2DPatch
{
    internal static HashSet<int> NativeTextures = new();

    [HarmonyPatch(typeof(Texture2D), "GetPixels32", typeof(int))]
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> GetPixels32(IEnumerable<CodeInstruction> instructions)
    {
        yield return new CodeInstruction(OpCodes.Ldarg_0);
        yield return new CodeInstruction(OpCodes.Ldarg_1);
        yield return new CodeInstruction(OpCodes.Call,
            AccessTools.Method(typeof(Texture2DPatch), nameof(GetPixels32_0)));
        yield return new CodeInstruction(OpCodes.Ret);
    }

    //public static bool GetPixels32_0(ref Texture2D __instance, ref Color32[] __result)
    public static Color32[] GetPixels32_0(Texture2D texture, int miplevel)
    {
        /*if (!NativeTextures.Contains(texture.GetInstanceID()))
        {
            return true;
        }*/

        var temp = CopyTexture(texture, miplevel);
        var res = temp.GetRawTextureData<Color32>().ToArray();
        Object.Destroy(temp);
        return res;
    }

    /*[HarmonyPatch(typeof(Texture2D), "GetPixels", new Type[] { })]
    [HarmonyPrefix]
    public static bool GetPixels0(ref Texture2D __instance)
    {
        GetTexture(ref __instance);
        return true;
    }

    [HarmonyPatch(typeof(Texture2D), "SetPixels",
        new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(Color[]), typeof(int) })]
    [HarmonyPrefix]
    public static bool SetPixels(ref Texture2D __instance)
    {
        GetTexture(ref __instance);
        return true;
    }

    [HarmonyPatch(typeof(Texture2D), "SetPixels32",
        new[] { typeof(Color32[]), typeof(int) })]
    [HarmonyPrefix]
    public static bool SetPixels32_2(ref Texture2D __instance)
    {
        GetTexture(ref __instance);
        return true;
    }

    [HarmonyPatch(typeof(Texture2D), "SetPixels32",
        new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(Color32[]), typeof(int) })]
    [HarmonyPrefix]
    public static bool SetPixels32_6(ref Texture2D __instance)
    {
        GetTexture(ref __instance);
        return true;
    }

    [HarmonyPatch(typeof(Texture2D), "GetPixelImpl",
        new[] { typeof(int), typeof(int), typeof(int), typeof(int) })]
    [HarmonyPrefix]
    public static bool GetPixelImpl(ref Texture2D __instance)
    {
        GetTexture(ref __instance);
        return true;
    }*/
    
    private static Texture2D CopyTexture(Texture2D original, int mipLevel)
    {
        var mipWidth = Mathf.Max(1, original.width >> mipLevel);
        var mipHeight = Mathf.Max(1, original.height >> mipLevel);
        var temp = original;
        if (mipLevel != 0)
        {
            temp = new Texture2D(mipWidth, mipHeight, original.format, false);
            Graphics.CopyTexture(original, 0, mipLevel, temp, 0, 0);
        }
        
        var rt = RenderTexture.GetTemporary(
            mipWidth,
            mipHeight,
            0,
            RenderTextureFormat.Default,
            RenderTextureReadWrite.Default
        );

        Graphics.Blit(temp, rt);
        var previous = RenderTexture.active;
        RenderTexture.active = rt;

        var coped = new Texture2D(mipWidth, mipHeight, TextureFormat.RGBA32, false);
        coped.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);

        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);
        if (mipLevel != 0)
            Object.Destroy(temp);
        return coped;
    }
}