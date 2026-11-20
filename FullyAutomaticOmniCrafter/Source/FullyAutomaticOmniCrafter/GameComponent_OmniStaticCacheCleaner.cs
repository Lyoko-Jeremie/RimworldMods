using Verse;

namespace FullyAutomaticOmniCrafter
{
    /// <summary>
    /// 每次新建 Game 时清空跨 Game 静态缓存。
    ///
    /// 时机必须放在构造函数，而不是 FinalizeInit：Game.LoadGame 的顺序是
    ///   1. new Game()（本构造函数在此执行；此时 Current.Game 仍指向旧 Game）
    ///   2. 反序列化 world/maps，maps[].FinalizeLoading()（Thing 重新 SpawnSetup，
    ///      CompBiosphere 等会把新数据注册进静态缓存）
    ///   3. Game.FinalizeInit / GameComponentUtility.FinalizeInit
    ///   4. GameComponentUtility.LoadedGame
    /// 若在 3/4 清空，会把第 2 步刚注册的新数据一并抹掉。放在第 1 步则只清旧
    /// Game 的残留，随后反序列化会自然重建。
    ///
    /// RimWorld 的 Game.FillComponents 会用反射自动实例化所有 GameComponent 子类
    /// （见 Verse/Game.cs:354-371），因此本类无需任何注册代码。
    /// </summary>
    public sealed class GameComponent_OmniStaticCacheCleaner : GameComponent
    {
        // GameComponent 基类没有带参构造；保留 (Game) 形参是为了让
        // Game.FillComponents 的 Activator.CreateInstance(type, game) 能匹配到。
        public GameComponent_OmniStaticCacheCleaner(Game game)
        {
            OmniStaticCacheCleaner.ClearAll();
        }
    }
}
