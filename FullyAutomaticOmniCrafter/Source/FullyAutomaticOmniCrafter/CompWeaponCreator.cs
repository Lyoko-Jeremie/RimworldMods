using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    public class CompProperties_WeaponCreator : CompProperties
    {
        public CompProperties_WeaponCreator()
        {
            this.compClass = typeof(CompWeaponCreator);
        }
    }
    
    /// <summary>
    /// 特化武器和人格武器制作台
    /// CompBladelinkWeapon
    /// CompUniqueWeapon
    /// 打开一个窗口，显示 左、中左、中右、右 四栏，
    /// 左侧是类似Dialog_OmniCrafter左侧的树分类表（只包含武器），
    /// 中左间是类似Dialog_OmniCrafter的搜索和筛选以及武器查看列表（只包含武器），
    /// 中右侧是武器制作界面，查看当前武器的状态，上半部分是武器基本介绍，下半部分是当前添加的组件列表，可以点击删除列表中的组件，最下方是生成武器按钮，武器直接生成在建筑所在附近
    /// 右可以选择所有可用的特化组件和人格，并在选中后显示对应组件可以设置的的参数并设置，然后按键添加到中右栏武器界面的组件列表中
    /// </summary>
    public class CompWeaponCreator : ThingComp
    {
        
    }
}