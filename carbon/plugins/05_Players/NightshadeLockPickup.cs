#if !NIGHTSHADE_LOGIC_ONLY
using System;
using System.Reflection;
using HarmonyLib;
using Oxide.Core.Plugins;
#endif

namespace Nightshade.LockPickup
{
    public static class LockPickupRules
    {
        // UltimateLocker.CanPickupLock kills the lock and cancels vanilla pickup for
        // ultimatelocker.autolock.nolockrequired. Carbon calls that hook before BaseLock
        // checks IsLocked and before it creates the lock item. Allow that destroy only
        // when auto-lock placement is actually enabled, so a manually paid lock is
        // returned and a still-locked lock is left for vanilla to reject.
        public static bool AllowUltimateLockerDestroy(
            bool couldReadSteamId,
            bool couldReadLocked,
            bool lockIsLocked,
            bool couldReadPlacement,
            bool autoPlacementEnabled)
        {
            if (!couldReadSteamId || !couldReadLocked || !couldReadPlacement)
                return false;

            if (lockIsLocked)
                return false;

            if (!autoPlacementEnabled)
                return false;

            return true;
        }
    }
}

#if !NIGHTSHADE_LOGIC_ONLY
namespace Oxide.Plugins
{
    [Info("NightshadeLockPickup", "Nightshade", "1.0.0")]
    [Description("Stops UltimateLocker from destroying a lock pickup unless auto-lock placement is enabled.")]
    public class NightshadeLockPickup : RustPlugin
    {
        private const string PatchId = "com.nightshade.lockpickup";

        private static NightshadeLockPickup _instance;
        private Harmony _harmony;
        private PluginConfig _config;

        private class PluginConfig
        {
            public bool Enabled = true;
        }

        private void Init()
        {
            _instance = this;
            LoadConfig();
        }

        private void OnServerInitialized()
        {
            TryPatch();
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin == null || plugin.Name != "UltimateLocker")
                return;

            TryPatch();
        }

        private void Unload()
        {
            Unpatch();
            _instance = null;
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                    throw new Exception("Config was null");
            }
            catch (Exception ex)
            {
                PrintWarning("Could not read config. Loading defaults. " + ex.Message);
                LoadDefaultConfig();
            }

            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void TryPatch()
        {
            Unpatch();

            if (_config == null || !_config.Enabled)
            {
                Puts("Lock pickup guard is disabled in carbon/configs/NightshadeLockPickup.json.");
                return;
            }

            Type lockerType = AccessTools.TypeByName("Oxide.Plugins.UltimateLocker");
            if (lockerType == null)
            {
                Puts("UltimateLocker is not loaded. Lock pickup guard is idle.");
                return;
            }

            MethodInfo target = AccessTools.Method(lockerType, "CanPickupLock");
            if (target == null)
            {
                PrintWarning("UltimateLocker.CanPickupLock was not found. Lock pickup was left unchanged.");
                return;
            }

            try
            {
                _harmony = new Harmony(PatchId);
                _harmony.Patch(target, prefix: new HarmonyMethod(typeof(LockPickupPatch), nameof(LockPickupPatch.Prefix)));
                Puts("Lock pickup guard active. Vanilla returns the lock item unless that player's UltimateLocker auto-lock placement is enabled.");
            }
            catch (Exception ex)
            {
                PrintError("Failed to patch UltimateLocker.CanPickupLock: " + ex.Message);
            }
        }

        private void Unpatch()
        {
            try
            {
                _harmony?.UnpatchAll(PatchId);
            }
            catch (Exception ex)
            {
                PrintWarning("Harmony unpatch warning: " + ex.Message);
            }

            _harmony = null;
        }

        internal static bool ShouldAllowDestroy(object ultimateLocker, object player, object baseLock)
        {
            bool couldReadLocked = TryGetLocked(baseLock, out bool locked);
            if (!couldReadLocked || locked)
                return false;

            bool couldReadSteamId = TryGetSteamId(player, out ulong steamId);
            bool placementEnabled = false;
            bool couldReadPlacement = couldReadSteamId && TryGetAutoPlacementEnabled(ultimateLocker, steamId, out placementEnabled);
            return Nightshade.LockPickup.LockPickupRules.AllowUltimateLockerDestroy(
                couldReadSteamId,
                true,
                false,
                couldReadPlacement,
                placementEnabled);
        }

        private static bool TryGetSteamId(object player, out ulong steamId)
        {
            steamId = 0;
            if (player == null)
                return false;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo field = player.GetType().GetField("UserIDString", flags);
            string raw = field == null ? null : field.GetValue(player) as string;
            return ulong.TryParse(raw, out steamId) && steamId > 0;
        }

        private static bool TryGetLocked(object entity, out bool locked)
        {
            locked = true;
            if (entity == null)
                return false;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo method = entity.GetType().GetMethod("IsLocked", flags, null, Type.EmptyTypes, null);
            if (method == null || method.ReturnType != typeof(bool))
                return false;

            object value = method.Invoke(entity, null);
            if (!(value is bool flag))
                return false;

            locked = flag;
            return true;
        }

        private static bool TryGetAutoPlacementEnabled(object ultimateLocker, ulong steamId, out bool enabled)
        {
            enabled = false;
            if (ultimateLocker == null)
                return false;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo method = ultimateLocker.GetType().GetMethod(
                "GetAutoLockPlayerSettingsData",
                flags,
                null,
                new[] { typeof(ulong) },
                null);
            if (method == null)
                return false;

            object settings = method.Invoke(ultimateLocker, new object[] { steamId });
            if (settings == null)
                return false;

            FieldInfo field = settings.GetType().GetField("AutoLockOnPlacementEnabled", flags);
            if (field == null || field.FieldType != typeof(bool))
                return false;

            object value = field.GetValue(settings);
            if (!(value is bool flag))
                return false;

            enabled = flag;
            return true;
        }

        public static class LockPickupPatch
        {
            public static bool Prefix(object __instance, object player, object baseLock, ref object __result)
            {
                try
                {
                    if (NightshadeLockPickup.ShouldAllowDestroy(__instance, player, baseLock))
                        return true;

                    __result = null;
                    return false;
                }
                catch (Exception ex)
                {
                    if (_instance != null)
                        _instance.PrintError("Lock pickup guard failed open to vanilla pickup: " + ex.Message);

                    __result = null;
                    return false;
                }
            }
        }
    }
}
#endif
