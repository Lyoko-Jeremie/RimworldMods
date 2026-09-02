using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimWorld.IO;
using UnityEngine;
using Verse;
using Object = UnityEngine.Object;

namespace ImageOpt;

[HarmonyPatch(typeof(ModContentPack), "AnyContentLoaded")]
public static class ParallelTextureLoadPatch
{
    internal static Dictionary<int, VirtualFile> Tasks = new();
    internal static HashSet<int> NativeTextures = new();
    internal static bool Once = false;

    public static void Prefix(ref ModContentHolder<Texture2D> ___textures, ModContentPack __instance)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            PrefixV2(ref ___textures, __instance);
        }
        else
        {
            PrefixV1(ref ___textures);
        }
    }

    public static void PrefixV2(ref ModContentHolder<Texture2D> ___textures, ModContentPack mod)
    {
        var time = Stopwatch.StartNew();
        DeepProfiler.Start("ImageOpt.WaitForTextureComplete");
        if (!Once)
        {
            Once = true;
            NativeMethods.wait_load_images();
        }

        List<(Texture2D, Texture2D)> copyTasks = new();

        foreach (var key in ___textures.contentList.Keys.ToList())
        {
            var id = ___textures.contentList[key].GetInstanceID();
            if (!Tasks.ContainsKey(id))
            {
                continue;
            }

            Texture2D texture;
            unsafe
            {
                var textureData = NativeMethods.get_texture(id);
                if ((IntPtr)textureData.ptr == IntPtr.Zero || textureData.error != 0)
                {
                    texture = TextureLoadPatch.VanillaLoadTexture(Tasks[id]);
                    Statistics.fallback += 1;
                    Log.Warning($"[ImageOpt] failed to load image {key}. using vanilla texture load");
                }
                else
                {
                    texture = Texture2D.CreateExternalTexture(textureData.width, textureData.height,
                        (TextureFormat)textureData.format, textureData.mip_count > 1, false, (IntPtr)textureData.ptr);
                    if (textureData.is_dds)
                    {
                        Statistics.loaded_dds += 1;
                    }
                    else
                    {
                        Statistics.loaded_image += 1;
                    }

                    Texture2DPatch.NativeTextures.Add(texture.GetInstanceID());

                    // disable
                    // Switch to Texture2DPatch, but because Unity's native functions cannot be patched,
                    // there will be some limitations, and some special cases may require falling back to this implementation.
                    /*if (new[] { "smashphil.vehicleframework", "oskarpotocki.vanillavehiclesexpanded" }.Contains(
                            mod.PackageId))
                    {
                        var newTexture = new Texture2D(texture.width, texture.height, texture.format
                            , texture.mipmapCount, false, false);
                        // I want to spread it out over multiple frames to avoid graphics driver crashes due to excessive load.
                        // But the Vehicle Framework v1.6.2088 update caches graphics at startup, If it is not copied here, it will cache an incorrect graphic.
                        // ... damn
                        // copyTasks.Add((texture, newTexture));
                        Graphics.CopyTexture(texture, newTexture);
                        texture = newTexture;
                    }*/
                }
            }

            texture.filterMode = FilterMode.Trilinear;
            texture.anisoLevel = ImageOpt.settings.aniso_level <= 0 ? 1 : ImageOpt.settings.aniso_level;
            texture.mipMapBias = ImageOpt.settings.mipmap_bias;
            texture.name = ___textures.contentList[key].name;

            Statistics.video_memory_usage += TextureMemoryEstimator.EstimateTextureMemory(texture);
            Object.DestroyImmediate(___textures.contentList[key]);
            ___textures.contentList[key] = texture;
            Tasks.Remove(id);
        }

        BatchedTextureCopier.instance.copyTasks = copyTasks;
        BatchedTextureCopier.instance.StartCopying();
        DeepProfiler.End();
        ImageOpt.FlushLogger();
        Statistics.load_time += time.Elapsed;
    }

    public static void PrefixV1(ref ModContentHolder<Texture2D> ___textures)
    {
        var time = Stopwatch.StartNew();
        DeepProfiler.Start("ImageOpt.WaitForTextureComplete");
        foreach (var key in ___textures.contentList.Keys.ToList())
        {
            var id = ___textures.contentList[key].GetInstanceID();
            if (!Tasks.ContainsKey(id))
            {
                continue;
            }

            var texture = LoadTextureAndApply(id, ___textures.contentList[key].name);
            if (texture == null)
            {
                texture = TextureLoadPatch.VanillaLoadTexture(Tasks[id]);
                Log.Warning($"[ImageOpt] failed to load image {key}. using vanilla texture load");
            }

            Object.DestroyImmediate(___textures.contentList[key]);
            ___textures.contentList[key] = texture;
            Tasks.Remove(id);
        }

        DeepProfiler.End();
        Statistics.load_time += time.Elapsed;
    }

    public static Texture2D? LoadTextureAndApply(int id, string name = "[unknown]")
    {
        if (!Tasks.ContainsKey(id))
        {
            Log.Warning($"[ImageOpt] try wait texture {name} but not found");
            return null;
        }

        ImageBuffer image;

        unsafe
        {
            image = NativeMethods.get_image_buffer(id, msg =>
            {
                var str = new string((sbyte*)msg);
                Log.Warning(str);
                NativeMethods.free_string(msg);
            });
            if (image.ptr == null)
            {
                return null;
            }
        }

        // if the texture size is not a power of 2 and mipmaps are enabled, Unity may encounter errors.
        // see https://discussions.unity.com/t/d3d11-failed-to-create-2d-texture/620706/16
        var is_power_of_two = Mathf.IsPowerOfTwo(image.width) && Mathf.IsPowerOfTwo(image.height);

        if (image.length <= 0 || image.width <= 0 || image.height <= 0)
        {
            return null;
        }

        try
        {
            DeepProfiler.Start("ImageOpt.LoadTexture.LoadImage.LoadRawTextureData");
            Texture2D texture;
            if (image.is_dds)
            {
                texture = new Texture2D(image.width, image.height, (TextureFormat)image.format,
                    is_power_of_two ? image.mipmap_count : 1, false, true);
                Statistics.loaded_dds += 1;
            }
            else
            {
                texture = new Texture2D(image.width, image.height,
                    TextureFormat.RGBA32,
                    image.mipmap_count > 1 && is_power_of_two);

                Statistics.loaded_image += 1;
            }

            unsafe
            {
                texture.LoadRawTextureData((IntPtr)image.ptr, image.length);
            }

            texture.filterMode = FilterMode.Trilinear;
            texture.anisoLevel = ImageOpt.settings.aniso_level <= 0 ? 1 : ImageOpt.settings.aniso_level;
            texture.mipMapBias = ImageOpt.settings.mipmap_bias;
            texture.Apply(image.mipmap_count <= 1 && is_power_of_two, true);
            texture.name = name;
            Statistics.video_memory_usage += TextureMemoryEstimator.EstimateTextureMemory(texture);
            return texture;
        }
        catch (Exception e)
        {
            Log.Error($"[ImageOpt] load image {name} failed: {e}");
        }
        finally
        {
            NativeMethods.free_buffer(image);
            DeepProfiler.End();
            if ((Statistics.loaded_image + Statistics.loaded_dds) % 500 == 0)
            {
                // DXGI_ERROR_DEVICE_REMOVED errors randomly appear when loading a large number of textures.
                // This may be caused by excessive GPU load. In any case, we flush regularly.
                GL.Flush();
            }
        }

        return null;
    }


    internal static class TextureMemoryEstimator
    {
        public static long EstimateTextureMemory(Texture2D texture)
        {
            if (texture == null)
            {
                return 0;
            }

            var bitsPerPixel = GetBitsPerPixel(texture.format);
            var mainTextureSize = (long)texture.width * texture.height * bitsPerPixel / 8;

            if (texture.mipmapCount > 1)
            {
                return (long)(mainTextureSize * 1.333333f);
            }

            return mainTextureSize;
        }

        private static int GetBitsPerPixel(TextureFormat format)
        {
            return format switch
            {
                TextureFormat.Alpha8 => 8,
                TextureFormat.R16 => 16,
                TextureFormat.RGB24 => 24,
                TextureFormat.RGBA32 or TextureFormat.ARGB32 => 32,
                TextureFormat.DXT1 or TextureFormat.ETC_RGB4 or TextureFormat.ETC2_RGB => 4,
                TextureFormat.DXT5 or TextureFormat.BC7 or TextureFormat.ETC2_RGBA8 => 8,
                _ => 32
            };
        }
    }
}

[HarmonyPatch(typeof(ModContentLoader<Texture2D>), "LoadTexture")]
public static class TextureLoadPatch
{
    internal static bool Started = false;
    internal static bool Once = false;

    [HarmonyPriority(1000)]
    [HarmonyBefore("com.telefonmast.graphicssettings.rimworld.mod")]
    public static bool Prefix(VirtualFile file, ref Texture2D __result)
    {
        if (!Once)
        {
            unsafe
            {
                var ok = NativeMethods.get_device((byte*)Texture2D.normalTexture.GetNativeTexturePtr());
                if (!ok)
                {
                    Log.Error("[ImageOpt] Failed to get device.");
                }
            }

            Once = true;
        }


        DeepProfiler.Start("ImageOpt.LoadTexture");
        var time = Stopwatch.StartNew();

        if (Started)
        {
            __result = _LoadTextureSync(file);
        }
        else
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                __result = _LoadTextureAsyncV2(file);
            }
            else
            {
                __result = _LoadTextureAsync(file);
            }

            Statistics.load_time += time.Elapsed;
        }

        DeepProfiler.End();
        return false;
    }

    private static Texture2D _LoadTextureAsyncV2(VirtualFile file)
    {
        if (!file.Exists)
        {
            return null;
        }

        if (file.Name.EndsWith(".psd") || file.GetType().FullName == "RimWorld.IO.TarFile")
        {
            return VanillaLoadTexture(file);
        }

        var d = new Texture2D(2, 2, TextureFormat.Alpha8, false, false);

        unsafe
        {
            fixed (char* p = file.FullPath)
            {
                NativeMethods.enqueue_image((ushort*)p, file.FullPath.Length, d.GetInstanceID());
            }
        }

        d.name = Path.GetFileNameWithoutExtension(file.Name);
        ParallelTextureLoadPatch.Tasks.Add(d.GetInstanceID(), file);
        return d;
    }

    private static Texture2D _LoadTextureAsync(VirtualFile file)
    {
        if (!file.Exists)
        {
            return null;
        }

        if (file.Name.EndsWith(".psd") || file.GetType().FullName == "RimWorld.IO.TarFile")
        {
            return VanillaLoadTexture(file);
        }

        var d = new Texture2D(2, 2, TextureFormat.Alpha8, false, false, true);
        Task.Run(() =>
        {
            unsafe
            {
                fixed (char* p = file.FullPath)
                {
                    NativeMethods.load_image_async((ushort*)p, file.FullPath.Length, ImageOpt.settings.enable_dds,
                        (msg) =>
                        {
                            var str = new string((sbyte*)msg);
                            Log.Message(str);
                            NativeMethods.free_string(msg);
                        }, d.GetInstanceID());
                }
            }
        });
        d.name = Path.GetFileNameWithoutExtension(file.Name);
        ParallelTextureLoadPatch.Tasks.Add(d.GetInstanceID(), file);
        return d;
    }

    private static Texture2D _LoadTextureSync(VirtualFile file)
    {
        if (!file.Exists)
        {
            return null;
        }

        if (file.Name.EndsWith(".psd") || file.GetType().FullName == "RimWorld.IO.TarFile")
        {
            return VanillaLoadTexture(file);
        }

        Texture2D texture;
        unsafe
        {
            fixed (char* p = file.FullPath)
            {
                var textureData = NativeMethods.load_image_sync((ushort*)p, file.FullPath.Length);
                if ((IntPtr)textureData.ptr == IntPtr.Zero || textureData.error != 0)
                {
                    texture = VanillaLoadTexture(file);
                    Statistics.fallback += 1;
                    Log.Warning($"[ImageOpt] failed to load image {file.Name}. using vanilla texture load");
                }
                else
                {
                    texture = Texture2D.CreateExternalTexture(textureData.width, textureData.height,
                        (TextureFormat)textureData.format, textureData.mip_count > 1, false, (IntPtr)textureData.ptr);
                    if (textureData.is_dds)
                    {
                        Statistics.loaded_dds += 1;
                    }
                    else
                    {
                        Statistics.loaded_image += 1;
                    }
                }
            }
        }

        texture.filterMode = FilterMode.Trilinear;
        texture.anisoLevel = ImageOpt.settings.aniso_level <= 0 ? 1 : ImageOpt.settings.aniso_level;
        texture.mipMapBias = ImageOpt.settings.mipmap_bias;
        texture.name = Path.GetFileNameWithoutExtension(file.Name);

        Statistics.video_memory_usage += ParallelTextureLoadPatch.TextureMemoryEstimator.EstimateTextureMemory(texture);
        ImageOpt.FlushLogger();
        return texture;
    }

    internal static Texture2D VanillaLoadTexture(VirtualFile file)
    {
        if (!file.Exists)
            return null;
        byte[] data = file.ReadAllBytes();
        Texture2D texture2D1;

        DeepProfiler.Start("Vanilla.LoadTexture.LoadImage");
        texture2D1 = new Texture2D(2, 2, TextureFormat.Alpha8, true);
        texture2D1.LoadImage(data);
        DeepProfiler.End();
        if ((texture2D1.width < 4 || texture2D1.height < 4 || !Mathf.IsPowerOfTwo(texture2D1.width)
                ? 0
                : (Mathf.IsPowerOfTwo(texture2D1.height) ? 1 : 0)) == 0 && Prefs.TextureCompression)
        {
            int mipmapsForDxtSupport = StaticTextureAtlas.CalculateMaxMipmapsForDxtSupport(texture2D1);
            if (Prefs.LogVerbose)
                Log.Warning(
                    $"Texture {file.Name} is being reloaded with reduced mipmap count (clamped to {mipmapsForDxtSupport}) due to non-power-of-two dimensions: ({texture2D1.width}x{texture2D1.height}). This will be slower to load, and will look worse when zoomed out. Consider using a power-of-two texture size instead.");
            if (!UnityData.ComputeShadersSupported)
            {
                Texture2D texture2D2 = new Texture2D(texture2D1.width, texture2D1.height, TextureFormat.Alpha8,
                    mipmapsForDxtSupport, false);
                UnityEngine.Object.DestroyImmediate((UnityEngine.Object)texture2D1);
                texture2D1 = texture2D2;
                texture2D1.LoadImage(data);
            }
        }

        if (Prefs.TextureCompression & (texture2D1.width % 4 == 0 && texture2D1.height % 4 == 0))
        {
            DeepProfiler.Start("Vanilla.LoadTexture.Compress");
            if (!UnityData.ComputeShadersSupported)
            {
                texture2D1.Compress(true);
                texture2D1.filterMode = FilterMode.Trilinear;
                texture2D1.anisoLevel = ImageOpt.settings.aniso_level <= 0 ? 1 : ImageOpt.settings.aniso_level;
                texture2D1.mipMapBias = ImageOpt.settings.mipmap_bias;
                texture2D1.Apply(true, true);
            }
            else
            {
                texture2D1.filterMode = FilterMode.Trilinear;
                texture2D1.anisoLevel = ImageOpt.settings.aniso_level <= 0 ? 1 : ImageOpt.settings.aniso_level;
                texture2D1.mipMapBias = ImageOpt.settings.mipmap_bias;
                texture2D1.Apply(true, true);
                texture2D1 = StaticTextureAtlas.FastCompressDXT(texture2D1, true);
            }

            DeepProfiler.End();
        }
        else
        {
            texture2D1.filterMode = FilterMode.Trilinear;
            texture2D1.anisoLevel = ImageOpt.settings.aniso_level <= 0 ? 1 : ImageOpt.settings.aniso_level;
            texture2D1.mipMapBias = ImageOpt.settings.mipmap_bias;
            texture2D1.name = Path.GetFileNameWithoutExtension(file.Name);
            DeepProfiler.Start("Vanilla.LoadTexture.ApplyTexture");
            texture2D1.Apply(true, true);
            DeepProfiler.End();
        }

        texture2D1.name = Path.GetFileNameWithoutExtension(file.Name);
        return texture2D1;
    }
}