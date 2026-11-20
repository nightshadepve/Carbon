using System;
using System.Reflection;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("NightshadeTeleportGuard", "Nightshade", "1.0.0")]
    [Description("Builds the IQTeleportation UI object that /home crashes on when suggestion popups are disabled.")]
    public class NightshadeTeleportGuard : RustPlugin
    {
        private const string TargetName = "IQTeleportation";

        [PluginReference]
        private Plugin IQTeleportation;

        private PluginConfig _config;
        private Timer _retryTimer;
        private int _attempts;
        private bool _builtByGuard;
        private string _status = "Not checked yet.";

        private class PluginConfig
        {
            public bool Enabled = true;
            public bool PrintStartupMessage = true;
            public float RetrySeconds = 1f;
            public int MaxAttempts = 6;
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

                if (_config.RetrySeconds < 0.2f)
                    _config.RetrySeconds = 0.2f;
                if (_config.MaxAttempts < 1)
                    _config.MaxAttempts = 1;
            }
            catch (Exception ex)
            {
                PrintWarning("Could not read config. Loading defaults. Error: " + ex.Message);
                LoadDefaultConfig();
            }

            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void Init()
        {
            LoadConfig();
        }

        private void OnServerInitialized()
        {
            ScheduleRepair();
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin != null && plugin.Name == TargetName)
                ScheduleRepair();
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin == null || plugin.Name != TargetName)
                return;

            _builtByGuard = false;
            _status = "IQTeleportation unloaded. Waiting for it to load again.";
        }

        private void Unload()
        {
            if (_retryTimer != null && !_retryTimer.Destroyed)
                _retryTimer.Destroy();
            _retryTimer = null;
        }

        private void ScheduleRepair()
        {
            _attempts = 0;
            if (_retryTimer != null && !_retryTimer.Destroyed)
                _retryTimer.Destroy();
            _retryTimer = null;
            TryRepair();
        }

        private void TryRepair()
        {
            _retryTimer = null;

            if (_config == null || !_config.Enabled)
            {
                _status = "Disabled in configs/NightshadeTeleportGuard.json.";
                return;
            }

            _attempts++;
            Plugin iq = IQTeleportation ?? plugins.Find(TargetName);
            Type iqType = FindLiveIqType();
            if (iq == null || iqType == null)
            {
                _status = "IQTeleportation is not loaded.";
                QueueRetry();
                return;
            }

            FieldInfo selfField = FindField(iqType, "_");
            object self = selfField?.GetValue(null);
            if (self == null)
            {
                _status = "IQTeleportation is still starting.";
                QueueRetry();
                return;
            }

            FieldInfo interfaceField = FindField(iqType, "_interface");
            if (interfaceField == null)
            {
                _status = "Could not find IQTeleportation._interface. This guard does not match the installed plugin.";
                PrintError(_status);
                return;
            }

            if (interfaceField.GetValue(null) != null)
            {
                _status = _builtByGuard
                    ? "IQTeleportation UI object is present. This plugin built it."
                    : "IQTeleportation UI object is already present. /home will not hit the null UI reference.";
                if (_config.PrintStartupMessage)
                    Puts(_status);
                return;
            }

            bool suggestAccept = ReadBool(iqType, "config", "teleportationController", "teleportSetting", "suggetionAcceptedUI");
            bool suggestBed = ReadBool(iqType, "config", "homeController", "suggetionSetHomeAfterInstallBed");
            bool mapList = ReadBool(iqType, "config", "homeController", "uiMapHomeList");
            object imageUi = FindField(iqType, "_imageUI")?.GetValue(null);
            bool suggestionUi = suggestAccept || suggestBed;

            // Suggestion popups construct _interface only after their PNG loads.
            // Building it ourselves before _imageUI exists throws inside that constructor.
            if (suggestionUi && imageUi == null)
            {
                _status = "Waiting for IQTeleportation to finish creating its suggestion UI.";
                QueueRetry();
                return;
            }

            try
            {
                Type builderType = iqType.GetNestedType("InterfaceBuilder", BindingFlags.Public | BindingFlags.NonPublic);
                if (builderType == null)
                    throw new MissingMemberException("InterfaceBuilder");

                ConstructorInfo ctor = builderType.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                if (ctor == null)
                    throw new MissingMemberException("InterfaceBuilder constructor");

                object builder;
                try
                {
                    builder = ctor.Invoke(null);
                }
                catch
                {
                    FieldInfo instanceField = builderType.GetField("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    instanceField?.SetValue(null, null);
                    throw;
                }

                interfaceField.SetValue(null, builder);
                _builtByGuard = true;
                DrawHomeMap(self, iqType);
                _status = suggestionUi
                    ? "Built the IQTeleportation UI object before its image loader assigned it. /home no longer calls UpdateMapButtons on null."
                    : "Built the missing IQTeleportation UI object. Both suggestion popups are off, so IQTeleportation never created it, and /home crashed on UpdateMapButtons. G-Map home list is " + (mapList ? "on" : "off") + ".";
                Puts(_status);
            }
            catch (Exception ex)
            {
                _status = "Failed to build the IQTeleportation UI object: " + Unwrap(ex).Message;
                PrintError(_status);
                QueueRetry();
            }
        }

        private void QueueRetry()
        {
            if (_attempts >= _config.MaxAttempts)
            {
                PrintError(_status + " Gave up after " + _attempts + " attempts. /home can still throw until IQTeleportation creates its UI object.");
                return;
            }

            _retryTimer = timer.Once(_config.RetrySeconds, TryRepair);
        }

        private static Type FindLiveIqType()
        {
            Type fallback = null;
            Assembly[] assemblies;
            try
            {
                assemblies = AppDomain.CurrentDomain.GetAssemblies();
            }
            catch (Exception)
            {
                return null;
            }

            foreach (Assembly assembly in assemblies)
            {
                Type type;
                try
                {
                    type = assembly.GetType("Oxide.Plugins.IQTeleportation", false);
                }
                catch (Exception)
                {
                    continue;
                }

                if (type == null)
                    continue;

                fallback = type;
                FieldInfo self = type.GetField("_", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (self != null && self.GetValue(null) != null)
                    return type;
            }

            return fallback;
        }

        private void DrawHomeMap(object iq, Type iqType)
        {
            MethodInfo draw = iqType.GetMethod("DrawUI_LastHomes_Map", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (draw == null)
                return;

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected)
                    continue;

                try
                {
                    draw.Invoke(iq, new object[] { player });
                }
                catch (Exception ex)
                {
                    PrintWarning("Home map buttons were not drawn for " + player.displayName + ": " + Unwrap(ex).Message);
                }
            }
        }

        private static FieldInfo FindField(Type type, string name)
        {
            return type.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        }

        private static bool ReadBool(Type rootType, params string[] path)
        {
            object current = null;
            Type currentType = rootType;
            bool first = true;

            foreach (string name in path)
            {
                FieldInfo field = currentType.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null)
                    return false;

                current = first ? field.GetValue(null) : field.GetValue(current);
                first = false;
                if (current == null)
                    return false;
                currentType = current.GetType();
            }

            return current is bool value && value;
        }

        private static Exception Unwrap(Exception ex)
        {
            if (ex is TargetInvocationException && ex.InnerException != null)
                return ex.InnerException;
            return ex;
        }

        [ConsoleCommand("ns.teleportguard")]
        private void CmdStatus(ConsoleSystem.Arg arg)
        {
            if (!HasConsoleAccess(arg))
            {
                SendReply(arg, "You do not have access to ns.teleportguard.");
                return;
            }

            string action = arg.HasArgs() ? arg.GetString(0) : "status";
            if (string.Equals(action, "retry", StringComparison.OrdinalIgnoreCase))
            {
                ScheduleRepair();
                SendReply(arg, "NightshadeTeleportGuard: " + _status);
                return;
            }

            SendReply(arg, "NightshadeTeleportGuard: " + _status + " Built by this plugin: " + _builtByGuard + ".");
        }

        // Server console and Rust auth level 2 (owner). No Oxide permission is registered or granted.
        private static bool HasConsoleAccess(ConsoleSystem.Arg arg)
        {
            return arg.Connection == null || arg.Connection.authLevel >= 2;
        }
    }
}
