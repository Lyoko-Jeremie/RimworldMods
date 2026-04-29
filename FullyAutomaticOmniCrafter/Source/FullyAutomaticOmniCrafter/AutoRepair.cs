using Verse;
using RimWorld;
using System.Collections.Generic;
using UnityEngine;

namespace FullyAutomaticOmniCrafter
{
    public class Building_AutoRepair : Building
    {
        public Area targetArea;

        // 你的修复逻辑 (TickRare)
        public override void TickRare()
        {
            base.TickRare();

            // 1. 检查是否有电
            var powerComp = this.GetComp<CompPowerTrader>();
            if (powerComp != null && !powerComp.PowerOn) return; // 没电直接跳过

            // 2. 检查是否有设定区域
            if (targetArea == null) return;

            // ... (在此处执行你的寻找损坏物品和增加 HitPoints 的逻辑) ...

            // 假设你找到了一个物品并修复了它
            // int repairAmount = 10;
            // thing.HitPoints += repairAmount;

            // 3. 向全图大脑汇报数据！
            var tracker = this.Map.GetComponent<RepairTrackerMapComponent>();
            if (tracker != null)
            {
                // 替换为实际修复的物品和血量
                tracker.RecordRepair(thing.def.label, repairAmount); 
            }
        }

        // 添加底部按钮 (Gizmo)
        public override IEnumerable<Gizmo> GetGizmos()
        {
            // 返回原版自带的按钮（比如电源开关）
            foreach (Gizmo c in base.GetGizmos())
            {
                yield return c;
            }

            // ... (这里放你选择活动区的 Gizmo) ...

            // 添加打开统计面板的按钮
            yield return new Command_Action
            {
                defaultLabel = "查看修复统计",
                defaultDesc = "打开全图共享的修复统计面板，查看所有机器的劳动成果。",
                // 如果你有自己的图标，用 ContentFinder<Texture2D>.Get("路径")
                icon = ContentFinder<Texture2D>.Get("UI/Icons/Medical/HealthOverview", true), 
                action = delegate
                {
                    // 点击按钮时，把我们写的 Window 塞进游戏的窗口栈中显示出来
                    Find.WindowStack.Add(new Window_RepairStats(this.Map));
                }
            };
        }
    }
    public class Window_RepairStats : Window
    {
        private Map map;
        private Vector2 scrollPosition = Vector2.zero;

        // 构造函数
        public Window_RepairStats(Map map)
        {
            this.map = map;
            this.doCloseX = true; // 窗口右上角显示关闭 X 按钮
            this.forcePause = false; // 弹出时是否强制暂停游戏
            this.absorbInputAroundWindow = false; // 是否允许点击窗口外的区域
        }

        // 定义窗口大小
        public override Vector2 InitialSize => new Vector2(400f, 500f);

        // 绘制窗口内容
        public override void DoWindowContents(Rect inRect)
        {
            // 标题
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 35f), "全区修复统计面板");
            Text.Font = GameFont.Small;

            // 获取地图组件中的数据
            var tracker = map.GetComponent<RepairTrackerMapComponent>();
            if (tracker == null || tracker.sharedRepairStats.Count == 0)
            {
                Widgets.Label(new Rect(0, 40f, inRect.width, 30f), "暂无修复记录。");
                return;
            }

            // 设置滚动视图
            Rect outRect = new Rect(0, 40f, inRect.width, inRect.height - 40f);
            // 计算列表总高度 (每行24像素)
            Rect viewRect = new Rect(0, 0, inRect.width - 16f, tracker.sharedRepairStats.Count * 24f);

            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            
            float yPos = 0;
            foreach (var kvp in tracker.sharedRepairStats)
            {
                Rect rowRect = new Rect(0, yPos, viewRect.width, 24f);
                Widgets.Label(rowRect, $"{kvp.Key}: 累计恢复 {kvp.Value} HP");
                yPos += 24f;
            }

            Widgets.EndScrollView();
        }
    }
    
    public class RepairTrackerMapComponent : MapComponent
    {
        // 存储全图共享的修复数据
        public Dictionary<string, int> sharedRepairStats = new Dictionary<string, int>();

        public RepairTrackerMapComponent(Map map) : base(map)
        {
        }

        // 供建筑调用的记录方法
        public void RecordRepair(string itemName, int amount)
        {
            if (sharedRepairStats.ContainsKey(itemName))
            {
                sharedRepairStats[itemName] += amount;
            }
            else
            {
                sharedRepairStats.Add(itemName, amount);
            }
        }

        // 处理存档与读档
        public override void ExposeData()
        {
            base.ExposeData();
            // 保存字典数据
            Scribe_Collections.Look(ref sharedRepairStats, "sharedRepairStats", LookMode.Value, LookMode.Value);
            
            // 防御性编程：如果读档后字典为空（比如首次加载Mod），初始化它
            if (sharedRepairStats == null)
            {
                sharedRepairStats = new Dictionary<string, int>();
            }
        }
    }
    
}