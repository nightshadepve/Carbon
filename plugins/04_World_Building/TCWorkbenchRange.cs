using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("TCWorkbenchRange", "Nightshade", "1.3.0")]
    [Description("Gives authorized players the tier of a range-upgraded workbench anywhere inside that tool cupboard's building privilege.")]
    public class TCWorkbenchRange : RustPlugin
    {
        private const string PermissionUse = "tcworkbenchrange.use";
        private const string RangeShortname = "workbench.upgrade.range";
        private const string HoldName = "TCWorkbenchRange";

        private readonly List<Workbench> benches = new List<Workbench>();
        private readonly Dictionary<ulong, int> matchCountByPrivilege = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, int> rangeCountByPrivilege = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, int> bestLevelByPrivilege = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, Workbench> bestBenchByPrivilege = new Dictionary<ulong, Workbench>();
        private readonly Dictionary<ulong, TriggerWorkbench> holds = new Dictionary<ulong, TriggerWorkbench>();
        private readonly HashSet<ulong> granted = new HashSet<ulong>();
        private Settings settings;

        private class Settings
        {
            public float PlayerCheckSeconds = 0.2f;
            public float WorkbenchRefreshSeconds = 5f;
            public bool RequireRangeUpgrade = true;
            public bool RequireTCAuthorization = true;
            public bool RequirePermission = false;
            public bool DebugLogging = false;
        }

        protected override void LoadDefaultConfig()
        {
            settings = new Settings();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try { settings = Config.ReadObject<Settings>() ?? new Settings(); }
            catch { settings = new Settings(); }
            // InvalidateWorkbenchCache() zeros nextCheckTime whenever a trigger is entered or left.
            // The tick has to put the hold back before the native getter recomputes from the small box.
            settings.PlayerCheckSeconds = Mathf.Clamp(settings.PlayerCheckSeconds, 0.1f, 0.25f);
            settings.WorkbenchRefreshSeconds = Mathf.Max(settings.WorkbenchRefreshSeconds, 2f);
            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(settings, true);
        }

        private void Init()
        {
            permission.RegisterPermission(PermissionUse, this);
        }

        private void OnServerInitialized()
        {
            Scan();
            timer.Every(settings.WorkbenchRefreshSeconds, Scan);
            timer.Every(settings.PlayerCheckSeconds, TickPlayers);
            NextTick(TickPlayers);
        }

        private void Unload()
        {
            List<BasePlayer> players = new List<BasePlayer>();
            foreach (KeyValuePair<ulong, TriggerWorkbench> pair in holds)
            {
                TriggerWorkbench hold = pair.Value;
                if (hold == null) continue;
                BasePlayer player = hold.GetComponentInParent<BasePlayer>();
                if (player != null) players.Add(player);
            }
            for (int i = 0; i < players.Count; i++) Clear(players[i]);
            foreach (BasePlayer player in BasePlayer.activePlayerList)
                if (player != null) Clear(player);
            foreach (KeyValuePair<ulong, TriggerWorkbench> pair in holds)
                if (pair.Value != null) UnityEngine.Object.Destroy(pair.Value.gameObject);
            holds.Clear();
            granted.Clear();
            benches.Clear();
        }

        private void OnEntitySpawned(Workbench workbench)
        {
            timer.Once(1f, Scan);
        }

        private void OnEntityKill(Workbench workbench)
        {
            timer.Once(0.1f, Scan);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            Clear(player);
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

        private void RebuildPrivilegeIndex()
        {
            matchCountByPrivilege.Clear();
            rangeCountByPrivilege.Clear();
            bestLevelByPrivilege.Clear();
            bestBenchByPrivilege.Clear();
            for (int i = 0; i < benches.Count; i++)
            {
                Workbench bench = benches[i];
                if (bench == null || bench.IsDestroyed) continue;
                ulong privilegeId = PrivilegeId(bench.GetBuildingPrivilege());
                if (privilegeId == 0) continue;
                int matches;
                matchCountByPrivilege.TryGetValue(privilegeId, out matches);
                matchCountByPrivilege[privilegeId] = matches + 1;
                if (settings.RequireRangeUpgrade && !HasRangeUpgrade(bench)) continue;
                int ranges;
                rangeCountByPrivilege.TryGetValue(privilegeId, out ranges);
                rangeCountByPrivilege[privilegeId] = ranges + 1;
                int level = Level(bench);
                int best;
                if (!bestLevelByPrivilege.TryGetValue(privilegeId, out best) || level > best)
                {
                    bestLevelByPrivilege[privilegeId] = level;
                    bestBenchByPrivilege[privilegeId] = bench;
                }
            }
        }

        private static ulong PrivilegeId(BuildingPrivlidge privilege)
        {
            if (privilege == null || privilege.IsDestroyed || privilege.net == null) return 0;
            return privilege.net.ID.Value;
        }

        private static int Level(Workbench bench)
        {
            if (bench == null) return 0;
            int level = bench.Workbenchlevel;
            if (level >= 1 && level <= 3) return level;
            string prefab = ((bench.ShortPrefabName ?? "") + " " + (bench.PrefabName ?? "")).ToLowerInvariant();
            if (prefab.Contains("workbench3") || prefab.Contains("workbench_3") || prefab.Contains("workbench.level3")) return 3;
            if (prefab.Contains("workbench2") || prefab.Contains("workbench_2") || prefab.Contains("workbench.level2")) return 2;
            if (prefab.Contains("workbench1") || prefab.Contains("workbench_1") || prefab.Contains("workbench.level1")) return 1;
            return 0;
        }

        // The range add-on is an ItemModWorkbenchRange in an upgrade slot. Its shortname is
        // workbench.upgrade.range on this server; the component is what the game actually installs.
        private static bool HasRangeUpgrade(Workbench bench)
        {
            if (bench == null || bench.IsDestroyed) return false;
            if (bench.cachedServerUpgrades != null)
            {
                for (int i = 0; i < bench.cachedServerUpgrades.Count; i++)
                {
                    ItemModWorkbenchUpgrade mod = bench.cachedServerUpgrades[i].mod;
                    if (mod is ItemModWorkbenchRange) return true;
                }
            }
            ItemContainer inventory = bench.inventory;
            if (inventory == null || inventory.itemList == null) return false;
            for (int i = 0; i < inventory.itemList.Count; i++)
            {
                Item item = inventory.itemList[i];
                if (item == null || item.info == null) continue;
                if (item.info.shortname == RangeShortname) return true;
                if (item.info.GetComponent<ItemModWorkbenchRange>() != null) return true;
            }
            return false;
        }

        private int EligibleLevel(BasePlayer player, out int matching, out int upgraded, out Workbench bestBench)
        {
            matching = 0;
            upgraded = 0;
            bestBench = null;
            BuildingPrivlidge privilege = player.GetBuildingPrivilege();
            ulong privilegeId = PrivilegeId(privilege);
            if (privilegeId == 0) return 0;
            matchCountByPrivilege.TryGetValue(privilegeId, out matching);
            rangeCountByPrivilege.TryGetValue(privilegeId, out upgraded);
            int level;
            bestLevelByPrivilege.TryGetValue(privilegeId, out level);
            bestBenchByPrivilege.TryGetValue(privilegeId, out bestBench);
            if (settings.RequireTCAuthorization && !privilege.IsAuthed(player)) return 0;
            if (settings.RequirePermission && !permission.UserHasPermission(player.UserIDString, PermissionUse)) return 0;
            if (level <= 0 || bestBench == null) return 0;
            return level;
        }

        private void TickPlayers()
        {
            RebuildPrivilegeIndex();
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null) continue;
                if (!player.IsConnected || player.IsDead() || player.IsSleeping())
                {
                    Clear(player);
                    continue;
                }
                int matching, upgraded;
                Workbench bench;
                int bonus = EligibleLevel(player, out matching, out upgraded, out bench);
                if (bonus <= 0 || bench == null || bench.IsDestroyed) Clear(player);
                else Apply(player, bench, bonus);
            }
        }

        // Vanilla ItemModWorkbenchRange only multiplies the workbench TriggerWorkbench box
        // (default x2) inside Workbench.RefreshCraftRange. currentCraftLevel then ignores
        // cachedCraftLevel whenever triggers is null, and otherwise recomputes from those
        // boxes with a line-of-sight check once nextCheckTime passes. Holding nextCheckTime
        // at MaxValue, with at least one trigger in the list, is what makes the getter
        // return the TC tier. UpdateWorkbenchFlags copies that into the Workbench1/2/3 flags.
        private void Apply(BasePlayer player, Workbench bench, int bonus)
        {
            ulong id = player.userID;
            TriggerWorkbench hold;
            holds.TryGetValue(id, out hold);
            if (hold == null)
            {
                GameObject marker = new GameObject(HoldName);
                marker.hideFlags = HideFlags.HideAndDontSave;
                marker.transform.SetParent(player.transform, false);
                hold = marker.AddComponent<TriggerWorkbench>();
                holds[id] = hold;
            }
            hold.parentBench = bench;
            StripDeadTriggers(player, hold);
            if (player.triggers == null || !player.triggers.Contains(hold)) player.EnterTrigger(hold);

            Workbench foreignBench;
            float foreign = ForeignLevel(player, hold, out foreignBench);
            float effective = Mathf.Max(bonus, foreign);
            player.cachedCraftLevel = effective;
            player._cachedWorkbench = effective > bonus && foreignBench != null ? foreignBench : bench;
            player.nextCheckTime = float.MaxValue;
            if (player.metabolism != null) player.metabolism.UpdateWorkbenchFlags();
            player.nextCheckTime = float.MaxValue;

            if (granted.Add(id) && settings.DebugLogging)
                Puts(string.Format("TC craft hold for {0}: level {1} from {2} at {3}.", player.displayName, bonus, bench.ShortPrefabName, bench.transform.position));
        }

        private static void StripDeadTriggers(BasePlayer player, TriggerWorkbench current)
        {
            if (player.triggers == null) return;
            for (int i = player.triggers.Count - 1; i >= 0; i--)
            {
                TriggerBase trigger = player.triggers[i];
                if (trigger == null)
                {
                    player.triggers.RemoveAt(i);
                    continue;
                }
                if (trigger == current || trigger.gameObject == player.gameObject) continue;
                if (trigger.gameObject != null && trigger.gameObject.name == HoldName)
                {
                    player.triggers.RemoveAt(i);
                    UnityEngine.Object.Destroy(trigger.gameObject);
                }
            }
        }

        private static float ForeignLevel(BasePlayer player, TriggerWorkbench ours, out Workbench bench)
        {
            bench = null;
            float best = 0f;
            if (player == null || player.triggers == null) return 0f;
            for (int i = 0; i < player.triggers.Count; i++)
            {
                TriggerWorkbench trigger = player.triggers[i] as TriggerWorkbench;
                if (trigger == null || trigger == ours) continue;
                Workbench parent = trigger.parentBench;
                if (parent == null || parent.IsDestroyed) continue;
                float level = trigger.WorkbenchLevel();
                if (level <= best) continue;
                best = level;
                bench = parent;
            }
            return best;
        }

        private void Clear(BasePlayer player)
        {
            if (player == null) return;
            ulong id = player.userID;
            TriggerWorkbench hold;
            bool hadHold = holds.TryGetValue(id, out hold);
            if (hadHold)
            {
                holds.Remove(id);
                if (hold != null)
                {
                    player.LeaveTrigger(hold);
                    UnityEngine.Object.Destroy(hold.gameObject);
                }
            }
            if (!granted.Remove(id) && !hadHold) return;
            player.nextCheckTime = 0f;
            if (player.metabolism != null) player.metabolism.UpdateWorkbenchFlags();
            if (settings != null && settings.DebugLogging)
                Puts(string.Format("TC craft hold cleared for {0}.", player.displayName));
        }

        [ChatCommand("tcwb")]
        private void ChatStatus(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            Scan();
            RebuildPrivilegeIndex();
            BuildingPrivlidge privilege = player.GetBuildingPrivilege();
            int matching, upgraded;
            Workbench bench;
            int level = EligibleLevel(player, out matching, out upgraded, out bench);
            if (!player.IsDead() && !player.IsSleeping())
            {
                if (level > 0 && bench != null && !bench.IsDestroyed) Apply(player, bench, level);
                else Clear(player);
            }
            bool held = holds.ContainsKey(player.userID);
            SendReply(player, string.Format("[TC Workbench] In TC: {0} | Authorized: {1} | Matched workbenches: {2} | Range-ready: {3} | Eligible level: {4} | Craft level: {5} | Held: {6}",
                privilege != null, privilege != null && privilege.IsAuthed(player), matching, upgraded, level, player.currentCraftLevel, held));
        }

        [ConsoleCommand("tcwb.debug")]
        private void Debug(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Connection.authLevel < 2) return;
            Scan();
            RebuildPrivilegeIndex();
            Puts("--- TCWorkbenchRange v1.3.0: " + benches.Count + " workbenches ---");
            for (int i = 0; i < benches.Count; i++)
            {
                Workbench bench = benches[i];
                if (bench == null || bench.IsDestroyed) continue;
                BuildingPrivlidge privilege = bench.GetBuildingPrivilege();
                Puts(string.Format("Workbench prefab={0}, position={1}, level={2}, TC={3}, rangeMod={4}, rangeMultiplier={5}",
                    bench.ShortPrefabName, bench.transform.position, Level(bench),
                    privilege == null || privilege.net == null ? "none" : privilege.net.ID.Value.ToString(),
                    HasRangeUpgrade(bench), bench.GetRangeMultiplier()));
            }
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null) continue;
                int matching, upgraded;
                Workbench bench;
                int level = EligibleLevel(player, out matching, out upgraded, out bench);
                TriggerWorkbench hold;
                bool held = holds.TryGetValue(player.userID, out hold) && hold != null;
                Puts(string.Format("Player {0}: matched={1}, range-ready={2}, level={3}, craft={4}, cache={5}, nextCheck={6}, held={7}, triggers={8}",
                    player.displayName, matching, upgraded, level, player.currentCraftLevel, player.cachedCraftLevel,
                    player.nextCheckTime >= float.MaxValue ? "held" : player.nextCheckTime.ToString(),
                    held, player.triggers == null ? 0 : player.triggers.Count));
            }
        }
    }
}
