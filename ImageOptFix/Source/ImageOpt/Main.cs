using System;
using System.IO;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ImageOpt;

public class ImageOpt : Mod
{
    public static Settings settings = new();

    public ImageOpt(ModContentPack content) : base(content)
    {
        if (!LoadRustLibrary(content.RootDir, content.ModMetaData.ModVersion))
        {
            return;
        }

        settings = GetSettings<Settings>();
        // Abandoned
        NativeMethods.init_semaphore(settings.image_limit);
        NativeMethods.set_auto_compress(settings.auto_compress);
        var harmony = new Harmony("dev.soeur.ImageOpt");
        harmony.PatchAll();
    }

    public override string SettingsCategory()
    {
        return "ImageOpt".Translate();
    }

    public override void DoSettingsWindowContents(Rect inRect)
    {
        settings.DrawSettingsWindow(inRect);
        base.DoSettingsWindowContents(inRect);
    }

    // Because the damn rimworld will load the dll file in the Assemblies directory as a .net program, which will cause errors if it is not a .net program.
    // So I can only find the dll file manually and then load it.
    public static bool LoadRustLibrary(string root, string version)
    {
        // Some mods such as Prepatcher will change the assembly loading method causing Assembly.Location == null, so we use a more robust method.
        /*var assemblyPath = typeof(ImageOpt).Assembly.Location;
        var modRootPath = Directory.GetParent(Path.GetDirectoryName(assemblyPath) ?? "")?.FullName;*/

        var libName = "";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            libName = "image_lib.dll";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            libName = "libimage_lib.so";
        }

        var libPath = Path.Combine(root, libName);

        if (!File.Exists(libPath))
        {
            Log.Error($"[ImageOpt] Rust DLL not found at the {libPath}.");
            return false;
        }

        if (!SharedLibrary.Load(libPath))
        {
            return false;
        }

        Log.Message($"[ImageOpt] Successfully loaded Rust library into process memory. ver: {version}");
        return true;
    }

    public static void FlushLogger()
    {
        unsafe
        {
            NativeMethods.flush_log(msg =>
            {
                var str = new string((sbyte*)msg);
                Log.Message($"[ImageOpt] {str}");
                NativeMethods.free_string(msg);
            }, msg =>
            {
                var str = new string((sbyte*)msg);
                Log.Warning($"[ImageOpt] {str}");
                NativeMethods.free_string(msg);
            }, msg =>
            {
                var str = new string((sbyte*)msg);
                Log.Error($"[ImageOpt] {str}");
                NativeMethods.free_string(msg);
            });
        }
    }
}

static class Statistics
{
    public static int loaded_image = 0;
    public static int loaded_dds = 0;
    public static int fallback = 0;
    public static long video_memory_usage = 0;

    public static TimeSpan load_time = TimeSpan.Zero;
}

[StaticConstructorOnStartup]
static class OnStartup
{
    static OnStartup()
    {
        LongEventHandler.ExecuteWhenFinished(() => { TextureLoadPatch.Started = true; });
    }
}

public static class SharedLibrary
{
    private static class Linux
    {
        internal const int RTLD_LAZY = 0x00001;
        
        [DllImport("libdl.so", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr dlopen(string filename, int flags);

        [DllImport("libdl.so", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr dlerror();
    }

    private static class Windows
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr LoadLibrary(string lpFileName);
    }

    public static bool Load(string libraryPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Linux.dlerror();
            var handle = Linux.dlopen(libraryPath, Linux.RTLD_LAZY);
            if (handle == IntPtr.Zero)
            {
                var errorMessagePtr = Linux.dlerror();
                var errorMessage = Marshal.PtrToStringAnsi(errorMessagePtr);
                Log.Message($"[Rust] Failed to load {libraryPath} on Linux: {errorMessage}");
                return false;
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var handle = Windows.LoadLibrary(libraryPath);
            if (handle == IntPtr.Zero)
            {
                var errorCode = Marshal.GetLastWin32Error();
                Log.Message($"[Rust] Failed to load {libraryPath} on Windows. Win32 Error Code: {errorCode}");
                return false;
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            /*Linux.dlerror();
            var handle = Linux.dlopen(libraryPath, Linux.RTLD_LAZY);
            if (handle == IntPtr.Zero)
            {
                var errorMessagePtr = Linux.dlerror();
                var errorMessage = Marshal.PtrToStringAnsi(errorMessagePtr);
                Log.Message($"[Rust] Failed to load {libraryPath} on macOS: {errorMessage}");
            }
            return handle;*/
            Log.Message("[Rust] macOS is not supported yet.");
            return false;
        }

        return true;
    }
}