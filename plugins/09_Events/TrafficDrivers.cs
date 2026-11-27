// Requires: NPCsController

// #define DefaultConfig
// #define DebugLog

using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Oxide.Plugins.NPCsControllerEx;
using UnityEngine;
using static Oxide.Plugins.NPCsController;
using Debug = Oxide.Plugins.NPCsControllerEx.Debug;
using Pool = Facepunch.Pool;

namespace Oxide.Plugins
{
    [Info("TrafficDrivers", "walkinrey & Max39ru", "3.0.1")]
    public class TrafficDrivers : CovalencePlugin
    {
        #region Fields

        private static TrafficDrivers instance;
        private static CoreHost coreHost;
        private static EventsManager eventsManagerInstance;
        private static Recorder recorderInstance;
        private const string CMD_VisibleEvent = "visiblecar";
        private const string CMD_TestEvent = "testeventcar";
        private const string RecordCommand = "recordcarpath";
        private const string CommandUI = "record_car_path_gui";
        public Configuration config;
        public Dictionary<TypeCar, string> PrefabsCar = new()
        {
            [TypeCar.Random2Module] = "assets/content/vehicles/modularcar/2module_car_spawned.entity.prefab",
            [TypeCar.Random3Module] = "assets/content/vehicles/modularcar/3module_car_spawned.entity.prefab",
            [TypeCar.Random4Module] = "assets/content/vehicles/modularcar/4module_car_spawned.entity.prefab",
            [TypeCar.Module2] = "assets/content/vehicles/modularcar/admin_prefabs/2_modules/car_2mod_05.prefab",
            [TypeCar.Module3] = "assets/content/vehicles/modularcar/admin_prefabs/3_modules/car_3mod_06.prefab",
            [TypeCar.Module4] = "assets/content/vehicles/modularcar/admin_prefabs/4_modules/car_4mod_07.prefab",
        };
        public Dictionary<string, LocalPosition> TurretsPosition = new()
        {
            ["1module_cockpit_with_engine"] = new(new(0, 1.4f, -0.65f), new()),
            ["1module_taxi"] = new(new(0, 1.4f, 0), new()),
            ["1module_rear_seats"] = new(new(0, 1.4f, 0), new()),
            ["1module_cockpit"] = new(new(0, 1.4f, 0), new()),
            ["1module_storage"] = new(new(0, 0.3f, 0), new()),
            ["1module_flatbed"] = new(new(0, 0.2f, 0), new()),
        };

        #endregion

        #region Configuration

        public class Configuration
        {
            [JsonProperty(RU ? "Настройка событий" : "Event settings")]
            public EventSettings Event = new();

            [JsonProperty(RU ? "Настройка машин" : "Car settings")]
            public List<VehicleSettings> Cars = new();

            [JsonProperty(RU ? CrewSettings.JsonKeyRu : CrewSettings.JsonKeyEn)]
            public CrewSettings Crew;

            public static Configuration GetDefault()
            {
                return new()
                {
                    Event = new EventSettings
                    {
                        maxEvents = new(3, 6),
                        restartCooldown = new(60, 120),
                        blockAttackedToNpcEvents = true,
                        blockDamageVehicleIsNotPlayer = true,
                        blockDamageNpcEventsIsNotPlayer = true,
                        destroyEventsFromPause = true,
                        color = UIColor.blue(1f),
                        NameEvent = "EVENT CAR",
                        ConvoyPlugin = new (true),
                        recorderPathSettings = new Recorder.RecorderCarSettings(true, 20, 50, true),
                        generatePathSettings = new BaseGeneratePathSettings
                        {
                            Enable = true,
                        }
                    },
                    Cars = new List<VehicleSettings>
                    {
                        new VehicleSettings
                        {
                            Enable = true,
                            TypeEvent = TypeCar.Module2,
                            NameDriverID = "DriverCar",
                            NamesCompanionID = new string[2] { "Companion1", "Companion2" },
                            Controller = new VehicleSettings.SettingsController
                            {
                                MaxSpeed = 60,
                                MaxSpeedObstacle = 5,
                                Target = new()
                                {
                                    Enable = true,
                                    DistanceAggression = 150,
                                    TimerAggression = 120f
                                },
                            },
                            damageReceiveRate = 1f,
                            CanRouteToGround = true,
                            CanRouteToRoads = true,
                            Turret = new TurretSettings
                            {
                                Enable = false,
                                MaxCount = 1,
                                MaxDistanceForAttack = 50,
                                RateDamage = 0.5f,
                                PercentHealthModuleFromKill = new(2f, 5f)
                            },
                            fuel = new BaseVehicleSettings.FuelSettings
                            {
                                autoFuel = true,
                                levelFuel = 500,
                                removeFuel = true
                            },
                            Headlights = new BaseVehicleSettings.HeadlightSettings
                            {
                                enableHeadlights = true,
                                onlyAtNightMode = true,
                            },
                            detonator = new BaseVehicleSettings.Detonator
                            {
                                enableDetonator = true,
                                offset = new(0, 0.8f, 0),
                                timer = 60
                            },
                            Engine = new()
                            {
                                CanDropParts = false,
                                TierParts = 1,
                            },
                            storage = new BaseStorageLoot
                            {
                                enableLoot = true,
                                CanDropLoots = false,
                                capacity = 1,
                                NameContainerID = new string[1] { "ResourcesCar" }
                            }
                        },
                        new VehicleSettings
                        {
                            Enable = true,
                            TypeEvent = TypeCar.Random3Module,
                            NameDriverID = "DriverCar",
                            NamesCompanionID = new string[] { "Companion1", "Companion2", },
                            Controller = new VehicleSettings.SettingsController
                            {
                                MaxSpeed = 60,
                                MaxSpeedObstacle = 5,
                                Target = new()
                                {
                                    Enable = true,
                                    DistanceAggression = 150,
                                    TimerAggression = 120f
                                },
                            },
                            damageReceiveRate = 1f,
                            CanRouteToGround = true,
                            CanRouteToRoads = true,
                            Turret = new TurretSettings
                            {
                                Enable = false,
                                MaxCount = 1,
                                MaxDistanceForAttack = 50,
                                RateDamage = 0.5f,
                                PercentHealthModuleFromKill = new(2f, 5f)
                            },
                            fuel = new BaseVehicleSettings.FuelSettings
                            {
                                autoFuel = true,
                                levelFuel = 500,
                                removeFuel = true
                            },
                            Headlights = new BaseVehicleSettings.HeadlightSettings
                            {
                                enableHeadlights = true,
                                onlyAtNightMode = true,
                            },
                            detonator = new BaseVehicleSettings.Detonator
                            {
                                enableDetonator = true,
                                offset = new(0, 0.8f, 0),
                                timer = 60
                            },
                            Engine = new()
                            {
                                CanDropParts = false,
                                TierParts = 1,
                            },
                            storage = new BaseStorageLoot
                            {
                                enableLoot = true,
                                CanDropLoots = false,
                                capacity = 1,
                                NameContainerID = new string[1] { "ResourcesCar" }
                            }
                        },
                    },
                };
            }

            public HashSet<string> GetUsedCrewKeys()
            {
                HashSet<string> keys = new HashSet<string>();
                if (Cars == null) return keys;
                foreach (VehicleSettings car in Cars)
                {
                    if (car == null) continue;
                    if (!string.IsNullOrEmpty(car.NameDriverID)) keys.Add(car.NameDriverID);
                    if (car.NamesCompanionID != null)
                        foreach (string key in car.NamesCompanionID)
                            if (!string.IsNullOrEmpty(key)) keys.Add(key);
                    if (car.storage?.NameContainerID != null)
                        foreach (string key in car.storage.NameContainerID)
                            if (!string.IsNullOrEmpty(key)) keys.Add(key);
                }
                return keys;
            }
        }
        public class EventSettings : BaseEventSettings
        {
            [JsonIgnore] private const int order = 100;

            [JsonProperty(RU ? "Настройка поведения при появлении конвоя" : "Behavior when Convoy spawns", Order = order + 5)]
            public ConvoyPluginSetup ConvoyPlugin = new(true);

            [JsonProperty(RU ? "Настройка цвета текста маркера" : "Marker text color", Order = order + 10)]
            public UIColor color = UIColor.blue(1f);

            [JsonProperty(RU ? "Настройка размера текста маркера (8 - 50)" : "Marker text size (8 - 50)", Order = order + 15)]
            private int sizeMarker = 16;
            [JsonIgnore] public int SizeMarker => Mathf.Clamp(sizeMarker, 8, 50);

            [JsonProperty(RU ? "Название события для маркера" : "Event name for the marker", Order = order + 20)]
            public string NameEvent = "EVENT CAR";

            [JsonProperty(RU ? "Настройка записи маршрутов" : "Route recording settings", Order = order + 30)]
            public Recorder.RecorderCarSettings recorderPathSettings = new();

            [JsonProperty(RU ? "Настройка генерации маршрутов" : "Route generation", Order = order + 40)]
            public BaseGeneratePathSettings generatePathSettings = new BaseGeneratePathSettings();
        }
        public class VehicleSettings : BaseVehicleSettings
        {
            [JsonIgnore] private const int order = 0;

            [JsonProperty(RU ? "Включить адаптивную скорость (более аккуратное вождение и избегание препятствий)" : "Enable adaptive speed (more accurate driving and obstacle avoidance)", Order = 535)]
            public bool EnableAdaptiveSpeed = true;

            [JsonProperty(RU ? "Тип транспорта для события(Random2Module, Random3Module, Random4Module, Module2, Module3, Module4)" : "Transport type for event (Random2Module, Random3Module, Random4Module, Module2, Module3, Module4)", Order = order + 10)]
            public TypeCar TypeEvent = new();

            [JsonProperty(RU ? "Кастомный префаб транспорта (оставьте пустым если не нужно)" : "Custom vehicle prefab (leave empty if not needed)", Order = order + 10)]
            public string VehiclePrefab = "";

            [JsonProperty(RU ? "Название(ключ) водителя из настройки водителей раздела экипажа" : "Driver name (key) from driver settings in the Crew section", Order = order + 20)]
            public string NameDriverID;
            [JsonProperty(RU ? "Название(ключ) водителя из настройки водителей в плагине DriverController" : "Driver name (key) from driver settings in DriverController plugin")]
            private string NameDriverIDOld { set => NameDriverID ??= value; }

            [JsonProperty(RU ? "Название(ключ) компаньона из настройки компаньонов раздела экипажа (перечислите, что бы заняли свободные места в транспорте)" : "Companion name (key) from companion settings in the Crew section (list to occupy free seats in the vehicle)", Order = order + 30)]
            public string[] NamesCompanionID;
            [JsonProperty(RU ? "Название(ключ) компаньона из настройки компаньонов в плагине DriverController (перечислите, что бы заняли свободные места в транспорте)" : "Companion name (key) from companion settings in DriverController plugin (list to occupy free seats in the vehicle)")]
            private string[] NamesCompanionIDOld { set => NamesCompanionID ??= value; }

            [JsonProperty(RU ? "Настройка двигателя" : "Engine settings", Order = order + 33)]
            public EngineSettings Engine = new ();

            [JsonProperty(RU ? "Настройка контейнера" : "Loot container settings", Order = order + 35)]
            public BaseStorageLoot storage = new BaseStorageLoot();

            [JsonProperty(RU ? "Использовать маршруты по дорогам(работает при включении генерации маршрутов и имеют приоритет над бездорожьем)" : "Use Road Routes (has priority over off-road)", Order = order + 40)]
            public bool CanRouteToRoads = true;

            [JsonProperty(RU ? "Использовать маршруты по бездорожью(работает при включении генерации маршрутов и используются если нет маршрута по дороге)" : "Use Off-Road Routes (used if no road routes are available)", Order = order + 50)]
            public bool CanRouteToGround = true;

            [JsonProperty(RU ? "Индивидуальная настройка контроллера" : "Controller settings", Order = order + 55)]
            public SettingsController Controller = new();

            [JsonProperty(RU ? "Настройка турелей(будет добавлять указанное количество на каждый подходящий модуль)" : "Turret Settings (will add the specified amount to each suitable module)", Order = order + 70)]
            public TurretSettings Turret = new TurretSettings();

            public class SettingsController : BaseSettingsController
            {
                [JsonIgnore] private const int order = 100;

                [JsonProperty(RU ? "Максимальная скорость при обнаружении препятствий(Km/h)" : "Maximum speed when obstacles detected (km/h)", Order = order + 10)]
                public float MaxSpeedObstacle = 10f;

                [JsonProperty(RU ? "Смещение от оси дороги вправо по ходу на маршрутах по дорогам (м, 0 - ехать по оси)" : "Offset from the road center line to the right on road routes (m, 0 - drive on the center line)", Order = order + 15)]
                public float LaneOffset = 2.5f;

                [JsonProperty(RU ? "Настройка режима преследования" : "Chase mode settings", Order = order + 20)]
                public BaseTargetSettings Target = new();
            }

        }
        public class EngineSettings
        {
            [JsonIgnore] private const int order = 0;

            [JsonProperty(RU ? "Какой уровень деталей использовать?(1 - 3)" : "Engine parts level (1 - 3)", Order = order + 10)]
            public int TierParts = 3;

            [JsonProperty(RU ? "Сбрасывать детали при уничтожении транспорта?" : "Drop the parts when destroying vehicle?", Order = order + 10)]
            public bool CanDropParts = false;

            public int GetTier() => Mathf.Clamp(TierParts, 1, 3);
        }
        public class TurretSettings : BaseTurretSettings
        {
            [JsonIgnore] private const int order = 100;

            [JsonProperty(RU ? "Максимальное количество турелей" : "Max turrets amount", Order = order + 10)]
            public int MaxCount = 1;

            [JsonProperty(RU ? "При каком проценте оставшегося состояния модуля, удалять установленную на нем турель" : "At what percentage of the module's remaining health remove the turret installed on it", Order = order + 20)]
            public NPCsController.MinMax PercentHealthModuleFromKill = new(2f, 5f);
        }

        [JsonConverter(typeof(StringEnumConverter))]
        public enum TypeCar
        {
            Random2Module, Random3Module, Random4Module, Module2, Module3, Module4
        }

        #endregion

        #region Загрузка конфига

        protected override void LoadDefaultConfig() => config = Configuration.GetDefault();
        protected override void LoadConfig()
        {
            base.LoadConfig();
            if (config == null)
            {
                config = CoreConfig.Load<Configuration>(this);
            }
            if (config == null) LoadDefaultConfig();
            config.Event ??= new EventSettings();
            config.Cars ??= new List<VehicleSettings>();
            if (CrewImport.Ensure(this, ref config.Crew, config.GetUsedCrewKeys()))
                Debug.Log<TrafficDrivers>("Crew section added to the config (import from DriverController)");
            SaveConfig();
        }
        protected override void SaveConfig()
        {
            Config.WriteObject(config);
        }

        #endregion

        #region Localization

        protected override void LoadDefaultMessages()
        {
            Dictionary<string, string> ru = new Dictionary<string, string>(RecorderManager.DefaultMessagesRu)
            {
                ["HasTestEventOtherAdmin"] = "Тестовое событие уже запущено другим администратором.",
                ["NoPointBelowOceanLevel"] = "Не допускается в океане",
            };
            Dictionary<string, string> en = new Dictionary<string, string>(RecorderManager.DefaultMessagesEn)
            {
                ["HasTestEventOtherAdmin"] = "Test event has already been started by another administrator.",
                ["NoPointBelowOceanLevel"] = "Not allowed in the ocean",
            };
            lang.RegisterMessages(ru, this, "ru");
            lang.RegisterMessages(en, this, "en");
        }

        #endregion

        #region InitHooks

        private void Init()
        {
            instance = this;
            coreHost = VehicleCore.Init(this);
        }
        private void OnServerInitialized()
        {
            HelperVehicles.EnsureRoads();
            recorderInstance = RecorderManager.Create<Recorder>();
            eventsManagerInstance = BaseEventsManager<TrafficDrivers>.CreateManager<EventsManager>();
        }
        private void Unload()
        {
            VehicleCore.Unload(this);
            eventsManagerInstance = null;
            recorderInstance = null;
            instance = null;
        }

        #endregion

        #region Hooks

        [HookMethod("OnTestModeEvent")]
        public void OnTestModeEvent(BasePlayer admin) => eventsManagerInstance?.StopTestEvent(admin);

        private void OnNewSave(string filename) => VehicleCore.HostFor(this).IsWipe = true;

        private void OnConvoyStart()
        {
            if (config?.Event.ConvoyPlugin.CanPause == true && eventsManagerInstance != null) eventsManagerInstance.StartPause();
        }
        private void OnConvoyStop()
        {
            if (config?.Event.ConvoyPlugin.CanPause == true && eventsManagerInstance != null) eventsManagerInstance.StopPause();
        }

        private void OnEntityKill(BaseNetworkable entity) => VehicleCoreHooks.OnEntityKill(coreHost, entity);

        #endregion

        #region Commands

        [Command(CMD_VisibleEvent, CMD_TestEvent)]
        private void Cmd_Car(IPlayer iPlayer, string command, string[] args)
            => eventsManagerInstance?.AdminCommand(iPlayer, command, args, CMD_VisibleEvent, CMD_TestEvent);
        [Command(RecordCommand, CommandUI)]
        private void Cmd_Record(IPlayer iPlayer, string command, string[] args)
        {
            recorderInstance?.Command(iPlayer, command, args);
        }

        #endregion

        #region EventsManager

        public class EventsManager : BaseEventsManager<TrafficDrivers>
        {
            protected Color color;
            protected int size = 16;
            protected override int maxEvents => instance.config.Event.maxEvents.GetRandomInt();
            public override float Cooldown => instance.config.Event.restartCooldown.GetRandomFloat();
            protected override Color colorMarker => color;
            protected override int sizeMarker => size;

            protected override void Awake()
            {
                base.Awake();

                color = instance.config.Event.color.GetUnityColor();
                size = instance.config.Event.SizeMarker;
                if (instance.config.Event.generatePathSettings?.Enable == true) modePaths.Add(ModePath.Generate);
                if (instance.config.Event.recorderPathSettings?.Enable == true && recorderInstance != null && recorderInstance.HasRecorded)
                    modePaths.Add(ModePath.Record);
                if (modePaths.Count == 0)
                {
                    Debug.LogWarning<TrafficDrivers>(
                        "Route building mode is not set (enable recorded routes or route generation in the config)");
                }
            }

            protected override void Start()
            {
                base.Start();
                if (!instance.config.Cars.Exists(x => x != null && x.Enable))
                {
                    Debug.LogWarning<TrafficDrivers>("No active car settings found in the config");
                    return;
                }
                if (!HasTypePath) return;
                CreateEvents<EventController>(maxEvents);
            }
            protected override void SpawnTestEvent(BasePlayer admin) => CreateEvents<EventController>(1, admin);
        }

        #endregion

        #region EventController

        public class EventController : BaseGroundEventController<TrafficDrivers>
        {
            #region Настройки события
            protected VehicleSettings vehicleSettings;
            private bool _pathOnRoad;
            protected int NumTurrets = 0;

            protected override string NameEvent
            {
                get
                {
                    string name = !string.IsNullOrEmpty(instance.config.Event.NameEvent) ? instance.config.Event.NameEvent : "EVENT CAR";
                    return IsTestController ? "TEST " + name : name;
                }
            }
            public override string TypeEvent => vehicleSettings?.TypeEvent.ToString() ?? "";
            protected override string NameDriverID => vehicleSettings?.NameDriverID;
            protected override string[] NamesCompanionID => vehicleSettings?.NamesCompanionID;
            protected override CrewSettings Crew => instance.config.Crew;
            protected override float RouteSpeedKmh => vehicleSettings?.Controller?.MaxSpeed ?? 40f;
            protected override BaseVehicleSettings.BaseTargetSettings ChaseSettings => vehicleSettings?.Controller?.Target;
            protected override float timerDelayedInitVehicle => 1f;
            public override bool CanDisableTurretToEndEvent => vehicleSettings?.Turret?.CanDisable ?? false;
            protected override bool canEngineStart
            {
                get
                {
                    if (!base.canEngineStart) return false;
                    if (Vehicle is GroundVehicle car) return car.engineController?.owner?.MeetsEngineRequirements() == true;
                    return true;
                }
            }

            public override BaseVehicleSettings GetSettings() => vehicleSettings;
            public override BaseEventSettings GetEventSettings() => instance.config.Event;
            private bool RouteRecorded => modePath == BaseEventsManager<TrafficDrivers>.ModePath.Record;
            #endregion

            #region Событие
            protected override GroundProfile CreateProfile() => CarRoute.Profile;

            protected override void SetSettingsVehicle()
            {
                vehicleSettings = CoreEx.GetRandom(instance.config.Cars.FindAll(x => x != null && x.Enable));
                modePath = eventManager.GetTypePath();
                NumTurrets = 0;
                Turrets.Clear();
                if (vehicleSettings == null || !eventManager.HasTypePath) return;
                if (RouteRecorded)
                {
                    ClearMonumentLine();
                    SetPath(recorderInstance != null ? CoreEx.GetRandom(recorderInstance.PathRecords) : null);
                    _pathOnRoad = true;
#if DebugLog
                    Debug.Log<TrafficDrivers>("Using a recorded route [{0} #{1}]", NameEvent, number);
#endif
                }
            }
            protected override string SpawnPrefab => string.IsNullOrEmpty(vehicleSettings.VehiclePrefab)
                ? instance.PrefabsCar[vehicleSettings.TypeEvent]
                : vehicleSettings.VehiclePrefab;
            protected override HelperVehicles.VehicleControllerBase AddPilot(CrewNpc driver)
            {
                CarPilot pilot = driver.gameObject.AddComponent<CarPilot>();
                pilot.Settings = vehicleSettings;
                pilot.Owner = this;
                return pilot;
            }
            protected override bool PathByRoad => _pathOnRoad;
            protected override void InitStorage()
            {
                if (storage is StorageContainer container && container != null) FillStorageLoot(container, vehicleSettings.storage);
            }
            protected override void InitMountPoints()
            {
                NumTurrets = 0;
                if (Vehicle is ModularCar modularCar)
                {
                    foreach (BaseVehicleModule module in modularCar.AttachedModuleEntities)
                    {
                        if (module == null) continue;
                        module.SetHealth(module.MaxHealth());
                        InitTurret(module);
                        if (module is VehicleModuleStorage moduleStorage && moduleStorage.ShortPrefabName == "1module_storage"
                            && vehicleSettings.storage?.enableLoot == true)
                        {
                            if (storage == null) storage = moduleStorage.GetContainer();
                            continue;
                        }
                        if (module is VehicleModuleEngine moduleEngine)
                        {
                            InitEngineModule(moduleEngine);
                        }
                        if (module is VehicleModuleSeating moduleSeating && moduleSeating.mountPoints != null)
                        {
                            foreach (BaseVehicle.MountPointInfo mount in moduleSeating.mountPoints) RegisterMountPoint(mount);
                        }
                    }
                }
                base.InitMountPoints();
            }
            private void InitTurret(BaseVehicleModule module)
            {
                if (vehicleSettings?.Turret?.Enable != true || NumTurrets >= vehicleSettings.Turret.MaxCount) return;
                if (!instance.TurretsPosition.TryGetValue(module.ShortPrefabName, out LocalPosition localPosition)) return;
                TurretController turret = VehicleCore.SpawnTurret<TurretController>(localPosition, module);
                if (turret == null) return;
                NumTurrets++;
                turret.InitSpawn(vehicleSettings.Turret, this);
                Turrets.Add(turret.Owner);
            }
            private void InitEngineModule(VehicleModuleEngine moduleEngine)
            {
                var container = moduleEngine.GetContainer();
                if (container?.inventory == null) return;
                container.inventory.SetLocked(true);
                moduleEngine.AdminFixUp(vehicleSettings.Engine?.GetTier() ?? 3);

                if (container.inventory.entityOwner is StorageContainer storageContainer)
                    storageContainer.dropsLoot = vehicleSettings.Engine?.CanDropParts == true;
            }
            public override string GetTestInfo()
            {
                string text = base.GetTestInfo();
                if (Pilot is CarPilot pilot)
                {
                    text += $"\nSpeed limit: {CoreEx.ToKms(pilot.SpeedLimit)}, obstacle: {pilot.ObstacleAhead}";
                    if (Vehicle != null) text += $", engine: {(Vehicle.HasFlag(BaseEntity.Flags.On) ? "on" : "off")}";
                    text += $"\nLane steering: {pilot.LaneSteering}, shift {pilot.LaneShift:0.00} m, "
                            + $"clear {pilot.SteerClearance:0.0} m, blocked: {pilot.LaneBlocked}";
                    if (pilot.CurveLimit < 100f) text += $", curve limit: {CoreEx.ToKms(pilot.CurveLimit)}";
                }
                text += $"\nTeleports: {Teleports}";
                return text;
            }
            #endregion

            #region Маршрут события
            private CarRoute _carRoute;
            protected override bool PrepareGeneration()
            {
                if (!vehicleSettings.CanRouteToRoads && !vehicleSettings.CanRouteToGround)
                {
                    Debug.LogWarning<TrafficDrivers>(
                        "Route generation is enabled, but the car has both roads and off-road disabled [{0} #{1}]", NameEvent, number);
                    return false;
                }
                List<PathList> roads = GroundRoads.Roads(CarRoute.Profile.RoadClasses);
                if (roads.Count == 0)
                {
                    Debug.LogWarning<TrafficDrivers>("No roads on the map - route not generated [{0} #{1}]", NameEvent, number);
                    return false;
                }
                _carRoute = new CarRoute(vehicleSettings.CanRouteToRoads, vehicleSettings.CanRouteToGround, MinPathPoints, NameEvent, number,
                    roads);
                return true;
            }
            protected override RouteCandidate NewCandidate() => new CarRoute.Candidate();
            protected override IEnumerator GenerateAttempt(RouteCandidate candidate) => _carRoute.Attempt((CarRoute.Candidate)candidate);
            protected override bool MoreAttempts(RouteCandidate best, int attempt)
                => !(best is CarRoute.Candidate car && car.Flaws == car.Tight && attempt >= CarRoute.TightAttempts);
            protected override void OnRouteChosen(RouteCandidate route)
            {
                _pathOnRoad = route.OnRoad;
                if (route is CarRoute.Candidate car && car.Seams.Failed > 0)
                    Debug.Log<TrafficDrivers>(
                        "{0} #{1}: road seams the car cannot drive along the chord: {2}, replaced by a crossing nearer the roadbed: {3}, bypassed by a side point: {4}",
                        NameEvent, number, car.Seams.Failed, car.Seams.Moved, car.Seams.Bypassed);
            }
            #endregion

            #region Полосы
            protected override float LaneOffset => vehicleSettings?.Controller?.LaneOffset ?? 0f;
            #endregion

            #region Линия через монумент
            private const float LineNeedSlack = 0.25f;
            private const int LineSharedSamples = 6;

            protected override CoreLines.Request LineRequest(Vector3[] points, int a, int b)
            {
                GroundProfile profile = CarRoute.Profile;
                float body = profile.LineBodyHalfWidth;
                float lane = Mathf.Clamp(LaneOffset, 0f, MaxLaneOffset);
                float roadHalf = GroundRoads.RoadPointAt(points[a], profile.RoadClasses, out float width, out _)
                                 || GroundRoads.RoadPointAt(points[b], profile.RoadClasses, out width, out _)
                    ? width * 0.5f
                    : profile.RoadHalfWidth;
                lane = Mathf.Max(0f, Mathf.Min(lane, roadHalf - body - LaneEdgeGap));
                float rightLimit = Mathf.Max(profile.SidestepMinRight, roadHalf - lane - body - SidestepEdgeGap) + LineNeedSlack;
                float leftLimit = Mathf.Min(lane + roadHalf - body - SidestepEdgeGap, lane + profile.OncomingSidestep) + LineNeedSlack;
                bool Needed(float[][] lateral)
                {
                    float[] forward = lateral[0], backward = lateral[1];
                    for (int k = 0, shared = 0; k < forward.Length; k++)
                    {
                        shared = Mathf.Abs(forward[k] - backward[k]) < 2f * body ? shared + 1 : 0;
                        float ahead = forward[k] - lane, back = -(backward[k] + lane);
                        if (shared >= LineSharedSamples) return true;
                        if (ahead > rightLimit || -ahead > leftLimit || back > rightLimit || -back > leftLimit) return true;
                    }
                    return false;
                }
                return new CoreLines.Request { Body = body, Lanes = new[] { lane, -lane }, Needed = Needed };
            }
            #endregion

            #region Разворот
            protected override bool TurnsWhereStopped => true;
            #endregion
        }

        #endregion

        #region Маршрут машины

        public sealed class CarRoute
        {
            private const float ShortRouteLength = 300f;
            private const float RoadSnapDistance = 30f;
            private const float MaxImpassableSegment = 30f;
            private const float GroundRouteMinDistance = 300f, GroundRouteMaxDistance = 1500f;
            public const int TightAttempts = 3;
            private readonly bool _byRoads, _byGround;
            private readonly int _minPoints, _number;
            private readonly string _name;
            private readonly List<PathList> _roads;
            private bool _onRoad;
            public CarRoute(bool byRoads, bool byGround, int minPoints, string name, int number, List<PathList> roads)
            {
                _byRoads = byRoads;
                _byGround = byGround;
                _minPoints = minPoints;
                _name = name;
                _number = number;
                _roads = roads;
            }

            #region Попытка маршрута
            public sealed class Candidate : RouteCandidate
            {
                public GroundRoads.SeamReport Seams = new GroundRoads.SeamReport();
                public int Cramped, Tight;
                public override string Describe() => $"seams not replaced {Seams.Left}, ends without room to turn {Cramped}";
            }
            public IEnumerator Attempt(Candidate result)
            {
                List<PathList> roads = _roads;
                PathList startRoad = CoreEx.GetRandom(roads);
                if (startRoad?.Path == null) yield break;
                Vector3 start = startRoad.Path.GetStartPoint();
                Vector3 finish;
                if (_byRoads)
                {
                    PathList finishRoad = CoreEx.GetRandom(roads);
                    if (finishRoad?.Path == null) yield break;
                    finish = finishRoad.Path.GetEndPoint();
                }
                else if (!TryGetGroundFinish(roads, start, out finish)) yield break;
                List<Vector3> route = FindRoute(start, finish, out bool byLand);
                int tight = 0;
                if (route == null && byLand)
                {
                    LandBuild land = new LandBuild();
                    IEnumerator building = BuildLandGuarded(start, finish, land);
                    while (building.MoveNext()) yield return building.Current;
                    if (land.Route != null)
                    {
                        _onRoad = false;
                        route = land.Route;
                        tight = land.Tight;
                        Debug.Log<TrafficDrivers>("{0} #{1}: off-road route {2:0} m{3}; {4}", _name, _number, CoreEx.Distance(route),
                            land.Describe(), CoreNavLand.Describe(land.Job));
                    }
#if DebugLog
                    else
                        Debug.Log<TrafficDrivers>("Off-road route not found [{0} #{1}]: {2}", _name, _number,
                            CoreNavLand.Describe(land.Job));
#endif
                }
                if (route == null) yield break;
                int cramped = 0;
                if (_onRoad) route = GroundRoads.TrimEnds(GroundRoads.RepairSeams(route, Profile, out result.Seams), Profile, out cramped);
                result.Flaws = result.Seams.Left + cramped + tight + (CoreEx.Distance(route) < ShortRouteLength ? 1 : 0);
                result.Route = route;
                result.OnRoad = _onRoad;
                result.Tight = tight;
                result.Cramped = cramped;
            }
            private List<Vector3> FindRoute(Vector3 start, Vector3 finish, out bool byLand)
            {
                byLand = false;
                if (_byRoads)
                {
                    List<Vector3> route = HelperVehicles.RoadRoute(start, finish, Profile.RoadClasses, RoadSnapDistance);
#if DebugLog
                    Debug.Log<TrafficDrivers>("Road route search [{0} #{1}]: points {2}", _name, _number, route.Count);
#endif
                    if (IsRouteValid(start, route))
                    {
                        _onRoad = true;
                        return CoreRoutes.WithStart(start, route);
                    }
                    if (route.Count > 0) return null;
                }
                byLand = _byGround;
                return null;
            }
            private static bool TryGetGroundFinish(List<PathList> roads, Vector3 start, out Vector3 finish)
            {
                finish = start;
                List<Vector3> candidates = Pool.Get<List<Vector3>>();
                foreach (PathList road in roads)
                {
                    if (road?.Path == null) continue;
                    AddGroundFinish(candidates, start, road.Path.GetStartPoint());
                    AddGroundFinish(candidates, start, road.Path.GetEndPoint());
                }
                if (candidates.Count == 0)
                {
                    foreach (PathList road in roads)
                    {
                        Vector3[] points = road?.Path?.Points;
                        if (points == null) continue;
                        for (int i = 0; i < points.Length; i += 8) AddGroundFinish(candidates, start, points[i]);
                    }
                }
                bool found = candidates.Count > 0;
                if (found) finish = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                Pool.FreeUnmanaged(ref candidates);
                return found;
            }
            private static void AddGroundFinish(List<Vector3> into, Vector3 start, Vector3 point)
            {
                float distance = CoreEx.DistanceFlat(start, point);
                if (distance >= GroundRouteMinDistance && distance <= GroundRouteMaxDistance) into.Add(point);
            }
            private bool IsRouteValid(Vector3 start, List<Vector3> route) => CoreRoutes.LongEnough(start, route, _minPoints, Profile.MinRouteLength);
            private static bool IsRoutePassable(Vector3 start, List<Vector3> route)
            {
                Vector3 prev = start;
                foreach (Vector3 point in route)
                {
                    if (CoreEx.DistanceFlat(prev, point) > MaxImpassableSegment && !HelperVehicles.SegmentPassable(prev, point, out _))
                        return false;
                    prev = point;
                }
                return true;
            }
            #endregion

            #region Бездорожье
            private const float LandFrameBudget = 4f;
            private static CoreNavLand.Job LandJob(Vector3 start, Vector3 finish)
                => new CoreNavLand.Job { From = start, To = finish, Check = LegCheck, BudgetMs = LandFrameBudget, WholeFallback = true };
            private sealed class LandBuild
            {
                public List<Vector3> Route;
                public CoreNavLand.Job Job;
                public int Tight, Rounded, Reshaped, Builds, FirstTight = -1;
                public string Describe() => FirstTight > 0
                    ? string.Format(", sharp turns without room for the body {0} of {1}, rounded {2}, straightened {3} (builds {4})", Tight,
                        FirstTight, Rounded, Reshaped, Builds)
                    : "";
            }
            private const int CornerRetries = 2;
            private IEnumerator BuildLand(Vector3 start, Vector3 finish, LandBuild result)
            {
                for (int pass = 0; pass <= CornerRetries; pass++)
                {
                    CoreNavLand.Job job = LandJob(start, finish);
                    IEnumerator build = CoreNavLand.Build(job);
                    while (build.MoveNext()) yield return null;
                    result.Builds++;
                    if (result.Job == null) result.Job = job;
                    if (!IsRouteValid(start, job.Result) || !IsRoutePassable(start, job.Result)) yield break;
                    List<Vector3> route = CoreRoutes.WithStart(start, job.Result);
                    GroundCorners.Result corners = new GroundCorners.Result();
                    IEnumerator round = GroundCorners.Round(route, Profile, corners);
                    while (round.MoveNext()) yield return null;
                    if (result.FirstTight < 0) result.FirstTight = corners.Tight + corners.Rounded + corners.Reshaped;
                    bool better = result.Route == null || corners.Tight < result.Tight;
                    if (better)
                    {
                        result.Route = route;
                        result.Job = job;
                        result.Tight = corners.Tight;
                        result.Rounded = corners.Rounded;
                        result.Reshaped = corners.Reshaped;
                    }
                    if (corners.Tight == 0 || !better) yield break;
                    foreach (Vector3 spot in corners.Spots) HelperVehicles.RememberStuckSpot(spot);
                    yield return null;
                }
            }
            private IEnumerator BuildLandGuarded(Vector3 start, Vector3 finish, LandBuild land)
            {
                IEnumerator build = BuildLand(start, finish, land);
                while (true)
                {
                    bool more;
                    try { more = build.MoveNext(); }
                    catch (Exception ex)
                    {
                        Debug.LogError<TrafficDrivers>("{0} #{1}: off-road route failed: {2}", _name, _number, ex);
                        land.Route = null;
                        yield break;
                    }
                    if (!more) yield break;
                    yield return null;
                }
            }
            private static readonly CoreNavLand.LegCheck LegCheck =
                (Vector3 a, Vector3 b, out Vector3 blockedAt) => GroundChecks.Leg(a, b, Profile, out blockedAt);
            #endregion

            #region Профиль машины
            public static readonly GroundProfile Profile = new GroundProfile
            {
                Noun = "car", TeleportSteps = new[] { 20f, 30f, 45f, 60f, 80f }, BoxMargin = 0.4f, BoxBottom = 0.5f, BoxTop = 2f,
                FacingAhead = 15f, ResumeAheadMin = 6f, ResumeAheadSearch = 30f, DefaultHalfWidth = 1.3f, DefaultHalfLength = 2.3f,
                LegStep = 2.5f, LegMaxGrade = 28f, AxisGround = true, LedgeStep = 0.25f, DeckOverWater = true, WaterDepth = 0.4f,
                WaterOffsets = new[] { 0f, -2f, 2f, -4f, 4f, -6f, 6f }, LegWheel = 1f, LegCross = 26f, LegMidWheels = 1.5f,
                SplitCorridor = 1.65f, CorridorLift = (0.2f + 1.7f) * 0.5f, CorridorHalfHeight = (1.7f - 0.2f) * 0.5f,
                TurnRadius = 6f,
                RoadClasses = HelperVehicles.RoadClass.Main | HelperVehicles.RoadClass.Side, RoadHalfWidth = 5f, MinRouteLength = 100f,
                SidestepMinRight = 0.3f, OncomingSidestep = 2.5f, SeamSidestep = 2.5f,
                SeamReach = GroundRoads.SeamMaxChord, SeamCheck = SeamChordClear, SeamWindowNodes = 8, SeamWindowLength = float.MaxValue,
                SeamMinChord = 1f, SeamMaxTurn = 100f, SeamSingleTurn = 0f, SeamTurnFromRoute = true, SeamNodeCost = 4f,
                TurnSpotSearch = 60f, TurnSpotHalfWidth = 6f, TurnSpotHalfLength = 6f, TurnSpotLift = 1.5f, TurnSpotHalfHeight = 1f,
                TurnSpotSlope = 30f, TurnSpotLevelArea = true, TurnSpotJunction = 20f,
                TurnSpotLayers = new[] { "World", "Construction", "Deployed", "Tree", "Default" },
                GenerateAttempts = 8, GenerateFrameBudget = float.MaxValue, SpawnOnSurface = false, Queues = true, LineBodyHalfWidth = 1.3f
            };
            private static int _seamMask;
            private static readonly Collider[] _seamHits = new Collider[16];
            private static bool SeamChordClear(Vector3 from, Vector3 to, out Vector3 blockedAt)
            {
                blockedAt = to;
                Vector3 flat = to - from;
                flat.y = 0f;
                float length = flat.magnitude;
                if (length < 1f) return true;
                int steps = Mathf.Max(2, Mathf.CeilToInt(length / 2f));
                float run = length / steps, grade = Mathf.Tan(25f * Mathf.Deg2Rad);
                float prev = TerrainMeta.HeightMap.GetHeight(from);
                for (int k = 1; k <= steps; k++)
                {
                    float t = (float)k / steps;
                    Vector3 p = Vector3.Lerp(from, to, t);
                    float height = TerrainMeta.HeightMap.GetHeight(p);
                    float line = Mathf.Lerp(from.y, to.y, t);
                    if (height - line > 0.6f || line - height > 1.5f || Mathf.Abs(height - prev) > grade * run)
                    {
                        blockedAt = p;
                        return false;
                    }
                    prev = height;
                }
                if (_seamMask == 0) _seamMask = LayerMask.GetMask("World", "Construction", "Deployed", "Tree");
                Vector3 center = (from + to) * 0.5f + Vector3.up * 1.25f;
                int count = Physics.OverlapBoxNonAlloc(center, new Vector3(1.6f, 0.75f, length * 0.5f), _seamHits,
                    Quaternion.LookRotation(to - from, Vector3.up), _seamMask, QueryTriggerInteraction.Ignore);
                for (int c = 0; c < count; c++)
                {
                    Collider collider = _seamHits[c];
                    if (collider == null || GroundChecks.IsRoadOrBush(collider.name)) continue;
                    blockedAt = collider.ClosestPointOnBounds(center);
                    return false;
                }
                return true;
            }
            #endregion
        }

        #endregion

        #region CarPilot

        public class CarPilot : EventGroundPilot
        {
            private const float RoadCorridorMargin = 0.8f;

            public VehicleSettings Settings;
            public EventController Owner;

            #region Настройки вождения
            protected override float ObstacleBoxFront(Bounds bounds)
                => Mathf.Max(bounds.extents.z, _profile != null ? _profile.HalfLength : 0f) + 0.3f;
            protected override bool SteerToAim => true;
            protected override bool CarefulManeuvers => true;
            protected override int EventFanRays => 13;
            protected override float LaneAimDistance(float v) => LineSpeed > 0f ? Mathf.Clamp(v * 1.2f, 5f, 22f) : base.LaneAimDistance(v);
            protected override bool NoseOnTurn => LineSpeed <= 0f;
            protected override bool NoseAlongWheels => true;
            protected override float CorridorMargin => LineSpeed > 0f ? base.CorridorMargin : RoadCorridorMargin;
            protected override bool SidestepChecksSurface => !KeepToRoad;
            protected override bool SidestepChecksLedges => !KeepToRoad;
            protected override bool CorridorTerrainByMap => !KeepToRoad;
            protected override float SteerDeadZone => Tuning?.SteerDeadZone ?? 1f;
            protected override float UsefulClear => KeepToRoad ? base.UsefulClear : Mathf.Max(base.UsefulClear, 30f);
            protected override bool ReportsLaneObstacles => !KeepToRoad;
            #endregion

            #region Вождение
            protected override float ObstacleSpeedKmh => Settings?.Controller != null ? Settings.Controller.MaxSpeedObstacle : 10f;
            protected override bool AdaptiveSpeed => Settings != null && Settings.EnableAdaptiveSpeed;
            protected override float LineSpeedAt(Vector3 position) => Owner != null ? Owner.LineSpeedNear(position) : 0f;
            protected override bool BoxIgnoredOnLine => true;
            protected override void OnLaneObstacle(Vector3 point) => Owner?.LaneObstacle(point);
            #endregion
        }

        #endregion

        #region TurretController

        public class TurretController : BaseTurretController
        {
            private BaseEntity parent;
            private float percent;
            public override void InitSpawn(BaseTurretSettings settings, IEventController eventController)
            {
                base.InitSpawn(settings, eventController);
                parent = owner != null ? owner.GetParentEntity() : null;
                percent = settings is TurretSettings turretSettings ? turretSettings.PercentHealthModuleFromKill.GetRandomFloat() : 0f;
            }
            protected override void CheckHealthParent()
            {
                if (parent == null || parent.IsDestroyed) return;
                float maxHealth = parent.MaxHealth();
                if (maxHealth > 0f && parent.Health() / maxHealth * 100f < percent) KillImmediate();
            }
        }

        #endregion

        #region Recorder

        public class Recorder : RecorderManager
        {
            public override RecorderPathSettings Config => instance.config.Event.recorderPathSettings;
            protected override string nameFile => $"{nameof(TrafficDrivers)}/RecorderPaths";
            public override string RecordCommandName => RecordCommand;
            public override string RecordUiCommandName => CommandUI;

            public class RecorderCarSettings : RecorderPathSettings
            {
                [JsonIgnore] public override bool CanPointFromOcean => false;
                [JsonIgnore] public override float Height => 0.5f;
                [JsonIgnore] public override bool CanPointFromGround => true;
                [JsonIgnore] public override float AreaDepth => 0f;

                public RecorderCarSettings(bool enable, float minDistanceSavePoint, float minPointsForSave, bool canClearRecFromWipe)
                    : base(enable, minDistanceSavePoint, minPointsForSave, canClearRecFromWipe)
                {
                }

                public RecorderCarSettings()
                {
                }
            }
        }

        #endregion
    }
}
