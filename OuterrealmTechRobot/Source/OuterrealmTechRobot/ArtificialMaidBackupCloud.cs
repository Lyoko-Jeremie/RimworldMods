using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using Verse;
using Verse.AI;

namespace OuterrealmTechRobot
{
    /// <summary>
    /// Stores periodic deep snapshots of Artificial Maids so a lost pawn can be rebuilt by serial number.
    /// </summary>
    public class ArtificialMaidBackupCloud : GameComponent
    {
        private const int BackupIntervalTicks = 60000;

        private List<BackupRecord> backups = new List<BackupRecord>();
        private Dictionary<string, BackupRecord> backupsBySerial = new Dictionary<string, BackupRecord>();

        private static readonly List<Thing> tmpHeldThings = new List<Thing>();

        public ArtificialMaidBackupCloud(Game game)
        {
        }

        public static ArtificialMaidBackupCloud Current
        {
            get
            {
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
                if (backups == null)
                {
                    backups = new List<BackupRecord>();
                }

                backups.RemoveAll(record => record == null || string.IsNullOrEmpty(record.SerialNumber) || record.Snapshot == null);
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
            if (Find.TickManager.TicksGame % BackupIntervalTicks != 0)
            {
                return;
            }

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
            Current?.BackupPawn(pawn);
        }

        public static void NotifyMaidDestroyed(Pawn pawn)
        {
            Current?.BackupPawn(pawn);
        }

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
                reason = "ArtificialMaidBackupCloudStillActive".Translate(existingPawn.LabelShort);
                return false;
            }

            return true;
        }

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
                if (!existingPawn.Dead && !existingPawn.Destroyed)
                {
                    return false;
                }

                restoredPawn = existingPawn;
                if (existingPawn.Dead)
                {
                    if (!ResurrectionUtility.TryResurrect(existingPawn, ResurrectionParms))
                    {
                        return false;
                    }
                }

                FinalizeRestoredPawn(restoredPawn, map, position);
                cloud.BackupPawn(restoredPawn);
                return true;
            }

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
            if (!IsArtificialMaid(pawn) || Scribe.mode != LoadSaveMode.Inactive)
            {
                return false;
            }

            CompArtificialMaid comp = CompArtificialMaid.GetCompCached(pawn);
            string serialNumber = comp?.serialNumber;
            if (string.IsNullOrEmpty(serialNumber))
            {
                return false;
            }

            Pawn snapshot = ClonePawn(pawn);
            if (snapshot == null)
            {
                return false;
            }

            if (!backupsBySerial.TryGetValue(serialNumber, out BackupRecord record))
            {
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

            string filePath = Path.Combine(GenFilePaths.TempFolderPath, "OuterrealmTechRobot_ArtificialMaidBackup_" + Guid.NewGuid().ToString("N") + ".xml");
            Pawn pawnToSave = source;
            Pawn clone = null;

            try
            {
                Scribe.saver.InitSaving(filePath, "artificialMaidBackup");
                Scribe.saver.savingForDebug = true;
                Scribe_Deep.Look(ref pawnToSave, "pawn");
                Scribe.saver.FinalizeSaving();

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
            pawn.ForceSetStateToUnspawned();
            pawn.SetPositionDirect(IntVec3.Invalid);
            pawn.thingIDNumber = Find.UniqueIDsManager.GetNextThingID();

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

            PawnComponentsUtility.CreateInitialComponents(pawn);
            PawnComponentsUtility.AddAndRemoveDynamicComponents(pawn, true);
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
                return;
            }

            if (pawn.Faction != Faction.OfPlayer)
            {
                pawn.SetFaction(Faction.OfPlayer);
            }

            if (pawn.Dead)
            {
                ResurrectionUtility.TryResurrect(pawn, ResurrectionParms);
            }

            PawnComponentsUtility.CreateInitialComponents(pawn);
            PawnComponentsUtility.AddAndRemoveDynamicComponents(pawn, true);

            IntVec3 spawnCell = position.IsValid ? position : map.Center;
            if (!CellFinder.TryFindRandomSpawnCellForPawnNear(spawnCell, map, out spawnCell))
            {
                spawnCell = map.Center;
            }

            if (!pawn.Spawned)
            {
                pawn.ForceSetStateToUnspawned();
                GenSpawn.Spawn(pawn, spawnCell, map);
            }
            else if (pawn.Map == map)
            {
                pawn.Position = spawnCell;
            }
            else
            {
                pawn.DeSpawnOrDeselect();
                pawn.ForceSetStateToUnspawned();
                GenSpawn.Spawn(pawn, spawnCell, map);
            }

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
            if (backupsBySerial == null || backupsBySerial.Count != backups.Count)
            {
                RebuildIndex();
            }

            return backupsBySerial.TryGetValue(serialNumber, out record);
        }

        private void RebuildIndex()
        {
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
                    backups.RemoveAt(i);
                    continue;
                }

                backupsBySerial[record.SerialNumber] = record;
            }
        }

        private static Pawn FindExistingMaidBySerial(string serialNumber)
        {
            if (string.IsNullOrEmpty(serialNumber))
            {
                return null;
            }

            List<Pawn> pawns = PawnsFinder.All_AliveOrDead;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (PawnHasSerial(pawn, serialNumber))
                {
                    return pawn;
                }
            }

            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
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
            private string serialNumber;
            private string label;
            private int lastBackupTick = -1;
            private int originalThingId = -1;
            private int manufactureTick = -1;
            private int joinPlayerTick = -1;
            private bool isDuplicate;
            private string originSerialNumber;
            private Pawn snapshot;
            private Pawn lastKnownPawn;

            public string SerialNumber => serialNumber;
            public string Label => label;
            public int LastBackupTick => lastBackupTick;
            public Pawn Snapshot => snapshot;

            public void UpdateFrom(Pawn source, Pawn newSnapshot, string newSerialNumber, CompArtificialMaid comp)
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
                lastKnownPawn = source;
            }

            public void ExposeData()
            {
                Scribe_Values.Look(ref serialNumber, "serialNumber");
                Scribe_Values.Look(ref label, "label");
                Scribe_Values.Look(ref lastBackupTick, "lastBackupTick", -1);
                Scribe_Values.Look(ref originalThingId, "originalThingId", -1);
                Scribe_Values.Look(ref manufactureTick, "manufactureTick", -1);
                Scribe_Values.Look(ref joinPlayerTick, "joinPlayerTick", -1);
                Scribe_Values.Look(ref isDuplicate, "isDuplicate", false);
                Scribe_Values.Look(ref originSerialNumber, "originSerialNumber");
                Scribe_Deep.Look(ref snapshot, "snapshot");
                Scribe_References.Look(ref lastKnownPawn, "lastKnownPawn", true);
            }

            public string FloatMenuLabel
            {
                get
                {
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
