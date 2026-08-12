using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 侍奉关系管理器（WorldComponent）：主-仆绑定数据的唯一权威存储。
    /// - servantToMaster：侍奉者 → 主人（主表，随存档保存）
    /// - masterToServants：主人 → 侍奉者列表（反向索引，PostLoadInit 重建，不落盘）
    /// 设计原则：
    /// - 查询全部 O(1)，无扫描；
    /// - 一仆一主（TryBind 自动解旧），一主多仆；
    /// - 事件驱动扩展点：Bound/Unbound 事件供后续行为模块订阅（可扩展性核心）。
    /// </summary>
    public class ArtificialMaidServitudeManager : WorldComponent
    {
        private Dictionary<Pawn, Pawn> servantToMaster = new Dictionary<Pawn, Pawn>();
        private Dictionary<Pawn, List<Pawn>> masterToServants = new Dictionary<Pawn, List<Pawn>>();

        /// <summary>互动冷却表：HashCombine(servant.thingIDNumber, jobDef.GetHashCode()) → 到期 tick。容量极小。</summary>
        private Dictionary<int, int> interactionCooldowns = new Dictionary<int, int>();

        /// <summary>跨图伴随传送分频间隔（tick）。</summary>
        private const int TeleportCheckInterval = 120;

        public override void WorldComponentTick()
        {
            // 分频（120 tick ≈ 2 秒一次；低频遍历，绑定表通常极小）
            if (Find.TickManager.TicksGame % TeleportCheckInterval != 0)
            {
                return;
            }

            // 低频清理绑定残留（10 秒一次；Cleanup 含反向索引重建，需控制频率）
            if (Find.TickManager.TicksGame % 600 == 0)
            {
                Cleanup();
            }

            // 跨图伴随（M5 v1）：绑定双方不在同一地图时，把女仆传送到主人所在图
            if (servantToMaster.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<Pawn, Pawn> pair in servantToMaster)
            {
                TryTeleportServantToMaster(pair.Key, pair.Value);
            }
        }

        /// <summary>
        /// 跨图传送伴随：主人已生成在地图而女仆在别处（其他图/未生成）时，
        /// 将女仆生成到主人附近可站立格。低频（120 tick 分频）+ 多项跳过守卫，无每 tick 开销。
        /// </summary>
        private void TryTeleportServantToMaster(Pawn maid, Pawn master)
        {
            if (maid == null || master == null || maid.Dead || master.Dead || maid.Destroyed || master.Destroyed)
            {
                return;
            }

            if (maid.Map == master.Map)
            {
                return; // 同图（含双方均未生成）
            }

            // 主人不在任何地图（商队/世界）→ 无法传送
            Map targetMap = master.Map;
            if (targetMap == null)
            {
                return;
            }

            // 女仆在商队中 → 不传送（尊重商队结构，避免拆散远行队）
            if (maid.GetCaravan() != null)
            {
                return;
            }

            // 女仆在容器中（展示柜/运输盒等）→ 尊重收纳，不强制取出
            if (maid.ParentHolder != null && !(maid.ParentHolder is Map))
            {
                return;
            }

            // 女仆被携带（ParentHolder 为其他 Pawn）→ 不传送
            if (maid.ParentHolder is Pawn)
            {
                return;
            }

            // 女仆正在携带物品 → 不传送（避免丢失携带物）
            if (maid.carryTracker != null && maid.carryTracker.CarriedThing != null)
            {
                return;
            }

            // 留守 → 不传送（完全静默）
            CompArtificialMaid comp = CompArtificialMaid.GetCompCached(maid);
            if (comp != null && comp.standbyMode)
            {
                return;
            }

            // 传送：先注销再生成到主人附近
            try
            {
                if (maid.Spawned)
                {
                    maid.DeSpawn();
                }

                IntVec3 cell = CellFinder.RandomSpawnCellForPawnNear(master.Position, targetMap);
                GenSpawn.Spawn(maid, cell, targetMap);
            }
            catch (System.Exception ex)
            {
                Log.Warning("[OuterrealmTech] 侍奉跨图传送失败: " + ex.Message);
            }
        }

        /// <summary>绑定建立事件（主人, 侍奉者）。</summary>
        public event System.Action<Pawn, Pawn> Bound;

        /// <summary>绑定解除事件（侍奉者, 原主人）。</summary>
        public event System.Action<Pawn, Pawn> Unbound;

        public ArtificialMaidServitudeManager(World world) : base(world)
        {
        }

        public static ArtificialMaidServitudeManager Get()
        {
            return Current.Game?.World?.GetComponent<ArtificialMaidServitudeManager>();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref servantToMaster, "servantToMaster", LookMode.Reference, LookMode.Reference);
            Scribe_Collections.Look(ref interactionCooldowns, "interactionCooldowns", LookMode.Value, LookMode.Value);

            if (Scribe.mode != LoadSaveMode.PostLoadInit)
            {
                return;
            }

            if (interactionCooldowns == null)
            {
                interactionCooldowns = new Dictionary<int, int>();
            }

            if (servantToMaster == null)
            {
                servantToMaster = new Dictionary<Pawn, Pawn>();
            }

            RebuildReverseIndex();
            Cleanup();
        }

        /// <summary>由主表重建反向索引（仅加载后调用一次）。</summary>
        private void RebuildReverseIndex()
        {
            masterToServants = new Dictionary<Pawn, List<Pawn>>();
            foreach (KeyValuePair<Pawn, Pawn> pair in servantToMaster)
            {
                if (pair.Key == null || pair.Value == null || pair.Key == pair.Value)
                {
                    continue;
                }

                if (!masterToServants.TryGetValue(pair.Value, out List<Pawn> list))
                {
                    list = new List<Pawn>();
                    masterToServants[pair.Value] = list;
                }

                if (!list.Contains(pair.Key))
                {
                    list.Add(pair.Key);
                }
            }
        }

        /// <summary>清理已销毁/失效的绑定关系。直接遍历主表移除残留（null 键、null 值、已销毁），
        /// 不能复用 Unbind：Unbind 依赖正向查找 master 才能清理反向索引，对残留条目无能为力。</summary>
        private void Cleanup()
        {
            foreach (Pawn servant in servantToMaster.Keys.ToList())
            {
                if (servant == null || servant.DestroyedOrNull())
                {
                    servantToMaster.Remove(servant);
                    continue;
                }

                Pawn master = servantToMaster[servant];
                if (master == null || master.DestroyedOrNull())
                {
                    servantToMaster.Remove(servant);
                }
            }

            // 主表清理后直接重建反向索引，保证与主表严格一致
            RebuildReverseIndex();
        }

        /// <summary>建立绑定：master 为主人，servant 为侍奉者。servant 已有旧主人时自动解旧（一仆一主）。</summary>
        public bool TryBind(Pawn master, Pawn servant)
        {
            if (master == null || servant == null || master == servant)
            {
                return false;
            }

            Unbind(servant);

            servantToMaster[servant] = master;
            if (!masterToServants.TryGetValue(master, out List<Pawn> list))
            {
                list = new List<Pawn>();
                masterToServants[master] = list;
            }

            if (!list.Contains(servant))
            {
                list.Add(servant);
            }

            Bound?.Invoke(master, servant);
            return true;
        }

        /// <summary>解除某侍奉者的绑定。</summary>
        public void Unbind(Pawn servant)
        {
            if (servant == null || !servantToMaster.TryGetValue(servant, out Pawn master))
            {
                return;
            }

            servantToMaster.Remove(servant);
            if (masterToServants.TryGetValue(master, out List<Pawn> list))
            {
                list.Remove(servant);
                if (list.Count == 0)
                {
                    masterToServants.Remove(master);
                }
            }

            Unbound?.Invoke(servant, master);
        }

        /// <summary>解除某主人的全部侍奉者。</summary>
        public void UnbindAll(Pawn master)
        {
            if (master == null || !masterToServants.TryGetValue(master, out List<Pawn> list))
            {
                return;
            }

            foreach (Pawn servant in list.ToList())
            {
                Unbind(servant);
            }
        }

        /// <summary>是否为主人（至少有一名侍奉者）。</summary>
        public bool IsMaster(Pawn pawn)
        {
            return pawn != null && masterToServants.ContainsKey(pawn);
        }

        /// <summary>是否为侍奉者。</summary>
        public bool IsServant(Pawn pawn)
        {
            return pawn != null && servantToMaster.ContainsKey(pawn);
        }

        /// <summary>获取侍奉者的主人（无则 null）。</summary>
        public Pawn GetMaster(Pawn servant)
        {
            return servant != null && servantToMaster.TryGetValue(servant, out Pawn master) ? master : null;
        }

        /// <summary>获取主人的侍奉者列表（无则返回新空列表，调用方不应修改）。</summary>
        public List<Pawn> GetServants(Pawn master)
        {
            return master != null && masterToServants.TryGetValue(master, out List<Pawn> list) ? list : new List<Pawn>();
        }

        /// <summary>判断侍奉者某互动是否处于冷却中。</summary>
        public bool IsOnCooldown(Pawn servant, JobDef jobDef)
        {
            if (servant == null || jobDef == null)
            {
                return false;
            }

            int key = Gen.HashCombine<int>(servant.thingIDNumber, jobDef.GetHashCode());
            return interactionCooldowns.TryGetValue(key, out int endTick) && Find.TickManager.TicksGame < endTick;
        }

        /// <summary>记录侍奉者某互动的冷却。</summary>
        public void StartCooldown(Pawn servant, JobDef jobDef, int cooldownTicks)
        {
            if (servant == null || jobDef == null)
            {
                return;
            }

            int key = Gen.HashCombine<int>(servant.thingIDNumber, jobDef.GetHashCode());
            interactionCooldowns[key] = Find.TickManager.TicksGame + cooldownTicks;
        }
    }
}
