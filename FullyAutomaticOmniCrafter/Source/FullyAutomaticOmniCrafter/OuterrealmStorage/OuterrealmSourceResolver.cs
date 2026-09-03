using Verse;
using Verse.AI;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>超维存储查询对象的来源类型。查询身份只在本层区分，业务代码统一使用解析结果。</summary>
    internal enum OuterrealmSourceKind
    {
        None,
        Projection,
        IdentityAnchor,
        SubspaceCanonical
    }

    /// <summary>一次只读来源解析结果；不拥有库存，也不代表已经取出。</summary>
    internal readonly struct OuterrealmSource
    {
        public readonly OuterrealmSourceKind Kind;
        public readonly Thing QueryThing;
        public readonly OuterrealmEntry Entry;
        public readonly OuterrealmVaultViewThingOwner View;
        public readonly Building_OuterrealmVault Vault;

        public OuterrealmSource(
            OuterrealmSourceKind kind,
            Thing queryThing,
            OuterrealmEntry entry,
            OuterrealmVaultViewThingOwner view,
            Building_OuterrealmVault vault)
        {
            Kind = kind;
            QueryThing = queryThing;
            Entry = entry;
            View = view;
            Vault = vault;
        }

        public bool IsVaultQuery => Kind == OuterrealmSourceKind.Projection
            || Kind == OuterrealmSourceKind.IdentityAnchor;
    }

    /// <summary>
    /// 普通投影、唯一权威锚点和随身权威候选的统一解析与最终取出网关。
    /// Reserve 只能使用 TryResolve 做只读判断；只有实际携带、穿戴、装备等所有权边界才能调用 Checkout。
    /// </summary>
    internal static class OuterrealmSourceResolver
    {
        public static bool TryResolve(Thing thing, out OuterrealmSource source)
        {
            source = default;
            if (thing == null || thing.Destroyed)
            {
                return false;
            }

            OuterrealmVaultViewThingOwner view = thing.holdingOwner as OuterrealmVaultViewThingOwner;
            if (view != null)
            {
                OuterrealmEntry projectionEntry = view.GetEntryOf(thing);
                if (projectionEntry == null || projectionEntry.Count <= 0)
                {
                    return false;
                }
                source = new OuterrealmSource(
                    OuterrealmSourceKind.Projection,
                    thing,
                    projectionEntry,
                    view,
                    view.Context as Building_OuterrealmVault);
                return true;
            }

            GameComponent_OuterrealmStorage storage = GameComponent_OuterrealmStorage.Instance;
            OuterrealmEntry entry;
            if (storage == null || !storage.TryGetCanonicalEntry(thing, out entry))
            {
                return false;
            }

            if (OuterrealmIdentityRouting.IsAnchor(thing))
            {
                Building_OuterrealmVault vault;
                IntVec3 ignored;
                OuterrealmIdentityRouting.TryGetAnchor(thing, out vault, out ignored);
                source = new OuterrealmSource(
                    OuterrealmSourceKind.IdentityAnchor, thing, entry, null, vault);
                return true;
            }

            source = new OuterrealmSource(
                OuterrealmSourceKind.SubspaceCanonical, thing, entry, null, null);
            return true;
        }

        /// <summary>在最终所有权转移边界取出权威实例；不 Spawn，不加入任何容器。</summary>
        public static Thing Checkout(in OuterrealmSource source, int count)
        {
            if (count <= 0 || source.Entry == null || source.Entry.Count <= 0)
            {
                return null;
            }
            if (source.Kind == OuterrealmSourceKind.Projection)
            {
                return source.View?.WithdrawCanonical(source.QueryThing, count);
            }
            if (source.Kind == OuterrealmSourceKind.IdentityAnchor
                || source.Kind == OuterrealmSourceKind.SubspaceCanonical)
            {
                return GameComponent_OuterrealmStorage.Instance?.Withdraw(source.Entry, count);
            }
            return null;
        }

        /// <summary>把 Job 当前引用从查询对象精确迁移到真实实例。</summary>
        public static void ReplaceJobTarget(Job job, in OuterrealmSource source, Thing actual)
        {
            if (job != null && actual != null && actual != source.QueryThing)
            {
                OuterrealmPatchUtil.ReplaceJobThingReference(job, source.QueryThing, actual);
            }
        }
    }
}
