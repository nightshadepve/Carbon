using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("AutoMilker", "kayotics", "1.5.0")]
    [Description("Custom 'Auto Milker' hopper item — place on any box to auto-milk nearby cows. Also extends the hopper pickup radius.")]
    public class AutoMilker : RustPlugin
    {
        private const string PermUse = "automilker.use";
        private const string PermGive = "automilker.give";
        private const string PermAdmin = "automilker.admin";
        private const string HopperShortName = "hopper";
        private const string MilkShortName = "milk";
        private const string SkimmedMilkShortName = "skimmedmilk";
        private const string CreamShortName = "cream";
        private const string ItemMarker = "§automilker";
        private const string DefaultDisplayName = "Auto Milker";
        private const string DataFileName = "AutoMilker";
        private const ulong WorkshopSkinId = 3818526915UL;
        private const ulong LegacyWorkshopSkinId = 3818514461UL;
        private const ulong LegacyPlaceholderSkinId = 2468100001UL;

        // Shared radius for cow milking and hopper item pickup.
        private const float MinRadius = 1f;
        private const float MaxRadius = 30f;

        // How many Auto Milkers one player may have placed at once.
        private const int MinPerPlayer = 1;
        private const int MaxPerPlayerCap = 10;

        private const float MinFridgeSkimInterval = 60f;

        // Cap work per skim pass so one base cannot stall the server.
        private const int MaxFridgesPerSkimPass = 24;
        private const int MinMilkSkimsPerPass = 1;
        private const int MaxMilkSkimsPerPassCap = 4;
        private const int DefaultMilkSkimsPerPass = 4;

        // Gen2 livestock is not reliably on AI-only; scan all layers.
        private const int AnimalLayerMask = -1;

        // Cow API via reflection — MilkReady is a static BaseEntity.Flags, MilkDef is a field, no IsTamed.
        private static Type _cowType;
        private static FieldInfo _milkReadyFlagField;
        private static FieldInfo _milkDefField;
        private static MethodInfo _milkedAmountMethod;
        private static MethodInfo _setMilkReadyMethod;
        private static MethodInfo _bestFamiliarityMethod;
        private static FieldInfo _milkTimerField;
        private static FieldInfo _milkCooldownField;
        private static PropertyInfo _timerRemainingProp;
        private static MethodInfo _timerStartMethod;
        private static BaseEntity.Flags _milkReadyFlag;
        private static bool _cowApiResolved;

        private Configuration _config;
        private PluginData _data;
        private readonly List<HopperState> _hoppers = new();
        private readonly Dictionary<ulong, float> _pendingDeployByPlayer = new();
        private readonly Dictionary<ulong, float> _pendingPickupByPlayer = new();
        private readonly Dictionary<Hopper, PickupTriggerData> _pickupHoppers = new();
        private Timer _tick;
        // One world fridge-skim per owner per interval (multiple milker hoppers share this).
        private readonly Dictionary<ulong, float> _nextOwnerFridgeSkimAt = new();

        private sealed class HopperState
        {
            public ulong NetId;
            public Hopper Hopper;
            public float NextScanAt;
        }

        private sealed class PickupTriggerData
        {
            public SphereCollider Trigger;
            public float OriginalRadius;
        }

        private class PluginData
        {
            // Keyed by player display name, e.g. "chaotics" -> their hopper net IDs.
            [JsonProperty("Players")]
            public Dictionary<string, PlayerHopperData> Players = new(StringComparer.OrdinalIgnoreCase);

            // Old flat list — migrated on load, then cleared.
            [JsonProperty("AutoMilkerNetIds", NullValueHandling = NullValueHandling.Ignore)]
            public HashSet<ulong> LegacyAutoMilkerNetIds;
        }

        private class PlayerHopperData
        {
            [JsonProperty("Steam ID")]
            public ulong SteamId;

            [JsonProperty("Hoppers")]
            public HashSet<ulong> Hoppers = new();
        }

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermGive, this);
            permission.RegisterPermission(PermAdmin, this);
            AddCovalenceCommand("automilker", nameof(CmdAutoMilkerCov));
            AddCovalenceCommand("automilk", nameof(CmdAutoMilkerCov));
            LoadData();
        }

        private void OnServerInitialized()
        {
            ResolveCowApi();
            RebuildHopperCache();
            _tick?.Destroy();
            _tick = timer.Every(Mathf.Max(0.5f, _config.TickSeconds), TickHoppers);
            timer.Once(2f, () => RefreshPickupRadii(true));
            Puts(
                $"AutoMilker {Version}: '{_config.DisplayName}' skin {_config.SkinId}, " +
                $"{_hoppers.Count} placed, radius {_config.Radius:0.##}m (milk + pickup), " +
                $"fridgeSkim={_config.AutoSkimOwnerFridgeMilk} every {_config.FridgeSkimIntervalSeconds:0.#}s, " +
                $"cowApi={_cowApiResolved}.");
        }

        private void Unload()
        {
            RestoreAllPickupRadii();
            SaveData();
            _tick?.Destroy();
            _tick = null;
            _hoppers.Clear();
            _nextOwnerFridgeSkimAt.Clear();
            _pendingDeployByPlayer.Clear();
            _pendingPickupByPlayer.Clear();
        }

        #endregion

        #region Give / item identity

        private void CmdAutoMilkerCov(IPlayer cov, string command, string[] args)
        {
            var player = cov?.Object as BasePlayer;
            if (player == null)
            {
                cov?.Reply("In-game only. Use: automilker.give <player> [amount]");
                return;
            }

            // /automilk give [n]  → item
            // /automilk skim      → force fridge skim now
            // /automilk           → convert the hopper you are looking at
            if (args != null && args.Length > 0)
            {
                if (args[0].Equals("skim", StringComparison.OrdinalIgnoreCase))
                {
                    HandleForceSkim(player);
                    return;
                }

                if (args[0].Equals("give", StringComparison.OrdinalIgnoreCase)
                    || int.TryParse(args[0], out _))
                {
                    HandleGiveChat(player, args);
                    return;
                }
            }

            HandleActivateLookHopper(player);
        }

        private void HandleForceSkim(BasePlayer player)
        {
            // Force skim bypasses the interval — admin only (not automilker.use).
            if (!permission.UserHasPermission(player.UserIDString, PermAdmin) && !player.IsAdmin)
            {
                player.ChatMessage("No permission.");
                return;
            }

            if (!_config.AutoSkimOwnerFridgeMilk)
            {
                player.ChatMessage("Fridge auto-skim is disabled in AutoMilker.json.");
                return;
            }

            Hopper hopper = null;
            if (TryGetLookHopper(player, out var look) && IsAutoMilkerHopper(look))
                hopper = look;

            if (hopper == null)
            {
                var steamId = GetSteamId(player);
                for (var i = 0; i < _hoppers.Count; i++)
                {
                    var h = _hoppers[i]?.Hopper;
                    if (h == null || h.IsDestroyed || steamId == 0UL || h.OwnerID != steamId)
                        continue;
                    hopper = h;
                    break;
                }
            }

            if (hopper == null)
            {
                player.ChatMessage("No Auto Milker found. Look at one and /automilk, or place an Auto Milker first.");
                return;
            }

            var skimmed = SkimReadyMilkInOwnerFridges(hopper);
            player.ChatMessage(skimmed > 0
                ? $"Skimmed {skimmed} milk."
                : "No ready milk in your fridges right now.");
        }

        private void HandleActivateLookHopper(BasePlayer player)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermUse)
                && !permission.UserHasPermission(player.UserIDString, PermGive)
                && !player.IsAdmin)
            {
                player.ChatMessage("No permission.");
                return;
            }

            if (!TryGetLookHopper(player, out var hopper))
            {
                player.ChatMessage("Look at a hopper on a box, then type /automilk to activate it. Or /automilk give for an Auto Milker item.");
                return;
            }

            var steamId = GetSteamId(player);
            if (hopper.OwnerID == 0)
                hopper.OwnerID = steamId;

            // Converting a normal hopper counts toward this player's limit (admins included).
            if (!IsAutoMilkerHopper(hopper)
                && IsAtAutoMilkerLimit(steamId, out var owned, out var max))
            {
                NotifyLimitReached(player, owned, max);
                return;
            }

            ApplyEntityIdentity(hopper);
            RegisterHopper(hopper);
            NotifyLimitStatus(player, steamId);

            var container = hopper.Container;
            var boxOk = container != null;
            player.ChatMessage(
                boxOk
                    ? $"{_config.DisplayName} activated — milking / pickup within {_config.Radius:0.##}m."
                    : $"{_config.DisplayName} tagged, but this hopper has no box inventory. Mount it on a box.");

            // Kick a scan immediately so you can tell if cows are ready.
            if (boxOk && _cowApiResolved)
            {
                if (!hopper.IsPowered())
                {
                    player.ChatMessage("Hopper needs power for milking and item pickup.");
                }
                else
                {
                    var milked = MilkNearHopper(hopper, container, Mathf.Max(1, _config.MaxMilksPerTick));
                    if (milked > 0)
                        player.ChatMessage($"Milked {milked} cow(s) now.");
                    else
                        player.ChatMessage("No milk-ready cows in range right now (cows need the milk-ready state).");
                }
            }
            else if (!_cowApiResolved)
            {
                player.ChatMessage("Cow milk API not resolved — check server console for AutoMilker warnings.");
            }
        }

        private static bool TryGetLookHopper(BasePlayer player, out Hopper hopper)
        {
            hopper = null;
            if (player?.eyes == null)
                return false;

            var layers = Layers.Mask.Deployed | Layers.Mask.Default | Layers.Mask.World
                         | Layers.Mask.Construction | Layers.Mask.Prevent_Building;
            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 6f, layers, QueryTriggerInteraction.Ignore))
                return false;

            var ent = hit.GetEntity();
            for (var depth = 0; ent != null && depth < 6; depth++)
            {
                if (ent is Hopper h)
                {
                    hopper = h;
                    return true;
                }

                var children = ent.children;
                if (children != null)
                {
                    for (var i = 0; i < children.Count; i++)
                    {
                        if (children[i] is Hopper child)
                        {
                            hopper = child;
                            return true;
                        }
                    }
                }

                ent = ent.GetParentEntity();
            }

            return false;
        }

        private void HandleGiveChat(BasePlayer player, string[] args)
        {
            var amount = 1;
            if (args != null && args.Length > 0)
            {
                if (args[0].Equals("give", StringComparison.OrdinalIgnoreCase))
                {
                    if (args.Length >= 2)
                        int.TryParse(args[1], out amount);
                }
                else
                    int.TryParse(args[0], out amount);
            }

            if (!permission.UserHasPermission(player.UserIDString, PermGive) && !player.IsAdmin)
            {
                player.ChatMessage("No permission.");
                return;
            }

            if (amount < 1)
                amount = 1;
            if (amount > 100)
                amount = 100;

            var item = CreateAutoMilkerItem(amount);
            if (item == null)
            {
                player.ChatMessage("Could not create Auto Milker (hopper item missing).");
                return;
            }

            player.GiveItem(item);
            player.ChatMessage($"Gave {amount}x {_config.DisplayName}. Place on a box, or look at a hopper and type /automilk.");
        }

        [ConsoleCommand("automilker.give")]
        private void CmdGiveConsole(ConsoleSystem.Arg arg)
        {
            var amount = arg.GetInt(1, 1);
            if (amount < 1)
                amount = 1;

            BasePlayer target = null;
            if (arg.Args != null && arg.Args.Length > 0)
                target = BasePlayer.FindAwakeOrSleeping(arg.Args[0].ToString());

            if (target == null)
                target = arg.Player();

            if (target == null)
            {
                arg.ReplyWith("Usage: automilker.give <player> [amount]");
                return;
            }

            if (arg.Player() != null
                && !arg.Player().IsAdmin
                && !permission.UserHasPermission(arg.Player().UserIDString, PermGive))
            {
                arg.ReplyWith("No permission.");
                return;
            }

            var item = CreateAutoMilkerItem(amount);
            if (item == null)
            {
                arg.ReplyWith("Failed to create item.");
                return;
            }

            target.GiveItem(item);
            arg.ReplyWith($"Gave {amount}x {_config.DisplayName} to {target.displayName}.");
        }

        private Item CreateAutoMilkerItem(int amount)
        {
            var item = ItemManager.CreateByName(HopperShortName, amount, _config.SkinId);
            if (item == null)
                return null;

            ApplyItemIdentity(item);
            return item;
        }

        private void ApplyItemIdentity(Item item)
        {
            if (item == null)
                return;

            item.name = _config.DisplayName;
            item.text = ItemMarker;
            if (_config.SkinId != 0UL)
                item.skin = _config.SkinId;
            item.MarkDirty();
        }

        private void ApplyEntityIdentity(Hopper hopper)
        {
            if (hopper == null || hopper.IsDestroyed)
                return;

            if (_config.SkinId != 0UL)
                hopper.skinID = _config.SkinId;

            hopper._name = _config.DisplayName;
            hopper.SendNetworkUpdateImmediate();
        }

        private bool IsAutoMilkerItem(Item item)
        {
            if (item?.info == null)
                return false;

            if (!string.Equals(item.info.shortname, HopperShortName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(item.text)
                && string.Equals(item.text, ItemMarker, StringComparison.Ordinal))
                return true;

            if (!string.IsNullOrEmpty(item.name)
                && string.Equals(item.name.Trim(), _config.DisplayName, StringComparison.OrdinalIgnoreCase))
                return true;

            return IsAutoMilkerSkin(item.skin);
        }

        private bool IsAutoMilkerSkin(ulong skin)
        {
            if (skin == 0UL)
                return false;
            if (_config.SkinId != 0UL && skin == _config.SkinId)
                return true;
            if (skin == WorkshopSkinId || skin == LegacyWorkshopSkinId || skin == LegacyPlaceholderSkinId)
                return true;
            return false;
        }

        private bool IsAutoMilkerHopper(Hopper hopper)
        {
            if (hopper == null || hopper.IsDestroyed || hopper.net == null)
                return false;

            if (DataContainsHopper(hopper.net.ID.Value))
                return true;

            if (IsAutoMilkerSkin(hopper.skinID))
                return true;

            if (!string.IsNullOrEmpty(hopper._name)
                && string.Equals(hopper._name.Trim(), _config.DisplayName, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        #endregion

        #region Deploy / pickup

        private static ulong GetSteamId(BasePlayer player)
        {
            if (player == null)
                return 0UL;
            return (ulong)player.userID;
        }

        // Remember that this player is about to place an Auto Milker (item is consumed before most deploy hooks).
        private object CanDeployItem(BasePlayer player, Deployer deployer, NetworkableId entityId)
        {
            if (player == null)
                return null;

            var item = player.GetActiveItem();
            if (!IsAutoMilkerItem(item))
                return null;

            var steamId = GetSteamId(player);
            if (IsAtAutoMilkerLimit(steamId, out var owned, out var max))
            {
                NotifyLimitReached(player, owned, max);

                // return false normally keeps the item; if this build still consumes it, refund once.
                var refundAmount = Mathf.Max(1, item.amount);
                var itemUid = item.uid;
                NextTick(() =>
                {
                    if (player == null || player.IsDestroyed)
                        return;
                    if (PlayerHasItemUid(player, itemUid))
                        return;
                    EnsureAutoMilkerRefund(player, refundAmount);
                });

                return false;
            }

            _pendingDeployByPlayer[steamId] = Time.realtimeSinceStartup;
            return null;
        }

        private void OnEntitySpawned(Hopper hopper)
        {
            if (hopper == null)
                return;

            NextTick(() => TryClaimDeployedHopper(hopper));
            timer.Once(0.15f, () => TryClaimDeployedHopper(hopper));

            // Extended pickup radius (delay so the hopper's colliders are fully set up).
            timer.Once(0.25f, () =>
            {
                if (hopper == null || hopper.IsDestroyed)
                    return;

                ApplyPickupRadius(hopper);
            });
        }

        private void OnItemDeployed(Deployer deployer, ItemModDeployable modDeployable, BaseEntity entity)
        {
            if (entity is Hopper hopper)
                TryClaimDeployedHopper(hopper, GetDeployerPlayer(deployer));
        }

        private void OnItemDeployed(Deployer deployer, BaseEntity parentEntity, BaseEntity entity)
        {
            if (entity is Hopper hopper)
                TryClaimDeployedHopper(hopper, GetDeployerPlayer(deployer));
        }

        private static BasePlayer GetDeployerPlayer(Deployer deployer)
        {
            if (deployer == null)
                return null;
            return deployer.GetOwnerPlayer() ?? deployer.GetParentEntity() as BasePlayer;
        }

        private void TryClaimDeployedHopper(Hopper hopper, BasePlayer deployerPlayer = null)
        {
            if (hopper == null || hopper.IsDestroyed || hopper.net == null)
                return;

            // Already registered (reload / re-scan) — refresh only, do not re-check limit.
            if (IsTrackedAutoMilker(hopper))
            {
                if (deployerPlayer != null && hopper.OwnerID == 0)
                    hopper.OwnerID = GetSteamId(deployerPlayer);
                ApplyEntityIdentity(hopper);
                RegisterHopper(hopper);
                return;
            }

            var now = Time.realtimeSinceStartup;
            var ownerId = deployerPlayer != null ? GetSteamId(deployerPlayer) : hopper.OwnerID;
            var pending = false;

            if (ownerId != 0
                && _pendingDeployByPlayer.TryGetValue(ownerId, out var t0)
                && now - t0 < 8f)
            {
                pending = true;
                _pendingDeployByPlayer.Remove(ownerId);
            }
            else if (hopper.OwnerID != 0
                     && _pendingDeployByPlayer.TryGetValue(hopper.OwnerID, out var t1)
                     && now - t1 < 8f)
            {
                pending = true;
                ownerId = hopper.OwnerID;
                _pendingDeployByPlayer.Remove(hopper.OwnerID);
            }
            else
            {
                // CanDeployItem may not fire for hoppers — match any recent pending deploy.
                ulong matchedPending = 0UL;
                foreach (var pair in _pendingDeployByPlayer)
                {
                    if (now - pair.Value >= 8f)
                        continue;
                    matchedPending = pair.Key;
                    break;
                }

                if (matchedPending != 0UL)
                {
                    pending = true;
                    if (ownerId == 0)
                        ownerId = matchedPending;
                    _pendingDeployByPlayer.Remove(matchedPending);
                }
            }

            // New Auto Milker from item (skin/name already on entity) or pending deploy claim.
            var isNewAutoMilker = pending || IsAutoMilkerSkin(hopper.skinID)
                || (!string.IsNullOrEmpty(hopper._name)
                    && string.Equals(hopper._name.Trim(), _config.DisplayName, StringComparison.OrdinalIgnoreCase));

            if (!isNewAutoMilker)
                return;

            if (ownerId == 0)
                ownerId = hopper.OwnerID;

            // Always stamp owner before limit check / data write.
            if (ownerId != 0)
                hopper.OwnerID = ownerId;

            // Applies to everyone, including admins — no bypass.
            // Count excludes this hopper (not registered yet).
            if (ownerId != 0 && IsAtAutoMilkerLimit(ownerId, out var owned, out var max))
            {
                if (deployerPlayer == null)
                    deployerPlayer = BasePlayer.FindByID(ownerId) ?? BasePlayer.FindSleeping(ownerId);
                RejectOverLimitPlacement(hopper, deployerPlayer, owned, max);
                return;
            }

            ApplyEntityIdentity(hopper);
            RegisterHopper(hopper);

            if (deployerPlayer == null && ownerId != 0)
                deployerPlayer = BasePlayer.FindByID(ownerId) ?? BasePlayer.FindSleeping(ownerId);
            NotifyLimitStatus(deployerPlayer, ownerId);
        }

        private bool IsTrackedAutoMilker(Hopper hopper)
        {
            if (hopper?.net == null)
                return false;

            var id = hopper.net.ID.Value;
            if (DataContainsHopper(id))
                return true;

            for (var i = 0; i < _hoppers.Count; i++)
            {
                if (_hoppers[i] != null && _hoppers[i].NetId == id)
                    return true;
            }

            return false;
        }

        private static string FormatLimitReachedMessage(string displayName, int owned, int max)
        {
            return $"{displayName} limit reached {owned}/{max}";
        }

        private static string FormatLimitStatusMessage(string displayName, int owned, int max)
        {
            return $"{displayName} limit {owned}/{max}";
        }

        private void NotifyLimitReached(BasePlayer player, int owned, int max)
        {
            if (player == null || player.IsDestroyed)
                return;

            var message = FormatLimitReachedMessage(_config.DisplayName, owned, max);

            try
            {
                player.ShowToast(GameTip.Styles.Error, message, true);
            }
            catch
            {
                player.SendConsoleCommand("gametip.showtoast", 1, message);
            }

            player.ChatMessage(message);
        }

        private void NotifyLimitStatus(BasePlayer player, ulong ownerId)
        {
            if (player == null || player.IsDestroyed || ownerId == 0)
                return;

            player.ChatMessage(
                FormatLimitStatusMessage(_config.DisplayName, CountOwnedAutoMilkers(ownerId), GetMaxPerPlayer()));
        }

        // Kill + refund so the limit cannot be bypassed if CanDeployItem is ignored (e.g. some admin paths).
        private void RejectOverLimitPlacement(Hopper hopper, BasePlayer player, int owned, int max)
        {
            NotifyLimitReached(player, owned, max);

            // Refund immediately (don't wait) so inventory is restored even if kill is delayed.
            EnsureAutoMilkerRefund(player, 1);

            NextTick(() =>
            {
                if (hopper != null && !hopper.IsDestroyed)
                    hopper.Kill();
            });
        }

        // Give back Auto Milker(s) if the deploy attempt consumed them.
        private void EnsureAutoMilkerRefund(BasePlayer player, int amount)
        {
            if (player == null || player.IsDestroyed || amount < 1)
                return;

            var refund = CreateAutoMilkerItem(amount);
            if (refund == null)
                return;

            player.GiveItem(refund);
        }

        private static bool PlayerHasItemUid(BasePlayer player, ItemId uid)
        {
            if (player?.inventory == null)
                return false;

            foreach (var container in new[]
                     {
                         player.inventory.containerMain,
                         player.inventory.containerBelt,
                         player.inventory.containerWear
                     })
            {
                if (container?.itemList == null)
                    continue;

                for (var i = 0; i < container.itemList.Count; i++)
                {
                    var item = container.itemList[i];
                    if (item != null && item.uid == uid)
                        return true;
                }
            }

            return false;
        }

        private void OnItemAddedToContainer(ItemContainer container, Item item)
        {
            if (item?.info == null
                || !string.Equals(item.info.shortname, HopperShortName, StringComparison.OrdinalIgnoreCase))
                return;

            // Pending pickup rename (skin may have been stripped by vanilla).
            if (container?.playerOwner != null
                && _pendingPickupByPlayer.TryGetValue(container.playerOwner.userID, out var at)
                && Time.realtimeSinceStartup - at < 5f)
            {
                ApplyItemIdentity(item);
                return;
            }

            if (IsAutoMilkerItem(item))
                ApplyItemIdentity(item);
        }

        private void OnEntityKill(Hopper hopper)
        {
            if (hopper == null)
                return;

            _pickupHoppers.Remove(hopper);

            if (hopper.net == null)
                return;

            var id = hopper.net.ID.Value;
            RemoveCached(id);
            if (RemoveHopperFromData(id))
                SaveData();
        }

        private object CanPickupEntity(BasePlayer player, Hopper hopper)
        {
            if (player == null || !IsAutoMilkerHopper(hopper))
                return null;

            _pendingPickupByPlayer[player.userID] = Time.realtimeSinceStartup;
            return null;
        }

        private object OnPickupEntity(BasePlayer player, Hopper hopper)
        {
            if (player == null || !IsAutoMilkerHopper(hopper))
                return null;

            _pendingPickupByPlayer[player.userID] = Time.realtimeSinceStartup;
            NextTick(() => TryRenameRecentHopperPickup(player));
            timer.Once(0.2f, () => TryRenameRecentHopperPickup(player));
            return null;
        }

        private void TryRenameRecentHopperPickup(BasePlayer player)
        {
            if (player?.inventory == null)
                return;

            var force = _pendingPickupByPlayer.TryGetValue(player.userID, out var at)
                        && Time.realtimeSinceStartup - at < 5f;

            foreach (var container in new[]
                     {
                         player.inventory.containerMain,
                         player.inventory.containerBelt
                     })
            {
                if (container?.itemList == null)
                    continue;

                for (var i = 0; i < container.itemList.Count; i++)
                {
                    var item = container.itemList[i];
                    if (item?.info == null)
                        continue;
                    if (!string.Equals(item.info.shortname, HopperShortName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!force
                        && _config.SkinId != 0UL && item.skin != _config.SkinId
                        && !string.Equals(item.name, _config.DisplayName, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(item.text, ItemMarker, StringComparison.Ordinal))
                        continue;

                    ApplyItemIdentity(item);
                }
            }

            if (force)
                _pendingPickupByPlayer.Remove(player.userID);
        }

        #endregion

        #region Cache

        private void RebuildHopperCache()
        {
            _hoppers.Clear();
            MigrateLegacyNetIds();

            foreach (var networkable in BaseNetworkable.serverEntities)
            {
                var hopper = networkable as Hopper;
                if (hopper == null || hopper.IsDestroyed || hopper.net == null)
                    continue;

                if (!IsAutoMilkerHopper(hopper))
                    continue;

                ApplyEntityIdentity(hopper);
                AddCached(hopper);
                SyncHopperToPlayerData(hopper);
            }

            PruneMissingHoppersFromData();
            SaveData();
        }

        private int GetMaxPerPlayer()
        {
            return Mathf.Clamp(_config.MaxPerPlayer, MinPerPlayer, MaxPerPlayerCap);
        }

        private int CountOwnedAutoMilkers(ulong ownerId)
        {
            if (ownerId == 0)
                return 0;

            var seen = new HashSet<ulong>();

            // Live cache by OwnerID (primary — reliable even if data is stale).
            for (var i = 0; i < _hoppers.Count; i++)
            {
                var hopper = _hoppers[i]?.Hopper;
                if (hopper == null || hopper.IsDestroyed || hopper.net == null)
                    continue;
                if (hopper.OwnerID == ownerId)
                    seen.Add(hopper.net.ID.Value);
            }

            // Data file entries for this Steam ID.
            var entry = FindPlayerDataBySteamId(ownerId);
            if (entry?.Hoppers != null)
            {
                foreach (var netId in entry.Hoppers)
                {
                    if (seen.Contains(netId))
                        continue;

                    if (FindLiveHopper(netId) != null)
                    {
                        seen.Add(netId);
                        continue;
                    }

                    for (var i = 0; i < _hoppers.Count; i++)
                    {
                        if (_hoppers[i] != null && _hoppers[i].NetId == netId)
                        {
                            seen.Add(netId);
                            break;
                        }
                    }
                }
            }

            return seen.Count;
        }

        private bool IsAtAutoMilkerLimit(ulong ownerId, out int owned, out int max)
        {
            max = GetMaxPerPlayer();
            owned = CountOwnedAutoMilkers(ownerId);
            return ownerId != 0 && owned >= max;
        }

        private void RegisterHopper(Hopper hopper)
        {
            if (hopper?.net == null || hopper.IsDestroyed)
                return;

            ApplyEntityIdentity(hopper);
            AddCached(hopper);

            var id = hopper.net.ID.Value;
            if (SyncHopperToPlayerData(hopper))
                SaveData();

            // Matters when the pickup radius is limited to Auto Milker hoppers.
            ApplyPickupRadius(hopper);

            if (_config.DebugLog)
                Puts($"Registered Auto Milker hopper {id} owner={hopper.OwnerID}");
        }

        private void AddCached(Hopper hopper)
        {
            if (hopper?.net == null)
                return;

            var id = hopper.net.ID.Value;
            for (var i = 0; i < _hoppers.Count; i++)
            {
                if (_hoppers[i].NetId == id)
                {
                    _hoppers[i].Hopper = hopper;
                    return;
                }
            }

            _hoppers.Add(new HopperState
            {
                NetId = id,
                Hopper = hopper,
                NextScanAt = 0f,
            });
        }

        private void RemoveCached(ulong netId)
        {
            for (var i = _hoppers.Count - 1; i >= 0; i--)
            {
                if (_hoppers[i].NetId == netId)
                    _hoppers.RemoveAt(i);
            }
        }

        #endregion

        #region Tick

        private void TickHoppers()
        {
            var count = _hoppers.Count;
            if (count == 0)
                return;

            var now = Time.realtimeSinceStartup;
            var milksLeft = Mathf.Max(1, _config.MaxMilksPerTick);

            for (var i = count - 1; i >= 0; i--)
            {
                if (i >= _hoppers.Count)
                    continue;

                var state = _hoppers[i];
                if (state == null)
                    continue;

                var hopper = state.Hopper;
                if (hopper == null || hopper.IsDestroyed || !IsAutoMilkerHopper(hopper))
                {
                    RemoveCached(state.NetId);
                    continue;
                }

                // Vanilla electricity required — same as hopper suction/lights.
                var powered = hopper.IsPowered();
                var container = powered ? hopper.Container : null;

                // Fridge skim is per owner (not per hopper) so multi-milker bases scan once.
                if (_config.AutoSkimOwnerFridgeMilk)
                    TrySkimOwnerFridges(hopper, now);

                if (now < state.NextScanAt)
                    continue;

                state.NextScanAt = now + Mathf.Max(1f, _config.HopperScanIntervalSeconds);

                if (!powered || container == null || !HasOutputSpace(container))
                    continue;

                milksLeft -= MilkNearHopper(hopper, container, milksLeft);
                if (milksLeft <= 0)
                    return;
            }
        }

        private float GetFridgeSkimInterval()
        {
            return Mathf.Max(MinFridgeSkimInterval, _config.FridgeSkimIntervalSeconds);
        }

        private int GetMilkSkimsPerPass()
        {
            return Mathf.Clamp(_config.FridgeSkimsPerInterval, MinMilkSkimsPerPass, MaxMilkSkimsPerPassCap);
        }

        private void TrySkimOwnerFridges(Hopper hopper, float now)
        {
            if (hopper == null || hopper.IsDestroyed)
                return;

            // Prefer owner id; fall back to hopper net id when OwnerID is unset.
            var key = hopper.OwnerID != 0UL ? hopper.OwnerID : hopper.net?.ID.Value ?? 0UL;
            if (key == 0UL)
                return;

            if (_nextOwnerFridgeSkimAt.TryGetValue(key, out var nextAt) && now < nextAt)
                return;

            _nextOwnerFridgeSkimAt[key] = now + GetFridgeSkimInterval();
            SkimReadyMilkInOwnerFridges(hopper);
        }

        /// <summary>
        /// Skim ready milk in place inside owner (or same-TC) fridges.
        /// World entity walk, but fridge-typed entities only — not every storage box.
        /// </summary>
        private int SkimReadyMilkInOwnerFridges(Hopper hopper)
        {
            if (hopper == null || hopper.IsDestroyed)
                return 0;

            var ownerId = hopper.OwnerID;
            var privilege = hopper.GetBuildingPrivilege();
            var privilegeId = privilege != null && !privilege.IsDestroyed && privilege.net != null
                ? privilege.net.ID.Value
                : 0UL;
            if (ownerId == 0UL && privilegeId == 0UL)
                return 0;

            var milkDef = ItemManager.FindItemDefinition(MilkShortName);
            if (milkDef == null)
                return 0;

            var maxSkims = GetMilkSkimsPerPass();
            var skimmed = 0;
            var fridgesChecked = 0;

            foreach (var networkable in BaseNetworkable.serverEntities)
            {
                if (skimmed >= maxSkims || fridgesChecked >= MaxFridgesPerSkimPass)
                    break;

                // Fast reject: only Fridge entities (includes mini fridge).
                var fridge = networkable as Fridge;
                if (fridge == null || fridge.IsDestroyed)
                    continue;

                if (!IsOwnerOrSameTc(fridge, ownerId, privilegeId))
                    continue;

                fridgesChecked++;

                var container = fridge.inventory;
                if (container?.itemList == null || container.itemList.Count == 0)
                    continue;

                skimmed += SkimReadyMilkInContainer(container, milkDef, maxSkims - skimmed);
            }

            if (_config.DebugLog)
            {
                Puts(
                    $"Fridge skim owner={ownerId} fridges={fridgesChecked} skimmed={skimmed}/{maxSkims}");
            }

            return skimmed;
        }

        private static bool IsOwnerOrSameTc(BaseEntity entity, ulong ownerId, ulong privilegeId)
        {
            if (ownerId != 0UL && entity.OwnerID == ownerId)
                return true;

            if (privilegeId == 0UL)
                return false;

            // Expensive — only when owner does not match (teammate-placed fridge).
            var fridgePriv = entity.GetBuildingPrivilege();
            return fridgePriv != null && fridgePriv.net != null
                && fridgePriv.net.ID.Value == privilegeId;
        }

        private int SkimReadyMilkInContainer(ItemContainer container, ItemDefinition milkDef, int maxSkims)
        {
            if (container?.itemList == null || milkDef == null || maxSkims <= 0)
                return 0;

            var skimmed = 0;
            var list = container.itemList;

            // Walk backwards — UseItem mutates itemList.
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (skimmed >= maxSkims)
                    break;

                var item = list[i];
                if (item?.info == null || item.amount <= 0 || item.info != milkDef)
                    continue;
                if (!ItemModConditionRefrigeratedTime.IsReady(item))
                    continue;

                var canSkim = Mathf.Min(item.amount, maxSkims - skimmed);
                for (var n = 0; n < canSkim; n++)
                {
                    if (item.amount <= 0 || !ItemModConditionRefrigeratedTime.IsReady(item))
                        break;
                    if (!TryManualSkimMilkInPlace(item))
                        break;
                    skimmed++;
                }
            }

            return skimmed;
        }

        private bool TryManualSkimMilkInPlace(Item milk)
        {
            var parent = milk?.parent;
            if (parent == null || milk.amount <= 0)
                return false;

            var skimmedDef = ItemManager.FindItemDefinition(SkimmedMilkShortName);
            var creamDef = ItemManager.FindItemDefinition(CreamShortName);
            if (skimmedDef == null || creamDef == null)
                return false;

            // One milk → one skimmed milk + one cream (vanilla skim).
            milk.UseItem(1);

            var skimmedItem = ItemManager.Create(skimmedDef, 1);
            var creamItem = ItemManager.Create(creamDef, 1);
            if (skimmedItem == null || creamItem == null)
            {
                skimmedItem?.Remove();
                creamItem?.Remove();
                return false;
            }

            if (!skimmedItem.MoveToContainer(parent))
            {
                if (parent.entityOwner != null)
                    skimmedItem.Drop(parent.entityOwner.transform.position + Vector3.up, Vector3.zero);
                else
                    skimmedItem.Remove();
            }

            if (!creamItem.MoveToContainer(parent))
            {
                if (parent.entityOwner != null)
                    creamItem.Drop(parent.entityOwner.transform.position + Vector3.up, Vector3.zero);
                else
                    creamItem.Remove();
            }

            return true;
        }

        private int MilkNearHopper(Hopper hopper, ItemContainer container, int milksLeft)
        {
            if (!_cowApiResolved)
                return 0;

            var origin = hopper.transform.position;
            var radius = _config.Radius;
            var radiusSq = radius * radius;
            var ownerId = hopper.OwnerID;
            var milked = 0;

            var entities = Facepunch.Pool.Get<List<BaseEntity>>();
            try
            {
                Vis.Entities(origin, radius, entities, AnimalLayerMask);

                for (var i = 0; i < entities.Count && milked < milksLeft; i++)
                {
                    var entity = entities[i];
                    if (entity == null || entity.IsDestroyed || !IsCowEntity(entity))
                        continue;

                    var offset = entity.transform.position - origin;
                    if (offset.sqrMagnitude > radiusSq)
                        continue;

                    if (_config.OnlyOwnedOrTamed && !IsEligibleCow(entity, ownerId))
                        continue;

                    if (!IsMilkReady(entity))
                        continue;

                    if (!TryDepositMilk(entity, container))
                        continue;

                    milked++;
                }
            }
            finally
            {
                Facepunch.Pool.FreeUnmanaged(ref entities);
            }

            return milked;
        }

        #endregion

        #region Milk

        private void ResolveCowApi()
        {
            _cowApiResolved = false;
            _cowType = null;
            _milkReadyFlagField = null;
            _milkDefField = null;
            _milkedAmountMethod = null;
            _setMilkReadyMethod = null;
            _bestFamiliarityMethod = null;
            _milkTimerField = null;
            _milkCooldownField = null;
            _timerRemainingProp = null;
            _timerStartMethod = null;
            _milkReadyFlag = 0;

            _cowType = FindType("Rust.Ai.Gen2.Cow") ?? FindType("Cow");
            if (_cowType == null)
            {
                PrintWarning("Cow type not found — Auto Milker will idle until livestock types are available.");
                return;
            }

            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            // MilkReady is a static BaseEntity.Flags field (not a bool property).
            _milkReadyFlagField = _cowType.GetField("MilkReady", stat);
            if (_milkReadyFlagField != null)
                _milkReadyFlag = (BaseEntity.Flags)_milkReadyFlagField.GetValue(null);

            _milkDefField = FindMemberField(_cowType, "MilkDef");
            _milkCooldownField = FindMemberField(_cowType, "MilkCooldown");
            _milkedAmountMethod = _cowType.GetMethod("MilkedAmount", inst, null, Type.EmptyTypes, null);
            _setMilkReadyMethod = _cowType.GetMethod("SetMilkReady", inst, null, new[] { typeof(bool) }, null);
            _bestFamiliarityMethod = FindMemberMethod(_cowType, "BestFamiliarity", new[] { typeof(ulong) });
            _milkTimerField = FindMemberField(_cowType, "milkTimer");
            if (_milkTimerField != null)
            {
                var timerType = _milkTimerField.FieldType;
                _timerRemainingProp = timerType.GetProperty("Remaining", inst);
                _timerStartMethod = timerType.GetMethod("Start", inst, null, new[] { typeof(float) }, null);
            }

            _cowApiResolved = _milkReadyFlagField != null && _milkDefField != null && _milkedAmountMethod != null;
            if (!_cowApiResolved)
                PrintWarning("Cow milk API incomplete — MilkReady flag / MilkDef / MilkedAmount missing.");
        }

        private static Type FindType(string fullName)
        {
            var direct = Type.GetType(fullName);
            if (direct != null)
                return direct;

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    var type = assemblies[i].GetType(fullName, false);
                    if (type != null)
                        return type;
                }
                catch
                {
                    // ignored
                }
            }

            return null;
        }

        private static FieldInfo FindMemberField(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            while (type != null)
            {
                var field = type.GetField(name, flags);
                if (field != null)
                    return field;
                type = type.BaseType;
            }

            return null;
        }

        private static MethodInfo FindMemberMethod(Type type, string name, Type[] args)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            while (type != null)
            {
                var method = type.GetMethod(name, flags, null, args, null);
                if (method != null)
                    return method;
                type = type.BaseType;
            }

            return null;
        }

        private bool IsCowEntity(BaseEntity entity)
        {
            if (entity == null || _cowType == null)
                return false;

            return _cowType.IsInstanceOfType(entity);
        }

        private bool IsEligibleCow(BaseEntity cow, ulong hopperOwnerId)
        {
            if (!_config.OnlyOwnedOrTamed)
                return true;

            if (hopperOwnerId == 0)
                return true;

            if (cow.OwnerID == hopperOwnerId)
                return true;

            // Livestock uses familiarity, not IsTamed.
            if (_bestFamiliarityMethod == null)
                return true;

            try
            {
                var raw = _bestFamiliarityMethod.Invoke(cow, new object[] { hopperOwnerId });
                return raw is float fam && fam > 0f;
            }
            catch
            {
                return true;
            }
        }

        private static bool IsMilkReady(BaseEntity cow)
        {
            try
            {
                return cow.HasFlag(_milkReadyFlag);
            }
            catch
            {
                return false;
            }
        }

        private bool TryDepositMilk(BaseEntity cow, ItemContainer dest)
        {
            ItemDefinition def;
            int amount;
            try
            {
                def = _milkDefField?.GetValue(cow) as ItemDefinition;
                var raw = _milkedAmountMethod?.Invoke(cow, null);
                amount = raw is int i ? i : 0;
            }
            catch
            {
                return false;
            }

            if (def == null || amount <= 0)
                return false;

            var item = ItemManager.Create(def, amount);
            if (item == null)
                return false;

            if (dest == null || !item.MoveToContainer(dest))
            {
                item.Remove();
                return false;
            }

            RestartMilkCooldown(cow);
            return true;
        }

        private void RestartMilkCooldown(BaseEntity cow)
        {
            try
            {
                _setMilkReadyMethod?.Invoke(cow, new object[] { false });

                if (_milkTimerField == null)
                    return;

                var milkTimer = _milkTimerField.GetValue(cow);
                if (milkTimer == null)
                    return;

                // Always use the cow's own milk cooldown (vanilla livestock timing).
                var duration = 600f;
                if (_milkCooldownField?.GetValue(cow) is float cd && cd > 1f)
                    duration = cd;
                else if (_timerRemainingProp?.GetValue(milkTimer) is float remaining && remaining > 1f)
                    duration = remaining;

                _timerStartMethod?.Invoke(milkTimer, new object[] { duration });
            }
            catch (Exception ex)
            {
                if (_config.DebugLog)
                    PrintWarning($"Milk timer reset failed: {ex.Message}");
            }
        }

        private static bool HasOutputSpace(ItemContainer container)
        {
            if (container == null)
                return false;

            if (!container.IsFull())
                return true;

            var milk = ItemManager.FindItemDefinition("milk");
            if (milk == null)
                return false;

            var list = container.itemList;
            for (var i = 0; i < list.Count; i++)
            {
                var item = list[i];
                if (item != null && item.info == milk && item.amount < item.MaxStackable())
                    return true;
            }

            return false;
        }

        #endregion

        #region Pickup radius

        private bool IsPickupRadiusEligible(Hopper hopper)
        {
            return !_config.PickupRadiusAutoMilkerOnly || IsAutoMilkerHopper(hopper);
        }

        private void RefreshPickupRadii(bool log)
        {
            var count = 0;

            foreach (var networkable in BaseNetworkable.serverEntities)
            {
                var hopper = networkable as Hopper;
                if (hopper == null || hopper.IsDestroyed)
                    continue;

                if (ApplyPickupRadius(hopper))
                    count++;
            }

            if (log)
                Puts($"Applied extended pickup radius to {count} hopper(s).");
        }

        private bool ApplyPickupRadius(Hopper hopper)
        {
            if (hopper == null || hopper.IsDestroyed)
                return false;

            // Hoppers that no longer qualify (e.g. setting changed) get their original radius back.
            if (!IsPickupRadiusEligible(hopper))
            {
                RestorePickupRadius(hopper);
                return false;
            }

            PickupTriggerData data;

            if (!_pickupHoppers.TryGetValue(hopper, out data))
            {
                var trigger = FindPickupTrigger(hopper);

                if (trigger == null)
                {
                    PrintWarning(
                        "Could not find a pickup trigger on hopper at " +
                        hopper.transform.position + ".");
                    return false;
                }

                data = new PickupTriggerData
                {
                    Trigger = trigger,
                    OriginalRadius = trigger.radius
                };

                _pickupHoppers[hopper] = data;
            }

            return UpdatePickupTrigger(hopper, data);
        }

        private bool UpdatePickupTrigger(Hopper hopper, PickupTriggerData data)
        {
            if (data.Trigger == null)
            {
                _pickupHoppers.Remove(hopper);
                return false;
            }

            // SphereCollider.radius is local space; divide by the largest lossy scale axis for a true world radius.
            var scale = GetMaxScale(data.Trigger.transform);

            data.Trigger.radius = _config.Radius / scale;
            data.Trigger.enabled = true;

            return true;
        }

        private static float GetMaxScale(Transform t)
        {
            var s = t.lossyScale;
            var max = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));

            return max < 0.0001f ? 1f : max;
        }

        // Only trigger spheres are considered, so the hopper's solid hitbox is never resized.
        private static SphereCollider FindPickupTrigger(Hopper hopper)
        {
            var colliders = hopper.GetComponentsInChildren<SphereCollider>(true);

            if (colliders == null)
                return null;

            foreach (var collider in colliders)
            {
                if (collider != null && collider.isTrigger)
                    return collider;
            }

            return null;
        }

        private void RestorePickupRadius(Hopper hopper)
        {
            PickupTriggerData data;

            if (hopper == null || !_pickupHoppers.TryGetValue(hopper, out data))
                return;

            if (data.Trigger != null)
            {
                data.Trigger.radius = data.OriginalRadius;
                data.Trigger.enabled = true;
            }

            _pickupHoppers.Remove(hopper);
        }

        private void RestoreAllPickupRadii()
        {
            foreach (var pair in _pickupHoppers)
            {
                var hopper = pair.Key;
                var data = pair.Value;

                if (hopper == null || hopper.IsDestroyed)
                    continue;

                if (data.Trigger != null)
                {
                    data.Trigger.radius = data.OriginalRadius;
                    data.Trigger.enabled = true;
                }
            }

            _pickupHoppers.Clear();
        }

        private static bool TryParseRadius(string input, out float radius)
        {
            if (!float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out radius)
                || float.IsNaN(radius)
                || float.IsInfinity(radius))
            {
                return false;
            }

            radius = Mathf.Clamp(radius, MinRadius, MaxRadius);
            return true;
        }

        private void SetRadius(float radius)
        {
            _config.Radius = radius;
            SaveConfig();
            RefreshPickupRadii(false);
        }

        [ChatCommand("hopperradius")]
        private void HopperRadiusCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null)
                return;

            if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
            {
                SendReply(player, "You do not have permission to use this command.");
                return;
            }

            if (args == null || args.Length != 1)
            {
                SendReply(
                    player,
                    $"Current radius (milking + hopper pickup): {_config.Radius:0.##}m\n" +
                    $"Usage: /hopperradius <{MinRadius:0.##}-{MaxRadius:0.##} meters>");
                return;
            }

            if (!TryParseRadius(args[0], out var radius))
            {
                SendReply(player, "Invalid radius.");
                return;
            }

            SetRadius(radius);

            SendReply(player, $"Milking + hopper pickup radius changed to {radius:0.##} meters.");
        }

        [ConsoleCommand("hopperradius.set")]
        private void HopperRadiusConsoleCommand(ConsoleSystem.Arg arg)
        {
            if (arg == null)
                return;

            // Server console/RCON admins, or players holding the permission.
            var allowed = arg.IsAdmin;
            var player = arg.Player();

            if (!allowed && player != null)
                allowed = permission.UserHasPermission(player.UserIDString, PermAdmin);

            if (!allowed)
            {
                arg.ReplyWith("You do not have permission to use this command.");
                return;
            }

            if (arg.Args == null || arg.Args.Length != 1)
            {
                arg.ReplyWith(
                    $"Current radius (milking + hopper pickup): {_config.Radius:0.##}m\n" +
                    $"Usage: hopperradius.set <{MinRadius:0.##}-{MaxRadius:0.##} meters>");
                return;
            }

            if (!TryParseRadius(arg.GetString(0), out var radius))
            {
                arg.ReplyWith("Invalid radius.");
                return;
            }

            SetRadius(radius);

            arg.ReplyWith($"Milking + hopper pickup radius changed to {radius:0.##} meters.");
        }

        #endregion

        #region Data

        private void LoadData()
        {
            try
            {
                _data = Interface.Oxide.DataFileSystem.ReadObject<PluginData>(DataFileName) ?? new PluginData();
            }
            catch
            {
                _data = new PluginData();
            }

            if (_data.Players == null)
                _data.Players = new Dictionary<string, PlayerHopperData>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in _data.Players)
            {
                if (pair.Value == null)
                    continue;
                if (pair.Value.Hoppers == null)
                    pair.Value.Hoppers = new HashSet<ulong>();
            }
        }

        private void MigrateLegacyNetIds()
        {
            if (_data?.LegacyAutoMilkerNetIds == null || _data.LegacyAutoMilkerNetIds.Count == 0)
                return;

            foreach (var netId in _data.LegacyAutoMilkerNetIds)
            {
                if (DataContainsHopper(netId))
                    continue;

                var hopper = FindLiveHopper(netId);
                var steamId = hopper != null ? hopper.OwnerID : 0UL;
                var name = ResolvePlayerName(steamId);
                AddHopperToPlayerData(steamId, name, netId);
            }

            _data.LegacyAutoMilkerNetIds = null;
            Puts("Migrated legacy AutoMilker net IDs into per-player data.");
        }

        private void SaveData()
        {
            if (_data == null)
                return;

            Interface.Oxide.DataFileSystem.WriteObject(DataFileName, _data);
        }

        private bool DataContainsHopper(ulong netId)
        {
            if (_data?.Players == null)
                return false;

            foreach (var pair in _data.Players)
            {
                if (pair.Value?.Hoppers != null && pair.Value.Hoppers.Contains(netId))
                    return true;
            }

            return false;
        }

        private PlayerHopperData FindPlayerDataBySteamId(ulong steamId)
        {
            if (steamId == 0 || _data?.Players == null)
                return null;

            foreach (var pair in _data.Players)
            {
                if (pair.Value != null && pair.Value.SteamId == steamId)
                    return pair.Value;
            }

            return null;
        }

        private string FindPlayerDataKeyBySteamId(ulong steamId)
        {
            if (steamId == 0 || _data?.Players == null)
                return null;

            foreach (var pair in _data.Players)
            {
                if (pair.Value != null && pair.Value.SteamId == steamId)
                    return pair.Key;
            }

            return null;
        }

        private static Hopper FindLiveHopper(ulong netId)
        {
            foreach (var networkable in BaseNetworkable.serverEntities)
            {
                if (networkable is Hopper hopper
                    && hopper.net != null
                    && hopper.net.ID.Value == netId
                    && !hopper.IsDestroyed)
                    return hopper;
            }

            return null;
        }

        private string ResolvePlayerName(ulong steamId)
        {
            if (steamId == 0)
                return "unknown";

            var player = BasePlayer.FindByID(steamId) ?? BasePlayer.FindSleeping(steamId);
            if (player != null && !string.IsNullOrWhiteSpace(player.displayName))
                return player.displayName.Trim();

            var cov = covalence.Players.FindPlayerById(steamId.ToString());
            if (cov != null && !string.IsNullOrWhiteSpace(cov.Name))
                return cov.Name.Trim();

            return steamId.ToString();
        }

        private static string SanitizePlayerKey(string name, ulong steamId)
        {
            if (string.IsNullOrWhiteSpace(name))
                return steamId != 0 ? steamId.ToString() : "unknown";

            return name.Trim();
        }

        // Returns true if the data file changed.
        private bool SyncHopperToPlayerData(Hopper hopper)
        {
            if (hopper?.net == null || _data == null)
                return false;

            var netId = hopper.net.ID.Value;
            var steamId = hopper.OwnerID;
            var name = ResolvePlayerName(steamId);
            return AddHopperToPlayerData(steamId, name, netId);
        }

        private bool AddHopperToPlayerData(ulong steamId, string playerName, ulong netId)
        {
            if (_data == null || netId == 0)
                return false;

            if (_data.Players == null)
                _data.Players = new Dictionary<string, PlayerHopperData>(StringComparer.OrdinalIgnoreCase);

            var existingKey = FindPlayerDataKeyBySteamId(steamId);
            var wantedKey = SanitizePlayerKey(playerName, steamId);

            PlayerHopperData entry;
            if (existingKey != null)
            {
                entry = _data.Players[existingKey];
                if (!string.Equals(existingKey, wantedKey, StringComparison.OrdinalIgnoreCase))
                {
                    // Rename key when display name changes; avoid clobbering another player.
                    if (!_data.Players.ContainsKey(wantedKey)
                        || (_data.Players[wantedKey] != null && _data.Players[wantedKey].SteamId == steamId))
                    {
                        _data.Players.Remove(existingKey);
                        _data.Players[wantedKey] = entry;
                    }
                    else
                    {
                        wantedKey = $"{wantedKey}_{steamId}";
                        _data.Players.Remove(existingKey);
                        _data.Players[wantedKey] = entry;
                    }
                }
            }
            else
            {
                if (_data.Players.TryGetValue(wantedKey, out var conflict)
                    && conflict != null
                    && conflict.SteamId != 0
                    && conflict.SteamId != steamId)
                {
                    wantedKey = steamId != 0 ? $"{wantedKey}_{steamId}" : $"{wantedKey}_unknown";
                }

                entry = new PlayerHopperData { SteamId = steamId, Hoppers = new HashSet<ulong>() };
                _data.Players[wantedKey] = entry;
            }

            if (steamId != 0)
                entry.SteamId = steamId;
            if (entry.Hoppers == null)
                entry.Hoppers = new HashSet<ulong>();

            // Hopper may have been under a different player — move it.
            var moved = RemoveHopperFromData(netId, exceptSteamId: steamId);
            var added = entry.Hoppers.Add(netId);
            return moved || added;
        }

        private bool RemoveHopperFromData(ulong netId, ulong exceptSteamId = 0)
        {
            if (_data?.Players == null || netId == 0)
                return false;

            var changed = false;
            var emptyKeys = new List<string>();

            foreach (var pair in _data.Players)
            {
                var entry = pair.Value;
                if (entry?.Hoppers == null)
                    continue;
                if (exceptSteamId != 0 && entry.SteamId == exceptSteamId)
                    continue;

                if (entry.Hoppers.Remove(netId))
                    changed = true;

                if (entry.Hoppers.Count == 0)
                    emptyKeys.Add(pair.Key);
            }

            for (var i = 0; i < emptyKeys.Count; i++)
                _data.Players.Remove(emptyKeys[i]);

            return changed;
        }

        private void PruneMissingHoppersFromData()
        {
            if (_data?.Players == null)
                return;

            var emptyKeys = new List<string>();

            foreach (var pair in _data.Players)
            {
                var entry = pair.Value;
                if (entry?.Hoppers == null)
                {
                    emptyKeys.Add(pair.Key);
                    continue;
                }

                entry.Hoppers.RemoveWhere(id => FindLiveHopper(id) == null);

                if (entry.Hoppers.Count == 0)
                    emptyKeys.Add(pair.Key);
            }

            for (var i = 0; i < emptyKeys.Count; i++)
                _data.Players.Remove(emptyKeys[i]);
        }

        #endregion

        #region Config

        private class Configuration
        {
            [JsonProperty("Display name")]
            public string DisplayName = DefaultDisplayName;

            [JsonProperty("Skin ID (change this to use a different Workshop skin in inventory)")]
            public ulong SkinId = WorkshopSkinId;

            [JsonProperty("Hopper radius (meters, world space, 1-30 — milking and item pickup)")]
            public float Radius = 10f;

            [JsonProperty("Plugin tick seconds")]
            public float TickSeconds = 5f;

            [JsonProperty("Per-hopper scan interval seconds")]
            public float HopperScanIntervalSeconds = 20f;

            [JsonProperty("Max cows milked per tick (global)")]
            public int MaxMilksPerTick = 4;

            [JsonProperty("Max Auto Milkers per player (1-10)")]
            public int MaxPerPlayer = 1;

            [JsonProperty("Only milk owned cows or cows familiar with the hopper owner")]
            public bool OnlyOwnedOrTamed = false;

            [JsonProperty("Apply hopper radius to Auto Milker hoppers only (false = all hoppers for item pickup)")]
            public bool PickupRadiusAutoMilkerOnly = true;

            // Keep this key string stable so existing configs keep their true/false value.
            [JsonProperty("Auto-skim refrigerated milk in hopper owner fridges under the same TC (disabled by default)")]
            public bool AutoSkimOwnerFridgeMilk = false;

            [JsonProperty("Fridge skim interval seconds (minimum 60)")]
            public float FridgeSkimIntervalSeconds = 60f;

            [JsonProperty("Max milk skims per interval (1-4)")]
            public int FridgeSkimsPerInterval = DefaultMilkSkimsPerPass;

            [JsonProperty("Debug log")]
            public bool DebugLog = false;
        }

        protected override void LoadDefaultConfig() => _config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>() ?? new Configuration();
            }
            catch
            {
                PrintWarning("Invalid config — defaults.");
                _config = new Configuration();
            }

            if (string.IsNullOrWhiteSpace(_config.DisplayName))
                _config.DisplayName = DefaultDisplayName;

            // Migrate known old defaults to the current Workshop skin; leave custom IDs alone.
            if (_config.SkinId == 0UL
                || _config.SkinId == LegacyPlaceholderSkinId
                || _config.SkinId == LegacyWorkshopSkinId)
                _config.SkinId = WorkshopSkinId;

            if (float.IsNaN(_config.Radius) || float.IsInfinity(_config.Radius) || _config.Radius <= 0f)
                _config.Radius = new Configuration().Radius;

            _config.Radius = Mathf.Clamp(_config.Radius, MinRadius, MaxRadius);
            _config.MaxPerPlayer = Mathf.Clamp(_config.MaxPerPlayer, MinPerPlayer, MaxPerPlayerCap);
            _config.FridgeSkimIntervalSeconds = Mathf.Max(MinFridgeSkimInterval, _config.FridgeSkimIntervalSeconds);
            _config.FridgeSkimsPerInterval = Mathf.Clamp(_config.FridgeSkimsPerInterval, MinMilkSkimsPerPass, MaxMilkSkimsPerPassCap);

            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion
    }
}
