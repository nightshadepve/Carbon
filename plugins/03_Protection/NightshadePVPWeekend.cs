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

        // The plugin ORs a persisted full-weekend force onto the Eastern window.
        // The protection-only switch is not an input here.
        public static bool EffectiveActive(bool scheduledActive, bool fullForce)
        {
            return scheduledActive || fullForce;
        }

        // Faketime must not rewrite TruePVE.json. A persisted full-weekend force uses
        // the normal write-and-reload path, and only when Decide says the target changed.
        public static bool SkipRulesetFile(bool fakeTime, bool fullForce)
        {
            return fakeTime && !fullForce;
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
                        NightshadeTruePveProfiles.WriteRoot(path, root);
                        result.Wrote = true;
                    }
                    catch (Exception ex)
                    {
                        result.Error = ex.Message;
                    }
                }
            }
            if (command == null || !truePveLoaded) return result;
            // A reload loads Default RuleSet from the file just written, so the in-memory
            // tpve.ruleset command is only needed when the file was already correct.
            // A brand-new ruleset is not in memory until that reload.
            if (plan.ReloadTruePve && result.Wrote)
            {
                command(ReloadCommand, new object[0]);
                result.Reloaded = true;
            }
            else if (plan.RunRulesetCommand && !string.IsNullOrEmpty(plan.Ruleset))
            {
                command(RulesetCommand, new object[] { plan.Ruleset });
                result.RanRulesetCommand = true;
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
            public ulong CupboardId;
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
            public bool FromOverlap;
            public bool ClearedByAuth;
            public ulong CupboardId;
        }

        // Only cupboards attached to the hit building can block on their own.
        // A foreign cupboard cannot cancel that. Overlap coverage is a separate one-hop check.
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
                if (allowAuthorizedDamage && vote.AttackerAuthorized)
                {
                    decision.ClearedByAuth = true;
                    continue;
                }
                // Keep a paid block over a later offline block so the reason stays on the shield.
                if (decision.Block && decision.Paid && !vote.PaidShield) continue;
                decision.Block = true;
                decision.Paid = vote.PaidShield || decision.Paid;
                decision.FromOverlap = false;
                decision.CupboardId = vote.CupboardId;
            }
            return decision;
        }

        // Closest gap between two buildings' block bounds, in metres (OBB.Distance).
        // BaseEntity.GetBuildingPrivilege treats a query as inside a block when that gap
        // is not greater than 16 (the same value as BuildingPrivlidge.Radius). The privilege
        // volume around a block is that 16m cushion. Two volumes intersect when the gap
        // between the blocks is at most twice the cushion. One hop only: callers must not
        // walk from a matched neighbor to its neighbors.
        public static bool PrivilegeZonesOverlap(float closestBlockGapMeters, float cushionMeters)
        {
            if (cushionMeters <= 0f || float.IsNaN(cushionMeters) || float.IsInfinity(cushionMeters)) return false;
            if (float.IsNaN(closestBlockGapMeters) || float.IsInfinity(closestBlockGapMeters) || closestBlockGapMeters < 0f)
                return false;
            return closestBlockGapMeters <= cushionMeters * 2f;
        }

        public static bool SameOwnerOnOverlap(ulong protectedOwnerId, ulong steamIdMinimum, bool authorizedOnOverlap, bool isOverlapOwner)
        {
            if (protectedOwnerId < steamIdMinimum) return false;
            return authorizedOnOverlap || isOverlapOwner;
        }

        // One hop from a protected cupboard to a neighbor whose privilege sphere overlaps it.
        // Paid shields copy when the buyer is on the neighbor's auth list right now.
        // Same owner id without that auth entry does not count, and a neighbor that fails
        // the check cannot clear a shield that already applies.
        // Offline protection still needs the owner link, and the neighbor must qualify on
        // its own so someone online there does not inherit the grace.
        public struct OverlapLink
        {
            public bool ZonesOverlap;
            public bool OwnerOnOverlap;
            public bool BuyerAuthedOnHit;
            public bool SourceShield;
            public bool SourceOffline;
            public bool OverlapOffline;
            public bool AttackerAuthorizedOnSource;
            public bool AttackerAuthorizedOnHit;
        }

        public static HitDecision MatchedOverlap(bool share, bool allowAuthorizedDamage, OverlapLink link)
        {
            HitDecision decision = new HitDecision();
            if (!share || !link.ZonesOverlap) return decision;
            if (link.SourceShield && link.BuyerAuthedOnHit)
            {
                if (allowAuthorizedDamage && (link.AttackerAuthorizedOnSource || link.AttackerAuthorizedOnHit))
                {
                    decision.ClearedByAuth = true;
                    return decision;
                }
                decision.Block = true;
                decision.Paid = true;
                decision.FromOverlap = true;
                return decision;
            }
            if (link.OwnerOnOverlap && link.SourceOffline && link.OverlapOffline)
            {
                decision.Block = true;
                decision.Paid = false;
                decision.FromOverlap = true;
            }
            return decision;
        }
    }

    // Which building a container is protected with.
    // An attached entity uses its own building id only.
    // A loose player container uses every cupboard privilege zone that contains it,
    // and each of those cupboards' own building ids. The nearest cupboard is not preferred.
    public static class NightshadeRaidTargets
    {
        public struct ZoneHit
        {
            public uint BuildingId;
            public float DistanceMeters;
            public float RadiusMeters;
        }

        public static uint[] BuildingsForContainer(uint attachedBuildingId, bool loosePlayerContainer, ZoneHit[] zones)
        {
            if (attachedBuildingId != 0u)
                return new[] { attachedBuildingId };
            if (!loosePlayerContainer || zones == null || zones.Length == 0)
                return new uint[0];
            var list = new List<uint>();
            for (int i = 0; i < zones.Length; i++)
            {
                ZoneHit zone = zones[i];
                if (zone.BuildingId == 0u) continue;
                if (zone.RadiusMeters <= 0f || float.IsNaN(zone.RadiusMeters) || float.IsInfinity(zone.RadiusMeters)) continue;
                if (float.IsNaN(zone.DistanceMeters) || float.IsInfinity(zone.DistanceMeters) || zone.DistanceMeters < 0f) continue;
                if (zone.DistanceMeters > zone.RadiusMeters) continue;
                bool seen = false;
                for (int j = 0; j < list.Count; j++)
                {
                    if (list[j] != zone.BuildingId) continue;
                    seen = true;
                    break;
                }
                if (!seen) list.Add(zone.BuildingId);
            }
            return list.ToArray();
        }
    }

    // Paid shield time. Seconds drain only while the real Eastern weekend is open,
    // and only from the front of the queue. A test clock and a forced weekend do not
    // reach this helper: callers pass the real UTC clock.
    public static class NightshadeShieldTime
    {
        public struct Slice
        {
            public ulong BuyerId;
            public double RemainingSeconds;
        }

        public static double Total(Slice[] slices, int count)
        {
            if (slices == null || count <= 0) return 0d;
            if (count > slices.Length) count = slices.Length;
            double total = 0d;
            for (int i = 0; i < count; i++)
            {
                double seconds = slices[i].RemainingSeconds;
                if (seconds > 0d && !double.IsNaN(seconds) && !double.IsInfinity(seconds)) total += seconds;
            }
            return total;
        }

        // Remove seconds from the front. Spent slices are dropped. Order of the rest is kept.
        public static int Consume(Slice[] slices, int count, double seconds)
        {
            if (slices == null || count <= 0) return 0;
            if (count > slices.Length) count = slices.Length;
            if (seconds > 0d && !double.IsNaN(seconds) && !double.IsInfinity(seconds))
            {
                for (int i = 0; i < count && seconds > 0d; i++)
                {
                    double have = slices[i].RemainingSeconds;
                    if (have <= 0d || double.IsNaN(have) || double.IsInfinity(have))
                    {
                        slices[i].RemainingSeconds = 0d;
                        continue;
                    }
                    double take = have < seconds ? have : seconds;
                    slices[i].RemainingSeconds = have - take;
                    seconds -= take;
                }
            }
            int write = 0;
            for (int i = 0; i < count; i++)
            {
                if (slices[i].RemainingSeconds <= 0.0001d) continue;
                slices[write++] = slices[i];
            }
            return write;
        }

        // Seconds of [from, now) that sit inside Friday 20:00 through Monday 03:00 Eastern.
        public static double WeekendOverlapSeconds(DateTime fromUtc, DateTime nowUtc, TimeZoneInfo zone)
        {
            if (zone == null) zone = TimeZoneInfo.Utc;
            DateTime cursor = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
            DateTime endAt = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
            if (endAt <= cursor) return 0d;
            double total = 0d;
            for (int i = 0; i < 64 && cursor < endAt; i++)
            {
                DateTime start;
                DateTime end;
                bool active;
                NightshadeWeekendClock.Evaluate(cursor, zone, out start, out end, out active);
                if (active)
                {
                    DateTime sliceEnd = endAt < end ? endAt : end;
                    if (sliceEnd > cursor) total += (sliceEnd - cursor).TotalSeconds;
                    if (end <= cursor) break;
                    cursor = end;
                    continue;
                }
                if (start <= cursor || start >= endAt) break;
                cursor = start;
            }
            return total;
        }

        // Records saved before the queue stored an expiry. A past expiry is already spent.
        // No expiry, or an expiry still in the future, keeps the stored seconds.
        public static bool LegacyStillRunning(long expiresUtcTicks, double remainingSeconds, DateTime nowUtc)
        {
            if (remainingSeconds <= 0d || double.IsNaN(remainingSeconds) || double.IsInfinity(remainingSeconds)) return false;
            if (expiresUtcTicks <= 0L) return true;
            return expiresUtcTicks > nowUtc.Ticks;
        }
    }

    // Player voucher names. The shop sells paper tokens; the skin id is the tier.
    // Current shop skins (data/Nightshade/NightshadeShop/Shop.json):
    // 6h 3843239199, 1d 3843240692, weekend 3843241106.
    // The previous shop generation stays redeemable:
    // 6h 3843173236, 1d 3843173835, weekend 3843174471.
    // Older papers stay redeemable. Skin 3810298504 named like "1 Hour" is 1 hour.
    // Any other name on that skin is 6 hours. Skin 3810324315 is 1 day (24 hours).
    // Skin 3810324938 is a weekend. 24h is an old name for the 1d command.
    // Weekend length is the Eastern window (end minus start) unless a positive
    // config hour value is passed in.
    public static class NightshadeVoucherTiers
    {
        public const string SixHours = "6h";
        public const string OneDay = "1d";
        public const string Weekend = "weekend";

        public const ulong SkinSixHours = 3843239199UL;
        public const ulong SkinOneDay = 3843240692UL;
        public const ulong SkinWeekend = 3843241106UL;
        public const ulong PriorSkinSixHours = 3843173236UL;
        public const ulong PriorSkinOneDay = 3843173835UL;
        public const ulong PriorSkinWeekend = 3843174471UL;
        public const ulong LegacySkinShort = 3810298504UL;
        public const ulong LegacySkinDay = 3810324315UL;
        public const ulong LegacySkinWeekend = 3810324938UL;

        public const string OneHour = "1h";
        public const double SixHourSeconds = 21600d;
        public const double OneHourSeconds = 3600d;
        public const double OneDaySeconds = 86400d;

        public struct Resolved
        {
            public bool Known;
            public string Tier;
            public string Input;
            public ulong Skin;
            public bool FullWeekend;
            public double Seconds;
            public bool LegacyName;
        }

        public static bool TryResolve(string token, out Resolved tier)
        {
            tier = new Resolved();
            if (string.IsNullOrEmpty(token)) return false;
            string key = token.Trim().ToLowerInvariant();
            if (key.Length == 0) return false;
            if (IsLegacyHour(key))
            {
                tier = Fixed(OneHour, key, LegacySkinShort, OneHourSeconds, key != OneHour);
                return true;
            }
            if (IsSix(key))
            {
                tier = Fixed(SixHours, key, SkinSixHours, SixHourSeconds, key != SixHours);
                return true;
            }
            if (IsDay(key))
            {
                tier = Fixed(OneDay, key, SkinOneDay, OneDaySeconds, key != OneDay);
                return true;
            }
            if (key == Weekend)
            {
                tier = Fixed(Weekend, key, SkinWeekend, 0d, false);
                tier.FullWeekend = true;
                return true;
            }
            return false;
        }

        // Old shop items are named "Nightshade Raid Shield - 1 Hour" on LegacySkinShort.
        public static bool LegacyOneHourName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string text = name.ToLowerInvariant();
            return text.Contains("1 hour") || text.Contains("1hour") || text.Contains("one hour");
        }

        // Current and previous shop skins match on id alone. The oldest short skin still uses the item name.
        public static bool Matches(ulong skin, string itemName, string tier)
        {
            if (tier == SixHours)
            {
                if (skin == SkinSixHours || skin == PriorSkinSixHours) return true;
                return skin == LegacySkinShort && !LegacyOneHourName(itemName);
            }
            if (tier == OneHour)
                return skin == LegacySkinShort && LegacyOneHourName(itemName);
            if (tier == OneDay)
                return skin == SkinOneDay || skin == PriorSkinOneDay || skin == LegacySkinDay;
            if (tier == Weekend)
                return skin == SkinWeekend || skin == PriorSkinWeekend || skin == LegacySkinWeekend;
            return false;
        }

        public static double WindowSeconds(DateTime startUtc, DateTime endUtc)
        {
            double seconds = (endUtc - startUtc).TotalSeconds;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0d) return 0d;
            return seconds;
        }

        // configuredHours applies only to the weekend tier. Zero or less uses the window.
        public static double SecondsFor(Resolved tier, DateTime startUtc, DateTime endUtc, double configuredHours)
        {
            if (!tier.Known) return 0d;
            if (!tier.FullWeekend) return tier.Seconds;
            if (!double.IsNaN(configuredHours) && !double.IsInfinity(configuredHours) && configuredHours > 0d)
                return configuredHours * 3600d;
            return WindowSeconds(startUtc, endUtc);
        }

        public static string Validate()
        {
            Resolved tier;
            if (!TryResolve("6h", out tier) || tier.Tier != SixHours || tier.LegacyName || tier.Seconds != SixHourSeconds || tier.Skin != SkinSixHours)
                return "6h";
            if (!TryResolve("1h", out tier) || tier.Tier != OneHour || tier.LegacyName || tier.Skin != LegacySkinShort || tier.Seconds != OneHourSeconds)
                return "1h";
            if (!TryResolve("1hour", out tier) || tier.Tier != OneHour || !tier.LegacyName || tier.Seconds != OneHourSeconds)
                return "1hour";
            if (!TryResolve("1d", out tier) || tier.Tier != OneDay || tier.LegacyName || tier.Seconds != OneDaySeconds || tier.Skin != SkinOneDay || tier.FullWeekend)
                return "1d";
            if (!TryResolve("24h", out tier) || tier.Tier != OneDay || !tier.LegacyName || tier.Seconds != OneDaySeconds)
                return "24h";
            if (!TryResolve("24hours", out tier) || tier.Tier != OneDay)
                return "24hours";
            if (!TryResolve("weekend", out tier) || tier.Tier != Weekend || !tier.FullWeekend || tier.Skin != SkinWeekend || tier.LegacyName)
                return "weekend";
            if (TryResolve("", out tier) || TryResolve("2h", out tier) || TryResolve("redeem", out tier) || TryResolve("7d", out tier))
                return "reject";
            if (WindowSeconds(new DateTime(2026, 11, 6, 1, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 8, 8, 0, 0, DateTimeKind.Utc)) != 55d * 3600d)
                return "window";
            DateTime sample = new DateTime(2026, 11, 6, 1, 0, 0, DateTimeKind.Utc);
            if (WindowSeconds(sample, sample) != 0d)
                return "empty-window";
            Resolved weekend;
            TryResolve("weekend", out weekend);
            if (SecondsFor(weekend, sample, sample.AddHours(55), 0d) != 55d * 3600d)
                return "derived";
            if (SecondsFor(weekend, sample, sample.AddHours(55), 40d) != 40d * 3600d)
                return "configured";
            Resolved six;
            TryResolve("6h", out six);
            if (SecondsFor(six, sample, sample.AddHours(55), 40d) != SixHourSeconds)
                return "fixed";
            if (LegacyOneHourName("Nightshade Raid Shield - 1 Hour") == false) return "legacy-name";
            if (LegacyOneHourName("Nightshade Raid Shield - 6 Hours")) return "six-name";
            if (LegacyOneHourName("Nightshade Raid Shield - 10 Hours")) return "ten-name";
            if (LegacyOneHourName(null) || LegacyOneHourName("")) return "blank-name";
            if (!Matches(SkinSixHours, "Nightshade Raid Shield - 1 Hour", SixHours)) return "new-6h-name";
            if (Matches(SkinSixHours, "Nightshade Raid Shield - 1 Hour", OneHour)) return "new-6h-not-1h";
            if (!Matches(SkinSixHours, null, SixHours)) return "new-6h-blank";
            if (!Matches(LegacySkinShort, "Nightshade Raid Shield - 1 Hour", OneHour)) return "old-1h";
            if (Matches(LegacySkinShort, "Nightshade Raid Shield - 1 Hour", SixHours)) return "old-1h-not-6h";
            if (!Matches(LegacySkinShort, "Nightshade Raid Shield - 6 Hours", SixHours)) return "old-6h";
            if (Matches(LegacySkinShort, "Nightshade Raid Shield - 6 Hours", OneHour)) return "old-6h-not-1h";
            if (!Matches(LegacySkinShort, null, SixHours)) return "old-blank-6h";
            if (!Matches(SkinOneDay, null, OneDay) || !Matches(LegacySkinDay, "Nightshade Raid Shield - 24 Hours", OneDay)) return "day-skins";
            if (Matches(SkinOneDay, null, SixHours) || Matches(LegacySkinDay, null, Weekend)) return "day-cross";
            if (!Matches(SkinWeekend, null, Weekend) || !Matches(LegacySkinWeekend, null, Weekend)) return "weekend-skins";
            if (Matches(SkinWeekend, "1 Hour", OneHour)) return "weekend-name";
            if (!Matches(SkinWeekend, "", Weekend) || !Matches(PriorSkinWeekend, "Nightshade Raid Shield - Full Weekend", Weekend)) return "weekend-names";
            if (Matches(SkinWeekend, "", SixHours) || Matches(SkinWeekend, "", OneDay) || Matches(SkinWeekend, "", OneHour)) return "weekend-cross";
            if (Matches(PriorSkinWeekend, null, SixHours) || Matches(PriorSkinWeekend, null, OneDay)) return "prior-weekend-cross";
            if (!Matches(SkinSixHours, "", SixHours) || !Matches(PriorSkinSixHours, null, SixHours)) return "six-generations";
            if (Matches(SkinSixHours, "", OneDay) || Matches(PriorSkinSixHours, "1 Hour", OneHour)) return "six-cross";
            if (!Matches(SkinOneDay, "", OneDay) || !Matches(PriorSkinOneDay, null, OneDay)) return "day-generations";
            if (Matches(SkinOneDay, "", Weekend) || Matches(PriorSkinOneDay, null, SixHours)) return "day-generations-cross";
            if (Matches(0UL, null, Weekend) || Matches(0UL, "", SixHours)) return "blank-skin";
            return null;
        }

        private static bool IsLegacyHour(string key)
        {
            return key == "1h" || key == "1hr" || key == "1hrs" || key == "1hour" || key == "1hours";
        }

        private static bool IsSix(string key)
        {
            return key == "6h" || key == "6hr" || key == "6hrs" || key == "6hour" || key == "6hours";
        }

        private static bool IsDay(string key)
        {
            return key == "1d" || key == "1day" || key == "24h" || key == "24hr" || key == "24hrs"
                || key == "24hour" || key == "24hours";
        }

        private static Resolved Fixed(string name, string input, ulong skin, double seconds, bool legacy)
        {
            Resolved tier = new Resolved();
            tier.Known = true;
            tier.Tier = name;
            tier.Input = input;
            tier.Skin = skin;
            tier.FullWeekend = false;
            tier.Seconds = seconds;
            tier.LegacyName = legacy;
            return tier;
        }
    }


    // Owned TruePVE rulesets.
    // TruePVE 2.4.4 switches the active ruleset in memory with "tpve.ruleset <name>".
    // Its Schedule, when enabled, also switches currentRuleSet on a timer (every 30s
    // when useRealtime is true). That clock is DateTime.Now, the server's local time,
    // not America/New_York. It cannot yield to a purge, the test clock, or the
    // full-weekend force, and a running schedule would undo tpve.ruleset.
    // AddOrUpdateMapping only stores a zone key in TruePVE's data file, and the
    // ruleset must already exist in config. This plugin keeps Schedule.enabled false,
    // writes the two ruleset bodies below into TruePVE's RuleSets and Mappings when
    // they differ, and selects the active one by Default RuleSet plus tpve.ruleset.
    public static class NightshadeTruePveProfiles
    {
        public const string WeekdayName = "nightshade_pve_weekday";
        public const string WeekendName = "nightshade_pvp_weekend";
        public const string WeekdayFlags = "NoHeliDamage, HopperCannotTargetEnemyLoot, AuthorizedFarmableDamage, CupboardOwnership, HumanNPCDamage, LockedBoxesImmortal, LockedDoorsImmortal, LockedVehiclesImmortal";

        public struct Spec
        {
            public string Name;
            public bool Enabled;
            public bool DefaultAllowDamage;
            public string Flags;
            public List<string> Rules;
        }

        public static Spec Weekday()
        {
            return new Spec
            {
                Name = WeekdayName,
                Enabled = true,
                DefaultAllowDamage = false,
                Flags = WeekdayFlags,
                Rules = new List<string>(WeekdayRules)
            };
        }

        public static Spec Weekend()
        {
            return new Spec
            {
                Name = WeekendName,
                Enabled = true,
                DefaultAllowDamage = true,
                Flags = "",
                Rules = new List<string> { "anything can hurt anything" }
            };
        }

        // True when a ruleset body or its name mapping changed. Every other TruePVE key stays.
        public static bool Merge(JObject root, Spec weekday, Spec weekend)
        {
            if (root == null) return false;
            bool changed = false;
            if (Upsert(root, weekday)) changed = true;
            if (Upsert(root, weekend)) changed = true;
            return changed;
        }

        public static void WriteRoot(string path, JObject root)
        {
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
        }

        private static bool Upsert(JObject root, Spec spec)
        {
            if (root == null || string.IsNullOrEmpty(spec.Name) || spec.Rules == null || spec.Rules.Count == 0) return false;
            bool changed = false;
            JArray sets = root["RuleSets"] as JArray;
            if (sets == null)
            {
                sets = new JArray();
                root["RuleSets"] = sets;
                changed = true;
            }
            JObject found = null;
            for (int i = 0; i < sets.Count; i++)
            {
                JObject entry = sets[i] as JObject;
                if (entry == null) continue;
                JToken name = entry["name"];
                if (name != null && name.Type == JTokenType.String && string.Equals((string)name, spec.Name, StringComparison.Ordinal))
                {
                    found = entry;
                    break;
                }
            }
            if (found == null)
            {
                found = new JObject();
                sets.Add(found);
                changed = true;
            }
            if (!SameString(found["name"], spec.Name)) { found["name"] = spec.Name; changed = true; }
            if (!SameBool(found["enabled"], spec.Enabled)) { found["enabled"] = spec.Enabled; changed = true; }
            if (!SameBool(found["defaultAllowDamage"], spec.DefaultAllowDamage)) { found["defaultAllowDamage"] = spec.DefaultAllowDamage; changed = true; }
            string flags = spec.Flags ?? "";
            if (!SameString(found["flags"], flags)) { found["flags"] = flags; changed = true; }
            if (!SameRules(found["rules"] as JArray, spec.Rules))
            {
                JArray rules = new JArray();
                for (int i = 0; i < spec.Rules.Count; i++) rules.Add(spec.Rules[i] ?? "");
                found["rules"] = rules;
                changed = true;
            }
            JObject mappings = root["Mappings"] as JObject;
            if (mappings == null)
            {
                mappings = new JObject();
                root["Mappings"] = mappings;
                changed = true;
            }
            if (!SameString(mappings[spec.Name], spec.Name))
            {
                mappings[spec.Name] = spec.Name;
                changed = true;
            }
            return changed;
        }

        private static bool SameString(JToken token, string value)
        {
            return token != null && token.Type == JTokenType.String && string.Equals((string)token, value ?? "", StringComparison.Ordinal);
        }

        private static bool SameBool(JToken token, bool value)
        {
            return token != null && token.Type == JTokenType.Boolean && token.Value<bool>() == value;
        }

        private static bool SameRules(JArray current, List<string> wanted)
        {
            if (current == null || wanted == null || current.Count != wanted.Count) return false;
            HashSet<string> have = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < current.Count; i++)
            {
                JToken token = current[i];
                if (token == null || token.Type != JTokenType.String) return false;
                have.Add((string)token);
            }
            if (have.Count != wanted.Count) return false;
            for (int i = 0; i < wanted.Count; i++)
            {
                if (!have.Contains(wanted[i] ?? "")) return false;
            }
            return true;
        }

        private static readonly string[] WeekdayRules = new string[]
        {
            "base_defenses can hurt Heli",
            "players can hurt trees",
            "nothing can hurt base_external_defenses",
            "nothing can hurt base_barricades",
            "nothing can hurt base_defenses",
            "nothing can hurt siege",
            "nothing can hurt base_structure",
            "nothing can hurt base_deployables",
            "entities cannot hurt base_deployables",
            "npcs cannot hurt base_deployables",
            "guards cannot hurt base_deployables",
            "heli cannot hurt base_deployables",
            "heliturrets cannot hurt base_deployables",
            "bradley cannot hurt base_deployables",
            "fire cannot hurt base_deployables",
            "mlrs cannot hurt base_deployables",
            "siege cannot hurt base_deployables",
            "npcs cannot hurt base_structure",
            "npcs cannot hurt base_external_defenses",
            "npcs cannot hurt base_defenses",
            "npcs cannot hurt base_barricades",
            "guards cannot hurt base_structure",
            "guards cannot hurt base_external_defenses",
            "guards cannot hurt base_defenses",
            "guards cannot hurt base_barricades",
            "heli cannot hurt base_structure",
            "heli cannot hurt base_external_defenses",
            "heli cannot hurt base_defenses",
            "heli cannot hurt base_barricades",
            "heliturrets cannot hurt base_structure",
            "heliturrets cannot hurt base_external_defenses",
            "heliturrets cannot hurt base_defenses",
            "heliturrets cannot hurt base_barricades",
            "bradley cannot hurt base_structure",
            "bradley cannot hurt base_external_defenses",
            "bradley cannot hurt base_defenses",
            "bradley cannot hurt base_barricades",
            "fire cannot hurt base_structure",
            "fire cannot hurt base_external_defenses",
            "fire cannot hurt base_defenses",
            "fire cannot hurt base_barricades",
            "mlrs cannot hurt base_structure",
            "mlrs cannot hurt base_external_defenses",
            "mlrs cannot hurt base_defenses",
            "mlrs cannot hurt base_barricades",
            "siege cannot hurt base_structure",
            "siege cannot hurt base_external_defenses",
            "siege cannot hurt base_defenses",
            "siege cannot hurt base_barricades",
            "heli can hurt players",
            "heliturrets can hurt players",
            "bradley can hurt players",
            "guards can hurt players",
            "fire can hurt players",
            "traps can hurt players",
            "npcs can hurt players",
            "animals can hurt players",
            "farm can hurt players",
            "bees can hurt players",
            "players can hurt bradley",
            "players can hurt twig",
            "players can hurt resources",
            "anything can hurt dispensers",
            "anything can hurt heli",
            "anything can hurt npcs",
            "nothing can hurt ch47",
            "players cannot hurt base_structure",
            "players cannot hurt cars",
            "nothing can hurt mini",
            "nothing can hurt snowmobiles",
            "players can hurt ridablehorses",
            "cars cannot hurt anything",
            "mini cannot hurt anything",
            "ch47 cannot hurt anything",
            "scrapheli cannot hurt anything",
            "players cannot hurt base_defenses",
            "players cannot hurt farm",
            "players cannot hurt players",
            "players cannot hurt tugboats",
            "base_defenses can hurt players",
            "base_external_defenses can hurt players",
            "base_barricades can hurt players",
            "mini cannot hurt mini",
            "junkyard cannot hurt anything",
            "junkyard can hurt cars",
            "ramhead can hurt ramhead",
            "siege can hurt players",
        };
    }

    // Player-vs-player combat for the teleport block. NPCs, self hits, zero damage,
    // and (when configured) teammates do not start the timer.
    public static class NightshadeCombatGate
    {
        public const string DefaultMessage = "Combat lock: teleport blocked for {seconds}s.";

        public static bool Tag(bool enabled, bool onlyWeekend, bool weekendActive, bool selfHit, bool realAttacker, bool realVictim, bool teammates, bool ignoreTeammates, float damage)
        {
            if (!enabled || damage <= 0f) return false;
            if (onlyWeekend && !weekendActive) return false;
            if (selfHit || !realAttacker || !realVictim) return false;
            if (ignoreTeammates && teammates) return false;
            return true;
        }
    }

#if !NIGHTSHADE_RAID_TESTS
    [Info("NightshadePVPWeekend", "Nightshade", "2.0.0")]
    [Description("Eastern-time PvP weekend, owned TruePVE rulesets, TC raid shields, offline protection, the Giant Excavator boost, and a combat teleport block.")]
    public class NightshadePVPWeekend : RustPlugin
    {
        private const string PermAdmin = "nightshadepvpweekend.admin";
        // Not granted to any user or group. IsAdmin does not bypass the combat teleport block.
        private const string PermBypassCombat = "nightshadepvpweekend.bypasscombatblock";
        private const string DataPath = "Nightshade/NightshadePVPWeekend/RaidProtection";
        private const string LegacyDataDirName = "NightshadeRaidProtection";
        private const ulong SteamIdMin = 76561197960265728UL;
        // Shop purchases are PAPER items. The skin id is the tier. See NightshadeVoucherTiers.
        private const string VoucherShortname = "paper";

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
        // While on, the offline-protection damage decision treats the PvP weekend as active.
        // It does not call CheckWindow, EnsureRuleset, or ApplyExcavator.
        // TruePVE rulesets and reloads, Giant Excavator speed, and API_IsPvPActive
        // are unchanged by this switch. nspvp.faketime still affects only the paths it already did.
        // Paid shields are not gated on this switch. A shield with time left blocks raid damage
        // on the real clock whether or not the weekend, this switch, or the full-weekend force is on.
        private bool forceOfflinePvp;
        private int? forceOfflineGraceMinutes;
        private float nextForceOfflineWarning;
        private const int MaxForceGraceMinutes = 10080;
        private const float ForceOfflineWarningSeconds = 300f;

        // Persisted full-server weekend. While on, the scheduled window is treated as open
        // everywhere the real weekend is: TruePVE (existing Decide/Apply path, reload only
        // when the target changes), excavator boost, offline protection, API_IsPvPActive,
        // and the HUD/info listeners of that API. Paid-shield time still drains only on the
        // real Eastern weekend. nspvp.forcepvp on does not flip this. nspvp.pvpweekend does.
        private bool forceFullPvp;
        private float nextFullForceWarning;

        // Admin hit log. Memory only. At most one line per entity every two seconds.
        private bool debugHits;
        private readonly Dictionary<ulong, float> debugNext = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, float> combatUntil = new Dictionary<ulong, float>();
        private int scaleFrame = -1;
        private ulong scaleEntity;
        private ulong scaleAttacker;
        private ScaleResult scaleCached;

        private class Settings
        {
            [JsonProperty("Enable paid TC raid shields")] public bool PaidShields = true;
            [JsonProperty("Enable automatic offline raid protection during PvP")] public bool OfflineProtection = true;
            [JsonProperty("Minutes everyone authorized on TC must be offline before auto protection")] public int OfflineGraceMinutes = 30;
            // Dev-server opt-in. False by default and not flipped by nspvp.forcepvp. A true value
            // enables the same in-memory offline-protection override after each load. It still does
            // not switch TruePVE or the excavator.
            [JsonProperty("Force PvP weekend for testing")] public bool ForcePvpWeekendForTesting = false;
            // Dev-server opt-in. False by default. nspvp.pvpweekend on|off saves this.
            // A true value makes the whole server follow the PvP weekend until it is turned off.
            [JsonProperty("Force full PvP weekend")] public bool ForceFullPvpWeekend = false;
            [JsonProperty("Offline protection damage reduction percent (100 = fully block)")] public int OfflineReductionPercent = 100;
            [JsonProperty("Prevent teammates authorized on TC from damaging their own shielded building")] public bool ProtectFromAuthorizedPlayers = false;
            // One switch for both protections. Paid shields cover a neighbor only while one
            // of that shield's buyers is on the neighbor's auth list. Offline protection uses
            // the same one-hop privilege overlap (block bounds, not a 32m TC-center sphere).
            [JsonProperty("Share protection with the owner's directly overlapping cupboards")] public bool ShareOverlappingCupboards = true;
            // Map wipe destroys every TC. Shields are cleared with the wipe unless this is on.
            [JsonProperty("Keep paid shields across a map wipe")] public bool KeepPaidShieldsOnWipe = false;
            [JsonProperty("Show blocked-hit message to attacker (rate limited)")] public bool NotifyAttacker = true;
            [JsonProperty("Maximum total paid shield time on one TC in hours")] public int MaximumStackedHours = 72;
            // 0 uses the Eastern window length (Friday 8 PM through Monday 3 AM, about 55 hours).
            // A positive value is the weekend token's shield hours instead of that window.
            [JsonProperty("Weekend voucher hours (0 = full Eastern window)")] public double WeekendVoucherHours = 0d;
            [JsonProperty("Drive TruePVE from the Eastern weekend window")] public bool DriveTruePve = true;
            [JsonProperty("TruePVE ruleset during the weekend window")] public string PvpRuleset = NightshadeTruePveProfiles.WeekendName;
            [JsonProperty("TruePVE ruleset outside the weekend window")] public string PveRuleset = NightshadeTruePveProfiles.WeekdayName;
            [JsonProperty("Yield TruePVE changes while a purge is active")] public bool YieldToPurge = true;
            [JsonProperty("Install owned TruePVE rulesets")] public bool InstallOwnedRulesets = true;
            [JsonProperty("TruePVE weekday profile")] public TruePveProfile WeekdayProfile;
            [JsonProperty("TruePVE weekend profile")] public TruePveProfile WeekendProfile;
            [JsonProperty("Combat teleport block")] public CombatBlockSettings CombatTeleport = new CombatBlockSettings();
            [JsonProperty("Excavator")] public ExcavatorSettings Excavator = new ExcavatorSettings();
        }

        private class TruePveProfile
        {
            [JsonProperty("Name")] public string Name;
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Default allow damage")] public bool DefaultAllowDamage;
            [JsonProperty("Flags")] public string Flags = "";
            [JsonProperty("Rules")] public List<string> Rules = new List<string>();
        }

        private class CombatBlockSettings
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Seconds")] public int Seconds = 60;
            [JsonProperty("Only during the PvP weekend")] public bool OnlyDuringPvpWeekend;
            [JsonProperty("Ignore damage between teammates")] public bool IgnoreTeammates = true;
            [JsonProperty("Message")] public string Message = NightshadeCombatGate.DefaultMessage;
        }

        private class ExcavatorSettings
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("SpeedMultiplier")] public float SpeedMultiplier = 2f;
            [JsonProperty("KeepYieldPerDiesel")] public bool KeepYieldPerDiesel = true;
        }

        private class ShieldEntry
        {
            public ulong BuyerSteamId;
            public double RemainingSeconds;
        }

        private class Shield
        {
            public ulong CupboardId;
            public uint BuildingId;
            // Steam id of the player who redeemed the first voucher. 0 on shields saved before 1.4.6.
            // Each queue entry has its own buyer. This field is the legacy single buyer.
            public ulong OwnerSteamId;
            public double RemainingSeconds;
            public long LastAccountedTicks;
            // Set on records saved before 1.4.7. Cleared once the record is a queue.
            // A past value means the old Monday expiry already spent the shield.
            public long ExpiresUtcTicks;
            public List<ShieldEntry> Queue;
        }

        private struct ScaleResult
        {
            public float Reduction;
            public bool Paid;
            public string Reason;
            public ulong CupboardId;
            public bool FromOverlap;
        }

        private class Store
        {
            public Dictionary<ulong, Shield> Shields = new Dictionary<ulong, Shield>();
            // Legacy UID records retained solely for compatibility with previously saved data.
            public HashSet<ulong> LegitimateVoucherUids = new HashSet<ulong>();
            public Dictionary<ulong, long> LastSeenUtcTicks = new Dictionary<ulong, long>();
            // SaveRestore.SaveCreatedTime ticks from the map this data was last saved with.
            // Empty until the first load that records it. A later change is a wipe.
            public string SaveId;
        }

        private readonly Dictionary<ulong, float> notifyThrottle = new Dictionary<ulong, float>();
        // BaseEntity.GetBuildingPrivilege uses a 16m gap against each block's world OBB.
        private const float OverlapCacheSeconds = 5f;
        private const float ForeignBlockSlopMeters = 16f;
        private const float OverlapCellMeters = 16f;
        private bool wipeNoted;

        private struct BlockVolume
        {
            public OBB Box;
            public float Extent;
            public int CellX;
            public int CellZ;
        }

        private struct OverlapNeighbor
        {
            public uint BuildingId;
            public float ClosestBlockGap;
        }

        private class OverlapSnapshot
        {
            public float BuiltAt;
            public List<OverlapNeighbor> Neighbors;
        }

        private readonly Dictionary<uint, OverlapSnapshot> overlapCache = new Dictionary<uint, OverlapSnapshot>();
        private readonly HashSet<uint> overlapDirty = new HashSet<uint>();

        protected override void LoadDefaultConfig() { settings = new Settings(); }
        protected override void LoadConfig()
        {
            bool legacyShape = ImportLegacyConfig();
            base.LoadConfig();
            try { settings = Config.ReadObject<Settings>() ?? new Settings(); }
            catch (Exception ex) { PrintError("Invalid config; inspect and repair it: " + ex.Message); settings = new Settings(); }
            if (string.IsNullOrEmpty(settings.PvpRuleset)) settings.PvpRuleset = NightshadeTruePveProfiles.WeekendName;
            if (legacyShape && (string.IsNullOrEmpty(settings.PveRuleset) || settings.PveRuleset == "default"))
            {
                settings.PveRuleset = NightshadeTruePveProfiles.WeekdayName;
                Puts("Weekday TruePVE ruleset moved from \"default\" to \"" + NightshadeTruePveProfiles.WeekdayName + "\". That profile is this plugin's copy of the weekday rules. The default ruleset already in TruePVE.json is not deleted.");
            }
            if (string.IsNullOrEmpty(settings.PveRuleset)) settings.PveRuleset = NightshadeTruePveProfiles.WeekdayName;
            if (double.IsNaN(settings.WeekendVoucherHours) || double.IsInfinity(settings.WeekendVoucherHours) || settings.WeekendVoucherHours < 0d)
                settings.WeekendVoucherHours = 0d;
            if (settings.Excavator == null) settings.Excavator = new ExcavatorSettings();
            float excavatorMult = settings.Excavator.SpeedMultiplier;
            if (float.IsNaN(excavatorMult) || float.IsInfinity(excavatorMult) || excavatorMult < 0.01f)
                settings.Excavator.SpeedMultiplier = NightshadeExcavatorBoost.DefaultSpeedMultiplier;
            if (settings.WeekdayProfile == null || settings.WeekdayProfile.Rules == null || settings.WeekdayProfile.Rules.Count == 0)
                settings.WeekdayProfile = ProfileFrom(NightshadeTruePveProfiles.Weekday());
            if (settings.WeekendProfile == null || settings.WeekendProfile.Rules == null || settings.WeekendProfile.Rules.Count == 0)
                settings.WeekendProfile = ProfileFrom(NightshadeTruePveProfiles.Weekend());
            if (string.IsNullOrEmpty(settings.WeekdayProfile.Name)) settings.WeekdayProfile.Name = NightshadeTruePveProfiles.WeekdayName;
            if (string.IsNullOrEmpty(settings.WeekendProfile.Name)) settings.WeekendProfile.Name = NightshadeTruePveProfiles.WeekendName;
            if (settings.CombatTeleport == null) settings.CombatTeleport = new CombatBlockSettings();
            if (settings.CombatTeleport.Seconds < 1) settings.CombatTeleport.Seconds = 60;
            else if (settings.CombatTeleport.Seconds > 900) settings.CombatTeleport.Seconds = 900;
            if (string.IsNullOrEmpty(settings.CombatTeleport.Message))
                settings.CombatTeleport.Message = NightshadeCombatGate.DefaultMessage;
            // The protection-only override dies with the plugin instance unless its own config key is set.
            // The full-weekend force is the config key, so a reload keeps it.
            forceOfflinePvp = settings.ForcePvpWeekendForTesting;
            forceOfflineGraceMinutes = null;
            nextForceOfflineWarning = 0f;
            forceFullPvp = settings.ForceFullPvpWeekend;
            nextFullForceWarning = 0f;
            SaveConfig();
        }
        protected override void SaveConfig() => Config.WriteObject(settings, true);

        // First load only: copy configs/NightshadeRaidProtection.json when the new file is absent.
        // True when that file has no "Install owned TruePVE rulesets" key (the 1.4.9 shape).
        // Permissions are not copied and are not granted.
        private bool ImportLegacyConfig()
        {
            string dir = Interface.Oxide.ConfigDirectory;
            string next = Path.Combine(dir, "NightshadePVPWeekend.json");
            string prev = Path.Combine(dir, "NightshadeRaidProtection.json");
            if (!File.Exists(next) && File.Exists(prev))
            {
                File.Copy(prev, next, false);
                Puts("Imported configs/NightshadeRaidProtection.json to configs/NightshadePVPWeekend.json.");
            }
            if (!File.Exists(next)) return true;
            try
            {
                JObject raw = JObject.Parse(File.ReadAllText(next));
                return raw["Install owned TruePVE rulesets"] == null;
            }
            catch (Exception ex)
            {
                PrintWarning("Could not inspect configs/NightshadePVPWeekend.json before load: " + ex.Message);
                return false;
            }
        }

        private static TruePveProfile ProfileFrom(NightshadeTruePveProfiles.Spec spec)
        {
            TruePveProfile profile = new TruePveProfile();
            profile.Name = spec.Name;
            profile.Enabled = spec.Enabled;
            profile.DefaultAllowDamage = spec.DefaultAllowDamage;
            profile.Flags = spec.Flags ?? "";
            profile.Rules = spec.Rules ?? new List<string>();
            return profile;
        }

        private static NightshadeTruePveProfiles.Spec SpecFrom(TruePveProfile profile)
        {
            NightshadeTruePveProfiles.Spec spec = new NightshadeTruePveProfiles.Spec();
            if (profile == null) return spec;
            spec.Name = profile.Name;
            spec.Enabled = profile.Enabled;
            spec.DefaultAllowDamage = profile.DefaultAllowDamage;
            spec.Flags = profile.Flags;
            spec.Rules = profile.Rules;
            return spec;
        }

        private void Init()
        {
            // Registered only. Neither permission is granted to any user or group.
            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermBypassCombat, this);
            cmd.AddChatCommand("raidredeem", this, nameof(RedeemCommand));
            cmd.AddChatCommand("raidvoucher", this, nameof(LegacyVoucherCommand));
            cmd.AddConsoleCommand("raidredeem", this, nameof(ConsoleRedeem));
            cmd.AddChatCommand("raidshield", this, nameof(StatusCommand));
            cmd.AddChatCommand("nspvptest", this, nameof(CmdNsRaidTest));
            cmd.AddChatCommand("nspvppvp", this, nameof(CmdNsRaidPvp));
            cmd.AddChatCommand("nspvpwhy", this, nameof(CmdNsRaidWhy));
            cmd.AddConsoleCommand("nspvp.faketime", this, nameof(CmdFakeTime));
            cmd.AddConsoleCommand("nspvp.window", this, nameof(CmdWindow));
            cmd.AddConsoleCommand("nspvp.forcepvp", this, nameof(CmdForcePvp));
            cmd.AddConsoleCommand("nspvp.pvpweekend", this, nameof(CmdPvpWeekend));
            cmd.AddConsoleCommand("nspvp.why", this, nameof(CmdWhy));
            cmd.AddConsoleCommand("nspvp.check", this, nameof(CmdCheck));
            cmd.AddConsoleCommand("nspvp.debug", this, nameof(CmdDebug));
            // Old names still run the same handlers. They are not removed.
            cmd.AddChatCommand("nsraidtest", this, nameof(CmdNsRaidTest));
            cmd.AddChatCommand("nsraidpvp", this, nameof(CmdNsRaidPvp));
            cmd.AddChatCommand("nsraidwhy", this, nameof(CmdNsRaidWhy));
            cmd.AddConsoleCommand("nrp.faketime", this, nameof(CmdFakeTime));
            cmd.AddConsoleCommand("nrp.window", this, nameof(CmdWindow));
            cmd.AddConsoleCommand("nsraid.forcepvp", this, nameof(CmdForcePvp));
            cmd.AddConsoleCommand("nsraid.pvpweekend", this, nameof(CmdPvpWeekend));
            cmd.AddConsoleCommand("nsraid.why", this, nameof(CmdWhy));
            cmd.AddConsoleCommand("nsraid.check", this, nameof(CmdCheck));
            cmd.AddConsoleCommand("nsraid.debug", this, nameof(CmdDebug));
            ImportLegacyData();
            try { store = Interface.Oxide.DataFileSystem.ReadObject<Store>(DataPath) ?? new Store(); }
            catch (Exception ex) { throw new Exception("Raid shield data could not be loaded: purchases disabled to prevent lost vouchers. " + ex.Message); }
            if (store.Shields == null) store.Shields = new Dictionary<ulong, Shield>();
            if (store.LegitimateVoucherUids == null) store.LegitimateVoucherUids = new HashSet<ulong>();
            if (store.LastSeenUtcTicks == null) store.LastSeenUtcTicks = new Dictionary<ulong, long>();
            eastern = NightshadeWeekendClock.ResolveEastern(out usedZoneFallback);
        }

        private void Save() => Interface.Oxide.DataFileSystem.WriteObject(DataPath, store);

        // First load only: copy data/Nightshade/NightshadeRaidProtection/* when the new file is absent.
        // Existing files under data/Nightshade/NightshadePVPWeekend/ are left as they are.
        private void ImportLegacyData()
        {
            string root = Interface.Oxide.DataDirectory;
            string oldDir = Path.Combine(root, "Nightshade", LegacyDataDirName);
            string newDir = Path.Combine(root, "Nightshade", "NightshadePVPWeekend");
            if (!Directory.Exists(oldDir)) return;
            string[] files;
            try { files = Directory.GetFiles(oldDir); }
            catch (Exception ex)
            {
                throw new Exception("Could not read data/Nightshade/NightshadeRaidProtection. Shield data was not loaded. " + ex.Message);
            }
            for (int i = 0; i < files.Length; i++)
            {
                string dest = Path.Combine(newDir, Path.GetFileName(files[i]));
                if (File.Exists(dest)) continue;
                try
                {
                    Directory.CreateDirectory(newDir);
                    File.Copy(files[i], dest, false);
                    Puts("Imported " + files[i] + " to " + dest + ".");
                }
                catch (Exception ex)
                {
                    throw new Exception("Could not import " + files[i] + ". Shield data was not loaded. " + ex.Message);
                }
            }
        }
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
                PrintWarning("NightshadePVPWeekend TEST: forced PvP weekend for offline protection cleared on unload.");
            forceOfflinePvp = false;
            forceOfflineGraceMinutes = null;
            nextForceOfflineWarning = 0f;
            Save();
        }
        private void OnNewSave(string filename)
        {
            // Paid shields follow the map. TCs are gone after a wipe, so the default is to
            // clear them here and again on load if the save id changed. The config switch
            // keeps the balances instead.
            wipeNoted = true;
            if (settings == null || !settings.KeepPaidShieldsOnWipe)
            {
                if (store.Shields != null) store.Shields.Clear();
                Puts("Map wipe: paid shields cleared.");
            }
            else
                Puts("Map wipe: paid shields kept because \"Keep paid shields across a map wipe\" is on.");
            if (store.LegitimateVoucherUids != null) store.LegitimateVoucherUids.Clear();
            if (store.LastSeenUtcTicks != null) store.LastSeenUtcTicks.Clear();
            published.Clear();
            overlapCache.Clear();
            overlapDirty.Clear();
            Save();
        }
        private void OnPlayerDisconnected(BasePlayer player, string reason) => NoteSeen(player);
        private void OnPlayerConnected(BasePlayer player) => NoteSeen(player);
        private void OnServerInitialized()
        {
            TouchConnectedPlayers();
            ApplyWipeSaveId();
            MigrateAllShields();
            Save();
            if (usedZoneFallback)
                PrintWarning("America/New_York and Eastern Standard Time were unavailable. Using a built-in US Eastern zone (EST/EDT).");
            SeedShieldStates();
            CheckWindow();
            WarnIfTruePveScheduleEnabled();
            EnsureRuleset();
            rulesTimer = timer.Every(60f, OnReconcileTick);
            Puts("Weekend window is America/New_York Friday 8:00 PM through Monday 3:00 AM. Giant Excavator uses that same window.");
            WarnForcedOffline(true);
            WarnFullForce(true);
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

        private void OnEntitySpawned(BuildingBlock block) => MarkOverlapDirty(block);
        private void OnEntityKill(BuildingBlock block) => MarkOverlapDirty(block);
        private void OnEntitySpawned(BuildingPrivlidge tc) => MarkOverlapDirty(tc);
        private void OnEntityBuilt(Planner planner, GameObject go)
        {
            if (go == null) return;
            MarkOverlapDirty(go.GetComponent<BuildingBlock>());
        }

        private void OnEntityKill(BuildingPrivlidge tc)
        {
            MarkOverlapDirty(tc);
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
        //     nspvp.faketime and API_SetTestClockUtcTicks are included. API_ClearTestClock returns to the real clock.
        //     nspvp.pvpweekend (config "Force full PvP weekend") is included. While that force is on
        //     this returns true even outside the Eastern window.
        //     nspvp.forcepvp / /nspvptest without "full" are NOT included. That switch only lets this
        //     plugin's offline protection treat the weekend as active. It does not change this API.
        // API_IsFullPvPForced() -> bool
        //     True only for the persisted full-server force, not for the real window or the test clock.
        // API_HasTestClock() -> bool, API_GetTestClockUtcTicks() -> long (0 when the test clock is off)
        // API_SetTestClockUtcTicks(long utcTicks) -> bool, API_ClearTestClock() -> bool
        //     Memory only. TruePVE.json is not written. The excavator boost follows this clock.
        // API_GetExcavatorStatus() -> string
        //     The same excavator clause nspvp.window prints.
        // API_SecondsUntilPvPChange() -> double
        //     Seconds until the window opens, or until it closes when it is already open.
        //     -1 while the full-server force is holding PvP open outside the scheduled window
        //     (there is no Monday end until the force is turned off or the real window opens).
        // API_GetPvPStartUtcTicks() / API_GetPvPEndUtcTicks() -> long (DateTime UTC ticks)
        // API_GetWeekend() -> Dictionary
        //     active (bool, effective), scheduled (bool), forced (bool), realActive (bool),
        //     openEnded (bool), startUtcTicks (long), endUtcTicks (long),
        //     secondsUntilChange (double), ruleset (string)
        // API_GetPlayerShield(BasePlayer) -> Dictionary
        // API_GetCupboardShield(ulong cupboardNetId) -> Dictionary
        // API_GetBuildingShield(uint buildingId) -> Dictionary
        //     state "active" (real weekend, timer running) | "queued" (waiting, time kept) | "none"
        //     seconds (double, total still stored), tcId (ulong), buildingId (uint),
        //     draining (bool), secondsUntilDrain (double, 0 while the real weekend is open),
        //     entries (string, comma-separated seconds, front of the queue first)
        // API_IsEntityProtected(BaseEntity entity, ulong attackerSteamId) -> bool
        //     True when this plugin would reduce that attacker's damage to the entity.
        //     Pass 0 for an outsider. A real steam id is cleared only when
        //     "Prevent teammates authorized on TC from damaging their own shielded building"
        //     is false and that player is authorized on the cupboard that would block.
        //     A paid shield with time left blocks whether or not the Eastern weekend is open.
        //     The timer counts down only during the real weekend. Unused seconds carry to the
        //     next one. A shield also covers each building whose privilege volume overlaps
        //     this one (one hop, no chain) while any queued buyer's auth is still on that TC.
        //     Attached entities use their own building. A player-placed container with no
        //     building id uses every cupboard zone that contains it, by that cupboard's
        //     own building, not the nearest tool cupboard.
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
            bool scheduled;
            return EffectiveNow(NowUtc(), out start, out end, out scheduled);
        }

        [HookMethod("API_IsFullPvPForced")]
        public bool API_IsFullPvPForced()
        {
            return forceFullPvp;
        }

        [HookMethod("API_SecondsUntilPvPChange")]
        public double API_SecondsUntilPvPChange()
        {
            DateTime start, end;
            bool scheduled;
            bool effective = EffectiveNow(NowUtc(), out start, out end, out scheduled);
            if (effective && !scheduled) return -1d;
            return SecondsUntilNow(start, end, scheduled);
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
            bool scheduled;
            bool effective = EffectiveNow(NowUtc(), out start, out end, out scheduled);
            DateTime realStart, realEnd;
            bool realActive;
            Weekend(DateTime.UtcNow, out realStart, out realEnd, out realActive);
            bool openEnded = effective && !scheduled;
            return new Dictionary<string, object>
            {
                ["active"] = effective,
                ["scheduled"] = scheduled,
                ["forced"] = forceFullPvp,
                ["realActive"] = realActive,
                ["openEnded"] = openEnded,
                ["startUtcTicks"] = start.Ticks,
                ["endUtcTicks"] = end.Ticks,
                ["secondsUntilChange"] = openEnded ? -1d : SecondsUntilNow(start, end, scheduled),
                ["ruleset"] = effective ? settings.PvpRuleset : settings.PveRuleset
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
            Dictionary<string, object> own = API_GetCupboardShield(CupboardId(tc));
            if (own != null && !string.Equals(own["state"] as string, "none", StringComparison.Ordinal))
                return own;
            Shield cover = CoveringNeighborShield(tc);
            return cover == null ? own : Describe(cover);
        }

        [HookMethod("API_GetCupboardShield")]
        public Dictionary<string, object> API_GetCupboardShield(ulong cupboardNetId)
        {
            Shield shield;
            if (cupboardNetId == 0UL || !store.Shields.TryGetValue(cupboardNetId, out shield) || shield == null)
                return NoneShield();
            return Describe(shield);
        }

        // Outsider (attackerSteamId 0) or a specific attacker. Same decision as the damage hook.
        [HookMethod("API_IsEntityProtected")]
        public bool API_IsEntityProtected(BaseEntity entity, ulong attackerSteamId)
        {
            return ComputeProtection(entity as BaseCombatEntity, attackerSteamId).Reduction > 0f;
        }

        [HookMethod("API_GetBuildingShield")]
        public Dictionary<string, object> API_GetBuildingShield(uint buildingId)
        {
            if (buildingId == 0u) return NoneShield();
            // Real clock, same as the damage check. A test clock must not hide a live shield.
            Shield best = null;
            double bestSeconds = 0d;
            foreach (var shield in store.Shields.Values)
            {
                if (shield == null || shield.BuildingId != buildingId) continue;
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
            PrintWarning(forceFullPvp
                ? "PvP TEST CLOCK is " + fakeUtc.Value.ToString("u") + " (" + FormatEdge(fakeUtc.Value) + ") via API. Full PvP weekend force is ON, so the server stays on the PvP weekend. The test clock does not turn that off."
                : "PvP TEST CLOCK is " + fakeUtc.Value.ToString("u") + " (" + FormatEdge(fakeUtc.Value) + ") via API. TruePVE.json is not changed. The Giant Excavator boost follows this clock. Clear with API_ClearTestClock or nspvp.faketime clear.");
            return true;
        }

        [HookMethod("API_ClearTestClock")]
        public bool API_ClearTestClock()
        {
            if (!fakeUtc.HasValue) return false;
            fakeUtc = null;
            CheckWindow();
            EnsureRuleset();
            PrintWarning("PvP test clock cleared via API. The live Eastern window is in use again." + FullForceClockNote());
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

        // scheduled is the clock (real, or nspvp.faketime when utc is NowUtc()).
        // The return value is what the server should behave as.
        private bool EffectiveNow(DateTime utc, out DateTime startUtc, out DateTime endUtc, out bool scheduled)
        {
            Weekend(utc, out startUtc, out endUtc, out scheduled);
            return NightshadeWeekendClock.EffectiveActive(scheduled, forceFullPvp);
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

        // Chat args are string[]. ConsoleSystem.Arg.Args is Facepunch.StringView[] on this host,
        // so the console entry point reads tokens with HasArgs and GetString.
        private void RedeemCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (args == null || args.Length != 1)
            {
                ReplyUsage(player);
                return;
            }
            BeginRedeem(player, args[0]);
        }

        private void LegacyVoucherCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            Reply(player, "/raidvoucher was removed. Use /raidredeem 6h, /raidredeem 1d, or /raidredeem weekend.");
            ReplyUsage(player);
        }

        private void ConsoleRedeem(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            BasePlayer player = arg.Connection == null ? null : arg.Connection.player as BasePlayer;
            if (player == null)
            {
                arg.ReplyWith("Usage: /raidredeem 6h | 1d | weekend");
                return;
            }
            if (!arg.HasArgs() || arg.HasArgs(2))
            {
                ReplyUsage(player);
                return;
            }
            BeginRedeem(player, arg.GetString(0));
        }

        private void ReplyUsage(BasePlayer player)
        {
            Reply(player, "Usage: /raidredeem 6h | 1d | weekend");
            Reply(player, "Look at an authorized TC. 6h is 6 hours, 1d is 24 hours, weekend is one full PvP weekend. The timer runs only during that weekend. Unused time carries over.");
            Reply(player, "A paper named 1 Hour still redeems with /raidredeem 1h. A 24h token redeems as 1d.");
            Reply(player, "Your vouchers: " + VoucherBalance(player) + ".");
        }

        private string VoucherBalance(BasePlayer player)
        {
            string line = "6h x" + CountTier(player, NightshadeVoucherTiers.SixHours).ToString(CultureInfo.InvariantCulture)
                + ", 1d x" + CountTier(player, NightshadeVoucherTiers.OneDay).ToString(CultureInfo.InvariantCulture)
                + ", weekend x" + CountTier(player, NightshadeVoucherTiers.Weekend).ToString(CultureInfo.InvariantCulture);
            int legacy = CountTier(player, NightshadeVoucherTiers.OneHour);
            if (legacy > 0)
                line += ". 1h x" + legacy.ToString(CultureInfo.InvariantCulture) + " (use /raidredeem 1h)";
            return line;
        }

        private int CountTier(BasePlayer player, string tierName)
        {
            NightshadeVoucherTiers.Resolved tier;
            if (!NightshadeVoucherTiers.TryResolve(tierName, out tier) || player == null || player.inventory == null) return 0;
            int count = 0;
            ItemContainer[] containers = VoucherContainers(player);
            for (int c = 0; c < containers.Length; c++)
            {
                ItemContainer container = containers[c];
                if (container == null || container.itemList == null) continue;
                for (int i = 0; i < container.itemList.Count; i++)
                {
                    Item item = container.itemList[i];
                    if (!VoucherMatches(item, tier)) continue;
                    if (item.amount > 0) count += item.amount;
                }
            }
            return count;
        }

        private static ItemContainer[] VoucherContainers(BasePlayer player)
        {
            return new ItemContainer[]
            {
                player.inventory.containerMain,
                player.inventory.containerBelt,
                player.inventory.containerWear
            };
        }

        private bool VoucherMatches(Item item, NightshadeVoucherTiers.Resolved tier)
        {
            if (item == null || item.info == null) return false;
            if (item.info.shortname != VoucherShortname) return false;
            return NightshadeVoucherTiers.Matches(item.skin, item.name, tier.Tier);
        }

        private Item FindVoucher(BasePlayer player, NightshadeVoucherTiers.Resolved tier)
        {
            if (player == null || player.inventory == null) return null;
            ItemContainer[] containers = VoucherContainers(player);
            for (int c = 0; c < containers.Length; c++)
            {
                ItemContainer container = containers[c];
                if (container == null || container.itemList == null) continue;
                Item found = container.itemList.FirstOrDefault(i => VoucherMatches(i, tier));
                if (found != null) return found;
            }
            return null;
        }

        private static string AliasNote(NightshadeVoucherTiers.Resolved tier)
        {
            if (!tier.LegacyName || string.IsNullOrEmpty(tier.Input)) return "";
            if (tier.Tier == NightshadeVoucherTiers.OneDay && tier.Input.StartsWith("24h", StringComparison.Ordinal))
                return " 24h is the 1d tier.";
            return "";
        }

        private void BeginRedeem(BasePlayer player, string token)
        {
            NightshadeVoucherTiers.Resolved tier;
            if (!NightshadeVoucherTiers.TryResolve(token, out tier))
            {
                ReplyUsage(player);
                return;
            }
            if (!settings.PaidShields) { Reply(player, "Voucher redemption is currently disabled."); return; }
            BuildingPrivlidge tc = TargetCupboard(player);
            if (tc == null || !Authorized(tc, player.userID)) { Reply(player, "Look directly at a TC you are authorized on, within 5 metres."); return; }
            Item voucher = FindVoucher(player, tier);
            if (voucher == null)
            {
                Reply(player, "You need the " + tier.Tier + " Nightshade Raid Shield token in your inventory.");
                if (tier.Tier == NightshadeVoucherTiers.SixHours && CountTier(player, NightshadeVoucherTiers.OneHour) > 0)
                    Reply(player, "Papers named 1 Hour are still 1 hour. Use /raidredeem 1h.");
                Reply(player, "Your vouchers: " + VoucherBalance(player) + ".");
                return;
            }

            DateTime now = DateTime.UtcNow;
            DateTime start, end;
            bool active;
            Weekend(now, out start, out end, out active);
            ulong id = CupboardId(tc);
            Shield existing;
            store.Shields.TryGetValue(id, out existing);
            if (existing != null) Account(existing, now);
            double newSeconds = NightshadeVoucherTiers.SecondsFor(tier, start, end, settings.WeekendVoucherHours);
            if (newSeconds <= 0d || double.IsNaN(newSeconds) || double.IsInfinity(newSeconds))
            { Reply(player, "No PvP weekend shield time is available. Voucher retained."); return; }
            double already = existing == null ? 0d : Math.Max(0d, existing.RemainingSeconds);
            if (already + newSeconds > Math.Max(1, settings.MaximumStackedHours) * 3600d)
            { Reply(player, "This would exceed the maximum shield time on this TC. Voucher retained."); return; }
            Shield previous = CloneShield(existing);
            Shield shield = existing ?? new Shield();
            if (shield.Queue == null) shield.Queue = new List<ShieldEntry>();
            shield.CupboardId = id;
            if (tc.buildingID != 0u) shield.BuildingId = tc.buildingID;
            ulong buyer = SteamIdOf(player);
            shield.Queue.Add(new ShieldEntry { BuyerSteamId = buyer, RemainingSeconds = newSeconds });
            if (!IsSteamId(shield.OwnerSteamId)) shield.OwnerSteamId = buyer;
            shield.ExpiresUtcTicks = 0L;
            shield.LastAccountedTicks = now.Ticks;
            SyncRemaining(shield);
            if (shield.RemainingSeconds <= 0d)
            {
                if (shield.Queue != null && shield.Queue.Count > 0) shield.Queue.RemoveAt(shield.Queue.Count - 1);
                SyncRemaining(shield);
                Reply(player, "No PvP weekend shield time is available. Voucher retained.");
                return;
            }

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
            string total = Format(TimeSpan.FromSeconds(shield.RemainingSeconds));
            string added = Format(TimeSpan.FromSeconds(newSeconds));
            int count = shield.Queue == null ? 0 : shield.Queue.Count;
            string note = AliasNote(tier);
            Reply(player, active
                ? "Added " + added + " to this TC (" + count + " in queue, " + total + " total). It blocks raid damage now. Unused time carries to the next weekend." + note
                : "Queued " + added + " on this TC (" + count + " in queue, " + total + " total). It blocks raid damage now. The timer waits for Friday 8 PM Eastern, and unused time carries over." + note);
            Puts("Redeemed " + tier.Tier + (tier.LegacyName ? " from " + tier.Input : "") + " for TC " + id + " by " + player.UserIDString + " queue " + count);
        }

        private void Account(Shield shield, DateTime now)
        {
            if (shield == null) return;
            MigrateShield(shield, now);
            if (shield.Queue == null) shield.Queue = new List<ShieldEntry>();
            if (shield.LastAccountedTicks <= 0L || shield.LastAccountedTicks > now.Ticks)
            {
                shield.LastAccountedTicks = now.Ticks;
                shield.ExpiresUtcTicks = 0L;
                SyncRemaining(shield);
                return;
            }
            DateTime from = new DateTime(shield.LastAccountedTicks, DateTimeKind.Utc);
            double drain = NightshadeShieldTime.WeekendOverlapSeconds(from, now, eastern);
            if (drain > 0d && shield.Queue.Count > 0)
            {
                NightshadeShieldTime.Slice[] slices = new NightshadeShieldTime.Slice[shield.Queue.Count];
                for (int i = 0; i < shield.Queue.Count; i++)
                {
                    ShieldEntry entry = shield.Queue[i];
                    slices[i].BuyerId = entry == null ? 0UL : entry.BuyerSteamId;
                    slices[i].RemainingSeconds = entry == null ? 0d : entry.RemainingSeconds;
                }
                int count = NightshadeShieldTime.Consume(slices, slices.Length, drain);
                shield.Queue.Clear();
                for (int i = 0; i < count; i++)
                {
                    shield.Queue.Add(new ShieldEntry { BuyerSteamId = slices[i].BuyerId, RemainingSeconds = slices[i].RemainingSeconds });
                }
            }
            shield.LastAccountedTicks = now.Ticks;
            shield.ExpiresUtcTicks = 0L;
            SyncRemaining(shield);
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

        // The shield follows the cupboard net id. A later building merge changes buildingID;
        // the id saved at redeem must not drop the shield. Time still drains only inside the
        // real weekend window, but any time left blocks raid damage immediately.
        private bool ShieldBlocks(BuildingPrivlidge tc, uint buildingId, DateTime now)
        {
            if (!settings.PaidShields || tc == null) return false;
            Shield shield;
            if (!store.Shields.TryGetValue(CupboardId(tc), out shield) || shield == null)
                return false;
            Account(shield, now);
            if (shield.RemainingSeconds <= 0d) return false;
            if (buildingId != 0u && tc.buildingID != 0u && tc.buildingID != buildingId) return false;
            if (tc.buildingID != 0u && shield.BuildingId != tc.buildingID)
                shield.BuildingId = tc.buildingID;
            PublishShield(shield);
            return true;
        }

        // A neighbor's paid shield covers this cupboard while any queued buyer is authorized here.
        private Shield CoveringNeighborShield(BuildingPrivlidge hitTc)
        {
            if (!settings.ShareOverlappingCupboards || hitTc == null || hitTc.IsDestroyed || hitTc.buildingID == 0u)
                return null;
            DateTime now = DateTime.UtcNow;
            float cushion = PrivilegeRadius();
            List<OverlapNeighbor> neighbors = NeighborsOf(hitTc.buildingID);
            for (int i = 0; i < neighbors.Count; i++)
            {
                OverlapNeighbor neighbor = neighbors[i];
                if (!NightshadeOfflineGate.PrivilegeZonesOverlap(neighbor.ClosestBlockGap, cushion)) continue;
                if (BuildingManager.server == null) return null;
                BuildingManager.Building other = BuildingManager.server.GetBuilding(neighbor.BuildingId);
                if (other == null || other.buildingPrivileges == null) continue;
                foreach (BuildingPrivlidge source in other.buildingPrivileges)
                {
                    if (source == null || source.IsDestroyed) continue;
                    if (source.buildingID == 0u || source.buildingID == hitTc.buildingID) continue;
                    if (!ShieldBlocks(source, source.buildingID, now)) continue;
                    if (!AnyShieldBuyerAuthed(source, hitTc)) continue;
                    Shield shield;
                    if (store.Shields.TryGetValue(CupboardId(source), out shield)) return shield;
                }
            }
            return null;
        }

        private static float PrivilegeRadius()
        {
            float radius = BuildingPrivlidge.Radius;
            if (radius <= 0f || float.IsNaN(radius)) radius = 16f;
            return radius;
        }

        private void ApplyWipeSaveId()
        {
            if (store == null) return;
            if (store.Shields == null) store.Shields = new Dictionary<ulong, Shield>();
            string current = CurrentSaveId();
            bool keep = settings != null && settings.KeepPaidShieldsOnWipe;
            bool idChanged = !string.IsNullOrEmpty(store.SaveId) && !string.IsNullOrEmpty(current)
                && !string.Equals(store.SaveId, current, StringComparison.Ordinal);
            if (!keep && (wipeNoted || idChanged))
            {
                if (store.Shields.Count > 0)
                    Puts("Paid shields cleared because the map save changed.");
                store.Shields.Clear();
                published.Clear();
            }
            if (!string.IsNullOrEmpty(current)) store.SaveId = current;
        }

        private static string CurrentSaveId()
        {
            try
            {
                long ticks = SaveRestore.SaveCreatedTime.Ticks;
                if (ticks <= 0L) return null;
                return ticks.ToString(CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private void MigrateAllShields()
        {
            if (store == null || store.Shields == null) return;
            DateTime now = DateTime.UtcNow;
            foreach (Shield shield in store.Shields.Values)
                Account(shield, now);
        }

        private static void MigrateShield(Shield shield, DateTime now)
        {
            if (shield == null || shield.Queue != null) return;
            shield.Queue = new List<ShieldEntry>();
            if (NightshadeShieldTime.LegacyStillRunning(shield.ExpiresUtcTicks, shield.RemainingSeconds, now))
            {
                shield.Queue.Add(new ShieldEntry
                {
                    BuyerSteamId = shield.OwnerSteamId,
                    RemainingSeconds = shield.RemainingSeconds
                });
            }
            else
                shield.RemainingSeconds = 0d;
            shield.ExpiresUtcTicks = 0L;
        }

        private void SyncRemaining(Shield shield)
        {
            if (shield == null) return;
            double total = 0d;
            if (shield.Queue != null)
            {
                for (int i = 0; i < shield.Queue.Count; i++)
                {
                    ShieldEntry entry = shield.Queue[i];
                    if (entry == null || entry.RemainingSeconds <= 0d || double.IsNaN(entry.RemainingSeconds)) continue;
                    total += entry.RemainingSeconds;
                }
            }
            shield.RemainingSeconds = total;
            if (shield.Queue != null && shield.Queue.Count > 0 && IsSteamId(shield.Queue[0].BuyerSteamId))
                shield.OwnerSteamId = shield.Queue[0].BuyerSteamId;
        }

        private static Shield CloneShield(Shield source)
        {
            if (source == null) return null;
            Shield copy = new Shield();
            copy.CupboardId = source.CupboardId;
            copy.BuildingId = source.BuildingId;
            copy.OwnerSteamId = source.OwnerSteamId;
            copy.RemainingSeconds = source.RemainingSeconds;
            copy.LastAccountedTicks = source.LastAccountedTicks;
            copy.ExpiresUtcTicks = source.ExpiresUtcTicks;
            if (source.Queue == null) return copy;
            copy.Queue = new List<ShieldEntry>(source.Queue.Count);
            for (int i = 0; i < source.Queue.Count; i++)
            {
                ShieldEntry entry = source.Queue[i];
                if (entry == null) continue;
                copy.Queue.Add(new ShieldEntry { BuyerSteamId = entry.BuyerSteamId, RemainingSeconds = entry.RemainingSeconds });
            }
            return copy;
        }

        private static string EntrySeconds(Shield shield)
        {
            if (shield == null || shield.Queue == null) return string.Empty;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < shield.Queue.Count; i++)
            {
                ShieldEntry entry = shield.Queue[i];
                if (entry == null || entry.RemainingSeconds <= 0d) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(entry.RemainingSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private ulong ResolveBuyer(Shield shield, ShieldEntry entry, BuildingPrivlidge tc)
        {
            if (entry != null && IsSteamId(entry.BuyerSteamId)) return entry.BuyerSteamId;
            if (shield != null && IsSteamId(shield.OwnerSteamId)) return shield.OwnerSteamId;
            return tc != null ? tc.OwnerID : 0UL;
        }

        private bool AnyShieldBuyerAuthed(BuildingPrivlidge source, BuildingPrivlidge hitTc)
        {
            if (source == null || hitTc == null) return false;
            Shield shield;
            if (!store.Shields.TryGetValue(CupboardId(source), out shield) || shield == null) return false;
            Account(shield, DateTime.UtcNow);
            if (shield.Queue == null) return false;
            for (int i = 0; i < shield.Queue.Count; i++)
            {
                ShieldEntry entry = shield.Queue[i];
                if (entry == null || entry.RemainingSeconds <= 0d) continue;
                ulong buyer = ResolveBuyer(shield, entry, source);
                if (IsSteamId(buyer) && Authorized(hitTc, buyer)) return true;
            }
            return false;
        }

        private void MarkOverlapDirty(DecayEntity ent)
        {
            if (ent == null) return;
            DirtyOverlap(ent.buildingID);
            DecayEntity captured = ent;
            NextTick(() =>
            {
                if (captured == null || captured.IsDestroyed) return;
                DirtyOverlap(captured.buildingID);
            });
        }

        private void DirtyOverlap(uint buildingId)
        {
            if (buildingId == 0u) return;
            overlapDirty.Add(buildingId);
            OverlapSnapshot snap;
            if (overlapCache.TryGetValue(buildingId, out snap) && snap != null && snap.Neighbors != null)
            {
                for (int i = 0; i < snap.Neighbors.Count; i++)
                    overlapDirty.Add(snap.Neighbors[i].BuildingId);
            }
            List<uint> reverse = null;
            foreach (KeyValuePair<uint, OverlapSnapshot> pair in overlapCache)
            {
                if (pair.Key == buildingId || pair.Value == null || pair.Value.Neighbors == null) continue;
                for (int i = 0; i < pair.Value.Neighbors.Count; i++)
                {
                    if (pair.Value.Neighbors[i].BuildingId != buildingId) continue;
                    if (reverse == null) reverse = new List<uint>();
                    reverse.Add(pair.Key);
                    break;
                }
            }
            if (reverse == null) return;
            for (int i = 0; i < reverse.Count; i++) overlapDirty.Add(reverse[i]);
        }

        private List<OverlapNeighbor> NeighborsOf(uint buildingId)
        {
            float now = Time.realtimeSinceStartup;
            OverlapSnapshot snap;
            if (buildingId != 0u && overlapCache.TryGetValue(buildingId, out snap) && snap != null && snap.Neighbors != null
                && !overlapDirty.Contains(buildingId) && now >= snap.BuiltAt && now - snap.BuiltAt < OverlapCacheSeconds)
                return snap.Neighbors;
            snap = BuildOverlap(buildingId);
            if (buildingId != 0u)
            {
                overlapCache[buildingId] = snap;
                overlapDirty.Remove(buildingId);
            }
            return snap.Neighbors;
        }

        // Privilege volumes meet when any block of A and any block of B have
        // OBB.Distance <= 2 * 16m. That is the same cushion BaseEntity.GetBuildingPrivilege
        // applies around each building block. The set is cached per building.
        private OverlapSnapshot BuildOverlap(uint buildingId)
        {
            OverlapSnapshot snap = new OverlapSnapshot();
            snap.BuiltAt = Time.realtimeSinceStartup;
            snap.Neighbors = new List<OverlapNeighbor>();
            if (buildingId == 0u || BuildingManager.server == null) return snap;
            BuildingManager.Building building = BuildingManager.server.GetBuilding(buildingId);
            if (building == null || building.buildingBlocks == null || building.buildingBlocks.Count == 0) return snap;
            float cushion = PrivilegeRadius();
            float gapLimit = cushion * 2f;
            List<BlockVolume> own = new List<BlockVolume>(building.buildingBlocks.Count);
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            float maxExtent = 0f;
            foreach (BuildingBlock block in building.buildingBlocks)
            {
                if (block == null || block.IsDestroyed || block.transform == null) continue;
                OBB box = block.WorldSpaceBounds();
                float extent = box.extents.magnitude;
                if (float.IsNaN(extent) || float.IsInfinity(extent) || extent < 0f) extent = 0f;
                if (extent > maxExtent) maxExtent = extent;
                Vector3 pos = box.position;
                if (pos.x < min.x) min.x = pos.x;
                if (pos.y < min.y) min.y = pos.y;
                if (pos.z < min.z) min.z = pos.z;
                if (pos.x > max.x) max.x = pos.x;
                if (pos.y > max.y) max.y = pos.y;
                if (pos.z > max.z) max.z = pos.z;
                own.Add(new BlockVolume { Box = box, Extent = extent, CellX = CellOf(pos.x), CellZ = CellOf(pos.z) });
            }
            if (own.Count == 0) return snap;
            Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();
            for (int i = 0; i < own.Count; i++)
            {
                long key = CellKey(own[i].CellX, own[i].CellZ);
                List<int> bucket;
                if (!grid.TryGetValue(key, out bucket))
                {
                    bucket = new List<int>();
                    grid[key] = bucket;
                }
                bucket.Add(i);
            }
            Vector3 center = (min + max) * 0.5f;
            float radius = Vector3.Distance(center, max) + gapLimit + maxExtent + ForeignBlockSlopMeters;
            if (float.IsNaN(radius) || float.IsInfinity(radius) || radius < gapLimit) radius = gapLimit + ForeignBlockSlopMeters;
            List<BuildingBlock> found = Facepunch.Pool.Get<List<BuildingBlock>>();
            Dictionary<uint, float> best = new Dictionary<uint, float>();
            try
            {
                Vis.Entities(center, radius, found);
                for (int i = 0; i < found.Count; i++)
                {
                    BuildingBlock other = found[i];
                    if (other == null || other.IsDestroyed || other.transform == null) continue;
                    uint otherId = other.buildingID;
                    if (otherId == 0u || otherId == buildingId) continue;
                    float known;
                    if (best.TryGetValue(otherId, out known) && known <= 0f) continue;
                    OBB otherBox = other.WorldSpaceBounds();
                    float otherExtent = otherBox.extents.magnitude;
                    if (float.IsNaN(otherExtent) || float.IsInfinity(otherExtent) || otherExtent < 0f) otherExtent = 0f;
                    float reach = gapLimit + maxExtent + otherExtent;
                    int cells = (int)Math.Ceiling(reach / OverlapCellMeters);
                    if (cells < 1) cells = 1;
                    if (cells > 8) cells = 8;
                    int cx = CellOf(otherBox.position.x);
                    int cz = CellOf(otherBox.position.z);
                    float gap = float.MaxValue;
                    for (int dx = -cells; dx <= cells && gap > 0f; dx++)
                    {
                        for (int dz = -cells; dz <= cells && gap > 0f; dz++)
                        {
                            List<int> bucket;
                            if (!grid.TryGetValue(CellKey(cx + dx, cz + dz), out bucket)) continue;
                            for (int b = 0; b < bucket.Count; b++)
                            {
                                float distance = otherBox.Distance(own[bucket[b]].Box);
                                if (float.IsNaN(distance) || float.IsInfinity(distance) || distance < 0f) continue;
                                if (distance < gap) gap = distance;
                            }
                        }
                    }
                    if (!NightshadeOfflineGate.PrivilegeZonesOverlap(gap, cushion)) continue;
                    if (!best.ContainsKey(otherId) || gap < best[otherId]) best[otherId] = gap;
                }
            }
            finally
            {
                Facepunch.Pool.FreeUnmanaged(ref found);
            }
            foreach (KeyValuePair<uint, float> pair in best)
                snap.Neighbors.Add(new OverlapNeighbor { BuildingId = pair.Key, ClosestBlockGap = pair.Value });
            snap.Neighbors.Sort(delegate(OverlapNeighbor a, OverlapNeighbor b) { return a.BuildingId.CompareTo(b.BuildingId); });
            return snap;
        }

        private static int CellOf(float world)
        {
            if (float.IsNaN(world) || float.IsInfinity(world)) return 0;
            return (int)Math.Floor(world / OverlapCellMeters);
        }

        private static long CellKey(int x, int z)
        {
            return ((long)x << 32) ^ (uint)z;
        }

        // One hop from the hit building's own cupboards. A neighbor that only touches
        // another neighbor is not included, so protection cannot walk across the map.
        // A paid shield on the neighbor covers this building while any of its buyers
        // is authorized here. Failing that check does not clear any other shield.
        private NightshadeOfflineGate.HitDecision MatchDirectOverlaps(List<BuildingPrivlidge> own, ulong attacker, bool allowAuthorized, DateTime now, bool offlineWindow)
        {
            NightshadeOfflineGate.HitDecision best = new NightshadeOfflineGate.HitDecision();
            if (!settings.ShareOverlappingCupboards || own == null || own.Count == 0 || BuildingManager.server == null) return best;
            float cushion = PrivilegeRadius();
            for (int i = 0; i < own.Count; i++)
            {
                BuildingPrivlidge hitTc = own[i];
                if (hitTc == null || hitTc.IsDestroyed || hitTc.buildingID == 0u) continue;
                List<OverlapNeighbor> neighbors = NeighborsOf(hitTc.buildingID);
                for (int n = 0; n < neighbors.Count; n++)
                {
                    OverlapNeighbor neighbor = neighbors[n];
                    if (!NightshadeOfflineGate.PrivilegeZonesOverlap(neighbor.ClosestBlockGap, cushion)) continue;
                    BuildingManager.Building other = BuildingManager.server.GetBuilding(neighbor.BuildingId);
                    if (other == null || other.buildingPrivileges == null) continue;
                    foreach (BuildingPrivlidge source in other.buildingPrivileges)
                    {
                        if (source == null || source.IsDestroyed) continue;
                        if (source.buildingID == 0u || source.buildingID == hitTc.buildingID) continue;
                        bool buyerAuthed = AnyShieldBuyerAuthed(source, hitTc);
                        // Live auth list only. Matching owner ids after a deauth does not keep the hop.
                        bool ownerAuthed = IsSteamId(source.OwnerID) && Authorized(hitTc, source.OwnerID);
                        bool ownerLinked = NightshadeOfflineGate.SameOwnerOnOverlap(source.OwnerID, SteamIdMin, ownerAuthed, false);
                        bool sourceShield = ShieldBlocks(source, source.buildingID, now);
                        bool sourceOffline = offlineWindow && !sourceShield && OfflineProtected(source, now);
                        // The overlap cupboard must qualify on its own. An online player on it blocks the copy.
                        bool overlapOffline = sourceOffline && OfflineProtected(hitTc, now);
                        bool attackerOnSource = attacker != 0UL && Authorized(source, attacker);
                        bool attackerOnHit = attacker != 0UL && Authorized(hitTc, attacker);
                        NightshadeOfflineGate.OverlapLink link = new NightshadeOfflineGate.OverlapLink();
                        link.ZonesOverlap = true;
                        link.OwnerOnOverlap = ownerLinked;
                        link.BuyerAuthedOnHit = buyerAuthed;
                        link.SourceShield = sourceShield;
                        link.SourceOffline = sourceOffline;
                        link.OverlapOffline = overlapOffline;
                        link.AttackerAuthorizedOnSource = attackerOnSource;
                        link.AttackerAuthorizedOnHit = attackerOnHit;
                        NightshadeOfflineGate.HitDecision part = NightshadeOfflineGate.MatchedOverlap(true, allowAuthorized, link);
                        if (!part.Block)
                        {
                            if (part.ClearedByAuth) best.ClearedByAuth = true;
                            continue;
                        }
                        if (best.Paid && !part.Paid) continue;
                        best.Block = true;
                        best.Paid = part.Paid || best.Paid;
                        best.FromOverlap = true;
                        best.CupboardId = CupboardId(source);
                        if (best.Paid) return best;
                    }
                }
            }
            return best;
        }

        // 0 means this hit is not reduced. Paid shields return 1 whenever time is left.
        // Offline protection uses the config percent, and only inside the weekend or a force.
        // attacker 0 is an outsider, so tool-cupboard authorization cannot clear the block.
        // The real clock is used on purpose. nspvp.faketime does not open or drain a shield.
        private ScaleResult ComputeProtection(BaseCombatEntity entity, ulong attacker)
        {
            ScaleResult result = new ScaleResult();
            result.Reason = "not protected";
            if (entity == null || entity is BasePlayer)
            {
                result.Reason = "not a building";
                return result;
            }
            if (BuildingManager.server == null)
            {
                result.Reason = "not protected: building manager is not ready";
                return result;
            }
            DateTime now = DateTime.UtcNow;
            DateTime start, end;
            bool active;
            Weekend(now, out start, out end, out active);
            if (end <= start) active = false;
            bool offlineWindow = active || forceOfflinePvp || forceFullPvp;

            uint attached = BuildingIdOf(entity);
            uint[] buildings = attached != 0u
                ? NightshadeRaidTargets.BuildingsForContainer(attached, false, null)
                : IsLoosePlayerContainer(entity) && entity.transform != null
                    ? NightshadeRaidTargets.BuildingsForContainer(0u, true, ZonesContaining(entity.transform.position))
                    : new uint[0];
            if (buildings.Length == 0)
            {
                result.Reason = "not protected: no building id";
                return result;
            }
            bool clearedByAuth = false;
            for (int i = 0; i < buildings.Length; i++)
            {
                NightshadeOfflineGate.HitDecision decision = DecideBuilding(buildings[i], attacker, now, offlineWindow);
                if (decision.ClearedByAuth) clearedByAuth = true;
                if (!decision.Block) continue;
                float reduction = NightshadeOfflineGate.Reduction(decision.Paid, true, settings.OfflineReductionPercent);
                if (reduction <= result.Reduction) continue;
                result.Reduction = reduction;
                result.Paid = decision.Paid;
                result.FromOverlap = decision.FromOverlap;
                result.CupboardId = decision.CupboardId;
                result.Reason = (decision.Paid ? "paid shield on " : "offline protection on ")
                    + (decision.FromOverlap ? "overlapping TC " : "TC ")
                    + decision.CupboardId;
            }
            if (result.Reduction <= 0f)
            {
                if (clearedByAuth)
                    result.Reason = "not protected for this attacker: authorized on the TC, and authorized damage is allowed";
                else
                    result.Reason = offlineWindow
                        ? "not protected: no paid shield with time left, and offline grace is not met"
                        : "not protected: no paid shield with time left";
            }
            return result;
        }

        // TruePVE calls CanEntityTakeDamage from inside its own damage hook. The same hit
        // then reaches OnEntityTakeDamage. One result per frame keeps the overlap scan to one pass.
        private ScaleResult ScaleForHit(BaseCombatEntity entity, ulong attacker)
        {
            int frame = Time.frameCount;
            ulong id = entity != null && entity.net != null ? entity.net.ID.Value : 0UL;
            if (frame == scaleFrame && id == scaleEntity && attacker == scaleAttacker && id != 0UL)
                return scaleCached;
            scaleFrame = frame;
            scaleEntity = id;
            scaleAttacker = attacker;
            scaleCached = ComputeProtection(entity, attacker);
            return scaleCached;
        }

        // Player-placed storage that is not attached to a building. World crates, corpses,
        // and backpacks are not included. Attached deployables already use BuildingIdOf.
        private static bool IsLoosePlayerContainer(BaseEntity entity)
        {
            if (entity == null) return false;
            if (entity is BasePlayer || entity is LootContainer || entity is LootableCorpse ||
                entity is DroppedItemContainer || entity is SupplyDrop || entity is HackableLockedCrate)
                return false;
            if (entity.OwnerID < SteamIdMin) return false;
            return entity is StorageContainer || entity is ContainerIOEntity || entity is WeaponRack;
        }

        private static NightshadeRaidTargets.ZoneHit[] ZonesContaining(Vector3 position)
        {
            float radius = BuildingPrivlidge.Radius;
            if (radius <= 0f || float.IsNaN(radius)) radius = 16f;
            List<BuildingPrivlidge> nearby = Facepunch.Pool.Get<List<BuildingPrivlidge>>();
            try
            {
                Vis.Entities(position, radius, nearby);
                var hits = new List<NightshadeRaidTargets.ZoneHit>(nearby.Count);
                for (int i = 0; i < nearby.Count; i++)
                {
                    BuildingPrivlidge tc = nearby[i];
                    if (tc == null || tc.IsDestroyed || tc.transform == null) continue;
                    hits.Add(new NightshadeRaidTargets.ZoneHit
                    {
                        BuildingId = tc.buildingID,
                        DistanceMeters = Vector3.Distance(position, tc.transform.position),
                        RadiusMeters = radius
                    });
                }
                return hits.ToArray();
            }
            finally
            {
                Facepunch.Pool.FreeUnmanaged(ref nearby);
            }
        }

        private NightshadeOfflineGate.HitDecision DecideBuilding(uint buildingId, ulong attacker, DateTime now, bool offlineWindow)
        {
            NightshadeOfflineGate.HitDecision none = new NightshadeOfflineGate.HitDecision();
            if (buildingId == 0u || BuildingManager.server == null) return none;
            BuildingManager.Building building = BuildingManager.server.GetBuilding(buildingId);
            if (building == null || building.buildingPrivileges == null || building.buildingPrivileges.Count == 0)
                return none;
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
                        vote.CupboardId = CupboardId(tc);
                        vote.BuildingId = tc.buildingID;
                        vote.AttackerAuthorized = attacker != 0UL && Authorized(tc, attacker);
                        // A foreign building id is recorded and then ignored. It cannot cancel a shield.
                        if (tc.buildingID == buildingId)
                        {
                            vote.PaidShield = ShieldBlocks(tc, buildingId, now);
                            if (offlineWindow)
                                vote.OfflineProtected = OfflineProtected(tc, now);
                            own.Add(tc);
                        }
                    }
                    votes[n++] = vote;
                }
                decision = NightshadeOfflineGate.Decide(buildingId, allowAuthorized, votes);
                if (!decision.Block)
                {
                    NightshadeOfflineGate.HitDecision overlap = MatchDirectOverlaps(own, attacker, allowAuthorized, now, offlineWindow);
                    if (overlap.Block)
                        decision = overlap;
                    else if (overlap.ClearedByAuth)
                        decision.ClearedByAuth = true;
                }
            }
            finally
            {
                Facepunch.Pool.FreeUnmanaged(ref own);
            }
            return decision;
        }

        private static ulong AttackerId(HitInfo hit)
        {
            if (hit == null) return 0UL;
            if (hit.InitiatorPlayer != null) return SteamIdOf(hit.InitiatorPlayer);
            if (hit.Initiator != null && hit.Initiator.OwnerID >= SteamIdMin) return hit.Initiator.OwnerID;
            return 0UL;
        }

        private void NotifyBlocked(HitInfo hit, bool paid)
        {
            if (!settings.NotifyAttacker || hit == null || hit.InitiatorPlayer == null) return;
            float last;
            ulong id = SteamIdOf(hit.InitiatorPlayer);
            if (notifyThrottle.TryGetValue(id, out last) && Time.realtimeSinceStartup - last <= 8f) return;
            notifyThrottle[id] = Time.realtimeSinceStartup;
            Reply(hit.InitiatorPlayer, paid ? "Nightshade raid shield blocked this hit." : "Offline protection reduced this hit.");
        }

        private void NoteHit(BaseCombatEntity entity, ulong attacker, ScaleResult result)
        {
            if (!debugHits || entity == null || entity.net == null) return;
            float now = Time.realtimeSinceStartup;
            if (debugNext.Count > 300) debugNext.Clear();
            float next;
            ulong id = entity.net.ID.Value;
            if (debugNext.TryGetValue(id, out next) && now < next) return;
            debugNext[id] = now + 2f;
            string name = string.IsNullOrEmpty(entity.ShortPrefabName) ? entity.GetType().Name : entity.ShortPrefabName;
            Puts("hit " + name + " net " + id
                + " attacker " + attacker
                + " reduction " + result.Reduction.ToString("0.##", CultureInfo.InvariantCulture)
                + " " + (result.Reason ?? ""));
        }

        // TruePVE asks this before its ruleset. false blocks the hit. null lets TruePVE decide.
        // Never return true: that would force TruePVE to allow the hit and skip its own rules.
        // The argument is BaseEntity because TruePVE passes whatever it was given, including non-combat.
        private object CanEntityTakeDamage(BaseEntity entity, HitInfo hit)
        {
            BaseCombatEntity combat = entity as BaseCombatEntity;
            if (combat == null || hit == null || hit.damageTypes == null) return null;
            if (hit.damageTypes.Has(Rust.DamageType.Decay)) return null;
            if (combat is BasePlayer) return null;
            ulong attacker = AttackerId(hit);
            ScaleResult result = ScaleForHit(combat, attacker);
            if (result.Reduction < 1f) return null;
            NotifyBlocked(hit, result.Paid);
            NoteHit(combat, attacker, result);
            return false;
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hit)
        {
            if (entity == null || hit == null || hit.damageTypes == null || hit.damageTypes.Has(Rust.DamageType.Decay)) return null;
            // Players, NPCs, and animals are not building pieces. Buildings, attached
            // deployables, and loose player containers inside a protected cupboard zone are.
            if (entity is BasePlayer)
            {
                NotePlayerCombat(hit.InitiatorPlayer, entity as BasePlayer, hit);
                return null;
            }
            ulong attacker = AttackerId(hit);
            ScaleResult result = ScaleForHit(entity, attacker);
            NoteHit(entity, attacker, result);
            if (result.Reduction <= 0f) return null;
            hit.damageTypes.ScaleAll(1f - result.Reduction);
            NotifyBlocked(hit, result.Paid);
            // A full block must cancel the hit. Scaling to zero and returning null still lets
            // a later hook, including TruePVE's weekend "anything can hurt anything" ruleset, treat it as a normal hit.
            if (result.Reduction >= 1f) return true;
            return null;
        }

        private void OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            if (attacker == null || info == null) return;
            NotePlayerCombat(attacker, info.HitEntity as BasePlayer, info);
        }

        // Direct player hits only. InitiatorPlayer ignores turrets, traps, and other owned entities.
        private void NotePlayerCombat(BasePlayer attacker, BasePlayer victim, HitInfo hit)
        {
            CombatBlockSettings combat = settings == null ? null : settings.CombatTeleport;
            if (combat == null || hit == null || hit.damageTypes == null) return;
            bool teammates = attacker != null && victim != null && attacker.currentTeam != 0UL && attacker.currentTeam == victim.currentTeam;
            bool selfHit = attacker != null && victim != null && attacker.userID == victim.userID;
            if (!NightshadeCombatGate.Tag(combat.Enabled, combat.OnlyDuringPvpWeekend, API_IsPvPActive(), selfHit, RealFighter(attacker), RealFighter(victim), teammates, combat.IgnoreTeammates, hit.damageTypes.Total()))
                return;
            float until = Time.realtimeSinceStartup + combat.Seconds;
            combatUntil[attacker.userID] = until;
            combatUntil[victim.userID] = until;
            if (combatUntil.Count > 256) PruneCombatLocks();
        }

        private static bool RealFighter(BasePlayer player)
        {
            if (player == null || player.IsNpc) return false;
            return player.userID >= SteamIdMin;
        }

        private void PruneCombatLocks()
        {
            float now = Time.realtimeSinceStartup;
            List<ulong> drop = new List<ulong>();
            foreach (KeyValuePair<ulong, float> pair in combatUntil)
            {
                if (pair.Value <= now) drop.Add(pair.Key);
            }
            for (int i = 0; i < drop.Count; i++) combatUntil.Remove(drop[i]);
        }

        // IQTeleportation treats a string from these hooks as the chat line and cancels the teleport.
        // A null return lets the teleport continue. NTeleportation and Kits (not installed here) use
        // the same CanTeleport / canTeleport names. Admin /tp in IQTeleportation does not call them.
        private object CanTeleport(BasePlayer player) { return CombatTeleportBlock(player); }
        private object canTeleport(BasePlayer player) { return CombatTeleportBlock(player); }
        private object CanTeleport(BasePlayer player, Vector3 destination) { return CombatTeleportBlock(player); }
        private object CanTeleportHome(BasePlayer player) { return CombatTeleportBlock(player); }
        private object CanTeleportHome(BasePlayer player, Vector3 destination) { return CombatTeleportBlock(player); }
        private object CanTeleportPlayer(BasePlayer player) { return CombatTeleportBlock(player); }
        private object CanTeleportWarp(BasePlayer player) { return CombatTeleportBlock(player); }
        private object CanTownTeleport(BasePlayer player) { return CombatTeleportBlock(player); }
        private object CanOutpostTeleport(BasePlayer player) { return CombatTeleportBlock(player); }

        // IQ calls this as OnTeleportRequested(acceptor, requester). The requester is the player who moves.
        private object OnTeleportRequested(BasePlayer player, BasePlayer target)
        {
            object mover = CombatTeleportBlock(target);
            if (mover != null) return mover;
            return CombatTeleportBlock(player);
        }

        private object CombatTeleportBlock(BasePlayer player)
        {
            CombatBlockSettings combat = settings == null ? null : settings.CombatTeleport;
            if (combat == null || !combat.Enabled || player == null) return null;
            if (combat.OnlyDuringPvpWeekend && !API_IsPvPActive()) return null;
            if (permission.UserHasPermission(player.UserIDString, PermBypassCombat)) return null;
            float now = Time.realtimeSinceStartup;
            float until;
            if (!combatUntil.TryGetValue(player.userID, out until) || until <= now)
            {
                if (until > 0f) combatUntil.Remove(player.userID);
                return null;
            }
            int seconds = (int)Math.Ceiling(until - now);
            if (seconds < 1) seconds = 1;
            string template = string.IsNullOrEmpty(combat.Message) ? NightshadeCombatGate.DefaultMessage : combat.Message;
            return template.Replace("{seconds}", seconds.ToString(CultureInfo.InvariantCulture));
        }

        private string CombatWindowText()
        {
            CombatBlockSettings combat = settings == null ? null : settings.CombatTeleport;
            if (combat == null || !combat.Enabled) return "off";
            float now = Time.realtimeSinceStartup;
            int locks = 0;
            foreach (KeyValuePair<ulong, float> pair in combatUntil)
            {
                if (pair.Value > now) locks++;
            }
            return "on " + combat.Seconds.ToString(CultureInfo.InvariantCulture) + "s"
                + (combat.OnlyDuringPvpWeekend ? " weekend-only" : "")
                + " locks " + locks.ToString(CultureInfo.InvariantCulture);
        }

        private void StatusCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            DateTime now = NowUtc();
            DateTime start, end;
            bool active;
            Weekend(now, out start, out end, out active);
            if (fakeUtc.HasValue) Reply(player, "Test clock is on. Window checks below are not the live clock. Clear it with nspvp.faketime clear.");
            if (forceFullPvp) Reply(player, "Full PvP weekend force is on. Raiding follows the PvP weekend. A paid shield with time left still blocks. Its timer uses the real Eastern weekend.");
            Reply(player, active ? "PvP weekend ends in " + Format(end - now) + "." : "PvP weekend starts in " + Format(start - now) + ".");
            BuildingPrivlidge tc = TargetCupboard(player);
            if (tc == null) tc = player.GetBuildingPrivilege();
            if (tc == null || !Authorized(tc, player.userID)) { Reply(player, "Look at or stand near an authorized TC to inspect its protection."); return; }
            Shield shield = null;
            DateTime realStart, realEnd;
            bool realActive;
            Weekend(DateTime.UtcNow, out realStart, out realEnd, out realActive);
            store.Shields.TryGetValue(CupboardId(tc), out shield);
            Account(shield, DateTime.UtcNow);
            if (shield == null || shield.RemainingSeconds <= 0d)
            {
                Shield cover = CoveringNeighborShield(tc);
                if (cover == null)
                    Reply(player, "TC paid shield: none.");
                else
                {
                    Account(cover, DateTime.UtcNow);
                    Reply(player, "This TC has no shield of its own. Privilege-overlap TC " + cover.CupboardId
                        + " covers it while one of its buyers stays authorized. "
                        + Format(TimeSpan.FromSeconds(Math.Max(0, cover.RemainingSeconds))) + " left. Unused time carries over.");
                }
                ReplyOffline(player, tc);
                return;
            }
            int queued = shield.Queue == null ? 0 : shield.Queue.Count;
            string label = realActive ? "TC raid shield active: " : "TC raid shield queued: ";
            Reply(player, label + Format(TimeSpan.FromSeconds(Math.Max(0, shield.RemainingSeconds)))
                + " across " + queued + " " + (queued == 1 ? "entry" : "entries") + ". Unused time carries to the next weekend.");
            if (!realActive)
                Reply(player, "Raid damage is blocked now. Stored time counts down from " + FormatEdge(realStart) + " through " + FormatEdge(realEnd) + ".");
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

        private bool IsPluginAdmin(BasePlayer player)
        {
            return player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermAdmin));
        }

        private void CmdNsRaidWhy(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!IsPluginAdmin(player))
            {
                Reply(player, "No permission.");
                return;
            }
            BaseCombatEntity entity = LookTarget(player);
            ReplyLines(player, entity == null
                ? "Look at a building piece or deployable and run /nspvpwhy again."
                : ExplainEntity(entity, SteamIdOf(player)));
        }

        private void CmdWhy(ConsoleSystem.Arg arg)
        {
            if (arg == null || !ConsoleAdmin(arg)) return;
            BasePlayer player = arg.Connection == null ? null : arg.Connection.player as BasePlayer;
            if (player == null)
            {
                arg.ReplyWith("Look at an entity and run nspvp.why from F1, or use nspvp.check x y z.");
                return;
            }
            BaseCombatEntity entity = LookTarget(player);
            string report = entity == null
                ? "Look at a building piece or deployable and run nspvp.why again."
                : ExplainEntity(entity, SteamIdOf(player));
            arg.ReplyWith(report);
            ReplyLines(player, report);
        }

        private void CmdCheck(ConsoleSystem.Arg arg)
        {
            if (arg == null || !ConsoleAdmin(arg)) return;
            string[] args = ConsoleArgs(arg);
            float x, y, z;
            if (args.Length < 3
                || !float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                || !float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                || !float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
            {
                arg.ReplyWith("Usage: nspvp.check <x> <y> <z>");
                return;
            }
            BaseCombatEntity entity = CombatNear(new Vector3(x, y, z));
            BasePlayer player = arg.Connection == null ? null : arg.Connection.player as BasePlayer;
            string report = entity == null
                ? "No building piece or deployable within 3m of "
                    + x.ToString("0.0", CultureInfo.InvariantCulture) + " "
                    + y.ToString("0.0", CultureInfo.InvariantCulture) + " "
                    + z.ToString("0.0", CultureInfo.InvariantCulture) + "."
                : ExplainEntity(entity, SteamIdOf(player));
            arg.ReplyWith(report);
            if (player != null) ReplyLines(player, report);
        }

        private void CmdDebug(ConsoleSystem.Arg arg)
        {
            if (arg == null || !ConsoleAdmin(arg)) return;
            arg.ReplyWith(HandleDebug(ConsoleArgs(arg)));
        }

        private string HandleDebug(string[] args)
        {
            if (args == null || args.Length != 1 || string.IsNullOrEmpty(args[0]))
                return "Usage: nspvp.debug on | off | status";
            string action = args[0];
            if (action.Equals("status", StringComparison.OrdinalIgnoreCase))
                return "Hit debug log is " + (debugHits ? "ON" : "OFF") + ". Each entity is logged at most once every 2 seconds.";
            if (action.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                debugHits = false;
                debugNext.Clear();
                return "Hit debug log is OFF.";
            }
            if (!action.Equals("on", StringComparison.OrdinalIgnoreCase))
                return "Usage: nspvp.debug on | off | status";
            debugHits = true;
            return "Hit debug log is ON. Each entity is logged at most once every 2 seconds. Turn it off with nspvp.debug off.";
        }

        private static BaseCombatEntity LookTarget(BasePlayer player)
        {
            if (player == null || player.eyes == null) return null;
            RaycastHit hit;
            if (!Physics.Raycast(player.eyes.HeadRay(), out hit, 30f)) return null;
            BaseEntity entity = hit.GetEntity();
            if (entity is BasePlayer) return null;
            return entity as BaseCombatEntity;
        }

        private static BaseCombatEntity CombatNear(Vector3 pos)
        {
            List<BaseCombatEntity> found = Facepunch.Pool.Get<List<BaseCombatEntity>>();
            try
            {
                Vis.Entities(pos, 3f, found);
                BaseCombatEntity best = null;
                float bestDist = float.MaxValue;
                for (int i = 0; i < found.Count; i++)
                {
                    BaseCombatEntity entity = found[i];
                    if (entity == null || entity.IsDestroyed || entity is BasePlayer || entity.transform == null) continue;
                    float dist = Vector3.Distance(pos, entity.transform.position);
                    if (dist >= bestDist) continue;
                    best = entity;
                    bestDist = dist;
                }
                return best;
            }
            finally
            {
                Facepunch.Pool.FreeUnmanaged(ref found);
            }
        }

        private bool OfflineWindowOpen()
        {
            DateTime start, end;
            bool active;
            Weekend(DateTime.UtcNow, out start, out end, out active);
            return active || forceOfflinePvp || forceFullPvp;
        }

        private string ModeLine()
        {
            DateTime start, end;
            bool real;
            Weekend(DateTime.UtcNow, out start, out end, out real);
            int grace = Math.Max(0, EffectiveOfflineGraceMinutes());
            return "Mode: real weekend " + (real ? "ACTIVE" : "inactive")
                + " (" + FormatEdge(start) + " -> " + FormatEdge(end) + ")"
                + " | offline-force " + (forceOfflinePvp ? "ON" : "off")
                + " grace " + grace + " min"
                + " | full-force " + (forceFullPvp ? "ON" : "off")
                + " | paid shields " + (settings != null && settings.PaidShields ? "enabled" : "disabled");
        }

        private string ExplainEntity(BaseCombatEntity entity, ulong viewer)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(ModeLine()).Append('\n');
            if (entity == null)
            {
                sb.Append("No entity.\n");
                return sb.ToString();
            }
            uint buildingId = BuildingIdOf(entity);
            string name = string.IsNullOrEmpty(entity.ShortPrefabName) ? entity.GetType().Name : entity.ShortPrefabName;
            Vector3 pos = entity.transform != null ? entity.transform.position : Vector3.zero;
            ulong netId = entity.net == null ? 0UL : entity.net.ID.Value;
            sb.Append("Entity: ").Append(name)
                .Append(" net ").Append(netId)
                .Append(" at ").Append(Num(pos.x)).Append(' ').Append(Num(pos.y)).Append(' ').Append(Num(pos.z))
                .Append('\n');
            sb.Append("Building id: ").Append(buildingId).Append('\n');
            float radius = PrivilegeRadius();
            sb.Append("Overlap share: ").Append(settings != null && settings.ShareOverlappingCupboards ? "on" : "off")
                .Append(" (privilege overlap, ").Append(Num(radius))
                .Append("m around each building block, volumes meet within ").Append(Num(radius * 2f))
                .Append("m, one hop, no chain)\n");
            if (buildingId == 0u || BuildingManager.server == null)
            {
                sb.Append("No own building. A loose player container is checked against every cupboard zone that contains it.\n");
            }
            else
            {
                BuildingManager.Building building = BuildingManager.server.GetBuilding(buildingId);
                if (building == null || building.buildingPrivileges == null || building.buildingPrivileges.Count == 0)
                    sb.Append("Building ").Append(buildingId).Append(" has no tool cupboard.\n");
                else
                {
                    foreach (BuildingPrivlidge tc in building.buildingPrivileges)
                        AppendCupboard(sb, tc, buildingId);
                }
            }
            ScaleResult outsider = ComputeProtection(entity, 0UL);
            sb.Append("Outsider: ").Append(outsider.Reduction > 0f ? "PROTECTED yes" : "protected no")
                .Append(" - ").Append(outsider.Reason ?? "").Append('\n');
            if (IsSteamId(viewer))
            {
                ScaleResult mine = ComputeProtection(entity, viewer);
                sb.Append("You (").Append(viewer).Append("): ")
                    .Append(mine.Reduction > 0f ? "PROTECTED yes" : "protected no")
                    .Append(" - ").Append(mine.Reason ?? "").Append('\n');
            }
            return sb.ToString();
        }

        private void AppendCupboard(StringBuilder sb, BuildingPrivlidge tc, uint hitBuilding)
        {
            if (tc == null || tc.IsDestroyed)
            {
                sb.Append("Cupboard: missing\n");
                return;
            }
            bool own = tc.buildingID == hitBuilding;
            sb.Append(own ? "Own TC: " : "Listed TC on another building: ")
                .Append(CupboardId(tc))
                .Append(" building ").Append(tc.buildingID)
                .Append(" owner ").Append(tc.OwnerID)
                .Append('\n');
            AppendShieldRecord(sb, tc);
            AppendAuth(sb, tc);
            if (own) AppendCoveredNeighbors(sb, tc);
        }

        private void AppendShieldRecord(StringBuilder sb, BuildingPrivlidge tc)
        {
            DateTime now = DateTime.UtcNow;
            DateTime start, end;
            bool realActive;
            Weekend(now, out start, out end, out realActive);
            Shield shield;
            if (tc == null || !store.Shields.TryGetValue(CupboardId(tc), out shield) || shield == null)
            {
                sb.Append("  Paid shield record: none\n");
                Shield cover = tc == null ? null : CoveringNeighborShield(tc);
                if (cover != null)
                {
                    Account(cover, now);
                    sb.Append("  Covered by privilege-overlap TC ").Append(cover.CupboardId)
                        .Append(" remaining ").Append(Math.Max(0d, cover.RemainingSeconds).ToString("0", CultureInfo.InvariantCulture))
                        .Append("s carries over\n");
                    AppendQueue(sb, cover, realActive);
                }
                return;
            }
            Account(shield, now);
            bool blocks = settings.PaidShields && shield.RemainingSeconds > 0d && tc.buildingID != 0u;
            sb.Append("  Paid shield record: TC ").Append(shield.CupboardId)
                .Append(" saved building ").Append(shield.BuildingId)
                .Append(" current building ").Append(tc.buildingID)
                .Append(" total ").Append(Math.Max(0d, shield.RemainingSeconds).ToString("0", CultureInfo.InvariantCulture)).Append("s")
                .Append(" blocks ").Append(blocks ? "yes" : "no")
                .Append(" timer ").Append(!blocks ? "empty" : realActive ? "counting down until " + FormatEdge(end) : "waiting until " + FormatEdge(start))
                .Append('\n');
            AppendQueue(sb, shield, realActive);
        }

        private void AppendQueue(StringBuilder sb, Shield shield, bool realActive)
        {
            if (shield == null || shield.Queue == null || shield.Queue.Count == 0)
            {
                sb.Append("  Queue: empty\n");
                return;
            }
            sb.Append("  Queue: ").Append(realActive ? "active" : "waiting").Append('\n');
            int index = 1;
            for (int i = 0; i < shield.Queue.Count; i++)
            {
                ShieldEntry entry = shield.Queue[i];
                if (entry == null || entry.RemainingSeconds <= 0d) continue;
                ulong buyer = ResolveBuyer(shield, entry, null);
                sb.Append("  Entry ").Append(index)
                    .Append(" buyer ").Append(buyer)
                    .Append(" remaining ").Append(entry.RemainingSeconds.ToString("0", CultureInfo.InvariantCulture)).Append("s")
                    .Append(" (").Append(Format(TimeSpan.FromSeconds(Math.Max(0d, entry.RemainingSeconds)))).Append(")")
                    .Append(" ").Append(realActive ? "active" : "waiting")
                    .Append('\n');
                index++;
            }
        }

        private void AppendAuth(StringBuilder sb, BuildingPrivlidge tc)
        {
            int grace = Math.Max(0, EffectiveOfflineGraceMinutes());
            DateTime now = DateTime.UtcNow;
            sb.Append("  Grace required: ").Append(grace).Append(" min. Offline window: ")
                .Append(OfflineWindowOpen() ? "open" : "closed").Append('\n');
            if (tc == null || tc.authorizedPlayers == null || tc.authorizedPlayers.Count == 0)
            {
                sb.Append("  Authorized players: none\n");
                return;
            }
            bool any = false;
            bool ownerListed = false;
            foreach (ulong id in tc.authorizedPlayers)
            {
                if (!IsSteamId(id)) continue;
                any = true;
                if (id == tc.OwnerID) ownerListed = true;
                sb.Append("  ").Append(MemberLine(id, now, grace)).Append('\n');
            }
            if (!any) sb.Append("  Authorized players: none with a steam id\n");
            if (IsSteamId(tc.OwnerID) && !ownerListed)
                sb.Append("  TC owner ").Append(tc.OwnerID).Append(" is not on the auth list. ").Append(MemberLine(tc.OwnerID, now, grace)).Append('\n');
        }

        private string MemberLine(ulong id, DateTime now, int grace)
        {
            bool online = IsConnectedPlayer(id);
            float sleepSeconds;
            bool sleeping = TrySleepingSeconds(id, out sleepSeconds);
            long ticks;
            bool hasSeen = store.LastSeenUtcTicks.TryGetValue(id, out ticks);
            bool enough = NightshadeOfflineGate.OfflineLongEnough(online, sleeping, sleepSeconds, hasSeen, ticks, now, grace);
            string seen = "no last-seen record";
            string graceLeft = online ? "grace not started" : "no clock yet, not eligible";
            if (hasSeen && ticks > 0L)
            {
                DateTime when = new DateTime(ticks, DateTimeKind.Utc);
                double minutes = (now - when).TotalMinutes;
                if (minutes < 0d) minutes = 0d;
                seen = "last seen " + FormatEdge(when);
                if (!online)
                {
                    double left = grace - minutes;
                    graceLeft = left <= 0d ? "last-seen grace met" : "last-seen grace " + left.ToString("0.0", CultureInfo.InvariantCulture) + " min left";
                }
            }
            if (sleeping && !online)
            {
                double left = grace - (sleepSeconds / 60d);
                string sleepGrace = left <= 0d ? "sleeper grace met" : "sleeper grace " + left.ToString("0.0", CultureInfo.InvariantCulture) + " min left";
                graceLeft = graceLeft + "; " + sleepGrace;
            }
            string state = online ? "ONLINE" : "offline";
            string sleep = sleeping ? "sleeper " + sleepSeconds.ToString("0", CultureInfo.InvariantCulture) + "s" : "no sleeper";
            string eligible = enough ? "offline long enough" : "not offline long enough";
            return id.ToString() + " " + state + ", " + sleep + ", " + seen + ", " + graceLeft + ", " + eligible;
        }

        private void AppendCoveredNeighbors(StringBuilder sb, BuildingPrivlidge source)
        {
            if (source == null || source.transform == null)
            {
                sb.Append("  Covered TCs: cupboard has no position\n");
                return;
            }
            if (settings == null || !settings.ShareOverlappingCupboards)
            {
                sb.Append("  Covered TCs: overlap sharing is off\n");
                return;
            }
            DateTime now = DateTime.UtcNow;
            bool shieldOn = ShieldBlocks(source, source.buildingID, now);
            float cushion = PrivilegeRadius();
            if (source.buildingID == 0u || BuildingManager.server == null)
            {
                sb.Append("  Covered TCs: no building id\n");
                return;
            }
            List<OverlapNeighbor> neighbors = NeighborsOf(source.buildingID);
            int shown = 0;
            for (int n = 0; n < neighbors.Count; n++)
            {
                OverlapNeighbor neighbor = neighbors[n];
                if (!NightshadeOfflineGate.PrivilegeZonesOverlap(neighbor.ClosestBlockGap, cushion)) continue;
                BuildingManager.Building otherBuilding = BuildingManager.server.GetBuilding(neighbor.BuildingId);
                if (otherBuilding == null || otherBuilding.buildingPrivileges == null) continue;
                foreach (BuildingPrivlidge other in otherBuilding.buildingPrivileges)
                {
                    if (other == null || other.IsDestroyed) continue;
                    if (other.buildingID == 0u || other.buildingID == source.buildingID) continue;
                    shown++;
                    float distance = -1f;
                    if (source.transform != null && other.transform != null)
                        distance = Vector3.Distance(source.transform.position, other.transform.position);
                    bool authed = AnyShieldBuyerAuthed(source, other);
                    sb.Append("  Neighbor TC ").Append(CupboardId(other))
                        .Append(" building ").Append(other.buildingID)
                        .Append(" found by privilege overlap");
                    if (distance >= 0f)
                        sb.Append(" distance ").Append(distance.ToString("0.0", CultureInfo.InvariantCulture)).Append("m");
                    sb.Append(" closest blocks ").Append(neighbor.ClosestBlockGap.ToString("0.0", CultureInfo.InvariantCulture)).Append("m")
                        .Append(" authed ").Append(authed ? "yes" : "no")
                        .Append(" covered ").Append(shieldOn && authed ? "yes" : "no")
                        .Append('\n');
                }
            }
            if (shown == 0)
                sb.Append("  Covered TCs: no privilege overlap (one hop)\n");
        }

        private static string Format(TimeSpan t) => ((int)Math.Max(0, t.TotalHours)) + "h " + Math.Max(0, t.Minutes) + "m";
        private static void Reply(BasePlayer player, string message) => player.ChatMessage("<color=#b084ff>[Nightshade]</color> " + message);

        #region Window, ruleset, shields

        private void CheckWindow()
        {
            DateTime start, end;
            bool scheduled;
            bool active = EffectiveNow(NowUtc(), out start, out end, out scheduled);
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
                string when = FormatEdge(start) + " -> " + FormatEdge(end);
                if (forceFullPvp && !scheduled) when = "full PvP weekend force (scheduled window " + when + ")";
                else when = when + WindowNote();
                Puts((active ? "PvP window opened. " : "PvP window closed. ") + when);
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
            Account(shield, DateTime.UtcNow);
            double seconds = Math.Max(0d, shield.RemainingSeconds);
            if (seconds <= 0d) return NoneShield();
            // Time left blocks raid damage now, including before Friday 8 PM and during a force.
            // active here is only whether the real weekend is draining the stored seconds.
            double until = active ? 0d : Math.Max(0d, (start - DateTime.UtcNow).TotalSeconds);
            return new Dictionary<string, object>
            {
                ["state"] = active ? "active" : "queued",
                ["seconds"] = seconds,
                ["tcId"] = shield.CupboardId,
                ["buildingId"] = shield.BuildingId,
                ["ownerSteamId"] = shield.OwnerSteamId,
                ["draining"] = active,
                ["secondsUntilDrain"] = until,
                ["entries"] = EntrySeconds(shield)
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
                    PrintWarning("TruePVE Schedule.enabled is true. NightshadePVPWeekend turns it off. TruePVE 2.4.4's schedule uses the server local clock, not America/New_York, and it would replace the active ruleset every 30 seconds.");
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
            WarnFullForce(false);
            DateTime start, end;
            bool scheduled;
            bool active = EffectiveNow(NowUtc(), out start, out end, out scheduled);
            if (windowReady && active != windowActive) CheckWindow();
            else
            {
                EnsureRuleset();
                ApplyExcavator();
            }
        }

        private void EnsureRuleset()
        {
            if (applyingRuleset || settings == null) return;
            string path = Path.Combine(Interface.Oxide.ConfigDirectory, "TruePVE.json");
            bool skipFile = NightshadeWeekendClock.SkipRulesetFile(fakeUtc.HasValue, forceFullPvp);
            bool profilesChanged = !skipFile && InstallOwnedProfiles(path);
            if (!settings.DriveTruePve)
            {
                if (profilesChanged && TruePVE != null && TruePVE.IsLoaded)
                {
                    Server.Command(NightshadeWeekendClock.ReloadCommand);
                    Puts("Installed owned TruePVE rulesets and reloaded TruePVE. The Eastern window is not selecting the active ruleset.");
                }
                return;
            }
            string purgeRuleset = null;
            bool purgeActive = false;
            // Purge owns the ruleset for its whole span, including a weekend that falls inside a manual purge.
            if (settings.YieldToPurge && PurgeStatus != null && PurgeStatus.IsLoaded)
            {
                purgeRuleset = PurgeStatus.Call("API_GetActiveRulesetOverride") as string;
                purgeActive = !string.IsNullOrEmpty(purgeRuleset);
            }
            DateTime start, end;
            bool scheduled;
            bool active = EffectiveNow(NowUtc(), out start, out end, out scheduled);
            NightshadeWeekendClock.TruePveState state = NightshadeWeekendClock.Read(path);
            NightshadeWeekendClock.TogglePlan plan = NightshadeWeekendClock.Decide(state, appliedRuleset, purgeActive, purgeRuleset, active, settings.PvpRuleset, settings.PveRuleset, skipFile);
            if (!profilesChanged && (plan.Adopt || (!plan.WriteConfig && !plan.ReloadTruePve && !plan.RunRulesetCommand)))
            {
                appliedRuleset.Known = true;
                appliedRuleset.Ruleset = plan.Ruleset;
                return;
            }
            if (profilesChanged) plan.ReloadTruePve = true;
            bool loaded = TruePVE != null && TruePVE.IsLoaded;
            applyingRuleset = true;
            try
            {
                if (plan.WriteConfig) BackupTruePveOnce(path);
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
                bool reloaded = result.Reloaded;
                if (profilesChanged && loaded && !reloaded)
                {
                    Server.Command(NightshadeWeekendClock.ReloadCommand);
                    reloaded = true;
                }
                if (plan.ReloadTruePve && loaded && !reloaded) return;
                if (plan.RunRulesetCommand && loaded && !reloaded && !result.RanRulesetCommand) return;
                appliedRuleset.Known = true;
                appliedRuleset.Ruleset = plan.Ruleset;
                if (profilesChanged || result.Wrote || result.RanRulesetCommand || reloaded)
                    Puts("TruePVE " + plan.Reason + " -> " + plan.Ruleset
                        + (profilesChanged ? " (installed owned rulesets)" : "")
                        + (result.Wrote ? " (wrote Default RuleSet" + (plan.DisableSchedule ? ", schedule off" : "") + ")" : "")
                        + (result.RanRulesetCommand ? " via tpve.ruleset" : "")
                        + (reloaded ? " and reloaded TruePVE" : "")
                        + WindowNote()
                        + ".");
            }
            finally
            {
                applyingRuleset = false;
            }
        }

        // Upserts the weekday and weekend ruleset objects and their name mappings.
        // The original configs/TruePVE.json is copied once, before the first write.
        private bool InstallOwnedProfiles(string path)
        {
            if (settings == null || !settings.InstallOwnedRulesets) return false;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                PrintWarning("TruePVE.json was not found, so the owned rulesets were not installed.");
                return false;
            }
            JObject root;
            try { root = JObject.Parse(File.ReadAllText(path)); }
            catch (Exception ex)
            {
                PrintWarning("Could not read TruePVE.json: " + ex.Message);
                return false;
            }
            bool changed = NightshadeTruePveProfiles.Merge(root, SpecFrom(settings.WeekdayProfile), SpecFrom(settings.WeekendProfile));
            if (!changed) return false;
            try
            {
                BackupTruePveOnce(path);
                NightshadeTruePveProfiles.WriteRoot(path, root);
            }
            catch (Exception ex)
            {
                PrintWarning("Owned TruePVE rulesets were not saved: " + ex.Message);
                return false;
            }
            string weekday = settings.WeekdayProfile == null ? "" : settings.WeekdayProfile.Name;
            string weekend = settings.WeekendProfile == null ? "" : settings.WeekendProfile.Name;
            Puts("Wrote TruePVE rulesets " + weekday + " and " + weekend + " into configs/TruePVE.json (those RuleSets entries and their Mappings only).");
            return true;
        }

        private void BackupTruePveOnce(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            string backup = Path.Combine(Interface.Oxide.DataDirectory, "Nightshade", "NightshadePVPWeekend", "TruePVE.json.original");
            if (File.Exists(backup)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(backup));
            File.Copy(path, backup, false);
            Puts("Backed up configs/TruePVE.json to data/Nightshade/NightshadePVPWeekend/TruePVE.json.original.");
        }

        private string FormatEdge(DateTime utc)
        {
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), eastern);
            return local.ToString("ddd MMM d h:mm tt", CultureInfo.InvariantCulture) + " ET";
        }

        private string WindowNote()
        {
            if (forceFullPvp) return " (full PvP weekend force)";
            if (fakeUtc.HasValue) return " (test clock)";
            return "";
        }

        private string FullForceClockNote()
        {
            if (!forceFullPvp) return "";
            return " Full PvP weekend force is ON, so the server stays on the PvP weekend. The test clock does not turn that off.";
        }

        private void CmdWindow(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAdmin(arg)) return;
            DateTime start, end;
            bool scheduled;
            bool active = EffectiveNow(NowUtc(), out start, out end, out scheduled);
            DateTime realStart, realEnd;
            bool realActive;
            Weekend(DateTime.UtcNow, out realStart, out realEnd, out realActive);
            string purge = PurgeStatus != null && PurgeStatus.IsLoaded ? (PurgeStatus.Call("API_GetActiveRulesetOverride") as string ?? "inactive") : "(purge plugin not loaded)";
            bool purgeActive = purge != "inactive" && purge != "(purge plugin not loaded)";
            NightshadeWeekendClock.TruePveState state = NightshadeWeekendClock.Read(Path.Combine(Interface.Oxide.ConfigDirectory, "TruePVE.json"));
            NightshadeWeekendClock.TogglePlan plan = NightshadeWeekendClock.Plan(state, purgeActive, purgeActive ? purge : null, active, settings.PvpRuleset, settings.PveRuleset);
            // CurrentRuleSetName is private and not a HookMethod, so Carbon Call cannot read the live ruleset.
            // The file's Default RuleSet is what TruePVE loads and what Info used to treat as purge.
            double seconds = active && !scheduled ? -1d : SecondsUntilNow(start, end, scheduled);
            arg.ReplyWith("Window " + (active ? "ACTIVE" : "inactive")
                + " | scheduled " + (scheduled ? "active" : "inactive")
                + " | full force " + (forceFullPvp ? "ON" : "off")
                + " | real weekend " + (realActive ? "active" : "inactive")
                + " | " + FormatEdge(start) + " -> " + FormatEdge(end)
                + " | seconds " + (seconds < 0d ? "open-ended" : Math.Ceiling(seconds).ToString(CultureInfo.InvariantCulture))
                + " | target ruleset " + plan.Ruleset
                + " | file ruleset " + (state.Found ? (string.IsNullOrEmpty(state.Ruleset) ? "(blank)" : state.Ruleset) : "unreadable")
                + " | schedule " + (state.Found && state.ScheduleEnabled ? "ENABLED" : "off")
                + " | purge override " + purge
                + " | zone " + eastern.Id + (usedZoneFallback ? " (fallback)" : "")
                + " | test clock " + (fakeUtc.HasValue ? fakeUtc.Value.ToString("u") : "off")
                + " | combat tp " + CombatWindowText()
                + " | " + excavatorStatus);
        }

        // Args is string[] on some builds and Facepunch.StringView[] on others.
        // GetString returns each token as a string on both, so callers never take the array type.
        private static string[] ConsoleArgs(ConsoleSystem.Arg arg)
        {
            if (arg == null || !arg.HasArgs())
                return new string[0];
            int count = arg.Args.Length;
            string[] copy = new string[count];
            for (int i = 0; i < count; i++)
                copy[i] = arg.GetString(i);
            return copy;
        }

        private void CmdFakeTime(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAdmin(arg)) return;
            string[] args = ConsoleArgs(arg);
            if (args.Length == 0)
            {
                arg.ReplyWith(fakeUtc.HasValue
                    ? "Test clock is " + fakeUtc.Value.ToString("u") + " (" + FormatEdge(fakeUtc.Value) + "). nspvp.faketime clear"
                    : "Test clock is off. nspvp.faketime 2026-11-02T07:59:00Z   or   nspvp.faketime eastern 2026-10-30 20:00");
                return;
            }
            if (args[0].Equals("clear", StringComparison.OrdinalIgnoreCase) || args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                fakeUtc = null;
                CheckWindow();
                EnsureRuleset();
                PrintWarning("PvP test clock cleared. The live Eastern window is in use again." + FullForceClockNote());
                arg.ReplyWith("Test clock cleared.");
                return;
            }
            bool easternWall = args[0].Equals("eastern", StringComparison.OrdinalIgnoreCase) || args[0].Equals("et", StringComparison.OrdinalIgnoreCase);
            string text = easternWall ? string.Join(" ", args.Skip(1)) : string.Join(" ", args);
            DateTime parsed;
            if (easternWall)
            {
                if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed))
                {
                    arg.ReplyWith("Could not parse that Eastern time. Example: nspvp.faketime eastern 2026-10-30 20:00");
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
                    arg.ReplyWith("Could not parse that UTC time. Example: nspvp.faketime 2026-11-02T07:59:00Z");
                    return;
                }
                fakeUtc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }
            CheckWindow();
            EnsureRuleset();
            PrintWarning(forceFullPvp
                ? "PvP TEST CLOCK is " + fakeUtc.Value.ToString("u") + " (" + FormatEdge(fakeUtc.Value) + "). Full PvP weekend force is ON, so the server stays on the PvP weekend and TruePVE keeps the normal ruleset path. The test clock does not turn that off. Shield drain still uses the real clock."
                : "PvP TEST CLOCK is " + fakeUtc.Value.ToString("u") + " (" + FormatEdge(fakeUtc.Value) + "). TruePVE follows it in memory only. TruePVE.json is not changed. Shield drain still uses the real clock. The Giant Excavator boost follows this clock until nspvp.faketime clear.");
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
        // this is the configured grace. "nspvp.forcepvp on 1" replaces it until off or reload.
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
            PrintWarning("NightshadePVPWeekend TEST: forced PvP weekend is ON for offline protection only. Effective grace minutes: "
                + grace + ". Real weekend: " + (realActive ? "active" : "inactive")
                + ". This switch does not change TruePVE mode, TruePVE reloads, or Giant Excavator speed. "
                + (forceFullPvp ? "Full server force is also ON (nspvp.pvpweekend). " : "In memory only. ")
                + "Turn it off with nspvp.forcepvp off.");
        }

        private string ForcePvpStatus()
        {
            DateTime start, end;
            bool realActive;
            // Real clock on purpose. nspvp.faketime and the force switch must not change this line.
            Weekend(DateTime.UtcNow, out start, out end, out realActive);
            int grace = EffectiveOfflineGraceMinutes();
            if (grace < 0) grace = 0;
            bool configOn = settings != null && settings.ForcePvpWeekendForTesting;
            string full = forceFullPvp
                ? "\nFull server force is ON (nspvp.pvpweekend). That switch does change TruePVE, the excavator, and API_IsPvPActive."
                : "\nTruePVE, excavator speed, and API_IsPvPActive are unchanged by this protection-only switch. Full server force: nspvp.pvpweekend status.";
            return "Force PvP weekend (offline protection only): " + (forceOfflinePvp ? "ON" : "OFF")
                + "\nEffective grace minutes: " + grace
                + "\nReal weekend: " + (realActive ? "active" : "inactive")
                + full
                + "\nThis protection-only switch is not saved. A reload clears a command override"
                + (configOn
                    ? ". Config \"Force PvP weekend for testing\" is true, so a reload turns it on again."
                    : " (config key is false).");
        }

        // Console, RCON, and in-game admins.
        // on/off/status is the protection-only switch and does not write config.
        // "full" is the persisted server-wide force: it saves config and uses the normal TruePVE path.
        private string HandleForcePvp(string[] args)
        {
            if (args == null || args.Length == 0 || string.IsNullOrEmpty(args[0]))
                return "Usage: nspvp.forcepvp on [graceMinutes] | off | status    or    nspvp.forcepvp full on|off|status    or    nspvp.pvpweekend on|off|status";
            string action = args[0];
            if (action.Equals("full", StringComparison.OrdinalIgnoreCase))
                return HandleFullPvp(ShiftArgs(args));
            if (action.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length != 1)
                    return "Usage: nspvp.forcepvp status";
                return ForcePvpStatus();
            }
            if (action.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length != 1)
                    return "Usage: nspvp.forcepvp off";
                bool wasOn = forceOfflinePvp;
                forceOfflinePvp = false;
                forceOfflineGraceMinutes = null;
                nextForceOfflineWarning = 0f;
                if (wasOn)
                {
                    string extra = settings != null && settings.ForcePvpWeekendForTesting
                        ? " Config \"Force PvP weekend for testing\" is still true, so a reload turns this back on."
                        : "";
                    PrintWarning("NightshadePVPWeekend TEST: forced PvP weekend for offline protection is OFF. Offline protection follows the real Eastern weekend again. TruePVE and the Giant Excavator were not changed." + extra + (forceFullPvp ? " Full server force is still ON." : ""));
                }
                return ForcePvpStatus();
            }
            if (!action.Equals("on", StringComparison.OrdinalIgnoreCase))
                return "Usage: nspvp.forcepvp on [graceMinutes] | off | status    or    nspvp.forcepvp full on|off|status    or    nspvp.pvpweekend on|off|status";
            if (args.Length > 2)
                return "Usage: nspvp.forcepvp on [graceMinutes]";
            int? grace = null;
            if (args.Length == 2)
            {
                int minutes;
                if (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out minutes) || minutes < 0 || minutes > MaxForceGraceMinutes)
                    return "Grace minutes must be a whole number from 0 to 10080. Example: nspvp.forcepvp on 1";
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
            arg.ReplyWith(HandleForcePvp(ConsoleArgs(arg)));
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
            ReplyLines(player, report);
        }

        private void CmdPvpWeekend(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            // Server console and RCON have no in-game player. F1 still needs IsAdmin or the permission.
            if (!(arg.IsRcon || arg.Connection == null || ConsoleAdmin(arg))) return;
            arg.ReplyWith(HandleFullPvp(ConsoleArgs(arg)));
        }

        private void CmdNsRaidPvp(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, PermAdmin))
            {
                Reply(player, "No permission.");
                return;
            }
            ReplyLines(player, HandleFullPvp(args));
        }

        private static string[] ShiftArgs(string[] args)
        {
            if (args == null || args.Length <= 1) return new string[0];
            string[] rest = new string[args.Length - 1];
            for (int i = 1; i < args.Length; i++) rest[i - 1] = args[i];
            return rest;
        }

        private static void ReplyLines(BasePlayer player, string report)
        {
            if (player == null || report == null) return;
            // One chat line per fact. A single message with newlines is easy to clip in game.
            string[] lines = report.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                Reply(player, lines[i]);
            }
        }

        private void ApplyFullForce(bool on)
        {
            if (settings == null) settings = new Settings();
            forceFullPvp = on;
            settings.ForceFullPvpWeekend = on;
            nextFullForceWarning = 0f;
            SaveConfig();
            CheckWindow();
            EnsureRuleset();
        }

        private void DescribeTruePve(out bool realActive, out string mode, out string target, out string fileRuleset)
        {
            DateTime start, end;
            bool scheduled;
            bool effective = EffectiveNow(NowUtc(), out start, out end, out scheduled);
            Weekend(DateTime.UtcNow, out start, out end, out realActive);
            string purgeName = null;
            bool purgeActive = false;
            if (settings != null && settings.YieldToPurge && PurgeStatus != null && PurgeStatus.IsLoaded)
            {
                purgeName = PurgeStatus.Call("API_GetActiveRulesetOverride") as string;
                purgeActive = !string.IsNullOrEmpty(purgeName);
            }
            string pvp = settings != null && !string.IsNullOrEmpty(settings.PvpRuleset) ? settings.PvpRuleset : "nightshade_pvp_weekend";
            string pve = settings != null && !string.IsNullOrEmpty(settings.PveRuleset) ? settings.PveRuleset : "default";
            NightshadeWeekendClock.TruePveState state = NightshadeWeekendClock.Read(Path.Combine(Interface.Oxide.ConfigDirectory, "TruePVE.json"));
            NightshadeWeekendClock.TogglePlan plan = NightshadeWeekendClock.Plan(state, purgeActive, purgeActive ? purgeName : null, effective, pvp, pve);
            target = plan.Ruleset ?? "";
            if (plan.Reason == "purge") mode = "Purge";
            else if (plan.Reason == "weekend") mode = "PvP";
            else mode = "PvE";
            if (!state.Found) fileRuleset = "unreadable";
            else if (string.IsNullOrEmpty(state.Ruleset)) fileRuleset = "(blank)";
            else fileRuleset = state.Ruleset;
        }

        private string FullPvpStatus()
        {
            bool realActive;
            string mode;
            string target;
            string fileRuleset;
            DescribeTruePve(out realActive, out mode, out target, out fileRuleset);
            DateTime start, end;
            bool scheduled;
            bool effective = EffectiveNow(NowUtc(), out start, out end, out scheduled);
            bool saved = settings != null && settings.ForceFullPvpWeekend;
            return "Full PvP weekend force: " + (forceFullPvp ? "ON" : "OFF")
                + "\nReal weekend: " + (realActive ? "active" : "inactive")
                + "\nEffective TruePVE mode: " + mode + " (" + target + ")"
                + "\nScheduled window: " + (scheduled ? "active" : "inactive") + " (" + FormatEdge(start) + " -> " + FormatEdge(end) + ")"
                + "\nEffective weekend: " + (effective ? "active" : "inactive")
                + "\nConfig \"Force full PvP weekend\": " + (saved ? "true" : "false")
                + "\nTruePVE file ruleset: " + fileRuleset
                + "\nTest clock: " + (fakeUtc.HasValue ? "on" : "off")
                + "\nExcavator: " + (excavatorStatus ?? "excavator not checked")
                + "\nPaid shields block while they have time left. The timer counts down on the real weekend only.";
        }

        private string HandleFullPvp(string[] args)
        {
            if (args == null || args.Length != 1 || string.IsNullOrEmpty(args[0]))
                return "Usage: nspvp.pvpweekend on | off | status    or    nspvp.forcepvp full on|off|status";
            string action = args[0];
            if (action.Equals("status", StringComparison.OrdinalIgnoreCase))
                return FullPvpStatus();
            if (action.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                bool wasOn = forceFullPvp;
                ApplyFullForce(false);
                if (wasOn)
                {
                    bool realActive;
                    string mode;
                    string target;
                    string fileRuleset;
                    DescribeTruePve(out realActive, out mode, out target, out fileRuleset);
                    PrintWarning("NightshadePVPWeekend: FULL PvP weekend force is OFF. The Eastern schedule is in use again. Real weekend: "
                        + (realActive ? "active" : "inactive") + ". Effective TruePVE mode: " + mode + " (" + target + ").");
                }
                return FullPvpStatus();
            }
            if (!action.Equals("on", StringComparison.OrdinalIgnoreCase))
                return "Usage: nspvp.pvpweekend on | off | status    or    nspvp.forcepvp full on|off|status";
            ApplyFullForce(true);
            WarnFullForce(true);
            return FullPvpStatus();
        }

        private void WarnFullForce(bool immediate)
        {
            if (!forceFullPvp) return;
            float now = Time.realtimeSinceStartup;
            if (!immediate && now < nextFullForceWarning) return;
            nextFullForceWarning = now + ForceOfflineWarningSeconds;
            bool realActive;
            string mode;
            string target;
            string fileRuleset;
            DescribeTruePve(out realActive, out mode, out target, out fileRuleset);
            PrintWarning("NightshadePVPWeekend: FULL PvP weekend force is ON. Real weekend: "
                + (realActive ? "active" : "inactive") + ". Effective TruePVE mode: " + mode + " (" + target
                + "). Saved in config \"Force full PvP weekend\". Turn it off with nspvp.pvpweekend off.");
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
            Puts(text + WindowNote() + ".");
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
