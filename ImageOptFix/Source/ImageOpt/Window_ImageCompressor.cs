using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;
using Verse;

namespace ImageOpt;

public class Window_ImageCompressor : Window
{
    private bool isProcessing = false;
    private float currentProgress = 0f;
    private string currentStatusText = "";
    private string currentProgressText = "";
    private Thread workerThread;
    private bool processCompleted = false;

    private int processed = 0;
    private int total = 0;
    private long size = 0;
    private bool started = false;
    private TimeSpan time = TimeSpan.Zero;

    private Quality quality = Quality.High;
    private bool gen_mipmap = true;
    private bool use_zstd = true;
    private bool overwrite = false;
    private bool auto_scaling = true;

    public override Vector2 InitialSize => new Vector2(700f, 500f);

    enum Quality
    {
        Low = 0,
        Normal,
        High
    }

    public Window_ImageCompressor()
    {
        forcePause = true;
        absorbInputAroundWindow = true;
        doCloseX = true;
    }

    public override void DoWindowContents(Rect inRect)
    {
        var listing = new Listing_Standard();
        listing.Begin(inRect);

        Text.Font = GameFont.Medium;
        listing.Label("ImageOpt.GPUCompressImageTools".Translate());
        Text.Font = GameFont.Small;
        listing.GapLine();

        listing.CheckboxLabeled("ImageOpt.GenMipmaps".Translate(), ref gen_mipmap,
            "ImageOpt.GenMipmaps.desc".Translate().Trim());
        listing.CheckboxLabeled("ImageOpt.zstd".Translate(), ref use_zstd, "ImageOpt.zstd.desc".Translate().Trim());
        listing.CheckboxLabeled("ImageOpt.Overwrite".Translate(), ref overwrite,
            "ImageOpt.Overwrite.desc".Translate().Trim());
        listing.CheckboxLabeled("ImageOpt.AutoScaling".Translate(), ref auto_scaling,
            "ImageOpt.AutoScaling.desc".Translate().Trim());

        if (listing.ButtonTextLabeled("ImageOpt.Quality".Translate(), quality.ToString(),
                tooltip: "ImageOpt.Quality.desc".Translate().Trim()))
        {
            var quality_options = new List<FloatMenuOption>();
            quality_options.Add(new FloatMenuOption(
                "ImageOpt.HighQuality".Translate(), () => { quality = Quality.High; }));
            quality_options.Add(new FloatMenuOption(
                "ImageOpt.NormalQuality".Translate(), () => { quality = Quality.Normal; }));
            quality_options.Add(new FloatMenuOption(
                "ImageOpt.LowQuality".Translate(), () => { quality = Quality.Low; }));
            Find.WindowStack.Add(new FloatMenu(quality_options));
        }

        listing.Gap(24f);

        if (!isProcessing)
        {
            var generateButtonRect = listing.GetRect(40f);
            if (Widgets.ButtonText(generateButtonRect, "ImageOpt.Generate".Translate()))
            {
                isProcessing = true;
                processCompleted = false;
                currentProgress = 0f;
                currentStatusText = "...";

                CompressImages();
            }
        }
        else
        {
            var progressBarRect = listing.GetRect(40f);
            Widgets.DrawBox(progressBarRect);

            var progressFillRect = progressBarRect.ContractedBy(2f);
            progressFillRect.width *= currentProgress;

            var barTexture = processCompleted
                ? SolidColorMaterials.NewSolidColorTexture(new Color(0.2f, 0.8f, 0.2f))
                : Texture2D.whiteTexture;
            GUI.DrawTexture(progressFillRect, barTexture);
            listing.Gap(6f);

            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(progressBarRect, $"{currentProgress:P0}");
            Text.Anchor = TextAnchor.UpperLeft;

            Text.Anchor = TextAnchor.MiddleCenter;
            listing.Label(currentStatusText);
            listing.Gap(6f);
            listing.Label(currentProgressText);
            Text.Anchor = TextAnchor.UpperLeft;

            if (!workerThread.IsAlive)
            {
                currentStatusText = "ImageOpt.AllImagesProcessed".Translate($"{time.TotalSeconds:F2}");
                processCompleted = true;
                currentProgress = 1f;
                processed = total;
            }
        }

        listing.End();
    }

    public override void PostClose()
    {
        if (processCompleted)
        {
            isProcessing = false;
            started = false;
            processed = 0;
            total = 0;
            size = 0;
            time = TimeSpan.Zero;
        }

        base.PostClose();
    }

    private void CompressImages()
    {
        var image_paths = new List<IntPtr>();
        foreach (var mod in LoadedModManager.RunningMods)
        {
            foreach (var folder in mod.foldersToLoadDescendingOrder)
            {
                var utf8 = Encoding.UTF8.GetBytes(Path.Combine(folder, GenFilePaths.TexturesFolder) + "\0");
                var unmanaged = Marshal.AllocHGlobal(utf8.Length);
                Marshal.Copy(utf8, 0, unmanaged, utf8.Length);
                image_paths.Add(unmanaged);
            }
        }

        var thread = new Thread(() =>
        {
            var pointer = Marshal.AllocHGlobal(image_paths.Count * IntPtr.Size);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                unsafe
                {
                    for (var i = 0; i < image_paths.Count; i++)
                    {
                        Marshal.WriteIntPtr(pointer, i * IntPtr.Size, image_paths[i]);
                    }

                    NativeMethods.compress_images((byte**)pointer, image_paths.Count, use_zstd, overwrite, gen_mipmap,
                        auto_scaling, (int)quality,
                        (text, s) =>
                        {
                            var str = new string((sbyte*)text);
                            if (started)
                            {
                                currentStatusText = $"Compressed {Path.GetFileName(str)}";
                                currentProgressText = $"{processed} / {total} ~ {FormatBytes(size)}";
                                currentProgress = (float)processed / total;
                                processed += 1;
                                size += s;
                            }
                            else
                            {
                                currentStatusText = $"Searching for images... {Path.GetFileName(str)}";
                            }

                            NativeMethods.free_string(text);
                        }, i =>
                        {
                            started = true;
                            total = i;
                        }, text =>
                        {
                            var str = new string((sbyte*)text);
                            Log.Warning(str);
                            NativeMethods.free_string(text);
                        });
                }
            }
            finally
            {
                for (var i = 0; i < image_paths.Count; i++)
                {
                    var ptr = Marshal.ReadIntPtr(pointer, i * IntPtr.Size);
                    Marshal.FreeHGlobal(ptr);
                }

                Marshal.FreeHGlobal(pointer);
                time = stopwatch.Elapsed;
            }
        });
        thread.Start();
        workerThread = thread;
    }

    public static string FormatBytes(long bytes)
    {
        var sizes = new[] { "B", "KB", "MB", "GB", "TB" };
        var len = (double)bytes;
        var order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024d;
        }

        if (order == 0)
        {
            return $"{bytes} {sizes[order]}";
        }

        return $"{len:0.##} {sizes[order]}";
    }
}