using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using Oxide.Plugins;

// Regression tests for the Eastern weekend window and the TruePVE toggle path.
// Compile with NIGHTSHADE_RAID_TESTS so NightshadeRaidProtection.cs contributes
// NightshadeWeekendClock and NightshadeExcavatorBoost only. The RustPlugin type needs Unity assemblies that
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
        TestReloadBudget(iana);
        TestApply();
        TestRepoConfig(configPath);
        TestExcavator();
        TestExcavatorConfig(configPath);

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
        Check(plan.Ruleset == "purge" && !plan.WriteConfig && !plan.ReloadTruePve && !plan.RunRulesetCommand, "an already-purge file during purge must not reload or rewrite");

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
        Check(!plan.WriteConfig && !plan.ReloadTruePve && !plan.RunRulesetCommand && plan.Ruleset == "default", "matching weekday file must not reload or command");
    }

    static void TestReloadBudget(TimeZoneInfo zone)
    {
        string dir = Path.Combine(Path.GetTempPath(), "nightshade-reload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "TruePVE.json");
            File.WriteAllText(path, SampleConfig("default", false));
            var tracked = new NightshadeWeekendClock.TrackedRuleset();
            int reloads = 0;
            int commands = 0;

            // Load onto a file that already matches. No reload, and repeats stay quiet.
            reloads += Ticks(zone, path, new DateTime(2026, 11, 9, 12, 0, 0), false, null, false, 5, ref tracked, ref commands);
            Check(reloads == 0 && commands == 0, "steady weekday reloaded or commanded, reloads " + reloads + " commands " + commands);

            // Weekend edge: one reload, then the same mode does nothing.
            int before = reloads;
            reloads += Ticks(zone, path, new DateTime(2026, 11, 6, 20, 0, 0), false, null, false, 1, ref tracked, ref commands);
            Check(reloads == before + 1, "weekend start reloads " + (reloads - before));
            before = reloads;
            int commandsBefore = commands;
            reloads += Ticks(zone, path, new DateTime(2026, 11, 8, 15, 0, 0), false, null, false, 4, ref tracked, ref commands);
            Check(reloads == before && commands == commandsBefore, "repeated weekend ticks reloaded or commanded");

            // Monday close: one reload back to default.
            before = reloads;
            reloads += Ticks(zone, path, new DateTime(2026, 11, 9, 3, 0, 0), false, null, false, 1, ref tracked, ref commands);
            Check(reloads == before + 1, "weekend end reloads " + (reloads - before));
            before = reloads;
            commandsBefore = commands;
            reloads += Ticks(zone, path, new DateTime(2026, 11, 9, 12, 0, 0), false, null, false, 4, ref tracked, ref commands);
            Check(reloads == before && commands == commandsBefore, "repeated weekday ticks reloaded or commanded");

            // Purge start and end are one reload each.
            before = reloads;
            reloads += Ticks(zone, path, new DateTime(2026, 11, 9, 18, 0, 0), true, "purge", false, 1, ref tracked, ref commands);
            Check(reloads == before + 1, "purge start reloads " + (reloads - before));
            before = reloads;
            commandsBefore = commands;
            reloads += Ticks(zone, path, new DateTime(2026, 11, 10, 12, 0, 0), true, "purge", false, 3, ref tracked, ref commands);
            Check(reloads == before && commands == commandsBefore, "repeated purge ticks reloaded or commanded");
            before = reloads;
            reloads += Ticks(zone, path, new DateTime(2026, 11, 10, 12, 0, 0), false, null, false, 1, ref tracked, ref commands);
            Check(reloads == before + 1, "purge end reloads " + (reloads - before));

            // Another plugin reloads TruePVE and leaves a stale Default RuleSet.
            NightshadeWeekendClock.TruePveState stale = NightshadeWeekendClock.Read(path);
            Check(stale.Ruleset == "default", "purge end did not restore default before the mismatch test");
            JObject root = JObject.Parse(File.ReadAllText(path));
            root["Default RuleSet"] = "nightshade_pvp_weekend";
            ((JObject)root["Schedule"])["enabled"] = true;
            File.WriteAllText(path, root.ToString());
            before = reloads;
            reloads += Ticks(zone, path, new DateTime(2026, 11, 10, 12, 0, 0), false, null, false, 1, ref tracked, ref commands);
            Check(reloads == before + 1, "stale file mismatch reloads " + (reloads - before));
            stale = NightshadeWeekendClock.Read(path);
            Check(stale.Ruleset == "default" && !stale.ScheduleEnabled, "mismatch repair did not restore default with the schedule off");
            before = reloads;
            commandsBefore = commands;
            reloads += Ticks(zone, path, new DateTime(2026, 11, 10, 13, 0, 0), false, null, false, 4, ref tracked, ref commands);
            Check(reloads == before && commands == commandsBefore, "ticks after the mismatch repair reloaded or commanded");

            // Test clock moves memory only, once per target, and never reloads.
            before = reloads;
            commandsBefore = commands;
            reloads += Ticks(zone, path, new DateTime(2026, 11, 6, 21, 0, 0), false, null, true, 1, ref tracked, ref commands);
            Check(reloads == before && commands == commandsBefore + 1, "faketime did not push one in-memory ruleset");
            stale = NightshadeWeekendClock.Read(path);
            Check(stale.Ruleset == "default", "faketime wrote TruePVE.json");
            commandsBefore = commands;
            reloads += Ticks(zone, path, new DateTime(2026, 11, 7, 12, 0, 0), false, null, true, 3, ref tracked, ref commands);
            Check(reloads == before && commands == commandsBefore, "repeated faketime ticks commanded again");
            reloads += Ticks(zone, path, new DateTime(2026, 11, 10, 13, 0, 0), false, null, false, 1, ref tracked, ref commands);
            Check(reloads == before && commands == commandsBefore + 1, "clearing faketime reloaded or skipped the memory restore");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
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
            root["Default RuleSet"] = "default";
            ((JObject)root["Schedule"])["enabled"] = false;
            File.WriteAllText(path, root.ToString());
            state = NightshadeWeekendClock.Read(path);
            plan = NightshadeWeekendClock.Plan(state, false, null, false, "nightshade_pvp_weekend", "default");
            result = NightshadeWeekendClock.Apply(path, plan, true, (cmd, args) => calls.Add(FormatCall(cmd, args)));
            Check(!result.Wrote && !result.Reloaded && !result.RanRulesetCommand && calls.Count == 0, "a matching file must not write, command, or reload");

            calls.Clear();
            File.Delete(path);
            plan.WriteConfig = true;
            plan.ReloadTruePve = true;
            plan.RunRulesetCommand = true;
            plan.Ruleset = "default";
            result = NightshadeWeekendClock.Apply(path, plan, true, (cmd, args) => calls.Add(FormatCall(cmd, args)));
            Check(!string.IsNullOrEmpty(result.Error) && !result.Wrote && !result.Reloaded && calls.Count == 1 && calls[0] == "tpve.ruleset default", "missing file must not reload the old ruleset");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static void TestExcavator()
    {
        NightshadeExcavatorBoost.Baseline vanilla = NightshadeExcavatorBoost.Defaults();
        Check(vanilla.TickRate == 3f && vanilla.FullResourcesSeconds == 120f && vanilla.FuelSeconds == 120f, "ctor defaults are not 3/120/120");

        NightshadeExcavatorBoost.Target boost = NightshadeExcavatorBoost.TargetFrom(vanilla, true, true, 2f, true);
        Check(boost.Applied && NightshadeExcavatorBoost.Near(boost.TickRate, 3f) && NightshadeExcavatorBoost.Near(boost.FullResourcesSeconds, 60f) && NightshadeExcavatorBoost.Near(boost.FuelSeconds, 60f), "2x keep-yield should be tick 3, full 60, fuel 60");

        NightshadeExcavatorBoost.Target fasterOnly = NightshadeExcavatorBoost.TargetFrom(vanilla, true, true, 2f, false);
        Check(fasterOnly.Applied && NightshadeExcavatorBoost.Near(fasterOnly.FullResourcesSeconds, 60f) && NightshadeExcavatorBoost.Near(fasterOnly.FuelSeconds, 120f), "2x without keep-yield must leave the diesel at 120s");

        NightshadeExcavatorBoost.Target closed = NightshadeExcavatorBoost.TargetFrom(vanilla, false, true, 2f, true);
        Check(!closed.Applied && NightshadeExcavatorBoost.Near(closed.FullResourcesSeconds, 120f) && NightshadeExcavatorBoost.Near(closed.FuelSeconds, 120f), "closed window must be vanilla");

        NightshadeExcavatorBoost.Target disabled = NightshadeExcavatorBoost.TargetFrom(vanilla, true, false, 2f, true);
        Check(!disabled.Applied && NightshadeExcavatorBoost.Near(disabled.FuelSeconds, 120f), "disabled config must be vanilla");

        NightshadeExcavatorBoost.Target same = NightshadeExcavatorBoost.TargetFrom(vanilla, true, true, 1f, true);
        Check(!same.Applied, "multiplier 1 is not a boost");

        NightshadeExcavatorBoost.Target fromNaN = NightshadeExcavatorBoost.TargetFrom(vanilla, true, true, float.NaN, true);
        Check(fromNaN.Applied && NightshadeExcavatorBoost.Near(fromNaN.FullResourcesSeconds, 60f), "invalid multiplier should fall back to 2");

        var custom = new NightshadeExcavatorBoost.Baseline { TickRate = 3f, FullResourcesSeconds = 90f, FuelSeconds = 100f };
        NightshadeExcavatorBoost.Target customBoost = NightshadeExcavatorBoost.TargetFrom(custom, true, true, 2f, true);
        Check(NightshadeExcavatorBoost.Near(customBoost.FullResourcesSeconds, 45f) && NightshadeExcavatorBoost.Near(customBoost.FuelSeconds, 50f) && NightshadeExcavatorBoost.Near(customBoost.TickRate, 3f), "custom vanilla should scale and keep the tick");

        float half = NightshadeExcavatorBoost.ScaleFuel(90f, 120f, 60f);
        float back = NightshadeExcavatorBoost.ScaleFuel(half, 60f, 120f);
        Check(NightshadeExcavatorBoost.Near(half, 45f), "90s of a 120s diesel should become 45s at 60s");
        Check(NightshadeExcavatorBoost.Near(back, 90f), "scaling back should return the same diesel fraction");
        Check(NightshadeExcavatorBoost.ScaleFuel(0f, 120f, 60f) == 0f, "an empty fuel timer must not gain a free diesel");
        Check(NightshadeExcavatorBoost.Near(NightshadeExcavatorBoost.ScaleFuel(45f, 60f, 60f), 45f), "same rate must not scale again");
        Check(NightshadeExcavatorBoost.ScaleFuel(-5f, 120f, 60f) == 0f, "negative fuel time should clamp to zero");

        NightshadeExcavatorBoost.Baseline leftover = NightshadeExcavatorBoost.Capture(3f, 60f, 60f, 2f, true);
        Check(NightshadeExcavatorBoost.Near(leftover.FullResourcesSeconds, 120f) && NightshadeExcavatorBoost.Near(leftover.FuelSeconds, 120f), "a leftover 60/60 boost must not be captured as vanilla");
        NightshadeExcavatorBoost.Baseline liveVanilla = NightshadeExcavatorBoost.Capture(3f, 120f, 120f, 2f, true);
        Check(NightshadeExcavatorBoost.Near(liveVanilla.FullResourcesSeconds, 120f) && NightshadeExcavatorBoost.Near(liveVanilla.FuelSeconds, 120f), "live 120/120 should stay vanilla");
        NightshadeExcavatorBoost.Baseline customLive = NightshadeExcavatorBoost.Capture(3f, 90f, 100f, 2f, true);
        Check(NightshadeExcavatorBoost.Near(customLive.FullResourcesSeconds, 90f) && NightshadeExcavatorBoost.Near(customLive.FuelSeconds, 100f), "custom live rates should be stored");
        NightshadeExcavatorBoost.Baseline fuelOnly = NightshadeExcavatorBoost.Capture(3f, 120f, 60f, 2f, true);
        Check(NightshadeExcavatorBoost.Near(fuelOnly.FuelSeconds, 60f) && NightshadeExcavatorBoost.Near(fuelOnly.FullResourcesSeconds, 120f), "fuel 60 with vanilla production is not the boosted triple");

        Check(!NightshadeExcavatorBoost.ShouldRestartMining(true, 3f, 3f), "an unchanged 3s tick must not restart BeginMining");
        Check(NightshadeExcavatorBoost.ShouldRestartMining(true, 1f, 3f), "a changed tick on a running arm should restart");
        Check(!NightshadeExcavatorBoost.ShouldRestartMining(false, 1f, 3f), "a stopped arm has no invoke to restart");
        Check(NightshadeExcavatorBoost.Near(NightshadeExcavatorBoost.EnginePairMeters, 50f), "engine pair distance drifted from the 20m broadcast plus margin");
    }

    static void TestExcavatorConfig(string truePvePath)
    {
        string path = Path.Combine(Path.GetDirectoryName(truePvePath) ?? "", "NightshadeRaidProtection.json");
        Check(File.Exists(path), "NightshadeRaidProtection.json not found next to " + truePvePath);
        if (!File.Exists(path)) return;
        JObject root = JObject.Parse(File.ReadAllText(path));
        JObject excavator = root["Excavator"] as JObject;
        Check(excavator != null, "Excavator config block missing");
        if (excavator == null) return;
        Check(excavator["Enabled"] != null && (bool)excavator["Enabled"], "Excavator.Enabled should default on");
        Check(excavator["SpeedMultiplier"] != null && Math.Abs((double)excavator["SpeedMultiplier"] - 2d) < 0.001, "Excavator.SpeedMultiplier should be 2");
        Check(excavator["KeepYieldPerDiesel"] != null && (bool)excavator["KeepYieldPerDiesel"], "Excavator.KeepYieldPerDiesel should default on");
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

    static int Ticks(TimeZoneInfo zone, string path, DateTime wall, bool purgeActive, string purgeRuleset, bool fakeTime, int times, ref NightshadeWeekendClock.TrackedRuleset tracked, ref int commands)
    {
        int reloads = 0;
        for (int i = 0; i < times; i++)
        {
            DateTime start, end;
            bool weekend;
            NightshadeWeekendClock.Evaluate(Wall(zone, wall), zone, out start, out end, out weekend);
            NightshadeWeekendClock.TruePveState file = NightshadeWeekendClock.Read(path);
            NightshadeWeekendClock.TogglePlan plan = NightshadeWeekendClock.Decide(file, tracked, purgeActive, purgeRuleset, weekend, "nightshade_pvp_weekend", "default", fakeTime);
            if (plan.Adopt || (!plan.WriteConfig && !plan.ReloadTruePve && !plan.RunRulesetCommand))
            {
                tracked.Known = true;
                tracked.Ruleset = plan.Ruleset;
                continue;
            }
            var calls = new List<string>();
            NightshadeWeekendClock.ApplyResult result = NightshadeWeekendClock.Apply(path, plan, true, (cmd, args) => calls.Add(FormatCall(cmd, args)));
            commands += calls.Count;
            if (result.Reloaded) reloads++;
            if (string.IsNullOrEmpty(result.Error))
            {
                tracked.Known = true;
                tracked.Ruleset = plan.Ruleset;
            }
        }
        return reloads;
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
