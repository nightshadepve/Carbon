using System;
using System.Collections.Generic;
#if !NIGHTSHADE_LOOT_TESTS
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries.Covalence;
using UnityEngine;
#endif

namespace Carbon.Plugins
{
    // Open and wipe decisions that do not need the Rust host.
    public static class NightshadeLootRules
    {
        public const ulong SteamIdMin = 76561197960265728UL;

        public static bool IsPlayerSteamId(ulong id)
        {
            return id >= SteamIdMin;
        }

        public struct OpenRequest
        {
            public bool Admin;
            public bool OwnerOrTeammate;
            public bool ExplicitShare;
            public bool PlayerPlacedStorage;
            public bool StorageEnabled;
            public bool WeekendOrPurge;
            public bool OpenPlayerStorageOnWeekend;
            public bool RaidProtectedAgainstOutsiders;
            public bool FrontUse;
            public bool ExemptZone;
        }

        // True means this plugin does not block the open. Code locks are not decided here.
        public static bool AllowPlayerPlacedOpen(OpenRequest request)
        {
            if (!request.PlayerPlacedStorage) return true;
            if (request.ExemptZone || request.Admin || request.FrontUse) return true;
            if (!request.StorageEnabled) return true;
            if (request.OwnerOrTeammate || request.ExplicitShare) return true;
            if (request.OpenPlayerStorageOnWeekend && request.WeekendOrPurge && !request.RaidProtectedAgainstOutsiders)
                return true;
            return false;
        }

        public static DateTime AsUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Local) return value.ToUniversalTime();
            if (value.Kind == DateTimeKind.Utc) return value;
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        public static bool SaveClockKnown(DateTime saveCreatedUtc)
        {
            return saveCreatedUtc.Year >= 2000;
        }

        // Legacy share and name files are imported only when they belong to this map save.
        // A file last written before the save was created is a previous wipe and stays out.
        public static bool ShouldImportLegacy(bool newDataPresent, bool legacyPresent, bool settled, bool saveClockKnown, DateTime legacyWriteUtc, DateTime saveCreated)
        {
            if (settled || newDataPresent || !legacyPresent || !saveClockKnown) return false;
            DateTime created = AsUtc(saveCreated);
            if (!SaveClockKnown(created)) return false;
            return AsUtc(legacyWriteUtc) >= created;
        }
    }

#if !NIGHTSHADE_LOOT_TESTS
    [Info("NightshadeLootProtection", "Nightshade", "1.3.2")]
    [Description("Team-shared player containers. Everyone else has to destroy the container. Raid protection still blocks that damage.")]
    public class NightshadeLootProtection : CarbonPlugin
    {
        #region Fields

        // entity net ID -> ownership data
        private readonly Dictionary<ulong, OwnershipData> _entityOwnership = new();

        // set of prefab short names that are always publicly accessible
        private static readonly HashSet<string> PublicEntities = new(StringComparer.OrdinalIgnoreCase)
        {
            "mailbox.deployed", "repairbench_deployed",
            "workbench1", "workbench2", "workbench3", "iotable",
            "research.table", "researchtable_deployed", "mixingtable.deployed",
            "electric.switch", "sam_site_turret_deployed", "autoturret_deployed",
            "computerstation", "vendingmachine.deployed"
        };

        private PluginConfig _config;

        // entityNetId -> set of steamIds who have been granted access to that specific entity
        private Dictionary<ulong, HashSet<ulong>> _shareData = new();

        // Raid-base zones registered by NightshadeRaidbase. Not saved. Re-registered when a raid starts.
        private readonly Dictionary<string, ExemptZone> _exemptZones = new(StringComparer.OrdinalIgnoreCase);

        private bool _dataReady;

        // item UID -> thrower steamId  -  tracks in-flight supply signals (fixes race condition)
        private readonly Dictionary<ulong, ulong> _pendingSupplyOwners = new();

        // net IDs of CH47s currently carrying a Bradley drop  -  immune to damage until cargo drops
        private readonly HashSet<ulong> _immuneCh47s = new();

        // Tracks NPC death positions so we can match ownership to the spawned LootableCorpse
        // position -> OwnershipData, written in OnEntityDeath, consumed in OnEntitySpawned(LootableCorpse)
        private readonly Dictionary<Vector3, OwnershipData> _npcDeathPositions = new();

        // steamId -> last known display name for offline share lookups
        private Dictionary<ulong, string> _knownPlayers = new();

        // Auto turret net id -> steam ids this plugin added from the covering TC.
        // Manual turret authorization is not stored here, so a TC deauth does not remove it.
        private readonly Dictionary<ulong, List<ulong>> _turretSync = new Dictionary<ulong, List<ulong>>();
        private bool _turretSyncDirty;

        private const string PermAdmin = "nightshadelootprotection.admin";
        private const string PermAdminLegacy = "unifiedlootprotection.admin";
        private const string DataDir = "Nightshade/NightshadeLootProtection";
        private const string SharesName = "shares";
        private const string KnownName = "knownplayers";
        private const string TurretName = "tcturretauth";
        private const string WipeName = "wipe";

        [PluginReference] private Plugin RaidableBases, TruePVE, NightshadePVPWeekend, NightshadeDeathBox;

        // plugins.Find avoids a missing-plugin warning after the rename. The old name still answers if it is loaded.
        private Plugin WeekendPlugin()
        {
            if (NightshadePVPWeekend != null && NightshadePVPWeekend.IsLoaded) return NightshadePVPWeekend;
            Plugin legacy = plugins.Find("NightshadeRaidProtection");
            if (legacy != null && legacy.IsLoaded) return legacy;
            return null;
        }
        [PluginReference("Nightshade Purge Status")] private Plugin PurgeStatus;

        #endregion

        #region Config

        private class PluginConfig
        {
            [JsonProperty("Bradley")]
            public BradleyConfig Bradley { get; set; } = new();

            [JsonProperty("Helicopter")]
            public HeliConfig Helicopter { get; set; } = new();

            [JsonProperty("HackableCrate")]
            public HackableCrateConfig HackableCrate { get; set; } = new();

            [JsonProperty("NPC")]
            public NpcConfig NPC { get; set; } = new();

            [JsonProperty("SupplyDrop")]
            public SupplyDropConfig SupplyDrop { get; set; } = new();

            [JsonProperty("StorageProtection")]
            public StorageConfig StorageProtection { get; set; } = new();

            [JsonProperty("CorpseProtection")]
            public CorpseConfig CorpseProtection { get; set; } = new();

            [JsonProperty("WeaponRackProtection")]
            public WeaponRackConfig WeaponRackProtection { get; set; } = new();

            [JsonProperty("ChatAlerts")]
            public ChatAlertsConfig ChatAlerts { get; set; } = new();

            [JsonProperty("WeekendPvP")]
            public WeekendPvPConfig WeekendPvP { get; set; } = new();

            [JsonProperty("TrapTeamSafety")]
            public TrapTeamSafetyConfig TrapTeamSafety { get; set; } = new();

            [JsonProperty("TurretTcAuth")]
            public TurretTcAuthConfig TurretTcAuth { get; set; } = new();

            // Off by default. The save-timer cleanup stays silent unless this is true.
            [JsonProperty("Log cleanup summary")]
            public bool LogCleanupSummary { get; set; } = false;
        }

        private class TrapTeamSafetyConfig
        {
            // Owner, that owner's Rust team, and players authorized on the covering TC.
            public bool Enabled { get; set; } = true;
        }

        private class TurretTcAuthConfig
        {
            // Players authorized on the TC that covers an auto turret are authorized on that turret.
            public bool Enabled { get; set; } = true;
        }

        private class WeekendPvPConfig
        {
            [JsonProperty("Disable NightshadeLootProtection access restrictions during PvP weekend")]
            public bool Enabled { get; set; } = true;

            [JsonProperty("Read TruePVE GetPlayerMapping for the active weekend profile")]
            public bool ReadTruePVEProfile { get; set; } = true;

            [JsonProperty("Weekend TruePVE ruleset name")]
            public string WeekendRuleSet { get; set; } = "nightshade_pvp_weekend";

            [JsonProperty("Use Friday 8 PM - Monday 3 AM Eastern clock fallback when TruePVE mapping is unavailable")]
            public bool EasternClockFallback { get; set; } = true;

            [JsonProperty("Disable Bradley, heli, NPC, hackable crate and supply drop ownership locks")]
            public bool DisableEventOwnershipLocks { get; set; } = true;

            [JsonProperty("Allow access during active Purge ruleset")]
            public bool AllowDuringPurge { get; set; } = true;

            [JsonProperty("TruePVE Purge ruleset name")]
            public string PurgeRuleSet { get; set; } = "purge";

            [JsonProperty("Keep TC authorization restricted during raiding")]
            public bool KeepTcAuthProtected { get; set; } = true;

            // Shel's rule. False keeps player-placed containers closed to non-teammates
            // on the weekend and during Purge. Event loot still follows the flags above.
            [JsonProperty("Open player-placed storage to non-teammates during PvP weekend and Purge")]
            public bool OpenPlayerStorageToNonTeammates { get; set; } = false;
        }

        private class ExemptZone
        {
            public Vector3 Center;
            public float Radius;
        }

        private class WipeState
        {
            public string WipeId;
            public bool LegacySettled;
        }

        private class BradleyConfig
        {
            public bool Enabled { get; set; } = true;
            public float DamageLockThreshold { get; set; } = 0.2f;
            public float TooHotSeconds { get; set; } = 180f;
            public int LockForSeconds { get; set; } = 300;
            public bool BroadcastKillToKiller { get; set; } = true;
            public float XPReward { get; set; } = 200f;
        }

        private class HeliConfig
        {
            public bool Enabled { get; set; } = true;
            public float DamageLockThreshold { get; set; } = 0.35f;
            public float TooHotSeconds { get; set; } = 480f;
            public int LockForSeconds { get; set; } = 480;
            public bool BroadcastKillToKiller { get; set; } = true;
            public bool BroadcastLockedToChat { get; set; } = true;
            public float XPReward { get; set; } = 500f;
            public float RustRewardsRP { get; set; } = 250f;
        }

        private class HackableCrateConfig
        {
            public bool Enabled { get; set; } = true;
            public int LockForSeconds { get; set; } = 900;
        }

        private class NpcConfig
        {
            public bool Enabled { get; set; } = true;
            public float DamageLockThreshold { get; set; } = 0.2f;
            public int LockForSeconds { get; set; } = 300;
            public bool BroadcastKillToKiller { get; set; } = true;
            public float RustRewardsRP { get; set; } = 25f;
        }

        private class SupplyDropConfig
        {
            public bool Enabled { get; set; } = true;
            public int LockForSeconds { get; set; } = 600;
        }

        private class StorageConfig
        {
            public bool Enabled { get; set; } = true;
            public bool UseTeams { get; set; } = true;
            public bool UseCupboard { get; set; } = true;
            public bool OnlyInCupboardRange { get; set; } = true;
        }

        private class CorpseConfig
        {
            public bool Enabled { get; set; } = true;
            public bool UseTeams { get; set; } = true;
            public bool AdminCanLoot { get; set; } = false;
        }

        private class WeaponRackConfig
        {
            public bool Enabled { get; set; } = true;
            public bool UseTeams { get; set; } = true;
        }

        private class ChatAlertsConfig
        {
            [JsonProperty("KillLockNotifyOwner")]
            public bool KillLockNotifyOwner { get; set; } = true;

            [JsonProperty("TooHot")]
            public bool TooHot { get; set; } = true;

            [JsonProperty("LootBlocked")]
            public bool LootBlocked { get; set; } = true;

            [JsonProperty("StorageBlocked")]
            public bool StorageBlocked { get; set; } = true;

            [JsonProperty("CorpseBlocked")]
            public bool CorpseBlocked { get; set; } = true;

            [JsonProperty("WeaponRackBlocked")]
            public bool WeaponRackBlocked { get; set; } = true;

            [JsonProperty("TCAuthBlocked")]
            public bool TCAuthBlocked { get; set; } = true;

            [JsonProperty("VendingAdminBlocked")]
            public bool VendingAdminBlocked { get; set; } = true;

            [JsonProperty("PickupBlocked")]
            public bool PickupBlocked { get; set; } = true;

            [JsonProperty("OvenToggleBlocked")]
            public bool OvenToggleBlocked { get; set; } = true;
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            bool copiedLegacy = TryCopyLegacyConfig();
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                    _config = new PluginConfig();
                if (_config.WeekendPvP == null)
                    _config.WeekendPvP = new WeekendPvPConfig();
                if (_config.TrapTeamSafety == null)
                    _config.TrapTeamSafety = new TrapTeamSafetyConfig();
                if (_config.TurretTcAuth == null)
                    _config.TurretTcAuth = new TurretTcAuthConfig();
                ApplyLegacyWeekendKey();
                // Rewrite once after a legacy copy so the new keys are on disk.
                // Later loads do not rewrite the config.
                if (copiedLegacy)
                    SaveConfig();
            }
            catch
            {
                LoadDefaultConfig();
            }
        }

        private bool TryCopyLegacyConfig()
        {
            try
            {
                string directory = Interface.Oxide.ConfigDirectory;
                string neu = Path.Combine(directory, "NightshadeLootProtection.json");
                string old = Path.Combine(directory, "UnifiedLootProtection.json");
                if (File.Exists(neu) || !File.Exists(old)) return false;
                File.Copy(old, neu);
                Puts("NightshadeLootProtection migrated config: copied UnifiedLootProtection.json to NightshadeLootProtection.json.");
                return true;
            }
            catch (Exception ex)
            {
                PrintWarning("NightshadeLootProtection could not copy UnifiedLootProtection.json: " + ex.Message);
                return false;
            }
        }

        private void ApplyLegacyWeekendKey()
        {
            if (_config == null || _config.WeekendPvP == null) return;
            try
            {
                string path = Path.Combine(Interface.Oxide.ConfigDirectory, "NightshadeLootProtection.json");
                if (!File.Exists(path)) return;
                JObject root = JObject.Parse(File.ReadAllText(path));
                JObject weekend = root["WeekendPvP"] as JObject;
                if (weekend == null) return;
                const string newEnabled = "Disable NightshadeLootProtection access restrictions during PvP weekend";
                const string oldEnabled = "Disable UnifiedLootProtection access restrictions during PvP weekend";
                const string openKey = "Open player-placed storage to non-teammates during PvP weekend and Purge";
                if (weekend[newEnabled] == null && weekend[oldEnabled] != null && weekend[oldEnabled].Type == JTokenType.Boolean)
                    _config.WeekendPvP.Enabled = (bool)weekend[oldEnabled];
                if (weekend[openKey] == null)
                    _config.WeekendPvP.OpenPlayerStorageToNonTeammates = false;
            }
            catch (Exception ex)
            {
                PrintWarning("NightshadeLootProtection could not read legacy weekend keys: " + ex.Message);
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Data Persistence

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            MigrateAdminPermission();
            LoadPersistentData(false);
            if (!_config.StorageProtection.Enabled)
                PrintWarning("StorageProtection.Enabled is FALSE. Player-placed storage will not be protected. Enable StorageProtection in NightshadeLootProtection.json.");
        }

        private void OnServerInitialized()
        {
            LoadPersistentData(true);
            try
            {
                if (SyncAllAutoTurrets())
                    SaveTurretSync();
            }
            catch (Exception ex)
            {
                PrintWarning("NightshadeLootProtection could not sync turret authorization: " + ex.Message);
            }
        }

        // Called when a player connects - keep known players list up to date
        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null || !_dataReady) return;
            _knownPlayers[player.userID] = player.displayName;
            SaveKnownPlayers();
        }

        private void MigrateAdminPermission()
        {
            int users = 0;
            int groups = 0;
            try
            {
                foreach (string user in permission.GetPermissionUsers(PermAdminLegacy) ?? new string[0])
                {
                    if (string.IsNullOrEmpty(user)) continue;
                    string id = user.Split('(')[0].Trim();
                    if (id.Length == 0 || permission.UserHasPermission(id, PermAdmin)) continue;
                    permission.GrantUserPermission(id, PermAdmin, this);
                    users++;
                }
                foreach (string group in permission.GetPermissionGroups(PermAdminLegacy) ?? new string[0])
                {
                    if (string.IsNullOrEmpty(group) || permission.GroupHasPermission(group, PermAdmin)) continue;
                    permission.GrantGroupPermission(group, PermAdmin, this);
                    groups++;
                }
            }
            catch (Exception ex)
            {
                PrintWarning("NightshadeLootProtection could not copy unifiedlootprotection.admin grants: " + ex.Message);
            }
            if (users > 0 || groups > 0)
                Puts("NightshadeLootProtection: copied legacy admin permission to " + users + " users, " + groups + " groups.");
        }

        private bool HasAdminPerm(BasePlayer player)
        {
            if (player == null) return false;
            string id = player.UserIDString;
            return permission.UserHasPermission(id, PermAdmin) || permission.UserHasPermission(id, PermAdminLegacy);
        }

        private static string DataFile(string name)
        {
            return DataDir + "/" + name;
        }

        private string DataPath(string name)
        {
            return Path.Combine(Interface.Oxide.DataDirectory, "Nightshade", "NightshadeLootProtection", name + ".json");
        }

        private static string LegacyDataPath(string fileName)
        {
            return Path.Combine(Interface.Oxide.DataDirectory, fileName);
        }

        private bool TryReadWipe(out string wipeId, out DateTime saveCreatedUtc)
        {
            wipeId = null;
            saveCreatedUtc = DateTime.MinValue;
            try
            {
                string save = World.SaveFileName;
                if (string.IsNullOrEmpty(save)) return false;
                DateTime created = SaveRestore.SaveCreatedTime;
                saveCreatedUtc = NightshadeLootRules.AsUtc(created);
                if (!NightshadeLootRules.SaveClockKnown(saveCreatedUtc)) return false;
                wipeId = save + "|" + World.Seed + "|" + saveCreatedUtc.Ticks;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private WipeState ReadWipeState()
        {
            try
            {
                if (!File.Exists(DataPath(WipeName))) return null;
                return Interface.Oxide.DataFileSystem.ReadObject<WipeState>(DataFile(WipeName));
            }
            catch
            {
                return null;
            }
        }

        private void WriteWipeState(WipeState state)
        {
            Interface.Oxide.DataFileSystem.WriteObject(DataFile(WipeName), state);
        }

        private void LoadPersistentData(bool allowLegacy)
        {
            bool haveWipe = TryReadWipe(out string wipeId, out DateTime created);
            WipeState state = ReadWipeState();
            if (haveWipe && state != null && !string.IsNullOrEmpty(state.WipeId) && !string.Equals(state.WipeId, wipeId, StringComparison.Ordinal))
            {
                string previous = state.WipeId;
                ClearPerWipeData("load");
                WriteWipeState(new WipeState { WipeId = wipeId, LegacySettled = true });
                _shareData = new Dictionary<ulong, HashSet<ulong>>();
                _knownPlayers = new Dictionary<ulong, string>();
                _dataReady = true;
                Puts("NightshadeLootProtection wipe: saved wipe id " + previous + " did not match " + wipeId + ". Cleared shares.json, knownplayers.json, and tcturretauth.json. Config and permission grants were kept. Legacy UnifiedLootProtection data was not imported.");
                return;
            }

            bool sharesPresent = File.Exists(DataPath(SharesName));
            bool knownPresent = File.Exists(DataPath(KnownName));
            if (sharesPresent || knownPresent)
            {
                ReadNewData();
                if (haveWipe && (state == null || string.IsNullOrEmpty(state.WipeId)))
                    WriteWipeState(new WipeState { WipeId = wipeId, LegacySettled = true });
                _dataReady = true;
                Puts("NightshadeLootProtection loaded share data: " + _shareData.Count + " entries. Known players: " + _knownPlayers.Count + ".");
                return;
            }

            if (!allowLegacy)
                return;

            bool settled = state != null && state.LegacySettled;
            ImportLegacyData(haveWipe, created, settled);
            if (haveWipe)
                WriteWipeState(new WipeState { WipeId = wipeId, LegacySettled = true });
            _dataReady = true;
        }

        private void ReadNewData()
        {
            if (File.Exists(DataPath(SharesName)))
            {
                var stored = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, HashSet<ulong>>>(DataFile(SharesName));
                if (stored != null) _shareData = stored;
            }
            if (File.Exists(DataPath(KnownName)))
            {
                var knownStored = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, string>>(DataFile(KnownName));
                if (knownStored != null) _knownPlayers = knownStored;
            }
            ReadTurretSync();
        }

        private void ImportLegacyData(bool haveWipe, DateTime saveCreatedUtc, bool settled)
        {
            string[] legacy =
            {
                "UnifiedLootProtection_shares.json",
                "UnifiedLootProtection_knownplayers.json",
                "UnifiedLootProtection_tcturretauth.json"
            };
            string[] names = { SharesName, KnownName, TurretName };
            bool anyLegacy = false;
            try { Directory.CreateDirectory(Path.GetDirectoryName(DataPath(SharesName))); }
            catch { /* The data write below reports a real failure. */ }
            for (int i = 0; i < legacy.Length; i++)
            {
                string path = LegacyDataPath(legacy[i]);
                if (!File.Exists(path))
                {
                    Puts("NightshadeLootProtection migration: " + legacy[i] + " was not found.");
                    continue;
                }
                anyLegacy = true;
                DateTime written = File.GetLastWriteTimeUtc(path);
                bool import = NightshadeLootRules.ShouldImportLegacy(false, true, settled, haveWipe, written, saveCreatedUtc);
                if (!import)
                {
                    string why = settled
                        ? "a previous load already settled legacy import"
                        : !haveWipe
                            ? "the map save creation time is not available, so old wipe data is not imported"
                            : "last write " + written.ToString("u") + " is before this map save " + saveCreatedUtc.ToString("u");
                    Puts("NightshadeLootProtection migration: skipped " + legacy[i] + " (" + why + ").");
                    continue;
                }
                if (names[i] == SharesName)
                {
                    var stored = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, HashSet<ulong>>>("UnifiedLootProtection_shares");
                    if (stored != null) _shareData = stored;
                    SaveShareData();
                    Puts("NightshadeLootProtection migration: imported " + _shareData.Count + " share entries from UnifiedLootProtection_shares.json for this map save.");
                }
                else if (names[i] == KnownName)
                {
                    var stored = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, string>>("UnifiedLootProtection_knownplayers");
                    if (stored != null) _knownPlayers = stored;
                    SaveKnownPlayers();
                    Puts("NightshadeLootProtection migration: imported " + _knownPlayers.Count + " known players from UnifiedLootProtection_knownplayers.json for this map save.");
                }
                else
                {
                    File.Copy(path, DataPath(TurretName), true);
                    Puts("NightshadeLootProtection migration: copied UnifiedLootProtection_tcturretauth.json to data/Nightshade/NightshadeLootProtection/tcturretauth.json.");
                }
            }
            if (!anyLegacy)
                Puts("NightshadeLootProtection migration: no UnifiedLootProtection data files were found.");
            ReadTurretSync();
        }

        private void ClearPerWipeData(string reason)
        {
            _shareData = new Dictionary<ulong, HashSet<ulong>>();
            _knownPlayers = new Dictionary<ulong, string>();
            _turretSync.Clear();
            _turretSyncDirty = false;
            _entityOwnership.Clear();
            _pendingSupplyOwners.Clear();
            _npcDeathPositions.Clear();
            _immuneCh47s.Clear();
            SaveShareData();
            SaveKnownPlayers();
            string turret = DataPath(TurretName);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(turret));
                File.WriteAllText(turret, "{}\n", new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                PrintWarning("NightshadeLootProtection could not clear tcturretauth.json: " + ex.Message);
            }
            Puts("NightshadeLootProtection wipe (" + reason + "): cleared shares.json, knownplayers.json, and tcturretauth.json. Config and permission grants were kept.");
        }

        private void SaveShareData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(DataFile(SharesName), _shareData);
        }

        private void SaveKnownPlayers()
        {
            Interface.Oxide.DataFileSystem.WriteObject(DataFile(KnownName), _knownPlayers);
        }

        private class TurretSyncFile
        {
            public Dictionary<ulong, List<ulong>> Turrets = new Dictionary<ulong, List<ulong>>();
        }

        private void ReadTurretSync()
        {
            _turretSync.Clear();
            _turretSyncDirty = false;
            try
            {
                if (!File.Exists(DataPath(TurretName))) return;
                TurretSyncFile stored = Interface.Oxide.DataFileSystem.ReadObject<TurretSyncFile>(DataFile(TurretName));
                if (stored == null || stored.Turrets == null) return;
                foreach (KeyValuePair<ulong, List<ulong>> pair in stored.Turrets)
                {
                    if (pair.Key == 0UL || pair.Value == null || pair.Value.Count == 0) continue;
                    _turretSync[pair.Key] = pair.Value;
                }
            }
            catch (Exception ex)
            {
                PrintWarning("NightshadeLootProtection could not read tcturretauth.json: " + ex.Message);
            }
        }

        private void SaveTurretSync()
        {
            TurretSyncFile file = new TurretSyncFile();
            foreach (KeyValuePair<ulong, List<ulong>> pair in _turretSync)
            {
                if (pair.Value == null || pair.Value.Count == 0) continue;
                file.Turrets[pair.Key] = pair.Value;
            }
            Interface.Oxide.DataFileSystem.WriteObject(DataFile(TurretName), file);
            _turretSyncDirty = false;
        }

        #endregion

        #region Data Structures

        private class OwnershipData
        {
            public ulong OwnerId { get; set; }
            public string OwnerName { get; set; }
            public HashSet<ulong> Contributors { get; set; } = new();
            public Dictionary<ulong, float> DamageMap { get; set; } = new();
            public float TotalDamage { get; set; }
            public float EntityMaxHealth { get; set; }
            public DateTime LockedAt { get; set; }
            public int LockDurationSeconds { get; set; }
            public float TooHotSeconds { get; set; }

            public bool IsExpired =>
                LockDurationSeconds > 0 && (DateTime.UtcNow - LockedAt).TotalSeconds >= LockDurationSeconds;

            public bool IsTooHot =>
                TooHotSeconds > 0 && (DateTime.UtcNow - LockedAt).TotalSeconds < TooHotSeconds;

            public bool CanAccess(ulong playerId)
            {
                return OwnerId == playerId || Contributors.Contains(playerId);
            }
        }

        #endregion

        #region Weekend PvP / TruePVE profile integration

        // GetPlayerMapping is location-specific; it may report a monument or stale
        // mapping and MUST NEVER, by itself, unlock someone else's storage.
        private string GetTruePveMapping(BasePlayer player)
        {
            if (_config?.WeekendPvP == null || !_config.WeekendPvP.ReadTruePVEProfile ||
                TruePVE == null || !TruePVE.IsLoaded || player == null)
                return null;
            return TruePVE.Call("GetPlayerMapping", player) as string;
        }

        // TruePVE 2.4.4 exposes its actual GLOBAL current ruleset through this API.
        private string GetTruePveRuleSet()
        {
            if (TruePVE == null || !TruePVE.IsLoaded) return null;
            return TruePVE.Call("CurrentRuleSetName") as string;
        }

        private TimeZoneInfo _nightshadeEasternZone;
        private bool _triedEasternZone;
        private bool _warnedEasternZone;

        private bool IsEasternWeekendWindow()
        {
            if (!_triedEasternZone)
            {
                _triedEasternZone = true;
                try { _nightshadeEasternZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
                catch
                {
                    try { _nightshadeEasternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
                    catch { /* Fail closed if neither timezone is installed. */ }
                }
            }
            if (_nightshadeEasternZone == null)
            {
                if (!_warnedEasternZone)
                {
                    _warnedEasternZone = true;
                    PrintError("Could not find Eastern timezone; weekend loot opening is disabled. Check host timezone data.");
                }
                return false;
            }

            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _nightshadeEasternZone);
            switch (local.DayOfWeek)
            {
                case DayOfWeek.Friday: return local.TimeOfDay >= TimeSpan.FromHours(20);
                case DayOfWeek.Saturday:
                case DayOfWeek.Sunday: return true;
                case DayOfWeek.Monday: return local.TimeOfDay < TimeSpan.FromHours(3);
                default: return false;
            }
        }

        private bool IsPurgeActive()
        {
            var opt = _config?.WeekendPvP;
            if (opt == null || !opt.Enabled || !opt.AllowDuringPurge) return false;
            // Prefer the actual purge controller when available.
            if (PurgeStatus != null && PurgeStatus.IsLoaded)
            {
                var overrideName = PurgeStatus.Call("API_GetActiveRulesetOverride") as string;
                if (overrideName != null)
                    return overrideName.Equals(opt.PurgeRuleSet, StringComparison.OrdinalIgnoreCase);
            }
            // Also support scheduled/manual Purge via TruePVE when the purge plugin
            // is not installed or does not expose an override.
            return string.Equals(GetTruePveRuleSet(), opt.PurgeRuleSet, StringComparison.OrdinalIgnoreCase);
        }

        private bool TryGetRaidWeekendState(out bool active)
        {
            active = false;
            if (WeekendPlugin() == null || !WeekendPlugin().IsLoaded) return false;
            var value = WeekendPlugin().Call("API_IsPvPActive");
            if (!(value is bool)) return false;
            active = (bool)value;
            return true;
        }

        // The real Eastern clock is an additional guard: nspvp.faketime modifies
        // NRP's published window, but its damage blocker uses the REAL clock.
        // Never open storage on a weekday just because the test clock is active.
        // nspvp.pvpweekend is the persisted full-server force. API_IsPvPActive is
        // true for it. Event loot follows that window. Player-placed storage stays
        // closed to non-teammates unless OpenPlayerStorageToNonTeammates is true.
        private bool IsWeekendPvp(BasePlayer player = null)
        {
            var opt = _config?.WeekendPvP;
            if (opt == null || !opt.Enabled) return false;
            if (IsPurgeActive()) return true;
            bool realWeekend = IsEasternWeekendWindow();
            bool forced = IsFullPvPForced();
            if (!realWeekend && !forced) return false;
            bool raidActive;
            if (TryGetRaidWeekendState(out raidActive)) return raidActive || forced;
            if (forced) return true;
            // Safe fallback when NRP is absent: require BOTH Eastern weekend time
            // and TruePVE's actual global weekend profile, not a zone mapping.
            return opt.EasternClockFallback &&
                string.Equals(GetTruePveRuleSet(), opt.WeekendRuleSet, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsFullPvPForced()
        {
            if (WeekendPlugin() == null || !WeekendPlugin().IsLoaded) return false;
            var value = WeekendPlugin().Call("API_IsFullPvPForced");
            return value is bool && (bool)value;
        }

        private bool SkipEventOwnership(BasePlayer player = null) =>
            IsWeekendPvp(player) && _config.WeekendPvP.DisableEventOwnershipLocks;

        private bool IsPlayerOrTeamOwner(BasePlayer player, ulong ownerId)
        {
            if (player == null || ownerId == 0UL) return false;
            if (player.userID == ownerId) return true;
            if (!_config.StorageProtection.UseTeams || player.currentTeam == 0UL) return false;
            var team = RelationshipManager.ServerInstance.FindTeam(player.currentTeam);
            return team != null && team.members.Contains(ownerId);
        }

        private static bool IsStorage(BaseEntity entity) =>
            entity is StorageContainer || entity is BaseOven || entity is ContainerIOEntity;

        // Use the entity's OWN building privilege, not the first TC located in a
        // 16m sphere (which can belong to a neighboring base).
        private static BuildingPrivlidge GetStorageTc(BaseEntity entity) =>
            entity == null || entity is SupplyDrop ? null : entity.GetBuildingPrivilege();

        private bool IsExplicitlyShared(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity?.net == null) return false;
            HashSet<ulong> shared;
            return _shareData.TryGetValue(entity.net.ID.Value, out shared) && shared.Contains(player.userID);
        }

        private bool CanPlayerUseStorage(BasePlayer player, BaseEntity entity, out string explanation)
        {
            explanation = "Unknown";
            if (player == null || entity == null || !IsStorage(entity))
            { explanation = "Not managed storage"; return true; }
            if (player.IsAdmin) { explanation = "Admin bypass"; return true; }
            if (IsExplicitlyShared(player, entity)) { explanation = "Explicitly shared"; return true; }
            if (IsPlayerOrTeamOwner(player, entity.OwnerID))
            { explanation = "Owner or Rust teammate"; return true; }

            BuildingPrivlidge tc = GetStorageTc(entity);
            ulong owner = entity.OwnerID != 0UL ? entity.OwnerID : tc != null ? tc.OwnerID : 0UL;
            if (owner == 0UL && tc == null)
            { explanation = "Unowned / no TC (public)"; return true; }
            if (IsPlayerOrTeamOwner(player, owner))
            { explanation = "TC owner or Rust teammate"; return true; }
            explanation = "Not owner/team/shared";
            return false;
        }

        // Outsiders are blocked when NightshadePVPWeekend would block their damage.
        // Attacker 0 keeps TC authorization from clearing the block. The raid plugin
        // uses the container's own building, not the nearest cupboard.
        private bool IsRaidProtectedAgainstOutsiders(BaseEntity entity)
        {
            if (entity == null || WeekendPlugin() == null || !WeekendPlugin().IsLoaded)
                return false;
            var result = WeekendPlugin().Call("API_IsEntityProtected", entity, 0UL);
            return result is bool && (bool)result;
        }

        private bool IsLootProtectedByRaidShield(BasePlayer player, BaseEntity entity)
        {
            if (player != null && player.IsAdmin) return false;
            if (entity == null) return false;
            if (IsPlayerOrTeamOwner(player, entity.OwnerID) || IsExplicitlyShared(player, entity)) return false;
            BuildingPrivlidge tc = OwnBuildingCupboard(entity);
            if (tc != null && IsPlayerOrTeamOwner(player, tc.OwnerID)) return false;
            return IsRaidProtectedAgainstOutsiders(entity);
        }

        // Cupboard attached to this entity's own building. Never the nearest overlap.
        private static BuildingPrivlidge OwnBuildingCupboard(BaseEntity entity)
        {
            DecayEntity decay = entity as DecayEntity;
            if (decay == null || decay.buildingID == 0u || BuildingManager.server == null) return null;
            BuildingManager.Building building = BuildingManager.server.GetBuilding(decay.buildingID);
            if (building == null || building.buildingPrivileges == null) return null;
            for (int i = 0; i < building.buildingPrivileges.Count; i++)
            {
                BuildingPrivlidge tc = building.buildingPrivileges[i];
                if (tc != null && !tc.IsDestroyed && tc.buildingID == decay.buildingID)
                    return tc;
            }
            return null;
        }

        private bool IsUtilityPublic(BaseEntity entity)
        {
            if (entity == null || string.IsNullOrEmpty(entity.ShortPrefabName)) return false;
            if (entity.ShortPrefabName.Equals("vendingmachine.deployed", StringComparison.OrdinalIgnoreCase))
                return false;
            return PublicEntities.Contains(entity.ShortPrefabName);
        }

        // Death boxes are not normal player storage. CanLootEntity non-null aborts the open.
        private bool IsNightshadeDeathBox(BaseEntity entity)
        {
            if (entity == null || NightshadeDeathBox == null || !NightshadeDeathBox.IsLoaded) return false;
            try
            {
                object value = NightshadeDeathBox.Call("API_IsNightshadeDeathBox", entity);
                return value is bool && (bool)value;
            }
            catch
            {
                return false;
            }
        }

        // Player-placed storage: a player steam id owns it, and it is not an event
        // crate, corpse, backpack, world loot container, or a public utility.
        private bool IsPlayerPlacedStorage(BaseEntity entity)
        {
            if (entity == null || !IsStorage(entity)) return false;
            if (entity is SupplyDrop || entity is HackableLockedCrate || entity is LootableCorpse ||
                entity is DroppedItemContainer || entity is LootContainer)
                return false;
            if (IsUtilityPublic(entity)) return false;
            return NightshadeLootRules.IsPlayerSteamId(entity.OwnerID);
        }

        private bool InExemptZone(BaseEntity entity)
        {
            if (entity == null || entity.transform == null || _exemptZones.Count == 0) return false;
            Vector3 pos = entity.transform.position;
            foreach (ExemptZone zone in _exemptZones.Values)
            {
                if (zone == null || zone.Radius <= 0f) continue;
                if (Vector3.Distance(pos, zone.Center) <= zone.Radius) return true;
            }
            return false;
        }

        private NightshadeLootRules.OpenRequest BuildOpenRequest(BasePlayer player, BaseEntity entity, bool frontUse, bool? ruleEnabled = null)
        {
            return new NightshadeLootRules.OpenRequest
            {
                Admin = player != null && player.IsAdmin,
                OwnerOrTeammate = IsPlayerOrTeamOwner(player, entity != null ? entity.OwnerID : 0UL),
                ExplicitShare = IsExplicitlyShared(player, entity),
                PlayerPlacedStorage = true,
                StorageEnabled = ruleEnabled ?? (_config.StorageProtection != null && _config.StorageProtection.Enabled),
                WeekendOrPurge = IsWeekendPvp(player),
                OpenPlayerStorageOnWeekend = _config.WeekendPvP != null && _config.WeekendPvP.OpenPlayerStorageToNonTeammates,
                RaidProtectedAgainstOutsiders = IsRaidProtectedAgainstOutsiders(entity),
                FrontUse = frontUse,
                ExemptZone = InExemptZone(entity) || (entity != null && IsRaidableBase(entity.transform.position))
            };
        }

        private object DecidePlayerPlaced(BasePlayer player, BaseEntity entity, bool storageMessage)
        {
            NightshadeLootRules.OpenRequest request = BuildOpenRequest(
                player, entity, IsVendingOpen(player, entity) || IsDropBoxOpen(player, entity));
            if (NightshadeLootRules.AllowPlayerPlacedOpen(request)) return null;
            if (player != null && storageMessage && _config.ChatAlerts.StorageBlocked)
            {
                if (request.RaidProtectedAgainstOutsiders)
                    player.ChatMessage("<color=#A04A4A>This container is in a raid-protected base. It cannot be damaged right now.</color>");
                else
                    player.ChatMessage("<color=#A04A4A>This container belongs to another player. Teammates can open it. Everyone else has to destroy it.</color>");
            }
            return false;
        }

        [HookMethod("AddExemptZone")]
        public bool AddExemptZone(string zoneId, Vector3 center, float radius)
        {
            if (string.IsNullOrEmpty(zoneId) || radius <= 0f) return false;
            _exemptZones[zoneId] = new ExemptZone { Center = center, Radius = radius };
            return true;
        }

        [HookMethod("RemoveExemptZone")]
        public bool RemoveExemptZone(string zoneId)
        {
            if (string.IsNullOrEmpty(zoneId)) return false;
            return _exemptZones.Remove(zoneId);
        }

        #endregion

        #region Base trap safety and turret TC auth

        // TruePVE asks CanEntityTakeDamage before its own rules. false blocks that hit.
        // null leaves the hit to TruePVE. true would force TruePVE to allow it and skip its rules.
        // OnEntityTakeDamage still returns true for a full cancel: a zeroed hit that returns null
        // can be treated as a normal hit by a later hook, including a weekend "anything can hurt anything" ruleset.
        // CanEntityBeTargeted false tells TruePVE not to target. true would force it to allow the target.
        // CanEntityTrapTrigger false tells TruePVE not to spring the trap. true would force the trap to spring.
        // OnTurretTarget and OnTrapTrigger return true to cancel the vanilla action, and null to leave it alone.
        // CanBeTargeted on an auto turret returns false for one candidate. ObjectVisible is per target,
        // so the scan keeps looking for someone else. It never returns true, because that skips the line-of-sight check.
        // Flame turrets and shotgun traps treat a bool from CanBeTargeted as the result of the whole
        // CheckTrigger pass, so a false there would make the trap ignore enemies while a friendly is inside.
        // Those two stay on OnEntityEnter plus the damage hook.

        private bool TrapSafetyOn
        {
            get { return _config != null && _config.TrapTeamSafety != null && _config.TrapTeamSafety.Enabled; }
        }

        private bool TurretTcOn
        {
            get { return _config != null && _config.TurretTcAuth != null && _config.TurretTcAuth.Enabled; }
        }

        private static bool IsRealPlayer(BasePlayer player)
        {
            return player != null && !player.IsNpc && NightshadeLootRules.IsPlayerSteamId(player.userID);
        }

        // SAM sites and NPC safe-zone turrets are not base traps.
        private static bool IsPlayerTrap(BaseEntity entity)
        {
            if (entity == null || entity is SamSite || entity is NPCAutoTurret) return false;
            if (entity is AutoTurret || entity is FlameTurret || entity is GunTrap || entity is Landmine || entity is BearTrap)
                return true;
            string name = entity.ShortPrefabName;
            if (string.IsNullOrEmpty(name)) return false;
            return name.Equals("spikes.floor", StringComparison.OrdinalIgnoreCase)
                || name.Equals("spikes.trap", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOwnerOrTrapTeam(BasePlayer player, ulong ownerId)
        {
            if (!IsRealPlayer(player) || ownerId == 0UL) return false;
            if (player.userID == ownerId) return true;
            if (player.currentTeam == 0UL || RelationshipManager.ServerInstance == null) return false;
            RelationshipManager.PlayerTeam team = RelationshipManager.ServerInstance.FindTeam(player.currentTeam);
            return team != null && team.members != null && team.members.Contains(ownerId);
        }

        private static bool IsCoveringTcAuthed(BasePlayer player, BaseEntity entity)
        {
            if (!IsRealPlayer(player) || entity == null) return false;
            BuildingPrivlidge tc = entity.GetBuildingPrivilege();
            return tc != null && !tc.IsDestroyed && tc.IsAuthed(player);
        }

        private bool IsTrapFriendly(BasePlayer player, BaseEntity trap)
        {
            if (!IsRealPlayer(player) || !IsPlayerTrap(trap)) return false;
            bool team = IsOwnerOrTrapTeam(player, trap.OwnerID);
            bool tc = IsCoveringTcAuthed(player, trap);
            if (TrapSafetyOn && (team || tc)) return true;
            return TurretTcOn && trap is AutoTurret && tc;
        }

        private static BaseEntity FindTrap(BaseEntity entity, int depth)
        {
            if (entity == null || depth > 4) return null;
            if (IsPlayerTrap(entity)) return entity;
            BaseEntity viaCreator = FindTrap(entity.creatorEntity, depth + 1);
            if (viaCreator != null) return viaCreator;
            return FindTrap(entity.GetParentEntity(), depth + 1);
        }

        private static BaseEntity ResolveTrap(HitInfo info)
        {
            if (info == null) return null;
            BaseEntity found = FindTrap(info.Initiator, 0);
            if (found != null) return found;
            found = FindTrap(info.WeaponPrefab, 0);
            if (found != null) return found;
            return FindTrap(info.Weapon as BaseEntity, 0);
        }

        private bool ShouldBlockTrapDamage(BaseEntity victim, HitInfo info)
        {
            BasePlayer player = victim as BasePlayer;
            if (!IsRealPlayer(player)) return false;
            BaseEntity trap = ResolveTrap(info);
            return trap != null && IsTrapFriendly(player, trap);
        }

        private void DropTurretIfAiming(BaseEntity trap, BasePlayer player)
        {
            AutoTurret turret = trap as AutoTurret;
            if (turret == null || player == null || turret.target != player) return;
            turret.SetTarget(null);
        }

        private object SpareTarget(BasePlayer player, BaseEntity trap)
        {
            if (!IsTrapFriendly(player, trap)) return null;
            DropTurretIfAiming(trap, player);
            return false;
        }

        private object CanBeTargeted(BaseCombatEntity target, AutoTurret turret)
        {
            return SpareTarget(target as BasePlayer, turret);
        }

        private object OnTurretTarget(AutoTurret turret, BasePlayer target)
        {
            if (!IsTrapFriendly(target, turret)) return null;
            DropTurretIfAiming(turret, target);
            return true;
        }

        private object OnTrapTrigger(BaseTrap trap, GameObject go)
        {
            if (go == null) return null;
            BasePlayer player = go.GetComponent<BasePlayer>();
            if (!IsTrapFriendly(player, trap)) return null;
            return true;
        }

        private object OnEntityEnter(TriggerBase trigger, BasePlayer player)
        {
            if (trigger == null || trigger.gameObject == null) return null;
            BaseEntity trap = trigger.gameObject.ToBaseEntity();
            if (!IsTrapFriendly(player, trap)) return null;
            return true;
        }

        // TruePVE calls these from inside its own targeting and trap hooks.
        private object CanEntityBeTargeted(BasePlayer target, BaseEntity entity)
        {
            if (!IsTrapFriendly(target, entity)) return null;
            DropTurretIfAiming(entity, target);
            return false;
        }

        private object CanEntityTrapTrigger(BaseTrap trap, BasePlayer player)
        {
            if (!IsTrapFriendly(player, trap)) return null;
            return false;
        }

        private object CanEntityTakeDamage(BaseEntity entity, HitInfo info)
        {
            if (!ShouldBlockTrapDamage(entity, info)) return null;
            return false;
        }

        // A code lock on the turret opens for a player authorized on the covering TC.
        // null leaves every other lock to its own check. true is the allow override.
        private object CanUseLockedEntity(BasePlayer player, BaseLock baseLock)
        {
            if (!TurretTcOn || player == null || baseLock == null) return null;
            AutoTurret turret = baseLock.GetParentEntity() as AutoTurret;
            if (turret == null || turret is NPCAutoTurret) return null;
            if (!IsCoveringTcAuthed(player, turret)) return null;
            return true;
        }

        private bool SyncTurret(AutoTurret turret)
        {
            if (turret == null || turret.IsDestroyed || turret.net == null || turret is NPCAutoTurret) return false;
            if (turret.authorizedPlayers == null) return false;

            ulong id = turret.net.ID.Value;
            List<ulong> synced;
            if (!_turretSync.TryGetValue(id, out synced) || synced == null)
                synced = new List<ulong>();

            BuildingPrivlidge tc = TurretTcOn ? turret.GetBuildingPrivilege() : null;
            HashSet<ulong> wanted = new HashSet<ulong>();
            if (TurretTcOn && tc != null && !tc.IsDestroyed && tc.authorizedPlayers != null)
            {
                foreach (ulong userId in tc.authorizedPlayers)
                {
                    if (userId != 0UL) wanted.Add(userId);
                }
            }

            bool changed = false;
            for (int i = synced.Count - 1; i >= 0; i--)
            {
                ulong userId = synced[i];
                if (wanted.Contains(userId)) continue;
                turret.authorizedPlayers.Remove(userId);
                synced.RemoveAt(i);
                changed = true;
            }

            if (TurretTcOn)
            {
                foreach (ulong userId in wanted)
                {
                    if (turret.authorizedPlayers.Contains(userId)) continue;
                    if (turret.AtMaxAuthCapacity()) break;
                    turret.authorizedPlayers.Add(userId);
                    if (!synced.Contains(userId)) synced.Add(userId);
                    changed = true;
                }
            }

            if (synced.Count == 0) _turretSync.Remove(id);
            else _turretSync[id] = synced;
            if (!changed) return false;

            turret.authDirty = true;
            turret.UpdateMaxAuthCapacity();
            turret.SendNetworkUpdate();
            if (turret.target is BasePlayer aimed && turret.IsAuthed(aimed))
                turret.SetTarget(null);
            _turretSyncDirty = true;
            return true;
        }

        private bool SyncPrivilegeTurrets(BuildingPrivlidge privilege)
        {
            bool changed = false;
            if (privilege != null && privilege.buildingID != 0u && BuildingManager.server != null)
            {
                BuildingManager.Building building = BuildingManager.server.GetBuilding(privilege.buildingID);
                if (building != null && building.decayEntities != null)
                {
                    foreach (DecayEntity decay in building.decayEntities)
                    {
                        AutoTurret turret = decay as AutoTurret;
                        if (turret != null && SyncTurret(turret)) changed = true;
                    }
                }
            }
            if (privilege == null || privilege.transform == null) return changed;
            float radius = BuildingPrivlidge.Radius;
            if (radius < 16f || float.IsNaN(radius)) radius = 30f;
            List<AutoTurret> found = new List<AutoTurret>();
            Vis.Entities(privilege.transform.position, radius, found);
            for (int i = 0; i < found.Count; i++)
            {
                if (SyncTurret(found[i])) changed = true;
            }
            return changed;
        }

        private bool SyncAllAutoTurrets()
        {
            bool changed = false;
            HashSet<ulong> live = new HashSet<ulong>();
            if (BaseNetworkable.serverEntities != null)
            {
                foreach (BaseNetworkable net in BaseNetworkable.serverEntities)
                {
                    AutoTurret turret = net as AutoTurret;
                    if (turret == null || turret.IsDestroyed || turret.net == null || turret is NPCAutoTurret) continue;
                    live.Add(turret.net.ID.Value);
                    if (SyncTurret(turret)) changed = true;
                }
            }
            List<ulong> stale = new List<ulong>();
            foreach (ulong id in _turretSync.Keys)
            {
                if (!live.Contains(id)) stale.Add(id);
            }
            for (int i = 0; i < stale.Count; i++)
            {
                _turretSync.Remove(stale[i]);
                changed = true;
            }
            if (changed) _turretSyncDirty = true;
            return changed;
        }

        private void ScheduleTurretSync(BuildingPrivlidge privilege)
        {
            if (privilege == null) return;
            NextTick(() =>
            {
                if (privilege == null || privilege.IsDestroyed) return;
                try
                {
                    if (SyncPrivilegeTurrets(privilege))
                        SaveTurretSync();
                }
                catch (Exception ex)
                {
                    PrintWarning("NightshadeLootProtection could not sync turret authorization: " + ex.Message);
                }
            });
        }

        private void OnEntitySpawned(AutoTurret turret)
        {
            if (turret == null) return;
            NextTick(() =>
            {
                try
                {
                    if (SyncTurret(turret))
                        SaveTurretSync();
                }
                catch (Exception ex)
                {
                    PrintWarning("NightshadeLootProtection could not sync turret authorization: " + ex.Message);
                }
            });
        }

        #endregion

        #region Damage Tracking

        // Event damage tracking only. This does not block damage to player containers.
        // NightshadePVPWeekend is what stops damage while a base is protected.
        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return null;

            // Immune CH47 - cancel all damage until cargo is dropped
            // Belt-and-suspenders: add to immune set here in case OnEntitySpawned missed it
            if (entity is CH47Helicopter)
            {
                _immuneCh47s.Add(entity.net.ID.Value);
                info.damageTypes.ScaleAll(0f);
                return true;
            }

            if (ShouldBlockTrapDamage(entity, info))
            {
                if (info.damageTypes != null) info.damageTypes.ScaleAll(0f);
                info.HitEntity = null;
                info.DoHitEffects = false;
                DropTurretIfAiming(ResolveTrap(info), entity as BasePlayer);
                return true;
            }

            if (SkipEventOwnership(info.InitiatorPlayer)) return null;

            if (!(entity is BradleyAPC || entity is PatrolHelicopter ||
                  entity is BaseNpc || entity is ScientistNPC))
                return null;

            var attacker = info.InitiatorPlayer;
            if (attacker == null || attacker.IsNpc) return null;

            var uid = entity.net.ID.Value;
            float damage = info.damageTypes.Total();

            if (!_entityOwnership.TryGetValue(uid, out var data))
            {
                data = new OwnershipData
                {
                    EntityMaxHealth = entity.MaxHealth(),
                    TooHotSeconds = GetTooHotSeconds(entity),
                    LockDurationSeconds = GetLockDuration(entity)
                };
                _entityOwnership[uid] = data;
            }

            data.TotalDamage += damage;
            data.DamageMap.TryGetValue(attacker.userID, out float existing);
            data.DamageMap[attacker.userID] = existing + damage;

            float threshold = GetDamageThreshold(entity);
            float playerFraction = data.DamageMap[attacker.userID] / data.EntityMaxHealth;

            if (data.OwnerId == 0 && playerFraction >= threshold)
            {
                data.OwnerId = attacker.userID;
                data.OwnerName = attacker.displayName;
                data.LockedAt = DateTime.UtcNow;

                if (attacker.currentTeam != 0)
                {
                    var team = RelationshipManager.ServerInstance.FindTeam(attacker.currentTeam);
                    if (team != null)
                        foreach (var memberId in team.members)
                            data.Contributors.Add(memberId);
                }
            }
            else if (data.OwnerId != 0)
            {
                data.Contributors.Add(attacker.userID);
                if (attacker.currentTeam != 0)
                {
                    var team = RelationshipManager.ServerInstance.FindTeam(attacker.currentTeam);
                    if (team != null)
                        foreach (var memberId in team.members)
                            data.Contributors.Add(memberId);
                }
            }

            return null;
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null) return;
            if (SkipEventOwnership(info?.InitiatorPlayer)) return;
            var uid = entity.net.ID.Value;

            if (!_entityOwnership.TryGetValue(uid, out var data)) return;
            if (data.OwnerId == 0) return;

            // For NPCs, record death position so OnEntitySpawned(LootableCorpse) can match it
            if (entity is BaseNpc || entity is ScientistNPC)
                _npcDeathPositions[entity.transform.position] = data;

            var owner = BasePlayer.FindByID(data.OwnerId);
            if (owner == null) return;

            bool shouldNotify = false;
            string entityType = "";

            if (entity is BradleyAPC && _config.Bradley.BroadcastKillToKiller)
            { shouldNotify = true; entityType = "Bradley APC"; }
            else if (entity is PatrolHelicopter && _config.Helicopter.BroadcastKillToKiller)
            { shouldNotify = true; entityType = "Patrol Helicopter"; }
            else if ((entity is BaseNpc || entity is ScientistNPC) && _config.NPC.BroadcastKillToKiller)
            { shouldNotify = true; entityType = "NPC"; }

            if (shouldNotify && _config.ChatAlerts.KillLockNotifyOwner)
                owner.ChatMessage($"<color=#88D66C>You killed the {entityType}! Loot is locked to you and your team.</color>");
        }

        #endregion

        #region Loot Access Control

        // Carbon requires typed overloads  -  a single BaseEntity signature misses most container types
        private object CanLootEntity(BasePlayer player, StorageContainer container) => CheckLootAccess(player, container);
        private object CanLootEntity(BasePlayer player, ContainerIOEntity container) => CheckLootAccess(player, container);
        private object CanLootEntity(BasePlayer player, LootableCorpse corpse) => CheckLootAccess(player, corpse);
        private object CanLootEntity(BasePlayer player, DroppedItemContainer container) => CheckLootAccess(player, container);
        private object CanLootEntity(BasePlayer player, WorldItem item) => CheckLootAccess(player, item);

        private object CheckLootAccess(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null) return null;
            // NightshadeDeathBox owns these containers. Any non-null CanLootEntity
            // result aborts PlayerOpenLoot, so this plugin must not add a false.
            if (IsNightshadeDeathBox(entity)) return null;
            if (IsPlayerPlacedStorage(entity))
                return DecidePlayerPlaced(player, entity, true);
            if (IsWeekendPvp(player))
            {
                if (IsLootProtectedByRaidShield(player, entity))
                {
                    if (_config.ChatAlerts.StorageBlocked)
                        player.ChatMessage("<color=#A04A4A>This base has active Nightshade raid protection.</color>");
                    return false;
                }
                return null;
            }

            // Admins always bypass storage/loot protection
            if (player.IsAdmin)
            {
                return null;
            }

            var shortName = entity.ShortPrefabName;
            if (PublicEntities.Contains(shortName))
            {
                return null;
            }

            // Allow players buying from a vending machine (front panel) or depositing into a dropbox
            if (IsVendingOpen(player, entity) || IsDropBoxOpen(player, entity))
            {
                return null;
            }

            var uid = entity.net.ID.Value;

            if (_entityOwnership.TryGetValue(uid, out var data) && data.OwnerId != 0)
            {
                if (data.IsExpired)
                {
                    _entityOwnership.Remove(uid);
                    _shareData.Remove(uid); // FIX 3: clean shares on inline expiry check
                    return null;
                }

                if (data.IsTooHot)
                {
                    if (_config.ChatAlerts.TooHot)
                        player.ChatMessage("<color=#A04A4A>This loot is too hot right now. Wait a moment.</color>");
                    return false;
                }

                if (!data.CanAccess(player.userID))
                {
                    if (_config.ChatAlerts.LootBlocked)
                        player.ChatMessage("<color=#A04A4A>This loot belongs to another player.</color>");
                    return false;
                }

                return null;
            }

            // Per-entity share check
            if (_shareData.TryGetValue(uid, out var entityShared) && entityShared.Contains(player.userID))
                return null;

            // Weekday PVE: only box owner, their Rust team, or an explicit share.
            // TC authorization ALONE does not grant box access to outsiders.
            if (_config.StorageProtection.Enabled && IsStorage(entity))
            {
                string explanation;
                if (!CanPlayerUseStorage(player, entity, out explanation))
                {
                    if (_config.ChatAlerts.StorageBlocked)
                        player.ChatMessage("<color=#A04A4A>Storage is private during PvE (owner/team/shared only).</color>");
                    return false;
                }
            }

            return null;
        }

        // Protect sleeping players all week, including PvP weekends AND Purge.
        // Do NOT apply IsWeekendPvp here: CanLootPlayer covers live player inventories.
        // Dead-player corpse containers are handled separately by CanLootEntity.
        private object CanLootPlayer(BasePlayer target, BasePlayer looter)
        {
            // Nightshade rule: sleeper inventories stay protected in ALL modes,
            // even if the generic CorpseProtection.Enabled config was disabled.
            if (looter == null || target == null) return null;
            if (looter.IsAdmin && _config.CorpseProtection.AdminCanLoot) return null;
            if (looter.userID == target.userID) return null;

            if (_config.CorpseProtection.UseTeams && AreTeammates(looter, target))
                return null;

            if (_config.ChatAlerts.CorpseBlocked)
                looter.ChatMessage("<color=#A04A4A>You cannot loot another player's corpse.</color>");
            return false;
        }

        private object CanRackedWeaponMount(BasePlayer player, WeaponRack rack) => CheckWeaponRack(player, rack);
        private object CanRackedWeaponTake(BasePlayer player, WeaponRack rack) => CheckWeaponRack(player, rack);
        private object CanRackedWeaponSwap(BasePlayer player, WeaponRack rack) => CheckWeaponRack(player, rack);
        private object CanRackedWeaponLoad(BasePlayer player, WeaponRack rack) => CheckWeaponRack(player, rack);
        private object CanRackedWeaponUnload(BasePlayer player, WeaponRack rack) => CheckWeaponRack(player, rack);

        private object CheckWeaponRack(BasePlayer player, WeaponRack rack)
        {
            if (!_config.WeaponRackProtection.Enabled) return null;
            if (player == null || rack == null) return null;
            if (NightshadeLootRules.IsPlayerSteamId(rack.OwnerID))
            {
                if (NightshadeLootRules.AllowPlayerPlacedOpen(BuildOpenRequest(player, rack, false, true)))
                    return null;
                if (_config.ChatAlerts.WeaponRackBlocked)
                    player.ChatMessage("<color=#A04A4A>This weapon rack belongs to another player. Teammates can open it. Everyone else has to destroy it.</color>");
                return false;
            }
            if (IsWeekendPvp(player)) return null;
            if (player.IsAdmin) return null;

            if (rack.net != null && _shareData.TryGetValue(rack.net.ID.Value, out var rackShared) && rackShared.Contains(player.userID))
                return null;

            var privs = new List<BuildingPrivlidge>();
            Vis.Entities(rack.transform.position, 16f, privs);
            if (privs.Count == 0) return null;

            foreach (var priv in privs)
            {
                if (IsAuthorizedOrTeammate(player, priv))
                    return null;
            }

            if (_config.ChatAlerts.WeaponRackBlocked)
                player.ChatMessage("<color=#A04A4A>You cannot access this weapon rack.</color>");
            return false;
        }

        // CanHackCrate gives us the actual initiating player  -  no proximity guessing needed
        private void CanHackCrate(BasePlayer player, HackableLockedCrate crate)
        {
            if (!_config.HackableCrate.Enabled) return;
            if (crate == null || player == null) return;
            if (SkipEventOwnership(player)) return;

            // Set OwnerID directly on the crate so storage protection handles it automatically
            crate.OwnerID = player.userID;

            // Also register in _entityOwnership for the lock timer and contributor tracking
            var uid = crate.net.ID.Value;
            _entityOwnership[uid] = new OwnershipData
            {
                OwnerId = player.userID,
                OwnerName = player.displayName,
                LockedAt = DateTime.UtcNow,
                LockDurationSeconds = _config.HackableCrate.LockForSeconds,
                EntityMaxHealth = 1f
            };

            if (player.currentTeam != 0)
            {
                var team = RelationshipManager.ServerInstance.FindTeam(player.currentTeam);
                if (team != null)
                    foreach (var memberId in team.members)
                        _entityOwnership[uid].Contributors.Add(memberId);
            }

            player.ChatMessage($"<color=#88D66C>Hackable crate locked to you for {_config.HackableCrate.LockForSeconds / 60} minutes.</color>");
        }

        // Fired when a supply signal is thrown  -  track signal item UID -> thrower
        private void OnExplosiveDropped(BaseEntity entity, Item item, BasePlayer player)
        {
            if (!_config.SupplyDrop.Enabled) return;
            if (item?.info?.shortname != "supply.signal") return;
            if (player == null || entity == null) return;
            if (SkipEventOwnership(player)) return;
            _pendingSupplyOwners[entity.net.ID.Value] = player.userID;
        }

        // Fired when an NPC corpse spawns  -  transfer ownership from the NPC death position
        private void OnEntitySpawned(LootableCorpse corpse)
        {
            if (corpse == null || corpse.playerSteamID.IsSteamId()) return; // skip player corpses
            if (SkipEventOwnership()) return;

            // Find the closest recorded NPC death position within 1m
            Vector3 corpsePos = corpse.transform.position;
            Vector3 bestMatch = default;
            float bestDist = float.MaxValue;

            foreach (var pos in _npcDeathPositions.Keys)
            {
                float dist = Vector3.Distance(pos, corpsePos);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestMatch = pos;
                }
            }

            if (bestDist > 1f || bestMatch == default) return; // no close match

            var data = _npcDeathPositions[bestMatch];
            _npcDeathPositions.Remove(bestMatch);

            var uid = corpse.net.ID.Value;
            _entityOwnership[uid] = new OwnershipData
            {
                OwnerId = data.OwnerId,
                OwnerName = data.OwnerName,
                Contributors = new HashSet<ulong>(data.Contributors),
                LockedAt = data.LockedAt,
                LockDurationSeconds = data.LockDurationSeconds,
                EntityMaxHealth = 1f
            };
        }

        // Fired when a CH47 spawns  -  mark all CH47s immune until they drop cargo
        // We can't reliably detect Bradley-drop CH47s at spawn time so we protect all of them;
        // regular patrol/transport CH47s don't carry lootable cargo so there's no downside
        private void OnEntitySpawned(CH47Helicopter ch47)
        {
            if (ch47 == null) return;
            _immuneCh47s.Add(ch47.net.ID.Value);
        }

        // Fired when a CH47 drops its cargo  -  lift immunity so it can be engaged normally
        private void OnCh47DropCargo(CH47Helicopter ch47, BaseEntity cargo)
        {
            if (ch47 == null) return;
            _immuneCh47s.Remove(ch47.net.ID.Value);
        }

        private void OnSupplyDropLanded(SupplyDrop drop)
        {
            if (!_config.SupplyDrop.Enabled) return;
            if (drop == null) return;
            if (SkipEventOwnership()) return;

            // Match this drop to the signal entity closest to the drop position
            // that still has a pending owner  -  find the signal by proximity
            ulong ownerId = 0;
            ulong matchedSignalId = 0;
            float closestDist = float.MaxValue;

            foreach (var kvp in _pendingSupplyOwners)
            {
                // The signal entity may already be destroyed; use position fallback
                var signalEntity = BaseNetworkable.serverEntities.Find(new NetworkableId(kvp.Key)) as BaseEntity;
                if (signalEntity == null) continue;
                float dist = Vector3.Distance(signalEntity.transform.position, drop.transform.position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    ownerId = kvp.Value;
                    matchedSignalId = kvp.Key;
                }
            }

            // Fallback: if all signal entities are gone (normal  -  they despawn), take any pending owner
            if (ownerId == 0 && _pendingSupplyOwners.Count > 0)
            {
                foreach (var kvp in _pendingSupplyOwners)
                {
                    ownerId = kvp.Value;
                    matchedSignalId = kvp.Key;
                    break;
                }
            }

            if (ownerId == 0) return;

            _pendingSupplyOwners.Remove(matchedSignalId);

            var uid = drop.net.ID.Value;
            var owner = BasePlayer.FindByID(ownerId);

            _entityOwnership[uid] = new OwnershipData
            {
                OwnerId = ownerId,
                OwnerName = owner?.displayName ?? "Unknown",
                LockedAt = DateTime.UtcNow,
                LockDurationSeconds = _config.SupplyDrop.LockForSeconds,
                EntityMaxHealth = 1f
            };

            if (owner != null && owner.currentTeam != 0)
            {
                var team = RelationshipManager.ServerInstance.FindTeam(owner.currentTeam);
                if (team != null)
                    foreach (var memberId in team.members)
                        _entityOwnership[uid].Contributors.Add(memberId);
            }
        }

        #endregion

        #region TC Authorization Protection

        // Prevents strangers from authorizing themselves on your tool cupboard
        private object OnCupboardAuthorize(BuildingPrivlidge privilege, BasePlayer player)
        {
            ScheduleTurretSync(privilege);
            if (privilege == null || player == null) return null;
            // Keep TC authorization protected during raid windows by default.
            // Letting an attacker authorize on a TC could defeat offline protection.
            if (IsWeekendPvp(player) && !_config.WeekendPvP.KeepTcAuthProtected) return null;
            if (player.IsAdmin) return null;

            // Allow TC auth inside RaidableBases zones
            if (IsRaidableBase(privilege.transform.position)) return null;

            var ownerId = privilege.OwnerID;
            if (ownerId == 0) return null;           // unowned TC, allow
            if (ownerId == player.userID) return null; // it's their TC

            // Team check
            if (player.currentTeam != 0)
            {
                var team = RelationshipManager.ServerInstance.FindTeam(player.currentTeam);
                if (team != null && team.members.Contains(ownerId)) return null;
            }

            // Already authed players can re-auth (refresh)
            if (privilege.IsAuthed(player)) return null;

            if (_config.ChatAlerts.TCAuthBlocked)
                player.ChatMessage("<color=#A04A4A>You are not authorized to use this tool cupboard.</color>");
            return false;
        }

        private void OnCupboardDeauthorize(BuildingPrivlidge privilege, BasePlayer player)
        {
            ScheduleTurretSync(privilege);
        }

        private void OnCupboardClearList(BuildingPrivlidge privilege, BasePlayer player)
        {
            ScheduleTurretSync(privilege);
        }

        #endregion

        #region Vending Machine Protection

        // Protects the admin/back panel of vending machines from unauthorized editing
        // Buying (PlayerInfront) is always allowed  -  handled by IsVendingOpen in CanLootEntity
        private object CanAdministerVending(BasePlayer player, VendingMachine machine)
        {
            if (machine == null || player == null) return null;
            if (IsPlayerPlacedStorage(machine))
            {
                object decision = DecidePlayerPlaced(player, machine, false);
                if (decision != null && _config.ChatAlerts.VendingAdminBlocked)
                    player.ChatMessage("<color=#A04A4A>You cannot administer this vending machine.</color>");
                return decision;
            }
            if (IsWeekendPvp(player)) return null;
            if (player.IsAdmin) return null;

            // Owner can always manage their own machine
            if (machine.OwnerID == player.userID) return null;
            if (machine.OwnerID == 0) return null;

            // Team check against the machine owner
            if (player.currentTeam != 0)
            {
                var ownerPlayer = BasePlayer.FindByID(machine.OwnerID);
                if (ownerPlayer != null && AreTeammates(player, ownerPlayer)) return null;

                var team = RelationshipManager.ServerInstance.FindTeam(player.currentTeam);
                if (team != null && team.members.Contains(machine.OwnerID)) return null;
            }

            // TC auth check
            var privs = new List<BuildingPrivlidge>();
            Vis.Entities(machine.transform.position, 16f, privs);
            foreach (var priv in privs)
                if (IsAuthorizedOrTeammate(player, priv)) return null;

            if (_config.ChatAlerts.VendingAdminBlocked)
                player.ChatMessage("<color=#A04A4A>You cannot administer this vending machine.</color>");
            return false;
        }

        #endregion

        #region Pickup Protection

        // Prevents picking up entities the player doesn't own or have TC auth for
        private object CanPickupEntity(BasePlayer player, BaseCombatEntity entity)
        {
            if (entity == null || player == null) return null;
            if (IsNightshadeDeathBox(entity)) return null;

            // Vehicles stay on their own rules. A player-owned storage box still cannot be picked up by a stranger.
            if (entity is BaseVehicle || entity is BaseVehicleModule) return null;
            if (IsPlayerPlacedStorage(entity))
            {
                object decision = DecidePlayerPlaced(player, entity, false);
                if (decision != null && _config.ChatAlerts.PickupBlocked)
                    player.ChatMessage("<color=#A04A4A>You cannot pick up another player's container. Teammates can. Everyone else has to destroy it.</color>");
                return decision;
            }
            if (IsWeekendPvp(player)) return null;
            if (player.IsAdmin) return null;

            // Skip protection inside RaidableBases zones
            if (IsRaidableBase(entity.transform.position)) return null;

            var ownerId = entity.OwnerID;
            if (ownerId == 0) return null;          // unowned, allow
            if (ownerId == player.userID) return null; // their own

            // Team check
            if (player.currentTeam != 0)
            {
                var team = RelationshipManager.ServerInstance.FindTeam(player.currentTeam);
                if (team != null && team.members.Contains(ownerId)) return null;
            }

            // TC auth check
            var privs = new List<BuildingPrivlidge>();
            Vis.Entities(entity.transform.position, 16f, privs);
            foreach (var priv in privs)
                if (IsAuthorizedOrTeammate(player, priv)) return null;

            if (_config.ChatAlerts.PickupBlocked)
                player.ChatMessage("<color=#A04A4A>You cannot pick up what isn't yours.</color>");
            return false;
        }

        #endregion

        #region Oven Protection

        private object CanToggle(BaseOven oven, BasePlayer player)
        {
            if (oven == null || player == null) return null;
            if (IsPlayerPlacedStorage(oven))
            {
                object decision = DecidePlayerPlaced(player, oven, false);
                if (decision != null && _config.ChatAlerts.OvenToggleBlocked)
                    player.ChatMessage("<color=#A04A4A>You cannot toggle another player's oven.</color>");
                return decision;
            }
            if (IsWeekendPvp(player)) return null;
            if (player.IsAdmin) return null;

            var privs = new List<BuildingPrivlidge>();
            Vis.Entities(oven.transform.position, 16f, privs);
            if (privs.Count == 0) return null;

            foreach (var priv in privs)
            {
                if (IsAuthorizedOrTeammate(player, priv))
                    return null;
            }

            if (_config.ChatAlerts.OvenToggleBlocked)
                player.ChatMessage("<color=#A04A4A>You cannot toggle this oven.</color>");
            return false;
        }

        #endregion

        #region Helpers

        // Returns true if the player is standing in front of the vending machine (buying side)
        private bool IsVendingOpen(BasePlayer player, BaseEntity entity)
        {
            if (entity is VendingMachine vm)
                return vm.PlayerInfront(player);
            return false;
        }

        // Returns true if the player is standing in front of a drop box (depositing side)
        private bool IsDropBoxOpen(BasePlayer player, BaseEntity entity)
        {
            if (entity is DropBox db)
                return db.PlayerInfront(player);
            return false;
        }

        // Returns true if the position is inside an active RaidableBases zone
        private bool IsRaidableBase(Vector3 position)
        {
            if (RaidableBases == null) return false;
            return RaidableBases.Call<bool>("EventTerritory", position);
        }

        private bool AreTeammates(BasePlayer a, BasePlayer b)
        {
            if (a.currentTeam == 0) return false;
            return a.currentTeam == b.currentTeam;
        }

        private bool IsAuthorizedOrTeammate(BasePlayer player, BuildingPrivlidge priv)
        {
            // Direct TC auth only  -  being a teammate of someone who is authed is NOT sufficient
            return priv.IsAuthed(player);
        }

        private float GetDamageThreshold(BaseCombatEntity entity)
        {
            if (entity is BradleyAPC) return _config.Bradley.DamageLockThreshold;
            if (entity is PatrolHelicopter) return _config.Helicopter.DamageLockThreshold;
            return _config.NPC.DamageLockThreshold;
        }

        private int GetLockDuration(BaseCombatEntity entity)
        {
            if (entity is BradleyAPC) return _config.Bradley.LockForSeconds;
            if (entity is PatrolHelicopter) return _config.Helicopter.LockForSeconds;
            return _config.NPC.LockForSeconds;
        }

        private float GetTooHotSeconds(BaseCombatEntity entity)
        {
            if (entity is BradleyAPC) return _config.Bradley.TooHotSeconds;
            if (entity is PatrolHelicopter) return _config.Helicopter.TooHotSeconds;
            return 0f;
        }

        #endregion

        #region Cleanup

        private void OnServerSave()
        {
            var expired = new List<ulong>();
            foreach (var kvp in _entityOwnership)
                if (kvp.Value.IsExpired)
                    expired.Add(kvp.Key);

            foreach (var uid in expired)
            {
                _entityOwnership.Remove(uid);
                _shareData.Remove(uid); // FIX 2: clean up shares for expired ownership records
            }

            // Also clear stale NPC death positions older than 10 seconds
            var stalePositions = new List<Vector3>();
            foreach (var kvp in _npcDeathPositions)
                if ((DateTime.UtcNow - kvp.Value.LockedAt).TotalSeconds > 10)
                    stalePositions.Add(kvp.Key);
            foreach (var pos in stalePositions)
                _npcDeathPositions.Remove(pos);

            if (_config != null && _config.LogCleanupSummary)
                Puts("NightshadeLootProtection cleaned " + expired.Count + " expired ownership records. Active: " + _entityOwnership.Count);
            SaveShareData();
            try
            {
                if (SyncAllAutoTurrets() || _turretSyncDirty)
                    SaveTurretSync();
            }
            catch (Exception ex)
            {
                PrintWarning("NightshadeLootProtection could not sync turret authorization: " + ex.Message);
            }
        }

        // Rust forced wipe (and any new map save). Clears per-wipe data only.
        // Config and permission grants stay. Legacy files are not imported again.
        private void OnNewSave(string filename)
        {
            ClearPerWipeData("OnNewSave");
            string wipeId = string.IsNullOrEmpty(filename) ? "unknown|pending" : filename + "|pending";
            DateTime created;
            if (TryReadWipe(out string id, out _))
                wipeId = id;
            WriteWipeState(new WipeState { WipeId = wipeId, LegacySettled = true });
            _dataReady = true;
            Puts("NightshadeLootProtection wipe: OnNewSave for '" + (filename ?? "") + "' (wipe id " + wipeId + "). Cleared shares.json, knownplayers.json, and tcturretauth.json. Config and permission grants were kept. Legacy UnifiedLootProtection data will not be imported.");
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity?.net == null) return;
            var uid = entity.net.ID.Value;
            _entityOwnership.Remove(uid);
            _shareData.Remove(uid);           // FIX 1: clean up shares when entity is destroyed
            _immuneCh47s.Remove(uid);         // clean up CH47 immunity if it dies mid-flight
            _pendingSupplyOwners.Remove(uid); // clean up any signal that was destroyed before landing
            if (_turretSync.Remove(uid))
                _turretSyncDirty = true;
        }

        #endregion

        #region Commands

        [ChatCommand("loot")]
        private void CmdLoot(BasePlayer player, string command, string[] args)
        {
            var ray = player.eyes.HeadRay();
            if (!Physics.Raycast(ray, out var hit, 5f)) return;

            var entity = hit.GetEntity();
            if (entity == null)
            {
                player.ChatMessage("No entity found.");
                return;
            }

            var uid = entity.net.ID.Value;
            var msg = new System.Text.StringBuilder();
            msg.AppendLine($"<color=#88D66C>Entity:</color> {entity.ShortPrefabName}");

            if (_entityOwnership.TryGetValue(uid, out var data) && data.OwnerId != 0)
            {
                var remaining = data.LockDurationSeconds > 0
                    ? $"{Math.Max(0, data.LockDurationSeconds - (DateTime.UtcNow - data.LockedAt).TotalSeconds):F0}s remaining"
                    : "no expiry";
                msg.AppendLine($"<color=#88D66C>Owner:</color> {data.OwnerName}");
                msg.AppendLine($"<color=#88D66C>Lock:</color> {remaining}");
                msg.AppendLine($"<color=#88D66C>Contributors:</color> {data.Contributors.Count}");
                msg.AppendLine($"<color=#88D66C>Too Hot:</color> {data.IsTooHot}");
            }
            else
            {
                msg.AppendLine("<color=#88D66C>Ownership:</color> TC-protected (no damage lock)");
            }

            var privs = new List<BuildingPrivlidge>();
            Vis.Entities(entity.transform.position, 16f, privs);
            if (privs.Count > 0)
            {
                msg.AppendLine($"<color=#88D66C>TC Coverage:</color> {privs.Count} cupboard(s)");
                msg.AppendLine($"<color=#88D66C>You are authed:</color> {privs[0].IsAuthed(player)}");
            }
            else
            {
                msg.AppendLine("<color=#88D66C>TC Coverage:</color> None (publicly accessible)");
            }

            if (_shareData.TryGetValue(uid, out var shared) && shared.Count > 0)
            {
                var names = new List<string>();
                foreach (var sid in shared)
                {
                    var p = BasePlayer.FindByID(sid);
                    names.Add(p != null ? p.displayName : $"[offline:{sid}]");
                }
                msg.AppendLine($"<color=#88D66C>Shared with:</color> {string.Join(", ", names)}");
            }
            else
            {
                msg.AppendLine("<color=#88D66C>Shared with:</color> Nobody");
            }

            player.ChatMessage(msg.ToString().TrimEnd());
        }

        [ChatCommand("share")]
        private void CmdShare(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0)
            {
                player.ChatMessage(
                    "<color=#88D66C>Look at a container and use:</color>\n" +
                    "<color=#88D66C>/share <playername></color>  -  Share this container\n" +
                    "<color=#88D66C>/share remove <playername></color>  -  Remove their access\n" +
                    "<color=#88D66C>/share list</color>  -  See who has access to this container\n" +
                    "<color=#88D66C>/share clear</color>  -  Remove all shares from this container");
                return;
            }

            var entity = GetLookedAtEntity(player);
            if (entity == null)
            {
                player.ChatMessage("<color=#A04A4A>You must be looking at a container to use this command.</color>");
                return;
            }

            // FIX 4: contributors can share, not just the TC owner
            if (!PlayerCanShare(player, entity))
            {
                player.ChatMessage("<color=#A04A4A>You do not have permission to manage sharing on this container.</color>");
                return;
            }

            var entityId = entity.net.ID.Value;
            var entityName = entity.ShortPrefabName;

            if (args[0].ToLower() == "list")
            {
                if (!_shareData.TryGetValue(entityId, out var sharedWith) || sharedWith.Count == 0)
                {
                    player.ChatMessage($"No one has been shared on this {entityName}.");
                    return;
                }
                var names = new List<string>();
                foreach (var uid in sharedWith)
                {
                    var p = BasePlayer.FindByID(uid);
                    if (p != null)
                        names.Add(p.displayName);
                    else if (_knownPlayers.TryGetValue(uid, out var knownName))
                        names.Add($"{knownName} [offline]");
                    else
                        names.Add($"[unknown:{uid}]");
                }
                player.ChatMessage($"<color=#88D66C>{entityName} shared with:</color> {string.Join(", ", names)}");
                return;
            }

            if (args[0].ToLower() == "clear")
            {
                _shareData.Remove(entityId);
                SaveShareData();
                player.ChatMessage($"<color=#88D66C>Cleared all shares from this {entityName}.</color>");
                return;
            }

            if (args[0].ToLower() == "remove" && args.Length >= 2)
            {
                var removeInput = string.Join(" ", args, 1, args.Length - 1);
                ulong removeId = 0;
                string removeName = null;
                BasePlayer removeOnline = null;

                if (ulong.TryParse(removeInput, out ulong parsedRemoveId) && parsedRemoveId > 70000000000000000UL)
                {
                    removeId = parsedRemoveId;
                    removeOnline = BasePlayer.FindByID(removeId);
                    removeName = removeOnline?.displayName
                        ?? (_knownPlayers.TryGetValue(removeId, out var kn) ? kn : $"[unknown:{removeId}]");
                }
                else
                {
                    removeOnline = FindPlayerByName(removeInput);
                    if (removeOnline == null)
                    {
                        player.ChatMessage($"<color=#A04A4A>Player '{removeInput}' not found. Use SteamID to remove offline players.</color>");
                        return;
                    }
                    removeId = removeOnline.userID;
                    removeName = removeOnline.displayName;
                }

                if (_shareData.TryGetValue(entityId, out var sharedWith) && sharedWith.Remove(removeId))
                {
                    SaveShareData();
                    player.ChatMessage($"<color=#88D66C>Removed {removeName}'s access to this {entityName}.</color>");
                    removeOnline?.ChatMessage($"<color=#A04A4A>{player.displayName} removed your access to their {entityName}.</color>");
                }
                else
                {
                    player.ChatMessage($"{removeName} did not have access to this container.");
                }
                return;
            }

            // /share <steamid or playername>
            var input = string.Join(" ", args);
            ulong targetId = 0;
            string targetName = null;
            BasePlayer onlineTarget = null;

            // Try SteamID first
            if (ulong.TryParse(input, out ulong parsedId) && parsedId > 70000000000000000UL)
            {
                targetId = parsedId;
                onlineTarget = BasePlayer.FindByID(targetId);
                if (onlineTarget != null)
                    targetName = onlineTarget.displayName;
                else if (_knownPlayers.TryGetValue(targetId, out var kn))
                    targetName = kn;
                else
                    targetName = $"[unknown:{targetId}]";
            }
            else
            {
                // Fall back to name search (online only)
                onlineTarget = FindPlayerByName(input);
                if (onlineTarget == null)
                {
                    player.ChatMessage($"<color=#A04A4A>Player not found. Use their SteamID to share with offline players.</color>");
                    return;
                }
                targetId = onlineTarget.userID;
                targetName = onlineTarget.displayName;
            }

            if (targetId == player.userID)
            {
                player.ChatMessage("You cannot share with yourself.");
                return;
            }

            if (!_shareData.ContainsKey(entityId))
                _shareData[entityId] = new HashSet<ulong>();

            if (_shareData[entityId].Contains(targetId))
            {
                player.ChatMessage($"{targetName} already has access to this container.");
                return;
            }

            _shareData[entityId].Add(targetId);
            SaveShareData();
            player.ChatMessage($"<color=#88D66C>Shared this {entityName} with {targetName}.</color>");
            onlineTarget?.ChatMessage($"<color=#88D66C>{player.displayName} has shared a {entityName} with you.</color>");
        }

        // FIX 4: bumped raycast from 3m to 5m to match loot interaction range
        private BaseEntity GetLookedAtEntity(BasePlayer player)
        {
            var ray = player.eyes.HeadRay();
            if (!Physics.Raycast(ray, out var hit, 5f)) return null;
            return hit.GetEntity();
        }

        private bool PlayerCanShare(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null || entity.net == null) return false;
            if (player.IsAdmin) return true;
            var uid = entity.net.ID.Value;

            // Event owners/contributors may share their event rewards.
            OwnershipData data;
            if (_entityOwnership.TryGetValue(uid, out data) && data.OwnerId != 0UL)
                return data.CanAccess(player.userID);

            // A TC authorization by itself does NOT let a stranger add themselves
            // or others to a private storage box's explicit share list.
            if (IsPlayerOrTeamOwner(player, entity.OwnerID)) return true;
            BuildingPrivlidge tc = GetStorageTc(entity);
            return entity.OwnerID == 0UL && tc != null && IsPlayerOrTeamOwner(player, tc.OwnerID);
        }

        [ChatCommand("ulp")]
        [ChatCommand("nlp")]
        private void CmdUlp(BasePlayer player, string command, string[] args)
        {
            if (!HasAdminPerm(player))
            {
                player.ChatMessage("<color=#A04A4A>You don't have permission to use this command.</color>");
                return;
            }

            if (args.Length == 0)
            {
                player.ChatMessage(
                    "<color=#88D66C>/nlp unlock</color> - Remove ownership lock from entity you're looking at\n" +
                    "<color=#88D66C>/nlp info</color> - Show full ownership info for entity you're looking at\n" +
                    "<color=#88D66C>/nlp weekend</color> - Show global TruePVE, Purge and raid window status\n" +
                    "<color=#88D66C>/nlp check [playername]</color> - Audit access to the box you're looking at\n" +
                    "<color=#88D66C>/ulp</color> - Same commands");
                return;
            }

            if (args[0].Equals("weekend", StringComparison.OrdinalIgnoreCase))
            {
                string mapping = GetTruePveMapping(player) ?? "(none)";
                string globalRule = GetTruePveRuleSet() ?? "(unavailable)";
                bool raidActive;
                string nrp = TryGetRaidWeekendState(out raidActive) ? raidActive.ToString() : "unavailable";
                bool clock = IsEasternWeekendWindow();
                bool forced = IsFullPvPForced();
                bool purge = IsPurgeActive();
                bool bypass = IsWeekendPvp(player);
                bool openBoxes = _config.WeekendPvP != null && _config.WeekendPvP.OpenPlayerStorageToNonTeammates && bypass;
                player.ChatMessage($"<color=#B084FF>Nightshade Access</color> | TruePVE global: {globalRule} | Mapping (diagnostic only): {mapping} | Real ET weekend: {clock} | Full force: {forced} | NRP API: {nrp} | Purge: {purge} | Event loot open: {bypass} | Non-team player boxes: {(openBoxes ? "OPEN" : "CLOSED")} | Sleeper looting: PROTECTED | Code locks: ENFORCED");
                return;
            }

            var entity = GetLookedAtEntity(player);
            if (entity == null)
            {
                player.ChatMessage("<color=#A04A4A>No entity found. Look directly at something within 5 m.</color>");
                return;
            }

            var uid = entity.net.ID.Value;

            if (args[0].Equals("check", StringComparison.OrdinalIgnoreCase))
            {
                BasePlayer subject = player;
                if (args.Length > 1)
                {
                    var find = string.Join(" ", args, 1, args.Length - 1);
                    BasePlayer found = FindPlayerByName(find);
                    if (found == null)
                    {
                        player.ChatMessage("Specified player must be online with a unique matching name.");
                        return;
                    }
                    subject = found;
                }
                if (!IsStorage(entity))
                {
                    player.ChatMessage("/ulp check is for boxes and other protected storage containers.");
                    return;
                }
                string why;
                bool weekdayPermit = CanPlayerUseStorage(subject, entity, out why);
                bool placed = IsPlayerPlacedStorage(entity);
                BuildingPrivlidge tc = OwnBuildingCupboard(entity);
                bool open = IsWeekendPvp(subject);
                bool raidProtected = IsRaidProtectedAgainstOutsiders(entity);
                NightshadeLootRules.OpenRequest request = BuildOpenRequest(subject, entity, IsVendingOpen(subject, entity) || IsDropBoxOpen(subject, entity));
                bool permitted = placed
                    ? NightshadeLootRules.AllowPlayerPlacedOpen(request)
                    : open
                        ? !IsLootProtectedByRaidShield(subject, entity)
                        : IsUtilityPublic(entity) || PublicEntities.Contains(entity.ShortPrefabName) || !_config.StorageProtection.Enabled || weekdayPermit;
                player.ChatMessage($"<color=#B084FF>NLP STORAGE CHECK</color> {subject.displayName} | {entity.ShortPrefabName} | Box owner: {entity.OwnerID} | Own building TC owner: {(tc == null ? 0UL : tc.OwnerID)} | Team: {subject.currentTeam} | Player-placed: {placed} | PvP/Purge: {open} | Outsider raid block: {raidProtected} | Non-team weekend open: {request.OpenPlayerStorageOnWeekend} | Other storage rule: {weekdayPermit} ({why}) | Storage decision: {(permitted ? "ALLOW" : "DENY")} | Code lock: still enforced");
                return;
            }

            if (args[0].ToLower() == "unlock")
            {
                if (!_entityOwnership.TryGetValue(uid, out var data) || data.OwnerId == 0)
                {
                    player.ChatMessage("<color=#A04A4A>This entity has no ownership lock.</color>");
                    return;
                }

                var prevOwner = data.OwnerName;
                _entityOwnership.Remove(uid);
                _shareData.Remove(uid);
                player.ChatMessage($"<color=#88D66C>Unlocked {entity.ShortPrefabName}. Previous owner: {prevOwner}.</color>");
                return;
            }

            if (args[0].ToLower() == "info")
            {
                var msg = new System.Text.StringBuilder();
                msg.AppendLine($"<color=#88D66C>Entity:</color> {entity.ShortPrefabName} (uid: {uid})");

                if (_entityOwnership.TryGetValue(uid, out var data) && data.OwnerId != 0)
                {
                    var remaining = data.LockDurationSeconds > 0
                        ? $"{Math.Max(0, data.LockDurationSeconds - (DateTime.UtcNow - data.LockedAt).TotalSeconds):F0}s remaining"
                        : "no expiry";
                    msg.AppendLine($"<color=#88D66C>Owner:</color> {data.OwnerName} ({data.OwnerId})");
                    msg.AppendLine($"<color=#88D66C>Lock:</color> {remaining}");
                    msg.AppendLine($"<color=#88D66C>Contributors:</color> {data.Contributors.Count}");
                    msg.AppendLine($"<color=#88D66C>Too Hot:</color> {data.IsTooHot}");
                    msg.AppendLine($"<color=#88D66C>Expired:</color> {data.IsExpired}");
                }
                else
                {
                    msg.AppendLine("<color=#88D66C>Ownership:</color> None (TC-protected or public)");
                }

                var privs = new List<BuildingPrivlidge>();
                Vis.Entities(entity.transform.position, 16f, privs);
                msg.AppendLine(privs.Count > 0
                    ? $"<color=#88D66C>TC Coverage:</color> {privs.Count} cupboard(s), you authed: {privs[0].IsAuthed(player)}"
                    : "<color=#88D66C>TC Coverage:</color> None");

                if (_shareData.TryGetValue(uid, out var shared) && shared.Count > 0)
                {
                    var names = new List<string>();
                    foreach (var sid in shared)
                    {
                        var p = BasePlayer.FindByID(sid);
                        if (p != null) names.Add(p.displayName);
                        else if (_knownPlayers.TryGetValue(sid, out var kn)) names.Add($"{kn} [offline]");
                        else names.Add($"[unknown:{sid}]");
                    }
                    msg.AppendLine($"<color=#88D66C>Shared with:</color> {string.Join(", ", names)}");
                }
                else
                {
                    msg.AppendLine("<color=#88D66C>Shared with:</color> Nobody");
                }

                player.ChatMessage(msg.ToString().TrimEnd());
                return;
            }

            player.ChatMessage("<color=#A04A4A>Unknown subcommand. Use /ulp for help.</color>");
        }

        private BasePlayer FindPlayerByName(string name)
        {
            name = name.ToLower();
            BasePlayer found = null;
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p.displayName.ToLower().Contains(name))
                {
                    if (found != null) return null;
                    found = p;
                }
            }
            return found;
        }

        #endregion
    }
#endif
}