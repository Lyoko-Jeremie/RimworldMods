using System.Collections.Generic;
using RimWorld;
using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 万能重生平台的全局管理器。
    /// 保存全局共用的"已登记"死亡 Pawn 列表，并通过原版 CompHasPawnSources 的孤儿实例
    /// 将这些 Pawn 登记到 Find.WorldPawns 的 sourcedPawns 字典中，使其不会被 WorldPawnGC 回收。
    /// 该管理器由 RimWorld 的 Game.FillComponents 自动实例化并随存档深保存，
    /// 与建筑生命周期完全解耦：建筑被拆除不会释放任何登记。
    /// </summary>
    public class GameComponent_OmniResurrector : GameComponent
    {
        /// <summary>当前游戏实例的单例入口（每次新游戏/读档由 FinalizeInit 重新赋值）。</summary>
        public static GameComponent_OmniResurrector Instance;

        /// <summary>
        /// GC 保护载体：一个不挂在任何 Thing 上的孤儿 CompHasPawnSources 实例。
        /// AddSource 只调用 Find.WorldPawns.AddPawnSource(pawn, this)，不访问 props，因此无需挂载。
        /// </summary>
        private readonly CompHasPawnSources sourceComp = new CompHasPawnSources();

        /// <summary>全局"已登记"列表（即受 GC 保护的死亡 Pawn 列表）。</summary>
        public List<Pawn> Registered => sourceComp.pawnSources;

        public GameComponent_OmniResurrector(Game game)
        {
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            Instance = this;
        }

        /// <summary>登记一个死亡 Pawn，使其不会被 WorldPawnGC 回收。</summary>
        public void Register(Pawn pawn)
        {
            if (pawn == null)
            {
                return;
            }
            sourceComp.AddSource(pawn);
        }

        /// <summary>取消登记，解除对该 Pawn 的 GC 保护。</summary>
        public void Unregister(Pawn pawn)
        {
            if (pawn == null || !sourceComp.pawnSources.Contains(pawn))
            {
                return;
            }
            // 先从 WorldPawns 的 sourcedPawns 字典移除该 Pawn 的条目，再移出登记列表。
            Find.WorldPawns.RemovePawnSources(new List<Pawn> { pawn }, sourceComp);
            sourceComp.pawnSources.Remove(pawn);
        }

        /// <summary>
        /// 清理失效登记：Pawn 已被回收（Discarded）或为 null 时移除。
        /// 登记列表允许包含存活的 Pawn（预约保护），因此不要求 Pawn 死亡。
        /// 在打开复活控制界面时调用一次即可。
        /// </summary>
        public void CleanupInvalid()
        {
            sourceComp.pawnSources.RemoveAll(p => p == null || p.Discarded);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            // LookMode.Reference：死亡 Pawn 本体保存在 WorldPawns.pawnsDead 中，随存档深保存，读档后可解析引用。
            Scribe_Collections.Look(ref sourceComp.pawnSources, "registered", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (sourceComp.pawnSources == null)
                {
                    sourceComp.pawnSources = new List<Pawn>();
                }
                sourceComp.pawnSources.RemoveAll(p => p == null || p.Discarded);
                // 读档后 sourcedPawns 字典已丢失，需要重新登记以恢复 GC 保护。
                foreach (Pawn p in sourceComp.pawnSources)
                {
                    if (p != null && !p.Discarded)
                    {
                        Find.WorldPawns.AddPawnSource(p, sourceComp);
                    }
                }
            }
        }
    }
}
