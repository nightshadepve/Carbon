using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Facepunch;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Rust;
using Rust.Ai.Gen2;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

namespace Oxide.Plugins
{
    [Info("Dungeon Events", "Martian", "3.13.2")]
    [Description("Create dungeons populated with NPCs, turrets and loot containers.")]
    public class DungeonEvents : RustPlugin
    {
        #region Fields
        [PluginReference]
        private Plugin Friends,
            Economics,
            ServerRewards,
            Notify,
            NightVision,
            ZoneManager,
            Duelist,
            RaidableBases,
            AbandonedBases,
            RestoreUponDeath,
            SkillTree;
        private static DungeonEvents _instance;
        private Configuration _config;
        private readonly List<ActiveDungeon> _activeDungeons = new List<ActiveDungeon>();
        private readonly Dictionary<ulong, ActiveDungeon> _dungeonByEntityId = new Dictionary<ulong, ActiveDungeon>();
        private readonly Dictionary<string, ActiveDungeon> _dungeonByUniqueId = new Dictionary<string, ActiveDungeon>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<ulong>> _dungeonEntityIdCache = new Dictionary<string, HashSet<ulong>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _dirtyDungeonEntityCaches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Bounds> _enemySpawnBounds = new Dictionary<string, Bounds>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LootFileCache> _lootFileCache = new Dictionary<string, LootFileCache>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ulong, EventUiState> _eventUiStates = new Dictionary<ulong, EventUiState>();
        private static readonly Func<BasePlayer, HashSet<string>> _getActiveUiPanels = CreateActiveUiPanelLookup();
        private Timer _entityCacheFlushTimer;
        private bool _isUnloading;
        private readonly List<BasePlayer> _wasInDungeonPlayers = new List<BasePlayer>();
        private readonly Dictionary<ulong, PlayerLootRecovery> _pendingPlayerLootRecoveries = new Dictionary<ulong, PlayerLootRecovery>();
        private readonly Dictionary<ulong, float> _lastEntranceAttackWarning = new Dictionary<ulong, float>();
        private const float EntranceAttackWarningCooldown = 5f;
        private const float PlayerLootRecoveryLifetimeSeconds = 120f;
        private readonly Dictionary<ulong, Dictionary<string, DateTime>> _buyCooldowns = new Dictionary<ulong, Dictionary<string, DateTime>>();
        private readonly List<DungeonMarker> _activeDungeonMarkers = new List<DungeonMarker>();
        private readonly Queue<DungeonRequest> _dungeonQueue = new Queue<DungeonRequest>();
        private readonly Queue<DungeonRequest> _dungeonFailedAutoSpawnQueue = new Queue<DungeonRequest>();
        private readonly Queue<DungeonRequest> _dungeonFailedBuyQueue = new Queue<DungeonRequest>();
        private Timer _dungeonTimer;
        private bool _isCreatingDungeon;
        private DungeonRequest _currentDungeonRequest;
        private const float DungeonTimerInterval = 10f;
        private const string PermissionAdmin = "dungeonevents.admin";
        private const string PermissionBuy = "dungeonevents.buy";
        private const string PermissionEnter = "dungeonevents.enter";
        private const string PermissionFree = "dungeonevents.free";
        private const string PermissionBypassCooldown = "dungeonevents.bypasscooldown";
        private const string LootItemTextFieldKey = "Item.text";
        private const ulong OwnerID = 10203040505;
        private const string UiEventPanel = "yFifW5s1zAY09h5cU0qeIQUNV6GGwevG";
        private const string UiMainPanel = "5cU06GGweeIQ09hyFifW5qvGVsUNAY1z";
        private const string UiCommandClose = "lDM82GJH3biLcI95ic8zzteteOeQx77h";
        private Dictionary<DungeonTierConfig, TierInfo> _tierInfoMap = new Dictionary<DungeonTierConfig, TierInfo>();
        private DateTime _lastAutoSpawnTime = DateTime.MinValue;
        private WaitForSeconds _buildBatchDelay;
        private WaitForSeconds _spawnBatchDelay;
        private WaitForSeconds _cleanupBatchDelay;
        private WaitForSeconds _searchBatchDelay;
        private int _activeDungeonCleanups;
        private float _lastCleanupCompletedAt = -99999f;
        private Coroutine _startupRoutine;
        private Coroutine _dungeonCreationRoutine;
        private const string ConstructionTypeConfigKey =
            "Construction - 1:Wood 2:Legacy 3:Gingerbread 4:Stone 5:Adobe 6:Brick 7:Brutalist 8:Metal 9:Container 10:Armored 11:Jungle 12:Crypt 13:Space 14:Classic";
        private const int ClassicDungeonSelection = 14;
        private const int DungeonEnemyCollisionMask =
            (Layers.Solid | Layers.Mask.AI | Layers.Mask.Player_Server | Layers.Mask.Player_Movement | Layers.Mask.Ragdoll | Layers.Mask.Physics_Debris | Layers.Mask.Prevent_Movement)
            & ~Layers.Mask.Terrain;
        private const string DungeonRockPrefab = "assets/bundled/prefabs/modding/admin/admin_rock_quarry_small_a.prefab";
        private const string DungeonTorchPrefab = "assets/bundled/prefabs/static/torchholder_on.static.prefab";
        private const float DungeonRockOverlap = 0.27f;
        private static readonly Bounds DungeonRockBounds = new Bounds(new Vector3(0.004f, 0f, 0.007f), new Vector3(2.323f, 1.929f, 3.192f));
        private static readonly Vector2Int[] RockDirections = { Vector2Int.right, Vector2Int.up, Vector2Int.left, Vector2Int.down };
        private const string WallPrefab = "assets/prefabs/building core/wall/wall.prefab";
        private const string FoundationPrefab = "assets/prefabs/building core/foundation/foundation.prefab";
        private const string CeilingPrefab = "assets/prefabs/building core/floor/floor.prefab";
        private const string TurretPrefab = "assets/prefabs/npc/autoturret/autoturret_deployed.prefab";
        private const string LightPrefab = "assets/prefabs/misc/permstore/industriallight/industrial.wall.lamp.red.deployed.prefab";
        private const string GreenLightPrefab = "assets/prefabs/misc/permstore/industriallight/industrial.wall.lamp.green.deployed.prefab";
        private const string PortalPrefab = "assets/prefabs/missions/portal/halloweenportalexit.prefab";
        private const string WallFramePrefab = "assets/prefabs/building core/wall.frame/wall.frame.prefab";
        private const string DoorPrefab = "assets/prefabs/building/wall.frame.garagedoor/wall.frame.garagedoor.prefab";
        private readonly Dictionary<Vector3, ZoneInfo> _blockedZones = new Dictionary<Vector3, ZoneInfo>();
        private readonly Vector3 _wallOffset = new Vector3(-1.5f, 0, -1.5f);
        private static readonly Vector3 CeilingLightOffset = new Vector3(-1.5f, 0, -1.5f);
        private static readonly List<string> RockKeywords = new List<string> { "rock", "formation", "junk", "cliff", "invisible" };
        private int _gridSize = 5;
        private const int SubCells = 2;
        private const int MinimumActiveCells = 13;
        private const int MaxGenerationAttempts = 1000;
        private const float PurchasedPortalSearchStartRadius = 4f;
        private const float PurchasedPortalSearchMaxRadius = 30f;
        private const float PurchasedPortalSearchRadiusStep = 3f;
        private const float PurchasedPortalTargetSpacing = 3f;
        private const float PurchasedPortalFallbackSearchMaxRadius = 450f;
        private const float PurchasedPortalRelaxedMaxSlope = 60f;
        private const float PurchasedPortalStrictSafeZoneDistance = 90f;
        private const float PurchasedPortalRelaxedSafeZoneDistance = 40f;
        private const float PurchasedPortalBaseAvoidanceExtraRadius = 2f;
        private const float CustomPortalSpawnOccupiedTolerance = 1f;
        private const float CellSpacing = 6f;
        private const float SubCellSpacing = 3f;
        private const float WallRotationOffset = 90f;
        private const float CeilingHeight = 3f;
        private const float BossRouteStartFraction = 0.4f;
        private const float CorridorLength = 12f;
        private const float EntranceInnerLegLength = CorridorLength;
        private const float EntranceOuterLegLength = CellSpacing * 3f;
        private const float EntranceDoorDistanceFromTurn = CellSpacing * 2f;
        private const bool PlaceCeiling = true;
        private const bool ClassicDungeonRoofForTesting = true;
        private const bool PlaceLights = true;
        private uint _randomSeed;
        private static readonly string[] BlockedRespawnPrefabs =
        {
            "assets/prefabs/deployable/bed/bed_deployed.prefab",
            "assets/prefabs/misc/summer_dlc/beach_towel/beachtowel.deployed.prefab",
            "assets/prefabs/deployable/sleeping bag/sleepingbag_leather_deployed.prefab",
        };
        private const string ScientistPrefab = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_heavy.prefab";
        private const float AnimalPushRadius = 0.5f;
        private const float AnimalPushCooldown = 2.0f;
        private const float Gen1AnimalAttackRange = 1.35f;
        private const float Gen1AnimalAttackCooldown = 1.15f;
        private const float TigerAnimalAttackRange = 2.4f;
        private const float TigerAnimalAttackCooldown = 1.5f;
        private const float CrocodileAnimalAttackRange = 3.2f;
        private const float CrocodileAnimalAttackCooldown = 1.6f;
        private const float WolfAnimalAttackRange = 2.0f;
        private const float WolfAnimalAttackCooldown = 1.2f;
        private Vector3 _cachedAnimalNavmeshSpawnPosition = Vector3.zero;
        private bool _animalNavmeshLookupFailed = false;
        private const float CustomTurretServerTickInterval = 0.05f;
        private const float NpcFireTickInterval = 0.10f;
        private const float DungeonEntityCacheFlushInterval = 1f;
        private static readonly Dictionary<string, string> AnimalPrefabPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Boar"] = "assets/rust.ai/agents/boar/boar.prefab",
            ["Wolf"] = "assets/rust.ai/agents/wolf/wolf2.prefab",
            ["Stag"] = "assets/rust.ai/agents/stag/stag.prefab",
            ["Chicken"] = "assets/rust.ai/agents/chicken/chicken.prefab",
            ["Panther"] = "assets/rust.ai/agents/panther/panther.prefab",
            ["Tiger"] = "assets/rust.ai/agents/tiger/tiger.prefab",
            ["Crocodile"] = "assets/rust.ai/agents/crocodile/crocodile.prefab",
            ["Bear"] = "assets/rust.ai/agents/bear/bear.prefab",
            ["Bull"] = "assets/rust.ai/agents/bull/bull.prefab",
            ["Cow"] = "assets/rust.ai/agents/cow/cow.prefab",
            ["Calf"] = "assets/rust.ai/agents/calf/calf.prefab",
            ["CalfMale"] = "assets/rust.ai/agents/calf/calfmale.prefab",
            ["Sheep"] = "assets/rust.ai/agents/sheep/sheep.prefab",
            ["Lamb"] = "assets/rust.ai/agents/sheep/lamb.prefab",
        };
        private static readonly string[] AnimalSpeciesOrder = { "Boar", "Wolf", "Stag", "Chicken", "Panther", "Tiger", "Crocodile", "Bear", "Bull", "Cow", "Calf", "CalfMale", "Sheep", "Lamb" };

        private static List<AnimalSpeciesConfig> CreateDefaultAnimalSpecies()
        {
            return AnimalSpeciesOrder
                .Select(name => new AnimalSpeciesConfig
                {
                    Animal = name,
                    Enabled = true,
                    SpawnChancePercentage = 100f,
                    Health = 300f,
                })
                .ToList();
        }
        #endregion

        #region Harmony Patches
        [AutoPatch]
        [HarmonyPatch(typeof(ScientistNPC), "get_displayName")]
        public static class ScientistNPC_DisplayName_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(ScientistNPC __instance, ref string __result)
            {
                if (__instance == null || __instance.IsDestroyed)
                    return;
                var data = __instance.GetComponent<DungeonNpcName>();
                if (data == null)
                    return;
                if (string.IsNullOrEmpty(data.CustomName))
                    return;
                __result = data.CustomName;
            }
        }

        public class DungeonNpcName : MonoBehaviour
        {
            public string CustomName;
        }
        #endregion

        #region Hooks
        private void Init()
        {
            _instance = this;
            permission.RegisterPermission(PermissionEnter, this);
            permission.RegisterPermission(PermissionAdmin, this);
            permission.RegisterPermission(PermissionBuy, this);
            permission.RegisterPermission(PermissionFree, this);
            permission.RegisterPermission(PermissionBypassCooldown, this);
            RegisterTierPermissions();
        }

        private void OnServerInitialized()
        {
            UpdatePerformanceDelays();
            EnsureUndergroundTerrainProtectionDisabled();
            _startupRoutine = ServerMgr.Instance.StartCoroutine(StartupRoutine());
            NextTick(RegisterCommands);
        }

        private void Unload()
        {
            _isUnloading = true;
            _entityCacheFlushTimer?.Destroy();
            _entityCacheFlushTimer = null;
            if (_startupRoutine != null)
                ServerMgr.Instance.StopCoroutine(_startupRoutine);
            if (_dungeonCreationRoutine != null)
                ServerMgr.Instance.StopCoroutine(_dungeonCreationRoutine);
            _dungeonTimer?.Destroy();
            _dungeonQueue.Clear();
            _dungeonFailedAutoSpawnQueue.Clear();
            _dungeonFailedBuyQueue.Clear();
            ResetDungeonCreation();
            DestroyAllUis();
            RemoveAllDungeons();
            _pendingPlayerLootRecoveries.Clear();
            _lastEntranceAttackWarning.Clear();
            _wasInDungeonPlayers.Clear();
            _eventUiStates.Clear();
            _lootFileCache.Clear();
            _dirtyDungeonEntityCaches.Clear();
        }

        private object CanDropActiveItem(BasePlayer player)
        {
            if (RestoreUponDeath != null)
            {
                return null;
            }
            if (player == null)
                return null;
            if (_wasInDungeonPlayers.Contains(player))
            {
                var activeItem = player.GetActiveItem();
                if (activeItem != null)
                    return false;
            }
            return null;
        }

        private object OnBackpackDrop(Item backpack, PlayerInventory inventory)
        {
            if (!_config.PreventDropBackpack)
                return null;
            var player = inventory?.containerMain?.playerOwner;
            if (RestoreUponDeath != null)
                return null;
            if (player == null)
                return null;
            if (_wasInDungeonPlayers.Contains(player))
                return true;
            return null;
        }

        private object CanDropBackpack(ulong userID, Vector3 backpackPluginHook)
        {
            var player = BasePlayer.Find(userID.ToString());
            if (player == null)
                return null;
            if (_wasInDungeonPlayers.Contains(player))
                return false;
            return null;
        }

        private object CanPickupEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null)
                return null;
            if (GetDungeonForPlayer(player) != null)
                return false;
            return null;
        }

        private object CanDeployItem(BasePlayer player, Deployer deployer, ulong entityId)
        {
            if (player == null)
                return null;
            if (GetDungeonForPlayer(player) != null)
                return false;
            Vector3 deployPosition = GetDeployCheckPosition(player);
            var portalDungeon = GetPortalProtectionDungeon(deployPosition);
            if (portalDungeon == null)
                return null;
            if (!_config.AllowOwnerAndAlliesToDeployInPortalProtectionBubble)
                return false;
            return IsDungeonOwnerOrAlly(player.userID, portalDungeon) ? (object)null : false;
        }

        private object CanBuild(Planner planner, Construction prefab, Construction.Target target)
        {
            var player = planner?.GetOwnerPlayer();
            if (player == null)
                return null;
            if (GetDungeonForPlayer(player) != null)
                return false;
            Vector3 buildPos = target.position;
            if (buildPos == Vector3.zero && target.socket != null && target.entity != null)
                buildPos = target.GetWorldPosition();
            var portalDungeon = GetPortalProtectionDungeon(buildPos);
            if (portalDungeon == null)
                return null;
            if (!_config.AllowOwnerAndAlliesToBuildInPortalProtectionBubble)
                return false;
            return IsDungeonOwnerOrAlly(player.userID, portalDungeon) ? (object)null : false;
        }

        private object OnPlayerCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return null;
            if (GetDungeonForPlayer(player) == null)
                return null;
            if (_config.BlockCommands.Contains("/" + command.ToLower()))
            {
                NotifyPlayer(player, "BlockedCommands", 0);
                return true;
            }
            return null;
        }

        private object OnServerCommand(ConsoleSystem.Arg arg)
        {
            if (arg == null || arg.cmd == null)
                return null;
            BasePlayer player = arg.Player();
            if (player != null && (GetDungeonForPlayer(player) != null))
            {
                if (_config.BlockCommands.Contains(arg.cmd.Name.ToLower()) || _config.BlockCommands.Contains(arg.cmd.FullName.ToLower()))
                {
                    NotifyPlayer(player, "BlockedCommands", 0);
                    return true;
                }
            }
            return null;
        }

        private object OnCustomNpcTarget(ScientistNPC scientistNPC, BasePlayer player)
        {
            if (scientistNPC == null || !IsDungeonEntity(scientistNPC))
                return null;
            if (player == null || !player.userID.IsSteamId())
                return false;
            if (IsPlayerInEntranceForEntity(player, scientistNPC))
                return false;
            return null;
        }

        private object CanEntityBeTargeted(BasePlayer player, AutoTurret turret)
        {
            if (turret == null || !IsDungeonEntity(turret))
                return null;
            if (player == null || !player.userID.IsSteamId())
                return false;
            if (IsPlayerInEntranceForEntity(player, turret))
                return false;
            return true;
        }

        private object OnEntityEnter(TargetTrigger trigger, ScientistNPC scientistNPC)
        {
            if (trigger == null || scientistNPC == null || scientistNPC.net == null || scientistNPC.userID.IsSteamId())
                return null;
            AutoTurret autoTurret = trigger.GetComponentInParent<AutoTurret>();
            if (autoTurret == null || autoTurret.net == null)
                return null;
            if (IsDungeonEntity(autoTurret))
                return true;
            return null;
        }

        private object OnPortalUse(BasePlayer player, BasePortal portal)
        {
            var activeDungeon = GetActiveDungeonByPortal(portal);
            if (activeDungeon == null)
                return null;
            if (!activeDungeon.Spawned)
            {
                NotifyPlayer(player, "DungeonStillSpawningMessage", 0);
                return false;
            }
            if (portal == activeDungeon.BasePortal && !CanEnter(player, activeDungeon))
            {
                NotifyDungeonEntryDenied(player, activeDungeon);
                return false;
            }
            if (IsUndergroundDungeon(activeDungeon))
                EnsureUndergroundTerrainProtectionDisabled();
            return null;
        }

        private object OnDoorAutoClose(BasePlayer player, Door door)
        {
            if (door == null)
                return null;
            return IsDungeonEntity(door) ? (object)true : null;
        }

        private void OnCorpsePopulate(ScientistNPC npc, NPCPlayerCorpse corpse)
        {
            if (npc == null || corpse == null)
                return;
            if (!IsDungeonEntity(npc))
                return;
            var activeDungeon = GetActiveDungeonByEntity(npc);
            if (activeDungeon == null)
                return;
            bool isBoss = npc.GetComponent<BossMonoBehaviour>() != null;
            var corpseData = corpse.gameObject.AddComponent<CorpseLootData>();
            corpseData.IsBoss = isBoss;
            corpseData.LootConfig = isBoss ? activeDungeon.TierConfig?.BossNpcConfig : activeDungeon.TierConfig?.NpcConfig;
            NextTick(() =>
            {
                if (corpse == null || corpse.IsDestroyed)
                    return;
                var storedData = corpse.GetComponent<CorpseLootData>();
                if (storedData == null)
                    return;
                if (storedData.LootConfig == null)
                    return;
                Effect.server.Run("assets/prefabs/weapons/flashbang/effects/fx-flashbang-boom.prefab", corpse.transform.position);
                if (corpse.containers == null || corpse.containers.Length == 0 || corpse.containers[0] == null)
                    return;
                var container = corpse.containers[0];
                container.Clear();
                var items = GetNpcLootItems(storedData.LootConfig);
                if (items == null || items.Count == 0)
                    return;
                int maxDifferentItems = storedData.IsBoss ? (activeDungeon?.TierConfig?.MaxItemsPerBossNpc ?? int.MaxValue) : (activeDungeon?.TierConfig?.MaxItemsPerNpc ?? int.MaxValue);
                PopulateLootContainer(container, items, maxDifferentItems);
            });
        }

        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null || !player.userID.IsSteamId())
                return;
            ActiveDungeon dungeon;
            try
            {
                dungeon = GetDungeonForPlayer(player);
            }
            catch (Exception)
            {
                return;
            }
            if (dungeon == null)
                return;
            try
            {
                RegisterPendingPlayerLootRecovery(player.userID, dungeon, _config.TeleportPlayerOnDeath);
                TeleportPlayerToOutsidePortal(player, dungeon);
                player.Invoke(
                    () =>
                    {
                        if (player != null && !player.IsDestroyed)
                            player.SendNetworkUpdateImmediate();
                    },
                    0.05f
                );
                timer.Once(
                    1f,
                    () =>
                    {
                        if (player == null || player.IsDestroyed)
                            return;
                        var checker = player.GetComponent<PlayerDungeonChecker>();
                        if (checker != null)
                            UnityEngine.Object.Destroy(checker);
                    }
                );
            }
            catch (Exception ex)
            {
                LogDebug($"Failed to prepare player death recovery for {player.userID}: {ex.Message}", 2);
            }
            HandlePlayerDungeonDeparture(dungeon, player.userID);
        }

        private object CanRaidBlock(BasePlayer player)
        {
            var dungeon = GetDungeonForPlayer(player);
            if (dungeon != null)
                return false;
            return null;
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null)
                return;
            ActiveDungeon dungeon = GetActiveDungeonByEntity(entity);
            if (dungeon == null || !dungeon.Spawned)
                return;
            bool isAnimal = IsAnimalEntity(entity);
            bool isNpc = entity is ScientistNPC;
            bool isBoss = isNpc && entity.GetComponent<BossMonoBehaviour>() != null;
            bool isNpcReward = isNpc || isAnimal;
            bool isTurret = entity is AutoTurret;
            bool isBox = entity is StorageContainer;
            if (!isNpcReward && !isTurret && !isBox)
                return;
            if (isNpcReward)
                MarkDungeonNpcDefeated(dungeon, entity);
            BasePlayer attacker = info?.InitiatorPlayer;
            if (attacker == null)
                return;
            RewardConfig r = dungeon.TierConfig?.RewardConfig;
            if (r == null)
                return;
            int econ =
                isBoss ? r.BossEconomicsReward
                : isAnimal ? r.AnimalEconomicsReward
                : isNpc ? r.NpcEconomicsReward
                : isTurret ? r.TurretEconomicsReward
                : r.BoxEconomicsReward;
            int sr =
                isBoss ? r.BossServerRewards
                : isAnimal ? r.AnimalServerRewards
                : isNpc ? r.NpcServerRewards
                : isTurret ? r.TurretServerRewards
                : r.BoxServerRewards;
            if (econ > 0 && Economics != null)
            {
                Economics.Call("Deposit", attacker.userID, (double)econ);
                attacker.ChatMessage(Msg(attacker.UserIDString, "EconRewardMessage", econ));
            }
            if (sr > 0 && ServerRewards != null)
            {
                ServerRewards.Call("AddPoints", attacker.userID, sr);
                attacker.ChatMessage(Msg(attacker.UserIDString, "ServerRewardMessage", sr));
            }
        }

        private void MarkDungeonNpcDefeated(ActiveDungeon dungeon, BaseEntity entity)
        {
            if (dungeon?.Npcs == null || entity == null)
                return;
            dungeon.Npcs.Remove(entity);
        }

        private bool IsDungeonNpcAlive(BaseEntity entity)
        {
            if (entity == null || entity.IsDestroyed)
                return false;
            var combatEntity = entity as BaseCombatEntity;
            if (combatEntity == null)
                return true;
            if (combatEntity.IsDead())
                return false;
            return combatEntity.Health() > 0.01f;
        }

        private object OnEntityTakeDamage(BuildingBlock block, HitInfo info)
        {
            if (info == null || !IsDungeonEntity(block))
                return null;
            info.damageTypes?.ScaleAll(0f);
            return true;
        }

        private object OnEntityTakeDamage(ScientistNPC scientistNPC, HitInfo info)
        {
            if (scientistNPC == null || info == null || info.Initiator == null)
                return null;
            if (info.Initiator is AutoTurret turret && IsDungeonEntity(turret))
            {
                info.damageTypes?.ScaleAll(0f);
                return null;
            }
            if (!IsDungeonEntity(scientistNPC))
                return null;
            if (BlockAndNotifyEntranceAttack(info.InitiatorPlayer, scientistNPC))
            {
                info.damageTypes?.ScaleAll(0f);
                return true;
            }
            BasePlayer playerAttacker = info.InitiatorPlayer;
            float incomingExplosion = info.damageTypes?.Get(DamageType.Explosion) ?? 0f;
            float incomingHeat = info.damageTypes?.Get(DamageType.Heat) ?? 0f;
            if (IsPlayer(playerAttacker) && incomingExplosion <= 0f && incomingHeat <= 0f && !HasClearCoreLineToDungeonNpc(playerAttacker, scientistNPC))
            {
                info.damageTypes?.ScaleAll(0f);
                return true;
            }
            HandleDungeonScientistDamage(scientistNPC, info);
            if (scientistNPC.GetComponent<BossMonoBehaviour>() == null)
                return null;
            float explosion = info.damageTypes?.Get(DamageType.Explosion) ?? 0f;
            float heat = info.damageTypes?.Get(DamageType.Heat) ?? 0f;
            if (explosion > 0f || heat > 0f)
            {
                var activeDungeon = GetActiveDungeonByEntity(scientistNPC);
                if (activeDungeon == null)
                    return null;
                float multiplier = Mathf.Max(0f, activeDungeon.TierConfig?.BossNpcConfig?.DamageExplosionTakenMultiplier ?? 1f);
                if (Mathf.Approximately(multiplier, 1f))
                    return null;
                info.damageTypes?.ScaleAll(multiplier);
            }
            return null;
        }

        private bool HasClearCoreLineToDungeonNpc(BasePlayer attacker, ScientistNPC npc)
        {
            if (attacker == null || npc == null)
                return false;
            Vector3 from = attacker.eyes?.position ?? attacker.transform.position + Vector3.up * 1.6f;
            int mask = Layers.Mask.Construction | Layers.Mask.Deployed | Layers.Mask.World | Layers.Mask.Terrain | Layers.Mask.Default;
            Vector3 torso = npc.transform.position + Vector3.up * 1.15f;
            Vector3 upperTorso = npc.transform.position + Vector3.up * 1.55f;
            if (!Physics.Linecast(from, torso, mask, QueryTriggerInteraction.Ignore))
                return true;
            return !Physics.Linecast(from, upperTorso, mask, QueryTriggerInteraction.Ignore);
        }

        private void HandleDungeonScientistDamage(ScientistNPC scientistNPC, HitInfo info)
        {
            if (scientistNPC == null || info == null)
                return;
            BasePlayer attacker = info.InitiatorPlayer;
            if (!IsPlayer(attacker) || attacker.IsSleeping() || attacker.IsDead())
                return;
            var movement = scientistNPC.GetComponent<NpcMovement>();
            if (movement != null)
                movement.OnDamagedByPlayer(attacker);
            AlertNearbyDungeonScientists(scientistNPC, attacker);
        }

        private void AlertNearbyDungeonScientists(ScientistNPC damagedNpc, BasePlayer attacker)
        {
            if (damagedNpc == null || attacker == null)
                return;
            var dungeon = GetActiveDungeonByEntity(damagedNpc);
            if (dungeon == null || dungeon.Npcs == null)
                return;
            const float alertRadius = 18f;
            float alertRadiusSqr = alertRadius * alertRadius;
            Vector3 source = damagedNpc.transform.position;
            foreach (var entity in dungeon.Npcs)
            {
                var ally = entity as ScientistNPC;
                if (ally == null || ally == damagedNpc || ally.IsDestroyed || ally.Health() <= 0f)
                    continue;
                if ((ally.transform.position - source).sqrMagnitude > alertRadiusSqr)
                    continue;
                var movement = ally.GetComponent<NpcMovement>();
                if (movement != null)
                    movement.OnAllyDamagedByPlayer(attacker, source);
            }
        }

        private object OnEntityTakeDamage(BaseAnimalNPC animal, HitInfo info)
        {
            if (animal == null || animal.net == null || info == null)
                return null;
            return HandleDamageToDungeonAnimal(animal, info);
        }

        private object OnEntityTakeDamage(BaseNPC2 victim, HitInfo info)
        {
            if (victim == null || info == null)
                return null;
            if (!IsDungeonEntity(victim))
                return null;
            if (BlockAndNotifyEntranceAttack(info.InitiatorPlayer, victim))
            {
                info.damageTypes?.ScaleAll(0f);
                return true;
            }
            if (info.InitiatorPlayer == null || !IsPlayer(info.InitiatorPlayer))
                return true;
            float damage = info.damageTypes?.Total() ?? 0f;
            if (damage > 0f && victim.Health() - damage <= 0f)
                NextTick(() => ForceKillGen2DungeonAnimal(victim));
            return null;
        }

        private void ForceKillGen2DungeonAnimal(BaseCombatEntity entity)
        {
            if (entity == null || entity.IsDestroyed)
                return;
            if (!IsDungeonEntity(entity))
                return;
            if (!IsGen2Animal(entity))
                return;
            if (entity.Health() > 0f && !entity.IsDead())
                return;
            var rootMotion = entity.GetComponent<RootMotionPlayer>();
            if (rootMotion != null && rootMotion.currentPlayState != null)
                rootMotion.StopServer(rootMotion.currentPlayState, false);
            var fsm = entity.GetComponent<FSMComponent>();
            if (fsm != null)
                fsm.SetFsmActive(false);
            var movement = entity.GetComponent<DungeonAnimalMovement>();
            if (movement != null)
                UnityEngine.Object.Destroy(movement);
            var attack = entity.GetComponent<DungeonAnimalAttack>();
            if (attack != null)
                UnityEngine.Object.Destroy(attack);
            var gen2Aggro = entity.GetComponent<DungeonGen2AnimalAggroController>();
            if (gen2Aggro != null)
                UnityEngine.Object.Destroy(gen2Aggro);
            entity.CancelInvoke();
            var dungeon = GetActiveDungeonByEntity(entity);
            if (dungeon != null)
                MarkDungeonNpcDefeated(dungeon, entity);
            if (!entity.IsDestroyed)
                entity.Kill();
        }

        private void ForceKillGen1DungeonAnimal(BaseAnimalNPC animal, HitInfo info)
        {
            if (animal == null || animal.IsDestroyed)
                return;
            if (!IsDungeonEntity(animal))
                return;
            if (animal.Health() > 0f)
                return;
            if (animal.IsDead())
                return;
            var movement = animal.GetComponent<DungeonAnimalMovement>();
            if (movement != null)
                UnityEngine.Object.Destroy(movement);
            var attack = animal.GetComponent<DungeonAnimalAttack>();
            if (attack != null)
                UnityEngine.Object.Destroy(attack);
            var dungeon = GetActiveDungeonByEntity(animal);
            if (dungeon != null)
                MarkDungeonNpcDefeated(dungeon, animal);
            animal.Die(info);
        }

        private object CanEntityTakeDamage(BuildingBlock block, HitInfo info)
        {
            if (info == null || !IsDungeonEntity(block))
                return null;
            info.damageTypes?.ScaleAll(0f);
            return false;
        }

        private object CanEntityTakeDamage(BasePlayer victim, HitInfo info)
        {
            if (victim == null || info == null || !IsPlayer(victim))
                return null;
            var portalBubbleResult = HandlePortalBubblePlayerDamage(victim, info);
            if (portalBubbleResult != null)
                return portalBubbleResult;
            if (info.Initiator == null || info.Initiator.net == null)
                return null;
            if (info.Initiator is ScientistNPC && IsDungeonEntity(info.Initiator))
                return !IsPlayerInEntranceForEntity(victim, info.Initiator);
            if (info.Initiator is AutoTurret && IsDungeonEntity(info.Initiator))
                return !IsPlayerInEntranceForEntity(victim, info.Initiator);
            if (IsAnimalEntity(info.Initiator) && IsDungeonEntity(info.Initiator))
            {
                if (IsPlayerInEntranceForEntity(victim, info.Initiator))
                    return false;
                if (IsGen2Animal(info.Initiator))
                    return true;
                return IsDungeonAnimalDamageAllowed(victim, info.Initiator) ? (object)true : false;
            }
            return null;
        }

        private object CanEntityTakeDamage(AutoTurret autoTurret, HitInfo hitinfo)
        {
            if (autoTurret == null || hitinfo == null)
                return null;
            if (!IsDungeonEntity(autoTurret))
                return null;
            if (BlockAndNotifyEntranceAttack(hitinfo.InitiatorPlayer, autoTurret))
                return false;
            if (hitinfo.InitiatorPlayer == null || !IsPlayer(hitinfo.Initiator as BasePlayer))
                return false;
            return true;
        }

        private object CanEntityTakeDamage(StorageContainer storageContainer, HitInfo hitinfo)
        {
            if (storageContainer == null || hitinfo == null)
                return null;
            if (!IsDungeonEntity(storageContainer))
                return null;
            if (BlockAndNotifyEntranceAttack(hitinfo.InitiatorPlayer, storageContainer))
                return false;
            if (!IsPlayer(hitinfo.InitiatorPlayer))
                return false;
            return true;
        }

        private object CanEntityTakeDamage(Door victim, HitInfo hitinfo)
        {
            if (victim == null || hitinfo == null)
                return null;
            if (!IsDungeonEntity(victim))
                return null;
            if (hitinfo.InitiatorPlayer == null || !IsPlayer(hitinfo.Initiator as BasePlayer))
                return false;
            return true;
        }

        private object CanEntityTakeDamage(BaseNPC2 victim, HitInfo info)
        {
            if (victim == null || info == null)
                return null;
            if (!IsDungeonEntity(victim))
                return null;
            if (BlockAndNotifyEntranceAttack(info.InitiatorPlayer, victim))
                return false;
            if (info.InitiatorPlayer == null || !IsPlayer(info.InitiatorPlayer))
                return false;
            return true;
        }

        private object CanEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null)
                return null;
            if (entity is BasePlayer)
                return null;
            if (IsDungeonEntity(entity))
            {
                if (entity is StorageContainer storageContainer)
                    return CanEntityTakeDamage(storageContainer, info);
                if (entity is AutoTurret autoTurret)
                    return CanEntityTakeDamage(autoTurret, info);
                if (entity is Door door)
                    return CanEntityTakeDamage(door, info);
                if (entity is BaseNPC2 npc)
                    return CanEntityTakeDamage(npc, info);
                return null;
            }
            return HandlePortalBubbleEntityDamage(entity, info);
        }

        private object OnPlayerViolation(BasePlayer player, AntiHackType type, float amount, GameObject gameObject)
        {
            if (player == null || !player.IsConnected || _config?.DungeonSpawn == null)
                return null;
            if (type != AntiHackType.InsideTerrain)
                return null;
            var dungeon = GetUndergroundDungeonAtPosition(player.transform.position);
            if (dungeon == null || !dungeon.Spawned)
                return null;
            return false;
        }
        #endregion

        #region Data Initialization
        protected override void LoadConfig()
        {
            _lootFileCache.Clear();
            base.LoadConfig();
            try
            {
                JObject raw;
                var threshold = new VersionNumber(3, 6, 0);
                VersionNumber parsedVersion = new VersionNumber(0, 0, 0);
                var hasVersion = TryReadRawConfig(out raw) && TryParseConfigVersion(raw, out parsedVersion);
                if (!hasVersion || parsedVersion < threshold)
                {
                    PrintWarning($"Legacy config detected {(hasVersion ? parsedVersion.ToString() : "unknown")} updating to {threshold}+.");
                    LoadDefaultConfig();
                    _config.Version = Version;
                    EnsureLootDataFiles();
                    InitializeTierInfoMap();
                    SaveConfig();
                    return;
                }
                var mutated = MigrateTiersObjectToArray(raw);
                mutated |= MigrateBuildingGradeSelectionPropertyName(raw);
                mutated |= MigrateDungeonHeightPropertyName(raw);
                mutated |= MigrateOldWolfPrefabToWolf2(raw);
                mutated |= MigrateEnemyRewardProperties(raw);
                _config = raw.ToObject<Configuration>() ?? new Configuration();
                var changed = NormalizeAnimalConfigurations(raw);
                if (_config.Version == null || _config.Version != Version)
                {
                    _config.Version = Version;
                    changed = true;
                }
                EnsureLootDataFiles();
                InitializeTierInfoMap();
                if (mutated || changed)
                    SaveConfig();
            }
            catch (Exception ex)
            {
                PrintError($"Error reading config: {ex.Message}");
                LoadDefaultConfig();
                _config.Version = Version;
                EnsureLootDataFiles();
                InitializeTierInfoMap();
                SaveConfig();
            }
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Creating a new configuration file");
            _config = new Configuration { Version = Version };
            NormalizeAnimalConfigurations(null);
        }

        private static string ResolveAnimalNameFromLegacyPath(string prefab)
        {
            if (string.IsNullOrEmpty(prefab))
                return null;
            string fileName = Path.GetFileNameWithoutExtension(prefab);
            if (fileName.Equals("wolf2", StringComparison.OrdinalIgnoreCase) || fileName.Equals("wolf", StringComparison.OrdinalIgnoreCase))
                return "Wolf";
            return AnimalSpeciesOrder.FirstOrDefault(name => name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        }

        private bool NormalizeAnimalConfigurations(JObject raw)
        {
            if (_config?.Tiers == null)
                return false;
            bool changed = false;
            JArray oldTiers = raw?["Tiers"] as JArray;
            for (int index = 0; index < _config.Tiers.Count; index++)
            {
                DungeonTierConfig tier = _config.Tiers[index];
                if (tier == null)
                    continue;
                if (tier.AnimalConfig == null)
                {
                    tier.AnimalConfig = new DungeonAnimalConfig();
                    changed = true;
                }
                DungeonAnimalConfig cfg = tier.AnimalConfig;
                JObject oldAnimal = index < (oldTiers?.Count ?? 0) ? oldTiers[index]?["Animal Config"] as JObject : null;
                bool hasNewAnimals = oldAnimal?["Animals"] is JArray;
                float previousHealth = Mathf.Max(1f, oldAnimal?.Value<float?>("Health") ?? 300f);
                if (!hasNewAnimals)
                {
                    cfg.Animals = CreateDefaultAnimalSpecies();
                    foreach (var animal in cfg.Animals)
                        animal.Health = previousHealth;
                    var oldPrefabs = oldAnimal?["Animal Prefabs"] as JArray;
                    if (oldPrefabs != null)
                    {
                        foreach (var oldName in new[] { "Boar", "Wolf", "Stag", "Chicken", "Panther", "Tiger", "Crocodile" })
                            cfg.Animals.First(a => a.Animal == oldName).Enabled = false;
                        foreach (JObject entry in oldPrefabs.OfType<JObject>())
                        {
                            string id = ResolveAnimalNameFromLegacyPath(entry.Value<string>("Prefab Path"));
                            AnimalSpeciesConfig current = cfg.Animals.FirstOrDefault(a => a.Animal.Equals(id, StringComparison.OrdinalIgnoreCase));
                            if (current != null)
                            {
                                current.Enabled = true;
                                current.SpawnChancePercentage = Mathf.Max(0f, entry.Value<float?>("Spawn Chance Percentage") ?? 100f);
                            }
                        }
                    }
                    changed = true;
                }
                if (cfg.Animals == null)
                {
                    cfg.Animals = new List<AnimalSpeciesConfig>();
                    changed = true;
                }
                var valid = new Dictionary<string, AnimalSpeciesConfig>(StringComparer.OrdinalIgnoreCase);
                foreach (var candidate in cfg.Animals)
                {
                    if (candidate == null || string.IsNullOrEmpty(candidate.Animal) || !AnimalPrefabPaths.ContainsKey(candidate.Animal))
                    {
                        changed = true;
                        continue;
                    }
                    if (valid.ContainsKey(candidate.Animal))
                    {
                        changed = true;
                        continue;
                    }
                    valid[candidate.Animal] = candidate;
                }
                List<AnimalSpeciesConfig> normalized = new List<AnimalSpeciesConfig>();
                foreach (string name in AnimalSpeciesOrder)
                {
                    AnimalSpeciesConfig entry;
                    if (!valid.TryGetValue(name, out entry))
                    {
                        entry = new AnimalSpeciesConfig
                        {
                            Animal = name,
                            Enabled = true,
                            SpawnChancePercentage = 100f,
                            Health = previousHealth,
                        };
                        changed = true;
                    }
                    if (entry.Animal != name)
                    {
                        entry.Animal = name;
                        changed = true;
                    }
                    if (entry.Health <= 0f || float.IsNaN(entry.Health) || float.IsInfinity(entry.Health))
                    {
                        entry.Health = previousHealth;
                        changed = true;
                    }
                    normalized.Add(entry);
                }
                cfg.Animals = normalized;
            }
            return changed;
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(
                new Dictionary<string, string>
                {
                    ["AlreadyOwnsDungeonMessage"] = "You already own an active dungeon. You cannot purchase another one until it expires or is removed.",
                    ["DungeonAppeared"] = "A {0} has appeared at grid {1}!",
                    ["DungeonRemovedDetail"] = "Removed dungeon of tier {0} at grid {1}.",
                    ["DungeonSpawnedSuccess"] = "Dungeon spawned successfully at grid {0}!",
                    ["NpcSpawnNotLoaded"] = "NpcSpawn plugin is not loaded.",
                    ["DungeonStillSpawningMessage"] = "The dungeon is still spawning. Please wait until it's ready before entering.",
                    ["InsufficientFundsMessage"] = "You don't have enough funds to buy the {0} tier. Required: {1}.",
                    ["InvalidTierMessage"] = "Invalid tier name '{0}'. Available tiers: {1}.",
                    ["ThisTierDisabled"] = "This Tier is disabled for purchases",
                    ["MaxActiveDungeonsReached"] = "Cannot spawn: Maximum number of active dungeons reached.",
                    ["NoAccess"] = "You don't have permission to do this.",
                    ["NoTierPermission"] = "You do not have permission for the {0} dungeon. Required: {1}.",
                    ["PurchaseSuccessMessage"] = "You have successfully purchased the {0} tier!",
                    ["NoBuyPortalLocationAvailable"] = "There is no portal location available for purchase right now. Please try again later.",
                    ["ReceivedRequest"] = "Your request has been added to the queue. Please wait.",
                    ["UsageCreateMessage"] = "Usage: /createdungeon <tier_name>",
                    ["CleanedUp"] = "All entities related to this plugin have been cleaned up.",
                    ["NoInactiveDungeonsToRemove"] = "There are no inactive dungeons to remove.",
                    ["WithdrawFailedMessage"] = "Failed to withdraw funds for {0}.",
                    ["DungeonBuyTitle"] = "Buy Dungeon",
                    ["BuyButton"] = "{0} ({1})",
                    ["BuyButtonWithName"] = "{0} ({1} {2})",
                    ["BuyButtonFreeWithName"] = "{0} (Free)",
                    ["CloseButton"] = "Close",
                    ["DungeonRemovalCountdown"] = "Your dungeon will be removed in {0} minute(s)!",
                    ["BlockedCommands"] = "You cannot use commands inside a dungeon!",
                    ["EntranceCombatBlocked"] = "You cannot deal damage from the protected entrance. Move forward to fight.",
                    ["EconRewardMessage"] = "You received {0} RP!",
                    ["ServerRewardMessage"] = "You received {0} RN!",
                    ["DungeonPurchased"] = "A {0} dungeon purchased by {1} has appeared at grid {2}!",
                    ["DungeonCooldownMessage"] = "You must wait {0} seconds before purchasing another dungeon.",
                    ["DungeonClearEconomyReward"] = "You received {0} RP for clearing the dungeon!",
                    ["DungeonClearServerReward"] = "You received {0} RN for clearing the dungeon!",
                    ["Dungeonclosed"] = "Dungeon closed. You have been teleported to the exit.",
                    ["DungeonClearXpReward"] = "You received {0} XP for clearing the dungeon!",
                    ["NoDungeonsToRemove"] = "There are no dungeons to remove.",
                    ["TooFarToRemove"] = "You must be within 10 meters of a dungeon to remove it.",
                    ["NotDungeonOwner"] = "You don't own an active dungeon.",
                    ["ConfigReloaded"] = "Configuration reloaded.",
                    ["PurchasesEnabled"] = "Purchases are now enabled.",
                    ["PurchasesDisabled"] = "Purchases are now disabled.",
                },
                this
            );
            lang.RegisterMessages(
                new Dictionary<string, string>
                {
                    ["AlreadyOwnsDungeonMessage"] = "Você já possui uma masmorra ativa. Não é possível comprar outra antes que ela expire ou seja removida.",
                    ["DungeonAppeared"] = "Uma {0} apareceu na grade {1}!",
                    ["DungeonRemovedDetail"] = "Masmorra de nível {0} removida na grade {1}.",
                    ["DungeonSpawnedSuccess"] = "Masmorra gerada com sucesso na grade {0}!",
                    ["NpcSpawnNotLoaded"] = "O plugin NpcSpawn não está carregado.",
                    ["DungeonStillSpawningMessage"] = "A masmorra ainda está sendo gerada. Por favor, aguarde até que esteja pronta antes de entrar.",
                    ["InsufficientFundsMessage"] = "Você não tem fundos suficientes para comprar o nível {0}. Requerido: {1}.",
                    ["InvalidTierMessage"] = "Nome de nível inválido '{0}'. Níveis disponíveis: {1}.",
                    ["ThisTierDisabled"] = "Esta dificuldade está desativada para compras",
                    ["MaxActiveDungeonsReached"] = "Não é possível gerar: número máximo de masmorras ativas alcançado.",
                    ["NoAccess"] = "Você não tem permissão para fazer isso.",
                    ["NoTierPermission"] = "Você não tem permissão para a masmorra {0}. Necessário: {1}.",
                    ["PurchaseSuccessMessage"] = "Você comprou com sucesso o nível {0}!",
                    ["NoBuyPortalLocationAvailable"] = "No momento não há nenhum local de portal disponível para compra. Tente novamente mais tarde.",
                    ["ReceivedRequest"] = "Seu pedido foi adicionado à fila. Por favor aguarde.",
                    ["UsageCreateMessage"] = "Uso: /createdungeon <nome_do_nível>",
                    ["CleanedUp"] = "Todos os objetos relacionados a este plugin foram removidos.",
                    ["NoInactiveDungeonsToRemove"] = "Não há masmorras inativas para remover.",
                    ["WithdrawFailedMessage"] = "Falha ao retirar fundos para {0}.",
                    ["DungeonBuyTitle"] = "Comprar Masmorra",
                    ["BuyButton"] = "{0} ({1})",
                    ["BuyButtonWithName"] = "{0} ({1} {2})",
                    ["BuyButtonFreeWithName"] = "{0} (Grátis)",
                    ["CloseButton"] = "Fechar",
                    ["DungeonRemovalCountdown"] = "Sua masmorra será removida em {0} minuto(s)!",
                    ["BlockedCommands"] = "Você não pode usar comandos dentro de uma masmorra!",
                    ["EntranceCombatBlocked"] = "Você não pode causar dano da entrada protegida. Avance para lutar.",
                    ["EconRewardMessage"] = "Você recebeu {0} RP!",
                    ["ServerRewardMessage"] = "Você recebeu {0} pontos de RN!",
                    ["DungeonPurchased"] = "Uma masmorra {0} comprada por {1} apareceu na grade {2}!",
                    ["DungeonCooldownMessage"] = "Você deve esperar {0} segundo(s) antes de comprar outra masmorra.",
                    ["DungeonClearEconomyReward"] = "Você recebeu {0} RP por limpar a masmorra!",
                    ["DungeonClearServerReward"] = "Você recebeu {0} pontos de RN por limpar a masmorra!",
                    ["Dungeonclosed"] = "Masmorra fechada. Você foi teleportado para a saída.",
                    ["DungeonClearXpReward"] = "Você recebeu {0} XP por limpar a masmorra!",
                    ["NoDungeonsToRemove"] = "Não há masmorras para remover.",
                    ["TooFarToRemove"] = "Você deve estar a até 10 metros de uma masmorra para removê-la.",
                    ["NotDungeonOwner"] = "Você não possui uma masmorra ativa.",
                    ["ConfigReloaded"] = "Configuração recarregada.",
                    ["PurchasesEnabled"] = "Compras agora estão habilitadas.",
                    ["PurchasesDisabled"] = "Compras agora estão desabilitadas.",
                },
                this,
                "pt-BR"
            );
            lang.RegisterMessages(
                new Dictionary<string, string>
                {
                    ["AlreadyOwnsDungeonMessage"] = "Du besitzt bereits ein aktives Verlies. Du kannst kein weiteres kaufen, bevor es abläuft oder entfernt wird.",
                    ["DungeonAppeared"] = "Ein {0} ist bei Gitter {1} erschienen!",
                    ["DungeonRemovedDetail"] = "Verlies der Stufe {0} bei Gitter {1} wurde entfernt.",
                    ["DungeonSpawnedSuccess"] = "Verlies erfolgreich bei Gitter {0} erstellt!",
                    ["NpcSpawnNotLoaded"] = "Das Plugin NpcSpawn ist nicht geladen.",
                    ["DungeonStillSpawningMessage"] = "Das Verlies wird noch erstellt. Bitte warte, bis es bereit ist, bevor du es betrittst.",
                    ["InsufficientFundsMessage"] = "Du hast nicht genug Geld, um die Stufe {0} zu kaufen. Erforderlich: {1}.",
                    ["InvalidTierMessage"] = "Ungültiger Stufenname '{0}'. Verfügbare Stufen: {1}.",
                    ["ThisTierDisabled"] = "Diese Schwierigkeitsstufe ist für Käufe deaktiviert",
                    ["MaxActiveDungeonsReached"] = "Kann nicht erstellt werden: Maximale Anzahl aktiver Verliese erreicht.",
                    ["NoAccess"] = "Du hast keine Berechtigung, dies zu tun.",
                    ["NoTierPermission"] = "Du hast keine Berechtigung für das Verlies {0}. Benötigt: {1}.",
                    ["PurchaseSuccessMessage"] = "Du hast die Stufe {0} erfolgreich gekauft!",
                    ["NoBuyPortalLocationAvailable"] = "Derzeit ist kein Portalplatz für einen Kauf verfügbar. Bitte versuche es später erneut.",
                    ["ReceivedRequest"] = "Deine Anfrage wurde der Warteschlange hinzugefügt. Bitte warte.",
                    ["UsageCreateMessage"] = "Verwendung: /createdungeon <stufen_name>",
                    ["CleanedUp"] = "Alle mit diesem Plugin verbundenen Objekte wurden entfernt.",
                    ["NoInactiveDungeonsToRemove"] = "Es gibt keine inaktiven Verliese zum Entfernen.",
                    ["WithdrawFailedMessage"] = "Fehler beim Abheben der Mittel für {0}.",
                    ["DungeonBuyTitle"] = "Verlies kaufen",
                    ["BuyButton"] = "{0} ({1})",
                    ["BuyButtonWithName"] = "{0} ({1} {2})",
                    ["BuyButtonFreeWithName"] = "{0} (Kostenlos)",
                    ["CloseButton"] = "Schließen",
                    ["DungeonRemovalCountdown"] = "Dein Verlies wird in {0} Minute(n) entfernt!",
                    ["BlockedCommands"] = "Du kannst in einem Verlies keine Befehle verwenden!",
                    ["EntranceCombatBlocked"] = "Vom geschützten Eingang aus kannst du keinen Schaden verursachen. Geh weiter, um zu kämpfen.",
                    ["EconRewardMessage"] = "Du hast {0} Münzen erhalten!",
                    ["ServerRewardMessage"] = "Du hast {0} RP erhalten!",
                    ["DungeonPurchased"] = "Ein {0} Verlies, gekauft von {1}, ist bei Gitter {2} erschienen!",
                    ["DungeonCooldownMessage"] = "Du musst {0} Sekunde(n) warten, bevor du ein weiteres Verlies kaufen kannst.",
                    ["DungeonClearEconomyReward"] = "Du hast {0} RP erhalten, weil du das Verlies vollständig geleert hast!",
                    ["DungeonClearServerReward"] = "Du hast {0} Serverpunkte erhalten, weil du das Verlies vollständig geleert hast!",
                    ["Dungeonclosed"] = "Verlies geschlossen. Du wurdest zum Ausgang teleportiert.",
                    ["DungeonClearXpReward"] = "Du hast {0} XP erhalten, weil du das Verlies vollständig geleert hast!",
                    ["NoDungeonsToRemove"] = "Es gibt keine Verliese zum Entfernen.",
                    ["TooFarToRemove"] = "Du musst dich innerhalb von 10 Metern eines Verlieses befinden, um es zu entfernen.",
                    ["NotDungeonOwner"] = "Du besitzt kein aktives Verlies.",
                    ["ConfigReloaded"] = "Konfiguration neu geladen.",
                    ["PurchasesEnabled"] = "Käufe sind jetzt aktiviert.",
                    ["PurchasesDisabled"] = "Käufe sind jetzt deaktiviert.",
                },
                this,
                "de"
            );
            lang.RegisterMessages(
                new Dictionary<string, string>
                {
                    ["AlreadyOwnsDungeonMessage"] = "Ya posees una mazmorra activa. No puedes comprar otra hasta que expire o sea eliminada.",
                    ["DungeonAppeared"] = "¡Una {0} ha aparecido en la cuadrícula {1}!",
                    ["DungeonRemovedDetail"] = "Mazmorra de nivel {0} eliminada en la cuadrícula {1}.",
                    ["DungeonSpawnedSuccess"] = "¡Mazmorra creada exitosamente en la cuadrícula {0}!",
                    ["NpcSpawnNotLoaded"] = "El plugin NpcSpawn no está cargado.",
                    ["DungeonStillSpawningMessage"] = "La mazmorra aún está generándose. Por favor, espera antes de entrar.",
                    ["InsufficientFundsMessage"] = "No tienes suficientes fondos para comprar el nivel {0}. Requerido: {1}.",
                    ["InvalidTierMessage"] = "Nombre de nivel inválido '{0}'. Niveles disponibles: {1}.",
                    ["ThisTierDisabled"] = "Esta dificultad está deshabilitada para compras",
                    ["MaxActiveDungeonsReached"] = "No se puede generar: se alcanzó el número máximo de mazmorras activas.",
                    ["NoAccess"] = "No tienes permiso para hacer esto.",
                    ["NoTierPermission"] = "No tienes permiso para la mazmorra {0}. Requerido: {1}.",
                    ["PurchaseSuccessMessage"] = "¡Has comprado con éxito el nivel {0}!",
                    ["NoBuyPortalLocationAvailable"] = "No hay ninguna ubicación de portal disponible para comprar en este momento. Inténtalo de nuevo más tarde.",
                    ["ReceivedRequest"] = "Tu solicitud se ha añadido a la cola. Por favor, espera.",
                    ["UsageCreateMessage"] = "Uso: /createdungeon <nombre_del_nivel>",
                    ["CleanedUp"] = "Todos los objetos relacionados con este plugin han sido eliminados.",
                    ["NoInactiveDungeonsToRemove"] = "No hay mazmorras inactivas para eliminar.",
                    ["WithdrawFailedMessage"] = "Error al retirar fondos para {0}.",
                    ["DungeonBuyTitle"] = "Comprar Mazmorra",
                    ["BuyButton"] = "{0} ({1})",
                    ["BuyButtonWithName"] = "{0} ({1} {2})",
                    ["BuyButtonFreeWithName"] = "{0} (Gratis)",
                    ["CloseButton"] = "Cerrar",
                    ["DungeonRemovalCountdown"] = "¡Tu mazmorra será eliminada en {0} minuto(s)!",
                    ["BlockedCommands"] = "¡No puedes usar comandos dentro de una mazmorra!",
                    ["EntranceCombatBlocked"] = "No puedes causar daño desde la entrada protegida. Avanza para combatir.",
                    ["EconRewardMessage"] = "¡Has recibido {0} RP!",
                    ["ServerRewardMessage"] = "¡Has recibido {0} puntos de RN!",
                    ["DungeonPurchased"] = "¡Una mazmorra {0} comprada por {1} ha aparecido en la cuadrícula {2}!",
                    ["DungeonCooldownMessage"] = "Debes esperar {0} segundo(s) antes de comprar otra mazmorra.",
                    ["DungeonClearEconomyReward"] = "¡Has recibido {0} RP por vaciar completamente la mazmorra!",
                    ["DungeonClearServerReward"] = "¡Has recibido {0} puntos de RN por vaciar completamente la mazmorra!",
                    ["Dungeonclosed"] = "Mazmorra cerrada. Has sido teletransportado a la salida.",
                    ["DungeonClearXpReward"] = "¡Has recibido {0} XP por vaciar completamente la mazmorra!",
                    ["NoDungeonsToRemove"] = "No hay mazmorras para eliminar.",
                    ["TooFarToRemove"] = "Debes estar a menos de 10 metros de una mazmorra para eliminarla.",
                    ["NotDungeonOwner"] = "No posees una mazmorra activa.",
                    ["ConfigReloaded"] = "Configuración recargada.",
                    ["PurchasesEnabled"] = "Las compras ahora están habilitadas.",
                    ["PurchasesDisabled"] = "Las compras ahora están deshabilitadas.",
                },
                this,
                "es"
            );
            lang.RegisterMessages(
                new Dictionary<string, string>
                {
                    ["AlreadyOwnsDungeonMessage"] = "У вас уже есть активное подземелье. Вы не можете приобрести другое, пока оно не истечет или не будет удалено.",
                    ["DungeonAppeared"] = "Подземелье {0} появилось в сетке {1}!",
                    ["DungeonRemovedDetail"] = "Удалено подземелье уровня {0} в сетке {1}.",
                    ["DungeonSpawnedSuccess"] = "Подземелье успешно создано в сетке {0}!",
                    ["NpcSpawnNotLoaded"] = "Плагин NpcSpawn не загружен.",
                    ["DungeonStillSpawningMessage"] = "Подземелье еще загружается. Пожалуйста, подождите, прежде чем войти.",
                    ["InsufficientFundsMessage"] = "У вас недостаточно средств для покупки уровня {0}. Нужно: {1}.",
                    ["InvalidTierMessage"] = "Неверное название уровня '{0}'. Доступные уровни: {1}.",
                    ["ThisTierDisabled"] = "Этот уровень сложности отключен для покупок",
                    ["MaxActiveDungeonsReached"] = "Невозможно создать: достигнуто максимальное количество активных подземелий.",
                    ["NoAccess"] = "У вас нет разрешения на это действие.",
                    ["NoTierPermission"] = "У вас нет разрешения на подземелье {0}. Требуется: {1}.",
                    ["PurchaseSuccessMessage"] = "Вы успешно приобрели уровень {0}!",
                    ["NoBuyPortalLocationAvailable"] = "Сейчас нет свободной точки портала для покупки. Попробуйте снова позже.",
                    ["ReceivedRequest"] = "Ваш запрос был добавлен в очередь. Пожалуйста, подождите.",
                    ["UsageCreateMessage"] = "Использование: /createdungeon <название_уровня>",
                    ["CleanedUp"] = "Все объекты, связанные с этим плагином, были удалены.",
                    ["NoInactiveDungeonsToRemove"] = "Нет неактивных подземелий для удаления.",
                    ["WithdrawFailedMessage"] = "Ошибка при попытке вывести средства для {0}.",
                    ["DungeonBuyTitle"] = "Купить Подземелье",
                    ["BuyButton"] = "{0} ({1})",
                    ["BuyButtonWithName"] = "{0} ({1} {2})",
                    ["BuyButtonFreeWithName"] = "{0} (Бесплатно)",
                    ["CloseButton"] = "Закрыть",
                    ["DungeonRemovalCountdown"] = "Ваше подземелье будет удалено через {0} минут(ы)!",
                    ["BlockedCommands"] = "Вы не можете использовать команды внутри подземелья!",
                    ["EntranceCombatBlocked"] = "Из защищённого входа нельзя наносить урон. Пройдите вперёд, чтобы сражаться.",
                    ["EconRewardMessage"] = "Вы получили {0} RP!",
                    ["ServerRewardMessage"] = "Вы получили {0} RN!",
                    ["DungeonPurchased"] = "Подземелье {0}, купленное {1}, появилось на сетке {2}!",
                    ["DungeonCooldownMessage"] = "Вы должны подождать {0} секунд(ы), прежде чем купить другое подземелье.",
                    ["DungeonClearEconomyReward"] = "Вы получили {0} RP за полное опустошение подземелья!",
                    ["DungeonClearServerReward"] = "Вы получили {0} RN за полное опустошение подземелья!",
                    ["Dungeonclosed"] = "Подземелье закрыто. Вы были телепортированы к выходу.",
                    ["DungeonClearXpReward"] = "Вы получили {0} XP за полное опустошение подземелья!",
                    ["NoDungeonsToRemove"] = "Нет подземелий для удаления.",
                    ["TooFarToRemove"] = "Вы должны находиться в пределах 10 метров от подземелья, чтобы удалить его.",
                    ["NotDungeonOwner"] = "Вы не являетесь владельцем активного подземелья.",
                    ["ConfigReloaded"] = "Конфигурация перезагружена.",
                    ["PurchasesEnabled"] = "Покупки теперь включены.",
                    ["PurchasesDisabled"] = "Покупки теперь отключены.",
                },
                this,
                "ru"
            );
        }
        #endregion

        #region Configuration
        private class Configuration
        {
            [JsonProperty("Enable Purchases")]
            public bool EnablePurchases { get; set; } = true;

            [JsonProperty("Use Notify Plugin")]
            public bool UseNotify { get; set; } = false;

            [JsonProperty("Enable Debug Logs")]
            public bool EnableDebugLogs { get; set; } = false;

            [JsonProperty("Enable Warning Logs")]
            public bool EnableWarningLogs { get; set; } = true;

            [JsonProperty("NPC Max Move Distance")]
            public float MaxNpcDistanceFromSpawnPoint { get; set; } = 30f;

            [JsonProperty("NPC Attack Distance")]
            public float NpcAttackDistanceOverride { get; set; } = 0f;

            [JsonProperty("NPC Seconds Shooting Before Move (0 = disabled)")]
            public float NpcSecondsShootingBeforeLateralMove { get; set; } = 2f;

            [JsonProperty("Enable Toast Messages")]
            public bool EnableShowToast { get; set; } = true;

            [JsonProperty("Enable Dungeon Lights")]
            public bool EnableDungeonLights { get; set; } = true;

            [JsonProperty("Reset NPCs And Animals When The Last Player Dies Or Leaves The Dungeon")]
            public bool ResetEnemiesOnPlayerDeparture { get; set; } = true;

            [JsonProperty("Place Entrance Room Doors And Frames")]
            public bool PlaceEntranceDoors { get; set; } = true;

            [JsonProperty("Place Dungeon Doors And Frames (Except Entrance Room)")]
            public bool PlaceDungeonDoors { get; set; } = true;

            [JsonProperty("Close Entrance Room Doors When No Players Remain Inside (Left Or Died)")]
            public bool CloseEntranceDoorsWhenEmpty { get; set; } = true;

            [JsonProperty("Close Dungeon Doors (Except Entrance Room) When No Players Remain Inside (Left Or Died)")]
            public bool CloseDungeonDoorsWhenEmpty { get; set; } = false;

            [JsonProperty("Dungeon Light Spacing")]
            public float DungeonLightSpacing { get; set; } = 30f;

            [JsonProperty("Prevent Drop Backpack")]
            public bool PreventDropBackpack { get; set; } = true;

            [JsonProperty("Teleport Player To Outside On Death")]
            public bool TeleportPlayerOnDeath { get; set; } = true;

            [JsonProperty("Spawn Point Configuration")]
            public SpawnPointConfiguration SpawnPointConfiguration { get; set; } = new SpawnPointConfiguration();

            [JsonProperty("Custom Portal Spawn Locations")]
            public CustomPortalSpawnConfiguration CustomPortalSpawnLocations { get; set; } = new CustomPortalSpawnConfiguration();

            [JsonProperty("Place Sphere on Dungeon Portal")]
            public bool EnablePlaceSphere { get; set; } = true;

            [JsonProperty("Minimum Distance Between Portals")]
            public float MinimumPortalDistanceBetweenPortals { get; set; } = 50f;

            [JsonProperty("Enable Portal Protection Bubble")]
            public bool EnablePortalProtectionBubble { get; set; } = true;

            [JsonProperty("Portal Protection Bubble Radius")]
            public float PortalProtectionBubbleRadius { get; set; } = 10f;

            [JsonProperty("Allow Owner And Allies To Build In Portal Protection Bubble")]
            public bool AllowOwnerAndAlliesToBuildInPortalProtectionBubble { get; set; } = true;

            [JsonProperty("Allow Owner And Allies To Deploy In Portal Protection Bubble")]
            public bool AllowOwnerAndAlliesToDeployInPortalProtectionBubble { get; set; } = true;

            [JsonProperty("Block Damage From Outside To Owner And Allies Inside Portal Protection Bubble")]
            public bool BlockDamageFromOutsideToOwnerAndAlliesInsidePortalProtectionBubble { get; set; } = true;

            [JsonProperty("Block Damage From Owner And Allies Inside Portal Protection Bubble To Outside")]
            public bool BlockDamageFromOwnerAndAlliesInsidePortalProtectionBubbleToOutside { get; set; } = true;

            [JsonProperty("Protect Owner And Allies Vehicles Inside Portal Protection Bubble")]
            public bool ProtectOwnerAndAlliesVehiclesInsidePortalProtectionBubble { get; set; } = true;

            [JsonProperty("NightVision Plugin Time On Leaving Dungeon(-1 for no override)")]
            public int NightVisionTime { get; set; } = 12;

            [JsonProperty("Lock dungeon for first player")]
            public bool LockDungeonToPlayer { get; set; } = true;

            [JsonProperty("Economy Plugin (1 - Economics, 2 - ServerRewards, 3 - Custom)")]
            public int EconomyPlugin { get; set; } = 3;

            [JsonProperty("Custom Economy Item Name")]
            public string CustomEconomyItemName { get; set; } = "scrap";

            [JsonProperty("Custom Economy Item Skin ID")]
            public ulong CustomEconomyItemSkinID { get; set; } = 0UL;

            [JsonProperty("Custom Economy Display Name")]
            public string CustomEconomyDisplayName { get; set; } = "Scrap";

            [JsonProperty("Economics Display Name")]
            public string EconomicsDisplayName { get; set; } = "RP";

            [JsonProperty("ServerRewards Display Name")]
            public string ServerRewardsDisplayName { get; set; } = "RN";

            [JsonProperty("Enable SkillTree XP Team Sharing")]
            public bool EnableSkillTreeXpTeamSharing { get; set; } = true;

            [JsonProperty("SkillTree XP Share Mode (0 = Split total between players, 1 = Give full amount to each player)")]
            public int SkillTreeXpShareMode { get; set; } = 1;

            public string GetEconomyItemName()
            {
                return EconomyPlugin switch
                {
                    1 => "economics",
                    2 => "serverrewards",
                    _ => CustomEconomyItemName,
                };
            }

            public string GetEconomyDisplayName()
            {
                return EconomyPlugin switch
                {
                    1 => EconomicsDisplayName,
                    2 => ServerRewardsDisplayName,
                    _ => CustomEconomyDisplayName,
                };
            }

            public ulong GetEconomyItemSkinID()
            {
                return EconomyPlugin == 3 ? CustomEconomyItemSkinID : 0UL;
            }

            [JsonProperty("Allow team members")]
            public bool AllowTeam { get; set; } = true;

            [JsonProperty("Allow friends")]
            public bool AllowFriends { get; set; } = true;

            [JsonProperty("Performance Settings")]
            public DungeonPerformanceSettings Performance { get; set; } = new DungeonPerformanceSettings();

            [JsonProperty("Dungeon Settings")]
            public DungeonSpawnSettings DungeonSpawn { get; set; } = new DungeonSpawnSettings();

            [JsonProperty("Blocked commands inside a dungeon", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public HashSet<string> BlockCommands { get; set; } = new HashSet<string> { "/remove", "/tpa", "remove.toggle" };

            [JsonProperty("Tiers", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<DungeonTierConfig> Tiers { get; set; } =
                new List<DungeonTierConfig>
                {
                    new DungeonTierConfig
                    {
                        TierName = "Easy",
                        MapName = "Dungeon: Easy",
                        MarkerColorHex = "#3cd834",
                        BuildingGradeSelection = 14,
                        ContainerColor = 0,
                        DungeonRooms = 1,
                        DungeonRoomSize = 5,
                        BuyCost = 300,
                        EntitySpawnLimits = new EntitySpawnLimits
                        {
                            NpcsCounts = 7,
                            BossesCounts = 1,
                            AnimalsCounts = 4,
                            TurretsCounts = 5,
                            LootBoxesCounts = 5,
                        },
                        AutoTurretConfig = new TurretConfig { Health = 300, WeaponShortName = "pistol.revolver" },
                        LootBoxSkinID = 2998755525,
                        MaxItemsPerBox = 3,
                        MaxItemsPerNpc = 3,
                        MaxItemsPerBossNpc = 3,
                        NpcConfig = new NpcConfig
                        {
                            Name = "Wild Scavenger",
                            Health = 100,
                            DamageScale = 0.5f,
                            WearItems = new WearConfig
                            {
                                Items = new List<WearItemConfig>
                                {
                                    new WearItemConfig { ShortName = "tshirt.long", SkinID = 10118 },
                                    new WearItemConfig { ShortName = "burlap.gloves", SkinID = 949616124 },
                                    new WearItemConfig { ShortName = "attire.snowman.helmet", SkinID = 0 },
                                    new WearItemConfig { ShortName = "pants", SkinID = 2352962213 },
                                    new WearItemConfig { ShortName = "shoes.boots", SkinID = 2380731293 },
                                },
                            },
                            BeltItems = new BeltConfig
                            {
                                Items = new List<BeltItemConfig>
                                {
                                    new BeltItemConfig
                                    {
                                        ShortName = "pistol.revolver",
                                        Amount = 1,
                                        SkinID = 3140577175,
                                    },
                                },
                            },
                        },
                        BossNpcConfig = new NpcConfig
                        {
                            Name = "Easy Dungeon Boss",
                            Health = 1000,
                            DamageScale = 1.5f,
                            WearItems = new WearConfig
                            {
                                Items = new List<WearItemConfig>
                                {
                                    new WearItemConfig { ShortName = "riot.helmet", SkinID = 1087995729 },
                                    new WearItemConfig { ShortName = "hoodie", SkinID = 1087990973 },
                                    new WearItemConfig { ShortName = "burlap.gloves", SkinID = 1087998101 },
                                    new WearItemConfig { ShortName = "pants", SkinID = 1087992342 },
                                    new WearItemConfig { ShortName = "shoes.boots", SkinID = 1088000573 },
                                },
                            },
                            BeltItems = new BeltConfig
                            {
                                Items = new List<BeltItemConfig>
                                {
                                    new BeltItemConfig
                                    {
                                        ShortName = "rifle.ak",
                                        Amount = 1,
                                        SkinID = 3368362976,
                                        Mods = new List<string> { "weapon.mod.flashlight" },
                                    },
                                },
                            },
                        },
                    },
                    new DungeonTierConfig
                    {
                        TierName = "Normal",
                        MapName = "Dungeon: Normal",
                        MarkerColorHex = "#e7c50a",
                        BuildingGradeSelection = 2,
                        ContainerColor = 0,
                        DungeonRooms = 2,
                        DungeonRoomSize = 5,
                        BuyCost = 500,
                        EntitySpawnLimits = new EntitySpawnLimits
                        {
                            NpcsCounts = 14,
                            BossesCounts = 2,
                            AnimalsCounts = 6,
                            TurretsCounts = 10,
                            LootBoxesCounts = 10,
                        },
                        AutoTurretConfig = new TurretConfig { Health = 400, WeaponShortName = "pistol.revolver" },
                        LootBoxSkinID = 2998755525,
                        MaxItemsPerBox = 4,
                        MaxItemsPerNpc = 4,
                        MaxItemsPerBossNpc = 4,
                        NpcConfig = new NpcConfig
                        {
                            Name = "Battle Forager",
                            Health = 150,
                            DamageScale = 0.8f,
                            WearItems = new WearConfig
                            {
                                Items = new List<WearItemConfig>
                                {
                                    new WearItemConfig { ShortName = "bucket.helmet", SkinID = 1073904216 },
                                    new WearItemConfig { ShortName = "jacket", SkinID = 2350426469 },
                                    new WearItemConfig { ShortName = "tshirt", SkinID = 10039 },
                                    new WearItemConfig { ShortName = "burlap.gloves.new", SkinID = 0 },
                                    new WearItemConfig { ShortName = "pants", SkinID = 1441311938 },
                                    new WearItemConfig { ShortName = "shoes.boots", SkinID = 2075527039 },
                                },
                            },
                            BeltItems = new BeltConfig
                            {
                                Items = new List<BeltItemConfig>
                                {
                                    new BeltItemConfig
                                    {
                                        ShortName = "smg.2",
                                        Amount = 1,
                                        SkinID = 2386688842,
                                        Mods = new List<string> { "weapon.mod.flashlight" },
                                    },
                                },
                            },
                        },
                        BossNpcConfig = new NpcConfig
                        {
                            Name = "Normal Dungeon Boss",
                            Health = 1500,
                            DamageScale = 1.5f,
                            WearItems = new WearConfig
                            {
                                Items = new List<WearItemConfig>
                                {
                                    new WearItemConfig { ShortName = "riot.helmet", SkinID = 1087995729 },
                                    new WearItemConfig { ShortName = "hoodie", SkinID = 1087990973 },
                                    new WearItemConfig { ShortName = "burlap.gloves", SkinID = 1087998101 },
                                    new WearItemConfig { ShortName = "pants", SkinID = 1087992342 },
                                    new WearItemConfig { ShortName = "shoes.boots", SkinID = 1088000573 },
                                },
                            },
                            BeltItems = new BeltConfig
                            {
                                Items = new List<BeltItemConfig>
                                {
                                    new BeltItemConfig
                                    {
                                        ShortName = "lmg.m249",
                                        Amount = 1,
                                        SkinID = 1883947256,
                                        Mods = new List<string> { "weapon.mod.flashlight" },
                                    },
                                },
                            },
                        },
                    },
                    new DungeonTierConfig
                    {
                        TierName = "Medium",
                        MapName = "Dungeon: Medium",
                        MarkerColorHex = "#e57109",
                        BuildingGradeSelection = 6,
                        ContainerColor = 0,
                        DungeonRooms = 3,
                        DungeonRoomSize = 5,
                        BuyCost = 700,
                        EntitySpawnLimits = new EntitySpawnLimits
                        {
                            NpcsCounts = 21,
                            BossesCounts = 5,
                            AnimalsCounts = 8,
                            TurretsCounts = 15,
                            LootBoxesCounts = 15,
                        },
                        AutoTurretConfig = new TurretConfig { Health = 600, WeaponShortName = "rifle.ak" },
                        LootBoxSkinID = 2998755525,
                        MaxItemsPerBox = 5,
                        MaxItemsPerNpc = 5,
                        MaxItemsPerBossNpc = 5,
                        NpcConfig = new NpcConfig
                        {
                            Name = "Crimson Raider",
                            Health = 200,
                            DamageScale = 1.1f,
                            WearItems = new WearConfig
                            {
                                Items = new List<WearItemConfig>
                                {
                                    new WearItemConfig { ShortName = "coffeecan.helmet", SkinID = 1727561127 },
                                    new WearItemConfig { ShortName = "roadsign.jacket", SkinID = 1727562915 },
                                    new WearItemConfig { ShortName = "roadsign.gloves", SkinID = 2799639349 },
                                    new WearItemConfig { ShortName = "pants", SkinID = 2814837980 },
                                    new WearItemConfig { ShortName = "shoes.boots", SkinID = 1839313604 },
                                    new WearItemConfig { ShortName = "tshirt.long", SkinID = 566893368 },
                                },
                            },
                            BeltItems = new BeltConfig
                            {
                                Items = new List<BeltItemConfig>
                                {
                                    new BeltItemConfig
                                    {
                                        ShortName = "rifle.semiauto",
                                        Amount = 1,
                                        SkinID = 2617680693,
                                        Mods = new List<string> { "weapon.mod.flashlight" },
                                    },
                                    new BeltItemConfig
                                    {
                                        ShortName = "syringe.medical",
                                        Amount = 10,
                                        SkinID = 0,
                                    },
                                },
                            },
                        },
                        BossNpcConfig = new NpcConfig
                        {
                            Name = "Medium Dungeon Boss",
                            Health = 2000,
                            DamageScale = 1.5f,
                            WearItems = new WearConfig
                            {
                                Items = new List<WearItemConfig>
                                {
                                    new WearItemConfig { ShortName = "riot.helmet", SkinID = 1087995729 },
                                    new WearItemConfig { ShortName = "hoodie", SkinID = 1087990973 },
                                    new WearItemConfig { ShortName = "burlap.gloves", SkinID = 1087998101 },
                                    new WearItemConfig { ShortName = "pants", SkinID = 1087992342 },
                                    new WearItemConfig { ShortName = "shoes.boots", SkinID = 1088000573 },
                                },
                            },
                            BeltItems = new BeltConfig
                            {
                                Items = new List<BeltItemConfig>
                                {
                                    new BeltItemConfig
                                    {
                                        ShortName = "lmg.m249",
                                        Amount = 1,
                                        SkinID = 1883947256,
                                        Mods = new List<string> { "weapon.mod.flashlight" },
                                    },
                                },
                            },
                        },
                    },
                    new DungeonTierConfig
                    {
                        TierName = "Hard",
                        MapName = "Dungeon: Hard",
                        MarkerColorHex = "#9c1825",
                        BuildingGradeSelection = 14,
                        ContainerColor = 5,
                        DungeonRooms = 4,
                        DungeonRoomSize = 5,
                        BuyCost = 1000,
                        EntitySpawnLimits = new EntitySpawnLimits
                        {
                            NpcsCounts = 26,
                            BossesCounts = 5,
                            AnimalsCounts = 8,
                            TurretsCounts = 20,
                            LootBoxesCounts = 25,
                        },
                        AutoTurretConfig = new TurretConfig { Health = 800, WeaponShortName = "rifle.ak" },
                        LootBoxSkinID = 2998755525,
                        MaxItemsPerBox = 6,
                        MaxItemsPerNpc = 6,
                        MaxItemsPerBossNpc = 6,
                        NpcConfig = new NpcConfig
                        {
                            Name = "Elite Outlaw",
                            Health = 250,
                            DamageScale = 1.3f,
                            WearItems = new WearConfig
                            {
                                Items = new List<WearItemConfig>
                                {
                                    new WearItemConfig { ShortName = "metal.facemask", SkinID = 3284864766 },
                                    new WearItemConfig { ShortName = "metal.plate.torso", SkinID = 2105505757 },
                                    new WearItemConfig { ShortName = "burlap.gloves", SkinID = 2090790324 },
                                    new WearItemConfig { ShortName = "hoodie", SkinID = 2080975449 },
                                    new WearItemConfig { ShortName = "shoes.boots", SkinID = 10023 },
                                    new WearItemConfig { ShortName = "pants", SkinID = 2080977144 },
                                    new WearItemConfig { ShortName = "roadsign.kilt", SkinID = 2120628865 },
                                },
                            },
                            BeltItems = new BeltConfig
                            {
                                Items = new List<BeltItemConfig>
                                {
                                    new BeltItemConfig
                                    {
                                        ShortName = "rifle.ak",
                                        Amount = 1,
                                        SkinID = 3190379864,
                                        Mods = new List<string> { "weapon.mod.flashlight" },
                                    },
                                    new BeltItemConfig
                                    {
                                        ShortName = "syringe.medical",
                                        Amount = 20,
                                        SkinID = 0,
                                    },
                                },
                            },
                        },
                        BossNpcConfig = new NpcConfig
                        {
                            Name = "Hard Dungeon Boss",
                            Health = 3000,
                            DamageScale = 1.5f,
                            WearItems = new WearConfig
                            {
                                Items = new List<WearItemConfig>
                                {
                                    new WearItemConfig { ShortName = "riot.helmet", SkinID = 1087995729 },
                                    new WearItemConfig { ShortName = "hoodie", SkinID = 1087990973 },
                                    new WearItemConfig { ShortName = "burlap.gloves", SkinID = 1087998101 },
                                    new WearItemConfig { ShortName = "pants", SkinID = 1087992342 },
                                    new WearItemConfig { ShortName = "shoes.boots", SkinID = 1088000573 },
                                },
                            },
                            BeltItems = new BeltConfig
                            {
                                Items = new List<BeltItemConfig>
                                {
                                    new BeltItemConfig
                                    {
                                        ShortName = "lmg.m249",
                                        Amount = 1,
                                        SkinID = 1883947256,
                                        Mods = new List<string> { "weapon.mod.flashlight" },
                                    },
                                },
                            },
                        },
                    },
                    new DungeonTierConfig
                    {
                        TierName = "Nightmare",
                        MapName = "Dungeon: Nightmare",
                        MarkerColorHex = "#9236f5",
                        BuildingGradeSelection = 10,
                        ContainerColor = 0,
                        DungeonRooms = 5,
                        DungeonRoomSize = 5,
                        BuyCost = 1500,
                        EntitySpawnLimits = new EntitySpawnLimits
                        {
                            NpcsCounts = 31,
                            BossesCounts = 10,
                            AnimalsCounts = 8,
                            TurretsCounts = 25,
                            LootBoxesCounts = 30,
                        },
                        AutoTurretConfig = new TurretConfig { Health = 1000, WeaponShortName = "rifle.ak" },
                        LootBoxSkinID = 2998755525,
                        MaxItemsPerBox = 7,
                        MaxItemsPerNpc = 7,
                        MaxItemsPerBossNpc = 7,
                        NpcConfig = new NpcConfig
                        {
                            Name = "Nightmare Hunter",
                            Health = 300,
                            DamageScale = 1.5f,
                            WearItems = new WearConfig
                            {
                                Items = new List<WearItemConfig>
                                {
                                    new WearItemConfig { ShortName = "metal.facemask", SkinID = 3343860599 },
                                    new WearItemConfig { ShortName = "metal.plate.torso", SkinID = 3343861569 },
                                    new WearItemConfig { ShortName = "hoodie", SkinID = 1369835131 },
                                    new WearItemConfig { ShortName = "roadsign.kilt", SkinID = 1727564168 },
                                    new WearItemConfig { ShortName = "burlap.gloves", SkinID = 1210780157 },
                                    new WearItemConfig { ShortName = "pants", SkinID = 810745264 },
                                    new WearItemConfig { ShortName = "shoes.boots", SkinID = 1210771348 },
                                },
                            },
                            BeltItems = new BeltConfig
                            {
                                Items = new List<BeltItemConfig>
                                {
                                    new BeltItemConfig
                                    {
                                        ShortName = "lmg.m249",
                                        Amount = 1,
                                        SkinID = 1883947256,
                                        Mods = new List<string> { "weapon.mod.flashlight" },
                                    },
                                    new BeltItemConfig
                                    {
                                        ShortName = "syringe.medical",
                                        Amount = 50,
                                        SkinID = 0,
                                    },
                                },
                            },
                        },
                        BossNpcConfig = new NpcConfig
                        {
                            Name = "Nightmare Dungeon Boss",
                            Health = 5000,
                            DamageScale = 1.5f,
                            WearItems = new WearConfig
                            {
                                Items = new List<WearItemConfig>
                                {
                                    new WearItemConfig { ShortName = "riot.helmet", SkinID = 1087995729 },
                                    new WearItemConfig { ShortName = "hoodie", SkinID = 1087990973 },
                                    new WearItemConfig { ShortName = "burlap.gloves", SkinID = 1087998101 },
                                    new WearItemConfig { ShortName = "pants", SkinID = 1087992342 },
                                    new WearItemConfig { ShortName = "shoes.boots", SkinID = 1088000573 },
                                },
                            },
                            BeltItems = new BeltConfig
                            {
                                Items = new List<BeltItemConfig>
                                {
                                    new BeltItemConfig
                                    {
                                        ShortName = "lmg.m249",
                                        Amount = 1,
                                        SkinID = 1883947256,
                                        Mods = new List<string> { "weapon.mod.flashlight" },
                                    },
                                },
                            },
                        },
                    },
                    new DungeonTierConfig
                    {
                        TierName = "Impossible",
                        MapName = "Dungeon: Impossible",
                        MarkerColorHex = "#1a1412",
                        BuildingGradeSelection = 10,
                        ContainerColor = 4,
                        DungeonRooms = 6,
                        DungeonRoomSize = 6,
                        BuyCost = 2000,
                        EntitySpawnLimits = new EntitySpawnLimits
                        {
                            NpcsCounts = 5,
                            BossesCounts = 30,
                            AnimalsCounts = 12,
                            TurretsCounts = 20,
                            LootBoxesCounts = 25,
                        },
                        AutoTurretConfig = new TurretConfig { Health = 1500, WeaponShortName = "rifle.ak" },
                        LootBoxSkinID = 2998755525,
                        MaxItemsPerBox = 8,
                        MaxItemsPerNpc = 8,
                        MaxItemsPerBossNpc = 8,
                        NpcConfig = new NpcConfig
                        {
                            Name = "Impossible Hunter",
                            Health = 300,
                            DamageScale = 1.5f,
                            WearItems = new WearConfig
                            {
                                Items = new List<WearItemConfig>
                                {
                                    new WearItemConfig { ShortName = "metal.facemask", SkinID = 3343860599 },
                                    new WearItemConfig { ShortName = "metal.plate.torso", SkinID = 3343861569 },
                                    new WearItemConfig { ShortName = "hoodie", SkinID = 1369835131 },
                                    new WearItemConfig { ShortName = "roadsign.kilt", SkinID = 1727564168 },
                                    new WearItemConfig { ShortName = "burlap.gloves", SkinID = 1210780157 },
                                    new WearItemConfig { ShortName = "pants", SkinID = 810745264 },
                                    new WearItemConfig { ShortName = "shoes.boots", SkinID = 1210771348 },
                                },
                            },
                            BeltItems = new BeltConfig
                            {
                                Items = new List<BeltItemConfig>
                                {
                                    new BeltItemConfig
                                    {
                                        ShortName = "lmg.m249",
                                        Amount = 1,
                                        SkinID = 1883947256,
                                        Mods = new List<string> { "weapon.mod.flashlight" },
                                    },
                                    new BeltItemConfig
                                    {
                                        ShortName = "syringe.medical",
                                        Amount = 50,
                                        SkinID = 0,
                                    },
                                },
                            },
                        },
                        BossNpcConfig = new NpcConfig
                        {
                            Name = "Impossible Dungeon Boss",
                            Health = 5000,
                            DamageScale = 1.5f,
                            WearItems = new WearConfig
                            {
                                Items = new List<WearItemConfig>
                                {
                                    new WearItemConfig { ShortName = "riot.helmet", SkinID = 871867116 },
                                    new WearItemConfig { ShortName = "hoodie", SkinID = 3308083017 },
                                    new WearItemConfig { ShortName = "pants", SkinID = 3269772521 },
                                    new WearItemConfig { ShortName = "shoes.boots", SkinID = 3332536449 },
                                },
                            },
                            BeltItems = new BeltConfig
                            {
                                Items = new List<BeltItemConfig>
                                {
                                    new BeltItemConfig
                                    {
                                        ShortName = "lmg.m249",
                                        Amount = 1,
                                        SkinID = 1883947256,
                                        Mods = new List<string> { "weapon.mod.flashlight" },
                                    },
                                },
                            },
                        },
                    },
                };

            [JsonProperty("Chat Commands")]
            public ChatCommandConfig ChatCommands { get; set; } = new ChatCommandConfig();

            [JsonProperty("Console Commands")]
            public ConsoleCommandConfig ConsoleCommands { get; set; } = new ConsoleCommandConfig();

            [JsonProperty("Version")]
            public VersionNumber Version { get; set; }
        }

        public class ChatCommandConfig
        {
            [JsonProperty("Buy Dungeon")]
            public string BuyDungeon { get; set; } = "buydungeon";

            [JsonProperty("Create Dungeon")]
            public string CreateDungeon { get; set; } = "createdungeon";

            [JsonProperty("Reload Config")]
            public string ReloadConfig { get; set; } = "de.reloadconfig";

            [JsonProperty("Toggle Purchases")]
            public string TogglePurchases { get; set; } = "de.toggle";

            [JsonProperty("Remove Inactive Dungeons")]
            public string RemoveInactiveDungeons { get; set; } = "removeinactivedungeons";

            [JsonProperty("Remove All Dungeons")]
            public string RemoveAllDungeons { get; set; } = "removealldungeons";

            [JsonProperty("Remove Nearest Dungeon")]
            public string RemoveNearest { get; set; } = "de.removenearest";

            [JsonProperty("Remove Own Dungeon")]
            public string RemoveOwnDungeon { get; set; } = "removedungeon";

            [JsonProperty("Force Remove All Dungeons")]
            public string ForceRemoveAll { get; set; } = "forceremovealldungeons";

            [JsonProperty("Add Auto Portal Spawn Location")]
            public string AddAutoPortalSpawnLocation { get; set; } = "de.addautospawn";

            [JsonProperty("Add Buy Portal Spawn Location")]
            public string AddBuyPortalSpawnLocation { get; set; } = "de.addbuyspawn";

            [JsonProperty("Remove Nearest Custom Portal Spawn Location")]
            public string RemoveNearestCustomPortalSpawnLocation { get; set; } = "de.removespawn";

            [JsonProperty("List Custom Portal Spawn Locations")]
            public string ListCustomPortalSpawnLocations { get; set; } = "de.listspawns";

            [JsonProperty("List Active Dungeons")]
            public string ListActiveDungeons { get; set; } = "dungeons";
        }

        public class ConsoleCommandConfig
        {
            [JsonProperty("Buy Dungeon")]
            public string BuyDungeon { get; set; } = "buydungeon";

            [JsonProperty("Spawn Random Dungeon")]
            public string SpawnRandomDungeon { get; set; } = "spawnrandomdungeon";

            [JsonProperty("Spawn Fixed Dungeon")]
            public string SpawnFixedDungeon { get; set; } = "spawnfixeddungeon";

            [JsonProperty("Reload Config")]
            public string ReloadConfig { get; set; } = "de.reloadconfig";

            [JsonProperty("List Active Dungeons")]
            public string ListActiveDungeons { get; set; } = "dungeons";
        }

        public class RewardConfig
        {
            private int? _bossEconomicsReward;
            private int? _animalEconomicsReward;
            private int? _bossServerRewards;
            private int? _animalServerRewards;

            [JsonProperty("NPC Economics Reward")]
            public int NpcEconomicsReward { get; set; } = 300;

            [JsonProperty("Boss Economics Reward")]
            public int BossEconomicsReward
            {
                get => _bossEconomicsReward ?? NpcEconomicsReward;
                set => _bossEconomicsReward = value;
            }

            [JsonProperty("Animal Economics Reward")]
            public int AnimalEconomicsReward
            {
                get => _animalEconomicsReward ?? NpcEconomicsReward;
                set => _animalEconomicsReward = value;
            }

            [JsonProperty("Turret Economics Reward")]
            public int TurretEconomicsReward { get; set; } = 200;

            [JsonProperty("Box Economics Reward")]
            public int BoxEconomicsReward { get; set; } = 100;

            [JsonProperty("NPC ServerRewards Points")]
            public int NpcServerRewards { get; set; } = 300;

            [JsonProperty("Boss ServerRewards Points")]
            public int BossServerRewards
            {
                get => _bossServerRewards ?? NpcServerRewards;
                set => _bossServerRewards = value;
            }

            [JsonProperty("Animal ServerRewards Points")]
            public int AnimalServerRewards
            {
                get => _animalServerRewards ?? NpcServerRewards;
                set => _animalServerRewards = value;
            }

            [JsonProperty("Turret ServerRewards Points")]
            public int TurretServerRewards { get; set; } = 200;

            [JsonProperty("Box ServerRewards Points")]
            public int BoxServerRewards { get; set; } = 100;

            [JsonProperty("Dungeon Clear Economics Reward")]
            public int DungeonClearEconomicsReward { get; set; } = 500;

            [JsonProperty("Dungeon Clear ServerRewards Points")]
            public int DungeonClearServerRewards { get; set; } = 500;

            [JsonProperty("Dungeon Clear Skill Tree XP Reward")]
            public double DungeonClearXpReward { get; set; } = 500.0;
        }

        public class DungeonTierConfig
        {
            [JsonProperty("Tier Name")]
            public string TierName { get; set; } = "Tier";

            [JsonProperty("Map Name")]
            public string MapName { get; set; }

            [JsonProperty("Marker Color Hex")]
            public string MarkerColorHex { get; set; } = "#FF8800";

            [JsonProperty("Enable Auto Spawn")]
            public bool EnableAutoSpawn { get; set; } = true;

            [JsonProperty("Enable Buy")]
            public bool EnableBuy { get; set; } = true;

            [JsonProperty("Require Permission")]
            public bool RequirePermission { get; set; } = false;

            [JsonProperty("Minutes After Wipe To Activate")]
            public int MinutesAfterWipeToActivate { get; set; } = 0;

            [JsonProperty("Max Auto Spawn (0 to Unlimited)")]
            public int MaxActiveAutoSpawn { get; set; } = 2;

            [JsonProperty("Max Buy (0 to Unlimited)")]
            public int MaxActiveBuy { get; set; } = 2;

            [JsonProperty(ConstructionTypeConfigKey)]
            public int BuildingGradeSelection { get; set; } = 9;

            [JsonProperty(
                "Container Color - 1(Light Blue) 2(Light Green) 3(Purple) 4(Dark Red) 5(Orange) 6(White) 7(Black) 8(Brown) 9(Dark Blue) 10(Dark Green) 11(Red) 12(Light Orange) 13(Yellow) 14(Dark Gray) 15(Cyan) 16(Light Gray)"
            )]
            public int ContainerColor { get; set; } = 2;

            [JsonProperty("Garage Door Skin ID")]
            public ulong DoorSkinID { get; set; } = 2427605919;

            [JsonProperty("Dungeon Time of Day Override(-1 for no override)")]
            public int TriggerTime { get; set; } = 0;

            [JsonProperty("Dungeon Rooms (1 - 6)")]
            public int DungeonRooms { get; set; } = 2;

            [JsonProperty("Dungeon Room Size (5 - 10)")]
            public int DungeonRoomSize { get; set; } = 5;

            [JsonProperty("Buy Cost")]
            public int BuyCost { get; set; }

            [JsonProperty("Per-Tier Buy Cooldown (seconds, 0 = use global)")]
            public int BuyCooldown { get; set; } = 0;

            [JsonProperty("Max Unentered Lifetime (in seconds)")]
            public int MaxUnenteredLifetimeSeconds { get; set; } = 1800;

            [JsonProperty("Max Time (in seconds) to Keep Dungeon Alive")]
            public int MaxDungeonLifetime { get; set; } = 1800;

            [JsonProperty("Entity Spawn Limits")]
            public EntitySpawnLimits EntitySpawnLimits { get; set; } = new EntitySpawnLimits();

            [JsonProperty("NPC Config")]
            public NpcConfig NpcConfig { get; set; } = new NpcConfig();

            [JsonProperty("Boss NPC Config")]
            public NpcConfig BossNpcConfig { get; set; } = new NpcConfig();

            [JsonProperty("Animal Config")]
            public DungeonAnimalConfig AnimalConfig { get; set; } = new DungeonAnimalConfig();

            [JsonProperty("Loot Box Prefab Path")]
            public string LootBoxPrefabPath { get; set; } = "assets/prefabs/deployable/woodenbox/woodbox_deployed.prefab";

            [JsonProperty("Loot Box Skin ID")]
            public ulong LootBoxSkinID { get; set; } = 0UL;

            [JsonProperty("Max Items Per Box")]
            public int MaxItemsPerBox { get; set; } = 3;

            [JsonProperty("Max Items Per NPC")]
            public int MaxItemsPerNpc { get; set; } = 3;

            [JsonProperty("Max Items Per Boss NPC")]
            public int MaxItemsPerBossNpc { get; set; } = 6;

            [JsonProperty("Auto Turret Config")]
            public TurretConfig AutoTurretConfig { get; set; } = new TurretConfig();

            [JsonProperty("Reward Config")]
            public RewardConfig RewardConfig { get; set; } = new RewardConfig();

            [JsonIgnore]
            public string LootBoxDataFile { get; set; } = string.Empty;

            [JsonIgnore]
            public List<ItemConfig> LootBoxItems { get; set; } = new List<ItemConfig>();
        }

        public class EntitySpawnLimits
        {
            [JsonProperty("NPC Count")]
            public int NpcsCounts { get; set; }

            [JsonProperty("Boss Count")]
            public int BossesCounts { get; set; }

            [JsonProperty("Animal Count")]
            public int AnimalsCounts { get; set; } = 2;

            [JsonProperty("Turret Count")]
            public int TurretsCounts { get; set; }

            [JsonProperty("Loot Box Count")]
            public int LootBoxesCounts { get; set; }
        }

        public class TurretConfig
        {
            [JsonProperty("Health")]
            public float Health { get; set; }

            [JsonProperty("Weapon Short Name")]
            public string WeaponShortName { get; set; }
        }

        public class NpcConfig
        {
            [JsonProperty("NPC Name")]
            public string Name { get; set; } = "Default NPC";

            [JsonProperty("NPC Health")]
            public int Health { get; set; } = 100;

            [JsonProperty("Damage Scale")]
            public float DamageScale { get; set; } = 1.0f;

            [JsonProperty("Wear Items")]
            public WearConfig WearItems { get; set; } = new WearConfig();

            [JsonProperty("Belt Items")]
            public BeltConfig BeltItems { get; set; } = new BeltConfig();

            [JsonProperty("Disable Radio")]
            public bool DisableRadio { get; set; } = false;

            [JsonProperty("Damage Taken From Explosion Multiplier")]
            public float DamageExplosionTakenMultiplier { get; set; } = 1.0f;

            [JsonIgnore]
            public string LootDropDataFile { get; set; } = string.Empty;

            [JsonIgnore]
            public LootDropConfig LootDropConfig { get; set; } = new LootDropConfig();
        }

        public class DungeonAnimalConfig
        {
            [JsonProperty("Enabled")]
            public bool Enabled { get; set; } = true;

            [JsonProperty("Damage")]
            public float Damage { get; set; } = 12f;

            [JsonProperty("Move Speed")]
            public float MoveSpeed { get; set; } = 4.0f;

            [JsonProperty("Sense Range")]
            public float SenseRange { get; set; } = 30f;

            [JsonProperty("Max Distance From Spawn Point")]
            public float MaxDistanceFromSpawnPoint { get; set; } = 30f;

            [JsonProperty("Animals", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<AnimalSpeciesConfig> Animals { get; set; } = CreateDefaultAnimalSpecies();
        }

        public class AnimalSpeciesConfig
        {
            [JsonProperty("Animal")]
            public string Animal { get; set; }

            [JsonProperty("Enabled")]
            public bool Enabled { get; set; } = true;

            [JsonProperty("Spawn Weight")]
            public float SpawnChancePercentage { get; set; } = 100f;

            [JsonProperty("HP")]
            public float Health { get; set; } = 300f;
        }

        public class WearConfig
        {
            [JsonProperty("Items", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<WearItemConfig> Items { get; set; } = new List<WearItemConfig>();
        }

        public class BeltConfig
        {
            [JsonProperty("Items", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<BeltItemConfig> Items { get; set; } = new List<BeltItemConfig>();
        }

        public class WearItemConfig
        {
            [JsonProperty("ShortName")]
            public string ShortName { get; set; }

            [JsonProperty("SkinID")]
            public ulong SkinID { get; set; }
        }

        public class BeltItemConfig
        {
            [JsonProperty("ShortName")]
            public string ShortName { get; set; }

            [JsonProperty("Amount")]
            public int Amount { get; set; }

            [JsonProperty("SkinID")]
            public ulong SkinID { get; set; }

            [JsonProperty("Mods")]
            public List<string> Mods { get; set; } = new List<string>();
        }

        public class LootDropConfig
        {
            [JsonProperty("Items", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ItemConfig> Items { get; set; } = new List<ItemConfig>();
        }

        public class ItemConfig
        {
            [JsonProperty("ShortName")]
            public string ShortName { get; set; }

            [JsonProperty("Inclusion Chance Percentage")]
            public float InclusionChancePercentage { get; set; }

            [JsonProperty("Minimum Amount")]
            public int MinimumAmount { get; set; }

            [JsonProperty("Maximum Amount")]
            public int MaximumAmount { get; set; }

            [JsonProperty("CustomName")]
            public string CustomName { get; set; } = string.Empty;

            [JsonProperty(LootItemTextFieldKey)]
            public string Text { get; set; } = string.Empty;

            [JsonProperty("Armor Insert Slots", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int ArmorSlots { get; set; }

            [JsonProperty("SkinID")]
            public ulong SkinID { get; set; }
        }

        public class SpawnPointConfiguration
        {
            [JsonProperty("Avoid Terrain Topology")]
            public bool AvoidTerrainTopology { get; set; } = true;

            [JsonProperty("Enable Physics Overlap Check")]
            public bool CheckSphereRadius { get; set; } = true;

            [JsonProperty("Enable Entity Proximity Check")]
            public bool CheckEntities { get; set; } = true;

            [JsonProperty("Avoid ZoneManager Zones")]
            public bool CheckZoneManager { get; set; } = true;

            [JsonProperty("Avoid Duelist Territory")]
            public bool CheckDuelist { get; set; } = true;

            [JsonProperty("Avoid Raidable Bases Territory")]
            public bool CheckRaidableBases { get; set; } = true;

            [JsonProperty("Avoid Abandoned Bases Territory")]
            public bool CheckAbandonedBases { get; set; } = true;
        }

        public class CustomPortalSpawnConfiguration
        {
            [JsonProperty("Enable Custom Portal Spawn Locations")]
            public bool Enabled { get; set; } = true;

            [JsonProperty("Fallback To Random Auto Spawn Location If No Custom Location Is Available")]
            public bool FallbackToRandomAutoSpawn { get; set; } = false;

            [JsonProperty("Fallback To Random Buy Location If No Custom Location Is Available")]
            public bool FallbackToRandomBuy { get; set; } = false;

            [JsonProperty("Auto Spawn Locations", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<StoredPortalPosition> AutoSpawnLocations { get; set; } = new List<StoredPortalPosition>();

            [JsonProperty("Buy Locations", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<StoredPortalPosition> BuyLocations { get; set; } = new List<StoredPortalPosition>();
        }

        public class DungeonPerformanceSettings
        {
            [JsonProperty("Building - Room Cells Per Batch")]
            public int BuildCellsPerBatch { get; set; } = 8;

            [JsonProperty("Building - Door Frame Cells Per Batch")]
            public int BuildFrameCellsPerBatch { get; set; } = 8;

            [JsonProperty("Building - Wall Foundations Per Batch")]
            public int BuildWallFoundationsPerBatch { get; set; } = 12;

            [JsonProperty("Building - Delay Between Batches (seconds)")]
            public float BuildBatchDelaySeconds { get; set; } = 0.03f;

            [JsonProperty("NPCs, Animals, Turrets And Loot - Spawns Per Batch")]
            public int EntitySpawnsPerBatch { get; set; } = 5;

            [JsonProperty("NPCs, Animals, Turrets And Loot - Delay Between Batches (seconds)")]
            public float EntitySpawnBatchDelaySeconds { get; set; } = 0.10f;

            [JsonProperty("Cleanup - Entities Removed Per Batch")]
            public int CleanupEntitiesPerBatch { get; set; } = 25;

            [JsonProperty("Cleanup - Delay Between Batches (seconds)")]
            public float CleanupBatchDelaySeconds { get; set; } = 0.05f;

            [JsonProperty("Auto Spawn - Delay After Cleanup Finishes (seconds)")]
            public float AutoSpawnDelayAfterCleanupSeconds { get; set; } = 15f;

            [JsonProperty("Location Search - Attempts Per Batch")]
            public int SearchAttemptsPerBatch { get; set; } = 250;

            [JsonProperty("Location Search - Delay Between Batches (seconds)")]
            public float SearchBatchDelaySeconds { get; set; } = 0.02f;
        }

        public class DungeonSpawnSettings
        {
            [JsonProperty("Enable Auto Spawn")]
            public bool EnableAutoSpawn { get; set; } = false;

            [JsonProperty("Map Marker Radius")]
            public float MapMarkerRadius { get; set; } = 0.2f;

            [JsonProperty("Create Map Markers for Purchased Dungeons")]
            public bool EnableMapMarkersBuy { get; set; } = true;

            [JsonProperty("Create Map Markers for Auto-Spawned Dungeons")]
            public bool EnableMapMarkersAutoSpawn { get; set; } = true;

            [JsonProperty("Max Active Dungeons Auto Spawn (It is not recommended to exceed 10)")]
            public int MaxTotalActiveDungeonsAutoSpawn { get; set; } = 6;

            [JsonProperty("Max Active Dungeons Buy (It is not recommended to exceed 10")]
            public int MaxTotalActiveDungeonsBuy { get; set; } = 6;

            [JsonProperty("Dungeon Buy Cooldown (in seconds)")]
            public int BuyCooldown { get; set; } = 600;

            [JsonProperty("Auto Spawn Delay (in seconds)")]
            public int AutoSpawnDelay { get; set; } = 120;

            [JsonProperty("Dungeon Height (0 = terrain, negative = underground, max 700)")]
            public float DungeonHeight { get; set; } = 700f;

            [JsonProperty("Dungeon Removal")]
            public DungeonRemovalSettings DungeonRemoval { get; set; } = new DungeonRemovalSettings();

            [JsonProperty("Show Dungeon Spawn Announcement")]
            public bool ShowDungeonSpawnAnnouncement { get; set; } = true;

            [JsonProperty("Show Purchase Announcement To Owner Only")]
            public bool ShowPurchaseAnnouncementToOwnerOnly { get; set; } = false;
        }

        public class DungeonRemovalSettings
        {
            [JsonProperty("Remove only if all NPCs are dead")]
            public bool RemoveIfAllNpcsDead { get; set; } = true;

            [JsonProperty("Remove only if all boxes are destroyed")]
            public bool RemoveIfAllBoxesDestroyed { get; set; } = true;

            [JsonProperty("Remove only if all turrets are destroyed")]
            public bool RemoveIfAllTurretsDestroyed { get; set; } = true;

            [JsonProperty("Removal timer after they are destroyed (in seconds)")]
            public int RemovalAfterDestroyedTimer { get; set; } = 300;
        }

        public class LootBoxConfig
        {
            [JsonProperty(PropertyName = "Loot Items", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ItemConfig> LootItems { get; set; } =
                new List<ItemConfig>
                {
                    new()
                    {
                        ShortName = "ammo.rifle",
                        InclusionChancePercentage = 15,
                        MinimumAmount = 100,
                        MaximumAmount = 300,
                    },
                    new()
                    {
                        ShortName = "sulfur.ore",
                        InclusionChancePercentage = 15,
                        MinimumAmount = 100,
                        MaximumAmount = 300,
                    },
                };
        }
        #endregion

        #region Models Configs
        public class DungeonRequest
        {
            public BasePlayer Player { get; set; }
            public DungeonTierConfig Tier { get; set; }
            public bool LockToPlayer { get; set; }
            public DungeonOrigin Origin { get; set; }
            public Vector3 PortalPosition { get; set; }
            public Vector3 DungeonPosition { get; set; }
            public bool UsesCustomPortalPosition { get; set; }
        }

        public class StoredPortalPosition
        {
            [JsonProperty("X")]
            public float X { get; set; }

            [JsonProperty("Y")]
            public float Y { get; set; }

            [JsonProperty("Z")]
            public float Z { get; set; }

            public Vector3 ToVector3() => new Vector3(X, Y, Z);

            public static StoredPortalPosition FromVector3(Vector3 position)
            {
                return new StoredPortalPosition
                {
                    X = position.x,
                    Y = position.y,
                    Z = position.z,
                };
            }
        }

        private class IconInfo
        {
            public string Sprite { get; set; }
            public string Color { get; set; }
            public string Text { get; set; }
            public float X { get; set; }
            public float Y { get; set; }
            public float Width { get; set; }
            public float Height { get; set; }
        }

        private class ResourceItem
        {
            public string Shortname { get; set; }
            public int Amount { get; set; }
            public ulong SkinID { get; set; }
        }

        private class PlayerLootRecovery
        {
            public Vector3 ExitPosition { get; set; }
            public DateTime ExpiresAtUtc { get; set; }
            public string DungeonUniqueId { get; set; }
            public bool RelocateOnSpawn { get; set; }
        }

        public class ActiveDungeon
        {
            public readonly HashSet<ulong> IndexedEntityIds = new HashSet<ulong>();
            public readonly Dictionary<Vector3Int, List<BuildingBlock>> BuildingBlocksByPosition = new Dictionary<Vector3Int, List<BuildingBlock>>();
            public bool HasUniformFloorHeight = true;
            public float UniformFloorHeight;
            public int UiCountsFrame = -1;
            public int UiNpcCount;
            public int UiBoxCount;
            public int UiTurretCount;
            public DateTime? ActivationTime { get; set; } = null;
            public bool HasBeenCleared { get; set; } = false;
            public DateTime? DestructionCountdownStart { get; set; } = null;
            public float DestructionDelay { get; set; } = 0;
            public string UniqueId { get; set; }
            public bool[] LastGeneratedGrid = null;
            public List<bool[]> DungeonGrids = new List<bool[]>();
            public List<Vector3> TurretPositions = new List<Vector3>();
            public List<Vector3> SpawnPoints { get; set; }
            public BasePortal BasePortal { get; set; }
            public BasePortal InsidePortal { get; set; }
            public ulong OwnerID { get; set; }
            public string OwnerName { get; set; }
            public Vector3 PortalPosition { get; set; }
            public Vector3 DungeonPosition { get; set; }
            public string Grid { get; set; }
            public string TierName { get; set; }
            public DungeonTierConfig TierConfig { get; set; }
            public HashSet<BaseEntity> Entities { get; set; }
            public HashSet<BaseEntity> Npcs { get; set; }
            public bool Spawned { get; set; }
            public DateTime CreationTime { get; set; }
            public DungeonOrigin Origin { get; set; }
            public DateTime? AllDestroyedTime { get; set; }
            public bool MarkedForRemoval { get; set; }
            public int LastReminderMinute { get; set; } = -1;
            public Bounds DungeonBounds { get; set; }
            public DungeonEventsTrigger Trigger { get; set; }
            public int DungeonRooms { get; set; } = 2;
            public int DungeonRoomSize { get; set; } = 2;
            public HashSet<string> OccupiedPositions = new HashSet<string>();
            public HashSet<Vector3> DungeonFloorCenters = new HashSet<Vector3>();
            public HashSet<ulong> PlayersInside = new HashSet<ulong>();
            public readonly List<Door> Doors = new List<Door>();
            public readonly List<(Vector3 Position, float Yaw)> ClassicDoorFrames = new List<(Vector3 Position, float Yaw)>();
            public List<Door> EntranceDoors = new List<Door>();
            public readonly List<Bounds> EntranceCorridorSegments = new List<Bounds>();
            public Bounds EntranceSafeZoneBounds;
            public bool HasEntranceSafeZone;
            public bool EntranceLayoutFailed;
            public float LastEnemyResetAt = -99999f;
            public bool ClearRewardGiven { get; set; } = false;
            public List<Vector3> GridOrigins = new List<Vector3>();
            public List<bool[]> CellGrids = new List<bool[]>();
            public readonly List<List<Vector3>> RockRoomSpawnPoints = new List<List<Vector3>>();
            public readonly Dictionary<Vector3Int, List<Collider>> RockFloorColliders = new Dictionary<Vector3Int, List<Collider>>();
            public readonly List<Vector3> RockTorchPositions = new List<Vector3>();
            public List<Vector3> Foundations = new List<Vector3>();
            public HashSet<string> FoundationKeys = new HashSet<string>();
            public HashSet<Vector3> RoomFloorCenters = new HashSet<Vector3>();
        }

        private class DungeonMarker
        {
            public VendingMachineMapMarker VendingMachineMapMarker { get; set; }
            public MapMarkerGenericRadius MapMarkerGenericRadius { get; set; }
            public Vector3 Position { get; set; }
            public string Tier { get; set; }
        }

        private class EventUiState
        {
            public string DungeonId;
            public bool Cleared;
            public string Counter;
            public int Npcs;
            public int Boxes;
            public int Turrets;
        }

        private class LootFileCache
        {
            public DateTime LastWriteTimeUtc;
            public long Length;
            public List<ItemConfig> Items;
        }

        private class DungeonEntitySpawns
        {
            public readonly List<Vector3> Turrets = new List<Vector3>();
            public readonly List<Vector3> Boxes = new List<Vector3>();
            public readonly List<Vector3> Npcs = new List<Vector3>();
            public readonly List<Vector3> Bosses = new List<Vector3>();
            public readonly List<Vector3> Animals = new List<Vector3>();
        }

        private class TierInfo
        {
            public string Name { get; set; }
            public string MapName { get; set; }
            public int BuyCost { get; set; }
            public Color MarkerColor { get; set; }
            public string ColorString { get; set; }
            public bool Enabled { get; set; }
        }

        public enum DungeonOrigin
        {
            AutoSpawn,
            Buy,
        }
        #endregion

        #region Chat Commands
        private void BuyDungeonChatCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;
            if (!permission.UserHasPermission(player.UserIDString, PermissionBuy) && !IsAdmin(player) && !permission.UserHasPermission(player.UserIDString, PermissionAdmin))
            {
                NotifyPlayer(player, "NoAccess", 0);
                return;
            }
            if (args != null && args.Length > 0)
            {
                ProcessBuyDungeon(player, args[0]);
                return;
            }
            if (!_config.EnablePurchases)
            {
                NotifyPlayer(player, "PurchasesDisabled", 0);
                return;
            }
            OpenBuyDungeonGUI(player);
        }

        private void CreateDungeonChatCommand(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player))
                return;
            if (args.Length == 0)
            {
                NotifyPlayer(player, "UsageCreateMessage", 0);
                return;
            }
            var tierName = args[0];
            var tier = FindTierByName(tierName);
            if (tier == null)
            {
                NotifyPlayer(player, "InvalidTierMessage", 0, tierName, GetAvailableTiers());
                return;
            }
            if (!tier.EnableBuy || !IsTierUnlockedByWipeTime(tier))
            {
                NotifyPlayer(player, "ThisTierDisabled", 0);
                return;
            }
            LogDebug("Received dungeon creation request.", 1);
            EnqueueDungeonRequest(player, tier, false);
        }

        private void ReloadPluginConfiguration(BasePlayer player = null, ConsoleSystem.Arg arg = null)
        {
            LoadConfig();
            RegisterTierPermissions();
            UpdatePerformanceDelays();
            EnsureUndergroundTerrainProtectionDisabled();
            RegisterCommands();
            _dungeonTimer?.Destroy();
            _dungeonTimer = timer.Every(DungeonTimerInterval, HandleDungeonTimers);
            UpdateMarkers();
            _dungeonQueue.Clear();
            if (player != null)
                NotifyPlayer(player, "ConfigReloaded", 0);
            else
                arg?.ReplyWith("Configuration reloaded.");
        }

        private void ReloadConfigChatCommand(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player))
                return;
            ReloadPluginConfiguration(player);
        }

        private void TogglePurchasesChatCommand(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player))
                return;
            _config.EnablePurchases = !_config.EnablePurchases;
            if (_config.EnablePurchases)
                NotifyPlayer(player, "PurchasesEnabled", 0);
            else
                NotifyPlayer(player, "PurchasesDisabled", 0);
        }

        private void RemoveInactiveDungeonsChatCommand(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player))
                return;
            var removedDungeons = RemoveInactiveDungeons();
            NotifyPlayerOnRemoval(player, removedDungeons);
        }

        private void RemoveAllDungeonsChatCommand(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player))
                return;
            var removed = RemoveAllDungeons();
            NotifyPlayerOnRemoval(player, removed);
        }

        private void RemoveNearestDungeonChatCommand(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player))
                return;
            if (_activeDungeons.Count == 0)
            {
                NotifyPlayer(player, "NoDungeonsToRemove", 0);
                return;
            }
            ActiveDungeon nearest = null;
            float nearestDistance = float.MaxValue;
            Vector3 playerPosition = player.transform.position;
            foreach (var dungeon in _activeDungeons)
            {
                float portalDistance = Vector3.Distance(playerPosition, dungeon.PortalPosition);
                float dungeonDistance = Vector3.Distance(playerPosition, dungeon.DungeonPosition);
                float distance = Mathf.Min(portalDistance, dungeonDistance);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = dungeon;
                }
            }
            if (nearest == null || nearestDistance > 10f)
            {
                NotifyPlayer(player, "TooFarToRemove", 0);
                return;
            }
            DestroyDungeon(nearest);
            NotifyPlayer(player, "DungeonRemovedDetail", 0, nearest.TierName, nearest.Grid);
        }

        private void RemoveOwnDungeonChatCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;
            var owned = _activeDungeons.FirstOrDefault(d => d.OwnerID == player.userID);
            if (owned == null)
            {
                NotifyPlayer(player, "NotDungeonOwner", 0);
                return;
            }
            DestroyDungeon(owned);
            NotifyPlayer(player, "DungeonRemovedDetail", 0, owned.TierName, owned.Grid);
        }

        private void ForceRemoveAllDungeonsChatCommand(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player))
                return;
            CleanupExistingEntities();
            NotifyPlayer(player, "CleanedUp", 0);
        }

        private void AddAutoPortalSpawnLocationChatCommand(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player))
                return;
            AddCustomPortalSpawnLocation(player, DungeonOrigin.AutoSpawn);
        }

        private void AddBuyPortalSpawnLocationChatCommand(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player))
                return;
            AddCustomPortalSpawnLocation(player, DungeonOrigin.Buy);
        }

        private void RemoveNearestCustomPortalSpawnLocationChatCommand(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player))
                return;
            if (args == null || args.Length == 0)
            {
                player.ChatMessage($"Usage: /{command} <auto|buy> [radius]");
                return;
            }
            DungeonOrigin origin;
            if (args[0].Equals("auto", StringComparison.OrdinalIgnoreCase))
                origin = DungeonOrigin.AutoSpawn;
            else if (args[0].Equals("buy", StringComparison.OrdinalIgnoreCase) || args[0].Equals("compra", StringComparison.OrdinalIgnoreCase))
                origin = DungeonOrigin.Buy;
            else
            {
                player.ChatMessage($"Usage: /{command} <auto|buy> [radius]");
                return;
            }
            float radius = 5f;
            if (args.Length >= 2 && float.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsedRadius))
                radius = Mathf.Clamp(parsedRadius, 0.5f, 100f);
            RemoveNearestCustomPortalSpawnLocation(player, origin, radius);
        }

        private void ListCustomPortalSpawnLocationsChatCommand(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player))
                return;
            ShowCustomPortalSpawnLocations(player);
        }

        private void ListActiveDungeonsChatCommand(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player))
                return;
            SendActiveDungeonList(message => SendReply(player, message));
        }
        #endregion

        #region Console Commands
        private void BuyDungeonCommand(ConsoleSystem.Arg arg)
        {
            if (arg == null || !arg.HasArgs(2))
            {
                LogDebug("Usage: buydungeon <tierName> <playerID>", 1);
                return;
            }
            string tierName = arg.GetString(0);
            string playerId = arg.GetString(1);
            if (string.IsNullOrEmpty(tierName) || string.IsNullOrEmpty(playerId))
                return;
            BasePlayer player = BasePlayer.FindAwakeOrSleeping(playerId);
            if (player == null || !player.userID.IsSteamId())
            {
                LogDebug($"Player with Steam ID '{playerId}' not found.", 1);
                return;
            }
            BasePlayer caller = arg.Player();
            if (caller != null && caller.userID != player.userID && !IsAdmin(caller) && !permission.UserHasPermission(caller.UserIDString, PermissionAdmin))
            {
                arg.ReplyWith("You cannot purchase a dungeon for another player.");
                return;
            }
            ProcessBuyDungeon(player, tierName);
        }

        private void SpawnRandomDungeonCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null && !IsAdmin(arg.Player()))
            {
                arg.ReplyWith("You do not have permission to run this command.");
                return;
            }
            var tier = GetDifficultyWithLeastActiveDungeons();
            if (tier == null)
            {
                arg.ReplyWith("No Auto Spawn tier is currently available.");
                return;
            }
            EnqueueDungeonRequest(null, tier, false);
            arg.ReplyWith($"Enqueued random dungeon spawn of tier: {_tierInfoMap[tier].Name} (no owner).");
        }

        private void SpawnFixedDungeonCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null && !IsAdmin(arg.Player()))
            {
                arg.ReplyWith("You do not have permission to run this command.");
                return;
            }
            if (!arg.HasArgs(1))
            {
                arg.ReplyWith("Usage: spawnfixeddungeon <tierName> [playerID]");
                return;
            }
            var tierName = arg.GetString(0);
            var tier = FindTierByName(tierName);
            if (tier == null)
            {
                arg.ReplyWith($"Invalid tier '{tierName}'. Available tiers: {GetAvailableTiers()}");
                return;
            }
            if (!tier.EnableAutoSpawn || !IsTierUnlockedByWipeTime(tier))
            {
                arg.ReplyWith("This tier is disabled for Auto Spawn.");
                return;
            }
            int activeAutoSameTier = _activeDungeons.Count(d => d.Origin == DungeonOrigin.AutoSpawn && d.TierConfig == tier);
            if (tier.MaxActiveAutoSpawn > 0 && activeAutoSameTier >= tier.MaxActiveAutoSpawn)
            {
                arg.ReplyWith("Reached the per-tier max for Auto Spawn.");
                return;
            }
            if (arg.Args != null && arg.Args.Length >= 2)
            {
                string playerId = arg.GetString(1);
                BasePlayer owner = BasePlayer.FindAwakeOrSleeping(playerId);
                if (owner == null)
                {
                    arg.ReplyWith($"Player with ID '{playerId}' not found.");
                    return;
                }
                var request = new DungeonRequest
                {
                    Player = owner,
                    Tier = tier,
                    LockToPlayer = true,
                    Origin = DungeonOrigin.AutoSpawn,
                };
                _dungeonQueue.Enqueue(request);
                ProcessDungeonQueue();
                arg.ReplyWith($"Enqueued dungeon spawn of tier: {_tierInfoMap[tier].Name} for player {owner.displayName}.");
                return;
            }
            EnqueueDungeonRequest(null, tier, false);
            arg.ReplyWith($"Enqueued dungeon spawn of tier: {_tierInfoMap[tier].Name} (no owner).");
        }

        private void ReloadConfigConsoleCommand(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player != null && !IsAdmin(player))
            {
                arg.ReplyWith("You do not have permission to run this command.");
                return;
            }
            ReloadPluginConfiguration(null, arg);
        }

        private void ListActiveDungeonsConsoleCommand(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player != null && !IsAdmin(player))
            {
                arg.ReplyWith("You do not have permission to run this command.");
                return;
            }
            SendActiveDungeonList(message => arg?.ReplyWith(message));
        }

        [ConsoleCommand(UiCommandClose)]
        private void CloseUiCommand(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null)
                DestroyUi(player);
        }
        #endregion

        #region Flow
        private DungeonPerformanceSettings Performance => _config.Performance ??= new DungeonPerformanceSettings();

        private void UpdatePerformanceDelays()
        {
            _buildBatchDelay = new WaitForSeconds(Mathf.Clamp(Performance.BuildBatchDelaySeconds, 0.01f, 5f));
            _spawnBatchDelay = new WaitForSeconds(Mathf.Clamp(Performance.EntitySpawnBatchDelaySeconds, 0.01f, 5f));
            _cleanupBatchDelay = new WaitForSeconds(Mathf.Clamp(Performance.CleanupBatchDelaySeconds, 0.01f, 5f));
            _searchBatchDelay = new WaitForSeconds(Mathf.Clamp(Performance.SearchBatchDelaySeconds, 0.01f, 5f));
        }

        private bool IsDungeonCreationPaused()
        {
            if (_activeDungeonCleanups > 0)
                return true;
            float cooldown = Mathf.Max(0f, Performance.AutoSpawnDelayAfterCleanupSeconds);
            return Time.realtimeSinceStartup - _lastCleanupCompletedAt < cooldown;
        }

        private IEnumerator StartupRoutine()
        {
            yield return CoroutineEx.waitForEndOfFrame;
            float delay = BasePlayer.activePlayerList.Count > 0 ? 1f : 60f;
            yield return new WaitForSeconds(delay);
            yield return RemoveLeftoverDungeonFiles();
            BlockZones();
            _dungeonTimer?.Destroy();
            _dungeonTimer = timer.Every(DungeonTimerInterval, HandleDungeonTimers);
        }

        private bool HasBlockedRespawnEntityNearby(Vector3 position, float radius)
        {
            var entities = Pool.Get<List<BaseEntity>>();
            try
            {
                Vis.Entities(position, radius, entities, Layers.Mask.Deployed);
                foreach (var entity in entities)
                {
                    if (entity == null)
                        continue;
                    var prefabName = entity.PrefabName;
                    if (string.IsNullOrEmpty(prefabName))
                        continue;
                    for (int i = 0; i < BlockedRespawnPrefabs.Length; i++)
                    {
                        if (prefabName.Equals(BlockedRespawnPrefabs[i], StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
            finally
            {
                Pool.FreeUnmanaged(ref entities);
            }
        }

        private IEnumerator CreateDungeon(DungeonRequest request)
        {
            _isCreatingDungeon = true;
            yield return null;
            bool hasPreselectedCustomPortal = request.UsesCustomPortalPosition && request.PortalPosition != Vector3.zero;
            Vector3 preselectedCustomPortalPosition = hasPreselectedCustomPortal ? request.PortalPosition : Vector3.zero;
            if (!hasPreselectedCustomPortal)
            {
                request.PortalPosition = Vector3.zero;
                request.UsesCustomPortalPosition = false;
            }
            request.DungeonPosition = Vector3.zero;
            var tierConfig = request.Tier;
            var player = request.Player;
            bool lockDungeonToPlayer = request.LockToPlayer;
            var origin = request.Origin;
            Vector3 portalPosition = Vector3.zero;
            Vector3 dungeonPosition = Vector3.zero;
            LogDebug("Generating portal spawn point.", 1);
            var sp = _config?.SpawnPointConfiguration ?? new SpawnPointConfiguration();
            bool customLocationsConfigured = false;
            if (hasPreselectedCustomPortal)
            {
                request.PortalPosition = preselectedCustomPortalPosition;
                request.UsesCustomPortalPosition = true;
                portalPosition = preselectedCustomPortalPosition;
                customLocationsConfigured = true;
                LogDebug($"Using preselected custom {origin} portal spawn at {MapHelper.PositionToString(portalPosition)}.", 1);
            }
            else if (TrySelectCustomPortalPosition(request, out var customPortalPosition, out customLocationsConfigured))
            {
                request.PortalPosition = customPortalPosition;
                request.UsesCustomPortalPosition = true;
                portalPosition = customPortalPosition;
                LogDebug($"Using custom {origin} portal spawn at {MapHelper.PositionToString(portalPosition)}.", 1);
            }
            else
            {
                if (!CanFallbackToRandomPortalPosition(origin, customLocationsConfigured))
                {
                    LogDebug($"No available custom {origin} portal spawn location and random fallback is disabled. Requeuing...", 1);
                    RetryDungeonCreation(request);
                    yield break;
                }
                var configPortal = new GeneratorConfig
                {
                    Origin = player != null ? player.transform.position : Vector3.zero,
                    ClosestToOrigin = player != null,
                    YOffset = 0f,
                    MinimumDistance = Mathf.Max(10f, _config?.MinimumPortalDistanceBetweenPortals ?? 50f),
                    BlockedPositions = _activeDungeons.Select(d => d.PortalPosition).ToList(),
                    AvoidTerrainTopology = sp.AvoidTerrainTopology,
                    AvoidedTopology = TerrainTopology.Enum.Building | TerrainTopology.Enum.Cliff | TerrainTopology.Enum.Monument | TerrainTopology.Enum.Ocean,
                    AboveWater = true,
                    MaxSlopeDegrees = 40f,
                    CheckSphereRadius = sp.CheckSphereRadius ? GetPortalProtectionRadius() + PurchasedPortalBaseAvoidanceExtraRadius : 0f,
                    CheckSphereMask =
                        Layers.Mask.AI
                        | Layers.Mask.Bush
                        | Layers.Mask.Construction
                        | Layers.Mask.Default
                        | Layers.Mask.Deployed
                        | Layers.Mask.Harvestable
                        | Layers.Mask.Physics_Debris
                        | Layers.Mask.Prevent_Building
                        | Layers.Mask.Prevent_Movement
                        | Layers.Mask.Tree
                        | Layers.Mask.Vehicle_Detailed
                        | Layers.Mask.Vehicle_Large
                        | Layers.Mask.Vehicle_World
                        | Layers.Server.Deployed,
                    MaxAttempts = 10000,
                    UseSlopeCheck = true,
                    CheckSafeZones = true,
                    ZoneManager = sp.CheckZoneManager,
                    TerrainMask = Layers.Mask.Terrain | Layers.Mask.World | Layers.Mask.Construction,
                    CheckRocks = true,
                    RockCheck = RockKeywords,
                };
                yield return TryFindPosition(configPortal, request, true);
                portalPosition = request.PortalPosition;
                if (player != null && portalPosition != Vector3.zero)
                    LogDebug($"Purchased portal selected {Vector3.Distance(player.transform.position, portalPosition):0.0}m from {player.displayName}.", 1);
            }
            LogDebug("Generating dungeon spawn point.", 1);
            bool undergroundDungeon = _config.DungeonSpawn.DungeonHeight < 0f;
            var configDungeon = new GeneratorConfig
            {
                Origin = Vector3.zero,
                ClosestToOrigin = false,
                YOffset = Mathf.Min(_config.DungeonSpawn.DungeonHeight, 700f),
                MinimumDistance = 200,
                BlockedPositions = _activeDungeons.Select(d => d.DungeonPosition).ToList(),
                AvoidTerrainTopology = undergroundDungeon || sp.AvoidTerrainTopology,
                AvoidedTopology = undergroundDungeon
                    ? TerrainTopology.Enum.Monument | TerrainTopology.Enum.Ocean | TerrainTopology.Enum.River | TerrainTopology.Enum.Lake
                    : TerrainTopology.Enum.Monument,
                WaterCheckOffset = undergroundDungeon ? new Vector3(-90f, 0f, 0f) : Vector3.zero,
                AvoidWaterRadius = undergroundDungeon ? GetUndergroundWaterClearanceRadius(tierConfig) : 0f,
                AboveWater = true,
                MaxSlopeDegrees = 30f,
                CheckSphereRadius = sp.CheckSphereRadius ? 150f : 0f,
                CheckSphereMask =
                    Layers.Mask.AI
                    | Layers.Mask.Bush
                    | Layers.Mask.Clutter
                    | Layers.Mask.Construction
                    | Layers.Mask.Default
                    | Layers.Mask.Deployed
                    | Layers.Mask.Harvestable
                    | Layers.Mask.Physics_Debris
                    | Layers.Mask.Player_Server
                    | Layers.Mask.Prevent_Building
                    | Layers.Mask.Prevent_Movement
                    | Layers.Mask.Tree
                    | Layers.Mask.Trigger
                    | Layers.Mask.Vehicle_Detailed
                    | Layers.Mask.Vehicle_Large
                    | Layers.Mask.Vehicle_World
                    | Layers.Mask.Water
                    | Layers.Mask.World
                    | Layers.Server.Deployed,
                MaxAttempts = 1000,
                UseSlopeCheck = true,
                CheckSafeZones = true,
                ZoneManager = sp.CheckZoneManager,
                TerrainMask = Layers.Mask.Terrain | Layers.Mask.World | Layers.Mask.Construction,
                CheckRocks = true,
                RockCheck = RockKeywords,
            };
            yield return TryFindPosition(configDungeon, request, false);
            dungeonPosition = request.DungeonPosition;
            if (portalPosition == Vector3.zero || dungeonPosition == Vector3.zero)
            {
                LogDebug("Failed to find spawn points. Requeuing...", 1);
                RetryDungeonCreation(request);
                yield break;
            }
            if (!request.UsesCustomPortalPosition && HasBlockedRespawnEntityNearby(portalPosition, 4f))
            {
                LogDebug("Rejected portal spawn point because a bed/sleeping bag/beach towel is too close. Requeuing...", 1);
                RetryDungeonCreation(request);
                yield break;
            }
            LogDebug("Dungeon spawn point found.");
            portalPosition += new Vector3(0f, 0.3f, 0f);
            request.PortalPosition = portalPosition;
            if (!_tierInfoMap.ContainsKey(tierConfig))
            {
                LogDebug($"[CreateDungeon] Tier '{tierConfig?.MapName}' disabled!", 2);
                ResetDungeonCreation();
                ProcessDungeonQueue();
                yield break;
            }
            var activeDungeon = RegisterActiveDungeon(null, portalPosition, dungeonPosition, MapHelper.PositionToString(portalPosition), tierConfig, player, lockDungeonToPlayer, origin);
            activeDungeon.DungeonBounds = new Bounds(activeDungeon.DungeonPosition, new Vector3(1, 1, 1));
            activeDungeon.Trigger = new GameObject().AddComponent<DungeonEventsTrigger>();
            activeDungeon.Trigger.Initialize(activeDungeon);
            activeDungeon.Trigger.SetPosition(activeDungeon.DungeonPosition, new Vector3(1, 1, 1));
            activeDungeon.Trigger.TriggerTime = tierConfig.TriggerTime;
            activeDungeon.Trigger.NightVisionTime = _config.NightVisionTime;
            if (tierConfig.DungeonRooms < 1 || tierConfig.DungeonRooms > 6)
                tierConfig.DungeonRooms = 1;
            if (tierConfig.DungeonRoomSize < 5 || tierConfig.DungeonRoomSize > 10)
                tierConfig.DungeonRoomSize = 5;
            activeDungeon.DungeonRooms = tierConfig.DungeonRooms;
            activeDungeon.DungeonRoomSize = tierConfig.DungeonRoomSize;
            float buildStarted = Time.realtimeSinceStartup;
            if (IsClassicDungeon(activeDungeon))
                yield return CreateClassicDungeon(activeDungeon);
            else
                yield return CreateCustomProceduralDynamicDungeon(activeDungeon);
            if (activeDungeon.EntranceLayoutFailed || activeDungeon.InsidePortal == null)
            {
                LogDebug("Could not build a collision-free L-shaped dungeon entrance. Requeuing dungeon generation.", 2);
                RetryDungeonCreation(request, activeDungeon);
                yield break;
            }
            if (_config.DungeonSpawn.DungeonHeight < 0f && DoesDungeonFootprintOverlapWater(activeDungeon))
            {
                LogDebug("Generated underground dungeon footprint overlaps surface water (ocean, river, or lake). Requeuing...", 1);
                RetryDungeonCreation(request, activeDungeon);
                yield break;
            }
            float boundsPadding = Mathf.Max(5f, GetPortalProtectionRadius());
            if (DoesDungeonOverlapExistingDungeons(activeDungeon, boundsPadding))
            {
                LogDebug("Generated dungeon bounds overlap an existing dungeon. Requeuing...", 1);
                RetryDungeonCreation(request, activeDungeon);
                yield break;
            }
            yield return BuildWalls(activeDungeon);
            yield return BuildFrames(activeDungeon);
            LogDebug($"Dungeon structure built in {Time.realtimeSinceStartup - buildStarted:0.00}s ({activeDungeon.Foundations.Count} foundations).", 1);
            LogDebug("Procedural Dynamic Dungeon Created.");
            activeDungeon.Trigger.SetPosition(activeDungeon.DungeonBounds.center, activeDungeon.DungeonBounds.size);
            if (activeDungeon.SpawnPoints.Count == 0)
            {
                RetryDungeonCreation(request, activeDungeon);
                LogDebug("Failed to find spawn points. Requeuing...", 1);
                yield break;
            }
            var spawns = new DungeonEntitySpawns();
            var spawnLimits = tierConfig.EntitySpawnLimits;
            SelectRouteBossSpawnPoints(activeDungeon, Math.Min(spawnLimits.BossesCounts, activeDungeon.SpawnPoints.Count), spawns.Bosses);
            var bossPoints = new HashSet<Vector3>(spawns.Bosses);
            var limitsWithoutBosses = new EntitySpawnLimits
            {
                NpcsCounts = spawnLimits.NpcsCounts,
                BossesCounts = 0,
                AnimalsCounts = spawnLimits.AnimalsCounts,
                TurretsCounts = spawnLimits.TurretsCounts,
                LootBoxesCounts = spawnLimits.LootBoxesCounts,
            };
            if (IsClassicDungeon(activeDungeon))
                yield return AssignClassicDungeonEntities(activeDungeon, spawns, bossPoints, limitsWithoutBosses);
            else
            {
                yield return AssignEntitiesToSpawnPoints(activeDungeon.SpawnPoints.Where(point => !bossPoints.Contains(point)).ToList(), tierConfig, spawns, limitsWithoutBosses);
                for (int i = 0; i < spawns.Bosses.Count; i++)
                    spawns.Bosses[i] += Vector3.up * 0.1f;
            }
            float entitySpawnStarted = Time.realtimeSinceStartup;
            yield return PlaceEntities(spawns.Turrets, spawns.Boxes, spawns.Npcs, spawns.Bosses, spawns.Animals, activeDungeon);
            LogDebug($"Dungeon entities spawned in {Time.realtimeSinceStartup - entitySpawnStarted:0.00}s.", 1);
            CreateDungeonMarkers(activeDungeon);
            activeDungeon.BasePortal = CreateDungeonPortal(portalPosition, activeDungeon);
            if (activeDungeon.BasePortal == null || activeDungeon.InsidePortal == null || activeDungeon.InsidePortal.IsDestroyed)
            {
                LogDebug("Could not create or link dungeon portals. Requeuing dungeon generation.", 2);
                RetryDungeonCreation(request, activeDungeon);
                yield break;
            }
            IndexDungeonEntity(activeDungeon.BasePortal, activeDungeon);
            activeDungeon.InsidePortal.targetPortal = activeDungeon.BasePortal;
            activeDungeon.BasePortal.targetPortal = activeDungeon.InsidePortal;
            activeDungeon.InsidePortal.LinkPortal();
            activeDungeon.BasePortal.LinkPortal();
            FlushDungeonEntityCache(activeDungeon.UniqueId);
            if (_config.DungeonSpawn.ShowDungeonSpawnAnnouncement || activeDungeon.Origin == DungeonOrigin.Buy)
                NotifyPlayersOfNewDungeon(activeDungeon);
            LogDebug($"{activeDungeon.TierName} Dungeon spawned successfully at grid: {activeDungeon.Grid}.", 1);
            activeDungeon.Spawned = true;
            Interface.CallHook("OnDungeonSpawn", activeDungeon.OwnerID, activeDungeon.PortalPosition, activeDungeon.Grid, activeDungeon.TierName);
            ResetDungeonCreation();
            UpdateMarkers();
            DrawLines(activeDungeon, player);
            ProcessDungeonQueue();
        }

        private void ResetDungeonCreation()
        {
            _currentDungeonRequest = null;
            _isCreatingDungeon = false;
        }

        private void RetryDungeonCreation(DungeonRequest request, ActiveDungeon dungeon = null)
        {
            if (dungeon != null)
                DestroyDungeon(dungeon);
            Requeue(request, request.Origin);
            ResetDungeonCreation();
            ProcessDungeonQueue();
        }

        private bool DoesDungeonOverlapExistingDungeons(ActiveDungeon activeDungeon, float padding)
        {
            if (activeDungeon == null || activeDungeon.DungeonBounds.size == Vector3.zero)
                return false;
            Bounds candidateBounds = ExpandBoundsXZ(activeDungeon.DungeonBounds, padding);
            foreach (var dungeon in _activeDungeons)
            {
                if (dungeon == null || dungeon == activeDungeon)
                    continue;
                if (dungeon.DungeonBounds.size != Vector3.zero)
                {
                    Bounds existingBounds = ExpandBoundsXZ(dungeon.DungeonBounds, padding);
                    if (BoundsOverlapXZ(candidateBounds, existingBounds))
                        return true;
                    continue;
                }
                if (dungeon.DungeonPosition == Vector3.zero)
                    continue;
                float fallbackDistance = Mathf.Max(150f, padding);
                float dx = activeDungeon.DungeonBounds.center.x - dungeon.DungeonPosition.x;
                float dz = activeDungeon.DungeonBounds.center.z - dungeon.DungeonPosition.z;
                if (dx * dx + dz * dz <= fallbackDistance * fallbackDistance)
                    return true;
            }
            return false;
        }

        private Bounds ExpandBoundsXZ(Bounds bounds, float padding)
        {
            var size = bounds.size;
            size.x += padding * 2f;
            size.z += padding * 2f;
            return new Bounds(bounds.center, size);
        }

        private bool BoundsOverlapXZ(Bounds a, Bounds b)
        {
            return a.min.x <= b.max.x && a.max.x >= b.min.x && a.min.z <= b.max.z && a.max.z >= b.min.z;
        }
        #endregion

        #region Behaviours
        private class DungeonGen2AnimalAggroController : MonoBehaviour
        {
            private ActiveDungeon _dungeon;
            private BaseEntity _entity;
            private BaseCombatEntity _combat;
            private SenseComponent _senses;
            private RootMotionPlayer _rootMotion;
            private TigerFSM _tiger;
            private CrocodileFSM _crocodile;
            private FSMStateBase _activeAttackState;
            private Vector3 _lastSafeAttackPosition;
            private Quaternion _lastSafeAttackRotation;
            private bool _hasSafeAttackPosition;
            private float _damage;
            private float _attackRange;
            private float _attackCooldown;
            private float _senseRange;
            private float _nextAttack;
            private float _attackLockUntil;
            private int _blockMask;
            private State_AttackWithTracking _activeCrocAttack;
            private BasePlayer _activeCrocTarget;
            private float _activeCrocTargetHealth;
            private BasePlayer _activeTigerTarget;
            private float _activeTigerTargetHealth;
            private Action _tigerFallbackDamageAction;
            private Action _crocFallbackDamageAction;
            private Action _stopAttackAnimationAction;
            private readonly Collider[] _playersBuffer = new Collider[96];
            public bool IsAttacking => Time.time < _attackLockUntil;
            private Wolf2FSM _wolf;
            private BasicAnimalFsm _basic;
            private LivestockAnimalFSM _livestock;
            private RootMotionPlayer.PlayServerState _manualAttackState;
            private BasePlayer _manualDamageTarget;
            private Action _manualDamageAction;
            private float _manualAttackDuration;
            private bool _animationErrorLogged;
            private BasePlayer _activeWolfTarget;
            private float _activeWolfTargetHealth;
            private Action _wolfFallbackDamageAction;
            private NPCNetworking _networking;
            private float _nextNetworkingTick;

            public void Init(ActiveDungeon dungeon, BaseEntity entity, float damage, float attackRange, float attackCooldown, float senseRange)
            {
                _dungeon = dungeon;
                _entity = entity;
                _combat = entity as BaseCombatEntity;
                _damage = Mathf.Max(0f, damage);
                _attackRange = Mathf.Clamp(attackRange, 0.75f, 6f);
                _attackCooldown = Mathf.Clamp(attackCooldown, 0.6f, 5f);
                _senseRange = Mathf.Clamp(senseRange, _attackRange + 2f, 80f);
                _senses = entity.GetComponent<SenseComponent>();
                _rootMotion = entity.GetComponent<RootMotionPlayer>();
                _tiger = entity.GetComponent<TigerFSM>();
                _crocodile = entity.GetComponent<CrocodileFSM>();
                _wolf = entity.GetComponent<Wolf2FSM>();
                _basic = entity.GetComponent<BasicAnimalFsm>();
                _livestock = entity.GetComponent<LivestockAnimalFSM>();
                _manualDamageAction = ApplyManualAttackDamage;
                _networking = entity.GetComponent<NPCNetworking>();
                _blockMask = Layers.Mask.Construction | Layers.Mask.Deployed | Layers.Mask.World | Layers.Mask.Terrain | Layers.Mask.Default;
                _stopAttackAnimationAction = StopAttackAnimation;
                _tigerFallbackDamageAction = ApplyTigerDamageFallback;
                _crocFallbackDamageAction = ApplyCrocDamageFallback;
                _wolfFallbackDamageAction = ApplyWolfDamageFallback;
                PrepareAttackStates();
                DisableGen2RuntimeNavigation();
                InvokeRepeating(nameof(Tick), Random.Range(0.10f, 0.30f), 0.1f);
            }

            private void OnDestroy()
            {
                CancelInvoke();
                StopAttackAnimation();
            }

            private void Tick()
            {
                if (_entity == null || _entity.IsDestroyed || _combat == null || _dungeon == null)
                {
                    CancelInvoke();
                    return;
                }
                if (_combat.IsDead() || _combat.Health() <= 0f)
                {
                    ForceVisualDeath();
                    return;
                }
                if (
                    _instance != null
                    && (_instance.IsInDungeonEntranceCorridor(_dungeon, _entity.transform.position) || !_instance.TryGetDungeonFloorPoint(_dungeon, _entity.transform.position, out _))
                )
                {
                    StopAttackAnimation();
                    var home = _entity.GetComponent<AnimalHomePosition>();
                    if (home != null)
                    {
                        _instance.TryReturnDungeonEnemyHome(_entity, _dungeon, home.HomePosition, home.HomeRotation);
                    }
                    return;
                }
                if (_manualAttackState != null)
                {
                    DisableCurrentRootMotionNavConstraint(_entity, _livestock != null);
                    if (!GuardAttackPosition())
                        return;
                    if (!_manualAttackState.isPlaying)
                        StopAttackAnimation();
                }
                if (_activeAttackState != null)
                {
                    DisableCurrentRootMotionNavConstraint(_entity);
                    var status = _activeAttackState.OnStateUpdate(0.1f);
                    DisableCurrentRootMotionNavConstraint(_entity);
                    TickNetworking();
                    if (!GuardAttackPosition())
                        return;
                    if (status == EFSMStateStatus.Success || status == EFSMStateStatus.Failure)
                        StopAttackAnimation();
                }
                var target = FindClosestVisiblePlayer(_senseRange);
                if (target == null)
                    return;
                FeedSenses(target);
                if (!IsAttacking)
                    FaceTarget(target);
                TickNetworking();
                float distance = Vector3.Distance(_entity.transform.position, target.transform.position);
                if (distance > _attackRange)
                    return;
                if (Time.time < _nextAttack)
                    return;
                if (!IsFacingTarget(target, 110f))
                    return;
                if (TryPlayAttack(target))
                {
                    float lockTime = Mathf.Max(_attackCooldown, _manualAttackDuration);
                    _nextAttack = Time.time + lockTime;
                    _attackLockUntil = Time.time + lockTime;
                }
            }

            private void TickNetworking()
            {
                if (_networking == null)
                    return;
                if (Time.time < _nextNetworkingTick)
                    return;
                _nextNetworkingTick = Time.time + 0.25f;
                try
                {
                    _networking.Tick();
                }
                catch { }
            }

            private void CaptureSafeAttackPosition()
            {
                if (_entity == null || _entity.IsDestroyed)
                    return;
                _lastSafeAttackPosition = _entity.transform.position;
                _lastSafeAttackRotation = _entity.transform.rotation;
                _hasSafeAttackPosition = true;
            }

            private bool GuardAttackPosition()
            {
                if (_entity == null || _entity.IsDestroyed)
                    return false;
                if (!_hasSafeAttackPosition)
                {
                    CaptureSafeAttackPosition();
                    return true;
                }
                Vector3 currentPosition = _entity.transform.position;
                if (_instance != null && _instance.IsEnemyEntrancePositionBlocked(_dungeon, currentPosition))
                {
                    RestoreSafeAttackPosition();
                    StopAttackAnimation();
                    return false;
                }
                if (!IsInsideDungeonXZ(currentPosition))
                {
                    RestoreSafeAttackPosition();
                    StopAttackAnimation();
                    return false;
                }
                if (HasCrossedWall(_lastSafeAttackPosition, currentPosition))
                {
                    RestoreSafeAttackPosition();
                    StopAttackAnimation();
                    return false;
                }
                if (Vector3.Distance(_lastSafeAttackPosition, currentPosition) > GetAllowedAttackRootMotionDistance())
                {
                    RestoreSafeAttackPosition();
                    StopAttackAnimation();
                    return false;
                }
                _lastSafeAttackPosition = currentPosition;
                _lastSafeAttackRotation = _entity.transform.rotation;
                return true;
            }

            private float GetAllowedAttackRootMotionDistance()
            {
                if (_tiger != null && _activeAttackState == _tiger.attack)
                    return Mathf.Max(_attackRange + 1.75f, 4.25f);
                if (_wolf != null && _activeAttackState == _wolf.attack)
                    return Mathf.Max(_attackRange + 0.75f, 2.75f);
                if (_activeCrocAttack != null)
                    return Mathf.Max(_activeCrocAttack.radius, _attackRange) + 0.75f;
                return 1.25f;
            }

            private void RestoreSafeAttackPosition()
            {
                if (_entity == null || _entity.IsDestroyed)
                    return;
                if (!_hasSafeAttackPosition)
                    return;
                _entity.transform.position = _lastSafeAttackPosition;
                _entity.transform.rotation = _lastSafeAttackRotation;
                _entity.UpdateNetworkGroup();
                _entity.SendNetworkUpdateImmediate();
            }

            private bool IsInsideDungeonXZ(Vector3 position)
            {
                if (_dungeon == null || _dungeon.DungeonBounds.size == Vector3.zero)
                    return true;
                Bounds bounds = _dungeon.DungeonBounds;
                return position.x >= bounds.min.x + 0.35f && position.x <= bounds.max.x - 0.35f && position.z >= bounds.min.z + 0.35f && position.z <= bounds.max.z - 0.35f;
            }

            private bool HasCrossedWall(Vector3 from, Vector3 to)
            {
                Vector3 delta = to - from;
                delta.y = 0f;
                if (delta.sqrMagnitude < 0.0001f)
                    return false;
                Vector3 start = from + Vector3.up * 0.85f;
                Vector3 end = to + Vector3.up * 0.85f;
                return Physics.Linecast(start, end, _blockMask, QueryTriggerInteraction.Ignore);
            }

            private void PrepareAttackStates()
            {
                if (_tiger != null && _tiger.attack != null)
                {
                    _tiger.attack.Owner = _entity;
                    _tiger.attack.Damage = _damage;
                    _tiger.attack.DamageType = DamageType.Bite;
                    _tiger.attack.range = Mathf.Max(_attackRange, 3.25f);
                }
                if (_wolf != null && _wolf.attack != null)
                {
                    _wolf.attack.Owner = _entity;
                }
                if (_crocodile != null)
                {
                    PrepareCrocAttack(_crocodile.staticAttack, Mathf.Max(_attackRange, 2.4f));
                    PrepareCrocAttack(_crocodile.intimidateSnap, Mathf.Max(_attackRange, 2.4f));
                    PrepareCrocAttack(_crocodile.bellyFlop, Mathf.Max(_attackRange, 3.5f));
                    PrepareCrocAttack(_crocodile.swimAttack, Mathf.Max(_attackRange, 3.5f));
                }
            }

            private void PrepareCrocAttack(State_AttackWithTracking attack, float radius)
            {
                if (attack == null)
                    return;
                attack.Owner = _entity;
                attack.damage = _damage;
                attack.radius = Mathf.Max(radius, _attackRange + 2.25f);
                attack.DamageType = DamageType.Bite;
                attack.damageDelay = 0.12f;
                attack.trackingSpeed = 420f;
                attack.trackingDuration = 1.2f;
                attack.forceScale = 1f;
                attack.doesStrafeDodge = false;
            }

            private void DisableGen2RuntimeNavigation()
            {
                var fsm = _entity.GetComponent<FSMComponent>();
                if (fsm != null)
                    fsm.SetFsmActive(false);
                var rustNav = _entity.GetComponent<RustNavMeshAgent>();
                if (rustNav != null)
                {
                    try
                    {
                        rustNav.speed = 0f;
                        rustNav.overrideDirectionWS = null;
                    }
                    catch { }
                    try
                    {
                        rustNav.enabled = false;
                    }
                    catch { }
                }
                var navAgent = _entity.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (navAgent != null)
                {
                    try
                    {
                        if (navAgent.enabled && navAgent.isOnNavMesh && navAgent.hasPath)
                            navAgent.ResetPath();
                        navAgent.enabled = false;
                    }
                    catch { }
                }
            }

            private bool TryPlayAttack(BasePlayer target)
            {
                if (target == null)
                    return false;
                if (_rootMotion == null)
                    return ApplyDirectAttackDamage(target);
                FeedSenses(target);
                FaceTarget(target);
                if (_tiger != null && _tiger.attack != null && _tiger.attack.Animation != null)
                    return PlayTigerAttack(target);
                if (_wolf != null && _wolf.attack != null)
                    return PlayWolfAttack(target);
                if (_crocodile != null)
                {
                    if (_crocodile.intimidateSnap != null && _crocodile.intimidateSnap.Animation != null)
                        return PlayCrocAttack(_crocodile.intimidateSnap, target);
                    if (_crocodile.staticAttack != null && _crocodile.staticAttack.Animation != null)
                        return PlayCrocAttack(_crocodile.staticAttack, target);
                    if (_crocodile.bellyFlop != null && _crocodile.bellyFlop.Animation != null)
                        return PlayCrocAttack(_crocodile.bellyFlop, target);
                }
                if (_basic != null && _basic.attack != null)
                    return PlayBasicAnimalAttack(target) || ApplyDirectAttackDamage(target);
                if (_livestock != null && _livestock.headbutt != null)
                    return PlayLivestockAttack(target) || ApplyDirectAttackDamage(target);
                return ApplyDirectAttackDamage(target);
            }

            private bool PlayBasicAnimalAttack(BasePlayer target)
            {
                StopAttackAnimation();
                var bite = _basic?.attack;
                if (bite == null)
                    return false;
                bite.Owner = _entity;
                FeedSenses(target);
                FaceTarget(target);
                CaptureSafeAttackPosition();
                try
                {
                    bite.PlayMontage();
                    _manualAttackState = bite.animState;
                }
                catch (Exception ex)
                {
                    LogAnimationError(ex);
                    _manualAttackState = null;
                }
                if (_manualAttackState == null)
                {
                    DisableGen2RuntimeNavigation();
                    return false;
                }
                DisableCurrentRootMotionNavConstraint(_entity);
                float duration = _manualAttackState.GetAnimLength() / Mathf.Max(0.1f, bite.PlaybackSpeed);
                BeginManualAttack(target, duration, Mathf.Clamp(bite.DamageAtClipFraction, 0.1f, 0.85f));
                return true;
            }

            private bool PlayLivestockAttack(BasePlayer target)
            {
                StopAttackAnimation();
                if (_livestock == null || _livestock.headbutt == null)
                    return false;
                var animation = _livestock.headbutt.Animation;
                if (!(_entity is Sheep) && _livestock.kick != null && _livestock.kick.Animation != null && Random.value < 0.30f)
                    animation = _livestock.kick.Animation;
                if (animation == null)
                    return false;
                FeedSenses(target);
                FaceTarget(target);
                CaptureSafeAttackPosition();
                RootMotionPlayer.PlayServerState state = null;
                try
                {
                    state = Facepunch.Pool.Get<RootMotionPlayer.PlayServerState>();
                    state.rmData = animation;
                    state.initialLocation = _entity.transform.position;
                    state.initialRotation = _entity.transform.rotation;
                    state.SeedMotionAccumulators();
                    state.pauseAgent = false;
                    state.constrainToNavmesh = false;
                    state.warps = new[] { new RootMotionPlayer.Warp(0f, 30f, Vector3.zero, 0f) };
                    _rootMotion.PlayServer(state);
                    _manualAttackState = state;
                }
                catch (Exception ex)
                {
                    LogAnimationError(ex);
                    if (state != null)
                        _rootMotion.StopServerAndReturnToPool(ref state);
                    _manualAttackState = null;
                }
                if (_manualAttackState == null)
                {
                    DisableGen2RuntimeNavigation();
                    return false;
                }
                BeginManualAttack(target, _manualAttackState.GetAnimLength(), 0.35f);
                return true;
            }

            private void BeginManualAttack(BasePlayer target, float duration, float hitFraction)
            {
                _manualDamageTarget = target;
                _manualAttackDuration = Mathf.Max(0.25f, duration);
                float delay = Mathf.Clamp(_manualAttackDuration * hitFraction, 0.12f, Mathf.Max(0.15f, _manualAttackDuration - 0.25f));
                _entity.Invoke(_manualDamageAction, delay);
                _entity.Invoke(_stopAttackAnimationAction, Mathf.Max(0.25f, _manualAttackDuration - 0.08f));
            }

            private void ApplyManualAttackDamage()
            {
                BasePlayer target = _manualDamageTarget;
                _manualDamageTarget = null;
                ApplyDirectAttackDamage(target);
            }

            private bool ApplyDirectAttackDamage(BasePlayer target)
            {
                if (_entity == null || _entity.IsDestroyed || _combat == null || _combat.IsDead() || target == null || target.IsDead() || target.IsSleeping())
                    return false;
                if (_dungeon == null || !_dungeon.PlayersInside.Contains(target.userID) || (_instance != null && _instance.IsInDungeonEntranceSafeZone(_dungeon, target.transform.position)))
                    return false;
                if (Mathf.Abs(target.transform.position.y - _entity.transform.position.y) > 2.5f)
                    return false;
                if (Vector3.Distance(_entity.transform.position, target.transform.position) > _attackRange + 0.4f || !IsVisibleForDamage(target))
                    return false;
                target.Hurt(_damage, DamageType.Bite, _entity);
                return true;
            }

            private void LogAnimationError(Exception ex)
            {
                if (_animationErrorLogged)
                    return;
                _animationErrorLogged = true;
                _instance?.PrintWarning($"DungeonEvents animal animation failed ({_entity?.ShortPrefabName}): {ex.GetType().Name}: {ex.Message}");
            }

            private bool PlayWolfAttack(BasePlayer target)
            {
                StopAttackAnimation();
                if (_wolf == null || _wolf.attack == null || target == null)
                    return false;
                _wolf.attack.Owner = _entity;
                FeedSenses(target);
                FaceTarget(target);
                CaptureSafeAttackPosition();
                _activeWolfTarget = target;
                _activeWolfTargetHealth = target.Health();
                var status = _wolf.attack.OnStateEnter(default(FSMPayload));
                if (status == EFSMStateStatus.Failure)
                {
                    ClearWolfDamageData();
                    return false;
                }
                _activeAttackState = _wolf.attack;
                DisableCurrentRootMotionNavConstraint(_entity);
                _entity.Invoke(_wolfFallbackDamageAction, 0.22f);
                _entity.Invoke(_stopAttackAnimationAction, 1.15f);
                return true;
            }

            private bool PlayTigerAttack(BasePlayer target)
            {
                StopAttackAnimation();
                _tiger.attack.Owner = _entity;
                FeedSenses(target);
                FaceTarget(target);
                CaptureSafeAttackPosition();
                _activeTigerTarget = target;
                _activeTigerTargetHealth = target != null ? target.Health() : 0f;
                var status = _tiger.attack.OnStateEnter(default(FSMPayload));
                if (status == EFSMStateStatus.Failure)
                {
                    ClearTigerDamageData();
                    return false;
                }
                _activeAttackState = _tiger.attack;
                DisableCurrentRootMotionNavConstraint(_entity);
                float fallbackDelay = Mathf.Clamp(_tiger.attack.Delay + 0.08f, 0.16f, 0.75f);
                float stopDelay = Mathf.Clamp(_tiger.attack.Delay + 1.05f, 0.9f, 2.25f);
                _entity.Invoke(_tigerFallbackDamageAction, fallbackDelay);
                _entity.Invoke(_stopAttackAnimationAction, stopDelay);
                return true;
            }

            private bool PlayCrocAttack(State_AttackWithTracking attack, BasePlayer target)
            {
                StopAttackAnimation();
                attack.Owner = _entity;
                if (target == null)
                    return false;
                FeedSenses(target);
                FaceTarget(target);
                CaptureSafeAttackPosition();
                _activeCrocAttack = attack;
                _activeCrocTarget = target;
                _activeCrocTargetHealth = target.Health();
                var status = attack.OnStateEnter(default(FSMPayload));
                if (status == EFSMStateStatus.Failure)
                {
                    ClearCrocDamageData();
                    return false;
                }
                _activeAttackState = attack;
                DisableCurrentRootMotionNavConstraint(_entity);
                float fallbackDelay = Mathf.Clamp(attack.damageDelay + 0.12f, 0.16f, 0.65f);
                float stopDelay = Mathf.Clamp(attack.damageDelay + 0.95f, 0.9f, 2.25f);
                _entity.Invoke(_crocFallbackDamageAction, fallbackDelay);
                _entity.Invoke(_stopAttackAnimationAction, stopDelay);
                return true;
            }

            private void ApplyWolfDamageFallback()
            {
                if (_entity == null || _entity.IsDestroyed || _combat == null || _combat.IsDead())
                    return;
                if (_activeWolfTarget == null || _activeWolfTarget.IsDead())
                    return;
                if (!_dungeon.PlayersInside.Contains(_activeWolfTarget.userID) || (_instance != null && _instance.IsInDungeonEntranceSafeZone(_dungeon, _activeWolfTarget.transform.position)))
                    return;
                float distance = Vector3.Distance(_entity.transform.position, _activeWolfTarget.transform.position);
                if (distance > Mathf.Max(_attackRange + 0.85f, 2.75f))
                    return;
                if (!IsVisibleForDamage(_activeWolfTarget))
                    return;
                FeedSenses(_activeWolfTarget);
                FaceTarget(_activeWolfTarget);
                if (_activeWolfTarget.Health() < _activeWolfTargetHealth - 0.01f)
                    return;
                ApplyDirectWolfDamage(_activeWolfTarget);
            }

            private void ApplyDirectWolfDamage(BasePlayer target)
            {
                if (target == null || target.IsDead())
                    return;
                target.OnAttacked(_damage, DamageType.Bite, _entity, ignoreShield: false);
                Vector3 flat = target.transform.position - _entity.transform.position;
                flat.y = 0f;
                if (flat.sqrMagnitude <= 1e-6f)
                    return;
                Vector3 force = (_entity.transform.forward * 8f + Vector3.up * 2f);
                target.DoPush(force);
            }

            private void ClearWolfDamageData()
            {
                _activeWolfTarget = null;
                _activeWolfTargetHealth = 0f;
            }

            private void ApplyTigerDamageFallback()
            {
                if (_entity == null || _entity.IsDestroyed || _combat == null || _combat.IsDead())
                    return;
                if (_activeTigerTarget == null || _activeTigerTarget.IsDead())
                    return;
                if (!_dungeon.PlayersInside.Contains(_activeTigerTarget.userID) || (_instance != null && _instance.IsInDungeonEntranceSafeZone(_dungeon, _activeTigerTarget.transform.position)))
                    return;
                float distance = Vector3.Distance(_entity.transform.position, _activeTigerTarget.transform.position);
                if (distance > Mathf.Max(_attackRange + 1.25f, 4f))
                    return;
                if (!IsVisibleForDamage(_activeTigerTarget))
                    return;
                FeedSenses(_activeTigerTarget);
                FaceTarget(_activeTigerTarget);
                if (_activeTigerTarget.Health() < _activeTigerTargetHealth - 0.01f)
                    return;
                _tiger.attack.DoDamage();
                if (_activeTigerTarget == null || _activeTigerTarget.IsDead())
                    return;
                if (_activeTigerTarget.Health() < _activeTigerTargetHealth - 0.01f)
                    return;
                ApplyDirectTigerDamage(_activeTigerTarget);
            }

            private void ApplyDirectTigerDamage(BasePlayer target)
            {
                if (target == null || target.IsDead())
                    return;
                float amount = _damage;
                float protection = target.baseProtection.Get(DamageType.Bite);
                float scale = (1f - protection) * 1.2f;
                float calculated = _damage * scale;
                if (protection == 0f && target.health >= 30f && calculated > target.health)
                    amount = (target.health - 1f) * (1f / scale);
                target.OnAttacked(amount, DamageType.Bite, _entity, ignoreShield: false);
                Vector3 flat = target.transform.position - _entity.transform.position;
                flat.y = 0f;
                if (flat.sqrMagnitude <= 1e-6f)
                    return;
                Vector3 side = Vector3.Dot(_entity.transform.right, flat.normalized) > 0f ? _entity.transform.right : -_entity.transform.right;
                Vector3 force = _entity.transform.forward * State_DeadlyAttack.force.z + side * State_DeadlyAttack.force.x + Vector3.up * State_DeadlyAttack.force.y;
                target.DoPush(force);
            }

            private void ApplyCrocDamageFallback()
            {
                if (_entity == null || _entity.IsDestroyed || _combat == null || _combat.IsDead())
                    return;
                if (_activeCrocAttack == null || _activeCrocTarget == null || _activeCrocTarget.IsDead())
                    return;
                if (!_dungeon.PlayersInside.Contains(_activeCrocTarget.userID) || (_instance != null && _instance.IsInDungeonEntranceSafeZone(_dungeon, _activeCrocTarget.transform.position)))
                    return;
                float distance = Vector3.Distance(_entity.transform.position, _activeCrocTarget.transform.position);
                if (distance > _activeCrocAttack.radius)
                    return;
                if (!IsVisibleForDamage(_activeCrocTarget))
                    return;
                FeedSenses(_activeCrocTarget);
                FaceTarget(_activeCrocTarget);
                if (_activeCrocTarget.Health() < _activeCrocTargetHealth - 0.01f)
                    return;
                _activeCrocAttack.DoDamage();
                if (_activeCrocTarget == null || _activeCrocTarget.IsDead())
                    return;
                if (_activeCrocTarget.Health() < _activeCrocTargetHealth - 0.01f)
                    return;
                ApplyDirectCrocDamage(_activeCrocTarget, _activeCrocAttack);
            }

            private void ApplyDirectCrocDamage(BasePlayer target, State_AttackWithTracking attack)
            {
                if (target == null || target.IsDead() || attack == null)
                    return;
                target.OnAttacked(attack.damage, attack.DamageType, _entity, ignoreShield: false);
                if (attack.forceScale <= 0f)
                    return;
                Vector3 flat = target.transform.position - _entity.transform.position;
                flat.y = 0f;
                if (flat.sqrMagnitude <= 1e-6f)
                    return;
                Vector3 side = Vector3.Dot(_entity.transform.right, flat.normalized) > 0f ? _entity.transform.right : -_entity.transform.right;
                Vector3 force =
                    (State_AttackWithTracking.force.z * _entity.transform.forward + State_AttackWithTracking.force.x * side + Vector3.up * State_AttackWithTracking.force.y) * attack.forceScale;
                target.DoPush(force);
            }

            private bool IsVisibleForDamage(BasePlayer target)
            {
                if (target == null || _entity == null)
                    return false;
                Vector3 from = _entity.CenterPoint();
                Vector3 to = target.eyes?.position ?? target.transform.position + Vector3.up * 1.6f;
                return !Physics.Linecast(from, to, _blockMask, QueryTriggerInteraction.Ignore);
            }

            private void ClearTigerDamageData()
            {
                _activeTigerTarget = null;
                _activeTigerTargetHealth = 0f;
            }

            private void ClearCrocDamageData()
            {
                _activeCrocAttack = null;
                _activeCrocTarget = null;
                _activeCrocTargetHealth = 0f;
            }

            private void StopAttackAnimation()
            {
                if (_entity != null)
                {
                    _entity.CancelInvoke(_stopAttackAnimationAction);
                    if (_tigerFallbackDamageAction != null)
                        _entity.CancelInvoke(_tigerFallbackDamageAction);
                    if (_crocFallbackDamageAction != null)
                        _entity.CancelInvoke(_crocFallbackDamageAction);
                    if (_wolfFallbackDamageAction != null)
                        _entity.CancelInvoke(_wolfFallbackDamageAction);
                    if (_manualDamageAction != null)
                        _entity.CancelInvoke(_manualDamageAction);
                }
                if (_manualAttackState != null && _rootMotion != null)
                {
                    if (_basic != null && _basic.attack != null && ReferenceEquals(_basic.attack.animState, _manualAttackState))
                    {
                        _rootMotion.StopServerAndReturnToPool(ref _basic.attack.animState);
                        _manualAttackState = null;
                    }
                    else
                        _rootMotion.StopServerAndReturnToPool(ref _manualAttackState);
                }
                if (_activeAttackState != null)
                {
                    try
                    {
                        _activeAttackState.OnStateExit();
                    }
                    catch { }
                    _activeAttackState = null;
                }
                else if (_rootMotion != null && _rootMotion.currentPlayState != null)
                {
                    _rootMotion.StopServer(_rootMotion.currentPlayState, false);
                }
                ClearTigerDamageData();
                ClearCrocDamageData();
                ClearWolfDamageData();
                _manualDamageTarget = null;
                _manualAttackDuration = 0f;
                DisableGen2RuntimeNavigation();
                if (
                    _hasSafeAttackPosition
                    && _entity != null
                    && !_entity.IsDestroyed
                    && (Mathf.Abs(_entity.transform.position.y - _lastSafeAttackPosition.y) > 0.65f || !IsInsideDungeonXZ(_entity.transform.position))
                )
                    RestoreSafeAttackPosition();
                _hasSafeAttackPosition = false;
            }

            private void ForceVisualDeath()
            {
                StopAttackAnimation();
                CancelInvoke();
                if (_entity == null || _entity.IsDestroyed)
                    return;
                _entity.Invoke(
                    () =>
                    {
                        if (_entity == null || _entity.IsDestroyed)
                            return;
                        _entity.Kill();
                    },
                    0.01f
                );
            }

            private void FeedSenses(BasePlayer target)
            {
                if (_senses == null || target == null)
                    return;
                Vector3 targetLookPosition = target.eyes != null ? target.eyes.position : target.transform.position + Vector3.up * 1.6f;
                _senses.Forget(target);
                _senses.SimulateSighting(target, targetLookPosition);
                if (_senses.GetVisibilityStatus(target, out var status))
                    status.UpdateVisibility(true, 0.1f, 2f, targetLookPosition, 2f);
                _senses.TrySetTarget(target, true);
                _senses.HasPlayerInVicinity = true;
            }

            private void FaceTarget(BasePlayer target)
            {
                if (_entity == null || target == null)
                    return;
                Vector3 direction = target.transform.position - _entity.transform.position;
                direction.y = 0f;
                if (direction.sqrMagnitude <= 1e-6f)
                    return;
                FaceEntityDirection(_entity, direction);
            }

            private bool IsFacingTarget(BasePlayer target, float maxAngle)
            {
                if (_entity == null || target == null)
                    return false;
                Vector3 direction = target.transform.position - _entity.transform.position;
                direction.y = 0f;
                if (direction.sqrMagnitude <= 1e-6f)
                    return true;
                return Vector3.Angle(_entity.transform.forward, direction.normalized) <= maxAngle;
            }

            private BasePlayer FindClosestVisiblePlayer(float radius)
            {
                if (_dungeon == null || _dungeon.PlayersInside.Count == 0)
                    return null;
                int hits = Physics.OverlapSphereNonAlloc(_entity.transform.position, radius, _playersBuffer, Layers.Mask.Player_Server, QueryTriggerInteraction.Ignore);
                BasePlayer best = null;
                float bestSqr = radius * radius;
                for (int i = 0; i < hits; i++)
                {
                    var player = _playersBuffer[i]?.GetComponent<BasePlayer>();
                    if (player == null || !player.userID.IsSteamId() || player.IsNpc || player.IsSleeping() || player.IsDead())
                        continue;
                    if (_dungeon == null || !_dungeon.PlayersInside.Contains(player.userID))
                        continue;
                    if (_instance != null && _instance.IsInDungeonEntranceSafeZone(_dungeon, player.transform.position))
                        continue;
                    Vector3 from = _entity.CenterPoint();
                    Vector3 to = player.eyes?.position ?? player.transform.position + Vector3.up * 1.6f;
                    float sqr = (to - from).sqrMagnitude;
                    if (sqr > bestSqr)
                        continue;
                    if (Physics.Linecast(from, to, _blockMask, QueryTriggerInteraction.Ignore))
                        continue;
                    best = player;
                    bestSqr = sqr;
                }
                return best;
            }
        }

        public class DungeonAnimalMovement : MonoBehaviour
        {
            private ActiveDungeon _dungeon;
            private BaseCombatEntity _animal;
            private float _speed;
            private float _senseRange;
            private float _maxDistanceFromHome;
            private float _attackRange;
            private float _nextPush;
            private Vector3 _home;
            private int _blockMask;
            private readonly Collider[] _playersBuffer = new Collider[128];
            private readonly Collider[] _nearbyBuffer = new Collider[64];
            private Vector3 _lastNetworkPosition;
            private Quaternion _lastNetworkRotation;
            private bool _hasNetworkTransform;

            public void Init(ActiveDungeon dungeon, BaseCombatEntity animal, float moveSpeed, float senseRange, float maxDistanceFromHome, float attackRange)
            {
                _dungeon = dungeon;
                _animal = animal;
                _speed = Mathf.Clamp(moveSpeed, 0.25f, 5f);
                _senseRange = Mathf.Clamp(senseRange, 3f, 80f);
                _maxDistanceFromHome = Mathf.Max(0f, maxDistanceFromHome);
                _attackRange = Mathf.Clamp(attackRange, 0.5f, 6f);
                _home = animal.transform.position;
                _blockMask = Layers.Mask.Construction | Layers.Mask.Deployed | Layers.Mask.World | Layers.Mask.Terrain | Layers.Mask.Default;
                _lastNetworkPosition = _animal.transform.position;
                _lastNetworkRotation = _animal.transform.rotation;
                InvokeRepeating(nameof(Tick), Random.Range(0.15f, 0.40f), 0.2f);
            }

            public void SetHomePosition(Vector3 position)
            {
                _home = position;
            }

            private void OnDestroy()
            {
                CancelInvoke();
            }

            private void Tick()
            {
                if (_animal == null || _animal.IsDestroyed || _dungeon == null || _instance == null)
                {
                    Destroy(this);
                    return;
                }
                if (_animal.IsDead() || _animal.Health() <= 0.01f)
                {
                    var deadAnimal = _animal;
                    var attack = deadAnimal.GetComponent<DungeonAnimalAttack>();
                    if (attack != null)
                        UnityEngine.Object.Destroy(attack);
                    Destroy(this);
                    deadAnimal.CancelInvoke();
                    _instance.MarkDungeonNpcDefeated(_dungeon, deadAnimal);
                    if (!deadAnimal.IsDestroyed)
                        deadAnimal.Kill();
                    return;
                }
                if (_instance.IsInDungeonEntranceCorridor(_dungeon, _animal.transform.position) || !_instance.TryGetDungeonFloorPoint(_dungeon, _animal.transform.position, out _))
                {
                    _instance.TryReturnDungeonEnemyHome(_animal, _dungeon, _home, _animal.transform.rotation);
                    return;
                }
                var gen2Attack = _animal.GetComponent<DungeonGen2AnimalAggroController>();
                if (gen2Attack != null && gen2Attack.IsAttacking)
                {
                    SnapToGround();
                    _animal.SendNetworkUpdate();
                    return;
                }
                var player = GetClosestDungeonPlayer(true, _senseRange);
                if (player != null)
                {
                    Vector3 toPlayer = player.transform.position - _animal.transform.position;
                    toPlayer.y = 0f;
                    float distance = toPlayer.magnitude;
                    float preferredDistance = GetPreferredDistance();
                    if (distance > preferredDistance)
                    {
                        Vector3 desired = player.transform.position - toPlayer.normalized * preferredDistance;
                        desired += SeparationVector(1.15f, player);
                        StepTowards(desired, 1f);
                    }
                    else if (distance < GetMinimumDistance())
                    {
                        Vector3 away = _animal.transform.position - player.transform.position;
                        away.y = 0f;
                        if (away.sqrMagnitude > 1e-6f)
                        {
                            Vector3 next = _animal.transform.position + away.normalized * Mathf.Min(0.22f, _speed * 0.08f);
                            if (!_instance.IsEnemyEntrancePositionBlocked(_dungeon, next))
                                _animal.transform.position = next;
                        }
                    }
                    toPlayer = player.transform.position - _animal.transform.position;
                    toPlayer.y = 0f;
                    if (toPlayer.sqrMagnitude > 1e-6f)
                        FaceEntityDirection(_animal, toPlayer);
                    if (distance <= AnimalPushRadius && Time.realtimeSinceStartup >= _nextPush && IsVisible(player))
                    {
                        _nextPush = Time.realtimeSinceStartup + AnimalPushCooldown;
                        Vector3 flatToPlayer = player.transform.position - _animal.transform.position;
                        flatToPlayer.y = 0f;
                        if (flatToPlayer.sqrMagnitude > 1e-6f)
                        {
                            Vector3 side = Vector3.Dot(player.transform.right, flatToPlayer.normalized) > 0f ? player.transform.right : -player.transform.right;
                            Vector3 force = new Vector3(10f, 3f, 10f);
                            player.DoPush(player.transform.forward * force.z + side * force.x + Vector3.up * force.y);
                        }
                    }
                }
                else
                {
                    float homeGap = Vector3.Distance(_animal.transform.position, _home);
                    if (homeGap > 0.75f)
                        StepTowards(_home, 0.75f);
                }
                if (_maxDistanceFromHome > 0f)
                {
                    Vector3 fromHome = _animal.transform.position - _home;
                    fromHome.y = 0f;
                    if (fromHome.magnitude > _maxDistanceFromHome)
                    {
                        _instance.TryReturnDungeonEnemyHome(_animal, _dungeon, _home, _animal.transform.rotation);
                    }
                }
                ClampInsideDungeon();
                SnapToGround();
                if (!_hasNetworkTransform || _animal.transform.position != _lastNetworkPosition || _animal.transform.rotation != _lastNetworkRotation)
                {
                    _hasNetworkTransform = true;
                    _lastNetworkPosition = _animal.transform.position;
                    _lastNetworkRotation = _animal.transform.rotation;
                    _animal.SendNetworkUpdate();
                }
            }

            private float GetPreferredDistance()
            {
                if (DungeonEvents.IsGen2Animal(_animal))
                    return Mathf.Clamp(_attackRange - 0.08f, 0.9f, 3.0f);
                return Mathf.Clamp(_attackRange - 0.05f, 0.85f, 2.25f);
            }

            private float GetMinimumDistance()
            {
                if (DungeonEvents.IsGen2Animal(_animal))
                    return Mathf.Clamp(_attackRange * 0.60f, 0.55f, 1.85f);
                return Mathf.Clamp(_attackRange * 0.65f, 0.60f, 1.5f);
            }

            private Vector3 SeparationVector(float radius, BasePlayer player)
            {
                int hits = Physics.OverlapSphereNonAlloc(_animal.transform.position, radius, _nearbyBuffer, ~0, QueryTriggerInteraction.Ignore);
                Vector3 push = Vector3.zero;
                int count = 0;
                for (int i = 0; i < hits; i++)
                {
                    var col = _nearbyBuffer[i];
                    if (col == null)
                        continue;
                    var other = col.GetComponentInParent<BaseCombatEntity>();
                    if (other == null || other == _animal || other == player)
                        continue;
                    if (other.IsDestroyed)
                        continue;
                    if (!_instance.IsDungeonEntity(other))
                        continue;
                    if (!_instance.IsAnimalEntity(other))
                        continue;
                    Vector3 away = _animal.transform.position - other.transform.position;
                    away.y = 0f;
                    float d = away.magnitude;
                    if (d < 0.001f || d > radius)
                        continue;
                    float t = 1f - d / radius;
                    push += away / (d + 0.001f) * t;
                    count++;
                }
                if (count > 0)
                    push /= count;
                return push.normalized * 0.9f;
            }

            private BasePlayer GetClosestDungeonPlayer(bool withLos, float radius)
            {
                if (_dungeon == null || _dungeon.PlayersInside.Count == 0)
                    return null;
                int hits = Physics.OverlapSphereNonAlloc(_animal.transform.position, radius, _playersBuffer, Layers.Mask.Player_Server, QueryTriggerInteraction.Ignore);
                BasePlayer best = null;
                float bestDist = float.MaxValue;
                for (int i = 0; i < hits; i++)
                {
                    var p = _playersBuffer[i]?.GetComponent<BasePlayer>();
                    if (p == null || !p.userID.IsSteamId() || p.IsNpc || p.IsSleeping() || p.IsDead())
                        continue;
                    if (_dungeon == null || !_dungeon.PlayersInside.Contains(p.userID))
                        continue;
                    if (_instance.IsInDungeonEntranceSafeZone(_dungeon, p.transform.position))
                        continue;
                    if (withLos && !IsVisible(p))
                        continue;
                    float d = Vector3.Distance(_animal.transform.position, p.transform.position);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = p;
                    }
                }
                return best;
            }

            private bool IsVisible(BasePlayer player)
            {
                Vector3 a = _animal.transform.position + Vector3.up * 1.2f;
                Vector3 b = player.transform.position + Vector3.up * 1.0f;
                return !Physics.Linecast(a, b, _blockMask, QueryTriggerInteraction.Ignore);
            }

            private void StepTowards(Vector3 goal, float speedMultiplier)
            {
                Vector3 here = _animal.transform.position;
                Vector3 flatGoal = new Vector3(goal.x, here.y, goal.z);
                Vector3 dir = flatGoal - here;
                if (dir.sqrMagnitude < 0.0001f)
                    return;
                dir.y = 0f;
                dir.Normalize();
                float moveDist = _speed * 0.2f * Mathf.Clamp(speedMultiplier, 0.1f, 1f);
                if (Physics.CapsuleCast(here + Vector3.up * 0.5f, here + Vector3.up * 1.6f, 0.28f, dir, out RaycastHit hit, moveDist, _blockMask, QueryTriggerInteraction.Ignore))
                {
                    Vector3 slideDir = Vector3.ProjectOnPlane(dir, hit.normal);
                    if (slideDir.sqrMagnitude < 1e-6f)
                        slideDir = Vector3.Cross(Vector3.up, dir);
                    if (slideDir.sqrMagnitude < 1e-6f)
                        return;
                    slideDir.Normalize();
                    Vector3 slide = slideDir * (moveDist * 0.5f);
                    if (
                        !Physics.CapsuleCast(here + Vector3.up * 0.5f, here + Vector3.up * 1.6f, 0.28f, slideDir, out _, slide.magnitude, _blockMask, QueryTriggerInteraction.Ignore)
                        && !_instance.IsEnemyEntrancePositionBlocked(_dungeon, here + slide)
                        && _instance.TryGetDungeonFloorPoint(_dungeon, here + slide, out _)
                    )
                    {
                        _animal.transform.position = here + slide;
                        FaceEntityDirection(_animal, slideDir);
                    }
                    return;
                }
                Vector3 next = here + dir * moveDist;
                if (_instance.IsEnemyEntrancePositionBlocked(_dungeon, next) || !_instance.TryGetDungeonFloorPoint(_dungeon, next, out _))
                    return;
                _animal.transform.position = next;
                FaceEntityDirection(_animal, dir);
            }

            private void ClampInsideDungeon()
            {
                if (_dungeon == null || _animal == null || _animal.IsDestroyed)
                    return;
                if (!_dungeon.DungeonBounds.Contains(_animal.transform.position) || !_instance.TryGetDungeonFloorPoint(_dungeon, _animal.transform.position, out _))
                    _instance.TryReturnDungeonEnemyHome(_animal, _dungeon, _home, _animal.transform.rotation);
            }

            private void SnapToGround()
            {
                if (_animal == null || _animal.IsDestroyed)
                    return;
                if (TryGetClosestDungeonFloorY(_animal.transform.position, out float floorY))
                {
                    var pos = _animal.transform.position;
                    _animal.transform.position = new Vector3(pos.x, floorY, pos.z);
                    return;
                }
                _instance.TryReturnDungeonEnemyHome(_animal, _dungeon, _home, _animal.transform.rotation);
            }

            private bool TryGetClosestDungeonFloorY(Vector3 position, out float floorY)
            {
                floorY = 0f;
                if (_dungeon == null || _instance == null || !_instance.TryGetDungeonFloorPoint(_dungeon, position, out Vector3 floor))
                    return false;
                floorY = floor.y + 0.02f;
                return true;
            }
        }

        public class DungeonAnimalAttack : MonoBehaviour
        {
            private ActiveDungeon _dungeon;
            private BaseCombatEntity _animal;
            private BasePlayer _pendingTarget;
            private float _damage;
            private float _attackRange;
            private float _attackCooldown;
            private float _nextAttack;
            private int _blockMask;
            private readonly Collider[] _playersBuffer = new Collider[64];

            public void Init(ActiveDungeon dungeon, BaseCombatEntity animal, float damage, float attackRange, float attackCooldown)
            {
                _dungeon = dungeon;
                _animal = animal;
                _damage = Mathf.Max(0f, damage);
                _attackRange = Mathf.Clamp(attackRange, 0.5f, 5f);
                _attackCooldown = Mathf.Clamp(attackCooldown, 0.2f, 10f);
                _blockMask = Layers.Mask.Construction | Layers.Mask.Deployed | Layers.Mask.World | Layers.Mask.Terrain | Layers.Mask.Default;
                InvokeRepeating(nameof(Tick), Random.Range(0.20f, 0.45f), 0.25f);
            }

            private void OnDestroy()
            {
                _pendingTarget = null;
                CancelInvoke();
            }

            private void Tick()
            {
                if (_animal == null || _animal.IsDestroyed || _dungeon == null || _instance == null)
                {
                    Destroy(this);
                    return;
                }
                if (_animal.IsDead() || _animal.Health() <= 0.01f)
                {
                    Destroy(this);
                    return;
                }
                if (DungeonEvents.IsGen2Animal(_animal))
                {
                    Destroy(this);
                    return;
                }
                if (_damage <= 0f)
                    return;
                if (Time.realtimeSinceStartup < _nextAttack)
                    return;
                var target = GetClosestTarget();
                if (target == null)
                    return;
                FaceTarget(target);
                if (!IsValidTargetForDamage(target))
                    return;
                _pendingTarget = target;
                _nextAttack = Time.realtimeSinceStartup + _attackCooldown;
                _animal.SignalBroadcast(BaseEntity.Signal.Attack);
                _animal.SendNetworkUpdate();
                Invoke(nameof(ApplyPendingDamage), 0.18f);
            }

            private void ApplyPendingDamage()
            {
                if (_animal == null || _animal.IsDestroyed || _dungeon == null || _instance == null)
                    return;
                var target = _pendingTarget;
                _pendingTarget = null;
                if (target == null)
                    return;
                FaceTarget(target);
                if (!IsValidTargetForDamage(target))
                    return;
                target.Hurt(_damage, DamageType.Bite, _animal);
            }

            private BasePlayer GetClosestTarget()
            {
                if (_dungeon == null || _dungeon.PlayersInside.Count == 0)
                    return null;
                int hits = Physics.OverlapSphereNonAlloc(_animal.transform.position, _attackRange + 0.35f, _playersBuffer, Layers.Mask.Player_Server, QueryTriggerInteraction.Ignore);
                BasePlayer best = null;
                float bestDist = float.MaxValue;
                for (int i = 0; i < hits; i++)
                {
                    var player = _playersBuffer[i]?.GetComponent<BasePlayer>();
                    if (player == null || !player.userID.IsSteamId() || player.IsNpc || player.IsSleeping() || player.IsDead())
                        continue;
                    if (!_dungeon.PlayersInside.Contains(player.userID))
                        continue;
                    if (!IsValidTargetForDamage(player))
                        continue;
                    float dist = GetFlatDistance(GetAttackOrigin(), player.transform.position);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = player;
                    }
                }
                return best;
            }

            private bool IsValidTargetForDamage(BasePlayer player)
            {
                if (player == null || player.IsDead() || player.IsSleeping())
                    return false;
                if (_dungeon == null || !_dungeon.PlayersInside.Contains(player.userID))
                    return false;
                if (_instance != null && _instance.IsInDungeonEntranceSafeZone(_dungeon, player.transform.position))
                    return false;
                if (Mathf.Abs(player.transform.position.y - _animal.transform.position.y) > 2.2f)
                    return false;
                if (GetFlatDistance(GetAttackOrigin(), player.transform.position) > _attackRange + 0.08f)
                    return false;
                if (!IsFacingTarget(player, 95f))
                    return false;
                if (!IsVisible(player))
                    return false;
                return true;
            }

            private Vector3 GetAttackOrigin()
            {
                var animalNpc = _animal as BaseAnimalNPC;
                if (animalNpc != null)
                    return animalNpc.AttackPosition;
                return _animal.transform.position;
            }

            private float GetFlatDistance(Vector3 from, Vector3 to)
            {
                from.y = 0f;
                to.y = 0f;
                return Vector3.Distance(from, to);
            }

            private void FaceTarget(BasePlayer player)
            {
                if (player == null || _animal == null)
                    return;
                Vector3 direction = player.transform.position - _animal.transform.position;
                direction.y = 0f;
                if (direction.sqrMagnitude <= 1e-6f)
                    return;
                FaceEntityDirection(_animal, direction);
            }

            private bool IsFacingTarget(BasePlayer player, float maxAngle)
            {
                if (player == null || _animal == null)
                    return false;
                Vector3 direction = player.transform.position - _animal.transform.position;
                direction.y = 0f;
                if (direction.sqrMagnitude <= 1e-6f)
                    return true;
                return Vector3.Angle(_animal.transform.forward, direction.normalized) <= maxAngle;
            }

            private bool IsVisible(BasePlayer player)
            {
                Vector3 a = _animal.CenterPoint();
                Vector3 b = player.eyes?.position ?? player.CenterPoint();
                return !Physics.Linecast(a, b, _blockMask, QueryTriggerInteraction.Ignore);
            }
        }

        public class CorpseLootData : MonoBehaviour
        {
            public bool IsBoss;
            public NpcConfig LootConfig;
        }

        public class NpcMovement : MonoBehaviour
        {
            private const float WaypointArrivalTol = 0.5f;
            private const float MaxStepHeight = 1.2f;
            private const int MaxWaypointHistory = 99;
            private const int WaypointsCapacity = MaxWaypointHistory + 4;
            private const float HeadHeight = 1.2f;
            private const float EPS = 1e-6f;
            private const float PlayerRescanInterval = 0.50f;
            private const float CombatMemorySeconds = 8f;
            private const float LostSightRepositionIntervalMin = 0.75f;
            private const float LostSightRepositionIntervalMax = 1.55f;
            private const float HitReactionCooldown = 0.55f;
            private const float StrafeArrivalDistance = 0.23f;
            private const float StrafeMinDistance = 1.3f;
            private const float StrafeMaxDistance = 2.6f;
            private readonly Collider[] _playerScanBuf = new Collider[256];
            private readonly Collider[] _nearby = new Collider[64];
            private readonly Queue<Vector3> _wayPoints = new Queue<Vector3>(WaypointsCapacity);
            private float _separationRadius = 1.05f;
            private float _separationWeight = 0.9f;
            private BaseAIBrain _npcBrain;
            private NPCPlayer _npc;
            private BaseEntity _mainTarget;
            private BasePlayer _combatTarget;
            private Vector3 _initialPosition;
            private Vector3 _moveTarget;
            private Vector3 _lastKnownTargetPosition;
            private int _blockMask;
            private bool _contact;
            private bool _targetIsVisible;
            private float _npcMoveSpeed;
            private float _range;
            private float _attackDistance = 1f;
            private float _maxDistanceFromHome = 15f;
            private float _lastPlayerScanTime = -999f;
            private float _lastAllyDamageReactionTime = -999f;
            private float _lastKnownTargetTime = -999f;
            private float _lastDamageReactionTime = -999f;
            private float _nextRepositionTime;
            private BasePlayer _cachedPlayer;
            private double _nextFireTime;
            private Vector3 _lastMovementSamplePosition;
            private float _lastMovementProgressTime;
            private float _nextStuckCheckTime;
            private float _stationaryFireStartedAt = -1f;
            private bool _isCombatStrafing;
            private Vector3 _combatStrafeTarget;
            private float _combatStrafeSpeed;
            private float _combatStrafeDeadline;
            private float _combatStrafeLastProgressTime;
            private Vector3 _combatStrafeLastPosition;

            private void Awake()
            {
                _npc = GetComponent<NPCPlayer>();
                if (_npc == null)
                {
                    Destroy(this);
                    return;
                }
                _npcBrain = GetComponent<BaseAIBrain>();
                _attackDistance = GetAttackDistance();
                _npcMoveSpeed = 0.9f;
                _blockMask = Layers.Mask.Construction | Layers.Mask.Deployed | Layers.Mask.World | Layers.Mask.Terrain | Layers.Mask.Default;
                _initialPosition = _npc.transform.position;
                _moveTarget = _npc.transform.position;
                _lastKnownTargetPosition = _npc.transform.position;
                _mainTarget = _npc;
                _contact = false;
                _targetIsVisible = false;
                _range = WaypointArrivalTol;
                float configuredMaxDistance = Mathf.Max(0f, _instance?._config?.MaxNpcDistanceFromSpawnPoint ?? 30f);
                _maxDistanceFromHome = configuredMaxDistance <= 0f ? 0f : Mathf.Max(20f, configuredMaxDistance);
                try
                {
                    _npc.CancelInvoke(_npc.TickMovement);
                }
                catch { }
                try
                {
                    _npc.CancelInvoke(_npc.ServerThink_Internal);
                }
                catch { }
                _nextFireTime = 0;
                _lastMovementSamplePosition = _npc.transform.position;
                _lastMovementProgressTime = Time.realtimeSinceStartup;
                _nextStuckCheckTime = Time.realtimeSinceStartup + 1f;
                InvokeRepeating(nameof(UpdateAi), Random.Range(0.20f, 0.55f), 0.40f);
                InvokeRepeating(nameof(UpdateMovement), Random.Range(0.15f, 0.40f), 0.25f);
                InvokeRepeating(nameof(UpdateFire), Random.Range(0.15f, 0.35f), NpcFireTickInterval);
            }

            public void OnDamagedByPlayer(BasePlayer attacker)
            {
                RegisterCombatStimulus(attacker, attacker != null ? attacker.transform.position : Vector3.zero, true);
            }

            public void OnAllyDamagedByPlayer(BasePlayer attacker, Vector3 sourcePosition)
            {
                if (_npc == null || attacker == null)
                    return;
                if ((_npc.transform.position - sourcePosition).sqrMagnitude > 18f * 18f)
                    return;
                float now = Time.realtimeSinceStartup;
                if (_combatTarget == attacker && now - _lastAllyDamageReactionTime < HitReactionCooldown)
                {
                    if (!IsValidPlayer(attacker, true))
                        return;
                    _lastKnownTargetPosition = attacker.transform.position;
                    _lastKnownTargetTime = now;
                    return;
                }
                _lastAllyDamageReactionTime = now;
                RegisterCombatStimulus(attacker, sourcePosition, false);
            }

            private void RegisterCombatStimulus(BasePlayer attacker, Vector3 sourcePosition, bool forceReposition)
            {
                if (!IsValidPlayer(attacker, true))
                    return;
                float now = Time.realtimeSinceStartup;
                if (forceReposition && now - _lastDamageReactionTime < HitReactionCooldown)
                    return;
                _lastDamageReactionTime = now;
                _combatTarget = attacker;
                _cachedPlayer = attacker;
                _lastPlayerScanTime = now - PlayerRescanInterval;
                _mainTarget = attacker;
                _contact = true;
                _lastKnownTargetPosition = attacker.transform.position;
                _lastKnownTargetTime = now;
                _targetIsVisible = HasLineOfSight(attacker);
                _range = _targetIsVisible ? 5f : WaypointArrivalTol;
                if (!_targetIsVisible || forceReposition)
                    SelectRepositionMoveTarget(attacker, true);
            }

            private float GetAttackDistance()
            {
                float weaponDistance = GetWeaponAttackDistance();
                float configuredAttackDistance = Mathf.Max(0f, _instance?._config?.NpcAttackDistanceOverride ?? 0f);
                if (configuredAttackDistance > 0f)
                    return Mathf.Max(configuredAttackDistance, weaponDistance);
                return weaponDistance;
            }

            private float GetWeaponAttackDistance()
            {
                string shortName = null;
                var active = _npc?.GetActiveItem();
                if (active != null)
                    shortName = active.info?.shortname;
                if (string.IsNullOrEmpty(shortName))
                {
                    var belt = _npc?.inventory?.containerBelt;
                    if (belt?.itemList != null)
                    {
                        for (int i = 0; i < belt.itemList.Count; i++)
                        {
                            var it = belt.itemList[i];
                            if (it?.info == null)
                                continue;
                            shortName = it.info.shortname;
                            if (!string.IsNullOrEmpty(shortName))
                                break;
                        }
                    }
                }
                if (_instance != null && _instance.IsMeleeWeapon(shortName))
                    return 1.75f;
                if (string.IsNullOrEmpty(shortName))
                    return 14f;
                if (shortName.Contains("shotgun"))
                    return 8f;
                if (shortName.Contains("pistol") || shortName.Contains("revolver"))
                    return 12f;
                if (shortName.Contains("smg"))
                    return 14f;
                if (shortName.Contains("rifle") || shortName.Contains("lmg") || shortName.Contains("m249"))
                    return 22f;
                return 14f;
            }

            private BasePlayer GetBestTarget()
            {
                if (IsValidPlayer(_combatTarget, true) && HasRecentTargetMemory())
                    return _combatTarget;
                return GetFirstPlayer();
            }

            private BasePlayer GetFirstPlayer()
            {
                if (Time.realtimeSinceStartup - _lastPlayerScanTime < PlayerRescanInterval)
                {
                    if (_cachedPlayer == null)
                        return null;
                    if (IsValidPlayer(_cachedPlayer, false))
                        return _cachedPlayer;
                }
                float sense = _npcBrain != null ? _npcBrain.SenseRange : 30f;
                sense = Mathf.Max(sense, _attackDistance * 1.5f, 30f);
                int hitCount = Physics.OverlapSphereNonAlloc(_npc.transform.position, sense, _playerScanBuf, Layers.Mask.Player_Server, QueryTriggerInteraction.Ignore);
                BasePlayer closest = null;
                float bestSqr = float.MaxValue;
                for (int i = 0; i < hitCount; i++)
                {
                    var p = _playerScanBuf[i]?.GetComponent<BasePlayer>();
                    if (!IsValidPlayer(p, false))
                        continue;
                    float sq = (_npc.transform.position - p.transform.position).sqrMagnitude;
                    if (sq < bestSqr)
                    {
                        bestSqr = sq;
                        closest = p;
                    }
                }
                _cachedPlayer = closest;
                _lastPlayerScanTime = Time.realtimeSinceStartup;
                return closest;
            }

            private bool IsValidPlayer(BasePlayer player, bool allowCombatMemoryDistance)
            {
                if (player == null || player.IsDestroyed || player.IsNpc || player.IsSleeping() || player.IsDead())
                    return false;
                if (_instance != null)
                {
                    var dungeon = _instance.GetActiveDungeonByEntity(_npc);
                    if (dungeon != null && dungeon.PlayersInside != null && dungeon.PlayersInside.Count > 0 && !dungeon.PlayersInside.Contains(player.userID))
                        return false;
                    if (_instance.IsInDungeonEntranceSafeZone(dungeon, player.transform.position))
                        return false;
                }
                if (Mathf.Abs(player.transform.position.y - _npc.transform.position.y) > 4.5f)
                    return false;
                if (_maxDistanceFromHome > 0f)
                {
                    float allowed = _maxDistanceFromHome + (allowCombatMemoryDistance ? 8f : 2f);
                    Vector3 flat = player.transform.position - _initialPosition;
                    flat.y = 0f;
                    if (flat.sqrMagnitude > allowed * allowed)
                        return false;
                }
                return true;
            }

            private void UpdateAi()
            {
                if (_npc == null)
                    return;
                if (_instance != null)
                {
                    var dungeon = _instance.GetActiveDungeonByEntity(_npc);
                    if (dungeon != null && (_instance.IsInDungeonEntranceCorridor(dungeon, _npc.transform.position) || !_instance.TryGetDungeonFloorPoint(dungeon, _npc.transform.position, out _)))
                    {
                        if (_instance.TryReturnDungeonEnemyHome(_npc, dungeon, _initialPosition, _npc.transform.rotation))
                            _initialPosition = _npc.transform.position;
                        ResetCombatAndReturnHome();
                        return;
                    }
                }
                var player = GetBestTarget();
                if (player == null)
                {
                    if (!HasRecentTargetMemory())
                        ResetCombatAndReturnHome();
                    return;
                }
                Vector3 playerPos = player.transform.position;
                bool visible = HasLineOfSight(player);
                _mainTarget = player;
                _combatTarget = player;
                if (visible)
                {
                    RefreshTargetMemory(player);
                    _contact = true;
                    _targetIsVisible = true;
                    _range = 5f;
                    _moveTarget = playerPos;
                    return;
                }
                _targetIsVisible = false;
                StopCombatStrafe();
                if (HasRecentTargetMemory())
                {
                    _contact = true;
                    _range = WaypointArrivalTol;
                    if (Time.realtimeSinceStartup >= _nextRepositionTime || (_npc.transform.position - _moveTarget).sqrMagnitude < 0.75f * 0.75f)
                        SelectRepositionMoveTarget(player, false);
                    return;
                }
                ResetCombatAndReturnHome();
            }

            private void RefreshTargetMemory(BasePlayer player)
            {
                if (player == null)
                    return;
                _lastKnownTargetPosition = player.transform.position;
                _lastKnownTargetTime = Time.realtimeSinceStartup;
            }

            private bool HasRecentTargetMemory()
            {
                return Time.realtimeSinceStartup - _lastKnownTargetTime <= CombatMemorySeconds;
            }

            private void ResetCombatAndReturnHome()
            {
                StopCombatStrafe();
                _mainTarget = _npc;
                _combatTarget = null;
                _contact = false;
                _targetIsVisible = false;
                _range = WaypointArrivalTol;
                _wayPoints.Clear();
                _moveTarget = _initialPosition;
            }

            private bool HasLineOfSight(BasePlayer player)
            {
                if (player == null || _npc == null)
                    return false;
                Vector3 eyeNpc = _npc.transform.position + Vector3.up * HeadHeight;
                Vector3 eyePlayer = player.eyes?.position ?? player.transform.position + Vector3.up * 1.0f;
                return !Physics.Linecast(eyeNpc, eyePlayer, _blockMask, QueryTriggerInteraction.Ignore);
            }

            private bool HasLineOfSightFrom(Vector3 from, BasePlayer player)
            {
                if (player == null)
                    return false;
                Vector3 eyePlayer = player.eyes?.position ?? player.transform.position + Vector3.up * 1.0f;
                return !Physics.Linecast(from + Vector3.up * HeadHeight, eyePlayer, _blockMask, QueryTriggerInteraction.Ignore);
            }

            private void SelectRepositionMoveTarget(BasePlayer player, bool immediate)
            {
                if (_npc == null)
                    return;
                Vector3 candidate;
                if (TryFindRepositionPoint(player, out candidate))
                    SetMoveTarget(candidate, WaypointArrivalTol);
                else
                    SetMoveTarget(_lastKnownTargetPosition, WaypointArrivalTol);
                _nextRepositionTime = Time.realtimeSinceStartup + Random.Range(LostSightRepositionIntervalMin, LostSightRepositionIntervalMax);
                if (immediate)
                    _nextRepositionTime = Mathf.Min(_nextRepositionTime, Time.realtimeSinceStartup + 0.35f);
            }

            private void SetMoveTarget(Vector3 target, float range)
            {
                _moveTarget = target;
                _range = Mathf.Max(0.25f, range);
                _wayPoints.Clear();
                _wayPoints.Enqueue(target);
            }

            private bool TryFindRepositionPoint(BasePlayer player, out Vector3 target)
            {
                target = _lastKnownTargetPosition;
                if (player == null || _npc == null)
                    return false;
                Vector3 here = _npc.transform.position;
                Vector3 playerPos = player.transform.position;
                Vector3 fromPlayerToNpc = here - playerPos;
                fromPlayerToNpc.y = 0f;
                if (fromPlayerToNpc.sqrMagnitude < EPS)
                    fromPlayerToNpc = _npc.transform.forward;
                fromPlayerToNpc.Normalize();
                Vector3 side = Vector3.Cross(Vector3.up, fromPlayerToNpc).normalized;
                Vector3 toPlayer = -fromPlayerToNpc;
                var candidates = Pool.Get<List<Vector3>>();
                try
                {
                    candidates.Add(playerPos + fromPlayerToNpc * 1.6f);
                    candidates.Add(playerPos + fromPlayerToNpc * 2.4f);
                    candidates.Add(playerPos + side * 1.4f);
                    candidates.Add(playerPos - side * 1.4f);
                    candidates.Add(playerPos + side * 2.4f + fromPlayerToNpc * 0.8f);
                    candidates.Add(playerPos - side * 2.4f + fromPlayerToNpc * 0.8f);
                    candidates.Add(here + side * 1.15f + toPlayer * 0.35f);
                    candidates.Add(here - side * 1.15f + toPlayer * 0.35f);
                    candidates.Add(here + side * 1.8f);
                    candidates.Add(here - side * 1.8f);
                    float bestScore = float.MaxValue;
                    bool found = false;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        Vector3 c = candidates[i];
                        if (!TryProjectCandidate(c, out var grounded))
                            continue;
                        if (!IsWithinHomeLimit(grounded))
                            continue;
                        if (WouldOverlapNpcs(grounded, 0.5f))
                            continue;
                        if (IsBlockedAt(grounded))
                            continue;
                        float score = Vector3.Distance(here, grounded) * 0.35f + Vector3.Distance(playerPos, grounded) * 0.65f;
                        if (HasLineOfSightFrom(grounded, player))
                            score -= 8f;
                        if (!IsDirectMoveLikelyClear(grounded))
                            score += 2.5f;
                        if (score >= bestScore)
                            continue;
                        bestScore = score;
                        target = grounded;
                        found = true;
                    }
                    return found;
                }
                finally
                {
                    Pool.FreeUnmanaged(ref candidates);
                }
            }

            private bool TryProjectCandidate(Vector3 candidate, out Vector3 grounded)
            {
                grounded = new Vector3(candidate.x, _npc.transform.position.y, candidate.z);
                if (!TryGetGroundPosition(grounded, out var g))
                    return false;
                grounded = g;
                var dungeon = _instance?.GetActiveDungeonByEntity(_npc);
                return _instance == null || !_instance.IsEnemyEntrancePositionBlocked(dungeon, grounded);
            }

            private bool IsWithinHomeLimit(Vector3 position)
            {
                var dungeon = _instance?.GetActiveDungeonByEntity(_npc);
                if (dungeon != null && (_instance.IsEnemyEntrancePositionBlocked(dungeon, position) || !_instance.TryGetDungeonFloorPoint(dungeon, position, out _)))
                    return false;
                if (_maxDistanceFromHome <= 0f)
                    return true;
                Vector3 flat = position - _initialPosition;
                flat.y = 0f;
                return flat.sqrMagnitude <= _maxDistanceFromHome * _maxDistanceFromHome;
            }

            private bool IsDirectMoveLikelyClear(Vector3 target)
            {
                Vector3 here = _npc.transform.position;
                Vector3 dir = target - here;
                dir.y = 0f;
                float dist = dir.magnitude;
                if (dist < 0.05f)
                    return true;
                dir /= dist;
                Vector3 a = here + Vector3.up * 0.5f;
                Vector3 b = here + Vector3.up * 1.6f;
                return !Physics.CapsuleCast(a, b, 0.28f, dir, out _, Mathf.Min(dist, 3f), _blockMask, QueryTriggerInteraction.Ignore);
            }

            private bool IsBlockedAt(Vector3 position)
            {
                Vector3 a = position + Vector3.up * 0.45f;
                Vector3 b = position + Vector3.up * 1.55f;
                return Physics.CheckCapsule(a, b, 0.25f, _blockMask, QueryTriggerInteraction.Ignore);
            }

            private bool TryStartCombatStrafe(BasePlayer player)
            {
                if (_npc == null || player == null || !_contact || !_targetIsVisible || _isCombatStrafing)
                    return false;
                Vector3 here = _npc.transform.position;
                Vector3 toPlayer = player.transform.position - here;
                toPlayer.y = 0f;
                if (toPlayer.sqrMagnitude <= EPS)
                    return false;
                toPlayer.Normalize();
                Vector3 right = Vector3.Cross(Vector3.up, toPlayer).normalized;
                float firstSide = Random.value < 0.5f ? -1f : 1f;
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    float side = attempt % 2 == 0 ? firstSide : -firstSide;
                    float distance = Random.Range(StrafeMinDistance, StrafeMaxDistance);
                    Vector3 candidate = here + right * side * distance + toPlayer * Random.Range(-0.35f, 0.40f);
                    if (!TryGetGroundPosition(candidate, out Vector3 grounded))
                        continue;
                    if (!IsWithinHomeLimit(grounded) || IsBlockedAt(grounded) || WouldOverlapNpcs(grounded, 0.55f))
                        continue;
                    if ((grounded - player.transform.position).sqrMagnitude < 1.3f * 1.3f)
                        continue;
                    if (!IsDirectMoveLikelyClear(grounded))
                        continue;
                    _combatStrafeTarget = grounded;
                    _combatStrafeSpeed = Random.Range(0.45f, 0.62f);
                    _combatStrafeDeadline = Time.realtimeSinceStartup + Mathf.Clamp(distance / (_npcMoveSpeed * _combatStrafeSpeed * 4f) + 1.0f, 1.5f, 3.5f);
                    _combatStrafeLastProgressTime = Time.realtimeSinceStartup;
                    _combatStrafeLastPosition = here;
                    _isCombatStrafing = true;
                    return true;
                }
                return false;
            }

            private void StopCombatStrafe()
            {
                _isCombatStrafing = false;
                _stationaryFireStartedAt = -1f;
                if (_npc == null)
                    return;
                _lastMovementSamplePosition = _npc.transform.position;
                _lastMovementProgressTime = Time.realtimeSinceStartup;
                _nextStuckCheckTime = Time.realtimeSinceStartup + 1f;
            }

            private void UpdateCombatStrafe()
            {
                float now = Time.realtimeSinceStartup;
                Vector3 here = _npc.transform.position;
                Vector3 delta = _combatStrafeTarget - here;
                delta.y = 0f;
                float distance = delta.magnitude;
                if (distance <= StrafeArrivalDistance || now >= _combatStrafeDeadline || now - _combatStrafeLastProgressTime > 0.85f)
                {
                    StopCombatStrafe();
                    return;
                }
                Quaternion previousRotation = _npc.transform.rotation;
                AdvanceWithObstacleHandling(delta / distance, Mathf.Min(distance, _npcMoveSpeed * _combatStrafeSpeed));
                Vector3 moved = _npc.transform.position - here;
                moved.y = 0f;
                if (moved.sqrMagnitude > 0.035f * 0.035f)
                {
                    _npc.transform.rotation = Quaternion.Slerp(previousRotation, _npc.transform.rotation, 0.55f);
                    _npc.SendNetworkUpdate();
                    if ((_npc.transform.position - _combatStrafeLastPosition).sqrMagnitude > 0.12f * 0.12f)
                    {
                        _combatStrafeLastPosition = _npc.transform.position;
                        _combatStrafeLastProgressTime = now;
                    }
                }
            }

            private void UpdateMovement()
            {
                if (_npc == null)
                    return;
                if (_maxDistanceFromHome > 0f)
                {
                    Vector3 flatFromHome = _npc.transform.position - _initialPosition;
                    flatFromHome.y = 0f;
                    float maxDistanceSqr = _maxDistanceFromHome * _maxDistanceFromHome;
                    if (flatFromHome.sqrMagnitude > maxDistanceSqr)
                    {
                        _contact = false;
                        _targetIsVisible = false;
                        _mainTarget = _npc;
                        _combatTarget = null;
                        _wayPoints.Clear();
                        _moveTarget = _initialPosition;
                    }
                }
                var player = _mainTarget as BasePlayer;
                if (player == null || !IsValidPlayer(player, true))
                    _contact = false;
                if (_contact && player != null && !_isCombatStrafing && TryRecoverIfStuck(player))
                    return;
                if (!_contact)
                {
                    StopCombatStrafe();
                    if ((_npc.transform.position - _initialPosition).sqrMagnitude > 0.01f)
                        MoveTowards(_initialPosition, 0.85f);
                    return;
                }
                if (_isCombatStrafing)
                {
                    if (!_targetIsVisible || Time.realtimeSinceStartup >= _combatStrafeDeadline)
                        StopCombatStrafe();
                    else
                    {
                        UpdateCombatStrafe();
                        return;
                    }
                }
                float stopToShootDistance = _attackDistance * 1.2f;
                float stopToShootDistanceSqr = stopToShootDistance * stopToShootDistance;
                float distToPlayerSqr = (_npc.transform.position - player.transform.position).sqrMagnitude;
                if (_targetIsVisible && distToPlayerSqr <= stopToShootDistanceSqr)
                {
                    FaceTarget(player);
                    return;
                }
                if (!_targetIsVisible && HasRecentTargetMemory() && (_moveTarget - _npc.transform.position).sqrMagnitude < 0.45f * 0.45f)
                    SelectRepositionMoveTarget(player, false);
                if (_wayPoints.Count >= WaypointsCapacity)
                    _wayPoints.Dequeue();
                if (_wayPoints.Count == 0)
                    _wayPoints.Enqueue(_moveTarget);
                float rangeSqr = _range * _range;
                float distToTargetSqr = (_npc.transform.position - _moveTarget).sqrMagnitude;
                if (distToTargetSqr < rangeSqr)
                {
                    if (_range < 0.6f && _wayPoints.Count > 0)
                        _wayPoints.Dequeue();
                    _moveTarget = _wayPoints.Count > 0 ? _wayPoints.Peek() : _npc.transform.position;
                }
                if (distToTargetSqr <= 0.04f)
                    return;
                Vector3 beforeMove = _npc.transform.position;
                MoveTowards(_moveTarget, _targetIsVisible ? 0.9f : 1f, player);
                if ((_npc.transform.position - beforeMove).sqrMagnitude > 0.05f * 0.05f)
                    _stationaryFireStartedAt = -1f;
            }

            private bool TryRecoverIfStuck(BasePlayer player)
            {
                float now = Time.realtimeSinceStartup;
                if (now < _nextStuckCheckTime)
                    return false;
                _nextStuckCheckTime = now + 1f;
                Vector3 current = _npc.transform.position;
                if (_targetIsVisible && player != null)
                {
                    Vector3 toPlayer = player.transform.position - current;
                    toPlayer.y = 0f;
                    float holdDistance = _attackDistance * 1.2f;
                    if (toPlayer.sqrMagnitude <= holdDistance * holdDistance)
                    {
                        _lastMovementSamplePosition = current;
                        _lastMovementProgressTime = now;
                        return false;
                    }
                }
                Vector3 moved = current - _lastMovementSamplePosition;
                moved.y = 0f;
                if (moved.sqrMagnitude >= 0.18f * 0.18f)
                {
                    _lastMovementSamplePosition = current;
                    _lastMovementProgressTime = now;
                    return false;
                }
                float targetDistanceSqr = (_moveTarget - current).sqrMagnitude;
                if (targetDistanceSqr <= 0.75f * 0.75f || now - _lastMovementProgressTime < 3f)
                    return false;
                if (!TryFindStuckRecoveryPoint(player, out var recovery))
                {
                    SelectRepositionMoveTarget(player, true);
                    _lastMovementProgressTime = now;
                    return false;
                }
                _npc.transform.position = recovery;
                _npc.UpdateNetworkGroup();
                _npc.SendNetworkUpdateImmediate();
                _moveTarget = player.transform.position;
                _wayPoints.Clear();
                _lastMovementSamplePosition = recovery;
                _lastMovementProgressTime = now;
                return true;
            }

            private bool TryFindStuckRecoveryPoint(BasePlayer player, out Vector3 recovery)
            {
                recovery = Vector3.zero;
                Vector3 current = _npc.transform.position;
                Vector3[] directions =
                {
                    Vector3.forward,
                    Vector3.back,
                    Vector3.left,
                    Vector3.right,
                    (Vector3.forward + Vector3.right).normalized,
                    (Vector3.forward + Vector3.left).normalized,
                    (Vector3.back + Vector3.right).normalized,
                    (Vector3.back + Vector3.left).normalized,
                };
                float bestScore = float.MaxValue;
                bool found = false;
                for (int radiusIndex = 0; radiusIndex < 2; radiusIndex++)
                {
                    float radius = radiusIndex == 0 ? 0.9f : 1.65f;
                    for (int i = 0; i < directions.Length; i++)
                    {
                        Vector3 candidate = current + directions[i] * radius;
                        if (!TryProjectCandidate(candidate, out var grounded))
                            continue;
                        if (!IsWithinHomeLimit(grounded) || IsBlockedAt(grounded) || WouldOverlapNpcs(grounded, 0.55f))
                            continue;
                        if (!IsDirectMoveLikelyClear(grounded))
                            continue;
                        float score = Vector3.Distance(grounded, player.transform.position);
                        if (HasLineOfSightFrom(grounded, player))
                            score -= 5f;
                        if (score >= bestScore)
                            continue;
                        bestScore = score;
                        recovery = grounded;
                        found = true;
                    }
                }
                if (found)
                    return true;
                if (TryProjectCandidate(_initialPosition, out var home) && !IsBlockedAt(home) && !WouldOverlapNpcs(home, 0.55f))
                {
                    recovery = home;
                    return true;
                }
                return false;
            }

            private void FaceTarget(BasePlayer player)
            {
                if (player == null || _npc == null)
                    return;
                Vector3 lookDir = player.transform.position - _npc.transform.position;
                lookDir.y = 0f;
                if (lookDir.sqrMagnitude > EPS)
                    _npc.transform.rotation = Quaternion.LookRotation(lookDir.normalized);
            }

            private void UpdateFire()
            {
                if (_npc == null || _isCombatStrafing)
                    return;
                var player = _mainTarget as BasePlayer;
                if (player == null)
                    return;
                if (!_contact || !_targetIsVisible)
                    return;
                if (!HasLineOfSight(player))
                {
                    _targetIsVisible = false;
                    StopCombatStrafe();
                    SelectRepositionMoveTarget(player, false);
                    return;
                }
                float shootDistance = _attackDistance * 1.2f;
                float shootDistanceSqr = shootDistance * shootDistance;
                if ((_npc.transform.position - player.transform.position).sqrMagnitude > shootDistanceSqr)
                    return;
                var attackEntity = _npc.GetHeldEntity() as AttackEntity;
                var heldProjectile = attackEntity as BaseProjectile;
                if (attackEntity == null)
                    return;
                double now = Time.timeAsDouble;
                if (now < _nextFireTime)
                    return;
                Vector3 npcEye = _npc.transform.position + Vector3.up * HeadHeight;
                Vector3 playerAim = player.transform.position + Vector3.up * 1.0f;
                Vector3 aimDir = playerAim - npcEye;
                if (aimDir.sqrMagnitude > 0.0001f)
                    aimDir.Normalize();
                try
                {
                    _npc.SetAimDirection(aimDir);
                }
                catch { }
                FaceTarget(player);
                if (heldProjectile != null)
                {
                    if (heldProjectile.primaryMagazine != null && heldProjectile.primaryMagazine.contents <= 0)
                    {
                        try
                        {
                            heldProjectile.ServerReload();
                        }
                        catch { }
                        _nextFireTime = now + 0.15;
                        return;
                    }
                }
                bool fired = false;
                try
                {
                    attackEntity.ServerUse(new HeldEntityServerUseParams(_npc.damageScale));
                    fired = true;
                }
                catch { }
                float delay = attackEntity.repeatDelay;
                if (delay < 0.05f)
                    delay = 0.05f;
                _nextFireTime = now + delay;
                if (!fired || heldProjectile == null)
                    return;
                float secondsBeforeMoving = _instance?._config?.NpcSecondsShootingBeforeLateralMove ?? 0f;
                if (secondsBeforeMoving <= 0f)
                {
                    _stationaryFireStartedAt = -1f;
                    return;
                }
                float time = Time.realtimeSinceStartup;
                if (_stationaryFireStartedAt < 0f)
                    _stationaryFireStartedAt = time;
                else if (time - _stationaryFireStartedAt >= Mathf.Max(0.1f, secondsBeforeMoving))
                {
                    TryStartCombatStrafe(player);
                    _stationaryFireStartedAt = -1f;
                }
            }

            private void MoveTowards(Vector3 target, float speedMultiplier = 1f, BasePlayer player = null)
            {
                Vector3 here = _npc.transform.position;
                Vector3 flatGoal = new Vector3(target.x, here.y, target.z);
                Vector3 dir = flatGoal - here;
                dir.y = 0f;
                if (dir.sqrMagnitude < EPS)
                    return;
                dir.Normalize();
                Vector3 sep = SeparationVector(_separationRadius, player);
                if (sep.sqrMagnitude > EPS)
                    dir = (dir + sep).normalized;
                AdvanceWithObstacleHandling(dir, _npcMoveSpeed * Mathf.Clamp(speedMultiplier, 0.1f, 1.25f));
            }

            private void AdvanceWithObstacleHandling(Vector3 dir, float moveDist)
            {
                Vector3 here = _npc.transform.position;
                Vector3 a = here + Vector3.up * 0.5f;
                Vector3 b = here + Vector3.up * 1.6f;
                if (Physics.CapsuleCast(a, b, 0.28f, dir, out RaycastHit hit, moveDist, _blockMask, QueryTriggerInteraction.Ignore) && !ShouldIgnoreCollider(hit.collider))
                {
                    Vector3 slideDir = Vector3.ProjectOnPlane(dir, hit.normal);
                    if (slideDir.sqrMagnitude < 1e-6f)
                        slideDir = Vector3.Cross(Vector3.up, dir);
                    if (slideDir.sqrMagnitude > 1e-6f)
                    {
                        slideDir.Normalize();
                        float slideDist = moveDist * 0.5f;
                        if (!Physics.CapsuleCast(a, b, 0.28f, slideDir, out _, slideDist, _blockMask, QueryTriggerInteraction.Ignore))
                        {
                            Vector3 next = here + slideDir * slideDist;
                            if (TryGetGroundPosition(next, out var g))
                                next = g;
                            if (!WouldOverlapNpcs(next, 0.55f) && IsWithinHomeLimit(next))
                            {
                                _npc.transform.position = next;
                                _npc.transform.rotation = Quaternion.LookRotation(slideDir);
                                return;
                            }
                        }
                    }
                    Vector3 side = Vector3.Cross(Vector3.up, dir).normalized;
                    if (TrySideAdvance(here, a, b, side, moveDist))
                        return;
                    if (TrySideAdvance(here, a, b, -side, moveDist))
                        return;
                    float shortDist = moveDist * 0.3f;
                    if (!Physics.CapsuleCast(a, b, 0.28f, dir, out _, shortDist, _blockMask, QueryTriggerInteraction.Ignore))
                    {
                        Vector3 shorter = here + dir * shortDist;
                        if (TryGetGroundPosition(shorter, out var g2))
                            shorter = g2;
                        if (!WouldOverlapNpcs(shorter, 0.55f) && IsWithinHomeLimit(shorter))
                        {
                            _npc.transform.position = shorter;
                            _npc.transform.rotation = Quaternion.LookRotation(dir);
                        }
                    }
                    return;
                }
                Vector3 nextFwd = here + dir * moveDist;
                if (TryGetGroundPosition(nextFwd, out var g3))
                    nextFwd = g3;
                if (!WouldOverlapNpcs(nextFwd, 0.55f) && IsWithinHomeLimit(nextFwd))
                {
                    _npc.transform.position = nextFwd;
                    _npc.transform.rotation = Quaternion.LookRotation(dir);
                }
            }

            private bool TrySideAdvance(Vector3 here, Vector3 capA, Vector3 capB, Vector3 sideDir, float moveDist)
            {
                if (sideDir.sqrMagnitude < 1e-6f)
                    return false;
                if (Physics.CapsuleCast(capA, capB, 0.28f, sideDir, out _, moveDist, _blockMask, QueryTriggerInteraction.Ignore))
                    return false;
                Vector3 target = here + sideDir * moveDist;
                if (TryGetGroundPosition(target, out var g))
                    target = g;
                if (WouldOverlapNpcs(target, 0.55f) || !IsWithinHomeLimit(target))
                    return false;
                _npc.transform.position = target;
                _npc.transform.rotation = Quaternion.LookRotation(sideDir);
                return true;
            }

            private Vector3 SeparationVector(float radius, BasePlayer player)
            {
                int hits = Physics.OverlapSphereNonAlloc(_npc.transform.position, radius, _nearby, ~0, QueryTriggerInteraction.Ignore);
                Vector3 push = Vector3.zero;
                int count = 0;
                for (int i = 0; i < hits; i++)
                {
                    var col = _nearby[i];
                    if (col == null)
                        continue;
                    var other = col.GetComponentInParent<BaseCombatEntity>();
                    if (other == null || other == _npc || other == player)
                        continue;
                    if (other.IsDestroyed)
                        continue;
                    if (_instance == null || !_instance.IsDungeonEntity(other))
                        continue;
                    Vector3 away = _npc.transform.position - other.transform.position;
                    away.y = 0f;
                    float d = away.magnitude;
                    if (d < 0.001f || d > radius)
                        continue;
                    float t = 1f - (d / radius);
                    push += away / (d + 0.001f) * t;
                    count++;
                }
                if (count > 0)
                    push /= count;
                return push.normalized * _separationWeight;
            }

            private bool WouldOverlapNpcs(Vector3 p, float radius)
            {
                int hits = Physics.OverlapSphereNonAlloc(p, radius, _nearby, ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < hits; i++)
                {
                    var col = _nearby[i];
                    if (col == null)
                        continue;
                    int lmask = 1 << col.gameObject.layer;
                    if ((lmask & _blockMask) != 0)
                        continue;
                    var other = col.GetComponentInParent<NPCPlayer>();
                    if (other != null)
                    {
                        if (other == _npc)
                            continue;
                        if (other.IsDestroyed)
                            continue;
                        if (_instance != null && !_instance.IsDungeonEntity(other))
                            continue;
                        return true;
                    }
                }
                return false;
            }

            private bool ShouldIgnoreCollider(Collider col)
            {
                if (col == null)
                    return true;
                if (col.gameObject == _npc.gameObject || col.transform.IsChildOf(_npc.transform))
                    return true;
                int lmask = 1 << col.gameObject.layer;
                if ((lmask & Layers.Mask.Player_Server) != 0)
                    return true;
                if (col.bounds.max.y < _npc.transform.position.y + 0.2f)
                    return true;
                return false;
            }

            private bool TryGetGroundPosition(Vector3 pos, out Vector3 groundPos)
            {
                groundPos = pos;
                var dungeon = _instance?.GetActiveDungeonByEntity(_npc);
                if (dungeon != null)
                {
                    if (!_instance.TryGetDungeonFloorPoint(dungeon, pos, out Vector3 floor) || Mathf.Abs(_npc.transform.position.y - floor.y) > MaxStepHeight)
                        return false;
                    groundPos = floor + Vector3.up * 0.02f;
                    return true;
                }
                if (Physics.Raycast(pos + Vector3.up * 1.5f, Vector3.down, out RaycastHit hit, 3f, _blockMask, QueryTriggerInteraction.Ignore))
                {
                    float delta = _npc.transform.position.y - hit.point.y;
                    if (Mathf.Abs(delta) <= MaxStepHeight)
                    {
                        groundPos = new Vector3(pos.x, hit.point.y, pos.z);
                        return true;
                    }
                }
                return false;
            }

            private void OnDestroy()
            {
                CancelInvoke(nameof(UpdateAi));
                CancelInvoke(nameof(UpdateMovement));
                CancelInvoke(nameof(UpdateFire));
            }
        }

        private bool IsMeleeWeapon(string shortName)
        {
            if (string.IsNullOrEmpty(shortName))
                return false;
            var def = ItemManager.FindItemDefinition(shortName);
            if (def == null)
                return false;
            var entityMod = def.GetComponent<ItemModEntity>();
            if (entityMod == null)
                return false;
            var prefab = entityMod.entityPrefab?.Get();
            if (prefab == null)
                return false;
            var attackEntity = prefab.GetComponent<AttackEntity>();
            if (attackEntity == null)
                return false;
            if (attackEntity is BaseProjectile)
                return false;
            if (attackEntity is BaseMelee)
                return true;
            return false;
        }

        public class BossMonoBehaviour : MonoBehaviour { }

        public class TurretMonoBehaviour : MonoBehaviour
        {
            private AutoTurret _turret;

            private void Awake()
            {
                _turret = GetComponent<AutoTurret>();
                _turret.SetPeacekeepermode(false);
                _turret.InitiateStartup();
                _turret.isLootable = false;
                _turret.dropFloats = false;
                _turret.dropsLoot = false;
                InvokeRepeating(nameof(ManageAmmo), 2f, 30f);
                InvokeRepeating(nameof(Perform360IdleSweep), 3f, 1f);
                InvokeRepeating(nameof(RemoveInterference), 1f, 1f);
            }

            private void RemoveInterference()
            {
                AutoTurret.interferenceUpdateList.Remove(_turret);
                if (_turret.HasFlag(BaseEntity.Flags.OnFire))
                {
                    using (var setFlags = _turret.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate_Flags))
                    {
                        setFlags.Set(BaseEntity.Flags.OnFire, false);
                    }
                }
            }

            private void ManageAmmo()
            {
                const int maxAmmoCapacity = 1000;
                if (_turret.AttachedWeapon is not BaseProjectile projectileWeapon || projectileWeapon.primaryMagazine?.ammoType == null)
                    return;
                var ammoType = projectileWeapon.primaryMagazine.ammoType;
                int currentAmmoCount = _turret.inventory.itemList.Where(item => item.info == ammoType).Sum(item => item.amount);
                if (currentAmmoCount < maxAmmoCapacity)
                {
                    int ammoNeeded = maxAmmoCapacity - currentAmmoCount;
                    Item additionalAmmo = ItemManager.Create(ammoType, ammoNeeded);
                    if (additionalAmmo != null && !additionalAmmo.MoveToContainer(_turret.inventory))
                        additionalAmmo.Remove();
                    _turret.UpdateTotalAmmo();
                    _turret.EnsureReloaded();
                    _turret.SendNetworkUpdateImmediate();
                }
            }

            private void Perform360IdleSweep()
            {
                if (!_turret.HasTarget())
                {
                    _turret.targetAimDir = Quaternion.Euler(0, Random.Range(0f, 360f), 0) * Vector3.forward;
                    _turret.aimDir = Vector3.Lerp(_turret.aimDir, _turret.targetAimDir, Time.deltaTime * 2f);
                    _turret.UpdateAiming(Time.deltaTime);
                }
            }

            private void OnDestroy()
            {
                CancelInvoke(nameof(ManageAmmo));
                CancelInvoke(nameof(Perform360IdleSweep));
                CancelInvoke(nameof(RemoveInterference));
            }
        }

        public class DungeonEventsTrigger : FacepunchBehaviour
        {
            private ActiveDungeon _dungeon;
            private bool _rewardSent = false;
            private Rigidbody _rigidbody;
            private BoxCollider _boxCollider;
            public int TriggerTime = 0;
            public float NightVisionTime = -1;

            private void Awake()
            {
                if (_rigidbody != null)
                    DestroyImmediate(_rigidbody);
                gameObject.layer = (int)Layer.Reserved1;
                gameObject.name = "DungeonEventsTrigger";
                _rigidbody = gameObject.AddComponent<Rigidbody>();
                _rigidbody.useGravity = false;
                _rigidbody.isKinematic = true;
                _rigidbody.detectCollisions = true;
                _rigidbody.collisionDetectionMode = CollisionDetectionMode.Discrete;
                _boxCollider = gameObject.GetComponent<BoxCollider>();
            }

            private void OnDestroy()
            {
                CancelInvoke(nameof(UpdateDungeonPlayersUI));
                CancelInvoke(nameof(CheckDungeonClear));
            }

            public void Initialize(ActiveDungeon dungeon)
            {
                _dungeon = dungeon;
                InvokeRepeating(nameof(UpdateDungeonPlayersUI), 1f, 1f);
                InvokeRepeating(nameof(CheckDungeonClear), 5f, 5f);
            }

            private void CheckDungeonClear()
            {
                if (_rewardSent || _dungeon == null || !_dungeon.Spawned || _instance == null || _instance._isUnloading)
                    return;
                bool allNpcsDead = _instance.AreAllNpcsGone(_dungeon);
                bool allBoxesDead = _instance.AreAllBoxesGone(_dungeon);
                bool allTurretsDead = _instance.AreAllTurretsGone(_dungeon);
                if (!_dungeon.ClearRewardGiven && allNpcsDead && allBoxesDead && allTurretsDead)
                {
                    _dungeon.ClearRewardGiven = true;
                    _instance.RewardDungeonClear(_dungeon);
                    _rewardSent = true;
                }
            }

            public void SetPosition(Vector3 position, Vector3 size)
            {
                transform.position = position;
                if (position != Vector3.zero)
                {
                    if (_boxCollider == null)
                    {
                        _boxCollider = gameObject.AddComponent<BoxCollider>();
                        _boxCollider.isTrigger = true;
                    }
                    _boxCollider.size = size;
                }
            }

            private void UpdateDungeonPlayersUI()
            {
                if (_dungeon == null || _instance == null)
                {
                    CancelInvoke(nameof(UpdateDungeonPlayersUI));
                    CancelInvoke(nameof(CheckDungeonClear));
                    return;
                }
                double destructionTimeRemaining = 0;
                if (_dungeon.HasBeenCleared && _dungeon.DestructionCountdownStart.HasValue)
                {
                    double elapsed = (DateTime.UtcNow - _dungeon.DestructionCountdownStart.Value).TotalSeconds;
                    destructionTimeRemaining = _dungeon.DestructionDelay - elapsed;
                }
                var playersInside = Pool.Get<List<ulong>>();
                var playersToRemove = Pool.Get<List<ulong>>();
                try
                {
                    playersInside.AddRange(_dungeon.PlayersInside);
                    foreach (var userID in playersInside)
                    {
                        var player = BasePlayer.FindByID(userID);
                        if (player == null || !player.IsConnected || _instance.GetDungeonForPlayer(player) != _dungeon)
                        {
                            playersToRemove.Add(userID);
                            if (_instance._eventUiStates.TryGetValue(userID, out var state) && state.DungeonId == _dungeon.UniqueId)
                            {
                                _instance._eventUiStates.Remove(userID);
                                _instance.DestroyEventUi(player);
                            }
                            continue;
                        }
                        _instance.OpenEventUI(player, _dungeon);
                    }
                    foreach (var userID in playersToRemove)
                        _dungeon.PlayersInside.Remove(userID);
                    if (playersToRemove.Count > 0)
                        _instance.HandlePlayerDungeonDeparture(_dungeon);
                }
                finally
                {
                    Pool.FreeUnmanaged(ref playersInside);
                    Pool.FreeUnmanaged(ref playersToRemove);
                }
                if (_dungeon.HasBeenCleared && destructionTimeRemaining <= 0)
                {
                    _instance.TeleportPlayersAndDestroyDungeon(_dungeon);
                    CancelInvoke(nameof(UpdateDungeonPlayersUI));
                    CancelInvoke(nameof(CheckDungeonClear));
                }
            }

            private void OnTriggerEnter(Collider col)
            {
                if (col == null || _dungeon == null || _instance == null)
                    return;
                var entity = col.ToBaseEntity();
                if (entity is not BasePlayer player || !player.userID.IsSteamId())
                    return;
                if (!_instance.CanEnter(player, _dungeon))
                {
                    _instance.NotifyDungeonEntryDenied(player, _dungeon);
                    _instance.TeleportPlayerToOutsidePortal(player, _dungeon, true);
                    return;
                }
                if (!_dungeon.ActivationTime.HasValue)
                    _dungeon.ActivationTime = DateTime.UtcNow;
                _dungeon.PlayersInside.Add(player.userID);
                if (!IsInvoking(nameof(UpdateDungeonPlayersUI)))
                    InvokeRepeating(nameof(UpdateDungeonPlayersUI), 1f, 1f);
                _instance.UpdateTime(player, TriggerTime);
                _instance.OpenEventUI(player, _dungeon);
                if (player.GetComponent<PlayerDungeonChecker>() == null)
                    player.gameObject.AddComponent<PlayerDungeonChecker>().Initialize(player, NightVisionTime);
            }

            private void OnTriggerExit(Collider col)
            {
                if (col == null || _dungeon == null || _instance == null)
                    return;
                var entity = col.ToBaseEntity();
                if (entity == null)
                    return;
                if (entity is BasePlayer player && player.userID.IsSteamId())
                {
                    if (_dungeon.PlayersInside.Remove(player.userID))
                        _instance.HandlePlayerDungeonDeparture(_dungeon);
                    _instance.UpdateTime(player, -1);
                    _instance.DestroyEventUi(player);
                    var checker = player.GetComponent<PlayerDungeonChecker>();
                    if (checker != null)
                        Destroy(checker);
                    if (_instance.NightVision != null && NightVisionTime > -1)
                    {
                        _instance.NightVision.CallHook("LockPlayerTime", player, NightVisionTime);
                    }
                }
            }
        }

        public class PlayerDungeonChecker : MonoBehaviour
        {
            private BasePlayer _player;
            private DungeonEvents _plugin;
            public float NightVisionTime = -1;
            private float _nextTemperatureUpdate;

            public void Initialize(BasePlayer player, float nightVisionTime)
            {
                _player = player;
                _plugin = _instance;
                NightVisionTime = nightVisionTime;
                _nextTemperatureUpdate = Time.time + 1f;
                InvokeRepeating(nameof(CheckDungeonStatus), 5f, 5f);
                _plugin._wasInDungeonPlayers.Add(_player);
            }

            private void FixedUpdate()
            {
                if (_player == null || !_player.IsConnected || !_player.IsAlive())
                    return;
                if (Time.time >= _nextTemperatureUpdate)
                {
                    _nextTemperatureUpdate = Time.time + 1f;
                    float t = _player.metabolism.temperature.value;
                    if (t < 19f || t > 31f)
                        _player.metabolism.temperature.value = 25f;
                }
            }

            private void CheckDungeonStatus()
            {
                if (_player == null || !_player.IsConnected || _plugin == null || _plugin._isUnloading)
                {
                    Destroy(this);
                    return;
                }
                if (_plugin.GetDungeonForPlayer(_player) == null)
                {
                    _plugin.UpdateTime(_player, -1);
                    _plugin.DestroyEventUi(_player);
                    if (_plugin.NightVision != null && NightVisionTime > -1)
                        _plugin.NightVision.CallHook("LockPlayerTime", _player, NightVisionTime);
                    Destroy(this);
                }
            }

            private void OnDestroy()
            {
                CancelInvoke(nameof(CheckDungeonStatus));
                var plugin = _plugin;
                if (plugin == null)
                    return;
                if (plugin._isUnloading)
                    plugin._wasInDungeonPlayers.Remove(_player);
                else
                    plugin.timer.Once(4f, () => plugin._wasInDungeonPlayers.Remove(_player));
            }
        }
        #endregion

        #region Utilities / Helpers
        private List<StoredPortalPosition> GetCustomPortalSpawnList(DungeonOrigin origin)
        {
            _config ??= new Configuration();
            _config.CustomPortalSpawnLocations ??= new CustomPortalSpawnConfiguration();
            _config.CustomPortalSpawnLocations.AutoSpawnLocations ??= new List<StoredPortalPosition>();
            _config.CustomPortalSpawnLocations.BuyLocations ??= new List<StoredPortalPosition>();
            return origin == DungeonOrigin.AutoSpawn ? _config.CustomPortalSpawnLocations.AutoSpawnLocations : _config.CustomPortalSpawnLocations.BuyLocations;
        }

        private void AddCustomPortalSpawnLocation(BasePlayer player, DungeonOrigin origin)
        {
            if (player == null)
                return;
            Vector3 position = RoundVector(player.transform.position, 2);
            var locations = GetCustomPortalSpawnList(origin);
            if (locations.Any(x => x != null && Vector3.Distance(x.ToVector3(), position) <= 0.5f))
            {
                player.ChatMessage("A custom portal spawn location of this type already exists here.");
                return;
            }
            locations.Add(StoredPortalPosition.FromVector3(position));
            SaveConfig();
            string type = origin == DungeonOrigin.AutoSpawn ? "Auto Spawn" : "Buy";
            player.ChatMessage($"Custom {type} portal spawn #{locations.Count} saved at {MapHelper.PositionToString(position)} ({position.x:0.00}, {position.y:0.00}, {position.z:0.00}).");
        }

        private void RemoveNearestCustomPortalSpawnLocation(BasePlayer player, DungeonOrigin origin, float radius)
        {
            if (player == null)
                return;
            var locations = GetCustomPortalSpawnList(origin);
            StoredPortalPosition nearest = null;
            float nearestDistance = float.MaxValue;
            Vector3 playerPosition = player.transform.position;
            foreach (var stored in locations)
            {
                if (stored == null)
                    continue;
                float distance = Vector3.Distance(playerPosition, stored.ToVector3());
                if (distance >= nearestDistance)
                    continue;
                nearestDistance = distance;
                nearest = stored;
            }
            if (nearest == null || nearestDistance > radius)
            {
                player.ChatMessage($"No custom {(origin == DungeonOrigin.AutoSpawn ? "Auto Spawn" : "Buy")} portal spawn found within {radius:0.#}m.");
                return;
            }
            Vector3 removedPosition = nearest.ToVector3();
            locations.Remove(nearest);
            SaveConfig();
            player.ChatMessage(
                $"Removed custom {(origin == DungeonOrigin.AutoSpawn ? "Auto Spawn" : "Buy")} portal spawn at {MapHelper.PositionToString(removedPosition)} ({nearestDistance:0.0}m away)."
            );
        }

        private void ShowCustomPortalSpawnLocations(BasePlayer player)
        {
            if (player == null)
                return;
            var autoLocations = GetCustomPortalSpawnList(DungeonOrigin.AutoSpawn);
            var buyLocations = GetCustomPortalSpawnList(DungeonOrigin.Buy);
            player.ChatMessage($"Custom portal spawns: Auto Spawn = {autoLocations.Count}, Buy = {buyLocations.Count}.");
            for (int i = 0; i < autoLocations.Count; i++)
            {
                var stored = autoLocations[i];
                if (stored == null)
                    continue;
                Vector3 position = stored.ToVector3();
                player.ChatMessage($"AUTO #{i + 1}: {MapHelper.PositionToString(position)} ({position.x:0.00}, {position.y:0.00}, {position.z:0.00})");
            }
            for (int i = 0; i < buyLocations.Count; i++)
            {
                var stored = buyLocations[i];
                if (stored == null)
                    continue;
                Vector3 position = stored.ToVector3();
                player.ChatMessage($"BUY #{i + 1}: {MapHelper.PositionToString(position)} ({position.x:0.00}, {position.y:0.00}, {position.z:0.00})");
            }
        }

        private void SendActiveDungeonList(Action<string> send)
        {
            if (send == null)
                return;
            var active = _activeDungeons.Where(d => d != null).OrderBy(d => d.CreationTime).ToList();
            if (active.Count == 0)
            {
                send("There are no active dungeons.");
                return;
            }
            send($"Active dungeons: {active.Count}");
            for (int i = 0; i < active.Count; i++)
            {
                var dungeon = active[i];
                string origin = dungeon.Origin == DungeonOrigin.Buy ? "Purchased" : "Auto";
                string state =
                    !dungeon.Spawned ? "Spawning"
                    : dungeon.HasBeenCleared ? "Cleared"
                    : dungeon.PlayersInside.Count > 0 ? $"Active ({dungeon.PlayersInside.Count} player(s))"
                    : "Ready";
                string owner = dungeon.OwnerID != 0UL ? $" | Owner: {(string.IsNullOrEmpty(dungeon.OwnerName) ? dungeon.OwnerID.ToString() : dungeon.OwnerName)}" : string.Empty;
                send($"#{i + 1} | {dungeon.TierName} | Grid {dungeon.Grid} | {origin} | {state}{owner}");
            }
        }

        private bool TrySelectCustomPortalPosition(DungeonRequest request, out Vector3 position, out bool customLocationsConfigured)
        {
            position = Vector3.zero;
            customLocationsConfigured = false;
            if (request == null)
                return false;
            var customConfig = _config?.CustomPortalSpawnLocations;
            if (customConfig == null || !customConfig.Enabled)
                return false;
            var locations = GetCustomPortalSpawnList(request.Origin);
            customLocationsConfigured = locations.Count > 0;
            if (!customLocationsConfigured)
                return false;
            var available = locations.Where(x => x != null).Select(x => x.ToVector3()).Where(x => x != Vector3.zero && !IsCustomPortalPositionInUse(x)).ToList();
            if (available.Count == 0)
                return false;
            position = available[Random.Range(0, available.Count)];
            return true;
        }

        private bool IsCustomPortalPositionInUse(Vector3 position)
        {
            float toleranceSqr = CustomPortalSpawnOccupiedTolerance * CustomPortalSpawnOccupiedTolerance;
            foreach (var dungeon in _activeDungeons)
            {
                if (dungeon == null || dungeon.PortalPosition == Vector3.zero)
                    continue;
                if ((dungeon.PortalPosition - position).sqrMagnitude <= toleranceSqr)
                    return true;
            }
            if (IsRequestUsingCustomPortalPosition(_currentDungeonRequest, position, toleranceSqr))
                return true;
            if (_dungeonQueue.Any(request => IsRequestUsingCustomPortalPosition(request, position, toleranceSqr)))
                return true;
            if (_dungeonFailedBuyQueue.Any(request => IsRequestUsingCustomPortalPosition(request, position, toleranceSqr)))
                return true;
            if (_dungeonFailedAutoSpawnQueue.Any(request => IsRequestUsingCustomPortalPosition(request, position, toleranceSqr)))
                return true;
            return false;
        }

        private bool IsRequestUsingCustomPortalPosition(DungeonRequest request, Vector3 position, float toleranceSqr)
        {
            if (request == null || !request.UsesCustomPortalPosition || request.PortalPosition == Vector3.zero)
                return false;
            return (request.PortalPosition - position).sqrMagnitude <= toleranceSqr;
        }

        private bool CanFallbackToRandomPortalPosition(DungeonOrigin origin, bool customLocationsConfigured)
        {
            if (!customLocationsConfigured)
                return true;
            var customConfig = _config?.CustomPortalSpawnLocations;
            if (customConfig == null || !customConfig.Enabled)
                return true;
            return origin == DungeonOrigin.AutoSpawn ? customConfig.FallbackToRandomAutoSpawn : customConfig.FallbackToRandomBuy;
        }

        private bool IsDungeonAnimalDamageAllowed(BasePlayer victim, BaseEntity animal)
        {
            if (victim == null || animal == null)
                return false;
            if (!IsPlayer(victim) || victim.IsDead() || victim.IsSleeping())
                return false;
            if (!IsDungeonEntity(animal) || !IsAnimalEntity(animal))
                return false;
            var dungeon = GetActiveDungeonByEntity(animal);
            if (dungeon == null || !dungeon.PlayersInside.Contains(victim.userID))
                return false;
            float range = GetAnimalAttackRange(animal) + 0.15f;
            Vector3 origin = animal.transform.position;
            var animalNpc = animal as BaseAnimalNPC;
            if (animalNpc != null)
                origin = animalNpc.AttackPosition;
            Vector3 target = victim.transform.position;
            origin.y = 0f;
            target.y = 0f;
            if ((target - origin).sqrMagnitude > range * range)
                return false;
            if (Mathf.Abs(victim.transform.position.y - animal.transform.position.y) > 2.2f)
                return false;
            Vector3 direction = victim.transform.position - animal.transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 1e-6f && Vector3.Angle(animal.transform.forward, direction.normalized) > 105f)
                return false;
            int blockMask = Layers.Mask.Construction | Layers.Mask.Deployed | Layers.Mask.World | Layers.Mask.Terrain | Layers.Mask.Default;
            Vector3 from = animal.CenterPoint();
            Vector3 to = victim.eyes?.position ?? victim.CenterPoint();
            if (Physics.Linecast(from, to, blockMask, QueryTriggerInteraction.Ignore))
                return false;
            return true;
        }

        private object HandleDamageToDungeonAnimal(BaseEntity victim, HitInfo info)
        {
            if (victim == null || info == null)
                return null;
            if (!IsDungeonEntity(victim))
                return null;
            if (!IsAnimalEntity(victim))
                return null;
            if (BlockAndNotifyEntranceAttack(info.InitiatorPlayer, victim))
            {
                info.damageTypes?.ScaleAll(0f);
                return true;
            }
            var attacker = info.InitiatorPlayer;
            if (attacker == null || !IsPlayer(attacker))
            {
                info.damageTypes?.ScaleAll(0f);
                return true;
            }
            return null;
        }

        private static int GetMinutesSinceLastWipe()
        {
            DateTime baseUtcTime = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            double currentSeconds = DateTime.UtcNow.Subtract(baseUtcTime).TotalSeconds;
            double wipeSeconds = SaveRestore.SaveCreatedTime.Subtract(baseUtcTime).TotalSeconds;
            double elapsedSeconds = currentSeconds - wipeSeconds;
            if (elapsedSeconds <= 0d)
                return 0;
            return Mathf.FloorToInt((float)(elapsedSeconds / 60d));
        }

        private static bool IsTierUnlockedByWipeTime(DungeonTierConfig tierConfig)
        {
            if (tierConfig == null)
                return false;
            if (tierConfig.MinutesAfterWipeToActivate <= 0)
                return true;
            int minutesSinceWipe = GetMinutesSinceLastWipe();
            return minutesSinceWipe >= tierConfig.MinutesAfterWipeToActivate;
        }

        private static string GetTierPermission(DungeonTierConfig tier)
        {
            string name = (tier?.TierName ?? string.Empty).Trim().ToLowerInvariant();
            string suffix = new string(name.Where(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')).ToArray());
            return "dungeonevents." + (suffix.Length > 0 ? suffix : "tier");
        }

        private void RegisterTierPermissions()
        {
            if (_config?.Tiers == null)
                return;
            foreach (var tier in _config.Tiers)
            {
                if (tier == null)
                    continue;
                string tierPermission = GetTierPermission(tier);
                if (!permission.PermissionExists(tierPermission))
                    permission.RegisterPermission(tierPermission, this);
            }
        }

        private bool HasTierPermission(BasePlayer player, DungeonTierConfig tier)
        {
            if (player == null || !player.userID.IsSteamId() || tier == null)
                return false;
            if (!tier.RequirePermission)
                return true;
            string steamId = player.UserIDString;
            return IsAdmin(player) || permission.UserHasPermission(steamId, PermissionAdmin) || permission.UserHasPermission(steamId, GetTierPermission(tier));
        }

        private DungeonTierConfig GetCurrentDungeonTier(ActiveDungeon dungeon)
        {
            if (dungeon == null)
                return null;
            return FindTierByName(dungeon.TierName) ?? dungeon.TierConfig;
        }

        private void NotifyDungeonEntryDenied(BasePlayer player, ActiveDungeon dungeon)
        {
            if (player == null)
                return;
            var tier = GetCurrentDungeonTier(dungeon);
            if (tier != null && !HasTierPermission(player, tier))
                NotifyPlayer(player, "NoTierPermission", 0, tier.TierName, GetTierPermission(tier));
            else
                NotifyPlayer(player, "NoAccess", 0);
        }

        private void RegisterCommands()
        {
            if (_config == null)
                return;
            _config.ChatCommands ??= new ChatCommandConfig();
            _config.ConsoleCommands ??= new ConsoleCommandConfig();
            TryAddChat(_config.ChatCommands.BuyDungeon, nameof(BuyDungeonChatCommand));
            TryAddChat(_config.ChatCommands.CreateDungeon, nameof(CreateDungeonChatCommand));
            TryAddChat(_config.ChatCommands.ReloadConfig, nameof(ReloadConfigChatCommand));
            TryAddChat(_config.ChatCommands.TogglePurchases, nameof(TogglePurchasesChatCommand));
            TryAddChat(_config.ChatCommands.RemoveInactiveDungeons, nameof(RemoveInactiveDungeonsChatCommand));
            TryAddChat(_config.ChatCommands.RemoveAllDungeons, nameof(RemoveAllDungeonsChatCommand));
            TryAddChat(_config.ChatCommands.RemoveNearest, nameof(RemoveNearestDungeonChatCommand));
            TryAddChat(_config.ChatCommands.RemoveOwnDungeon, nameof(RemoveOwnDungeonChatCommand));
            TryAddChat(_config.ChatCommands.ForceRemoveAll, nameof(ForceRemoveAllDungeonsChatCommand));
            TryAddChat(_config.ChatCommands.AddAutoPortalSpawnLocation, nameof(AddAutoPortalSpawnLocationChatCommand));
            TryAddChat(_config.ChatCommands.AddBuyPortalSpawnLocation, nameof(AddBuyPortalSpawnLocationChatCommand));
            TryAddChat(_config.ChatCommands.RemoveNearestCustomPortalSpawnLocation, nameof(RemoveNearestCustomPortalSpawnLocationChatCommand));
            TryAddChat(_config.ChatCommands.ListCustomPortalSpawnLocations, nameof(ListCustomPortalSpawnLocationsChatCommand));
            TryAddChat(_config.ChatCommands.ListActiveDungeons, nameof(ListActiveDungeonsChatCommand));
            TryAddConsole(_config.ConsoleCommands.BuyDungeon, nameof(BuyDungeonCommand));
            TryAddConsole(_config.ConsoleCommands.SpawnRandomDungeon, nameof(SpawnRandomDungeonCommand));
            TryAddConsole(_config.ConsoleCommands.SpawnFixedDungeon, nameof(SpawnFixedDungeonCommand));
            TryAddConsole(_config.ConsoleCommands.ReloadConfig, nameof(ReloadConfigConsoleCommand));
            TryAddConsole(_config.ConsoleCommands.ListActiveDungeons, nameof(ListActiveDungeonsConsoleCommand));
        }

        private void TryAddChat(string command, string handler)
        {
            if (!string.IsNullOrWhiteSpace(command))
                cmd.AddChatCommand(command, this, handler);
        }

        private void TryAddConsole(string command, string handler)
        {
            if (!string.IsNullOrWhiteSpace(command))
                cmd.AddConsoleCommand(command, this, handler);
        }

        public void UpdateTime(BasePlayer player, int time)
        {
            if (!player.IsConnected)
                return;
            if (player.IsAdmin)
            {
                player.SendConsoleCommand("admintime", time);
            }
            else if (!player.IsFlying)
            {
                player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                player.SendNetworkUpdateImmediate();
                player.SendConsoleCommand("admintime", time);
                player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false);
            }
            player.SendNetworkUpdateImmediate();
        }

        private string FormatTime(double totalSeconds)
        {
            if (totalSeconds < 0)
                totalSeconds = 0;
            TimeSpan ts = TimeSpan.FromSeconds(totalSeconds);
            return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        private void DrawLines(ActiveDungeon activeDungeon, BasePlayer player)
        {
            if (activeDungeon == null || player == null)
                return;
            var bounds = activeDungeon.DungeonBounds;
            var center = bounds.center;
            var ex = bounds.extents.x;
            var ey = bounds.extents.y;
            var ez = bounds.extents.z;
            Vector3 c000 = center + new Vector3(-ex, -ey, -ez);
            Vector3 c001 = center + new Vector3(-ex, -ey, ez);
            Vector3 c010 = center + new Vector3(-ex, ey, -ez);
            Vector3 c011 = center + new Vector3(-ex, ey, ez);
            Vector3 c100 = center + new Vector3(ex, -ey, -ez);
            Vector3 c101 = center + new Vector3(ex, -ey, ez);
            Vector3 c110 = center + new Vector3(ex, ey, -ez);
            Vector3 c111 = center + new Vector3(ex, ey, ez);
            float duration = 120f;
            Color color = Color.red;
            string cmd = "ddraw.line";
            player.SendConsoleCommand(cmd, duration, color, c000, c001);
            player.SendConsoleCommand(cmd, duration, color, c001, c101);
            player.SendConsoleCommand(cmd, duration, color, c101, c100);
            player.SendConsoleCommand(cmd, duration, color, c100, c000);
            player.SendConsoleCommand(cmd, duration, color, c010, c011);
            player.SendConsoleCommand(cmd, duration, color, c011, c111);
            player.SendConsoleCommand(cmd, duration, color, c111, c110);
            player.SendConsoleCommand(cmd, duration, color, c110, c010);
            player.SendConsoleCommand(cmd, duration, color, c000, c010);
            player.SendConsoleCommand(cmd, duration, color, c001, c011);
            player.SendConsoleCommand(cmd, duration, color, c100, c110);
            player.SendConsoleCommand(cmd, duration, color, c101, c111);
        }

        private static bool TryParseHexColor(string hex, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrEmpty(hex))
                return false;
            var h = hex.Trim().TrimStart('#');
            byte r,
                g,
                b,
                a = 255;
            if (h.Length == 6)
            {
                if (
                    byte.TryParse(h.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out r)
                    && byte.TryParse(h.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out g)
                    && byte.TryParse(h.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out b)
                )
                {
                    color = new Color32(r, g, b, a);
                    return true;
                }
            }
            else if (h.Length == 8)
            {
                if (
                    byte.TryParse(h.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out a)
                    && byte.TryParse(h.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out r)
                    && byte.TryParse(h.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out g)
                    && byte.TryParse(h.Substring(6, 2), System.Globalization.NumberStyles.HexNumber, null, out b)
                )
                {
                    color = new Color32(r, g, b, a);
                    return true;
                }
            }
            return false;
        }

        private bool IsAdmin(BasePlayer player) => player != null && player.IsAdmin;

        private static bool IsPlayer(BasePlayer targetPlayer) => targetPlayer != null && targetPlayer.userID.IsSteamId();

        private bool IsDungeonEntity(BaseEntity entity)
        {
            return GetActiveDungeonByEntity(entity) != null;
        }

        private void LogDebug(string message, int level = 0)
        {
            if (string.IsNullOrEmpty(message))
                return;
            string formattedMessage = $"[{Name}] {message}";
            switch (level)
            {
                case 1:
                    if (_config.EnableWarningLogs)
                        Debug.LogWarning(formattedMessage);
                    break;
                case 2:
                    if (_config.EnableWarningLogs)
                        Debug.LogError(formattedMessage);
                    break;
                default:
                    if (_config.EnableDebugLogs)
                        Debug.Log(formattedMessage);
                    break;
            }
        }

        private string Msg(string userId, string key, params object[] args)
        {
            return string.Format(lang.GetMessage(key, this, userId), args);
        }

        private void NotifyPlayer(BasePlayer player, string key, int type, params object[] args)
        {
            if (player == null || !player.IsConnected)
                return;
            var msgString = Msg(player.UserIDString, key, args);
            if (_config.UseNotify && Notify != null)
            {
                Interface.Oxide.CallHook("SendNotify", player.UserIDString, type, msgString);
            }
            else
            {
                player.ChatMessage(msgString);
            }
            if (_config.EnableShowToast)
            {
                player.ShowToast(GameTip.Styles.Blue_Normal, msgString);
            }
        }

        private void NotifyPlayersOfNewDungeon(ActiveDungeon dungeon)
        {
            if (dungeon == null || dungeon.TierConfig == null)
                return;
            if (dungeon.Origin == DungeonOrigin.Buy && dungeon.OwnerID.IsSteamId())
            {
                bool ownerOnly = _config.DungeonSpawn.ShowPurchaseAnnouncementToOwnerOnly;
                foreach (var p in BasePlayer.activePlayerList)
                {
                    if (p == null || !p.userID.IsSteamId())
                        continue;
                    if (ownerOnly && p.userID != dungeon.OwnerID)
                        continue;
                    NotifyPlayer(p, "DungeonPurchased", 0, dungeon.TierConfig.MapName, dungeon.OwnerName, dungeon.Grid);
                }
                return;
            }
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p != null && p.userID.IsSteamId())
                    NotifyPlayer(p, "DungeonAppeared", 0, dungeon.TierConfig.MapName, dungeon.Grid);
            }
        }

        private void NotifyPlayerOnRemoval(BasePlayer player, IEnumerable<ActiveDungeon> removedDungeons)
        {
            var list = removedDungeons.ToList();
            if (!list.Any())
            {
                NotifyPlayer(player, "NoInactiveDungeonsToRemove", 0);
                return;
            }
            foreach (var dungeon in list)
            {
                NotifyPlayer(player, "DungeonRemovedDetail", 0, dungeon.TierName, dungeon.Grid);
            }
        }

        private (BuildingGrade.Enum grade, ulong skinID) GetGradeAndSkin(int selection)
        {
            switch (selection)
            {
                case 1:
                    return (BuildingGrade.Enum.Wood, 0);
                case 2:
                    return (BuildingGrade.Enum.Wood, 10232);
                case 3:
                    return (BuildingGrade.Enum.Wood, 2);
                case 4:
                    return (BuildingGrade.Enum.Stone, 0);
                case 5:
                    return (BuildingGrade.Enum.Stone, 10220);
                case 6:
                    return (BuildingGrade.Enum.Stone, 10223);
                case 7:
                    return (BuildingGrade.Enum.Stone, 10225);
                case 8:
                    return (BuildingGrade.Enum.Metal, 0);
                case 9:
                    return (BuildingGrade.Enum.Metal, 10221);
                case 10:
                    return (BuildingGrade.Enum.TopTier, 0);
                case 11:
                    return (BuildingGrade.Enum.Stone, 10326);
                case 12:
                    return (BuildingGrade.Enum.Stone, 10472);
                case 13:
                    return (BuildingGrade.Enum.TopTier, 10430);
                case ClassicDungeonSelection:
                    return (BuildingGrade.Enum.Stone, 0);
                default:
                    return (BuildingGrade.Enum.Stone, 0);
            }
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "Default";
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(s.Where(c => !invalid.Contains(c)).ToArray());
            return string.IsNullOrWhiteSpace(cleaned) ? "Default" : cleaned.Replace(' ', '_');
        }

        private static string ToUiColorString(Color c)
        {
            return $"{c.r:F2} {c.g:F2} {c.b:F2} 1";
        }

        private (Color color, string colorString) GetTierColor(string name)
        {
            if (string.IsNullOrEmpty(name))
                name = "Tier";
            int hash = name.Aggregate(0, (a, c) => unchecked(a * 31 + c));
            float h = (Mathf.Abs(hash) % 360) / 360f;
            float s = 0.6f;
            float v = 0.9f;
            Color col = Color.HSVToRGB(h, s, v);
            string sCol = $"{col.r:F2} {col.g:F2} {col.b:F2} 1";
            return (col, sCol);
        }

        private bool TryReadRawConfig(out JObject raw)
        {
            raw = null;
            try
            {
                raw = Config.ReadObject<JObject>();
                return raw != null;
            }
            catch
            {
                return false;
            }
        }

        private bool TryParseConfigVersion(JObject raw, out VersionNumber version)
        {
            version = new VersionNumber(0, 0, 0);
            if (raw == null)
                return false;
            var v = raw["Version"];
            if (v == null)
                return false;
            if (v.Type == JTokenType.Object)
            {
                var ma = 0;
                var mi = 0;
                var pa = 0;
                int.TryParse(v["Major"] != null ? v["Major"].ToString() : "0", out ma);
                int.TryParse(v["Minor"] != null ? v["Minor"].ToString() : "0", out mi);
                int.TryParse(v["Patch"] != null ? v["Patch"].ToString() : "0", out pa);
                version = new VersionNumber(ma, mi, pa);
                return true;
            }
            if (v.Type == JTokenType.String)
            {
                var parts = v.ToString().Split('.');
                var ma = 0;
                var mi = 0;
                var pa = 0;
                if (parts.Length > 0)
                    int.TryParse(parts[0], out ma);
                if (parts.Length > 1)
                    int.TryParse(parts[1], out mi);
                if (parts.Length > 2)
                    int.TryParse(parts[2], out pa);
                version = new VersionNumber(ma, mi, pa);
                return true;
            }
            return false;
        }

        private bool MigrateTiersObjectToArray(JObject root)
        {
            var tiers = root?["Tiers"];
            if (tiers == null || tiers.Type != JTokenType.Object)
                return false;
            var obj = (JObject)tiers;
            var arr = new JArray();
            foreach (var prop in obj.Properties())
            {
                var item = prop.Value as JObject ?? new JObject();
                if (item["Tier Name"] == null)
                    item["Tier Name"] = prop.Name;
                var enabledToken = item["Enabled"];
                if (enabledToken != null && enabledToken.Type == JTokenType.Boolean)
                {
                    var enabled = enabledToken.Value<bool>();
                    if (item["Enable Auto Spawn"] == null)
                        item["Enable Auto Spawn"] = enabled;
                    if (item["Enable Buy"] == null)
                        item["Enable Buy"] = enabled;
                    item.Remove("Enabled");
                }
                arr.Add(item);
            }
            root["Tiers"] = arr;
            return true;
        }

        private bool MigrateBuildingGradeSelectionPropertyName(JObject root)
        {
            var tiers = root?["Tiers"] as JArray;
            if (tiers == null)
                return false;
            string[] oldKeys =
            {
                "Building Grade - 1(Wood) 2(Legacy Wood) 3(Gingerbread) 4(Stone) 5(Adobe) 6(Brick) 7(Brutalist) 8(Metal) 9(Container) 10(TopTier) 11(Jungle) 12(Crypt) 13(Space Station)",
                "Building Grade - 1(Wood) 2(Legacy Wood) 3(Gingerbread) 4(Stone) 5(Adobe) 6(Brick) 7(Brutalist) 8(Metal) 9(Container) 10(TopTier)",
            };
            bool changed = false;
            foreach (var token in tiers)
            {
                var tier = token as JObject;
                if (tier == null)
                    continue;
                foreach (string oldKey in oldKeys)
                {
                    var selection = tier[oldKey];
                    if (selection == null)
                        continue;
                    if (tier[ConstructionTypeConfigKey] == null)
                        tier[ConstructionTypeConfigKey] = selection;
                    tier.Remove(oldKey);
                    changed = true;
                }
            }
            return changed;
        }

        private bool MigrateDungeonHeightPropertyName(JObject root)
        {
            const string oldKey = "Dungeon Height (Y coordinate for dungeon, max 700)";
            const string newKey = "Dungeon Height (0 = terrain, negative = underground, max 700)";
            var settings = root?["Dungeon Settings"] as JObject;
            if (settings == null || settings[newKey] != null || settings[oldKey] == null)
                return false;
            settings[newKey] = settings[oldKey];
            settings.Remove(oldKey);
            return true;
        }

        private bool MigrateEnemyRewardProperties(JObject root)
        {
            var tiers = root?["Tiers"] as JArray;
            if (tiers == null)
                return false;
            bool changed = false;
            foreach (var token in tiers)
            {
                var tier = token as JObject;
                var rewards = tier?["Reward Config"] as JObject;
                if (rewards == null)
                    continue;
                var config = rewards.ToObject<RewardConfig>();
                foreach (string key in new[] { "Boss Economics Reward", "Animal Economics Reward", "Boss ServerRewards Points", "Animal ServerRewards Points" })
                {
                    if (rewards[key] != null)
                        continue;
                    rewards[key] = key.EndsWith("Economics Reward", StringComparison.Ordinal) ? config.NpcEconomicsReward : config.NpcServerRewards;
                    changed = true;
                }
            }
            return changed;
        }

        private bool MigrateOldWolfPrefabToWolf2(JObject root)
        {
            const string oldPrefab = "assets/rust.ai/agents/wolf/wolf.prefab";
            const string newPrefab = "assets/rust.ai/agents/wolf/wolf2.prefab";
            var tiers = root?["Tiers"] as JArray;
            if (tiers == null)
                return false;
            bool changed = false;
            foreach (var token in tiers)
            {
                var tier = token as JObject;
                var prefabs = tier?["Animal Config"]?["Animal Prefabs"] as JArray;
                if (prefabs == null)
                    continue;
                foreach (var prefabToken in prefabs)
                {
                    var prefab = prefabToken as JObject;
                    if (prefab == null)
                        continue;
                    var pathToken = prefab["Prefab Path"];
                    if (pathToken == null)
                        continue;
                    if (!string.Equals(pathToken.ToString(), oldPrefab, StringComparison.OrdinalIgnoreCase))
                        continue;
                    prefab["Prefab Path"] = newPrefab;
                    changed = true;
                }
            }
            return changed;
        }

        private ScientistNPC CreateScientist(Vector3 pos, Quaternion rot)
        {
            BaseEntity ent = GameManager.server.CreateEntity(ScientistPrefab, pos, rot);
            if (ent == null)
                return null;
            var npc = ent as ScientistNPC;
            if (npc == null)
            {
                ent.Kill();
                return null;
            }
            npc.OwnerID = OwnerID;
            npc.Spawn();
            return npc;
        }

        private void ApplyNpcHealth(ScientistNPC npc, float health)
        {
            if (npc == null || npc.IsDestroyed)
                return;
            try
            {
                npc.startHealth = health;
                npc.InitializeHealth(health, health);
                npc.SendNetworkUpdateImmediate();
            }
            catch { }
        }

        private void ApplyNpcConfig(ScientistNPC npc, NpcConfig cfg)
        {
            if (npc == null || cfg == null)
                return;
            if (!string.IsNullOrEmpty(cfg.Name))
            {
                var data = npc.GetComponent<DungeonNpcName>();
                if (data == null)
                    data = npc.gameObject.AddComponent<DungeonNpcName>();
                data.CustomName = cfg.Name;
                try
                {
                    npc.SendNetworkUpdateImmediate();
                }
                catch { }
            }
            ApplyNpcHealth(npc, cfg.Health);
            timer.Once(0.25f, () => ApplyNpcHealth(npc, cfg.Health));
            try
            {
                npc.damageScale = cfg.DamageScale;
            }
            catch { }
            try
            {
                npc.inventory?.containerWear?.Clear();
                npc.inventory?.containerBelt?.Clear();
                npc.inventory?.containerMain?.Clear();
            }
            catch { }
            if (cfg.WearItems?.Items != null)
            {
                foreach (var w in cfg.WearItems.Items)
                {
                    var def = ItemManager.FindItemDefinition(w.ShortName);
                    if (def == null)
                        continue;
                    var item = ItemManager.Create(def, 1, w.SkinID);
                    item?.MoveToContainer(npc.inventory.containerWear);
                }
            }
            if (cfg.BeltItems?.Items != null)
            {
                foreach (var b in cfg.BeltItems.Items)
                {
                    if (b.Amount <= 0)
                        continue;
                    var def = ItemManager.FindItemDefinition(b.ShortName);
                    if (def == null)
                        continue;
                    var item = ItemManager.Create(def, b.Amount, b.SkinID);
                    if (item == null)
                        continue;
                    if (b.Mods != null && b.Mods.Count > 0 && item.contents != null)
                    {
                        foreach (var modShort in b.Mods)
                        {
                            var modDef = ItemManager.FindItemDefinition(modShort);
                            if (modDef == null)
                                continue;
                            var modItem = ItemManager.Create(modDef, 1);
                            modItem?.MoveToContainer(item.contents);
                        }
                    }
                    item.MoveToContainer(npc.inventory.containerBelt);
                }
            }
            if (cfg.DisableRadio)
            {
                try
                {
                    npc.CancelInvoke(npc.PlayRadioChatter);
                    npc.RadioChatterEffects = Array.Empty<GameObjectRef>();
                    npc.DeathEffects = Array.Empty<GameObjectRef>();
                }
                catch { }
            }
            try
            {
                npc.SendNetworkUpdateImmediate();
            }
            catch { }
        }
        #endregion

        #region Economy
        private bool TryGetBuyCooldown(ulong userId, string tierName, out DateTime until)
        {
            until = DateTime.MinValue;
            if (string.IsNullOrEmpty(tierName))
                return false;
            if (!_buyCooldowns.TryGetValue(userId, out var perTier))
                return false;
            if (perTier == null)
                return false;
            return perTier.TryGetValue(tierName, out until);
        }

        private void SetBuyCooldown(ulong userId, string tierName, int seconds)
        {
            if (string.IsNullOrEmpty(tierName) || seconds <= 0)
                return;
            if (!_buyCooldowns.TryGetValue(userId, out var perTier) || perTier == null)
            {
                perTier = new Dictionary<string, DateTime>();
                _buyCooldowns[userId] = perTier;
            }
            perTier[tierName] = DateTime.UtcNow.AddSeconds(seconds);
        }

        private void ProcessBuyDungeon(BasePlayer player, string tierName)
        {
            if (player == null || !player.userID.IsSteamId())
                return;
            if (!permission.UserHasPermission(player.UserIDString, PermissionBuy) && !IsAdmin(player) && !permission.UserHasPermission(player.UserIDString, PermissionAdmin))
            {
                NotifyPlayer(player, "NoAccess", 0);
                return;
            }
            if (!_config.EnablePurchases)
            {
                NotifyPlayer(player, "PurchasesDisabled", 0);
                return;
            }
            var tier = FindTierByName(tierName);
            if (tier == null || !tier.EnableBuy)
            {
                NotifyPlayer(player, "InvalidTierMessage", 0, tierName, GetAvailableTiers());
                return;
            }
            if (!IsTierUnlockedByWipeTime(tier))
            {
                NotifyPlayer(player, "ThisTierDisabled", 0);
                return;
            }
            if (!HasTierPermission(player, tier))
            {
                NotifyPlayer(player, "NoTierPermission", 0, tier.TierName, GetTierPermission(tier));
                return;
            }
            if (!permission.UserHasPermission(player.UserIDString, PermissionBypassCooldown) && TryGetBuyCooldown(player.userID, tier.TierName, out DateTime until) && DateTime.UtcNow < until)
            {
                int remaining = (int)(until - DateTime.UtcNow).TotalSeconds;
                NotifyPlayer(player, "DungeonCooldownMessage", 0, remaining);
                return;
            }
            if (
                _activeDungeons.Any(d => d.OwnerID == player.userID)
                || _currentDungeonRequest?.Player?.userID == player.userID
                || _dungeonQueue.Any(r => r.Player?.userID == player.userID)
                || _dungeonFailedBuyQueue.Any(r => r.Player?.userID == player.userID)
            )
            {
                NotifyPlayer(player, "AlreadyOwnsDungeonMessage", 0);
                return;
            }
            int activeBuySameTier = _activeDungeons.Count(d => d.Origin == DungeonOrigin.Buy && d.TierConfig == tier);
            if (tier.MaxActiveBuy > 0 && activeBuySameTier >= tier.MaxActiveBuy)
            {
                NotifyPlayer(player, "MaxActiveDungeonsReached", 0);
                return;
            }
            Vector3 preselectedCustomPortalPosition = Vector3.zero;
            bool usePreselectedCustomPortal = false;
            var portalSelectionRequest = new DungeonRequest { Origin = DungeonOrigin.Buy };
            bool customBuyLocationsConfigured;
            if (TrySelectCustomPortalPosition(portalSelectionRequest, out var availableCustomPortalPosition, out customBuyLocationsConfigured))
            {
                preselectedCustomPortalPosition = availableCustomPortalPosition;
                usePreselectedCustomPortal = true;
            }
            else if (!CanFallbackToRandomPortalPosition(DungeonOrigin.Buy, customBuyLocationsConfigured))
            {
                NotifyPlayer(player, "NoBuyPortalLocationAvailable", 0);
                return;
            }
            int cost = HasFreePurchase(player) ? 0 : tier.BuyCost;
            if (cost > 0)
            {
                if (_config.EconomyPlugin == 3)
                {
                    if (!CheckAndRemoveBuyItems(player, cost))
                    {
                        NotifyPlayer(player, "InsufficientFundsMessage", 0, tierName, cost);
                        return;
                    }
                }
                else
                {
                    if (!CheckBalance(player, cost))
                    {
                        NotifyPlayer(player, "InsufficientFundsMessage", 0, tierName, cost);
                        return;
                    }
                    if (!WithdrawFunds(player, cost))
                    {
                        NotifyPlayer(player, "WithdrawFailedMessage", 0, tierName);
                        return;
                    }
                }
            }
            EnqueueDungeonRequest(player, tier, true, usePreselectedCustomPortal ? preselectedCustomPortalPosition : Vector3.zero);
            NotifyPlayer(player, "PurchaseSuccessMessage", 0, tier.MapName);
        }

        private bool HasFreePurchase(BasePlayer player)
        {
            return player != null && permission.UserHasPermission(player.UserIDString, PermissionFree);
        }

        private bool CheckAndRemoveBuyItems(BasePlayer player, int cost)
        {
            var requiredItem = new ResourceItem
            {
                Shortname = _config.GetEconomyItemName(),
                SkinID = _config.GetEconomyItemSkinID(),
                Amount = cost,
            };
            var allItems = Pool.Get<List<Item>>();
            try
            {
                player.inventory.GetAllItems(allItems);
                if (!HasRequiredBuyItem(allItems, requiredItem))
                    return false;
                RemoveRequiredBuyItems(allItems, requiredItem);
                return true;
            }
            finally
            {
                Pool.FreeUnmanaged(ref allItems);
            }
        }

        private bool HasRequiredBuyItem(List<Item> allItems, ResourceItem requiredItem)
        {
            if (requiredItem == null || string.IsNullOrEmpty(requiredItem.Shortname) || requiredItem.Amount <= 0)
                return false;
            int amount = 0;
            foreach (var item in allItems)
            {
                if (!IsRequiredBuyItem(item, requiredItem))
                    continue;
                amount += item.amount;
                if (amount >= requiredItem.Amount)
                    return true;
            }
            return false;
        }

        private void RemoveRequiredBuyItems(List<Item> allItems, ResourceItem requiredItem)
        {
            int amountToRemove = requiredItem.Amount;
            foreach (var item in allItems)
            {
                if (amountToRemove <= 0)
                    break;
                if (!IsRequiredBuyItem(item, requiredItem))
                    continue;
                if (item.amount <= amountToRemove)
                {
                    amountToRemove -= item.amount;
                    item.Remove();
                    continue;
                }
                item.amount -= amountToRemove;
                item.MarkDirty();
                amountToRemove = 0;
            }
        }

        private bool IsRequiredBuyItem(Item item, ResourceItem requiredItem)
        {
            if (item == null || item.info == null || requiredItem == null)
                return false;
            if (!string.Equals(item.info.shortname, requiredItem.Shortname, StringComparison.OrdinalIgnoreCase))
                return false;
            if (requiredItem.SkinID > 0 && item.skin != requiredItem.SkinID)
                return false;
            return true;
        }

        private bool CheckBalance(BasePlayer player, int cost)
        {
            return _config.EconomyPlugin switch
            {
                1 => CheckEconomicsBalance(player, cost),
                2 => CheckServerRewardsBalance(player, cost),
                _ => false,
            };
        }

        private bool WithdrawFunds(BasePlayer player, int cost)
        {
            return _config.EconomyPlugin switch
            {
                1 => WithdrawEconomicsFunds(player, cost),
                2 => WithdrawServerRewardsFunds(player, cost),
                _ => false,
            };
        }

        private bool CheckEconomicsBalance(BasePlayer player, int cost)
        {
            if (Economics == null)
            {
                PrintWarning("Economics plugin is not loaded.");
                return false;
            }
            object balanceObj = Economics.CallHook("Balance", player.userID);
            if (balanceObj == null || !double.TryParse(balanceObj.ToString(), out double balance))
                return false;
            return balance >= cost;
        }

        private bool CheckServerRewardsBalance(BasePlayer player, int cost)
        {
            if (ServerRewards == null)
            {
                PrintWarning("ServerRewards plugin is not loaded.");
                return false;
            }
            object balanceObj = ServerRewards.CallHook("CheckPoints", player.userID);
            if (balanceObj == null || !int.TryParse(balanceObj.ToString(), out int balance))
                return false;
            return balance >= cost;
        }

        private bool WithdrawEconomicsFunds(BasePlayer player, int cost)
        {
            if (Economics == null)
            {
                PrintWarning("Economics plugin is not loaded.");
                return false;
            }
            object result = Economics.CallHook("Withdraw", player.userID, (double)cost);
            return result != null && Convert.ToBoolean(result);
        }

        private bool WithdrawServerRewardsFunds(BasePlayer player, int cost)
        {
            if (ServerRewards == null)
            {
                PrintWarning("ServerRewards plugin is not loaded.");
                return false;
            }
            object result = ServerRewards.CallHook("TakePoints", player.userID, cost);
            return result != null && Convert.ToBoolean(result);
        }
        #endregion

        #region UI
        private static Func<BasePlayer, HashSet<string>> CreateActiveUiPanelLookup()
        {
            var method = typeof(CuiHelper).GetMethod("GetActivePanelList", new[] { typeof(BasePlayer) });
            if (method == null || !method.IsStatic || method.ReturnType != typeof(HashSet<string>))
                return null;
            return (Func<BasePlayer, HashSet<string>>)Delegate.CreateDelegate(typeof(Func<BasePlayer, HashSet<string>>), method);
        }

        private void OpenEventUI(BasePlayer player, ActiveDungeon dungeon)
        {
            if (player == null || !player.IsConnected)
                return;
            double timeLeft;
            string counterText;
            if (dungeon.HasBeenCleared && dungeon.DestructionCountdownStart.HasValue)
            {
                double elapsed = (DateTime.UtcNow - dungeon.DestructionCountdownStart.Value).TotalSeconds;
                timeLeft = dungeon.DestructionDelay - elapsed;
                if (timeLeft < 0)
                    timeLeft = 0;
                counterText = FormatTime(timeLeft);
            }
            else
            {
                double elapsed = dungeon.ActivationTime.HasValue ? (DateTime.UtcNow - dungeon.ActivationTime.Value).TotalSeconds : 0;
                timeLeft = dungeon.TierConfig.MaxDungeonLifetime - elapsed;
                if (timeLeft < 0)
                    timeLeft = 0;
                counterText = FormatTime(timeLeft);
            }
            if (dungeon.UiCountsFrame != Time.frameCount)
            {
                dungeon.UiNpcCount = 0;
                foreach (var npc in dungeon.Npcs)
                {
                    if (IsDungeonNpcAlive(npc))
                        dungeon.UiNpcCount++;
                }
                dungeon.UiBoxCount = 0;
                dungeon.UiTurretCount = 0;
                foreach (var entity in dungeon.Entities)
                {
                    if (entity == null || !entity.IsValid() || entity.IsDestroyed)
                        continue;
                    if (entity is StorageContainer)
                        dungeon.UiBoxCount++;
                    if (entity is AutoTurret)
                        dungeon.UiTurretCount++;
                }
                dungeon.UiCountsFrame = Time.frameCount;
            }
            int npcCount = dungeon.UiNpcCount;
            int boxCount = dungeon.UiBoxCount;
            int turretCount = dungeon.UiTurretCount;
            if (
                _eventUiStates.TryGetValue(player.userID, out var state)
                && state.DungeonId == dungeon.UniqueId
                && state.Cleared == dungeon.HasBeenCleared
                && (_getActiveUiPanels == null || _getActiveUiPanels(player).Contains(UiEventPanel))
            )
            {
                if (state.Counter == counterText && state.Npcs == npcCount && state.Boxes == boxCount && state.Turrets == turretCount)
                    return;
                var updates = new CuiElementContainer();
                if (state.Counter != counterText)
                    AddEventTextUpdate(updates, 1, counterText);
                if (state.Npcs != npcCount)
                    AddEventTextUpdate(updates, 2, npcCount.ToString());
                if (state.Boxes != boxCount)
                    AddEventTextUpdate(updates, 3, boxCount.ToString());
                if (state.Turrets != turretCount)
                    AddEventTextUpdate(updates, 4, turretCount.ToString());
                if (updates.Count > 0 && !CuiHelper.AddUi(player, CuiHelper.ToJson(updates)))
                    return;
                state.Counter = counterText;
                state.Npcs = npcCount;
                state.Boxes = boxCount;
                state.Turrets = turretCount;
                return;
            }
            var infos = new List<IconInfo>
            {
                new IconInfo
                {
                    Sprite = "assets/icons/stopwatch.png",
                    Color = dungeon.HasBeenCleared ? "1 1 0 1" : "1 1 1 1",
                    Text = counterText,
                    X = 11,
                    Y = 4,
                    Width = 18,
                    Height = 18,
                },
                new IconInfo
                {
                    Sprite = "assets/icons/clan.png",
                    Color = dungeon.HasBeenCleared ? "1 1 0 1" : "1 1 1 1",
                    Text = npcCount.ToString(),
                    X = 9,
                    Y = 1.8f,
                    Width = 24,
                    Height = 24,
                },
                new IconInfo
                {
                    Sprite = "assets/icons/loot.png",
                    Color = dungeon.HasBeenCleared ? "1 1 0 1" : "1 1 1 1",
                    Text = boxCount.ToString(),
                    X = 9,
                    Y = 1,
                    Width = 24,
                    Height = 24,
                },
                new IconInfo
                {
                    Sprite = "assets/prefabs/npc/autoturret/autoturret.png",
                    Color = dungeon.HasBeenCleared ? "1 1 0 1" : "1 1 1 1",
                    Text = turretCount.ToString(),
                    X = 9,
                    Y = 4,
                    Width = 21,
                    Height = 17,
                },
            };
            if (!CreateEventUI(player, dungeon, infos))
                return;
            _eventUiStates[player.userID] = new EventUiState
            {
                DungeonId = dungeon.UniqueId,
                Cleared = dungeon.HasBeenCleared,
                Counter = counterText,
                Npcs = npcCount,
                Boxes = boxCount,
                Turrets = turretCount,
            };
        }

        private void AddEventTextUpdate(CuiElementContainer updates, int index, string text)
        {
            updates.Add(
                new CuiElement
                {
                    Name = $"Info_{index}_Text",
                    Parent = $"Info_{index}_Panel",
                    Update = true,
                    Components =
                    {
                        new CuiTextComponent
                        {
                            Text = text,
                            FontSize = 10,
                            Align = TextAnchor.MiddleCenter,
                            Color = "1 1 1 1",
                        },
                    },
                }
            );
        }

        private bool CreateEventUI(BasePlayer player, ActiveDungeon dungeon, List<IconInfo> infos)
        {
            string color = dungeon.HasBeenCleared ? "0.6 0.6 0 1" : "0.6 0 0 1";
            float panelWidth = 85f;
            float panelHeight = 25f;
            float panelSpacing = 95f;
            float totalWidth = panelWidth + panelSpacing * (infos.Count - 1);
            float border = totalWidth / 2f;
            CuiHelper.DestroyUi(player, UiEventPanel);
            var container = new CuiElementContainer();
            container.Add(
                new CuiPanel
                {
                    Image = { Color = "0 0 0 0" },
                    RectTransform =
                    {
                        AnchorMin = "0.5 1",
                        AnchorMax = "0.5 1",
                        OffsetMin = $"-{border} -56",
                        OffsetMax = $"{border} -36",
                    },
                    CursorEnabled = false,
                },
                "Under",
                UiEventPanel
            );
            int i = 0;
            foreach (var info in infos)
            {
                i++;
                float panelX = panelSpacing * (i - 1);
                container.Add(
                    new CuiElement
                    {
                        Name = $"Info_{i}_Panel",
                        Parent = UiEventPanel,
                        Components =
                        {
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "0 0",
                                OffsetMin = $"{panelX} 0",
                                OffsetMax = $"{panelX + panelWidth} {panelHeight}",
                            },
                        },
                    }
                );
                container.Add(
                    new CuiElement
                    {
                        Name = $"Info_{i}_Red",
                        Parent = $"Info_{i}_Panel",
                        Components =
                        {
                            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" },
                            new CuiImageComponent { Color = color, Material = "assets/content/ui/namefontmaterial.mat" },
                        },
                    }
                );
                container.Add(
                    new CuiElement
                    {
                        Name = $"Info_{i}_Sprite",
                        Parent = $"Info_{i}_Panel",
                        Components =
                        {
                            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" },
                            new CuiImageComponent { Color = "0 0 0 0.9", Sprite = "assets/content/ui/ui.background.transparent.radial.psd" },
                        },
                    }
                );
                container.Add(
                    new CuiElement
                    {
                        Name = $"Info_{i}_Icon",
                        Parent = $"Info_{i}_Panel",
                        Components =
                        {
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "0 0",
                                OffsetMin = $"{info.X} {info.Y}",
                                OffsetMax = $"{info.X + info.Width} {info.Y + info.Height}",
                            },
                            new CuiImageComponent
                            {
                                Sprite = info.Sprite,
                                Color = info.Color,
                                Material = "assets/icons/iconmaterial.mat",
                            },
                        },
                    }
                );
                container.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = info.Text,
                            FontSize = 10,
                            Align = TextAnchor.MiddleCenter,
                            Color = "1 1 1 1",
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "0 0",
                            OffsetMin = "20 6",
                            OffsetMax = "100 20",
                        },
                    },
                    $"Info_{i}_Panel",
                    $"Info_{i}_Text"
                );
            }
            return CuiHelper.AddUi(player, container);
        }

        private bool TryGetBuyCostIcon(out int itemId, out ulong skinId)
        {
            itemId = 0;
            skinId = 0UL;
            if (_config == null || _config.EconomyPlugin != 3)
                return false;
            string shortname = _config.GetEconomyItemName();
            if (string.IsNullOrEmpty(shortname))
                return false;
            var itemDefinition = ItemManager.FindItemDefinition(shortname);
            if (itemDefinition == null)
                return false;
            itemId = itemDefinition.itemid;
            skinId = _config.GetEconomyItemSkinID();
            return true;
        }

        private void OpenBuyDungeonGUI(BasePlayer player)
        {
            if (player == null)
                return;
            DestroyUi(player);
            var elements = new CuiElementContainer();
            elements.Add(
                new CuiPanel
                {
                    Image = { Color = "0.1 0.1 0.1 0.8", Material = "assets/content/ui/uibackgroundblur.mat" },
                    RectTransform = { AnchorMin = "0.25 0.2", AnchorMax = "0.75 0.8" },
                    CursorEnabled = true,
                },
                "Overlay",
                UiMainPanel
            );
            elements.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = Msg(player.UserIDString, "DungeonBuyTitle"),
                        FontSize = 22,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1",
                    },
                    RectTransform = { AnchorMin = "0 0.92", AnchorMax = "1 1" },
                },
                UiMainPanel,
                "DungeonBuyTitle"
            );
            var visibleTiers = new List<KeyValuePair<DungeonTierConfig, TierInfo>>();
            foreach (var kvp in _tierInfoMap)
            {
                if (kvp.Key != null && kvp.Key.EnableBuy && IsTierUnlockedByWipeTime(kvp.Key) && HasTierPermission(player, kvp.Key))
                    visibleTiers.Add(kvp);
            }
            const int rowH = 40;
            const int gap = 4;
            int totalH = visibleTiers.Count * (rowH + gap);
            const int viewportH = 352;
            bool needScroll = totalH > viewportH;
            bool freePurchase = HasFreePurchase(player);
            int buyCostItemId = 0;
            ulong buyCostSkinId = 0UL;
            bool showBuyCostIcon = !freePurchase && TryGetBuyCostIcon(out buyCostItemId, out buyCostSkinId);
            string body = UiMainPanel + "_viewport";
            elements.Add(new CuiPanel { Image = { Color = "0 0 0 0" }, RectTransform = { AnchorMin = "0.05 0.08", AnchorMax = "0.95 0.88" } }, UiMainPanel, body);
            string scroll = body + "_scroll";
            var scrollEl = new CuiElement
            {
                Parent = body,
                Name = scroll,
                Components =
                {
                    new CuiImageComponent { Color = "0 0 0 0" },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1",
                        OffsetMin = "0 0",
                        OffsetMax = "0 0",
                    },
                },
            };
            if (needScroll)
            {
                scrollEl.Components.Add(
                    new CuiScrollViewComponent
                    {
                        ContentTransform = new CuiRectTransform
                        {
                            AnchorMin = "0 1",
                            AnchorMax = "1 1",
                            OffsetMin = $"0 -{totalH}",
                            OffsetMax = "-16 0",
                        },
                        Vertical = true,
                        Horizontal = false,
                        ScrollSensitivity = 10f,
                        Elasticity = 0.05f,
                        VerticalScrollbar = new CuiScrollbar { HandleColor = "1 1 1 1", Size = 8 },
                    }
                );
            }
            elements.Add(scrollEl);
            int y = 0;
            int index = 0;
            foreach (var kvp in visibleTiers)
            {
                var tierInfo = kvp.Value;
                string rowId = $"{scroll}_row_{index}";
                string buyText =
                    freePurchase || tierInfo.BuyCost <= 0
                        ? Msg(player.UserIDString, "BuyButtonFreeWithName", tierInfo.MapName)
                        : Msg(player.UserIDString, "BuyButtonWithName", tierInfo.MapName, tierInfo.BuyCost, _config.GetEconomyDisplayName());
                float iconOffset = Mathf.Clamp((buyText.Length * 4.6f) - 20f, 90f, 330f);
                elements.Add(
                    new CuiPanel
                    {
                        Image = { Color = tierInfo.ColorString, Material = "assets/content/ui/namefontmaterial.mat" },
                        RectTransform =
                        {
                            AnchorMin = "0 1",
                            AnchorMax = "1 1",
                            OffsetMin = $"4 {-y - rowH + 4}",
                            OffsetMax = $"-4 {-y - 4}",
                        },
                    },
                    scroll,
                    rowId
                );
                elements.Add(
                    new CuiElement
                    {
                        Parent = rowId,
                        Name = $"{rowId}_text",
                        Components =
                        {
                            new CuiTextComponent
                            {
                                Text = buyText,
                                FontSize = 18,
                                Align = TextAnchor.MiddleCenter,
                                Color = "1 1 1 1",
                            },
                            new CuiOutlineComponent { Color = "0 0 0 1", Distance = "1 -1" },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "1 1",
                                OffsetMin = showBuyCostIcon ? "-18 0" : "0 0",
                                OffsetMax = showBuyCostIcon ? "-18 0" : "0 0",
                            },
                        },
                    }
                );
                if (showBuyCostIcon)
                {
                    elements.Add(
                        new CuiElement
                        {
                            Parent = rowId,
                            Name = $"{rowId}_cost_icon",
                            Components =
                            {
                                new CuiImageComponent
                                {
                                    ItemId = buyCostItemId,
                                    SkinId = buyCostSkinId,
                                    Color = "1 1 1 1",
                                },
                                new CuiRectTransformComponent
                                {
                                    AnchorMin = "0.5 0.5",
                                    AnchorMax = "0.5 0.5",
                                    OffsetMin = $"{iconOffset - 12f} -12",
                                    OffsetMax = $"{iconOffset + 12f} 12",
                                },
                            },
                        }
                    );
                }
                elements.Add(
                    new CuiButton
                    {
                        Button =
                        {
                            Color = "0 0 0 0",
                            Command = $"{_config?.ConsoleCommands?.BuyDungeon ?? "buydungeon"} {tierInfo.Name} {player.UserIDString}",
                            Close = UiMainPanel,
                        },
                        Text =
                        {
                            Text = string.Empty,
                            FontSize = 1,
                            Align = TextAnchor.MiddleCenter,
                            Color = "0 0 0 0",
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "1 1",
                            OffsetMin = "0 0",
                            OffsetMax = "0 0",
                        },
                    },
                    rowId,
                    $"{rowId}_click"
                );
                y += rowH + gap;
                index++;
            }
            elements.Add(
                new CuiButton
                {
                    Button =
                    {
                        Color = "1 0 0 1",
                        Command = UiCommandClose,
                        Close = UiMainPanel,
                    },
                    RectTransform = { AnchorMin = "0.965 0.95", AnchorMax = "1 1" },
                    Text =
                    {
                        Text = "✖",
                        FontSize = 14,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1",
                    },
                },
                UiMainPanel
            );
            CuiHelper.AddUi(player, elements);
        }

        private void DestroyUi(BasePlayer player)
        {
            if (player == null)
                return;
            CuiHelper.DestroyUi(player, UiMainPanel);
        }

        private void DestroyEventUi(BasePlayer player)
        {
            if (player == null)
                return;
            _eventUiStates.Remove(player.userID);
            CuiHelper.DestroyUi(player, UiEventPanel);
        }

        private void DestroyAllUis()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                DestroyUi(player);
                DestroyEventUi(player);
            }
        }
        #endregion

        #region Dungeon Spawn
        private void SaveDungeonEntities(string dungeonId, List<ulong> entityIds)
        {
            var file = new Oxide.Core.Configuration.DynamicConfigFile(GetDungeonEntitiesFilePath(dungeonId));
            file.WriteObject(entityIds);
        }

        private List<ulong> LoadDungeonEntities(string dungeonId)
        {
            var file = new Oxide.Core.Configuration.DynamicConfigFile(GetDungeonEntitiesFilePath(dungeonId));
            if (!file.Exists())
                return new List<ulong>();
            return file.ReadObject<List<ulong>>() ?? new List<ulong>();
        }

        private string GetDungeonEntitiesFilePath(string dungeonId)
        {
            return Path.Combine(Interface.Oxide.DataFileSystem.Directory, $"{Name}_Cache_Data", $"{dungeonId}.json");
        }

        private void DeleteDungeonEntitiesFile(string dungeonId)
        {
            string filePath = GetDungeonEntitiesFilePath(dungeonId);
            if (File.Exists(filePath))
            {
                try
                {
                    File.Delete(filePath);
                }
                catch (Exception ex)
                {
                    PrintError("Error deleting dungeon file: " + ex.Message);
                }
            }
        }

        private void RegisterSpawnedEntityForDungeon(BaseEntity entity, string dungeonId)
        {
            if (entity == null || entity.net == null || string.IsNullOrEmpty(dungeonId))
                return;
            ulong id = entity.net.ID.Value;
            if (!_dungeonEntityIdCache.TryGetValue(dungeonId, out var ids))
            {
                ids = new HashSet<ulong>();
                _dungeonEntityIdCache[dungeonId] = ids;
            }
            if (ids.Add(id))
                MarkDungeonEntityCacheDirty(dungeonId);
            if (_dungeonByUniqueId.TryGetValue(dungeonId, out var dungeon))
                IndexDungeonEntity(entity, dungeon);
        }

        private void MarkDungeonEntityCacheDirty(string dungeonId)
        {
            if (_isUnloading)
                return;
            _dirtyDungeonEntityCaches.Add(dungeonId);
            if (_entityCacheFlushTimer == null)
                _entityCacheFlushTimer = timer.Once(DungeonEntityCacheFlushInterval, FlushDirtyDungeonEntityCaches);
        }

        private void FlushDirtyDungeonEntityCaches()
        {
            _entityCacheFlushTimer = null;
            var dirty = Pool.Get<List<string>>();
            try
            {
                dirty.AddRange(_dirtyDungeonEntityCaches);
                foreach (string dungeonId in dirty)
                    FlushDungeonEntityCache(dungeonId);
            }
            finally
            {
                Pool.FreeUnmanaged(ref dirty);
            }
            if (!_isUnloading && _dirtyDungeonEntityCaches.Count > 0 && _entityCacheFlushTimer == null)
                _entityCacheFlushTimer = timer.Once(10f, FlushDirtyDungeonEntityCaches);
        }

        private void FlushDungeonEntityCache(string dungeonId)
        {
            if (string.IsNullOrEmpty(dungeonId) || !_dirtyDungeonEntityCaches.Contains(dungeonId))
                return;
            try
            {
                if (_dungeonEntityIdCache.TryGetValue(dungeonId, out var ids))
                    SaveDungeonEntities(dungeonId, ids.ToList());
                _dirtyDungeonEntityCaches.Remove(dungeonId);
            }
            catch (Exception ex)
            {
                PrintError($"Failed to save dungeon entity cache {dungeonId}: {ex.Message}");
                if (!_isUnloading && _entityCacheFlushTimer == null)
                    _entityCacheFlushTimer = timer.Once(10f, FlushDirtyDungeonEntityCaches);
            }
        }

        private void UnregisterSpawnedEntityForDungeon(BaseEntity entity, string dungeonId)
        {
            if (entity == null || entity.net == null || string.IsNullOrEmpty(dungeonId))
                return;
            ulong id = entity.net.ID.Value;
            _dungeonByEntityId.Remove(id);
            if (_dungeonByUniqueId.TryGetValue(dungeonId, out var dungeon))
                dungeon.IndexedEntityIds.Remove(id);
            if (_dungeonEntityIdCache.TryGetValue(dungeonId, out var ids))
            {
                if (ids.Remove(id))
                    MarkDungeonEntityCacheDirty(dungeonId);
            }
        }

        private void IndexDungeonEntity(BaseEntity entity, ActiveDungeon dungeon)
        {
            if (entity?.net == null || dungeon == null)
                return;
            _dungeonByEntityId[entity.net.ID.Value] = dungeon;
            dungeon.IndexedEntityIds.Add(entity.net.ID.Value);
        }

        private void RemoveDungeonEntityIndex(ActiveDungeon dungeon)
        {
            if (dungeon == null)
                return;
            foreach (ulong id in dungeon.IndexedEntityIds)
                _dungeonByEntityId.Remove(id);
            dungeon.IndexedEntityIds.Clear();
            dungeon.BuildingBlocksByPosition.Clear();
            if (!string.IsNullOrEmpty(dungeon.UniqueId))
            {
                _dirtyDungeonEntityCaches.Remove(dungeon.UniqueId);
                _dungeonEntityIdCache.Remove(dungeon.UniqueId);
                _dungeonByUniqueId.Remove(dungeon.UniqueId);
            }
        }

        private void KillDungeonEntity(BaseEntity entity)
        {
            if (entity == null || entity.IsDestroyed)
                return;
            try
            {
                entity.CancelInvoke();
            }
            catch { }
            try
            {
                entity.SetParent(null, true, true);
            }
            catch { }
            entity.Kill(BaseNetworkable.DestroyMode.None);
        }

        private IEnumerator RemoveEntitiesForDungeon(string dungeonId, HashSet<ulong> killedIds = null)
        {
            if (string.IsNullOrEmpty(dungeonId))
                yield break;
            bool hasMemoryCache = _dungeonEntityIdCache.TryGetValue(dungeonId, out var cachedIds);
            var ids = hasMemoryCache ? cachedIds.ToList() : LoadDungeonEntities(dungeonId);
            int count = 0;
            int batch = Mathf.Clamp(Performance.CleanupEntitiesPerBatch, 1, 250);
            foreach (ulong id in ids)
            {
                if (killedIds != null && !killedIds.Add(id))
                    continue;
                var entity = BaseNetworkable.serverEntities.Find(new NetworkableId(id)) as BaseEntity;
                if (entity != null && !entity.IsDestroyed && (hasMemoryCache || entity.OwnerID == OwnerID))
                    KillDungeonEntity(entity);
                _dungeonByEntityId.Remove(id);
                if (++count >= batch)
                {
                    count = 0;
                    yield return _cleanupBatchDelay;
                }
            }
            _dungeonEntityIdCache.Remove(dungeonId);
            _dirtyDungeonEntityCaches.Remove(dungeonId);
            DeleteDungeonEntitiesFile(dungeonId);
        }

        private IEnumerator RemoveLeftoverDungeonFiles()
        {
            string folder = Path.Combine(Interface.Oxide.DataFileSystem.Directory, $"{Name}_Cache_Data");
            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.GetFiles(folder, "*.json"))
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    IEnumerator removal = RemoveEntitiesForDungeon(fileName);
                    while (true)
                    {
                        bool hasNext;
                        try
                        {
                            hasNext = removal.MoveNext();
                        }
                        catch (Exception ex)
                        {
                            PrintError($"Failed to clean cached dungeon {fileName}: {ex.Message}");
                            break;
                        }
                        if (!hasNext)
                            break;
                        yield return removal.Current;
                    }
                }
            }
            yield return CleanupExistingEntities();
        }

        private bool TryGetDungeonFloorPoint(ActiveDungeon dungeon, Vector3 position, out Vector3 floor)
        {
            floor = position;
            if (dungeon.Foundations.Count == 0)
                return false;
            Vector3 origin = dungeon.Foundations[0];
            var tile = new Vector3(
                origin.x + Mathf.Round((position.x - origin.x) / SubCellSpacing) * SubCellSpacing,
                origin.y,
                origin.z + Mathf.Round((position.z - origin.z) / SubCellSpacing) * SubCellSpacing
            );
            if (!dungeon.FoundationKeys.Contains(PositionKey(tile, 2)))
                return false;
            if (IsClassicDungeon(dungeon))
                return TryGetRockFloorPoint(position, dungeon, out floor);
            Vector3 rayOrigin = new Vector3(position.x, origin.y + 1.5f, position.z);
            float closest = 4f;
            bool found = false;
            foreach (var hit in Physics.RaycastAll(rayOrigin, Vector3.down, 4f, Layers.Mask.Construction, QueryTriggerInteraction.Ignore))
            {
                var entity = hit.collider.ToBaseEntity();
                if (entity == null || entity.IsDestroyed || !dungeon.Entities.Contains(entity) || entity.PrefabName != FoundationPrefab || hit.distance >= closest)
                    continue;
                closest = hit.distance;
                floor = hit.point;
                found = true;
            }
            return found;
        }

        private static Bounds GetPortalPlacementBounds(BasePortal portal)
        {
            Bounds bounds = portal.bounds;
            foreach (Collider collider in portal.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled || collider.isTrigger)
                    continue;
                Bounds worldBounds = collider.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = worldBounds.center + Vector3.Scale(worldBounds.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    bounds.Encapsulate(portal.transform.InverseTransformPoint(corner));
                }
            }
            bounds.Expand(new Vector3(0.3f, 0.1f, 0.3f));
            return bounds;
        }

        private static bool IsPortalSpaceClear(BasePortal portal, Vector3 center, Vector3 extents, Quaternion rotation)
        {
            int mask = Physics.AllLayers & ~Layers.Mask.Terrain;
            foreach (Collider collider in Physics.OverlapBox(center, extents, rotation, mask, QueryTriggerInteraction.Ignore))
            {
                if (collider.transform.IsChildOf(portal.transform))
                    continue;
                return false;
            }
            return true;
        }

        private bool IsPortalFootprintSupported(ActiveDungeon dungeon, Vector3 floor, Quaternion rotation, Vector3 localCenter, Vector3 extents)
        {
            for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++)
            {
                Vector3 sample = floor + rotation * new Vector3(localCenter.x + x * extents.x, 0f, localCenter.z + z * extents.z);
                if (!TryGetDungeonFloorPoint(dungeon, sample, out Vector3 support) || Mathf.Abs(support.y - floor.y) > 0.25f)
                    return false;
            }
            return true;
        }

        private static bool IsPortalApproachVisible(BasePortal portal, Vector3 playerPosition, Vector3 approach)
        {
            Vector3 from = playerPosition + Vector3.up * 1.2f;
            Vector3 to = approach + Vector3.up * 1.2f;
            Vector3 direction = to - from;
            foreach (var hit in Physics.RaycastAll(from, direction.normalized, direction.magnitude, Physics.AllLayers & ~Layers.Mask.Terrain, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider.transform.IsChildOf(portal.transform) || hit.collider.ToBaseEntity() is BasePlayer)
                    continue;
                return false;
            }
            return true;
        }

        private bool TryFindDungeonPortalPosition(ActiveDungeon dungeon, Vector3 playerPosition, out Vector3 position, out Quaternion rotation)
        {
            BasePortal portal = dungeon.InsidePortal;
            position = portal.transform.position;
            rotation = portal.transform.rotation;
            if (!dungeon.DungeonBounds.Contains(playerPosition) || !TryGetDungeonFloorPoint(dungeon, playerPosition, out Vector3 playerFloor) || Mathf.Abs(playerPosition.y - playerFloor.y) > 3f)
                return false;
            Bounds portalBounds = GetPortalPlacementBounds(portal);
            Vector3 entry = portal.transform.InverseTransformPoint(portal.GetLocalEntryExitPosition());
            Vector3 approachCenter = new Vector3(entry.x, 1f, entry.z);
            Vector3 approachExtents = new Vector3(0.45f, 0.9f, 0.45f);
            var candidates = dungeon.Foundations.OrderBy(p => (new Vector2(p.x - playerPosition.x, p.z - playerPosition.z)).sqrMagnitude);
            foreach (Vector3 candidate in candidates)
            {
                if (Vector3.Distance(candidate, playerPosition) < 2f || !TryGetDungeonFloorPoint(dungeon, candidate, out Vector3 floor))
                    continue;
                Vector3 direction = playerPosition - candidate;
                direction.y = 0f;
                Quaternion facing = Quaternion.LookRotation(direction.normalized, Vector3.up);
                for (int turn = 0; turn < 4; turn++)
                {
                    Quaternion candidateRotation = facing * Quaternion.Euler(0f, turn * 90f, 0f);
                    Vector3 candidatePosition = floor + Vector3.up * 0.15f;
                    if (!IsPortalFootprintSupported(dungeon, floor, candidateRotation, portalBounds.center, portalBounds.extents))
                        continue;
                    if (!IsPortalFootprintSupported(dungeon, floor, candidateRotation, approachCenter, approachExtents))
                        continue;
                    if (!IsPortalSpaceClear(portal, candidatePosition + candidateRotation * portalBounds.center, portalBounds.extents, candidateRotation))
                        continue;
                    if (!IsPortalSpaceClear(portal, candidatePosition + candidateRotation * approachCenter, approachExtents, candidateRotation))
                        continue;
                    Vector3 approach = floor + candidateRotation * new Vector3(entry.x, 0f, entry.z);
                    if (!IsPortalApproachVisible(portal, playerPosition, approach))
                        continue;
                    position = candidatePosition;
                    rotation = candidateRotation;
                    return true;
                }
            }
            return false;
        }

        private void RewardDungeonClear(ActiveDungeon dungeon)
        {
            int econReward = dungeon.TierConfig.RewardConfig.DungeonClearEconomicsReward;
            int srReward = dungeon.TierConfig.RewardConfig.DungeonClearServerRewards;
            double xpReward = dungeon.TierConfig.RewardConfig.DungeonClearXpReward;
            bool rewarded = false;
            ulong winnerId = 0UL;
            int tierCooldown = GetTierCooldownSeconds(dungeon.TierConfig);
            if (dungeon.OwnerID != 0UL)
            {
                var owner = BasePlayer.FindByID(dungeon.OwnerID);
                if (owner != null && owner.IsConnected)
                {
                    try
                    {
                        if (econReward > 0 && Economics != null)
                        {
                            Economics.Call("Deposit", owner.userID, (double)econReward);
                            owner.ChatMessage(Msg(owner.UserIDString, "DungeonClearEconomyReward", econReward));
                        }
                        if (srReward > 0 && ServerRewards != null)
                        {
                            ServerRewards.Call("AddPoints", owner.userID, srReward);
                            owner.ChatMessage(Msg(owner.UserIDString, "DungeonClearServerReward", srReward));
                        }
                        rewarded = true;
                        winnerId = owner.userID;
                    }
                    catch
                    {
                        rewarded = false;
                    }
                }
            }
            if (!rewarded)
            {
                foreach (ulong uid in dungeon.PlayersInside.ToList())
                {
                    var participant = BasePlayer.FindByID(uid);
                    if (participant != null && participant.IsConnected)
                    {
                        try
                        {
                            if (econReward > 0 && Economics != null)
                            {
                                Economics.Call("Deposit", participant.userID, (double)econReward);
                                participant.ChatMessage(Msg(participant.UserIDString, "DungeonClearEconomyReward", econReward));
                            }
                            if (srReward > 0 && ServerRewards != null)
                            {
                                ServerRewards.Call("AddPoints", participant.userID, srReward);
                                participant.ChatMessage(Msg(participant.UserIDString, "DungeonClearServerReward", srReward));
                            }
                            rewarded = true;
                            winnerId = participant.userID;
                            break;
                        }
                        catch { }
                    }
                }
            }
            if (xpReward > 0.0 && SkillTree != null)
            {
                try
                {
                    if (_config.EnableSkillTreeXpTeamSharing)
                    {
                        var xpTargets = GetDungeonXpTargets(dungeon);
                        if (xpTargets.Count == 0)
                        {
                            if (winnerId != 0UL)
                            {
                                var winner = BasePlayer.FindByID(winnerId);
                                if (winner != null && winner.IsConnected)
                                {
                                    SkillTree.Call("AwardXP", winner, xpReward, "DungeonEvents", false);
                                    winner.ChatMessage(Msg(winner.UserIDString, "DungeonClearXpReward", xpReward));
                                }
                            }
                        }
                        else
                        {
                            double perPlayerXp = xpReward;
                            if (_config.SkillTreeXpShareMode == 0 && xpTargets.Count > 0)
                            {
                                perPlayerXp = xpReward / xpTargets.Count;
                            }
                            foreach (var player in xpTargets)
                            {
                                SkillTree.Call("AwardXP", player, perPlayerXp, "DungeonEvents", false);
                                player.ChatMessage(Msg(player.UserIDString, "DungeonClearXpReward", perPlayerXp));
                            }
                        }
                    }
                    else
                    {
                        if (winnerId != 0UL)
                        {
                            var winner = BasePlayer.FindByID(winnerId);
                            if (winner != null && winner.IsConnected)
                            {
                                SkillTree.Call("AwardXP", winner, xpReward, "DungeonEvents", false);
                                winner.ChatMessage(Msg(winner.UserIDString, "DungeonClearXpReward", xpReward));
                            }
                        }
                    }
                }
                catch { }
            }
            if (winnerId != 0UL)
            {
                SetBuyCooldown(winnerId, dungeon.TierConfig.TierName, tierCooldown);
                Interface.CallHook("OnDungeonWin", winnerId, dungeon.TierConfig.MapName);
            }
            foreach (var entity in dungeon.Entities.ToList())
            {
                if (entity == null || entity.IsDestroyed)
                    continue;
                if (!string.Equals(entity.PrefabName, LightPrefab, StringComparison.OrdinalIgnoreCase))
                    continue;
                Vector3 pos = entity.transform.position;
                Quaternion rot = entity.transform.rotation;
                dungeon.Entities.Remove(entity);
                UnregisterSpawnedEntityForDungeon(entity, dungeon.UniqueId);
                KillDungeonEntity(entity);
                var newLight = GameManager.server.CreateEntity(GreenLightPrefab, pos, rot);
                if (newLight == null)
                    continue;
                newLight.OwnerID = OwnerID;
                newLight.enableSaving = false;
                newLight.Spawn();
                if (newLight is IOEntity io)
                {
                    io.UpdateHasPower(1000, 100);
                    io.IOStateChanged(1000, 100);
                    io.SendNetworkUpdateImmediate();
                }
                dungeon.Entities.Add(newLight);
                RegisterSpawnedEntityForDungeon(newLight, dungeon.UniqueId);
            }
            foreach (var entity in dungeon.Entities.ToList())
            {
                if (entity == null)
                {
                    continue;
                }
                if (entity is Door || entity.PrefabName.Contains("door"))
                {
                    UnregisterSpawnedEntityForDungeon(entity, dungeon.UniqueId);
                    entity.Kill();
                    dungeon.Entities.Remove(entity);
                }
            }
            FlushDungeonEntityCache(dungeon.UniqueId);
            dungeon.HasBeenCleared = true;
            dungeon.DestructionDelay = _config.DungeonSpawn.DungeonRemoval.RemovalAfterDestroyedTimer;
            dungeon.DestructionCountdownStart = DateTime.UtcNow;
            if (dungeon.InsidePortal != null && !dungeon.InsidePortal.IsDestroyed)
            {
                BasePlayer targetPlayer = null;
                foreach (ulong uid in dungeon.PlayersInside)
                {
                    var p = BasePlayer.FindByID(uid);
                    if (p != null && p.IsConnected)
                    {
                        targetPlayer = p;
                        break;
                    }
                }
                if (targetPlayer == null && dungeon.OwnerID != 0UL)
                {
                    targetPlayer = BasePlayer.FindByID(dungeon.OwnerID);
                }
                if (targetPlayer != null)
                {
                    if (TryFindDungeonPortalPosition(dungeon, targetPlayer.transform.position, out Vector3 portalPosition, out Quaternion portalRotation))
                    {
                        dungeon.InsidePortal.transform.position = portalPosition;
                        dungeon.InsidePortal.transform.rotation = portalRotation;
                        dungeon.InsidePortal.UpdateNetworkGroup();
                        dungeon.InsidePortal.SendNetworkUpdateImmediate();
                    }
                    else
                    {
                        LogDebug("No clear dungeon floor position near the player for the exit portal; keeping its entrance position.", 1);
                    }
                }
            }
        }

        private List<BasePlayer> GetDungeonXpTargets(ActiveDungeon dungeon)
        {
            var result = new List<BasePlayer>();
            if (dungeon == null || dungeon.PlayersInside == null || dungeon.PlayersInside.Count == 0)
            {
                return result;
            }
            foreach (var uid in dungeon.PlayersInside)
            {
                var player = BasePlayer.FindByID(uid);
                if (player != null && player.IsConnected && !result.Any(x => x.userID == player.userID))
                {
                    result.Add(player);
                }
            }
            return result;
        }

        private void TeleportPlayersAndDestroyDungeon(ActiveDungeon dungeon)
        {
            if (dungeon == null)
                return;
            var notified = new HashSet<ulong>();
            foreach (var player in GetPlayersPhysicallyInsideDungeon(dungeon))
            {
                if (player == null || !player.IsConnected || !notified.Add(player.userID))
                    continue;
                player.ChatMessage(Msg(player.UserIDString, "Dungeonclosed"));
            }
            foreach (ulong userID in dungeon.PlayersInside.ToList())
            {
                var player = BasePlayer.FindByID(userID);
                if (player == null || !player.IsConnected || !notified.Add(player.userID))
                    continue;
                player.ChatMessage(Msg(player.UserIDString, "Dungeonclosed"));
            }
            DestroyDungeon(dungeon);
        }

        private void TeleportPlayerToOutsidePortal(BasePlayer player, ActiveDungeon dungeon, bool forceTeleport = false)
        {
            if (player == null || player.IsDestroyed || dungeon == null)
                return;
            if (forceTeleport || _config.TeleportPlayerOnDeath)
            {
                Vector3 safePos = GetDungeonExitRecoveryPosition(dungeon, player.userID, 8f);
                if (player.IsConnected)
                {
                    SafeTeleport(player, safePos);
                }
                else
                {
                    try
                    {
                        player.EnsureDismounted();
                        player.SetParent(null, true, true);
                    }
                    catch { }
                    player.transform.position = safePos;
                    player.UpdateNetworkGroup();
                    player.SendNetworkUpdateImmediate();
                }
            }
            dungeon.PlayersInside.Remove(player.userID);
            UpdateTime(player, -1);
            DestroyEventUi(player);
            if (player.IsConnected)
                player.SendNetworkUpdateImmediate();
        }

        private Vector3 GetDungeonExitRecoveryPosition(ActiveDungeon dungeon, ulong userID, float extraHeight, int slotOffset = 0)
        {
            if (dungeon == null)
                return Vector3.zero;
            Vector3 exitPos = (dungeon.BasePortal != null && dungeon.BasePortal.transform != null) ? dungeon.BasePortal.transform.position : dungeon.PortalPosition;
            int seed = unchecked((int)(userID ^ (userID >> 32)));
            float angle = Mathf.Repeat(Mathf.Abs(seed) * 0.6180339f + slotOffset * 47f, 360f);
            float distance = 8f + (Mathf.Abs(seed + slotOffset) % 4);
            float rad = angle * Mathf.Deg2Rad;
            Vector3 targetFlat = exitPos + new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad)) * distance;
            return GetSafeTeleportPosition(targetFlat, extraHeight);
        }

        private void RegisterPendingPlayerLootRecovery(ulong userID, ActiveDungeon dungeon, bool relocateOnSpawn)
        {
            if (!userID.IsSteamId() || dungeon == null)
                return;
            var recovery = new PlayerLootRecovery
            {
                ExitPosition = GetDungeonExitRecoveryPosition(dungeon, userID, 1.25f),
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(PlayerLootRecoveryLifetimeSeconds),
                DungeonUniqueId = dungeon.UniqueId,
                RelocateOnSpawn = relocateOnSpawn,
            };
            _pendingPlayerLootRecoveries[userID] = recovery;
            timer.Once(
                PlayerLootRecoveryLifetimeSeconds,
                () =>
                {
                    if (_pendingPlayerLootRecoveries.TryGetValue(userID, out var current) && ReferenceEquals(current, recovery))
                        _pendingPlayerLootRecoveries.Remove(userID);
                }
            );
        }

        private bool TryGetPendingPlayerLootRecovery(ulong userID, out Vector3 position)
        {
            position = Vector3.zero;
            if (!userID.IsSteamId())
                return false;
            if (!_pendingPlayerLootRecoveries.TryGetValue(userID, out var recovery) || recovery == null)
                return false;
            if (DateTime.UtcNow > recovery.ExpiresAtUtc)
            {
                _pendingPlayerLootRecoveries.Remove(userID);
                return false;
            }
            if (!recovery.RelocateOnSpawn)
                return false;
            position = recovery.ExitPosition;
            return position != Vector3.zero;
        }

        private void MovePlayerLootEntity(BaseEntity entity, Vector3 position)
        {
            if (entity == null || entity.IsDestroyed || position == Vector3.zero)
                return;
            try
            {
                entity.SetParent(null, true, true);
            }
            catch { }
            var corpse = entity as PlayerCorpse;
            if (corpse != null)
                MovePlayerCorpse(corpse, position);
            else
                entity.transform.position = position;
            entity.UpdateNetworkGroup();
            entity.SendNetworkUpdateImmediate();
            if (corpse != null && corpse.CorpseRagdollScript != null)
            {
                corpse.CorpseRagdollScript.SyncJointsToClients();
                timer.Repeat(
                    1f,
                    3,
                    () =>
                    {
                        if (corpse == null || corpse.IsDestroyed || corpse.CorpseRagdollScript == null || corpse.CorpseRagdollScript.IsKinematic || corpse.rigidBody == null || corpse.buoyancy == null)
                            return;
                        corpse.SleepCheck();
                        corpse.SendNetworkUpdateImmediate();
                    }
                );
            }
        }

        private void MovePlayerCorpse(PlayerCorpse corpse, Vector3 position)
        {
            corpse.parentEnt = null;
            Physics.SyncTransforms();
            var rigidbodies = corpse.GetComponentsInChildren<Rigidbody>(true);
            var bodyPositions = new Vector3[rigidbodies.Length];
            Vector3 offset = position - corpse.transform.position;
            for (int i = 0; i < rigidbodies.Length; i++)
            {
                if (rigidbodies[i] != null)
                    bodyPositions[i] = rigidbodies[i].position + offset;
            }
            corpse.BecomeActive();
            corpse.transform.position = position;
            for (int i = 0; i < rigidbodies.Length; i++)
            {
                var body = rigidbodies[i];
                if (body == null)
                    continue;
                body.position = bodyPositions[i];
                if (body.isKinematic)
                    continue;
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.WakeUp();
            }
            Physics.SyncTransforms();
            corpse.prevLocalPos = corpse.transform.localPosition;
        }

        private void SchedulePlayerLootRecovery(BaseEntity entity, ulong ownerId)
        {
            if (entity == null || !ownerId.IsSteamId())
                return;
            if (!TryGetPendingPlayerLootRecovery(ownerId, out var recoveryPosition))
                return;
            NextTick(() =>
            {
                if (entity == null || entity.IsDestroyed)
                    return;
                MovePlayerLootEntity(entity, recoveryPosition);
            });
        }

        private List<BasePlayer> GetPlayersPhysicallyInsideDungeon(ActiveDungeon dungeon)
        {
            var result = new List<BasePlayer>();
            if (dungeon == null || dungeon.DungeonBounds.size == Vector3.zero)
                return result;
            var seen = new HashSet<ulong>();
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || player.IsDestroyed || player.IsDead() || !player.userID.IsSteamId() || !dungeon.DungeonBounds.Contains(player.transform.position) || !seen.Add(player.userID))
                    continue;
                result.Add(player);
            }
            foreach (var player in BasePlayer.sleepingPlayerList)
            {
                if (player == null || player.IsDestroyed || player.IsDead() || !player.userID.IsSteamId() || !dungeon.DungeonBounds.Contains(player.transform.position) || !seen.Add(player.userID))
                    continue;
                result.Add(player);
            }
            return result;
        }

        private void EvacuateDungeonPlayersAndLoot(ActiveDungeon dungeon)
        {
            if (dungeon == null)
                return;
            foreach (var recovery in _pendingPlayerLootRecoveries.Values)
            {
                if (recovery != null && string.Equals(recovery.DungeonUniqueId, dungeon.UniqueId, StringComparison.OrdinalIgnoreCase))
                    recovery.RelocateOnSpawn = true;
            }
            foreach (var player in GetPlayersPhysicallyInsideDungeon(dungeon))
                TeleportPlayerToOutsidePortal(player, dungeon, true);
            if (dungeon.DungeonBounds.size == Vector3.zero)
                return;
            Bounds bounds = dungeon.DungeonBounds;
            Vector3 center = bounds.center;
            float radius = bounds.extents.magnitude + 5f;
            int slot = 0;
            var corpses = Pool.Get<List<LootableCorpse>>();
            try
            {
                Vis.Entities(center, radius, corpses);
                foreach (var corpse in corpses)
                {
                    if (corpse == null || corpse.IsDestroyed || corpse is NPCPlayerCorpse || !bounds.Contains(corpse.transform.position))
                        continue;
                    ulong ownerId = corpse.playerSteamID;
                    if (!ownerId.IsSteamId())
                        continue;
                    MovePlayerLootEntity(corpse, GetDungeonExitRecoveryPosition(dungeon, ownerId, 1.25f, slot++));
                }
            }
            finally
            {
                Pool.FreeUnmanaged(ref corpses);
            }
            var droppedContainers = Pool.Get<List<DroppedItemContainer>>();
            try
            {
                Vis.Entities(center, radius, droppedContainers);
                foreach (var container in droppedContainers)
                {
                    if (container == null || container.IsDestroyed || !bounds.Contains(container.transform.position))
                        continue;
                    ulong ownerId = container.playerSteamID;
                    if (!ownerId.IsSteamId())
                        continue;
                    MovePlayerLootEntity(container, GetDungeonExitRecoveryPosition(dungeon, ownerId, 1.25f, slot++));
                }
            }
            finally
            {
                Pool.FreeUnmanaged(ref droppedContainers);
            }
        }

        private Vector3 GetSafeTeleportPosition(Vector3 targetXZ, float extraHeight)
        {
            float surfaceY = WaterLevel.GetWaterOrTerrainSurface(targetXZ, false, false);
            Vector3 p = new Vector3(targetXZ.x, surfaceY + Mathf.Max(0f, extraHeight), targetXZ.z);
            int tries = 0;
            while (AntiHack.TestInsideTerrain(p) && tries++ < 16)
            {
                p.y += 2f;
            }
            return p;
        }

        private void SafeTeleport(BasePlayer player, Vector3 pos)
        {
            if (player == null || !player.IsConnected)
                return;
            bool wasNetworkingLimited = player.limitNetworking;
            try
            {
                player.limitNetworking = true;
                player.EnsureDismounted();
                player.Teleport(pos);
            }
            finally
            {
                player.limitNetworking = wasNetworkingLimited;
            }
            player.SendNetworkUpdateImmediate();
        }

        private IEnumerator GreedySelectPositions(List<Vector3> candidates, int requiredCount, List<Vector3> selected)
        {
            if (candidates.Count == 0)
                yield break;
            var selectedSet = new HashSet<Vector3>();
            var minDistances = new float[candidates.Count];
            for (int i = 0; i < minDistances.Length; i++)
                minDistances[i] = float.MaxValue;
            selected.Add(candidates[0]);
            selectedSet.Add(candidates[0]);
            int operations = 0;
            while (selected.Count < requiredCount && selected.Count < candidates.Count)
            {
                Vector3 nextCandidate = candidates[0];
                float bestMinDistance = 0;
                Vector3 newest = selected[selected.Count - 1];
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (++operations >= 1024)
                    {
                        operations = 0;
                        yield return null;
                    }
                    var candidate = candidates[i];
                    if (selectedSet.Contains(candidate))
                        continue;
                    float minDistance = Mathf.Min(minDistances[i], Vector3.Distance(candidate, newest));
                    minDistances[i] = minDistance;
                    if (minDistance > bestMinDistance)
                    {
                        bestMinDistance = minDistance;
                        nextCandidate = candidate;
                    }
                }
                selected.Add(nextCandidate);
                selectedSet.Add(nextCandidate);
            }
        }

        private IEnumerator GreedySelectPairs(List<Tuple<Vector3, Vector3>> pairs, int requiredCount, List<Tuple<Vector3, Vector3>> selected, bool uniquePoints = false)
        {
            if (requiredCount <= 0 || pairs.Count == 0)
                yield break;
            var usedPoints = new HashSet<Vector3>();
            var selectedSet = new HashSet<Tuple<Vector3, Vector3>>();
            var centers = new Vector3[pairs.Count];
            var minDistances = new float[pairs.Count];
            int operations = 0;
            for (int i = 0; i < pairs.Count; i++)
            {
                centers[i] = (pairs[i].Item1 + pairs[i].Item2) / 2;
                minDistances[i] = float.MaxValue;
                if (++operations >= 1024)
                {
                    operations = 0;
                    yield return null;
                }
            }
            selected.Add(pairs[0]);
            selectedSet.Add(pairs[0]);
            usedPoints.Add(pairs[0].Item1);
            usedPoints.Add(pairs[0].Item2);
            while (selected.Count < requiredCount && selected.Count < pairs.Count)
            {
                Tuple<Vector3, Vector3> nextPair = uniquePoints ? null : pairs[0];
                float bestMinDistance = 0;
                var newest = selected[selected.Count - 1];
                Vector3 newestCenter = (newest.Item1 + newest.Item2) / 2;
                for (int i = 0; i < pairs.Count; i++)
                {
                    if (++operations >= 1024)
                    {
                        operations = 0;
                        yield return null;
                    }
                    var pair = pairs[i];
                    if (selectedSet.Contains(pair) || (uniquePoints && (usedPoints.Contains(pair.Item1) || usedPoints.Contains(pair.Item2))))
                        continue;
                    float minDistance = Mathf.Min(minDistances[i], Vector3.Distance(centers[i], newestCenter));
                    minDistances[i] = minDistance;
                    if (minDistance > bestMinDistance)
                    {
                        bestMinDistance = minDistance;
                        nextPair = pair;
                    }
                }
                if (nextPair == null)
                    break;
                selected.Add(nextPair);
                selectedSet.Add(nextPair);
                usedPoints.Add(nextPair.Item1);
                usedPoints.Add(nextPair.Item2);
            }
        }

        private IEnumerator AssignEntitiesToSpawnPoints(List<Vector3> spawnPoints, DungeonTierConfig tierConfig, DungeonEntitySpawns spawns, EntitySpawnLimits spawnLimits = null)
        {
            bool classicDungeon = tierConfig.BuildingGradeSelection == ClassicDungeonSelection;
            float spawnHeightOffset = classicDungeon ? 0f : 0.1f;
            var limits = spawnLimits ?? tierConfig.EntitySpawnLimits;
            var turretSpawns = spawns.Turrets;
            var boxSpawns = spawns.Boxes;
            var npcSpawns = spawns.Npcs;
            var bossSpawns = spawns.Bosses;
            var animalSpawns = spawns.Animals;
            int totalPoints = spawnPoints.Count;
            LogDebug("Total spawn points: " + totalPoints);
            LogDebug("TierConfig TurretsCounts: " + limits.TurretsCounts);
            LogDebug("TierConfig LootBoxesCounts: " + limits.LootBoxesCounts);
            LogDebug("TierConfig NpcsCounts: " + limits.NpcsCounts);
            LogDebug("TierConfig BossesCounts: " + limits.BossesCounts);
            LogDebug("TierConfig AnimalsCounts: " + limits.AnimalsCounts);
            int requiredTurrets = Math.Min(limits.TurretsCounts, totalPoints);
            int requiredBoxes = Math.Min(limits.LootBoxesCounts, totalPoints);
            int requiredNPCs = Math.Min(limits.NpcsCounts, totalPoints);
            int requiredBosses = Math.Min(limits.BossesCounts, totalPoints);
            int requiredAnimals = tierConfig.AnimalConfig != null && tierConfig.AnimalConfig.Enabled ? Math.Min(limits.AnimalsCounts, totalPoints) : 0;
            LogDebug("Initial requiredTurrets: " + requiredTurrets);
            LogDebug("Initial requiredBoxes: " + requiredBoxes);
            LogDebug("Initial requiredNPCs: " + requiredNPCs);
            LogDebug("Initial requiredBosses: " + requiredBosses);
            LogDebug("Initial requiredAnimals: " + requiredAnimals);
            int pairCount = Math.Min(requiredTurrets, requiredBoxes);
            LogDebug("Pair count (min of requiredTurrets and requiredBoxes): " + pairCount);
            List<Tuple<Vector3, Vector3>> selectedPairs = new List<Tuple<Vector3, Vector3>>();
            if (pairCount > 0)
            {
                float maxPairDistance = 5f;
                List<Tuple<Vector3, Vector3>> candidatePairs = new List<Tuple<Vector3, Vector3>>();
                var buckets = new Dictionary<Vector3Int, List<int>>();
                int operations = 0;
                for (int i = 0; i < spawnPoints.Count; i++)
                {
                    var key = GetSpatialBucket(spawnPoints[i], maxPairDistance);
                    if (!buckets.TryGetValue(key, out var indices))
                        buckets[key] = indices = new List<int>();
                    indices.Add(i);
                    if (++operations >= 1024)
                    {
                        operations = 0;
                        yield return null;
                    }
                }
                var nearbyIndices = new List<int>();
                for (int i = 0; i < spawnPoints.Count; i++)
                {
                    nearbyIndices.Clear();
                    var key = GetSpatialBucket(spawnPoints[i], maxPairDistance);
                    for (int x = -1; x <= 1; x++)
                    for (int y = -1; y <= 1; y++)
                    for (int z = -1; z <= 1; z++)
                    {
                        if (!buckets.TryGetValue(key + new Vector3Int(x, y, z), out var indices))
                            continue;
                        foreach (int j in indices)
                        {
                            if (j > i && Vector3.Distance(spawnPoints[i], spawnPoints[j]) <= maxPairDistance)
                                nearbyIndices.Add(j);
                            if (++operations >= 1024)
                            {
                                operations = 0;
                                yield return null;
                            }
                        }
                    }
                    nearbyIndices.Sort();
                    foreach (int j in nearbyIndices)
                        candidatePairs.Add(new Tuple<Vector3, Vector3>(spawnPoints[i], spawnPoints[j]));
                }
                LogDebug("Candidate pairs count: " + candidatePairs.Count);
                yield return GreedySelectPairs(candidatePairs, pairCount, selectedPairs, classicDungeon);
                LogDebug("Selected pairs count: " + selectedPairs.Count);
            }
            HashSet<Vector3> usedPoints = new HashSet<Vector3>();
            foreach (var pair in selectedPairs)
            {
                LogDebug("Adding pair - Turret: " + pair.Item1 + ", Box: " + pair.Item2);
                turretSpawns.Add(new Vector3(pair.Item1.x, pair.Item1.y + spawnHeightOffset, pair.Item1.z));
                boxSpawns.Add(new Vector3(pair.Item2.x, pair.Item2.y + spawnHeightOffset, pair.Item2.z));
                usedPoints.Add(pair.Item1);
                usedPoints.Add(pair.Item2);
            }
            requiredTurrets -= selectedPairs.Count;
            requiredBoxes -= selectedPairs.Count;
            LogDebug("After pairs, requiredTurrets: " + requiredTurrets + ", requiredBoxes: " + requiredBoxes);
            List<Vector3> remainingPoints = new List<Vector3>();
            foreach (var point in spawnPoints)
            {
                if (!usedPoints.Contains(point))
                    remainingPoints.Add(point);
            }
            LogDebug("Remaining points count after pairs: " + remainingPoints.Count);
            int remainingRequired = requiredNPCs + requiredBosses + requiredAnimals;
            List<Vector3> selectedRemaining = new List<Vector3>();
            if (remainingPoints.Count > 0 && remainingRequired > 0)
            {
                yield return GreedySelectPositions(remainingPoints, remainingRequired, selectedRemaining);
            }
            LogDebug("Selected remaining points count for NPCs, Bosses and Animals: " + selectedRemaining.Count);
            int count = selectedRemaining.Count;
            for (int i = 0; i < count; i++)
            {
                if (i < requiredNPCs)
                {
                    LogDebug("Assigning NPC spawn: " + selectedRemaining[i]);
                    npcSpawns.Add(new Vector3(selectedRemaining[i].x, selectedRemaining[i].y + spawnHeightOffset, selectedRemaining[i].z));
                }
                else if (i < requiredNPCs + requiredBosses)
                {
                    LogDebug("Assigning Boss spawn: " + selectedRemaining[i]);
                    bossSpawns.Add(new Vector3(selectedRemaining[i].x, selectedRemaining[i].y + spawnHeightOffset, selectedRemaining[i].z));
                }
                else if (i < requiredNPCs + requiredBosses + requiredAnimals)
                {
                    LogDebug("Assigning Animal spawn: " + selectedRemaining[i]);
                    animalSpawns.Add(new Vector3(selectedRemaining[i].x, selectedRemaining[i].y + spawnHeightOffset, selectedRemaining[i].z));
                }
            }
            if (requiredTurrets > 0 || requiredBoxes > 0)
            {
                HashSet<Vector3> alreadyUsed = new HashSet<Vector3>(usedPoints);
                alreadyUsed.UnionWith(selectedRemaining);
                List<Vector3> extraPoints = new List<Vector3>();
                foreach (var point in spawnPoints)
                {
                    if (!alreadyUsed.Contains(point))
                        extraPoints.Add(point);
                }
                LogDebug("Extra points count: " + extraPoints.Count);
                int extraNeeded = requiredTurrets + requiredBoxes;
                LogDebug("Extra needed (requiredTurrets + requiredBoxes): " + extraNeeded);
                if (extraPoints.Count > 0 && extraNeeded > 0)
                {
                    var selectedExtra = new List<Vector3>();
                    yield return GreedySelectPositions(extraPoints, extraNeeded, selectedExtra);
                    LogDebug("Selected extra points count: " + selectedExtra.Count);
                    int extraCount = selectedExtra.Count;
                    for (int i = 0; i < extraCount; i++)
                    {
                        if (i < requiredTurrets)
                        {
                            LogDebug("Assigning extra Turret spawn: " + selectedExtra[i]);
                            turretSpawns.Add(new Vector3(selectedExtra[i].x, selectedExtra[i].y + spawnHeightOffset, selectedExtra[i].z));
                        }
                        else if (i < requiredTurrets + requiredBoxes)
                        {
                            LogDebug("Assigning extra Box spawn: " + selectedExtra[i]);
                            boxSpawns.Add(new Vector3(selectedExtra[i].x, selectedExtra[i].y + spawnHeightOffset, selectedExtra[i].z));
                        }
                    }
                }
            }
        }

        private IEnumerator PlaceEntities(
            List<Vector3> turretSpawns,
            List<Vector3> boxSpawns,
            List<Vector3> npcSpawns,
            List<Vector3> bossSpawns,
            List<Vector3> animalSpawns,
            ActiveDungeon activeDungeon
        )
        {
            LogDebug("Placing Entities.");
            var count = 0;
            int batch = Mathf.Clamp(Performance.EntitySpawnsPerBatch, 1, 100);
            foreach (var turretPos in turretSpawns)
            {
                var rotation = Quaternion.Euler(0, Random.Range(0f, 360f), 0);
                SpawnTurret(GetDungeonEntitySpawnPosition(turretPos, activeDungeon), rotation, activeDungeon.TierConfig.AutoTurretConfig, activeDungeon);
                count++;
                if (count >= batch)
                {
                    count = 0;
                    yield return _spawnBatchDelay;
                }
            }
            foreach (var boxPos in boxSpawns)
            {
                SpawnLootBox(GetDungeonEntitySpawnPosition(boxPos, activeDungeon), Quaternion.identity, activeDungeon.TierConfig, activeDungeon);
                count++;
                if (count >= batch)
                {
                    count = 0;
                    yield return _spawnBatchDelay;
                }
            }
            foreach (var animalPos in animalSpawns)
            {
                SpawnAnimal(GetDungeonEntitySpawnPosition(animalPos, activeDungeon), activeDungeon);
                count++;
                if (count >= batch)
                {
                    count = 0;
                    yield return _spawnBatchDelay;
                }
            }
            foreach (var npcPos in npcSpawns)
            {
                SpawnNpc(GetDungeonEntitySpawnPosition(npcPos, activeDungeon), activeDungeon);
                count++;
                if (count >= batch)
                {
                    count = 0;
                    yield return _spawnBatchDelay;
                }
            }
            foreach (var bossPos in bossSpawns)
            {
                SpawnNpc(GetDungeonEntitySpawnPosition(bossPos, activeDungeon), activeDungeon, true);
                count++;
                if (count >= batch)
                {
                    count = 0;
                    yield return _spawnBatchDelay;
                }
            }
        }

        private BasePortal CreateDungeonPortal(Vector3 position, ActiveDungeon activeDungeon)
        {
            var dungeonPortal = GameManager.server.CreateEntity(PortalPrefab, position) as BasePortal;
            if (dungeonPortal == null)
                return null;
            dungeonPortal.OwnerID = OwnerID;
            dungeonPortal.Spawn();
            if (_config.EnablePlaceSphere)
                CreateSpheres(position, GetPortalProtectionRadius() * 2.0f, activeDungeon);
            return dungeonPortal;
        }

        private void CreateSpheres(Vector3 center, float radius, ActiveDungeon activeDungeon)
        {
            if (activeDungeon == null)
                return;
            float finalRadius = Mathf.Max(1f, radius);
            for (int i = 0; i < 10; i++)
            {
                var sphere = GameManager.server.CreateEntity("assets/prefabs/visualization/sphere.prefab", center) as SphereEntity;
                if (sphere == null)
                    continue;
                sphere.currentRadius = finalRadius;
                sphere.lerpSpeed = 0f;
                sphere.enableSaving = false;
                sphere.OwnerID = OwnerID;
                sphere.Spawn();
                RegisterSpawnedEntityForDungeon(sphere, activeDungeon.UniqueId);
            }
            var colorSphere = GameManager.server.CreateEntity("assets/bundled/prefabs/modding/events/twitch/br_sphere_purple.prefab", center) as SphereEntity;
            if (colorSphere == null)
                return;
            colorSphere.currentRadius = finalRadius;
            colorSphere.lerpSpeed = 0f;
            colorSphere.enableSaving = false;
            colorSphere.OwnerID = OwnerID;
            colorSphere.Spawn();
            RegisterSpawnedEntityForDungeon(colorSphere, activeDungeon.UniqueId);
        }

        private void AutoSpawnDungeon()
        {
            if (_config?.DungeonSpawn == null)
                return;
            int delay = _config.DungeonSpawn.AutoSpawnDelay;
            if (delay > 0 && (DateTime.UtcNow - _lastAutoSpawnTime).TotalSeconds < delay)
                return;
            var tier = GetDifficultyWithLeastActiveDungeons();
            if (tier == null || !tier.EnableAutoSpawn)
                return;
            EnqueueDungeonRequest(null, tier);
            _lastAutoSpawnTime = DateTime.UtcNow;
            LogDebug($"Enqueued Dungeon Auto Spawn of tier {_tierInfoMap[tier].Name}.", 1);
        }

        private void Requeue(DungeonRequest request, DungeonOrigin origin)
        {
            if (request == null)
                return;
            request.PortalPosition = Vector3.zero;
            request.DungeonPosition = Vector3.zero;
            request.UsesCustomPortalPosition = false;
            if (origin == DungeonOrigin.AutoSpawn)
            {
                _dungeonFailedAutoSpawnQueue.Enqueue(request);
            }
            else
            {
                _dungeonFailedBuyQueue.Enqueue(request);
            }
        }

        private ActiveDungeon RegisterActiveDungeon(
            BasePortal dungeonPortal,
            Vector3 portalPosition,
            Vector3 DungeonPosition,
            string grid,
            DungeonTierConfig selectedTier,
            BasePlayer owner,
            bool lockToPlayer,
            DungeonOrigin origin
        )
        {
            var activeDungeon = new ActiveDungeon
            {
                UniqueId = Guid.NewGuid().ToString(),
                BasePortal = dungeonPortal,
                PortalPosition = portalPosition,
                DungeonPosition = DungeonPosition,
                Grid = grid,
                TierName = _tierInfoMap[selectedTier].Name,
                TierConfig = selectedTier,
                Entities = new HashSet<BaseEntity>(),
                Npcs = new HashSet<BaseEntity>(),
                Spawned = false,
                CreationTime = DateTime.UtcNow,
                Origin = origin,
                AllDestroyedTime = null,
            };
            if (lockToPlayer && owner != null)
            {
                activeDungeon.OwnerID = owner.userID;
                activeDungeon.OwnerName = owner.displayName;
            }
            _activeDungeons.Add(activeDungeon);
            _dungeonByUniqueId[activeDungeon.UniqueId] = activeDungeon;
            return activeDungeon;
        }

        private List<ActiveDungeon> RemoveInactiveDungeons()
        {
            var removedDungeons = new List<ActiveDungeon>();
            foreach (var dungeon in _activeDungeons.ToList())
            {
                if (CanBeRemoved(dungeon))
                {
                    removedDungeons.Add(dungeon);
                    DestroyDungeon(dungeon);
                    LogDebug($"Removed inactive dungeon of tier {dungeon.TierName} at grid {dungeon.Grid}.", 1);
                }
            }
            return removedDungeons;
        }

        private void DestroyDungeon(ActiveDungeon dungeon)
        {
            if (dungeon == null || dungeon.MarkedForRemoval)
                return;
            dungeon.MarkedForRemoval = true;
            _activeDungeonCleanups++;
            EvacuateDungeonPlayersAndLoot(dungeon);
            UnityEngine.GameObject.Destroy(dungeon.Trigger);
            dungeon.BasePortal?.Kill();
            ServerMgr.Instance.StartCoroutine(DestroyEntities(dungeon));
            RemoveDungeonMarkers(dungeon);
            _activeDungeons.Remove(dungeon);
            Interface.CallHook("OnDungeonDespawn", dungeon.OwnerID, dungeon.PortalPosition, dungeon.Grid, dungeon.TierName);
        }

        private IEnumerator CleanupDungeonArea(ActiveDungeon dungeon)
        {
            if (dungeon == null || dungeon.DungeonBounds.size == Vector3.zero)
                yield break;
            Bounds bounds = dungeon.DungeonBounds;
            Vector3 center = bounds.center;
            float radius = bounds.extents.magnitude + 5f;
            var cleanupTargets = new List<BaseEntity>();
            var corpses = Pool.Get<List<LootableCorpse>>();
            try
            {
                Vis.Entities(center, radius, corpses);
                foreach (var corpse in corpses)
                {
                    if (corpse != null && !corpse.IsDestroyed && bounds.Contains(corpse.transform.position) && corpse is NPCPlayerCorpse && corpse.GetComponent<CorpseLootData>() != null)
                        cleanupTargets.Add(corpse);
                }
            }
            finally
            {
                Pool.FreeUnmanaged(ref corpses);
            }
            var droppedContainers = Pool.Get<List<DroppedItemContainer>>();
            try
            {
                Vis.Entities(center, radius, droppedContainers);
                foreach (var container in droppedContainers)
                {
                    if (container == null || container.IsDestroyed || !bounds.Contains(container.transform.position))
                        continue;
                    if (!container.playerSteamID.IsSteamId() && container.OwnerID == OwnerID)
                        cleanupTargets.Add(container);
                }
            }
            finally
            {
                Pool.FreeUnmanaged(ref droppedContainers);
            }
            int count = 0;
            int batch = Mathf.Clamp(Performance.CleanupEntitiesPerBatch, 1, 250);
            foreach (var entity in cleanupTargets)
            {
                if (entity != null && !entity.IsDestroyed)
                    KillDungeonEntity(entity);
                if (++count >= batch)
                {
                    count = 0;
                    yield return _cleanupBatchDelay;
                }
            }
        }

        private IEnumerable<ActiveDungeon> RemoveAllDungeons()
        {
            var removed = new List<ActiveDungeon>(_activeDungeons);
            foreach (var d in removed)
            {
                DestroyDungeon(d);
            }
            _activeDungeons.Clear();
            _activeDungeonMarkers.Clear();
            return removed;
        }

        private IEnumerator DestroyEntities(ActiveDungeon dungeon)
        {
            yield return null;
            if (dungeon == null)
                yield break;
            int batch = Mathf.Clamp(Performance.CleanupEntitiesPerBatch, 1, 250);
            float cleanupStarted = Time.realtimeSinceStartup;
            var killedIds = new HashSet<ulong>();
            try
            {
                yield return CleanupDungeonArea(dungeon);
                int count = 0;
                foreach (var entity in dungeon.Entities.ToList())
                {
                    if (entity == null || entity.IsDestroyed)
                        continue;
                    ulong id = entity.net != null ? entity.net.ID.Value : 0UL;
                    if (id != 0UL && !killedIds.Add(id))
                        continue;
                    KillDungeonEntity(entity);
                    if (++count >= batch)
                    {
                        count = 0;
                        yield return _cleanupBatchDelay;
                    }
                }
                foreach (var npc in dungeon.Npcs.ToList())
                {
                    if (npc == null || npc.IsDestroyed)
                        continue;
                    ulong id = npc.net != null ? npc.net.ID.Value : 0UL;
                    if (id != 0UL && !killedIds.Add(id))
                        continue;
                    KillDungeonEntity(npc);
                    if (++count >= batch)
                    {
                        count = 0;
                        yield return _cleanupBatchDelay;
                    }
                }
                yield return RemoveEntitiesForDungeon(dungeon.UniqueId, killedIds);
            }
            finally
            {
                RemoveDungeonEntityIndex(dungeon);
                dungeon.Entities.Clear();
                dungeon.Npcs.Clear();
                dungeon.EntranceDoors.Clear();
                dungeon.Doors.Clear();
                dungeon.ClassicDoorFrames.Clear();
                dungeon.EntranceCorridorSegments.Clear();
                dungeon.EntranceSafeZoneBounds = default(Bounds);
                dungeon.HasEntranceSafeZone = false;
                _activeDungeonCleanups = Mathf.Max(0, _activeDungeonCleanups - 1);
                _lastCleanupCompletedAt = Time.realtimeSinceStartup;
                LogDebug($"Dungeon cleanup finished in {Time.realtimeSinceStartup - cleanupStarted:0.00}s ({killedIds.Count} tracked entities).", 1);
            }
        }

        private IEnumerator CleanupExistingEntities()
        {
            yield return null;
            var count = 0;
            var leftovers = BaseNetworkable.serverEntities.OfType<BaseEntity>().Where(entity => entity != null && entity.OwnerID == OwnerID).ToList();
            foreach (var entity in leftovers)
            {
                if (entity == null || entity.IsDestroyed)
                    continue;
                KillDungeonEntity(entity);
                LogDebug("Removed: " + entity, 0);
                count++;
                if (count >= Mathf.Clamp(Performance.CleanupEntitiesPerBatch, 1, 250))
                {
                    count = 0;
                    yield return _cleanupBatchDelay;
                }
            }
            _activeDungeons.Clear();
            _activeDungeonMarkers.Clear();
            _dungeonByEntityId.Clear();
            _dungeonByUniqueId.Clear();
            _dungeonEntityIdCache.Clear();
        }

        private bool CanBeRemoved(ActiveDungeon dungeon)
        {
            if (dungeon.ActivationTime == null)
            {
                int idleLimit = dungeon.TierConfig.MaxUnenteredLifetimeSeconds;
                if (idleLimit > 0)
                {
                    var elapsedIdle = DateTime.UtcNow - dungeon.CreationTime;
                    if (elapsedIdle.TotalSeconds >= idleLimit)
                        return !ContainsAnyPlayers(dungeon) && dungeon.Spawned;
                }
                return false;
            }
            if (dungeon.TierConfig.MaxDungeonLifetime > 0)
            {
                var elapsedActive = DateTime.UtcNow - dungeon.ActivationTime.Value;
                if (elapsedActive.TotalSeconds >= dungeon.TierConfig.MaxDungeonLifetime)
                    return !ContainsAnyPlayers(dungeon) && dungeon.Spawned;
            }
            var removalCfg = _config.DungeonSpawn.DungeonRemoval;
            if (!removalCfg.RemoveIfAllNpcsDead && !removalCfg.RemoveIfAllBoxesDestroyed && !removalCfg.RemoveIfAllTurretsDestroyed)
                return false;
            bool npcsGone = !removalCfg.RemoveIfAllNpcsDead || AreAllNpcsGone(dungeon);
            bool boxesGone = !removalCfg.RemoveIfAllBoxesDestroyed || AreAllBoxesGone(dungeon);
            bool turretsGone = !removalCfg.RemoveIfAllTurretsDestroyed || AreAllTurretsGone(dungeon);
            if (npcsGone && boxesGone && turretsGone)
            {
                if (dungeon.AllDestroyedTime == null)
                {
                    dungeon.AllDestroyedTime = DateTime.UtcNow;
                    dungeon.LastReminderMinute = -1;
                }
                else
                {
                    double elapsed = (DateTime.UtcNow - dungeon.AllDestroyedTime.Value).TotalSeconds;
                    double timeLeft = removalCfg.RemovalAfterDestroyedTimer - elapsed;
                    if (timeLeft <= 0)
                        return !ContainsAnyPlayers(dungeon) && dungeon.Spawned;
                    SendRemovalReminder(dungeon, timeLeft);
                }
            }
            else
            {
                dungeon.AllDestroyedTime = null;
                dungeon.LastReminderMinute = -1;
            }
            return false;
        }

        private void SendRemovalReminder(ActiveDungeon dungeon, double timeLeftSeconds)
        {
            int minutesLeft = Mathf.CeilToInt((float)(timeLeftSeconds / 60.0));
            double totalElapsed = (DateTime.UtcNow - dungeon.AllDestroyedTime.Value).TotalSeconds;
            int elapsedMinutes = Mathf.FloorToInt((float)(totalElapsed / 60.0f));
            if (elapsedMinutes == dungeon.LastReminderMinute)
                return;
            dungeon.LastReminderMinute = elapsedMinutes;
            if (dungeon.OwnerID != 0UL)
            {
                var owner = BasePlayer.FindByID(dungeon.OwnerID);
                if (owner != null && owner.IsConnected)
                {
                    NotifyPlayer(owner, "DungeonRemovalCountdown", 0, minutesLeft);
                }
            }
        }

        private bool AreAllNpcsGone(ActiveDungeon dungeon)
        {
            if (dungeon?.Npcs == null)
                return true;
            foreach (var npc in dungeon.Npcs)
            {
                if (IsDungeonNpcAlive(npc))
                    return false;
            }
            return true;
        }

        private bool AreAllBoxesGone(ActiveDungeon dungeon)
        {
            if (dungeon?.Entities == null)
                return true;
            foreach (var entity in dungeon.Entities)
            {
                if (entity is StorageContainer box && box != null && !box.IsDestroyed)
                    return false;
            }
            return true;
        }

        private bool AreAllTurretsGone(ActiveDungeon dungeon)
        {
            if (dungeon?.Entities == null)
                return true;
            foreach (var entity in dungeon.Entities)
            {
                if (entity is AutoTurret turret && turret != null && !turret.IsDestroyed)
                    return false;
            }
            return true;
        }

        private bool ContainsAnyPlayers(ActiveDungeon dungeon)
        {
            if (dungeon == null)
                return false;
            Bounds dungeonBounds = dungeon.DungeonBounds;
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (dungeonBounds.Contains(player.transform.position))
                    return true;
            }
            foreach (BasePlayer sleeper in BasePlayer.sleepingPlayerList)
            {
                if (dungeonBounds.Contains(sleeper.transform.position))
                    return true;
            }
            return false;
        }

        private ActiveDungeon GetDungeonForPlayer(BasePlayer player)
        {
            if (player == null)
                return null;
            Vector3 playerPos = player.transform.position;
            foreach (var dungeon in _activeDungeons)
            {
                if (dungeon == null || dungeon.DungeonBounds.size == Vector3.zero)
                    continue;
                if (dungeon.DungeonBounds.Contains(playerPos))
                {
                    return dungeon;
                }
            }
            return null;
        }

        private void EnsureUndergroundTerrainProtectionDisabled()
        {
            var spawnSettings = _config?.DungeonSpawn;
            if (spawnSettings == null || spawnSettings.DungeonHeight >= 0f)
                return;
            ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), "antihack.terrain_protection 0");
        }

        private bool IsUndergroundDungeon(ActiveDungeon dungeon)
        {
            if (dungeon == null)
                return false;
            Bounds bounds = dungeon.DungeonBounds;
            if (bounds.size == Vector3.zero)
            {
                if (TerrainMeta.HeightMap == null)
                    return dungeon.DungeonPosition.y < 0f;
                return dungeon.DungeonPosition.y < TerrainMeta.HeightMap.GetHeight(dungeon.DungeonPosition) - 1f;
            }
            float terrainHeight = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(bounds.center) : 0f;
            return TerrainMeta.HeightMap == null ? dungeon.DungeonPosition.y < 0f : bounds.max.y < terrainHeight - 1f;
        }

        private float GetUndergroundWaterClearanceRadius(DungeonTierConfig tier)
        {
            int rooms = Mathf.Clamp(tier?.DungeonRooms ?? 1, 1, 6);
            int roomSize = Mathf.Clamp(tier?.DungeonRoomSize ?? 5, 5, 10);
            float roomSpan = roomSize * CellSpacing;
            float roomStep = roomSpan + CorridorLength;
            int moves = Mathf.Max(0, rooms - 1);
            int primaryMoves = (moves + 1) / 2;
            int secondaryMoves = moves / 2;
            float maxOriginReach = Mathf.Sqrt(primaryMoves * primaryMoves + secondaryMoves * secondaryMoves) * roomStep;
            float roomFootprintPadding = roomSpan * 0.75f;
            float corridorPadding = CorridorLength + CellSpacing;
            return maxOriginReach + roomFootprintPadding + corridorPadding;
        }

        private bool HasWaterInClearanceArea(Vector3 center, float radius)
        {
            if (radius <= 0f)
                return false;
            if (!IsInsideWorld(center) || IsSurfaceWaterAt(center))
                return true;
            const float desiredRingSpacing = 24f;
            const float desiredArcSpacing = 18f;
            int rings = Mathf.Clamp(Mathf.CeilToInt(radius / desiredRingSpacing), 2, 12);
            for (int ring = 1; ring <= rings; ring++)
            {
                float ringRadius = radius * ring / rings;
                int samples = Mathf.Clamp(Mathf.CeilToInt((2f * Mathf.PI * ringRadius) / desiredArcSpacing), 16, 96);
                float angleOffset = (ring & 1) == 0 ? 0.5f : 0f;
                for (int i = 0; i < samples; i++)
                {
                    float angle = ((i + angleOffset) * 2f * Mathf.PI) / samples;
                    Vector3 probe = center + new Vector3(Mathf.Cos(angle) * ringRadius, 0f, Mathf.Sin(angle) * ringRadius);
                    if (!IsInsideWorld(probe) || IsSurfaceWaterAt(probe))
                        return true;
                }
            }
            return false;
        }

        private bool IsSurfaceWaterAt(Vector3 position)
        {
            if (TerrainMeta.TopologyMap != null)
            {
                int topology = TerrainMeta.TopologyMap.GetTopology(position);
                int waterTopology = (int)(TerrainTopology.Enum.Ocean | TerrainTopology.Enum.River | TerrainTopology.Enum.Lake);
                if ((topology & waterTopology) != 0)
                    return true;
            }
            if (TerrainMeta.HeightMap == null || TerrainMeta.WaterMap == null)
                return false;
            float terrainHeight = TerrainMeta.HeightMap.GetHeight(position);
            float waterHeight = TerrainMeta.WaterMap.GetHeight(position);
            return waterHeight > terrainHeight + 0.05f;
        }

        private bool DoesDungeonFootprintOverlapWater(ActiveDungeon dungeon)
        {
            if (dungeon?.Foundations == null || dungeon.Foundations.Count == 0)
                return false;
            foreach (var foundation in dungeon.Foundations)
            {
                if (IsSurfaceWaterAt(foundation))
                    return true;
            }
            return false;
        }

        private ActiveDungeon GetUndergroundDungeonAtPosition(Vector3 position)
        {
            foreach (var dungeon in _activeDungeons)
            {
                if (dungeon == null || dungeon.DungeonBounds.size == Vector3.zero)
                    continue;
                Bounds bounds = dungeon.DungeonBounds;
                bounds.Expand(new Vector3(2f, 4f, 2f));
                if (!bounds.Contains(position))
                    continue;
                if (IsUndergroundDungeon(dungeon))
                    return dungeon;
            }
            return null;
        }

        private float GetPortalProtectionRadius()
        {
            if (_config == null)
                return 0f;
            return Mathf.Max(1f, _config.PortalProtectionBubbleRadius);
        }

        private Vector3 GetDeployCheckPosition(BasePlayer player)
        {
            if (player == null)
                return Vector3.zero;
            RaycastHit hit;
            if (player.eyes != null && Physics.Raycast(player.eyes.HeadRay(), out hit, 10f))
                return hit.point;
            return player.transform.position;
        }

        private ActiveDungeon GetPortalProtectionDungeon(Vector3 position)
        {
            if (_config == null || !_config.EnablePortalProtectionBubble)
                return null;
            float radius = GetPortalProtectionRadius();
            if (radius <= 0f)
                return null;
            foreach (var dungeon in _activeDungeons)
            {
                if (dungeon == null || dungeon.PortalPosition == Vector3.zero)
                    continue;
                if (IsInsidePortalProtection(dungeon, position, radius))
                    return dungeon;
            }
            return null;
        }

        private bool IsInsidePortalProtection(ActiveDungeon dungeon, Vector3 position)
        {
            return IsInsidePortalProtection(dungeon, position, GetPortalProtectionRadius());
        }

        private bool IsInsidePortalProtection(ActiveDungeon dungeon, Vector3 position, float radius)
        {
            if (dungeon == null || dungeon.PortalPosition == Vector3.zero || radius <= 0f)
                return false;
            return (position - dungeon.PortalPosition).sqrMagnitude <= radius * radius;
        }

        private bool IsDungeonOwnerOrAlly(ulong userID, ActiveDungeon dungeon)
        {
            if (dungeon == null || !userID.IsSteamId())
                return false;
            if (permission.UserHasPermission(userID.ToString(), PermissionAdmin))
                return true;
            if (dungeon.OwnerID == 0UL)
                return false;
            if (dungeon.OwnerID == userID)
                return true;
            if (_config.AllowTeam)
            {
                var player = BasePlayer.FindByID(userID);
                if (player != null && player.currentTeam != 0UL)
                {
                    var team = RelationshipManager.ServerInstance.FindTeam(player.currentTeam);
                    if (team != null && team.members.Contains(dungeon.OwnerID))
                        return true;
                }
                var owner = BasePlayer.FindByID(dungeon.OwnerID);
                if (owner != null && owner.currentTeam != 0UL)
                {
                    var team = RelationshipManager.ServerInstance.FindTeam(owner.currentTeam);
                    if (team != null && team.members.Contains(userID))
                        return true;
                }
            }
            if (_config.AllowFriends && Friends != null)
            {
                var areFriends = Friends.CallHook("AreFriends", dungeon.OwnerID, userID);
                if (areFriends != null && Convert.ToBoolean(areFriends))
                    return true;
            }
            return false;
        }

        private bool CanUsePortalProtection(BasePlayer player, ActiveDungeon dungeon)
        {
            return player != null && IsPlayer(player) && IsDungeonOwnerOrAlly(player.userID, dungeon);
        }

        private object CancelPortalBubbleDamage(HitInfo info)
        {
            info?.damageTypes?.ScaleAll(0f);
            return true;
        }

        private bool IsDamageFromOutsidePortalProtection(HitInfo info, ActiveDungeon dungeon)
        {
            if (dungeon == null || info == null)
                return true;
            var attacker = info.InitiatorPlayer;
            if (attacker != null)
                return !IsInsidePortalProtection(dungeon, attacker.transform.position);
            if (info.Initiator == null || info.Initiator.transform == null)
                return true;
            return !IsInsidePortalProtection(dungeon, info.Initiator.transform.position);
        }

        private object HandlePortalBubblePlayerDamage(BasePlayer victim, HitInfo info)
        {
            if (_config == null || !_config.EnablePortalProtectionBubble)
                return null;
            if (info?.Initiator != null && IsDungeonEntity(info.Initiator))
                return null;
            var victimDungeon = GetPortalProtectionDungeon(victim.transform.position);
            if (
                _config.BlockDamageFromOutsideToOwnerAndAlliesInsidePortalProtectionBubble
                && victimDungeon != null
                && CanUsePortalProtection(victim, victimDungeon)
                && IsDamageFromOutsidePortalProtection(info, victimDungeon)
            )
            {
                return CancelPortalBubbleDamage(info);
            }
            if (!_config.BlockDamageFromOwnerAndAlliesInsidePortalProtectionBubbleToOutside)
                return null;
            var attacker = info?.InitiatorPlayer;
            if (attacker == null || attacker == victim || !IsPlayer(attacker))
                return null;
            var attackerDungeon = GetPortalProtectionDungeon(attacker.transform.position);
            if (attackerDungeon != null && CanUsePortalProtection(attacker, attackerDungeon) && !IsInsidePortalProtection(attackerDungeon, victim.transform.position))
            {
                return CancelPortalBubbleDamage(info);
            }
            return null;
        }

        private object HandlePortalBubbleEntityDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (_config == null || !_config.EnablePortalProtectionBubble)
                return null;
            if (info?.Initiator != null && IsDungeonEntity(info.Initiator))
                return null;
            var entityDungeon = GetPortalProtectionDungeon(entity.transform.position);
            if (
                _config.BlockDamageFromOutsideToOwnerAndAlliesInsidePortalProtectionBubble
                && entityDungeon != null
                && IsPortalProtectedEntity(entity, entityDungeon)
                && IsDamageFromOutsidePortalProtection(info, entityDungeon)
            )
            {
                return CancelPortalBubbleDamage(info);
            }
            if (!_config.BlockDamageFromOwnerAndAlliesInsidePortalProtectionBubbleToOutside)
                return null;
            var attacker = info?.InitiatorPlayer;
            if (attacker == null || !IsPlayer(attacker))
                return null;
            var attackerDungeon = GetPortalProtectionDungeon(attacker.transform.position);
            if (attackerDungeon != null && CanUsePortalProtection(attacker, attackerDungeon) && !IsInsidePortalProtection(attackerDungeon, entity.transform.position))
            {
                return CancelPortalBubbleDamage(info);
            }
            return null;
        }

        private bool IsPortalProtectedEntity(BaseCombatEntity entity, ActiveDungeon dungeon)
        {
            if (entity == null || dungeon == null)
                return false;
            if (entity is BasePlayer player)
                return CanUsePortalProtection(player, dungeon);
            if (entity.OwnerID.IsSteamId() && IsDungeonOwnerOrAlly(entity.OwnerID, dungeon))
                return true;
            if (_config.ProtectOwnerAndAlliesVehiclesInsidePortalProtectionBubble && IsPortalProtectableVehicle(entity))
            {
                if (HasProtectedMountedPlayer(entity, dungeon))
                    return true;
                if (HasProtectedPlayerNearEntity(entity, dungeon))
                    return true;
            }
            return false;
        }

        private bool IsPortalProtectableVehicle(BaseCombatEntity entity)
        {
            if (entity == null)
                return false;
            if (entity is BaseVehicle || entity is BaseMountable)
                return true;
            string prefab = entity.ShortPrefabName ?? string.Empty;
            return prefab.Contains("mini")
                || prefab.Contains("copter")
                || prefab.Contains("helicopter")
                || prefab.Contains("hotairballoon")
                || prefab.Contains("snowmobile")
                || prefab.Contains("vehicle");
        }

        private bool HasProtectedMountedPlayer(BaseCombatEntity entity, ActiveDungeon dungeon)
        {
            if (entity == null || dungeon == null)
                return false;
            var mountedPlayers = Pool.Get<List<BasePlayer>>();
            try
            {
                var vehicle = entity as BaseVehicle;
                if (vehicle != null)
                {
                    vehicle.GetMountedPlayers(mountedPlayers);
                }
                else if (entity is BaseMountable mountable)
                {
                    var mounted = mountable.GetMounted();
                    if (mounted != null)
                        mountedPlayers.Add(mounted);
                }
                foreach (var player in mountedPlayers)
                {
                    if (CanUsePortalProtection(player, dungeon))
                        return true;
                }
                return false;
            }
            finally
            {
                Pool.FreeUnmanaged(ref mountedPlayers);
            }
        }

        private bool HasProtectedPlayerNearEntity(BaseCombatEntity entity, ActiveDungeon dungeon)
        {
            if (entity == null || dungeon == null)
                return false;
            var players = Pool.Get<List<BasePlayer>>();
            try
            {
                Vis.Entities(entity.transform.position, 8f, players);
                foreach (var player in players)
                {
                    if (player == null || !IsPlayer(player))
                        continue;
                    if (!IsInsidePortalProtection(dungeon, player.transform.position))
                        continue;
                    if (CanUsePortalProtection(player, dungeon))
                        return true;
                }
                return false;
            }
            finally
            {
                Pool.FreeUnmanaged(ref players);
            }
        }

        private BaseEntity FindSupportingFoundation(BaseEntity baseEntity)
        {
            Vector3 origin = baseEntity.transform.position + Vector3.up * 0.1f;
            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hitData, 4f))
            {
                return hitData.GetEntity();
            }
            return null;
        }

        private void SpawnTurret(Vector3 position, Quaternion rotation, TurretConfig turretConfig, ActiveDungeon activeDungeon)
        {
            var turretEntity = GameManager.server.CreateEntity(TurretPrefab, position, rotation) as AutoTurret;
            if (turretEntity == null)
            {
                LogDebug($"Failed to spawn turret at {position}.", 1);
                return;
            }
            turretEntity.startHealth = turretConfig.Health;
            turretEntity.InitializeHealth(turretConfig.Health, turretConfig.Health);
            using (var setFlags = turretEntity.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate_Flags))
            {
                setFlags.Set(BaseEntity.Flags.Busy, true);
                setFlags.Set(BaseEntity.Flags.Locked, true);
                setFlags.Set(BaseEntity.Flags.Reserved8, true);
            }
            turretEntity.OwnerID = OwnerID;
            var parent = FindSupportingFoundation(turretEntity);
            turretEntity.SetParent(parent, true, true);
            turretEntity.Spawn();
            turretEntity.inventory.canAcceptItem -= turretEntity.CanAcceptItem;
            turretEntity.inventory.canAcceptItem += CanAcceptTurretItem;
            var weapon = ItemManager.CreateByName(turretConfig.WeaponShortName);
            if (weapon != null)
            {
                if (!weapon.MoveToContainer(turretEntity.inventory, 0))
                    weapon.Remove();
                else if (weapon.contents != null)
                {
                    var laserSight = ItemManager.CreateByName("weapon.mod.lasersight");
                    if (laserSight != null && !laserSight.MoveToContainer(weapon.contents))
                        laserSight.Remove();
                }
            }
            turretEntity.UpdateAttachedWeapon();
            turretEntity.gameObject.AddComponent<TurretMonoBehaviour>();
            activeDungeon.Entities.Add(turretEntity);
            RegisterSpawnedEntityForDungeon(turretEntity, activeDungeon.UniqueId);
            LogDebug($"AutoTurret spawned at {position} with '{turretConfig.WeaponShortName}'.", 0);
        }

        private bool CanAcceptTurretItem(BasePlayer player, Item item, int pos)
        {
            if (item?.info == null)
                return false;
            if (pos == 0)
                return item.info.category == ItemCategory.Weapon;
            return item.info.category == ItemCategory.Ammunition;
        }

        private void SpawnLootBox(Vector3 position, Quaternion rotation, DungeonTierConfig tierConfig, ActiveDungeon activeDungeon)
        {
            var lootBoxEntity = GameManager.server.CreateEntity(tierConfig.LootBoxPrefabPath, position, rotation) as StorageContainer;
            if (lootBoxEntity == null)
            {
                LogDebug($"Failed to spawn loot box at {position}.", 1);
                return;
            }
            lootBoxEntity.skinID = tierConfig.LootBoxSkinID;
            lootBoxEntity.OwnerID = OwnerID;
            var parent = FindSupportingFoundation(lootBoxEntity);
            lootBoxEntity.SetParent(parent, true, true);
            lootBoxEntity.Spawn();
            var boxItems = GetLootBoxItems(tierConfig);
            PopulateLootContainer(lootBoxEntity.inventory, boxItems, tierConfig.MaxItemsPerBox);
            AddLock(lootBoxEntity);
            activeDungeon.Entities.Add(lootBoxEntity);
            RegisterSpawnedEntityForDungeon(lootBoxEntity, activeDungeon.UniqueId);
            LogDebug($"LootBox spawned at {position}.", 0);
        }

        private static Bounds TransformDungeonBounds(Bounds bounds, Transform source, Transform destination)
        {
            Bounds transformed = new Bounds(destination.InverseTransformPoint(source.TransformPoint(bounds.center)), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = bounds.center + Vector3.Scale(bounds.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                transformed.Encapsulate(destination.InverseTransformPoint(source.TransformPoint(corner)));
            }
            return transformed;
        }

        private static Bounds GetDungeonWorldBounds(Bounds bounds, Vector3 position, Quaternion rotation)
        {
            Bounds transformed = new Bounds(position + rotation * bounds.center, Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = bounds.center + Vector3.Scale(bounds.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                transformed.Encapsulate(position + rotation * corner);
            }
            return transformed;
        }

        private bool TryGetDungeonEnemyBounds(string prefab, out Bounds bounds)
        {
            if (_enemySpawnBounds.TryGetValue(prefab, out bounds))
                return true;
            var template = GameManager.server.FindPrefab(prefab)?.GetComponent<BaseEntity>();
            if (template == null)
                return false;
            bounds = template.bounds;
            foreach (Collider collider in template.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled || collider.isTrigger || ((1 << collider.gameObject.layer) & DungeonEnemyCollisionMask) == 0)
                    continue;
                Bounds colliderBounds;
                if (collider is BoxCollider box)
                {
                    colliderBounds = new Bounds(box.center, box.size);
                }
                else if (collider is CapsuleCollider capsule)
                {
                    Vector3 size = Vector3.one * (capsule.radius * 2f);
                    size[capsule.direction] = Mathf.Max(size[capsule.direction], capsule.height);
                    colliderBounds = new Bounds(capsule.center, size);
                }
                else if (collider is SphereCollider sphere)
                {
                    colliderBounds = new Bounds(sphere.center, Vector3.one * (sphere.radius * 2f));
                }
                else
                {
                    continue;
                }
                bounds.Encapsulate(TransformDungeonBounds(colliderBounds, collider.transform, template.transform));
            }
            Vector3 minimum = bounds.min - new Vector3(0.1f, 0f, 0.1f);
            Vector3 maximum = bounds.max + new Vector3(0.1f, 0.05f, 0.1f);
            minimum.y = 0.08f;
            maximum.y = Mathf.Max(maximum.y, minimum.y + 0.2f);
            bounds.SetMinMax(minimum, maximum);
            _enemySpawnBounds[prefab] = bounds;
            return true;
        }

        private bool IsDungeonEnemyPositionClear(ActiveDungeon dungeon, Bounds bounds, Vector3 position, Quaternion rotation, BaseEntity ignoredEntity)
        {
            if (!dungeon.DungeonBounds.Contains(position) || IsEnemyEntrancePositionBlocked(dungeon, position))
                return false;
            Bounds worldBounds = GetDungeonWorldBounds(bounds, position, rotation);
            foreach (Door door in dungeon.Doors)
            {
                if (door == null || door.IsDestroyed)
                    continue;
                Bounds doorway = door.WorldSpaceBounds().ToBounds();
                doorway.Expand(new Vector3(0.4f, 0.1f, 0.4f));
                if (doorway.Intersects(worldBounds))
                    return false;
            }
            foreach (BaseEntity other in dungeon.Npcs)
            {
                if (other == ignoredEntity || !IsDungeonNpcAlive(other) || !TryGetDungeonEnemyBounds(other.PrefabName, out Bounds otherBounds))
                    continue;
                if (worldBounds.Intersects(GetDungeonWorldBounds(otherBounds, other.transform.position, other.transform.rotation)))
                    return false;
            }
            foreach (Collider collider in Physics.OverlapBox(position + rotation * bounds.center, bounds.extents, rotation, DungeonEnemyCollisionMask, QueryTriggerInteraction.Ignore))
            {
                if (ignoredEntity != null && collider.transform.IsChildOf(ignoredEntity.transform))
                    continue;
                return false;
            }
            int xSteps = Mathf.Max(1, Mathf.CeilToInt(bounds.size.x / (SubCellSpacing * 0.5f)));
            int zSteps = Mathf.Max(1, Mathf.CeilToInt(bounds.size.z / (SubCellSpacing * 0.5f)));
            for (int x = 0; x <= xSteps; x++)
            for (int z = 0; z <= zSteps; z++)
            {
                Vector3 sample = position + rotation * new Vector3(Mathf.Lerp(bounds.min.x, bounds.max.x, (float)x / xSteps), 0f, Mathf.Lerp(bounds.min.z, bounds.max.z, (float)z / zSteps));
                if (IsEnemyEntrancePositionBlocked(dungeon, sample) || !TryGetDungeonFloorPoint(dungeon, sample, out Vector3 support) || Mathf.Abs(support.y + 0.02f - position.y) > 0.25f)
                    return false;
            }
            return true;
        }

        private bool TryFindDungeonEnemyPosition(
            ActiveDungeon dungeon,
            string prefab,
            Vector3 preferredPosition,
            Quaternion preferredRotation,
            BaseEntity ignoredEntity,
            out Vector3 position,
            out Quaternion rotation
        )
        {
            position = preferredPosition;
            rotation = preferredRotation;
            if (dungeon == null || dungeon.MarkedForRemoval || !TryGetDungeonEnemyBounds(prefab, out Bounds bounds))
                return false;
            Physics.SyncTransforms();
            var points = dungeon.SpawnPoints != null && dungeon.SpawnPoints.Count > 0 ? dungeon.SpawnPoints : dungeon.Foundations;
            var candidates = new List<Vector3>(points.Count + 1) { preferredPosition };
            candidates.AddRange(points.OrderBy(p => (p - preferredPosition).sqrMagnitude));
            foreach (Vector3 candidate in candidates)
            {
                if (!TryGetDungeonFloorPoint(dungeon, candidate, out Vector3 floor))
                    continue;
                Vector3 grounded = floor + Vector3.up * 0.02f;
                for (int turn = 0; turn < 5; turn++)
                {
                    Quaternion facing = turn == 0 ? preferredRotation : Quaternion.Euler(0f, (turn - 1) * 90f, 0f);
                    if (!IsDungeonEnemyPositionClear(dungeon, bounds, grounded, facing, ignoredEntity))
                        continue;
                    position = grounded;
                    rotation = facing;
                    return true;
                }
            }
            return false;
        }

        private bool TryReturnDungeonEnemyHome(BaseEntity entity, ActiveDungeon dungeon, Vector3 preferredPosition, Quaternion preferredRotation)
        {
            if (
                entity == null
                || entity.IsDestroyed
                || !TryFindDungeonEnemyPosition(dungeon, entity.PrefabName, preferredPosition, preferredRotation, entity, out Vector3 position, out Quaternion rotation)
            )
                return false;
            if (entity is ScientistNPC npc)
                npc.Teleport(position);
            else if (IsGen2Animal(entity))
                ApplyGen2DungeonPosition(entity, position, rotation);
            else
                entity.transform.position = position;
            entity.transform.rotation = rotation;
            var npcHome = entity.GetComponent<NpcHomePosition>();
            if (npcHome != null)
            {
                npcHome.HomePosition = position;
                npcHome.HomeRotation = rotation;
            }
            var animalHome = entity.GetComponent<AnimalHomePosition>();
            if (animalHome != null)
            {
                animalHome.HomePosition = position;
                animalHome.HomeRotation = rotation;
            }
            var movement = entity.GetComponent<DungeonAnimalMovement>();
            if (movement != null)
                movement.SetHomePosition(position);
            entity.UpdateNetworkGroup();
            entity.SendNetworkUpdateImmediate();
            Physics.SyncTransforms();
            return true;
        }

        private void RecheckDungeonAnimalPosition(BaseEntity entity, ActiveDungeon dungeon, Vector3 preferredPosition, Quaternion rotation)
        {
            if (entity == null || entity.IsDestroyed || dungeon == null || dungeon.MarkedForRemoval || !TryGetDungeonEnemyBounds(entity.PrefabName, out Bounds bounds))
                return;
            Physics.SyncTransforms();
            if (!TryGetDungeonFloorPoint(dungeon, preferredPosition, out Vector3 floor))
                return;
            Vector3 position = floor + Vector3.up * 0.02f;
            if (IsDungeonEnemyPositionClear(dungeon, bounds, position, rotation, entity))
                ApplyGen2DungeonPosition(entity, position, rotation);
        }

        private void SpawnNpc(Vector3 position, ActiveDungeon activeDungeon, bool boss = false)
        {
            if (activeDungeon == null)
                return;
            var rot = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            if (!TryFindDungeonEnemyPosition(activeDungeon, ScientistPrefab, position, rot, null, out position, out rot))
            {
                LogDebug("No clear dungeon floor position for the scientist; spawn skipped.", 2);
                return;
            }
            var npc = CreateScientist(position, rot);
            if (npc == null)
            {
                LogDebug($"Failed to spawn ScientistNPC at {position}", 1);
                return;
            }
            var cfg = boss ? activeDungeon.TierConfig.BossNpcConfig : activeDungeon.TierConfig.NpcConfig;
            ApplyNpcConfig(npc, cfg);
            var home = npc.GetComponent<NpcHomePosition>();
            if (home == null)
                home = npc.gameObject.AddComponent<NpcHomePosition>();
            home.HomePosition = position;
            home.HomeRotation = rot;
            DisableNpcNavAndBrainMovement(npc);
            npc.gameObject.AddComponent<NpcMovement>();
            if (boss)
                npc.gameObject.AddComponent<BossMonoBehaviour>();
            activeDungeon.Entities.Add(npc);
            activeDungeon.Npcs.Add(npc);
            RegisterSpawnedEntityForDungeon(npc, activeDungeon.UniqueId);
            Physics.SyncTransforms();
            LogDebug($"NPC spawned (native) at {position}.", 0);
        }

        public class NpcHomePosition : MonoBehaviour
        {
            public Vector3 HomePosition;
            public Quaternion HomeRotation;
        }

        public class AnimalHomePosition : MonoBehaviour
        {
            public Vector3 HomePosition;
            public Quaternion HomeRotation;
        }

        private void SpawnAnimal(Vector3 position, ActiveDungeon activeDungeon)
        {
            if (activeDungeon == null)
                return;
            var cfg = activeDungeon.TierConfig?.AnimalConfig;
            if (cfg == null || !cfg.Enabled)
                return;
            var species = GetRandomAnimalSpecies(cfg);
            if (species == null || !AnimalPrefabPaths.TryGetValue(species.Animal, out string prefab))
                return;
            float hp = Mathf.Max(1f, species.Health);
            Quaternion targetRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            if (!TryFindDungeonEnemyPosition(activeDungeon, prefab, position, targetRotation, null, out Vector3 targetPos, out targetRotation))
            {
                LogDebug($"No clear dungeon floor position for animal '{species.Animal}'; spawn skipped.", 2);
                return;
            }
            Vector3 spawnPos = FindAnimalPosition();
            if (spawnPos == Vector3.zero)
            {
                spawnPos = targetPos;
            }
            var entity = SpawnAnimalEntityCommon(prefab, spawnPos, hp, targetPos, targetRotation, activeDungeon);
            if (entity == null)
            {
                LogDebug($"Failed to spawn animal '{species.Animal}' ({prefab}) at {spawnPos}", 1);
                return;
            }
            var home = entity.GetComponent<AnimalHomePosition>();
            if (home == null)
                home = entity.gameObject.AddComponent<AnimalHomePosition>();
            home.HomePosition = targetPos;
            home.HomeRotation = entity.transform.rotation;
            ConfigureDungeonAnimal(entity, activeDungeon, cfg);
            activeDungeon.Entities.Add(entity);
            activeDungeon.Npcs.Add(entity);
            RegisterSpawnedEntityForDungeon(entity, activeDungeon.UniqueId);
            Physics.SyncTransforms();
            LogDebug($"Animal spawned at {entity.transform.position}: {species.Animal} (HP {hp:0})", 0);
        }

        private AnimalSpeciesConfig GetRandomAnimalSpecies(DungeonAnimalConfig cfg)
        {
            if (cfg?.Animals == null)
                return null;
            var eligible = cfg.Animals.Where(a => a != null && a.Enabled && a.SpawnChancePercentage > 0f && !string.IsNullOrEmpty(a.Animal) && AnimalPrefabPaths.ContainsKey(a.Animal)).ToList();
            if (eligible.Count == 0)
                return null;
            float totalWeight = eligible.Sum(a => Mathf.Clamp(a.SpawnChancePercentage, 0f, 100000f));
            float roll = Random.Range(0f, totalWeight);
            float cumulative = 0f;
            foreach (var species in eligible)
            {
                cumulative += Mathf.Clamp(species.SpawnChancePercentage, 0f, 100000f);
                if (roll <= cumulative)
                    return species;
            }
            return eligible[eligible.Count - 1];
        }

        private BaseEntity SpawnAnimalEntityCommon(string prefab, Vector3 spawnPos, float hp, Vector3? finalPos = null, Quaternion? finalRotation = null, ActiveDungeon dungeon = null)
        {
            var entity = GameManager.server.CreateEntity(prefab, spawnPos);
            if (entity == null)
                return null;
            if (entity is BaseCombatEntity combat)
            {
                var skeleton = combat.skeletonProperties;
                if (skeleton != null && skeleton.boneReference == null && skeleton.quickLookup == null)
                    combat.skeletonProperties = null;
            }
            if (entity is Chicken chicken)
            {
                chicken.EggDropFrequency = int.MaxValue;
                chicken.EggDefinition = null;
            }
            if (entity is BaseNPC2 gen2)
            {
                gen2.OwnerID = OwnerID;
                gen2.enableSaving = false;
                DisableAnimalGen2NavMesh(gen2);
                gen2.Spawn();
                gen2.InitializeHealth(hp, hp);
                DisableAnimalGen2NavMesh(gen2);
                if (finalPos.HasValue)
                {
                    Quaternion rotation = finalRotation ?? SafeLookRotation(gen2.transform, RandomFlatDir());
                    ApplyGen2DungeonPosition(gen2, finalPos.Value, rotation);
                    gen2.Invoke(() => RecheckDungeonAnimalPosition(gen2, dungeon, finalPos.Value, rotation), 0.05f);
                    gen2.Invoke(() => RecheckDungeonAnimalPosition(gen2, dungeon, finalPos.Value, rotation), 0.25f);
                }
                return gen2;
            }
            var animal = entity as BaseAnimalNPC;
            if (animal == null)
            {
                entity.Kill();
                return null;
            }
            animal.OwnerID = OwnerID;
            animal.spawnDeployableCorpseOnDeath = false;
            animal.enableSaving = false;
            DisableAnimalNavMesh(animal);
            animal.Spawn();
            animal.InitializeHealth(hp, hp);
            DisableAnimalNavMesh(animal);
            if (finalPos.HasValue)
            {
                animal.transform.position = finalPos.Value;
                animal.transform.rotation = finalRotation ?? SafeLookRotation(animal.transform, RandomFlatDir());
                animal.UpdateNetworkGroup();
                animal.SendNetworkUpdateImmediate();
            }
            return animal;
        }

        private void ConfigureDungeonAnimal(BaseEntity entity, ActiveDungeon activeDungeon, DungeonAnimalConfig cfg)
        {
            if (entity == null || activeDungeon == null || cfg == null)
                return;
            var combat = entity as BaseCombatEntity;
            if (combat == null)
                return;
            float attackRange = GetAnimalAttackRange(entity);
            float attackCooldown = GetAnimalAttackCooldown(entity);
            var movement = entity.GetComponent<DungeonAnimalMovement>();
            if (movement != null)
                UnityEngine.Object.Destroy(movement);
            movement = entity.gameObject.AddComponent<DungeonAnimalMovement>();
            movement.Init(activeDungeon, combat, cfg.MoveSpeed, cfg.SenseRange, cfg.MaxDistanceFromSpawnPoint, attackRange);
            var attack = entity.GetComponent<DungeonAnimalAttack>();
            if (attack != null)
                UnityEngine.Object.Destroy(attack);
            var gen2Aggro = entity.GetComponent<DungeonGen2AnimalAggroController>();
            if (gen2Aggro != null)
                UnityEngine.Object.Destroy(gen2Aggro);
            if (IsGen2Animal(entity))
            {
                gen2Aggro = entity.gameObject.AddComponent<DungeonGen2AnimalAggroController>();
                gen2Aggro.Init(activeDungeon, entity, cfg.Damage, attackRange, attackCooldown, cfg.SenseRange);
                return;
            }
            attack = entity.gameObject.AddComponent<DungeonAnimalAttack>();
            attack.Init(activeDungeon, combat, cfg.Damage, attackRange, attackCooldown);
        }

        private static bool IsGen2Animal(BaseEntity entity)
        {
            if (!(entity is BaseNPC2))
                return false;
            var name = entity.ShortPrefabName;
            return !string.IsNullOrEmpty(name) && (AnimalPrefabPaths.ContainsKey(name) || name.Equals("wolf2", StringComparison.OrdinalIgnoreCase));
        }

        private static float GetAnimalAttackRange(BaseEntity entity)
        {
            if (entity == null || !IsGen2Animal(entity))
                return Gen1AnimalAttackRange;
            switch ((entity.ShortPrefabName ?? string.Empty).ToLowerInvariant())
            {
                case "chicken":
                    return 0.95f;
                case "boar":
                    return 2.0f;
                case "stag":
                    return 2.2f;
                case "bear":
                    return 2.5f;
                case "wolf2":
                    return WolfAnimalAttackRange;
                case "crocodile":
                    return CrocodileAnimalAttackRange;
                case "bull":
                    return 2.6f;
                case "cow":
                    return 2.3f;
                case "calf":
                case "calfmale":
                    return 1.8f;
                case "sheep":
                    return 2.2f;
                case "lamb":
                    return 1.8f;
                default:
                    return TigerAnimalAttackRange;
            }
        }

        private static float GetAnimalAttackCooldown(BaseEntity entity)
        {
            if (entity == null || !IsGen2Animal(entity))
                return Gen1AnimalAttackCooldown;
            switch ((entity.ShortPrefabName ?? string.Empty).ToLowerInvariant())
            {
                case "chicken":
                    return 1.65f;
                case "boar":
                    return 3.3f;
                case "stag":
                    return 3.0f;
                case "bear":
                    return 2.6f;
                case "wolf2":
                    return WolfAnimalAttackCooldown;
                case "crocodile":
                    return CrocodileAnimalAttackCooldown;
                case "bull":
                case "cow":
                case "calf":
                case "calfmale":
                    return 1.9f;
                case "sheep":
                case "lamb":
                    return 2.0f;
                default:
                    return TigerAnimalAttackCooldown;
            }
        }

        private bool IsAnimalEntity(BaseEntity entity)
        {
            return entity is BaseAnimalNPC || IsGen2Animal(entity);
        }

        private void DisableAnimalGen2NavMesh(BaseEntity ent)
        {
            if (ent == null)
                return;
            var fsm = ent.GetComponent<FSMComponent>();
            if (fsm != null)
                fsm.SetFsmActive(false);
            var rootMotion = ent.GetComponent<RootMotionPlayer>();
            if (rootMotion != null && rootMotion.currentPlayState != null)
                rootMotion.StopServer(rootMotion.currentPlayState, false);
            var rustNav = ent.GetComponent<RustNavMeshAgent>();
            if (rustNav != null)
            {
                try
                {
                    rustNav.speed = 0f;
                    rustNav.overrideDirectionWS = null;
                }
                catch { }
                try
                {
                    rustNav.enabled = false;
                }
                catch { }
            }
            var navAgent = ent.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (navAgent != null)
            {
                try
                {
                    if (navAgent.enabled && navAgent.isOnNavMesh && navAgent.hasPath)
                        navAgent.ResetPath();
                    navAgent.enabled = false;
                }
                catch { }
            }
        }

        private static void ApplyGen2DungeonPosition(BaseEntity entity, Vector3 position, Quaternion rotation)
        {
            if (entity == null || entity.IsDestroyed)
                return;
            var rootMotion = entity.GetComponent<RootMotionPlayer>();
            if (rootMotion != null && rootMotion.currentPlayState != null)
                rootMotion.StopServer(rootMotion.currentPlayState, false);
            var fsm = entity.GetComponent<FSMComponent>();
            if (fsm != null)
                fsm.SetFsmActive(false);
            var rustNav = entity.GetComponent<RustNavMeshAgent>();
            if (rustNav != null)
            {
                try
                {
                    rustNav.speed = 0f;
                    rustNav.overrideDirectionWS = null;
                }
                catch { }
                try
                {
                    rustNav.enabled = false;
                }
                catch { }
            }
            var navAgent = entity.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (navAgent != null)
            {
                try
                {
                    if (navAgent.enabled && navAgent.isOnNavMesh && navAgent.hasPath)
                        navAgent.ResetPath();
                    navAgent.enabled = false;
                }
                catch { }
            }
            entity.transform.position = position;
            entity.transform.rotation = rotation;
            entity.UpdateNetworkGroup();
            entity.SendNetworkUpdateImmediate();
        }

        private static void DisableCurrentRootMotionNavConstraint(BaseEntity entity, bool freezeRotation = false)
        {
            if (entity == null)
                return;
            var rootMotion = entity.GetComponent<RootMotionPlayer>();
            if (rootMotion == null || rootMotion.currentPlayState == null)
                return;
            rootMotion.currentPlayState.constrainToNavmesh = false;
            rootMotion.currentPlayState.warps = new[] { new RootMotionPlayer.Warp(0f, 30f, Vector3.zero, freezeRotation ? 0f : 1f) };
        }

        private static void DisableAnimalNavMesh(BaseAnimalNPC animal)
        {
            if (animal == null)
                return;
            try
            {
                animal.brain?.StopMovementTick();
                if (animal.brain != null)
                    animal.brain.enabled = false;
                animal.HasBrain = false;
                animal.AttackTarget = null;
                animal.ChaseTransform = null;
                animal.CurrentBehaviour = BaseNpc.Behaviour.Idle;
                animal.AttackDamage = 0f;
                animal.AttackRange = Gen1AnimalAttackRange;
                animal.AttackRate = 999f;
                AIThinkManager.RemoveAnimal(animal);
                animal.CancelInvoke(nameof(BaseNpc.TickAi));
                animal.CancelInvoke(nameof(BaseNpc.StartAttack));
            }
            catch { }
            try
            {
                if (animal.NavAgent != null)
                {
                    if (animal.NavAgent.enabled && animal.NavAgent.isOnNavMesh)
                    {
                        if (animal.NavAgent.hasPath)
                            animal.NavAgent.ResetPath();
                        animal.NavAgent.isStopped = true;
                    }
                    animal.NavAgent.enabled = false;
                }
            }
            catch { }
            try
            {
                animal.Stats.Family = BaseNpc.AiStatistics.FamilyEnum.Bear;
            }
            catch { }
            var nav = animal.GetComponent<BaseNavigator>();
            if (nav == null)
                return;
            try
            {
                if (nav.Agent != null)
                {
                    if (nav.Agent.enabled && nav.Agent.isOnNavMesh)
                    {
                        if (nav.Agent.hasPath)
                            nav.Agent.ResetPath();
                        nav.Agent.isStopped = true;
                    }
                    nav.Agent.enabled = false;
                }
                nav.CanUseBaseNav = false;
                nav.CanUseCustomNav = false;
                nav.CanUseNavMesh = false;
                nav.CanUseAStar = false;
                nav.paused = true;
                nav.enabled = false;
            }
            catch { }
        }

        private Vector3 FindAnimalPosition()
        {
            if (_cachedAnimalNavmeshSpawnPosition != Vector3.zero)
                return _cachedAnimalNavmeshSpawnPosition;
            if (_animalNavmeshLookupFailed)
                return Vector3.zero;
            int maxAttempts = 1500;
            float radius = World.Size * 0.48f;
            Vector3 center = TerrainMeta.Center;
            for (int i = 0; i < maxAttempts; i++)
            {
                Vector2 offset = Random.insideUnitCircle * radius;
                Vector3 pXZ = new Vector3(center.x + offset.x, 0f, center.z + offset.y);
                if (TerrainMeta.TopologyMap != null)
                {
                    int topo = TerrainMeta.TopologyMap.GetTopology(pXZ);
                    if ((topo & (int)TerrainTopology.Enum.Ocean) != 0)
                        continue;
                }
                float terrainH = TerrainMeta.HeightMap.GetHeight(pXZ);
                if (TriggerSafeZone.allSafeZones.Any(z => Vector3Ex.Distance2D(z.transform.position, pXZ) <= 100f))
                    continue;
                if (TrySampleAnimalNavMeshPosition(new Vector3(pXZ.x, terrainH + 0.1f, pXZ.z), 30f, out Vector3 navPosition))
                {
                    _cachedAnimalNavmeshSpawnPosition = navPosition;
                    return _cachedAnimalNavmeshSpawnPosition;
                }
            }
            _animalNavmeshLookupFailed = true;
            return Vector3.zero;
        }

        private bool TrySampleAnimalNavMeshPosition(Vector3 sourcePosition, float maxDistance, out Vector3 navPosition)
        {
            navPosition = Vector3.zero;
            try
            {
                if (NavMesh.SamplePosition(sourcePosition, out NavMeshHit hit, maxDistance, NavMesh.AllAreas))
                {
                    navPosition = hit.position;
                    return true;
                }
            }
            catch { }
            int settingsCount = 0;
            try
            {
                settingsCount = NavMesh.GetSettingsCount();
            }
            catch { }
            for (int i = 0; i < settingsCount; i++)
            {
                try
                {
                    var settings = NavMesh.GetSettingsByIndex(i);
                    if (settings.agentTypeID == -1)
                        continue;
                    var filter = new NavMeshQueryFilter { agentTypeID = settings.agentTypeID, areaMask = NavMesh.AllAreas };
                    if (NavMesh.SamplePosition(sourcePosition, out NavMeshHit filteredHit, maxDistance, filter))
                    {
                        navPosition = filteredHit.position;
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private static Quaternion SafeLookRotation(Transform transformFallback, Vector3 direction)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 1e-6f)
                return transformFallback != null ? transformFallback.rotation : Quaternion.identity;
            return Quaternion.LookRotation(direction.normalized, Vector3.up);
        }

        private static Vector3 RandomFlatDir()
        {
            var v = Random.insideUnitSphere;
            v.y = 0f;
            if (v.sqrMagnitude < 1e-6f)
                return Vector3.forward;
            return v.normalized;
        }

        private static void FaceEntityDirection(BaseEntity entity, Vector3 direction)
        {
            if (entity == null)
                return;
            direction.y = 0f;
            if (direction.sqrMagnitude < 1e-6f)
                return;
            entity.transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        }

        private void CloseDoorsOnDungeonEmpty(ActiveDungeon dungeon)
        {
            bool closeEntranceDoors = _config?.CloseEntranceDoorsWhenEmpty == true;
            bool closeDungeonDoors = _config?.CloseDungeonDoorsWhenEmpty == true;
            if (dungeon?.Doors == null || (!closeEntranceDoors && !closeDungeonDoors))
                return;
            foreach (var door in dungeon.Doors)
            {
                if (door == null || door.IsDestroyed)
                    continue;
                bool isEntranceDoor = dungeon.EntranceDoors != null && dungeon.EntranceDoors.Contains(door);
                if (isEntranceDoor ? !closeEntranceDoors : !closeDungeonDoors)
                    continue;
                door.SetOpen(false);
                door.SendNetworkUpdate();
            }
        }

        private bool HasOtherLivingPlayersInsideDungeon(ActiveDungeon dungeon, ulong excludedUserId)
        {
            foreach (var insidePlayer in GetPlayersPhysicallyInsideDungeon(dungeon))
            {
                if (insidePlayer.userID != excludedUserId)
                    return true;
            }
            return false;
        }

        private void HandlePlayerDungeonDeparture(ActiveDungeon dungeon, ulong departedUserId = 0UL)
        {
            if (dungeon == null || !dungeon.Spawned || dungeon.MarkedForRemoval)
                return;
            NextTick(() =>
            {
                if (dungeon == null || !dungeon.Spawned || dungeon.MarkedForRemoval)
                    return;
                if (HasOtherLivingPlayersInsideDungeon(dungeon, departedUserId))
                    return;
                CloseDoorsOnDungeonEmpty(dungeon);
                if (_config?.ResetEnemiesOnPlayerDeparture != true)
                    return;
                float now = Time.realtimeSinceStartup;
                if (now - dungeon.LastEnemyResetAt < 0.5f)
                    return;
                dungeon.LastEnemyResetAt = now;
                ResetDungeonNpcsToHome(dungeon);
            });
        }

        private bool IsInDungeonEntranceCorridor(ActiveDungeon dungeon, Vector3 position, float margin = 0f)
        {
            if (dungeon?.EntranceCorridorSegments == null)
                return false;
            foreach (var segment in dungeon.EntranceCorridorSegments)
            {
                Bounds bounds = segment;
                if (margin > 0f)
                    bounds.Expand(new Vector3(margin * 2f, 0f, margin * 2f));
                if (bounds.Contains(position))
                    return true;
            }
            return false;
        }

        private bool IsInDungeonEntranceSafeZone(ActiveDungeon dungeon, Vector3 position)
        {
            return dungeon != null && dungeon.HasEntranceSafeZone && dungeon.EntranceSafeZoneBounds.Contains(position);
        }

        private bool IsEnemyEntrancePositionBlocked(ActiveDungeon dungeon, Vector3 position)
        {
            return IsInDungeonEntranceCorridor(dungeon, position, 0.35f);
        }

        private bool IsPlayerInEntranceForEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null)
                return false;
            return IsInDungeonEntranceSafeZone(GetActiveDungeonByEntity(entity), player.transform.position);
        }

        private bool BlockAndNotifyEntranceAttack(BasePlayer attacker, BaseEntity target)
        {
            if (!IsPlayerInEntranceForEntity(attacker, target))
                return false;
            if (attacker.IsConnected && attacker.userID.IsSteamId())
            {
                float now = Time.realtimeSinceStartup;
                if (!_lastEntranceAttackWarning.TryGetValue(attacker.userID, out float lastTime) || now - lastTime >= EntranceAttackWarningCooldown)
                {
                    _lastEntranceAttackWarning[attacker.userID] = now;
                    NotifyPlayer(attacker, "EntranceCombatBlocked", 0);
                }
            }
            return true;
        }

        private void ResetDungeonNpcsToHome(ActiveDungeon dungeon)
        {
            if (dungeon == null || dungeon.Npcs == null)
                return;
            foreach (var ent in dungeon.Npcs.ToList())
            {
                if (!IsDungeonNpcAlive(ent))
                    continue;
                var npc = ent as ScientistNPC;
                if (npc != null)
                {
                    var home = npc.GetComponent<NpcHomePosition>();
                    if (home == null)
                        continue;
                    if (!TryReturnDungeonEnemyHome(npc, dungeon, home.HomePosition, home.HomeRotation))
                        continue;
                    var mv = npc.GetComponent<NpcMovement>();
                    if (mv != null)
                        UnityEngine.Object.Destroy(mv);
                    npc.gameObject.AddComponent<NpcMovement>();
                    continue;
                }
                if (IsAnimalEntity(ent))
                {
                    var home = ent.GetComponent<AnimalHomePosition>();
                    if (home == null)
                        continue;
                    if (!TryReturnDungeonEnemyHome(ent, dungeon, home.HomePosition, home.HomeRotation))
                        continue;
                    var movement = ent.GetComponent<DungeonAnimalMovement>();
                    if (movement != null)
                        UnityEngine.Object.Destroy(movement);
                    var attack = ent.GetComponent<DungeonAnimalAttack>();
                    if (attack != null)
                        UnityEngine.Object.Destroy(attack);
                    var gen2Aggro = ent.GetComponent<DungeonGen2AnimalAggroController>();
                    if (gen2Aggro != null)
                        UnityEngine.Object.Destroy(gen2Aggro);
                    var cfg = dungeon.TierConfig?.AnimalConfig;
                    if (cfg != null && cfg.Enabled)
                        ConfigureDungeonAnimal(ent, dungeon, cfg);
                }
            }
        }

        private void OnPlayerCorpseSpawned(BasePlayer player, PlayerCorpse corpse)
        {
            if (corpse == null)
                return;
            ulong ownerId = corpse.playerSteamID.IsSteamId() ? corpse.playerSteamID : (player != null ? player.userID : 0UL);
            SchedulePlayerLootRecovery(corpse, ownerId);
        }

        private void OnEntitySpawned(BaseCorpse corpse)
        {
            if (corpse == null)
                return;
            if (corpse is PlayerCorpse playerCorpse && TryGetPendingPlayerLootRecovery(playerCorpse.playerSteamID, out var recoveryPosition))
            {
                MovePlayerCorpse(playerCorpse, recoveryPosition);
                playerCorpse.UpdateNetworkGroup();
                return;
            }
            if (corpse.parentEnt == null)
                return;
            var animal = corpse.parentEnt as BaseEntity;
            if (animal == null)
                return;
            if (!IsDungeonEntity(animal))
                return;
            if (!IsAnimalEntity(animal))
                return;
            animal.CancelInvoke();
            corpse.CancelInvoke();
            corpse.Invoke("KillMessage", 1f);
        }

        private void OnEntitySpawned(DroppedItemContainer container)
        {
            if (container == null || !container.playerSteamID.IsSteamId())
                return;
            SchedulePlayerLootRecovery(container, container.playerSteamID);
        }

        private void DisableNpcNavAndBrainMovement(ScientistNPC npc)
        {
            if (npc == null || npc.IsDestroyed)
                return;
            NextTick(() =>
            {
                try
                {
                    var brain = npc.Brain;
                    if (brain != null)
                    {
                        if (brain.Navigator != null)
                        {
                            brain.Navigator.CanUseNavMesh = false;
                            try
                            {
                                var agent = brain.Navigator.Agent;
                                if (agent != null)
                                    agent.enabled = false;
                            }
                            catch { }
                            try
                            {
                                brain.Navigator.Stop();
                            }
                            catch { }
                        }
                        try
                        {
                            brain.enabled = false;
                        }
                        catch { }
                    }
                    npc.enableSaving = false;
                }
                catch { }
            });
        }

        private DungeonTierConfig FindTierByName(string name)
        {
            if (_config?.Tiers == null || string.IsNullOrEmpty(name))
                return null;
            return _config.Tiers.FirstOrDefault(t => t != null && string.Equals(t.TierName, name, StringComparison.OrdinalIgnoreCase));
        }

        private static void ApplyLootItemMetadata(Item item, ItemConfig config)
        {
            if (item == null || config == null)
                return;
            if (!string.IsNullOrEmpty(config.CustomName))
                item.name = config.CustomName;
            if (!string.IsNullOrEmpty(config.Text))
                item.text = config.Text;
            if (config.ArmorSlots > 0 && item.contents == null && item.info.TryGetComponent<ItemModContainerArmorSlot>(out var armorSlots))
                armorSlots.CreateAtCapacity(Mathf.Min(config.ArmorSlots, armorSlots.MaxSlots), item);
        }

        private List<ItemConfig> GetNpcLootItems(NpcConfig cfg)
        {
            return cfg == null ? new List<ItemConfig>() : GetConfiguredLootItems(cfg.LootDropDataFile);
        }

        private List<ItemConfig> GetLootBoxItems(DungeonTierConfig tierConfig)
        {
            return tierConfig == null ? new List<ItemConfig>() : GetConfiguredLootItems(tierConfig.LootBoxDataFile);
        }

        private List<ItemConfig> GetConfiguredLootItems(string dataFile)
        {
            var items = string.IsNullOrEmpty(dataFile) ? GetDefaultLootItems() : LoadLootItems(dataFile);
            if (items == null || items.Count == 0)
                items = GetDefaultLootItems();
            return items.Where(item => item != null && item.MinimumAmount > 0 && item.MaximumAmount > 0).ToList();
        }

        private List<ItemConfig> LoadLootItems(string dataFile)
        {
            if (string.IsNullOrEmpty(dataFile))
                return new List<ItemConfig>();
            var file = Interface.Oxide.DataFileSystem.GetFile(dataFile);
            var info = new FileInfo(file.Filename);
            DateTime lastWriteTime = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            long length = info.Exists ? info.Length : -1L;
            if (_lootFileCache.TryGetValue(dataFile, out var cached) && cached.LastWriteTimeUtc == lastWriteTime && cached.Length == length)
                return cached.Items;
            var obj = file.ReadObject<LootDropConfig>();
            var items = obj?.Items ?? new List<ItemConfig>();
            info.Refresh();
            if (length >= 0L && (!info.Exists || info.LastWriteTimeUtc != lastWriteTime || info.Length != length))
            {
                _lootFileCache.Remove(dataFile);
                return items;
            }
            _lootFileCache[dataFile] = new LootFileCache
            {
                LastWriteTimeUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue,
                Length = info.Exists ? info.Length : -1L,
                Items = items,
            };
            return items;
        }

        private void EnsureLootDataFiles()
        {
            EnsureDefaultLootFile();
            EnsureLootItemTextField("DungeonEvents/Default_Loot");
            if (_config.Tiers == null || _config.Tiers.Count == 0)
                return;
            foreach (var tier in _config.Tiers)
            {
                if (tier == null)
                    continue;
                var tierFolder = Sanitize(tier.TierName);
                if (tier.NpcConfig == null)
                    tier.NpcConfig = new NpcConfig();
                if (tier.BossNpcConfig == null)
                    tier.BossNpcConfig = new NpcConfig();
                tier.NpcConfig.LootDropDataFile = $"DungeonEvents/{tierFolder}/npc_loot";
                EnsureEmptyLootFile(tier.NpcConfig.LootDropDataFile);
                EnsureLootItemTextField(tier.NpcConfig.LootDropDataFile);
                tier.BossNpcConfig.LootDropDataFile = $"DungeonEvents/{tierFolder}/boss_loot";
                EnsureEmptyLootFile(tier.BossNpcConfig.LootDropDataFile);
                EnsureLootItemTextField(tier.BossNpcConfig.LootDropDataFile);
                tier.LootBoxDataFile = $"DungeonEvents/{tierFolder}/lootbox";
                EnsureEmptyLootFile(tier.LootBoxDataFile);
                EnsureLootItemTextField(tier.LootBoxDataFile);
            }
        }

        private void EnsureEmptyLootFile(string dataFile)
        {
            if (string.IsNullOrEmpty(dataFile))
                return;
            var file = Interface.Oxide.DataFileSystem.GetFile(dataFile);
            var current = file.ReadObject<LootDropConfig>();
            if (current == null || current.Items == null)
            {
                file.WriteObject(new LootDropConfig { Items = new List<ItemConfig>() });
            }
        }

        private void EnsureLootItemTextField(string dataFile)
        {
            if (string.IsNullOrEmpty(dataFile))
                return;
            var file = Interface.Oxide.DataFileSystem.GetFile(dataFile);
            var lootData = file.ReadObject<JObject>();
            var items = lootData?["Items"] as JArray;
            if (items == null)
                return;
            bool updated = false;
            foreach (var token in items)
            {
                var entry = token as JObject;
                if (entry == null || entry.Property(LootItemTextFieldKey) != null)
                    continue;
                entry[LootItemTextFieldKey] = string.Empty;
                updated = true;
            }
            if (updated)
                file.WriteObject(lootData);
        }

        private void EnsureDefaultLootFile()
        {
            var file = Interface.Oxide.DataFileSystem.GetFile("DungeonEvents/Default_Loot");
            var current = file.ReadObject<LootDropConfig>();
            if (current == null || current.Items == null || current.Items.Count == 0)
            {
                file.WriteObject(
                    new LootDropConfig
                    {
                        Items = new List<ItemConfig>
                        {
                            new ItemConfig
                            {
                                ShortName = "ammo.rifle",
                                InclusionChancePercentage = 15.0f,
                                MinimumAmount = 100,
                                MaximumAmount = 300,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "sulfur.ore",
                                InclusionChancePercentage = 15.0f,
                                MinimumAmount = 100,
                                MaximumAmount = 300,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "scrap",
                                InclusionChancePercentage = 70.0f,
                                MinimumAmount = 500,
                                MaximumAmount = 2000,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "grenade.f1",
                                InclusionChancePercentage = 10.0f,
                                MinimumAmount = 1,
                                MaximumAmount = 5,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "crude.oil",
                                InclusionChancePercentage = 40.0f,
                                MinimumAmount = 10,
                                MaximumAmount = 100,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "diesel_barrel",
                                InclusionChancePercentage = 30.0f,
                                MinimumAmount = 1,
                                MaximumAmount = 5,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "cctv.camera",
                                InclusionChancePercentage = 20.0f,
                                MinimumAmount = 5,
                                MaximumAmount = 20,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "gears",
                                InclusionChancePercentage = 20.0f,
                                MinimumAmount = 1,
                                MaximumAmount = 20,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "metal.refined",
                                InclusionChancePercentage = 20.0f,
                                MinimumAmount = 10,
                                MaximumAmount = 80,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "hazmatsuit",
                                InclusionChancePercentage = 10.0f,
                                MinimumAmount = 1,
                                MaximumAmount = 1,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "ammo.rocket.hv",
                                InclusionChancePercentage = 10.0f,
                                MinimumAmount = 1,
                                MaximumAmount = 20,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "ammo.rifle.hv",
                                InclusionChancePercentage = 10.0f,
                                MinimumAmount = 100,
                                MaximumAmount = 300,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "ammo.rocket.fire",
                                InclusionChancePercentage = 10.0f,
                                MinimumAmount = 5,
                                MaximumAmount = 10,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "lowgradefuel",
                                InclusionChancePercentage = 70.0f,
                                MinimumAmount = 50,
                                MaximumAmount = 200,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "rifle.lr300",
                                InclusionChancePercentage = 5.0f,
                                MinimumAmount = 1,
                                MaximumAmount = 1,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "rifle.m39",
                                InclusionChancePercentage = 5.0f,
                                MinimumAmount = 1,
                                MaximumAmount = 1,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "pistol.m92",
                                InclusionChancePercentage = 10.0f,
                                MinimumAmount = 1,
                                MaximumAmount = 1,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "lmg.m249",
                                InclusionChancePercentage = 2.0f,
                                MinimumAmount = 1,
                                MaximumAmount = 1,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "syringe.medical",
                                InclusionChancePercentage = 70.0f,
                                MinimumAmount = 10,
                                MaximumAmount = 30,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "metal.ore",
                                InclusionChancePercentage = 80.0f,
                                MinimumAmount = 100,
                                MaximumAmount = 300,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "metalpipe",
                                InclusionChancePercentage = 60.0f,
                                MinimumAmount = 1,
                                MaximumAmount = 5,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "metalspring",
                                InclusionChancePercentage = 60.0f,
                                MinimumAmount = 1,
                                MaximumAmount = 5,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "minigun",
                                InclusionChancePercentage = 2.0f,
                                MinimumAmount = 1,
                                MaximumAmount = 1,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "riflebody",
                                InclusionChancePercentage = 60.0f,
                                MinimumAmount = 5,
                                MaximumAmount = 10,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "ammo.rocket.basic",
                                InclusionChancePercentage = 30.0f,
                                MinimumAmount = 1,
                                MaximumAmount = 5,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "rocket.launcher",
                                InclusionChancePercentage = 5.0f,
                                MinimumAmount = 1,
                                MaximumAmount = 1,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "rifle.sks",
                                InclusionChancePercentage = 7.0f,
                                MinimumAmount = 1,
                                MaximumAmount = 1,
                                CustomName = "",
                                SkinID = 0,
                            },
                            new ItemConfig
                            {
                                ShortName = "tarp",
                                InclusionChancePercentage = 60.0f,
                                MinimumAmount = 1,
                                MaximumAmount = 5,
                                CustomName = "",
                                SkinID = 0,
                            },
                        },
                    }
                );
            }
        }

        private List<ItemConfig> GetDefaultLootItems()
        {
            return LoadLootItems("DungeonEvents/Default_Loot");
        }

        private void EnqueueDungeonRequest(BasePlayer player, DungeonTierConfig tier, bool lockToPlayer = false, Vector3 customPortalPosition = default(Vector3))
        {
            var request = new DungeonRequest
            {
                Player = player,
                Tier = tier,
                LockToPlayer = lockToPlayer,
                Origin = player != null ? DungeonOrigin.Buy : DungeonOrigin.AutoSpawn,
                PortalPosition = customPortalPosition,
                UsesCustomPortalPosition = customPortalPosition != Vector3.zero,
            };
            _dungeonQueue.Enqueue(request);
            if (player != null)
            {
                NotifyPlayer(player, "ReceivedRequest", 0);
                if (!_isCreatingDungeon)
                    ProcessDungeonQueue();
            }
        }

        private void ProcessDungeonQueue()
        {
            if (_isUnloading || _isCreatingDungeon || IsDungeonCreationPaused() || _dungeonQueue.Count == 0)
                return;
            DungeonRequest req = _dungeonQueue.Dequeue();
            if (req == null || req.Tier == null)
                return;
            if (!IsTierUnlockedByWipeTime(req.Tier))
            {
                if (req.Player != null)
                    NotifyPlayer(req.Player, "ThisTierDisabled", 0);
                return;
            }
            bool isManualAdminCreate = req.Origin == DungeonOrigin.Buy && req.Player != null && !req.LockToPlayer && IsAdmin(req.Player);
            if (req.Origin == DungeonOrigin.AutoSpawn)
            {
                int totalAuto = _activeDungeons.Count(d => d.Origin == DungeonOrigin.AutoSpawn);
                if (totalAuto >= _config.DungeonSpawn.MaxTotalActiveDungeonsAutoSpawn)
                {
                    _dungeonFailedAutoSpawnQueue.Enqueue(req);
                    return;
                }
                if (!req.Tier.EnableAutoSpawn)
                {
                    _dungeonFailedAutoSpawnQueue.Enqueue(req);
                    return;
                }
                int activeAutoSameTier = _activeDungeons.Count(d => d.Origin == DungeonOrigin.AutoSpawn && d.TierConfig == req.Tier);
                if (req.Tier.MaxActiveAutoSpawn > 0 && activeAutoSameTier >= req.Tier.MaxActiveAutoSpawn)
                {
                    _dungeonFailedAutoSpawnQueue.Enqueue(req);
                    return;
                }
            }
            else if (isManualAdminCreate)
            {
                int totalBuy = _activeDungeons.Count(d => d.Origin == DungeonOrigin.Buy);
                if (totalBuy >= _config.DungeonSpawn.MaxTotalActiveDungeonsBuy)
                {
                    NotifyPlayer(req.Player, "MaxActiveDungeonsReached", 0);
                    return;
                }
                if (!req.Tier.EnableBuy)
                {
                    NotifyPlayer(req.Player, "ThisTierDisabled", 0);
                    return;
                }
                int activeBuySameTier = _activeDungeons.Count(d => d.Origin == DungeonOrigin.Buy && d.TierConfig == req.Tier);
                if (req.Tier.MaxActiveBuy > 0 && activeBuySameTier >= req.Tier.MaxActiveBuy)
                {
                    NotifyPlayer(req.Player, "MaxActiveDungeonsReached", 0);
                    return;
                }
            }
            else
            {
                int totalBuy = _activeDungeons.Count(d => d.Origin == DungeonOrigin.Buy);
                if (totalBuy >= _config.DungeonSpawn.MaxTotalActiveDungeonsBuy)
                {
                    _dungeonFailedBuyQueue.Enqueue(req);
                    if (req.Player != null)
                        NotifyPlayer(req.Player, "MaxActiveDungeonsReached", 0);
                    return;
                }
                if (!req.Tier.EnableBuy)
                {
                    _dungeonFailedBuyQueue.Enqueue(req);
                    if (req.Player != null)
                        NotifyPlayer(req.Player, "MaxActiveDungeonsReached", 0);
                    return;
                }
                int activeBuySameTier = _activeDungeons.Count(d => d.Origin == DungeonOrigin.Buy && d.TierConfig == req.Tier);
                if (req.Tier.MaxActiveBuy > 0 && activeBuySameTier >= req.Tier.MaxActiveBuy)
                {
                    _dungeonFailedBuyQueue.Enqueue(req);
                    if (req.Player != null)
                        NotifyPlayer(req.Player, "MaxActiveDungeonsReached", 0);
                    return;
                }
            }
            _currentDungeonRequest = req;
            _isCreatingDungeon = true;
            _dungeonCreationRoutine = ServerMgr.Instance.StartCoroutine(CreateDungeon(req));
        }

        private int GetTierCooldownSeconds(DungeonTierConfig tier)
        {
            if (tier != null && tier.BuyCooldown > 0)
                return tier.BuyCooldown;
            return _config?.DungeonSpawn?.BuyCooldown ?? 0;
        }

        private void InitializeTierInfoMap()
        {
            _tierInfoMap = new Dictionary<DungeonTierConfig, TierInfo>();
            if (_config?.Tiers == null || _config.Tiers.Count == 0)
                return;
            foreach (var cfg in _config.Tiers)
            {
                if (cfg == null)
                    continue;
                var name = string.IsNullOrEmpty(cfg.TierName) ? "Tier" : cfg.TierName;
                Color marker;
                if (!TryParseHexColor(cfg.MarkerColorHex, out marker))
                {
                    var pair = GetTierColor(name);
                    marker = pair.color;
                }
                _tierInfoMap[cfg] = new TierInfo
                {
                    Name = name,
                    MapName = cfg.MapName,
                    BuyCost = cfg.BuyCost,
                    MarkerColor = marker,
                    ColorString = ToUiColorString(marker),
                    Enabled = cfg.EnableBuy,
                };
            }
        }

        private ActiveDungeon GetActiveDungeonByPortal(BasePortal portal)
        {
            if (portal == null)
                return null;
            if (portal.net != null && _dungeonByEntityId.TryGetValue(portal.net.ID.Value, out var indexedDungeon))
                return indexedDungeon;
            foreach (var dungeon in _activeDungeons)
            {
                if (dungeon == null || dungeon.BasePortal != portal)
                    continue;
                IndexDungeonEntity(portal, dungeon);
                return dungeon;
            }
            return null;
        }

        private ActiveDungeon GetActiveDungeonByEntity(BaseEntity entity)
        {
            if (entity == null)
                return null;
            if (entity.net != null && _dungeonByEntityId.TryGetValue(entity.net.ID.Value, out var indexedDungeon))
                return indexedDungeon;
            foreach (var dungeon in _activeDungeons)
            {
                if (dungeon?.Entities == null || !dungeon.Entities.Contains(entity))
                    continue;
                IndexDungeonEntity(entity, dungeon);
                return dungeon;
            }
            return null;
        }

        private string GetAvailableTiers()
        {
            if (_config?.Tiers == null || _config.Tiers.Count == 0)
                return "None";
            var names = _config.Tiers.Where(t => t != null && t.EnableBuy && IsTierUnlockedByWipeTime(t)).Select(t => t.TierName);
            return string.Join(", ", names);
        }

        private void HandleDungeonTimers()
        {
            foreach (var dungeon in _activeDungeons.ToList())
            {
                if (dungeon == null || !dungeon.Spawned)
                    continue;
                if (!dungeon.ActivationTime.HasValue)
                    continue;
                int lifetime = dungeon.TierConfig?.MaxDungeonLifetime ?? 0;
                if (lifetime <= 0)
                    continue;
                double elapsed = (DateTime.UtcNow - dungeon.ActivationTime.Value).TotalSeconds;
                if (elapsed >= lifetime)
                {
                    TeleportPlayersAndDestroyDungeon(dungeon);
                }
            }
            RemoveInactiveDungeons();
            if (IsDungeonCreationPaused())
            {
                UpdateMarkers();
                return;
            }
            int activeAutoSpawn = _activeDungeons.Count(d => d.Origin == DungeonOrigin.AutoSpawn);
            if (_config.DungeonSpawn.MaxTotalActiveDungeonsAutoSpawn > activeAutoSpawn)
            {
                if (_dungeonFailedAutoSpawnQueue.Count > 0)
                {
                    var req = _dungeonFailedAutoSpawnQueue.Dequeue();
                    _dungeonQueue.Enqueue(req);
                }
            }
            int activeBuyDungeons = _activeDungeons.Count(d => d.Origin == DungeonOrigin.Buy);
            if (_config.DungeonSpawn.MaxTotalActiveDungeonsBuy > activeBuyDungeons)
            {
                if (_dungeonFailedBuyQueue.Count > 0)
                {
                    var req = _dungeonFailedBuyQueue.Dequeue();
                    _dungeonQueue.Enqueue(req);
                }
            }
            if (_config.DungeonSpawn.EnableAutoSpawn && activeAutoSpawn < _config.DungeonSpawn.MaxTotalActiveDungeonsAutoSpawn)
            {
                AutoSpawnDungeon();
            }
            UpdateMarkers();
            ProcessDungeonQueue();
        }

        private T CreateMapMarker<T>(string prefab, Vector3 position)
            where T : BaseEntity
        {
            var entity = GameManager.server.CreateEntity(prefab, position) as T;
            entity?.Spawn();
            return entity;
        }

        private bool ShouldCreateDungeonMarker(ActiveDungeon dungeon)
        {
            if (dungeon == null || _config?.DungeonSpawn == null)
                return false;
            if (dungeon.Origin == DungeonOrigin.AutoSpawn)
                return _config.DungeonSpawn.EnableMapMarkersAutoSpawn;
            return !_config.DungeonSpawn.ShowPurchaseAnnouncementToOwnerOnly && _config.DungeonSpawn.EnableMapMarkersBuy;
        }

        private bool TryGetDungeonTierInfo(ActiveDungeon dungeon, out TierInfo tierInfo)
        {
            tierInfo = null;
            if (dungeon == null)
                return false;
            if (dungeon.TierConfig != null && _tierInfoMap.TryGetValue(dungeon.TierConfig, out tierInfo))
                return true;
            var configuredTier = FindTierByName(dungeon.TierName);
            return configuredTier != null && _tierInfoMap.TryGetValue(configuredTier, out tierInfo);
        }

        private void CreateDungeonMarkers(ActiveDungeon activeDungeon)
        {
            if (!TryGetDungeonTierInfo(activeDungeon, out var tierInfo))
                return;
            if (!ShouldCreateDungeonMarker(activeDungeon))
                return;
            const string vendingPrefab = "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";
            var vendMarker = CreateMapMarker<VendingMachineMapMarker>(vendingPrefab, activeDungeon.PortalPosition);
            if (vendMarker != null)
            {
                vendMarker.OwnerID = OwnerID;
                string timeText = FormatTime(activeDungeon.TierConfig.MaxDungeonLifetime);
                vendMarker.markerShopName = $"{tierInfo.MapName} - {timeText}";
                vendMarker.SendNetworkUpdate();
            }
            const string radiusPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";
            var radMarker = CreateMapMarker<MapMarkerGenericRadius>(radiusPrefab, activeDungeon.PortalPosition);
            if (radMarker != null)
            {
                radMarker.OwnerID = OwnerID;
                radMarker.alpha = 0.75f;
                radMarker.radius = _config.DungeonSpawn.MapMarkerRadius;
                radMarker.color2 = tierInfo.MarkerColor;
                radMarker.SendUpdate();
                radMarker.SendNetworkUpdate();
            }
            _activeDungeonMarkers.Add(
                new DungeonMarker
                {
                    VendingMachineMapMarker = vendMarker,
                    MapMarkerGenericRadius = radMarker,
                    Position = activeDungeon.PortalPosition,
                    Tier = tierInfo.MapName,
                }
            );
        }

        private void UpdateMarkers()
        {
            foreach (var dungeon in _activeDungeons)
            {
                if (dungeon == null || !dungeon.Spawned)
                    continue;
                if (!ShouldCreateDungeonMarker(dungeon))
                {
                    RemoveDungeonMarkers(dungeon);
                    continue;
                }
                if (!TryGetDungeonTierInfo(dungeon, out var tierInfo))
                    continue;
                if (!_activeDungeonMarkers.Any(m => m.Position == dungeon.PortalPosition))
                    CreateDungeonMarkers(dungeon);
                double timeLeft = dungeon.ActivationTime.HasValue
                    ? dungeon.TierConfig.MaxDungeonLifetime - (DateTime.UtcNow - dungeon.ActivationTime.Value).TotalSeconds
                    : dungeon.TierConfig.MaxDungeonLifetime;
                if (timeLeft < 0)
                    timeLeft = 0;
                string timeText = FormatTime(timeLeft);
                var markers = _activeDungeonMarkers.Where(m => m.Position == dungeon.PortalPosition).ToList();
                foreach (var marker in markers)
                {
                    string name = tierInfo.MapName;
                    if (!string.IsNullOrEmpty(dungeon.OwnerName))
                        name = $"{name} ({dungeon.OwnerName})";
                    if (marker.VendingMachineMapMarker != null && !marker.VendingMachineMapMarker.IsDestroyed)
                    {
                        marker.VendingMachineMapMarker.markerShopName = $"{name} - {timeText}";
                        marker.VendingMachineMapMarker.SendNetworkUpdate();
                    }
                    marker.MapMarkerGenericRadius?.SendUpdate();
                }
            }
        }

        private void RemoveDungeonMarkers(ActiveDungeon dungeon)
        {
            var markersToRemove = _activeDungeonMarkers.Where(m => m.Position == dungeon.PortalPosition).ToList();
            foreach (var marker in markersToRemove)
            {
                marker.VendingMachineMapMarker?.Kill();
                marker.MapMarkerGenericRadius?.Kill();
                _activeDungeonMarkers.Remove(marker);
            }
        }

        private DungeonTierConfig GetDifficultyWithLeastActiveDungeons()
        {
            var availableTiers = _tierInfoMap.Keys.Where(t => t.EnableAutoSpawn && IsTierUnlockedByWipeTime(t)).ToList();
            if (availableTiers.Count == 0)
                return null;
            var viable = new Dictionary<DungeonTierConfig, int>();
            foreach (var tier in availableTiers)
            {
                int activeAuto = _activeDungeons.Count(d => d.Origin == DungeonOrigin.AutoSpawn && d.TierConfig == tier);
                if (tier.MaxActiveAutoSpawn > 0 && activeAuto >= tier.MaxActiveAutoSpawn)
                    continue;
                viable[tier] = activeAuto;
            }
            if (viable.Count == 0)
                return null;
            int minCount = viable.Values.Min();
            var candidates = viable.Where(kv => kv.Value == minCount).Select(kv => kv.Key).ToList();
            return candidates.Count == 0 ? null : candidates[Random.Range(0, candidates.Count)];
        }

        private void PopulateLootContainer(ItemContainer container, List<ItemConfig> lootItems, int maxDifferentItems)
        {
            if (container == null || lootItems == null)
                return;
            var shuffled = lootItems.OrderBy(_ => Random.value).ToList();
            int differentItemsCount = 0;
            foreach (var itemConfig in shuffled)
            {
                if (differentItemsCount >= maxDifferentItems)
                    break;
                if (itemConfig == null)
                    continue;
                if (Random.value * 100f > itemConfig.InclusionChancePercentage)
                    continue;
                var def = ItemManager.FindItemDefinition(itemConfig.ShortName);
                if (def == null)
                    continue;
                int min = itemConfig.MinimumAmount;
                int max = itemConfig.MaximumAmount;
                if (min <= 0 || max <= 0)
                    continue;
                if (max < min)
                {
                    int t = min;
                    min = max;
                    max = t;
                }
                int amount = Random.Range(min, max + 1);
                if (amount <= 0)
                    continue;
                if (container.itemList.Count >= container.capacity)
                    break;
                var item = ItemManager.Create(def, amount, itemConfig.SkinID);
                if (item == null)
                    continue;
                ApplyLootItemMetadata(item, itemConfig);
                if (!item.MoveToContainer(container))
                {
                    item.Remove();
                    continue;
                }
                differentItemsCount++;
            }
        }

        private void AddLock(BaseEntity storage)
        {
            const string prefabName = "assets/prefabs/locks/keylock/lock.key.prefab";
            var keyLock = GameManager.server.CreateEntity(prefabName) as KeyLock;
            if (keyLock == null)
                return;
            keyLock.gameObject.Identity();
            keyLock.SetParent(storage, storage.GetSlotAnchorName(BaseEntity.Slot.Lock));
            keyLock.Spawn();
            storage.SetSlot(BaseEntity.Slot.Lock, keyLock);
            keyLock.keyCode = Random.Range(1000, 9999);
            keyLock.OwnerID = 0UL;
            keyLock.firstKeyCreated = true;
            using (var setFlags = keyLock.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate_Flags))
            {
                setFlags.Set(BaseEntity.Flags.Locked, true);
            }
        }

        private bool CanEnter(BasePlayer player, ActiveDungeon activeDungeon)
        {
            if (player == null || !player.userID.IsSteamId() || activeDungeon == null)
                return false;
            var tier = GetCurrentDungeonTier(activeDungeon);
            if (tier == null)
                return false;
            bool admin = IsAdmin(player) || permission.UserHasPermission(player.UserIDString, PermissionAdmin);
            if (!HasTierPermission(player, tier))
                return false;
            if (!_config.LockDungeonToPlayer)
                return true;
            if (!admin && !permission.UserHasPermission(player.UserIDString, PermissionEnter))
                return false;
            var ownPurchasedDungeon = _activeDungeons.FirstOrDefault(d => d.OwnerID == player.userID);
            if (ownPurchasedDungeon != null && ownPurchasedDungeon != activeDungeon && !admin)
                return false;
            if (activeDungeon.OwnerID == 0UL)
            {
                activeDungeon.OwnerID = player.userID;
                activeDungeon.OwnerName = player.displayName;
                return true;
            }
            if (activeDungeon.OwnerID == player.userID)
                return true;
            if (_config.AllowTeam && player.currentTeam != 0UL)
            {
                var team = RelationshipManager.ServerInstance.FindTeam(player.currentTeam);
                if (team != null && team.members.Contains(activeDungeon.OwnerID))
                    return true;
            }
            if (_config.AllowFriends && Friends != null)
            {
                var areFriends = Friends.CallHook("AreFriends", activeDungeon.OwnerID, player.userID);
                if (areFriends != null && Convert.ToBoolean(areFriends))
                    return true;
            }
            return admin;
        }
        #endregion

        #region Grid / Construction
        private sealed class RockRoom
        {
            public int X;
            public int Z;
            public int Width;
            public int Depth;
            public Vector2Int Slot;
            public readonly HashSet<Vector2Int> Cells = new HashSet<Vector2Int>();
            public Vector2Int Center => new Vector2Int(X + Width / 2, Z + Depth / 2);
        }

        private sealed class RockConnection
        {
            public int From;
            public int To;
            public int Width;
            public readonly List<Vector2Int> Path = new List<Vector2Int>();
        }

        private sealed class RockLayout
        {
            public int Seed;
            public int RoomSize;
            public int SlotSize;
            public int MainRoomCount;
            public readonly HashSet<Vector2Int> Cells = new HashSet<Vector2Int>();
            public readonly List<RockRoom> Rooms = new List<RockRoom>();
            public readonly List<RockConnection> Connections = new List<RockConnection>();
            public int CorridorCount => Connections.Count;
        }

        private struct RockAttachment
        {
            public int Parent;
            public Vector2Int Slot;
        }

        private static RockLayout CreateRockLayout(int seed, int rooms, int roomSize)
        {
            if (rooms < 1 || rooms > 120 || roomSize < 3 || roomSize > 4)
                throw new ArgumentOutOfRangeException(nameof(rooms));
            var random = new System.Random(seed);
            int branches = rooms > 2 ? random.Next(rooms / 3 + 1) : 0;
            int mainCount = rooms - branches;
            var layout = new RockLayout
            {
                Seed = seed,
                MainRoomCount = mainCount,
                RoomSize = roomSize,
                SlotSize = roomSize + 2,
            };
            var path = new List<Vector2Int> { Vector2Int.zero };
            var occupied = new HashSet<Vector2Int> { Vector2Int.zero };
            int budget = 4096;
            int turnChance = random.Next(0, 76);
            if (!TryGrowRockPath(path, occupied, mainCount, turnChance, random, ref budget))
            {
                path.Clear();
                for (int i = 0; i < mainCount; i++)
                    path.Add(new Vector2Int(i / 2, (i + 1) / 2));
            }
            int turns = random.Next(4);
            bool mirror = random.Next(2) == 0;
            occupied.Clear();
            for (int i = 0; i < path.Count; i++)
            {
                path[i] = TransformRockSlot(path[i], turns, mirror);
                occupied.Add(path[i]);
                AddRandomRockRoom(layout, path[i], random);
            }
            int narrow = rooms > 1 ? random.Next(rooms - 1) : 0;
            int wide = rooms > 2 ? (narrow + 1 + random.Next(rooms - 2)) % (rooms - 1) : narrow;
            for (int to = 1; to < mainCount; to++)
                AddRockConnection(layout, to - 1, to, ChooseRockCorridorWidth(layout.Connections.Count, narrow, wide, random), new List<Vector2Int> { path[to - 1], path[to] });
            while (layout.Rooms.Count < rooms)
            {
                var frontier = new List<RockAttachment>();
                for (int i = 0; i < layout.Rooms.Count; i++)
                    foreach (Vector2Int direction in RockDirections)
                    {
                        Vector2Int candidate = layout.Rooms[i].Slot + direction;
                        if (!occupied.Contains(candidate))
                            frontier.Add(new RockAttachment { Parent = i, Slot = candidate });
                    }
                RockAttachment attachment = frontier[random.Next(frontier.Count)];
                int to = layout.Rooms.Count;
                AddRandomRockRoom(layout, attachment.Slot, random);
                occupied.Add(attachment.Slot);
                AddRockConnection(
                    layout,
                    attachment.Parent,
                    to,
                    ChooseRockCorridorWidth(layout.Connections.Count, narrow, wide, random),
                    new List<Vector2Int> { layout.Rooms[attachment.Parent].Slot, attachment.Slot }
                );
            }
            for (int i = 0; i < layout.Rooms.Count; i++)
            {
                CreateRockRoomShape(layout, i, random);
                layout.Cells.UnionWith(layout.Rooms[i].Cells);
            }
            return layout;
        }

        private static bool TryGrowRockPath(List<Vector2Int> path, HashSet<Vector2Int> occupied, int target, int turnChance, System.Random random, ref int budget)
        {
            if (path.Count == target)
                return true;
            if (--budget < 0)
                return false;
            var directions = (Vector2Int[])RockDirections.Clone();
            for (int i = directions.Length - 1; i > 0; i--)
            {
                int other = random.Next(i + 1);
                Vector2Int swap = directions[i];
                directions[i] = directions[other];
                directions[other] = swap;
            }
            if (path.Count > 1 && random.Next(100) < turnChance)
            {
                Vector2Int previous = path[path.Count - 1] - path[path.Count - 2];
                for (int i = 0; i < directions.Length - 1; i++)
                {
                    if (directions[i] != previous)
                        continue;
                    Vector2Int swap = directions[directions.Length - 1];
                    directions[directions.Length - 1] = directions[i];
                    directions[i] = swap;
                    break;
                }
            }
            Vector2Int current = path[path.Count - 1];
            foreach (Vector2Int direction in directions)
            {
                Vector2Int candidate = current + direction;
                if (!occupied.Add(candidate))
                    continue;
                path.Add(candidate);
                if (TryGrowRockPath(path, occupied, target, turnChance, random, ref budget))
                    return true;
                path.RemoveAt(path.Count - 1);
                occupied.Remove(candidate);
                if (budget < 0)
                    break;
            }
            return false;
        }

        private static int ChooseRockCorridorWidth(int index, int narrow, int wide, System.Random random)
        {
            return index == narrow ? 1
                : index == wide ? 2
                : random.Next(1, 3);
        }

        private static Vector2Int TransformRockSlot(Vector2Int slot, int turns, bool mirror)
        {
            if (mirror)
                slot.x = -slot.x;
            for (int i = 0; i < turns; i++)
                slot = new Vector2Int(-slot.y, slot.x);
            return slot;
        }

        private static void AddRandomRockRoom(RockLayout layout, Vector2Int slot, System.Random random)
        {
            int width = random.Next(3, layout.RoomSize + 1);
            int depth = random.Next(3, layout.RoomSize + 1);
            layout.Rooms.Add(
                new RockRoom
                {
                    X = slot.x * layout.SlotSize - width / 2,
                    Z = slot.y * layout.SlotSize - depth / 2,
                    Width = width,
                    Depth = depth,
                    Slot = slot,
                }
            );
        }

        private static void CreateRockRoomShape(RockLayout layout, int index, System.Random random)
        {
            RockRoom room = layout.Rooms[index];
            StampRockRoom(room, room.Center - Vector2Int.one, 2);
            foreach (RockConnection connection in layout.Connections)
            {
                if (connection.From != index && connection.To != index)
                    continue;
                int other = connection.From == index ? connection.To : connection.From;
                Vector2Int direction = layout.Rooms[other].Slot - room.Slot;
                int width = connection.Width;
                Vector2Int from = room.Center + new Vector2Int(1 - width, 1 - width);
                Vector2Int to = from;
                if (direction.x > 0)
                    to.x = room.X + room.Width - width;
                else if (direction.x < 0)
                    to.x = room.X;
                else if (direction.y > 0)
                    to.y = room.Z + room.Depth - width;
                else
                    to.y = room.Z;
                while (true)
                {
                    StampRockRoom(room, from, width);
                    if (from == to)
                        break;
                    from += new Vector2Int(Math.Sign(to.x - from.x), Math.Sign(to.y - from.y));
                }
            }
            int minimum = Math.Max(room.Cells.Count, (room.Width * room.Depth * random.Next(60, 86) + 99) / 100);
            int density = random.Next(35, 81);
            for (int x = room.X; x < room.X + room.Width; x++)
            for (int z = room.Z; z < room.Z + room.Depth; z++)
                if (random.Next(100) < density)
                    room.Cells.Add(new Vector2Int(x, z));
            var reachable = new HashSet<Vector2Int> { room.Center };
            var pending = new List<Vector2Int> { room.Center };
            for (int i = 0; i < pending.Count; i++)
                foreach (Vector2Int direction in RockDirections)
                {
                    Vector2Int next = pending[i] + direction;
                    if (room.Cells.Contains(next) && reachable.Add(next))
                        pending.Add(next);
                }
            room.Cells.IntersectWith(reachable);
            while (room.Cells.Count < minimum)
            {
                var frontier = new List<Vector2Int>();
                for (int x = room.X; x < room.X + room.Width; x++)
                for (int z = room.Z; z < room.Z + room.Depth; z++)
                {
                    Vector2Int cell = new Vector2Int(x, z);
                    if (room.Cells.Contains(cell))
                        continue;
                    foreach (Vector2Int direction in RockDirections)
                    {
                        if (!room.Cells.Contains(cell + direction))
                            continue;
                        frontier.Add(cell);
                        break;
                    }
                }
                if (frontier.Count == 0)
                    break;
                room.Cells.Add(frontier[random.Next(frontier.Count)]);
            }
        }

        private static void StampRockRoom(RockRoom room, Vector2Int origin, int width)
        {
            for (int x = 0; x < width; x++)
            for (int z = 0; z < width; z++)
            {
                Vector2Int cell = origin + new Vector2Int(x, z);
                if (cell.x >= room.X && cell.x < room.X + room.Width && cell.y >= room.Z && cell.y < room.Z + room.Depth)
                    room.Cells.Add(cell);
            }
        }

        private static void AddRockConnection(RockLayout layout, int fromIndex, int toIndex, int width, List<Vector2Int> path)
        {
            var connection = new RockConnection
            {
                From = fromIndex,
                To = toIndex,
                Width = width,
            };
            connection.Path.AddRange(path);
            Vector2Int offset = new Vector2Int(1 - width, 1 - width);
            for (int i = 0; i < path.Count - 1; i++)
            {
                Vector2Int from = path[i] * layout.SlotSize + offset;
                Vector2Int to = path[i + 1] * layout.SlotSize + offset;
                while (true)
                {
                    for (int x = 0; x < width; x++)
                    for (int z = 0; z < width; z++)
                        layout.Cells.Add(from + new Vector2Int(x, z));
                    if (from == to)
                        break;
                    from += new Vector2Int(Math.Sign(to.x - from.x), Math.Sign(to.y - from.y));
                }
            }
            layout.Connections.Add(connection);
        }

        private static bool IsClassicDungeon(ActiveDungeon dungeon)
        {
            return dungeon.TierConfig.BuildingGradeSelection == ClassicDungeonSelection;
        }

        private IEnumerator CreateClassicDungeon(ActiveDungeon dungeon)
        {
            _gridSize = dungeon.DungeonRoomSize;
            int rooms = dungeon.DungeonRooms * Mathf.Max(1, (_gridSize * _gridSize + 2) / 5);
            var layout = CreateRockLayout(Random.Range(0, int.MaxValue), rooms, 4);
            Vector3 origin = dungeon.DungeonPosition + new Vector3(-90f, 0f, 0f);
            RockRoom first = layout.Rooms[0];
            for (int x = first.X; x < first.X + first.Width; x++)
            for (int lane = -1; lane <= 0; lane++)
                layout.Cells.Add(new Vector2Int(x, first.Center.y + lane));
            for (int z = first.Z; z < first.Z + first.Depth; z++)
            for (int lane = -1; lane <= 0; lane++)
                layout.Cells.Add(new Vector2Int(first.Center.x + lane, z));
            var entranceEdges = new Dictionary<Vector3, Vector3>
            {
                [Vector3.left] = origin + new Vector3(first.X * SubCellSpacing, 0f, (first.Center.y - 1) * SubCellSpacing),
                [Vector3.right] = origin + new Vector3((first.X + first.Width - 2) * SubCellSpacing, 0f, (first.Center.y - 1) * SubCellSpacing),
                [Vector3.back] = origin + new Vector3((first.Center.x - 1) * SubCellSpacing, 0f, first.Z * SubCellSpacing),
                [Vector3.forward] = origin + new Vector3((first.Center.x - 1) * SubCellSpacing, 0f, (first.Z + first.Depth - 2) * SubCellSpacing),
            };
            var cells = layout.Cells.OrderBy(c => c.y).ThenBy(c => c.x).ToList();
            int batch = Mathf.Clamp(Performance.BuildCellsPerBatch, 1, 100) * SubCells * SubCells;
            int processed = 0;
            foreach (Vector2Int cell in cells)
            {
                Vector3 position = origin + new Vector3(cell.x * SubCellSpacing, 0f, cell.y * SubCellSpacing);
                HandleSubCell(position, dungeon, false);
                if (++processed >= batch)
                {
                    processed = 0;
                    yield return _buildBatchDelay;
                }
            }
            CollectClassicDoorFrames(layout, origin, dungeon);
            foreach (RockRoom room in layout.Rooms)
            {
                var points = room.Cells.OrderBy(c => c.y).ThenBy(c => c.x).Select(c => origin + new Vector3(c.x * SubCellSpacing, 0f, c.y * SubCellSpacing)).ToList();
                dungeon.RockRoomSpawnPoints.Add(points);
                dungeon.TurretPositions.AddRange(points);
            }
            yield return BuildSingleRoomExtraCorridor(origin, null, null, dungeon, entranceEdges);
            dungeon.SpawnPoints = dungeon.TurretPositions;
            dungeon.DungeonBounds = CalculateRoomBounds(dungeon);
            LogDebug($"Classic dungeon: {layout.Rooms.Count} chambers, {layout.Rooms.Count - layout.MainRoomCount} branches, {dungeon.SpawnPoints.Count} room spawn points.", 1);
        }

        private void CollectClassicDoorFrames(RockLayout layout, Vector3 origin, ActiveDungeon dungeon)
        {
            dungeon.ClassicDoorFrames.Clear();
            foreach (RockConnection connection in layout.Connections)
            {
                AddClassicRoomExitFrames(layout, connection, connection.From, connection.To, origin, dungeon);
                AddClassicRoomExitFrames(layout, connection, connection.To, connection.From, origin, dungeon);
            }
        }

        private void AddClassicRoomExitFrames(RockLayout layout, RockConnection connection, int roomIndex, int otherRoomIndex, Vector3 origin, ActiveDungeon dungeon)
        {
            RockRoom room = layout.Rooms[roomIndex];
            Vector2Int slotOffset = layout.Rooms[otherRoomIndex].Slot - room.Slot;
            var direction = new Vector2Int(Math.Sign(slotOffset.x), Math.Sign(slotOffset.y));
            bool alongX = direction.x != 0;
            if (alongX == (direction.y != 0))
                return;
            int edge;
            float yaw;
            if (direction.x > 0)
            {
                edge = room.X + room.Width - 1;
                yaw = 90f + WallRotationOffset;
            }
            else if (direction.x < 0)
            {
                edge = room.X;
                yaw = 270f + WallRotationOffset;
            }
            else if (direction.y > 0)
            {
                edge = room.Z + room.Depth - 1;
                yaw = 0f + WallRotationOffset;
            }
            else
            {
                edge = room.Z;
                yaw = 180f + WallRotationOffset;
            }
            var outward = new Vector3(direction.x, 0f, direction.y);
            var frames = new List<(Vector3 Position, float Yaw)>();
            for (int lane = 1 - connection.Width; lane <= 0; lane++)
            {
                Vector2Int inside = alongX ? new Vector2Int(edge, room.Center.y + lane) : new Vector2Int(room.Center.x + lane, edge);
                if (!layout.Cells.Contains(inside) || !layout.Cells.Contains(inside + direction))
                    return;
                Vector3 position = origin + new Vector3(inside.x * SubCellSpacing, 0f, inside.y * SubCellSpacing) + outward * (SubCellSpacing * 0.5f);
                frames.Add((position, yaw));
            }
            dungeon.ClassicDoorFrames.AddRange(frames);
        }

        private IEnumerator AssignClassicDungeonEntities(ActiveDungeon dungeon, DungeonEntitySpawns spawns, HashSet<Vector3> reservedPoints, EntitySpawnLimits limits)
        {
            var groups = dungeon.RockRoomSpawnPoints.Select(points => points.Where(point => !reservedPoints.Contains(point)).ToList()).ToList();
            for (int i = groups.Count - 1; i > 0; i--)
            {
                int other = Random.Range(0, i + 1);
                var swap = groups[i];
                groups[i] = groups[other];
                groups[other] = swap;
            }
            for (int i = 0; i < groups.Count; i++)
            {
                var roomLimits = new EntitySpawnLimits
                {
                    NpcsCounts = GetRockRoomEntityCount(limits.NpcsCounts, groups.Count, i, 0),
                    BossesCounts = GetRockRoomEntityCount(limits.BossesCounts, groups.Count, i, limits.NpcsCounts),
                    AnimalsCounts = GetRockRoomEntityCount(limits.AnimalsCounts, groups.Count, i, limits.NpcsCounts + limits.BossesCounts),
                    TurretsCounts = GetRockRoomEntityCount(limits.TurretsCounts, groups.Count, i, 0),
                    LootBoxesCounts = GetRockRoomEntityCount(limits.LootBoxesCounts, groups.Count, i, 0),
                };
                var roomSpawns = new DungeonEntitySpawns();
                yield return AssignEntitiesToSpawnPoints(groups[i], dungeon.TierConfig, roomSpawns, roomLimits);
                spawns.Turrets.AddRange(roomSpawns.Turrets);
                spawns.Boxes.AddRange(roomSpawns.Boxes);
                spawns.Npcs.AddRange(roomSpawns.Npcs);
                spawns.Bosses.AddRange(roomSpawns.Bosses);
                spawns.Animals.AddRange(roomSpawns.Animals);
                if ((i + 1) % 4 == 0)
                    yield return null;
            }
            var used = new HashSet<Vector3>(spawns.Turrets);
            used.UnionWith(spawns.Boxes);
            used.UnionWith(spawns.Npcs);
            used.UnionWith(spawns.Bosses);
            used.UnionWith(spawns.Animals);
            var remaining = dungeon.SpawnPoints.Where(p => !used.Contains(p)).ToList();
            var remainingLimits = new EntitySpawnLimits
            {
                TurretsCounts = Mathf.Max(0, limits.TurretsCounts - spawns.Turrets.Count),
                LootBoxesCounts = Mathf.Max(0, limits.LootBoxesCounts - spawns.Boxes.Count),
                NpcsCounts = Mathf.Max(0, limits.NpcsCounts - spawns.Npcs.Count),
                BossesCounts = Mathf.Max(0, limits.BossesCounts - spawns.Bosses.Count),
                AnimalsCounts = Mathf.Max(0, limits.AnimalsCounts - spawns.Animals.Count),
            };
            var extra = new DungeonEntitySpawns();
            yield return AssignEntitiesToSpawnPoints(remaining, dungeon.TierConfig, extra, remainingLimits);
            spawns.Turrets.AddRange(extra.Turrets);
            spawns.Boxes.AddRange(extra.Boxes);
            spawns.Npcs.AddRange(extra.Npcs);
            spawns.Bosses.AddRange(extra.Bosses);
            spawns.Animals.AddRange(extra.Animals);
        }

        private void SelectRouteBossSpawnPoints(ActiveDungeon dungeon, int count, List<Vector3> result)
        {
            if (count <= 0 || dungeon?.SpawnPoints == null || dungeon.SpawnPoints.Count == 0)
                return;
            var candidates = new List<(Vector3 Point, int Distance)>();
            if (dungeon.Foundations.Count > 0 && dungeon.InsidePortal != null)
            {
                Vector3 reference = dungeon.Foundations[0];
                var routeDistances = GetRouteDistancesFromEntrance(dungeon, reference);
                foreach (Vector3 point in dungeon.SpawnPoints.Distinct())
                {
                    if (routeDistances.TryGetValue(GetRouteCell(point, reference), out int distance))
                        candidates.Add((point, distance));
                }
            }
            if (candidates.Count == 0)
            {
                result.AddRange(dungeon.SpawnPoints.Distinct().OrderBy(_ => Random.value).Take(count));
                return;
            }
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int other = Random.Range(0, i + 1);
                var swap = candidates[i];
                candidates[i] = candidates[other];
                candidates[other] = swap;
            }
            count = Math.Min(count, candidates.Count);
            int maxDistance = candidates.Max(candidate => candidate.Distance);
            var used = new HashSet<int>();
            for (int bossIndex = 1; bossIndex <= count; bossIndex++)
            {
                float routeFraction = BossRouteStartFraction + (1f - BossRouteStartFraction) * bossIndex / count;
                float target = maxDistance * routeFraction;
                float bestDelta = float.MaxValue;
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (!used.Contains(i))
                        bestDelta = Mathf.Min(bestDelta, Mathf.Abs(candidates[i].Distance - target));
                }
                int selected = -1;
                float bestSeparation = -1f;
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (used.Contains(i) || Mathf.Abs(candidates[i].Distance - target) > bestDelta + 1f)
                        continue;
                    float separation = float.MaxValue;
                    foreach (Vector3 boss in result)
                        separation = Mathf.Min(separation, (candidates[i].Point - boss).sqrMagnitude);
                    if (separation <= bestSeparation)
                        continue;
                    bestSeparation = separation;
                    selected = i;
                }
                if (selected < 0)
                    break;
                used.Add(selected);
                result.Add(candidates[selected].Point);
            }
        }

        private Dictionary<Vector3Int, int> GetRouteDistancesFromEntrance(ActiveDungeon dungeon, Vector3 reference)
        {
            var distances = new Dictionary<Vector3Int, int>();
            var floorCells = new HashSet<Vector3Int>();
            Vector3 entrance = dungeon.InsidePortal.transform.position;
            Vector3Int start = default;
            float closest = float.MaxValue;
            foreach (Vector3 foundation in dungeon.Foundations)
            {
                Vector3Int cell = GetRouteCell(foundation, reference);
                floorCells.Add(cell);
                float distance = new Vector2(foundation.x - entrance.x, foundation.z - entrance.z).sqrMagnitude;
                if (distance >= closest)
                    continue;
                closest = distance;
                start = cell;
            }
            var pending = new Queue<Vector3Int>();
            distances[start] = 0;
            pending.Enqueue(start);
            var directions = new[] { new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0), new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1) };
            while (pending.Count > 0)
            {
                Vector3Int current = pending.Dequeue();
                int next = distances[current] + 1;
                foreach (Vector3Int direction in directions)
                {
                    Vector3Int neighbor = current + direction;
                    if (!floorCells.Contains(neighbor) || distances.ContainsKey(neighbor))
                        continue;
                    distances[neighbor] = next;
                    pending.Enqueue(neighbor);
                }
            }
            return distances;
        }

        private static Vector3Int GetRouteCell(Vector3 position, Vector3 reference)
        {
            return new Vector3Int(
                Mathf.RoundToInt((position.x - reference.x) / SubCellSpacing),
                Mathf.RoundToInt(position.y - reference.y),
                Mathf.RoundToInt((position.z - reference.z) / SubCellSpacing)
            );
        }

        private static int GetRockRoomEntityCount(int total, int rooms, int index, int offset)
        {
            total = Mathf.Max(0, total);
            return total / rooms + ((index - offset % rooms + rooms) % rooms < total % rooms ? 1 : 0);
        }

        private bool TryGetRockFloorPoint(Vector3 position, ActiveDungeon dungeon, out Vector3 point)
        {
            point = position;
            var bucket = GetSpatialBucket(position, SubCellSpacing);
            int floorBucketY = GetSpatialBucket(dungeon.DungeonPosition, SubCellSpacing).y;
            var ray = new Ray(new Vector3(position.x, dungeon.DungeonPosition.y + 1.5f, position.z), Vector3.down);
            bool found = false;
            float distance = 4f;
            for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++)
            {
                var key = new Vector3Int(bucket.x + x, floorBucketY, bucket.z + z);
                if (!dungeon.RockFloorColliders.TryGetValue(key, out var colliders))
                    continue;
                foreach (Collider collider in colliders)
                {
                    if (collider == null || !collider.enabled || collider.isTrigger || !collider.gameObject.activeInHierarchy)
                        continue;
                    if (!collider.Raycast(ray, out RaycastHit hit, distance) || hit.distance >= distance)
                        continue;
                    point = hit.point;
                    distance = hit.distance;
                    found = true;
                }
            }
            return found;
        }

        private Vector3 GetDungeonEntitySpawnPosition(Vector3 position, ActiveDungeon dungeon)
        {
            return IsClassicDungeon(dungeon) && TryGetRockFloorPoint(position, dungeon, out Vector3 floor) ? floor + Vector3.up * 0.02f : position;
        }

        private static Bounds GetDungeonRockBounds(Quaternion rotation)
        {
            Vector3 min = DungeonRockBounds.min;
            Vector3 max = DungeonRockBounds.max;
            var bounds = new Bounds(rotation * min, Vector3.zero);
            for (int i = 0; i < 8; i++)
                bounds.Encapsulate(rotation * new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z));
            return bounds;
        }

        private BaseEntity SpawnDungeonRock(Vector3 center, Quaternion rotation, ActiveDungeon dungeon)
        {
            Bounds bounds = GetDungeonRockBounds(rotation);
            BaseEntity entity = GameManager.server.CreateEntity(DungeonRockPrefab, center - bounds.center, rotation);
            if (entity == null)
            {
                dungeon.EntranceLayoutFailed = true;
                return null;
            }
            entity.OwnerID = OwnerID;
            entity.enableSaving = false;
            dungeon.Entities.Add(entity);
            entity.Spawn();
            if (entity.IsDestroyed)
            {
                dungeon.EntranceLayoutFailed = true;
                return null;
            }
            RegisterSpawnedEntityForDungeon(entity, dungeon.UniqueId);
            return entity;
        }

        private BaseEntity CreateRockFloor(Vector3 position, ActiveDungeon dungeon)
        {
            string key = PositionKey(position, 2);
            if (dungeon.FoundationKeys.Contains(key))
                return null;
            BaseEntity floor = null;
            for (int i = 0; i < 2; i++)
            {
                Vector3 center = position + new Vector3(i == 0 ? -0.75f : 0.75f, -DungeonRockBounds.extents.y, 0f);
                BaseEntity rock = SpawnDungeonRock(center, Quaternion.identity, dungeon);
                if (floor == null)
                    floor = rock;
                if (rock != null)
                {
                    var bucket = GetSpatialBucket(position, SubCellSpacing);
                    if (!dungeon.RockFloorColliders.TryGetValue(bucket, out var colliders))
                        dungeon.RockFloorColliders[bucket] = colliders = new List<Collider>();
                    colliders.AddRange(rock.GetComponentsInChildren<Collider>());
                }
                if (ClassicDungeonRoofForTesting)
                {
                    Quaternion rotation = Quaternion.AngleAxis(180f, Vector3.right);
                    center.y = position.y + CeilingHeight + DungeonRockBounds.extents.y;
                    SpawnDungeonRock(center, rotation, dungeon);
                }
            }
            return floor;
        }

        private bool CreateRockWall(Vector3 foundation, Vector3 offset, ActiveDungeon dungeon)
        {
            string key = "ROCKWALL:" + PositionKey(foundation + offset * 0.5f, 2);
            if (dungeon.OccupiedPositions.Contains(key))
                return true;
            Vector3 outward = offset.normalized;
            bool alongX = Mathf.Abs(outward.x) > 0.5f;
            Quaternion rotation = alongX ? Quaternion.AngleAxis(outward.x * 90f, Vector3.forward) : Quaternion.AngleAxis(-outward.z * 90f, Vector3.right);
            Bounds bounds = GetDungeonRockBounds(rotation);
            int columns = alongX ? 1 : 2;
            float thickness = alongX ? bounds.extents.x : bounds.extents.z;
            Vector3 wallCenter = foundation + outward * (SubCellSpacing * 0.5f + thickness - DungeonRockOverlap);
            int layers = Mathf.CeilToInt((CeilingHeight + DungeonRockOverlap) / (bounds.size.y - DungeonRockOverlap));
            BaseEntity torchWall = null;
            for (int column = 0; column < columns; column++)
            for (int layer = 0; layer < layers; layer++)
            {
                Vector3 center = wallCenter;
                if (!alongX)
                    center.x += column == 0 ? -0.75f : 0.75f;
                center.y += -DungeonRockOverlap + bounds.extents.y + layer * (bounds.size.y - DungeonRockOverlap);
                BaseEntity rock = SpawnDungeonRock(center, rotation, dungeon);
                if (rock == null)
                    return false;
                if (torchWall == null)
                    torchWall = rock;
            }
            dungeon.OccupiedPositions.Add(key);
            CreateRockTorch(torchWall, foundation + outward * SubCellSpacing * 0.5f + Vector3.up * 1.5f, -outward, thickness * 2f + 2f, dungeon);
            return true;
        }

        private void CreateRockTorch(BaseEntity wall, Vector3 position, Vector3 inward, float distance, ActiveDungeon dungeon)
        {
            if (!_config.EnableDungeonLights || wall == null)
                return;
            float spacing = Mathf.Clamp(_config.DungeonLightSpacing, SubCellSpacing * 1.5f, 10.5f);
            int maxTorches = Mathf.Max(100, dungeon.RockRoomSpawnPoints.Count * 8 + 16);
            if (dungeon.RockTorchPositions.Count >= maxTorches || dungeon.RockTorchPositions.Any(p => (p - position).sqrMagnitude < spacing * spacing))
                return;
            Collider[] colliders = wall.GetComponentsInChildren<Collider>();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Vector3 tangent = Vector3.Cross(Vector3.up, inward);
                float side =
                    attempt == 0 ? 0f
                    : attempt == 1 ? -0.35f
                    : 0.35f;
                var ray = new Ray(position + tangent * side + inward * 0.75f, -inward);
                RaycastHit nearest = default(RaycastHit);
                bool found = false;
                float nearestDistance = distance;
                foreach (Collider collider in colliders)
                {
                    if (!collider.enabled || collider.isTrigger || !collider.gameObject.activeInHierarchy)
                        continue;
                    if (!collider.Raycast(ray, out RaycastHit hit, distance) || Vector3.Dot(hit.normal, inward) < 0.25f || hit.distance >= nearestDistance)
                        continue;
                    nearest = hit;
                    nearestDistance = hit.distance;
                    found = true;
                }
                if (!found)
                    continue;
                Vector3 facing = new Vector3(nearest.normal.x, 0f, nearest.normal.z).normalized;
                BaseEntity torch = GameManager.server.CreateEntity(DungeonTorchPrefab, nearest.point + facing * 0.03f, Quaternion.LookRotation(facing, Vector3.up));
                if (torch == null)
                    return;
                torch.OwnerID = OwnerID;
                torch.enableSaving = false;
                dungeon.Entities.Add(torch);
                torch.SetParent(wall, worldPositionStays: true);
                torch.Spawn();
                if (!torch.IsDestroyed)
                {
                    dungeon.RockTorchPositions.Add(torch.transform.position);
                    RegisterSpawnedEntityForDungeon(torch, dungeon.UniqueId);
                }
                return;
            }
        }

        private IEnumerator CreateCustomProceduralDynamicDungeon(ActiveDungeon activeDungeon)
        {
            yield return null;
            _gridSize = activeDungeon.DungeonRoomSize;
            var basePosition = activeDungeon.DungeonPosition + new Vector3(-90f, 0, 0);
            var corridorRow = _gridSize / 2;
            _randomSeed = (uint)Random.Range(0, int.MaxValue);
            var origins = GenerateRoomOrigins(activeDungeon.DungeonRooms, basePosition);
            var grids = GenerateRoomGrids(origins.Count, corridorRow, activeDungeon);
            yield return BuildRoomCells(origins, grids, corridorRow, activeDungeon);
            yield return BuildRoomCorridors(origins, grids, activeDungeon);
            yield return ConnectAdjacentRooms(origins, grids, activeDungeon);
            yield return BuildSingleRoomExtraCorridor(origins[0], grids[0], origins.Count > 1 ? origins[1] : (Vector3?)null, activeDungeon);
            if (activeDungeon.EntranceLayoutFailed || activeDungeon.InsidePortal == null)
                yield break;
            bool lastHorizontal = origins.Count <= 1 || Mathf.Abs(origins[^1].x - origins[^2].x) >= Mathf.Abs(origins[^1].z - origins[^2].z);
            yield return BuildExitCorridor(origins[^1], grids[^1], lastHorizontal, activeDungeon);
            yield return _buildBatchDelay;
            activeDungeon.SpawnPoints = activeDungeon.TurretPositions;
            activeDungeon.GridOrigins = origins;
            activeDungeon.CellGrids = grids;
            activeDungeon.DungeonBounds = CalculateRoomBounds(activeDungeon);
        }

        private IEnumerator BuildSingleRoomExtraCorridor(Vector3 origin, bool[] grid, Vector3? nextOrigin, ActiveDungeon activeDungeon, IReadOnlyDictionary<Vector3, Vector3> entranceEdges = null)
        {
            var row = _gridSize / 2;
            var col = _gridSize / 2;
            Vector3 preferredDirection = Vector3.left;
            if (nextOrigin.HasValue)
            {
                Vector3 diff = nextOrigin.Value - origin;
                preferredDirection = Mathf.Abs(diff.x) >= Mathf.Abs(diff.z) ? (diff.x >= 0f ? Vector3.left : Vector3.right) : (diff.z >= 0f ? Vector3.back : Vector3.forward);
            }
            Vector3[] allDirections = { Vector3.left, Vector3.right, Vector3.back, Vector3.forward };
            var directions = new List<Vector3> { preferredDirection };
            foreach (var direction in allDirections)
                if (direction != preferredDirection)
                    directions.Add(direction);
            Vector3 chosenStart = Vector3.zero;
            Vector3 chosenStraightEnd = Vector3.zero;
            Vector3 chosenTurnStart = Vector3.zero;
            Vector3 chosenOuterEnd = Vector3.zero;
            Vector3 chosenTurn = Vector3.zero;
            int lowestCollisionCount = int.MaxValue;
            foreach (Vector3 direction in directions)
            {
                Vector3 roomEdge = entranceEdges != null ? entranceEdges[direction] : GetEntranceRoomEdge(origin, grid, row, col, direction);
                Vector3 straightEnd = roomEdge + direction * EntranceInnerLegLength;
                Vector3 side = new Vector3(-direction.z, 0f, direction.x);
                foreach (Vector3 turn in new[] { side, -side })
                {
                    Vector3 turnStart = GetEntranceTurnStart(roomEdge, straightEnd, direction, turn);
                    Vector3 outerEnd = turnStart + turn * EntranceOuterLegLength;
                    int collisions = CountEntranceExistingFoundationConflicts(roomEdge, straightEnd, turnStart, outerEnd, activeDungeon);
                    if (collisions >= lowestCollisionCount)
                        continue;
                    chosenStart = roomEdge;
                    chosenStraightEnd = straightEnd;
                    chosenTurnStart = turnStart;
                    chosenOuterEnd = outerEnd;
                    chosenTurn = turn;
                    lowestCollisionCount = collisions;
                }
                if (lowestCollisionCount == 0)
                    break;
            }
            if (lowestCollisionCount != 0)
            {
                activeDungeon.EntranceLayoutFailed = true;
                yield break;
            }
            yield return BuildCorridor(chosenStart, chosenStraightEnd, activeDungeon, false, false, true);
            yield return BuildCorridor(chosenTurnStart, chosenOuterEnd, activeDungeon, false, true, true);
            ConfigureEntranceSafeZone(activeDungeon, chosenTurnStart, chosenTurn);
            if (_config?.PlaceEntranceDoors != true)
                yield break;
            if (!HasEntranceDoorFloorSupport(chosenTurnStart, chosenTurn, activeDungeon))
            {
                activeDungeon.EntranceLayoutFailed = true;
                LogDebug("L-shaped entrance door floor support is missing; cancelling this layout.", 2);
                yield break;
            }
            Vector3 doorPlane = chosenTurnStart + chosenTurn * EntranceDoorDistanceFromTurn;
            if (activeDungeon.InsidePortal == null || Vector3.Dot(activeDungeon.InsidePortal.transform.position - doorPlane, chosenTurn) < 3f)
            {
                activeDungeon.EntranceLayoutFailed = true;
                LogDebug("L-shaped entrance portal is missing or too close to the wrong side of the door; cancelling this layout.", 2);
                yield break;
            }
            var doorCell = chosenTurnStart + chosenTurn * (chosenTurn.x > 0f || chosenTurn.z > 0f ? EntranceDoorDistanceFromTurn - CellSpacing : EntranceDoorDistanceFromTurn);
            var doorSide =
                chosenTurn.x > 0f ? "east"
                : chosenTurn.x < 0f ? "west"
                : chosenTurn.z > 0f ? "north"
                : "south";
            PlaceCorridorFrame(doorSide, doorCell, SubCells * SubCellSpacing, activeDungeon, true);
            if (activeDungeon.EntranceDoors.Count < SubCells)
            {
                activeDungeon.EntranceLayoutFailed = true;
                LogDebug("L-shaped entrance door frame is incomplete; cancelling this layout.", 2);
            }
        }

        private void ConfigureEntranceSafeZone(ActiveDungeon dungeon, Vector3 turnStart, Vector3 outerDirection)
        {
            if (dungeon == null)
                return;
            dungeon.HasEntranceSafeZone = false;
            dungeon.EntranceSafeZoneBounds = default(Bounds);
            if (dungeon.EntranceCorridorSegments.Count < 2)
                return;
            Bounds outerLeg = dungeon.EntranceCorridorSegments[dungeon.EntranceCorridorSegments.Count - 1];
            Vector3 minimum = outerLeg.min;
            Vector3 maximum = outerLeg.max;
            Vector3 safeEnd = turnStart + outerDirection * (SubCells * SubCellSpacing);
            if (outerDirection.x > 0.5f)
                minimum.x = Mathf.Max(minimum.x, safeEnd.x);
            else if (outerDirection.x < -0.5f)
                maximum.x = Mathf.Min(maximum.x, safeEnd.x);
            else if (outerDirection.z > 0.5f)
                minimum.z = Mathf.Max(minimum.z, safeEnd.z);
            else if (outerDirection.z < -0.5f)
                maximum.z = Mathf.Min(maximum.z, safeEnd.z);
            else
                return;
            if (minimum.x >= maximum.x || minimum.z >= maximum.z)
                return;
            dungeon.EntranceSafeZoneBounds = new Bounds((minimum + maximum) * 0.5f, maximum - minimum);
            dungeon.HasEntranceSafeZone = true;
        }

        private bool HasEntranceDoorFloorSupport(Vector3 turnStart, Vector3 turn, ActiveDungeon dungeon)
        {
            if (dungeon?.FoundationKeys == null)
                return false;
            Vector3 boundary = turnStart + turn * EntranceDoorDistanceFromTurn;
            for (int lane = 0; lane < SubCells; lane++)
            {
                Vector3 inside;
                Vector3 outside;
                if (Mathf.Abs(turn.x) > 0.5f)
                {
                    float z = boundary.z + lane * SubCellSpacing;
                    inside = new Vector3(boundary.x - (turn.x > 0f ? SubCellSpacing : 0f), boundary.y, z);
                    outside = new Vector3(boundary.x - (turn.x < 0f ? SubCellSpacing : 0f), boundary.y, z);
                }
                else
                {
                    float x = boundary.x + lane * SubCellSpacing;
                    inside = new Vector3(x, boundary.y, boundary.z - (turn.z > 0f ? SubCellSpacing : 0f));
                    outside = new Vector3(x, boundary.y, boundary.z - (turn.z < 0f ? SubCellSpacing : 0f));
                }
                if (!dungeon.FoundationKeys.Contains(PositionKey(inside, 2)) || !dungeon.FoundationKeys.Contains(PositionKey(outside, 2)))
                    return false;
            }
            return true;
        }

        private Vector3 GetEntranceRoomEdge(Vector3 origin, bool[] grid, int row, int col, Vector3 direction)
        {
            if (direction == Vector3.left)
                return GetCellPosition(origin, FindActiveCellHorizontalLeft(grid, row), row);
            if (direction == Vector3.right)
                return GetCellPosition(origin, FindActiveCellHorizontalRight(grid, row), row);
            if (direction == Vector3.forward)
                return GetCellPosition(origin, col, FindActiveCellVerticalTop(grid, col));
            return GetCellPosition(origin, col, FindActiveCellVerticalBottom(grid, col));
        }

        private Vector3 GetEntranceTurnStart(Vector3 roomEdge, Vector3 straightEnd, Vector3 direction, Vector3 turn)
        {
            Vector3 corner = straightEnd;
            if (direction.x > 0f)
                corner.x -= SubCellSpacing;
            if (direction.z > 0f)
                corner.z -= SubCellSpacing;
            if (turn.x > 0f)
                corner.x = roomEdge.x + SubCellSpacing;
            if (turn.x < 0f)
                corner.x = roomEdge.x;
            if (turn.z > 0f)
                corner.z = roomEdge.z + SubCellSpacing;
            if (turn.z < 0f)
                corner.z = roomEdge.z;
            return corner;
        }

        private int CountEntranceExistingFoundationConflicts(Vector3 start, Vector3 end, Vector3 corner, Vector3 outerEnd, ActiveDungeon room)
        {
            var candidate = new HashSet<string>();
            AddEntranceCandidateFloorKeys(start, end, candidate);
            AddEntranceCandidateFloorKeys(corner, outerEnd, candidate);
            var allowed = new HashSet<string>();
            for (int j = 0; j < SubCells; j++)
            {
                if (Mathf.Abs(end.x - start.x) >= Mathf.Abs(end.z - start.z))
                {
                    allowed.Add(PositionKey(start + new Vector3(0f, 0f, j * SubCellSpacing), 2));
                    if (end.x > start.x)
                        allowed.Add(PositionKey(start + new Vector3(SubCellSpacing, 0f, j * SubCellSpacing), 2));
                }
                else
                {
                    allowed.Add(PositionKey(start + new Vector3(j * SubCellSpacing, 0f, 0f), 2));
                    if (end.z > start.z)
                        allowed.Add(PositionKey(start + new Vector3(j * SubCellSpacing, 0f, SubCellSpacing), 2));
                }
            }
            int conflicts = 0;
            foreach (var key in candidate)
                if (room.FoundationKeys.Contains(key) && !allowed.Contains(key))
                    conflicts++;
            return conflicts;
        }

        private void AddEntranceCandidateFloorKeys(Vector3 start, Vector3 end, HashSet<string> keys)
        {
            Vector3 diff = end - start;
            bool alongX = Mathf.Abs(diff.x) >= Mathf.Abs(diff.z);
            int steps = Mathf.CeilToInt((alongX ? Mathf.Abs(diff.x) : Mathf.Abs(diff.z)) / SubCellSpacing);
            float dx = alongX ? Mathf.Sign(diff.x) : 0f;
            float dz = alongX ? 0f : Mathf.Sign(diff.z);
            for (int i = 0; i <= steps; i++)
            for (int j = 0; j < SubCells; j++)
            {
                Vector3 pos = alongX
                    ? new Vector3(start.x + dx * i * SubCellSpacing, start.y, start.z + j * SubCellSpacing)
                    : new Vector3(start.x + j * SubCellSpacing, start.y, start.z + dz * i * SubCellSpacing);
                keys.Add(PositionKey(pos, 2));
            }
        }

        private IEnumerator ConnectAdjacentRooms(IReadOnlyList<Vector3> origins, IReadOnlyList<bool[]> grids, ActiveDungeon activeDungeon)
        {
            var step = _gridSize * CellSpacing + CorridorLength;
            var corridorRow = _gridSize / 2;
            var corridorCol = _gridSize / 2;
            var size = SubCells * SubCellSpacing;
            for (var i = 0; i < origins.Count - 1; i++)
            {
                for (var j = i + 1; j < origins.Count; j++)
                {
                    var aOrigin = origins[i];
                    var bOrigin = origins[j];
                    var sameX = Mathf.Abs(aOrigin.x - bOrigin.x) < 0.1f;
                    var sameZ = Mathf.Abs(aOrigin.z - bOrigin.z) < 0.1f;
                    if (sameX && Mathf.Abs(Mathf.Abs(aOrigin.z - bOrigin.z) - step) < 0.1f)
                    {
                        var aGrid = grids[i];
                        var bGrid = grids[j];
                        var verticalDir = aOrigin.z < bOrigin.z;
                        var startY = verticalDir ? FindActiveCellVerticalTop(aGrid, corridorCol) : FindActiveCellVerticalBottom(aGrid, corridorCol);
                        var endY = verticalDir ? FindActiveCellVerticalBottom(bGrid, corridorCol) : FindActiveCellVerticalTop(bGrid, corridorCol);
                        var start = GetCellPosition(aOrigin, corridorCol, startY);
                        var end = GetCellPosition(bOrigin, corridorCol, endY);
                        yield return BuildCorridor(start, end, activeDungeon, true, false);
                        PlaceCorridorFrame(verticalDir ? "north" : "south", start, size, activeDungeon);
                        PlaceCorridorFrame(verticalDir ? "south" : "north", end, size, activeDungeon);
                    }
                    else if (sameZ && Mathf.Abs(Mathf.Abs(aOrigin.x - bOrigin.x) - step) < 0.1f)
                    {
                        var aGrid = grids[i];
                        var bGrid = grids[j];
                        var horizontalDir = aOrigin.x < bOrigin.x;
                        var startX = horizontalDir ? FindActiveCellHorizontalRight(aGrid, corridorRow) : FindActiveCellHorizontalLeft(aGrid, corridorRow);
                        var endX = horizontalDir ? FindActiveCellHorizontalLeft(bGrid, corridorRow) : FindActiveCellHorizontalRight(bGrid, corridorRow);
                        var start = GetCellPosition(aOrigin, startX, corridorRow);
                        var end = GetCellPosition(bOrigin, endX, corridorRow);
                        yield return BuildCorridor(start, end, activeDungeon, true, false);
                        PlaceCorridorFrame(horizontalDir ? "east" : "west", start, size, activeDungeon);
                        PlaceCorridorFrame(horizontalDir ? "west" : "east", end, size, activeDungeon);
                    }
                }
            }
        }

        private Bounds CalculateRoomBounds(ActiveDungeon activeDungeon)
        {
            if (activeDungeon.Foundations == null || activeDungeon.Foundations.Count == 0)
                return new Bounds(activeDungeon.DungeonPosition, Vector3.zero);
            var min = activeDungeon.Foundations[0];
            var max = activeDungeon.Foundations[0];
            foreach (var p in activeDungeon.Foundations)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            var center = (min + max) * 0.5f;
            var size = max - min + new Vector3(CellSpacing, 30f, CellSpacing);
            if (size.y < 30f)
                size.y = 30f;
            return new Bounds(center, size);
        }

        private List<Vector3> GenerateRoomOrigins(int roomsToBuild, Vector3 basePosition)
        {
            var origins = new List<Vector3> { basePosition };
            if (roomsToBuild <= 1)
                return origins;
            var step = _gridSize * CellSpacing + CorridorLength;
            var directions = new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
            const int attemptsPerRoom = 20;
            Vector3 prevDir = Vector3.zero;
            for (var i = 1; i < roomsToBuild; i++)
            {
                var placed = false;
                for (var a = 0; a < attemptsPerRoom && !placed; a++)
                {
                    var dir = directions[Random.Range(0, directions.Length)];
                    if (prevDir != Vector3.zero && Vector3.Dot(dir, prevDir) > 0.9f)
                        continue;
                    var next = origins[^1] + dir * step;
                    if (origins.Any(o => Vector3.Distance(o, next) < 0.1f))
                        continue;
                    origins.Add(next);
                    prevDir = dir;
                    placed = true;
                }
                if (!placed)
                {
                    var fallbackDir = Mathf.Abs(prevDir.x) > 0 ? Vector3.forward : Vector3.right;
                    origins.Add(origins[^1] + fallbackDir * step);
                    prevDir = fallbackDir;
                }
            }
            return origins;
        }

        private List<bool[]> GenerateRoomGrids(int roomsToBuild, int corridorRow, ActiveDungeon activeDungeon)
        {
            var layouts = new List<bool[]>();
            var corridorCol = _gridSize / 2;
            for (var i = 0; i < roomsToBuild; i++)
            {
                var forcedCells = new List<Vector2Int>
                {
                    new Vector2Int(_gridSize - 1, corridorRow),
                    new Vector2Int(0, corridorRow),
                    new Vector2Int(corridorCol, _gridSize - 1),
                    new Vector2Int(corridorCol, 0),
                };
                GenerateGrid(forcedCells, corridorRow, activeDungeon);
                if (activeDungeon.LastGeneratedGrid == null)
                    throw new Exception($"Failed to generate grid for activeDungeon {i}");
                layouts.Add(activeDungeon.LastGeneratedGrid);
            }
            return layouts;
        }

        private IEnumerator BuildRoomCells(IReadOnlyList<Vector3> origins, IReadOnlyList<bool[]> grids, int corridorRow, ActiveDungeon activeDungeon)
        {
            for (var i = 0; i < origins.Count; i++)
            {
                var westForced = i == 0 ? (Vector2Int?)null : new Vector2Int(0, corridorRow);
                var eastForced = new Vector2Int(_gridSize - 1, corridorRow);
                yield return BuildRoomGrid(grids[i], origins[i], westForced, eastForced, activeDungeon);
            }
        }

        private IEnumerator BuildRoomCorridors(IReadOnlyList<Vector3> origins, IReadOnlyList<bool[]> grids, ActiveDungeon activeDungeon)
        {
            var corridorRow = _gridSize / 2;
            var corridorCol = _gridSize / 2;
            for (var i = 0; i < origins.Count - 1; i++)
            {
                var aGrid = grids[i];
                var bGrid = grids[i + 1];
                var aOrigin = origins[i];
                var bOrigin = origins[i + 1];
                var horizontal = Mathf.Abs(bOrigin.x - aOrigin.x) >= Mathf.Abs(bOrigin.z - aOrigin.z);
                if (horizontal)
                {
                    var startX = FindActiveCellHorizontalRight(aGrid, corridorRow);
                    var endX = FindActiveCellHorizontalLeft(bGrid, corridorRow);
                    var start = GetCellPosition(aOrigin, startX, corridorRow);
                    var end = GetCellPosition(bOrigin, endX, corridorRow);
                    yield return BuildCorridor(start, end, activeDungeon, true, false);
                }
                else
                {
                    var startY = FindActiveCellVerticalTop(aGrid, corridorCol);
                    var endY = FindActiveCellVerticalBottom(bGrid, corridorCol);
                    var start = GetCellPosition(aOrigin, corridorCol, startY);
                    var end = GetCellPosition(bOrigin, corridorCol, endY);
                    yield return BuildCorridor(start, end, activeDungeon, true, false);
                }
            }
        }

        private IEnumerator BuildExitCorridor(Vector3 lastOrigin, bool[] lastGrid, bool horizontal, ActiveDungeon activeDungeon)
        {
            var corridorRow = _gridSize / 2;
            var corridorCol = _gridSize / 2;
            if (horizontal)
            {
                var startX = FindActiveCellHorizontalRight(lastGrid, corridorRow);
                var start = GetCellPosition(lastOrigin, startX, corridorRow);
                var end = start + new Vector3(9.01f, 0, 0);
                yield return BuildCorridor(start, end, activeDungeon, false, false);
            }
            else
            {
                var startY = FindActiveCellVerticalTop(lastGrid, corridorCol);
                var start = GetCellPosition(lastOrigin, corridorCol, startY);
                var end = start + new Vector3(0, 0, 9.01f);
                yield return BuildCorridor(start, end, activeDungeon, false, false);
            }
        }

        private int FindActiveCellHorizontalRight(bool[] grid, int row)
        {
            for (var x = _gridSize - 1; x >= 0; x--)
                if (GetGridValue(grid, x, row))
                    return x;
            return _gridSize - 1;
        }

        private int FindActiveCellHorizontalLeft(bool[] grid, int row)
        {
            for (var x = 0; x < _gridSize; x++)
                if (GetGridValue(grid, x, row))
                    return x;
            return 0;
        }

        private int FindActiveCellVerticalTop(bool[] grid, int col)
        {
            for (var y = _gridSize - 1; y >= 0; y--)
                if (GetGridValue(grid, col, y))
                    return y;
            return _gridSize - 1;
        }

        private int FindActiveCellVerticalBottom(bool[] grid, int col)
        {
            for (var y = 0; y < _gridSize; y++)
                if (GetGridValue(grid, col, y))
                    return y;
            return 0;
        }

        private Vector3 GetCellPosition(Vector3 origin, int x, int y)
        {
            var start = origin - new Vector3(_gridSize * CellSpacing * 0.5f, 0, _gridSize * CellSpacing * 0.5f);
            return start + new Vector3(x * CellSpacing, 0, y * CellSpacing);
        }

        private IEnumerator BuildFrames(ActiveDungeon activeDungeon)
        {
            if (IsClassicDungeon(activeDungeon))
            {
                yield return BuildClassicDoorFrames(activeDungeon);
                yield break;
            }
            yield return null;
            var corridorRow = _gridSize / 2;
            for (var i = 0; i < activeDungeon.CellGrids.Count; i++)
            {
                var westForced = i == 0 ? (Vector2Int?)null : new Vector2Int(0, corridorRow);
                var eastForced = new Vector2Int(_gridSize - 1, corridorRow);
                yield return BuildFrameGrid(activeDungeon.CellGrids[i], activeDungeon.GridOrigins[i], westForced, eastForced, activeDungeon);
            }
            for (var i = 0; i < activeDungeon.GridOrigins.Count - 1; i++)
            {
                CreateCorridorFrames(activeDungeon.GridOrigins[i], activeDungeon.GridOrigins[i + 1], corridorRow, activeDungeon);
                yield return _buildBatchDelay;
            }
            if (activeDungeon.GridOrigins.Count > 1)
                PlaceFinalExitFrames(activeDungeon.GridOrigins[^1], activeDungeon.GridOrigins[^2], corridorRow, activeDungeon);
            else
                PlaceFinalExitFrames(activeDungeon.GridOrigins[^1], activeDungeon.GridOrigins[^1] + Vector3.right, corridorRow, activeDungeon);
            yield return _buildBatchDelay;
        }

        private IEnumerator BuildClassicDoorFrames(ActiveDungeon activeDungeon)
        {
            if (_config?.PlaceDungeonDoors != true)
                yield break;
            int processed = 0;
            int batch = Mathf.Clamp(Performance.BuildFrameCellsPerBatch, 1, 100);
            foreach (var frame in activeDungeon.ClassicDoorFrames)
            {
                PlaceDoorFrame(frame.Position, frame.Yaw, activeDungeon);
                if (++processed >= batch)
                {
                    processed = 0;
                    yield return _buildBatchDelay;
                }
            }
        }

        private Door PlaceDoorFrame(Vector3 position, float yaw, ActiveDungeon room)
        {
            var rotation = Quaternion.Euler(0, yaw, 0);
            var frame = CreateBlock(WallFramePrefab, position, rotation, room);
            if (frame == null)
                return null;
            room.Entities.Add(frame);
            return CreateDoor(position, rotation, room) as Door;
        }

        private void CreateCorridorFrames(Vector3 originA, Vector3 originB, int corridorRow, ActiveDungeon activeDungeon)
        {
            var size = SubCells * SubCellSpacing;
            var corridorCol = _gridSize / 2;
            if (Mathf.Abs(originB.x - originA.x) > Mathf.Abs(originB.z - originA.z))
            {
                var leftCell = GetCellPosition(originA, _gridSize - 1, corridorRow);
                var rightCell = GetCellPosition(originB, 0, corridorRow);
                PlaceCorridorFrame("east", leftCell, size, activeDungeon);
                PlaceCorridorFrame("west", rightCell, size, activeDungeon);
            }
            else
            {
                var bottomCell = GetCellPosition(originA, corridorCol, _gridSize - 1);
                var topCell = GetCellPosition(originB, corridorCol, 0);
                PlaceCorridorFrame("north", bottomCell, size, activeDungeon);
                PlaceCorridorFrame("south", topCell, size, activeDungeon);
            }
        }

        private void PlaceFinalExitFrames(Vector3 lastOrigin, Vector3 prevOrigin, int corridorRow, ActiveDungeon activeDungeon)
        {
            var size = SubCells * SubCellSpacing;
            var corridorCol = _gridSize / 2;
            var horizontal = Mathf.Abs(lastOrigin.x - prevOrigin.x) > Mathf.Abs(lastOrigin.z - prevOrigin.z);
            if (horizontal)
            {
                var lastCell = GetCellPosition(lastOrigin, _gridSize - 1, corridorRow);
                PlaceCorridorFrame("east", lastCell, size, activeDungeon);
            }
            else
            {
                var lastCell = GetCellPosition(lastOrigin, corridorCol, _gridSize - 1);
                PlaceCorridorFrame("north", lastCell, size, activeDungeon);
            }
        }

        private IEnumerator BuildWalls(ActiveDungeon activeDungeon)
        {
            yield return null;
            int batch = Mathf.Clamp(Performance.BuildWallFoundationsPerBatch, 1, 100);
            var loop = 0;
            var retries = new List<(Vector3 Foundation, Vector3 Offset, float Yaw)>();
            foreach (var foundation in activeDungeon.Foundations)
            {
                if (!TryPlaceWall(foundation, Vector3.forward * SubCellSpacing, WallRotationOffset, activeDungeon))
                    retries.Add((foundation, Vector3.forward * SubCellSpacing, WallRotationOffset));
                if (!TryPlaceWall(foundation, Vector3.back * SubCellSpacing, 180f + WallRotationOffset, activeDungeon))
                    retries.Add((foundation, Vector3.back * SubCellSpacing, 180f + WallRotationOffset));
                if (!TryPlaceWall(foundation, Vector3.right * SubCellSpacing, 90f + WallRotationOffset, activeDungeon))
                    retries.Add((foundation, Vector3.right * SubCellSpacing, 90f + WallRotationOffset));
                if (!TryPlaceWall(foundation, Vector3.left * SubCellSpacing, 270f + WallRotationOffset, activeDungeon))
                    retries.Add((foundation, Vector3.left * SubCellSpacing, 270f + WallRotationOffset));
                loop++;
                if (loop % batch == 0)
                    yield return _buildBatchDelay;
            }
            yield return _buildBatchDelay;
            loop = 0;
            foreach (var retry in retries)
            {
                TryPlaceWall(retry.Foundation, retry.Offset, retry.Yaw, activeDungeon);
                loop++;
                if (loop % (batch * 4) == 0)
                    yield return _buildBatchDelay;
            }
        }

        private void GenerateGrid(IEnumerable<Vector2Int> forcedCells, int corridorRow, ActiveDungeon activeDungeon)
        {
            var grid = new bool[_gridSize * _gridSize];
            var success = false;
            for (var attempt = 0; attempt < MaxGenerationAttempts; attempt++)
            {
                RandomiseGrid(grid);
                ForceCorridorRow(grid, corridorRow);
                ApplyForcedCells(grid, forcedCells);
                var allForced = CollectForcedCells(grid, forcedCells);
                var root = allForced[0];
                if (!IsGridConnected(grid, allForced, root))
                {
                    ResetRandomSeed();
                    continue;
                }
                PruneIsolatedCells(grid, root.x, root.y);
                if (CountActiveCells(grid) >= MinimumActiveCells)
                {
                    success = true;
                    break;
                }
                ResetRandomSeed();
            }
            activeDungeon.LastGeneratedGrid = success ? grid : null;
        }

        private void RandomiseGrid(bool[] grid)
        {
            for (var i = 0; i < grid.Length; i++)
                grid[i] = SeedRandom.Range(ref _randomSeed, 0, 2) == 0;
        }

        private void ForceCorridorRow(bool[] grid, int corridorRow)
        {
            for (var x = 0; x < _gridSize; x++)
                grid[corridorRow * _gridSize + x] = true;
        }

        private void ApplyForcedCells(bool[] grid, IEnumerable<Vector2Int> forcedCells)
        {
            foreach (var cell in forcedCells)
            {
                SetGridValue(grid, cell.x, cell.y, true);
                if (cell.x > 0)
                    SetGridValue(grid, cell.x - 1, cell.y, true);
                if (cell.x < _gridSize - 1)
                    SetGridValue(grid, cell.x + 1, cell.y, true);
            }
        }

        private List<Vector2Int> CollectForcedCells(bool[] grid, IEnumerable<Vector2Int> forcedCells)
        {
            var allForced = new List<Vector2Int>(forcedCells);
            for (var x = 0; x < _gridSize; x++)
            for (var y = 0; y < _gridSize; y++)
                if (GetGridValue(grid, x, y))
                {
                    var cell = new Vector2Int(x, y);
                    if (!allForced.Contains(cell))
                        allForced.Add(cell);
                }
            return allForced;
        }

        private bool IsGridConnected(bool[] grid, IEnumerable<Vector2Int> requiredCells, Vector2Int root)
        {
            var reachable = FindConnectedCells(grid, root.x, root.y);
            foreach (var cell in requiredCells)
            {
                int index = GridIndex(cell.x, cell.y);
                if (index == -1 || !reachable[index])
                    return false;
            }
            return true;
        }

        private void ResetRandomSeed()
        {
            _randomSeed = (uint)Random.Range(0, int.MaxValue);
        }

        private BaseEntity CreateBlock(string prefab, Vector3 position, Quaternion rotation, ActiveDungeon room)
        {
            var key = PositionKey(position, 2);
            if (room.OccupiedPositions.Contains(key))
                return null;
            var entity = GameManager.server.CreateEntity(prefab, position, rotation);
            if (entity == null)
                return null;
            if (entity is BuildingBlock block)
            {
                block.blockDefinition = PrefabAttribute.server.Find<Construction>(block.prefabID);
                var (grade, skin) = GetGradeAndSkin(room.TierConfig.BuildingGradeSelection);
                block.SetGrade(grade);
                block.skinID = skin;
                block.grounded = true;
            }
            entity.OwnerID = OwnerID;
            entity.enableSaving = false;
            entity.Spawn();
            IndexDungeonEntity(entity, room);
            if (entity is BuildingBlock b)
            {
                var bucket = GetSpatialBucket(b.transform.position, 1f);
                if (!room.BuildingBlocksByPosition.TryGetValue(bucket, out var blocks))
                    room.BuildingBlocksByPosition[bucket] = blocks = new List<BuildingBlock>();
                blocks.Add(b);
                b.SetHealthToMax();
                if (b.skinID == 10221 && room.TierConfig.ContainerColor >= 1 && room.TierConfig.ContainerColor <= 16)
                {
                    b.SetCustomColour((uint)room.TierConfig.ContainerColor);
                }
            }
            room.OccupiedPositions.Add(key);
            return entity;
        }

        private BaseEntity CreateDoor(Vector3 position, Quaternion rotation, ActiveDungeon room)
        {
            var door = GameManager.server.CreateEntity(DoorPrefab, position, rotation);
            if (door == null)
                return null;
            door.skinID = room.TierConfig.DoorSkinID;
            door.OwnerID = OwnerID;
            door.enableSaving = false;
            door.Spawn();
            room.Entities.Add(door);
            if (door is Door dungeonDoor)
                room.Doors.Add(dungeonDoor);
            RegisterSpawnedEntityForDungeon(door, room.UniqueId);
            return door;
        }

        private void CreateLight(Vector3 centre, ActiveDungeon room)
        {
            if (room == null || _config == null || IsClassicDungeon(room))
                return;
            if (!_config.EnableDungeonLights)
                return;
            var lightPos = centre + CeilingLightOffset + Vector3.up * CeilingHeight;
            var posKey = "LIGHT:" + PositionKey(lightPos, 2);
            if (room.OccupiedPositions.Contains(posKey))
                return;
            room.OccupiedPositions.Add(posKey);
            var rotation = Quaternion.Euler(90f, 0, 0);
            var lightEntity = GameManager.server.CreateEntity(LightPrefab, lightPos, rotation);
            if (lightEntity == null)
                return;
            lightEntity.OwnerID = OwnerID;
            lightEntity.enableSaving = false;
            lightEntity.Spawn();
            if (lightEntity is IOEntity io)
            {
                io.UpdateHasPower(1000, 100);
                io.IOStateChanged(1000, 100);
                io.SendNetworkUpdateImmediate();
            }
            room.Entities.Add(lightEntity);
            RegisterSpawnedEntityForDungeon(lightEntity, room.UniqueId);
        }

        private void CreateTurret(Vector3 subCellPos, ActiveDungeon room)
        {
            var turretPos = subCellPos + new Vector3(SubCellSpacing * 0.5f, 0, SubCellSpacing * 0.5f) + _wallOffset;
            room.TurretPositions.Add(turretPos);
        }

        private void HandleSubCell(Vector3 subCellPos, ActiveDungeon room, bool allowTurret)
        {
            var foundation = IsClassicDungeon(room) ? CreateRockFloor(subCellPos, room) : CreateBlock(FoundationPrefab, subCellPos, Quaternion.identity, room);
            if (foundation == null)
                return;
            room.Entities.Add(foundation);
            var key = PositionKey(subCellPos, 2);
            if (room.FoundationKeys.Add(key))
                room.Foundations.Add(subCellPos);
            if (allowTurret)
                CreateTurret(subCellPos, room);
            var centre = IsClassicDungeon(room) ? subCellPos : RoundVector(subCellPos + new Vector3(SubCellSpacing * 0.5f, 0, SubCellSpacing * 0.5f), 2);
            if (room.RoomFloorCenters.Add(centre))
            {
                if (room.RoomFloorCenters.Count == 1)
                    room.UniformFloorHeight = centre.y;
                else if (centre.y != room.UniformFloorHeight)
                    room.HasUniformFloorHeight = false;
            }
            if (PlaceCeiling && !IsClassicDungeon(room))
                CreateCeiling(subCellPos, room);
        }

        private void CreateCeiling(Vector3 subCellPos, ActiveDungeon room)
        {
            var ceilingPos = subCellPos + Vector3.up * CeilingHeight;
            var ceiling = CreateBlock(CeilingPrefab, ceilingPos, Quaternion.identity, room);
            if (ceiling == null)
                return;
            room.Entities.Add(ceiling);
        }

        private IEnumerator BuildCorridor(Vector3 start, Vector3 end, ActiveDungeon room, bool spawnEntities, bool placePortal, bool entranceSegment = false)
        {
            var diff = end - start;
            var alongX = Mathf.Abs(diff.x) >= Mathf.Abs(diff.z);
            var primarySteps = Mathf.CeilToInt((alongX ? Mathf.Abs(diff.x) : Mathf.Abs(diff.z)) / SubCellSpacing);
            var dirX = alongX ? Mathf.Sign(diff.x) : 0f;
            var dirZ = alongX ? 0f : Mathf.Sign(diff.z);
            var allowTurret = spawnEntities && !placePortal;
            if (entranceSegment)
            {
                Vector3 inner = start;
                if (!placePortal)
                    inner += new Vector3(dirX, 0f, dirZ) * (dirX > 0f || dirZ > 0f ? 2f * SubCellSpacing : SubCellSpacing);
                Vector3 lower = Vector3.Min(inner, end);
                Vector3 upper = Vector3.Max(inner, end);
                if (alongX)
                {
                    lower.z = start.z - 0.35f;
                    upper.z = start.z + SubCells * SubCellSpacing + 0.35f;
                    upper.x += SubCellSpacing;
                }
                else
                {
                    lower.x = start.x - 0.35f;
                    upper.x = start.x + SubCells * SubCellSpacing + 0.35f;
                    upper.z += SubCellSpacing;
                }
                lower.y = start.y - 1.5f;
                upper.y = start.y + CeilingHeight + 1.5f;
                room.EntranceCorridorSegments.Add(new Bounds((lower + upper) * 0.5f, upper - lower));
            }
            int stepsInBatch = 0;
            int stepsPerBatch = Mathf.Clamp(Performance.BuildCellsPerBatch, 1, 100);
            for (var i = 0; i <= primarySteps; i++)
            {
                for (var j = 0; j < SubCells; j++)
                {
                    Vector3 p = alongX
                        ? new Vector3(start.x + dirX * i * SubCellSpacing, start.y, start.z + j * SubCellSpacing)
                        : new Vector3(start.x + j * SubCellSpacing, start.y, start.z + dirZ * i * SubCellSpacing);
                    HandleSubCell(p, room, allowTurret);
                }
                if (++stepsInBatch >= stepsPerBatch)
                {
                    stepsInBatch = 0;
                    yield return _buildBatchDelay;
                }
            }
            if (_config != null && _config.EnableDungeonLights)
            {
                var totalLength = alongX ? Mathf.Abs(diff.x) : Mathf.Abs(diff.z);
                var spacing = Mathf.Clamp(_config.DungeonLightSpacing, 2f, 30f);
                var lightCount = Mathf.Max(1, Mathf.CeilToInt(totalLength / spacing));
                for (var c = 0; c <= lightCount; c++)
                {
                    var offset = c * spacing + spacing * 0.5f;
                    if (offset > totalLength + 0.01f)
                        break;
                    Vector3 lightPos = alongX ? new Vector3(start.x + dirX * offset, start.y, start.z + SubCellSpacing) : new Vector3(start.x + SubCellSpacing, start.y, start.z + dirZ * offset);
                    CreateLight(lightPos, room);
                }
            }
            if (!placePortal)
                yield break;
            var mid = Vector3.Lerp(start, end, entranceSegment ? 0.80f : 0.50f);
            var portalOffset = alongX ? new Vector3(2.5f * dirX, 0.2f, 1.5f) : new Vector3(1.5f, 0.2f, 2.5f * dirZ);
            var yaw = alongX ? (dirX >= 0 ? 90f : 270f) : (dirZ >= 0 ? 0f : 180f);
            var portal = GameManager.server.CreateEntity(PortalPrefab, mid + portalOffset, Quaternion.Euler(0, yaw, 0)) as BasePortal;
            if (portal == null)
                yield break;
            portal.OwnerID = OwnerID;
            portal.Spawn();
            room.InsidePortal = portal;
            room.Entities.Add(portal);
            RegisterSpawnedEntityForDungeon(portal, room.UniqueId);
        }

        private bool TryPlaceWall(Vector3 foundationPos, Vector3 offset, float yaw, ActiveDungeon room)
        {
            if (room.FoundationKeys.Contains(PositionKey(foundationPos + offset, 2)))
                return true;
            Vector3 basePos =
                Mathf.Abs(offset.x) > 0
                    ? foundationPos + new Vector3(offset.x > 0 ? SubCellSpacing : 0f, 0f, SubCellSpacing * 0.5f) + _wallOffset
                    : foundationPos + new Vector3(SubCellSpacing * 0.5f, 0f, offset.z > 0 ? SubCellSpacing : 0f) + _wallOffset;
            if (IsClassicDungeon(room))
                return CreateRockWall(foundationPos, offset, room);
            if (HasLiveBuildingBlockAt(basePos, room))
                return true;
            room.OccupiedPositions.Remove(PositionKey(basePos, 2));
            var wall = CreateBlock(WallPrefab, basePos, Quaternion.Euler(0, yaw, 0), room);
            if (wall != null)
                room.Entities.Add(wall);
            return wall != null;
        }

        private bool HasLiveBuildingBlockAt(Vector3 position, ActiveDungeon room, float tolerance = 0.05f)
        {
            if (room?.Entities == null)
                return false;
            float toleranceSqr = tolerance * tolerance;
            var minimum = GetSpatialBucket(position - Vector3.one * tolerance, 1f);
            var maximum = GetSpatialBucket(position + Vector3.one * tolerance, 1f);
            for (int x = minimum.x; x <= maximum.x; x++)
            for (int y = minimum.y; y <= maximum.y; y++)
            for (int z = minimum.z; z <= maximum.z; z++)
            {
                if (!room.BuildingBlocksByPosition.TryGetValue(new Vector3Int(x, y, z), out var blocks))
                    continue;
                foreach (var block in blocks)
                {
                    if (block != null && !block.IsDestroyed && (block.transform.position - position).sqrMagnitude <= toleranceSqr)
                        return true;
                }
            }
            return false;
        }

        private void PlaceEdgeWalls(Vector3 cellPos, float size, bool frameNeeded, float yaw, string direction, ActiveDungeon room)
        {
            if (!frameNeeded || IsClassicDungeon(room) || _config?.PlaceDungeonDoors != true)
                return;
            for (var i = 0; i < SubCells; i++)
            {
                float x,
                    z;
                if (direction is "north" or "south")
                {
                    x = cellPos.x + i * SubCellSpacing + SubCellSpacing * 0.5f;
                    z = direction == "north" ? cellPos.z + size : cellPos.z;
                }
                else
                {
                    x = direction == "east" ? cellPos.x + size : cellPos.x;
                    z = cellPos.z + i * SubCellSpacing + SubCellSpacing * 0.5f;
                }
                var basePos = new Vector3(x, cellPos.y, z) + _wallOffset;
                if (room.OccupiedPositions.Contains(PositionKey(basePos, 2)))
                    continue;
                PlaceDoorFrame(basePos, yaw, room);
            }
        }

        private void PlaceCorridorFrame(string side, Vector3 cellPos, float size, ActiveDungeon room, bool isEntrance = false)
        {
            if (isEntrance ? _config?.PlaceEntranceDoors != true : _config?.PlaceDungeonDoors != true)
                return;
            float yaw;
            switch (side)
            {
                case "east":
                    yaw = 90f + WallRotationOffset;
                    break;
                case "west":
                    yaw = 270f + WallRotationOffset;
                    break;
                case "north":
                    yaw = 0f + WallRotationOffset;
                    break;
                case "south":
                    yaw = 180f + WallRotationOffset;
                    break;
                default:
                    yaw = 0f;
                    break;
            }
            for (var i = 0; i < SubCells; i++)
            {
                float x,
                    z;
                if (side == "east" || side == "west")
                {
                    x = side == "east" ? cellPos.x + size : cellPos.x;
                    z = cellPos.z + i * SubCellSpacing + SubCellSpacing * 0.5f;
                }
                else
                {
                    x = cellPos.x + i * SubCellSpacing + SubCellSpacing * 0.5f;
                    z = side == "north" ? cellPos.z + size : cellPos.z;
                }
                var basePos = new Vector3(x, cellPos.y, z) + _wallOffset;
                if (room.OccupiedPositions.Contains(PositionKey(basePos, 2)))
                    continue;
                var door = PlaceDoorFrame(basePos, yaw, room);
                if (isEntrance && door != null)
                    room.EntranceDoors.Add(door);
            }
        }

        private IEnumerator BuildFrameGrid(bool[] grid, Vector3 origin, Vector2Int? westCell, Vector2Int? eastCell, ActiveDungeon room)
        {
            int processed = 0;
            int batch = Mathf.Clamp(Performance.BuildFrameCellsPerBatch, 1, 100);
            var start = origin - new Vector3(_gridSize * CellSpacing * 0.5f, 0, _gridSize * CellSpacing * 0.5f);
            for (var x = 0; x < _gridSize; x++)
            for (var y = 0; y < _gridSize; y++)
            {
                if (!GetGridValue(grid, x, y))
                    continue;
                var cellPos = start + new Vector3(x * CellSpacing, 0, y * CellSpacing);
                ProcessCellForFrames(grid, x, y, cellPos, westCell, eastCell, room);
                if (++processed >= batch)
                {
                    processed = 0;
                    yield return _buildBatchDelay;
                }
            }
        }

        private void ProcessCellForFrames(bool[] grid, int x, int y, Vector3 cellPos, Vector2Int? westCell, Vector2Int? eastCell, ActiveDungeon room)
        {
            var size = SubCells * SubCellSpacing;
            var horizontal = GetGridValue(grid, x - 1, y) && GetGridValue(grid, x + 1, y);
            if (horizontal)
            {
                if (!GetGridValue(grid, x - 1, y))
                    PlaceEdgeWalls(cellPos, size, true, 0f + WallRotationOffset, "north", room);
                else
                    PlaceEdgeWalls(cellPos, size, GetGridValue(grid, x, y + 1), 0f + WallRotationOffset, "north", room);
                if (!GetGridValue(grid, x, y - 1))
                    PlaceEdgeWalls(cellPos, size, GetGridValue(grid, x, y - 1), 180f + WallRotationOffset, "south", room);
            }
            else
            {
                if (!GetGridValue(grid, x, y + 1))
                    PlaceEdgeWalls(cellPos, size, GetGridValue(grid, x, y + 1), 0f + WallRotationOffset, "north", room);
                if (!GetGridValue(grid, x, y - 1))
                    PlaceEdgeWalls(cellPos, size, GetGridValue(grid, x, y - 1), 180f + WallRotationOffset, "south", room);
            }
            if (!GetGridValue(grid, x + 1, y) && !(eastCell.HasValue && x == eastCell.Value.x && y == eastCell.Value.y))
                PlaceEdgeWalls(cellPos, size, true, 90f + WallRotationOffset, "east", room);
            if (!GetGridValue(grid, x - 1, y) && !(westCell.HasValue && x == westCell.Value.x && y == westCell.Value.y))
                PlaceEdgeWalls(cellPos, size, true, 270f + WallRotationOffset, "west", room);
        }

        private IEnumerator BuildRoomGrid(bool[] grid, Vector3 origin, Vector2Int? westCell, Vector2Int? eastCell, ActiveDungeon room)
        {
            int processed = 0;
            int batch = Mathf.Clamp(Performance.BuildCellsPerBatch, 1, 100);
            var start = origin - new Vector3(_gridSize * CellSpacing * 0.5f, 0, _gridSize * CellSpacing * 0.5f);
            for (var x = 0; x < _gridSize; x++)
            for (var y = 0; y < _gridSize; y++)
            {
                if (!GetGridValue(grid, x, y))
                    continue;
                var cellPos = start + new Vector3(x * CellSpacing, 0, y * CellSpacing);
                ProcessCell(grid, x, y, cellPos, westCell, eastCell, room);
                if (++processed >= batch)
                {
                    processed = 0;
                    yield return _buildBatchDelay;
                }
            }
        }

        private void ProcessCell(bool[] grid, int x, int y, Vector3 cellPos, Vector2Int? westCell, Vector2Int? eastCell, ActiveDungeon room)
        {
            var isolated = IsIsolatedCell(grid, x, y);
            for (var sx = 0; sx < SubCells; sx++)
            for (var sy = 0; sy < SubCells; sy++)
            {
                var subPos = cellPos + new Vector3(sx * SubCellSpacing, 0, sy * SubCellSpacing);
                HandleSubCell(subPos, room, !isolated);
            }
            if (PlaceLights)
            {
                var size = SubCells * SubCellSpacing;
                var centre = cellPos + new Vector3(size / 2f, 0, size / 2f);
                CreateLight(centre, room);
            }
        }

        private int GridIndex(int x, int y)
        {
            return x < 0 || x >= _gridSize || y < 0 || y >= _gridSize ? -1 : y * _gridSize + x;
        }

        private bool GetGridValue(bool[] grid, int x, int y)
        {
            var idx = GridIndex(x, y);
            return idx != -1 && grid[idx];
        }

        private void SetGridValue(bool[] grid, int x, int y, bool value)
        {
            var idx = GridIndex(x, y);
            if (idx != -1)
                grid[idx] = value;
        }

        private static int CountActiveCells(bool[] grid)
        {
            return grid.Count(t => t);
        }

        private bool IsIsolatedCell(bool[] grid, int x, int y)
        {
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                if ((dx != 0 || dy != 0) && GetGridValue(grid, x + dx, y + dy))
                    return false;
            return true;
        }

        private void PruneIsolatedCells(bool[] grid, int targetX, int targetY)
        {
            var reachable = FindConnectedCells(grid, targetX, targetY);
            for (int i = 0; i < grid.Length; i++)
                grid[i] &= reachable[i];
        }

        private bool[] FindConnectedCells(bool[] grid, int startX, int startY)
        {
            var reachable = new bool[grid.Length];
            int start = GridIndex(startX, startY);
            if (start == -1 || !grid[start])
                return reachable;
            var pending = new int[grid.Length];
            int readIndex = 0;
            int writeIndex = 0;
            reachable[start] = true;
            pending[writeIndex++] = start;
            while (readIndex < writeIndex)
            {
                int index = pending[readIndex++];
                int x = index % _gridSize;
                int y = index / _gridSize;
                QueueConnectedCell(grid, x + 1, y, reachable, pending, ref writeIndex);
                QueueConnectedCell(grid, x - 1, y, reachable, pending, ref writeIndex);
                QueueConnectedCell(grid, x, y + 1, reachable, pending, ref writeIndex);
                QueueConnectedCell(grid, x, y - 1, reachable, pending, ref writeIndex);
            }
            return reachable;
        }

        private void QueueConnectedCell(bool[] grid, int x, int y, bool[] reachable, int[] pending, ref int writeIndex)
        {
            var index = GridIndex(x, y);
            if (index == -1 || reachable[index] || !grid[index])
                return;
            reachable[index] = true;
            pending[writeIndex++] = index;
        }

        private static Vector3 RoundVector(Vector3 v, int decimals)
        {
            return new Vector3((float)Math.Round(v.x, decimals), (float)Math.Round(v.y, decimals), (float)Math.Round(v.z, decimals));
        }

        private static string PositionKey(Vector3 pos, int decimals)
        {
            var r = RoundVector(pos, decimals);
            return $"{r.x}_{r.y}_{r.z}";
        }

        private static Vector3Int GetSpatialBucket(Vector3 position, float size)
        {
            return new Vector3Int(Mathf.FloorToInt(position.x / size), Mathf.FloorToInt(position.y / size), Mathf.FloorToInt(position.z / size));
        }

        private class ZoneInfo
        {
            public Vector3 Pos;
            public float Dist;
            public Vector3 Size;
            public OBB OBB;
        }

        private class GeneratorConfig
        {
            public Vector3 Origin;
            public bool ClosestToOrigin;
            public float YOffset;
            public float MinimumDistance;
            public List<Vector3> BlockedPositions;
            public bool AvoidTerrainTopology;
            public TerrainTopology.Enum AvoidedTopology;
            public Vector3 WaterCheckOffset;
            public float AvoidWaterRadius;
            public bool AboveWater;
            public float MaxSlopeDegrees;
            public float CheckSphereRadius;
            public int CheckSphereMask;
            public int MaxAttempts;
            public bool UseSlopeCheck;
            public bool CheckSafeZones;
            public bool ZoneManager;
            public int TerrainMask;
            public bool CheckRocks;
            public List<string> RockCheck;
        }

        private IEnumerator TryFindPosition(GeneratorConfig config, DungeonRequest request, bool isPortal)
        {
            var sp = _config?.SpawnPointConfiguration;
            bool duelistLoaded = Duelist != null && Duelist.IsLoaded && (sp?.CheckDuelist ?? true);
            bool raidableBasesLoaded = RaidableBases != null && RaidableBases.IsLoaded && (sp?.CheckRaidableBases ?? true);
            bool abandonedBasesLoaded = AbandonedBases != null && AbandonedBases.IsLoaded && (sp?.CheckAbandonedBases ?? true);
            int attempts = config.MaxAttempts;
            float half = World.Size * 0.5f;
            if (config.ClosestToOrigin && isPortal && config.Origin != Vector3.zero)
            {
                Vector3 closePosition = Vector3.zero;
                yield return TryFindClosestPortalPosition(config, duelistLoaded, raidableBasesLoaded, abandonedBasesLoaded, result => closePosition = result);
                if (closePosition != Vector3.zero)
                {
                    request.PortalPosition = closePosition;
                    yield break;
                }
                LogDebug("Could not find purchased portal position near or far from origin after full map search.", 1);
                request.PortalPosition = Vector3.zero;
                yield break;
            }
            if (config.ClosestToOrigin)
            {
                Vector3 bestPosition = Vector3.zero;
                float bestDist = float.MaxValue;
                int count = 0;
                while (attempts-- > 0)
                {
                    float rx = Random.Range(-half, half);
                    float rz = Random.Range(-half, half);
                    Vector3 candidate = new Vector3(rx, 0f, rz);
                    Vector3 final = CheckCandidate(candidate, config, duelistLoaded, raidableBasesLoaded, abandonedBasesLoaded, isPortal);
                    if (final != Vector3.zero)
                    {
                        float dist = Vector3.Distance(final, config.Origin);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestPosition = final;
                        }
                    }
                    count++;
                    if (count >= Mathf.Clamp(Performance.SearchAttemptsPerBatch, 1, 2000))
                    {
                        count = 0;
                        yield return _searchBatchDelay;
                    }
                }
                if (isPortal)
                    request.PortalPosition = bestPosition;
                else
                    request.DungeonPosition = bestPosition;
                yield break;
            }
            int normalCount = 0;
            while (attempts-- > 0)
            {
                float rx = Random.Range(-half, half);
                float rz = Random.Range(-half, half);
                Vector3 candidate = new Vector3(rx, 0f, rz);
                Vector3 final = CheckCandidate(candidate, config, duelistLoaded, raidableBasesLoaded, abandonedBasesLoaded, isPortal);
                if (final != Vector3.zero)
                {
                    if (isPortal)
                        request.PortalPosition = final;
                    else
                        request.DungeonPosition = final;
                    yield break;
                }
                normalCount++;
                if (normalCount >= Mathf.Clamp(Performance.SearchAttemptsPerBatch, 1, 2000))
                {
                    normalCount = 0;
                    yield return _searchBatchDelay;
                }
            }
            if (isPortal)
                request.PortalPosition = Vector3.zero;
            else
                request.DungeonPosition = Vector3.zero;
        }

        private IEnumerator TryFindClosestPortalPosition(GeneratorConfig config, bool duelistLoaded, bool raidableBasesLoaded, bool abandonedBasesLoaded, Action<Vector3> result)
        {
            int count = 0;
            int batch = Mathf.Clamp(Performance.SearchAttemptsPerBatch, 1, 2000);
            Vector3 origin = config.Origin;
            origin.y = 0f;
            for (int pass = 0; pass < 4; pass++)
            {
                bool strictPass = pass == 0;
                float maxRadius =
                    pass == 0 ? 75f
                    : pass == 1 ? 140f
                    : pass == 2 ? PurchasedPortalSearchMaxRadius
                    : PurchasedPortalFallbackSearchMaxRadius;
                float radiusStep =
                    pass == 0 ? PurchasedPortalSearchRadiusStep
                    : pass == 1 ? PurchasedPortalSearchRadiusStep
                    : pass == 2 ? PurchasedPortalSearchRadiusStep * 1.5f
                    : PurchasedPortalSearchRadiusStep * 2f;
                float spacing =
                    pass == 0 ? PurchasedPortalTargetSpacing
                    : pass == 1 ? PurchasedPortalTargetSpacing
                    : pass == 2 ? PurchasedPortalTargetSpacing * 1.5f
                    : PurchasedPortalTargetSpacing * 2f;
                for (float radius = PurchasedPortalSearchStartRadius; radius <= maxRadius; radius += radiusStep)
                {
                    int samples = Mathf.Clamp(Mathf.CeilToInt((2f * Mathf.PI * radius) / spacing), 12, 256);
                    float angleOffset = Random.Range(0f, 360f);
                    for (int i = 0; i < samples; i++)
                    {
                        float angle = (angleOffset + (360f * i / samples)) * Mathf.Deg2Rad;
                        Vector3 candidate = origin + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                        if (!IsInsideWorld(candidate))
                            continue;
                        Vector3 final = strictPass
                            ? CheckCandidate(candidate, config, duelistLoaded, raidableBasesLoaded, abandonedBasesLoaded, true)
                            : CheckPurchasedPortalCandidate(candidate, config, Mathf.Min(pass, 2), duelistLoaded, raidableBasesLoaded, abandonedBasesLoaded);
                        if (final != Vector3.zero)
                        {
                            result(final);
                            yield break;
                        }
                        count++;
                        if (count >= batch)
                        {
                            count = 0;
                            yield return _searchBatchDelay;
                        }
                    }
                }
            }
            int randomAttempts = Mathf.Max(config.MaxAttempts, 12000);
            float half = Mathf.Max(25f, World.Size * 0.5f - 25f);
            while (randomAttempts-- > 0)
            {
                Vector3 candidate = new Vector3(Random.Range(-half, half), 0f, Random.Range(-half, half));
                if (!IsInsideWorld(candidate))
                    continue;
                Vector3 final = CheckPurchasedPortalCandidate(candidate, config, 2, duelistLoaded, raidableBasesLoaded, abandonedBasesLoaded);
                if (final != Vector3.zero)
                {
                    result(final);
                    yield break;
                }
                count++;
                if (count >= batch)
                {
                    count = 0;
                    yield return _searchBatchDelay;
                }
            }
            result(Vector3.zero);
        }

        private bool IsInsideWorld(Vector3 position)
        {
            float limit = World.Size * 0.5f - 25f;
            return position.x > -limit && position.x < limit && position.z > -limit && position.z < limit;
        }

        private Vector3 CheckPurchasedPortalCandidate(Vector3 position, GeneratorConfig cfg, int pass, bool duelistLoaded, bool raidableBasesLoaded, bool abandonedBasesLoaded)
        {
            if (IsNearBlocked(position, cfg))
                return Vector3.zero;
            if (HasBlockedRespawnEntityNearby(position, 4f))
                return Vector3.zero;
            position.y = TerrainMeta.HeightMap.GetHeight(position);
            int topology = TerrainMeta.TopologyMap.GetTopology(position);
            if ((topology & (int)TerrainTopology.Enum.Ocean) != 0)
                return Vector3.zero;
            if (pass == 1 && (topology & (int)TerrainTopology.Enum.Monument) != 0)
                return Vector3.zero;
            float slope = TerrainMeta.HeightMap.GetSlope(position);
            if (slope > PurchasedPortalRelaxedMaxSlope)
                return Vector3.zero;
            if (cfg.CheckRocks && (IsInsideRock(position, cfg) || AntiHack.TestInsideTerrain(position)))
                return Vector3.zero;
            float safeZoneDistance = pass == 1 ? PurchasedPortalStrictSafeZoneDistance : PurchasedPortalRelaxedSafeZoneDistance;
            if (cfg.CheckSafeZones && SafeZoneBlockedForPurchasedPortal(position, safeZoneDistance))
                return Vector3.zero;
            if (pass == 1)
            {
                if (cfg.ZoneManager && ZoneBlocked(position))
                    return Vector3.zero;
                if (duelistLoaded && Convert.ToBoolean(Duelist?.Call("DuelistTerritory", position)))
                    return Vector3.zero;
                if (raidableBasesLoaded && Convert.ToBoolean(RaidableBases?.Call("EventTerritory", position)))
                    return Vector3.zero;
                if (abandonedBasesLoaded && Convert.ToBoolean(AbandonedBases?.Call("EventTerritory", position)))
                    return Vector3.zero;
            }
            float radius = GetPortalProtectionRadius() + PurchasedPortalBaseAvoidanceExtraRadius;
            int solidMask = Layers.Mask.World | Layers.Mask.Construction | Layers.Mask.Deployed | Layers.Mask.Default | Layers.Mask.Tree;
            if (Physics.CheckSphere(position + Vector3.up * 1.0f, radius, solidMask, QueryTriggerInteraction.Ignore))
                return Vector3.zero;
            return position;
        }

        private bool SafeZoneBlockedForPurchasedPortal(Vector3 pos, float distance)
        {
            foreach (var zone in TriggerSafeZone.allSafeZones)
            {
                if (zone == null || zone.transform == null)
                    continue;
                if (Vector3Ex.Distance2D(zone.transform.position, pos) <= distance)
                    return true;
            }
            return false;
        }

        private Vector3 CheckCandidate(Vector3 position, GeneratorConfig cfg, bool duelistLoaded, bool raidableBasesLoaded, bool abandonedBasesLoaded, bool isPortal)
        {
            if (isPortal)
            {
                if (IsNearBlocked(position, cfg))
                    return Vector3.zero;
            }
            else
            {
                if (IsPositionTooCloseToExistingDungeons(position, cfg.MinimumDistance))
                    return Vector3.zero;
                if (IsNearBlocked(position, cfg))
                    return Vector3.zero;
            }
            bool underground = !isPortal && cfg.YOffset < 0f;
            if (underground)
            {
                Vector3 waterCheckCenter = position + cfg.WaterCheckOffset;
                if (cfg.AvoidWaterRadius > 0f && HasWaterInClearanceArea(waterCheckCenter, cfg.AvoidWaterRadius))
                    return Vector3.zero;
                position.y = cfg.YOffset;
            }
            else if (cfg.YOffset < 2f)
            {
                if (cfg.AboveWater)
                    position.y = WaterLevel.GetWaterOrTerrainSurface(position, false, false);
                else
                    position.y = TerrainMeta.HeightMap.GetHeight(position);
            }
            else
                position.y = cfg.YOffset;
            float slope = TerrainMeta.HeightMap.GetSlope(position);
            if (cfg.UseSlopeCheck && slope > cfg.MaxSlopeDegrees)
                return Vector3.zero;
            if (cfg.AvoidTerrainTopology && InAvoidedTopology(position, cfg))
                return Vector3.zero;
            if (cfg.CheckSphereRadius > 0f && Physics.CheckSphere(position, cfg.CheckSphereRadius, cfg.CheckSphereMask))
                return Vector3.zero;
            if ((_config?.SpawnPointConfiguration?.CheckEntities ?? true) && cfg.CheckSphereRadius > 0f && HasEntities(position, cfg.CheckSphereRadius, cfg.CheckSphereMask))
                return Vector3.zero;
            if (cfg.CheckRocks && !underground && (IsInsideRock(position, cfg) || AntiHack.TestInsideTerrain(position)))
                return Vector3.zero;
            if (cfg.CheckSafeZones && SafeZoneBlocked(position))
                return Vector3.zero;
            if (cfg.ZoneManager && ZoneBlocked(position))
                return Vector3.zero;
            if (duelistLoaded && Convert.ToBoolean(Duelist?.Call("DuelistTerritory", position)))
                return Vector3.zero;
            if (raidableBasesLoaded && Convert.ToBoolean(RaidableBases?.Call("EventTerritory", position)))
                return Vector3.zero;
            if (abandonedBasesLoaded && Convert.ToBoolean(AbandonedBases?.Call("EventTerritory", position)))
                return Vector3.zero;
            return position;
        }

        private bool IsPositionTooCloseToExistingDungeons(Vector3 position, float buffer)
        {
            foreach (var dungeon in _activeDungeons)
            {
                if (dungeon == null)
                    continue;
                if (dungeon.DungeonBounds.size != Vector3.zero)
                {
                    var bounds = dungeon.DungeonBounds;
                    var horizontalExtent = Mathf.Max(bounds.extents.x, bounds.extents.z);
                    var minimumDistance = horizontalExtent + buffer;
                    float dx = position.x - bounds.center.x;
                    float dz = position.z - bounds.center.z;
                    float sqrDistance = dx * dx + dz * dz;
                    if (sqrDistance < minimumDistance * minimumDistance)
                        return true;
                    continue;
                }
                if (dungeon.DungeonPosition == Vector3.zero)
                    continue;
                float fallbackBuffer = Mathf.Max(buffer, 120f);
                float dxFallback = position.x - dungeon.DungeonPosition.x;
                float dzFallback = position.z - dungeon.DungeonPosition.z;
                float sqrFallbackDistance = dxFallback * dxFallback + dzFallback * dzFallback;
                if (sqrFallbackDistance < fallbackBuffer * fallbackBuffer)
                    return true;
            }
            return false;
        }

        private bool IsNearBlocked(Vector3 position, GeneratorConfig cfg)
        {
            if (cfg.BlockedPositions == null || cfg.BlockedPositions.Count == 0)
                return false;
            for (int i = 0; i < cfg.BlockedPositions.Count; i++)
            {
                if (Vector3.Distance(position, cfg.BlockedPositions[i]) < cfg.MinimumDistance)
                    return true;
            }
            return false;
        }

        private bool InAvoidedTopology(Vector3 pos, GeneratorConfig cfg)
        {
            int top = TerrainMeta.TopologyMap.GetTopology(pos);
            return (top & (int)cfg.AvoidedTopology) != 0;
        }

        private bool IsInsideRock(Vector3 point, GeneratorConfig cfg)
        {
            bool previousHitBackfaces = Physics.queriesHitBackfaces;
            bool upward;
            try
            {
                Physics.queriesHitBackfaces = true;
                upward = CheckRockUp(point, cfg);
            }
            finally
            {
                Physics.queriesHitBackfaces = previousHitBackfaces;
            }
            if (upward)
                return true;
            return CheckRockDown(point, cfg);
        }

        private bool CheckRockUp(Vector3 p, GeneratorConfig cfg)
        {
            if (!Physics.Raycast(p, Vector3.up, out RaycastHit hit, 20f, cfg.TerrainMask))
                return false;
            return IsRockName(hit.collider.name, cfg.RockCheck);
        }

        private bool CheckRockDown(Vector3 p, GeneratorConfig cfg)
        {
            Vector3 from = p + Vector3.up * 20f;
            Vector3 dir = p - from;
            RaycastHit[] hits = Physics.RaycastAll(from, dir, dir.magnitude, cfg.TerrainMask);
            for (int i = 0; i < hits.Length; i++)
            {
                if (IsRockName(hits[i].collider.name, cfg.RockCheck))
                    return true;
            }
            return false;
        }

        private bool IsRockName(string s, List<string> terms)
        {
            for (int i = 0; i < terms.Count; i++)
            {
                if (s.IndexOf(terms[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private bool SafeZoneBlocked(Vector3 pos)
        {
            foreach (var t in TriggerSafeZone.allSafeZones)
            {
                if (Vector3Ex.Distance2D(t.transform.position, pos) <= 200f)
                    return true;
            }
            return false;
        }

        private bool ZoneBlocked(Vector3 pos)
        {
            if (_blockedZones.Count == 0)
                return false;
            foreach (var kvp in _blockedZones)
            {
                var z = kvp.Value;
                if (z.Size != Vector3.zero)
                {
                    if (z.OBB.ClosestPoint(pos) == pos)
                        return true;
                }
                else
                {
                    if (Vector3Ex.Distance2D(z.Pos, pos) <= z.Dist)
                        return true;
                }
            }
            return false;
        }

        private void BlockZones()
        {
            if (ZoneManager == null || !ZoneManager.IsLoaded)
                return;
            _blockedZones.Clear();
            var zoneIds = ZoneManager.Call("GetZoneIDs") as string[];
            if (zoneIds == null)
                return;
            for (int i = 0; i < zoneIds.Length; i++)
            {
                var p = ZoneManager.Call("GetZoneLocation", zoneIds[i]);
                if (!(p is Vector3 v) || v == default)
                    continue;
                var zi = new ZoneInfo { Pos = v, OBB = new OBB(v, Vector3.zero, Quaternion.identity) };
                var rr = ZoneManager.Call("GetZoneRadius", zoneIds[i]);
                if (rr is float dist)
                    zi.Dist = dist;
                var sz = ZoneManager.Call("GetZoneSize", zoneIds[i]);
                if (sz is Vector3 s)
                {
                    zi.Size = s;
                    zi.OBB = new OBB(zi.Pos, s, Quaternion.identity);
                }
                _blockedZones[v] = zi;
            }
        }

        private bool HasEntities(Vector3 position, float radius, int layers)
        {
            List<BaseEntity> list = Pool.Get<List<BaseEntity>>();
            try
            {
                Vis.Entities(position, radius, list, layers);
                return list.Count > 0;
            }
            finally
            {
                Pool.FreeUnmanaged(ref list);
            }
        }
        #endregion
    }
}
 