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

        // Same daily cap as NightshadeRaidProtection.ScheduleBoundary: wake at the
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

        // Same built-in US Eastern zone NightshadeInfo and NightshadeRaidProtection use
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
    [Info("NightshadeDefaultUnlocked", "Nightshade", "1.0.0")]
    [Description("Removes default_unlocked from wipe through Monday 4 AM Eastern, then enrolls known and future players until the next monthly wipe.")]
    public class NightshadeDefaultUnlocked : RustPlugin
    {
        private const string PermAdmin = "nightshadedefaultunlocked.admin";
        private const string DataPath = "Nightshade/NightshadeDefaultUnlocked/State";
        private const int MaxFollowUps = 3;

        private Settings settings;
        private State state;
        private TimeZoneInfo zone;
        private bool zoneFallback;
        private bool ready;
        private string groupName = "default_unlocked";
        private Timer boundaryTimer;
        private Timer followTimer;
        private int suppress;
        private int followUps;
        private bool followQueued;
        private bool writerWarned;
        private bool missingGroupWarned;
        private string inheritanceNote = "not checked";
        private readonly HashSet<string> applying = new HashSet<string>(StringComparer.Ordinal);

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
            permission.RegisterPermission(PermAdmin, this);
            cmd.AddConsoleCommand("ndu.status", this, nameof(CmdStatus));
            cmd.AddConsoleCommand("ndu.reconcile", this, nameof(CmdReconcile));
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
            inheritanceNote = DescribeInheritance();
            if (inheritanceNote.StartsWith("These groups", StringComparison.Ordinal))
                PrintWarning(inheritanceNote);
            Reconcile("load");
        }

        private void Unload()
        {
            ready = false;
            if (boundaryTimer != null) boundaryTimer.Destroy();
            if (followTimer != null) followTimer.Destroy();
            boundaryTimer = null;
            followTimer = null;
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
            if (CurrentCycle().Open) AddOne(id);
            else RemoveOne(id);
        }

        private void OnUserGroupAdded(string id, string name)
        {
            if (suppress > 0 || !ready || settings == null || !settings.Enabled) return;
            if (!SameGroup(name) || !NightshadeDefaultUnlockedSchedule.IsPlayerId(id)) return;
            if (!CurrentCycle().Open) RemoveOne(id);
        }

        private void OnUserGroupRemoved(string id, string name)
        {
            if (suppress > 0 || !ready || settings == null || !settings.Enabled) return;
            if (!SameGroup(name) || !NightshadeDefaultUnlockedSchedule.IsPlayerId(id)) return;
            if (CurrentCycle().Open) AddOne(id);
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
                Schedule(cycle);
                return;
            }

            if (reason == "load" || reason == "timer" || reason == "command") followUps = 0;

            List<string> known = KnownIds();
            string[] rows = permission.GetUsersInGroup(groupName);
            NightshadeDefaultUnlockedSchedule.MembershipPlan plan = NightshadeDefaultUnlockedSchedule.Build(cycle.Open, known, rows);
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

            string phase = cycle.Open ? "open" : "closed";
            bool changed = state == null || !string.Equals(state.LastPhase, phase, StringComparison.Ordinal) || added > 0 || removed > 0;
            if (state == null) state = new State();
            state.LastPhase = phase;
            state.LastReconcileUtcTicks = DateTime.UtcNow.Ticks;
            state.CycleWipeUtcTicks = cycle.WipeUtc.Ticks;
            state.CycleOpenUtcTicks = cycle.OpenUtc.Ticks;
            state.NextBoundaryUtcTicks = cycle.NextBoundaryUtc.Ticks;
            state.LastAdded = added;
            state.LastRemoved = removed;
            state.LastReason = reason ?? "";
            SaveState();

            if (reason == "load" || reason == "command" || changed)
            {
                Puts(groupName + " is " + (cycle.Open ? "OPEN" : "CLOSED")
                    + " until " + FormatWall(cycle.NextBoundaryUtc)
                    + ". Added " + added.ToString(CultureInfo.InvariantCulture)
                    + ", removed " + removed.ToString(CultureInfo.InvariantCulture)
                    + ". Known " + known.Count.ToString(CultureInfo.InvariantCulture)
                    + ". Zone " + zone.Id + (zoneFallback ? " (fallback)" : "")
                    + ". Reason " + reason + ".");
            }
            Schedule(cycle);
        }

        private void Schedule(NightshadeDefaultUnlockedSchedule.Cycle cycle)
        {
            if (boundaryTimer != null) boundaryTimer.Destroy();
            boundaryTimer = null;
            if (settings == null || !settings.Enabled || cycle == null) return;
            double seconds = NightshadeDefaultUnlockedSchedule.WakeSeconds(DateTime.UtcNow, cycle.NextBoundaryUtc, settings.DailyWakeSeconds);
            boundaryTimer = timer.Once((float)seconds, delegate { Reconcile("timer"); });
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

        private void SaveState()
        {
            if (state == null) return;
            try { Interface.Oxide.DataFileSystem.WriteObject(DataPath, state); }
            catch (Exception ex) { PrintWarning("Could not save cycle state: " + ex.Message); }
        }

        private string StatusText()
        {
            NightshadeDefaultUnlockedSchedule.Cycle cycle = zone == null
                ? null
                : NightshadeDefaultUnlockedSchedule.Evaluate(DateTime.UtcNow, zone, RulesFromSettings());
            int members = 0;
            if (permission != null && permission.GroupExists(groupName))
            {
                string[] rows = permission.GetUsersInGroup(groupName);
                if (rows != null) members = rows.Length;
            }
            string phase = cycle == null ? "unknown" : (cycle.Open ? "OPEN" : "CLOSED");
            string until = cycle == null ? "unknown" : FormatWall(cycle.NextBoundaryUtc);
            string wipe = cycle == null ? "unknown" : FormatWall(cycle.WipeUtc);
            string open = cycle == null ? "unknown" : FormatWall(cycle.OpenUtc);
            return groupName + " " + phase
                + " | enabled " + (settings != null && settings.Enabled ? "yes" : "no")
                + " | zone " + (zone == null ? "none" : zone.Id) + (zoneFallback ? " (fallback)" : "")
                + " | configured zone " + (settings == null ? "" : settings.TimeZoneId)
                + " | wipe " + wipe
                + " | unlock " + open
                + " | next boundary " + until
                + " | direct members " + members.ToString(CultureInfo.InvariantCulture)
                + " | known players " + KnownIds().Count.ToString(CultureInfo.InvariantCulture)
                + " | inheritance " + inheritanceNote;
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
