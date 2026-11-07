using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using UnityEngine;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("VoteDayTests")]

namespace Oxide.Plugins
{
    [Info("NightshadeVoteDay", "Nightshade", "1.2.2")]
    [Description("Vote for day at configurable night time. 75%+ required. One vote per night. Players can buy day instantly via /buyday.")]
    public class NightshadeVoteDay : RustPlugin
    {
        // --- Config ---
        private const string SkipDataPath = "Nightshade/NightshadeVoteDay";
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

            [JsonProperty("Currency (ServerRewards or Economics)")]
            public string Currency      = "ServerRewards";

            [JsonProperty("ServerRewards Cost")]
            public int RPCost           = 500;

            [JsonProperty("Economics Cost")]
            public double EcoCost       = 500.0;
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
            SaveConfig();
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
                ["VoteProgress"]        = "{current}/{needed} votes ({percent}%). Type /day!",
                ["AlreadyVoted"]        = "You've already voted.",

                ["VotePassed"]          = "Vote Day PASSED \u2014 {votes}/{pop} voted ({percent}%). Time set to {daytime}!",
                ["VotePassedLater"]     = "This night is now morning ({daytime}). The next {nights} night(s) have no vote and run on the normal clock. They are not set to morning.",
                ["VoteFailed"]          = "Vote Day FAILED \u2014 {votes}/{pop} voted ({percent}%). Needed {needed}. See you tomorrow night!",

                ["NoVoteNatural"]       = "No vote tonight. This night runs normally and is not set to morning. {left} more night(s) without a vote after this one.",
                ["NoVoteNaturalLast"]   = "No vote tonight. This night runs normally and is not set to morning. Voting opens again next night.",

                ["NoVoteYet"]           = "No active vote. The window opens at {time} (~{hours} game hour(s) away).",
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
        private HashSet<ulong>  _votes          = new HashSet<ulong>();

        // --- Plugin references ---
        [PluginReference] Plugin ServerRewards;
        [PluginReference] Plugin Economics;

        // --- Hooks ---

        void OnServerInitialized()
        {
            LoadSkips();
            timer.Every(10f, CheckGameTime);
        }

        void Unload()
        {
            _windowTimer?.Destroy();
            _voteTimer?.Destroy();
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

            int    pop    = BasePlayer.activePlayerList.Count;
            int    needed = Mathf.CeilToInt(pop * _config.VoteThreshold);
            int    pct    = Mathf.RoundToInt(_config.VoteThreshold * 100f);

            Say(null, L("VoteStart", null,
                ("time",      FormatHour(_config.VoteStartTime)),
                ("threshold", pct.ToString(CultureInfo.InvariantCulture)),
                ("needed",    needed.ToString(CultureInfo.InvariantCulture)),
                ("duration",  ((int)_config.VoteDuration).ToString(CultureInfo.InvariantCulture))));

            Say(null, L("VoteStartBuyHint"));

            _voteTimer = timer.Once(_config.VoteDuration, EndVote);
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
            int    needed   = Mathf.CeilToInt(pop * _config.VoteThreshold);
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
        }

        private void ForceDayByPurchase(BasePlayer buyer)
        {
            _voteTimer?.Destroy();
            _windowTimer?.Destroy();
            _voteActive     = false;
            _windowOpen     = false;
            _votedThisNight = true;
            _naturalTonight = false;
            _votes.Clear();
            _nightsWithoutVote = QuietNightsAfterPurchase(_nightsWithoutVote);
            RememberNight();
            SaveSkips();
            ApplyDay();

            Say(null, L("BuyDayPurchasedOne", null,
                ("player",  buyer.displayName),
                ("cost",    CostLabel()),
                ("daytime", FormatHour(_config.DayTime))));
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
            int needed    = Mathf.CeilToInt(pop * _config.VoteThreshold);
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
            }
        }

        // --- /buyday command ---

        [ChatCommand("buyday")]
        private void CmdBuyDay(BasePlayer player, string command, string[] args)
        {
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
            if (_config.Currency != null && _config.Currency.ToLowerInvariant() == "economics")
                return _config.EcoCost.ToString(CultureInfo.InvariantCulture) + " coins";
            return _config.RPCost.ToString(CultureInfo.InvariantCulture) + " RP";
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
    }
}
