#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

using Oxide.Core;
using Newtonsoft.Json;
using Oxide.Core.Plugins;
using Oxide.Ext.CrashExtensions;
using UnityEngine;

namespace Oxide.Plugins;

[Info("Scrap Worm", "crash", "1.3.2")]
[Description("A giant scrap worm hunts under your map: it breaches under the loudest noise, and when you kill it, it drops burning crates.")]
public class ScrapWorm : RustPlugin
{
    private const string Command = "scrapworm";
    private const string StartCommand = "scrapworm.start";
    private const string TerritoriesCommand = "scrapworm.territories";
    private const string GibPrefab = "assets/content/vehicles/scrap heli carrier/servergibs_scraptransport.prefab";
    private const string CorePrefab = "assets/prefabs/deployable/ceiling light/ceilinglight.deployed.prefab";
    private const string HeliCratePrefab = "assets/prefabs/npc/patrol helicopter/heli_crate.prefab";
    private const string BradleyCratePrefab = "assets/prefabs/npc/m2bradley/bradley_crate.prefab";
    private const string HackCratePrefab = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab";
    private const string FireBallPrefab = "assets/bundled/prefabs/oilfireballsmall.prefab";
    private const string MarkerPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";
    private const string DataFile = "ScrapWorm";
    private const string LootFile = "ScrapWorm_Loot";
    private const float MaxPelletCos = 0.866f;
    private const float GridMeters = 146.3f;
    private const float WatchSeconds = 1f;
    private const float CrushCooldown = 1f;
    private const string BloodEffect = "assets/prefabs/deployable/hopper/effects/hopper_bloodspurt.prefab";
    private const int PullMask = Rust.Layers.Mask.Terrain | Rust.Layers.Mask.World | Rust.Layers.Mask.Construction | Rust.Layers.Mask.Deployed | Rust.Layers.Mask.Tree;
    private const float PullSpeed = 7f;
    private const float BiteWindup = 0.8f;
    private const float BiteHold = 2.5f;
    private const float BiteCooldown = 7f;
    private const float TailCooldown = 1.5f;
    private readonly Dictionary<ulong, float> _tailHitAt = new();
    private readonly List<TimedExplosive> _thrown = new();
    private readonly List<Vector3> _thrownLast = new();
    private Vector3 _noise;
    private float _noiseAt = float.NegativeInfinity;
    private int _noiseWeight;
    private bool _slamArmed;
    private float _maxHealth = 1f;
    private BasePlayer _biteTarget;
    private float _biteSince;
    private float _nextBite;
    private readonly Dictionary<ulong, float> _biteSkipUntil = new();
    private ulong _bitten;
    private float _bittenUntil;
    private const float BittenSeconds = 1.5f;
    private const float CoverSeconds = 1f;
    private const float FightSeconds = 60f;
    private struct Cover { public float Until; public bool Covered; }
    private readonly Dictionary<ulong, Cover> _cover = new();
    private Dictionary<ulong, float>[] _coreBy = Array.Empty<Dictionary<ulong, float>>();
    private float _lastDamageAt = float.NegativeInfinity;
    private int _hurtFaults;
    private int _crushFaults;
    private Vector3 _raidAsk;
    private float _raidAskAt = float.NegativeInfinity;
    private bool _raidAskIn;
    private readonly List<Territory> _territories = new();
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private readonly Dictionary<NetworkableId, float> _crushedAt = new();
    private Territory _territory;
    private Timer _eventTimer;
    private float _eventEndsAt;
    private float _lastSeenAt;
    private float _nextWatch;
    private bool _warnedDirty;
    private MapMarkerGenericRadius _surfaceMarker;
    private ItemDefinition[] _debrisDefs = Array.Empty<ItemDefinition>();
    private readonly Dictionary<BaseEntity, PerHitPiece> _pieces = new();
    private readonly List<BaseEntity> _stalePieces = new();
    private readonly List<Vector3> _stuckFrom = new();
    private readonly List<TimedExplosive> _stuck = new();
    private readonly BaseEntity[] _anchors = new BaseEntity[Segments];
    private readonly Vector3[] _framePos = new Vector3[Segments];
    private readonly Quaternion[] _frameRot = new Quaternion[Segments];
    private float _nextGroupCheck;
    private const int CoverMask = Rust.Layers.Mask.Construction | Rust.Layers.Mask.Deployed;
    private const int StuckMask = Rust.Layers.Mask.Terrain | Rust.Layers.Mask.World | Rust.Layers.Mask.Construction | Rust.Layers.Mask.Deployed;
    private const float MonumentClearance = 20f;
    private const float NoiseRingMeters = 6f;
    private const int NoiseTestBudget = 44;
    private const float ArcRetrySeconds = 5f;
    private int _buildingTests;
    private const string NotifySeePermission = "notify.see";
    private int _life;
    private bool _killQueued;
    private bool _protecting;
    private bool _damageHooked;
    private float _nextPieceSync;
    private readonly Dictionary<ulong, float> _lockToldAt = new();
    private readonly List<PendingWinner> _pendingWinners = new();
    private bool _pendingDirty;
    private const string WinnersFile = "ScrapWorm_Winners";
    private const int MaxPendingWinners = 20;
    private const float PendingWinnerHours = 1f;
    private bool HasPlaces => _settings.RandomLocationEachEvent || _territories.Count > 0;

    private sealed class PendingWinner
    {
        public ulong Id;
        public string Name = "";
        public string Grid = "";
        public string Wipe = "";
        public long ExpiresUtc;
    }

    private sealed class Territory
    {
        public string Name;
        public Vector3 Center;
        public float Radius;
        public MapMarkerGenericRadius Marker;
        public bool Temporary;
    }

    private sealed class LootLockEntry
    {
        public string Winner = "";
        public float Until;
        public readonly HashSet<ulong> Owners = new();
    }

    private sealed class SavedLock
    {
        public ulong Crate;
        public uint Prefab;
        public float X, Y, Z, Until;
        public string Winner;
        public List<ulong> Owners;
    }

    private struct Pellet
    {
        public BasePlayer Player;
        public int Id, Core, Segment;
        public float Amount;
        public Vector3 Point;
    }

    private sealed class TerritorySpec
    {
        public string Name = "";
        public float X;
        public float Z;
        public float Radius = 300f;
    }

    private sealed class DebrisItem
    {
        public string ShortName = "";
        public int Amount;
        public float MinPieceSize;
        public int AmountPerHit;
    }

    private sealed class PerHitLoot
    {
        public ItemDefinition Definition;
        public int Left;
        public int Step;
    }

    private sealed class PerHitPiece
    {
        public ResourceDispenser Dispenser;
        public readonly List<PerHitLoot> Loot = new();
        public int Budget;
        public int Left;
    }

    private const string BlastEffect = "assets/bundled/prefabs/fx/explosions/explosion_01.prefab";
    private const string SparkEffect = "assets/prefabs/locks/keypad/effects/lock.code.shock.prefab";
    private const string HitNotifyEffect = "assets/bundled/prefabs/fx/hit_notify.prefab";
    private const string DirtEffect = "assets/bundled/prefabs/fx/survey_explosion.prefab";
    private const string QuakeEffect = "assets/content/vehicles/siegeweapons/catapult/effects/boulder_explosion_mid.prefab";
    private const string DiveEffect = "assets/content/vehicles/siegeweapons/catapult/effects/boulder_explosion.prefab";
    private const string EruptEffect = "assets/prefabs/satellitecrash/fxgibs/satellite impact fx.prefab";
    private const string GrindEffect = "assets/content/vehicles/scrap heli carrier/effects/debris_effect.prefab";
    private const string RoarEffect = "assets/rust.ai/agents/bear/sound/roar.prefab";
    private const string StrongQuakeEffect = "assets/prefabs/weapons/satchelcharge/effects/satchel-charge-explosion.prefab";
    private const string DeathEffect = "assets/content/vehicles/scrap heli carrier/effects/scrap-transport-heli-explosion.prefab";
    private const int SightMask = Rust.Layers.Mask.Terrain | Rust.Layers.Mask.World | Rust.Layers.Mask.Construction | Rust.Layers.Mask.Deployed | Rust.Layers.Mask.Tree;
    private const float TickSeconds = 0.1f;
    private const float PauseSeconds = 2.5f;
    private const float WarnSeconds = 2.6f;
    private const float RumbleRadius = 60f;
    private const float MaxGroundVariation = 2.5f;
    private const float NoticeRadius = 150f;
    private const float CoreRadius = 0.9f;
    private const float CoreBurstShare = 0.1f;
    private const float StrippedBonus = 2.5f;
    private const float MaxShotRange = 300f;
    private const int History = 4;
    private const int MaxDebris = 64;
    private const float TeethSpeed = 220f;
    private const float AnchorScale = 0.1f;
    private const string AnchorGib = "ScrapHeliGibs_Part033";
    private const float RocketCheckSeconds = 0.02f;
    private const float CrushSeconds = 0.2f;
    private const int CrushPerTick = 8;
    private const int CrushMask = Rust.Layers.Mask.Construction | Rust.Layers.Mask.Deployed;
    private static readonly Collider[] CrushHits = new Collider[64];
    private float _nextCrush;
    private static readonly Effect Sfx = new();
    [PluginReference] private Plugin AlphaLoot, Economics, ServerRewards, NCP, Notify, RaidableBases;
    private const float RaidBaseStartMargin = 10f;
    private int _raidBlockedStarts;
    private const string AdminPermission = "scrapworm.admin";
    private static readonly string[] WebhookPrefixes = { "https://discord.com/api/webhooks/", "https://discordapp.com/api/webhooks/", "https://ptb.discord.com/api/webhooks/", "https://canary.discord.com/api/webhooks/" };
    private static readonly Dictionary<string, string> JsonHeaders = new() { ["Content-Type"] = "application/json" };
    private const string HudRoot = "ScrapWorm.Hud";
    private const string HudBg = "0.1529 0.1412 0.1137 0.96";
    private const string HudDeep = "0.0784 0.0706 0.0588 1";
    private const string HudRed = "0.8039 0.2549 0.1686 1";
    private const string HudRimIdle = "0.7882 0.7529 0.7216 0.37";
    private const string HudChipHot = "0.8039 0.2549 0.1686 0.34";
    private const string HudChipIdle = "0.9686 0.9216 0.8824 0.14";
    private const string HudStateIdle = "0.7882 0.7529 0.7216 0.7";
    private const string HudCounter = "0.9686 0.9216 0.8824 0.75";
    private const string HudNotch = "0.1529 0.1412 0.1137 1";
    private const string ImageBase = "https://rust-cui.crashplugins.space/scrapworm/";
    private const string PortraitKey = "scrapworm.portrait";
    private const string CoreOnKey = "scrapworm.core.on";
    private const string CoreOffKey = "scrapworm.core.off";
    private const string HudInk = "0.9686 0.9216 0.8824 1";
    private const string HudMuted = "0.7882 0.7529 0.7216 0.6";
    private const string HudTrack = "0.9686 0.9216 0.8824 0.12";
    private const string HudPending = "0.9686 0.9216 0.8824 0.28";
    private const string HudValue = "1 0.6549 0.6039 1";
    private const float HudSeconds = 0.25f;
    private readonly UITree _hudTree = CUI.Tree(HudRoot);
    private readonly HashSet<BasePlayer> _hudShown = new();
    private readonly List<BasePlayer> _hudGone = new();
    private float _nextHud;
    private float _trail = 1f;
    private readonly Dictionary<ulong, float> _damageBy = new();
    private readonly bool[] _shed = new bool[Model.Length];
    private readonly bool[] _stripped = new bool[Segments];
    private readonly Dictionary<BaseEntity, LootLockEntry> _lootLocks = new();
    private readonly List<BaseEntity> _lootGone = new();
    private readonly List<Pellet> _pellets = new();
    private float[] _coreBlast = Array.Empty<float>();
    private readonly List<BaseEntity> _parts = new();
    private readonly HashSet<BaseEntity> _owned = new();
    private readonly List<BaseEntity> _heldChildren = new();
    private readonly List<BaseEntity> _debris = new();
    private readonly List<Collider> _debrisBatch = new();
    private readonly Dictionary<string, Bounds> _gibBounds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Mesh> _gibMeshes = new(StringComparer.Ordinal);
    private int[] _coreParts = Array.Empty<int>();
    private float[] _coreHealth = Array.Empty<float>();
    private float[] _nextSpark = Array.Empty<float>();
    private Vector3[] _coreHistory = Array.Empty<Vector3>();
    private readonly Vector3[] _segmentHistory = new Vector3[Segments * History];
    private float _health;
    private float _teethAngle;
    private float _arcLength;
    private float _arcHeight;
    private float _arcSpeed;
    private Timer _rocketTick;
    private readonly List<TimedExplosive> _rockets = new();
    private readonly List<Vector3> _rocketLast = new();
    private int _historyCursor;
    private Timer _tick;
    private bool _roarOn, _roarListening;
    private float _roarSeconds;
    private readonly HashSet<ulong> _roarEarly = new();
    private float _roarAt;
    private Settings _settings;
    private bool _ready;
    private bool _breaching;
    private bool _loop;
    private bool _dead;
    private int _coresAlive;
    private float _headS;
    private float _resumeAt;
    private float _lastTick;
    private float _warnUntil;
    private float _nextPulse;
    private float _nextGrind;
    private Vector3 _origin;
    private Vector3 _heading;
    private readonly List<BasePlayer> _near = new();
    private readonly Dictionary<string, UIDocument> _hudDocs = new(StringComparer.Ordinal);
    private int _hudHealth = -1, _hudTrail = -1, _hudBits = -1;

    private sealed class PartSpec
    {
        public readonly string Gib;
        public readonly int Segment;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly Vector3 Scale;
        public readonly bool Spin;

        public PartSpec(string gib, int segment, Vector3 position, Quaternion rotation, Vector3 scale, bool spin = false)
        { Gib = gib; Segment = segment; Position = position; Rotation = rotation; Scale = scale; Spin = spin; }
    }

    private const int Segments = 7;
    private const float Spacing = 2.2f;
    private static readonly float[] Radii = { 1.35f, 1.15f, 1.0695f, 0.989f, 0.828f, 0.644f, 0.483f };
    private static readonly PartSpec[] Model =
    {
        new("ScrapHeliGibs_Part043", 0, new(0f, 0f, 0f), new(0f, 0f, 0f, 1f), new(1.84f, 1.84f, 0.95f)),
        new("ScrapHeliGibs_Part047", 0, new(0f, 0.897000015f, 0f), new(0f, 0f, 0f, 1f), new(0.897f, 1.265f, 0.82f)),
        new("ScrapHeliGibs_Part046", 0, new(0.920000017f, 0.057500001f, 0f), new(0f, 0f, -0.629320443f, 0.777145922f), new(0.713f, 1.035f, 1.12f)),
        new("ScrapHeliGibs_Part046", 0, new(-0.920000017f, 0.057500001f, 0f), new(0f, 0f, 0.629320383f, 0.777145982f), new(0.713f, 1.035f, 1.12f)),
        new("ScrapHeliGibs_Part046", 0, new(0f, -0.828000009f, 0f), new(0f, 0f, -1f, 0f), new(0.69f, 1.035f, 1.12f)),
        new("ScrapHeliGibs_Part027", 0, new(0f, 1.28799999f, -0.100000001f), new(0f, 0f, 0f, 1f), new(1.2f, 1.4375f, 1.4375f)),
        new("ScrapHeliGibs_Part047", 0, new(0f, 0.550000012f, 0.899999976f), new(-0.241921946f, 0f, 0f, 0.970295668f), new(0.85f, 1.1f, 0.55f)),
        new("ScrapHeliGibs_Part002", 0, new(0f, -0.0500000007f, 1.25f), new(-0.707106769f, 0f, 0f, 0.707106769f), new(0.62f, 0.55f, 0.62f)),
        new("ScrapHeliGibs_Part027", 0, new(0.67175144f, 0.67175144f, 1.75f), new(-0.923879564f, 0.382683456f, 0f, 0f), new(1.6f, 1.5f, 1.7f)),
        new("ScrapHeliGibs_Part027", 0, new(-0.67175144f, 0.67175144f, 1.75f), new(-0.923879564f, -0.382683456f, 0f, 0f), new(1.6f, 1.5f, 1.7f)),
        new("ScrapHeliGibs_Part027", 0, new(-0.67175144f, -0.67175144f, 1.75f), new(-0.382683456f, -0.923879564f, 0f, 0f), new(1.6f, 1.5f, 1.7f)),
        new("ScrapHeliGibs_Part027", 0, new(0.67175144f, -0.67175144f, 1.75f), new(-0.382683367f, 0.923879564f, 0f, 0f), new(1.6f, 1.5f, 1.7f)),
        new("ScrapHeliGibs_Part027", 0, new(0.550000012f, 0f, 2.3499999f), new(-0.707106769f, 0.707106769f, 0f, 0f), new(1.1f, 0.85f, 0.95f), true),
        new("ScrapHeliGibs_Part027", 0, new(0.275000006f, 0.476313978f, 2.3499999f), new(-0.965925813f, 0.258819103f, 0f, 0f), new(1.1f, 0.85f, 0.95f), true),
        new("ScrapHeliGibs_Part027", 0, new(-0.275000006f, 0.476313978f, 2.3499999f), new(-0.965925813f, -0.258819103f, 0f, 0f), new(1.1f, 0.85f, 0.95f), true),
        new("ScrapHeliGibs_Part027", 0, new(-0.550000012f, 0f, 2.3499999f), new(-0.707106769f, -0.707106769f, 0f, 0f), new(1.1f, 0.85f, 0.95f), true),
        new("ScrapHeliGibs_Part027", 0, new(-0.275000006f, -0.476313978f, 2.3499999f), new(-0.258819073f, -0.965925813f, 0f, 0f), new(1.1f, 0.85f, 0.95f), true),
        new("ScrapHeliGibs_Part027", 0, new(0.275000006f, -0.476313978f, 2.3499999f), new(-0.258818954f, 0.965925813f, 0f, 0f), new(1.1f, 0.85f, 0.95f), true),
        new("ScrapHeliGibs_Part043", 1, new(0f, 0f, 0f), new(0f, 0f, 0f, 1f), new(1.6f, 1.6f, 0.95f)),
        new("ScrapHeliGibs_Part047", 1, new(0f, 0.779999971f, 0f), new(0f, 0f, 0f, 1f), new(0.78f, 1.1f, 0.82f)),
        new("ScrapHeliGibs_Part046", 1, new(0.800000012f, 0.0500000007f, 0f), new(0f, 0f, -0.629320443f, 0.777145922f), new(0.62f, 0.9f, 1.12f)),
        new("ScrapHeliGibs_Part046", 1, new(-0.800000012f, 0.0500000007f, 0f), new(0f, 0f, 0.629320383f, 0.777145982f), new(0.62f, 0.9f, 1.12f)),
        new("ScrapHeliGibs_Part046", 1, new(0f, -0.720000029f, 0f), new(0f, 0f, -1f, 0f), new(0.6f, 0.9f, 1.12f)),
        new("ScrapHeliGibs_Part027", 1, new(0f, 1.12f, -0.100000001f), new(0f, 0f, 0f, 1f), new(1.2f, 1.25f, 1.25f)),
        new(null, 1, new(1.04999995f, 0.150000006f, 0f), new(0f, 0f, 0.707106709f, 0.707106769f), new(0.9f, 0.9f, 0.9f)),
        new("ScrapHeliGibs_Part043", 2, new(0f, 0f, 0f), new(0f, 0f, 0f, 1f), new(1.488f, 1.488f, 0.95f)),
        new("ScrapHeliGibs_Part047", 2, new(0f, 0.725399971f, 0f), new(0f, 0f, 0f, 1f), new(0.7254f, 1.023f, 0.82f)),
        new("ScrapHeliGibs_Part046", 2, new(0.744000018f, 0.0465000011f, 0f), new(0f, 0f, -0.629320443f, 0.777145922f), new(0.5766f, 0.837f, 1.12f)),
        new("ScrapHeliGibs_Part046", 2, new(-0.744000018f, 0.0465000011f, 0f), new(0f, 0f, 0.629320383f, 0.777145982f), new(0.5766f, 0.837f, 1.12f)),
        new("ScrapHeliGibs_Part046", 2, new(0f, -0.66960001f, 0f), new(0f, 0f, -1f, 0f), new(0.558f, 0.837f, 1.12f)),
        new("ScrapHeliGibs_Part027", 2, new(0f, 1.04159999f, -0.100000001f), new(0f, 0f, 0f, 1f), new(1.2f, 1.1625f, 1.1625f)),
        new(null, 2, new(-0.976499975f, 0.139500007f, 0f), new(0f, 0f, -0.707106769f, 0.707106769f), new(0.9f, 0.9f, 0.9f)),
        new("ScrapHeliGibs_Part043", 3, new(0f, 0f, 0f), new(0f, 0f, 0f, 1f), new(1.376f, 1.376f, 0.95f)),
        new("ScrapHeliGibs_Part047", 3, new(0f, 0.670799971f, 0f), new(0f, 0f, 0f, 1f), new(0.6708f, 0.946f, 0.82f)),
        new("ScrapHeliGibs_Part046", 3, new(0.688000023f, 0.0430000015f, 0f), new(0f, 0f, -0.629320443f, 0.777145922f), new(0.5332f, 0.774f, 1.12f)),
        new("ScrapHeliGibs_Part046", 3, new(-0.688000023f, 0.0430000015f, 0f), new(0f, 0f, 0.629320323f, 0.777145982f), new(0.5332f, 0.774f, 1.12f)),
        new("ScrapHeliGibs_Part046", 3, new(0f, -0.619199991f, 0f), new(0f, 0f, -1f, 0f), new(0.516f, 0.774f, 1.12f)),
        new("ScrapHeliGibs_Part027", 3, new(0f, 0.963199973f, -0.100000001f), new(0f, 0f, 0f, 1f), new(1.2f, 1.075f, 1.075f)),
        new(null, 3, new(0.902999997f, 0.128999993f, 0f), new(0f, 0f, 0.707106709f, 0.707106769f), new(0.9f, 0.9f, 0.9f)),
        new("ScrapHeliGibs_Part043", 4, new(0f, 0f, 0f), new(0f, 0f, 0f, 1f), new(1.152f, 1.152f, 0.95f)),
        new("ScrapHeliGibs_Part047", 4, new(0f, 0.561600029f, 0f), new(0f, 0f, 0f, 1f), new(0.5616f, 0.792f, 0.82f)),
        new("ScrapHeliGibs_Part046", 4, new(0.575999975f, 0.0359999985f, 0f), new(0f, 0f, -0.629320443f, 0.777145922f), new(0.4464f, 0.648f, 1.12f)),
        new("ScrapHeliGibs_Part046", 4, new(-0.575999975f, 0.0359999985f, 0f), new(0f, 0f, 0.629320383f, 0.777145982f), new(0.4464f, 0.648f, 1.12f)),
        new("ScrapHeliGibs_Part046", 4, new(0f, -0.518400013f, 0f), new(0f, 0f, -1f, 0f), new(0.432f, 0.648f, 1.12f)),
        new("ScrapHeliGibs_Part027", 4, new(0f, 0.806400001f, -0.100000001f), new(0f, 0f, 0f, 1f), new(1.2f, 0.9f, 0.9f)),
        new(null, 4, new(-0.755999982f, 0.108000003f, 0f), new(0f, 0f, -0.707106769f, 0.707106769f), new(0.9f, 0.9f, 0.9f)),
        new("ScrapHeliGibs_Part043", 5, new(0f, 0f, 0f), new(0f, 0f, 0f, 1f), new(0.896f, 0.896f, 0.95f)),
        new("ScrapHeliGibs_Part047", 5, new(0f, 0.436800003f, 0f), new(0f, 0f, 0f, 1f), new(0.4368f, 0.616f, 0.82f)),
        new("ScrapHeliGibs_Part046", 5, new(0.448000014f, 0.0280000009f, 0f), new(0f, 0f, -0.629320443f, 0.777145922f), new(0.3472f, 0.504f, 1.12f)),
        new("ScrapHeliGibs_Part046", 5, new(-0.448000014f, 0.0280000009f, 0f), new(0f, 0f, 0.629320323f, 0.777145982f), new(0.3472f, 0.504f, 1.12f)),
        new("ScrapHeliGibs_Part046", 5, new(0f, -0.403200001f, 0f), new(0f, 0f, -1f, 0f), new(0.336f, 0.504f, 1.12f)),
        new("ScrapHeliGibs_Part027", 5, new(0f, 0.627200007f, -0.100000001f), new(0f, 0f, 0f, 1f), new(1.2f, 0.7f, 0.7f)),
        new("ScrapHeliGibs_Part043", 6, new(0f, 0f, 0f), new(0f, 0f, 0f, 1f), new(0.672f, 0.672f, 0.95f)),
        new("ScrapHeliGibs_Part047", 6, new(0f, 0.327600002f, 0f), new(0f, 0f, 0f, 1f), new(0.3276f, 0.462f, 0.82f)),
        new("ScrapHeliGibs_Part046", 6, new(0.335999995f, 0.0209999997f, 0f), new(0f, 0f, -0.629320443f, 0.777145922f), new(0.2604f, 0.378f, 1.12f)),
        new("ScrapHeliGibs_Part046", 6, new(-0.335999995f, 0.0209999997f, 0f), new(0f, 0f, 0.629320383f, 0.777145982f), new(0.2604f, 0.378f, 1.12f)),
        new("ScrapHeliGibs_Part046", 6, new(0f, -0.302399993f, 0f), new(0f, 0f, -1f, 0f), new(0.252f, 0.378f, 1.12f)),
        new("ScrapHeliGibs_Part027", 6, new(0f, 0.470400006f, -0.100000001f), new(0f, 0f, 0f, 1f), new(1.2f, 0.525f, 0.525f)),
        new("ScrapHeliGibs_Part033", 6, new(0f, 0f, -1.39999998f), new(0.707106769f, 0f, 0f, 0.707106769f), new(0.63f, 0.462f, 0.63f)),
    };

    private sealed class Settings
    {
        public float Scale = 1.6f;
        public float Speed = 9f;
        public float Height = 9f;
        public float Length = 38f;
        public float Depth = 5f;
        public float WormHealth = 3000f;
        public float BodyDamage = 0.3f;
        public float CoreDamage = 2f;
        public float CoreHealth = 400f;
        public float CoverDamageScale = 0.25f;
        public float CoverBaseRadius = 10f;
        public bool ScreenShake = true;
        public bool ShedArmor = true;
        public int Crates = 4;
        public string CrateType = "heli";
        public bool HackableCrate = true;
        public string AlphaLootProfile = "";
        public float LootLockSeconds = 0f;
        public bool HealthBar = true;
        public float HealthBarRadius = 150f;
        public float HealthBarScale = 0.75f;
        public bool DestroyBuildings = false;
        public bool SpareRaidableBases = true;
        public float RunOverDamage = 1000f;
        public bool ForceDamageThroughTruePVE = true;
        public float BuildingDamage = 250f;
        public bool HuntNoise = true;
        public float NoiseMemorySeconds = 30f;
        public float SlamRadius = 6f;
        public float SlamDamage = 45f;
        public float BiteRange = 14f;
        public float BiteDamage = 80f;
        public float TailRange = 5f;
        public float TailDamage = 30f;
        public float HealthPerExtraPlayer = 0.35f;
        public float MaxHealthMultiplier = 4f;
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<double> RewardEconomics = new() { 1000, 500, 250 };
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<int> RewardServerRewards = new() { 0, 0, 0 };
        public float MinRewardShare = 0.03f;
        public string DiscordWebhook = "";
        public string RoarSound = "";
        public float SoundRadius = 150f;
        public float RoarSoundPeak = 0f;
        public bool AutoStart = true;
        public float FirstStartMinutes = 20f;
        public float IntervalMinutes = 90f;
        public int MinPlayers = 1;
        public float LifetimeMinutes = 25f;
        public float IdleMinutes = 5f;
        public float IdleRadius = 250f;
        public int AutoTerritories = 3;
        public float TerritoryRadius = 300f;
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<TerritorySpec> Territories = new();
        public bool MapMarkers = true;
        public bool BuildWarning = true;
        public bool AnnounceGlobal = true;
        public bool RandomLocationEachEvent = false;
        public bool SurfaceMarker = true;
        public float SurfaceMarkerRadius = 60f;
        public bool BreachMessage = true;
        public bool UseNCP = false;
        public int NCPType = 0;
        public bool UseNotify = false;
        public int NotifyType = 0;
        public bool ChatWithNotifications = false;
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> WinnerCommands = new();
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<DebrisItem> DebrisLoot = new()
        {
            new DebrisItem { ShortName = "metal.refined", Amount = 7, MinPieceSize = 0.75f },
            new DebrisItem { ShortName = "metal.fragments", Amount = 150 },
            new DebrisItem { ShortName = "charcoal", Amount = 80 }
        };
    }

    private static class LangKeys
    {
        public const string Help = nameof(Help);
        public const string AdminOnly = nameof(AdminOnly);
        public const string NotReady = nameof(NotReady);
        public const string Occupied = nameof(Occupied);
        public const string OtherColossus = nameof(OtherColossus);
        public const string BadGround = nameof(BadGround);
        public const string Spawned = nameof(Spawned);
        public const string Missing = nameof(Missing);
        public const string Busy = nameof(Busy);
        public const string Breach = nameof(Breach);
        public const string Looping = nameof(Looping);
        public const string Stopped = nameof(Stopped);
        public const string Removed = nameof(Removed);
        public const string Failed = nameof(Failed);
        public const string Status = nameof(Status);
        public const string CoreDown = nameof(CoreDown);
        public const string Killed = nameof(Killed);
        public const string KilledBy = nameof(KilledBy);
        public const string LootLocked = nameof(LootLocked);
        public const string HudName = nameof(HudName);
        public const string HudExposed = nameof(HudExposed);
        public const string HudBurrowed = nameof(HudBurrowed);
        public const string HudHealth = nameof(HudHealth);
        public const string HudCores = nameof(HudCores);
        public const string EventStart = nameof(EventStart);
        public const string EventLeft = nameof(EventLeft);
        public const string BuildDanger = nameof(BuildDanger);
        public const string BuildInfo = nameof(BuildInfo);
        public const string TerritoryLine = nameof(TerritoryLine);
        public const string NoTerritory = nameof(NoTerritory);
        public const string RewardPaid = nameof(RewardPaid);
        public const string RandomPlaces = nameof(RandomPlaces);
        public const string ToastKilledBy = nameof(ToastKilledBy);
    }

    protected override void LoadDefaultConfig() => Config.WriteObject(new Settings(), true);

    protected override void LoadConfig()
    {
        base.LoadConfig();
        try
        {
            _settings = Config.ReadObject<Settings>() ?? new Settings();
            if (!InRange(_settings.Scale, 1f, 2.5f) || !InRange(_settings.Speed, 3f, 16f) || !InRange(_settings.Height, 2f, 14f) ||
                !InRange(_settings.Length, 14f, 60f) || !InRange(_settings.Depth, 2f, 8f) ||
                !InRange(_settings.CoreHealth, 50f, 10000f) || !InRange(_settings.WormHealth, 100f, 100000f) ||
                !InRange(_settings.BodyDamage, 0f, 1f) || !InRange(_settings.CoreDamage, 1f, 10f) || !InRange(_settings.CoverDamageScale, 0f, 1f) || !InRange(_settings.CoverBaseRadius, 0f, 50f) || _settings.Crates < 0 || _settings.Crates > 8 || CratePrefab(_settings.CrateType) is null ||
                !InRange(_settings.LootLockSeconds, 0f, 3600f) || _settings.AlphaLootProfile is null ||
                !InRange(_settings.HealthBarRadius, 10f, 1000f) || !InRange(_settings.HealthBarScale, 0.5f, 1.5f) || !InRange(_settings.RunOverDamage, 0f, 10000f) ||
                !InRange(_settings.BuildingDamage, 1f, 10000f) || !InRange(_settings.NoiseMemorySeconds, 5f, 300f) ||
                !InRange(_settings.SlamRadius, 2f, 20f) || !InRange(_settings.SlamDamage, 0f, 1000f) || !InRange(_settings.BiteRange, 0f, 40f) || !InRange(_settings.BiteDamage, 0f, 1000f) ||
                !InRange(_settings.TailRange, 0f, 20f) || !InRange(_settings.TailDamage, 0f, 1000f) || !InRange(_settings.HealthPerExtraPlayer, 0f, 5f) || !InRange(_settings.MaxHealthMultiplier, 1f, 20f) ||
                _settings.RewardEconomics is null || _settings.RewardEconomics.Count > 10 || _settings.RewardEconomics.Exists(static v => double.IsNaN(v) || v < 0 || v > 1e9) ||
                _settings.RewardServerRewards is null || _settings.RewardServerRewards.Count > 10 || _settings.RewardServerRewards.Exists(static v => v < 0) || !InRange(_settings.MinRewardShare, 0f, 1f) ||
                _settings.DiscordWebhook is null || _settings.DiscordWebhook.Length > 0 && !ValidWebhook(_settings.DiscordWebhook) || !InRange(_settings.FirstStartMinutes, 1f, 1440f) || !InRange(_settings.IntervalMinutes, 10f, 1440f) ||
                _settings.MinPlayers < 0 || _settings.MinPlayers > 1000 || !InRange(_settings.LifetimeMinutes, 2f, 180f) || !InRange(_settings.IdleMinutes, 1f, 60f) ||
                !InRange(_settings.IdleRadius, 50f, 1000f) || _settings.AutoTerritories < 0 || _settings.AutoTerritories > 10 || !InRange(_settings.TerritoryRadius, 100f, 1000f) ||
                BadTerritories(_settings.Territories) ||
                !InRange(_settings.SoundRadius, 30f, 500f) || !InRange(_settings.RoarSoundPeak, 0f, 30f) ||
                !InRange(_settings.SurfaceMarkerRadius, 20f, 500f) || _settings.NCPType < 0 || _settings.NCPType > 100 || _settings.NotifyType < 0 || _settings.NotifyType > 100 ||
                BadCommands(_settings.WinnerCommands) ||
                _settings.DebrisLoot is null || _settings.DebrisLoot.Count > 10 || _settings.DebrisLoot.Exists(static d => d is null || string.IsNullOrWhiteSpace(d.ShortName) || d.Amount < 0 || d.Amount > 100000 || d.AmountPerHit < 0 || d.AmountPerHit > 100000 || !InRange(d.MinPieceSize, 0f, 1f)))
                throw new InvalidOperationException("Scale 1-2.5, Speed 3-16, Height 2-14, Length 14-60, Depth 2-8, CoreHealth 50-10000, WormHealth 100-100000, BodyDamage 0-1, CoreDamage 1-10, CoverDamageScale 0-1, CoverBaseRadius 0-50, Crates 0-8, CrateType heli or bradley, LootLockSeconds 0-3600, AlphaLootProfile a string, HealthBarRadius 10-1000, HealthBarScale 0.5-1.5, RunOverDamage 0-10000, BuildingDamage 1-10000, NoiseMemorySeconds 5-300, SlamRadius 2-20, SlamDamage 0-1000, BiteRange 0-40, BiteDamage 0-1000, TailRange 0-20, TailDamage 0-1000, HealthPerExtraPlayer 0-5, MaxHealthMultiplier 1-20, RewardEconomics/RewardServerRewards up to 10 non-negative amounts, MinRewardShare 0-1, DiscordWebhook empty or a discord.com webhook URL, FirstStartMinutes 1-1440, IntervalMinutes 10-1440, MinPlayers 0-1000, LifetimeMinutes 2-180, IdleMinutes 1-60, IdleRadius 50-1000, AutoTerritories 0-10, TerritoryRadius 100-1000, Territories with a Name, X/Z inside the map and Radius 100-1000, SurfaceMarkerRadius 20-500, SoundRadius 30-500, RoarSoundPeak 0-30, NCPType/NotifyType 0-100, WinnerCommands up to 20 single-line commands of 512 characters, DebrisLoot up to 10 items with a ShortName, Amount and AmountPerHit 0-100000 and MinPieceSize 0-1; finite numbers only.");
        }
        catch (Exception ex)
        {
            _settings = null;
            _configBroken = true;
            PrintError("Configuration rejected; the original file was preserved: " + ex.Message);
            return;
        }
        try { Config.WriteObject(_settings, true); }
        catch (Exception ex) { PrintWarning("Could not write the configuration back: " + ex.Message); }
        float minDepth = 1.35f * _settings.Scale + 0.5f;
        if (_settings.Depth < minDepth)
        {
            PrintWarning($"Depth {_settings.Depth} is too shallow for Scale {_settings.Scale}: using {minDepth:0.##} so the buried worm does not show.");
            _settings.Depth = minDepth;
        }
    }

    private bool _configBroken;

    private static bool ValidWebhook(string url)
    {
        if (url.IndexOfAny(new[] { ' ', '"', '\\', '\n', '\r' }) >= 0) return false;
        foreach (string prefix in WebhookPrefixes) if (url.StartsWith(prefix, StringComparison.Ordinal) && url.Length > prefix.Length + 10) return true;
        return false;
    }

    private static bool BadTerritories(List<TerritorySpec> list)
        => list is null || list.Exists(static t => t is null || t.Name is null || !InRange(t.Radius, 100f, 1000f) || !InRange(t.X, -5000f, 5000f) || !InRange(t.Z, -5000f, 5000f));

    private static bool BadCommands(List<string> list)
        => list is null || list.Count > 20 || list.Exists(static c => c is null || c.Length > 512 || c.IndexOf('\n') >= 0 || c.IndexOf('\r') >= 0);

    private static bool InRange(float value, float min, float max)
        => !float.IsNaN(value) && !float.IsInfinity(value) && value >= min && value <= max;

    private static string CratePrefab(string type)
    {
        if (string.Equals(type, "heli", StringComparison.OrdinalIgnoreCase)) return HeliCratePrefab;
        if (string.Equals(type, "bradley", StringComparison.OrdinalIgnoreCase)) return BradleyCratePrefab;
        return null;
    }

    private void Init()
    {
        if (_configBroken)
        {
            Interface.Oxide.NextTick(() => { if (Manager?.GetPlugin(Name) == this) Interface.Oxide.UnloadPlugin(Name); });
            throw new InvalidOperationException("Configuration rejected (see the error above); fix oxide/config/ScrapWorm.json and reload the plugin.");
        }
        permission.RegisterPermission(AdminPermission, this);
        ToggleProtection(false);
        Unsubscribe(nameof(CanLootEntity));
        Unsubscribe(nameof(CanHackCrate));
        Unsubscribe(nameof(OnEntityBuilt));
        Unsubscribe(nameof(OnPlayerSleepEnded));
        Unsubscribe(nameof(OnEntityTakeDamage));
    }

    private void OnServerInitialized()
    {
        if (_settings is null) return;
        LoadLootLocks();
        CUI.Initialize();
        UIImages.Register(this, PortraitKey, ImageBase + "7d098bbda92fb853.png");
        UIImages.Register(this, CoreOnKey, ImageBase + "967f831bd67ab027.png");
        UIImages.Register(this, CoreOffKey, ImageBase + "28b1e9c662971474.png");
        UIImages.LoadAll(this);
        try
        {
            var source = GameManager.server.FindPrefab(GibPrefab)?.GetComponent<ServerGib>();
            if (source is null || source._gibSource is null) throw new InvalidOperationException("Missing ServerGib source");
            foreach (var renderer in source._gibSource.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh is not null) { _gibBounds[renderer.name] = mesh.bounds; _gibMeshes[renderer.name] = mesh; }
            }
            int cores = 0;
            for (int i = 0; i < Model.Length; i++)
            {
                if (Model[i].Gib is null) { cores++; continue; }
                if (!_gibBounds.ContainsKey(Model[i].Gib)) throw new InvalidOperationException("Missing ServerGib mesh: " + Model[i].Gib);
            }
            if (!_gibBounds.ContainsKey(AnchorGib)) throw new InvalidOperationException("Missing ServerGib mesh: " + AnchorGib);
            if (GameManager.server.FindPrefab(CorePrefab)?.GetComponent<CeilingLight>() is null) throw new InvalidOperationException("Missing core light prefab");
            if (GameManager.server.FindPrefab(CratePrefab(_settings.CrateType))?.GetComponent<LockedByEntCrate>() is null ||
                GameManager.server.FindPrefab(FireBallPrefab)?.GetComponent<FireBall>() is null ||
                GameManager.server.FindPrefab(HackCratePrefab)?.GetComponent<HackableLockedCrate>() is null)
                throw new InvalidOperationException("Missing crate or fire prefab");
            foreach (string effect in new[] { BlastEffect, SparkEffect, HitNotifyEffect, DirtEffect, QuakeEffect, StrongQuakeEffect, DiveEffect, EruptEffect, GrindEffect, RoarEffect, DeathEffect, BloodEffect })
                if (StringPool.Get(effect) == 0) throw new InvalidOperationException("Missing effect prefab: " + effect);
            _coreParts = new int[cores];
            _coreHealth = new float[cores];
            _coreBy = new Dictionary<ulong, float>[cores];
            for (int core = 0; core < cores; core++) _coreBy[core] = new Dictionary<ulong, float>();
            _coreBlast = new float[cores];
            _nextSpark = new float[cores];
            _coreHistory = new Vector3[cores * History];
            for (int i = 0, core = 0; i < Model.Length; i++) if (Model[i].Gib is null) _coreParts[core++] = i;
            _ready = Model.Length > 0 && Segments > 0 && cores > 0;
        }
        catch (Exception ex) { PrintError("Worm assets unavailable: " + ex.Message); }
        if (!_ready) return;
        LoadRoarSound();
        LoadWarned();
        ResolveDebrisLoot();
        if (_settings.RandomLocationEachEvent) Puts("Random location mode: Territories and AutoTerritories are ignored, every event picks a new spot away from monuments.");
        else SetupTerritories();
        LoadPendingWinners();
        if (_settings.BuildWarning && _territories.Count > 0) Subscribe(nameof(OnEntityBuilt));
        SyncSleepHook();
        RunPendingForAwake();
        if (_settings.UseNCP && Live(NCP, nameof(NCP)) is null) PrintWarning("UseNCP is on but NCP is not loaded: messages go to chat until it loads.");
        if (_settings.UseNotify && Live(Notify, nameof(Notify)) is null) PrintWarning("UseNotify is on but Notify is not loaded: messages go to chat until it loads.");
        if (_settings.RewardEconomics.Exists(static v => v > 0) && Live(Economics, nameof(Economics)) is null) PrintWarning("RewardEconomics has amounts but Economics is not loaded: that money is not paid until it loads.");
        if (_settings.RewardServerRewards.Exists(static v => v > 0) && Live(ServerRewards, nameof(ServerRewards)) is null) PrintWarning("RewardServerRewards has amounts but ServerRewards is not loaded: those points are not paid until it loads.");
        ScheduleEvent(_settings.FirstStartMinutes);
    }

    protected override void LoadDefaultMessages()
    {
        lang.RegisterMessages(new Dictionary<string, string>
        {
            [LangKeys.Help] = "/" + Command + " start | spawn | breach | loop | stop | status | territories | remove",
            [LangKeys.AdminOnly] = "Server administrators only.",
            [LangKeys.NotReady] = "The worm is unavailable. Check the server log for configuration errors.",
            [LangKeys.Occupied] = "A worm already exists. Remove it first.",
            [LangKeys.OtherColossus] = "Another Colossus is out. Wait until it is gone.",
            [LangKeys.BadGround] = "Find dry, fairly flat open ground: the breach needs a straight strip of land ahead of you.",
            [LangKeys.Spawned] = "Scrap worm placed underground 15 m ahead. Shoot it while it is above ground: the glowing cores take much more damage.",
            [LangKeys.Missing] = "There is no active worm.",
            [LangKeys.Busy] = "The worm is already breaching.",
            [LangKeys.Breach] = "The ground shakes...",
            [LangKeys.Looping] = "The worm keeps breaching forward until stop or remove.",
            [LangKeys.Stopped] = "The worm stops after this breach.",
            [LangKeys.Removed] = "Worm removed.",
            [LangKeys.Failed] = "Worm creation failed and its parts were removed. Check the server log.",
            [LangKeys.Status] = "Worm: {3:0}/{4:0} HP | Cores alive: {0}/{1} | Core HP: {2}",
            [LangKeys.CoreDown] = "A worm core burst! {0} left.",
            [LangKeys.Killed] = "The scrap worm is dead. Its crates are burning where it fell.",
            [LangKeys.KilledBy] = "The scrap worm is dead! Most damage: {0}. Its crates are burning where it fell.",
            [LangKeys.LootLocked] = "This loot belongs to {0}'s team for {1:0} more seconds.",
            [LangKeys.HudName] = "SCRAP WORM",
            [LangKeys.HudExposed] = "EXPOSED",
            [LangKeys.HudBurrowed] = "BURROWING",
            [LangKeys.HudHealth] = "HEALTH",
            [LangKeys.HudCores] = "CORES",
            [LangKeys.EventStart] = "The ground rumbles at {0}: the Scrap Worm is hunting in {1}!",
            [LangKeys.EventLeft] = "The Scrap Worm burrowed deep and left {0}.",
            [LangKeys.BuildDanger] = "This is Scrap Worm territory ({0}): it can tear through buildings here.",
            [LangKeys.BuildInfo] = "This is Scrap Worm territory ({0}): expect it to surface around here.",
            [LangKeys.TerritoryLine] = "{0}: {1}, radius {2:0} m",
            [LangKeys.NoTerritory] = "No worm territory could be placed on this map.",
            [LangKeys.RewardPaid] = "You placed #{0} in damage on the Scrap Worm: {1}",
            [LangKeys.RandomPlaces] = "Random location mode: the worm picks a new spot for every event.",
            [LangKeys.ToastKilledBy] = "The Scrap Worm is dead! Most damage: {0}"
        }, this);
        lang.RegisterMessages(new Dictionary<string, string>
        {
            [LangKeys.Help] = "/" + Command + " start | spawn | breach | loop | stop | status | territories | remove",
            [LangKeys.AdminOnly] = "Solo administradores del servidor.",
            [LangKeys.NotReady] = "El gusano no está disponible. Revisa el registro del servidor.",
            [LangKeys.Occupied] = "Ya existe un gusano. Elimínalo primero.",
            [LangKeys.OtherColossus] = "Ya hay otro Coloso afuera. Espera a que se vaya.",
            [LangKeys.BadGround] = "Busca terreno seco, abierto y bastante plano: la embestida necesita una franja recta delante de ti.",
            [LangKeys.Spawned] = "Gusano de chatarra colocado bajo tierra 15 m adelante. Dispárale mientras está fuera: los núcleos brillantes reciben mucho más daño.",
            [LangKeys.Missing] = "No hay un gusano activo.",
            [LangKeys.Busy] = "El gusano ya está saliendo.",
            [LangKeys.Breach] = "La tierra tiembla...",
            [LangKeys.Looping] = "El gusano sigue saliendo hacia adelante hasta stop o remove.",
            [LangKeys.Stopped] = "El gusano se detiene después de esta embestida.",
            [LangKeys.Removed] = "Gusano eliminado.",
            [LangKeys.Failed] = "Falló la creación y se eliminaron sus piezas. Revisa el registro del servidor.",
            [LangKeys.Status] = "Gusano: {3:0}/{4:0} HP | Núcleos vivos: {0}/{1} | Vida de núcleos: {2}",
            [LangKeys.CoreDown] = "¡Reventó un núcleo del gusano! Quedan {0}.",
            [LangKeys.Killed] = "El gusano de chatarra murió. Sus cajas arden donde cayó.",
            [LangKeys.KilledBy] = "¡El gusano de chatarra murió! Más daño: {0}. Sus cajas arden donde cayó.",
            [LangKeys.LootLocked] = "Este botín es del equipo de {0} durante {1:0} segundos más.",
            [LangKeys.HudName] = "GUSANO DE CHATARRA",
            [LangKeys.HudExposed] = "EXPUESTO",
            [LangKeys.HudBurrowed] = "BAJO TIERRA",
            [LangKeys.HudHealth] = "VIDA",
            [LangKeys.HudCores] = "NÚCLEOS",
            [LangKeys.EventStart] = "La tierra tiembla en {0}: ¡el Gusano de chatarra está cazando en {1}!",
            [LangKeys.EventLeft] = "El Gusano de chatarra se hundió y dejó {0}.",
            [LangKeys.BuildDanger] = "Esto es territorio del Gusano de chatarra ({0}): aquí puede destrozar construcciones.",
            [LangKeys.BuildInfo] = "Esto es territorio del Gusano de chatarra ({0}): suele salir por aquí.",
            [LangKeys.TerritoryLine] = "{0}: {1}, radio {2:0} m",
            [LangKeys.NoTerritory] = "No se pudo colocar ningún territorio del gusano en este mapa.",
            [LangKeys.RewardPaid] = "Quedaste #{0} en daño al Gusano de chatarra: {1}",
            [LangKeys.RandomPlaces] = "Modo de ubicación aleatoria: el gusano elige un lugar nuevo en cada evento.",
            [LangKeys.ToastKilledBy] = "¡El gusano de chatarra murió! Más daño: {0}"
        }, this, "es");
        lang.RegisterMessages(new Dictionary<string, string>
        {
            [LangKeys.Help] = "/" + Command + " start | spawn | breach | loop | stop | status | territories | remove",
            [LangKeys.AdminOnly] = "Apenas administradores do servidor.",
            [LangKeys.NotReady] = "O verme não está disponível. Verifique o log do servidor.",
            [LangKeys.Occupied] = "Já existe um verme. Remova-o primeiro.",
            [LangKeys.OtherColossus] = "Já há outro Colosso solto. Espere até ele sumir.",
            [LangKeys.BadGround] = "Procure um terreno seco, aberto e bem plano: a investida precisa de uma faixa reta à sua frente.",
            [LangKeys.Spawned] = "Verme de sucata colocado sob a terra 15 m à frente. Atire nele enquanto estiver fora: os núcleos brilhantes recebem muito mais dano.",
            [LangKeys.Missing] = "Não há nenhum verme ativo.",
            [LangKeys.Busy] = "O verme já está saindo.",
            [LangKeys.Breach] = "A terra treme...",
            [LangKeys.Looping] = "O verme continua saindo para frente até stop ou remove.",
            [LangKeys.Stopped] = "O verme para depois desta investida.",
            [LangKeys.Removed] = "Verme removido.",
            [LangKeys.Failed] = "A criação do verme falhou e suas peças foram removidas. Verifique o log do servidor.",
            [LangKeys.Status] = "Verme: {3:0}/{4:0} HP | Núcleos vivos: {0}/{1} | Vida dos núcleos: {2}",
            [LangKeys.CoreDown] = "Um núcleo do verme estourou! Restam {0}.",
            [LangKeys.Killed] = "O verme de sucata morreu. Suas caixas queimam onde ele caiu.",
            [LangKeys.KilledBy] = "O verme de sucata morreu! Maior dano: {0}. Suas caixas queimam onde ele caiu.",
            [LangKeys.LootLocked] = "Este saque pertence à equipe de {0} por mais {1:0} segundos.",
            [LangKeys.HudName] = "VERME DE SUCATA",
            [LangKeys.HudExposed] = "EXPOSTO",
            [LangKeys.HudBurrowed] = "ENTERRADO",
            [LangKeys.HudHealth] = "VIDA",
            [LangKeys.HudCores] = "NÚCLEOS",
            [LangKeys.EventStart] = "A terra treme em {0}: o Verme de sucata está caçando em {1}!",
            [LangKeys.EventLeft] = "O Verme de sucata se enterrou e deixou {0}.",
            [LangKeys.BuildDanger] = "Este é o território do Verme de sucata ({0}): aqui ele pode destruir construções.",
            [LangKeys.BuildInfo] = "Este é o território do Verme de sucata ({0}): ele costuma sair por aqui.",
            [LangKeys.TerritoryLine] = "{0}: {1}, raio {2:0} m",
            [LangKeys.NoTerritory] = "Não foi possível colocar nenhum território do verme neste mapa.",
            [LangKeys.RewardPaid] = "Você ficou em #{0} no dano ao Verme de sucata: {1}",
            [LangKeys.RandomPlaces] = "Modo de local aleatório: o verme escolhe um lugar novo a cada evento.",
            [LangKeys.ToastKilledBy] = "O verme de sucata morreu! Maior dano: {0}"
        }, this, "pt-BR");
        lang.RegisterMessages(new Dictionary<string, string>
        {
            [LangKeys.Help] = "/" + Command + " start | spawn | breach | loop | stop | status | territories | remove",
            [LangKeys.AdminOnly] = "Только для администраторов сервера.",
            [LangKeys.NotReady] = "Червь недоступен. Проверьте лог сервера.",
            [LangKeys.Occupied] = "Червь уже существует. Сначала удалите его.",
            [LangKeys.OtherColossus] = "Другой Колосс уже на свободе. Дождитесь, пока он исчезнет.",
            [LangKeys.BadGround] = "Найдите сухую, открытую и достаточно ровную землю: для броска нужна прямая полоса перед вами.",
            [LangKeys.Spawned] = "Мусорный червь появился под землёй в 15 м впереди. Стреляйте, пока он снаружи: светящиеся ядра получают гораздо больше урона.",
            [LangKeys.Missing] = "Активного червя нет.",
            [LangKeys.Busy] = "Червь уже выходит.",
            [LangKeys.Breach] = "Земля дрожит...",
            [LangKeys.Looping] = "Червь продолжает выходить вперёд до stop или remove.",
            [LangKeys.Stopped] = "Червь остановится после этого броска.",
            [LangKeys.Removed] = "Червь удалён.",
            [LangKeys.Failed] = "Не удалось создать червя, его части удалены. Проверьте лог сервера.",
            [LangKeys.Status] = "Червь: {3:0}/{4:0} HP | Живых ядер: {0}/{1} | Здоровье ядер: {2}",
            [LangKeys.CoreDown] = "Ядро червя лопнуло! Осталось: {0}.",
            [LangKeys.Killed] = "Мусорный червь мёртв. Его ящики горят там, где он упал.",
            [LangKeys.KilledBy] = "Мусорный червь мёртв! Больше всех урона: {0}. Его ящики горят там, где он упал.",
            [LangKeys.LootLocked] = "Эта добыча принадлежит команде {0} ещё {1:0} сек.",
            [LangKeys.HudName] = "МУСОРНЫЙ ЧЕРВЬ",
            [LangKeys.HudExposed] = "СНАРУЖИ",
            [LangKeys.HudBurrowed] = "ПОД ЗЕМЛЁЙ",
            [LangKeys.HudHealth] = "ЗДОРОВЬЕ",
            [LangKeys.HudCores] = "ЯДРА",
            [LangKeys.EventStart] = "Земля дрожит в {0}: Мусорный червь охотится в {1}!",
            [LangKeys.EventLeft] = "Мусорный червь ушёл под землю и покинул {0}.",
            [LangKeys.BuildDanger] = "Это территория Мусорного червя ({0}): здесь он может разрушать постройки.",
            [LangKeys.BuildInfo] = "Это территория Мусорного червя ({0}): он часто появляется здесь.",
            [LangKeys.TerritoryLine] = "{0}: {1}, радиус {2:0} м",
            [LangKeys.NoTerritory] = "На этой карте не удалось разместить ни одной территории червя.",
            [LangKeys.RewardPaid] = "Вы заняли #{0} место по урону Мусорному червю: {1}",
            [LangKeys.RandomPlaces] = "Режим случайного места: червь выбирает новое место для каждого события.",
            [LangKeys.ToastKilledBy] = "Мусорный червь мёртв! Больше всех урона: {0}"
        }, this, "ru");
    }

    private string[] _langFolders;

    private string Msg(string key, BasePlayer player)
        => lang.GetMessageByLanguage(key, this, LangOf(player));

    private static string Fmt(string text, params object[] args)
    {
        if (args.Length == 0) return text;
        try { return string.Format(text, args); }
        catch (FormatException) { return text; }
    }

    private static string PlainName(string name) => name.Replace("<", "").Replace(">", "");

    private static string DiscordSafe(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            if ("\\*_~`|[]()<>#-".IndexOf(c) >= 0) builder.Append('\\');
            builder.Append(c);
            if (c == '@') builder.Append((char)0x200B);
        }
        return builder.ToString();
    }

    private string LangOf(BasePlayer player)
    {
        string code = player is null ? null : lang.GetLanguage(player.UserIDString);
        return LangFile(code) ?? LangFile(lang.GetServerLanguage()) ?? "en";
    }

    private string LangFile(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        string[] files = _langFolders ??= lang.GetLanguages(this);
        int dash = code.IndexOf('-');
        string baseCode = dash > 0 ? code.Substring(0, dash) : code;
        string regional = null;
        foreach (string file in files)
        {
            if (string.Equals(file, code, StringComparison.OrdinalIgnoreCase)) return file;
            if (string.Equals(file, baseCode, StringComparison.OrdinalIgnoreCase)) regional = file;
            else if (regional is null && file.StartsWith(baseCode + "-", StringComparison.OrdinalIgnoreCase)) regional = file;
        }
        return regional;
    }

    private void Reply(BasePlayer player, string key) => SendReply(player, Msg(key, player));

    private Plugin Live(Plugin cached, string name)
    {
        if (cached is { IsLoaded: true }) return cached;
        return plugins.Find(name) is { IsLoaded: true } found ? found : null;
    }

    private bool Toast(BasePlayer player, string message)
    {
        bool shown = false;
        Plugin ncp = _settings.UseNCP ? Live(NCP, nameof(NCP)) : null;
        if (ncp is not null)
        {
            if (ncp.Call("SendNotifyEx", player, _settings.NCPType, "", message, "", 0) is bool accepted) shown = accepted;
            else { ncp.Call("SendNotify", player, _settings.NCPType, message); shown = true; }
        }
        Plugin notify = _settings.UseNotify ? Live(Notify, nameof(Notify)) : null;
        if (notify is not null && permission.UserHasPermission(player.UserIDString, NotifySeePermission))
        {
            notify.Call("SendNotify", player, _settings.NotifyType, message);
            shown = true;
        }
        return shown;
    }

    private void Tell(BasePlayer player, string text, string toast = null)
    {
        bool shown = !player.IsDead() && Toast(player, toast ?? text);
        if (!shown || _settings.ChatWithNotifications) SendReply(player, text);
    }

    private void NotifyNearby(string key, Vector3 center, params object[] args)
    {
        foreach (BasePlayer player in BasePlayer.activePlayerList)
        {
            if (player is null || !player.IsConnected || !Near(player.transform.position, center, NoticeRadius)) continue;
            Tell(player, Fmt(Msg(key, player), args));
        }
    }

    private void NotifyNearbyWithToast(string key, string toastKey, Vector3 center, string arg)
    {
        foreach (BasePlayer player in BasePlayer.activePlayerList)
        {
            if (player is null || !player.IsConnected || !Near(player.transform.position, center, NoticeRadius)) continue;
            Tell(player, Fmt(Msg(key, player), arg), Fmt(Msg(toastKey, player), arg));
        }
    }

    private static bool Near(Vector3 a, Vector3 b, float radius)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return dx * dx + dz * dz <= radius * radius;
    }

    private static void SendLocal(BasePlayer player, string prefab, Vector3 position)
    {
        if (player is null || !player.IsConnected) return;
        Sfx.Init(Effect.Type.Generic, position, Vector3.up);
        Sfx.pooledString = prefab;
        EffectNetwork.Send(Sfx, player.net.connection);
    }

    private static bool SafeSoundName(string name)
    {
        foreach (char c in name)
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') return false;
        return name.Length <= 64;
    }

    private const string RoarKey = "roar";
    private const string SoundExtension = "CrashExtensions";
    private const float RoarKeepFactor = 1.25f;
    private static readonly VersionNumber SoundFloor = new(1, 12, 16);

    private void LoadRoarSound()
    {
        DropRoar();
        string name = _settings.RoarSound?.Trim() ?? "";
        if (name.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
        if (name.Length == 0) return;
        if (!SafeSoundName(name)) { PrintWarning($"Custom roar off, using the vanilla roar: RoarSound must be letters, digits, _ or - (up to 64), got '{_settings.RoarSound}'"); return; }
        VersionNumber? loaded = Interface.Oxide.GetExtension(SoundExtension)?.Version;
        if (loaded is null || loaded.Value < SoundFloor)
        {
            PrintWarning($"Custom roar off, using the vanilla roar: it needs {SoundExtension} {SoundFloor} or newer and this server is running {(loaded is null ? "none" : loaded.Value.ToString())}. Put {SoundExtension} {SoundFloor} or newer in place and restart the server (a DLL replaced on disk loads only at the next restart).");
            return;
        }
        string path = System.IO.Path.Combine(Interface.Oxide.DataDirectory, "ScrapWorm", "sounds", name + ".ogg");
        try
        {
            if (!RegisterRoar(path, out float seconds, out string error)) { PrintWarning($"Custom roar off, using the vanilla roar: {error}"); return; }
            _roarOn = true;
            _roarSeconds = seconds;
            Puts($"Custom roar loaded: {name}.ogg ({seconds:0.0} s), heard within {_settings.SoundRadius} m" + (_settings.RoarSoundPeak > 0f ? $", peak at {_settings.RoarSoundPeak:0.00} s" : ""));
            if (_settings.RoarSoundPeak >= seconds) PrintWarning($"RoarSoundPeak {_settings.RoarSoundPeak} s is past the end of the {seconds:0.0} s clip");
        }
        catch (Exception ex) { PrintWarning($"Custom roar off, using the vanilla roar: CUISound is not usable in the loaded {SoundExtension} {loaded.Value} ({ex.GetType().Name}: {ex.Message})"); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool RegisterRoar(string path, out float seconds, out string error)
    {
        seconds = 0f;
        if (!CUISound.Register(this, RoarKey, path, out error)) return false;
        seconds = CUISound.Length(this, RoarKey);
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool ListenRoar(Vector3 center, Vector3? source, out string broken)
    {
        if (CUISound.IsBroken(this, RoarKey, out broken)) return false;
        CUISound.Listen(this, RoarKey, center, _settings.SoundRadius, RoarKeepFactor);
        if (source.HasValue) CUISound.MoveSource(this, RoarKey, source.Value);
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool CuiPlayRoar(BasePlayer player, Vector3 source) => CUISound.Play(this, player, RoarKey, source);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool RoarPlaying(BasePlayer player) => CUISound.IsPlaying(this, player, RoarKey);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReleaseRoar() => CUISound.Release(this, RoarKey);

    private void TickSound(float now)
    {
        if (!_roarOn) return;
        try
        {
            Vector3 center = _origin + _heading * (_arcLength * 0.5f);
            Vector3? source = _breaching && _parts.Count > 0 ? (now < _warnUntil ? EntryPoint + Vector3.up * 4f : _segmentHistory[_historyCursor]) : null;
            if (ListenRoar(center, source, out string broken)) { _roarListening = true; return; }
            PrintWarning("Custom roar off, back to the vanilla roar until reload: " + broken);
            DropRoar();
        }
        catch (Exception ex) { PrintError("Custom roar failed, back to the vanilla roar: " + ex.Message); DropRoar(); }
    }

    private bool PlayRoar(BasePlayer player, Vector3 source)
    {
        if (!_roarOn) return false;
        try { return CuiPlayRoar(player, source) || RoarPlaying(player); }
        catch (Exception ex) { PrintError("Custom roar failed, back to the vanilla roar: " + ex.Message); DropRoar(); return false; }
    }

    private bool SafePlaying(BasePlayer player)
    {
        if (!_roarOn) return false;
        try { return RoarPlaying(player); }
        catch (Exception ex) { PrintError("Custom roar failed, back to the vanilla roar: " + ex.Message); DropRoar(); return false; }
    }

    private void Roar(Vector3 source)
    {
        _roarAt = 0f;
        if (!_roarOn) { Effect.server.Run(RoarEffect, source, Vector3.up); return; }
        bool early = _settings.RoarSoundPeak > 0f && _settings.RoarSoundPeak < _roarSeconds;
        foreach (BasePlayer player in BasePlayer.activePlayerList)
        {
            if (player is null || !player.IsConnected || !Near(player.transform.position, source, _settings.SoundRadius)) continue;
            if (early)
            {
                if (!_roarEarly.Contains(player.userID.Get()) || !SafePlaying(player)) SendLocal(player, RoarEffect, source);
                continue;
            }
            if (!PlayRoar(player, source)) SendLocal(player, RoarEffect, source);
        }
        if (early) _roarEarly.Clear();
    }

    private void ScheduleEarlyRoar(float now)
    {
        _roarEarly.Clear();
        _roarAt = 0f;
        if (!_roarOn || _settings.RoarSoundPeak <= 0f || _settings.RoarSoundPeak >= _roarSeconds) return;
        _roarAt = now + Mathf.Max(0f, WarnSeconds + SecondsToEruption() - _settings.RoarSoundPeak);
    }

    private float SecondsToEruption()
    {
        float entryCross = _settings.Depth / (_arcHeight + _settings.Depth) * _arcLength / Mathf.PI;
        float headS = -1f, t = 0f;
        while (SegmentS(0, headS) < entryCross && t < 10f)
        {
            headS += PaceAt(headS) * TickSeconds;
            t += TickSeconds;
        }
        return t;
    }

    private void EarlyRoar()
    {
        if (!_roarOn) return;
        Vector3 source = EntryPoint + Vector3.up * 4f;
        foreach (BasePlayer player in BasePlayer.activePlayerList)
        {
            if (player is null || !player.IsConnected || !Near(player.transform.position, source, _settings.SoundRadius)) continue;
            if (PlayRoar(player, source)) _roarEarly.Add(player.userID.Get());
            if (!_roarOn) { _roarEarly.Clear(); return; }
        }
    }

    private void RetireRoar()
    {
        if (!_roarOn || !_roarListening) return;
        _roarListening = false;
        try { ReleaseRoar(); }
        catch (Exception ex) { PrintError("Custom roar cleanup failed: " + ex.Message); DropRoar(); }
    }

    private void DropRoar()
    {
        _roarAt = 0f;
        _roarOn = _roarListening = false;
    }

    [ChatCommand(Command)]
    private void WormCommand(BasePlayer player, string command, string[] args)
    {
        if (!CanManage(player)) { Reply(player, LangKeys.AdminOnly); return; }
        if (!_ready) { Reply(player, LangKeys.NotReady); return; }
        if (args.Length != 1) { Reply(player, LangKeys.Help); return; }
        switch (args[0].ToLowerInvariant())
        {
            case "start":
                if (_parts.Count > 0) { Reply(player, LangKeys.Occupied); return; }
                if (OtherColossusActive()) { Reply(player, LangKeys.OtherColossus); return; }
                if (!HasPlaces) { Reply(player, LangKeys.NoTerritory); return; }
                if (!StartEvent()) Reply(player, LangKeys.BadGround);
                return;
            case "territories":
                if (_settings.RandomLocationEachEvent) { Reply(player, LangKeys.RandomPlaces); return; }
                if (_territories.Count == 0) { Reply(player, LangKeys.NoTerritory); return; }
                foreach (Territory territory in _territories)
                    SendReply(player, Fmt(Msg(LangKeys.TerritoryLine, player), territory.Name, MapHelper.PositionToString(territory.Center), territory.Radius));
                return;
            case "spawn":
                if (_parts.Count > 0) { Reply(player, LangKeys.Occupied); return; }
                if (OtherColossusActive()) { Reply(player, LangKeys.OtherColossus); return; }
                Vector3 heading = Quaternion.Euler(0f, player.eyes.rotation.eulerAngles.y, 0f) * Vector3.forward;
                RollArc();
                if (!TryPlaceArc(player.transform.position + heading * 15f, heading, null, !_settings.DestroyBuildings)) { Reply(player, LangKeys.BadGround); return; }
                _territory = FindTerritory(player.transform.position) ?? new Territory { Name = MapHelper.PositionToString(player.transform.position), Center = player.transform.position, Radius = _settings.TerritoryRadius, Temporary = true };
                _eventEndsAt = 0f;
                try
                {
                    SpawnWorm();
                    Reply(player, LangKeys.Spawned);
                }
                catch (Exception ex)
                {
                    RemoveWorm();
                    PrintError("Worm spawn rolled back: " + ex);
                    Reply(player, LangKeys.Failed);
                }
                return;
            case "breach":
                if (_parts.Count == 0) { Reply(player, LangKeys.Missing); return; }
                if (_breaching) { Reply(player, LangKeys.Busy); return; }
                if (!TryNextArc()) { Reply(player, LangKeys.BadGround); return; }
                StartBreach();
                return;
            case "loop":
                if (_parts.Count == 0) { Reply(player, LangKeys.Missing); return; }
                _loop = true;
                _resumeAt = 0f;
                Reply(player, LangKeys.Looping);
                return;
            case "stop":
                if (_parts.Count == 0) { Reply(player, LangKeys.Missing); return; }
                _loop = false;
                Reply(player, LangKeys.Stopped);
                return;
            case "status":
                if (_parts.Count == 0) { Reply(player, LangKeys.Missing); return; }
                SendReply(player, Fmt(Msg(LangKeys.Status, player), _coresAlive, _coreHealth.Length, string.Join(", ", Array.ConvertAll(_coreHealth, static hp => hp.ToString("0"))), _health, _maxHealth));
                return;
            case "remove":
                RemoveWorm();
                Reply(player, LangKeys.Removed);
                return;
            default:
                Reply(player, LangKeys.Help);
                return;
        }
    }

    private bool CanManage(BasePlayer player)
        => player is not null && (player.Connection is { authLevel: >= 2 } || permission.UserHasPermission(player.UserIDString, AdminPermission));

    [ConsoleCommand(StartCommand)]
    private void StartConsole(ConsoleSystem.Arg arg)
    {
        if (arg.Connection is not null && !CanManage(arg.Player())) return;
        if (!_ready) { arg.ReplyWith("Scrap Worm is not ready; check the log."); return; }
        if (_parts.Count > 0) { arg.ReplyWith("A worm already exists."); return; }
        if (OtherColossusActive()) { arg.ReplyWith("Another Colossus is out; wait until it is gone."); return; }
        arg.ReplyWith(StartEvent() ? $"Scrap Worm event started in {_territory?.Name} at {MapHelper.PositionToString(_origin)}." : "No territory had room for a breach.");
    }

    [ConsoleCommand(TerritoriesCommand)]
    private void TerritoriesConsole(ConsoleSystem.Arg arg)
    {
        if (arg.Connection is not null && !CanManage(arg.Player())) return;
        if (_settings.RandomLocationEachEvent) { arg.ReplyWith("Random location mode: every event picks a new spot away from monuments."); return; }
        var lines = new List<string>(_territories.Count + 1) { $"{_territories.Count} worm territories:" };
        foreach (Territory territory in _territories) lines.Add($"{territory.Name}: {territory.Center.x:0} {territory.Center.z:0}, radius {territory.Radius:0} m");
        arg.ReplyWith(string.Join(Environment.NewLine, lines));
    }

    private void RollArc()
    {
        float roll = UnityEngine.Random.value;
        float height = roll < 0.3f ? UnityEngine.Random.Range(0.35f, 0.6f) : roll < 0.75f ? UnityEngine.Random.Range(0.8f, 1.1f) : UnityEngine.Random.Range(1.25f, 1.5f);
        _arcHeight = Mathf.Clamp(_settings.Height * height, 2.5f, 16f);
        _arcLength = Mathf.Clamp(_settings.Length * UnityEngine.Random.Range(0.8f, 1.1f) * (0.75f + 0.25f * height), 18f, 70f);
        _arcSpeed = _settings.Speed * UnityEngine.Random.Range(0.8f, 1.25f);
    }

    private float PaceAt(float headS)
    {
        float phase = Mathf.Clamp01(headS / _arcLength);
        return _arcSpeed * (1f + 0.3f * Mathf.Sin(phase * Mathf.PI * 3f));
    }

    private bool TryPlaceArc(Vector3 start, Vector3 heading, Territory bounds, bool avoidBuildings)
    {
        if (TerrainMeta.HeightMap is null) return false;
        heading.y = 0f;
        if (heading.sqrMagnitude < 0.01f) return false;
        heading.Normalize();
        if (bounds is not null && !Near(start + heading * _arcLength, bounds.Center, bounds.Radius)) return false;
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i <= 10; i++)
        {
            Vector3 point = start + heading * (_arcLength * i / 10f);
            float height = TerrainMeta.HeightMap.GetHeight(point);
            point.y = height;
            if (Flooded(point)) return false;
            min = Mathf.Min(min, height);
            max = Mathf.Max(max, height);
        }
        if (max - min > MaxGroundVariation) return false;
        if (bounds is not null && NearMonument(start, start + heading * _arcLength)) return false;
        if (ArcNearRaidBase(start, heading)) { _raidBlockedStarts++; return false; }
        if (avoidBuildings)
        {
            float sweep = Mathf.Max(SegmentRadius(0), _arcLength * 0.05f + 1f), rise = _arcHeight + _settings.Depth;
            for (int i = 0; i <= 10; i++)
            {
                Vector3 point = start + heading * (_arcLength * i / 10f);
                point.y = TerrainMeta.HeightMap.GetHeight(point);
                _buildingTests++;
                if (BuildingAt(point, rise * Mathf.Sin(Mathf.PI * i / 10f) - _settings.Depth, sweep)) return false;
            }
        }
        _origin = new Vector3(start.x, min, start.z);
        _heading = heading;
        return true;
    }

    private static bool Flooded(Vector3 ground) => WaterLevel.Test(ground + Vector3.up * 0.5f, false, false);

    private bool BuildingAt(Vector3 ground, float top, float radius)
    {
        int count = Physics.OverlapCapsuleNonAlloc(ground + Vector3.up, ground + Vector3.up * Mathf.Max(1f, top), radius, CrushHits, CrushMask, QueryTriggerInteraction.Ignore);
        bool found = false;
        for (int i = 0; i < count && !found; i++) found = IsBase(CrushHits[i]);
        Array.Clear(CrushHits, 0, count);
        return found || CommunityEntity.ServerInstance is { IsDestroyed: false } probe && Privileged(probe, ground);
    }

    private bool BaseNear(Vector3 at)
    {
        float radius = _settings.CoverBaseRadius;
        if (radius <= 0f) return false;
        int count = Physics.OverlapSphereNonAlloc(at, radius, CrushHits, CrushMask, QueryTriggerInteraction.Ignore);
        bool found = false;
        for (int i = 0; i < count && !found; i++) found = IsBase(CrushHits[i]);
        Array.Clear(CrushHits, 0, count);
        return found;
    }

    private bool IsBase(Collider hit)
        => hit.ToBaseEntity() is DecayEntity { IsDestroyed: false } piece && !_owned.Contains(piece) &&
           (piece is BuildingBlock || piece.OwnerID != 0 && piece.buildingID != 0 && piece.GetBuilding() is { } building && building.HasBuildingBlocks());

    private static bool NearMonument(Vector3 from, Vector3 to)
    {
        if (TerrainMeta.Path is null) return false;
        float dx = to.x - from.x, dz = to.z - from.z, length = dx * dx + dz * dz;
        foreach (MonumentInfo monument in TerrainMeta.Path.Monuments)
        {
            if (monument is null) continue;
            Vector3 at = monument.transform.position;
            float t = length > 0f ? Mathf.Clamp01(((at.x - from.x) * dx + (at.z - from.z) * dz) / length) : 0f;
            float ex = from.x + dx * t - at.x, ez = from.z + dz * t - at.z;
            float clear = Mathf.Max(monument.Bounds.extents.x, monument.Bounds.extents.z) + MonumentClearance;
            if (ex * ex + ez * ez < clear * clear) return true;
        }
        return false;
    }

    private bool TryNextArc()
    {
        Vector3 exit = _origin + _heading * _arcLength;
        Vector3 heading = _heading;
        float length = _arcLength, height = _arcHeight, speed = _arcSpeed;
        RollArc();
        _slamArmed = false;
        _buildingTests = 0;
        if (_settings.HuntNoise && Time.realtimeSinceStartup - _noiseAt <= _settings.NoiseMemorySeconds)
        {
            Vector3 toward = _noise - exit;
            toward.y = 0f;
            if (toward.sqrMagnitude < 4f) toward = heading;
            if (TryNoiseArc(toward.normalized)) { _noiseAt = float.NegativeInfinity; _slamArmed = true; return true; }
        }
        bool avoidBuildings = !_settings.DestroyBuildings;
        if (_territory is not null && !Near(exit, _territory.Center, _territory.Radius * 0.75f))
        {
            Vector3 home = _territory.Center - exit;
            home.y = 0f;
            if (home.sqrMagnitude > 1f && TryPlaceArc(exit + home.normalized * 4f, home, _territory, avoidBuildings)) return true;
        }
        float sign = UnityEngine.Random.value < 0.5f ? -1f : 1f;
        float first = UnityEngine.Random.Range(15f, 75f);
        for (int i = 0; i < 6; i++)
        {
            float angle = i switch { 0 => first * sign, 1 => -first * sign, 2 => 100f * sign, 3 => -100f * sign, 4 => 0f, _ => 180f };
            Vector3 turned = Quaternion.Euler(0f, angle, 0f) * heading;
            if (TryPlaceArc(exit + turned * 4f, turned, _territory, avoidBuildings)) return true;
        }
        _arcLength = length; _arcHeight = height; _arcSpeed = speed;
        return false;
    }

    private bool TryNoiseArc(Vector3 toward)
    {
        bool middle = !NearRaidBase(_noise, _arcLength * 0.25f + RaidBaseStartMargin);
        for (int i = 0; middle && i < 6; i++)
        {
            if (_buildingTests >= NoiseTestBudget) return false;
            Vector3 aim = Quaternion.Euler(0f, i switch { 0 => 0f, 1 => 35f, 2 => -35f, 3 => 70f, 4 => -70f, _ => 180f }, 0f) * toward;
            if (TryPlaceArc(_noise, aim, _territory, true)) return true;
        }
        float turn = UnityEngine.Random.Range(0f, 360f);
        for (int i = 0; i < 12; i++)
        {
            Vector3 side = Quaternion.Euler(0f, turn + i * 60f + (i < 6 ? 0f : 30f), 0f) * Vector3.forward;
            Vector3 start = _noise + side * (i < 6 ? NoiseRingMeters : NoiseRingMeters * 2f);
            if (_territory is not null && !Near(start, _territory.Center, _territory.Radius)) continue;
            if (_buildingTests >= NoiseTestBudget) return false;
            if (TryPlaceArc(start, new Vector3(-side.z, 0f, side.x), _territory, true)) return true;
            if (_buildingTests >= NoiseTestBudget) return false;
            if (TryPlaceArc(start, side, _territory, true)) return true;
        }
        return false;
    }

    private void CurvePoint(float s, out Vector3 position, out Quaternion rotation)
    {
        float length = _arcLength, depth = _settings.Depth, rise = _arcHeight + depth, k = Mathf.PI / length;
        float y, dy;
        if (s >= 0f && s <= length) { y = rise * Mathf.Sin(k * s) - depth; dy = rise * k * Mathf.Cos(k * s); }
        else if (s < 0f) { y = -depth + 0.3f * s; dy = 0.3f; }
        else { y = -depth - 0.3f * (s - length); dy = -0.3f; }
        Vector3 tangent = (_heading + Vector3.up * dy).normalized;
        Vector3 normal = Vector3.Cross(tangent, Vector3.Cross(Vector3.up, _heading));
        position = _origin + _heading * s + Vector3.up * y;
        rotation = Quaternion.LookRotation(tangent, normal);
    }

    private float SegmentS(int segment, float headS) => headS - segment * Spacing * _settings.Scale;

    private Vector3 EntryPoint => _origin;

    private Vector3 ExitPoint => _origin + _heading * _arcLength;

    private void SpawnWorm()
    {
        ToggleProtection(true);
        _dead = false;
        _killQueued = false;
        _life++;
        _headS = -1f;
        for (int segment = 0; segment < Segments; segment++)
        {
            CurvePoint(SegmentS(segment, _headS), out _framePos[segment], out _frameRot[segment]);
            BaseEntity anchor = SpawnPiece(GibPrefab, AnchorGib, _framePos[segment], _frameRot[segment], Vector3.one * AnchorScale, false);
            _anchors[segment] = anchor;
            if (segment > 0) anchor.SetParent(_anchors[segment - 1], true, true);
        }
        for (int i = 0; i < Model.Length; i++)
        {
            int segment = Model[i].Segment;
            Vector3 frame = _framePos[segment];
            Quaternion rotation = _frameRot[segment];
            BaseEntity part = SpawnPiece(Model[i].Gib is null ? CorePrefab : GibPrefab, Model[i].Gib, frame + rotation * (Model[i].Position * _settings.Scale), rotation * Model[i].Rotation, Model[i].Scale * _settings.Scale, false);
            _parts.Add(part);
            part.SetParent(_anchors[segment], true, true);
        }
        Regroup();
        _coresAlive = _coreHealth.Length;
        int around = 0;
        Vector3 hub = _territory?.Center ?? _origin;
        float reach = (_territory?.Radius ?? _settings.TerritoryRadius) + NoticeRadius;
        foreach (BasePlayer player in BasePlayer.activePlayerList) if (player is not null && player.IsConnected && Near(player.transform.position, hub, reach)) around++;
        _maxHealth = _settings.WormHealth * Mathf.Min(_settings.MaxHealthMultiplier, 1f + _settings.HealthPerExtraPlayer * Mathf.Max(0, around - 1));
        _health = _maxHealth;
        float coreHealth = _settings.CoreHealth * (_maxHealth / _settings.WormHealth);
        for (int core = 0; core < _coreHealth.Length; core++) { _coreHealth[core] = coreHealth; _nextSpark[core] = 0f; _coreBy[core].Clear(); }
        _cover.Clear();
        _lastDamageAt = float.NegativeInfinity;
        _hurtFaults = _crushFaults = 0;
        _noiseAt = float.NegativeInfinity;
        _slamArmed = false;
        _biteTarget = null;
        _nextBite = 0f;
        _bitten = 0;
        _bittenUntil = 0f;
        _biteSkipUntil.Clear();
        _tailHitAt.Clear();
        _teethAngle = 0f;
        _damageBy.Clear();
        _trail = 1f;
        _nextHud = 0f;
        _hudHealth = _hudTrail = _hudBits = -1;
        Array.Clear(_shed, 0, _shed.Length);
        Array.Clear(_stripped, 0, _stripped.Length);
        RecordHistory(true);
        _lastTick = _lastSeenAt = Time.realtimeSinceStartup;
        _nextWatch = 0f;
        _tick = timer.Every(TickSeconds, TickWorm);
        StartBreach();
    }

    private BaseEntity SpawnPiece(string prefab, string gibName, Vector3 position, Quaternion rotation, Vector3 scale, bool syncPosition)
    {
        BaseEntity entity = GameManager.server.CreateEntity(prefab, position, rotation);
        if (entity is null) throw new InvalidOperationException("Missing network entity prefab: " + prefab);
        _owned.Add(entity);
        entity.enableSaving = false;
        entity.syncPosition = syncPosition;
        entity.networkEntityScale = true;
        entity.transform.localScale = scale;
        if (gibName is not null && entity is ServerGib gib) { gib._gibName = gibName; gib.bounds = _gibBounds[gibName]; }
        if (entity is CeilingLight ceiling) ceiling.shouldAffectGrowables = false;
        UnityEngine.Object.DestroyImmediate(entity.GetComponent<GroundWatch>());
        UnityEngine.Object.DestroyImmediate(entity.GetComponent<DestroyOnGroundMissing>());
        if (entity is StabilityEntity stability) stability.grounded = true;
        entity.Spawn();
        entity.EnableSaving(false);
        if (entity.IsDestroyed) throw new InvalidOperationException("Part was destroyed during Spawn: " + prefab);
        if (entity is BaseCombatEntity combat) { combat.pickup.enabled = false; combat.ShowHealthInfo = false; }
        if (entity is ServerGib fragment) fragment.CancelInvoke(fragment.RemoveMe);
        if (entity is CeilingLight light)
        {
            CeilingLight.FarmLightGrid.Remove(light);
            light.UpdateFromInput(light.ConsumptionAmount(), 0);
        }
        Collider[] colliders = entity.GetComponentsInChildren<Collider>();
        for (int i = 0; i < colliders.Length; i++) colliders[i].enabled = false;
        Rigidbody[] bodies = entity.GetComponentsInChildren<Rigidbody>();
        for (int i = 0; i < bodies.Length; i++) { bodies[i].isKinematic = true; bodies[i].useGravity = false; }
        return entity;
    }

    private void StartBreach()
    {
        _headS = -1f;
        _breaching = true;
        float now = Time.realtimeSinceStartup;
        _warnUntil = now + WarnSeconds;
        _nextPulse = now;
        _nextGrind = now + WarnSeconds + 1f;
        ScheduleEarlyRoar(now);
        if (_settings.BreachMessage) NotifyNearby(LangKeys.Breach, EntryPoint);
    }

    private void TickWorm()
    {
        float now = Time.realtimeSinceStartup;
        float dt = Mathf.Min(now - _lastTick, 0.5f);
        _lastTick = now;
        try
        {
            if (now >= _nextWatch && WatchEvent(now)) return;
            TickSound(now);
            if (_roarAt > 0f && now >= _roarAt) { _roarAt = 0f; EarlyRoar(); }
            if (now >= _nextHud) { _nextHud = now + HudSeconds; RefreshHud(); }
            if (!_breaching)
            {
                if (!_loop) return;
                if (_resumeAt <= 0f) _resumeAt = now + PauseSeconds;
                if (now < _resumeAt) return;
                _resumeAt = 0f;
                if (WatchEvent(now)) return;
                if (!TryNextArc()) { _resumeAt = now + ArcRetrySeconds; return; }
                StartBreach();
            }
            if (now < _warnUntil) { Rumble(now); return; }
            float previous = _headS;
            _headS += PaceAt(_headS) * dt;
            _teethAngle = Mathf.Repeat(_teethAngle + TeethSpeed * dt, 360f);
            CollectNear();
            BreachEffects(previous, _headS, now);
            if (SegmentS(Segments - 1, _headS) >= _arcLength + 2f) { _breaching = false; HideSurfaceMarker(); }
            ApplyPose();
            RecordHistory(false);
            if (_breaching && _settings.SurfaceMarker) SyncSurfaceMarker();
            if (_settings.RunOverDamage > 0f) RunOver();
            if (_settings.BiteRange > 0f) Bite(now, dt);
            if (_settings.TailDamage > 0f && _settings.TailRange > 0f) TailSweep(now);
            if (_settings.DestroyBuildings && now >= _nextCrush) { _nextCrush = now + CrushSeconds; CrushBuildings(); }
        }
        catch (Exception ex)
        {
            PrintError("Worm update failed; removing it: " + ex);
            RemoveWorm();
        }
    }

    private void Hear(Vector3 position, int weight)
    {
        if (!_settings.HuntNoise || _dead || _parts.Count == 0 || _territory is null || !Near(position, _territory.Center, _territory.Radius)) return;
        float now = Time.realtimeSinceStartup;
        if (weight < _noiseWeight && now - _noiseAt < 5f || HeardInRaidBase(position, now)) return;
        _noise = position;
        _noiseAt = now;
        _noiseWeight = weight;
    }

    private bool HeardInRaidBase(Vector3 position, float now)
    {
        if (!_settings.SpareRaidableBases) return false;
        if (now - _raidAskAt < 1f && Near(position, _raidAsk, 3f)) return _raidAskIn;
        _raidAsk = position;
        _raidAskAt = now;
        return _raidAskIn = NearRaidBase(position, 0f);
    }

    private void OnDispenserGather(ResourceDispenser dispenser, BasePlayer player, Item item)
    {
        if (player is not null) Hear(player.transform.position, 1);
    }

    private object HurtPiece(BaseCombatEntity entity, HitInfo info, PerHitPiece piece)
    {
        if (info is not null && info.DidGather && info.gatherScale > 0f && info.InitiatorPlayer is { IsNpc: false } player) PayStep(piece, player);
        float native = piece.Dispenser is null ? 0f : piece.Dispenser.fractionRemaining;
        if (piece.Left > 0)
        {
            KeepHealth(entity, Mathf.Max(native, (float)piece.Left / piece.Budget));
            return true;
        }
        _pieces.Remove(entity);
        if (native > 0f) KeepHealth(entity, native);
        else entity.Die(info);
        return true;
    }

    private void PayStep(PerHitPiece piece, BasePlayer player)
    {
        if (piece.Left <= 0) return;
        Hear(player.transform.position, 1);
        foreach (PerHitLoot loot in piece.Loot)
        {
            if (loot.Left <= 0) continue;
            int grant = Math.Min(loot.Step, loot.Left);
            loot.Left -= grant;
            piece.Left -= grant;
            Item item = ItemManager.Create(loot.Definition, grant);
            if (item is null) continue;
            item.SetItemOwnership(player, ItemOwnershipPhrases.GatheredPhrase);
            while (item.amount > item.MaxStackable()) player.GiveItem(item.SplitItem(item.MaxStackable()), BaseEntity.GiveItemReason.ResourceHarvested, GiveItemOptions.BackpackOverflow);
            player.GiveItem(item, BaseEntity.GiveItemReason.ResourceHarvested, GiveItemOptions.BackpackOverflow);
        }
    }

    private static void KeepHealth(BaseCombatEntity entity, float fraction)
    {
        float target = entity.MaxHealth() * fraction;
        if (target >= entity.health) return;
        entity.health = target;
        entity.SendNetworkUpdate();
    }

    private void SyncDamageHook()
    {
        foreach (KeyValuePair<BaseEntity, PerHitPiece> entry in _pieces)
            if (!entry.Key || entry.Key.IsDestroyed) _stalePieces.Add(entry.Key);
        for (int i = 0; i < _stalePieces.Count; i++) _pieces.Remove(_stalePieces[i]);
        _stalePieces.Clear();
        _nextPieceSync = Time.realtimeSinceStartup + 5f;
        bool on = _protecting || _pieces.Count > 0;
        if (on == _damageHooked) return;
        _damageHooked = on;
        if (on) Subscribe(nameof(OnEntityTakeDamage));
        else Unsubscribe(nameof(OnEntityTakeDamage));
    }

    private void Rumble(float now)
    {
        if (now < _nextPulse) return;
        _nextPulse = now + 0.65f;
        Effect.server.Run(DirtEffect, EntryPoint, Vector3.up);
        if (_slamArmed)
            for (int i = 0; i < 6; i++)
                Effect.server.Run(DirtEffect, EntryPoint + Quaternion.Euler(0f, i * 60f + now * 40f, 0f) * Vector3.forward * _settings.SlamRadius, Vector3.up);
        if (!_settings.ScreenShake) return;
        bool last = now + 0.65f >= _warnUntil;
        foreach (BasePlayer player in BasePlayer.activePlayerList)
        {
            if (player is null || !player.IsConnected || !Near(player.transform.position, EntryPoint, RumbleRadius)) continue;
            SendLocal(player, last ? StrongQuakeEffect : DiveEffect, player.transform.position + Vector3.down * 2.5f);
        }
    }

    private void BreachEffects(float previous, float current, float now)
    {
        float length = _arcLength;
        for (int segment = 0; segment < Segments; segment++)
        {
            float before = SegmentS(segment, previous), after = SegmentS(segment, current);
            float entryCross = _settings.Depth / (_arcHeight + _settings.Depth) * length / Mathf.PI;
            if (before < entryCross && after >= entryCross)
            {
                Effect.server.Run(DirtEffect, EntryPoint, Vector3.up);
                if (segment == 0) { Effect.server.Run(EruptEffect, EntryPoint, Vector3.up); Roar(EntryPoint + Vector3.up * 4f); Slam(); }
            }
            if (before < length - entryCross && after >= length - entryCross)
            {
                Effect.server.Run(DirtEffect, ExitPoint, Vector3.up);
                if (segment == 0) Effect.server.Run(DiveEffect, ExitPoint, Vector3.up);
            }
        }
        if (now < _nextGrind) return;
        _nextGrind = now + 1.1f;
        int pick = UnityEngine.Random.Range(0, _parts.Count);
        if (!_shed[pick] && _parts[pick] is not null && !_parts[pick].IsDestroyed && AboveGround(_parts[pick].transform.position, 0.5f))
            Effect.server.Run(GrindEffect, _parts[pick].transform.position, Vector3.up);
    }

    private void ApplyPose()
    {
        _stuck.Clear();
        _stuckFrom.Clear();
        for (int i = 0; i < Model.Length; i++)
        {
            if (_shed[i]) continue;
            BaseEntity part = _parts[i];
            if (part is null || part.IsDestroyed) throw new InvalidOperationException("A worm part was lost");
            List<BaseEntity> children = part.children;
            for (int c = 0; c < children.Count; c++)
            {
                if (children[c] is not TimedExplosive explosive || explosive.IsDestroyed) continue;
                _stuck.Add(explosive);
                _stuckFrom.Add(explosive.transform.position);
            }
        }
        for (int segment = 0; segment < Segments; segment++)
        {
            BaseEntity anchor = _anchors[segment];
            if (anchor is null || anchor.IsDestroyed) throw new InvalidOperationException("A worm anchor was lost");
            CurvePoint(SegmentS(segment, _headS), out _framePos[segment], out _frameRot[segment]);
            if (segment == 0)
            {
                anchor.transform.SetPositionAndRotation(_framePos[0], _frameRot[0]);
                float now = Time.realtimeSinceStartup;
                if (now >= _nextGroupCheck) { _nextGroupCheck = now + 1f; Regroup(); }
            }
            else
            {
                Quaternion inverse = Quaternion.Inverse(_frameRot[segment - 1]);
                anchor.transform.localPosition = inverse * (_framePos[segment] - _framePos[segment - 1]) / AnchorScale;
                anchor.transform.localRotation = inverse * _frameRot[segment];
            }
            SendPose(anchor);
        }
        Quaternion teeth = Quaternion.AngleAxis(_teethAngle, Vector3.forward);
        for (int i = 0; i < Model.Length; i++)
        {
            if (_shed[i] || !Model[i].Spin) continue;
            Transform spin = _parts[i].transform;
            spin.localPosition = teeth * (Model[i].Position * _settings.Scale) / AnchorScale;
            spin.localRotation = teeth * Model[i].Rotation;
            SendPose(_parts[i]);
        }
        GuardStuck();
    }

    private void Regroup()
    {
        Network.Visibility.Group group = BodyGroup(_anchors[0], _anchors[0] is null ? default : _anchors[0].transform.position);
        if (group is null) return;
        for (int segment = 0; segment < Segments; segment++) MoveGroup(_anchors[segment], group);
        for (int i = 0; i < _parts.Count; i++) MoveGroup(_parts[i], group);
    }

    private void MoveGroup(BaseEntity piece, Network.Visibility.Group group)
    {
        if (piece is null || piece.IsDestroyed || piece.net is null || piece.net.group == group) return;
        List<BaseEntity> children = piece.children;
        _heldChildren.Clear();
        _heldChildren.AddRange(children);
        children.Clear();
        bool moved;
        try { moved = piece.net.SwitchGroup(group); }
        finally { children.AddRange(_heldChildren); }
        if (!moved) return;
        piece.SendNetworkGroupChange();
        for (int c = 0; c < _heldChildren.Count; c++)
            if (_heldChildren[c] is { IsDestroyed: false } child && !_owned.Contains(child)) child.UpdateNetworkGroup();
    }

    private static Network.Visibility.Group BodyGroup(BaseEntity root, Vector3 at)
    {
        if (root is null || root.IsDestroyed || root.net is null || TerrainMeta.HeightMap is null || Network.Net.sv?.visibility is null) return null;
        at.y = Mathf.Max(at.y, TerrainMeta.HeightMap.GetHeight(at) + 1f);
        Network.Visibility.Manager visibility = Network.Net.sv.visibility;
        if (root.net.group is { } current && visibility.IsInside(current, at, root.networkRange)) return current;
        return visibility.GetGroup(at, root.networkRange);
    }

    private static void SendPose(BaseEntity entity)
    {
        entity.InvalidateNetworkCache();
        entity.SendNetworkUpdate_Position();
    }

    private void GuardStuck()
    {
        for (int c = _stuck.Count - 1; c >= 0; c--)
        {
            TimedExplosive explosive = _stuck[c];
            if (explosive is null || explosive.IsDestroyed) continue;
            Vector3 from = _stuckFrom[c];
            if (!Physics.Linecast(from, explosive.transform.position, out RaycastHit block, StuckMask, QueryTriggerInteraction.Ignore) || block.collider.ToBaseEntity() is TimedExplosive) continue;
            Detach(explosive, Vector3.MoveTowards(block.point, from, 0.25f));
        }
        _stuck.Clear();
    }

    private static void Detach(TimedExplosive explosive, Vector3 position)
    {
        explosive.UnStick();
        float ground = TerrainMeta.HeightMap.GetHeight(position);
        if (position.y < ground + 0.3f) position.y = ground + 0.5f;
        if (position == explosive.transform.position) return;
        explosive.transform.position = position;
        explosive.SendNetworkUpdateImmediate();
    }

    private void RecordHistory(bool fill)
    {
        _historyCursor = (_historyCursor + 1) % History;
        for (int segment = 0; segment < Segments; segment++)
        {
            CurvePoint(SegmentS(segment, _headS), out Vector3 position, out _);
            if (fill) for (int i = 0; i < History; i++) _segmentHistory[segment * History + i] = position;
            else _segmentHistory[segment * History + _historyCursor] = position;
        }
        for (int core = 0; core < _coreParts.Length; core++)
        {
            Vector3 position = CoreCenter(core);
            if (fill) for (int i = 0; i < History; i++) _coreHistory[core * History + i] = position;
            else _coreHistory[core * History + _historyCursor] = position;
        }
    }

    private Vector3 CoreCenter(int core)
    {
        Transform transform = _parts[_coreParts[core]].transform;
        return transform.position + transform.rotation * (Vector3.down * 0.45f * _settings.Scale);
    }

    private float SegmentRadius(int segment) => Radii[segment] * _settings.Scale;

    private void HurtPlayer(BasePlayer player, float damage, Vector3 at)
    {
        BaseEntity initiator = _parts.Count > 0 ? _parts[0] : null;
        if (damage <= 0f || initiator is null || initiator.IsDestroyed || player is null || player.IsDead()) return;
        if (Interface.CallHook("OnScrapWormRunOver", player) is not null) return;
        try { player.OnAttacked(new HitInfo(initiator, player, Rust.DamageType.Blunt, damage, at)); }
        catch (Exception ex) { if (_hurtFaults++ == 0) PrintError("Worm hit failed on a player (further ones in this life are not logged): " + ex); }
    }

    private static bool Targetable(BasePlayer player)
        => player is not null && player.IsConnected && player.IsAlive() && !player.IsDead() && !player.IsSpectating() && !player.IsSleeping() && !((player.IsAdmin || player.IsDeveloper) && player.IsFlying) && !player.InSafeZone();

    private void CollectNear()
    {
        _near.Clear();
        Vector3 center = _origin + _heading * (_arcLength * 0.5f);
        float reach = _arcLength * 0.5f + Mathf.Max(_settings.BiteRange, Mathf.Max(_settings.TailRange, _settings.SlamRadius)) + 4f * _settings.Scale + 2f;
        foreach (BasePlayer player in BasePlayer.activePlayerList)
            if (player is not null && Near(player.transform.position, center, reach) && Targetable(player)) _near.Add(player);
    }

    private void Slam()
    {
        if (!_slamArmed) return;
        _slamArmed = false;
        Vector3 ground = EntryPoint;
        ground.y = TerrainMeta.HeightMap.GetHeight(ground) + 1f;
        float reach = _settings.SlamRadius * _settings.SlamRadius;
        foreach (BasePlayer player in _near)
        {
            Vector3 body = player.CenterPoint();
            if ((body - ground).sqrMagnitude > reach || Physics.Linecast(ground, body, SightMask, QueryTriggerInteraction.Ignore)) continue;
            Effect.server.Run(BloodEffect, body, Vector3.up);
            HurtPlayer(player, _settings.SlamDamage, body);
        }
    }

    private void Bite(float now, float dt)
    {
        Vector3 head = _segmentHistory[_historyCursor];
        if (!AboveGround(head, 0.5f)) { _biteTarget = null; return; }
        CurvePoint(SegmentS(0, _headS), out _, out Quaternion facing);
        Vector3 mouth = head + facing * (Vector3.forward * (2.6f * _settings.Scale));
        if (_biteTarget is not null && (!Targetable(_biteTarget) || _biteTarget.isMounted || _biteTarget.HasParent() || now - _biteSince > BiteWindup + BiteHold)) { _biteTarget = null; _nextBite = now + BiteCooldown; }
        if (_biteTarget is null)
        {
            if (now < _nextBite) return;
            float best = _settings.BiteRange * _settings.BiteRange;
            foreach (BasePlayer player in _near)
            {
                if (player.isMounted || player.HasParent() || _biteSkipUntil.TryGetValue(player.userID, out float skipUntil) && now < skipUntil) continue;
                float distance = (player.CenterPoint() - mouth).sqrMagnitude;
                if (distance >= best || Physics.Linecast(mouth, player.CenterPoint(), PullMask, QueryTriggerInteraction.Ignore)) continue;
                best = distance;
                _biteTarget = player;
            }
            if (_biteTarget is null) return;
            _biteSince = now;
            if (!PlayRoar(_biteTarget, head)) SendLocal(_biteTarget, RoarEffect, _biteTarget.transform.position);
            return;
        }
        if (now - _biteSince < BiteWindup) return;
        BasePlayer target = _biteTarget;
        Vector3 position = target.transform.position;
        Vector3 destination = mouth - Vector3.up * (target.GetHeight() * 0.5f);
        if ((target.CenterPoint() - mouth).sqrMagnitude <= 1.6f * 1.6f)
        {
            Effect.server.Run(BloodEffect, target.CenterPoint(), Vector3.up);
            HurtPlayer(target, _settings.BiteDamage, target.CenterPoint());
            _bitten = target.userID;
            _bittenUntil = now + BittenSeconds;
            _biteTarget = null;
            _nextBite = now + BiteCooldown;
            return;
        }
        Vector3 next = Vector3.MoveTowards(position, destination, PullSpeed * Mathf.Clamp(dt, 0f, 0.2f));
        Vector3 delta = next - position;
        float radius = BasePlayer.GetRadius();
        Vector3 bottom = position + Vector3.up * (radius + 0.05f), top = position + Vector3.up * (target.GetHeight() - radius);
        if (Physics.CheckCapsule(bottom + delta, top + delta, radius, PullMask, QueryTriggerInteraction.Ignore) ||
            (delta.sqrMagnitude > 0.000001f && Physics.CapsuleCast(bottom, top, radius, delta.normalized, delta.magnitude, PullMask, QueryTriggerInteraction.Ignore)) ||
            target.IsBuildingBlocked(next, Quaternion.identity, target.bounds))
        {
            _biteSkipUntil[target.userID] = now + BiteCooldown;
            _biteTarget = null;
            _nextBite = now + 1f;
            return;
        }
        target.PauseFlyHackDetection();
        target.PauseSpeedHackDetection();
        target.PauseTickDistanceDetection();
        target.Teleport(next);
        target.SendNetworkUpdateImmediate();
    }

    private void TailSweep(float now)
    {
        Vector3 tail = _segmentHistory[(Segments - 1) * History + _historyCursor];
        if (!AboveGround(tail, -SegmentRadius(Segments - 1))) return;
        float reach = _settings.TailRange + SegmentRadius(Segments - 1);
        foreach (BasePlayer player in _near)
        {
            if ((player.CenterPoint() - tail).sqrMagnitude > reach * reach) continue;
            if (_tailHitAt.TryGetValue(player.userID, out float last) && now - last < TailCooldown) continue;
            if (Physics.Linecast(tail, player.CenterPoint(), CoverMask, QueryTriggerInteraction.Ignore)) continue;
            _tailHitAt[player.userID] = now;
            Effect.server.Run(GrindEffect, player.CenterPoint(), Vector3.up);
            HurtPlayer(player, _settings.TailDamage, player.CenterPoint());
        }
    }

    private void RunOver()
    {
        BaseEntity initiator = _parts.Count > 0 ? _parts[0] : null;
        if (initiator is null || initiator.IsDestroyed) return;
        float now = Time.realtimeSinceStartup;
        foreach (BasePlayer player in _near)
        {
            if (player.IsDead() || ReferenceEquals(player, _biteTarget) || player.userID == _bitten && now < _bittenUntil) continue;
            Vector3 body = player.transform.position + Vector3.up * 0.9f;
            for (int segment = 0; segment < Segments; segment++)
            {
                Vector3 center = _segmentHistory[segment * History + _historyCursor];
                float reach = SegmentRadius(segment) + 0.6f;
                if ((center - body).sqrMagnitude > reach * reach || !AboveGround(center, -SegmentRadius(segment)) ||
                    Physics.Linecast(center, body, CoverMask, QueryTriggerInteraction.Ignore)) continue;
                Effect.server.Run(GrindEffect, body, Vector3.up);
                HurtPlayer(player, _settings.RunOverDamage, body);
                break;
            }
        }
    }

    private void CrushBuildings()
    {
        if (_territory is null) return;
        BaseEntity initiator = _parts.Count > 0 ? _parts[0] : null;
        if (initiator is null || initiator.IsDestroyed) return;
        float now = Time.realtimeSinceStartup;
        int crushed = 0;
        List<DecayEntity> hits = Facepunch.Pool.Get<List<DecayEntity>>();
        try
        {
            for (int segment = 0; segment < Segments && crushed < CrushPerTick; segment++)
            {
                Vector3 center = _segmentHistory[segment * History + _historyCursor];
                if (!AboveGround(center, -SegmentRadius(segment)) || !Near(center, _territory.Center, _territory.Radius)) continue;
                int count = Physics.OverlapSphereNonAlloc(center, SegmentRadius(segment) * 1.1f, CrushHits, CrushMask, QueryTriggerInteraction.Ignore);
                hits.Clear();
                for (int i = 0; i < count; i++) if (CrushHits[i].ToBaseEntity() is DecayEntity piece) hits.Add(piece);
                Array.Clear(CrushHits, 0, count);
                for (int i = 0; i < hits.Count && crushed < CrushPerTick; i++)
                {
                    DecayEntity building = hits[i];
                    if (building.IsDestroyed || _owned.Contains(building)) continue;
                    if (building is not BuildingBlock && building.OwnerID == 0 || !Near(building.transform.position, _territory.Center, _territory.Radius)) continue;
                    if (_crushedAt.TryGetValue(building.net.ID, out float last) && now - last < CrushCooldown) continue;
                    _crushedAt[building.net.ID] = now;
                    if (IsRaidBasePiece(building)) continue;
                    if (Interface.CallHook("OnScrapWormCrush", building) is not null) continue;
                    Effect.server.Run(GrindEffect, building.transform.position, Vector3.up);
                    try { building.Hurt(new HitInfo(initiator, building, Rust.DamageType.Explosion, _settings.BuildingDamage, center)); }
                    catch (Exception ex) { if (_crushFaults++ == 0) PrintError("Worm crush failed on a building (further ones in this life are not logged): " + ex); }
                    crushed++;
                }
            }
        }
        finally { Facepunch.Pool.FreeUnmanaged(ref hits); }
    }

    private bool IsRaidBasePiece(BaseEntity entity)
        => _settings.SpareRaidableBases && Live(RaidableBases, nameof(RaidableBases)) is { } raids && raids.Call("EventTerritory", entity.transform.position, 0f) is true;

    private bool NearRaidBase(Vector3 point, float margin)
    {
        if (!_settings.SpareRaidableBases || TerrainMeta.HeightMap is null || Live(RaidableBases, nameof(RaidableBases)) is not { } raids) return false;
        point.y = TerrainMeta.HeightMap.GetHeight(point);
        return raids.Call("EventTerritory", point, margin) is true;
    }

    private bool ArcNearRaidBase(Vector3 start, Vector3 heading)
    {
        float margin = _arcLength * 0.25f + RaidBaseStartMargin;
        return NearRaidBase(start, margin) || NearRaidBase(start + heading * (_arcLength * 0.5f), margin) || NearRaidBase(start + heading * _arcLength, margin);
    }

    private static float RayEntry(Vector3 origin, Vector3 direction, Vector3 center, float radius)
    {
        float along = Vector3.Dot(center - origin, direction);
        if (along <= 0f) return float.PositiveInfinity;
        float miss = (origin + direction * along - center).sqrMagnitude;
        return miss > radius * radius ? float.PositiveInfinity : along - Mathf.Sqrt(radius * radius - miss);
    }

    private static bool AboveGround(Vector3 point, float margin)
        => TerrainMeta.HeightMap is not null && point.y > TerrainMeta.HeightMap.GetHeight(point) + margin;

    private static readonly string[] WormHooks =
        { nameof(CanPickupEntity), nameof(OnWeaponFired), nameof(OnTimedExplosiveExplode), nameof(OnRocketLaunched), nameof(OnExplosiveThrown), nameof(OnDispenserGather) };

    private void ToggleProtection(bool enabled)
    {
        _protecting = enabled;
        foreach (string hook in WormHooks)
            if (enabled) Subscribe(hook);
            else Unsubscribe(hook);
        SyncDamageHook();
    }

    private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
    {
        if (_protecting && _owned.Contains(entity)) return true;
        if (_pieces.Count > 0 && entity is not null && _pieces.TryGetValue(entity, out PerHitPiece piece)) return HurtPiece(entity, info, piece);
        if (Time.realtimeSinceStartup >= _nextPieceSync) SyncDamageHook();
        return null;
    }

    private object CanPickupEntity(BasePlayer player, BaseCombatEntity entity) => _owned.Contains(entity) ? false : null;

    private void OnWeaponFired(BaseProjectile weapon, BasePlayer player, ItemModProjectile mod, ProtoBuf.ProjectileShoot shot)
    {
        if (_dead || _parts.Count == 0 || weapon is null || player is null || player.IsNpc || shot?.projectiles is null || shot.projectiles.Count == 0 || mod is null) return;
        Hear(player.transform.position, 2);
        Vector3 origin = player.eyes.position, forward = player.eyes.HeadForward();
        if (!NearWorm(origin)) return;
        Projectile projectile = mod.GetOverrideProjectile(weapon)?.Get()?.GetComponent<Projectile>();
        if (projectile is null) return;
        float total = 0f;
        for (int i = 0; i < projectile.damageTypes.Count; i++) total += projectile.damageTypes[i].amount;
        Projectile.Modifier modifier = weapon.GetProjectileModifier();
        bool scheduled = _pellets.Count > 0;
        foreach (ProtoBuf.ProjectileShoot.Projectile fired in shot.projectiles)
        {
            if (fired is null || player.HasFiredProjectile(fired.projectileID) || Queued(player, fired.projectileID)) continue;
            Vector3 direction = fired.startVel.normalized;
            if (!(Vector3.Dot(direction, forward) >= MaxPelletCos) || !AimAtWorm(origin, direction, out int core, out int segment, out float distance)) continue;
            float amount = PelletDamage(projectile, modifier, total, distance);
            if (!InRange(amount, 0.01f, 2000f)) continue;
            _pellets.Add(new Pellet { Player = player, Id = fired.projectileID, Core = core, Segment = segment, Amount = amount, Point = core >= 0 ? _coreHistory[core * History + _historyCursor] : origin + direction * distance });
        }
        if (!scheduled && _pellets.Count > 0) NextTick(ApplyPellets);
    }

    private bool Queued(BasePlayer player, int id)
    {
        foreach (Pellet pellet in _pellets) if (pellet.Id == id && ReferenceEquals(pellet.Player, player)) return true;
        return false;
    }

    private void ApplyPellets()
    {
        foreach (Pellet pellet in _pellets)
        {
            if (_dead || _parts.Count == 0) break;
            if (pellet.Player is not null && pellet.Player.HasFiredProjectile(pellet.Id)) HitWorm(pellet.Core, pellet.Segment, pellet.Player, pellet.Amount, pellet.Point);
        }
        _pellets.Clear();
    }

    private static float PelletDamage(Projectile projectile, Projectile.Modifier modifier, float total, float distance)
    {
        float near = modifier.distanceOffset + modifier.distanceScale * projectile.damageDistances.x;
        float far = modifier.distanceOffset + modifier.distanceScale * (modifier.farDistanceScale >= 0f ? modifier.farDistanceScale : 1f) * projectile.damageDistances.y;
        float falloff = Mathf.Lerp(projectile.damageMultipliers.x, modifier.farDamageScale >= 0f ? modifier.farDamageScale : projectile.damageMultipliers.y, Mathf.InverseLerp(near, far, distance));
        return total * (modifier.damageOffset + modifier.damageScale * falloff);
    }

    private bool AimAtWorm(Vector3 origin, Vector3 direction, out int core, out int segment, out float nearest)
    {
        core = segment = -1;
        nearest = MaxShotRange;
        for (int i = 0; i < _coreParts.Length; i++)
        {
            if (_coreHealth[i] <= 0f) continue;
            for (int h = 0; h < History; h++)
            {
                Vector3 center = _coreHistory[i * History + h];
                float entry = RayEntry(origin, direction, center, CoreRadius * _settings.Scale);
                if (entry >= nearest || !AboveGround(center, 0.2f)) continue;
                nearest = entry; core = i;
            }
        }
        for (int i = 0; i < Segments; i++)
            for (int h = 0; h < History; h++)
            {
                Vector3 center = _segmentHistory[i * History + h];
                float entry = RayEntry(origin, direction, center, SegmentRadius(i));
                if (entry >= nearest || !AboveGround(center, SegmentRadius(i) * 0.25f)) continue;
                nearest = entry; core = -1; segment = i;
            }
        return (core >= 0 || segment >= 0) && !Physics.Linecast(origin, origin + direction * Mathf.Max(0f, nearest - 0.1f), SightMask, QueryTriggerInteraction.Ignore);
    }

    private void OnRocketLaunched(BasePlayer player, BaseEntity entity)
    {
        if (_dead || _parts.Count == 0 || player is null || entity is not TimedExplosive rocket || !NearWorm(player.transform.position) || rocket.GetComponent<ServerProjectile>() is null) return;
        _rockets.Add(rocket);
        _rocketLast.Add(rocket.transform.position);
        ArmSweep();
    }

    private void OnExplosiveThrown(BasePlayer player, BaseEntity entity, ThrownWeapon item)
    {
        if (_dead || _parts.Count == 0 || player is null || entity is not TimedExplosive explosive || !NearWorm(player.transform.position)) return;
        _thrown.Add(explosive);
        _thrownLast.Add(explosive.transform.position);
        ArmSweep();
    }

    private bool NearWorm(Vector3 position) => Near(position, _origin + _heading * (_arcLength * 0.5f), MaxShotRange + _arcLength);

    private void ArmSweep()
    {
        if (_rocketTick is null || _rocketTick.Destroyed) _rocketTick = timer.Every(RocketCheckSeconds, CheckRockets);
    }

    private void CheckThrown()
    {
        for (int i = _thrown.Count - 1; i >= 0; i--)
        {
            TimedExplosive explosive = _thrown[i];
            if (_dead || _parts.Count == 0 || explosive is null || explosive.IsDestroyed || explosive.HasParent()) { _thrown.RemoveAt(i); _thrownLast.RemoveAt(i); continue; }
            Vector3 from = _thrownLast[i], to = explosive.transform.position;
            _thrownLast[i] = to;
            if (!SweepWorm(from, to, out Vector3 hit)) continue;
            _thrown.RemoveAt(i);
            _thrownLast.RemoveAt(i);
            BaseEntity anchor = NearestPart(hit);
            if (anchor is null) continue;
            Vector3 normal = hit - anchor.transform.position;
            explosive.DoStick(hit, normal.sqrMagnitude > 0.0001f ? normal : Vector3.up, anchor, null);
        }
    }

    private BaseEntity NearestPart(Vector3 point)
    {
        BaseEntity best = null;
        float nearest = float.PositiveInfinity;
        for (int i = 0; i < _parts.Count; i++)
        {
            BaseEntity part = _parts[i];
            if (_shed[i] || part is null || part.IsDestroyed) continue;
            float distance = (part.transform.position - point).sqrMagnitude;
            if (distance < nearest) { nearest = distance; best = part; }
        }
        return best;
    }

    private void CheckRockets()
    {
        CheckThrown();
        for (int i = _rockets.Count - 1; i >= 0; i--)
        {
            TimedExplosive rocket = _rockets[i];
            if (_dead || _parts.Count == 0 || rocket is null || rocket.IsDestroyed) { DropRocket(i); continue; }
            Vector3 from = _rocketLast[i], to = rocket.transform.position;
            _rocketLast[i] = to;
            if (!SweepWorm(from, to, out Vector3 hit)) continue;
            DropRocket(i);
            rocket.transform.position = hit;
            rocket.Explode(hit);
        }
        if (_rockets.Count == 0 && _thrown.Count == 0) timer.Destroy(ref _rocketTick);
    }

    private void DropRocket(int index)
    {
        _rockets.RemoveAt(index);
        _rocketLast.RemoveAt(index);
    }

    private bool SweepWorm(Vector3 from, Vector3 to, out Vector3 hit)
    {
        hit = to;
        Vector3 delta = to - from;
        float length = delta.magnitude;
        if (length < 0.001f) return false;
        Vector3 direction = delta / length;
        float nearest = length;
        bool found = false;
        for (int segment = 0; segment < Segments; segment++)
        {
            Vector3 center = _segmentHistory[segment * History + _historyCursor];
            if (!AboveGround(center, SegmentRadius(segment) * 0.25f)) continue;
            float entry = RayEntry(from, direction, center, SegmentRadius(segment));
            if (entry > nearest) continue;
            nearest = Mathf.Max(0f, entry);
            found = true;
        }
        if (found) hit = from + direction * nearest;
        return found;
    }

    private void OnTimedExplosiveExplode(TimedExplosive explosive, Vector3 position)
    {
        if (_dead || _parts.Count == 0 || explosive is null) return;
        Hear(position, 3);
        if (explosive.creatorEntity is not BasePlayer player || player.IsNpc) return;
        float total = 0f;
        for (int i = 0; i < explosive.damageTypes.Count; i++) total += explosive.damageTypes[i].amount;
        float blast = explosive.explosionRadius;
        if (!InRange(total, 0.01f, 100000f) || !InRange(blast, 0.1f, 50f)) return;
        int strongest = -1;
        float best = 0f;
        for (int core = 0; core < _coreParts.Length; core++)
        {
            _coreBlast[core] = 0f;
            if (_coreHealth[core] <= 0f) continue;
            Vector3 center = CoreCenter(core);
            float reach = blast + CoreRadius * _settings.Scale, distance = Vector3.Distance(position, center);
            if (distance > reach || !AboveGround(center, 0.2f) || Physics.Linecast(position, center, SightMask, QueryTriggerInteraction.Ignore)) continue;
            _coreBlast[core] = total * (1f - 0.6f * distance / reach);
            if (_coreBlast[core] * _settings.CoreDamage > best) { best = _coreBlast[core] * _settings.CoreDamage; strongest = core; }
        }
        float closest = float.PositiveInfinity, body = 0f;
        int segment = -1;
        Vector3 hit = position;
        for (int i = 0; i < Segments; i++)
        {
            Vector3 center = _segmentHistory[i * History + _historyCursor];
            float distance = Vector3.Distance(position, center) - SegmentRadius(i);
            if (distance >= closest || !AboveGround(center, SegmentRadius(i) * 0.25f)) continue;
            closest = distance; hit = center; segment = i;
        }
        if (segment >= 0 && closest <= blast && !Physics.Linecast(position, hit, SightMask, QueryTriggerInteraction.Ignore))
            body = total * (1f - 0.6f * Mathf.Max(0f, closest) / blast);
        if (body * BodyFactor(Mathf.Max(0, segment)) > best) strongest = -1;
        else body = 0f;
        for (int core = 0; core < _coreParts.Length; core++)
            if (_coreBlast[core] > 0f) HitWorm(core, -1, player, _coreBlast[core], CoreCenter(core), core == strongest);
        if (body > 0f) HitWorm(-1, segment, player, body, hit);
    }

    private float BodyFactor(int segment) => _stripped[segment] ? Mathf.Min(1f, _settings.BodyDamage * StrippedBonus) : _settings.BodyDamage;

    private void HitWorm(int core, int segment, BasePlayer attacker, float amount, Vector3 point, bool shared = true)
    {
        if (_dead || _health <= 0f) return;
        float share = AttackerShare(attacker);
        if (share <= 0f) return;
        amount *= share;
        float now = Time.realtimeSinceStartup;
        _lastDamageAt = now;
        if (attacker.IsConnected) SendLocal(attacker, HitNotifyEffect, attacker.eyes.position);
        float before = _health;
        if (core >= 0 && _coreHealth[core] <= 0f) { segment = Model[_coreParts[core]].Segment; core = -1; }
        if (core < 0)
        {
            _health -= amount * BodyFactor(segment);
            Credit(attacker, before);
            return;
        }
        if (now >= _nextSpark[core]) { _nextSpark[core] = now + 0.25f; Effect.server.Run(SparkEffect, point, Vector3.up); }
        if (shared) _health -= amount * _settings.CoreDamage;
        ulong id = attacker.userID;
        if (id.IsSteamId())
        {
            Dictionary<ulong, float> by = _coreBy[core];
            by.TryGetValue(id, out float done);
            by[id] = done + Mathf.Min(amount, _coreHealth[core]);
        }
        _coreHealth[core] -= amount;
        float burst = 0f;
        if (_coreHealth[core] <= 0f)
        {
            _coreHealth[core] = 0f;
            if (_parts[_coreParts[core]] is CeilingLight light) light.UpdateFromInput(0, 0);
            Effect.server.Run(BlastEffect, point, Vector3.up);
            _coresAlive--;
            burst = Mathf.Min(_maxHealth * CoreBurstShare, Mathf.Max(0f, _health));
            _health -= _maxHealth * CoreBurstShare;
            ShareBurst(core, burst);
            if (_settings.ShedArmor && _health > 0f && _coresAlive > 0) ShedArmor(Model[_coreParts[core]].Segment);
            if (_coresAlive > 0 && _health > 0f) NotifyNearby(LangKeys.CoreDown, point, _coresAlive);
        }
        Credit(attacker, before - burst);
    }

    private float AttackerShare(BasePlayer attacker)
    {
        if (attacker is null || attacker.IsDestroyed || attacker.InSafeZone()) return 0f;
        Vector3 at = attacker.transform.position;
        if (_territory is not null && !Near(at, _territory.Center, _territory.Radius)) return 0f;
        if (_settings.CoverDamageScale >= 1f) return 1f;
        float now = Time.realtimeSinceStartup;
        ulong id = attacker.userID;
        if (!_cover.TryGetValue(id, out Cover cover) || now >= cover.Until)
            _cover[id] = cover = new Cover { Until = now + CoverSeconds, Covered = NoBreachUnder(at) || attacker.GetBuildingPrivilege() is not null || BaseNear(at) || NearRaidBase(at, 0f) };
        return cover.Covered ? _settings.CoverDamageScale : 1f;
    }

    private static bool NoBreachUnder(Vector3 at)
    {
        if (TerrainMeta.HeightMap is null) return false;
        at.y = TerrainMeta.HeightMap.GetHeight(at);
        return Flooded(at) || NearMonument(at, at);
    }

    private void ShareBurst(int core, float burst)
    {
        Dictionary<ulong, float> by = _coreBy[core];
        float total = 0f;
        foreach (KeyValuePair<ulong, float> pair in by) total += pair.Value;
        if (burst > 0f && total > 0f)
            foreach (KeyValuePair<ulong, float> pair in by)
            {
                _damageBy.TryGetValue(pair.Key, out float dealt);
                _damageBy[pair.Key] = dealt + burst * pair.Value / total;
            }
        by.Clear();
    }

    private void Credit(BasePlayer attacker, float before)
    {
        float removed = before - Mathf.Max(0f, _health);
        if (removed > 0f && ((ulong)attacker.userID).IsSteamId())
        {
            _damageBy.TryGetValue(attacker.userID, out float dealt);
            _damageBy[attacker.userID] = dealt + removed;
        }
        if (_killQueued || _health > 0f && _coresAlive > 0) return;
        _killQueued = true;
        int life = _life;
        NextTick(() => { if (life == _life) KillWorm(); });
    }

    private static bool IsArmor(PartSpec spec)
        => !spec.Spin && spec.Gib is not null && (spec.Gib.EndsWith("046", StringComparison.Ordinal) || spec.Gib.EndsWith("047", StringComparison.Ordinal) || spec.Gib.EndsWith("027", StringComparison.Ordinal));

    private void ShedArmor(int segment)
    {
        _stripped[segment] = true;
        for (int i = _debris.Count - 1; i >= 0; i--) if (_debris[i] is null || _debris[i].IsDestroyed) _debris.RemoveAt(i);
        CurvePoint(SegmentS(segment, _headS), out Vector3 center, out _);
        _debrisBatch.Clear();
        for (int i = 0; i < Model.Length; i++)
        {
            if (_shed[i] || Model[i].Segment != segment || !IsArmor(Model[i])) continue;
            BaseEntity part = _parts[i];
            _shed[i] = true;
            if (part is null || part.IsDestroyed) continue;
            Vector3 position = part.transform.position;
            if (_debris.Count < MaxDebris && AboveGround(position, 0.3f))
                SpawnDebris(Model[i], part, (position - center).normalized * UnityEngine.Random.Range(3f, 6f) + Vector3.up * UnityEngine.Random.Range(1f, 3f));
            _owned.Remove(part);
            ReleaseStuck(part);
            part.Kill();
        }
        _debrisBatch.Clear();
        Effect.server.Run(GrindEffect, center, Vector3.up);
        Effect.server.Run(DirtEffect, center, Vector3.up);
    }

    private static void ReleaseStuck(BaseEntity part)
    {
        for (int i = part.children.Count - 1; i >= 0; i--)
        {
            if (part.children[i] is not TimedExplosive explosive || explosive.IsDestroyed) continue;
            Detach(explosive, explosive.transform.position);
        }
    }

    private void KillWorm()
    {
        if (_dead || _parts.Count == 0) return;
        _dead = true;
        _breaching = _loop = false;
        Vector3 sum = Vector3.zero;
        int exposed = 0;
        for (int i = _debris.Count - 1; i >= 0; i--) if (_debris[i] is null || _debris[i].IsDestroyed) _debris.RemoveAt(i);
        _debrisBatch.Clear();
        for (int i = 0; i < _parts.Count; i++)
        {
            BaseEntity part = _parts[i];
            if (_shed[i] || part is null || part.IsDestroyed || !AboveGround(part.transform.position, 0.3f)) continue;
            sum += part.transform.position;
            exposed++;
            if (i % 6 == 0) Effect.server.Run(BlastEffect, part.transform.position, Vector3.up);
            if (Model[i].Gib is not null && _debris.Count < MaxDebris) SpawnDebris(Model[i], part);
        }
        _debrisBatch.Clear();
        Vector3 fall = exposed > 0 ? sum / exposed : _origin + _heading * (_arcLength * 0.5f);
        fall.y = TerrainMeta.HeightMap.GetHeight(fall);
        Effect.server.Run(DeathEffect, fall + Vector3.up * 2f, Vector3.up);
        fall = OutsideBases(fall);
        ulong winner = 0;
        float most = 0f;
        foreach (var pair in _damageBy) if (pair.Value > most) { most = pair.Value; winner = pair.Key; }
        ulong earned = most < _settings.MinRewardShare * _maxHealth ? 0UL : winner;
        BasePlayer top = winner == 0 ? null : RelationshipManager.FindByID(winner);
        string winnerName = PlainName(top?.displayName ?? winner.ToString());
        RemoveWorm();
        try { SpawnLoot(fall, LootOwners(earned, winnerName)); }
        catch (Exception ex) { PrintError("Worm loot failed: " + ex); }
        Interface.CallHook("OnScrapWormKilled", fall, winner);
        PayRewards();
        RunWinnerCommands(earned, top?.displayName, fall);
        PostDiscord(winner == 0 ? LangKeys.Killed : LangKeys.KilledBy, DiscordSafe(winnerName));
        if (winner == 0) NotifyNearby(LangKeys.Killed, fall);
        else NotifyNearbyWithToast(LangKeys.KilledBy, LangKeys.ToastKilledBy, fall, winnerName);
    }

    private void FillDebris(ResourceDispenser dispenser, Vector3 size, Vector3 scale)
    {
        float volume = size.x * scale.x * size.y * scale.y * size.z * scale.z;
        float share = Mathf.Clamp(volume / 3f, 0.35f, 1f);
        dispenser.containedItems = new List<ItemAmount>(_debrisDefs.Length);
        BaseCombatEntity owner = dispenser.baseEntity as BaseCombatEntity;
        PerHitPiece piece = null;
        for (int i = 0; i < _debrisDefs.Length; i++)
        {
            ItemDefinition definition = _debrisDefs[i];
            DebrisItem spec = _settings.DebrisLoot[i];
            if (definition is null || spec.Amount <= 0 || share < spec.MinPieceSize) continue;
            int amount = Mathf.CeilToInt(spec.Amount * share);
            if (spec.AmountPerHit > 0 && owner is not null)
            {
                piece ??= new PerHitPiece { Dispenser = dispenser };
                piece.Loot.Add(new PerHitLoot { Definition = definition, Left = amount, Step = spec.AmountPerHit });
                piece.Budget += amount;
                piece.Left += amount;
                continue;
            }
            dispenser.containedItems.Add(new ItemAmount(definition, amount));
        }
        if (piece is not null)
        {
            dispenser.finishBonus = null;
            _pieces[owner] = piece;
            SyncDamageHook();
        }
        dispenser.Initialize();
    }

    private void ResolveDebrisLoot()
    {
        _debrisDefs = new ItemDefinition[_settings.DebrisLoot.Count];
        for (int i = 0; i < _debrisDefs.Length; i++)
        {
            DebrisItem spec = _settings.DebrisLoot[i];
            ItemDefinition definition = ItemManager.FindItemDefinition(spec.ShortName.Trim());
            if (definition is null) { PrintWarning($"DebrisLoot: no item with the short name '{spec.ShortName}', skipped."); continue; }
            _debrisDefs[i] = definition;
        }
    }

    private void SpawnDebris(PartSpec spec, BaseEntity part, Vector3 push = default)
    {
        BaseEntity created = null;
        try
        {
            created = GameManager.server.CreateEntity(GibPrefab, part.transform.position, part.transform.rotation);
            if (created is not ServerGib debris) { if (created is not null) UnityEngine.Object.Destroy(created.gameObject); return; }
            debris._gibName = spec.Gib;
            debris.bounds = _gibBounds[spec.Gib];
            LiftClear(debris.transform, debris.bounds);
            debris.useContinuousCollision = true;
            debris.PhysicsInit(_gibMeshes[spec.Gib]);
            if (debris is HelicopterDebris dispenserOwner && dispenserOwner.GetComponent<ResourceDispenser>() is { } dispenser) FillDebris(dispenser, debris.bounds.size, spec.Scale * _settings.Scale);
            debris.enableSaving = false;
            debris.Spawn();
            _debris.Add(debris);
            if (debris.GetCollider() is { } collider)
            {
                foreach (Collider other in _debrisBatch) if (other) Physics.IgnoreCollision(collider, other, true);
                _debrisBatch.Add(collider);
            }
            if (push != default && debris.GetComponent<Rigidbody>() is { } body)
            {
                body.linearVelocity = push;
                body.angularVelocity = Vector3Ex.Range(-2f, 2f);
            }
            if (debris is HelicopterDebris heli) { heli.OnCooledDown(); heli.CancelInvoke(heli.OnCooledDown); }
        }
        catch (Exception ex)
        {
            if (created is not null && !created.IsDestroyed) created.Kill();
            PrintError("Failed to create worm debris: " + ex.Message);
        }
    }

    private static void LiftClear(Transform piece, Bounds box)
    {
        Vector3 center = piece.TransformPoint(box.center), half = Vector3.Scale(box.extents, piece.lossyScale);
        float reach = Mathf.Abs(piece.right.y * half.x) + Mathf.Abs(piece.up.y * half.y) + Mathf.Abs(piece.forward.y * half.z);
        float lift = SolidGround(center) + reach + 0.1f - center.y;
        if (lift > 0f) piece.position += Vector3.up * lift;
    }

    private LootLockEntry LootOwners(ulong winner, string winnerName)
    {
        float now = Time.realtimeSinceStartup;
        foreach (var pair in _lootLocks) if (pair.Key is null || pair.Key.IsDestroyed || pair.Value.Until <= now) _lootGone.Add(pair.Key);
        foreach (BaseEntity crate in _lootGone) _lootLocks.Remove(crate);
        _lootGone.Clear();
        if (winner == 0 || _settings.LootLockSeconds <= 0f) return null;
        var entry = new LootLockEntry { Winner = winnerName, Until = now + _settings.LootLockSeconds };
        entry.Owners.Add(winner);
        RelationshipManager.PlayerTeam team = RelationshipManager.ServerInstance?.FindPlayersTeam(winner);
        if (team is not null) foreach (ulong member in team.members) entry.Owners.Add(member);
        return entry;
    }

    private void LootHooks()
    {
        if (_lootLocks.Count > 0) { Subscribe(nameof(CanLootEntity)); Subscribe(nameof(CanHackCrate)); }
        else { Unsubscribe(nameof(CanLootEntity)); Unsubscribe(nameof(CanHackCrate)); _lockToldAt.Clear(); }
    }

    private void FillCrate(BaseEntity crate, LootLockEntry owners)
    {
        if (crate is LootContainer container && _settings.AlphaLootProfile.Length > 0 && Live(AlphaLoot, nameof(AlphaLoot)) is { } alphaLoot &&
            alphaLoot.Call("ProfileExists", _settings.AlphaLootProfile) is true)
            alphaLoot.Call("PopulateLoot", container, _settings.AlphaLootProfile);
        if (owners is not null) _lootLocks[crate] = owners;
    }

    private object LootLock(BasePlayer player, BaseEntity entity)
    {
        if (player is null || entity is null || !_lootLocks.TryGetValue(entity, out LootLockEntry owners)) return null;
        float left = owners.Until - Time.realtimeSinceStartup;
        if (left <= 0f)
        {
            _lootLocks.Remove(entity);
            if (_lootLocks.Count == 0) LootHooks();
            return null;
        }
        if (owners.Owners.Contains(player.userID)) return null;
        float now = Time.realtimeSinceStartup;
        if (!_lockToldAt.TryGetValue(player.userID, out float next) || now >= next)
        {
            _lockToldAt[player.userID] = now + 3f;
            Tell(player, Fmt(Msg(LangKeys.LootLocked, player), owners.Winner, left));
        }
        return false;
    }

    private void SaveLootLocks()
    {
        var saved = new List<SavedLock>(_lootLocks.Count);
        float now = Time.realtimeSinceStartup;
        foreach (var pair in _lootLocks)
        {
            BaseEntity crate = pair.Key;
            if (crate is null || crate.IsDestroyed || crate.net is null || pair.Value.Until <= now) continue;
            Vector3 at = crate.transform.position;
            saved.Add(new SavedLock { Crate = crate.net.ID.Value, Prefab = crate.prefabID, X = at.x, Y = at.y, Z = at.z, Until = pair.Value.Until, Winner = pair.Value.Winner, Owners = new List<ulong>(pair.Value.Owners) });
        }
        try { Interface.Oxide.DataFileSystem.WriteObject(LootFile, saved); }
        catch (Exception ex) { PrintWarning("Could not save the loot locks: " + ex.Message); }
    }

    private void LoadLootLocks()
    {
        List<SavedLock> saved;
        try { saved = Interface.Oxide.DataFileSystem.ReadObject<List<SavedLock>>(LootFile); }
        catch (Exception ex) { PrintWarning("Loot locks unreadable, crates left unlocked: " + ex.Message); return; }
        if (saved is null) return;
        float now = Time.realtimeSinceStartup;
        foreach (SavedLock lockState in saved)
        {
            if (lockState?.Owners is null || lockState.Until <= now || lockState.Until > now + 3600f) continue;
            if (BaseNetworkable.serverEntities.Find(new NetworkableId(lockState.Crate)) is not BaseEntity crate || crate.IsDestroyed || crate.enableSaving || crate.prefabID != lockState.Prefab ||
                (crate.transform.position - new Vector3(lockState.X, lockState.Y, lockState.Z)).sqrMagnitude > 25f) continue;
            var entry = new LootLockEntry { Winner = lockState.Winner ?? "", Until = lockState.Until };
            entry.Owners.UnionWith(lockState.Owners);
            _lootLocks[crate] = entry;
        }
        LootHooks();
    }

    private object CanLootEntity(BasePlayer player, StorageContainer container) => LootLock(player, container);

    private object CanHackCrate(BasePlayer player, HackableLockedCrate crate) => LootLock(player, crate);

    private object CanEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
    {
        if (entity is null || info is null) return null;
        if (_settings is { ForceDamageThroughTruePVE: true })
        {
            if (info.Initiator is not null && _owned.Contains(info.Initiator)) return true;
            if (IsCrateFire(info.Initiator)) return entity is BasePlayer burnt && Targetable(burnt) ? true : null;
        }
        if (info.InitiatorPlayer is not null && entity is ServerGib gib && _debris.Contains(gib)) return true;
        return null;
    }

    private Vector3 OutsideBases(Vector3 ground)
    {
        BaseEntity probe = null;
        foreach (BaseEntity part in _parts) if (part is not null && !part.IsDestroyed) { probe = part; break; }
        if (probe is null || !Privileged(probe, ground)) return ground;
        for (float radius = 8f; radius <= 256f; radius += radius < 96f ? 8f : 16f)
        {
            float start = UnityEngine.Random.Range(0f, 360f);
            for (int i = 0; i < 16; i++)
            {
                Vector3 point = ground + Quaternion.Euler(0f, start + i * 22.5f, 0f) * Vector3.forward * radius;
                point.y = TerrainMeta.HeightMap.GetHeight(point);
                if (WaterLevel.Test(point + Vector3.up * 0.5f, false, false) || Privileged(probe, point)) continue;
                return point;
            }
        }
        PrintWarning($"No dry ground outside building privilege within 256 m of {MapHelper.PositionToString(ground)}: the worm's loot drops where it died.");
        return ground;
    }

    private static bool Privileged(BaseEntity probe, Vector3 point)
        => probe.GetBuildingPrivilege(new OBB(point + Vector3.up * 1.5f, Quaternion.identity, new Bounds(Vector3.zero, new Vector3(7f, 3f, 7f))), false) is not null;

    private const float CrateSettleSeconds = 6f, CrateStrayMeters = 8f, CrateGapMeters = 0.3f, CrateSunkMeters = 1.5f, CrateClearMeters = 1.6f;

    private void SpawnLoot(Vector3 ground, LootLockEntry owners)
    {
        string cratePrefab = CratePrefab(_settings.CrateType);
        var thrown = new List<BaseEntity>(_settings.Crates);
        for (int i = 0; i < _settings.Crates; i++)
        {
            BaseEntity crate = null;
            try
            {
                Vector3 direction = Quaternion.Euler(0f, 360f * i / _settings.Crates + UnityEngine.Random.Range(-20f, 20f), 0f) * Vector3.forward;
                Vector3 position = ground + Vector3.up * 1.5f + direction * UnityEngine.Random.Range(2f, 3f);
                position.y = Mathf.Max(position.y, TerrainMeta.HeightMap.GetHeight(position) + 1.5f);
                crate = GameManager.server.CreateEntity(cratePrefab, position, Quaternion.LookRotation(direction));
                if (crate is null) continue;
                crate.enableSaving = false;
                crate.Spawn();
                FillCrate(crate, owners);
                var body = crate.gameObject.AddComponent<Rigidbody>();
                body.useGravity = true;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                body.mass = 2f;
                body.interpolation = RigidbodyInterpolation.Interpolate;
                body.linearVelocity = direction * UnityEngine.Random.Range(1f, 3f);
                body.angularVelocity = Vector3Ex.Range(-1.75f, 1.75f);
                body.linearDamping = 0.2f;
                body.angularDamping = 0.08f;
                if (GameManager.server.CreateEntity(FireBallPrefab) is FireBall fire)
                {
                    fire.enableSaving = false;
                    fire.SetParent(crate);
                    fire.Spawn();
                    if (fire.GetComponent<Rigidbody>() is { } fireBody) fireBody.isKinematic = true;
                    if (fire.GetComponent<Collider>() is { } fireCollider) fireCollider.enabled = false;
                    fire.CancelInvoke(fire.TryToSpread);
                    TameCrateFire(fire);
                    if (crate is LockedByEntCrate locked) locked.SetLockingEnt(fire);
                }
                thrown.Add(crate);
            }
            catch (Exception ex)
            {
                PrintError("Worm crate failed: " + ex.Message);
                if (crate is not null)
                {
                    _lootLocks.Remove(crate);
                    if (!crate.IsDestroyed) crate.Kill();
                }
            }
        }
        try
        {
            if (_settings.HackableCrate && GameManager.server.CreateEntity(HackCratePrefab, ground + Vector3.up * 0.5f, Quaternion.LookRotation(_heading)) is { } hackable)
            {
                hackable.enableSaving = false;
                hackable.Spawn();
                FillCrate(hackable, owners);
            }
        }
        catch (Exception ex) { PrintError("Worm hackable crate failed: " + ex.Message); }
        LootHooks();
        if (thrown.Count > 0) timer.Once(CrateSettleSeconds, () => SettleCrates(thrown, ground));
    }

    private void SettleCrates(List<BaseEntity> crates, Vector3 ground)
    {
        bool groundClear = !WaterLevel.Test(ground + Vector3.up * 0.5f, false, false);
        for (int i = 0; i < crates.Count; i++)
        {
            BaseEntity crate = crates[i];
            if (crate is null || crate.IsDestroyed) continue;
            try
            {
                Rigidbody body = crate.GetComponent<Rigidbody>();
                if (body is not null)
                {
                    body.linearVelocity = body.angularVelocity = Vector3.zero;
                    body.collisionDetectionMode = CollisionDetectionMode.Discrete;
                    body.isKinematic = true;
                }
                if (groundClear && Privileged(crate, ground)) groundClear = false;
                Vector3 at = crate.transform.position;
                Bounds box = crate.GetComponent<Collider>() is { } shape ? shape.bounds : new Bounds(at, Vector3.zero);
                float gap = box.min.y - SolidGround(box.center);
                bool stray = !Near(at, ground, CrateStrayMeters) || gap > CrateGapMeters || gap < -CrateSunkMeters || (groundClear && (WaterLevel.Test(at + Vector3.up * 0.5f, false, false) || Privileged(crate, at)));
                if (stray)
                {
                    Vector3 direction = Quaternion.Euler(0f, 360f * i / crates.Count, 0f) * Vector3.forward;
                    Vector3 back = ground + direction * 2.5f;
                    if (WaterLevel.Test(back.WithY(TerrainMeta.HeightMap.GetHeight(back)) + Vector3.up * 0.5f, false, false) || Privileged(crate, back) || Taken(crates, i, back)) back = ground + direction * 1.5f;
                    back.y = SolidGround(back) + 0.1f;
                    Quaternion upright = Quaternion.Euler(0f, crate.transform.eulerAngles.y, 0f);
                    crate.transform.SetPositionAndRotation(back, upright);
                }
                crate.SendNetworkUpdateImmediate();
            }
            catch (Exception ex) { PrintError("Worm crate settle failed: " + ex.Message); }
        }
    }

    private static bool Taken(List<BaseEntity> crates, int self, Vector3 point)
    {
        for (int i = 0; i < crates.Count; i++)
            if (i != self && crates[i] is { IsDestroyed: false } other && Near(other.transform.position, point, CrateClearMeters)) return true;
        return false;
    }

    private static float SolidGround(Vector3 at)
    {
        float ground = TerrainMeta.HeightMap.GetHeight(at);
        return Physics.Raycast(new Vector3(at.x, ground + 30f, at.z), Vector3.down, out RaycastHit hit, 40f, Rust.Layers.Mask.Terrain | Rust.Layers.Mask.World, QueryTriggerInteraction.Ignore) ? Mathf.Max(ground, hit.point.y) : ground;
    }

    private const float CrateFireTick = 0.5f;
    private const float CrateFireRadius = 2f;
    private const string CrateFireHitHook = "OnScrapWormRunOver";
    private const string CrateFireHitEffect = "assets/bundled/prefabs/fx/impacts/additive/fire.prefab";
    private readonly List<CrateFire> _crateFires = new();
    private Timer _crateFireTimer;

    private sealed class CrateFire
    {
        public FireBall Fire;
        public float Damage, Radius;
    }

    private bool IsCrateFire(BaseEntity entity)
    {
        if (entity is not FireBall) return false;
        foreach (CrateFire entry in _crateFires) if (ReferenceEquals(entry.Fire, entity)) return true;
        return false;
    }

    private void TameCrateFire(FireBall fire)
    {
        if (fire is null || fire.IsDestroyed) return;
        var entry = new CrateFire { Fire = fire, Damage = fire.damagePerSecond * CrateFireTick, Radius = Mathf.Min(fire.radius, CrateFireRadius) };
        fire.damagePerSecond = 0f;
        fire.AttackLayers = 0;
        fire.radius = 0f;
        if (!IsLoaded) return;
        _crateFires.Add(entry);
        if (_crateFires.Count > 1) return;
        timer.Destroy(ref _crateFireTimer);
        _crateFireTimer = timer.Every(CrateFireTick, CrateFireStep);
    }

    private void CrateFireStep()
    {
        List<BasePlayer> near = Facepunch.Pool.Get<List<BasePlayer>>();
        try
        {
            for (int i = _crateFires.Count - 1; i >= 0; i--)
            {
                CrateFire entry = _crateFires[i];
                FireBall fire = entry.Fire;
                if (fire is null || fire.IsDestroyed) { _crateFires.RemoveAt(i); continue; }
                Vector3 center = fire.transform.position + Vector3.up * (entry.Radius * 0.75f);
                near.Clear();
                Vis.Entities(center, entry.Radius, near, Rust.Layers.Mask.Player_Server, QueryTriggerInteraction.Ignore);
                foreach (BasePlayer player in near)
                {
                    if (!Targetable(player) || !player.IsVisible(center)) continue;
                    if (Interface.CallHook(CrateFireHitHook, player) is not null) continue;
                    Effect.server.Run(CrateFireHitEffect, player, 0, Vector3.up, Vector3.up);
                    player.OnAttacked(new HitInfo(fire, player, Rust.DamageType.Heat, entry.Damage, player.transform.position));
                }
            }
        }
        catch (Exception ex)
        {
            PrintError("Crate fire tick failed; the fires stay lit but burn nobody from now on: " + ex);
            _crateFires.Clear();
        }
        finally { Facepunch.Pool.FreeUnmanaged(ref near); }
        if (_crateFires.Count == 0) DropCrateFires();
    }

    private void DropCrateFires()
    {
        timer.Destroy(ref _crateFireTimer);
        _crateFires.Clear();
    }

    private bool Exposed()
    {
        for (int segment = 0; segment < Segments; segment++)
            if (AboveGround(_segmentHistory[segment * History + _historyCursor], 0.5f)) return true;
        return false;
    }

    private void RefreshHud()
    {
        if (!_settings.HealthBar || _parts.Count == 0) { HideHud(); return; }
        float fill = Mathf.Clamp01(_health / _maxHealth);
        _trail = fill >= _trail ? fill : Mathf.MoveTowards(_trail, fill, 0.06f);
        bool exposed = Exposed();
        int health = Mathf.CeilToInt(Mathf.Max(0f, _health)), trail = Mathf.RoundToInt(_trail * 1000f), bits = HudBits(exposed);
        bool changed = health != _hudHealth || trail != _hudTrail || bits != _hudBits;
        if (changed)
        {
            _hudHealth = health; _hudTrail = trail; _hudBits = bits;
            _hudDocs.Clear();
        }
        Vector3 center = _origin + _heading * (_arcLength * 0.5f);
        foreach (BasePlayer player in _hudShown)
            if (player is null || !player.IsConnected || !Near(player.transform.position, center, _settings.HealthBarRadius)) _hudGone.Add(player);
        foreach (BasePlayer player in _hudGone)
        {
            _hudShown.Remove(player);
            if (player is not null && player.IsConnected) _hudTree.Destroy(player);
        }
        _hudGone.Clear();
        foreach (BasePlayer player in BasePlayer.activePlayerList)
        {
            if (player is null || !player.IsConnected || !Near(player.transform.position, center, _settings.HealthBarRadius)) continue;
            if (_hudShown.Add(player)) UIImages.Preload(this, player);
            else if (!changed) continue;
            string code = LangOf(player);
            if (!_hudDocs.TryGetValue(code, out UIDocument doc)) _hudDocs[code] = doc = HudDoc(code, fill, exposed);
            _hudTree.Render(player, doc);
        }
    }

    private int HudBits(bool exposed)
    {
        int bits = (exposed ? 1 : 0) | (UIImages.Get(PortraitKey) is null ? 0 : 2) | (UIImages.Get(CoreOnKey) is null ? 0 : 4) | (UIImages.Get(CoreOffKey) is null ? 0 : 8);
        for (int core = 0; core < _coreHealth.Length; core++) if (_coreHealth[core] > 0f) bits |= 16 << core;
        return bits;
    }

    private UIDocument HudDoc(string code, float fill, bool exposed)
    {
        var doc = CUI.Document(HudRoot);
        float k = _settings.HealthBarScale;
        doc.Panel(HudRoot, "Hud").Anchor(0.5f, 0f, 0.5f, 0f).Offset(-270f * k - 5f, 92f, 270f * k - 5f, 92f + 80f * k).Color(HudBg);
        HudPanel(doc, "ScrapWorm.Hud.Portrait", -262f, -32f, -198f, 32f, HudDeep);
        string portrait = UIImages.Get(PortraitKey);
        if (portrait is not null) doc.Image("ScrapWorm.Hud.PortraitImage", HudRoot).Anchor(0.5f, 0.5f, 0.5f, 0.5f).Offset(-262f * k, -32f * k, -198f * k, 32f * k).Png(portrait);
        HudPanel(doc, "ScrapWorm.Hud.Rim", -262f, -32f, -198f, -30f, exposed ? HudRed : HudRimIdle);
        string name = lang.GetMessageByLanguage(LangKeys.HudName, this, code);
        float nameRight = -186f + Mathf.Ceil(UILayout.MeasureTextWidth(name, Size(18)) / k);
        HudLabel(doc, "ScrapWorm.Hud.Name", -186f, 10f, nameRight + 2f, 32f, name, 18, HudInk, TextAnchor.MiddleLeft, true);
        string state = lang.GetMessageByLanguage(exposed ? LangKeys.HudExposed : LangKeys.HudBurrowed, this, code);
        float chipLeft = nameRight + 10f, chipRight = chipLeft + Mathf.Ceil(UILayout.MeasureTextWidth(state, Size(10)) / k) + 20f;
        HudPanel(doc, "ScrapWorm.Hud.Chip", chipLeft, 13f, chipRight, 29f, exposed ? HudChipHot : HudChipIdle);
        HudLabel(doc, "ScrapWorm.Hud.State", chipLeft, 13f, chipRight, 29f, state, 10, exposed ? HudValue : HudStateIdle, TextAnchor.MiddleCenter, true);
        HudLabel(doc, "ScrapWorm.Hud.CoresLabel", 100f, 13f, 180f, 29f, lang.GetMessageByLanguage(LangKeys.HudCores, this, code), 10, HudMuted, TextAnchor.MiddleRight, true);
        string coreOn = UIImages.Get(CoreOnKey), coreOff = UIImages.Get(CoreOffKey);
        for (int core = 0; core < _coreHealth.Length; core++)
        {
            float x = 188f + core * 19f;
            string png = _coreHealth[core] > 0f ? coreOn : coreOff;
            if (png is null) continue;
            doc.Image("ScrapWorm.Hud.Core" + core, HudRoot).Anchor(0.5f, 0.5f, 0.5f, 0.5f).Offset(x * k, 14f * k, (x + 14f) * k, 28f * k).Png(png);
        }
        const float trackLeft = -186f, trackWidth = 448f;
        HudPanel(doc, "ScrapWorm.Hud.Track", trackLeft, -10f, trackLeft + trackWidth, 4f, HudTrack);
        if (_trail > 0.001f) HudPanel(doc, "ScrapWorm.Hud.Pending", trackLeft, -10f, trackLeft + trackWidth * _trail, 4f, HudPending);
        if (fill > 0.001f)
        {
            HudPanel(doc, "ScrapWorm.Hud.Fill", trackLeft, -10f, trackLeft + trackWidth * fill, 4f, HudRed);
        }
        for (int notch = 1; notch < 4; notch++)
        {
            float x = trackLeft + trackWidth * notch / 4f;
            HudPanel(doc, "ScrapWorm.Hud.Notch" + notch, x - 1f, -10f, x + 1f, 4f, HudNotch);
        }
        HudLabel(doc, "ScrapWorm.Hud.HealthLabel", -186f, -32f, -66f, -14f, lang.GetMessageByLanguage(LangKeys.HudHealth, this, code), 11, HudMuted, TextAnchor.MiddleLeft, true);
        string counter = Mathf.CeilToInt(Mathf.Max(0f, _health)).ToString("N0", CultureInfo.InvariantCulture) + " / " + Mathf.CeilToInt(_maxHealth).ToString("N0", CultureInfo.InvariantCulture);
        HudLabel(doc, "ScrapWorm.Hud.Counter", -37f, -32f, 113f, -14f, counter, 12, HudCounter, TextAnchor.MiddleCenter, false);
        HudLabel(doc, "ScrapWorm.Hud.Percent", 202f, -32f, 262f, -14f, Mathf.CeilToInt(fill * 100f) + "%", 15, HudValue, TextAnchor.MiddleRight, true);
        return doc;
    }

    private int Size(int size) => Mathf.Max(8, Mathf.RoundToInt(size * _settings.HealthBarScale));

    private void HudPanel(UIDocument doc, string name, float x1, float y1, float x2, float y2, string color)
    {
        float k = _settings.HealthBarScale;
        doc.Panel(name, HudRoot).Anchor(0.5f, 0.5f, 0.5f, 0.5f).Offset(x1 * k, y1 * k, x2 * k, y2 * k).Color(color);
    }

    private void HudLabel(UIDocument doc, string name, float x1, float y1, float x2, float y2, string text, int size, string color, TextAnchor align, bool bold)
    {
        float k = _settings.HealthBarScale;
        doc.Label(name, HudRoot).Anchor(0.5f, 0.5f, 0.5f, 0.5f).Offset(x1 * k, y1 * k, x2 * k, y2 * k).Text(text, Size(size)).TextColor(color)
            .Font(bold ? CUIFont.RobotoCondensedBold : CUIFont.RobotoCondensedRegular).Align(align);
    }

    private void HideHud()
    {
        foreach (BasePlayer player in _hudShown)
            if (player is not null && player.IsConnected) _hudTree.Destroy(player);
        _hudShown.Clear();
        _hudDocs.Clear();
    }

    private void SetupTerritories()
    {
        float edge = World.Size * 0.5f;
        foreach (TerritorySpec spec in _settings.Territories)
        {
            if (Mathf.Abs(spec.X) > edge || Mathf.Abs(spec.Z) > edge) { PrintWarning($"Territory '{spec.Name}' ({spec.X:0}, {spec.Z:0}) is outside the {World.Size} m map, skipped."); continue; }
            Vector3 center = new(spec.X, 0f, spec.Z);
            center.y = TerrainMeta.HeightMap.GetHeight(center);
            _territories.Add(new Territory { Name = spec.Name.Length > 0 ? spec.Name : MapHelper.PositionToString(center), Center = center, Radius = spec.Radius });
        }
        var random = new System.Random(unchecked((int)World.Seed * 31 + (int)World.Size));
        float half = World.Size * 0.5f - _settings.TerritoryRadius - 100f;
        int[] rejected = new int[4];
        for (int attempt = 0, added = 0; attempt < 1500 && added < _settings.AutoTerritories && half > 0f; attempt++)
        {
            Vector3 center = new((float)(random.NextDouble() * 2.0 - 1.0) * half, 0f, (float)(random.NextDouble() * 2.0 - 1.0) * half);
            int why = TerritoryProblem(center, _settings.TerritoryRadius);
            if (why >= 0) { rejected[why]++; continue; }
            center.y = TerrainMeta.HeightMap.GetHeight(center);
            _territories.Add(new Territory { Name = MapHelper.PositionToString(center), Center = center, Radius = _settings.TerritoryRadius });
            added++;
        }
        if (_settings.MapMarkers) foreach (Territory territory in _territories) territory.Marker = SpawnMarker(territory, false);
        if (_territories.Count < _settings.Territories.Count + _settings.AutoTerritories)
            PrintWarning($"Placed {_territories.Count} worm territories of {_settings.Territories.Count + _settings.AutoTerritories} (map {World.Size} m; rejected: {rejected[0]} water, {rejected[1]} slope, {rejected[2]} monuments, {rejected[3]} overlap). Add them by hand under Territories or lower TerritoryRadius.");
    }

    private int TerritoryProblem(Vector3 center, float radius)
    {
        if (TerrainMeta.HeightMap is null || TerrainMeta.Path is null) return 0;
        float middle = TerrainMeta.HeightMap.GetHeight(center);
        if (middle < 1f || WaterLevel.Test(new Vector3(center.x, middle + 0.5f, center.z), false, false)) return 0;
        for (int i = 0; i < 8; i++)
        {
            Vector3 ring = center + Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward * (radius * 0.4f);
            float height = TerrainMeta.HeightMap.GetHeight(ring);
            if (WaterLevel.Test(new Vector3(ring.x, height + 0.5f, ring.z), false, false)) return 0;
            if (Mathf.Abs(height - middle) > 25f) return 1;
        }
        foreach (MonumentInfo monument in TerrainMeta.Path.Monuments)
        {
            if (monument is null) continue;
            float size = Mathf.Max(monument.Bounds.extents.x, monument.Bounds.extents.z);
            if (Near(monument.transform.position, center, radius * 0.6f + size + 40f)) return 2;
        }
        foreach (Territory other in _territories)
            if (Near(other.Center, center, radius + other.Radius)) return 3;
        return -1;
    }

    private Territory FindTerritory(Vector3 position)
    {
        foreach (Territory territory in _territories) if (Near(position, territory.Center, territory.Radius)) return territory;
        return null;
    }

    private MapMarkerGenericRadius SpawnMarker(Territory territory, bool active)
    {
        if (GameManager.server.CreateEntity(MarkerPrefab, territory.Center) is not MapMarkerGenericRadius marker) return null;
        marker.enableSaving = false;
        marker.Spawn();
        PaintMarker(marker, territory, active);
        return marker;
    }

    private static void PaintMarker(MapMarkerGenericRadius marker, Territory territory, bool active)
    {
        if (marker is null || marker.IsDestroyed) return;
        marker.radius = territory.Radius / GridMeters;
        marker.color1 = active ? new Color(0.80f, 0.25f, 0.17f, 1f) : new Color(1f, 0.68f, 0.28f, 1f);
        marker.color2 = new Color(0.08f, 0.07f, 0.06f, 1f);
        marker.alpha = active ? 0.55f : 0.25f;
        marker.SendUpdate();
    }

    private void OnPlayerSleepEnded(BasePlayer player)
    {
        if (player is null || !player.IsConnected) return;
        foreach (Territory territory in _territories) ResendMarker(territory.Marker, player);
        if (_territory is not null && _territory.Temporary) ResendMarker(_territory.Marker, player);
        ResendMarker(_surfaceMarker, player);
        if (_pendingWinners.Count > 0 && !player.IsDead()) RunPendingFor(player.userID);
    }

    private void SyncSleepHook()
    {
        if (_settings.MapMarkers || _settings.SurfaceMarker || _pendingWinners.Count > 0) Subscribe(nameof(OnPlayerSleepEnded));
        else Unsubscribe(nameof(OnPlayerSleepEnded));
    }

    private static void ResendMarker(MapMarkerGenericRadius marker, BasePlayer player)
    {
        if (marker is null || marker.IsDestroyed) return;
        marker.ClientRPC(RpcTarget.Player("MarkerUpdate", player), new Vector3(marker.color1.r, marker.color1.g, marker.color1.b), marker.color1.a,
            new Vector3(marker.color2.r, marker.color2.g, marker.color2.b), marker.alpha, marker.radius);
    }

    private void ShowSurfaceMarker()
    {
        try
        {
            Vector3 center = _origin + _heading * (_arcLength * 0.5f);
            if (GameManager.server.CreateEntity(MarkerPrefab, center) is not MapMarkerGenericRadius marker) return;
            marker.enableSaving = false;
            marker.Spawn();
            _surfaceMarker = marker;
            marker.radius = _settings.SurfaceMarkerRadius / GridMeters;
            marker.color1 = new Color(0.86f, 0.12f, 0.08f, 1f);
            marker.color2 = new Color(0.30f, 0.02f, 0.01f, 1f);
            marker.alpha = 0.8f;
            marker.SendUpdate();
        }
        catch (Exception ex)
        {
            _settings.SurfaceMarker = false;
            PrintError("Surface map marker failed and is off until reload: " + ex.Message);
        }
    }

    private void SyncSurfaceMarker()
    {
        bool up = Exposed();
        if (up && _surfaceMarker is null) ShowSurfaceMarker();
        else if (!up && _surfaceMarker is not null && _headS > _arcLength * 0.5f) HideSurfaceMarker();
    }

    private void HideSurfaceMarker()
    {
        if (_surfaceMarker is not null && !_surfaceMarker.IsDestroyed) _surfaceMarker.Kill();
        _surfaceMarker = null;
    }

    private static string CommandSafe(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.') builder.Append(c);
            else if (char.IsWhiteSpace(c)) builder.Append('_');
        }
        return builder.ToString().TrimStart('_', '-').TrimEnd('_');
    }

    private void RunWinnerCommands(ulong winner, string displayName, Vector3 fall)
    {
        if (winner == 0 || _settings.WinnerCommands.Count == 0) return;
        string id = winner.ToString(CultureInfo.InvariantCulture);
        string name = CommandSafe(displayName);
        if (name.Length == 0) name = id;
        string grid = CommandSafe(MapHelper.PositionToString(fall));
        if (RelationshipManager.FindByID(winner) is { IsConnected: true } player && !player.IsDead())
        {
            RunCommandsFor(winner, name, grid);
            return;
        }
        PrunePending();
        if (_pendingWinners.Count >= MaxPendingWinners)
        {
            PrintWarning($"Winner commands for {id} ({name}) not queued: {MaxPendingWinners} winners are already waiting. Run them by hand.");
            return;
        }
        _pendingWinners.Add(new PendingWinner { Id = winner, Name = name, Grid = grid, Wipe = SaveRestore.WipeId ?? "", ExpiresUtc = DateTime.UtcNow.AddHours(PendingWinnerHours).Ticks });
        _pendingDirty = true;
        SavePendingWinners();
        Subscribe(nameof(OnPlayerSleepEnded));
        Puts($"Winner {id} ({name}) is dead or offline: the winner commands run when they are alive and online again (within {PendingWinnerHours:0} h).");
    }

    private void RunCommandsFor(ulong winner, string name, string grid)
    {
        string id = winner.ToString(CultureInfo.InvariantCulture);
        foreach (string line in _settings.WinnerCommands)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string command = line.Replace("{steamid}", id).Replace("{name}", name).Replace("{grid}", grid);
            try { ConsoleSystem.Run(ConsoleSystem.Option.Server, command); }
            catch (Exception ex) { PrintWarning($"Winner command '{line}' failed: {ex.Message}"); }
        }
    }

    private void RunPendingFor(ulong id)
    {
        PrunePending();
        List<PendingWinner> due = null;
        for (int i = _pendingWinners.Count - 1; i >= 0; i--)
        {
            if (_pendingWinners[i].Id != id) continue;
            (due ??= new List<PendingWinner>()).Add(_pendingWinners[i]);
            _pendingWinners.RemoveAt(i);
        }
        if (due is not null)
        {
            _pendingDirty = true;
            if (!SavePendingWinners())
            {
                _pendingWinners.AddRange(due);
                PrintWarning($"Winner commands for {id} not run: the queue could not be saved, so they wait for the next wake.");
                return;
            }
            foreach (PendingWinner pending in due) RunCommandsFor(pending.Id, pending.Name, pending.Grid);
        }
        if (_pendingWinners.Count == 0) SyncSleepHook();
    }

    private void RunPendingForAwake()
    {
        foreach (BasePlayer player in BasePlayer.activePlayerList)
        {
            if (_pendingWinners.Count == 0) return;
            if (player is not null && player.IsConnected && !player.IsDead() && !player.IsSleeping()) RunPendingFor(player.userID);
        }
    }

    private void PrunePending()
    {
        long now = DateTime.UtcNow.Ticks;
        for (int i = _pendingWinners.Count - 1; i >= 0; i--)
        {
            PendingWinner pending = _pendingWinners[i];
            if (pending.ExpiresUtc > now) continue;
            _pendingWinners.RemoveAt(i);
            _pendingDirty = true;
            PrintWarning($"Winner commands for {pending.Id} ({pending.Name}) expired: the player did not come back alive within {PendingWinnerHours:0} h. Run them by hand if you want.");
        }
    }

    private bool SavePendingWinners()
    {
        if (!_pendingDirty) return true;
        try
        {
            Interface.Oxide.DataFileSystem.WriteObject(WinnersFile, _pendingWinners);
            _pendingDirty = false;
            return true;
        }
        catch (Exception ex)
        {
            PrintWarning("Could not save the waiting winner commands, retrying at the next save: " + ex.Message);
            return false;
        }
    }

    private void LoadPendingWinners()
    {
        List<PendingWinner> saved;
        try { saved = Interface.Oxide.DataFileSystem.ReadObject<List<PendingWinner>>(WinnersFile); }
        catch (Exception ex) { PrintWarning("Waiting winner commands unreadable, none restored: " + ex.Message); return; }
        if (saved is null) return;
        string wipe = SaveRestore.WipeId ?? "";
        foreach (PendingWinner pending in saved)
        {
            if (pending is null || pending.Id == 0 || !string.Equals(pending.Wipe, wipe, StringComparison.Ordinal) || _pendingWinners.Count >= MaxPendingWinners) { _pendingDirty = true; continue; }
            pending.Name = CommandSafe(pending.Name);
            pending.Grid = CommandSafe(pending.Grid);
            if (pending.Name.Length == 0) pending.Name = pending.Id.ToString(CultureInfo.InvariantCulture);
            _pendingWinners.Add(pending);
        }
        PrunePending();
    }

    private void ScheduleEvent(float minutes)
    {
        timer.Destroy(ref _eventTimer);
        if (!_settings.AutoStart || !HasPlaces) return;
        _eventTimer = timer.Once(minutes * 60f, () =>
        {
            _eventTimer = null;
            bool started = false;
            bool blocked = false;
            try { started = _ready && _parts.Count == 0 && BasePlayer.activePlayerList.Count >= _settings.MinPlayers && !(blocked = OtherColossusActive()) && StartEvent(); }
            finally
            {
                if (!started && _ready)
                {
                    float retry = blocked ? Mathf.Min(_settings.IntervalMinutes, 5f + UnityEngine.Random.Range(0f, 5f)) : _settings.IntervalMinutes;
                    if (blocked) Puts($"Automatic start skipped: another Colossus is out; retrying in {retry:0} min.");
                    ScheduleEvent(retry);
                }
            }
        });
    }

    private bool IsColossusActive() => _parts.Count > 0;

    private static readonly string[] OtherColossi = { "ScrapLeviathan", "ScrapStormray" };

    private bool OtherColossusActive()
    {
        foreach (string name in OtherColossi)
            if (plugins.Find(name) is { IsLoaded: true } other && other.Call("IsColossusActive") is true) return true;
        return false;
    }

    private bool StartEvent()
    {
        _raidBlockedStarts = 0;
        bool started = StartEventIn();
        if (!started && _raidBlockedStarts > 0) Puts($"No start; {_raidBlockedStarts} breach spots were skipped next to a RaidableBases base (SpareRaidableBases).");
        return started;
    }

    private bool StartEventIn()
    {
        if (!HasPlaces || _parts.Count > 0 || OtherColossusActive()) return false;
        if (_settings.RandomLocationEachEvent)
        {
            float half = World.Size * 0.5f - _settings.TerritoryRadius - 100f;
            for (int pick = 0; pick < 80 && half > 0f; pick++)
            {
                Vector3 center = new(UnityEngine.Random.Range(-half, half), 0f, UnityEngine.Random.Range(-half, half));
                if (TerritoryProblem(center, _settings.TerritoryRadius) >= 0) continue;
                center.y = TerrainMeta.HeightMap.GetHeight(center);
                var place = new Territory { Name = MapHelper.PositionToString(center), Center = center, Radius = _settings.TerritoryRadius, Temporary = true };
                int result = TryStartIn(place, 10);
                if (result != 0) return result > 0;
            }
            return false;
        }
        int first = UnityEngine.Random.Range(0, _territories.Count);
        for (int t = 0; t < _territories.Count; t++)
        {
            int result = TryStartIn(_territories[(first + t) % _territories.Count], 40);
            if (result != 0) return result > 0;
        }
        return false;
    }

    private int TryStartIn(Territory territory, int attempts)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            Vector2 offset = UnityEngine.Random.insideUnitCircle * territory.Radius * 0.6f;
            Vector3 heading = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f) * Vector3.forward;
            RollArc();
            Vector3 start = territory.Center + new Vector3(offset.x, 0f, offset.y);
            if (!TryPlaceArc(start, heading, territory, !_settings.DestroyBuildings)) continue;
            _territory = territory;
            try { SpawnWorm(); }
            catch (Exception ex)
            {
                RemoveWorm();
                PrintError("Worm event rolled back: " + ex);
                return -1;
            }
            _loop = true;
            _eventEndsAt = Time.realtimeSinceStartup + _settings.LifetimeMinutes * 60f;
            if (territory.Temporary && _settings.MapMarkers) territory.Marker = SpawnMarker(territory, true);
            else PaintMarker(territory.Marker, territory, true);
            Interface.CallHook("OnScrapWormStart", territory.Center, territory.Name);
            Announce(LangKeys.EventStart, territory.Center, territory.Radius + NoticeRadius, MapHelper.PositionToString(_origin), territory.Name);
            PostDiscord(LangKeys.EventStart, MapHelper.PositionToString(_origin), territory.Name);
            return 1;
        }
        return 0;
    }

    private bool WatchEvent(float now)
    {
        _nextWatch = now + WatchSeconds;
        Vector3 center = _origin + _heading * (_arcLength * 0.5f);
        foreach (BasePlayer player in BasePlayer.activePlayerList)
            if (player is not null && player.IsConnected && !player.IsDead() && Near(player.transform.position, center, _settings.IdleRadius)) { _lastSeenAt = now; break; }
        bool idle = now - _lastSeenAt > _settings.IdleMinutes * 60f;
        bool expired = _eventEndsAt > 0f && now > _eventEndsAt;
        if (expired && now <= _eventEndsAt + _settings.LifetimeMinutes * 60f && Fighting(now)) expired = false;
        if (!idle && (!expired || _breaching)) return false;
        string name = _territory?.Name ?? MapHelper.PositionToString(center);
        Vector3 hub = _territory?.Center ?? center;
        float reach = (_territory?.Radius ?? _settings.IdleRadius) + NoticeRadius;
        RemoveWorm();
        Announce(LangKeys.EventLeft, hub, reach, name);
        PostDiscord(LangKeys.EventLeft, name);
        return true;
    }

    private bool Fighting(float now)
        => _biteTarget is not null || now - _lastDamageAt < FightSeconds || _settings.HuntNoise && now - _noiseAt <= _settings.NoiseMemorySeconds;

    private void PayRewards()
    {
        int ranks = Math.Max(_settings.RewardEconomics.Count, _settings.RewardServerRewards.Count);
        if (ranks == 0 || _damageBy.Count == 0) return;
        Plugin economics = Live(Economics, nameof(Economics)), serverRewards = Live(ServerRewards, nameof(ServerRewards));
        var ranking = new List<KeyValuePair<ulong, float>>(_damageBy);
        ranking.Sort(static (a, b) => b.Value.CompareTo(a.Value));
        float floor = _settings.MinRewardShare * _maxHealth;
        for (int rank = 0; rank < ranks && rank < ranking.Count; rank++)
        {
            if (ranking[rank].Value < floor) continue;
            ulong id = ranking[rank].Key;
            double money = rank < _settings.RewardEconomics.Count ? _settings.RewardEconomics[rank] : 0;
            int points = rank < _settings.RewardServerRewards.Count ? _settings.RewardServerRewards[rank] : 0;
            var paid = new List<string>(2);
            if (money > 0 && economics is not null)
            {
                if (economics.Call("Deposit", id, money) is true) paid.Add("$" + money.ToString("N0", CultureInfo.InvariantCulture));
                else PrintWarning($"Economics did not pay rank {rank + 1} ({id}): {money.ToString("N0", CultureInfo.InvariantCulture)} was not deposited.");
            }
            if (points > 0 && serverRewards is not null)
            {
                if (serverRewards.Call("AddPoints", id, points) is true) paid.Add(points.ToString("N0", CultureInfo.InvariantCulture) + " RP");
                else PrintWarning($"ServerRewards did not pay rank {rank + 1} ({id}): {points.ToString("N0", CultureInfo.InvariantCulture)} RP were not added.");
            }
            BasePlayer player = RelationshipManager.FindByID(id);
            if (paid.Count > 0 && player is not null && player.IsConnected) Tell(player, Fmt(Msg(LangKeys.RewardPaid, player), rank + 1, string.Join(" + ", paid)));
        }
    }

    private void PostDiscord(string key, params object[] args)
    {
        if (_settings.DiscordWebhook.Length == 0) return;
        string text = Fmt(lang.GetMessageByLanguage(key, this, LangFile(lang.GetServerLanguage()) ?? "en"), args);
        string body = JsonConvert.SerializeObject(new Dictionary<string, object> { ["content"] = text, ["allowed_mentions"] = new Dictionary<string, object> { ["parse"] = Array.Empty<string>() } });
        webrequest.Enqueue(_settings.DiscordWebhook, body, (code, response) =>
        {
            if (code < 200 || code >= 300) PrintWarning($"Discord webhook answered HTTP {code}.");
        }, this, Oxide.Core.Libraries.RequestMethod.POST, JsonHeaders, 10f);
    }

    private void Announce(string key, Vector3 center, float reach, params object[] args)
    {
        foreach (BasePlayer player in BasePlayer.activePlayerList)
        {
            if (player is null || !player.IsConnected) continue;
            if (!_settings.AnnounceGlobal && !Near(player.transform.position, center, reach)) continue;
            Tell(player, Fmt(Msg(key, player), args));
        }
    }

    private void OnEntityBuilt(Planner planner, GameObject go)
    {
        if (!_settings.BuildWarning || go is null || go.ToBaseEntity() is not BuildingPrivlidge cupboard) return;
        BasePlayer player = planner?.GetOwnerPlayer();
        Territory territory = FindTerritory(cupboard.transform.position);
        if (player is null || territory is null || territory.Temporary || !_warned.Add(player.UserIDString + "|" + territory.Name)) return;
        _warnedDirty = true;
        string text = Fmt(Msg(_settings.DestroyBuildings ? LangKeys.BuildDanger : LangKeys.BuildInfo, player), territory.Name);
        if (Toast(player, text))
        {
            if (_settings.ChatWithNotifications) SendReply(player, text);
            return;
        }
        player.ShowToast(_settings.DestroyBuildings ? GameTip.Styles.Red_Normal : GameTip.Styles.Blue_Long, new Translate.Phrase("scrapworm.territory", text));
        SendReply(player, text);
    }

    private void OnNewSave()
    {
        _warned.Clear();
        _warnedDirty = true;
        SaveWarned();
    }

    private void OnServerSave()
    {
        SaveWarned();
        SavePendingWinners();
    }

    private void SaveWarned()
    {
        if (!_warnedDirty) return;
        try
        {
            Interface.Oxide.DataFileSystem.WriteObject(DataFile, new List<string>(_warned));
            _warnedDirty = false;
        }
        catch (Exception ex) { PrintWarning("Could not save the build warning history, retrying at the next save: " + ex.Message); }
    }

    private void LoadWarned()
    {
        try { foreach (string key in Interface.Oxide.DataFileSystem.ReadObject<List<string>>(DataFile) ?? new List<string>()) _warned.Add(key); }
        catch (Exception ex) { PrintWarning("Build warning history unreadable, starting empty: " + ex.Message); }
    }

    private void RemoveWorm()
    {
        RetireRoar();
        HideSurfaceMarker();
        if (_territory is not null)
        {
            if (_territory.Temporary)
            {
                if (_territory.Marker is not null && !_territory.Marker.IsDestroyed) _territory.Marker.Kill();
                _territory.Marker = null;
            }
            else PaintMarker(_territory.Marker, _territory, false);
            _territory = null;
            if (_eventEndsAt > 0f && _ready) ScheduleEvent(_settings.IntervalMinutes);
        }
        _eventEndsAt = 0f;
        _crushedAt.Clear();
        HideHud();
        timer.Destroy(ref _tick);
        timer.Destroy(ref _rocketTick);
        _rockets.Clear();
        _rocketLast.Clear();
        _thrown.Clear();
        _thrownLast.Clear();
        _pellets.Clear();
        _biteTarget = null;
        _breaching = _loop = false;
        _resumeAt = 0f;
        for (int i = _parts.Count - 1; i >= 0; i--)
        {
            BaseEntity entity = _parts[i];
            if (entity is null || entity.IsDestroyed) continue;
            ReleaseStuck(entity);
            try { entity.Kill(); }
            catch (Exception ex) { PrintError("Failed to remove worm part: " + ex); }
        }
        _parts.Clear();
        for (int segment = Segments - 1; segment >= 0; segment--)
        {
            BaseEntity anchor = _anchors[segment];
            _anchors[segment] = null;
            if (anchor is null || anchor.IsDestroyed) continue;
            try { anchor.Kill(); }
            catch (Exception ex) { PrintError("Failed to remove worm anchor: " + ex); }
        }
        foreach (BaseEntity entity in new List<BaseEntity>(_owned))
        {
            if (entity is null || entity.IsDestroyed) continue;
            try { entity.Kill(); }
            catch (Exception ex) { PrintError("Failed to remove worm piece: " + ex); }
        }
        _owned.Clear();
        _coresAlive = 0;
        ToggleProtection(false);
    }

    private void Unload()
    {
        _ready = false;
        DropCrateFires();
        timer.Destroy(ref _eventTimer);
        RemoveWorm();
        DropRoar();
        SaveWarned();
        if (_settings is not null) SaveLootLocks();
        SavePendingWinners();
        foreach (Territory territory in _territories) if (territory.Marker is not null && !territory.Marker.IsDestroyed) territory.Marker.Kill();
        _territories.Clear();
        for (int i = _debris.Count - 1; i >= 0; i--) if (_debris[i] is not null && !_debris[i].IsDestroyed) _debris[i].Kill();
        _debris.Clear();
        _pieces.Clear();
    }
}
