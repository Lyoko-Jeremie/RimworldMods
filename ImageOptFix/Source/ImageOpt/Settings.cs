using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using UnityEngine.Profiling;
using Verse;

namespace ImageOpt;

public class Settings : ModSettings
{
    public bool enable_dds = true;
    public float mipmap_bias = -0.5f;
    public int aniso_level = 1;
    public int image_limit = 5000;
    public bool auto_compress = true;

    private static Window_ImageCompressor? ImageCompressor;

    public override void ExposeData()
    {
        Scribe_Values.Look(ref enable_dds, "enable_dds", true);
        Scribe_Values.Look(ref mipmap_bias, "mipmap_bias", -0.5f);
        Scribe_Values.Look(ref aniso_level, "aniso_level", 1);
        Scribe_Values.Look(ref image_limit, "image_limit", 5000);
        Scribe_Values.Look(ref auto_compress, "auto_compress", true);
        base.ExposeData();
    }

    public void DrawSettingsWindow(Rect rect)
    {
        var listing = new Listing_Standard();
        listing.Begin(rect);

        var virInfoRect = listing.GetRect(0).ContractedBy(10f) with
        {
            height = float.MaxValue
        };
        var infoListing = new Listing_Standard();
        infoListing.Begin(virInfoRect);

        var font = Text.Font;
        Text.Font = GameFont.Medium;
        infoListing.Label("ImageOpt.LoadInfo".Translate());
        Text.Font = font;
        infoListing.GapLine();
        infoListing.Label("ImageOpt.DdsImageLoaded".Translate(Statistics.loaded_dds));
        infoListing.Label("ImageOpt.OtherImageLoaded".Translate(Statistics.loaded_image));
        infoListing.Label("ImageOpt.FallbackLoaded".Translate(Statistics.fallback));
        infoListing.Label("ImageOpt.NeedUpdate".Translate(NativeMethods.get_expired()));
        infoListing.Label(
            "ImageOpt.EstimatedVideoMem".Translate(Window_ImageCompressor.FormatBytes(Statistics.video_memory_usage)));
        infoListing.Label(
            "ImageOpt.LoadTimeS".Translate(
                $"{(int)Statistics.load_time.TotalSeconds}.{Statistics.load_time.Milliseconds:D3}"));
        infoListing.End();

        var infoCardRect = listing.GetRect(infoListing.CurHeight);

        Widgets.DrawBoxSolidWithOutline(infoCardRect with { height = infoCardRect.height + 15 }, Color.clear,
            new Color(0.6f, 0.6f, 0.6f, 0.5f), 2);

        listing.Gap(24f);
        listing.CheckboxLabeled("ImageOpt.EnableDds".Translate(), ref enable_dds,
            "ImageOpt.EnableDds.desc".Translate());
        listing.Gap();
        listing.CheckboxLabeled("ImageOpt.AutoCompress".Translate(), ref auto_compress,
            "ImageOpt.AutoCompress.desc".Translate());
        /*listing.Gap();
        var image_limit_buffer = image_limit.ToString();
        var inputRect = listing.GetRect(Text.LineHeight);
        TextFieldNumericLabeled(inputRect, "ImageOpt.ImageLoadingLimit".Translate(), ref image_limit,
            ref image_limit_buffer, 100);
        TooltipHandler.TipRegionByKey(inputRect, "ImageOpt.ImageLoadingLimit.desc");*/
        listing.Gap(24f);
        var mract = listing.GetRect(30f);
        mipmap_bias = Widgets.HorizontalSlider(mract, mipmap_bias, -2f, 1f, true,
            $"{"ImageOpt.MipmapBias".Translate()}: {mipmap_bias:F1}", "ImageOpt.Quality".Translate(),
            "ImageOpt.Performance".Translate(), roundTo: 0.1f);
        TooltipHandler.TipRegion(mract, "ImageOpt.MipmapBias.desc".Translate().Trim());
        listing.Gap();
        aniso_level = (int)Widgets.HorizontalSlider(listing.GetRect(30f), aniso_level, 0, 16, true,
            $"{"ImageOpt.AnisoLevel".Translate()}: x{aniso_level}", roundTo: 2f);

        listing.Gap(24f);

        var compressorButtonRect = listing.GetRect(30f);
        if (Widgets.ButtonText(compressorButtonRect, "ImageOpt.OpenGPUCompressImageTools".Translate()))
        {
            if (ImageCompressor == null)
            {
                ImageCompressor = new Window_ImageCompressor();
            }

            Find.WindowStack.Add(ImageCompressor);
        }

        listing.Gap();

        var clearActivatedButtonRect = listing.GetRect(30f);
        if (Widgets.ButtonText(clearActivatedButtonRect, "ImageOpt.CleanActivatedDds".Translate()))
        {
            Find.WindowStack.Add(new Dialog_MessageBox(
                "ImageOpt.CleanDdsAlert".Translate(),
                "ImageOpt.ConfirmContinue".Translate(),
                () =>
                {
                    foreach (var mod in LoadedModManager.RunningMods)
                    {
                        foreach (var folder in mod.foldersToLoadDescendingOrder)
                        {
                            DeleteTextures(folder);
                        }
                    }

                    Messages.Message("ImageOpt.CleanDone".Translate(),
                        DefDatabase<MessageTypeDef>.GetNamed("PositiveEvent"));
                },
                "ImageOpt.Cancel".Translate(),
                null,
                "Confirm?",
                true,
                null,
                null
            ));
        }

        listing.Gap();

        var clearNotActivatedButtonRect = listing.GetRect(30f);
        if (Widgets.ButtonText(clearNotActivatedButtonRect, "ImageOpt.CleanNotActivatedDds".Translate()))
        {
            Find.WindowStack.Add(new Dialog_MessageBox(
                "ImageOpt.CleanDdsAlert".Translate(),
                "ImageOpt.ConfirmContinue".Translate(),
                () =>
                {
                    var activated = ModLister.AllInstalledMods.Where(mod => mod.Active).ToHashSet();
                    foreach (var mod in ModLister.AllInstalledMods.Where(mod => !activated.Contains(mod)))
                    {
                        DeleteTextures(mod.RootDir.FullName);
                    }

                    Messages.Message("ImageOpt.CleanDone".Translate(),
                        DefDatabase<MessageTypeDef>.GetNamed("PositiveEvent"));
                },
                "ImageOpt.Cancel".Translate(),
                null,
                "Confirm?",
                true,
                null,
                null
            ));
        }

        listing.End();
    }

    private static void TextFieldNumericLabeled<T>(
        Rect rect,
        string label,
        ref T val,
        ref string buffer,
        float min = 0.0f,
        float max = 1E+09f)
        where T : struct
    {
        Rect rect1 = rect.LeftHalf().Rounded();
        Rect rect2 = rect.RightHalf().Rounded();
        int anchor = (int)Verse.Text.Anchor;
        Verse.Text.Anchor = TextAnchor.MiddleLeft;
        Widgets.Label(rect1, label);
        Verse.Text.Anchor = (TextAnchor)anchor;
        ref T local1 = ref val;
        ref string local2 = ref buffer;
        double min1 = (double)min;
        double max1 = (double)max;
        Widgets.TextFieldNumeric<T>(rect2, ref local1, ref local2, (float)min1, (float)max1);
    }

    private static void DeleteTextures(string folder)
    {
        var dir = new DirectoryInfo(Path.Combine(folder, GenFilePaths.TexturesFolder));
        if (dir.Exists)
        {
            foreach (var file in dir.GetFiles("*.dds", SearchOption.AllDirectories))
            {
                var exts = new[] { ".png", ".jpg", ".jpeg" };
                if (exts.Any(ext => File.Exists(Path.ChangeExtension(file.FullName, ext))))
                {
                    file.Delete();
                }
            }

            foreach (var file in dir.GetFiles("*.dds.zstd", SearchOption.AllDirectories))
            {
                file.Delete();
            }
        }
    }
}