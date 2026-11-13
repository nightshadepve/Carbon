using System;
using System.Collections.Generic;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Linq;

namespace Oxide.Plugins
{
    [Info("Monument Loot Notifier", "JackMachine", "1.0.1")]
    [Description(
        "Notifies the server when a player loots items in a monument, retrieves an airdrop, or hacks a locked crate.")]
    public class MonumentLootNotifier : RustPlugin
    {
        #region Fields

        private float _displayDuration = 30f; // For the show command

        private bool _discordRateLimited = false;
        private float _discordCooldownUntil = 0f;
        private Queue<string> _discordQueue = new Queue<string>();

        private string DiscordWebhookUrl; // Remplace avec ton vrai lien
        private bool _trackLockedCrates;
        private bool _trackAirdrops;

        private string lootMessage;
        private string _dropMessage;
        private string _hackMessage;
        private int _lootThreshold;
        private bool _enableDiscord = false;
        private string _webhookURL;

        private Dictionary<ulong, PlayerMonumentData> _playerLootedMonuments;
        private Dictionary<ulong, CustomMonument> _monumentSessions = new();
        private Dictionary<ulong, NetworkableId> _lastNotifiedContainer = new();
        private readonly Dictionary<NetworkableId, ulong> _crateHackers = new();

        private List<CustomMonument> _customMonuments;
        private readonly HashSet<SphereEntity> _spheres = new HashSet<SphereEntity>();

        private string _permissionExcludeTracking = "monumentlootnotifier.exclude.monuments";
        private string _permissionExcludeDrops = "monumentlootnotifier.exclude.drops";
        private string _permissionExcludeLockedCrates = "monumentlootnotifier.exclude.lockedcrates";
        private string _permissionCreateCustomMonument = "monumentlootnotifier.custommonument.create";

        #endregion

        #region Configuration

        protected override void LoadDefaultConfig()
        {
            Config["LootThreshold"] = Config["LootThreshold"] ?? 3;
            Config["TrackLockedCrates"] = Config["TrackLockedCrates"] ?? true;
            Config["TrackAirdrops"] = Config["TrackAirdrops"] ?? true;
            Config["MapPreset"] = Config["MapPreset"] ?? "";
            Config["EnableDiscord"] = Config["EnableDiscord"] ?? false;
            Config["DiscordWebhookUrl"] = Config["DiscordWebhookUrl"] ?? "https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks";
            SaveConfig();
        }

        private void SetPermissions()
        {
            permission.RegisterPermission(_permissionExcludeTracking, this);
            permission.RegisterPermission(_permissionExcludeDrops, this);
            permission.RegisterPermission(_permissionExcludeLockedCrates, this);
            permission.RegisterPermission(_permissionCreateCustomMonument, this);
        }

        private void LoadMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                { "DefaultLootMessage", "{0} is looting <color=yellow>{1}</color>" },
                { "CargoShipLootMessage", "{0} is looting <color=yellow>{1}</color> at {2}" },
                { "AbandonedSupermarketLootMessage", "{0} is looting <color=yellow>{1}</color> at {2}" },
                { "OxumsGasStationLootMessage", "{0} is looting <color=yellow>{1}</color> at {2}" },
                { "AirdropMessage", "{0} has looted a supply drop at {1}" },
                { "HackMessage", "{0} is hacking a locked crate at {1}" }
            }, this);
        }

        #endregion

        #region Oxide Hooks

        private void Init()
        {
            LoadDefaultConfig();
            SetPermissions();

            _lootThreshold = Convert.ToInt32(Config["LootThreshold"]);
            _trackLockedCrates = Convert.ToBoolean(Config["TrackLockedCrates"]);
            _trackAirdrops = Convert.ToBoolean(Config["TrackAirdrops"]);
            _enableDiscord = Convert.ToBoolean(Config["EnableDiscord"]);
            _webhookURL = Config["DiscordWebhookUrl"]?.ToString();
            
            if (!_enableDiscord || string.IsNullOrWhiteSpace(_webhookURL) ||
                _webhookURL.Contains("support.discord.com") ||
                !_webhookURL.StartsWith("https://discord.com/api/webhooks/"))
            {
                _enableDiscord = false;
                _webhookURL = null;
                PrintWarning("[MonumentLootNotifier] Discord integration disabled.");
            }
            
            if (string.IsNullOrEmpty(_webhookURL))
            {
                Puts("Discord webhook URL is not set. Notifications will not be sent to Discord.");
            }

            _playerLootedMonuments = new Dictionary<ulong, PlayerMonumentData>();

            LoadMessages();
            PlayerMonumentData.LootMessageTemplate = lang.GetMessage("DefaultLootMessage", this);
            _dropMessage = lang.GetMessage("DropMessage", this);
            _hackMessage = lang.GetMessage("HackMessage", this);

            _customMonuments = new List<CustomMonument>();
            _monumentSessions = new Dictionary<ulong, CustomMonument>();
            LoadCustomMonuments();
        }

        private void OnLootEntity(BasePlayer looter, LootContainer container)
        {
            if (looter == null || container == null) return;
            if (permission.UserHasPermission(looter.UserIDString, _permissionExcludeDrops)) return;

            if (container.net == null) return;
            if (_lastNotifiedContainer.TryGetValue(looter.userID, out var lastContainerId) &&
                lastContainerId == container.net.ID)
            {
                return; // Skip notification if it's the same container
            }

            _lastNotifiedContainer[looter.userID] = container.net.ID;

            // air drop
            if (container.prefabID == 3632568684 && _trackAirdrops)
            {
                string mapCoordinates = GetMapCoordinates(container.transform.position);
                string message = string.Format(lang.GetMessage("AirdropMessage", this), looter.displayName,
                    mapCoordinates);
                SendChatAndDiscord(message);
                return;
            }

            HandleMonumentLoot(looter, container);
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return;

            if (entity.ShortPrefabName.Contains("barrel") && info.InitiatorPlayer != null)
            {
                if (permission.UserHasPermission(info.InitiatorPlayer.UserIDString, _permissionExcludeTracking)) return;

                HandleMonumentLoot(info.InitiatorPlayer, entity);
            }
        }

        private object CanHackCrate(BasePlayer player, HackableLockedCrate crate)
        {
            if (player == null || crate == null || crate.net == null)
                return null;

            _crateHackers[crate.net.ID] = player.userID;

            return null;
        }

        private void OnCrateHack(HackableLockedCrate crate)
        {
            if (!_trackLockedCrates || crate == null || crate.net == null)
                return;

            if (!_crateHackers.TryGetValue(crate.net.ID, out ulong playerId))
            {
                PrintWarning($"Unable to determine hacker for locked crate at {GetMapCoordinates(crate.transform.position)}");
                return;
            }

            BasePlayer player = BasePlayer.FindByID(playerId);

            if (player == null)
            {
                _crateHackers.Remove(crate.net.ID);
                return;
            }

            if (permission.UserHasPermission(
                    player.UserIDString,
                    _permissionExcludeLockedCrates))
            {
                _crateHackers.Remove(crate.net.ID);
                return;
            }

            string message = string.Format(
                _hackMessage,
                player.displayName,
                GetMapCoordinates(crate.transform.position)
            );

            SendChatAndDiscord(message);

            _crateHackers.Remove(crate.net.ID);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;

            _playerLootedMonuments.Remove(player.userID);
            _lastNotifiedContainer.Remove(player.userID);
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            var crate = entity as HackableLockedCrate;
            if (crate?.net == null)
                return;

            _crateHackers.Remove(crate.net.ID);
        }
        #endregion

        #region Helpers

        private void HandleMonumentLoot(BasePlayer looter, BaseEntity container)
        {
            var parentEntity = container.transform.parent?.GetComponent<BaseEntity>();
            string monumentName = GetMonumentName(container.transform.position, parentEntity);

            if (string.IsNullOrEmpty(monumentName))
            {
                _playerLootedMonuments.Remove(looter.userID);
                return;
            }

            if (!_playerLootedMonuments.TryGetValue(looter.userID, out var monumentData) ||
                monumentData.MonumentName != monumentName)
            {
                _playerLootedMonuments[looter.userID] =
                    new PlayerMonumentData(looter.userID, monumentName, _lootThreshold);
            }

            _playerLootedMonuments[looter.userID].OnLoot(container);
            if (_playerLootedMonuments[looter.userID].HasToNotify()) NotifyPlayer(looter);
        }

        private void NotifyPlayer(BasePlayer looter)
        {
            string message = _playerLootedMonuments[looter.userID].GetLootMessage(looter.displayName, this);
            SendChatAndDiscord(message);
        }

        private string GetMonumentName(Vector3 position, BaseEntity parentEntity)
        {
            foreach (var monument in TerrainMeta.Path.Monuments)
            {
                if (monument.IsInBounds(position))
                {
                    return monument.displayPhrase.english;
                }
            }

            foreach (var customMonument in _customMonuments)
            {
                if (customMonument.Contains(position))
                {
                    return customMonument.Name;
                }
            }

            if (parentEntity != null && parentEntity.prefabID == 3234960997) return "Cargo Ship";

            return string.Empty;
        }

        private string GetMapCoordinates(Vector3 position)
        {
            return MapHelper.PositionToString(position);
        }

        private string GetMapFriendlyName()
        {
            string friendlyName = Config["MapPreset"]?.ToString();
            if (!string.IsNullOrEmpty(friendlyName))
            {
                return friendlyName;
            }

            // Fallback si aucun nom n'est défini
            string mapKey = $"{World.Seed}_{World.Size}";
            Puts($"No friendly name defined in config. Using default map key: {mapKey}");
            return mapKey;
        }

        private void LoadCustomMonuments()
        {
            string friendlyName = GetMapFriendlyName();
            string filePath = $"MonumentLootNotifier/{friendlyName}";

            _customMonuments = Interface.Oxide.DataFileSystem.ReadObject<List<CustomMonument>>(filePath) ??
                               new List<CustomMonument>();

            Puts($"Loaded {_customMonuments.Count} custom monuments from {filePath}");
        }

        private void SaveCustomMonuments()
        {
            string friendlyName = GetMapFriendlyName();
            string filePath = $"MonumentLootNotifier/{friendlyName}";

            // Create a list of simplified monuments for serialization
            var serializedMonuments = _customMonuments.Select(m => new
            {
                m.Name,
                m.Position,
                m.Extents,
                RotationEuler = m.Rotation.eulerAngles,
                m.CreationPoints
            }).ToList();

            Interface.Oxide.DataFileSystem.WriteObject(filePath, serializedMonuments);

            Puts($"Saved {_customMonuments.Count} custom monuments to {filePath}");
        }

        private void DrawBoundingBoxLines(BasePlayer player, CustomMonument monument, Color color, float duration,
            float sphereSize, float spacing)
        {
            var extents = monument.Extents;
            var center = monument.Position;
            var rotation = monument.Bounds.rotation;

            Vector3[] localCorners = new Vector3[8]
            {
                new Vector3(-extents.x, -extents.y, -extents.z),
                new Vector3(extents.x, -extents.y, -extents.z),
                new Vector3(-extents.x, -extents.y, extents.z),
                new Vector3(extents.x, -extents.y, extents.z),
                new Vector3(-extents.x, extents.y, -extents.z),
                new Vector3(extents.x, extents.y, -extents.z),
                new Vector3(-extents.x, extents.y, extents.z),
                new Vector3(extents.x, extents.y, extents.z)
            };

            Vector3[] worldCorners = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                worldCorners[i] = center + rotation * localCorners[i];
            }

            int[,] edges =
            {
                { 0, 1 }, { 1, 3 }, { 3, 2 }, { 2, 0 }, // Bottom edges
                { 4, 5 }, { 5, 7 }, { 7, 6 }, { 6, 4 }, // Top edges
                { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } // Vertical edges
            };

            player.SendConsoleCommand("ddraw.sphere", _displayDuration, Color.red, monument.CalculatedPoints[0],
                0.8f); // A
            player.SendConsoleCommand("ddraw.sphere", _displayDuration, Color.red, monument.CalculatedPoints[1],
                0.8f); // B2
            player.SendConsoleCommand("ddraw.sphere", _displayDuration, Color.red, monument.CalculatedPoints[2],
                0.8f); // C2
            player.SendConsoleCommand("ddraw.sphere", _displayDuration, Color.yellow, monument.CreationPoints[1],
                0.5f); // B
            player.SendConsoleCommand("ddraw.sphere", _displayDuration, Color.yellow, monument.CreationPoints[2],
                0.5f); // C

            player.SendConsoleCommand("ddraw.sphere", _displayDuration, Color.magenta, center, sphereSize);

            for (int i = 0; i < edges.GetLength(0); i++)
            {
                Vector3 start = worldCorners[edges[i, 0]];
                Vector3 end = worldCorners[edges[i, 1]];

                DrawPrefabLine(player, start, end, 2.0f, _displayDuration);
            }
        }

        private void CompleteBoundingBox(BasePlayer player, CustomMonument session)
        {
            if (session.CreationPoints.Count != 3)
            {
                player.ChatMessage("Error: Not enough points to define the bounding box.");
                return;
            }

            _customMonuments.Add(session);
            SaveCustomMonuments();

            _monumentSessions.Remove(player.userID);
        }

        private void DrawPrefabLine(BasePlayer player, Vector3 start, Vector3 end, float spacing, float duration)
        {
            var prefabPath = "assets/prefabs/misc/chinesenewyear/chineselantern/chineselantern_white.deployed.prefab";
            float distance = Vector3.Distance(start, end);
            Vector3 direction = (end - start).normalized;

            int prefabCount = Mathf.FloorToInt(distance / spacing);

            for (int i = 0; i <= prefabCount; i++)
            {
                Vector3 position = start + direction * (i * spacing);

                var prefab = GameManager.server.CreateEntity(prefabPath, position + Vector3.up * 0.4f);
                prefab.enableSaving = false;

                if (prefab != null)
                {
                    prefab.Spawn();
                    timer.Once(duration, () => { prefab.Kill(); });
                }
            }
        }

        private void SendToDiscord(string message)
        {
            if (!_enableDiscord || string.IsNullOrEmpty(_webhookURL)) return;
            
            message = $"📦 {StripRichText(message)}";
            
            // Si rate limité, on met le message en file
            if (_discordRateLimited || Time.realtimeSinceStartup < _discordCooldownUntil)
            {
                _discordQueue.Enqueue(message);
                return;
            }

            var payload = new
            {
                content = message
            };

            webrequest.Enqueue(
                _webhookURL,
                JsonConvert.SerializeObject(payload),
                (code, response) =>
                {
                    if (code == 200 || code == 204)
                    {
                        // Succès, rien à faire
                    }
                    else if (code == 429 && !string.IsNullOrEmpty(response))
                    {
                        // Rate limit, on lit le retry_after
                        try
                        {
                            var json = JObject.Parse(response);
                            float retryAfter = json["retry_after"]?.Value<float>() ?? 1f;
                            _discordRateLimited = true;

                            PrintWarning($"[Discord] Rate limited. Retrying after {retryAfter} sec");

                            timer.In(retryAfter, () =>
                            {
                                _discordRateLimited = false;
                                TrySendNextDiscordMessage();
                            });
                        }
                        catch
                        {
                            PrintWarning("[Discord] Failed to parse retry_after from 429 response.");
                        }
                    }
                    else
                    {
                        PrintWarning($"[Discord] Error {code}: {response}");
                    }

                    // Petite pause entre les envois (protection contre le spam)
                    _discordCooldownUntil = Time.realtimeSinceStartup + 2f;
                    timer.In(2f, TrySendNextDiscordMessage);
                },
                this,
                Core.Libraries.RequestMethod.POST,
                new Dictionary<string, string> { ["Content-Type"] = "application/json" }
            );
        }

        private void TrySendNextDiscordMessage()
        {
            if (_discordQueue.Count == 0 || _discordRateLimited || Time.realtimeSinceStartup < _discordCooldownUntil)
                return;

            var next = _discordQueue.Dequeue();
            SendToDiscord(next);
        }

        void SendChatAndDiscord(string message)
        {
            PrintToChat(message);
            SendToDiscord(ToDiscordMarkdown(message));
        }
        
        private string StripRichText(string input)
        {
            return System.Text.RegularExpressions.Regex.Replace(input, "<.*?>", string.Empty);
        }

        private string ToDiscordMarkdown(string richText)
        {
            return System.Text.RegularExpressions.Regex.Replace(
                richText,
                "<color=\\w+>(.*?)</color>",
                "***$1***"
            );
        }
        
        #endregion

        #region Commands

        private bool hasCustomMonumentPermission(BasePlayer player)
        {
            if (!permission.UserHasPermission(player.UserIDString, _permissionCreateCustomMonument))
            {
                player.ChatMessage("You do not have permission to do this.");
                return false;
            }

            return true;
        }

        [ChatCommand("monument_add")]
        private void AddMonumentStartCommand(BasePlayer player, string command, string[] args)
        {
            if (!hasCustomMonumentPermission(player)) return;

            if (args.Length != 1)
            {
                player.ChatMessage("Usage: /monument_add <name>");
                return;
            }

            string name = args[0];
            if (_monumentSessions.ContainsKey(player.userID))
            {
                player.ChatMessage(
                    "You are already creating a monument. Use <color=yellow>/add_point</color> to continue.");
                return;
            }

            _monumentSessions[player.userID] = new CustomMonument(name);
            player.ChatMessage(
                $"Monument creation started for '{name}'. Move to the <color=yellow>top left</color> corner and type  <color=yellow>/add_point</color>.");
        }

        [ChatCommand("add_point")]
        private void AddPointCommand(BasePlayer player, string command, string[] args)
        {
            if (!hasCustomMonumentPermission(player)) return;


            if (!_monumentSessions.TryGetValue(player.userID, out var session))
            {
                player.ChatMessage(
                    "You haven't started a monument creation session. Use <color=yellow>/monument_add <name></color> first.");
                return;
            }

            session.CreationPoints.Add(player.transform.position);
            int pointCount = session.CreationPoints.Count;

            if (pointCount == 1)
            {
                player.ChatMessage(
                    "First point defined. Move to the <color=yellow>top right</color> corner and type <color=yellow>/add_point</color>.");
            }
            else if (pointCount == 2)
            {
                player.ChatMessage(
                    "Second point defined. Move to the <color=yellow>bottom right</color> and type <color=yellow>/add_point</color>.");
            }
            else if (pointCount == 3)
            {
                player.ChatMessage($"Bounding box completed for <color=yellow>'{session.Name}'</color>.");
                var name = session.Name;

                session.CalculateBox();


                CompleteBoundingBox(player, session);
                var monument = _customMonuments.Find(m => m.Name == name);
                DrawBoundingBoxLines(player, monument, Color.green, _displayDuration, 0.5f, 2.0f);
            }
            else
            {
                player.ChatMessage("You already defined the bounding box. Use /monument_add to restart.");
            }
        }

        [ChatCommand("monument_cancel")]
        private void CancelMonumentCommand(BasePlayer player, string command, string[] args)
        {
            if (!hasCustomMonumentPermission(player)) return;

            if (_monumentSessions.Remove(player.userID))
            {
                player.ChatMessage("Monument creation session canceled.");
            }
            else
            {
                player.ChatMessage("No active monument creation session found.");
            }
        }

        [ChatCommand("monuments_list")]
        private void ListMonumentsCommand(BasePlayer player, string command, string[] args)
        {
            if (!hasCustomMonumentPermission(player)) return;

            if (_customMonuments.Count == 0)
            {
                player.ChatMessage("No custom monuments defined.");
                return;
            }

            foreach (var monument in _customMonuments)
            {
                player.ChatMessage(
                    $"Name: {monument.Name}, Center: {monument.Bounds.position}, Size: {monument.Bounds.extents * 2}");
            }
        }

        [ChatCommand("monuments_show")]
        private void ShowMonumentsCommand(BasePlayer player, string command, string[] args)
        {
            if (!hasCustomMonumentPermission(player)) return;

            foreach (var monument in _customMonuments)
            {
                DrawBoundingBoxLines(player, monument, Color.green, _displayDuration, 0.5f, 2.0f);
            }

            player.ChatMessage("Showing all custom monuments bounding box.");
        }

        #endregion

        #region Classes

        private class PlayerMonumentData
        {
            public ulong PlayerId { get; private set; }
            public string MonumentName { get; }
            public int LootCount { get; private set; }
            public Vector3 LastLootPosition;
            private int _lootThreshold;

            public static string LootMessageTemplate = "{0} is looting <color=yellow>'{1}'</color>";

            public PlayerMonumentData(ulong playerId, string monument, int lootThreshold = 3)
            {
                PlayerId = playerId;
                MonumentName = monument;
                _lootThreshold = lootThreshold;
            }

            public void OnLoot(BaseEntity container)
            {
                LootCount++;
                LastLootPosition = container.transform.position;
            }

            public bool HasToNotify()
            {
                return LootCount == _lootThreshold;
            }

            public string GetLootMessage(string playerName, MonumentLootNotifier plugin)
            {
                string lootMessageKey = MonumentName switch
                {
                    "Cargo Ship" => "CargoShipLootMessage",
                    "Abandoned Supermarket" => "AbandonedSupermarketLootMessage",
                    "Oxum's Gas Station" => "OxumsGasStationLootMessage",
                    _ => "DefaultLootMessage"
                };

                return string.Format(plugin.lang.GetMessage(lootMessageKey, plugin), playerName, MonumentName,
                    MapHelper.PositionToString(LastLootPosition));
            }
        }

        private class CustomMonument
        {
            public string Name { get; set; }

            public List<Vector3> CreationPoints { get; set; }
            private List<Vector3> _calculatedPoints;

            public Vector3 Position { get; set; }
            public Vector3 Extents { get; set; }
            public Vector3 RotationEuler { get; set; }

            [Newtonsoft.Json.JsonIgnore]
            public OBB Bounds => new OBB(Position, Extents * 2, Quaternion.Euler(RotationEuler));

            public CustomMonument(string name, Vector3 position, Vector3 extents, Quaternion rotation)
            {
                Name = name;
                Position = position;
                Extents = extents;
                RotationEuler = rotation.eulerAngles;
                CreationPoints = new List<Vector3>();
                CalculatedPoints = new List<Vector3>();
            }

            public CustomMonument(string name)
            {
                Name = name;
                CreationPoints = new List<Vector3>();
                CalculatedPoints = new List<Vector3>();
            }

            [Newtonsoft.Json.JsonConstructor]
            public CustomMonument(string name, Vector3 position, Vector3 extents, Vector3 rotationEuler)
            {
                Name = name;
                Position = position;
                Extents = extents;
                RotationEuler = rotationEuler;
                CreationPoints = new List<Vector3>();
                CalculatedPoints = new List<Vector3>();
            }

            public bool Contains(Vector3 point)
            {
                return Bounds.Contains(point);
            }

            public void CalculateBox()
            {
                if (CreationPoints.Count != 3)
                {
                    throw new InvalidOperationException("Three points (A, B, C) are required to calculate the box.");
                }

                Vector3 pointA = CreationPoints[0];
                Vector3 pointB = CreationPoints[1];
                Vector3 pointC = CreationPoints[2];

                Vector3 pointB2 = new Vector3(pointB.x, pointA.y, pointB.z);

                Vector3 vectorAB2 = pointB2 - pointA;

                Vector3 perpToAB2 = new Vector3(-vectorAB2.z, 0, vectorAB2.x).normalized;

                Vector3 vectorBC = pointC - pointB2;
                float projectionLength = Vector3.Dot(vectorBC, perpToAB2);
                Vector3 projectedXZ = pointB2 + perpToAB2 * projectionLength;

                Vector3 pointC2 = new Vector3(projectedXZ.x, pointC.y, projectedXZ.z);

                _calculatedPoints.Clear();
                _calculatedPoints.Add(pointA);
                _calculatedPoints.Add(pointB2);
                _calculatedPoints.Add(pointC2);

                RotationEuler = (Rotation * Quaternion.Euler(0, 90f, 0)).eulerAngles;
                Extents = new Vector3(Width / 2, Height / 2, Depth / 2);
                Position = Center;
            }

            public List<Vector3> CalculatedPoints
            {
                get
                {
                    if (_calculatedPoints == null || _calculatedPoints.Count == 0) CalculateBox();

                    return _calculatedPoints;
                }

                private set => _calculatedPoints = value;
            }

            public Vector3 Center
            {
                get
                {
                    Vector3 pointA = CalculatedPoints[0];
                    Vector3 pointC2 = CalculatedPoints[2];
                    return (pointA + pointC2) / 2;
                }
            }

            public Quaternion Rotation
            {
                get
                {
                    if (CalculatedPoints.Count != 3)
                    {
                        throw new InvalidOperationException(
                            "CalculatedPoints must contain exactly 3 points (A, B, C2).");
                    }

                    Vector3 pointA = CalculatedPoints[0];
                    Vector3 pointB = CalculatedPoints[1];

                    Vector3 vectorAB = pointB - pointA;
                    vectorAB.y = 0;

                    if (vectorAB == Vector3.zero)
                    {
                        return Quaternion.identity;
                    }

                    return Quaternion.LookRotation(vectorAB.normalized, Vector3.up);
                }
            }

            public float Width
            {
                get
                {
                    Vector3 pointA = CalculatedPoints[0];
                    Vector3 pointB2 = CalculatedPoints[1];
                    return Vector3.Distance(pointA, pointB2);
                }
            }

            public float Depth
            {
                get
                {
                    Vector3 pointB2 = CalculatedPoints[1];
                    Vector3 pointC2 = CalculatedPoints[2];

                    return Vector3.Distance(pointB2, new Vector3(pointC2.x, pointB2.y, pointC2.z));
                }
            }

            public float Height
            {
                get
                {
                    Vector3 pointA = CalculatedPoints[0];
                    Vector3 pointC2 = CalculatedPoints[2];
                    return Mathf.Abs(pointA.y - pointC2.y);
                }
            }
        }

        #endregion
    }
}