using System;
using System.Collections.Generic;
using System.Globalization;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("NightshadeExcavatorTest", "Nightshade", "1.0.2")]
    [Description("Admin-only live check for the Giant Excavator weekend boost. Unload clears any test clock this plugin set.")]
    public class NightshadeExcavatorTest : RustPlugin
    {
        // Not granted to any group. Server console and IsAdmin are allowed; everyone else needs this.
        private const string PermUse = "nightshadeexcavatortest.use";
        private const float AutoRestoreSeconds = 600f;
        // Same pairing as NightshadeExcavatorBoost.EnginePairMeters. DieselEngine.EngineOn broadcasts at 20m.
        private const float EnginePairMeters = 50f;

        [PluginReference] private Plugin NightshadePVPWeekend;

        private bool ownsClock;
        private bool hadClock;
        private long previousTicks;
        private Timer restoreTimer;
        private Timer measureTimer;
        private int measureToken;
        private int measureTicks;
        private int measureGoal;
        private ulong measureUserId;
        private readonly List<List<ArmSample>> measureSamples = new List<List<ArmSample>>();

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
            cmd.AddConsoleCommand("nexttest.status", this, nameof(CmdStatus));
            cmd.AddConsoleCommand("nexttest.force", this, nameof(CmdForce));
            cmd.AddConsoleCommand("nexttest.measure", this, nameof(CmdMeasure));
        }

        protected override void LoadDefaultConfig() { }
        protected override void LoadConfig() { }
        protected override void SaveConfig() { }

        private void Unload()
        {
            measureToken++;
            measureTimer?.Destroy();
            measureTimer = null;
            restoreTimer?.Destroy();
            restoreTimer = null;
            RestoreOwnedClock("unload");
        }

        private void CmdStatus(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            arg.ReplyWith(BuildStatus());
        }

        private void CmdForce(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            string mode = arg.Args != null && arg.Args.Length > 0 ? Text(arg.Args[0]) : "";
            if (mode.Equals("on", StringComparison.OrdinalIgnoreCase) || mode.Equals("start", StringComparison.OrdinalIgnoreCase))
            {
                PushClock(true, arg);
                return;
            }
            if (mode.Equals("off", StringComparison.OrdinalIgnoreCase) || mode.Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                PushClock(false, arg);
                return;
            }
            if (mode.Equals("restore", StringComparison.OrdinalIgnoreCase) || mode.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                if (!ownsClock)
                {
                    arg.ReplyWith("This plugin is not holding a test clock. nspvp.faketime clear resets one set by hand.");
                    return;
                }
                restoreTimer?.Destroy();
                restoreTimer = null;
                RestoreOwnedClock("command");
                arg.ReplyWith("Test clock restored. " + WeekendClause());
                return;
            }
            arg.ReplyWith("nexttest.force on | off | restore. on simulates the weekend, off simulates the weekday. Either one restores the previous clock after 10 minutes, on restore, and on unload.");
        }

        private void CmdMeasure(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (arg.Args == null || arg.Args.Length != 1)
            {
                arg.ReplyWith("nexttest.measure <seconds>  (5 to 600). Leave the excavator running. This command only reads piles and fuel.");
                return;
            }
            int seconds;
            if (!int.TryParse(Text(arg.Args[0]), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) || seconds < 5 || seconds > 600)
            {
                arg.ReplyWith("Seconds must be a whole number from 5 to 600.");
                return;
            }
            if (measureTimer != null)
            {
                arg.ReplyWith("A measure is already running.");
                return;
            }
            List<ArmSample> start = Snapshot();
            if (start.Count == 0)
            {
                arg.ReplyWith("No ExcavatorArm on the map.");
                return;
            }
            measureSamples.Clear();
            measureSamples.Add(start);
            measureGoal = seconds;
            measureTicks = 0;
            int token = ++measureToken;
            BasePlayer caller = arg.Connection != null ? arg.Connection.player as BasePlayer : null;
            measureUserId = caller != null ? caller.userID : 0UL;
            measureTimer = timer.Every(1f, () => MeasureTick(token));
            arg.ReplyWith("Measuring " + seconds + "s across " + start.Count + " arm(s), once a second. Burn is engine-on time, not the fuel stack. " + Summarize(start));
        }

        private void MeasureTick(int token)
        {
            if (token != measureToken) return;
            measureTicks++;
            measureSamples.Add(Snapshot());
            if (measureTicks < measureGoal) return;
            measureTimer?.Destroy();
            measureTimer = null;
            FinishMeasure();
        }

        private void FinishMeasure()
        {
            List<ArmSample> start = measureSamples.Count > 0 ? measureSamples[0] : new List<ArmSample>();
            var lines = new List<string>();
            lines.Add("nexttest.measure " + measureGoal + "s done (" + measureTicks + " one-second samples). " + WeekendClause());
            double ore = 0d;
            float burned = 0f;
            for (int i = 0; i < start.Count; i++)
            {
                ArmSample before = start[i];
                var series = new List<ArmSample>();
                for (int s = 0; s < measureSamples.Count; s++)
                {
                    List<ArmSample> frame = measureSamples[s];
                    for (int n = 0; n < frame.Count; n++)
                    {
                        if (frame[n].Id == before.Id) { series.Add(frame[n]); break; }
                    }
                }
                if (series.Count == 0)
                {
                    lines.Add("Arm " + before.Id + " is gone.");
                    continue;
                }
                ArmSample after = series[series.Count - 1];
                double gained = Total(after.Ore) - Total(before.Ore);
                ore += gained;
                float onSeconds = 0f;
                float armBurn = 0f;
                int purchases = 0;
                for (int s = 1; s < series.Count; s++)
                {
                    ArmSample left = series[s - 1];
                    ArmSample right = series[s];
                    if (!left.EngineOn && !right.EngineOn) continue;
                    float rate = right.FuelSeconds > 0.01f ? right.FuelSeconds : left.FuelSeconds;
                    onSeconds += 1f;
                    if (rate <= 0.01f) continue;
                    armBurn += 1f / rate;
                    if (left.EngineFound && right.EngineFound && right.CachedFuel > left.CachedFuel + 1f)
                    {
                        int bought = (int)Math.Round((right.CachedFuel - left.CachedFuel) / rate);
                        if (bought < 1) bought = 1;
                        purchases += bought;
                    }
                }
                burned += armBurn;
                int stackDelta = after.FuelItems - before.FuelItems;
                float stackFormula = 0f;
                if (after.FuelSeconds > 0.01f)
                    stackFormula = (before.FuelItems - after.FuelItems) + (before.CachedFuel - after.CachedFuel) / after.FuelSeconds;
                double perOnMinute = onSeconds <= 0.01f ? 0d : gained / onSeconds * 60d;
                lines.Add("Arm " + before.Id
                    + " tick " + Num(after.Tick) + " (not written by the boost)"
                    + " full " + Num(after.Full)
                    + " fuelSeconds " + (after.EngineFound ? Num(after.FuelSeconds) : "none")
                    + " mining " + (after.Mining ? "yes" : "no")
                    + " engineOn " + (after.EngineFound ? (after.EngineOn ? "yes" : "no") : "none"));
                lines.Add("  ore " + Num(gained) + " in " + Num(onSeconds) + "s engine-on (" + Num(perOnMinute) + "/min while on)");
                lines.Add("  ore by item: " + DeltaText(before.Ore, after.Ore));
                lines.Add("  fuel stack " + before.FuelItems + " -> " + after.FuelItems
                    + " (" + (stackDelta >= 0 ? "+" : "") + stackDelta + " in the tank, refuel is not burn)"
                    + " cached " + Num(before.CachedFuel) + " -> " + Num(after.CachedFuel)
                    + " purchases " + purchases
                    + " burned " + Num(armBurn) + " diesel");
                if (Math.Abs(stackFormula) > 0.05f)
                    lines.Add("  ignored stack formula " + Num(stackFormula) + " diesel (that number mixed refueling into burn; the 60s sample that printed -4.17 came from this).");
                if (after.Full > 0.01f && onSeconds > 0.01f)
                {
                    double cycle = gained / onSeconds * after.Full;
                    lines.Add("  one full cycle at this speed is about " + Num(cycle) + " ore. full " + Num(after.Full)
                        + " vs vanilla 120 is speed x" + Num(120f / after.Full)
                        + (after.FuelSeconds > 0.01f ? ", yield x" + Num(after.FuelSeconds / after.Full) : "")
                        + ". Tick is not changed, so tick 1 and tick 3 are the same speed while the invoke still matches the tick.");
                }
                if (armBurn > 0.02f)
                    lines.Add("  yield " + Num(gained / armBurn) + " ore per diesel burned.");
            }
            if (burned > 0.02f)
                lines.Add("Total ore " + Num(ore) + ", burned " + Num(burned) + " diesel, yield " + Num(ore / burned) + " ore per diesel.");
            else
                lines.Add("Total ore " + Num(ore) + ". The engine was not on, so yield per diesel is not available.");
            Deliver(measureUserId, string.Join("\n", lines.ToArray()));
        }

        private bool PushClock(bool weekend, ConsoleSystem.Arg arg)
        {
            Plugin plugin = Protection();
            if (plugin == null)
            {
                arg.ReplyWith("NightshadePVPWeekend is not loaded.");
                return false;
            }
            bool already = ownsClock;
            bool savedHad = hadClock;
            long savedTicks = previousTicks;
            if (!already)
            {
                object has = plugin.Call("API_HasTestClock");
                hadClock = has is bool && (bool)has;
                previousTicks = 0L;
                if (hadClock)
                {
                    object ticks = plugin.Call("API_GetTestClockUtcTicks");
                    if (ticks is long) previousTicks = (long)ticks;
                    else if (ticks is int) previousTicks = (int)ticks;
                }
            }
            object edge = plugin.Call(weekend ? "API_GetPvPStartUtcTicks" : "API_GetPvPEndUtcTicks");
            long when = edge is long ? (long)edge : edge is int ? (int)edge : 0L;
            if (when <= 0L)
            {
                if (!already) { hadClock = savedHad; previousTicks = savedTicks; }
                arg.ReplyWith("Could not read the weekend edge from NightshadePVPWeekend.");
                return false;
            }
            object set = plugin.Call("API_SetTestClockUtcTicks", when);
            if (!(set is bool) || !(bool)set)
            {
                if (!already) { hadClock = savedHad; previousTicks = savedTicks; }
                arg.ReplyWith("Test clock was not set.");
                return false;
            }
            ownsClock = true;
            restoreTimer?.Destroy();
            restoreTimer = timer.Once(AutoRestoreSeconds, () =>
            {
                Puts("nexttest.force auto-restore after 10 minutes.");
                RestoreOwnedClock("timer");
            });
            arg.ReplyWith((weekend ? "Simulating the PvP weekend." : "Simulating a weekday.") + " " + WeekendClause() + " Auto-restore in 10 minutes. nexttest.force restore puts the previous clock back now.");
            return true;
        }

        private void RestoreOwnedClock(string why)
        {
            if (!ownsClock) return;
            ownsClock = false;
            Plugin plugin = Protection();
            if (plugin == null)
            {
                Puts("nexttest was holding a test clock but NightshadePVPWeekend is not loaded (" + why + "). If the clock is still on, run nspvp.faketime clear.");
                return;
            }
            try
            {
                if (hadClock) plugin.Call("API_SetTestClockUtcTicks", previousTicks);
                else plugin.Call("API_ClearTestClock");
            }
            catch (Exception ex)
            {
                Puts("Could not restore the test clock (" + why + "): " + ex.Message);
                return;
            }
            Puts("Excavator test clock restored (" + why + ").");
        }

        private string BuildStatus()
        {
            var lines = new List<string>();
            lines.Add(WeekendClause());
            lines.Add("Protection says: " + CallString("API_GetExcavatorStatus"));
            lines.Add("Test clock held by nexttest: " + (ownsClock ? "yes" : "no"));
            List<ArmSample> arms = Snapshot();
            if (arms.Count == 0) lines.Add("No ExcavatorArm on the map.");
            for (int i = 0; i < arms.Count; i++) lines.Add(FormatArm(arms[i]));
            string text = string.Join("\n", lines.ToArray());
            Puts(text);
            return text;
        }

        private static string FormatArm(ArmSample arm)
        {
            return "Arm " + arm.Id + " " + arm.Prefab
                + " tick " + Num(arm.Tick)
                + " full " + Num(arm.Full)
                + " fuelSeconds " + (arm.EngineFound ? Num(arm.FuelSeconds) : "none")
                + " mining " + (arm.Mining ? "yes" : "no")
                + " engineOn " + (arm.EngineFound ? (arm.EngineOn ? "yes" : "no") : "none")
                + " fuelItems " + (arm.EngineFound ? arm.FuelItems.ToString(CultureInfo.InvariantCulture) : "n/a")
                + " cachedFuel " + (arm.EngineFound ? Num(arm.CachedFuel) : "n/a")
                + " ore " + Num(Total(arm.Ore));
        }

        private static string Summarize(List<ArmSample> arms)
        {
            var parts = new List<string>();
            for (int i = 0; i < arms.Count; i++) parts.Add(FormatArm(arms[i]));
            return string.Join(" | ", parts.ToArray());
        }

        private List<ArmSample> Snapshot()
        {
            var arms = new List<ExcavatorArm>();
            var engines = new List<DieselEngine>();
            if (BaseNetworkable.serverEntities != null)
            {
                foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
                {
                    ExcavatorArm arm = entity as ExcavatorArm;
                    if (arm != null && !arm.IsDestroyed) arms.Add(arm);
                    DieselEngine engine = entity as DieselEngine;
                    if (engine != null && !engine.IsDestroyed) engines.Add(engine);
                }
            }
            bool anyNamed = false;
            for (int i = 0; i < engines.Count; i++)
                if (NamedExcavator(engines[i])) anyNamed = true;
            var samples = new List<ArmSample>();
            for (int i = 0; i < arms.Count; i++)
            {
                ExcavatorArm arm = arms[i];
                if (arm == null || arm.net == null) continue;
                ArmSample sample = new ArmSample();
                sample.Id = arm.net.ID.Value;
                sample.Prefab = Text(arm.ShortPrefabName);
                sample.Ore = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                sample.Tick = arm.resourceProductionTickRate;
                sample.Full = arm.timeForFullResources;
                sample.Mining = arm.IsMining();
                if (arm.pendingResources != null)
                {
                    for (int p = 0; p < arm.pendingResources.Length; p++)
                    {
                        ItemAmount pending = arm.pendingResources[p];
                        if (pending == null) continue;
                        AddOre(sample.Ore, pending.itemDef != null ? pending.itemDef.shortname : "pending", pending.amount);
                    }
                }
                if (arm.outputPiles != null)
                {
                    for (int p = 0; p < arm.outputPiles.Count; p++)
                    {
                        StorageContainer pile = arm.outputPiles[p];
                        if (pile == null || pile.inventory == null || pile.inventory.itemList == null) continue;
                        for (int n = 0; n < pile.inventory.itemList.Count; n++)
                        {
                            Item item = pile.inventory.itemList[n];
                            if (item == null || item.info == null) continue;
                            AddOre(sample.Ore, item.info.shortname, item.amount);
                        }
                    }
                }
                DieselEngine engine = NearestEngine(arm, engines, anyNamed);
                if (engine != null)
                {
                    sample.EngineFound = true;
                    sample.FuelSeconds = engine.runningTimePerFuelUnit;
                    sample.CachedFuel = engine.cachedFuelTime;
                    sample.FuelItems = engine.GetFuelAmount();
                    sample.EngineOn = engine.IsOn();
                }
                samples.Add(sample);
            }
            return samples;
        }

        private static DieselEngine NearestEngine(ExcavatorArm arm, List<DieselEngine> engines, bool anyNamed)
        {
            if (arm == null || arm.transform == null) return null;
            DieselEngine best = null;
            float bestSqr = float.MaxValue;
            float limit = EnginePairMeters * EnginePairMeters;
            for (int i = 0; i < engines.Count; i++)
            {
                DieselEngine engine = engines[i];
                if (engine == null || engine.IsDestroyed || engine.transform == null) continue;
                bool named = NamedExcavator(engine);
                if (anyNamed && !named) continue;
                float sqr = (arm.transform.position - engine.transform.position).sqrMagnitude;
                if (!named && sqr > limit) continue;
                if (sqr < bestSqr)
                {
                    best = engine;
                    bestSqr = sqr;
                }
            }
            return best;
        }

        private static bool NamedExcavator(BaseEntity entity)
        {
            BaseEntity current = entity;
            for (int i = 0; current != null && i < 6; i++)
            {
                string name = Text(current.ShortPrefabName) + " " + Text(current.PrefabName);
                if (name.IndexOf("excavator", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                current = current.GetParentEntity();
            }
            return false;
        }

        private string WeekendClause()
        {
            Plugin plugin = Protection();
            if (plugin == null) return "NightshadePVPWeekend is not loaded.";
            object active = plugin.Call("API_IsPvPActive");
            bool on = active is bool && (bool)active;
            return "PvP weekend " + (on ? "ACTIVE" : "inactive") + ".";
        }

        private string CallString(string hook)
        {
            Plugin plugin = Protection();
            if (plugin == null) return "(plugin not loaded)";
            object value = plugin.Call(hook);
            return value as string ?? "(no answer)";
        }


        private Plugin Protection()
        {
            if (NightshadePVPWeekend != null && NightshadePVPWeekend.IsLoaded) return NightshadePVPWeekend;
            Plugin legacy = plugins.Find("NightshadeRaidProtection");
            if (legacy != null && legacy.IsLoaded) return legacy;
            return null;
        }

        private bool Allowed(ConsoleSystem.Arg arg)
        {
            if (arg == null) return false;
            if (arg.Connection == null) return true;
            BasePlayer player = arg.Connection.player as BasePlayer;
            if (player == null) return false;
            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermUse)) return true;
            arg.ReplyWith("Admin only.");
            return false;
        }

        private void Deliver(ulong userId, string text)
        {
            Puts(text);
            if (userId == 0UL || BasePlayer.activePlayerList == null) return;
            for (int i = 0; i < BasePlayer.activePlayerList.Count; i++)
            {
                BasePlayer player = BasePlayer.activePlayerList[i];
                if (player != null && player.userID == userId && player.IsConnected)
                    player.ConsoleMessage(text);
            }
        }

        private static void AddOre(Dictionary<string, double> ore, string name, double amount)
        {
            if (ore == null || string.IsNullOrEmpty(name) || amount == 0d) return;
            double have;
            ore.TryGetValue(name, out have);
            ore[name] = have + amount;
        }

        private static double Total(Dictionary<string, double> ore)
        {
            if (ore == null) return 0d;
            double sum = 0d;
            foreach (var pair in ore) sum += pair.Value;
            return sum;
        }

        private static string DeltaText(Dictionary<string, double> before, Dictionary<string, double> after)
        {
            var names = new List<string>();
            if (before != null) foreach (var pair in before) if (!names.Contains(pair.Key)) names.Add(pair.Key);
            if (after != null) foreach (var pair in after) if (!names.Contains(pair.Key)) names.Add(pair.Key);
            if (names.Count == 0) return "(none)";
            var parts = new List<string>();
            for (int i = 0; i < names.Count; i++)
            {
                double a = 0d;
                double b = 0d;
                if (before != null) before.TryGetValue(names[i], out a);
                if (after != null) after.TryGetValue(names[i], out b);
                parts.Add(names[i] + " " + Num(b - a));
            }
            return string.Join(", ", parts.ToArray());
        }

        private static string Num(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Num(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        // Args, ShortPrefabName, and PrefabName are Facepunch.StringView on the live host.
        // Boxing covers both that struct and a real string, including null.
        private static string Text(object value)
        {
            return value == null ? "" : value.ToString();
        }

        private class ArmSample
        {
            public ulong Id;
            public string Prefab;
            public float Tick;
            public float Full;
            public float FuelSeconds;
            public float CachedFuel;
            public int FuelItems;
            public bool Mining;
            public bool EngineOn;
            public bool EngineFound;
            public Dictionary<string, double> Ore;
        }
    }
}
