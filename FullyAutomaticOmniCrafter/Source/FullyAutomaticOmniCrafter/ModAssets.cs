using UnityEngine;
using Verse;
using System;
using System.Collections.Generic;
using System.IO;

namespace FullyAutomaticOmniCrafter
{
    public static class ModAssets
    {
        private const string PackageId = "Jeremie.Fully.Automatic.OmniCrafter";
        private const string BundleFileName = "rim_world_breathing_light_overlay.assetbundle";
        private const string ShaderSourceFileName = "RimWorldBreathingLightOverlay.shader";
        private const string ShaderAssetName = "Custom/RimWorldBreathingLightOverlay";

        private static Shader breathingLightShader;

        // Keep the runtime-compiled Material alive so Unity doesn't destroy the shader it owns.
        private static Material runtimeShaderMaterial;
        private static bool loadAttempted;
        private static bool missingModLogged;
        private static bool missingBundleLogged;
        private static bool bundleLoadFailedLogged;
        private static bool missingShaderLogged;
        private static bool shaderSourceNotFoundLogged;
        private static bool embeddedShaderFailedLogged;

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
                    Log.Warning(
                        "[FullyAutomaticOmniCrafter] Could not resolve the mod content pack for breathing-light assets. Trying shader source file.");
                }

                TryLoadShaderFromSourceFile(null);
                return;
            }

            string bundlePath = ResolveBundlePath(myMod);
            if (string.IsNullOrEmpty(bundlePath))
            {
                if (!missingBundleLogged)
                {
                    missingBundleLogged = true;
                    Log.Warning("[FullyAutomaticOmniCrafter] Breathing-light AssetBundle not found. RootDir='" +
                                myMod.RootDir + "'. Checked: " +
                                string.Join(", ", GetCandidateBundlePaths(myMod.RootDir).ToArray()) +
                                ". Trying shader source file.");
                }

                TryLoadShaderFromSourceFile(myMod.RootDir);
                return;
            }

            AssetBundle bundle = null;
            bool bundleWasPreloaded = false;
            try
            {
                Log.Message("[FullyAutomaticOmniCrafter] lookup breathing-light AssetBundle from AllLoadedAssetBundles.");
                // 优先检查 Unity 是否已预加载了该 AssetBundle（RimWorld 会在启动时加载 AssetBundles 文件夹中的内容）
                string bundleNameWithoutExt = Path.GetFileNameWithoutExtension(BundleFileName);
                string s = "AllLoadedAssetBundles:\n";
                foreach (AssetBundle loaded in AssetBundle.GetAllLoadedAssetBundles())
                {
                    s += loaded.name + "\n";
                    if (loaded != null
                        && (
                            string.Equals(loaded.name, bundleNameWithoutExt, StringComparison.OrdinalIgnoreCase)
                            ||
                            string.Equals(loaded.name, BundleFileName, StringComparison.OrdinalIgnoreCase)
                        )
                       )
                    {
                        bundle = loaded;
                        bundleWasPreloaded = true;
                        Log.Message("[FullyAutomaticOmniCrafter] Found preloaded AssetBundle '" + bundleNameWithoutExt +
                                    "'.");
                        break;
                    }
                }
                Log.Message(s);

                // 若未找到预加载版本，则从文件加载
                if (bundle == null)
                {
                    Log.Message("[FullyAutomaticOmniCrafter] Loading AssetBundle from file '" + bundlePath + "'.");
                    bundle = AssetBundle.LoadFromFile(bundlePath);
                    if (bundle != null)
                    {
                        Log.Message("[FullyAutomaticOmniCrafter] Loaded AssetBundle '" + bundleNameWithoutExt +
                                    "' from file '" + bundlePath + "'.");
                    }
                    else
                    {
                        Log.Warning("[FullyAutomaticOmniCrafter] Failed to load AssetBundle from file '" + bundlePath +
                                    "'. Trying LoadFromMemory as fallback.");

                        // 备选加载方法：先将文件读入内存，再通过 LoadFromMemory 加载
                        try
                        {
                            byte[] data = File.ReadAllBytes(bundlePath);
                            Log.Message("[FullyAutomaticOmniCrafter] 读取到字节长度: " + data.Length);
                            bundle = AssetBundle.LoadFromMemory(data);
                            if (bundle != null)
                            {
                                Log.Message("[FullyAutomaticOmniCrafter] Loaded AssetBundle '" + bundleNameWithoutExt +
                                            "' from memory (fallback).");
                            }
                            else
                            {
                                Log.Warning("[FullyAutomaticOmniCrafter] LoadFromMemory also failed for '" + bundlePath + "'.");
                            }
                        }
                        catch (Exception exMem)
                        {
                            Log.Warning("[FullyAutomaticOmniCrafter] Exception during LoadFromMemory fallback for '" +
                                        bundlePath + "': " + exMem.Message);
                        }
                    }
                }

                if (bundle == null)
                {
                    if (!bundleLoadFailedLogged)
                    {
                        bundleLoadFailedLogged = true;
                        Log.Warning("[FullyAutomaticOmniCrafter] Failed to load breathing-light AssetBundle from '" +
                                    bundlePath +
                                    "'. The file exists but Unity refused to load it (likely Unity-version/platform/compression mismatch). Trying shader source file.");
                    }

                    TryLoadShaderFromSourceFile(myMod.RootDir);
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
                    Log.Warning(
                        "[FullyAutomaticOmniCrafter] AssetBundle loaded, but no shader could be resolved from '" +
                        bundlePath + "'. Asset names: " + string.Join(", ", bundle.GetAllAssetNames()) +
                        ". Trying shader source file.");
                    TryLoadShaderFromSourceFile(myMod.RootDir);
                }
            }
            catch (Exception ex)
            {
                if (!bundleLoadFailedLogged)
                {
                    bundleLoadFailedLogged = true;
                    Log.Warning("[FullyAutomaticOmniCrafter] Exception while loading breathing-light assets from '" +
                                bundlePath + "': " + ex + " Trying shader source file.");
                }

                TryLoadShaderFromSourceFile(myMod.RootDir);
            }
            finally
            {
                // 只卸载由我们自己加载的 bundle；预加载的 bundle 由 RimWorld 统一管理，不能卸载
                if (bundle != null && !bundleWasPreloaded)
                {
                    bundle.Unload(false);
                }
            }
        }

        /// <summary>
        /// Reads the ShaderLab source from <c>AssetBundles/RimWorldBreathingLightOverlay.shader</c>
        /// beside the mod root and compiles it into a runtime Material/Shader.
        /// Falls back to the CPU-driven animation when the file is missing or compilation fails.
        /// </summary>
        private static void TryLoadShaderFromSourceFile(string rootDir)
        {
            if (breathingLightShader != null)
            {
                return;
            }

            // Locate the .shader text file using the same search roots as the AssetBundle.
            string shaderFilePath = null;
            if (!string.IsNullOrEmpty(rootDir))
            {
                foreach (string candidateDir in GetCandidateShaderSourcePaths(rootDir))
                {
                    if (File.Exists(candidateDir))
                    {
                        shaderFilePath = candidateDir;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(shaderFilePath))
            {
                if (!shaderSourceNotFoundLogged)
                {
                    shaderSourceNotFoundLogged = true;
                    string searched = rootDir != null
                        ? string.Join(", ", GetCandidateShaderSourcePaths(rootDir).ToArray())
                        : "<rootDir unknown>";
                    Log.Warning("[FullyAutomaticOmniCrafter] Breathing-light shader source file not found. Searched: " +
                                searched + ". Falling back to code-driven transparent overlay animation.");
                }

                return;
            }

            string shaderSource;
            try
            {
                shaderSource = File.ReadAllText(shaderFilePath, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                if (!embeddedShaderFailedLogged)
                {
                    embeddedShaderFailedLogged = true;
                    Log.Warning("[FullyAutomaticOmniCrafter] Failed to read breathing-light shader source from '" +
                                shaderFilePath + "': " + ex.Message +
                                ". Falling back to code-driven transparent overlay animation.");
                }

                return;
            }

            try
            {
                // CS0618: Material(string) is marked obsolete in Unity 2021.2+ but still functional
                // in Unity 2022.3 (RimWorld's engine). We suppress the warning intentionally.
#pragma warning disable 618
                Material mat = new Material(shaderSource);
#pragma warning restore 618
                if (mat != null && mat.shader != null && mat.shader.isSupported)
                {
                    breathingLightShader = mat.shader;
                    // Store in a static field and call DontDestroyOnLoad so Unity does not
                    // destroy the Material (and the shader it owns) during scene transitions.
                    runtimeShaderMaterial = mat;
                    UnityEngine.Object.DontDestroyOnLoad(runtimeShaderMaterial);
                    Log.Message("[FullyAutomaticOmniCrafter] Breathing-light shader compiled from source file '" +
                                shaderFilePath + "'.");
                }
                else
                {
                    if (!embeddedShaderFailedLogged)
                    {
                        embeddedShaderFailedLogged = true;
                        Log.Warning("[FullyAutomaticOmniCrafter] Breathing-light shader from '" + shaderFilePath +
                                    "' is not supported on this platform. Falling back to code-driven transparent overlay animation.");
                    }
                }
            }
            catch (Exception ex)
            {
                if (!embeddedShaderFailedLogged)
                {
                    embeddedShaderFailedLogged = true;
                    Log.Warning("[FullyAutomaticOmniCrafter] Failed to compile breathing-light shader from '" +
                                shaderFilePath + "': " + ex.Message +
                                ". Falling back to code-driven transparent overlay animation.");
                }
            }
        }

        private static List<string> GetCandidateShaderSourcePaths(string rootDir)
        {
            List<string> result = new List<string>();

            void AddCandidate(string baseDir)
            {
                if (string.IsNullOrEmpty(baseDir))
                {
                    return;
                }

                string candidatePath = Path.Combine(baseDir, "AssetBundles", ShaderSourceFileName);
                if (!result.Contains(candidatePath))
                {
                    result.Add(candidatePath);
                }
            }

            AddCandidate(rootDir);

            try
            {
                DirectoryInfo current = string.IsNullOrEmpty(rootDir) ? null : new DirectoryInfo(rootDir);
                for (int i = 0; i < 2 && current != null; i++)
                {
                    current = current.Parent;
                    AddCandidate(current?.FullName);
                }
            }
            catch
            {
                /* best-effort path enumeration */
            }

            return result;
        }

        private static string ResolveBundlePath(ModContentPack myMod)
        {
            foreach (string candidatePath in GetCandidateBundlePaths(myMod.RootDir))
            {
                if (File.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }

            return null;
        }

        private static List<string> GetCandidateBundlePaths(string rootDir)
        {
            List<string> result = new List<string>();

            void AddCandidate(string baseDir)
            {
                if (string.IsNullOrEmpty(baseDir))
                {
                    return;
                }

                string candidatePath = Path.Combine(baseDir, "AssetBundles", BundleFileName);
                if (!result.Contains(candidatePath))
                {
                    result.Add(candidatePath);
                }
            }

            AddCandidate(rootDir);

            try
            {
                DirectoryInfo current = string.IsNullOrEmpty(rootDir) ? null : new DirectoryInfo(rootDir);
                for (int i = 0; i < 2 && current != null; i++)
                {
                    current = current.Parent;
                    AddCandidate(current?.FullName);
                }
            }
            catch (Exception ex)
            {
                if (!missingBundleLogged)
                {
                    Log.Warning(
                        "[FullyAutomaticOmniCrafter] Failed to enumerate breathing-light AssetBundle search paths from RootDir='" +
                        rootDir + "': " + ex);
                }
            }

            return result;
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