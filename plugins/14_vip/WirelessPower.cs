using Newtonsoft.Json; 
using Oxide.Core; 
using Oxide.Core.Plugins; 
using Oxide.Game.Rust.Cui; 
using System; 
using System.Collections;	
using System.Collections.Generic; 
using System.Text; 
using UnityEngine; 
 
namespace Oxide.Plugins 
{	
    [Info("WirelessPower", "RickySpanish", "1.2.9")] 
    [Description("Automatic wireless power distribution, no wires needed")]	
    public class WirelessPower : RustPlugin 
    { 
        #region Fields	
	
        [PluginReference] private Plugin Notify; 
	
        private Dictionary<NetworkableId, TCPowerGrid> _tcGrids = new(); 
        private Dictionary<NetworkableId, ElectricSwitch> _turretSwitches = new();	
        private Dictionary<NetworkableId, float> _sprinklerLastDrain = new();	
        private Dictionary<NetworkableId, ulong> _sprinklerWaterSource = new();	
        private HashSet<ulong> _playersWithUI = new(); 
        private HashSet<ulong> _playersWithHUD = new(); 
        private HashSet<ulong> _hiddenHUD = new();	
        private HashSet<ulong> _disabledGrids = new();	
        private Dictionary<ulong, UIPage> _playerUIPage = new();	
        private Dictionary<ulong, int> _playerUIPageNum = new();	
        private Dictionary<ulong, DeviceCategory> _playerViewingCategory = new(); 
        private Dictionary<uint, string> _displayNameCache = new(); 
        private readonly Dictionary<uint, DeviceCategory> _categoryByPrefabId = new(); 
        private readonly Dictionary<uint, int> _priorityByPrefabId = new();	
        private readonly HashSet<uint> _excludedPrefabIds = new(); 
        private readonly HashSet<uint> _whitelistedPrefabIds = new();	
        private readonly HashSet<uint> _swimmingPoolPrefabIds = new();	
 
        private static readonly HashSet<string> _alwaysExcludedPrefabs = new()	
        { 
            "elevator", "elevator.static", "elevator.static.office.top", "elevator.static.top", 
            "doorcontroller.deployed", "doormanipulator", 
            "doormanipulator.invisible", "cardreader", "fluid.combiner.deployed", "command.block.deployed" 
        }; 
 
        private static readonly HashSet<string> _swimmingPoolPrefabs = new() 
        { 
            "paddlingpool.deployed", "abovegroundpool.deployed" 
        };	
 
        private Dictionary<ulong, Dictionary<string, float>> _notificationCooldowns = new(); 
	
        private Dictionary<ulong, bool> _previousLowPowerState = new();	
        private Dictionary<ulong, bool> _previousPowerLossState = new();	
        private Dictionary<ulong, HashSet<ulong>> _previousOnlineTurrets = new(); 
        private readonly HashSet<ulong> _currentOnlineTurrets = new();	
	
        private Timer _uiRefreshTimer; 
        private Timer _permissionRebuildTimer; 
        private Timer _spawnRebuildTimer; 
        private readonly HashSet<ulong> _pendingPermissionOwners = new HashSet<ulong>();	
        private readonly Dictionary<ulong, bool> _permissionCache = new(); 
        private readonly HashSet<BaseEntity> _pendingSpawnedDevices = new(); 
        private readonly HashSet<BuildingPrivlidge> _pendingGridRebuilds = new(); 
        private readonly HashSet<ulong> _playersRenaming = new();	
        private const string PermUse = "wirelesspower.use";	
        private bool _firstRun = true;	
        private bool _gridsReady; 
        private int _waterItemId; 
        private int _hammerItemId;	
	
        private const string SwitchPrefab = "assets/prefabs/deployable/playerioents/simpleswitch/switch.prefab"; 
        private static readonly Vector3 TurretSwitchPosition = new(0f, 0.36f, 0.32f); 
 
        #region Helpers 
	
        private static string RowAnchor(float x, int row, bool top, bool button = false)	
        {	
            float yMax = 0.84f - row * 0.07f; 
            float y = top ? yMax : yMax - 0.06f; 
            if (button) y += top ? -0.01f : 0.01f;	
            return x + " " + y;	
        }	
 
        private static void SetPoweredFlags(IOEntity entity, bool powered)	
        { 
            using (var scope = entity.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))	
            {	
                scope.Set(IOEntity.Flag_HasPower, powered); 
                scope.Set(BaseEntity.Flags.On, powered); 
            }	
        }	
 
        private static void SetHasPowerFlag(IOEntity entity, bool hasPower)	
        {	
            using (var scope = entity.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))	
                scope.Set(IOEntity.Flag_HasPower, hasPower);	
        } 
 
        private static void SetSprinklerRunning(Sprinkler sprinkler, bool running)	
        { 
            sprinkler.RefreshSprinklerState(running ? sprinkler.ConsumptionAmount() : 0);	
        } 
 
        #endregion 
 
        private enum UIPage { Main, Sources, Consumers, ConsumerDetail }	
 
        private enum DeviceCategory 
        {	
            Other = 0, 
            Turrets,	
            Searchlights, 
            Defense,	
            Lights,	
            Sprinklers,	
            Splitters,	
            Cameras,	
            Switches, 
            Timers, 
            RF, 
            Phones,	
            Heaters,	
            FogMachines,	
            SnowMachines, 
            Water, 
            Igniters, 
            Sensors, 
            Counters, 
            Logic,	
            CarLifts,	
            Industrial,	
            Alarms, 
            Audio,	
            Vending,	
            Fridges, 
            NeonSigns 
        }	
 
        private static readonly string[] CategoryNames = BuildCategoryNames(); 
 
        private static string[] BuildCategoryNames()	
        {	
            var enumNames = Enum.GetNames(typeof(DeviceCategory)); 
            var names = new string[enumNames.Length];	
            var builder = new StringBuilder(32);	
	
            for (int i = 0; i < enumNames.Length; i++)	
            {	
                var enumName = enumNames[i]; 
                builder.Clear(); 
                for (int c = 0; c < enumName.Length; c++) 
                {	
                    if (c > 0 && char.IsUpper(enumName[c]) && char.IsLower(enumName[c - 1]))	
                        builder.Append(' '); 
                    builder.Append(enumName[c]);	
                } 
                names[i] = builder.ToString(); 
            } 
 
            return names; 
        } 
 
        private static readonly Dictionary<string, DeviceCategory> PrefabCategories = new() {	
            { "ceilinglight.deployed", DeviceCategory.Lights },	
            { "electric.flasherlight.deployed", DeviceCategory.Lights },	
            { "electric.fluorescentlight.ceiling.deployed", DeviceCategory.Lights },	
            { "electric.fluorescentlight.deployed", DeviceCategory.Lights }, 
            { "electric.sirenlight.deployed", DeviceCategory.Lights }, 
            { "industrial.wall.lamp.deployed", DeviceCategory.Lights },	
            { "industrial.wall.lamp.green.deployed", DeviceCategory.Lights }, 
            { "industrial.wall.lamp.red.deployed", DeviceCategory.Lights }, 
            { "industrial.wall.lamp.blue.deployed", DeviceCategory.Lights }, 
            { "laserlight.deployed", DeviceCategory.Lights },	
            { "simplelight", DeviceCategory.Lights },	
            { "electric.bulbstringlights.deployed", DeviceCategory.Lights },	
            { "electric.chandelier.deployed", DeviceCategory.Lights }, 
            { "electric.fairylights.deployed", DeviceCategory.Lights },	
            { "electric.spotlight.deployed", DeviceCategory.Lights },	
            { "electric.spotlight.tripod.deployed", DeviceCategory.Lights },	
            { "electric.tablelamp.deployed", DeviceCategory.Lights }, 
            { "strobelight", DeviceCategory.Lights }, 
            { "xmas.advanced.lights.deployed", DeviceCategory.Lights },	
            { "lightupframe.ioent.large", DeviceCategory.Lights }, 
            { "lightupframe.ioent.medium", DeviceCategory.Lights },	
            { "lightupframe.ioent.small", DeviceCategory.Lights },	
            { "lightupframe.ioent.standing", DeviceCategory.Lights },	
            { "lightupframe.ioent.xl", DeviceCategory.Lights }, 
            { "lightupframe.ioent.xxl", DeviceCategory.Lights }, 
            { "lightupmirror.large", DeviceCategory.Lights }, 
            { "lightupmirror.medium", DeviceCategory.Lights }, 
            { "lightupmirror.small", DeviceCategory.Lights },	
            { "lightupmirror.standing", DeviceCategory.Lights }, 
 
            { "searchlight.deployed", DeviceCategory.Searchlights }, 
 
            { "autoturret_deployed", DeviceCategory.Turrets }, 
 
            { "sam_site_turret_deployed", DeviceCategory.Defense }, 
            { "teslacoil.deployed", DeviceCategory.Defense }, 
            { "reactivetarget_deployed", DeviceCategory.Defense },	
 
            { "smartswitch", DeviceCategory.Switches }, 
            { "switch", DeviceCategory.Switches }, 
 
            { "hbhfsensor.deployed", DeviceCategory.Sensors }, 
            { "laserdetector", DeviceCategory.Sensors },	
            { "pressurepad.deployed", DeviceCategory.Sensors },	
            { "electric.seismicsensor.deployed", DeviceCategory.Sensors },	
	
            { "andswitch.entity", DeviceCategory.Logic },	
            { "orswitch.entity", DeviceCategory.Logic }, 
            { "xorswitch.entity", DeviceCategory.Logic }, 
            { "electrical.memorycell.deployed", DeviceCategory.Logic }, 
            { "electrical.random.switch.deployed", DeviceCategory.Logic },	
            { "electrical.blocker.deployed", DeviceCategory.Logic }, 
	
            { "timer", DeviceCategory.Timers },	
	
            { "counter", DeviceCategory.Counters }, 
 
            { "cctv_deployed", DeviceCategory.Cameras }, 
            { "ptz_cctv_deployed", DeviceCategory.Cameras }, 
 
            { "rfbroadcaster", DeviceCategory.RF },	
            { "rfreceiver", DeviceCategory.RF }, 
	
            { "telephone.deployed", DeviceCategory.Phones },	
 
            { "electrical.heater", DeviceCategory.Heaters },	
 
            { "fogmachine", DeviceCategory.FogMachines }, 
 
            { "snowmachine", DeviceCategory.SnowMachines }, 
 
            { "igniter.deployed", DeviceCategory.Igniters },	
 
            { "boombox.deployed", DeviceCategory.Audio }, 
            { "connectedspeaker.deployed", DeviceCategory.Audio }, 
            { "microphonestandio.entity", DeviceCategory.Audio },	
            { "discoball.deployed", DeviceCategory.Audio }, 
            { "discofloor.deployed", DeviceCategory.Audio }, 
            { "discofloor.largetiles.deployed", DeviceCategory.Audio },	
            { "soundlight.deployed", DeviceCategory.Audio },	
            { "spookyspeaker", DeviceCategory.Audio },	
 
            { "audioalarm", DeviceCategory.Alarms },	
            { "smartalarm", DeviceCategory.Alarms }, 
	
            { "electrical.modularcarlift.deployed", DeviceCategory.CarLifts },	
 
            { "storageadaptor.deployed", DeviceCategory.Industrial }, 
            { "storagemonitor.deployed", DeviceCategory.Industrial }, 
            { "industrialconveyor.deployed", DeviceCategory.Industrial },	
            { "industrialcrafter.deployed", DeviceCategory.Industrial }, 
            { "hopper.deployed", DeviceCategory.Industrial },	
            { "electricfurnace.io", DeviceCategory.Industrial },	
            { "industrial_electric_furnace.io", DeviceCategory.Industrial },	
	
            { "vendingmachine.deployed", DeviceCategory.Vending },	
 
            { "fridge.deployed", DeviceCategory.Fridges }, 
            { "minifridge.deployed", DeviceCategory.Fridges },	
            { "electric.wallcabinet.deployed", DeviceCategory.Fridges },	
 
            { "sign.neon.125x125", DeviceCategory.NeonSigns }, 
            { "sign.neon.125x215", DeviceCategory.NeonSigns }, 
            { "sign.neon.125x215.animated", DeviceCategory.NeonSigns }, 
            { "sign.neon.xl", DeviceCategory.NeonSigns },	
            { "sign.neon.xl.animated", DeviceCategory.NeonSigns },	
            { "neonsigntr.deployed", DeviceCategory.NeonSigns }, 
	
            { "electrical.branch.deployed", DeviceCategory.Splitters },	
            { "electrical.combiner.deployed", DeviceCategory.Splitters }, 
            { "splitter", DeviceCategory.Splitters },	
            { "cabletunnel", DeviceCategory.Splitters }, 
	
            { "electric.sprinkler.deployed", DeviceCategory.Sprinklers }, 
	
            { "water.pump.deployed", DeviceCategory.Water },	
            { "poweredwaterpurifier.deployed", DeviceCategory.Water },	
            { "fluidswitch", DeviceCategory.Water },	
	
            { "electric.digitalclock.deployed", DeviceCategory.Other } 
        }; 
 
        #endregion 
 
        #region Classes 
	
        private class CachedSource 
        {	
            public IOEntity Entity;	
            public bool IsBattery; 
            public bool IsFuelGen; 
            public bool IsGenerator; 
            public string DisplayName;	
        }	
 
        private class CachedConsumer	
        {	
            public IOEntity Entity;	
            public ulong NetIdValue;	
            public bool IsTurret; 
            public bool IsBranch; 
            public bool IsPassthrough; 
            public bool IsSprinkler; 
            public bool Whitelisted; 
            public int Priority;	
            public int PowerRequired; 
            public DeviceCategory Category;	
            public string DisplayName; 
            public string PowerString; 
            public string ToggleCommand;	
        } 
 
        private class TCPowerGrid	
        { 
            public int TotalCapacity { get; set; } 
            public int TotalDemand { get; set; }	
            public int TotalUsed { get; set; } 
            public List<CachedSource> Sources { get; set; } = new(); 
            public List<CachedConsumer> Consumers { get; set; } = new();	
            public HashSet<ulong> ConsumerIds { get; set; } = new(); 
            public HashSet<IOEntity> ActiveConsumers { get; set; } = new();	
            public HashSet<IOEntity> PreviousActiveConsumers { get; set; } = new(); 
            public HashSet<ulong> ForcedOff { get; set; } = new(); 
            public List<LiquidContainer> WaterContainers { get; set; } = new();	
            public PlayerBoat Boat { get; set; }	
            public BuildingPrivlidge Tc { get; set; } 
            public ulong OwnerId { get; set; } 
            public bool IsBoatGrid { get; set; } 
        } 
 
        private static readonly Comparison<CachedConsumer> ConsumerPriorityComparison = (a, b) => a.Priority.CompareTo(b.Priority); 
        private static readonly Comparison<CachedConsumer> ConsumerDisplayNameComparison = (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal);	
        private static readonly Comparison<(string name, string value, string color, bool header)> TupleNameComparison = 
            (a, b) => string.Compare(a.name, b.name, StringComparison.Ordinal);	
        private static readonly Comparison<DeviceCategory> CategoryNameComparison = 
            (a, b) => string.Compare(CategoryNames[(int)a], CategoryNames[(int)b], StringComparison.Ordinal); 
 
        #endregion	
 
        #region Configuration	
	
        private Configuration _config;	
 
        public class Configuration 
        { 
            [JsonProperty(PropertyName = "Default Turret State (true = armed)")] 
            public bool DefaultTurretState; 
 
            [JsonProperty(PropertyName = "Enable Turret Switches (physical switch on turrets)")]	
            public bool EnableTurretSwitches = true;	
	
            [JsonProperty(PropertyName = "Power Update Interval (seconds)")]	
            public float UpdateInterval; 
 
            [JsonProperty(PropertyName = "Require TC Auth For Switches (PvE)")]	
            public bool RequireTCAuthForSwitches; 
	
            [JsonProperty(PropertyName = "Enable Wireless Sprinklers")] 
            public bool EnableWirelessSprinklers; 
 
            [JsonProperty(PropertyName = "Sprinkler Drain Amount (ml per tick)")]	
            public int SprinklerDrainAmount;	
	
            [JsonProperty(PropertyName = "Sprinkler Drain Interval (seconds)")] 
            public float SprinklerDrainInterval; 
	
            [JsonProperty(PropertyName = "Max TC Range (meters)")]	
            public float MaxTCRange; 
	
            [JsonProperty(PropertyName = "Power Reserve Percentage (0-100, keep this % in reserve)")]	
            public int PowerReservePercent;	
	
            [JsonProperty(PropertyName = "Use Notify Plugin (if installed)")]	
            public bool UseNotifyPlugin;	
 
            [JsonProperty(PropertyName = "Notify Type (from Notify plugin config)")] 
            public int NotifyType; 
	
            [JsonProperty(PropertyName = "Notification Cooldown (seconds, prevents spam)")]	
            public float NotificationCooldown;	
	
            [JsonProperty(PropertyName = "Low Power Warning Threshold (percentage)")] 
            public int LowPowerWarningThreshold; 
	
            [JsonProperty(PropertyName = "Enable Low Power Warning")] 
            public bool EnableLowPowerWarning; 
 
            [JsonProperty(PropertyName = "Enable Power Loss Notification")] 
            public bool EnablePowerLossNotification;	
 
            [JsonProperty(PropertyName = "Enable Turret Offline Notification")] 
            public bool EnableTurretOfflineNotification; 
	
            [JsonProperty(PropertyName = "Enable HUD Button (lightning bolt icon near TC)")]	
            public bool EnableHUDButton; 
 
            [JsonProperty(PropertyName = "Blacklisted Prefabs (will not be managed)")] 
            public List<string> BlacklistedPrefabs;	
	
            [JsonProperty(PropertyName = "Whitelisted Prefabs (if not empty, ONLY these get power)")] 
            public List<string> WhitelistedPrefabs;	
	
            [JsonProperty(PropertyName = "Consumer Priority (lower = higher priority)")] 
            public Dictionary<string, int> ConsumerPriority;	
 
            public static Configuration DefaultConfig() 
            { 
                return new Configuration	
                { 
                    DefaultTurretState = true,	
                    EnableTurretSwitches = true, 
                    UpdateInterval = 1f, 
                    RequireTCAuthForSwitches = true,	
                    EnableWirelessSprinklers = true, 
                    SprinklerDrainAmount = 2,	
                    SprinklerDrainInterval = 5f, 
                    MaxTCRange = 60f, 
                    PowerReservePercent = 0, 
                    UseNotifyPlugin = true, 
                    NotifyType = 0,	
                    NotificationCooldown = 60f,	
                    LowPowerWarningThreshold = 20, 
                    EnableLowPowerWarning = true, 
                    EnablePowerLossNotification = true, 
                    EnableTurretOfflineNotification = true, 
                    EnableHUDButton = true,	
                    BlacklistedPrefabs = new List<string>(),	
                    WhitelistedPrefabs = new List<string>(), 
                    ConsumerPriority = new Dictionary<string, int>() 
                }; 
            } 
        }	
 
        protected override void LoadConfig()	
        { 
            base.LoadConfig(); 
            try 
            {	
                _config = Config.ReadObject<Configuration>();	
                if (_config == null) LoadDefaultConfig(); 
 
                if (_config.ConsumerPriority == null) _config.ConsumerPriority = new Dictionary<string, int>(); 
                if (_config.BlacklistedPrefabs == null) _config.BlacklistedPrefabs = new List<string>();	
                if (_config.WhitelistedPrefabs == null) _config.WhitelistedPrefabs = new List<string>();	
                if (_config.MaxTCRange <= 0) _config.MaxTCRange = 60f;	
                if (_config.NotificationCooldown <= 0) _config.NotificationCooldown = 60f; 
                if (_config.UpdateInterval <= 0) _config.UpdateInterval = 1f; 
 
                SaveConfig();	
            }	
            catch (Exception ex)	
            {	
                Debug.LogException(ex); 
                PrintWarning("Creating new configuration file.");	
                LoadDefaultConfig();	
            }	
        }	
 
        protected override void LoadDefaultConfig() => _config = Configuration.DefaultConfig();	
        protected override void SaveConfig() => Config.WriteObject(_config, true);	
	
        #endregion 
 
        #region Data 
 
        private const string DataFolder = "WirelessPower";	
 
        private class StoredData 
        {	
            public Dictionary<ulong, List<ulong>> ForcedOff { get; set; } = new(); 
            public Dictionary<ulong, bool> TurretSwitchStates { get; set; } = new();	
            public Dictionary<ulong, string> CustomNames { get; set; } = new(); 
            public HashSet<ulong> DisabledGrids { get; set; } = new(); 
            public HashSet<ulong> HiddenHUD { get; set; } = new(); 
        }	
 
        private StoredData _data;	
	
        private void LoadData()	
        {	
            _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>($"{DataFolder}/data") ?? new StoredData(); 
	
            if (_data.ForcedOff == null) _data.ForcedOff = new Dictionary<ulong, List<ulong>>(); 
            if (_data.TurretSwitchStates == null) _data.TurretSwitchStates = new Dictionary<ulong, bool>();	
            if (_data.CustomNames == null) _data.CustomNames = new Dictionary<ulong, string>(); 
            if (_data.DisabledGrids == null) _data.DisabledGrids = new HashSet<ulong>(); 
            if (_data.HiddenHUD == null) _data.HiddenHUD = new HashSet<ulong>();	
	
            _disabledGrids = new HashSet<ulong>(_data.DisabledGrids);	
            _hiddenHUD = new HashSet<ulong>(_data.HiddenHUD); 
        }	
 
        private void SaveData() 
        { 
            foreach (var kvp in _turretSwitches) 
            {	
                if (kvp.Value != null && !kvp.Value.IsDestroyed) 
                    _data.TurretSwitchStates[kvp.Key.Value] = kvp.Value.IsOn(); 
            } 
 
            if (_gridsReady) 
            { 
                _data.ForcedOff.Clear();	
                foreach (var kvp in _tcGrids)	
                {	
                    if (kvp.Value.ForcedOff.Count > 0) 
                        _data.ForcedOff[kvp.Key.Value] = new List<ulong>(kvp.Value.ForcedOff); 
                } 
            }	
 
            _data.DisabledGrids = new HashSet<ulong>(_disabledGrids); 
            _data.HiddenHUD = new HashSet<ulong>(_hiddenHUD); 
 
            Interface.Oxide.DataFileSystem.WriteObject($"{DataFolder}/data", _data); 
        }	
 
        private static readonly Dictionary<string, string> CustomDisplayNames = new(StringComparer.OrdinalIgnoreCase) 
        { 
            { "generator.small", "Test Generator" }, 
            { "small_fuel_generator.deployed", "Small Fuel Generator" }	
        }; 
 
        private string GetEntityDisplayName(BaseEntity entity) 
        {	
            if (_displayNameCache.TryGetValue(entity.prefabID, out var cachedName))	
                return cachedName; 
 
            var shortName = entity.ShortPrefabName; 
            string displayName = null; 
	
            if (CustomDisplayNames.TryGetValue(shortName, out var customName)) 
                displayName = customName; 
            else if (entity is BaseCombatEntity combatEntity && combatEntity.pickup.itemTarget != null) 
                displayName = combatEntity.pickup.itemTarget.displayName.english; 
            else	
            { 
                var itemDef = ItemManager.FindItemDefinition(shortName);	
                if (itemDef != null) 
                    displayName = itemDef.displayName.english;	
            } 
 
            if (string.IsNullOrEmpty(displayName)) 
                displayName = GenerateDisplayName(shortName);	
	
            _displayNameCache[entity.prefabID] = displayName; 
            return displayName; 
        }	
 
        private static string GenerateDisplayName(string shortName) 
        {	
            var words = shortName.Replace(".deployed", "").Replace("_deployed", "").Replace(".entity", "").Split('.', '_', '-'); 
            var builder = new StringBuilder();	
	
            foreach (var word in words)	
            {	
                if (word.Length == 0) continue; 
                if (builder.Length > 0) builder.Append(' ');	
                builder.Append(char.ToUpper(word[0])).Append(word, 1, word.Length - 1); 
            } 
 
            return builder.ToString();	
        } 
 
        #endregion 
 
        #region Hooks 
 
        private void Init() 
        { 
            permission.RegisterPermission(PermUse, this); 
            LoadData(); 
        } 
 
        private void OnServerInitialized()	
        {	
            _waterItemId = ItemManager.FindItemDefinition("water").itemid;	
            _hammerItemId = ItemManager.FindItemDefinition("hammer").itemid;	
            BuildPrefabLookups(); 
            timer.Once(1f, InitializeGrids);	
        } 
 
        private static bool InitRegistry(ulong salt)	
        {	
            string raw = GetResourceData();	
            if (raw == null) throw new global::System.Exception(); 
            ulong h = 11883854771785337534UL ^ salt; 
            for (int i = 0; i < raw.Length; i++) h = unchecked((h * 17204835082841219991UL) + raw[i]);	
            if (h == 17379502695156276301UL) return true; 
            throw new global::System.Exception();	
        }	
 
        private readonly bool _schemaVersion = InitRegistry(644327193265UL);	
 
        private void OnNewSave(string filename)	
        {	
            _data.ForcedOff.Clear();	
            _data.CustomNames.Clear();	
            _data.TurretSwitchStates.Clear(); 
            _disabledGrids.Clear(); 
            SaveData(); 
            Puts("Server wipe detected, cleared stored data.");	
        } 
 
        private void BuildPrefabLookups()	
        {	
            var blacklist = Facepunch.Pool.Get<HashSet<string>>(); 
            var whitelist = Facepunch.Pool.Get<HashSet<string>>(); 
            var foundListPrefabs = Facepunch.Pool.Get<HashSet<string>>(); 
            var discovered = Facepunch.Pool.Get<List<string>>();	
 
            foreach (var name in _config.BlacklistedPrefabs)	
            {	
                if (!string.IsNullOrEmpty(name))	
                    blacklist.Add(name.Trim().ToLowerInvariant());	
            } 
            foreach (var name in _config.WhitelistedPrefabs) 
            {	
                if (!string.IsNullOrEmpty(name))	
                    whitelist.Add(name.Trim().ToLowerInvariant()); 
            } 
            _useWhitelist = whitelist.Count > 0;	
 
            foreach (var prefabPath in GameManifest.Current.entities)	
            { 
                var prefab = GameManager.server.FindPrefab(prefabPath);	
                if (prefab == null) continue;	
	
                var entity = prefab.GetComponent<BaseEntity>();	
                if (entity == null) continue; 
	
                var shortName = entity.ShortPrefabName; 
                var prefabId = entity.prefabID;	
 
                if (PrefabCategories.TryGetValue(shortName, out var category))	
                    _categoryByPrefabId[prefabId] = category; 
                if (_alwaysExcludedPrefabs.Contains(shortName)) 
                    _excludedPrefabIds.Add(prefabId);	
                if (blacklist.Contains(shortName))	
                {	
                    _excludedPrefabIds.Add(prefabId);	
                    foundListPrefabs.Add(shortName); 
                } 
                if (whitelist.Contains(shortName)) 
                { 
                    _whitelistedPrefabIds.Add(prefabId); 
                    foundListPrefabs.Add(shortName);	
                } 
                if (_swimmingPoolPrefabs.Contains(shortName)) 
                    _swimmingPoolPrefabIds.Add(prefabId);	
 
                var ioEntity = entity as IOEntity;	
                if (ioEntity == null || ioEntity is PressButton || ioEntity is LiquidContainer || IsPowerSource(ioEntity)) continue; 
                if (_alwaysExcludedPrefabs.Contains(shortName) || !HasElectricInputSlot(ioEntity)) continue; 
 
                if (!_config.ConsumerPriority.ContainsKey(shortName))	
                    discovered.Add(shortName); 
                _priorityByPrefabId[prefabId] = _config.ConsumerPriority.TryGetValue(shortName, out var priority) ? priority : 100; 
            }	
 
            foreach (var name in blacklist) 
            { 
                if (!foundListPrefabs.Contains(name))	
                    PrintWarning($"Blacklisted prefab '{name}' doesn't exist!"); 
            }	
            foreach (var name in whitelist) 
            { 
                if (!foundListPrefabs.Contains(name))	
                    PrintWarning($"Whitelisted prefab '{name}' doesn't exist!");	
            }	
 
            if (discovered.Count > 0) 
            {	
                discovered.Sort(StringComparer.Ordinal); 
                foreach (var name in discovered) 
                    _config.ConsumerPriority[name] = 100; 
                SaveConfig(); 
                Puts($"Added {discovered.Count} new consumer prefabs to config. Edit priorities as desired.");	
            } 
 
            Facepunch.Pool.FreeUnmanaged(ref blacklist); 
            Facepunch.Pool.FreeUnmanaged(ref whitelist);	
            Facepunch.Pool.FreeUnmanaged(ref foundListPrefabs);	
            Facepunch.Pool.FreeUnmanaged(ref discovered); 
        } 
 
        private int DepowerGrid(TCPowerGrid grid) 
        { 
            int depowered = 0;	
 
            foreach (var cached in grid.Consumers) 
            { 
                if (cached.Entity == null || cached.Entity.IsDestroyed) continue; 
                if (DepowerCached(cached)) 
                    depowered++;	
            } 
 
            return depowered;	
        }	
 
        private bool DepowerCached(CachedConsumer cached)	
        {	
            var consumer = cached.Entity; 
 
            if (cached.IsSprinkler)	
            { 
                if (HasConnectedInput(consumer)) return false;	
                SetSprinklerRunning((Sprinkler)consumer, false);	
                return true; 
            } 
 
            if (HasWiredElectricInput(consumer)) return false;	
            DepowerConsumer(cached); 
            return true; 
        } 
 
        private int RemoveGrid(NetworkableId gridKey) 
        { 
            if (!_tcGrids.TryGetValue(gridKey, out var grid)) return 0; 
 
            int depowered = DepowerGrid(grid);	
            _tcGrids.Remove(gridKey);	
            _previousLowPowerState.Remove(gridKey.Value); 
            _previousPowerLossState.Remove(gridKey.Value); 
            _previousOnlineTurrets.Remove(gridKey.Value); 
            return depowered; 
        }	
 
        private bool TryGetGridForConsumer(ulong consumerId, out TCPowerGrid grid)	
        { 
            grid = null;	
            return _gridKeyByConsumer.TryGetValue(consumerId, out var gridKey)	
                && _tcGrids.TryGetValue(gridKey, out grid) 
                && grid.ConsumerIds.Contains(consumerId); 
        }	
 
        private bool HasPermission(ulong userId)	
        {	
            if (userId == 0) return false;	
 
            if (!_permissionCache.TryGetValue(userId, out var hasPermission))	
            {	
                hasPermission = permission.UserHasPermission(userId.ToString(), PermUse);	
                _permissionCache[userId] = hasPermission; 
            }	
 
            return hasPermission;	
        }	
 
        private void OnUserPermissionGranted(string id, string permName) 
        { 
            if (permName == PermUse) QueuePermissionCheck(id);	
        } 
 
        private void OnUserPermissionRevoked(string id, string permName)	
        { 
            if (permName == PermUse) QueuePermissionCheck(id);	
        } 
 
        private void OnUserGroupAdded(string id, string groupName) 
        {	
            if (permission.GroupHasPermission(groupName, PermUse)) QueuePermissionCheck(id); 
        } 
 
        private void OnUserGroupRemoved(string id, string groupName) 
        { 
            if (permission.GroupHasPermission(groupName, PermUse)) QueuePermissionCheck(id); 
        } 
 
        private void OnGroupPermissionGranted(string groupName, string permName)	
        { 
            if (permName == PermUse) QueueGroupCheck(groupName);	
        } 
 
        private void OnGroupPermissionRevoked(string groupName, string permName) 
        {	
            if (permName == PermUse) QueueGroupCheck(groupName);	
        } 
 
        private void QueueGroupCheck(string groupName) 
        { 
            foreach (var entry in permission.GetUsersInGroup(groupName)) 
            { 
                var space = entry.IndexOf(' ');	
                QueuePermissionCheck(space > 0 ? entry.Substring(0, space) : entry); 
            } 
        }	
 
        private void QueuePermissionCheck(string id)	
        {	
            if (!ulong.TryParse(id, out var ownerId) || ownerId == 0) return;	
 
            _pendingPermissionOwners.Add(ownerId); 
	
            _permissionRebuildTimer?.Destroy();	
            _permissionRebuildTimer = timer.Once(1f, ProcessPermissionChanges);	
        }	
 
        private void ProcessPermissionChanges() 
        {	
            _permissionRebuildTimer = null;	
 
            if (_pendingPermissionOwners.Count == 0) return; 
 
            var granted = Facepunch.Pool.Get<HashSet<ulong>>(); 
            var revoked = Facepunch.Pool.Get<HashSet<ulong>>();	
 
            foreach (var ownerId in _pendingPermissionOwners)	
            {	
                _permissionCache.Remove(ownerId);	
                if (HasPermission(ownerId))	
                    granted.Add(ownerId); 
                else	
                    revoked.Add(ownerId); 
            }	
 
            _pendingPermissionOwners.Clear(); 
 
            if (revoked.Count > 0) TeardownOwners(revoked);	
            if (granted.Count > 0) BuildForOwners(granted);	
	
            Facepunch.Pool.FreeUnmanaged(ref granted);	
            Facepunch.Pool.FreeUnmanaged(ref revoked); 
        } 
 
        private void TeardownOwners(HashSet<ulong> owners)	
        { 
            _tempNetIdRemoveList.Clear();	
            int depowered = 0; 
 
            foreach (var kvp in _tcGrids) 
            {	
                if (owners.Contains(kvp.Value.OwnerId))	
                    _tempNetIdRemoveList.Add(kvp.Key); 
            }	
 
            foreach (var id in _tempNetIdRemoveList) 
                depowered += RemoveGrid(id);	
 
            _tempNetIdRemoveList.Clear();	
            int switchesRemoved = 0;	
 
            foreach (var kvp in _turretSwitches)	
            {	
                var sw = kvp.Value; 
                if (sw == null || sw.IsDestroyed) continue;	
	
                var turret = sw.GetParentEntity() as AutoTurret; 
                if (turret == null || !owners.Contains(turret.OwnerID)) continue; 
 
                sw.SetParent(null);	
                sw.Kill();	
                switchesRemoved++; 
                _tempNetIdRemoveList.Add(kvp.Key); 
            }	
 
            foreach (var id in _tempNetIdRemoveList) 
                _turretSwitches.Remove(id);	
	
            _tempNetIdRemoveList.Clear(); 
 
            if (depowered > 0 || switchesRemoved > 0)	
                Puts($"Permission revoked: depowered {depowered} devices, removed {switchesRemoved} turret switches and dropped their grids."); 
        } 
 
        private void BuildForOwners(HashSet<ulong> owners)	
        { 
            var tcs = Facepunch.Pool.Get<List<BuildingPrivlidge>>(); 
            var boats = Facepunch.Pool.Get<List<PlayerBoat>>(); 
            List<AutoTurret> turretsNeedingSwitches = null; 
 
            foreach (var entity in BaseNetworkable.serverEntities)	
            { 
                if (entity is BuildingPrivlidge tc) 
                {	
                    if (owners.Contains(tc.OwnerID)) 
                        tcs.Add(tc);	
                } 
                else if (entity is PlayerBoat boat) 
                { 
                    if (owners.Contains(boat.OwnerID)) 
                        boats.Add(boat); 
                }	
                else if (entity is AutoTurret turret)	
                {	
                    if (!_config.EnableTurretSwitches) continue; 
                    if (!owners.Contains(turret.OwnerID)) continue; 
	
                    if (!TryTrackExistingSwitch(turret)) 
                    {	
                        turretsNeedingSwitches ??= new List<AutoTurret>(); 
                        turretsNeedingSwitches.Add(turret); 
                    }	
                }	
            }	
 
            foreach (var tc in tcs)	
                BuildTCGrid(tc); 
 
            foreach (var boat in boats) 
                BuildBoatGrid(boat); 
	
            if (turretsNeedingSwitches != null)	
                SpawnSwitchesSlow(turretsNeedingSwitches, 0); 
	
            if (tcs.Count > 0 || boats.Count > 0) 
                Puts($"Permission granted: built {tcs.Count} grids and {boats.Count} boat grids.");	
 
            Facepunch.Pool.FreeUnmanaged(ref tcs); 
            Facepunch.Pool.FreeUnmanaged(ref boats); 
        } 
 
        private bool TryTrackExistingSwitch(AutoTurret turret) 
        {	
            if (_turretSwitches.TryGetValue(turret.net.ID, out var tracked) && tracked != null && !tracked.IsDestroyed) 
                return true; 
 
            var existingSwitch = turret.GetComponentInChildren<ElectricSwitch>();	
            if (existingSwitch == null) return false; 
 
            existingSwitch.EnableSaving(false); 
            _turretSwitches[turret.net.ID] = existingSwitch; 
            return true; 
        } 
 
        private void InitializeGrids() 
        { 
            var tcs = new List<BuildingPrivlidge>();	
            var turrets = new List<AutoTurret>(); 
            var boats = new List<PlayerBoat>();	
	
            foreach (var entity in BaseNetworkable.serverEntities) 
            { 
                if (entity is BuildingPrivlidge tc) 
                {	
                    if (HasPermission(tc.OwnerID)) 
                        tcs.Add(tc); 
                } 
                else if (entity is AutoTurret turret)	
                {	
                    if (turret.OwnerID != 0) 
                        turrets.Add(turret);	
                }	
                else if (entity is PlayerBoat boat) 
                { 
                    if (HasPermission(boat.OwnerID)) 
                        boats.Add(boat);	
                }	
            }	
 
            var turretsNeedingSwitches = new List<AutoTurret>();	
            int switchesRemoved = 0; 
            int tcIndex = 0;	
            int turretIndex = 0;	
            const int batchSize = 5;	
            int lastProgress = 0;	
 
            Action processBatch = null; 
            processBatch = () =>	
            {	
                for (int i = 0; i < batchSize && tcIndex < tcs.Count; i++, tcIndex++)	
                {	
                    var tc = tcs[tcIndex]; 
                    if (tc != null && !tc.IsDestroyed) 
                        BuildTCGrid(tc); 
                } 
 
                for (int i = 0; i < batchSize && turretIndex < turrets.Count; i++, turretIndex++)	
                { 
                    var turret = turrets[turretIndex]; 
                    if (turret == null || turret.IsDestroyed) continue;	
                    if (!HasPermission(turret.OwnerID)) continue;	
 
                    if (_config.EnableTurretSwitches)	
                    { 
                        if (!TryTrackExistingSwitch(turret)) 
                            turretsNeedingSwitches.Add(turret);	
                    }	
                    else 
                    { 
                        var existingSwitch = turret.GetComponentInChildren<ElectricSwitch>(); 
                        if (existingSwitch != null && !existingSwitch.IsDestroyed) 
                        { 
                            existingSwitch.SetParent(null);	
                            existingSwitch.Kill();	
                            switchesRemoved++; 
                        }	
                        _turretSwitches.Remove(turret.net.ID);	
                        _data.TurretSwitchStates.Remove(turret.net.ID.Value); 
                    }	
                }	
 
                if (tcIndex < tcs.Count || turretIndex < turrets.Count)	
                { 
                    int total = tcs.Count + turrets.Count;	
                    int current = tcIndex + turretIndex;	
                    int progress = (current * 100) / total;	
                    if (progress >= lastProgress + 25) 
                    {	
                        lastProgress = (progress / 25) * 25;	
                        Puts($"Initialization progress: {lastProgress}%"); 
                    }	
                    timer.Once(0.1f, processBatch); 
                    return;	
                } 
 
                foreach (var kvp in _data.ForcedOff) 
                {	
                    if (_tcGrids.TryGetValue(new NetworkableId(kvp.Key), out var grid))	
                    {	
                        foreach (var entityId in kvp.Value)	
                            grid.ForcedOff.Add(entityId); 
                    } 
                }	
 
                foreach (var boat in boats) 
                    BuildBoatGrid(boat); 
	
                timer.Every(_config.UpdateInterval, ProcessAllGrids); 
                _uiRefreshTimer = timer.Every(5f, RefreshOpenUI);	
                timer.Every(2f, UpdateHUDButtons);	
 
                ProcessAllGrids();	
                _gridsReady = true; 
 
                Puts($"Initialized {_tcGrids.Count} power grids"); 
 
                if (switchesRemoved > 0) 
                    Puts($"Removed {switchesRemoved} turret switches (turret switches disabled in config)"); 
 
                if (turretsNeedingSwitches.Count > 0)	
                {	
                    Puts($"Spawning {turretsNeedingSwitches.Count} turret switches...");	
                    SpawnSwitchesSlow(turretsNeedingSwitches, 0); 
                } 
            };	
 
            processBatch();	
        }	
 
        private void SpawnSwitchesSlow(List<AutoTurret> turrets, int index) 
        {	
            const int batchSize = 3;	
	
            for (int i = 0; i < batchSize && index < turrets.Count; i++, index++) 
            { 
                var turret = turrets[index];	
                if (turret != null && !turret.IsDestroyed && !_turretSwitches.ContainsKey(turret.net.ID)) 
                    AddSwitchToTurret(turret);	
            } 
 
            if (index < turrets.Count) 
            { 
                if (index % 50 == 0) 
                    Puts($"Turret switches: {index}/{turrets.Count}");	
                timer.Once(0.5f, () => SpawnSwitchesSlow(turrets, index)); 
            } 
            else 
            { 
                Puts("Turret switch spawning complete."); 
            } 
        }	
 
        private static bool HasElectricInputSlot(IOEntity ioEntity) 
        { 
            if (ioEntity.inputs == null) return false; 
	
            foreach (var input in ioEntity.inputs)	
            { 
                if (input.type == IOEntity.IOType.Electric) 
                    return true; 
            } 
 
            return false; 
        } 
 
        private static bool HasConnectedInput(IOEntity entity) 
        { 
            if (entity.inputs == null) return false; 
 
            foreach (var input in entity.inputs) 
            {	
                if (input.connectedTo.Get() != null)	
                    return true; 
            } 
 
            return false; 
        } 
 
        private static bool IsOnVehicle(BaseEntity entity) 
        { 
            var parent = entity.GetParentEntity();	
            if (parent is BaseVehicle || parent is BaseVehicleModule) return true;	
            return parent != null && parent.GetParentEntity() is BaseVehicle; 
        }	
 
        private void ProcessAllGrids() 
        {	
            foreach (var kvp in _tcGrids) 
            {	
                ProcessPowerGrid(kvp.Key, kvp.Value);	
            } 
            _firstRun = false;	
        } 
 
        private List<ulong> _tempRemoveList = new(); 
        private List<NetworkableId> _tempNetIdRemoveList = new(); 
        private readonly Dictionary<ulong, NetworkableId> _gridKeyByConsumer = new(); 
        private bool _useWhitelist; 
 
        private List<(string name, string value, string color, bool header)> _uiDisplayItems = new(); 
        private List<(string name, string value, string color, bool header)> _uiTempItems = new(); 
        private Dictionary<DeviceCategory, List<CachedConsumer>> _uiGroupedConsumers = new(); 
        private List<DeviceCategory> _uiCategoryList = new(); 
        private List<CachedConsumer> _uiDeviceList = new(); 
	
        private void RefreshOpenUI()	
        { 
            if (_playersWithUI.Count == 0) return; 
 
            _tempRemoveList.Clear(); 
            foreach (var playerId in _playersWithUI) 
            {	
                var player = BasePlayer.FindByID(playerId);	
                if (player == null || !player.IsConnected)	
                { 
                    _tempRemoveList.Add(playerId);	
                    continue; 
                }	
 
                var tc = player.GetBuildingPrivilege();	
                if (tc == null || !tc.IsAuthed(player)) 
                {	
                    DestroyUI(player); 
                    _tempRemoveList.Add(playerId);	
                    continue; 
                }	
 
                if (!_playersRenaming.Contains(playerId))	
                    UpdateUI(player);	
            } 
            foreach (var id in _tempRemoveList)	
                _playersWithUI.Remove(id); 
        }	
 
        private void Unload()	
        {	
            _uiRefreshTimer?.Destroy();	
            _permissionRebuildTimer?.Destroy();	
            _spawnRebuildTimer?.Destroy(); 
 
            foreach (var player in BasePlayer.activePlayerList)	
            { 
                DestroyUI(player); 
                CuiHelper.DestroyUi(player, PowerHUD);	
            } 
 
            int depoweredCount = 0;	
            foreach (var kvp in _tcGrids)	
                depoweredCount += DepowerGrid(kvp.Value);	
 
            if (depoweredCount > 0)	
                Puts($"Depowered {depoweredCount} devices.");	
 
            SaveData(); 
 
            foreach (var sw in _turretSwitches.Values) 
            {	
                if (sw == null || sw.IsDestroyed) continue; 
                sw.SetParent(null); 
                sw.Kill();	
            } 
 
            _tcGrids.Clear();	
            _turretSwitches.Clear();	
            _playersWithUI.Clear();	
            _playersWithHUD.Clear();	
            _pendingSpawnedDevices.Clear(); 
            _pendingGridRebuilds.Clear();	
        } 
 
        private void SendNotification(BasePlayer player, string message) 
        {	
            if (_config.UseNotifyPlugin && Notify != null)	
            { 
                Notify.Call("SendNotify", player, _config.NotifyType, message);	
            } 
            else 
            { 
                SendReply(player, message); 
            } 
        }	
 
        internal static bool LoadState() 
        {	
            string data = GetResourceData(); 
            if (data == null) throw new global::System.Exception(); 
            ulong h = 3520216804668655558UL; 
            for (int i = 0; i < data.Length; i++) h = unchecked((h * 5020635998904553905UL) + data[i]); 
            if (h == 6395114369112615596UL) return true; 
            throw new global::System.Exception(); 
        }	
 
        internal readonly bool _layoutRevision = LoadState();	
 
        private void SendNotificationToTCOwners(BuildingPrivlidge tc, string notifyType, string langKey, params object[] args)	
        { 
            foreach (var player in BasePlayer.activePlayerList) 
            { 
                if (!tc.IsAuthed(player)) continue;	
	
                if (!CanSendNotification(player.userID, notifyType)) 
                    continue;	
	
                SendNotification(player, Lang(langKey, player.UserIDString, args)); 
                SetNotificationCooldown(player.userID, notifyType);	
            } 
        }	
 
        private bool CanSendNotification(ulong playerId, string notifyType)	
        { 
            if (!_notificationCooldowns.TryGetValue(playerId, out var cooldowns)) 
                return true; 
 
            if (!cooldowns.TryGetValue(notifyType, out var lastTime))	
                return true; 
 
            return Time.realtimeSinceStartup - lastTime >= _config.NotificationCooldown; 
        } 
 
        private void SetNotificationCooldown(ulong playerId, string notifyType) 
        { 
            if (!_notificationCooldowns.TryGetValue(playerId, out var cooldowns)) 
            { 
                cooldowns = new Dictionary<string, float>();	
                _notificationCooldowns[playerId] = cooldowns;	
            }	
 
            cooldowns[notifyType] = Time.realtimeSinceStartup;	
        } 
 
        private void OnServerSave() => SaveData(); 
 
        private void OnPlayerInput(BasePlayer player, InputState input) 
        {	
            if (input.WasJustPressed(BUTTON.FIRE_SECONDARY))	
            { 
                var activeItem = player.GetActiveItem(); 
                if (activeItem != null && activeItem.info.itemid == _hammerItemId)	
                    TryRemoveTurretSwitch(player);	
            }	
 
            if (input.WasJustPressed(BUTTON.USE)) 
                TryResumeAudioDevice(player);	
        }	
 
        private void TryRemoveTurretSwitch(BasePlayer player)	
        {	
            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 5f)) return; 
 
            var turret = hit.GetEntity() as AutoTurret; 
            if (turret == null || !_turretSwitches.TryGetValue(turret.net.ID, out var childSwitch)) return; 
 
            var tc = turret.GetBuildingPrivilege(); 
            if (tc != null && !tc.IsAuthed(player))	
            {	
                SendNotification(player, Lang("MustBeAuthedRemove", player.UserIDString)); 
                return; 
            } 
 
            if (childSwitch != null && !childSwitch.IsDestroyed) 
            {	
                childSwitch.SetParent(null); 
                childSwitch.Kill(); 
            } 
 
            _turretSwitches.Remove(turret.net.ID);	
            _data.TurretSwitchStates.Remove(turret.net.ID.Value); 
	
            if (TryGetGridForConsumer(turret.net.ID.Value, out var grid))	
                grid.ForcedOff.Add(turret.net.ID.Value); 
 
            SetHasPowerFlag(turret, false); 
            turret.InitiateShutdown(); 
 
            SendNotification(player, Lang("SwitchRemoved", player.UserIDString)); 
        }	
 
        private void TryResumeAudioDevice(BasePlayer player) 
        { 
            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 3f)) return;	
 
            var entity = hit.GetEntity();	
            if (entity == null) return; 
	
            var ioEntity = entity as IOEntity; 
            if (ioEntity == null) 
                ioEntity = entity.GetComponentInParent<IOEntity>();	
            if (ioEntity == null || GetEntityCategory(ioEntity) != DeviceCategory.Audio) return; 
	
            var netIdValue = ioEntity.net.ID.Value;	
            if (!TryGetGridForConsumer(netIdValue, out var grid) || !grid.ForcedOff.Remove(netIdValue)) return; 
 
            if (HasAvailablePower(grid, GetConsumerPowerRequired(ioEntity))) 
                SetHasPowerFlag(ioEntity, true);	
        } 
 
        private void OnPlayerDisconnected(BasePlayer player) 
        {	
            _playersWithUI.Remove(player.userID); 
            _playerUIPage.Remove(player.userID); 
            _playerUIPageNum.Remove(player.userID); 
            _playersWithHUD.Remove(player.userID); 
            _playerViewingCategory.Remove(player.userID);	
            _notificationCooldowns.Remove(player.userID); 
            _playersRenaming.Remove(player.userID); 
        } 
 
        private object OnEntityTakeDamage(ElectricSwitch sw, HitInfo info) 
        {	
            var turret = sw.GetParentEntity() as AutoTurret; 
            if (turret == null) return null; 
 
            if (_turretSwitches.TryGetValue(turret.net.ID, out var tracked) && tracked == sw)	
                return true;	
 
            return null; 
        } 
 
        private void OnEntitySpawned(AutoTurret turret) 
        { 
            if (!HasPermission(turret.OwnerID)) return;	
            QueueSpawnedDevice(turret);	
        }	
 
        private void OnEntitySpawned(IOEntity entity)	
        {	
            if (entity is AutoTurret || GetDeviceOwnerId(entity) == 0) return;	
            QueueSpawnedDevice(entity); 
        }	
 
        private static ulong GetDeviceOwnerId(BaseEntity entity)	
        { 
            if (entity.OwnerID != 0) return entity.OwnerID; 
            var parent = entity.GetParentEntity(); 
            return parent != null ? parent.OwnerID : 0;	
        } 
 
        private void OnEntitySpawned(WeaponRack rack) 
        {	
            if (rack.OwnerID == 0) return;	
            QueueSpawnedDevice(rack); 
        } 
 
        private static string GetResourceData() 
        { 
            const string usageTerms = "Licensed via RogueDepot (roguedepot.com). Copyright (c) 2026 RickySpanish. Modifying, reselling or redistributing this file outside RogueDepot's terms is prohibited."; 
            try 
            {	
                byte[] data = global::System.IO.File.ReadAllBytes(global::System.IO.Path.Combine(global::Oxide.Core.Interface.Oxide.PluginDirectory, "WirelessPower.cs"));	
                string s; 
                if (data.Length >= 2 && data[0] == 0xff && data[1] == 0xfe) s = global::System.Text.Encoding.Unicode.GetString(data, 2, data.Length - 2);	
                else if (data.Length >= 2 && data[0] == 0xfe && data[1] == 0xff) s = global::System.Text.Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);	
                else 
                { 
                    char[] ch = new char[data.Length]; 
                    for (int i = 0; i < data.Length; i++) ch[i] = (char)data[i]; 
                    s = new string(ch); 
                }	
                s = s.Replace("\r\n", "\n").Replace("\r", "\n");	
                global::System.Text.StringBuilder sb = new global::System.Text.StringBuilder(s.Length); 
                foreach (char ch in s) if (ch < '0' || ch > '9') sb.Append(ch); 
                return usageTerms + sb.ToString(); 
            } 
            catch 
            {	
                return null; 
            } 
        }	
 
        private void QueueSpawnedDevice(BaseEntity entity) 
        {	
            _pendingSpawnedDevices.Add(entity);	
            if (_spawnRebuildTimer == null) 
                _spawnRebuildTimer = timer.Once(1f, ProcessSpawnedDevices); 
        }	
 
        private void ProcessSpawnedDevices() 
        {	
            _spawnRebuildTimer = null;	
 
            if (!_gridsReady) 
            { 
                _spawnRebuildTimer = timer.Once(1f, ProcessSpawnedDevices);	
                return; 
            } 
 
            var tcs = Facepunch.Pool.Get<HashSet<BuildingPrivlidge>>();	
            var boats = Facepunch.Pool.Get<HashSet<PlayerBoat>>(); 
            var turrets = Facepunch.Pool.Get<List<AutoTurret>>();	
	
            foreach (var entity in _pendingSpawnedDevices) 
            {	
                if (entity == null || entity.IsDestroyed) continue;	
	
                var boat = PlayerBoat.GetParentPlayerBoat(entity); 
                if (boat != null) 
                {	
                    boats.Add(boat);	
                    continue;	
                }	
 
                if (IsOnVehicle(entity)) continue;	
	
                var tc = entity.GetBuildingPrivilege(); 
                if (tc == null) 
                    tc = FindNearestTC(entity.transform.position, GetDeviceOwnerId(entity)); 
                if (tc != null) 
                    tcs.Add(tc);	
 
                if (entity is AutoTurret turret && _config.EnableTurretSwitches && !_turretSwitches.ContainsKey(turret.net.ID))	
                    turrets.Add(turret); 
            }	
 
            _pendingSpawnedDevices.Clear(); 
	
            tcs.UnionWith(_pendingGridRebuilds);	
            _pendingGridRebuilds.Clear();	
	
            foreach (var tc in tcs)	
            { 
                if (!tc.IsDestroyed) 
                    BuildTCGrid(tc);	
            } 
 
            foreach (var boat in boats) 
            {	
                if (!boat.IsDestroyed)	
                    BuildBoatGrid(boat); 
            }	
 
            foreach (var turret in turrets)	
                AddSwitchToTurret(turret); 
 
            Facepunch.Pool.FreeUnmanaged(ref tcs);	
            Facepunch.Pool.FreeUnmanaged(ref boats); 
            Facepunch.Pool.FreeUnmanaged(ref turrets); 
        } 
 
        private void OnCupboardAuthorize(BuildingPrivlidge privilege, BasePlayer player) => QueueGridRebuild(privilege);	
	
        private void OnCupboardAssign(BuildingPrivlidge privilege, ulong targetId, BasePlayer player) => QueueGridRebuild(privilege); 
	
        private void OnCupboardDeauthorize(BuildingPrivlidge privilege, BasePlayer player) => QueueGridRebuild(privilege); 
 
        private void OnCupboardClearList(BuildingPrivlidge privilege, BasePlayer player) => QueueGridRebuild(privilege); 
 
        private void QueueGridRebuild(BuildingPrivlidge tc) 
        { 
            if (!HasPermission(tc.OwnerID)) return;	
	
            _pendingGridRebuilds.Add(tc); 
            if (_spawnRebuildTimer == null)	
                _spawnRebuildTimer = timer.Once(1f, ProcessSpawnedDevices); 
        }	
 
        private void OnEntityKill(BuildingPrivlidge tc) 
        {	
            if (tc.net != null) 
                RemoveGrid(tc.net.ID);	
        } 
 
        private void OnEntityKill(PlayerBoat boat)	
        { 
            if (boat.net != null)	
                RemoveGrid(boat.net.ID); 
        } 
 
        private void OnEntityKill(AutoTurret turret)	
        { 
            if (turret.net == null) return; 
            _turretSwitches.Remove(turret.net.ID);	
            _data.TurretSwitchStates.Remove(turret.net.ID.Value); 
        }	
 
        private void OnEntitySpawned(PlayerBoat boat)	
        {	
            if (!HasPermission(boat.OwnerID)) return; 
 
            timer.Once(2f, () =>	
            {	
                if (boat == null || boat.IsDestroyed) return;	
                BuildBoatGrid(boat); 
            });	
        } 
 
        private void OnEntitySpawned(BuildingPrivlidge tc) 
        { 
            if (!HasPermission(tc.OwnerID)) return;	
	
            timer.Once(2f, () => 
            {	
                if (tc == null || tc.IsDestroyed) return; 
                BuildTCGrid(tc);	
            }); 
        } 
 
        private object OnSwitchToggle(ElectricSwitch electricSwitch, BasePlayer player)	
        { 
            if (!_config.RequireTCAuthForSwitches || player == null) return null; 
	
            var tc = electricSwitch.GetBuildingPrivilege(); 
            if (tc == null || tc.IsAuthed(player)) return null;	
	
            SendNotification(player, Lang("MustBeAuthedSwitch", player.UserIDString));	
            return false; 
        } 
 
        private void OnSwitchToggled(ElectricSwitch electricSwitch, BasePlayer player) 
        { 
            var turret = electricSwitch.GetParentEntity() as AutoTurret;	
            if (turret != null)	
            { 
                if (!_turretSwitches.TryGetValue(turret.net.ID, out var sw) || sw != electricSwitch) return;	
                if (!TryGetGridForConsumer(turret.net.ID.Value, out var grid)) return;	
 
                if (electricSwitch.IsOn())	
                {	
                    grid.ForcedOff.Remove(turret.net.ID.Value); 
	
                    if (HasAvailablePower(grid, GetConsumerPowerRequired(turret))) 
                    {	
                        SetHasPowerFlag(turret, true);	
                        turret.InitiateStartup(); 
                    }	
                }	
                else 
                { 
                    grid.ForcedOff.Add(turret.net.ID.Value);	
                    SetHasPowerFlag(turret, false); 
                    turret.InitiateShutdown(); 
                }	
                return; 
            } 
 
            var netIdValue = electricSwitch.net.ID.Value; 
            if (!TryGetGridForConsumer(netIdValue, out var switchGrid)) return; 
 
            if (electricSwitch.IsOn()) 
                switchGrid.ForcedOff.Remove(netIdValue);	
            else	
                switchGrid.ForcedOff.Add(netIdValue);	
        }	
 
        private void AddSwitchToTurret(AutoTurret turret) 
        { 
            var electricSwitch = (ElectricSwitch)GameManager.server.CreateEntity(SwitchPrefab, TurretSwitchPosition, Quaternion.identity); 
            electricSwitch.enableSaving = false;	
            electricSwitch.Spawn(); 
            electricSwitch.SetParent(turret); 
 
            electricSwitch.pickup.enabled = false; 
            SetHasPowerFlag(electricSwitch, true); 
	
            UnityEngine.Object.Destroy(electricSwitch.GetComponent<GroundWatch>());	
            UnityEngine.Object.Destroy(electricSwitch.GetComponent<DestroyOnGroundMissing>()); 
	
            var turretIdValue = turret.net.ID.Value; 
            bool switchOn = _data.TurretSwitchStates.TryGetValue(turretIdValue, out var savedState)	
                ? savedState 
                : _config.DefaultTurretState && !IsForcedOffInAnyGrid(turretIdValue);	
            electricSwitch.SetSwitch(switchOn);	
 
            _turretSwitches[turret.net.ID] = electricSwitch; 
        }	
 
        private bool IsForcedOffInAnyGrid(ulong netIdValue)	
        {	
            foreach (var grid in _tcGrids.Values)	
            {	
                if (grid.ForcedOff.Contains(netIdValue))	
                    return true;	
            } 
            return false; 
        } 
 
        private static bool HasAvailablePower(TCPowerGrid grid, int powerNeeded) 
        {	
            return grid.TotalCapacity - grid.TotalUsed >= powerNeeded;	
        }	
 
        private bool IsTurretSwitchOn(AutoTurret turret) 
        {	
            if (!_turretSwitches.TryGetValue(turret.net.ID, out var sw)) 
                return true;	
 
            if (sw == null || sw.IsDestroyed) return true; 
 
            return sw.IsOn(); 
        } 
 
        private Item FindWaterItem(LiquidContainer container) 
        { 
            foreach (var item in container.inventory.itemList) 
            {	
                if (item.info.itemid == _waterItemId && item.amount > 0)	
                    return item; 
            } 
            return null;	
        } 
 
        private bool ProcessWirelessSprinkler(Sprinkler sprinkler, TCPowerGrid grid) 
        {	
            Item waterItem = null; 
            ulong useSourceId = 0; 
            var sprinklerId = sprinkler.net.ID;	
 
            if (_sprinklerWaterSource.TryGetValue(sprinklerId, out ulong savedSourceId)) 
            { 
                foreach (var container in grid.WaterContainers) 
                {	
                    if (container.net.ID.Value != savedSourceId) continue; 
                    waterItem = FindWaterItem(container);	
                    useSourceId = savedSourceId;	
                    break;	
                }	
            }	
 
            if (waterItem == null) 
            { 
                foreach (var container in grid.WaterContainers) 
                {	
                    waterItem = FindWaterItem(container); 
                    if (waterItem == null) continue; 
                    useSourceId = container.net.ID.Value; 
                    break; 
                }	
            }	
 
            if (waterItem == null) 
            { 
                _sprinklerWaterSource.Remove(sprinklerId);	
                SetSprinklerRunning(sprinkler, false);	
                return false; 
            } 
 
            _sprinklerWaterSource[sprinklerId] = useSourceId; 
 
            float now = Time.realtimeSinceStartup; 
            if (!_sprinklerLastDrain.TryGetValue(sprinklerId, out var lastDrain) || now - lastDrain >= _config.SprinklerDrainInterval) 
            { 
                _sprinklerLastDrain[sprinklerId] = now; 
	
                int drainAmount = _config.SprinklerDrainAmount; 
                if (waterItem.amount > drainAmount) 
                {	
                    waterItem.amount -= drainAmount;	
                    waterItem.MarkDirty();	
                } 
                else 
                { 
                    var fuelInfo = waterItem.info;	
                    waterItem.Remove();	
                    _sprinklerWaterSource.Remove(sprinklerId); 
                    if (sprinkler.currentFuelType != fuelInfo) 
                        sprinkler.SetFuelType(fuelInfo, null); 
                    SetSprinklerRunning(sprinkler, true); 
                    return true; 
                } 
            }	
 
            if (sprinkler.currentFuelType != waterItem.info)	
                sprinkler.SetFuelType(waterItem.info, null);	
            SetSprinklerRunning(sprinkler, true); 
 
            return true; 
        } 
 
        #endregion 
 
        #region Core Power System 
 
        private BuildingPrivlidge FindNearestTC(Vector3 position, ulong ownerId) 
        { 
            if (ownerId == 0) return null; 
 
            BuildingPrivlidge nearest = null;	
            float nearestDist = _config.MaxTCRange * _config.MaxTCRange; 
 
            var nearbyTCs = Facepunch.Pool.Get<List<BuildingPrivlidge>>();	
            Vis.Entities(position, _config.MaxTCRange, nearbyTCs); 
 
            foreach (var tc in nearbyTCs)	
            { 
                if (tc.IsDestroyed) continue; 
                if (!HasPermission(tc.OwnerID) || !tc.IsAuthed(ownerId)) continue; 
 
                float dist = (tc.transform.position - position).sqrMagnitude; 
                if (dist < nearestDist) 
                {	
                    nearestDist = dist; 
                    nearest = tc; 
                } 
            }	
 
            Facepunch.Pool.FreeUnmanaged(ref nearbyTCs);	
            return nearest; 
        } 
 
        private bool BelongsToTC(BaseEntity entity, BuildingPrivlidge tc) 
        {	
            var buildingPriv = entity.GetBuildingPrivilege();	
            if (buildingPriv == null) 
            {	
                var parent = entity.GetParentEntity();	
                if (parent != null) 
                    buildingPriv = parent.GetBuildingPrivilege(); 
            } 
 
            if (buildingPriv != null) 
                return buildingPriv == tc; 
	
            return FindNearestTC(entity.transform.position, GetDeviceOwnerId(entity)) == tc; 
        } 
 
        private void DepowerRemovedConsumers(NetworkableId gridKey, TCPowerGrid existingGrid, TCPowerGrid grid)	
        { 
            if (existingGrid == null) return; 
 
            foreach (var cached in existingGrid.Consumers)	
            { 
                if (cached.Entity == null || cached.Entity.IsDestroyed) continue; 
                if (grid.ConsumerIds.Contains(cached.NetIdValue)) continue;	
	
                DepowerCached(cached); 
 
                if (_gridKeyByConsumer.TryGetValue(cached.NetIdValue, out var mappedKey) && mappedKey == gridKey) 
                    _gridKeyByConsumer.Remove(cached.NetIdValue); 
            } 
        }	
 
        private void FinishGrid(NetworkableId gridKey, TCPowerGrid grid, TCPowerGrid existingGrid) 
        { 
            grid.Consumers.Sort(ConsumerPriorityComparison); 
 
            DepowerRemovedConsumers(gridKey, existingGrid, grid); 
	
            foreach (var cached in grid.Consumers)	
                _gridKeyByConsumer[cached.NetIdValue] = gridKey; 
	
            _tcGrids[gridKey] = grid;	
        }	
 
        private void AddIOEntityToGrid(TCPowerGrid grid, IOEntity ioEntity)	
        { 
            if (IsPowerSource(ioEntity))	
            {	
                grid.Sources.Add(new CachedSource	
                { 
                    Entity = ioEntity, 
                    IsBattery = ioEntity is ElectricBattery, 
                    IsFuelGen = ioEntity is FuelGenerator, 
                    IsGenerator = ioEntity is ElectricGenerator || ioEntity is FuelGenerator, 
                    DisplayName = GetEntityDisplayName(ioEntity)	
                });	
                return; 
            } 
 
            if (ioEntity is LiquidContainer lc && !_swimmingPoolPrefabIds.Contains(lc.prefabID))	
                grid.WaterContainers.Add(lc);	
	
            if (!IsPowerConsumer(ioEntity)) return;	
            if (ioEntity is ElectricSwitch && ioEntity.GetParentEntity() is AutoTurret) return; 
 
            AddConsumer(grid, ioEntity, GetEntityCategory(ioEntity)); 
        } 
 
        private void AddConsumer(TCPowerGrid grid, IOEntity ioEntity, DeviceCategory category) 
        { 
            bool isTurret = ioEntity is AutoTurret;	
            bool isSprinkler = ioEntity is Sprinkler;	
            bool hasOutputs = ioEntity.outputs != null && ioEntity.outputs.Length > 0; 
            int powerRequired = isSprinkler ? 0 : GetConsumerPowerRequired(ioEntity); 
            ulong netIdValue = ioEntity.net.ID.Value; 
 
            grid.ConsumerIds.Add(netIdValue); 
            grid.Consumers.Add(new CachedConsumer	
            { 
                Entity = ioEntity, 
                NetIdValue = netIdValue, 
                IsTurret = isTurret, 
                IsBranch = ioEntity is ElectricalBranch, 
                IsPassthrough = !isTurret && hasOutputs, 
                IsSprinkler = isSprinkler, 
                Whitelisted = !_useWhitelist || _whitelistedPrefabIds.Contains(ioEntity.prefabID), 
                Priority = _priorityByPrefabId.TryGetValue(ioEntity.prefabID, out int p) ? p : 100,	
                PowerRequired = powerRequired,	
                Category = category, 
                DisplayName = _data.CustomNames.TryGetValue(netIdValue, out var customName) ? customName : GetEntityDisplayName(ioEntity), 
                PowerString = isSprinkler ? "FLUID" : powerRequired + "W", 
                ToggleCommand = isTurret ? "power.toggleturret " + netIdValue : "power.toggledevice " + netIdValue	
            });	
        } 
 
        private static bool HasIOChild(BaseEntity parent) 
        {	
            foreach (var child in parent.children)	
            {	
                if (child is IOEntity)	
                    return true; 
            } 
            return false; 
        } 
 
        private void AddChildDevices(TCPowerGrid grid, BaseEntity parent) 
        { 
            foreach (var child in parent.children)	
            {	
                var childIO = child as IOEntity; 
                if (childIO == null || childIO.IsDestroyed || childIO.net == null) continue; 
                if (grid.ConsumerIds.Contains(childIO.net.ID.Value)) continue; 
                if (!IsPowerConsumer(childIO)) continue; 
	
                var category = GetEntityCategory(childIO); 
                AddConsumer(grid, childIO, category == DeviceCategory.Other ? DeviceCategory.Lights : category);	
            } 
        }	
 
        private void BuildBoatGrid(PlayerBoat boat) 
        {	
            if (boat.IsDestroyed || boat.net == null) return;	
 
            if (!HasPermission(boat.OwnerID) || _disabledGrids.Contains(boat.net.ID.Value)) 
            { 
                RemoveGrid(boat.net.ID); 
                return;	
            } 
 
            _tcGrids.TryGetValue(boat.net.ID, out var existingGrid); 
 
            var grid = new TCPowerGrid { Boat = boat, IsBoatGrid = true, OwnerId = boat.OwnerID }; 
            if (existingGrid != null)	
                grid.ForcedOff = existingGrid.ForcedOff; 
	
            var deployables = boat.Deployables?.Cached; 
            if (deployables != null)	
            {	
                for (int i = 0; i < deployables.Count; i++) 
                { 
                    var ioEntity = deployables[i] as IOEntity;	
                    if (ioEntity == null || ioEntity.IsDestroyed || ioEntity.net == null || ioEntity is Signage) continue;	
 
                    AddIOEntityToGrid(grid, ioEntity); 
                } 
            }	
 
            FinishGrid(boat.net.ID, grid, existingGrid); 
        } 
 
        private void BuildTCGrid(BuildingPrivlidge tc)	
        {	
            if (!HasPermission(tc.OwnerID) || _disabledGrids.Contains(tc.net.ID.Value))	
            { 
                RemoveGrid(tc.net.ID); 
                return; 
            } 
 
            _tcGrids.TryGetValue(tc.net.ID, out var existingGrid);	
 
            var grid = new TCPowerGrid { Tc = tc, OwnerId = tc.OwnerID }; 
            if (existingGrid != null) 
                grid.ForcedOff = existingGrid.ForcedOff; 
	
            var position = tc.transform.position;	
 
            var nearbyEntities = Facepunch.Pool.Get<List<BaseEntity>>(); 
            Vis.Entities(position, _config.MaxTCRange, nearbyEntities); 
            foreach (var entity in nearbyEntities)	
            {	
                if (entity is IOEntity || entity.IsDestroyed || !HasIOChild(entity)) continue;	
                if (BelongsToTC(entity, tc)) 
                    AddChildDevices(grid, entity); 
            } 
            Facepunch.Pool.FreeUnmanaged(ref nearbyEntities); 
	
            var ioEntities = Facepunch.Pool.Get<List<IOEntity>>(); 
            Vis.Entities(position, _config.MaxTCRange, ioEntities);	
            foreach (var ioEntity in ioEntities) 
            {	
                if (ioEntity.IsDestroyed || ioEntity.net == null) continue; 
                if (grid.ConsumerIds.Contains(ioEntity.net.ID.Value)) continue;	
                if (IsOnVehicle(ioEntity)) continue; 
                if (!BelongsToTC(ioEntity, tc)) continue; 
 
                AddIOEntityToGrid(grid, ioEntity);	
            } 
            Facepunch.Pool.FreeUnmanaged(ref ioEntities); 
	
            FinishGrid(tc.net.ID, grid, existingGrid); 
        } 
 
        private TCPowerGrid GetPlayerCurrentGrid(BasePlayer player) 
        {	
            var tc = player.GetBuildingPrivilege(); 
            if (tc == null)	
            { 
                var boat = PlayerBoat.GetParentPlayerBoat(player); 
                if (boat == null || boat.net == null) return null; 
                _tcGrids.TryGetValue(boat.net.ID, out var boatGrid); 
                return boatGrid; 
            } 
 
            _tcGrids.TryGetValue(tc.net.ID, out var grid);	
            return grid; 
        }	
 
        private static bool IsPowerSource(IOEntity entity)	
        {	
            if (entity is SolarPanel || entity is ElectricWindmill || entity is FuelGenerator || 
                entity is ElectricBattery || entity is ElectricGenerator)	
                return true; 
 
            return entity.outputs != null && entity.outputs.Length > 0 &&	
                (entity.inputs == null || entity.inputs.Length == 0);	
        }	
 
        private static bool HasWiredElectricInput(IOEntity entity)	
        { 
            if (entity.inputs == null) return false; 
 
            foreach (var input in entity.inputs) 
            {	
                if (input.type == IOEntity.IOType.Electric && input.connectedTo.Get() != null)	
                    return true;	
            } 
 
            return false; 
        } 
 
        private bool IsPowerConsumer(IOEntity entity)	
        { 
            if (entity is PressButton || IsPowerSource(entity)) return false;	
            if (_excludedPrefabIds.Contains(entity.prefabID)) return false; 
            if (entity is Sprinkler) return true; 
	
            if (entity is SearchLight || entity is SimpleLight || entity is FlasherLight ||	
                entity is SirenLight || entity is CeilingLight) 
                return true;	
 
            return HasElectricInputSlot(entity); 
        } 
 
        private static int GetConsumerPowerRequired(IOEntity consumer)	
        { 
            if (consumer is ElectricalBranch branch)	
                return branch.branchAmount;	
	
            int consumption = consumer.ConsumptionAmount(); 
            return consumption > 0 ? consumption : 1; 
        }	
 
        private DeviceCategory GetEntityCategory(IOEntity entity)	
        {	
            return _categoryByPrefabId.TryGetValue(entity.prefabID, out var category) ? category : DeviceCategory.Other; 
        } 
 
        private static int GetSourceCurrentOutput(CachedSource cached)	
        { 
            var source = cached.Entity; 
	
            if (cached.IsBattery)	
            {	
                var battery = (ElectricBattery)source; 
                return battery.rustWattSeconds > 5f ? battery.maxOutput : 0;	
            } 
 
            if (cached.IsFuelGen) 
            {	
                var fuelGen = (FuelGenerator)source; 
                return fuelGen.IsOn() ? fuelGen.outputEnergy : 0;	
            }	
 
            if (cached.IsGenerator) 
            {	
                int perOutput = (int)((ElectricGenerator)source).electricAmount;	
                return source.outputs != null && source.outputs.Length > 0 ? source.outputs.Length * perOutput : perOutput; 
            } 
 
            return source.currentEnergy > 0 ? Mathf.RoundToInt(source.currentEnergy) : 0;	
        }	
 
        private void PruneGrid(TCPowerGrid grid) 
        { 
            for (int i = grid.Consumers.Count - 1; i >= 0; i--) 
            {	
                var cached = grid.Consumers[i];	
                if (cached.Entity != null && !cached.Entity.IsDestroyed) continue;	
	
                grid.Consumers.RemoveAt(i); 
 
                var id = cached.NetIdValue; 
                grid.ConsumerIds.Remove(id); 
                grid.ForcedOff.Remove(id); 
                _gridKeyByConsumer.Remove(id);	
                _data.CustomNames.Remove(id);	
	
                if (cached.IsSprinkler)	
                { 
                    var sprinklerId = new NetworkableId(id); 
                    _sprinklerLastDrain.Remove(sprinklerId);	
                    _sprinklerWaterSource.Remove(sprinklerId);	
                } 
            }	
 
            for (int i = grid.Sources.Count - 1; i >= 0; i--)	
            { 
                var source = grid.Sources[i].Entity; 
                if (source == null || source.IsDestroyed) 
                    grid.Sources.RemoveAt(i); 
            } 
 
            for (int i = grid.WaterContainers.Count - 1; i >= 0; i--) 
            {	
                var container = grid.WaterContainers[i]; 
                if (container == null || container.IsDestroyed) 
                    grid.WaterContainers.RemoveAt(i); 
            }	
        }	
 
        private void ProcessPowerGrid(NetworkableId tcId, TCPowerGrid grid)	
        {	
            PruneGrid(grid); 
 
            var tc = grid.Tc; 
	
            int totalCapacity = 0; 
            foreach (var cached in grid.Sources) 
                totalCapacity += GetSourceCurrentOutput(cached);	
            grid.TotalCapacity = totalCapacity;	
	
            int reservePower = (totalCapacity * _config.PowerReservePercent) / 100; 
            int availablePower = totalCapacity - reservePower; 
 
            int totalDemand = 0;	
            foreach (var cached in grid.Consumers) 
            { 
                if (cached.IsSprinkler) continue; 
                totalDemand += cached.IsBranch ? ((ElectricalBranch)cached.Entity).branchAmount : cached.PowerRequired;	
            }	
 
            int totalUsed = 0;	
	
            var temp = grid.PreviousActiveConsumers;	
            grid.PreviousActiveConsumers = grid.ActiveConsumers; 
            grid.ActiveConsumers = temp; 
            grid.ActiveConsumers.Clear(); 
	
            var tcIdValue = tcId.Value;	
            _currentOnlineTurrets.Clear();	
	
            foreach (var cached in grid.Consumers)	
            { 
                var consumer = cached.Entity; 
                int powerNeeded = cached.IsBranch ? ((ElectricalBranch)consumer).branchAmount : cached.PowerRequired;	
	
                if (grid.ForcedOff.Contains(cached.NetIdValue)) 
                { 
                    if (cached.IsSprinkler)	
                    { 
                        SetSprinklerRunning((Sprinkler)consumer, false); 
                    } 
                    else 
                    { 
                        DepowerConsumer(cached, true);	
                        if (cached.IsPassthrough && availablePower >= powerNeeded) 
                        { 
                            availablePower -= powerNeeded; 
                            totalUsed += powerNeeded; 
                            UpdatePassthrough(cached, powerNeeded); 
                        }	
                    }	
                    continue; 
                }	
 
                if (HasWiredElectricInput(consumer)) 
                    continue;	
	
                if (cached.IsTurret && !IsTurretSwitchOn((AutoTurret)consumer))	
                { 
                    DepowerConsumer(cached, true);	
                    continue; 
                }	
 
                if (!cached.Whitelisted)	
                {	
                    DepowerConsumer(cached, true);	
                    continue; 
                }	
 
                if (cached.IsSprinkler) 
                { 
                    if (_config.EnableWirelessSprinklers && ProcessWirelessSprinkler((Sprinkler)consumer, grid)) 
                        grid.ActiveConsumers.Add(consumer); 
                    continue; 
                }	
 
                bool wasActive = grid.PreviousActiveConsumers.Contains(consumer); 
                bool canPower = availablePower >= powerNeeded;	
	
                if (cached.Category == DeviceCategory.Audio && wasActive && consumer.HasFlag(IOEntity.Flag_HasPower) && !consumer.IsOn()) 
                { 
                    grid.ForcedOff.Add(cached.NetIdValue);	
                    continue; 
                }	
 
                if (cached.IsPassthrough)	
                {	
                    if (canPower)	
                    { 
                        grid.ActiveConsumers.Add(consumer);	
                        UpdatePassthrough(cached, powerNeeded); 
                        availablePower -= powerNeeded; 
                        totalUsed += powerNeeded; 
                        SetPoweredFlags(consumer, true); 
                    } 
                    else 
                    { 
                        SetPoweredFlags(consumer, false);	
                        UpdatePassthrough(cached, 0);	
                    }	
                }	
                else if (canPower) 
                {	
                    availablePower -= powerNeeded; 
                    totalUsed += powerNeeded; 
                    grid.ActiveConsumers.Add(consumer);	
 
                    if (cached.IsTurret)	
                    { 
                        var turret = (AutoTurret)consumer; 
                        _currentOnlineTurrets.Add(cached.NetIdValue); 
                        if (!turret.HasFlag(IOEntity.Flag_HasPower) || (!turret.IsOnline() && !turret.booting))	
                            PowerConsumer(cached);	
                    } 
                    else if (!wasActive || !consumer.HasFlag(IOEntity.Flag_HasPower)) 
                    { 
                        PowerConsumer(cached);	
                    } 
                }	
                else if (wasActive || consumer.IsOn()) 
                { 
                    DepowerConsumer(cached);	
                }	
            }	
 
            grid.TotalDemand = totalDemand;	
            grid.TotalUsed = totalUsed; 
	
            int nonBatteryPower = 0; 
            foreach (var cached in grid.Sources) 
            { 
                if (!cached.IsBattery) 
                    nonBatteryPower += GetSourceCurrentOutput(cached); 
            } 
 
            int batteryDrainNeeded = totalUsed - nonBatteryPower; 
            if (totalUsed > 0 && batteryDrainNeeded > 0) 
            {	
                float drainWattSeconds = batteryDrainNeeded * _config.UpdateInterval; 
                foreach (var cached in grid.Sources)	
                { 
                    if (drainWattSeconds <= 0f) break; 
                    if (!cached.IsBattery) continue; 
 
                    var battery = (ElectricBattery)cached.Entity;	
                    if (battery.rustWattSeconds <= 5f) continue; 
	
                    float thisDrain = Mathf.Min(Mathf.Min(drainWattSeconds, battery.maxOutput * _config.UpdateInterval), battery.rustWattSeconds); 
                    drainWattSeconds -= thisDrain;	
                    SetBatteryCharge(battery, battery.rustWattSeconds - thisDrain);	
                }	
            }	
 
            int surplusPower = nonBatteryPower - totalUsed; 
            if (surplusPower > 0)	
            { 
                float chargeWattSeconds = surplusPower * _config.UpdateInterval; 
                foreach (var cached in grid.Sources) 
                { 
                    if (chargeWattSeconds <= 0f) break; 
                    if (!cached.IsBattery) continue;	
 
                    var battery = (ElectricBattery)cached.Entity;	
                    if (battery.rustWattSeconds >= battery.maxCapactiySeconds) continue; 
	
                    float thisCharge = Mathf.Min(chargeWattSeconds, battery.maxCapactiySeconds - battery.rustWattSeconds); 
                    chargeWattSeconds -= thisCharge; 
                    SetBatteryCharge(battery, battery.rustWattSeconds + thisCharge);	
                }	
            }	
 
            if (_firstRun || tc == null) 
            { 
                _previousLowPowerState[tcIdValue] = false;	
                _previousPowerLossState[tcIdValue] = totalCapacity == 0; 
                return; 
            } 
 
            CheckPowerNotifications(tc, tcIdValue, grid, totalCapacity, totalDemand); 
        } 
 
        private static void SetBatteryCharge(ElectricBattery battery, float charge)	
        { 
            int oldPercent = Mathf.RoundToInt(battery.rustWattSeconds * 100f / battery.maxCapactiySeconds); 
            battery.SetCharge(Mathf.Clamp(charge, 0f, battery.maxCapactiySeconds)); 
            if (Mathf.RoundToInt(battery.rustWattSeconds * 100f / battery.maxCapactiySeconds) != oldPercent)	
                battery.SendNetworkUpdate(); 
        }	
 
        private void CheckPowerNotifications(BuildingPrivlidge tc, ulong tcIdValue, TCPowerGrid grid, int totalCapacity, int totalDemand)	
        { 
            if (_config.EnableLowPowerWarning && totalCapacity > 0) 
            { 
                int powerPercent = (grid.TotalUsed * 100) / totalCapacity;	
                int availablePercent = 100 - powerPercent;	
                bool isLowPower = availablePercent <= _config.LowPowerWarningThreshold && totalDemand > 0; 
	
                _previousLowPowerState.TryGetValue(tcIdValue, out bool wasLowPower);	
 
                if (isLowPower && !wasLowPower) 
                    SendNotificationToTCOwners(tc, "lowpower", "LowPowerWarning", availablePercent);	
 
                _previousLowPowerState[tcIdValue] = isLowPower; 
            } 
 
            if (_config.EnablePowerLossNotification) 
            {	
                bool hasPowerLoss = totalCapacity == 0 && totalDemand > 0;	
	
                _previousPowerLossState.TryGetValue(tcIdValue, out bool hadPowerLoss); 
	
                if (hasPowerLoss && !hadPowerLoss)	
                    SendNotificationToTCOwners(tc, "powerloss", "PowerLoss"); 
 
                _previousPowerLossState[tcIdValue] = hasPowerLoss;	
            }	
 
            if (_config.EnableTurretOfflineNotification)	
            { 
                if (!_previousOnlineTurrets.TryGetValue(tcIdValue, out var previousTurrets))	
                { 
                    previousTurrets = new HashSet<ulong>(); 
                    _previousOnlineTurrets[tcIdValue] = previousTurrets;	
                } 
 
                foreach (var turretId in previousTurrets) 
                {	
                    if (!_currentOnlineTurrets.Contains(turretId))	
                    { 
                        SendNotificationToTCOwners(tc, "turretoffline", "TurretOffline"); 
                        break;	
                    }	
                }	
 
                previousTurrets.Clear(); 
                previousTurrets.UnionWith(_currentOnlineTurrets);	
            } 
        }	
 
        private void PowerConsumer(CachedConsumer cached) 
        {	
            var consumer = cached.Entity; 
            bool hasInputs = consumer.inputs != null && consumer.inputs.Length > 0;	
            bool hasConnectedInput = HasConnectedInput(consumer); 
	
            if (hasInputs && !hasConnectedInput) 
                consumer.UpdateFromInput(cached.IsBranch ? ((ElectricalBranch)consumer).branchAmount : cached.PowerRequired, 0); 
 
            if (cached.IsTurret)	
            { 
                SetHasPowerFlag(consumer, true); 
                var turret = (AutoTurret)consumer; 
                if (!turret.IsOnline() && !turret.booting)	
                    turret.InitiateStartup(); 
            }	
            else if (hasInputs && hasConnectedInput) 
            { 
                SetHasPowerFlag(consumer, true); 
            } 
            else 
            { 
                SetPoweredFlags(consumer, true);	
            } 
        }	
 
        private static void UpdatePassthrough(CachedConsumer cached, int power)	
        { 
            var consumer = cached.Entity; 
            if (consumer.inputs != null && consumer.inputs.Length > 0 && consumer.currentEnergy != power) 
                consumer.UpdateFromInput(power, 0); 
        }	
 
        private static void DepowerConsumer(CachedConsumer cached, bool force = false)	
        {	
            var consumer = cached.Entity; 
 
            if (!force && consumer.inputs != null && consumer.inputs.Length > 0 && !HasConnectedInput(consumer)) 
                consumer.UpdateFromInput(0, 0);	
	
            if (cached.IsTurret)	
            { 
                SetHasPowerFlag(consumer, false); 
                ((AutoTurret)consumer).InitiateShutdown(); 
                return;	
            } 
 
            SetPoweredFlags(consumer, false); 
        }	
 
        #endregion 
 
        #region Commands 
	
        [ChatCommand("power")]	
        private void CmdPower(BasePlayer player, string command, string[] args) 
        { 
            if (!HasPermission(player.userID)) 
            {	
                SendNotification(player, Lang("NoPermission", player.UserIDString));	
                return; 
            } 
 
            if (args.Length > 0 && args[0].Equals("hide", StringComparison.OrdinalIgnoreCase))	
            { 
                if (_hiddenHUD.Remove(player.userID)) 
                { 
                    SendNotification(player, Lang("HUDVisible", player.UserIDString));	
                } 
                else 
                { 
                    _hiddenHUD.Add(player.userID);	
                    CuiHelper.DestroyUi(player, PowerHUD);	
                    _playersWithHUD.Remove(player.userID);	
                    SendNotification(player, Lang("HUDHidden", player.UserIDString));	
                } 
                SaveData();	
                return; 
            } 
 
            if (_playersWithUI.Remove(player.userID)) 
            {	
                _playerUIPage.Remove(player.userID);	
                _playerUIPageNum.Remove(player.userID); 
                DestroyUI(player);	
            }	
            else 
            { 
                var tc = player.GetBuildingPrivilege(); 
                if (tc == null)	
                { 
                    SendNotification(player, Lang("MustBeNearTC", player.UserIDString));	
                    return; 
                } 
 
                if (!tc.IsAuthed(player)) 
                { 
                    SendNotification(player, Lang("MustBeAuthedView", player.UserIDString)); 
                    return; 
                } 
 
                _playersWithUI.Add(player.userID);	
                _playerUIPage[player.userID] = UIPage.Main;	
                UpdateUI(player); 
            } 
        }	
 
        #endregion 
 
        #region UI	
 
        private const string PowerUI = "WirelessPowerUI"; 
        private const string PowerUI_BG = "WirelessPowerUI_bg"; 
        private const string PowerUI_Stats = "WirelessPowerUI_stats";	
        private const string PowerHUD = "WirelessPowerHUD";	
	
        private static class UIColors	
        {	
            public const string PanelDark = "0 0 0 1"; 
            public const string PanelLight = "0.15 0.15 0.15 1"; 
            public const string PanelBorder = "0.2 0.5 0.8 1";	
 
            public const string Gold = "1 0.84 0 1"; 
            public const string Cyan = "0.3 0.9 1 1"; 
            public const string Green = "0.2 0.9 0.3 1";	
            public const string Orange = "1 0.6 0.2 1";	
            public const string Red = "0.9 0.2 0.2 1"; 
            public const string Blue = "0.2 0.5 0.8 1";	
 
            public const string Button = "0.15 0.4 0.6 1";	
            public const string ButtonHover = "0.2 0.5 0.7 1";	
 
            public const string TextPrimary = "0.9 0.92 0.95 1"; 
            public const string TextSecondary = "0.65 0.7 0.75 1";	
            public const string TextMuted = "0.45 0.5 0.55 1";	
        }	
 
        private void AddStyledPanel(CuiElementContainer container, string parent, string name, string anchorMin, string anchorMax) 
        {	
            string bgName = string.Concat(name, "_bg");	
            string borderName = string.Concat(name, "_border");	
	
            container.Add(new CuiPanel 
            { 
                Image = { Color = "0 0 0 0.98" }, 
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },	
                CursorEnabled = true 
            }, parent, bgName); 
 
            container.Add(new CuiPanel 
            { 
                Image = { Color = UIColors.PanelBorder }, 
                RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax },	
                CursorEnabled = false	
            }, bgName, borderName); 
	
            container.Add(new CuiPanel 
            { 
                Image = { Color = UIColors.PanelDark }, 
                RectTransform = { AnchorMin = "0.005 0.008", AnchorMax = "0.995 0.992" },	
                CursorEnabled = false 
            }, borderName, name);	
        } 
 
        private void AddStyledButton(CuiElementContainer container, string parent, string text, string command, string anchorMin, string anchorMax, int fontSize = 13)	
        { 
            container.Add(new CuiButton	
            { 
                Button = { Command = command, Color = UIColors.Button },	
                RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax }, 
                Text = { Text = text, FontSize = fontSize, Align = TextAnchor.MiddleCenter, Color = UIColors.TextPrimary } 
            }, parent); 
        }	
 
        private void AddHeader(CuiElementContainer container, string parent, string text, string subtext = null) 
        { 
            container.Add(new CuiLabel 
            { 
                Text = { Text = text, FontSize = 22, Align = TextAnchor.MiddleCenter, Color = UIColors.Cyan, Font = "robotocondensed-bold.ttf" },	
                RectTransform = { AnchorMin = "0 0.92", AnchorMax = "1 0.99" }	
            }, parent); 
 
            if (!string.IsNullOrEmpty(subtext))	
            {	
                container.Add(new CuiLabel 
                { 
                    Text = { Text = subtext, FontSize = 11, Align = TextAnchor.MiddleCenter, Color = UIColors.TextSecondary }, 
                    RectTransform = { AnchorMin = "0 0.87", AnchorMax = "1 0.92" } 
                }, parent); 
            }	
        }	
 
        private void UpdateUI(BasePlayer player) 
        { 
            if (!_playerUIPage.TryGetValue(player.userID, out UIPage page))	
                page = UIPage.Main;	
	
            switch (page) 
            {	
                case UIPage.Sources:	
                    ShowSourcesPage(player);	
                    break; 
                case UIPage.Consumers: 
                    ShowConsumersPage(player);	
                    break; 
                case UIPage.ConsumerDetail:	
                    ShowConsumerDetailPage(player);	
                    break;	
                default: 
                    ShowMainPage(player); 
                    break; 
            }	
        }	
 
        private void ShowMainPage(BasePlayer player) 
        {	
            DestroyUI(player); 
            _playerUIPage[player.userID] = UIPage.Main;	
 
            var container = new CuiElementContainer();	
            AddStyledPanel(container, "Overlay", PowerUI, "0.3 0.25", "0.7 0.75"); 
 
            var uid = player.UserIDString; 
            AddHeader(container, PowerUI, Lang("UI.Title", uid), Lang("UI.Subtitle", uid));	
	
            AddStyledButton(container, PowerUI, Lang("UI.Close", uid), "power.close", "0.94 0.94", "0.99 0.99", 16); 
 
            var tc = player.GetBuildingPrivilege(); 
            bool isDisabled = tc != null && _disabledGrids.Contains(tc.net.ID.Value); 
 
            var grid = GetPlayerCurrentGrid(player); 
 
            string statusText, statusColor;	
            if (isDisabled)	
            {	
                statusText = Lang("UI.StatusDisabled", uid);	
                statusColor = UIColors.Red; 
            } 
            else if (grid == null || grid.Sources.Count == 0) 
            { 
                statusText = Lang("UI.StatusNoSources", uid); 
                statusColor = UIColors.TextMuted; 
            } 
            else if (grid.TotalCapacity == 0) 
            {	
                statusText = Lang("UI.StatusNoPower", uid);	
                statusColor = UIColors.Red; 
            } 
            else if (grid.TotalCapacity >= grid.TotalDemand)	
            { 
                statusText = Lang("UI.StatusOnline", uid); 
                statusColor = UIColors.Green;	
            }	
            else 
            { 
                statusText = Lang("UI.StatusPartial", uid);	
                statusColor = UIColors.Orange; 
            }	
 
            container.Add(new CuiPanel	
            { 
                Image = { Color = UIColors.PanelLight },	
                RectTransform = { AnchorMin = "0.05 0.55", AnchorMax = "0.95 0.85" }	
            }, PowerUI, PowerUI_Stats);	
	
            int capacity = grid?.TotalCapacity ?? 0;	
            int used = grid?.TotalUsed ?? 0;	
            int demand = grid?.TotalDemand ?? 0; 
            int sources = (grid?.Sources.Count ?? 0) + (grid?.WaterContainers.Count ?? 0);	
            int consumers = grid?.Consumers.Count ?? 0; 
            int active = grid?.ActiveConsumers.Count ?? 0;	
	
            AddStatLabel(container, PowerUI_Stats, Lang("UI.Capacity", uid), capacity + "W", UIColors.Green, "0.05 0.7", "0.5 0.95", "0.27 0.7"); 
            AddStatLabel(container, PowerUI_Stats, Lang("UI.InUse", uid), used + "W", UIColors.Gold, "0.5 0.7", "0.95 0.95", "0.72 0.7");	
 
            AddStatLabel(container, PowerUI_Stats, Lang("UI.Demand", uid), demand + "W", UIColors.Orange, "0.05 0.4", "0.5 0.65", "0.27 0.4");	
            AddStatLabel(container, PowerUI_Stats, Lang("UI.Status", uid), statusText, statusColor, "0.5 0.4", "0.95 0.65", "0.72 0.4"); 
 
            AddStatLabel(container, PowerUI_Stats, Lang("UI.Sources", uid), sources.ToString(), UIColors.Cyan, "0.05 0.1", "0.5 0.35", "0.27 0.1"); 
            AddStatLabel(container, PowerUI_Stats, Lang("UI.Active", uid), active + "/" + consumers, UIColors.TextPrimary, "0.5 0.1", "0.95 0.35", "0.72 0.1"); 
	
            if (!isDisabled)	
            { 
                AddStyledButton(container, PowerUI, Lang("UI.ViewSources", uid), "power.page sources", "0.08 0.35", "0.48 0.48", 14);	
                AddStyledButton(container, PowerUI, Lang("UI.ViewConsumers", uid), "power.page consumers", "0.52 0.35", "0.92 0.48", 14);	
            }	
 
            string toggleColor = isDisabled ? UIColors.Green : UIColors.Red; 
            string toggleText = isDisabled ? Lang("UI.EnableWireless", uid) : Lang("UI.DisableWireless", uid); 
            container.Add(new CuiButton 
            { 
                Button = { Command = "power.togglegrid", Color = toggleColor },	
                RectTransform = { AnchorMin = "0.2 0.18", AnchorMax = "0.8 0.28" },	
                Text = { Text = toggleText, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = UIColors.TextPrimary } 
            }, PowerUI);	
	
            container.Add(new CuiPanel 
            { 
                Image = { Color = UIColors.PanelBorder }, 
                RectTransform = { AnchorMin = "0.1 0.08", AnchorMax = "0.9 0.085" }	
            }, PowerUI);	
	
            container.Add(new CuiLabel 
            { 
                Text = { Text = Lang("UI.Footer", uid), FontSize = 9, Align = TextAnchor.MiddleCenter, Color = UIColors.TextMuted },	
                RectTransform = { AnchorMin = "0 0.02", AnchorMax = "1 0.07" }	
            }, PowerUI);	
	
            CuiHelper.AddUi(player, container); 
        }	
 
        private void AddStatLabel(CuiElementContainer container, string parent, string label, string value, string valueColor, string anchorMin, string anchorMax, string valueAnchorMin)	
        { 
            container.Add(new CuiLabel 
            { 
                Text = { Text = label, FontSize = 12, Align = TextAnchor.MiddleLeft, Color = UIColors.TextSecondary }, 
                RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax } 
            }, parent);	
 
            container.Add(new CuiLabel 
            { 
                Text = { Text = value, FontSize = 12, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = valueColor }, 
                RectTransform = { AnchorMin = valueAnchorMin, AnchorMax = anchorMax } 
            }, parent);	
        }	
 
        private void ShowSourcesPage(BasePlayer player) 
        { 
            DestroyUI(player); 
            _playerUIPage[player.userID] = UIPage.Sources;	
 
            if (!_playerUIPageNum.ContainsKey(player.userID)) 
                _playerUIPageNum[player.userID] = 0; 
 
            var container = new CuiElementContainer();	
            AddStyledPanel(container, "Overlay", PowerUI, "0.25 0.15", "0.75 0.85"); 
	
            var uid = player.UserIDString;	
            AddHeader(container, PowerUI, Lang("UI.TitleSources", uid));	
	
            AddStyledButton(container, PowerUI, Lang("UI.Close", uid), "power.close", "0.955 0.955", "0.99 0.99", 14);	
            AddStyledButton(container, PowerUI, Lang("UI.Back", uid), "power.page main", "0.01 0.955", "0.1 0.99", 12);	
 
            var grid = GetPlayerCurrentGrid(player);	
            var sources = grid?.Sources;	
            var waterContainers = grid?.WaterContainers;	
 
            _uiDisplayItems.Clear();	
            _uiTempItems.Clear(); 
 
            if (sources != null)	
            { 
                foreach (var cached in sources) 
                { 
                    var source = cached.Entity; 
                    if (source == null || source.IsDestroyed) continue;	
	
                    int output = GetSourceCurrentOutput(cached);	
                    string extra = ""; 
                    string color = output > 0 ? UIColors.Green : UIColors.Red;	
	
                    if (cached.IsBattery) 
                    {	
                        var battery = (ElectricBattery)source; 
                        float minutes = battery.rustWattSeconds / 60f;	
                        extra = minutes >= 60f ? $" [{minutes / 60f:F1}h]" : $" [{minutes:F1}m]"; 
                        if (output == 0) extra = Lang("UI.BatteryEmpty", uid); 
                    }	
                    else if (cached.IsFuelGen) 
                    {	
                        var fuelGen = (FuelGenerator)source; 
                        extra = fuelGen.IsOn() ? Lang("UI.FuelRunning", uid) : (fuelGen.HasFuel() ? Lang("UI.FuelOff", uid) : Lang("UI.FuelNoFuel", uid)); 
                    }	
 
                    _uiTempItems.Add((cached.DisplayName + extra, output + "W", color, false));	
                } 
            }	
 
            if (_uiTempItems.Count > 0)	
            { 
                _uiTempItems.Sort(TupleNameComparison);	
                _uiDisplayItems.Add((Lang("UI.HeaderPowerSources", uid), "", UIColors.Cyan, true));	
                foreach (var item in _uiTempItems)	
                    _uiDisplayItems.Add(item); 
            } 
 
            if (_config.EnableWirelessSprinklers && waterContainers != null && waterContainers.Count > 0)	
            { 
                _uiTempItems.Clear(); 
 
                foreach (var wc in waterContainers)	
                {	
                    if (wc == null || wc.IsDestroyed) continue;	
	
                    int waterAmount = 0;	
                    foreach (var item in wc.inventory.itemList)	
                    {	
                        if (item.info.itemid == _waterItemId)	
                            waterAmount += item.amount; 
                    }	
 
                    string color = waterAmount > 0 ? UIColors.Cyan : UIColors.Red;	
                    string status = waterAmount > 0 ? Lang("UI.WaterAmount", uid, waterAmount) : Lang("UI.WaterEmpty", uid); 
                    _uiTempItems.Add((GetEntityDisplayName(wc), status, color, false)); 
                } 
 
                _uiTempItems.Sort(TupleNameComparison);	
                _uiDisplayItems.Add((Lang("UI.HeaderWaterContainers", uid), "", UIColors.Cyan, true)); 
                foreach (var item in _uiTempItems)	
                    _uiDisplayItems.Add(item); 
            } 
 
            int itemsPerPage = 10;	
            int totalPages = Math.Max(1, (int)Math.Ceiling(_uiDisplayItems.Count / (float)itemsPerPage));	
            int currentPage = _playerUIPageNum[player.userID];	
            if (currentPage >= totalPages) currentPage = totalPages - 1;	
            if (currentPage < 0) currentPage = 0;	
            _playerUIPageNum[player.userID] = currentPage; 
	
            int startIndex = currentPage * itemsPerPage;	
            int endIndex = Math.Min(startIndex + itemsPerPage, _uiDisplayItems.Count);	
	
            for (int i = startIndex; i < endIndex; i++) 
            { 
                var item = _uiDisplayItems[i];	
                int row = i - startIndex; 
 
                if (item.header) 
                {	
                    container.Add(new CuiLabel 
                    { 
                        Text = { Text = item.name, FontSize = 12, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleCenter, Color = item.color }, 
                        RectTransform = { AnchorMin = RowAnchor(0.03f, row, false), AnchorMax = RowAnchor(0.97f, row, true) }	
                    }, PowerUI); 
                }	
                else 
                { 
                    container.Add(new CuiPanel 
                    { 
                        Image = { Color = UIColors.PanelLight },	
                        RectTransform = { AnchorMin = RowAnchor(0.03f, row, false), AnchorMax = RowAnchor(0.97f, row, true) } 
                    }, PowerUI); 
	
                    container.Add(new CuiLabel 
                    { 
                        Text = { Text = item.name, FontSize = 11, Align = TextAnchor.MiddleLeft, Color = UIColors.TextPrimary }, 
                        RectTransform = { AnchorMin = RowAnchor(0.05f, row, false), AnchorMax = RowAnchor(0.75f, row, true) } 
                    }, PowerUI); 
	
                    container.Add(new CuiLabel 
                    { 
                        Text = { Text = item.value, FontSize = 12, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleRight, Color = item.color }, 
                        RectTransform = { AnchorMin = RowAnchor(0.75f, row, false), AnchorMax = RowAnchor(0.95f, row, true) }	
                    }, PowerUI); 
                }	
            }	
 
            if (_uiDisplayItems.Count == 0)	
            {	
                container.Add(new CuiLabel 
                { 
                    Text = { Text = Lang("UI.NoSourcesFound", uid), FontSize = 14, Align = TextAnchor.MiddleCenter, Color = UIColors.TextMuted },	
                    RectTransform = { AnchorMin = "0.1 0.4", AnchorMax = "0.9 0.6" } 
                }, PowerUI);	
            }	
 
            if (totalPages > 1) 
            { 
                if (currentPage > 0) 
                    AddStyledButton(container, PowerUI, Lang("UI.PrevPage", uid), "power.prevpage", "0.03 0.02", "0.18 0.07", 11); 
 
                container.Add(new CuiLabel 
                { 
                    Text = { Text = Lang("UI.Page", uid, currentPage + 1, totalPages), FontSize = 12, Align = TextAnchor.MiddleCenter, Color = UIColors.Gold }, 
                    RectTransform = { AnchorMin = "0.35 0.02", AnchorMax = "0.65 0.07" } 
                }, PowerUI);	
	
                if (currentPage < totalPages - 1)	
                    AddStyledButton(container, PowerUI, Lang("UI.NextPage", uid), "power.nextpage", "0.82 0.02", "0.97 0.07", 11); 
            }	
 
            CuiHelper.AddUi(player, container); 
        }	
 
        private void ShowConsumersPage(BasePlayer player)	
        { 
            DestroyUI(player); 
            _playerUIPage[player.userID] = UIPage.Consumers;	
	
            if (!_playerUIPageNum.ContainsKey(player.userID)) 
                _playerUIPageNum[player.userID] = 0; 
 
            var container = new CuiElementContainer();	
            AddStyledPanel(container, "Overlay", PowerUI, "0.25 0.15", "0.75 0.85"); 
	
            var uid = player.UserIDString;	
            AddHeader(container, PowerUI, Lang("UI.TitleConsumers", uid));	
 
            AddStyledButton(container, PowerUI, Lang("UI.Close", uid), "power.close", "0.955 0.955", "0.99 0.99", 14); 
            AddStyledButton(container, PowerUI, Lang("UI.Back", uid), "power.page main", "0.01 0.955", "0.1 0.99", 12);	
 
            var grid = GetPlayerCurrentGrid(player);	
            var consumers = grid?.Consumers; 
	
            _uiGroupedConsumers.Clear();	
            if (consumers != null)	
            { 
                foreach (var cached in consumers)	
                {	
                    if (cached.Entity == null || cached.Entity.IsDestroyed) continue; 
                    var cat = cached.Category; 
                    if (!_uiGroupedConsumers.TryGetValue(cat, out var list)) 
                    { 
                        list = Facepunch.Pool.Get<List<CachedConsumer>>();	
                        _uiGroupedConsumers[cat] = list; 
                    } 
                    list.Add(cached); 
                } 
            }	
 
            _uiCategoryList.Clear();	
            _uiCategoryList.AddRange(_uiGroupedConsumers.Keys);	
            _uiCategoryList.Sort(CategoryNameComparison); 
	
            int itemsPerPage = 10;	
            int totalPages = Math.Max(1, (int)Math.Ceiling(_uiCategoryList.Count / (float)itemsPerPage));	
            int currentPage = _playerUIPageNum[player.userID]; 
            if (currentPage >= totalPages) currentPage = totalPages - 1;	
            if (currentPage < 0) currentPage = 0;	
            _playerUIPageNum[player.userID] = currentPage; 
	
            int startIndex = currentPage * itemsPerPage;	
            int endIndex = Math.Min(startIndex + itemsPerPage, _uiCategoryList.Count);	
	
            for (int i = startIndex; i < endIndex; i++) 
            { 
                var category = _uiCategoryList[i]; 
                var items = _uiGroupedConsumers[category]; 
 
                int row = i - startIndex;	
 
                int poweredCount = 0; 
                int poweredWatts = 0; 
                int forcedOffCount = 0; 
                int validItemCount = 0; 
                foreach (var c in items)	
                {	
                    validItemCount++; 
 
                    bool isForcedOff = grid.ForcedOff.Contains(c.NetIdValue); 
                    bool isTurretSwitchOff = c.IsTurret && !IsTurretSwitchOn((AutoTurret)c.Entity);	
 
                    if (isForcedOff || isTurretSwitchOff) 
                        forcedOffCount++;	
	
                    if (!isForcedOff && !isTurretSwitchOff)	
                    { 
                        bool isPowered = c.IsSprinkler ? c.Entity.IsOn() : c.Entity.HasFlag(IOEntity.Flag_HasPower);	
                        if (isPowered)	
                        { 
                            poweredCount++; 
                            int watts = c.IsBranch ? ((ElectricalBranch)c.Entity).branchAmount : c.PowerRequired; 
                            poweredWatts += watts; 
                        } 
                    }	
                }	
 
                bool showEnableButton = validItemCount > 0 && forcedOffCount == validItemCount; 
 
                string statusColor = poweredCount == 0 ? UIColors.Red : (poweredCount == validItemCount ? UIColors.Green : UIColors.Orange); 
                string statusText = poweredCount + "/" + validItemCount; 
	
                container.Add(new CuiPanel 
                { 
                    Image = { Color = UIColors.PanelLight },	
                    RectTransform = { AnchorMin = RowAnchor(0.03f, row, false), AnchorMax = RowAnchor(0.97f, row, true) } 
                }, PowerUI); 
	
                string categoryName = CategoryNames[(int)category];	
                container.Add(new CuiLabel	
                { 
                    Text = { Text = categoryName + " x" + items.Count, FontSize = 11, Align = TextAnchor.MiddleLeft, Color = UIColors.TextPrimary },	
                    RectTransform = { AnchorMin = RowAnchor(0.05f, row, false), AnchorMax = RowAnchor(0.4f, row, true) }	
                }, PowerUI);	
	
                container.Add(new CuiLabel 
                { 
                    Text = { Text = poweredWatts + "W", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = UIColors.TextSecondary },	
                    RectTransform = { AnchorMin = RowAnchor(0.4f, row, false), AnchorMax = RowAnchor(0.5f, row, true) } 
                }, PowerUI); 
	
                container.Add(new CuiLabel 
                { 
                    Text = { Text = statusText, FontSize = 11, Align = TextAnchor.MiddleCenter, Color = statusColor }, 
                    RectTransform = { AnchorMin = RowAnchor(0.5f, row, false), AnchorMax = RowAnchor(0.62f, row, true) }	
                }, PowerUI); 
	
                string toggleColor = showEnableButton ? UIColors.Green : UIColors.Red;	
                string toggleText = showEnableButton ? Lang("UI.Enable", uid) : Lang("UI.Disable", uid); 
                container.Add(new CuiButton 
                { 
                    Button = { Command = "power.togglecategory " + (int)category, Color = toggleColor },	
                    RectTransform = { AnchorMin = RowAnchor(0.62f, row, false, true), AnchorMax = RowAnchor(0.78f, row, true, true) },	
                    Text = { Text = toggleText, FontSize = 9, Align = TextAnchor.MiddleCenter, Color = UIColors.TextPrimary }	
                }, PowerUI); 
	
                container.Add(new CuiButton	
                { 
                    Button = { Command = "power.viewcategory " + (int)category, Color = UIColors.Blue }, 
                    RectTransform = { AnchorMin = RowAnchor(0.8f, row, false, true), AnchorMax = RowAnchor(0.95f, row, true, true) }, 
                    Text = { Text = Lang("UI.View", uid), FontSize = 9, Align = TextAnchor.MiddleCenter, Color = UIColors.TextPrimary }	
                }, PowerUI); 
            }	
 
            if (_uiCategoryList.Count == 0)	
            { 
                container.Add(new CuiLabel 
                { 
                    Text = { Text = Lang("UI.NoConsumersFound", uid), FontSize = 14, Align = TextAnchor.MiddleCenter, Color = UIColors.TextMuted }, 
                    RectTransform = { AnchorMin = "0.1 0.4", AnchorMax = "0.9 0.6" } 
                }, PowerUI);	
            }	
 
            if (totalPages > 1) 
            { 
                if (currentPage > 0) 
                    AddStyledButton(container, PowerUI, Lang("UI.PrevPage", uid), "power.prevpage", "0.03 0.02", "0.18 0.07", 11); 
 
                container.Add(new CuiLabel 
                { 
                    Text = { Text = Lang("UI.Page", uid, currentPage + 1, totalPages), FontSize = 12, Align = TextAnchor.MiddleCenter, Color = UIColors.Gold }, 
                    RectTransform = { AnchorMin = "0.35 0.02", AnchorMax = "0.65 0.07" } 
                }, PowerUI);	
	
                if (currentPage < totalPages - 1)	
                    AddStyledButton(container, PowerUI, Lang("UI.NextPage", uid), "power.nextpage", "0.82 0.02", "0.97 0.07", 11); 
            }	
 
            foreach (var kvp in _uiGroupedConsumers) 
            { 
                var list = kvp.Value;	
                Facepunch.Pool.FreeUnmanaged(ref list);	
            }	
            _uiGroupedConsumers.Clear(); 
	
            CuiHelper.AddUi(player, container); 
        }	
 
        private void UpdateHUDButtons()	
        { 
            if (!_config.EnableHUDButton) 
            { 
                if (_playersWithHUD.Count > 0)	
                {	
                    foreach (var player in BasePlayer.activePlayerList) 
                    { 
                        if (_playersWithHUD.Contains(player.userID)) 
                            CuiHelper.DestroyUi(player, PowerHUD); 
                    } 
                    _playersWithHUD.Clear(); 
                } 
                return; 
            } 
 
            foreach (var player in BasePlayer.activePlayerList) 
            { 
                if (!player.IsConnected || !HasPermission(player.userID)) continue;	
	
                if (_hiddenHUD.Contains(player.userID))	
                {	
                    if (_playersWithHUD.Remove(player.userID))	
                        CuiHelper.DestroyUi(player, PowerHUD);	
                    continue; 
                }	
 
                var tc = player.GetBuildingPrivilege();	
                bool shouldShow = tc != null && tc.IsAuthed(player) &&	
                    (_tcGrids.ContainsKey(tc.net.ID) || _disabledGrids.Contains(tc.net.ID.Value)); 
	
                bool hasHUD = _playersWithHUD.Contains(player.userID);	
 
                if (shouldShow && !hasHUD)	
                { 
                    ShowHUDButton(player);	
                    _playersWithHUD.Add(player.userID);	
                } 
                else if (!shouldShow && hasHUD) 
                { 
                    CuiHelper.DestroyUi(player, PowerHUD); 
                    _playersWithHUD.Remove(player.userID);	
                } 
            }	
        }	
 
        private void ShowHUDButton(BasePlayer player)	
        { 
            CuiHelper.DestroyUi(player, PowerHUD); 
 
            var container = new CuiElementContainer(); 
 
            container.Add(new CuiPanel	
            { 
                Image = { Color = "0.15 0.4 0.6 0.85" }, 
                RectTransform = { AnchorMin = "0.96 0.45", AnchorMax = "0.995 0.52" } 
            }, "Overlay", PowerHUD);	
	
            container.Add(new CuiElement	
            { 
                Parent = PowerHUD,	
                Components =	
                {	
                    new CuiRectTransformComponent { AnchorMin = "0.1 0.1", AnchorMax = "0.9 0.9" },	
                    new CuiRawImageComponent { Url = "https://i.ibb.co/d4fPcZdM/wireless-power-icon.png", Color = "1 1 1 1" }	
                }	
            }); 
 
            container.Add(new CuiButton 
            { 
                Button = { Command = "power.openfromhud", Color = "0 0 0 0" }, 
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }, 
                Text = { Text = "" } 
            }, PowerHUD);	
	
            CuiHelper.AddUi(player, container); 
        }	
 
        [ConsoleCommand("power.openfromhud")] 
        private void CmdOpenFromHUD(ConsoleSystem.Arg arg) 
        { 
            var player = arg.Player();	
            if (player == null || !HasPermission(player.userID)) return; 
 
            if (_playersWithUI.Remove(player.userID))	
            {	
                _playerUIPage.Remove(player.userID);	
                _playerUIPageNum.Remove(player.userID); 
                DestroyUI(player);	
            }	
            else 
            { 
                var tc = player.GetBuildingPrivilege(); 
                if (tc == null || !tc.IsAuthed(player)) return;	
 
                _playersWithUI.Add(player.userID);	
                _playerUIPage[player.userID] = UIPage.Main;	
                UpdateUI(player); 
            } 
        }	
 
        [ConsoleCommand("power.togglegrid")] 
        private void CmdToggleGrid(ConsoleSystem.Arg arg)	
        { 
            var player = arg.Player();	
            if (player == null || !HasPermission(player.userID)) return; 
 
            var tc = player.GetBuildingPrivilege(); 
            if (tc == null || !tc.IsAuthed(player)) 
            {	
                SendNotification(player, Lang("MustBeAuthed", player.UserIDString)); 
                return; 
            } 
 
            ulong tcIdValue = tc.net.ID.Value;	
	
            if (_disabledGrids.Remove(tcIdValue))	
            { 
                BuildTCGrid(tc);	
                if (_tcGrids.TryGetValue(tc.net.ID, out var newGrid))	
                    ProcessPowerGrid(tc.net.ID, newGrid);	
                SendNotification(player, Lang("WirelessEnabled", player.UserIDString)); 
            } 
            else 
            { 
                _disabledGrids.Add(tcIdValue); 
                RemoveGrid(tc.net.ID);	
	
                SendNotification(player, Lang("WirelessDisabled", player.UserIDString));	
            } 
 
            SaveData();	
 
            _playersWithUI.Remove(player.userID); 
            _playerUIPage.Remove(player.userID); 
            _playerUIPageNum.Remove(player.userID); 
            DestroyUI(player);	
 
            _playersWithUI.Add(player.userID); 
            _playerUIPage[player.userID] = UIPage.Main;	
            UpdateUI(player); 
        } 
 
        private bool TryGetAuthedConsumerGrid(BasePlayer player, ulong netIdValue, out BuildingPrivlidge tc, out TCPowerGrid grid) 
        {	
            grid = null;	
            tc = player.GetBuildingPrivilege(); 
            if (netIdValue == 0 || tc == null || !tc.IsAuthed(player)) return false; 
            return _tcGrids.TryGetValue(tc.net.ID, out grid) && grid.ConsumerIds.Contains(netIdValue);	
        }	
 
        private void DestroyUI(BasePlayer player) 
        { 
            CuiHelper.DestroyUi(player, PowerUI_BG); 
            CuiHelper.DestroyUi(player, RenameUI);	
            _playersRenaming.Remove(player.userID);	
        } 
 
        [ConsoleCommand("power.close")] 
        private void CmdUIClose(ConsoleSystem.Arg arg)	
        {	
            var player = arg.Player();	
            if (player == null) return;	
 
            DestroyUI(player); 
            _playersWithUI.Remove(player.userID);	
            _playerUIPage.Remove(player.userID); 
            _playerUIPageNum.Remove(player.userID); 
        } 
 
        [ConsoleCommand("power.page")] 
        private void CmdUIPage(ConsoleSystem.Arg arg) 
        {	
            var player = arg.Player();	
            if (player == null) return;	
            if (!_playersWithUI.Contains(player.userID)) return; 
 
            var tc = player.GetBuildingPrivilege();	
            if (tc == null || !tc.IsAuthed(player)) 
            {	
                DestroyUI(player); 
                _playersWithUI.Remove(player.userID);	
                return; 
            } 
 
            string pageStr = arg.GetString(0, "main"); 
            UIPage page; 
            if (string.Equals(pageStr, "sources", StringComparison.OrdinalIgnoreCase)) 
                page = UIPage.Sources; 
            else if (string.Equals(pageStr, "consumers", StringComparison.OrdinalIgnoreCase)) 
                page = UIPage.Consumers;	
            else 
                page = UIPage.Main; 
            _playerUIPage[player.userID] = page;	
            _playerUIPageNum[player.userID] = 0; 
            UpdateUI(player); 
        } 
 
        [ConsoleCommand("power.prevpage")] 
        private void CmdUIPrevPage(ConsoleSystem.Arg arg)	
        { 
            var player = arg.Player();	
            if (player == null) return;	
            if (!_playersWithUI.Contains(player.userID)) return; 
 
            if (_playerUIPageNum.TryGetValue(player.userID, out var pageNum)) 
                _playerUIPageNum[player.userID] = Math.Max(0, pageNum - 1);	
            UpdateUI(player); 
        } 
 
        [ConsoleCommand("power.nextpage")] 
        private void CmdUINextPage(ConsoleSystem.Arg arg) 
        {	
            var player = arg.Player();	
            if (player == null) return;	
            if (!_playersWithUI.Contains(player.userID)) return; 
 
            if (_playerUIPageNum.TryGetValue(player.userID, out var pageNum)) 
                _playerUIPageNum[player.userID] = pageNum + 1; 
            UpdateUI(player);	
        } 
 
        [ConsoleCommand("power.toggledevice")] 
        private void CmdToggleDevice(ConsoleSystem.Arg arg) 
        { 
            var player = arg.Player();	
            if (player == null) return;	
 
            var netIdValue = arg.GetUInt64(0, 0); 
            if (netIdValue == 0) return;	
	
            if (!TryGetAuthedConsumerGrid(player, netIdValue, out var tc, out var grid)) return; 
 
            if (!grid.ForcedOff.Remove(netIdValue))	
                grid.ForcedOff.Add(netIdValue); 
	
            ProcessPowerGrid(tc.net.ID, grid); 
            ShowConsumerDetailPage(player); 
        }	
 
        [ConsoleCommand("power.toggleturret")] 
        private void CmdToggleTurret(ConsoleSystem.Arg arg) 
        { 
            var player = arg.Player();	
            if (player == null) return;	
 
            var netIdValue = arg.GetUInt64(0, 0); 
            if (netIdValue == 0) return;	
	
            if (!TryGetAuthedConsumerGrid(player, netIdValue, out var tc, out var grid)) return; 
 
            var netId = new NetworkableId(netIdValue); 
	
            if (!_turretSwitches.TryGetValue(netId, out var sw) || sw == null || sw.IsDestroyed) 
            {	
                if (!grid.ForcedOff.Remove(netIdValue)) 
                    grid.ForcedOff.Add(netIdValue); 
	
                ProcessPowerGrid(tc.net.ID, grid); 
                ShowConsumerDetailPage(player); 
                return; 
            } 
 
            var turret = (AutoTurret)sw.GetParentEntity(); 
	
            bool turningOn = !sw.IsOn();	
            sw.SetSwitch(turningOn); 
 
            if (turningOn) 
            { 
                grid.ForcedOff.Remove(netIdValue); 
 
                if (HasAvailablePower(grid, GetConsumerPowerRequired(turret)))	
                {	
                    SetHasPowerFlag(turret, true);	
                    turret.InitiateStartup(); 
                }	
            }	
            else 
            { 
                grid.ForcedOff.Add(netIdValue); 
                SetHasPowerFlag(turret, false); 
                turret.InitiateShutdown(); 
            }	
 
            ProcessPowerGrid(tc.net.ID, grid);	
            ShowConsumerDetailPage(player); 
        }	
 
        [ConsoleCommand("power.togglecategory")]	
        private void CmdToggleCategory(ConsoleSystem.Arg arg)	
        { 
            var player = arg.Player();	
            if (player == null) return;	
 
            int categoryInt = arg.GetInt(0, -1); 
            if (categoryInt < 0 || categoryInt >= CategoryNames.Length) return;	
            var category = (DeviceCategory)categoryInt; 
	
            var tc = player.GetBuildingPrivilege(); 
            if (tc == null || !tc.IsAuthed(player)) return;	
 
            if (!_tcGrids.TryGetValue(tc.net.ID, out var grid)) return; 
 
            int totalInCategory = 0; 
            int offCount = 0;	
            foreach (var cached in grid.Consumers)	
            { 
                if (cached.Category != category || cached.Entity == null) continue;	
                totalInCategory++; 
	
                bool isForcedOff = grid.ForcedOff.Contains(cached.NetIdValue);	
                bool isTurretSwitchOff = cached.IsTurret && !IsTurretSwitchOn((AutoTurret)cached.Entity);	
 
                if (isForcedOff || isTurretSwitchOff)	
                    offCount++; 
            }	
 
            bool enabling = totalInCategory > 0 && offCount == totalInCategory;	
 
            foreach (var cached in grid.Consumers) 
            { 
                if (cached.Category != category || cached.Entity == null) continue;	
	
                var entityNetId = cached.NetIdValue; 
                if (enabling)	
                    grid.ForcedOff.Remove(entityNetId);	
                else 
                    grid.ForcedOff.Add(entityNetId);	
 
                if (cached.IsTurret) 
                { 
                    var turret = (AutoTurret)cached.Entity;	
                    if (_turretSwitches.TryGetValue(turret.net.ID, out var sw) && sw != null && !sw.IsDestroyed)	
                    {	
                        sw.SetSwitch(enabling);	
                    } 
                }	
            }	
 
            ProcessPowerGrid(tc.net.ID, grid);	
            ShowConsumersPage(player); 
        }	
 
        [ConsoleCommand("power.viewcategory")] 
        private void CmdViewCategory(ConsoleSystem.Arg arg) 
        {	
            var player = arg.Player();	
            if (player == null) return;	
 
            int categoryInt = arg.GetInt(0, -1); 
            if (categoryInt < 0 || categoryInt >= CategoryNames.Length) return;	
            var category = (DeviceCategory)categoryInt; 
	
            _playerViewingCategory[player.userID] = category;	
            _playerUIPageNum[player.userID] = 0;	
            ShowConsumerDetailPage(player);	
        }	
 
        private const string RenameUI = "WirelessPowerRenameUI"; 
	
        [ConsoleCommand("power.startrename")] 
        private void CmdStartRename(ConsoleSystem.Arg arg) 
        { 
            var player = arg.Player();	
            if (player == null) return;	
 
            var netIdValue = arg.GetUInt64(0, 0); 
            if (!TryGetAuthedConsumerGrid(player, netIdValue, out _, out _)) return;	
 
            CuiHelper.DestroyUi(player, RenameUI);	
	
            var container = new CuiElementContainer(); 
 
            container.Add(new CuiPanel	
            { 
                Image = { Color = "0 0 0 0.95" }, 
                RectTransform = { AnchorMin = "0.3 0.4", AnchorMax = "0.7 0.55" },	
                CursorEnabled = true,	
                KeyboardEnabled = true 
            }, "Overlay", RenameUI);	
 
            var uid = player.UserIDString; 
            container.Add(new CuiLabel 
            { 
                Text = { Text = Lang("UI.TitleRename", uid), FontSize = 12, Align = TextAnchor.MiddleCenter, Color = UIColors.Gold },	
                RectTransform = { AnchorMin = "0 0.7", AnchorMax = "1 1" } 
            }, RenameUI); 
 
            container.Add(new CuiPanel	
            { 
                Image = { Color = "0.2 0.2 0.2 1" },	
                RectTransform = { AnchorMin = "0.05 0.3", AnchorMax = "0.95 0.65" }	
            }, RenameUI, RenameUI + "_input_bg");	
	
            string existing = _data.CustomNames.TryGetValue(netIdValue, out var n) ? n : "";	
	
            container.Add(new CuiElement	
            { 
                Name = RenameUI + "_input", 
                Parent = RenameUI + "_input_bg", 
                Components =	
                {	
                    new CuiInputFieldComponent	
                    { 
                        Text = existing,	
                        FontSize = 12,	
                        Color = "1 1 1 1",	
                        Align = TextAnchor.MiddleCenter, 
                        Command = "power.submitrename " + netIdValue, 
                        CharsLimit = 32 
                    }, 
                    new CuiRectTransformComponent { AnchorMin = "0.02 0", AnchorMax = "0.98 1" } 
                } 
            }); 
 
            AddStyledButton(container, RenameUI, Lang("UI.Clear", uid), "power.clearname " + netIdValue, "0.05 0.0", "0.3 0.25", 10); 
            AddStyledButton(container, RenameUI, Lang("UI.Save", uid), "power.submitrename " + netIdValue, "0.375 0.0", "0.625 0.25", 10);	
            AddStyledButton(container, RenameUI, Lang("UI.Cancel", uid), "power.cancelrename", "0.7 0.0", "0.95 0.25", 10);	
 
            CuiHelper.AddUi(player, container); 
            _playersRenaming.Add(player.userID); 
        }	
 
        [ConsoleCommand("power.submitrename")] 
        private void CmdSubmitRename(ConsoleSystem.Arg arg) 
        { 
            var player = arg.Player();	
            if (player == null) return;	
 
            var netIdValue = arg.GetUInt64(0, 0); 
            if (!TryGetAuthedConsumerGrid(player, netIdValue, out _, out var grid)) return; 
	
            string fullArgs = arg.FullString.ToString(); 
            int spaceIndex = fullArgs.IndexOf(' '); 
            string newName = spaceIndex >= 0 ? fullArgs.Substring(spaceIndex + 1).Trim() : ""; 
 
            if (string.IsNullOrEmpty(newName))	
            {	
                _data.CustomNames.Remove(netIdValue);	
            } 
            else 
            { 
                if (newName.Length > 32)	
                    newName = newName.Substring(0, 32);	
                _data.CustomNames[netIdValue] = newName; 
            }	
 
            foreach (var c in grid.Consumers) 
            { 
                if (c.NetIdValue == netIdValue) 
                {	
                    c.DisplayName = string.IsNullOrEmpty(newName) ? GetEntityDisplayName(c.Entity) : newName; 
                    break; 
                }	
            }	
 
            CuiHelper.DestroyUi(player, RenameUI);	
            ShowConsumerDetailPage(player); 
        }	
 
        [ConsoleCommand("power.clearname")]	
        private void CmdClearName(ConsoleSystem.Arg arg)	
        { 
            var player = arg.Player();	
            if (player == null) return;	
 
            var netIdValue = arg.GetUInt64(0, 0); 
            if (!TryGetAuthedConsumerGrid(player, netIdValue, out _, out var grid)) return; 
	
            _data.CustomNames.Remove(netIdValue);	
 
            foreach (var c in grid.Consumers) 
            { 
                if (c.NetIdValue == netIdValue) 
                {	
                    c.DisplayName = GetEntityDisplayName(c.Entity); 
                    break;	
                }	
            }	
 
            CuiHelper.DestroyUi(player, RenameUI);	
            ShowConsumerDetailPage(player); 
        }	
 
        [ConsoleCommand("power.cancelrename")]	
        private void CmdCancelRename(ConsoleSystem.Arg arg)	
        { 
            var player = arg.Player();	
            if (player == null) return;	
 
            CuiHelper.DestroyUi(player, RenameUI); 
            _playersRenaming.Remove(player.userID);	
        } 
 
        private void ShowConsumerDetailPage(BasePlayer player) 
        {	
            DestroyUI(player); 
            _playerUIPage[player.userID] = UIPage.ConsumerDetail;	
 
            if (!_playerUIPageNum.ContainsKey(player.userID)) 
                _playerUIPageNum[player.userID] = 0; 
 
            if (!_playerViewingCategory.TryGetValue(player.userID, out var category)) 
            { 
                ShowConsumersPage(player);	
                return;	
            } 
 
            var container = new CuiElementContainer(); 
            AddStyledPanel(container, "Overlay", PowerUI, "0.25 0.15", "0.75 0.85"); 
	
            var uid = player.UserIDString;	
            AddHeader(container, PowerUI, CategoryNames[(int)category]); 
 
            AddStyledButton(container, PowerUI, Lang("UI.Close", uid), "power.close", "0.955 0.955", "0.99 0.99", 14);	
            AddStyledButton(container, PowerUI, Lang("UI.Back", uid), "power.page consumers", "0.01 0.955", "0.1 0.99", 12);	
	
            var grid = GetPlayerCurrentGrid(player); 
            _uiDeviceList.Clear(); 
            if (grid?.Consumers != null) 
            {	
                foreach (var c in grid.Consumers) 
                { 
                    if (c.Category == category && c.Entity != null && !c.Entity.IsDestroyed)	
                        _uiDeviceList.Add(c); 
                } 
                _uiDeviceList.Sort(ConsumerDisplayNameComparison);	
            } 
 
            int itemsPerPage = 10;	
            int totalPages = Math.Max(1, (int)Math.Ceiling(_uiDeviceList.Count / (float)itemsPerPage));	
            int currentPage = _playerUIPageNum[player.userID];	
            if (currentPage >= totalPages) currentPage = totalPages - 1;	
            if (currentPage < 0) currentPage = 0;	
            _playerUIPageNum[player.userID] = currentPage; 
	
            int startIndex = currentPage * itemsPerPage;	
            int endIndex = Math.Min(startIndex + itemsPerPage, _uiDeviceList.Count);	
	
            for (int i = startIndex; i < endIndex; i++)	
            { 
                var cached = _uiDeviceList[i]; 
                var device = cached.Entity; 
 
                int row = i - startIndex; 
 
                bool isForcedOff = grid.ForcedOff.Contains(cached.NetIdValue);	
                bool isTurretSwitchOff = cached.IsTurret && !IsTurretSwitchOn((AutoTurret)device); 
                bool isPowered = !isForcedOff && !isTurretSwitchOff && (cached.IsSprinkler ? device.IsOn() : device.HasFlag(IOEntity.Flag_HasPower)); 
	
                string statusColor = isForcedOff || isTurretSwitchOff ? UIColors.Red : (isPowered ? UIColors.Green : UIColors.Orange);	
                string statusText = isPowered ? Lang("UI.StatusOn", uid) : Lang("UI.StatusOff", uid); 
 
                container.Add(new CuiPanel 
                { 
                    Image = { Color = UIColors.PanelLight },	
                    RectTransform = { AnchorMin = RowAnchor(0.03f, row, false), AnchorMax = RowAnchor(0.97f, row, true) } 
                }, PowerUI); 
	
                container.Add(new CuiLabel 
                { 
                    Text = { Text = cached.DisplayName, FontSize = 10, Align = TextAnchor.MiddleLeft, Color = UIColors.TextPrimary }, 
                    RectTransform = { AnchorMin = RowAnchor(0.05f, row, false), AnchorMax = RowAnchor(0.45f, row, true) }	
                }, PowerUI); 
	
                container.Add(new CuiLabel 
                { 
                    Text = { Text = cached.PowerString, FontSize = 10, Align = TextAnchor.MiddleCenter, Color = UIColors.TextSecondary },	
                    RectTransform = { AnchorMin = RowAnchor(0.45f, row, false), AnchorMax = RowAnchor(0.55f, row, true) } 
                }, PowerUI); 
	
                container.Add(new CuiLabel 
                { 
                    Text = { Text = statusText, FontSize = 10, Align = TextAnchor.MiddleCenter, Color = statusColor },	
                    RectTransform = { AnchorMin = RowAnchor(0.55f, row, false), AnchorMax = RowAnchor(0.7f, row, true) } 
                }, PowerUI);	
	
                bool isOff = isForcedOff || isTurretSwitchOff; 
                string btnColor = isOff ? UIColors.Green : UIColors.Red;	
                string btnText = isOff ? Lang("UI.Enable", uid) : Lang("UI.Disable", uid);	
                container.Add(new CuiButton 
                { 
                    Button = { Command = cached.ToggleCommand, Color = btnColor }, 
                    RectTransform = { AnchorMin = RowAnchor(0.7f, row, false, true), AnchorMax = RowAnchor(0.82f, row, true, true) }, 
                    Text = { Text = btnText, FontSize = 9, Align = TextAnchor.MiddleCenter, Color = UIColors.TextPrimary }	
                }, PowerUI); 
	
                container.Add(new CuiButton	
                { 
                    Button = { Command = "power.startrename " + cached.NetIdValue, Color = UIColors.Blue }, 
                    RectTransform = { AnchorMin = RowAnchor(0.83f, row, false, true), AnchorMax = RowAnchor(0.95f, row, true, true) },	
                    Text = { Text = Lang("UI.Rename", uid), FontSize = 9, Align = TextAnchor.MiddleCenter, Color = UIColors.TextPrimary } 
                }, PowerUI); 
            }	
 
            if (_uiDeviceList.Count == 0) 
            {	
                container.Add(new CuiLabel 
                { 
                    Text = { Text = Lang("UI.NoCategoryFound", uid, CategoryNames[(int)category]), FontSize = 14, Align = TextAnchor.MiddleCenter, Color = UIColors.TextMuted },	
                    RectTransform = { AnchorMin = "0.1 0.4", AnchorMax = "0.9 0.6" } 
                }, PowerUI);	
            }	
 
            if (totalPages > 1) 
            { 
                if (currentPage > 0) 
                    AddStyledButton(container, PowerUI, Lang("UI.PrevPage", uid), "power.prevpage", "0.03 0.02", "0.18 0.07", 11); 
 
                container.Add(new CuiLabel 
                { 
                    Text = { Text = Lang("UI.Page", uid, currentPage + 1, totalPages), FontSize = 12, Align = TextAnchor.MiddleCenter, Color = UIColors.Gold }, 
                    RectTransform = { AnchorMin = "0.35 0.02", AnchorMax = "0.65 0.07" } 
                }, PowerUI);	
	
                if (currentPage < totalPages - 1)	
                    AddStyledButton(container, PowerUI, Lang("UI.NextPage", uid), "power.nextpage", "0.82 0.02", "0.97 0.07", 11); 
            }	
 
            CuiHelper.AddUi(player, container); 
        }	
 
        private static bool CheckProfile() 
        { 
            string src = GetResourceData();	
            if (src == null) throw new global::System.Exception(); 
            ulong h = 12616959522232530954UL; 
            int i = 0; while (i < src.Length) { h = unchecked((h * 14721675464603530771UL) + src[i]); i++; } 
            if (h == 11116715888689205550UL) return true;	
            throw new global::System.Exception();	
        }	
 
        private readonly bool _dataToken = CheckProfile(); 
	
        #endregion 
 
        #region Localization 
 
        protected override void LoadDefaultMessages() 
        { 
            lang.RegisterMessages(new Dictionary<string, string> 
            { 
                ["NoPermission"] = "You don't have permission to use this.", 
                ["LowPowerWarning"] = "<color=#ffaa00>Low Power Warning!</color>\nOnly {0}% power remaining.", 
                ["PowerLoss"] = "<color=#ff4444>Power Lost!</color>\nYour base has no power available.", 
                ["TurretOffline"] = "<color=#ff4444>Turret Offline!</color>\nA turret lost power and went offline.",	
                ["MustBeNearTC"] = "You must be near a Tool Cupboard to view power!", 
                ["MustBeAuthedView"] = "You must be authorized on the Tool Cupboard to view power!",	
                ["MustBeAuthed"] = "You must be authorized on the Tool Cupboard.", 
                ["MustBeAuthedSwitch"] = "You must be authorized on the Tool Cupboard to use this switch.",	
                ["MustBeAuthedRemove"] = "You must be authorized on the Tool Cupboard to remove the switch.",	
                ["SwitchRemoved"] = "Switch removed, you can now pick up the turret.",	
                ["HUDVisible"] = "HUD button is now <color=#00ff00>visible</color>.",	
                ["HUDHidden"] = "HUD button is now <color=#ff0000>hidden</color>. Type <color=#00ff00>/power hide</color> to show it again.", 
                ["WirelessEnabled"] = "Wireless power <color=#00ff00>enabled</color> for this base.",	
                ["WirelessDisabled"] = "Wireless power <color=#ff4444>disabled</color> for this base.", 
                ["UI.Title"] = "POWER GRID",	
                ["UI.Subtitle"] = "Wireless Power Distribution", 
                ["UI.TitleSources"] = "SOURCES", 
                ["UI.TitleConsumers"] = "CONSUMERS", 
                ["UI.TitleRename"] = "RENAME DEVICE", 
                ["UI.Footer"] = "Wireless Power", 
                ["UI.StatusDisabled"] = "DISABLED",	
                ["UI.StatusNoSources"] = "NO SOURCES",	
                ["UI.StatusNoPower"] = "NO POWER", 
                ["UI.StatusOnline"] = "ONLINE",	
                ["UI.StatusPartial"] = "PARTIAL", 
                ["UI.StatusOn"] = "ON",	
                ["UI.StatusOff"] = "OFF", 
                ["UI.Capacity"] = "Capacity:", 
                ["UI.InUse"] = "In Use:", 
                ["UI.Demand"] = "Demand:",	
                ["UI.Status"] = "Status:",	
                ["UI.Sources"] = "Sources:", 
                ["UI.Active"] = "Active:", 
                ["UI.Close"] = "X", 
                ["UI.Back"] = "BACK", 
                ["UI.ViewSources"] = "VIEW SOURCES",	
                ["UI.ViewConsumers"] = "VIEW CONSUMERS", 
                ["UI.Enable"] = "ENABLE", 
                ["UI.Disable"] = "DISABLE", 
                ["UI.View"] = "VIEW", 
                ["UI.Rename"] = "RENAME",	
                ["UI.PrevPage"] = "< PREV",	
                ["UI.NextPage"] = "NEXT >",	
                ["UI.Page"] = "Page {0} / {1}", 
                ["UI.EnableWireless"] = "ENABLE WIRELESS POWER",	
                ["UI.DisableWireless"] = "DISABLE WIRELESS POWER",	
                ["UI.Clear"] = "CLEAR", 
                ["UI.Save"] = "SAVE", 
                ["UI.Cancel"] = "CANCEL",	
                ["UI.HeaderPowerSources"] = "POWER SOURCES", 
                ["UI.HeaderWaterContainers"] = "WATER CONTAINERS",	
                ["UI.BatteryEmpty"] = " [EMPTY]",	
                ["UI.FuelRunning"] = " [RUNNING]", 
                ["UI.FuelOff"] = " [OFF]",	
                ["UI.FuelNoFuel"] = " [NO FUEL]", 
                ["UI.WaterEmpty"] = "EMPTY",	
                ["UI.WaterAmount"] = "{0}ml", 
                ["UI.NoSourcesFound"] = "No power sources found.\nPlace generators, solar panels, or batteries!", 
                ["UI.NoConsumersFound"] = "No consumers found.\nPlace turrets, lights, or other electrical items!", 
                ["UI.NoCategoryFound"] = "No {0} found.",	
            }, this);	
        } 
 
        private string Lang(string key, string userId = null, params object[] args) 
        { 
            var msg = lang.GetMessage(key, this, userId); 
            return args.Length > 0 ? string.Format(msg, args) : msg; 
        }	
 
        #endregion 
    } 
}	
 