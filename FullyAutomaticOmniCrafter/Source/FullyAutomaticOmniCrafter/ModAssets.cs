using UnityEngine;
using Verse;
using System.IO;

namespace FullyAutomaticOmniCrafter
{
    public class ModAssets
    {
        // 全局静态变量，用于存放你的Shader
        public static Shader BreathingLightShader;

        static ModAssets()
        {
            // 1. 找到当前Mod的根目录 (替换 "YourName.YourMod" 为你 About.xml 里的 packageId)
            ModContentPack myMod =
                LoadedModManager.RunningModsListForReading.FirstOrDefault(m =>
                    m.PackageId == "Jeremie.Fully.Automatic.OmniCrafter");

            if (myMod != null)
            {
                // 2. 拼接AssetBundle的绝对路径
                string bundlePath = Path.Combine(myMod.RootDir, "AssetBundles", "rim_world_breathing_light_overlay.assetbundle");

                // 3. 读取AssetBundle
                AssetBundle bundle = AssetBundle.LoadFromFile(bundlePath);
                if (bundle != null)
                {
                    // 4. 从Bundle中提取Shader
                    // 注意：这里的名字必须是你在ShaderLab代码首行写的名字，比如 "Custom/RimWorldBreathingLight"
                    BreathingLightShader = bundle.LoadAsset<Shader>("Custom/RimWorldBreathingLightOverlay");

                    // 5. 卸载Bundle以释放内存，参数传false表示保留已经加载出的Shader对象
                    bundle.Unload(false);

                    if (BreathingLightShader == null)
                    {
                        Log.Error(
                            "[FullyAutomaticOmniCrafter] Bundle loaded, but failed to find Shader 'Custom/RimWorldBreathingLightOverlay'!");
                    }
                }
                else
                {
                    Log.Error("[FullyAutomaticOmniCrafter] Failed to load AssetBundle from: " + bundlePath);
                }
            }
            else
            {
                Log.Error("[FullyAutomaticOmniCrafter] Mod not found!");
            }
        }
    }
}