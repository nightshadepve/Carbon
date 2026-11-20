// NightshadeLocker
// Original Nightshade plugin for Carbon.
// Lock, share, and door-closer behavior is aligned with Ultimate Locker 2.1.5.
// The panel stays the narrow Nightshade layout. UltimateLocker is not modified.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
#if !NSL_RULES_ONLY
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using UnityEngine.UI;

namespace Oxide.Plugins
{
    [Info("NightshadeLocker", "Nightshade", "1.2.1")]
    [Description("Nightshade locks, automatic lock placement, and automatic closing.")]
    public class NightshadeLocker : RustPlugin
    {
        private const string PermUse = "nightshadelocker.use";
        private const string PermAdmin = "nightshadelocker.admin";
        private const string PermAuto = "nightshadelocker.autolock";
        private const string PermFree = "nightshadelocker.autolock.free";
        private const string PermClose = "nightshadelocker.autoclose";
        private const string PermCloseFree = "nightshadelocker.autoclose.free";
        private const string PermVehicleBypass = "nightshadelocker.bypass.vehicle";
        private const string PermForce = "nightshadelocker.bypass.force";
        private const string PermDlc = "nightshadelocker.bypass.dlc";
        private const string DataFile = "Nightshade/NightshadeLocker/State";
        private const string UiRoot = "NightshadeLocker";
        private const string UiCmd = "nslock.ui";
        private const string CodePrefab = "assets/prefabs/locks/keypad/lock.code.prefab";
        private const string PilotPrefab = "assets/prefabs/locks/keypad/skins/codelock_a_pilot/lock.code.a.pilot.prefab";
        private const string PilotShort = "lock.code.a.pilot";
        private const string KeyPrefab = "assets/prefabs/locks/keylock/lock.key.prefab";
        private const string CloserPrefab = "assets/prefabs/misc/doorcloser/doorcloser.prefab";
        private const string DefaultLogo = "https://raw.githubusercontent.com/nightshadepve/Nightshadeserver/refs/heads/main/260625_Logo_75_nobg.png";
        private const string FontReg = "robotocondensed-regular.ttf";
        private const string FontBold = "robotocondensed-bold.ttf";

        private static readonly string[] ReservedAliases =
        {
            "ul", "ultimatelocker", "al", "autolock", "codelock", "locker", "ual",
            "ac", "autoclose", "autocloser", "uad", "closer", "uncloser"
        };

        private Configuration config;
        private Store store;
        private Timer ticker;
        private bool worldReady;
        private bool conflict;
        private bool conflictLogged;
        private string conflictPlugin = string.Empty;
        private float nextPauseLog;
        private readonly HashSet<ulong> toldAutoOff = new HashSet<ulong>();
        private readonly HashSet<string> toldSkip = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, NightshadeLockerRules.PlayerPref> legacyById = new Dictionary<string, NightshadeLockerRules.PlayerPref>(StringComparer.Ordinal);
        private bool dlcWarned;
        private int tickCount;
        private ulong flightSkin;
        private readonly Dictionary<ulong, float> openedAt = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, string> uiPage = new Dictionary<ulong, string>();
        private readonly HashSet<string> commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> pending = new HashSet<int>();

        #region Config and data

        private class Configuration
        {
            [JsonProperty("Chat commands", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> Commands = new List<string> { NightshadeLockerRules.PrimaryCommand };
            [JsonProperty("Extra chat aliases", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> Aliases = new List<string> { "nsl" };
            [JsonProperty("Chat prefix")] public string Prefix = "Nightshade";
            [JsonProperty("Use distance")] public float UseDistance = 6f;
            [JsonProperty("Theme mode (Shop, Auto, or a theme name)")] public string ThemeMode = "Shop";
            [JsonProperty("Theme source config file")] public string ThemeFile = "NightshadeShop.json";
            [JsonProperty("Logo source config file")] public string LogoFile = "NightshadeInfo.json";
            [JsonProperty("Logo URL fallback")] public string LogoUrl = DefaultLogo;
            [JsonProperty("Window width")] public float WindowWidth = NightshadeLockerRules.Layout.WindowWidth;
            [JsonProperty("Window height")] public float WindowHeight = NightshadeLockerRules.Layout.WindowHeight;
            [JsonProperty("Layout version")] public int LayoutVersion = 0;
            [JsonProperty("Plugins that pause entity control while loaded", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> ConflictPlugins = new List<string> { "UltimateLocker" };
            [JsonProperty("Log automatic lock decisions")] public bool LogAuto = false;
            [JsonProperty("Warn if NightshadeLockPickup is loaded")] public bool WarnLockPickup = true;
            [JsonProperty("Legacy preferences file under the data directory")] public string LegacyDataFile = "UltimateLocker.json";
            [JsonProperty("Require building privilege for structures")] public bool RequirePrivilege = true;
            [JsonProperty("Require building privilege for unowned vehicles")] public bool RequirePrivilegeUnownedVehicles = false;
            [JsonProperty("Allow a lock on a vehicle owned by someone else")] public bool AllowOthersVehicles = false;
            [JsonProperty("Allow a lock on an unowned vehicle")] public bool AllowUnownedVehicles = true;
            [JsonProperty("Claim an unowned vehicle when a lock is placed")] public bool ClaimUnownedVehicles = true;
            [JsonProperty("Replace another player's vehicle ownership when a lock is allowed")] public bool ReplaceOtherVehicleOwnership = true;
            [JsonProperty("Block wires on a locked electrical entity")] public bool BlockWires = true;
            [JsonProperty("Block RF frequency changes on a locked broadcaster or receiver")] public bool BlockRf = true;
            [JsonProperty("Block harvesting and cloning on a locked planter")] public bool BlockFarming = true;
            [JsonProperty("Block instruments and boom boxes that are locked")] public bool BlockInstruments = true;
            [JsonProperty("Block turret id, rotate, and mode changes")] public bool BlockTurretControls = true;
            [JsonProperty("Allow pushing a locked vehicle")] public bool AllowVehiclePush = true;
            [JsonProperty("Allow pushing a locked vehicle inside your building privilege")] public bool AllowVehiclePushInPrivilege = true;
            [JsonProperty("Key locks only")] public bool KeyLocksOnly = false;
            [JsonProperty("Force team share (true, false, or null)")] public bool? ForceShare = null;
            [JsonProperty("Reset player toggles to defaults once")] public bool ResyncDefaults = false;
            [JsonProperty("Minimum close delay seconds")] public int MinDelay = 10;
            [JsonProperty("Maximum close delay seconds")] public int MaxDelay = 60;
            [JsonProperty("Default close delay seconds")] public int DefaultDelay = 30;
            [JsonProperty("Show a button near an auto-closing door")] public bool ProximityIcon = false;
            [JsonProperty("Open the door panel from the mouse")] public bool ProximityButton = false;
            [JsonProperty("Mouse chord (1 middle, 2 shift+middle, 3 ctrl+middle)")] public int ProximityChord = 2;
            [JsonProperty("Flight Control skin id (0 finds it by name)")] public int FlightSkinId = 0;
            [JsonProperty("Skip DLC ownership checks (creative or test servers only)")] public bool SkipDlcCheck = false;
            [JsonProperty("Admins may bypass locks without the bypass permission")] public bool AdminBypass = false;
            [JsonProperty("Blocked entity skins for locks", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<ulong> LockSkinBlacklist = new List<ulong>();
            [JsonProperty("Blocked entity skins for closers", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<ulong> CloserSkinBlacklist = new List<ulong>();
            [JsonProperty("Server lock categories", ObjectCreationHandling = ObjectCreationHandling.Replace)] public Dictionary<string, bool> LockCategories = NightshadeLockerRules.DefaultServerLocks();
            [JsonProperty("Server closing categories", ObjectCreationHandling = ObjectCreationHandling.Replace)] public Dictionary<string, bool> CloseCategories = NightshadeLockerRules.DefaultServerCloses();
            [JsonProperty("Player default toggles")] public NightshadeLockerRules.PlayerPref Defaults = NightshadeLockerRules.PlayerPref.CreateDefault();
            [JsonProperty("Vehicle prefabs", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<NightshadeLockerRules.EntityRule> Vehicles = NightshadeLockerRules.DefaultVehicles();
            [JsonProperty("Extra entity prefabs", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<NightshadeLockerRules.EntityRule> Extras = new List<NightshadeLockerRules.EntityRule>();
        }

        private class Store
        {
            public Dictionary<string, NightshadeLockerRules.PlayerPref> Players = new Dictionary<string, NightshadeLockerRules.PlayerPref>(StringComparer.Ordinal);
            public Dictionary<string, LockRecord> Locks = new Dictionary<string, LockRecord>(StringComparer.Ordinal);
            public Dictionary<string, CloserRecord> Closers = new Dictionary<string, CloserRecord>(StringComparer.Ordinal);
            public bool LegacyAutoRestored;
        }

        private class LockRecord
        {
            public ulong LockId;
            public ulong ParentId;
            public ulong OwnerId;
            public string Provenance = NightshadeLockerRules.ProvenancePaid;
            public string Item = "lock.code";
            public string Mode = "lock";
            public List<ulong> Shared = new List<ulong>();
        }

        private class CloserRecord
        {
            public ulong CloserId;
            public ulong DoorId;
            public ulong OwnerId;
            public string Provenance = NightshadeLockerRules.ProvenancePaid;
            public int Delay = 30;
        }

        private class Palette
        {
            public string Name = "Nightshade";
            public string Tagline = "AFTER DARK";
            public string Overlay = "#000000B0";
            public string Background = "#1A1024F2";
            public string Header = "#241634FF";
            public string Panel = "#2A1B3AFF";
            public string Card = "#33224AFF";
            public string Accent = "#8B3FBFFF";
            public string AccentText = "#F2E6FFFF";
            public string Button = "#3E2A55FF";
            public string ButtonText = "#E6D9F2FF";
            public string Text = "#F2EAF7FF";
            public string SubText = "#A898B8FF";
            public string Danger = "#A63A3AFF";
            public string DangerText = "#FFD6D6FF";
            public string Source = "built-in Nightshade fallback";
        }

        protected override void LoadDefaultConfig()
        {
            config = new Configuration();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>() ?? new Configuration();
            }
            catch (Exception ex)
            {
                PrintWarning("NightshadeLocker config could not be read. Using defaults. " + ex.Message);
                config = new Configuration();
            }
            if (config.Commands == null || config.Commands.Count == 0) config.Commands = new List<string> { NightshadeLockerRules.PrimaryCommand };
            if (config.Aliases == null) config.Aliases = new List<string>();
            if (config.LockCategories == null) config.LockCategories = NightshadeLockerRules.DefaultServerLocks();
            else NightshadeLockerRules.FillMissing(config.LockCategories, NightshadeLockerRules.DefaultServerLocks());
            if (config.CloseCategories == null) config.CloseCategories = NightshadeLockerRules.DefaultServerCloses();
            else NightshadeLockerRules.FillMissing(config.CloseCategories, NightshadeLockerRules.DefaultServerCloses());
            if (config.Defaults == null) config.Defaults = NightshadeLockerRules.PlayerPref.CreateDefault();
            if (config.Vehicles == null) config.Vehicles = NightshadeLockerRules.DefaultVehicles();
            if (config.Extras == null) config.Extras = new List<NightshadeLockerRules.EntityRule>();
            if (config.ConflictPlugins == null) config.ConflictPlugins = new List<string> { "UltimateLocker" };
            if (config.LockSkinBlacklist == null) config.LockSkinBlacklist = new List<ulong>();
            if (config.CloserSkinBlacklist == null) config.CloserSkinBlacklist = new List<ulong>();
            if (config.LayoutVersion < NightshadeLockerRules.Layout.Version)
            {
                config.WindowWidth = NightshadeLockerRules.MigrateWindowWidth(config.WindowWidth);
                config.LayoutVersion = NightshadeLockerRules.Layout.Version;
            }
            if (config.WindowWidth < 480f) config.WindowWidth = NightshadeLockerRules.Layout.WindowWidth;
            if (config.WindowHeight < 320f) config.WindowHeight = NightshadeLockerRules.Layout.WindowHeight;
            if (config.MinDelay < 1) config.MinDelay = 1;
            if (config.MaxDelay < config.MinDelay) config.MaxDelay = config.MinDelay;
            config.DefaultDelay = NightshadeLockerRules.ClampDelay(config.DefaultDelay, config.MinDelay, config.MaxDelay);
            NightshadeLockerRules.PlayerPref.Normalize(config.Defaults, config.DefaultDelay);
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        private void LoadStore()
        {
            try
            {
                store = Interface.Oxide.DataFileSystem.ReadObject<Store>(DataFile) ?? new Store();
            }
            catch (Exception ex)
            {
                PrintWarning("NightshadeLocker data could not be read. Starting empty. " + ex.Message);
                store = new Store();
            }
            if (store.Players == null) store.Players = new Dictionary<string, NightshadeLockerRules.PlayerPref>(StringComparer.Ordinal);
            if (store.Locks == null) store.Locks = new Dictionary<string, LockRecord>(StringComparer.Ordinal);
            if (store.Closers == null) store.Closers = new Dictionary<string, CloserRecord>(StringComparer.Ordinal);
            foreach (var pair in store.Players)
            {
                if (pair.Value == null) continue;
                pair.Value.SteamId = pair.Key;
                NightshadeLockerRules.PlayerPref.Normalize(pair.Value, config.DefaultDelay);
            }
            bool resynced = false;
            if (config.ResyncDefaults)
            {
                foreach (var pref in store.Players.Values)
                    NightshadeLockerRules.ApplyToggleDefaults(pref, config.Defaults);
                config.ResyncDefaults = false;
                store.LegacyAutoRestored = true;
                SaveStore();
                SaveConfig();
                resynced = true;
                Puts("Player toggles were reset to the current defaults. Pins and owed items were kept. Resync is now off.");
            }
            try
            {
                LoadLegacyCache();
                if (!resynced) RestoreLegacyAuto();
            }
            catch (Exception ex)
            {
                PrintWarning("NightshadeLocker could not read the legacy locker preferences. " + ex.Message);
            }
        }

        private void LoadLegacyCache()
        {
            legacyById.Clear();
            string path = config.LegacyDataFile;
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!Path.IsPathRooted(path)) path = Path.Combine(Interface.Oxide.DataDirectory, path);
            if (!File.Exists(path)) return;
            var notes = new List<string>();
            var incoming = NightshadeLockerRules.ReadLegacy(File.ReadAllText(path), notes);
            for (int i = 0; i < incoming.Count; i++)
            {
                var row = incoming[i];
                if (row == null || string.IsNullOrEmpty(row.SteamId)) continue;
                legacyById[row.SteamId] = row;
            }
        }

        private void RestoreLegacyAuto()
        {
            if (store.LegacyAutoRestored) return;
            foreach (var pair in legacyById)
            {
                NightshadeLockerRules.PlayerPref have;
                if (!store.Players.TryGetValue(pair.Key, out have) || have == null || pair.Value == null) continue;
                if (!pair.Value.Auto || have.Auto) continue;
                have.Auto = true;
                Puts("NightshadeLocker restored automatic locks for " + pair.Key + ". They were on in Ultimate Locker and off in NightshadeLocker.");
            }
            store.LegacyAutoRestored = true;
            SaveStore();
        }

        private void SaveStore()
        {
            try
            {
                var root = Path.Combine(Interface.Oxide.DataDirectory, "Nightshade", "NightshadeLocker");
                Directory.CreateDirectory(root);
                Interface.Oxide.DataFileSystem.WriteObject(DataFile, store);
            }
            catch (Exception ex)
            {
                PrintWarning("NightshadeLocker data could not be saved. " + ex.Message);
            }
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermAuto, this);
            permission.RegisterPermission(PermFree, this);
            permission.RegisterPermission(PermClose, this);
            permission.RegisterPermission(PermCloseFree, this);
            permission.RegisterPermission(PermVehicleBypass, this);
            permission.RegisterPermission(PermForce, this);
            permission.RegisterPermission(PermDlc, this);
            LoadStore();
            RegisterCommands();
            cmd.AddConsoleCommand(NightshadeLockerRules.PrimaryCommand, this, nameof(ConsoleMain));
            cmd.AddConsoleCommand(UiCmd, this, nameof(ConsoleUi));
        }

        private void OnServerInitialized()
        {
            RefreshConflict(true);
            worldReady = true;
            if (config.SkipDlcCheck && !dlcWarned)
            {
                dlcWarned = true;
                PrintWarning("DLC ownership checks are off. Use that only on a creative or test server.");
            }
            ticker = timer.Every(1f, Tick);
            if (conflict)
                Puts("NightshadeLocker is PAUSED. '" + conflictPlugin + "' is loaded, so code locks will not be placed on doors, boxes, lockers, or anything else. Unload that plugin to turn placement back on. NightshadeLocker does not unload it.");
            else
                Puts("NightshadeLocker ready. Automatic code locks run when the player has nightshadelocker.autolock and Place locks for me is on. Players open it with /" + NightshadeLockerRules.PrimaryCommand + ".");
        }

        private void Unload()
        {
            worldReady = false;
            if (ticker != null)
            {
                ticker.Destroy();
                ticker = null;
            }
            openedAt.Clear();
            foreach (var player in BasePlayer.activePlayerList)
                CuiHelper.DestroyUi(player, UiRoot);
            uiPage.Clear();
            SaveStore();
        }

        private void OnServerSave() => SaveStore();

        private void OnPluginLoaded(Plugin plugin)
        {
            _ = plugin;
            RefreshConflict(true);
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            _ = plugin;
            RefreshConflict(true);
        }

        private void OnNewSave(string filename)
        {
            _ = filename;
            if (store == null) return;
            store.Locks.Clear();
            store.Closers.Clear();
            openedAt.Clear();
            SaveStore();
            Puts("NightshadeLocker cleared placed locks and closers for the new save. Player settings were kept.");
        }

        private void Tick()
        {
            if (!worldReady) return;
            tickCount++;
            if (tickCount % 10 == 0) RefreshConflict(false);
            if (conflict) return;
            CloseDueDoors();
            if (config.ProximityIcon && tickCount % 2 == 0) RefreshProximity();
            if (tickCount % 15 == 0) DeliverAllOwed();
        }

        private void RegisterCommands()
        {
            foreach (var name in CommandNames())
            {
                if (commands.Contains(name)) continue;
                if (IsReserved(name))
                {
                    PrintWarning("Alias '" + name + "' is not registered. It belongs to the other locker plugin. Use /" + NightshadeLockerRules.PrimaryCommand + ".");
                    continue;
                }
                commands.Add(name);
                cmd.AddChatCommand(name, this, nameof(ChatMain));
            }
        }

        private IEnumerable<string> CommandNames()
        {
            foreach (var name in config.Commands) yield return CleanCommand(name);
            foreach (var name in config.Aliases) yield return CleanCommand(name);
        }

        private static string CleanCommand(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            name = name.Trim().TrimStart('/').ToLowerInvariant();
            return name;
        }

        private static bool IsReserved(string name)
        {
            for (int i = 0; i < ReservedAliases.Length; i++)
                if (ReservedAliases[i] == name) return true;
            return false;
        }

        #endregion

        #region Permissions and conflict

        private bool HasPerm(BasePlayer player, string perm)
        {
            return player != null && permission.UserHasPermission(player.UserIDString, perm);
        }

        private bool CanUse(BasePlayer player) => HasPerm(player, PermUse) || HasPerm(player, PermAdmin);

        private bool IsStaff(BasePlayer player) => HasPerm(player, PermAdmin);

        private void RefreshConflict(bool announce)
        {
            bool found = false;
            string which = string.Empty;
            if (config.ConflictPlugins != null)
            {
                for (int i = 0; i < config.ConflictPlugins.Count; i++)
                {
                    var name = config.ConflictPlugins[i];
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var plugin = plugins.Find(name);
                    if (plugin != null)
                    {
                        found = true;
                        which = name;
                        break;
                    }
                }
            }
            conflictPlugin = found ? which : string.Empty;
            if (found != conflict || announce)
            {
                conflict = found;
                if (found && (announce || !conflictLogged))
                {
                    conflictLogged = true;
                    string paused = "NightshadeLocker is PAUSED while '" + which + "' is loaded. Locks will not be placed, removed, or blocked. Unload '" + which + "' before switching over. NightshadeLocker does not unload it.";
                    PrintWarning(paused);
                    Puts(paused);
                }
                else if (!found && conflictLogged)
                {
                    conflictLogged = false;
                    Puts("NightshadeLocker is the only locker plugin loaded. Entity control is on. Automatic code locks are active for players who turned them on.");
                }
            }
            if (config.WarnLockPickup && plugins.Find("NightshadeLockPickup") != null && announce)
                PrintWarning("NightshadeLockPickup is loaded. That patch is for the other locker plugin. Do not run it with NightshadeLocker.");
        }

        private bool Paused(BasePlayer player, bool tell)
        {
            if (!conflict) return false;
            if (tell && player != null)
            {
                string name = string.IsNullOrEmpty(conflictPlugin) ? "another locker plugin" : conflictPlugin;
                Reply(player, "NightshadeLocker is paused because '" + name + "' is loaded.");
            }
            return true;
        }

        #endregion

        #region Commands

        private void ChatMain(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            Handle(player, args ?? new string[0], false);
        }

        private void ConsoleMain(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            var args = ArgsOf(arg);
            if (player == null)
            {
                if (!arg.IsAdmin) return;
                ServerHandle(args);
                return;
            }
            Handle(player, args, false);
        }

        private void ConsoleUi(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !CanUse(player)) return;
            UiAction(player, ArgsOf(arg));
        }

        private static string[] ArgsOf(ConsoleSystem.Arg arg)
        {
            if (arg == null || !arg.HasArgs()) return new string[0];
            int count = 0;
            while (arg.HasArgs(count + 1)) count++;
            var copy = new string[count];
            for (int i = 0; i < count; i++)
            {
                string text = arg.GetString(i);
                copy[i] = text ?? string.Empty;
            }
            return copy;
        }

        private void Handle(BasePlayer player, string[] args, bool forceAdmin)
        {
            if (!CanUse(player))
            {
                Reply(player, "You need the nightshadelocker.use permission.");
                return;
            }
            if (args.Length == 0)
            {
                OpenUi(player, "locks");
                return;
            }
            string verb = args[0].ToLowerInvariant();
            if (verb == "help")
            {
                Reply(player, "Open the panel with /" + NightshadeLockerRules.PrimaryCommand + ". Also: toggle, place, closer, uncloser, code, codeall, guest, show, delay, closetoggle.");
                return;
            }
            if (verb == "admin" || forceAdmin)
            {
                if (!IsStaff(player))
                {
                    Reply(player, "You need the nightshadelocker.admin permission.");
                    return;
                }
                Admin(player, verb == "admin" ? Shift(args) : args);
                return;
            }
            if (Paused(player, true) && verb != "help") return;
            switch (verb)
            {
                case "toggle": ToggleAuto(player); break;
                case "closetoggle": ToggleClose(player); break;
                case "place": PlaceLook(player); break;
                case "closer": PlaceCloserLook(player); break;
                case "uncloser": RemoveCloserLook(player); break;
                case "code": SetLookCode(player, args.Length > 1 ? args[1] : null, false); break;
                case "codeall": SetAllCodes(player, args.Length > 1 ? args[1] : null); break;
                case "guest": SetGuest(player, args.Length > 1 ? args[1] : null, false); break;
                case "show": ShowCode(player, false); break;
                case "delay": SetLookDelay(player, args.Length > 1 ? args[1] : null); break;
                default:
                    Reply(player, "Unknown option. Open /" + NightshadeLockerRules.PrimaryCommand + " or use help.");
                    break;
            }
        }

        private void ServerHandle(string[] args)
        {
            if (args.Length == 0 || args[0].ToLowerInvariant() != "admin")
            {
                Puts("From the server console use: nslock admin status");
                return;
            }
            var rest = Shift(args);
            if (rest.Length == 0 || rest[0].ToLowerInvariant() == "status")
            {
                Puts(StatusText());
                return;
            }
            Puts("That admin action needs a player. Use status from the console, or run it in game.");
        }

        private static string[] Shift(string[] args)
        {
            if (args == null || args.Length <= 1) return new string[0];
            var copy = new string[args.Length - 1];
            Array.Copy(args, 1, copy, 0, copy.Length);
            return copy;
        }

        private void Admin(BasePlayer player, string[] args)
        {
            if (args.Length == 0)
            {
                Reply(player, "Admin: status, grants, import, show, lock, unlock, remove, code, codeall, lockall, unlockall, removeall.");
                return;
            }
            string verb = args[0].ToLowerInvariant();
            if (verb == "status") { Reply(player, StatusText()); return; }
            if (verb == "grants") { Reply(player, GrantText()); return; }
            if (verb == "import") { Reply(player, ImportLegacy(player)); return; }
            if (Paused(player, true)) return;
            var target = Look(player);
            switch (verb)
            {
                case "show": ShowCode(player, true); break;
                case "lock": SetLockFlag(player, target, true); break;
                case "unlock": SetLockFlag(player, target, false); break;
                case "remove": RemoveLook(player, args.Any(a => a.Equals("norefund", StringComparison.OrdinalIgnoreCase))); break;
                case "code": SetLookCode(player, args.Length > 1 ? args[1] : null, true); break;
                case "codeall":
                    if (args.Length < 3) { Reply(player, "Use: nslock admin codeall <steamId> <4 digits>"); return; }
                    SetAllCodesFor(player, args[1], args[2]);
                    break;
                case "lockall": FlagAll(player, args.Length > 1 ? args[1] : null, true); break;
                case "unlockall": FlagAll(player, args.Length > 1 ? args[1] : null, false); break;
                case "removeall": RemoveAll(player, args); break;
                default: Reply(player, "Unknown admin option."); break;
            }
        }

        private string StatusText()
        {
            RefreshConflict(false);
            var theme = LoadPalette();
            return "NightshadeLocker command /" + NightshadeLockerRules.PrimaryCommand
                + ". Conflict pause: " + (conflict ? "yes (" + conflictPlugin + ")" : "no")
                + ". Theme: " + theme.Name + " (" + theme.Source + ")."
                + " Players: " + store.Players.Count
                + ". Tracked locks: " + store.Locks.Count
                + ". Tracked closers: " + store.Closers.Count + ".";
        }

        private static string GrantText()
        {
            return "NightshadeLocker does not edit groups. Map the old grants yourself, then add the new ones. "
                + "default and default_unlocked had use + autolock. "
                + "vip_base(nouser) had use + autolock + free locks. "
                + "vip_1 had free locks. "
                + "vipx had autoclose + free closers. "
                + "New names: nightshadelocker.use, nightshadelocker.autolock, nightshadelocker.autolock.free, "
                + "nightshadelocker.autoclose, nightshadelocker.autoclose.free, nightshadelocker.admin, "
                + "nightshadelocker.bypass.vehicle, nightshadelocker.bypass.force, nightshadelocker.bypass.dlc.";
        }

        #endregion

        #region Player prefs

        private NightshadeLockerRules.PlayerPref Pref(BasePlayer player)
        {
            var id = player.UserIDString;
            NightshadeLockerRules.PlayerPref pref;
            if (!store.Players.TryGetValue(id, out pref) || pref == null)
            {
                NightshadeLockerRules.PlayerPref legacy;
                if (legacyById.TryGetValue(id, out legacy) && legacy != null)
                {
                    pref = legacy;
                    legacyById.Remove(id);
                    Puts("NightshadeLocker copied lock preferences for " + id + " from Ultimate Locker. Automatic locks are " + (pref.Auto ? "on" : "off") + ".");
                }
                else
                {
                    pref = NightshadeLockerRules.PlayerPref.CreateDefault();
                    NightshadeLockerRules.ApplyToggleDefaults(pref, config.Defaults);
                }
                pref.SteamId = id;
                pref.CloseDelay = config.DefaultDelay;
                store.Players[id] = pref;
                SaveStore();
            }
            NightshadeLockerRules.PlayerPref.Normalize(pref, config.DefaultDelay);
            pref.SteamId = id;
            return pref;
        }

        private bool WantsShare(NightshadeLockerRules.PlayerPref pref)
        {
            if (config.ForceShare.HasValue) return config.ForceShare.Value;
            return pref.Share;
        }

        private void ToggleAuto(BasePlayer player)
        {
            if (!HasPerm(player, PermAuto) && !IsStaff(player))
            {
                Reply(player, "You need the nightshadelocker.autolock permission.");
                return;
            }
            var pref = Pref(player);
            pref.Auto = !pref.Auto;
            SaveStore();
            Reply(player, pref.Auto ? "Automatic locks are on." : "Automatic locks are off.");
        }

        private void ToggleClose(BasePlayer player)
        {
            if (!HasPerm(player, PermClose) && !IsStaff(player))
            {
                Reply(player, "You need the nightshadelocker.autoclose permission.");
                return;
            }
            var pref = Pref(player);
            pref.AutoClose = !pref.AutoClose;
            SaveStore();
            Reply(player, pref.AutoClose ? "Automatic closing is on." : "Automatic closing is off.");
        }

        #endregion

        #region Placement

        private void OnEntityBuilt(Planner plan, GameObject go)
        {
            if (!worldReady || plan == null || go == null) return;
            var player = plan.GetOwnerPlayer();
            var entity = go.ToBaseEntity();
            if (player == null || entity == null) return;
            QueueAuto(player, entity, entity is Door);
        }

        private void OnItemDeployed(Deployer deployer, ItemModDeployable mod, BaseEntity entity)
        {
            _ = mod;
            if (!worldReady || deployer == null || entity == null) return;
            var player = deployer.GetOwnerPlayer();
            if (player == null) return;
            QueueAuto(player, entity, entity is Door);
        }

        private void OnItemDeployed(Deployer deployer, BaseEntity parent, BaseEntity entity)
        {
            _ = parent;
            if (!worldReady || deployer == null || entity == null) return;
            var player = deployer.GetOwnerPlayer();
            if (player == null) return;
            QueueAuto(player, entity, entity is Door);
        }

        private void OnEntitySpawned(BaseVehicle vehicle)
        {
            if (!worldReady || vehicle == null || vehicle.OwnerID == 0) return;
            var player = BasePlayer.FindByID(vehicle.OwnerID);
            if (player != null) QueueAuto(player, vehicle, false);
        }

        private void OnEntitySpawned(HotAirBalloon balloon)
        {
            if (!worldReady || balloon == null || balloon.OwnerID == 0) return;
            var player = BasePlayer.FindByID(balloon.OwnerID);
            if (player != null) QueueAuto(player, balloon, false);
        }

        private void QueueAuto(BasePlayer player, BaseEntity entity, bool closer)
        {
            if (player == null || entity == null) return;
            if (conflict)
            {
                NotePaused(entity);
                return;
            }
            int token = entity.GetInstanceID();
            if (!pending.Add(token)) return;
            ulong user = player.userID;
            BasePlayer captured = player;
            NextTick(() =>
            {
                pending.Remove(token);
                if (!worldReady || entity == null || entity.IsDestroyed) return;
                if (conflict)
                {
                    NotePaused(entity);
                    return;
                }
                var still = captured;
                if (still == null || !still.IsConnected)
                    still = BasePlayer.FindByID(user) ?? BasePlayer.FindSleeping(user);
                if (still == null)
                {
                    NoteAuto(player, "The player was not online a tick after placement, so no lock was added.", false);
                    return;
                }
                try
                {
                    TryAutoLock(still, entity);
                    if (closer && entity is Door) TryAutoCloser(still, entity as Door, true);
                }
                catch (Exception ex)
                {
                    PrintWarning("NightshadeLocker failed while placing a lock on " + (entity.ShortPrefabName ?? "an entity") + ". " + ex.Message);
                }
            });
        }

        private void NotePaused(BaseEntity entity)
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextPauseLog) return;
            nextPauseLog = now + 15f;
            string name = string.IsNullOrEmpty(conflictPlugin) ? "another locker plugin" : conflictPlugin;
            string what = entity != null && !string.IsNullOrEmpty(entity.ShortPrefabName) ? entity.ShortPrefabName : "that entity";
            Puts("NightshadeLocker is PAUSED because '" + name + "' is loaded. No lock was placed on " + what + ". Unload '" + name + "' to let NightshadeLocker place code locks. This plugin does not unload it.");
        }

        private void NoteAuto(BasePlayer player, string reason, bool tellPlayer)
        {
            if (string.IsNullOrEmpty(reason)) return;
            bool first = true;
            if (tellPlayer && player != null)
                first = toldSkip.Add(player.UserIDString + "\n" + reason);
            bool log = (config != null && config.LogAuto) || (tellPlayer && first);
            if (!log) return;
            string who = player != null ? player.displayName + " (" + player.UserIDString + ")" : "unknown player";
            Puts("NightshadeLocker did not place a lock for " + who + ". " + reason);
            if (tellPlayer && first && player != null) Reply(player, reason);
        }

        private void TryAutoLock(BasePlayer player, BaseEntity entity)
        {
            if (!HasPerm(player, PermAuto) && !IsStaff(player))
            {
                NoteAuto(player, "Missing nightshadelocker.autolock.", false);
                return;
            }
            var pref = Pref(player);
            if (!pref.Auto)
            {
                if (toldAutoOff.Add(player.userID))
                    Puts("NightshadeLocker: automatic locks are off for " + player.displayName + " (" + player.UserIDString + "). Turn on Place locks for me in /" + NightshadeLockerRules.PrimaryCommand + ". Nothing is placed until then.");
                NoteAuto(player, "Automatic locks are off for this player.", false);
                return;
            }
            var match = Match(entity);
            if (match == null)
            {
                NoteAuto(player, "Not a lockable entity (" + (entity.ShortPrefabName ?? "unknown") + ").", false);
                return;
            }
            bool serverOn = CategoryOn(config.LockCategories, match.Category);
            bool playerOn = pref.LockOn(match.Category);
            if (!NightshadeLockerRules.AutoWanted(serverOn, pref.Auto, playerOn))
            {
                string why = !serverOn
                    ? "The server has locks turned off for " + match.Category + "."
                    : "Automatic locks are off for " + match.Category + " in your settings.";
                NoteAuto(player, why, true);
                return;
            }
            var denial = Denial(player, entity, match, pref, true);
            if (denial != null)
            {
                NoteAuto(player, denial, denial != "That already has a lock.");
                return;
            }
            var result = PlaceLock(player, entity, match, pref, false);
            if (!string.IsNullOrEmpty(result)) Reply(player, result);
            if (config.LogAuto && !string.IsNullOrEmpty(result))
                Puts("NightshadeLocker lock result for " + player.displayName + " on " + (entity.ShortPrefabName ?? match.Category) + ": " + result);
        }

        private void PlaceLook(BasePlayer player)
        {
            var entity = Look(player);
            if (entity == null) { Reply(player, "Look at the thing you want to lock."); return; }
            var match = Match(entity);
            var pref = Pref(player);
            var denial = Denial(player, entity, match, pref, false);
            if (denial != null) { Reply(player, denial); return; }
            var result = PlaceLock(player, entity, match, pref, true);
            if (result != null) Reply(player, result);
        }

        private string Denial(BasePlayer player, BaseEntity entity, NightshadeLockerRules.Match match, NightshadeLockerRules.PlayerPref pref, bool automatic)
        {
            bool free = HasPerm(player, PermFree);
            bool hasCode = HasCodeLock(player);
            bool hasKey = HasItem(player, "lock.key");
            var choice = NightshadeLockerRules.ChooseLock(config.KeyLocksOnly, hasCode, hasKey, pref.KeyFallback, free);
            ulong owner = entity.OwnerID;
            bool team = IsWith(player, owner);
            var request = new NightshadeLockerRules.PlaceRequest
            {
                Conflict = conflict,
                HasUse = CanUse(player),
                Known = match != null,
                CategoryOpen = match != null && CategoryOn(config.LockCategories, match.Category),
                AlreadyLocked = HasLock(Host(entity)),
                SkinBlocked = NightshadeLockerRules.SkinBlocked(entity.skinID, config.LockSkinBlacklist),
                Vehicle = match != null && (match.Category == "vehicles" || match.Category == "naval"),
                RequirePrivilege = config.RequirePrivilege,
                HasPrivilege = HasPrivilege(player, entity),
                RequireUnownedPrivilege = config.RequirePrivilegeUnownedVehicles,
                Unowned = owner == 0,
                OwnedByOther = owner != 0 && owner != player.userID && !(WantsShare(pref) && team),
                AllowUnowned = config.AllowUnownedVehicles,
                AllowOthers = config.AllowOthersVehicles,
                BypassVehicle = HasPerm(player, PermVehicleBypass),
                HasItemOrFree = choice.Item != null,
                JustPlaced = automatic && owner == 0
            };
            return NightshadeLockerRules.PlaceDenial(request);
        }

        private string PlaceLock(BasePlayer player, BaseEntity entity, NightshadeLockerRules.Match match, NightshadeLockerRules.PlayerPref pref, bool tell)
        {
            var host = Host(entity);
            if (host is ModularCar) return PlaceCarLock(player, host as ModularCar, match, pref, tell);
            if (host is SteeringWheel) return PlaceBoatLock(player, host as SteeringWheel, match, pref, tell);
            if (host == null || !host.HasSlot(BaseEntity.Slot.Lock)) return "That cannot take a lock.";
            bool freePerm = HasPerm(player, PermFree);
            bool hasCode = HasCodeLock(player);
            bool hasKey = HasItem(player, "lock.key");
            var choice = NightshadeLockerRules.ChooseLock(config.KeyLocksOnly, hasCode, hasKey, pref.KeyFallback, freePerm);
            if (choice.Item == null) return "You need a lock in your inventory.";
            string pin = EnsurePin(player, pref, choice.Item);
            bool consumed = false;
            bool pilot = false;
            if (!choice.Free)
            {
                bool took = choice.Item == "lock.code" ? TakeCodeLock(player, out pilot) : TakeItem(player, choice.Item);
                if (!took) return "The lock could not be taken from your inventory.";
                consumed = true;
            }
            BaseLock created = choice.Item == "lock.key"
                ? (BaseLock)CreateKeyLock(player, host)
                : CreateCodeLock(player, host, pref, pin, pilot);
            if (created == null)
            {
                if (consumed) ReturnOrOwe(player, pilot ? PilotShort : choice.Item, 1);
                SaveStore();
                return "The lock could not be placed. A paid lock was returned or marked owed.";
            }
            var record = new LockRecord
            {
                LockId = IdOf(created),
                ParentId = IdOf(host),
                OwnerId = player.userID,
                Provenance = choice.Free ? NightshadeLockerRules.ProvenanceFree : NightshadeLockerRules.ProvenancePaid,
                Item = choice.Item,
                Mode = "lock"
            };
            ShareLock(player, created, pref, record);
            if (record.LockId != 0) store.Locks[record.LockId.ToString()] = record;
            ClaimVehicle(player, host, match);
            SaveStore();
            _ = tell;
            return PlacedText(choice, pref, pin);
        }

        private string EnsurePin(BasePlayer player, NightshadeLockerRules.PlayerPref pref, string item)
        {
            string pin = pref.Code;
            if (item == "lock.code" && !NightshadeLockerRules.PinValid(pin))
            {
                pin = NightshadeLockerRules.MakePin(player.userID, DateTime.UtcNow.Ticks);
                pref.Code = pin;
            }
            return pin;
        }

        private static string PlacedText(NightshadeLockerRules.LockChoice choice, NightshadeLockerRules.PlayerPref pref, string pin)
        {
            string message = choice.Free ? "A lock was placed without using one from your inventory." : "Lock placed.";
            if (choice.Item == "lock.code" && pref != null && !pref.Streamer && NightshadeLockerRules.PinValid(pin))
                message += " Code " + pin + ".";
            return message;
        }

        private void ClaimVehicle(BasePlayer player, BaseEntity entity, NightshadeLockerRules.Match match)
        {
            if (player == null || entity == null || match == null) return;
            if (match.Category != "vehicles" && match.Category != "naval") return;
            bool bypass = HasPerm(player, PermVehicleBypass);
            bool team = IsWith(player, entity.OwnerID);
            if (NightshadeLockerRules.MayClaim(entity.OwnerID, player.userID, config.ClaimUnownedVehicles))
                entity.OwnerID = player.userID;
            else if (NightshadeLockerRules.MayReplaceOwner(entity.OwnerID, player.userID, config.ReplaceOtherVehicleOwnership, (config.AllowOthersVehicles || bypass) && !(WantsShare(Pref(player)) && team)))
                entity.OwnerID = player.userID;
        }

        private CodeLock CreateCodeLock(BasePlayer player, BaseEntity entity, NightshadeLockerRules.PlayerPref pref, string pin, bool pilot)
        {
            string prefab = pilot ? PilotPrefab : CodePrefab;
            var codeLock = GameManager.server.CreateEntity(prefab, entity.transform.position, entity.transform.rotation) as CodeLock;
            if (codeLock == null) return null;
            try
            {
                if (!pilot)
                {
                    ulong skin = ResolveSkin(player, pref);
                    if (skin != 0) codeLock.skinID = skin;
                }
                codeLock.OwnerID = player.userID;
                codeLock.code = pin ?? string.Empty;
                codeLock.hasCode = NightshadeLockerRules.PinValid(pin);
                if (pref.GuestEnabled && NightshadeLockerRules.PinValid(pref.Guest))
                {
                    codeLock.guestCode = pref.Guest;
                    codeLock.hasGuestCode = true;
                }
                Whitelist(codeLock, player.userID);
                // Spawn before SetParent. On this build SetParent reads net and sends a network update, and net is null until Spawn.
                codeLock.Spawn();
                if (!AttachToSlot(entity, codeLock, BaseEntity.Slot.Lock, "lock"))
                {
                    if (!codeLock.IsDestroyed) codeLock.Kill();
                    return null;
                }
                Whitelist(codeLock, player.userID);
                SetLocked(codeLock, true);
                codeLock.SendNetworkUpdateImmediate();
                return codeLock;
            }
            catch (Exception ex)
            {
                PrintWarning("NightshadeLocker could not create a code lock. " + ex.Message);
                try { if (codeLock != null && !codeLock.IsDestroyed) codeLock.Kill(); } catch (Exception) { }
                return null;
            }
        }

        private static void Whitelist(CodeLock codeLock, ulong userId)
        {
            if (codeLock == null || userId == 0) return;
            if (codeLock.whitelistPlayers == null) codeLock.whitelistPlayers = new List<ulong>();
            if (!codeLock.whitelistPlayers.Contains(userId)) codeLock.whitelistPlayers.Add(userId);
        }

        private static bool AttachToSlot(BaseEntity parent, BaseEntity child, BaseEntity.Slot slot, string fallbackBone)
        {
            if (parent == null || child == null) return false;
            string bone = fallbackBone;
            try
            {
                string named = parent.GetSlotAnchorName(slot);
                if (!string.IsNullOrEmpty(named)) bone = named;
            }
            catch (Exception)
            {
                bone = fallbackBone;
            }
            try
            {
                if (!string.IsNullOrEmpty(bone)) child.SetParent(parent, bone);
                else child.SetParent(parent);
            }
            catch (Exception)
            {
                try { child.SetParent(parent); }
                catch (Exception) { return false; }
            }
            parent.SetSlot(slot, child);
            return true;
        }

        private KeyLock CreateKeyLock(BasePlayer player, BaseEntity entity)
        {
            var keyLock = GameManager.server.CreateEntity(KeyPrefab, entity.transform.position, entity.transform.rotation) as KeyLock;
            if (keyLock == null) return null;
            try
            {
                keyLock.OwnerID = player.userID;
                keyLock.keyCode = UnityEngine.Random.Range(1, 100000);
                keyLock.firstKeyCreated = false;
                keyLock.Spawn();
                if (!AttachToSlot(entity, keyLock, BaseEntity.Slot.Lock, "lock"))
                {
                    if (!keyLock.IsDestroyed) keyLock.Kill();
                    return null;
                }
                SetLocked(keyLock, true);
                keyLock.SendNetworkUpdateImmediate();
                return keyLock;
            }
            catch (Exception ex)
            {
                PrintWarning("NightshadeLocker could not create a key lock. " + ex.Message);
                try { if (keyLock != null && !keyLock.IsDestroyed) keyLock.Kill(); } catch (Exception) { }
                return null;
            }
        }

        private string PlaceCarLock(BasePlayer player, ModularCar car, NightshadeLockerRules.Match match, NightshadeLockerRules.PlayerPref pref, bool tell)
        {
            _ = tell;
            if (car == null || car.CarLock == null) return "The lock could not be placed.";
            if (car.CarLock.HasALock) return "That already has a lock.";
            bool freePerm = HasPerm(player, PermFree);
            bool hasCode = HasCodeLock(player);
            bool hasKey = HasItem(player, "lock.key");
            var choice = NightshadeLockerRules.ChooseLock(config.KeyLocksOnly, hasCode, hasKey, pref.KeyFallback, freePerm);
            if (choice.Item != "lock.code" && !choice.Free) return "A modular car needs a code lock.";
            if (choice.Item == null) return "You need a lock in your inventory.";
            string pin = EnsurePin(player, pref, "lock.code");
            bool consumed = false;
            bool pilot = false;
            if (!choice.Free)
            {
                if (!TakeCodeLock(player, out pilot)) return "The lock could not be taken from your inventory.";
                consumed = true;
            }
            if (!car.CarLock.TrySetNewCode(pin, player.userID))
            {
                if (consumed) ReturnOrOwe(player, pilot ? PilotShort : "lock.code", 1);
                return "The lock could not be placed. A paid lock was returned or marked owed.";
            }
            using (var flags = car.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))
                flags.Set(ModularCarCodeLock.FLAG_CENTRAL_LOCKING, true);
            car.CarLock.TryAddPlayer(player.userID);
            var members = ShareIds(player, pref);
            for (int i = 0; i < members.Count; i++) car.CarLock.TryAddPlayer(members[i]);
            car.OwnerID = player.userID;
            RememberSpecial(car, player.userID, choice, "car", members);
            ClaimVehicle(player, car, match);
            SaveStore();
            return PlacedText(choice, pref, pin);
        }

        private string PlaceBoatLock(BasePlayer player, SteeringWheel wheel, NightshadeLockerRules.Match match, NightshadeLockerRules.PlayerPref pref, bool tell)
        {
            _ = tell;
            if (wheel == null || wheel.BoatLock == null) return "The lock could not be placed.";
            if (wheel.BoatLock.HasALock) return "That already has a lock.";
            bool freePerm = HasPerm(player, PermFree);
            bool hasCode = HasCodeLock(player);
            bool hasKey = HasItem(player, "lock.key");
            var choice = NightshadeLockerRules.ChooseLock(config.KeyLocksOnly, hasCode, hasKey, pref.KeyFallback, freePerm);
            if (choice.Item == null) return "You need a lock in your inventory.";
            if (choice.Item != "lock.code" && !choice.Free) return "That boat needs a code lock.";
            string pin = EnsurePin(player, pref, "lock.code");
            if (!wheel.BoatLock.IsValidLockCode(pin)) return "Use 4 digits.";
            bool consumed = false;
            bool pilot = false;
            if (!choice.Free)
            {
                if (!TakeCodeLock(player, out pilot)) return "The lock could not be taken from your inventory.";
                consumed = true;
            }
            if (!wheel.BoatLock.TrySetNewCode(pin, player))
            {
                if (consumed) ReturnOrOwe(player, pilot ? PilotShort : "lock.code", 1);
                return "The lock could not be placed. A paid lock was returned or marked owed.";
            }
            SetLocked(wheel, true);
            wheel.OwnerID = player.userID;
            var members = ShareIds(player, pref);
            ShareBoat(wheel, player, members);
            RememberSpecial(wheel, player.userID, new NightshadeLockerRules.LockChoice { Item = "lock.code", Free = choice.Free }, "boat", members);
            ClaimVehicle(player, wheel, match);
            SaveStore();
            return PlacedText(new NightshadeLockerRules.LockChoice { Item = "lock.code", Free = choice.Free }, pref, pin);
        }

        private void RememberSpecial(BaseEntity entity, ulong owner, NightshadeLockerRules.LockChoice choice, string mode, List<ulong> shared)
        {
            ulong id = IdOf(entity);
            if (id == 0) return;
            store.Locks[id.ToString()] = new LockRecord
            {
                LockId = id,
                ParentId = id,
                OwnerId = owner,
                Provenance = choice.Free ? NightshadeLockerRules.ProvenanceFree : NightshadeLockerRules.ProvenancePaid,
                Item = "lock.code",
                Mode = mode,
                Shared = shared ?? new List<ulong>()
            };
        }

        private void ShareLock(BasePlayer player, BaseLock lck, NightshadeLockerRules.PlayerPref pref, LockRecord record)
        {
            var code = lck as CodeLock;
            if (code == null) return;
            if (code.whitelistPlayers == null) code.whitelistPlayers = new List<ulong>();
            if (!code.whitelistPlayers.Contains(player.userID)) code.whitelistPlayers.Add(player.userID);
            record.Shared = ShareIds(player, pref);
            for (int i = 0; i < record.Shared.Count; i++)
            {
                if (!code.whitelistPlayers.Contains(record.Shared[i]))
                    code.whitelistPlayers.Add(record.Shared[i]);
            }
            code.SendNetworkUpdate();
        }

        private List<ulong> ShareIds(BasePlayer player, NightshadeLockerRules.PlayerPref pref)
        {
            if (player == null || !WantsShare(pref)) return new List<ulong>();
            return NightshadeLockerRules.ShareTargets(player.userID, TeamIds(player), true);
        }

        private List<ulong> MemberIds(ulong ownerId, BasePlayer owner, NightshadeLockerRules.PlayerPref pref)
        {
            if (owner != null) return ShareIds(owner, pref);
            var list = new List<ulong>();
            if (RelationshipManager.ServerInstance != null)
            {
                var team = RelationshipManager.ServerInstance.FindPlayersTeam(ownerId);
                if (team != null && team.members != null) list.AddRange(team.members);
            }
            return NightshadeLockerRules.ShareTargets(ownerId, list, true);
        }

        private void ShareBoat(SteeringWheel wheel, BasePlayer player, List<ulong> members)
        {
            if (wheel == null || wheel.Privilege == null) return;
            var auth = wheel.Privilege.authorizedPlayers;
            if (auth == null) return;
            if (!auth.Contains(player.userID)) auth.Add(player.userID);
            if (members == null) return;
            for (int i = 0; i < members.Count; i++)
                if (!auth.Contains(members[i])) auth.Add(members[i]);
            wheel.SendNetworkUpdate();
        }

        private static void SetLocked(BaseEntity entity, bool locked)
        {
            if (entity == null) return;
            using (var flags = entity.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))
                flags.Set(BaseEntity.Flags.Locked, locked);
            entity.SendNetworkUpdateImmediate();
        }

        private ulong ResolveSkin(BasePlayer player, NightshadeLockerRules.PlayerPref pref)
        {
            if (!pref.FlightSkin) return 0;
            ulong skin = flightSkin;
            if (config.FlightSkinId > 0) skin = (ulong)config.FlightSkinId;
            if (skin == 0) skin = DiscoverFlightSkin();
            if (skin == 0) return 0;
            if (config.SkipDlcCheck || HasPerm(player, PermDlc)) return skin;
            if (OwnsSkin(player, skin)) return skin;
            return 0;
        }

        private ulong DiscoverFlightSkin()
        {
            if (flightSkin != 0) return flightSkin;
            try
            {
                foreach (var skin in ItemSkinDirectory.Instance.skins)
                {
                    if (skin.name != null && skin.name.IndexOf("Flight Control", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        flightSkin = (ulong)skin.id;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                PrintWarning("Flight Control skin lookup failed. " + ex.Message);
            }
            return flightSkin;
        }

        private bool OwnsSkin(BasePlayer player, ulong skin)
        {
            try
            {
                if (player.blueprints == null || player.blueprints.steamInventory == null) return false;
                int id = skin > int.MaxValue ? 0 : (int)skin;
                return id != 0 && player.blueprints.steamInventory.HasItem(id);
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Closing

        private void OnDoorOpened(Door door, BasePlayer player)
        {
            if (!worldReady || conflict || door == null) return;
            if (player != null) TryAutoCloser(player, door, false);
            ArmCloser(door);
        }

        private object OnDoorAutoClose(BasePlayer player, Door door)
        {
            _ = player;
            if (conflict || door == null) return null;
            if (door.GetSlot(BaseEntity.Slot.UpperModifier) is DoorCloser) return false;
            return null;
        }

        private void ArmCloser(Door door)
        {
            if (door == null) return;
            if (!(door.GetSlot(BaseEntity.Slot.UpperModifier) is DoorCloser)) return;
            openedAt[IdOf(door)] = Time.realtimeSinceStartup;
        }

        private void OnDoorClosed(Door door, BasePlayer player)
        {
            if (door != null) openedAt.Remove(IdOf(door));
            _ = player;
        }

        private void TryAutoCloser(BasePlayer player, Door door, bool justBuilt)
        {
            if (player == null || door == null) return;
            if (!HasPerm(player, PermClose) && !IsStaff(player)) return;
            if (!justBuilt && door.OwnerID != player.userID && !SharedWithOwner(player, door.OwnerID) && !IsStaff(player)) return;
            var pref = Pref(player);
            if (!pref.AutoClose) return;
            string close = NightshadeLockerRules.CloseCategory(door.ShortPrefabName);
            if (close == null) return;
            if (!CategoryOn(config.CloseCategories, close) || !pref.CloseOn(close)) return;
            if (NightshadeLockerRules.SkinBlocked(door.skinID, config.CloserSkinBlacklist)) return;
            if (config.RequirePrivilege && !HasPrivilege(player, door)) return;
            var result = EnsureCloser(player, door, pref.CloseDelay, true);
            if (justBuilt && !string.IsNullOrEmpty(result)) Reply(player, result);
            ArmCloser(door);
        }

        private void PlaceCloserLook(BasePlayer player)
        {
            var door = Look(player) as Door;
            if (door == null) { Reply(player, "Look at a door."); return; }
            string close = NightshadeLockerRules.CloseCategory(door.ShortPrefabName);
            if (close == null || !CategoryOn(config.CloseCategories, close))
            {
                Reply(player, "Automatic closing is not enabled for that.");
                return;
            }
            if (!HasPerm(player, PermClose) && !IsStaff(player))
            {
                Reply(player, "You need the nightshadelocker.autoclose permission.");
                return;
            }
            if (config.RequirePrivilege && !HasPrivilege(player, door))
            {
                Reply(player, "You need building privilege to do that.");
                return;
            }
            var pref = Pref(player);
            var result = EnsureCloser(player, door, pref.CloseDelay, true);
            if (result != null) Reply(player, result);
        }

        private string EnsureCloser(BasePlayer player, Door door, int delay, bool tell)
        {
            delay = NightshadeLockerRules.ClampDelay(delay, config.MinDelay, config.MaxDelay);
            var existing = door.GetSlot(BaseEntity.Slot.UpperModifier) as DoorCloser;
            if (existing != null)
            {
                existing.delay = delay;
                RememberCloser(existing, door, player.userID, delay, ExistingProvenance(existing));
                openedAt[IdOf(door)] = Time.realtimeSinceStartup;
                _ = tell;
                return "Closing delay updated.";
            }
            bool free = HasPerm(player, PermCloseFree);
            bool has = HasItem(player, "door.closer");
            if (!free && !has) return "You need a door closer in your inventory.";
            bool consumed = false;
            if (!free)
            {
                if (!TakeItem(player, "door.closer")) return "The door closer could not be taken from your inventory.";
                consumed = true;
            }
            else if (has)
            {
                // A closer in the inventory is used first so a free permission does not skip a paid item.
                if (TakeItem(player, "door.closer"))
                {
                    consumed = true;
                    free = false;
                }
            }
            var closer = GameManager.server.CreateEntity(CloserPrefab, door.transform.position, door.transform.rotation) as DoorCloser;
            if (closer == null)
            {
                if (consumed) ReturnOrOwe(player, "door.closer", 1);
                return "The door closer could not be placed.";
            }
            closer.delay = delay;
            closer.OwnerID = player.userID;
            closer.Spawn();
            if (!AttachToSlot(door, closer, BaseEntity.Slot.UpperModifier, "uppermodifier"))
            {
                if (!closer.IsDestroyed) closer.Kill();
                if (consumed) ReturnOrOwe(player, "door.closer", 1);
                return "The door closer could not be placed.";
            }
            RememberCloser(closer, door, player.userID, delay, free ? NightshadeLockerRules.ProvenanceFree : NightshadeLockerRules.ProvenancePaid);
            openedAt[IdOf(door)] = Time.realtimeSinceStartup;
            return free ? "A door closer was placed without using one from your inventory." : "Door closer placed.";
        }

        private void RememberCloser(DoorCloser closer, Door door, ulong owner, int delay, string provenance)
        {
            var id = IdOf(closer);
            CloserRecord previous;
            if (store.Closers.TryGetValue(id.ToString(), out previous) && previous != null && !string.IsNullOrEmpty(previous.Provenance))
                provenance = previous.Provenance;
            store.Closers[id.ToString()] = new CloserRecord
            {
                CloserId = id,
                DoorId = IdOf(door),
                OwnerId = owner,
                Provenance = provenance,
                Delay = delay
            };
            SaveStore();
        }

        private string ExistingProvenance(DoorCloser closer)
        {
            CloserRecord record;
            if (closer != null && store.Closers.TryGetValue(IdOf(closer).ToString(), out record) && record != null)
                return record.Provenance;
            return NightshadeLockerRules.ProvenancePaid;
        }

        private void RemoveCloserLook(BasePlayer player)
        {
            var door = Look(player) as Door;
            var closer = door != null ? door.GetSlot(BaseEntity.Slot.UpperModifier) as DoorCloser : Look(player) as DoorCloser;
            if (closer == null) { Reply(player, "Look at a door with a closer."); return; }
            if (!CanManage(player, closer.GetParentEntity(), null) && closer.OwnerID != player.userID && !IsStaff(player))
            {
                Reply(player, "You cannot remove that closer.");
                return;
            }
            string prov = ExistingProvenance(closer);
            if (!NightshadeLockerRules.IsFree(prov))
            {
                if (!ReturnOrOwe(player, "door.closer", 1))
                {
                    Reply(player, "There is no room for the door closer, and it could not be dropped. It was left in place.");
                    return;
                }
            }
            store.Closers.Remove(IdOf(closer).ToString());
            SaveStore();
            closer.Kill();
            Reply(player, NightshadeLockerRules.IsFree(prov) ? "Closer removed. No item was returned." : "Closer removed.");
        }

        private void CloseDueDoors()
        {
            if (openedAt.Count == 0) return;
            var due = new List<ulong>();
            foreach (var pair in openedAt)
            {
                var entity = Find(pair.Key) as Door;
                if (entity == null || entity.IsDestroyed || !entity.HasFlag(BaseEntity.Flags.Open) || !(entity.GetSlot(BaseEntity.Slot.UpperModifier) is DoorCloser))
                {
                    due.Add(pair.Key);
                    continue;
                }
                int delay = DelayFor(entity);
                if (Time.realtimeSinceStartup - pair.Value < delay) continue;
                using (var flags = entity.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))
                    flags.Set(BaseEntity.Flags.Open, false);
                entity.SendNetworkUpdateImmediate();
                due.Add(pair.Key);
            }
            for (int i = 0; i < due.Count; i++) openedAt.Remove(due[i]);
        }

        private int DelayFor(Door door)
        {
            var closer = door.GetSlot(BaseEntity.Slot.UpperModifier) as DoorCloser;
            int delay = closer != null ? Mathf.RoundToInt(closer.delay) : config.DefaultDelay;
            CloserRecord record;
            if (closer != null && store.Closers.TryGetValue(IdOf(closer).ToString(), out record) && record != null)
                delay = record.Delay;
            return NightshadeLockerRules.ClampDelay(delay, config.MinDelay, config.MaxDelay);
        }

        private void SetLookDelay(BasePlayer player, string text)
        {
            int seconds;
            if (!int.TryParse(text, out seconds))
            {
                Reply(player, "Use a number of seconds.");
                return;
            }
            var door = Look(player) as Door;
            if (door == null) { Reply(player, "Look at a door."); return; }
            if (!CanManage(player, door, door.GetSlot(BaseEntity.Slot.Lock) as BaseLock))
            {
                Reply(player, "You cannot change that door.");
                return;
            }
            seconds = NightshadeLockerRules.ClampDelay(seconds, config.MinDelay, config.MaxDelay);
            var pref = Pref(player);
            pref.CloseDelay = seconds;
            var closer = door.GetSlot(BaseEntity.Slot.UpperModifier) as DoorCloser;
            if (closer != null)
            {
                closer.delay = seconds;
                RememberCloser(closer, door, closer.OwnerID == 0 ? player.userID : closer.OwnerID, seconds, ExistingProvenance(closer));
            }
            else SaveStore();
            Reply(player, "Closing delay set to " + seconds + " seconds.");
        }

        private void OnEntityDismounted(BaseMountable mount, BasePlayer player)
        {
            if (!worldReady || conflict || mount == null || player == null) return;
            var pref = Pref(player);
            if (!pref.LockOnDismount || !pref.Auto) return;
            var host = Host(mount.GetParentEntity() ?? mount);
            var car = host as ModularCar ?? mount.GetComponentInParent<ModularCar>();
            if (car != null && car.CarLock != null && car.CarLock.HasALock)
            {
                if (!CanManage(player, car, null) && car.OwnerID != player.userID) return;
                using (var flags = car.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))
                    flags.Set(ModularCarCodeLock.FLAG_CENTRAL_LOCKING, true);
                Reply(player, "The lock was closed.");
                return;
            }
            var lck = host.GetSlot(BaseEntity.Slot.Lock) as BaseLock;
            if (lck == null) return;
            if (!CanManage(player, host, lck)) return;
            SetLocked(lck, true);
            Reply(player, "The lock was closed.");
        }

        #endregion

        #region Access and pickup

        private object CanPickupLock(BasePlayer player, BaseLock lck)
        {
            if (conflict || lck == null) return null;
            bool locked = lck.HasFlag(BaseEntity.Flags.Locked);
            string prov = ProvenanceOf(lck);
            bool auth = CanManage(player, lck.GetParentEntity(), lck);
            var decision = NightshadeLockerRules.DecidePickup(locked, auth, prov, HasPerm(player, PermFree));
            if (decision == NightshadeLockerRules.Pickup.Block) return false;
            if (decision == NightshadeLockerRules.Pickup.RemoveWithoutItem)
            {
                lck.Kill();
                return true;
            }
            return null;
        }

        private object CanPickupEntity(BasePlayer player, BaseEntity entity)
        {
            if (conflict || entity == null) return null;
            var closer = entity as DoorCloser;
            if (closer != null && NightshadeLockerRules.IsFree(ExistingProvenance(closer)))
            {
                if (!CanManage(player, closer.GetParentEntity(), null) && closer.OwnerID != player.userID) return false;
                closer.Kill();
                return true;
            }
            var lck = entity.GetSlot(BaseEntity.Slot.Lock) as BaseLock;
            if (lck != null && lck.HasFlag(BaseEntity.Flags.Locked)) return false;
            return null;
        }

        private object CanLootEntity(BasePlayer player, StorageContainer container) => Deny(player, container);
        private object CanLootEntity(BasePlayer player, BaseOven oven) => Deny(player, oven);
        private object CanLootEntity(BasePlayer player, VendingMachine machine) => Deny(player, machine);
        private object CanLootEntity(BasePlayer player, Locker locker) => Deny(player, locker);
        private object CanLootEntity(BasePlayer player, DropBox box) => Deny(player, box);
        private object CanLootEntity(BasePlayer player, Mailbox box) => Deny(player, box);

        private object CanMountEntity(BasePlayer player, BaseMountable mount)
        {
            if (mount == null) return null;
            return Deny(player, Host(mount));
        }

        private object CanUseLockedEntity(BasePlayer player, BaseLock lck)
        {
            if (conflict || lck == null || player == null || !lck.HasFlag(BaseEntity.Flags.Locked)) return null;
            if (Bypass(player) || Vanished(player)) return true;
            if (lck.GetPlayerLockPermission(player)) return true;
            if (lck.OwnerID == player.userID) return true;
            if (lck is KeyLock && SharedWithOwner(player, lck.OwnerID)) return true;
            return null;
        }

        private object OnOvenToggle(BaseOven oven, BasePlayer player) => Deny(player, oven);
        private object OnSwitchToggle(BaseEntity entity, BasePlayer player) => Deny(player, entity);
        private object OnCupboardAuthorize(BuildingPrivlidge priv, BasePlayer player) => Deny(player, priv);
        private object OnTurretAuthorize(AutoTurret turret, BasePlayer player) => Deny(player, turret);
        private object OnTurretModeToggle(AutoTurret turret, BasePlayer player) => Deny(player, turret);
        private object CanUpdateSign(BasePlayer player, Signage sign) => Deny(player, sign);
        private object CanLootEntity(BasePlayer player, PlanterBox planter) => Deny(player, planter);
        private object CanLootEntity(BasePlayer player, Composter composter) => Deny(player, composter);
        private object CanLootEntity(BasePlayer player, MixingTable table) => Deny(player, table);
        private object CanLootEntity(BasePlayer player, ChickenCoop coop) => Deny(player, coop);
        private object CanLootEntity(BasePlayer player, Beehive hive) => Deny(player, hive);
        private object CanLootEntity(BasePlayer player, ComputerStation station) => Deny(player, station);
        private object CanLootEntity(BasePlayer player, RepairBench bench) => Deny(player, bench);
        private object CanLootEntity(BasePlayer player, ResearchTable table) => Deny(player, table);
        private object CanLootEntity(BasePlayer player, WeaponRack rack) => Deny(player, rack);
        private object CanLootEntity(BasePlayer player, HitchTrough trough) => Deny(player, trough);
        private object CanLootEntity(BasePlayer player, Workbench bench) => Deny(player, bench);
        private object CanLootEntity(BasePlayer player, Recycler recycler) => Deny(player, recycler);
        private object CanLootEntity(BasePlayer player, IndustrialCrafter crafter) => Deny(player, crafter);
        private object CanLootEntity(BasePlayer player, LiquidContainer container) => Deny(player, container);
        private object CanAdministerVending(BasePlayer player, VendingMachine machine) => Deny(player, machine);

        private object OnWireConnect(BasePlayer player, IOEntity entity, int inputSlot, IOEntity other, int outputSlot)
        {
            _ = inputSlot;
            _ = outputSlot;
            if (!config.BlockWires) return null;
            return Deny(player, entity) ?? Deny(player, other);
        }

        private object OnRfFrequencyChanged(IRFObject rf, int frequency, BasePlayer player)
        {
            _ = frequency;
            if (!config.BlockRf) return null;
            return Deny(player, rf as BaseEntity);
        }

        private object OnGrowableGather(GrowableEntity plant, BasePlayer player)
        {
            if (!config.BlockFarming || plant == null) return null;
            var parent = plant.GetParentEntity();
            return Deny(player, parent) ?? Deny(player, plant);
        }

        private object CanTakeCutting(BasePlayer player, GrowableEntity plant)
        {
            if (!config.BlockFarming || plant == null) return null;
            return Deny(player, plant.GetParentEntity()) ?? Deny(player, plant);
        }

        private object OnHorseLead(RidableHorse horse, BasePlayer player)
        {
            return PushOrDeny(horse, player);
        }

        private object OnVehiclePush(BaseVehicle vehicle, BasePlayer player)
        {
            return PushOrDeny(vehicle, player);
        }

        private object PushOrDeny(BaseEntity entity, BasePlayer player)
        {
            if (conflict || entity == null || player == null) return null;
            bool locked = Deny(player, entity) != null;
            if (NightshadeLockerRules.PushAllowed(config.AllowVehiclePush, config.AllowVehiclePushInPrivilege, HasPrivilege(player, entity), locked))
                return null;
            return false;
        }

        private object OnButtonPress(PressButton button, BasePlayer player) => Deny(player, button);
        private object OnTurretIdentifierSet(AutoTurret turret, BasePlayer player)
        {
            if (!config.BlockTurretControls) return null;
            return Deny(player, turret);
        }
        private object OnTurretRotate(AutoTurret turret, BasePlayer player)
        {
            if (!config.BlockTurretControls) return null;
            return Deny(player, turret);
        }
        private object OnTurretToggle(AutoTurret turret, BasePlayer player)
        {
            if (!config.BlockTurretControls) return null;
            return Deny(player, turret);
        }
        private object OnSamSiteModeToggle(SamSite site, BasePlayer player) => Deny(player, site);
        private object OnEngineStart(BaseVehicle vehicle, BasePlayer player) => Deny(player, vehicle);
        private object OnLiftUse(ModularCarGarage lift, BasePlayer player) => Deny(player, lift);
        private object OnElevatorButtonPress(Elevator elevator, BasePlayer player) => Deny(player, elevator);
        private object OnQuarryToggled(MiningQuarry quarry, BasePlayer player) => Deny(player, quarry);
        private object OnQuarryGather(MiningQuarry quarry, Item item, BasePlayer player)
        {
            _ = item;
            return Deny(player, quarry);
        }
        private object OnBoomboxStationUpdate(BaseEntity box, BasePlayer player)
        {
            if (!config.BlockInstruments) return null;
            return Deny(player, box);
        }
        private object OnRackedWeaponMount(Item item, BasePlayer player, WeaponRack rack)
        {
            _ = item;
            return Deny(player, rack);
        }
        private object OnConveyorFiltersChange(IndustrialConveyor conveyor, BasePlayer player) => Deny(player, conveyor);
        private object OnCCTVDirectionChange(CCTV_RC camera, BasePlayer player) => Deny(player, camera);
        private object OnPhoneDial(Telephone phone, string number, BasePlayer player)
        {
            _ = number;
            return Deny(player, phone);
        }

        private object Deny(BasePlayer player, BaseEntity entity)
        {
            if (conflict || player == null || entity == null) return null;
            if (Bypass(player) || Vanished(player)) return null;
            var host = Host(entity);
            var car = host as ModularCar;
            if (car != null && car.CarLock != null && car.CarLock.HasALock && car.HasFlag(ModularCarCodeLock.FLAG_CENTRAL_LOCKING))
            {
                if (car.OwnerID == player.userID) return null;
                if (car.CarLock.WhitelistPlayers != null && car.CarLock.WhitelistPlayers.Contains(player.userID)) return null;
                return false;
            }
            var wheel = host as SteeringWheel;
            if (wheel != null && wheel.BoatLock != null && wheel.BoatLock.HasALock && wheel.HasFlag(BaseEntity.Flags.Locked))
            {
                if (BoatAllowed(player, wheel)) return null;
                return false;
            }
            var lck = host as BaseLock ?? host.GetSlot(BaseEntity.Slot.Lock) as BaseLock;
            if (lck == null || !lck.HasFlag(BaseEntity.Flags.Locked)) return null;
            if (lck.GetPlayerLockPermission(player)) return null;
            if (lck.OwnerID == player.userID || host.OwnerID == player.userID) return null;
            if (lck is KeyLock && SharedWithOwner(player, lck.OwnerID != 0 ? lck.OwnerID : host.OwnerID)) return null;
            var turret = host as AutoTurret;
            if (turret != null && turret.IsAuthed(player)) return null;
            return false;
        }

        private bool BoatAllowed(BasePlayer player, SteeringWheel wheel)
        {
            if (player == null || wheel == null) return false;
            if (wheel.OwnerID == player.userID) return true;
            var auth = wheel.Privilege != null ? wheel.Privilege.authorizedPlayers : null;
            if (auth != null && auth.Contains(player.userID)) return true;
            return SharedWithOwner(player, wheel.OwnerID);
        }

        private bool Bypass(BasePlayer player)
        {
            if (HasPerm(player, PermForce)) return true;
            return config.AdminBypass && IsStaff(player);
        }

        private bool Vanished(BasePlayer player)
        {
            if (player == null) return false;
            if (player.limitNetworking) return true;
            var vanish = plugins.Find("Vanish");
            if (vanish == null) return false;
            try
            {
                var result = vanish.Call("IsInvisible", player);
                return result is bool && (bool)result;
            }
            catch
            {
                return false;
            }
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity == null || entity.net == null) return;
            string id = entity.net.ID.Value.ToString();
            bool changed = store.Locks.Remove(id) || store.Closers.Remove(id);
            openedAt.Remove(entity.net.ID.Value);
            if (changed) SaveStore();
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            DeliverOwed(player);
            if (player != null && !conflict) RefreshShare(player.userID);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, UiRoot);
            uiPage.Remove(player.userID);
            _ = reason;
        }

        private void OnTeamAcceptInvite(RelationshipManager.PlayerTeam team, BasePlayer player)
        {
            ulong extra = player != null ? player.userID : 0;
            NextTick(() => RefreshMembers(team, extra));
        }

        private object OnTeamLeave(RelationshipManager.PlayerTeam team, BasePlayer player)
        {
            ulong extra = player != null ? player.userID : 0;
            NextTick(() => RefreshMembers(team, extra));
            return null;
        }

        private object OnTeamKick(RelationshipManager.PlayerTeam team, BasePlayer leader, ulong target)
        {
            _ = leader;
            NextTick(() => RefreshMembers(team, target));
            return null;
        }

        private void OnTeamDisbanded(RelationshipManager.PlayerTeam team)
        {
            NextTick(() => RefreshMembers(team, 0));
        }

        private void OnClanUpdate(string clanName)
        {
            _ = clanName;
            NextTick(RefreshOnlineShares);
        }

        private void OnClanDestroy(string clanName)
        {
            _ = clanName;
            NextTick(RefreshOnlineShares);
        }

        private void OnClanMemberGone(string userID, string tag)
        {
            _ = tag;
            ulong id;
            ulong.TryParse(userID, out id);
            NextTick(() => RefreshShare(id));
        }

        private void OnClanMemberGone(ulong playerId, List<ulong> members)
        {
            NextTick(() => RefreshIds(members, playerId));
        }

        private void OnClanMemberGone(string playerId, List<string> members)
        {
            ulong id;
            ulong.TryParse(playerId, out id);
            NextTick(() => RefreshIds(ParseIds(members), id));
        }

        private void OnClanMemberGone(string tag, ulong leaving, List<ulong> members)
        {
            _ = tag;
            NextTick(() => RefreshIds(members, leaving));
        }

        private void OnClanMemberGone(string tag, string leaving, List<string> members)
        {
            _ = tag;
            ulong id;
            ulong.TryParse(leaving, out id);
            NextTick(() => RefreshIds(ParseIds(members), id));
        }

        private void RefreshMembers(RelationshipManager.PlayerTeam team, ulong extra)
        {
            var ids = new List<ulong>();
            if (team != null && team.members != null)
            {
                for (int i = 0; i < team.members.Count; i++) ids.Add(team.members[i]);
            }
            RefreshIds(ids, extra);
        }

        private void RefreshIds(IList<ulong> ids, ulong extra)
        {
            if (ids != null)
            {
                for (int i = 0; i < ids.Count; i++) RefreshShare(ids[i]);
            }
            if (extra != 0) RefreshShare(extra);
        }

        private static List<ulong> ParseIds(IList<string> raw)
        {
            var ids = new List<ulong>();
            if (raw == null) return ids;
            for (int i = 0; i < raw.Count; i++)
            {
                ulong id;
                if (ulong.TryParse(raw[i], out id)) ids.Add(id);
            }
            return ids;
        }

        private void RefreshOnlineShares()
        {
            foreach (var player in BasePlayer.activePlayerList) RefreshShare(player.userID);
        }

        private void RefreshShare(ulong ownerId)
        {
            if (conflict || ownerId == 0 || store == null) return;
            var owner = FindPlayer(ownerId);
            NightshadeLockerRules.PlayerPref pref;
            if (!store.Players.TryGetValue(ownerId.ToString(), out pref) || pref == null) return;
            bool share = WantsShare(pref);
            var members = share ? MemberIds(ownerId, owner, pref) : new List<ulong>();
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var code = entity as CodeLock;
                if (code != null && OwnsRecord(code, ownerId))
                {
                    if (code.whitelistPlayers == null) code.whitelistPlayers = new List<ulong>();
                    code.whitelistPlayers.Clear();
                    code.whitelistPlayers.Add(ownerId);
                    for (int i = 0; i < members.Count; i++)
                        if (!code.whitelistPlayers.Contains(members[i])) code.whitelistPlayers.Add(members[i]);
                    code.SendNetworkUpdate();
                    TouchShare(code, members);
                    continue;
                }
                var car = entity as ModularCar;
                if (car != null && car.CarLock != null && car.CarLock.HasALock && OwnsRecord(car, ownerId))
                {
                    var current = car.CarLock.WhitelistPlayers != null ? car.CarLock.WhitelistPlayers.ToList() : new List<ulong>();
                    for (int i = 0; i < current.Count; i++)
                        if (current[i] != ownerId && !members.Contains(current[i])) car.CarLock.TryRemovePlayer(current[i]);
                    car.CarLock.TryAddPlayer(ownerId);
                    for (int i = 0; i < members.Count; i++) car.CarLock.TryAddPlayer(members[i]);
                    TouchShare(car, members);
                    continue;
                }
                var wheel = entity as SteeringWheel;
                if (wheel != null && wheel.BoatLock != null && wheel.BoatLock.HasALock && OwnsRecord(wheel, ownerId) && wheel.Privilege != null && wheel.Privilege.authorizedPlayers != null)
                {
                    wheel.Privilege.authorizedPlayers.Clear();
                    wheel.Privilege.authorizedPlayers.Add(ownerId);
                    for (int i = 0; i < members.Count; i++)
                        if (!wheel.Privilege.authorizedPlayers.Contains(members[i]))
                            wheel.Privilege.authorizedPlayers.Add(members[i]);
                    wheel.SendNetworkUpdate();
                    TouchShare(wheel, members);
                }
            }
            SaveStore();
        }

        private bool OwnsRecord(BaseEntity entity, ulong ownerId)
        {
            LockRecord record;
            if (entity == null || !store.Locks.TryGetValue(IdOf(entity).ToString(), out record) || record == null) return false;
            return record.OwnerId == ownerId;
        }

        private void TouchShare(BaseEntity entity, List<ulong> members)
        {
            LockRecord record;
            string key = IdOf(entity).ToString();
            if (!store.Locks.TryGetValue(key, out record) || record == null) return;
            record.Shared = members ?? new List<ulong>();
        }

        #endregion

        #region Admin mutations

        private void SetLockFlag(BasePlayer player, BaseEntity entity, bool locked)
        {
            var host = Host(entity);
            var car = host as ModularCar;
            if (car != null && car.CarLock != null && car.CarLock.HasALock)
            {
                using (var flags = car.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))
                    flags.Set(ModularCarCodeLock.FLAG_CENTRAL_LOCKING, locked);
                Reply(player, locked ? "Locked." : "Unlocked.");
                return;
            }
            var wheel = host as SteeringWheel;
            if (wheel != null && wheel.BoatLock != null && wheel.BoatLock.HasALock)
            {
                SetLocked(wheel, locked);
                Reply(player, locked ? "Locked." : "Unlocked.");
                return;
            }
            var lck = host != null ? host.GetSlot(BaseEntity.Slot.Lock) as BaseLock : null;
            if (lck == null) lck = entity as BaseLock;
            if (lck == null) { Reply(player, "Look at a lock."); return; }
            SetLocked(lck, locked);
            Reply(player, locked ? "Locked." : "Unlocked.");
        }

        private void RemoveLook(BasePlayer player, bool noRefund)
        {
            var entity = Look(player);
            var host = Host(entity);
            if (host is ModularCar && RemoveCarLock(player, host as ModularCar, noRefund)) return;
            if (host is SteeringWheel && RemoveBoatLock(player, host as SteeringWheel, noRefund)) return;
            var lck = entity is BaseLock ? entity as BaseLock : host != null ? host.GetSlot(BaseEntity.Slot.Lock) as BaseLock : null;
            if (lck == null) { Reply(player, "Look at a lock."); return; }
            if (RemoveLock(player, lck, noRefund)) Reply(player, noRefund ? "Lock removed without returning an item." : "Lock removed.");
        }

        private bool RemoveCarLock(BasePlayer player, ModularCar car, bool noRefund)
        {
            if (car == null || car.CarLock == null || !car.CarLock.HasALock) return false;
            if (!CanManage(player, car, null) && !IsStaff(player))
            {
                Reply(player, "You cannot remove that lock.");
                return true;
            }
            if (!RefundSpecial(player, car, noRefund)) return true;
            car.CarLock.RemoveLock();
            store.Locks.Remove(IdOf(car).ToString());
            SaveStore();
            Reply(player, noRefund ? "Lock removed without returning an item." : "Lock removed.");
            return true;
        }

        private bool RemoveBoatLock(BasePlayer player, SteeringWheel wheel, bool noRefund)
        {
            if (wheel == null || wheel.BoatLock == null || !wheel.BoatLock.HasALock) return false;
            if (!CanManage(player, wheel, null) && !IsStaff(player))
            {
                Reply(player, "You cannot remove that lock.");
                return true;
            }
            if (!RefundSpecial(player, wheel, noRefund)) return true;
            wheel.BoatLock.RemoveLock();
            SetLocked(wheel, false);
            store.Locks.Remove(IdOf(wheel).ToString());
            SaveStore();
            Reply(player, noRefund ? "Lock removed without returning an item." : "Lock removed.");
            return true;
        }

        private bool RefundSpecial(BasePlayer player, BaseEntity entity, bool noRefund)
        {
            LockRecord record;
            store.Locks.TryGetValue(IdOf(entity).ToString(), out record);
            string prov = noRefund ? NightshadeLockerRules.ProvenanceFree : (record != null ? record.Provenance : NightshadeLockerRules.ProvenancePaid);
            if (NightshadeLockerRules.IsFree(prov)) return true;
            if (!ReturnOrOwe(player, "lock.code", 1))
            {
                Reply(player, "The lock item could not be returned or dropped, so the lock was left in place.");
                return false;
            }
            return true;
        }

        private bool RemoveLock(BasePlayer player, BaseLock lck, bool noRefund)
        {
            string prov = noRefund ? NightshadeLockerRules.ProvenanceFree : ProvenanceOf(lck);
            if (string.IsNullOrEmpty(prov)) prov = NightshadeLockerRules.ProvenancePaid;
            string item = "lock.code";
            LockRecord record;
            if (store.Locks.TryGetValue(IdOf(lck).ToString(), out record) && record != null && !string.IsNullOrEmpty(record.Item))
                item = record.Item;
            else if (lck is KeyLock) item = "lock.key";
            if (!NightshadeLockerRules.IsFree(prov))
            {
                var owner = FindPlayer(lck.OwnerID) ?? player;
                if (!ReturnOrOwe(owner, item, 1))
                {
                    Reply(player, "The lock item could not be returned or dropped, so the lock was left in place.");
                    return false;
                }
            }
            store.Locks.Remove(IdOf(lck).ToString());
            SaveStore();
            lck.Kill();
            return true;
        }

        private void SetLookCode(BasePlayer player, string pin, bool staff)
        {
            if (!NightshadeLockerRules.PinValid(pin)) { Reply(player, "Use 4 digits."); return; }
            var entity = Look(player);
            var code = entity != null ? entity.GetSlot(BaseEntity.Slot.Lock) as CodeLock : null;
            if (code == null) code = entity as CodeLock;
            if (code == null) { Reply(player, "Look at a code lock."); return; }
            if (!staff && !CanManage(player, code.GetParentEntity(), code))
            {
                Reply(player, "You cannot change that code.");
                return;
            }
            code.code = pin;
            code.hasCode = true;
            code.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
            var pref = Pref(player);
            if (!staff) pref.Code = pin;
            SaveStore();
            Reply(player, "Code updated.");
        }

        private void ShowCode(BasePlayer player, bool staff)
        {
            var entity = Look(player);
            var code = entity != null ? entity.GetSlot(BaseEntity.Slot.Lock) as CodeLock : null;
            if (code == null) code = entity as CodeLock;
            if (code == null) { Reply(player, "Look at a code lock."); return; }
            if (!staff && !CanManage(player, code.GetParentEntity(), code))
            {
                Reply(player, "You cannot read that code.");
                return;
            }
            if (!staff && Pref(player).Streamer)
            {
                Reply(player, "Streamer mode is on, so the code stays off this chat.");
                return;
            }
            if (!code.hasCode || string.IsNullOrEmpty(code.code))
            {
                Reply(player, "That lock has no code.");
                return;
            }
            string guest = code.hasGuestCode && !string.IsNullOrEmpty(code.guestCode) ? " Guest " + code.guestCode + "." : string.Empty;
            Reply(player, "Code " + code.code + "." + guest);
        }

        private void SetGuest(BasePlayer player, string pin, bool fromUi)
        {
            var pref = Pref(player);
            if (string.IsNullOrEmpty(pin) || pin == "clear")
            {
                pref.Guest = string.Empty;
                pref.GuestEnabled = false;
            }
            else if (!NightshadeLockerRules.PinValid(pin))
            {
                Reply(player, "Use 4 digits, or clear.");
                return;
            }
            else
            {
                pref.Guest = pin;
                pref.GuestEnabled = true;
            }
            SaveStore();
            int updated = ApplyGuest(player.userID, pref.Guest, pref.GuestEnabled);
            Reply(player, updated > 0 ? "Guest code updated on " + updated + " locks." : "Guest code updated.");
            _ = fromUi;
        }

        private void SetAllCodes(BasePlayer player, string pin)
        {
            if (!NightshadeLockerRules.PinValid(pin)) { Reply(player, "Use 4 digits."); return; }
            var pref = Pref(player);
            pref.Code = pin;
            int count = ApplyPin(player.userID, pin, false);
            SaveStore();
            Reply(player, "Updated " + count + " locks.");
        }

        private void SetAllCodesFor(BasePlayer staff, string steam, string pin)
        {
            ulong id;
            if (!ulong.TryParse(steam, out id) || !NightshadeLockerRules.PinValid(pin))
            {
                Reply(staff, "Use a steam id and 4 digits.");
                return;
            }
            int count = ApplyPin(id, pin, true);
            SaveStore();
            Reply(staff, "Updated " + count + " locks.");
        }

        private int ApplyPin(ulong owner, string pin, bool staff)
        {
            int count = 0;
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var code = entity as CodeLock;
                if (code == null) continue;
                LockRecord record;
                store.Locks.TryGetValue(IdOf(code).ToString(), out record);
                bool mine = code.OwnerID == owner || (record != null && record.OwnerId == owner);
                if (!mine) continue;
                if (!staff && code.OwnerID != owner && (record == null || record.OwnerId != owner)) continue;
                code.code = pin;
                code.hasCode = true;
                code.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
                count++;
            }
            return count;
        }

        private int ApplyGuest(ulong owner, string pin, bool enabled)
        {
            int count = 0;
            if (store == null) return 0;
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var code = entity as CodeLock;
                if (code == null) continue;
                LockRecord record;
                store.Locks.TryGetValue(IdOf(code).ToString(), out record);
                bool mine = code.OwnerID == owner || (record != null && record.OwnerId == owner);
                if (!mine) continue;
                if (enabled && NightshadeLockerRules.PinValid(pin))
                {
                    code.guestCode = pin;
                    code.hasGuestCode = true;
                }
                else
                {
                    code.guestCode = string.Empty;
                    code.hasGuestCode = false;
                    if (code.guestPlayers != null) code.guestPlayers.Clear();
                }
                code.SendNetworkUpdate();
                count++;
            }
            return count;
        }

        private void FlagAll(BasePlayer player, string steam, bool locked)
        {
            ulong id = player.userID;
            if (!string.IsNullOrEmpty(steam) && !ulong.TryParse(steam, out id))
            {
                Reply(player, "That steam id was not valid.");
                return;
            }
            int count = 0;
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var lck = entity as BaseLock;
                if (lck == null || lck.OwnerID != id) continue;
                SetLocked(lck, locked);
                count++;
            }
            Reply(player, (locked ? "Locked " : "Unlocked ") + count + " locks.");
        }

        private void RemoveAll(BasePlayer player, string[] args)
        {
            if (args.Length < 3 || !args[2].Equals("confirm", StringComparison.OrdinalIgnoreCase) && !args.Any(a => a.Equals("confirm", StringComparison.OrdinalIgnoreCase)))
            {
                Reply(player, "Use: nslock admin removeall <steamId> confirm");
                return;
            }
            ulong id;
            if (!ulong.TryParse(args[1], out id)) { Reply(player, "That steam id was not valid."); return; }
            bool noRefund = args.Any(a => a.Equals("norefund", StringComparison.OrdinalIgnoreCase));
            var found = new List<BaseLock>();
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var lck = entity as BaseLock;
                if (lck == null) continue;
                LockRecord record;
                store.Locks.TryGetValue(IdOf(lck).ToString(), out record);
                if (lck.OwnerID == id || (record != null && record.OwnerId == id)) found.Add(lck);
            }
            int removed = 0;
            for (int i = 0; i < found.Count; i++)
                if (RemoveLock(player, found[i], noRefund)) removed++;
            Reply(player, "Removed " + removed + " of " + found.Count + " locks.");
        }

        #endregion

        #region Items

        private static Item FindCarried(BasePlayer player, string shortname)
        {
            if (player == null || player.inventory == null || string.IsNullOrEmpty(shortname)) return null;
            Item item = player.inventory.FindItemByItemID(shortname);
            if (item != null && item.amount > 0) return item;
            try
            {
                Item bag = player.inventory.GetBackpackWithInventory();
                if (bag != null && bag.contents != null)
                {
                    Item packed = bag.contents.FindItemByItemName(shortname);
                    if (packed != null && packed.amount > 0) return packed;
                }
            }
            catch (Exception)
            {
                return null;
            }
            return null;
        }

        private static bool HasCodeLock(BasePlayer player)
        {
            return FindCarried(player, "lock.code") != null || FindCarried(player, PilotShort) != null;
        }

        private static bool TakeCodeLock(BasePlayer player, out bool pilot)
        {
            pilot = false;
            Item item = FindCarried(player, "lock.code");
            if (item == null)
            {
                item = FindCarried(player, PilotShort);
                pilot = item != null;
            }
            if (item == null || item.amount < 1) return false;
            item.UseItem(1);
            return true;
        }

        private static bool HasItem(BasePlayer player, string shortname)
        {
            return FindCarried(player, shortname) != null;
        }

        private static bool TakeItem(BasePlayer player, string shortname)
        {
            var item = FindCarried(player, shortname);
            if (item == null || item.amount < 1) return false;
            item.UseItem(1);
            return true;
        }

        private bool ReturnOrOwe(BasePlayer player, string shortname, int amount)
        {
            if (amount < 1) return true;
            bool accepted = false;
            bool dropped = false;
            Item item = null;
            if (player != null && player.IsConnected)
            {
                item = ItemManager.CreateByName(shortname, amount);
                if (item != null && player.inventory.GiveItem(item)) accepted = true;
                else if (item != null)
                {
                    var droppedEntity = item.Drop(player.GetDropPosition(), player.GetDropVelocity(), Quaternion.identity);
                    dropped = droppedEntity != null;
                    if (!dropped) item.Remove();
                }
            }
            var plan = NightshadeLockerRules.PlanReturn(NightshadeLockerRules.ProvenancePaid, accepted, dropped);
            if (plan == NightshadeLockerRules.Refund.Give || plan == NightshadeLockerRules.Refund.Drop) return true;
            if (player == null) return false;
            var pref = Pref(player);
            NightshadeLockerRules.AddOwed(pref, shortname, amount);
            SaveStore();
            Reply(player, "Your inventory could not take the lock item. It is owed and will be delivered when there is room.");
            return true;
        }

        private void DeliverAllOwed()
        {
            foreach (var player in BasePlayer.activePlayerList) DeliverOwed(player);
        }

        private void DeliverOwed(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            NightshadeLockerRules.PlayerPref pref;
            if (!store.Players.TryGetValue(player.UserIDString, out pref) || pref == null) return;
            bool changed = Pay(player, pref, "lock.code", ref pref.OwedCode);
            changed = Pay(player, pref, "lock.key", ref pref.OwedKey) || changed;
            changed = Pay(player, pref, "door.closer", ref pref.OwedCloser) || changed;
            if (changed) SaveStore();
        }

        private bool Pay(BasePlayer player, NightshadeLockerRules.PlayerPref pref, string shortname, ref int owed)
        {
            if (owed <= 0) return false;
            int left = owed;
            while (left > 0)
            {
                var item = ItemManager.CreateByName(shortname, 1);
                if (item == null) break;
                if (!player.inventory.GiveItem(item))
                {
                    var drop = item.Drop(player.GetDropPosition(), player.GetDropVelocity(), Quaternion.identity);
                    if (drop == null)
                    {
                        item.Remove();
                        break;
                    }
                }
                left--;
            }
            if (left == owed) return false;
            owed = left;
            _ = pref;
            return true;
        }

        #endregion

        #region Matching and authority

        private NightshadeLockerRules.Match Match(BaseEntity entity)
        {
            if (entity == null || entity is BasePlayer || entity is BaseLock) return null;
            if (entity.GetParentEntity() is BasePlayer) return null;
            if (entity is SteeringWheel)
                return new NightshadeLockerRules.Match { Category = "naval", Prefab = entity.PrefabName ?? string.Empty };
            string full = entity.PrefabName ?? string.Empty;
            string shortName = entity.ShortPrefabName ?? string.Empty;
            var rule = NightshadeLockerRules.FindRule(full, config.Vehicles, config.Extras);
            if (rule != null)
            {
                if (!rule.Enabled) return null;
                return new NightshadeLockerRules.Match { Category = rule.Category, Prefab = full };
            }
            string kind = Kind(entity);
            string category = NightshadeLockerRules.LockCategory(shortName, kind);
            if (category == null) return null;
            return new NightshadeLockerRules.Match { Category = category, Prefab = full };
        }

        private static string Kind(BaseEntity entity)
        {
            if (entity is Door) return "door";
            if (entity is BuildingPrivlidge) return "cupboard";
            if (entity is Locker) return "locker";
            if (entity is StashContainer) return "stash";
            if (entity is BoxStorage) return "box";
            if (entity is VendingMachine) return "vending";
            if (entity is BaseOven) return "furnace";
            if (entity is Composter) return "composter";
            if (entity is MixingTable) return "mixing";
            if (entity is PlanterBox) return "planter";
            if (entity is AutoTurret) return "turret";
            if (entity is SamSite) return "sam";
            if (entity is FlameTurret || entity is GunTrap) return "trap";
            if (entity is WeaponRack) return "rack";
            if (entity is NeonSign) return "neon";
            if (entity is Signage) return "sign";
            if (entity is ChickenCoop) return "farming";
            if (entity is Beehive) return "farming";
            if (entity is HitchTrough) return "farming";
            if (entity is WaterCatcher) return "farming";
            if (entity is ComputerStation || entity is RepairBench || entity is ResearchTable || entity is Workbench || entity is Recycler) return "crafting";
            if (entity is IOEntity) return "electric";
            if (entity is RFBroadcaster || entity is RFReceiver || entity is Telephone) return "electric";
            if (entity is IndustrialCrafter || entity is IndustrialConveyor) return "industrial";
            if (entity is MiningQuarry) return "quarry";
            if (entity is PressButton || entity is SmartSwitch || entity is TimerSwitch || entity is FuelGenerator) return "electric";
            if (entity is Elevator || entity is ModularCarGarage || entity is SearchLight || entity is CCTV_RC) return "electric";
            if (entity is BoomBox || entity is InstrumentTool || entity is BaseArcadeMachine) return "fun";
            if (entity is HotAirBalloon) return "balloon";
            if (entity is BaseVehicle) return "vehicle";
            if (entity is StorageContainer) return "storage";
            return "other";
        }

        private static bool CategoryOn(Dictionary<string, bool> map, string key)
        {
            if (map == null || string.IsNullOrEmpty(key)) return false;
            bool value;
            return map.TryGetValue(key, out value) && value;
        }

        private bool HasPrivilege(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null) return false;
            if (player.IsBuildingBlocked()) return false;
            var priv = entity.GetBuildingPrivilege();
            if (priv != null) return priv.IsAuthed(player);
            return true;
        }

        private bool CanManage(BasePlayer player, BaseEntity entity, BaseLock lck)
        {
            if (player == null) return false;
            if (IsStaff(player)) return true;
            if (lck != null && lck.OwnerID == player.userID) return true;
            if (entity != null && entity.OwnerID == player.userID) return true;
            var code = lck as CodeLock;
            if (code != null && code.whitelistPlayers != null && code.whitelistPlayers.Contains(player.userID)) return true;
            ulong owner = lck != null && lck.OwnerID != 0 ? lck.OwnerID : (entity != null ? entity.OwnerID : 0);
            return SharedWithOwner(player, owner);
        }

        private bool IsWith(BasePlayer player, ulong other)
        {
            if (other == 0 || player == null || other == player.userID) return false;
            var ids = TeamIds(player);
            for (int i = 0; i < ids.Count; i++)
                if (ids[i] == other) return true;
            return false;
        }

        private List<ulong> TeamIds(BasePlayer player)
        {
            var list = new List<ulong>();
            if (player.currentTeam != 0 && RelationshipManager.ServerInstance != null)
            {
                var team = RelationshipManager.ServerInstance.FindTeam(player.currentTeam);
                if (team != null && team.members != null)
                {
                    for (int i = 0; i < team.members.Count; i++) list.Add(team.members[i]);
                }
            }
            var clans = plugins.Find("Clans");
            if (clans != null)
            {
                object raw = null;
                try { raw = clans.Call("GetClanMembers", player.userID); }
                catch { raw = null; }
                if (raw == null)
                {
                    try { raw = clans.Call("GetClanMembers", player.UserIDString); }
                    catch { raw = null; }
                }
                AddIds(list, raw);
            }
            return list;
        }

        private static void AddIds(List<ulong> list, object raw)
        {
            if (list == null || raw == null || raw is string) return;
            var enumerable = raw as IEnumerable;
            if (enumerable == null) return;
            foreach (var item in enumerable)
            {
                if (item == null) continue;
                ulong id;
                if (ulong.TryParse(item.ToString(), out id) && id != 0 && !list.Contains(id))
                    list.Add(id);
            }
        }

        private bool SharedWithOwner(BasePlayer player, ulong ownerId)
        {
            if (player == null || ownerId == 0 || ownerId == player.userID) return false;
            NightshadeLockerRules.PlayerPref pref;
            if (!store.Players.TryGetValue(ownerId.ToString(), out pref) || pref == null || !WantsShare(pref)) return false;
            return IsWith(player, ownerId);
        }

        private BaseEntity Host(BaseEntity entity)
        {
            if (entity == null) return null;
            var car = entity as ModularCar ?? entity.GetComponentInParent<ModularCar>();
            if (car != null) return car;
            var wheel = entity as SteeringWheel ?? entity.GetComponentInParent<SteeringWheel>();
            if (wheel == null && !entity.HasSlot(BaseEntity.Slot.Lock))
                wheel = entity.GetComponentInChildren<SteeringWheel>();
            if (wheel != null) return wheel;
            return entity;
        }

        private static bool HasLock(BaseEntity entity)
        {
            if (entity == null) return false;
            var car = entity as ModularCar;
            if (car != null && car.CarLock != null && car.CarLock.HasALock) return true;
            var wheel = entity as SteeringWheel;
            if (wheel != null && wheel.BoatLock != null && wheel.BoatLock.HasALock) return true;
            return entity.GetSlot(BaseEntity.Slot.Lock) != null;
        }

        private string ProvenanceOf(BaseLock lck)
        {
            if (lck == null || lck.net == null) return null;
            LockRecord record;
            if (store.Locks.TryGetValue(lck.net.ID.Value.ToString(), out record) && record != null)
                return record.Provenance;
            return null;
        }

        private static ulong IdOf(BaseEntity entity)
        {
            if (entity == null || entity.net == null) return 0;
            return entity.net.ID.Value;
        }

        private static BaseEntity Find(ulong id)
        {
            if (id == 0) return null;
            return BaseNetworkable.serverEntities.Find(new NetworkableId(id)) as BaseEntity;
        }

        private BaseEntity Look(BasePlayer player)
        {
            if (player == null || player.eyes == null) return null;
            var hits = Physics.RaycastAll(player.eyes.HeadRay(), config.UseDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            BaseEntity found = null;
            for (int i = 0; i < hits.Length; i++)
            {
                var ent = hits[i].GetEntity();
                if (ent == null || ent == player || ent is BasePlayer) continue;
                if (hits[i].distance >= best) continue;
                best = hits[i].distance;
                found = ent;
            }
            return found == null ? null : Host(found);
        }

        private static BasePlayer FindPlayer(ulong id)
        {
            if (id == 0) return null;
            return BasePlayer.FindByID(id) ?? BasePlayer.FindSleeping(id);
        }

        #endregion

        #region Import

        private string ImportLegacy(BasePlayer player)
        {
            string path = config.LegacyDataFile;
            if (!Path.IsPathRooted(path)) path = Path.Combine(Interface.Oxide.DataDirectory, path);
            if (!File.Exists(path)) return "Legacy file was not found. Nothing was changed.";
            string json;
            try { json = File.ReadAllText(path); }
            catch (Exception ex) { return "Legacy file could not be read. " + ex.Message; }
            var notes = new List<string>();
            var incoming = NightshadeLockerRules.ReadLegacy(json, notes);
            try { BackupStore(); }
            catch (Exception ex) { return "Import stopped because the backup failed. " + ex.Message; }
            var report = NightshadeLockerRules.Merge(store.Players, incoming);
            if (report.Created > 0 || report.Updated > 0) SaveStore();
            var text = report.Summary;
            for (int i = 0; i < notes.Count; i++) text += " " + notes[i];
            Puts("NightshadeLocker import by " + player.displayName + ": " + report.Summary);
            return text;
        }

        private void BackupStore()
        {
            try
            {
                var root = Path.Combine(Interface.Oxide.DataDirectory, "Nightshade", "NightshadeLocker");
                var source = Path.Combine(root, "State.json");
                if (!File.Exists(source)) return;
                var backupDir = Path.Combine(root, "Backups");
                Directory.CreateDirectory(backupDir);
                var name = NightshadeLockerRules.BackupName(DateTime.UtcNow);
                File.Copy(source, Path.Combine(backupDir, name), false);
            }
            catch (Exception ex)
            {
                PrintWarning("Backup before import failed. Import was not started. " + ex.Message);
                throw;
            }
        }

        #endregion

        #region Theme and UI

        private Palette LoadPalette()
        {
            var fallback = BuiltinPalette();
            try
            {
                string path = Path.Combine(Interface.Oxide.ConfigDirectory, string.IsNullOrEmpty(config.ThemeFile) ? "NightshadeShop.json" : config.ThemeFile);
                if (!File.Exists(path)) return fallback;
                var root = JObject.Parse(File.ReadAllText(path));
                var rows = new List<NightshadeLockerRules.ThemeRow>();
                var themes = root["Themes"] as JArray;
                if (themes != null)
                {
                    for (int i = 0; i < themes.Count; i++)
                    {
                        var item = themes[i] as JObject;
                        if (item == null) continue;
                        var colors = item["Colors (#RRGGBB or #RRGGBBAA)"] as JObject;
                        rows.Add(new NightshadeLockerRules.ThemeRow
                        {
                            Name = item.Value<string>("Name"),
                            Tagline = item.Value<string>("Tagline"),
                            From = item.Value<string>("Active From (MM-DD)"),
                            To = item.Value<string>("Active To (MM-DD)"),
                            Priority = item.Value<int?>("Priority (higher wins when dates overlap)") ?? 0,
                            Background = ColorOf(colors, "Background", fallback.Background),
                            Header = ColorOf(colors, "Header", fallback.Header),
                            Panel = ColorOf(colors, "Panel", fallback.Panel),
                            Card = ColorOf(colors, "Card", fallback.Card),
                            Accent = ColorOf(colors, "Accent", fallback.Accent),
                            AccentText = ColorOf(colors, "AccentText", fallback.AccentText),
                            Button = ColorOf(colors, "Button", fallback.Button),
                            ButtonText = ColorOf(colors, "ButtonText", fallback.ButtonText),
                            Text = ColorOf(colors, "Text", fallback.Text),
                            SubText = ColorOf(colors, "SubText", fallback.SubText)
                        });
                    }
                }
                string mode = config.ThemeMode;
                if (string.Equals(mode, "Shop", StringComparison.OrdinalIgnoreCase))
                    mode = root.Value<string>("Theme Mode (Auto or a theme name)") ?? "Auto";
                int today = DateTime.Now.Month * 100 + DateTime.Now.Day;
                var chosen = NightshadeLockerRules.ResolveTheme(mode, rows, today);
                if (chosen == null) return fallback;
                return new Palette
                {
                    Name = chosen.Name,
                    Tagline = string.IsNullOrEmpty(chosen.Tagline) ? fallback.Tagline : chosen.Tagline,
                    Background = chosen.Background,
                    Header = chosen.Header,
                    Panel = chosen.Panel,
                    Card = chosen.Card,
                    Accent = chosen.Accent,
                    AccentText = chosen.AccentText,
                    Button = chosen.Button,
                    ButtonText = chosen.ButtonText,
                    Text = chosen.Text,
                    SubText = chosen.SubText,
                    Overlay = fallback.Overlay,
                    Danger = fallback.Danger,
                    DangerText = fallback.DangerText,
                    Source = "NightshadeShop.json"
                };
            }
            catch (Exception ex)
            {
                PrintWarning("Theme file could not be used. Falling back to Nightshade colors. " + ex.Message);
                return fallback;
            }
        }

        private static string ColorOf(JObject colors, string key, string fallback)
        {
            if (colors == null) return fallback;
            var value = colors.Value<string>(key);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        private static Palette BuiltinPalette()
        {
            return new Palette();
        }

        private string Logo()
        {
            try
            {
                string path = Path.Combine(Interface.Oxide.ConfigDirectory, string.IsNullOrEmpty(config.LogoFile) ? "NightshadeInfo.json" : config.LogoFile);
                if (File.Exists(path))
                {
                    var root = JObject.Parse(File.ReadAllText(path));
                    var url = root.Value<string>("Logo URL");
                    if (!string.IsNullOrEmpty(url)) return url;
                }
            }
            catch
            {
                // The fallback logo still draws.
            }
            return string.IsNullOrEmpty(config.LogoUrl) ? DefaultLogo : config.LogoUrl;
        }

        private class Row
        {
            public string Kind = "toggle";
            public string Text;
            public string Command;
            public bool On;
            public bool Live = true;
            public string Value;
        }

        private void OpenUi(BasePlayer player, string page)
        {
            if (!CanUse(player)) return;
            if (page != "places" && page != "closing" && page != "code") page = "locks";
            uiPage[player.userID] = page;
            var palette = LoadPalette();
            var pref = Pref(player);
            CuiHelper.DestroyUi(player, UiRoot);
            var c = new CuiElementContainer();
            c.Add(new CuiElement
            {
                Name = UiRoot, Parent = "Overlay", DestroyUi = UiRoot,
                Components =
                {
                    new CuiImageComponent { Color = Hex(palette.Overlay), BlocksRaycast = true },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" },
                    new CuiNeedsCursorComponent()
                }
            });
            float w = config.WindowWidth;
            float h = config.WindowHeight;
            string frame = UiRoot + ".Frame";
            c.Add(new CuiElement
            {
                Name = frame, Parent = UiRoot,
                Components =
                {
                    new CuiImageComponent { Color = Hex(palette.Background) },
                    new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = (-w / 2f).ToString(CultureInfo.InvariantCulture) + " " + (-h / 2f).ToString(CultureInfo.InvariantCulture), OffsetMax = (w / 2f).ToString(CultureInfo.InvariantCulture) + " " + (h / 2f).ToString(CultureInfo.InvariantCulture) }
                }
            });
            string header = Box(c, frame, palette.Header, 0, h - NightshadeLockerRules.Layout.Header, w, h);
            Box(c, frame, palette.Accent, 0, h - NightshadeLockerRules.Layout.Header - 2, w, h - NightshadeLockerRules.Layout.Header);
            c.Add(new CuiElement
            {
                Name = CuiHelper.GetGuid(), Parent = header,
                Components =
                {
                    new CuiRawImageComponent { Url = Logo(), Color = "1 1 1 1", BlocksRaycast = false },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "14 9", OffsetMax = "42 37" }
                }
            });
            float textRight = w - 56f;
            Label(c, header, "Nightshade Locker", 16, palette.Text, TextAnchor.MiddleLeft, 50, 16, textRight, 40, true);
            Label(c, header, palette.Tagline, 10, palette.SubText, TextAnchor.MiddleLeft, 50, 4, conflict ? 196f : textRight, 18, false);
            float tabW = 88f;
            float tabGap = 8f;
            float tabStrip = tabW * 4f + tabGap * 3f;
            float tabX = (w - tabStrip) * 0.5f;
            if (tabX < 12f) tabX = 12f;
            float tabY = h - NightshadeLockerRules.Layout.Header - 2 - NightshadeLockerRules.Layout.Tab - 8;
            var rows = page == "places" ? PlaceRows(pref) : page == "closing" ? CloseRows(player, pref) : page == "code" ? CodeRows(pref) : LockRows(player, pref);
            int contentH = 12;
            for (int i = 0; i < rows.Count; i++)
                contentH += rows[i].Kind == "section" ? NightshadeLockerRules.Layout.Section : rows[i].Kind == "field" ? 36 : NightshadeLockerRules.Layout.Row + NightshadeLockerRules.Layout.RowGap;
            contentH = Math.Max(contentH, 80);
            string scroll = frame + ".Scroll";
            c.Add(new CuiElement
            {
                Name = scroll, Parent = frame,
                Components =
                {
                    new CuiImageComponent { Color = Hex(palette.Panel), BlocksRaycast = true },
                    new CuiNeedsCursorComponent(),
                    new CuiScrollViewComponent
                    {
                        Vertical = true, Horizontal = false,
                        MovementType = ScrollRect.MovementType.Clamped,
                        Elasticity = 0.08f, Inertia = true, DecelerationRate = 0.12f, ScrollSensitivity = 28f,
                        ContentTransform = new CuiRectTransform { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 -" + contentH, OffsetMax = "0 0", Pivot = "0.5 1" },
                        VerticalScrollbar = new CuiScrollbar { Size = 4, AutoHide = true, HandleColor = Hex(palette.Accent), TrackColor = Hex(palette.Panel) }
                    },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "14 12", OffsetMax = (w - 14).ToString(CultureInfo.InvariantCulture) + " " + (tabY - 8).ToString(CultureInfo.InvariantCulture) }
                }
            });
            float cursor = 6;
            float inner = w - 46;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Kind == "section")
                {
                    Label(c, scroll, row.Text.ToUpperInvariant(), 10, palette.SubText, TextAnchor.LowerLeft, 8, contentH - cursor - 16, inner - 8, contentH - cursor, true);
                    cursor += NightshadeLockerRules.Layout.Section;
                    continue;
                }
                if (row.Kind == "note")
                {
                    Label(c, scroll, row.Text, 11, palette.SubText, TextAnchor.MiddleLeft, 8, contentH - cursor - 20, inner - 8, contentH - cursor, false);
                    cursor += 22;
                    continue;
                }
                if (row.Kind == "field")
                {
                    Label(c, scroll, row.Text, 11, palette.SubText, TextAnchor.MiddleLeft, 8, contentH - cursor - 30, 120, contentH - cursor - 6, false);
                    string box = Box(c, scroll, palette.Card, 124, contentH - cursor - 30, inner - 8, contentH - cursor - 4);
                    c.Add(new CuiElement
                    {
                        Name = CuiHelper.GetGuid(), Parent = box,
                        Components =
                        {
                            new CuiInputFieldComponent { Text = row.Value ?? string.Empty, Command = row.Command ?? string.Empty, FontSize = 13, Color = Hex(palette.Text), Align = TextAnchor.MiddleLeft, Font = FontReg, CharsLimit = 4, NeedsKeyboard = true },
                            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "8 0", OffsetMax = "-8 0" }
                        }
                    });
                    cursor += 36;
                    continue;
                }
                float top = cursor;
                float bottom = contentH - top - NightshadeLockerRules.Layout.Row;
                string tone = row.Live ? palette.Text : palette.SubText;
                if (!string.IsNullOrEmpty(row.Command) && row.Live)
                {
                    c.Add(new CuiElement
                    {
                        Name = CuiHelper.GetGuid(), Parent = scroll,
                        Components =
                        {
                            new CuiButtonComponent { Command = row.Command, Color = "0 0 0 0.02", BlocksRaycast = true },
                            new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = "0 " + (-(top + NightshadeLockerRules.Layout.Row)).ToString(CultureInfo.InvariantCulture), OffsetMax = inner.ToString(CultureInfo.InvariantCulture) + " " + (-top).ToString(CultureInfo.InvariantCulture) }
                        }
                    });
                }
                Label(c, scroll, row.Text, 12, tone, TextAnchor.MiddleLeft, 8, bottom, inner - 64, bottom + NightshadeLockerRules.Layout.Row, false);
                DrawSwitch(c, scroll, palette, inner - 46, bottom + 6, row.On, row.Live);
                cursor += NightshadeLockerRules.Layout.Row + NightshadeLockerRules.Layout.RowGap;
                _ = bottom;
            }
            DrawTab(c, frame, palette, "locks", "Locks", page, tabX, tabY);
            DrawTab(c, frame, palette, "places", "Places", page, tabX + tabW + tabGap, tabY);
            DrawTab(c, frame, palette, "closing", "Closing", page, tabX + (tabW + tabGap) * 2f, tabY);
            DrawTab(c, frame, palette, "code", "Code", page, tabX + (tabW + tabGap) * 3f, tabY);
            if (conflict)
                Label(c, header, "Paused while the other locker is loaded.", 10, palette.DangerText, TextAnchor.MiddleRight, 200, 4, textRight, 18, false);
            float closeSize = 26f;
            float closeBottom = h - NightshadeLockerRules.Layout.Header + (NightshadeLockerRules.Layout.Header - closeSize) * 0.5f;
            float closeX1 = (w - 12f - closeSize) - (w / 2f);
            float closeY1 = closeBottom - (h / 2f);
            float closeX2 = (w - 12f) - (w / 2f);
            float closeY2 = (closeBottom + closeSize) - (h / 2f);
            string closeName = CuiHelper.GetGuid();
            var closeButton = new CuiButtonComponent { Command = UiCmd + " dismiss", Color = Hex(palette.Danger), BlocksRaycast = true };
            closeButton.Close = UiRoot;
            c.Add(new CuiElement
            {
                Name = closeName,
                Parent = UiRoot,
                Components =
                {
                    closeButton,
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin = closeX1.ToString(CultureInfo.InvariantCulture) + " " + closeY1.ToString(CultureInfo.InvariantCulture),
                        OffsetMax = closeX2.ToString(CultureInfo.InvariantCulture) + " " + closeY2.ToString(CultureInfo.InvariantCulture)
                    }
                }
            });
            Label(c, closeName, "X", 13, palette.DangerText, TextAnchor.MiddleCenter, 0, 0, closeSize, closeSize, true);
            CuiHelper.AddUi(player, c);
        }

        private void CloseUi(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, UiRoot);
            uiPage.Remove(player.userID);
        }

        private void DrawTab(CuiElementContainer c, string parent, Palette palette, string id, string title, string current, float x, float y)
        {
            bool on = current == id;
            Button(c, parent, UiCmd + " page " + id, on ? palette.Card : palette.Background, x, y, x + 88, y + 22, title, 12, on ? palette.AccentText : palette.SubText);
            if (on) Box(c, parent, palette.Accent, x + 16, y, x + 72, y + 2);
        }

        private static void DrawSwitch(CuiElementContainer c, string parent, Palette palette, float x, float y, bool on, bool live)
        {
            string track = on && live ? palette.Accent : palette.Button;
            Box(c, parent, track, x, y, x + 34, y + 14);
            float knob = on ? x + 18 : x + 2;
            Box(c, parent, live ? palette.Text : palette.SubText, knob, y + 2, knob + 10, y + 12);
        }

        private List<Row> LockRows(BasePlayer player, NightshadeLockerRules.PlayerPref pref)
        {
            bool autoPerm = HasPerm(player, PermAuto) || IsStaff(player);
            bool closePerm = HasPerm(player, PermClose) || IsStaff(player);
            var rows = new List<Row>();
            rows.Add(new Row { Kind = "section", Text = "Automatic" });
            rows.Add(new Row { Text = autoPerm ? "Place locks for me" : "Place locks for me  (needs autolock)", Command = autoPerm ? UiCmd + " toggle auto" : null, On = pref.Auto, Live = autoPerm });
            rows.Add(new Row { Text = "Key lock if no code lock is on me", Command = UiCmd + " toggle key", On = pref.KeyFallback });
            rows.Add(new Row { Text = "Lock the vehicle when I get off", Command = UiCmd + " toggle dismount", On = pref.LockOnDismount });
            rows.Add(new Row { Text = closePerm ? "Close doors for me" : "Close doors for me  (needs autoclose)", Command = closePerm ? UiCmd + " toggle close" : null, On = pref.AutoClose, Live = closePerm });
            rows.Add(new Row { Kind = "section", Text = "Sharing" });
            rows.Add(new Row { Text = config.ForceShare.HasValue ? "Team share is set by the server" : "Share locks with my team", Command = config.ForceShare.HasValue ? null : UiCmd + " toggle share", On = WantsShare(pref), Live = !config.ForceShare.HasValue });
            rows.Add(new Row { Kind = "section", Text = "Privacy" });
            rows.Add(new Row { Text = "Streamer mode", Command = UiCmd + " toggle stream", On = pref.Streamer });
            rows.Add(new Row { Text = "Flight Control skin when I own it", Command = UiCmd + " toggle skin", On = pref.FlightSkin });
            return rows;
        }

        private List<Row> PlaceRows(NightshadeLockerRules.PlayerPref pref)
        {
            var rows = new List<Row>();
            string section = string.Empty;
            var order = NightshadeLockerRules.CategoryOrder();
            for (int i = 0; i < order.Length; i++)
            {
                string key = order[i];
                string next = NightshadeLockerRules.SectionFor(key);
                if (next != section)
                {
                    section = next;
                    rows.Add(new Row { Kind = "section", Text = section });
                }
                bool server = CategoryOn(config.LockCategories, key);
                rows.Add(new Row
                {
                    Text = NightshadeLockerRules.Label(key) + (server ? string.Empty : "   server off"),
                    Command = server ? UiCmd + " lock " + key : null,
                    On = pref.LockOn(key),
                    Live = server
                });
            }
            return rows;
        }

        private List<Row> CloseRows(BasePlayer player, NightshadeLockerRules.PlayerPref pref)
        {
            bool perm = HasPerm(player, PermClose) || IsStaff(player);
            var rows = new List<Row>();
            rows.Add(new Row { Kind = "section", Text = "Closing" });
            rows.Add(new Row { Text = perm ? "Automatic closing" : "Automatic closing  (needs autoclose)", Command = perm ? UiCmd + " toggle close" : null, On = pref.AutoClose, Live = perm });
            rows.Add(new Row { Text = "Delay " + pref.CloseDelay + "s    shorter", Command = UiCmd + " delay down", On = false });
            rows.Add(new Row { Text = "Delay " + pref.CloseDelay + "s    longer", Command = UiCmd + " delay up", On = true });
            rows.Add(new Row { Kind = "section", Text = "Which doors" });
            string[] order = { "door", "double", "window", "garage", "hatch", "external", "fence", "legacy" };
            for (int i = 0; i < order.Length; i++)
            {
                string key = order[i];
                bool server = CategoryOn(config.CloseCategories, key);
                rows.Add(new Row
                {
                    Text = NightshadeLockerRules.Label(key) + (server ? string.Empty : "   server off"),
                    Command = server ? UiCmd + " close " + key : null,
                    On = pref.CloseOn(key),
                    Live = server
                });
            }
            return rows;
        }

        private List<Row> CodeRows(NightshadeLockerRules.PlayerPref pref)
        {
            var rows = new List<Row>();
            rows.Add(new Row { Kind = "section", Text = "Your code" });
            bool hide = pref.Streamer;
            rows.Add(new Row { Kind = "field", Text = "Code", Value = hide ? string.Empty : pref.Code, Command = UiCmd + " setpin" });
            rows.Add(new Row { Kind = "note", Text = hide ? "Streamer mode is hiding the saved code." : "Four digits. Enter replaces the saved code." });
            rows.Add(new Row { Kind = "section", Text = "Guest" });
            rows.Add(new Row { Text = "Guest code on new locks", Command = UiCmd + " toggle guest", On = pref.GuestEnabled });
            rows.Add(new Row { Kind = "field", Text = "Guest", Value = hide ? string.Empty : pref.Guest, Command = UiCmd + " setguest" });
            rows.Add(new Row { Kind = "section", Text = "Apply" });
            rows.Add(new Row { Text = "Write this code onto my locks", Command = UiCmd + " apply", On = true });
            return rows;
        }

        private void UiAction(BasePlayer player, string[] args)
        {
            if (args.Length == 0) return;
            string page;
            if (!uiPage.TryGetValue(player.userID, out page)) page = "locks";
            string verb = args[0].ToLowerInvariant();
            if (verb == "dismiss" || (verb == "close" && args.Length < 2))
            {
                CloseUi(player);
                return;
            }
            if (verb == "page" && args.Length > 1)
            {
                OpenUi(player, args[1]);
                return;
            }
            if (Paused(player, true)) { OpenUi(player, page); return; }
            var pref = Pref(player);
            if (verb == "toggle" && args.Length > 1)
            {
                switch (args[1])
                {
                    case "auto":
                        if (HasPerm(player, PermAuto) || IsStaff(player)) pref.Auto = !pref.Auto;
                        break;
                    case "key": pref.KeyFallback = !pref.KeyFallback; break;
                    case "share":
                        if (!config.ForceShare.HasValue) pref.Share = !pref.Share;
                        SaveStore();
                        RefreshShare(player.userID);
                        break;
                    case "stream": pref.Streamer = !pref.Streamer; break;
                    case "skin": pref.FlightSkin = !pref.FlightSkin; break;
                    case "dismount": pref.LockOnDismount = !pref.LockOnDismount; break;
                    case "close":
                        if (HasPerm(player, PermClose) || IsStaff(player)) pref.AutoClose = !pref.AutoClose;
                        break;
                    case "guest":
                        pref.GuestEnabled = !pref.GuestEnabled;
                        ApplyGuest(player.userID, pref.Guest, pref.GuestEnabled);
                        break;
                }
            }
            else if (verb == "lock" && args.Length > 1 && CategoryOn(config.LockCategories, args[1]))
                pref.SetLock(args[1], !pref.LockOn(args[1]));
            else if (verb == "close" && args.Length > 1 && CategoryOn(config.CloseCategories, args[1]))
                pref.SetClose(args[1], !pref.CloseOn(args[1]));
            else if (verb == "delay" && args.Length > 1)
            {
                int delta = args[1] == "up" ? 5 : -5;
                pref.CloseDelay = NightshadeLockerRules.ClampDelay(pref.CloseDelay + delta, config.MinDelay, config.MaxDelay);
            }
            else if (verb == "setpin" && args.Length > 1)
            {
                if (NightshadeLockerRules.PinValid(args[1])) pref.Code = args[1];
                else Reply(player, "Use 4 digits.");
            }
            else if (verb == "setguest" && args.Length > 1)
            {
                if (args[1] == "clear" || args[1].Length == 0) { pref.Guest = string.Empty; pref.GuestEnabled = false; }
                else if (NightshadeLockerRules.PinValid(args[1])) { pref.Guest = args[1]; pref.GuestEnabled = true; }
                else { Reply(player, "Use 4 digits."); OpenUi(player, page); return; }
                ApplyGuest(player.userID, pref.Guest, pref.GuestEnabled);
            }
            else if (verb == "apply")
            {
                if (!NightshadeLockerRules.PinValid(pref.Code)) Reply(player, "Save a 4 digit code first.");
                else
                {
                    int count = ApplyPin(player.userID, pref.Code, false);
                    ApplyGuest(player.userID, pref.Guest, pref.GuestEnabled);
                    Reply(player, "Updated " + count + " locks.");
                }
            }
            else
            {
                return;
            }
            SaveStore();
            OpenUi(player, page);
        }


            private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (!worldReady || conflict || !config.ProximityButton || player == null || input == null) return;
            if (!input.WasJustPressed(BUTTON.FIRE_THIRD)) return;
            bool shift = input.IsDown(BUTTON.SPRINT);
            bool ctrl = input.IsDown(BUTTON.DUCK);
            int chord = config.ProximityChord;
            if (chord == 2 && !shift) return;
            if (chord == 3 && !ctrl) return;
            if (chord == 1 && (shift || ctrl)) return;
            var door = Look(player) as Door;
            if (door == null || door.GetSlot(BaseEntity.Slot.UpperModifier) as DoorCloser == null) return;
            if (!CanUse(player)) return;
            OpenUi(player, "closing");
        }

        private void RefreshProximity()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (!CanUse(player)) continue;
                var door = Look(player) as Door;
                bool show = door != null && door.GetSlot(BaseEntity.Slot.UpperModifier) is DoorCloser;
                string name = UiRoot + ".Near";
                CuiHelper.DestroyUi(player, name);
                if (!show) continue;
                var palette = LoadPalette();
                var c = new CuiElementContainer();
                c.Add(new CuiElement
                {
                    Name = name,
                    Parent = "Overlay",
                    Components =
                    {
                        new CuiButtonComponent { Command = UiCmd + " page closing", Color = Hex(palette.Accent) },
                        new CuiRectTransformComponent { AnchorMin = "1 1", AnchorMax = "1 1", OffsetMin = "-150 -70", OffsetMax = "-16 -28" }
                    }
                });
                Label(c, name, "DOOR DELAY", 12, palette.AccentText, TextAnchor.MiddleCenter, 0, 0, 134, 42, true);
                CuiHelper.AddUi(player, c);
            }
        }

        private static string Box(CuiElementContainer c, string parent, string color, float x1, float y1, float x2, float y2)
        {
            return Rect(c, parent, color, "0 0", "0 0", x1, y1, x2, y2);
        }

        private static string TopBox(CuiElementContainer c, string parent, string color, float x, float y, float width, float height)
        {
            return Rect(c, parent, color, "0 1", "0 1", x, -(y + height), x + width, -y);
        }

        private static string Rect(CuiElementContainer c, string parent, string color, string anchorMin, string anchorMax, float x1, float y1, float x2, float y2)
        {
            string name = CuiHelper.GetGuid();
            c.Add(new CuiElement
            {
                Name = name,
                Parent = parent,
                Components =
                {
                    new CuiImageComponent { Color = Hex(color), BlocksRaycast = false },
                    new CuiRectTransformComponent { AnchorMin = anchorMin, AnchorMax = anchorMax, OffsetMin = x1.ToString(CultureInfo.InvariantCulture) + " " + y1.ToString(CultureInfo.InvariantCulture), OffsetMax = x2.ToString(CultureInfo.InvariantCulture) + " " + y2.ToString(CultureInfo.InvariantCulture) }
                }
            });
            return name;
        }

        private static void Label(CuiElementContainer c, string parent, string text, int size, string color, TextAnchor align, float x1, float y1, float x2, float y2, bool bold)
        {
            c.Add(new CuiElement
            {
                Name = CuiHelper.GetGuid(),
                Parent = parent,
                Components =
                {
                    new CuiTextComponent { Text = text ?? string.Empty, FontSize = size, Color = Hex(color), Align = align, Font = bold ? FontBold : FontReg, BlocksRaycast = false },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = x1.ToString(CultureInfo.InvariantCulture) + " " + y1.ToString(CultureInfo.InvariantCulture), OffsetMax = x2.ToString(CultureInfo.InvariantCulture) + " " + y2.ToString(CultureInfo.InvariantCulture) }
                }
            });
        }

        private static void Button(CuiElementContainer c, string parent, string command, string color, float x1, float y1, float x2, float y2, string text, int size, string textColor, bool dismiss = false)
        {
            string name = CuiHelper.GetGuid();
            var button = new CuiButtonComponent { Command = command ?? string.Empty, Color = Hex(color), BlocksRaycast = true };
            if (dismiss) button.Close = UiRoot;
            c.Add(new CuiElement
            {
                Name = name,
                Parent = parent,
                Components =
                {
                    button,
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = x1.ToString(CultureInfo.InvariantCulture) + " " + y1.ToString(CultureInfo.InvariantCulture), OffsetMax = x2.ToString(CultureInfo.InvariantCulture) + " " + y2.ToString(CultureInfo.InvariantCulture) }
                }
            });
            Label(c, name, text, size, textColor, TextAnchor.MiddleCenter, 0, 0, x2 - x1, y2 - y1, true);
        }

        private static string Hex(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return "1 1 1 1";
            if (hex.IndexOf(' ') >= 0) return hex;
            try
            {
                string h = hex.Trim().TrimStart('#');
                int r = int.Parse(h.Substring(0, 2), NumberStyles.HexNumber);
                int g = int.Parse(h.Substring(2, 2), NumberStyles.HexNumber);
                int b = int.Parse(h.Substring(4, 2), NumberStyles.HexNumber);
                int a = h.Length >= 8 ? int.Parse(h.Substring(6, 2), NumberStyles.HexNumber) : 255;
                return (r / 255f).ToString("0.###", CultureInfo.InvariantCulture) + " "
                    + (g / 255f).ToString("0.###", CultureInfo.InvariantCulture) + " "
                    + (b / 255f).ToString("0.###", CultureInfo.InvariantCulture) + " "
                    + (a / 255f).ToString("0.###", CultureInfo.InvariantCulture);
            }
            catch
            {
                return "1 1 1 1";
            }
        }

        private void Reply(BasePlayer player, string message)
        {
            if (player == null || string.IsNullOrEmpty(message)) return;
            var palette = LoadPalette();
            player.ChatMessage("<color=" + palette.Accent + ">[" + config.Prefix + "]</color> " + message);
        }

        #endregion
    }
}
#endif

namespace Oxide.Plugins
{
    public static class NightshadeLockerRules
    {
        public const string PrimaryCommand = "nslock";
        public const string ProvenanceFree = "free-grant";
        public const string ProvenancePaid = "paid-inventory";

        public enum Pickup { Vanilla, Block, RemoveWithoutItem }
        public enum Refund { None, Give, Drop, Owe }

        public sealed class PlayerPref
        {
            public string SteamId = string.Empty;
            public string Code = string.Empty;
            public string Guest = string.Empty;
            public bool Auto;
            public bool KeyFallback;
            public bool GuestEnabled;
            public bool Share = true;
            public bool Streamer;
            public bool FlightSkin;
            public bool LockOnDismount;
            public bool AutoClose;
            public int CloseDelay = 30;
            public int OwedCode;
            public int OwedKey;
            public int OwedCloser;
            public Dictionary<string, bool> Lock = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, bool> Close = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            public static PlayerPref CreateDefault()
            {
                var pref = new PlayerPref();
                Set(pref.Lock, DefaultPlayerLocks());
                Set(pref.Close, DefaultPlayerCloses());
                pref.Share = true;
                pref.Auto = false;
                pref.KeyFallback = false;
                pref.LockOnDismount = false;
                pref.AutoClose = false;
                pref.CloseDelay = 30;
                return pref;
            }

            public static void Normalize(PlayerPref pref, int delayFallback)
            {
                if (pref.Lock == null) pref.Lock = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                if (pref.Close == null) pref.Close = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                if (pref.Code == null) pref.Code = string.Empty;
                if (pref.Guest == null) pref.Guest = string.Empty;
                if (!PinValid(pref.Code)) pref.Code = string.Empty;
                if (!PinValid(pref.Guest)) pref.Guest = string.Empty;
                if (pref.CloseDelay < 1) pref.CloseDelay = delayFallback;
            }

            public bool LockOn(string key)
            {
                bool value;
                return Lock != null && Lock.TryGetValue(key, out value) && value;
            }

            public bool CloseOn(string key)
            {
                bool value;
                return Close != null && Close.TryGetValue(key, out value) && value;
            }

            public void SetLock(string key, bool value)
            {
                if (Lock == null) Lock = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                Lock[key] = value;
            }

            public void SetClose(string key, bool value)
            {
                if (Close == null) Close = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                Close[key] = value;
            }
        }

        public sealed class EntityRule
        {
            public string Name = string.Empty;
            public string Prefab = string.Empty;
            public string Category = "vehicles";
            public bool Enabled = true;
        }

        public sealed class Match
        {
            public string Category;
            public string Prefab;
        }

        public sealed class PlaceRequest
        {
            public bool Conflict;
            public bool HasUse;
            public bool Known;
            public bool CategoryOpen;
            public bool AlreadyLocked;
            public bool SkinBlocked;
            public bool Vehicle;
            public bool RequirePrivilege;
            public bool HasPrivilege;
            public bool RequireUnownedPrivilege;
            public bool Unowned;
            public bool OwnedByOther;
            public bool AllowUnowned;
            public bool AllowOthers;
            public bool BypassVehicle;
            public bool HasItemOrFree;
            public bool JustPlaced;
        }

        public struct LockChoice
        {
            public string Item;
            public bool Free;
        }

        public sealed class ThemeRow
        {
            public string Name;
            public string Tagline;
            public string From;
            public string To;
            public int Priority;
            public string Background;
            public string Header;
            public string Panel;
            public string Card;
            public string Accent;
            public string AccentText;
            public string Button;
            public string ButtonText;
            public string Text;
            public string SubText;
        }

        public sealed class ImportReport
        {
            public int Read;
            public int Created;
            public int Updated;
            public int Unchanged;
            public int Skipped;
            public string Summary = string.Empty;
        }

        public static bool IsFree(string provenance)
        {
            return string.Equals(provenance, ProvenanceFree, StringComparison.Ordinal);
        }

        public static Pickup DecidePickup(bool isLocked, bool authorized, string provenance, bool freePermissionIgnored)
        {
            _ = freePermissionIgnored;
            if (isLocked) return Pickup.Block;
            if (IsFree(provenance)) return authorized ? Pickup.RemoveWithoutItem : Pickup.Block;
            return Pickup.Vanilla;
        }

        public static Refund PlanReturn(string provenance, bool inventoryAccepts, bool dropSucceeds)
        {
            if (IsFree(provenance)) return Refund.None;
            if (inventoryAccepts) return Refund.Give;
            if (dropSucceeds) return Refund.Drop;
            return Refund.Owe;
        }

        public static void AddOwed(PlayerPref pref, string shortname, int amount)
        {
            if (pref == null || amount < 1) return;
            if (shortname == "lock.key") pref.OwedKey += amount;
            else if (shortname == "door.closer") pref.OwedCloser += amount;
            else pref.OwedCode += amount;
        }

        public static bool PinValid(string pin)
        {
            if (string.IsNullOrEmpty(pin) || pin.Length != 4) return false;
            for (int i = 0; i < 4; i++)
                if (pin[i] < '0' || pin[i] > '9') return false;
            return true;
        }

        public static string MakePin(ulong user, long ticks)
        {
            int value = (int)((user ^ (ulong)ticks) % 10000UL);
            if (value < 0) value = -value;
            return value.ToString("D4");
        }

        public static LockChoice ChooseLock(bool keyOnly, bool hasCode, bool hasKey, bool keyFallback, bool freeCode)
        {
            if (keyOnly)
            {
                if (hasKey) return new LockChoice { Item = "lock.key", Free = false };
                return new LockChoice();
            }
            if (hasCode) return new LockChoice { Item = "lock.code", Free = false };
            if (keyFallback && hasKey) return new LockChoice { Item = "lock.key", Free = false };
            if (freeCode) return new LockChoice { Item = "lock.code", Free = true };
            return new LockChoice();
        }

        public static bool AutoWanted(bool serverCategory, bool playerMaster, bool playerCategory)
        {
            return serverCategory && playerMaster && playerCategory;
        }

        public static int ClampDelay(int value, int min, int max)
        {
            if (max < min) max = min;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static bool ControlAllowed(bool otherLoaded) => !otherLoaded;

        public static bool MayClaim(ulong owner, ulong actor, bool claimUnowned)
        {
            return claimUnowned && owner == 0 && actor != 0;
        }

        public static bool MayReplaceOwner(ulong owner, ulong actor, bool replaceOthers, bool allowedOnThatVehicle)
        {
            return replaceOthers && allowedOnThatVehicle && owner != 0 && actor != 0 && owner != actor;
        }

        public static bool PushAllowed(bool allowPush, bool allowInPrivilege, bool hasPrivilege, bool blockedByLock)
        {
            if (!blockedByLock) return true;
            if (allowPush) return true;
            return allowInPrivilege && hasPrivilege;
        }

        public static string PrivateCodeText(string code, string guest, bool includeGuest)
        {
            if (!PinValid(code)) return "That lock has no code.";
            string text = "Code " + code + ".";
            if (includeGuest && PinValid(guest)) text += " Guest " + guest + ".";
            return text;
        }

        public static string SafeText(string message, string secret)
        {
            if (string.IsNullOrEmpty(message)) return string.Empty;
            if (string.IsNullOrEmpty(secret)) return message;
            return message.Replace(secret, "****");
        }

        public static bool SkinBlocked(ulong skin, IList<ulong> blocked)
        {
            if (skin == 0 || blocked == null) return false;
            for (int i = 0; i < blocked.Count; i++)
                if (blocked[i] == skin) return true;
            return false;
        }

        public static List<ulong> ShareTargets(ulong owner, IList<ulong> members, bool share)
        {
            var list = new List<ulong>();
            if (!share || members == null) return list;
            for (int i = 0; i < members.Count; i++)
            {
                ulong id = members[i];
                if (id == 0 || id == owner || list.Contains(id)) continue;
                list.Add(id);
            }
            return list;
        }

        public static string PlaceDenial(PlaceRequest request)
        {
            if (request == null) return "That could not be locked.";
            if (request.Conflict) return "NightshadeLocker is paused because another locker plugin is loaded.";
            if (!request.HasUse) return "You need the nightshadelocker.use permission.";
            if (!request.Known || !request.CategoryOpen) return "Locks are not enabled for that.";
            if (request.AlreadyLocked) return "That already has a lock.";
            if (request.SkinBlocked) return "That skin is excluded from locks.";
            if (request.Vehicle)
            {
                if (request.OwnedByOther && !request.AllowOthers && !request.BypassVehicle)
                    return "That vehicle belongs to someone else.";
                if (request.Unowned && !request.AllowUnowned && !request.BypassVehicle)
                    return "Unowned vehicles are not open for locks.";
                if (request.Unowned && request.RequireUnownedPrivilege && !request.HasPrivilege && !request.BypassVehicle)
                    return "You need building privilege for that vehicle.";
            }
            else
            {
                if (request.RequirePrivilege && !request.HasPrivilege)
                    return "You need building privilege to lock that.";
                if (request.OwnedByOther)
                    return "You cannot lock another player's entity.";
                if (request.Unowned && !request.JustPlaced)
                    return "You cannot lock that.";
            }
            if (!request.HasItemOrFree) return "You need a lock in your inventory.";
            return null;
        }

        public static string LockCategory(string shortPrefab, string kind)
        {
            string close = CloseCategory(shortPrefab);
            if (close == "window") return "windows";
            if (close != null) return "doors";
            string named = CategoryFromName(shortPrefab);
            if (named != null) return named;
            switch (kind)
            {
                case "door": return "doors";
                case "cupboard": return "cupboards";
                case "locker": return "lockers";
                case "stash": return "stashes";
                case "box": return "boxes";
                case "storage": return "storage";
                case "furnace": return "furnaces";
                case "vending": return "vending";
                case "composter": return "composters";
                case "mixing": return "mixing";
                case "planter": return "planters";
                case "turret": return "turrets";
                case "sam": return "sam";
                case "trap": return "traps";
                case "rack": return "racks";
                case "neon": return "neon";
                case "sign": return "signs";
                case "balloon": return "vehicles";
                case "vehicle": return "vehicles";
                case "farming": return "farming";
                case "crafting": return "crafting";
                case "electric": return "electric";
                case "industrial": return "industrial";
                case "quarry": return "quarry";
                case "fun": return "fun";
                default:
                    if (!string.IsNullOrEmpty(shortPrefab))
                    {
                        string s = shortPrefab.ToLowerInvariant();
                        if (s.Contains("ballista") || s.Contains("batteringram") || s.Contains("catapult") || s.Contains("siegetower"))
                            return "medieval";
                    }
                    return null;
            }
        }

        public static class Layout
        {
            public const int Version = 2;
            public const float WindowWidth = 580f;
            public const float WindowHeight = 448f;
            public const float WidthBeforeNarrow = 700f;
            public const int Header = 46;
            public const int Tab = 26;
            public const int Row = 26;
            public const int RowGap = 3;
            public const int Section = 18;
        }

        public static float MigrateWindowWidth(float width)
        {
            if (Math.Abs(width - Layout.WidthBeforeNarrow) < 0.51f) return Layout.WindowWidth;
            return width;
        }

        public static string SectionFor(string key)
        {
            switch (key)
            {
                case "doors":
                case "windows":
                case "cupboards":
                    return "Structures";
                case "boxes":
                case "storage":
                case "lockers":
                case "stashes":
                    return "Storage";
                case "vehicles":
                case "naval":
                case "medieval":
                    return "Vehicles";
                case "furnaces":
                case "vending":
                case "composters":
                case "mixing":
                case "planters":
                case "farming":
                case "quarry":
                    return "Workshop";
                case "turrets":
                case "sam":
                case "traps":
                case "racks":
                    return "Defense";
                case "electric":
                case "industrial":
                    return "Power";
                case "crafting":
                    return "Crafting";
                case "neon":
                case "signs":
                case "fun":
                    return "Decor";
                default:
                    return "Other";
            }
        }

        public static string[] CategoryOrder()
        {
            return new[]
            {
                "doors", "windows", "cupboards",
                "boxes", "storage", "lockers", "stashes",
                "vehicles", "naval", "medieval",
                "furnaces", "vending", "composters", "mixing", "planters", "farming", "quarry",
                "turrets", "sam", "traps", "racks",
                "electric", "industrial", "crafting",
                "neon", "signs", "fun", "other", "custom"
            };
        }

        public static string CategoryFromName(string shortPrefab)
        {
            if (string.IsNullOrEmpty(shortPrefab)) return null;
            string s = shortPrefab.ToLowerInvariant();
            string[] map =
            {
                "fluidswitch", "farming", "sprinkler", "farming", "water_catcher", "farming", "waterbarrel", "farming",
                "poweredwaterpurifier", "farming", "chickencoop", "farming", "beehive", "farming", "hitchtrough", "farming",
                "planter", "planters", "plantpot", "planters", "composter", "composters", "mixingtable", "mixing",
                "industrial.furnace", "furnaces", "industrial_electric_furnace", "furnaces", "furnace", "furnaces",
                "fireplace", "furnaces", "bbq", "furnaces", "hobobarrel", "furnaces", "refinery", "furnaces",
                "campfire", "furnaces", "cauldron", "furnaces", "skull_fire", "furnaces",
                "rfbroadcaster", "electric", "rfreceiver", "electric", "smartswitch", "electric", "smartalarm", "electric",
                "fuel_generator", "electric", "searchlight", "electric", "cctv", "electric", "button", "electric",
                "elevator", "electric", "modularcarlift", "electric", "fogmachine", "electric", "snowmachine", "electric",
                "switch", "electric", "timer", "electric",
                "industrialconveyor", "industrial", "industrialcrafter", "industrial", "industrial_storage", "boxes", "hopper", "industrial",
                "weaponrack", "racks", "autoturret", "turrets", "flameturret", "traps", "guntrap", "traps", "sam_site", "sam",
                "workbench", "crafting", "repairbench", "crafting", "researchtable", "crafting", "computerstation", "crafting",
                "cookingworkbench", "crafting", "recycler", "crafting",
                "mining_quarry", "quarry", "miningquarry", "quarry", "pumpjack", "quarry",
                "piano", "fun", "drumkit", "fun", "boombox", "fun", "chippy", "fun", "arcade", "fun",
                "strobelight", "fun", "laserlight", "fun", "soundlight", "fun", "lantern", "fun", "tunalight", "fun", "jackolantern", "fun",
                "neon", "neon", "canvas", "signs", "lightup", "signs", "goldframe", "signs", "scrapframe", "signs", "easel", "signs", "sign.", "signs",
                "vending", "vending", "stash", "stashes", "dropbox", "boxes", "mailbox", "boxes", "barrel", "boxes", "shopfront", "boxes",
                "mannequin", "storage", "ballista", "medieval", "batteringram", "medieval", "catapult", "medieval", "siegetower", "medieval"
            };
            for (int i = 0; i + 1 < map.Length; i += 2)
                if (s.IndexOf(map[i], StringComparison.Ordinal) >= 0) return map[i + 1];
            return null;
        }

        public static string CloseCategory(string shortPrefab)
        {
            if (string.IsNullOrEmpty(shortPrefab)) return null;
            string s = shortPrefab.ToLowerInvariant();
            if (s.Contains("legacy.shelter") && s.Contains("door")) return "legacy";
            if (s.Contains("garagedoor")) return "garage";
            if (s.Contains("ladder.hatch")) return "hatch";
            if (s.Contains("fence.gate")) return "fence";
            if (s.Contains("gates.external")) return "external";
            if (s.Contains("window") || s.Contains("shutter")) return "window";
            if (s.Contains("door.double") || s.Contains(".double.")) return "double";
            if (s.Contains("door")) return "door";
            return null;
        }

        public static EntityRule FindRule(string prefab, IList<EntityRule> vehicles, IList<EntityRule> extras)
        {
            EntityRule found = FindIn(prefab, vehicles);
            if (found != null) return found;
            return FindIn(prefab, extras);
        }

        private static EntityRule FindIn(string prefab, IList<EntityRule> rules)
        {
            if (string.IsNullOrEmpty(prefab) || rules == null) return null;
            for (int i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                if (rule != null && string.Equals(rule.Prefab, prefab, StringComparison.OrdinalIgnoreCase)) return rule;
            }
            return null;
        }

        public static void FillMissing(Dictionary<string, bool> dest, Dictionary<string, bool> defaults)
        {
            if (dest == null || defaults == null) return;
            foreach (var pair in defaults)
                if (!dest.ContainsKey(pair.Key)) dest[pair.Key] = pair.Value;
        }

        public static void ApplyToggleDefaults(PlayerPref dest, PlayerPref defaults)
        {
            if (dest == null || defaults == null) return;
            dest.Auto = defaults.Auto;
            dest.KeyFallback = defaults.KeyFallback;
            dest.GuestEnabled = defaults.GuestEnabled;
            dest.Share = defaults.Share;
            dest.Streamer = defaults.Streamer;
            dest.FlightSkin = defaults.FlightSkin;
            dest.LockOnDismount = defaults.LockOnDismount;
            dest.AutoClose = defaults.AutoClose;
            dest.CloseDelay = defaults.CloseDelay;
            dest.Lock = defaults.Lock == null ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, bool>(defaults.Lock, StringComparer.OrdinalIgnoreCase);
            dest.Close = defaults.Close == null ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, bool>(defaults.Close, StringComparer.OrdinalIgnoreCase);
        }

        public static ThemeRow ResolveTheme(string mode, IList<ThemeRow> themes, int monthDay)
        {
            if (themes == null || themes.Count == 0) return null;
            if (!string.IsNullOrEmpty(mode) && !mode.Equals("Auto", StringComparison.OrdinalIgnoreCase) && !mode.Equals("Shop", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < themes.Count; i++)
                    if (themes[i] != null && string.Equals(themes[i].Name, mode, StringComparison.OrdinalIgnoreCase)) return themes[i];
            }
            ThemeRow best = null;
            for (int i = 0; i < themes.Count; i++)
            {
                var row = themes[i];
                if (row == null || !InRange(row.From, row.To, monthDay)) continue;
                if (best == null || row.Priority > best.Priority) best = row;
            }
            return best ?? themes[0];
        }

        public static bool InRange(string from, string to, int today)
        {
            int start = ParseMd(from, 101);
            int end = ParseMd(to, 1231);
            return start <= end ? today >= start && today <= end : today >= start || today <= end;
        }

        public static int ParseMd(string text, int fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            var parts = text.Split('-');
            int month, day;
            if (parts.Length == 2 && int.TryParse(parts[0], out month) && int.TryParse(parts[1], out day))
                return month * 100 + day;
            return fallback;
        }

        public static string BackupName(DateTime utc)
        {
            return "state-" + utc.ToString("yyyyMMddHHmmss") + ".json";
        }

        public static string Label(string key)
        {
            switch (key)
            {
                case "doors": return "doors";
                case "windows": return "windows";
                case "boxes": return "boxes";
                case "storage": return "storage";
                case "lockers": return "lockers";
                case "cupboards": return "cupboards";
                case "vehicles": return "vehicles";
                case "naval": return "boats";
                case "medieval": return "medieval";
                case "furnaces": return "furnaces";
                case "vending": return "vending machines";
                case "composters": return "composters";
                case "mixing": return "mixing tables";
                case "planters": return "planters";
                case "turrets": return "auto turrets";
                case "sam": return "SAM sites";
                case "traps": return "traps";
                case "racks": return "weapon racks";
                case "stashes": return "stashes";
                case "neon": return "neon signs";
                case "signs": return "signs";
                case "electric": return "electrical";
                case "industrial": return "industrial";
                case "crafting": return "crafting stations";
                case "farming": return "farming";
                case "fun": return "instruments and lights";
                case "quarry": return "quarries and pump jacks";
                case "other": return "other lockable entities";
                case "custom": return "custom entities";
                case "door": return "single doors";
                case "double": return "double doors";
                case "window": return "windows";
                case "garage": return "garage doors";
                case "hatch": return "ladder hatches";
                case "external": return "external gates";
                case "fence": return "fence gates";
                case "legacy": return "legacy shelter doors";
                default: return key;
            }
        }

        public static List<PlayerPref> ReadLegacy(string json, List<string> notes)
        {
            var list = new List<PlayerPref>();
            if (notes == null) notes = new List<string>();
            object rootObj;
            try { rootObj = JsonMini.Parse(json); }
            catch
            {
                notes.Add("Legacy file was not valid JSON.");
                return list;
            }
            var root = rootObj as Dictionary<string, object>;
            if (root == null)
            {
                notes.Add("Legacy file had no object.");
                return list;
            }
            object playersObj;
            root.TryGetValue("Auto Lock Player Settings", out playersObj);
            var players = playersObj as Dictionary<string, object>;
            if (players == null) notes.Add("No auto-lock player settings were found.");
            else
            {
                foreach (var pair in players)
                {
                    var body = pair.Value as Dictionary<string, object>;
                    if (body == null) continue;
                    var pref = PlayerPref.CreateDefault();
                    pref.SteamId = pair.Key;
                    pref.Code = PinOrEmpty(Value(body, "Code Lock"), notes);
                    pref.Guest = PinOrEmpty(Value(body, "Guest Code Lock"), notes);
                    pref.Auto = Bool(body, "Is automatic lock placement enabled.", false);
                    pref.KeyFallback = Bool(body, "Use Key Locks if there are no Code Locks in inventory.", false);
                    pref.GuestEnabled = Bool(body, "Is automatic Guest Code enabled.", false);
                    pref.Share = Bool(body, "Is automatic Share locks with clan/team members enabled.", true);
                    pref.Streamer = Bool(body, "Is streamer mode enabled.", false);
                    pref.FlightSkin = Bool(body, "Code Lock skin: Flight Control Codelock enabled", false);
                    pref.LockOnDismount = Bool(body, "Is automatic lock closing when dismount of the vehicle enabled.", false);
                    SetBool(pref, "doors", body, "Is automatic lock on Doors enabled.");
                    SetBool(pref, "windows", body, "Is automatic lock on Windows enabled.");
                    SetBool(pref, "boxes", body, "Is automatic lock on Boxes enabled.");
                    SetBool(pref, "storage", body, "Is automatic lock on Storage Container enabled.");
                    SetBool(pref, "lockers", body, "Is automatic lock on Lockers enabled.");
                    SetBool(pref, "cupboards", body, "Is automatic lock on TC (Cupboards) enabled.");
                    SetBool(pref, "vehicles", body, "Is automatic lock on Vehicles enabled.");
                    SetBool(pref, "naval", body, "Is automatic lock on Naval Deep Sea enabled.");
                    SetBool(pref, "medieval", body, "Is automatic lock on Medieval entity enabled.");
                    SetBool(pref, "furnaces", body, "Is automatic lock on Furnace enabled.");
                    SetBool(pref, "vending", body, "Is automatic lock on Vending Machine enabled.");
                    SetBool(pref, "composters", body, "Is automatic lock on Composter enabled.");
                    SetBool(pref, "mixing", body, "Is automatic lock on Mixing Table enabled.");
                    SetBool(pref, "planters", body, "Is automatic lock on Planter enabled.");
                    SetBool(pref, "turrets", body, "Is automatic lock on Auto Turret enabled.");
                    SetBool(pref, "sam", body, "Is automatic lock on SAM Site enabled.");
                    SetBool(pref, "traps", body, "Is automatic lock on Traps enabled.");
                    SetBool(pref, "racks", body, "Is automatic lock on Weapon Rack enabled.");
                    SetBool(pref, "stashes", body, "Is automatic lock on Stash enabled.");
                    SetBool(pref, "neon", body, "Is automatic lock on Neon Sign enabled.");
                    SetBool(pref, "signs", body, "Is automatic lock on Sign / Drawing enabled.");
                    SetBool(pref, "other", body, "Is automatic lock on Other Lockable Entities enabled.");
                    SetBool(pref, "custom", body, "Is automatic lock on Other Custom Entities enabled.");
                    list.Add(pref);
                }
            }
            object closingObj;
            root.TryGetValue("Auto Closing Player Settings", out closingObj);
            var closing = closingObj as Dictionary<string, object>;
            notes.Add("Closing preference records in the legacy file: " + (closing == null ? 0 : closing.Count) + ".");
            object locksObj;
            root.TryGetValue("CodeLockItems", out locksObj);
            var locks = locksObj as Dictionary<string, object>;
            notes.Add("Legacy placed-lock rows were left untouched (" + (locks == null ? 0 : locks.Count) + " present). They have no free or paid marker.");
            return list;
        }

        public static ImportReport Merge(Dictionary<string, PlayerPref> dest, IList<PlayerPref> incoming)
        {
            var report = new ImportReport();
            if (dest == null) dest = new Dictionary<string, PlayerPref>(StringComparer.Ordinal);
            if (incoming == null) incoming = new PlayerPref[0];
            var secrets = new List<string>();
            for (int i = 0; i < incoming.Count; i++)
            {
                var row = incoming[i];
                report.Read++;
                if (row == null || string.IsNullOrEmpty(row.SteamId))
                {
                    report.Skipped++;
                    continue;
                }
                if (PinValid(row.Code)) secrets.Add(row.Code);
                if (PinValid(row.Guest)) secrets.Add(row.Guest);
                PlayerPref have;
                if (!dest.TryGetValue(row.SteamId, out have) || have == null)
                {
                    dest[row.SteamId] = Clone(row);
                    report.Created++;
                    continue;
                }
                if (Same(have, row))
                {
                    report.Unchanged++;
                    continue;
                }
                int owedCode = have.OwedCode, owedKey = have.OwedKey, owedCloser = have.OwedCloser;
                var copy = Clone(row);
                copy.OwedCode = owedCode;
                copy.OwedKey = owedKey;
                copy.OwedCloser = owedCloser;
                dest[row.SteamId] = copy;
                report.Updated++;
            }
            report.Summary = Redact("Imported player preferences. Read " + report.Read
                + ", new " + report.Created
                + ", updated " + report.Updated
                + ", unchanged " + report.Unchanged
                + ", skipped " + report.Skipped
                + ". Codes were stored and are not printed.", secrets);
            return report;
        }

        public static string Redact(string text, IList<string> secrets)
        {
            if (string.IsNullOrEmpty(text) || secrets == null) return text ?? string.Empty;
            for (int i = 0; i < secrets.Count; i++)
            {
                var secret = secrets[i];
                if (string.IsNullOrEmpty(secret) || secret.Length < 4) continue;
                text = text.Replace(secret, "****");
            }
            return text;
        }

        private static PlayerPref Clone(PlayerPref row)
        {
            var copy = new PlayerPref
            {
                SteamId = row.SteamId,
                Code = row.Code,
                Guest = row.Guest,
                Auto = row.Auto,
                KeyFallback = row.KeyFallback,
                GuestEnabled = row.GuestEnabled,
                Share = row.Share,
                Streamer = row.Streamer,
                FlightSkin = row.FlightSkin,
                LockOnDismount = row.LockOnDismount,
                AutoClose = row.AutoClose,
                CloseDelay = row.CloseDelay,
                OwedCode = row.OwedCode,
                OwedKey = row.OwedKey,
                OwedCloser = row.OwedCloser,
                Lock = row.Lock == null ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, bool>(row.Lock, StringComparer.OrdinalIgnoreCase),
                Close = row.Close == null ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, bool>(row.Close, StringComparer.OrdinalIgnoreCase)
            };
            return copy;
        }

        private static bool Same(PlayerPref a, PlayerPref b)
        {
            if (a.Auto != b.Auto || a.KeyFallback != b.KeyFallback || a.GuestEnabled != b.GuestEnabled || a.Share != b.Share
                || a.Streamer != b.Streamer || a.FlightSkin != b.FlightSkin || a.LockOnDismount != b.LockOnDismount
                || a.AutoClose != b.AutoClose || a.CloseDelay != b.CloseDelay || a.Code != b.Code || a.Guest != b.Guest)
                return false;
            return SameMap(a.Lock, b.Lock) && SameMap(a.Close, b.Close);
        }

        private static bool SameMap(Dictionary<string, bool> a, Dictionary<string, bool> b)
        {
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;
            foreach (var pair in a)
            {
                bool value;
                if (!b.TryGetValue(pair.Key, out value) || value != pair.Value) return false;
            }
            return true;
        }

        private static void SetBool(PlayerPref pref, string key, Dictionary<string, object> body, string name)
        {
            pref.SetLock(key, Bool(body, name, pref.LockOn(key)));
        }

        private static bool Bool(Dictionary<string, object> body, string name, bool fallback)
        {
            object value;
            if (body == null || !body.TryGetValue(name, out value) || !(value is bool)) return fallback;
            return (bool)value;
        }

        private static string Value(Dictionary<string, object> body, string name)
        {
            object value;
            if (body == null || !body.TryGetValue(name, out value) || value == null) return string.Empty;
            return value.ToString();
        }

        private static string PinOrEmpty(string raw, List<string> notes)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            if (PinValid(raw)) return raw;
            notes.Add("One saved code was ignored because it was not 4 digits.");
            return string.Empty;
        }

        private static void Set(Dictionary<string, bool> dest, Dictionary<string, bool> source)
        {
            foreach (var pair in source) dest[pair.Key] = pair.Value;
        }

        public static Dictionary<string, bool> DefaultServerLocks()
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["doors"] = true, ["windows"] = false, ["boxes"] = true, ["storage"] = true, ["lockers"] = true,
                ["cupboards"] = true, ["vehicles"] = true, ["naval"] = true, ["medieval"] = false, ["furnaces"] = false,
                ["vending"] = false, ["composters"] = false, ["mixing"] = false, ["planters"] = false, ["turrets"] = false,
                ["sam"] = false, ["traps"] = false, ["racks"] = false, ["stashes"] = true, ["neon"] = false,
                ["signs"] = false, ["other"] = true, ["custom"] = true,
                ["electric"] = false, ["industrial"] = false, ["crafting"] = false,
                ["farming"] = false, ["fun"] = false, ["quarry"] = false
            };
        }

        public static Dictionary<string, bool> DefaultServerCloses()
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["door"] = false, ["double"] = false, ["window"] = false, ["garage"] = false,
                ["hatch"] = false, ["external"] = false, ["fence"] = false, ["legacy"] = false
            };
        }

        public static Dictionary<string, bool> DefaultPlayerLocks()
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["doors"] = true, ["windows"] = true, ["boxes"] = false, ["storage"] = false, ["lockers"] = true,
                ["cupboards"] = true, ["vehicles"] = true, ["naval"] = true, ["medieval"] = false, ["furnaces"] = false,
                ["vending"] = false, ["composters"] = false, ["mixing"] = false, ["planters"] = false, ["turrets"] = false,
                ["sam"] = false, ["traps"] = false, ["racks"] = false, ["stashes"] = false, ["neon"] = false,
                ["signs"] = false, ["other"] = false, ["custom"] = false,
                ["electric"] = false, ["industrial"] = false, ["crafting"] = false,
                ["farming"] = false, ["fun"] = false, ["quarry"] = false
            };
        }

        public static Dictionary<string, bool> DefaultPlayerCloses()
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["door"] = false, ["double"] = false, ["window"] = false, ["garage"] = false,
                ["hatch"] = false, ["external"] = false, ["fence"] = false, ["legacy"] = false
            };
        }

        public static List<EntityRule> DefaultVehicles()
        {
            return new List<EntityRule>
            {
                Rule("Minicopter", "assets/content/vehicles/minicopter/minicopter.entity.prefab", "vehicles"),
                Rule("Scrap Transport Helicopter", "assets/content/vehicles/scrap heli carrier/scraptransporthelicopter.prefab", "vehicles"),
                Rule("Attack Helicopter", "assets/content/vehicles/attackhelicopter/attackhelicopter.entity.prefab", "vehicles"),
                Rule("Hot Air Balloon", "assets/prefabs/deployable/hot air balloon/hotairballoon.prefab", "vehicles"),
                Rule("Horse", "assets/content/vehicles/horse/ridablehorse.prefab", "vehicles"),
                Rule("Tomaha Snowmobile", "assets/content/vehicles/snowmobiles/tomahasnowmobile.prefab", "vehicles"),
                Rule("Snowmobile", "assets/content/vehicles/snowmobiles/snowmobile.prefab", "vehicles"),
                Rule("Sedan", "assets/content/vehicles/sedan_a/sedantest.entity.prefab", "vehicles"),
                Rule("2 Module Car", "assets/content/vehicles/modularcar/2module_car_spawned.entity.prefab", "vehicles"),
                Rule("3 Module Car", "assets/content/vehicles/modularcar/3module_car_spawned.entity.prefab", "vehicles"),
                Rule("4 Module Car", "assets/content/vehicles/modularcar/4module_car_spawned.entity.prefab", "vehicles"),
                Rule("Motorbike", "assets/content/vehicles/bikes/motorbike.prefab", "vehicles"),
                Rule("Motorbike With Sidecar", "assets/content/vehicles/bikes/motorbike_sidecar.prefab", "vehicles"),
                Rule("Pedal Bike", "assets/content/vehicles/bikes/pedalbike.prefab", "vehicles"),
                Rule("Pedal Trike", "assets/content/vehicles/bikes/pedaltrike.prefab", "vehicles"),
                Rule("Kayak", "assets/content/vehicles/boats/kayak/kayak.prefab", "naval"),
                Rule("Row Boat", "assets/content/vehicles/boats/rowboat/rowboat.prefab", "naval"),
                Rule("RHIB", "assets/content/vehicles/boats/rhib/rhib.prefab", "naval"),
                Rule("Tugboat", "assets/content/vehicles/boats/tugboat/tugboat.prefab", "naval"),
                Rule("Submarine Solo", "assets/content/vehicles/submarine/submarinesolo.entity.prefab", "naval"),
                Rule("Submarine Duo", "assets/content/vehicles/submarine/submarineduo.entity.prefab", "naval")
            };
        }

        private static EntityRule Rule(string name, string prefab, string category)
        {
            return new EntityRule { Name = name, Prefab = prefab, Category = category, Enabled = true };
        }
    }

    internal static class JsonMini
    {
        public static object Parse(string json)
        {
            if (json == null) throw new FormatException("null");
            int index = 0;
            var value = Read(json, ref index);
            Skip(json, ref index);
            return value;
        }

        private static object Read(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) throw new FormatException("end");
            char c = s[i];
            if (c == '{') return ReadObject(s, ref i);
            if (c == '[') return ReadArray(s, ref i);
            if (c == '"') return ReadString(s, ref i);
            if (c == 't') { i += 4; return true; }
            if (c == 'f') { i += 5; return false; }
            if (c == 'n') { i += 4; return null; }
            return ReadNumber(s, ref i);
        }

        private static Dictionary<string, object> ReadObject(string s, ref int i)
        {
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            i++;
            Skip(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return map; }
            while (i < s.Length)
            {
                Skip(s, ref i);
                string key = ReadString(s, ref i);
                Skip(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("colon");
                i++;
                map[key] = Read(s, ref i);
                Skip(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; break; }
                throw new FormatException("object");
            }
            return map;
        }

        private static List<object> ReadArray(string s, ref int i)
        {
            var list = new List<object>();
            i++;
            Skip(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (i < s.Length)
            {
                list.Add(Read(s, ref i));
                Skip(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; break; }
                throw new FormatException("array");
            }
            return list;
        }

        private static string ReadString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("string");
            i++;
            var chars = new System.Text.StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return chars.ToString();
                if (c == '\\' && i < s.Length)
                {
                    char n = s[i++];
                    if (n == 'n') chars.Append('\n');
                    else if (n == 'r') chars.Append('\r');
                    else if (n == 't') chars.Append('\t');
                    else chars.Append(n);
                }
                else chars.Append(c);
            }
            throw new FormatException("string end");
        }

        private static object ReadNumber(string s, ref int i)
        {
            int start = i;
            if (s[i] == '-') i++;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            long number;
            if (!long.TryParse(s.Substring(start, i - start), out number)) throw new FormatException("number");
            return number;
        }

        private static void Skip(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }
    }
}
