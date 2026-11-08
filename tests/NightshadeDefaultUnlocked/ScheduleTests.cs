using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Oxide.Plugins;

internal static class ScheduleTests
{
    private const string Offline = "76561198000000001";
    private const string Online = "76561198000000002";
    private const string Future = "76561198000000003";
    private const string Vip = "76561198000000004";
    private const string DefaultOnly = "76561198000000005";

    private static int failed;

    private static int Main()
    {
        NightshadeDefaultUnlockedSchedule.Rules rules = NightshadeDefaultUnlockedSchedule.Rules.NightshadeDefaults();
        bool systemFallback;
        TimeZoneInfo zone = NightshadeDefaultUnlockedSchedule.ResolveZone("America/New_York", out systemFallback);
        Eq("host has America/New_York", systemFallback, false);

        FirstThursdays(zone, rules);
        MondayBoundary(zone, rules);
        BeforeWipeUsesPreviousCycle(zone, rules);
        MonthAndYearRollover(zone, rules);
        DstTransitions(zone, rules);
        FallbackZoneMatches(zone, rules);
        DowntimeAndReload(zone, rules);
        Membership(rules);
        WakeTimer();
        SourceDoesNotTouchOtherPermissions();
        CheckedInGroupsDoNotInheritUnlocked();

        if (failed == 0)
        {
            Console.WriteLine("All NightshadeDefaultUnlocked schedule tests passed.");
            return 0;
        }
        Console.Error.WriteLine(failed + " test(s) failed.");
        return 1;
    }

    private static void FirstThursdays(TimeZoneInfo zone, NightshadeDefaultUnlockedSchedule.Rules rules)
    {
        int[] days2026 = { 1, 5, 5, 2, 7, 4, 2, 6, 3, 1, 5, 3 };
        for (int month = 1; month <= 12; month++)
        {
            DateTime wall = NightshadeDefaultUnlockedSchedule.NthWeekday(2026, month, DayOfWeek.Thursday, 1, 14, 0);
            Eq("2026-" + month + " first Thursday day", wall.Day, days2026[month - 1]);
            Eq("2026-" + month + " weekday", wall.DayOfWeek, DayOfWeek.Thursday);
            Eq("2026-" + month + " hour", wall.Hour, 14);
            DateTime atWipe = NightshadeDefaultUnlockedSchedule.ToUtc(wall, zone);
            var cycle = NightshadeDefaultUnlockedSchedule.Evaluate(atWipe, zone, rules);
            Eq("2026-" + month + " closed at wipe", cycle.Open, false);
            Eq("2026-" + month + " cycle wipe", cycle.WipeUtc, atWipe);
            DateTime openWall = TimeZoneInfo.ConvertTimeFromUtc(cycle.OpenUtc, zone);
            Eq("2026-" + month + " unlock weekday", openWall.DayOfWeek, DayOfWeek.Monday);
            Eq("2026-" + month + " unlock hour", openWall.Hour, 4);
            Eq("2026-" + month + " unlock minute", openWall.Minute, 0);
            Eq("2026-" + month + " unlock is four days later", openWall.Date, wall.Date.AddDays(4));
        }

        Eq("2024-02-01 leap Thursday", NightshadeDefaultUnlockedSchedule.NthWeekday(2024, 2, DayOfWeek.Thursday, 1, 14, 0).Day, 1);
        Eq("2027-01-07", NightshadeDefaultUnlockedSchedule.NthWeekday(2027, 1, DayOfWeek.Thursday, 1, 14, 0).Day, 7);
        Eq("2027-11-04", NightshadeDefaultUnlockedSchedule.NthWeekday(2027, 11, DayOfWeek.Thursday, 1, 14, 0).Day, 4);
        Eq("2028-02-03", NightshadeDefaultUnlockedSchedule.NthWeekday(2028, 2, DayOfWeek.Thursday, 1, 14, 0).Day, 3);
        Eq("2035-06-07", NightshadeDefaultUnlockedSchedule.NthWeekday(2035, 6, DayOfWeek.Thursday, 1, 14, 0).Day, 7);
        Eq("2099-12-03", NightshadeDefaultUnlockedSchedule.NthWeekday(2099, 12, DayOfWeek.Thursday, 1, 14, 0).Day, 3);
    }

    private static void MondayBoundary(TimeZoneInfo zone, NightshadeDefaultUnlockedSchedule.Rules rules)
    {
        DateTime wipe = Utc(2026, 11, 5, 19, 0, 0);
        DateTime open = Utc(2026, 11, 9, 9, 0, 0);
        var beforeWipe = NightshadeDefaultUnlockedSchedule.Evaluate(wipe.AddSeconds(-1), zone, rules);
        var atWipe = NightshadeDefaultUnlockedSchedule.Evaluate(wipe, zone, rules);
        var beforeOpen = NightshadeDefaultUnlockedSchedule.Evaluate(open.AddSeconds(-1), zone, rules);
        var atOpen = NightshadeDefaultUnlockedSchedule.Evaluate(open, zone, rules);
        var afterOpen = NightshadeDefaultUnlockedSchedule.Evaluate(open.AddSeconds(1), zone, rules);

        Eq("second before November wipe is open", beforeWipe.Open, true);
        Eq("second before November wipe boundary is the wipe", beforeWipe.NextBoundaryUtc, wipe);
        Eq("November wipe instant is closed", atWipe.Open, false);
        Eq("November wipe instant boundary is Monday", atWipe.NextBoundaryUtc, open);
        Eq("second before Monday 04:00 is closed", beforeOpen.Open, false);
        Eq("second before Monday boundary", beforeOpen.NextBoundaryUtc, open);
        Eq("Monday 04:00 is open", atOpen.Open, true);
        Eq("Monday 04:00 cycle wipe", atOpen.WipeUtc, wipe);
        Eq("Monday 04:00 next wipe", atOpen.NextBoundaryUtc, Utc(2026, 12, 3, 19, 0, 0));
        Eq("second after Monday stays open", afterOpen.Open, true);
        Eq("open window is not the following weekend only", atOpen.NextWipeUtc, Utc(2026, 12, 3, 19, 0, 0));
    }

    private static void BeforeWipeUsesPreviousCycle(TimeZoneInfo zone, NightshadeDefaultUnlockedSchedule.Rules rules)
    {
        // Wednesday 4 Nov 2026 12:00 ET, before Thursday 5 Nov 14:00. October's Monday
        // has passed. The next Monday (9 Nov) belongs to the wipe that has not happened.
        var before = NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2026, 11, 4, 17, 0, 0), zone, rules);
        Eq("4 Nov is open", before.Open, true);
        Eq("4 Nov cycle wipe is 1 Oct 18:00 UTC", before.WipeUtc, Utc(2026, 10, 1, 18, 0, 0));
        Eq("4 Nov unlock is 5 Oct 08:00 UTC", before.OpenUtc, Utc(2026, 10, 5, 8, 0, 0));
        Eq("4 Nov boundary is 5 Nov wipe, not 9 Nov", before.NextBoundaryUtc, Utc(2026, 11, 5, 19, 0, 0));

        // Wipe morning, 5 Nov 13:59 ET.
        var morning = NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2026, 11, 5, 18, 59, 0), zone, rules);
        Eq("wipe morning is still open", morning.Open, true);
        Eq("wipe morning boundary is 14:00 the same day", morning.NextBoundaryUtc, Utc(2026, 11, 5, 19, 0, 0));

        // 1 Oct 13:59 ET is still the September cycle. 5 Oct is not the boundary yet.
        var octMorning = NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2026, 10, 1, 17, 59, 0), zone, rules);
        Eq("1 Oct morning is open", octMorning.Open, true);
        Eq("1 Oct morning cycle is September", octMorning.WipeUtc, Utc(2026, 9, 3, 18, 0, 0));
        Eq("1 Oct morning boundary is today's wipe", octMorning.NextBoundaryUtc, Utc(2026, 10, 1, 18, 0, 0));
    }

    private static void MonthAndYearRollover(TimeZoneInfo zone, NightshadeDefaultUnlockedSchedule.Rules rules)
    {
        var nye = NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2027, 1, 1, 4, 0, 0), zone, rules);
        Eq("31 Dec 23:00 ET is open", nye.Open, true);
        Eq("31 Dec cycle wipe is 3 Dec", nye.WipeUtc, Utc(2026, 12, 3, 19, 0, 0));
        Eq("31 Dec boundary is 7 Jan wipe", nye.NextBoundaryUtc, Utc(2027, 1, 7, 19, 0, 0));

        var newYear = NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2027, 1, 1, 5, 30, 0), zone, rules);
        Eq("1 Jan 00:30 ET is open", newYear.Open, true);
        Eq("1 Jan still on the December cycle", newYear.WipeUtc, Utc(2026, 12, 3, 19, 0, 0));
        Eq("1 Jan boundary is 7 Jan, not 11 Jan", newYear.NextBoundaryUtc, Utc(2027, 1, 7, 19, 0, 0));

        var janWipe = NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2027, 1, 7, 19, 0, 0), zone, rules);
        Eq("7 Jan 14:00 ET is closed", janWipe.Open, false);
        Eq("7 Jan unlock is 11 Jan 09:00 UTC", janWipe.OpenUtc, Utc(2027, 1, 11, 9, 0, 0));
        var janOpen = NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2027, 1, 11, 9, 0, 0), zone, rules);
        Eq("11 Jan 04:00 ET is open", janOpen.Open, true);
        Eq("11 Jan stays open until 4 Feb", janOpen.NextBoundaryUtc, Utc(2027, 2, 4, 19, 0, 0));

        var far = NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2035, 6, 15, 16, 0, 0), zone, rules);
        Eq("15 Jun 2035 is open", far.Open, true);
        Eq("15 Jun 2035 wipe was 7 Jun", far.WipeUtc, Utc(2035, 6, 7, 18, 0, 0));
        Eq("15 Jun 2035 boundary is 5 Jul", far.NextBoundaryUtc, Utc(2035, 7, 5, 18, 0, 0));

        var early = NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2020, 1, 1, 17, 0, 0), zone, rules);
        Eq("1 Jan 2020 noon ET is open", early.Open, true);
        Eq("1 Jan 2020 boundary is 2 Jan wipe", early.NextBoundaryUtc, Utc(2020, 1, 2, 19, 0, 0));

        var late = NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2099, 12, 1, 17, 0, 0), zone, rules);
        Eq("1 Dec 2099 is open", late.Open, true);
        Eq("1 Dec 2099 boundary is 3 Dec", late.NextBoundaryUtc, Utc(2099, 12, 3, 19, 0, 0));
    }

    private static void DstTransitions(TimeZoneInfo zone, NightshadeDefaultUnlockedSchedule.Rules rules)
    {
        // 2026-03-08 spring forward sits inside the closed window.
        // Thursday 14:00 EST is 19:00 UTC. Monday 04:00 EDT is 08:00 UTC.
        var marchWipe = NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2026, 3, 5, 19, 0, 0), zone, rules);
        Eq("March wipe closed", marchWipe.Open, false);
        Eq("March wipe UTC", marchWipe.WipeUtc, Utc(2026, 3, 5, 19, 0, 0));
        Eq("March unlock UTC is EDT", marchWipe.OpenUtc, Utc(2026, 3, 9, 8, 0, 0));
        Eq("before spring-forward is closed", NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2026, 3, 8, 6, 30, 0), zone, rules).Open, false);
        Eq("after spring-forward is closed", NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2026, 3, 8, 7, 30, 0), zone, rules).Open, false);
        Eq("March Monday 04:00 opens", NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2026, 3, 9, 8, 0, 0), zone, rules).Open, true);

        DateTime gap = NightshadeDefaultUnlockedSchedule.ToUtc(new DateTime(2026, 3, 8, 2, 30, 0), zone);
        Eq("invalid 02:30 wall moves to 03:30 EDT", gap, Utc(2026, 3, 8, 7, 30, 0));

        // 2027-11-07 fall back sits inside the closed window.
        // Thursday 14:00 EDT is 18:00 UTC. Monday 04:00 EST is 09:00 UTC.
        var novWipe = NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2027, 11, 4, 18, 0, 0), zone, rules);
        Eq("Nov 2027 wipe closed", novWipe.Open, false);
        Eq("Nov 2027 unlock is EST", novWipe.OpenUtc, Utc(2027, 11, 8, 9, 0, 0));
        Eq("first 01:30 is closed", NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2027, 11, 7, 5, 30, 0), zone, rules).Open, false);
        Eq("second 01:30 is closed", NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2027, 11, 7, 6, 30, 0), zone, rules).Open, false);
        Eq("Nov 2027 Monday opens", NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2027, 11, 8, 9, 0, 0), zone, rules).Open, true);
        DateTime ambiguous = NightshadeDefaultUnlockedSchedule.ToUtc(new DateTime(2027, 11, 7, 1, 30, 0), zone);
        Eq("ambiguous 01:30 uses standard EST", ambiguous, Utc(2027, 11, 7, 6, 30, 0));
    }

    private static void FallbackZoneMatches(TimeZoneInfo zone, NightshadeDefaultUnlockedSchedule.Rules rules)
    {
        bool fallback;
        TimeZoneInfo resolved = NightshadeDefaultUnlockedSchedule.ResolveZone("Nightshade-Missing-Zone", out fallback);
        Eq("missing id still resolves to America/New_York", resolved.Id, "America/New_York");
        Eq("candidate Eastern zone is not the custom fallback", fallback, false);
        TimeZoneInfo custom = NightshadeDefaultUnlockedSchedule.FallbackEastern();
        DateTime[] samples = new DateTime[]
        {
            Utc(2026, 3, 5, 19, 0, 0),
            Utc(2026, 3, 8, 6, 30, 0),
            Utc(2026, 3, 8, 7, 30, 0),
            Utc(2026, 3, 9, 8, 0, 0),
            Utc(2026, 11, 4, 17, 0, 0),
            Utc(2026, 11, 5, 18, 59, 0),
            Utc(2026, 11, 5, 19, 0, 0),
            Utc(2026, 11, 9, 8, 59, 0),
            Utc(2026, 11, 9, 9, 0, 0),
            Utc(2027, 1, 1, 5, 30, 0),
            Utc(2027, 11, 4, 18, 0, 0),
            Utc(2027, 11, 7, 5, 30, 0),
            Utc(2027, 11, 7, 6, 30, 0),
            Utc(2027, 11, 8, 9, 0, 0),
            Utc(2035, 6, 15, 16, 0, 0),
            Utc(2099, 12, 1, 17, 0, 0)
        };
        for (int i = 0; i < samples.Length; i++)
        {
            var left = NightshadeDefaultUnlockedSchedule.Evaluate(samples[i], zone, rules);
            var right = NightshadeDefaultUnlockedSchedule.Evaluate(samples[i], custom, rules);
            Eq("fallback phase " + i, right.Open, left.Open);
            Eq("fallback wipe " + i, right.WipeUtc, left.WipeUtc);
            Eq("fallback open " + i, right.OpenUtc, left.OpenUtc);
            Eq("fallback boundary " + i, right.NextBoundaryUtc, left.NextBoundaryUtc);
            if (right.NextBoundaryUtc <= samples[i])
                Fail("boundary is not after the sample " + samples[i].ToString("u"));
        }
    }

    private static void DowntimeAndReload(TimeZoneInfo zone, NightshadeDefaultUnlockedSchedule.Rules rules)
    {
        // No stored phase. Each call is what a restart would compute.
        var offlineClosed = NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2026, 11, 8, 17, 0, 0), zone, rules);
        Eq("downtime lands in closed", offlineClosed.Open, false);
        var afterMonths = NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2027, 2, 1, 17, 0, 0), zone, rules);
        Eq("months later is open without a saved phase", afterMonths.Open, true);
        Eq("Feb 2027 before wipe is the January cycle", afterMonths.WipeUtc, Utc(2027, 1, 7, 19, 0, 0));

        var beforeGap = NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2026, 11, 1, 17, 0, 0), zone, rules);
        var afterGap = NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2026, 11, 6, 17, 0, 0), zone, rules);
        Eq("before the gap is open", beforeGap.Open, true);
        Eq("after the gap is closed immediately", afterGap.Open, false);
        Eq("catch-up does not wait for Monday", afterGap.NextBoundaryUtc, Utc(2026, 11, 9, 9, 0, 0));

        var yearsLater = NightshadeDefaultUnlockedSchedule.Evaluate(Utc(2035, 6, 15, 16, 0, 0), zone, rules);
        Eq("years of downtime still resolve", yearsLater.Open, true);

        string[] known = new string[] { Offline, Online };
        var closedPlan = NightshadeDefaultUnlockedSchedule.Build(false, known, new string[] { Offline + " (Offline)", Online + " (Online)" });
        Eq("catch-up closed removes both, including offline", closedPlan.Remove.Count, 2);
        Eq("catch-up closed adds nobody", closedPlan.Add.Count, 0);
        var openPlan = NightshadeDefaultUnlockedSchedule.Build(true, known, new string[0]);
        Eq("catch-up open re-adds offline and online", openPlan.Add.Count, 2);
    }

    private static void Membership(NightshadeDefaultUnlockedSchedule.Rules rules)
    {
        string[] known = new string[] { Offline, Online, Future, Vip, DefaultOnly, Offline };
        string[] members = new string[]
        {
            Offline + " (Offline Player)",
            Online + " (Online)",
            Vip + " (VIP Name)",
            "not-a-player",
            "admin (console)"
        };

        var closed = NightshadeDefaultUnlockedSchedule.Build(false, known, members);
        SetEq("closed removes direct members", closed.Remove, new string[] { Offline, Online, Vip });
        Eq("closed does not add future or default-only players", closed.Add.Count, 0);
        Eq("closed ignores a non-player row", closed.Remove.Contains("not-a-player"), false);

        var afterRemoval = NightshadeDefaultUnlockedSchedule.Build(false, known, new string[0]);
        Eq("closed reconcile is idempotent", afterRemoval.Remove.Count, 0);
        Eq("second closed pass adds nothing", afterRemoval.Add.Count, 0);

        var open = NightshadeDefaultUnlockedSchedule.Build(true, known, new string[0]);
        SetEq("open adds offline, online, future, vip, and default-only", open.Add, new string[] { Offline, Online, Future, Vip, DefaultOnly });
        Eq("open does not remove", open.Remove.Count, 0);

        var enrolled = new List<string>();
        for (int i = 0; i < open.Add.Count; i++) enrolled.Add(open.Add[i] + " (Name)");
        var again = NightshadeDefaultUnlockedSchedule.Build(true, known, enrolled);
        Eq("open reconcile is idempotent", again.Add.Count + again.Remove.Count, 0);

        string[] withoutFuture = new string[] { Offline, Online, Vip, DefaultOnly };
        var already = new List<string>();
        for (int i = 0; i < withoutFuture.Length; i++) already.Add(withoutFuture[i] + " (Name)");
        var beforeJoin = NightshadeDefaultUnlockedSchedule.Build(true, withoutFuture, already);
        Eq("future player is not invented before they exist", beforeJoin.Add.Count, 0);
        var afterJoin = NightshadeDefaultUnlockedSchedule.Build(true, known, already);
        SetEq("future player is enrolled on the next pass", afterJoin.Add, new string[] { Future });

        var regrant = NightshadeDefaultUnlockedSchedule.Build(false, known, new string[] { Future + " (Future)" });
        SetEq("closed period removes a regrant", regrant.Remove, new string[] { Future });

        var orphan = NightshadeDefaultUnlockedSchedule.Build(true, new string[] { Online }, new string[] { Online + " (Online)", Offline + " (Offline)" });
        Eq("open does not drop a direct member missing from the known list", orphan.Remove.Count, 0);
        Eq("open does not re-add someone already direct", orphan.Add.Count, 0);

        Eq("parse carbon row", NightshadeDefaultUnlockedSchedule.ParseMemberId(Offline + " (Player Name)"), Offline);
        Eq("reject short id", NightshadeDefaultUnlockedSchedule.IsPlayerId("12345"), false);
        Eq("reject empty", NightshadeDefaultUnlockedSchedule.ParseMemberId("  "), null);
    }

    private static void WakeTimer()
    {
        DateTime now = Utc(2026, 11, 6, 12, 0, 0);
        Eq("far boundary wakes daily", NightshadeDefaultUnlockedSchedule.WakeSeconds(now, Utc(2026, 12, 3, 19, 0, 0), 86400), 86400d);
        Eq("near boundary uses the real delay", NightshadeDefaultUnlockedSchedule.WakeSeconds(now, now.AddSeconds(100), 86400), 100d);
        Eq("sub-second boundary waits one second", NightshadeDefaultUnlockedSchedule.WakeSeconds(now, now.AddMilliseconds(200), 86400), 1d);
        Eq("past boundary waits one second", NightshadeDefaultUnlockedSchedule.WakeSeconds(now, now.AddSeconds(-5), 86400), 1d);
        Eq("unset cap still wakes daily", NightshadeDefaultUnlockedSchedule.WakeSeconds(now, Utc(2027, 1, 7, 19, 0, 0), 0), 86400d);
    }

    private static void SourceDoesNotTouchOtherPermissions()
    {
        string path = FindRepoFile("carbon/plugins/01_Server/NightshadeDefaultUnlocked.cs");
        string source = File.ReadAllText(path);
        string[] banned = new string[]
        {
            "GrantUserPermission",
            "GrantGroupPermission",
            "RevokeUserPermission",
            "RevokeGroupPermission",
            "SetGroupParent",
            "CreateGroup",
            "RemoveGroup",
            "usergroup add",
            "o.usergroup",
            "oxide.usergroup"
        };
        for (int i = 0; i < banned.Length; i++)
        {
            if (source.IndexOf(banned[i], StringComparison.Ordinal) >= 0)
                Fail("plugin source contains " + banned[i]);
        }
        if (source.IndexOf("AddUserGroup(id, groupName, true)", StringComparison.Ordinal) < 0)
            Fail("plugin does not enroll with AddUserGroup on the configured group");
        if (source.IndexOf("RemoveUserGroup(id, groupName)", StringComparison.Ordinal) < 0)
            Fail("plugin does not remove with RemoveUserGroup on the configured group");
    }

    private static void CheckedInGroupsDoNotInheritUnlocked()
    {
        string path = FindRepoFile("carbon/data/oxide.groups.data");
        Dictionary<string, string> parents = ReadParents(File.ReadAllBytes(path));
        string parent;
        if (!parents.TryGetValue("default_unlocked", out parent))
            Fail("default_unlocked is missing from oxide.groups.data");
        else
            Eq("default_unlocked parent is default", parent, "default");
        string vipx;
        string og;
        string vip1;
        parents.TryGetValue("vipx", out vipx);
        parents.TryGetValue("og", out og);
        parents.TryGetValue("vip_1", out vip1);
        Eq("vipx parent is vip_base, not its title", vipx, "vip_base(nouser)");
        Eq("og parent is default", og, "default");
        Eq("vip_1 parent is vip_base", vip1, "vip_base(nouser)");

        foreach (KeyValuePair<string, string> pair in parents)
        {
            if (string.Equals(pair.Value, "default_unlocked", StringComparison.OrdinalIgnoreCase))
                Fail(pair.Key + " inherits default_unlocked, so removing direct members would not remove its permissions");
        }
        string defaultParent;
        parents.TryGetValue("default", out defaultParent);
        Eq("default has no parent", defaultParent ?? "", "");
    }

    private static Dictionary<string, string> ReadParents(byte[] data)
    {
        var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        while (index < data.Length)
        {
            int tag;
            index = ReadVarint(data, index, out tag);
            if ((tag & 7) != 2) throw new InvalidDataException("groups file is not a protobuf dictionary");
            int length;
            index = ReadVarint(data, index, out length);
            int end = index + length;
            string name = null;
            string parent = "";
            bool sawValue = false;
            while (index < end)
            {
                int field;
                index = ReadVarint(data, index, out field);
                int wire = field & 7;
                int number = field >> 3;
                if (wire == 2)
                {
                    int size;
                    index = ReadVarint(data, index, out size);
                    byte[] chunk = new byte[size];
                    Buffer.BlockCopy(data, index, chunk, 0, size);
                    index += size;
                    if (number == 1 && name == null && IsAscii(chunk)) name = Encoding.ASCII.GetString(chunk);
                    else if (number == 2) parent = FirstString(chunk);
                    sawValue = sawValue || number == 2;
                }
                else if (wire == 0)
                {
                    int ignored;
                    index = ReadVarint(data, index, out ignored);
                }
                else
                {
                    throw new InvalidDataException("unsupported wire type in groups file");
                }
            }
            if (!string.IsNullOrEmpty(name)) parents[name] = sawValue ? parent : "";
        }
        return parents;
    }

    private static string FirstString(byte[] message)
    {
        int index = 0;
        while (index < message.Length)
        {
            int field;
            index = ReadVarint(message, index, out field);
            int wire = field & 7;
            int number = field >> 3;
            if (wire == 2)
            {
                int size;
                index = ReadVarint(message, index, out size);
                if (number == 1 && size > 0 && index + size <= message.Length && IsAscii(message, index, size))
                    return Encoding.ASCII.GetString(message, index, size);
                index += size;
            }
            else if (wire == 0)
            {
                int ignored;
                index = ReadVarint(message, index, out ignored);
            }
            else break;
        }
        return "";
    }

    private static bool IsAscii(byte[] data)
    {
        return IsAscii(data, 0, data.Length);
    }

    private static bool IsAscii(byte[] data, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            byte value = data[offset + i];
            if (value < 32 || value > 126) return false;
        }
        return count > 0;
    }

    private static int ReadVarint(byte[] data, int index, out int value)
    {
        int shift = 0;
        value = 0;
        while (index < data.Length)
        {
            byte current = data[index++];
            value |= (current & 0x7f) << shift;
            if ((current & 0x80) == 0) return index;
            shift += 7;
        }
        throw new InvalidDataException("truncated varint");
    }

    private static string FindRepoFile(string relative)
    {
        DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(relative);
    }

    private static void SetEq(string name, List<string> actual, string[] expected)
    {
        var left = new HashSet<string>(actual, StringComparer.Ordinal);
        var right = new HashSet<string>(expected, StringComparer.Ordinal);
        if (left.Count != actual.Count) Fail(name + " had duplicate ids");
        if (!left.SetEquals(right))
            Fail(name + " actual=[" + string.Join(",", actual.ToArray()) + "] expected=[" + string.Join(",", expected) + "]");
    }

    private static DateTime Utc(int year, int month, int day, int hour, int minute, int second)
    {
        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
    }

    private static void Eq(string name, object actual, object expected)
    {
        if (!object.Equals(actual, expected))
            Fail(name + " actual=" + Format(actual) + " expected=" + Format(expected));
    }

    private static string Format(object value)
    {
        if (value is DateTime)
        {
            DateTime time = (DateTime)value;
            return time.ToString("yyyy-MM-dd HH:mm:ss") + " kind=" + time.Kind;
        }
        return value == null ? "null" : value.ToString();
    }

    private static void Fail(string message)
    {
        failed++;
        Console.Error.WriteLine("FAIL " + message);
    }
}
