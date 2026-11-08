// NightshadeSymmetry for Carbon 2.0.262.
// Mirrored copies do not call Planner.DoBuild. That path rejects targets beyond
// the player's reach and line of sight. Each copy still uses the game checks:
// socket CanConnect and CheckSocketMods, IsOccupied, IsBuildingBlocked,
// DeployVolume.Check, BuildingProximity.Check, TestPlacingCloseToRoad,
// CanBuild hooks, then Spawn plus StabilityCheck. Cost matches
// Planner.PayForPlacement through ConstructionGrade.CostToBuild.
#if !NIGHTSHADE_SYMMETRY_CORE_ONLY
using System.Globalization;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;
#endif
using System;
using System.Collections.Generic;

namespace Nightshade.Symmetry
{
    public struct Vec3
    {
        public float X;
        public float Y;
        public float Z;

        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float Length
        {
            get { return (float)Math.Sqrt(X * X + Y * Y + Z * Z); }
        }

        public float SqrLength
        {
            get { return X * X + Y * Y + Z * Z; }
        }

        public static Vec3 operator +(Vec3 a, Vec3 b)
        {
            return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        }

        public static Vec3 operator -(Vec3 a, Vec3 b)
        {
            return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        }

        public static Vec3 operator *(Vec3 a, float s)
        {
            return new Vec3(a.X * s, a.Y * s, a.Z * s);
        }

        public static Vec3 operator *(float s, Vec3 a)
        {
            return a * s;
        }

        public float Dot(Vec3 b)
        {
            return X * b.X + Y * b.Y + Z * b.Z;
        }

        public Vec3 Cross(Vec3 b)
        {
            return new Vec3(
                Y * b.Z - Z * b.Y,
                Z * b.X - X * b.Z,
                X * b.Y - Y * b.X);
        }

        public Vec3 Normalized()
        {
            float length = Length;
            if (length < 1e-8f)
                return new Vec3(0f, 0f, 0f);
            return this * (1f / length);
        }
    }

    public struct Quat
    {
        public float X;
        public float Y;
        public float Z;
        public float W;

        public Quat(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public static Quat Identity
        {
            get { return new Quat(0f, 0f, 0f, 1f); }
        }

        public Vec3 Right
        {
            get { return Rotate(new Vec3(1f, 0f, 0f)); }
        }

        public Vec3 Up
        {
            get { return Rotate(new Vec3(0f, 1f, 0f)); }
        }

        public Vec3 Forward
        {
            get { return Rotate(new Vec3(0f, 0f, 1f)); }
        }

        public float Dot(Quat b)
        {
            return X * b.X + Y * b.Y + Z * b.Z + W * b.W;
        }

        public Quat Normalized()
        {
            float length = (float)Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
            if (length < 1e-8f)
                return Identity;
            return new Quat(X / length, Y / length, Z / length, W / length);
        }

        public Vec3 Rotate(Vec3 v)
        {
            Vec3 u = new Vec3(X, Y, Z);
            float s = W;
            return u * (2f * u.Dot(v))
                + v * (s * s - u.Dot(u))
                + u.Cross(v) * (2f * s);
        }

        public static Quat FromYawDegrees(float degrees)
        {
            float half = degrees * (float)Math.PI / 360f;
            return new Quat(0f, (float)Math.Sin(half), 0f, (float)Math.Cos(half));
        }

        public static Quat FromBasis(Vec3 right, Vec3 up, Vec3 forward)
        {
            float m00 = right.X, m01 = up.X, m02 = forward.X;
            float m10 = right.Y, m11 = up.Y, m12 = forward.Y;
            float m20 = right.Z, m21 = up.Z, m22 = forward.Z;
            float trace = m00 + m11 + m22;
            Quat q;
            if (trace > 0f)
            {
                float s = (float)Math.Sqrt(trace + 1f) * 2f;
                q = new Quat((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = (float)Math.Sqrt(1f + m00 - m11 - m22) * 2f;
                q = new Quat(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }
            else if (m11 > m22)
            {
                float s = (float)Math.Sqrt(1f + m11 - m00 - m22) * 2f;
                q = new Quat((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
            }
            else
            {
                float s = (float)Math.Sqrt(1f + m22 - m00 - m11) * 2f;
                q = new Quat((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s);
            }
            return q.Normalized();
        }

        public static float AngleDegrees(Quat a, Quat b)
        {
            float d = Math.Abs(a.Normalized().Dot(b.Normalized()));
            if (d > 1f)
                d = 1f;
            return (float)(Math.Acos(d) * 360.0 / Math.PI);
        }
    }

    public struct Pose
    {
        public Vec3 Position;
        public Quat Rotation;

        public Pose(Vec3 position, Quat rotation)
        {
            Position = position;
            Rotation = rotation;
        }
    }

    public struct SocketSample
    {
        public Vec3 LocalPosition;
        public Vec3 LocalForward;

        public SocketSample(Vec3 localPosition, Vec3 localForward)
        {
            LocalPosition = localPosition;
            LocalForward = localForward;
        }
    }

    public struct Axes
    {
        public Vec3 Center;
        public Vec3 Right;
        public Vec3 Forward;

        public static bool TryCreate(Vec3 center, Vec3 rawForward, bool snapToRightAngle, out Axes axes)
        {
            axes = new Axes();
            Vec3 flat = new Vec3(rawForward.X, 0f, rawForward.Z);
            if (flat.Length < 1e-4f)
                return false;
            float yaw = YawDegrees(flat);
            if (snapToRightAngle)
                yaw = (float)(Math.Round(yaw / 90.0) * 90.0);
            float rad = yaw * (float)Math.PI / 180f;
            Vec3 forward = new Vec3((float)Math.Sin(rad), 0f, (float)Math.Cos(rad));
            Vec3 right = new Vec3(forward.Z, 0f, -forward.X);
            axes.Center = center;
            axes.Right = right.Normalized();
            axes.Forward = forward.Normalized();
            return IsOrthonormal(axes.Right, axes.Forward);
        }

        public static bool IsOrthonormal(Vec3 right, Vec3 forward)
        {
            if (Math.Abs(right.Y) > 0.02f || Math.Abs(forward.Y) > 0.02f)
                return false;
            if (Math.Abs(right.Length - 1f) > 0.02f || Math.Abs(forward.Length - 1f) > 0.02f)
                return false;
            if (Math.Abs(right.Dot(forward)) > 0.02f)
                return false;
            return true;
        }

        public static float YawDegrees(Vec3 forward)
        {
            return (float)(Math.Atan2(forward.X, forward.Z) * 180.0 / Math.PI);
        }
    }

    public struct PlannedCopy
    {
        public string Label;
        public bool Representable;
        public Pose Pose;

        public PlannedCopy(string label, bool representable, Pose pose)
        {
            Label = label;
            Representable = representable;
            Pose = pose;
        }
    }

    public static class MirrorMath
    {
        public const float HardMaxDistance = 80f;
        public const float HardMaxSearchRadius = 6f;

        public static Vec3 ReflectVector(Vec3 value, Vec3 normal)
        {
            Vec3 n = normal.Normalized();
            return value - n * (2f * value.Dot(n));
        }

        public static Vec3 ReflectPoint(Vec3 point, Vec3 center, Vec3 normal)
        {
            return center + ReflectVector(point - center, normal);
        }

        public static bool WithinRange(Vec3 point, Vec3 center, float maxDistance)
        {
            if (maxDistance <= 0f)
                return false;
            float capped = maxDistance;
            if (capped > HardMaxDistance)
                capped = HardMaxDistance;
            return (point - center).Length <= capped + 0.001f;
        }

        public static float ClampDistance(float value)
        {
            if (value < 5f)
                return 5f;
            if (value > HardMaxDistance)
                return HardMaxDistance;
            return value;
        }

        public static float ClampRadius(float value)
        {
            if (value < 1f)
                return 1f;
            if (value > HardMaxSearchRadius)
                return HardMaxSearchRadius;
            return value;
        }

        public static int ClampRate(int value)
        {
            if (value < 1)
                return 1;
            if (value > 20)
                return 20;
            return value;
        }

        public static bool AllowRate(int countThisWindow, int maxPerSecond)
        {
            int max = ClampRate(maxPerSecond);
            return countThisWindow <= max;
        }

        public static List<PlannedCopy> Plan(
            Pose pose,
            IReadOnlyList<SocketSample> sockets,
            Axes axes,
            float socketPositionEpsilon,
            float socketAngleDegrees,
            float dedupPosition,
            float dedupAngle)
        {
            List<PlannedCopy> planned = new List<PlannedCopy>();
            Pose side;
            bool sideOk = TrySingle(pose, sockets, axes.Center, axes.Right, socketPositionEpsilon, socketAngleDegrees, out side);
            planned.Add(new PlannedCopy("left-right", sideOk, sideOk ? side : new Pose()));
            Pose depth;
            bool depthOk = TrySingle(pose, sockets, axes.Center, axes.Forward, socketPositionEpsilon, socketAngleDegrees, out depth);
            planned.Add(new PlannedCopy("front-back", depthOk, depthOk ? depth : new Pose()));
            Pose both;
            bool bothOk = TryDouble(pose, sockets, axes.Center, axes.Right, axes.Forward, socketPositionEpsilon, socketAngleDegrees, out both);
            planned.Add(new PlannedCopy("opposite", bothOk, bothOk ? both : new Pose()));
            return Dedup(pose, planned, dedupPosition, dedupAngle);
        }

        public static bool TrySingle(
            Pose pose,
            IReadOnlyList<SocketSample> sockets,
            Vec3 center,
            Vec3 normal,
            float positionEpsilon,
            float angleDegrees,
            out Pose result)
        {
            result = new Pose();
            if (sockets == null || sockets.Count == 0)
                return false;
            Vec3 reflectedPosition = ReflectPoint(pose.Position, center, normal);
            List<SocketWorld> expected = ReflectSockets(pose, sockets, center, normal, false, new Vec3());
            Vec3 trueUp = ReflectVector(pose.Rotation.Up, normal);
            bool found = false;
            float bestScore = -2f;
            Quat best = Quat.Identity;
            Vec3 reflectedRight = ReflectVector(pose.Rotation.Right, normal);
            Vec3 reflectedUp = ReflectVector(pose.Rotation.Up, normal);
            Vec3 reflectedForward = ReflectVector(pose.Rotation.Forward, normal);
            Quat[] candidates = new Quat[]
            {
                Quat.FromBasis(-1f * reflectedRight, reflectedUp, reflectedForward),
                Quat.FromBasis(reflectedRight, reflectedUp, -1f * reflectedForward),
                Quat.FromBasis(reflectedRight, -1f * reflectedUp, reflectedForward)
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                if (!BasisIsProper(candidates[i]))
                    continue;
                float score = UpScore(candidates[i], trueUp);
                if (score < 0.5f)
                    continue;
                Pose candidate = new Pose(reflectedPosition, candidates[i]);
                if (!MatchWorld(sockets, candidate, expected, positionEpsilon, angleDegrees))
                    continue;
                if (!found || score > bestScore)
                {
                    found = true;
                    bestScore = score;
                    best = candidates[i];
                }
            }
            if (!found)
                return false;
            result = new Pose(reflectedPosition, best);
            return true;
        }

        public static bool TryDouble(
            Pose pose,
            IReadOnlyList<SocketSample> sockets,
            Vec3 center,
            Vec3 firstNormal,
            Vec3 secondNormal,
            float positionEpsilon,
            float angleDegrees,
            out Pose result)
        {
            result = new Pose();
            if (sockets == null || sockets.Count == 0)
                return false;
            Vec3 position = ReflectPoint(ReflectPoint(pose.Position, center, firstNormal), center, secondNormal);
            Vec3 right = ReflectVector(ReflectVector(pose.Rotation.Right, firstNormal), secondNormal);
            Vec3 up = ReflectVector(ReflectVector(pose.Rotation.Up, firstNormal), secondNormal);
            Vec3 forward = ReflectVector(ReflectVector(pose.Rotation.Forward, firstNormal), secondNormal);
            Quat rotation = Quat.FromBasis(right, up, forward);
            result = new Pose(position, rotation);
            if (!BasisIsProper(rotation))
                return false;
            List<SocketWorld> expected = ReflectSockets(pose, sockets, center, firstNormal, true, secondNormal);
            return MatchWorld(sockets, result, expected, positionEpsilon, angleDegrees);
        }

        public static List<PlannedCopy> Dedup(Pose original, List<PlannedCopy> copies, float positionEpsilon, float angleDegrees)
        {
            List<PlannedCopy> kept = new List<PlannedCopy>();
            for (int i = 0; i < copies.Count; i++)
            {
                PlannedCopy copy = copies[i];
                if (!copy.Representable)
                {
                    kept.Add(copy);
                    continue;
                }
                if ((original.Position - copy.Pose.Position).Length <= positionEpsilon)
                    continue;
                bool duplicate = false;
                for (int k = 0; k < kept.Count; k++)
                {
                    if (kept[k].Representable && (kept[k].Pose.Position - copy.Pose.Position).Length <= positionEpsilon)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate)
                    kept.Add(copy);
            }
            return kept;
        }

        public static bool Close(Pose a, Pose b, float positionEpsilon, float angleDegrees)
        {
            if ((a.Position - b.Position).Length > positionEpsilon)
                return false;
            return Quat.AngleDegrees(a.Rotation, b.Rotation) <= angleDegrees;
        }

        public static bool BasisIsProper(Quat rotation)
        {
            Vec3 right = rotation.Right;
            Vec3 up = rotation.Up;
            Vec3 forward = rotation.Forward;
            if (Math.Abs(right.Length - 1f) > 0.05f || Math.Abs(up.Length - 1f) > 0.05f || Math.Abs(forward.Length - 1f) > 0.05f)
                return false;
            float triple = right.Dot(up.Cross(forward));
            return triple > 0.5f;
        }

        private static float UpScore(Quat rotation, Vec3 trueUp)
        {
            Vec3 up = rotation.Up;
            float ul = up.Length;
            float tl = trueUp.Length;
            if (ul < 1e-4f || tl < 1e-4f)
                return -2f;
            return up.Dot(trueUp) / (ul * tl);
        }

        private struct SocketWorld
        {
            public Vec3 Position;
            public Vec3 Forward;
            public bool HasForward;
        }

        private static List<SocketWorld> ReflectSockets(
            Pose pose,
            IReadOnlyList<SocketSample> sockets,
            Vec3 center,
            Vec3 normal,
            bool second,
            Vec3 secondNormal)
        {
            List<SocketWorld> list = new List<SocketWorld>(sockets.Count);
            for (int i = 0; i < sockets.Count; i++)
            {
                Vec3 world = pose.Position + pose.Rotation.Rotate(sockets[i].LocalPosition);
                Vec3 forward = pose.Rotation.Rotate(sockets[i].LocalForward);
                world = ReflectPoint(world, center, normal);
                forward = ReflectVector(forward, normal);
                if (second)
                {
                    world = ReflectPoint(world, center, secondNormal);
                    forward = ReflectVector(forward, secondNormal);
                }
                SocketWorld sample = new SocketWorld();
                sample.Position = world;
                sample.HasForward = forward.Length > 1e-4f;
                sample.Forward = sample.HasForward ? forward.Normalized() : new Vec3();
                list.Add(sample);
            }
            return list;
        }

        private static bool MatchWorld(
            IReadOnlyList<SocketSample> locals,
            Pose pose,
            List<SocketWorld> expected,
            float positionEpsilon,
            float angleDegrees)
        {
            if (locals.Count != expected.Count)
                return false;
            bool[] used = new bool[expected.Count];
            float limit = positionEpsilon * positionEpsilon;
            for (int i = 0; i < locals.Count; i++)
            {
                Vec3 world = pose.Position + pose.Rotation.Rotate(locals[i].LocalPosition);
                Vec3 forward = pose.Rotation.Rotate(locals[i].LocalForward);
                bool hasForward = forward.Length > 1e-4f;
                if (hasForward)
                    forward = forward.Normalized();
                int found = -1;
                for (int j = 0; j < expected.Count; j++)
                {
                    if (used[j])
                        continue;
                    Vec3 delta = world - expected[j].Position;
                    if (delta.Dot(delta) > limit)
                        continue;
                    if (hasForward && expected[j].HasForward)
                    {
                        float dot = forward.Dot(expected[j].Forward);
                        if (dot > 1f)
                            dot = 1f;
                        if (dot < -1f)
                            dot = -1f;
                        float degrees = (float)(Math.Acos(dot) * 180.0 / Math.PI);
                        if (degrees > angleDegrees)
                            continue;
                    }
                    found = j;
                    break;
                }
                if (found < 0)
                    return false;
                used[found] = true;
            }
            return true;
        }
    }

    public static class PieceCatalog
    {
        public static readonly string[] Eligible = new string[]
        {
            "foundation",
            "foundation.triangle",
            "foundation.steps",
            "floor",
            "floor.triangle",
            "floor.frame",
            "floor.triangle.frame",
            "wall",
            "wall.doorway",
            "wall.window",
            "wall.frame",
            "wall.half",
            "wall.low",
            "roof",
            "roof.triangle",
            "ramp",
            "block.stair.ushape"
        };

        public static readonly string[] Excluded = new string[]
        {
            "block.stair.lshape",
            "block.stair.spiral",
            "block.stair.spiral.triangle"
        };

        public static string NormalizePrefab(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "";
            string text = name.Replace('\\', '/').Trim();
            int slash = text.LastIndexOf('/');
            if (slash >= 0 && slash + 1 < text.Length)
                text = text.Substring(slash + 1);
            if (text.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(0, text.Length - ".prefab".Length);
            return text.ToLowerInvariant();
        }

        public static bool IsEligible(string shortName, bool allowUnlisted, IList<string> eligible, IList<string> excluded)
        {
            string name = NormalizePrefab(shortName);
            if (name.Length == 0)
                return false;
            if (Contains(excluded, name))
                return false;
            if (Contains(eligible, name))
                return true;
            return allowUnlisted;
        }

        private static bool Contains(IList<string> list, string name)
        {
            if (list == null)
                return false;
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(NormalizePrefab(list[i]), name, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }

    public struct CostLine
    {
        public int ItemId;
        public int Amount;

        public CostLine(int itemId, int amount)
        {
            ItemId = itemId;
            Amount = amount;
        }
    }

    public sealed class MaterialLedger
    {
        private readonly Dictionary<int, int> balance = new Dictionary<int, int>();
        private readonly List<int[]> receipts = new List<int[]>();
        private readonly List<bool> open = new List<bool>();

        public void Set(int itemId, int amount)
        {
            balance[itemId] = amount;
        }

        public int Get(int itemId)
        {
            int amount;
            if (balance.TryGetValue(itemId, out amount))
                return amount;
            return 0;
        }

        public bool TryCharge(IList<CostLine> cost, out int receipt)
        {
            receipt = 0;
            if (cost == null)
                return false;
            for (int i = 0; i < cost.Count; i++)
            {
                if (cost[i].Amount < 0 || cost[i].Amount > 1000000)
                    return false;
                if (Get(cost[i].ItemId) < cost[i].Amount)
                    return false;
            }
            int[] stored = new int[cost.Count * 2];
            for (int i = 0; i < cost.Count; i++)
            {
                int have = Get(cost[i].ItemId) - cost[i].Amount;
                balance[cost[i].ItemId] = have;
                stored[i * 2] = cost[i].ItemId;
                stored[i * 2 + 1] = cost[i].Amount;
            }
            receipts.Add(stored);
            open.Add(true);
            receipt = receipts.Count;
            return true;
        }

        public bool TryRefund(int receipt)
        {
            if (receipt <= 0 || receipt > receipts.Count)
                return false;
            if (!open[receipt - 1])
                return false;
            int[] stored = receipts[receipt - 1];
            for (int i = 0; i < stored.Length; i += 2)
                balance[stored[i]] = Get(stored[i]) + stored[i + 1];
            open[receipt - 1] = false;
            return true;
        }
    }

    public static class CopyOwnership
    {
        public static bool TryOwner(ulong playerId, out ulong ownerId)
        {
            if (playerId == 0)
            {
                ownerId = 0;
                return false;
            }
            ownerId = playerId;
            return true;
        }
    }

    public static class HookPolicy
    {
        public static bool Allows(object result)
        {
            if (result == null)
                return true;
            if (result is bool)
                return (bool)result;
            return false;
        }
    }

    public static class ChargePolicy
    {
        public static bool ShouldCharge(bool creativeFreeBuild, bool tutorial)
        {
            if (creativeFreeBuild)
                return false;
            if (tutorial)
                return false;
            return true;
        }
    }

    public enum SessionAction
    {
        Help,
        Status,
        Center,
        Enable,
        Disable,
        Reset,
        Show,
        Mirror
    }

    public struct SessionDecision
    {
        public bool Allowed;
        public bool DisableSession;
        public string Reason;
    }

    public static class PermissionGate
    {
        public const string Use = "nightshadesymmetry.use";

        public static SessionDecision Evaluate(bool hasUsePermission, SessionAction action, bool enabled, bool hasCenter)
        {
            SessionDecision decision = new SessionDecision();
            if (!hasUsePermission)
            {
                decision.Allowed = false;
                decision.DisableSession = true;
                decision.Reason = "permission";
                return decision;
            }
            if (action == SessionAction.Enable && !hasCenter)
            {
                decision.Allowed = false;
                decision.Reason = "center";
                return decision;
            }
            if (action == SessionAction.Mirror)
            {
                if (!hasCenter)
                {
                    decision.Allowed = false;
                    decision.Reason = "center";
                    return decision;
                }
                if (!enabled)
                {
                    decision.Allowed = false;
                    decision.Reason = "disabled";
                    return decision;
                }
            }
            decision.Allowed = true;
            decision.Reason = "ok";
            return decision;
        }
    }

    public static class SessionEnd
    {
        public static bool ShouldDisable(bool disconnected, bool dead, bool permissionLost)
        {
            return disconnected || dead || permissionLost;
        }
    }

    public enum MirrorDecision
    {
        Place,
        SkipDuplicate,
        DenyPermission,
        DenyDisabled,
        DenyCenter,
        DenyPlayer,
        DenyRate,
        DenyRange,
        DenyBoat,
        DenyParent,
        DenyGrade,
        DenyUnsupported,
        DenyUnrepresentable,
        DenyHook,
        DenyPrivilege,
        DenyCollision,
        DenyProximity,
        DenyRoad,
        DenySupport,
        DenyStability,
        DenyMaterials
    }

    public struct OriginFacts
    {
        public bool Permission;
        public bool Player;
        public bool Enabled;
        public bool HasCenter;
        public bool RateOk;
        public bool InRange;
        public bool Boat;
        public bool Parented;
        public bool DefaultGrade;
        public bool Eligible;
        public bool SocketsPresent;
    }

    public struct CopyFacts
    {
        public bool PermissionOk;
        public bool PlayerOk;
        public bool Representable;
        public bool Duplicate;
        public bool HookBlocked;
        public bool PrivilegeBlocked;
        public bool VolumeBlocked;
        public bool ProximityBlocked;
        public bool RoadBlocked;
        public bool SocketModsOk;
        public bool Supported;
        public bool CanAfford;

        public static CopyFacts Passing()
        {
            CopyFacts facts = new CopyFacts();
            facts.PermissionOk = true;
            facts.PlayerOk = true;
            facts.Representable = true;
            facts.SocketModsOk = true;
            facts.Supported = true;
            facts.CanAfford = true;
            return facts;
        }
    }

    public static class PlacementDecision
    {
        public static MirrorDecision Begin(OriginFacts facts)
        {
            if (!facts.Permission)
                return MirrorDecision.DenyPermission;
            if (!facts.Player)
                return MirrorDecision.DenyPlayer;
            if (!facts.Enabled)
                return MirrorDecision.DenyDisabled;
            if (!facts.HasCenter)
                return MirrorDecision.DenyCenter;
            if (!facts.RateOk)
                return MirrorDecision.DenyRate;
            if (!facts.InRange)
                return MirrorDecision.DenyRange;
            if (facts.Boat)
                return MirrorDecision.DenyBoat;
            if (facts.Parented)
                return MirrorDecision.DenyParent;
            if (!facts.DefaultGrade)
                return MirrorDecision.DenyGrade;
            if (!facts.Eligible)
                return MirrorDecision.DenyUnsupported;
            if (!facts.SocketsPresent)
                return MirrorDecision.DenyUnrepresentable;
            return MirrorDecision.Place;
        }

        public static MirrorDecision Copy(CopyFacts facts)
        {
            if (!facts.PermissionOk)
                return MirrorDecision.DenyPermission;
            if (!facts.PlayerOk)
                return MirrorDecision.DenyPlayer;
            if (!facts.Representable)
                return MirrorDecision.DenyUnrepresentable;
            if (facts.Duplicate)
                return MirrorDecision.SkipDuplicate;
            if (facts.HookBlocked)
                return MirrorDecision.DenyHook;
            if (facts.PrivilegeBlocked)
                return MirrorDecision.DenyPrivilege;
            if (facts.VolumeBlocked)
                return MirrorDecision.DenyCollision;
            if (facts.ProximityBlocked)
                return MirrorDecision.DenyProximity;
            if (facts.RoadBlocked)
                return MirrorDecision.DenyRoad;
            if (!facts.SocketModsOk || !facts.Supported)
                return MirrorDecision.DenySupport;
            if (!facts.CanAfford)
                return MirrorDecision.DenyMaterials;
            return MirrorDecision.Place;
        }
    }

    public static class SpawnOutcome
    {
        public static bool Keep(bool spawned, bool destroyed, bool stable)
        {
            return spawned && !destroyed && stable;
        }
    }

    public static class PlacementLifecycle
    {
        public static string AwakePlan(bool activeSelf, bool hasPoolable)
        {
            if (!activeSelf)
                return "activate";
            if (hasPoolable)
                return "enable-poolable";
            return "skip-poolable";
        }

        public static bool GradeCanBeApplied(bool hasDefinition, bool hasGrades, bool resolvedGradeBase)
        {
            return hasDefinition && hasGrades && resolvedGradeBase;
        }

        public static string BoundDiagnostic(string stage, string prefab, string label, string exceptionText, int maxLines, int maxChars)
        {
            if (maxLines < 1)
                maxLines = 1;
            if (maxChars < 80)
                maxChars = 80;
            string safeStage = string.IsNullOrEmpty(stage) ? "unknown" : stage;
            string safePrefab = string.IsNullOrEmpty(prefab) ? "unknown" : prefab;
            string safeLabel = string.IsNullOrEmpty(label) ? "unknown" : label;
            string body = exceptionText ?? "";
            string[] lines = body.Replace("\r", "").Split('\n');
            int count = lines.Length;
            if (count > maxLines)
                count = maxLines;
            string stack = "";
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                    stack += "\n";
                stack += lines[i];
            }
            string text = "Mirror copy failed at stage=" + safeStage + " prefab=" + safePrefab + " side=" + safeLabel + "\n" + stack;
            if (text.Length > maxChars)
                text = text.Substring(0, maxChars);
            return text;
        }
    }

    public sealed class SpawnRescue
    {
        public string Stage = "none";
        public bool EntityCreated;
        public bool ItemsConsumed;
        public bool Refunded;
        public int Receipt;
        public MaterialLedger Ledger;

        public bool RefundOnce()
        {
            if (!ItemsConsumed || Refunded || Ledger == null)
                return false;
            if (!Ledger.TryRefund(Receipt))
                return false;
            Refunded = true;
            return true;
        }
    }

    public sealed class RecursionGuard
    {
        private int depth;
        private readonly HashSet<ulong> mirrors = new HashSet<ulong>();
        private readonly HashSet<ulong> originals = new HashSet<ulong>();

        public bool IsBusy
        {
            get { return depth > 0; }
        }

        public void Enter()
        {
            depth++;
        }

        public void Exit()
        {
            if (depth > 0)
                depth--;
        }

        public bool TryHandleOriginal(ulong id)
        {
            if (id == 0)
                return false;
            return originals.Add(id);
        }

        public void MarkMirror(ulong id)
        {
            if (id != 0)
                mirrors.Add(id);
        }

        public bool IsMirror(ulong id)
        {
            return id != 0 && mirrors.Contains(id);
        }
    }
}

#if !NIGHTSHADE_SYMMETRY_CORE_ONLY
namespace Oxide.Plugins
{
    [Info("NightshadeSymmetry", "Nightshade", "1.0.1")]
    [Description("Permission-based four-way mirror placement across two vertical planes.")]
    public class NightshadeSymmetry : RustPlugin
    {
        private const string UsePermission = Nightshade.Symmetry.PermissionGate.Use;
        private const string DataPath = "Nightshade/NightshadeSymmetry/Centers";
        private const string UiRoot = "NightshadeSymmetry.Status";

        private PluginConfig config;
        private Store store;
        private bool dataWritable = true;
        private int spawnDepth;
        private readonly Dictionary<ulong, bool> enabled = new Dictionary<ulong, bool>();
        private readonly Dictionary<ulong, LiveCenter> centers = new Dictionary<ulong, LiveCenter>();
        private readonly Dictionary<ulong, RateWindow> rates = new Dictionary<ulong, RateWindow>();
        private readonly Dictionary<ulong, float> chatThrottle = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, Timer> toasts = new Dictionary<ulong, Timer>();
        private readonly HashSet<ulong> mirroredIds = new HashSet<ulong>();
        private readonly HashSet<ulong> handledIds = new HashSet<ulong>();
        private readonly Nightshade.Symmetry.RecursionGuard guard = new Nightshade.Symmetry.RecursionGuard();

        private class PluginConfig
        {
            [JsonProperty("Command")] public string Command = "symmetry";
            [JsonProperty("Permission")] public string Permission = UsePermission;
            [JsonProperty("Chat prefix")] public string Prefix = "<color=#A267D3>[Symmetry]</color>";
            [JsonProperty("Maximum distance from center")] public float MaxDistance = 40f;
            [JsonProperty("Maximum placements per second")] public int MaxPlacementsPerSecond = 6;
            [JsonProperty("Center aim distance")] public float CenterRayDistance = 10f;
            [JsonProperty("Support search radius")] public float SearchRadius = 3.5f;
            [JsonProperty("Support search limit")] public int SearchLimit = 32;
            [JsonProperty("Socket position tolerance")] public float SocketEpsilon = 0.02f;
            [JsonProperty("Socket angle tolerance")] public float SocketAngle = 2f;
            [JsonProperty("Duplicate position tolerance")] public float DedupPosition = 0.05f;
            [JsonProperty("Duplicate angle tolerance")] public float DedupAngle = 2f;
            [JsonProperty("Axis preview seconds")] public float PreviewSeconds = 15f;
            [JsonProperty("Axis preview length")] public float PreviewLength = 6f;
            [JsonProperty("Show toast")] public bool ShowToast = true;
            [JsonProperty("Toast on partial mirror")] public bool ToastPartial = true;
            [JsonProperty("Chat on partial mirror")] public bool ChatPartial = true;
            [JsonProperty("Chat on full mirror")] public bool ChatFull = false;
            [JsonProperty("Persist centers across reload")] public bool PersistCenters = true;
            [JsonProperty("Allow unlisted building blocks")] public bool AllowUnlisted = false;
            [JsonProperty("Debug failures")] public bool Debug = false;
            [JsonProperty("Panel color")] public string PanelColor = "#0E0E12";
            [JsonProperty("Accent color")] public string AccentColor = "#A267D3";
            [JsonProperty("Text color")] public string TextColor = "#E7E2F2";
            [JsonProperty("Muted color")] public string MutedColor = "#8A8A8A";
            [JsonProperty("Logo URL")] public string LogoUrl = "https://raw.githubusercontent.com/nightshadepve/Nightshadeserver/refs/heads/main/260625_Logo_75_nobg.png";
            [JsonProperty("Eligible short prefabs")] public List<string> Eligible = new List<string>(Nightshade.Symmetry.PieceCatalog.Eligible);
            [JsonProperty("Excluded short prefabs")] public List<string> Excluded = new List<string>(Nightshade.Symmetry.PieceCatalog.Excluded);
            [JsonProperty("Messages")] public Dictionary<string, string> Messages = DefaultMessages();
            [JsonProperty("Documentation")] public List<string> Documentation = DefaultDocs();
        }

        private class Store
        {
            public string WorldKey = "";
            public Dictionary<string, SavedCenter> Centers = new Dictionary<string, SavedCenter>();
        }

        private class SavedCenter
        {
            public float X;
            public float Y;
            public float Z;
            public float RightX;
            public float RightY;
            public float RightZ;
            public float ForwardX;
            public float ForwardY;
            public float ForwardZ;
            public string Source = "";
        }

        private struct LiveCenter
        {
            public Nightshade.Symmetry.Axes Axes;
            public string Source;
        }

        private struct RateWindow
        {
            public float Start;
            public int Count;
        }

        private struct TakenLine
        {
            public int ItemId;
            public int Amount;
            public List<Item> Items;
        }

        private static Dictionary<string, string> DefaultMessages()
        {
            return new Dictionary<string, string>
            {
                ["NoPermission"] = "Symmetry requires permission nightshadesymmetry.use.",
                ["Help"] = "Commands: /{cmd} status, center, center here, center here snap, on, off, reset, show. Center look uses the building block you aim at. Center here uses your feet and horizontal look. Snap locks that yaw to 90 degrees. Symmetry mirrors across the two vertical planes and both together. It does not spin four rotated copies.",
                ["StatusOn"] = "Symmetry is on. Center {source} at {pos}, yaw {yaw}.",
                ["StatusOff"] = "Symmetry is off. Center {source} at {pos}, yaw {yaw}.",
                ["StatusNoCenter"] = "Symmetry is off. No center set. Aim at a block and use /{cmd} center, or /{cmd} center here.",
                ["CenterLook"] = "Center set on the {prefab} pivot at {pos}, yaw {yaw}. Planes are vertical through that yaw.",
                ["CenterHere"] = "Center set at your feet {pos}, yaw {yaw}.",
                ["CenterFailed"] = "Look at a building block within {dist}m, or use /{cmd} center here.",
                ["CenterLevel"] = "Look horizontally enough to define a forward axis.",
                ["Enabled"] = "Symmetry on. The next building-plan pieces mirror to the other sides when those spots pass normal build checks.",
                ["NeedCenter"] = "Set a center before turning symmetry on.",
                ["Disabled"] = "Symmetry off. The center is unchanged.",
                ["Reset"] = "Symmetry center cleared and symmetry turned off.",
                ["Show"] = "Showing the center and both axes for {seconds}s. Side axis is accent, depth axis is muted.",
                ["NotBuildingBlock"] = "Symmetry only mirrors building-plan blocks.",
                ["Mirrored"] = "Mirrored {placed} of {total}. {details}",
                ["OnAxis"] = "No extra copy. That piece already lies on the mirror.",
                ["Unsupported"] = "{prefab} is not in the symmetry allow list.",
                ["Excluded"] = "{prefab} is excluded. Its shape cannot be mirrored safely.",
                ["Boat"] = "Boat building pieces are not mirrored.",
                ["Parent"] = "Parented pieces are not mirrored.",
                ["Grade"] = "Only the default placement grade is mirrored.",
                ["Range"] = "That piece is outside the symmetry range ({dist}m).",
                ["Rate"] = "Symmetry is pausing briefly so placement stays within the rate limit.",
                ["NoSockets"] = "This piece has no sockets symmetry can validate.",
                ["Revoked"] = "Symmetry permission was removed. Mirroring stopped.",
                ["left-right"] = "left-right",
                ["front-back"] = "front-back",
                ["opposite"] = "opposite",
                ["Place"] = "placed",
                ["SkipDuplicate"] = "already there",
                ["DenyPermission"] = "permission removed",
                ["DenyPlayer"] = "player unavailable",
                ["DenyUnrepresentable"] = "shape cannot mirror",
                ["DenyHook"] = "blocked by a build hook",
                ["DenyPrivilege"] = "no build privilege",
                ["DenyCollision"] = "blocked",
                ["DenyProximity"] = "too close to another building",
                ["DenyRoad"] = "too close to a road",
                ["DenySupport"] = "no valid support",
                ["DenyStability"] = "not stable",
                ["DenyMaterials"] = "not enough materials"
            };
        }

        private static List<string> DefaultDocs()
        {
            return new List<string>
            {
                "Permission: nightshadesymmetry.use. Nothing is granted automatically and admin status is not a bypass.",
                "Example grant, documentation only: c.grant group <group> nightshadesymmetry.use",
                "Reload: c.reload NightshadeSymmetry",
                "Center look: /symmetry center aims at a building block. The center is that block pivot, not the bounds center and not your feet. The two mirror planes are vertical and use the block yaw (forward flattened to the horizontal).",
                "Center here: /symmetry center here uses your feet and your horizontal look direction. Add snap to round that yaw to the nearest 90 degrees.",
                "Each placement can add at most three copies: left-right, front-back, and the opposite corner. Pieces on an axis are not duplicated. This is reflection, not four 90-degree copies, and it does not use negative scale.",
                "The original piece is placed and charged by vanilla. Each mirrored copy is charged the full default-grade construction cost only after it spawns and stays stable. Creative free-build and tutorial follow Planner.PayForPlacement and do not take items there either.",
                "Eligible pieces are attempted only when their live sockets match a proper rotation of the reflected shape. Chiral stairs are excluded. Unknown prefabs are rejected.",
                "Centers can persist across reload and are cleared when the wipe id, seed, or world size changes. Symmetry is always off after reload, reconnect, death, or permission loss.",
                "There is no mirrored upgrade, rotate, or demolish in this version.",
                "Hard caps: distance 80m, nearby search 6m, 20 placements per second."
            };
        }

        protected override void LoadDefaultConfig()
        {
            config = new PluginConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<PluginConfig>() ?? new PluginConfig();
            }
            catch (Exception ex)
            {
                PrintError("NightshadeSymmetry config could not be read. Defaults were kept in memory: " + ex.Message);
                config = new PluginConfig();
            }
            if (config.Messages == null)
                config.Messages = DefaultMessages();
            if (config.Documentation == null)
                config.Documentation = DefaultDocs();
            if (config.Eligible == null)
                config.Eligible = new List<string>(Nightshade.Symmetry.PieceCatalog.Eligible);
            if (config.Excluded == null)
                config.Excluded = new List<string>(Nightshade.Symmetry.PieceCatalog.Excluded);
            if (string.IsNullOrEmpty(config.Command))
                config.Command = "symmetry";
            config.Permission = UsePermission;
            config.MaxDistance = Nightshade.Symmetry.MirrorMath.ClampDistance(config.MaxDistance);
            config.SearchRadius = Nightshade.Symmetry.MirrorMath.ClampRadius(config.SearchRadius);
            config.MaxPlacementsPerSecond = Nightshade.Symmetry.MirrorMath.ClampRate(config.MaxPlacementsPerSecond);
            if (config.SearchLimit < 4)
                config.SearchLimit = 4;
            if (config.SearchLimit > 48)
                config.SearchLimit = 48;
            if (config.SocketEpsilon < 0.005f)
                config.SocketEpsilon = 0.005f;
            if (config.SocketEpsilon > 0.05f)
                config.SocketEpsilon = 0.05f;
            if (config.SocketAngle < 0.5f)
                config.SocketAngle = 0.5f;
            if (config.SocketAngle > 5f)
                config.SocketAngle = 5f;
            if (config.DedupPosition < 0.02f)
                config.DedupPosition = 0.02f;
            if (config.DedupPosition > 0.2f)
                config.DedupPosition = 0.2f;
            if (config.DedupAngle < 1f)
                config.DedupAngle = 1f;
            if (config.DedupAngle > 8f)
                config.DedupAngle = 8f;
            if (config.CenterRayDistance < 2f)
                config.CenterRayDistance = 2f;
            if (config.CenterRayDistance > 20f)
                config.CenterRayDistance = 20f;
            if (config.PreviewSeconds < 1f)
                config.PreviewSeconds = 1f;
            if (config.PreviewSeconds > 30f)
                config.PreviewSeconds = 30f;
            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(config, true);
        }

        private void Init()
        {
            permission.RegisterPermission(UsePermission, this);
            cmd.AddChatCommand(config.Command.Trim().TrimStart('/').ToLowerInvariant(), this, nameof(CmdSymmetry));
            LoadStore();
        }

        private void OnServerInitialized()
        {
            ApplyWorldIdentity(false);
            enabled.Clear();
        }

        private void OnServerSave()
        {
            SaveStore();
        }

        private void Unload()
        {
            SaveStore();
            foreach (BasePlayer player in BasePlayer.activePlayerList)
                CuiHelper.DestroyUi(player, UiRoot);
            foreach (KeyValuePair<ulong, Timer> pair in toasts)
                pair.Value?.Destroy();
            toasts.Clear();
            enabled.Clear();
        }

        private void OnNewSave(string filename)
        {
            centers.Clear();
            enabled.Clear();
            if (store == null)
                store = new Store();
            store.Centers.Clear();
            store.WorldKey = CurrentWorldKey();
            SaveStore();
            Puts("NightshadeSymmetry cleared centers for a new save.");
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null)
                return;
            if (Nightshade.Symmetry.SessionEnd.ShouldDisable(true, false, false))
                DisablePlayer(player.userID, false);
        }

        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null)
                return;
            if (Nightshade.Symmetry.SessionEnd.ShouldDisable(false, true, false))
                DisablePlayer(player.userID, false);
        }

        private void OnEntityBuilt(Planner planner, GameObject go)
        {
            if (spawnDepth > 0 || guard.IsBusy)
                return;
            if (planner == null || go == null)
                return;
            BasePlayer player = planner.GetOwnerPlayer();
            if (player == null || !player.IsConnected)
                return;
            BaseEntity entity = go.ToBaseEntity();
            BuildingBlock block = entity as BuildingBlock;
            if (block == null || block.IsDestroyed)
                return;
            ulong id = NetId(block);
            if (id == 0 || guard.IsMirror(id) || mirroredIds.Contains(id))
                return;
            if (!guard.TryHandleOriginal(id) || !handledIds.Add(id))
                return;
            try
            {
                MirrorBlock(player, planner, block);
            }
            catch (Exception ex)
            {
                string prefab = block != null ? block.ShortPrefabName : "unknown";
                PrintError(Nightshade.Symmetry.PlacementLifecycle.BoundDiagnostic("mirror", prefab, "original", ex.ToString(), 12, 900));
            }
        }

        private void CmdSymmetry(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;
            if (!HasUse(player))
            {
                DisablePlayer(player.userID, false);
                Reply(player, "NoPermission");
                return;
            }
            string sub = args == null || args.Length == 0 ? "status" : args[0].ToLowerInvariant();
            if (sub == "help")
            {
                Reply(player, "Help", "cmd", config.Command);
                return;
            }
            if (sub == "status")
            {
                ReplyStatus(player);
                return;
            }
            if (sub == "on" || sub == "enable")
            {
                Enable(player);
                return;
            }
            if (sub == "off" || sub == "disable")
            {
                DisablePlayer(player.userID, true);
                Reply(player, "Disabled");
                Toast(player, Msg("Disabled"));
                return;
            }
            if (sub == "reset")
            {
                centers.Remove(player.userID);
                RemoveStored(player.userID);
                DisablePlayer(player.userID, false);
                SaveStore();
                Reply(player, "Reset");
                return;
            }
            if (sub == "show")
            {
                Show(player);
                return;
            }
            if (sub == "center")
            {
                bool here = args != null && args.Length > 1 && args[1].Equals("here", StringComparison.OrdinalIgnoreCase);
                bool look = args != null && args.Length > 1 && args[1].Equals("look", StringComparison.OrdinalIgnoreCase);
                bool snap = false;
                if (args != null)
                {
                    for (int i = 1; i < args.Length; i++)
                    {
                        if (args[i].Equals("snap", StringComparison.OrdinalIgnoreCase))
                            snap = true;
                    }
                }
                if (here)
                    SetCenterHere(player, snap);
                else
                    SetCenterLook(player, snap);
                return;
            }
            Reply(player, "Help", "cmd", config.Command);
        }

        private void Enable(BasePlayer player)
        {
            bool hasCenter = centers.ContainsKey(player.userID);
            Nightshade.Symmetry.SessionDecision decision = Nightshade.Symmetry.PermissionGate.Evaluate(true, Nightshade.Symmetry.SessionAction.Enable, false, hasCenter);
            if (!decision.Allowed)
            {
                Reply(player, "NeedCenter");
                return;
            }
            enabled[player.userID] = true;
            Reply(player, "Enabled");
            Toast(player, Msg("Enabled"));
        }

        private void SetCenterLook(BasePlayer player, bool snap)
        {
            if (player.eyes == null)
            {
                Reply(player, "CenterFailed", "dist", config.CenterRayDistance.ToString("0", CultureInfo.InvariantCulture), "cmd", config.Command);
                return;
            }
            RaycastHit hit;
            if (!Physics.Raycast(player.eyes.HeadRay(), out hit, config.CenterRayDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                Reply(player, "CenterFailed", "dist", config.CenterRayDistance.ToString("0", CultureInfo.InvariantCulture), "cmd", config.Command);
                return;
            }
            BuildingBlock block = hit.GetEntity() as BuildingBlock;
            if (block == null)
            {
                Reply(player, "CenterFailed", "dist", config.CenterRayDistance.ToString("0", CultureInfo.InvariantCulture), "cmd", config.Command);
                return;
            }
            Vector3 forward = block.transform.forward;
            forward.y = 0f;
            Nightshade.Symmetry.Axes axes;
            if (!Nightshade.Symmetry.Axes.TryCreate(V(block.transform.position), V(forward), snap, out axes))
            {
                Reply(player, "CenterLevel");
                return;
            }
            StoreCenter(player.userID, axes, "block " + block.ShortPrefabName);
            Reply(player, "CenterLook", "prefab", block.ShortPrefabName, "pos", Format(axes.Center), "yaw", axes.Forward.Length > 0f ? Nightshade.Symmetry.Axes.YawDegrees(axes.Forward).ToString("0", CultureInfo.InvariantCulture) : "0");
        }

        private void SetCenterHere(BasePlayer player, bool snap)
        {
            if (player.eyes == null)
            {
                Reply(player, "CenterLevel");
                return;
            }
            Vector3 forward = player.eyes.HeadForward();
            forward.y = 0f;
            Nightshade.Symmetry.Axes axes;
            if (!Nightshade.Symmetry.Axes.TryCreate(V(player.transform.position), V(forward), snap, out axes))
            {
                Reply(player, "CenterLevel");
                return;
            }
            StoreCenter(player.userID, axes, snap ? "player snap" : "player");
            Reply(player, "CenterHere", "pos", Format(axes.Center), "yaw", Nightshade.Symmetry.Axes.YawDegrees(axes.Forward).ToString("0", CultureInfo.InvariantCulture));
        }

        private void Show(BasePlayer player)
        {
            LiveCenter center;
            if (!centers.TryGetValue(player.userID, out center))
            {
                Reply(player, "StatusNoCenter", "cmd", config.Command);
                return;
            }
            Vector3 origin = U(center.Axes.Center);
            Vector3 side = U(center.Axes.Right) * config.PreviewLength;
            Vector3 depth = U(center.Axes.Forward) * config.PreviewLength;
            Color accent = Rgb(config.AccentColor, Color.magenta);
            Color muted = Rgb(config.MutedColor, Color.white);
            player.SendConsoleCommand("ddraw.line", config.PreviewSeconds, accent, origin - side, origin + side);
            player.SendConsoleCommand("ddraw.line", config.PreviewSeconds, muted, origin - depth, origin + depth);
            player.SendConsoleCommand("ddraw.line", config.PreviewSeconds, accent, origin, origin + Vector3.up * (config.PreviewLength * 0.5f));
            Reply(player, "Show", "seconds", config.PreviewSeconds.ToString("0", CultureInfo.InvariantCulture));
        }

        private void MirrorBlock(BasePlayer player, Planner planner, BuildingBlock block)
        {
            bool permitted = HasUse(player);
            if (Nightshade.Symmetry.SessionEnd.ShouldDisable(false, player.IsDead(), !permitted))
            {
                if (!permitted || player.IsDead())
                    DisablePlayer(player.userID, false);
                if (!permitted)
                    Throttled(player, "Revoked");
                return;
            }
            LiveCenter center;
            bool hasCenter = centers.TryGetValue(player.userID, out center);
            bool isEnabled = false;
            enabled.TryGetValue(player.userID, out isEnabled);
            Nightshade.Symmetry.SessionDecision session = Nightshade.Symmetry.PermissionGate.Evaluate(permitted, Nightshade.Symmetry.SessionAction.Mirror, isEnabled, hasCenter);
            if (!session.Allowed)
                return;

            Construction definition = block.blockDefinition;
            if (definition == null)
                definition = PrefabAttribute.server.Find<Construction>(block.prefabID);
            Nightshade.Symmetry.OriginFacts origin = new Nightshade.Symmetry.OriginFacts();
            origin.Permission = permitted;
            origin.Player = player.IsConnected && !player.IsDead();
            origin.Enabled = isEnabled;
            origin.HasCenter = hasCenter;
            origin.RateOk = ConsumeRate(player.userID);
            origin.InRange = hasCenter && Nightshade.Symmetry.MirrorMath.WithinRange(V(block.transform.position), center.Axes.Center, config.MaxDistance);
            origin.Boat = IsBoat(definition);
            origin.Parented = block.GetParentEntity() != null;
            origin.DefaultGrade = IsDefaultGrade(block, definition);
            origin.Eligible = Nightshade.Symmetry.PieceCatalog.IsEligible(block.ShortPrefabName, config.AllowUnlisted, config.Eligible, config.Excluded);
            List<Nightshade.Symmetry.SocketSample> sockets = CollectSockets(definition);
            origin.SocketsPresent = sockets.Count > 0;
            Nightshade.Symmetry.MirrorDecision begin = Nightshade.Symmetry.PlacementDecision.Begin(origin);
            if (begin != Nightshade.Symmetry.MirrorDecision.Place)
            {
                if (begin == Nightshade.Symmetry.MirrorDecision.DenyRate)
                    Throttled(player, "Rate");
                else if (begin == Nightshade.Symmetry.MirrorDecision.DenyRange)
                    Throttled(player, "Range", "dist", config.MaxDistance.ToString("0", CultureInfo.InvariantCulture));
                else if (begin == Nightshade.Symmetry.MirrorDecision.DenyUnsupported || begin == Nightshade.Symmetry.MirrorDecision.DenyBoat)
                    Throttled(player, begin == Nightshade.Symmetry.MirrorDecision.DenyBoat ? "Boat" : (IsExcluded(block.ShortPrefabName) ? "Excluded" : "Unsupported"), "prefab", block.ShortPrefabName);
                else if (begin == Nightshade.Symmetry.MirrorDecision.DenyParent)
                    Throttled(player, "Parent");
                else if (begin == Nightshade.Symmetry.MirrorDecision.DenyGrade)
                    Throttled(player, "Grade");
                else if (begin == Nightshade.Symmetry.MirrorDecision.DenyUnrepresentable)
                    Throttled(player, "NoSockets");
                return;
            }

            Nightshade.Symmetry.Pose pose = new Nightshade.Symmetry.Pose(V(block.transform.position), Q(block.transform.rotation));
            List<Nightshade.Symmetry.PlannedCopy> copies = Nightshade.Symmetry.MirrorMath.Plan(
                pose,
                sockets,
                center.Axes,
                config.SocketEpsilon,
                config.SocketAngle,
                config.DedupPosition,
                config.DedupAngle);
            if (copies.Count == 0)
            {
                if (config.ChatFull)
                    Throttled(player, "OnAxis");
                return;
            }

            int placed = 0;
            List<string> details = new List<string>();
            bool anyFailure = false;
            for (int i = 0; i < copies.Count; i++)
            {
                if (!player.IsConnected || player.IsDead())
                    break;
                bool stillAllowed = HasUse(player);
                if (!stillAllowed)
                {
                    DisablePlayer(player.userID, false);
                    details.Add(Label(copies[i].Label) + ": " + Msg("Revoked"));
                    anyFailure = true;
                    break;
                }
                string detail;
                bool didPlace = TryPlaceCopy(player, planner, block, definition, center.Axes, copies[i], out detail);
                if (didPlace)
                    placed++;
                else if (copies[i].Representable || detail.Length > 0)
                    anyFailure = true;
                if (detail.Length > 0)
                    details.Add(Label(copies[i].Label) + ": " + detail);
            }
            if (placed == 0 && !anyFailure)
                return;
            bool partial = placed != copies.Count;
            if (partial && !config.ChatPartial && !config.ToastPartial)
                return;
            if (!partial && !config.ChatFull)
                return;
            string summary = Msg("Mirrored", "placed", placed.ToString(CultureInfo.InvariantCulture), "total", copies.Count.ToString(CultureInfo.InvariantCulture), "details", string.Join(", ", details.ToArray()));
            if ((partial && config.ChatPartial) || (!partial && config.ChatFull))
                ReplyRaw(player, summary);
            if (config.ShowToast && ((partial && config.ToastPartial) || (!partial && config.ChatFull)))
                Toast(player, summary);
        }

        private bool TryPlaceCopy(BasePlayer player, Planner planner, BuildingBlock original, Construction definition, Nightshade.Symmetry.Axes axes, Nightshade.Symmetry.PlannedCopy copy, out string detail)
        {
            detail = "";
            Nightshade.Symmetry.CopyFacts facts = Nightshade.Symmetry.CopyFacts.Passing();
            facts.PermissionOk = HasUse(player);
            facts.PlayerOk = player.IsConnected && !player.IsDead();
            facts.Representable = copy.Representable;
            if (!copy.Representable)
            {
                detail = Msg("DenyUnrepresentable");
                return false;
            }
            Vector3 position = U(copy.Pose.Position);
            Quaternion rotation = U(copy.Pose.Rotation);
            facts.Duplicate = IsDuplicate(original, position, rotation);
            DecayEntity support;
            Socket_Base female;
            Socket_Base male;
            bool terrain;
            facts.Supported = FindSupport(original, definition, axes, copy.Label, position, rotation, out support, out female, out male, out terrain);
            facts.SocketModsOk = facts.Supported && SocketModsAccept(player, definition, position, rotation, support, female, male, terrain);
            facts.PrivilegeBlocked = PrivilegeBlocked(player, definition, position, rotation);
            facts.VolumeBlocked = VolumeBlocked(original.prefabID, position, rotation);
            facts.ProximityBlocked = BuildingProximity.Check(player, definition, position, rotation);
            facts.RoadBlocked = RoadBlocked(player, definition, position, rotation, support, female);
            facts.HookBlocked = HookBlocked(player, planner, definition, position, rotation, support, female);
            List<Nightshade.Symmetry.CostLine> cost = BuildCost(definition);
            bool charge = ShouldCharge(player);
            Nightshade.Symmetry.MaterialLedger ledger = null;
            int receipt = 0;
            if (charge)
            {
                ledger = SeedLedger(player, cost);
                facts.CanAfford = ledger.TryCharge(cost, out receipt);
            }
            Nightshade.Symmetry.MirrorDecision decision = Nightshade.Symmetry.PlacementDecision.Copy(facts);
            if (decision != Nightshade.Symmetry.MirrorDecision.Place)
            {
                if (ledger != null && receipt != 0)
                    ledger.TryRefund(receipt);
                if (decision == Nightshade.Symmetry.MirrorDecision.SkipDuplicate)
                {
                    detail = "";
                    return false;
                }
                detail = Msg(decision.ToString());
                if (config.Debug)
                    Puts("Skip " + copy.Label + " " + decision + " at " + position);
                return false;
            }

            List<TakenLine> taken = null;
            Nightshade.Symmetry.SpawnRescue rescue = new Nightshade.Symmetry.SpawnRescue();
            rescue.Stage = "take";
            rescue.Ledger = ledger;
            rescue.Receipt = receipt;
            BuildingBlock spawned = null;
            spawnDepth++;
            guard.Enter();
            try
            {
                if (charge)
                {
                    string takeError;
                    if (!TakeCost(player, cost, out taken, out takeError))
                    {
                        if (ledger != null)
                            ledger.TryRefund(receipt);
                        if (config.Debug && takeError != null && takeError.Length > 0)
                            Puts("Mirror take failed: " + takeError);
                        detail = Msg("DenyMaterials");
                        return false;
                    }
                    rescue.ItemsConsumed = true;
                }

                rescue.Stage = "create";
                spawned = CreateDormantBlock(original, position, rotation);
                rescue.EntityCreated = spawned != null;
                if (spawned == null)
                {
                    AbandonCopy(player, rescue, null, taken);
                    detail = Msg("DenySupport");
                    return false;
                }

                rescue.Stage = "awake";
                AwakeDormantBlock(spawned.gameObject);

                rescue.Stage = "grade";
                ulong ownerId;
                if (!Nightshade.Symmetry.CopyOwnership.TryOwner(player.userID, out ownerId) || !ApplyGradeAndSpawn(player, original, spawned, ownerId))
                {
                    AbandonCopy(player, rescue, spawned, taken);
                    detail = Msg("DenyGrade");
                    return false;
                }

                rescue.Stage = "attach";
                if (support is BuildingBlock)
                    spawned.AttachToBuilding(support);
                else
                    spawned.AttachToBuilding(null);

                rescue.Stage = "identity";
                ulong mid = NetId(spawned);
                if (mid == 0 || spawned.IsDestroyed)
                {
                    AbandonCopy(player, rescue, spawned, taken);
                    detail = Msg("DenySupport");
                    return false;
                }
                mirroredIds.Add(mid);
                guard.MarkMirror(mid);

                rescue.Stage = "stability";
                spawned.GetEntityLinks(true);
                spawned.UpdateSurroundingEntities();
                spawned.StabilityCheck();
                bool stable = spawned != null && !spawned.IsDestroyed && (spawned.grounded || spawned.cachedStability + 0.0001f >= ConVar.Stability.collapse);
                if (!Nightshade.Symmetry.SpawnOutcome.Keep(spawned != null, spawned == null || spawned.IsDestroyed, stable))
                {
                    AbandonCopy(player, rescue, spawned, taken);
                    detail = Msg("DenyStability");
                    return false;
                }

                rescue.Stage = "hook";
                if (spawned.gameObject != null)
                {
                    try
                    {
                        Interface.CallHook("OnEntityBuilt", planner, spawned.gameObject);
                    }
                    catch (Exception hookEx)
                    {
                        PrintWarning(Nightshade.Symmetry.PlacementLifecycle.BoundDiagnostic("hook", original.ShortPrefabName, copy.Label, hookEx.ToString(), 8, 700));
                    }
                }
                if (spawned.IsDestroyed)
                {
                    AbandonCopy(player, rescue, null, taken);
                    detail = Msg("DenyHook");
                    return false;
                }

                rescue.Stage = "note";
                if (charge)
                    NoteCost(player, cost);
                rescue.EntityCreated = false;
            }
            catch (Exception ex)
            {
                string prefab = original != null ? original.ShortPrefabName : "unknown";
                PrintError(Nightshade.Symmetry.PlacementLifecycle.BoundDiagnostic(rescue.Stage, prefab, copy.Label, ex.ToString(), 12, 900));
                AbandonCopy(player, rescue, spawned, taken);
                detail = Msg("DenySupport");
                return false;
            }
            finally
            {
                guard.Exit();
                spawnDepth--;
            }
            detail = Msg("Place");
            return true;
        }

        private BuildingBlock CreateDormantBlock(BuildingBlock original, Vector3 position, Quaternion rotation)
        {
            if (original == null || GameManager.server == null || string.IsNullOrEmpty(original.PrefabName))
                return null;
            GameObject created = GameManager.server.CreatePrefab(original.PrefabName, position, rotation, false);
            if (created == null)
                return null;
            BuildingBlock block = created.ToBaseEntity() as BuildingBlock;
            if (block == null)
            {
                UnityEngine.Object.Destroy(created);
                return null;
            }
            return block;
        }

        private static void AwakeDormantBlock(GameObject gameObject)
        {
            if (gameObject == null)
                return;
            Poolable poolable = gameObject.GetComponent<Poolable>();
            string plan = Nightshade.Symmetry.PlacementLifecycle.AwakePlan(gameObject.activeSelf, poolable != null);
            if (plan == "activate")
                gameObject.SetActive(true);
            else if (plan == "enable-poolable" && poolable != null)
                poolable.SetBehaviourEnabled(true);
        }

        private bool ApplyGradeAndSpawn(BasePlayer player, BuildingBlock original, BuildingBlock block, ulong ownerId)
        {
            if (block == null || original == null || PrefabAttribute.server == null)
                return false;
            if (block.blockDefinition == null)
                block.blockDefinition = PrefabAttribute.server.Find<Construction>(block.prefabID);
            Construction definition = block.blockDefinition;
            if (!GradesAreSafe(definition))
                return false;
            block.skinID = original.skinID;
            ConstructionGrade resolved = definition.GetGrade(original.grade, block.skinID);
            if (resolved == null || resolved.gradeBase == null)
                resolved = definition.defaultGrade;
            if (!Nightshade.Symmetry.PlacementLifecycle.GradeCanBeApplied(true, true, resolved != null && resolved.gradeBase != null))
                return false;
            block.SetGrade(resolved.gradeBase.type);
            if (block.currentGrade == null || block.currentGrade.gradeBase == null)
                return false;
            block.ResetLifeStateOnSpawn = false;
            float max = block.currentGrade.maxHealth;
            block.InitializeHealth(max, block.StartMaxHealth());
            block.OwnerID = ownerId;
            block.OnPlaced(player);
            block.Spawn();
            return !block.IsDestroyed;
        }

        private static bool GradesAreSafe(Construction definition)
        {
            if (definition == null || definition.grades == null || definition.defaultGrade == null || definition.defaultGrade.gradeBase == null)
                return false;
            for (int i = 0; i < definition.grades.Length; i++)
            {
                ConstructionGrade grade = definition.grades[i];
                if (grade == null || grade.gradeBase == null)
                    return false;
            }
            return true;
        }

        private void AbandonCopy(BasePlayer player, Nightshade.Symmetry.SpawnRescue rescue, BuildingBlock spawned, List<TakenLine> taken)
        {
            if (spawned != null && !spawned.IsDestroyed)
            {
                try
                {
                    spawned.Kill();
                    if (!spawned.IsDestroyed && spawned.gameObject != null)
                        UnityEngine.Object.Destroy(spawned.gameObject);
                }
                catch (Exception ex)
                {
                    string prefab = "unknown";
                    try
                    {
                        prefab = spawned.ShortPrefabName;
                    }
                    catch
                    {
                    }
                    PrintWarning(Nightshade.Symmetry.PlacementLifecycle.BoundDiagnostic("kill", prefab, rescue != null ? rescue.Stage : "kill", ex.ToString(), 6, 500));
                    try
                    {
                        if (spawned.gameObject != null)
                            UnityEngine.Object.Destroy(spawned.gameObject);
                    }
                    catch
                    {
                    }
                }
            }
            if (rescue != null)
                rescue.EntityCreated = false;
            if (rescue == null || !rescue.RefundOnce())
                return;
            try
            {
                GiveTaken(player, taken);
            }
            catch (Exception ex)
            {
                PrintError(Nightshade.Symmetry.PlacementLifecycle.BoundDiagnostic("refund", "unknown", rescue.Stage, ex.ToString(), 8, 700));
            }
        }

        private static void GiveTaken(BasePlayer player, List<TakenLine> taken)
        {
            if (player == null || taken == null)
                return;
            List<Nightshade.Symmetry.CostLine> refund = new List<Nightshade.Symmetry.CostLine>();
            for (int i = 0; i < taken.Count; i++)
            {
                if (taken[i].Amount > 0)
                    refund.Add(new Nightshade.Symmetry.CostLine(taken[i].ItemId, taken[i].Amount));
            }
            GiveCost(player, refund);
        }

        private bool FindSupport(
            BuildingBlock original,
            Construction definition,
            Nightshade.Symmetry.Axes axes,
            string label,
            Vector3 position,
            Quaternion rotation,
            out DecayEntity support,
            out Socket_Base female,
            out Socket_Base male,
            out bool terrain)
        {
            support = null;
            female = null;
            male = null;
            terrain = false;
            List<EntityLink> links = original.GetEntityLinks(true);
            int required = 0;
            int matched = 0;
            if (links != null)
            {
                for (int i = 0; i < links.Count; i++)
                {
                    EntityLink link = links[i];
                    if (link == null || !link.IsMale() || link.socket == null || link.socket.maleDummy || link.IsEmpty() || link.connections == null || link.connections.Count == 0)
                        continue;
                    required++;
                    bool one = false;
                    for (int c = 0; c < link.connections.Count; c++)
                    {
                        EntityLink other = link.connections[c];
                        if (other == null || other.owner == null || other.socket == null)
                            continue;
                        Vector3 socketWorld = original.transform.TransformPoint(link.socket.localPosition);
                        Vector3 reflectedSocket = U(ReflectByLabel(V(socketWorld), axes, label));
                        DecayEntity neighbor;
                        Socket_Base femaleSocket;
                        if (!FindFemale(reflectedSocket, link.socket, position, rotation, out neighbor, out femaleSocket))
                            continue;
                        if (neighbor.IsOccupied(femaleSocket))
                            continue;
                        one = true;
                        support = neighbor;
                        female = femaleSocket;
                        male = link.socket;
                        break;
                    }
                    if (one)
                        matched++;
                }
            }
            if (required > 0)
                return matched == required;
            if (original.grounded && definition != null && HasTerrainSocket(definition))
            {
                terrain = true;
                return true;
            }
            return FindGeometricSupport(original, definition, axes, label, position, rotation, out support, out female, out male);
        }

        private bool FindGeometricSupport(
            BuildingBlock original,
            Construction definition,
            Nightshade.Symmetry.Axes axes,
            string label,
            Vector3 position,
            Quaternion rotation,
            out DecayEntity support,
            out Socket_Base female,
            out Socket_Base male)
        {
            support = null;
            female = null;
            male = null;
            if (definition == null || definition.allSockets == null)
                return false;
            for (int i = 0; i < definition.allSockets.Length; i++)
            {
                Socket_Base socket = definition.allSockets[i];
                if (socket == null || !socket.male || socket.maleDummy)
                    continue;
                Vector3 socketWorld = original.transform.TransformPoint(socket.localPosition);
                Vector3 reflectedSocket = U(ReflectByLabel(V(socketWorld), axes, label));
                DecayEntity neighbor;
                Socket_Base femaleSocket;
                if (!FindFemale(reflectedSocket, socket, position, rotation, out neighbor, out femaleSocket))
                    continue;
                if (neighbor.IsOccupied(femaleSocket))
                    continue;
                support = neighbor;
                female = femaleSocket;
                male = socket;
                return true;
            }
            return false;
        }

        private static Nightshade.Symmetry.Vec3 ReflectByLabel(Nightshade.Symmetry.Vec3 point, Nightshade.Symmetry.Axes axes, string label)
        {
            if (label == "front-back")
                return Nightshade.Symmetry.MirrorMath.ReflectPoint(point, axes.Center, axes.Forward);
            if (label == "opposite")
            {
                Nightshade.Symmetry.Vec3 once = Nightshade.Symmetry.MirrorMath.ReflectPoint(point, axes.Center, axes.Right);
                return Nightshade.Symmetry.MirrorMath.ReflectPoint(once, axes.Center, axes.Forward);
            }
            return Nightshade.Symmetry.MirrorMath.ReflectPoint(point, axes.Center, axes.Right);
        }

        private bool FindFemale(Vector3 reflectedSocket, Socket_Base maleSocket, Vector3 blockPosition, Quaternion blockRotation, out DecayEntity neighbor, out Socket_Base femaleSocket)
        {
            neighbor = null;
            femaleSocket = null;
            List<BuildingBlock> nearby = new List<BuildingBlock>();
            Vis.Entities(reflectedSocket, config.SearchRadius, nearby, -1, QueryTriggerInteraction.Collide);
            if (nearby.Count > config.SearchLimit)
            {
                nearby.Sort((a, b) =>
                {
                    float da = a == null ? float.MaxValue : Vector3.Distance(a.transform.position, reflectedSocket);
                    float db = b == null ? float.MaxValue : Vector3.Distance(b.transform.position, reflectedSocket);
                    return da.CompareTo(db);
                });
                nearby.RemoveRange(config.SearchLimit, nearby.Count - config.SearchLimit);
            }
            float best = 0.15f;
            for (int i = 0; i < nearby.Count; i++)
            {
                BuildingBlock block = nearby[i];
                if (block == null || block.IsDestroyed)
                    continue;
                Construction construction = block.blockDefinition;
                if (construction == null || construction.allSockets == null)
                    continue;
                for (int s = 0; s < construction.allSockets.Length; s++)
                {
                    Socket_Base socket = construction.allSockets[s];
                    if (socket == null || !socket.female || socket.femaleDummy)
                        continue;
                    if (!maleSocket.CanConnect(blockPosition, blockRotation, socket, block.transform.position, block.transform.rotation))
                        continue;
                    Vector3 femaleWorld = block.transform.TransformPoint(socket.localPosition);
                    float distance = Vector3.Distance(femaleWorld, reflectedSocket);
                    if (distance < best)
                    {
                        best = distance;
                        neighbor = block;
                        femaleSocket = socket;
                    }
                }
            }
            return neighbor != null && femaleSocket != null;
        }

        private static bool HasTerrainSocket(Construction definition)
        {
            if (definition.allSockets == null)
                return false;
            for (int i = 0; i < definition.allSockets.Length; i++)
            {
                if (definition.allSockets[i] is Socket_Terrain && definition.allSockets[i].male)
                    return true;
            }
            return false;
        }

        private bool SocketModsAccept(BasePlayer player, Construction definition, Vector3 position, Quaternion rotation, BaseEntity support, Socket_Base female, Socket_Base male, bool terrain)
        {
            Construction.Target target = BuildTarget(player, position, rotation, support, female, terrain);
            Construction.Placement placement = new Construction.Placement(target);
            placement.position = position;
            placement.rotation = rotation;
            if (male != null)
            {
                Vector3 before = placement.position;
                if (!male.CheckSocketMods(ref placement))
                    return false;
                if (Vector3.Distance(before, placement.position) > config.DedupPosition)
                    return false;
            }
            if (terrain)
            {
                Socket_Base terrainSocket = null;
                if (definition.allSockets != null)
                {
                    for (int i = 0; i < definition.allSockets.Length; i++)
                    {
                        if (definition.allSockets[i] is Socket_Terrain && definition.allSockets[i].male)
                        {
                            terrainSocket = definition.allSockets[i];
                            break;
                        }
                    }
                }
                if (terrainSocket == null)
                    return false;
                Vector3 before = placement.position;
                if (!terrainSocket.CheckSocketMods(ref placement))
                    return false;
                if (Vector3.Distance(before, placement.position) > config.DedupPosition)
                    return false;
            }
            return true;
        }

        private static bool PrivilegeBlocked(BasePlayer player, Construction definition, Vector3 position, Quaternion rotation)
        {
            if (definition != null && definition.canBypassBuildingPermission)
                return false;
            return player.IsBuildingBlocked(position, rotation, definition.bounds, false);
        }

        private static bool VolumeBlocked(uint prefabId, Vector3 position, Quaternion rotation)
        {
            DeployVolume[] volumes = PrefabAttribute.server.FindAll<DeployVolume>(prefabId);
            if (volumes == null || volumes.Length == 0)
                return false;
            return DeployVolume.Check(position, rotation, volumes);
        }

        private bool RoadBlocked(BasePlayer player, Construction definition, Vector3 position, Quaternion rotation, BaseEntity support, Socket_Base female)
        {
            Construction.Target target = BuildTarget(player, position, rotation, support, female, support == null);
            Construction.Placement placement = new Construction.Placement(target);
            placement.position = position;
            placement.rotation = rotation;
            return !definition.TestPlacingCloseToRoad(ref placement, target, definition);
        }

        private bool HookBlocked(BasePlayer player, Planner planner, Construction definition, Vector3 position, Quaternion rotation, BaseEntity support, Socket_Base female)
        {
            object first;
            object second;
            try
            {
                first = Interface.CallHook("CanBuild", player, position);
                Construction.Target target = BuildTarget(player, position, rotation, support, female, support == null);
                second = Interface.CallHook("CanBuild", planner, definition, target);
            }
            catch (Exception ex)
            {
                PrintWarning("CanBuild hook failed closed: " + ex.Message);
                return true;
            }
            return !Nightshade.Symmetry.HookPolicy.Allows(first) || !Nightshade.Symmetry.HookPolicy.Allows(second);
        }

        private Construction.Target BuildTarget(BasePlayer player, Vector3 position, Quaternion rotation, BaseEntity support, Socket_Base female, bool terrain)
        {
            Construction.Target target = new Construction.Target();
            target.valid = true;
            target.player = player;
            target.position = position;
            target.normal = Vector3.up;
            target.rotation = rotation.eulerAngles;
            target.onTerrain = terrain;
            target.entity = support;
            target.socket = female;
            Vector3 origin = player.eyes != null ? player.eyes.position : player.transform.position;
            Vector3 direction = position - origin;
            if (direction.sqrMagnitude < 0.001f)
                direction = Vector3.up;
            target.ray = new Ray(origin, direction);
            return target;
        }

        private bool IsDuplicate(BuildingBlock original, Vector3 position, Quaternion rotation)
        {
            List<BuildingBlock> nearby = new List<BuildingBlock>();
            Vis.Entities(position, 0.6f, nearby, -1, QueryTriggerInteraction.Collide);
            for (int i = 0; i < nearby.Count; i++)
            {
                BuildingBlock block = nearby[i];
                if (block == null || block == original || block.IsDestroyed || block.prefabID != original.prefabID)
                    continue;
                if (Vector3.Distance(block.transform.position, position) <= config.DedupPosition
                    && Quaternion.Angle(block.transform.rotation, rotation) <= config.DedupAngle)
                    return true;
            }
            return false;
        }

        private static List<Nightshade.Symmetry.SocketSample> CollectSockets(Construction definition)
        {
            List<Nightshade.Symmetry.SocketSample> sockets = new List<Nightshade.Symmetry.SocketSample>();
            if (definition == null || definition.allSockets == null)
                return sockets;
            for (int i = 0; i < definition.allSockets.Length; i++)
            {
                Socket_Base socket = definition.allSockets[i];
                if (socket == null)
                    continue;
                sockets.Add(new Nightshade.Symmetry.SocketSample(V(socket.localPosition), V(socket.localRotation * Vector3.forward)));
            }
            return sockets;
        }

        private static bool IsBoat(Construction definition)
        {
            if (definition == null || definition.allSockets == null)
                return false;
            for (int i = 0; i < definition.allSockets.Length; i++)
            {
                ConstructionSocket socket = definition.allSockets[i] as ConstructionSocket;
                if (socket != null && socket.blockType == ConstructionSocket.BlockType.Boat)
                    return true;
            }
            return false;
        }

        private static bool IsDefaultGrade(BuildingBlock block, Construction definition)
        {
            if (block == null || definition == null || definition.defaultGrade == null || definition.defaultGrade.gradeBase == null)
                return false;
            return block.grade == definition.defaultGrade.gradeBase.type;
        }

        private bool IsExcluded(string shortName)
        {
            string name = Nightshade.Symmetry.PieceCatalog.NormalizePrefab(shortName);
            if (config.Excluded == null)
                return false;
            for (int i = 0; i < config.Excluded.Count; i++)
            {
                if (Nightshade.Symmetry.PieceCatalog.NormalizePrefab(config.Excluded[i]) == name)
                    return true;
            }
            return false;
        }

        private static List<Nightshade.Symmetry.CostLine> BuildCost(Construction definition)
        {
            List<Nightshade.Symmetry.CostLine> cost = new List<Nightshade.Symmetry.CostLine>();
            if (definition == null || definition.defaultGrade == null)
                return cost;
            List<ItemAmount> amounts = definition.defaultGrade.CostToBuild();
            if (amounts == null)
                return cost;
            for (int i = 0; i < amounts.Count; i++)
            {
                ItemAmount amount = amounts[i];
                if (amount == null || amount.itemDef == null || amount.amount <= 0f)
                    continue;
                cost.Add(new Nightshade.Symmetry.CostLine(amount.itemDef.itemid, (int)amount.amount));
            }
            return cost;
        }

        private static bool ShouldCharge(BasePlayer player)
        {
            bool creative = player.IsInCreativeMode && ConVar.Creative.freeBuild;
            bool tutorial = player.IsInTutorial;
            return Nightshade.Symmetry.ChargePolicy.ShouldCharge(creative, tutorial);
        }

        private static Nightshade.Symmetry.MaterialLedger SeedLedger(BasePlayer player, List<Nightshade.Symmetry.CostLine> cost)
        {
            Nightshade.Symmetry.MaterialLedger ledger = new Nightshade.Symmetry.MaterialLedger();
            for (int i = 0; i < cost.Count; i++)
                ledger.Set(cost[i].ItemId, player.inventory.GetAmount(cost[i].ItemId, true, true));
            return ledger;
        }

        private bool TakeCost(BasePlayer player, List<Nightshade.Symmetry.CostLine> cost, out List<TakenLine> taken, out string error)
        {
            taken = new List<TakenLine>();
            error = "";
            for (int i = 0; i < cost.Count; i++)
            {
                List<Item> items = new List<Item>();
                int got = player.inventory.Take(items, cost[i].ItemId, cost[i].Amount);
                TakenLine line = new TakenLine();
                line.ItemId = cost[i].ItemId;
                line.Amount = got;
                line.Items = items;
                taken.Add(line);
                if (got < cost[i].Amount)
                {
                    ReturnTaken(player, taken);
                    error = "short";
                    return false;
                }
            }
            for (int i = 0; i < taken.Count; i++)
            {
                List<Item> items = taken[i].Items;
                for (int n = 0; n < items.Count; n++)
                {
                    if (items[n] != null)
                        items[n].Remove();
                }
            }
            return true;
        }

        private void UndoTake(BasePlayer player, Nightshade.Symmetry.MaterialLedger ledger, int receipt, List<TakenLine> taken, bool consumed)
        {
            if (ledger != null && receipt != 0 && !ledger.TryRefund(receipt))
                return;
            if (!consumed)
            {
                ReturnTaken(player, taken);
                return;
            }
            if (taken == null)
                return;
            List<Nightshade.Symmetry.CostLine> cost = new List<Nightshade.Symmetry.CostLine>();
            for (int i = 0; i < taken.Count; i++)
                cost.Add(new Nightshade.Symmetry.CostLine(taken[i].ItemId, taken[i].Amount));
            GiveCost(player, cost);
        }

        private static void ReturnTaken(BasePlayer player, List<TakenLine> taken)
        {
            if (taken == null)
                return;
            for (int i = 0; i < taken.Count; i++)
            {
                List<Item> items = taken[i].Items;
                if (items == null)
                    continue;
                for (int n = 0; n < items.Count; n++)
                {
                    Item item = items[n];
                    if (item == null)
                        continue;
                    if (!player.inventory.GiveItem(item, player.inventory.containerMain, GiveItemOptions.None)
                        && !player.inventory.GiveItem(item, player.inventory.containerBelt, GiveItemOptions.None))
                        item.Drop(player.transform.position + Vector3.up, Vector3.zero, Quaternion.identity);
                }
            }
        }

        private static void GiveCost(BasePlayer player, List<Nightshade.Symmetry.CostLine> cost)
        {
            for (int i = 0; i < cost.Count; i++)
            {
                int left = cost[i].Amount;
                int guard = 0;
                while (left > 0 && guard < 20)
                {
                    guard++;
                    Item item = ItemManager.CreateByItemID(cost[i].ItemId, left, 0UL, 0UL);
                    if (item == null)
                        break;
                    int made = item.amount;
                    if (made <= 0)
                        break;
                    if (!player.inventory.GiveItem(item, player.inventory.containerMain, GiveItemOptions.None)
                        && !player.inventory.GiveItem(item, player.inventory.containerBelt, GiveItemOptions.None))
                        item.Drop(player.transform.position + Vector3.up, Vector3.zero, Quaternion.identity);
                    left -= made;
                }
            }
        }

        private static void NoteCost(BasePlayer player, List<Nightshade.Symmetry.CostLine> cost)
        {
            for (int i = 0; i < cost.Count; i++)
                player.Command("note.inv", cost[i].ItemId, (float)(-cost[i].Amount));
        }

        private bool ConsumeRate(ulong playerId)
        {
            float now = Time.realtimeSinceStartup;
            RateWindow window;
            if (!rates.TryGetValue(playerId, out window) || now - window.Start >= 1f)
            {
                window.Start = now;
                window.Count = 1;
                rates[playerId] = window;
                return true;
            }
            window.Count++;
            rates[playerId] = window;
            return Nightshade.Symmetry.MirrorMath.AllowRate(window.Count, config.MaxPlacementsPerSecond);
        }

        private bool HasUse(BasePlayer player)
        {
            if (player == null || string.IsNullOrEmpty(player.UserIDString))
                return false;
            return permission.UserHasPermission(player.UserIDString, UsePermission);
        }

        private void DisablePlayer(ulong playerId, bool keepCenter)
        {
            enabled.Remove(playerId);
            if (!keepCenter)
            {
            }
        }

        private void StoreCenter(ulong playerId, Nightshade.Symmetry.Axes axes, string source)
        {
            LiveCenter live = new LiveCenter();
            live.Axes = axes;
            live.Source = source;
            centers[playerId] = live;
            if (store == null)
                store = new Store();
            if (store.Centers == null)
                store.Centers = new Dictionary<string, SavedCenter>();
            SavedCenter saved = new SavedCenter();
            saved.X = axes.Center.X;
            saved.Y = axes.Center.Y;
            saved.Z = axes.Center.Z;
            saved.RightX = axes.Right.X;
            saved.RightY = axes.Right.Y;
            saved.RightZ = axes.Right.Z;
            saved.ForwardX = axes.Forward.X;
            saved.ForwardY = axes.Forward.Y;
            saved.ForwardZ = axes.Forward.Z;
            saved.Source = source;
            store.Centers[playerId.ToString(CultureInfo.InvariantCulture)] = saved;
            store.WorldKey = CurrentWorldKey();
            SaveStore();
        }

        private void RemoveStored(ulong playerId)
        {
            if (store == null || store.Centers == null)
                return;
            store.Centers.Remove(playerId.ToString(CultureInfo.InvariantCulture));
        }

        private void LoadStore()
        {
            store = new Store();
            if (!config.PersistCenters)
                return;
            try
            {
                store = Interface.Oxide.DataFileSystem.ReadObject<Store>(DataPath) ?? new Store();
                dataWritable = true;
            }
            catch (Exception ex)
            {
                dataWritable = false;
                store = new Store();
                PrintError("NightshadeSymmetry center data was not loaded and will not be overwritten: " + ex.Message);
                return;
            }
            if (store.Centers == null)
                store.Centers = new Dictionary<string, SavedCenter>();
            ApplyWorldIdentity(true);
        }

        private void ApplyWorldIdentity(bool fromLoad)
        {
            if (store == null)
                store = new Store();
            string key = CurrentWorldKey();
            if (!string.IsNullOrEmpty(store.WorldKey) && !string.IsNullOrEmpty(key) && store.WorldKey != key)
            {
                store.Centers.Clear();
                centers.Clear();
                store.WorldKey = key;
                SaveStore();
                Puts("NightshadeSymmetry cleared centers because the world identity changed.");
                return;
            }
            if (string.IsNullOrEmpty(store.WorldKey) && !string.IsNullOrEmpty(key))
                store.WorldKey = key;
            centers.Clear();
            if (store.Centers == null)
                return;
            foreach (KeyValuePair<string, SavedCenter> pair in store.Centers)
            {
                ulong id;
                if (!ulong.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) || pair.Value == null)
                    continue;
                Nightshade.Symmetry.Vec3 right = new Nightshade.Symmetry.Vec3(pair.Value.RightX, pair.Value.RightY, pair.Value.RightZ);
                Nightshade.Symmetry.Vec3 forward = new Nightshade.Symmetry.Vec3(pair.Value.ForwardX, pair.Value.ForwardY, pair.Value.ForwardZ);
                if (!Nightshade.Symmetry.Axes.IsOrthonormal(right, forward))
                    continue;
                LiveCenter live = new LiveCenter();
                live.Axes.Center = new Nightshade.Symmetry.Vec3(pair.Value.X, pair.Value.Y, pair.Value.Z);
                live.Axes.Right = right;
                live.Axes.Forward = forward;
                live.Source = pair.Value.Source ?? "";
                centers[id] = live;
            }
        }

        private void SaveStore()
        {
            if (!dataWritable || !config.PersistCenters || store == null)
                return;
            if (string.IsNullOrEmpty(store.WorldKey))
                store.WorldKey = CurrentWorldKey();
            Interface.Oxide.DataFileSystem.WriteObject(DataPath, store);
        }

        private static string CurrentWorldKey()
        {
            string wipe = SaveRestore.WipeId ?? "";
            return wipe + "|" + World.Seed.ToString(CultureInfo.InvariantCulture) + "|" + World.Size.ToString(CultureInfo.InvariantCulture);
        }

        private void ReplyStatus(BasePlayer player)
        {
            LiveCenter center;
            if (!centers.TryGetValue(player.userID, out center))
            {
                Reply(player, "StatusNoCenter", "cmd", config.Command);
                return;
            }
            bool isOn = false;
            enabled.TryGetValue(player.userID, out isOn);
            Reply(player, isOn ? "StatusOn" : "StatusOff",
                "source", string.IsNullOrEmpty(center.Source) ? "saved" : center.Source,
                "pos", Format(center.Axes.Center),
                "yaw", Nightshade.Symmetry.Axes.YawDegrees(center.Axes.Forward).ToString("0", CultureInfo.InvariantCulture));
        }

        private void Throttled(BasePlayer player, string key, params string[] tokens)
        {
            float now = Time.realtimeSinceStartup;
            float last;
            if (chatThrottle.TryGetValue(player.userID, out last) && now - last < 1.5f)
                return;
            chatThrottle[player.userID] = now;
            Reply(player, key, tokens);
        }

        private void Reply(BasePlayer player, string key, params string[] tokens)
        {
            ReplyRaw(player, Msg(key, tokens));
        }

        private void ReplyRaw(BasePlayer player, string message)
        {
            if (player == null)
                return;
            player.ChatMessage(config.Prefix + " " + message);
        }

        private string Msg(string key, params string[] tokens)
        {
            string message;
            if (config.Messages == null || !config.Messages.TryGetValue(key, out message) || string.IsNullOrEmpty(message))
                message = key;
            for (int i = 0; i + 1 < tokens.Length; i += 2)
                message = message.Replace("{" + tokens[i] + "}", tokens[i + 1] ?? "");
            return message;
        }

        private string Label(string key)
        {
            return Msg(key);
        }

        private void Toast(BasePlayer player, string text)
        {
            if (!config.ShowToast || player == null || !player.IsConnected)
                return;
            CuiHelper.DestroyUi(player, UiRoot);
            CuiElementContainer container = new CuiElementContainer();
            container.Add(new CuiPanel
            {
                Image = { Color = Cui(config.PanelColor, 0.9f) },
                RectTransform = { AnchorMin = "0.32 0.88", AnchorMax = "0.68 0.93" }
            }, "Overlay", UiRoot);
            container.Add(new CuiLabel
            {
                Text = { Text = text, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = Cui(config.TextColor, 1f) },
                RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.98 1" }
            }, UiRoot);
            CuiHelper.AddUi(player, container);
            Timer existing;
            if (toasts.TryGetValue(player.userID, out existing))
                existing?.Destroy();
            ulong id = player.userID;
            toasts[id] = timer.Once(4f, () =>
            {
                BasePlayer online = BasePlayer.FindByID(id);
                if (online != null)
                    CuiHelper.DestroyUi(online, UiRoot);
            });
        }

        private static string Cui(string hex, float alpha)
        {
            float r, g, b;
            if (!TryRgb(hex, out r, out g, out b))
            {
                r = 0.64f;
                g = 0.4f;
                b = 0.83f;
            }
            return r.ToString("0.00", CultureInfo.InvariantCulture) + " "
                + g.ToString("0.00", CultureInfo.InvariantCulture) + " "
                + b.ToString("0.00", CultureInfo.InvariantCulture) + " "
                + alpha.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static Color Rgb(string hex, Color fallback)
        {
            float r, g, b;
            if (!TryRgb(hex, out r, out g, out b))
                return fallback;
            return new Color(r, g, b, 1f);
        }

        private static bool TryRgb(string hex, out float r, out float g, out float b)
        {
            r = g = b = 1f;
            if (string.IsNullOrEmpty(hex))
                return false;
            string text = hex.Trim();
            if (text.StartsWith("#"))
                text = text.Substring(1);
            int value;
            if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                return false;
            r = ((value >> 16) & 255) / 255f;
            g = ((value >> 8) & 255) / 255f;
            b = (value & 255) / 255f;
            return true;
        }

        private static string Format(Nightshade.Symmetry.Vec3 value)
        {
            return value.X.ToString("0.0", CultureInfo.InvariantCulture) + " "
                + value.Y.ToString("0.0", CultureInfo.InvariantCulture) + " "
                + value.Z.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static ulong NetId(BaseEntity entity)
        {
            if (entity == null || entity.net == null)
                return 0;
            return entity.net.ID.Value;
        }

        private static Nightshade.Symmetry.Vec3 V(Vector3 value)
        {
            return new Nightshade.Symmetry.Vec3(value.x, value.y, value.z);
        }

        private static Vector3 U(Nightshade.Symmetry.Vec3 value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }

        private static Nightshade.Symmetry.Quat Q(Quaternion value)
        {
            return new Nightshade.Symmetry.Quat(value.x, value.y, value.z, value.w);
        }

        private static Quaternion U(Nightshade.Symmetry.Quat value)
        {
            return new Quaternion(value.X, value.Y, value.Z, value.W);
        }
    }
}
#endif
