// NightshadeEvents
// Schedules vanilla and custom Rust events for Nightshade.
// Event definitions, chat wording, and last-run / next-run times are stored in
// data/Nightshade/NightshadeEvents/Store.json. That file is kept across plugin
// reloads, server restarts, and map wipes. This plugin never deletes it.
//
// The panel and "nsevents list" group events into Custom, Vanilla, and Seasonal.
// Built-in events take their group from the event type (no stored category).
// A custom event may set Category to "custom" or "seasonal". A missing Category
// stays in Custom, so older Store.json files load with their schedules unchanged.
//
// Starts are spaced by "Minimum minutes between scheduled event starts".
// Clock-time events keep their clock time. While the server is empty, or below
// an event's min players, a due event is moved forward instead of left overdue.
//
// Vanilla names and prefabs were checked against developer/patched_assemblies/Assembly-CSharp.dll.
// EventsManager (plugins/09_Events/EventsManager.cs, commands /em and em_spawn) is a separate
// plugin. This one uses /nsevents and nightshadeevents.admin and does not edit that file.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using UnityEngine.UI;

namespace Oxide.Plugins
{
    [Info("NightshadeEvents", "Nightshade", "1.1.2")]
    [Description("Schedules vanilla, seasonal, and custom events from a compact admin panel.")]
    public class NightshadeEvents : RustPlugin
    {
        private const string Perm = "nightshadeevents.admin";
        private const string DataFileName = "Store.json";
        private const string UiRoot = "NightshadeEvents";
        private const string UiCmd = "nsevents.ui";
        private const string FontReg = "robotocondensed-regular.ttf";
        private const string FontBold = "robotocondensed-bold.ttf";
        private const string DefaultLogo = "https://raw.githubusercontent.com/nightshadepve/Nightshadeserver/refs/heads/main/260625_Logo_75_nobg.png";
        private const string HeliPrefab = "assets/prefabs/npc/patrol helicopter/patrolhelicopter.prefab";
        private const string CargoPrefab = "assets/content/vehicles/boats/cargoship/cargoshiptest.prefab";
        private const string ChinookPrefab = "assets/Prefabs/NPC/CH47/ch47scientists.entity.prefab";
        private const string PlanePrefab = "assets/prefabs/npc/cargo plane/cargo_plane.prefab";
        private const string SantaPrefab = "assets/prefabs/misc/xmas/sleigh/santasleigh.prefab";
        private const string XmasPrefab = "assets/prefabs/misc/xmas/xmasrefill.prefab";

        private static readonly string[] VanillaOrder =
        {
            "heli", "cargo", "chinook", "airdrop", "bradley", "roadbradley", "vendor",
            "deepsea", "satellite"
        };

        private static readonly string[] SeasonalOrder =
        {
            "egghunt", "halloween", "christmas", "santa"
        };

        private static readonly string[] DayKeys = { "mon", "tue", "wed", "thu", "fri", "sat", "sun" };

        private Configuration config;
        private StoreFile store;
        private bool storeOk = true;
        private TimeZoneInfo zone = TimeZoneInfo.Utc;
        private string zoneLabel = "UTC";
        private Timer ticker;
        private DateTime lastScheduledStartUtc = DateTime.MinValue;
        private readonly Dictionary<string, DateTime> blockedSince = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private const int ClockGraceMinutes = 2;
        private int bindTries;
        private bool bound;
        private bool ownDeepSea;
        private int allowDepth;
        private bool eventsManagerLoaded;
        private readonly Dictionary<string, List<EventSchedule>> boundSchedules = new Dictionary<string, List<EventSchedule>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, string> scheduleOwner = new Dictionary<int, string>();
        private readonly Dictionary<int, HeldSchedule> heldSchedules = new Dictionary<int, HeldSchedule>();
        private readonly HashSet<ulong> openUi = new HashSet<ulong>();
        private readonly Dictionary<ulong, string> editing = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> notice = new Dictionary<ulong, string>();
        private readonly Dictionary<string, float> warnAt = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        private class Configuration
        {
            [JsonProperty("Time zone")] public string TimeZone = "America/New_York";
            [JsonProperty("Disable vanilla automatic events")] public bool DisableVanillaAutomaticEvents = true;
            [JsonProperty("Missed events after downtime (skip or once)")] public string MissedEvents = "skip";
            [JsonProperty("Minutes to wait for min players before skipping a due event")] public int MinPlayerWaitMinutes = 180;
            [JsonProperty("Minimum minutes between scheduled event starts")] public int MinStartGapMinutes = 5;
            [JsonProperty("Schedule check seconds")] public int CheckSeconds = 15;
            [JsonProperty("Theme mode (Shop, Auto, or a theme name)")] public string ThemeMode = "Shop";
            [JsonProperty("Theme source config file")] public string ThemeFile = "NightshadeShop.json";
            [JsonProperty("Logo source config file")] public string LogoFile = "NightshadeInfo.json";
            [JsonProperty("Logo URL fallback")] public string LogoUrl = DefaultLogo;
            [JsonProperty("Window width")] public float WindowWidth = 572f;
            [JsonProperty("Window height")] public float WindowHeight = 448f;
            [JsonProperty("Custom events to add when that id is missing from saved data")] public List<EventDef> CustomSeeds = new List<EventDef>();
        }

        private class StoreFile
        {
            [JsonProperty("Version")] public int Version = 1;
            [JsonProperty("Events")] public List<EventDef> Events = new List<EventDef>();
            [JsonProperty("Next run UTC ticks")] public Dictionary<string, long> NextRunUtcTicks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            [JsonProperty("Last run UTC ticks")] public Dictionary<string, long> LastRunUtcTicks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            [JsonProperty("Saved Bradley enabled")] public bool HasBradleyOriginal;
            [JsonProperty("Bradley enabled before this plugin")] public bool BradleyOriginal = true;
            [JsonProperty("Saved Halloween enabled")] public bool HasHalloweenOriginal;
            [JsonProperty("Halloween enabled before this plugin")] public bool HalloweenOriginal;
            [JsonProperty("Halloween left on by a start")] public bool HalloweenLive;
            [JsonProperty("Saved Christmas enabled")] public bool HasXmasOriginal;
            [JsonProperty("Christmas enabled before this plugin")] public bool XmasOriginal;
            [JsonProperty("Christmas left on by a start")] public bool ChristmasLive;
            [JsonProperty("Saved deep sea timer")] public bool HasDeepSeaTimer;
            [JsonProperty("Deep sea seconds before this plugin held it")] public float DeepSeaTimer;
        }

        private class EventDef
        {
            [JsonProperty("Id")] public string Id;
            [JsonProperty("Name")] public string Name;
            [JsonProperty("Kind")] public string Kind;
            [JsonProperty("Enabled")] public bool Enabled;
            [JsonProperty("Mode (minutes, hours, or clock)")] public string Mode = "hours";
            [JsonProperty("Interval")] public int Interval = 4;
            [JsonProperty("Times (HH:MM, clock mode)")] public List<string> Times = new List<string>();
            [JsonProperty("Days (mon-sun, empty means every day)")] public List<string> Days = new List<string>();
            [JsonProperty("Jitter minutes")] public int JitterMinutes;
            [JsonProperty("Min players")] public int MinPlayers;
            [JsonProperty("Vanilla auto (default, allow, or block)")] public string VanillaAuto = "default";
            [JsonProperty("Announce")] public bool Announce = true;
            [JsonProperty("Announcement")] public string Announcement = "";
            [JsonProperty("Commands")] public List<string> Commands = new List<string>();
            [JsonProperty("Schedule key")] public string ScheduleKey = "";
            [JsonProperty("Custom")] public bool Custom;
            [JsonProperty("Category", NullValueHandling = NullValueHandling.Ignore)] public string Category;
        }

        private class HeldSchedule
        {
            public EventSchedule Schedule;
            public bool WasEnabled;
            public bool TimerPaused;
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
        }

        private class ThemeRow
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

        #region Lifecycle

        private void Init()
        {
            LoadConfig();
            LoadStore();
            if (storeOk)
            {
                NormalizeStore();
                bool changed = MergeBuiltins();
                if (MergeSeeds()) changed = true;
                if (changed) SaveStore();
            }
            permission.RegisterPermission(Perm, this);
            ResolveZone();
        }

        private void OnServerInitialized()
        {
            eventsManagerLoaded = plugins.Find("EventsManager") != null;
            if (eventsManagerLoaded)
                PrintWarning("EventsManager is also loaded. It uses /em and EventsManager.use and can fight this plugin over the same vanilla schedules and the deep sea. Use one suppressor.");
            if (plugins.Find("HeliSignals") != null)
                PrintWarning("HeliSignals is loaded. If its no-vanilla-heli option is on, it kills patrol helicopters spawned by the event schedule, including ones this plugin starts that way.");
            Puts("Data file: data/Nightshade/NightshadeEvents/Store.json. It is kept across reloads, restarts, and map wipes. Missed events: " + MissedMode() + ". Minimum gap between starts: " + GapMinutes() + " minutes.");
            timer.Once(1f, BindWhenReady);
        }

        private void OnServerSave() => SaveStore();

        private void Unload()
        {
            if (ticker != null) ticker.Destroy();
            ticker = null;
            RestoreAll();
            SaveStore();
            foreach (var id in new List<ulong>(openUi))
            {
                var player = BasePlayer.FindByID(id);
                if (player != null) CuiHelper.DestroyUi(player, UiRoot);
            }
            openUi.Clear();
        }

        // Map wipes call this. Event data is intentionally left on disk.
        private void OnNewSave(string filename)
        {
            Puts("Map wipe (" + (filename ?? "unknown") + "). NightshadeEvents schedules, custom events, chat wording, and timers were kept.");
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (player == null) return;
            openUi.Remove(player.userID);
            editing.Remove(player.userID);
            notice.Remove(player.userID);
        }

        #endregion

        #region Config and data

        protected override void LoadDefaultConfig() => config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>() ?? new Configuration();
            }
            catch (Exception ex)
            {
                PrintWarning("Config could not be read. Using defaults. " + ex.Message);
                config = new Configuration();
            }
            if (config.CustomSeeds == null) config.CustomSeeds = new List<EventDef>();
            if (string.IsNullOrEmpty(config.TimeZone)) config.TimeZone = "America/New_York";
            if (string.IsNullOrEmpty(config.MissedEvents)) config.MissedEvents = "skip";
            if (config.CheckSeconds < 5) config.CheckSeconds = 15;
            if (config.MinPlayerWaitMinutes < 1) config.MinPlayerWaitMinutes = 180;
            if (config.MinStartGapMinutes < 0) config.MinStartGapMinutes = 5;
            if (config.MinStartGapMinutes > 180) config.MinStartGapMinutes = 180;
            if (config.WindowWidth < 480f) config.WindowWidth = 572f;
            if (config.WindowHeight < 360f) config.WindowHeight = 448f;
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        private string StorePath()
        {
            return Path.Combine(Interface.Oxide.DataDirectory, "Nightshade", "NightshadeEvents", DataFileName);
        }

        private void LoadStore()
        {
            store = new StoreFile();
            string path = StorePath();
            if (!File.Exists(path))
            {
                storeOk = true;
                return;
            }
            try
            {
                var read = JsonConvert.DeserializeObject<StoreFile>(File.ReadAllText(path));
                if (read == null) throw new Exception("File was empty.");
                store = read;
                storeOk = true;
            }
            catch (Exception ex)
            {
                storeOk = false;
                store = new StoreFile();
                PrintError("Could not read " + path + ". It was NOT replaced. Fix the file and reload. " + ex.Message);
            }
        }

        private void SaveStore()
        {
            if (!storeOk || store == null) return;
            try
            {
                string path = StorePath();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string json = JsonConvert.SerializeObject(store, Formatting.Indented);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                }
                catch
                {
                    File.Copy(tmp, path, true);
                    File.Delete(tmp);
                }
            }
            catch (Exception ex)
            {
                PrintWarning("Could not save NightshadeEvents data. " + ex.Message);
            }
        }

        private void NormalizeStore()
        {
            if (store.Events == null) store.Events = new List<EventDef>();
            if (store.NextRunUtcTicks == null) store.NextRunUtcTicks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (store.LastRunUtcTicks == null) store.LastRunUtcTicks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            for (int i = store.Events.Count - 1; i >= 0; i--)
            {
                var ev = store.Events[i];
                if (ev == null || string.IsNullOrEmpty(ev.Id))
                {
                    store.Events.RemoveAt(i);
                    continue;
                }
                if (ev.Times == null) ev.Times = new List<string>();
                if (ev.Days == null) ev.Days = new List<string>();
                if (ev.Commands == null) ev.Commands = new List<string>();
                if (string.IsNullOrEmpty(ev.Mode)) ev.Mode = "hours";
                if (string.IsNullOrEmpty(ev.VanillaAuto)) ev.VanillaAuto = "default";
                if (string.IsNullOrEmpty(ev.Kind)) ev.Kind = ev.Custom ? "custom" : ev.Id;
                if (ev.Interval < 1) ev.Interval = 1;
                if (ev.JitterMinutes < 0) ev.JitterMinutes = 0;
                if (ev.MinPlayers < 0) ev.MinPlayers = 0;
                if (ev.Announcement == null) ev.Announcement = "";
                if (ev.Name == null) ev.Name = ev.Id;
                if (ev.ScheduleKey == null) ev.ScheduleKey = "";
                if (IsCustomEvent(ev)) ev.Category = CustomCategory(ev.Category);
            }
        }

        private bool MergeBuiltins()
        {
            bool changed = false;
            var defs = BuiltinDefs();
            for (int i = 0; i < defs.Count; i++)
            {
                if (FindEvent(defs[i].Id) != null) continue;
                store.Events.Add(defs[i]);
                changed = true;
            }
            return changed;
        }

        private bool MergeSeeds()
        {
            bool changed = false;
            if (config.CustomSeeds == null) return false;
            for (int i = 0; i < config.CustomSeeds.Count; i++)
            {
                var seed = config.CustomSeeds[i];
                if (seed == null || string.IsNullOrEmpty(seed.Id)) continue;
                if (FindEvent(seed.Id) != null) continue;
                store.Events.Add(CopySeed(seed));
                changed = true;
            }
            return changed;
        }

        private static EventDef CopySeed(EventDef seed)
        {
            return new EventDef
            {
                Id = seed.Id,
                Name = string.IsNullOrEmpty(seed.Name) ? seed.Id : seed.Name,
                Kind = "custom",
                Custom = true,
                Enabled = seed.Enabled,
                Mode = string.IsNullOrEmpty(seed.Mode) ? "hours" : seed.Mode,
                Interval = seed.Interval < 1 ? 4 : seed.Interval,
                Times = seed.Times == null ? new List<string>() : new List<string>(seed.Times),
                Days = seed.Days == null ? new List<string>() : new List<string>(seed.Days),
                JitterMinutes = seed.JitterMinutes,
                MinPlayers = seed.MinPlayers,
                VanillaAuto = "allow",
                Announce = seed.Announce,
                Announcement = seed.Announcement ?? "",
                Commands = seed.Commands == null ? new List<string>() : new List<string>(seed.Commands),
                Category = CustomCategory(seed.Category)
            };
        }

        private static List<EventDef> BuiltinDefs()
        {
            return new List<EventDef>
            {
                Def("heli", "Patrol Helicopter", true, "<color=#E2B968>Patrol helicopter</color> is in the air. Grid {grid}."),
                Def("cargo", "Cargo Ship", true, "<color=#E2B968>Cargo ship</color> is on the water. Grid {grid}."),
                Def("chinook", "Chinook", true, "<color=#E2B968>Chinook</color> is inbound with a crate. Grid {grid}."),
                Def("airdrop", "Airdrop", true, "<color=#E2B968>Airdrop</color> is inbound. Grid {grid}."),
                Def("bradley", "Bradley", true, "<color=#E2B968>Bradley</color> is out at Launch Site."),
                Def("roadbradley", "Road Bradley", true, "<color=#E2B968>Road Bradley</color> is on the roads. Grid {grid}."),
                Def("vendor", "Travelling Vendor", false, "<color=#E2B968>Travelling vendor</color> is on the road. Grid {grid}."),
                Def("deepsea", "Deep Sea", false, "<color=#7FB7E8>The deep sea</color> is open."),
                Def("egghunt", "Egg Hunt", false, "<color=#E7C1D8>The egg hunt</color> has started."),
                Def("halloween", "Halloween", true, "<color=#E07A3D>Halloween</color> is active."),
                Def("christmas", "Christmas", true, "<color=#F2EAF7>Christmas</color> gifts are dropping."),
                Def("santa", "Santa Sleigh", true, "<color=#F2EAF7>Santa</color> is flying over. Grid {grid}."),
                Def("satellite", "Satellite Crash", false, "<color=#C9D4E8>A satellite</color> is coming down. Grid {grid}.")
            };
        }

        private static EventDef Def(string id, string name, bool announce, string text)
        {
            return new EventDef
            {
                Id = id,
                Name = name,
                Kind = id,
                Enabled = false,
                Mode = "hours",
                Interval = 4,
                Announce = announce,
                Announcement = text,
                VanillaAuto = "default",
                Custom = false
            };
        }

        private EventDef FindEvent(string id)
        {
            if (string.IsNullOrEmpty(id) || store == null || store.Events == null) return null;
            for (int i = 0; i < store.Events.Count; i++)
            {
                var ev = store.Events[i];
                if (ev != null && string.Equals(ev.Id, id, StringComparison.OrdinalIgnoreCase)) return ev;
            }
            return null;
        }

        private EventDef FindEventToken(string token)
        {
            if (string.IsNullOrEmpty(token) || store == null || store.Events == null) return null;
            var byId = FindEvent(token);
            if (byId != null) return byId;
            string needle = token.Trim();
            EventDef exact = null;
            int exactCount = 0;
            EventDef partial = null;
            int partialCount = 0;
            for (int i = 0; i < store.Events.Count; i++)
            {
                var ev = store.Events[i];
                if (ev == null || string.IsNullOrEmpty(ev.Name)) continue;
                if (string.Equals(ev.Name, needle, StringComparison.OrdinalIgnoreCase))
                {
                    exact = ev;
                    exactCount++;
                    continue;
                }
                if (ev.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    partial = ev;
                    partialCount++;
                }
            }
            if (exactCount == 1) return exact;
            if (exactCount > 1) return null;
            if (partialCount == 1) return partial;
            return null;
        }

        private static string JoinArgs(string[] args, int from)
        {
            if (args == null || from >= args.Length) return "";
            string text = args[from] ?? "";
            for (int i = from + 1; i < args.Length; i++)
            {
                text += " ";
                text += args[i] ?? "";
            }
            return text.Trim();
        }

        private void MarkScheduledStart()
        {
            lastScheduledStartUtc = DateTime.UtcNow;
        }

        #endregion

        #region Time

        private void ResolveZone()
        {
            string wanted = string.IsNullOrEmpty(config.TimeZone) ? "America/New_York" : config.TimeZone.Trim();
            TimeZoneInfo found = TryZone(wanted);
            if (found == null && !wanted.Equals("America/New_York", StringComparison.OrdinalIgnoreCase))
                found = TryZone("America/New_York");
            if (found == null) found = TryZone("Eastern Standard Time");
            if (found == null)
            {
                zone = TimeZoneInfo.Utc;
                zoneLabel = "UTC";
                PrintWarning("Time zone '" + wanted + "' was not found. Using UTC.");
                return;
            }
            zone = found;
            zoneLabel = wanted;
        }

        private static TimeZoneInfo TryZone(string id)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { return null; }
        }

        private string MissedMode()
        {
            return config != null && string.Equals(config.MissedEvents, "once", StringComparison.OrdinalIgnoreCase) ? "once" : "skip";
        }

        private DateTime ToLocal(DateTime utc)
        {
            try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone); }
            catch { return utc; }
        }

        private DateTime ToUtc(DateTime local)
        {
            var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            try { return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone); }
            catch
            {
                try { return TimeZoneInfo.ConvertTimeToUtc(unspecified.AddHours(1), zone); }
                catch { return DateTime.UtcNow.AddHours(1); }
            }
        }

        private void ResumeAfterLoad()
        {
            if (!storeOk || store == null || store.Events == null) return;
            bool once = MissedMode() == "once";
            var now = DateTime.UtcNow;
            int gap = GapMinutes();
            bool changed = false;
            var catchup = new List<EventDef>();
            for (int i = 0; i < store.Events.Count; i++)
            {
                var ev = store.Events[i];
                if (ev == null || !ev.Enabled) continue;
                DateTime due;
                if (!TryDue(ev, out due))
                {
                    var fresh = ArmNext(ev, now, !IsClock(ev));
                    if (fresh.HasValue)
                    {
                        SetNext(ev, fresh);
                        changed = true;
                    }
                    continue;
                }
                if (due > now) continue;
                if (IsClock(ev))
                {
                    // A missed clock slot is the next clock time, not a catch-up start.
                    SetNext(ev, NextClock(ev, now, false));
                    changed = true;
                    continue;
                }
                if (!once)
                {
                    var future = ComputeFutureAfterMiss(ev, due, now);
                    if (future.HasValue)
                    {
                        SetNext(ev, future);
                        changed = true;
                    }
                    continue;
                }
                catchup.Add(ev);
            }
            if (catchup.Count > 0)
            {
                QueueFrom(catchup, now, gap);
                changed = true;
            }
            if (SeparateUpcoming(now, gap)) changed = true;
            SeedLastStart();
            if (changed) SaveStore();
        }

        private int GapMinutes()
        {
            int gap = config == null ? 5 : config.MinStartGapMinutes;
            if (gap < 0) return 5;
            if (gap > 180) return 180;
            return gap;
        }

        private bool TryDue(EventDef ev, out DateTime when)
        {
            when = DateTime.MinValue;
            if (ev == null || store == null || store.NextRunUtcTicks == null) return false;
            long ticks;
            if (!store.NextRunUtcTicks.TryGetValue(ev.Id, out ticks) || ticks <= 0) return false;
            when = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }

        private void SeedLastStart()
        {
            lastScheduledStartUtc = DateTime.MinValue;
            if (store == null || store.LastRunUtcTicks == null) return;
            long best = 0;
            foreach (var pair in store.LastRunUtcTicks)
                if (pair.Value > best) best = pair.Value;
            if (best > 0) lastScheduledStartUtc = new DateTime(best, DateTimeKind.Utc);
        }

        private void QueueFrom(List<EventDef> events, DateTime now, int gap)
        {
            int step = gap > 0 ? gap : 1;
            DateTime cursor = now;
            var clocks = ClockTimesAfter(now);
            for (int i = 0; i < events.Count; i++)
            {
                DateTime slot = PlaceAfter(cursor.AddMinutes(step), cursor, true, clocks, step);
                if (slot <= now) slot = now.AddMinutes(step * (i + 1));
                SetNext(events[i], slot);
                cursor = slot;
            }
        }

        private bool SeparateUpcoming(DateTime now, int gap)
        {
            if (gap <= 0 || store == null || store.Events == null) return false;
            var clocks = new List<DateTime>();
            var intervals = new List<EventDef>();
            for (int i = 0; i < store.Events.Count; i++)
            {
                var ev = store.Events[i];
                if (ev == null || !ev.Enabled) continue;
                DateTime when;
                if (!TryDue(ev, out when) || when <= now) continue;
                if (IsClock(ev)) clocks.Add(when);
                else intervals.Add(ev);
            }
            clocks.Sort();
            intervals.Sort(delegate (EventDef a, EventDef b)
            {
                long ta = 0;
                long tb = 0;
                store.NextRunUtcTicks.TryGetValue(a.Id, out ta);
                store.NextRunUtcTicks.TryGetValue(b.Id, out tb);
                if (ta < tb) return -1;
                if (ta > tb) return 1;
                return string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
            });
            bool changed = false;
            DateTime cursor = DateTime.MinValue;
            bool hasCursor = false;
            for (int i = 0; i < intervals.Count; i++)
            {
                var ev = intervals[i];
                DateTime when;
                if (!TryDue(ev, out when)) continue;
                DateTime placed = PlaceAfter(when, cursor, hasCursor, clocks, gap);
                if (placed != when)
                {
                    SetNext(ev, placed);
                    changed = true;
                }
                cursor = placed;
                hasCursor = true;
            }
            return changed;
        }

        private List<DateTime> ClockTimesAfter(DateTime now)
        {
            var clocks = new List<DateTime>();
            if (store == null || store.Events == null) return clocks;
            for (int i = 0; i < store.Events.Count; i++)
            {
                var ev = store.Events[i];
                if (ev == null || !ev.Enabled || !IsClock(ev)) continue;
                DateTime when;
                if (TryDue(ev, out when) && when > now) clocks.Add(when);
            }
            clocks.Sort();
            return clocks;
        }

        // Clock times stay put. Interval times move to at least `gap` minutes after
        // the previous interval start and clear of every clock time.
        private static DateTime PlaceAfter(DateTime when, DateTime cursor, bool hasCursor, List<DateTime> clocks, int gap)
        {
            if (gap <= 0) return when;
            for (int guard = 0; guard < 48; guard++)
            {
                DateTime next = when;
                if (hasCursor)
                {
                    DateTime earliest = cursor.AddMinutes(gap);
                    if (next < earliest) next = earliest;
                }
                if (clocks != null)
                {
                    for (int i = 0; i < clocks.Count; i++)
                    {
                        double delta = (next - clocks[i]).TotalMinutes;
                        if (delta < gap && delta > -gap)
                        {
                            DateTime after = clocks[i].AddMinutes(gap);
                            if (after > next) next = after;
                        }
                    }
                }
                if (next == when) return when;
                when = next;
            }
            return when;
        }

        private DateTime NextOpenSlot(DateTime now, DateTime parkFrom, bool hasPark, int gap)
        {
            int step = gap > 0 ? gap : 1;
            // Chain onto other interval times that are already parked soon. Leave a
            // multi-hour natural slot alone so a deferral does not jump the queue.
            if (store != null && store.Events != null)
            {
                int intervals = 0;
                for (int i = 0; i < store.Events.Count; i++)
                {
                    var ev = store.Events[i];
                    if (ev != null && ev.Enabled && !IsClock(ev)) intervals++;
                }
                double limit = step * (intervals < 1 ? 1 : intervals);
                for (int i = 0; i < store.Events.Count; i++)
                {
                    var ev = store.Events[i];
                    if (ev == null || !ev.Enabled || IsClock(ev)) continue;
                    DateTime when;
                    if (!TryDue(ev, out when) || when <= now) continue;
                    if ((when - now).TotalMinutes > limit) continue;
                    if (!hasPark || when > parkFrom)
                    {
                        parkFrom = when;
                        hasPark = true;
                    }
                }
            }
            List<DateTime> clocks = gap > 0 ? ClockTimesAfter(now) : null;
            DateTime seed = hasPark ? parkFrom.AddMinutes(step) : now.AddMinutes(step);
            DateTime slot = PlaceAfter(seed, parkFrom, hasPark, clocks, step);
            if (slot <= now)
            {
                slot = PlaceAfter(now.AddMinutes(step), now, true, clocks, step);
                if (slot <= now) slot = now.AddMinutes(step);
            }
            return slot;
        }

        private static bool PopulationBlocked(EventDef ev, int players)
        {
            if (players <= 0) return true;
            return ev != null && ev.MinPlayers > 0 && players < ev.MinPlayers;
        }

        private DateTime? ComputeFutureAfterMiss(EventDef ev, DateTime missedDue, DateTime now)
        {
            if (IsClock(ev)) return NextClock(ev, now, false);
            var cursor = missedDue;
            int guard = 0;
            int step = StepMinutes(ev);
            while (cursor <= now && guard++ < 20000) cursor = cursor.AddMinutes(step);
            return SkipDays(ev, cursor);
        }

        private DateTime? NextClock(EventDef ev, DateTime afterUtc, bool jitter)
        {
            var times = ParseTimes(ev);
            if (times.Count == 0) return null;
            int extra = jitter ? RollJitter(ev) : 0;
            var localAfter = ToLocal(afterUtc);
            for (int d = 0; d < 21; d++)
            {
                var day = localAfter.Date.AddDays(d);
                if (!DayOk(ev, day.DayOfWeek)) continue;
                for (int i = 0; i < times.Count; i++)
                {
                    var cand = day.AddMinutes(times[i] + extra);
                    var utc = ToUtc(cand);
                    if (utc > afterUtc) return utc;
                }
            }
            return null;
        }

        private DateTime SkipDays(EventDef ev, DateTime utc)
        {
            var local = ToLocal(utc);
            int guard = 0;
            while (!DayOk(ev, local.DayOfWeek) && guard++ < 14) local = local.AddDays(1);
            return ToUtc(local);
        }

        private DateTime? ArmNext(EventDef ev, DateTime afterUtc, bool jitter)
        {
            DateTime? next = IsClock(ev) ? NextClock(ev, afterUtc, jitter) : SkipDays(ev, afterUtc.AddMinutes(StepMinutes(ev) + (jitter ? RollJitter(ev) : 0)));
            return next;
        }

        private static bool IsClock(EventDef ev)
        {
            return ev != null && string.Equals(ev.Mode, "clock", StringComparison.OrdinalIgnoreCase);
        }

        private static int StepMinutes(EventDef ev)
        {
            int n = ev.Interval < 1 ? 1 : ev.Interval;
            if (string.Equals(ev.Mode, "minutes", StringComparison.OrdinalIgnoreCase)) return n;
            return n * 60;
        }

        private static int RollJitter(EventDef ev)
        {
            if (ev == null || ev.JitterMinutes <= 0) return 0;
            return UnityEngine.Random.Range(0, ev.JitterMinutes + 1);
        }

        private static bool DayOk(EventDef ev, DayOfWeek day)
        {
            if (ev.Days == null || ev.Days.Count == 0) return true;
            string key = DayKey(day);
            for (int i = 0; i < ev.Days.Count; i++)
                if (string.Equals(ev.Days[i], key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string DayKey(DayOfWeek day)
        {
            switch (day)
            {
                case DayOfWeek.Monday: return "mon";
                case DayOfWeek.Tuesday: return "tue";
                case DayOfWeek.Wednesday: return "wed";
                case DayOfWeek.Thursday: return "thu";
                case DayOfWeek.Friday: return "fri";
                case DayOfWeek.Saturday: return "sat";
                default: return "sun";
            }
        }

        private static List<int> ParseTimes(EventDef ev)
        {
            var list = new List<int>();
            if (ev.Times == null) return list;
            for (int i = 0; i < ev.Times.Count; i++)
            {
                int mins;
                if (TryTime(ev.Times[i], out mins)) list.Add(mins);
            }
            list.Sort();
            return list;
        }

        private static bool TryTime(string text, out int minutes)
        {
            minutes = 0;
            if (string.IsNullOrEmpty(text)) return false;
            var parts = text.Trim().Split(':');
            if (parts.Length != 2) return false;
            int h, m;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out h)) return false;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m)) return false;
            if (h < 0 || h > 23 || m < 0 || m > 59) return false;
            minutes = h * 60 + m;
            return true;
        }

        private void SetNext(EventDef ev, DateTime? when)
        {
            if (ev == null) return;
            if (!when.HasValue) store.NextRunUtcTicks.Remove(ev.Id);
            else store.NextRunUtcTicks[ev.Id] = when.Value.Ticks;
        }

        private void Retime(EventDef ev, bool jitter)
        {
            if (ev == null) return;
            SetNext(ev, ArmNext(ev, DateTime.UtcNow, jitter));
        }

        #endregion

        #region Schedules and suppression

        private void BindWhenReady()
        {
            int count = 0;
            var all = EventSchedule.allEvents;
            if (all != null)
                foreach (var schedule in all) if (schedule != null) count++;
            if (count == 0 && bindTries++ < 12)
            {
                timer.Once(5f, BindWhenReady);
                return;
            }
            if (bound) return;
            bound = true;
            BindSchedules();
            ResumeAfterLoad();
            ApplySuppression();
            if (ticker != null) ticker.Destroy();
            ticker = timer.Every(Mathf.Clamp(config.CheckSeconds, 5, 120), Tick);
        }

        private void BindSchedules()
        {
            boundSchedules.Clear();
            scheduleOwner.Clear();
            var all = EventSchedule.allEvents;
            if (all == null) return;
            bool changed = false;
            foreach (var schedule in all)
            {
                if (schedule == null) continue;
                string id = Classify(schedule);
                if (id == null)
                {
                    id = EnsureDiscovered(schedule, ref changed);
                    if (id == null) continue;
                }
                List<EventSchedule> list;
                if (!boundSchedules.TryGetValue(id, out list))
                {
                    list = new List<EventSchedule>();
                    boundSchedules[id] = list;
                }
                list.Add(schedule);
                scheduleOwner[schedule.GetInstanceID()] = id;
                var ev = FindEvent(id);
                if (ev != null && string.IsNullOrEmpty(ev.ScheduleKey))
                {
                    ev.ScheduleKey = ScheduleName(schedule);
                    changed = true;
                }
            }
            if (changed) SaveStore();
        }

        private string EnsureDiscovered(EventSchedule schedule, ref bool changed)
        {
            string key = ScheduleName(schedule);
            if (string.IsNullOrEmpty(key)) return null;
            for (int i = 0; i < store.Events.Count; i++)
            {
                var ev = store.Events[i];
                if (ev != null && string.Equals(ev.ScheduleKey, key, StringComparison.OrdinalIgnoreCase) && ev.Kind == "schedule")
                    return ev.Id;
            }
            string id = "sched_" + Slug(key);
            string unique = id;
            int n = 2;
            while (FindEvent(unique) != null) unique = id + "_" + n++;
            store.Events.Add(new EventDef
            {
                Id = unique,
                Name = PrettySchedule(key),
                Kind = "schedule",
                ScheduleKey = key,
                Enabled = false,
                Mode = "hours",
                Interval = 4,
                Announce = true,
                Announcement = "<color=#E2B968>{event}</color> has started. Grid {grid}.",
                VanillaAuto = "default"
            });
            changed = true;
            return unique;
        }

        private static string Classify(EventSchedule schedule)
        {
            var parts = schedule.GetComponents<TriggeredEvent>();
            if (parts != null)
            {
                for (int i = 0; i < parts.Length; i++)
                {
                    var part = parts[i];
                    if (part == null) continue;
                    if (part is TravellingVendorEvent) return "vendor";
                    if (part is RoadBradleys) return "roadbradley";
                    string type = part.GetType().Name;
                    if (type.IndexOf("Egg", StringComparison.OrdinalIgnoreCase) >= 0 || type.IndexOf("Easter", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "egghunt";
                    var prefab = part as TriggeredEventPrefab;
                    string path = PrefabPath(prefab).ToLowerInvariant();
                    if (path.Length == 0) continue;
                    if (path.Contains("patrolhelicopter") || path.Contains("patrol helicopter")) return "heli";
                    if (path.Contains("cargo_plane") || path.Contains("cargo plane")) return "airdrop";
                    if (path.Contains("cargoship") || path.Contains("cargo ship")) return "cargo";
                    if (path.Contains("ch47")) return "chinook";
                    if (path.Contains("santasleigh")) return "santa";
                    if (path.Contains("xmasrefill")) return "christmas";
                    if (path.Contains("satellite")) return "satellite";
                    if (path.Contains("bradley")) return "roadbradley";
                }
            }
            string name = ScheduleName(schedule);
            if (name.IndexOf("easter", StringComparison.OrdinalIgnoreCase) >= 0) return "egghunt";
            return null;
        }

        private static string PrefabPath(TriggeredEventPrefab prefab)
        {
            if (prefab == null || prefab.targetPrefab == null) return "";
            return prefab.targetPrefab.resourcePath ?? "";
        }

        private static string ScheduleName(EventSchedule schedule)
        {
            try
            {
                string name = schedule.GetName();
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { }
            return schedule.gameObject != null ? schedule.gameObject.name : "";
        }

        private static string PrettySchedule(string key)
        {
            string name = key.StartsWith("event_", StringComparison.OrdinalIgnoreCase) ? key.Substring(6) : key;
            name = name.Replace('_', ' ').Trim();
            if (name.Length == 0) return key;
            return char.ToUpperInvariant(name[0]) + (name.Length > 1 ? name.Substring(1) : "");
        }

        private static string Slug(string text)
        {
            var chars = new List<char>();
            string lower = (text ?? "").ToLowerInvariant();
            for (int i = 0; i < lower.Length && chars.Count < 24; i++)
            {
                char c = lower[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                if (ok) chars.Add(c);
                else if (chars.Count > 0 && chars[chars.Count - 1] != '_') chars.Add('_');
            }
            while (chars.Count > 0 && chars[chars.Count - 1] == '_') chars.RemoveAt(chars.Count - 1);
            return chars.Count == 0 ? "event" : new string(chars.ToArray());
        }

        private bool IsSuppressed(EventDef ev)
        {
            if (ev == null || ev.Custom || ev.Kind == "custom") return false;
            string mode = ev.VanillaAuto ?? "default";
            if (mode.Equals("allow", StringComparison.OrdinalIgnoreCase)) return false;
            if (mode.Equals("block", StringComparison.OrdinalIgnoreCase)) return true;
            return config.DisableVanillaAutomaticEvents;
        }

        private bool IsSuppressed(string id) => IsSuppressed(FindEvent(id));

        // The airfield Call Chinook terminal looks this schedule up in
        // EventSchedule.enabledEvents and then calls Trigger(). Disabling the
        // behaviour removes it from that list, so the terminal never spawns the
        // locked crate. Leave it enabled and stop only the repeating timer.
        private static bool KeepsTrigger(string id)
        {
            return id != null && id.Equals("chinook", StringComparison.OrdinalIgnoreCase);
        }

        private static void PauseScheduleTimer(EventSchedule schedule)
        {
            if (schedule == null) return;
            if (!schedule.enabled) schedule.enabled = true;
            schedule.CancelInvoke(schedule.RunSchedule);
        }

        private static void ResumeScheduleTimer(EventSchedule schedule)
        {
            if (schedule == null || !schedule.enabled) return;
            schedule.CancelInvoke(schedule.RunSchedule);
            schedule.InvokeRepeating(schedule.RunSchedule, 1f, 1f);
        }

        private void HoldChinookTimer()
        {
            List<EventSchedule> list;
            if (!boundSchedules.TryGetValue("chinook", out list) || list == null) return;
            for (int i = 0; i < list.Count; i++)
                PauseScheduleTimer(list[i]);
        }

        private void ApplySuppression()
        {
            if (!storeOk) return;
            foreach (var pair in boundSchedules)
            {
                bool block = IsSuppressed(pair.Key);
                var list = pair.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    var schedule = list[i];
                    if (schedule == null) continue;
                    int sid = schedule.GetInstanceID();
                    HeldSchedule held;
                    if (block)
                    {
                        if (!heldSchedules.TryGetValue(sid, out held))
                        {
                            held = new HeldSchedule { Schedule = schedule, WasEnabled = schedule.enabled };
                            heldSchedules[sid] = held;
                        }
                        if (KeepsTrigger(pair.Key))
                        {
                            PauseScheduleTimer(schedule);
                            held.TimerPaused = true;
                        }
                        else if (schedule.enabled) schedule.enabled = false;
                    }
                    else if (heldSchedules.TryGetValue(sid, out held))
                    {
                        if (held.TimerPaused && held.WasEnabled) ResumeScheduleTimer(schedule);
                        schedule.enabled = held.WasEnabled;
                        heldSchedules.Remove(sid);
                    }
                }
            }
            ApplyBradley();
            ApplyHalloween();
            ApplyChristmas();
            if (IsSuppressed("deepsea")) HoldDeepSea();
            else ReleaseDeepSea(true);
        }

        private void ApplyBradley()
        {
            if (IsSuppressed("bradley"))
            {
                if (!store.HasBradleyOriginal)
                {
                    store.BradleyOriginal = ConVar.Bradley.enabled;
                    store.HasBradleyOriginal = true;
                    SaveStore();
                }
                ConVar.Bradley.enabled = false;
            }
            else if (store.HasBradleyOriginal)
            {
                ConVar.Bradley.enabled = store.BradleyOriginal;
                store.HasBradleyOriginal = false;
                SaveStore();
            }
        }

        private void ApplyHalloween()
        {
            if (IsSuppressed("halloween"))
            {
                if (!store.HasHalloweenOriginal)
                {
                    store.HalloweenOriginal = ConVar.Halloween.enabled;
                    store.HasHalloweenOriginal = true;
                    SaveStore();
                }
                if (!store.HalloweenLive) ConVar.Halloween.enabled = false;
            }
            else if (store.HasHalloweenOriginal)
            {
                ConVar.Halloween.enabled = store.HalloweenOriginal;
                store.HasHalloweenOriginal = false;
                SaveStore();
            }
        }

        private void ApplyChristmas()
        {
            if (IsSuppressed("christmas"))
            {
                if (!store.HasXmasOriginal)
                {
                    store.XmasOriginal = ConVar.XMas.enabled;
                    store.HasXmasOriginal = true;
                    SaveStore();
                }
                if (!store.ChristmasLive) ConVar.XMas.enabled = false;
            }
            else if (store.HasXmasOriginal)
            {
                ConVar.XMas.enabled = store.XmasOriginal;
                store.HasXmasOriginal = false;
                SaveStore();
            }
        }

        private void HoldDeepSea()
        {
            var manager = DeepSeaManager.Get(true);
            if (manager == null) return;
            if (!store.HasDeepSeaTimer)
            {
                store.DeepSeaTimer = manager.TimeToNextOpening;
                store.HasDeepSeaTimer = true;
                SaveStore();
            }
            if (manager.TimeToNextOpening < 600f) manager.TimeToNextOpening = 600f;
        }

        private void ReleaseDeepSea(bool clearSaved)
        {
            if (!store.HasDeepSeaTimer) return;
            var manager = DeepSeaManager.Get(true);
            if (manager != null) manager.TimeToNextOpening = store.DeepSeaTimer;
            if (!clearSaved) return;
            store.HasDeepSeaTimer = false;
            SaveStore();
        }

        private void RestoreAll()
        {
            foreach (var pair in heldSchedules)
            {
                var schedule = pair.Value.Schedule;
                if (schedule == null) continue;
                if (pair.Value.TimerPaused && pair.Value.WasEnabled) ResumeScheduleTimer(schedule);
                schedule.enabled = pair.Value.WasEnabled;
            }
            heldSchedules.Clear();
            if (store == null) return;
            if (store.HasBradleyOriginal) ConVar.Bradley.enabled = store.BradleyOriginal;
            if (store.HasHalloweenOriginal) ConVar.Halloween.enabled = store.HalloweenOriginal;
            if (store.HasXmasOriginal) ConVar.XMas.enabled = store.XmasOriginal;
            ReleaseDeepSea(false);
        }

        private object OnEventTrigger(TriggeredEventPrefab prefab)
        {
            if (allowDepth > 0 || prefab == null) return null;
            var schedule = prefab.GetComponent<EventSchedule>();
            if (schedule == null) return null;
            string id;
            if (!scheduleOwner.TryGetValue(schedule.GetInstanceID(), out id)) return null;
            if (IsSuppressed(id) && !KeepsTrigger(id)) return true;
            return null;
        }

        private object OnHuntEventStart(EggHuntEvent hunt)
        {
            if (allowDepth > 0) return null;
            if (IsSuppressed("egghunt")) return true;
            return null;
        }

        private object OnDeepSeaOpen(DeepSeaManager manager)
        {
            if (ownDeepSea || allowDepth > 0) return null;
            if (IsSuppressed("deepsea")) return true;
            return null;
        }

        private void OnDeepSeaOpened(DeepSeaManager manager) => ownDeepSea = false;

        #endregion

        #region Tick and spawn

        private void Tick()
        {
            if (!storeOk || store.Events == null) return;
            if (IsSuppressed("chinook")) HoldChinookTimer();
            if (IsSuppressed("deepsea")) HoldDeepSea();
            ApplyHalloween();
            ApplyChristmas();
            if (IsSuppressed("bradley")) ConVar.Bradley.enabled = false;
            var now = DateTime.UtcNow;
            int players = BasePlayer.activePlayerList == null ? 0 : BasePlayer.activePlayerList.Count;
            int gap = GapMinutes();
            int freshSeconds = (config.CheckSeconds < 5 ? 15 : config.CheckSeconds) * 2;
            bool changed = false;
            bool firedInterval = false;
            DateTime parkFrom = lastScheduledStartUtc;
            bool hasPark = parkFrom != DateTime.MinValue;
            // Clock events first, so an on-time arena is not pushed behind an interval event.
            for (int pass = 0; pass < 2; pass++)
            {
                bool clocksPass = pass == 0;
                for (int i = 0; i < store.Events.Count; i++)
                {
                    var ev = store.Events[i];
                    if (ev == null || !ev.Enabled) continue;
                    if (IsClock(ev) != clocksPass) continue;
                    DateTime due;
                    if (!TryDue(ev, out due))
                    {
                        var armed = ArmNext(ev, now, !IsClock(ev));
                        if (!armed.HasValue) continue;
                        SetNext(ev, armed);
                        changed = true;
                        continue;
                    }
                    if (due > now)
                    {
                        if (!PopulationBlocked(ev, players)) blockedSince.Remove(ev.Id);
                        continue;
                    }
                    if (PopulationBlocked(ev, players))
                    {
                        if (DeferBlocked(ev, due, now, gap, ref parkFrom, ref hasPark)) changed = true;
                        continue;
                    }
                    blockedSince.Remove(ev.Id);
                    if (IsClock(ev))
                    {
                        if ((now - due).TotalMinutes > ClockGraceMinutes)
                        {
                            SetNext(ev, NextClock(ev, now, false));
                            changed = true;
                            WarnOnce(ev.Id, ev.Name + " missed its clock time and was moved to the next one.");
                            continue;
                        }
                        bool startedClock = StartScheduled(ev, now);
                        changed = true;
                        if (!startedClock) continue;
                        parkFrom = lastScheduledStartUtc;
                        hasPark = true;
                        continue;
                    }
                    bool backlog = (now - due).TotalSeconds > freshSeconds;
                    bool gapBlocked = gap > 0 && lastScheduledStartUtc != DateTime.MinValue && now < lastScheduledStartUtc.AddMinutes(gap);
                    if (backlog || gapBlocked || firedInterval)
                    {
                        DateTime slot = NextOpenSlot(now, parkFrom, hasPark, gap);
                        SetNext(ev, slot);
                        parkFrom = slot;
                        hasPark = true;
                        changed = true;
                        continue;
                    }
                    bool started = StartScheduled(ev, now);
                    changed = true;
                    if (!started) continue;
                    firedInterval = true;
                    parkFrom = lastScheduledStartUtc;
                    hasPark = true;
                }
            }
            if (changed) SaveStore();
        }

        // Returns true when the saved next-run time changed.
        private bool DeferBlocked(EventDef ev, DateTime due, DateTime now, int gap, ref DateTime parkFrom, ref bool hasPark)
        {
            if (IsClock(ev))
            {
                if ((now - due).TotalMinutes <= ClockGraceMinutes) return false;
                SetNext(ev, NextClock(ev, now, false));
                WarnOnce(ev.Id, ev.Name + " missed its clock time while the server was blocked and was moved to the next one.");
                return true;
            }
            DateTime since;
            if (!blockedSince.TryGetValue(ev.Id, out since))
            {
                since = now;
                blockedSince[ev.Id] = since;
            }
            int wait = config != null && config.MinPlayerWaitMinutes > 0 ? config.MinPlayerWaitMinutes : 180;
            if (now > since.AddMinutes(wait))
            {
                SetNext(ev, ArmNext(ev, now, false));
                blockedSince.Remove(ev.Id);
                WarnOnce(ev.Id, ev.Name + " skipped this slot. Not enough players online.");
                return true;
            }
            DateTime slot = NextOpenSlot(now, parkFrom, hasPark, gap);
            SetNext(ev, slot);
            parkFrom = slot;
            hasPark = true;
            return true;
        }

        private bool StartScheduled(EventDef ev, DateTime now)
        {
            string grid;
            string error = RunFire(ev, out grid);
            store.LastRunUtcTicks[ev.Id] = now.Ticks;
            SetNext(ev, ArmNext(ev, now, !IsClock(ev)));
            if (error != null)
            {
                WarnOnce(ev.Id, ev.Name + " did not start. " + error);
                return false;
            }
            lastScheduledStartUtc = now;
            Puts("Started " + ev.Name + (string.IsNullOrEmpty(grid) ? "." : " at " + grid + "."));
            Say(ev, grid);
            return true;
        }

        private void WarnOnce(string id, string message)
        {
            float now = Time.realtimeSinceStartup;
            float last;
            if (warnAt.TryGetValue(id, out last) && now - last < 600f) return;
            warnAt[id] = now;
            PrintWarning(message);
        }

        private string RunFire(EventDef ev, out string grid)
        {
            grid = "";
            try { return Fire(ev, out grid); }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private string Fire(EventDef ev, out string grid)
        {
            grid = "";
            if (ev == null) return "Unknown event.";
            if (ev.Custom || ev.Kind == "custom") return FireCommands(ev);
            if (ev.Kind == "schedule") return FireBound(ev, out grid);
            switch (ev.Kind)
            {
                case "heli": return FirePrefabEvent(ev, HeliPrefab, false, out grid);
                case "cargo": return FirePrefabEvent(ev, CargoPrefab, true, out grid);
                case "chinook": return FirePrefabEvent(ev, ChinookPrefab, true, out grid);
                case "airdrop": return FirePrefabEvent(ev, PlanePrefab, false, out grid);
                case "santa": return FirePrefabEvent(ev, SantaPrefab, false, out grid);
                case "bradley": return FireBradley(out grid);
                case "roadbradley": return FireBound(ev, out grid);
                case "vendor": return FireVendor(ev, out grid);
                case "deepsea": return FireDeepSea();
                case "egghunt": return FireEgg(ev);
                case "halloween": return FireHalloween();
                case "christmas": return FireChristmas(ev, out grid);
                case "satellite": return FireSatellite(ev, out grid);
                default: return FireBound(ev, out grid);
            }
        }

        private string FireCommands(EventDef ev)
        {
            if (ev.Commands == null || ev.Commands.Count == 0) return "That custom event has no commands.";
            bool any = false;
            for (int i = 0; i < ev.Commands.Count; i++)
            {
                string cmd = ev.Commands[i] == null ? "" : ev.Commands[i].Trim();
                if (cmd.Length == 0) continue;
                any = true;
                string error = RunServerCommand(cmd);
                if (error != null) return error;
            }
            if (!any) return "That custom event has no commands.";
            return null;
        }

        private static string RunServerCommand(string commandLine)
        {
            try
            {
                ConsoleSystem.CommandResult result = ConsoleSystem.RunWithResult(ConsoleSystem.Option.Server, commandLine);
                if (result.Result == ConsoleSystem.CommandResultType.CommandNotFound)
                    return "Command '" + CommandName(commandLine) + "' was not found.";
                if (result.Result == ConsoleSystem.CommandResultType.PermissionDenied)
                    return "Command '" + CommandName(commandLine) + "' was denied.";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            return null;
        }

        private static string CommandName(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return "";
            string text = commandLine.Trim();
            int space = text.IndexOf(' ');
            return space < 0 ? text : text.Substring(0, space);
        }

        private string FirePrefabEvent(EventDef ev, string prefab, bool triggeredMessage, out string grid)
        {
            grid = "";
            var schedule = FirstSchedule(ev.Id);
            BaseEntity entity;
            if (schedule != null) entity = TriggerSchedule(schedule, ev.Announce);
            else entity = SpawnPrefab(prefab, triggeredMessage);
            if (entity == null) return "Could not spawn " + ev.Name + ".";
            grid = GridOf(entity);
            return null;
        }

        private string FireBradley(out string grid)
        {
            grid = "";
            var spawner = BradleySpawner.singleton;
            if (spawner == null) return "This map has no Launch Site Bradley spawner.";
            bool restoreOff = IsSuppressed("bradley");
            bool previous = ConVar.Bradley.enabled;
            ConVar.Bradley.enabled = true;
            try
            {
                if (spawner.spawned != null)
                {
                    var old = spawner.spawned;
                    if (!old.IsDestroyed) old.Kill();
                    if (spawner.spawned == old) spawner.spawned = null;
                }
                spawner.SpawnBradley();
            }
            finally
            {
                ConVar.Bradley.enabled = restoreOff ? false : previous;
            }
            if (spawner.spawned == null) return "Bradley did not spawn.";
            grid = GridOf(spawner.spawned);
            return null;
        }

        private string FireVendor(EventDef ev, out string grid)
        {
            grid = "";
            if (TravellingVendorEvent.currentVendor != null) return "A travelling vendor is already out.";
            var path = TerrainMeta.Path;
            if (path == null || CountOf(path.Roads) == 0) return "There is no road for the travelling vendor.";
            if (!TravellingVendor.should_spawn) return "travellingvendor.should_spawn is off, so the vendor will not spawn.";
            if (RoadBradleys.StaticBradleyCount > 0) return "A road Bradley is out, so the vendor will not spawn.";
            if (CountOf(path.MainRoads) == 0) return "There is no main road for the travelling vendor.";
            var schedule = FirstSchedule(ev.Id);
            if (!ev.Announce && schedule != null)
            {
                var vendorEvent = schedule.GetComponent<TravellingVendorEvent>();
                if (vendorEvent != null)
                {
                    allowDepth++;
                    try { vendorEvent.RunEvent(); }
                    finally { allowDepth--; }
                    grid = GridOf(TravellingVendorEvent.currentVendor);
                    return TravellingVendorEvent.currentVendor == null ? "Travelling vendor did not spawn." : null;
                }
            }
            var vendor = TravellingVendor.SpawnTravellingVendorForEvent();
            if (vendor == null) return "Travelling vendor did not spawn.";
            grid = GridOf(vendor);
            return null;
        }

        private string FireDeepSea()
        {
            var manager = DeepSeaManager.Get(true);
            if (manager == null) return "Deep sea is not on this server.";
            if (manager.IsBusy()) return "Deep sea is busy opening or closing.";
            if (manager.IsOpen()) return "Deep sea is already open.";
            ownDeepSea = true;
            allowDepth++;
            try { manager.OpenDeepSea(); }
            finally { allowDepth--; }
            timer.Once(30f, () => ownDeepSea = false);
            return null;
        }

        private string FireEgg(EventDef ev)
        {
            var schedule = FirstSchedule(ev.Id);
            if (schedule != null)
            {
                TriggerSchedule(schedule, ev.Announce);
                return null;
            }
            var hunt = EggHuntEvent.serverEvent;
            if (hunt == null) return "Egg hunt is not on this server.";
            allowDepth++;
            try { hunt.StartEvent(); }
            finally { allowDepth--; }
            return null;
        }

        private string FireHalloween()
        {
            if (!store.HasHalloweenOriginal)
            {
                store.HalloweenOriginal = ConVar.Halloween.enabled;
                store.HasHalloweenOriginal = true;
            }
            ConVar.Halloween.enabled = true;
            store.HalloweenLive = true;
            return null;
        }

        private string FireChristmas(EventDef ev, out string grid)
        {
            grid = "";
            if (!store.HasXmasOriginal)
            {
                store.XmasOriginal = ConVar.XMas.enabled;
                store.HasXmasOriginal = true;
            }
            ConVar.XMas.enabled = true;
            store.ChristmasLive = true;
            var schedule = FirstSchedule(ev.Id);
            BaseEntity entity = schedule != null ? TriggerSchedule(schedule, ev.Announce) : SpawnPrefab(XmasPrefab, false);
            grid = GridOf(entity);
            return null;
        }

        private string FireSatellite(EventDef ev, out string grid)
        {
            grid = "";
            var schedule = FirstSchedule(ev.Id);
            if (schedule != null)
            {
                var entity = TriggerSchedule(schedule, ev.Announce);
                grid = GridOf(entity);
                return entity == null ? "Satellite schedule did not spawn." : null;
            }
            allowDepth++;
            try { ConsoleSystem.Run(ConsoleSystem.Option.Server, "satellite.random"); }
            finally { allowDepth--; }
            return null;
        }

        private string FireBound(EventDef ev, out string grid)
        {
            grid = "";
            var schedule = FirstSchedule(ev.Id);
            if (schedule == null) return ev.Name + " has no loaded schedule on this server.";
            var entity = TriggerSchedule(schedule, ev.Announce);
            grid = GridOf(entity);
            return null;
        }

        private EventSchedule FirstSchedule(string id)
        {
            List<EventSchedule> list;
            if (id == null || !boundSchedules.TryGetValue(id, out list) || list == null) return null;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null) return list[i];
            return null;
        }

        private BaseEntity TriggerSchedule(EventSchedule schedule, bool silenceBroadcast)
        {
            var prefabs = schedule.GetComponents<TriggeredEventPrefab>();
            bool[] previous = null;
            if (silenceBroadcast && prefabs != null)
            {
                previous = new bool[prefabs.Length];
                for (int i = 0; i < prefabs.Length; i++)
                {
                    if (prefabs[i] == null) continue;
                    previous[i] = prefabs[i].shouldBroadcastSpawn;
                    prefabs[i].shouldBroadcastSpawn = false;
                }
            }
            allowDepth++;
            try { schedule.Trigger(); }
            finally
            {
                allowDepth--;
                if (previous != null)
                {
                    for (int i = 0; i < prefabs.Length; i++)
                        if (prefabs[i] != null) prefabs[i].shouldBroadcastSpawn = previous[i];
                }
            }
            if (prefabs == null) return null;
            for (int i = 0; i < prefabs.Length; i++)
            {
                if (prefabs[i] != null && prefabs[i].spawnedEntity != null) return prefabs[i].spawnedEntity;
            }
            return null;
        }

        private static BaseEntity SpawnPrefab(string prefab, bool triggeredMessage)
        {
            var entity = GameManager.server.CreateEntity(prefab, Vector3.zero, Quaternion.identity, true);
            if (entity == null) return null;
            if (triggeredMessage) entity.SendMessage("TriggeredEventSpawn", SendMessageOptions.DontRequireReceiver);
            entity.Spawn();
            if (triggeredMessage) entity.SendMessage("TriggeredEventPostSpawn", SendMessageOptions.DontRequireReceiver);
            return entity;
        }

        private static string GridOf(BaseEntity entity)
        {
            if (entity == null) return "";
            try { return MapHelper.PositionToString(entity.transform.position) ?? ""; }
            catch { return ""; }
        }

        private static int CountOf(object collection)
        {
            var list = collection as ICollection;
            return list == null ? 0 : list.Count;
        }

        private void Say(EventDef ev, string grid)
        {
            if (ev == null || !ev.Announce) return;
            string text = ev.Announcement ?? "";
            if (text.Trim().Length == 0) return;
            if (string.IsNullOrEmpty(grid)) grid = "the map";
            string when = ToLocal(DateTime.UtcNow).ToString("HH:mm", CultureInfo.InvariantCulture);
            text = text.Replace("{event}", ev.Name ?? ev.Id ?? "")
                .Replace("{id}", ev.Id ?? "")
                .Replace("{grid}", grid)
                .Replace("{time}", when);
            PrintToChat(text);
        }

        private bool IsLive(EventDef ev)
        {
            if (ev == null) return false;
            switch (ev.Kind)
            {
                case "heli": return Any<PatrolHelicopter>();
                case "cargo": return Any<CargoShip>();
                case "chinook": return Any<CH47HelicopterAIController>();
                case "airdrop": return Any<CargoPlane>();
                case "santa": return Any<SantaSleigh>();
                case "bradley":
                    var spawner = BradleySpawner.singleton;
                    return spawner != null && spawner.spawned != null && !spawner.spawned.IsDestroyed && spawner.spawned.IsAlive();
                case "vendor": return TravellingVendorEvent.currentVendor != null;
                case "deepsea":
                    var manager = DeepSeaManager.Get(true);
                    return manager != null && manager.IsOpen();
                case "egghunt":
                    var hunt = EggHuntEvent.serverEvent;
                    return hunt != null && hunt.IsEventActive();
                case "halloween": return ConVar.Halloween.enabled;
                case "christmas": return ConVar.XMas.enabled;
                default: return false;
            }
        }

        private static bool Any<T>() where T : BaseNetworkable
        {
            var list = BaseNetworkable.serverEntities;
            if (list == null) return false;
            foreach (var entity in list)
            {
                var hit = entity as T;
                if (hit != null && !hit.IsDestroyed) return true;
            }
            return false;
        }

        #endregion

        #region Commands

        [ChatCommand("nsevents")]
        private void CmdChat(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!Allowed(player))
            {
                player.ChatMessage("You need the nightshadeevents.admin permission.");
                return;
            }
            if (args != null && args.Length > 0)
            {
                RunVerb(player, null, args);
                return;
            }
            editing.Remove(player.userID);
            OpenUi(player);
        }

        [ConsoleCommand("nsevents")]
        private void CmdConsole(ConsoleSystem.Arg arg)
        {
            var player = arg == null ? null : arg.Player();
            if (player != null && !Allowed(player))
            {
                SendReply(arg, "You need the nightshadeevents.admin permission.");
                return;
            }
            var args = ReadArgs(arg);
            if (args.Count == 0)
            {
                Reply(player, arg, "nsevents list | nsevents next | nsevents start <id or name>. Chat /nsevents opens the panel.");
                return;
            }
            RunVerb(player, arg, args.ToArray());
        }

        [ConsoleCommand(UiCmd)]
        private void CmdUi(ConsoleSystem.Arg arg)
        {
            var player = arg == null ? null : arg.Player();
            if (player == null || !Allowed(player)) return;
            UiAction(player, ReadArgs(arg));
        }

        private void RunVerb(BasePlayer player, ConsoleSystem.Arg arg, string[] args)
        {
            string verb = args.Length == 0 ? "" : (args[0] ?? "").ToLowerInvariant();
            if (verb == "list")
            {
                Reply(player, arg, ListText());
                return;
            }
            if (verb == "next")
            {
                Reply(player, arg, NextText());
                return;
            }
            if (verb == "start" || verb == "now")
            {
                if (args.Length < 2)
                {
                    Reply(player, arg, "Usage: nsevents start <id or name>");
                    return;
                }
                var ev = FindEventToken(JoinArgs(args, 1));
                if (ev == null)
                {
                    Reply(player, arg, "No event with that id or name.");
                    return;
                }
                string grid;
                string error = RunFire(ev, out grid);
                if (error != null)
                {
                    Reply(player, arg, error);
                    return;
                }
                if (storeOk)
                {
                    store.LastRunUtcTicks[ev.Id] = DateTime.UtcNow.Ticks;
                    BumpIfSoon(ev);
                    SaveStore();
                }
                MarkScheduledStart();
                Say(ev, grid);
                Reply(player, arg, "Started " + ev.Name + ".");
                return;
            }
            Reply(player, arg, "nsevents list | nsevents next | nsevents start <id or name>");
        }

        private void BumpIfSoon(EventDef ev)
        {
            if (ev == null || !ev.Enabled) return;
            long ticks;
            if (!store.NextRunUtcTicks.TryGetValue(ev.Id, out ticks) || ticks <= 0) return;
            var due = new DateTime(ticks, DateTimeKind.Utc);
            if (due <= DateTime.UtcNow.AddMinutes(2)) SetNext(ev, ArmNext(ev, DateTime.UtcNow, true));
        }

        private string ListText()
        {
            var lines = new List<string>();
            var events = Ordered();
            AppendListSection(lines, events, "Custom", "custom", false);
            AppendListSection(lines, events, "Vanilla", "vanilla", true);
            AppendListSection(lines, events, "Seasonal", "seasonal", true);
            return lines.Count == 0 ? "No events saved." : string.Join("\n", lines.ToArray());
        }

        private void AppendListSection(List<string> lines, List<EventDef> events, string title, string category, bool gap)
        {
            if (gap && lines.Count > 0) lines.Add("");
            lines.Add("[" + title + "]");
            int count = 0;
            for (int i = 0; i < events.Count; i++)
            {
                var ev = events[i];
                if (ev == null || !CategoryOf(ev).Equals(category, StringComparison.OrdinalIgnoreCase)) continue;
                string row = ev.Id + " | " + ev.Name + " | " + (ev.Enabled ? "on" : "off") + " | " + NextLabel(ev);
                if (category.Equals("seasonal", StringComparison.OrdinalIgnoreCase) && IsCustomEvent(ev)) row += " | custom";
                lines.Add(row);
                count++;
            }
            if (count == 0) lines.Add("(none)");
        }

        private string NextText()
        {
            var rows = new List<KeyValuePair<long, string>>();
            for (int i = 0; i < store.Events.Count; i++)
            {
                var ev = store.Events[i];
                if (ev == null || !ev.Enabled) continue;
                long ticks;
                if (!store.NextRunUtcTicks.TryGetValue(ev.Id, out ticks) || ticks <= 0) continue;
                rows.Add(new KeyValuePair<long, string>(ticks, ev.Name + "  " + NextLabel(ev)));
            }
            rows.Sort((a, b) => a.Key.CompareTo(b.Key));
            if (rows.Count == 0) return "No enabled events have a next run.";
            int n = Math.Min(8, rows.Count);
            var lines = new string[n];
            for (int i = 0; i < n; i++) lines[i] = rows[i].Value;
            return string.Join("\n", lines);
        }

        private string NextLabel(EventDef ev)
        {
            if (ev == null) return "";
            if (IsLive(ev)) return "live";
            if (!ev.Enabled) return "schedule off";
            long ticks;
            if (!store.NextRunUtcTicks.TryGetValue(ev.Id, out ticks) || ticks <= 0)
                return IsClock(ev) ? "set a time" : "no time";
            var due = new DateTime(ticks, DateTimeKind.Utc);
            var now = DateTime.UtcNow;
            if (due <= now)
            {
                int players = BasePlayer.activePlayerList == null ? 0 : BasePlayer.activePlayerList.Count;
                if (ev.MinPlayers > 0 && players < ev.MinPlayers) return "needs players";
                return "due";
            }
            return ToLocal(due).ToString("ddd HH:mm", CultureInfo.InvariantCulture);
        }

        private static List<string> ReadArgs(ConsoleSystem.Arg arg)
        {
            var list = new List<string>();
            if (arg == null) return list;
            for (int i = 0; i < 48 && arg.HasArgs(i + 1); i++) list.Add(arg.GetString(i) ?? "");
            return list;
        }

        private void Reply(BasePlayer player, ConsoleSystem.Arg arg, string text)
        {
            if (player == null)
            {
                if (arg != null) SendReply(arg, text ?? "");
                return;
            }
            player.ChatMessage(text ?? "");
        }

        private bool Allowed(BasePlayer player)
        {
            return player != null && permission.UserHasPermission(player.UserIDString, Perm);
        }

        #endregion

        #region UI actions

        private void UiAction(BasePlayer player, List<string> args)
        {
            if (args.Count == 0) return;
            string verb = (args[0] ?? "").ToLowerInvariant();
            if (verb == "close")
            {
                CloseUi(player);
                return;
            }
            if (!storeOk)
            {
                notice[player.userID] = "Data file could not be read. It was not overwritten.";
                OpenUi(player);
                return;
            }
            if (verb == "back")
            {
                editing.Remove(player.userID);
                OpenUi(player);
                return;
            }
            if (verb == "vanilla")
            {
                config.DisableVanillaAutomaticEvents = !config.DisableVanillaAutomaticEvents;
                SaveConfig();
                ApplySuppression();
                notice[player.userID] = config.DisableVanillaAutomaticEvents ? "Vanilla auto events are blocked." : "Vanilla auto events are left to the game.";
                OpenUi(player);
                return;
            }
            if (verb == "missed")
            {
                config.MissedEvents = MissedMode() == "once" ? "skip" : "once";
                SaveConfig();
                notice[player.userID] = "After downtime, missed events will " + MissedMode() + ". Applies on the next reload.";
                OpenUi(player);
                return;
            }
            if (verb == "add")
            {
                var ev = NewCustom();
                store.Events.Add(ev);
                editing[player.userID] = ev.Id;
                SaveStore();
                OpenUi(player);
                return;
            }
            if (args.Count < 2)
            {
                OpenUi(player);
                return;
            }
            var target = FindEvent(args[1]);
            if (target == null)
            {
                notice[player.userID] = "That event is gone.";
                editing.Remove(player.userID);
                OpenUi(player);
                return;
            }
            bool retime = false;
            switch (verb)
            {
                case "edit":
                    editing[player.userID] = target.Id;
                    break;
                case "toggle":
                    target.Enabled = !target.Enabled;
                    if (!target.Enabled) ClearSeason(target);
                    else
                    {
                        long existing;
                        bool keep = store.NextRunUtcTicks.TryGetValue(target.Id, out existing) && existing > DateTime.UtcNow.Ticks;
                        retime = !keep;
                    }
                    ApplySuppression();
                    break;
                case "start":
                    string grid;
                    string error = RunFire(target, out grid);
                    if (error != null) notice[player.userID] = error;
                    else
                    {
                        store.LastRunUtcTicks[target.Id] = DateTime.UtcNow.Ticks;
                        BumpIfSoon(target);
                        MarkScheduledStart();
                        Say(target, grid);
                        notice[player.userID] = "Started " + target.Name + ".";
                    }
                    SaveStore();
                    break;
                case "mode":
                    if (args.Count > 2) target.Mode = args[2];
                    retime = true;
                    break;
                case "day":
                    if (args.Count > 2) ToggleDay(target, args[2]);
                    retime = true;
                    break;
                case "alldays":
                    target.Days.Clear();
                    retime = true;
                    break;
                case "auto":
                    target.VanillaAuto = NextAuto(target.VanillaAuto);
                    ApplySuppression();
                    break;
                case "announce":
                    target.Announce = !target.Announce;
                    break;
                case "delete":
                    if (target.Custom)
                    {
                        store.Events.Remove(target);
                        store.NextRunUtcTicks.Remove(target.Id);
                        store.LastRunUtcTicks.Remove(target.Id);
                        editing.Remove(player.userID);
                    }
                    break;
                case "interval":
                    target.Interval = ParseInt(Rest(args, 2), target.Interval, 1, 10080);
                    retime = true;
                    break;
                case "jitter":
                    target.JitterMinutes = ParseInt(Rest(args, 2), target.JitterMinutes, 0, 720);
                    retime = true;
                    break;
                case "players":
                    target.MinPlayers = ParseInt(Rest(args, 2), target.MinPlayers, 0, 500);
                    break;
                case "name":
                    if (target.Custom)
                    {
                        string name = Rest(args, 2).Trim();
                        if (name.Length > 0) target.Name = name.Length > 32 ? name.Substring(0, 32) : name;
                    }
                    break;
                case "times":
                    target.Times = SplitTimes(Rest(args, 2));
                    retime = true;
                    break;
                case "text":
                    target.Announcement = Rest(args, 2);
                    if (target.Announcement.Length > 220) target.Announcement = target.Announcement.Substring(0, 220);
                    break;
                case "commands":
                    if (target.Custom) target.Commands = SplitCommands(Rest(args, 2));
                    break;
                case "group":
                    if (IsCustomEvent(target) && args.Count > 2)
                        target.Category = CustomCategory(args[2]);
                    break;
            }
            if (retime) Retime(target, true);
            if (verb != "edit") SaveStore();
            OpenUi(player);
        }

        private void ClearSeason(EventDef ev)
        {
            if (ev.Kind == "halloween")
            {
                store.HalloweenLive = false;
                if (IsSuppressed(ev)) ConVar.Halloween.enabled = false;
            }
            if (ev.Kind == "christmas")
            {
                store.ChristmasLive = false;
                if (IsSuppressed(ev)) ConVar.XMas.enabled = false;
            }
        }

        private EventDef NewCustom()
        {
            string id = "custom_event";
            int n = 2;
            while (FindEvent(id) != null) id = "custom_event_" + n++;
            return new EventDef
            {
                Id = id,
                Name = "Custom event",
                Kind = "custom",
                Custom = true,
                Enabled = false,
                Mode = "hours",
                Interval = 4,
                Announce = true,
                Announcement = "<color=#E2B968>{event}</color> is starting.",
                VanillaAuto = "allow",
                Category = "custom"
            };
        }

        private static void ToggleDay(EventDef ev, string key)
        {
            if (ev.Days == null) ev.Days = new List<string>();
            bool every = ev.Days.Count == 0;
            if (every)
            {
                for (int i = 0; i < DayKeys.Length; i++)
                    if (!DayKeys[i].Equals(key, StringComparison.OrdinalIgnoreCase)) ev.Days.Add(DayKeys[i]);
                return;
            }
            bool found = false;
            for (int i = ev.Days.Count - 1; i >= 0; i--)
            {
                if (!ev.Days[i].Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                ev.Days.RemoveAt(i);
                found = true;
            }
            if (!found) ev.Days.Add(key);
            if (ev.Days.Count >= 7) ev.Days.Clear();
        }

        private static string NextAuto(string mode)
        {
            if (string.Equals(mode, "block", StringComparison.OrdinalIgnoreCase)) return "allow";
            if (string.Equals(mode, "allow", StringComparison.OrdinalIgnoreCase)) return "default";
            return "block";
        }

        private static int ParseInt(string text, int fallback, int min, int max)
        {
            int value;
            if (!int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return fallback;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static string Rest(List<string> args, int start)
        {
            if (args == null || start >= args.Count) return "";
            var parts = new List<string>();
            for (int i = start; i < args.Count; i++) parts.Add(args[i] ?? "");
            return string.Join(" ", parts.ToArray());
        }

        private static List<string> SplitTimes(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            var bits = text.Replace(';', ',').Replace(' ', ',').Split(',');
            for (int i = 0; i < bits.Length; i++)
            {
                int mins;
                if (!TryTime(bits[i], out mins)) continue;
                list.Add((mins / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (mins % 60).ToString("00", CultureInfo.InvariantCulture));
            }
            return list;
        }

        private static List<string> SplitCommands(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            var bits = text.Split('|');
            for (int i = 0; i < bits.Length; i++)
            {
                string cmd = bits[i].Trim();
                if (cmd.Length > 0) list.Add(cmd.Length > 180 ? cmd.Substring(0, 180) : cmd);
            }
            return list;
        }

        #endregion

        #region UI

        private void OpenUi(BasePlayer player)
        {
            if (player == null || !Allowed(player)) return;
            openUi.Add(player.userID);
            string editId;
            editing.TryGetValue(player.userID, out editId);
            var palette = LoadPalette();
            CuiHelper.DestroyUi(player, UiRoot);
            var c = new CuiElementContainer();
            c.Add(new CuiElement
            {
                Name = UiRoot,
                Parent = "Overlay",
                DestroyUi = UiRoot,
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
                Name = frame,
                Parent = UiRoot,
                Components =
                {
                    new CuiImageComponent { Color = Hex(palette.Background) },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin = Px(-w / 2f, -h / 2f),
                        OffsetMax = Px(w / 2f, h / 2f)
                    }
                }
            });
            string header = Box(c, frame, palette.Header, "0 0", "0 0", 0, h - 40f, w, h);
            Box(c, frame, palette.Accent, "0 0", "0 0", 0, h - 42f, w, h - 40f);
            c.Add(new CuiElement
            {
                Name = CuiHelper.GetGuid(),
                Parent = header,
                Components =
                {
                    new CuiRawImageComponent { Url = Logo(), Color = "1 1 1 1", BlocksRaycast = false },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "10 8", OffsetMax = "34 32" }
                }
            });
            Label(c, header, "Events", 15, palette.Text, TextAnchor.MiddleLeft, 40, 16, 150, 36, true);
            Label(c, header, palette.Tagline + "  " + zoneLabel, 10, palette.SubText, TextAnchor.MiddleLeft, 40, 2, w - 80f, 16, false);
            string note;
            notice.TryGetValue(player.userID, out note);
            if (!string.IsNullOrEmpty(note))
                Label(c, header, note, 10, palette.AccentText, TextAnchor.MiddleRight, 160, 4, w - 44f, 34, false);
            Button(c, frame, UiCmd + " close", palette.Danger, w - 34f, h - 33f, w - 8f, h - 7f, "X", 12, palette.DangerText);
            float subY = h - 70f;
            string sub = Box(c, frame, palette.Panel, "0 0", "0 0", 8, subY, w - 8f, h - 46f);
            if (string.IsNullOrEmpty(editId))
            {
                Button(c, sub, UiCmd + " vanilla", config.DisableVanillaAutomaticEvents ? palette.Accent : palette.Button, 6, 4, 132, 20, config.DisableVanillaAutomaticEvents ? "Vanilla blocked" : "Vanilla allowed", 10, config.DisableVanillaAutomaticEvents ? palette.AccentText : palette.ButtonText);
                Button(c, sub, UiCmd + " missed", palette.Button, 138, 4, 250, 20, "Downtime " + MissedMode(), 10, palette.ButtonText);
                float subW = w - 16f;
                Button(c, sub, UiCmd + " add", palette.Button, subW - 110f, 4, subW - 8f, 20, "Add custom", 10, palette.ButtonText);
            }
            else
            {
                var editingEvent = FindEvent(editId);
                Button(c, sub, UiCmd + " back", palette.Button, 6, 4, 64, 20, "Back", 10, palette.ButtonText);
                Label(c, sub, editingEvent == null ? "Missing" : editingEvent.Name + "  (" + editingEvent.Id + ")", 12, palette.Text, TextAnchor.MiddleLeft, 72, 2, w - 24f, 22, true);
            }
            float scrollTop = subY - 6f;
            var events = Ordered();
            float content = !string.IsNullOrEmpty(editId) ? EditHeight(FindEvent(editId)) : (storeOk ? ListContentHeight(events) : 40f);
            if (content < 40f) content = 40f;
            string scroll = frame + ".Scroll";
            c.Add(new CuiElement
            {
                Name = scroll,
                Parent = frame,
                Components =
                {
                    new CuiImageComponent { Color = Hex(palette.Panel), BlocksRaycast = true },
                    new CuiNeedsCursorComponent(),
                    new CuiScrollViewComponent
                    {
                        Vertical = true,
                        Horizontal = false,
                        MovementType = ScrollRect.MovementType.Clamped,
                        Elasticity = 0.08f,
                        Inertia = true,
                        DecelerationRate = 0.12f,
                        ScrollSensitivity = 24f,
                        ContentTransform = new CuiRectTransform
                        {
                            AnchorMin = "0 1",
                            AnchorMax = "1 1",
                            OffsetMin = "0 -" + content.ToString("0", CultureInfo.InvariantCulture),
                            OffsetMax = "0 0",
                            Pivot = "0.5 1"
                        },
                        VerticalScrollbar = new CuiScrollbar { Size = 3, AutoHide = true, HandleColor = Hex(palette.Accent), TrackColor = Hex(palette.Panel) }
                    },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "8 8", OffsetMax = Px(w - 8f, scrollTop) }
                }
            });
            float inner = w - 28f;
            if (string.IsNullOrEmpty(editId)) DrawList(c, scroll, palette, events, inner);
            else DrawEdit(c, scroll, palette, FindEvent(editId), inner);
            CuiHelper.AddUi(player, c);
        }

        private void CloseUi(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, UiRoot);
            openUi.Remove(player.userID);
            editing.Remove(player.userID);
            notice.Remove(player.userID);
        }

        private void DrawList(CuiElementContainer c, string parent, Palette palette, List<EventDef> events, float width)
        {
            if (!storeOk)
            {
                LabelTop(c, parent, "The data file could not be read, so nothing was overwritten.", 12, palette.DangerText, 8, 8, width - 8, 28, false);
                return;
            }
            float y = 4f;
            y = DrawSection(c, parent, palette, events, width, y, "CUSTOM", "custom");
            y = DrawSection(c, parent, palette, events, width, y, "VANILLA", "vanilla");
            DrawSection(c, parent, palette, events, width, y, "SEASONAL", "seasonal");
        }

        private float DrawSection(CuiElementContainer c, string parent, Palette palette, List<EventDef> events, float width, float y, string title, string category)
        {
            int count = CountCategory(events, category);
            BoxTop(c, parent, palette.Header, 4, y, width - 8f, 16);
            BoxTop(c, parent, palette.Accent, 4, y + 16f, width - 8f, 2);
            LabelTop(c, parent, title, 11, palette.Text, 12, y, 180, y + 16, true);
            LabelTop(c, parent, count.ToString(CultureInfo.InvariantCulture), 11, palette.SubText, width - 36f, y, width - 12f, y + 16, false);
            y += 22f;
            if (count == 0)
            {
                LabelTop(c, parent, "None", 11, palette.SubText, 12, y, width - 12f, y + 16, false);
                return y + 22f;
            }
            for (int i = 0; i < events.Count; i++)
            {
                var ev = events[i];
                if (ev == null || !CategoryOf(ev).Equals(category, StringComparison.OrdinalIgnoreCase)) continue;
                y = DrawEventRow(c, parent, palette, ev, width, y, category);
            }
            return y + 2f;
        }

        private float DrawEventRow(CuiElementContainer c, string parent, Palette palette, EventDef ev, float width, float y, string category)
        {
            BoxTop(c, parent, palette.Card, 4, y, width - 8f, 28);
            bool tag = category.Equals("seasonal", StringComparison.OrdinalIgnoreCase) && IsCustomEvent(ev);
            float nameRight = tag ? 146f : 168f;
            LabelTop(c, parent, ev.Name, 12, palette.Text, 12, y + 2, nameRight, y + 26, true);
            if (tag)
                LabelTop(c, parent, "custom", 10, palette.AccentText, 148, y + 4, 196, y + 24, false);
            string next = NextLabel(ev);
            float nextLeft = tag ? 200f : 168f;
            LabelTop(c, parent, next, 11, next == "live" ? palette.AccentText : palette.SubText, nextLeft, y + 4, width - 168f, y + 24, false);
            ButtonTop(c, parent, UiCmd + " toggle " + ev.Id, ev.Enabled ? palette.Accent : palette.Button, width - 156f, y + 5, 36, 18, ev.Enabled ? "On" : "Off", 10, ev.Enabled ? palette.AccentText : palette.ButtonText);
            ButtonTop(c, parent, UiCmd + " start " + ev.Id, palette.Button, width - 116f, y + 5, 52, 18, "Start", 10, palette.ButtonText);
            ButtonTop(c, parent, UiCmd + " edit " + ev.Id, palette.Button, width - 60f, y + 5, 48, 18, "Edit", 10, palette.ButtonText);
            return y + 32f;
        }

        private void DrawEdit(CuiElementContainer c, string parent, Palette palette, EventDef ev, float width)
        {
            if (ev == null)
            {
                LabelTop(c, parent, "That event is no longer saved.", 12, palette.SubText, 8, 8, width - 8, 28, false);
                return;
            }
            float y = 8f;
            string note = KindNote(ev.Kind);
            if (note.Length > 0)
            {
                LabelTop(c, parent, note, 10, palette.SubText, 8, y, width - 12, y + 28, false);
                y += 32f;
            }
            if (IsCustomEvent(ev))
            {
                LabelTop(c, parent, "Group", 10, palette.SubText, 8, y, 70, y + 14, true);
                y += 16f;
                bool seasonal = CategoryOf(ev).Equals("seasonal", StringComparison.OrdinalIgnoreCase);
                ButtonTop(c, parent, UiCmd + " group " + ev.Id + " custom", seasonal ? palette.Button : palette.Accent, 8, y, 88, 20, "Custom", 11, seasonal ? palette.ButtonText : palette.AccentText);
                ButtonTop(c, parent, UiCmd + " group " + ev.Id + " seasonal", seasonal ? palette.Accent : palette.Button, 100, y, 100, 20, "Seasonal", 11, seasonal ? palette.AccentText : palette.ButtonText);
                y += 26f;
            }
            LabelTop(c, parent, "Schedule", 10, palette.SubText, 8, y, 120, y + 14, true);
            y += 16f;
            ButtonTop(c, parent, UiCmd + " mode " + ev.Id + " minutes", IsMode(ev, "minutes") ? palette.Accent : palette.Button, 8, y, 78, 20, "Minutes", 11, IsMode(ev, "minutes") ? palette.AccentText : palette.ButtonText);
            ButtonTop(c, parent, UiCmd + " mode " + ev.Id + " hours", IsMode(ev, "hours") ? palette.Accent : palette.Button, 90, y, 70, 20, "Hours", 11, IsMode(ev, "hours") ? palette.AccentText : palette.ButtonText);
            ButtonTop(c, parent, UiCmd + " mode " + ev.Id + " clock", IsMode(ev, "clock") ? palette.Accent : palette.Button, 164, y, 70, 20, "Clock", 11, IsMode(ev, "clock") ? palette.AccentText : palette.ButtonText);
            y += 26f;
            if (IsClock(ev))
                y = Field(c, parent, palette, width, y, "Times (HH:MM, comma separated)", string.Join(", ", (ev.Times ?? new List<string>()).ToArray()), UiCmd + " times " + ev.Id, 80);
            else
                y = Field(c, parent, palette, width, y, IsMode(ev, "minutes") ? "Every N minutes" : "Every N hours", ev.Interval.ToString(CultureInfo.InvariantCulture), UiCmd + " interval " + ev.Id, 6);
            LabelTop(c, parent, "Days", 10, palette.SubText, 8, y, 50, y + 16, true);
            y += 16f;
            bool every = ev.Days == null || ev.Days.Count == 0;
            ButtonTop(c, parent, UiCmd + " alldays " + ev.Id, every ? palette.Accent : palette.Button, 8, y, 42, 18, "All", 10, every ? palette.AccentText : palette.ButtonText);
            for (int i = 0; i < DayKeys.Length; i++)
            {
                bool on = every || HasDay(ev, DayKeys[i]);
                ButtonTop(c, parent, UiCmd + " day " + ev.Id + " " + DayKeys[i], on ? palette.Accent : palette.Button, 54 + i * 46, y, 42, 18, DayLabel(DayKeys[i]), 10, on ? palette.AccentText : palette.ButtonText);
            }
            y += 24f;
            y = Field(c, parent, palette, width, y, "Random extra minutes", ev.JitterMinutes.ToString(CultureInfo.InvariantCulture), UiCmd + " jitter " + ev.Id, 4);
            y = Field(c, parent, palette, width, y, "Min players online (0 = any)", ev.MinPlayers.ToString(CultureInfo.InvariantCulture), UiCmd + " players " + ev.Id, 4);
            if (!ev.Custom)
            {
                LabelTop(c, parent, "Vanilla automatic spawn", 10, palette.SubText, 8, y, 200, y + 14, true);
                y += 16f;
                string auto = string.IsNullOrEmpty(ev.VanillaAuto) ? "default" : ev.VanillaAuto;
                ButtonTop(c, parent, UiCmd + " auto " + ev.Id, palette.Button, 8, y, 200, 20, "Vanilla: " + auto, 11, palette.ButtonText);
                y += 26f;
            }
            ButtonTop(c, parent, UiCmd + " announce " + ev.Id, ev.Announce ? palette.Accent : palette.Button, 8, y, 150, 20, ev.Announce ? "Chat on" : "Chat off", 11, ev.Announce ? palette.AccentText : palette.ButtonText);
            y += 24f;
            y = Field(c, parent, palette, width, y, "Chat text. {event} {grid} {time} {id}. Color and size tags work.", ev.Announcement ?? "", UiCmd + " text " + ev.Id, 220);
            if (ev.Custom)
            {
                y = Field(c, parent, palette, width, y, "Name", ev.Name ?? "", UiCmd + " name " + ev.Id, 32);
                y = Field(c, parent, palette, width, y, "Console commands, separated with |", string.Join(" | ", (ev.Commands ?? new List<string>()).ToArray()), UiCmd + " commands " + ev.Id, 240);
                ButtonTop(c, parent, UiCmd + " delete " + ev.Id, palette.Danger, 8, y, 120, 20, "Delete", 11, palette.DangerText);
            }
            LabelTop(c, parent, "Next: " + NextLabel(ev), 11, palette.SubText, 8, y + 28, width - 12, y + 46, false);
        }

        private static float EditHeight(EventDef ev)
        {
            bool custom = IsCustomEvent(ev);
            float y = 8f;
            if (ev != null && KindNote(ev.Kind).Length > 0) y += 32f;
            if (custom) y += 42f;
            y += 16f + 26f + 44f + 16f + 24f + 44f + 44f;
            if (ev == null || !ev.Custom) y += 42f;
            y += 24f + 44f;
            if (ev != null && ev.Custom) y += 44f + 44f;
            return y + 54f;
        }

        private static float Field(CuiElementContainer c, string parent, Palette palette, float width, float y, string label, string value, string command, int limit)
        {
            LabelTop(c, parent, label, 10, palette.SubText, 8, y, width - 16, y + 14, false);
            string box = BoxTop(c, parent, palette.Background, 8, y + 16, width - 20, 22);
            c.Add(new CuiElement
            {
                Name = CuiHelper.GetGuid(),
                Parent = box,
                Components =
                {
                    new CuiInputFieldComponent
                    {
                        Text = value ?? "",
                        Command = command,
                        FontSize = 12,
                        Color = Hex(palette.Text),
                        Align = TextAnchor.MiddleLeft,
                        Font = FontReg,
                        CharsLimit = limit,
                        NeedsKeyboard = true
                    },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "6 0", OffsetMax = "-6 0" }
                }
            });
            return y + 44f;
        }

        private static bool IsMode(EventDef ev, string mode)
        {
            string current = string.IsNullOrEmpty(ev.Mode) ? "hours" : ev.Mode;
            return current.Equals(mode, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasDay(EventDef ev, string key)
        {
            if (ev.Days == null) return false;
            for (int i = 0; i < ev.Days.Count; i++)
                if (ev.Days[i].Equals(key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string DayLabel(string key)
        {
            switch (key)
            {
                case "mon": return "Mon";
                case "tue": return "Tue";
                case "wed": return "Wed";
                case "thu": return "Thu";
                case "fri": return "Fri";
                case "sat": return "Sat";
                default: return "Sun";
            }
        }

        private static string KindNote(string kind)
        {
            switch (kind)
            {
                case "deepsea": return "The game still posts its own deep sea notice. Auto-open is held and restored on unload.";
                case "vendor": return "Chat on skips the game notice. Chat off keeps the game notice.";
                case "satellite": return "Player satellite launches are not blocked. The game still shows its crash line.";
                case "egghunt": return "Needs the easter schedule or an egg hunt already on the server. The hunt posts its own results.";
                case "halloween": return "Stays on after a start until you disable this event. Unload restores the convar.";
                case "christmas": return "Stays on after a start until you disable this event. Unload restores the convar.";
                case "bradley": return "Launch Site only. Automatic respawn stays off while vanilla auto is blocked.";
                case "roadbradley": return "Needs the road Bradley schedule on this map.";
                case "chinook": return "Vanilla timer stays paused. An airfield call can still launch the chinook.";
                case "cargo": return "The schedule is paused. The cargo event convar stays on so our ship still runs.";
                default: return "";
            }
        }

        private List<EventDef> Ordered()
        {
            var list = new List<EventDef>();
            if (store == null || store.Events == null) return list;
            for (int i = 0; i < store.Events.Count; i++)
                if (store.Events[i] != null) list.Add(store.Events[i]);
            list.Sort(CompareEvents);
            return list;
        }

        private static int CompareEvents(EventDef a, EventDef b)
        {
            int cat = CategoryRank(CategoryOf(a)).CompareTo(CategoryRank(CategoryOf(b)));
            if (cat != 0) return cat;
            int rank = WithinRank(a).CompareTo(WithinRank(b));
            if (rank != 0) return rank;
            int name = string.Compare(a == null ? "" : a.Name, b == null ? "" : b.Name, StringComparison.OrdinalIgnoreCase);
            if (name != 0) return name;
            return string.Compare(a == null ? "" : a.Id, b == null ? "" : b.Id, StringComparison.OrdinalIgnoreCase);
        }

        private static float ListContentHeight(List<EventDef> events)
        {
            float y = 4f;
            y = SectionSpan(y, CountCategory(events, "custom"));
            y = SectionSpan(y, CountCategory(events, "vanilla"));
            y = SectionSpan(y, CountCategory(events, "seasonal"));
            return y + 4f;
        }

        private static float SectionSpan(float y, int count)
        {
            y += 22f;
            if (count == 0) return y + 22f;
            return y + count * 32f + 2f;
        }

        private static int CountCategory(List<EventDef> events, string category)
        {
            int count = 0;
            if (events == null) return 0;
            for (int i = 0; i < events.Count; i++)
                if (events[i] != null && CategoryOf(events[i]).Equals(category, StringComparison.OrdinalIgnoreCase)) count++;
            return count;
        }

        private static bool IsCustomEvent(EventDef ev)
        {
            return ev != null && (ev.Custom || string.Equals(ev.Kind, "custom", StringComparison.OrdinalIgnoreCase));
        }

        private static string CustomCategory(string category)
        {
            return string.Equals(category, "seasonal", StringComparison.OrdinalIgnoreCase) ? "seasonal" : "custom";
        }

        private static string CategoryOf(EventDef ev)
        {
            if (ev == null) return "vanilla";
            if (IsCustomEvent(ev))
                return string.Equals(ev.Category, "seasonal", StringComparison.OrdinalIgnoreCase) ? "seasonal" : "custom";
            if (Listed(SeasonalOrder, ev.Id) || Listed(SeasonalOrder, ev.Kind)) return "seasonal";
            return "vanilla";
        }

        private static int CategoryRank(string category)
        {
            if (string.Equals(category, "custom", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(category, "vanilla", StringComparison.OrdinalIgnoreCase)) return 1;
            return 2;
        }

        private static int WithinRank(EventDef ev)
        {
            if (ev == null) return 900;
            if (IsCustomEvent(ev)) return 400;
            int vanilla = ListedAt(VanillaOrder, ev.Id);
            if (vanilla < 0) vanilla = ListedAt(VanillaOrder, ev.Kind);
            if (vanilla >= 0) return vanilla;
            int seasonal = ListedAt(SeasonalOrder, ev.Id);
            if (seasonal < 0) seasonal = ListedAt(SeasonalOrder, ev.Kind);
            if (seasonal >= 0) return seasonal;
            return 200;
        }

        private static bool Listed(string[] ids, string value)
        {
            return ListedAt(ids, value) >= 0;
        }

        private static int ListedAt(string[] ids, string value)
        {
            if (ids == null || string.IsNullOrEmpty(value)) return -1;
            for (int i = 0; i < ids.Length; i++)
                if (ids[i].Equals(value, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        #endregion

        #region Theme

        private Palette LoadPalette()
        {
            var fallback = new Palette();
            try
            {
                string path = Path.Combine(Interface.Oxide.ConfigDirectory, string.IsNullOrEmpty(config.ThemeFile) ? "NightshadeShop.json" : config.ThemeFile);
                if (!File.Exists(path)) return fallback;
                var root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
                var rows = new List<ThemeRow>();
                var themes = root["Themes"] as Newtonsoft.Json.Linq.JArray;
                if (themes != null)
                {
                    for (int i = 0; i < themes.Count; i++)
                    {
                        var item = themes[i] as Newtonsoft.Json.Linq.JObject;
                        if (item == null) continue;
                        var colors = item["Colors (#RRGGBB or #RRGGBBAA)"] as Newtonsoft.Json.Linq.JObject;
                        rows.Add(new ThemeRow
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
                var chosen = ResolveTheme(mode, rows, today);
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
                    DangerText = fallback.DangerText
                };
            }
            catch (Exception ex)
            {
                PrintWarning("Theme file could not be used. " + ex.Message);
                return fallback;
            }
        }

        private static ThemeRow ResolveTheme(string mode, IList<ThemeRow> themes, int monthDay)
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

        private static bool InRange(string from, string to, int today)
        {
            int start = ParseMd(from, 101);
            int end = ParseMd(to, 1231);
            return start <= end ? today >= start && today <= end : today >= start || today <= end;
        }

        private static int ParseMd(string text, int fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            var parts = text.Split('-');
            int month, day;
            if (parts.Length == 2 && int.TryParse(parts[0], out month) && int.TryParse(parts[1], out day))
                return month * 100 + day;
            return fallback;
        }

        private static string ColorOf(Newtonsoft.Json.Linq.JObject colors, string key, string fallback)
        {
            if (colors == null) return fallback;
            var value = colors.Value<string>(key);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        private string Logo()
        {
            try
            {
                string path = Path.Combine(Interface.Oxide.ConfigDirectory, string.IsNullOrEmpty(config.LogoFile) ? "NightshadeInfo.json" : config.LogoFile);
                if (File.Exists(path))
                {
                    var root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
                    var url = root.Value<string>("Logo URL");
                    if (!string.IsNullOrEmpty(url)) return url;
                }
            }
            catch { }
            return string.IsNullOrEmpty(config.LogoUrl) ? DefaultLogo : config.LogoUrl;
        }

        #endregion

        #region CUI helpers

        private static string Box(CuiElementContainer c, string parent, string color, string anchorMin, string anchorMax, float x1, float y1, float x2, float y2)
        {
            string name = CuiHelper.GetGuid();
            c.Add(new CuiElement
            {
                Name = name,
                Parent = parent,
                Components =
                {
                    new CuiImageComponent { Color = Hex(color) },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = anchorMin,
                        AnchorMax = anchorMax,
                        OffsetMin = Px(x1, y1),
                        OffsetMax = Px(x2, y2)
                    }
                }
            });
            return name;
        }

        private static string BoxTop(CuiElementContainer c, string parent, string color, float x, float y, float w, float h)
        {
            string name = CuiHelper.GetGuid();
            c.Add(new CuiElement
            {
                Name = name,
                Parent = parent,
                Components =
                {
                    new CuiImageComponent { Color = Hex(color), BlocksRaycast = true },
                    new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = Px(x, -(y + h)), OffsetMax = Px(x + w, -y) }
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
                    new CuiTextComponent { Text = text ?? "", FontSize = size, Color = Hex(color), Align = align, Font = bold ? FontBold : FontReg, BlocksRaycast = false },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = Px(x1, y1), OffsetMax = Px(x2, y2) }
                }
            });
        }

        private static void LabelTop(CuiElementContainer c, string parent, string text, int size, string color, float x1, float y1, float x2, float y2, bool bold)
        {
            c.Add(new CuiElement
            {
                Name = CuiHelper.GetGuid(),
                Parent = parent,
                Components =
                {
                    new CuiTextComponent { Text = text ?? "", FontSize = size, Color = Hex(color), Align = TextAnchor.MiddleLeft, Font = bold ? FontBold : FontReg, BlocksRaycast = false },
                    new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = Px(x1, -y2), OffsetMax = Px(x2, -y1) }
                }
            });
        }

        private static void Button(CuiElementContainer c, string parent, string command, string color, float x1, float y1, float x2, float y2, string text, int size, string textColor)
        {
            string name = CuiHelper.GetGuid();
            c.Add(new CuiElement
            {
                Name = name,
                Parent = parent,
                Components =
                {
                    new CuiButtonComponent { Command = command ?? "", Color = Hex(color), BlocksRaycast = true },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = Px(x1, y1), OffsetMax = Px(x2, y2) }
                }
            });
            Label(c, name, text, size, textColor, TextAnchor.MiddleCenter, 0, 0, x2 - x1, y2 - y1, true);
        }

        private static void ButtonTop(CuiElementContainer c, string parent, string command, string color, float x, float y, float w, float h, string text, int size, string textColor)
        {
            string name = CuiHelper.GetGuid();
            c.Add(new CuiElement
            {
                Name = name,
                Parent = parent,
                Components =
                {
                    new CuiButtonComponent { Command = command ?? "", Color = Hex(color), BlocksRaycast = true },
                    new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = Px(x, -(y + h)), OffsetMax = Px(x + w, -y) }
                }
            });
            c.Add(new CuiElement
            {
                Name = CuiHelper.GetGuid(),
                Parent = name,
                Components =
                {
                    new CuiTextComponent { Text = text ?? "", FontSize = size, Color = Hex(textColor), Align = TextAnchor.MiddleCenter, Font = FontBold },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                }
            });
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
            catch { return "1 1 1 1"; }
        }

        private static string Px(float x, float y)
        {
            return x.ToString("0.##", CultureInfo.InvariantCulture) + " " + y.ToString("0.##", CultureInfo.InvariantCulture);
        }

        #endregion
    }
}
