using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("NightshadeHud", "Nightshade", "1.3.4")]
    [Description("Top-right HUD: clock, PvP countdown, PvP shield, and teleport prices.")]
    public class NightshadeHud : RustPlugin
    {
        private const string Root = "NightshadeHud";
        private const string Bg = "NightshadeHud.Bg";
        private const string Icon = "NightshadeHud.Icon";
        private const string IconHit = "NightshadeHud.IconHit";
        private const string IconDrag = "NightshadeHud.IconDrag";
        private const string Half = "NightshadeHud.Half";
        private const string BorderName = "NightshadeHud.Border";
        private const string CountdownName = "NightshadeHud.Countdown";
        private const string StatusSepName = "NightshadeHud.StatusSep";
        private const string ShieldName = "NightshadeHud.ShieldQueue";
        private const string DataPath = "Nightshade/NightshadeHud/State";
        private const string ArtDir = "Nightshade/NightshadeHud";
        private const string DefaultBorderFile = "Nightshade/NightshadeHud/hud_border.png";
        private const string DefaultBackgroundFile = "Nightshade/NightshadeHud/hud_bg.png";
        private const string DefaultLogoFile = "logo.png";
        private const string DefaultPurgeLogoFile = "purge_logo.png";
        private const string ConfigVersion = "1.3.4";
        private const float DefaultScale = 0.75f;
        private const float DefaultLogoScale = 1.5f;
        private const float StockLogoScale = 1.3f;
        private const float StockHalfScale = 0.5f;
        // Design space is the 1.3.0 box (268x120) with the rows pulled together.
        // Scale 0.75 draws that box at about 201x90. Logo scale multiplies the icon on top of Scale.
        private const float DesignWidth = 268f;
        private const float DesignIcon = 32f;
        private const float DesignPad = 8f;
        private const float DesignNameGap = 6f;
        private const float DesignNameTop = 6f;
        private const float DesignNameH = 20f;
        private const float DesignOnlineTop = 26f;
        private const float DesignOnlineH = 18f;
        private const float DesignStatusH = 18f;
        private const float DesignOutL = 8f;
        private const float DesignOutR = 8f;
        private const float DesignOutT = 12f;
        private const float DesignOutB = 8f;
        private const string BadgeBgName = "NightshadeHud.BadgeBg";
        private const string BadgeName = "NightshadeHud.Badge";
        private const string LogoImageKey = "NightshadeHud.Logo";
        private const string PurgeImageKey = "NightshadeHud.PurgeLogo";
        private const string BorderKey = "NightshadeHud.Border";
        private const string BackgroundKey = "NightshadeHud.Background";
        private const int ImageMaxBytes = 3145728;
        private const int ImageTries = 4;

        [PluginReference] private Plugin NightshadePVPWeekend;

        // plugins.Find avoids a missing-plugin warning after the rename. The old name still answers if it is loaded.
        private Plugin WeekendPlugin()
        {
            if (NightshadePVPWeekend != null && NightshadePVPWeekend.IsLoaded) return NightshadePVPWeekend;
            Plugin legacy = plugins.Find("NightshadeRaidProtection");
            if (legacy != null && legacy.IsLoaded) return legacy;
            return null;
        }

        [PluginReference("Nightshade Purge Status")] private Plugin PurgeStatus;
        [PluginReference] private Plugin ImageLibrary;

        private Settings config;
        private Stored stored;
        private TimeZoneInfo zone;
        private bool zoneFallback;
        private bool purge;
        private Timer clockTimer;
        private Timer artTimer;
        private Coroutine startupRoutine;

        private readonly Dictionary<ulong, string> mode = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, float> dragX = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, Timer> dragCommit = new Dictionary<ulong, Timer>();
        private readonly Dictionary<ulong, float> ignoreCycleUntil = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, string> shown = new Dictionary<ulong, string>();
        private readonly HashSet<ulong> station = new HashSet<ulong>();
        private readonly Dictionary<ulong, string> shieldCache = new Dictionary<ulong, string>();
        private string badgeCache = "\0";
        private string clockCache = "\0";
        private string onlineCache = "\0";
        private string countdownCache = "\0";
        private Art logoArt;
        private Art purgeArt;
        private Art borderArt;
        private Art backgroundArt;

        private class Art
        {
            public string Key;
            public string Label;
            public string Path;
            public int Tries;
            public bool Queued;
            public bool Waiting;
            public bool GaveUp;
            public bool Warned;
            public string Shown;
        }

        private float ScaleOf
        {
            get
            {
                if (config == null || float.IsNaN(config.Scale) || float.IsInfinity(config.Scale)) return DefaultScale;
                return Mathf.Clamp(config.Scale, 0.35f, 1.25f);
            }
        }

        private float D(float design) => design * ScaleOf;

        // Font sizes follow Scale, but stay large enough to read at the default half size.
        private static int F(float design, float scale, int min)
        {
            int n = Mathf.RoundToInt(design * scale);
            return n < min ? min : n;
        }

        private float LogoScaleOf
        {
            get
            {
                if (config == null || float.IsNaN(config.LogoScale) || float.IsInfinity(config.LogoScale)) return DefaultLogoScale;
                return Mathf.Clamp(config.LogoScale, 0.5f, 2f);
            }
        }

        // Drawn logo. Icon size in the config is the Scale-derived base; Logo scale multiplies it.
        private float LogoSize => Mathf.Max(8f, config.IconSize * LogoScaleOf);

        private float Pad => D(DesignPad);
        private float NameGap => D(DesignNameGap);
        private float NameTop => D(DesignNameTop);
        private float NameH => D(DesignNameH);
        private float OnlineTop => D(DesignOnlineTop);
        private float OnlineH => D(DesignOnlineH);
        private float StatusH => D(DesignStatusH);
        // Keep the status line under the badge when the logo is enlarged.
        private float StatusTop
        {
            get
            {
                float belowBadge = Pad + LogoSize + BadgeHang() + D(2f);
                float belowOnline = OnlineTop + OnlineH;
                return Mathf.Max(belowBadge, belowOnline);
            }
        }
        private float PanelH => Mathf.Ceil(StatusTop + StatusH + Pad);
        private int NameFont => F(14f, ScaleOf, 8);
        private int TagFont => F(11f, ScaleOf, 7);
        private int ClockFont => F(12f, ScaleOf, 7);
        private int OnlineFont => F(11f, ScaleOf, 7);
        private int StatusFont => F(12f, ScaleOf, 7);
        private int ShieldFont => F(11f, ScaleOf, 7);
        private int BadgeFont => F(8f, ScaleOf, 6);

        private class Settings
        {
            [JsonProperty("Config version")] public string Version = ConfigVersion;
            [JsonProperty("Scale")] public float Scale = DefaultScale;
            [JsonProperty("Logo scale")] public float LogoScale = DefaultLogoScale;
            [JsonProperty("Chat command")] public string Command = "hud";
            [JsonProperty("Default state for new players (minimized, half, full)")] public string DefaultState = "full";
            [JsonProperty("Layer (Overlay shows in the inventory)")] public string Layer = "Overlay";
            [JsonProperty("Anchor")] public string Anchor = "1 1";
            [JsonProperty("Left anchor (under the missions bar)")] public string LeftAnchor = "0.008 0.905";
            [JsonProperty("Inset from the right (px)")] public float Right = 16f;
            [JsonProperty("Inset from the top (px)")] public float Top = 12f;
            [JsonProperty("Width (px)")] public float Width = 201f;
            [JsonProperty("Height (px)")] public float Height = 66f;
            [JsonProperty("Icon size (px)")] public float IconSize = 24f;
            [JsonProperty("Hide while using a computer station")] public bool HideInComputerStation = true;
            [JsonProperty("Logo image file")] public string LogoImage = "logo.png";
            [JsonProperty("Purge logo image file")] public string PurgeLogoImage = "purge_logo.png";
            [JsonProperty("Server name")] public string ServerName = "Nightshade";
            [JsonProperty("Tag text")] public string TagText = "Solo/Duo";
            [JsonProperty("Tag color")] public string TagColor = "#9A9692";
            [JsonProperty("Use 24-hour clock")] public bool Use24Hour = false;
            [JsonProperty("Time zone id")] public string TimeZoneId = "America/New_York";
            [JsonProperty("Wipe day of week")] public string WipeDay = "Thursday";
            [JsonProperty("Wipe time (24h HH:mm)")] public string WipeTime = "14:00";
            [JsonProperty("Wipe time zone id")] public string WipeTimeZoneId = "America/New_York";
            [JsonProperty("Show ET label")] public bool ShowZoneLabel = true;
            [JsonProperty("Show online count")] public bool ShowOnline = true;
            [JsonProperty("Show sleepers")] public bool ShowSleepers = true;
            [JsonProperty("Count admins in online")] public bool CountAdmins = false;
            [JsonProperty("Show PvP countdown")] public bool ShowCountdown = true;
            [JsonProperty("Show shield line")] public bool ShowShield = true;
            [JsonProperty("Show border")] public bool ShowBorder = true;
            [JsonProperty("Border image file")] public string BorderImage = DefaultBorderFile;
            [JsonProperty("Background image file")] public string BackgroundImage = DefaultBackgroundFile;
            [JsonProperty("Border outset left (px)")] public float BorderOutsetLeft = 6f;
            [JsonProperty("Border outset right (px)")] public float BorderOutsetRight = 6f;
            [JsonProperty("Border outset top (px)")] public float BorderOutsetTop = 9f;
            [JsonProperty("Border outset bottom (px)")] public float BorderOutsetBottom = 6f;
            [JsonProperty("Panel color")] public string PanelColor = "#0E0E12";
            [JsonProperty("Panel opacity (0-100)")] public float PanelOpacity = 78f;
            [JsonProperty("Name color")] public string NameColor = "#A267D3";
            [JsonProperty("Clock color")] public string ClockColor = "#E7E2F2";
            [JsonProperty("Muted text color")] public string MutedColor = "#8A8A8A";
            [JsonProperty("PvE badge text")] public string BadgePveText = "PVE";
            [JsonProperty("PvP badge text")] public string BadgePvpText = "PVP";
            [JsonProperty("PvE badge color")] public string BadgePveColor = "#3DDC84";
            [JsonProperty("PvP badge color")] public string BadgePvpColor = "#FF4D4D";
            [JsonProperty("PvE badge background")] public string BadgePveBg = "#143528";
            [JsonProperty("PvP badge background")] public string BadgePvpBg = "#3A1212";
            [JsonProperty("Countdown color while PvP is active")] public string CountdownActive = "#FF4D4D";
            [JsonProperty("Countdown color while PvP is waiting")] public string CountdownIdle = "#FF4D4D";
            [JsonProperty("Shield active color")] public string ShieldActive = "#69D8A0";
            [JsonProperty("Shield queued color")] public string ShieldQueued = "#E2B968";
        }

        private class Stored
        {
            public Dictionary<string, string> Players = new Dictionary<string, string>(StringComparer.Ordinal);
            public Dictionary<string, string> Sides = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        protected override void LoadDefaultConfig()
        {
            config = new Settings();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            JObject raw = null;
            try { raw = Config.ReadObject<JObject>(); }
            catch { raw = null; }
            try { config = Config.ReadObject<Settings>() ?? new Settings(); }
            catch (Exception ex)
            {
                PrintWarning("Invalid config, using defaults: " + ex.Message);
                config = new Settings();
                raw = null;
            }
            bool migrate = IsOlderConfig(raw);
            if (migrate) MigrateOlderConfig(raw);
            RepairImages();
            FillMissingText();
            ApplyScale();
            config.Version = ConfigVersion;
            SaveConfig();
        }

        private static bool IsOlderConfig(JObject raw)
        {
            if (raw == null) return true;
            JToken token = raw["Config version"];
            if (token == null || token.Type == JTokenType.Null) return true;
            string text = token.ToString().Trim();
            if (text.Length == 0) return true;
            int dots = 0;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '.') dots++;
            while (dots < 2)
            {
                text += ".0";
                dots++;
            }
            System.Version have;
            System.Version need;
            // System.Version, not the plugin instance Version from BaseHookable.
            if (!System.Version.TryParse(text, out have)) return true;
            if (!System.Version.TryParse(ConfigVersion, out need)) return true;
            return have < need;
        }

        private void MigrateOlderConfig(JObject raw)
        {
            bool hasScale = raw != null && raw["Scale"] != null && raw["Scale"].Type != JTokenType.Null;
            if (!hasScale)
            {
                float oldWidth = DesignWidth;
                JToken widthToken = raw == null ? null : raw["Width (px)"];
                float parsed;
                if (widthToken != null && widthToken.Type != JTokenType.Null
                    && float.TryParse(widthToken.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    oldWidth = parsed;
                bool stock = Mathf.Abs(oldWidth - 252f) < 0.6f || Mathf.Abs(oldWidth - DesignWidth) < 0.6f;
                config.Scale = stock ? DefaultScale : Mathf.Clamp(oldWidth / DesignWidth, 0.35f, 1.25f);
            }
            else
            {
                float stored;
                if (float.TryParse(raw["Scale"].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out stored)
                    && Mathf.Abs(stored - StockHalfScale) < 0.001f)
                    config.Scale = DefaultScale;
            }
            bool hasLogo = raw != null && raw["Logo scale"] != null && raw["Logo scale"].Type != JTokenType.Null;
            if (!hasLogo)
                config.LogoScale = DefaultLogoScale;
            else
            {
                float storedLogo;
                if (float.TryParse(raw["Logo scale"].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out storedLogo)
                    && Mathf.Abs(storedLogo - StockLogoScale) < 0.001f)
                    config.LogoScale = DefaultLogoScale;
            }
            // 58 cleared the old top-right skill panel. 12 is the 1.3 corner inset.
            if (Mathf.Abs(config.Top - 58f) < 0.1f) config.Top = 12f;
            if (string.IsNullOrEmpty(config.NameColor) || string.Equals(config.NameColor, "#F4F1EA", StringComparison.OrdinalIgnoreCase))
                config.NameColor = "#A267D3";
            if (string.IsNullOrEmpty(config.TagColor) || string.Equals(config.TagColor, "#C4A6E0", StringComparison.OrdinalIgnoreCase))
                config.TagColor = "#9A9692";
            if (string.Equals(config.CountdownIdle, "#C8C8C8", StringComparison.OrdinalIgnoreCase))
                config.CountdownIdle = "#FF4D4D";
        }

        private void RepairImages()
        {
            config.LogoImage = CleanArtName(config.LogoImage, true, DefaultLogoFile);
            config.PurgeLogoImage = CleanArtName(config.PurgeLogoImage, true, DefaultPurgeLogoFile);
            config.BorderImage = CleanArtName(config.BorderImage, false, DefaultBorderFile);
            config.BackgroundImage = CleanArtName(config.BackgroundImage, false, DefaultBackgroundFile);
        }

        private static string CleanArtName(string name, bool filenameOnly, string fallback)
        {
            if (!SafeArtName(name, filenameOnly)) return fallback;
            return name.Trim();
        }

        private void FillMissingText()
        {
            if (config.TagText == null) config.TagText = "Solo/Duo";
            if (string.IsNullOrEmpty(config.LeftAnchor)) config.LeftAnchor = "0.008 0.905";
            if (string.IsNullOrEmpty(config.Anchor)) config.Anchor = "1 1";
            if (string.IsNullOrEmpty(config.CountdownActive)) config.CountdownActive = "#FF4D4D";
            if (string.IsNullOrEmpty(config.CountdownIdle)) config.CountdownIdle = "#FF4D4D";
            if (string.IsNullOrEmpty(config.ShieldActive)) config.ShieldActive = "#69D8A0";
            if (string.IsNullOrEmpty(config.ShieldQueued)) config.ShieldQueued = "#E2B968";
            if (string.IsNullOrEmpty(config.Command)) config.Command = "hud";
            if (string.IsNullOrEmpty(config.Layer)) config.Layer = "Overlay";
            if (string.IsNullOrEmpty(config.WipeDay)) config.WipeDay = "Thursday";
            if (string.IsNullOrEmpty(config.WipeTime)) config.WipeTime = "14:00";
            if (string.IsNullOrEmpty(config.WipeTimeZoneId)) config.WipeTimeZoneId = "America/New_York";
            if (float.IsNaN(config.LogoScale) || float.IsInfinity(config.LogoScale) || config.LogoScale <= 0f)
                config.LogoScale = DefaultLogoScale;
            config.LogoScale = Mathf.Clamp(config.LogoScale, 0.5f, 2f);
        }

        private void ApplyScale()
        {
            if (float.IsNaN(config.Scale) || float.IsInfinity(config.Scale) || config.Scale < 0.35f || config.Scale > 1.25f)
                config.Scale = DefaultScale;
            config.Width = Mathf.Round(DesignWidth * config.Scale);
            config.IconSize = Mathf.Round(DesignIcon * config.Scale);
            config.Height = PanelH;
            config.BorderOutsetLeft = Mathf.Round(DesignOutL * config.Scale);
            config.BorderOutsetRight = Mathf.Round(DesignOutR * config.Scale);
            config.BorderOutsetTop = Mathf.Round(DesignOutT * config.Scale);
            config.BorderOutsetBottom = Mathf.Round(DesignOutB * config.Scale);
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        private void Init()
        {
            cmd.AddChatCommand(string.IsNullOrEmpty(config.Command) ? "hud" : config.Command, this, nameof(CmdHud));
            cmd.AddConsoleCommand("nightshadehud.ui", this, nameof(CmdUi));
            try { stored = Interface.Oxide.DataFileSystem.ReadObject<Stored>(DataPath) ?? new Stored(); }
            catch { stored = new Stored(); }
            if (stored.Players == null) stored.Players = new Dictionary<string, string>(StringComparer.Ordinal);
            if (stored.Sides == null) stored.Sides = new Dictionary<string, string>(StringComparer.Ordinal);
            zone = ResolveZone(config.TimeZoneId, out zoneFallback);
            logoArt = new Art { Key = LogoImageKey, Label = "HUD logo" };
            purgeArt = new Art { Key = PurgeImageKey, Label = "purge logo" };
            borderArt = new Art { Key = BorderKey, Label = "HUD border" };
            backgroundArt = new Art { Key = BackgroundKey, Label = "HUD background" };
            EnsureArtPaths();
        }

        private void OnServerInitialized()
        {
            timer.Once(1f, QueueImages);
            if (zoneFallback)
                PrintWarning("Time zone '" + config.TimeZoneId + "' was not found. Using " + zone.Id + ".");
            if (ServerMgr.Instance != null)
                startupRoutine = ServerMgr.Instance.StartCoroutine(BootHud());
            else
                timer.Once(1f, () => { if (ServerMgr.Instance != null) startupRoutine = ServerMgr.Instance.StartCoroutine(BootHud()); });
        }

        private void Unload()
        {
            clockTimer?.Destroy();
            artTimer?.Destroy();
            foreach (var pending in dragCommit.Values)
                if (pending != null) pending.Destroy();
            dragCommit.Clear();
            if (ServerMgr.Instance != null && startupRoutine != null)
                ServerMgr.Instance.StopCoroutine(startupRoutine);
            startupRoutine = null;
            SaveData();
            foreach (var player in BasePlayer.activePlayerList)
                if (player != null) CuiHelper.DestroyUi(player, Root);
        }

        private void OnServerSave() => SaveData();

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(DataPath, stored);

        #region Player UI

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            timer.Once(1f, () => { if (player != null && player.IsConnected) Show(player); });
            MarkPop();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            shown.Remove(player.userID);
            station.Remove(player.userID);
            shieldCache.Remove(player.userID);
            CancelDrag(player.userID);
            ignoreCycleUntil.Remove(player.userID);
            MarkPop();
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null) return;
            timer.Once(1f, () => { if (player != null && player.IsConnected) Show(player); });
        }

        private void OnEntityMounted(ComputerStation entity, BasePlayer player)
        {
            if (!config.HideInComputerStation || player == null) return;
            station.Add(player.userID);
            CuiHelper.DestroyUi(player, Root);
            shown.Remove(player.userID);
        }

        private void OnEntityDismounted(ComputerStation entity, BasePlayer player)
        {
            if (player == null || !station.Remove(player.userID)) return;
            Show(player);
        }

        private void OnPlayerGainedBuildingPrivilege(BasePlayer player) => RefreshShield(player);
        private void OnPlayerLostBuildingPrivilege(BasePlayer player) => RefreshShield(player);

        // Chat args are string[]. ConsoleSystem.Arg.Args is Facepunch.StringView[] on this host.
        private void CmdHud(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (args != null && args.Length > 0 && args[0].Equals("debug", StringComparison.OrdinalIgnoreCase))
            {
                if (!player.IsAdmin) { player.ChatMessage("<color=#b084ff>[Nightshade]</color> HUD debug is admin only."); return; }
                DebugHud(player);
                return;
            }
            string requested = args == null || args.Length == 0 ? "cycle" : Normalize(args[0]);
            if (requested == "left" || requested == "right" || requested == "side")
            {
                SetSide(player, requested);
                return;
            }
            if (requested == "cycle")
            {
                float until;
                if (ignoreCycleUntil.TryGetValue(player.userID, out until) && Time.realtimeSinceStartup < until)
                    return;
            }
            if (requested == null)
            {
                player.ChatMessage("<color=#b084ff>[Nightshade]</color> /" + config.Command + " min | half | full | cycle | left | right | side");
                return;
            }
            SetState(player, requested == "cycle" ? Cycle(StateOf(player)) : requested);
        }

        // Chat args are string[]. ConsoleSystem.Arg.Args is Facepunch.StringView[]; read them with GetString.
        private void CmdUi(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            SetState(player, Cycle(StateOf(player)));
        }

        private void SetState(BasePlayer player, string next)
        {
            string id = player.UserIDString;
            mode[player.userID] = next;
            stored.Players[id] = next;
            SaveData();
            if (station.Contains(player.userID)) return;
            string visible;
            if (!shown.TryGetValue(player.userID, out visible)) Show(player);
            else if (visible != next) Transition(player, visible, next);
        }

        private string StateOf(BasePlayer player)
        {
            string value;
            if (mode.TryGetValue(player.userID, out value)) return value;
            if (stored.Players.TryGetValue(player.UserIDString, out value))
            {
                value = Normalize(value) ?? Normalize(config.DefaultState) ?? "full";
                mode[player.userID] = value;
                return value;
            }
            value = Normalize(config.DefaultState) ?? "full";
            mode[player.userID] = value;
            return value;
        }

        private static string Cycle(string state)
        {
            if (state == "minimized") return "half";
            if (state == "half") return "full";
            return "minimized";
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            switch (value.Trim().ToLowerInvariant())
            {
                case "min":
                case "minimized":
                case "icon":
                case "1": return "minimized";
                case "half":
                case "top":
                case "2": return "half";
                case "full":
                case "all":
                case "3": return "full";
                case "cycle": return "cycle";
                case "left": return "left";
                case "right": return "right";
                case "side": return "side";
                default: return null;
            }
        }

        private static bool IsOpen(string state)
        {
            return state != "minimized";
        }

        private void Show(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            CuiHelper.DestroyUi(player, "UI_Hud");
            CuiHelper.DestroyUi(player, Root);
            shown.Remove(player.userID);
            if (station.Contains(player.userID) || (config.HideInComputerStation && player.GetMounted() is ComputerStation))
            {
                station.Add(player.userID);
                return;
            }
            string state = StateOf(player);
            var container = new CuiElementContainer();
            AddRoot(container, player, state);
            if (IsOpen(state))
            {
                AddBackground(container);
                AddBorder(container);
                AddBody(container, player);
            }
            AddIconGroup(container, state);
            CuiHelper.AddUi(player, container);
            shown[player.userID] = state;
            if (IsOpen(state)) shieldCache[player.userID] = ShieldSignature(player);
        }

        private void Transition(BasePlayer player, string from, string to)
        {
            if (from == to) return;
            Resize(player, to);
            bool fromOpen = IsOpen(from);
            bool toOpen = IsOpen(to);
            if (!fromOpen && toOpen)
            {
                var add = new CuiElementContainer();
                AddBackground(add);
                AddBorder(add);
                AddBody(add, player);
                CuiHelper.AddUi(player, add);
            }
            else if (fromOpen && !toOpen)
            {
                CuiHelper.DestroyUi(player, Bg);
                CuiHelper.DestroyUi(player, BorderName);
                CuiHelper.DestroyUi(player, Half);
                CuiHelper.DestroyUi(player, CountdownName);
                CuiHelper.DestroyUi(player, StatusSepName);
                CuiHelper.DestroyUi(player, ShieldName);
            }
            if (!fromOpen)
            {
                var icon = new CuiElementContainer();
                AddIconGroup(icon, to);
                CuiHelper.AddUi(player, icon);
            }
            else
                MoveIconGroup(player, to);
            shown[player.userID] = to;
            if (toOpen) shieldCache[player.userID] = ShieldSignature(player);
        }

        private void AddRoot(CuiElementContainer container, BasePlayer player, string state)
        {
            string anchor = AnchorOf(player);
            container.Add(new CuiElement
            {
                Name = Root,
                Parent = string.IsNullOrEmpty(config.Layer) ? "Overlay" : config.Layer,
                DestroyUi = Root,
                Components =
                {
                    new CuiImageComponent { Color = "0 0 0 0" },
                    new CuiRectTransformComponent { AnchorMin = anchor, AnchorMax = anchor, OffsetMin = OffsetMin(player, state), OffsetMax = OffsetMax(player, state) }
                }
            });
        }

        private void AddBackground(CuiElementContainer container)
        {
            string png = ArtPng(backgroundArt);
            if (!string.IsNullOrEmpty(png))
            {
                container.Add(new CuiElement
                {
                    Name = Bg,
                    Parent = Root,
                    Components =
                    {
                        new CuiRawImageComponent
                        {
                            Png = png,
                            Color = "1 1 1 1",
                            Sprite = "assets/content/textures/generic/fulltransparent.tga"
                        },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                    }
                });
                return;
            }
            container.Add(new CuiElement
            {
                Name = Bg,
                Parent = Root,
                Components =
                {
                    new CuiImageComponent { Color = Hex(config.PanelColor, config.PanelOpacity / 100f) },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                }
            });
        }

        private void MoveIconGroup(BasePlayer player, string state)
        {
            // Recreate the group. An update that includes the draggable component is treated by the
            // client as moveToAnchor/rebuildAnchor and destroys the anchor while the element is live.
            var container = new CuiElementContainer();
            AddIconGroup(container, state);
            CuiHelper.AddUi(player, container);
        }

        private void AddIconGroup(CuiElementContainer container, string state)
        {
            float hitLeft, hitBottom, hitRight, hitTop;
            HitRect(state, out hitLeft, out hitBottom, out hitRight, out hitTop);
            container.Add(new CuiElement
            {
                Name = IconDrag,
                Parent = Root,
                DestroyUi = IconDrag,
                Components =
                {
                    new CuiImageComponent { Color = "0 0 0 0" },
                    new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = Px(hitLeft, hitBottom), OffsetMax = Px(hitRight, hitTop) },
                    DragComponent()
                }
            });
            AddIcon(container, state, hitLeft, hitTop);
            AddBadge(container, state, hitLeft, hitTop);
            AddIconButton(container, hitLeft, hitTop, hitRight, hitBottom);
        }

        private void AddIcon(CuiElementContainer container, string state, float originLeft, float originTop)
        {
            float left, top;
            IconOrigin(state, out left, out top);
            float x = left - originLeft;
            float y = top - originTop;
            string logo = LogoPng();
            if (string.IsNullOrEmpty(logo)) return;
            container.Add(new CuiElement
            {
                Name = Icon,
                Parent = IconDrag,
                DestroyUi = Icon,
                Components =
                {
                    new CuiRawImageComponent
                    {
                        Png = logo,
                        Color = "1 1 1 1",
                        Sprite = "assets/content/textures/generic/fulltransparent.tga"
                    },
                    new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = Px(x, y - LogoSize), OffsetMax = Px(x + LogoSize, y) }
                }
            });
        }

        private string LogoPng()
        {
            if (PurgeActive())
            {
                string purgePng = ArtPng(purgeArt);
                if (!string.IsNullOrEmpty(purgePng)) return purgePng;
            }
            return ArtPng(logoArt);
        }

        private void AddBadge(CuiElementContainer container, string state, float originLeft, float originTop)
        {
            bool pvp = CombatPvp();
            float left, bottom, right, top;
            BadgeRect(state, out left, out bottom, out right, out top);
            string text = pvp ? BadgeWord(config.BadgePvpText, "PVP") : BadgeWord(config.BadgePveText, "PVE");
            container.Add(new CuiElement
            {
                Name = BadgeBgName,
                Parent = IconDrag,
                DestroyUi = BadgeBgName,
                Components =
                {
                    new CuiImageComponent { Color = Hex(pvp ? config.BadgePvpBg : config.BadgePveBg) },
                    new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = Px(left - originLeft, bottom - originTop), OffsetMax = Px(right - originLeft, top - originTop) }
                }
            });
            container.Add(new CuiElement
            {
                Name = BadgeName,
                Parent = BadgeBgName,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = text,
                        FontSize = BadgeFont,
                        Align = TextAnchor.MiddleCenter,
                        Color = Hex(pvp ? config.BadgePvpColor : config.BadgePveColor),
                        Font = "robotocondensed-bold.ttf"
                    },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "0 0", OffsetMax = "0 0" }
                }
            });
            badgeCache = pvp ? "1" : "0";
        }

        private void AddIconButton(CuiElementContainer container, float originLeft, float originTop, float hitRight, float hitBottom)
        {
            container.Add(new CuiElement
            {
                Name = IconHit,
                Parent = IconDrag,
                DestroyUi = IconHit,
                Components =
                {
                    new CuiButtonComponent { Command = IconCommand(), Color = "0 0 0 0.02" },
                    new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = Px(0f, hitBottom - originTop), OffsetMax = Px(hitRight - originLeft, 0f) }
                }
            });
        }

        // keepOnTop stays false so OnEndDrag parents the logo back under the HUD before the rebuild.
        // The client fires DragRPC once, on release, and DropRPC only when two draggables swap.
        private static CuiDraggableComponent DragComponent()
        {
            return new CuiDraggableComponent
            {
                LimitToParent = false,
                MaxDistance = -1f,
                AllowSwapping = false,
                DropAnywhere = true,
                DragAlpha = 0.9f,
                ParentLimitIndex = 1,
                ParentPadding = "0 0",
                AnchorOffset = "0 0",
                KeepOnTop = false,
                PositionRPC = CommunityEntity.DraggablePositionSendType.NormalizedScreen,
                MoveToAnchor = false,
                RebuildAnchor = false,
                Enabled = true
            };
        }

        private static string BadgeWord(string text, string fallback)
        {
            return string.IsNullOrEmpty(text) ? fallback : text;
        }

        private void IconOrigin(string state, out float left, out float top)
        {
            if (state == "minimized")
            {
                left = 0f;
                top = 0f;
                return;
            }
            left = Pad;
            top = -Pad;
        }

        private void BadgeMetrics(out float width, out float height, out float hang)
        {
            float text = Mathf.Max(TextWidth(BadgeWord(config.BadgePveText, "PVE"), BadgeFont, true), TextWidth(BadgeWord(config.BadgePvpText, "PVP"), BadgeFont, true));
            width = text + 6f;
            height = BadgeFont + 2f;
            hang = height / 3f;
        }

        private void BadgeRect(string state, out float left, out float bottom, out float right, out float top)
        {
            float iconLeft, iconTop, width, height, hang;
            IconOrigin(state, out iconLeft, out iconTop);
            BadgeMetrics(out width, out height, out hang);
            float center = iconLeft + LogoSize * 0.5f;
            left = center - width * 0.5f;
            right = left + width;
            bottom = iconTop - LogoSize - hang;
            top = bottom + height;
        }

        private float BadgeHang()
        {
            float width, height, hang;
            BadgeMetrics(out width, out height, out hang);
            return hang;
        }

        private void HitRect(string state, out float left, out float bottom, out float right, out float top)
        {
            float iconLeft, iconTop, badgeLeft, badgeBottom, badgeRight, badgeTop;
            IconOrigin(state, out iconLeft, out iconTop);
            BadgeRect(state, out badgeLeft, out badgeBottom, out badgeRight, out badgeTop);
            left = Mathf.Min(iconLeft, badgeLeft);
            right = Mathf.Max(iconLeft + LogoSize, badgeRight);
            top = iconTop;
            bottom = Mathf.Min(iconTop - LogoSize, badgeBottom);
        }

        private string IconCommand()
        {
            string command = string.IsNullOrEmpty(config.Command) ? "hud" : config.Command;
            return "chat.say /" + command + " cycle";
        }

        private float NameX()
        {
            return Pad + LogoSize + NameGap;
        }

        private void AddBody(CuiElementContainer container, BasePlayer player)
        {
            string parent = BodyParent(container);
            float nameX = NameX();
            float edge = config.Width - Pad;
            string name = string.IsNullOrEmpty(config.ServerName) ? "Nightshade" : config.ServerName;
            float nameInk = TextWidth(name, NameFont, true);
            string clock = ClockText();
            float clockInk = TextWidth(clock, ClockFont, true) + 2f;
            float clockLeft = edge - clockInk;
            float nameRight = Mathf.Min(nameX + nameInk + 1f, clockLeft - 2f);
            AddLabel(container, "NightshadeHud.Name", parent, name, Hex(config.NameColor), NameFont, TextAnchor.MiddleLeft, "0 1", "0 1", Px(nameX, -(NameTop + NameH)), Px(nameRight, -NameTop), true);
            float tagX = nameX + nameInk + NameGap;
            if (!string.IsNullOrEmpty(config.TagText) && tagX < clockLeft - 8f)
                AddLabel(container, "NightshadeHud.Tag", parent, config.TagText, Hex(config.TagColor), TagFont, TextAnchor.MiddleLeft, "0 1", "0 1", Px(tagX, -(NameTop + NameH)), Px(clockLeft - 2f, -NameTop));
            AddLabel(container, "NightshadeHud.Clock", parent, clock, Hex(config.ClockColor), ClockFont, TextAnchor.MiddleRight, "0 1", "0 1", Px(clockLeft, -(NameTop + NameH)), Px(edge, -NameTop), true);
            AddLabel(container, "NightshadeHud.Online", parent, OnlineText(), Hex(config.MutedColor, 0.95f), OnlineFont, TextAnchor.MiddleLeft, "0 1", "0 1", Px(nameX, -(OnlineTop + OnlineH)), Px(edge, -OnlineTop));
            AddStatusRow(container, parent, player);
            clockCache = clock;
            onlineCache = OnlineText();
        }

        private void AddStatusRow(CuiElementContainer container, string parent, BasePlayer player)
        {
            float x = NameX();
            float edge = config.Width - Pad;
            float top = -StatusTop;
            float bottom = -(StatusTop + StatusH);
            string countText, countColor;
            Countdown(out countText, out countColor);
            float countW = TextWidth(countText, StatusFont, true) + 2f;
            float room = edge - x;
            if (countW > room) countW = room;
            if (!string.IsNullOrEmpty(countText) && countW >= 8f)
            {
                AddLabel(container, CountdownName, parent, countText, countColor, StatusFont, TextAnchor.MiddleLeft, "0 1", "0 1", Px(x, bottom), Px(x + countW, top), true);
                x += TextWidth(countText, StatusFont, true);
            }
            string shieldText = string.Empty;
            string shieldColor = Hex(config.ShieldActive);
            if (player != null)
                RaidLine(player, out shieldText, out shieldColor);
            bool shield = !string.IsNullOrEmpty(shieldText) && x < edge - 12f;
            if (shield)
            {
                const string sep = " \u00B7 ";
                float sepW = TextWidth(sep, StatusFont, true);
                if (x + sepW + 8f < edge)
                {
                    AddLabel(container, StatusSepName, parent, sep, Hex(config.MutedColor), StatusFont, TextAnchor.MiddleLeft, "0 1", "0 1", Px(x, bottom), Px(x + sepW + 1f, top), true);
                    x += sepW;
                    float shieldRoom = Mathf.Max(8f, edge - x);
                    shieldText = FitWidth(shieldText, ShieldFont, true, shieldRoom);
                    float shieldW = Mathf.Min(shieldRoom, TextWidth(shieldText, ShieldFont, true) + 2f);
                    if (!string.IsNullOrEmpty(shieldText) && shieldW >= 8f)
                        AddLabel(container, ShieldName, parent, shieldText, shieldColor, ShieldFont, TextAnchor.MiddleLeft, "0 1", "0 1", Px(x, bottom), Px(x + shieldW, top), true);
                }
            }
            if (player != null) shieldCache[player.userID] = countText + "|" + countColor + "|" + ShieldSignature(player);
            countdownCache = countText + "|" + countColor;
        }

        private string BodyParent(CuiElementContainer container)
        {
            container.Add(new CuiElement
            {
                Name = Half,
                Parent = Root,
                Components =
                {
                    new CuiImageComponent { Color = "0 0 0 0" },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                }
            });
            return Half;
        }

        private void Resize(BasePlayer player, string state)
        {
            var container = new CuiElementContainer();
            container.Add(new CuiElement
            {
                Name = Root,
                Update = true,
                Components =
                {
                    new CuiImageComponent { Color = "0 0 0 0" },
                    new CuiRectTransformComponent { AnchorMin = AnchorOf(player), AnchorMax = AnchorOf(player), OffsetMin = OffsetMin(player, state), OffsetMax = OffsetMax(player, state) }
                }
            });
            CuiHelper.AddUi(player, container);
        }

        private static string Px(float x, float y) => x.ToString("0.#", CultureInfo.InvariantCulture) + " " + y.ToString("0.#", CultureInfo.InvariantCulture);

        // Advance widths in thousandths of an em, measured from Roboto Condensed (the robotocondensed face).
        private static readonly int[] BoldUpper = { 597, 561, 570, 565, 491, 476, 585, 614, 271, 490, 553, 479, 758, 612, 604, 569, 604, 554, 540, 543, 571, 578, 749, 560, 542, 530 };
        private static readonly int[] BoldLower = { 471, 495, 460, 496, 479, 328, 503, 492, 252, 246, 479, 252, 742, 492, 498, 495, 497, 332, 454, 307, 492, 449, 633, 453, 446, 453 };
        private static readonly int[] RegUpper = { 576, 545, 567, 571, 497, 480, 591, 620, 250, 483, 545, 475, 755, 619, 601, 554, 601, 531, 518, 521, 562, 560, 762, 551, 524, 522 };
        private static readonly int[] RegLower = { 479, 493, 462, 496, 468, 316, 493, 483, 229, 224, 451, 229, 752, 484, 502, 493, 500, 304, 456, 295, 483, 428, 650, 439, 417, 439 };

        private static float TextWidth(string text, int fontSize, bool bold)
        {
            if (string.IsNullOrEmpty(text) || fontSize <= 0) return 0f;
            int[] upper = bold ? BoldUpper : RegUpper;
            int[] lower = bold ? BoldLower : RegLower;
            int sum = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= 'A' && c <= 'Z') sum += upper[c - 'A'];
                else if (c >= 'a' && c <= 'z') sum += lower[c - 'a'];
                else if (c >= '0' && c <= '9') sum += bold ? 506 : 494;
                else if (c == ' ') sum += bold ? 230 : 229;
                else if (c == '/' || c == '\\') sum += bold ? 331 : 371;
                else if (c == '-' || c == '\u2013') sum += bold ? 363 : 248;
                else sum += bold ? 500 : 480;
            }
            return sum / 1000f * fontSize;
        }

        private string OffsetMax(BasePlayer player, string state)
        {
            if (!OnLeft(player)) return Px(-config.Right, -config.Top);
            float width = state == "minimized" ? LogoSize : config.Width;
            return Px(width, 0f);
        }

        private string OffsetMin(BasePlayer player, string state)
        {
            float height = PanelHeight(state);
            if (!OnLeft(player))
            {
                float width = state == "minimized" ? LogoSize : config.Width;
                return Px(-config.Right - width, -config.Top - height);
            }
            return Px(0f, -height);
        }

        private string AnchorOf(BasePlayer player)
        {
            return OnLeft(player) ? config.LeftAnchor : (string.IsNullOrEmpty(config.Anchor) ? "1 1" : config.Anchor);
        }

        private bool OnLeft(BasePlayer player)
        {
            string side;
            if (player == null || stored == null || stored.Sides == null) return false;
            return stored.Sides.TryGetValue(player.UserIDString, out side) && side == "left";
        }

        private void SetSide(BasePlayer player, string word)
        {
            if (player == null) return;
            bool left = word == "side" ? !OnLeft(player) : word == "left";
            if (stored.Sides == null) stored.Sides = new Dictionary<string, string>(StringComparer.Ordinal);
            stored.Sides[player.UserIDString] = left ? "left" : "right";
            SaveData();
            string state;
            if (!shown.TryGetValue(player.userID, out state)) return;
            Show(player);
        }

        private void OnCuiDraggableDrag(BasePlayer player, string name, Vector3 position, CommunityEntity.DraggablePositionSendType type)
        {
            if (player == null || name != IconDrag || type != CommunityEntity.DraggablePositionSendType.NormalizedScreen) return;
            if (float.IsNaN(position.x) || float.IsInfinity(position.x)) return;
            ulong id = player.userID;
            dragX[id] = Mathf.Clamp01(position.x);
            ignoreCycleUntil[id] = Time.realtimeSinceStartup + 0.75f;
            Timer pending;
            if (dragCommit.TryGetValue(id, out pending) && pending != null && !pending.Destroyed) pending.Destroy();
            dragCommit[id] = timer.Once(0.2f, () => CommitDrag(id, true));
        }

        private void OnCuiDraggableDrop(BasePlayer player, string draggedName, string draggedSlot, string swappedName, string swappedSlot)
        {
            if (player == null || draggedName != IconDrag) return;
            CommitDrag(player.userID, false);
        }

        private void CancelDrag(ulong id)
        {
            Timer pending;
            if (dragCommit.TryGetValue(id, out pending) && pending != null && !pending.Destroyed) pending.Destroy();
            dragCommit.Remove(id);
            dragX.Remove(id);
        }

        private void CommitDrag(ulong id, bool fromTimer)
        {
            Timer pending;
            if (dragCommit.TryGetValue(id, out pending))
            {
                dragCommit.Remove(id);
                if (!fromTimer && pending != null && !pending.Destroyed) pending.Destroy();
            }
            float x;
            if (!dragX.TryGetValue(id, out x)) return;
            dragX.Remove(id);
            BasePlayer player = BasePlayer.FindByID(id);
            if (player == null || !player.IsConnected || !shown.ContainsKey(id)) return;
            ignoreCycleUntil[id] = Time.realtimeSinceStartup + 0.75f;
            SetSide(player, x < 0.5f ? "left" : "right");
        }

        private float PanelHeight(string state)
        {
            if (state == "minimized") return LogoSize + BadgeHang();
            return config.Height > 1f ? config.Height : PanelH;
        }

        #endregion

        #region Shared updates

        private void ScheduleClock()
        {
            clockTimer?.Destroy();
            clockTimer = timer.Once(12f, () =>
            {
                TickShared(true);
                ScheduleClock();
            });
        }

        private void TickShared(bool includeShield)
        {
            string clock = ClockText();
            string online = OnlineText();
            string countText, countColor;
            Countdown(out countText, out countColor);
            string countSig = countText + "|" + countColor;
            bool pvp = CombatPvp();
            string badge = pvp ? "1" : "0";
            bool clockChanged = clock != clockCache;
            bool onlineChanged = online != onlineCache;
            bool countChanged = countSig != countdownCache;
            bool badgeChanged = badge != badgeCache;
            if (clockChanged) clockCache = clock;
            if (onlineChanged) onlineCache = online;
            if (countChanged) countdownCache = countSig;
            if (badgeChanged) badgeCache = badge;
            if (!clockChanged && !onlineChanged && !countChanged && !badgeChanged && !includeShield) return;
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                string state;
                if (!shown.TryGetValue(player.userID, out state)) continue;
                if (badgeChanged) PushBadge(player, pvp);
                if (!IsOpen(state)) continue;
                if (clockChanged) PushText(player, "NightshadeHud.Clock", clock, Hex(config.ClockColor), TextAnchor.MiddleRight, ClockFont, true);
                if (onlineChanged) PushText(player, "NightshadeHud.Online", online, Hex(config.MutedColor, 0.95f), TextAnchor.MiddleLeft, OnlineFont, false);
                if (countChanged || includeShield) RefreshStatus(player, false);
            }
        }

        private void PushModeLine()
        {
            badgeCache = "\0";
            countdownCache = "\0";
            TickShared(false);
        }

        private void MarkPop()
        {
            onlineCache = "\0";
            NextTick(() => TickShared(false));
        }

        private void RefreshShield(BasePlayer player)
        {
            if (player == null) return;
            NextTick(() => RefreshStatus(player, true));
        }

        private void RefreshStatus(BasePlayer player, bool force)
        {
            if (player == null) return;
            string state;
            if (!shown.TryGetValue(player.userID, out state) || !IsOpen(state)) return;
            string countText, countColor;
            Countdown(out countText, out countColor);
            string row = countText + "|" + countColor + "|" + ShieldSignature(player);
            string previous;
            if (!force && shieldCache.TryGetValue(player.userID, out previous) && previous == row) return;
            shieldCache[player.userID] = row;
            CuiHelper.DestroyUi(player, CountdownName);
            CuiHelper.DestroyUi(player, StatusSepName);
            CuiHelper.DestroyUi(player, ShieldName);
            var container = new CuiElementContainer();
            AddStatusRow(container, Half, player);
            CuiHelper.AddUi(player, container);
        }

        private void PushBadge(BasePlayer player, bool pvp)
        {
            var container = new CuiElementContainer();
            container.Add(new CuiElement
            {
                Name = BadgeBgName,
                Update = true,
                Components = { new CuiImageComponent { Color = Hex(pvp ? config.BadgePvpBg : config.BadgePveBg) } }
            });
            container.Add(new CuiElement
            {
                Name = BadgeName,
                Update = true,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = pvp ? BadgeWord(config.BadgePvpText, "PVP") : BadgeWord(config.BadgePveText, "PVE"),
                        FontSize = BadgeFont,
                        Align = TextAnchor.MiddleCenter,
                        Color = Hex(pvp ? config.BadgePvpColor : config.BadgePveColor),
                        Font = "robotocondensed-bold.ttf"
                    }
                }
            });
            CuiHelper.AddUi(player, container);
        }

        private void PushText(BasePlayer player, string name, string text, string color, TextAnchor align, int fontSize, bool bold)
        {
            var container = new CuiElementContainer();
            container.Add(new CuiElement
            {
                Name = name,
                Update = true,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = text ?? string.Empty,
                        FontSize = fontSize,
                        Align = align,
                        Color = color,
                        Font = bold ? "robotocondensed-bold.ttf" : "robotocondensed-regular.ttf"
                    }
                }
            });
            CuiHelper.AddUi(player, container);
        }

        #endregion

        #region Text

        private bool PurgeActive()
        {
            if (purge) return true;
            return CallBool(PurgeStatus, "API_IsPurgeActive");
        }

        private bool CombatPvp()
        {
            if (PurgeActive()) return true;
            return CallBool(WeekendPlugin(), "API_IsPvPActive");
        }

        private string ClockText()
        {
            TOD_Sky sky = TOD_Sky.Instance;
            if (sky == null || sky.Cycle == null) return "--:--";
            float hour = sky.Cycle.Hour;
            if (float.IsNaN(hour) || float.IsInfinity(hour)) return "--:--";
            int hours = Mathf.FloorToInt(Mathf.Repeat(hour, 24f));
            int minutes = Mathf.FloorToInt(Mathf.Repeat(hour, 1f) * 60f);
            if (minutes > 59) minutes = 59;
            if (config.Use24Hour) return hours.ToString("00") + ":" + minutes.ToString("00");
            int hour12 = hours % 12;
            if (hour12 == 0) hour12 = 12;
            return hour12 + ":" + minutes.ToString("00") + (hours < 12 ? " AM" : " PM");
        }

        private int ConnectedCount()
        {
            int online = 0;
            if (BasePlayer.activePlayerList == null) return 0;
            foreach (var player in BasePlayer.activePlayerList)
                if (player != null && player.IsConnected) online++;
            return online;
        }

        private string OnlineText()
        {
            if (!config.ShowOnline && !config.ShowSleepers) return string.Empty;
            int online = ConnectedCount();
            int sleepers = BasePlayer.sleepingPlayerList == null ? 0 : BasePlayer.sleepingPlayerList.Count;
            if (config.ShowOnline && config.ShowSleepers)
                return "Online " + online + "  \u00B7  Sleepers " + sleepers;
            if (config.ShowOnline) return "Online " + online;
            return "Sleepers " + sleepers;
        }

        private void Countdown(out string text, out string color)
        {
            if (!config.ShowCountdown)
            {
                text = string.Empty;
                color = Hex(config.CountdownIdle);
                return;
            }
            if (PurgeActive())
            {
                text = "Server wipe in " + FormatWipe(SecondsUntilWipe());
                color = Hex(config.CountdownActive);
                return;
            }
            if (WeekendPlugin() == null || !WeekendPlugin().IsLoaded)
            {
                text = "PvP unavailable";
                color = Hex(config.MutedColor);
                return;
            }
            bool active = CallBool(WeekendPlugin(), "API_IsPvPActive");
            double seconds = ToDouble(WeekendPlugin().Call("API_SecondsUntilPvPChange"));
            // -1 means the full-server force is holding PvP open with no scheduled end.
            if (active && seconds < 0d) text = "PVP";
            else text = active ? "PVP \u00B7 ends in " + FormatDur(seconds) : "PvP in " + FormatDur(seconds);
            color = Hex(active ? config.CountdownActive : config.CountdownIdle);
        }

        // Same schedule Nightshade Purge Status uses for an automatic purge: Thursday 14:00 America/New_York.
        private double SecondsUntilWipe()
        {
            bool ignored;
            string zoneId = string.IsNullOrEmpty(config.WipeTimeZoneId) ? "America/New_York" : config.WipeTimeZoneId;
            TimeZoneInfo tz = ResolveZone(zoneId, out ignored);
            int hour = 14;
            int minute = 0;
            ParseWipeClock(config.WipeTime, ref hour, ref minute);
            DayOfWeek day = ParseWipeDay(config.WipeDay);
            DateTime utc = DateTime.UtcNow;
            DateTime local;
            try { local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz); }
            catch { local = utc; }
            int days = ((int)day - (int)local.DayOfWeek + 7) % 7;
            DateTime target = local.Date.AddDays(days).AddHours(hour).AddMinutes(minute);
            if (local >= target) target = target.AddDays(7);
            DateTime targetUtc;
            try { targetUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(target, DateTimeKind.Unspecified), tz); }
            catch { targetUtc = DateTime.SpecifyKind(target, DateTimeKind.Utc); }
            double seconds = (targetUtc - utc).TotalSeconds;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0d) return 0d;
            return seconds;
        }

        private static DayOfWeek ParseWipeDay(string name)
        {
            if (string.IsNullOrEmpty(name)) return DayOfWeek.Thursday;
            switch (name.Trim().ToLowerInvariant())
            {
                case "sunday": return DayOfWeek.Sunday;
                case "monday": return DayOfWeek.Monday;
                case "tuesday": return DayOfWeek.Tuesday;
                case "wednesday": return DayOfWeek.Wednesday;
                case "thursday": return DayOfWeek.Thursday;
                case "friday": return DayOfWeek.Friday;
                case "saturday": return DayOfWeek.Saturday;
                default: return DayOfWeek.Thursday;
            }
        }

        private static void ParseWipeClock(string text, ref int hour, ref int minute)
        {
            if (string.IsNullOrEmpty(text)) return;
            string[] parts = text.Trim().Split(':');
            int h;
            int m = 0;
            if (parts.Length < 1 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out h)) return;
            if (parts.Length > 1 && !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m)) return;
            if (h < 0 || h > 23 || m < 0 || m > 59) return;
            hour = h;
            minute = m;
        }

        private static string FormatWipe(double seconds)
        {
            if (seconds < 0d) seconds = 0d;
            var span = TimeSpan.FromSeconds(Math.Ceiling(seconds));
            if (span.TotalDays >= 1d)
                return ((int)span.TotalDays) + "d " + span.Hours.ToString("00", CultureInfo.InvariantCulture) + "h";
            return ((int)span.TotalHours) + "h " + span.Minutes.ToString("00", CultureInfo.InvariantCulture) + "m";
        }

        private void RaidLine(BasePlayer player, out string text, out string color)
        {
            text = string.Empty;
            color = Hex(config.MutedColor);
            if (config == null || !config.ShowShield || player == null || WeekendPlugin() == null || !WeekendPlugin().IsLoaded)
                return;
            object raw = WeekendPlugin().Call("API_GetPlayerShield", player);
            string state = DictString(raw, "state") ?? "none";
            if (state != "active" && state != "queued") return;
            double seconds = ToDouble(DictValue(raw, "seconds"));
            if (seconds <= 0d) return;
            bool draining = DictBool(raw, "draining") || state == "active";
            int hours = HoursLeft(seconds);
            text = "PvP Shield: " + hours.ToString(CultureInfo.InvariantCulture) + "h left";
            color = draining ? Hex(config.ShieldActive) : Hex(config.ShieldQueued);
        }

        private static int HoursLeft(double seconds)
        {
            if (seconds < 0d || double.IsNaN(seconds) || double.IsInfinity(seconds)) return 0;
            int total = (int)Math.Ceiling(seconds);
            if (total < 0) return 0;
            return total / 3600;
        }

        private string FitWidth(string text, int font, bool bold, float limit)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (limit < 8f) limit = 8f;
            if (TextWidth(text, font, bold) <= limit) return text;
            const string ellipsis = "...";
            int keep = text.Length;
            while (keep > 4 && TextWidth(text.Substring(0, keep) + ellipsis, font, bold) > limit) keep--;
            if (keep < 4) return string.Empty;
            return text.Substring(0, keep) + ellipsis;
        }

        private string ShieldSignature(BasePlayer player)
        {
            string text, color;
            RaidLine(player, out text, out color);
            bool visible = !string.IsNullOrEmpty(text);
            return (visible ? "1|" : "0|") + text + "|" + color;
        }

        private void DebugHud(BasePlayer player)
        {
            object activeRaw = WeekendPlugin() != null && WeekendPlugin().IsLoaded ? WeekendPlugin().Call("API_IsPvPActive") : null;
            object secondsRaw = WeekendPlugin() != null && WeekendPlugin().IsLoaded ? WeekendPlugin().Call("API_SecondsUntilPvPChange") : null;
            object weekendRaw = WeekendPlugin() != null && WeekendPlugin().IsLoaded ? WeekendPlugin().Call("API_GetWeekend") : null;
            object shieldRaw = WeekendPlugin() != null && WeekendPlugin().IsLoaded ? WeekendPlugin().Call("API_GetPlayerShield", player) : null;
            BuildingPrivlidge tc = player.GetBuildingPrivilege();
            bool authed = tc != null && tc.authorizedPlayers != null && tc.authorizedPlayers.Contains(player.userID);
            string ruleset = DictString(weekendRaw, "ruleset") ?? "(no weekend result)";
            player.ChatMessage("<color=#b084ff>[Nightshade HUD]</color> mode " + StateOf(player)
                + " | pvp weekend plugin " + (WeekendPlugin() != null && WeekendPlugin().IsLoaded ? "loaded" : "MISSING")
                + " | purge " + (PurgeActive() ? "active" : "off"));
            player.ChatMessage("PvP active raw: " + (activeRaw == null ? "null" : activeRaw.GetType().Name + " " + activeRaw)
                + " | seconds raw: " + (secondsRaw == null ? "null" : secondsRaw.GetType().Name + " " + secondsRaw)
                + " | ruleset: " + ruleset);
            player.ChatMessage("Online count: " + ConnectedCount() + " (you are admin: " + player.IsAdmin + ") | sleepers: " + (BasePlayer.sleepingPlayerList == null ? 0 : BasePlayer.sleepingPlayerList.Count));
            player.ChatMessage("Cupboard net id: " + (tc == null || tc.net == null ? "none" : tc.net.ID.Value.ToString())
                + " | building " + (tc == null ? "none" : tc.buildingID.ToString())
                + " | authorized: " + authed
                + " | shield: " + (DictString(shieldRaw, "state") ?? "null call")
                + " " + ToDouble(DictValue(shieldRaw, "seconds")).ToString("0") + "s"
                + " tc " + (DictString(shieldRaw, "tcId") ?? "-"));
        }

        private static string FormatDur(double seconds)
        {
            if (seconds < 0d) seconds = 0d;
            var span = TimeSpan.FromSeconds(Math.Ceiling(seconds));
            if (span.TotalDays >= 1d) return ((int)span.TotalDays) + "d " + span.Hours.ToString("00") + "h";
            if (span.TotalHours >= 1d) return ((int)span.TotalHours) + "h " + span.Minutes.ToString("00") + "m";
            if (span.TotalMinutes >= 10d) return span.Minutes + "m";
            return span.Minutes.ToString("00") + "m " + span.Seconds.ToString("00") + "s";
        }

        #endregion

        #region Images

        private IEnumerator BootHud()
        {
            yield return null;
            purge = CallBool(PurgeStatus, "API_IsPurgeActive");
            var players = new List<BasePlayer>();
            if (BasePlayer.activePlayerList != null)
                foreach (var connected in BasePlayer.activePlayerList)
                    if (connected != null) players.Add(connected);
            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (player != null && player.IsConnected) Show(player);
                if ((i + 1) % 2 == 0) yield return null;
            }
            ScheduleClock();
            startupRoutine = null;
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin == null) return;
            if (plugin.Name == "ImageLibrary")
            {
                ResetArt(logoArt);
                ResetArt(purgeArt);
                ResetArt(borderArt);
                ResetArt(backgroundArt);
                QueueImages();
            }
            if (plugin.Name == "Nightshade Purge Status")
            {
                purge = CallBool(PurgeStatus, "API_IsPurgeActive");
                RedrawAll();
            }
            else if (plugin.Name == "NightshadePVPWeekend" || plugin.Name == "NightshadeRaidProtection")
                PushModeLine();
        }

        private void OnNightshadePvPStarted(long startUtcTicks, long endUtcTicks) => PushModeLine();
        private void OnNightshadePvPEnded(long startUtcTicks, long endUtcTicks) => PushModeLine();

        private void OnPurgeStarted(double endTimestamp)
        {
            purge = true;
            RedrawAll();
        }

        private void OnPurgeEnded()
        {
            purge = false;
            RedrawAll();
        }

        private void ResetArt(Art art)
        {
            if (art == null || art.Queued) return;
            art.GaveUp = false;
            art.Tries = 0;
            art.Waiting = false;
        }

        private void EnsureArtPaths()
        {
            BindArt(logoArt, LogoPath(config == null ? null : config.LogoImage));
            BindArt(purgeArt, LogoPath(config == null ? null : config.PurgeLogoImage, DefaultPurgeLogoFile));
            BindArt(borderArt, DataPathOf(config == null ? null : config.BorderImage, DefaultBorderFile));
            BindArt(backgroundArt, DataPathOf(config == null ? null : config.BackgroundImage, DefaultBackgroundFile));
        }

        private static void BindArt(Art art, string path)
        {
            if (art == null) return;
            string next = path ?? string.Empty;
            if (art.Path == next) return;
            bool wasEmpty = string.IsNullOrEmpty(art.Path);
            art.Path = next;
            if (wasEmpty && art.GaveUp && next.Length > 0)
            {
                art.GaveUp = false;
                art.Warned = false;
                art.Tries = 0;
            }
        }

        private void QueueImages()
        {
            EnsureArtPaths();
            QueueArt(logoArt);
            QueueArt(purgeArt);
            QueueArt(borderArt);
            QueueArt(backgroundArt);
        }

        private void QueueArt(Art art)
        {
            if (art == null || art.Queued || art.Waiting) return;
            if (string.IsNullOrEmpty(art.Path)) EnsureArtPaths();
            if (art.GaveUp) return;
            if (string.IsNullOrEmpty(art.Path) || !File.Exists(art.Path))
            {
                art.GaveUp = true;
                WarnArt(art, art.Label + " file is missing (" + (string.IsNullOrEmpty(art.Path) ? "no path" : art.Path) + "). The HUD stays up without it.");
                return;
            }
            if (ImageLibrary == null || !ImageLibrary.IsLoaded)
            {
                art.Tries++;
                if (art.Tries >= ImageTries)
                {
                    art.GaveUp = true;
                    WarnArt(art, "ImageLibrary is not loaded. The HUD stays up without its " + art.Label + ".");
                    return;
                }
                art.Waiting = true;
                float delay = art.Tries * 5f;
                timer.Once(delay, () =>
                {
                    art.Waiting = false;
                    QueueArt(art);
                });
                return;
            }
            try
            {
                byte[] bytes = File.ReadAllBytes(art.Path);
                if (bytes == null || bytes.Length < 8 || bytes.Length > ImageMaxBytes || !IsPng(bytes))
                {
                    art.GaveUp = true;
                    WarnArt(art, art.Label + " is empty, not a png, or larger than 3 MB (" + art.Path + "). The HUD stays up without it.");
                    return;
                }
                object added = ImageLibrary.Call("AddImageData", art.Key, bytes, 0UL);
                if (!(added is bool) || !(bool)added)
                {
                    art.GaveUp = true;
                    WarnArt(art, "ImageLibrary did not accept the " + art.Label + ". The HUD stays up without it.");
                    return;
                }
                art.Queued = true;
                ScheduleArtPoll();
            }
            catch (Exception ex)
            {
                art.GaveUp = true;
                WarnArt(art, "Could not read the " + art.Label + ": " + ex.Message + ". The HUD stays up without it.");
            }
        }

        private void WarnArt(Art art, string message)
        {
            if (art == null || art.Warned) return;
            art.Warned = true;
            PrintWarning(message);
        }

        private static bool IsPng(byte[] bytes)
        {
            return bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
                && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;
        }

        private int artPolls;

        private void ScheduleArtPoll()
        {
            if (artTimer != null) return;
            artTimer = timer.Once(1f, PollArt);
        }

        private void PollArt()
        {
            artTimer = null;
            bool ready = NoteArt(logoArt) | NoteArt(purgeArt) | NoteArt(borderArt) | NoteArt(backgroundArt);
            if (ready) RedrawAll();
            artPolls++;
            if (artPolls >= 8) return;
            if ((logoArt.Queued && string.IsNullOrEmpty(logoArt.Shown))
                || (purgeArt.Queued && string.IsNullOrEmpty(purgeArt.Shown))
                || (borderArt.Queued && string.IsNullOrEmpty(borderArt.Shown))
                || (backgroundArt.Queued && string.IsNullOrEmpty(backgroundArt.Shown)))
                ScheduleArtPoll();
        }

        private bool NoteArt(Art art)
        {
            if (art == null || !art.Queued) return false;
            string png = ReadArt(art);
            if (string.IsNullOrEmpty(png) || png == art.Shown) return false;
            art.Shown = png;
            return true;
        }

        private string ArtPng(Art art)
        {
            if (art == null) return null;
            if (!art.Queued && !art.GaveUp) QueueArt(art);
            if (art.GaveUp || !art.Queued) return null;
            string png = ReadArt(art);
            if (!string.IsNullOrEmpty(png)) art.Shown = png;
            return png;
        }

        private string ReadArt(Art art)
        {
            if (ImageLibrary == null || !ImageLibrary.IsLoaded) return null;
            try
            {
                object has = ImageLibrary.Call("HasImage", art.Key, 0UL);
                if (!(has is bool) || !(bool)has) return null;
                string png = ImageLibrary.Call<string>("GetImage", art.Key, 0UL);
                if (string.IsNullOrEmpty(png) || png == "0" || png == "NONE_0") return null;
                return png;
            }
            catch (Exception ex)
            {
                art.GaveUp = true;
                WarnArt(art, "ImageLibrary could not return the " + art.Label + ": " + ex.Message);
                return null;
            }
        }

        private void RedrawAll()
        {
            if (BasePlayer.activePlayerList == null) return;
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                if (!shown.ContainsKey(player.userID)) continue;
                Show(player);
            }
        }

        private static bool SafeArtName(string name, bool filenameOnly)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string text = name.Trim().Replace('\\', '/');
            if (text.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
            if (!text.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return false;
            if (filenameOnly && text.IndexOf('/') >= 0) return false;
            if (Path.IsPathRooted(text)) return false;
            return true;
        }

        private string LogoPath(string name, string fallback = DefaultLogoFile)
        {
            string file = CleanArtName(name, true, string.IsNullOrEmpty(fallback) ? DefaultLogoFile : fallback);
            string root = Interface.Oxide.DataDirectory ?? string.Empty;
            return Path.Combine(root, "Nightshade", "NightshadeHud", file);
        }

        private string DataPathOf(string file, string fallback)
        {
            if (!SafeArtName(file, false)) file = fallback ?? DefaultBorderFile;
            file = (file ?? DefaultBorderFile).Replace('\\', '/').Trim().TrimStart('/');
            string root = Interface.Oxide.DataDirectory ?? string.Empty;
            return Path.Combine(root, file);
        }

        #endregion

        #region Border

        private void AddBorder(CuiElementContainer container)
        {
            if (config == null || !config.ShowBorder) return;
            string png = ArtPng(borderArt);
            if (string.IsNullOrEmpty(png)) return;
            float left = -config.BorderOutsetLeft;
            float right = config.Width + config.BorderOutsetRight;
            float top = config.BorderOutsetTop;
            float bottom = config.Height + config.BorderOutsetBottom;
            container.Add(new CuiElement
            {
                Name = BorderName,
                Parent = Root,
                Components =
                {
                    new CuiRawImageComponent
                    {
                        Png = png,
                        Color = "1 1 1 1",
                        Sprite = "assets/content/textures/generic/fulltransparent.tga"
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "0 1",
                        OffsetMin = Px(left, -bottom),
                        OffsetMax = Px(right, top)
                    }
                }
            });
        }

        private void RedrawBoxed()
        {
            if (BasePlayer.activePlayerList == null) return;
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                string state;
                if (!shown.TryGetValue(player.userID, out state) || !IsOpen(state)) continue;
                Show(player);
            }
        }

        #endregion

        #region Helpers

        private void AddLabel(CuiElementContainer container, string name, string parent, string text, string color, int size, TextAnchor align, string anchorMin, string anchorMax, string offsetMin, string offsetMax, bool bold = false)
        {
            container.Add(new CuiElement
            {
                Name = name,
                Parent = parent,
                Components =
                {
                    new CuiTextComponent { Text = text ?? string.Empty, FontSize = size, Align = align, Color = color, Font = bold ? "robotocondensed-bold.ttf" : "robotocondensed-regular.ttf" },
                    new CuiRectTransformComponent { AnchorMin = anchorMin, AnchorMax = anchorMax, OffsetMin = offsetMin, OffsetMax = offsetMax }
                }
            });
        }

        private static object DictValue(object raw, string key)
        {
            var dict = raw as System.Collections.IDictionary;
            if (dict == null || !dict.Contains(key)) return null;
            return dict[key];
        }

        private static string DictString(object raw, string key)
        {
            object value = DictValue(raw, key);
            return value == null ? null : value.ToString();
        }

        private static bool DictBool(object raw, string key)
        {
            object value = DictValue(raw, key);
            if (value is bool flag) return flag;
            return value != null && string.Equals(value.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static bool CallBool(Plugin plugin, string method)
        {
            if (plugin == null || !plugin.IsLoaded) return false;
            return plugin.Call(method) is bool value && value;
        }

        private static double ToDouble(object value)
        {
            if (value is double d) return d;
            if (value is float f) return f;
            if (value is int i) return i;
            if (value is long l) return l;
            double parsed;
            return value != null && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : 0d;
        }

        private static string Hex(string hex, float alpha = -1f)
        {
            if (string.IsNullOrEmpty(hex)) return "1 1 1 1";
            hex = hex.Trim().TrimStart('#');
            if (hex.Length < 6) return "1 1 1 1";
            byte r = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
            byte g = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
            byte b = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
            float a = alpha >= 0f ? Mathf.Clamp01(alpha) : 1f;
            return (r / 255f).ToString("0.###", CultureInfo.InvariantCulture) + " "
                + (g / 255f).ToString("0.###", CultureInfo.InvariantCulture) + " "
                + (b / 255f).ToString("0.###", CultureInfo.InvariantCulture) + " "
                + a.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static TimeZoneInfo ResolveZone(string id, out bool fallback)
        {
            fallback = false;
            if (!string.IsNullOrEmpty(id))
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
            string[] ids = { "America/New_York", "Eastern Standard Time", "US/Eastern" };
            foreach (string candidate in ids)
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
            fallback = true;
            try
            {
                var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
                var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
                var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2007, 1, 1), new DateTime(9999, 12, 31), TimeSpan.FromHours(1), start, end);
                return TimeZoneInfo.CreateCustomTimeZone("Nightshade-Eastern", TimeSpan.FromHours(-5), "Eastern Time (fallback)", "Eastern Standard Time", "Eastern Daylight Time", new[] { rule });
            }
            catch
            {
                return TimeZoneInfo.CreateCustomTimeZone("Nightshade-Eastern-EST", TimeSpan.FromHours(-5), "Eastern Time (fixed EST)", "EST");
            }
        }

        #endregion
    }
}
