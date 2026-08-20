using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace FullyAutomaticOmniCrafter.OuterrealmStorage
{
    /// <summary>
    /// 超维存储仓内容 Tab（§6.1 手动取出入口）。
    /// 继承原版 ITab_ContentsBase（内容列表 Tab 基类），但行渲染/滑条/丢弃全部自定义：
    /// 数量源为全局 long 计数（非副本 stackCount），避免 stackLimit（钢铁=75）封顶与 int 截断（§3.2）。
    /// 丢弃走全局 Withdraw（按 stackLimit 分批物化 → GenDrop.TryDropSpawn 到建筑旁），
    /// 不触发副本 SplitOff（§6.5：滑条与丢弃按钮必须一并自定义，否则玩家永远拿不到 stackLimit 以上数量）。
    /// </summary>
    public class ITab_OuterrealmVaultContents : ITab_ContentsBase
    {
        public override IList<Thing> container
        {
            get
            {
                Building_OuterrealmVault vault = SelThing as Building_OuterrealmVault;
                return vault != null ? vault.view : null;
            }
        }

        public ITab_OuterrealmVaultContents()
        {
            size = new Vector2(460f, 450f);
            labelKey = "TabOuterrealmVaultContents";
            containedItemsKey = "TabOuterrealmVaultContents";
        }

        protected override void DoItemsLists(Rect inRect, ref float curY)
        {
            Widgets.BeginGroup(inRect);
            Widgets.ListSeparator(ref curY, inRect.width, containedItemsKey.Translate());
            Building_OuterrealmVault vault = SelThing as Building_OuterrealmVault;
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            bool any = false;
            if (vault != null && vault.view != null && gs != null)
            {
                List<OuterrealmEntry> entries = gs.EntriesForReading;
                for (int i = 0; i < entries.Count; i++)
                {
                    OuterrealmEntry entry = entries[i];
                    if (entry.Count <= 0)
                    {
                        continue;
                    }
                    Thing copy = vault.view.FindCopy(entry.Key);
                    if (copy == null)
                    {
                        // 副本未物化（尸体唯一实体不物化 / filter 刚允许但帧末微批尚未执行）：
                        // 直接用条目 proto 渲染——filter 是视图过滤语义，可见性以 CanShow 判定，
                        // 不依赖副本物化（暂停时帧末微批不执行，UI 仍须正确显示，§filter 视图过滤简化）
                        if (vault.CanShow(entry.Proto))
                        {
                            copy = entry.Proto;
                        }
                        else
                        {
                            continue; // 本建筑 filter 不可见（§6.2）
                        }
                    }
                    any = true;
                    DoVaultRow(vault, copy, entry, inRect.width, ref curY);
                }
            }
            if (!any)
            {
                Widgets.NoneLabel(ref curY, inRect.width, "NoneLower".Translate());
            }
            Widgets.EndGroup();
        }

        /// <summary>自定义行渲染：全局 long 计数 + 滑条/全部丢弃按钮（数量源为全局层，非副本 stackCount）。</summary>
        private void DoVaultRow(Building_OuterrealmVault vault, Thing copy, OuterrealmEntry entry, float width, ref float curY)
        {
            Rect rect = new Rect(0f, curY, width, 28f);
            if (canRemoveThings)
            {
                // 滑条丢弃（指定数量；全局量可能超 int，上限取 int.MaxValue，实际丢弃分批循环）
                if (Widgets.ButtonImage(new Rect(rect.x + rect.width - 24f, rect.y + (rect.height - 24f) / 2f, 24f, 24f), CaravanThingsTabUtility.AbandonSpecificCountButtonTex))
                {
                    int max = (int)Mathf.Min(entry.Count, int.MaxValue);
                    if (max > 0)
                    {
                        string label = OuterrealmVaultUtil.SafeLabelCapNoCount(copy);
                        Find.WindowStack.Add(new Dialog_Slider(
                            (int v) => label + " x" + v.ToString("N0"),
                            1,
                            max,
                            (int v) => DropFromVault(vault, entry, v)));
                    }
                }
                rect.width -= 24f;
                // 全部丢弃（long 总量）
                if (Widgets.ButtonImage(new Rect(rect.x + rect.width - 24f, rect.y + (rect.height - 24f) / 2f, 24f, 24f), CaravanThingsTabUtility.AbandonButtonTex))
                {
                    DropFromVault(vault, entry, entry.Count);
                }
                rect.width -= 24f;
            }
            if (Mouse.IsOver(rect))
            {
                GUI.color = ThingHighlightColor;
                GUI.DrawTexture(rect, TexUI.HighlightTex);
            }
            if ((copy is Corpse corpseForCard && corpseForCard.Bugged)
                || (copy is MinifiedThing minForCard && minForCard.InnerThing == null))
            {
                // Corpse.Bugged / MinifiedThing 空 InnerThing：实例访问会 NRE，用 def 打开信息卡
                Widgets.InfoCardButton(rect.width - 24f, curY, copy.def);
            }
            else
            {
                Widgets.InfoCardButton(rect.width - 24f, curY, copy);
            }
            rect.width -= 24f;
            OuterrealmVaultUtil.ThingIconSafe(new Rect(4f, curY, 28f, 28f), copy);
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            string labelText = OuterrealmVaultUtil.SafeLabelCapNoCount(copy) + " x" + entry.Count.ToString("N0");
            Rect labelRect = new Rect(36f, curY, rect.width - 36f, rect.height);
            Text.WordWrap = false;
            Widgets.Label(labelRect, labelText.StripTags().Truncate(labelRect.width));
            Text.WordWrap = true;
            Text.Anchor = TextAnchor.UpperLeft;
            TooltipHandler.TipRegion(rect, labelText);
            curY += 28f;
        }

        /// <summary>
        /// 从全局层取出 count 个并分批放置到建筑交互格附近（§6.1 手动取出）。
        /// 每批 ≤ stackLimit（物化堆叠不超过 stackLimit，保持原版合并/放置假设）；
        /// 放置失败的部分退回全局层。取出后即时刷新本建筑视图副本数字。
        /// </summary>
        private void DropFromVault(Building_OuterrealmVault vault, OuterrealmEntry entry, long count)
        {
            GameComponent_OuterrealmStorage gs = GameComponent_OuterrealmStorage.Instance;
            if (gs == null || vault == null || vault.Map == null || count <= 0)
            {
                return;
            }
            long remaining = count;
            int safety = 0;
            while (remaining > 0 && safety++ < 100000)
            {
                int stackLimit = entry.Proto != null && entry.Proto.def != null ? entry.Proto.def.stackLimit : 75;
                int take = (int)Mathf.Min(remaining, Mathf.Max(stackLimit, 1));
                Thing t = gs.Withdraw(entry, take);
                if (t == null)
                {
                    break;
                }
                Thing dropped;
                // 放置时排除本系统建筑占位格（建筑 PassThroughOnly，物品可落在建筑格上——需放到建筑外附近）
                // 1.6 放置语义：take ≤ stackLimit 时 TryDropSpawn 成功会把整个堆 Spawn（t.Spawned）
                // 或并入已有堆（t 被吸收销毁）——t.stackCount 不代表"未放置剩余"。
                // 仅放置失败 / 防御性部分放置时才退回全局（remaining 不变 → 循环重试，safety 保护）。
                if (GenDrop.TryDropSpawn(t, vault.InteractionCell, vault.Map, ThingPlaceMode.Near, out dropped, null, c => !GameComponent_OuterrealmStorage.IsVaultCell(c, vault.Map)))
                {
                    remaining -= take; // 成功：整个堆落地（或并入已有堆 / destroyOnDrop 销毁）
                    if (t != null && !t.Spawned && !t.Destroyed)
                    {
                        // 防御：部分放置残余退回全局，remaining 恢复待重试（take ≤ stackLimit 时不会发生）
                        gs.Deposit(t);
                        remaining += t.stackCount;
                    }
                }
                else
                {
                    gs.Deposit(t); // 放置失败退回全局层（remaining 不变，下次循环重试）
                }
            }
            vault.view.SyncKey(entry.Key); // 即时刷新本建筑视图副本数字（§3.3 补回到变更点）
        }
    }
}
