using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace OuterrealmArchotechTeleportStation
{
    /// <summary>
    /// 世界地图上的超维科技远古传送站。
    /// 继承 MapParent 是为了让它既能作为世界地标被远行队右键交互，又能拥有一张小型局部地图。
    /// </summary>
    public class OuterrealmArchotechTeleportStationWorldObject : MapParent, IRenameable, INameableWorldObject
    {
        private string nameInt;
        private int stationNumber = -1;

        /// <summary>
        /// 原版通用进入菜单要求 HasMap=true；传送站需要在到达时再生成地图。
        /// </summary>
        protected override bool UseGenericEnterMapFloatMenuOption => false;

        public string Name
        {
            get => nameInt;
            set => nameInt = value;
        }

        public string RenamableLabel
        {
            get => EnsureName();
            set => nameInt = value?.Trim();
        }

        public string BaseLabel => def.label.CapitalizeFirst();

        public string InspectLabel => RenamableLabel;

        public int StationNumber
        {
            get
            {
                EnsureStationNumber();
                return stationNumber;
            }
        }

        public override string Label => EnsureName();

        public override string LabelShort => Label;

        public override bool HasName => !nameInt.NullOrEmpty();

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref nameInt, "nameInt");
            Scribe_Values.Look(ref stationNumber, "stationNumber", -1);
        }

        public override void PostAdd()
        {
            base.PostAdd();
            EnsureName();
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }

            yield return new Command_Action
            {
                defaultLabel = "OATS_CommandRenameTeleportStation".Translate(),
                defaultDesc = "OATS_CommandRenameTeleportStationDesc".Translate(),
                icon = TexButton.Rename,
                action = () => Find.WindowStack.Add(new Dialog_RenameOuterrealmTeleportStation(this))
            };
        }

        /// <summary>
        /// 为右键点击该地标的玩家远行队追加传送选项。
        /// 保留 base 选项是为了继续使用原版“进入地图”等 MapParent 交互。
        /// </summary>
        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Caravan caravan)
        {
            foreach (FloatMenuOption option in base.GetFloatMenuOptions(caravan))
            {
                yield return option;
            }

            // 原版 FloatMenuMakerWorld 理论上只会传入玩家远行队，但这里保留防御式检查，
            // 以兼容其他 Mod 直接调用 GetFloatMenuOptions 的情况。
            if (caravan == null || !caravan.IsPlayerControlled)
            {
                yield break;
            }

            foreach (FloatMenuOption option in CaravanArrivalAction_EnterOuterrealmTeleportStation.GetFloatMenuOptions(caravan, this))
            {
                yield return option;
            }

            // 远行队在传送站本格或相邻一格内都视为抵达传送站，解决地标 tile 无法直接停留的问题。
            if (!OuterrealmTeleportNetworkUtility.CaravanInStationRange(caravan, this))
            {
                yield return new FloatMenuOption("OATS_CannotTeleportTooFar".Translate(), null);
                yield break;
            }

            // 目标列出其他世界传送站，以及玩家基地内已启用的传送门。
            List<OuterrealmTeleportDestination> destinations =
                OuterrealmTeleportNetworkUtility.GetDestinations(this);
            if (destinations.Count == 0)
            {
                yield return new FloatMenuOption("OATS_CannotTeleportNoDestinations".Translate(), null);
                yield break;
            }

            foreach (OuterrealmTeleportDestination destination in destinations)
            {
                // yield + 闭包需要复制局部变量，避免所有菜单项最终都引用循环变量的最后一个值。
                OuterrealmTeleportDestination localDestination = destination;
                yield return new FloatMenuOption(
                    "OATS_CommandTeleportToStation".Translate(localDestination.GetMenuLabel(Tile)),
                    () => OuterrealmTeleportNetworkUtility.TeleportCaravan(caravan, this, localDestination));
            }
        }

        /// <summary>
        /// 控制传送站局部地图何时可以卸载。
        /// 返回 true 只表示卸载 Map；alsoRemoveWorldObject 保持 false，确保世界地标不会随空地图一起销毁。
        /// </summary>
        public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
        {
            alsoRemoveWorldObject = false;
            if (!HasMap)
            {
                return false;
            }

            // 传送站地标永久保留；只有局部地图空置时允许卸载。
            return !Map.mapPawns.AnyPawnBlockingMapRemoval;
        }

        private string EnsureName()
        {
            if (nameInt.NullOrEmpty())
            {
                nameInt = GenerateDefaultName();
            }

            return nameInt.NullOrEmpty() ? BaseLabel : nameInt;
        }

        private string GenerateDefaultName()
        {
            string rootName = GenerateNameRoot();
            return "OATS_DefaultTeleportStationName".Translate(rootName, StationNumber.ToString("00"));
        }

        private string GenerateNameRoot()
        {
            RulePackDef rulePack = def?.nameMaker ?? Faction.OfPlayer?.def?.settlementNameMaker;
            if (rulePack != null)
            {
                List<string> usedNames = new List<string>();
                List<OuterrealmArchotechTeleportStationWorldObject> stations = OuterrealmTeleportNetworkUtility.GetStations();
                for (int i = 0; i < stations.Count; i++)
                {
                    if (stations[i] != this && !stations[i].nameInt.NullOrEmpty())
                    {
                        usedNames.Add(stations[i].nameInt);
                    }
                }

                return NameGenerator.GenerateName(rulePack, usedNames, true);
            }

            return BaseLabel;
        }

        private void EnsureStationNumber()
        {
            if (stationNumber > 0)
            {
                return;
            }

            int maxNumber = 0;
            List<OuterrealmArchotechTeleportStationWorldObject> stations = OuterrealmTeleportNetworkUtility.GetStations();
            for (int i = 0; i < stations.Count; i++)
            {
                OuterrealmArchotechTeleportStationWorldObject station = stations[i];
                if (station != this && station.stationNumber > maxNumber)
                {
                    maxNumber = station.stationNumber;
                }
            }

            stationNumber = Mathf.Max(1, maxNumber + 1);
        }
    }

    public class Dialog_RenameOuterrealmTeleportStation : Dialog_Rename<OuterrealmArchotechTeleportStationWorldObject>
    {
        public Dialog_RenameOuterrealmTeleportStation(OuterrealmArchotechTeleportStationWorldObject station) : base(station)
        {
        }

        protected override int MaxNameLength => 40;

        protected override AcceptanceReport NameIsValid(string name)
        {
            AcceptanceReport report = base.NameIsValid(name);
            if (!report.Accepted)
            {
                return report;
            }

            string trimmedName = name.Trim();
            if (trimmedName.Length == 0)
            {
                return "NameIsInvalid".Translate();
            }

            List<OuterrealmArchotechTeleportStationWorldObject> stations = OuterrealmTeleportNetworkUtility.GetStations();
            for (int i = 0; i < stations.Count; i++)
            {
                OuterrealmArchotechTeleportStationWorldObject station = stations[i];
                if (station != renaming && station.RenamableLabel == trimmedName)
                {
                    return "NameIsInUse".Translate();
                }
            }

            return true;
        }
    }
}
