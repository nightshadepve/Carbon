using Facepunch;
using HarmonyLib;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using System.Collections;
using Oxide.Game.Rust.Cui;
using Newtonsoft.Json.Linq;
using Rust;
using System.IO;
using static RelationshipManager;


/* 
* Spectator mode.
* Make it so servers can disable item prizes.
* Add scoreboard for wipe and all time.
* 
* Add version to the class. Have the plugin download the json, parse for a version number, and if it's later than the current one, print a message in console advising that there is a later version of the map, and have a command to write the json to file.
* Add auto reward loot when a player leaves the match after winning.
* Disable the backpack fetch system 
* 
* Spectate idea
* - Create an invisible cube that players are teleported to once they die. Would need to hook death and tp them to the cube, remove them from the participants and add to a new list of spectators.
*/


/* 1.0.43
 * Updated for the November Rust update: tree beehives are suppressed with the new tree addition settings.
 * Fixed the next plugin on the server missing its startup when this plugin unloads itself during server boot. The unload now waits one tick.
 */

/* 1.0.42
 * Removed debug commands that gave free prizes.
 * Admin commands are admin-only.
 * Downed or dead players can't join.
 * Inventories are saved in step with the world save, and stripped correctly after a crash.
 * Dropped-container sweep only runs around the arena.
 * A broken config is no longer overwritten.
 * Auto-start no longer survives reloads.
 * Sandbag upgrade-in-place blocked.
 * Fixed an error when EventHelper reloads.
 */

namespace Oxide.Plugins
{
    [Info("Survival Arena", "imthenewguy", "1.0.43")]
    [Description("Spawns a battle royal arena in the sky")]
    class SurvivalArena : RustPlugin
    {
        #region Config

        private Configuration config;
        public class Configuration
        {
            [JsonProperty("Game settings")]
            public GameSettings gameSettings = new GameSettings();

            [JsonProperty("Event Helper settings")]
            public EventSettings eventSettings = new EventSettings();

            [JsonProperty("Sound settings")]
            public SoundEffects soundsSettings = new SoundEffects();

            [JsonProperty("Radiation zone settings")]
            public SphereInfo radiationZoneSettings = new SphereInfo();

            [JsonProperty("Bush prefabs")]
            public Dictionary<BiomeType, List<string>> biome_bushes = new Dictionary<BiomeType, List<string>>();

            [JsonProperty("Tree prefabs")]
            public Dictionary<BiomeType, List<string>> biome_trees = new Dictionary<BiomeType, List<string>>();

            [JsonProperty("Dead log prefabs")]
            public Dictionary<BiomeType, List<string>> biome_logs = new Dictionary<BiomeType, List<string>>();

            [JsonProperty("Prize settings")]
            public Rewards prize_settings = new Rewards();

            [JsonProperty("Command settings")]
            public CommandInfo command_settings = new CommandInfo();

            [JsonProperty("Loot settings")]
            public Dictionary<string, LootSettings> lootSettings = new Dictionary<string, LootSettings>(StringComparer.OrdinalIgnoreCase);

            [JsonProperty("How many delete/spawn actions should we do per game tick when building/removing the arena?")]
            public int max_procs_per_tick = 50;

            [JsonProperty("Anchor settings")]
            public Anchors anchors = new Anchors();

            [JsonProperty("Notifications settings")]
            public NotificationsInfo notifications = new NotificationsInfo();

            [JsonProperty("Death feed settings")]
            public DeathFeedSettings deathFeedSettings = new DeathFeedSettings();

            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonConvert.DeserializeObject<Dictionary<string, object>>(ToJson());
        }

        protected override void LoadDefaultConfig()
        {
            config = new Configuration();
            config.biome_trees = DefaultBiomeTrees;
            config.biome_bushes = DefaultBiomeBushes;
            config.biome_logs = DefaultBiomeLogs;
            config.lootSettings = DefaultLootSettings;
            config.prize_settings.prizes = DefaultPrizes;
            config.prize_settings.participation_rewards.participation_prizes = DefaultParticipationPrizes;
            config.prize_settings.participation_rewards.modifiers = DefaultParticipationModifiers;
            config.eventSettings.black_listed_items = DefaultBlacklistItems;
        }

        List<string> DefaultBlacklistItems
        {
            get
            {
                return new List<string>()
                {
                    "cassette",
                    "cassette.medium",
                    "cassette.short",
                    "fun.casetterecorder",
                    "boombox"
                };
            }
        }

        public class DeathFeedSettings
        {
            [JsonProperty("Name colour for death feed")]
            public string DeathFeedCol = "F27C1B";

            [JsonProperty("How long should a kill stay around for?")]
            public float Duration = 10;

            [JsonProperty("Maximum kills on a feed before it starts to overwrite the oldest kills")]
            public int MaxKillsDisplayed = 6;
        }

        public class CommandInfo
        {
            [JsonProperty("Command to join an active lobby")]
            public string join_command = "survival";

            [JsonProperty("Command to leave an active lobby")]
            public string leave_command = "saleave";

            [JsonProperty("Command to view the scoreboard")]
            public string score_command = "sarank";
        }

        public class SphereInfo
        {
            [JsonProperty("Sphere entity colour [Red, Purple, Black, Green]")]
            public SphereColour sphereColour = SphereColour.Red;

            [JsonProperty("How many spheres should we overlap to make it darker [higher number = darker]?")]
            public int darkness = 8;

            [JsonProperty("How many stacks of radiation should players accumulate for each second spent outside of the sphere?")]
            public int rads_per_second = 3;

            [JsonProperty("How many points of radiation should the player accumulate per second while outside of the dome?")]
            public int rad_increase_per_tick = 3;

            [JsonProperty("Radiation check interval (seconds)")]
            public int check_interval = 1;

            [JsonProperty("Final circle size")]
            public int min_ring_size = 20;

            [JsonProperty("Seconds after game start before the circle spawns")]
            public int circle_delay = 60;

            [JsonProperty("The size modifier of the circle when it initially spawns [1.0 = the size of the area. 0.5 = half size]")]
            public float circle_radius_modifier = 1;

            [JsonProperty("Radiation panel colour [Red Green Blue Alpha][1.0 = full colour]")]
            public string panel_colour = "0.5 0 0 0.90";
        }

        public class Rewards
        {
            [JsonProperty("How many prizes should the player receive per claim?")]
            public int rolls_per_claim = 1;

            [JsonProperty("Prizes")]
            public List<PrizeInfo> prizes = new List<PrizeInfo>();

            [JsonProperty("Economic dollars for winning a match [requires: Economics]")]
            public CurrencyReward economic_reward = new CurrencyReward(0, 0);

            [JsonProperty("Server reward points for winning a match [requires: ServerRewards]")]
            public CurrencyReward srp_reward = new CurrencyReward(0, 0);

            [JsonProperty("Skill Tree XP given to the player when they win the event [Requires: SkillTree]")]
            public double SkillTree_XP_Reward = 1000;

            [JsonProperty("Automatically award the player with their prize (false means the player must type the /sprize command to redeem their prize)")]
            public bool auto_award = false;

            public class CurrencyReward
            {
                public int min_amount;
                public int max_amount;
                public CurrencyReward(int min_amount, int max_amount)
                {
                    this.min_amount = min_amount;
                    this.max_amount = max_amount;
                }
            }

            [JsonProperty("Participation rewards")]
            public ParticipationRewards participation_rewards = new ParticipationRewards();
            public class ParticipationRewards
            {
                [JsonProperty("Provide players with a reward for participating in the event?")]
                public bool enabled = false;

                [JsonProperty("Modifiers for dying first [1, 1.0 = the first player to die will receive 1x of a random reward]")]
                //​‌​​‌‌‌​​‌​‌​‌​​​‌​​​‌​‌​​‌‌​‌​​​‌​​‌‌​‌​‌​​​‌​​​‌​​​​​‌​​‌‌‌‌​‌ Multiplies the selected rewards amount by the float value.
                public Dictionary<int, float> modifiers = new Dictionary<int, float>();

                [JsonProperty("List of participation rewards [The modifier from the dictionary will multiply the amounts when selected].")]
                public List<PrizeInfo> participation_prizes = new List<PrizeInfo>();

                [JsonProperty("Economics reward [The modifier from the dictionary will multiply the amounts when selected].")]
                public double econParticipationPrize = 25;

                [JsonProperty("Server Reward points reward [The modifier from the dictionary will multiply the amounts when selected].")]
                public int srpParticipationPrize = 25;
            }

        }

        public class PrizeInfo
        {
            public string shortname;
            public int min_quantity;
            public int max_quantity;
            public ulong skin;
            public string displayName;
            public int dropWeight;

            public PrizeInfo(string shortname, int min_quantity, int max_quantity, int dropWeight = 100, ulong skin = 0, string displayName = null)
            {
                this.shortname = shortname;
                this.min_quantity = min_quantity;
                this.max_quantity = max_quantity;
                this.skin = skin;
                this.displayName = displayName;
                this.dropWeight = dropWeight;
            }
        }

        public class Anchors
        {
            [JsonProperty("Player counter anchor [Key: Left/Right, Value: Up/Down]")]
            public KeyValuePair<float, float> player_count_anchor_adjustment = new KeyValuePair<float, float>(-40f, 7f);
        }

        public class SoundEffects
        {
            [JsonProperty("Sound effect when the game is about to start")]
            public string sound_for_starting = "assets/prefabs/missions/effects/mission_victory.prefab";

            [JsonProperty("Sound effect for killing another player")]
            public string sound_for_killing = "assets/prefabs/missions/effects/mission_accept.prefab";

            [JsonProperty("Sound effect when a player dies (players to all participants)")]
            public string sound_for_death = "assets/prefabs/npc/m2bradley/effects/maincannonattack.prefab";

            [JsonProperty("Sound when a player redeems a prize")]
            public string sound_for_prize = "assets/prefabs/misc/easter/painted eggs/effects/gold_open.prefab";

            [JsonProperty("SoundLibraryApi Settings")]
            public SoundLibraryApiSettings soundLibraryApiSettings = new SoundLibraryApiSettings();

            public class SoundLibraryApiSettings
            {
                [JsonProperty("Use Sound Library API")]
                public bool useSoundLibraryAPI = false;

                [JsonProperty("Custom sound effect for when the game is about to start (Sound Library API File Name)")]
                public string customSoundForStart = "";


            }
        }

        public class EventSettings
        {
            [JsonProperty("Prevent players from entering the arena if they have items on them?")]
            public bool prevent_entry_with_items = false;

            [JsonProperty("Return the players items back immediately after respawn? (setting false requries the player to use a command to get their items back)")]
            public bool give_items_back_on_death = true;

            [JsonProperty("Use EventHelper to schedule the events (recommended)?")]
            public bool use_event_helper_timer = true;

            [JsonProperty("If not using EventHelper, how often should we run the event?")]
            public float non_eventhelper_timer = 3600;

            [JsonProperty("Commands to prevent when a player are at the event")]
            public string[] prevent_commands = { "kit", "backpack", "backpack.open" };

            [JsonProperty("Allow players to stay in a team when they join the event? [set to true if using any sort of clans or team management plugin]")]
            public bool allow_teams = true;

            [JsonProperty("A list of items that cannot be taken into the event")]
            public List<string> black_listed_items = new List<string>();

            [JsonProperty("Prevent players who are flagged with the NoEscape plugin from joining the event?")]
            public bool prevent_no_escape_joins = true;

            [JsonProperty("Prevent the double jump plugin?")]
            public bool prevent_double_jump_plugin = true;

            [JsonProperty("Command to restore stored items")]
            public string restore_items_command = "sarestore";

            [JsonProperty("Prevent xp loss when dying at the event?")]
            public bool prevent_xp_loss = true;

            [JsonProperty("Prevent the player from losing their backpack when dying at the event?")]
            public bool prevent_bag_loss = true;

            [JsonProperty("Prevent the player from opening their backpack while at the event?")]
            public bool prevent_backpack_opening = true;

            [JsonProperty("Prevent buffs from Skill Tree from activating [won't disable monobehaviour buffs]?")]
            public bool prevent_skill_tree_buffs = false;
        }

        public class GameSettings
        {
            [JsonProperty("Minimum players required for the event to proceed")]
            public int min_player = 2;

            [JsonProperty("Send a message to the contestants when a player dies?")]
            public bool message_on_death = true;

            [JsonProperty("Minimum respawn time for crates")]
            public float min_crate_respawn_time = 30f;

            [JsonProperty("Maximum respawn time for crates")]
            public float max_crate_respawn_time = 90f;

            [JsonProperty("Attempt to clear old arenas when the plugin loads?")]
            public bool clear_arena_on_server_start = true;

            [JsonProperty("Default lobby time")]
            public int defaultStartTime = 300;

            [JsonProperty("Commands to run for each player when the game starts and the gates open. Use {id} in replacement of the players steam id")]
            public List<string> commands_on_start = new List<string>();

            [JsonProperty("Commands to run for each player when they leave the game. Use {id} in replacement of the players steam id")]
            public List<string> commands_on_leave = new List<string>();

            [JsonProperty("Use the NightVision plugin?")]
            public bool use_nightvision = true;

            [JsonProperty("Allow players to loot while they are outside of the circle (in the radiation zone)?")]
            public bool allow_looting_in_rad_zone = false;

            [JsonProperty("Announce when a player joins the event?")]
            public bool announce_join = true;

            [JsonProperty("Settings to help prevent non-participants from getting to the arena")]
            public InterferenceSettings interference_settings = new InterferenceSettings();
        }

        public class InterferenceSettings
        {
            [JsonProperty("Constantly check to see if non-participants are near the arena? [They will be warned then killed if found]")]
            public bool check_for_outsiders = true;

            [JsonProperty("How often should we check [seconds]")]
            public float check_time = 1;

            [JsonProperty("How close can the player get to the arena before they are warned")]
            public float dist_from_edge_restriction = 50f;

            [JsonProperty("How many seconds does the player have to leave the arena before they are killed?")]
            public float seconds_to_vacate = 10;

            [JsonProperty("Exclude players witht he IsAdmin flag from our checks?")]
            public bool ignore_admins = true;
        }

        public class NotificationsInfo
        {
            [JsonProperty("Notify plugin settings")]
            public NotifyInfo sendNotify = new NotifyInfo();

            [JsonProperty("GUIAnnouncements plugin settings")]
            public GUIAnnouncementsInfo GUIAnnouncements = new GUIAnnouncementsInfo();

            [JsonProperty("ChatNotifications settings")]
            public ChatNotifications chatNotifications = new ChatNotifications();
        }


        public class ChatNotifications
        {
            [JsonProperty("Send the SurvivalArenaStartingBeginIn lang message in chat")]
            public bool SurvivalArenaStartingBeginIn = true;

            [JsonProperty("Send the Cancelled lang message in chat")]
            public bool Cancelled = true;

            [JsonProperty("Send the AnnounceWinner lang message in chat")]
            public bool AnnounceWinner = true;

            [JsonProperty("Send the NobodyWon lang message in chat")]
            public bool NobodyWon = true;
        }

        public class NotifyInfo
        {
            [JsonProperty("Send notifications using the Notify plugin?")]
            public bool enabled = true;

            [JsonProperty("If enabled, send notifications when the event is being advertised?")]
            public bool sendNotifyEventReminder = false;

            [JsonProperty("If enabled, send notifications when the player has items they need to recover?")]
            public bool sendNotifyRecoverReminder = true;

            [JsonProperty("Notify profile/type")]
            public int notifyType = 0;
        }

        public class GUIAnnouncementsInfo
        {
            [JsonProperty("Send announcements using the GUIAnnouncements plugin?")]
            public bool enabled = true;

            [JsonProperty("Banner colour - see GUIAnnouncements for colours")]
            public string banner_colour = "Purple";

            [JsonProperty("Text colour - see GUIAnnouncements for colours")]
            public string text_colour = "Yellow";

            [JsonProperty("Position adjustment")]
            public float position_adjustment = 0;
        }

        public class LootSettings
        {
            [JsonProperty("Chance for this profile to be selected for the game (weighted system)")]
            public int profileWeight = 100;
            public int min_items;
            public int max_items;
            public List<LootInfo> items = new List<LootInfo>();

            public LootSettings(int min_items, int max_items, List<LootInfo> items, int profileWeight = 100)
            {
                this.min_items = min_items;
                this.max_items = max_items;
                this.items = items;
                this.profileWeight = profileWeight;
            }
        }

        public class LootInfo
        {
            public string shortname;
            public int min_amount;
            public int max_amount;
            public ulong skin;
            public string displayName;
            public int dropWeight;

            public LootInfo(string shortname, int min_amount, int max_amount, ulong skin = 0, string displayName = null, int dropWeight = 100)
            {
                this.shortname = shortname;
                this.min_amount = min_amount;
                this.max_amount = max_amount;
                this.skin = skin;
                this.displayName = displayName;
                this.dropWeight = dropWeight;
            }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null)
                {
                    throw new JsonException();
                }

                if (!config.ToDictionary().Keys.SequenceEqual(Config.ToDictionary(x => x.Key, x => x.Value).Keys))
                {
                    PrintToConsole("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
                Interface.Oxide.UnloadPlugin(Name);
                return;
            }
            SaveConfig();
        }

        protected override void SaveConfig()
        {
            if (config == null) return;
            PrintToConsole($"Configuration changes saved to {Name}.json");
            Config.WriteObject(config, true);
        }

        #endregion

        #region Defalt config

        Dictionary<BiomeType, List<string>> DefaultBiomeTrees
        {
            get
            {
                return new Dictionary<BiomeType, List<string>>()
                {
                    [BiomeType.Arid] = new List<string>()
                    {
                        "assets/bundled/prefabs/autospawn/resource/v3_arid_forest/palm_tree_tall_a_entity.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_arid_forest/palm_tree_tall_b_entity.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_arid_beachside/palm_tree_short_a_entity.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_arid_beachside/palm_tree_short_b_entity.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_arid_beachside/palm_tree_short_c_entity.prefab"
                    },
                    [BiomeType.Temperate] = new List<string>()
                    {
                        "assets/bundled/prefabs/autospawn/resource/v3_temp_forest_pine/pine_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_temp_forest_pine/pine_c.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_temp_field_large/oak_b.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_temp_field_large/oak_c.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_temp_field_large/oak_d.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_temp_forest_pine/birch_big_temp.prefab"
                    },
                    [BiomeType.Tundra] = new List<string>()
                    {
                        "assets/bundled/prefabs/autospawn/resource/v3_tundra_field_pines/pine_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_tundra_field_pines/pine_b.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_tundra_field_pines/pine_d.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_tundra_forest/pine_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_tundra_forest/pine_c.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_tundra_forest/pine_dead_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_tundra_forest/pine_dead_b.prefab"
                    },
                    [BiomeType.Arctic] = new List<string>()
                    {
                        "assets/bundled/prefabs/autospawn/resource/v3_arctic_forest/pine_a_snow.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_arctic_forest/pine_c_snow.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_arctic_forest/pine_dead_snow_b.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_arctic_forest/pine_dead_snow_c.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_arctic_forestside/pine_a_snow.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_arctic_forestside/pine_b snow.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_arctic_forestside/pine_d_snow.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_arctic_forestside/pine_dead_snow_e.prefab",
                    },
                    [BiomeType.Jungle] = new List<string>()
                    {
                        "assets/bundled/prefabs/autospawn/resource/v3_jungle_forest/hura_crepitans_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_jungle_forest/hura_crepitans_b.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_jungle_forest/hura_crepitans_c.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_jungle_forest/hura_crepitans_d.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_jungle_forest/trumpet_tree_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_jungle_forest/trumpet_tree_b.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_jungle_forest/trumpet_tree_c.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_jungle_forest/trumpet_tree_d.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_jungle_forest/mauritia_flexuosa_l.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_jungle_forest/mauritia_flexuosa_m.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_jungle_forest/mauritia_flexuosa_s.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_jungle_forest/mauritia_flexuosa_xs.prefab",
                    }
                };
            }
        }

        Dictionary<BiomeType, List<string>> DefaultBiomeBushes
        {
            get
            {
                return new Dictionary<BiomeType, List<string>>()
                {
                    [BiomeType.Arid] = new List<string>()
                    {
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_arid_desert/creosote_bush_c.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_arid_desert/creosote_bush_d.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_arid_desert/mormon_tea_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_arid_desert/mormon_tea_b.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_arid_desert/mormon_tea_c.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_arid_desert/mormon_tea_d.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_arid_dry/creosote_bush_dry_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_arid_dry/creosote_bush_dry_b.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_arid_grass/creosote_bush_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_arid_grass/creosote_bush_b.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_arid_grass/creosote_bush_c.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_arid_grass/creosote_bush_d.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_arid_cactus/cactus-1.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_arid_cactus/cactus-2.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_arid_cactus/cactus-3.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_arid_cactus/cactus-4.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_arid_cactus/cactus-5.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_arid_cactus/cactus-6.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_arid_cactus/cactus-7.prefab"
                    },
                    [BiomeType.Temperate] = new List<string>()
                    {
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_temp/bush_spicebush_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_temp/bush_spicebush_b.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_temp/bush_spicebush_c.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_temp/bush_spicebush_d.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_temp/bush_willow_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_temp/bush_willow_b.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_temp/bush_willow_c.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_temp/bush_willow_d.prefab"
                    },
                    [BiomeType.Tundra] = new List<string>()
                    {
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_tundra/bush_spicebush_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_tundra/bush_spicebush_b.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_tundra/bush_spicebush_c.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_tundra/bush_spicebush_d.prefab"
                    },
                    [BiomeType.Arctic] = new List<string>()
                    {
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_arctic/bush_willow_snow_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_arctic/bush_willow_snow_b.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_arctic/bush_willow_snow_c.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_arctic/bush_willow_snow_d.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_arctic/bush_willow_snow_small_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_arctic_forest/bush_spicebush_a_snow.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_arctic_forest/bush_spicebush_c_snow.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_arctic_forest/bush_willow_snow_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_arctic_forest/bush_willow_snow_b.prefab"
                    },
                    [BiomeType.Jungle] = new List<string>()
                    {
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_jungle/monstera_deliciosa_l.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_jungle/monstera_deliciosa_m.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_jungle/monstera_deliciosa_s.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_jungle/monstera_deliciosa_xl.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_jungle/heliconia_rostrata_l.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_jungle/heliconia_rostrata_m.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_jungle/heliconia_rostrata_s.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_jungle/heliconia_rostrata_s_short.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_jungle/heliconia_rostrata_xs.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_jungle/bromeliad_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_jungle/bromeliad_b.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_jungle/bromeliad_c.prefab",
                        "assets/bundled/prefabs/autospawn/resource/v3_bushes_jungle/bromeliad_d.prefab",
                    }
                };
            }
        }

        Dictionary<BiomeType, List<string>> DefaultBiomeLogs
        {
            get
            {
                return new Dictionary<BiomeType, List<string>>()
                {
                    [BiomeType.Arid] = new List<string>()
                    {
                        "assets/bundled/prefabs/autospawn/resource/logs_dry/dead_log_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/logs_dry/dead_log_b.prefab",
                        "assets/bundled/prefabs/autospawn/resource/logs_dry/dead_log_c.prefab"
                    },
                    [BiomeType.Temperate] = new List<string>()
                    {
                        "assets/bundled/prefabs/autospawn/resource/logs_dry/dead_log_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/logs_dry/dead_log_b.prefab",
                        "assets/bundled/prefabs/autospawn/resource/logs_dry/dead_log_c.prefab",
                        "assets/bundled/prefabs/autospawn/resource/logs_wet/dead_log_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/logs_wet/dead_log_b.prefab",
                        "assets/bundled/prefabs/autospawn/resource/logs_wet/dead_log_c.prefab"
                    },
                    [BiomeType.Tundra] = new List<string>()
                    {
                        "assets/bundled/prefabs/autospawn/resource/logs_dry/dead_log_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/logs_dry/dead_log_b.prefab",
                        "assets/bundled/prefabs/autospawn/resource/logs_dry/dead_log_c.prefab",
                        "assets/bundled/prefabs/autospawn/resource/logs_wet/dead_log_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/logs_wet/dead_log_b.prefab",
                        "assets/bundled/prefabs/autospawn/resource/logs_wet/dead_log_c.prefab"
                    },
                    [BiomeType.Arctic] = new List<string>()
                    {
                        "assets/bundled/prefabs/autospawn/resource/logs_snow/dead_log_b.prefab",
                        "assets/bundled/prefabs/autospawn/resource/logs_snow/dead_log_c.prefab",
                        "assets/bundled/prefabs/autospawn/resource/logs_snow/dead_log_a.prefab"
                    },
                    [BiomeType.Jungle] = new List<string>()
                    {
                        "assets/bundled/prefabs/autospawn/resource/logs_dry/dead_log_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/logs_dry/dead_log_b.prefab",
                        "assets/bundled/prefabs/autospawn/resource/logs_dry/dead_log_c.prefab",
                        "assets/bundled/prefabs/autospawn/resource/logs_wet/dead_log_a.prefab",
                        "assets/bundled/prefabs/autospawn/resource/logs_wet/dead_log_b.prefab",
                        "assets/bundled/prefabs/autospawn/resource/logs_wet/dead_log_c.prefab"
                    },
                };
            }
        }

        Dictionary<string, LootSettings> DefaultLootSettings
        {
            get
            {
                return new Dictionary<string, LootSettings>()
                {
                    ["PrimitiveLoot"] = new LootSettings(2, 4, new List<LootInfo>()
                    {
                        new LootInfo("attire.hide.pants", 1, 1),
                        new LootInfo("attire.hide.poncho", 1, 1),
                        new LootInfo("attire.hide.skirt", 1, 1),
                        new LootInfo("attire.hide.vest", 1, 1),
                        new LootInfo("attire.hide.helterneck", 1, 1),
                        new LootInfo("attire.hide.boots", 1, 1),
                        new LootInfo("grenade.beancan", 1, 1),
                        new LootInfo("hat.wolf", 1, 1),
                        new LootInfo("spear.stone", 1, 1),
                        new LootInfo("spear.wooden", 1, 1),
                        new LootInfo("arrow.bone", 10, 20),
                        new LootInfo("arrow.fire", 3, 6),
                        new LootInfo("arrow.hv", 5, 12),
                        new LootInfo("arrow.wooden", 5, 20),
                        new LootInfo("longsword", 1, 1),
                        new LootInfo("salvaged.sword", 1, 1),
                        new LootInfo("machete", 1, 1),
                        new LootInfo("bow.compound", 1, 1),
                        new LootInfo("crossbow", 1, 1),
                        new LootInfo("bow.hunting", 1, 1),
                        new LootInfo("grenade.f1", 1, 3),
                        new LootInfo("pistol.revolver", 1, 1),
                        new LootInfo("ammo.pistol", 5, 15),
                        new LootInfo("pistol.nailgun", 1, 1),
                        new LootInfo("bandage", 1, 6),
                        new LootInfo("syringe.medical", 1, 2),
                        new LootInfo("bone.armor.suit", 1, 1),
                        new LootInfo("bone.club", 1, 1),
                        new LootInfo("deer.skull.mask", 1, 1),
                        new LootInfo("knife.bone", 1, 1),
                        new LootInfo("wood.armor.helmet", 1, 1),
                        new LootInfo("wood.armor.pants", 1, 1),
                        new LootInfo("wood.armor.jacket", 1, 1),
                        new LootInfo("hat.boonie", 1, 1),
                        new LootInfo("bucket.helmet", 1, 1),
                        new LootInfo("riot.helmet", 1, 1),
                        new LootInfo("burlap.gloves.new", 1, 1),
                        new LootInfo("burlap.headwrap", 1, 1),
                        new LootInfo("burlap.shirt", 1, 1),
                        new LootInfo("burlap.shoes", 1, 1),
                        new LootInfo("burlap.trousers", 1, 1),
                        new LootInfo("knife.butcher", 1, 1),
                        new LootInfo("knife.combat", 1, 1),
                        new LootInfo("shotgun.waterpipe", 1, 1),
                        new LootInfo("ammo.handmade.shell", 1, 5),
                        new LootInfo("stonehatchet", 1, 1),
                        new LootInfo("mace", 1, 1),
                        new LootInfo("salvaged.cleaver", 1, 1),
                        new LootInfo("rock", 1, 1),
                        new LootInfo("sickle", 1, 1),
                    }),
                    ["GunLoot"] = new LootSettings(2, 4, new List<LootInfo>()
                    {
                        new LootInfo("weapon.mod.8x.scope", 1, 1),
                        new LootInfo("weapon.mod.small.scope", 1, 1),
                        new LootInfo("crossbow", 1, 1),
                        new LootInfo("weapon.mod.holosight", 1, 1),
                        new LootInfo("longsword", 1, 1),
                        new LootInfo("machete", 1, 1),
                        new LootInfo("weapon.mod.muzzleboost", 1, 1),
                        new LootInfo("weapon.mod.muzzlebrake", 1, 1),
                        new LootInfo("salvaged.cleaver", 1, 1),
                        new LootInfo("weapon.mod.silencer", 1, 1),
                        new LootInfo("weapon.mod.simplesight", 1, 1),
                        new LootInfo("tactical.gloves", 1, 1),
                        new LootInfo("weapon.mod.lasersight", 1, 1),
                        new LootInfo("ammo.shotgun", 5, 10),
                        new LootInfo("ammo.shotgun.fire", 5, 10),
                        new LootInfo("ammo.shotgun.slug", 2, 7),
                        new LootInfo("ammo.grenadelauncher.he", 2, 5),
                        new LootInfo("ammo.rifle", 20, 40),
                        new LootInfo("ammo.rifle.explosive", 5, 10),
                        new LootInfo("ammo.handmade.shell", 10, 20),
                        new LootInfo("ammo.rocket.hv", 1, 2),
                        new LootInfo("ammo.rifle.hv", 5, 10),
                        new LootInfo("ammo.pistol.fire", 5, 10),
                        new LootInfo("ammo.nailgun.nails", 20, 40),
                        new LootInfo("ammo.pistol", 20, 40),
                        new LootInfo("ammo.pistol.hv", 10, 20),
                        new LootInfo("ammo.rifle.incendiary", 10, 15),
                        new LootInfo("rifle.ak", 1, 1),
                        new LootInfo("rifle.bolt", 1, 1),
                        new LootInfo("rifle.l96", 1, 1),
                        new LootInfo("rifle.lr300", 1, 1),
                        new LootInfo("rifle.m39", 1, 1),
                        new LootInfo("rifle.semiauto", 1, 1),
                        new LootInfo("pistol.eoka", 1, 1),
                        new LootInfo("pistol.m92", 1, 1),
                        new LootInfo("pistol.nailgun", 1, 1),
                        new LootInfo("pistol.python", 1, 1),
                        new LootInfo("pistol.revolver", 1, 1),
                        new LootInfo("pistol.semiauto", 1, 1),
                        new LootInfo("arrow.bone", 10, 40),
                        new LootInfo("arrow.fire", 5, 10),
                        new LootInfo("arrow.hv", 10, 40),
                        new LootInfo("arrow.wooden", 20, 60),
                        new LootInfo("jumpsuit.suit.blue", 1, 1),
                        new LootInfo("bone.armor.suit", 1, 1),
                        new LootInfo("hazmatsuit", 1, 1),
                        new LootInfo("hazmat.plague", 1, 1),
                        new LootInfo("roadsign.jacket", 1, 1),
                        new LootInfo("roadsign.kilt", 1, 1),
                        new LootInfo("wood.armor.helmet", 1, 1),
                        new LootInfo("wood.armor.pants", 1, 1),
                        new LootInfo("wood.armor.jacket", 1, 1),
                        new LootInfo("deer.skull.mask", 1, 1),
                        new LootInfo("bucket.helmet", 1, 1),
                        new LootInfo("coffeecan.helmet", 1, 1),
                        new LootInfo("heavy.plate.helmet", 1, 1),
                        new LootInfo("riot.helmet", 1, 1),
                        new LootInfo("shotgun.double", 1, 1),
                        new LootInfo("shotgun.pump", 1, 1),
                        new LootInfo("shotgun.spas12", 1, 1),
                        new LootInfo("shotgun.waterpipe", 1, 1),
                        new LootInfo("smg.2", 1, 1),
                        new LootInfo("smg.mp5", 1, 1),
                        new LootInfo("smg.thompson", 1, 1),
                        new LootInfo("grenade.f1", 2, 5),
                        new LootInfo("multiplegrenadelauncher", 1, 1),
                        new LootInfo("grenade.smoke", 1, 2),
                        new LootInfo("grenade.beancan", 1, 1),
                        new LootInfo("rocket.launcher", 1, 1),
                        new LootInfo("jacket", 1, 1),
                        new LootInfo("burlap.gloves.new", 1, 1),
                        new LootInfo("burlap.gloves", 1, 1),
                        new LootInfo("roadsign.gloves", 1, 1),
                        new LootInfo("shoes.boots", 1, 1),
                        new LootInfo("attire.hide.boots", 1, 1),
                        new LootInfo("attire.hide.pants", 1, 1),
                        new LootInfo("attire.hide.poncho", 1, 1),
                        new LootInfo("attire.hide.skirt", 1, 1),
                        new LootInfo("attire.hide.vest", 1, 1),
                        new LootInfo("pants", 1, 1),
                        new LootInfo("pants.shorts", 1, 1),
                        new LootInfo("hoodie", 1, 1),
                        new LootInfo("syringe.medical", 1, 3),
                        new LootInfo("bandage", 3, 6),
                        new LootInfo("largemedkit", 1, 2),
                        new LootInfo("metal.plate.torso", 1, 1),
                        new LootInfo("metal.facemask", 1, 1)

                    })
                };
            }
        }

        List<PrizeInfo> DefaultPrizes
        {
            get
            {
                return new List<PrizeInfo>()
                {
                    new PrizeInfo("scrap", 300, 600)
                };
            }
        }

        List<PrizeInfo> DefaultParticipationPrizes
        {
            get
            {
                return new List<PrizeInfo>()
                {
                    new PrizeInfo("scrap", 50, 50)
                };
            }
        }

        Dictionary<int, float> DefaultParticipationModifiers
        {
            get
            {
                return new Dictionary<int, float>()
                {
                    [1] = 1
                };
            }
        }

        #endregion

        #region Localization

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPerms"] = "You do not have permission to use this command.",
                ["EventRunning"] = "The event is already running.",
                ["SurvivalArenaStartingBeginIn"] = "Game will start in <color=#ffb600>{0}</color> seconds. Type <color=#00ff00>/{1}</color> to join.",
                ["Cancelled"] = "Survival Arena has been cancelled due to lack of players.",
                ["DoorsOpeningSoon"] = "Doors opening in <color=#ffb600>5</color> seconds.",
                ["Prefix"] = "[<color=#ffae00>SurvivalArena</color>] ",
                ["CircleClosingStarted"] = "<color=#0094c8>The circle has started to close!</color>",
                ["CircleClosingFinished"] = "<color=#0094c8>The final circle has been formed!</color>",
                ["AnnounceWinner"] = "{0} has won the event!",
                ["MessageWinner"] = "Type <color=#23f207>/sprize</color> to claim your prize. Prizes remaining: <color=#23f207>{0}</color>.\n",
                ["MsgToWinner"] = "You have won the event!\n",
                ["NobodyWon"] = "Nobody won the event.",
                ["EconomicsWon"] = "<color=#23f207>${0}</color> has been added to your account for winning.\n",
                ["ServerRewardsWon"] = "<color=#23f207>{0} points</color> were added to your account for winning.\n",
                ["SkillTreeXPWon"] = "You gained <color=#23f207>{0} xp</color> for winning.\n",

                ["FailedJoin_Closed"] = "Unable to join - Joining has closed.",
                ["FailedJoin_EventHelper"] = "Unable to join - Check the message from EventHelper.",
                ["FailedJoin_Enrolled"] = "Unable to join - You are already in this competition.",

                ["JoinAnnounce"] = "{0} joined the event [<color=#ffb600>{1}</color>/<color=#ffb600>{2}</color>]",
                ["LeftCompetition"] = "You left the arena.",
                ["NotAtEvent"] = "You are not at the event.",
                ["AddEntityEventRunning"] = "Event needs to be running to use this command.",
                ["AddEntityElevation"] = "Arena must not be above default elevation when adding spawns.",
                ["NullInvalidEntity"] = "Invalid entity targeted. Tried to target a rock, player or gates.",
                ["EntityRemoved"] = "Removed entity: {0} [POS: {1} - ROT: {2}]",
                ["EntityNotFoundInData"] = "Entity: {0} [POS: {1} - ROT: {2}] was not found in arena data.",
                ["EntityAddedTree"] = "Added new tree spawn at {0}",
                ["EntityAddedBush"] = "Added new bush spawn at {0}",
                ["EntityAddedLoot"] = "Added new loot spawn spawn at {0}",
                ["EntityAddedLog"] = "Added new log spawn at {0}",
                ["EntityAddInvalidType"] = "Invalid type selected.",
                ["InRadZone"] = "YOU ARE IN THE RADIATION ZONE!",
                ["UIPlayersRemaining"] = "Players remaining: <color=#ffae00>{0}</color>",
                ["UILootProfile"] = "Loot Profile",
                ["UIProfSelected"] = "<color=#ffae00>{0}</color>",
                ["UIHeightMod"] = "Height Mod",
                ["UILobbyTime"] = "Lobby Time",
                ["UISTOP"] = "<color=#f22407>STOP</color>",
                ["UISTART"] = "<color=#00be06>START</color>",
                ["UICLOSE"] = "CLOSE",
                ["UIEndedEvent"] = "Ended the event.",
                ["UIStartingEvent"] = "Starting event with the following parameters:\n- Lobby time: {0}\n- Height: {1}\n- Profile: {2}",
                ["_UILobbyTime"] = "GAME STARTING IN <color=#ffae00>{0}</color> SECONDS.\n<size=10>Type <color=#ffae00>/{1}</color> to leave the event.</size>",
                ["NoPrize"] = "You have no prizes left to claim.",
                ["PrizeGiven"] = "You received {0}x {1}.",
                ["ParticipationPrizeGiven"] = "You received {0}x {1} for participating in the event.",
                ["JoinedTheEvent"] = "You joined the Survival Arena event!\nType <color=#00ff00>/{0}</color> if you wish to leave the event.",
                ["KillMessage1"] = "<color=#ffae00>{0}</color> stood no chance against <color=#ffae00>{1}</color>!",
                ["KillMessage2"] = "<color=#ffae00>{0}</color> was killed by magic...Or was it <color=#ffae00>{1}</color>?",
                ["KillMessage3"] = "<color=#ffae00>{0}</color> was escorted out of the arena by <color=#ffae00>{1}</color>.",
                ["KillMessage4"] = "<color=#ffae00>{0}</color> was slain by <color=#ffae00>{1}</color>.",
                ["KillMessage5"] = "<color=#ffae00>{0}</color> misplaced their weapon inside of <color=#ffae00>{1}</color>.",
                ["DeathMessage1"] = "<color=#ffae00>{0}</color> tripped on a branch and died.",
                ["DeathMessage2"] = "A cold breeze got the better of <color=#ffae00>{0}</color>!",
                ["DeathMessage3"] = "<color=#ffae00>{0}</color> decided that they no longer wish to play, so they died.",
                ["EventStillDespawning"] = "The arena is still being cleared from the last event. Please try again in a few seconds.",
                ["InvalidProfile"] = "You have specified an invalid loot profile [{0}]. Valid profiles:\n- {1}",
                ["PrizesDisabled"] = "This command is not enabled as there are no prizes specified in the config.",
                ["CircleStatusUpdate"] = "Circle status: {0}",
                ["CircleStatusMOVING"] = "<color=#fff700>MOVING</color>",
                ["CircleStatusSTOPPED"] = "<color=#00ff00>STOPPED</color>",
                ["CircleStatusINACTIVE"] = "<color=#ff0000>INACTIVE</color>",
                ["UILeaveButton"] = "<color=#ff0000>LEAVE</color>",
                ["SAJointAnnounceMsg_1"] = "<color=#00fff7>{0}</color> has decided to join the action!",
                ["SAJointAnnounceMsg_2"] = "<color=#00fff7>{0}</color> has joined the event and is ready to take some names!",
                ["SAJointAnnounceMsg_3"] = "Oh no, <color=#00fff7>{0}</color> has joined the event!",
                ["NoCommand"] = "You cannot use this command while in Survival Arena.",
                ["RestoreItemsReminder"] = "You have items that need to be recovered. Use the <color=#00ff00>{0}</color> command to recover them.",
                ["RestoreItemsCanJoinEvent"] = "You still have items stored from a previous event. Recover them first using the <color=#00ff00>/{0}</color> command before attempting to join the event.",
                ["CorpseLocationBlocked"] = "You cannot teleport to your Suvival Arena corpse.",
                ["CorpseLocationFromArena"] = "You cannot teleport while you are at an event.",
                ["HasItemsInInventory"] = "You cannot join this event if you have items in your inventory.",
                ["NoCrafting"] = "You are not allowed to craft at this event.",
                ["DeadOrWounded"] = "Cannot join the event while dead or wounded.",
                ["NoRestoreAtEvent"] = "You cannot restore your items while you are at the event.",
                ["EscapeBlocked"] = "You cannot join the event while escape blocked.",
                ["BlackListedItem"] = "You cannot join the event with {0} in your inventory.",
                ["BlackListedItemSub"] = "You cannot join the event: {0} contains a blacklisted item: {1}. Remove it first. ",
                ["PreventTeamCreation"] = "You cannot create a team while at this event.",
                ["NSMessageTitle"] = "Congratulations on your win!",
                ["NSMessageBody"] = "You won a game of <color=#30BFFF>Survival arena</color> and were awarded a prize redemption.\n<color=#38F527>You have {0} prizes available.</color>\nClick the <color=#EDCE02>Redeem</color> button or type <color=#EDCE02>/sprize</color> to redeem your prize(s).",

            }, this);
        }

        #endregion

        #region Data

        PlayerEntity pcdData;
        SpawnedEntities spawnData;

        private DynamicConfigFile PCDDATA;
        private DynamicConfigFile SPAWNDATA;

        Dictionary<string, DynamicConfigFile> ARENASDATA = new Dictionary<string, DynamicConfigFile>();
        Dictionary<string, ArenaData> Arenas = new Dictionary<string, ArenaData>();

        public static Vector3 CurrentCentrePoint;
        ArenaData CurrentArena;

        const string subDirectory = "survivalArena/";
        const string perm_admin = "survivalarena.admin";
        const string perm_autojoin = "survivalarena.autojoin";

        void Init()
        {
            UnsubHooks();
            LoadData();

            permission.RegisterPermission(perm_admin, this);
            permission.RegisterPermission(perm_autojoin, this);

            cmd.AddChatCommand(config.command_settings.join_command, this, nameof(JoinEvent));
            cmd.AddChatCommand(config.command_settings.leave_command, this, nameof(LeaveEventCMD));
            cmd.AddChatCommand(config.command_settings.score_command, this, nameof(SendBoard));
            cmd.AddChatCommand(config.eventSettings.restore_items_command, this, nameof(RestoreChatCMD));
            cmd.AddConsoleCommand(config.eventSettings.restore_items_command, this, nameof(RestoreConsoleCMD));
        }

        void Unload()
        {
            Pool.FreeUnmanaged(ref Scores);
            try
            {
                EndEvent(true);
            }
            catch { }
            SaveData();

            cmd.RemoveChatCommand(config.command_settings.join_command, this);
            cmd.RemoveChatCommand(config.command_settings.leave_command, this);
            cmd.RemoveChatCommand(config.command_settings.score_command, this);
            cmd.RemoveChatCommand(config.eventSettings.restore_items_command, this);
            cmd.RemoveConsoleCommand(config.eventSettings.restore_items_command, this);
            foreach (var player in BasePlayer.activePlayerList)
            {
                try
                {
                    DestroyMonitor(player);
                    DestroyOutsiderMonitor(player);
                    DestroyTempRestriction(player);
                }
                catch { }
            }

            foreach (var kvp in TempTracking)
            {
                if (kvp.Key == null) continue;
                DestroyTempRestriction(kvp.Key, false);
            }

            TempTracking?.Clear();

            if (Spawn_routine != null) ServerMgr.Instance.StopCoroutine(Spawn_routine);
            Spawn_routine = null;

            ServerMgr.Instance.StartCoroutine(DespawnEntities(0f, false, true));
            foreach (var player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, "GameStartUI");
                CuiHelper.DestroyUi(player, "MenuBackground");
                CuiHelper.DestroyUi(player, "STARTCountdownUI");
                CuiHelper.DestroyUi(player, "EventLeaveButton");
                CuiHelper.DestroyUi(player, "CircleStatusHUD");
                CuiHelper.DestroyUi(player, "PlayerCounter");
                CuiHelper.DestroyUi(player, "SurvivalScoreBoard");
                CuiHelper.DestroyUi(player, "SurvivalScoreBackground");
            }
            DeathFeed?.Unload();

            SaveData(SaveType.Spawn);
            if (config.eventSettings.use_event_helper_timer && EventHelper != null && EventHelper.IsLoaded) EventHelper.Call("EMRemoveEvent", this.Name);
            if (WasEventPlayerList != null) Pool.FreeUnmanaged(ref WasEventPlayerList);
        }

        public enum SphereColour
        {
            Black,
            Red,
            Green,
            Purple            
        }

        enum SaveType
        {
            Both,
            Player,
            Arena,
            Spawn
        }

        void SaveData(SaveType saveType = SaveType.Player)
        {
            if (saveType == SaveType.Both || saveType == SaveType.Player) PCDDATA.WriteObject(pcdData);
            if (saveType == SaveType.Spawn) SPAWNDATA.WriteObject(spawnData);
            if (saveType == SaveType.Both || saveType == SaveType.Arena)
            {
                foreach (var arena in Arenas)
                {
                    //ArenaData arenaData;
                    //if (Arenas.TryGetValue(DATA.Key, out arenaData)) DATA.Value.WriteObject(arenaData);
                    SaveArenaData(arena.Key);
                }

                //ARENADATA.WriteObject(arenaData);
            }
        }

#if CARBON
        string ArenaDirectory = Carbon.Core.Defines.GetDataFolder() + Path.DirectorySeparatorChar + "survivalArena" + Path.DirectorySeparatorChar;
#else
        string ArenaDirectory = Interface.Oxide.DataDirectory + Path.DirectorySeparatorChar + "survivalArena" + Path.DirectorySeparatorChar;
#endif

        void SaveArenaData(string name)
        {
            try
            {
                ArenaData data;
                if (!Arenas.TryGetValue(name, out data))
                {
                    Puts($"Attempted to save changes for {name}, but it does not exist in ArenaData.");
                    return;
                }

                var json = JsonConvert.SerializeObject(data);
                if (!Directory.Exists(ArenaDirectory)) Directory.CreateDirectory(ArenaDirectory);
                File.WriteAllText(ArenaDirectory + name + ".json", json);
            }
            catch (Exception ex)
            {
                Puts($"Exception encountered while trying to save {name ?? "null"}. Exception: {ex.Message}");
            }
        }

        bool HaveArenaFile = false;
        void LoadData()
        {
            try
            {
                PCDDATA = Interface.Oxide.DataFileSystem.GetFile(subDirectory + "player_data");
                pcdData = Interface.Oxide.DataFileSystem.ReadObject<PlayerEntity>(subDirectory + "player_data");
            }
            catch
            {
                Puts("Couldn't load player data, creating new Playerfile");
                pcdData = new PlayerEntity();
            }

            try
            {
                SPAWNDATA = Interface.Oxide.DataFileSystem.GetFile(subDirectory + "spawn_data");
                spawnData = Interface.Oxide.DataFileSystem.ReadObject<SpawnedEntities>(subDirectory + "spawn_data");
            }
            catch
            {
                Puts("Could not load spawn_data file.");
                spawnData = new SpawnedEntities();
            }

            // Arena files are loaded in OnServerInitialized.
            //if (Interface.Oxide.DataFileSystem.ExistsDatafile(subDirectory + "Arena"))
            //{
            //    ARENADATA = Interface.Oxide.DataFileSystem.GetFile(subDirectory + "Arena");
            //    try
            //    {
            //        arenaData = Interface.Oxide.DataFileSystem.ReadObject<ArenaData>(subDirectory + "Arena");
            //        Puts($"Found and loaded arena data - Entities: {arenaData.entities?.Count}");
            //        HaveArenaFile = true;
            //    }
            //    catch
            //    {
            //        Puts("Couldn't load arena data, creating new Arena data file");
            //        AddArenaFile();
            //    }
            //}
        }

        class ArenaData
        {
            public float size = 210;
            public Vector3 CenterPoint;
            public List<EntityInfo> entities = new List<EntityInfo>();
        }

        public class EntityInfo
        {
            public string prefab;
            public Vector3 pos;
            public Vector3 rot;
            public Vector3 scale;

            public EntityInfo(string prefab, Vector3 pos, Vector3 rot, Vector3 scale)
            {
                this.prefab = prefab;
                this.pos = pos;
                this.rot = rot;
                this.scale = scale;
            }
        }

        class SpawnedEntities
        {
            public List<ulong> spawnedEntities = new List<ulong>();
        }

        class PlayerEntity
        {
            public Dictionary<ulong, PCDInfo> pEntity = new Dictionary<ulong, PCDInfo>();
            public Dictionary<ulong, ScoreInfo> scoreData = new Dictionary<ulong, ScoreInfo>();
        }

        class ScoreInfo
        {
            public string name;
            public int wins;
            public int kills;
            public int games;
            public ScoreInfo(string name, int wins = 0, int kills = 0, int games = 0)
            {
                this.name = name;
                this.wins = wins;
                this.kills = kills;
                this.games = games;
            }
        }

        class PCDInfo
        {
            public bool GrantedNightVision;
            public int rewards_remaining;
            public Vector3 Location;
            public float participation_rewards_modifier = 0;
            public string Modifiers;
            public string Metabolism;
            public float Health;
            public bool StripOnItemRestore;

            public List<ItemInfo> Items = new List<ItemInfo>();
        }
        public enum ContainerType
        {
            Main,
            Belt,
            Wear,
            None
        }

        public class ItemInfo
        {
            public string shortname;
            public ulong skin;
            public int amount;
            public float condition;
            public float maxCondition;
            public int ammo;
            public string ammotype;
            public ContainerType container;
            public int position;
            public int frequency;
            public Item.Flag flags;
            public KeyInfo instanceData;
            public class KeyInfo
            {
                public int dataInt;
                public int blueprintTarget;
                public int blueprintAmount;
                public float dataFloat;
            }
            public int contentsSlots;
            public List<ItemInfo> item_contents;
            public string text;
            public string name;
            public List<ItemOwnershipShare> ownershipShares;
            public int armorSlots;
        }

        #endregion;

        #region Get Arena

        //[ConsoleCommand("getarena")]
        //void GetArena(ConsoleSystem.Arg arg)
        //{
        //    var player = arg.Player();
        //    if (player != null && !IsAdmin(player))
        //    {
        //        PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("NoPerms", this, player.UserIDString));
        //        return;
        //    }

        //    if (arg.Args == null || arg.Args.Length == 0)
        //    {
        //        arg.ReplyWith("You must specify the arena name");
        //        return;
        //    }

        //    GetArena((BasePlayer)null);
        //}

        [ChatCommand("getarena")]
        void GetArena(BasePlayer player, string cmd, string[] args)
        {
            if (player != null && !IsAdmin(player))
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("NoPerms", this, player.UserIDString));
                return;
            }
            if (string.IsNullOrEmpty(ConVar.Server.levelurl) || !ConVar.Server.levelurl.Contains("SurvivalArena"))
            {
                if (player != null) PrintToChat(player, "The map file URL on your server must contain the name SurvivalArena in order to run this command.");
                else Puts("The map file on your server must contain the name SurvivalArena in order to run this command.");
                return;
            }

            if (args == null || args.Length == 0)
            {
                if (player != null) PrintToChat(player, "You must specify a name for the arena");
                else Puts("You must specify a name for the arena");
                return;
            }

            var name = string.Join(" ", args);
            name = name.Replace(" ", "_");

            ArenaData arenaData = new ArenaData();

            foreach (var entity in spawned_entities)
            {
                entity?.KillMessage();
            }

            arenaData.entities.Clear();
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                if (entity is BaseNpc || entity is Rust.Ai.Gen2.BaseNPC2 || entity is BasePlayer) continue;
                if (!IsWhitelisted(entity.PrefabName)) continue;
                if (entity.PrefabName == "assets/prefabs/deployable/bbq/bbq.deployed.prefab")
                {
                    Puts("Found and stored center point.");
                    arenaData.CenterPoint = entity.transform.position;
                    continue;
                }

                // Replaces fire pit with wooden external gates
                if (entity.PrefabName == "assets/prefabs/misc/halloween/skull_fire_pit/skull_fire_pit.prefab")
                {
                    arenaData.entities.Add(new EntityInfo("assets/prefabs/building/gates.external.high/gates.external.high.wood/gates.external.high.wood.prefab", entity.transform.position, entity.transform.rotation.eulerAngles, entity.transform.localScale));
                    continue;
                }

                // Replaces snow machine with roof triangles
                if (entity.PrefabName == "assets/prefabs/misc/xmas/snow_machine/models/snowmachine.prefab")
                {
                    arenaData.entities.Add(new EntityInfo("assets/prefabs/building core/roof.triangle/roof.triangle.prefab", entity.transform.position, entity.transform.rotation.eulerAngles, entity.transform.localScale));
                    continue;
                }

                // Replaces carvable pumpkins with loot crates
                if (entity.PrefabName == "assets/prefabs/misc/halloween/carvablepumpkin/carvable.pumpkin.prefab")
                {
                    arenaData.entities.Add(new EntityInfo("assets/bundled/prefabs/radtown/crate_normal_2.prefab", entity.transform.position, entity.transform.rotation.eulerAngles, entity.transform.localScale));
                    continue;
                }

                arenaData.entities.Add(new EntityInfo(entity.PrefabName, entity.transform.position, entity.transform.rotation.eulerAngles, entity.transform.localScale));
            }

            float furthest = 0f;
            foreach (var entity in arenaData.entities)
            {
                if (entity.prefab == "assets/prefabs/deployable/bbq/bbq.deployed.prefab") continue;
                if (InRange(entity.pos, arenaData.CenterPoint, furthest)) continue;
                furthest = Vector3.Distance(entity.pos, arenaData.CenterPoint);
            }

            arenaData.size = furthest / 2;

            if (Arenas.ContainsKey(name))
            {
                Arenas[name] = arenaData;
                PrintToChat(player, $"Overwrote old arena: {name}");
            }
            else
            {
                Arenas.Add(name, arenaData);
                PrintToChat(player, $"Added new arena: {name}");
            }

            SaveData(SaveType.Arena);
        }

        [ChatCommand("spawnarena")]
        void SpawnArena(BasePlayer player, string cmd, string[] args)
        {
            if (!IsAdmin(player))
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("NoPerms", this, player.UserIDString));
                return;
            }
            if (IsDespawning)
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player?.UserIDString) + lang.GetMessage("EventStillDespawning", this, player.UserIDString));
                return;
            }

            if (args == null || args.Length == 0)
            {
                PrintToChat(player, $"You must specify the arena. Available arenas:\n{string.Join("\n", Arenas.Keys)}");
                return;
            }

            string arenaName = args[0];
            if (!Arenas.ContainsKey(arenaName))
            {
                PrintToChat(player, $"{arenaName} is not a valid arena! Valid arenas:\n{string.Join("\n", Arenas.Keys)}");
                return;
            }

            float heightMod = args != null && args.Length > 1 ? Convert.ToSingle(args[1]) : 0f;
            CurrentArena = Arenas[arenaName];
            StartSpawnSequence(heightMod, false);
            DevMode = true;
        }

        [ChatCommand("cleararena")]
        void ClearArena(BasePlayer player)
        {
            if (!IsAdmin(player))
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("NoPerms", this, player.UserIDString));
                return;
            }
            EndEvent();
        }

        bool IsWhitelisted(string prefab)
        {
            foreach (var item in prefab_whitelist)
            {
                if (prefab.StartsWith(item)) return true;
            }
            return false;
        }

        List<string> prefab_whitelist = new List<string>()
        {
            "assets/bundled/prefabs/modding/admin/",
            "assets/bundled/prefabs/autospawn/resource/",
            "assets/prefabs/misc/xmas/",
            "assets/prefabs/deployable/",
            "assets/prefabs/building/",
            "assets/prefabs/building core/",
            "assets/prefabs/misc/halloween/",
            "assets/prefabs/misc/desertbasedwelling/"
        };

        [ChatCommand("setcentrepoint")]
        void SetCentrePoint(BasePlayer player, string cmd, string[] args)
        {
            if (!IsAdmin(player))
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("NoPerms", this, player.UserIDString));
                return;
            }

            if (CanJoin || IsRunning || DevMode)
            {
                PrintToChat(player, "The arena cannot be spawned when running this command. Please despawn the arena with /endarena.");
                return;
            }

            if (args == null || args.Length == 0)
            {
                PrintToChat(player, $"You must specify the arena name. Available arenas:\n{string.Join("\n", Arenas.Keys)}");
                return;
            }

            var name = string.Join(" ", args);

            MoveCentrePoint(player, player.transform.position, name);
        }

        void MoveCentrePoint(BasePlayer player, Vector3 new_pos, string arena)
        {
            ArenaData arenaData;
            if (!Arenas.TryGetValue(arena, out arenaData))
            {
                PrintToChat(player, $"Failed to find the arena: {arena}");
                return;
            }
            foreach (var entry in arenaData.entities)
            {
                var diff_x = Math.Abs(arenaData.CenterPoint.x - new_pos.x);
                if (arenaData.CenterPoint.x > new_pos.x) entry.pos.x -= diff_x;
                else entry.pos.x += diff_x;

                var diff_y = Math.Abs(arenaData.CenterPoint.y - new_pos.y);
                if (arenaData.CenterPoint.y > new_pos.y) entry.pos.y -= diff_y;
                else entry.pos.y += diff_y;

                var diff_z = Math.Abs(arenaData.CenterPoint.z - new_pos.z);
                if (arenaData.CenterPoint.z > new_pos.z) entry.pos.z -= diff_z;
                else entry.pos.z += diff_z;
            }
            arenaData.CenterPoint = new_pos;
            SaveData(SaveType.Arena);
            PrintToChat(player, $"Set new position: {new_pos} and adjusted points.");
        }

        #endregion

        #region Spawn Arena

        bool IsAdmin(BasePlayer player)
        {
            return permission.UserHasPermission(player.UserIDString, perm_admin);
        }

        public List<BaseEntity> spawned_entities = new List<BaseEntity>();
        public List<LootContainer> containerSpawns = new List<LootContainer>();
        public List<ulong> containerSpawnIDs = new List<ulong>();
        public List<Door> doors = new List<Door>();
        bool ManualStart = false;

        string GetRandomArena()
        {
            List<string> arenas = Pool.Get<List<string>>();
            arenas.AddRange(Arenas.Keys);
            var name = arenas.GetRandom();
            Pool.FreeUnmanaged(ref arenas);
            return name;
        }

        [ConsoleCommand("startarena")]
        void StartArena(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null && !IsAdmin(player))
            {
                arg.ReplyWith(lang.GetMessage("Prefix", this, player?.UserIDString) + lang.GetMessage("NoPerms", this, player?.UserIDString));
                return;
            }
            if (IsRunning || CanJoin)
            {
                arg.ReplyWith(lang.GetMessage("Prefix", this, player?.UserIDString) + lang.GetMessage("EventRunning", this, player?.UserIDString));
                return;
            }
            if (IsDespawning)
            {
                arg.ReplyWith(lang.GetMessage("Prefix", this, player?.UserIDString) + lang.GetMessage("EventStillDespawning", this, player?.UserIDString));
                return;
            }
            float heightMod = arg.Args != null && arg.Args.Length > 0 ? Convert.ToSingle(arg.GetString(0)) : 0f;
            int timerOverride = arg.Args != null && arg.Args.Length > 1 ? Convert.ToInt32(arg.GetString(1)) : 0;
            string arenaName = arg.Args != null && arg.Args.Length > 2 ? arg.GetString(2) : GetRandomArena();
            string profile = arg.Args != null && arg.Args.Length > 3 ? string.Join(" ", arg.Args.Skip(3)) : null;
            if (profile != null)
            {
                bool validated_profile = false;
                foreach (var prof in config.lootSettings)
                {
                    if (prof.Key.Equals(profile, StringComparison.OrdinalIgnoreCase))
                    {
                        validated_profile = true;
                        profile = prof.Key;
                        break;
                    }
                }
                if (!validated_profile)
                {
                    arg.ReplyWith(lang.GetMessage("Prefix", this, player?.UserIDString) + string.Format(lang.GetMessage("InvalidProfile", this, player?.UserIDString), profile, string.Join("\n- ", config.lootSettings.Keys)));
                    return;
                }
            }
            ManualStart = true;
            if (timerOverride > 0) LobbyTime = timerOverride;
            StartEvent(arenaName, heightMod, profile);
        }

        [ChatCommand("startarena")]
        void SpawnChatCMD(BasePlayer player, string cmd, string[] args)
        {
            if (!IsAdmin(player))
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("NoPerms", this, player.UserIDString));
                return;
            }
            if (IsRunning || CanJoin)
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("EventRunning", this, player.UserIDString));
                return;
            }
            if (IsDespawning)
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("EventStillDespawning", this, player.UserIDString));
                return;
            }
            float heightMod = args != null && args.Length >= 1 ? Convert.ToSingle(args[0]) : 0f;
            int timerOverride = args != null && args.Length >= 2 ? Convert.ToInt32(args[1]) : 0;
            string arenaName = args != null && args.Length >= 3 ? Arenas.ContainsKey(args[2]) ? args[2] : GetRandomArena() : GetRandomArena();
            string profile = args != null && args.Length >= 4 ? string.Join(" ", args.Skip(3)) : null;

            if (profile != null)
            {
                bool validated_profile = false;
                foreach (var prof in config.lootSettings)
                {
                    if (prof.Key.Equals(profile, StringComparison.OrdinalIgnoreCase))
                    {
                        validated_profile = true;
                        profile = prof.Key;
                        break;
                    }
                }
                if (!validated_profile)
                {
                    PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + string.Format(lang.GetMessage("InvalidProfile", this, player.UserIDString), profile, string.Join("\n- ", config.lootSettings.Keys)));
                    return;
                }
            }

            ManualStart = true;
            if (timerOverride > 0) LobbyTime = timerOverride;
            StartEvent(arenaName, heightMod, profile);
        }

        [ConsoleCommand("endarena")]
        void EndArenaConsole(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null && !IsAdmin(player))
            {
                arg.ReplyWith(lang.GetMessage("Prefix", this, player?.UserIDString) + lang.GetMessage("NoPerms", this, player?.UserIDString));
                return;
            }

            EndEvent();
        }

        [ChatCommand("endarena")]
        void EndArenaCMD(BasePlayer player)
        {
            if (!IsAdmin(player))
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("NoPerms", this, player.UserIDString));
                return;
            }
            EndEvent();
        }

        void StartSpawnSequence(float height = 0f, bool runEvent = true)
        {
            Puts("Attempting to build arena.");
            if (spawned_entities.Count > 0)
            {
                Puts("Stopping build attempt while we despawn the old arena.");
                ServerMgr.Instance.StartCoroutine(DespawnEntities(height, true));
                return;
            }
            Spawn_routine = ServerMgr.Instance.StartCoroutine(SpawnEntities(height, runEvent));
        }

        public IEnumerator DespawnEntities(float heightMod = 0f, bool startArena = false, bool unloaded = false)
        {
            IsDespawning = true;
            Puts("Starting despawn process.");
            Unsubscribe(nameof(OnEntityKill));
            foreach (var _timer in ContainerRespawnTimers)
            {
                if (_timer != null && !_timer.Destroyed) _timer.Destroy();
            }
            ContainerRespawnTimers?.Clear();
            int count = 0;
            
            if (spawned_entities.Count > 0)
            {
                List<BaseEntity> entities = Pool.Get<List<BaseEntity>>();
                entities.AddRange(spawned_entities);
                Puts($"Starting despawn process. Entities to despawn: {spawned_entities.Count}");

                foreach (var entity in entities)
                {
                    try
                    {
                        if (entity != null && !entity.IsDestroyed)
                        {
                            count++;
                            entity.Kill();
                        }
                    }
                    catch { }

                    if (count >= config.max_procs_per_tick && !unloaded)
                    {
                        count = 0;
                        yield return CoroutineEx.waitForEndOfFrame;
                    }
                }
                Pool.FreeUnmanaged(ref entities);
                Puts("Finished despawning arena entities.");
                spawned_entities.Clear();
                spawnData.spawnedEntities.Clear();
            }

            count = 0;
            if (containerSpawns.Count > 0)
            {
                List<BaseEntity> containers = Pool.Get<List<BaseEntity>>();
                containers.AddRange(containerSpawns);
                Puts("Starting despawn process for container spawns.");
                foreach (var crate in containers)
                {
                    try
                    {
                        if (crate != null && !crate.IsDestroyed)
                        {
                            crate.Kill();
                            count++;
                        }
                    }
                    catch { }

                    if (count >= config.max_procs_per_tick && !unloaded)
                    {
                        count = 0;
                        yield return CoroutineEx.waitForEndOfFrame;
                    }
                }
                Pool.FreeUnmanaged(ref containers);
                Puts("Finished despawning container entities.");
                containerSpawns.Clear();
                containerSpawnIDs.Clear();
            }
            if (PlayerCorpses.Count > 0)
            {
                List<BaseEntity> corpses = Pool.Get<List<BaseEntity>>();
                corpses.AddRange(PlayerCorpses);
                foreach (var corpse in corpses)
                {
                    try
                    {
                        corpse.Kill();
                    }
                    catch { }
                }
                Pool.FreeUnmanaged(ref corpses);
            }
            
            PlayerCorpses?.Clear();
            CorpseMonitor?.Clear();

            Subscribe(nameof(OnEntityKill));
            Puts("Finished despawn process.");
            IsDespawning = false;
            NextTick(() => Despawn_routine = null);
            if (startArena) StartSpawnSequence(heightMod);
        }

        Coroutine Spawn_routine;
        Coroutine Despawn_routine;
        static float FurthestEntity;

        public IEnumerator SpawnEntities(float height = 0f, bool runEvent = true)
        {
            int count = 0;
            EventElevationMod = height;
            FurthestEntity = 0f;
            CurrentCentrePoint = GetModifiedVector(CurrentArena.CenterPoint);

            foreach (var entry in CurrentArena.entities)
            {
                try
                {
                    var pos = new Vector3(entry.pos.x, entry.pos.y + height, entry.pos.z);
                    var eulerRotation = entry.rot;
                    BaseEntity entity;
                    if (entry.prefab == "assets/prefabs/deployable/playerioents/gates/branch/electrical.branch.deployed.prefab")
                    {
                        var rot = new Vector3(eulerRotation.x, eulerRotation.y + UnityEngine.Random.Range(-90f, 90f), eulerRotation.z);
                        entity = GameManager.server.CreateEntity(GetRandomBush(pos), pos, Quaternion.Euler(rot));
                    }
                    else if (entry.prefab == "assets/prefabs/misc/xmas/pookie/pookie_deployed.prefab")
                    {
                        var rot = new Vector3(eulerRotation.x, eulerRotation.y + UnityEngine.Random.Range(-90f, 90f), eulerRotation.z);
                        entity = GameManager.server.CreateEntity(GetRandomTree(pos), pos, Quaternion.Euler(rot));
                        if (entity is TreeEntity tree)
                        {
                            tree.treeAdditionSpawnChance = -1f;
                        }
                    }
                    else if (entry.prefab == "assets/bundled/prefabs/modding/admin/admin_rock_formation_medium_a.prefab")
                    {
                        var rot = new Vector3(eulerRotation.x, eulerRotation.y + UnityEngine.Random.Range(-90f, 90f), eulerRotation.z);
                        entity = GameManager.server.CreateEntity(entry.prefab, pos, Quaternion.Euler(rot));
                    }
                    else if (entry.prefab == "assets/prefabs/deployable/playerioents/lights/flasherlight/electric.flasherlight.deployed.prefab")
                    {
                        var rot = new Vector3(eulerRotation.x, eulerRotation.y + UnityEngine.Random.Range(-90f, 90f), eulerRotation.z);
                        entity = GameManager.server.CreateEntity(GetRandomLog(pos), pos, Quaternion.Euler(rot));
                    }
                    else if (entry.prefab == "assets/bundled/prefabs/radtown/crate_normal_2.prefab")
                    {
                        var rot = new Vector3(eulerRotation.x, eulerRotation.y + UnityEngine.Random.Range(-90f, 90f), eulerRotation.z);
                        entity = GameManager.server.CreateEntity(entry.prefab, pos, Quaternion.Euler(rot));
                        containerSpawns.Add(entity as LootContainer);
                    }
                    else entity = GameManager.server.CreateEntity(entry.prefab, pos, Quaternion.Euler(eulerRotation));
                    if (entity == null) continue;

                    entity.enableSaving = false;

                    var stabilityEntity = entity as StabilityEntity;
                    if (stabilityEntity != null)
                    {
                        stabilityEntity.grounded = true;
                    }

                    var groundWatch = entity.GetComponent<GroundWatch>();
                    if (groundWatch != null) UnityEngine.Object.Destroy(groundWatch);

                    var groundMissing = entity.GetComponent<DestroyOnGroundMissing>();
                    if (groundMissing != null) UnityEngine.Object.Destroy(groundMissing);

                    var buildingBlock = entity as BuildingBlock;
                    if (buildingBlock != null)
                    {
                        buildingBlock.blockDefinition = PrefabAttribute.server.Find<Construction>(buildingBlock.prefabID);
                        buildingBlock.SetGrade(BuildingGrade.Enum.Wood);
                        buildingBlock.grounded = true;
                    }

                    if (entry.prefab.StartsWith("assets/prefabs/misc/desertbasedwelling/")) HandleDwelling(entity as NPCDwelling);

                    if (entry.scale != Vector3.one && entry.scale != Vector3.zero)
                    {
                        entity.networkEntityScale = true;
                        entity.transform.localScale = entry.scale;
                    }

                    entity.Spawn();

                    if (entry.prefab == "assets/bundled/prefabs/radtown/crate_normal_2.prefab") containerSpawnIDs.Add(entity.net.ID.Value);

                    var baseCombat = entity as BaseCombatEntity;
                    if (baseCombat != null) baseCombat.SetHealth(baseCombat.MaxHealth());

                    var cp = CurrentArena.CenterPoint;
                    if (entity is Door)
                    {
                        NextTick(() =>
                        {
                            try
                            {
                                SetupDoor(entity as Door);
                            }
                            catch { }
                        });
                    }
                    spawned_entities.Add(entity);
                    spawnData.spawnedEntities.Add(entity.net.ID.Value);

                    var distFromCentre = Vector3.Distance(entity.transform.position, CurrentCentrePoint);
                    if (distFromCentre > FurthestEntity)
                    {
                        FurthestEntity = distFromCentre;
                    }
                }
                catch { }

                count++;
                if (count >= config.max_procs_per_tick)
                {
                    count = 0;
                    yield return CoroutineEx.waitForEndOfFrame;
                }
            }


            if (runEvent) NextTick(() => ArenaSpawned());
            Spawn_routine = null;
            FurthestEntity += 10f;
        }

        void HandleDwelling(NPCDwelling dwelling)
        {
            // Removes NPC and crate spawns from the dwelling entitiy.
            dwelling.NPCSpawnChance = 0;
            if (dwelling.spawnGroups != null)
            {
                foreach (var group in dwelling.spawnGroups)
                {
                    group.prefabs.Clear();
                }
            }
        }

        void SetupDoor(Door door)
        {
            if (door == null) return;
            if (!doors.Contains(door))
            {
                doors.Add(door);
            }

            if (door.IsLocked()) return;
            var key_lock = GameManager.server.CreateEntity("assets/prefabs/locks/keylock/lock.key.prefab") as KeyLock;
            key_lock.gameObject.Identity();
            key_lock.SetParent(door, door.GetSlotAnchorName(BaseEntity.Slot.Lock));
            key_lock.Spawn();
            door.SetSlot(BaseEntity.Slot.Lock, key_lock);
            using (var scope = key_lock.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))
                scope.Set(BaseEntity.Flags.Locked, true);
            spawned_entities.Add(key_lock);
        }

        // Add biome parameter later.
        string GetRandomBush(Vector3 pos)
        {
            switch (GetBiome(pos))
            {
                case BiomeType.Arid: return config.biome_bushes[BiomeType.Arid].GetRandom();
                case BiomeType.Temperate: return config.biome_bushes[BiomeType.Temperate].GetRandom();
                case BiomeType.Arctic: return config.biome_bushes[BiomeType.Arctic].GetRandom();
                case BiomeType.Tundra: return config.biome_bushes[BiomeType.Tundra].GetRandom();
                case BiomeType.Jungle: return config.biome_bushes[BiomeType.Jungle].GetRandom();
            }

            return "assets/bundled/prefabs/autospawn/resource/v3_bushes_temp/bush_spicebush_b.prefab";
        }

        string GetRandomTree(Vector3 pos)
        {
            switch (GetBiome(pos))
            {
                case BiomeType.Arid: return config.biome_trees[BiomeType.Arid].GetRandom();
                case BiomeType.Temperate: return config.biome_trees[BiomeType.Temperate].GetRandom();
                case BiomeType.Arctic: return config.biome_trees[BiomeType.Arctic].GetRandom();
                case BiomeType.Tundra: return config.biome_trees[BiomeType.Tundra].GetRandom();
                case BiomeType.Jungle: return config.biome_trees[BiomeType.Jungle].GetRandom();
            }

            return "assets/bundled/prefabs/autospawn/resource/v3_temp_forest_pine/pine_a.prefab";
        }

        string GetRandomLog(Vector3 pos)
        {
            switch (GetBiome(pos))
            {
                case BiomeType.Arid: return config.biome_logs[BiomeType.Arid].GetRandom();
                case BiomeType.Temperate: return config.biome_logs[BiomeType.Temperate].GetRandom();
                case BiomeType.Arctic: return config.biome_logs[BiomeType.Arctic].GetRandom();
                case BiomeType.Tundra: return config.biome_logs[BiomeType.Tundra].GetRandom();
                case BiomeType.Jungle: return config.biome_logs[BiomeType.Jungle].GetRandom();
            }

            return "assets/bundled/prefabs/autospawn/resource/logs_dry/dead_log_a.prefab";
        }

        public enum BiomeType
        {
            Arid,
            Temperate,
            Tundra,
            Arctic,
            Jungle
        }

        BiomeType GetBiome(Vector3 pos)
        {
            if (TerrainMeta.BiomeMap.GetBiome(pos, 1) > 0.5f) return BiomeType.Arid;
            else if (TerrainMeta.BiomeMap.GetBiome(pos, 2) > 0.5f) return BiomeType.Temperate;
            else if (TerrainMeta.BiomeMap.GetBiome(pos, 4) > 0.5f) return BiomeType.Tundra;
            else if (TerrainMeta.BiomeMap.GetBiome(pos, 8) > 0.5f) return BiomeType.Arctic;
            else if (TerrainMeta.BiomeMap.GetBiome(pos, 16) > 0.5f) return BiomeType.Jungle;
            return BiomeType.Temperate;
        }

        #endregion

        #region Loot handling

        void OnEntityKill(LootContainer container)
        {
            if (containerSpawns.Contains(container))
            {
                containerSpawns.Remove(container);
                containerSpawnIDs.Remove(container.net.ID.Value);
                var pos = container.transform.position;
                HandleRespawn(pos);
            }
        }

        List<Timer> ContainerRespawnTimers = new List<Timer>();

        void HandleRespawn(Vector3 pos)
        {
            ContainerRespawnTimers.Add(timer.Once(UnityEngine.Random.Range(config.gameSettings.min_crate_respawn_time, config.gameSettings.max_crate_respawn_time), () =>
            {
                var rot = new Vector3(0, UnityEngine.Random.Range(-90f, 90f), 0);
                var container = GameManager.server.CreateEntity("assets/bundled/prefabs/radtown/crate_normal_2.prefab", pos, Quaternion.Euler(rot)) as LootContainer;
                containerSpawns.Add(container);
                container.Spawn();
                containerSpawnIDs.Add(container.net.ID.Value);
            }));
        }

        #endregion

        #region Event

        Dictionary<ulong, BasePlayer> Participants = new Dictionary<ulong, BasePlayer>();
        int DiedCount = 0;
        bool CanJoin = false;
        bool IsRunning = false;
        bool IsDespawning = false;
        int LobbyTime = 300;
        bool DevMode = false;

        string LootProfile;
        static float EventElevationMod = 0f;

        static Vector3 GetModifiedVector(Vector3 pos)
        {
            if (EventElevationMod == 0) return pos;
            else return new Vector3(pos.x, pos.y + EventElevationMod, pos.z);
        }

        void StartEvent(string arenaName, float elevationMod = 0f, string profile = null)
        {
            if (config.lootSettings == null || config.lootSettings.Count == 0)
            {
                Puts("Failed to start event - no loot settings.");
                return;
            }
            if (string.IsNullOrEmpty(arenaName))
            {
                Puts("Failed to load the arena - no valid arena specified.");
                return;
            }
            if (!Arenas.TryGetValue(arenaName, out var arena))
            {
                Puts($"Failed to load the arena - {arenaName} does not exist.");
                return;
            }
            CurrentArena = arena;
            if (string.IsNullOrEmpty(profile))
            {
                LootProfile = GetRandomProfile();
            }
            else LootProfile = profile;
            if (!config.lootSettings.ContainsKey(LootProfile))
            {
                Puts($"Failed to start event - settings failure.");
                return;
            }
            Puts($"Set loot profile: {LootProfile}");
            IsRunning = true;

            Subscribe(nameof(OnLootSpawn));
            Subscribe(nameof(OnPlayerDeath));
            Subscribe(nameof(OnPlayerCorpseSpawn));
            Subscribe(nameof(CanLootEntity));
            Subscribe(nameof(CanLootPlayer));
            Subscribe(nameof(OnEntityKill));
            Subscribe(nameof(CanHelicopterTarget));
            Subscribe(nameof(CanHelicopterStrafeTarget));
            Subscribe(nameof(CanBuild));
            Subscribe(nameof(CanDeployItem));
            Subscribe(nameof(OnPlayerWound));
            Subscribe(nameof(OnItemDropped));
            Subscribe(nameof(OnEntityTakeDamage));
            if (config.eventSettings.prevent_backpack_opening)
            {
                Subscribe(nameof(CanOpenBackpack));
                Subscribe(nameof(STOnPouchOpen));
            }
            SubscribeThirdpartyHooks(true);
            StartSpawnSequence(elevationMod);
        }

        Timer LobbyTimer;
        Timer GameStartTimer;
        Timer OutsiderCheckTimer;

        void DestroyTimer(Timer _timer)
        {
            if (_timer != null && !_timer.Destroyed) _timer.Destroy();
        }

        void ArenaSpawned()
        {
            Puts("Arena has finished spawning.");
            CircleDist = CurrentArena.size * config.radiationZoneSettings.circle_radius_modifier;
            if (config.gameSettings.interference_settings.check_for_outsiders)
            {
                DestroyTimer(OutsiderCheckTimer);
                OutsiderCheckTimer = timer.Every(config.gameSettings.interference_settings.check_time, () =>
                {
                    CheckForOutsiders();
                });
            }
            CanJoin = true;
            DiedCount = 0;
            foreach (var player in BasePlayer.activePlayerList)
            {
                string s = lang.GetMessage("Prefix", this, player.UserIDString) + string.Format(lang.GetMessage("SurvivalArenaStartingBeginIn", this, player.UserIDString), LobbyTime, config.command_settings.join_command);
                if (config.notifications.chatNotifications.SurvivalArenaStartingBeginIn) PrintToChat(player, s);
                SendGUIAnnouncement(player, s);
                if (config.notifications.sendNotify.enabled && config.notifications.sendNotify.sendNotifyEventReminder && Notify != null && Notify.IsLoaded)
                {
                    Notify.Call("SendNotify", player.userID.Get(), config.notifications.sendNotify.notifyType, s);
                }
                if (permission.UserHasPermission(player.UserIDString, perm_autojoin)) JoinEvent(player);
            }

            int timeElapsed = 0;

            if (doors.Count < 4)
            {
                var spawnedDoors = Pool.Get<List<Door>>();
                try
                {
                    foreach (var entity in spawned_entities)
                    {
                        if (entity is Door door) spawnedDoors.Add(door);
                    }
                    foreach (var door in spawnedDoors)
                    {
                        if (!door.IsLocked())
                        {
                            SetupDoor(door);
                        }
                    }
                }
                finally
                {
                    Pool.FreeUnmanaged(ref spawnedDoors);
                }
            }
            else
            {
                foreach (var door in doors)
                {
                    if (!door.IsLocked())
                    {
                        SetupDoor(door);
                    }
                }
            }

            Subscribe(nameof(CanCraft));

            if (LobbyTimer != null && !LobbyTimer.Destroyed) LobbyTimer.Destroy();

            int nextNotify = LobbyTime > 60 ? LobbyTime - 60 : LobbyTime > 30 ? 30 : LobbyTime > 10 ? 10 : 0;

            Interface.CallHook("OnSurvivalArenaStart");
            LobbyTimer = timer.Every(1f, () =>
            {
                // Do lobby time reminders.

                timeElapsed++;
                if (timeElapsed > LobbyTime)
                {
                    CanJoin = false;
                    if (Participants.Count < config.gameSettings.min_player)
                    {
                        foreach (var player in BasePlayer.activePlayerList)
                        {
                            string s = lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("Cancelled", this, player.UserIDString);
                            if (config.notifications.chatNotifications.Cancelled) PrintToChat(player, s);
                            SendGUIAnnouncement(player, s);
                        }
                        EndEvent();
                        return;
                    }

                    MessageParticipants("DoorsOpeningSoon");
                    if (config.soundsSettings.soundLibraryApiSettings.useSoundLibraryAPI && !string.IsNullOrEmpty(config.soundsSettings.soundLibraryApiSettings.customSoundForStart))
                    {
                        foreach (var player in Participants)
                        {
                            ulong rawPlayerID = Convert.ToUInt64(player.Value.userID.ToString());
                            try
                            {
                                object result = SoundLibraryApi.Call("SendSoundToPlayer", rawPlayerID, config.soundsSettings.soundLibraryApiSettings.customSoundForStart);
                            }
                            catch { }
                        }
                    }
                    else if (!string.IsNullOrEmpty(config.soundsSettings.sound_for_starting)) PlaySoundToPlayers(Participants, config.soundsSettings.sound_for_starting, CurrentCentrePoint);
                    if (GameStartTimer != null && !GameStartTimer.Destroyed) GameStartTimer.Destroy();

                    foreach (var p in Participants)
                    {
                        CuiHelper.DestroyUi(p.Value, "STARTCountdownUI");
                    }

                    GameStartTimer = timer.Once(5f, () =>
                    {
                        StartPlay();
                    });

                    if (LobbyTimer != null && !LobbyTimer.Destroyed) LobbyTimer.Destroy();
                    return;
                }
                else
                {
                    var timeLeft = LobbyTime - timeElapsed;
                    if (timeLeft <= nextNotify && timeLeft > 0)
                    {
                        foreach (var player in BasePlayer.activePlayerList)
                        {
                            string s = string.Format(lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("SurvivalArenaStartingBeginIn", this, player.UserIDString), nextNotify, config.command_settings.join_command);
                            if (config.notifications.chatNotifications.SurvivalArenaStartingBeginIn) PrintToChat(player, s);
                            SendGUIAnnouncement(player, s);
                            if (config.notifications.sendNotify.enabled && config.notifications.sendNotify.sendNotifyEventReminder && Notify != null && Notify.IsLoaded)
                            {
                                Notify.Call("SendNotify", player.userID.Get(), config.notifications.sendNotify.notifyType, s);
                            }
                        }

                        nextNotify = timeLeft > 60 ? timeLeft - 60 : timeLeft > 30 ? 30 : timeLeft > 10 ? 10 : 0;
                    }
                    foreach (var p in Participants)
                    {
                        STARTCountdownUI(p.Value, timeLeft);
                    }
                }
            });
        }

        public List<BasePlayer> Intruders = new List<BasePlayer>();

        void CheckForOutsiders()
        {
            Intruders.Clear();
            FindPlayers(Intruders, CurrentCentrePoint, FurthestEntity + DistanceThreshold);
            if (Intruders == null) return;
            foreach (var player in Intruders)
            {
                if (player.HasPlayerFlag(BasePlayer.PlayerFlags.IsAdmin)) continue;
                if (!Participants.ContainsKey(player.userID) && !HasOutsiderMonitor(player) && player.transform.position.y > CurrentCentrePoint.y - 50)
                    AddOutsiderMonitor(player);
            }
        }

        private static void FindPlayers(List<BasePlayer> players, Vector3 a, float n, int m = Layers.Mask.Player_Server)
        {
            int hits = Physics.OverlapSphereNonAlloc(a, n, Vis.colBuffer, m, QueryTriggerInteraction.Collide);
            for (int i = 0; i < hits; i++)
            {
                var entity = Vis.colBuffer[i]?.ToBaseEntity();
                if (entity is BasePlayer) players.Add(entity as BasePlayer);
                Vis.colBuffer[i] = null;
            }
            //Interface.Oxide.LogInfo($"Found {players.Count} players.");
        }

        void PlaySoundToPlayers(Dictionary<ulong, BasePlayer> players, string effect, Vector3 pos)
        {
            foreach (var player in players)
            {
                EffectNetwork.Send(new Effect(effect, pos, pos), player.Value.net.connection);
            }
        }

        void PlaySound(BasePlayer player, string effect)
        {
            EffectNetwork.Send(new Effect(effect, player.transform.position, player.transform.position), player.net.connection);
        }

        void MessageParticipants(string lang_string)
        {
            foreach (var player in Participants)
            {
                PrintToChat(player.Value, lang.GetMessage("Prefix", this, player.Value.UserIDString) + lang.GetMessage(lang_string, this, player.Value.UserIDString));
            }
        }

        Timer CircleTimer;
        static float CircleDist;

        void StartPlay()
        {
            foreach (var player in Participants)
            {
                CircleStatusHUD(player.Value, 0);
                if (config.gameSettings.commands_on_start.Count > 0)
                {
                    // Running commands for each player
                    foreach (var _command in config.gameSettings.commands_on_start)
                    {
                        try
                        {
                            string command_string = _command.Replace("{id}", player.Value.UserIDString);
                            rust.RunServerCommand(command_string);
                        }
                        catch
                        {
                            Puts($"Exception: Failed to run command: {_command} for {player.Value.displayName}");
                        }
                    }
                }
                AddScore(player.Value, false, false, true);
            }
            SendCounterUpdate();
            Subscribe(nameof(CanEntityTakeDamage));
            Subscribe(nameof(WarMode_OverrideOnEntityTakeDamage));
            foreach (var door in doors)
            {
                door.SetOpen(true);
            }

            if (CircleTimer != null && !CircleTimer.Destroyed) CircleTimer.Destroy();

            var count = 0;
            bool triggered = false;
            CircleTimer = timer.Every(1f, () =>
            {
                count++;
                if (count > config.radiationZoneSettings.circle_delay)
                {
                    if (!triggered)
                    {
                        triggered = true;
                        CreateSphere(CurrentCentrePoint, CircleDist, config.radiationZoneSettings.darkness, 0.5f);
                        MessageParticipants("CircleClosingStarted");
                        foreach (var player in Participants)
                        {
                            CircleStatusHUD(player.Value, 1);
                        }
                    }
                    else
                    {
                        var shrinkDist = CircleDist - 0.5f;
                        if ((shrinkDist) > config.radiationZoneSettings.min_ring_size)
                        {
                            CircleDist = shrinkDist;
                        }
                        else
                        {
                            destroyspheres();
                            CreateSphere(CurrentCentrePoint, config.radiationZoneSettings.min_ring_size, config.radiationZoneSettings.darkness, 0);
                            MessageParticipants("CircleClosingFinished");
                            foreach (var player in Participants)
                            {
                                CircleStatusHUD(player.Value, 2);
                            }
                            if (!CircleTimer.Destroyed) CircleTimer.Destroy();
                        }
                    }
                }
            });
        }

        bool IsEnding = false;

        void CheckWin()
        {
            if (CanJoin || IsEnding) return;
            if (Participants.Count > 1)
            {
                SendCounterUpdate();
                return;
            }

            if (Participants.Count == 1)
            {
                var winner = Participants.ElementAt(0).Value;
                AddScore(winner, true, false, false);

                foreach (var player in BasePlayer.activePlayerList)
                {
                    string s = lang.GetMessage("Prefix", this, player.UserIDString) + string.Format(lang.GetMessage("AnnounceWinner", this, player.UserIDString), winner.displayName);
                    if (config.notifications.chatNotifications.AnnounceWinner) PrintToChat(player, s);
                    SendGUIAnnouncement(player, s);
                }
                string prize_str = lang.GetMessage("Prefix", this, winner.UserIDString) + lang.GetMessage("MsgToWinner", this, winner.UserIDString);
                // Handle prize.
                if (!pcdData.pEntity.TryGetValue(winner.userID, out var pi)) pcdData.pEntity.Add(winner.userID, pi = new PCDInfo());
                if (config.prize_settings.rolls_per_claim > 0)
                {
                    pi.rewards_remaining++;
                    if (!config.prize_settings.auto_award)
                    {
                        var s = string.Format(lang.GetMessage("MessageWinner", this, winner.UserIDString), pi.rewards_remaining);
                        prize_str += s;
                        if (config.notifications.sendNotify.enabled && Notify != null && Notify.IsLoaded) Notify.Call("SendNotify", winner.userID.Get(), config.notifications.sendNotify.notifyType, s);
                        SendPrizeNotification(winner, pi);
                    }
                }

                string currency_out;
                if (config.prize_settings.economic_reward.max_amount > 0 && Economics != null && Economics.IsLoaded)
                {
                    GiveEconomicReward(winner, out currency_out);
                    var s = string.Format(lang.GetMessage("EconomicsWon", this, winner.UserIDString), currency_out);
                    prize_str += s;
                    if (config.notifications.sendNotify.enabled && Notify != null && Notify.IsLoaded) Notify.Call("SendNotify", winner.userID.Get(), config.notifications.sendNotify.notifyType, s);
                }
                if (config.prize_settings.srp_reward.max_amount > 0 && ServerRewards != null && ServerRewards.IsLoaded)
                {
                    GiveServerReward(winner, out currency_out);
                    var s = string.Format(lang.GetMessage("ServerRewardsWon", this, winner.UserIDString), currency_out);
                    prize_str += s;
                    if (config.notifications.sendNotify.enabled && Notify != null && Notify.IsLoaded) Notify.Call("SendNotify", winner.userID.Get(), config.notifications.sendNotify.notifyType, s);
                }

                if (config.prize_settings.SkillTree_XP_Reward > 0 && SkillTree != null && SkillTree.IsLoaded)
                {
                    GiveSkilTreeXP(winner, config.prize_settings.SkillTree_XP_Reward);
                    prize_str += string.Format(lang.GetMessage("SkillTreeXPWon", this, winner.UserIDString), config.prize_settings.SkillTree_XP_Reward);
                }

                PrintToChat(winner, prize_str);

                IsEnding = true;

                Interface.CallHook("OnSurvivalArenaWin", winner);

                UpdateScoreBoard();

                timer.Once(3f, () =>
                {
                    EndEvent();
                });

                return;
            }
            else if (Participants.Count == 0 && !CanJoin)
            {
                foreach (var player in BasePlayer.activePlayerList)
                {
                    string s = lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("NobodyWon", this, player.UserIDString);
                    if (config.notifications.chatNotifications.AnnounceWinner) PrintToChat(player, s);
                    SendGUIAnnouncement(player, s);
                }
                IsEnding = true;
                timer.Once(3f, () =>
                {
                    EndEvent();
                });
                return;
            }
        }

        void SendGUIAnnouncement(BasePlayer player, string message)
        {
            if (!config.notifications.GUIAnnouncements.enabled || GUIAnnouncements == null || !GUIAnnouncements.IsLoaded) return;
            GUIAnnouncements.Call("CreateAnnouncement", message, config.notifications.GUIAnnouncements.banner_colour, config.notifications.GUIAnnouncements.text_colour, player, config.notifications.GUIAnnouncements.position_adjustment);
        }

        void GiveEconomicReward(BasePlayer player, out string amount)
        {
            var am = (double)UnityEngine.Random.Range(Math.Max(config.prize_settings.economic_reward.min_amount, 1), Math.Max(config.prize_settings.economic_reward.max_amount, 1) + 1);
            amount = am.ToString();

            if (!Convert.ToBoolean(Economics.Call("Deposit", player.UserIDString, am)))
            {
                Puts($"Failed to give {player.displayName} their economics prize for some reason.");
            }
        }

        void GiveServerReward(BasePlayer player, out string amount)
        {
            var am = UnityEngine.Random.Range(Math.Max(config.prize_settings.srp_reward.min_amount, 1), Math.Max(config.prize_settings.srp_reward.max_amount, 1) + 1);
            amount = am.ToString();

            if (!Convert.ToBoolean(ServerRewards.Call("AddPoints", player.UserIDString, am)))
            {
                Puts($"Failed to give {player.displayName} their server rewards prize for some reason.");
            }
        }

        void GiveSkilTreeXP(BasePlayer player, double amount)
        {
            SkillTree.Call("AwardXP", player, amount, this.Name);
        }

        void LeaveEventCMD(BasePlayer player)
        {
            if (Participants.ContainsKey(player.userID))
            {
                LeaveEvent(player);
            }
        }

        void JoinEvent(BasePlayer player)
        {
            if (!CanJoin)
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("FailedJoin_Closed", this, player.UserIDString));
                return;
            }
            if (Participants.ContainsKey(player.userID)) return;
            if (!EnrollPlayer(player)) return;
            Participants.Add(player.userID, player);
            PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + string.Format(lang.GetMessage("JoinAnnounce", this, player.UserIDString), player.displayName, Participants.Count, config.gameSettings.min_player));
            
            PrintToChat(player, string.Format(lang.GetMessage("JoinedTheEvent", this, player.UserIDString), config.command_settings.leave_command));
            AddMonitor(player);
            EventLeaveButton(player);
            if (config.eventSettings.prevent_skill_tree_buffs && SkillTree != null && SkillTree.IsLoaded) SkillTree.Call("DisableBuffs", player.userID.Get());
        }

        string GetRandomJoinMessage()
        {
            return "SAJointAnnounceMsg_" + UnityEngine.Random.Range(1, 4).ToString();
        }

        void LeaveEvent(BasePlayer player, bool died = false, bool event_ended = false)
        {
            CuiHelper.DestroyUi(player, "BleedingPanel");
            CuiHelper.DestroyUi(player, "PlayerCounter");
            CuiHelper.DestroyUi(player, "CircleStatusHUD");
            CuiHelper.DestroyUi(player, "EventLeaveButton");
            if (Participants.ContainsKey(player.userID))
            {
                if (config.eventSettings.prevent_skill_tree_buffs && SkillTree != null && SkillTree.IsLoaded) SkillTree.Call("EnableBuffs", player.userID.Get());
                if (NoEscape != null && NoEscape.IsLoaded) NoEscape.Call("StopCombatBlocking", player);
                Interface.CallHook("OnEventLeave", player, this.Name);

                var playerData = GetPlayerData(player.userID, false); //DrSmoker - Added this line
                if (NightVision != null && NightVision.IsLoaded && config.gameSettings.use_nightvision)
                {
                    NightVision.Call("UnlockPlayerTime", player);
                }
                RevokeGrantedNightVision(player, playerData);

                //DrSmoker - Removed this line - RemoveItems(player);
                //player.inventory.Strip();

                DestroyMonitor(player);
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("LeftCompetition", this, player.UserIDString));

                Participants.Remove(player.userID);
                CuiHelper.DestroyUi(player, "STARTCountdownUI");

                if (!died)
                {
                    //player.inventory.Strip();
                    Puts($"Stripping {player.displayName} from LeaveEvent.");
                    player.inventory.Strip();
                    //DrSmoker - Moved this up above - var playerData = GetPlayerData(player.userID, false);
                    HandleStatsRestore(player, playerData);
                }
                else if (playerData != null) playerData.StripOnItemRestore = true;

                if (config.gameSettings.commands_on_leave.Count > 0)
                {
                    // Running commands for each player
                    foreach (var _command in config.gameSettings.commands_on_leave)
                    {
                        try
                        {
                            string command_string = _command.Replace("{id}", player.UserIDString);
                            rust.RunServerCommand(command_string);
                        }
                        catch
                        {
                            Puts($"Exception: Failed to run command: {_command} for {player.displayName}");
                        }
                    }
                }

                if (!event_ended)
                {
                    CheckWin();
                }
            }
            else
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("NotAtEvent", this, player.UserIDString));
                if (event_ended) DestroyMonitor(player);
            }
        }


        void EndEvent(bool unloaded = false)
        {
            Interface.CallHook("OnSurvivalArenaEnd");
            DiedCount = 0;
            string Error = string.Empty;
            try
            {
                Intruders.Clear();
                DestroyTimer(OutsiderCheckTimer);
                Error = "Location 1";
                LootProfile = null;
                CanJoin = false;
                IsRunning = false;
                LobbyTime = config.gameSettings.defaultStartTime;
                ManualStart = false;
                DevMode = false;

                Error = "Location 2";
                if (LobbyTimer != null && !LobbyTimer.Destroyed) LobbyTimer.Destroy();
                if (GameStartTimer != null && !GameStartTimer.Destroyed) GameStartTimer.Destroy();
                if (CircleTimer != null && !CircleTimer.Destroyed) CircleTimer.Destroy();
                if (CurrentArena != null) CircleDist = CurrentArena.size;
                destroyspheres();

                Error = "Location 3";
                List<KeyValuePair<ulong, BasePlayer>> _participants = Pool.Get<List<KeyValuePair<ulong, BasePlayer>>>();
                try
                {
                    _participants.AddRange(Participants);
                    foreach (var player in _participants)
                    {
                        //player.inventory.Strip();
                        if (unloaded)
                        {
                            if (config.eventSettings.prevent_skill_tree_buffs && SkillTree != null && SkillTree.IsLoaded) SkillTree.Call("EnableBuffs", player.Key);
                            player.Value.inventory.Strip();
                            player.Value.Die();
                        }
                        else LeaveEvent(player.Value, false, true);
                    }

                }
                catch { }
                Error = "Location 4";
                Pool.FreeUnmanaged(ref _participants);

                try
                {
                    // Removes corpses after event.
                    foreach (var corpse in PlayerCorpses)
                    {
                        if (corpse.inventory?.itemList != null)
                        {
                            try
                            {
                                foreach (var item in corpse.inventory.itemList)
                                    item.Remove();
                            }
                            catch { }
                        }

                        try
                        {
                            corpse.Kill();
                        }
                        catch { }
                    }
                }
                catch { }
                Error = "Location 5";

                if (CurrentArena != null)
                {
                    var droppedContainers = FindEntitiesOfType<DroppedItemContainer>(CurrentCentrePoint, CurrentArena.size);
                    try
                    {

                        foreach (var container in droppedContainers)
                        {
                            container.Kill();
                        }

                    }
                    catch { }
                    Pool.FreeUnmanaged(ref droppedContainers);
                }
                Error = "Location 6";

                //try
                //{
                //    foreach (var item in EventItems)
                //    {
                //        if (item != null) item.Remove();
                //    }
                //}
                //catch { }
                Error = "Location 7";

                try
                {
                    foreach (var p in BasePlayer.allPlayerList)
                        DestroyOutsiderMonitor(p);
                }
                catch { }
                Error += ". EmEndEvent successful.";
                if (!unloaded)
                {
                    Error += $". unloaded: {unloaded}. Spawn_routine null: {Spawn_routine == null}.";
                    if (Spawn_routine != null) ServerMgr.Instance.StopCoroutine(Spawn_routine);
                    Error += $". Stopped Spawn_routine.";
                    Spawn_routine = null;
                    Error += $". nulled Spawn_routine. Trying to run Despawn_routine.";
                    Despawn_routine = ServerMgr.Instance.StartCoroutine(DespawnEntities());
                    Error += $". Ran Spawn_routine.";
                }
                Error = "Location 9";

                UnsubHooks();

                //EventItems.Clear();
                Participants.Clear();
                ClearTeamCache();
                ContainerRespawnTimers.Clear();
                doors.Clear();
                Error = "Location 10";

                EventElevationMod = 0f;

                CurrentArena = null;
                IsEnding = false;

                Error = "Location 11";
            }
            catch (Exception ex)
            {
                Puts($"Found an error: {Error}. Exception: {ex.Message}");
                LogToFile(this.Name + "_errors", Error, this, true, true);
            }
        }

        #endregion

        #region Hooks

        List<Item> AllItems(BasePlayer player)
        {
            List<Item> result = Pool.Get<List<Item>>();
            player.inventory.GetAllItems(result);

            return result;
        }

        void OnNewSave(string filename)
        {
            pcdData.pEntity.Clear();
            SaveData();
            spawnData.spawnedEntities.Clear();
            SaveData(SaveType.Spawn);
        }

        void OnServerSave()
        {
            SaveData();
        }


        void UnsubHooks()
        {
            Unsubscribe(nameof(CanOpenBackpack));
            Unsubscribe(nameof(STOnPouchOpen));
            Unsubscribe(nameof(OnLootSpawn));
            Unsubscribe(nameof(CanDropActiveItem));
            Unsubscribe(nameof(OnItemDropped));
            Unsubscribe(nameof(OnPlayerDeath));
            Unsubscribe(nameof(CanLootEntity));
            Unsubscribe(nameof(CanLootPlayer));
            Unsubscribe(nameof(OnEntityKill));
            Unsubscribe(nameof(CanHelicopterTarget));
            Unsubscribe(nameof(CanHelicopterStrafeTarget));
            Unsubscribe(nameof(CanBuild));
            Unsubscribe(nameof(CanDeployItem));
            Unsubscribe(nameof(OnPlayerWound));
            Unsubscribe(nameof(CanEntityTakeDamage));
            Unsubscribe(nameof(WarMode_OverrideOnEntityTakeDamage));
            Unsubscribe(nameof(CanCraft));
            Unsubscribe(nameof(OnEntityTakeDamage));

            SubscribeThirdpartyHooks(false);

            if (CorpseMonitor.Count == 0 && PlayerCorpses.Count == 0)
            {
                Unsubscribe(nameof(OnPlayerCorpseSpawn));
            }
        }

        object CanCraft(ItemCrafter crafter, ItemBlueprint bp, int amount, bool free)
        {
            if (crafter?.owner == null) return null;
            if (Participants.ContainsKey(crafter.owner.userID)) return false;
            return null;
        }

        bool ShouldBlockUpgradeInPlace(BasePlayer player, BaseEntity replaced)
        {
            if (replaced != null && spawned_entities.Contains(replaced)) return true;
            if (player != null && Participants.ContainsKey(player.userID)) return true;
            if (player != null && InArenaBuildZone(player.transform.position)) return true;
            return replaced != null && InArenaBuildZone(replaced.transform.position);
        }

        bool InArenaBuildZone(Vector3 pos)
        {
            return CurrentArena != null && pos.y > CurrentCentrePoint.y - 50 && Vector3.Distance(pos, CurrentCentrePoint) < FurthestEntity;
        }

        object CanBuild(Planner planner, Construction prefab, Construction.Target target)
        {
            var player = planner?.GetOwnerPlayer();
            if (player == null) return null;
            if (Participants.ContainsKey(player.userID) || (player.transform.position.y > CurrentCentrePoint.y - 50 && Vector3.Distance(player.transform.position, CurrentCentrePoint) < FurthestEntity)) return false;
            return null;
        }

        object CanDeployItem(BasePlayer player, Deployer deployer, NetworkableId entityId)
        {
            if (player != null && Participants.ContainsKey(player.userID)) return false;
            return null;
        }

        object CanHelicopterTarget(PatrolHelicopterAI heli, BasePlayer player)
        {
            if (Participants.ContainsKey(player.userID)) return false;
            return null;
        }

        object CanHelicopterStrafeTarget(PatrolHelicopterAI entity, BasePlayer player)
        {
            if (Participants.ContainsKey(player.userID)) return false;
            return null;
        }

        void OnLootSpawn(LootContainer container)
        {
            if (container == null || !containerSpawns.Contains(container))
            {
                return;
            }

            timer.Once(0.1f, () =>
            {
                if (container == null || container.IsDestroyed || LootProfile == null || !config.lootSettings.TryGetValue(LootProfile, out var lootSettings)) return;
                container.inventory.Clear();
                ItemManager.DoRemoves();

                for (int i = 0; i < UnityEngine.Random.Range(lootSettings.min_items, lootSettings.max_items + 1); i++)
                {
                    var randProfile = RollLoot(lootSettings);
                    var item = ItemManager.CreateByName(randProfile.shortname, UnityEngine.Random.Range(randProfile.min_amount, randProfile.max_amount + 1), randProfile.skin);

                    if (item == null)
                    {
                        Puts($"Item: {randProfile.shortname} was invalid.");
                        continue;
                    }

                    if (randProfile.displayName != null)
                        item.name = randProfile.displayName;

                    //EventItems.Add(item);

                    if (!item.MoveToContainer(container.inventory))
                        item.Remove();
                }
            });
        }

        LootInfo RollLoot(LootSettings lootSettings)
        {
            var totalWeight = lootSettings.items.Sum(x => x.dropWeight);
            var roll = UnityEngine.Random.Range(1, totalWeight + 1);

            var count = 0;
            foreach (var profile in lootSettings.items)
            {
                count += profile.dropWeight;
                if (roll <= count) return profile;
            }
            Puts($"Failed to roll loot. Selecting it randomly.");
            return lootSettings.items.GetRandom();
        }

        //List<Item> EventItems = new List<Item>();

        object CanDropActiveItem(BasePlayer player)
        {
            if (Participants.ContainsKey(player.userID)) return false;
            return null;
        }

        void OnItemDropped(Item item, BaseEntity entity)
        {
            if (item == null) return;
            //if (EventItems.Contains(item)) NextTick(() =>
            //{
            //    if (item != null) EventItems.Remove(item);
            //    entity?.KillMessage();
            //});
            //else 
            if (entity != null && CurrentArena != null && CurrentCentrePoint.y - 50 < entity.transform.position.y && InRange(CurrentCentrePoint, entity.transform.position, FurthestEntity + DistanceThreshold)) NextTick(() => entity?.KillMessage());
        }

        [PluginReference]
        private Plugin NightVision, Economics, ServerRewards, Notify, GUIAnnouncements, SkillTree, NoEscape, EventHelper, MultiEvents, SoundLibraryApi, NotificationSystem;

        private static SurvivalArena Instance { get; set; }

        void OnServerInitialized(bool initial)
        {
            if (!config.eventSettings.prevent_xp_loss)
            {
                Unsubscribe(nameof(CanBePenalized));
                Unsubscribe(nameof(STOnLoseXP));
            }

            if (!config.eventSettings.prevent_bag_loss)
            {
                Unsubscribe(nameof(CanDropBackpack));
                Unsubscribe(nameof(STOnPouchDrop));
            }

            if (!config.eventSettings.prevent_double_jump_plugin) Unsubscribe(nameof(CanDoubleJump));

            Instance = this;

            foreach (var file in Interface.Oxide.DataFileSystem.GetFiles(subDirectory))
            {
                if (file.Contains("player_data") || file.Contains("spawn_data")) continue;
                var name = file.Split('/')?.Last();
                name = name.Replace(".json", "");
                if (ARENASDATA.ContainsKey(name)) continue;

                var dataFile = Interface.Oxide.DataFileSystem.GetFile(subDirectory + name);
                if (dataFile == null) continue;

                try
                {
                    var arenaData = Interface.Oxide.DataFileSystem.ReadObject<ArenaData>(subDirectory + name);
                    if (arenaData == null) continue;

                    bool fixedScale = false;
                    foreach (var entity in arenaData.entities)
                    {
                        if (entity.scale == Vector3.zero)
                        {
                            entity.scale = Vector3.one;
                            fixedScale = true;
                        }
                    }
                    if (fixedScale)
                    {
                        Interface.Oxide.DataFileSystem.WriteObject(subDirectory + name, arenaData);
                        Puts($"Fixed zero-scale entities in {name} and overwrote file.");
                    }

                    ARENASDATA.Add(name, dataFile);
                    Arenas.Add(name, arenaData);
                    HaveArenaFile = true;
                    Puts($"Found and added arena: {name}");
                    SaveData(SaveType.Arena);
                }
                catch { }
            }

            List<KeyValuePair<string, ArenaData>> arenas = Pool.Get<List<KeyValuePair<string, ArenaData>>>();
            arenas.AddRange(Arenas);
            List<string> toDelete = Pool.Get<List<string>>();
            foreach (var arena in arenas)
            {
                if (arena.Value.CenterPoint == Vector3.zero)
                {
                    Puts($"Failed to load {arena.Key} due to an invalid Centre Point.");
                    Arenas.Remove(arena.Key);
                }
                if (HasOldPrefabs(arena.Value.entities))
                {
                    Puts($"Failed to load {arena.Key} due to invalid prefabs detected.");
                    toDelete.Add(arena.Key);
                    Arenas.Remove(arena.Key);
                }
            }
            Pool.FreeUnmanaged(ref arenas);

            foreach (var arena in toDelete)
            {
                if (!Interface.Oxide.DataFileSystem.ExistsDatafile(subDirectory + arena)) continue;
                Interface.Oxide.DataFileSystem.DeleteDataFile(subDirectory + arena);
            }
            Pool.FreeUnmanaged(ref toDelete);

            if (Arenas.Count == 0)
            {
                if (!RequestMade) AddArenaFile();
                return;
            }

            RadAccumulation = config.radiationZoneSettings.rad_increase_per_tick;
            CheckInterval = config.radiationZoneSettings.check_interval;
            bool DoSave = false;
            if (config.lootSettings == null || config.lootSettings.Count == 0)
            {
                config.lootSettings = DefaultLootSettings;
                DoSave = true;
            }
            else
            {
                foreach (var prof in config.lootSettings)
                {
                    if (prof.Value.profileWeight == 0)
                    {
                        prof.Value.profileWeight = 100;
                        DoSave = true;
                    }
                    var totalWeight = prof.Value.items.Sum(x => x.dropWeight);
                    if (totalWeight == 0)
                    {
                        foreach (var loot in prof.Value.items)
                        {
                            loot.dropWeight = 100;
                        }
                        DoSave = true;
                    }
                }
            }

            if (config.prize_settings.prizes.Count == 0)
            {
                config.prize_settings.prizes = DefaultPrizes;
                DoSave = true;
            }

            if (config.prize_settings.participation_rewards.participation_prizes.Count == 0)
            {
                config.prize_settings.participation_rewards.participation_prizes = DefaultParticipationPrizes;
                DoSave = true;
            }

            if (config.prize_settings.participation_rewards.modifiers.Count == 0)
            {
                config.prize_settings.participation_rewards.modifiers = DefaultParticipationModifiers;
                DoSave = true;
            }

            var biome = DefaultBiomeTrees;
            foreach (var type in biome)
            {
                if (config.biome_trees.TryGetValue(type.Key, out var prefabs) && prefabs.Count > 0) continue;
                Puts($"Adding trees for {type.Key} biome.");
                var entities = new List<string>();
                entities.AddRange(type.Value);
                config.biome_trees[type.Key] = entities;
                DoSave = true;
            }
            biome.Clear();
            biome = DefaultBiomeBushes;
            foreach (var type in biome)
            {
                if (config.biome_bushes.TryGetValue(type.Key, out var prefabs) && prefabs.Count > 0) continue;
                Puts($"Adding bushes for {type.Key} biome.");
                var entities = new List<string>();
                entities.AddRange(type.Value);
                config.biome_bushes[type.Key] = entities;
                DoSave = true;
            }
            biome.Clear();
            biome = DefaultBiomeLogs;
            foreach (var type in biome)
            {
                if (config.biome_logs.TryGetValue(type.Key, out var prefabs) && prefabs.Count > 0) continue;
                Puts($"Adding logs for {type.Key} biome.");
                var entities = new List<string>();
                entities.AddRange(type.Value);
                config.biome_logs[type.Key] = entities;
                DoSave = true;
            }
            biome.Clear();

            if (DoSave) SaveConfig();

            if (config.gameSettings.clear_arena_on_server_start && (string.IsNullOrEmpty(ConVar.Server.levelurl) || !ConVar.Server.levelurl.Contains("SurvivalArena")))
            {
                WipeOldArena();
            }
            LobbyTime = config.gameSettings.defaultStartTime;

            SecondsToVacate = config.gameSettings.interference_settings.seconds_to_vacate;
            DistanceThreshold = config.gameSettings.interference_settings.dist_from_edge_restriction;

            SubscribeThirdpartyHooks(false);

            if (config.eventSettings.allow_teams) Unsubscribe(nameof(OnTeamCreate));
            if (!config.eventSettings.use_event_helper_timer) Unsubscribe(nameof(OnPluginLoaded));
            if (config.eventSettings.use_event_helper_timer && EventHelper != null && EventHelper.IsLoaded) EventHelper.Call("EMCreateEvent", this.Name, true, false, false, false, false, false, Vector3.zero);
            for (int i = 1; i < 4; i++)
            {
                var message = lang.GetMessage($"SAJointAnnounceMsg_{i}", this);
                if (message != null && message != $"SAJointAnnounceMsg_{i}") ValidJoinMessages.Add($"SAJointAnnounceMsg_{i}");
            }

            UpdateScoreBoard();

            if (!config.eventSettings.use_event_helper_timer && config.eventSettings.non_eventhelper_timer > 0)
                timer.Every(config.eventSettings.non_eventhelper_timer, () =>
                {
                    if (IsRunning || CanJoin || DevMode || IsDespawning) return;
                    StartEvent(GetRandomArena());
                });

            RegisterMultiEvent();
            SetupDeathFeed();

            SpherePrefab = GetSphereColour();
        }

        string GetSphereColour()
        {
            switch (config.radiationZoneSettings.sphereColour)
            {
                default: return "assets/prefabs/visualization/sphere.prefab";
                case SphereColour.Green: return "assets/bundled/prefabs/modding/events/twitch/br_sphere_green.prefab";
                case SphereColour.Red: return "assets/bundled/prefabs/modding/events/twitch/br_sphere_red.prefab";
                case SphereColour.Purple: return "assets/bundled/prefabs/modding/events/twitch/br_sphere_purple.prefab";
            }
        }

        bool HasOldPrefabs(List<EntityInfo> entities)
        {
            foreach (var entity in entities)
                if (entity.prefab.Contains("admin_cliff_low_arc.prefab") || entity.prefab.Contains("admin_cliff_medium_arc_arid_small")) return true;

            return false;
        }

        object OnPlayerCommand(BasePlayer player, string command, string[] args)
        {
            if (!Participants.ContainsKey(player.userID)) return null;
            foreach (var _cmd in config.eventSettings.prevent_commands)
            {
                if (!_cmd.Equals(command, StringComparison.OrdinalIgnoreCase)) continue;
                PrintToChat(player, string.Format(lang.GetMessage("NoCommand", this, player.UserIDString), command));
                return true;
            }
            return null;
        }

        object OnServerCommand(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || arg.cmd.FullName == "chat.say") return null;
            if (!Participants.ContainsKey(player.userID)) return null;

            foreach (var _cmd in config.eventSettings.prevent_commands)
            {
                if (!_cmd.Equals(arg.cmd.Name, StringComparison.OrdinalIgnoreCase) && !_cmd.Equals(arg.cmd.FullName, StringComparison.OrdinalIgnoreCase)) continue;
                PrintToChat(player, string.Format(lang.GetMessage("NoCommand", this, player.UserIDString), _cmd));
                return true;
            }
            return null;
        }

        void WipeOldArena(BasePlayer player = null)
        {
            if (IsRunning || Spawn_routine != null || Despawn_routine != null)
            {
                if (player != null) PrintToChat(player, "Cannot wipe old arena data while a game is running, or if when the area is spawning or despawning.");
                else Puts("Cannot wipe old arena data while a game is running, or if when the area is spawning or despawning.");
                return;
            }
            if (WipingOldData)
            {
                if (player != null) PrintToChat(player, "Still wiping old data.");
                else Puts("Still wiping old data.");
                return;
            }

            if (spawnData.spawnedEntities.Count == 0)
            {
                if (player != null) PrintToChat(player, "There are no entities to wipe");
                else Puts("There are no entities to wipe");
                return;
            }

            var spawnedIDs = Pool.Get<HashSet<ulong>>();
            try
            {
                spawnedIDs.UnionWith(spawnData.spawnedEntities);
                foreach (var entity in BaseNetworkable.serverEntities)
                {
                    if (spawnedIDs.Contains(entity.net.ID.Value)) OldEntitiesWipeList.Add(entity);
                }
            }
            finally
            {
                Pool.FreeUnmanaged(ref spawnedIDs);
            }

            if (player != null) PrintToChat(player, $"Found {OldEntitiesWipeList.Count} entities to wipe");
            else Puts($"Found {OldEntitiesWipeList.Count} entities to wipe");
            if (OldEntitiesWipeList.Count > 0) ServerMgr.Instance.StartCoroutine(DoArenaWipe());
            else spawnData.spawnedEntities.Clear();
        }

        [ChatCommand("forcewipeoldarena")]
        void ForceWipeOldArenaChat(BasePlayer player)
        {
            if (!IsAdmin(player))
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("NoPerms", this, player.UserIDString));
                return;
            }

            ForceWipeArena(player);
        }

        void ForceWipeArena(BasePlayer player = null)
        {
            if (IsRunning || Spawn_routine != null || Despawn_routine != null)
            {
                if (player != null) PrintToChat(player, "Cannot wipe old arena data while a game is running, or if when the area is spawning or despawning.");
                else Puts("Cannot wipe old arena data while a game is running, or if when the area is spawning or despawning.");
                return;
            }
            if (WipingOldData)
            {
                if (player != null) PrintToChat(player, "Still wiping old data.");
                else Puts("Still wiping old data.");
                return;
            }
            List<BaseNetworkable> entities = Pool.Get<List<BaseNetworkable>>();
            entities.AddRange(BaseNetworkable.serverEntities);

            if (entities.Count == 0)
            {
                if (player != null) PrintToChat(player, "Could not find any entities to wipe.");
                else Puts("Could not find any entities to wipe.");
                Pool.FreeUnmanaged(ref entities);
                return;
            }

            foreach (var arenaData in Arenas)
            {
                float heightTarget = arenaData.Value.CenterPoint.y - 40;
                foreach (var entity in entities)
                {
                    if (entity.transform.position.y > heightTarget && Vector3.Distance(entity.transform.position, arenaData.Value.CenterPoint) <= arenaData.Value.size && !(entity is BaseVehicle) && !(entity is BasePlayer))
                    {
                        OldEntitiesWipeList.Add(entity);
                    }
                }
            }

            Pool.FreeUnmanaged(ref entities);
            if (OldEntitiesWipeList.Count > 0) ServerMgr.Instance.StartCoroutine(DoArenaWipe());
        }

        [ConsoleCommand("forcewipevector")]
        void ForceWipeArenaVector3Console(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null && !IsAdmin(player)) return;
            ForceWipeOldArenaUsingVector3(player);
        }

        [ChatCommand("forcewipevector")]
        void ForceWipeArenaVector3Chat(BasePlayer player)
        {
            if (!IsAdmin(player))
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("NoPerms", this, player.UserIDString));
                return;
            }
            ForceWipeOldArenaUsingVector3(player);
        }

        void ForceWipeOldArenaUsingVector3(BasePlayer player)
        {
            if (IsRunning || Spawn_routine != null || Despawn_routine != null)
            {
                if (player != null) PrintToChat(player, "Cannot wipe old arena data while a game is running, or if when the area is spawning or despawning.");
                else Puts("Cannot wipe old arena data while a game is running, or if when the area is spawning or despawning.");
                return;
            }
            if (WipingOldData)
            {
                if (player != null) PrintToChat(player, "Still wiping old data.");
                else Puts("Still wiping old data.");
                return;
            }

            List<BaseEntity> entitiesToDestroy = Pool.Get<List<BaseEntity>>();
            foreach (var arena in Arenas)
            {
                var centrePos = arena.Value.CenterPoint;
                List<BaseEntity> entities = FindEntitiesOfType<BaseEntity>(centrePos, arena.Value.size);
                foreach (var entity in entities)
                {
                    if (entity is BasePlayer || entity is BaseVehicle) continue;
                    if (entity.transform.position.y < centrePos.y - 50) continue;
                    // Entity is above the arena threshold.
                    entitiesToDestroy.Add(entity);
                }
                Pool.FreeUnmanaged(ref entities);
            }

            foreach (var entity in entitiesToDestroy)
            {
                try
                {
                    if (entity.IsFullySpawned()) entity.Kill();
                }
                catch
                {
                    Puts($"Failed to delete entity at pos: {entity?.transform.position} [Type: {entity?.GetType()}]");
                }
            }

            Pool.FreeUnmanaged(ref entitiesToDestroy);
        }

        private static List<T> FindEntitiesOfType<T>(Vector3 a, float n, int m = -1) where T : BaseEntity
        {
            int hits = Physics.OverlapSphereNonAlloc(a, n, Vis.colBuffer, m, QueryTriggerInteraction.Collide);
            List<T> entities = Pool.Get<List<T>>();
            for (int i = 0; i < hits; i++)
            {
                var entity = Vis.colBuffer[i]?.ToBaseEntity() as T;
                if (entity != null && !entities.Contains(entity)) entities.Add(entity);
                Vis.colBuffer[i] = null;
            }
            return entities;
        }
        private static bool InRange(Vector3 a, Vector3 b, float distance)
        {
            return (a - b).sqrMagnitude <= distance * distance;
        }

        //void WipeOldArena(BasePlayer player = null)
        //{
        //    Puts($"IsRunning: {IsRunning}. Spawn_routine: {Spawn_routine != null}. Despawn_routine: {Despawn_routine != null}");
        //    if (IsRunning || Spawn_routine != null || Despawn_routine != null)
        //    {
        //        if (player != null) PrintToChat(player, "Cannot wipe old arena data while a game is running, or if when the area is spawning or despawning.");
        //        else Puts("Cannot wipe old arena data while a game is running, or if when the area is spawning or despawning.");
        //        return;
        //    }
        //    if (WipingOldData)
        //    {
        //        if (player != null) PrintToChat(player, "Still wiping old data.");
        //        else Puts("Still wiping old data.");
        //        return;
        //    }
        //    List<BaseNetworkable> entities = Pool.Get<List<BaseNetworkable>();
        //    entities.AddRange(BaseNetworkable.serverEntities);

        //    if (entities.Count == 0)
        //    {
        //        if (player != null) PrintToChat(player, "Could not find any entities to wipe.");
        //        else Puts("Could not find any entities to wipe.");
        //        Pool.FreeUnmanaged(ref entities);
        //        return;
        //    }

        //    foreach (var arenaData in Arenas)
        //    {
        //        float heightTarget = arenaData.Value.CenterPoint.y - 40;
        //        foreach (var entity in entities)
        //        {
        //            if (entity.transform.position.y > heightTarget && Vector3.Distance(entity.transform.position, arenaData.Value.CenterPoint) <= CurrentArena.size && !(entity is BaseVehicle) && !(entity is BasePlayer))
        //            {
        //                OldEntitiesWipeList.Add(entity);
        //            }
        //        }
        //    }

        //    Pool.FreeUnmanaged(ref entities);
        //    if (OldEntitiesWipeList.Count > 0) ServerMgr.Instance.StartCoroutine(DoArenaWipe());            
        //}

        public bool WipingOldData = false;
        public IEnumerator DoArenaWipe()
        {
            WipingOldData = true;
            int count = 0;

            foreach (var entity in OldEntitiesWipeList)
            {
                try
                {
                    if (entity != null)
                    {
                        spawnData.spawnedEntities.Remove(entity.net.ID.Value);
                        count++;
                        entity.KillMessage();
                    }
                }
                catch { }
                if (count >= config.max_procs_per_tick)
                {
                    count = 0;
                    yield return CoroutineEx.waitForEndOfFrame;
                }
            }
            WipingOldData = false;
            OldEntitiesWipeList.Clear();
        }

        [ChatCommand("wipeoldarena")]
        void WipeOldArenaChat(BasePlayer player)
        {
            if (!IsAdmin(player))
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("NoPerms", this, player.UserIDString));
                return;
            }

            WipeOldArena(player);
        }

        [ConsoleCommand("wipeoldarena")]
        void WipeOldArenaConsole(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null)
            {
                WipeOldArenaChat(player);
                return;
            }
            else WipeOldArena();
        }

        List<BaseNetworkable> OldEntitiesWipeList = new List<BaseNetworkable>();

        List<DroppedItemContainer> PlayerCorpses = new List<DroppedItemContainer>();
        List<ulong> CorpseMonitor = new List<ulong>();
        List<ulong> WasEventPlayerList = Pool.Get<List<ulong>>();

        void HandleParticipationPrize(BasePlayer player)
        {
            float prizeMod;
            if (!config.prize_settings.participation_rewards.modifiers.TryGetValue(DiedCount, out prizeMod))
            {
                var highest = 0;
                for (int i = 0; i < config.prize_settings.participation_rewards.modifiers.Count; i++)
                {
                    var amount = config.prize_settings.participation_rewards.modifiers.ElementAt(i).Key;
                    if (amount < DiedCount && amount > highest) highest = amount;
                }
                if (highest == 0)
                {
                    return;
                }
                prizeMod = config.prize_settings.participation_rewards.modifiers[highest];
            }
            PCDInfo playerData = GetPlayerData(player.userID, true);
            playerData.participation_rewards_modifier = prizeMod;
        }

        void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (Participants.ContainsKey(player.userID))
            {
                HandleWasList(player.userID);
                DestroyOutsiderMonitor(player);
                if (info != null && !info.damageTypes.Has(DamageType.Suicide))
                {
                    DiedCount++;
                    if (config.prize_settings.participation_rewards.enabled) HandleParticipationPrize(player);
                    if (!string.IsNullOrWhiteSpace(config.soundsSettings.sound_for_killing) && info.InitiatorPlayer != null && Participants.ContainsKey(info.InitiatorPlayer.userID)) PlaySound(info.InitiatorPlayer, config.soundsSettings.sound_for_killing);
                }

                var container = MovePlayerLootToDropContainer(player);
                if (container != null) PlayerCorpses.Add(container);
                CorpseMonitor.Add(player.userID);

                LeaveEvent(player, true);
                PlaySoundToPlayers(Participants, config.soundsSettings.sound_for_death, new Vector3(CurrentCentrePoint.x, CurrentCentrePoint.y - 10, CurrentCentrePoint.z));
                if (info?.InitiatorPlayer != null && info.InitiatorPlayer.userID.IsSteamId()) AddScore(info.InitiatorPlayer, false, true, false);
                if (config.gameSettings.message_on_death)
                    foreach (var p in Participants)
                    {
                        if (info?.InitiatorPlayer != null) PrintToChat(p.Value, string.Format(lang.GetMessage($"KillMessage{UnityEngine.Random.Range(1, 6)}", this, p.Value.UserIDString), player.displayName, info.InitiatorPlayer.displayName));
                        else PrintToChat(p.Value, string.Format(lang.GetMessage($"DeathMessage{UnityEngine.Random.Range(1, 4)}", this, p.Value.UserIDString), player.displayName));
                    }
            }
            else DestroyOutsiderMonitor(player);

        }

        void AddScore(BasePlayer player, bool win, bool kill, bool game)
        {
            if (!pcdData.scoreData.TryGetValue(player.userID, out var playerData)) pcdData.scoreData.Add(player.userID, playerData = new ScoreInfo(player.displayName));
            if (string.IsNullOrEmpty(playerData.name)) playerData.name = player.displayName;
            if (win) playerData.wins++;
            if (kill) playerData.kills++;
            if (game) playerData.games++;
        }

        void HandleWasList(ulong id)
        {
            if (!WasEventPlayerList.Contains(id))
            {
                WasEventPlayerList.Add(id);
                timer.Once(1f, () => WasEventPlayerList?.Remove(id));
            }
        }

        DroppedItemContainer MovePlayerLootToDropContainer(BasePlayer player)
        {
            if (player.inventory == null) return null;
            var itemList = AllItems(player);
            try
            {
                if (itemList.Count == 0) return null;
                DroppedItemContainer container = GameManager.server.CreateEntity("assets/prefabs/misc/item drop/item_drop_backpack.prefab", player.transform.position, Quaternion.identity) as DroppedItemContainer;

                container.lootPanelName = "generic_resizable";
                container.playerName = $"{player.displayName}'s Backpack";
                container.playerSteamID = player.userID;

                container.inventory = new ItemContainer();
                container.inventory.ServerInitialize(null, itemList.Count);
                container.inventory.GiveUID();
                container.inventory.entityOwner = container;
                container.inventory.SetFlag(ItemContainer.Flag.NoItemInput, true);

                foreach (Item item in itemList)
                {
                    if (!item.MoveToContainer(container.inventory))
                    {
                        item.RemoveFromContainer();
                        item.Remove();
                    }
                }

                container.Spawn();

                return container;
            }
            finally
            {
                Pool.FreeUnmanaged(ref itemList);
            }
        }

        void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (Participants.ContainsKey(player.userID))
            {
                player.inventory.Strip();
                player.Die();
            }
        }

        void OnPlayerRespawned(BasePlayer player)
        {
            if (!pcdData.pEntity.TryGetValue(player.userID, out var playerData)) return;
            if (!Participants.ContainsKey(player.userID)) RevokeGrantedNightVision(player, playerData);
            if (!config.eventSettings.give_items_back_on_death) return;
            HandleStatsRestore(player, playerData);
            //playerData.StripOnItemRestore = true;
        }

        void OnPlayerSleepEnded(BasePlayer player)
        {
            if (Participants.ContainsKey(player.userID))
            {
                return;
            }
            if (!pcdData.pEntity.TryGetValue(player.userID, out var playerData)) return;

            if (config.eventSettings.give_items_back_on_death)
            {
                timer.Once(1f, () => HandleItemRestore(player, playerData, true));
            }
            else if (playerData.Items.Count > 1)
            {
                var s = String.Format(lang.GetMessage("RestoreItemsReminder", this, player.UserIDString), config.eventSettings.restore_items_command);
                PrintToChat(player, s);
                if (config.notifications.sendNotify.enabled && config.notifications.sendNotify.sendNotifyRecoverReminder && Notify != null && Notify.IsLoaded)
                    Notify.Call("SendNotify", player.userID.Get(), config.notifications.sendNotify.notifyType, s);
            }

            if (config.prize_settings.participation_rewards.enabled)
            {
                if (playerData.participation_rewards_modifier > 0)
                {
                    timer.Once(3f, () => DoParticipationPrizes(player, playerData));
                }
            }
            if (config.prize_settings.auto_award && playerData.rewards_remaining > 0)
            {
                timer.Once(3f, () => HandleAutoPrize(player, playerData));
            }
        }

        void HandleAutoPrize(BasePlayer player, PCDInfo playerData)
        {
            if (playerData.rewards_remaining <= 0)
            {
                if (playerData.rewards_remaining < 0) playerData.rewards_remaining = 0;
                return;
            }
            HandleRewards(player, playerData);
        }

        void DoParticipationPrizes(BasePlayer player, PCDInfo playerData)
        {            
            if (player == null || playerData == null || player.IsDead()) return;
            RollParticipationReward(player, playerData.participation_rewards_modifier);
            playerData.participation_rewards_modifier = 0;
        }

        void RollParticipationReward(BasePlayer player, float mod)
        {
            var roll = UnityEngine.Random.Range(0, config.prize_settings.participation_rewards.participation_prizes.Sum(x => x.dropWeight) + 1);
            var count = 0;
            foreach (var prize in config.prize_settings.participation_rewards.participation_prizes)
            {
                count += prize.dropWeight;
                if (roll <= count)
                {
                    var item = ItemManager.CreateByName(prize.shortname, Convert.ToInt32(UnityEngine.Random.Range(Math.Max(prize.min_quantity, 1), Math.Max(prize.max_quantity, 1) + 1) * mod), prize.skin);
                    if (item == null) return;
                    if (!string.IsNullOrEmpty(prize.displayName)) item.name = prize.displayName;

                    PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + string.Format(lang.GetMessage("ParticipationPrizeGiven", this, player.UserIDString), item.amount, item.name ?? item.info.displayName.english));
                    player.GiveItem(item);
                    break;
                    //found prize.
                }
            }

            if (config.prize_settings.participation_rewards.econParticipationPrize > 0 && Economics != null && Economics.IsLoaded)
            {
                var amount = config.prize_settings.participation_rewards.econParticipationPrize * mod;
                Economics.Call("Deposit", player.userID.Get(), amount);
            }

            if (config.prize_settings.participation_rewards.srpParticipationPrize > 0 && ServerRewards != null && ServerRewards.IsLoaded)
            {
                var amount = Mathf.RoundToInt(config.prize_settings.participation_rewards.srpParticipationPrize * mod);
                ServerRewards.Call("AddPoints", player.userID.Get(), amount);
            }
        }

        object OnPlayerCorpseSpawn(BasePlayer player)
        {
            if (CorpseMonitor.Contains(player.userID))
            {
                CorpseMonitor.Remove(player.userID);
                if (CorpseMonitor.Count == 0 && !IsRunning) Unsubscribe(nameof(OnPlayerCorpseSpawn));
                return true;
            }
            return null;
        }

        // Checks when a corpse is looted, if it is outside of the arena.
        object CanLootEntity(BasePlayer player, LootableCorpse corpse)
        {
            if (CorpseMonitor.Contains(corpse.playerSteamID) && IsOutsideOfArena(corpse.transform.position))
            {
                NextTick(() =>
                {
                    corpse.KillMessage();
                });
                return false;
            }
            return null;
        }

        object CanLootPlayer(BasePlayer target, BasePlayer looter)
        {
            if (Participants.ContainsKey(target.userID) && !Participants.ContainsKey(looter.userID)) return false;
            return null;
        }

        static bool IsOutsideOfArena(Vector3 pos)
        {
            return pos.y < CurrentCentrePoint.y - 50;
        }

        object OnEntityTakeDamage(BasePlayer victim, HitInfo info)
        {
            if (victim == null || info == null || info.InitiatorPlayer == null || victim == info.InitiatorPlayer) return null;

            var damageType = info.damageTypes?.GetMajorityDamageType();
            if (damageType == Rust.DamageType.Radiation || damageType == Rust.DamageType.Fall) return null;

            if (Participants.ContainsKey(victim.userID))
            {
                // PRevents damage from external players or while lobby is active.
                if (!Participants.ContainsKey(info.InitiatorPlayer.userID) || CanJoin)
                {
                    info.damageTypes?.ScaleAll(0f);
                    return true;
                }
            }
            return null;
        }

        #endregion

        #region API

        object isEventPlayer(BasePlayer player)
        {
            if (Participants.ContainsKey(player.userID) || WasEventPlayerList.Contains(player.userID)) return true;
            return null;
        }

        void SendPrizeNotification(BasePlayer player, PCDInfo playerData)
        {
            if (playerData.rewards_remaining <= 0) return;
            if (NotificationSystem == null || !NotificationSystem.IsLoaded) return;
            NotificationSystem.Call("Add", player.userID.Get(), lang.GetMessage("NSMessageTitle", this, player.UserIDString), string.Format(lang.GetMessage("NSMessageBody", this, player.UserIDString), playerData.rewards_remaining), 0, 0ul, null, new List<(string, bool)>() { ("chat.say \"/sprize\"", false) });
        }

        object CanDoubleJump(BasePlayer player)
        {
            if (Participants.ContainsKey(player.userID)) return false;
            return null;
        }

        void RegisterMultiEvent()
        {
            if (MultiEvents == null || !MultiEvents.IsLoaded) return;

            MultiEvents.Call("API_RegisterEvent", Name, new JObject
            {
                ["title"] = "Survival Arena",
                ["description"] = GetMultiEventsDescription(),
                ["commandToStart"] = "startarena",
                ["commandToStop"] = "endarena",
                ["bannerURL"] = "https://www.dropbox.com/scl/fi/ak4nb0gj4z0bsiehewz0j/MultiEventBanner.png?rlkey=2o06gozzzlk9bimmvxl79st4l&st=0auvgavp&dl=1",
                ["icon"] = "https://www.dropbox.com/scl/fi/4hsuphssci1folu4c81x9/MultiEventIcon.png?rlkey=zheo0wd5ok3560d3olvdb0zcg&st=m8ji2a9x&dl=1",
            });
        }

        string GetMultiEventsDescription()
        {
            string result = "Loot crates for equipment and eliminate your fellow contestants to be the last person standing!";
            if (config.prize_settings.rolls_per_claim > 0)
                result += string.Format("\nThe winner will receive {0} prize rolls for winning.", config.prize_settings.rolls_per_claim);
            if (config.prize_settings.participation_rewards.enabled && config.prize_settings.participation_rewards.modifiers.Count > 0)
                result += "\nPlayers will receive rewards for participating.";

            return result;
        }

        object OnAddLootToContainer(LootContainer container)
        {
            if (container == null || container.net == null) return null;
            if (containerSpawnIDs.Contains(container.net.ID.Value)) return true;
            return null;
        }

        object CanSpawnWalker(BasePlayer player, HitInfo info)
        {
            if (!IsRunning) return null;
            if (player.transform.position.y > CurrentCentrePoint.y - 50 && InRange(CurrentCentrePoint, player.transform.position, FurthestEntity + DistanceThreshold)) return true;
            return null;
        }

        object CanTeleport(BasePlayer player, Vector3 pos)
        {
            if (!IsRunning) return null;
            if (Participants.ContainsKey(player.userID)) return lang.GetMessage("CorpseLocationFromArena", this, player.UserIDString);
            if (pos.y > CurrentCentrePoint.y - 50 && InRange(CurrentCentrePoint, pos, FurthestEntity + DistanceThreshold))
                return lang.GetMessage("CorpseLocationBlocked", this, player.UserIDString);
            return null;
        }

        object CLOnCheckLog(BasePlayer player, LootContainer container)
        {
            if (!containerSpawns.Contains(container)) return null;
            return true;
        }

        object OnLifeSupportSavingLife(BasePlayer player)
        {
            if (Participants.ContainsKey(player.userID)) return true;
            return null;
        }

        object CanOpenBackpack(BasePlayer player, ulong backpackOwnerID)
        {
            if (Participants.ContainsKey(player.userID)) return "You cannot open your backpack while at Survival Arena.";
            return null;
        }

        object STOnPouchOpen(BasePlayer player)
        {
            if (Participants.ContainsKey(player.userID)) return true;
            return null;
        }

        object OnRestoreUponDeath(BasePlayer player)
        {
            if (!Participants.ContainsKey(player.userID) && !WasEventPlayerList.Contains(player.userID)) return null;
            return true;
        }

        object CanBePenalized(BasePlayer player)
        {
            if (!Participants.ContainsKey(player.userID) && !WasEventPlayerList.Contains(player.userID)) return null;
            return false;
        }

        object STOnLoseXP(BasePlayer player)
        {
            if (!Participants.ContainsKey(player.userID) && !WasEventPlayerList.Contains(player.userID)) return null;
            return true;
        }

        object CanDropBackpack(ulong backpackOwnerID, Vector3 position)
        {
            var player = BasePlayer.Find(backpackOwnerID.ToString());
            if (player != null && Participants.ContainsKey(player.userID)) return false;
            if (WasEventPlayerList.Contains(backpackOwnerID)) return false;

            return null;
        }

        object STOnPouchDrop(BasePlayer player)
        {
            if (!Participants.ContainsKey(player.userID) && !WasEventPlayerList.Contains(player.userID)) return null;
            return true;
        }

        object CanRevivePlayer(BasePlayer player, Vector3 pos)
        {
            if (!Participants.ContainsKey(player.userID) && !WasEventPlayerList.Contains(player.userID)) return null;
            return false;
        }

        [HookMethod("IsEventPlayer")]
        public object IsEventPlayer(BasePlayer player)
        {
            if (Participants.ContainsKey(player.userID) || WasEventPlayerList.Contains(player.userID)) return true;
            return null;
        }

        void OnPlayerTeleported(BasePlayer player, Vector3 oldPos, Vector3 newPos)
        {
            if (!IsRunning) return;
            if (Participants.ContainsKey(player.userID)) return;
            if (Vector3.Distance(CurrentCentrePoint, newPos) < FurthestEntity + DistanceThreshold && player.transform.position.y > CurrentCentrePoint.y - 50)
            {
                PrintToChat(player, "You cannot teleport to an active event.");
                Player.Teleport(player, oldPos);
            }
        }

        void SubscribeThirdpartyHooks(bool enable)
        {
            if (enable)
            {
                Subscribe(nameof(OnPopulateBetterLoot));
                Subscribe(nameof(OnAddRecipeCardToLootContainer));
                Subscribe(nameof(OnContainerPopulate));
                Subscribe(nameof(CanPopulateLoot));
                Subscribe(nameof(OnCustomLootContainer));
                Subscribe(nameof(STCanReceiveYield));
                Subscribe(nameof(STCanReceiveBonusLootFromContainer));
                Subscribe(nameof(CanReceiveEpicLootFromCrate));
                Subscribe(nameof(OnIngredientAddedToContainer));
                Subscribe(nameof(OnRecipeAddedToContainer));
                Subscribe(nameof(OnLifeSupportSavingLife));
                Subscribe(nameof(OnAddLootToContainer));
                return;
            }
            Unsubscribe(nameof(OnPopulateBetterLoot));
            Unsubscribe(nameof(OnAddRecipeCardToLootContainer));
            Unsubscribe(nameof(OnContainerPopulate));
            Unsubscribe(nameof(CanPopulateLoot));
            Unsubscribe(nameof(OnCustomLootContainer));
            Unsubscribe(nameof(STCanReceiveYield));
            Unsubscribe(nameof(STCanReceiveBonusLootFromContainer));
            Unsubscribe(nameof(CanReceiveEpicLootFromCrate));
            Unsubscribe(nameof(OnIngredientAddedToContainer));
            Unsubscribe(nameof(OnRecipeAddedToContainer));
            Unsubscribe(nameof(OnLifeSupportSavingLife));
            Unsubscribe(nameof(OnAddLootToContainer));
        }

        object OnPopulateBetterLoot(LootContainer container)
        {
            if (containerSpawns.Contains(container)) return true;
            return null;
        }

        object OnAddRecipeCardToLootContainer(BasePlayer player, LootContainer container)
        {
            if (containerSpawns.Contains(container))
            {
                return true;
            }
            return null;
        }

        object OnContainerPopulate(LootContainer container)
        {
            if (containerSpawns.Contains(container)) return true;
            return null;
        }

        object CanPopulateLoot(LootContainer container)
        {
            if (containerSpawns.Contains(container)) return true;
            return null;
        }

        object OnCustomLootContainer(NetworkableId containerID)
        {
            if (containerSpawnIDs.Contains(containerID.Value)) return true;
            return null;
        }

        object STCanReceiveYield(BasePlayer player, BaseEntity entity = null)
        {
            if (Participants.ContainsKey(player.userID)) return false;
            return null;
        }

        object STCanReceiveBonusLootFromContainer(BasePlayer player, LootContainer container)
        {
            if (containerSpawns.Contains(container)) return false;
            return null;
        }

        object CanReceiveEpicLootFromCrate(BasePlayer player, StorageContainer container)
        {
            if (containerSpawns.Contains(container))
            {
                return false;
            }
            return null;
        }

        object OnIngredientAddedToContainer(LootContainer container)
        {
            if (containerSpawns.Contains(container))
            {
                return true;
            }
            return null;
        }

        object OnRecipeAddedToContainer(LootContainer container)
        {
            if (containerSpawns.Contains(container))
            {
                return true;
            }
            return null;
        }

        // Handles truePVE
        object CanEntityTakeDamage(BasePlayer victim, HitInfo info)
        {
            if (victim != null && Participants.ContainsKey(victim.userID)) return true;
            return null;
        }

        object WarMode_OverrideOnEntityTakeDamage(BasePlayer victim, HitInfo info)
        {
            if (victim != null && Participants.ContainsKey(victim.userID)) return true;
            return null;
        }

        bool IsSurivalArenaContainer(LootContainer container)
        {
            return containerSpawns.Contains(container);
        }

        object OnEventJoin(BasePlayer player, string eventName)
        {
            if (Participants.ContainsKey(player.userID)) return "You must leave SurvivalArena to join this event.";
            return null;
        }

        object AreFriends(ulong friendID, ulong playerID)
        {
            if (Participants.ContainsKey(friendID) && Participants.ContainsKey(playerID)) return true;

            return null;
        }

        #endregion

        #region Monobehaviour 

        static int RadAccumulation = 3;
        static int CheckInterval = 1;

        void DestroyMonitor(BasePlayer player)
        {
            var gameObject = player.GetComponent<Monitor>();
            if (gameObject != null) GameObject.DestroyImmediate(gameObject);
        }

        void AddMonitor(BasePlayer player)
        {
            DestroyMonitor(player);
            player.gameObject.AddComponent<Monitor>();
        }

        public class Monitor : MonoBehaviour
        {
            private BasePlayer player;
            private float checkDelay;

            private void Awake()
            {
                player = GetComponent<BasePlayer>();
                checkDelay = Time.time + CheckInterval;
            }

            public void FixedUpdate()
            {
                if (player == null) return;
                if (checkDelay < Time.time)
                {
                    DoCheck();
                    checkDelay = Time.time + CheckInterval;
                }
            }

            private bool SentUI = false;

            public void DoCheck()
            {
                if (IsOutsideOfArena(player.transform.position))
                {
                    Interface.Oxide.LogInfo($"Stripping {player.displayName} due to outside of arena.");
                    player.inventory.Strip();
                    player.Die();
                    return;
                }
                if (Vector3.Distance(player.transform.position, CurrentCentrePoint) > CircleDist)
                {
                    player.metabolism.radiation_level.SetValue(player.metabolism.radiation_level.value + RadAccumulation);
                    player.metabolism.radiation_poison.SetValue(player.metabolism.radiation_poison.value + RadAccumulation);
                    player.metabolism.SendChanges();
                    if (!SentUI)
                    {
                        SentUI = true;
                        BleedingPanel(player);
                    }
                }
                else if (SentUI)
                {
                    SentUI = false;
                    CuiHelper.DestroyUi(player, "BleedingPanel");
                }
            }

            private void OnDestroy()
            {
                enabled = false;
                CancelInvoke();
                CuiHelper.DestroyUi(player, "BleedingPanel");
            }
        }

        object OnPlayerWound(BasePlayer player, HitInfo info)
        {
            if (Participants.ContainsKey(player.userID))
            {
                if (IsOutsideOfArena(player.transform.position)) return false;
            }
            return null;
        }


        #endregion

        #region Outsider behaviour

        static void DestroyOutsiderMonitor(BasePlayer player)
        {
            var gameObject = player.GetComponent<OutsiderBehaviour>();
            if (gameObject != null) GameObject.DestroyImmediate(gameObject);
        }

        void AddOutsiderMonitor(BasePlayer player)
        {
            DestroyOutsiderMonitor(player);
            player.gameObject.AddComponent<OutsiderBehaviour>();
        }

        bool HasOutsiderMonitor(BasePlayer player)
        {
            var gameObject = player.GetComponent<OutsiderBehaviour>();
            return gameObject != null;
        }

        static float SecondsToVacate;
        static float DistanceThreshold;

        public class OutsiderBehaviour : MonoBehaviour
        {
            private BasePlayer player;
            private float checkDelay;
            private float startedCheck;

            private void Awake()
            {
                player = GetComponent<BasePlayer>();
                checkDelay = Time.time + CheckInterval;
                player.ChatMessage("You have entered a restricted area. Vacate the area or you will be destroyed.");
                startedCheck = Time.time;
            }

            public void FixedUpdate()
            {
                if (player == null)
                {
                    DestroyOutsiderMonitor(player);
                    return;
                }
                if (checkDelay < Time.time)
                {
                    DoCheck();
                    checkDelay = Time.time + CheckInterval;
                }
            }

            public void DoCheck()
            {
                if (Vector3.Distance(CurrentCentrePoint, player.transform.position) < FurthestEntity + DistanceThreshold && player.transform.position.y > CurrentCentrePoint.y - 50)
                {
                    if (Time.time - startedCheck > SecondsToVacate)
                        DestroyPlayer();
                    else player.ChatMessage($"You have {Math.Round(startedCheck + SecondsToVacate - Time.time, 2)} seconds left to vacate the area");
                }
                else DestroyOutsiderMonitor(player);
            }

            private void DestroyPlayer()
            {
                if (player != null)
                {
                    player.Die();
                    player.ChatMessage("You were slain for getting too close to the Survival Arena event!");
                }
                DestroyOutsiderMonitor(player);
            }

            private void OnDestroy()
            {
                enabled = false;
                CancelInvoke();
            }
        }

        #endregion

        #region Adding/removing entities

        private const int LAYER_TARGET = ~(1 << 2 | 1 << 3 | 1 << 4 | 1 << 10 | 1 << 18 | 1 << 28 | 1 << 29);
        private BaseEntity GetTargetEntity(BasePlayer player)
        {
            RaycastHit raycastHit;
            bool flag = Physics.Raycast(player.eyes.HeadRay(), out raycastHit, 5, LAYER_TARGET);
            var targetEntity = flag ? raycastHit.GetEntity() : null;
            return targetEntity;
        }

        [ChatCommand("saremove")]
        void RemoveEntity(BasePlayer player)
        {
            if (!IsAdmin(player))
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("NoPerms", this, player.UserIDString));
                return;
            }

            if (!IsRunning && !CanJoin)
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("AddEntityEventRunning", this, player.UserIDString));
                return;
            }
            if (EventElevationMod > 0f)
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("AddEntityElevation", this, player.UserIDString));
                return;
            }

            var target = GetTargetEntity(player);
            if (target == null || target.PrefabName.StartsWith("assets/bundled/prefabs/modding/admin/") || target.ShortPrefabName == "gates.external.high.wood")
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("NullInvalidEntity", this, player.UserIDString));
                return;
            }

            foreach (var entry in CurrentArena.entities)
            {
                if (entry.pos == target.transform.position)
                {
                    PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + string.Format(lang.GetMessage("EntityRemoved", this, player.UserIDString), entry.prefab, entry.pos, entry.rot));
                    CurrentArena.entities.Remove(entry);
                    target.KillMessage();
                    return;
                }
            }
            PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + string.Format(lang.GetMessage("EntityNotFoundInData", this, player.UserIDString), target.PrefabName, target.transform.position, target.transform.rotation.eulerAngles));
        }

        [ChatCommand("addtree")]
        void AddTree(BasePlayer player)
        {
            if (!IsAdmin(player))
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("NoPerms", this, player.UserIDString));
                return;
            }

            AddSpawn(player, "tree");
        }

        [ChatCommand("addbush")]
        void AddBush(BasePlayer player)
        {
            if (!IsAdmin(player))
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("NoPerms", this, player.UserIDString));
                return;
            }

            AddSpawn(player, "bush");
        }

        [ChatCommand("addlog")]
        void AddLog(BasePlayer player)
        {
            if (!IsAdmin(player))
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("NoPerms", this, player.UserIDString));
                return;
            }

            AddSpawn(player, "log");
        }

        [ChatCommand("addloot")]
        void AddLoot(BasePlayer player)
        {
            if (!IsAdmin(player))
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("NoPerms", this, player.UserIDString));
                return;
            }

            AddSpawn(player, "loot");
        }

        void AddSpawn(BasePlayer player, string type)
        {
            if (!IsRunning && !CanJoin && !DevMode)
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("AddEntityEventRunning", this, player.UserIDString));
                return;
            }
            if (EventElevationMod > 0f)
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("AddEntityElevation", this, player.UserIDString));
                return;
            }
            Vector3 rot = new Vector3(0, UnityEngine.Random.Range(-90f, 90f), 0);
            BaseEntity entity;

            Vector3 pos = new Vector3(player.transform.position.x, player.transform.position.y - 0.1f, player.transform.position.z);

            switch (type)
            {
                case "tree":                   
                    PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + string.Format(lang.GetMessage("EntityAddedTree", this, player.UserIDString), pos));
                    entity = GameManager.server.CreateEntity(GetRandomTree(player.transform.position), pos, Quaternion.Euler(rot));
                    if (entity is TreeEntity tree)
                    {
                        tree.treeAdditionSpawnChance = -1f;
                    }
                    entity.Spawn();
                    spawned_entities.Add(entity);
                    CurrentArena.entities.Add(new EntityInfo("assets/prefabs/misc/xmas/pookie/pookie_deployed.prefab", pos, Vector3.zero, Vector3.one));
                    SaveData(SaveType.Arena);
                    return;

                case "bush":                    
                    PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + string.Format(lang.GetMessage("EntityAddedBush", this, player.UserIDString), pos));
                    entity = GameManager.server.CreateEntity(GetRandomBush(player.transform.position), pos, Quaternion.Euler(rot));
                    entity.Spawn();
                    spawned_entities.Add(entity);
                    CurrentArena.entities.Add(new EntityInfo("assets/prefabs/deployable/playerioents/gates/branch/electrical.branch.deployed.prefab", pos, Vector3.zero, Vector3.one));
                    SaveData(SaveType.Arena);
                    return;

                case "loot":                    
                    PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + string.Format(lang.GetMessage("EntityAddedLoot", this, player.UserIDString), pos));
                    entity = GameManager.server.CreateEntity("assets/bundled/prefabs/radtown/crate_normal_2.prefab", pos, Quaternion.Euler(rot));
                    containerSpawns.Add((LootContainer)entity);
                    containerSpawnIDs.Add(entity.net.ID.Value);
                    entity.Spawn();
                    spawned_entities.Add(entity);
                    CurrentArena.entities.Add(new EntityInfo("assets/bundled/prefabs/radtown/crate_normal_2.prefab", pos, Vector3.zero, Vector3.one));
                    SaveData(SaveType.Arena);
                    return;

                case "log":                    
                    PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + string.Format(lang.GetMessage("EntityAddedLog", this, player.UserIDString), pos));
                    entity = GameManager.server.CreateEntity(GetRandomLog(player.transform.position), pos, Quaternion.Euler(rot));
                    entity.Spawn();
                    spawned_entities.Add(entity);
                    CurrentArena.entities.Add(new EntityInfo("assets/prefabs/deployable/playerioents/lights/flasherlight/electric.flasherlight.deployed.prefab", pos, Vector3.zero, Vector3.one));
                    SaveData(SaveType.Arena);
                    return;

                default:
                    PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("EntityAddInvalidType", this, player.UserIDString));
                    return;
            }
        }

        #endregion

        #region Radiation Sphere

        List<BaseEntity> Spheres = new List<BaseEntity>();

        static string SpherePrefab;

        private void CreateSphere(Vector3 position, float radius, int darkness, float speed)
        {
            for (int i = 0; i < darkness; i++)
            {
                SphereEntity sphere = (SphereEntity)GameManager.server.CreateEntity(SpherePrefab, position, new Quaternion(), true);
                sphere.currentRadius = radius * 2;
                sphere.lerpSpeed = speed * 2;
                sphere.Spawn();
                Spheres.Add(sphere);
            }
        }

        private void destroyspheres()
        {
            foreach (var sphere in Spheres)
            {
                if (sphere != null)
                    sphere.KillMessage();
            }
            Spheres.Clear();
        }

        #endregion

        #region CUI

        #region HUDs

        static void BleedingPanel(BasePlayer player)
        {
            var container = new CuiElementContainer();

            container.Add(new CuiPanel
            {
                CursorEnabled = false,
                Image = { Color = Instance.config.radiationZoneSettings.panel_colour },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "-0.01 0.01", OffsetMax = "0.1 0.1" }
            }, Instance.config.gameSettings.allow_looting_in_rad_zone ? "Hud" : "Overlay", "BleedingPanel");

            container.Add(new CuiElement
            {
                Name = "BleedingOutText",
                Parent = "BleedingPanel",
                FadeOut = 1,
                Components = {
                    new CuiTextComponent { Text = Instance.lang.GetMessage("InRadZone", Instance), Font = "robotocondensed-bold.ttf", FontSize = 50, Align = TextAnchor.MiddleCenter, Color = "1 0.7532371 0 1", FadeIn = 1 },
                    new CuiOutlineComponent { Color = "0 0 0 1", Distance = "1 1" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-192.131 21.616", OffsetMax = "192.131 171.984" }
                }
            });

            CuiHelper.DestroyUi(player, "BleedingPanel");
            CuiHelper.AddUi(player, container);
        }

        private void PlayerCounter(BasePlayer player, int remaining)
        {
            var container = new CuiElementContainer();
            container.Add(new CuiElement
            {
                Name = "PlayerCounter",
                Parent = "Hud",
                Components = {
                    new CuiTextComponent { Text = string.Format(lang.GetMessage("UIPlayersRemaining", this, player.UserIDString), remaining), Font = "robotocondensed-bold.ttf", FontSize = 14, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                    new CuiOutlineComponent { Color = "0 0 0 0.5", Distance = "1 -1" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 0", AnchorMax = "0.5 0", OffsetMin = $"{-159.3 + config.anchors.player_count_anchor_adjustment.Key} {68.881 + config.anchors.player_count_anchor_adjustment.Value}", OffsetMax = $"{68.924 + config.anchors.player_count_anchor_adjustment.Key} {92.519 + config.anchors.player_count_anchor_adjustment.Value}" }
                }
            });

            CuiHelper.DestroyUi(player, "PlayerCounter");
            CuiHelper.AddUi(player, container);
        }

        void SendCounterUpdate()
        {
            int count = Participants.Count;
            foreach (var player in Participants)
            {
                PlayerCounter(player.Value, count);
            }
        }

        private void CircleStatusHUD(BasePlayer player, int activity)
        {
            var container = new CuiElementContainer();
            container.Add(new CuiElement
            {
                Name = "CircleStatusHUD",
                Parent = "Hud",
                Components = {
                    new CuiTextComponent { Text = string.Format(lang.GetMessage("CircleStatusUpdate", this, player.UserIDString), activity == 0 ? lang.GetMessage("CircleStatusINACTIVE", this, player.UserIDString) : activity == 1 ? lang.GetMessage("CircleStatusMOVING", this, player.UserIDString) : lang.GetMessage("CircleStatusSTOPPED", this, player.UserIDString)), Font = "robotocondensed-regular.ttf", FontSize = 16, Align = TextAnchor.UpperCenter, Color = "1 1 1 1" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = "-100.003 -63.9", OffsetMax = "99.997 -33.9" }
                }
            });

            CuiHelper.DestroyUi(player, "CircleStatusHUD");
            CuiHelper.AddUi(player, container);
        }

        #endregion

        #region Button UI

        private void EventLeaveButton(BasePlayer player)
        {
            var container = new CuiElementContainer();
            container.Add(new CuiPanel
            {
                CursorEnabled = false,
                Image = { Color = "0.4036579 0.4223583 0.4433962 0.8509804" },
                RectTransform = { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = "-319.8 -34", OffsetMax = "-243.8 -2" }
            }, "Overlay", "EventLeaveButton");

            container.Add(new CuiElement
            {
                Name = "Label_8022",
                Parent = "EventLeaveButton",
                Components = {
                    new CuiTextComponent { Text = lang.GetMessage("UILeaveButton", this, player.UserIDString), Font = "robotocondensed-bold.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    new CuiOutlineComponent { Color = "0 0 0 0.5", Distance = "1 -1" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-38 -16", OffsetMax = "38 16" }
                }
            });

            container.Add(new CuiButton
            {
                Button = { Color = "1 1 1 0", Command = "survivalarenaleaveevent" },
                Text = { Text = " ", Font = "robotocondensed-regular.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0 0 0 1" },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-38 -16", OffsetMax = "38 16" }
            }, "EventLeaveButton", "Button_63");

            CuiHelper.DestroyUi(player, "EventLeaveButton");
            CuiHelper.AddUi(player, container);
        }

        [ConsoleCommand("survivalarenaleaveevent")]
        void SurvivalArenaLeaveEventButtonPressed(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            CuiHelper.DestroyUi(player, "EventLeaveButton");
            LeaveEventCMD(player);
        }

        #endregion

        #region Game start UI

        private void MenuBackground(BasePlayer player)
        {
            var container = new CuiElementContainer();
            container.Add(new CuiPanel
            {
                CursorEnabled = true,
                Image = { Color = "0 0 0 0.9803922" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "0 -3.265", OffsetMax = "0 -0.335" }
            }, "Overlay", "MenuBackground");

            CuiHelper.DestroyUi(player, "MenuBackground");
            CuiHelper.AddUi(player, container);
        }

        private Dictionary<string, string> DelayFieldInputText = new Dictionary<string, string>();
        private Dictionary<string, string> HeightFieldInputText = new Dictionary<string, string>();

        private void GameStartUI(BasePlayer player, string startDelay = "0", string heightMod = "0", string profile = "null", string arena = "null")
        {
            if (startDelay == "0") startDelay = config.gameSettings.defaultStartTime.ToString();
            var container = new CuiElementContainer();
            container.Add(new CuiPanel
            {
                CursorEnabled = true,
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "0 -3.265", OffsetMax = "0 -0.335" }
            }, "Overlay", "GameStartUI");

            container.Add(new CuiElement
            {
                Name = "LootProfileHeader",
                Parent = "GameStartUI",
                Components = {
                    new CuiTextComponent { Text = lang.GetMessage("UILootProfile", this, player.UserIDString), Font = "robotocondensed-bold.ttf", FontSize = 20, Align = TextAnchor.UpperCenter, Color = "1 1 1 1" },
                    new CuiOutlineComponent { Color = "0 0 0 0.5", Distance = "1 -1" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-100 145.7", OffsetMax = "100 175.7" }
                }
            });

            var count = 0;
            int finalCount;
            foreach (var prof in config.lootSettings.Keys)
            {
                container.Add(new CuiPanel
                {
                    CursorEnabled = false,
                    Image = { Color = "0.1226415 0.1226415 0.1226415 1" },
                    RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = $"-100 {-63.4 - (count * 40)}", OffsetMax = $"100 {-33.4 - (count * 40)}" }
                }, "LootProfileHeader", "Profile");

                container.Add(new CuiElement
                {
                    Name = "text",
                    Parent = "Profile",
                    Components = {
                    new CuiTextComponent { Text = prof == profile ? string.Format(lang.GetMessage("UIProfSelected", this, player.UserIDString), prof.TitleCase()) : prof.TitleCase(), Font = "robotocondensed-regular.ttf", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    new CuiOutlineComponent { Color = "0 0 0 0.5", Distance = "1 -1" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-100 -15", OffsetMax = "100 15" }
                }
                });

                container.Add(new CuiButton
                {
                    Button = { Color = "1 1 1 0", Command = $"survivalarenaselectprofile {prof} {arena}" },
                    Text = { Text = " ", Font = "robotocondensed-regular.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0 0 0 1" },
                    RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-100 -15", OffsetMax = "100 15" }
                }, "Profile", "button");

                count++;
            }
            finalCount = count;
            container.Add(new CuiElement
            {
                Name = "HeightModHeader",
                Parent = "GameStartUI",
                Components = {
                    new CuiTextComponent { Text = lang.GetMessage("UIHeightMod", this, player.UserIDString), Font = "robotocondensed-bold.ttf", FontSize = 20, Align = TextAnchor.UpperCenter, Color = "1 1 1 1" },
                    new CuiOutlineComponent { Color = "0 0 0 0.5", Distance = "1 -1" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-220 145.7", OffsetMax = "-120 175.7" }
                }
            });

            container.Add(new CuiPanel
            {
                CursorEnabled = false,
                Image = { Color = "0.1226415 0.1226415 0.1226415 1" },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-50 -63.4", OffsetMax = "50 -33.4" }
            }, "HeightModHeader", "HeightMod");

            container.Add(new CuiElement
            {
                Name = "inputHeightText",
                Parent = "HeightMod",
                Components =
                {
                    new CuiInputFieldComponent
                    {
                        Text = heightMod ?? "0",
                        CharsLimit = 40,
                        Color = "1 1 1 1",
                        IsPassword = false,
                        Command = $"{"heightmod.heightyinputtextcb"} {startDelay} 51800 {profile} {arena}",
                        Font = "robotocondensed-regular.ttf",
                        FontSize = 16,
                        NeedsKeyboard = true,
                        Align = TextAnchor.MiddleCenter
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin = "-50 -15",
                        OffsetMax = "50 15"
                    }
                }
            });

            container.Add(new CuiElement
            {
                Name = "LobbyTimeHeader",
                Parent = "GameStartUI",
                Components = {
                    new CuiTextComponent { Text = lang.GetMessage("UILobbyTime", this, player.UserIDString), Font = "robotocondensed-bold.ttf", FontSize = 20, Align = TextAnchor.UpperCenter, Color = "1 1 1 1" },
                    new CuiOutlineComponent { Color = "0 0 0 0.5", Distance = "1 -1" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "120 145.7", OffsetMax = "220 175.7" }
                }
            });

            container.Add(new CuiPanel
            {
                CursorEnabled = false,
                Image = { Color = "0.1226415 0.1226415 0.1226415 1" },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-50 -63.4", OffsetMax = "50 -33.4" }
            }, "LobbyTimeHeader", "LobbyTime");

            container.Add(new CuiElement
            {
                Name = "inputText",
                Parent = "LobbyTime",
                Components =
                {
                    new CuiInputFieldComponent
                    {
                        Text = startDelay ?? string.Empty,
                        CharsLimit = 40,
                        Color = "1 1 1 1",
                        IsPassword = false,
                        Command = $"{"delaytime.delayinputtextcb"} {heightMod} {profile} {arena}",
                        Font = "robotocondensed-regular.ttf",
                        FontSize = 16,
                        NeedsKeyboard = true,
                        Align = TextAnchor.MiddleCenter
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin = "-50 -15",
                        OffsetMax = "50 15"
                    }
                }
            });

            container.Add(new CuiPanel
            {
                CursorEnabled = false,
                Image = { Color = "0.1215686 0.1215686 0.1215686 1" },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = $"20 {45 - (finalCount * 40)}", OffsetMax = $"100 {75 - (finalCount * 40)}" }
            }, "GameStartUI", "StopStartButton");

            container.Add(new CuiElement
            {
                Name = "text",
                Parent = "StopStartButton",
                Components = {
                    new CuiTextComponent { Text = IsRunning || CanJoin ? lang.GetMessage("UISTOP", this, player.UserIDString) : lang.GetMessage("UISTART", this, player.UserIDString), Font = "robotocondensed-regular.ttf", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    new CuiOutlineComponent { Color = "0 0 0 0.5", Distance = "1 -1" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-40 -15", OffsetMax = "40 15" }
                }
            });

            container.Add(new CuiButton
            {
                Button = { Color = "1 1 1 0", Command = $"stopstartsurvivalarena {heightMod} {startDelay} {arena} {profile}" },
                Text = { Text = " ", Font = "robotocondensed-regular.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0 0 0 1" },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-40 -15", OffsetMax = "40 15" }
            }, "StopStartButton", "button");

            //
            count = 0;
            container.Add(new CuiElement
            {
                Name = "ArenaProfileHeader",
                Parent = "GameStartUI",
                Components = {
                    new CuiTextComponent { Text = "Arena", Font = "robotocondensed-bold.ttf", FontSize = 20, Align = TextAnchor.UpperCenter, Color = "1 1 1 1" },
                    new CuiOutlineComponent { Color = "0 0 0 0.5", Distance = "1 -1" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = $"-440 145.7", OffsetMax = "-240 175.7" }
                }
            });
            foreach (var _arena in Arenas.Keys)
            {
                container.Add(new CuiPanel
                {
                    CursorEnabled = false,
                    Image = { Color = "0.1226415 0.1226415 0.1226415 1" },
                    RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = $"-100 {-63.4 - (count * 40)}", OffsetMax = $"100 {-33.4 - (count * 40)}" }
                }, "ArenaProfileHeader", "Profile");

                container.Add(new CuiElement
                {
                    Name = "text",
                    Parent = "Profile",
                    Components = {
                    new CuiTextComponent { Text = _arena == arena ? string.Format(lang.GetMessage("UIProfSelected", this, player.UserIDString), _arena.TitleCase()) : _arena.TitleCase(), Font = "robotocondensed-regular.ttf", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    new CuiOutlineComponent { Color = "0 0 0 0.5", Distance = "1 -1" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-100 -15", OffsetMax = "100 15" }
                }
                });

                container.Add(new CuiButton
                {
                    Button = { Color = "1 1 1 0", Command = $"survivalarenaselectarena {profile} {_arena}" },
                    Text = { Text = " ", Font = "robotocondensed-regular.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0 0 0 1" },
                    RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-100 -15", OffsetMax = "100 15" }
                }, "Profile", "button");
                count++;
            }

            //Interface.Oxide.LogInfo($"Level = X[3761] * SqrtXP[{Math.Sqrt(xp)}] (xp: {xp}): {x * Math.Sqrt(xp)}");

            container.Add(new CuiPanel
            {
                CursorEnabled = false,
                Image = { Color = "0.1215686 0.1215686 0.1215686 1" },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = $"-100 {45 - (finalCount * 40)}", OffsetMax = $"-20 {75 - (finalCount * 40)}" }
            }, "GameStartUI", "CloseButton");

            container.Add(new CuiElement
            {
                Name = "text",
                Parent = "CloseButton",
                Components = {
                    new CuiTextComponent { Text = lang.GetMessage("UICLOSE", this, player.UserIDString), Font = "robotocondensed-regular.ttf", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    new CuiOutlineComponent { Color = "0 0 0 0.5", Distance = "1 -1" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-40 -15", OffsetMax = "40 15" }
                }
            });

            container.Add(new CuiButton
            {
                Button = { Color = "1 1 1 0", Command = "survivalarenaclosestartmenu" },
                Text = { Text = " ", Font = "robotocondensed-regular.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0 0 0 1" },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-40 -15", OffsetMax = "40 15" }
            }, "CloseButton", "button");

            CuiHelper.DestroyUi(player, "GameStartUI");
            CuiHelper.AddUi(player, container);
        }

        [ConsoleCommand("survivalarenaclosestartmenu")]
        void CloseStartUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            CuiHelper.DestroyUi(player, "GameStartUI");
            CuiHelper.DestroyUi(player, "MenuBackground");
        }

        [ChatCommand("survivalarena")]
        void StartArenaUI(BasePlayer player)
        {
            if (!IsAdmin(player))
            {
                PrintToChat(player, lang.GetMessage("NoPerms", this, player.UserIDString));
                return;
            }
            MenuBackground(player);
            GameStartUI(player);
        }

        [ConsoleCommand("delaytime.delayinputtextcb")]
        void DelayInputText(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !IsAdmin(player)) return;

            string heightMod = arg.Args.Length > 0 ? arg.GetString(0) : "0";
            string profile = arg.Args.Length > 1 ? arg.GetString(1) : "null";
            string arena = arg.Args.Length > 2 ? arg.GetString(2) : "null";

            if (arg.Args.Length <= 0)
            {
                if (DelayFieldInputText.ContainsKey(player.UserIDString))
                    DelayFieldInputText.Remove(player.UserIDString);

                return;
            }

            if (DelayFieldInputText.ContainsKey(player.UserIDString))
            {
                DelayFieldInputText[player.UserIDString] = arg.GetString(3);
            }
            else
            {
                DelayFieldInputText.Add(player.UserIDString, arg.GetString(3));
            }

            string timeDelay = arg.Args.Length > 3 ? arg.GetString(3) : config.gameSettings.defaultStartTime.ToString();

            GameStartUI(player, timeDelay, heightMod, profile, arena);
        }

        [ConsoleCommand("heightmod.heightyinputtextcb")]
        void HeightInputText(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !IsAdmin(player)) return;

            if (!float.TryParse(arg.Args[0], out var timeDelay)) timeDelay = config.gameSettings.defaultStartTime;
            string profile = arg.Args.Length > 2 ? arg.GetString(2) : "null";
            string arena = arg.Args.Length > 3 ? arg.GetString(3) : "null";
            float heightMod = 0;
            if (arg.Args.Length > 4) float.TryParse(arg.Args[4], out heightMod);
            var heightModStr = heightMod.ToString();
            if (heightMod != 0) HeightFieldInputText[player.UserIDString] = heightModStr;
            else HeightFieldInputText.Remove(player.UserIDString);
            GameStartUI(player, timeDelay.ToString(), heightModStr, profile, arena);
        }

        [ConsoleCommand("stopstartsurvivalarena")]
        void StartButtonPressed(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !IsAdmin(player)) return;

            CuiHelper.DestroyUi(player, "GameStartUI");
            CuiHelper.DestroyUi(player, "MenuBackground");

            if (!float.TryParse(arg.Args[0], out var height)) return;
            if (!int.TryParse(arg.Args[1], out var time)) return;
            var arenaName = arg.Args.Length > 2 ? Arenas.ContainsKey(arg.GetString(2)) ? arg.GetString(2) : GetRandomArena() : GetRandomArena();
            var profile = arg.Args.Length > 3 ? arg.GetString(3) : null;

            if (IsRunning || CanJoin)
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("UIEndedEvent", this, player.UserIDString));
                EndEvent();
                return;
            }

            if (IsDespawning)
            {
                arg.ReplyWith(lang.GetMessage("Prefix", this, player?.UserIDString) + lang.GetMessage("EventStillDespawning", this, player?.UserIDString));
                return;
            }

            if (string.IsNullOrEmpty(profile) || profile == "null")
            {
                profile = GetRandomProfile();
            }

            LobbyTime = time;

            PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + string.Format(lang.GetMessage("UIStartingEvent", this, player.UserIDString), time, height, profile));

            StartEvent(arenaName, height, profile);
        }

        string GetRandomProfile()
        {
            var totalweight = config.lootSettings.Sum(x => x.Value.profileWeight);

            var roll = UnityEngine.Random.Range(0, totalweight + 1);
            var check = 0;
            foreach (var kvp in config.lootSettings)
            {
                check += kvp.Value.profileWeight;
                if (roll <= check) return kvp.Key;
            }

            string result;
            List<string> randProfile = Pool.Get<List<string>>();
            randProfile.AddRange(config.lootSettings.Keys);
            result = randProfile.GetRandom();
            Pool.FreeUnmanaged(ref randProfile);

            return result;
        }

        [ConsoleCommand("survivalarenaselectprofile")]
        void SelectProfile(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !IsAdmin(player)) return;

            GameStartUI(player, DelayFieldInputText.ContainsKey(player.UserIDString) ? DelayFieldInputText[player.UserIDString] : config.gameSettings.defaultStartTime.ToString(), HeightFieldInputText.ContainsKey(player.UserIDString) ? HeightFieldInputText[player.UserIDString] : "0", arg.GetString(0), arg.GetString(1));
        }

        [ConsoleCommand("survivalarenaselectarena")]
        void SelectArena(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !IsAdmin(player)) return;

            GameStartUI(player, DelayFieldInputText.TryGetValue(player.UserIDString, out var _time) ? _time : config.gameSettings.defaultStartTime.ToString(), HeightFieldInputText.TryGetValue(player.UserIDString, out var _height) ? _height : "0", arg.GetString(0), arg.GetString(1));
        }

        #endregion

        #region Start timer UI

        private void STARTCountdownUI(BasePlayer player, int seconds)
        {
            var container = new CuiElementContainer();
            container.Add(new CuiElement
            {
                Name = "STARTCountdownUI",
                Parent = "Overlay",
                Components = {
                    new CuiTextComponent { Text = string.Format(lang.GetMessage("_UILobbyTime", this, player.UserIDString), seconds, config.command_settings.leave_command), Font = "robotocondensed-regular.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    new CuiOutlineComponent { Color = "0 0 0 0.5", Distance = "1 -1" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = "-100.003 -163.9", OffsetMax = "99.997 -103.9" }
                }
            });

            CuiHelper.DestroyUi(player, "STARTCountdownUI");
            CuiHelper.AddUi(player, container);
        }

        #endregion

        #endregion

        #region Rewards

        [ChatCommand("sprize")]
        void ClaimPrize(BasePlayer player)
        {
            if (Participants.ContainsKey(player.userID)) return;
            if (config.prize_settings.rolls_per_claim < 0)
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("PrizesDisabled", this, player.UserIDString));
                return;
            }
            PCDInfo pi;
            if (!pcdData.pEntity.TryGetValue(player.userID, out pi) || pi.rewards_remaining <= 0)
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("NoPrize", this, player.UserIDString));
                return;
            }

            HandleRewards(player, pi);            
        }

        void HandleRewards(BasePlayer player, PCDInfo playerData)
        {
            for (int j = 0; j < playerData.rewards_remaining; j++)
            {
                for (int i = 0; i < config.prize_settings.rolls_per_claim; i++)
                {
                    RollReward(player);
                }
            }
            playerData.rewards_remaining = 0;
            Interface.Call("OnSurvivalArenaPrizesRedeemed", player);

            PlaySound(player, config.soundsSettings.sound_for_prize);
        }

        void RollReward(BasePlayer player)
        {
            var roll = UnityEngine.Random.Range(0, config.prize_settings.prizes.Sum(x => x.dropWeight) + 1);
            var count = 0;
            foreach (var prize in config.prize_settings.prizes)
            {
                count += prize.dropWeight;
                if (roll <= count)
                {
                    var item = ItemManager.CreateByName(prize.shortname, UnityEngine.Random.Range(Math.Max(prize.min_quantity, 1), Math.Max(prize.max_quantity, 1) + 1), prize.skin);
                    if (item == null) return;
                    if (!string.IsNullOrEmpty(prize.displayName)) item.name = prize.displayName;

                    PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + string.Format(lang.GetMessage("PrizeGiven", this, player.UserIDString), item.amount, item.name ?? item.info.displayName.english));
                    player.GiveItem(item);
                    break;
                    //found prize.
                }
            }
        }

        #endregion

        #region Handle web request
        // Credit Fetch by Wulf for a lot of the code
        bool RequestMade = false;
        void AddArenaFile()
        {
            if (Interface.Oxide.DataFileSystem.ExistsDatafile(subDirectory + "World_2_0_Arena")) return;
            Puts("Missing the Arena.json file in data. Attempting to obtain it.");
            Uri uriResult;
            string url = "https://www.dropbox.com/scl/fi/79dwxygcl7tn0icilijtb/World_2_0_Arena.json?rlkey=0tyz5y08zkdqx94hs0qw0hzcu&st=hzphbr0k&dl=1";
            bool uriTest = Uri.TryCreate(url, UriKind.Absolute, out uriResult)
                && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
            if (uriTest)
            {
                try
                {
                    webrequest.Enqueue(url, null, (code, response) =>
                      GetCallback(code, response, url, "World_2_0_Arena"), this);
                    RequestMade = true;
                }
                catch
                {
                    Puts("Invalid request. Failed to fetch World_2_0_Arena file [1].");
                    NextTick(() => Interface.Oxide.UnloadPlugin(Name));
                    return;
                }
            }
            else
            {
                Puts("Invalid request. Failed to fetch World_2_0_Arena file [2].");
                NextTick(() => Interface.Oxide.UnloadPlugin(Name));
                return;
            }
        }

        private void GetCallback(int code, string response, string uri, string savefilename)
        {
            if (response == null || code != 200)
            {
                Puts($"Error fetching file: {code}");
                return;
            }
            string SaveFilePath = String.Format($"{Interface.Oxide.DataDirectory}\\survivalArena\\{savefilename}");
            try
            {
                var json = JObject.Parse(response);
                Interface.Oxide.DataFileSystem.WriteObject(SaveFilePath, json);
                var arenaData = Interface.Oxide.DataFileSystem.ReadObject<ArenaData>(subDirectory + "World_2_0_Arena");
                if (arenaData.CenterPoint == Vector3.zero)
                {
                    Puts("Failed to acquire CentrePoint");
                    Interface.Oxide.UnloadPlugin(Name);
                    return;
                }
                if (Arenas.ContainsKey("World_2_0_Arena")) Arenas["World_2_0_Arena"] = arenaData;
                else Arenas.Add("World_2_0_Arena", arenaData);
                HaveArenaFile = true;
                Puts("Saved Arena file to survivalArena/World_2_0_Arena.json");
                RequestMade = false;
            }
            catch
            {
                Puts($"Failed to save {savefilename} in {SaveFilePath}. Creating new one.");
                Interface.Oxide.UnloadPlugin(Name);
                return;
            }
        }

        #endregion

        #region Event Handling

        PCDInfo GetPlayerData(ulong id, bool create = true)
        {
            PCDInfo result;
            if (!pcdData.pEntity.TryGetValue(id, out result))
            {
                if (create) pcdData.pEntity.Add(id, result = new PCDInfo());
                else result = null;
            }
            return result;
        }

        void AnnouncePlayerJoin(BasePlayer player)
        {
            string message = ValidJoinMessages.Count > 0 ? ValidJoinMessages.GetRandom() : "SAJointAnnounceMsg_1";
            foreach (var p in BasePlayer.activePlayerList)
                PrintToChat(p, lang.GetMessage("Prefix", this) + string.Format(lang.GetMessage(message, this, p.UserIDString), player.displayName));
        }

        List<string> ValidJoinMessages = new List<string>();

        #region Store data

        bool EnrollPlayer(BasePlayer player)
        {
            var playerData = GetPlayerData(player.userID);
            if (!CanJoinEvent(player, playerData))
            {
                return false;
            }
            StorePlayerItems(player, playerData);
            StorePlayerMetabolism(player, playerData);
            StorePlayerMods(player, playerData);
            playerData.Location = player.transform.position;
            if (!config.eventSettings.allow_teams) HandleTeam(player, true);
            Teleport(player, CurrentCentrePoint, true);
            AnnouncePlayerJoin(player);
            Interface.CallHook("OnEventJoined", player, this.Name);
            if (NightVision != null && NightVision.IsLoaded && config.gameSettings.use_nightvision)
            {
                if (!permission.UserHasPermission(player.UserIDString, "nightvision.allowed"))
                {
                    playerData.GrantedNightVision = true;
                    permission.GrantUserPermission(player.UserIDString, "nightvision.allowed", NightVision);
                }
                NightVision.Call("LockPlayerTime", player, 10f);
            }
            return true;
        }

        void RevokeGrantedNightVision(BasePlayer player, PCDInfo playerData)
        {
            if (playerData == null || !playerData.GrantedNightVision) return;
            playerData.GrantedNightVision = false;
            permission.RevokeUserPermission(player.UserIDString, "nightvision.allowed");
        }

        bool CanJoinEvent(BasePlayer player, PCDInfo playerData)
        {
            if (Participants.ContainsKey(player.userID))
            {
                PrintToChat(player, "You are already enrolled.");
                return false;
            }
            if (player.IsDead() || player.IsWounded())
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("DeadOrWounded", this, player.UserIDString));
                return false;
            }
            var allItems = AllItems(player);
            try
            {
                if (config.eventSettings.prevent_entry_with_items && allItems.Count > 0)
                {
                    PrintToChat(player, lang.GetMessage("HasItemsInInventory", this, player.UserIDString));
                    return false;
                }

                if (playerData.Items.Count > 0)
                {
                    PrintToChat(player, string.Format(lang.GetMessage("RestoreItemsCanJoinEvent", this, player.UserIDString), config.eventSettings.restore_items_command));
                    return false;
                }

                if (player.inventory.crafting.queue.Count != 0)
                {
                    PrintToChat(player, lang.GetMessage("NoCrafting", this, player.UserIDString));
                    return false;
                }

                if (NoEscape != null && config.eventSettings.prevent_no_escape_joins && Convert.ToBoolean(NoEscape.Call("IsEscapeBlocked", player.UserIDString)))
                {
                    PrintToChat(player, lang.GetMessage("EscapeBlocked", this, player.UserIDString));
                    return false;
                }

                if (HasProhibitedItems(player, allItems)) return false;

                var hook = Interface.CallHook("OnEventJoin", player, this.Name);
                if (hook != null)
                {
                    if (hook is string) PrintToChat(player, hook as string);
                    else PrintToChat(player, "An external plugin has preventing you from joining the event.");
                    return false;
                }
            }
            catch 
            {
                PrintToChat(player, "Failed to join due to exception.");
                return false;
            }
            finally
            {
                Pool.FreeUnmanaged(ref allItems);
            }
            return true;
        }

        bool HasProhibitedItems(BasePlayer player, List<Item> items)
        {
            try
            {
                if (config.eventSettings.black_listed_items == null || config.eventSettings.black_listed_items.Count == 0 || items.Count == 0) return false;
                foreach (var item in items)
                {
                    if (config.eventSettings.black_listed_items.Contains(item.info.shortname))
                    {
                        PrintToChat(player, string.Format(lang.GetMessage("BlackListedItem", this, player.UserIDString), item.info.displayName.english));
                        return true;
                    }
                    if (item.contents != null && item.contents.itemList != null && item.contents.itemList.Count > 1)
                    {
                        foreach (var sub_item in item.contents.itemList)
                        {
                            if (config.eventSettings.black_listed_items.Contains(sub_item.info.shortname))
                            {
                                PrintToChat(player, string.Format(lang.GetMessage("BlackListedItemSub", this, player.UserIDString), item.info.displayName.english, sub_item.info.displayName.english));
                                return true;
                            }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        void StorePlayerItems(BasePlayer player, PCDInfo playerData)
        {
            playerData.Items.AddRange(GetItems(player, player.inventory.containerWear));
            playerData.Items.AddRange(GetItems(player, player.inventory.containerMain));
            playerData.Items.AddRange(GetItems(player, player.inventory.containerBelt));
            playerData.StripOnItemRestore = true;
            player.inventory.Strip();
        }

        List<ItemInfo> GetItems(BasePlayer player, ItemContainer container)
        {
            string messageText = $"\n\n------------\nGetItems for {player.displayName} [{player.userID}]\n";
            List<ItemInfo> result = new List<ItemInfo>();
            foreach (var item in container.itemList)
            {
                messageText += $"- Adding item: {item.name ?? item.info.shortname}. Container: {(container == player.inventory.containerWear ? "Worn items" : container == player.inventory.containerBelt ? "Belt items" : container == player.inventory.containerMain ? "Main items" : "Item contents")}\n";
                result.Add(new ItemInfo()
                {
                    shortname = item.info.shortname,
                    position = item.position,
                    container = GetContainer(player, container),
                    amount = item.amount,
                    ammo = item.GetHeldEntity() is BaseProjectile ? (item.GetHeldEntity() as BaseProjectile).primaryMagazine.contents : item.GetHeldEntity() is FlameThrower ? (item.GetHeldEntity() as FlameThrower).ammo : item.GetHeldEntity() is Chainsaw ? (item.GetHeldEntity() as Chainsaw).ammo : 0,
                    ammotype = (item.GetHeldEntity() as BaseProjectile)?.primaryMagazine.ammoType.shortname ?? null,
                    skin = item.skin,
                    condition = item.condition,
                    maxCondition = item.maxCondition,
                    flags = item.flags,
                    instanceData = item.instanceData != null ? new ItemInfo.KeyInfo()
                    {
                        dataInt = item.instanceData.dataInt,
                        blueprintTarget = item.instanceData.blueprintTarget,
                        blueprintAmount = item.instanceData.blueprintAmount,
                        dataFloat = item.instanceData.dataFloat,
                    }
                    : null,
                    name = item.name ?? null,
                    text = item.text ?? null,
                    item_contents = item.contents?.itemList != null ? GetItems(player, item.contents) : null,
                    contentsSlots = item.contents?.capacity ?? 0,
                    ownershipShares = item.ownershipShares != null ? new List<ItemOwnershipShare>(item.ownershipShares) : null
                });
            }
            LogToFile("GetItems", messageText, this, true, true);
            return result;
        }

        ContainerType GetContainer(BasePlayer player, ItemContainer container)
        {
            if (container.uid == player.inventory.containerBelt.uid) return ContainerType.Belt;
            else if (container.uid == player.inventory.containerWear.uid) return ContainerType.Wear;
            else if (container.uid == player.inventory.containerMain.uid) return ContainerType.Main;
            else return ContainerType.None;
        }

        void StorePlayerMods(BasePlayer player, PCDInfo playerData)
        {
            playerData.Modifiers = SaveModifiers(player);
            player.modifiers.RemoveAll();
            player.modifiers.SendChangesToClient();
        }

        void StorePlayerMetabolism(BasePlayer player, PCDInfo playerData)
        {
            playerData.Metabolism = SaveMetabolism(player);
            playerData.Health = player.health;
            player.metabolism.Reset();
            player.metabolism.calories.SetValue(player.metabolism.calories.max);
            player.metabolism.hydration.SetValue(player.metabolism.hydration.max);
            player.metabolism.SendChanges();
            player.SetHealth(player.MaxHealth());
        }

        string SaveModifiers(BasePlayer player)
        {
            ProtoBuf.PlayerModifiers snapshot = player.modifiers.Save(true);
            byte[] bytes = snapshot.ToProtoBytes();
            return Convert.ToBase64String(bytes); // encode byte array to utf8 string
        }

        string SaveMetabolism(BasePlayer player)
        {
            ProtoBuf.PlayerMetabolism snapshot = player.metabolism.Save();
            byte[] bytes = snapshot.ToProtoBytes();
            return Convert.ToBase64String(bytes); // encode byte array to utf8 string
        }

        void Teleport(BasePlayer player, Vector3 loc, bool toLobby)
        {
            if (loc == Vector3.zero) return;
            player.SetParent(null, worldPositionStays: true);
            Player.Teleport(player, loc);
            player.ForceUpdateTriggers();
            player.ClientRPC(RpcTarget.Player("StartLoading_Quick", player), arg1: true);
            player.UpdateNetworkGroup();
            player.SetPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot, true);
            BasePlayer.ResetAntiHack(player, AntiHack.PlayerStates, AntiHack.PlayerNoclipStates, AntiHack.PlayerSpeedhackStates, AntiHack.PlayerFlyhackStates);
            player.PauseFlyHackDetection(3f);
            player.PauseSpeedHackDetection(3f);
            player.ApplyStallProtection(4f);
            if (!toLobby) player.StartSleeping();

            if (player.children != null)
            {
                List<BaseEntity> stuck = Pool.Get<List<BaseEntity>>();
                foreach (var child in player.children)
                {
                    if (child.PrefabName == "assets/prefabs/misc/burlap sack/generic_world.prefab")
                    {
                        var item = child.GetItem();
                        if (item != null)
                        {
                            var heldEntity = item.GetHeldEntity();
                            if ((heldEntity != null && heldEntity is BaseMelee) || item.info.shortname.StartsWith("arrow.") || item.info.shortname.Equals("ammo.nailgun.nails")) stuck.Add(child);

                        }
                    }
                }
                foreach (var child in stuck)
                {
                    child.KillMessage();
                }
                Pool.FreeUnmanaged(ref stuck);
            }
        }

        #region monobehaviour

        Dictionary<BasePlayer, Temperature> TempTracking = new Dictionary<BasePlayer, Temperature>();

        void AddTempRestriction(BasePlayer player)
        {
            Puts("Adding temp restriction.");
            DestroyTempRestriction(player);
            TempTracking.Add(player, player.gameObject.AddComponent<Temperature>());
        }

        void DestroyTempRestriction(BasePlayer player, bool doRemove = true)
        {
            Temperature temp;
            if (TempTracking.TryGetValue(player, out temp))
            {
                GameObject.DestroyImmediate(temp);
                if (doRemove) TempTracking.Remove(player);
            }
        }

        public class Temperature : MonoBehaviour
        {
            public SurvivalArena Instance;
            private BasePlayer player;
            private float adjustmentDelay;

            private void Awake()
            {
                player = GetComponent<BasePlayer>();
                adjustmentDelay = Time.time + 1f;
            }

            public void FixedUpdate()
            {
                if (player == null) return;
                if (adjustmentDelay < Time.time)
                {
                    adjustmentDelay = Time.time + 1f;
                    DoChange();
                }
            }

            public void DoChange()
            {
                if (player == null || !player.IsConnected || !player.IsAlive()) return;
                if (player.metabolism.temperature.value < 20) player.metabolism.temperature.value = 25;
                else if (player.metabolism.temperature.value > 30) player.metabolism.temperature.value = 25;
            }

            private void OnDestroy()
            {
                enabled = false;
                CancelInvoke();
            }
        }

        #endregion

        #endregion

        #region Restore data
        void HandleStatsRestore(BasePlayer player, PCDInfo playerData)
        {
            if (playerData == null || !player.IsConnected || player.IsDead()) return;
            if (player.IsWounded()) player.RecoverFromWounded();
            LoadMetabolism(player, playerData);
            LoadModifiers(player, playerData);
            if (playerData.Health > 0)
            {
                player.health = playerData.Health;
                playerData.Health = 0;
            }
            
            TeleportBack(player, playerData);
            DestroyTempRestriction(player);

            if (!config.eventSettings.allow_teams)
            {
                HandleTeam(player, false);
            }
        }

        void HandleItemRestore(BasePlayer player, PCDInfo playerData, bool force)
        {
            if (player.IsDead()) return;
            if (playerData == null) return;

            if (playerData.StripOnItemRestore)
            {
                Puts($"stripping {player.displayName} from HandleItemRestore");
                player.inventory.Strip();
                playerData.StripOnItemRestore = false;
            }

            if (playerData.Items.Count > 0)
            {
                if (RestoreItems(player, playerData, force)) Interface.Oxide.CallHook("OnPlayerItemsRestored", player);
            }

            if (playerData.Items.Count == 0 && playerData.rewards_remaining <= 0 && !playerData.GrantedNightVision)
            {
                pcdData.pEntity.Remove(player.userID);
            }
        }

        void LoadMetabolism(BasePlayer player, PCDInfo playerData)
        {
            if (string.IsNullOrEmpty(playerData.Metabolism)) return;
            byte[] bytes = Convert.FromBase64String(playerData.Metabolism);
            ProtoBuf.PlayerMetabolism snapshot = ProtoBuf.PlayerMetabolism.Deserialize(bytes);
            player.metabolism.Load(snapshot);
            playerData.Metabolism = null;
        }

        void LoadModifiers(BasePlayer player, PCDInfo playerData)
        {
            if (string.IsNullOrEmpty(playerData.Modifiers)) return;
            byte[] bytes = Convert.FromBase64String(playerData.Modifiers);
            ProtoBuf.PlayerModifiers snapshot = ProtoBuf.PlayerModifiers.Deserialize(bytes);
            player.modifiers.Load(snapshot, true);
            playerData.Modifiers = null;
        }

        void TeleportBack(BasePlayer player, PCDInfo playerData)
        {
            if (playerData.Location == Vector3.zero) return;
            Teleport(player, playerData.Location, false);
            player.State.unHostileTimestamp = Network.TimeEx.currentTimestamp;
            player.MarkHostileFor(0);

            playerData.Location = Vector3.zero;            
        }

        bool RestoreItems(BasePlayer player, PCDInfo playerData, bool force)
        {
            string messageText = $"\n\n------------\nRestoreItems for {player.displayName} [{player.userID}]\n";
            try
            {
                if (player.IsDead() || !player.IsConnected)
                {
                    messageText += "- Player is not connected.\n";
                    return false;
                }
                messageText += "- Player is alive.\n";
                if (playerData.Items == null || playerData.Items.Count == 0)
                {
                    messageText += "- Player items are null or count is 0.\n";
                    return true;
                }
                messageText += "- Has items to restore.\n";
                foreach (var item in playerData.Items)
                {
                    try
                    {
                        if (item.amount < 1) continue;
                        messageText += $"- - Restoring: {item.name ?? item.shortname} x {item.amount}\n";
                        if (item.container == ContainerType.Main) GetRestoreItem(player, player.inventory.containerMain, item, force);
                        else if (item.container == ContainerType.Wear) GetRestoreItem(player, player.inventory.containerWear, item, force);
                        else if (item.container == ContainerType.Belt) GetRestoreItem(player, player.inventory.containerBelt, item, force);
                    }
                    catch
                    {
                        messageText += $"- - Failed to restore: {item?.name ?? item?.shortname ?? "Item null"} x {item?.amount}\n";
                    }
                }
                messageText += "- Finished restoring items.\n";
                player.inventory.containerWear.MarkDirty();
                player.inventory.containerMain.MarkDirty();
                player.inventory.containerBelt.MarkDirty();
                playerData.Items.Clear();
                return true;
            }
            finally
            {
                LogToFile("RestoreItems", messageText, this, true, true);
            }
        }

        Item GetRestoreItem(BasePlayer player, ItemContainer container, ItemInfo savedItem, bool forceMove)
        {
            var item = ItemManager.CreateByName(savedItem.shortname, savedItem.amount, savedItem.skin);
            if (savedItem.name != null) item.name = savedItem.name;
            if (savedItem.text != null) item.text = savedItem.text;
            item.condition = savedItem.condition;
            item.maxCondition = savedItem.maxCondition;
            if (savedItem.contentsSlots > 0 && item.info.isWearable && item.info.TryGetComponent<ItemModContainerArmorSlot>(out var aSlots))
            {
                aSlots.CreateAtCapacity(savedItem.contentsSlots, item);
            }
            BaseEntity heldEntity = item.GetHeldEntity();
            item.flags = savedItem.flags;
            if (savedItem.instanceData != null)
            {
                // Don't discard the instanceData the item was created with - food items are seeded with their spoil time in OnItemCreated.
                if (item.instanceData == null)
                {
                    item.instanceData = new ProtoBuf.Item.InstanceData();
                    item.instanceData.ShouldPool = false;
                }
                item.instanceData.dataInt = savedItem.instanceData.dataInt;
                item.instanceData.blueprintTarget = savedItem.instanceData.blueprintTarget;
                item.instanceData.blueprintAmount = savedItem.instanceData.blueprintAmount;
                if (savedItem.instanceData.dataFloat > 0f) item.instanceData.dataFloat = savedItem.instanceData.dataFloat;
            }
            if (savedItem.item_contents != null && savedItem.item_contents.Count > 0)
            {
                if (item.contents == null)
                {
                    item.contents = new ItemContainer();
                    item.contents.ServerInitialize(null, savedItem.item_contents.Count);
                    item.contents.GiveUID();
                    item.contents.parent = item;
                }
                foreach (var _item in savedItem.item_contents)
                {
                    GetRestoreItem(player, item.contents, _item, forceMove);
                }
            }
            if (savedItem.ownershipShares != null && savedItem.ownershipShares.Count > 0)
            {
                if (item.ownershipShares == null) item.ownershipShares = Pool.Get<List<ItemOwnershipShare>>();
                item.ownershipShares.Clear();
                item.ownershipShares.AddRange(savedItem.ownershipShares);
            }

            BaseProjectile weapon = heldEntity as BaseProjectile;
            if (weapon != null)
            {
                weapon.DelayedModsChanged();

                if (!string.IsNullOrEmpty(savedItem.ammotype))
                    weapon.primaryMagazine.ammoType = ItemManager.FindItemDefinition(savedItem.ammotype);
                weapon.primaryMagazine.contents = savedItem.ammo;
            }
            else if (savedItem.ammo > 0)
            {
                FlameThrower flameThrower = heldEntity as FlameThrower;
                if (flameThrower != null) flameThrower.ammo = savedItem.ammo;
                else
                {
                    Chainsaw chainsaw = heldEntity as Chainsaw;
                    if (chainsaw != null) chainsaw.ammo = savedItem.ammo;
                }
            }
            item.MarkDirty();
            if (!item.MoveToContainer(container, savedItem.position))
                player.GiveItem(item);

            return item;
        }

        void RestoreChatCMD(BasePlayer player) => HandleFromCMD(player);
        void RestoreConsoleCMD(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            HandleFromCMD(player);
        }

        void HandleFromCMD(BasePlayer player)
        {
            var playerData = GetPlayerData(player.userID, false);
            if (playerData == null) return;
            if (Participants.ContainsKey(player.userID) || IsAtAnyEvent(player))
            {
                PrintToChat(player, lang.GetMessage("Prefix", this, player.UserIDString) + lang.GetMessage("NoRestoreAtEvent", this, player.UserIDString));
                return;
            }
            bool needsTP = playerData.Location != Vector3.zero;
            HandleStatsRestore(player, playerData);
            if (!needsTP)
            {
                playerData.StripOnItemRestore = false;
                timer.Once(1f, () => HandleItemRestore(player, playerData, false));
            }
        }

        bool IsAtAnyEvent(BasePlayer player)
        {
            if (Interface.CallHook("isEventPlayer", player) != null) return true;
            return EventHelper != null && EventHelper.IsLoaded && Convert.ToBoolean(EventHelper.Call("EMAtEvent", player.userID.Get()));
        }

        #endregion

        #region Team management

        // ulong = TeamID.
        Dictionary<ulong, TeamData> TeamCache = new Dictionary<ulong, TeamData>();

        public class TeamData
        {
            public ulong leader;
            public List<ulong> members;
            public string name;
            public ulong teamID;

            public TeamData(ulong leader, List<ulong> members, ulong teamID, string name)
            {
                this.leader = leader;
                this.members = members;
                this.teamID = teamID;
                this.name = name;
            }

            public RelationshipManager.PlayerTeam GetTeam() => RelationshipManager.ServerInstance.FindTeam(teamID);
        }

        void HandleEndGameRestoration()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                HandleTeam(player, false);
            }
        }

        void HandleTeam(BasePlayer player, bool joining)
        {
            if (joining) StoreTeamData(player);
            else RestoreTeamData(player);
        }

        TeamData FindExistingStoredTeam(BasePlayer player)
        {
            foreach (var kvp in TeamCache)
            {
                if (kvp.Value.members.Contains(player.userID))
                {
                    return kvp.Value;
                }
            }
            return null;
        }

        void StoreTeamData(BasePlayer player)
        {
            if (player.Team == null) return;

            TeamData teamData = FindExistingStoredTeam(player);
            if (teamData == null)
            {
                if (TeamCache.TryGetValue(player.Team.teamID, out teamData)) teamData.members.Add(player.userID);
                else
                {
                    List<ulong> members = Pool.Get<List<ulong>>();
                    members.AddRange(player.Team.members);
                    TeamCache.Add(player.Team.teamID, teamData = new TeamData(player.Team.teamLeader, members, player.Team.teamID, player.Team.teamName));
                }
            }

            player.Team.RemovePlayer(player.userID);
        }

        void RestoreTeamData(BasePlayer player)
        {
            var teamData = FindExistingStoredTeam(player);
            if (teamData == null) return;
            try
            {
                var team = teamData.GetTeam();
                if (player.Team != null)
                {
                    if (team != null && player.Team == team) return;
                    player.Team.RemovePlayer(player.userID);
                }
                if (team == null || team.members.Count == 0)
                {
                    RelationshipManager.PlayerTeam playerTeam = RelationshipManager.ServerInstance.CreateTeam();
                    playerTeam.teamLeader = player.userID;
                    playerTeam.AddPlayer(player);
                    Facepunch.Rust.Analytics.Azure.OnTeamChanged("created", playerTeam.teamID, player.userID, player.userID, playerTeam.members);

                    teamData.teamID = playerTeam.teamID;
                    return;
                }
                team.AddPlayer(player);
                if (teamData.leader == player.userID) team.SetTeamLeader(player.userID);
            }
            finally
            {
                ClearTeamCache();
            }
        }

        void SetLeaders()
        {
            foreach (var kvp in TeamCache)
            {
                var team = kvp.Value.GetTeam();
                if (team == null) continue;
                if (team.teamLeader != kvp.Value.leader && kvp.Value.members.Contains(kvp.Value.leader)) team.SetTeamLeader(kvp.Value.leader);
            }
        }

        void ClearTeamCache()
        {
            var remove = Pool.Get<List<ulong>>();
            try
            {
                foreach (var kvp in TeamCache)
                {
                    if (HasPendingTeamMember(kvp.Value)) continue;
                    remove.Add(kvp.Key);
                }
                foreach (var key in remove)
                {
                    var teamData = TeamCache[key];
                    if (teamData.members != null) Pool.FreeUnmanaged(ref teamData.members);
                    TeamCache.Remove(key);
                }
            }
            finally
            {
                Pool.FreeUnmanaged(ref remove);
            }
        }

        bool HasPendingTeamMember(TeamData teamData)
        {
            if (teamData.members == null) return false;
            foreach (var id in teamData.members)
            {
                if (Participants.ContainsKey(id)) return true;
                if (pcdData.pEntity.TryGetValue(id, out var playerData) && playerData.Location != Vector3.zero) return true;
            }
            return false;
        }

        object OnTeamCreate(BasePlayer player)
        {
            var playerData = GetPlayerData(player.userID, false);
            if (playerData == null) return null;
            if (Participants.ContainsKey(player.userID))
            {
                PrintToChat(player, lang.GetMessage("PreventTeamCreation", this, player.UserIDString));
                return true;
            }

            return null;
        }


        #endregion

        #region EventHelper stuff

        void EMStartNextEvent(string eventName)
        {
            if (eventName != this.Name || IsRunning) return;
            if (IsDespawning)
            {
                timer.Once(1f, () => EMStartNextEvent(eventName));
                return;
            }
            StartEvent(GetRandomArena());
        }

        void OnPluginLoaded(Plugin plugin)
        {
            if (plugin.Name == "EventHelper")
            {
                NextTick(() => { if (IsLoaded) EventHelper?.Call("EMCreateEvent", this.Name, true, false, false, false, false, false, Vector3.zero); });
            }
            if (plugin.Name == "MultiEvents")
            {
                RegisterMultiEvent();
            }
        }

        void EMEndGame(string eventName)
        {
            if (eventName == this.Name)
            {
                EndEvent();
            }
        }

        #endregion

        #endregion

        #region Score board

        List<ScoreInfo> Scores = Pool.Get<List<ScoreInfo>>();
        void UpdateScoreBoard()
        {
            Scores.Clear();
            Scores.AddRange(pcdData.scoreData.Values.OrderByDescending(x => x.wins).ThenByDescending(x => x.kills).ThenByDescending(x => x.games));
        }

        void SendBoard(BasePlayer player)
        {
            SurvivalScoreBackground(player);
            SurvivalScoreBoard(player);
        }

        private void SurvivalScoreBackground(BasePlayer player)
        {
            var container = new CuiElementContainer();
            container.Add(new CuiButton
            {
                Button = { Color = "0 0 0 0.9803922", Command = "closesurvivalarenascoreboard" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "0.332 0.008", OffsetMax = "-0.338 0.008" }
            }, "Overlay", "SurvivalScoreBackground");

            CuiHelper.DestroyUi(player, "SurvivalScoreBackground");
            CuiHelper.AddUi(player, container);
        }

        [ConsoleCommand("closesurvivalarenascoreboard")]
        void CloseScore(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            CuiHelper.DestroyUi(player, "SurvivalScoreBoard");
            CuiHelper.DestroyUi(player, "SurvivalScoreBackground");
        }

        private void SurvivalScoreBoard(BasePlayer player)
        {
            var container = new CuiElementContainer();
            container.Add(new CuiElement
            {
                Name = "SurvivalScoreBoard",
                Parent = "Overlay",
                Components =
                {
                    new CuiNeedsCursorComponent(),
                    new CuiImageComponent{ Color = "0.1792453 0.1792453 0.1792453 0" },
                    new CuiRectTransformComponent{ AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = $"-225 {Math.Max(-200, 200 - (Scores.Count * 20))}", OffsetMax = "225 200" }
                }
            });

            container.Add(new CuiElement
            {
                Name = "TitlePanel",
                Parent = "SurvivalScoreBoard",
                Components =
                {
                    new CuiNeedsCursorComponent(),
                    new CuiImageComponent{ Color = "0.5471698 0.5445592 0.5394269 1" },
                    new CuiRectTransformComponent{ AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = "-225 -40", OffsetMax = "225 0" }
                }
            });

            container.Add(new CuiElement
            {
                Name = "PlayerTitle",
                Parent = "SurvivalScoreBoard",
                Components = {
                    new CuiTextComponent { Text = "PLAYER", Font = "robotocondensed-bold.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    new CuiOutlineComponent { Color = "0 0 0 0.5", Distance = "1 -1" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = "-225 -40", OffsetMax = "-45 0" }
                }
            });

            var miny = Math.Max(-400, -40 - (Scores.Count * 20));
            container.Add(new CuiElement
            {
                Name = "PlayerListPanel",
                Parent = "SurvivalScoreBoard",
                Components =
                {
                    new CuiNeedsCursorComponent(),
                    new CuiImageComponent{ Color = "0.3396226 0.3396226 0.3396226 1" },
                    new CuiRectTransformComponent{ AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = $"-225 {miny}", OffsetMax = "-45 -40" }
                }
            });

            container.Add(new CuiElement
            {
                Name = "WinsTitle",
                Parent = "SurvivalScoreBoard",
                Components = {
                    new CuiTextComponent { Text = "WINS", Font = "robotocondensed-bold.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    new CuiOutlineComponent { Color = "0 0 0 0.5", Distance = "1 -1" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = "-45 -40", OffsetMax = "45 0" }
                }
            });

            container.Add(new CuiElement
            {
                Name = "WinsListPanel",
                Parent = "SurvivalScoreBoard",
                Components =
                {
                    new CuiNeedsCursorComponent(),
                    new CuiImageComponent{ Color = "0.2264151 0.2264151 0.2264151 1" },
                    new CuiRectTransformComponent{ AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = $"-45 {miny}", OffsetMax = "45 -40" }
                }
            });

            container.Add(new CuiElement
            {
                Name = "KillsTitle",
                Parent = "SurvivalScoreBoard",
                Components = {
                    new CuiTextComponent { Text = "KILLS", Font = "robotocondensed-bold.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    new CuiOutlineComponent { Color = "0 0 0 0.5", Distance = "1 -1" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = "45 -40", OffsetMax = "135 0" }
                }
            });

            container.Add(new CuiElement
            {
                Name = "KillsListPanel",
                Parent = "SurvivalScoreBoard",
                Components =
                {
                    new CuiNeedsCursorComponent(),
                    new CuiImageComponent{ Color = "0.3411765 0.3411765 0.3411765 1" },
                    new CuiRectTransformComponent{ AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = $"45 {miny}", OffsetMax = "135 -40" }
                }
            });

            container.Add(new CuiElement
            {
                Name = "GamesTitle",
                Parent = "SurvivalScoreBoard",
                Components = {
                    new CuiTextComponent { Text = "GAMES", Font = "robotocondensed-bold.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    new CuiOutlineComponent { Color = "0 0 0 0.5", Distance = "1 -1" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = "135 -40", OffsetMax = "225 0" }
                }
            });

            container.Add(new CuiElement
            {
                Name = "GamesListPanel",
                Parent = "SurvivalScoreBoard",
                Components =
                {
                    new CuiNeedsCursorComponent(),
                    new CuiImageComponent{ Color = "0.2264151 0.2264151 0.2264151 1" },
                    new CuiRectTransformComponent{ AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = $"135 {miny}", OffsetMax = "225 -40" }
                }
            });

            container.Add(new CuiElement
            {
                Name = "SAScoreScrollPanel",
                Parent = "SurvivalScoreBoard",
                Components =
                {
                    new CuiNeedsCursorComponent(),
                    new CuiImageComponent{ Color = "1 1 1 0" },
                    new CuiRectTransformComponent{ AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = $"-225 {miny}", OffsetMax = "225 -40" }
                }
            });

            if (Scores.Count > 18)
            {
                container.Add(new CuiElement
                {
                    Name = "SurvivalArenaScrollPanel",
                    Parent = "SAScoreScrollPanel",
                    Components = {
                    new CuiScrollViewComponent {
                        MovementType = UnityEngine.UI.ScrollRect.MovementType.Elastic,
                        Vertical = true,
                        Inertia = true,
                        Horizontal = false,
                        Elasticity = 0.25f,
                        DecelerationRate = 0.3f,
                        ScrollSensitivity = 24f,
                        ContentTransform = new CuiRectTransform { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 " + Scores.Count * 20 * -1, OffsetMax = "0 0" },
                        VerticalScrollbar = new CuiScrollbar() { Size = 1f, AutoHide = true }, // Remove this to remove the scroll bar and just have it scrollable with mwheel
                    },
                    new CuiNeedsCursorComponent()
                }
                });
                container.Add(new CuiPanel()
                {
                    Image = { Color = "0 0 0 0" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                }, "SurvivalArenaScrollPanel", "SurvivalArenaScrollPanelInner");
            }

            var parent = Scores.Count > 18 ? "SurvivalArenaScrollPanel" : "SAScoreScrollPanel";
            var count = 0;
            foreach (var score in Scores)
            {
                container.Add(new CuiElement
                {
                    Name = $"ScoreEntry_{count}",
                    Parent = parent,
                    Components =
                {
                    new CuiNeedsCursorComponent(),
                    new CuiImageComponent{ Color = count%2 == 0 ? "0 0 0 0.2509804" : "0.8867924 0.8867924 0.8867924 0.2509804" },
                    new CuiRectTransformComponent{ AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = $"-225 {-20 - (count * 20)}", OffsetMax = $"225 {0 - (count * 20)}" }
                }
                });

                container.Add(new CuiElement
                {
                    Name = "Name",
                    Parent = $"ScoreEntry_{count}",
                    Components = {
                    new CuiTextComponent { Text = score.name, Font = "robotocondensed-regular.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    new CuiOutlineComponent { Color = "0 0 0 0.5", Distance = "1 -1" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = "-225 -20", OffsetMax = "-45 0" }
                }
                });

                container.Add(new CuiElement
                {
                    Name = "Wins",
                    Parent = $"ScoreEntry_{count}",
                    Components = {
                    new CuiTextComponent { Text = score.wins.ToString(), Font = "robotocondensed-regular.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    new CuiOutlineComponent { Color = "0 0 0 0.5", Distance = "1 -1" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = "-45 -20", OffsetMax = "45 0" }
                }
                });

                container.Add(new CuiElement
                {
                    Name = "Kills",
                    Parent = $"ScoreEntry_{count}",
                    Components = {
                    new CuiTextComponent { Text = score.kills.ToString(), Font = "robotocondensed-regular.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },

                    new CuiOutlineComponent { Color = "0 0 0 0.5", Distance = "1 -1" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = "45 -20", OffsetMax = "135 0" }
                }
                });

                container.Add(new CuiElement
                {
                    Name = "GamesPlayed",
                    Parent = $"ScoreEntry_{count}",
                    Components = {
                    new CuiTextComponent { Text = score.games.ToString(), Font = "robotocondensed-regular.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },

                    new CuiOutlineComponent { Color = "0 0 0 0.5", Distance = "1 -1" },
                    new CuiRectTransformComponent { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = "135 -20", OffsetMax = "225 0" }
                }
                });

                count++;
            }

            CuiHelper.DestroyUi(player, "SurvivalScoreBoard");
            CuiHelper.AddUi(player, container);
        }



        #endregion

        #region Death Feed

        DeathFeedHandler DeathFeed = new DeathFeedHandler();
        Dictionary<string, ItemDefinition> WeaponPrefabs = new Dictionary<string, ItemDefinition>();
        public void SetupDeathFeed()
        {
            foreach (var def in ItemManager.GetItemDefinitions())
            {
                var mod = def.GetComponent<ItemModEntity>();
                if (mod == null)
                {
                    continue;
                }

                WeaponPrefabs[mod.entityPrefab.resourcePath] = def;
            }

            DeathFeed.Setup();
        }

        public class DeathFeedHandler
        {
            public List<KillInfo> killFeed = new List<KillInfo>();
            public Timer checkTimer;

            public void Setup()
            {
                checkTimer = Instance.timer.Every(1, () =>
                {
                    if (RemoveOld())
                        SendUI();
                });
            }

            public void Unload()
            {
                foreach (var player in BasePlayer.activePlayerList)
                    CuiHelper.DestroyUi(player, "DeathFeed");
            }

            public bool RemoveOld()
            {
                bool Changed = false;
                for (int i = killFeed.Count - 1; i >= 0; i--)
                {
                    var killData = killFeed[i];
                    if (killData.expires < Time.time)
                    {
                        killFeed.RemoveAt(i);
                        Changed = true;
                    }
                }
                if (killFeed.Count > Instance.config.deathFeedSettings.MaxKillsDisplayed)
                {
                    var remove = killFeed.Count - Instance.config.deathFeedSettings.MaxKillsDisplayed;
                    for (int i = 0; i < remove; i++)
                    {
                        killFeed.RemoveAt(i);
                        Changed = true;
                    }
                }

                return Changed;
            }
            public void AddKill(BasePlayer victim, HitInfo info)
            {
                if (victim == null || info == null || victim.IsNpc || !victim.userID.IsSteamId()) return;
                (string, ulong) attacker = info.InitiatorPlayer != null && info.InitiatorPlayer.userID != victim.userID ? (info.InitiatorPlayer.displayName, info.InitiatorPlayer.userID) : (null, 0);

                int itemID = 0;
                string icon = null;
                if (info.WeaponPrefab != null && Instance.WeaponPrefabs.TryGetValue(info.WeaponPrefab.PrefabName, out var def))
                {
                    itemID = def.itemid;
                }
                else
                {
                    switch (info.damageTypes.GetMajorityDamageType())
                    {
                        case Rust.DamageType.Fall: icon = "assets/icons/fall.png"; break;
                        case Rust.DamageType.Drowned: icon = "assets/icons/drowning.png"; break;
                        case Rust.DamageType.Heat: icon = "assets/icons/light_campfire.png"; break;
                        case Rust.DamageType.Suicide: icon = "assets/icons/poison.png"; break;
                        case Rust.DamageType.ColdExposure:
                        case Rust.DamageType.Cold: icon = "assets/icons/freezing.png"; break;
                        case Rust.DamageType.Bite: icon = "assets/icons/bite.png"; break;
                        case Rust.DamageType.Radiation: icon = "assets/icons/radiation.png"; break;
                        default:
                            icon = "assets/icons/traps.png"; break;

                    }
                }

                killFeed.Add(new KillInfo((victim.displayName, victim.userID), attacker, itemID, Time.time + Instance.config.deathFeedSettings.Duration, icon));

                RemoveOld();
                SendUI();
            }

            public void SendUI()
            {
                var ui = DeathFeed();
                foreach (var player in Instance.Participants.Values)
                {
                    CuiHelper.AddUi(player, ui);
                }
            }

            private CuiElementContainer DeathFeed()
            {
                var container = new CuiElementContainer();
                container.Add(new CuiElement
                {
                    Name = "DeathFeed",
                    Parent = "Overlay",
                    DestroyUi = "DeathFeed",
                    Components =
                {
                    new CuiImageComponent{ Color = "1 1 1 0" },
                    new CuiRectTransformComponent{ AnchorMin = "1 1", AnchorMax = "1 1", OffsetMin = "-250 -40", OffsetMax = "0 -20" }
                }
                });

                var count = 0;
                foreach (var kill in killFeed)
                {
                    container.Add(new CuiElement
                    {
                        Name = "DeathFeedElement",
                        Parent = "DeathFeed",
                        Components = {
                            new CuiImageComponent{ Color = "1 1 1 0" },
                            new CuiRectTransformComponent{ AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = $"-125 {-20 - (count * 20)}", OffsetMax = $"125 {0 - (count * 20)}" },
                        }
                    });

                    container.Add(new CuiElement
                    {
                        Name = "WeaponImg",
                        Parent = "DeathFeedElement",
                        Components = {
                            kill.itemID != 0 ?
                            new CuiImageComponent { Color = "1 1 1 1", ItemId = kill.itemID } :
                            !string.IsNullOrEmpty(kill.icon) ?
                            new CuiImageComponent { Color = "1 1 1 1", Sprite = kill.icon } :
                            new CuiImageComponent { Color = "1 1 1 1", Sprite = "assets/icons/blunt.png" },
                            new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-10 -10", OffsetMax = "10 10" }
                        }
                    });

                    if (!string.IsNullOrEmpty(kill.attacker.Item1))
                    {
                        container.Add(new CuiElement
                        {
                            Name = "Killer",
                            Parent = "DeathFeedElement",
                            Components = {
                            new CuiTextComponent { Text = Instance.ColourName(kill.attacker.Item1), Font = "robotocondensed-regular.ttf", FontSize = 10, Align = TextAnchor.MiddleRight, Color = "1 1 1 1" },
                            new CuiOutlineComponent { Color = "0 0 0 0.5", Distance = "1 -1" },
                            new CuiRectTransformComponent { AnchorMin = "0 0.5", AnchorMax = "0 0.5", OffsetMin = "0 -10", OffsetMax = "110 10" }
                        }
                        });
                    }


                    container.Add(new CuiElement
                    {
                        Name = "Victim",
                        Parent = "DeathFeedElement",
                        Components = {
                        new CuiTextComponent { Text = Instance.ColourName(kill.victim.Item1), Font = "robotocondensed-regular.ttf", FontSize = 10, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                        new CuiOutlineComponent { Color = "0 0 0 0.5", Distance = "1 -1" },
                        new CuiRectTransformComponent { AnchorMin = "1 0.5", AnchorMax = "1 0.5", OffsetMin = "-110 -10", OffsetMax = "0 10" }
                    }
                    });
                    count++;
                }

                return container;
            }
        }

        string ColourName(string text)
        {
            return $"<color=#{config.deathFeedSettings.DeathFeedCol}>{text}</color>";
        }

        public struct KillInfo
        {
            public (string, ulong) attacker;
            public (string, ulong) victim;
            public int itemID;
            public float expires;
            public string icon;
            public KillInfo((string, ulong) victim, (string, ulong) attacker, int itemID, float expires, string icon)
            {
                this.victim = victim;
                this.attacker = attacker;
                this.itemID = itemID;
                this.expires = expires;
                this.icon = icon;
            }
        }

        #endregion

        #region Harmony

        [AutoPatch]
        [HarmonyPatch(typeof(Planner), "CanUpgradeInPlace")]
        static class Patch_Planner_CanUpgradeInPlace
        {
            static bool Prefix(BasePlayer player, BaseEntity replaced, ref bool __result)
            {
                if (Instance != null && Instance.ShouldBlockUpgradeInPlace(player, replaced))
                {
                    Construction.lastPlacementError = ConstructionErrors.NoPermission;
                    __result = false;
                    return false;
                }
                return true;
            }
        }

        [AutoPatch]
        [HarmonyPatch(typeof(AntiHack), "ShouldIgnore")]
        static class Patch_AntiHack_ShouldIgnore
        {
            static bool Prefix(BasePlayer ply, ref bool __result)
            {
                if (Instance?.Participants.ContainsKey(ply.userID) == true)
                {
                    __result = true;
                    return false;
                }
                return true;
            }
        }

        #endregion
    }
}
