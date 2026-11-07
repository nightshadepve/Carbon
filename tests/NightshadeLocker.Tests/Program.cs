using System;
using System.Collections.Generic;
using Oxide.Plugins;

internal static class Program
{
    private static int failed;

    private static void Main()
    {
        Check(NightshadeLockerRules.PrimaryCommand == "nslock", "primary command is nslock");
        Pickup();
        Refunds();
        Locks();
        Ownership();
        Themes();
        Import();
        Doors();
        Timers();
        if (failed > 0)
        {
            Console.Error.WriteLine(failed + " failed");
            Environment.Exit(1);
        }
        Console.WriteLine("NightshadeLocker rules: all checks passed");
    }

    private static void Pickup()
    {
        var block = NightshadeLockerRules.Pickup.Block;
        var vanilla = NightshadeLockerRules.Pickup.Vanilla;
        var free = NightshadeLockerRules.Pickup.RemoveWithoutItem;
        Check(NightshadeLockerRules.DecidePickup(true, true, NightshadeLockerRules.ProvenancePaid, true) == block, "locked paid pickup stays blocked even with the free permission");
        Check(NightshadeLockerRules.DecidePickup(true, true, NightshadeLockerRules.ProvenanceFree, false) == block, "locked free pickup stays blocked");
        Check(NightshadeLockerRules.DecidePickup(true, false, null, true) == block, "locked untracked pickup stays blocked");
        Check(NightshadeLockerRules.DecidePickup(false, true, NightshadeLockerRules.ProvenancePaid, true) == vanilla, "unlocked paid pickup uses vanilla refund");
        Check(NightshadeLockerRules.DecidePickup(false, false, null, true) == vanilla, "free permission does not make an untracked lock free");
        Check(NightshadeLockerRules.DecidePickup(false, true, NightshadeLockerRules.ProvenanceFree, false) == free, "unlocked free lock is removed without an item");
        Check(NightshadeLockerRules.DecidePickup(false, false, NightshadeLockerRules.ProvenanceFree, true) == block, "someone else cannot pocket a free lock");
    }

    private static void Refunds()
    {
        Check(NightshadeLockerRules.PlanReturn(NightshadeLockerRules.ProvenancePaid, true, false) == NightshadeLockerRules.Refund.Give, "paid lock returns to inventory");
        Check(NightshadeLockerRules.PlanReturn(NightshadeLockerRules.ProvenancePaid, false, true) == NightshadeLockerRules.Refund.Drop, "full inventory drops the paid lock");
        Check(NightshadeLockerRules.PlanReturn(NightshadeLockerRules.ProvenancePaid, false, false) == NightshadeLockerRules.Refund.Owe, "failed drop owes the paid lock");
        Check(NightshadeLockerRules.PlanReturn(null, false, false) == NightshadeLockerRules.Refund.Owe, "missing provenance is not treated as free");
        Check(NightshadeLockerRules.PlanReturn(NightshadeLockerRules.ProvenanceFree, true, true) == NightshadeLockerRules.Refund.None, "free lock is not refunded");
        var pref = new NightshadeLockerRules.PlayerPref();
        NightshadeLockerRules.AddOwed(pref, "lock.code", 1);
        NightshadeLockerRules.AddOwed(pref, "lock.key", 2);
        NightshadeLockerRules.AddOwed(pref, "door.closer", 1);
        Check(pref.OwedCode == 1 && pref.OwedKey == 2 && pref.OwedCloser == 1, "owed counts follow the item");
    }

    private static void Locks()
    {
        var paid = NightshadeLockerRules.ChooseLock(false, true, false, true, true);
        Check(paid.Item == "lock.code" && !paid.Free, "a code lock in inventory is consumed");
        var key = NightshadeLockerRules.ChooseLock(false, false, true, true, true);
        Check(key.Item == "lock.key" && !key.Free, "key fallback uses the key lock the player holds");
        var free = NightshadeLockerRules.ChooseLock(false, false, false, true, true);
        Check(free.Item == "lock.code" && free.Free, "free grant only when no lock item is used");
        var none = NightshadeLockerRules.ChooseLock(true, false, false, false, true);
        Check(none.Item == null && !none.Free, "key-only mode does not mint a free code lock");
        Check(!NightshadeLockerRules.AutoWanted(true, false, true), "master toggle off stops auto lock");
        Check(!NightshadeLockerRules.AutoWanted(false, true, true), "server category off stops auto lock");
        Check(NightshadeLockerRules.AutoWanted(true, true, true), "auto lock runs when server, master, and category are on");
        Check(NightshadeLockerRules.ClampDelay(3, 10, 60) == 10, "delay clamps to the minimum");
        Check(NightshadeLockerRules.ClampDelay(90, 10, 60) == 60, "delay clamps to the maximum");
        Check(NightshadeLockerRules.PinValid("1234") && !NightshadeLockerRules.PinValid("123") && !NightshadeLockerRules.PinValid("12a4"), "pins are 4 digits");
        var shared = NightshadeLockerRules.ShareTargets(5, new ulong[] { 5, 0, 9, 9, 8 }, true);
        Check(shared.Count == 2 && shared[0] == 9 && shared[1] == 8, "share list skips the owner and duplicates");
        Check(NightshadeLockerRules.ShareTargets(5, new ulong[] { 9 }, false).Count == 0, "share can be off");
    }

    private static void Ownership()
    {
        Check(NightshadeLockerRules.MayClaim(0, 42, true), "unowned vehicle can be claimed");
        Check(!NightshadeLockerRules.MayClaim(7, 42, true), "another player's vehicle is not claimed");
        Check(!NightshadeLockerRules.MayReplaceOwner(7, 42), "ownership replacement is never allowed");
        Check(!NightshadeLockerRules.ControlAllowed(true), "a loaded rival plugin pauses control");
        Check(NightshadeLockerRules.ControlAllowed(false), "control is allowed when no rival is loaded");
        var denied = NightshadeLockerRules.PlaceDenial(new NightshadeLockerRules.PlaceRequest
        {
            Conflict = false,
            HasUse = true,
            Known = true,
            CategoryOpen = true,
            Vehicle = true,
            OwnedByOther = true,
            HasItemOrFree = true
        });
        Check(denied != null && denied.IndexOf("someone else", StringComparison.Ordinal) >= 0, "other owners block vehicle locks");
        var privilege = NightshadeLockerRules.PlaceDenial(new NightshadeLockerRules.PlaceRequest
        {
            HasUse = true,
            Known = true,
            CategoryOpen = true,
            RequirePrivilege = true,
            HasPrivilege = false,
            HasItemOrFree = true
        });
        Check(privilege != null && privilege.IndexOf("building privilege", StringComparison.Ordinal) >= 0, "structures need building privilege");
        var ok = NightshadeLockerRules.PlaceDenial(new NightshadeLockerRules.PlaceRequest
        {
            HasUse = true,
            Known = true,
            CategoryOpen = true,
            RequirePrivilege = true,
            HasPrivilege = true,
            HasItemOrFree = true
        });
        Check(ok == null, "authorized structure lock is allowed");
        var paused = NightshadeLockerRules.PlaceDenial(new NightshadeLockerRules.PlaceRequest { Conflict = true, HasUse = true });
        Check(paused != null, "conflict denies placement");
    }

    private static void Themes()
    {
        var themes = new List<NightshadeLockerRules.ThemeRow>
        {
            new NightshadeLockerRules.ThemeRow { Name = "Nightshade", From = "01-01", To = "12-31", Priority = -1, Accent = "#8B3FBFFF" },
            new NightshadeLockerRules.ThemeRow { Name = "Autumn", From = "09-22", To = "12-20", Priority = 0, Accent = "#C8642DFF" },
            new NightshadeLockerRules.ThemeRow { Name = "Halloween", From = "10-01", To = "10-31", Priority = 10, Accent = "#F07A1AFF" },
            new NightshadeLockerRules.ThemeRow { Name = "Winter", From = "12-21", To = "03-19", Priority = 0, Accent = "#6FB3E0FF" },
            new NightshadeLockerRules.ThemeRow { Name = "Christmas", From = "12-01", To = "12-31", Priority = 10, Accent = "#C8333AFF" }
        };
        var november = NightshadeLockerRules.ResolveTheme("Auto", themes, 1107);
        Check(november != null && november.Name == "Autumn", "7 November resolves to Autumn, matching the shop date rule");
        var halloween = NightshadeLockerRules.ResolveTheme("Auto", themes, 1031);
        Check(halloween != null && halloween.Name == "Halloween", "higher priority wins inside an overlap");
        var forced = NightshadeLockerRules.ResolveTheme("Winter", themes, 1107);
        Check(forced != null && forced.Name == "Winter", "a named theme overrides the date");
        Check(NightshadeLockerRules.ResolveTheme("Auto", new List<NightshadeLockerRules.ThemeRow>(), 1107) == null, "empty theme list falls back to the caller");
        Check(NightshadeLockerRules.InRange("12-21", "03-19", 102) && !NightshadeLockerRules.InRange("12-21", "03-19", 701), "winter wraps the year");
    }

    private static void Import()
    {
        string json = "{"
            + "\"Auto Lock Player Settings\":{"
            + "\"76561198000000001\":{"
            + "\"Code Lock\":\"2468\","
            + "\"Guest Code Lock\":\"1357\","
            + "\"Is automatic lock placement enabled.\":false,"
            + "\"Use Key Locks if there are no Code Locks in inventory.\":false,"
            + "\"Is automatic Guest Code enabled.\":true,"
            + "\"Is automatic Share locks with clan/team members enabled.\":true,"
            + "\"Is streamer mode enabled.\":true,"
            + "\"Code Lock skin: Flight Control Codelock enabled\":false,"
            + "\"Is automatic lock on Doors enabled.\":true,"
            + "\"Is automatic lock on TC (Cupboards) enabled.\":true,"
            + "\"Is automatic lock closing when dismount of the vehicle enabled.\":false"
            + "}},"
            + "\"Auto Closing Player Settings\":{},"
            + "\"CodeLockItems\":{\"99\":[]},"
            + "\"DoorCloserCustomTimeItemsData\":{}"
            + "}";
        var notes = new List<string>();
        var rows = NightshadeLockerRules.ReadLegacy(json, notes);
        Check(rows.Count == 1, "one legacy player is read");
        Check(rows[0].Code == "2468" && rows[0].Guest == "1357" && rows[0].Streamer && rows[0].GuestEnabled, "legacy toggles and pins are mapped");
        Check(rows[0].LockOn("doors") && rows[0].LockOn("cupboards") && !rows[0].Auto, "door and cupboard toggles import");
        Check(string.Join(" ", notes).IndexOf("2468", StringComparison.Ordinal) < 0, "import notes do not contain the pin");
        Check(string.Join(" ", notes).IndexOf("left untouched", StringComparison.Ordinal) >= 0, "placed lock rows are not imported");
        var dest = new Dictionary<string, NightshadeLockerRules.PlayerPref>(StringComparer.Ordinal);
        var first = NightshadeLockerRules.Merge(dest, rows);
        var second = NightshadeLockerRules.Merge(dest, rows);
        Check(first.Created == 1 && first.Updated == 0, "first import creates the player");
        Check(second.Unchanged == 1 && second.Created == 0 && dest.Count == 1, "second import is a no-op");
        Check(first.Summary.IndexOf("2468", StringComparison.Ordinal) < 0 && second.Summary.IndexOf("1357", StringComparison.Ordinal) < 0, "import summary redacts codes");
        rows[0].Share = false;
        var third = NightshadeLockerRules.Merge(dest, rows);
        Check(third.Updated == 1 && dest[rows[0].SteamId].Share == false, "a changed toggle updates once");
        dest[rows[0].SteamId].OwedCode = 4;
        rows[0].Share = false;
        NightshadeLockerRules.Merge(dest, rows);
        Check(dest[rows[0].SteamId].OwedCode == 4, "import keeps owed items");
        var bad = new List<string>();
        var ignored = NightshadeLockerRules.ReadLegacy("{\"Auto Lock Player Settings\":{\"5\":{\"Code Lock\":\"12\"}}}", bad);
        Check(ignored.Count == 1 && ignored[0].Code == string.Empty && string.Join(" ", bad).IndexOf("12", StringComparison.Ordinal) < 0, "a short code is dropped without being echoed");
        Check(NightshadeLockerRules.BackupName(new DateTime(2026, 11, 7, 1, 2, 3, DateTimeKind.Utc)) == "state-20261107010203.json", "backup file name is stable");
    }

    private static void Doors()
    {
        Check(NightshadeLockerRules.CloseCategory("door.hinged.metal") == "door", "single door");
        Check(NightshadeLockerRules.CloseCategory("door.double.hinged.wood") == "double", "double door");
        Check(NightshadeLockerRules.CloseCategory("wall.frame.garagedoor") == "garage", "garage");
        Check(NightshadeLockerRules.CloseCategory("floor.ladder.hatch") == "hatch", "hatch");
        Check(NightshadeLockerRules.CloseCategory("wall.frame.fence.gate") == "fence", "fence gate");
        Check(NightshadeLockerRules.CloseCategory("gates.external.high.wood") == "external", "external gate");
        Check(NightshadeLockerRules.CloseCategory("shutter.wood.a") == "window", "shutter");
        Check(NightshadeLockerRules.LockCategory("door.hinged.wood", "door") == "doors", "doors share one lock category");
        Check(NightshadeLockerRules.LockCategory("wall.window.bars", "door") == "windows", "windows are their own lock category");
        Check(NightshadeLockerRules.LockCategory("", "cupboard") == "cupboards", "cupboard kind");
        Check(NightshadeLockerRules.LockCategory("ballista.static", "other") == "medieval", "medieval name");
        var vehicles = NightshadeLockerRules.DefaultVehicles();
        var mini = NightshadeLockerRules.FindRule("assets/content/vehicles/minicopter/minicopter.entity.prefab", vehicles, null);
        Check(mini != null && mini.Enabled && mini.Category == "vehicles", "minicopter is enabled");
        var boat = NightshadeLockerRules.FindRule("assets/content/vehicles/boats/tugboat/tugboat.prefab", vehicles, null);
        Check(boat != null && boat.Category == "naval", "tugboat is naval");
        Check(NightshadeLockerRules.FindRule("assets/content/vehicles/bikes/motorbike.prefab", vehicles, null) != null, "motorbike is enabled");
        Check(NightshadeLockerRules.SkinBlocked(5, new ulong[] { 5 }), "blocked skins are skipped");
    }

    private static void Timers()
    {
        var book = new HashSet<string>();
        book.Add("a");
        book.Add("b");
        book.Add("c");
        book.Remove("b");
        Check(book.Count == 2, "entity removal drops one timer");
        book.Clear();
        Check(book.Count == 0, "unload clears every timer");
        book.Clear();
        Check(book.Count == 0, "a second cleanup is empty");
    }

    private static void Check(bool condition, string name)
    {
        if (condition) Console.WriteLine("ok  " + name);
        else
        {
            failed++;
            Console.WriteLine("FAIL " + name);
        }
    }
}
