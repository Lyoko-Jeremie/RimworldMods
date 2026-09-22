using System.Collections.Generic;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    internal class FakeTradeView { public void EnsureCopyFor(OuterrealmEntry entry) { } }
}

namespace Verse
{
    public static class TradeTranslation { public static string Translate(this string text) => text; }
    public static class Messages { public static void Message(string text, object type, bool historical) { } }
}
namespace RimWorld
{
    public static class MessageTypeDefOf { public static readonly object RejectInput = new object(); }
    public enum Transactor { Colony, Trader }
    public enum TradeAction { None, PlayerSells, PlayerBuys }
    public class Tradeable
    {
        public List<Thing> thingsColony = new List<Thing>();
        public TradeAction ActionToDo;
        public int CountToTransferToDestination;
    }
    public class TradeDeal
    {
        public List<Tradeable> AllTradeables = new List<Tradeable>();
        public void UpdateCurrencyCount() { }
        public void Reset() { }
    }
}
