using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("VoteDayTests")]

namespace Oxide.Plugins
{
    [Info("NightshadeVoteDay", "Nightshade", "1.6.4")]
    [Description("Vote for day at a configurable night time. One vote per night. Players vote and buy day from one Overlay panel left of the hotbar.")]
    public class NightshadeVoteDay : RustPlugin
    {
        // --- Config ---
        private const string SkipDataPath = "Nightshade/NightshadeVoteDay";
        private const string PanelDataPath = "Nightshade/NightshadeVoteDay/Panel";
        private const string Root = "NightshadeVoteDay";
        private const string BgName = "NightshadeVoteDay.Bg";
        private const string LogoName = "NightshadeVoteDay.Logo";
        private const string SunName = "NightshadeVoteDay.Sun";
        private const string TitleName = "NightshadeVoteDay.Title";
        private const string CountName = "NightshadeVoteDay.Count";
        private const string TrackName = "NightshadeVoteDay.Track";
        private const string FillName = "NightshadeVoteDay.Fill";
        private const string VotesName = "NightshadeVoteDay.Votes";
        private const string HaveName = "NightshadeVoteDay.Have";
        private const string StatusName = "NightshadeVoteDay.Status";
        private const string PriceName = "NightshadeVoteDay.Price";
        private const string VoteButton = "NightshadeVoteDay.Vote";
        private const string BuyButton = "NightshadeVoteDay.Buy";
        private const string ConfirmButton = "NightshadeVoteDay.Confirm";
        private const string CancelButton = "NightshadeVoteDay.Cancel";
        private const float UiGapSeconds = 0.3f;
        private const string ToggleName = "NightshadeVoteDay.Toggle";
        private const string UiCommand = "nightshadevoteday.ui";
        // 1.6.0-1.6.2 built a second card with this suffix, 16px lower than the
        // hotbar card. 1.6.3 kept drawing that lower spot, so both boxes showed.
        private const string LegacyMenuSuffix = ".Inv";
        private const string LegacyLogoUrl = "https://raw.githubusercontent.com/nightshadepve/Nightshadeserver/refs/heads/main/260625_Logo_75_nobg.png";
        private const int SkipStateVersion = 2;
        internal const string ModeKey = "Vote mode (Daily = vote every night, SkipMultiple = a pass sets this night to morning, then the next N nights have no vote and run normally)";
        internal const string QuietNightsKey = "Nights without a vote after a pass (SkipMultiple only, default 2; these nights are not set to morning)";
        private const string OldModeKey = "Vote mode (Daily = vote every night, SkipMultiple = one passing vote skips several nights)";
        private const string OldNightsKey = "Nights to skip (SkipMultiple only, includes the voted night, minimum 1)";

        private PluginConfig _config;

        internal class PluginConfig
        {
            [JsonProperty(ModeKey)]
            public string VoteMode = "SkipMultiple";

            [JsonProperty(QuietNightsKey)]
            public int NightsWithoutVote = 2;

            [JsonProperty("VoteStartTime (24hr format)")]
            public float VoteStartTime  = 19f;

            [JsonProperty("DayTime (Game hour to set if vote passes or day is purchased)")]
            public float DayTime        = 8f;

            [JsonProperty("VoteDuration (Time players have to vote in seconds)")]
            public float VoteDuration   = 60f;

            [JsonProperty("WindowDuration (Voting is locked after this many seconds)")]
            public float WindowDuration = 120f;

            [JsonProperty("VoteThreshold (Fraction of online players required to pass, e.g. 0.75 = 75%)")]
            public float VoteThreshold  = 0.75f;

            [JsonProperty(NightshadeVoteDayRules.CurrencyKey)]
            public string Currency      = "ServerRewards";

            [JsonProperty(NightshadeVoteDayRules.RpCostKey)]
            public int RPCost           = 500;

            [JsonProperty(NightshadeVoteDayRules.EcoCostKey)]
            public double EcoCost       = 500.0;

            [JsonProperty("Show vote panel")]
            public bool ShowPanel = true;

            [JsonProperty(NightshadeVoteDayRules.ClosedLayerKey)]
            public string ClosedLayer = NightshadeVoteDayRules.ClosedLayerName;

            [JsonProperty(NightshadeVoteDayRules.OpenLayerKey)]
            public string OpenLayer = NightshadeVoteDayRules.OpenLayerName;

            [JsonProperty("Theme mode (Shop, Auto, or a theme name)")]
            public string ThemeMode = "Shop";

            [JsonProperty("Theme source config file")]
            public string ThemeFile = "NightshadeShop.json";

            [JsonProperty("Panel anchor")]
            public string PanelAnchor = "0.5 0";

            [JsonProperty("Hotbar left edge (px from bottom center, negative is left)")]
            public float HotbarLeft = NightshadeVoteDayRules.DefaultHotbarLeft;

            [JsonProperty("Gap between panel and hotbar (px)")]
            public float PanelGap = NightshadeVoteDayRules.DefaultGap;

            [JsonProperty("Panel bottom (px)")]
            public float PanelBottom = NightshadeVoteDayRules.DefaultBottom;

            [JsonProperty("Panel width (px)")]
            public float PanelWidth = NightshadeVoteDayRules.DefaultWidth;

            [JsonProperty("Panel height (px)")]
            public float PanelHeight = NightshadeVoteDayRules.DefaultHeight;

            [JsonProperty("Collapsed size (px)")]
            public float CollapsedSize = NightshadeVoteDayRules.DefaultCollapsed;

            [JsonProperty(NightshadeVoteDayRules.OpenRightKey)]
            public float OpenRight = NightshadeVoteDayRules.DefaultOpenRight;

            [JsonProperty(NightshadeVoteDayRules.OpenBottomKey)]
            public float OpenBottom = NightshadeVoteDayRules.DefaultOpenBottom;

            [JsonProperty("Panel opacity (0-100)")]
            public float PanelOpacity = 94f;

            [JsonProperty("Panel color")]
            public string PanelColor = "#1A1024";

            [JsonProperty("Panel accent color")]
            public string AccentColor = "#A267D3";

            [JsonProperty("Panel text color")]
            public string TextColor = "#F2EAF7";

            [JsonProperty("Panel muted color")]
            public string MutedColor = "#A898B8";

            [JsonProperty("Panel success color")]
            public string SuccessColor = "#3DDC84";

            [JsonProperty("Panel fail color")]
            public string FailColor = "#D58D7C";

            [JsonProperty("Panel sun color")]
            public string SunColor = "#E2B968";

            [JsonProperty("Panel track color")]
            public string TrackColor = "#120A1C";

            [JsonProperty("Panel title")]
            public string PanelTitle = "Skip Night";

            [JsonProperty("Free the cursor while the panel is expanded")]
            public bool CursorWhileExpanded = false;

            [JsonProperty("Cursor key (firethird, use, reload, none)")]
            public string CursorKey = "firethird";

            [JsonProperty(NightshadeVoteDayRules.LogoKey)]
            public string PanelLogoUrl = "";

            [JsonProperty("Seconds to keep the panel after the vote ends (0 removes it immediately)")]
            public float ResultHoldSeconds = 6f;

            [JsonProperty(NightshadeVoteDayRules.ConfigVersionKey)]
            public int ConfigVersion = 0;
        }

        internal class SkipState
        {
            public int Version;
            public int SkipsRemaining;
            public int NightsWithoutVote;
            public int HandledNightId;
            public bool NaturalTonight;
        }

        internal struct ClockInput
        {
            public float Hour;
            public int Year;
            public int Month;
            public int Day;
            public float VoteStart;
            public bool SkipMultiple;
            public bool VoteActive;
            public bool WindowOpen;
            public bool VotedThisNight;
            public bool NaturalTonight;
            public int NightsWithoutVote;
            public int HandledNightId;
        }

        internal struct ClockResult
        {
            public bool SetMorning;
            public string Action;
            public bool VotedThisNight;
            public bool NaturalTonight;
            public bool WindowOpen;
            public int NightsWithoutVote;
            public int HandledNightId;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            JObject raw = null;
            try { raw = Config.ReadObject<JObject>(); }
            catch { raw = null; }
            try { _config = raw == null ? new PluginConfig() : raw.ToObject<PluginConfig>() ?? new PluginConfig(); }
            catch { _config = new PluginConfig(); }
            ApplyConfigMigrations(raw, _config);
            NormalizeConfig(_config);
            SaveConfig();
            if (_ready) OnConfigReloaded();
        }

        private static void NormalizeConfig(PluginConfig config)
        {
            if (config == null) return;
            config.PanelAnchor = NightshadeVoteDayRules.CleanAnchor(config.PanelAnchor);
            // Hud is under the inventory blur. Hud.Menu and Inventory do not draw a
            // plugin panel on this build. 1.5.0 was visible because it used Overlay.
            config.ClosedLayer = NightshadeVoteDayRules.OpenLayerName;
            config.OpenLayer = NightshadeVoteDayRules.OpenLayerName;
            if (config.OpenRight == 0f || float.IsNaN(config.OpenRight) || float.IsInfinity(config.OpenRight))
                config.OpenRight = NightshadeVoteDayRules.DefaultOpenRight;
            if (string.IsNullOrWhiteSpace(config.ThemeMode)) config.ThemeMode = "Shop";
            if (string.IsNullOrWhiteSpace(config.ThemeFile)) config.ThemeFile = "NightshadeShop.json";
            if (string.IsNullOrWhiteSpace(config.PanelTitle)) config.PanelTitle = "Skip Night";
            config.CursorKey = NightshadeVoteDayRules.NormalizeCursorKey(config.CursorKey);
            if (string.IsNullOrWhiteSpace(config.PanelColor)) config.PanelColor = "#1A1024";
            if (string.IsNullOrWhiteSpace(config.AccentColor)) config.AccentColor = "#A267D3";
            if (string.IsNullOrWhiteSpace(config.TextColor)) config.TextColor = "#F2EAF7";
            if (string.IsNullOrWhiteSpace(config.MutedColor)) config.MutedColor = "#A898B8";
            if (string.IsNullOrWhiteSpace(config.SuccessColor)) config.SuccessColor = "#3DDC84";
            if (string.IsNullOrWhiteSpace(config.FailColor)) config.FailColor = "#D58D7C";
            if (string.IsNullOrWhiteSpace(config.SunColor)) config.SunColor = "#E2B968";
            if (string.IsNullOrWhiteSpace(config.TrackColor)) config.TrackColor = "#120A1C";
            if (config.PanelLogoUrl == null) config.PanelLogoUrl = "";
            else config.PanelLogoUrl = config.PanelLogoUrl.Trim();
            NightshadeVoteDayRules.MigratePanelSize(config);
            if (float.IsNaN(config.PanelWidth) || config.PanelWidth < 120f) config.PanelWidth = NightshadeVoteDayRules.DefaultWidth;
            if (config.PanelWidth > 420f) config.PanelWidth = 420f;
            if (float.IsNaN(config.PanelHeight) || config.PanelHeight < NightshadeVoteDayRules.DefaultHeight) config.PanelHeight = NightshadeVoteDayRules.DefaultHeight;
            if (config.PanelHeight > 320f) config.PanelHeight = 320f;
            if (float.IsNaN(config.CollapsedSize) || config.CollapsedSize < 40f) config.CollapsedSize = NightshadeVoteDayRules.DefaultCollapsed;
            if (config.CollapsedSize > 96f) config.CollapsedSize = 96f;
            if (float.IsNaN(config.PanelGap) || config.PanelGap < 0f) config.PanelGap = 0f;
            if (config.PanelOpacity < 0f || float.IsNaN(config.PanelOpacity)) config.PanelOpacity = 0f;
            if (config.PanelOpacity > 100f) config.PanelOpacity = 100f;
            if (float.IsNaN(config.ResultHoldSeconds) || config.ResultHoldSeconds < 0f) config.ResultHoldSeconds = 0f;
            if (float.IsNaN(config.HotbarLeft) || float.IsInfinity(config.HotbarLeft)) config.HotbarLeft = NightshadeVoteDayRules.DefaultHotbarLeft;
            if (float.IsNaN(config.PanelBottom) || float.IsInfinity(config.PanelBottom)) config.PanelBottom = NightshadeVoteDayRules.DefaultBottom;
            if (float.IsNaN(config.OpenRight) || float.IsInfinity(config.OpenRight)) config.OpenRight = NightshadeVoteDayRules.DefaultOpenRight;
            if (float.IsNaN(config.OpenBottom) || float.IsInfinity(config.OpenBottom)) config.OpenBottom = NightshadeVoteDayRules.DefaultOpenBottom;
        }

        protected override void LoadDefaultConfig() => _config = new PluginConfig();
        protected override void SaveConfig()         => Config.WriteObject(_config);

        internal static void ApplyConfigMigrations(JObject raw, PluginConfig config)
        {
            if (config == null) return;
            bool hasNewMode = raw != null && raw[ModeKey] != null && raw[ModeKey].Type != JTokenType.Null;
            bool hasNewNights = raw != null && raw[QuietNightsKey] != null && raw[QuietNightsKey].Type != JTokenType.Null;
            string oldMode = raw != null && raw[OldModeKey] != null && raw[OldModeKey].Type != JTokenType.Null ? raw[OldModeKey].ToString() : null;
            string oldNights = null;
            if (raw != null && raw[OldNightsKey] != null && raw[OldNightsKey].Type != JTokenType.Null)
            {
                int oldValue;
                if (TryReadInt(raw[OldNightsKey], out oldValue))
                    oldNights = oldValue.ToString(CultureInfo.InvariantCulture);
            }
            ApplyMigratedValues(hasNewMode, oldMode, hasNewNights, oldNights, config);
            if (config.ConfigVersion < NightshadeVoteDayRules.CurrentConfigVersion)
            {
                MigrateLogo(raw, config);
                MigratePanelLayers(raw, config);
                MigrateOpenLayer(raw, config);
                // 1.6.x stored Hud / Hud.Menu / Inventory here. None of those draw
                // this card the way Overlay did in 1.5.0. One layer from here on.
                config.ClosedLayer = NightshadeVoteDayRules.OpenLayerName;
                config.OpenLayer = NightshadeVoteDayRules.OpenLayerName;
                if (config.OpenRight == 0f) config.OpenRight = NightshadeVoteDayRules.DefaultOpenRight;
                config.ConfigVersion = NightshadeVoteDayRules.CurrentConfigVersion;
            }
        }

        internal static void MigrateLogo(JObject raw, PluginConfig config)
        {
            if (config == null) return;
            if (!HasConfigKey(raw, NightshadeVoteDayRules.LogoKey))
            {
                config.PanelLogoUrl = "";
                return;
            }
            string url = ConfigText(raw[NightshadeVoteDayRules.LogoKey]);
            if (url != null && string.Equals(url.Trim(), LegacyLogoUrl, StringComparison.Ordinal))
                config.PanelLogoUrl = "";
        }

        internal static void MigratePanelLayers(JObject raw, PluginConfig config)
        {
            if (config == null) return;
            bool hasClosed = HasConfigKey(raw, NightshadeVoteDayRules.ClosedLayerKey);
            bool hasOpen = HasConfigKey(raw, NightshadeVoteDayRules.OpenLayerKey)
                || HasConfigKey(raw, NightshadeVoteDayRules.OpenLayerKeyV3)
                || HasConfigKey(raw, NightshadeVoteDayRules.OpenLayerKeyV2);
            if (hasClosed && hasOpen) return;
            string legacy = null;
            if (HasConfigKey(raw, NightshadeVoteDayRules.LayerKey))
                legacy = ConfigText(raw[NightshadeVoteDayRules.LayerKey]);
            else if (HasConfigKey(raw, NightshadeVoteDayRules.OldLayerKey))
                legacy = ConfigText(raw[NightshadeVoteDayRules.OldLayerKey]);
            if (!hasClosed)
            {
                if (string.IsNullOrWhiteSpace(legacy) || IsStockPanelLayer(legacy))
                    config.ClosedLayer = NightshadeVoteDayRules.ClosedLayerName;
                else
                    config.ClosedLayer = legacy.Trim();
            }
            if (!hasOpen)
                config.OpenLayer = NightshadeVoteDayRules.OpenLayerName;
        }

        // 1.6.0 stock "Hud.Menu" sits under the inventory blur. 1.6.1 stock "Inventory"
        // does not draw a plugin panel on this build either. Both become Overlay.
        // A custom open layer (Overall, or anything else that is not one of those)
        // is left alone.
        internal static void MigrateOpenLayer(JObject raw, PluginConfig config)
        {
            if (config == null) return;
            string previous = null;
            if (HasConfigKey(raw, NightshadeVoteDayRules.OpenLayerKey))
                previous = ConfigText(raw[NightshadeVoteDayRules.OpenLayerKey]);
            else if (HasConfigKey(raw, NightshadeVoteDayRules.OpenLayerKeyV3))
                previous = ConfigText(raw[NightshadeVoteDayRules.OpenLayerKeyV3]);
            else if (HasConfigKey(raw, NightshadeVoteDayRules.OpenLayerKeyV2))
                previous = ConfigText(raw[NightshadeVoteDayRules.OpenLayerKeyV2]);
            else if (!string.IsNullOrWhiteSpace(config.OpenLayer))
                previous = config.OpenLayer;
            if (string.IsNullOrWhiteSpace(previous) || NightshadeVoteDayRules.IsBrokenOpenLayer(previous))
                config.OpenLayer = NightshadeVoteDayRules.OpenLayerName;
            else
                config.OpenLayer = previous.Trim();
        }

        private static bool HasConfigKey(JObject raw, string key)
        {
            return raw != null && raw[key] != null && raw[key].Type != JTokenType.Null;
        }

        private static string ConfigText(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            return token.Type == JTokenType.String ? (string)token : token.ToString();
        }

        private static bool IsStockPanelLayer(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            value = value.Trim();
            return value.Equals("Overlay", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Hud", StringComparison.OrdinalIgnoreCase);
        }

        internal static void ApplyMigratedValues(bool hasNewMode, string oldMode, bool hasNewNights, string oldNightsText, PluginConfig config)
        {
            if (config == null) return;
            if (!hasNewMode && !string.IsNullOrEmpty(oldMode))
                config.VoteMode = oldMode;
            if (!hasNewNights && !string.IsNullOrEmpty(oldNightsText))
            {
                double number;
                if (double.TryParse(oldNightsText, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    config.NightsWithoutVote = (int)number;
            }
            config.VoteMode = NormalizeMode(config.VoteMode);
            if (config.NightsWithoutVote < 0) config.NightsWithoutVote = 2;
        }

        private static bool TryReadInt(JToken token, out int value)
        {
            value = 0;
            if (token == null) return false;
            double number;
            if (!double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                return false;
            value = (int)number;
            return true;
        }

        // --- Lang ---
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["UiVotes"]        = "Votes required",
                ["UiVoteHint"]     = "Waiting on votes",
                ["UiWillSkip"]     = "Night will be skipped",
                ["UiQuietAfter"]   = "Skipped. {nights} quiet nights",
                ["UiFailed"]       = "Not enough votes",
                ["UiPurchased"]    = "Day purchased",
                ["UiVote"]         = "Vote",
                ["UiVoted"]        = "Voted",
                ["UiBuy"]          = "Buy Day {cost}",
                ["UiConfirm"]      = "Confirm",
                ["UiCancel"]       = "Cancel",
                ["UiPay"]          = "Pay {cost}?",
                ["UiHintMiddle"]   = "Middle-click buttons",
                ["UiHintUse"]      = "Press E for buttons",
                ["UiHintReload"]   = "Press R for buttons",
                ["UiHintReady"]    = "Click Vote or Buy",
                ["UiCounted"]      = "Vote counted",
                ["UiAlready"]      = "Already voted",
                ["UiNeedRp"]       = "Need {need} RP, have {have}",
                ["UiNeedEco"]      = "Need {need} coins, have {have}",
                ["UiClosed"]       = "Vote is closed",
                ["UiDaytime"]      = "Already daytime",
                ["UiQuiet"]        = "No vote tonight",
                ["UiQuietLeft"]    = "No vote. {left} nights left",
                ["UiNotYet"]       = "Vote is not open",
                ["UiDone"]         = "Vote already finished",
                ["UiCurrency"]     = "Currency is not set up",
                ["UiNoRewards"]    = "Rewards plugin missing",
                ["UiNoEco"]        = "Economics plugin missing",
            }, this);
        }

        // --- State ---
        private bool            _voteActive     = false;
        private bool            _votedThisNight = false;
        private bool            _windowOpen     = false;
        private bool            _naturalTonight = false;
        private int             _nightsWithoutVote = 0;
        private int             _handledNightId = 0;
        private Timer           _windowTimer;
        private Timer           _voteTimer;
        private Timer           _uiTimer;
        private Timer           _holdTimer;
        private HashSet<ulong>  _votes          = new HashSet<ulong>();
        private bool            _ready;
        private bool            _panelOpen;
        private bool            _holdingResult;
        private bool            _priceReadFailed;
        private string          _phase = NightshadeVoteDayRules.PhaseVoting;
        private string          _buyerName = string.Empty;
        private float           _voteEndsAt;
        private int             _displayVotes;
        private int             _displayNeeded;
        private string          _shownPhase;
        private int             _shownSeconds = int.MinValue;
        private int             _shownVotes = int.MinValue;
        private int             _shownNeeded = int.MinValue;
        private string          _shownCost = string.Empty;
        private Dictionary<string, bool> _collapsed = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly HashSet<ulong> _cursor = new HashSet<ulong>();
        private readonly HashSet<ulong> _confirming = new HashSet<ulong>();
        private readonly Dictionary<ulong, float> _uiNext = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, Note> _notes = new Dictionary<ulong, Note>();
        private Palette _palette;
        private int _themeDay = -1;
        private DateTime _themeStamp = DateTime.MinValue;
        private string _themeModeSeen = string.Empty;
        private bool _themeWarned;
        private bool _warnedRewards;
        private bool _warnedEco;
        private bool _warnedCurrency;

        private class Note
        {
            public string Text = string.Empty;
            public string Kind = "idle";
        }

        // --- Plugin references ---
        [PluginReference] Plugin ServerRewards;
        [PluginReference] Plugin Economics;

        // --- Hooks ---

        void OnServerInitialized()
        {
            LoadSkips();
            LoadPanel();
            _ready = true;
            int version = _config == null ? 0 : _config.ConfigVersion;
            Puts("Config version " + version.ToString(CultureInfo.InvariantCulture)
                + ". Skip Night is one Overlay panel left of the hotbar. It is created when a vote starts, and removed when that vote ends.");
            timer.Every(10f, CheckGameTime);
            timer.Once(1f, CheckGameTime);
        }

        void Unload()
        {
            _ready = false;
            _windowTimer?.Destroy();
            _voteTimer?.Destroy();
            ClosePanel();
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null || !_panelOpen) return;
            NextTick(() => ShowIfOpen(player));
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null || !_panelOpen) return;
            NextTick(() => ShowIfOpen(player));
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            if (player == null || !_panelOpen) return;
            NextTick(() => ShowIfOpen(player));
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            _cursor.Remove(player.userID);
            _confirming.Remove(player.userID);
            _uiNext.Remove(player.userID);
            _notes.Remove(player.userID);
            DestroyPanel(player);
        }

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (!_panelOpen || player == null || input == null || _config == null) return;
            if (_phase != NightshadeVoteDayRules.PhaseVoting) return;
            BUTTON button;
            if (!TryCursorButton(_config.CursorKey, out button)) return;
            if (!input.WasJustPressed(button)) return;
            if (player.IsSleeping() || player.IsDead()) return;
            if (player.inventory != null && player.inventory.loot != null && player.inventory.loot.IsLooting()) return;
            input.SwallowButton(button);
            ToggleCursor(player);
        }

        // --- Time watcher ---

        private void CheckGameTime()
        {
            TOD_Sky sky = TOD_Sky.Instance;
            if (sky == null || sky.Cycle == null) return;
            float hour = sky.Cycle.Hour;
            if (hour < 0f || float.IsNaN(hour)) return;

            bool windowWasOpen = _windowOpen;
            ClockResult result = Advance(new ClockInput
            {
                Hour = hour,
                Year = sky.Cycle.Year,
                Month = sky.Cycle.Month,
                Day = sky.Cycle.Day,
                VoteStart = _config.VoteStartTime,
                SkipMultiple = IsSkipMultiple(),
                VoteActive = _voteActive,
                WindowOpen = _windowOpen,
                VotedThisNight = _votedThisNight,
                NaturalTonight = _naturalTonight,
                NightsWithoutVote = _nightsWithoutVote,
                HandledNightId = _handledNightId
            });

            _votedThisNight = result.VotedThisNight;
            _naturalTonight = result.NaturalTonight;
            _windowOpen = result.WindowOpen;
            _nightsWithoutVote = result.NightsWithoutVote;
            _handledNightId = result.HandledNightId;

            if (result.Action == "daytime")
            {
                if (windowWasOpen) _windowTimer?.Destroy();
                if (!_voteActive && _panelOpen) ClosePanel();
                return;
            }
            if (result.Action == "natural")
            {
                SaveSkips();
                Puts("Quiet night. Nights still queued after this one: " + _nightsWithoutVote.ToString(CultureInfo.InvariantCulture) + ".");
                return;
            }
            if (result.Action == "vote")
            {
                SaveSkips();
                OpenWindow();
            }
        }

        internal static ClockResult Advance(ClockInput s)
        {
            ClockResult result = new ClockResult
            {
                SetMorning = false,
                Action = "none",
                VotedThisNight = s.VotedThisNight,
                NaturalTonight = s.NaturalTonight,
                WindowOpen = s.WindowOpen,
                NightsWithoutVote = s.NightsWithoutVote < 0 ? 0 : s.NightsWithoutVote,
                HandledNightId = s.HandledNightId
            };
            float hour = s.Hour;
            if (float.IsNaN(hour) || hour < 0f) return result;

            float voteStart = s.VoteStart <= 0f ? 19f : s.VoteStart;
            if (hour >= 6f && hour < voteStart)
            {
                result.Action = "daytime";
                result.VotedThisNight = false;
                result.NaturalTonight = false;
                result.WindowOpen = false;
                return result;
            }
            if (hour < voteStart) return result;

            int nightId = NightId(s.Year, s.Month, s.Day, hour, voteStart);
            if (nightId != 0 && nightId == s.HandledNightId) return result;
            if (s.VoteActive || s.WindowOpen) return result;

            result.HandledNightId = nightId;
            if (!s.SkipMultiple)
                result.NightsWithoutVote = 0;
            if (s.SkipMultiple && result.NightsWithoutVote > 0)
            {
                result.NightsWithoutVote = result.NightsWithoutVote - 1;
                result.VotedThisNight = true;
                result.NaturalTonight = true;
                result.WindowOpen = false;
                result.Action = "natural";
                result.SetMorning = false;
                return result;
            }

            result.NaturalTonight = false;
            result.Action = "vote";
            result.SetMorning = false;
            return result;
        }

        internal static int NightId(int year, int month, int day, float hour, float voteStart)
        {
            if (year < 1 || month < 1 || month > 12 || day < 1 || day > 31) return 0;
            DateTime date;
            try { date = new DateTime(year, month, day); }
            catch (ArgumentOutOfRangeException) { return 0; }
            if (hour < 6f && hour < voteStart)
                date = date.AddDays(-1);
            return date.Year * 10000 + date.Month * 100 + date.Day;
        }

        internal static int QuietNightsAfterPurchase(int nightsWithoutVote)
        {
            return nightsWithoutVote < 0 ? 0 : nightsWithoutVote;
        }

        internal static void OnVotePassed(bool skipMultiple, int configuredQuietNights, out int nightsWithoutVote, out bool setMorning)
        {
            setMorning = true;
            nightsWithoutVote = skipMultiple ? Math.Max(0, configuredQuietNights) : 0;
        }

        internal static SkipState MigrateSkipState(SkipState state)
        {
            if (state == null)
                return new SkipState { Version = SkipStateVersion, SkipsRemaining = 0 };
            if (state.Version >= SkipStateVersion)
            {
                if (state.NightsWithoutVote < 0) state.NightsWithoutVote = 0;
                state.SkipsRemaining = 0;
                state.Version = SkipStateVersion;
                return state;
            }
            int queued = state.SkipsRemaining > 0 ? state.SkipsRemaining : 0;
            return new SkipState
            {
                Version = SkipStateVersion,
                SkipsRemaining = 0,
                NightsWithoutVote = queued,
                HandledNightId = 0,
                NaturalTonight = false
            };
        }

        // --- Window & vote lifecycle ---

        private void OpenWindow()
        {
            _windowOpen = true;
            _naturalTonight = false;
            RememberNight();
            StartVote();

            _windowTimer = timer.Once(_config.WindowDuration, () =>
            {
                _windowOpen     = false;
                _votedThisNight = true;
            });
        }

        private void StartVote()
        {
            _voteActive = true;
            _votes.Clear();
            _phase = NightshadeVoteDayRules.PhaseVoting;
            _buyerName = string.Empty;
            _voteEndsAt = Time.realtimeSinceStartup + Math.Max(0f, _config.VoteDuration);

            int needed = NeededVotes();
            Puts("Vote started. Need " + needed.ToString(CultureInfo.InvariantCulture) + " votes.");

            _voteTimer = timer.Once(_config.VoteDuration, EndVote);
            BeginVotePanel();
        }

        private void EndVote()
        {
            _voteTimer?.Destroy();
            _windowTimer?.Destroy();

            _voteActive     = false;
            _windowOpen     = false;
            _votedThisNight = true;
            _naturalTonight = false;

            int    pop      = BasePlayer.activePlayerList.Count;
            int    votes    = _votes.Count;
            float  ratio    = pop > 0 ? (float)votes / pop : 0f;
            bool   passed   = ratio >= _config.VoteThreshold;
            int    needed   = NeededVotes();
            FreezeVotes(votes, needed);
            string daytime  = FormatHour(_config.DayTime);

            if (passed)
            {
                int quiet;
                bool setMorning;
                OnVotePassed(IsSkipMultiple(), _config.NightsWithoutVote, out quiet, out setMorning);
                _nightsWithoutVote = quiet;
                RememberNight();
                SaveSkips();
                if (setMorning) ApplyDay();
                Puts("Vote passed. " + votes.ToString(CultureInfo.InvariantCulture) + "/" + pop.ToString(CultureInfo.InvariantCulture)
                    + ". Time set to " + daytime + ". Quiet nights after this one: " + quiet.ToString(CultureInfo.InvariantCulture) + ".");
            }
            else
            {
                RememberNight();
                SaveSkips();
                Puts("Vote failed. " + votes.ToString(CultureInfo.InvariantCulture) + "/" + pop.ToString(CultureInfo.InvariantCulture)
                    + ". Needed " + needed.ToString(CultureInfo.InvariantCulture) + ".");
            }

            _votes.Clear();
            _phase = passed ? NightshadeVoteDayRules.PhasePassed : NightshadeVoteDayRules.PhaseFailed;
            _buyerName = string.Empty;
            BeginResultPanel();
        }

        private void ForceDayByPurchase(BasePlayer buyer)
        {
            _voteTimer?.Destroy();
            _windowTimer?.Destroy();
            _voteActive     = false;
            _windowOpen     = false;
            _votedThisNight = true;
            _naturalTonight = false;
            FreezeVotes(_votes.Count, NeededVotes());
            _votes.Clear();
            _phase = NightshadeVoteDayRules.PhaseBought;
            _buyerName = NightshadeVoteDayRules.CleanName(buyer == null ? null : buyer.displayName);
            _nightsWithoutVote = QuietNightsAfterPurchase(_nightsWithoutVote);
            RememberNight();
            SaveSkips();
            ApplyDay();

            string who = buyer == null ? "Someone" : buyer.displayName;
            Puts(who + " bought the day for " + CostLabel() + ". Time set to " + FormatHour(_config.DayTime) + ".");
            BeginResultPanel();
        }

        private void ApplyDay()
        {
            ConsoleSystem.Run(ConsoleSystem.Option.Server, "env.time " + _config.DayTime.ToString(CultureInfo.InvariantCulture));
        }

        private void RememberNight()
        {
            TOD_Sky sky = TOD_Sky.Instance;
            if (sky == null || sky.Cycle == null) return;
            int nightId = NightId(sky.Cycle.Year, sky.Cycle.Month, sky.Cycle.Day, sky.Cycle.Hour, _config.VoteStartTime);
            if (nightId != 0) _handledNightId = nightId;
        }

        // --- Vote and buy from the panel ---

        private void TryCastVote(BasePlayer player)
        {
            if (player == null) return;
            if (!_voteActive)
            {
                if (_naturalTonight)
                {
                    if (_nightsWithoutVote > 0)
                        SetNote(player, Ui("UiQuietLeft", ("left", _nightsWithoutVote.ToString(CultureInfo.InvariantCulture))), "idle");
                    else
                        SetNote(player, Ui("UiQuiet"), "idle");
                }
                else if (_votedThisNight)
                    SetNote(player, Ui("UiDone"), "idle");
                else
                    SetNote(player, Ui("UiNotYet"), "idle");
                Show(player);
                return;
            }

            if (_votes.Contains(player.userID))
            {
                SetNote(player, Ui("UiAlready"), "ok");
                Show(player);
                return;
            }

            _votes.Add(player.userID);
            _confirming.Remove(player.userID);
            if (!_config.CursorWhileExpanded) _cursor.Remove(player.userID);

            int needed = NeededVotes();
            int voteCount = _votes.Count;
            if (voteCount >= needed)
            {
                _voteTimer?.Destroy();
                EndVote();
                return;
            }
            SetNote(player, Ui("UiCounted"), "ok");
            TickPanel();
            Show(player);
        }

        private void AskBuy(BasePlayer player)
        {
            if (player == null || _confirming.Contains(player.userID)) return;
            if (!CanBuy(player)) return;
            _confirming.Add(player.userID);
            _cursor.Add(player.userID);
            ClearNote(player);
            Show(player);
        }

        private void CancelBuy(BasePlayer player)
        {
            if (player == null || !_confirming.Remove(player.userID)) return;
            Show(player);
        }

        private void ConfirmBuy(BasePlayer player)
        {
            if (player == null || !_confirming.Contains(player.userID)) return;
            _confirming.Remove(player.userID);
            if (!CanBuy(player))
            {
                Show(player);
                return;
            }
            string mode = _config.Currency == null ? string.Empty : _config.Currency.ToLowerInvariant();
            if (!TryCharge(player, mode))
            {
                Show(player);
                return;
            }
            _cursor.Remove(player.userID);
            ForceDayByPurchase(player);
        }

        private bool CanBuy(BasePlayer player)
        {
            RefreshPriceFromDisk();
            float t = TOD_Sky.Instance == null || TOD_Sky.Instance.Cycle == null ? -1f : TOD_Sky.Instance.Cycle.Hour;
            string mode = _config.Currency == null ? string.Empty : _config.Currency.ToLowerInvariant();
            bool isNight = (t >= _config.VoteStartTime) || (t >= 0f && t < 6f);
            if (!isNight)
            {
                SetNote(player, Ui("UiDaytime"), "bad");
                Show(player);
                return false;
            }
            if (_votedThisNight && !_voteActive)
            {
                if (_naturalTonight)
                {
                    if (_nightsWithoutVote > 0)
                        SetNote(player, Ui("UiQuietLeft", ("left", _nightsWithoutVote.ToString(CultureInfo.InvariantCulture))), "idle");
                    else
                        SetNote(player, Ui("UiQuiet"), "idle");
                }
                else SetNote(player, Ui("UiClosed"), "bad");
                Show(player);
                return false;
            }
            if (mode == "serverrewards" && ServerRewards == null)
            {
                if (!_warnedRewards)
                {
                    _warnedRewards = true;
                    PrintWarning("ServerRewards is not loaded. Buying the day is unavailable.");
                }
                SetNote(player, Ui("UiNoRewards"), "bad");
                Show(player);
                return false;
            }
            if (mode == "economics" && Economics == null)
            {
                if (!_warnedEco)
                {
                    _warnedEco = true;
                    PrintWarning("Economics is not loaded. Buying the day is unavailable.");
                }
                SetNote(player, Ui("UiNoEco"), "bad");
                Show(player);
                return false;
            }
            if (mode != "serverrewards" && mode != "economics")
            {
                if (!_warnedCurrency)
                {
                    _warnedCurrency = true;
                    PrintWarning("Day buy currency must be ServerRewards or Economics. Current value: " + (string.IsNullOrEmpty(_config.Currency) ? "(empty)" : _config.Currency) + ".");
                }
                SetNote(player, Ui("UiCurrency"), "bad");
                Show(player);
                return false;
            }
            return true;
        }

        // --- Currency helpers ---

        private bool TryCharge(BasePlayer player, string mode)
        {
            if (mode == "serverrewards") return TryChargeRP(player);
            if (mode == "economics")     return TryChargeEco(player);
            return false;
        }

        private bool TryChargeRP(BasePlayer player)
        {
            int balance = (int)(ServerRewards.Call("CheckPoints", player.userID) ?? 0);
            if (balance < _config.RPCost)
            {
                SetNote(player, Ui("UiNeedRp",
                    ("need", _config.RPCost.ToString(CultureInfo.InvariantCulture)),
                    ("have", balance.ToString(CultureInfo.InvariantCulture))), "bad");
                return false;
            }
            ServerRewards.Call("TakePoints", player.userID, _config.RPCost);
            return true;
        }

        private bool TryChargeEco(BasePlayer player)
        {
            double balance = (double)(Economics.Call("Balance", player.UserIDString) ?? 0.0);
            if (balance < _config.EcoCost)
            {
                SetNote(player, Ui("UiNeedEco",
                    ("need", _config.EcoCost.ToString(CultureInfo.InvariantCulture)),
                    ("have", balance.ToString(CultureInfo.InvariantCulture))), "bad");
                return false;
            }
            Economics.Call("Withdraw", player.UserIDString, _config.EcoCost);
            return true;
        }

        private string CostLabel()
        {
            return NightshadeVoteDayRules.FormatCost(_config.Currency, _config.RPCost, _config.EcoCost);
        }

        private int NeededVotes()
        {
            int pop = BasePlayer.activePlayerList == null ? 0 : BasePlayer.activePlayerList.Count;
            return NightshadeVoteDayRules.Needed(pop, _config.VoteThreshold);
        }

        private void FreezeVotes(int votes, int needed)
        {
            _displayVotes = votes < 0 ? 0 : votes;
            _displayNeeded = needed < 0 ? 0 : needed;
        }

        // --- Utility ---

        private bool IsSkipMultiple()
        {
            return !string.Equals(NormalizeMode(_config.VoteMode), "Daily", StringComparison.Ordinal);
        }

        private static string NormalizeMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return "SkipMultiple";
            mode = mode.Trim();
            if (string.Equals(mode, "Daily", StringComparison.OrdinalIgnoreCase)) return "Daily";
            if (string.Equals(mode, "SkipMultiple", StringComparison.OrdinalIgnoreCase)) return "SkipMultiple";
            return "SkipMultiple";
        }

        private void LoadSkips()
        {
            SkipState loaded = null;
            try { loaded = Interface.Oxide.DataFileSystem.ReadObject<SkipState>(SkipDataPath); }
            catch { loaded = null; }
            bool legacy = loaded != null && loaded.Version < SkipStateVersion;
            SkipState state = MigrateSkipState(loaded);
            _nightsWithoutVote = state.NightsWithoutVote;
            _handledNightId = state.HandledNightId;
            _naturalTonight = state.NaturalTonight;
            if (loaded == null || legacy || loaded.SkipsRemaining != 0 || loaded.Version < SkipStateVersion)
                SaveSkips();
        }

        private void SaveSkips()
        {
            if (_nightsWithoutVote < 0) _nightsWithoutVote = 0;
            Interface.Oxide.DataFileSystem.WriteObject(SkipDataPath, new SkipState
            {
                Version = SkipStateVersion,
                SkipsRemaining = 0,
                NightsWithoutVote = _nightsWithoutVote,
                HandledNightId = _handledNightId,
                NaturalTonight = _naturalTonight
            });
        }

        private string FormatHour(float hour)
        {
            int h = (int)hour % 24;
            if (h < 0) h += 24;
            return h.ToString("00", CultureInfo.InvariantCulture) + "00";
        }

        // --- Panel ---

        private class PanelStore
        {
            public Dictionary<string, bool> Collapsed = new Dictionary<string, bool>(StringComparer.Ordinal);
        }

        private void OnConfigReloaded()
        {
            bool period = _voteActive || _holdingResult;
            if (_config == null || !_config.ShowPanel || !period)
            {
                if (_panelOpen) ClosePanel();
                return;
            }
            _themeDay = -1;
            _warnedRewards = false;
            _warnedEco = false;
            _warnedCurrency = false;
            Puts("Vote panel config reloaded. Day buy price is " + CostLabel() + ".");
            RebuildAll();
        }

        private void BeginVotePanel()
        {
            if (_holdTimer != null) { _holdTimer.Destroy(); _holdTimer = null; }
            _holdingResult = false;
            _cursor.Clear();
            _confirming.Clear();
            _notes.Clear();
            if (_config == null || !_config.ShowPanel)
            {
                ClosePanel();
                return;
            }
            _panelOpen = true;
            if (_uiTimer == null) _uiTimer = timer.Every(1f, TickPanel);
            RebuildAll();
            Puts(PanelSentMessage());
        }

        private void BeginResultPanel()
        {
            _holdingResult = true;
            _notes.Clear();
            if (_config == null || !_config.ShowPanel || _config.ResultHoldSeconds <= 0f)
            {
                _holdingResult = false;
                ClosePanel();
                return;
            }
            _panelOpen = true;
            if (_uiTimer == null) _uiTimer = timer.Every(1f, TickPanel);
            RebuildAll();
            if (_holdTimer != null) _holdTimer.Destroy();
            _holdTimer = timer.Once(_config.ResultHoldSeconds, delegate()
            {
                _holdingResult = false;
                ClosePanel();
            });
        }

        private void ClosePanel()
        {
            _panelOpen = false;
            _holdingResult = false;
            _cursor.Clear();
            _confirming.Clear();
            _notes.Clear();
            if (_holdTimer != null) { _holdTimer.Destroy(); _holdTimer = null; }
            if (_uiTimer != null) { _uiTimer.Destroy(); _uiTimer = null; }
            if (BasePlayer.activePlayerList == null) return;
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player != null) DestroyPanel(player);
            }
        }

        private static void DestroyPanel(BasePlayer player)
        {
            if (player == null) return;
            DestroyVoteNames(player, true);
        }

        // Roots and children from 1.5.0 through 1.6.3. The ".Inv" names are the
        // lower card. Destroying the root alone leaves that card up when its
        // pieces are not parented under the name the dict still holds.
        private static readonly string[] VoteUiNames = new string[]
        {
            Root,
            BgName,
            LogoName,
            SunName,
            SunName + "U",
            SunName + "D",
            SunName + "L",
            SunName + "R",
            TitleName,
            CountName,
            TrackName,
            FillName,
            VotesName,
            HaveName,
            StatusName,
            PriceName,
            VoteButton,
            VoteButton + ".T",
            BuyButton,
            BuyButton + ".T",
            ConfirmButton,
            ConfirmButton + ".T",
            CancelButton,
            CancelButton + ".T",
            ToggleName,
            "NightshadeVoteDay.Head",
            "NightshadeVoteDay.Rule",
            "NightshadeVoteDay.Chevron"
        };

        private static void DestroyVoteNames(BasePlayer player, bool liveCard)
        {
            if (player == null) return;
            for (int i = 0; i < VoteUiNames.Length; i++)
            {
                string name = VoteUiNames[i];
                if (string.IsNullOrEmpty(name)) continue;
                CuiHelper.DestroyUi(player, name + LegacyMenuSuffix);
                CuiHelper.DestroyUi(player, name + LegacyMenuSuffix + ".T");
                if (liveCard) CuiHelper.DestroyUi(player, name);
            }
        }

        private void ShowIfOpen(BasePlayer player)
        {
            if (player == null || !player.IsConnected || !_panelOpen) return;
            Show(player);
        }

        private void RebuildAll()
        {
            if (!_panelOpen) return;
            RefreshPriceFromDisk();
            RememberShown();
            EachPlayer(Show);
        }

        private void RememberShown()
        {
            _shownPhase = _phase;
            _shownSeconds = RemainingSeconds();
            _shownVotes = DisplayVotes();
            _shownNeeded = DisplayNeeded();
            _shownCost = CostLabel();
        }

        private void TickPanel()
        {
            if (!_panelOpen || _config == null) return;
            try
            {
                RefreshPriceFromDisk();
                Palette previous = _palette;
                EnsurePalette();
                if (previous == null || _palette == null || previous.Name != _palette.Name || previous.Accent != _palette.Accent || previous.Background != _palette.Background)
                {
                    RebuildAll();
                    return;
                }
                if (_phase != _shownPhase)
                {
                    RebuildAll();
                    return;
                }
                int seconds = RemainingSeconds();
                int votes = DisplayVotes();
                int needed = DisplayNeeded();
                string cost = CostLabel();
                bool secondsChanged = seconds != _shownSeconds;
                bool votesChanged = votes != _shownVotes || needed != _shownNeeded;
                bool priceChanged = cost != _shownCost;
                // Clear the lower 1.6.x card even when the hotbar card does not need a redraw.
                EachPlayer(delegate(BasePlayer player) { DestroyVoteNames(player, false); });
                if (!secondsChanged && !votesChanged && !priceChanged) return;
                if (priceChanged)
                {
                    _shownSeconds = seconds;
                    _shownVotes = votes;
                    _shownNeeded = needed;
                    _shownCost = cost;
                    EachPlayer(Show);
                    return;
                }
                string headline = NightshadeVoteDayRules.Headline(_phase, seconds, _buyerName);
                string have = NightshadeVoteDayRules.VotesLabel(votes, needed);
                float fraction = NightshadeVoteDayRules.VoteFraction(votes, needed);
                int font = _phase == NightshadeVoteDayRules.PhaseVoting ? 15 : HeadlineFont(headline);
                string countColor = HeadlineColor();
                string secondText = seconds.ToString(CultureInfo.InvariantCulture);
                string ink = Hex(P().Text);
                EachPlayer(delegate(BasePlayer player)
                {
                    bool collapsed = IsCollapsed(player);
                    var container = new CuiElementContainer();
                    if (secondsChanged)
                    {
                        if (collapsed)
                            AddTextUpdate(container, CountName, secondText, ink, 12, TextAnchor.MiddleLeft, true);
                        else
                            AddTextUpdate(container, CountName, headline, countColor, font, TextAnchor.MiddleCenter, true);
                    }
                    if (!collapsed && votesChanged)
                    {
                        AddFill(container, fraction, false);
                        AddStretchText(container, VotesName, TrackName, Ui("UiVotes"), ink, 8, TextAnchor.MiddleLeft, true, "4 0", "-34 0", false);
                        AddEdgeText(container, HaveName, TrackName, have, ink, 8, TextAnchor.MiddleRight, true, "-40 0", "-4 0", false);
                    }
                    if (container.Count > 0) CuiHelper.AddUi(player, container);
                });
                _shownSeconds = seconds;
                _shownVotes = votes;
                _shownNeeded = needed;
                _shownCost = cost;
            }
            catch (Exception ex)
            {
                PrintWarning("Vote panel refresh failed: " + ex.Message);
            }
        }

        private void RefreshPriceFromDisk()
        {
            if (_config == null || Config == null) return;
            try
            {
                string path = Config.Filename;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                string json = File.ReadAllText(path);
                string currency = _config.Currency;
                int rp = _config.RPCost;
                double eco = _config.EcoCost;
                if (!NightshadeVoteDayRules.ApplyLiveCostsJson(json, ref currency, ref rp, ref eco)) return;
                _config.Currency = currency;
                _config.RPCost = rp;
                _config.EcoCost = eco;
                _priceReadFailed = false;
            }
            catch (Exception ex)
            {
                if (_priceReadFailed) return;
                _priceReadFailed = true;
                PrintWarning("Day buy price could not be read from the config file. The panel will keep the last good price. " + ex.Message);
            }
        }

        private int RemainingSeconds()
        {
            if (_phase != NightshadeVoteDayRules.PhaseVoting) return 0;
            float left = _voteEndsAt - Time.realtimeSinceStartup;
            if (float.IsNaN(left) || left < 0f) return 0;
            return (int)Math.Ceiling(left);
        }

        private int DisplayVotes()
        {
            if (_phase == NightshadeVoteDayRules.PhaseVoting) return _votes.Count;
            return _displayVotes;
        }

        private int DisplayNeeded()
        {
            if (_phase == NightshadeVoteDayRules.PhaseVoting) return NeededVotes();
            return _displayNeeded;
        }

        private void Show(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            try
            {
                // One card only: the hotbar rect. Wipe the live tree and the lower
                // ".Inv" tree before the single AddUi so a stale second box cannot remain.
                DestroyVoteNames(player, true);
                var container = new CuiElementContainer();
                if (IsCollapsed(player)) BuildCollapsed(container, player, false);
                else BuildExpanded(container, player, false);
                if (container.Count > 0) CuiHelper.AddUi(player, container);
                else PrintWarning("Vote panel was empty and was not sent.");
            }
            catch (Exception ex)
            {
                PrintWarning("Vote panel could not be shown: " + ex.Message);
            }
        }

        private string PanelSentMessage()
        {
            float hotbar = _config == null ? NightshadeVoteDayRules.DefaultHotbarLeft : _config.HotbarLeft;
            float gap = _config == null ? NightshadeVoteDayRules.DefaultGap : _config.PanelGap;
            float bottom = _config == null ? NightshadeVoteDayRules.DefaultBottom : _config.PanelBottom;
            float width = _config == null ? NightshadeVoteDayRules.DefaultWidth : _config.PanelWidth;
            float height = _config == null ? NightshadeVoteDayRules.DefaultHeight : _config.PanelHeight;
            float right = hotbar - (float.IsNaN(gap) || gap < 0f ? 0f : gap);
            return "Skip Night panel sent on Overlay. One card. Right edge "
                + right.ToString("0.###", CultureInfo.InvariantCulture)
                + ", bottom " + bottom.ToString("0.###", CultureInfo.InvariantCulture)
                + ", size " + width.ToString("0.###", CultureInfo.InvariantCulture)
                + "x" + height.ToString("0.###", CultureInfo.InvariantCulture)
                + ". It stays up for this vote only.";
        }

        private void BuildExpanded(CuiElementContainer container, BasePlayer player, bool menu)
        {
            float width = _config.PanelWidth;
            bool voting = _phase == NightshadeVoteDayRules.PhaseVoting;
            float height = voting ? _config.PanelHeight : Math.Min(_config.PanelHeight, -NightshadeVoteDayRules.StatusBottom + 4f);
            NightshadeVoteDayRules.ScreenBox box = PanelBox(menu, width, height);
            AddRoot(container, box, menu, menu ? false : WantsCursor(player));
            AddRect(container, "NightshadeVoteDay.Head", BgName, Hex(P().Header), null, 0f, NightshadeVoteDayRules.HeaderBottom, width, 0f, menu);
            AddRect(container, "NightshadeVoteDay.Rule", BgName, Hex(P().Accent), null, 0f, NightshadeVoteDayRules.RuleBottom, width, NightshadeVoteDayRules.HeaderBottom, menu);
            string title = string.IsNullOrWhiteSpace(_config.PanelTitle) ? "Skip Night" : _config.PanelTitle.Trim();
            if (HasLogo())
            {
                AddLogo(container, 3f, -2f, 12f, menu);
                AddSun(container, 17f, -2f, 12f, menu);
                AddText(container, TitleName, BgName, title, Hex(P().Text), 10, TextAnchor.MiddleLeft, true, 32f, NightshadeVoteDayRules.HeaderBottom, width - 16f, -1f, menu);
            }
            else
            {
                AddSun(container, 2f, -2f, 12f, menu);
                AddText(container, TitleName, BgName, title, Hex(P().Text), 10, TextAnchor.MiddleCenter, true, 16f, NightshadeVoteDayRules.HeaderBottom, width - 16f, -1f, menu);
            }
            AddText(container, "NightshadeVoteDay.Chevron", BgName, "v", Hex(P().SubText), 10, TextAnchor.MiddleCenter, true, width - 14f, -14f, width - 2f, -1f, menu);
            AddToggle(container, width - 16f, -1f, 14f, 14f, menu);
            string headline = NightshadeVoteDayRules.Headline(_phase, RemainingSeconds(), _buyerName);
            int font = voting ? 15 : HeadlineFont(headline);
            AddText(container, CountName, BgName, headline, HeadlineColor(), font, TextAnchor.MiddleCenter, true, 4f, NightshadeVoteDayRules.CountBottom, width - 4f, NightshadeVoteDayRules.CountTop, menu);
            AddRect(container, TrackName, BgName, Hex(P().Card), null, 4f, NightshadeVoteDayRules.TrackBottom, width - 4f, NightshadeVoteDayRules.TrackTop, menu);
            AddFill(container, NightshadeVoteDayRules.VoteFraction(DisplayVotes(), DisplayNeeded()), menu);
            AddStretchText(container, VotesName, TrackName, Ui("UiVotes"), Hex(P().Text), 8, TextAnchor.MiddleLeft, true, "4 0", "-34 0", menu);
            AddEdgeText(container, HaveName, TrackName, NightshadeVoteDayRules.VotesLabel(DisplayVotes(), DisplayNeeded()), Hex(P().Text), 8, TextAnchor.MiddleRight, true, "-40 0", "-4 0", menu);
            string status;
            string kind;
            PlayerStatus(player, menu, out status, out kind);
            AddText(container, StatusName, BgName, status, KindColor(kind), 8, TextAnchor.MiddleCenter, false, 4f, NightshadeVoteDayRules.StatusBottom, width - 4f, NightshadeVoteDayRules.StatusTop, menu);
            if (!voting) return;
            bool voted = player != null && _votes.Contains(player.userID);
            AddActionButton(container, VoteButton, UiCommand + " vote", voted ? Hex(P().Card) : Hex(P().Accent), voted ? Ui("UiVoted") : Ui("UiVote"), voted ? Hex(P().Success) : Hex(P().AccentText), 4f, NightshadeVoteDayRules.VoteBottom, width - 4f, NightshadeVoteDayRules.VoteTop, menu);
            if (player != null && _confirming.Contains(player.userID))
            {
                float mid = 4f + (width - 8f) * 0.5f;
                AddActionButton(container, ConfirmButton, UiCommand + " confirm", Hex(P().Accent), Ui("UiConfirm"), Hex(P().AccentText), 4f, NightshadeVoteDayRules.ActionBottom, mid - 2f, NightshadeVoteDayRules.ActionTop, menu);
                AddActionButton(container, CancelButton, UiCommand + " cancel", Hex(P().Button), Ui("UiCancel"), Hex(P().Danger), mid + 2f, NightshadeVoteDayRules.ActionBottom, width - 4f, NightshadeVoteDayRules.ActionTop, menu);
            }
            else
            {
                AddActionButton(container, BuyButton, UiCommand + " buy", Hex(P().Button), BuyText(), Hex(P().ButtonText), 4f, NightshadeVoteDayRules.ActionBottom, width - 4f, NightshadeVoteDayRules.ActionTop, menu);
            }
        }

        private void BuildCollapsed(CuiElementContainer container, BasePlayer player, bool menu)
        {
            float width = _config.PanelWidth;
            float height = _config.CollapsedSize;
            NightshadeVoteDayRules.ScreenBox box = PanelBox(menu, width, height);
            AddRoot(container, box, menu, false);
            float sun = Math.Min(14f, Math.Max(11f, height * 0.35f));
            AddSun(container, 4f, -2f, sun, menu);
            float mid = -2f - sun;
            AddText(container, CountName, BgName, RemainingSeconds().ToString(CultureInfo.InvariantCulture), Hex(P().Text), 12, TextAnchor.MiddleLeft, true, 6f + sun, mid, 40f + sun, -2f, menu);
            AddText(container, PriceName, BgName, CostLabel(), Hex(P().Accent), 9, TextAnchor.MiddleLeft, true, 42f + sun, mid, width - 16f, -2f, menu);
            bool voted = player != null && _votes.Contains(player.userID);
            AddActionButton(container, VoteButton, UiCommand + " vote", voted ? Hex(P().Card) : Hex(P().Accent), voted ? Ui("UiVoted") : Ui("UiVote"), voted ? Hex(P().Success) : Hex(P().AccentText), 4f, 3f - height, 72f, mid - 2f, menu);
            AddText(container, "NightshadeVoteDay.Chevron", BgName, "^", Hex(P().SubText), 10, TextAnchor.MiddleCenter, true, width - 14f, -14f, width - 1f, -1f, menu);
            AddToggle(container, width - 16f, -1f, 14f, 14f, menu);
        }

        // Overlay is the layer the 1.5.0 panel used. It stays visible and clickable
        // over the inventory. Hud is covered by the inventory blur. Hud.Menu is under
        // that blur. The Inventory view does not draw a plugin panel on this build.
        private void AddRoot(CuiElementContainer container, NightshadeVoteDayRules.ScreenBox box, bool menu, bool cursor)
        {
            string root = UiId(Root, menu);
            string layer = NightshadeVoteDayRules.OpenLayerName;
            var element = new CuiElement
            {
                Name = root,
                Parent = layer,
                DestroyUi = root
            };
            element.Components.Add(new CuiImageComponent { Color = Hex(P().Accent), BlocksRaycast = true });
            element.Components.Add(new CuiRectTransformComponent { AnchorMin = box.Anchor, AnchorMax = box.Anchor, OffsetMin = box.OffsetMin, OffsetMax = box.OffsetMax });
            if (cursor) element.Components.Add(new CuiNeedsCursorComponent());
            container.Add(element);
            container.Add(new CuiPanel
            {
                Image = { Color = Hex(P().Background) },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "1 1", OffsetMax = "-1 -1" },
                CursorEnabled = false
            }, root, UiId(BgName, menu));
        }

        // One rect: left of the hotbar (bottom 16). The lower card used OpenBottom 0
        // and the ".Inv" names. menu must not select that rect.
        private NightshadeVoteDayRules.ScreenBox PanelBox(bool menu, float width, float height)
        {
            if (menu) return HotbarCard(width, height);
            return HotbarCard(width, height);
        }

        private NightshadeVoteDayRules.ScreenBox HotbarCard(float width, float height)
        {
            float hotbar = _config == null ? NightshadeVoteDayRules.DefaultHotbarLeft : _config.HotbarLeft;
            float gap = _config == null ? NightshadeVoteDayRules.DefaultGap : _config.PanelGap;
            float bottom = _config == null ? NightshadeVoteDayRules.DefaultBottom : _config.PanelBottom;
            string anchor = _config == null ? "0.5 0" : _config.PanelAnchor;
            return NightshadeVoteDayRules.LeftOfHotbar(hotbar, gap, bottom, width, height, anchor);
        }

        private bool HasLogo()
        {
            return _config != null && !string.IsNullOrWhiteSpace(_config.PanelLogoUrl);
        }

        private static string UiId(string name, bool menu)
        {
            // One tree. menu true used to append ".Inv". Both flags now share the 1.5.0 names.
            if (string.IsNullOrEmpty(name)) return name;
            return menu ? name : name;
        }

        private void AddLogo(CuiElementContainer container, float left, float top, float size, bool menu)
        {
            if (!HasLogo()) return;
            container.Add(new CuiElement
            {
                Name = UiId(LogoName, menu),
                Parent = UiId(BgName, menu),
                Components =
                {
                    new CuiRawImageComponent
                    {
                        Url = _config.PanelLogoUrl.Trim(),
                        Color = "1 1 1 1",
                        Sprite = "assets/content/textures/generic/fulltransparent.tga"
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "0 1",
                        OffsetMin = Px(left, top - size),
                        OffsetMax = Px(left + size, top)
                    }
                }
            });
        }

        private void AddSun(CuiElementContainer container, float left, float top, float size, bool menu)
        {
            string color = Hex(P().Accent);
            float body = size * 0.46f;
            float inset = (size - body) * 0.5f;
            float bodyLeft = left + inset;
            float bodyTop = top - inset;
            float bodyBottom = bodyTop - body;
            AddRect(container, SunName, BgName, color, "assets/icons/circle_closed.png", bodyLeft, bodyBottom, bodyLeft + body, bodyTop, menu);
            float thick = 1.5f;
            float ray = size * 0.2f;
            float cx = left + size * 0.5f;
            float mid = (bodyTop + bodyBottom) * 0.5f;
            AddRect(container, SunName + "U", BgName, color, null, cx - thick * 0.5f, bodyTop + 1f, cx + thick * 0.5f, bodyTop + 1f + ray, menu);
            AddRect(container, SunName + "D", BgName, color, null, cx - thick * 0.5f, bodyBottom - 1f - ray, cx + thick * 0.5f, bodyBottom - 1f, menu);
            AddRect(container, SunName + "L", BgName, color, null, bodyLeft - 1f - ray, mid - thick * 0.5f, bodyLeft - 1f, mid + thick * 0.5f, menu);
            AddRect(container, SunName + "R", BgName, color, null, bodyLeft + body + 1f, mid - thick * 0.5f, bodyLeft + body + 1f + ray, mid + thick * 0.5f, menu);
        }

        private void AddActionButton(CuiElementContainer container, string name, string command, string color, string text, string textColor, float left, float bottom, float right, float top, bool menu)
        {
            name = UiId(name, menu);
            container.Add(new CuiElement
            {
                Name = name,
                Parent = UiId(BgName, menu),
                DestroyUi = name,
                Components =
                {
                    new CuiButtonComponent { Command = command, Color = color },
                    new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = Px(left, bottom), OffsetMax = Px(right, top) }
                }
            });
            container.Add(new CuiElement
            {
                Name = name + ".T",
                Parent = name,
                Components =
                {
                    TextComponent(text, textColor, 9, TextAnchor.MiddleCenter, true),
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "1 0", OffsetMax = "-1 0" }
                }
            });
        }

        private void AddToggle(CuiElementContainer container, float left, float top, float width, float height, bool menu)
        {
            container.Add(new CuiElement
            {
                Name = UiId(ToggleName, menu),
                Parent = UiId(BgName, menu),
                Components =
                {
                    new CuiButtonComponent { Command = UiCommand + " toggle", Color = "0 0 0 0.02" },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "0 1",
                        OffsetMin = Px(left, top - height),
                        OffsetMax = Px(left + width, top)
                    }
                }
            });
        }

        private void AddFill(CuiElementContainer container, float fraction, bool menu)
        {
            float track = Math.Max(0f, _config.PanelWidth - 8f);
            float inner = Math.Max(0f, track - 2f);
            float width = inner * (fraction < 0f ? 0f : (fraction > 1f ? 1f : fraction));
            string color = _phase == NightshadeVoteDayRules.PhasePassed || _phase == NightshadeVoteDayRules.PhaseBought
                ? Hex(P().Success)
                : Hex(P().Accent);
            float right = width < 0.5f ? 1f : 1f + width;
            float top = width < 0.5f ? 1f : 12f;
            string fill = UiId(FillName, menu);
            container.Add(new CuiElement
            {
                Name = fill,
                Parent = UiId(TrackName, menu),
                DestroyUi = fill,
                Components =
                {
                    new CuiImageComponent { Color = width < 0.5f ? "0 0 0 0" : color },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "0 0",
                        OffsetMin = "1 1",
                        OffsetMax = right.ToString("0.##", CultureInfo.InvariantCulture) + " " + top.ToString("0.##", CultureInfo.InvariantCulture)
                    }
                }
            });
        }

        private void AddRect(CuiElementContainer container, string name, string parent, string color, string sprite, float left, float bottom, float right, float top, bool menu)
        {
            var image = new CuiImageComponent { Color = color };
            if (!string.IsNullOrEmpty(sprite)) image.Sprite = sprite;
            container.Add(new CuiElement
            {
                Name = UiId(name, menu),
                Parent = UiId(parent, menu),
                Components =
                {
                    image,
                    new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = Px(left, bottom), OffsetMax = Px(right, top) }
                }
            });
        }

        private void AddText(CuiElementContainer container, string name, string parent, string text, string color, int size, TextAnchor align, bool bold, float left, float bottom, float right, float top, bool menu)
        {
            container.Add(new CuiElement
            {
                Name = UiId(name, menu),
                Parent = UiId(parent, menu),
                Components =
                {
                    TextComponent(text, color, size, align, bold),
                    new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = Px(left, bottom), OffsetMax = Px(right, top) }
                }
            });
        }

        private void AddStretchText(CuiElementContainer container, string name, string parent, string text, string color, int size, TextAnchor align, bool bold, string offsetMin, string offsetMax, bool menu)
        {
            name = UiId(name, menu);
            container.Add(new CuiElement
            {
                Name = name,
                Parent = UiId(parent, menu),
                DestroyUi = name,
                Components =
                {
                    TextComponent(text, color, size, align, bold),
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = offsetMin, OffsetMax = offsetMax }
                }
            });
        }

        private void AddEdgeText(CuiElementContainer container, string name, string parent, string text, string color, int size, TextAnchor align, bool bold, string offsetMin, string offsetMax, bool menu)
        {
            name = UiId(name, menu);
            container.Add(new CuiElement
            {
                Name = name,
                Parent = UiId(parent, menu),
                DestroyUi = name,
                Components =
                {
                    TextComponent(text, color, size, align, bold),
                    new CuiRectTransformComponent { AnchorMin = "1 0", AnchorMax = "1 1", OffsetMin = offsetMin, OffsetMax = offsetMax }
                }
            });
        }

        private void AddTextUpdate(CuiElementContainer container, string name, string text, string color, int size, TextAnchor align, bool bold)
        {
            container.Add(new CuiElement
            {
                Name = name,
                Update = true,
                Components = { TextComponent(text, color, size, align, bold) }
            });
        }

        private static CuiTextComponent TextComponent(string text, string color, int size, TextAnchor align, bool bold)
        {
            return new CuiTextComponent
            {
                Text = text ?? string.Empty,
                FontSize = size,
                Align = align,
                Color = color,
                Font = bold ? "robotocondensed-bold.ttf" : "robotocondensed-regular.ttf"
            };
        }

        private void PlayerStatus(BasePlayer player, bool menu, out string text, out string kind)
        {
            if (_phase == NightshadeVoteDayRules.PhaseVoting && player != null && _confirming.Contains(player.userID))
            {
                text = Ui("UiPay", ("cost", CostLabel()));
                kind = "idle";
                return;
            }
            Note note;
            if (player != null && _notes.TryGetValue(player.userID, out note) && note != null && !string.IsNullOrEmpty(note.Text))
            {
                text = note.Text;
                kind = string.IsNullOrEmpty(note.Kind) ? "idle" : note.Kind;
                return;
            }
            if (_phase == NightshadeVoteDayRules.PhaseVoting)
            {
                text = ActionHint(player, menu);
                kind = "idle";
                return;
            }
            int quiet = _phase == NightshadeVoteDayRules.PhasePassed ? _nightsWithoutVote : 0;
            string key = NightshadeVoteDayRules.ResultStatusKey(_phase, quiet);
            text = Ui(key, ("nights", quiet.ToString(CultureInfo.InvariantCulture)));
            kind = NightshadeVoteDayRules.StatusKind(_phase);
        }

        private string KindColor(string kind)
        {
            if (kind == "ok") return Hex(P().Success);
            if (kind == "bad") return Hex(P().Danger);
            return Hex(P().SubText);
        }

        private string HeadlineColor()
        {
            if (_phase == NightshadeVoteDayRules.PhaseBought && !string.IsNullOrEmpty(_buyerName)) return Hex(P().Accent);
            return Hex(P().Text);
        }

        private static int HeadlineFont(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= 3) return 18;
            if (text.Length > 10) return 11;
            return 13;
        }

        private string Ui(string key, params (string token, string value)[] replacements)
        {
            string msg = lang.GetMessage(key, this, null);
            if (string.IsNullOrEmpty(msg) || msg == key) msg = FallbackUi(key);
            if (replacements != null)
            {
                for (int i = 0; i < replacements.Length; i++)
                    msg = msg.Replace("{" + replacements[i].token + "}", replacements[i].value ?? string.Empty);
            }
            return msg;
        }

        private static string FallbackUi(string key)
        {
            if (key == "UiVotes") return "Votes required";
            if (key == "UiVoteHint") return "Waiting on votes";
            if (key == "UiWillSkip") return "Night will be skipped";
            if (key == "UiQuietAfter") return "Skipped. {nights} quiet nights";
            if (key == "UiFailed") return "Not enough votes";
            if (key == "UiPurchased") return "Day purchased";
            if (key == "UiVote") return "Vote";
            if (key == "UiVoted") return "Voted";
            if (key == "UiBuy") return "Buy Day {cost}";
            if (key == "UiConfirm") return "Confirm";
            if (key == "UiCancel") return "Cancel";
            if (key == "UiPay") return "Pay {cost}?";
            if (key == "UiHintMiddle") return "Middle-click buttons";
            if (key == "UiHintUse") return "Press E for buttons";
            if (key == "UiHintReload") return "Press R for buttons";
            if (key == "UiHintReady") return "Click Vote or Buy";
            if (key == "UiCounted") return "Vote counted";
            if (key == "UiAlready") return "Already voted";
            if (key == "UiNeedRp") return "Need {need} RP, have {have}";
            if (key == "UiNeedEco") return "Need {need} coins, have {have}";
            if (key == "UiClosed") return "Vote is closed";
            if (key == "UiDaytime") return "Already daytime";
            if (key == "UiQuiet") return "No vote tonight";
            if (key == "UiQuietLeft") return "No vote. {left} nights left";
            if (key == "UiNotYet") return "Vote is not open";
            if (key == "UiDone") return "Vote already finished";
            if (key == "UiCurrency") return "Currency is not set up";
            if (key == "UiNoRewards") return "Rewards plugin missing";
            if (key == "UiNoEco") return "Economics plugin missing";
            return key;
        }

        private string BuyText()
        {
            return Ui("UiBuy", ("cost", CostLabel()));
        }

        private void SetNote(BasePlayer player, string text, string kind)
        {
            if (player == null) return;
            _notes[player.userID] = new Note
            {
                Text = text ?? string.Empty,
                Kind = string.IsNullOrEmpty(kind) ? "idle" : kind
            };
        }

        private void ClearNote(BasePlayer player)
        {
            if (player != null) _notes.Remove(player.userID);
        }

        private Palette P()
        {
            return EnsurePalette();
        }

        private Palette EnsurePalette()
        {
            int today = DateTime.Now.Month * 100 + DateTime.Now.Day;
            string mode = _config == null || string.IsNullOrWhiteSpace(_config.ThemeMode) ? "Shop" : _config.ThemeMode;
            DateTime stamp = DateTime.MinValue;
            string path = ThemePath();
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) stamp = File.GetLastWriteTimeUtc(path);
            }
            catch (IOException)
            {
                stamp = DateTime.MinValue;
            }
            if (_palette != null && _themeDay == today && _themeStamp == stamp && _themeModeSeen == mode)
                return _palette;
            _themeDay = today;
            _themeStamp = stamp;
            _themeModeSeen = mode;
            _palette = LoadPalette(path);
            return _palette;
        }

        private string ThemePath()
        {
            string file = _config == null || string.IsNullOrWhiteSpace(_config.ThemeFile) ? "NightshadeShop.json" : _config.ThemeFile;
            return Path.Combine(Interface.Oxide.ConfigDirectory, file);
        }

        private Palette LoadPalette(string path)
        {
            Palette fallback = FallbackPalette();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return fallback;
                JObject root = JObject.Parse(File.ReadAllText(path));
                List<NightshadeVoteDayRules.ThemeRow> rows = NightshadeVoteDayRules.ReadThemeRows(root);
                string mode = _config == null || string.IsNullOrWhiteSpace(_config.ThemeMode) ? "Shop" : _config.ThemeMode.Trim();
                if (mode.Equals("Shop", StringComparison.OrdinalIgnoreCase))
                    mode = NightshadeVoteDayRules.ShopMode(root);
                int today = DateTime.Now.Month * 100 + DateTime.Now.Day;
                NightshadeVoteDayRules.ThemeRow chosen = NightshadeVoteDayRules.ResolveTheme(mode, rows, today);
                if (chosen == null) return fallback;
                _themeWarned = false;
                return FromRow(chosen);
            }
            catch (Exception ex)
            {
                if (!_themeWarned)
                {
                    _themeWarned = true;
                    PrintWarning("Shop theme could not be read. Using Nightshade colors. " + ex.Message);
                }
                return fallback;
            }
        }

        private Palette FallbackPalette()
        {
            return new Palette
            {
                Name = "Nightshade",
                Background = FirstColor(_config == null ? null : _config.PanelColor, "#1A1024F2"),
                Header = "#241634FF",
                Panel = "#2A1B3AFF",
                Card = FirstColor(_config == null ? null : _config.TrackColor, "#33224AFF"),
                Accent = FirstColor(_config == null ? null : _config.AccentColor, "#8B3FBFFF"),
                AccentText = "#F2E6FFFF",
                Button = "#3E2A55FF",
                ButtonText = FirstColor(_config == null ? null : _config.TextColor, "#E6D9F2FF"),
                Text = FirstColor(_config == null ? null : _config.TextColor, "#F2EAF7FF"),
                SubText = FirstColor(_config == null ? null : _config.MutedColor, "#A898B8FF"),
                Danger = FirstColor(_config == null ? null : _config.FailColor, "#A63A3AFF"),
                DangerText = "#FFD6D6FF",
                Success = FirstColor(_config == null ? null : _config.SuccessColor, "#3DDC84FF"),
                Source = "Nightshade"
            };
        }

        private static Palette FromRow(NightshadeVoteDayRules.ThemeRow row)
        {
            Palette fallback = new Palette();
            return new Palette
            {
                Name = string.IsNullOrEmpty(row.Name) ? fallback.Name : row.Name,
                Background = FirstColor(row.Background, fallback.Background),
                Header = FirstColor(row.Header, fallback.Header),
                Panel = FirstColor(row.Panel, fallback.Panel),
                Card = FirstColor(row.Card, fallback.Card),
                Accent = FirstColor(row.Accent, fallback.Accent),
                AccentText = FirstColor(row.AccentText, fallback.AccentText),
                Button = FirstColor(row.Button, fallback.Button),
                ButtonText = FirstColor(row.ButtonText, fallback.ButtonText),
                Text = FirstColor(row.Text, fallback.Text),
                SubText = FirstColor(row.SubText, fallback.SubText),
                Danger = FirstColor(row.Danger, fallback.Danger),
                DangerText = FirstColor(row.DangerText, fallback.DangerText),
                Success = fallback.Success,
                Source = "NightshadeShop.json"
            };
        }

        private static string FirstColor(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private class Palette
        {
            public string Name = "Nightshade";
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
            public string Success = "#3DDC84FF";
            public string Source = "Nightshade";
        }

        private string ActionHint(BasePlayer player, bool menu)
        {
            // The inventory menu already freed the cursor, so Vote and Buy take a normal click.
            if (menu || _config.CursorWhileExpanded) return Ui("UiHintReady");
            if (player != null && _cursor.Contains(player.userID)) return Ui("UiHintReady");
            string key = NightshadeVoteDayRules.NormalizeCursorKey(_config.CursorKey);
            if (key == "use") return Ui("UiHintUse");
            if (key == "reload") return Ui("UiHintReload");
            if (key == "none") return Ui("UiHintReady");
            return Ui("UiHintMiddle");
        }

        private bool WantsCursor(BasePlayer player)
        {
            if (player == null || IsCollapsed(player)) return false;
            if (_phase != NightshadeVoteDayRules.PhaseVoting) return false;
            if (_config.CursorWhileExpanded) return true;
            return _cursor.Contains(player.userID);
        }

        private bool AllowUi(BasePlayer player)
        {
            float now = Time.realtimeSinceStartup;
            float next;
            if (_uiNext.TryGetValue(player.userID, out next) && now < next) return false;
            _uiNext[player.userID] = now + UiGapSeconds;
            return true;
        }

        private void ToggleCursor(BasePlayer player)
        {
            if (IsCollapsed(player))
            {
                _collapsed[player.UserIDString] = false;
                _cursor.Add(player.userID);
                SavePanel();
                Show(player);
                return;
            }
            if (_cursor.Contains(player.userID)) _cursor.Remove(player.userID);
            else _cursor.Add(player.userID);
            Show(player);
        }

        private static bool TryCursorButton(string key, out BUTTON button)
        {
            string normal = NightshadeVoteDayRules.NormalizeCursorKey(key);
            if (normal == "use") { button = BUTTON.USE; return true; }
            if (normal == "reload") { button = BUTTON.RELOAD; return true; }
            if (normal == "firethird") { button = BUTTON.FIRE_THIRD; return true; }
            button = BUTTON.FIRE_THIRD;
            return false;
        }

        [ConsoleCommand(UiCommand)]
        private void CmdPanel(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg == null ? null : arg.Player();
            if (player == null || !_panelOpen) return;
            string action = string.Empty;
            if (arg.Args != null && arg.Args.Length > 0)
            {
                object first = arg.Args.GetValue(0);
                if (first != null) action = first.ToString();
            }
            if (string.Equals(action, "toggle", StringComparison.OrdinalIgnoreCase))
            {
                bool collapsed = !IsCollapsed(player);
                _collapsed[player.UserIDString] = collapsed;
                if (collapsed)
                {
                    _cursor.Remove(player.userID);
                    _confirming.Remove(player.userID);
                }
                SavePanel();
                Show(player);
                return;
            }
            if (string.Equals(action, "cursor", StringComparison.OrdinalIgnoreCase))
            {
                ToggleCursor(player);
                return;
            }
            if (string.Equals(action, "vote", StringComparison.OrdinalIgnoreCase))
            {
                if (AllowUi(player)) TryCastVote(player);
                return;
            }
            if (string.Equals(action, "buy", StringComparison.OrdinalIgnoreCase))
            {
                if (AllowUi(player)) AskBuy(player);
                return;
            }
            if (string.Equals(action, "confirm", StringComparison.OrdinalIgnoreCase))
            {
                if (AllowUi(player)) ConfirmBuy(player);
                return;
            }
            if (string.Equals(action, "cancel", StringComparison.OrdinalIgnoreCase))
            {
                if (AllowUi(player)) CancelBuy(player);
            }
        }

        private bool IsCollapsed(BasePlayer player)
        {
            if (player == null || string.IsNullOrEmpty(player.UserIDString)) return false;
            bool value;
            return _collapsed.TryGetValue(player.UserIDString, out value) && value;
        }

        private void LoadPanel()
        {
            PanelStore store = null;
            try { store = Interface.Oxide.DataFileSystem.ReadObject<PanelStore>(PanelDataPath); }
            catch { store = null; }
            _collapsed = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (store == null || store.Collapsed == null) return;
            foreach (KeyValuePair<string, bool> pair in store.Collapsed)
            {
                if (!string.IsNullOrEmpty(pair.Key)) _collapsed[pair.Key] = pair.Value;
            }
        }

        private void SavePanel()
        {
            try
            {
                Interface.Oxide.DataFileSystem.WriteObject(PanelDataPath, new PanelStore { Collapsed = _collapsed });
            }
            catch (Exception ex)
            {
                PrintWarning("Could not save vote panel state: " + ex.Message);
            }
        }

        private void EachPlayer(Action<BasePlayer> action)
        {
            if (action == null || BasePlayer.activePlayerList == null) return;
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                action(player);
            }
        }

        private static string Px(float x, float y)
        {
            return x.ToString("0.##", CultureInfo.InvariantCulture) + " " + y.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Hex(string hex, float alpha = -1f)
        {
            if (string.IsNullOrEmpty(hex)) return "1 1 1 1";
            hex = hex.Trim();
            if (hex.Length > 0 && hex[0] == '#') hex = hex.Substring(1);
            if (hex.Length < 6) return "1 1 1 1";
            int r;
            int g;
            int b;
            int embedded = 255;
            if (!int.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)) return "1 1 1 1";
            if (!int.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)) return "1 1 1 1";
            if (!int.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b)) return "1 1 1 1";
            if (hex.Length >= 8 && !int.TryParse(hex.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out embedded)) return "1 1 1 1";
            float a = alpha >= 0f ? alpha : embedded / 255f;
            if (a < 0f) a = 0f;
            if (a > 1f) a = 1f;
            return (r / 255f).ToString("0.###", CultureInfo.InvariantCulture) + " "
                + (g / 255f).ToString("0.###", CultureInfo.InvariantCulture) + " "
                + (b / 255f).ToString("0.###", CultureInfo.InvariantCulture) + " "
                + a.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    internal static class NightshadeVoteDayRules
    {
        internal const string CurrencyKey = "Currency (ServerRewards or Economics)";
        internal const string RpCostKey = "ServerRewards Cost";
        internal const string EcoCostKey = "Economics Cost";
        internal const string PhaseVoting = "voting";
        internal const string PhasePassed = "passed";
        internal const string PhaseFailed = "failed";
        internal const string PhaseBought = "bought";
        internal static readonly string MenuLayer = "Overlay";
        // 1.5.0 drew on Overlay and was visible. 1.6.x moved the closed card to Hud.
        internal const string ClosedLayerName = "Overlay";
        // Overlay is visible and clickable over the inventory (this panel, 1.5.0).
        // Hud.Menu is under the blur. The Inventory view does not draw a plugin panel here.
        internal const string OpenLayerName = "Overlay";
        internal const string BrokenOpenLayer = "Hud.Menu";
        internal const string InventoryLayerName = "Inventory";
        internal const string LayerKey = "Panel layer (Overlay stays above the inventory, Overall is above every menu, Hud sits under it)";
        internal const string OldLayerKey = "Panel layer (Hud sits with the hotbar, Overlay stays up in menus)";
        internal const string ClosedLayerKey = "Panel layer when the inventory is closed (Hud hides while it is open)";
        internal const string OpenLayerKeyV2 = "Panel layer when the inventory is open (Hud.Menu shows only then)";
        internal const string OpenLayerKeyV3 = "Panel layer when the inventory is open (Inventory view; Overlay stays above every menu)";
        internal const string OpenLayerKey = "Panel layer when the inventory is open (Overlay stays above it, Overall is above every menu)";
        internal const int CurrentConfigVersion = 5;
        internal const string OpenRightKey = "Inventory open panel right (px from bottom center)";
        internal const string OpenBottomKey = "Inventory open panel bottom (px)";
        internal const string LogoKey = "Panel logo URL (empty hides the logo)";
        internal const string ConfigVersionKey = "Config version";
        internal const float DefaultHotbarLeft = -204f;
        internal const float DefaultGap = 8f;
        internal const float DefaultBottom = 16f;
        // The 1.5.0 panel (right edge at the hotbar gap) clips the lower attire slots
        // while inventory is open. This copy sits 16px further left and 16px lower.
        // The right edge is what stays clear of that column, so a wider panel grows left.
        internal const float OpenNudgeLeft = 16f;
        internal const float OpenNudgeDown = 16f;
        internal const float DefaultOpenRight = DefaultHotbarLeft - DefaultGap - OpenNudgeLeft;
        internal const float DefaultOpenBottom = DefaultBottom - OpenNudgeDown;
        internal static readonly float DefaultWidth = 136f;
        internal static readonly float DefaultHeight = 106f;
        internal static readonly float HeaderBottom = -16f;
        internal static readonly float RuleBottom = -18f;
        internal static readonly float CountTop = -19f;
        internal static readonly float CountBottom = -36f;
        internal static readonly float TrackTop = -38f;
        internal static readonly float TrackBottom = -51f;
        internal static readonly float StatusTop = -52f;
        internal static readonly float StatusBottom = -63f;
        internal static readonly float VoteTop = -65f;
        internal static readonly float VoteBottom = -83f;
        internal static readonly float ActionTop = -85f;
        internal static readonly float ActionBottom = -103f;

        internal static bool IsBrokenOpenLayer(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            value = value.Trim();
            return value.Equals(BrokenOpenLayer, StringComparison.OrdinalIgnoreCase)
                || value.Equals("Hud.Menus", StringComparison.OrdinalIgnoreCase)
                || value.Equals(InventoryLayerName, StringComparison.OrdinalIgnoreCase);
        }

        internal static string ResolveOpenLayer(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || IsBrokenOpenLayer(value)) return OpenLayerName;
            return value.Trim();
        }

        internal static string NormalizeCursorKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "firethird";
            key = key.Trim().ToLowerInvariant();
            if (key == "use" || key == "e") return "use";
            if (key == "reload" || key == "r") return "reload";
            if (key == "none" || key == "off") return "none";
            if (key == "firethird" || key == "middle" || key == "mouse2") return "firethird";
            return "firethird";
        }
        internal const float DefaultCollapsed = 40f;

        internal struct ScreenBox
        {
            public float Left;
            public float Bottom;
            public float Right;
            public float Top;
            public string Anchor;
            public string OffsetMin;
            public string OffsetMax;
        }

        internal static ScreenBox LeftOfHotbar(float hotbarLeft, float gap, float bottom, float width, float height, string anchor)
        {
            if (float.IsNaN(hotbarLeft) || float.IsInfinity(hotbarLeft)) hotbarLeft = DefaultHotbarLeft;
            if (float.IsNaN(bottom) || float.IsInfinity(bottom)) bottom = DefaultBottom;
            if (float.IsNaN(width) || width < 1f) width = DefaultWidth;
            if (float.IsNaN(height) || height < 1f) height = DefaultHeight;
            if (float.IsNaN(gap) || gap < 0f) gap = 0f;
            float right = hotbarLeft - gap;
            float left = right - width;
            float top = bottom + height;
            string clean = CleanAnchor(anchor);
            return new ScreenBox
            {
                Left = left,
                Bottom = bottom,
                Right = right,
                Top = top,
                Anchor = clean,
                OffsetMin = Num(left) + " " + Num(bottom),
                OffsetMax = Num(right) + " " + Num(top)
            };
        }

        internal static ScreenBox Place(float left, float bottom, float width, float height, string anchor)
        {
            if (float.IsNaN(left) || float.IsInfinity(left)) left = DefaultOpenRight - DefaultWidth;
            if (float.IsNaN(bottom) || float.IsInfinity(bottom)) bottom = DefaultOpenBottom;
            if (float.IsNaN(width) || width < 1f) width = DefaultWidth;
            if (float.IsNaN(height) || height < 1f) height = DefaultHeight;
            float right = left + width;
            float top = bottom + height;
            string clean = CleanAnchor(anchor);
            return new ScreenBox
            {
                Left = left,
                Bottom = bottom,
                Right = right,
                Top = top,
                Anchor = clean,
                OffsetMin = Num(left) + " " + Num(bottom),
                OffsetMax = Num(right) + " " + Num(top)
            };
        }

        internal static bool Overlaps(ScreenBox a, ScreenBox b)
        {
            if (a.Right <= b.Left || b.Right <= a.Left) return false;
            if (a.Top <= b.Bottom || b.Top <= a.Bottom) return false;
            return true;
        }

        internal static string CleanAnchor(string anchor)
        {
            if (string.IsNullOrWhiteSpace(anchor)) return "0.5 0";
            string[] parts = anchor.Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return "0.5 0";
            float x;
            float y;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return "0.5 0";
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return "0.5 0";
            return Num(x) + " " + Num(y);
        }

        internal static string FormatCost(string currency, int rpCost, double ecoCost)
        {
            if (currency != null && string.Equals(currency.Trim(), "economics", StringComparison.OrdinalIgnoreCase))
                return ecoCost.ToString(CultureInfo.InvariantCulture) + " coins";
            return rpCost.ToString(CultureInfo.InvariantCulture) + " RP";
        }

        internal static bool ApplyLiveCostsJson(string json, ref string currency, ref int rp, ref double eco)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            JObject raw;
            try { raw = JObject.Parse(json); }
            catch (JsonException) { return false; }
            return ApplyLiveCosts(raw, ref currency, ref rp, ref eco);
        }

        internal static bool ApplyLiveCosts(JObject raw, ref string currency, ref int rp, ref double eco)
        {
            if (raw == null) return false;
            bool any = false;
            JToken token = raw[CurrencyKey];
            if (token != null && token.Type != JTokenType.Null)
            {
                string text = token.Type == JTokenType.String ? (string)token : token.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    currency = text.Trim();
                    any = true;
                }
            }
            double number;
            token = raw[RpCostKey];
            if (token != null && token.Type != JTokenType.Null && TryNumber(token, out number))
            {
                rp = (int)number;
                any = true;
            }
            token = raw[EcoCostKey];
            if (token != null && token.Type != JTokenType.Null && TryNumber(token, out number))
            {
                eco = number;
                any = true;
            }
            return any;
        }

        internal static int Needed(int population, float threshold)
        {
            if (population < 0) population = 0;
            if (float.IsNaN(threshold) || float.IsInfinity(threshold) || threshold < 0f) threshold = 0f;
            return (int)Math.Ceiling((double)population * threshold);
        }

        internal static float VoteFraction(int votes, int needed)
        {
            if (votes < 0) votes = 0;
            if (needed <= 0) return votes > 0 ? 1f : 0f;
            float value = (float)votes / needed;
            if (value < 0f) return 0f;
            if (value > 1f) return 1f;
            return value;
        }

        internal static string VotesLabel(int votes, int needed)
        {
            if (votes < 0) votes = 0;
            if (needed < 0) needed = 0;
            return votes.ToString(CultureInfo.InvariantCulture) + "/" + needed.ToString(CultureInfo.InvariantCulture);
        }

        internal static string Headline(string phase, int seconds, string buyer)
        {
            if (phase == PhaseBought && !string.IsNullOrEmpty(buyer)) return buyer;
            if (seconds < 0) seconds = 0;
            return seconds.ToString(CultureInfo.InvariantCulture);
        }

        internal static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            name = name.Replace("<", string.Empty).Replace(">", string.Empty).Replace("\n", " ").Replace("\r", " ").Trim();
            if (name.Length > 16) name = name.Substring(0, 16);
            return name;
        }

        internal static string StatusKey(string phase)
        {
            if (phase == PhasePassed) return "UiWillSkip";
            if (phase == PhaseFailed) return "UiFailed";
            if (phase == PhaseBought) return "UiPurchased";
            return "UiVoteHint";
        }

        internal static string StatusKind(string phase)
        {
            if (phase == PhasePassed || phase == PhaseBought) return "ok";
            if (phase == PhaseFailed) return "bad";
            return "idle";
        }

        internal static string ResultStatusKey(string phase, int quietNights)
        {
            if (phase == PhasePassed && quietNights > 0) return "UiQuietAfter";
            return StatusKey(phase);
        }

        internal static void MigratePanelSize(NightshadeVoteDay.PluginConfig config)
        {
            if (config == null) return;
            if (Math.Abs(config.PanelWidth - 152f) < 0.01f && Math.Abs(config.PanelHeight - 136f) < 0.01f)
            {
                config.PanelWidth = DefaultWidth;
                config.PanelHeight = DefaultHeight;
            }
        }

        internal static bool ExpandedFits(float width, float height)
        {
            if (width < 120f || height + 0.01f < DefaultHeight) return false;
            float vote = VoteTop - VoteBottom;
            float action = ActionTop - ActionBottom;
            if (vote < 18f || action < 18f) return false;
            if (VoteBottom < ActionTop) return false;
            if (ActionBottom < -(height - 2f)) return false;
            if (StatusBottom < VoteTop) return false;
            return true;
        }

        internal sealed class ThemeRow
        {
            public string Name;
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
            public string Danger;
            public string DangerText;
        }

        internal static List<ThemeRow> ReadThemeRows(JObject root)
        {
            var rows = new List<ThemeRow>();
            if (root == null) return rows;
            JArray themes = root["Themes"] as JArray;
            if (themes == null) return rows;
            for (int i = 0; i < themes.Count; i++)
            {
                JObject item = themes[i] as JObject;
                if (item == null) continue;
                JObject colors = item["Colors (#RRGGBB or #RRGGBBAA)"] as JObject;
                rows.Add(new ThemeRow
                {
                    Name = item.Value<string>("Name"),
                    From = item.Value<string>("Active From (MM-DD)"),
                    To = item.Value<string>("Active To (MM-DD)"),
                    Priority = item.Value<int?>("Priority (higher wins when dates overlap)") ?? 0,
                    Background = ColorOf(colors, "Background"),
                    Header = ColorOf(colors, "Header"),
                    Panel = ColorOf(colors, "Panel"),
                    Card = ColorOf(colors, "Card"),
                    Accent = ColorOf(colors, "Accent"),
                    AccentText = ColorOf(colors, "AccentText"),
                    Button = ColorOf(colors, "Button"),
                    ButtonText = ColorOf(colors, "ButtonText"),
                    Text = ColorOf(colors, "Text"),
                    SubText = ColorOf(colors, "SubText"),
                    Danger = ColorOf(colors, "Danger"),
                    DangerText = ColorOf(colors, "DangerText")
                });
            }
            return rows;
        }

        internal static string ShopMode(JObject root)
        {
            if (root == null) return "Auto";
            string mode = root.Value<string>("Theme Mode (Auto or a theme name)");
            return string.IsNullOrWhiteSpace(mode) ? "Auto" : mode.Trim();
        }

        internal static ThemeRow ResolveTheme(string mode, IList<ThemeRow> themes, int monthDay)
        {
            if (themes == null || themes.Count == 0) return null;
            if (!string.IsNullOrEmpty(mode) && !mode.Equals("Auto", StringComparison.OrdinalIgnoreCase) && !mode.Equals("Shop", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < themes.Count; i++)
                {
                    ThemeRow named = themes[i];
                    if (named != null && string.Equals(named.Name, mode, StringComparison.OrdinalIgnoreCase)) return named;
                }
            }
            ThemeRow best = null;
            for (int i = 0; i < themes.Count; i++)
            {
                ThemeRow row = themes[i];
                if (row == null || !InRange(row.From, row.To, monthDay)) continue;
                if (best == null || row.Priority > best.Priority) best = row;
            }
            return best ?? themes[0];
        }

        internal static bool InRange(string from, string to, int today)
        {
            int start = ParseMd(from, 101);
            int end = ParseMd(to, 1231);
            return start <= end ? today >= start && today <= end : today >= start || today <= end;
        }

        internal static int ParseMd(string text, int fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            string[] parts = text.Split('-');
            int month;
            int day;
            if (parts.Length == 2 && int.TryParse(parts[0], out month) && int.TryParse(parts[1], out day))
                return month * 100 + day;
            return fallback;
        }

        private static string ColorOf(JObject colors, string key)
        {
            if (colors == null) return null;
            return colors.Value<string>(key);
        }

        private static bool TryNumber(JToken token, out double number)
        {
            number = 0d;
            if (token == null) return false;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                number = token.Value<double>();
                return !double.IsNaN(number) && !double.IsInfinity(number);
            }
            return double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }

        private static string Num(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
