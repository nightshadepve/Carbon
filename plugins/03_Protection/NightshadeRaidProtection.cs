using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
#if !NIGHTSHADE_RAID_TESTS
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;
#endif

namespace Oxide.Plugins
{
    // Eastern weekend clock and the TruePVE toggle plan. Pure on purpose so the
    // boundary, DST, restart, and ruleset-file tests can compile without Rust.
    public static class NightshadeWeekendClock
    {
        public const string RulesetCommand = "tpve.ruleset";
        public const string ReloadCommand = "c.reload TruePVE";

        public struct TruePveState
        {
            public bool Found;
            public string Ruleset;
            public bool ScheduleEnabled;
            public string Error;
        }

        public struct TogglePlan
        {
            public string Ruleset;
            public bool WriteConfig;
            public bool DisableSchedule;
            public bool RunRulesetCommand;
            public bool ReloadTruePve;
            // File already matches and this process has not tracked a ruleset yet. Record it; do not touch TruePVE.
            public bool Adopt;
            public string Reason;
        }

        public struct TrackedRuleset
        {
            public bool Known;
            public string Ruleset;
        }

        public struct ApplyResult
        {
            public bool Wrote;
            public bool RanRulesetCommand;
            public bool Reloaded;
            public string Error;
        }

        public static TimeZoneInfo ResolveEastern(out bool fallback)
        {
            fallback = false;
            string[] ids = { "America/New_York", "Eastern Standard Time", "US/Eastern" };
            foreach (string id in ids)
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
            fallback = true;
            return FallbackEastern();
        }

        // US DST since 2007: second Sunday in March, first Sunday in November, both at 02:00.
        public static TimeZoneInfo FallbackEastern()
        {
            try
            {
                TimeZoneInfo.TransitionTime start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
                TimeZoneInfo.TransitionTime end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
                TimeZoneInfo.AdjustmentRule rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                    new DateTime(2007, 1, 1), new DateTime(9999, 12, 31), TimeSpan.FromHours(1), start, end);
                return TimeZoneInfo.CreateCustomTimeZone(
                    "Nightshade-Eastern", TimeSpan.FromHours(-5), "Eastern Time (fallback)",
                    "Eastern Standard Time", "Eastern Daylight Time", new[] { rule });
            }
            catch
            {
                return TimeZoneInfo.CreateCustomTimeZone("Nightshade-Eastern-EST", TimeSpan.FromHours(-5), "Eastern Time (fixed EST)", "EST");
            }
        }

        // Friday 20:00 through Monday 03:00 on the supplied zone. End is exclusive.
        // Outside the window, start/end are the next weekend.
        public static void Evaluate(DateTime utc, TimeZoneInfo zone, out DateTime startUtc, out DateTime endUtc, out bool active)
        {
            if (zone == null) zone = TimeZoneInfo.Utc;
            DateTime instant = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(instant, zone);
            int back = ((int)local.DayOfWeek - (int)DayOfWeek.Friday + 7) % 7;
            DateTime friday = local.Date.AddDays(-back);
            DateTime candidateStart = friday.AddHours(20);
            DateTime candidateEnd = friday.AddDays(3).AddHours(3);
            active = local >= candidateStart && local < candidateEnd;
            if (!active && local >= candidateEnd) friday = friday.AddDays(7);
            startUtc = ToUtc(friday.AddHours(20), zone);
            endUtc = ToUtc(friday.AddDays(3).AddHours(3), zone);
        }

        public static DateTime ToUtc(DateTime easternWall, TimeZoneInfo zone)
        {
            if (zone == null) zone = TimeZoneInfo.Utc;
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(easternWall, DateTimeKind.Unspecified), zone);
        }

        public static double SecondsUntil(DateTime utc, DateTime startUtc, DateTime endUtc, bool active)
        {
            DateTime instant = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return Math.Max(0d, ((active ? endUtc : startUtc) - instant).TotalSeconds);
        }

        // Wake at the next edge, but at least once a day, and never in under a second.
        public static double BoundaryDelaySeconds(DateTime utc, DateTime startUtc, DateTime endUtc, bool active)
        {
            DateTime instant = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            double seconds = Math.Max(1d, ((active ? endUtc : startUtc) - instant).TotalSeconds);
            return Math.Min(seconds, 86400d);
        }

        public static TruePveState Read(string path)
        {
            var state = new TruePveState();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                state.Error = "TruePVE.json was not found.";
                return state;
            }
            try
            {
                JObject root = JObject.Parse(File.ReadAllText(path));
                state.Found = true;
                JToken ruleset = root["Default RuleSet"];
                if (ruleset == null || ruleset.Type == JTokenType.Null) state.Ruleset = string.Empty;
                else state.Ruleset = ruleset.Type == JTokenType.String ? (string)ruleset : ruleset.ToString();
                JObject schedule = root["Schedule"] as JObject;
                JToken enabled = schedule == null ? null : schedule["enabled"];
                state.ScheduleEnabled = enabled != null && enabled.Type == JTokenType.Boolean && (bool)enabled;
                return state;
            }
            catch (Exception ex)
            {
                state.Error = ex.Message;
                return state;
            }
        }

        // Purge wins over the weekend. The file's Default RuleSet is what TruePVE loads
        // on init and what a later reload (raid base mapping, purge sync) puts back.
        // tpve.ruleset alone only changes private in-memory state and does not survive that.
        public static TogglePlan Plan(TruePveState state, bool purgeActive, string purgeRuleset, bool weekendActive, string pvpRuleset, string pveRuleset)
        {
            string desired;
            string reason;
            if (purgeActive && !string.IsNullOrEmpty(purgeRuleset))
            {
                desired = purgeRuleset;
                reason = "purge";
            }
            else if (weekendActive)
            {
                desired = string.IsNullOrEmpty(pvpRuleset) ? "nightshade_pvp_weekend" : pvpRuleset;
                reason = "weekend";
            }
            else
            {
                desired = string.IsNullOrEmpty(pveRuleset) ? "default" : pveRuleset;
                reason = "weekday";
            }
            bool rulesetMismatch = !state.Found || !string.Equals(state.Ruleset, desired, StringComparison.OrdinalIgnoreCase);
            bool scheduleOn = state.Found && state.ScheduleEnabled;
            bool write = rulesetMismatch || scheduleOn;
            return new TogglePlan
            {
                Ruleset = desired,
                WriteConfig = write,
                DisableSchedule = scheduleOn,
                // A matching file is not a change. tpve.ruleset and c.reload run only from Decide.
                RunRulesetCommand = write,
                ReloadTruePve = write,
                Reason = reason
            };
        }

        // Steady state is a compare: no write, no tpve.ruleset, no reload.
        // Write and reload only when the target changed or the file disagrees
        // (stale Default RuleSet, or Schedule.enabled, including after another plugin reloads TruePVE).
        // Faketime never writes or reloads; it pushes tpve.ruleset only when the test target changes.
        public static TogglePlan Decide(TruePveState file, TrackedRuleset tracked, bool purgeActive, string purgeRuleset, bool weekendActive, string pvpRuleset, string pveRuleset, bool fakeTime)
        {
            TogglePlan plan = Plan(file, purgeActive, purgeRuleset, weekendActive, pvpRuleset, pveRuleset);
            bool sameMemory = tracked.Known && string.Equals(tracked.Ruleset, plan.Ruleset, StringComparison.Ordinal);
            if (fakeTime)
            {
                plan.WriteConfig = false;
                plan.ReloadTruePve = false;
                plan.DisableSchedule = false;
                plan.RunRulesetCommand = !sameMemory;
                plan.Adopt = false;
                return plan;
            }
            if (!plan.WriteConfig && !plan.ReloadTruePve)
            {
                if (sameMemory) return plan;
                // First look at a correct file: TruePVE already loaded it. Do not reload.
                // A later memory drift with a correct file (test clock cleared) is one tpve.ruleset and no reload.
                plan.RunRulesetCommand = tracked.Known;
                plan.Adopt = !tracked.Known;
                return plan;
            }
            return plan;
        }

        public static ApplyResult Apply(string path, TogglePlan plan, bool truePveLoaded, Action<string, object[]> command)
        {
            var result = new ApplyResult();
            if (plan.WriteConfig)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    result.Error = "TruePVE.json was not found.";
                else
                {
                    try
                    {
                        JObject root = JObject.Parse(File.ReadAllText(path));
                        root["Default RuleSet"] = plan.Ruleset ?? string.Empty;
                        JObject schedule = root["Schedule"] as JObject;
                        if (schedule != null) schedule["enabled"] = false;
                        string text = root.ToString(Formatting.Indented) + "\n";
                        string tmp = path + ".nightshade.tmp";
                        File.WriteAllText(tmp, text, new UTF8Encoding(false));
                        try
                        {
                            File.Replace(tmp, path, null);
                        }
                        catch
                        {
                            File.Copy(tmp, path, true);
                            File.Delete(tmp);
                        }
                        result.Wrote = true;
                    }
                    catch (Exception ex)
                    {
                        result.Error = ex.Message;
                    }
                }
            }
            if (command == null || !truePveLoaded) return result;
            if (plan.RunRulesetCommand && !string.IsNullOrEmpty(plan.Ruleset))
            {
                command(RulesetCommand, new object[] { plan.Ruleset });
                result.RanRulesetCommand = true;
            }
            // Reload only after a successful write. Reloading the old file would put the previous ruleset back.
            if (plan.ReloadTruePve && result.Wrote)
            {
                command(ReloadCommand, new object[0]);
                result.Reloaded = true;
            }
            return result;
        }
    }

    // Giant Excavator weekend rates. Pure so the fuel-fraction tests compile without Rust.
    // Decompiled from Assembly-CSharp (Carbon 2.0.262, publicized):
    //   ExcavatorArm ctor: resourceProductionTickRate = 3, timeForFullResources = 120
    //   DieselEngine ctor: runningTimePerFuelUnit = 120
    //   ProduceResources adds (tick / timeForFullResources) of the selected pile each invoke
    //   BeginMining does InvokeRepeating(ProduceResources, tick, tick) and does not read the fuel timer
    //   DieselEngine.FixedUpdate drains cachedFuelTime and, when it runs out, adds runningTimePerFuelUnit
    // Those three rate fields are not in the save. cachedFuelTime is (dieselEngine.fuelTime).
    public static class NightshadeExcavatorBoost
    {
        public const float VanillaTickRate = 3f;
        public const float VanillaFullResourcesSeconds = 120f;
        public const float VanillaFuelSeconds = 120f;
        public const float DefaultSpeedMultiplier = 2f;
        // DieselEngine.EngineOn broadcasts DieselEngineOn at 20m, which is what the arm hears.
        public const float EnginePairMeters = 50f;

        public struct Baseline
        {
            public float TickRate;
            public float FullResourcesSeconds;
            public float FuelSeconds;
        }

        public struct Target
        {
            public bool Applied;
            public float TickRate;
            public float FullResourcesSeconds;
            public float FuelSeconds;
        }

        public static bool Near(float a, float b)
        {
            return Math.Abs(a - b) <= 0.01f;
        }

        public static float SanitizeMultiplier(float multiplier)
        {
            if (float.IsNaN(multiplier) || float.IsInfinity(multiplier) || multiplier < 0.01f)
                return DefaultSpeedMultiplier;
            return multiplier;
        }

        public static Baseline Defaults()
        {
            return new Baseline
            {
                TickRate = VanillaTickRate,
                FullResourcesSeconds = VanillaFullResourcesSeconds,
                FuelSeconds = VanillaFuelSeconds
            };
        }

        // Tick stays at the captured vanilla tick. Speed is timeForFullResources / multiplier.
        // KeepYieldPerDiesel shortens the diesel by the same factor, so ore per diesel stays put.
        public static Target TargetFrom(Baseline baseline, bool windowActive, bool enabled, float speedMultiplier, bool keepYieldPerDiesel)
        {
            var target = new Target
            {
                Applied = false,
                TickRate = baseline.TickRate,
                FullResourcesSeconds = baseline.FullResourcesSeconds,
                FuelSeconds = baseline.FuelSeconds
            };
            if (!windowActive || !enabled) return target;
            float mult = SanitizeMultiplier(speedMultiplier);
            if (Near(mult, 1f)) return target;
            target.Applied = true;
            target.FullResourcesSeconds = baseline.FullResourcesSeconds / mult;
            if (keepYieldPerDiesel)
                target.FuelSeconds = baseline.FuelSeconds / mult;
            return target;
        }

        // A hot reload that skipped Unload leaves 3/60/60 on the live entities. Those floats
        // are not what a fresh spawn loads (the ctor writes 3/120/120). Recognize that exact
        // boosted triple and keep the ctor numbers as vanilla. Anything else is captured as-is.
        public static Baseline Capture(float tick, float full, float fuel, float speedMultiplier, bool keepYieldPerDiesel)
        {
            Baseline defaults = Defaults();
            Target boosted = TargetFrom(defaults, true, true, speedMultiplier, keepYieldPerDiesel);
            if (boosted.Applied
                && Near(tick, boosted.TickRate)
                && Near(full, boosted.FullResourcesSeconds)
                && Near(fuel, boosted.FuelSeconds))
                return defaults;
            if (tick <= 0f) tick = defaults.TickRate;
            if (full <= 0f) full = defaults.FullResourcesSeconds;
            if (fuel <= 0f) fuel = defaults.FuelSeconds;
            return new Baseline { TickRate = tick, FullResourcesSeconds = full, FuelSeconds = fuel };
        }

        // Keep the fraction of the diesel already paid for. 90s of a 120s unit becomes 45s of a 60s unit.
        // Zero stays zero, so a stopped engine does not gain a free unit.
        public static float ScaleFuel(float cachedFuelTime, float oldSecondsPerUnit, float newSecondsPerUnit)
        {
            if (cachedFuelTime <= 0f) return 0f;
            if (oldSecondsPerUnit <= 0f || newSecondsPerUnit <= 0f) return cachedFuelTime;
            if (Near(oldSecondsPerUnit, newSecondsPerUnit)) return cachedFuelTime;
            return cachedFuelTime * (newSecondsPerUnit / oldSecondsPerUnit);
        }

        // BeginMining cancels ProduceResources and waits a full tick before the next payout.
        // Restart only when the interval itself must change. The weekend boost does not change it.
        public static bool ShouldRestartMining(bool mining, float liveTick, float desiredTick)
        {
            return mining && !Near(liveTick, desiredTick);
        }
    }

    // Whether one authorized player has been offline long enough for automatic protection.
    // A connected player is never eligible. When both the sleeper clock and the saved
    // last-seen exist, both have to clear the grace, so a stale login stamp cannot win.
    public static class NightshadeOfflineGate
    {
        public static bool OfflineLongEnough(bool online, bool hasSleepClock, float sleepSeconds, bool hasLastSeen, long lastSeenUtcTicks, DateTime nowUtc, int graceMinutes)
        {
            if (online) return false;
            int grace = graceMinutes < 0 ? 0 : graceMinutes;
            bool anyClock = false;
            if (hasSleepClock)
            {
                anyClock = true;
                if (sleepSeconds < 0f) sleepSeconds = 0f;
                if (sleepSeconds < grace * 60f) return false;
            }
            if (hasLastSeen && lastSeenUtcTicks > 0L)
            {
                anyClock = true;
                double minutes = (nowUtc - new DateTime(lastSeenUtcTicks, DateTimeKind.Utc)).TotalMinutes;
                if (minutes < grace) return false;
            }
            return anyClock;
        }

        // Paid shields fully block. Offline protection scales only when the base qualified.
        public static float Reduction(bool paidShield, bool offlineProtected, int offlinePercent)
        {
            if (paidShield) return 1f;
            if (!offlineProtected || offlinePercent <= 0) return 0f;
            float scale = offlinePercent / 100f;
            if (scale > 1f) return 1f;
            return scale;
        }

        // One cupboard considered for a hit. BuildingId 0 or a different building is foreign.
        public struct CupboardVote
        {
            public uint BuildingId;
            public bool Destroyed;
            public bool PaidShield;
            public bool OfflineProtected;
            public bool AttackerAuthorized;
        }

        public struct HitDecision
        {
            public bool Block;
            public bool Paid;
        }

        // Only cupboards attached to the hit building can block. A foreign overlap cannot
        // cancel a real shield or offline protection, and it cannot grant either.
        // When allowAuthorizedDamage is set, auth on the cupboard that would block skips that cupboard only.
        public static HitDecision Decide(uint hitBuildingId, bool allowAuthorizedDamage, CupboardVote[] cupboards)
        {
            HitDecision decision = new HitDecision();
            if (hitBuildingId == 0u || cupboards == null) return decision;
            for (int i = 0; i < cupboards.Length; i++)
            {
                CupboardVote vote = cupboards[i];
                if (vote.Destroyed || vote.BuildingId != hitBuildingId) continue;
                if (!vote.PaidShield && !vote.OfflineProtected) continue;
                if (allowAuthorizedDamage && vote.AttackerAuthorized) continue;
                decision.Block = true;
                if (vote.PaidShield) decision.Paid = true;
            }
            return decision;
        }

        // Privilege spheres intersect. Radius is the tool cupboard privilege radius.
        // This is one hop only: callers must not walk from a matched neighbor to its neighbors.
        public static bool PrivilegeZonesOverlap(float distanceMeters, float radiusMeters)
        {
            if (radiusMeters <= 0f || float.IsNaN(distanceMeters) || float.IsInfinity(distanceMeters) || distanceMeters < 0f)
                return false;
            return distanceMeters <= radiusMeters * 2f;
        }

        public static bool SameOwnerOnOverlap(ulong protectedOwnerId, ulong steamIdMinimum, bool authorizedOnOverlap, bool isOverlapOwner)
        {
            if (protectedOwnerId < steamIdMinimum) return false;
            return authorizedOnOverlap || isOverlapOwner;
        }

        // Direct overlap with a protected cupboard whose owner is also on the hit cupboard.
        // A shield copies as a full block. Offline protection copies only when the hit cupboard
        // itself also qualifies, so someone online on it does not inherit the grace.
        public struct OverlapLink
        {
            public bool ZonesOverlap;
            public bool OwnerOnOverlap;
            public bool SourceShield;
            public bool SourceOffline;
            public bool OverlapOffline;
            public bool AttackerAuthorizedOnSource;
        }

        public static HitDecision MatchedOverlap(bool share, bool allowAuthorizedDamage, OverlapLink link)
        {
            HitDecision decision = new HitDecision();
            if (!share || !link.ZonesOverlap || !link.OwnerOnOverlap) return decision;
            if (link.SourceShield)
            {
                if (allowAuthorizedDamage && link.AttackerAuthorizedOnSource) return decision;
                decision.Block = true;
                decision.Paid = true;
                return decision;
            }
            if (link.SourceOffline && link.OverlapOffline)
            {
                decision.Block = true;
                decision.Paid = false;
            }
            return decision;
        }
    }

#if !NIGHTSHADE_RAID_TESTS
    [Info("NightshadeRaidProtection", "Nightshade", "1.4.2")]
    [Description("PvP-window Tool Cupboard raid shields, offline protection, the Eastern-time weekend window that drives TruePVE, and the Giant Excavator weekend boost.")]
    public class NightshadeRaidProtection : RustPlugin
    {
        private const string PermAdmin = "nightshaderaidprotection.admin";
        private const string DataPath = "Nightshade/NightshadeRaidProtection/RaidProtection";
        private const ulong SteamIdMin = 76561197960265728UL;
        // These IDs identify the three dedicated custom-icon voucher skins.
        // The shop delivers PAPER items using these IDs. No purchase-registration plugin is needed.
        private const string VoucherShortname = "paper";
        private static readonly Dictionary<string, ulong> VoucherSkins = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
        {
            {"1h", 3810298504UL},
            {"24h", 3810324315UL},
            {"weekend", 3810324938UL}
        };

        [PluginReference] private Plugin TruePVE;
        [PluginReference("Nightshade Purge Status")] private Plugin PurgeStatus;

        private Settings settings;
        private Store store;
        private TimeZoneInfo eastern;
        private bool usedZoneFallback;
        private DateTime? fakeUtc;
        private bool windowReady;
        private bool windowActive;
        private bool suppressHooks;
        private Timer boundaryTimer;
        private Timer rulesTimer;
        private Timer shieldTimer;
        private NightshadeWeekendClock.TrackedRuleset appliedRuleset;
        private bool applyingRuleset;
        private readonly Dictionary<ulong, string> published = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, NightshadeExcavatorBoost.Baseline> excavatorBaselines = new Dictionary<ulong, NightshadeExcavatorBoost.Baseline>();
        private bool excavatorUnloading;
        private bool excavatorSaveHold;
        private bool excavatorBoosted;
        private bool excavatorEngineWarned;
        private Timer excavatorSaveTimer;
        private string excavatorStatus = "excavator not checked";
        private string excavatorLogSignature;

        // Offline-protection test switch. Memory only: a reload clears a command override.
        // The config key below can turn it back on, and it defaults to false.
        // While on, ONLY the offline-protection damage decision treats the PvP weekend as active.
        // It does not call CheckWindow, EnsureRuleset, or ApplyExcavator.
        // TruePVE rulesets and reloads, Giant Excavator speed, paid shields, and API_IsPvPActive
        // are unchanged by this switch. nrp.faketime still affects only the paths it already did.
        private bool forceOfflinePvp;
        private int? forceOfflineGraceMinutes;
        private float nextForceOfflineWarning;
        private const int MaxForceGraceMinutes = 10080;
        private const float ForceOfflineWarningSeconds = 300f;

        private class Settings
        {
            [JsonProperty("Enable paid TC raid shields")] public bool PaidShields = true;
            [JsonProperty("Enable automatic offline raid protection during PvP")] public bool OfflineProtection = true;
            [JsonProperty("Minutes everyone authorized on TC must be offline before auto protection")] public int OfflineGraceMinutes = 30;
            // Dev-server opt-in. False by default and not flipped by nsraid.forcepvp. A true value
            // enables the same in-memory offline-protection override after each load. It still does
            // not switch TruePVE or the excavator.
            [JsonProperty("Force PvP weekend for testing")] public bool ForcePvpWeekendForTesting = false;
            [JsonProperty("Offline protection damage reduction percent (100 = fully block)")] public int OfflineReductionPercent = 100;
            [JsonProperty("Prevent teammates authorized on TC from damaging their own shielded building")] public bool ProtectFromAuthorizedPlayers = false;
            [JsonProperty("Share protection with the owner's directly overlapping cupboards")] public bool ShareOverlappingCupboards = true;
            [JsonProperty("Show blocked-hit message to attacker (rate limited)")] public bool NotifyAttacker = true;
            [JsonProperty("Maximum total paid shield time on one TC in hours")] public int MaximumStackedHours = 72;
            [JsonProperty("Drive TruePVE from the Eastern weekend window")] public bool DriveTruePve = true;
            [JsonProperty("TruePVE ruleset during the weekend window")] public string PvpRuleset = "nightshade_pvp_weekend";
            [JsonProperty("TruePVE ruleset outside the weekend window")] public string PveRuleset = "default";
            [JsonProperty("Yield TruePVE changes while a purge is active")] public bool YieldToPurge = true;
            [JsonProperty("Excavator")] public ExcavatorSettings Excavator = new ExcavatorSettings();
        }

        private class ExcavatorSettings
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("SpeedMultiplier")] public float SpeedMultiplier = 2f;
            [JsonProperty("KeepYieldPerDiesel")] public bool KeepYieldPerDiesel = true;
        }

        private class Shield
        {
            public ulong CupboardId;
            public uint BuildingId;
            public double RemainingSeconds;
            public long LastAccountedTicks;
            public long ExpiresUtcTicks;
        }

        private class Store
        {
            public Dictionary<ulong, Shield> Shields = new Dictionary<ulong, Shield>();
            // Legacy UID records retained solely for compatibility with previously saved data.
            public HashSet<ulong> LegitimateVoucherUids = new HashSet<ulong>();
            public Dictionary<ulong, long> LastSeenUtcTicks = new Dictionary<ulong, long>();
        }

        private readonly Dictionary<ulong, float> notifyThrottle = new Dictionary<ulong, float>();

        protected override void LoadDefaultConfig() { settings = new Settings(); }
        protected override void LoadConfig()
        {
            base.LoadConfig();
            try { settings = Config.ReadObject<Settings>() ?? new Settings(); }
            catch (Exception ex) { PrintError("Invalid config; inspect and repair it: " + ex.Message); settings = new Settings(); }
            if (string.IsNullOrEmpty(settings.PvpRuleset)) settings.PvpRuleset = "nightshade_pvp_weekend";
            if (string.IsNullOrEmpty(settings.PveRuleset)) settings.PveRuleset = "default";
            if (settings.Excavator == null) settings.Excavator = new ExcavatorSettings();
            float excavatorMult = settings.Excavator.SpeedMultiplier;
            if (float.IsNaN(excavatorMult) || float.IsInfinity(excavatorMult) || excavatorMult < 0.01f)
                settings.Excavator.SpeedMultiplier = NightshadeExcavatorBoost.DefaultSpeedMultiplier;
            // Command overrides die with the plugin instance. Only the config key survives a reload.
            forceOfflinePvp = settings.ForcePvpWeekendForTesting;
            forceOfflineGraceMinutes = null;
            nextForceOfflineWarning = 0f;
            SaveConfig();
        }
        protected override void SaveConfig() => Config.WriteObject(settings, true);

        private void Init()
        {
            // Registered only. This permission is not granted to any user or group.
            permission.RegisterPermission(PermAdmin, this);
            cmd.AddChatCommand("raidvoucher", this, nameof(RedeemCommand));
            cmd.AddChatCommand("raidshield", this, nameof(StatusCommand));
            cmd.AddChatCommand("nsraidtest", this, nameof(CmdNsRaidTest));
            cmd.AddConsoleCommand("nrp.faketime", this, nameof(CmdFakeTime));
            cmd.AddConsoleCommand("nrp.window", this, nameof(CmdWindow));
            cmd.AddConsoleCommand("nsraid.forcepvp", this, nameof(CmdForcePvp));
            try { store = Interface.Oxide.DataFileSystem.ReadObject<Store>(DataPath) ?? new Store(); }
            catch (Exception ex) { throw new Exception("Raid shield data could not be loaded: purchases disabled to prevent lost vouchers. " + ex.Message); }
            if (store.Shields == null) store.Shields = new Dictionary<ulong, Shield>();
            if (store.LegitimateVoucherUids == null) store.LegitimateVoucherUids = new HashSet<ulong>();
            if (store.LastSeenUtcTicks == null) store.LastSeenUtcTicks = new Dictionary<ulong, long>();
            eastern = NightshadeWeekendClock.ResolveEastern(out usedZoneFallback);
        }

        private void Save() => Interface.Oxide.DataFileSystem.WriteObject(DataPath, store);
        private void OnServerSave()
        {
            // Entity serialization runs after this hook. Hold vanilla fuel seconds in cachedFuelTime
            // so a crash cannot reload a shortened timer against the ctor's 120s rate.
            SuspendExcavatorForSave();
            Save();
        }
        private void Unload()
        {
            excavatorUnloading = true;
            excavatorSaveTimer?.Destroy();
            excavatorSaveHold = false;
            RestoreExcavator();
            boundaryTimer?.Destroy();
            rulesTimer?.Destroy();
            shieldTimer?.Destroy();
            fakeUtc = null;
            if (forceOfflinePvp)
                PrintWarning("NightshadeRaidProtection TEST: forced PvP weekend for offline protection cleared on unload.");
            forceOfflinePvp = false;
            forceOfflineGraceMinutes = null;
            nextForceOfflineWarning = 0f;
            Save();
        }
        private void OnNewSave(string filename)
        {
            store.Shields.Clear();
            store.LegitimateVoucherUids.Clear();
            store.LastSeenUtcTicks.Clear();
            published.Clear();
            Save();
        }
        private void OnPlayerDisconnected(BasePlayer player, string reason) => NoteSeen(player);
        private void OnPlayerConnected(BasePlayer player) => NoteSeen(player);
        private void OnServerInitialized()
        {
            TouchConnectedPlayers();
            if (usedZoneFallback)
                PrintWarning("America/New_York and Eastern Standard Time were unavailable. Using a built-in US Eastern zone (EST/EDT).");
            SeedShieldStates();
            CheckWindow();
            WarnIfTruePveScheduleEnabled();
            EnsureRuleset();
            rulesTimer = timer.Every(60f, OnReconcileTick);
            Puts("Weekend window is America/New_York Friday 8:00 PM through Monday 3:00 AM. Giant Excavator uses that same window.");
            WarnForcedOffline(true);
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin != null && plugin.Name == "TruePVE")
                timer.Once(1f, EnsureRuleset);
        }

        private void OnPurgeStarted(double endTimestamp) => timer.Once(1f, EnsureRuleset);
        private void OnPurgeEnded() => timer.Once(1f, EnsureRuleset);

        private void OnEntitySpawned(ExcavatorArm arm)
        {
            if (arm != null) QueueExcavatorApply();
        }

        private void OnEntitySpawned(DieselEngine engine)
        {
            if (engine != null) QueueExcavatorApply();
        }

        private void OnEntityKill(BuildingPrivlidge tc)
        {
            if (tc == null || tc.net == null) return;
            ulong id = tc.net.ID.Value;
            Shield shield;
            if (!store.Shields.TryGetValue(id, out shield)) return;
            store.Shields.Remove(id);
            PublishState(id, shield != null ? shield.BuildingId : tc.buildingID, "none", 0d);
            Save();
        }

        #region Public API
        // Other plugins call these with Plugin.Call. Query them on load; the hooks below
        // fire only when the state changes while this plugin is already running.
        //
        // API_IsPvPActive() -> bool
        //     True during Friday 8:00 PM through Monday 3:00 AM America/New_York.
        //     A manual /purge is not included; ask Nightshade Purge Status API_IsPurgeActive.
        //     nrp.faketime and API_SetTestClockUtcTicks are included. API_ClearTestClock returns to the real clock.
        //     nsraid.forcepvp / /nsraidtest are NOT included. That switch only lets this plugin's
        //     offline protection treat the weekend as active. It does not change this API.
        // API_HasTestClock() -> bool, API_GetTestClockUtcTicks() -> long (0 when the test clock is off)
        // API_SetTestClockUtcTicks(long utcTicks) -> bool, API_ClearTestClock() -> bool
        //     Memory only. TruePVE.json is not written. The excavator boost follows this clock.
        // API_GetExcavatorStatus() -> string
        //     The same excavator clause nrp.window prints.
        // API_SecondsUntilPvPChange() -> double
        //     Seconds until the window opens, or until it closes when it is already open.
        // API_GetPvPStartUtcTicks() / API_GetPvPEndUtcTicks() -> long (DateTime UTC ticks)
        // API_GetWeekend() -> Dictionary
        //     active (bool), startUtcTicks (long), endUtcTicks (long), secondsUntilChange (double), ruleset (string)
        // API_GetPlayerShield(BasePlayer) -> Dictionary
        // API_GetCupboardShield(ulong cupboardNetId) -> Dictionary
        // API_GetBuildingShield(uint buildingId) -> Dictionary
        //     state "active" | "queued" | "none", seconds (double), tcId (ulong), buildingId (uint)
        //
        // Hooks (not fired for the state already in effect at plugin load):
        // OnNightshadePvPStarted(long startUtcTicks, long endUtcTicks)
        // OnNightshadePvPEnded(long startUtcTicks, long endUtcTicks)
        // OnNightshadeShieldStarted(ulong cupboardNetId, uint buildingId, double remainingSeconds)
        // OnNightshadeShieldQueued(ulong cupboardNetId, uint buildingId, double remainingSeconds)
        // OnNightshadeShieldEnded(ulong cupboardNetId, uint buildingId)

        [HookMethod("API_IsPvPActive")]
        public bool API_IsPvPActive()
        {
            DateTime start, end;
            bool active;
            Weekend(NowUtc(), out start, out end, out active);
            return active;
        }

        [HookMethod("API_SecondsUntilPvPChange")]
        public double API_SecondsUntilPvPChange()
        {
            DateTime start, end;
            bool active;
            Weekend(NowUtc(), out start, out end, out active);
            return SecondsUntilNow(start, end, active);
        }

        [HookMethod("API_GetPvPStartUtcTicks")]
        public long API_GetPvPStartUtcTicks()
        {
            DateTime start, end;
            bool active;
            Weekend(NowUtc(), out start, out end, out active);
            return start.Ticks;
        }

        [HookMethod("API_GetPvPEndUtcTicks")]
        public long API_GetPvPEndUtcTicks()
        {
            DateTime start, end;
            bool active;
            Weekend(NowUtc(), out start, out end, out active);
            return end.Ticks;
        }

        [HookMethod("API_GetWeekend")]
        public Dictionary<string, object> API_GetWeekend()
        {
            DateTime start, end;
            bool active;
            Weekend(NowUtc(), out start, out end, out active);
            return new Dictionary<string, object>
            {
                ["active"] = active,
                ["startUtcTicks"] = start.Ticks,
                ["endUtcTicks"] = end.Ticks,
                ["secondsUntilChange"] = SecondsUntilNow(start, end, active),
                ["ruleset"] = active ? settings.PvpRuleset : settings.PveRuleset
            };
        }

        // The cupboard the player is standing in, whether or not they are authorized on it.
        // Authorization still gates damage blocking; this is only the status lookup.
        [HookMethod("API_GetPlayerShield")]
        public Dictionary<string, object> API_GetPlayerShield(BasePlayer player)
        {
            if (player == null) return NoneShield();
            BuildingPrivlidge tc = player.GetBuildingPrivilege();
            if (tc == null) return NoneShield();
            return API_GetCupboardShield(CupboardId(tc));
        }

        [HookMethod("API_GetCupboardShield")]
        public Dictionary<string, object> API_GetCupboardShield(ulong cupboardNetId)
        {
            Shield shield;
            if (cupboardNetId == 0UL || !store.Shields.TryGetValue(cupboardNetId, out shield) || shield == null)
                return NoneShield();
            return Describe(shield);
        }

        [HookMethod("API_GetBuildingShield")]
        public Dictionary<string, object> API_GetBuildingShield(uint buildingId)
        {
            if (buildingId == 0u) return NoneShield();
            DateTime start, end;
            bool active;
            Weekend(NowUtc(), out start, out end, out active);
            Shield best = null;
            double bestSeconds = 0d;
            foreach (var shield in store.Shields.Values)
            {
                if (shield == null || shield.BuildingId != buildingId || shield.ExpiresUtcTicks != end.Ticks) continue;
                Account(shield, DateTime.UtcNow);
                if (shield.RemainingSeconds <= bestSeconds) continue;
                best = shield;
                bestSeconds = shield.RemainingSeconds;
            }
            return best == null ? NoneShield() : Describe(best);
        }

        [HookMethod("API_HasTestClock")]
        public bool API_HasTestClock()
        {
            return fakeUtc.HasValue;
        }

        [HookMethod("API_GetTestClockUtcTicks")]
        public long API_GetTestClockUtcTicks()
        {
            return fakeUtc.HasValue ? fakeUtc.Value.Ticks : 0L;
        }

        [HookMethod("API_SetTestClockUtcTicks")]
        public bool API_SetTestClockUtcTicks(long utcTicks)
        {
            DateTime utc;
            try { utc = new DateTime(utcTicks, DateTimeKind.Utc); }
            catch (ArgumentException) { return false; }
            fakeUtc = utc;
            CheckWindow();
            EnsureRuleset();
            PrintWarning("PvP TEST CLOCK is " + fakeUtc.Value.ToString("u") + " (" + FormatEdge(fakeUtc.Value) + ") via API. TruePVE.json is not changed. The Giant Excavator boost follows this clock. Clear with API_ClearTestClock or nrp.faketime clear.");
            return true;
        }

        [HookMethod("API_ClearTestClock")]
        public bool API_ClearTestClock()
        {
            if (!fakeUtc.HasValue) return false;
            fakeUtc = null;
            CheckWindow();
            EnsureRuleset();
            PrintWarning("PvP test clock cleared via API. The live Eastern window is in use again.");
            return true;
        }

        [HookMethod("API_GetExcavatorStatus")]
        public string API_GetExcavatorStatus()
        {
            return excavatorStatus ?? "excavator not checked";
        }

        #endregion

        // Returns Friday 8 PM through Monday 3 AM Eastern, DST-safe. Outside that window, returns NEXT weekend.
        private void Weekend(DateTime utc, out DateTime startUtc, out DateTime endUtc, out bool active)
        {
            NightshadeWeekendClock.Evaluate(utc, eastern, out startUtc, out endUtc, out active);
        }

        private DateTime ToUtc(DateTime easternWall)
        {
            return NightshadeWeekendClock.ToUtc(easternWall, eastern);
        }

        private DateTime NowUtc() => fakeUtc ?? DateTime.UtcNow;

        private double SecondsUntilNow(DateTime start, DateTime end, bool active)
        {
            return Math.Max(0d, ((active ? end : start) - NowUtc()).TotalSeconds);
        }

        private static ulong ItemUid(Item item) => item == null ? 0UL : item.uid.Value;
        private static ulong CupboardId(BuildingPrivlidge tc) => tc == null || tc.net == null ? 0UL : tc.net.ID.Value;
        private static bool Authorized(BuildingPrivlidge tc, ulong steamId) =>
            tc != null && tc.authorizedPlayers != null && tc.authorizedPlayers.Contains(steamId);

        private static BuildingPrivlidge TargetCupboard(BasePlayer player)
        {
            if (player == null) return null;
            RaycastHit hit;
            if (!Physics.Raycast(player.eyes.HeadRay(), out hit, 5f)) return null;
            if (hit.collider == null) return null;
            return hit.collider.GetComponentInParent<BuildingPrivlidge>();
        }

        private void RedeemCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (args == null || args.Length != 2 || !args[0].Equals("redeem", StringComparison.OrdinalIgnoreCase) || !VoucherSkins.ContainsKey(args[1]))
            {
                Reply(player, "Look at your authorized TC and use /raidvoucher redeem 1h | 24h | weekend");
                return;
            }
            if (!settings.PaidShields) { Reply(player, "Voucher redemption is currently disabled."); return; }
            BuildingPrivlidge tc = TargetCupboard(player);
            if (tc == null || !Authorized(tc, player.userID)) { Reply(player, "Look directly at a TC you are authorized on, within 5 metres."); return; }
            // Validate the carrier AND exact skin. Six-hour and all previous skins are retired.
            ulong requiredSkin = VoucherSkins[args[1]];
            Item voucher = null;
            foreach (var container in new[] { player.inventory.containerMain, player.inventory.containerBelt, player.inventory.containerWear })
            {
                if (container == null) continue;
                voucher = container.itemList.FirstOrDefault(i => i != null && i.info != null &&
                    i.info.shortname == VoucherShortname && i.skin == requiredSkin);
                if (voucher != null) break;
            }
            if (voucher == null) { Reply(player, "You need the matching Nightshade Raid Shield Token in your inventory."); return; }

            DateTime now = DateTime.UtcNow;
            DateTime start, end;
            bool active;
            Weekend(now, out start, out end, out active);
            ulong id = CupboardId(tc);
            Shield existing;
            store.Shields.TryGetValue(id, out existing);
            if (existing != null) Account(existing, now);
            if (existing != null && existing.ExpiresUtcTicks != end.Ticks) existing = null;
            double remainingWeekend = (end - (active ? now : start)).TotalSeconds;
            double newSeconds = args[1].Equals("weekend", StringComparison.OrdinalIgnoreCase) ? remainingWeekend :
                args[1].Equals("24h", StringComparison.OrdinalIgnoreCase) ? 86400d :
                3600d;
            if (existing != null && existing.RemainingSeconds > 0d && args[1].Equals("weekend", StringComparison.OrdinalIgnoreCase))
            { Reply(player, "This TC already has a shield. Use the existing time first; the full-weekend voucher was not consumed."); return; }
            double combined = (existing == null ? 0d : existing.RemainingSeconds) + newSeconds;
            if (combined > Math.Max(1, settings.MaximumStackedHours) * 3600d)
            { Reply(player, "This would exceed the maximum shield time on this TC. Voucher retained."); return; }
            Shield previous = existing;
            Shield shield = new Shield();
            shield.CupboardId = id;
            shield.BuildingId = tc.buildingID;
            shield.ExpiresUtcTicks = end.Ticks;
            shield.LastAccountedTicks = now.Ticks;
            shield.RemainingSeconds = Math.Min(combined, remainingWeekend);
            if (shield.RemainingSeconds <= 0d) { Reply(player, "No PvP weekend shield time is available. Voucher retained."); return; }

            // Persist redemption BEFORE removing the in-game item. On persistence failure voucher stays with buyer.
            store.Shields[id] = shield;
            try { Save(); }
            catch (Exception ex)
            {
                if (previous != null) store.Shields[id] = previous;
                else store.Shields.Remove(id);
                PrintError("Could not save redemption: " + ex.Message);
                Reply(player, "Shield could not be saved; voucher not consumed. Contact an admin.");
                return;
            }
            voucher.UseItem(1);
            PublishShield(shield);
            Reply(player, active ? "Shield active for this PvP weekend. Remaining protection expires Monday 3 AM Eastern; unused hours are lost." :
                "Shield queued for Friday at 8 PM Eastern. It does not protect during PvE weekdays. Any unused time expires Monday 3 AM Eastern.");
            Puts("Redeemed " + args[1] + " for TC " + id + " by " + player.UserIDString);
        }

        private void Account(Shield shield, DateTime now)
        {
            if (shield == null) return;
            if (now.Ticks >= shield.ExpiresUtcTicks) { shield.RemainingSeconds = 0d; shield.LastAccountedTicks = now.Ticks; return; }
            DateTime from = new DateTime(Math.Min(shield.LastAccountedTicks, now.Ticks), DateTimeKind.Utc);
            if (from > now) from = now;
            // Shield hours only drain during the one weekend for which this shield was issued.
            DateTime start, end;
            bool active;
            Weekend(new DateTime(shield.ExpiresUtcTicks, DateTimeKind.Utc).AddSeconds(-1), out start, out end, out active);
            DateTime a = from > start ? from : start;
            DateTime b = now < end ? now : end;
            if (b > a) shield.RemainingSeconds = Math.Max(0d, shield.RemainingSeconds - (b - a).TotalSeconds);
            shield.LastAccountedTicks = now.Ticks;
        }

        // Automatic offline protection is not "the owners are offline" and it is not a raid shield.
        // Every steam id on the TC must be offline for the configured minutes. No record means not protected.
        private bool OfflineProtected(BuildingPrivlidge tc, DateTime now)
        {
            if (!settings.OfflineProtection || tc == null || tc.authorizedPlayers == null || tc.authorizedPlayers.Count == 0)
                return false;
            int grace = EffectiveOfflineGraceMinutes();
            bool any = false;
            ulong owner = tc.OwnerID;
            bool ownerAuthed = false;
            foreach (ulong auth in tc.authorizedPlayers)
            {
                if (!IsSteamId(auth)) continue;
                any = true;
                if (auth == owner) ownerAuthed = true;
                if (!MemberOfflineLongEnough(auth, now, grace)) return false;
            }
            if (!any) return false;
            // The placer can be missing from the auth set. A connected owner is not an offline base.
            if (IsSteamId(owner) && !ownerAuthed && IsConnectedPlayer(owner)) return false;
            return true;
        }

        private bool MemberOfflineLongEnough(ulong id, DateTime now, int grace)
        {
            float sleepSeconds;
            bool sleeping = TrySleepingSeconds(id, out sleepSeconds);
            long ticks;
            bool hasSeen = store.LastSeenUtcTicks.TryGetValue(id, out ticks);
            return NightshadeOfflineGate.OfflineLongEnough(IsConnectedPlayer(id), sleeping, sleepSeconds, hasSeen, ticks, now, grace);
        }

        private static bool IsSteamId(ulong id) => id >= SteamIdMin;

        private static ulong SteamIdOf(BasePlayer player)
        {
            if (player == null) return 0UL;
            ulong id = player.userID;
            return id;
        }

        // Read the steam id out of EncryptedValue before comparing. userID == ulong does not.
        private static bool IsConnectedPlayer(ulong steamId)
        {
            if (!IsSteamId(steamId) || BasePlayer.activePlayerList == null) return false;
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (SteamIdOf(player) == steamId) return true;
            }
            return false;
        }

        // StartSleeping stores UnityEngine.Time.time. -1 means this body is not asleep.
        private static bool TrySleepingSeconds(ulong steamId, out float seconds)
        {
            seconds = 0f;
            if (!IsSteamId(steamId)) return false;
            BasePlayer sleeper = BasePlayer.FindSleeping(steamId);
            if (sleeper == null || sleeper.sleepStartTime < 0f) return false;
            seconds = UnityEngine.Time.time - sleeper.sleepStartTime;
            return true;
        }

        private void NoteSeen(BasePlayer player)
        {
            ulong id = SteamIdOf(player);
            if (!IsSteamId(id)) return;
            store.LastSeenUtcTicks[id] = DateTime.UtcNow.Ticks;
        }

        private void TouchConnectedPlayers()
        {
            if (BasePlayer.activePlayerList == null) return;
            foreach (var player in BasePlayer.activePlayerList)
                NoteSeen(player);
        }

        // The building this entity belongs to. Never the nearest overlapping privilege.
        // A block, door, or cupboard uses its own building id. A loose deployable uses
        // the building it is parented to, then a nearby block of the base it sits on.
        private static uint BuildingIdOf(BaseEntity entity)
        {
            BaseEntity current = entity;
            for (int i = 0; current != null && i < 8; i++)
            {
                DecayEntity decay = current as DecayEntity;
                if (decay != null && decay.buildingID != 0u) return decay.buildingID;
                current = current.GetParentEntity();
            }
            DecayEntity root = entity as DecayEntity;
            if (root != null && !root.IsDestroyed)
            {
                BuildingBlock nearby = root.GetNearbyBuildingBlock();
                if (nearby != null && !nearby.IsDestroyed && nearby.buildingID != 0u)
                    return nearby.buildingID;
            }
            return 0u;
        }

        // Shield stays on the cupboard and building it was redeemed for. A foreign TC is not passed in.
        private bool ShieldBlocks(BuildingPrivlidge tc, uint buildingId, DateTime now, DateTime weekendEnd)
        {
            if (!settings.PaidShields || tc == null) return false;
            Shield shield;
            if (!store.Shields.TryGetValue(CupboardId(tc), out shield) || shield == null || shield.ExpiresUtcTicks != weekendEnd.Ticks)
                return false;
            Account(shield, now);
            bool active = shield.RemainingSeconds > 0d && shield.BuildingId == buildingId;
            if (active) PublishShield(shield);
            return active;
        }

        // One hop from the hit building's own cupboards. A neighbor that only touches
        // another neighbor is not included, so protection cannot walk across the map.
        // includePaidShields is false only for the offline-protection test switch. A weekday
        // force-on must not let a queued paid shield start blocking.
        private NightshadeOfflineGate.HitDecision MatchDirectOverlaps(List<BuildingPrivlidge> own, ulong attacker, bool allowAuthorized, DateTime now, DateTime weekendEnd, bool includePaidShields)
        {
            NightshadeOfflineGate.HitDecision best = new NightshadeOfflineGate.HitDecision();
            if (own == null || own.Count == 0) return best;
            float radius = BuildingPrivlidge.Radius;
            if (radius <= 0f || float.IsNaN(radius)) radius = 16f;
            float reach = radius * 2f;
            List<BuildingPrivlidge> nearby = Facepunch.Pool.Get<List<BuildingPrivlidge>>();
            try
            {
                for (int i = 0; i < own.Count; i++)
                {
                    BuildingPrivlidge hitTc = own[i];
                    if (hitTc == null || hitTc.IsDestroyed || hitTc.transform == null) continue;
                    nearby.Clear();
                    Vis.Entities(hitTc.transform.position, reach, nearby);
                    for (int j = 0; j < nearby.Count; j++)
                    {
                        BuildingPrivlidge source = nearby[j];
                        if (source == null || source.IsDestroyed || source.transform == null) continue;
                        if (source.buildingID == 0u || source.buildingID == hitTc.buildingID) continue;
                        float distance = Vector3.Distance(hitTc.transform.position, source.transform.position);
                        if (!NightshadeOfflineGate.PrivilegeZonesOverlap(distance, radius)) continue;
                        bool ownerOn = IsSteamId(source.OwnerID) && (source.OwnerID == hitTc.OwnerID || Authorized(hitTc, source.OwnerID));
                        if (!NightshadeOfflineGate.SameOwnerOnOverlap(source.OwnerID, SteamIdMin, ownerOn, source.OwnerID == hitTc.OwnerID))
                            continue;
                        bool sourceShield = includePaidShields && ShieldBlocks(source, source.buildingID, now, weekendEnd);
                        bool sourceOffline = !sourceShield && OfflineProtected(source, now);
                        // The overlap cupboard must qualify on its own. An online raider on it blocks the copy.
                        bool overlapOffline = sourceOffline && OfflineProtected(hitTc, now);
                        bool attackerOnSource = attacker != 0UL && (attacker == source.OwnerID || Authorized(source, attacker));
                        NightshadeOfflineGate.OverlapLink link = new NightshadeOfflineGate.OverlapLink();
                        link.ZonesOverlap = true;
                        link.OwnerOnOverlap = true;
                        link.SourceShield = sourceShield;
                        link.SourceOffline = sourceOffline;
                        link.OverlapOffline = overlapOffline;
                        link.AttackerAuthorizedOnSource = attackerOnSource;
                        NightshadeOfflineGate.HitDecision part = NightshadeOfflineGate.MatchedOverlap(true, allowAuthorized, link);
                        if (!part.Block) continue;
                        best.Block = true;
                        if (part.Paid) best.Paid = true;
                        if (best.Paid) return best;
                    }
                }
            }
            finally
            {
                Facepunch.Pool.FreeUnmanaged(ref nearby);
            }
            return best;
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hit)
        {
            if (entity == null || hit == null || hit.damageTypes.Has(Rust.DamageType.Decay)) return null;
            // Exclude player/NPC/animal bodies; protect only buildings and their attached deployables.
            if (entity is BasePlayer) return null;
            DateTime now = DateTime.UtcNow;
            DateTime start, end;
            bool active;
            // Real Eastern clock, same as before. nrp.faketime does not open this gate.
            Weekend(now, out start, out end, out active);
            // forceOfflinePvp opens offline protection only. Paid shields still need the real weekend.
            bool offlineWindow = active || forceOfflinePvp;
            if (!offlineWindow) return null;
            uint buildingId = BuildingIdOf(entity);
            if (buildingId == 0u || BuildingManager.server == null) return null;
            BuildingManager.Building building = BuildingManager.server.GetBuilding(buildingId);
            if (building == null || building.buildingPrivileges == null || building.buildingPrivileges.Count == 0)
                return null;
            ulong attacker = hit.InitiatorPlayer != null ? hit.InitiatorPlayer.userID :
                hit.Initiator != null ? hit.Initiator.OwnerID : 0UL;
            bool allowAuthorized = !settings.ProtectFromAuthorizedPlayers && attacker != 0UL;
            NightshadeOfflineGate.CupboardVote[] votes = new NightshadeOfflineGate.CupboardVote[building.buildingPrivileges.Count];
            List<BuildingPrivlidge> own = Facepunch.Pool.Get<List<BuildingPrivlidge>>();
            NightshadeOfflineGate.HitDecision decision;
            try
            {
                int n = 0;
                foreach (BuildingPrivlidge tc in building.buildingPrivileges)
                {
                    if (n >= votes.Length) break;
                    NightshadeOfflineGate.CupboardVote vote = new NightshadeOfflineGate.CupboardVote();
                    if (tc == null || tc.IsDestroyed)
                    {
                        vote.Destroyed = true;
                    }
                    else
                    {
                        vote.BuildingId = tc.buildingID;
                        vote.AttackerAuthorized = attacker != 0UL && Authorized(tc, attacker);
                        // A foreign building id is recorded and then ignored. It is not read for a shield.
                        if (tc.buildingID == buildingId && !(allowAuthorized && vote.AttackerAuthorized))
                        {
                            if (active)
                                vote.PaidShield = ShieldBlocks(tc, buildingId, now, end);
                            vote.OfflineProtected = OfflineProtected(tc, now);
                            own.Add(tc);
                        }
                        else if (tc.buildingID == buildingId)
                        {
                            own.Add(tc);
                        }
                    }
                    votes[n++] = vote;
                }
                decision = NightshadeOfflineGate.Decide(buildingId, allowAuthorized, votes);
                if (!decision.Block && settings.ShareOverlappingCupboards)
                    decision = MatchDirectOverlaps(own, attacker, allowAuthorized, now, end, active);
            }
            finally
            {
                Facepunch.Pool.FreeUnmanaged(ref own);
            }
            if (!decision.Block) return null;
            float reduction = NightshadeOfflineGate.Reduction(decision.Paid, true, settings.OfflineReductionPercent);
            if (reduction <= 0f) return null;
            hit.damageTypes.ScaleAll(1f - reduction);
            if (settings.NotifyAttacker && hit.InitiatorPlayer != null)
            {
                float last;
                ulong id = hit.InitiatorPlayer.userID;
                if (!notifyThrottle.TryGetValue(id, out last) || Time.realtimeSinceStartup - last > 8f)
                {
                    notifyThrottle[id] = Time.realtimeSinceStartup;
                    string blockedMessage = decision.Paid ? "Nightshade raid shield blocked this hit." : "Offline protection reduced this hit.";
                    Reply(hit.InitiatorPlayer, blockedMessage);
                }
            }
            return null;
        }

        private void StatusCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            DateTime now = NowUtc();
            DateTime start, end;
            bool active;
            Weekend(now, out start, out end, out active);
            if (fakeUtc.HasValue) Reply(player, "Test clock is on. Window checks below are not the live clock. Clear it with nrp.faketime clear.");
            Reply(player, active ? "PvP weekend ends in " + Format(end - now) + "." : "PvP weekend starts in " + Format(start - now) + ".");
            BuildingPrivlidge tc = TargetCupboard(player);
            if (tc == null) tc = player.GetBuildingPrivilege();
            if (tc == null || !Authorized(tc, player.userID)) { Reply(player, "Look at or stand near an authorized TC to inspect its protection."); return; }
            Shield shield;
            DateTime realStart, realEnd;
            bool realActive;
            Weekend(DateTime.UtcNow, out realStart, out realEnd, out realActive);
            if (!store.Shields.TryGetValue(CupboardId(tc), out shield) || shield.ExpiresUtcTicks != realEnd.Ticks)
            {
                Reply(player, "TC paid shield: none for this weekend.");
                ReplyOffline(player, tc);
                return;
            }
            Account(shield, DateTime.UtcNow);
            bool showActive = realActive && shield.RemainingSeconds > 0d;
            Reply(player, (showActive ? "TC raid shield active: " : realActive ? "TC raid shield expired: " : "TC raid shield queued: ") + Format(TimeSpan.FromSeconds(Math.Max(0, shield.RemainingSeconds))) +
                ". Unused time expires Monday 3 AM Eastern.");
            ReplyOffline(player, tc);
        }

        private void ReplyOffline(BasePlayer player, BuildingPrivlidge tc)
        {
            if (!settings.OfflineProtection)
            {
                Reply(player, "Offline protection: off.");
                return;
            }
            int grace = Math.Max(0, EffectiveOfflineGraceMinutes());
            if (OfflineProtected(tc, DateTime.UtcNow))
                Reply(player, "Offline protection: on. Everyone authorized has been offline for at least " + grace + " minutes, so damage is reduced.");
            else
                Reply(player, "Offline protection: not active. An offline base with no raid shield takes normal damage until that time has passed.");
        }

        private static string Format(TimeSpan t) => ((int)Math.Max(0, t.TotalHours)) + "h " + Math.Max(0, t.Minutes) + "m";
        private static void Reply(BasePlayer player, string message) => player.ChatMessage("<color=#b084ff>[Nightshade]</color> " + message);

        #region Window, ruleset, shields

        private void CheckWindow()
        {
            DateTime start, end;
            bool active;
            Weekend(NowUtc(), out start, out end, out active);
            bool changed = windowReady && active != windowActive;
            windowActive = active;
            windowReady = true;
            if (changed)
            {
                RefreshPublished();
                if (active) Interface.CallHook("OnNightshadePvPStarted", start.Ticks, end.Ticks);
                else Interface.CallHook("OnNightshadePvPEnded", start.Ticks, end.Ticks);
                EnsureRuleset();
                RefreshShieldTimer();
                Puts((active ? "PvP window opened. " : "PvP window closed. ") + FormatEdge(start) + " -> " + FormatEdge(end) + (fakeUtc.HasValue ? " (test clock)" : ""));
            }
            else
            {
                RefreshShieldTimer();
            }
            ApplyExcavator();
            ScheduleBoundary();
        }

        private void ScheduleBoundary()
        {
            boundaryTimer?.Destroy();
            DateTime start, end;
            bool active;
            Weekend(NowUtc(), out start, out end, out active);
            // Wake at the edge, and at least daily, so a missed tick still converges.
            double seconds = NightshadeWeekendClock.BoundaryDelaySeconds(NowUtc(), start, end, active);
            boundaryTimer = timer.Once((float)seconds, CheckWindow);
        }

        private void RefreshShieldTimer()
        {
            shieldTimer?.Destroy();
            shieldTimer = null;
            DateTime start, end;
            bool active;
            Weekend(DateTime.UtcNow, out start, out end, out active);
            if (!active) return;
            shieldTimer = timer.Every(30f, () =>
            {
                bool ended = false;
                foreach (var shield in store.Shields.Values.ToList())
                {
                    string before;
                    published.TryGetValue(shield.CupboardId, out before);
                    PublishShield(shield);
                    string after;
                    published.TryGetValue(shield.CupboardId, out after);
                    if (before == "active" && after != "active") ended = true;
                }
                if (ended) Save();
            });
        }

        private void SeedShieldStates()
        {
            suppressHooks = true;
            RefreshPublished();
            suppressHooks = false;
        }

        private void RefreshPublished()
        {
            var seen = new HashSet<ulong>();
            foreach (var shield in store.Shields.Values.ToList())
            {
                if (shield == null) continue;
                seen.Add(shield.CupboardId);
                PublishShield(shield);
            }
            foreach (var id in published.Keys.ToList())
            {
                if (seen.Contains(id)) continue;
                PublishState(id, 0u, "none", 0d);
            }
        }

        private void PublishShield(Shield shield)
        {
            if (shield == null) return;
            var described = Describe(shield);
            PublishState(shield.CupboardId, shield.BuildingId, described["state"] as string, described["seconds"] is double seconds ? seconds : 0d);
        }

        private void PublishState(ulong tcId, uint buildingId, string state, double seconds)
        {
            string old;
            published.TryGetValue(tcId, out old);
            if (old == state) return;
            if (state == "none") published.Remove(tcId);
            else published[tcId] = state;
            if (suppressHooks) return;
            if (state == "active") Interface.CallHook("OnNightshadeShieldStarted", tcId, buildingId, seconds);
            else if (state == "queued") Interface.CallHook("OnNightshadeShieldQueued", tcId, buildingId, seconds);
            else if (old == "active" || old == "queued") Interface.CallHook("OnNightshadeShieldEnded", tcId, buildingId);
        }

        private Dictionary<string, object> Describe(Shield shield)
        {
            if (shield == null) return NoneShield();
            DateTime start, end;
            bool active;
            Weekend(DateTime.UtcNow, out start, out end, out active);
            if (shield.ExpiresUtcTicks != end.Ticks) return NoneShield();
            Account(shield, DateTime.UtcNow);
            double seconds = Math.Max(0d, shield.RemainingSeconds);
            string state = seconds <= 0d ? "none" : active ? "active" : "queued";
            // A test clock changes the label other plugins display, without draining stored time.
            if (fakeUtc.HasValue && state != "none")
            {
                DateTime fakeStart, fakeEnd;
                bool fakeActive;
                Weekend(fakeUtc.Value, out fakeStart, out fakeEnd, out fakeActive);
                if (shield.ExpiresUtcTicks == fakeEnd.Ticks || shield.ExpiresUtcTicks == end.Ticks)
                    state = fakeActive ? "active" : "queued";
            }
            return new Dictionary<string, object>
            {
                ["state"] = state,
                ["seconds"] = seconds,
                ["tcId"] = shield.CupboardId,
                ["buildingId"] = shield.BuildingId
            };
        }

        private static Dictionary<string, object> NoneShield()
        {
            return new Dictionary<string, object>
            {
                ["state"] = "none",
                ["seconds"] = 0d,
                ["tcId"] = 0UL,
                ["buildingId"] = 0u
            };
        }

        // TruePVE's schedule command cannot be disabled cleanly: both "tpve.sched disable"
        // and "tpve sched disable" reach HandleScheduleSet with args ["disable"], which
        // logs "Invalid parameters for command tpve.disable". The schedule is left off
        // in configs/TruePVE.json. This only reads that file and warns once if it is on.
        private void WarnIfTruePveScheduleEnabled()
        {
            if (!settings.DriveTruePve) return;
            string path = Path.Combine(Interface.Oxide.ConfigDirectory, "TruePVE.json");
            try
            {
                if (!File.Exists(path))
                {
                    PrintWarning("TruePVE.json was not found. Leave Schedule.enabled false so TruePVE's clock does not fight the Eastern weekend window.");
                    return;
                }
                JToken enabled = JObject.Parse(File.ReadAllText(path))["Schedule"]?["enabled"];
                if (enabled != null && enabled.Type == JTokenType.Boolean && enabled.Value<bool>())
                    PrintWarning("TruePVE Schedule.enabled is true. Set Schedule.enabled to false in carbon/configs/TruePVE.json and reload TruePVE. NightshadeRaidProtection sets the ruleset; TruePVE's own schedule will override it.");
            }
            catch (Exception ex)
            {
                PrintWarning("Could not read TruePVE's schedule flag: " + ex.Message);
            }
        }

        // The 60s tick is the restart and missed-boundary catch-up. The edge timer is exact
        // when it fires; this still flips the window if that timer was late or the process was down.
        private void OnReconcileTick()
        {
            TouchConnectedPlayers();
            WarnForcedOffline(false);
            DateTime start, end;
            bool active;
            Weekend(NowUtc(), out start, out end, out active);
            if (windowReady && active != windowActive) CheckWindow();
            else
            {
                EnsureRuleset();
                ApplyExcavator();
            }
        }

        private void EnsureRuleset()
        {
            if (applyingRuleset || settings == null || !settings.DriveTruePve) return;
            string purgeRuleset = null;
            bool purgeActive = false;
            // Purge owns the ruleset for its whole span, including a weekend that falls inside a manual purge.
            if (settings.YieldToPurge && PurgeStatus != null && PurgeStatus.IsLoaded)
            {
                purgeRuleset = PurgeStatus.Call("API_GetActiveRulesetOverride") as string;
                purgeActive = !string.IsNullOrEmpty(purgeRuleset);
            }
            DateTime start, end;
            bool active;
            Weekend(NowUtc(), out start, out end, out active);
            string path = Path.Combine(Interface.Oxide.ConfigDirectory, "TruePVE.json");
            NightshadeWeekendClock.TruePveState state = NightshadeWeekendClock.Read(path);
            NightshadeWeekendClock.TogglePlan plan = NightshadeWeekendClock.Decide(state, appliedRuleset, purgeActive, purgeRuleset, active, settings.PvpRuleset, settings.PveRuleset, fakeUtc.HasValue);
            if (plan.Adopt || (!plan.WriteConfig && !plan.ReloadTruePve && !plan.RunRulesetCommand))
            {
                appliedRuleset.Known = true;
                appliedRuleset.Ruleset = plan.Ruleset;
                return;
            }
            bool loaded = TruePVE != null && TruePVE.IsLoaded;
            applyingRuleset = true;
            try
            {
                NightshadeWeekendClock.ApplyResult result = NightshadeWeekendClock.Apply(path, plan, loaded, (cmd, args) =>
                {
                    if (args == null || args.Length == 0) Server.Command(cmd);
                    else Server.Command(cmd, args);
                });
                if (!string.IsNullOrEmpty(result.Error))
                {
                    PrintWarning("TruePVE ruleset was not saved: " + result.Error);
                    return;
                }
                if (plan.RunRulesetCommand && loaded && !result.RanRulesetCommand) return;
                if (plan.ReloadTruePve && loaded && !result.Reloaded) return;
                appliedRuleset.Known = true;
                appliedRuleset.Ruleset = plan.Ruleset;
                if (result.Wrote || result.RanRulesetCommand || result.Reloaded)
                    Puts("TruePVE " + plan.Reason + " -> " + plan.Ruleset
                        + (result.Wrote ? " (wrote Default RuleSet" + (plan.DisableSchedule ? ", schedule off" : "") + ")" : "")
                        + (result.RanRulesetCommand ? " via tpve.ruleset" : "")
                        + (result.Reloaded ? " and reloaded TruePVE" : "")
                        + (fakeUtc.HasValue ? " (test clock)" : "")
                        + ".");
            }
            finally
            {
                applyingRuleset = false;
            }
        }

        private string FormatEdge(DateTime utc)
        {
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), eastern);
            return local.ToString("ddd MMM d h:mm tt", CultureInfo.InvariantCulture) + " ET";
        }

        private void CmdWindow(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAdmin(arg)) return;
            DateTime start, end;
            bool active;
            Weekend(NowUtc(), out start, out end, out active);
            string purge = PurgeStatus != null && PurgeStatus.IsLoaded ? (PurgeStatus.Call("API_GetActiveRulesetOverride") as string ?? "inactive") : "(purge plugin not loaded)";
            bool purgeActive = purge != "inactive" && purge != "(purge plugin not loaded)";
            NightshadeWeekendClock.TruePveState state = NightshadeWeekendClock.Read(Path.Combine(Interface.Oxide.ConfigDirectory, "TruePVE.json"));
            NightshadeWeekendClock.TogglePlan plan = NightshadeWeekendClock.Plan(state, purgeActive, purgeActive ? purge : null, active, settings.PvpRuleset, settings.PveRuleset);
            // CurrentRuleSetName is private and not a HookMethod, so Carbon Call cannot read the live ruleset.
            // The file's Default RuleSet is what TruePVE loads and what Info used to treat as purge.
            arg.ReplyWith("Window " + (active ? "ACTIVE" : "inactive")
                + " | " + FormatEdge(start) + " -> " + FormatEdge(end)
                + " | seconds " + Math.Ceiling(SecondsUntilNow(start, end, active))
                + " | target ruleset " + plan.Ruleset
                + " | file ruleset " + (state.Found ? (string.IsNullOrEmpty(state.Ruleset) ? "(blank)" : state.Ruleset) : "unreadable")
                + " | schedule " + (state.Found && state.ScheduleEnabled ? "ENABLED" : "off")
                + " | purge override " + purge
                + " | zone " + eastern.Id + (usedZoneFallback ? " (fallback)" : "")
                + " | test clock " + (fakeUtc.HasValue ? fakeUtc.Value.ToString("u") : "off")
                + " | " + excavatorStatus);
        }

        private void CmdFakeTime(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAdmin(arg)) return;
            if (arg.Args == null || arg.Args.Length == 0)
            {
                arg.ReplyWith(fakeUtc.HasValue
                    ? "Test clock is " + fakeUtc.Value.ToString("u") + " (" + FormatEdge(fakeUtc.Value) + "). nrp.faketime clear"
                    : "Test clock is off. nrp.faketime 2026-11-02T07:59:00Z   or   nrp.faketime eastern 2026-10-30 20:00");
                return;
            }
            if (arg.Args[0].Equals("clear", StringComparison.OrdinalIgnoreCase) || arg.Args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                fakeUtc = null;
                CheckWindow();
                EnsureRuleset();
                PrintWarning("PvP test clock cleared. The live Eastern window is in use again.");
                arg.ReplyWith("Test clock cleared.");
                return;
            }
            bool easternWall = arg.Args[0].Equals("eastern", StringComparison.OrdinalIgnoreCase) || arg.Args[0].Equals("et", StringComparison.OrdinalIgnoreCase);
            string text = easternWall ? string.Join(" ", arg.Args.Skip(1)) : string.Join(" ", arg.Args);
            DateTime parsed;
            if (easternWall)
            {
                if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed))
                {
                    arg.ReplyWith("Could not parse that Eastern time. Example: nrp.faketime eastern 2026-10-30 20:00");
                    return;
                }
                try { fakeUtc = ToUtc(parsed); }
                catch (Exception ex)
                {
                    arg.ReplyWith("That Eastern time could not be converted: " + ex.Message);
                    return;
                }
            }
            else
            {
                if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
                {
                    arg.ReplyWith("Could not parse that UTC time. Example: nrp.faketime 2026-11-02T07:59:00Z");
                    return;
                }
                fakeUtc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }
            CheckWindow();
            EnsureRuleset();
            PrintWarning("PvP TEST CLOCK is " + fakeUtc.Value.ToString("u") + " (" + FormatEdge(fakeUtc.Value) + "). TruePVE follows it in memory only. TruePVE.json is not changed. Shield drain still uses the real clock. The Giant Excavator boost follows this clock until nrp.faketime clear.");
            arg.ReplyWith("Test clock set to " + fakeUtc.Value.ToString("u") + " (" + FormatEdge(fakeUtc.Value) + ").");
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

        // Offline grace while the test switch is on. Off, or on with no minute argument,
        // this is the configured grace. "nsraid.forcepvp on 1" replaces it until off or reload.
        private int EffectiveOfflineGraceMinutes()
        {
            if (forceOfflinePvp && forceOfflineGraceMinutes.HasValue)
                return forceOfflineGraceMinutes.Value;
            return settings.OfflineGraceMinutes;
        }

        // Server-console warning for as long as the override stays on. Repeated on the
        // existing 60s tick, but not more than once per ForceOfflineWarningSeconds.
        private void WarnForcedOffline(bool immediate)
        {
            if (!forceOfflinePvp) return;
            float now = Time.realtimeSinceStartup;
            if (!immediate && now < nextForceOfflineWarning) return;
            nextForceOfflineWarning = now + ForceOfflineWarningSeconds;
            int grace = EffectiveOfflineGraceMinutes();
            if (grace < 0) grace = 0;
            DateTime start, end;
            bool realActive;
            Weekend(DateTime.UtcNow, out start, out end, out realActive);
            PrintWarning("NightshadeRaidProtection TEST: forced PvP weekend is ON for offline protection only. Effective grace minutes: "
                + grace + ". Real weekend: " + (realActive ? "active" : "inactive")
                + ". TruePVE mode, TruePVE reloads, and Giant Excavator speed are not changed. In memory only. Turn it off with nsraid.forcepvp off.");
        }

        private string ForcePvpStatus()
        {
            DateTime start, end;
            bool realActive;
            // Real clock on purpose. nrp.faketime and the force switch must not change this line.
            Weekend(DateTime.UtcNow, out start, out end, out realActive);
            int grace = EffectiveOfflineGraceMinutes();
            if (grace < 0) grace = 0;
            bool configOn = settings != null && settings.ForcePvpWeekendForTesting;
            return "Force PvP weekend (offline protection only): " + (forceOfflinePvp ? "ON" : "OFF")
                + "\nEffective grace minutes: " + grace
                + "\nReal weekend: " + (realActive ? "active" : "inactive")
                + "\nTruePVE, excavator speed, and API_IsPvPActive are unchanged. Not saved. A reload clears a command override"
                + (configOn
                    ? ". Config \"Force PvP weekend for testing\" is true, so a reload turns it on again."
                    : " (config key is false).");
        }

        // Console, RCON, and in-game admins. Does not write config, TruePVE, or excavator state.
        private string HandleForcePvp(string[] args)
        {
            if (args == null || args.Length == 0 || string.IsNullOrEmpty(args[0]))
                return "Usage: nsraid.forcepvp on [graceMinutes] | off | status    or    /nsraidtest on [graceMinutes] | off | status";
            string action = args[0];
            if (action.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length != 1)
                    return "Usage: nsraid.forcepvp status";
                return ForcePvpStatus();
            }
            if (action.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length != 1)
                    return "Usage: nsraid.forcepvp off";
                bool wasOn = forceOfflinePvp;
                forceOfflinePvp = false;
                forceOfflineGraceMinutes = null;
                nextForceOfflineWarning = 0f;
                if (wasOn)
                {
                    string extra = settings != null && settings.ForcePvpWeekendForTesting
                        ? " Config \"Force PvP weekend for testing\" is still true, so a reload turns this back on."
                        : "";
                    PrintWarning("NightshadeRaidProtection TEST: forced PvP weekend for offline protection is OFF. Offline protection follows the real Eastern weekend again. TruePVE and the Giant Excavator were not changed." + extra);
                }
                return ForcePvpStatus();
            }
            if (!action.Equals("on", StringComparison.OrdinalIgnoreCase))
                return "Usage: nsraid.forcepvp on [graceMinutes] | off | status    or    /nsraidtest on [graceMinutes] | off | status";
            if (args.Length > 2)
                return "Usage: nsraid.forcepvp on [graceMinutes]";
            int? grace = null;
            if (args.Length == 2)
            {
                int minutes;
                if (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out minutes) || minutes < 0 || minutes > MaxForceGraceMinutes)
                    return "Grace minutes must be a whole number from 0 to 10080. Example: nsraid.forcepvp on 1";
                grace = minutes;
            }
            forceOfflinePvp = true;
            forceOfflineGraceMinutes = grace;
            // Do not call CheckWindow, EnsureRuleset, or ApplyExcavator.
            WarnForcedOffline(true);
            return ForcePvpStatus();
        }

        private void CmdForcePvp(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            // Server console and RCON have no in-game player. F1 still needs IsAdmin or the permission.
            if (!(arg.IsRcon || arg.Connection == null || ConsoleAdmin(arg))) return;
            arg.ReplyWith(HandleForcePvp(arg.Args));
        }

        private void CmdNsRaidTest(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, PermAdmin))
            {
                Reply(player, "No permission.");
                return;
            }
            string report = HandleForcePvp(args);
            // One chat line per fact. A single message with newlines is easy to clip in game.
            string[] lines = report.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                Reply(player, lines[i]);
            }
        }

        #endregion

        #region Excavator

        private bool excavatorApplyQueued;

        private void QueueExcavatorApply()
        {
            if (excavatorApplyQueued || excavatorUnloading || excavatorSaveHold) return;
            excavatorApplyQueued = true;
            timer.Once(1f, () =>
            {
                excavatorApplyQueued = false;
                ApplyExcavator();
            });
        }

        private void ApplyExcavator()
        {
            if (excavatorUnloading || excavatorSaveHold) return;
            ApplyExcavatorNow(false, false);
        }

        private void RestoreExcavator()
        {
            ApplyExcavatorNow(true, true);
        }

        private void SuspendExcavatorForSave()
        {
            if (excavatorUnloading || !excavatorBoosted) return;
            excavatorSaveHold = true;
            ApplyExcavatorNow(true, true);
            excavatorSaveTimer?.Destroy();
            excavatorSaveTimer = timer.Once(1f, EndExcavatorSaveHold);
        }

        private void EndExcavatorSaveHold()
        {
            if (excavatorUnloading) return;
            if (SaveRestore.IsSaving)
            {
                excavatorSaveTimer = timer.Once(1f, EndExcavatorSaveHold);
                return;
            }
            excavatorSaveHold = false;
            ApplyExcavatorNow(false, true);
        }

        private void ApplyExcavatorNow(bool forceVanilla, bool quiet)
        {
            if (settings == null) settings = new Settings();
            if (settings.Excavator == null) settings.Excavator = new ExcavatorSettings();
            bool enabled = settings.Excavator.Enabled;
            float multiplier = settings.Excavator.SpeedMultiplier;
            bool keepYield = settings.Excavator.KeepYieldPerDiesel;
            if (!enabled && !forceVanilla && excavatorBaselines.Count == 0)
            {
                excavatorBoosted = false;
                SetExcavatorStatus("excavator disabled", quiet);
                return;
            }
            bool window = !forceVanilla && enabled && windowActive;
            var arms = new List<ExcavatorArm>();
            var engines = new List<DieselEngine>();
            CollectExcavatorParts(arms, engines);
            if (arms.Count == 0)
            {
                excavatorBoosted = false;
                SetExcavatorStatus(enabled ? "excavator enabled, no ExcavatorArm on the map" : "excavator disabled", quiet);
                return;
            }
            var paired = new Dictionary<ulong, List<DieselEngine>>();
            for (int i = 0; i < arms.Count; i++)
            {
                if (arms[i] == null || arms[i].net == null) continue;
                paired[arms[i].net.ID.Value] = new List<DieselEngine>();
            }
            bool anyNamed = false;
            for (int i = 0; i < engines.Count; i++)
                if (ExcavatorNamed(engines[i])) anyNamed = true;
            for (int i = 0; i < engines.Count; i++)
            {
                DieselEngine engine = engines[i];
                ExcavatorArm best = null;
                float bestSqr = float.MaxValue;
                bool named = ExcavatorNamed(engine);
                // A named excavator engine wins. Distance is only the fallback, so a quarry
                // diesel sitting beside the pit is not sped up once the monument engine is known.
                if (anyNamed && !named) continue;
                for (int a = 0; a < arms.Count; a++)
                {
                    ExcavatorArm arm = arms[a];
                    if (arm == null || arm.net == null || arm.transform == null || engine.transform == null) continue;
                    float sqr = (arm.transform.position - engine.transform.position).sqrMagnitude;
                    float limit = NightshadeExcavatorBoost.EnginePairMeters * NightshadeExcavatorBoost.EnginePairMeters;
                    if (!named && sqr > limit) continue;
                    if (sqr < bestSqr)
                    {
                        best = arm;
                        bestSqr = sqr;
                    }
                }
                List<DieselEngine> list;
                if (best != null && paired.TryGetValue(best.net.ID.Value, out list)) list.Add(engine);
            }
            int usedEngines = 0;
            int missingEngines = 0;
            bool anyApplied = false;
            float shownTick = 0f;
            float shownFull = 0f;
            float shownFuel = 0f;
            float shownVanillaFull = 0f;
            float shownVanillaFuel = 0f;
            bool anyMining = false;
            for (int i = 0; i < arms.Count; i++)
            {
                ExcavatorArm arm = arms[i];
                if (arm == null || arm.IsDestroyed || arm.net == null) continue;
                ulong armId = arm.net.ID.Value;
                List<DieselEngine> list;
                if (!paired.TryGetValue(armId, out list) || list == null) list = new List<DieselEngine>();
                DieselEngine first = null;
                for (int e = 0; e < list.Count; e++)
                {
                    if (list[e] != null && !list[e].IsDestroyed) { first = list[e]; break; }
                }
                if (first == null && keepYield && !excavatorBaselines.ContainsKey(armId))
                {
                    missingEngines++;
                    if (!excavatorEngineWarned)
                    {
                        excavatorEngineWarned = true;
                        PrintWarning("Giant Excavator arm has no paired DieselEngine within " + Num(NightshadeExcavatorBoost.EnginePairMeters) + "m and the prefab name does not contain excavator. Production was left unchanged so a faster arm cannot outrun vanilla fuel.");
                    }
                    continue;
                }
                NightshadeExcavatorBoost.Baseline baseline;
                if (!excavatorBaselines.TryGetValue(armId, out baseline))
                {
                    float liveFuel = first != null ? first.runningTimePerFuelUnit : NightshadeExcavatorBoost.VanillaFuelSeconds;
                    baseline = NightshadeExcavatorBoost.Capture(arm.resourceProductionTickRate, arm.timeForFullResources, liveFuel, multiplier, keepYield);
                    excavatorBaselines[armId] = baseline;
                }
                // No paired engine and yield must stay 1x: do not speed the arm.
                bool allowBoost = window && !(keepYield && first == null);
                NightshadeExcavatorBoost.Target target = NightshadeExcavatorBoost.TargetFrom(baseline, allowBoost, true, multiplier, keepYield);
                bool restart = NightshadeExcavatorBoost.ShouldRestartMining(arm.IsMining(), arm.resourceProductionTickRate, target.TickRate);
                arm.timeForFullResources = target.FullResourcesSeconds;
                arm.resourceProductionTickRate = target.TickRate;
                if (restart)
                {
                    arm.StopMining();
                    arm.BeginMining();
                }
                for (int e = 0; e < list.Count; e++)
                {
                    DieselEngine engine = list[e];
                    if (engine == null || engine.IsDestroyed) continue;
                    float live = engine.runningTimePerFuelUnit;
                    if (!NightshadeExcavatorBoost.Near(live, target.FuelSeconds))
                    {
                        engine.cachedFuelTime = NightshadeExcavatorBoost.ScaleFuel(engine.cachedFuelTime, live, target.FuelSeconds);
                        engine.runningTimePerFuelUnit = target.FuelSeconds;
                    }
                    usedEngines++;
                }
                if (first == null) missingEngines++;
                if (arm.IsMining()) anyMining = true;
                if (target.Applied) anyApplied = true;
                shownTick = target.TickRate;
                shownFull = target.FullResourcesSeconds;
                shownFuel = target.FuelSeconds;
                shownVanillaFull = baseline.FullResourcesSeconds;
                shownVanillaFuel = baseline.FuelSeconds;
            }
            excavatorBoosted = anyApplied;
            string text;
            if (excavatorSaveHold)
                text = "excavator paused for save";
            else if (missingEngines > 0 && usedEngines == 0 && keepYield)
                text = "excavator waiting, DieselEngine not found, arm left unchanged so yield stays 1x";
            else if (anyApplied)
                text = "excavator active x" + Num(multiplier)
                    + " tick " + Num(shownTick)
                    + " full " + Num(shownFull)
                    + " fuel " + Num(shownFuel)
                    + " (vanilla full " + Num(shownVanillaFull) + " fuel " + Num(shownVanillaFuel) + ")"
                    + " arms " + arms.Count + " engines " + usedEngines
                    + (missingEngines > 0 ? " missing-engines " + missingEngines : "")
                    + " mining " + (anyMining ? "yes" : "no")
                    + (keepYield ? " yield-kept" : " yield-not-kept");
            else
                text = "excavator vanilla"
                    + " tick " + Num(shownTick)
                    + " full " + Num(shownFull)
                    + " fuel " + Num(shownFuel)
                    + " arms " + arms.Count + " engines " + usedEngines
                    + " mining " + (anyMining ? "yes" : "no");
            SetExcavatorStatus(text, quiet);
        }

        private void SetExcavatorStatus(string text, bool quiet)
        {
            excavatorStatus = text;
            if (quiet || text == excavatorLogSignature) return;
            excavatorLogSignature = text;
            Puts(text + (fakeUtc.HasValue ? " (test clock)" : "") + ".");
        }

        private static void CollectExcavatorParts(List<ExcavatorArm> arms, List<DieselEngine> engines)
        {
            if (BaseNetworkable.serverEntities == null) return;
            foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
            {
                ExcavatorArm arm = entity as ExcavatorArm;
                if (arm != null && !arm.IsDestroyed) arms.Add(arm);
                DieselEngine engine = entity as DieselEngine;
                if (engine != null && !engine.IsDestroyed) engines.Add(engine);
            }
        }

        private static bool ExcavatorNamed(BaseEntity entity)
        {
            BaseEntity current = entity;
            for (int i = 0; current != null && i < 6; i++)
            {
                string name = (current.ShortPrefabName ?? "") + " " + (current.PrefabName ?? "");
                if (name.IndexOf("excavator", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                current = current.GetParentEntity();
            }
            return false;
        }

        private static string Num(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        #endregion
    }
#endif
}
