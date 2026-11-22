using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("NightshadeDeathBox", "Nightshade", "1.0.2")]
    [Description("Puts a player's death loot in owner-only containers during the PvP weekend.")]
    public class NightshadeDeathBox : RustPlugin
    {
        // Spawned 48-slot item_drop_backpack containers, not a wooden box and not the
        // PlayerCorpse. NightshadeLootProtection 1.3.0 returns null (no opinion) for
        // DroppedItemContainer and LootableCorpse, including while "Disable
        // NightshadeLootProtection access restrictions during PvP weekend" is on.
        // A player-owned wooden box is player-placed storage: that plugin returns
        // false for anyone who is not the owner, the team, or a server admin, and
        // the last non-null CanLootEntity result wins. That false would block
        // nightshadedeathbox.admin and can block the owner after the weekend when
        // the box sits in someone else's tool cupboard. These backpacks avoid that.
        // ResetRemovalTime keeps them from despawning while they still have items.
        // RestoreUponDeath removes the items it will give back before the corpse is
        // filled, so only what actually lands in the corpse or a Backpacks bag is moved.

        private const string PermUse = "nightshadedeathbox.use";
        private const string PermAdmin = "nightshadedeathbox.admin";
        private const string DataPath = "Nightshade/NightshadeDeathBox/boxes";
        private const string BackpackPrefab = "assets/prefabs/misc/item drop/item_drop_backpack.prefab";
        private const string MarkerPrefab = "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";
        private const string LootPanel = "generic_resizable";
        private const ulong MarkerSkin = 29485761021UL;
        private const string NameSuffix = "'s Death Box";
        private const int BoxSlots = 48;
        private const float HoldSeconds = 86400f;
        private const float PendingSeconds = 8f;
        private const int TraceKeep = 12;
        private const int EmptyCorpseRetries = 4;
        private const string RaidbaseZonePrefix = "nightshade_raidbase";

        private PluginConfig _config;
        private Store _store;
        private Timer _maintainTimer;
        private bool _wipeNoted;
        private bool _raidWarned;
        private bool _worldReady;
        private long _nextGroup = 1;

        private readonly Dictionary<ulong, BoxRecord> _byNet = new Dictionary<ulong, BoxRecord>();
        private readonly Dictionary<ulong, PendingDeath> _pending = new Dictionary<ulong, PendingDeath>();
        private readonly Dictionary<long, ulong> _markerByGroup = new Dictionary<long, ulong>();
        private readonly HashSet<ulong> _seenSources = new HashSet<ulong>();
        private readonly List<RecentSource> _recent = new List<RecentSource>();
        private readonly HashSet<ulong> _announce = new HashSet<ulong>();
        private readonly Dictionary<ulong, float> _nextTell = new Dictionary<ulong, float>();
        private readonly List<DeathTrace> _traces = new List<DeathTrace>();

        [PluginReference] private Plugin NightshadeRaidProtection;
        [PluginReference] private Plugin Backpacks;
        [PluginReference] private Plugin RaidableBases;
        [PluginReference] private Plugin AbandonedBases;
        [PluginReference] private Plugin DungeonEvents;
        [PluginReference] private Plugin ZoneManager;

        private class PluginConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Only During PvP Weekend")] public bool OnlyDuringPvpWeekend = true;
            [JsonProperty("Allow Team To Loot")] public bool AllowTeamToLoot = true;
            [JsonProperty("Map Marker")] public bool MapMarker = true;
            [JsonProperty("Max Lifetime Hours")] public float MaxLifetimeHours = 0f;
            [JsonProperty("Remove protected boxes after the owner empties them")] public bool RemoveWhenEmpty = true;
            [JsonProperty("Convert disconnected sleepers")] public bool ConvertDisconnectedSleepers = true;
            [JsonProperty("Convert connected sleeping players")] public bool ConvertConnectedSleepers = true;
            [JsonProperty("Convert awake players")] public bool ConvertAwakePlayers = true;
            [JsonProperty("Protect suicide deaths")] public bool ProtectSuicideDeaths = false;
            [JsonProperty("Skip safe zone deaths")] public bool SkipSafeZones = true;
            [JsonProperty("Skip event and raid zone deaths")] public bool SkipEventZones = true;
            [JsonProperty("Convert separately dropped Backpacks plugin bags")] public bool ConvertSeparateBackpacks = true;
            [JsonProperty("Force Backpacks plugin bags to drop on death (overrides keep-on-death)")] public bool ForceBackpackDrop = true;
            [JsonProperty("Maximum 48-slot boxes per death")] public int MaxBoxesPerDeath = 5;
            [JsonProperty("Vertical spawn offset in meters")] public float VerticalOffset = 0.06f;
            [JsonProperty("Box spacing meters")] public float BoxSpacing = 0.75f;
            [JsonProperty("Log successful conversions")] public bool LogConversions = true;
            [JsonProperty("Log backpack conversion diagnostics")] public bool LogBackpackDiagnostics = false;
            [JsonProperty("Debug Logging")] public bool DebugLogging = false;
        }

        private class Store
        {
            public string SaveId;
            public long NextGroupId = 1;
            public List<BoxRecord> Boxes = new List<BoxRecord>();
        }

        private class BoxRecord
        {
            public ulong NetId;
            public ulong OwnerId;
            public string OwnerName;
            public float X;
            public float Y;
            public float Z;
            public long CreatedUtcTicks;
            public long GroupId;
        }

        private class PendingDeath
        {
            public ulong UserId;
            public string Name;
            public Vector3 Position;
            public long GroupId;
            public int Spawned;
            public int ItemsAtDeath;
            public int Moved;
            public float Until;
            public string Pvp;
            public bool HasPermission;
            public string Result;
            public string EmptyNote;
            public bool Traced;
            public bool FailureNoted;
        }

        private class DeathTrace
        {
            public string When;
            public ulong UserId;
            public string Name;
            public string Result;
            public string Reason;
            public string Pvp;
            public bool HasPermission;
            public int ItemCount;
            public int Boxes;
        }

        private class RecentSource
        {
            public ulong NetId;
            public ulong OwnerId;
            public float Until;
        }

        #region Config and data

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null) throw new Exception("Config was empty.");
            }
            catch (Exception ex)
            {
                PrintWarning("Config could not be read. Loading defaults. " + ex.Message);
                LoadDefaultConfig();
            }
            if (_config.MaxBoxesPerDeath < 1) _config.MaxBoxesPerDeath = 1;
            if (_config.MaxBoxesPerDeath > 30) _config.MaxBoxesPerDeath = 30;
            if (float.IsNaN(_config.MaxLifetimeHours) || _config.MaxLifetimeHours < 0f) _config.MaxLifetimeHours = 0f;
            if (float.IsNaN(_config.VerticalOffset)) _config.VerticalOffset = 0.06f;
            if (float.IsNaN(_config.BoxSpacing) || _config.BoxSpacing < 0.2f) _config.BoxSpacing = 0.75f;
            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Prefix"] = "<color=#c9b27c>[Death Box]</color> ",
                ["StoredOne"] = "Your loot is in a protected box at {0}. /deathbox lists it.",
                ["StoredMany"] = "Your loot is in {0} protected boxes at {1}. /deathbox lists them.",
                ["Blocked"] = "This death box belongs to {0}.",
                ["Break"] = "You cannot break this death box.",
                ["None"] = "You have no death boxes.",
                ["NoPerm"] = "You do not have permission to use death boxes.",
                ["Header"] = "Your death boxes:",
                ["AdminHeader"] = "Death boxes for {0}:",
                ["Line"] = "{0}. {1} - {2} items - {3}",
                ["More"] = "... and {0} more. Stand at a box to loot it.",
                ["Expired"] = "A death box of yours reached its time limit and was removed.",
                ["NotAdmin"] = "You need the nightshadedeathbox.admin permission to look up another player.",
                ["BadId"] = "Usage: /deathbox   or   /deathbox <steamid>"
            }, this);
        }

        private void LoadData()
        {
            try
            {
                _store = Interface.Oxide.DataFileSystem.ReadObject<Store>(DataPath) ?? new Store();
            }
            catch (Exception ex)
            {
                PrintWarning("Death box data could not be read. Starting empty. " + ex.Message);
                _store = new Store();
            }
            if (_store.Boxes == null) _store.Boxes = new List<BoxRecord>();
            if (_store.NextGroupId < 1) _store.NextGroupId = 1;
            _nextGroup = _store.NextGroupId;
        }

        private void SaveData()
        {
            if (_store == null) _store = new Store();
            _store.NextGroupId = _nextGroup;
            _store.Boxes = new List<BoxRecord>(_byNet.Values);
            Interface.Oxide.DataFileSystem.WriteObject(DataPath, _store);
        }

        #endregion

        #region Hooks

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermAdmin, this);
            LoadData();
            cmd.AddChatCommand("deathbox", this, nameof(CmdDeathBoxChat));
            cmd.AddConsoleCommand("deathbox", this, nameof(CmdDeathBoxConsole));
            cmd.AddConsoleCommand("nsdeathbox.status", this, nameof(CmdDeathBoxStatus));
        }

        private void OnServerInitialized()
        {
            bool wiped = ApplyWipe();
            if (wiped) DestroyWorldBoxes();
            else RebindWorld();
            _worldReady = true;
            _maintainTimer = timer.Every(60f, Maintain);
            Puts("NightshadeDeathBox 1.0.2. New boxes follow NightshadeRaidProtection.API_IsPvPActive (Eastern weekend, test clock, and nsraid.pvpweekend). Debug: /deathbox debug");
        }

        private void Unload()
        {
            _maintainTimer?.Destroy();
            KillAllMarkers();
            if (_worldReady) SaveData();
        }

        private void OnServerSave()
        {
            if (_worldReady) SaveData();
        }

        private void OnNewSave(string filename)
        {
            _wipeNoted = true;
            _byNet.Clear();
            _pending.Clear();
            _announce.Clear();
            KillAllMarkers();
            if (_store == null) _store = new Store();
            _store.Boxes.Clear();
            _store.SaveId = null;
            _nextGroup = 1;
            SaveData();
            Puts("Map wipe: death boxes cleared.");
        }

        private void OnEntityDeath(BasePlayer player, HitInfo info)
        {
            HandleDeath(player, info);
        }

        // Safe-zone sleeper removal and some plugin kills use this instead of OnEntityDeath.
        private void OnEntityKill(BasePlayer player)
        {
            HandleDeath(player, null);
        }

        private void OnEntityKill(DroppedItemContainer container)
        {
            Forget(container);
        }

        private void OnEntityKill(LootableCorpse corpse)
        {
            Forget(corpse);
        }

        private void OnPlayerCorpseSpawned(BasePlayer player, PlayerCorpse corpse)
        {
            ScheduleCorpse(corpse);
        }

        private void OnEntitySpawned(PlayerCorpse corpse)
        {
            ScheduleCorpse(corpse);
        }

        private void OnEntitySpawned(DroppedItemContainer container)
        {
            if (container == null || container.net == null || IsOurPrefab(container)) return;
            if (!string.Equals(container.ShortPrefabName, "item_drop_backpack", StringComparison.OrdinalIgnoreCase)) return;
            NoteRecent(container, container.playerSteamID);
            NextTick(() => AbsorbVanillaBackpack(container));
        }

        private void OnBackpackDropped(ulong ownerId, List<DroppedItemContainer> containers)
        {
            if (containers == null || containers.Count == 0) return;
            List<DroppedItemContainer> copy = new List<DroppedItemContainer>(containers);
            NextTick(() => AbsorbPluginBags(ownerId, copy));
        }

        private object CanLootEntity(BasePlayer player, DroppedItemContainer container)
        {
            return LootDecision(player, container);
        }

        private object CanLootEntity(BasePlayer player, LootableCorpse corpse)
        {
            return LootDecision(player, corpse);
        }

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null || !IsTracked(entity)) return;
            BoxRecord record;
            if (!TryRecord(entity, out record) || CanAccess(player, record)) return;
            NextTick(() => CloseLoot(player, record));
        }

        private object CanMoveItem(Item item, PlayerInventory inventory, ItemContainerId targetContainerId, int targetSlot, int amount)
        {
            if (item == null) return null;
            BaseEntity entity = item.parent == null ? null : item.parent.entityOwner;
            if (entity == null || !IsTracked(entity)) return null;
            BasePlayer player = inventory == null ? null : inventory.baseEntity;
            BoxRecord record;
            if (!TryRecord(entity, out record)) return null;
            if (player != null && CanAccess(player, record)) return null;
            return false;
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || !IsTracked(entity)) return null;
            if (info != null && info.damageTypes != null) info.damageTypes.ScaleAll(0f);
            BasePlayer attacker = info == null ? null : info.InitiatorPlayer;
            BoxRecord record;
            if (attacker != null && attacker.userID.IsSteamId() && TryRecord(entity, out record))
                Tell(attacker, "Break", record.OwnerName);
            return true;
        }

        // TruePVE treats false as a block. null would let a weekend ruleset apply the hit.
        private object CanEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity != null && IsTracked(entity)) return false;
            return null;
        }

        private object canRemove(BasePlayer player, BaseEntity entity)
        {
            if (entity != null && IsTracked(entity)) return false;
            return null;
        }

        private object CanPickupEntity(BasePlayer player, BaseCombatEntity entity)
        {
            if (entity != null && IsTracked(entity)) return false;
            return null;
        }

        private void OnItemRemovedFromContainer(ItemContainer container, Item item)
        {
            BaseEntity entity = container == null ? null : container.entityOwner;
            if (entity == null || !IsTracked(entity)) return;
            NextTick(() => TryRemoveIfEmpty(entity));
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            timer.Once(3f, () =>
            {
                if (player == null || !player.IsConnected) return;
                SendMarkers(player);
                Announce(player);
            });
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null) return;
            timer.Once(1f, () =>
            {
                if (player == null || !player.IsConnected) return;
                SendMarkers(player);
                Announce(player);
            });
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            if (player == null) return;
            timer.Once(1f, () =>
            {
                if (player == null || !player.IsConnected) return;
                Announce(player);
            });
        }

        #endregion

        #region Death

        private void HandleDeath(BasePlayer player, HitInfo info)
        {
            if (!_worldReady || player == null || player.IsDestroyed || player.IsNpc || !player.userID.IsSteamId()) return;
            PendingDeath existing;
            if (_pending.TryGetValue(player.userID, out existing) && existing != null && existing.Until >= Time.realtimeSinceStartup)
                return;

            string pvp;
            bool pvpActive = QueryPvP(out pvp);
            bool hasPerm = permission.UserHasPermission(player.UserIDString, PermUse);
            int items = CountPlayerItems(player);
            string skip;
            if (!ShouldConvert(player, info, pvpActive, pvp, hasPerm, out skip))
            {
                NoteDeath(player.userID, player.displayName, "skipped", skip, pvp, hasPerm, items, 0, IsLegacySkip(skip));
                return;
            }

            PendingDeath session = new PendingDeath
            {
                UserId = player.userID,
                Name = string.IsNullOrEmpty(player.displayName) ? player.userID.ToString() : player.displayName,
                Position = player.transform.position,
                GroupId = NextGroup(),
                Spawned = 0,
                ItemsAtDeath = items,
                Moved = 0,
                Until = Time.realtimeSinceStartup + PendingSeconds,
                Pvp = pvp,
                HasPermission = hasPerm,
                Result = null
            };
            _pending[player.userID] = session;
            if (_config.ForceBackpackDrop) ForceBackpack(player, session);
            ulong ownerId = player.userID;
            // CreateCorpse copies the inventory before Spawn. NextTick still catches a bag that spawned earlier in OnDied.
            NextTick(() => DrainRecent(ownerId));
            timer.Once(PendingSeconds, () => ClosePending(ownerId));
        }

        private void ClosePending(ulong userId)
        {
            PendingDeath session;
            if (!_pending.TryGetValue(userId, out session) || session == null) return;
            if (string.IsNullOrEmpty(session.Result) && !session.FailureNoted)
            {
                string why = string.IsNullOrEmpty(session.EmptyNote) ? "no corpse with items within 8s" : session.EmptyNote;
                FailSession(session, why);
            }
            _pending.Remove(userId);
        }

        private bool ShouldConvert(BasePlayer player, HitInfo info, out string skip)
        {
            string pvp;
            bool pvpActive = QueryPvP(out pvp);
            bool hasPerm = player != null && permission.UserHasPermission(player.UserIDString, PermUse);
            return ShouldConvert(player, info, pvpActive, pvp, hasPerm, out skip);
        }

        private bool ShouldConvert(BasePlayer player, HitInfo info, bool pvpActive, string pvpDetail, bool hasPerm, out string skip)
        {
            skip = null;
            if (player == null) { skip = "player missing"; return false; }
            if (_config == null || !_config.Enabled) { skip = "plugin disabled"; return false; }
            if (_config.OnlyDuringPvpWeekend && !pvpActive) { skip = "pvp weekend inactive (" + pvpDetail + ")"; return false; }
            if (!hasPerm) { skip = "no permission " + PermUse; return false; }

            bool sleeping = player.IsSleeping();
            bool connected = player.IsConnected;
            if (sleeping && !connected)
            {
                if (!_config.ConvertDisconnectedSleepers) { skip = "disconnected sleeper"; return false; }
            }
            else if (sleeping)
            {
                if (!_config.ConvertConnectedSleepers) { skip = "connected sleeper"; return false; }
            }
            else if (!_config.ConvertAwakePlayers)
            {
                skip = "awake player";
                return false;
            }

            if (!_config.ProtectSuicideDeaths && IsExplicitSuicide(player, info))
            {
                skip = "explicit suicide";
                return false;
            }
            if (_config.SkipSafeZones && player.InSafeZone())
            {
                skip = "safe zone";
                return false;
            }
            if (_config.SkipEventZones && InEventZone(player, player.transform.position))
            {
                skip = "event or raid zone";
                return false;
            }
            return true;
        }

        // /kill while standing. A downed player, a bleed-out, and a sleeper are not this.
        private static bool IsExplicitSuicide(BasePlayer player, HitInfo info)
        {
            if (player == null || info == null || info.damageTypes == null) return false;
            if (player.IsSleeping() || player.IsWounded()) return false;
            if (player.HasPlayerFlag(BasePlayer.PlayerFlags.Wounded)) return false;
            if (info.damageTypes.Get(Rust.DamageType.Bleeding) > 0f) return false;
            if (info.damageTypes.GetMajorityDamageType() != Rust.DamageType.Suicide) return false;
            BasePlayer initiator = info.InitiatorPlayer;
            return initiator == null || initiator == player;
        }

        // detail is what the status command prints: true, false, null, a type name, or why the plugin was not called.
        private bool QueryPvP(out string detail)
        {
            detail = "not checked";
            if (_config != null && !_config.OnlyDuringPvpWeekend)
            {
                detail = "weekend check off";
                return true;
            }
            if (NightshadeRaidProtection == null || !NightshadeRaidProtection.IsLoaded)
            {
                detail = NightshadeRaidProtection == null ? "NightshadeRaidProtection reference null" : "NightshadeRaidProtection not loaded";
                if (!_raidWarned)
                {
                    _raidWarned = true;
                    PrintWarning("NightshadeRaidProtection is not loaded. Only During PvP Weekend is on, so no new death boxes will be created.");
                }
                return false;
            }
            try
            {
                object value = NightshadeRaidProtection.Call("API_IsPvPActive");
                if (value is bool flag)
                {
                    detail = flag ? "true" : "false";
                    return flag;
                }
                detail = value == null ? "null" : value.GetType().FullName;
                return false;
            }
            catch (Exception ex)
            {
                detail = "threw " + ex.GetType().Name;
                PrintWarning("API_IsPvPActive failed: " + ex.Message);
                return false;
            }
        }

        private bool InEventZone(BasePlayer player, Vector3 position)
        {
            if (player != null && Interface.CallHook("isEventPlayer", player) != null) return true;
            if (CallBool(RaidableBases, "EventTerritory", position)) return true;
            if (CallBool(AbandonedBases, "EventTerritory", position)) return true;
            if (player != null && InDungeon(player)) return true;
            return InNightshadeRaidbaseZone(position);
        }

        private bool InDungeon(BasePlayer player)
        {
            if (DungeonEvents == null || !DungeonEvents.IsLoaded) return false;
            try
            {
                return DungeonEvents.Call("GetDungeonForPlayer", player) != null;
            }
            catch
            {
                return false;
            }
        }

        private bool InNightshadeRaidbaseZone(Vector3 position)
        {
            if (ZoneManager == null || !ZoneManager.IsLoaded) return false;
            string[] ids;
            try
            {
                ids = ZoneManager.Call("GetZoneIDs") as string[];
            }
            catch
            {
                return false;
            }
            if (ids == null) return false;
            for (int i = 0; i < ids.Length; i++)
            {
                string id = ids[i];
                if (string.IsNullOrEmpty(id) || id.IndexOf(RaidbaseZonePrefix, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (CallBool(ZoneManager, "IsPositionInZone", id, position)) return true;
            }
            return false;
        }

        private static bool CallBool(Plugin plugin, string method, params object[] args)
        {
            if (plugin == null || !plugin.IsLoaded) return false;
            try
            {
                object value = plugin.Call(method, args);
                return value is bool && (bool)value;
            }
            catch
            {
                return false;
            }
        }

        private void ForceBackpack(BasePlayer player, PendingDeath session)
        {
            if (player == null || Backpacks == null || !Backpacks.IsLoaded) 
            {
                if (_config.LogBackpackDiagnostics)
                    Puts("Backpack force skipped for " + session.UserId + ": Backpacks plugin is not loaded.");
                return;
            }
            try
            {
                object result = Backpacks.Call("API_DropBackpack", player);
                if (_config.LogBackpackDiagnostics)
                    Puts("API_DropBackpack for " + session.UserId + " returned " + (result == null ? "null" : result.GetType().Name) + ".");
            }
            catch (Exception ex)
            {
                PrintWarning("API_DropBackpack failed for " + session.UserId + ": " + ex.Message);
            }
        }

        #endregion

        #region Absorb

        private void ScheduleCorpse(PlayerCorpse corpse)
        {
            if (corpse == null || corpse.net == null) return;
            NoteRecent(corpse, corpse.playerSteamID);
            NextTick(() => AbsorbCorpse(corpse));
        }

        private void AbsorbCorpse(PlayerCorpse corpse)
        {
            AbsorbCorpse(corpse, 0);
        }

        private void AbsorbCorpse(PlayerCorpse corpse, int attempt)
        {
            if (corpse == null || corpse.IsDestroyed || corpse.net == null) return;
            PendingDeath session;
            if (!_pending.TryGetValue(corpse.playerSteamID, out session) || session == null || session.Until < Time.realtimeSinceStartup)
                return;
            if (session.Result == "skipped") return;
            // TakeFrom runs before Spawn, so the containers usually already have items.
            // Retry anyway: another plugin can clear them in the same tick, and a first look can still be empty.
            if (corpse.containers == null || IsEmptyCorpse(corpse))
            {
                if (attempt < EmptyCorpseRetries)
                {
                    timer.Once(0.25f, () => AbsorbCorpse(corpse, attempt + 1));
                    return;
                }
                session.EmptyNote = "corpse stayed empty (player inventory " + CountPlayerItems(FindPlayer(session.UserId)) + ")";
                return;
            }
            if (!_seenSources.Add(corpse.net.ID.Value)) return;
            List<Item> items = TakeCorpseItems(corpse);
            int moved = MoveItems(session, items);
            if (moved > 0 && IsEmptyCorpse(corpse))
            {
                // DoServerDestroy spawns item_drop_backpack unless blockBagDrop is set.
                // Set only after the items have already been moved off this corpse.
                corpse.blockBagDrop = true;
                corpse.Kill();
            }
            else if (moved > 0 && !IsEmptyCorpse(corpse))
            {
                // Cap was hit. Keep the leftover private instead of leaving a public corpse.
                TrackOverflow(session, corpse);
                HoldCorpse(corpse);
            }
            else if (moved == 0)
            {
                ReapEmptyBoxes(session);
                NoteMoveFailure(session, "corpse had " + items.Count.ToString(CultureInfo.InvariantCulture) + " items and none moved into a box");
                return;
            }
            FinishAbsorb(session, moved, "corpse");
        }

        private void AbsorbVanillaBackpack(DroppedItemContainer container)
        {
            if (container == null || container.IsDestroyed || container.net == null) return;
            if (IsOurPrefab(container) || IsTracked(container)) return;
            if (!string.Equals(container.ShortPrefabName, "item_drop_backpack", StringComparison.OrdinalIgnoreCase)) return;
            NoteRecent(container, container.playerSteamID);
            PendingDeath session;
            if (!_pending.TryGetValue(container.playerSteamID, out session) || session == null || session.Until < Time.realtimeSinceStartup)
                return;
            if (!_seenSources.Add(container.net.ID.Value)) return;
            int moved = AbsorbSource(session, container);
            FinishAbsorb(session, moved, "vanilla backpack");
        }

        private void AbsorbPluginBags(ulong ownerId, List<DroppedItemContainer> containers)
        {
            PendingDeath session;
            bool pending = _pending.TryGetValue(ownerId, out session) && session != null && session.Until >= Time.realtimeSinceStartup;
            if (!pending)
            {
                if (!_config.ConvertSeparateBackpacks) return;
                BasePlayer player = FindPlayer(ownerId);
                string skip = null;
                if (player == null || !ShouldConvert(player, null, out skip))
                {
                    if (_config.LogBackpackDiagnostics)
                        Puts("Separate backpack for " + ownerId + " was not converted" + (skip == null ? "." : ": " + skip));
                    return;
                }
                Vector3 where = player.transform.position;
                for (int i = 0; i < containers.Count; i++)
                {
                    if (containers[i] != null && !containers[i].IsDestroyed)
                    {
                        where = containers[i].transform.position;
                        break;
                    }
                }
                // Not stored in _pending. A manual drop must not swallow a real death in the next few seconds.
                session = new PendingDeath
                {
                    UserId = ownerId,
                    Name = string.IsNullOrEmpty(player.displayName) ? ownerId.ToString() : player.displayName,
                    Position = where,
                    GroupId = NextGroup(),
                    Spawned = 0,
                    Until = Time.realtimeSinceStartup + PendingSeconds
                };
            }

            int moved = 0;
            for (int i = 0; i < containers.Count; i++)
            {
                DroppedItemContainer container = containers[i];
                if (container == null || container.IsDestroyed || container.net == null) continue;
                if (!_seenSources.Add(container.net.ID.Value)) continue;
                moved += AbsorbSource(session, container);
            }
            if (moved > 0) FinishAbsorb(session, moved, "plugin backpack");
        }

        private void NoteRecent(BaseEntity entity, ulong ownerId)
        {
            if (entity == null || entity.net == null || ownerId == 0UL) return;
            ulong netId = entity.net.ID.Value;
            float until = Time.realtimeSinceStartup + 5f;
            for (int i = 0; i < _recent.Count; i++)
            {
                if (_recent[i].NetId != netId) continue;
                _recent[i].Until = until;
                return;
            }
            _recent.Add(new RecentSource { NetId = netId, OwnerId = ownerId, Until = until });
        }

        private void DrainRecent(ulong ownerId)
        {
            for (int i = _recent.Count - 1; i >= 0; i--)
            {
                RecentSource source = _recent[i];
                if (source.Until < Time.realtimeSinceStartup)
                {
                    _recent.RemoveAt(i);
                    continue;
                }
                if (source.OwnerId != ownerId) continue;
                _recent.RemoveAt(i);
                BaseEntity entity = FindEntity(source.NetId);
                if (entity == null || entity.IsDestroyed) continue;
                PlayerCorpse corpse = entity as PlayerCorpse;
                if (corpse != null) AbsorbCorpse(corpse);
                else
                {
                    DroppedItemContainer drop = entity as DroppedItemContainer;
                    if (drop != null) AbsorbVanillaBackpack(drop);
                }
            }
        }

        private int AbsorbSource(PendingDeath session, DroppedItemContainer source)
        {
            int before = source.inventory == null || source.inventory.itemList == null ? 0 : source.inventory.itemList.Count;
            List<Item> items = TakeContainer(source.inventory);
            int moved = MoveItems(session, items);
            if (moved > 0 && IsEmpty(source.inventory))
            {
                source.Kill();
            }
            else if (moved > 0)
            {
                TrackOverflow(session, source);
                HoldDrop(source);
            }
            else if (before > 0)
            {
                ReapEmptyBoxes(session);
                NoteMoveFailure(session, "backpack had " + before.ToString(CultureInfo.InvariantCulture) + " items and none moved into a box");
            }
            return moved;
        }

        private int MoveItems(PendingDeath session, List<Item> items)
        {
            if (items == null || items.Count == 0) return 0;
            int moved = 0;
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                if (item == null) continue;
                DroppedItemContainer box = FindOpenBox(session);
                if (box == null)
                {
                    if (session.Spawned >= _config.MaxBoxesPerDeath) break;
                    box = SpawnBox(session);
                }
                if (box == null || box.inventory == null) break;
                if (!TryPlace(session, box, item))
                {
                    if (session.Spawned >= _config.MaxBoxesPerDeath) break;
                    DroppedItemContainer next = SpawnBox(session);
                    if (next == null || next.inventory == null || !TryPlace(session, next, item))
                        break;
                }
                moved++;
            }
            ReapEmptyBoxes(session);
            return moved;
        }

        private bool TryPlace(PendingDeath session, DroppedItemContainer box, Item item)
        {
            bool wasEmpty = box.inventory.itemList == null || box.inventory.itemList.Count == 0;
            if (!item.MoveToContainer(box.inventory)) return false;
            if (wasEmpty) NoteBox(session, box);
            return true;
        }

        private DroppedItemContainer FindOpenBox(PendingDeath session)
        {
            foreach (BoxRecord record in _byNet.Values)
            {
                if (record.GroupId != session.GroupId) continue;
                DroppedItemContainer box = FindDrop(record.NetId);
                if (box == null || box.inventory == null) continue;
                int count = box.inventory.itemList == null ? 0 : box.inventory.itemList.Count;
                if (count < box.inventory.capacity) return box;
            }
            return null;
        }

        private DroppedItemContainer SpawnBox(PendingDeath session)
        {
            Vector3 position = Grounded(session.Position, session.Spawned);
            Quaternion rotation = Quaternion.Euler(0f, (session.Spawned * 35f) % 360f, 0f);
            BaseEntity entity = GameManager.server.CreateEntity(BackpackPrefab, position, rotation);
            DroppedItemContainer box = entity as DroppedItemContainer;
            if (box == null)
            {
                if (entity != null) entity.Kill();
                PrintWarning("Could not spawn a death box for " + session.UserId + ".");
                return null;
            }

            box.lootPanelName = LootPanel;
            box.playerName = TrimName(session.Name) + NameSuffix;
            box.playerSteamID = session.UserId;
            box.OwnerID = session.UserId;
            box.skinID = MarkerSkin;
            box.enableSaving = true;
            box.inventory = new ItemContainer();
            box.inventory.ServerInitialize(null, BoxSlots);
            box.inventory.GiveUID();
            PrepareBoxInventory(box);
            StripGroundWatch(box);
            box.Spawn();
            if (box.inventory != null) PrepareBoxInventory(box);
            HoldDrop(box);

            if (box.net == null)
            {
                box.Kill();
                PrintWarning("Death box spawned without a net id for " + session.UserId + ".");
                return null;
            }

            BoxRecord record = new BoxRecord
            {
                NetId = box.net.ID.Value,
                OwnerId = session.UserId,
                OwnerName = session.Name,
                X = box.transform.position.x,
                Y = box.transform.position.y,
                Z = box.transform.position.z,
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
                GroupId = session.GroupId
            };
            _byNet[record.NetId] = record;
            session.Spawned++;
            EnsureMarker(session, box.transform.position);
            return box;
        }

        private void NoteBox(PendingDeath session, DroppedItemContainer box)
        {
            if (session == null || box == null || _config == null) return;
            if (!_config.DebugLogging && !_config.LogConversions) return;
            Puts("Death box created for " + session.Name + " (" + session.UserId + ") at " + GridOf(box.transform.position) + ".");
        }

        // Item.MoveToContainer rejects an item when its volume is greater than containerVolume.
        // A fresh container has volume 0. Vanilla backpacks copy the source volume before the move.
        // A death box is limited by the 48 slots, so the volume ceiling is left open.
        private static void PrepareBoxInventory(DroppedItemContainer box)
        {
            if (box == null || box.inventory == null) return;
            if (box.inventory.capacity < BoxSlots) box.inventory.capacity = BoxSlots;
            box.inventory.containerVolume = int.MaxValue;
            box.inventory.entityOwner = box;
            box.inventory.SetFlag(ItemContainer.Flag.NoItemInput, true);
            box.inventory.SetFlag(ItemContainer.Flag.DroppedItemContainer, true);
        }

        private void TrackOverflow(PendingDeath session, BaseEntity entity)
        {
            if (entity == null || entity.net == null) return;
            if (_byNet.ContainsKey(entity.net.ID.Value)) return;
            DroppedItemContainer drop = entity as DroppedItemContainer;
            if (drop != null)
            {
                drop.playerName = TrimName(session.Name) + NameSuffix;
                drop.playerSteamID = session.UserId;
                drop.OwnerID = session.UserId;
                drop.skinID = MarkerSkin;
                drop.enableSaving = true;
                if (drop.inventory != null) drop.inventory.SetFlag(ItemContainer.Flag.NoItemInput, true);
            }
            else
            {
                entity.OwnerID = session.UserId;
                SuppressDestroyLoot(entity);
            }
            BoxRecord record = new BoxRecord
            {
                NetId = entity.net.ID.Value,
                OwnerId = session.UserId,
                OwnerName = session.Name,
                X = entity.transform.position.x,
                Y = entity.transform.position.y,
                Z = entity.transform.position.z,
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
                GroupId = session.GroupId
            };
            _byNet[record.NetId] = record;
            if (_config.LogConversions)
                Puts("Death box cap (" + _config.MaxBoxesPerDeath + ") hit for " + session.UserId + ". Leftover container kept private.");
        }

        private void FinishAbsorb(PendingDeath session, int moved, string kind)
        {
            if (session == null || moved <= 0) return;
            session.Moved += moved;
            session.Result = "converted";
            _announce.Add(session.UserId);
            BasePlayer online = FindPlayer(session.UserId);
            if (online != null && online.IsConnected && !online.IsDead()) Announce(online);
            SaveData();
            if (_config.LogConversions)
                Puts("Stored " + moved + " items for " + session.Name + " (" + session.UserId + ") from " + kind + " at " + GridOf(session.Position) + ".");
            string reason = kind + ", " + session.Moved + " items moved";
            if (!session.Traced)
            {
                session.Traced = true;
                NoteDeath(session.UserId, session.Name, "converted", reason, session.Pvp, session.HasPermission, session.ItemsAtDeath, session.Spawned, false);
            }
            else
            {
                TouchTrace(session, reason);
            }
        }

        private static List<Item> TakeContainer(ItemContainer container)
        {
            List<Item> items = new List<Item>();
            if (container == null || container.itemList == null) return items;
            for (int i = 0; i < container.itemList.Count; i++)
            {
                Item item = container.itemList[i];
                if (item != null) items.Add(item);
            }
            return items;
        }

        private static List<Item> TakeCorpseItems(PlayerCorpse corpse)
        {
            List<Item> items = new List<Item>();
            if (corpse == null || corpse.containers == null) return items;
            for (int i = 0; i < corpse.containers.Length; i++)
            {
                List<Item> part = TakeContainer(corpse.containers[i]);
                for (int n = 0; n < part.Count; n++) items.Add(part[n]);
            }
            return items;
        }

        private static bool IsEmpty(ItemContainer container)
        {
            return container == null || container.itemList == null || container.itemList.Count == 0;
        }

        private static bool IsEmptyCorpse(PlayerCorpse corpse)
        {
            if (corpse == null || corpse.containers == null) return true;
            for (int i = 0; i < corpse.containers.Length; i++)
            {
                if (!IsEmpty(corpse.containers[i])) return false;
            }
            return true;
        }

        #endregion

        #region Protection

        private object LootDecision(BasePlayer player, BaseEntity entity)
        {
            BoxRecord record;
            if (!TryRecord(entity, out record)) return null;
            if (player != null && CanAccess(player, record)) return null;
            if (player != null) Tell(player, "Blocked", record.OwnerName);
            return false;
        }

        private bool CanAccess(BasePlayer player, BoxRecord record)
        {
            if (player == null || record == null) return false;
            if (permission.UserHasPermission(player.UserIDString, PermAdmin)) return true;
            if (player.userID == record.OwnerId) return true;
            if (_config.AllowTeamToLoot && SameTeam(player.userID, record.OwnerId)) return true;
            return false;
        }

        private static bool SameTeam(ulong left, ulong right)
        {
            if (left == 0UL || right == 0UL || left == right) return false;
            RelationshipManager manager = RelationshipManager.ServerInstance;
            if (manager == null) return false;
            RelationshipManager.PlayerTeam team = manager.FindPlayersTeam(left);
            return team != null && team.members != null && team.members.Contains(right);
        }

        private void CloseLoot(BasePlayer player, BoxRecord record)
        {
            if (player == null || player.inventory == null || player.inventory.loot == null) return;
            player.inventory.loot.Clear();
            player.inventory.loot.MarkDirty();
            player.inventory.loot.SendImmediate();
            if (record != null) Tell(player, "Blocked", record.OwnerName);
        }

        private void Tell(BasePlayer player, string key, string ownerName)
        {
            if (player == null) return;
            float now = Time.realtimeSinceStartup;
            float next;
            if (_nextTell.TryGetValue(player.userID, out next) && next > now) return;
            _nextTell[player.userID] = now + 2f;
            player.ChatMessage(Msg("Prefix", player.UserIDString) + Msg(key, player.UserIDString, ownerName ?? "another player"));
        }

        private bool IsTracked(BaseEntity entity)
        {
            BoxRecord ignored;
            return TryRecord(entity, out ignored);
        }

        private bool TryRecord(BaseEntity entity, out BoxRecord record)
        {
            record = null;
            if (entity == null || entity.net == null) return false;
            return _byNet.TryGetValue(entity.net.ID.Value, out record);
        }

        private static bool IsOurPrefab(DroppedItemContainer container)
        {
            if (container == null) return false;
            if (container.skinID == MarkerSkin) return true;
            string name = container.playerName;
            return !string.IsNullOrEmpty(name) && name.EndsWith(NameSuffix, StringComparison.Ordinal);
        }

        private void TryRemoveIfEmpty(BaseEntity entity)
        {
            if (!_config.RemoveWhenEmpty || entity == null || entity.IsDestroyed) return;
            if (!IsTracked(entity)) return;
            if (!EntityEmpty(entity)) return;
            SuppressDestroyLoot(entity);
            entity.Kill();
        }

        private static bool EntityEmpty(BaseEntity entity)
        {
            DroppedItemContainer drop = entity as DroppedItemContainer;
            if (drop != null) return IsEmpty(drop.inventory);
            LootableCorpse corpse = entity as LootableCorpse;
            if (corpse != null && corpse.containers != null)
            {
                for (int i = 0; i < corpse.containers.Length; i++)
                {
                    if (!IsEmpty(corpse.containers[i])) return false;
                }
                return true;
            }
            StorageContainer storage = entity as StorageContainer;
            if (storage != null) return IsEmpty(storage.inventory);
            return false;
        }

        private void Forget(BaseEntity entity)
        {
            if (entity == null || entity.net == null) return;
            BoxRecord record;
            if (!_byNet.TryGetValue(entity.net.ID.Value, out record)) return;
            _byNet.Remove(entity.net.ID.Value);
            if (!GroupHasBoxes(record.GroupId)) KillMarker(record.GroupId);
            if (_worldReady) SaveData();
        }

        private bool GroupHasBoxes(long groupId)
        {
            foreach (BoxRecord record in _byNet.Values)
            {
                if (record.GroupId == groupId) return true;
            }
            return false;
        }

        private void Maintain()
        {
            List<BaseEntity> remove = new List<BaseEntity>();
            List<ulong> missing = new List<ulong>();
            DateTime now = DateTime.UtcNow;
            double lifeHours = _config.MaxLifetimeHours;
            foreach (KeyValuePair<ulong, BoxRecord> pair in _byNet)
            {
                BaseEntity entity = FindEntity(pair.Key);
                if (entity == null || entity.IsDestroyed)
                {
                    missing.Add(pair.Key);
                    continue;
                }
                DroppedItemContainer drop = entity as DroppedItemContainer;
                if (drop != null) HoldDrop(drop);
                else HoldCorpse(entity as LootableCorpse);

                if (_config.RemoveWhenEmpty && EntityEmpty(entity))
                {
                    remove.Add(entity);
                    continue;
                }
                if (lifeHours > 0d && pair.Value.CreatedUtcTicks > 0L)
                {
                    DateTime created = new DateTime(pair.Value.CreatedUtcTicks, DateTimeKind.Utc);
                    if ((now - created).TotalHours >= lifeHours) remove.Add(entity);
                }
            }
            for (int i = 0; i < missing.Count; i++)
            {
                BoxRecord record;
                if (_byNet.TryGetValue(missing[i], out record))
                {
                    _byNet.Remove(missing[i]);
                    if (record != null && !GroupHasBoxes(record.GroupId)) KillMarker(record.GroupId);
                }
            }
            HashSet<ulong> expiredOwners = new HashSet<ulong>();
            for (int i = 0; i < remove.Count; i++)
            {
                BaseEntity entity = remove[i];
                if (entity == null || entity.IsDestroyed) continue;
                BoxRecord record;
                bool expired = lifeHours > 0d && !EntityEmpty(entity);
                if (expired && TryRecord(entity, out record)) expiredOwners.Add(record.OwnerId);
                SuppressDestroyLoot(entity);
                entity.Kill();
            }
            foreach (ulong ownerId in expiredOwners)
            {
                BasePlayer owner = FindPlayer(ownerId);
                if (owner != null && owner.IsConnected)
                    owner.ChatMessage(Msg("Prefix", owner.UserIDString) + Msg("Expired", owner.UserIDString));
            }
            List<ulong> stalePending = new List<ulong>();
            foreach (KeyValuePair<ulong, PendingDeath> pair in _pending)
            {
                if (pair.Value == null || pair.Value.Until < Time.realtimeSinceStartup) stalePending.Add(pair.Key);
            }
            for (int i = 0; i < stalePending.Count; i++)
            {
                PendingDeath session;
                if (_pending.TryGetValue(stalePending[i], out session) && session != null && string.IsNullOrEmpty(session.Result) && !session.FailureNoted)
                {
                    string why = string.IsNullOrEmpty(session.EmptyNote)
                        ? "no corpse with items within 8s"
                        : session.EmptyNote;
                    FailSession(session, why);
                }
                _pending.Remove(stalePending[i]);
            }
            if (missing.Count > 0 || remove.Count > 0) SaveData();
            if (_seenSources.Count > 4000) _seenSources.Clear();
            for (int i = _recent.Count - 1; i >= 0; i--)
            {
                if (_recent[i].Until < Time.realtimeSinceStartup) _recent.RemoveAt(i);
            }
        }

        // LootableCorpse.DoServerDestroy drops containers as a backpack unless blockBagDrop
        // is set. DroppedItemContainer has no dropsLoot field; DoServerDestroy frees its inventory.
        private static void SuppressDestroyLoot(BaseEntity entity)
        {
            LootableCorpse corpse = entity as LootableCorpse;
            if (corpse != null) corpse.blockBagDrop = true;
        }

        private static void HoldDrop(DroppedItemContainer container)
        {
            if (container == null || container.IsDestroyed) return;
            // ServerInit schedules RemoveMe from CalculateRemovalTime while the box is still empty.
            // That delay is itemdespawn_quick (30s) when ItemBasedDespawn is on, and Invoke does not
            // replace it. Cancel the short timer or a full box is deleted on that first callback.
            CancelRemoval(container);
            container.ResetRemovalTime(HoldSeconds);
        }

        private static void CancelRemoval(DroppedItemContainer container)
        {
            try
            {
                MethodInfo method = container.GetType().GetMethod(
                    "CancelInvoke",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new Type[] { typeof(Action) },
                    null);
                if (method == null) return;
                method.Invoke(container, new object[] { (Action)container.RemoveMe });
            }
            catch
            {
                // ResetRemovalTime still runs. The short timer is the one this cancel is for.
            }
        }

        private static void HoldCorpse(LootableCorpse corpse)
        {
            if (corpse == null || corpse.IsDestroyed) return;
            try
            {
                InvokeRemoval(corpse, "ResetRemovalTime");
                InvokeRemoval(corpse, "ResetCorpseRemovalTime");
            }
            catch
            {
                // The compiled ResetRemovalTime(float) is on DroppedItemContainer.
                // Corpse leftovers only exist when the 48-slot cap is hit.
            }
        }

        private static void InvokeRemoval(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null) return;
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(float))
                method.Invoke(target, new object[] { HoldSeconds });
            else if (parameters.Length == 0)
                method.Invoke(target, null);
        }

        private static void StripGroundWatch(BaseEntity entity)
        {
            if (entity == null) return;
            GroundWatch watch = entity.GetComponent<GroundWatch>();
            if (watch != null) UnityEngine.Object.Destroy(watch);
            DestroyOnGroundMissing missing = entity.GetComponent<DestroyOnGroundMissing>();
            if (missing != null) UnityEngine.Object.Destroy(missing);
        }

        #endregion

        #region World bind

        private bool ApplyWipe()
        {
            if (_store == null) _store = new Store();
            string current = CurrentSaveId();
            bool changed = !string.IsNullOrEmpty(_store.SaveId) && !string.IsNullOrEmpty(current)
                && !string.Equals(_store.SaveId, current, StringComparison.Ordinal);
            bool cleared = _wipeNoted || changed;
            if (cleared)
            {
                if (_store.Boxes.Count > 0 || _byNet.Count > 0)
                    Puts("Death boxes cleared because the map save changed.");
                _byNet.Clear();
                _store.Boxes.Clear();
                _nextGroup = 1;
                KillAllMarkers();
            }
            if (!string.IsNullOrEmpty(current)) _store.SaveId = current;
            _wipeNoted = false;
            SaveData();
            return cleared;
        }

        private void DestroyWorldBoxes()
        {
            List<BaseEntity> kill = new List<BaseEntity>();
            foreach (BaseNetworkable net in BaseNetworkable.serverEntities)
            {
                DroppedItemContainer container = net as DroppedItemContainer;
                if (container == null || container.IsDestroyed) continue;
                if (IsOurPrefab(container)) kill.Add(container);
            }
            for (int i = 0; i < kill.Count; i++)
            {
                kill[i].Kill();
            }
            if (kill.Count > 0) Puts("Removed " + kill.Count + " death boxes left on the new map.");
        }

        private static string CurrentSaveId()
        {
            try
            {
                long ticks = SaveRestore.SaveCreatedTime.Ticks;
                if (ticks <= 0L) return null;
                return ticks.ToString(CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private void RebindWorld()
        {
            List<DroppedItemContainer> found = new List<DroppedItemContainer>();
            foreach (BaseNetworkable net in BaseNetworkable.serverEntities)
            {
                DroppedItemContainer container = net as DroppedItemContainer;
                if (container == null || container.IsDestroyed || container.net == null) continue;
                if (!IsOurPrefab(container)) continue;
                found.Add(container);
            }

            List<BoxRecord> unused = new List<BoxRecord>();
            long maxGroup = 0;
            if (_store.Boxes != null)
            {
                for (int i = 0; i < _store.Boxes.Count; i++)
                {
                    BoxRecord record = _store.Boxes[i];
                    if (record == null) continue;
                    unused.Add(record);
                    if (record.GroupId > maxGroup) maxGroup = record.GroupId;
                }
            }
            if (_nextGroup <= maxGroup) _nextGroup = maxGroup + 1;

            _byNet.Clear();
            for (int i = 0; i < found.Count; i++)
            {
                DroppedItemContainer container = found[i];
                BoxRecord record = TakeMatch(container, unused);
                if (record == null) record = Adopt(container);
                if (record == null) continue;
                record.NetId = container.net.ID.Value;
                record.X = container.transform.position.x;
                record.Y = container.transform.position.y;
                record.Z = container.transform.position.z;
                if (record.OwnerId == 0UL) record.OwnerId = container.playerSteamID != 0UL ? container.playerSteamID : container.OwnerID;
                _byNet[record.NetId] = record;
                container.enableSaving = true;
                HoldDrop(container);
                StripGroundWatch(container);
            }
            if (unused.Count > 0)
                Puts("Dropped " + unused.Count + " death box records whose containers are gone.");
            SaveData();
            RebuildMarkers();
            Puts("Rebound " + _byNet.Count + " death boxes.");
        }

        private static BoxRecord TakeMatch(DroppedItemContainer container, List<BoxRecord> unused)
        {
            ulong netId = container.net.ID.Value;
            for (int i = 0; i < unused.Count; i++)
            {
                if (unused[i].NetId != netId) continue;
                BoxRecord exact = unused[i];
                unused.RemoveAt(i);
                return exact;
            }
            ulong owner = container.playerSteamID != 0UL ? container.playerSteamID : container.OwnerID;
            Vector3 position = container.transform.position;
            int best = -1;
            float bestDistance = 3f;
            for (int i = 0; i < unused.Count; i++)
            {
                BoxRecord record = unused[i];
                if (owner != 0UL && record.OwnerId != 0UL && record.OwnerId != owner) continue;
                float distance = Vector3.Distance(position, new Vector3(record.X, record.Y, record.Z));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }
            if (best < 0) return null;
            BoxRecord match = unused[best];
            unused.RemoveAt(best);
            return match;
        }

        private BoxRecord Adopt(DroppedItemContainer container)
        {
            ulong owner = container.playerSteamID != 0UL ? container.playerSteamID : container.OwnerID;
            if (owner == 0UL) return null;
            string name = container.playerName;
            if (!string.IsNullOrEmpty(name) && name.EndsWith(NameSuffix, StringComparison.Ordinal))
                name = name.Substring(0, name.Length - NameSuffix.Length);
            if (string.IsNullOrEmpty(name)) name = owner.ToString();
            return new BoxRecord
            {
                OwnerId = owner,
                OwnerName = name,
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
                GroupId = NextGroup()
            };
        }

        #endregion

        #region Marker and command

        private void EnsureMarker(PendingDeath session, Vector3 position)
        {
            if (!_config.MapMarker || session == null) return;
            if (_markerByGroup.ContainsKey(session.GroupId))
            {
                BasePlayer owner = FindPlayer(session.UserId);
                if (owner != null) SendMarkers(owner);
                return;
            }
            VendingMachineMapMarker marker = GameManager.server.CreateEntity(MarkerPrefab, position) as VendingMachineMapMarker;
            if (marker == null) return;
            marker.markerShopName = TrimName(session.Name) + " Death Box";
            marker.enableSaving = false;
            marker.limitNetworking = true;
            marker.globalBroadcast = false;
            marker.Spawn();
            if (marker.net == null)
            {
                marker.Kill();
                return;
            }
            _markerByGroup[session.GroupId] = marker.net.ID.Value;
            BasePlayer player = FindPlayer(session.UserId);
            if (player != null) SendMarker(player, marker);
        }

        private void RebuildMarkers()
        {
            KillAllMarkers();
            if (!_config.MapMarker) return;
            Dictionary<long, BoxRecord> first = new Dictionary<long, BoxRecord>();
            foreach (BoxRecord record in _byNet.Values)
            {
                if (record == null) continue;
                if (!first.ContainsKey(record.GroupId)) first[record.GroupId] = record;
            }
            foreach (KeyValuePair<long, BoxRecord> pair in first)
            {
                PendingDeath session = new PendingDeath
                {
                    UserId = pair.Value.OwnerId,
                    Name = pair.Value.OwnerName,
                    GroupId = pair.Key,
                    Position = new Vector3(pair.Value.X, pair.Value.Y, pair.Value.Z)
                };
                EnsureMarker(session, session.Position);
            }
        }

        private void SendMarkers(BasePlayer player)
        {
            if (player == null || !player.IsConnected || !_config.MapMarker) return;
            foreach (BoxRecord record in _byNet.Values)
            {
                if (record.OwnerId != player.userID) continue;
                ulong markerId;
                if (!_markerByGroup.TryGetValue(record.GroupId, out markerId)) continue;
                VendingMachineMapMarker marker = FindEntity(markerId) as VendingMachineMapMarker;
                if (marker != null) SendMarker(player, marker);
            }
        }

        private static void SendMarker(BasePlayer player, VendingMachineMapMarker marker)
        {
            if (player == null || marker == null || marker.IsDestroyed) return;
            if (player.Connection == null) return;
            marker.SendAsSnapshot(player.Connection);
        }

        private void KillMarker(long groupId)
        {
            ulong markerId;
            if (!_markerByGroup.TryGetValue(groupId, out markerId)) return;
            _markerByGroup.Remove(groupId);
            BaseEntity marker = FindEntity(markerId);
            if (marker != null && !marker.IsDestroyed) marker.Kill();
        }

        private void KillAllMarkers()
        {
            foreach (ulong markerId in _markerByGroup.Values)
            {
                BaseEntity marker = FindEntity(markerId);
                if (marker != null && !marker.IsDestroyed) marker.Kill();
            }
            _markerByGroup.Clear();
        }

        private void Announce(BasePlayer player)
        {
            if (player == null || !_announce.Contains(player.userID)) return;
            int count = 0;
            string grid = "";
            long newest = 0L;
            foreach (BoxRecord record in _byNet.Values)
            {
                if (record.OwnerId != player.userID) continue;
                count++;
                if (record.CreatedUtcTicks >= newest)
                {
                    newest = record.CreatedUtcTicks;
                    grid = GridOf(new Vector3(record.X, record.Y, record.Z));
                }
            }
            if (count == 0) return;
            _announce.Remove(player.userID);
            string text = count == 1
                ? Msg("StoredOne", player.UserIDString, grid)
                : Msg("StoredMany", player.UserIDString, count, grid);
            player.ChatMessage(Msg("Prefix", player.UserIDString) + text);
        }

        private void CmdDeathBoxChat(BasePlayer player, string command, string[] args)
        {
            if (args != null && args.Length > 0 && IsDebugWord(args[0]))
            {
                string mode = args.Length > 1 ? args[1] : null;
                HandleDebugCommand(player, null, mode);
                return;
            }
            string who = args != null && args.Length > 0 ? args[0] : null;
            ShowList(player, who);
        }

        private void CmdDeathBoxConsole(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            BasePlayer player = arg.Player();
            string who = arg.HasArgs() ? arg.GetString(0) : null;
            if (IsDebugWord(who))
            {
                string mode = arg.HasArgs(2) ? arg.GetString(1) : null;
                HandleDebugCommand(player, arg, mode);
                return;
            }
            if (player == null)
            {
                arg.ReplyWith("Server console: deathbox debug   or   nsdeathbox.status");
                return;
            }
            ShowList(player, who);
        }

        private void CmdDeathBoxStatus(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            string mode = arg.HasArgs() ? arg.GetString(0) : null;
            HandleDebugCommand(arg.Player(), arg, mode);
        }

        private void ShowList(BasePlayer player, string who)
        {
            if (player == null) return;
            ulong ownerId = player.userID;
            string header = Msg("Header", player.UserIDString);
            if (!string.IsNullOrEmpty(who))
            {
                if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
                {
                    player.ChatMessage(Msg("Prefix", player.UserIDString) + Msg("NotAdmin", player.UserIDString));
                    return;
                }
                ulong parsed;
                if (!ulong.TryParse(who, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    player.ChatMessage(Msg("Prefix", player.UserIDString) + Msg("BadId", player.UserIDString));
                    return;
                }
                ownerId = parsed;
                header = Msg("AdminHeader", player.UserIDString, ownerId.ToString(CultureInfo.InvariantCulture));
            }
            else if (!permission.UserHasPermission(player.UserIDString, PermUse) && !OwnsAny(player.userID))
            {
                player.ChatMessage(Msg("Prefix", player.UserIDString) + Msg("NoPerm", player.UserIDString));
                return;
            }

            List<BoxRecord> rows = new List<BoxRecord>();
            foreach (BoxRecord record in _byNet.Values)
            {
                if (record.OwnerId == ownerId) rows.Add(record);
            }
            if (rows.Count == 0)
            {
                player.ChatMessage(Msg("Prefix", player.UserIDString) + Msg("None", player.UserIDString));
                return;
            }
            rows.Sort((a, b) => b.CreatedUtcTicks.CompareTo(a.CreatedUtcTicks));
            string body = Msg("Prefix", player.UserIDString) + header;
            int shown = Math.Min(rows.Count, 12);
            for (int i = 0; i < shown; i++)
            {
                BoxRecord record = rows[i];
                BaseEntity entity = FindEntity(record.NetId);
                Vector3 position = entity != null ? entity.transform.position : new Vector3(record.X, record.Y, record.Z);
                int items = CountItems(entity);
                body += "\n" + Msg("Line", player.UserIDString, (i + 1).ToString(CultureInfo.InvariantCulture), GridOf(position), items.ToString(CultureInfo.InvariantCulture), AgeOf(record.CreatedUtcTicks));
            }
            if (rows.Count > shown)
                body += "\n" + Msg("More", player.UserIDString, (rows.Count - shown).ToString(CultureInfo.InvariantCulture));
            player.ChatMessage(body);
        }

        private bool OwnsAny(ulong ownerId)
        {
            foreach (BoxRecord record in _byNet.Values)
            {
                if (record.OwnerId == ownerId) return true;
            }
            return false;
        }

        private static int CountItems(BaseEntity entity)
        {
            if (entity == null || entity.IsDestroyed) return 0;
            DroppedItemContainer drop = entity as DroppedItemContainer;
            if (drop != null && drop.inventory != null && drop.inventory.itemList != null) return drop.inventory.itemList.Count;
            LootableCorpse corpse = entity as LootableCorpse;
            if (corpse != null && corpse.containers != null)
            {
                int count = 0;
                for (int i = 0; i < corpse.containers.Length; i++)
                {
                    ItemContainer container = corpse.containers[i];
                    if (container != null && container.itemList != null) count += container.itemList.Count;
                }
                return count;
            }
            return 0;
        }

        private static string AgeOf(long ticks)
        {
            if (ticks <= 0L) return "unknown age";
            TimeSpan age = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
            if (age.TotalMinutes < 1d) return "just now";
            if (age.TotalHours < 1d) return Math.Floor(age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m ago";
            if (age.TotalDays < 2d) return Math.Floor(age.TotalHours).ToString(CultureInfo.InvariantCulture) + "h ago";
            return Math.Floor(age.TotalDays).ToString(CultureInfo.InvariantCulture) + "d ago";
        }

        #endregion

        #region Diagnostics

        private void NoteMoveFailure(PendingDeath session, string reason)
        {
            if (session == null || session.Result == "converted") return;
            session.EmptyNote = reason;
            if (session.FailureNoted) return;
            session.FailureNoted = true;
            NoteDeath(session.UserId, session.Name, "skipped", reason, session.Pvp, session.HasPermission, session.ItemsAtDeath, session.Spawned, false);
        }

        private void FailSession(PendingDeath session, string reason)
        {
            if (session == null || !string.IsNullOrEmpty(session.Result)) return;
            session.Result = "skipped";
            ReapEmptyBoxes(session);
            NoteDeath(session.UserId, session.Name, "skipped", reason, session.Pvp, session.HasPermission, session.ItemsAtDeath, session.Spawned, false);
        }

        private void ReapEmptyBoxes(PendingDeath session)
        {
            if (session == null) return;
            List<DroppedItemContainer> kill = new List<DroppedItemContainer>();
            foreach (BoxRecord record in _byNet.Values)
            {
                if (record == null || record.GroupId != session.GroupId) continue;
                DroppedItemContainer box = FindDrop(record.NetId);
                if (box != null && IsEmpty(box.inventory)) kill.Add(box);
            }
            for (int i = 0; i < kill.Count; i++)
            {
                if (kill[i] != null && !kill[i].IsDestroyed) kill[i].Kill();
            }
            if (kill.Count > 0)
            {
                session.Spawned -= kill.Count;
                if (session.Spawned < 0) session.Spawned = 0;
            }
        }

        private void NoteDeath(ulong userId, string name, string result, string reason, string pvp, bool hasPerm, int items, int boxes, bool legacyLog)
        {
            DeathTrace trace = new DeathTrace
            {
                When = DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                UserId = userId,
                Name = string.IsNullOrEmpty(name) ? userId.ToString(CultureInfo.InvariantCulture) : name,
                Result = result,
                Reason = reason ?? "",
                Pvp = string.IsNullOrEmpty(pvp) ? "?" : pvp,
                HasPermission = hasPerm,
                ItemCount = items,
                Boxes = boxes
            };
            _traces.Add(trace);
            while (_traces.Count > TraceKeep) _traces.RemoveAt(0);
            if (result == "converted") return;
            if (_config != null && _config.DebugLogging)
            {
                Puts("Death box skipped for " + trace.Name + " (" + trace.UserId.ToString(CultureInfo.InvariantCulture) + "): " + trace.Reason
                    + ". pvp=" + trace.Pvp + " perm=" + (hasPerm ? "yes" : "no") + " items=" + items.ToString(CultureInfo.InvariantCulture) + ".");
                return;
            }
            if (legacyLog && _config != null && _config.LogConversions)
                Puts("Skipped " + trace.Name + " (" + trace.UserId.ToString(CultureInfo.InvariantCulture) + "): " + trace.Reason);
        }

        private void TouchTrace(PendingDeath session, string reason)
        {
            if (session == null) return;
            for (int i = _traces.Count - 1; i >= 0; i--)
            {
                DeathTrace trace = _traces[i];
                if (trace.UserId != session.UserId || trace.Result != "converted") continue;
                trace.Reason = reason;
                trace.Boxes = session.Spawned;
                trace.ItemCount = session.ItemsAtDeath;
                return;
            }
        }

        private static bool IsLegacySkip(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return false;
            return reason == "disconnected sleeper"
                || reason == "connected sleeper"
                || reason == "awake player"
                || reason == "explicit suicide"
                || reason == "safe zone"
                || reason == "event or raid zone";
        }

        private static int CountPlayerItems(BasePlayer player)
        {
            if (player == null || player.inventory == null) return 0;
            return CountContainer(player.inventory.containerMain)
                + CountContainer(player.inventory.containerBelt)
                + CountContainer(player.inventory.containerWear);
        }

        private static int CountCorpseItems(PlayerCorpse corpse)
        {
            if (corpse == null) return 0;
            return CountItems(corpse);
        }

        private static int CountContainer(ItemContainer container)
        {
            if (container == null || container.itemList == null) return 0;
            return container.itemList.Count;
        }

        private bool CanDiagnose(BasePlayer player)
        {
            if (player == null) return true;
            if (player.IsAdmin) return true;
            return permission.UserHasPermission(player.UserIDString, PermAdmin);
        }

        private void ReplyDiagnose(BasePlayer player, ConsoleSystem.Arg arg, string text)
        {
            if (arg != null)
            {
                arg.ReplyWith(text);
                return;
            }
            if (player != null)
                player.ChatMessage(Msg("Prefix", player.UserIDString) + text);
        }

        private string BuildDebugReport()
        {
            string pvp;
            bool active = QueryPvP(out pvp);
            StringBuilder builder = new StringBuilder();
            builder.Append("NightshadeDeathBox 1.0.2 debug=");
            builder.Append(_config != null && _config.DebugLogging ? "on" : "off");
            builder.Append(" pvp=");
            builder.Append(active ? "yes" : "no");
            builder.Append(" (");
            builder.Append(pvp);
            builder.Append(") tracked=");
            builder.Append(_byNet.Count.ToString(CultureInfo.InvariantCulture));
            builder.Append(" permission=");
            builder.Append(PermUse);
            if (_traces.Count == 0)
            {
                builder.Append("\nNo deaths recorded since this plugin loaded.");
                return builder.ToString();
            }
            builder.Append("\nLast ");
            builder.Append(_traces.Count.ToString(CultureInfo.InvariantCulture));
            builder.Append(" deaths:");
            for (int i = _traces.Count - 1; i >= 0; i--)
            {
                DeathTrace trace = _traces[i];
                builder.Append("\n");
                builder.Append(trace.When);
                builder.Append(" ");
                builder.Append(trace.Name);
                builder.Append(" (");
                builder.Append(trace.UserId.ToString(CultureInfo.InvariantCulture));
                builder.Append(") ");
                builder.Append(trace.Result);
                builder.Append(": ");
                builder.Append(trace.Reason);
                builder.Append(" | pvp=");
                builder.Append(trace.Pvp);
                builder.Append(" perm=");
                builder.Append(trace.HasPermission ? "yes" : "no");
                builder.Append(" items=");
                builder.Append(trace.ItemCount.ToString(CultureInfo.InvariantCulture));
                builder.Append(" boxes=");
                builder.Append(trace.Boxes.ToString(CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        private bool HandleDebugCommand(BasePlayer player, ConsoleSystem.Arg arg, string mode)
        {
            if (!CanDiagnose(player))
            {
                ReplyDiagnose(player, arg, "You need admin or nightshadedeathbox.admin for death box diagnostics.");
                return true;
            }
            if (!string.IsNullOrEmpty(mode))
            {
                string token = mode.Trim();
                if (token.Equals("on", StringComparison.OrdinalIgnoreCase) || token.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    _config.DebugLogging = token.Equals("on", StringComparison.OrdinalIgnoreCase);
                    SaveConfig();
                    ReplyDiagnose(player, arg, "Debug logging " + (_config.DebugLogging ? "on" : "off") + ".");
                    return true;
                }
            }
            string report = BuildDebugReport();
            ReplyDiagnose(player, arg, report);
            Puts(report);
            return true;
        }

        private static bool IsDebugWord(string word)
        {
            if (string.IsNullOrEmpty(word)) return false;
            return word.Equals("debug", StringComparison.OrdinalIgnoreCase)
                || word.Equals("status", StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Helpers

        private long NextGroup()
        {
            long id = _nextGroup;
            _nextGroup++;
            if (_store != null) _store.NextGroupId = _nextGroup;
            return id;
        }

        private Vector3 Grounded(Vector3 death, int index)
        {
            Vector3 origin = death + Vector3.up * 0.75f;
            int mask = LayerMask.GetMask("Terrain", "World", "Construction", "Deployed");
            Vector3 point = death;
            RaycastHit hit;
            if (mask != 0 && Physics.Raycast(origin, Vector3.down, out hit, 8f, mask, QueryTriggerInteraction.Ignore))
            {
                if (death.y - hit.point.y <= 4f) point = hit.point;
            }
            else
            {
                float terrain = TerrainMeta.HeightMap.GetHeight(death);
                if (death.y < terrain) point.y = terrain;
            }
            point.y += _config.VerticalOffset;
            float step = _config.BoxSpacing;
            point.x += (index % 3) * step;
            point.z += (index / 3) * step;
            return point;
        }

        private static string GridOf(Vector3 position)
        {
            try
            {
                string grid = MapHelper.PositionToString(position);
                if (!string.IsNullOrEmpty(grid)) return grid;
            }
            catch
            {
                // Map helper can throw before the terrain is ready.
            }
            return Math.Round(position.x).ToString(CultureInfo.InvariantCulture) + " " + Math.Round(position.z).ToString(CultureInfo.InvariantCulture);
        }

        private static string TrimName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Player";
            StringBuilder builder = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length && builder.Length < 24; i++)
            {
                char c = name[i];
                if (c >= 32 && c != '{' && c != '}') builder.Append(c);
            }
            string trimmed = builder.ToString().Trim();
            return trimmed.Length == 0 ? "Player" : trimmed;
        }

        private string Msg(string key, string userId, params object[] args)
        {
            string text = lang.GetMessage(key, this, userId);
            if (args != null && args.Length > 0)
            {
                try { text = string.Format(CultureInfo.InvariantCulture, text, args); }
                catch { }
            }
            return text;
        }

        private static BasePlayer FindPlayer(ulong userId)
        {
            BasePlayer player = BasePlayer.FindByID(userId);
            if (player != null) return player;
            return BasePlayer.FindSleeping(userId);
        }

        private static BaseEntity FindEntity(ulong netId)
        {
            if (netId == 0UL) return null;
            BaseNetworkable net = BaseNetworkable.serverEntities.Find(new NetworkableId(netId));
            return net as BaseEntity;
        }

        private static DroppedItemContainer FindDrop(ulong netId)
        {
            return FindEntity(netId) as DroppedItemContainer;
        }

        #endregion
    }
}
