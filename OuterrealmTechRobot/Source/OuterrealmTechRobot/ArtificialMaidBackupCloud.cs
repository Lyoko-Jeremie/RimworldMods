using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// 人造人女仆的云端备份组件。
    /// 该组件随 Game 存档保存，定期为每个人造人女仆保存一份独立 Pawn 快照。
    /// 当原 Pawn 死亡或被其他 Mod 强制 Destroy 后，可以通过女仆自身序列号恢复原对象或重建新 Pawn。
    /// </summary>
    public class ArtificialMaidBackupCloud : GameComponent
    {
        // 每 60000 tick（约 1 游戏天）进行一次低频自动备份，避免频繁深拷贝 Pawn 带来性能压力。
        private const int BackupIntervalTicks = 60000;

        // 存档用列表。Scribe 可以稳定保存 List，但 Dictionary 需要加载后重建索引。
        private List<BackupRecord> backups = new List<BackupRecord>();

        // 运行时索引。key 使用 CompArtificialMaid.serialNumber，而不是 Pawn 的 thingIDNumber。
        private Dictionary<string, BackupRecord> backupsBySerial = new Dictionary<string, BackupRecord>();

        // 复用的临时列表，用于给快照 Pawn 及其携带物重新分配 ThingID，避免频繁分配内存。
        private static readonly List<Thing> tmpHeldThings = new List<Thing>();

        public ArtificialMaidBackupCloud(Game game)
        {
        }

        public static ArtificialMaidBackupCloud Current
        {
            get
            {
                // GameComponent 由 RimWorld 在 Game.FillComponents 中自动创建。
                // 这里额外判空，避免主菜单或读档过程中的空 Game 访问。
                if (CurrentGameValid())
                {
                    return Verse.Current.Game.GetComponent<ArtificialMaidBackupCloud>();
                }

                return null;
            }
        }

        public static IReadOnlyList<BackupRecord> BackupsForReading
        {
            get
            {
                // 终端菜单只读备份列表，不允许外部直接修改内部集合。
                var cloud = Current;
                return cloud != null ? cloud.backups : (IReadOnlyList<BackupRecord>)Array.Empty<BackupRecord>();
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref backups, "backups", LookMode.Deep);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                // 兼容旧存档或异常存档：缺失列表时创建空列表，坏记录直接丢弃。
                if (backups == null)
                {
                    backups = new List<BackupRecord>();
                }

                backups.RemoveAll(record => record == null || string.IsNullOrEmpty(record.SerialNumber) || record.Snapshot == null);

                // Dictionary 不参与保存，读档后需要根据序列号重新建立快速查找表。
                RebuildIndex();
            }
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            RebuildIndex();
        }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
            RebuildIndex();
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();

            // 深拷贝 Pawn 的成本较高，必须限制频率。
            if (Find.TickManager.TicksGame % BackupIntervalTicks != 0)
            {
                return;
            }

            // AllMapsWorldAndTemporary_Alive 覆盖地图、世界 Pawn 和临时 Pawn。
            // 这里不备份死亡 Pawn，因为死亡对象会在 Kill/Destroy 前缀中即时备份。
            var pawns = PawnsFinder.AllMapsWorldAndTemporary_Alive;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (IsArtificialMaid(pawn))
                {
                    BackupPawn(pawn);
                }
            }
        }

        public static void NotifyMaidKilled(Pawn pawn)
        {
            // Kill 前做一次即时备份，尽量保留死亡前的最新状态。
            Current?.BackupPawn(pawn);
        }

        public static void NotifyMaidDestroyed(Pawn pawn)
        {
            // Destroy 可能来自其他 Mod，执行后原 Pawn 可能彻底脱离游戏对象树，因此必须在前缀中备份。
            Current?.BackupPawn(pawn);
        }

        /// <summary>
        /// 检查指定序列号是否可以恢复。
        /// 如果同序列号女仆仍然存活，则拒绝恢复，避免生成重复个体。
        /// </summary>
        public static bool CanRestore(string serialNumber, out string reason)
        {
            reason = null;
            var cloud = Current;
            if (cloud == null || !cloud.TryGetRecord(serialNumber, out BackupRecord record) || record.Snapshot == null)
            {
                reason = "ArtificialMaidBackupCloudNoBackup".Translate();
                return false;
            }

            Pawn existingPawn = FindExistingMaidBySerial(serialNumber);
            if (existingPawn != null && !existingPawn.Dead && !existingPawn.Destroyed)
            {
                // 序列号代表女仆身份；原单位仍活动时，不允许从备份复制出第二个同序列号单位。
                reason = "ArtificialMaidBackupCloudStillActive".Translate(existingPawn.LabelShort);
                return false;
            }

            return true;
        }

        /// <summary>
        /// 按序列号恢复人造人女仆。
        /// 优先复活仍能找到的死亡 Pawn；找不到原 Pawn 时，才从云端快照重建。
        /// </summary>
        public static bool TryRestore(string serialNumber, Map map, IntVec3 position, out Pawn restoredPawn)
        {
            restoredPawn = null;
            var cloud = Current;
            if (cloud == null || !cloud.TryGetRecord(serialNumber, out BackupRecord record) || record.Snapshot == null)
            {
                return false;
            }

            Pawn existingPawn = FindExistingMaidBySerial(serialNumber);
            if (existingPawn != null)
            {
                // 原 Pawn 还在游戏中时，必须复用原对象，避免关系、记录、引用出现不必要的分叉。
                if (!existingPawn.Dead && !existingPawn.Destroyed)
                {
                    return false;
                }

                restoredPawn = existingPawn;
                if (existingPawn.Dead)
                {
                    // 对仍有死亡 Pawn 或尸体的情况，使用原版复活流程恢复内部状态。
                    if (!ResurrectionUtility.TryResurrect(existingPawn, ResurrectionParms))
                    {
                        return false;
                    }
                }

                FinalizeRestoredPawn(restoredPawn, map, position);
                cloud.BackupPawn(restoredPawn);
                return true;
            }

            // 原 Pawn 已经找不到，说明很可能被其他 Mod Destroy 或从容器中移除，只能从快照重建。
            Pawn rebuiltPawn = ClonePawn(record.Snapshot);
            if (rebuiltPawn == null)
            {
                return false;
            }

            restoredPawn = rebuiltPawn;
            FinalizeRestoredPawn(restoredPawn, map, position);
            cloud.BackupPawn(restoredPawn);
            return true;
        }

        private static ResurrectionParams ResurrectionParms
        {
            get
            {
                // 人造人女仆恢复应当是无副作用的系统重启，不产生伤疤、绑架或逃跑等普通复活事件。
                return new ResurrectionParams
                {
                    gettingScarsChance = 0f,
                    canKidnap = false,
                    canTimeoutOrFlee = false,
                    useAvoidGridSmart = true,
                    canSteal = false,
                    invisibleStun = false
                };
            }
        }

        private bool BackupPawn(Pawn pawn)
        {
            // Scribe 正在保存/读取时不能再嵌套启动临时 Scribe，否则会破坏全局 Scribe 状态。
            if (!IsArtificialMaid(pawn) || Scribe.mode != LoadSaveMode.Inactive)
            {
                return false;
            }

            CompArtificialMaid comp = CompArtificialMaid.GetCompCached(pawn);
            string serialNumber = comp?.serialNumber;
            if (string.IsNullOrEmpty(serialNumber))
            {
                // 序列号是云端备份的身份主键。没有序列号时不建立备份，避免无法匹配恢复目标。
                return false;
            }

            // 必须保存独立快照，而不是保存原 Pawn 引用；原 Pawn 被 Destroy 后引用会失效。
            Pawn snapshot = ClonePawn(pawn);
            if (snapshot == null)
            {
                return false;
            }

            if (!backupsBySerial.TryGetValue(serialNumber, out BackupRecord record))
            {
                // 每个序列号只保留最新备份，减少存档体积并避免恢复菜单重复。
                record = new BackupRecord();
                backups.Add(record);
                backupsBySerial[serialNumber] = record;
            }

            record.UpdateFrom(pawn, snapshot, serialNumber, comp);
            return true;
        }

        private static Pawn ClonePawn(Pawn source)
        {
            if (source == null)
            {
                return null;
            }

            // RimWorld 没有公开的 Pawn 深拷贝 API。
            // 这里通过临时 XML 文件走一遍 Scribe 深度保存/读取，得到结构完整的独立 Pawn。
            string filePath = Path.Combine(GenFilePaths.TempFolderPath, "OuterrealmTechRobot_ArtificialMaidBackup_" + Guid.NewGuid().ToString("N") + ".xml");
            Pawn pawnToSave = source;
            Pawn clone = null;

            try
            {
                Scribe.saver.InitSaving(filePath, "artificialMaidBackup");

                // 临时快照不是完整存档，可能引用外部 Pawn 或 Faction。
                // 标记为 debug 风格保存，避免 DevMode 下产生“引用对象未深度保存”的误导性警告。
                Scribe.saver.savingForDebug = true;
                Scribe_Deep.Look(ref pawnToSave, "pawn");
                Scribe.saver.FinalizeSaving();

                // 立即从临时文件读回，形成与原 Pawn 分离的新对象图。
                Scribe.loader.InitLoading(filePath);
                Scribe_Deep.Look(ref clone, "pawn");
                Scribe.loader.FinalizeLoading();
            }
            catch (Exception ex)
            {
                Log.Warning("[OuterrealmTechRobot] Failed to clone Artificial Maid backup pawn: " + ex);
                Scribe.ForceStop();
                clone = null;
            }
            finally
            {
                try
                {
                    // 临时文件只用于本次深拷贝，必须尽快清理。
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("[OuterrealmTechRobot] Failed to delete temporary Artificial Maid backup file: " + ex.Message);
                }
            }

            if (clone == null)
            {
                return null;
            }

            PrepareSnapshotPawn(clone);
            return clone;
        }

        private static void PrepareSnapshotPawn(Pawn pawn)
        {
            // 快照应保持未生成状态，避免保存时被当作地图上的真实 Pawn。
            pawn.ForceSetStateToUnspawned();
            pawn.SetPositionDirect(IntVec3.Invalid);

            // 深拷贝会带着原 Pawn 的 ThingID；若不换 ID，存档中会出现重复 loadID。
            pawn.thingIDNumber = Find.UniqueIDsManager.GetNextThingID();

            // 装备、背包、携带物等子 Thing 也可能带有原 loadID，必须递归换掉。
            tmpHeldThings.Clear();
            ThingOwnerUtility.GetAllThingsRecursively(pawn, tmpHeldThings);
            for (int i = 0; i < tmpHeldThings.Count; i++)
            {
                Thing thing = tmpHeldThings[i];
                if (thing != null && thing.def != null && thing.def.HasThingIDNumber)
                {
                    thing.thingIDNumber = Find.UniqueIDsManager.GetNextThingID();
                }
            }
            tmpHeldThings.Clear();

            // 确保动态组件完整，尤其是读档、派系或 DLC 状态变化后可能需要补齐的 tracker。
            PawnComponentsUtility.CreateInitialComponents(pawn);
            PawnComponentsUtility.AddAndRemoveDynamicComponents(pawn, true);

            // 快照不应保留正在执行的 Job，恢复后由 AI 重新决策。
            pawn.jobs?.StopAll(false);
        }

        private static void FinalizeRestoredPawn(Pawn pawn, Map map, IntVec3 position)
        {
            if (pawn == null)
            {
                return;
            }

            map = map ?? Find.CurrentMap ?? Find.AnyPlayerHomeMap;
            if (map == null)
            {
                // 没有可用地图时无法放置 Pawn。当前调用方来自地图上的终端，正常不会发生。
                return;
            }

            if (pawn.Faction != Faction.OfPlayer)
            {
                // 恢复出的女仆默认归属玩家，符合现有女仆安全协议逻辑。
                pawn.SetFaction(Faction.OfPlayer);
            }

            if (pawn.Dead)
            {
                // 防御性处理：TryRestore 已经处理死亡 Pawn，这里保证其他调用路径也能恢复。
                ResurrectionUtility.TryResurrect(pawn, ResurrectionParms);
            }

            // 派系变化后再次刷新动态组件，补齐玩家派系需要的 drafter、playerSettings 等。
            PawnComponentsUtility.CreateInitialComponents(pawn);
            PawnComponentsUtility.AddAndRemoveDynamicComponents(pawn, true);

            // 恢复位置优先使用调用者指定位置，例如终端所在格；不可用时寻找附近可站立格。
            IntVec3 spawnCell = position.IsValid ? position : map.Center;
            if (!CellFinder.TryFindRandomSpawnCellForPawnNear(spawnCell, map, out spawnCell))
            {
                spawnCell = map.Center;
            }

            if (!pawn.Spawned)
            {
                // 从云端快照重建的 Pawn 通常走这个分支。
                pawn.ForceSetStateToUnspawned();
                GenSpawn.Spawn(pawn, spawnCell, map);
            }
            else if (pawn.Map == map)
            {
                // 复活原 Pawn 后如果已经在目标地图，直接移动到恢复位置。
                pawn.Position = spawnCell;
            }
            else
            {
                // 复活位置不在目标地图时，先反生成再生成到目标地图。
                pawn.DeSpawnOrDeselect();
                pawn.ForceSetStateToUnspawned();
                GenSpawn.Spawn(pawn, spawnCell, map);
            }

            // 清理恢复前残留的任务、路径和伤病，然后交给女仆 Comp 做完整自检。
            pawn.jobs?.StopAll(false);
            pawn.pather?.StopDead();
            pawn.health?.Reset();

            CompArtificialMaid comp = CompArtificialMaid.GetCompCached(pawn);
            comp?.FullRepair();
            pawn.Drawer?.renderer?.SetAllGraphicsDirty();

            Find.LetterStack.ReceiveLetter("ArtificialMaidBackupCloudRestoredLabel".Translate(),
                "ArtificialMaidBackupCloudRestoredText".Translate(pawn.LabelShort),
                LetterDefOf.PositiveEvent, pawn);
        }

        private bool TryGetRecord(string serialNumber, out BackupRecord record)
        {
            // 运行时索引可能因读档或异常修复而失效，使用前进行轻量一致性检查。
            if (backupsBySerial == null || backupsBySerial.Count != backups.Count)
            {
                RebuildIndex();
            }

            return backupsBySerial.TryGetValue(serialNumber, out record);
        }

        private void RebuildIndex()
        {
            // Dictionary 是运行时缓存，不写入存档；所有数据来源都以 backups 列表为准。
            if (backupsBySerial == null)
            {
                backupsBySerial = new Dictionary<string, BackupRecord>();
            }
            else
            {
                backupsBySerial.Clear();
            }

            if (backups == null)
            {
                backups = new List<BackupRecord>();
                return;
            }

            for (int i = backups.Count - 1; i >= 0; i--)
            {
                BackupRecord record = backups[i];
                if (record == null || string.IsNullOrEmpty(record.SerialNumber) || record.Snapshot == null)
                {
                    // 坏记录没有恢复价值，直接清理，避免终端菜单出现无效项。
                    backups.RemoveAt(i);
                    continue;
                }

                // 如果旧存档里意外有重复序列号，后面的记录覆盖前面的记录，相当于保留最新可索引项。
                backupsBySerial[record.SerialNumber] = record;
            }
        }

        /// <summary>
        /// 在当前游戏对象树中查找同序列号女仆。
        /// 搜索范围包括活体 Pawn、世界 Pawn、临时 Pawn，以及地图上的尸体。
        /// </summary>
        private static Pawn FindExistingMaidBySerial(string serialNumber)
        {
            if (string.IsNullOrEmpty(serialNumber))
            {
                return null;
            }

            List<Pawn> pawns = PawnsFinder.All_AliveOrDead;
            for (int i = 0; i < pawns.Count; i++)
            {
                // All_AliveOrDead 覆盖多数正常保存的 Pawn，但不一定覆盖所有尸体容器。
                Pawn pawn = pawns[i];
                if (PawnHasSerial(pawn, serialNumber))
                {
                    return pawn;
                }
            }

            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                // 额外扫描尸体，保证 Kill 后留下 Corpse 的女仆能优先复活原 Pawn。
                List<Thing> corpses = maps[i].listerThings.ThingsInGroup(ThingRequestGroup.Corpse);
                for (int j = 0; j < corpses.Count; j++)
                {
                    if (corpses[j] is Corpse corpse && PawnHasSerial(corpse.InnerPawn, serialNumber))
                    {
                        return corpse.InnerPawn;
                    }
                }
            }

            return null;
        }

        private static bool PawnHasSerial(Pawn pawn, string serialNumber)
        {
            if (!IsArtificialMaid(pawn))
            {
                return false;
            }

            CompArtificialMaid comp = CompArtificialMaid.GetCompCached(pawn);
            // 不使用 Pawn.thingIDNumber 匹配，因为云端快照会重新分配 ThingID。
            return comp != null && comp.serialNumber == serialNumber;
        }

        private static bool IsArtificialMaid(Pawn pawn)
        {
            return pawn != null && pawn.def == ArtificialMaidDefOf.ArtificialMaid;
        }

        private static bool CurrentGameValid()
        {
            return Verse.Current.Game != null;
        }

        public class BackupRecord : IExposable
        {
            // 女仆身份主键。恢复、去重和菜单项都围绕序列号进行。
            private string serialNumber;

            // 最近一次备份时的显示名称，仅用于菜单展示。
            private string label;

            // 最近一次成功备份的游戏 tick，可用于之后扩展 UI 显示备份时间。
            private int lastBackupTick = -1;

            // 记录原 Pawn 的 ThingID 仅用于诊断，不参与恢复匹配。
            private int originalThingId = -1;

            // 以下字段镜像 CompArtificialMaid 的身份信息，便于后续扩展或排查存档问题。
            private int manufactureTick = -1;
            private int joinPlayerTick = -1;
            private bool isDuplicate;
            private string originSerialNumber;

            // 独立 Pawn 快照。该对象不应生成在地图上，只作为恢复模板保存。
            private Pawn snapshot;

            // 对原 Pawn 的弱意义引用：能解析时用于诊断，不能解析时不影响从 snapshot 恢复。
            private Pawn lastKnownPawn;

            public string SerialNumber => serialNumber;
            public string Label => label;
            public int LastBackupTick => lastBackupTick;
            public Pawn Snapshot => snapshot;

            public void UpdateFrom(Pawn source, Pawn newSnapshot, string newSerialNumber, CompArtificialMaid comp)
            {
                // 每次备份覆盖同序列号的旧快照，保证恢复时使用最新状态。
                serialNumber = newSerialNumber;
                label = source.LabelShort;
                lastBackupTick = Find.TickManager.TicksGame;
                originalThingId = source.thingIDNumber;
                manufactureTick = comp != null ? comp.manufactureTick : -1;
                joinPlayerTick = comp != null ? comp.joinPlayerTick : -1;
                isDuplicate = comp != null && comp.isDuplicate;
                originSerialNumber = comp?.originSerialNumber;
                snapshot = newSnapshot;
                lastKnownPawn = source;
            }

            public void ExposeData()
            {
                // 这些值用于菜单、诊断和未来扩展。
                Scribe_Values.Look(ref serialNumber, "serialNumber");
                Scribe_Values.Look(ref label, "label");
                Scribe_Values.Look(ref lastBackupTick, "lastBackupTick", -1);
                Scribe_Values.Look(ref originalThingId, "originalThingId", -1);
                Scribe_Values.Look(ref manufactureTick, "manufactureTick", -1);
                Scribe_Values.Look(ref joinPlayerTick, "joinPlayerTick", -1);
                Scribe_Values.Look(ref isDuplicate, "isDuplicate", false);
                Scribe_Values.Look(ref originSerialNumber, "originSerialNumber");

                // 快照必须深度保存，否则 Destroy 后无法重建 Pawn。
                Scribe_Deep.Look(ref snapshot, "snapshot");

                // 引用原 Pawn 时允许保存 Destroyed Thing，避免刚被 Destroy 前缀备份时丢失引用信息。
                Scribe_References.Look(ref lastKnownPawn, "lastKnownPawn", true);
            }

            public string FloatMenuLabel
            {
                get
                {
                    // 菜单显示优先使用备份时记录的名称；旧记录缺失时回退到快照名称。
                    string resolvedLabel = string.IsNullOrEmpty(label) && snapshot != null ? snapshot.LabelShort : label;
                    if (string.IsNullOrEmpty(resolvedLabel))
                    {
                        resolvedLabel = "ArtificialMaidBackupCloudUnknownMaid".Translate();
                    }

                    return "ArtificialMaidBackupCloudEntry".Translate(resolvedLabel, serialNumber);
                }
            }
        }
    }
}
