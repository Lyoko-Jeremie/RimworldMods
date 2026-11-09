using System;

namespace Jeremie.AtmosphereOverhaulPatch.Compat
{
    /// <summary>
    /// 设置界面用的模块视图。
    ///
    /// 它把「模块元数据（始终存在）」与「实现实例（可能因目标缺失而未加载）」分开：
    /// 目标 Mod 不在时，兼容 DLL 根本不会被加载，Instance 为 null，
    /// 但这一行仍然显示为「未安装」，玩家可以提前决定是否启用。
    /// </summary>
    public sealed class CompatModuleInfo
    {
        public string PackageId;
        public string LabelKey;
        public string HintKey;
        public string AssemblyName;
        public string RelativeFolder;

        /// <summary>从设置里读取开关（即使实现未加载也能读写）。</summary>
        public Func<bool> ReadEnabled;

        /// <summary>把开关写回设置。</summary>
        public Action<bool> WriteEnabled;

        /// <summary>运行时发现的实现；null 表示兼容 DLL 未被加载。</summary>
        public ICompatModule Instance;

        public bool Loaded
        {
            get { return Instance != null; }
        }

        public bool EnabledByUser
        {
            get
            {
                if (Instance != null)
                {
                    return Instance.EnabledByUser;
                }

                try
                {
                    return ReadEnabled != null && ReadEnabled();
                }
                catch
                {
                    return false;
                }
            }
        }

        public CompatState State
        {
            get { return Instance != null ? Instance.State : CompatState.NotInstalled; }
        }

        public string Detail
        {
            get
            {
                if (Instance != null)
                {
                    return Instance.Detail;
                }

                return "compat assembly not loaded";
            }
        }

        public void SetEnabledByUser(bool value)
        {
            if (Instance != null)
            {
                Instance.SetEnabledByUser(value);
                return;
            }

            try
            {
                if (WriteEnabled != null)
                {
                    WriteEnabled(value);
                }
            }
            catch
            {
                // 设置写入失败不应影响界面
            }
        }
    }
}
