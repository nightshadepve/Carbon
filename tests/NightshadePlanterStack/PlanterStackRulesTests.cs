using System;
using System.Collections.Generic;
using Nightshade.PlanterStack;

internal static class PlanterStackRulesTests
{
    private static int failed;

    private static void Check(string name, bool condition)
    {
        if (condition)
        {
            Console.WriteLine("PASS " + name);
            return;
        }

        failed++;
        Console.Error.WriteLine("FAIL " + name);
    }

    private static PlanterStackFacts Facts()
    {
        return new PlanterStackFacts
        {
            Enabled = true,
            HasPermission = true,
            IsAdmin = false,
            PlacingConfiguredPlanter = true,
            MinimumVerticalGap = 0f,
            HorizontalOverlap = 0.75f,
            FailedVanillaChecks = new List<string>(),
            Blockers = new List<PlanterBlocker>
            {
                new PlanterBlocker
                {
                    IsConfiguredPlanter = true,
                    VerticalDistance = 0.8f,
                    HorizontalDistance = 0.1f
                }
            }
        };
    }

    private static void Main()
    {
        AllowWhenOnlyConfiguredPlanterBlocks();
        DenyWhenAnotherEntityAlsoBlocks();
        DenyWhenPlayerBlocks();
        DenyWhenTerrainBlocks();
        DenyWithoutPermission();
        DenyAdminWithoutPermission();
        AllowAdminWhoAlsoHasPermission();
        DenyWhenDisabled();
        DenyWhenItemIsNotConfigured();
        DenyWhenPrivilegeFails();
        DenyWhenSocketFails();
        DenyWhenStabilityFails();
        DenyWhenGroundFails();
        DenyWhenLineOfSightFails();
        DenyWhenGapIsTooSmall();
        AllowWhenGapIsMet();
        AllowSideBySideUnderTheVerticalMinimum();
        AllowTouchingWhenGapIsZero();
        DenyUnconfiguredPlanterBlocker();
        DoNotInterveneWhenNothingBlocks();
        IgnoreBlankVanillaChecks();
        IdentifierMatchesLargePrefab();
        IdentifierDoesNotMatchSmallPrefab();
        IdentifierMatchesDecorPlanter();
        IdentifierIgnoresBlankEntries();
        IdentifierIsCaseInsensitive();
        NullFactsDeny();

        if (failed != 0)
        {
            Console.Error.WriteLine(failed + " failed");
            Environment.Exit(1);
        }

        Console.WriteLine("All planter stack decisions passed.");
    }

    private static void AllowWhenOnlyConfiguredPlanterBlocks()
    {
        PlanterStackResult result = PlanterStackRules.Decide(Facts());
        Check("allow planter-only block", result.Allowed && result.Reason == PlanterStackDenyReason.None);
    }

    private static void DenyWhenAnotherEntityAlsoBlocks()
    {
        PlanterStackFacts facts = Facts();
        facts.Blockers.Add(new PlanterBlocker
        {
            IsConfiguredPlanter = false,
            VerticalDistance = 0.2f,
            HorizontalDistance = 0.2f
        });
        PlanterStackResult result = PlanterStackRules.Decide(facts);
        Check("deny mixed planter and other entity", !result.Allowed && result.Reason == PlanterStackDenyReason.BlockedByNonPlanter);
    }

    private static void DenyWhenPlayerBlocks()
    {
        PlanterStackFacts facts = Facts();
        facts.Blockers.Clear();
        facts.Blockers.Add(new PlanterBlocker { IsConfiguredPlanter = false, VerticalDistance = 0f, HorizontalDistance = 0f });
        PlanterStackResult result = PlanterStackRules.Decide(facts);
        Check("deny player or other collider", !result.Allowed && result.Reason == PlanterStackDenyReason.BlockedByNonPlanter);
    }

    private static void DenyWhenTerrainBlocks()
    {
        PlanterStackFacts facts = Facts();
        facts.Blockers.Add(new PlanterBlocker { IsConfiguredPlanter = false, VerticalDistance = 0f, HorizontalDistance = 0.4f });
        PlanterStackResult result = PlanterStackRules.Decide(facts);
        Check("deny terrain collider", !result.Allowed && result.Reason == PlanterStackDenyReason.BlockedByNonPlanter);
    }

    private static void DenyWithoutPermission()
    {
        PlanterStackFacts facts = Facts();
        facts.HasPermission = false;
        PlanterStackResult result = PlanterStackRules.Decide(facts);
        Check("deny missing permission", !result.Allowed && result.Reason == PlanterStackDenyReason.MissingPermission);
    }

    private static void DenyAdminWithoutPermission()
    {
        PlanterStackFacts facts = Facts();
        facts.HasPermission = false;
        facts.IsAdmin = true;
        PlanterStackResult result = PlanterStackRules.Decide(facts);
        Check("deny admin without permission", !result.Allowed && result.Reason == PlanterStackDenyReason.MissingPermission);
    }

    private static void AllowAdminWhoAlsoHasPermission()
    {
        PlanterStackFacts facts = Facts();
        facts.IsAdmin = true;
        PlanterStackResult allowed = PlanterStackRules.Decide(facts);
        facts.IsAdmin = false;
        PlanterStackResult same = PlanterStackRules.Decide(facts);
        Check("admin flag does not change an allowed placement", allowed.Allowed && same.Allowed && allowed.Reason == same.Reason);
    }

    private static void DenyWhenDisabled()
    {
        PlanterStackFacts facts = Facts();
        facts.Enabled = false;
        PlanterStackResult result = PlanterStackRules.Decide(facts);
        Check("deny while disabled", !result.Allowed && result.Reason == PlanterStackDenyReason.Disabled);
    }

    private static void DenyWhenItemIsNotConfigured()
    {
        PlanterStackFacts facts = Facts();
        facts.PlacingConfiguredPlanter = false;
        PlanterStackResult result = PlanterStackRules.Decide(facts);
        Check("deny non-planter item", !result.Allowed && result.Reason == PlanterStackDenyReason.NotConfiguredPlanter);
    }

    private static void DenyWhenPrivilegeFails()
    {
        DenyVanilla("privilege");
    }

    private static void DenyWhenSocketFails()
    {
        DenyVanilla("socket");
    }

    private static void DenyWhenStabilityFails()
    {
        DenyVanilla("stability");
    }

    private static void DenyWhenGroundFails()
    {
        DenyVanilla("ground");
    }

    private static void DenyWhenLineOfSightFails()
    {
        DenyVanilla("lineofsight");
    }

    private static void DenyVanilla(string check)
    {
        PlanterStackFacts facts = Facts();
        facts.FailedVanillaChecks.Add(check);
        PlanterStackResult result = PlanterStackRules.Decide(facts);
        Check("deny vanilla " + check, !result.Allowed && result.Reason == PlanterStackDenyReason.VanillaCheckFailed);
    }

    private static void DenyWhenGapIsTooSmall()
    {
        PlanterStackFacts facts = Facts();
        facts.MinimumVerticalGap = 1f;
        facts.Blockers[0].VerticalDistance = 0.4f;
        facts.Blockers[0].HorizontalDistance = 0.2f;
        PlanterStackResult result = PlanterStackRules.Decide(facts);
        Check("deny stacked planter inside minimum gap", !result.Allowed && result.Reason == PlanterStackDenyReason.VerticalGap);
    }

    private static void AllowWhenGapIsMet()
    {
        PlanterStackFacts facts = Facts();
        facts.MinimumVerticalGap = 0.5f;
        facts.Blockers[0].VerticalDistance = 0.5f;
        facts.Blockers[0].HorizontalDistance = 0.75f;
        PlanterStackResult result = PlanterStackRules.Decide(facts);
        Check("allow gap equal to the minimum", result.Allowed);
    }

    private static void AllowSideBySideUnderTheVerticalMinimum()
    {
        PlanterStackFacts facts = Facts();
        facts.MinimumVerticalGap = 1f;
        facts.Blockers[0].VerticalDistance = 0.05f;
        facts.Blockers[0].HorizontalDistance = 1.5f;
        PlanterStackResult result = PlanterStackRules.Decide(facts);
        Check("allow side by side when horizontal overlap is clear", result.Allowed);
    }

    private static void AllowTouchingWhenGapIsZero()
    {
        PlanterStackFacts facts = Facts();
        facts.MinimumVerticalGap = 0f;
        facts.Blockers[0].VerticalDistance = 0f;
        facts.Blockers[0].HorizontalDistance = 0f;
        PlanterStackResult result = PlanterStackRules.Decide(facts);
        Check("allow touching planters when gap is zero", result.Allowed);
    }

    private static void DenyUnconfiguredPlanterBlocker()
    {
        PlanterStackFacts facts = Facts();
        facts.Blockers[0].IsConfiguredPlanter = false;
        PlanterStackResult result = PlanterStackRules.Decide(facts);
        Check("deny planter that is not in the configured list", !result.Allowed && result.Reason == PlanterStackDenyReason.BlockedByNonPlanter);
    }

    private static void DoNotInterveneWhenNothingBlocks()
    {
        PlanterStackFacts facts = Facts();
        facts.Blockers.Clear();
        PlanterStackResult result = PlanterStackRules.Decide(facts);
        Check("do not intervene when nothing blocks", !result.Allowed && result.Reason == PlanterStackDenyReason.NoPlanterBlock);
    }

    private static void IgnoreBlankVanillaChecks()
    {
        PlanterStackFacts facts = Facts();
        facts.FailedVanillaChecks.Add("  ");
        PlanterStackResult result = PlanterStackRules.Decide(facts);
        Check("blank vanilla check does not deny", result.Allowed);
    }

    private static void IdentifierMatchesLargePrefab()
    {
        List<string> ids = new List<string> { "planter.large", "planter.small" };
        bool path = PlanterStackRules.Matches("assets/prefabs/deployable/planters/planter.large.deployed.prefab", ids);
        bool deployed = PlanterStackRules.Matches("planter.large.deployed", ids);
        Check("large planter identifiers match prefab path", path && deployed);
    }

    private static void IdentifierDoesNotMatchSmallPrefab()
    {
        List<string> ids = new List<string> { "planter.large" };
        bool small = PlanterStackRules.Matches("planter.small.deployed", ids);
        bool furnace = PlanterStackRules.Matches("furnace.prefab", ids);
        Check("large identifier does not match other deployables", !small && !furnace);
    }

    private static void IdentifierMatchesDecorPlanter()
    {
        List<string> ids = new List<string> { "railroadplanter", "bathtub.planter", "minecart.planter" };
        bool rail = PlanterStackRules.Matches("assets/prefabs/misc/decor_dlc/rail road planter/railroadplanter.deployed.prefab", ids);
        bool bath = PlanterStackRules.Matches("bathtub.planter.deployed", ids);
        bool cart = PlanterStackRules.Matches("minecart.planter.deployed", ids);
        Check("decor planter identifiers match", rail && bath && cart);
    }

    private static void IdentifierIgnoresBlankEntries()
    {
        List<string> ids = new List<string> { " ", "", "planter.triangle" };
        bool match = PlanterStackRules.Matches("planter.triangle.deployed", ids);
        bool empty = PlanterStackRules.Matches("box.wooden", new List<string> { " ", null });
        Check("blank identifiers are ignored", match && !empty);
    }

    private static void IdentifierIsCaseInsensitive()
    {
        List<string> ids = new List<string> { "Planter.Large" };
        bool match = PlanterStackRules.Matches("PLANTER.LARGE.DEPLOYED", ids);
        Check("identifier match is case insensitive", match);
    }

    private static void NullFactsDeny()
    {
        PlanterStackResult result = PlanterStackRules.Decide(null);
        Check("null facts deny", !result.Allowed && result.Reason == PlanterStackDenyReason.Disabled);
    }
}
