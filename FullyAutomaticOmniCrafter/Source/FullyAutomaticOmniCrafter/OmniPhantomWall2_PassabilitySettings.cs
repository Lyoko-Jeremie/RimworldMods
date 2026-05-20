using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{

    public class OmniPhantomWall2_PassabilitySettings : IExposable
    {
        // 玩家殖民者
        public bool allowColonists = true;
        // 宠物
        public bool allowPets = true;
        // 商人/访客（非敌对的有派系人形）
        public bool allowTraders = true;
        // 囚犯
        public bool allowPrisoners = false;
        // 玩家的囚犯
        public bool allowColonyPrisoners = false;
        // 野生动物
        public bool allowWildAnimals = false;
        // 实体
        public bool allowEntities = false;
        // 敌对单位
        public bool allowHostiles = false;
        // 具有派系的人员
        public bool allowFactioned = false;
        // 具有领主（Lord）的群体单位
        public bool allowLords = false;
        // 类人角色 (Humanlike)，涵盖野人 (Wild Man)
        public bool allowHumanlikes = false;
        // 智力达到“工具使用”等级 (ToolUser)
        public bool allowToolUsers = false;
        // 无派系且无领主的角色 (确保野人等特殊中立角色能通过)
        public bool allowUnfactions = false;
        // 甲虫类 (Insects) : 原版游戏中的所有虫族 (Insectoids)：巨甲虫 (Megaspider)、巨型蜻蜓 (Spelopede)、甲壳虫 (Megascarab)
        public bool allowInsectoids = false;
    
        public void ExposeData()
        {
            Scribe_Values.Look(ref allowColonists, "allowColonists", true);
            Scribe_Values.Look(ref allowPets, "allowPets", true);
            Scribe_Values.Look(ref allowTraders, "allowTraders", true);
            Scribe_Values.Look(ref allowPrisoners, "allowPrisoners", false);
            Scribe_Values.Look(ref allowColonyPrisoners, "allowColonyPrisoners", false);
            Scribe_Values.Look(ref allowWildAnimals, "allowWildAnimals", false);
            Scribe_Values.Look(ref allowEntities, "allowEntities", false);
            Scribe_Values.Look(ref allowHostiles, "allowHostiles", false);
            Scribe_Values.Look(ref allowFactioned, "allowFactioned", false);
            Scribe_Values.Look(ref allowLords, "allowLords", false);
            Scribe_Values.Look(ref allowHumanlikes, "allowHumanlikes", false);
            Scribe_Values.Look(ref allowToolUsers, "allowToolUsers", false);
            Scribe_Values.Look(ref allowUnfactions, "allowUnfactions", false);
            Scribe_Values.Look(ref allowInsectoids, "allowInsectoids", false);
        }
    
        /// <summary>
        /// 生成规则签名，用于判断两堵墙是否属于同一"规则组"
        ///  | 方案 | 规则开关数量 | 组合数量 | 限制因素  | 
        ///  | ++++++ | ++++++++ | +++++++ | +++++++++++++++++++++ | 
        ///  | 当前方案 | 7 个布尔开关 | 2⁷ = 128 种 | 签名用 int，理论可扩展到 32 个开关  | 
        ///  | 扩展方案 | 32 个布尔开关 | 2³² = 42亿种 | int 位数限制  | 
        ///  | 终极方案 | 64 个布尔开关 | 2⁶⁴ 种 改用 | long 类型 | 
        /// </summary>
        public int GetSignature()
        {
            int sig = 0;
            if (allowColonists)    sig |= 1 << 0;
            if (allowPets)         sig |= 1 << 1;
            if (allowTraders)      sig |= 1 << 2;
            if (allowPrisoners)    sig |= 1 << 3;
            if (allowColonyPrisoners)    sig |= 1 << 4;
            if (allowWildAnimals)  sig |= 1 << 5;
            if (allowEntities)     sig |= 1 << 6;
            if (allowHostiles)     sig |= 1 << 7;
            if (allowFactioned)    sig |= 1 << 8;
            if (allowLords)        sig |= 1 << 9;
            if (allowHumanlikes)   sig |= 1 << 10;
            if (allowToolUsers)    sig |= 1 << 11;
            if (allowUnfactions)   sig |= 1 << 12;
            if (allowInsectoids)   sig |= 1 << 13;
            return sig;
        }
    
        /// <summary>
        /// 检查两个设置是否相同（用于房间合并判断）
        /// </summary>
        public bool Equals(OmniPhantomWall2_PassabilitySettings other)
        {
            if (other == null) return false;
            return GetSignature() == other.GetSignature();
        }

        public void CopyFrom(OmniPhantomWall2_PassabilitySettings other)
        {
            allowColonists = other.allowColonists;
            allowPets = other.allowPets;
            allowTraders = other.allowTraders;
            allowPrisoners = other.allowPrisoners;
            allowColonyPrisoners = other.allowColonyPrisoners;
            allowWildAnimals = other.allowWildAnimals;
            allowEntities = other.allowEntities;
            allowHostiles = other.allowHostiles;
            allowFactioned = other.allowFactioned;
            allowLords = other.allowLords;
        }

        public OmniPhantomWall2_PassabilitySettings Clone()
        {            
            return new OmniPhantomWall2_PassabilitySettings
            {
                allowColonists = this.allowColonists,
                allowPets = this.allowPets,
                allowTraders = this.allowTraders,
                allowPrisoners = this.allowPrisoners,
                allowColonyPrisoners = this.allowColonyPrisoners,
                allowWildAnimals = this.allowWildAnimals,
                allowEntities = this.allowEntities,
                allowHostiles = this.allowHostiles,
                allowFactioned = this.allowFactioned,
                allowLords = this.allowLords,
                allowHumanlikes = this.allowHumanlikes,
                allowToolUsers = this.allowToolUsers,
                allowUnfactions = this.allowUnfactions,
                allowInsectoids = this.allowInsectoids
            };
        }
    }
    
}
