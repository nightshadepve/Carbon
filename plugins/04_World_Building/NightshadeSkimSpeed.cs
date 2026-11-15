// NightshadeSkimSpeed
// Original Nightshade plugin for Carbon 2.0.264.
//
// Vanilla fridge milk skimming has no per-fridge timer. A powered fridge
// (spoil multiplier below 1) adds real seconds to
// ProtoBuf.Item.InstanceData.refrigeratedSeconds. The milk can be skimmed
// once that reaches ItemModConditionRefrigeratedTime.RequiredSeconds, which
// is ConVar.Server.creamSeparationHours * 3600. The console name is
// server.creamseparationhours. It is a replicated var, not a saved one, and
// the build default is 4 hours. Items already chilling do not pick up a new
// value until their next network update.
//
// This plugin divides the captured hours by "Skim speed multiplier" and
// writes the convar back through the console so clients receive it. Unload
// restores the captured hours. Cow milk charges (Cow.MilkCooldown) are a
// different clock and are left alone.
//
// A whole stack is skimmed by calling the milk item's vanilla ItemModSwap
// once per milk. That action consumes one milk and creates the outputs
// defined on the item. Players need nightshadeskimspeed.skimall, which is
// not granted automatically. /skim covers the inventory and an open
// container. The vanilla Skim button on a stack does that whole stack.

using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Newtonsoft.Json;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("NightshadeSkimSpeed", "Nightshade", "1.1.0")]
    [Description("Scales fridge milk skim time and skims a whole milk stack at once.")]
    public class NightshadeSkimSpeed : RustPlugin
    {
        private const string ConvarName = "server.creamseparationhours";
        private const string StatePath = "Nightshade/NightshadeSkimSpeed/State";
        private const string PermSkimAll = "nightshadeskimspeed.skimall";
        private const string PatchId = "com.nightshade.skimspeed";
        private const string DefaultChatCommand = "skim";
        private const string DefaultMilkShortname = "milk";
        private const int MaxUnitsPerUse = 2000;
        private const float DefaultVanillaHours = 4f;
        private const float MinimumHours = 1f / 3600f;
        private const float HoursEpsilon = 0.0001f;

        private static NightshadeSkimSpeed _plugin;

        private PluginConfig _config;
        private Harmony _harmony;
        private float _baselineHours = DefaultVanillaHours;
        private bool _baselineReady;
        private string _chatCommand = DefaultChatCommand;

        private class PluginConfig
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Skim speed multiplier")]
            public float SkimSpeedMultiplier = 2f;

            [JsonProperty("Skim without fridge time")]
            public bool SkimWithoutFridgeTime = false;

            [JsonProperty("Chat command")]
            public string ChatCommand = DefaultChatCommand;

            [JsonProperty("Skim your inventory")]
            public bool SkimYourInventory = true;

            [JsonProperty("Skim the container you have open")]
            public bool SkimOpenContainer = true;

            [JsonProperty("Milk item shortname")]
            public string MilkShortname = DefaultMilkShortname;
        }

        private struct SkimTotals
        {
            public int Units;
            public int Stacks;
            public int Waiting;
            public bool HitCap;
        }

        private class BaselineState
        {
            [JsonProperty("Vanilla cream separation hours")]
            public float VanillaHours;

            [JsonProperty("Applied cream separation hours")]
            public float AppliedHours;

            [JsonProperty("Applied")]
            public bool Applied;
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                {
                    throw new Exception("Config was null");
                }
            }
            catch (Exception ex)
            {
                PrintWarning("NightshadeSkimSpeed config could not be read. Using defaults. " + ex.Message);
                LoadDefaultConfig();
            }

            if (float.IsNaN(_config.SkimSpeedMultiplier) || float.IsInfinity(_config.SkimSpeedMultiplier) || _config.SkimSpeedMultiplier <= 0f)
            {
                PrintWarning("Skim speed multiplier must be greater than 0. Using 1.");
                _config.SkimSpeedMultiplier = 1f;
            }

            _config.ChatCommand = CleanCommand(_config.ChatCommand);
            if (string.IsNullOrEmpty(_config.MilkShortname))
            {
                _config.MilkShortname = DefaultMilkShortname;
            }

            _config.MilkShortname = _config.MilkShortname.Trim();
            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void Init()
        {
            _plugin = this;
            if (_config == null)
            {
                LoadConfig();
            }

            permission.RegisterPermission(PermSkimAll, this);
            _chatCommand = _config.ChatCommand;
            cmd.AddChatCommand(_chatCommand, this, nameof(CmdSkim));
            TryPatch();
            Apply(WorldIsUp());
        }

        private void OnServerInitialized()
        {
            Apply(true);
        }

        private void Unload()
        {
            try
            {
                if (_harmony != null)
                {
                    _harmony.UnpatchAll(PatchId);
                }
            }
            catch (Exception ex)
            {
                PrintWarning("Harmony unpatch warning: " + ex.Message);
            }

            _harmony = null;
            if (_plugin == this)
            {
                _plugin = null;
            }

            if (!_baselineReady)
            {
                return;
            }

            SetConvar(_baselineHours);
            WriteState(_baselineHours, _baselineHours, false);
            int fridges;
            int items;
            RefreshFridges(out fridges, out items);
            Puts("Restored " + ConvarName + " to " + FormatHours(_baselineHours) + " hours. Refreshed " + items + " milk items in " + fridges + " fridges.");
            _baselineReady = false;
        }

        [ConsoleCommand("nsskim.reload")]
        private void CmdReload(ConsoleSystem.Arg arg)
        {
            if (!HasConsoleAccess(arg))
            {
                return;
            }

            LoadConfig();
            Apply(true);
            SendReply(arg, StatusText());
        }

        [ConsoleCommand("nsskim.status")]
        private void CmdStatus(ConsoleSystem.Arg arg)
        {
            if (!HasConsoleAccess(arg))
            {
                return;
            }

            SendReply(arg, StatusText());
        }

        private void Apply(bool refresh)
        {
            if (_config == null)
            {
                LoadConfig();
            }

            float baseline = ResolveBaseline();
            _baselineHours = baseline;
            _baselineReady = true;

            float target = baseline;
            if (_config.Enabled)
            {
                target = baseline / _config.SkimSpeedMultiplier;
                if (target < MinimumHours)
                {
                    PrintWarning("Skim speed multiplier would put cream separation under 1 second. Clamped to 1 second.");
                    target = MinimumHours;
                }
            }

            SetConvar(target);
            WriteState(baseline, target, _config.Enabled);

            int fridges = 0;
            int items = 0;
            if (refresh)
            {
                RefreshFridges(out fridges, out items);
            }

            string mode = _config.Enabled ? "on" : "off";
            Puts("Skim speed " + mode + ". Multiplier " + FormatHours(_config.SkimSpeedMultiplier) + ". " + ConvarName + " " + FormatHours(baseline) + " -> " + FormatHours(ReadConvar()) + " hours." + (refresh ? " Refreshed " + items + " milk items in " + fridges + " fridges." : ""));
        }

        private float ResolveBaseline()
        {
            float current = ReadConvar();
            BaselineState state = ReadState();
            if (state != null && state.Applied && state.VanillaHours > 0f && Nearly(current, state.AppliedHours))
            {
                return state.VanillaHours;
            }

            if (current > 0f && !float.IsNaN(current) && !float.IsInfinity(current))
            {
                return current;
            }

            PrintWarning(ConvarName + " was not a positive number. Using the vanilla default of 4 hours as the baseline.");
            return DefaultVanillaHours;
        }

        private static float ReadConvar()
        {
            return ConVar.Server.creamSeparationHours;
        }

        private void SetConvar(float hours)
        {
            string text = hours.ToString("0.######", CultureInfo.InvariantCulture);
            ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), ConvarName, text);
            float actual = ReadConvar();
            if (!Nearly(actual, hours))
            {
                ConVar.Server.creamSeparationHours = hours;
                PrintWarning(ConvarName + " did not accept " + text + " from the console. Wrote the field directly, so connected clients may keep the previous replicated value until they reconnect.");
            }
        }

        private void RefreshFridges(out int fridges, out int items)
        {
            fridges = 0;
            items = 0;
            if (BaseNetworkable.serverEntities == null)
            {
                return;
            }

            foreach (BaseNetworkable networkable in BaseNetworkable.serverEntities)
            {
                try
                {
                    BaseEntity entity;
                    ItemContainer inventory;
                    if (!TryGetFridgeInventory(networkable, out entity, out inventory))
                    {
                        continue;
                    }

                    bool touched = false;
                    int count = inventory.itemList.Count;
                    for (int i = 0; i < count; i++)
                    {
                        Item item = inventory.itemList[i];
                        if (!HasRefrigeratedSkim(item))
                        {
                            continue;
                        }

                        item.MarkDirty();
                        touched = true;
                        items++;
                    }

                    if (!touched)
                    {
                        continue;
                    }

                    entity.SendNetworkUpdate();
                    fridges++;
                }
                catch (Exception ex)
                {
                    PrintWarning("Skipped a fridge while refreshing milk. " + ex.Message);
                }
            }
        }

        private static bool TryGetFridgeInventory(BaseNetworkable networkable, out BaseEntity entity, out ItemContainer inventory)
        {
            entity = networkable as BaseEntity;
            inventory = null;
            if (entity == null || entity.IsDestroyed)
            {
                return false;
            }

            Fridge fridge = entity as Fridge;
            if (fridge != null)
            {
                inventory = fridge.inventory;
                return inventory != null && inventory.itemList != null;
            }

            string name = entity.ShortPrefabName;
            if (string.IsNullOrEmpty(name) || name.IndexOf("fridge", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            StorageContainer storage = entity as StorageContainer;
            if (storage != null)
            {
                inventory = storage.inventory;
                return inventory != null && inventory.itemList != null;
            }

            ContainerIOEntity io = entity as ContainerIOEntity;
            if (io != null)
            {
                inventory = io.inventory;
                return inventory != null && inventory.itemList != null;
            }

            return false;
        }

        private static bool HasRefrigeratedSkim(Item item)
        {
            if (item == null || item.info == null)
            {
                return false;
            }

            ItemMod[] mods = item.info.itemMods;
            if (mods == null)
            {
                return item.info.GetComponent<ItemModConditionRefrigeratedTime>() != null;
            }

            for (int i = 0; i < mods.Length; i++)
            {
                if (mods[i] is ItemModConditionRefrigeratedTime)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool WorldIsUp()
        {
            return BaseNetworkable.serverEntities != null && BaseNetworkable.serverEntities.Count > 0;
        }

        private BaselineState ReadState()
        {
            try
            {
                return Interface.Oxide.DataFileSystem.ReadObject<BaselineState>(StatePath);
            }
            catch (Exception ex)
            {
                PrintWarning("Could not read skim baseline. " + ex.Message);
                return null;
            }
        }

        private void WriteState(float vanillaHours, float appliedHours, bool applied)
        {
            BaselineState state = new BaselineState();
            state.VanillaHours = vanillaHours;
            state.AppliedHours = appliedHours;
            state.Applied = applied;
            try
            {
                Interface.Oxide.DataFileSystem.WriteObject(StatePath, state);
            }
            catch (Exception ex)
            {
                PrintWarning("Could not write skim baseline. " + ex.Message);
            }
        }

        private string StatusText()
        {
            float live = ReadConvar();
            string mode = _config != null && _config.Enabled ? "on" : "off";
            float multiplier = _config != null ? _config.SkimSpeedMultiplier : 1f;
            string bypass = _config != null && _config.SkimWithoutFridgeTime ? "on" : "off";
            return "NightshadeSkimSpeed " + mode
                + ". Multiplier " + FormatHours(multiplier)
                + ". Vanilla " + FormatHours(_baselineHours) + " hours"
                + ". " + ConvarName + " is " + FormatHours(live) + " hours."
                + " Whole-stack skim permission " + PermSkimAll + " (not granted automatically)."
                + " Chat /" + _chatCommand + "."
                + " Skim without fridge time " + bypass + "."
                + " Reload with nsskim.reload.";
        }

        private static string FormatHours(float hours)
        {
            return hours.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static bool Nearly(float left, float right)
        {
            return Math.Abs(left - right) <= HoursEpsilon;
        }

        private static bool HasConsoleAccess(ConsoleSystem.Arg arg)
        {
            return arg.Connection == null || arg.Connection.authLevel >= 2;
        }

        private static string CleanCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return DefaultChatCommand;
            }

            command = command.Trim();
            if (command.StartsWith("/", StringComparison.Ordinal))
            {
                command = command.Substring(1);
            }

            return string.IsNullOrEmpty(command) ? DefaultChatCommand : command;
        }

        private void TryPatch()
        {
            try
            {
                _harmony = new Harmony(PatchId);
                _harmony.PatchAll();
            }
            catch (Exception ex)
            {
                PrintError("Could not patch the milk skim button. /" + _chatCommand + " still works. " + ex.Message);
                _harmony = null;
            }
        }

        private void CmdSkim(BasePlayer player, string command, string[] args)
        {
            if (player == null)
            {
                return;
            }

            if (!HasSkimAllPermission(player))
            {
                SendReply(player, "You do not have permission to skim a whole stack.");
                return;
            }

            SkimTotals totals = SkimPlayer(player);
            if (totals.HitCap)
            {
                SendReply(player, "Skimmed " + totals.Units + " milk and stopped at the safety cap.");
                return;
            }

            if (totals.Units <= 0 && totals.Waiting <= 0)
            {
                SendReply(player, "No milk to skim.");
                return;
            }

            if (totals.Units <= 0)
            {
                SendReply(player, totals.Waiting + " milk still needs time in a fridge.");
                return;
            }

            if (totals.Waiting > 0)
            {
                SendReply(player, "Skimmed " + totals.Units + " milk. " + totals.Waiting + " milk still needs time in a fridge.");
                return;
            }

            SendReply(player, "Skimmed " + totals.Units + " milk.");
        }

        private bool HasSkimAllPermission(BasePlayer player)
        {
            return player != null && permission.UserHasPermission(player.UserIDString, PermSkimAll);
        }

        private SkimTotals SkimPlayer(BasePlayer player)
        {
            List<ItemContainer> containers = new List<ItemContainer>();
            if (_config.SkimYourInventory && player.inventory != null)
            {
                AddContainer(containers, player.inventory.containerMain);
                AddContainer(containers, player.inventory.containerBelt);
            }

            if (_config.SkimOpenContainer && player.inventory != null && player.inventory.loot != null)
            {
                List<ItemContainer> open = player.inventory.loot.containers;
                if (open != null)
                {
                    for (int i = 0; i < open.Count; i++)
                    {
                        AddContainer(containers, open[i]);
                    }
                }
            }

            List<Item> milk = new List<Item>();
            for (int i = 0; i < containers.Count; i++)
            {
                CollectMilk(containers[i], milk, 0);
            }

            SkimTotals totals = new SkimTotals();
            for (int i = 0; i < milk.Count; i++)
            {
                if (totals.Units >= MaxUnitsPerUse)
                {
                    totals.HitCap = true;
                    break;
                }

                Item item = milk[i];
                if (!IsMilk(item))
                {
                    continue;
                }

                ItemModSwap swap = FindSkimSwap(item.info);
                if (swap == null)
                {
                    continue;
                }

                int skimmed = SkimStack(item, player, swap, MaxUnitsPerUse - totals.Units);
                totals.Units += skimmed;
                if (skimmed > 0)
                {
                    totals.Stacks++;
                }
                else if (item.amount > 0)
                {
                    totals.Waiting += item.amount;
                }

                if (skimmed > 0 && totals.Units >= MaxUnitsPerUse)
                {
                    totals.HitCap = true;
                }
            }

            return totals;
        }

        private int SkimStack(Item item, BasePlayer player, ItemModSwap swap, int limit)
        {
            if (item == null || player == null || swap == null || limit <= 0)
            {
                return 0;
            }

            int guard = item.amount;
            if (guard > limit)
            {
                guard = limit;
            }

            int skimmed = 0;
            bool ignoreFridgeTime = _config != null && _config.SkimWithoutFridgeTime;
            for (int i = 0; i < guard; i++)
            {
                if (item.amount <= 0)
                {
                    break;
                }

                if (!CanSkim(swap, item, player, ignoreFridgeTime))
                {
                    break;
                }

                int before = item.amount;
                try
                {
                    swap.DoAction(item, player);
                }
                catch (Exception ex)
                {
                    PrintWarning("Milk skim action failed. " + ex.Message);
                    break;
                }

                if (item.amount >= before)
                {
                    break;
                }

                skimmed++;
            }

            return skimmed;
        }

        private static bool CanSkim(ItemModSwap swap, Item item, BasePlayer player, bool ignoreFridgeTime)
        {
            ItemMod[] siblings = swap.siblingMods;
            if (siblings == null)
            {
                return ignoreFridgeTime || ItemModConditionRefrigeratedTime.IsReady(item);
            }

            if (swap.CanDoAction(item, player))
            {
                return true;
            }

            if (!ignoreFridgeTime)
            {
                return false;
            }

            for (int i = 0; i < siblings.Length; i++)
            {
                ItemMod sibling = siblings[i];
                if (sibling == null || sibling is ItemModConditionRefrigeratedTime)
                {
                    continue;
                }

                if (!sibling.Passes(item))
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsMilk(Item item)
        {
            if (item == null || item.info == null || string.IsNullOrEmpty(item.info.shortname))
            {
                return false;
            }

            string expected = _config != null && !string.IsNullOrEmpty(_config.MilkShortname) ? _config.MilkShortname : DefaultMilkShortname;
            return string.Equals(item.info.shortname, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static ItemModSwap FindSkimSwap(ItemDefinition info)
        {
            if (info == null || info.itemMods == null)
            {
                return null;
            }

            ItemModSwap fallback = null;
            for (int i = 0; i < info.itemMods.Length; i++)
            {
                ItemModMenuOption option = info.itemMods[i] as ItemModMenuOption;
                if (option == null)
                {
                    continue;
                }

                ItemModSwap swap = option.actionTarget as ItemModSwap;
                if (swap == null)
                {
                    continue;
                }

                if (HasRefrigeratedSibling(swap))
                {
                    return swap;
                }

                if (fallback == null)
                {
                    fallback = swap;
                }
            }

            if (fallback != null)
            {
                return fallback;
            }

            for (int i = 0; i < info.itemMods.Length; i++)
            {
                ItemModSwap swap = info.itemMods[i] as ItemModSwap;
                if (swap != null && HasRefrigeratedSibling(swap))
                {
                    return swap;
                }
            }

            return null;
        }

        private static bool HasRefrigeratedSibling(ItemMod mod)
        {
            if (mod == null || mod.siblingMods == null)
            {
                return false;
            }

            for (int i = 0; i < mod.siblingMods.Length; i++)
            {
                if (mod.siblingMods[i] is ItemModConditionRefrigeratedTime)
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsSkimOption(ItemModMenuOption option, Item item)
        {
            if (option == null || !IsMilk(item))
            {
                return false;
            }

            ItemModSwap swap = option.actionTarget as ItemModSwap;
            if (swap == null)
            {
                return false;
            }

            if (HasRefrigeratedSibling(swap))
            {
                return true;
            }

            return FindSkimSwap(item.info) == swap;
        }

        private static void AddContainer(List<ItemContainer> list, ItemContainer container)
        {
            if (container == null || container.itemList == null)
            {
                return;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == container)
                {
                    return;
                }
            }

            list.Add(container);
        }

        private void CollectMilk(ItemContainer container, List<Item> milk, int depth)
        {
            if (container == null || container.itemList == null || depth > 3)
            {
                return;
            }

            int count = container.itemList.Count;
            for (int i = 0; i < count; i++)
            {
                Item item = container.itemList[i];
                if (item == null)
                {
                    continue;
                }

                if (IsMilk(item))
                {
                    milk.Add(item);
                }

                if (item.contents != null && item.contents != container)
                {
                    CollectMilk(item.contents, milk, depth + 1);
                }
            }
        }

        [HarmonyPatch(typeof(ItemModMenuOption), "ServerCommand")]
        private static class MenuSkimPatch
        {
            private static bool Prefix(ItemModMenuOption __instance, Item item, string command, BasePlayer player)
            {
                NightshadeSkimSpeed plugin = _plugin;
                if (plugin == null || __instance == null || item == null || player == null)
                {
                    return true;
                }

                if (string.IsNullOrEmpty(command) || string.IsNullOrEmpty(__instance.commandName))
                {
                    return true;
                }

                if (!string.Equals(command, __instance.commandName, StringComparison.Ordinal))
                {
                    return true;
                }

                if (!plugin.HasSkimAllPermission(player) || !plugin.IsSkimOption(__instance, item))
                {
                    return true;
                }

                plugin.SkimStack(item, player, __instance.actionTarget as ItemModSwap, MaxUnitsPerUse);
                return false;
            }
        }
    }
}
