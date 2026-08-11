using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 人造人女仆光环管理器（GameComponent）。
    /// 保存所有被玩家标记获得"女仆在身边"光环的小人引用（跨地图、跨远行队、随存档保存），
    /// 并在光环因其他原因（其他 Mod、复活流程等）丢失时自动补回：
    /// 仅当女仆与小人同地图或同远行队时才补回；
    /// 标记本身只随"脱离我方阵营"或"玩家手动取消"而移除（贴合"保持生效"的设定）。
    /// </summary>
    public class GameComponent_ArtificialMaidAuraManager : GameComponent
    {
        private const int CheckIntervalTicks = 250; // 约 4 秒一次的轻量校正

        private HashSet<Pawn> markedPawns = new HashSet<Pawn>();
        private int ticksUntilNextCheck = CheckIntervalTicks;

        public GameComponent_ArtificialMaidAuraManager(Game game)
        {
        }

        /// <summary>
        /// 获取全局管理器实例（RimWorld 1.6 会自动实例化所有 GameComponent 子类并参与存档）。
        /// </summary>
        public static GameComponent_ArtificialMaidAuraManager Get()
        {
            return Current.Game.GetComponent<GameComponent_ArtificialMaidAuraManager>();
        }

        public bool IsMarked(Pawn pawn)
        {
            return pawn != null && markedPawns.Contains(pawn);
        }

        /// <summary>
        /// 手动标记：记录引用并立即施加光环（标记即生效，与女仆位置无关）。
        /// </summary>
        public void Mark(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || pawn.health == null) return;
            markedPawns.Add(pawn);
            AddAuraHediffAndThought(pawn);
        }

        /// <summary>
        /// 手动取消标记：移除引用与光环。
        /// </summary>
        public void Unmark(Pawn pawn)
        {
            if (pawn == null) return;
            markedPawns.Remove(pawn);
            RemoveAuraHediffAndThought(pawn);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref markedPawns, "markedPawns", LookMode.Reference);
            Scribe_Values.Look(ref ticksUntilNextCheck, "ticksUntilNextCheck", CheckIntervalTicks);
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            if (ticksUntilNextCheck > 0)
            {
                ticksUntilNextCheck--;
                return;
            }
            ticksUntilNextCheck = CheckIntervalTicks;
            if (markedPawns.Count == 0) return;
            TryCorrectMarkedPawns();
        }

        /// <summary>
        /// 校正所有被标记的小人：清理失效引用、处理脱离我方阵营、补回意外丢失的光环。
        /// </summary>
        private void TryCorrectMarkedPawns()
        {
            List<Pawn> toRemove = null;
            foreach (Pawn pawn in markedPawns)
            {
                // 1. 引用已失效（对象被销毁）→ 清理标记
                if (pawn == null || pawn.Destroyed)
                {
                    (toRemove ?? (toRemove = new List<Pawn>())).Add(pawn);
                    continue;
                }
                // 2. 不再属于我方 → 移除标记与光环
                if (pawn.Faction != Faction.OfPlayer)
                {
                    RemoveAuraHediffAndThought(pawn);
                    (toRemove ?? (toRemove = new List<Pawn>())).Add(pawn);
                    continue;
                }
                // 3. 死亡时保留标记（复活后继续生效），但不补光环
                if (pawn.Dead || pawn.health == null) continue;

                // 4. 光环保证：光环缺失且女仆在身边 → 补回；光环存在 → 保证心情思绪不丢失
                EnsureAuraOnPawn(pawn, IsMaidNearby(pawn));
            }
            if (toRemove == null) return;
            for (int i = 0; i < toRemove.Count; i++)
            {
                markedPawns.Remove(toRemove[i]);
            }
        }

        /// <summary>
        /// 保证光环与心情思绪存在。allowAdd 为 false（女仆不在身边）时不强制补回，
        /// 等待女仆回到身边后由后续校正补上。
        /// </summary>
        private void EnsureAuraOnPawn(Pawn pawn, bool allowAdd)
        {
            if (!pawn.health.hediffSet.HasHediff(ArtificialMaidDefOf.ArtificialMaidAura))
            {
                if (!allowAdd) return;
                pawn.health.AddHediff(ArtificialMaidDefOf.ArtificialMaidAura);
                AddMoodThought(pawn);
            }
            else
            {
                // 光环仍在但心情思绪被删除/即将过期 → 重新补上/刷新
                AddMoodThought(pawn);
            }
        }

        private void AddAuraHediffAndThought(Pawn pawn)
        {
            if (pawn.health == null || pawn.Dead) return;
            if (!pawn.health.hediffSet.HasHediff(ArtificialMaidDefOf.ArtificialMaidAura))
            {
                pawn.health.AddHediff(ArtificialMaidDefOf.ArtificialMaidAura);
            }
            AddMoodThought(pawn);
        }

        private void RemoveAuraHediffAndThought(Pawn pawn)
        {
            if (pawn?.health?.hediffSet != null)
            {
                Hediff hediff = pawn.health.hediffSet.GetFirstHediffOfDef(ArtificialMaidDefOf.ArtificialMaidAura);
                if (hediff != null)
                {
                    pawn.health.RemoveHediff(hediff);
                }
            }
            RemoveMoodThought(pawn);
        }

        /// <summary>
        /// 补上心情思绪：已存在则刷新有效期（TryGainMemoryFast 内部 Renew），
        /// 不存在则新建，从而维持恒定的正面心情。
        /// </summary>
        private static void AddMoodThought(Pawn pawn)
        {
            MemoryThoughtHandler memories = pawn.needs?.mood?.thoughts?.memories;
            if (memories == null) return;
            memories.TryGainMemoryFast(ArtificialMaidDefOf.ArtificialMaidAura_Mood);
        }

        private static void RemoveMoodThought(Pawn pawn)
        {
            MemoryThoughtHandler memories = pawn.needs?.mood?.thoughts?.memories;
            if (memories == null) return;
            memories.RemoveMemoriesOfDef(ArtificialMaidDefOf.ArtificialMaidAura_Mood);
        }

        /// <summary>
        /// 判断 pawn 身边是否有女仆：同地图（有活跃女仆）或同远行队。
        /// </summary>
        private static bool IsMaidNearby(Pawn pawn)
        {
            if (pawn.Spawned && pawn.Map != null)
            {
                ArtificialMaidMapComponent mapComp = ArtificialMaidMapComponent.Get(pawn.Map);
                return mapComp != null && mapComp.MaidCount > 0;
            }
            Caravan caravan = pawn.GetCaravan();
            if (caravan == null) return false;
            List<Pawn> caravanPawns = caravan.PawnsListForReading;
            for (int i = 0; i < caravanPawns.Count; i++)
            {
                if (caravanPawns[i].def == ArtificialMaidDefOf.ArtificialMaid) return true;
            }
            return false;
        }
    }
}
