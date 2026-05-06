using UnityEngine;
using Verse;
using System;
using System.IO;

namespace FullyAutomaticOmniCrafter
{
    public static class ModAssets
    {
        private const string PackageId = "Jeremie.Fully.Automatic.OmniCrafter";
        private const string BundleFileName = "rim_world_breathing_light_overlay.assetbundle";
        private const string ShaderAssetName = "Custom/RimWorldBreathingLightOverlay";

        private static Shader breathingLightShader;
        private static bool loadAttempted;
        private static bool missingModLogged;
        private static bool missingBundleLogged;
        private static bool bundleLoadFailedLogged;
        private static bool missingShaderLogged;

        // 全局静态属性，用于按需加载 Shader
        public static Shader BreathingLightShader
        {
            get
            {
                EnsureLoaded();
                return breathingLightShader;
            }
        }

        private static void EnsureLoaded()
        {
            if (loadAttempted)
            {
                return;
            }

            loadAttempted = true;

            ModContentPack myMod = ResolveCurrentMod();
            if (myMod == null)
            {
                if (!missingModLogged)
                {
                    missingModLogged = true;
                    Log.Warning("[FullyAutomaticOmniCrafter] Could not resolve the mod content pack for breathing-light assets. Falling back to ShaderDatabase.Transparent.");
                }

                return;
            }

            string bundlePath = Path.Combine(myMod.RootDir, "AssetBundles", BundleFileName);
            if (!File.Exists(bundlePath))
            {
                if (!missingBundleLogged)
                {
                    missingBundleLogged = true;
                    Log.Warning("[FullyAutomaticOmniCrafter] Breathing-light AssetBundle not found: " + bundlePath);
                }

                return;
            }

            AssetBundle bundle = null;
            try
            {
                bundle = AssetBundle.LoadFromFile(bundlePath);
                if (bundle == null)
                {
                    if (!bundleLoadFailedLogged)
                    {
                        bundleLoadFailedLogged = true;
                        Log.Warning("[FullyAutomaticOmniCrafter] Failed to load AssetBundle from: " + bundlePath);
                    }

                    return;
                }

                // 从 Bundle 中提取 Shader；若命名发生变化，则退回到第一个可用 Shader。
                breathingLightShader = bundle.LoadAsset<Shader>(ShaderAssetName);
                if (breathingLightShader == null)
                {
                    Shader[] shaders = bundle.LoadAllAssets<Shader>();
                    if (shaders != null && shaders.Length > 0)
                    {
                        breathingLightShader = shaders[0];
                    }
                }

                if (breathingLightShader == null && !missingShaderLogged)
                {
                    missingShaderLogged = true;
                    Log.Warning("[FullyAutomaticOmniCrafter] AssetBundle loaded, but no shader could be resolved from '" + bundlePath + "'. Falling back to ShaderDatabase.Transparent.");
                }
            }
            catch (Exception ex)
            {
                if (!bundleLoadFailedLogged)
                {
                    bundleLoadFailedLogged = true;
                    Log.Warning("[FullyAutomaticOmniCrafter] Exception while loading breathing-light assets: " + ex);
                }
            }
            finally
            {
                if (bundle != null)
                {
                    bundle.Unload(false);
                }
            }
        }

        private static ModContentPack ResolveCurrentMod()
        {
            OmniCrafterMod modInstance = OmniCrafterMod.Instance;
            if (modInstance != null && modInstance.Content != null)
            {
                return modInstance.Content;
            }

            string normalizedPackageId = PackageId.ToLowerInvariant();
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                if (mod == null)
                {
                    continue;
                }

                if (string.Equals(mod.PackageId, normalizedPackageId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(mod.PackageIdPlayerFacing, PackageId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(mod.FolderName, "FullyAutomaticOmniCrafter", StringComparison.OrdinalIgnoreCase))
                {
                    return mod;
                }
            }

            return null;
        }
    }
}