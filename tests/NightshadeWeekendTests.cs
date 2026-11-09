using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using Oxide.Plugins;

// Regression tests for the Eastern weekend window and the TruePVE toggle path.
// Compile with NIGHTSHADE_RAID_TESTS so NightshadeRaidProtection.cs contributes
// NightshadeWeekendClock only. The RustPlugin type needs Unity assemblies that
// are not in this repo; Carbon compiles that type on the server.
public static class NightshadeWeekendTests
{
    static int failed;

    public static int Main(string[] args)
    {
        string configPath = args != null && args.Length > 0 ? args[0] : "carbon/configs/TruePVE.json";
        bool ianaFallback;
        TimeZoneInfo iana = NightshadeWeekendClock.ResolveEastern(out ianaFallback);
        Check(!ianaFallback, "America/New_York should resolve on this host");
        TimeZoneInfo fallback = NightshadeWeekendClock.FallbackEastern();

        TestEdges(iana, "iana");
        TestEdges(fallback, "fallback");
        TestZonesAgree(iana, fallback);
        TestScheduler(iana, new DateTime(2026, 11, 5, 12, 0, 0), new DateTime(2026, 11, 6, 20, 0, 0), new DateTime(2026, 11, 9, 3, 0, 0), "november weekday");
        TestScheduler(iana, new DateTime(2026, 10, 29, 12, 0, 0), new DateTime(2026, 10, 30, 20, 0, 0), new DateTime(2026, 11, 2, 3, 0, 0), "fall-back weekend");
        TestScheduler(iana, new DateTime(2026, 3, 5, 12, 0, 0), new DateTime(2026, 3, 6, 20, 0, 0), new DateTime(2026, 3, 9, 3, 0, 0), "spring-forward weekend");
        TestNearBoundary(iana);
        TestRestartCatchUp(iana);
        TestPlans(iana);
        TestApply();
        TestRepoConfig(configPath);

        if (failed == 0)
        {
            Console.WriteLine("Nightshade weekend tests passed.");
            return 0;
        }
        Console.Error.WriteLine(failed + " test(s) failed.");
        return 1;
    }

    static void TestEdges(TimeZoneInfo zone, string label)
    {
        // The reported morning: Monday 9 Nov 2026 03:00 Eastern is EST (DST ended 1 Nov).
        AssertEdge(zone, label, new DateTime(2026, 11, 6, 19, 59, 59), false, new DateTime(2026, 11, 6, 20, 0, 0), new DateTime(2026, 11, 9, 3, 0, 0));
        AssertEdge(zone, label, new DateTime(2026, 11, 6, 20, 0, 0), true, new DateTime(2026, 11, 6, 20, 0, 0), new DateTime(2026, 11, 9, 3, 0, 0));
        AssertEdge(zone, label, new DateTime(2026, 11, 8, 12, 0, 0), true, new DateTime(2026, 11, 6, 20, 0, 0), new DateTime(2026, 11, 9, 3, 0, 0));
        AssertEdge(zone, label, new DateTime(2026, 11, 9, 2, 59, 59), true, new DateTime(2026, 11, 6, 20, 0, 0), new DateTime(2026, 11, 9, 3, 0, 0));
        AssertEdge(zone, label, new DateTime(2026, 11, 9, 3, 0, 0), false, new DateTime(2026, 11, 13, 20, 0, 0), new DateTime(2026, 11, 16, 3, 0, 0));
        AssertEdge(zone, label, new DateTime(2026, 11, 9, 12, 47, 0), false, new DateTime(2026, 11, 13, 20, 0, 0), new DateTime(2026, 11, 16, 3, 0, 0));

        // Fall back: Friday 30 Oct 2026 20:00 is still EDT. Monday 2 Nov 03:00 is EST.
        AssertEdge(zone, label, new DateTime(2026, 10, 30, 20, 0, 0), true, new DateTime(2026, 10, 30, 20, 0, 0), new DateTime(2026, 11, 2, 3, 0, 0));
        AssertEdge(zone, label, new DateTime(2026, 11, 1, 1, 30, 0), true, new DateTime(2026, 10, 30, 20, 0, 0), new DateTime(2026, 11, 2, 3, 0, 0));
        AssertEdge(zone, label, new DateTime(2026, 11, 2, 2, 59, 59), true, new DateTime(2026, 10, 30, 20, 0, 0), new DateTime(2026, 11, 2, 3, 0, 0));
        AssertEdge(zone, label, new DateTime(2026, 11, 2, 3, 0, 0), false, new DateTime(2026, 11, 6, 20, 0, 0), new DateTime(2026, 11, 9, 3, 0, 0));
        DateTime fallStart = Wall(zone, new DateTime(2026, 10, 30, 20, 0, 0));
        DateTime fallEnd = Wall(zone, new DateTime(2026, 11, 2, 3, 0, 0));
        Check(Math.Abs((fallEnd - fallStart).TotalHours - 56d) < 0.001, label + " fall-back weekend is 56 real hours, got " + (fallEnd - fallStart).TotalHours.ToString("0.###", CultureInfo.InvariantCulture));

        // Spring forward: Friday 6 Mar 2026 20:00 is still EST. Monday 9 Mar 03:00 is EDT.
        AssertEdge(zone, label, new DateTime(2026, 3, 6, 20, 0, 0), true, new DateTime(2026, 3, 6, 20, 0, 0), new DateTime(2026, 3, 9, 3, 0, 0));
        AssertEdge(zone, label, new DateTime(2026, 3, 9, 2, 59, 59), true, new DateTime(2026, 3, 6, 20, 0, 0), new DateTime(2026, 3, 9, 3, 0, 0));
        AssertEdge(zone, label, new DateTime(2026, 3, 9, 3, 0, 0), false, new DateTime(2026, 3, 13, 20, 0, 0), new DateTime(2026, 3, 16, 3, 0, 0));
        DateTime springStart = Wall(zone, new DateTime(2026, 3, 6, 20, 0, 0));
        DateTime springEnd = Wall(zone, new DateTime(2026, 3, 9, 3, 0, 0));
        Check(Math.Abs((springEnd - springStart).TotalHours - 54d) < 0.001, label + " spring-forward weekend is 54 real hours, got " + (springEnd - springStart).TotalHours.ToString("0.###", CultureInfo.InvariantCulture));
        DateTime stdStart = Wall(zone, new DateTime(2026, 11, 6, 20, 0, 0));
        DateTime stdEnd = Wall(zone, new DateTime(2026, 11, 9, 3, 0, 0));
        Check(Math.Abs((stdEnd - stdStart).TotalHours - 55d) < 0.001, label + " November weekend is 55 real hours, got " + (stdEnd - stdStart).TotalHours.ToString("0.###", CultureInfo.InvariantCulture));
    }

    static void TestZonesAgree(TimeZoneInfo iana, TimeZoneInfo fallback)
    {
        DateTime[] walls = {
            new DateTime(2026, 11, 6, 20, 0, 0),
            new DateTime(2026, 11, 9, 2, 59, 59),
            new DateTime(2026, 11, 9, 3, 0, 0),
            new DateTime(2026, 10, 30, 20, 0, 0),
            new DateTime(2026, 11, 2, 3, 0, 0),
            new DateTime(2026, 3, 6, 20, 0, 0),
            new DateTime(2026, 3, 9, 3, 0, 0)
        };
        foreach (DateTime wall in walls)
        {
            DateTime a = Wall(iana, wall);
            DateTime b = Wall(fallback, wall);
            DateTime as1, ae1, bs1, be1;
            bool aa, ba;
            NightshadeWeekendClock.Evaluate(a, iana, out as1, out ae1, out aa);
            NightshadeWeekendClock.Evaluate(b, fallback, out bs1, out be1, out ba);
            Check(aa == ba, "zone agreement active at " + wall.ToString("u", CultureInfo.InvariantCulture));
            Check(as1 == bs1 && ae1 == be1, "zone agreement edges at " + wall.ToString("u", CultureInfo.InvariantCulture));
        }
    }

    static void TestScheduler(TimeZoneInfo zone, DateTime fromWall, DateTime startWall, DateTime endWall, string label)
    {
        DateTime start = Wall(zone, startWall);
        DateTime end = Wall(zone, endWall);
        DateTime t = Wall(zone, fromWall);
        bool sawActive = false;
        bool sawInactiveAfter = false;
        DateTime firstInactive = DateTime.MinValue;
        for (int i = 0; i < 20 && t < end.AddHours(6); i++)
        {
            DateTime windowStart, windowEnd;
            bool active;
            NightshadeWeekendClock.Evaluate(t, zone, out windowStart, out windowEnd, out active);
            if (t < start) Check(!active, label + " active before Friday 20:00 at " + t.ToString("u"));
            if (t >= end)
            {
                Check(!active, label + " still active after Monday 03:00 at " + t.ToString("u"));
                if (!sawInactiveAfter)
                {
                    sawInactiveAfter = true;
                    firstInactive = t;
                }
            }
            if (active) sawActive = true;
            double delay = NightshadeWeekendClock.BoundaryDelaySeconds(t, windowStart, windowEnd, active);
            float step = (float)delay;
            Check(step >= 1f, label + " delay under 1s");
            Check(step <= 86400f, label + " delay over a day");
            if (!active && t < start)
            {
                double raw = (start - t).TotalSeconds;
                if (raw <= 86400d) Check(Math.Abs(step - raw) < 0.05, label + " pre-start delay drifted");
            }
            t = t.AddSeconds(step);
        }
        Check(sawActive, label + " scheduler never entered the weekend");
        Check(sawInactiveAfter, label + " scheduler never observed Monday 03:00");
        if (sawInactiveAfter)
        {
            double late = (firstInactive - end).TotalSeconds;
            Check(late >= -0.001 && late <= 2d, label + " Monday close was " + late.ToString("0.###", CultureInfo.InvariantCulture) + "s off");
        }
    }

    static void TestNearBoundary(TimeZoneInfo zone)
    {
        DateTime end = Wall(zone, new DateTime(2026, 11, 9, 3, 0, 0));
        DateTime almost = end.AddSeconds(-0.4);
        DateTime windowStart, windowEnd;
        bool active;
        NightshadeWeekendClock.Evaluate(almost, zone, out windowStart, out windowEnd, out active);
        Check(active, "0.4s before Monday 03:00 should still be the weekend");
        double delay = NightshadeWeekendClock.BoundaryDelaySeconds(almost, windowStart, windowEnd, active);
        Check(delay == 1d, "sub-second remainder waits one second instead of spinning");
        DateTime landed = almost.AddSeconds((float)delay);
        NightshadeWeekendClock.Evaluate(landed, zone, out windowStart, out windowEnd, out active);
        Check(!active, "the one-second wait should land after Monday 03:00");
        Check((landed - end).TotalSeconds <= 2d, "the one-second wait skipped the close");
    }

    static void TestRestartCatchUp(TimeZoneInfo zone)
    {
        // Down across Monday 03:00, back at Monday afternoon. This is the reported miss.
        DateTime mondayAfternoon = Wall(zone, new DateTime(2026, 11, 9, 12, 47, 0));
        var state = new NightshadeWeekendClock.TruePveState { Found = true, Ruleset = "purge", ScheduleEnabled = false };
        NightshadeWeekendClock.TogglePlan plan = PlanAt(zone, mondayAfternoon, state, false, null);
        Check(plan.Reason == "weekday" && plan.Ruleset == "default", "restart after Monday 03:00 still targets purge");
        Check(plan.WriteConfig && plan.ReloadTruePve && plan.RunRulesetCommand, "restart catch-up must rewrite and reload TruePVE");

        DateTime during = Wall(zone, new DateTime(2026, 11, 8, 15, 0, 0));
        state.Ruleset = "default";
        plan = PlanAt(zone, during, state, false, null);
        Check(plan.Reason == "weekend" && plan.Ruleset == "nightshade_pvp_weekend" && plan.WriteConfig, "restart during the weekend must open PvP");

        DateTime exactEnd = Wall(zone, new DateTime(2026, 11, 9, 3, 0, 0));
        state.Ruleset = "nightshade_pvp_weekend";
        plan = PlanAt(zone, exactEnd, state, false, null);
        Check(plan.Reason == "weekday" && plan.Ruleset == "default" && plan.WriteConfig, "exact Monday 03:00 must leave the weekend ruleset");

        DateTime exactStart = Wall(zone, new DateTime(2026, 11, 6, 20, 0, 0));
        state.Ruleset = "default";
        plan = PlanAt(zone, exactStart, state, false, null);
        Check(plan.Reason == "weekend" && plan.Ruleset == "nightshade_pvp_weekend", "exact Friday 20:00 must enter the weekend");
    }

    static void TestPlans(TimeZoneInfo zone)
    {
        var purgeFile = new NightshadeWeekendClock.TruePveState { Found = true, Ruleset = "purge", ScheduleEnabled = false };
        var weekendFile = new NightshadeWeekendClock.TruePveState { Found = true, Ruleset = "nightshade_pvp_weekend", ScheduleEnabled = false };
        var pveFile = new NightshadeWeekendClock.TruePveState { Found = true, Ruleset = "default", ScheduleEnabled = false };
        var scheduled = new NightshadeWeekendClock.TruePveState { Found = true, Ruleset = "default", ScheduleEnabled = true };

        DateTime monday = Wall(zone, new DateTime(2026, 11, 9, 12, 0, 0));
        DateTime fridayNight = Wall(zone, new DateTime(2026, 11, 6, 21, 0, 0));

        NightshadeWeekendClock.TogglePlan plan = PlanAt(zone, fridayNight, pveFile, true, "purge");
        Check(plan.Ruleset == "purge" && plan.Reason == "purge" && plan.WriteConfig && plan.ReloadTruePve, "purge must replace a weekday file during the weekend");

        plan = PlanAt(zone, fridayNight, purgeFile, true, "purge");
        Check(plan.Ruleset == "purge" && !plan.WriteConfig && !plan.ReloadTruePve && plan.RunRulesetCommand, "an already-purge file during purge must not reload");

        plan = PlanAt(zone, monday, weekendFile, true, "purge");
        Check(plan.Ruleset == "purge" && plan.WriteConfig, "purge on a weekday must beat a stale weekend ruleset");

        plan = PlanAt(zone, monday, purgeFile, false, null);
        Check(plan.Ruleset == "default" && plan.WriteConfig && plan.ReloadTruePve, "inactive purge left on the file must be cleared after Monday 03:00");

        plan = PlanAt(zone, monday, weekendFile, false, null);
        Check(plan.Ruleset == "default" && plan.WriteConfig, "purge end restoring the weekend ruleset on Monday must be corrected");

        plan = PlanAt(zone, fridayNight, pveFile, false, null);
        Check(plan.Ruleset == "nightshade_pvp_weekend" && plan.WriteConfig, "purge end restoring default during the weekend must be corrected");

        plan = PlanAt(zone, monday, scheduled, false, null);
        Check(plan.Ruleset == "default" && plan.DisableSchedule && plan.WriteConfig && plan.ReloadTruePve, "TruePVE's own schedule must be forced off");

        plan = PlanAt(zone, fridayNight, purgeFile, true, "purge");
        purgeFile.ScheduleEnabled = true;
        plan = PlanAt(zone, fridayNight, purgeFile, true, "purge");
        Check(plan.Ruleset == "purge" && plan.DisableSchedule && plan.WriteConfig, "schedule must be disabled without dropping an active purge");
        purgeFile.ScheduleEnabled = false;

        plan = PlanAt(zone, monday, pveFile, false, null);
        Check(!plan.WriteConfig && !plan.ReloadTruePve && plan.RunRulesetCommand && plan.Ruleset == "default", "matching weekday file must not reload");
    }

    static void TestApply()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nightshade-weekend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "TruePVE.json");
            File.WriteAllText(path, SampleConfig("purge", true));
            var calls = new List<string>();
            NightshadeWeekendClock.TruePveState state = NightshadeWeekendClock.Read(path);
            var plan = NightshadeWeekendClock.Plan(state, false, null, false, "nightshade_pvp_weekend", "default");
            NightshadeWeekendClock.ApplyResult result = NightshadeWeekendClock.Apply(path, plan, true, (cmd, args) => calls.Add(FormatCall(cmd, args)));
            Check(result.Wrote && result.RanRulesetCommand && result.Reloaded && string.IsNullOrEmpty(result.Error), "weekday apply did not write, command, and reload");
            Check(calls.Count == 2 && calls[0] == "tpve.ruleset default" && calls[1] == "c.reload TruePVE", "weekday commands were " + string.Join(" | ", calls.ToArray()));
            state = NightshadeWeekendClock.Read(path);
            Check(state.Found && state.Ruleset == "default" && !state.ScheduleEnabled, "file was not switched to default with the schedule off");
            JObject root = JObject.Parse(File.ReadAllText(path));
            Check((string)root["Mappings"]["airfield_1"] == "monumentpvp", "monument mapping was dropped");
            Check(root["RuleSets"][0]["name"].ToString() == "purge", "purge ruleset definition was dropped");

            calls.Clear();
            plan = NightshadeWeekendClock.Plan(state, false, null, true, "nightshade_pvp_weekend", "default");
            result = NightshadeWeekendClock.Apply(path, plan, true, (cmd, args) => calls.Add(FormatCall(cmd, args)));
            Check(result.Wrote && calls.Count == 2 && calls[0] == "tpve.ruleset nightshade_pvp_weekend" && calls[1] == "c.reload TruePVE", "weekend open did not toggle TruePVE");
            state = NightshadeWeekendClock.Read(path);
            Check(state.Ruleset == "nightshade_pvp_weekend" && !state.ScheduleEnabled, "weekend ruleset was not saved");

            calls.Clear();
            plan = NightshadeWeekendClock.Plan(state, true, "purge", true, "nightshade_pvp_weekend", "default");
            result = NightshadeWeekendClock.Apply(path, plan, true, (cmd, args) => calls.Add(FormatCall(cmd, args)));
            Check(result.Wrote && calls[0] == "tpve.ruleset purge", "purge did not take the file back from the weekend");
            root = JObject.Parse(File.ReadAllText(path));
            Check((string)root["Default RuleSet"] == "purge" && (string)root["Mappings"]["airfield_1"] == "monumentpvp", "purge write changed monument mapping");

            calls.Clear();
            state = NightshadeWeekendClock.Read(path);
            // Put the file on the weekday ruleset with the schedule already off, then apply the same plan.
            root["Default RuleSet"] = "default";
            ((JObject)root["Schedule"])["enabled"] = false;
            File.WriteAllText(path, root.ToString());
            state = NightshadeWeekendClock.Read(path);
            plan = NightshadeWeekendClock.Plan(state, false, null, false, "nightshade_pvp_weekend", "default");
            result = NightshadeWeekendClock.Apply(path, plan, true, (cmd, args) => calls.Add(FormatCall(cmd, args)));
            Check(!result.Wrote && !result.Reloaded && result.RanRulesetCommand && calls.Count == 1 && calls[0] == "tpve.ruleset default", "in-memory command ran without a needless reload");

            calls.Clear();
            File.Delete(path);
            plan.WriteConfig = true;
            plan.ReloadTruePve = true;
            plan.Ruleset = "default";
            result = NightshadeWeekendClock.Apply(path, plan, true, (cmd, args) => calls.Add(FormatCall(cmd, args)));
            Check(!string.IsNullOrEmpty(result.Error) && !result.Wrote && !result.Reloaded && calls.Count == 1 && calls[0] == "tpve.ruleset default", "missing file must not reload the old ruleset");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static void TestRepoConfig(string path)
    {
        Check(File.Exists(path), "TruePVE config not found at " + path);
        if (!File.Exists(path)) return;
        NightshadeWeekendClock.TruePveState state = NightshadeWeekendClock.Read(path);
        Check(state.Found && state.Ruleset == "default", "committed TruePVE Default RuleSet is " + state.Ruleset + " (purge was the stuck PvP ruleset)");
        Check(!state.ScheduleEnabled, "committed TruePVE schedule is enabled and would fight the Eastern window");
        JObject root = JObject.Parse(File.ReadAllText(path));
        Check((string)root["Mappings"]["airfield_1"] == "monumentpvp", "monument mapping missing from TruePVE config");
        bool sawWeekend = false;
        foreach (JToken ruleset in root["RuleSets"])
        {
            if ((string)ruleset["name"] == "nightshade_pvp_weekend") sawWeekend = true;
        }
        Check(sawWeekend, "nightshade_pvp_weekend ruleset is missing");
    }

    static NightshadeWeekendClock.TogglePlan PlanAt(TimeZoneInfo zone, DateTime utc, NightshadeWeekendClock.TruePveState state, bool purgeActive, string purgeRuleset)
    {
        DateTime start, end;
        bool active;
        NightshadeWeekendClock.Evaluate(utc, zone, out start, out end, out active);
        return NightshadeWeekendClock.Plan(state, purgeActive, purgeRuleset, active, "nightshade_pvp_weekend", "default");
    }

    static void AssertEdge(TimeZoneInfo zone, string label, DateTime wall, bool expectActive, DateTime expectStartWall, DateTime expectEndWall)
    {
        DateTime utc = Wall(zone, wall);
        DateTime start, end;
        bool active;
        NightshadeWeekendClock.Evaluate(utc, zone, out start, out end, out active);
        DateTime expectStart = Wall(zone, expectStartWall);
        DateTime expectEnd = Wall(zone, expectEndWall);
        string when = label + " " + wall.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Check(active == expectActive, when + " active=" + active);
        Check(start == expectStart, when + " start " + start.ToString("u") + " != " + expectStart.ToString("u"));
        Check(end == expectEnd, when + " end " + end.ToString("u") + " != " + expectEnd.ToString("u"));
    }

    static DateTime Wall(TimeZoneInfo zone, DateTime wall)
    {
        return NightshadeWeekendClock.ToUtc(wall, zone);
    }

    static string SampleConfig(string ruleset, bool scheduleEnabled)
    {
        return "{\n"
            + "  \"Default RuleSet\": \"" + ruleset + "\",\n"
            + "  \"Schedule\": { \"enabled\": " + (scheduleEnabled ? "true" : "false") + ", \"useRealtime\": true, \"entries\": [ \"1.03:00:00 default\", \"5.20:00:00 nightshade_pvp_weekend\" ] },\n"
            + "  \"Mappings\": { \"airfield_1\": \"monumentpvp\", \"purge\": \"purge\" },\n"
            + "  \"RuleSets\": [ { \"name\": \"purge\" }, { \"name\": \"default\" }, { \"name\": \"nightshade_pvp_weekend\" } ]\n"
            + "}\n";
    }

    static string FormatCall(string command, object[] args)
    {
        if (args == null || args.Length == 0) return command;
        return command + " " + args[0];
    }

    static void Check(bool condition, string message)
    {
        if (condition) return;
        failed++;
        Console.Error.WriteLine("FAIL " + message);
    }
}
