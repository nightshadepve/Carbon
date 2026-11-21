using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("VIPPerks", "Nightshade", "1.3.0")]
    [Description("VIP/VIPx AFK protection and locked crate timer reductions")]
    class VIPPerks : RustPlugin
    {
        // Permissions
        private const string PERM_VIP  = "vipperks.vip";
        private const string PERM_VIPX = "vipperks.vipx";

        // Crate unlock times (seconds)
        private const float CRATE_TIME_VIP  = 150f; // 2 min 30 s
        private const float CRATE_TIME_VIPX = 60f;  // 1 min

        // AFK tracking
        private class AfkData
        {
            public Vector3    LastPosition;
            public Quaternion LastRotation;
            public bool       IsAfk;
            public float      Health;
            public float      Hydration;
            public float      Food;
            // Cached refs - avoids FindByID calls inside repeating timers
            public BasePlayer Player;
            public Timer      FreezeTimer;
            public Timer      MovementTimer;
        }

        private readonly Dictionary<ulong, AfkData> _afkPlayers = new Dictionary<ulong, AfkData>();

        // Movement threshold - if the player moves more than this many units they exit AFK
        private const float MOVE_THRESHOLD = 0.2f;

        // Oxide hooks
        void Init()
        {
            permission.RegisterPermission(PERM_VIP,  this);
            permission.RegisterPermission(PERM_VIPX, this);
        }

        [ChatCommand("afk")]
        void CmdAfk(BasePlayer player, string command, string[] args)
        {
            if (!IsVip(player))
            {
                SendReply(player, "<color=#ff4444>You need VIP or VIPx to use AFK mode.</color>");
                return;
            }

            if (_afkPlayers.TryGetValue(player.userID, out var data) && data.IsAfk)
            {
                ExitAfk(player);
            }
            else
            {
                EnterAfk(player);
            }
        }

        // Detect movement to auto-exit AFK
        void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || !_afkPlayers.TryGetValue(player.userID, out var data) || !data.IsAfk)
                return;

            // Any WASD / jump / crouch input breaks AFK
            if (input.IsDown(BUTTON.FORWARD)  ||
                input.IsDown(BUTTON.BACKWARD) ||
                input.IsDown(BUTTON.LEFT)      ||
                input.IsDown(BUTTON.RIGHT)     ||
                input.IsDown(BUTTON.JUMP)      ||
                input.IsDown(BUTTON.DUCK))
            {
                ExitAfk(player);
            }
        }

        // Prevent idle kick while AFK
        object OnPlayerIdle(BasePlayer player)
        {
            if (_afkPlayers.TryGetValue(player.userID, out var data) && data.IsAfk)
                return false; // returning non-null cancels the kick
            return null;
        }

        // Clean up on disconnect
        void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            if (_afkPlayers.TryGetValue(player.userID, out var data))
            {
                data.FreezeTimer?.Destroy();
                data.MovementTimer?.Destroy();
                _afkPlayers.Remove(player.userID);
            }
        }

        // Hackable locked crate

        // Tracks which player initiated each crate hack (CanHackCrate has the player ref,
        // OnCrateHack does not - this bridges the gap).
        private readonly Dictionary<ulong, ulong> _crateHackers = new Dictionary<ulong, ulong>();

        // CanHackCrate fires just BEFORE hacking starts and includes the player.
        // We record who is hacking so OnCrateHack can look them up.
        object CanHackCrate(BasePlayer player, HackableLockedCrate crate)
        {
            if (player != null && crate != null)
                _crateHackers[crate.net.ID.Value] = player.userID;

            return null; // null = allow the hack to proceed normally
        }

        // OnCrateHack fires immediately after StartHacking() resets hackSeconds to 0.
        // hackSeconds = seconds already elapsed; to finish in `desired` seconds we set it
        // to (requiredHackSeconds - desired), so the progress counter reaches the threshold
        // after exactly `desired` more ticks.
        void OnCrateHack(HackableLockedCrate crate)
        {
            if (crate == null) return;

            ulong netId = crate.net.ID.Value;
            if (!_crateHackers.TryGetValue(netId, out ulong userId)) return;

            BasePlayer player = BasePlayer.FindByID(userId);
            if (player == null) return;

            float desired = GetCrateTime(player);
            if (desired >= HackableLockedCrate.requiredHackSeconds) return; // non-VIP

            float fastForward = HackableLockedCrate.requiredHackSeconds - desired;
            crate.hackSeconds = fastForward;

            // hackSeconds fast-forward is sufficient; the client UI self-corrects on next tick

            SendReply(player, "<color=#FFD700>VIP perk active - crate unlocks in <b>" + FormatTime(desired) + "</b>!</color>");
        }

        // Clean up the tracking dict when a crate is destroyed/looted
        void OnEntityKill(HackableLockedCrate crate)
        {
            if (crate?.net != null)
                _crateHackers.Remove(crate.net.ID.Value);
        }

        // AFK helpers
        private void EnterAfk(BasePlayer player)
        {
            var data = new AfkData
            {
                LastPosition  = player.transform.position,
                LastRotation  = player.transform.rotation,
                IsAfk         = true,
                Health        = player.health,
                Hydration     = player.metabolism.hydration.value,
                Food          = player.metabolism.calories.value,
                Player        = player,
            };

            // Per-player 1s freeze timer - only runs while THIS player is AFK
            data.FreezeTimer = timer.Every(1f, () =>
            {
                if (player == null || !player.IsConnected) { ExitAfk(player); return; }
                player.health = data.Health;
                player.metabolism.hydration.SetValue(data.Hydration);
                player.metabolism.calories.SetValue(data.Food);
                player.metabolism.SendChangesToClient();
            });

            // Per-player 0.5s position check - replaces the global OnPlayerTick hook
            data.MovementTimer = timer.Every(0.5f, () =>
            {
                if (player == null || !player.IsConnected) { ExitAfk(player); return; }
                if (Vector3.Distance(player.transform.position, data.LastPosition) > MOVE_THRESHOLD)
                    ExitAfk(player);
            });

            _afkPlayers[player.userID] = data;

            string tier = HasPermission(player, PERM_VIPX) ? "<color=#00BFFF>VIPx</color>" : "<color=#FFD700>VIP</color>";
            SendReply(player, tier + " <color=#aaffaa>AFK mode <b>enabled</b>. Your stats are frozen. Move to exit.</color>");
        }

        private void ExitAfk(BasePlayer player)
        {
            if (player == null) return;
            if (!_afkPlayers.TryGetValue(player.userID, out var data)) return;
            data.FreezeTimer?.Destroy();
            data.MovementTimer?.Destroy();
            _afkPlayers.Remove(player.userID);
            if (player.IsConnected)
                SendReply(player, "<color=#aaffaa>AFK mode <b>disabled</b>. Welcome back!</color>");
        }

        // Permission helpers
        private bool IsVip(BasePlayer player)
            => HasPermission(player, PERM_VIP) || HasPermission(player, PERM_VIPX);

        private bool HasPermission(BasePlayer player, string perm)
            => player != null && permission.UserHasPermission(player.UserIDString, perm);

        private float GetCrateTime(BasePlayer player)
        {
            if (HasPermission(player, PERM_VIPX)) return CRATE_TIME_VIPX;
            if (HasPermission(player, PERM_VIP))  return CRATE_TIME_VIP;
            return HackableLockedCrate.requiredHackSeconds; // default (15 min)
        }

        private string FormatTime(float seconds)
        {
            int m = (int)seconds / 60;
            int s = (int)seconds % 60;
            return m > 0 ? m + "m " + s.ToString("D2") + "s" : s + "s";
        }
    }
}
