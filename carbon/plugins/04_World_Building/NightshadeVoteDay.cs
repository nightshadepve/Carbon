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
    [Info("NightshadeVoteDay", "Nightshade", "1.3.0")]
    [Description("Vote for day at a configurable night time. One vote per night. Players can buy day with /buyday. Shows a compact panel to the left of the hotbar.")]
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
        private const string CommandName = "NightshadeVoteDay.Command";
        private const string ToggleName = "NightshadeVoteDay.Toggle";
        private const string UiCommand = "nightshadevoteday.ui";
        private const string DefaultLogo = "https://raw.githubusercontent.com/nightshadepve/Nightshadeserver/refs/heads/main/260625_Logo_75_nobg.png";
        private const int SkipStateVersion = 2;
        internal const string ModeKey = "Vote mode (Daily = vote every night, SkipMultiple = a pass sets this night to morning, then the next N nights have no vote and run normally)";
        internal const string QuietNightsKey = "Nights without a vote after a pass (SkipMultiple only, default 2; these nights are not set to morning)";
        private const string OldModeKey = "Vote mode (Daily = vote every night, SkipMultiple = one passing vote skips several nights)";
        private const string OldNightsKey = "Nights to skip (SkipMultiple only, includes the voted night, minimum 1)";

        private PluginConfig _config;

        internal class PluginConfig
        {
            [JsonProperty("Chat server name (sender of vote messages)")]
            public string ChatServerName = "Nightshade";

            [JsonProperty("Chat avatar SteamID (0 = server icon)")]
            public ulong ChatAvatarSteamId = 0;

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

            [JsonProperty("Panel layer (Hud sits with the hotbar, Overlay stays up in menus)")]
            public string PanelLayer = "Hud";

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

            [JsonProperty("Buy command shown on the panel")]
            public string BuyCommandText = "/buyday confirm";

            [JsonProperty("Panel logo URL (empty hides the logo)")]
            public string PanelLogoUrl = DefaultLogo;

            [JsonProperty("Seconds to keep the panel after the vote ends (0 removes it immediately)")]
            public float ResultHoldSeconds = 6f;
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
            if (string.IsNullOrWhiteSpace(config.PanelLayer)) config.PanelLayer = "Hud";
            else config.PanelLayer = config.PanelLayer.Trim();
            if (string.IsNullOrWhiteSpace(config.PanelTitle)) config.PanelTitle = "Skip Night";
            if (string.IsNullOrWhiteSpace(config.BuyCommandText)) config.BuyCommandText = "/buyday confirm";
            if (string.IsNullOrWhiteSpace(config.PanelColor)) config.PanelColor = "#1A1024";
            if (string.IsNullOrWhiteSpace(config.AccentColor)) config.AccentColor = "#A267D3";
            if (string.IsNullOrWhiteSpace(config.TextColor)) config.TextColor = "#F2EAF7";
            if (string.IsNullOrWhiteSpace(config.MutedColor)) config.MutedColor = "#A898B8";
            if (string.IsNullOrWhiteSpace(config.SuccessColor)) config.SuccessColor = "#3DDC84";
            if (string.IsNullOrWhiteSpace(config.FailColor)) config.FailColor = "#D58D7C";
            if (string.IsNullOrWhiteSpace(config.SunColor)) config.SunColor = "#E2B968";
            if (string.IsNullOrWhiteSpace(config.TrackColor)) config.TrackColor = "#120A1C";
            if (config.PanelLogoUrl == null) config.PanelLogoUrl = DefaultLogo;
            if (float.IsNaN(config.PanelWidth) || config.PanelWidth < 132f) config.PanelWidth = NightshadeVoteDayRules.DefaultWidth;
            if (config.PanelWidth > 420f) config.PanelWidth = 420f;
            if (float.IsNaN(config.PanelHeight) || config.PanelHeight < 118f) config.PanelHeight = NightshadeVoteDayRules.DefaultHeight;
            if (config.PanelHeight > 320f) config.PanelHeight = 320f;
            if (float.IsNaN(config.CollapsedSize) || config.CollapsedSize < 36f) config.CollapsedSize = NightshadeVoteDayRules.DefaultCollapsed;
            if (config.CollapsedSize > 96f) config.CollapsedSize = 96f;
            if (float.IsNaN(config.PanelGap) || config.PanelGap < 0f) config.PanelGap = 0f;
            if (config.PanelOpacity < 0f || float.IsNaN(config.PanelOpacity)) config.PanelOpacity = 0f;
            if (config.PanelOpacity > 100f) config.PanelOpacity = 100f;
            if (float.IsNaN(config.ResultHoldSeconds) || config.ResultHoldSeconds < 0f) config.ResultHoldSeconds = 0f;
            if (float.IsNaN(config.HotbarLeft) || float.IsInfinity(config.HotbarLeft)) config.HotbarLeft = NightshadeVoteDayRules.DefaultHotbarLeft;
            if (float.IsNaN(config.PanelBottom) || float.IsInfinity(config.PanelBottom)) config.PanelBottom = NightshadeVoteDayRules.DefaultBottom;
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
            if (string.IsNullOrWhiteSpace(config.ChatServerName)) config.ChatServerName = "Nightshade";
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
                ["VoteStart"]           = "It's {time}! Type /day to vote for daytime. Need {threshold}% ({needed} votes) \u2022 {duration}s to vote.",
                ["VoteStartBuyHint"]    = "Can't wait? Use /buyday to force day instantly!",

                ["VoteRegistered"]      = "Vote registered! {current}/{needed} needed.",
                ["VoteProgress"]        = "{current}/{needed} votes ({percent}%). Type /day",
                ["AlreadyVoted"]        = "You've already voted.",

                ["VotePassed"]          = "Vote Day PASSED \u2014 {votes}/{pop} voted ({percent}%). Time set to {daytime}!",
                ["VotePassedLater"]     = "This night is now morning ({daytime}). The next {nights} night(s) have no vote.",
                ["VoteFailed"]          = "Vote Day FAILED \u2014 {votes}/{pop} voted ({percent}%). Needed {needed}. See you tomorrow night!",

                ["NoVoteNatural"]       = "No vote tonight. {left} more night(s) without a vote after this one.",
                ["NoVoteNaturalLast"]   = "No vote tonight. Voting opens again next night.",

                ["NoVoteYet"]           = "No vote yet - Don't be greedy",
                ["NoVoteAtTime"]        = "No active vote right now.",
                ["VoteAlreadyDone"]     = "The vote already happened tonight. Check back tomorrow at {time}.",

                ["BuyDayInfo"]          = "Force it to {daytime} immediately for {cost}. Use /buyday confirm to purchase.",
                ["BuyDayUsage"]         = "Usage: /buyday confirm",
                ["BuyDayDaytime"]       = "It's already daytime \u2014 nothing to buy!",
                ["BuyDayClosed"]        = "The vote window for tonight has already closed.",
                ["BuyDayPurchasedOne"]  = "{player} spent {cost} to set this night to {daytime}. Only this night was changed.",

                ["NotEnoughRP"]         = "Not enough RP. Need {need} RP, you have {have}.",
                ["NotEnoughEco"]        = "Not enough coins. Need {need}, you have {have}.",
                ["CurrencyInvalid"]     = "Invalid currency config. Must be ServerRewards or Economics. Contact an admin.",
                ["PluginMissingRP"]     = "ServerRewards plugin not found. Contact an admin.",
                ["PluginMissingEco"]    = "Economics plugin not found. Contact an admin.",

                ["UiVotes"]             = "Votes required",
                ["UiVoteHint"]          = "Type /day to vote",
                ["UiWillSkip"]          = "The night will be skipped",
                ["UiFailed"]            = "Not enough votes",
                ["UiPurchased"]         = "Day purchased",
            }, this);
        }

        private string L(string key, string playerId = null, params (string token, string value)[] replacements)
        {
            string msg = lang.GetMessage(key, this, playerId);
            foreach (var (token, value) in replacements)
                msg = msg.Replace("{" + token + "}", value);
            return msg;
        }

        private void Say(BasePlayer player, string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            string name = string.IsNullOrWhiteSpace(_config.ChatServerName) ? "Nightshade" : _config.ChatServerName.Trim();
            name = name.Replace("<", string.Empty).Replace(">", string.Empty);
            string line = "<color=#C4A6E0>" + name + "</color>: " + message;
            ulong avatar = _config.ChatAvatarSteamId;
            if (player == null)
            {
                ConsoleNetwork.BroadcastToAllClients("chat.add", new object[] { 2, avatar, line });
                return;
            }
            if (player.IsConnected)
                player.SendConsoleCommand("chat.add", 2, avatar, line);
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

        // --- Plugin references ---
        [PluginReference] Plugin ServerRewards;
        [PluginReference] Plugin Economics;

        // --- Hooks ---

        void OnServerInitialized()
        {
            LoadSkips();
            LoadPanel();
            _ready = true;
            timer.Every(10f, CheckGameTime);
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
                AnnounceNaturalNight(null);
                return;
            }
            if (result.Action == "vote")
            {
                SaveSkips();
                OpenWindow();
            }
        }

        private void AnnounceNaturalNight(BasePlayer player)
        {
            string playerId = player == null ? null : player.UserIDString;
            if (_nightsWithoutVote > 0)
                Say(player, L("NoVoteNatural", playerId, ("left", _nightsWithoutVote.ToString(CultureInfo.InvariantCulture))));
            else
                Say(player, L("NoVoteNaturalLast", playerId));
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

            int    pop    = BasePlayer.activePlayerList.Count;
            int    needed = NeededVotes();
            int    pct    = Mathf.RoundToInt(_config.VoteThreshold * 100f);

            Say(null, L("VoteStart", null,
                ("time",      FormatHour(_config.VoteStartTime)),
                ("threshold", pct.ToString(CultureInfo.InvariantCulture)),
                ("needed",    needed.ToString(CultureInfo.InvariantCulture)),
                ("duration",  ((int)_config.VoteDuration).ToString(CultureInfo.InvariantCulture))));

            Say(null, L("VoteStartBuyHint"));

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
                Say(null, L("VotePassed", null,
                    ("votes",   votes.ToString(CultureInfo.InvariantCulture)),
                    ("pop",     pop.ToString(CultureInfo.InvariantCulture)),
                    ("percent", Mathf.RoundToInt(ratio * 100f).ToString(CultureInfo.InvariantCulture)),
                    ("daytime", daytime)));
                if (quiet > 0)
                    Say(null, L("VotePassedLater", null,
                        ("daytime", daytime),
                        ("nights", quiet.ToString(CultureInfo.InvariantCulture))));
            }
            else
            {
                RememberNight();
                SaveSkips();
                Say(null, L("VoteFailed", null,
                    ("votes",   votes.ToString(CultureInfo.InvariantCulture)),
                    ("pop",     pop.ToString(CultureInfo.InvariantCulture)),
                    ("percent", Mathf.RoundToInt(ratio * 100f).ToString(CultureInfo.InvariantCulture)),
                    ("needed",  needed.ToString(CultureInfo.InvariantCulture))));
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

            Say(null, L("BuyDayPurchasedOne", null,
                ("player",  buyer.displayName),
                ("cost",    CostLabel()),
                ("daytime", FormatHour(_config.DayTime))));
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

        // --- /day command ---

        [ChatCommand("day")]
        private void CmdVoteDay(BasePlayer player, string command, string[] args)
        {
            if (!_voteActive)
            {
                if (_naturalTonight)
                {
                    AnnounceNaturalNight(player);
                    return;
                }
                if (_votedThisNight)
                {
                    Say(player, L("VoteAlreadyDone", player.UserIDString,
                        ("time", FormatHour(_config.VoteStartTime))));
                }
                else
                {
                    float t         = TOD_Sky.Instance?.Cycle.Hour ?? -1f;
                    int   hoursLeft = t >= 0f ? Mathf.CeilToInt(_config.VoteStartTime - (t < _config.VoteStartTime ? t : t - 24f)) : 0;

                    if (hoursLeft > 0)
                        Say(player, L("NoVoteYet", player.UserIDString,
                            ("time",  FormatHour(_config.VoteStartTime)),
                            ("hours", hoursLeft.ToString(CultureInfo.InvariantCulture))));
                    else
                        Say(player, L("NoVoteAtTime", player.UserIDString));
                }
                return;
            }

            if (_votes.Contains(player.userID))
            {
                Say(player, L("AlreadyVoted", player.UserIDString));
                return;
            }

            _votes.Add(player.userID);

            int pop       = BasePlayer.activePlayerList.Count;
            int needed    = NeededVotes();
            int voteCount = _votes.Count;

            Say(player, L("VoteRegistered", player.UserIDString,
                ("current", voteCount.ToString(CultureInfo.InvariantCulture)),
                ("needed",  needed.ToString(CultureInfo.InvariantCulture))));

            int percent = pop > 0 ? Mathf.RoundToInt((float)voteCount / pop * 100f) : 0;
            Say(null, L("VoteProgress", null,
                ("current", voteCount.ToString(CultureInfo.InvariantCulture)),
                ("needed",  needed.ToString(CultureInfo.InvariantCulture)),
                ("percent", percent.ToString(CultureInfo.InvariantCulture))));

            if (voteCount >= needed)
            {
                _voteTimer?.Destroy();
                EndVote();
                return;
            }
            TickPanel();
        }

        // --- /buyday command ---

        [ChatCommand("buyday")]
        private void CmdBuyDay(BasePlayer player, string command, string[] args)
        {
            RefreshPriceFromDisk();
            float  t    = TOD_Sky.Instance?.Cycle.Hour ?? -1f;
            string uid  = player.UserIDString;
            string mode = _config.Currency == null ? string.Empty : _config.Currency.ToLowerInvariant();

            // /buyday is only available at night: 1900-2359 or 0000-0600
            bool isNight = (t >= _config.VoteStartTime) || (t >= 0f && t < 6f);
            if (!isNight)
            {
                Say(player, L("BuyDayDaytime", uid));
                return;
            }

            if (_votedThisNight && !_voteActive)
            {
                if (_naturalTonight) AnnounceNaturalNight(player);
                else Say(player, L("BuyDayClosed", uid));
                return;
            }

            if (mode == "serverrewards" && ServerRewards == null)
            {
                Say(player, L("PluginMissingRP", uid));
                return;
            }
            if (mode == "economics" && Economics == null)
            {
                Say(player, L("PluginMissingEco", uid));
                return;
            }
            if (mode != "serverrewards" && mode != "economics")
            {
                Say(player, L("CurrencyInvalid", uid));
                return;
            }

            if (args.Length == 0)
            {
                Say(player, L("BuyDayInfo", uid,
                    ("daytime", FormatHour(_config.DayTime)),
                    ("cost",    CostLabel())));
                return;
            }

            if (args[0].ToLowerInvariant() != "confirm")
            {
                Say(player, L("BuyDayUsage", uid));
                return;
            }

            if (!TryCharge(player, mode)) return;

            ForceDayByPurchase(player);
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
                Say(player, L("NotEnoughRP", player.UserIDString,
                    ("need", _config.RPCost.ToString(CultureInfo.InvariantCulture)),
                    ("have", balance.ToString(CultureInfo.InvariantCulture))));
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
                Say(player, L("NotEnoughEco", player.UserIDString,
                    ("need", _config.EcoCost.ToString(CultureInfo.InvariantCulture)),
                    ("have", balance.ToString(CultureInfo.InvariantCulture))));
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
            Puts("Vote panel config reloaded. Day buy price is " + CostLabel() + ".");
            RebuildAll();
        }

        private void BeginVotePanel()
        {
            if (_holdTimer != null) { _holdTimer.Destroy(); _holdTimer = null; }
            _holdingResult = false;
            if (_config == null || !_config.ShowPanel)
            {
                ClosePanel();
                return;
            }
            _panelOpen = true;
            if (_uiTimer == null) _uiTimer = timer.Every(1f, TickPanel);
            RebuildAll();
        }

        private void BeginResultPanel()
        {
            _holdingResult = true;
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
            if (_holdTimer != null) { _holdTimer.Destroy(); _holdTimer = null; }
            if (_uiTimer != null) { _uiTimer.Destroy(); _uiTimer = null; }
            if (BasePlayer.activePlayerList == null) return;
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player != null) CuiHelper.DestroyUi(player, Root);
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
                if (!secondsChanged && !votesChanged && !priceChanged) return;
                string headline = NightshadeVoteDayRules.Headline(_phase, seconds, _buyerName);
                string have = NightshadeVoteDayRules.VotesLabel(votes, needed);
                float fraction = NightshadeVoteDayRules.VoteFraction(votes, needed);
                int font = HeadlineFont(headline);
                string countColor = HeadlineColor();
                string secondText = seconds.ToString(CultureInfo.InvariantCulture);
                EachPlayer(delegate(BasePlayer player)
                {
                    bool collapsed = IsCollapsed(player);
                    var container = new CuiElementContainer();
                    if (secondsChanged)
                    {
                        if (collapsed) AddTextUpdate(container, CountName, secondText, Hex(_config.TextColor), 12, TextAnchor.MiddleLeft, true);
                        else AddTextUpdate(container, CountName, headline, countColor, font, TextAnchor.MiddleCenter, true);
                    }
                    if (!collapsed && votesChanged)
                    {
                        AddFill(container, fraction);
                        AddStretchText(container, VotesName, TrackName, Ui("UiVotes"), Hex(_config.TextColor), 8, TextAnchor.MiddleLeft, true, "4 0", "-34 0");
                        AddEdgeText(container, HaveName, TrackName, have, Hex(_config.TextColor), 8, TextAnchor.MiddleRight, true, "-40 0", "-4 0");
                    }
                    if (priceChanged)
                    {
                        int priceSize = collapsed ? 9 : 11;
                        AddTextUpdate(container, PriceName, cost, Hex(_config.AccentColor), priceSize, collapsed ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter, true);
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
                CuiHelper.DestroyUi(player, Root);
                var container = new CuiElementContainer();
                if (IsCollapsed(player)) BuildCollapsed(container);
                else BuildExpanded(container);
                CuiHelper.AddUi(player, container);
            }
            catch (Exception ex)
            {
                PrintWarning("Vote panel could not be shown: " + ex.Message);
            }
        }

        private void BuildExpanded(CuiElementContainer container)
        {
            float width = _config.PanelWidth;
            float height = _config.PanelHeight;
            NightshadeVoteDayRules.ScreenBox box = NightshadeVoteDayRules.LeftOfHotbar(_config.HotbarLeft, _config.PanelGap, _config.PanelBottom, width, height, _config.PanelAnchor);
            AddRoot(container, box);
            AddLogo(container, 7f, -6f, 14f);
            AddSun(container, (width - 18f) * 0.5f, -4f, 18f);
            AddText(container, "NightshadeVoteDay.Chevron", BgName, "v", Hex(_config.MutedColor), 12, TextAnchor.MiddleCenter, true, width - 20f, -18f, width - 4f, -3f);
            AddToggle(container, width - 22f, -2f, 20f, 20f);
            string title = string.IsNullOrWhiteSpace(_config.PanelTitle) ? "Skip Night" : _config.PanelTitle.Trim();
            AddText(container, TitleName, BgName, title, Hex(_config.TextColor), 12, TextAnchor.MiddleCenter, true, 24f, -38f, width - 24f, -22f);
            string headline = NightshadeVoteDayRules.Headline(_phase, RemainingSeconds(), _buyerName);
            AddText(container, CountName, BgName, headline, HeadlineColor(), HeadlineFont(headline), TextAnchor.MiddleCenter, true, 8f, -58f, width - 8f, -38f);
            AddRect(container, TrackName, BgName, Hex(_config.TrackColor), null, 8f, -73f, width - 8f, -60f);
            AddFill(container, NightshadeVoteDayRules.VoteFraction(DisplayVotes(), DisplayNeeded()));
            AddStretchText(container, VotesName, TrackName, Ui("UiVotes"), Hex(_config.TextColor), 8, TextAnchor.MiddleLeft, true, "4 0", "-34 0");
            AddEdgeText(container, HaveName, TrackName, NightshadeVoteDayRules.VotesLabel(DisplayVotes(), DisplayNeeded()), Hex(_config.TextColor), 8, TextAnchor.MiddleRight, true, "-40 0", "-4 0");
            AddText(container, StatusName, BgName, StatusText(), StatusColor(), 9, TextAnchor.MiddleCenter, false, 6f, -87f, width - 6f, -75f);
            AddText(container, PriceName, BgName, CostLabel(), Hex(_config.AccentColor), 11, TextAnchor.MiddleCenter, true, 6f, -101f, width - 6f, -88f);
            string command = string.IsNullOrWhiteSpace(_config.BuyCommandText) ? "/buyday confirm" : _config.BuyCommandText.Trim();
            AddText(container, CommandName, BgName, command, Hex(_config.MutedColor), 8, TextAnchor.MiddleCenter, false, 6f, -114f, width - 6f, -102f);
        }

        private void BuildCollapsed(CuiElementContainer container)
        {
            float width = _config.PanelWidth;
            float height = _config.CollapsedSize;
            NightshadeVoteDayRules.ScreenBox box = NightshadeVoteDayRules.LeftOfHotbar(_config.HotbarLeft, _config.PanelGap, _config.PanelBottom, width, height, _config.PanelAnchor);
            AddRoot(container, box);
            float sun = Math.Min(16f, Math.Max(12f, height * 0.4f));
            AddSun(container, 6f, -3f, sun);
            float mid = -3f - sun;
            AddText(container, CountName, BgName, RemainingSeconds().ToString(CultureInfo.InvariantCulture), Hex(_config.TextColor), 12, TextAnchor.MiddleLeft, true, 8f + sun, mid, 46f + sun, -3f);
            AddText(container, PriceName, BgName, CostLabel(), Hex(_config.AccentColor), 9, TextAnchor.MiddleLeft, true, 50f + sun, mid, width - 18f, -3f);
            string command = string.IsNullOrWhiteSpace(_config.BuyCommandText) ? "/buyday confirm" : _config.BuyCommandText.Trim();
            AddText(container, CommandName, BgName, command, Hex(_config.MutedColor), 8, TextAnchor.MiddleLeft, false, 8f, 3f - height, width - 8f, mid - 1f);
            AddText(container, "NightshadeVoteDay.Chevron", BgName, "^", Hex(_config.MutedColor), 10, TextAnchor.MiddleCenter, true, width - 16f, -16f, width - 2f, -2f);
            AddToggle(container, 0f, 0f, width, height);
        }

        private void AddRoot(CuiElementContainer container, NightshadeVoteDayRules.ScreenBox box)
        {
            container.Add(new CuiPanel
            {
                Image = { Color = Hex(_config.AccentColor, 0.95f) },
                RectTransform = { AnchorMin = box.Anchor, AnchorMax = box.Anchor, OffsetMin = box.OffsetMin, OffsetMax = box.OffsetMax },
                CursorEnabled = false
            }, string.IsNullOrEmpty(_config.PanelLayer) ? "Hud" : _config.PanelLayer, Root);
            container.Add(new CuiPanel
            {
                Image = { Color = Hex(_config.PanelColor, _config.PanelOpacity / 100f) },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "1 1", OffsetMax = "-1 -1" },
                CursorEnabled = false
            }, Root, BgName);
        }

        private void AddLogo(CuiElementContainer container, float left, float top, float size)
        {
            if (string.IsNullOrWhiteSpace(_config.PanelLogoUrl)) return;
            container.Add(new CuiElement
            {
                Name = LogoName,
                Parent = BgName,
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

        private void AddSun(CuiElementContainer container, float left, float top, float size)
        {
            string color = Hex(_config.SunColor);
            float body = size * 0.46f;
            float inset = (size - body) * 0.5f;
            float bodyLeft = left + inset;
            float bodyTop = top - inset;
            float bodyBottom = bodyTop - body;
            AddRect(container, SunName, BgName, color, "assets/icons/circle_closed.png", bodyLeft, bodyBottom, bodyLeft + body, bodyTop);
            float thick = 1.5f;
            float ray = size * 0.2f;
            float cx = left + size * 0.5f;
            float mid = (bodyTop + bodyBottom) * 0.5f;
            AddRect(container, SunName + "U", BgName, color, null, cx - thick * 0.5f, bodyTop + 1f, cx + thick * 0.5f, bodyTop + 1f + ray);
            AddRect(container, SunName + "D", BgName, color, null, cx - thick * 0.5f, bodyBottom - 1f - ray, cx + thick * 0.5f, bodyBottom - 1f);
            AddRect(container, SunName + "L", BgName, color, null, bodyLeft - 1f - ray, mid - thick * 0.5f, bodyLeft - 1f, mid + thick * 0.5f);
            AddRect(container, SunName + "R", BgName, color, null, bodyLeft + body + 1f, mid - thick * 0.5f, bodyLeft + body + 1f + ray, mid + thick * 0.5f);
        }

        private void AddToggle(CuiElementContainer container, float left, float top, float width, float height)
        {
            container.Add(new CuiElement
            {
                Name = ToggleName,
                Parent = BgName,
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

        private void AddFill(CuiElementContainer container, float fraction)
        {
            float track = Math.Max(0f, _config.PanelWidth - 16f);
            float inner = Math.Max(0f, track - 2f);
            float width = inner * (fraction < 0f ? 0f : (fraction > 1f ? 1f : fraction));
            string color = _phase == NightshadeVoteDayRules.PhasePassed || _phase == NightshadeVoteDayRules.PhaseBought
                ? Hex(_config.SuccessColor)
                : Hex(_config.AccentColor);
            float right = width < 0.5f ? 1f : 1f + width;
            float top = width < 0.5f ? 1f : 12f;
            container.Add(new CuiElement
            {
                Name = FillName,
                Parent = TrackName,
                DestroyUi = FillName,
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

        private void AddRect(CuiElementContainer container, string name, string parent, string color, string sprite, float left, float bottom, float right, float top)
        {
            var image = new CuiImageComponent { Color = color };
            if (!string.IsNullOrEmpty(sprite)) image.Sprite = sprite;
            container.Add(new CuiElement
            {
                Name = name,
                Parent = parent,
                Components =
                {
                    image,
                    new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = Px(left, bottom), OffsetMax = Px(right, top) }
                }
            });
        }

        private void AddText(CuiElementContainer container, string name, string parent, string text, string color, int size, TextAnchor align, bool bold, float left, float bottom, float right, float top)
        {
            container.Add(new CuiElement
            {
                Name = name,
                Parent = parent,
                Components =
                {
                    TextComponent(text, color, size, align, bold),
                    new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = Px(left, bottom), OffsetMax = Px(right, top) }
                }
            });
        }

        private void AddStretchText(CuiElementContainer container, string name, string parent, string text, string color, int size, TextAnchor align, bool bold, string offsetMin, string offsetMax)
        {
            container.Add(new CuiElement
            {
                Name = name,
                Parent = parent,
                DestroyUi = name,
                Components =
                {
                    TextComponent(text, color, size, align, bold),
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = offsetMin, OffsetMax = offsetMax }
                }
            });
        }

        private void AddEdgeText(CuiElementContainer container, string name, string parent, string text, string color, int size, TextAnchor align, bool bold, string offsetMin, string offsetMax)
        {
            container.Add(new CuiElement
            {
                Name = name,
                Parent = parent,
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

        private string StatusText()
        {
            return Ui(NightshadeVoteDayRules.StatusKey(_phase));
        }

        private string StatusColor()
        {
            string kind = NightshadeVoteDayRules.StatusKind(_phase);
            if (kind == "ok") return Hex(_config.SuccessColor);
            if (kind == "bad") return Hex(_config.FailColor);
            return Hex(_config.MutedColor);
        }

        private string HeadlineColor()
        {
            if (_phase == NightshadeVoteDayRules.PhaseBought && !string.IsNullOrEmpty(_buyerName)) return Hex(_config.AccentColor);
            return Hex(_config.TextColor);
        }

        private static int HeadlineFont(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= 3) return 18;
            if (text.Length > 10) return 11;
            return 13;
        }

        private string Ui(string key)
        {
            string msg = lang.GetMessage(key, this, null);
            if (string.IsNullOrEmpty(msg) || msg == key) return FallbackUi(key);
            return msg;
        }

        private static string FallbackUi(string key)
        {
            if (key == "UiVotes") return "Votes required";
            if (key == "UiVoteHint") return "Type /day to vote";
            if (key == "UiWillSkip") return "The night will be skipped";
            if (key == "UiFailed") return "Not enough votes";
            if (key == "UiPurchased") return "Day purchased";
            return key;
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
            if (!string.Equals(action, "toggle", StringComparison.OrdinalIgnoreCase)) return;
            _collapsed[player.UserIDString] = !IsCollapsed(player);
            SavePanel();
            Show(player);
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
            if (!int.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)) return "1 1 1 1";
            if (!int.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)) return "1 1 1 1";
            if (!int.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b)) return "1 1 1 1";
            float a = alpha >= 0f ? alpha : 1f;
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
        internal const float DefaultHotbarLeft = -204f;
        internal const float DefaultGap = 8f;
        internal const float DefaultBottom = 16f;
        internal const float DefaultWidth = 152f;
        internal const float DefaultHeight = 118f;
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
