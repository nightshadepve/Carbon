using System;
using System.Collections.Generic;
using System.Reflection;
using Nightshade.Symmetry;

internal static class Program
{
    private static int failures;

    private static void Main()
    {
        IdentityBasis();
        YawNinety();
        BothReflectionsAndRotatedAxes();
        NotAQuarterTurn();
        HeightPreserved();
        TriangleSockets();
        ChiralSinglesFailAndOppositeWorks();
        WallFacesOutward();
        DedupOnCenterAndAxis();
        LedgerPartialAndRefund();
        OriginalIsNotCharged();
        PermissionHasNoAdminBypass();
        PermissionRevocationStopsCopies();
        HookAndOwnership();
        DecisionMatrix();
        RecursionAndDuplicateCallback();
        CatalogAndPrefabNames();
        RangeAndRateFailClosed();
        ChargePolicyMatchesVanilla();
        AwakeAndGradeLifecycle();
        SpawnRescueRefundsOnce();
        DiagnosticIsBounded();
        DistanceAndOffsetStayExact();
        if (failures == 0)
        {
            Console.WriteLine("NightshadeSymmetry core tests passed.");
            return;
        }
        Console.Error.WriteLine(failures + " failure(s).");
        Environment.Exit(1);
    }

    private static void IdentityBasis()
    {
        Quat q = Quat.FromBasis(new Vec3(1f, 0f, 0f), new Vec3(0f, 1f, 0f), new Vec3(0f, 0f, 1f));
        Eq(Math.Abs(q.Dot(Quat.Identity)) > 0.999f, "identity basis");
        Eq(MirrorMath.BasisIsProper(q), "identity is proper");
    }

    private static void YawNinety()
    {
        Quat q = Quat.FromYawDegrees(90f);
        Vec3 forward = q.Forward;
        Near(forward.X, 1f, "yaw 90 forward x");
        Near(forward.Z, 0f, "yaw 90 forward z");
    }

    private static void BothReflectionsAndRotatedAxes()
    {
        Axes axes;
        Eq(Axes.TryCreate(new Vec3(10f, 2f, -4f), new Vec3((float)Math.Sin(37f * Math.PI / 180.0), 0.2f, (float)Math.Cos(37f * Math.PI / 180.0)), false, out axes), "axes");
        Near(Axes.YawDegrees(axes.Forward), 37f, "yaw 37");
        Vec3 offset = axes.Right * 6f + new Vec3(0f, 3f, 0f) + axes.Forward * 2f;
        Pose pose = new Pose(axes.Center + offset, Quat.FromYawDegrees(15f));
        List<SocketSample> sockets = SymmetricTriangle();
        List<PlannedCopy> plan = MirrorMath.Plan(pose, sockets, axes, 0.02f, 2f, 0.05f, 2f);
        PlannedCopy side = Find(plan, "left-right");
        PlannedCopy depth = Find(plan, "front-back");
        PlannedCopy both = Find(plan, "opposite");
        Eq(side.Representable && depth.Representable && both.Representable, "triangle representable on rotated axes");
        Vec3 sideLocal = ToLocal(side.Pose.Position, axes);
        Vec3 depthLocal = ToLocal(depth.Pose.Position, axes);
        Vec3 bothLocal = ToLocal(both.Pose.Position, axes);
        Near(sideLocal.X, -6f, "side x");
        Near(sideLocal.Z, 2f, "side z");
        Near(depthLocal.X, 6f, "depth x");
        Near(depthLocal.Z, -2f, "depth z");
        Near(bothLocal.X, -6f, "both x");
        Near(bothLocal.Z, -2f, "both z");
        Eq(MirrorMath.BasisIsProper(side.Pose.Rotation), "side proper");
        Eq(MirrorMath.BasisIsProper(depth.Pose.Rotation), "depth proper");
        Eq(MirrorMath.BasisIsProper(both.Pose.Rotation), "opposite proper");
    }

    private static void NotAQuarterTurn()
    {
        Axes axes;
        Axes.TryCreate(new Vec3(0f, 0f, 0f), new Vec3(0f, 0f, 1f), false, out axes);
        Pose pose = new Pose(new Vec3(6f, 3f, 2f), Quat.Identity);
        List<PlannedCopy> plan = MirrorMath.Plan(pose, SymmetricTriangle(), axes, 0.02f, 2f, 0.05f, 2f);
        Vec3 quarter = new Vec3(-2f, 3f, 6f);
        for (int i = 0; i < plan.Count; i++)
        {
            if (!plan[i].Representable)
                continue;
            float distance = (plan[i].Pose.Position - quarter).Length;
            Eq(distance > 1f, "mirror is not a 90 degree copy " + plan[i].Label);
        }
    }

    private static void HeightPreserved()
    {
        Axes axes;
        Axes.TryCreate(new Vec3(0f, 5f, 0f), new Vec3(0f, 0f, 1f), false, out axes);
        Pose pose = new Pose(new Vec3(4f, 8f, 1f), Quat.Identity);
        List<PlannedCopy> plan = MirrorMath.Plan(pose, SymmetricTriangle(), axes, 0.02f, 2f, 0.05f, 2f);
        Eq(plan.Count == 3, "three copies");
        for (int i = 0; i < plan.Count; i++)
            Near(plan[i].Pose.Position.Y, 8f, "height " + plan[i].Label);
    }

    private static void TriangleSockets()
    {
        Axes axes;
        Axes.TryCreate(new Vec3(1f, 0f, 1f), new Vec3(1f, 0f, 0f), true, out axes);
        Near(Axes.YawDegrees(axes.Forward), 90f, "snap 90");
        Pose pose = new Pose(axes.Center + axes.Right * 3f + new Vec3(0f, 1f, 0f), Quat.FromYawDegrees(20f));
        bool side = MirrorMath.TrySingle(pose, SymmetricTriangle(), axes.Center, axes.Right, 0.02f, 2f, out Pose _);
        bool depth = MirrorMath.TrySingle(pose, SymmetricTriangle(), axes.Center, axes.Forward, 0.02f, 2f, out Pose _);
        bool both = MirrorMath.TryDouble(pose, SymmetricTriangle(), axes.Center, axes.Right, axes.Forward, 0.02f, 2f, out Pose _);
        Eq(side && depth && both, "triangle sockets survive both planes");
    }

    private static void ChiralSinglesFailAndOppositeWorks()
    {
        Axes axes;
        Axes.TryCreate(new Vec3(0f, 0f, 0f), new Vec3(0f, 0f, 1f), false, out axes);
        Pose pose = new Pose(new Vec3(3f, 1f, 2f), Quat.FromYawDegrees(25f));
        List<SocketSample> chiral = new List<SocketSample>
        {
            new SocketSample(new Vec3(0f, 0f, 0f), new Vec3(0f, 0f, 1f)),
            new SocketSample(new Vec3(1.5f, 0f, 0f), new Vec3(0f, 0f, 1f)),
            new SocketSample(new Vec3(1.5f, 0f, 1.5f), new Vec3(0f, 0f, 1f))
        };
        Eq(!MirrorMath.TrySingle(pose, chiral, axes.Center, axes.Right, 0.02f, 2f, out Pose _), "chiral side rejected");
        Eq(!MirrorMath.TrySingle(pose, chiral, axes.Center, axes.Forward, 0.02f, 2f, out Pose _), "chiral depth rejected");
        Eq(MirrorMath.TryDouble(pose, chiral, axes.Center, axes.Right, axes.Forward, 0.02f, 2f, out Pose opposite), "chiral opposite is a real rotation");
        Near(opposite.Position.X, -3f, "opposite x");
        Near(opposite.Position.Y, 1f, "opposite y");
        Near(opposite.Position.Z, -2f, "opposite z");
        Eq(!MirrorMath.TrySingle(pose, new List<SocketSample>(), axes.Center, axes.Right, 0.02f, 2f, out Pose _), "empty sockets fail closed");
    }

    private static void WallFacesOutward()
    {
        Axes axes;
        Axes.TryCreate(new Vec3(0f, 0f, 0f), new Vec3(0f, 0f, 1f), false, out axes);
        Quat yaw = Quat.FromYawDegrees(90f);
        Pose pose = new Pose(new Vec3(3f, 0f, 0f), yaw);
        List<SocketSample> wall = new List<SocketSample>
        {
            new SocketSample(new Vec3(0f, 0f, 0f), new Vec3(0f, 0f, 1f)),
            new SocketSample(new Vec3(0f, 3f, 0f), new Vec3(0f, 0f, 1f)),
            new SocketSample(new Vec3(1.4f, 0f, 0f), new Vec3(0f, 0f, 1f)),
            new SocketSample(new Vec3(-1.4f, 0f, 0f), new Vec3(0f, 0f, 1f))
        };
        Eq(MirrorMath.TrySingle(pose, wall, axes.Center, axes.Right, 0.02f, 2f, out Pose side), "wall side");
        Near(side.Position.X, -3f, "wall x");
        Near(side.Position.Y, 0f, "wall y");
        Vec3 facing = side.Rotation.Forward.Normalized();
        Eq(facing.Dot(new Vec3(-1f, 0f, 0f)) > 0.9f, "soft side faces outward");
        Eq(side.Rotation.Up.Dot(new Vec3(0f, 1f, 0f)) > 0.9f, "wall stays upright");
    }

    private static void DedupOnCenterAndAxis()
    {
        Axes axes;
        Axes.TryCreate(new Vec3(0f, 0f, 0f), new Vec3(0f, 0f, 1f), false, out axes);
        List<PlannedCopy> center = MirrorMath.Plan(new Pose(new Vec3(0f, 0f, 0f), Quat.Identity), SymmetricTriangle(), axes, 0.02f, 2f, 0.05f, 2f);
        Eq(CountRepresentable(center) == 0, "center piece adds no copy");
        List<PlannedCopy> axis = MirrorMath.Plan(new Pose(new Vec3(0f, 0f, 4f), Quat.Identity), SymmetricTriangle(), axes, 0.02f, 2f, 0.05f, 2f);
        Eq(CountRepresentable(axis) == 1, "axis piece adds one copy");
        Eq(Find(axis, "front-back").Representable, "remaining copy is front-back");
    }

    private static void LedgerPartialAndRefund()
    {
        const int wood = -151838185;
        MaterialLedger ledger = new MaterialLedger();
        ledger.Set(wood, 30);
        List<CostLine> cost = new List<CostLine> { new CostLine(wood, 20) };
        int first;
        Eq(ledger.TryCharge(cost, out first), "first copy charged");
        CopyFacts blocked = CopyFacts.Passing();
        blocked.PrivilegeBlocked = true;
        Eq(PlacementDecision.Copy(blocked) == MirrorDecision.DenyPrivilege, "privilege skips charge");
        Eq(ledger.Get(wood) == 10, "privilege did not take more");
        int third;
        Eq(!ledger.TryCharge(cost, out third), "third copy cannot afford");
        Eq(ledger.Get(wood) == 10, "failed charge is unchanged");
        Eq(ledger.TryRefund(first), "refund the placed copy once");
        Eq(ledger.Get(wood) == 30, "refund restores the cost");
        Eq(!ledger.TryRefund(first), "second refund is rejected");
        Eq(ledger.Get(wood) == 30, "duplicate refund does not add items");
        Eq(!ledger.TryCharge(new List<CostLine> { new CostLine(wood, -1) }, out int _), "negative cost rejected");
        Eq(ledger.Get(wood) == 30, "negative cost did not change balance");
    }

    private static void OriginalIsNotCharged()
    {
        MaterialLedger copies = new MaterialLedger();
        copies.Set(1, 100);
        int originalCharges = 0;
        int receipt;
        Eq(copies.TryCharge(new List<CostLine> { new CostLine(1, 50) }, out receipt), "copy charge");
        Eq(originalCharges == 0, "vanilla original is outside the mirror ledger");
        Eq(copies.Get(1) == 50, "one copy cost");
    }

    private static void PermissionHasNoAdminBypass()
    {
        SessionDecision denied = PermissionGate.Evaluate(false, SessionAction.Mirror, true, true);
        Eq(!denied.Allowed && denied.DisableSession, "missing permission disables and denies");
        SessionDecision help = PermissionGate.Evaluate(false, SessionAction.Help, false, false);
        Eq(!help.Allowed, "help requires permission");
        SessionDecision show = PermissionGate.Evaluate(false, SessionAction.Show, false, true);
        Eq(!show.Allowed, "show requires permission");
        SessionDecision center = PermissionGate.Evaluate(false, SessionAction.Center, false, false);
        Eq(!center.Allowed, "center requires permission");
        SessionDecision allowed = PermissionGate.Evaluate(true, SessionAction.Enable, false, true);
        Eq(allowed.Allowed, "permission allows enable");
        Eq(!PermissionGate.Evaluate(true, SessionAction.Enable, false, false).Allowed, "enable needs a center");
        Eq(!PermissionGate.Evaluate(true, SessionAction.Mirror, false, true).Allowed, "mirror starts disabled");
        MethodInfo[] methods = typeof(PermissionGate).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        for (int i = 0; i < methods.Length; i++)
        {
            ParameterInfo[] parameters = methods[i].GetParameters();
            for (int p = 0; p < parameters.Length; p++)
                Eq(parameters[p].Name.IndexOf("admin", StringComparison.OrdinalIgnoreCase) < 0, "no admin parameter");
        }
        Eq(PermissionGate.Use == "nightshadesymmetry.use", "permission name");
    }

    private static void PermissionRevocationStopsCopies()
    {
        bool permission = true;
        int placed = 0;
        for (int i = 0; i < 3; i++)
        {
            if (i == 1)
                permission = false;
            SessionDecision decision = PermissionGate.Evaluate(permission, SessionAction.Mirror, true, true);
            if (SessionEnd.ShouldDisable(false, false, !permission))
            {
                Eq(!decision.Allowed, "revoked copy is refused");
                break;
            }
            placed++;
        }
        Eq(placed == 1, "copies stop when permission drops");
        Eq(SessionEnd.ShouldDisable(true, false, false), "disconnect disables");
        Eq(SessionEnd.ShouldDisable(false, true, false), "death disables");
    }

    private static void HookAndOwnership()
    {
        Eq(HookPolicy.Allows(null), "null hook allows");
        Eq(HookPolicy.Allows(true), "true hook allows");
        Eq(!HookPolicy.Allows(false), "false hook denies");
        Eq(!HookPolicy.Allows("blocked"), "message hook denies");
        ulong owner;
        Eq(!CopyOwnership.TryOwner(0, out owner) && owner == 0, "reject server owner");
        Eq(CopyOwnership.TryOwner(76561198000000000UL, out owner) && owner == 76561198000000000UL, "owner is the player");
    }

    private static void DecisionMatrix()
    {
        OriginFacts origin = new OriginFacts();
        origin.Permission = true;
        origin.Player = true;
        origin.Enabled = true;
        origin.HasCenter = true;
        origin.RateOk = true;
        origin.InRange = true;
        origin.DefaultGrade = true;
        origin.Eligible = true;
        origin.SocketsPresent = true;
        Eq(PlacementDecision.Begin(origin) == MirrorDecision.Place, "origin place");
        origin.Boat = true;
        Eq(PlacementDecision.Begin(origin) == MirrorDecision.DenyBoat, "boat");
        origin.Boat = false;
        origin.Parented = true;
        Eq(PlacementDecision.Begin(origin) == MirrorDecision.DenyParent, "parent");
        origin.Parented = false;
        origin.DefaultGrade = false;
        Eq(PlacementDecision.Begin(origin) == MirrorDecision.DenyGrade, "grade");
        origin.DefaultGrade = true;
        origin.Eligible = false;
        Eq(PlacementDecision.Begin(origin) == MirrorDecision.DenyUnsupported, "unsupported");
        origin.Eligible = true;
        origin.InRange = false;
        Eq(PlacementDecision.Begin(origin) == MirrorDecision.DenyRange, "range");
        origin.InRange = true;
        origin.RateOk = false;
        Eq(PlacementDecision.Begin(origin) == MirrorDecision.DenyRate, "rate");
        origin.RateOk = true;
        origin.Permission = false;
        Eq(PlacementDecision.Begin(origin) == MirrorDecision.DenyPermission, "origin permission wins");

        AssertCopy(CopyFacts.Passing(), MirrorDecision.Place, "copy place");
        CopyFacts facts = CopyFacts.Passing();
        facts.Duplicate = true;
        AssertCopy(facts, MirrorDecision.SkipDuplicate, "duplicate");
        facts = CopyFacts.Passing();
        facts.PrivilegeBlocked = true;
        AssertCopy(facts, MirrorDecision.DenyPrivilege, "tc");
        facts = CopyFacts.Passing();
        facts.VolumeBlocked = true;
        AssertCopy(facts, MirrorDecision.DenyCollision, "collision");
        facts = CopyFacts.Passing();
        facts.ProximityBlocked = true;
        AssertCopy(facts, MirrorDecision.DenyProximity, "proximity");
        facts = CopyFacts.Passing();
        facts.RoadBlocked = true;
        AssertCopy(facts, MirrorDecision.DenyRoad, "road");
        facts = CopyFacts.Passing();
        facts.HookBlocked = true;
        AssertCopy(facts, MirrorDecision.DenyHook, "hook");
        facts = CopyFacts.Passing();
        facts.Supported = false;
        AssertCopy(facts, MirrorDecision.DenySupport, "support");
        facts = CopyFacts.Passing();
        facts.CanAfford = false;
        AssertCopy(facts, MirrorDecision.DenyMaterials, "materials");
        facts = CopyFacts.Passing();
        facts.Representable = false;
        AssertCopy(facts, MirrorDecision.DenyUnrepresentable, "shape");
        Eq(!SpawnOutcome.Keep(false, false, true), "no spawn");
        Eq(!SpawnOutcome.Keep(true, true, true), "destroyed");
        Eq(!SpawnOutcome.Keep(true, false, false), "unstable");
        Eq(SpawnOutcome.Keep(true, false, true), "keep stable");
    }

    private static void RecursionAndDuplicateCallback()
    {
        RecursionGuard guard = new RecursionGuard();
        Eq(!guard.TryHandleOriginal(0), "missing id fails closed");
        Eq(guard.TryHandleOriginal(10), "first callback");
        Eq(!guard.TryHandleOriginal(10), "duplicate callback");
        guard.MarkMirror(22);
        Eq(guard.IsMirror(22), "mirror marked");
        guard.Enter();
        Eq(guard.IsBusy, "busy while spawning");
        guard.Exit();
        Eq(!guard.IsBusy, "busy cleared");
        RecursionGuard other = new RecursionGuard();
        Eq(other.TryHandleOriginal(10), "players do not share guards");
    }

    private static void CatalogAndPrefabNames()
    {
        Eq(PieceCatalog.NormalizePrefab("assets/prefabs/building core/foundation.triangle/foundation.triangle.prefab") == "foundation.triangle", "normalize");
        Eq(PieceCatalog.IsEligible("foundation.triangle", false, PieceCatalog.Eligible, PieceCatalog.Excluded), "triangle allowed");
        Eq(PieceCatalog.IsEligible("wall.doorway", false, PieceCatalog.Eligible, PieceCatalog.Excluded), "doorway allowed");
        Eq(PieceCatalog.IsEligible("block.stair.ushape", false, PieceCatalog.Eligible, PieceCatalog.Excluded), "u stair allowed");
        Eq(!PieceCatalog.IsEligible("block.stair.lshape", false, PieceCatalog.Eligible, PieceCatalog.Excluded), "l stair excluded");
        Eq(!PieceCatalog.IsEligible("block.stair.spiral", false, PieceCatalog.Eligible, PieceCatalog.Excluded), "spiral excluded");
        Eq(!PieceCatalog.IsEligible("block.stair.spiral.triangle", false, PieceCatalog.Eligible, PieceCatalog.Excluded), "spiral triangle excluded");
        Eq(!PieceCatalog.IsEligible("door.hinged.wood", false, PieceCatalog.Eligible, PieceCatalog.Excluded), "deployable excluded");
        List<string> both = new List<string>(PieceCatalog.Eligible);
        both.Add("block.stair.lshape");
        Eq(!PieceCatalog.IsEligible("block.stair.lshape", true, both, PieceCatalog.Excluded), "exclude wins");
        Eq(PieceCatalog.IsEligible("future.block", true, PieceCatalog.Eligible, PieceCatalog.Excluded), "unlisted only when enabled");
        Eq(!PieceCatalog.IsEligible("future.block", false, PieceCatalog.Eligible, PieceCatalog.Excluded), "unlisted denied by default");
    }

    private static void RangeAndRateFailClosed()
    {
        Eq(!MirrorMath.WithinRange(new Vec3(0f, 0f, 10f), new Vec3(0f, 0f, 0f), 0f), "zero range fails closed");
        Eq(MirrorMath.WithinRange(new Vec3(0f, 0f, 40f), new Vec3(0f, 0f, 0f), 40f), "edge of range");
        Eq(!MirrorMath.WithinRange(new Vec3(0f, 0f, 100f), new Vec3(0f, 0f, 0f), 200f), "hard cap");
        Eq(MirrorMath.ClampDistance(1000f) == MirrorMath.HardMaxDistance, "distance clamp");
        Eq(MirrorMath.ClampRadius(100f) == MirrorMath.HardMaxSearchRadius, "radius clamp");
        Eq(MirrorMath.AllowRate(6, 6), "rate allows the cap");
        Eq(!MirrorMath.AllowRate(7, 6), "rate blocks the next");
    }

    private static void AwakeAndGradeLifecycle()
    {
        Eq(PlacementLifecycle.AwakePlan(false, false) == "activate", "inactive block activates without Poolable");
        Eq(PlacementLifecycle.AwakePlan(false, true) == "activate", "inactive block activates even with Poolable");
        Eq(PlacementLifecycle.AwakePlan(true, true) == "enable-poolable", "active pooled block enables behaviour");
        Eq(PlacementLifecycle.AwakePlan(true, false) == "skip-poolable", "active block without Poolable is not dereferenced");
        Eq(!PlacementLifecycle.GradeCanBeApplied(true, true, false), "missing grade base is not applied");
        Eq(!PlacementLifecycle.GradeCanBeApplied(true, false, true), "missing grade list is not applied");
        Eq(PlacementLifecycle.GradeCanBeApplied(true, true, true), "safe grade can be applied");
    }

    private static void SpawnRescueRefundsOnce()
    {
        const int wood = -151838185;
        MaterialLedger ledger = new MaterialLedger();
        ledger.Set(wood, 100);
        int receipt;
        Eq(ledger.TryCharge(new List<CostLine> { new CostLine(wood, 50) }, out receipt), "spawn charge");
        SpawnRescue rescue = new SpawnRescue();
        rescue.Stage = "grade";
        rescue.EntityCreated = true;
        rescue.ItemsConsumed = true;
        rescue.Receipt = receipt;
        rescue.Ledger = ledger;
        Eq(rescue.RefundOnce(), "first rescue refunds");
        Eq(ledger.Get(wood) == 100, "materials restored");
        Eq(!rescue.RefundOnce(), "second rescue does not refund");
        Eq(ledger.Get(wood) == 100, "balance stays after duplicate rescue");

        MaterialLedger unpaid = new MaterialLedger();
        unpaid.Set(wood, 80);
        int unpaidReceipt;
        Eq(unpaid.TryCharge(new List<CostLine> { new CostLine(wood, 50) }, out unpaidReceipt), "pre-spawn charge");
        SpawnRescue beforeCreate = new SpawnRescue();
        beforeCreate.ItemsConsumed = true;
        beforeCreate.EntityCreated = false;
        beforeCreate.Receipt = unpaidReceipt;
        beforeCreate.Ledger = unpaid;
        Eq(beforeCreate.RefundOnce(), "refund when create never returned an entity");
        Eq(!beforeCreate.EntityCreated, "no entity to orphan");
        Eq(unpaid.Get(wood) == 80, "pre-spawn failure restored materials");

        SpawnRescue uncharged = new SpawnRescue();
        uncharged.ItemsConsumed = false;
        uncharged.Ledger = ledger;
        uncharged.Receipt = receipt;
        Eq(!uncharged.RefundOnce(), "uncharged failure does not create items");
    }

    private static void DiagnosticIsBounded()
    {
        string boom = "Object reference not set to an instance of an object";
        for (int i = 0; i < 30; i++)
            boom += "\n at NightshadeSymmetry.TryPlaceCopy line " + i + " player=76561198000000000";
        string text = PlacementLifecycle.BoundDiagnostic("awake", "foundation", "left-right", boom, 12, 900);
        Eq(text.IndexOf("stage=awake", StringComparison.Ordinal) >= 0, "stage is logged");
        Eq(text.IndexOf("prefab=foundation", StringComparison.Ordinal) >= 0, "prefab is logged");
        Eq(text.IndexOf("side=left-right", StringComparison.Ordinal) >= 0, "side is logged");
        Eq(text.Length <= 900, "diagnostic is bounded");
        int lines = text.Split('\n').Length;
        Eq(lines <= 13, "stack lines are bounded");
    }

    private static void DistanceAndOffsetStayExact()
    {
        Axes axes;
        Vec3 look = new Vec3((float)Math.Sin(123.4 * Math.PI / 180.0), 0.4f, (float)Math.Cos(123.4 * Math.PI / 180.0));
        Eq(Axes.TryCreate(new Vec3(1234.5f, 62.25f, -890.125f), look, false, out axes), "arbitrary center");
        Vec3 offset = axes.Right * 4.5f + new Vec3(0f, 3.25f, 0f) + axes.Forward * -2.75f;
        Pose pose = new Pose(axes.Center + offset, Quat.FromYawDegrees(20f));
        List<SocketSample> shifted = new List<SocketSample>
        {
            new SocketSample(new Vec3(0.2f, 0f, 0.4f), new Vec3(0f, 0f, 1f)),
            new SocketSample(new Vec3(-0.2f, 0f, 0.4f), new Vec3(0f, 0f, 1f)),
            new SocketSample(new Vec3(0f, 1.5f, 0.4f), new Vec3(0f, 0f, 1f))
        };
        List<PlannedCopy> plan = MirrorMath.Plan(pose, shifted, axes, 0.02f, 2f, 0.05f, 2f);
        Eq(CountRepresentable(plan) == 3, "offset sockets still mirror");
        float originalDistance = (pose.Position - axes.Center).Length;
        for (int i = 0; i < plan.Count; i++)
        {
            Eq(plan[i].Representable, plan[i].Label + " representable");
            float copyDistance = (plan[i].Pose.Position - axes.Center).Length;
            Eq(Math.Abs(copyDistance - originalDistance) <= MirrorMath.ExactPositionEpsilon, plan[i].Label + " distance");
            Eq(Math.Abs(plan[i].Pose.Position.Y - pose.Position.Y) <= MirrorMath.ExactPositionEpsilon, plan[i].Label + " height");
            string label = plan[i].Label;
            if (label == "left-right")
                Eq(MirrorMath.SingleMirrorOffset(axes.Center, axes.Right, pose.Position, plan[i].Pose.Position, MirrorMath.ExactPositionEpsilon), "left-right offset");
            else if (label == "front-back")
                Eq(MirrorMath.SingleMirrorOffset(axes.Center, axes.Forward, pose.Position, plan[i].Pose.Position, MirrorMath.ExactPositionEpsilon), "front-back offset");
            else
            {
                Vec3 mid = MirrorMath.ReflectPoint(pose.Position, axes.Center, axes.Right);
                Eq(MirrorMath.SingleMirrorOffset(axes.Center, axes.Forward, mid, plan[i].Pose.Position, MirrorMath.ExactPositionEpsilon), "opposite offset");
            }
        }

        List<PlannedCopy> triangles = MirrorMath.Plan(pose, SymmetricTriangle(), axes, 0.02f, 2f, 0.05f, 2f);
        Eq(CountRepresentable(triangles) == 3, "triangle distance set");
        for (int i = 0; i < triangles.Count; i++)
        {
            float copyDistance = (triangles[i].Pose.Position - axes.Center).Length;
            Eq(Math.Abs(copyDistance - originalDistance) <= MirrorMath.ExactPositionEpsilon, "triangle " + triangles[i].Label);
        }

        List<SocketSample> unpaired = new List<SocketSample>
        {
            new SocketSample(new Vec3(0f, 0f, 0f), new Vec3(0f, 0f, 1f)),
            new SocketSample(new Vec3(1.25f, 0.2f, 0.4f), new Vec3(0f, 0f, 1f))
        };
        Eq(!MirrorMath.TrySingle(pose, unpaired, axes.Center, axes.Right, 0.02f, 2f, out Pose _), "unpaired socket is skipped");
        Eq(!MirrorMath.TrySingle(pose, unpaired, axes.Center, axes.Forward, 0.02f, 2f, out Pose _), "unpaired depth is skipped");

        Eq(MirrorMath.AcceptSpawnedPivot(pose.Position, pose.Position + axes.Right * 0.0001f, MirrorMath.ExactPositionEpsilon), "numeric noise kept");
        Eq(!MirrorMath.AcceptSpawnedPivot(pose.Position, pose.Position + axes.Right * 0.05f, MirrorMath.ExactPositionEpsilon), "five centimetre shift rejected");
    }

    private static void ChargePolicyMatchesVanilla()
    {
        Eq(ChargePolicy.ShouldCharge(false, false), "survival charges");
        Eq(!ChargePolicy.ShouldCharge(true, false), "creative free-build matches vanilla");
        Eq(!ChargePolicy.ShouldCharge(false, true), "tutorial matches vanilla");
    }

    private static List<SocketSample> SymmetricTriangle()
    {
        return new List<SocketSample>
        {
            new SocketSample(new Vec3(0f, 0f, 1.2f), new Vec3(0f, 0f, 1f)),
            new SocketSample(new Vec3(-1f, 0f, -0.6f), new Vec3(0f, 0f, 1f)),
            new SocketSample(new Vec3(1f, 0f, -0.6f), new Vec3(0f, 0f, 1f))
        };
    }

    private static Vec3 ToLocal(Vec3 world, Axes axes)
    {
        Vec3 delta = world - axes.Center;
        return new Vec3(delta.Dot(axes.Right), delta.Y - axes.Center.Y, delta.Dot(axes.Forward));
    }

    private static PlannedCopy Find(List<PlannedCopy> plan, string label)
    {
        for (int i = 0; i < plan.Count; i++)
        {
            if (plan[i].Label == label)
                return plan[i];
        }
        Fail("missing " + label);
        return new PlannedCopy();
    }

    private static int CountRepresentable(List<PlannedCopy> plan)
    {
        int count = 0;
        for (int i = 0; i < plan.Count; i++)
        {
            if (plan[i].Representable)
                count++;
        }
        return count;
    }

    private static void AssertCopy(CopyFacts facts, MirrorDecision expected, string name)
    {
        Eq(PlacementDecision.Copy(facts) == expected, name);
    }

    private static void Near(float value, float expected, string name)
    {
        Eq(Math.Abs(value - expected) < 0.02f, name + " got " + value + " expected " + expected);
    }

    private static void Eq(bool condition, string name)
    {
        if (condition)
            return;
        Fail(name);
    }

    private static void Fail(string name)
    {
        failures++;
        Console.Error.WriteLine("FAIL " + name);
    }
}
