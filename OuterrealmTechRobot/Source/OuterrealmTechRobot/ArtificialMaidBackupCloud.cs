using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using RimWorld.Planet;
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
        public enum MaidObjectState
        {
            Unknown,
            ActiveOnMap,
            ActiveInCaravan,
            Contained,
            WorldPawn,
            Dead,
            Destroyed,
            Discarded,
            Transitioning,
            Unrooted,
            Missing,
            SerialConflict
        }

        public enum MaidBackupState
        {
            NeverAttempted,
            Capturing,
            Valid,
            ValidPartial,
            Stale,
            Failed,
            Corrupted,
            Incompatible
        }

        public enum RecoveryAction
        {
            None,
            RecallOriginal,
            ResurrectOriginal,
            RebuildFromBackup
        }

        // 每 60000 tick（约 1 游戏天）更新一次完整备份。
        private const int BackupIntervalTicks = 60000;

        // 低成本审计只检查女仆的位置和对象根，不执行深拷贝。
        private const int AuditIntervalTicks = 600;

        // 首次生成后延迟少量 tick，等待其他 Mod 完成 Pawn 初始化。
        private const int InitialBackupDelayTicks = 30;

        // 存档用列表。Scribe 可以稳定保存 List，但 Dictionary 需要加载后重建索引。
        private List<BackupRecord> backups = new List<BackupRecord>();

        // 运行时索引。key 使用 CompArtificialMaid.serialNumber，而不是 Pawn 的 thingIDNumber。
        private Dictionary<string, BackupRecord> backupsBySerial = new Dictionary<string, BackupRecord>();

        // 身份注册表独立于快照保存；即使备份失败，女仆也必须出现在管理界面中。
        private List<MaidRegistryRecord> maidRegistry = new List<MaidRegistryRecord>();

        private Dictionary<string, MaidRegistryRecord> registryBySerial =
            new Dictionary<string, MaidRegistryRecord>();

        private int nextAuditTick;
        private int backupCursor;

        private readonly Dictionary<string, Pawn> auditFirstPawnBySerial =
            new Dictionary<string, Pawn>();

        private readonly HashSet<string> auditConflictingSerials = new HashSet<string>();

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

        public static IReadOnlyList<MaidRegistryRecord> RegistryForReading
        {
            get
            {
                ArtificialMaidBackupCloud cloud = Current;
                return cloud != null
                    ? cloud.maidRegistry
                    : (IReadOnlyList<MaidRegistryRecord>)Array.Empty<MaidRegistryRecord>();
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref backups, "backups", LookMode.Deep);
            Scribe_Collections.Look(ref maidRegistry, "maidRegistry", LookMode.Deep);
            Scribe_Values.Look(ref nextAuditTick, "nextAuditTick", 0);
            Scribe_Values.Look(ref backupCursor, "backupCursor", 0);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (backups == null)
                {
                    backups = new List<BackupRecord>();
                }

                if (maidRegistry == null)
                {
                    maidRegistry = new List<MaidRegistryRecord>();
                }

                RebuildIndex();
                MigrateLegacyBackupsToRegistry();
                nextAuditTick = Find.TickManager != null ? Find.TickManager.TicksGame + 1 : 1;
            }
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            RebuildIndex();
            MigrateLegacyBackupsToRegistry();
            nextAuditTick = Find.TickManager.TicksGame + 1;
            Log.Message("[OuterrealmTechRobot] Artificial Maid backup cloud loaded. Registry=" +
                        maidRegistry.Count + ", backups=" + backups.Count);
        }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
            RebuildIndex();
            nextAuditTick = Find.TickManager.TicksGame + 1;
            Log.Message("[OuterrealmTechRobot] Artificial Maid backup cloud started.");
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();

            int ticksGame = Find.TickManager.TicksGame;
            if (ticksGame >= nextAuditTick)
            {
                AuditAllMaids();
                nextAuditTick = ticksGame + AuditIntervalTicks;
            }

            // 每 tick 最多处理一个到期备份，避免多个女仆同时深拷贝造成卡顿。
            if (maidRegistry.Count == 0)
            {
                return;
            }

            if (backupCursor >= maidRegistry.Count)
            {
                backupCursor = 0;
            }

            MaidRegistryRecord record = maidRegistry[backupCursor++];
            if (record != null && record.NextBackupTick >= 0 && ticksGame >= record.NextBackupTick)
            {
                Pawn pawn = ResolvePawn(record);
                if (pawn != null && !pawn.Dead && !pawn.Destroyed && !pawn.Discarded)
                {
                    BackupPawn(pawn, false);
                }
                else
                {
                    record.ScheduleNextBackup(ticksGame + BackupIntervalTicks);
                }
            }
        }

        public static void NotifyMaidKilled(Pawn pawn)
        {
            ArtificialMaidBackupCloud cloud = Current;
            cloud?.RegisterOrUpdateMaid(pawn, true);
            cloud?.BackupPawn(pawn, true);
            cloud?.SetObjectState(pawn, MaidObjectState.Dead, "Kill");
        }

        public static void NotifyMaidDestroyed(Pawn pawn)
        {
            ArtificialMaidBackupCloud cloud = Current;
            cloud?.RegisterOrUpdateMaid(pawn, true);
            cloud?.BackupPawn(pawn, true);
            cloud?.SetObjectState(pawn, MaidObjectState.Destroyed, "Destroy");
        }

        public static void NotifyMaidDiscarding(Pawn pawn)
        {
            ArtificialMaidBackupCloud cloud = Current;
            cloud?.RegisterOrUpdateMaid(pawn, true);
            cloud?.BackupPawn(pawn, true);
            cloud?.SetObjectState(pawn, MaidObjectState.Discarded, "Discard");
        }

        public static void NotifyMaidSpawned(Pawn pawn)
        {
            ArtificialMaidBackupCloud cloud = Current;
            MaidRegistryRecord record = cloud?.RegisterOrUpdateMaid(pawn, true);
            if (record != null)
            {
                record.ScheduleNextBackup(Find.TickManager.TicksGame + InitialBackupDelayTicks);
            }
        }

        public static void NotifyMaidDespawned(Pawn pawn)
        {
            ArtificialMaidBackupCloud cloud = Current;
            MaidRegistryRecord record = cloud?.RegisterOrUpdateMaid(pawn, true);
            if (record != null)
            {
                // 先保存转移前状态；之后即使第三方容器接收失败，也有可用快照。
                cloud.BackupPawn(pawn, true);
                record.SetObjectState(MaidObjectState.Transitioning, "DeSpawn", Find.TickManager.TicksGame);
            }
        }

        /// <summary>
        /// 检查指定序列号是否可以恢复。
        /// 如果同序列号女仆仍然存活，则拒绝恢复，避免生成重复个体。
        /// </summary>
        public static bool CanRestore(string serialNumber, out string reason)
        {
            return GetRecoveryAction(serialNumber, out reason) != RecoveryAction.None;
        }

        /// <summary>
        /// 按序列号恢复人造人女仆。
        /// 优先复活仍能找到的死亡 Pawn；找不到原 Pawn 时，才从云端快照重建。
        /// </summary>
        public static bool TryRestore(string serialNumber, Map map, IntVec3 position, out Pawn restoredPawn)
        {
            return TryRecoverOrRestore(serialNumber, map, position, out restoredPawn, out _);
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

        public static bool RequestBackup(Pawn pawn, bool immediate, out string reason)
        {
            reason = null;
            ArtificialMaidBackupCloud cloud = Current;
            if (cloud == null)
            {
                reason = "ArtificialMaidBackupCloudServiceUnavailable".Translate();
                return false;
            }

            MaidRegistryRecord registryRecord = cloud.RegisterOrUpdateMaid(pawn, true);
            if (registryRecord == null)
            {
                reason = "ArtificialMaidBackupCloudInvalidMaid".Translate();
                return false;
            }

            if (!immediate)
            {
                registryRecord.ScheduleNextBackup(Find.TickManager.TicksGame + 1);
                return true;
            }

            bool success = cloud.BackupPawn(pawn, true);
            reason = registryRecord.LastBackupError;
            return success;
        }

        private bool BackupPawn(Pawn pawn, bool lifecycleBackup)
        {
            MaidRegistryRecord registryRecord = RegisterOrUpdateMaid(pawn, true);
            if (registryRecord == null)
            {
                return false;
            }

            int ticksGame = Find.TickManager.TicksGame;
            registryRecord.BeginBackupAttempt(ticksGame);

            // Scribe 正在保存/读取时不能嵌套启动临时 Scribe。生命周期事件在存档期间发生时延后重试。
            if (Scribe.mode != LoadSaveMode.Inactive)
            {
                registryRecord.FailBackup("ScribeBusy", "Scribe mode is " + Scribe.mode,
                    ticksGame + InitialBackupDelayTicks);
                return false;
            }

            CompArtificialMaid comp = CompArtificialMaid.GetCompCached(pawn);
            string serialNumber = comp?.serialNumber;
            if (string.IsNullOrEmpty(serialNumber))
            {
                registryRecord.FailBackup("MissingSerial", "Artificial Maid has no serial number.",
                    ticksGame + InitialBackupDelayTicks);
                return false;
            }

            MaidCoreSnapshot coreSnapshot;
            try
            {
                coreSnapshot = MaidCoreSnapshot.FromPawn(pawn, comp);
            }
            catch (Exception ex)
            {
                registryRecord.FailBackup("CoreCapture", ex.ToString(), ticksGame + BackupIntervalTicks);
                Log.Error("[OuterrealmTechRobot] Failed to capture Artificial Maid core backup. pawn=" +
                          pawn.ToStringSafe() + "\n" + ex);
                return false;
            }

            Pawn snapshot = ClonePawn(pawn, out string cloneError);
            GearBackup gearBackup = null;
            string gearError = null;
            try
            {
                gearBackup = GearBackup.FromPawn(pawn);
            }
            catch (Exception ex)
            {
                gearError = ex.ToString();
            }

            // 即使完整 Pawn 克隆失败，核心快照仍能重建可用女仆。
            BackupRecord candidate = new BackupRecord();
            candidate.UpdateFrom(pawn, snapshot, coreSnapshot, gearBackup, serialNumber, comp,
                cloneError, gearError);

            ReplaceBackupRecord(serialNumber, candidate);
            registryRecord.CompleteBackup(candidate, ticksGame + BackupIntervalTicks);

            if (!string.IsNullOrEmpty(cloneError))
            {
                Log.Warning("[OuterrealmTechRobot] Artificial Maid full snapshot failed; core snapshot retained. pawn=" +
                            pawn.ToStringSafe() + ", error=" + cloneError);
            }

            if (!string.IsNullOrEmpty(gearError))
            {
                Log.Warning("[OuterrealmTechRobot] Artificial Maid gear snapshot failed. pawn=" +
                            pawn.ToStringSafe() + ", error=" + gearError);
            }

            return true;
        }

        private void ReplaceBackupRecord(string serialNumber, BackupRecord candidate)
        {
            if (backupsBySerial.TryGetValue(serialNumber, out BackupRecord oldRecord))
            {
                int index = backups.IndexOf(oldRecord);
                if (index >= 0)
                {
                    backups[index] = candidate;
                }
                else
                {
                    backups.Add(candidate);
                }
            }
            else
            {
                backups.Add(candidate);
            }

            backupsBySerial[serialNumber] = candidate;
        }

        private static Pawn ClonePawn(Pawn source)
        {
            return ClonePawn(source, out _);
        }

        private static Pawn ClonePawn(Pawn source, out string error)
        {
            error = null;
            if (source == null)
            {
                error = "Source pawn is null.";
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
                error = ex.ToString();
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
                if (string.IsNullOrEmpty(error))
                {
                    error = "Scribe returned a null Pawn snapshot.";
                }
                return null;
            }

            try
            {
                PrepareSnapshotPawn(clone);
                return clone;
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                Log.Warning("[OuterrealmTechRobot] Failed to prepare Artificial Maid backup pawn: " + ex);
                return null;
            }
        }

        private static T CloneThing<T>(T source) where T : Thing
        {
            if (source == null)
            {
                return null;
            }

            // 装备单独克隆时也走 Scribe，确保武器、服装、库存物品上的 Comp 数据完整保留。
            string filePath = Path.Combine(GenFilePaths.TempFolderPath, "OuterrealmTechRobot_ArtificialMaidGearBackup_" + Guid.NewGuid().ToString("N") + ".xml");
            Thing thingToSave = source;
            Thing clone = null;

            try
            {
                Scribe.saver.InitSaving(filePath, "artificialMaidGearBackup");
                Scribe.saver.savingForDebug = true;
                Scribe_Deep.Look(ref thingToSave, "thing");
                Scribe.saver.FinalizeSaving();

                Scribe.loader.InitLoading(filePath);
                Scribe_Deep.Look(ref clone, "thing");
                Scribe.loader.FinalizeLoading();
            }
            catch (Exception ex)
            {
                Log.Warning("[OuterrealmTechRobot] Failed to clone Artificial Maid gear backup thing: " + ex);
                Scribe.ForceStop();
                clone = null;
            }
            finally
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("[OuterrealmTechRobot] Failed to delete temporary Artificial Maid gear backup file: " + ex.Message);
                }
            }

            if (clone == null)
            {
                return null;
            }

            PrepareSnapshotThing(clone);
            return clone as T;
        }

        private static void PrepareSnapshotThing(Thing thing)
        {
            if (thing == null)
            {
                return;
            }

            // 单独保存装备时也必须重新分配 ThingID，避免和原装备或 Pawn 快照中的装备重复。
            thing.ForceSetStateToUnspawned();
            if (thing.def != null && thing.def.HasThingIDNumber)
            {
                thing.thingIDNumber = Find.UniqueIDsManager.GetNextThingID();
            }

            if (thing is IThingHolder holder)
            {
                tmpHeldThings.Clear();
                ThingOwnerUtility.GetAllThingsRecursively(holder, tmpHeldThings);
                for (int i = 0; i < tmpHeldThings.Count; i++)
                {
                    Thing child = tmpHeldThings[i];
                    if (child != null && child.def != null && child.def.HasThingIDNumber)
                    {
                        child.thingIDNumber = Find.UniqueIDsManager.GetNextThingID();
                    }
                }
                tmpHeldThings.Clear();
            }
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

        private void AuditAllMaids()
        {
            auditFirstPawnBySerial.Clear();
            auditConflictingSerials.Clear();

            List<Pawn> pawns = PawnsFinder.All_AliveOrDead;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (IsArtificialMaid(pawn))
                {
                    RegisterAuditPawn(pawn);
                }
            }

            // 额外扫描尸体，避免死亡 Pawn 没有进入通用查询集合。
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                List<Thing> corpses = maps[i].listerThings.ThingsInGroup(ThingRequestGroup.Corpse);
                for (int j = 0; j < corpses.Count; j++)
                {
                    if (corpses[j] is Corpse corpse && IsArtificialMaid(corpse.InnerPawn))
                    {
                        RegisterAuditPawn(corpse.InnerPawn);
                    }
                }
            }

            int ticksGame = Find.TickManager.TicksGame;
            for (int i = 0; i < maidRegistry.Count; i++)
            {
                MaidRegistryRecord record = maidRegistry[i];
                if (record == null)
                {
                    continue;
                }

                Pawn pawn = ResolvePawn(record);
                if (auditConflictingSerials.Contains(record.SerialNumber))
                {
                    record.SetObjectState(MaidObjectState.SerialConflict, "SerialConflict", ticksGame);
                    continue;
                }

                UpdateObjectState(record, pawn, ticksGame);

                // 活着但完全脱离游戏对象树的 Pawn 先放入 WorldPawns，避免下一次存档丢失。
                if (record.ObjectState == MaidObjectState.Unrooted && pawn != null &&
                    ArtificialMaidTransferUtility.TryKeepInWorld(pawn))
                {
                    record.SetObjectState(MaidObjectState.WorldPawn, "WorldPawns", ticksGame);
                }
            }

            auditFirstPawnBySerial.Clear();
            auditConflictingSerials.Clear();
        }

        private void RegisterAuditPawn(Pawn pawn)
        {
            MaidRegistryRecord record = RegisterOrUpdateMaid(pawn, true);
            if (record == null)
            {
                return;
            }

            if (auditFirstPawnBySerial.TryGetValue(record.SerialNumber, out Pawn first) && first != pawn)
            {
                auditConflictingSerials.Add(record.SerialNumber);
            }
            else
            {
                auditFirstPawnBySerial[record.SerialNumber] = pawn;
            }
        }

        private MaidRegistryRecord RegisterOrUpdateMaid(Pawn pawn, bool updateLocation)
        {
            if (!IsArtificialMaid(pawn))
            {
                return null;
            }

            CompArtificialMaid comp = CompArtificialMaid.GetCompCached(pawn);
            string serialNumber = comp?.serialNumber;
            if (string.IsNullOrEmpty(serialNumber))
            {
                return null;
            }

            if (!registryBySerial.TryGetValue(serialNumber, out MaidRegistryRecord record))
            {
                record = new MaidRegistryRecord();
                record.InitializeFrom(pawn, comp);
                maidRegistry.Add(record);
                registryBySerial[serialNumber] = record;
            }
            else
            {
                record.UpdateIdentity(pawn, comp);
            }

            if (updateLocation)
            {
                UpdateObjectState(record, pawn, Find.TickManager.TicksGame);
            }

            return record;
        }

        private void SetObjectState(Pawn pawn, MaidObjectState state, string location)
        {
            MaidRegistryRecord record = RegisterOrUpdateMaid(pawn, false);
            record?.SetObjectState(state, location, Find.TickManager.TicksGame);
        }

        private static void UpdateObjectState(MaidRegistryRecord record, Pawn pawn, int ticksGame)
        {
            if (record == null)
            {
                return;
            }

            if (pawn == null)
            {
                record.SetObjectState(MaidObjectState.Missing, "Missing", ticksGame, false);
                return;
            }

            record.SetLastKnownPawn(pawn);

            if (pawn.Discarded)
            {
                record.SetObjectState(MaidObjectState.Discarded, "Discarded", ticksGame);
                return;
            }

            if (pawn.Destroyed)
            {
                record.SetObjectState(MaidObjectState.Destroyed, "Destroyed", ticksGame);
                return;
            }

            if (pawn.Dead)
            {
                record.SetObjectState(MaidObjectState.Dead, pawn.Corpse?.MapHeld?.Parent?.LabelCap ?? "Dead",
                    ticksGame);
                return;
            }

            if (pawn.Spawned)
            {
                record.SetObjectState(MaidObjectState.ActiveOnMap,
                    pawn.MapHeld?.Parent?.LabelCap ?? pawn.MapHeld?.ToString() ?? "Map", ticksGame);
                return;
            }

            Caravan caravan = pawn.GetCaravan();
            if (caravan != null)
            {
                record.SetObjectState(MaidObjectState.ActiveInCaravan, caravan.LabelCap, ticksGame);
                return;
            }

            if (pawn.ParentHolder != null)
            {
                record.SetObjectState(MaidObjectState.Contained, DescribeHolder(pawn.ParentHolder), ticksGame);
                return;
            }

            if (Find.WorldPawns.Contains(pawn))
            {
                record.SetObjectState(MaidObjectState.WorldPawn, "WorldPawns", ticksGame);
                return;
            }

            record.SetObjectState(MaidObjectState.Unrooted, "Unrooted", ticksGame);
        }

        private static string DescribeHolder(IThingHolder holder)
        {
            if (holder is Thing thing)
            {
                return thing.LabelCap;
            }

            return holder?.GetType().Name ?? "Unknown";
        }

        private Pawn ResolvePawn(MaidRegistryRecord record)
        {
            if (record == null)
            {
                return null;
            }

            Pawn found = FindExistingMaidBySerial(record.SerialNumber);
            if (found != null)
            {
                return found;
            }

            Pawn lastKnown = record.LastKnownPawn;
            if (PawnHasSerial(lastKnown, record.SerialNumber))
            {
                return lastKnown;
            }

            return null;
        }

        private void MigrateLegacyBackupsToRegistry()
        {
            for (int i = 0; i < backups.Count; i++)
            {
                BackupRecord backup = backups[i];
                if (backup == null || string.IsNullOrEmpty(backup.SerialNumber))
                {
                    continue;
                }

                if (!registryBySerial.TryGetValue(backup.SerialNumber, out MaidRegistryRecord record))
                {
                    record = new MaidRegistryRecord();
                    record.InitializeFromBackup(backup);
                    maidRegistry.Add(record);
                    registryBySerial[backup.SerialNumber] = record;
                }

                record.SyncBackupState(backup);
            }
        }

        public static bool TryGetRegistryRecord(string serialNumber, out MaidRegistryRecord record)
        {
            record = null;
            ArtificialMaidBackupCloud cloud = Current;
            if (cloud == null || string.IsNullOrEmpty(serialNumber))
            {
                return false;
            }

            if (cloud.registryBySerial == null ||
                cloud.registryBySerial.Count != cloud.maidRegistry.Count)
            {
                cloud.RebuildIndex();
            }

            return cloud.registryBySerial.TryGetValue(serialNumber, out record);
        }

        public static void RequestAudit()
        {
            ArtificialMaidBackupCloud cloud = Current;
            if (cloud == null)
            {
                return;
            }

            cloud.AuditAllMaids();
            cloud.nextAuditTick = Find.TickManager.TicksGame + AuditIntervalTicks;
        }

        public static RecoveryAction GetRecoveryAction(string serialNumber, out string reason)
        {
            reason = null;
            ArtificialMaidBackupCloud cloud = Current;
            if (cloud == null || !TryGetRegistryRecord(serialNumber, out MaidRegistryRecord registry))
            {
                reason = "ArtificialMaidBackupCloudNoRegistry".Translate();
                return RecoveryAction.None;
            }

            Pawn pawn = cloud.ResolvePawn(registry);
            if (registry.ObjectState == MaidObjectState.SerialConflict)
            {
                reason = "ArtificialMaidBackupCloudSerialConflict".Translate();
                return RecoveryAction.None;
            }

            UpdateObjectState(registry, pawn, Find.TickManager.TicksGame);

            if (pawn != null && !pawn.Dead && !pawn.Destroyed && !pawn.Discarded)
            {
                if (registry.ObjectState == MaidObjectState.Unrooted ||
                    registry.ObjectState == MaidObjectState.WorldPawn)
                {
                    return RecoveryAction.RecallOriginal;
                }

                reason = "ArtificialMaidBackupCloudStillActive".Translate(pawn.LabelShort);
                return RecoveryAction.None;
            }

            if (pawn != null && pawn.Dead)
            {
                return RecoveryAction.ResurrectOriginal;
            }

            if (cloud.TryGetRecord(serialNumber, out BackupRecord backup) && backup.IsUsable)
            {
                return RecoveryAction.RebuildFromBackup;
            }

            reason = registry.BackupState == MaidBackupState.Failed
                ? "ArtificialMaidBackupCloudBackupFailedReason".Translate(registry.LastBackupStage)
                : "ArtificialMaidBackupCloudNoBackup".Translate();
            return RecoveryAction.None;
        }

        public static bool TryRecoverOrRestore(string serialNumber, Map map, IntVec3 position,
            out Pawn restoredPawn, out string reason)
        {
            restoredPawn = null;
            RecoveryAction action = GetRecoveryAction(serialNumber, out reason);
            if (action == RecoveryAction.None)
            {
                return false;
            }

            ArtificialMaidBackupCloud cloud = Current;
            MaidRegistryRecord registry = cloud.registryBySerial[serialNumber];
            Pawn existing = cloud.ResolvePawn(registry);

            try
            {
                if (action == RecoveryAction.RecallOriginal)
                {
                    if (existing == null || existing.Destroyed || existing.Dead || existing.Discarded)
                    {
                        reason = "ArtificialMaidBackupCloudRestoreFailed".Translate();
                        return false;
                    }

                    if (Find.WorldPawns.Contains(existing))
                    {
                        Find.WorldPawns.RemovePawn(existing);
                    }

                    restoredPawn = existing;
                    FinalizeRestoredPawn(restoredPawn, map, position);
                }
                else if (action == RecoveryAction.ResurrectOriginal)
                {
                    if (existing == null || !ResurrectionUtility.TryResurrect(existing, ResurrectionParms))
                    {
                        reason = "ArtificialMaidBackupCloudRestoreFailed".Translate();
                        return false;
                    }

                    restoredPawn = existing;
                    FinalizeRestoredPawn(restoredPawn, map, position);
                    if (cloud.TryGetRecord(serialNumber, out BackupRecord deathBackup))
                    {
                        deathBackup.RestoreGearTo(restoredPawn);
                    }
                }
                else
                {
                    if (!cloud.TryGetRecord(serialNumber, out BackupRecord backup) || !backup.IsUsable)
                    {
                        reason = "ArtificialMaidBackupCloudNoBackup".Translate();
                        return false;
                    }

                    restoredPawn = backup.CreateRestoredPawn();
                    if (restoredPawn == null)
                    {
                        reason = "ArtificialMaidBackupCloudRestoreFailed".Translate();
                        return false;
                    }

                    FinalizeRestoredPawn(restoredPawn, map, position);
                    backup.RestoreGearTo(restoredPawn);
                }

                cloud.RegisterOrUpdateMaid(restoredPawn, true);
                cloud.BackupPawn(restoredPawn, true);
                reason = null;
                return restoredPawn.Spawned && !restoredPawn.Destroyed;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                Log.Error("[OuterrealmTechRobot] Artificial Maid recovery failed. serial=" + serialNumber +
                          "\n" + ex);
                return false;
            }
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
            }

            for (int i = backups.Count - 1; i >= 0; i--)
            {
                BackupRecord record = backups[i];
                if (record == null || string.IsNullOrEmpty(record.SerialNumber))
                {
                    backups.RemoveAt(i);
                    continue;
                }

                // 如果旧存档里意外有重复序列号，后面的记录覆盖前面的记录，相当于保留最新可索引项。
                backupsBySerial[record.SerialNumber] = record;
            }

            if (registryBySerial == null)
            {
                registryBySerial = new Dictionary<string, MaidRegistryRecord>();
            }
            else
            {
                registryBySerial.Clear();
            }

            if (maidRegistry == null)
            {
                maidRegistry = new List<MaidRegistryRecord>();
            }

            for (int i = maidRegistry.Count - 1; i >= 0; i--)
            {
                MaidRegistryRecord record = maidRegistry[i];
                if (record == null || string.IsNullOrEmpty(record.SerialNumber))
                {
                    maidRegistry.RemoveAt(i);
                    continue;
                }

                registryBySerial[record.SerialNumber] = record;
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

        public class MaidRegistryRecord : IExposable
        {
            private string serialNumber;
            private string label;
            private int originalThingId = -1;
            private int manufactureTick = -1;
            private int joinPlayerTick = -1;
            private bool isDuplicate;
            private string originSerialNumber;
            private Pawn lastKnownPawn;
            private int lastSeenTick = -1;
            private MaidObjectState objectState;
            private string lastKnownLocation;
            private MaidBackupState backupState;
            private int lastBackupAttemptTick = -1;
            private int lastSuccessfulBackupTick = -1;
            private int nextBackupTick = -1;
            private string lastBackupStage;
            private string lastBackupError;
            private bool backupIsPartial;

            public string SerialNumber => serialNumber;
            public string Label => label;
            public int OriginalThingId => originalThingId;
            public int ManufactureTick => manufactureTick;
            public int JoinPlayerTick => joinPlayerTick;
            public bool IsDuplicate => isDuplicate;
            public string OriginSerialNumber => originSerialNumber;
            public Pawn LastKnownPawn => lastKnownPawn;
            public int LastSeenTick => lastSeenTick;
            public MaidObjectState ObjectState => objectState;
            public string LastKnownLocation => lastKnownLocation;
            public MaidBackupState BackupState => backupState;
            public int LastBackupAttemptTick => lastBackupAttemptTick;
            public int LastSuccessfulBackupTick => lastSuccessfulBackupTick;
            public int NextBackupTick => nextBackupTick;
            public string LastBackupStage => lastBackupStage;
            public string LastBackupError => lastBackupError;
            public bool BackupIsPartial => backupIsPartial;

            public void InitializeFrom(Pawn pawn, CompArtificialMaid comp)
            {
                UpdateIdentity(pawn, comp);
                backupState = MaidBackupState.NeverAttempted;
                nextBackupTick = Find.TickManager.TicksGame + InitialBackupDelayTicks;
            }

            public void InitializeFromBackup(BackupRecord backup)
            {
                serialNumber = backup.SerialNumber;
                label = backup.Label;
                originalThingId = backup.OriginalThingId;
                manufactureTick = backup.ManufactureTick;
                joinPlayerTick = backup.JoinPlayerTick;
                isDuplicate = backup.IsDuplicate;
                originSerialNumber = backup.OriginSerialNumber;
                objectState = MaidObjectState.Unknown;
                lastKnownLocation = "LegacyBackup";
                SyncBackupState(backup);
            }

            public void UpdateIdentity(Pawn pawn, CompArtificialMaid comp)
            {
                if (pawn == null)
                {
                    return;
                }

                serialNumber = comp?.serialNumber ?? serialNumber;
                label = pawn.LabelShort;
                originalThingId = pawn.thingIDNumber;
                manufactureTick = comp?.manufactureTick ?? manufactureTick;
                joinPlayerTick = comp?.joinPlayerTick ?? joinPlayerTick;
                isDuplicate = comp != null && comp.isDuplicate;
                originSerialNumber = comp?.originSerialNumber;
                lastKnownPawn = pawn;
            }

            public void SetLastKnownPawn(Pawn pawn)
            {
                lastKnownPawn = pawn;
                if (pawn != null)
                {
                    label = pawn.LabelShort;
                    originalThingId = pawn.thingIDNumber;
                }
            }

            public void SetObjectState(MaidObjectState state, string location, int tick, bool seen = true)
            {
                objectState = state;
                lastKnownLocation = location;
                if (seen)
                {
                    lastSeenTick = tick;
                }
            }

            public void ScheduleNextBackup(int tick)
            {
                nextBackupTick = tick;
            }

            public void BeginBackupAttempt(int tick)
            {
                lastBackupAttemptTick = tick;
                backupState = MaidBackupState.Capturing;
                lastBackupStage = "Capture";
                lastBackupError = null;
            }

            public void FailBackup(string stage, string error, int retryTick)
            {
                backupState = lastSuccessfulBackupTick >= 0
                    ? MaidBackupState.Stale
                    : MaidBackupState.Failed;
                lastBackupStage = stage;
                lastBackupError = error;
                nextBackupTick = retryTick;
            }

            public void CompleteBackup(BackupRecord backup, int nextTick)
            {
                lastSuccessfulBackupTick = backup.LastBackupTick;
                backupIsPartial = backup.IsPartial;
                backupState = backup.IsPartial ? MaidBackupState.ValidPartial : MaidBackupState.Valid;
                lastBackupStage = backup.IsPartial ? "CoreSnapshot" : "FullSnapshot";
                lastBackupError = backup.IsPartial ? backup.FullSnapshotError : null;
                nextBackupTick = nextTick;
            }

            public void SyncBackupState(BackupRecord backup)
            {
                if (backup == null || !backup.IsUsable)
                {
                    backupState = MaidBackupState.Corrupted;
                    backupIsPartial = false;
                    return;
                }

                lastSuccessfulBackupTick = backup.LastBackupTick;
                backupIsPartial = backup.IsPartial;
                backupState = backup.IsPartial ? MaidBackupState.ValidPartial : MaidBackupState.Valid;
                nextBackupTick = Math.Max(Find.TickManager.TicksGame + InitialBackupDelayTicks,
                    backup.LastBackupTick + BackupIntervalTicks);
            }

            public void ExposeData()
            {
                Scribe_Values.Look(ref serialNumber, "serialNumber");
                Scribe_Values.Look(ref label, "label");
                Scribe_Values.Look(ref originalThingId, "originalThingId", -1);
                Scribe_Values.Look(ref manufactureTick, "manufactureTick", -1);
                Scribe_Values.Look(ref joinPlayerTick, "joinPlayerTick", -1);
                Scribe_Values.Look(ref isDuplicate, "isDuplicate", false);
                Scribe_Values.Look(ref originSerialNumber, "originSerialNumber");
                Scribe_References.Look(ref lastKnownPawn, "lastKnownPawn", true);
                Scribe_Values.Look(ref lastSeenTick, "lastSeenTick", -1);
                Scribe_Values.Look(ref objectState, "objectState", MaidObjectState.Unknown);
                Scribe_Values.Look(ref lastKnownLocation, "lastKnownLocation");
                Scribe_Values.Look(ref backupState, "backupState", MaidBackupState.NeverAttempted);
                Scribe_Values.Look(ref lastBackupAttemptTick, "lastBackupAttemptTick", -1);
                Scribe_Values.Look(ref lastSuccessfulBackupTick, "lastSuccessfulBackupTick", -1);
                Scribe_Values.Look(ref nextBackupTick, "nextBackupTick", -1);
                Scribe_Values.Look(ref lastBackupStage, "lastBackupStage");
                Scribe_Values.Look(ref lastBackupError, "lastBackupError");
                Scribe_Values.Look(ref backupIsPartial, "backupIsPartial", false);
            }
        }

        public class MaidCoreSnapshot : IExposable
        {
            private string serialNumber;
            private string firstName;
            private string nickName;
            private string lastName;
            private Gender gender;
            private BackstoryDef childhood;
            private BackstoryDef adulthood;
            private List<TraitBackup> traits = new List<TraitBackup>();
            private List<SkillBackup> skills = new List<SkillBackup>();
            private int manufactureTick = -1;
            private int joinPlayerTick = -1;
            private bool isDuplicate;
            private int originPawnId = -1;
            private string originSerialNumber;
            private bool allowAutoHibernate = true;
            private bool enableHealingProtocol;
            private bool enableHuntMode;

            public static MaidCoreSnapshot FromPawn(Pawn pawn, CompArtificialMaid comp)
            {
                MaidCoreSnapshot snapshot = new MaidCoreSnapshot
                {
                    serialNumber = comp.serialNumber,
                    gender = pawn.gender,
                    childhood = pawn.story?.Childhood,
                    adulthood = pawn.story?.Adulthood,
                    manufactureTick = comp.manufactureTick,
                    joinPlayerTick = comp.joinPlayerTick,
                    isDuplicate = comp.isDuplicate,
                    originPawnId = comp.originPawnId,
                    originSerialNumber = comp.originSerialNumber,
                    allowAutoHibernate = comp.allowAutoHibernate,
                    enableHealingProtocol = comp.enableHealingProtocol,
                    enableHuntMode = comp.enableHuntMode
                };

                if (pawn.Name is NameTriple triple)
                {
                    snapshot.firstName = triple.First;
                    snapshot.nickName = triple.Nick;
                    snapshot.lastName = triple.Last;
                }
                else
                {
                    snapshot.nickName = pawn.LabelShort;
                }

                if (pawn.story?.traits?.allTraits != null)
                {
                    List<Trait> sourceTraits = pawn.story.traits.allTraits;
                    for (int i = 0; i < sourceTraits.Count; i++)
                    {
                        snapshot.traits.Add(new TraitBackup(sourceTraits[i]));
                    }
                }

                if (pawn.skills?.skills != null)
                {
                    List<SkillRecord> sourceSkills = pawn.skills.skills;
                    for (int i = 0; i < sourceSkills.Count; i++)
                    {
                        snapshot.skills.Add(new SkillBackup(sourceSkills[i]));
                    }
                }

                return snapshot;
            }

            public Pawn CreatePawn()
            {
                PawnKindDef kind = PawnKindDef.Named("ArtificialMaidKind");
                PawnGenerationRequest request = new PawnGenerationRequest(
                    kind,
                    Faction.OfPlayer,
                    PawnGenerationContext.NonPlayer,
                    forceGenerateNewPawn: true,
                    allowDead: false,
                    allowDowned: false,
                    canGeneratePawnRelations: false,
                    allowGay: false,
                    allowPregnant: false,
                    allowFood: false,
                    allowAddictions: false,
                    fixedGender: gender,
                    forceNoGear: true);
                Pawn pawn = PawnGenerator.GeneratePawn(request);
                pawn.gender = gender;

                if (!string.IsNullOrEmpty(firstName) || !string.IsNullOrEmpty(lastName))
                {
                    pawn.Name = new NameTriple(firstName ?? string.Empty, nickName, lastName ?? string.Empty);
                }
                else if (!string.IsNullOrEmpty(nickName))
                {
                    pawn.Name = new NameSingle(nickName);
                }

                if (pawn.story != null)
                {
                    if (childhood != null) pawn.story.Childhood = childhood;
                    if (adulthood != null) pawn.story.Adulthood = adulthood;
                    if (pawn.story.traits != null)
                    {
                        for (int i = pawn.story.traits.allTraits.Count - 1; i >= 0; i--)
                        {
                            pawn.story.traits.RemoveTrait(pawn.story.traits.allTraits[i]);
                        }

                        for (int i = 0; i < traits.Count; i++)
                        {
                            traits[i]?.ApplyTo(pawn);
                        }
                    }
                }

                for (int i = 0; i < skills.Count; i++)
                {
                    skills[i]?.ApplyTo(pawn);
                }

                CompArtificialMaid comp = CompArtificialMaid.GetCompCached(pawn);
                if (comp != null)
                {
                    comp.serialNumber = serialNumber;
                    comp.manufactureTick = manufactureTick;
                    comp.joinPlayerTick = joinPlayerTick;
                    comp.isDuplicate = isDuplicate;
                    comp.originPawnId = originPawnId;
                    comp.originSerialNumber = originSerialNumber;
                    comp.allowAutoHibernate = allowAutoHibernate;
                    comp.enableHealingProtocol = enableHealingProtocol;
                    comp.enableHuntMode = enableHuntMode;
                }

                return pawn;
            }

            public void ExposeData()
            {
                Scribe_Values.Look(ref serialNumber, "serialNumber");
                Scribe_Values.Look(ref firstName, "firstName");
                Scribe_Values.Look(ref nickName, "nickName");
                Scribe_Values.Look(ref lastName, "lastName");
                Scribe_Values.Look(ref gender, "gender", Gender.Female);
                Scribe_Defs.Look(ref childhood, "childhood");
                Scribe_Defs.Look(ref adulthood, "adulthood");
                Scribe_Collections.Look(ref traits, "traits", LookMode.Deep);
                Scribe_Collections.Look(ref skills, "skills", LookMode.Deep);
                Scribe_Values.Look(ref manufactureTick, "manufactureTick", -1);
                Scribe_Values.Look(ref joinPlayerTick, "joinPlayerTick", -1);
                Scribe_Values.Look(ref isDuplicate, "isDuplicate", false);
                Scribe_Values.Look(ref originPawnId, "originPawnId", -1);
                Scribe_Values.Look(ref originSerialNumber, "originSerialNumber");
                Scribe_Values.Look(ref allowAutoHibernate, "allowAutoHibernate", true);
                Scribe_Values.Look(ref enableHealingProtocol, "enableHealingProtocol", false);
                Scribe_Values.Look(ref enableHuntMode, "enableHuntMode", false);

                if (Scribe.mode == LoadSaveMode.PostLoadInit)
                {
                    if (traits == null) traits = new List<TraitBackup>();
                    if (skills == null) skills = new List<SkillBackup>();
                }
            }
        }

        public class TraitBackup : IExposable
        {
            private TraitDef def;
            private int degree;

            public TraitBackup()
            {
            }

            public TraitBackup(Trait trait)
            {
                def = trait.def;
                degree = trait.Degree;
            }

            public void ApplyTo(Pawn pawn)
            {
                if (def != null && pawn.story?.traits != null && !pawn.story.traits.HasTrait(def))
                {
                    pawn.story.traits.GainTrait(new Trait(def, degree));
                }
            }

            public void ExposeData()
            {
                Scribe_Defs.Look(ref def, "def");
                Scribe_Values.Look(ref degree, "degree", 0);
            }
        }

        public class SkillBackup : IExposable
        {
            private SkillDef def;
            private int level;
            private Passion passion;

            public SkillBackup()
            {
            }

            public SkillBackup(SkillRecord skill)
            {
                def = skill.def;
                level = skill.Level;
                passion = skill.passion;
            }

            public void ApplyTo(Pawn pawn)
            {
                SkillRecord skill = def != null ? pawn.skills?.GetSkill(def) : null;
                if (skill != null)
                {
                    skill.Level = level;
                    skill.passion = passion;
                }
            }

            public void ExposeData()
            {
                Scribe_Defs.Look(ref def, "def");
                Scribe_Values.Look(ref level, "level", 0);
                Scribe_Values.Look(ref passion, "passion", Passion.None);
            }
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

            // 不依赖第三方 Comp 的核心快照；完整 Pawn 克隆失败时仍可重建女仆。
            private MaidCoreSnapshot coreSnapshot;

            // 单独保存装备缓存，确保 Destroy 后从快照重建时可以显式装回武器、服装和背包物品。
            private GearBackup gearBackup;

            private string fullSnapshotError;
            private string gearSnapshotError;

            // 对原 Pawn 的弱意义引用：能解析时用于诊断，不能解析时不影响从 snapshot 恢复。
            private Pawn lastKnownPawn;

            public string SerialNumber => serialNumber;
            public string Label => label;
            public int LastBackupTick => lastBackupTick;
            public Pawn Snapshot => snapshot;
            public MaidCoreSnapshot CoreSnapshot => coreSnapshot;
            public bool IsUsable => snapshot != null || coreSnapshot != null;
            public bool IsPartial => snapshot == null && coreSnapshot != null;
            public string FullSnapshotError => fullSnapshotError;
            public int OriginalThingId => originalThingId;
            public int ManufactureTick => manufactureTick;
            public int JoinPlayerTick => joinPlayerTick;
            public bool IsDuplicate => isDuplicate;
            public string OriginSerialNumber => originSerialNumber;

            public void UpdateFrom(Pawn source, Pawn newSnapshot, MaidCoreSnapshot newCoreSnapshot,
                GearBackup newGearBackup, string newSerialNumber, CompArtificialMaid comp,
                string newFullSnapshotError, string newGearSnapshotError)
            {
                serialNumber = newSerialNumber;
                label = source.LabelShort;
                lastBackupTick = Find.TickManager.TicksGame;
                originalThingId = source.thingIDNumber;
                manufactureTick = comp != null ? comp.manufactureTick : -1;
                joinPlayerTick = comp != null ? comp.joinPlayerTick : -1;
                isDuplicate = comp != null && comp.isDuplicate;
                originSerialNumber = comp?.originSerialNumber;
                snapshot = newSnapshot;
                coreSnapshot = newCoreSnapshot;
                gearBackup = newGearBackup;
                fullSnapshotError = newFullSnapshotError;
                gearSnapshotError = newGearSnapshotError;
                lastKnownPawn = source;
            }

            public Pawn CreateRestoredPawn()
            {
                Pawn restored = snapshot != null ? ClonePawn(snapshot) : null;
                if (restored == null && coreSnapshot != null)
                {
                    restored = coreSnapshot.CreatePawn();
                }

                return restored;
            }

            public void RestoreGearTo(Pawn pawn)
            {
                // 兼容旧存档：旧记录没有 gearBackup 时，尝试从完整 Pawn 快照中补建装备缓存。
                if (gearBackup == null && snapshot != null)
                {
                    gearBackup = GearBackup.FromPawn(snapshot);
                }

                gearBackup?.RestoreTo(pawn);
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
                Scribe_Values.Look(ref fullSnapshotError, "fullSnapshotError");
                Scribe_Values.Look(ref gearSnapshotError, "gearSnapshotError");

                // 快照必须深度保存，否则 Destroy 后无法重建 Pawn。
                Scribe_Deep.Look(ref snapshot, "snapshot");
                Scribe_Deep.Look(ref coreSnapshot, "coreSnapshot");

                // 装备缓存也深度保存。旧存档没有该节点时，仍可退回使用 snapshot 中的装备数据。
                Scribe_Deep.Look(ref gearBackup, "gearBackup");

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

        public class GearBackup : IExposable
        {
            private List<ThingWithComps> equipment = new List<ThingWithComps>();
            private List<Apparel> apparel = new List<Apparel>();
            private List<bool> apparelLocked = new List<bool>();
            private List<Thing> inventory = new List<Thing>();

            public static GearBackup FromPawn(Pawn pawn)
            {
                GearBackup backup = new GearBackup();
                backup.CaptureFrom(pawn);
                return backup;
            }

            public void ExposeData()
            {
                Scribe_Collections.Look(ref equipment, "equipment", LookMode.Deep);
                Scribe_Collections.Look(ref apparel, "apparel", LookMode.Deep);
                Scribe_Collections.Look(ref apparelLocked, "apparelLocked", LookMode.Value);
                Scribe_Collections.Look(ref inventory, "inventory", LookMode.Deep);

                if (Scribe.mode == LoadSaveMode.PostLoadInit)
                {
                    if (equipment == null) equipment = new List<ThingWithComps>();
                    if (apparel == null) apparel = new List<Apparel>();
                    if (apparelLocked == null) apparelLocked = new List<bool>();
                    if (inventory == null) inventory = new List<Thing>();

                    equipment.RemoveAll(thing => thing == null || thing.Destroyed);
                    apparel.RemoveAll(thing => thing == null || thing.Destroyed);
                    inventory.RemoveAll(thing => thing == null || thing.Destroyed);
                }
            }

            private void CaptureFrom(Pawn pawn)
            {
                equipment.Clear();
                apparel.Clear();
                apparelLocked.Clear();
                inventory.Clear();

                if (pawn == null)
                {
                    return;
                }

                // 武器装备：使用 AddEquipment 恢复，因此这里保存 ThingWithComps。
                List<ThingWithComps> sourceEquipment = pawn.equipment?.AllEquipmentListForReading;
                if (sourceEquipment != null)
                {
                    for (int i = 0; i < sourceEquipment.Count; i++)
                    {
                        ThingWithComps cloned = CloneThing(sourceEquipment[i]);
                        if (cloned != null)
                        {
                            equipment.Add(cloned);
                        }
                    }
                }

                // 穿戴服装：额外保存锁定状态，恢复时调用 Wear(..., locked) 还原锁定。
                List<Apparel> sourceApparel = pawn.apparel?.WornApparel;
                if (sourceApparel != null)
                {
                    for (int i = 0; i < sourceApparel.Count; i++)
                    {
                        Apparel source = sourceApparel[i];
                        Apparel cloned = CloneThing(source);
                        if (cloned != null)
                        {
                            apparel.Add(cloned);
                            apparelLocked.Add(pawn.apparel.IsLocked(source));
                        }
                    }
                }

                // 背包库存：直接保存库存容器内的物品。
                ThingOwner sourceInventory = pawn.inventory?.GetDirectlyHeldThings();
                if (sourceInventory != null)
                {
                    for (int i = 0; i < sourceInventory.Count; i++)
                    {
                        Thing cloned = CloneThing(sourceInventory[i]);
                        if (cloned != null)
                        {
                            inventory.Add(cloned);
                        }
                    }
                }
            }

            public void RestoreTo(Pawn pawn)
            {
                if (pawn == null)
                {
                    return;
                }

                PawnComponentsUtility.CreateInitialComponents(pawn);
                PawnComponentsUtility.AddAndRemoveDynamicComponents(pawn, true);

                // 先清理恢复 Pawn 当前容器，避免快照自带的残缺装备和 GearBackup 重复叠加。
                pawn.equipment?.DestroyAllEquipment();
                pawn.apparel?.DestroyAll(DestroyMode.Vanish);
                pawn.inventory?.GetDirectlyHeldThings()?.ClearAndDestroyContents();

                RestoreEquipment(pawn);
                RestoreApparel(pawn);
                RestoreInventory(pawn);

                pawn.Drawer?.renderer?.SetAllGraphicsDirty();
            }

            private void RestoreEquipment(Pawn pawn)
            {
                if (pawn.equipment == null || equipment == null)
                {
                    return;
                }

                for (int i = 0; i < equipment.Count; i++)
                {
                    ThingWithComps cloned = CloneThing(equipment[i]);
                    if (cloned != null && !cloned.Destroyed)
                    {
                        pawn.equipment.AddEquipment(cloned);
                    }
                }
            }

            private void RestoreApparel(Pawn pawn)
            {
                if (pawn.apparel == null || apparel == null)
                {
                    return;
                }

                for (int i = 0; i < apparel.Count; i++)
                {
                    Apparel cloned = CloneThing(apparel[i]);
                    if (cloned != null && !cloned.Destroyed)
                    {
                        bool locked = i < apparelLocked.Count && apparelLocked[i];
                        pawn.apparel.Wear(cloned, false, locked);
                    }
                }
            }

            private void RestoreInventory(Pawn pawn)
            {
                ThingOwner targetInventory = pawn.inventory?.GetDirectlyHeldThings();
                if (targetInventory == null || inventory == null)
                {
                    return;
                }

                for (int i = 0; i < inventory.Count; i++)
                {
                    Thing cloned = CloneThing(inventory[i]);
                    if (cloned != null && !cloned.Destroyed)
                    {
                        targetInventory.TryAdd(cloned, true);
                    }
                }
            }
        }
    }
}
