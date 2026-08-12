using System.Collections.Generic;
using Verse;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 侍奉互动配置 Def（Def 驱动，可扩展）：
    /// 互动目录以 modExtension 挂在 Def 上，新互动 = 加一个 XML 条目（+ 可选新 JobDef/JobDriver），零代码侵入。
    /// 由 JobGiver_AMCompanion 读取执行。
    /// </summary>
    public class ArtificialMaidServitudeDef : Def
    {
    }

    /// <summary>互动目录扩展（挂在 ArtificialMaidServitudeDef 上）。</summary>
    public class ArtificialMaidServitudeExtension : DefModExtension
    {
        public List<ArtificialMaidServitudeInteraction> interactions = new List<ArtificialMaidServitudeInteraction>();
    }

    /// <summary>单个互动条目（XML 直接反序列化）。</summary>
    public class ArtificialMaidServitudeInteraction
    {
        /// <summary>执行的 JobDef（如 AM_Job_LapPillow / Lovin）。</summary>
        public JobDef jobDef;

        /// <summary>触发时双方位置喷的粒子（可选）。</summary>
        public FleckDef fleckDef;

        /// <summary>信件标签 i18n key（可选，留空不发信）。</summary>
        public string letterLabelKey;

        /// <summary>信件正文 i18n key（可选，{0}=侍奉者，{1}=主人）。</summary>
        public string letterTextKey;

        /// <summary>基础触发概率（每次思考节拍判定一次）。</summary>
        public float baseChance = 0.05f;

        /// <summary>冷却 tick。</summary>
        public int cooldownTicks = 30000;

        /// <summary>主人前置状态要求。</summary>
        public ArtificialMaidMasterState requiredMasterState = ArtificialMaidMasterState.Any;
    }

    /// <summary>主人状态要求枚举。</summary>
    public enum ArtificialMaidMasterState
    {
        Any,
        Resting, // 主人在休息/维持姿势（LayDown / Wait_MaintainPosture）
        Awake,   // 主人不在休息
    }
}
