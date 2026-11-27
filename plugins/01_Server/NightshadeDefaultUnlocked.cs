using System;
using System.Collections.Generic;
using System.Globalization;
#if !NIGHTSHADE_SCHEDULE_TESTS
using Newtonsoft.Json;
using Oxide.Core;
#endif

namespace Oxide.Plugins
{
    // Calendar and membership decisions for the monthly default_unlocked cycle.
    // Wall times are America/New_York (DST). The closed interval is
    // [first Thursday 14:00, following Monday 04:00). Before that Thursday the
    // previous cycle is still open. Phase is a function of the clock, so a
    // restart does not wait for the next boundary.
    //
    // That calendar is not a server wipe. A Physgun wipe on any other Thursday
    // stays inside the open phase, and the open phase re-adds everyone. Wipe
    // markers (SaveRestore.WipeId, save file, seed, size, save creation time)
    // are compared on startup. A new marker closes the group until the next
    // configured unlock (Monday 04:00), including when the calendar still says open.
    public static class NightshadeDefaultUnlockedSchedule
    {
        public sealed class Rules
        {
            public DayOfWeek WipeDay = DayOfWeek.Thursday;
            public int WipeOrdinal = 1;
            public int WipeHour = 14;
            public int WipeMinute = 0;
            public DayOfWeek UnlockDay = DayOfWeek.Monday;
            public int UnlockHour = 4;
            public int UnlockMinute = 0;

            public static Rules NightshadeDefaults()
            {
                return new Rules();
            }
        }

        public sealed class Cycle
        {
            public bool Open;
            public DateTime WipeUtc;
            public DateTime OpenUtc;
            public DateTime NextWipeUtc;
            public DateTime NextBoundaryUtc;
        }

        public sealed class MembershipPlan
        {
            public readonly List<string> Add = new List<string>();
            public readonly List<string> Remove = new List<string>();
        }

        public static Cycle Evaluate(DateTime utc, TimeZoneInfo zone, Rules rules)
        {
            if (zone == null) throw new ArgumentNullException("zone");
            if (rules == null) rules = Rules.NightshadeDefaults();
            utc = AsUtc(utc);

            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
            DateTime thisWipeWall = NthWeekday(local.Year, local.Month, rules.WipeDay, rules.WipeOrdinal, rules.WipeHour, rules.WipeMinute);
            DateTime thisWipeUtc = ToUtc(thisWipeWall, zone);

            DateTime cycleWipeWall;
            if (utc < thisWipeUtc)
            {
                int year = local.Year;
                int month = local.Month;
                ShiftMonth(ref year, ref month, -1);
                cycleWipeWall = NthWeekday(year, month, rules.WipeDay, rules.WipeOrdinal, rules.WipeHour, rules.WipeMinute);
            }
            else
            {
                cycleWipeWall = thisWipeWall;
            }

            DateTime openWall = UnlockWall(cycleWipeWall, rules.UnlockDay, rules.UnlockHour, rules.UnlockMinute);
            DateTime wipeUtc = ToUtc(cycleWipeWall, zone);
            DateTime openUtc = ToUtc(openWall, zone);

            DateTime nextWipeWall;
            if (utc < thisWipeUtc)
            {
                nextWipeWall = thisWipeWall;
            }
            else
            {
                int year = cycleWipeWall.Year;
                int month = cycleWipeWall.Month;
                ShiftMonth(ref year, ref month, 1);
                nextWipeWall = NthWeekday(year, month, rules.WipeDay, rules.WipeOrdinal, rules.WipeHour, rules.WipeMinute);
            }

            DateTime nextWipeUtc = ToUtc(nextWipeWall, zone);
            bool open = utc < wipeUtc || utc >= openUtc;
            var cycle = new Cycle();
            cycle.Open = open;
            cycle.WipeUtc = wipeUtc;
            cycle.OpenUtc = openUtc;
            cycle.NextWipeUtc = nextWipeUtc;
            cycle.NextBoundaryUtc = open ? nextWipeUtc : openUtc;
            return cycle;
        }

        // Same daily cap as NightshadePVPWeekend.ScheduleBoundary: wake at the
        // boundary, but at least once a day so a clock or DST correction is not missed.
        public static double WakeSeconds(DateTime utc, DateTime boundaryUtc, int dailyCapSeconds)
        {
            utc = AsUtc(utc);
            boundaryUtc = AsUtc(boundaryUtc);
            double seconds = (boundaryUtc - utc).TotalSeconds;
            if (seconds < 1d) seconds = 1d;
            int cap = dailyCapSeconds < 1 ? 86400 : dailyCapSeconds;
            if (seconds > cap) seconds = cap;
            return seconds;
        }

        public static DateTime NthWeekday(int year, int month, DayOfWeek day, int ordinal, int hour, int minute)
        {
            if (ordinal < 1) ordinal = 1;
            if (hour < 0) hour = 0;
            if (hour > 23) hour = 23;
            if (minute < 0) minute = 0;
            if (minute > 59) minute = 59;
            var first = new DateTime(year, month, 1, hour, minute, 0, DateTimeKind.Unspecified);
            int delta = ((int)day - (int)first.DayOfWeek + 7) % 7;
            DateTime result = first.AddDays(delta + (7 * (ordinal - 1)));
            if (result.Month != month) result = result.AddDays(-7);
            return result;
        }

        // First unlock weekday strictly after the wipe's calendar day, at the unlock time.
        public static DateTime UnlockWall(DateTime wipeWall, DayOfWeek unlockDay, int hour, int minute)
        {
            if (hour < 0) hour = 0;
            if (hour > 23) hour = 23;
            if (minute < 0) minute = 0;
            if (minute > 59) minute = 59;
            int delta = ((int)unlockDay - (int)wipeWall.DayOfWeek + 7) % 7;
            if (delta == 0) delta = 7;
            return wipeWall.Date.AddDays(delta).AddHours(hour).AddMinutes(minute);
        }

        public static DateTime ToUtc(DateTime wall, TimeZoneInfo zone)
        {
            DateTime local = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
            // Thursday 14:00 and Monday 04:00 are outside the 02:00 US transition.
            // A configured time that lands in the spring-forward gap moves forward
            // to the next valid wall time. Ambiguous fall-back times use standard
            // time, which is what TimeZoneInfo.ConvertTimeToUtc does.
            int guard = 0;
            while (zone.IsInvalidTime(local) && guard < 3)
            {
                local = local.AddHours(1);
                guard++;
            }
            return TimeZoneInfo.ConvertTimeToUtc(local, zone);
        }

        public static TimeZoneInfo ResolveZone(string id, out bool fallback)
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
            return FallbackEastern();
        }

        // Same built-in US Eastern zone NightshadeInfo and NightshadePVPWeekend use
        // when the host has no America/New_York data: EST (UTC-5), EDT (UTC-4),
        // second Sunday in March 02:00 through first Sunday in November 02:00.
        public static TimeZoneInfo FallbackEastern()
        {
            try
            {
                TimeZoneInfo.TransitionTime start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
                TimeZoneInfo.TransitionTime end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
                TimeZoneInfo.AdjustmentRule rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2007, 1, 1), new DateTime(9999, 12, 31), TimeSpan.FromHours(1), start, end);
                return TimeZoneInfo.CreateCustomTimeZone("Nightshade-Eastern", TimeSpan.FromHours(-5), "Eastern Time", "EST", "EDT", new[] { rule });
            }
            catch (Exception)
            {
                return TimeZoneInfo.CreateCustomTimeZone("Nightshade-Eastern-EST", TimeSpan.FromHours(-5), "Eastern Time", "EST");
            }
        }

        public static DayOfWeek ParseDay(string name, DayOfWeek fallback)
        {
            DayOfWeek day;
            if (!string.IsNullOrEmpty(name) && Enum.TryParse(name, true, out day)) return day;
            return fallback;
        }

        public static bool IsPlayerId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            ulong value;
            if (!ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value == 0UL) return false;
            int digits = 0;
            while (value > 0UL)
            {
                value /= 10UL;
                digits++;
            }
            return digits >= 17;
        }

        // Carbon's GetUsersInGroup returns "steamid (last name)".
        public static string ParseMemberId(string entry)
        {
            if (string.IsNullOrWhiteSpace(entry)) return null;
            string text = entry.Trim();
            int paren = text.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0) text = text.Substring(0, paren).Trim();
            else
            {
                int space = text.IndexOf(' ');
                if (space > 0) text = text.Substring(0, space);
            }
            return IsPlayerId(text) ? text : null;
        }

        // Closed: remove every direct member. Open: add every known player who is
        // not already a direct member. Neither side edits any other group.
        public static MembershipPlan Build(bool open, IEnumerable<string> knownIds, IEnumerable<string> directMemberRows)
        {
            var known = new HashSet<string>(StringComparer.Ordinal);
            if (knownIds != null)
            {
                foreach (string id in knownIds)
                {
                    if (IsPlayerId(id)) known.Add(id);
                }
            }

            var members = new HashSet<string>(StringComparer.Ordinal);
            if (directMemberRows != null)
            {
                foreach (string row in directMemberRows)
                {
                    string id = ParseMemberId(row);
                    if (id != null) members.Add(id);
                }
            }

            var plan = new MembershipPlan();
            if (open)
            {
                foreach (string id in known)
                {
                    if (!members.Contains(id)) plan.Add.Add(id);
                }
                plan.Add.Sort(StringComparer.Ordinal);
            }
            else
            {
                foreach (string id in members) plan.Remove.Add(id);
                plan.Remove.Sort(StringComparer.Ordinal);
            }
            return plan;
        }

        // "unread" means the live save identity is not ready.
        // "baseline" records the first marker and does not clear.
        // "refresh" fills in a late WipeId or save name for the same seed, size, and creation time.
        // "clear" is a different save. With no stored marker, a new save still counts when
        // this boot's OnNewSave hook ran, or when the save was created after the last
        // membership pass (lastReconcileUtcTicks > 0). A first install with neither signal
        // only records the marker.
        public static string ClassifyWipe(string storedMarker, string currentMarker, long saveCreatedUtcTicks, long lastReconcileUtcTicks, bool newSaveHook)
        {
            if (string.IsNullOrEmpty(currentMarker)) return "unread";
            if (string.IsNullOrEmpty(storedMarker))
            {
                if (newSaveHook) return "clear";
                if (lastReconcileUtcTicks > 0L && saveCreatedUtcTicks > lastReconcileUtcTicks) return "clear";
                return "baseline";
            }
            if (SameWipe(storedMarker, currentMarker))
                return string.Equals(storedMarker, currentMarker, StringComparison.Ordinal) ? "same" : "refresh";
            return "clear";
        }

        // Same world when seed, size, and save-creation ticks match. An empty wipe id or
        // save name on only one side is the same wipe (the id is often filled in late).
        public static bool SameWipe(string storedMarker, string currentMarker)
        {
            if (string.Equals(storedMarker, currentMarker, StringComparison.Ordinal)) return true;
            string[] stored = SplitMarker(storedMarker);
            string[] current = SplitMarker(currentMarker);
            if (stored == null || current == null) return false;
            if (!string.Equals(stored[2], current[2], StringComparison.Ordinal)) return false;
            if (!string.Equals(stored[3], current[3], StringComparison.Ordinal)) return false;
            if (!string.Equals(stored[4], current[4], StringComparison.Ordinal)) return false;
            if (!SameOrBlank(stored[0], current[0])) return false;
            if (!SameOrBlank(stored[1], current[1])) return false;
            return true;
        }

        // True when the current string is at least as filled-in as the stored one.
        public static bool PreferCurrentMarker(string storedMarker, string currentMarker)
        {
            string[] stored = SplitMarker(storedMarker);
            string[] current = SplitMarker(currentMarker);
            if (stored == null || current == null) return true;
            return FilledScore(current) >= FilledScore(stored);
        }

        public static string FormatMarker(string wipeId, string saveFile, string seed, string mapSize, long createdUtcTicks)
        {
            return SanitizeMarkerPart(wipeId)
                + "|" + SanitizeMarkerPart(saveFile)
                + "|" + NormalizeCount(seed)
                + "|" + NormalizeCount(mapSize)
                + "|" + createdUtcTicks.ToString(CultureInfo.InvariantCulture);
        }

        public static bool MarkerReady(string wipeId, string saveFile, string mapSize, long createdUtcTicks)
        {
            long size;
            if (!long.TryParse(NormalizeCount(mapSize), NumberStyles.Integer, CultureInfo.InvariantCulture, out size) || size <= 0L)
                return false;
            if (createdUtcTicks < new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks) return false;
            if (createdUtcTicks > DateTime.MaxValue.Ticks) return false;
            if (string.IsNullOrEmpty(SanitizeMarkerPart(wipeId)) && string.IsNullOrEmpty(SanitizeMarkerPart(saveFile))) return false;
            return true;
        }

        public static string SanitizeMarkerPart(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            char[] chars = value.ToCharArray();
            bool dirty = false;
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (c == '|' || c < 32 || c > 126)
                {
                    chars[i] = '_';
                    dirty = true;
                }
            }
            return dirty ? new string(chars) : value;
        }

        public static string NormalizeCount(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string clean = SanitizeMarkerPart(value.Trim());
            long whole;
            if (long.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out whole))
                return whole.ToString(CultureInfo.InvariantCulture);
            double num;
            if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out num))
            {
                long rounded = (long)Math.Round(num);
                if (Math.Abs(num - rounded) < 0.001d) return rounded.ToString(CultureInfo.InvariantCulture);
            }
            return clean;
        }

        // Closed while the monthly calendar is closed, and also until the post-wipe unlock.
        public static bool MembershipOpen(bool calendarOpen, long holdClosedUntilUtcTicks, DateTime utc)
        {
            if (!calendarOpen) return false;
            if (holdClosedUntilUtcTicks <= 0L) return true;
            return AsUtc(utc).Ticks >= holdClosedUntilUtcTicks;
        }

        // Next configured unlock strictly after `afterUtc` (Monday 04:00 Eastern by default).
        public static DateTime NextUnlockUtc(DateTime afterUtc, TimeZoneInfo zone, Rules rules)
        {
            if (zone == null) throw new ArgumentNullException("zone");
            if (rules == null) rules = Rules.NightshadeDefaults();
            afterUtc = AsUtc(afterUtc);
            int hour = rules.UnlockHour;
            int minute = rules.UnlockMinute;
            if (hour < 0) hour = 0;
            if (hour > 23) hour = 23;
            if (minute < 0) minute = 0;
            if (minute > 59) minute = 59;
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone);
            var day = new DateTime(local.Year, local.Month, local.Day, hour, minute, 0, DateTimeKind.Unspecified);
            int delta = ((int)rules.UnlockDay - (int)local.DayOfWeek + 7) % 7;
            DateTime candidateUtc = ToUtc(day.AddDays(delta), zone);
            if (candidateUtc <= afterUtc) candidateUtc = ToUtc(day.AddDays(delta + 7), zone);
            return candidateUtc;
        }

        public static DateTime EarlierWake(DateTime calendarBoundaryUtc, long holdClosedUntilUtcTicks, DateTime utc)
        {
            utc = AsUtc(utc);
            DateTime best = AsUtc(calendarBoundaryUtc);
            if (holdClosedUntilUtcTicks > utc.Ticks && holdClosedUntilUtcTicks < best.Ticks)
            {
                try { best = new DateTime(holdClosedUntilUtcTicks, DateTimeKind.Utc); }
                catch (ArgumentOutOfRangeException) { }
            }
            return best;
        }

        private static string[] SplitMarker(string marker)
        {
            if (string.IsNullOrEmpty(marker)) return null;
            string[] parts = marker.Split('|');
            if (parts.Length != 5) return null;
            return parts;
        }

        private static bool SameOrBlank(string left, string right)
        {
            if (string.Equals(left, right, StringComparison.Ordinal)) return true;
            return string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right);
        }

        private static int FilledScore(string[] parts)
        {
            int score = 0;
            if (!string.IsNullOrEmpty(parts[0])) score++;
            if (!string.IsNullOrEmpty(parts[1])) score++;
            return score;
        }

        private static DateTime AsUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc) return value;
            if (value.Kind == DateTimeKind.Local) return value.ToUniversalTime();
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private static void ShiftMonth(ref int year, ref int month, int delta)
        {
            month += delta;
            while (month > 12)
            {
                month -= 12;
                year++;
            }
            while (month < 1)
            {
                month += 12;
                year--;
            }
        }
    }

#if !NIGHTSHADE_SCHEDULE_TESTS
    [Info("NightshadeDefaultUnlocked", "Nightshade", "1.1.0")]
    [Description("Clears default_unlocked on a new server wipe and during the monthly Thursday-to-Monday lock, then enrolls known players.")]
    public class NightshadeDefaultUnlocked : RustPlugin
    {
        private const string PermAdmin = "nightshadedefaultunlocked.admin";
        private const string DataPath = "Nightshade/NightshadeDefaultUnlocked/State";
        private const int MaxFollowUps = 3;
        private const int MaxWipeReads = 6;

        private Settings settings;
        private State state;
        private TimeZoneInfo zone;
        private bool zoneFallback;
        private bool ready;
        private bool serverReady;
        private string groupName = "default_unlocked";
        private Timer boundaryTimer;
        private Timer followTimer;
        private Timer wipeRetryTimer;
        private Timer baselineTimer;
        private bool baselineDeferred;
        private bool baselinedThisBoot;
        private int suppress;
        private int followUps;
        private bool followQueued;
        private bool writerWarned;
        private bool missingGroupWarned;
        private bool rowsWarned;
        private int groupWaitAttempts;
        private string lastWipeReadError = "";
        private string inheritanceNote = "not checked";
        private readonly HashSet<string> applying = new HashSet<string>(StringComparer.Ordinal);

        // Live save identity from the last successful read. Not written into this file.
        private string liveMarker = "";
        private string liveWipeId = "";
        private string liveSaveFile = "";
        private string liveSeed = "";
        private string liveMapSize = "";
        private long liveCreatedTicks;
        private string lastWipeClass = "";
        private int wipeReadAttempts;
        private bool wipeReadFailedLogged;
        private bool onNewSaveSeen;
        private string onNewSaveFile = "";
        private bool wantNewSaveHint;
        private bool newSaveHintLogged;

        // Armed until the group pass commits the marker into data/Nightshade/NightshadeDefaultUnlocked/.
        private bool wipeClearPending;
        private bool pendingHold;
        private string pendingMarker = "";
        private string pendingWipeId = "";
        private string pendingSaveFile = "";
        private string pendingSeed = "";
        private string pendingMapSize = "";
        private long pendingCreatedTicks;
        private long pendingUnlockTicks;
        private string pendingSource = "";

        private class Settings
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Group name")] public string GroupName = "default_unlocked";
            [JsonProperty("Time zone id")] public string TimeZoneId = "America/New_York";
            [JsonProperty("Wipe weekday")] public string WipeWeekday = "Thursday";
            [JsonProperty("Wipe ordinal in month (1 = first)")] public int WipeOrdinal = 1;
            [JsonProperty("Wipe hour (0-23, Eastern wall time)")] public int WipeHour = 14;
            [JsonProperty("Wipe minute (0-59)")] public int WipeMinute = 0;
            [JsonProperty("Unlock weekday (first one after the wipe)")] public string UnlockWeekday = "Monday";
            [JsonProperty("Unlock hour (0-23, Eastern wall time)")] public int UnlockHour = 4;
            [JsonProperty("Unlock minute (0-59)")] public int UnlockMinute = 0;
            [JsonProperty("Daily schedule wake seconds")] public int DailyWakeSeconds = 86400;
        }

        // Written for operators. Reconcile never trusts LastPhase; the clock decides.
        private class State
        {
            public string LastPhase = "";
            public long LastReconcileUtcTicks;
            public long CycleWipeUtcTicks;
            public long CycleOpenUtcTicks;
            public long NextBoundaryUtcTicks;
            public int LastAdded;
            public int LastRemoved;
            public string LastReason = "";
            // Save identity of the wipe we already handled. Compared on startup.
            public string WipeMarker = "";
            public string WipeId = "";
            public string WipeSaveFile = "";
            public string WipeSeed = "";
            public string WipeMapSize = "";
            public long WipeSaveCreatedUtcTicks;
            public long WipeDetectedUtcTicks;
            public string WipeSource = "";
            // Membership stays closed until this UTC instant after a real wipe or ndu.clear.
            public long HoldClosedUntilUtcTicks;
            public int LastClearRemoved;
            public long LastClearUtcTicks;
            public string LastClearSource = "";
        }

        protected override void LoadDefaultConfig()
        {
            settings = new Settings();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                settings = Config.ReadObject<Settings>() ?? new Settings();
            }
            catch (Exception ex)
            {
                PrintWarning("NightshadeDefaultUnlocked config could not be read. Using defaults. " + ex.Message);
                settings = new Settings();
            }
            NormalizeSettings();
            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(settings, true);
        }

        private void NormalizeSettings()
        {
            if (settings == null) settings = new Settings();
            if (string.IsNullOrWhiteSpace(settings.GroupName)) settings.GroupName = "default_unlocked";
            settings.GroupName = settings.GroupName.Trim();
            if (string.IsNullOrWhiteSpace(settings.TimeZoneId)) settings.TimeZoneId = "America/New_York";
            settings.TimeZoneId = settings.TimeZoneId.Trim();
            if (settings.WipeOrdinal < 1) settings.WipeOrdinal = 1;
            if (settings.WipeHour < 0 || settings.WipeHour > 23) settings.WipeHour = 14;
            if (settings.WipeMinute < 0 || settings.WipeMinute > 59) settings.WipeMinute = 0;
            if (settings.UnlockHour < 0 || settings.UnlockHour > 23) settings.UnlockHour = 4;
            if (settings.UnlockMinute < 0 || settings.UnlockMinute > 59) settings.UnlockMinute = 0;
            if (settings.DailyWakeSeconds < 1) settings.DailyWakeSeconds = 86400;
        }

        private void Init()
        {
            if (settings == null) settings = new Settings();
            NormalizeSettings();
            groupName = settings.GroupName;
            // Registered only. This plugin does not grant the permission to a user or a group.
            permission.RegisterPermission(PermAdmin, this);
            // These commands take no arguments. ConsoleSystem.Arg.Args is Facepunch.StringView[] on Carbon; do not index it.
            cmd.AddConsoleCommand("ndu.status", this, nameof(CmdStatus));
            cmd.AddConsoleCommand("ndu.reconcile", this, nameof(CmdReconcile));
            cmd.AddConsoleCommand("ndu.clear", this, nameof(CmdClear));
            try
            {
                state = Interface.Oxide.DataFileSystem.ReadObject<State>(DataPath) ?? new State();
            }
            catch (Exception ex)
            {
                PrintWarning("Could not read cycle state. Starting a fresh diagnostic file. " + ex.Message);
                state = new State();
            }
            if (state == null) state = new State();
            NormalizeState();
            zone = NightshadeDefaultUnlockedSchedule.ResolveZone(settings.TimeZoneId, out zoneFallback);
            if (zoneFallback)
                PrintWarning("Time zone '" + settings.TimeZoneId + "' was not found. Using the built-in US Eastern zone (EST/EDT).");
            WarnWeekday(settings.WipeWeekday, "Wipe", "Thursday");
            WarnWeekday(settings.UnlockWeekday, "Unlock", "Monday");
            ready = true;
            Puts("default_unlocked cycle zone " + zone.Id + (zoneFallback ? " (fallback)" : "")
                + ". Wipe " + settings.WipeWeekday + " ordinal " + settings.WipeOrdinal + " at "
                + settings.WipeHour.ToString("00", CultureInfo.InvariantCulture) + ":"
                + settings.WipeMinute.ToString("00", CultureInfo.InvariantCulture)
                + ". Unlock the following " + settings.UnlockWeekday + " at "
                + settings.UnlockHour.ToString("00", CultureInfo.InvariantCulture) + ":"
                + settings.UnlockMinute.ToString("00", CultureInfo.InvariantCulture) + ".");
        }

        private void OnServerInitialized()
        {
            serverReady = true;
            inheritanceNote = DescribeInheritance();
            if (inheritanceNote.StartsWith("These groups", StringComparison.Ordinal))
                PrintWarning(inheritanceNote);
            string source = onNewSaveSeen ? "OnNewSave" : "load";
            ApplyWipeMarker(source);
            if (wipeClearPending)
            {
                wantNewSaveHint = false;
                Reconcile("wipe");
            }
            else Reconcile("load");
            if (onNewSaveSeen && !wipeClearPending && (lastWipeClass == "same" || lastWipeClass == "refresh" || lastWipeClass == "baseline"))
                LaterWipeRead(5f, "OnNewSave", true);
        }

        // Map-save hook. It does not run for a blueprint-only wipe, and it is missed when
        // the plugin loads after the hook or when the wiper deletes files before boot and
        // Carbon never marks the save as new. The stored marker check in OnServerInitialized
        // is what clears the group. This hook only forces that check to run again.
        private void OnNewSave(string filename)
        {
            onNewSaveSeen = true;
            onNewSaveFile = filename ?? "";
            wantNewSaveHint = true;
            newSaveHintLogged = false;
            Puts("OnNewSave '" + onNewSaveFile + "'. Comparing the stored wipe marker. A blueprint-only wipe does not change it.");
            if (!serverReady) return;
            ApplyWipeMarker("OnNewSave");
            if (wipeClearPending)
            {
                wantNewSaveHint = false;
                Reconcile("wipe");
            }
            else LaterWipeRead(5f, "OnNewSave", true);
        }

        private void Unload()
        {
            ready = false;
            serverReady = false;
            if (boundaryTimer != null) boundaryTimer.Destroy();
            if (followTimer != null) followTimer.Destroy();
            if (wipeRetryTimer != null) wipeRetryTimer.Destroy();
            if (baselineTimer != null) baselineTimer.Destroy();
            boundaryTimer = null;
            followTimer = null;
            wipeRetryTimer = null;
            baselineTimer = null;
        }

        private void OnServerSave()
        {
            SaveState();
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (!ready || player == null || settings == null || !settings.Enabled) return;
            string id = player.UserIDString;
            if (!NightshadeDefaultUnlockedSchedule.IsPlayerId(id)) return;
            if (!GroupReady()) return;
            if (Enrolling(CurrentCycle())) AddOne(id);
            else RemoveOne(id);
        }

        private void OnUserGroupAdded(string id, string name)
        {
            if (suppress > 0 || !ready || settings == null || !settings.Enabled) return;
            if (!SameGroup(name) || !NightshadeDefaultUnlockedSchedule.IsPlayerId(id)) return;
            if (!Enrolling(CurrentCycle())) RemoveOne(id);
        }

        private void OnUserGroupRemoved(string id, string name)
        {
            if (suppress > 0 || !ready || settings == null || !settings.Enabled) return;
            if (!SameGroup(name) || !NightshadeDefaultUnlockedSchedule.IsPlayerId(id)) return;
            if (Enrolling(CurrentCycle())) AddOne(id);
        }

        private void CmdStatus(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAdmin(arg)) return;
            arg.ReplyWith(StatusText());
        }

        private void CmdReconcile(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAdmin(arg)) return;
            if (settings == null || !settings.Enabled)
            {
                arg.ReplyWith("NightshadeDefaultUnlocked is disabled. No membership change was made.");
                return;
            }
            Reconcile("command");
            arg.ReplyWith(StatusText());
        }

        private void CmdClear(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAdmin(arg)) return;
            if (settings == null || !settings.Enabled)
            {
                arg.ReplyWith("NightshadeDefaultUnlocked is disabled. No membership change was made.");
                return;
            }
            if (zone == null)
            {
                arg.ReplyWith("Time zone is not ready. No membership change was made.");
                return;
            }
            if (!GroupReady())
            {
                arg.ReplyWith("Group '" + groupName + "' does not exist. No membership change was made.");
                return;
            }
            if (state == null) state = new State();
            DateTime unlock = NightshadeDefaultUnlockedSchedule.NextUnlockUtc(DateTime.UtcNow, zone, RulesFromSettings());
            state.HoldClosedUntilUtcTicks = unlock.Ticks;
            SaveState();
            Reconcile("clear");
            if (state.LastReason != "clear")
            {
                arg.ReplyWith("Could not list members of " + groupName + ". Holding closed until " + FormatTicks(state.HoldClosedUntilUtcTicks) + ". The clear will be tried again.");
                return;
            }
            arg.ReplyWith("Removed " + state.LastClearRemoved.ToString(CultureInfo.InvariantCulture)
                + " from " + groupName
                + ". Holding closed until " + FormatTicks(state.HoldClosedUntilUtcTicks)
                + ". Known players are enrolled again at that unlock. Direct members now "
                + MemberCount().ToString(CultureInfo.InvariantCulture) + ".");
        }

        private void Reconcile(string reason)
        {
            if (!ready || settings == null) return;
            NightshadeDefaultUnlockedSchedule.Cycle cycle = CurrentCycle();
            if (!settings.Enabled)
            {
                Schedule(cycle);
                return;
            }
            if (!GroupReady())
            {
                bool waiting = wipeClearPending || reason == "clear" || reason == "wipe";
                if (waiting && groupWaitAttempts < 12)
                {
                    groupWaitAttempts++;
                    ScheduleSoon(wipeClearPending ? "wipe" : "clear");
                }
                else
                {
                    if (waiting)
                        PrintWarning("Group '" + groupName + "' is missing, so " + groupName + " was not cleared. The stored wipe marker was not updated.");
                    Schedule(cycle);
                }
                return;
            }
            groupWaitAttempts = 0;

            if (reason == "load" || reason == "timer" || reason == "command" || reason == "wipe" || reason == "clear") followUps = 0;

            bool open = Enrolling(cycle);
            List<string> known = KnownIds();
            string[] rows = permission.GetUsersInGroup(groupName);
            if (rows == null)
            {
                if (!rowsWarned)
                {
                    rowsWarned = true;
                    PrintWarning("Could not list members of " + groupName + ". Membership was left unchanged.");
                }
                if (wipeClearPending || reason == "clear") ScheduleSoon(wipeClearPending ? "wipe" : "clear");
                else Schedule(cycle);
                return;
            }
            NightshadeDefaultUnlockedSchedule.MembershipPlan plan = NightshadeDefaultUnlockedSchedule.Build(open, known, rows);
            int added = 0;
            int removed = 0;
            for (int i = 0; i < plan.Remove.Count; i++)
            {
                if (RemoveOne(plan.Remove[i])) removed++;
            }
            for (int i = 0; i < plan.Add.Count; i++)
            {
                if (AddOne(plan.Add[i])) added++;
            }

            string phase = open ? "open" : "closed";
            bool changed = state == null || !string.Equals(state.LastPhase, phase, StringComparison.Ordinal) || added > 0 || removed > 0;
            if (state == null) state = new State();
            bool reportWipe = wipeClearPending;
            bool heldWipe = pendingHold;
            string armedSource = pendingSource;
            long armedUnlock = pendingUnlockTicks;
            if (wipeClearPending) CommitArmedWipe(removed);
            if (reason == "clear")
            {
                state.LastClearRemoved = removed;
                state.LastClearUtcTicks = DateTime.UtcNow.Ticks;
                state.LastClearSource = "command";
            }
            state.LastPhase = phase;
            state.LastReconcileUtcTicks = DateTime.UtcNow.Ticks;
            state.CycleWipeUtcTicks = cycle.WipeUtc.Ticks;
            state.CycleOpenUtcTicks = cycle.OpenUtc.Ticks;
            state.NextBoundaryUtcTicks = cycle.NextBoundaryUtc.Ticks;
            state.LastAdded = added;
            state.LastRemoved = removed;
            state.LastReason = reason ?? "";
            SaveState();

            DateTime until = EffectiveUntil(cycle, open);
            if (reason == "load" || reason == "command" || reason == "wipe" || reason == "clear" || changed)
            {
                string why = (!open && cycle.Open) ? " wipe-hold" : "";
                Puts(groupName + " is " + (open ? "OPEN" : "CLOSED") + why
                    + " until " + FormatWall(until)
                    + ". Added " + added.ToString(CultureInfo.InvariantCulture)
                    + ", removed " + removed.ToString(CultureInfo.InvariantCulture)
                    + ". Known " + known.Count.ToString(CultureInfo.InvariantCulture)
                    + ". Zone " + zone.Id + (zoneFallback ? " (fallback)" : "")
                    + ". Reason " + reason + ".");
            }
            if (reportWipe)
            {
                if (heldWipe)
                    Puts(groupName + " wipe clear: removed " + removed.ToString(CultureInfo.InvariantCulture)
                        + ". Wipe id '" + state.WipeId
                        + "'. Save created " + FormatTicks(state.WipeSaveCreatedUtcTicks)
                        + ". Holding closed until " + FormatTicks(armedUnlock)
                        + ". Source " + armedSource + ".");
                else
                    Puts(groupName + " wipe detected after its unlock " + FormatTicks(armedUnlock)
                        + ". Wipe id '" + state.WipeId
                        + "'. Save created " + FormatTicks(state.WipeSaveCreatedUtcTicks)
                        + ". The group was not held closed. This pass removed " + removed.ToString(CultureInfo.InvariantCulture)
                        + " and added " + added.ToString(CultureInfo.InvariantCulture)
                        + ". Source " + armedSource + ".");
            }
            if (reason == "clear")
                Puts(groupName + " manual clear: removed " + removed.ToString(CultureInfo.InvariantCulture)
                    + ". Holding closed until " + FormatTicks(state.HoldClosedUntilUtcTicks) + ".");
            Schedule(cycle);
        }

        private void Schedule(NightshadeDefaultUnlockedSchedule.Cycle cycle)
        {
            if (boundaryTimer != null) boundaryTimer.Destroy();
            boundaryTimer = null;
            if (settings == null || !settings.Enabled || cycle == null) return;
            long hold = state == null ? 0L : state.HoldClosedUntilUtcTicks;
            DateTime wakeAt = NightshadeDefaultUnlockedSchedule.EarlierWake(cycle.NextBoundaryUtc, hold, DateTime.UtcNow);
            double seconds = NightshadeDefaultUnlockedSchedule.WakeSeconds(DateTime.UtcNow, wakeAt, settings.DailyWakeSeconds);
            boundaryTimer = timer.Once((float)seconds, delegate { Reconcile("timer"); });
        }

        private void ScheduleSoon(string reason)
        {
            if (boundaryTimer != null) boundaryTimer.Destroy();
            boundaryTimer = timer.Once(5f, delegate { Reconcile(reason); });
        }

        private bool AddOne(string id)
        {
            if (!NightshadeDefaultUnlockedSchedule.IsPlayerId(id) || permission == null) return false;
            if (!applying.Add(id)) return false;
            try
            {
                if (permission.UserHasGroup(id, groupName)) return false;
                suppress++;
                try { permission.AddUserGroup(id, groupName, true); }
                finally { suppress--; }
                if (!permission.UserHasGroup(id, groupName))
                {
                    NoteCompetingWriter();
                    return false;
                }
                return true;
            }
            finally
            {
                applying.Remove(id);
            }
        }

        private bool RemoveOne(string id)
        {
            if (!NightshadeDefaultUnlockedSchedule.IsPlayerId(id) || permission == null) return false;
            if (!applying.Add(id)) return false;
            try
            {
                if (!permission.UserHasGroup(id, groupName)) return false;
                suppress++;
                try { permission.RemoveUserGroup(id, groupName); }
                finally { suppress--; }
                if (permission.UserHasGroup(id, groupName))
                {
                    NoteCompetingWriter();
                    return false;
                }
                return true;
            }
            finally
            {
                applying.Remove(id);
            }
        }

        private void NoteCompetingWriter()
        {
            if (!writerWarned)
            {
                writerWarned = true;
                PrintWarning("Another hook changed " + groupName + " membership during reconcile. This plugin will try again. No other group, permission, or ban was edited.");
            }
            if (followQueued || followUps >= MaxFollowUps) return;
            followUps++;
            followQueued = true;
            if (followTimer != null) followTimer.Destroy();
            followTimer = timer.Once(1f, delegate
            {
                followQueued = false;
                followTimer = null;
                Reconcile("follow-up");
            });
        }

        private bool GroupReady()
        {
            if (permission != null && permission.GroupExists(groupName)) return true;
            if (!missingGroupWarned)
            {
                missingGroupWarned = true;
                PrintWarning("Group '" + groupName + "' does not exist. No group was created and no permission was granted. Membership was left unchanged.");
            }
            return false;
        }

        private void WarnWeekday(string value, string label, string fallbackName)
        {
            DayOfWeek ignored;
            if (!Enum.TryParse(value, true, out ignored))
                PrintWarning(label + " weekday '" + value + "' is not a weekday name. Using " + fallbackName + ".");
        }

        private string DescribeInheritance()
        {
            if (permission == null || !permission.GroupExists(groupName))
                return "group missing";
            string parent = permission.GetGroupParent(groupName);
            if (!string.IsNullOrEmpty(parent))
                Puts(groupName + " parent group is '" + parent + "'. That parent was not modified. Direct members still gain and lose this group's own permissions with membership.");
            string[] groups = permission.GetGroups();
            if (groups == null) return "";
            var children = new List<string>();
            for (int i = 0; i < groups.Length; i++)
            {
                string candidate = groups[i];
                if (string.IsNullOrEmpty(candidate) || SameGroup(candidate)) continue;
                string candidateParent = permission.GetGroupParent(candidate);
                if (!string.IsNullOrEmpty(candidateParent) && SameGroup(candidateParent))
                    children.Add(candidate);
            }
            if (children.Count == 0) return "no group inherits " + groupName;
            children.Sort(StringComparer.OrdinalIgnoreCase);
            return "These groups inherit " + groupName + ", so their members keep its permissions after direct members are removed: "
                + string.Join(", ", children.ToArray())
                + ". Their parent was not changed. Remove that inheritance manually if the closed period must cover them.";
        }

        private List<string> KnownIds()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (permission != null && permission.userdata != null)
            {
                foreach (string key in permission.userdata.Keys)
                {
                    if (NightshadeDefaultUnlockedSchedule.IsPlayerId(key)) ids.Add(key);
                }
            }
            AddConnected(BasePlayer.activePlayerList, ids);
            AddConnected(BasePlayer.sleepingPlayerList, ids);
            var list = new List<string>(ids);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        private static void AddConnected(IEnumerable<BasePlayer> players, HashSet<string> ids)
        {
            if (players == null) return;
            foreach (BasePlayer player in players)
            {
                if (player == null) continue;
                string id = player.UserIDString;
                if (NightshadeDefaultUnlockedSchedule.IsPlayerId(id)) ids.Add(id);
            }
        }

        private NightshadeDefaultUnlockedSchedule.Cycle CurrentCycle()
        {
            return NightshadeDefaultUnlockedSchedule.Evaluate(DateTime.UtcNow, zone, RulesFromSettings());
        }

        private NightshadeDefaultUnlockedSchedule.Rules RulesFromSettings()
        {
            var rules = NightshadeDefaultUnlockedSchedule.Rules.NightshadeDefaults();
            if (settings == null) return rules;
            rules.WipeDay = NightshadeDefaultUnlockedSchedule.ParseDay(settings.WipeWeekday, DayOfWeek.Thursday);
            rules.WipeOrdinal = settings.WipeOrdinal < 1 ? 1 : settings.WipeOrdinal;
            rules.WipeHour = settings.WipeHour;
            rules.WipeMinute = settings.WipeMinute;
            rules.UnlockDay = NightshadeDefaultUnlockedSchedule.ParseDay(settings.UnlockWeekday, DayOfWeek.Monday);
            rules.UnlockHour = settings.UnlockHour;
            rules.UnlockMinute = settings.UnlockMinute;
            return rules;
        }

        private bool SameGroup(string name)
        {
            return !string.IsNullOrEmpty(name) && string.Equals(name, groupName, StringComparison.OrdinalIgnoreCase);
        }

        private bool Enrolling(NightshadeDefaultUnlockedSchedule.Cycle cycle)
        {
            if (cycle == null) return false;
            long hold = state == null ? 0L : state.HoldClosedUntilUtcTicks;
            return NightshadeDefaultUnlockedSchedule.MembershipOpen(cycle.Open, hold, DateTime.UtcNow);
        }

        private void ApplyWipeMarker(string source)
        {
            if (state == null) state = new State();
            NormalizeState();
            string marker;
            string wipeId;
            string saveFile;
            string seed;
            string mapSize;
            long createdTicks;
            if (!TryReadLiveWipe(out marker, out wipeId, out saveFile, out seed, out mapSize, out createdTicks))
            {
                lastWipeClass = "unread";
                wipeReadAttempts++;
                if (wipeReadAttempts < MaxWipeReads)
                    LaterWipeRead(2f, source, wantNewSaveHint && source == "OnNewSave");
                else if (!wipeReadFailedLogged)
                {
                    wipeReadFailedLogged = true;
                    PrintWarning("Could not read a wipe marker (SaveRestore.WipeId, save file, map seed, map size, save creation time). "
                        + groupName + " was not cleared."
                        + (string.IsNullOrEmpty(lastWipeReadError) ? "" : " " + lastWipeReadError));
                }
                return;
            }

            wipeReadAttempts = 0;
            wipeReadFailedLogged = false;
            if (zone == null)
            {
                lastWipeClass = "unread";
                PrintWarning("Time zone is not ready. The wipe marker was not stored and " + groupName + " was not cleared.");
                return;
            }

            long previousReconcile = state.LastReconcileUtcTicks;
            string kind = NightshadeDefaultUnlockedSchedule.ClassifyWipe(state.WipeMarker, marker, createdTicks, previousReconcile, onNewSaveSeen);
            // OnNewSave can land after this boot already recorded the new save and skipped the clear.
            if (kind == "same" && baselinedThisBoot && onNewSaveSeen) kind = "clear";
            lastWipeClass = kind;
            if (settings != null && !settings.Enabled)
            {
                if (kind == "baseline" || (kind == "refresh" && NightshadeDefaultUnlockedSchedule.PreferCurrentMarker(state.WipeMarker, marker)))
                {
                    RememberMarker(marker, wipeId, saveFile, seed, mapSize, createdTicks, source, true);
                    SaveState();
                    Puts("Wipe marker " + kind + " while NightshadeDefaultUnlocked is disabled. " + groupName + " was not changed. Wipe id '" + wipeId + "'.");
                }
                else if (kind == "clear")
                    PrintWarning("New wipe marker while NightshadeDefaultUnlocked is disabled. Previous '" + state.WipeMarker + "'. Current '" + marker + "'. " + groupName + " was not changed and the stored marker was left in place. Enable the plugin and reload, or run ndu.clear.");
                return;
            }

            if (kind == "same")
            {
                if (source == "load")
                    Puts("Wipe marker unchanged. Wipe id '" + wipeId + "'. Save created " + FormatTicks(createdTicks) + ". Seed " + seed + ". Size " + mapSize + ".");
                return;
            }

            if (kind == "refresh")
            {
                if (!NightshadeDefaultUnlockedSchedule.PreferCurrentMarker(state.WipeMarker, marker))
                {
                    Puts("Wipe marker still matches seed " + state.WipeSeed + ", size " + state.WipeMapSize + ", save created " + FormatTicks(state.WipeSaveCreatedUtcTicks) + ". Kept the stored wipe id '" + state.WipeId + "'. " + groupName + " was not cleared.");
                    return;
                }
                RememberMarker(marker, wipeId, saveFile, seed, mapSize, createdTicks, state.WipeSource, false);
                SaveState();
                Puts("Updated the stored wipe marker. Seed, size, and save creation time still match, so " + groupName + " was not cleared. Wipe id '" + wipeId + "'. Save created " + FormatTicks(createdTicks) + ".");
                return;
            }

            if (kind == "baseline")
            {
                // Do not write the marker yet. OnNewSave sometimes runs just after the first
                // read; leaving the stored id empty lets that hook still count as a new wipe.
                baselineDeferred = true;
                pendingMarker = marker ?? "";
                pendingWipeId = wipeId ?? "";
                pendingSaveFile = saveFile ?? "";
                pendingSeed = seed ?? "";
                pendingMapSize = mapSize ?? "";
                pendingCreatedTicks = createdTicks;
                pendingSource = source ?? "";
                if (baselineTimer != null) baselineTimer.Destroy();
                baselineTimer = timer.Once(3f, delegate { CommitDeferredBaseline(); });
                return;
            }

            baselineDeferred = false;
            if (baselineTimer != null)
            {
                baselineTimer.Destroy();
                baselineTimer = null;
            }
            string previous = state.WipeMarker ?? "";
            bool migrated = previous.Length == 0;
            bool recordedThenHooked = !migrated && string.Equals(previous, marker, StringComparison.Ordinal);
            ArmWipe(marker, wipeId, saveFile, seed, mapSize, createdTicks, source);
            if (migrated && onNewSaveSeen)
                Puts("OnNewSave '" + onNewSaveFile + "' and no stored wipe marker. Clearing " + groupName + ". Save created " + FormatTicks(createdTicks) + ".");
            else if (migrated)
                Puts("No stored wipe marker, and save created " + FormatTicks(createdTicks) + " is after the last reconcile " + FormatTicks(previousReconcile) + ". Clearing " + groupName + " for this wipe.");
            else if (recordedThenHooked)
                Puts("OnNewSave '" + onNewSaveFile + "' arrived after the wipe marker was recorded this boot. Clearing " + groupName + ". Wipe id '" + wipeId + "'. Save created " + FormatTicks(createdTicks) + ".");
            else
                Puts("Stored wipe marker changed. Previous '" + previous + "'. Current '" + marker + "' (wipe id '" + wipeId + "', save created " + FormatTicks(createdTicks) + ", seed " + seed + ", size " + mapSize + ").");
        }

        private void CommitDeferredBaseline()
        {
            baselineTimer = null;
            if (!ready || !baselineDeferred || wipeClearPending) return;
            baselineDeferred = false;
            baselinedThisBoot = true;
            RememberMarker(pendingMarker, pendingWipeId, pendingSaveFile, pendingSeed, pendingMapSize, pendingCreatedTicks, pendingSource, true);
            SaveState();
            Puts("Recorded wipe marker '" + state.WipeMarker + "' (wipe id '" + state.WipeId + "', save '" + state.WipeSaveFile + "', seed " + state.WipeSeed + ", size " + state.WipeMapSize + ", save created " + FormatTicks(state.WipeSaveCreatedUtcTicks) + "). No previous marker, and this save is not newer than the last reconcile, so " + groupName + " was not cleared. Run ndu.clear to empty it.");
        }

        private void ArmWipe(string marker, string wipeId, string saveFile, string seed, string mapSize, long createdTicks, string source)
        {
            DateTime created = new DateTime(createdTicks, DateTimeKind.Utc);
            DateTime unlock = NightshadeDefaultUnlockedSchedule.NextUnlockUtc(created, zone, RulesFromSettings());
            bool hold = DateTime.UtcNow < unlock;
            pendingMarker = marker ?? "";
            pendingWipeId = wipeId ?? "";
            pendingSaveFile = saveFile ?? "";
            pendingSeed = seed ?? "";
            pendingMapSize = mapSize ?? "";
            pendingCreatedTicks = createdTicks;
            pendingUnlockTicks = unlock.Ticks;
            pendingSource = source ?? "";
            pendingHold = hold;
            wipeClearPending = true;
            if (state == null) state = new State();
            state.HoldClosedUntilUtcTicks = hold ? unlock.Ticks : 0L;
            SaveState();
        }

        private void CommitArmedWipe(int removed)
        {
            if (!wipeClearPending || state == null) return;
            RememberMarker(pendingMarker, pendingWipeId, pendingSaveFile, pendingSeed, pendingMapSize, pendingCreatedTicks, pendingSource, true);
            state.LastClearRemoved = removed;
            state.LastClearUtcTicks = DateTime.UtcNow.Ticks;
            state.LastClearSource = pendingSource ?? "";
            wipeClearPending = false;
        }

        private void RememberMarker(string marker, string wipeId, string saveFile, string seed, string mapSize, long createdTicks, string source, bool stampDetected)
        {
            if (state == null) state = new State();
            state.WipeMarker = marker ?? "";
            state.WipeId = wipeId ?? "";
            state.WipeSaveFile = saveFile ?? "";
            state.WipeSeed = seed ?? "";
            state.WipeMapSize = mapSize ?? "";
            state.WipeSaveCreatedUtcTicks = createdTicks;
            if (stampDetected) state.WipeDetectedUtcTicks = DateTime.UtcNow.Ticks;
            if (!string.IsNullOrEmpty(source)) state.WipeSource = source;
        }

        private void LaterWipeRead(float seconds, string source, bool hintIfUnchanged)
        {
            if (wipeRetryTimer != null) wipeRetryTimer.Destroy();
            wipeRetryTimer = timer.Once(seconds, delegate
            {
                wipeRetryTimer = null;
                if (!ready) return;
                ApplyWipeMarker(source);
                if (wipeClearPending)
                {
                    wantNewSaveHint = false;
                    Reconcile("wipe");
                }
                else if (hintIfUnchanged && wantNewSaveHint && lastWipeClass == "same" && !newSaveHintLogged)
                {
                    newSaveHintLogged = true;
                    wantNewSaveHint = false;
                    Puts("OnNewSave '" + onNewSaveFile + "' did not change the wipe marker (wipe id '"
                        + (state == null ? "" : state.WipeId) + "', save created "
                        + FormatTicks(state == null ? 0L : state.WipeSaveCreatedUtcTicks) + ", seed "
                        + (state == null ? "" : state.WipeSeed) + ", size "
                        + (state == null ? "" : state.WipeMapSize) + "). " + groupName
                        + " was not cleared. A blueprint-only wipe does not change this marker. Run ndu.clear to empty the group.");
                }
            });
        }

        private bool TryReadLiveWipe(out string marker, out string wipeId, out string saveFile, out string seed, out string mapSize, out long createdTicks)
        {
            marker = "";
            wipeId = "";
            saveFile = "";
            seed = "";
            mapSize = "";
            createdTicks = 0L;
            try
            {
                wipeId = NightshadeDefaultUnlockedSchedule.SanitizeMarkerPart(SaveRestore.WipeId);
                saveFile = NightshadeDefaultUnlockedSchedule.SanitizeMarkerPart(FileNameOnly(World.SaveFileName));
                seed = NightshadeDefaultUnlockedSchedule.NormalizeCount(World.Seed.ToString(CultureInfo.InvariantCulture));
                mapSize = NightshadeDefaultUnlockedSchedule.NormalizeCount(World.Size.ToString(CultureInfo.InvariantCulture));
                DateTime created = SaveRestore.SaveCreatedTime;
                if (created.Kind == DateTimeKind.Local) created = created.ToUniversalTime();
                else if (created.Kind != DateTimeKind.Utc) created = DateTime.SpecifyKind(created, DateTimeKind.Utc);
                createdTicks = created.Ticks;
            }
            catch (Exception ex)
            {
                lastWipeReadError = ex.Message;
                return false;
            }
            if (!NightshadeDefaultUnlockedSchedule.MarkerReady(wipeId, saveFile, mapSize, createdTicks))
            {
                lastWipeReadError = "save identity is not ready yet";
                return false;
            }
            marker = NightshadeDefaultUnlockedSchedule.FormatMarker(wipeId, saveFile, seed, mapSize, createdTicks);
            if (string.IsNullOrEmpty(marker))
            {
                lastWipeReadError = "wipe marker was empty";
                return false;
            }
            liveMarker = marker;
            liveWipeId = wipeId;
            liveSaveFile = saveFile;
            liveSeed = seed;
            liveMapSize = mapSize;
            liveCreatedTicks = createdTicks;
            lastWipeReadError = "";
            return true;
        }

        private static string FileNameOnly(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int slash = path.LastIndexOf('/');
            int back = path.LastIndexOf('\\');
            int cut = slash > back ? slash : back;
            if (cut >= 0 && cut + 1 < path.Length) return path.Substring(cut + 1);
            return path;
        }

        private void NormalizeState()
        {
            if (state == null) state = new State();
            if (state.LastPhase == null) state.LastPhase = "";
            if (state.LastReason == null) state.LastReason = "";
            if (state.WipeMarker == null) state.WipeMarker = "";
            if (state.WipeId == null) state.WipeId = "";
            if (state.WipeSaveFile == null) state.WipeSaveFile = "";
            if (state.WipeSeed == null) state.WipeSeed = "";
            if (state.WipeMapSize == null) state.WipeMapSize = "";
            if (state.WipeSource == null) state.WipeSource = "";
            if (state.LastClearSource == null) state.LastClearSource = "";
        }

        private DateTime EffectiveUntil(NightshadeDefaultUnlockedSchedule.Cycle cycle, bool open)
        {
            DateTime until = cycle.NextBoundaryUtc;
            if (!open && state != null && state.HoldClosedUntilUtcTicks > DateTime.UtcNow.Ticks)
            {
                try
                {
                    DateTime hold = new DateTime(state.HoldClosedUntilUtcTicks, DateTimeKind.Utc);
                    if (hold < until) until = hold;
                }
                catch (ArgumentOutOfRangeException) { }
            }
            return until;
        }

        private int MemberCount()
        {
            if (permission == null || !permission.GroupExists(groupName)) return 0;
            string[] rows = permission.GetUsersInGroup(groupName);
            if (rows == null) return 0;
            return rows.Length;
        }

        private void SaveState()
        {
            if (state == null) return;
            try { Interface.Oxide.DataFileSystem.WriteObject(DataPath, state); }
            catch (Exception ex) { PrintWarning("Could not save cycle state: " + ex.Message); }
        }

        private string StatusText()
        {
            string marker;
            string wipeId;
            string saveFile;
            string seed;
            string mapSize;
            long createdTicks;
            if (!TryReadLiveWipe(out marker, out wipeId, out saveFile, out seed, out mapSize, out createdTicks))
            {
                marker = liveMarker;
                wipeId = liveWipeId;
                saveFile = liveSaveFile;
                seed = liveSeed;
                mapSize = liveMapSize;
                createdTicks = liveCreatedTicks;
            }
            NightshadeDefaultUnlockedSchedule.Cycle cycle = zone == null
                ? null
                : NightshadeDefaultUnlockedSchedule.Evaluate(DateTime.UtcNow, zone, RulesFromSettings());
            int members = MemberCount();
            bool calendarOpen = cycle != null && cycle.Open;
            bool open = cycle != null && Enrolling(cycle);
            string phase = cycle == null ? "unknown" : (open ? "OPEN" : "CLOSED");
            string until = cycle == null ? "unknown" : FormatWall(EffectiveUntil(cycle, open));
            string wipe = cycle == null ? "unknown" : FormatWall(cycle.WipeUtc);
            string unlock = cycle == null ? "unknown" : FormatWall(cycle.OpenUtc);
            bool holdActive = state != null && state.HoldClosedUntilUtcTicks > DateTime.UtcNow.Ticks;
            bool markerMatch = state != null && !string.IsNullOrEmpty(marker) && string.Equals(state.WipeMarker, marker, StringComparison.Ordinal);
            return groupName + " " + phase
                + " | calendar open " + (cycle == null ? "unknown" : (calendarOpen ? "yes" : "no"))
                + " | enabled " + (settings != null && settings.Enabled ? "yes" : "no")
                + " | zone " + (zone == null ? "none" : zone.Id) + (zoneFallback ? " (fallback)" : "")
                + " | configured zone " + (settings == null ? "" : settings.TimeZoneId)
                + " | calendar wipe " + wipe
                + " | calendar unlock " + unlock
                + " | next boundary " + until
                + " | direct members " + members.ToString(CultureInfo.InvariantCulture)
                + " | known players " + KnownIds().Count.ToString(CultureInfo.InvariantCulture)
                + " | inheritance " + inheritanceNote
                + " | server wipe id " + (string.IsNullOrEmpty(wipeId) ? "unknown" : wipeId)
                + " | save file " + (string.IsNullOrEmpty(saveFile) ? "unknown" : saveFile)
                + " | save created " + FormatTicks(createdTicks)
                + " | seed " + (string.IsNullOrEmpty(seed) ? "unknown" : seed)
                + " | size " + (string.IsNullOrEmpty(mapSize) ? "unknown" : mapSize)
                + " | marker match " + (string.IsNullOrEmpty(marker) ? "unknown" : (markerMatch ? "yes" : "no"))
                + " | stored wipe id " + (state == null || string.IsNullOrEmpty(state.WipeId) ? "none" : state.WipeId)
                + " | marker recorded " + (state == null ? "unknown" : FormatTicks(state.WipeDetectedUtcTicks))
                + " | marker source " + (state == null || string.IsNullOrEmpty(state.WipeSource) ? "none" : state.WipeSource)
                + " | last clear removed " + (state == null ? "0" : state.LastClearRemoved.ToString(CultureInfo.InvariantCulture))
                + " at " + (state == null ? "unknown" : FormatTicks(state.LastClearUtcTicks))
                + " source " + (state == null || string.IsNullOrEmpty(state.LastClearSource) ? "none" : state.LastClearSource)
                + " | hold active " + (holdActive ? "yes" : "no")
                + " | hold closed until " + (state == null ? "unknown" : FormatTicks(state.HoldClosedUntilUtcTicks))
                + " | clear pending " + (wipeClearPending ? "yes" : "no");
        }

        private string FormatTicks(long ticks)
        {
            if (ticks <= 0L) return "unknown";
            try { return FormatWall(new DateTime(ticks, DateTimeKind.Utc)); }
            catch (ArgumentOutOfRangeException) { return "unknown"; }
        }

        private string FormatWall(DateTime utc)
        {
            if (zone == null) return utc.ToString("u", CultureInfo.InvariantCulture);
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
            return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " " + zone.Id;
        }

        private bool ConsoleAdmin(ConsoleSystem.Arg arg)
        {
            if (arg == null) return false;
            if (arg.Connection == null) return true;
            BasePlayer player = arg.Connection.player as BasePlayer;
            if (player == null) return false;
            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermAdmin)) return true;
            arg.ReplyWith("No permission.");
            return false;
        }
    }
#endif
}
