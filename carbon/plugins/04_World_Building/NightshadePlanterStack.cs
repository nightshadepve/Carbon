#if !NIGHTSHADE_PLANTER_STACK_TESTS
using HarmonyLib;
using Nightshade.PlanterStack;
using Oxide.Core.Plugins;
using ProtoBuf;
using UnityEngine;
#endif
using System;
using System.Collections.Generic;

namespace Nightshade.PlanterStack
{
    public enum PlanterStackDecision
    {
        Allow = 0,
        Deny = 1
    }

    public enum PlanterStackDenyReason
    {
        None = 0,
        Disabled = 1,
        MissingPermission = 2,
        NotConfiguredPlanter = 3,
        VanillaCheckFailed = 4,
        BlockedByNonPlanter = 5,
        VerticalGap = 6,
        NoPlanterBlock = 7
    }

    public sealed class PlanterBlocker
    {
        public bool IsConfiguredPlanter;
        public float VerticalDistance;
        public float HorizontalDistance;
    }

    public sealed class PlanterStackFacts
    {
        public bool Enabled;
        public bool HasPermission;
        public bool IsAdmin;
        public bool PlacingConfiguredPlanter;
        public float MinimumVerticalGap;
        public float HorizontalOverlap;
        public List<string> FailedVanillaChecks;
        public List<PlanterBlocker> Blockers;
    }

    public readonly struct PlanterStackResult
    {
        public readonly PlanterStackDecision Decision;
        public readonly PlanterStackDenyReason Reason;

        public PlanterStackResult(PlanterStackDecision decision, PlanterStackDenyReason reason)
        {
            Decision = decision;
            Reason = reason;
        }

        public bool Allowed
        {
            get { return Decision == PlanterStackDecision.Allow; }
        }
    }

    // Policy for planter-on-planter placement. Admin status is stored on the
    // facts so callers can pass it, and Decide does not read it.
    public static class PlanterStackRules
    {
        public static bool CanOpenGate(bool enabled, bool hasPermission, bool placingConfiguredPlanter)
        {
            return enabled && hasPermission && placingConfiguredPlanter;
        }

        public static bool Matches(string value, IReadOnlyList<string> identifiers)
        {
            if (string.IsNullOrEmpty(value) || identifiers == null)
            {
                return false;
            }

            for (int i = 0; i < identifiers.Count; i++)
            {
                string identifier = identifiers[i];
                if (string.IsNullOrWhiteSpace(identifier))
                {
                    continue;
                }

                if (value.IndexOf(identifier.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool GapViolated(float minimumVerticalGap, float horizontalOverlap, float verticalDistance, float horizontalDistance)
        {
            if (minimumVerticalGap <= 0f)
            {
                return false;
            }

            return horizontalDistance <= horizontalOverlap && verticalDistance < minimumVerticalGap;
        }

        public static PlanterStackResult Decide(PlanterStackFacts facts)
        {
            if (facts == null)
            {
                return Deny(PlanterStackDenyReason.Disabled);
            }

            if (!facts.Enabled)
            {
                return Deny(PlanterStackDenyReason.Disabled);
            }

            if (!facts.HasPermission)
            {
                return Deny(PlanterStackDenyReason.MissingPermission);
            }

            if (!facts.PlacingConfiguredPlanter)
            {
                return Deny(PlanterStackDenyReason.NotConfiguredPlanter);
            }

            if (!CanOpenGate(facts.Enabled, facts.HasPermission, facts.PlacingConfiguredPlanter))
            {
                return Deny(PlanterStackDenyReason.Disabled);
            }

            if (HasVanillaFailure(facts.FailedVanillaChecks))
            {
                return Deny(PlanterStackDenyReason.VanillaCheckFailed);
            }

            bool planterBlock = false;
            List<PlanterBlocker> blockers = facts.Blockers;
            if (blockers != null)
            {
                for (int i = 0; i < blockers.Count; i++)
                {
                    PlanterBlocker blocker = blockers[i];
                    if (blocker == null)
                    {
                        continue;
                    }

                    if (!blocker.IsConfiguredPlanter)
                    {
                        return Deny(PlanterStackDenyReason.BlockedByNonPlanter);
                    }

                    planterBlock = true;
                    if (GapViolated(facts.MinimumVerticalGap, facts.HorizontalOverlap, blocker.VerticalDistance, blocker.HorizontalDistance))
                    {
                        return Deny(PlanterStackDenyReason.VerticalGap);
                    }
                }
            }

            if (!planterBlock)
            {
                return Deny(PlanterStackDenyReason.NoPlanterBlock);
            }

            return new PlanterStackResult(PlanterStackDecision.Allow, PlanterStackDenyReason.None);
        }

        private static bool HasVanillaFailure(List<string> checks)
        {
            if (checks == null)
            {
                return false;
            }

            for (int i = 0; i < checks.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(checks[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static PlanterStackResult Deny(PlanterStackDenyReason reason)
        {
            return new PlanterStackResult(PlanterStackDecision.Deny, reason);
        }
    }
}

#if !NIGHTSHADE_PLANTER_STACK_TESTS
namespace Oxide.Plugins
{
    [Info("NightshadePlanterStack", "Nightshade", "1.0.0")]
    [Description("Lets permitted players place configured planters when the only deploy-volume block is another configured planter.")]
    public class NightshadePlanterStack : RustPlugin
    {
        private const string PermUse = "nightshadeplanterstack.use";
        private const float RecentPlaceSeconds = 0.5f;

        private static NightshadePlanterStack instance;
        private PluginConfig config;
        private readonly Dictionary<string, float> yaw = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> heldKey = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> recentPlace = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> clickGeneration = new Dictionary<string, int>(StringComparer.Ordinal);

        public NightshadePlanterStack()
        {
            instance = this;
        }

        private void Init()
        {
            instance = this;
            permission.RegisterPermission(PermUse, this);
        }

        private void OnServerInitialized()
        {
            Puts("Ready. Permission " + PermUse + " is not granted to anyone automatically.");
        }

        private void Unload()
        {
            if (instance == this)
            {
                instance = null;
            }

            yaw.Clear();
            heldKey.Clear();
            recentPlace.Clear();
            clickGeneration.Clear();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null)
            {
                return;
            }

            string id = player.UserIDString;
            yaw.Remove(id);
            heldKey.Remove(id);
            recentPlace.Remove(id);
            clickGeneration.Remove(id);
        }

        #region Config

        private class PluginConfig
        {
            [Newtonsoft.Json.JsonProperty("Enabled")]
            public bool Enabled = true;

            [Newtonsoft.Json.JsonProperty("Planter identifiers")]
            public List<string> Planters = new List<string>
            {
                "planter.large",
                "planter.small",
                "planter.triangle",
                "minecart.planter",
                "railroadplanter",
                "bathtub.planter"
            };

            [Newtonsoft.Json.JsonProperty("Minimum vertical gap")]
            public float MinimumVerticalGap = 0f;

            [Newtonsoft.Json.JsonProperty("Horizontal overlap for gap")]
            public float HorizontalOverlap = 0.75f;

            [Newtonsoft.Json.JsonProperty("Gap search radius")]
            public float GapSearchRadius = 3f;
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
                config = Config.ReadObject<PluginConfig>();
                if (config == null)
                {
                    throw new Exception("missing");
                }
            }
            catch
            {
                PrintWarning("Config file corrupt or missing, creating a new one.");
                LoadDefaultConfig();
            }

            if (config.Planters == null)
            {
                config.Planters = new PluginConfig().Planters;
            }

            if (config.MinimumVerticalGap < 0f)
            {
                config.MinimumVerticalGap = 0f;
            }

            if (config.HorizontalOverlap < 0f)
            {
                config.HorizontalOverlap = 0f;
            }

            if (config.GapSearchRadius < 0.5f)
            {
                config.GapSearchRadius = 0.5f;
            }

            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(config, true);
        }

        #endregion

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || input == null || config == null || !config.Enabled)
            {
                return;
            }

            bool primary = input.WasJustPressed(BUTTON.FIRE_PRIMARY);
            bool reload = input.WasJustPressed(BUTTON.RELOAD);
            if (!primary && !reload)
            {
                return;
            }

            if (!HasPermission(player))
            {
                return;
            }

            HeldEntity held = player.GetHeldEntity();
            Planner planner = held as Planner;
            if (planner != null)
            {
                HandlePlanner(player, planner, input, primary);
                return;
            }

            if (!primary)
            {
                return;
            }

            Deployer deployer = held as Deployer;
            if (deployer != null)
            {
                HandleDeployer(player, deployer);
            }
        }

        private void HandlePlanner(BasePlayer player, Planner planner, InputState input, bool primary)
        {
            ItemModDeployable mod;
            Construction construction;
            if (!TryGetPlanterConstruction(planner, out mod, out construction) || construction == null)
            {
                return;
            }

            TrackYaw(player, planner, input, construction);
            if (!primary)
            {
                return;
            }

            RaycastHit hit;
            if (!TryTrace(player, construction, out hit))
            {
                return;
            }

            if (!NearConfiguredPlanter(hit.point, hit.GetEntity()))
            {
                return;
            }

            Construction.Target target = BuildTarget(player, planner, mod, hit);
            if (Probe(construction, target, true))
            {
                return;
            }

            PlacementGate.GapDenied = false;
            bool gated = Probe(construction, target, false);
            if (PlacementGate.GapDenied)
            {
                if (!string.IsNullOrEmpty(Construction.lastPlacementError.translated))
                {
                    player.ShowToast(GameTip.Styles.Error, Construction.lastPlacementError, false);
                }

                return;
            }

            if (!gated)
            {
                return;
            }

            Item item = planner.GetOwnerItem();
            if (item == null)
            {
                return;
            }

            string userId = player.UserIDString;
            int amount = item.amount;
            int generation;
            clickGeneration.TryGetValue(userId, out generation);
            generation++;
            clickGeneration[userId] = generation;
            Construction.Target captured = target;
            NextTick(() => PlaceCaptured(userId, generation, amount, captured));
        }

        private void PlaceCaptured(string userId, int generation, int amount, Construction.Target target)
        {
            int currentGeneration;
            if (!clickGeneration.TryGetValue(userId, out currentGeneration) || currentGeneration != generation)
            {
                return;
            }

            BasePlayer player = FindPlayer(userId);
            if (player == null || config == null || !config.Enabled || !HasPermission(player))
            {
                return;
            }

            Planner planner = player.GetHeldEntity() as Planner;
            if (planner == null)
            {
                return;
            }

            ItemModDeployable mod;
            Construction construction;
            if (!TryGetPlanterConstruction(planner, out mod, out construction) || construction == null)
            {
                return;
            }

            Item item = planner.GetOwnerItem();
            if (item == null || item.amount < amount)
            {
                return;
            }

            target.player = player;
            BaseEntity built = planner.DoBuild(target, construction);
            if (built != null)
            {
                recentPlace[userId] = Time.realtimeSinceStartup;
            }
        }

        private BasePlayer FindPlayer(string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                return null;
            }

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player != null && player.UserIDString == userId)
                {
                    return player;
                }
            }

            return null;
        }

        private bool HasPermission(BasePlayer player)
        {
            if (player == null)
            {
                return false;
            }

            return permission.UserHasPermission(player.UserIDString, PermUse);
        }

        private bool RecentlyPlaced(BasePlayer player)
        {
            if (player == null)
            {
                return false;
            }

            float when;
            if (!recentPlace.TryGetValue(player.UserIDString, out when))
            {
                return false;
            }

            return Time.realtimeSinceStartup - when <= RecentPlaceSeconds;
        }

        private bool TryGetPlanterConstruction(Planner planner, out ItemModDeployable mod, out Construction construction)
        {
            mod = planner != null ? planner.GetModDeployable() : null;
            construction = null;
            if (mod == null || mod.entityPrefab == null || config == null)
            {
                return false;
            }

            ItemDefinition definition = planner.GetOwnerItemDefinition();
            string shortname = definition != null ? definition.shortname : null;
            string path = mod.entityPrefab.resourcePath;
            construction = PrefabAttribute.server.Find<Construction>(mod.entityPrefab.resourceID);
            string fullName = construction != null ? construction.fullName : null;
            return PlanterStackRules.Matches(shortname, config.Planters)
                || PlanterStackRules.Matches(path, config.Planters)
                || PlanterStackRules.Matches(fullName, config.Planters);
        }

        private bool IsConfiguredPlanterEntity(BaseEntity entity)
        {
            if (entity == null || config == null)
            {
                return false;
            }

            return PlanterStackRules.Matches(entity.ShortPrefabName, config.Planters)
                || PlanterStackRules.Matches(entity.PrefabName, config.Planters);
        }

        private bool IsConfiguredConstruction(Construction construction)
        {
            if (construction == null || config == null)
            {
                return false;
            }

            return PlanterStackRules.Matches(construction.fullName, config.Planters);
        }

        private bool IsConfiguredDeployable(Deployable deployable)
        {
            if (deployable == null || config == null)
            {
                return false;
            }

            return PlanterStackRules.Matches(deployable.fullName, config.Planters);
        }

        private bool ShouldRelaxVolumes(Construction placed, BasePlayer player)
        {
            if (placed == null)
            {
                return false;
            }

            if (IsConfiguredConstruction(placed))
            {
                return true;
            }

            Planner planner = player != null ? player.GetHeldEntity() as Planner : null;
            ItemModDeployable mod;
            Construction held;
            if (!TryGetPlanterConstruction(planner, out mod, out held) || mod == null || mod.entityPrefab == null)
            {
                return false;
            }

            return mod.entityPrefab.resourceID == placed.prefabID;
        }

        private bool ShouldRelaxDeployer(Deployer deployer, Deployable deployable)
        {
            if (IsConfiguredDeployable(deployable))
            {
                return true;
            }

            if (deployer == null || config == null)
            {
                return false;
            }

            ItemDefinition definition = deployer.GetOwnerItemDefinition();
            return PlanterStackRules.Matches(definition != null ? definition.shortname : null, config.Planters);
        }

        private void TrackYaw(BasePlayer player, Planner planner, InputState input, Construction construction)
        {
            string id = player.UserIDString;
            ItemDefinition definition = planner.GetOwnerItemDefinition();
            string key = definition != null ? definition.shortname : string.Empty;
            string previous;
            if (!heldKey.TryGetValue(id, out previous) || previous != key)
            {
                heldKey[id] = key;
                yaw[id] = 0f;
            }

            if (!input.WasJustPressed(BUTTON.RELOAD))
            {
                return;
            }

            float step = construction != null ? construction.rotationAmount.y : 90f;
            if (Mathf.Abs(step) < 0.01f)
            {
                step = 90f;
            }

            float current;
            yaw.TryGetValue(id, out current);
            yaw[id] = current + step;
        }

        private float CurrentYaw(BasePlayer player)
        {
            float value;
            yaw.TryGetValue(player.UserIDString, out value);
            return value;
        }

        private bool TryTrace(BasePlayer player, Construction construction, out RaycastHit hit)
        {
            hit = default(RaycastHit);
            if (player.eyes == null)
            {
                return false;
            }

            Ray ray = player.eyes.HeadRay();
            float distance = (construction != null ? construction.maxplaceDistance : 4f) + 1f;
            int mask = TraceMask(construction);
            return GamePhysics.Trace(ray, 0f, out hit, distance, mask, QueryTriggerInteraction.Ignore, player);
        }

        private static int TraceMask(Construction construction)
        {
            int mask = Rust.Layers.PlannerPlacement;
            if (construction != null)
            {
                if (construction.overridePlacementLayer)
                {
                    mask = construction.overridedPlacementLayer;
                }

                mask |= construction.additionalPlacementLayer;
            }

            return mask;
        }

        private bool NearConfiguredPlanter(Vector3 point, BaseEntity hitEntity)
        {
            if (IsConfiguredPlanterEntity(hitEntity))
            {
                return true;
            }

            float radius = config != null ? config.GapSearchRadius : 3f;
            Collider[] colliders = Physics.OverlapSphere(point, radius, Rust.Layers.PlannerPlacement, QueryTriggerInteraction.Collide);
            if (colliders == null)
            {
                return false;
            }

            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null)
                {
                    continue;
                }

                if (IsConfiguredPlanterEntity(collider.ToBaseEntity()))
                {
                    return true;
                }
            }

            return false;
        }

        private void HandleDeployer(BasePlayer player, Deployer deployer)
        {
            Deployable deployable = deployer.GetDeployable();
            if (!ShouldRelaxDeployer(deployer, deployable) || player.eyes == null)
            {
                return;
            }

            Ray ray = player.eyes.HeadRay();
            RaycastHit hit;
            if (!GamePhysics.Trace(ray, 0f, out hit, 8f, Rust.Layers.Deploy, QueryTriggerInteraction.Ignore, player))
            {
                return;
            }

            if (!NearConfiguredPlanter(hit.point, hit.GetEntity()))
            {
                return;
            }

            bool previous = PlacementGate.Suppress;
            PlacementGate.Suppress = true;
            bool vanilla;
            try
            {
                vanilla = deployer.CheckPlacement(deployable, ray, 8f);
            }
            finally
            {
                PlacementGate.Suppress = previous;
            }

            if (vanilla)
            {
                return;
            }

            PlacementGate.GapDenied = false;
            PlacementGate.Suppress = false;
            bool gated = deployer.CheckPlacement(deployable, ray, 8f);
            if (PlacementGate.GapDenied)
            {
                if (!string.IsNullOrEmpty(Construction.lastPlacementError.translated))
                {
                    player.ShowToast(GameTip.Styles.Error, Construction.lastPlacementError, false);
                }

                return;
            }

            if (!gated)
            {
                return;
            }

            Item item = deployer.GetOwnerItem();
            if (item == null)
            {
                return;
            }

            string userId = player.UserIDString;
            int amount = item.amount;
            int generation;
            clickGeneration.TryGetValue(userId, out generation);
            generation++;
            clickGeneration[userId] = generation;
            NextTick(() => PlaceDeployer(userId, generation, amount, ray));
        }

        private void PlaceDeployer(string userId, int generation, int amount, Ray ray)
        {
            int currentGeneration;
            if (!clickGeneration.TryGetValue(userId, out currentGeneration) || currentGeneration != generation)
            {
                return;
            }

            BasePlayer player = FindPlayer(userId);
            if (player == null || config == null || !config.Enabled || !HasPermission(player))
            {
                return;
            }

            Deployer deployer = player.GetHeldEntity() as Deployer;
            if (deployer == null)
            {
                return;
            }

            Deployable deployable = deployer.GetDeployable();
            if (!ShouldRelaxDeployer(deployer, deployable))
            {
                return;
            }

            Item item = deployer.GetOwnerItem();
            if (item == null || item.amount < amount)
            {
                return;
            }

            deployer.DoDeploy_Regular(deployable, ray);
            Item after = deployer.GetOwnerItem();
            if (after == null || after.amount < amount)
            {
                recentPlace[userId] = Time.realtimeSinceStartup;
            }
        }

        private Construction.Target BuildTarget(BasePlayer player, Planner planner, ItemModDeployable mod, RaycastHit hit)
        {
            BaseEntity entity = hit.GetEntity();
            Deployable deployable = mod != null ? PrefabAttribute.server.Find<Deployable>(mod.entityPrefab.resourceID) : null;
            Construction.Target target = default(Construction.Target);
            target.valid = true;
            target.player = player;
            target.ray = player.eyes.HeadRay();
            target.position = hit.point;
            target.normal = hit.normal;
            target.rotation = new Vector3(0f, CurrentYaw(player), 0f);
            target.onTerrain = true;
            target.entity = entity;
            target.socket = null;
            target.shouldParent = planner.ShouldParent(entity, deployable);
            return target;
        }

        private bool Probe(Construction construction, Construction.Target target, bool suppressVolumes)
        {
            if (construction == null || GameManager.server == null || string.IsNullOrEmpty(construction.fullName))
            {
                return false;
            }

            GameObject temp = GameManager.server.CreatePrefab(construction.fullName, Vector3.zero, Quaternion.identity, false);
            if (temp == null)
            {
                return false;
            }

            bool previous = PlacementGate.Suppress;
            PlacementGate.Suppress = suppressVolumes;
            try
            {
                return construction.UpdatePlacement(temp.transform, construction, ref target);
            }
            finally
            {
                PlacementGate.Suppress = previous;
                DestroyPrefab(temp);
            }
        }

        private static void DestroyPrefab(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return;
            }

            BaseEntity entity = gameObject.ToBaseEntity();
            if (entity != null && entity.IsValid())
            {
                entity.Kill();
                return;
            }

            GameManager.Destroy(gameObject);
        }

        private bool ViolatesGap(Vector3 position, BaseEntity ignored)
        {
            if (config == null || config.MinimumVerticalGap <= 0f)
            {
                return false;
            }

            Collider[] colliders = Physics.OverlapSphere(position, config.GapSearchRadius, Rust.Layers.PlannerPlacement, QueryTriggerInteraction.Collide);
            if (colliders == null)
            {
                return false;
            }

            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null)
                {
                    continue;
                }

                BaseEntity entity = collider.ToBaseEntity();
                if (entity == null || entity == ignored || !IsConfiguredPlanterEntity(entity))
                {
                    continue;
                }

                Vector3 delta = entity.transform.position - position;
                float vertical = Mathf.Abs(delta.y);
                float horizontal = new Vector3(delta.x, 0f, delta.z).magnitude;
                if (vertical < 0.05f && horizontal < 0.05f)
                {
                    continue;
                }

                if (PlanterStackRules.GapViolated(config.MinimumVerticalGap, config.HorizontalOverlap, vertical, horizontal))
                {
                    return true;
                }
            }

            return false;
        }

        private static class PlacementGate
        {
            [ThreadStatic]
            private static List<NightshadePlanterStack> stack;

            [ThreadStatic]
            public static bool Suppress;

            [ThreadStatic]
            public static bool GapDenied;

            public static NightshadePlanterStack Current
            {
                get
                {
                    if (stack == null || stack.Count == 0)
                    {
                        return null;
                    }

                    return stack[stack.Count - 1];
                }
            }

            public static void Push(NightshadePlanterStack gate)
            {
                if (stack == null)
                {
                    stack = new List<NightshadePlanterStack>();
                }

                stack.Add(gate);
            }

            public static void Pop()
            {
                if (stack == null || stack.Count == 0)
                {
                    return;
                }

                stack.RemoveAt(stack.Count - 1);
            }
        }

        [AutoPatch(IsRequired = true, Silent = true)]
        [HarmonyPatch(typeof(Construction), "UpdatePlacement")]
        private static class UpdatePlacementPatch
        {
            private static void Prefix(Construction __instance, Construction common, ref Construction.Target target)
            {
                NightshadePlanterStack plugin = instance;
                Construction placed = __instance != null ? __instance : common;
                if (plugin == null || PlacementGate.Suppress || !plugin.ShouldRelaxVolumes(placed, target.player))
                {
                    PlacementGate.Push(null);
                    return;
                }

                bool permitted = plugin.HasPermission(target.player);
                if (!PlanterStackRules.CanOpenGate(plugin.config != null && plugin.config.Enabled, permitted, true))
                {
                    PlacementGate.Push(null);
                    return;
                }

                PlacementGate.Push(plugin);
            }

            private static void Postfix(Transform transform, ref bool __result)
            {
                NightshadePlanterStack plugin = PlacementGate.Current;
                if (plugin == null || !__result || transform == null)
                {
                    return;
                }

                BaseEntity self = transform.ToBaseEntity();
                if (!plugin.ViolatesGap(transform.position, self))
                {
                    return;
                }

                __result = false;
                PlacementGate.GapDenied = true;
                Construction.lastPlacementError = string.Format(ConstructionErrors.TooCloseTo.translated, "planter");
                Construction.lastPlacementErrorIsDetailed = true;
            }

            private static void Finalizer()
            {
                PlacementGate.Pop();
            }
        }

        [AutoPatch(IsRequired = true, Silent = true)]
        [HarmonyPatch(typeof(DeployVolume), "CheckFlags")]
        private static class CheckFlagsPatch
        {
            private static void Prefix(List<Collider> list, DeployVolume volume)
            {
                NightshadePlanterStack plugin = PlacementGate.Current;
                if (plugin == null || list == null || (volume != null && volume.IsBuildingBlock))
                {
                    return;
                }

                for (int i = list.Count - 1; i >= 0; i--)
                {
                    Collider collider = list[i];
                    if (collider == null)
                    {
                        continue;
                    }

                    BaseEntity entity = collider.ToBaseEntity();
                    if (entity != null && plugin.IsConfiguredPlanterEntity(entity))
                    {
                        list.RemoveAt(i);
                    }
                }
            }
        }

        [AutoPatch(IsRequired = true, Silent = true)]
        [HarmonyPatch(typeof(Deployer), "CheckPlacement")]
        private static class DeployerPlacementPatch
        {
            private static void Prefix(Deployer __instance, Deployable deployable)
            {
                NightshadePlanterStack plugin = instance;
                BasePlayer player = __instance != null ? __instance.GetOwnerPlayer() : null;
                bool open = plugin != null
                    && !PlacementGate.Suppress
                    && plugin.ShouldRelaxDeployer(__instance, deployable)
                    && PlanterStackRules.CanOpenGate(plugin.config != null && plugin.config.Enabled, plugin.HasPermission(player), true);
                PlacementGate.Push(open ? plugin : null);
            }

            private static void Postfix(Deployer __instance, Deployable deployable, Ray ray, ref bool __result)
            {
                NightshadePlanterStack plugin = PlacementGate.Current;
                if (plugin == null || !__result)
                {
                    return;
                }

                RaycastHit hit;
                if (!Physics.Raycast(ray, out hit, 8f, Rust.Layers.Deploy, QueryTriggerInteraction.Ignore))
                {
                    return;
                }

                if (!plugin.ViolatesGap(hit.point, hit.GetEntity()))
                {
                    return;
                }

                __result = false;
                PlacementGate.GapDenied = true;
                Construction.lastPlacementError = string.Format(ConstructionErrors.TooCloseTo.translated, "planter");
                Construction.lastPlacementErrorIsDetailed = true;
            }

            private static void Finalizer()
            {
                PlacementGate.Pop();
            }
        }

        [AutoPatch(IsRequired = false, Silent = true)]
        [HarmonyPatch(typeof(Planner), "DoBuild", new System.Type[] { typeof(CreateBuilding) })]
        private static class SwallowPlannerRpcPatch
        {
            private static bool Prefix(Planner __instance, CreateBuilding msg)
            {
                NightshadePlanterStack plugin = instance;
                if (plugin == null || __instance == null || msg == null)
                {
                    return true;
                }

                BasePlayer player = __instance.GetOwnerPlayer();
                if (!plugin.RecentlyPlaced(player))
                {
                    return true;
                }

                Construction construction = PrefabAttribute.server.Find<Construction>(msg.blockID);
                if (!plugin.IsConfiguredConstruction(construction))
                {
                    return true;
                }

                return false;
            }
        }

        [AutoPatch(IsRequired = false, Silent = true)]
        [HarmonyPatch(typeof(Deployer), "DoDeploy")]
        private static class SwallowDeployerRpcPatch
        {
            private static bool Prefix(Deployer __instance)
            {
                NightshadePlanterStack plugin = instance;
                if (plugin == null || __instance == null)
                {
                    return true;
                }

                BasePlayer player = __instance.GetOwnerPlayer();
                if (!plugin.RecentlyPlaced(player))
                {
                    return true;
                }

                Deployable deployable = __instance.GetDeployable();
                if (!plugin.ShouldRelaxDeployer(__instance, deployable))
                {
                    return true;
                }

                return false;
            }
        }
    }
}
#endif
