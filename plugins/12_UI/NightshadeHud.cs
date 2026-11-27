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
    [Info("NightshadeHud", "Nightshade", "1.3.0")]
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
        private const string TeleportTitleName = "NightshadeHud.TeleportTitle";
        private const string TeleportPriceName = "NightshadeHud.TeleportPrice";
        private const string CountdownName = "NightshadeHud.Countdown";
        private const string StatusSepName = "NightshadeHud.StatusSep";
        private const string ShieldName = "NightshadeHud.ShieldQueue";
        private const string DataPath = "Nightshade/NightshadeHud/State";
        private const string ArtDir = "Nightshade/NightshadeHud";
        private const string DefaultBorderFile = "Nightshade/NightshadeHud/hud_border.png";
        private const string DefaultBackgroundFile = "Nightshade/NightshadeHud/hud_bg.png";
        private const float Pad = 16f;
        private const float NameGap = 8f;
        private const float NameTop = 17f;
        private const float NameH = 16f;
        private const float OnlineTop = 37f;
        private const float OnlineH = 14f;
        private const float StatusTop = 53f;
        private const float StatusH = 16f;
        private const float TeleTitleTop = 80f;
        private const float TeleTitleH = 14f;
        private const float TelePriceTop = 95f;
        private const float TelePriceH = 12f;
        private const int NameFont = 14;
        private const int TagFont = 11;
        private const int ClockFont = 12;
        private const int OnlineFont = 11;
        private const int StatusFont = 12;
        private const int ShieldFont = 11;
        private const int TeleTitleFont = 11;
        private const int TelePriceFont = 10;
        private const int BadgeFont = 8;
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
        private Timer priceTimer;
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
        private int outpostPrice = 150;
        private int banditPrice = 200;
        private bool priceFileWarned;

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

        private class Settings
        {
            [JsonProperty("Chat command")] public string Command = "hud";
            [JsonProperty("Default state for new players (minimized, half, full)")] public string DefaultState = "full";
            [JsonProperty("Layer (Overlay shows in the inventory)")] public string Layer = "Overlay";
            [JsonProperty("Anchor")] public string Anchor = "1 1";
            [JsonProperty("Left anchor (under the missions bar)")] public string LeftAnchor = "0.008 0.905";
            [JsonProperty("Inset from the right (px)")] public float Right = 16f;
            [JsonProperty("Inset from the top (px)")] public float Top = 12f;
            [JsonProperty("Width (px)")] public float Width = 268f;
            [JsonProperty("Height (px)")] public float Height = 120f;
            [JsonProperty("Icon size (px)")] public float IconSize = 36f;
            [JsonProperty("Hide while using a computer station")] public bool HideInComputerStation = true;
            [JsonProperty("Logo image file")] public string LogoImage = "logo.png";
            [JsonProperty("Purge logo image file")] public string PurgeLogoImage = "purge_logo.png";
            [JsonProperty("Server name")] public string ServerName = "Nightshade";
            [JsonProperty("Tag text")] public string TagText = "Solo/Duo";
            [JsonProperty("Tag color")] public string TagColor = "#9A9692";
            [JsonProperty("Use 24-hour clock")] public bool Use24Hour = false;
            [JsonProperty("Time zone id")] public string TimeZoneId = "America/New_York";
            [JsonProperty("Show ET label")] public bool ShowZoneLabel = true;
            [JsonProperty("Show online count")] public bool ShowOnline = true;
            [JsonProperty("Show sleepers")] public bool ShowSleepers = true;
            [JsonProperty("Count admins in online")] public bool CountAdmins = false;
            [JsonProperty("Show PvP countdown")] public bool ShowCountdown = true;
            [JsonProperty("Show shield line")] public bool ShowShield = true;
            [JsonProperty("Show teleportation block")] public bool ShowTeleport = true;
            [JsonProperty("Teleportation title")] public string TeleportTitle = "Teleportation";
            [JsonProperty("Teleportation title color")] public string TeleportTitleColor = "#50A0FF";
            [JsonProperty("Teleportation label format")] public string TeleportFormat = "Outpost: {outpost}rp | Bandit: {bandit}rp";
            [JsonProperty("Default Outpost price")] public int DefaultOutpostPrice = 150;
            [JsonProperty("Default Bandit price")] public int DefaultBanditPrice = 200;
            [JsonProperty("Show border")] public bool ShowBorder = true;
            [JsonProperty("Border image file")] public string BorderImage = DefaultBorderFile;
            [JsonProperty("Background image file")] public string BackgroundImage = DefaultBackgroundFile;
            [JsonProperty("Border outset left (px)")] public float BorderOutsetLeft = 8f;
            [JsonProperty("Border outset right (px)")] public float BorderOutsetRight = 8f;
            [JsonProperty("Border outset top (px)")] public float BorderOutsetTop = 12f;
            [JsonProperty("Border outset bottom (px)")] public float BorderOutsetBottom = 8f;
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
            try { config = Config.ReadObject<Settings>() ?? new Settings(); }
            catch (Exception ex)
            {
                PrintWarning("Invalid config, using defaults: " + ex.Message);
                config = new Settings();
            }
            if (config.Width < 180f) config.Width = 268f;
            if (config.Height < 80f) config.Height = 120f;
            if (config.IconSize < 16f) config.IconSize = 36f;
            if (string.IsNullOrEmpty(config.NameColor) || string.Equals(config.NameColor, "#F4F1EA", StringComparison.OrdinalIgnoreCase))
                config.NameColor = "#A267D3";
            if (config.TagText == null) config.TagText = "Solo/Duo";
            // The approved 1.3 layout uses grey for the tag. #C4A6E0 was the 1.2 default.
            if (string.IsNullOrEmpty(config.TagColor) || string.Equals(config.TagColor, "#C4A6E0", StringComparison.OrdinalIgnoreCase))
                config.TagColor = "#9A9692";
            if (string.IsNullOrEmpty(config.LeftAnchor)) config.LeftAnchor = "0.008 0.905";
            if (string.IsNullOrEmpty(config.Anchor)) config.Anchor = "1 1";
            // 58 cleared the old top-right skill panel. That panel now sits at the bottom.
            if (Mathf.Abs(config.Top - 58f) < 0.1f) config.Top = 12f;
            if (Mathf.Abs(config.Width - 252f) < 0.1f) config.Width = 268f;
            if (Mathf.Abs(config.IconSize - 34f) < 0.1f) config.IconSize = 36f;
            // The rounded border is drawn for a 12px top outset. 6 was the 1.2 default.
            if (Mathf.Abs(config.BorderOutsetTop - 6f) < 0.1f) config.BorderOutsetTop = 12f;
            if (string.Equals(config.CountdownIdle, "#C8C8C8", StringComparison.OrdinalIgnoreCase))
                config.CountdownIdle = "#FF4D4D";
            if (string.IsNullOrEmpty(config.CountdownActive)) config.CountdownActive = "#FF4D4D";
            if (string.IsNullOrEmpty(config.CountdownIdle)) config.CountdownIdle = "#FF4D4D";
            if (!SafeArtName(config.LogoImage, true)) config.LogoImage = "logo.png";
            else config.LogoImage = config.LogoImage.Trim();
            if (!SafeArtName(config.PurgeLogoImage, true)) config.PurgeLogoImage = "purge_logo.png";
            else config.PurgeLogoImage = config.PurgeLogoImage.Trim();
            if (config.TeleportTitle == null) config.TeleportTitle = "Teleportation";
            if (string.IsNullOrEmpty(config.TeleportTitleColor)) config.TeleportTitleColor = "#50A0FF";
            if (string.IsNullOrEmpty(config.TeleportFormat)) config.TeleportFormat = "Outpost: {outpost}rp | Bandit: {bandit}rp";
            if (config.DefaultOutpostPrice < 0) config.DefaultOutpostPrice = 150;
            if (config.DefaultBanditPrice < 0) config.DefaultBanditPrice = 200;
            if (!SafeArtName(config.BorderImage, false)) config.BorderImage = DefaultBorderFile;
            if (!SafeArtName(config.BackgroundImage, false)) config.BackgroundImage = DefaultBackgroundFile;
            if (string.IsNullOrEmpty(config.ShieldActive)) config.ShieldActive = "#69D8A0";
            if (string.IsNullOrEmpty(config.ShieldQueued)) config.ShieldQueued = "#E2B968";
            SaveConfig();
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
            priceTimer?.Destroy();
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

        private void OnServerSave()
        {
            SaveData();
            RefreshTeleportPrices(true);
        }

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
                    new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = Px(x, y - config.IconSize), OffsetMax = Px(x + config.IconSize, y) }
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
            float center = iconLeft + config.IconSize * 0.5f;
            left = center - width * 0.5f;
            right = left + width;
            bottom = iconTop - config.IconSize - hang;
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
            right = Mathf.Max(iconLeft + config.IconSize, badgeRight);
            top = iconTop;
            bottom = Mathf.Min(iconTop - config.IconSize, badgeBottom);
        }

        private string IconCommand()
        {
            string command = string.IsNullOrEmpty(config.Command) ? "hud" : config.Command;
            return "chat.say /" + command + " cycle";
        }

        private float NameX()
        {
            return Pad + config.IconSize + NameGap;
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
            if (ShowTeleport())
            {
                string titleColor = Hex(string.IsNullOrEmpty(config.TeleportTitleColor) ? "#50A0FF" : config.TeleportTitleColor);
                AddLabel(container, TeleportTitleName, parent, TeleportTitleText(), titleColor, TeleTitleFont, TextAnchor.MiddleCenter, "0 1", "0 1", Px(0f, -(TeleTitleTop + TeleTitleH)), Px(config.Width, -TeleTitleTop), true);
                AddLabel(container, TeleportPriceName, parent, TeleportPriceText(), Hex(config.ClockColor), TelePriceFont, TextAnchor.MiddleCenter, "0 1", "0 1", Px(0f, -(TelePriceTop + TelePriceH)), Px(config.Width, -TelePriceTop));
            }
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
            float width = state == "minimized" ? config.IconSize : config.Width;
            return Px(width, 0f);
        }

        private string OffsetMin(BasePlayer player, string state)
        {
            float height = PanelHeight(state);
            if (!OnLeft(player))
            {
                float width = state == "minimized" ? config.IconSize : config.Width;
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
            if (state == "minimized") return config.IconSize + BadgeHang();
            return config.Height;
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
                double remain = PurgeRemaining();
                text = remain < 0d ? "PVP" : "PVP \u00B7 ends in " + FormatDur(remain);
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

        private double PurgeRemaining()
        {
            if (PurgeStatus == null || !PurgeStatus.IsLoaded) return -1d;
            double end = ToDouble(PurgeStatus.Call("API_GetPurgeEndTimestamp"));
            if (end <= 0d) return -1d;
            double now;
            try { now = Network.TimeEx.currentTimestamp; }
            catch { return -1d; }
            double remain = end - now;
            if (double.IsNaN(remain) || double.IsInfinity(remain)) return -1d;
            return remain;
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
            RefreshTeleportPrices(false);
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
            priceTimer?.Destroy();
            priceTimer = timer.Every(120f, () => RefreshTeleportPrices(true));
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

        private void QueueImages()
        {
            PrepareArt(logoArt, LogoPath(config.LogoImage));
            PrepareArt(purgeArt, LogoPath(config.PurgeLogoImage));
            PrepareArt(borderArt, DataPathOf(config.BorderImage, DefaultBorderFile));
            PrepareArt(backgroundArt, DataPathOf(config.BackgroundImage, DefaultBackgroundFile));
            QueueArt(logoArt);
            QueueArt(purgeArt);
            QueueArt(borderArt);
            QueueArt(backgroundArt);
        }

        private void PrepareArt(Art art, string path)
        {
            if (art == null) return;
            art.Path = path;
        }

        private void QueueArt(Art art)
        {
            if (art == null || art.Queued || art.GaveUp || art.Waiting) return;
            if (string.IsNullOrEmpty(art.Path) || !File.Exists(art.Path))
            {
                art.GaveUp = true;
                WarnArt(art, art.Label + " file is missing (" + art.Path + "). The HUD stays up without it.");
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

        private string LogoPath(string name)
        {
            string file = SafeArtName(name, true) ? name.Trim() : "logo.png";
            return Path.Combine(Interface.Oxide.DataDirectory, "Nightshade", "NightshadeHud", file);
        }

        private string DataPathOf(string file, string fallback)
        {
            if (!SafeArtName(file, false)) file = fallback;
            file = file.Replace('\\', '/').Trim().TrimStart('/');
            return Path.Combine(Interface.Oxide.DataDirectory, file);
        }

        #endregion

        #region Teleport and border

        private bool ShowTeleport()
        {
            return config != null && config.ShowTeleport;
        }

        private string TeleportTitleText()
        {
            return string.IsNullOrEmpty(config.TeleportTitle) ? "Teleportation" : config.TeleportTitle;
        }

        private string TeleportPriceText()
        {
            string format = string.IsNullOrEmpty(config.TeleportFormat)
                ? "Outpost: {outpost}rp | Bandit: {bandit}rp"
                : config.TeleportFormat;
            return format
                .Replace("{outpost}", outpostPrice.ToString(CultureInfo.InvariantCulture))
                .Replace("{bandit}", banditPrice.ToString(CultureInfo.InvariantCulture));
        }

        private void RefreshTeleportPrices(bool redraw)
        {
            if (config == null) return;
            int outpost = config.DefaultOutpostPrice;
            int bandit = config.DefaultBanditPrice;
            bool warned = false;
            try
            {
                string path = Path.Combine(Interface.Oxide.ConfigDirectory, "NTeleportation.json");
                if (!File.Exists(path))
                {
                    if (!priceFileWarned)
                    {
                        priceFileWarned = true;
                        PrintWarning("NTeleportation.json was not found. Teleport prices are using the HUD defaults.");
                    }
                }
                else
                {
                    JObject root = JObject.Parse(File.ReadAllText(path));
                    JObject commands = FindObject(root, "Dynamic Commands");
                    int parsed;
                    if (commands == null || !TryPay(commands, "Outpost", out parsed))
                        warned = true;
                    else
                        outpost = parsed;
                    if (commands == null || !TryPay(commands, "Bandit", out parsed))
                        warned = true;
                    else
                        bandit = parsed;
                    if (warned && !priceFileWarned)
                    {
                        priceFileWarned = true;
                        PrintWarning("NTeleportation.json is missing Outpost or Bandit Pay. Missing prices use the HUD defaults.");
                    }
                }
            }
            catch (Exception ex)
            {
                if (!priceFileWarned)
                {
                    priceFileWarned = true;
                    PrintWarning("Could not read NTeleportation prices: " + ex.Message);
                }
            }
            bool changed = outpost != outpostPrice || bandit != banditPrice;
            outpostPrice = outpost;
            banditPrice = bandit;
            if (redraw && changed && ShowTeleport()) RedrawBoxed();
        }

        private static JObject FindObject(JObject parent, string name)
        {
            if (parent == null || string.IsNullOrEmpty(name)) return null;
            JToken direct = parent[name];
            if (direct is JObject exact) return exact;
            foreach (var property in parent.Properties())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                return property.Value as JObject;
            }
            return null;
        }

        private static bool TryPay(JObject commands, string key, out int pay)
        {
            pay = 0;
            JObject entry = FindObject(commands, key);
            if (entry == null) return false;
            JToken token = entry["Pay"];
            if (token == null)
            {
                foreach (var property in entry.Properties())
                {
                    if (!string.Equals(property.Name, "Pay", StringComparison.OrdinalIgnoreCase)) continue;
                    token = property.Value;
                    break;
                }
            }
            if (token == null || token.Type == JTokenType.Null) return false;
            double number;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                number = token.Value<double>();
            else if (!double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                return false;
            if (double.IsNaN(number) || double.IsInfinity(number) || number < 0d) return false;
            pay = (int)Math.Round(number);
            return true;
        }

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
