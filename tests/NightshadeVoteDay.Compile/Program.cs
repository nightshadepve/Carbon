using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using Oxide.Plugins;

public static class Program
{
    public static int Main()
    {
        int failed = 0;
        failed += Check("price follows the config keys", PriceFollowsConfig);
        failed += Check("missing cost key is left alone", MissingKeyStays);
        failed += Check("economics label", EconomicsLabel);
        failed += Check("panel sits left of the hotbar", PanelMissesHotbar);
        failed += Check("negative gap cannot cross the hotbar", GapDoesNotCross);
        failed += Check("anchor cleanup", Anchor);
        failed += Check("vote counts", Votes);
        failed += Check("status copy", Status);
        failed += Check("panel is smaller and still clickable", CompactPanel);
        failed += Check("hud no longer covers the inventory buttons", InventoryLayer);
        failed += Check("season follows the shop dates", ShopSeason);
        failed += Check("the shop config picks the same season", ShopFile);
        if (failed > 0)
        {
            Console.Error.WriteLine(failed.ToString(CultureInfo.InvariantCulture) + " failed");
            return 1;
        }
        Console.WriteLine("NightshadeVoteDay checks ok");
        return 0;
    }

    private static int Check(string name, Func<string> test)
    {
        string error = test();
        if (error == null)
        {
            Console.WriteLine("ok " + name);
            return 0;
        }
        Console.Error.WriteLine("FAIL " + name + ": " + error);
        return 1;
    }

    private static string PriceFollowsConfig()
    {
        string currency = "Economics";
        int rp = 500;
        double eco = 500d;
        string json = "{"
            + "\"Currency (ServerRewards or Economics)\": \"ServerRewards\","
            + "\"ServerRewards Cost\": 750,"
            + "\"Economics Cost\": 12.5"
            + "}";
        if (!NightshadeVoteDayRules.ApplyLiveCostsJson(json, ref currency, ref rp, ref eco))
            return "parser rejected a normal config";
        if (currency != "ServerRewards" || rp != 750 || eco != 12.5d)
            return "values were not taken from the file";
        if (NightshadeVoteDayRules.FormatCost(currency, rp, eco) != "750 RP")
            return "label was not 750 RP";
        json = json.Replace("750", "900");
        if (!NightshadeVoteDayRules.ApplyLiveCostsJson(json, ref currency, ref rp, ref eco))
            return "second read failed";
        if (rp != 900 || NightshadeVoteDayRules.FormatCost(currency, rp, eco) != "900 RP")
            return "price stayed cached at 750";
        return null;
    }

    private static string MissingKeyStays()
    {
        string currency = "Economics";
        int rp = 40;
        double eco = 12.5d;
        var raw = new JObject();
        raw[NightshadeVoteDayRules.RpCostKey] = 80;
        if (!NightshadeVoteDayRules.ApplyLiveCosts(raw, ref currency, ref rp, ref eco))
            return "present key was ignored";
        if (rp != 80 || eco != 12.5d || currency != "Economics")
            return "absent keys were overwritten";
        return null;
    }

    private static string EconomicsLabel()
    {
        string label = NightshadeVoteDayRules.FormatCost(" economics ", 10, 15.5d);
        if (label != "15.5 coins") return label;
        if (NightshadeVoteDayRules.FormatCost(null, 10, 15.5d) != "10 RP") return "null currency";
        return null;
    }

    private static string PanelMissesHotbar()
    {
        NightshadeVoteDayRules.ScreenBox panel = NightshadeVoteDayRules.LeftOfHotbar(
            NightshadeVoteDayRules.DefaultHotbarLeft,
            NightshadeVoteDayRules.DefaultGap,
            NightshadeVoteDayRules.DefaultBottom,
            NightshadeVoteDayRules.DefaultWidth,
            NightshadeVoteDayRules.DefaultHeight,
            "0.5 0");
        var hotbar = new NightshadeVoteDayRules.ScreenBox { Left = -204f, Bottom = 15f, Right = 179f, Top = 80f };
        if (NightshadeVoteDayRules.Overlaps(panel, hotbar))
            return "default panel overlaps the hotbar";
        if (panel.Right > NightshadeVoteDayRules.DefaultHotbarLeft - NightshadeVoteDayRules.DefaultGap)
            return "panel crosses the hotbar's left edge";
        if (panel.OffsetMin != "-348 16" || panel.OffsetMax != "-212 122")
            return panel.OffsetMin + " / " + panel.OffsetMax;
        NightshadeVoteDayRules.ScreenBox chip = NightshadeVoteDayRules.LeftOfHotbar(-204f, 8f, 16f, 48f, 48f, "0.5 0");
        if (NightshadeVoteDayRules.Overlaps(chip, hotbar))
            return "collapsed chip overlaps the hotbar";
        return null;
    }

    private static string GapDoesNotCross()
    {
        NightshadeVoteDayRules.ScreenBox panel = NightshadeVoteDayRules.LeftOfHotbar(-204f, -30f, 16f, 100f, 40f, "0.5 0");
        if (panel.Right > -204f) return "negative gap crossed the hotbar";
        var hotbar = new NightshadeVoteDayRules.ScreenBox { Left = -204f, Bottom = 15f, Right = 179f, Top = 80f };
        if (NightshadeVoteDayRules.Overlaps(panel, hotbar)) return "touching boxes counted as overlap";
        return null;
    }

    private static string Anchor()
    {
        if (NightshadeVoteDayRules.CleanAnchor("nope") != "0.5 0") return "bad anchor kept";
        if (NightshadeVoteDayRules.CleanAnchor(" 0.25  0 ") != "0.25 0") return "good anchor changed";
        return null;
    }

    private static string Votes()
    {
        if (NightshadeVoteDayRules.Needed(1, 0.75f) != 1) return "one player";
        if (NightshadeVoteDayRules.Needed(4, 0.75f) != 3) return "four players";
        if (NightshadeVoteDayRules.Needed(0, 0.75f) != 0) return "empty";
        if (NightshadeVoteDayRules.VotesLabel(3, 5) != "3/5") return "label";
        if (Math.Abs(NightshadeVoteDayRules.VoteFraction(3, 5) - 0.6f) > 0.001f) return "fraction";
        if (NightshadeVoteDayRules.Headline("voting", 51, null) != "51") return "countdown";
        if (NightshadeVoteDayRules.Headline("bought", 0, "Alex") != "Alex") return "buyer";
        if (NightshadeVoteDayRules.CleanName(" <Ann>\n ") != "Ann") return "name";
        return null;
    }

    private static string Status()
    {
        if (NightshadeVoteDayRules.StatusKey("voting") != "UiVoteHint") return "hint";
        if (NightshadeVoteDayRules.StatusKey("passed") != "UiWillSkip") return "pass";
        if (NightshadeVoteDayRules.StatusKey("failed") != "UiFailed") return "fail";
        if (NightshadeVoteDayRules.StatusKey("bought") != "UiPurchased") return "buy";
        if (NightshadeVoteDayRules.StatusKind("passed") != "ok") return "ok";
        if (NightshadeVoteDayRules.StatusKind("failed") != "bad") return "bad";
        if (NightshadeVoteDayRules.NormalizeCursorKey(" Middle ") != "firethird") return "middle";
        if (NightshadeVoteDayRules.NormalizeCursorKey("e") != "use") return "use";
        if (NightshadeVoteDayRules.NormalizeCursorKey("off") != "none") return "none";
        if (NightshadeVoteDayRules.NormalizeCursorKey("nope") != "firethird") return "fallback";
        if (NightshadeVoteDayRules.ResultStatusKey("passed", 2) != "UiQuietAfter") return "quiet";
        if (NightshadeVoteDayRules.ResultStatusKey("passed", 0) != "UiWillSkip") return "plain pass";
        return null;
    }

    private static string CompactPanel()
    {
        if (NightshadeVoteDayRules.DefaultWidth >= 152f || NightshadeVoteDayRules.DefaultHeight >= 136f)
            return "defaults were not reduced";
        if (!NightshadeVoteDayRules.ExpandedFits(NightshadeVoteDayRules.DefaultWidth, NightshadeVoteDayRules.DefaultHeight))
            return "buttons do not fit the compact box";
        var oldSize = new NightshadeVoteDay.PluginConfig { PanelWidth = 152f, PanelHeight = 136f };
        NightshadeVoteDayRules.MigratePanelSize(oldSize);
        if (Math.Abs(oldSize.PanelWidth - NightshadeVoteDayRules.DefaultWidth) > 0.01f || Math.Abs(oldSize.PanelHeight - NightshadeVoteDayRules.DefaultHeight) > 0.01f)
            return "old default size was kept";
        var custom = new NightshadeVoteDay.PluginConfig { PanelWidth = 180f, PanelHeight = 140f };
        NightshadeVoteDayRules.MigratePanelSize(custom);
        if (Math.Abs(custom.PanelWidth - 180f) > 0.01f || Math.Abs(custom.PanelHeight - 140f) > 0.01f)
            return "custom size was overwritten";
        float vote = NightshadeVoteDayRules.VoteTop - NightshadeVoteDayRules.VoteBottom;
        float action = NightshadeVoteDayRules.ActionTop - NightshadeVoteDayRules.ActionBottom;
        if (vote < 18f || action < 18f) return "buttons are under 18px";
        return null;
    }

    private static string InventoryLayer()
    {
        if (NightshadeVoteDayRules.MenuLayer != "Overlay") return NightshadeVoteDayRules.MenuLayer;
        var config = new NightshadeVoteDay.PluginConfig();
        var raw = new JObject();
        raw[NightshadeVoteDayRules.OldLayerKey] = "Hud";
        NightshadeVoteDay.MigrateLayer(raw, config);
        if (config.PanelLayer != "Overlay") return "old Hud default was kept";
        config = new NightshadeVoteDay.PluginConfig();
        raw = new JObject();
        raw[NightshadeVoteDayRules.OldLayerKey] = "Overall";
        NightshadeVoteDay.MigrateLayer(raw, config);
        if (config.PanelLayer != "Overall") return "custom layer was dropped";
        config = new NightshadeVoteDay.PluginConfig();
        raw = new JObject();
        raw[NightshadeVoteDayRules.LayerKey] = "Overlay";
        raw[NightshadeVoteDayRules.OldLayerKey] = "Overall";
        NightshadeVoteDay.MigrateLayer(raw, config);
        if (config.PanelLayer != "Overlay") return "new key lost to the old key";
        return null;
    }

    private static string ShopSeason()
    {
        var themes = SampleThemes();
        var november = NightshadeVoteDayRules.ResolveTheme("Auto", themes, 1107);
        if (november == null || november.Name != "Autumn") return "7 November";
        var halloween = NightshadeVoteDayRules.ResolveTheme("Shop", themes, 1031);
        if (halloween == null || halloween.Name != "Halloween") return "31 October";
        var christmas = NightshadeVoteDayRules.ResolveTheme("Auto", themes, 1225);
        if (christmas == null || christmas.Name != "Christmas") return "25 December";
        var winter = NightshadeVoteDayRules.ResolveTheme("Auto", themes, 102);
        if (winter == null || winter.Name != "Winter") return "2 January";
        var forced = NightshadeVoteDayRules.ResolveTheme("Summer", themes, 1107);
        if (forced == null || forced.Name != "Summer") return "forced name";
        if (NightshadeVoteDayRules.ResolveTheme("Auto", new List<NightshadeVoteDayRules.ThemeRow>(), 1107) != null)
            return "empty list";
        if (!NightshadeVoteDayRules.InRange("12-21", "03-19", 102) || NightshadeVoteDayRules.InRange("12-21", "03-19", 701))
            return "year wrap";
        return null;
    }

    private static string ShopFile()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "configs", "NightshadeShop.json"));
        if (!File.Exists(path)) return "missing " + path;
        JObject root = JObject.Parse(File.ReadAllText(path));
        if (NightshadeVoteDayRules.ShopMode(root) != "Auto") return "shop mode";
        var rows = NightshadeVoteDayRules.ReadThemeRows(root);
        var november = NightshadeVoteDayRules.ResolveTheme("Auto", rows, 1107);
        if (november == null || november.Name != "Autumn") return november == null ? "no theme" : november.Name;
        if (november.Accent != "#C8642DFF") return november.Accent;
        var halloween = NightshadeVoteDayRules.ResolveTheme(NightshadeVoteDayRules.ShopMode(root), rows, 1031);
        if (halloween == null || halloween.Name != "Halloween") return "halloween";
        return null;
    }

    private static List<NightshadeVoteDayRules.ThemeRow> SampleThemes()
    {
        return new List<NightshadeVoteDayRules.ThemeRow>
        {
            new NightshadeVoteDayRules.ThemeRow { Name = "Nightshade", From = "01-01", To = "12-31", Priority = -1, Accent = "#8B3FBFFF" },
            new NightshadeVoteDayRules.ThemeRow { Name = "Spring", From = "03-20", To = "06-20", Priority = 0 },
            new NightshadeVoteDayRules.ThemeRow { Name = "Summer", From = "06-21", To = "09-21", Priority = 0 },
            new NightshadeVoteDayRules.ThemeRow { Name = "Autumn", From = "09-22", To = "12-20", Priority = 0, Accent = "#C8642DFF" },
            new NightshadeVoteDayRules.ThemeRow { Name = "Winter", From = "12-21", To = "03-19", Priority = 0 },
            new NightshadeVoteDayRules.ThemeRow { Name = "Halloween", From = "10-01", To = "10-31", Priority = 10 },
            new NightshadeVoteDayRules.ThemeRow { Name = "Christmas", From = "12-01", To = "12-31", Priority = 10 }
        };
    }
}
