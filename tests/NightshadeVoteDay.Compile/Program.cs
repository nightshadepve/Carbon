using System;
using System.Globalization;
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
        if (panel.OffsetMin != "-364 16" || panel.OffsetMax != "-212 134")
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
        return null;
    }
}
