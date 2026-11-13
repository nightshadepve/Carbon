using System;
using System.Collections.Generic;
using System.Reflection;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("TCWorkbenchRange", "Nightshade", "1.2.0")]
    [Description("Extends crafting benefit from range-upgraded workbenches to players authorized in their TC building privilege area.")]
    public class TCWorkbenchRange : RustPlugin
    {
        private const string PermissionUse = "tcworkbenchrange.use";
        private const string RangeShortname = "workbench.upgrade.range";
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly List<Workbench> benches = new List<Workbench>();
        private readonly HashSet<ulong> granted = new HashSet<ulong>();
        private Settings settings;
        private bool warnedCache;

        private class Settings
        {
            public float PlayerCheckSeconds = 0.2f;
            public float WorkbenchRefreshSeconds = 5f;
            public bool RequireRangeUpgrade = true;
            public bool RequireTCAuthorization = true;
            public bool RequirePermission = false;
            public bool DebugLogging = false;
        }
        protected override void LoadDefaultConfig() { settings = new Settings(); SaveConfig(); }
        protected override void LoadConfig()
        {
            base.LoadConfig();
            try { settings = Config.ReadObject<Settings>() ?? new Settings(); }
            catch { settings = new Settings(); }
            // Old configs from v1.1 used a slow tick, which let the native craft cache expire.
            settings.PlayerCheckSeconds = Mathf.Clamp(settings.PlayerCheckSeconds, 0.1f, 0.25f);
            settings.WorkbenchRefreshSeconds = Mathf.Max(settings.WorkbenchRefreshSeconds, 2f);
            SaveConfig();
        }
        protected override void SaveConfig() { Config.WriteObject(settings, true); }
        private void Init() { permission.RegisterPermission(PermissionUse, this); }
        private void OnServerInitialized()
        {
            Scan();
            timer.Every(settings.WorkbenchRefreshSeconds, Scan);
            timer.Every(settings.PlayerCheckSeconds, TickPlayers);
            NextTick(TickPlayers);
        }
        private void Unload()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
                if (player != null) Clear(player);
            granted.Clear(); benches.Clear();
        }
        private void OnEntitySpawned(Workbench workbench) { timer.Once(1f, Scan); }
        private void OnEntityKill(Workbench workbench) { timer.Once(0.1f, Scan); }
        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player != null) granted.Remove(player.userID);
        }
        private void Scan()
        {
            benches.Clear();
            foreach (BaseNetworkable entity in BaseNetworkable.serverEntities)
            {
                Workbench bench = entity as Workbench;
                if (bench != null && !bench.IsDestroyed) benches.Add(bench);
            }
        }
        private static bool ReadNumber(object obj, string name, out float number)
        {
            number = 0f;
            if (obj == null) return false;
            Type t = obj.GetType();
            while (t != null)
            {
                try
                {
                    FieldInfo f = t.GetField(name, Flags | BindingFlags.DeclaredOnly);
                    if (f != null) { number = Convert.ToSingle(f.GetValue(obj)); return true; }
                    PropertyInfo p = t.GetProperty(name, Flags | BindingFlags.DeclaredOnly);
                    if (p != null && p.GetIndexParameters().Length == 0) { number = Convert.ToSingle(p.GetValue(obj, null)); return true; }
                }
                catch { }
                t = t.BaseType;
            }
            return false;
        }
        private static bool WriteNumber(object obj, string name, float number)
        {
            if (obj == null) return false;
            Type t = obj.GetType();
            while (t != null)
            {
                try
                {
                    FieldInfo f = t.GetField(name, Flags | BindingFlags.DeclaredOnly);
                    if (f != null) { f.SetValue(obj, Convert.ChangeType(number, f.FieldType)); return true; }
                    PropertyInfo p = t.GetProperty(name, Flags | BindingFlags.DeclaredOnly);
                    if (p != null && p.CanWrite && p.GetIndexParameters().Length == 0)
                    { p.SetValue(obj, Convert.ChangeType(number, p.PropertyType), null); return true; }
                }
                catch { }
                t = t.BaseType;
            }
            return false;
        }
        private static int Level(Workbench bench)
        {
            if (bench == null) return 0;
            float n;
            string[] names = { "Workbenchlevel", "WorkbenchLevel", "workbenchLevel", "workbenchlevel", "Level", "level" };
            foreach (string name in names)
                if (ReadNumber(bench, name, out n) && n >= 1f && n <= 3f) return (int)n;
            // Rust's prefab paths are stable for the 3 workbench tiers even if fields change.
            string prefab = ((bench.ShortPrefabName ?? "") + " " + (bench.PrefabName ?? "")).ToLowerInvariant();
            if (prefab.Contains("workbench3") || prefab.Contains("workbench_3") || prefab.Contains("workbench.level3")) return 3;
            if (prefab.Contains("workbench2") || prefab.Contains("workbench_2") || prefab.Contains("workbench.level2")) return 2;
            if (prefab.Contains("workbench1") || prefab.Contains("workbench_1") || prefab.Contains("workbench.level1")) return 1;
            return 0;
        }
        private static bool SameTC(BuildingPrivlidge a, BuildingPrivlidge b)
        {
            if (a == null || b == null || a.IsDestroyed || b.IsDestroyed) return false;
            if (a == b) return true;
            // Network ID is a safer comparison than managed-object reference equality.
            if (a.net != null && b.net != null && a.net.ID == b.net.ID) return true;
            return false;
        }
        private static IEnumerable<ItemContainer> Containers(Workbench bench)
        {
            HashSet<ItemContainer> found = new HashSet<ItemContainer>();
            Type t = bench.GetType();
            while (t != null)
            {
                foreach (FieldInfo f in t.GetFields(Flags | BindingFlags.DeclaredOnly))
                {
                    if (!typeof(ItemContainer).IsAssignableFrom(f.FieldType)) continue;
                    try { ItemContainer c = f.GetValue(bench) as ItemContainer; if (c != null) found.Add(c); } catch { }
                }
                foreach (PropertyInfo p in t.GetProperties(Flags | BindingFlags.DeclaredOnly))
                {
                    if (!typeof(ItemContainer).IsAssignableFrom(p.PropertyType) || p.GetIndexParameters().Length != 0) continue;
                    try { ItemContainer c = p.GetValue(bench, null) as ItemContainer; if (c != null) found.Add(c); } catch { }
                }
                t = t.BaseType;
            }
            foreach (StorageContainer storage in bench.GetComponentsInChildren<StorageContainer>(true))
                if (storage != null && storage.inventory != null) found.Add(storage.inventory);
            return found;
        }
        private static bool HasUpgrade(Workbench bench)
        {
            foreach (ItemContainer c in Containers(bench))
                if (c.itemList != null)
                    foreach (Item item in c.itemList)
                        if (item != null && item.info != null && item.info.shortname == RangeShortname) return true;
            return false;
        }
        private int EligibleLevel(BasePlayer player, out int matching, out int upgraded)
        {
            matching = 0; upgraded = 0;
            BuildingPrivlidge tc = player.GetBuildingPrivilege();
            if (tc == null || tc.IsDestroyed) return 0;
            if (settings.RequireTCAuthorization && !tc.IsAuthed(player)) return 0;
            if (settings.RequirePermission && !permission.UserHasPermission(player.UserIDString, PermissionUse)) return 0;
            int best = 0;
            foreach (Workbench bench in benches)
            {
                if (bench == null || bench.IsDestroyed || !SameTC(tc, bench.GetBuildingPrivilege())) continue;
                matching++;
                if (settings.RequireRangeUpgrade && !HasUpgrade(bench)) continue;
                upgraded++;
                best = Math.Max(best, Level(bench));
            }
            return best;
        }
        private void TickPlayers()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null) continue;
                if (!player.IsConnected || player.IsDead() || player.IsSleeping()) { Clear(player); continue; }
                int matching, upgraded;
                int bonus = EligibleLevel(player, out matching, out upgraded);
                if (bonus <= 0) { Clear(player); continue; }
                // Native craft-level getter uses its short-lived cachedCraftLevel and nextCheckTime.
                // Keep cache alive between native checks, rather than trying to assign the read-only property.
                float current;
                if (!ReadNumber(player, "cachedCraftLevel", out current))
                {
                    if (!warnedCache) { PrintWarning("No cachedCraftLevel member found. Use tcwb.debug to diagnose this Rust build."); warnedCache = true; }
                    continue;
                }
                float effective = Mathf.Max(bonus, current);
                bool wroteLevel = WriteNumber(player, "cachedCraftLevel", effective);
                bool wroteExpiry = WriteNumber(player, "nextCheckTime", Time.realtimeSinceStartup + 0.4f);
                if (wroteLevel && wroteExpiry) granted.Add(player.userID);
                else if (!warnedCache) { PrintWarning("Unable to update native craft cache; check tcwb.debug."); warnedCache = true; }
            }
        }
        private void Clear(BasePlayer player)
        {
            if (player == null || !granted.Remove(player.userID)) return;
            // Only invalidate; never force the player's vanilla craft level to zero.
            WriteNumber(player, "nextCheckTime", 0f);
        }
        [ChatCommand("tcwb")]
        private void ChatStatus(BasePlayer player, string cmd, string[] args)
        {
            if (player == null) return;
            BuildingPrivlidge tc = player.GetBuildingPrivilege();
            int matching, equipped;
            int level = EligibleLevel(player, out matching, out equipped);
            SendReply(player, string.Format("[TC Workbench] In TC: {0} | Authorized: {1} | Matched workbenches: {2} | Range-ready: {3} | Eligible level: {4} | Native craft level: {5}",
                tc != null, tc != null && tc.IsAuthed(player), matching, equipped, level, player.currentCraftLevel));
        }
        [ConsoleCommand("tcwb.debug")]
        private void Debug(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Connection.authLevel < 2) return;
            Scan();
            Puts("--- TCWorkbenchRange v1.2.0: " + benches.Count + " workbenches ---");
            foreach (Workbench bench in benches)
            {
                BuildingPrivlidge tc = bench.GetBuildingPrivilege();
                List<string> names = new List<string>();
                foreach (ItemContainer c in Containers(bench))
                    if (c.itemList != null)
                        foreach (Item item in c.itemList)
                            if (item != null && item.info != null) names.Add(item.info.shortname);
                Puts(string.Format("Workbench prefab={0}, position={1}, level={2}, TC={3}, rangeDetected={4}, inventory=[{5}]",
                    bench.ShortPrefabName, bench.transform.position, Level(bench), tc == null ? "none" : tc.net == null ? "no-net" : tc.net.ID.ToString(), HasUpgrade(bench), string.Join(",", names.ToArray())));
            }
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                int matching, upgraded;
                int level = EligibleLevel(player, out matching, out upgraded);
                float cache, expiry;
                bool hasCache = ReadNumber(player, "cachedCraftLevel", out cache);
                bool hasExpiry = ReadNumber(player, "nextCheckTime", out expiry);
                Puts(string.Format("Player {0}: matched={1}, eligible-benches={2}, level={3}, native={4}, craftCache={5}:{6}, expiry={7}:{8}",
                    player.displayName, matching, upgraded, level, player.currentCraftLevel, hasCache, cache, hasExpiry, expiry));
            }
        }
    }
}
