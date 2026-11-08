// COMPILE-ONLY SHIMS. Do not copy this file into carbon/plugins or carbon/managed.
//
// Carbon.Common.dll in this workspace is Carbon 2.0.262.0 and contains the Oxide
// permission and RustPlugin types. Referencing it pulls UnityEngine.*, Assembly-CSharp,
// Facepunch.UnityEngine, Facepunch.Console, Facepunch.Network, and Newtonsoft.Json 13.
// Those game modules are not in this workspace (Assembly-CSharp is patched here, but it
// does not load without Unity). The stubs below exist only so
// NightshadeDefaultUnlocked.cs can be compiled with CARBON;RUST;OXIDE_PUBLICIZED;UNIX.
// They are not a substitute for a live Carbon/Rust load, and they do not change groups.

using System;
using System.Collections.Generic;

namespace Newtonsoft.Json
{
    public sealed class JsonPropertyAttribute : Attribute
    {
        public JsonPropertyAttribute(string propertyName) { }
    }
}

namespace Oxide.Core.Configuration
{
    public class DynamicConfigFile
    {
        public T ReadObject<T>() where T : class, new() { return new T(); }
        public void WriteObject(object value, bool indented) { }
    }
}

namespace Oxide.Core
{
    public class DataFileSystem
    {
        public T ReadObject<T>(string name) where T : class, new() { return new T(); }
        public void WriteObject<T>(string name, T data) { }
    }

    public class OxideMod
    {
        public DataFileSystem DataFileSystem = new DataFileSystem();
    }

    public static class Interface
    {
        public static OxideMod Oxide = new OxideMod();
    }
}

namespace Oxide.Game.Rust.Libraries
{
    public class Command
    {
        public void AddConsoleCommand(string name, object plugin, string method) { }
    }
}

namespace Oxide.Core.Libraries
{
    public class UserData
    {
    }

    public class Permission
    {
        public Dictionary<string, UserData> userdata = new Dictionary<string, UserData>();

        public void RegisterPermission(string name, object owner) { }
        public bool UserHasPermission(string id, string perm) { return false; }
        public bool GroupExists(string groupName) { return false; }
        public string GetGroupParent(string group) { return ""; }
        public string[] GetGroups() { return new string[0]; }
        public string[] GetUsersInGroup(string group) { return new string[0]; }
        public bool UserHasGroup(string id, string name) { return false; }
        public void AddUserGroup(string id, string name, bool addIfNotExisting) { }
        public void RemoveUserGroup(string id, string name) { }
    }
}

namespace Oxide.Plugins
{
    public class Timer
    {
        public void Destroy() { }
    }

    public class PluginTimers
    {
        public Timer Once(float delay, Action callback) { return new Timer(); }
    }

    public class Plugin
    {
        public Oxide.Game.Rust.Libraries.Command cmd = new Oxide.Game.Rust.Libraries.Command();
        public Oxide.Core.Libraries.Permission permission = new Oxide.Core.Libraries.Permission();
        public Oxide.Core.Configuration.DynamicConfigFile Config = new Oxide.Core.Configuration.DynamicConfigFile();

        protected virtual void LoadDefaultConfig() { }
        protected virtual void LoadConfig() { }
        protected virtual void SaveConfig() { }
        protected void Puts(string message) { }
        protected void PrintWarning(string message) { }
        protected void PrintError(string message) { }
    }

    public class RustPlugin : Plugin
    {
        public PluginTimers timer = new PluginTimers();
    }

    public sealed class InfoAttribute : Attribute
    {
        public InfoAttribute(string title, string author, string version) { }
    }

    public sealed class DescriptionAttribute : Attribute
    {
        public DescriptionAttribute(string description) { }
    }
}

public class BasePlayer
{
    public string UserIDString;
    public bool IsAdmin;
    public static List<BasePlayer> activePlayerList = new List<BasePlayer>();
    public static List<BasePlayer> sleepingPlayerList = new List<BasePlayer>();
}

public class ConsoleSystem
{
    public class Arg
    {
        public Connection Connection;
        public string[] Args;
        public void ReplyWith(string message) { }
    }

    public class Connection
    {
        public BasePlayer player;
    }
}
