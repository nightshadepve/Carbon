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

using System;
using System.Globalization;
using Newtonsoft.Json;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("NightshadeSkimSpeed", "Nightshade", "1.0.0")]
    [Description("Scales vanilla fridge milk skim time through server.creamseparationhours.")]
    public class NightshadeSkimSpeed : RustPlugin
    {
        private const string ConvarName = "server.creamseparationhours";
        private const string StatePath = "Nightshade/NightshadeSkimSpeed/State";
        private const float DefaultVanillaHours = 4f;
        private const float MinimumHours = 1f / 3600f;
        private const float HoursEpsilon = 0.0001f;

        private PluginConfig _config;
        private float _baselineHours = DefaultVanillaHours;
        private bool _baselineReady;

        private class PluginConfig
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Skim speed multiplier")]
            public float SkimSpeedMultiplier = 2f;
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

            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void Init()
        {
            Apply(WorldIsUp());
        }

        private void OnServerInitialized()
        {
            Apply(true);
        }

        private void Unload()
        {
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
            return "NightshadeSkimSpeed " + mode
                + ". Multiplier " + FormatHours(multiplier)
                + ". Vanilla " + FormatHours(_baselineHours) + " hours"
                + ". " + ConvarName + " is " + FormatHours(live) + " hours."
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
    }
}
