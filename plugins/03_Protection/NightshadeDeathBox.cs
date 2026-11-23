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
    [Info("NightshadeDeathBox", "Nightshade", "1.0.3")]
    [Description("Puts a player's death loot in owner-only containers during the PvP weekend.")]
    public class NightshadeDeathBox : RustPlugin
    {
        // Default container is a saved 48-slot item_drop_backpack. It looks like the
        // vanilla death bag. "Container Prefab" can be a storage entity such as
        // assets/prefabs/deployable/large wood storage/box.wooden.large.prefab or
        // assets/prefabs/deployable/small stash/small_stash_deployed.prefab.
        // Carbon's CanLootEntity patch on PlayerOpenLoot treats any non-null hook
        // result as "do not open". Returning true does not mean allow. This plugin
        // returns null to allow and true to deny. TruePVE 2.4.4 uses that same
        // true when "Prevent non-ally from looting backpacks" is on, and only
        // looks at DroppedItemContainer.playerSteamID. A zero id makes TruePVE
        // return null, so owner, team, and admin are not blocked by it and the
        // two plugins do not disagree. NightshadeLootProtection would
        // otherwise return false for a player-owned wooden box when the looter is
        // not the owner, the team, or a server admin (nightshadedeathbox.admin is
        // not enough). Load order puts that false last. Loot protection 1.3.1
        // returns null when API_IsNightshadeDeathBox is true, so this plugin's
        // null/false is the only opinion. needsBuildingPrivilegeToUse is cleared
        // on storage containers so another player's tool cupboard does not block
        // the owner. The marker skin is not a workshop skin; the model stays the
        // prefab default.
        // ResetRemovalTime keeps backpacks from despawning while they still have
        // items. Storage prefabs have their Decay component removed instead.
        // RestoreUponDeath removes the items it will give back before the corpse
        // is filled, so only what actually lands in the corpse or a Backpacks bag
        // is moved. CreateCorpse copies that inventory before OnPlayerCorpseSpawned.

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
        private const int DecisionLimit = 20;
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
        private readonly List<DeathDecision> _decisions = new List<DeathDecision>();
        private readonly Dictionary<ulong, List<ulong>> _listedNets = new Dictionary<ulong, List<ulong>>();

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
            [JsonProperty("Debug Logging")] public bool DebugLogging = true;
            [JsonProperty("Container Prefab")] public string ContainerPrefab = BackpackPrefab;
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
            public int ItemsMoved;
            public float Until;
            public DeathDecision Decision;
            public List<ulong> CreatedNets = new List<ulong>();
            public List<VanillaMark> Vanilla = new List<VanillaMark>();
        }

        private class VanillaMark
        {
            public ulong NetId;
            public string Kind;
        }

        private class DeathDecision
        {
            public string AtUtc;
            public ulong UserId;
            public string Name;
            public string Place;
            public bool PvpActive;
            public string PvpDetail;
            public bool HasPermission;
            public string Reason;
            public int ItemsMoved;
            public int BoxCount;
            public string Created;
            public string Vanilla;
            public bool Logged;
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
            if (string.IsNullOrWhiteSpace(_config.ContainerPrefab)) _config.ContainerPrefab = BackpackPrefab;
            else _config.ContainerPrefab = _config.ContainerPrefab.Trim();
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
            cmd.AddConsoleCommand("nsdeathbox.status", this, nameof(CmdStatusConsole));
            cmd.AddConsoleCommand("nsdeathbox.list", this, nameof(CmdListConsole));
            cmd.AddConsoleCommand("nsdeathbox.tp", this, nameof(CmdTpConsole));
        }

        private void OnServerInitialized()
        {
            bool wiped = ApplyWipe();
            if (wiped) DestroyWorldBoxes();
            else RebindWorld();
            _worldReady = true;
            _maintainTimer = timer.Every(60f, Maintain);
            WarnMissingDefaultPermission();
            Puts("NightshadeDeathBox 1.0.3. Debug logging is " + (_config.DebugLogging ? "on" : "off")
                + ". Container prefab: " + ContainerPrefabPath()
                + ". New boxes follow NightshadeRaidProtection.API_IsPvPActive (Eastern weekend, test clock, and nsraid.pvpweekend)."
                + " Admin locate: nsdeathbox.list [name|steamid], nsdeathbox.tp <netId|index>.");
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

        // BasePlayer.Die calls this hook before BaseCombatEntity.Die. The method stays
        // void: any non-null OnPlayerDeath result makes Carbon skip the rest of Die.
        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            HandleDeath(player, info);
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

        private object CanLootEntity(BasePlayer player, StorageContainer container)
        {
            return LootDecision(player, container);
        }

        private object CanLootEntity(BasePlayer player, LootableCorpse corpse)
        {
            return LootDecision(player, corpse);
        }

        [HookMethod("API_IsNightshadeDeathBox")]
        public bool API_IsNightshadeDeathBox(BaseEntity entity)
        {
            return IsTracked(entity);
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
            if (player == null || player.IsNpc || !player.userID.IsSteamId()) return;
            PendingDeath existing;
            if (_pending.TryGetValue(player.userID, out existing) && existing != null && existing.Until >= Time.realtimeSinceStartup)
                return;

            DeathDecision decision = new DeathDecision
            {
                UserId = player.userID,
                Name = string.IsNullOrEmpty(player.displayName) ? player.userID.ToString() : player.displayName,
                Place = FormatPos(player.transform.position)
            };
            if (!_worldReady || player.IsDestroyed)
            {
                decision.Reason = !_worldReady ? "plugin not ready" : "player entity already destroyed";
                decision.PvpDetail = "not checked";
                HoldDecision(player.userID, decision);
                return;
            }

            if (!ShouldConvert(player, info, decision))
            {
                if (decision.Reason != "missing nightshadedeathbox.use" && decision.Reason != "pvp weekend inactive" && decision.Reason != "plugin disabled" && _config.LogConversions && !_config.DebugLogging)
                    Puts("Skipped " + decision.Name + " (" + decision.UserId + "): " + decision.Reason);
                HoldDecision(player.userID, decision);
                return;
            }

            PendingDeath session = new PendingDeath
            {
                UserId = player.userID,
                Name = decision.Name,
                Position = player.transform.position,
                GroupId = NextGroup(),
                Spawned = 0,
                ItemsMoved = 0,
                Until = Time.realtimeSinceStartup + PendingSeconds,
                Decision = decision
            };
            _pending[player.userID] = session;
            if (_config.ForceBackpackDrop) ForceBackpack(player, session);
            ulong ownerId = player.userID;
            // CreateCorpse has already copied the inventory when OnPlayerCorpseSpawned
            // runs. NextTick still catches a bag that spawned before this hook.
            NextTick(() => DrainRecent(ownerId));
            timer.Once(PendingSeconds, () => CloseDeath(ownerId));
        }

        // Every false path sets decision.Reason. Permission and the weekend gate used
        // to leave the reason null, which produced no console line at all.
        private bool ShouldConvert(BasePlayer player, HitInfo info, DeathDecision decision)
        {
            string pvpDetail;
            bool pvp = QueryPvp(out pvpDetail);
            decision.PvpActive = pvp;
            decision.PvpDetail = pvpDetail;
            decision.HasPermission = permission.UserHasPermission(player.UserIDString, PermUse);
            if (_config == null || !_config.Enabled)
            {
                decision.Reason = "plugin disabled";
                return false;
            }
            if (_config.OnlyDuringPvpWeekend && !pvp)
            {
                decision.Reason = "pvp weekend inactive";
                return false;
            }
            if (!decision.HasPermission)
            {
                decision.Reason = "missing nightshadedeathbox.use";
                return false;
            }

            bool sleeping = player.IsSleeping();
            bool connected = player.IsConnected;
            if (sleeping && !connected)
            {
                if (!_config.ConvertDisconnectedSleepers) { decision.Reason = "disconnected sleeper"; return false; }
            }
            else if (sleeping)
            {
                if (!_config.ConvertConnectedSleepers) { decision.Reason = "connected sleeper"; return false; }
            }
            else if (!_config.ConvertAwakePlayers)
            {
                decision.Reason = "awake player";
                return false;
            }

            if (!_config.ProtectSuicideDeaths && IsExplicitSuicide(player, info))
            {
                decision.Reason = "explicit suicide";
                return false;
            }
            if (_config.SkipSafeZones && player.InSafeZone())
            {
                decision.Reason = "safe zone";
                return false;
            }
            if (_config.SkipEventZones && InEventZone(player, player.transform.position))
            {
                decision.Reason = "event or raid zone";
                return false;
            }
            decision.Reason = "accepted";
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

        private bool QueryPvp(out string detail)
        {
            detail = "not checked";
            if (NightshadeRaidProtection == null || !NightshadeRaidProtection.IsLoaded)
            {
                detail = "NightshadeRaidProtection not loaded";
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
                if (value is bool)
                {
                    bool active = (bool)value;
                    detail = active ? "API true" : "API false";
                    return active;
                }
                detail = value == null ? "API returned null" : "API returned " + value.GetType().Name;
                return false;
            }
            catch (Exception ex)
            {
                detail = "API failed: " + ex.Message;
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
            if (corpse == null || corpse.IsDestroyed || corpse.net == null) return;
            PendingDeath session;
            if (!_pending.TryGetValue(corpse.playerSteamID, out session) || session == null || session.Until < Time.realtimeSinceStartup)
                return;
            if (corpse.containers == null)
            {
                NoteVanilla(session, corpse, "corpse with no containers");
                return;
            }
            if (IsEmptyCorpse(corpse))
            {
                // Do not mark the corpse seen. TakeFrom has normally already run, but a
                // later pass can still move items if they land after this tick.
                NoteVanilla(session, corpse, "empty corpse");
                return;
            }
            if (!_seenSources.Add(corpse.net.ID.Value)) return;
            ForgetVanilla(session, corpse.net.ID.Value);
            List<Item> items = TakeCorpseItems(corpse);
            int moved = MoveItems(session, items);
            if (moved > 0 && IsEmptyCorpse(corpse))
            {
                // DoServerDestroy spawns item_drop_backpack unless blockBagDrop is set.
                corpse.blockBagDrop = true;
                corpse.Kill();
            }
            else if (!IsEmptyCorpse(corpse))
            {
                // Cap was hit. Keep the leftover private instead of leaving a public corpse.
                TrackOverflow(session, corpse);
                HoldCorpse(corpse);
            }
            else
            {
                NoteVanilla(session, corpse, "corpse");
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
            if (IsEmpty(container.inventory))
            {
                NoteVanilla(session, container, "empty vanilla backpack");
                return;
            }
            if (!_seenSources.Add(container.net.ID.Value)) return;
            ForgetVanilla(session, container.net.ID.Value);
            int moved = AbsorbSource(session, container);
            if (moved <= 0) NoteVanilla(session, container, "vanilla backpack");
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
                DeathDecision separate = new DeathDecision
                {
                    UserId = ownerId,
                    Name = player == null || string.IsNullOrEmpty(player.displayName) ? ownerId.ToString() : player.displayName,
                    Place = player == null ? "unknown" : FormatPos(player.transform.position)
                };
                if (player == null || !ShouldConvert(player, null, separate))
                {
                    if (_config.LogBackpackDiagnostics)
                        Puts("Separate backpack for " + ownerId + " was not converted" + (player == null ? "." : ": " + separate.Reason));
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
            List<Item> items = TakeContainer(source.inventory);
            int moved = MoveItems(session, items);
            if (IsEmpty(source.inventory))
            {
                source.Kill();
            }
            else
            {
                TrackOverflow(session, source);
                HoldDrop(source);
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
                BaseEntity box = FindOpenBox(session);
                if (box == null)
                {
                    if (session.Spawned >= _config.MaxBoxesPerDeath) break;
                    box = SpawnBox(session);
                }
                ItemContainer inventory = InventoryOf(box);
                if (box == null || inventory == null) break;
                if (!item.MoveToContainer(inventory))
                {
                    if (session.Spawned >= _config.MaxBoxesPerDeath) break;
                    BaseEntity next = SpawnBox(session);
                    ItemContainer nextInventory = InventoryOf(next);
                    if (next == null || nextInventory == null || !item.MoveToContainer(nextInventory))
                        break;
                }
                moved++;
            }
            session.ItemsMoved += moved;
            return moved;
        }

        private BaseEntity FindOpenBox(PendingDeath session)
        {
            foreach (BoxRecord record in _byNet.Values)
            {
                if (record.GroupId != session.GroupId) continue;
                BaseEntity box = FindEntity(record.NetId);
                ItemContainer inventory = InventoryOf(box);
                if (box == null || inventory == null) continue;
                int count = inventory.itemList == null ? 0 : inventory.itemList.Count;
                if (count < inventory.capacity) return box;
            }
            return null;
        }

        private BaseEntity SpawnBox(PendingDeath session)
        {
            string prefab = ContainerPrefabPath();
            Vector3 position = Grounded(session.Position, session.Spawned);
            Quaternion rotation = Quaternion.Euler(0f, (session.Spawned * 35f) % 360f, 0f);
            BaseEntity entity = GameManager.server.CreateEntity(prefab, position, rotation);
            if (entity == null)
            {
                PrintWarning("Could not spawn a death box for " + session.UserId + " from " + prefab + ".");
                return null;
            }

            DroppedItemContainer drop = entity as DroppedItemContainer;
            StorageContainer storage = entity as StorageContainer;
            if (drop == null && storage == null)
            {
                entity.Kill();
                PrintWarning("Death box prefab is not a dropped backpack or a storage container: " + prefab);
                return null;
            }

            if (drop != null)
            {
                drop.lootPanelName = LootPanel;
                drop.playerName = TrimName(session.Name) + NameSuffix;
                drop.playerSteamID = 0UL;
                drop.inventory = new ItemContainer();
                drop.inventory.ServerInitialize(null, BoxSlots);
                drop.inventory.GiveUID();
                drop.inventory.entityOwner = drop;
                drop.inventory.SetFlag(ItemContainer.Flag.NoItemInput, true);
            }
            else
            {
                storage.inventorySlots = BoxSlots;
                storage.dropsLoot = false;
                storage.isLockable = false;
                storage.isMonitorable = false;
                storage.needsBuildingPrivilegeToUse = false;
                storage.requireAuthIfNotLocked = false;
                storage.canBeDemolished = false;
                storage.pickup.enabled = false;
            }

            entity.OwnerID = session.UserId;
            entity.skinID = MarkerSkin;
            entity.enableSaving = true;
            StripGroundWatch(entity);
            if (storage != null)
            {
                Decay decay = storage.GetComponent<Decay>();
                if (decay != null) UnityEngine.Object.Destroy(decay);
            }
            entity.Spawn();

            if (drop != null && drop.inventory != null)
            {
                drop.inventory.entityOwner = drop;
                if (drop.inventory.capacity < BoxSlots) drop.inventory.capacity = BoxSlots;
                drop.inventory.SetFlag(ItemContainer.Flag.NoItemInput, true);
                HoldDrop(drop);
            }
            if (storage != null)
            {
                storage.dropsLoot = false;
                storage.pickup.enabled = false;
                if (storage.inventory != null)
                {
                    storage.inventory.entityOwner = storage;
                    if (storage.inventory.capacity < BoxSlots) storage.inventory.capacity = BoxSlots;
                    storage.inventory.SetFlag(ItemContainer.Flag.NoItemInput, true);
                }
                StashContainer stash = storage as StashContainer;
                if (stash != null && stash.IsHidden()) stash.SetHidden(false);
            }

            if (entity.net == null)
            {
                entity.Kill();
                PrintWarning("Death box spawned without a net id for " + session.UserId + ".");
                return null;
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
            session.Spawned++;
            ReleaseTruePveClaim(entity);
            NoteCreated(session, entity);
            EnsureMarker(session, entity.transform.position);
            return entity;
        }

        private void TrackOverflow(PendingDeath session, BaseEntity entity)
        {
            if (entity == null || entity.net == null) return;
            if (_byNet.ContainsKey(entity.net.ID.Value)) return;
            DroppedItemContainer drop = entity as DroppedItemContainer;
            if (drop != null)
            {
                drop.playerName = TrimName(session.Name) + NameSuffix;
                drop.playerSteamID = 0UL;
                drop.OwnerID = session.UserId;
                drop.skinID = MarkerSkin;
                drop.enableSaving = true;
                if (drop.inventory != null) drop.inventory.SetFlag(ItemContainer.Flag.NoItemInput, true);
            }
            else
            {
                entity.OwnerID = session.UserId;
                SuppressDestroyLoot(entity);
                ReleaseTruePveClaim(entity);
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
            NoteCreated(session, entity);
            if (_config.LogConversions && !_config.DebugLogging)
                Puts("Death box cap (" + _config.MaxBoxesPerDeath + ") hit for " + session.UserId + ". Leftover container kept private.");
        }

        private void FinishAbsorb(PendingDeath session, int moved, string kind)
        {
            if (session == null || moved <= 0) return;
            _announce.Add(session.UserId);
            BasePlayer online = FindPlayer(session.UserId);
            if (online != null && online.IsConnected && !online.IsDead()) Announce(online);
            SaveData();
            if (_config.LogConversions && (session.Decision == null || !_config.DebugLogging))
                Puts("Stored " + moved + " items for " + session.Name + " (" + session.UserId + ") from " + kind + " at " + GridOf(session.Position) + ".");
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

        // null lets the open continue. true cancels it. false would also cancel,
        // but TruePVE returns true for the same deny and Carbon treats the two
        // boxed values as a hook conflict.
        private object LootDecision(BasePlayer player, BaseEntity entity)
        {
            BoxRecord record;
            if (!TryRecord(entity, out record)) return null;
            if (player != null && CanAccess(player, record)) return null;
            if (player != null) Tell(player, "Blocked", record.OwnerName);
            return true;
        }

        // TruePVE.CanLootEntity(DroppedItemContainer) passes playerSteamID into
        // isPlayerProtected. Zero is not a steam id, so TruePVE returns null.
        private static void ReleaseTruePveClaim(BaseEntity entity)
        {
            if (entity == null || entity.IsDestroyed) return;
            DroppedItemContainer drop = entity as DroppedItemContainer;
            if (drop != null && drop.playerSteamID != 0UL)
            {
                drop.playerSteamID = 0UL;
                drop.SendNetworkUpdate();
            }
            LootableCorpse corpse = entity as LootableCorpse;
            if (corpse != null && corpse.playerSteamID != 0UL)
            {
                corpse.playerSteamID = 0UL;
                corpse.SendNetworkUpdate();
            }
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
            return IsMarked(container);
        }

        private static bool IsMarked(BaseEntity entity)
        {
            if (entity == null) return false;
            if (entity.skinID == MarkerSkin) return true;
            DroppedItemContainer drop = entity as DroppedItemContainer;
            if (drop == null) return false;
            string name = drop.playerName;
            return !string.IsNullOrEmpty(name) && name.EndsWith(NameSuffix, StringComparison.Ordinal);
        }

        private static ulong MarkedOwner(BaseEntity entity)
        {
            if (entity == null) return 0UL;
            DroppedItemContainer drop = entity as DroppedItemContainer;
            if (drop != null && drop.playerSteamID != 0UL) return drop.playerSteamID;
            return entity.OwnerID;
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
                ReleaseTruePveClaim(entity);
                DroppedItemContainer drop = entity as DroppedItemContainer;
                if (drop != null) HoldDrop(drop);
                else HoldCorpse(entity as LootableCorpse);
                StorageContainer storage = entity as StorageContainer;
                if (storage != null)
                {
                    storage.dropsLoot = false;
                    StashContainer stash = storage as StashContainer;
                    if (stash != null && stash.IsHidden()) stash.SetHidden(false);
                }

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
            for (int i = 0; i < stalePending.Count; i++) CloseDeath(stalePending[i]);
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
            StorageContainer storage = entity as StorageContainer;
            if (storage != null) storage.dropsLoot = false;
        }

        private static void HoldDrop(DroppedItemContainer container)
        {
            if (container == null || container.IsDestroyed) return;
            container.ResetRemovalTime(HoldSeconds);
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
                BaseEntity entity = net as BaseEntity;
                if (entity == null || entity.IsDestroyed) continue;
                if (!(entity is DroppedItemContainer) && !(entity is StorageContainer)) continue;
                if (IsMarked(entity)) kill.Add(entity);
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
            List<BaseEntity> found = new List<BaseEntity>();
            foreach (BaseNetworkable net in BaseNetworkable.serverEntities)
            {
                BaseEntity entity = net as BaseEntity;
                if (entity == null || entity.IsDestroyed || entity.net == null) continue;
                if (!(entity is DroppedItemContainer) && !(entity is StorageContainer)) continue;
                if (!IsMarked(entity)) continue;
                found.Add(entity);
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
                BaseEntity container = found[i];
                BoxRecord record = TakeMatch(container, unused);
                if (record == null) record = Adopt(container);
                if (record == null) continue;
                record.NetId = container.net.ID.Value;
                record.X = container.transform.position.x;
                record.Y = container.transform.position.y;
                record.Z = container.transform.position.z;
                if (record.OwnerId == 0UL) record.OwnerId = MarkedOwner(container);
                _byNet[record.NetId] = record;
                container.enableSaving = true;
                ReleaseTruePveClaim(container);
                DroppedItemContainer drop = container as DroppedItemContainer;
                if (drop != null) HoldDrop(drop);
                StorageContainer storage = container as StorageContainer;
                if (storage != null)
                {
                    storage.dropsLoot = false;
                    storage.needsBuildingPrivilegeToUse = false;
                    storage.pickup.enabled = false;
                    Decay decay = storage.GetComponent<Decay>();
                    if (decay != null) UnityEngine.Object.Destroy(decay);
                }
                StripGroundWatch(container);
            }
            if (unused.Count > 0)
                Puts("Dropped " + unused.Count + " death box records whose containers are gone.");
            SaveData();
            RebuildMarkers();
            Puts("Rebound " + _byNet.Count + " death boxes.");
        }

        private static BoxRecord TakeMatch(BaseEntity container, List<BoxRecord> unused)
        {
            ulong netId = container.net.ID.Value;
            for (int i = 0; i < unused.Count; i++)
            {
                if (unused[i].NetId != netId) continue;
                BoxRecord exact = unused[i];
                unused.RemoveAt(i);
                return exact;
            }
            ulong owner = MarkedOwner(container);
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

        private BoxRecord Adopt(BaseEntity container)
        {
            ulong owner = MarkedOwner(container);
            if (owner == 0UL) return null;
            DroppedItemContainer drop = container as DroppedItemContainer;
            string name = drop == null ? null : drop.playerName;
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
            if (args != null && args.Length > 0 && args[0].Equals("debug", StringComparison.OrdinalIgnoreCase))
            {
                ReplyStatus(player, null);
                return;
            }
            string who = args != null && args.Length > 0 ? args[0] : null;
            ShowList(player, who);
        }

        private void CmdDeathBoxConsole(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            if (arg.HasArgs() && string.Equals(arg.GetString(0), "debug", StringComparison.OrdinalIgnoreCase))
            {
                ReplyStatus(arg.Player(), arg);
                return;
            }
            BasePlayer player = arg.Player();
            if (player == null)
            {
                arg.ReplyWith("Use /deathbox in chat, nsdeathbox.list [name|steamid] to locate containers, or nsdeathbox.status for the last deaths.");
                return;
            }
            string who = arg.HasArgs() ? arg.GetString(0) : null;
            ShowList(player, who);
        }

        private void CmdStatusConsole(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            ReplyStatus(arg.Player(), arg);
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
            ItemContainer inventory = InventoryOf(entity);
            if (inventory != null && inventory.itemList != null) return inventory.itemList.Count;
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

        // Server console and RCON may list every tracked container. A player needs
        // nightshadedeathbox.admin, which this plugin registers and does not grant.
        // nsdeathbox.tp is player F1 only. A number inside the last list is an index.
        // Any other number is a net ID. "net" and "index" force that choice.
        private void CmdListConsole(ConsoleSystem.Arg arg)
        {
            if (arg == null || !AllowAdminConsole(arg)) return;
            string filter = JoinFilter(arg);
            List<BoxRecord> rows = CollectBoxes(filter);
            BasePlayer player = arg.Player();
            RememberList(player == null ? 0UL : player.userID, rows);
            arg.ReplyWith(FormatBoxList(rows, filter));
        }

        private void CmdTpConsole(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            BasePlayer player = arg.Player();
            if (player == null || arg.IsRcon || arg.Connection == null)
            {
                arg.ReplyWith("nsdeathbox.tp only works from a player's F1 console. Server console and RCON cannot teleport.");
                return;
            }
            if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
            {
                arg.ReplyWith("You need the nightshadedeathbox.admin permission.");
                return;
            }
            string mode;
            string token;
            string usage;
            if (!TryParseTeleportArg(arg, out mode, out token, out usage))
            {
                arg.ReplyWith(usage);
                return;
            }
            BoxRecord record;
            string error;
            if (!TryResolveTeleport(player, mode, token, out record, out error))
            {
                arg.ReplyWith(error);
                return;
            }
            BaseEntity entity = FindEntity(record.NetId);
            bool alive = EntityAlive(entity);
            Vector3 position = BoxPosition(record, entity);
            TeleportAdmin(player, position + new Vector3(0f, 1.5f, 0f));
            string state = alive
                ? "Entity exists."
                : "Entity is STALE (missing). This is the last known position.";
            arg.ReplyWith("Teleported to death container. owner " + OwnerText(record)
                + " net " + record.NetId.ToString(CultureInfo.InvariantCulture)
                + " pos " + FormatPos(position)
                + " grid " + GridOf(position)
                + ". " + state);
        }

        private bool AllowAdminConsole(ConsoleSystem.Arg arg)
        {
            if (arg == null) return false;
            if (arg.IsRcon || arg.Connection == null) return true;
            BasePlayer player = arg.Player();
            if (player == null) return true;
            if (permission.UserHasPermission(player.UserIDString, PermAdmin)) return true;
            arg.ReplyWith("You need the nightshadedeathbox.admin permission.");
            return false;
        }

        private static bool TryParseTeleportArg(ConsoleSystem.Arg arg, out string mode, out string token, out string error)
        {
            mode = "auto";
            token = null;
            error = "Usage: nsdeathbox.tp <netId|index>   or   nsdeathbox.tp net <netId>   or   nsdeathbox.tp index <n>";
            if (arg == null || !arg.HasArgs()) return false;
            string first = arg.GetString(0);
            if (string.IsNullOrEmpty(first)) return false;
            first = first.Trim();
            if (arg.HasArgs(2))
            {
                if (arg.HasArgs(3)) return false;
                string second = arg.GetString(1);
                if (string.IsNullOrEmpty(second)) return false;
                second = second.Trim();
                if (first.Equals("net", StringComparison.OrdinalIgnoreCase) || first.Equals("id", StringComparison.OrdinalIgnoreCase))
                {
                    mode = "net";
                    token = second;
                    return true;
                }
                if (first.Equals("index", StringComparison.OrdinalIgnoreCase) || first.Equals("line", StringComparison.OrdinalIgnoreCase))
                {
                    mode = "index";
                    token = second;
                    return true;
                }
                return false;
            }
            if (first.Length > 1 && first[0] == '#')
            {
                mode = "index";
                token = first.Substring(1);
                return true;
            }
            token = first;
            return true;
        }

        private bool TryResolveTeleport(BasePlayer player, string mode, string token, out BoxRecord record, out string error)
        {
            record = null;
            error = "Usage: nsdeathbox.tp <netId|index>   or   nsdeathbox.tp net <netId>   or   nsdeathbox.tp index <n>";
            ulong id;
            if (string.IsNullOrEmpty(token) || !ulong.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                return false;
            List<ulong> nets = ListNetsFor(player);
            bool asIndex = mode == "index" || (mode != "net" && id >= 1UL && id <= (ulong)nets.Count);
            if (asIndex)
            {
                if (nets.Count == 0 || id < 1UL || id > (ulong)nets.Count)
                {
                    error = nets.Count == 0
                        ? "There is no list index to use. Run nsdeathbox.list [name|steamid] first."
                        : "Index " + token + " is outside the list (1-" + nets.Count.ToString(CultureInfo.InvariantCulture) + "). Run nsdeathbox.list [name|steamid].";
                    return false;
                }
                ulong netId = nets[(int)id - 1];
                BoxRecord indexed;
                if (!_byNet.TryGetValue(netId, out indexed) || indexed == null)
                {
                    error = "List index " + token + " pointed at net " + netId.ToString(CultureInfo.InvariantCulture) + ", which is no longer tracked. Run nsdeathbox.list again.";
                    return false;
                }
                record = indexed;
                error = null;
                return true;
            }
            BoxRecord direct;
            if (_byNet.TryGetValue(id, out direct) && direct != null)
            {
                record = direct;
                error = null;
                return true;
            }
            error = "No death container with net ID " + token + ". Run nsdeathbox.list [name|steamid].";
            return false;
        }

        private List<ulong> ListNetsFor(BasePlayer player)
        {
            List<ulong> nets;
            if (player != null && _listedNets.TryGetValue(player.userID, out nets) && nets != null) return nets;
            return NetIdsOf(CollectBoxes(null));
        }

        private static string JoinFilter(ConsoleSystem.Arg arg)
        {
            if (arg == null || !arg.HasArgs()) return null;
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < 16 && arg.HasArgs(i + 1); i++)
            {
                string part = arg.GetString(i);
                if (string.IsNullOrEmpty(part)) continue;
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(part.Trim());
            }
            if (builder.Length == 0) return null;
            string text = builder.ToString().Trim();
            return text.Length == 0 ? null : text;
        }

        private List<BoxRecord> CollectBoxes(string filter)
        {
            List<BoxRecord> rows = new List<BoxRecord>();
            foreach (BoxRecord record in _byNet.Values)
            {
                if (record == null || !MatchesFilter(record, filter)) continue;
                rows.Add(record);
            }
            rows.Sort(CompareBoxes);
            return rows;
        }

        private int CompareBoxes(BoxRecord left, BoxRecord right)
        {
            if (left == null && right == null) return 0;
            if (left == null) return 1;
            if (right == null) return -1;
            int name = string.Compare(OwnerDisplayName(left), OwnerDisplayName(right), StringComparison.OrdinalIgnoreCase);
            if (name != 0) return name;
            int owner = left.OwnerId.CompareTo(right.OwnerId);
            if (owner != 0) return owner;
            int created = right.CreatedUtcTicks.CompareTo(left.CreatedUtcTicks);
            if (created != 0) return created;
            return left.NetId.CompareTo(right.NetId);
        }

        private bool MatchesFilter(BoxRecord record, string filter)
        {
            if (record == null) return false;
            if (string.IsNullOrEmpty(filter)) return true;
            ulong steamId;
            if (TrySteamId(filter, out steamId)) return record.OwnerId == steamId;
            if (ContainsText(OwnerDisplayName(record), filter)) return true;
            return !string.IsNullOrEmpty(record.OwnerName) && ContainsText(record.OwnerName, filter);
        }

        private static bool TrySteamId(string text, out ulong steamId)
        {
            steamId = 0UL;
            if (string.IsNullOrEmpty(text) || text.Length < 17) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < '0' || c > '9') return false;
            }
            return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out steamId);
        }

        private static bool ContainsText(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void RememberList(ulong callerId, List<BoxRecord> rows)
        {
            _listedNets[callerId] = NetIdsOf(rows);
        }

        private static List<ulong> NetIdsOf(List<BoxRecord> rows)
        {
            List<ulong> nets = new List<ulong>();
            if (rows == null) return nets;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i] != null) nets.Add(rows[i].NetId);
            }
            return nets;
        }

        private string FormatBoxList(List<BoxRecord> rows, string filter)
        {
            int count = rows == null ? 0 : rows.Count;
            if (count == 0)
            {
                if (!string.IsNullOrEmpty(filter))
                    return "No containers found for " + OneLine(filter, 64) + ". Total: 0";
                return "No active death containers. Total: 0";
            }
            StringBuilder builder = new StringBuilder();
            builder.Append("NightshadeDeathBox 1.0.3");
            if (!string.IsNullOrEmpty(filter))
                builder.Append(" matching \"").Append(OneLine(filter, 64)).Append("\"");
            for (int i = 0; i < count; i++)
            {
                builder.Append("\n");
                builder.Append(FormatBoxLine(i + 1, rows[i]));
            }
            builder.Append("\nTotal: ").Append(count.ToString(CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        private string FormatBoxLine(int index, BoxRecord record)
        {
            BaseEntity entity = record == null ? null : FindEntity(record.NetId);
            bool alive = EntityAlive(entity);
            Vector3 position = BoxPosition(record, entity);
            string items = alive ? CountItems(entity).ToString(CultureInfo.InvariantCulture) : "n/a";
            string state = alive ? "exists" : "STALE";
            return index.ToString(CultureInfo.InvariantCulture)
                + ". owner " + OwnerText(record)
                + " net " + (record == null ? "0" : record.NetId.ToString(CultureInfo.InvariantCulture))
                + " pos " + FormatPos(position)
                + " grid " + GridOf(position)
                + " items " + items
                + " created " + CreatedText(record == null ? 0L : record.CreatedUtcTicks)
                + " remaining " + RemainingText(record)
                + " entity " + state;
        }

        private string OwnerText(BoxRecord record)
        {
            string id = record == null || record.OwnerId == 0UL
                ? "unknown"
                : record.OwnerId.ToString(CultureInfo.InvariantCulture);
            return OwnerDisplayName(record) + " (" + id + ")";
        }

        private string OwnerDisplayName(BoxRecord record)
        {
            if (record == null) return "unknown";
            if (record.OwnerId != 0UL)
            {
                BasePlayer owner = FindPlayer(record.OwnerId);
                if (owner != null)
                {
                    string live = OneLine(owner.displayName, 32);
                    if (live != "unknown") return live;
                }
            }
            return OneLine(record.OwnerName, 32);
        }

        private string RemainingText(BoxRecord record)
        {
            float hours = _config == null ? 0f : _config.MaxLifetimeHours;
            if (hours <= 0f) return "no timed expiry or unlock";
            if (record == null || record.CreatedUtcTicks <= 0L) return "unknown";
            DateTime created = new DateTime(record.CreatedUtcTicks, DateTimeKind.Utc);
            TimeSpan left = created.AddHours(hours) - DateTime.UtcNow;
            if (left.TotalSeconds <= 0d) return "expired, awaiting removal";
            return FormatDuration(left) + " until expiry";
        }

        private static string CreatedText(long ticks)
        {
            if (ticks <= 0L) return "unknown";
            DateTime at = new DateTime(ticks, DateTimeKind.Utc);
            return at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC (" + AgeOf(ticks) + ")";
        }

        private static string FormatDuration(TimeSpan span)
        {
            if (span.TotalSeconds < 1d) return "0s";
            if (span.TotalDays >= 2d)
                return Math.Floor(span.TotalDays).ToString(CultureInfo.InvariantCulture) + "d " + span.Hours.ToString(CultureInfo.InvariantCulture) + "h";
            if (span.TotalHours >= 1d)
                return Math.Floor(span.TotalHours).ToString(CultureInfo.InvariantCulture) + "h " + span.Minutes.ToString(CultureInfo.InvariantCulture) + "m";
            if (span.TotalMinutes >= 1d)
                return Math.Floor(span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m " + span.Seconds.ToString(CultureInfo.InvariantCulture) + "s";
            return span.Seconds.ToString(CultureInfo.InvariantCulture) + "s";
        }

        private static Vector3 BoxPosition(BoxRecord record, BaseEntity entity)
        {
            if (entity != null && !entity.IsDestroyed && entity.transform != null) return entity.transform.position;
            if (record == null) return Vector3.zero;
            return new Vector3(record.X, record.Y, record.Z);
        }

        private static bool EntityAlive(BaseEntity entity)
        {
            return entity != null && !entity.IsDestroyed;
        }

        private static string OneLine(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || max < 1) return "unknown";
            StringBuilder builder = new StringBuilder(Math.Min(text.Length, max));
            for (int i = 0; i < text.Length && builder.Length < max; i++)
            {
                char c = text[i];
                if (c < 32 || c == '{' || c == '}') builder.Append(' ');
                else builder.Append(c);
            }
            string trimmed = builder.ToString().Trim();
            return trimmed.Length == 0 ? "unknown" : trimmed;
        }

        private static void TeleportAdmin(BasePlayer player, Vector3 dest)
        {
            if (player == null) return;
            player.PauseFlyHackDetection(5f);
            player.PauseSpeedHackDetection(5f);
            player.EnsureDismounted();
            if (player.HasParent()) player.SetParent(null, true, true);
            player.Teleport(dest);
        }

        #endregion

        #region Decisions

        private void HoldDecision(ulong userId, DeathDecision decision)
        {
            PendingDeath holder = new PendingDeath
            {
                UserId = userId,
                Name = decision.Name,
                Until = Time.realtimeSinceStartup + PendingSeconds,
                Decision = decision
            };
            _pending[userId] = holder;
            PushDecision(decision);
            timer.Once(PendingSeconds, () =>
            {
                PendingDeath current;
                if (_pending.TryGetValue(userId, out current) && current != null && current.Decision != null && current.Decision.Logged)
                    _pending.Remove(userId);
            });
        }

        private void CloseDeath(ulong userId)
        {
            PendingDeath session;
            if (!_pending.TryGetValue(userId, out session) || session == null) return;
            _pending.Remove(userId);
            DeathDecision decision = session.Decision;
            if (decision == null || decision.Logged) return;
            decision.ItemsMoved = session.ItemsMoved;
            decision.BoxCount = session.CreatedNets.Count;
            decision.Created = DescribeNets(session.CreatedNets);
            decision.Vanilla = DescribeVanilla(session);
            if (session.CreatedNets.Count > 0) decision.Reason = "protected";
            else if (decision.Reason == "accepted") decision.Reason = "no protected container";
            PushDecision(decision);
        }

        private void PushDecision(DeathDecision decision)
        {
            if (decision == null || decision.Logged) return;
            decision.Logged = true;
            decision.AtUtc = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture);
            _decisions.Insert(0, decision);
            while (_decisions.Count > DecisionLimit) _decisions.RemoveAt(_decisions.Count - 1);
            if (_config != null && _config.DebugLogging) Puts(FormatDecision(decision));
        }

        private void NoteCreated(PendingDeath session, BaseEntity entity)
        {
            if (session == null || entity == null || entity.net == null) return;
            ulong netId = entity.net.ID.Value;
            if (!session.CreatedNets.Contains(netId)) session.CreatedNets.Add(netId);
            ForgetVanilla(session, netId);
        }

        private static void NoteVanilla(PendingDeath session, BaseEntity entity, string kind)
        {
            if (session == null || entity == null || entity.net == null) return;
            ulong netId = entity.net.ID.Value;
            if (session.CreatedNets.Contains(netId)) return;
            for (int i = 0; i < session.Vanilla.Count; i++)
            {
                if (session.Vanilla[i].NetId == netId) return;
            }
            session.Vanilla.Add(new VanillaMark { NetId = netId, Kind = kind });
        }

        private static void ForgetVanilla(PendingDeath session, ulong netId)
        {
            if (session == null) return;
            for (int i = session.Vanilla.Count - 1; i >= 0; i--)
            {
                if (session.Vanilla[i].NetId == netId) session.Vanilla.RemoveAt(i);
            }
        }

        private string DescribeNets(List<ulong> nets)
        {
            if (nets == null || nets.Count == 0) return "none";
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < nets.Count; i++)
            {
                if (i > 0) builder.Append("; ");
                builder.Append(DescribeEntity(nets[i], null));
            }
            return builder.ToString();
        }

        private string DescribeVanilla(PendingDeath session)
        {
            if (session == null || session.Vanilla.Count == 0) return "none";
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < session.Vanilla.Count; i++)
            {
                if (i > 0) builder.Append("; ");
                VanillaMark mark = session.Vanilla[i];
                builder.Append(DescribeEntity(mark.NetId, mark.Kind));
            }
            return builder.ToString();
        }

        private string DescribeEntity(ulong netId, string kind)
        {
            BaseEntity entity = FindEntity(netId);
            string prefix = string.IsNullOrEmpty(kind) ? "net " : kind + " net ";
            string id = netId.ToString(CultureInfo.InvariantCulture);
            if (entity == null || entity.IsDestroyed)
                return prefix + id + " (gone)";
            return prefix + id + " at " + FormatPos(entity.transform.position)
                + " " + entity.ShortPrefabName + " (" + CountItems(entity).ToString(CultureInfo.InvariantCulture) + " items)";
        }

        private static string FormatDecision(DeathDecision decision)
        {
            string pvp = decision.PvpActive ? "yes" : "no";
            string perm = decision.HasPermission ? "yes" : "no";
            string tail;
            if (decision.Reason == "protected")
            {
                string boxes = decision.BoxCount.ToString(CultureInfo.InvariantCulture);
                string noun = decision.BoxCount == 1 ? "container" : "containers";
                tail = "protected " + decision.ItemsMoved.ToString(CultureInfo.InvariantCulture)
                    + " items in " + boxes + " " + noun + ": " + decision.Created + ". Vanilla left: " + decision.Vanilla + ".";
            }
            else
            {
                string vanilla = string.IsNullOrEmpty(decision.Vanilla) || decision.Vanilla == "none"
                    ? "Loot left vanilla."
                    : "Loot left vanilla: " + decision.Vanilla + ".";
                tail = "skipped (" + decision.Reason + "). " + vanilla;
            }
            return decision.Name + " (" + decision.UserId.ToString(CultureInfo.InvariantCulture) + ") at " + decision.Place
                + ": pvp=" + pvp + " (" + decision.PvpDetail + ") perm=" + perm + " -> " + tail;
        }

        private void ReplyStatus(BasePlayer player, ConsoleSystem.Arg arg)
        {
            if (player != null && !player.IsAdmin && !permission.UserHasPermission(player.UserIDString, PermAdmin))
            {
                string denied = "You need nightshadedeathbox.admin to read death box decisions.";
                if (arg != null) arg.ReplyWith(denied);
                else if (player != null) player.ChatMessage(Msg("Prefix", player.UserIDString) + denied);
                return;
            }
            string text = StatusText();
            if (arg != null) arg.ReplyWith(text);
            else if (player != null) player.ChatMessage(text);
        }

        private string StatusText()
        {
            string pvpDetail;
            bool pvp = QueryPvp(out pvpDetail);
            bool granted = false;
            try { granted = permission.GroupHasPermission("default", PermUse); }
            catch { granted = false; }
            StringBuilder builder = new StringBuilder();
            builder.Append("NightshadeDeathBox 1.0.3");
            builder.Append("\nadmin: nsdeathbox.list [name|steamid]   nsdeathbox.tp <netId|index> (player F1 only)");
            builder.Append("\ncontainer prefab: ").Append(ContainerPrefabPath());
            builder.Append("\ndefault group nightshadedeathbox.use: ").Append(granted ? "yes" : "NO");
            builder.Append("\npvp active: ").Append(pvp ? "yes" : "no").Append(" (").Append(pvpDetail).Append(")");
            builder.Append("\ntracked containers: ").Append(_byNet.Count.ToString(CultureInfo.InvariantCulture));
            builder.Append("\ndebug logging: ").Append(_config != null && _config.DebugLogging ? "on" : "off");
            builder.Append("\nlast deaths (newest first):");
            if (_decisions.Count == 0) builder.Append("\n(none since this load)");
            for (int i = 0; i < _decisions.Count; i++)
            {
                DeathDecision decision = _decisions[i];
                builder.Append("\n").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(". ");
                builder.Append(decision.AtUtc).Append(" ").Append(FormatDecision(decision));
            }
            return builder.ToString();
        }

        private void WarnMissingDefaultPermission()
        {
            bool granted;
            try { granted = permission.GroupHasPermission("default", PermUse); }
            catch (Exception ex)
            {
                PrintWarning("Could not read the default group's permissions: " + ex.Message);
                return;
            }
            if (granted) return;
            PrintWarning("The default group does not have nightshadedeathbox.use. Deaths for players without that permission are skipped and no protected container is created. Grant it with: c.grant group default nightshadedeathbox.use");
        }

        private string ContainerPrefabPath()
        {
            if (_config == null || string.IsNullOrWhiteSpace(_config.ContainerPrefab)) return BackpackPrefab;
            return _config.ContainerPrefab.Trim();
        }

        private static string FormatPos(Vector3 position)
        {
            return "(" + position.x.ToString("0.00", CultureInfo.InvariantCulture)
                + ", " + position.y.ToString("0.00", CultureInfo.InvariantCulture)
                + ", " + position.z.ToString("0.00", CultureInfo.InvariantCulture) + ")";
        }

        private static ItemContainer InventoryOf(BaseEntity entity)
        {
            if (entity == null || entity.IsDestroyed) return null;
            DroppedItemContainer drop = entity as DroppedItemContainer;
            if (drop != null) return drop.inventory;
            StorageContainer storage = entity as StorageContainer;
            if (storage != null) return storage.inventory;
            return null;
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
