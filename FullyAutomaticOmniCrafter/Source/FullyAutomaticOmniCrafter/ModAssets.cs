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
        private const string ShaderAssetName = "Custom/RimWorldBreathingLightOverlay";

        // Embedded ShaderLab source compiled at runtime when the AssetBundle cannot be loaded
        // (e.g. Unity-version / compression mismatch). This avoids any AssetBundle dependency.
        private const string EmbeddedShaderSource = @"
Shader ""Custom/RimWorldBreathingLightOverlay"" {
    SubShader {
        Tags { ""Queue""=""Transparent"" ""RenderType""=""Transparent"" ""IgnoreProjector""=""True"" }
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma exclude_renderers gles gles3 d3d11_9x
            #pragma target 3.0

            #include ""UnityCG.cginc""

            struct appdata_t {
                float4 vertex : POSITION;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f {
                float4 vertex : SV_POSITION;
                float2 texcoord : TEXCOORD0;
            };

            sampler2D _MainTex;
            float4 _Color;
            float _Speed;
            float _MinAlpha;
            float _MaxAlpha;

            v2f vert(appdata_t v) {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.texcoord = v.texcoord;
                return o;
            }

            float4 frag(v2f i) : SV_Target {
                float4 texColor = tex2D(_MainTex, i.texcoord);
                float breathFactor = (sin(_Time.y * _Speed) + 1.0) * 0.5;
                float currentAlpha = lerp(_MinAlpha, _MaxAlpha, breathFactor);
                float4 finalColor = texColor * _Color;
                finalColor.a *= currentAlpha;
                return finalColor;
            }
            ENDCG
        }
    }
}
";

        private static Shader breathingLightShader;
        // Keep the runtime-compiled Material alive so Unity doesn't destroy the shader it owns.
        private static Material runtimeShaderMaterial;
        private static bool loadAttempted;
        private static bool missingModLogged;
        private static bool missingBundleLogged;
        private static bool bundleLoadFailedLogged;
        private static bool missingShaderLogged;
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
                    Log.Warning("[FullyAutomaticOmniCrafter] Could not resolve the mod content pack for breathing-light assets. Trying embedded shader source.");
                }

                TryLoadShaderFromEmbeddedSource();
                return;
            }

            string bundlePath = ResolveBundlePath(myMod);
            if (string.IsNullOrEmpty(bundlePath))
            {
                if (!missingBundleLogged)
                {
                    missingBundleLogged = true;
                    Log.Warning("[FullyAutomaticOmniCrafter] Breathing-light AssetBundle not found. RootDir='" + myMod.RootDir + "'. Checked: " + string.Join(", ", GetCandidateBundlePaths(myMod.RootDir).ToArray()) + ". Trying embedded shader source.");
                }

                TryLoadShaderFromEmbeddedSource();
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
                        Log.Warning("[FullyAutomaticOmniCrafter] Failed to load breathing-light AssetBundle from '" + bundlePath + "'. The file exists but Unity refused to load it (likely Unity-version/platform/compression mismatch). Trying embedded shader source.");
                    }

                    TryLoadShaderFromEmbeddedSource();
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
                    Log.Warning("[FullyAutomaticOmniCrafter] AssetBundle loaded, but no shader could be resolved from '" + bundlePath + "'. Asset names: " + string.Join(", ", bundle.GetAllAssetNames()) + ". Trying embedded shader source.");
                    TryLoadShaderFromEmbeddedSource();
                }
            }
            catch (Exception ex)
            {
                if (!bundleLoadFailedLogged)
                {
                    bundleLoadFailedLogged = true;
                    Log.Warning("[FullyAutomaticOmniCrafter] Exception while loading breathing-light assets from '" + bundlePath + "': " + ex + " Trying embedded shader source.");
                }

                TryLoadShaderFromEmbeddedSource();
            }
            finally
            {
                if (bundle != null)
                {
                    bundle.Unload(false);
                }
            }
        }

        private static void TryLoadShaderFromEmbeddedSource()
        {
            if (breathingLightShader != null)
            {
                return;
            }

            try
            {
                // CS0618: Material(string) is marked obsolete in Unity 2021.2+ but still functional
                // in Unity 2022.3 (RimWorld's engine). We suppress the warning intentionally.
#pragma warning disable 618
                Material mat = new Material(EmbeddedShaderSource);
#pragma warning restore 618
                if (mat != null && mat.shader != null && mat.shader.isSupported)
                {
                    breathingLightShader = mat.shader;
                    // Store in a static field and call DontDestroyOnLoad so Unity does not
                    // destroy the Material (and the shader it owns) during scene transitions.
                    runtimeShaderMaterial = mat;
                    UnityEngine.Object.DontDestroyOnLoad(runtimeShaderMaterial);
                    Log.Message("[FullyAutomaticOmniCrafter] Breathing-light shader compiled from embedded source.");
                }
                else
                {
                    if (!embeddedShaderFailedLogged)
                    {
                        embeddedShaderFailedLogged = true;
                        Log.Warning("[FullyAutomaticOmniCrafter] Embedded breathing-light shader is not supported on this platform. Falling back to code-driven transparent overlay animation.");
                    }
                }
            }
            catch (Exception ex)
            {
                if (!embeddedShaderFailedLogged)
                {
                    embeddedShaderFailedLogged = true;
                    Log.Warning("[FullyAutomaticOmniCrafter] Failed to compile embedded breathing-light shader: " + ex.Message + ". Falling back to code-driven transparent overlay animation.");
                }
            }
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
                    Log.Warning("[FullyAutomaticOmniCrafter] Failed to enumerate breathing-light AssetBundle search paths from RootDir='" + rootDir + "': " + ex);
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