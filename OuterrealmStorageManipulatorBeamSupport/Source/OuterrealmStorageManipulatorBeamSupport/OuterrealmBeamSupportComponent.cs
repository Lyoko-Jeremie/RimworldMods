using System;
using FullyAutomaticOmniCrafter.OuterrealmStorage;
using Verse;

namespace OuterrealmStorageManipulatorBeamSupport
{
    /// <summary>
    /// 每局光束支持状态：持有本局的预留账本，并注册到主 mod 的外部预留注册表。
    /// 由 RimWorld 的 Game.FillComponents 自动实例化（与原版 GameComponent 一致，
    /// 构造器自行保存 Game 引用）；切换存档时先注销上一局的账本，避免旧账本残留。
    /// </summary>
    public class OuterrealmBeamSupportComponent : GameComponent
    {
        private static OuterrealmBeamSupportComponent current;
        private readonly Game ownerGame;
        internal OuterrealmBeamLedger Ledger;

        public OuterrealmBeamSupportComponent(Game game)
        {
            ownerGame = game;
            OuterrealmBeamSupportComponent previous = current;
            if (previous != null && !ReferenceEquals(previous, this))
            {
                OuterrealmExternalReservationRegistry.Unregister(previous.Ledger);
            }
            Ledger = new OuterrealmBeamLedger(
                OuterrealmExternalReservationRegistry.NotifyReservationChanged,
                OuterrealmExternalReservationRegistry.NotifyIdentityReservationReleased);
            OuterrealmExternalReservationRegistry.Register(Ledger);
            current = this;
        }

        /// <summary>当前存档的组件；无游戏或组件缺失时返回 null。</summary>
        public static OuterrealmBeamSupportComponent Current
        {
            get
            {
                Game game = Verse.Current.Game;
                if (game == null)
                {
                    return null;
                }
                OuterrealmBeamSupportComponent cached = current;
                if (cached != null && ReferenceEquals(cached.ownerGame, game))
                {
                    return cached;
                }
                current = game.GetComponent<OuterrealmBeamSupportComponent>();
                return current;
            }
        }
    }

    /// <summary>适配 mod 启动入口：所有 Mod 程序集加载完成后安装光束边界补丁。
    /// 失败只影响本 mod（主 mod 与牵引光束各自照常运行）。</summary>
    [StaticConstructorOnStartup]
    internal static class OuterrealmBeamSupportBootstrap
    {
        static OuterrealmBeamSupportBootstrap()
        {
            try
            {
                OuterrealmBeamAdapter.Install();
            }
            catch (Exception error)
            {
                Log.Error("[OuterrealmStorageManipulatorBeamSupport] bootstrap failed: " + error);
            }
        }
    }
}
