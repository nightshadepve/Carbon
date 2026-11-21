using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Facepunch;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using UnityEngine.UI;

namespace Oxide.Plugins
{
    [Info("NightshadeShop", "Nightshade", "1.7.1")]
    [Description("Seasonal-themed Server Rewards shop with in-game item management, an Admin Shop resource exchange, and claimable kits")]
    public class NightshadeShop : RustPlugin
    {
        #region Fields

        private const string PluginVersion = "1.7.1";

        [PluginReference] private Plugin ServerRewards, RaidableBases, DungeonEvents, ImageLibrary;

        private static readonly string[] RbDifficulties = { "EASY", "MEDIUM", "HARD", "EXPERT", "NIGHTMARE" };
        private const int PickPerRow = 10, PickRows = 4, PickPerPage = PickPerRow * PickRows;
        private const int TradeTiles = -2, TradeOther = -1;
        private const int SkinIconItemId = 1394042569; // carrier item used to render workshop skin icons

        private PurchaseData purchases;

        // Data files live in oxide/data/Nightshade/NightshadeShop/ (carbon/data/Nightshade/NightshadeShop/ on Carbon).
        private const string DataShop = "Nightshade/NightshadeShop/Shop";
        private const string DataPurchases = "Nightshade/NightshadeShop/Purchases";

        private List<ItemDefinition> pickList;
        private bool unloading;
        private bool serverReady;
        private const string DataRefunds = "Nightshade/NightshadeShop/Refunds";
        private const string DataKits = "Nightshade/NightshadeShop/Kits";
        private const string VipDailyId = "vip-daily";
        private const string VipxDailyId = "vipx-daily";
        private const string VipFourId = "vip-4day";
        private const string VipxFourId = "vipx-4day";
        private KitFile kitFile;
        private readonly List<PendingRefund> pendingRefunds = new List<PendingRefund>();

        private class PendingRefund
        {
            public ulong UserId;
            public string UserName;
            public int Amount;
            public string Label;
            public string Type;
            public string EventArg;
            public ShopItem Listing;
            public DateTime Deadline;
            public bool RefundDue;
            [JsonIgnore] public Timer Timer;
        }

        private const string PermAdmin = "nightshadeshop.admin";
        private const string PermUse = "nightshadeshop.use";
        // Granted to the default_unlocked group by NightshadeDefaultUnlocked. Shop categories use this same permission.
        private const string PermWipeLock = "nightshadeshop.wipelock";
        private const string PermVipPerk = "nightshadeshop.vipperk";
        private const string PermVipXPerk = "nightshadeshop.vipxperk";
        private const string DailyKitPng = "VIP_Daily.png";
        private const string FourDayKitPng = "VIP_4Day.png";
        private readonly HashSet<string> localPngQueued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> localPngWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool imageLibraryWarned;

        private const string UiMain = "NShop.Main";
        private const string UiScrollTest = "NShop.ScrollTest";
        private const string UiFrame = "NShop.Frame";
        private const string UiModal = "NShop.Modal";
        private const string UiCmd = "nshop.ui";

        private const string FontBold = "robotocondensed-bold.ttf";
        private const string FontReg = "robotocondensed-regular.ttf";

        private const string FxBuy = "assets/prefabs/misc/casino/slotmachine/effects/payout.prefab";
        private const string FxTrade = "assets/prefabs/misc/easter/painted eggs/effects/eggpickup.prefab";
        private const string FxDeny = "assets/prefabs/locks/keypad/effects/lock.code.denied.prefab";

        private Configuration config;
        private StoredData data;

        private readonly Dictionary<ulong, UiState> states = new Dictionary<ulong, UiState>();
        private readonly Dictionary<string, string> colorCache = new Dictionary<string, string>();

        private class UiState
        {
            public int Tab;              // 0 = Shop, 1 = Events, 2 = Admin Shop (exchange)
            public int Cat;
            public bool Admin;
            public int Mult = 1;
            public int EditItem;
            public int EditTrade;
            public string Status = "";
            public bool StatusBad;
            public float LastClick;
            public string LastCommand = "";
            public string Confirm;
            public float ConfirmAt;

            // Item picklist
            public string PickTarget;     // "new", "item:<id>", "icon:<id>", "tin:<id>", "tout:<id>"
            public string PickSearch = "";
            public int PickCat = -1;      // -1 = all, otherwise (int)ItemCategory
            public int PickPage;

            // Event menu
            public bool EventMenu;

            public bool Open;
            public int BuyItem;
            public int BuyQty = 1;
            public string BuyToken;
            public bool DescriptionEdit;
            public float LastQtyDraw;

            public int TradeCat = TradeTiles;
            public int TradeSel;                // trade row with a pending quantity
            public string TradeQty = "1";       // "1", "10" or "max"   // Admin Shop: -2 = category tiles, -1 = uncategorized, >= 0 category index

            public string EditKitId;
            public string KitShort = "";
            public string KitAmt = "1";
            public string KitSkin = "0";
        }

        #endregion

        #region Config

        private class Configuration
        {
            [JsonProperty("Chat Commands", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Commands = new List<string> { "shop", "store" };

            [JsonProperty("Require Use Permission (nightshadeshop.use)")]
            public bool RequireUsePerm = false;

            [JsonProperty("Shop Title")]
            public string Title = "NIGHTSHADE MARKET";

            // Theme taglines stay in config for other plugins. The shop header hides them unless this is turned on.
            [JsonProperty("Show Theme Tagline In Shop Header")]
            public bool ShowHeaderTagline = false;

            [JsonProperty("Admin Shop Tab Name")]
            public string ExchangeTabName = "ADMIN SHOP";

            [JsonProperty("Admin Shop Tab Subtitle")]
            public string ExchangeSubtitle = "Trade your resources for Blood and more.";

            [JsonProperty("Admin Shop Tab - Required Permission (empty = everyone)")]
            public string ExchangePerm = "";

            [JsonProperty("Theme Mode (Auto or a theme name)")]
            public string ThemeMode = "Auto";

            [JsonProperty("Default Price For New Items (RP)")]
            public int DefaultPrice = 100;

            [JsonProperty("Maximum Quantity In Purchase Popup")]
            public int MaxPurchaseQuantity = 100;

            [JsonProperty("Imported Item Descriptions", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, string> Descriptions = DefaultDescriptions();

            [JsonProperty("Purchase Quantity Buttons", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<int> Multipliers = new List<int> { 1, 5, 10 };

            [JsonProperty("Log Purchases And Trades To File")]
            public bool Log = true;

            [JsonProperty("UI Click Cooldown (seconds)")]
            public float ClickCooldown = 0.15f;

            [JsonProperty("Block Paid Store/DLC Items Players Don't Own (Facepunch TOS)")]
            public bool RequireOwnership = true;

            [JsonProperty("Event Listings")]
            public EventSettings Events = new EventSettings();

            [JsonProperty("Themes")]
            public List<Theme> Themes = new List<Theme>();
        }

        private class EventSettings
        {
            [JsonProperty("Automatically Add Raidable Bases Category And Difficulties")]
            public bool AutoAddRaidableTab = true;

            [JsonProperty("RaidableBases - Display Prices (actual RP charges are configured in RaidableBases)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, int> RbDisplayPrices = new Dictionary<string, int>
            {
                ["Easy"] = 500, ["Medium"] = 1000, ["Hard"] = 2000,
                ["Expert"] = 3500, ["Nightmare"] = 5000
            };

            [JsonProperty("RaidableBases - Buy Command (plugin charges RP)")]
            public string RbBuyCommand = "buyraid {arg} {steamid}";

            [JsonProperty("RaidableBases - Free Command (shop charges RP)")]
            public string RbFreeCommand = "buyraid {arg} {steamid} free";

            [JsonProperty("DungeonEvents - Buy Command (plugin charges RP)")]
            public string DeBuyCommand = "buydungeon {arg} {steamid}";

            [JsonProperty("DungeonEvents - Free Command (shop charges RP)")]
            public string DeFreeCommand = "spawnfixeddungeon {arg} {steamid}";

            [JsonProperty("Shop-Charged Raids - Refund If RaidableBases Doesn't Confirm Within (seconds)")]
            public float RbRefundTimeout = 180f;

            [JsonProperty("Shop-Charged Dungeons - Refund If No Spawn Confirmation Within (seconds)")]
            public float DeRefundTimeout = 180f;

            [JsonProperty("Default Raidable Base Icon (shortname)")]
            public string RbIcon = "explosive.timed";

            [JsonProperty("Default Dungeon Icon (shortname)")]
            public string DeIcon = "skull.human";
        }

        private class Theme
        {
            public string Name = "";

            [JsonProperty("Tagline")]
            public string Tagline = "";

            [JsonProperty("Icon Item Shortname")]
            public string Icon = "";

            [JsonProperty("Active From (MM-DD)")]
            public string From = "01-01";

            [JsonProperty("Active To (MM-DD)")]
            public string To = "12-31";

            [JsonProperty("Priority (higher wins when dates overlap)")]
            public int Priority;

            [JsonProperty("Colors (#RRGGBB or #RRGGBBAA)")]
            public ThemeColors Colors = new ThemeColors();
        }

        private class ThemeColors
        {
            public string Overlay = "#000000B0";
            public string Background = "#1A1024F2";
            public string Header = "#241634FF";
            public string Panel = "#2A1B3AFF";
            public string Card = "#33224AFF";
            public string Accent = "#8B3FBFFF";
            public string AccentText = "#F2E6FFFF";
            public string Button = "#3E2A55FF";
            public string ButtonText = "#E6D9F2FF";
            public string Text = "#F2EAF7FF";
            public string SubText = "#A898B8FF";
            public string Danger = "#A63A3AFF";
            public string DangerText = "#FFD6D6FF";
        }

        private static Dictionary<string, string> DefaultDescriptions() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
                { "perk_nightshaderecycler", "A personal recycler bound to your will. Because walking to Outpost is for people with patience \u2014 and we don\u2019t do that here. 2 per wipe. Place responsibly" },
                { "casino_cardtable", "Ante up. Bluff hard. Lose everything. A proper Nightshade evening starts with bad bets and worse decisions." },
                { "casino_slotmachine", "Pull the lever and let fate decide. Warning: the machine feeds on hope and pays out in disappointment." },
                { "vipperk_supercard", "Think: 5th Element Multi-Pass, but for puzzle rooms" },
                { "perk_supercard", "Think: 5th Element Multi-Pass, but for puzzle rooms" },
                { "helisignal_easy", "A light tap on Karen\u2019s shoulder. Single patrol heli with manageable pressure. Recommended: Solo / Duo. Good warm-up\u2026 or confidence boost." },
                { "helisignal_medium", "Karen heard you. Increased aggression and damage output. Recommended: Solo / Duo. Bring meds and don\u2019t stand still." },
                { "helisignal_hard", "Karen is now actively upset. Sustained fire, heavier damage, and less room for mistakes. Recommended: Duo / Small Team. Cover matters." },
                { "helisignal_elite", "Karen filed a complaint. High pressure, aggressive targeting, and serious damage output. Recommended: Small Team (2\u20133). You will feel this one." },
                { "multiheli_easy", "You called Karen\u2026 and her friend. Two helis, light coordination. Recommended: Duo. Manageable, but don\u2019t get sloppy." },
                { "multiheli_medium", "Karen brought backup. Dual heli pressure with overlapping fire. Recommended: Duo / Small Team. Positioning starts to matter." },
                { "multiheli_hard", "Karen formed a committee. Constant pressure from multiple angles. Recommended: Small Team (2\u20134). Expect chaos." },
                { "multiheli_elite", "Karen escalated this to management. Multiple helis, relentless fire, and zero breathing room. Recommended: Full Team (3\u20135). Good luck." },
                { "heliwave_normal", "They just keep coming. Sequential heli waves increasing in difficulty. Recommended: Full Team (3\u20135). Ammo check before you start." },
                { "heliwave_hard", "This is a mistake. Continuous waves, escalating chaos, and no real downtime. Recommended: Full Team (4\u20136+). Bring everything. Lose nothing." },
                { "dungeon_easy", "A gentle introduction into the unknown. Light resistance and modest rewards. Recommended: Solo / Duo. Great for testing builds, aim, or bad decisions with minimal consequences." },
                { "dungeon_normal", "Things start to bite back. Expect smarter enemies and more pressure. Recommended: Solo / Duo. Bring meds\u2014you\u2019ll actually use them now." },
                { "dungeon_medium", "You\u2019re no longer the main character here. Stronger enemies and tighter fights. Recommended: Duo / Small Team. Solo possible\u2026 but bold of you." },
                { "dungeon_hard", "Aggressive AI and real pressure. Mistakes get punished fast. Recommended: Small Team (2\u20133). Bring ammo, meds, and at least one competent friend." },
                { "dungeon_nightmare", "This dungeon does not like you. Chaos, high-tier enemies, and relentless pressure. Recommended: Full Team (3\u20135). Solo not recommended unless you enjoy suffering." },
                { "dungeon_impossible", "This was a bad idea. Overwhelming difficulty, nonstop aggression, zero forgiveness. Recommended: Full Team (4\u20136+). Communication required. Survival optional." },
                { "VIP_Market", "The fancy gremlin counter. VIP-only goodies, questionable perks, and just enough privilege to make the loot goblins jealous." },
                { "bradsignal_easy", "Summons an Easy Bradley encounter. Recommended: 1 player. Baby\u2019s first tank tantrum. Still has bullets, still has feelings, still very much would like you deleted." },
                { "bradsignal_medium", "Summons a Medium Bradley encounter. Recommended: 1\u20132 players. For when Easy was too polite and you\u2019re ready for the tank to develop a personality disorder." },
                { "bradsignal_hard", "Summons a Hard Bradley encounter. Recommended: 2\u20133 players. This is where the tank stops being a loot pi\u00F1ata and starts being your therapist\u2019s next invoice." },
                { "bradsignal_elite", "Summons an Elite Bradley encounter. Recommended: 3\u20134 players. Expensive, angry, and built like it knows you have meds on your hotbar." },
                { "bradsignal_expert", "Summons an Expert Bradley encounter. Recommended: 4\u20135 players. The tank has studied your habits, judged your base design, and chosen violence professionally." },
                { "bradsignal_nightmare", "Summons a Nightmare Bradley encounter. Recommended: 5+ players. Not recommended for the fragile, the sleepy, or anyone emotionally attached to their inventory." },
                { "bradwave_normal", "Starts a Normal Bradley wave event. Recommended: 3\u20134 players. One tank was apparently not enough, so now we\u2019re ordering armored problems in bulk." },
                { "bradwave_hard", "Starts a Hard Bradley wave event. Recommended: 5+ players. Multiple tanks, fewer life choices, and a very real chance your team comms become farm animal noises." },
                { "helisignal_expert", "Summons an Expert Patrol Heli. It arrives loud, rude, and fully prepared to turn your confidence into a smoking crater." },
                { "helisignal_nightmare", "Summons a Nightmare Patrol Heli. This is not a fight. This is an airborne HR complaint with rockets." },
                { "multiheli_expert", "Summons multiple Expert helis. Because apparently one flying war blender was not enough seasoning for your evening." },
                { "multiheli_nightmare", "Summons multiple Nightmare helis. Congratulations, you have purchased sky-based emotional damage with a side of rotor noises." },
                { "adv_large_planter_t1", "A reinforced large planter infused with early-stage enhancements. Operates at 80% optimal conditions with no water or light required, offering a reliable and low-maintenance farming upgrade." },
                { "adv_large_planter_t2", "A refined large planter engineered for efficiency. Operates at 100% optimal conditions with no water or light required, and crops remain ripe without decaying\u2014perfect for consistent, hands-off farming." },
                { "adv_large_planter_t3", "A high-tier agricultural planter designed for maximum performance. Operates at 150% optimal conditions with no water or light required, and crops never decay when ripe\u2014ideal for large-scale, high-output farming." },
                { "adv_small_planter_t1", "A compact planter upgraded with basic enhancements. Operates at 80% optimal conditions with no water or light required, making it ideal for simple, low-maintenance setups." },
                { "adv_small_planter_t2", "An optimized small planter built for reliable harvests. Operates at 100% optimal conditions with no water or light required, and crops remain ripe without decaying\u2014perfect for efficient micro-farming." },
                { "adv_small_planter_t3", "A precision-engineered small planter built for peak efficiency. Operates at 150% optimal conditions with no water or light required, and crops never decay when ripe\u2014enabling powerful, fully automated farming setups." },
                { "Animal_Pen", "A cozy little livestock jail for your future nuggets, bacon seeds, and questionable farming ambitions. Place it, feed the chaos, pretend this was a survival game plan." },
                { "pump_jack", "Virtual Quarry Only - oil goblin. Slap this in and let it scream crude oil out of the earth like the map owes you child support." },
                { "mining_quarry", "Virtual Quarry Only - Rock muncher. Fuel it, and watch it chew the planet into resources while you do literally anything else." },
                { "survey_charge", "For Virtual Quarries only. Toss this spicy dirt-sniffer to locate virtual quarry deposits for /VQ. Do not use for in world placeables" },
                { "quarry_static_pumpjack", "This is the physical oil machine you place in the world. (Player Placeable and removeable)" },
                { "quarry_static_mining", "This is the chunky real-world rock vacuum you place on the map. (Player Placeable and removeable)" },
                { "vipperk_iron_axe", "VIP perk iron axe. For when trees looked at you funny and now must be processed into emotionally unavailable lumber." },
                { "vipperk_iron_pickaxe", "VIP perk iron pickaxe. Not fancy. Not subtle. Just a reliable little rock bonker for people with places to be and nodes to disrespect." },
                { "vipperk_axe", "VIP perk axe. Sharper, meaner, and legally distinct from anger management. Trees hate this one weird trick." },
                { "vipperk_salvaged_pickaxe", "VIP perk salvaged pickaxe. For players who want their mining tool to look like it survived three divorces and a Bradley encounter." },
                { "large_backpack", "More pockets for your pocket problem. Carry extra loot, extra snacks, and the emotional burden of refusing to depot." },
                { "super_tea", "Liquid productivity with suspicious side effects. Chug it and become the kind of person nodes warn each other about." },
                { "test_generator", "Forbidden hum box. Produces power, bad decisions, and the vibe of someone who absolutely bypassed the intended tech tree." },
                { "mlrs_truck", "Mobile war crime-adjacent driveway decoration. Place it carefully, aim responsibly, and remember: consequences are just content with extra steps. (Player Placeable)" },
                { "door_closer", "For people who were raised in a barn but are now trying to heal. Automatically shuts doors before the base becomes a loot pi\u00F1ata." },
                { "hobo_barrel", "A warm little barrel of questionable comfort. Perfect for mood lighting, frostbite prevention, and looking like you lost an argument with capitalism." },
                { "plush_scientist", "Tiny scientist. Zero threat. Maximum judgment. Place him somewhere important so he can silently disapprove of your base layout." },
                { "plush_hazmat", "Soft little hazmat buddy. Immune to radiation, vulnerable to being placed in cursed corners for decoration crimes." },
                { "plush_charity_01", "A plushie with a cause and absolutely no survival instincts. Protect them or face the moral consequences, you monster." },
                { "plush_pookie_bear", "Pookie bear has seen your search history, your loot room, and your wiring. Pookie remains disappointed but supportive." },
                { "plush_charity_02", "Another adorable charity plushie. Adds cuteness, reduces emotional stability, and makes your base look slightly less like a bunker full of feral raccoons." },
                { "deployable_rustige_egg_a", "Rustige Egg A. Decorative egg of mystery. Does it do anything? Emotionally, yes. Mechanically, probably not. Still adorable." },
                { "deployable_rustige_egg_b", "A festive little egg for players who looked at survival horror and said, but what if it was seasonal?" },
                { "deployable_rustige_egg_c", "Place it proudly. Confuse your enemies. Concern your teammates. Interior design is warfare." },
                { "deployable_rustige_egg_d", "Egg-shaped proof that your base has entered its whimsical nonsense era." },
                { "deployable_rustige_egg_e", "Not edible, not tactical, still somehow necessary. The loot goblin demands decor." },
                { "deployable_rustige_egg_f", "A tiny seasonal object with huge divorced-craft-store energy. Perfect. No notes." },
                { "deployable_rustige_egg_g", "For when your compound needs less menace and more suspicious holiday cheer." },
                { "deployable_rustige_egg_h", "The final egg in the emotional support egg lineup. Collect them all and become ungovernable." },
                { "fuel_diesel", "Industrial soup for big machines. Feed it to quarries, pumpjacks, and other metal demons that refuse to run on hopes and vibes." },
                { "tool_jackhammer", "The node deleter. Point at rock, hold button, receive dopamine. Side effects include tunnel vision and forgetting you came outside for wood." },
                { "tool_chainsaw", "Tree violence with a pull cord. Bring low grade, bring confidence, and maybe apologize to the forest after." },
                { "tool_ladder", "A vertical problem solver. For climbing walls, making choices, and discovering exactly how confident your enemies were in their base design." },
                { "carmods_chassis_2mod", "A tiny gremlin wagon starter kit. Two module slots, four wheels, and just enough dignity to get you home before someone judges your parking." },
                { "carmods_chassis_3mod", "The reasonable adult choice, unfortunately. Three module slots for when you need storage, speed, chaos, or all three because planning is for people with sleep schedules." },
                { "carmods_chassis_4mod", "The full land yacht experience. Four module slots of mobile bad decisions, perfect for road trips, loot hauling, and becoming a loud problem on purpose." },
        };

        private static Theme MakeTheme(string name, string tagline, string icon, string from, string to, int priority,
            string bg, string header, string panel, string card, string accent, string accentText,
            string button, string buttonText, string text, string subText)
        {
            return new Theme
            {
                Name = name, Tagline = tagline, Icon = icon, From = from, To = to, Priority = priority,
                Colors = new ThemeColors
                {
                    Background = bg, Header = header, Panel = panel, Card = card, Accent = accent, AccentText = accentText,
                    Button = button, ButtonText = buttonText, Text = text, SubText = subText
                }
            };
        }

        private static List<Theme> DefaultThemes() => new List<Theme>
        {
            MakeTheme("Nightshade", "AFTER DARK", "blood", "01-01", "12-31", -1,
                "#1A1024F2", "#241634FF", "#2A1B3AFF", "#33224AFF", "#8B3FBFFF", "#F2E6FFFF", "#3E2A55FF", "#E6D9F2FF", "#F2EAF7FF", "#A898B8FF"),
            MakeTheme("Spring", "BLOSSOM BAZAAR", "seed.corn", "03-20", "06-20", 0,
                "#14201AF2", "#1C2E24FF", "#22362BFF", "#2A4234FF", "#7FB069FF", "#10200AFF", "#2F4A3AFF", "#E3F5DAFF", "#F3FAEEFF", "#A7C2A0FF"),
            MakeTheme("Summer", "SUNSCORCH SUPPLY", "sunglasses", "06-21", "09-21", 0,
                "#1E1812F2", "#2E2218FF", "#35281CFF", "#403122FF", "#E8A33DFF", "#2A1A05FF", "#4A3826FF", "#FCEBD2FF", "#FFF6E8FF", "#C9B08FFF"),
            MakeTheme("Autumn", "HARVEST MARKET", "pumpkin", "09-22", "12-20", 0,
                "#1F140EF2", "#2E1D13FF", "#36231AFF", "#432C20FF", "#C8642DFF", "#FFF0E3FF", "#4D3324FF", "#F7E2D2FF", "#FCEFE4FF", "#C4A48CFF"),
            MakeTheme("Winter", "FROSTBITE EXCHANGE", "snowball", "12-21", "03-19", 0,
                "#0F1822F2", "#16222FFF", "#1C2A3AFF", "#233447FF", "#6FB3E0FF", "#06131FFF", "#2A3E54FF", "#DDEEFAFF", "#EEF6FCFF", "#9DB4C8FF"),
            MakeTheme("Halloween", "SPOOKY EMPORIUM", "pumpkin", "10-01", "10-31", 10,
                "#120A14F2", "#1C0F1FFF", "#241329FF", "#2E1833FF", "#F07A1AFF", "#1A0A00FF", "#3B1F42FF", "#F5DDF7FF", "#F7EEF8FF", "#B39CB8FF"),
            MakeTheme("Christmas", "FROSTED GIFT SHOP", "xmas.present.large", "12-01", "12-31", 10,
                "#101A14F2", "#1A2A1FFF", "#203326FF", "#283F2FFF", "#C8333AFF", "#FFF1F1FF", "#2F4A38FF", "#E6F5EAFF", "#F7FBF7FF", "#A8C2AFFF"),
        };

        protected override void LoadDefaultConfig()
        {
            config = new Configuration { Themes = DefaultThemes() };
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) throw new Exception("null config");
                if (config.Themes == null || config.Themes.Count == 0) config.Themes = DefaultThemes();
                if (config.Multipliers == null || config.Multipliers.Count == 0) config.Multipliers = new List<int> { 1 };
                if (config.Commands == null || config.Commands.Count == 0) config.Commands = new List<string> { "shop" };

                // Clean up lists duplicated by earlier versions.
                config.Multipliers = config.Multipliers.Where(m => m > 0).Distinct().OrderBy(m => m).ToList();
                config.Commands = config.Commands.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim().ToLowerInvariant()).Distinct().ToList();
                if (config.Events == null) config.Events = new EventSettings();
                SaveConfig();
            }
            catch (Exception e)
            {
                PrintError($"Config file is invalid ({e.Message}). Using defaults for this session; your file was NOT overwritten.");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        #endregion

        #region Data

        private class StoredData
        {
            public int NextId = 1;
            public bool RaidDisplayPricesApplied;
            public List<ShopCategory> Categories = new List<ShopCategory>();
            public List<Exchange> Exchanges = new List<Exchange>();

            [JsonProperty("Admin Shop Categories")]
            public List<TradeCategory> TradeCategories = new List<TradeCategory>();
        }

        private class ShopCategory
        {
            public string Name = "";

            [JsonProperty("Managed Raidable Bases Tab")]
            public bool RaidableTab;

            [JsonProperty("Icon (URL, skin ID or shortname)")]
            public string Icon = "";

            [JsonProperty("Required Permission (empty = everyone)")]
            public string Permission = "";

            [JsonProperty("Only Visible During Themes (empty = always)")]
            public List<string> Themes = new List<string>();

            // Null when the key is absent in Shop.json. Missing means shop, until the one-time name migration fills it in.
            [JsonProperty("Tab (shop|events)", NullValueHandling = NullValueHandling.Ignore)]
            public string Tab;

            // Null when the key is absent. Missing means visible, until the one-time name migration fills it in.
            [JsonProperty("Admin mode only (hidden from players)", NullValueHandling = NullValueHandling.Ignore)]
            public bool? AdminModeOnly;

            public List<ShopItem> Items = new List<ShopItem>();
        }

        private class PurchaseData
        {
            public Dictionary<ulong, Dictionary<int, PurchaseRecord>> Players = new Dictionary<ulong, Dictionary<int, PurchaseRecord>>();
        }

        private class PurchaseRecord
        {
            public DateTime Last = DateTime.MinValue;
            public string Day = "";
            public int DayCount;
            public int WipeCount;
        }

        private class ShopItem
        {
            public int Id;

            [JsonProperty("Type (item, raidable, dungeon, command)")]
            public string Type = "item";

            [JsonProperty("Event Argument (raid difficulty 0-4 or dungeon tier name)")]
            public string EventArg = "";

            [JsonProperty("Charged By Event Plugin (true = RaidableBases/DungeonEvents take the RP and refund failures)")]
            public bool ChargedByPlugin = true;

            [JsonProperty("Show Event Plugin Pricing Instead Of Display Price")]
            public bool PluginPricing;

            [JsonProperty("Card Icon (URL, skin ID or shortname; empty = item icon)")]
            public string Icon = "";

            [JsonProperty("Required Permission (empty = everyone)")]
            public string Permission = "";

            [JsonProperty("Cooldown Between Purchases (seconds, 0 = none)")]
            public int Cooldown;

            [JsonProperty("Daily Buy Limit (0 = none)")]
            public int DailyLimit;

            [JsonProperty("Wipe Buy Limit (0 = none)")]
            public int WipeLimit;

            [JsonProperty("Limit Each Purchase To One")]
            public bool LimitToOne;

            [JsonProperty("Description (empty = use matching imported description)")]
            public string Description = "";

            [JsonProperty("Imported Description Key (for example helisignal_easy)")]
            public string DescriptionKey = "";

            [JsonProperty("Show Small Description Box Below Item")]
            public bool ShowDescriptionOnCard = true;

            [JsonProperty("Require Steam Ownership (opt-in for paid store/DLC items)")]
            public bool RequireOwnership;

            public string Shortname = "";
            public ulong Skin;
            public int Amount = 1;
            public int Price = 100;

            [JsonProperty("Display Name (shop card)")]
            public string DisplayName = "";

            [JsonProperty("Custom Item Name (applied to the item)")]
            public string ItemName = "";

            public bool Blueprint;

            [JsonProperty("Give Item (false for command-only rewards)")]
            public bool GiveItem = true;

            [JsonProperty("Commands On Purchase ({steamid} {name})")]
            public List<string> Commands = new List<string>();
        }

        private class TradeCategory
        {
            public string Name = "";

            [JsonProperty("Icon (URL, skin ID or shortname)")]
            public string Icon = "";
        }

        private class Exchange
        {
            public int Id;

            [JsonProperty("Admin Shop Category")]
            public string Category = "";

            [JsonProperty("Input Shortname (rp = Server Rewards points)")]
            public string InShortname = "wood";
            public ulong InSkin;
            public int InAmount = 1000;

            [JsonProperty("Output Shortname (rp = Server Rewards points)")]
            public string OutShortname = "blood";
            public ulong OutSkin;
            public int OutAmount = 1;

            [JsonProperty("Output Custom Item Name")]
            public string OutName = "";
        }

        private class KitFile
        {
            [JsonProperty("Migrated From VIPPerks")]
            public bool MigratedFromVipPerks;

            public int NextId = 1;

            public List<KitDef> Kits = new List<KitDef>();

            public Dictionary<string, Dictionary<string, KitClaim>> Claims = new Dictionary<string, Dictionary<string, KitClaim>>();
        }

        private class KitDef
        {
            public string Id = "";
            public string Name = "Kit";

            [JsonProperty("Icon (URL, skin ID or shortname)")]
            public string Icon = "";

            [JsonProperty("Required Permission (empty = anyone who can open Kits)")]
            public string Permission = "";

            [JsonProperty("Cooldown (seconds)")]
            public int CooldownSeconds;

            [JsonProperty("Max Uses Per Wipe (0 = no cap)")]
            public int MaxUsesPerWipe;

            public List<KitStack> Items = new List<KitStack>();
        }

        private class KitStack
        {
            public string Shortname = "";
            public int Amount = 1;
            public ulong Skin;
        }

        private class KitClaim
        {
            public DateTime LastClaim = DateTime.MinValue;
            public int WipeUses;
        }

        private StoredData DefaultData()
        {
            var d = new StoredData();
            Func<string, int, int, ShopItem> item = (sn, amt, price) => new ShopItem { Id = d.NextId++, Shortname = sn, Amount = amt, Price = price };

            d.Categories.Add(new ShopCategory
            {
                Name = "Resources", Icon = "wood",
                Items = { item("wood", 1000, 50), item("stones", 1000, 60), item("metal.fragments", 500, 90), item("sulfur", 500, 120), item("lowgradefuel", 100, 80) }
            });
            d.Categories.Add(new ShopCategory
            {
                Name = "Components", Icon = "gears",
                Items = { item("gears", 5, 75), item("metalspring", 5, 90), item("techparts", 3, 150), item("rope", 10, 40) }
            });
            d.Categories.Add(new ShopCategory
            {
                Name = "Medical", Icon = "syringe.medical",
                Items = { item("syringe.medical", 5, 80), item("largemedkit", 2, 100), item("bandage", 10, 30) }
            });

            d.Exchanges.Add(new Exchange { Id = d.NextId++, InShortname = "sulfur", InAmount = 1000, OutShortname = "blood", OutAmount = 10 });
            d.Exchanges.Add(new Exchange { Id = d.NextId++, InShortname = "metal.fragments", InAmount = 1500, OutShortname = "blood", OutAmount = 10 });
            d.Exchanges.Add(new Exchange { Id = d.NextId++, InShortname = "scrap", InAmount = 250, OutShortname = "blood", OutAmount = 5 });
            d.Exchanges.Add(new Exchange { Id = d.NextId++, InShortname = "blood", InAmount = 25, OutShortname = "rp", OutAmount = 100 });
            return d;
        }

        private void LoadData()
        {
            try { data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataShop); }
            catch (Exception e) { PrintError($"Data file failed to load: {e.Message}"); data = null; }

            if (data == null)
            {
                data = DefaultData();
                SaveData();
                return;
            }
            if (data.Categories == null) data.Categories = new List<ShopCategory>();
            if (data.Exchanges == null) data.Exchanges = new List<Exchange>();
            if (data.TradeCategories == null) data.TradeCategories = new List<TradeCategory>();
            if (data.Categories.Count == 0 && data.Exchanges.Count == 0 && data.NextId <= 1)
            {
                data = DefaultData();
                SaveData();
            }
            int migrated = 0;
            foreach (var cat in data.Categories)
                if (MigrateCategory(cat)) migrated++;
            if (migrated > 0)
            {
                SaveData();
                Puts("NightshadeShop migrated " + migrated + " categor" + (migrated == 1 ? "y" : "ies") + " in Shop.json (Events tab and admin-only flags).");
            }
        }

        private static readonly string[] EventsTabNames = { "Dungeons", "Raidable Bases", "Heli Signals", "Brad Signals" };
        private static readonly string[] AdminOnlyNames = { "Christmas", "Halloween" };

        private static bool NameIs(string name, string[] names)
        {
            if (string.IsNullOrWhiteSpace(name) || names == null) return false;
            string trimmed = name.Trim();
            for (int i = 0; i < names.Length; i++)
                if (string.Equals(trimmed, names[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Fills missing fields only. A stored shop/events value or an explicit false flag is left alone.
        private static bool MigrateCategory(ShopCategory cat)
        {
            if (cat == null) return false;
            bool changed = false;
            if (string.IsNullOrWhiteSpace(cat.Tab))
            {
                if (NameIs(cat.Name, EventsTabNames)) { cat.Tab = "events"; changed = true; }
                else if (cat.Tab != null) { cat.Tab = null; changed = true; }
            }
            else
            {
                string norm = cat.Tab.Trim().Equals("events", StringComparison.OrdinalIgnoreCase) ? "events" : "shop";
                if (!string.Equals(cat.Tab, norm, StringComparison.Ordinal)) { cat.Tab = norm; changed = true; }
            }
            if (!cat.AdminModeOnly.HasValue && NameIs(cat.Name, AdminOnlyNames))
            {
                cat.AdminModeOnly = true;
                changed = true;
            }
            return changed;
        }

        private static string CategoryTab(ShopCategory cat)
        {
            if (cat == null || string.IsNullOrWhiteSpace(cat.Tab)) return "shop";
            return cat.Tab.Trim().Equals("events", StringComparison.OrdinalIgnoreCase) ? "events" : "shop";
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(DataShop, data);

        private void LoadPurchases()
        {
            try { purchases = Interface.Oxide.DataFileSystem.ReadObject<PurchaseData>(DataPurchases); }
            catch { purchases = null; }
            if (purchases?.Players == null) purchases = new PurchaseData();
        }

        private void SavePurchases() => Interface.Oxide.DataFileSystem.WriteObject(DataPurchases, purchases);

        private void LoadKits()
        {
            try { kitFile = Interface.Oxide.DataFileSystem.ReadObject<KitFile>(DataKits); }
            catch (Exception e)
            {
                PrintError("Kits file failed to load: " + e.Message);
                kitFile = null;
            }
            if (kitFile == null) kitFile = new KitFile();
            if (kitFile.Kits == null) kitFile.Kits = new List<KitDef>();
            if (kitFile.Claims == null) kitFile.Claims = new Dictionary<string, Dictionary<string, KitClaim>>();
            bool changed = false;
            if (!kitFile.MigratedFromVipPerks)
            {
                if (MigrateVipPerkKits())
                {
                    kitFile.MigratedFromVipPerks = true;
                    changed = true;
                }
                else
                    PrintWarning("VIP kit cooldown import did not finish. Kits.json was left unmarked so the next load can retry.");
            }
            if (ApplyStoredKitDefaults()) changed = true;
            foreach (var kit in kitFile.Kits)
            {
                if (kit.Items == null) kit.Items = new List<KitStack>();
                if (kit.Permission == null) kit.Permission = "";
                RegisterPerm(kit.Permission);
            }
            if (changed) SaveKits();
        }

        private void SaveKits() => Interface.Oxide.DataFileSystem.WriteObject(DataKits, kitFile);

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "You don't have permission to use the shop.",
                ["NoServerRewards"] = "Server Rewards is not loaded, the shop is unavailable.",
                ["Purchased"] = "Purchased {0} x{1} for {2} RP.",
                ["NotEnoughRP"] = "You need {0} RP (you have {1}).",
                ["NoSpace"] = "Not enough inventory space.",
                ["ItemMissing"] = "That listing no longer exists.",
                ["InvalidItem"] = "Listing has an invalid item shortname: {0}",
                ["Traded"] = "Traded {0} for {1}.",
                ["NotEnoughTrade"] = "You need {0} to make this trade.",
                ["TradeMissing"] = "That trade no longer exists.",
                ["HoldItem"] = "Hold the item in your hands or place it in hotbar slot 1.",
                ["ItemAdded"] = "Added {0} to {1}.",
                ["ConfirmDelete"] = "Click again to confirm delete.",
                ["Deleted"] = "Deleted.",
                ["Saved"] = "Saved.",
                ["BadValue"] = "Invalid value.",
                ["CategoryExists"] = "A category with that name already exists.",
                ["CategoryAdded"] = "Category '{0}' added.",
                ["NoCategory"] = "Create a category first.",
                ["ThemeSet"] = "Theme mode set to {0}. Active theme: {1}.",
                ["ThemeUnknown"] = "Unknown theme. Available: {0}, Auto",
                ["PluginMissing"] = "{0} is not loaded right now.",
                ["EventRequested"] = "{0} requested. {1} will charge your RP and confirm in chat.",
                ["EventBought"] = "{0} purchased for {1} RP. It will spawn shortly.",
                ["EventRefunded"] = "{0} could not be spawned. {1} RP was refunded.",
                ["EventAdded"] = "Added {0} to {1}.",
                ["PickFirst"] = "Pick an item from the list.",
                ["NoAccess"] = "You don't have access to that listing.",
                ["HiddenCategory"] = "That category is hidden right now.",
                ["LimitReached"] = "Can't buy that right now: {0}.",
                ["NotOwned"] = "You need to own {0} on Steam to buy it here.",
                ["PickQty"] = "Pick x1, x10 or MAX first, then press CONFIRM.",
                ["TradeNeed"] = "You need {0} {1} for that (you have {2}).",
                ["Help"] = "Shop admin commands:\n/{0} theme <name|auto>\n/{0} themes\n/{0} addcat <name> [iconShortname]\n/{0} delcat <name|index>\n/{0} add <category|index> <price> [amount] (uses held item)\n/{0} remove <itemId>\n/{0} trade <inShortname> <inAmount> <outShortname> <outAmount>\n/{0} deltrade <tradeId>\nUse 'rp' as a shortname for Server Rewards points."
            }, this);
        }

        private string Msg(string key, BasePlayer p, params object[] args)
        {
            string m = lang.GetMessage(key, this, p?.UserIDString);
            return args.Length > 0 ? string.Format(m, args) : m;
        }

        #endregion

        #region Hooks

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermUse, this);
            if (!string.IsNullOrEmpty(config.ExchangePerm) && config.ExchangePerm.StartsWith("nightshadeshop.", StringComparison.OrdinalIgnoreCase)
                && !permission.PermissionExists(config.ExchangePerm, this))
                permission.RegisterPermission(config.ExchangePerm, this);

            LoadData();
            LoadPurchases();
            LoadRefunds();
            LoadKits();
            RegisterListingPerms();
            RegisterPerm(PermWipeLock);
            RegisterPerm(PermVipPerk);
            RegisterPerm(PermVipXPerk);
            EnsureIconsFolder();
            foreach (var c in config.Commands)
                cmd.AddChatCommand(c, this, nameof(ChatShop));
            cmd.AddChatCommand("perks", this, nameof(ChatOpenKits));
            cmd.AddChatCommand("vipdailykit", this, nameof(ChatOpenKits));
            cmd.AddChatCommand("vip4daykit", this, nameof(ChatOpenKits));
        }

        private void OnServerInitialized()
        {
            serverReady = true;
            if (ServerRewards == null)
                PrintWarning("ServerRewards is not loaded. Purchases and RP trades will fail until it is.");
            EnsureRaidableTab();
            FindPaidItems();
            PreloadLocalIcons();
            Puts($"NightshadeShop v{PluginVersion} loaded: {data.Categories.Count} categories, {data.Categories.Sum(x => x.Items.Count)} listings, {data.TradeCategories.Count} Admin Shop categories, {data.Exchanges.Count} trades, {kitFile.Kits.Count} kits.");
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin == null || unloading) return;
            if (plugin.Name == "RaidableBases")
            {
                RaidableBases = plugin;
                if (serverReady) NextTick(() => { if (!unloading) EnsureRaidableTab(); });
            }
            else if (plugin.Name == "ServerRewards")
            {
                ServerRewards = plugin;
                if (serverReady) NextTick(() =>
                {
                    if (unloading) return;
                    foreach (var refund in pendingRefunds.Where(r => r.RefundDue).ToList()) Refund(refund);
                });
            }
            else if (plugin.Name == "ImageLibrary")
            {
                ImageLibrary = plugin;
                localPngQueued.Clear();
                if (serverReady) NextTick(() => { if (!unloading) PreloadLocalIcons(); });
            }
        }

        private void EnsureRaidableTab()
        {
            if (data == null || !config.Events.AutoAddRaidableTab) return;
            var raid = data.Categories.FirstOrDefault(x => x.RaidableTab)
                ?? data.Categories.FirstOrDefault(x => string.Equals(x.Name, "Raidable Bases", StringComparison.OrdinalIgnoreCase));
            if (raid == null)
            {
                raid = new ShopCategory { Name = "Raidable Bases", Icon = config.Events.RbIcon, Tab = "events" };
                data.Categories.Add(raid);
            }
            raid.RaidableTab = true;
            MigrateCategory(raid);

            // Use the plugin's numeric levels, including custom difficulties, when available.
            var modes = new SortedDictionary<int, string>();
            if (RaidableBases != null)
            {
                try
                {
                    var configured = RaidableBases.Call("GetAllDifficulties") as System.Collections.IEnumerable;
                    if (configured != null)
                        foreach (var entry in configured)
                        {
                            if (entry == null) continue;
                            var t = entry.GetType();
                            var nameField = t.GetField("Item1");
                            var levelField = t.GetField("Item2");
                            if (nameField == null || levelField == null) continue;
                            var name = nameField.GetValue(entry) as string;
                            var level = levelField.GetValue(entry);
                            if (!string.IsNullOrWhiteSpace(name) && level is int)
                                modes[(int)level] = name;
                        }
                }
                catch (Exception ex) { PrintWarning($"Could not read Raidable Bases difficulties: {ex.Message}"); }
            }
            if (modes.Count == 0)
                for (int i = 0; i < RbDifficulties.Length; i++) modes[i] = RbDifficulties[i];

            foreach (var mode in modes)
            {
                int displayPrice = -1;
                if (config.Events.RbDisplayPrices != null)
                    foreach (var entry in config.Events.RbDisplayPrices)
                        if (string.Equals(entry.Key, mode.Value, StringComparison.OrdinalIgnoreCase))
                        { displayPrice = Math.Max(0, entry.Value); break; }
                // Move existing matching listings rather than duplicating them or resetting their settings.
                var existing = data.Categories.SelectMany(x => x.Items).FirstOrDefault(x => x.Type == "raidable"
                    && (x.EventArg == mode.Key.ToString(CultureInfo.InvariantCulture)
                        || string.Equals(x.EventArg, mode.Value, StringComparison.OrdinalIgnoreCase)));
                if (existing != null)
                {
                    foreach (var category in data.Categories.Where(x => x != raid)) category.Items.Remove(existing);
                    if (!raid.Items.Contains(existing)) raid.Items.Add(existing);
                    // One-time upgrade also updates cards created by versions 1.4.6/1.4.7.
                    if (!data.RaidDisplayPricesApplied && displayPrice >= 0)
                    {
                        existing.Price = displayPrice;
                        existing.PluginPricing = false;
                        existing.ChargedByPlugin = true;
                    }
                    continue;
                }
                raid.Items.Add(new ShopItem
                {
                    Id = data.NextId++, Type = "raidable", EventArg = mode.Key.ToString(CultureInfo.InvariantCulture),
                    DisplayName = mode.Value.ToUpperInvariant() + " RAID BASE", GiveItem = false,
                    ChargedByPlugin = true, PluginPricing = displayPrice < 0,
                    Price = Math.Max(0, displayPrice), LimitToOne = true
                });
            }

            data.RaidDisplayPricesApplied = true;
            data.Categories.Remove(raid);
            int dungeon = data.Categories.FindIndex(x => x.Name.IndexOf("dungeon", StringComparison.OrdinalIgnoreCase) >= 0
                || x.Items.Any(i => i.Type == "dungeon"));
            if (dungeon < 0)
            {
                data.Categories.Add(new ShopCategory { Name = "Dungeon", Icon = config.Events.DeIcon, Tab = "events" });
                dungeon = data.Categories.Count - 1;
            }
            data.Categories.Insert(dungeon + 1, raid);
            SaveData();
        }

        private int TradeHave(BasePlayer p, Exchange ex)
        {
            if (IsRP(ex.InShortname)) return Balance(p);
            var def = Def(ex.InShortname);
            return def != null ? CountItems(p, def, ex.InSkin) : 0;
        }

        // shortname -> Steam inventory/DLC id the player must own. Built once from the game's item and skin directories.
        private readonly Dictionary<string, int> paidItems = new Dictionary<string, int>();

        private void FindPaidItems()
        {
            paidItems.Clear();
            foreach (var skin in ItemSkinDirectory.Instance.skins)
            {
                var itemSkin = skin.invItem as ItemSkin;
                if (itemSkin == null || itemSkin.UnlockedByDefault || itemSkin.Redirect == null) continue;
                paidItems[itemSkin.Redirect.shortname] = skin.id;
            }
            foreach (var def in ItemManager.itemList)
            {
                if (def.steamDlc != null && !def.steamDlc.bypassLicenseCheck) paidItems[def.shortname] = def.steamDlc.dlcAppID;
                else if (def.steamItem != null) paidItems[def.shortname] = def.steamItem.id;
            }
        }

        private bool Owns(BasePlayer p, ItemDefinition def)
        {
            if (!config.RequireOwnership || def == null) return true;
            int id;
            if (!paidItems.TryGetValue(def.shortname, out id)) return true;
            return p.blueprints.steamInventory.HasItem(id);
        }

        private void Unload()
        {
            unloading = true;
            foreach (var p in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(p, UiMain);
                CuiHelper.DestroyUi(p, UiScrollTest);
            }

            // Preserve pending spawns across reloads; refunding here could give a free event
            // if its asynchronous spawn completes immediately after this plugin unloads.
            foreach (var r in pendingRefunds) r.Timer?.Destroy();
            SaveRefunds();
            SaveData();
            SavePurchases();
            SaveKits();
        }

        // Map wipe: reset per-wipe purchase counts.
        private void OnNewSave()
        {
            if (purchases == null) LoadPurchases();
            foreach (var player in purchases.Players.Values)
                foreach (var rec in player.Values)
                    rec.WipeCount = 0;
            SavePurchases();
            if (kitFile?.Claims != null)
            {
                foreach (var player in kitFile.Claims.Values)
                    foreach (var claim in player.Values)
                        claim.WipeUses = 0;
                SaveKits();
            }
        }

        // Use the complete documented signature for Carbon's plugin hook dispatch.
        private void OnRaidableBasePurchased(string owner, Vector3 location, string grid, int level,
            bool allowPVP, string baseName, DateTime spawnDateTime, DateTime despawnDateTime)
        {
            var match = pendingRefunds.FirstOrDefault(r => !r.RefundDue && r.Type == "raidable" && r.UserId.ToString() == owner);
            ConfirmEvent(match);
        }

        private void OnDungeonSpawn(ulong ownerID, Vector3 position, string grid, string tierName)
        {
            ConfirmEvent(pendingRefunds.FirstOrDefault(r => !r.RefundDue && r.Type == "dungeon"
                && r.UserId == ownerID && string.Equals(r.EventArg, tierName, StringComparison.OrdinalIgnoreCase)));
        }

        private void ConfirmEvent(PendingRefund r)
        {
            if (r == null || !pendingRefunds.Remove(r)) return;
            r.Timer?.Destroy();
            SaveRefunds();
            var player = BasePlayer.FindByID(r.UserId) ?? BasePlayer.FindSleeping(r.UserId);
            if (player != null && r.Listing != null) Record(player, r.Listing, 1);
            Log($"Confirmed delivery of {r.Label} to {r.UserId}");
        }

        private void SaveRefunds() => Interface.Oxide.DataFileSystem.WriteObject(DataRefunds, pendingRefunds);

        private void LoadRefunds()
        {
            List<PendingRefund> saved;
            try { saved = Interface.Oxide.DataFileSystem.ReadObject<List<PendingRefund>>(DataRefunds); }
            catch (Exception ex)
            {
                // Fail closed: never overwrite an unreadable debt ledger with an empty one.
                throw new Exception("Cannot load refund ledger; repair it before enabling purchases.", ex);
            }
            if (saved == null) return;
            foreach (var r in saved)
            {
                pendingRefunds.Add(r);
                float delay = r.RefundDue ? 1f : (float)Math.Max(1d, (r.Deadline - DateTime.UtcNow).TotalSeconds);
                r.Timer = timer.Once(delay, () => Refund(r));
            }
        }

        private void OnServerSave()
        {
            SaveData();
            SavePurchases();
            SaveKits();
        }

        private void OnPlayerDisconnected(BasePlayer p) => states.Remove(p.userID);

        #endregion

        #region Helpers

        private UiState S(BasePlayer p)
        {
            UiState s;
            if (!states.TryGetValue(p.userID, out s))
                states[p.userID] = s = new UiState();
            return s;
        }

        private bool IsAdmin(BasePlayer p) => p != null && (p.IsAdmin || permission.UserHasPermission(p.UserIDString, PermAdmin));

        private bool CanUse(BasePlayer p) => !config.RequireUsePerm || IsAdmin(p) || permission.UserHasPermission(p.UserIDString, PermUse);

        private bool CanExchange(BasePlayer p) => string.IsNullOrEmpty(config.ExchangePerm) || IsAdmin(p) || permission.UserHasPermission(p.UserIDString, config.ExchangePerm);

        private static bool IsRP(string sn) => string.Equals(sn, "rp", StringComparison.OrdinalIgnoreCase);

        private static ItemDefinition Def(string sn) => string.IsNullOrEmpty(sn) ? null : ItemManager.FindItemDefinition(sn);

        private static string NameOf(string sn, string custom = null)
        {
            if (!string.IsNullOrEmpty(custom)) return custom;
            if (IsRP(sn)) return "RP";
            var def = Def(sn);
            return def != null ? def.displayName.english : sn;
        }

        private static bool IsEvent(ShopItem item) => item.Type == "raidable" || item.Type == "dungeon";

        private static string RbName(string arg)
        {
            int d;
            if (int.TryParse(arg, out d) && d >= 0 && d < RbDifficulties.Length) return RbDifficulties[d];
            return (arg ?? "").ToUpperInvariant();
        }

        private static string ItemLabel(ShopItem item)
        {
            if (!string.IsNullOrEmpty(item.DisplayName)) return item.DisplayName;
            if (item.Type == "raidable") return $"{RbName(item.EventArg)} RAID BASE";
            if (item.Type == "dungeon") return $"{(item.EventArg ?? "").ToUpperInvariant()} DUNGEON";
            if (item.Type == "command") return "COMMAND REWARD";
            var name = NameOf(item.Shortname, item.ItemName);
            return item.Blueprint ? name + " Blueprint" : name;
        }

        private ShopItem FindItem(int id, out ShopCategory category)
        {
            foreach (var c in data.Categories)
                foreach (var i in c.Items)
                    if (i.Id == id) { category = c; return i; }
            category = null;
            return null;
        }

        private void SetStatus(UiState s, string text, bool bad)
        {
            s.Status = text;
            s.StatusBad = bad;
        }

        private bool Confirm(UiState s, string key)
        {
            float now = Time.realtimeSinceStartup;
            if (s.Confirm == key && now - s.ConfirmAt < 5f)
            {
                s.Confirm = null;
                return true;
            }
            s.Confirm = key;
            s.ConfirmAt = now;
            return false;
        }

        private void Fx(BasePlayer p, string path)
        {
            if (p?.net?.connection == null) return;
            var e = new Effect(path, p, 0, Vector3.zero, Vector3.forward);
            EffectNetwork.Send(e, p.net.connection);
        }

        private void Log(string line)
        {
            if (config.Log) LogToFile("transactions", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}", this);
        }

        private Item SourceItem(BasePlayer p) => p.GetActiveItem() ?? p.inventory.containerBelt.GetSlot(0);

        // Fallback icon when the listing's Icon field is empty or unusable.
        private ItemDefinition CardIcon(ShopItem item)
        {
            if (item.Type == "raidable") return Def(config.Events.RbIcon);
            if (item.Type == "dungeon") return Def(config.Events.DeIcon);
            return Def(item.Shortname);
        }

        private List<Exchange> TradesIn(int cat)
        {
            if (cat == TradeOther)
                return data.Exchanges.Where(e => !data.TradeCategories.Any(t => string.Equals(t.Name, e.Category, StringComparison.OrdinalIgnoreCase))).ToList();
            if (cat < 0 || cat >= data.TradeCategories.Count) return new List<Exchange>();
            string name = data.TradeCategories[cat].Name;
            return data.Exchanges.Where(e => string.Equals(e.Category, name, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private bool InSeason(ShopCategory cat)
        {
            if (cat.Themes == null || cat.Themes.Count == 0) return true;
            string active = ActiveTheme().Name;
            return cat.Themes.Any(t => string.Equals(t, active, StringComparison.OrdinalIgnoreCase));
        }

        private bool CanSee(BasePlayer p, string perm) =>
            string.IsNullOrEmpty(perm) || IsAdmin(p) || permission.UserHasPermission(p.UserIDString, perm);

        // Admin-only categories stay hidden, including from buy commands, until that admin turns admin mode on.
        private bool HiddenFromPlayer(BasePlayer p, ShopCategory cat)
        {
            if (cat == null || cat.AdminModeOnly != true) return false;
            if (p == null || !IsAdmin(p)) return true;
            UiState s;
            if (!states.TryGetValue(p.userID, out s) || s == null || !s.Admin) return true;
            return false;
        }

        private void RegisterPerm(string perm)
        {
            if (!string.IsNullOrEmpty(perm) && perm.StartsWith("nightshadeshop.", StringComparison.OrdinalIgnoreCase) && !permission.PermissionExists(perm, this))
                permission.RegisterPermission(perm, this);
        }

        private void RegisterListingPerms()
        {
            foreach (var c in data.Categories)
            {
                RegisterPerm(c.Permission);
                foreach (var i in c.Items) RegisterPerm(i.Permission);
            }
        }

        private static string Fill(string command, BasePlayer p) => command
            .Replace("{steamid}", p.UserIDString).Replace("{userid}", p.UserIDString).Replace("{userId}", p.UserIDString)
            .Replace("{name}", p.displayName).Replace("{username}", p.displayName).Replace("{userName}", p.displayName);

        private static string Today => DateTime.Now.ToString("yyyy-MM-dd");

        private PurchaseRecord Rec(ulong id, int itemId, bool create)
        {
            Dictionary<int, PurchaseRecord> player;
            if (!purchases.Players.TryGetValue(id, out player))
            {
                if (!create) return null;
                purchases.Players[id] = player = new Dictionary<int, PurchaseRecord>();
            }
            PurchaseRecord rec;
            if (!player.TryGetValue(itemId, out rec) && create)
                player[itemId] = rec = new PurchaseRecord();
            return rec;
        }

        private static string ShortTime(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Ceiling(seconds));
            if (t.TotalHours >= 1) return t.Minutes > 0 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{(int)t.TotalHours}h";
            if (t.TotalMinutes >= 1) return t.Seconds > 0 ? $"{t.Minutes}m {t.Seconds}s" : $"{t.Minutes}m";
            return $"{t.Seconds}s";
        }

        // How many of this listing the player may buy right now (int.MaxValue = unlimited). reason is set when 0.
        private int Remaining(BasePlayer p, ShopItem item, out string reason)
        {
            reason = null;
            var rec = Rec(p.userID, item.Id, false);
            if (item.Cooldown > 0 && rec != null)
            {
                double left = (rec.Last.AddSeconds(item.Cooldown) - DateTime.Now).TotalSeconds;
                if (left > 0) { reason = "COOLDOWN " + ShortTime(left); return 0; }
            }

            int max = item.LimitToOne || IsEvent(item) ? 1 : int.MaxValue;
            int wipeUsed = rec?.WipeCount ?? 0;
            int dayUsed = rec != null && rec.Day == Today ? rec.DayCount : 0;
            if (item.WipeLimit > 0)
            {
                if (wipeUsed >= item.WipeLimit) { reason = $"WIPE LIMIT {wipeUsed}/{item.WipeLimit}"; return 0; }
                max = Math.Min(max, item.WipeLimit - wipeUsed);
            }
            if (item.DailyLimit > 0)
            {
                if (dayUsed >= item.DailyLimit) { reason = $"DAILY LIMIT {dayUsed}/{item.DailyLimit}"; return 0; }
                max = Math.Min(max, item.DailyLimit - dayUsed);
            }
            return max;
        }

        private string LimitText(BasePlayer p, ShopItem item)
        {
            var rec = Rec(p.userID, item.Id, false);
            var lines = new List<string>();
            if (item.WipeLimit > 0) lines.Add($"WIPE\n{rec?.WipeCount ?? 0}/{item.WipeLimit}");
            if (item.DailyLimit > 0) lines.Add($"DAILY\n{(rec != null && rec.Day == Today ? rec.DayCount : 0)}/{item.DailyLimit}");
            if (item.Cooldown > 0) lines.Add("CD\n" + ShortTime(item.Cooldown));
            if (lines.Count > 0) return string.Join("\n\n", lines);
            return null;
        }

        private void Record(BasePlayer p, ShopItem item, int qty)
        {
            if (item.Cooldown <= 0 && item.DailyLimit <= 0 && item.WipeLimit <= 0) return;
            var rec = Rec(p.userID, item.Id, true);
            rec.Last = DateTime.Now;
            if (rec.Day != Today) { rec.Day = Today; rec.DayCount = 0; }
            rec.DayCount += qty;
            rec.WipeCount += qty;
        }

        private List<ItemDefinition> PickList()
        {
            if (pickList == null)
                pickList = ItemManager.itemList
                    .Where(d => d != null && !d.hidden && d.shortname != "blueprintbase")
                    .OrderBy(d => d.displayName.english)
                    .ToList();
            return pickList;
        }

        private List<ItemDefinition> FilteredPickList(UiState s)
        {
            IEnumerable<ItemDefinition> q = PickList();
            if (s.PickCat >= 0) q = q.Where(d => (int)d.category == s.PickCat);
            if (!string.IsNullOrEmpty(s.PickSearch))
                q = q.Where(d => d.displayName.english.IndexOf(s.PickSearch, StringComparison.OrdinalIgnoreCase) >= 0
                              || d.shortname.IndexOf(s.PickSearch, StringComparison.OrdinalIgnoreCase) >= 0);
            return q.ToList();
        }

        #endregion

        #region Currency

        private int Balance(BasePlayer p)
        {
            if (ServerRewards == null) return 0;
            ulong id = p.userID;
            var o = ServerRewards.Call("CheckPoints", id);
            return o is int ? (int)o : 0;
        }

        private bool TakeRP(ulong id, int amount)
        {
            if (amount <= 0) return true;
            if (ServerRewards == null) return false;
            var o = ServerRewards.Call("TakePoints", id, amount);
            return o is bool && (bool)o;
        }

        private bool AddRP(ulong id, int amount)
        {
            if (amount <= 0) return true;
            if (ServerRewards == null) return false;
            var o = ServerRewards.Call("AddPoints", id, amount);
            return o is bool && (bool)o;
        }

        #endregion

        #region Inventory

        private int CountItems(BasePlayer p, ItemDefinition def, ulong skin)
        {
            int n = 0;
            foreach (var it in p.inventory.containerMain.itemList) if (it.info == def && it.skin == skin) n += it.amount;
            foreach (var it in p.inventory.containerBelt.itemList) if (it.info == def && it.skin == skin) n += it.amount;
            return n;
        }

        private void TakeItems(BasePlayer p, ItemDefinition def, ulong skin, int amount)
        {
            foreach (var cont in new[] { p.inventory.containerMain, p.inventory.containerBelt })
            {
                foreach (var it in cont.itemList.ToList())
                {
                    if (amount <= 0) return;
                    if (it.info != def || it.skin != skin) continue;
                    int take = Math.Min(it.amount, amount);
                    it.UseItem(take);
                    amount -= take;
                }
            }
        }

        private int FreeSlots(BasePlayer p)
        {
            var m = p.inventory.containerMain;
            var b = p.inventory.containerBelt;
            return (m.capacity - m.itemList.Count) + (b.capacity - b.itemList.Count);
        }

        private int SlotsNeeded(ItemDefinition def, long amount, bool blueprint)
        {
            var stackDef = blueprint ? ItemManager.FindItemDefinition("blueprintbase") : def;
            long stack = Math.Max(1, stackDef != null ? stackDef.stackable : 1);
            return (int)Math.Min(int.MaxValue, (amount + stack - 1) / stack);
        }

        // Keep new stacks separate until all have arrived, so rollback never removes existing items.
        private bool TryDeliverItems(BasePlayer p, ItemDefinition def, ulong skin, int amount, bool blueprint, string itemName)
        {
            var created = new List<Item>();
            try
            {
                var actualDef = blueprint ? ItemManager.FindItemDefinition("blueprintbase") : def;
                if (actualDef == null || amount <= 0) return false;
                int stack = Math.Max(1, actualDef.stackable);
                while (amount > 0)
                {
                    int n = Math.Min(stack, amount);
                    var it = ItemManager.Create(actualDef, n, blueprint ? 0UL : skin);
                    if (it == null) throw new Exception("Item creation failed");
                    created.Add(it);
                    if (blueprint) it.blueprintTarget = def.itemid;
                    if (!string.IsNullOrEmpty(itemName)) it.name = itemName;
                    if (!it.MoveToContainer(p.inventory.containerMain, -1, false)
                        && !it.MoveToContainer(p.inventory.containerBelt, -1, false))
                        throw new Exception("Inventory rejected delivery");
                    amount -= n;
                }
                if (created.Any(it => it.parent != p.inventory.containerMain && it.parent != p.inventory.containerBelt))
                    throw new Exception("Item did not remain in the buyer's inventory");
                return true;
            }
            catch (Exception ex)
            {
                foreach (var it in created) it.Remove();
                PrintWarning($"Item delivery failed for {p.userID}: {ex.Message}. New stacks rolled back.");
                return false;
            }
        }

        private void GiveItems(BasePlayer p, ItemDefinition def, ulong skin, int amount, bool blueprint, string itemName)
        {
            if (blueprint)
            {
                var bpDef = ItemManager.FindItemDefinition("blueprintbase");
                int bpStack = Math.Max(1, bpDef.stackable);
                while (amount > 0)
                {
                    int n = Math.Min(bpStack, amount);
                    var bp = ItemManager.Create(bpDef, n);
                    bp.blueprintTarget = def.itemid;
                    p.GiveItem(bp, BaseEntity.GiveItemReason.PickedUp);
                    amount -= n;
                }
                return;
            }

            int stack = Math.Max(1, def.stackable);
            while (amount > 0)
            {
                int n = Math.Min(stack, amount);
                var it = ItemManager.Create(def, n, skin);
                if (it == null) return;
                if (!string.IsNullOrEmpty(itemName)) it.name = itemName;
                p.GiveItem(it, BaseEntity.GiveItemReason.PickedUp);
                amount -= n;
            }
        }

        #endregion

        #region Themes

        private Theme ActiveTheme()
        {
            if (!string.IsNullOrEmpty(config.ThemeMode) && !config.ThemeMode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                var forced = config.Themes.FirstOrDefault(x => string.Equals(x.Name, config.ThemeMode, StringComparison.OrdinalIgnoreCase));
                if (forced != null) return forced;
            }

            int today = DateTime.Now.Month * 100 + DateTime.Now.Day;
            Theme best = null;
            foreach (var t in config.Themes)
            {
                if (!InRange(t, today)) continue;
                if (best == null || t.Priority > best.Priority) best = t;
            }
            return best ?? config.Themes[0];
        }

        private static int ParseMD(string s, int fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            var parts = s.Split('-');
            int m, d;
            if (parts.Length == 2 && int.TryParse(parts[0], out m) && int.TryParse(parts[1], out d)) return m * 100 + d;
            return fallback;
        }

        private static bool InRange(Theme t, int today)
        {
            int from = ParseMD(t.From, 101), to = ParseMD(t.To, 1231);
            return from <= to ? today >= from && today <= to : today >= from || today <= to;
        }

        private string C(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return "1 1 1 1";
            if (hex.Contains(" ")) return hex;
            string result;
            if (colorCache.TryGetValue(hex, out result)) return result;
            try
            {
                string h = hex.TrimStart('#');
                int r = int.Parse(h.Substring(0, 2), NumberStyles.HexNumber);
                int g = int.Parse(h.Substring(2, 2), NumberStyles.HexNumber);
                int b = int.Parse(h.Substring(4, 2), NumberStyles.HexNumber);
                int a = h.Length >= 8 ? int.Parse(h.Substring(6, 2), NumberStyles.HexNumber) : 255;
                result = string.Format(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###} {3:0.###}", r / 255f, g / 255f, b / 255f, a / 255f);
            }
            catch { result = "1 1 1 1"; }
            colorCache[hex] = result;
            return result;
        }

        private static string Rich(string hex) => !string.IsNullOrEmpty(hex) && hex.StartsWith("#") ? hex : "#FFFFFF";

        #endregion

        #region Chat Command

        private void ChatShop(BasePlayer p, string command, string[] args)
        {
            if (!CanUse(p)) { SendReply(p, Msg("NoPermission", p)); return; }
            if (args.Length == 0 || !IsAdmin(p)) { OpenUi(p); return; }

            switch (args[0].ToLowerInvariant())
            {
                case "theme":
                {
                    if (args.Length < 2) break;
                    string mode = args[1];
                    if (!mode.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
                        !config.Themes.Any(t => string.Equals(t.Name, mode, StringComparison.OrdinalIgnoreCase)))
                    {
                        SendReply(p, Msg("ThemeUnknown", p, string.Join(", ", config.Themes.Select(t => t.Name))));
                        return;
                    }
                    config.ThemeMode = mode.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "Auto" : mode;
                    SaveConfig();
                    SendReply(p, Msg("ThemeSet", p, config.ThemeMode, ActiveTheme().Name));
                    return;
                }
                case "themes":
                {
                    var active = ActiveTheme();
                    SendReply(p, "Themes (mode: " + config.ThemeMode + "):\n" + string.Join("\n",
                        config.Themes.Select(t => $"{(t == active ? "> " : "  ")}{t.Name}  {t.From} to {t.To}  (priority {t.Priority})")));
                    return;
                }
                case "addcat":
                {
                    if (args.Length < 2) break;
                    string name = args[1];
                    if (data.Categories.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { SendReply(p, Msg("CategoryExists", p)); return; }
                    var added = new ShopCategory { Name = name, Icon = args.Length > 2 ? args[2] : "", Tab = "shop" };
                    MigrateCategory(added);
                    data.Categories.Add(added);
                    SaveData();
                    SendReply(p, Msg("CategoryAdded", p, name));
                    return;
                }
                case "delcat":
                {
                    if (args.Length < 2) break;
                    var cat = FindCategory(args[1]);
                    if (cat == null) { SendReply(p, Msg("BadValue", p)); return; }
                    data.Categories.Remove(cat);
                    SaveData();
                    SendReply(p, Msg("Deleted", p));
                    return;
                }
                case "add":
                {
                    int price, amount = 0;
                    if (args.Length < 3 || !int.TryParse(args[2], out price) || price < 0) break;
                    if (args.Length > 3) int.TryParse(args[3], out amount);
                    var cat = FindCategory(args[1]);
                    if (cat == null) { SendReply(p, Msg("BadValue", p)); return; }
                    var item = CreateFromHeld(p, cat, price, amount);
                    SendReply(p, item == null ? Msg("HoldItem", p) : Msg("ItemAdded", p, ItemLabel(item), cat.Name) + $" (id {item.Id})");
                    return;
                }
                case "remove":
                {
                    int id;
                    ShopCategory cat;
                    if (args.Length < 2 || !int.TryParse(args[1], out id)) break;
                    var item = FindItem(id, out cat);
                    if (item == null) { SendReply(p, Msg("ItemMissing", p)); return; }
                    cat.Items.Remove(item);
                    SaveData();
                    SendReply(p, Msg("Deleted", p));
                    return;
                }
                case "trade":
                {
                    int inAmt, outAmt;
                    if (args.Length < 5 || !int.TryParse(args[2], out inAmt) || !int.TryParse(args[4], out outAmt) || inAmt < 1 || outAmt < 1) break;
                    if ((!IsRP(args[1]) && Def(args[1]) == null) || (!IsRP(args[3]) && Def(args[3]) == null)) { SendReply(p, Msg("BadValue", p)); return; }
                    var ex = new Exchange { Id = data.NextId++, InShortname = args[1].ToLowerInvariant(), InAmount = inAmt, OutShortname = args[3].ToLowerInvariant(), OutAmount = outAmt };
                    data.Exchanges.Add(ex);
                    SaveData();
                    SendReply(p, $"Trade #{ex.Id} added: {inAmt} {NameOf(ex.InShortname)} -> {outAmt} {NameOf(ex.OutShortname)}");
                    return;
                }
                case "deltrade":
                {
                    int id;
                    if (args.Length < 2 || !int.TryParse(args[1], out id)) break;
                    int removed = data.Exchanges.RemoveAll(x => x.Id == id);
                    SaveData();
                    SendReply(p, removed > 0 ? Msg("Deleted", p) : Msg("TradeMissing", p));
                    return;
                }
            }
            SendReply(p, Msg("Help", p, command));
        }

        private ShopCategory FindCategory(string key)
        {
            int idx;
            if (int.TryParse(key, out idx) && idx >= 1 && idx <= data.Categories.Count) return data.Categories[idx - 1];
            return data.Categories.FirstOrDefault(c => c.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
        }

        private ShopItem CreateFromHeld(BasePlayer p, ShopCategory cat, int price, int amount)
        {
            var held = SourceItem(p);
            if (held == null) return null;

            var def = held.info;
            bool bp = false;
            if (held.IsBlueprint() && held.blueprintTargetDef != null)
            {
                def = held.blueprintTargetDef;
                bp = true;
            }

            var item = new ShopItem
            {
                Id = data.NextId++,
                Shortname = def.shortname,
                Skin = bp ? 0 : held.skin,
                Amount = amount > 0 ? amount : Math.Max(1, held.amount),
                Price = price,
                ItemName = bp ? "" : (held.name ?? ""),
                Blueprint = bp
            };
            cat.Items.Add(item);
            SaveData();
            return item;
        }

        #endregion

        #region Actions

        private static string DescriptionIdentity(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string text = value.ToLowerInvariant().Replace("patrol helicopter", "helisignal")
                .Replace("patrol heli", "helisignal").Replace("heli signal", "helisignal")
                .Replace("bradley signal", "bradsignal").Replace("brad signal", "bradsignal")
                .Replace("multi heli", "multiheli").Replace("heli wave", "heliwave")
                .Replace("bradley wave", "bradwave").Replace("brad wave", "bradwave");
            var words = System.Text.RegularExpressions.Regex.Split(text, @"[^\p{L}\p{Nd}]+").Where(x => x.Length > 0).OrderBy(x => x, StringComparer.Ordinal);
            return string.Concat(words);
        }

        private string ItemDescription(ShopItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.Description)) return item.Description;
            if (config.Descriptions == null) return "";
            var candidates = new[] { item.DescriptionKey, item.ItemName, item.DisplayName,
                item.Shortname, IsEvent(item) ? item.Type + "_" + item.EventArg : "" };
            foreach (var candidate in candidates)
            {
                string identity = DescriptionIdentity(candidate);
                if (identity.Length == 0) continue;
                foreach (var entry in config.Descriptions)
                    if (DescriptionIdentity(entry.Key) == identity) return entry.Value ?? "";
            }
            return "";
        }

        private int PopupMax(BasePlayer p, ShopItem item)
        {
            if (item.LimitToOne || IsEvent(item)) return 1;
            string reason;
            int max = Math.Min(Math.Max(1, Math.Min(1000, config.MaxPurchaseQuantity)), Math.Max(1, Remaining(p, item, out reason)));
            if (item.Amount > 0) max = Math.Min(max, int.MaxValue / item.Amount);
            if (item.Price > 0) max = Math.Min(max, int.MaxValue / item.Price);
            return Math.Max(1, max);
        }

        private void OpenPurchase(BasePlayer p, UiState s, int itemId)
        {
            ShopCategory cat;
            var item = FindItem(itemId, out cat);
            if (item == null || cat == null) return;
            if (HiddenFromPlayer(p, cat)) { SetStatus(s, Msg("HiddenCategory", p), true); return; }
            if (!CanSee(p, cat.Permission) || !CanSee(p, item.Permission) || (!InSeason(cat) && !IsAdmin(p))) return;
            s.EditItem = s.EditTrade = 0;
            s.PickTarget = null;
            s.EventMenu = s.DescriptionEdit = false;
            s.BuyItem = itemId;
            s.BuyToken = Guid.NewGuid().ToString("N");
            s.BuyQty = 1;
        }

        // Purchase modal. Description viewport is 68px, about 40% of the previous 162px box.
        private const int PurchaseDescH = 68;
        private const int PurchaseIcon = 72;
        private const int PurchaseDescW = 400;
        private const int PurchaseDescX = 80;

        private struct PurchaseFrame
        {
            public int ModalH;
            public int IconY;
            public int DescY;
            public int QtyY;
            public int QtyH;
            public int RowY;
            public int TypeY;
            public bool Slider;
        }

        private static PurchaseFrame PurchaseFrameFor(bool slider)
        {
            if (!slider)
                return new PurchaseFrame { ModalH = 307, IconY = 178, DescY = 102, QtyY = 62, QtyH = 32 };
            return new PurchaseFrame { ModalH = 381, IconY = 252, DescY = 176, QtyY = 140, QtyH = 26, RowY = 100, TypeY = 62, Slider = true };
        }

        private string PurchaseTotalRich(BasePlayer p, ShopItem item, int qty, ThemeColors tc)
        {
            long cost = (long)Math.Max(0, item.Price) * Math.Max(1, qty);
            string price = IsEvent(item) && item.ChargedByPlugin && item.PluginPricing ? "PRICED BY EVENT PLUGIN" : cost.ToString("N0") + " RP";
            string color = Rich(cost > Balance(p) ? tc.Danger : tc.Accent);
            return "<color=" + color + ">TOTAL: " + price + "</color>";
        }

        private void DrawQtyLine(CuiElementContainer c, string parent, BasePlayer p, UiState s, ShopItem item, ThemeColors tc, PurchaseFrame frame)
        {
            string name = UiModal + ".QtyLine";
            Box(c, parent, "0 0 0 0", R(16, frame.QtyY, 544, frame.QtyY + frame.QtyH), name);
            string total = PurchaseTotalRich(p, item, s.BuyQty, tc);
            string text = frame.Slider
                ? "QUANTITY: " + s.BuyQty + "  \u2022  " + total + "  \u2022  drag the handle, click the bar, or enter a number"
                : "QUANTITY: 1  \u2022  This listing is limited to one per purchase.  \u2022  " + total;
            Label(c, name, text, 11, tc.SubText, TextAnchor.MiddleCenter, Full(), false);
        }

        private void BuildPurchasePopup(CuiElementContainer c, BasePlayer p, UiState s, ThemeColors tc)
        {
            ShopCategory cat;
            var item = FindItem(s.BuyItem, out cat);
            if (item == null || HiddenFromPlayer(p, cat)) { s.BuyItem = 0; s.BuyToken = null; return; }
            int max = PopupMax(p, item);
            s.BuyQty = Mathf.Clamp(s.BuyQty, 1, max);
            var frame = PurchaseFrameFor(max > 1);
            string box = ModalBox(c, tc, 560, frame.ModalH, "PURCHASE \u2022 " + ItemLabel(item));
            int iconX = PurchaseDescX + (PurchaseDescW - PurchaseIcon) / 2;
            ListIcon(c, box, item.Icon, CardIcon(item), IsEvent(item) ? 0 : item.Skin, R(iconX, frame.IconY, iconX + PurchaseIcon, frame.IconY + PurchaseIcon));
            string desc = ItemDescription(item);
            if (string.IsNullOrWhiteSpace(desc)) desc = "No additional description has been provided for this item.";
            int textHeight = Math.Max(PurchaseDescH, ((desc.Length + 41) / 42 + desc.Count(ch => ch == '\n')) * 20);
            string descriptionArea = Scroll(c, box, R(PurchaseDescX, frame.DescY, PurchaseDescX + PurchaseDescW, frame.DescY + PurchaseDescH), PurchaseDescH, textHeight, tc);
            Label(c, descriptionArea, desc, 14, tc.Text, TextAnchor.UpperLeft, TL(8, 6, PurchaseDescW - 28, textHeight - 8), false);
            DrawQtyLine(c, box, p, s, item, tc, frame);
            if (frame.Slider)
            {
                int row = frame.RowY;
                Btn(c, box, $"{UiCmd} buyqty {s.BuyToken} {Math.Max(1, s.BuyQty - 1)}", tc.Button, R(24, row, 64, row + 32), "\u2212", 22, tc.ButtonText);
                Btn(c, box, $"{UiCmd} buyqty {s.BuyToken} {Math.Min(max, s.BuyQty + 1)}", tc.Button, R(496, row, 536, row + 32), "+", 22, tc.ButtonText);
                string track = Box(c, box, tc.Panel, R(100, row + 4, 460, row + 28));
                Box(c, track, tc.Button, R(12, 9, 348, 15));
                for (int i = 0; i <= 24; i++)
                {
                    int q = 1 + Mathf.RoundToInt((max - 1) * i / 24f);
                    Btn(c, track, $"{UiCmd} buyqty {s.BuyToken} {q}", "#00000000", R(i * 14, 0, i * 14 + 24, 24), null, 0, null);
                }
                int left = Mathf.RoundToInt((s.BuyQty - 1f) / (max - 1) * 336f);
                c.Add(new CuiElement
                {
                    Name = "NShop.QtyThumb." + s.BuyToken, Parent = track,
                    Components =
                    {
                        new CuiImageComponent { Color = C(tc.Accent), BlocksRaycast = true },
                        R(left, 0, left + 24, 24),
                        new CuiDraggableComponent
                        {
                            LimitToParent = true, ParentLimitIndex = 1, ParentPadding = "0 0",
                            DropAnywhere = true, AllowSwapping = false, KeepOnTop = false,
                            DragAlpha = 1f, MaxDistance = -1f,
                            PositionRPC = CommunityEntity.DraggablePositionSendType.NormalizedParent
                        }
                    }
                });
                int type = frame.TypeY;
                Label(c, box, "TYPE QTY", 11, tc.SubText, TextAnchor.MiddleRight, R(50, type, 150, type + 30));
                Input(c, box, s.BuyQty.ToString(), $"{UiCmd} buyqty {s.BuyToken}", tc.Card, tc.Text, R(160, type, 245, type + 30), 14, 4);
                Label(c, box, "1\u2013" + max + " purchases \u2022 Enter to apply", 11, tc.SubText, TextAnchor.MiddleLeft, R(258, type, 530, type + 30), false);
            }
            Btn(c, box, $"{UiCmd} buycancel", tc.Button, R(20, 16, 200, 54), "CANCEL", 14, tc.ButtonText);
            Btn(c, box, $"{UiCmd} buyconfirm {s.BuyToken}", tc.Accent, R(216, 16, 540, 54), "CONFIRM PURCHASE", 14, tc.AccentText);
        }

        private void OnCuiDraggableDrag(BasePlayer player, string name, Vector3 position, CommunityEntity.DraggablePositionSendType type)
        {
            if (player == null || !CanUse(player) || type != CommunityEntity.DraggablePositionSendType.NormalizedParent
                || float.IsNaN(position.x) || float.IsInfinity(position.x)) return;
            UiState s;
            if (!states.TryGetValue(player.userID, out s) || !s.Open || s.BuyItem == 0 || string.IsNullOrEmpty(s.BuyToken)
                || name != "NShop.QtyThumb." + s.BuyToken) return;
            ShopCategory cat;
            var item = FindItem(s.BuyItem, out cat);
            if (item == null) return;
            int max = PopupMax(player, item);
            if (max <= 1) return;
            float fraction = Mathf.Clamp01((position.x * 360f - 12f) / 336f);
            s.BuyQty = Mathf.Clamp(1 + Mathf.RoundToInt(fraction * (max - 1)), 1, max);
            if (Time.realtimeSinceStartup - s.LastQtyDraw < 0.1f) return;
            s.LastQtyDraw = Time.realtimeSinceStartup;
            CuiHelper.DestroyUi(player, UiModal + ".QtyLine");
            var c = new CuiElementContainer();
            DrawQtyLine(c, UiModal + ".Box", player, s, item, ActiveTheme().Colors, PurchaseFrameFor(true));
            CuiHelper.AddUi(player, c);
        }

        private void OnCuiDraggableDrop(BasePlayer player, string draggedName, string draggedSlot, string swappedName, string swappedSlot)
        {
            if (player == null) return;
            UiState s;
            if (states.TryGetValue(player.userID, out s) && s.Open && s.BuyItem != 0 && !string.IsNullOrEmpty(s.BuyToken)
                && draggedName == "NShop.QtyThumb." + s.BuyToken)
            {
                // Redraw only the modal after release; never destroy an actively dragged handle.
                CuiHelper.DestroyUi(player, UiModal);
                var c = new CuiElementContainer();
                BuildPurchasePopup(c, player, s, ActiveTheme().Colors);
                CuiHelper.AddUi(player, c);
            }
        }

        private void BuildDescriptionEditor(CuiElementContainer c, UiState s, ThemeColors tc)
        {
            ShopCategory cat;
            var item = FindItem(s.EditItem, out cat);
            if (item == null) { s.DescriptionEdit = false; return; }
            string box = ModalBox(c, tc, 740, 490, "DESCRIPTION \u2022 " + ItemLabel(item));
            string cmd = $"{UiCmd} iset {item.Id}";
            Label(c, box, "CUSTOM DESCRIPTION \u2022 type/paste, then press Enter", 12, tc.SubText, TextAnchor.MiddleLeft, R(20, 399, 720, 429));
            Input(c, box, item.Description, cmd + " description", tc.Card, tc.Text, R(20, 357, 720, 397), 13, 1200);
            Label(c, box, "IMPORTED KEY \u2022 e.g. helisignal_easy, mlrs_truck, adv_large_planter_t1", 12, tc.SubText, TextAnchor.MiddleLeft, R(20, 315, 720, 345));
            Input(c, box, item.DescriptionKey, cmd + " descriptionkey", tc.Card, tc.Text, R(20, 277, 520, 313), 13, 100);
            Btn(c, box, cmd + " cleardescription", tc.Button, R(534, 277, 720, 313), "USE IMPORTED TEXT", 11, tc.ButtonText);
            Btn(c, box, cmd + " carddescription", item.ShowDescriptionOnCard ? tc.Accent : tc.Button, R(20, 224, 380, 262),
                item.ShowDescriptionOnCard ? "CARD INFO BOX: ON" : "CARD INFO BOX: OFF", 13, tc.ButtonText);
            Btn(c, box, cmd + " one", item.LimitToOne ? tc.Accent : tc.Button, R(394, 224, 720, 262),
                item.LimitToOne ? "QTY SLIDER: OFF (LIMIT 1)" : "QTY SLIDER: ON", 12, tc.ButtonText);
            string preview = Box(c, box, tc.Panel, R(20, 76, 720, 210));
            Label(c, preview, ItemDescription(item), 13, tc.Text, TextAnchor.UpperLeft, R(12, 8, 688, 126), false);
            Label(c, box, "Custom text overrides imported text. Card boxes show an excerpt; the purchase popup shows the full description.", 11, tc.SubText, TextAnchor.MiddleLeft, R(20, 53, 720, 74), false);
            Btn(c, box, $"{UiCmd} descriptionback", tc.Button, R(500, 14, 720, 48), "BACK TO LISTING", 13, tc.ButtonText);
        }

        private void Purchase(BasePlayer p, int itemId, int requestedQuantity = 1)
        {
            var s = S(p);
            ShopCategory cat;
            var item = FindItem(itemId, out cat);
            if (item == null) { SetStatus(s, Msg("ItemMissing", p), true); return; }
            if (ServerRewards == null) { SetStatus(s, Msg("NoServerRewards", p), true); return; }
            if (HiddenFromPlayer(p, cat)) { SetStatus(s, Msg("HiddenCategory", p), true); Fx(p, FxDeny); return; }
            if (!CanSee(p, cat.Permission) || !CanSee(p, item.Permission) || (!InSeason(cat) && !IsAdmin(p))) { SetStatus(s, Msg("NoAccess", p), true); Fx(p, FxDeny); return; }
            if (item.GiveItem && item.RequireOwnership && !Owns(p, Def(item.Shortname))) { SetStatus(s, Msg("NotOwned", p, ItemLabel(item)), true); Fx(p, FxDeny); return; }

            string reason;
            int remaining = Remaining(p, item, out reason);
            if (remaining <= 0) { SetStatus(s, Msg("LimitReached", p, reason), true); Fx(p, FxDeny); return; }

            if (IsEvent(item)) { PurchaseEvent(p, s, item); return; }

            int qty = Math.Max(1, requestedQuantity);
            if (qty > remaining || qty > PopupMax(p, item))
            {
                SetStatus(s, "The selected quantity exceeds this listing's current purchase limit. Please select it again.", true);
                return;
            }
            long cost = (long)item.Price * qty;
            int bal = Balance(p);
            if (cost < 0 || cost > int.MaxValue || bal < cost)
            {
                SetStatus(s, Msg("NotEnoughRP", p, cost.ToString("N0"), bal.ToString("N0")), true);
                Fx(p, FxDeny);
                return;
            }

            ItemDefinition def = null;
            if (item.GiveItem)
            {
                def = Def(item.Shortname);
                if (def == null || item.Amount <= 0 || (long)item.Amount * qty > int.MaxValue) { SetStatus(s, Msg("InvalidItem", p, item.Shortname), true); return; }
                if (SlotsNeeded(def, (long)item.Amount * qty, item.Blueprint) > FreeSlots(p))
                {
                    SetStatus(s, Msg("NoSpace", p), true);
                    Fx(p, FxDeny);
                    return;
                }
            }

            if (!TakeRP(p.userID, (int)cost))
            {
                SetStatus(s, Msg("NotEnoughRP", p, cost.ToString("N0"), bal.ToString("N0")), true);
                Fx(p, FxDeny);
                return;
            }

            if (item.GiveItem && !TryDeliverItems(p, def, item.Skin, item.Amount * qty, item.Blueprint, item.ItemName))
            {
                var refund = new PendingRefund { UserId = p.userID, UserName = p.displayName,
                    Amount = (int)cost, Label = ItemLabel(item), RefundDue = true };
                pendingRefunds.Add(refund);
                SaveRefunds();
                Refund(refund);
                SetStatus(s, "Delivery failed. Your RP refund was issued or queued for retry.", true);
                Fx(p, FxDeny);
                return;
            }

            if (item.Commands != null && item.Commands.Count > 0)
            {
                for (int i = 0; i < qty; i++)
                    foreach (var c in item.Commands)
                        ConsoleSystem.Run(ConsoleSystem.Option.Server, Fill(c, p));
            }

            Record(p, item, qty);

            SetStatus(s, Msg("Purchased", p, ItemLabel(item), item.Amount * qty, cost.ToString("N0")), false);
            Fx(p, FxBuy);
            Log($"{p.displayName} ({p.UserIDString}) bought {ItemLabel(item)} x{item.Amount * qty} for {cost} RP");
        }

        private void PurchaseEvent(BasePlayer p, UiState s, ShopItem item)
        {
            bool rb = item.Type == "raidable";
            var plugin = rb ? RaidableBases : DungeonEvents;
            string pluginName = rb ? "RaidableBases" : "DungeonEvents";
            string label = ItemLabel(item);

            if (plugin == null) { SetStatus(s, Msg("PluginMissing", p, pluginName), true); Fx(p, FxDeny); return; }
            if (string.IsNullOrEmpty(item.EventArg)) { SetStatus(s, Msg("BadValue", p), true); return; }

            string template = item.ChargedByPlugin
                ? (rb ? config.Events.RbBuyCommand : config.Events.DeBuyCommand)
                : (rb ? config.Events.RbFreeCommand : config.Events.DeFreeCommand);
            if (!item.ChargedByPlugin && !rb && template.TrimStart().StartsWith("buydungeon", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus(s, "Dungeon free command is still buydungeon. Admin: set it to spawnfixeddungeon {arg} {steamid} before using shop billing.", true);
                return;
            }
            string command = Fill(template.Replace("{arg}", item.EventArg), p);

            if (item.ChargedByPlugin)
            {
                // The event plugin takes the RP itself, enforces its own cooldowns/limits and refunds failed spawns.
                ConsoleSystem.Run(ConsoleSystem.Option.Server, command);
                Record(p, item, 1);
                SetStatus(s, Msg("EventRequested", p, label, pluginName), false);
                Fx(p, FxBuy);
                Log($"{p.displayName} ({p.UserIDString}) requested {label} via {pluginName} ({command})");
                timer.Once(2f, () => { if (p != null && p.IsConnected && S(p).Open) OpenUi(p); });
                return;
            }

            if (pendingRefunds.Any(r => r.UserId == p.userID))
            {
                SetStatus(s, "Your previous purchase is still spawning or awaiting an RP refund. Try again shortly.", true);
                return;
            }

            int cost = Math.Max(0, item.Price);
            int bal = Balance(p);
            if (bal < cost || !TakeRP(p.userID, cost))
            {
                SetStatus(s, Msg("NotEnoughRP", p, cost.ToString("N0"), bal.ToString("N0")), true);
                Fx(p, FxDeny);
                return;
            }

            // Register BEFORE dispatch: a plugin may confirm synchronously during the command.
            var pr = new PendingRefund { UserId = p.userID, UserName = p.displayName,
                Amount = cost, Label = label, Type = item.Type, EventArg = item.EventArg, Listing = item,
                Deadline = DateTime.UtcNow.AddSeconds(Math.Max(10f, rb ? config.Events.RbRefundTimeout : config.Events.DeRefundTimeout)) };
            pendingRefunds.Add(pr);
            SaveRefunds();
            pr.Timer = timer.Once((float)(pr.Deadline - DateTime.UtcNow).TotalSeconds, () => Refund(pr));
            try { ConsoleSystem.Run(ConsoleSystem.Option.Server, command); }
            catch (Exception ex)
            {
                PrintWarning($"Event command failed for {p.userID}: {ex.Message}");
                Refund(pr);
                SetStatus(s, "Event request failed. Your RP refund was issued or queued for retry.", true);
                return;
            }

            SetStatus(s, Msg("EventBought", p, label, cost.ToString("N0")), false);
            Fx(p, FxBuy);
            Log($"{p.displayName} ({p.UserIDString}) bought {label} for {cost} RP ({command})");
        }

        private void Refund(PendingRefund r)
        {
            if (!pendingRefunds.Contains(r)) return;
            r.Timer?.Destroy();
            r.RefundDue = true;
            SaveRefunds();
            bool credited = false;
            try { credited = AddRP(r.UserId, r.Amount); }
            catch (Exception ex) { PrintWarning($"RP refund error for {r.UserId}: {ex.Message}"); }
            if (!credited)
            {
                PrintWarning($"Refund pending: {r.Amount} RP owed to {r.UserId} for {r.Label}; retrying when ServerRewards is available.");
                if (!unloading) r.Timer = timer.Once(30f, () => Refund(r));
                return;
            }
            pendingRefunds.Remove(r);
            SaveRefunds();
            var bp = BasePlayer.FindByID(r.UserId);
            if (bp != null) SendReply(bp, $"Delivery of {r.Label} failed. {r.Amount:N0} RP was refunded.");
            Log($"Refunded {r.Amount} RP to {r.UserName} ({r.UserId}) for {r.Label}");
        }

        private void DoTrade(BasePlayer p, int tradeId, string mode)
        {
            var s = S(p);
            var ex = data.Exchanges.Find(x => x.Id == tradeId);
            if (ex == null) { SetStatus(s, Msg("TradeMissing", p), true); return; }

            bool inRp = IsRP(ex.InShortname), outRp = IsRP(ex.OutShortname);
            var inDef = inRp ? null : Def(ex.InShortname);
            var outDef = outRp ? null : Def(ex.OutShortname);
            if ((!inRp && inDef == null) || (!outRp && outDef == null) || ex.InAmount < 1 || ex.OutAmount < 1)
            {
                SetStatus(s, Msg("InvalidItem", p, inDef == null && !inRp ? ex.InShortname : ex.OutShortname), true);
                return;
            }
            if ((inRp || outRp) && ServerRewards == null) { SetStatus(s, Msg("NoServerRewards", p), true); return; }

            int have = inRp ? Balance(p) : CountItems(p, inDef, ex.InSkin);
            int maxTimes = have / ex.InAmount;
            bool isMax = mode == "max";
            int times;
            if (isMax) times = maxTimes;
            else if (!int.TryParse(mode, out times) || times < 1) times = 1;

            if (times < 1 || times > maxTimes)
            {
                SetStatus(s, Msg("NotEnoughTrade", p, $"{((long)ex.InAmount * Math.Max(1, times)):N0} {NameOf(ex.InShortname)}"), true);
                Fx(p, FxDeny);
                return;
            }

            if (!outRp)
            {
                int free = FreeSlots(p);
                if (SlotsNeeded(outDef, (long)ex.OutAmount * times, false) > free)
                {
                    if (!isMax) { SetStatus(s, Msg("NoSpace", p), true); Fx(p, FxDeny); return; }
                    long fit = (long)free * Math.Max(1, outDef.stackable) / ex.OutAmount;
                    times = (int)Math.Min(times, fit);
                    if (times < 1) { SetStatus(s, Msg("NoSpace", p), true); Fx(p, FxDeny); return; }
                }
            }

            long inTotal = (long)ex.InAmount * times;
            long outTotal = (long)ex.OutAmount * times;
            if (inTotal > int.MaxValue || outTotal > int.MaxValue) { SetStatus(s, Msg("BadValue", p), true); return; }

            if (inRp)
            {
                if (!TakeRP(p.userID, (int)inTotal)) { SetStatus(s, Msg("NotEnoughTrade", p, $"{inTotal:N0} RP"), true); return; }
            }
            else TakeItems(p, inDef, ex.InSkin, (int)inTotal);

            if (inRp && !outRp)
            {
                if (!TryDeliverItems(p, outDef, ex.OutSkin, (int)outTotal, false, ex.OutName))
                {
                    var refund = new PendingRefund { UserId = p.userID, UserName = p.displayName,
                        Amount = (int)inTotal, Label = NameOf(ex.OutShortname, ex.OutName), RefundDue = true };
                    pendingRefunds.Add(refund);
                    SaveRefunds();
                    Refund(refund);
                    SetStatus(s, "Delivery failed. Your RP refund was issued or queued for retry.", true);
                    return;
                }
            }
            else if (outRp) AddRP(p.userID, (int)outTotal);
            else GiveItems(p, outDef, ex.OutSkin, (int)outTotal, false, ex.OutName);

            string given = $"{inTotal:N0} {NameOf(ex.InShortname)}";
            string got = $"{outTotal:N0} {NameOf(ex.OutShortname, ex.OutName)}";
            SetStatus(s, Msg("Traded", p, given, got), false);
            Fx(p, FxTrade);
            Log($"{p.displayName} ({p.UserIDString}) traded {given} for {got}");
        }

        #endregion

        #region UI Command

        [ConsoleCommand(UiCmd)]
        private void CmdUi(ConsoleSystem.Arg arg)
        {
            var p = arg.Player();
            if (p == null || !arg.HasArgs()) return;
            if (!CanUse(p)) return;

            var s = S(p);
            string action = arg.GetString(0).ToLowerInvariant();

            float now = Time.realtimeSinceStartup;
            string full = string.Join(" ", arg.Args);
            if (action != "close" && full == s.LastCommand && now - s.LastClick < config.ClickCooldown) return;
            s.LastClick = now;
            s.LastCommand = full;

            bool admin = IsAdmin(p);

            switch (action)
            {
                case "close":
                    s.BuyItem = 0;
                    s.BuyToken = null;
                    s.DescriptionEdit = false;
                    s.EditItem = s.EditTrade = 0;
                    s.EditKitId = null;
                    s.PickTarget = null;
                    s.EventMenu = false;
                    s.Open = false;
                    CuiHelper.DestroyUi(p, UiMain);
                    return;
                case "tab":
                    s.BuyItem = 0;
                    s.BuyToken = null;
                    int requested = arg.GetInt(1);
                    if (requested == 3 && CanExchange(p)) s.Tab = 3;
                    else if (requested == 2 && CanSeeKits(p)) s.Tab = 2;
                    else if (requested == 1) s.Tab = 1;
                    else s.Tab = 0;
                    s.EditItem = s.EditTrade = 0;
                    s.EditKitId = null;
                    s.TradeCat = TradeTiles;
                    s.Status = "";
                    break;
                case "kitclaim":
                    ClaimKit(p, s, arg.GetString(1, ""));
                    break;
                case "tcat":
                    s.TradeCat = Mathf.Clamp(arg.GetInt(1, TradeTiles), TradeOther, data.TradeCategories.Count - 1);
                    s.TradeSel = 0;
                    s.Status = "";
                    break;
                case "tback":
                    s.TradeCat = TradeTiles;
                    s.EditTrade = 0;
                    s.Status = "";
                    break;
                case "cat":
                    s.Cat = Mathf.Clamp(arg.GetInt(1), 0, Math.Max(0, data.Categories.Count - 1));
                    s.Status = "";
                    break;
                case "mult":
                    int m = arg.GetInt(1, 1);
                    if (config.Multipliers.Contains(m)) s.Mult = m;
                    break;
                case "buy":
                    OpenPurchase(p, s, arg.GetInt(1));
                    break;
                case "buycancel":
                    s.BuyItem = 0;
                    s.BuyToken = null;
                    break;
                case "buyqty":
                {
                    if (s.BuyItem == 0 || arg.GetString(1) != s.BuyToken) return;
                    ShopCategory purchaseCategory;
                    var purchaseItem = FindItem(s.BuyItem, out purchaseCategory);
                    if (purchaseItem == null) return;
                    int selected;
                    if (!int.TryParse(arg.GetString(2), out selected)) return;
                    s.BuyQty = Mathf.Clamp(selected, 1, PopupMax(p, purchaseItem));
                    break;
                }
                case "buyconfirm":
                {
                    if (s.BuyItem == 0 || arg.GetString(1) != s.BuyToken) return;
                    int purchaseId = s.BuyItem;
                    int purchaseQty = s.BuyQty;
                    s.BuyItem = 0;
                    s.BuyToken = null; // consume before delivery, so repeated confirm commands cannot repeat the purchase
                    Purchase(p, purchaseId, purchaseQty);
                    break;
                }
                case "tsel":
                {
                    int id = arg.GetInt(1);
                    string q = arg.GetString(2, "1").ToLowerInvariant();
                    if (q != "1" && q != "10" && q != "max") q = "1";
                    var ex = data.Exchanges.Find(x => x.Id == id);
                    if (ex == null) { SetStatus(s, Msg("TradeMissing", p), true); break; }

                    int have = TradeHave(p, ex);
                    int times = q == "10" ? 10 : 1;
                    if (ex.InAmount < 1 || have < (long)ex.InAmount * times)
                    {
                        s.TradeSel = 0;
                        long need = (long)Math.Max(1, ex.InAmount) * times;
                        SetStatus(s, Msg("TradeNeed", p, need.ToString("N0"), NameOf(ex.InShortname), have.ToString("N0")), true);
                        Fx(p, FxDeny);
                        break;
                    }
                    if (s.TradeSel == id && s.TradeQty == q) s.TradeSel = 0;   // click again to deselect
                    else { s.TradeSel = id; s.TradeQty = q; s.Status = ""; }
                    break;
                }
                case "thint":
                    SetStatus(s, Msg("PickQty", p), true);
                    break;
                case "trade":
                    if (!CanExchange(p)) return;
                    DoTrade(p, arg.GetInt(1), arg.GetString(2, "1").ToLowerInvariant());
                    s.TradeSel = 0;
                    break;
                case "admin":
                    if (!admin) return;
                    s.Admin = !s.Admin;
                    s.EditItem = s.EditTrade = 0;
                    s.EditKitId = null;
                    s.PickTarget = null;
                    s.EventMenu = false;
                    break;
                default:
                    if (!admin || !s.Admin) return;
                    HandleAdmin(p, s, arg, action);
                    break;
            }
            OpenUi(p);
        }

        private static string Rest(ConsoleSystem.Arg arg, int from)
        {
            if (arg.Args == null || arg.Args.Length <= from) return "";
            return string.Join(" ", arg.Args.Skip(from)).Trim();
        }

        private void HandleAdmin(BasePlayer p, UiState s, ConsoleSystem.Arg arg, string action)
        {
            switch (action)
            {
                case "addcat":
                {
                    string name = Rest(arg, 1);
                    if (string.IsNullOrEmpty(name)) return;
                    if (data.Categories.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { SetStatus(s, Msg("CategoryExists", p), true); return; }
                    var added = new ShopCategory { Name = name, Tab = s.Tab == 1 ? "events" : "shop" };
                    MigrateCategory(added);
                    data.Categories.Add(added);
                    s.Cat = data.Categories.Count - 1;
                    SaveData();
                    SetStatus(s, Msg("CategoryAdded", p, name), false);
                    return;
                }
                case "delcat":
                {
                    int idx = arg.GetInt(1, -1);
                    if (idx < 0 || idx >= data.Categories.Count) return;
                    if (!Confirm(s, "cat" + idx)) { SetStatus(s, Msg("ConfirmDelete", p) + $" ({data.Categories[idx].Name}, {data.Categories[idx].Items.Count} items)", true); return; }
                    data.Categories.RemoveAt(idx);
                    s.Cat = Mathf.Clamp(s.Cat, 0, Math.Max(0, data.Categories.Count - 1));
                    SaveData();
                    SetStatus(s, Msg("Deleted", p), false);
                    return;
                }
                case "catperm":
                {
                    if (s.Cat < 0 || s.Cat >= data.Categories.Count) return;
                    data.Categories[s.Cat].Permission = Rest(arg, 1);
                    RegisterPerm(data.Categories[s.Cat].Permission);
                    SaveData();
                    SetStatus(s, Msg("Saved", p), false);
                    return;
                }
                case "catthemes":
                {
                    if (s.Cat < 0 || s.Cat >= data.Categories.Count) return;
                    data.Categories[s.Cat].Themes = Rest(arg, 1)
                        .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => config.Themes.FirstOrDefault(x => string.Equals(x.Name, t, StringComparison.OrdinalIgnoreCase))?.Name ?? t)
                        .Distinct().ToList();
                    SaveData();
                    SetStatus(s, data.Categories[s.Cat].Themes.Count == 0 ? "Category is now visible all year." : "Category shows during: " + string.Join(", ", data.Categories[s.Cat].Themes), false);
                    return;
                }
                case "cattab":
                {
                    if (s.Cat < 0 || s.Cat >= data.Categories.Count) return;
                    string chosen = arg.GetString(1, "");
                    if (!string.Equals(chosen, "shop", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(chosen, "events", StringComparison.OrdinalIgnoreCase))
                    {
                        SetStatus(s, Msg("BadValue", p), true);
                        return;
                    }
                    string norm = string.Equals(chosen, "events", StringComparison.OrdinalIgnoreCase) ? "events" : "shop";
                    data.Categories[s.Cat].Tab = norm;
                    s.Tab = norm == "events" ? 1 : 0;
                    SaveData();
                    SetStatus(s, "Category tab set to " + norm + ".", false);
                    return;
                }
                case "cathidden":
                {
                    if (s.Cat < 0 || s.Cat >= data.Categories.Count) return;
                    var hiddenCat = data.Categories[s.Cat];
                    hiddenCat.AdminModeOnly = hiddenCat.AdminModeOnly != true;
                    SaveData();
                    SetStatus(s, hiddenCat.AdminModeOnly == true ? "Category is hidden from players." : "Category is visible to players.", false);
                    return;
                }
                case "caticontext":
                {
                    if (s.Cat < 0 || s.Cat >= data.Categories.Count) return;
                    data.Categories[s.Cat].Icon = Rest(arg, 1);
                    SaveData();
                    SetStatus(s, Msg("Saved", p), false);
                    return;
                }
                case "caticon":
                {
                    if (s.Cat < 0 || s.Cat >= data.Categories.Count) return;
                    var held = SourceItem(p);
                    if (held == null) { SetStatus(s, Msg("HoldItem", p), true); return; }
                    data.Categories[s.Cat].Icon = held.info.shortname;
                    SaveData();
                    SetStatus(s, Msg("Saved", p), false);
                    return;
                }
                case "additem":
                {
                    if (data.Categories.Count == 0) { SetStatus(s, Msg("NoCategory", p), true); return; }
                    var cat = data.Categories[Mathf.Clamp(s.Cat, 0, data.Categories.Count - 1)];
                    var item = CreateFromHeld(p, cat, config.DefaultPrice, 0);
                    if (item == null) { SetStatus(s, Msg("HoldItem", p), true); return; }
                    s.EditItem = item.Id;
                    SetStatus(s, Msg("ItemAdded", p, ItemLabel(item), cat.Name), false);
                    return;
                }
                case "description":
                    s.EditItem = arg.GetInt(1);
                    s.DescriptionEdit = true;
                    return;
                case "descriptionback":
                    s.DescriptionEdit = false;
                    return;
                case "edit":
                    s.DescriptionEdit = false;
                    s.EditItem = arg.GetInt(1);
                    s.EditTrade = 0;
                    return;
                case "done":
                    s.DescriptionEdit = false;
                    s.EditItem = s.EditTrade = 0;
                    s.EditKitId = null;
                    s.PickTarget = null;
                    s.EventMenu = false;
                    return;
                case "kitnew":
                {
                    if (kitFile.Kits == null) kitFile.Kits = new List<KitDef>();
                    var created = new KitDef
                    {
                        Id = "kit" + kitFile.NextId++,
                        Name = "New Kit",
                        Icon = "supply.signal",
                        Permission = "",
                        CooldownSeconds = 86400
                    };
                    kitFile.Kits.Add(created);
                    SaveKits();
                    s.EditKitId = created.Id;
                    s.KitShort = "";
                    s.KitAmt = "1";
                    s.KitSkin = "0";
                    SetStatus(s, "Kit created. Empty permission means anyone who can open Kits.", false);
                    return;
                }
                case "kitedit":
                    s.EditKitId = arg.GetString(1, "");
                    s.EditItem = s.EditTrade = 0;
                    s.KitShort = "";
                    s.KitAmt = "1";
                    s.KitSkin = "0";
                    return;
                case "kitdel":
                {
                    var kit = FindKit(arg.GetString(1, ""));
                    if (kit == null) return;
                    if (!Confirm(s, "kit" + kit.Id)) { SetStatus(s, Msg("ConfirmDelete", p) + " (" + kit.Name + ")", true); return; }
                    kitFile.Kits.Remove(kit);
                    if (s.EditKitId == kit.Id) s.EditKitId = null;
                    SaveKits();
                    SetStatus(s, Msg("Deleted", p), false);
                    return;
                }
                case "kitset":
                {
                    var kit = FindKit(arg.GetString(1, ""));
                    if (kit == null) return;
                    if (!SetKitField(kit, arg.GetString(2, "").ToLowerInvariant(), Rest(arg, 3))) { SetStatus(s, Msg("BadValue", p), true); return; }
                    RegisterPerm(kit.Permission);
                    SaveKits();
                    SetStatus(s, Msg("Saved", p), false);
                    return;
                }
                case "kiticonheld":
                {
                    var kit = FindKit(arg.GetString(1, ""));
                    if (kit == null) return;
                    var held = SourceItem(p);
                    if (held == null) { SetStatus(s, Msg("HoldItem", p), true); return; }
                    kit.Icon = held.info.shortname;
                    SaveKits();
                    SetStatus(s, Msg("Saved", p), false);
                    return;
                }
                case "kitdraft":
                {
                    string field = arg.GetString(2, "").ToLowerInvariant();
                    string value = Rest(arg, 3);
                    if (field == "short") s.KitShort = value.Trim();
                    else if (field == "amt") s.KitAmt = value.Trim();
                    else if (field == "skin") s.KitSkin = value.Trim();
                    return;
                }
                case "kitadd":
                {
                    var kit = FindKit(arg.GetString(1, ""));
                    if (kit == null) return;
                    if (!AddKitStack(kit, s.KitShort, s.KitAmt, s.KitSkin)) { SetStatus(s, Msg("BadValue", p), true); return; }
                    SaveKits();
                    s.KitShort = "";
                    s.KitAmt = "1";
                    s.KitSkin = "0";
                    SetStatus(s, Msg("Saved", p), false);
                    return;
                }
                case "kititem":
                {
                    var kit = FindKit(arg.GetString(1, ""));
                    int index = arg.GetInt(2, -1);
                    if (kit == null || kit.Items == null || index < 0 || index >= kit.Items.Count) return;
                    string field = arg.GetString(3, "").ToLowerInvariant();
                    string value = Rest(arg, 4);
                    var stack = kit.Items[index];
                    if (field == "amt")
                    {
                        int amount;
                        if (!int.TryParse(value, out amount) || amount < 1) { SetStatus(s, Msg("BadValue", p), true); return; }
                        stack.Amount = amount;
                    }
                    else if (field == "skin")
                    {
                        ulong skin;
                        if (!ulong.TryParse(value, out skin)) { SetStatus(s, Msg("BadValue", p), true); return; }
                        stack.Skin = skin;
                    }
                    else if (field == "short")
                    {
                        if (Def(value) == null) { SetStatus(s, Msg("BadValue", p), true); return; }
                        stack.Shortname = value.Trim();
                    }
                    else return;
                    SaveKits();
                    SetStatus(s, Msg("Saved", p), false);
                    return;
                }
                case "kitdelitem":
                {
                    var kit = FindKit(arg.GetString(1, ""));
                    int index = arg.GetInt(2, -1);
                    if (kit == null || kit.Items == null || index < 0 || index >= kit.Items.Count) return;
                    kit.Items.RemoveAt(index);
                    SaveKits();
                    SetStatus(s, Msg("Deleted", p), false);
                    return;
                }
                case "kitcopy":
                {
                    var kit = FindKit(arg.GetString(1, ""));
                    if (kit == null) return;
                    int added = CopyInventoryToKit(p, kit);
                    SaveKits();
                    SetStatus(s, "Added " + added + " stack" + (added == 1 ? "" : "s") + " from your main inventory and belt.", false);
                    return;
                }

                // ---- Item picklist ----
                case "pickopen":
                {
                    string target = arg.GetString(1);
                    if (target == "new" && data.Categories.Count == 0) { SetStatus(s, Msg("NoCategory", p), true); return; }
                    s.PickTarget = target;
                    s.PickPage = 0;
                    return;
                }
                case "pickclose":
                    s.PickTarget = null;
                    return;
                case "picksearch":
                    s.PickSearch = Rest(arg, 1);
                    s.PickPage = 0;
                    return;
                case "pickcat":
                    s.PickCat = arg.GetInt(1, -1);
                    s.PickPage = 0;
                    return;
                case "pickpage":
                    s.PickPage = Math.Max(0, s.PickPage + arg.GetInt(1));
                    return;
                case "pick":
                {
                    var def = Def(arg.GetString(1));
                    if (def == null || string.IsNullOrEmpty(s.PickTarget)) return;
                    ApplyPick(p, s, def);
                    return;
                }

                // ---- Event listings ----
                case "eventmenu":
                    if (data.Categories.Count == 0) { SetStatus(s, Msg("NoCategory", p), true); return; }
                    s.EventMenu = !s.EventMenu;
                    return;
                case "newrb":
                case "newde":
                {
                    if (data.Categories.Count == 0) return;
                    string evArg = action == "newrb" ? arg.GetString(1, "0") : Rest(arg, 1);
                    if (string.IsNullOrEmpty(evArg)) { SetStatus(s, Msg("BadValue", p), true); return; }
                    var cat = data.Categories[Mathf.Clamp(s.Cat, 0, data.Categories.Count - 1)];
                    var item = new ShopItem
                    {
                        Id = data.NextId++,
                        Type = action == "newrb" ? "raidable" : "dungeon",
                        EventArg = evArg,
                        Price = config.DefaultPrice,
                        GiveItem = false,
                        ChargedByPlugin = true
                    };
                    cat.Items.Add(item);
                    SaveData();
                    s.EventMenu = false;
                    s.EditItem = item.Id;
                    SetStatus(s, Msg("EventAdded", p, ItemLabel(item), cat.Name), false);
                    return;
                }
                case "icharge":
                {
                    ShopCategory cat;
                    var item = FindItem(arg.GetInt(1), out cat);
                    if (item == null) return;
                    item.ChargedByPlugin = !item.ChargedByPlugin;
                    SaveData();
                    SetStatus(s, Msg("Saved", p), false);
                    return;
                }
                case "iset":
                {
                    ShopCategory cat;
                    var item = FindItem(arg.GetInt(1), out cat);
                    if (item == null) return;
                    string field = arg.GetString(2).ToLowerInvariant();
                    string value = Rest(arg, 3);
                    if (!SetItemField(item, cat, field, value)) { SetStatus(s, Msg("BadValue", p), true); return; }
                    SaveData();
                    SetStatus(s, Msg("Saved", p), false);
                    return;
                }
                case "idel":
                {
                    int id = arg.GetInt(1);
                    ShopCategory cat;
                    var item = FindItem(id, out cat);
                    if (item == null) return;
                    if (!Confirm(s, "item" + id)) { SetStatus(s, Msg("ConfirmDelete", p) + $" ({ItemLabel(item)})", true); return; }
                    cat.Items.Remove(item);
                    if (s.EditItem == id) s.EditItem = 0;
                    SaveData();
                    SetStatus(s, Msg("Deleted", p), false);
                    return;
                }
                case "taddcat":
                {
                    string name = Rest(arg, 1);
                    if (string.IsNullOrEmpty(name)) return;
                    if (data.TradeCategories.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { SetStatus(s, Msg("CategoryExists", p), true); return; }
                    data.TradeCategories.Add(new TradeCategory { Name = name });
                    SaveData();
                    SetStatus(s, Msg("CategoryAdded", p, name), false);
                    return;
                }
                case "tdelcat":
                {
                    int idx = arg.GetInt(1, -1);
                    if (idx < 0 || idx >= data.TradeCategories.Count) return;
                    var tcat = data.TradeCategories[idx];
                    int count = TradesIn(idx).Count;
                    if (!Confirm(s, "tcat" + idx)) { SetStatus(s, Msg("ConfirmDelete", p) + $" ({tcat.Name}; its {count} trades move to OTHER)", true); return; }
                    data.TradeCategories.RemoveAt(idx);
                    s.TradeCat = TradeTiles;
                    SaveData();
                    SetStatus(s, Msg("Deleted", p), false);
                    return;
                }
                case "tcaticon":
                {
                    if (s.TradeCat < 0 || s.TradeCat >= data.TradeCategories.Count) return;
                    data.TradeCategories[s.TradeCat].Icon = Rest(arg, 1);
                    SaveData();
                    SetStatus(s, Msg("Saved", p), false);
                    return;
                }
                case "tcatnext":
                {
                    var ex = data.Exchanges.Find(x => x.Id == arg.GetInt(1));
                    if (ex == null || data.TradeCategories.Count == 0) return;
                    int cur = data.TradeCategories.FindIndex(t => string.Equals(t.Name, ex.Category, StringComparison.OrdinalIgnoreCase));
                    ex.Category = data.TradeCategories[(cur + 1) % data.TradeCategories.Count].Name;
                    SaveData();
                    SetStatus(s, "Moved to " + ex.Category + ".", false);
                    return;
                }
                case "newtrade":
                {
                    var ex = new Exchange { Id = data.NextId++ };
                    if (s.TradeCat >= 0 && s.TradeCat < data.TradeCategories.Count) ex.Category = data.TradeCategories[s.TradeCat].Name;
                    data.Exchanges.Add(ex);
                    s.EditTrade = ex.Id;
                    s.EditItem = 0;
                    SaveData();
                    return;
                }
                case "tedit":
                    s.EditTrade = arg.GetInt(1);
                    s.EditItem = 0;
                    return;
                case "tset":
                {
                    var ex = data.Exchanges.Find(x => x.Id == arg.GetInt(1));
                    if (ex == null) return;
                    if (!SetTradeField(ex, arg.GetString(2).ToLowerInvariant(), Rest(arg, 3))) { SetStatus(s, Msg("BadValue", p), true); return; }
                    SaveData();
                    SetStatus(s, Msg("Saved", p), false);
                    return;
                }
                case "theld":
                {
                    var ex = data.Exchanges.Find(x => x.Id == arg.GetInt(1));
                    if (ex == null) return;
                    var held = SourceItem(p);
                    if (held == null) { SetStatus(s, Msg("HoldItem", p), true); return; }
                    if (arg.GetString(2) == "in")
                    {
                        ex.InShortname = held.info.shortname;
                        ex.InSkin = held.skin;
                        ex.InAmount = Math.Max(1, held.amount);
                    }
                    else
                    {
                        ex.OutShortname = held.info.shortname;
                        ex.OutSkin = held.skin;
                        ex.OutAmount = Math.Max(1, held.amount);
                        ex.OutName = held.name ?? "";
                    }
                    SaveData();
                    SetStatus(s, Msg("Saved", p), false);
                    return;
                }
                case "tdel":
                {
                    int id = arg.GetInt(1);
                    var ex = data.Exchanges.Find(x => x.Id == id);
                    if (ex == null) return;
                    if (!Confirm(s, "trade" + id)) { SetStatus(s, Msg("ConfirmDelete", p), true); return; }
                    data.Exchanges.Remove(ex);
                    if (s.EditTrade == id) s.EditTrade = 0;
                    SaveData();
                    SetStatus(s, Msg("Deleted", p), false);
                    return;
                }
                case "imove":
                {
                    ShopCategory cat;
                    var item = FindItem(arg.GetInt(1), out cat);
                    if (item == null || data.Categories.Count < 2) return;
                    int next = (data.Categories.IndexOf(cat) + 1) % data.Categories.Count;
                    cat.Items.Remove(item);
                    data.Categories[next].Items.Add(item);
                    s.Cat = next;
                    SaveData();
                    SetStatus(s, $"Moved to {data.Categories[next].Name}.", false);
                    return;
                }
            }
        }

        private bool SetItemField(ShopItem item, ShopCategory cat, string field, string value)
        {
            int i;
            ulong u;
            switch (field)
            {
                case "description": item.Description = value; return true;
                case "descriptionkey":
                    string key = value.StartsWith("Description_", StringComparison.OrdinalIgnoreCase) ? value.Substring(12) : value;
                    if (key.Length > 0 && (config.Descriptions == null || !config.Descriptions.Keys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))) return false;
                    item.DescriptionKey = key; return true;
                case "carddescription": item.ShowDescriptionOnCard = !item.ShowDescriptionOnCard; return true;
                case "cleardescription": item.Description = ""; return true;
                case "name": item.DisplayName = value; return true;
                case "itemname": item.ItemName = value; return true;
                case "price":
                    if (!int.TryParse(value, out i) || i < 0) return false;
                    item.Price = i;
                    item.PluginPricing = false;
                    return true;
                case "amount":
                    if (!int.TryParse(value, out i) || i < 1) return false;
                    item.Amount = i; return true;
                case "skin":
                    if (!ulong.TryParse(value, out u)) return false;
                    item.Skin = u; return true;
                case "shortname":
                    if (Def(value) == null) return false;
                    item.Shortname = value.ToLowerInvariant(); return true;
                case "bp":
                    item.Blueprint = !item.Blueprint; return true;
                case "eventarg":
                    if (string.IsNullOrEmpty(value)) return false;
                    item.EventArg = value; return true;
                case "icon":
                {
                    ulong iconSkin;
                    if (!string.IsNullOrEmpty(value) && !value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        && !ulong.TryParse(value, out iconSkin) && Def(value) == null) return false;
                    item.Icon = value; return true;
                }
                case "perm":
                    item.Permission = value;
                    RegisterPerm(value); return true;
                case "cooldown":
                    if (!int.TryParse(value, out i) || i < 0) return false;
                    item.Cooldown = i; return true;
                case "daily":
                    if (!int.TryParse(value, out i) || i < 0) return false;
                    item.DailyLimit = i; return true;
                case "wipe":
                    if (!int.TryParse(value, out i) || i < 0) return false;
                    item.WipeLimit = i; return true;
                case "owned":
                    item.RequireOwnership = !item.RequireOwnership; return true;
                case "one":
                    item.LimitToOne = !item.LimitToOne; return true;
            }
            return false;
        }

        private void ApplyPick(BasePlayer p, UiState s, ItemDefinition def)
        {
            string target = s.PickTarget;
            s.PickTarget = null;

            if (!string.IsNullOrEmpty(target) && target.StartsWith("kitadd:", StringComparison.Ordinal))
            {
                var kit = FindKit(target.Substring("kitadd:".Length));
                if (kit == null) return;
                if (kit.Items == null) kit.Items = new List<KitStack>();
                kit.Items.Add(new KitStack { Shortname = def.shortname, Amount = 1, Skin = 0 });
                SaveKits();
                s.EditKitId = kit.Id;
                SetStatus(s, Msg("Saved", p) + " Set the amount and skin below.", false);
                return;
            }

            if (target == "new")
            {
                var cat = data.Categories[Mathf.Clamp(s.Cat, 0, data.Categories.Count - 1)];
                var item = new ShopItem { Id = data.NextId++, Shortname = def.shortname, Amount = 1, Price = config.DefaultPrice };
                cat.Items.Add(item);
                SaveData();
                s.EditItem = item.Id;
                SetStatus(s, Msg("ItemAdded", p, def.displayName.english, cat.Name) + " Set the skin ID and price below.", false);
                return;
            }

            var parts = target.Split(':');
            int id;
            if (parts.Length != 2 || !int.TryParse(parts[1], out id)) return;

            switch (parts[0])
            {
                case "item":
                case "icon":
                {
                    ShopCategory cat;
                    var item = FindItem(id, out cat);
                    if (item == null) return;
                    if (parts[0] == "item") { item.Shortname = def.shortname; item.Skin = 0; }
                    else item.Icon = def.shortname;
                    s.EditItem = id;
                    break;
                }
                case "tin":
                case "tout":
                {
                    var ex = data.Exchanges.Find(x => x.Id == id);
                    if (ex == null) return;
                    if (parts[0] == "tin") { ex.InShortname = def.shortname; ex.InSkin = 0; }
                    else { ex.OutShortname = def.shortname; ex.OutSkin = 0; }
                    s.EditTrade = id;
                    break;
                }
            }
            SaveData();
            SetStatus(s, Msg("Saved", p), false);
        }

        private bool SetTradeField(Exchange ex, string field, string value)
        {
            int i;
            ulong u;
            switch (field)
            {
                case "in":
                    if (!IsRP(value) && Def(value) == null) return false;
                    ex.InShortname = value.ToLowerInvariant(); return true;
                case "out":
                    if (!IsRP(value) && Def(value) == null) return false;
                    ex.OutShortname = value.ToLowerInvariant(); return true;
                case "inamt":
                    if (!int.TryParse(value, out i) || i < 1) return false;
                    ex.InAmount = i; return true;
                case "outamt":
                    if (!int.TryParse(value, out i) || i < 1) return false;
                    ex.OutAmount = i; return true;
                case "inskin":
                    if (!ulong.TryParse(value, out u)) return false;
                    ex.InSkin = u; return true;
                case "outskin":
                    if (!ulong.TryParse(value, out u)) return false;
                    ex.OutSkin = u; return true;
                case "outname":
                    ex.OutName = value; return true;
            }
            return false;
        }

        #endregion

        #region UI Helpers

        private CuiImageComponent Img(string color, bool blur = false)
        {
            var img = new CuiImageComponent { Color = C(color), BlocksRaycast = false };
            if (blur) img.Material = "assets/content/ui/uibackgroundblur.mat";
            return img;
        }

        private static CuiRectTransformComponent Full() =>
            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "0 0", OffsetMax = "0 0" };

        // Pixel rect from the parent's bottom-left corner.
        private static CuiRectTransformComponent R(int x1, int y1, int x2, int y2) =>
            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{x1} {y1}", OffsetMax = $"{x2} {y2}" };

        // Pixel rect from the parent's top-left corner (used inside scroll content).
        private static CuiRectTransformComponent TL(int x, int yTop, int w, int h) =>
            new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = $"{x} {-(yTop + h)}", OffsetMax = $"{x + w} {-yTop}" };

        private string Box(CuiElementContainer c, string parent, string color, CuiRectTransformComponent rect, string name = null)
        {
            name = name ?? CuiHelper.GetGuid();
            c.Add(new CuiElement { Name = name, Parent = parent, Components = { Img(color), rect } });
            return name;
        }

        private void Label(CuiElementContainer c, string parent, string text, int size, string color, TextAnchor align, CuiRectTransformComponent rect, bool bold = true)
        {
            c.Add(new CuiElement
            {
                Name = CuiHelper.GetGuid(),
                Parent = parent,
                Components = { new CuiTextComponent { Text = text, FontSize = size, Color = C(color), Align = align, Font = bold ? FontBold : FontReg, BlocksRaycast = false }, rect }
            });
        }

        private string Btn(CuiElementContainer c, string parent, string command, string bg, CuiRectTransformComponent rect, string text, int size, string textColor)
        {
            string name = CuiHelper.GetGuid();
            c.Add(new CuiElement { Name = name, Parent = parent, Components = { new CuiButtonComponent { Command = command ?? "", Color = C(bg), BlocksRaycast = true }, rect } });
            if (!string.IsNullOrEmpty(text)) Label(c, name, text, size, textColor, TextAnchor.MiddleCenter, Full());
            return name;
        }

        private void Icon(CuiElementContainer c, string parent, ItemDefinition def, ulong skin, CuiRectTransformComponent rect)
        {
            if (def == null) return;
            c.Add(new CuiElement { Name = CuiHelper.GetGuid(), Parent = parent, Components = { new CuiImageComponent { ItemId = def.itemid, SkinId = skin, BlocksRaycast = false }, rect } });
        }

        // URL, workshop skin ID, item shortname, or a png in data/Nightshade/NightshadeShop/Icons/.
        private void ListIcon(CuiElementContainer c, string parent, string icon, ItemDefinition fallback, ulong fallbackSkin, CuiRectTransformComponent rect)
        {
            if (!string.IsNullOrEmpty(icon))
            {
                if (icon.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    c.Add(new CuiElement { Name = CuiHelper.GetGuid(), Parent = parent, Components = { new CuiRawImageComponent { Url = icon, Color = "1 1 1 1", BlocksRaycast = false }, rect } });
                    return;
                }
                if (IsLocalPng(icon))
                {
                    string png = LocalPngId(icon);
                    if (!string.IsNullOrEmpty(png))
                    {
                        c.Add(new CuiElement { Name = CuiHelper.GetGuid(), Parent = parent, Components = { new CuiRawImageComponent { Png = png, Color = "1 1 1 1", BlocksRaycast = false }, rect } });
                        return;
                    }
                }
                else
                {
                    ulong skinIcon;
                    if (ulong.TryParse(icon, out skinIcon))
                    {
                        c.Add(new CuiElement { Name = CuiHelper.GetGuid(), Parent = parent, Components = { new CuiImageComponent { ItemId = SkinIconItemId, SkinId = skinIcon, BlocksRaycast = false }, rect } });
                        return;
                    }
                    var d = Def(icon);
                    if (d != null) { Icon(c, parent, d, 0, rect); return; }
                }
            }
            Icon(c, parent, fallback, fallbackSkin, rect);
        }

        private void Input(CuiElementContainer c, string parent, string text, string command, string bg, string fg, CuiRectTransformComponent rect, int size = 14, int charsLimit = 64)
        {
            string name = Box(c, parent, bg, rect);
            c.Add(new CuiElement
            {
                Name = CuiHelper.GetGuid(),
                Parent = name,
                Components =
                {
                    new CuiInputFieldComponent { Text = text ?? "", Command = command, FontSize = size, Color = C(fg), Align = TextAnchor.MiddleLeft, Font = FontReg, CharsLimit = charsLimit, NeedsKeyboard = true, BlocksRaycast = true },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "8 0", OffsetMax = "-8 0" }
                }
            });
        }

        [ChatCommand("shopscrolltest")]
        private void ChatScrollTest(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player)) return;
            CuiHelper.DestroyUi(player, UiMain);
            CuiHelper.DestroyUi(player, UiScrollTest);
            S(player).Open = false;
            var ui = new CuiElementContainer();
            ui.Add(new CuiElement
            {
                Name = UiScrollTest, Parent = "Overlay",
                Components =
                {
                    new CuiImageComponent { Color = "0.08 0.08 0.12 1", BlocksRaycast = true },
                    new CuiNeedsCursorComponent(),
                    new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-230 -220", OffsetMax = "230 220" }
                }
            });
            Label(ui, UiScrollTest, "NIGHTSHADE SCROLL TEST v" + PluginVersion, 18, "#FFFFFF", TextAnchor.MiddleCenter, R(10, 390, 450, 430));
            Label(ui, UiScrollTest, "Hover the numbered rows and turn the wheel.", 13, "#DDDDDD", TextAnchor.MiddleCenter, R(10, 355, 450, 390));
            string scrollName = UiScrollTest + ".Native";
            var scroll = new CuiScrollViewComponent
            {
                Vertical = true, Horizontal = false,
                MovementType = ScrollRect.MovementType.Clamped,
                Inertia = true, DecelerationRate = 0.135f, ScrollSensitivity = 40f,
                ContentTransform = new CuiRectTransform { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 -1200", OffsetMax = "0 0", Pivot = "0.5 1" },
                VerticalScrollbar = new CuiScrollbar { AutoHide = false, Size = 16, HandleColor = "0.7 0.4 0.9 1", TrackColor = "0.15 0.15 0.2 1" }
            };
            ui.Add(new CuiElement
            {
                Name = scrollName, Parent = UiScrollTest,
                Components =
                {
                    new CuiImageComponent { Color = "0.12 0.12 0.18 1", BlocksRaycast = true },
                    new CuiNeedsCursorComponent(), scroll, R(20, 65, 440, 350)
                }
            });
            for (int i = 0; i < 30; i++)
                Btn(ui, scrollName, "", i % 2 == 0 ? "#30243F" : "#251C32", TL(0, i * 40, 396, 38), "ROW " + (i + 1), 16, "#FFFFFF");
            ui.Add(new CuiButton
            {
                Button = { Color = "0.5 0.15 0.2 1", Close = UiScrollTest, BlocksRaycast = true },
                Text = { Text = "CLOSE TEST", FontSize = 14, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "120 15", OffsetMax = "340 50" }
            }, UiScrollTest);
            string encoded = JsonConvert.SerializeObject(scroll);
            Puts("Scroll diagnostic component: " + encoded);
            CuiHelper.AddUi(player, ui);
            SendReply(player, "Scroll test opened. Check mouse wheel over rows and scrollbar. Close the test before reopening /shop.");
        }

        private string Scroll(CuiElementContainer c, string parent, CuiRectTransformComponent rect, int viewportH, int contentH, ThemeColors tc)
        {
            string name = CuiHelper.GetGuid();
            int h = Math.Max(viewportH, contentH);
            c.Add(new CuiElement
            {
                Name = name,
                Parent = parent,
                Components =
                {
                    // Match the native scroll configuration verified by /shopscrolltest.
                    new CuiImageComponent { Color = C(tc.Panel), BlocksRaycast = true },
                    new CuiNeedsCursorComponent(),
                    new CuiScrollViewComponent
                    {
                        Vertical = true,
                        Horizontal = false,
                        MovementType = ScrollRect.MovementType.Clamped,
                        Elasticity = 0.1f,
                        Inertia = true,
                        DecelerationRate = 0.135f,
                        ScrollSensitivity = 40f,
                        ContentTransform = new CuiRectTransform { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = $"0 -{h}", OffsetMax = "0 0", Pivot = "0.5 1" },
                        VerticalScrollbar = new CuiScrollbar { Size = 6, AutoHide = false, HandleColor = C(tc.Accent), HighlightColor = C(tc.Accent), PressedColor = C(tc.Accent), TrackColor = C(tc.Panel) }
                    },
                    rect
                }
            });
            return name;
        }

        #endregion

        #region UI

        private void OpenUi(BasePlayer p)
        {
            if (p == null || !p.IsConnected) return;
            var s = S(p);
            bool admin = IsAdmin(p);
            if (!admin) s.Admin = false;
            if (s.Tab == 3 && !CanExchange(p)) s.Tab = 0;
            if (s.Tab == 2 && !CanSeeKits(p)) s.Tab = 0;
            if (s.Tab < 0 || s.Tab > 3) s.Tab = 0;

            var theme = ActiveTheme();
            var tc = theme.Colors;
            int bal = Balance(p);
            var c = new CuiElementContainer();

            // Root + frame
            c.Add(new CuiElement
            {
                Name = UiMain, Parent = "Overlay", DestroyUi = UiMain,
                Components = { Img(tc.Overlay, true), Full(), new CuiNeedsCursorComponent() }
            });
            c.Add(new CuiElement
            {
                Name = UiFrame, Parent = UiMain,
                Components = { Img(tc.Background), new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-550 -310", OffsetMax = "550 310" } }
            });

            // Header
            string header = Box(c, UiFrame, tc.Header, R(0, 570, 1100, 620));
            Box(c, UiFrame, tc.Accent, R(0, 566, 1100, 570));
            Icon(c, header, Def(theme.Icon), 0, R(12, 7, 48, 43));
            string heading = config.Title ?? "";
            if (config.ShowHeaderTagline && !string.IsNullOrEmpty(theme.Tagline))
                heading += "   <size=14><color=" + Rich(tc.Accent) + ">" + theme.Tagline + "</color></size>";
            Label(c, header, heading, 24, tc.Text, TextAnchor.MiddleLeft, R(58, 0, admin ? 612 : 790, 50));
            if (admin)
                Btn(c, header, $"{UiCmd} admin", s.Admin ? tc.Danger : tc.Button, R(622, 8, 790, 42), s.Admin ? "ADMIN MODE: ON" : "ADMIN MODE: OFF", 13, s.Admin ? tc.DangerText : tc.ButtonText);

            string balBox = Box(c, header, tc.Card, R(800, 8, 1050, 42));
            Label(c, balBox, "BALANCE", 11, tc.SubText, TextAnchor.MiddleLeft, R(10, 0, 100, 34));
            Label(c, balBox, ServerRewards == null ? "N/A" : $"{bal:N0} <color={Rich(tc.Accent)}>RP</color>", 18, tc.Text, TextAnchor.MiddleRight, R(90, 0, 240, 34));
            Btn(c, header, $"{UiCmd} close", tc.Danger, R(1058, 9, 1090, 41), "X", 16, tc.DangerText);

            // Tab bar. Width is split evenly across whichever tabs this player can see.
            string tabs = Box(c, UiFrame, tc.Panel, R(0, 526, 1100, 566));
            DrawTopTabs(c, tabs, p, s, tc);

            // Content. 0 shop, 1 events, 2 kits, 3 admin shop.
            if (s.Tab == 3) BuildExchange(c, p, s, tc);
            else if (s.Tab == 2) BuildKits(c, p, s, tc);
            else BuildShop(c, p, s, tc, bal);

            // Footer
            if (!string.IsNullOrEmpty(s.Status))
                Label(c, UiFrame, s.Status, 13, s.StatusBad ? tc.Danger : tc.Accent, TextAnchor.MiddleLeft, R(18, 10, 800, 44));
            string modeText = config.ThemeMode.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? "AUTO" : "LOCKED";
            Label(c, UiFrame, $"v{PluginVersion}   THEME: {theme.Name.ToUpperInvariant()} ({modeText})", 11, tc.SubText, TextAnchor.MiddleRight, R(800, 10, 1084, 44));

            // Editors
            if (s.BuyItem != 0) BuildPurchasePopup(c, p, s, tc);
            else if (s.Admin && s.DescriptionEdit && s.EditItem != 0) BuildDescriptionEditor(c, s, tc);
            else if (s.Admin && !string.IsNullOrEmpty(s.PickTarget)) BuildPicker(c, s, tc);
            else if (s.Admin && s.EventMenu) BuildEventMenu(c, tc);
            else if (s.Admin && s.EditItem != 0) BuildItemEditor(c, s, tc);
            else if (s.Admin && s.EditTrade != 0) BuildTradeEditor(c, s, tc);
            else if (s.Admin && !string.IsNullOrEmpty(s.EditKitId)) BuildKitEditor(c, s, tc);

            s.Open = true;
            CuiHelper.AddUi(p, c);
        }

        private void BuildShop(CuiElementContainer c, BasePlayer p, UiState s, ThemeColors tc, int bal)
        {
            string wantTab = s.Tab == 1 ? "events" : "shop";
            var visible = new List<int>();
            for (int i = 0; i < data.Categories.Count; i++)
            {
                var candidate = data.Categories[i];
                if (CategoryTab(candidate) != wantTab) continue;
                if (HiddenFromPlayer(p, candidate)) continue;
                if (CanSee(p, candidate.Permission) && (InSeason(candidate) || IsAdmin(p))) visible.Add(i);
            }
            if (visible.Count > 0 && !visible.Contains(s.Cat)) s.Cat = visible[0];

            // Sidebar
            string side = Box(c, UiFrame, tc.Panel, R(16, 56, 236, 510));
            Label(c, side, "CATEGORIES", 14, tc.SubText, TextAnchor.MiddleLeft, R(12, 420, 210, 448));

            int bottom = s.Admin ? 72 : 6;
            int viewH = 414 - bottom;
            string catScroll = Scroll(c, side, R(6, bottom, 214, 414), viewH, visible.Count * 40, tc);
            for (int row = 0; row < visible.Count; row++)
            {
                int i = visible[row];
                var cat = data.Categories[i];
                bool on = i == s.Cat;
                int count = cat.Items.Count(x => CanSee(p, x.Permission));
                string b = Btn(c, catScroll, $"{UiCmd} cat {i}", on ? tc.Accent : tc.Button, TL(0, row * 40, 198, 36), null, 0, null);
                ListIcon(c, b, cat.Icon, null, 0, R(6, 5, 32, 31));
                string marks = "";
                if (!InSeason(cat)) marks += " <size=9>OFF</size>";
                if (cat.AdminModeOnly == true) marks += " <size=9>HIDDEN</size>";
                Label(c, b, $"{cat.Name.ToUpperInvariant()} <size=11>({count})</size>{marks}", cat.Name.Length > 12 || marks.Length > 0 ? 11 : 14, on ? tc.AccentText : tc.ButtonText, TextAnchor.MiddleLeft, R(40, 0, 170, 36));
                if (s.Admin)
                    Btn(c, catScroll, $"{UiCmd} delcat {i}", tc.Danger, TL(172, row * 40 + 7, 22, 22), "X", 11, tc.DangerText);
            }

            if (s.Admin)
            {
                Label(c, side, "NEW CATEGORY (type + Enter)", 11, tc.SubText, TextAnchor.MiddleLeft, R(12, 42, 210, 64));
                Input(c, side, "", $"{UiCmd} addcat", tc.Card, tc.Text, R(10, 8, 210, 38));
            }

            // Grid panel
            string grid = Box(c, UiFrame, tc.Panel, R(250, 56, 1084, 510));
            if (visible.Count == 0)
            {
                string empty = s.Tab == 1
                    ? (s.Admin ? "No event categories yet. Add one on the left, or move a category here." : "No event listings right now.")
                    : (s.Admin ? "No categories yet. Add one on the left." : "The shop is empty right now.");
                Label(c, grid, empty, 18, tc.SubText, TextAnchor.MiddleCenter, Full());
                return;
            }

            var current = data.Categories[s.Cat];
            var listings = current.Items.Where(x => CanSee(p, x.Permission)).ToList();
            Label(c, grid, current.Name.ToUpperInvariant(), 20, tc.Text, TextAnchor.MiddleLeft, R(16, 414, 330, 446));
            if (s.Admin)
            {
                Btn(c, grid, $"{UiCmd} pickopen new", tc.Accent, R(336, 416, 456, 444), "+ PICK ITEM", 12, tc.AccentText);
                Btn(c, grid, $"{UiCmd} additem", tc.Button, R(462, 416, 572, 444), "+ HELD ITEM", 12, tc.ButtonText);
                Btn(c, grid, $"{UiCmd} eventmenu", tc.Accent, R(578, 416, 694, 444), "+ EVENT", 12, tc.AccentText);
                Btn(c, grid, $"{UiCmd} caticon", tc.Button, R(700, 416, 818, 444), "SET CAT ICON", 12, tc.ButtonText);
                Label(c, grid, "PERM", 10, tc.SubText, TextAnchor.MiddleLeft, R(16, 393, 48, 413));
                Input(c, grid, current.Permission, $"{UiCmd} catperm", tc.Card, tc.Text, R(48, 393, 290, 413), 11);
                Label(c, grid, "ICON", 10, tc.SubText, TextAnchor.MiddleLeft, R(298, 393, 330, 413));
                Input(c, grid, current.Icon, $"{UiCmd} caticontext", tc.Card, tc.Text, R(330, 393, 560, 413), 11, 128);
                Label(c, grid, "THEMES", 10, tc.SubText, TextAnchor.MiddleLeft, R(568, 393, 614, 413));
                Input(c, grid, string.Join(", ", current.Themes ?? new List<string>()), $"{UiCmd} catthemes", tc.Card, tc.Text, R(614, 393, 818, 413), 11);
                string tabNow = CategoryTab(current);
                Label(c, grid, "TAB", 10, tc.SubText, TextAnchor.MiddleLeft, R(16, 360, 46, 384));
                Btn(c, grid, $"{UiCmd} cattab shop", tabNow == "shop" ? tc.Accent : tc.Button, R(48, 360, 140, 384), "SHOP", 11, tabNow == "shop" ? tc.AccentText : tc.ButtonText);
                Btn(c, grid, $"{UiCmd} cattab events", tabNow == "events" ? tc.Accent : tc.Button, R(146, 360, 250, 384), "EVENTS", 11, tabNow == "events" ? tc.AccentText : tc.ButtonText);
                bool hiddenFlag = current.AdminModeOnly == true;
                Btn(c, grid, $"{UiCmd} cathidden", hiddenFlag ? tc.Danger : tc.Button, R(266, 360, 520, 384), hiddenFlag ? "HIDDEN FROM PLAYERS: ON" : "HIDDEN FROM PLAYERS: OFF", 11, hiddenFlag ? tc.DangerText : tc.ButtonText);
            }
            else
            {
                Label(c, grid, current.RaidableTab ? "BUY A RAID \u2022 PRICES SET BY RAIDABLE BASES" : $"{listings.Count} ITEMS", current.RaidableTab ? 10 : 12, tc.SubText, TextAnchor.MiddleRight, R(560, 414, 818, 446));
            }

            const int cardW = 152, gap = 9, perRow = 5;
            int descriptionH = listings.Any(x => x.ShowDescriptionOnCard && !string.IsNullOrWhiteSpace(ItemDescription(x))) ? 100 : 0;
            int cardH = 176 + descriptionH;
            int rows = (listings.Count + perRow - 1) / perRow;
            int gridTop = s.Admin ? 352 : 388;
            string area = Scroll(c, grid, R(8, 8, 826, gridTop), gridTop - 8, rows * (cardH + gap), tc);

            if (listings.Count == 0)
                Label(c, area, s.Admin ? "No items. Hold an item and click + ADD HELD ITEM." : "Nothing here yet.", 16, tc.SubText, TextAnchor.MiddleCenter, TL(0, 150, 810, 40));

            for (int i = 0; i < listings.Count; i++)
            {
                var item = listings[i];
                bool isEvent = IsEvent(item);
                var def = CardIcon(item);
                string tile = Box(c, area, tc.Panel, TL((i % perRow) * (cardW + gap), (i / perRow) * (cardH + gap), cardW, cardH));
                string card = Box(c, tile, tc.Card, R(0, descriptionH, cardW, descriptionH + 176));
                string description = ItemDescription(item);
                if (descriptionH > 0 && item.ShowDescriptionOnCard && !string.IsNullOrWhiteSpace(description))
                {
                    string info = Box(c, tile, tc.Header, R(0, 0, cardW, descriptionH - 4));
                    string preview = description.Length > 145 ? description.Substring(0, 142).TrimEnd() + "\u2026" : description;
                    Label(c, info, preview, 11, tc.SubText, TextAnchor.UpperLeft, R(8, 6, cardW - 8, descriptionH - 12), false);
                }

                Label(c, card, ItemLabel(item), 12, tc.Text, TextAnchor.MiddleCenter, R(6, 148, 146, 172));
                ListIcon(c, card, item.Icon, def, isEvent ? 0 : item.Skin, R(38, 66, 114, 142));
                // Tags stacked in the column right of the icon; limits in the column left of it.
                var tags = new List<string>();
                if (!string.IsNullOrEmpty(item.Permission)) tags.Add("PERM");
                if (item.RequireOwnership) tags.Add("STEAM");
                if (!isEvent && item.Skin != 0) tags.Add("SKIN");
                if (!isEvent && item.Blueprint) tags.Add("BP");
                if (tags.Count > 0)
                    Label(c, card, string.Join("\n\n", tags), 9, tc.Accent, TextAnchor.MiddleCenter, R(114, 70, 150, 138));
                string limit = LimitText(p, item);
                if (limit != null)
                    Label(c, card, limit, 9, tc.SubText, TextAnchor.MiddleCenter, R(2, 70, 38, 138));

                if (isEvent)
                {
                    var plugin = item.Type == "raidable" ? RaidableBases : DungeonEvents;
                    string tag = item.Type == "raidable" ? "RAIDABLE BASE" : "DUNGEON EVENT";
                    Label(c, card, plugin == null ? "PLUGIN NOT LOADED" : tag, 10, plugin == null ? tc.Danger : tc.SubText, TextAnchor.LowerCenter, R(6, 64, 146, 80));
                }
                else
                {
                    if (item.GiveItem && Def(item.Shortname) == null) Label(c, card, "INVALID\n" + item.Shortname, 11, tc.Danger, TextAnchor.MiddleCenter, R(6, 66, 146, 142));
                }

                string reason;
                int remaining = Remaining(p, item, out reason);
                int qty = 1;
                if (!isEvent && item.GiveItem)
                    Label(c, card, "x" + (item.Amount * qty).ToString("N0"), 13, tc.Text, TextAnchor.LowerRight, R(6, 64, 146, 86));

                long cost = (long)item.Price * qty;
                bool pluginPricing = isEvent && item.ChargedByPlugin && item.PluginPricing;
                bool affordable = pluginPricing || bal >= cost;
                string priceText = pluginPricing ? "PLUGIN PRICING" : item.Price == 0 ? "FREE" : $"{cost:N0} RP";
                Label(c, card, priceText, 15, affordable ? tc.Accent : tc.Danger, TextAnchor.MiddleCenter, R(6, 38, 146, 62));

                if (s.Admin)
                {
                    Btn(c, card, $"{UiCmd} edit {item.Id}", tc.Button, R(6, 6, 74, 34), "EDIT", 12, tc.ButtonText);
                    Btn(c, card, $"{UiCmd} idel {item.Id}", tc.Danger, R(78, 6, 146, 34), "DEL", 12, tc.DangerText);
                }
                else if (isEvent && (item.Type == "raidable" ? RaidableBases : DungeonEvents) == null)
                    Btn(c, card, "", tc.Button, R(6, 6, 146, 34), "UNAVAILABLE", 11, tc.SubText);
                else if (remaining <= 0)
                    Btn(c, card, "", tc.Button, R(6, 6, 146, 34), reason, 10, tc.SubText);
                else if (item.GiveItem && item.RequireOwnership && !Owns(p, Def(item.Shortname)))
                    Btn(c, card, "", tc.Button, R(6, 6, 146, 34), "NOT OWNED ON STEAM", 10, tc.SubText);
                else if (affordable)
                    Btn(c, card, $"{UiCmd} buy {item.Id}", tc.Accent, R(6, 6, 146, 34), "BUY", 14, tc.AccentText);
                else
                    Btn(c, card, "", tc.Button, R(6, 6, 146, 34), "NOT ENOUGH RP", 11, tc.SubText);
            }
        }

        private void BuildExchange(CuiElementContainer c, BasePlayer p, UiState s, ThemeColors tc)
        {
            string panel = Box(c, UiFrame, tc.Panel, R(16, 56, 1084, 510));
            if (s.TradeCat >= data.TradeCategories.Count || (s.TradeCat == TradeOther && TradesIn(TradeOther).Count == 0)) s.TradeCat = TradeTiles;
            if (s.TradeCat == TradeTiles)
            {
                BuildTradeTiles(c, panel, s, tc);
                return;
            }

            bool other = s.TradeCat == TradeOther;
            var trades = TradesIn(s.TradeCat);
            string catName = other ? "OTHER" : data.TradeCategories[s.TradeCat].Name.ToUpperInvariant();

            Btn(c, panel, $"{UiCmd} tback", tc.Button, R(16, 414, 110, 444), "< BACK", 13, tc.ButtonText);
            Label(c, panel, catName, 20, tc.Text, TextAnchor.MiddleLeft, R(122, 414, 600, 446));
            Label(c, panel, $"{config.ExchangeTabName}  \u2022  {trades.Count} TRADES", 12, tc.SubText, TextAnchor.MiddleLeft, R(16, 392, 800, 414), false);
            if (s.Admin)
            {
                if (!other)
                {
                    Label(c, panel, "ICON", 10, tc.SubText, TextAnchor.MiddleLeft, R(610, 414, 646, 444));
                    Input(c, panel, data.TradeCategories[s.TradeCat].Icon, $"{UiCmd} tcaticon", tc.Card, tc.Text, R(646, 416, 890, 442), 11);
                }
                Btn(c, panel, $"{UiCmd} newtrade", tc.Accent, R(900, 414, 1052, 444), "+ NEW TRADE", 13, tc.AccentText);
            }

            const int rowH = 84, gap = 8;
            string list = Scroll(c, panel, R(8, 8, 1060, 386), 378, trades.Count * (rowH + gap), tc);

            if (trades.Count == 0)
                Label(c, list, s.Admin ? "No trades yet. Click + NEW TRADE." : "No trades available right now.", 16, tc.SubText, TextAnchor.MiddleCenter, TL(0, 150, 1040, 40));

            int bal = -1;
            for (int i = 0; i < trades.Count; i++)
            {
                var ex = trades[i];
                string row = Box(c, list, tc.Card, TL(0, i * (rowH + gap), 1040, rowH));

                bool inRp = IsRP(ex.InShortname);
                var inDef = inRp ? null : Def(ex.InShortname);
                var outDef = IsRP(ex.OutShortname) ? null : Def(ex.OutShortname);

                int have;
                if (inRp) { if (bal < 0) bal = Balance(p); have = bal; }
                else have = inDef != null ? CountItems(p, inDef, ex.InSkin) : 0;
                bool can = ex.InAmount > 0 && have >= ex.InAmount;

                DrawTradeIcon(c, row, ex.InShortname, inDef, ex.InSkin, 12, tc);
                Label(c, row, $"{ex.InAmount:N0} {NameOf(ex.InShortname)}", 16, tc.Text, TextAnchor.MiddleLeft, R(86, 42, 330, 72));
                Label(c, row, $"You have {have:N0}", 12, can ? tc.SubText : tc.Danger, TextAnchor.MiddleLeft, R(86, 14, 330, 40), false);

                Label(c, row, "\u00BB", 34, tc.Accent, TextAnchor.MiddleCenter, R(330, 10, 390, 74));

                DrawTradeIcon(c, row, ex.OutShortname, outDef, ex.OutSkin, 400, tc);
                Label(c, row, $"{ex.OutAmount:N0} {NameOf(ex.OutShortname, ex.OutName)}", 16, tc.Text, TextAnchor.MiddleLeft, R(474, 42, 604, 72));

                // Quantity selectors, then CONFIRM to execute.
                bool selected = can && s.TradeSel == ex.Id;
                int maxTimes = ex.InAmount > 0 ? have / ex.InAmount : 0;
                int times = !selected ? 0 : s.TradeQty == "max" ? maxTimes : s.TradeQty == "10" ? 10 : 1;
                string preview = selected
                    ? (times <= maxTimes
                        ? $"x{times:N0}: {(long)ex.InAmount * times:N0} \u00BB {(long)ex.OutAmount * times:N0}"
                        : $"x{times} needs {(long)ex.InAmount * times:N0}")
                    : "per trade";
                Label(c, row, preview, 12, selected && times > maxTimes ? tc.Danger : tc.SubText, TextAnchor.MiddleLeft, R(474, 14, 604, 40), false);

                string[] qtys = { "1", "10", "max" };
                string[] qtyText = { "x1", "x10", "MAX" };
                int[] qx = { 610, 666, 722 };
                for (int q = 0; q < qtys.Length; q++)
                {
                    bool on = selected && s.TradeQty == qtys[q];
                    bool affordable = qtys[q] == "10" ? maxTimes >= 10 : can;
                    string qbg = on ? tc.Accent : affordable ? tc.Button : tc.Panel;
                    string qfg = on ? tc.AccentText : affordable ? tc.ButtonText : tc.SubText;
                    Btn(c, row, $"{UiCmd} tsel {ex.Id} {qtys[q]}", qbg, R(qx[q], 22, qx[q] + (q == 2 ? 64 : 52), 62), qtyText[q], 14, qfg);
                }

                bool ready = selected && times >= 1 && times <= maxTimes;
                Btn(c, row, ready ? $"{UiCmd} trade {ex.Id} {s.TradeQty}" : $"{UiCmd} thint", ready ? tc.Accent : tc.Panel, R(796, 22, 926, 62),
                    "CONFIRM", 14, ready ? tc.AccentText : tc.SubText);

                if (s.Admin)
                {
                    Btn(c, row, $"{UiCmd} tedit {ex.Id}", tc.Button, R(938, 22, 986, 62), "EDIT", 11, tc.ButtonText);
                    Btn(c, row, $"{UiCmd} tdel {ex.Id}", tc.Danger, R(990, 22, 1032, 62), "DEL", 11, tc.DangerText);
                }
            }
        }

        private void BuildTradeTiles(CuiElementContainer c, string panel, UiState s, ThemeColors tc)
        {
            Label(c, panel, config.ExchangeTabName, 20, tc.Text, TextAnchor.MiddleLeft, R(16, 414, 600, 446));
            Label(c, panel, config.ExchangeSubtitle, 12, tc.SubText, TextAnchor.MiddleLeft, R(16, 392, 800, 414), false);
            if (s.Admin)
            {
                Label(c, panel, "NEW CATEGORY", 10, tc.SubText, TextAnchor.MiddleLeft, R(700, 414, 790, 444));
                Input(c, panel, "", $"{UiCmd} taddcat", tc.Card, tc.Text, R(790, 416, 1052, 442), 12);
            }

            var tiles = new List<int>();
            for (int i = 0; i < data.TradeCategories.Count; i++) tiles.Add(i);
            if (TradesIn(TradeOther).Count > 0) tiles.Add(TradeOther);

            const int tileW = 200, tileH = 144, gap = 10, perRow = 5;
            int rows = (tiles.Count + perRow - 1) / perRow;
            string area = Scroll(c, panel, R(8, 8, 1060, 386), 378, rows * (tileH + gap), tc);

            if (tiles.Count == 0)
                Label(c, area, s.Admin ? "No categories yet. Type a name in NEW CATEGORY and press Enter." : "No trades available right now.", 16, tc.SubText, TextAnchor.MiddleCenter, TL(0, 150, 1040, 40));

            for (int n = 0; n < tiles.Count; n++)
            {
                int idx = tiles[n];
                bool other = idx == TradeOther;
                int x = (n % perRow) * (tileW + gap), y = (n / perRow) * (tileH + gap);
                string name = other ? "OTHER" : data.TradeCategories[idx].Name.ToUpperInvariant();
                string icon = other ? "" : data.TradeCategories[idx].Icon;
                int count = TradesIn(idx).Count;

                string tile = Btn(c, area, $"{UiCmd} tcat {idx}", tc.Card, TL(x, y, tileW, tileH), null, 0, null);
                Box(c, tile, tc.Accent, R(0, 0, tileW, 3));
                string iconBg = Box(c, tile, tc.Panel, R(tileW / 2 - 30, 64, tileW / 2 + 30, 124));
                ListIcon(c, iconBg, icon, Def("blood"), 0, R(4, 4, 56, 56));
                Label(c, tile, name, 14, tc.Text, TextAnchor.MiddleCenter, R(8, 28, tileW - 8, 58));
                Label(c, tile, $"{count} TRADES", 11, tc.SubText, TextAnchor.MiddleCenter, R(8, 8, tileW - 8, 28));

                if (s.Admin && !other)
                    Btn(c, area, $"{UiCmd} tdelcat {idx}", tc.Danger, TL(x + tileW - 30, y + 8, 22, 22), "X", 11, tc.DangerText);
            }
        }

        private void DrawTradeIcon(CuiElementContainer c, string row, string shortname, ItemDefinition def, ulong skin, int x, ThemeColors tc)
        {
            string bg = Box(c, row, tc.Panel, R(x, 10, x + 64, 74));
            if (IsRP(shortname))
                Label(c, bg, "RP", 22, tc.Accent, TextAnchor.MiddleCenter, Full());
            else if (def != null)
                Icon(c, bg, def, skin, R(4, 4, 60, 60));
            else
                Label(c, bg, "?", 22, tc.Danger, TextAnchor.MiddleCenter, Full());
        }

        private void BuildEventEditorFields(CuiElementContainer c, string box, ThemeColors tc, ShopItem item, string cmd)
        {
            bool rb = item.Type == "raidable";
            var plugin = rb ? RaidableBases : DungeonEvents;
            string pluginName = rb ? "RaidableBases" : "DungeonEvents";
            string loaded = plugin == null ? $"<color={Rich(tc.Danger)}>NOT LOADED</color>" : "loaded";
            Label(c, box, $"{(rb ? "RAIDABLE BASE" : "DUNGEON EVENT")}\n<size=11>{pluginName} {loaded}</size>", 14, tc.Text, TextAnchor.MiddleLeft, R(96, 366, 300, 430));

            Field(c, box, tc, 322, "SHOP DISPLAY NAME", item.DisplayName, cmd + " name");
            Field(c, box, tc, 282, "PRICE (RP)", item.Price.ToString(), cmd + " price");

            if (rb)
            {
                Label(c, box, "DIFFICULTY", 12, tc.SubText, TextAnchor.MiddleLeft, R(20, 242, 170, 272));
                int x = 175;
                for (int d = 0; d < RbDifficulties.Length; d++)
                {
                    bool on = item.EventArg == d.ToString() || string.Equals(item.EventArg, RbDifficulties[d], StringComparison.OrdinalIgnoreCase);
                    Btn(c, box, $"{cmd} eventarg {d}", on ? tc.Accent : tc.Button, R(x, 242, x + 54, 272), RbDifficulties[d], 9, on ? tc.AccentText : tc.ButtonText);
                    x += 57;
                }
            }
            else Field(c, box, tc, 242, "DUNGEON TIER NAME", item.EventArg, cmd + " eventarg");

            Label(c, box, "WHO CHARGES RP", 12, tc.SubText, TextAnchor.MiddleLeft, R(20, 202, 170, 232));
            Btn(c, box, $"{UiCmd} icharge {item.Id}", item.ChargedByPlugin ? tc.Accent : tc.Button, R(175, 202, 460, 232),
                item.ChargedByPlugin ? $"{pluginName.ToUpperInvariant()} (RECOMMENDED)" : "THIS SHOP", 12, item.ChargedByPlugin ? tc.AccentText : tc.ButtonText);

            string note = item.ChargedByPlugin
                ? $"{pluginName} charges the RP cost from its own config, enforces its cooldowns and limits, and refunds failed spawns. The price above is display-only in this mode, so keep it matched to that config."
                : rb
                    ? $"This shop takes the RP, runs the free spawn command, and refunds automatically if RaidableBases doesn't confirm the purchase within {config.Events.RbRefundTimeout:0}s."
                    : $"This shop takes the RP, runs the free spawn command, and refunds if no matching dungeon spawn is confirmed within {config.Events.DeRefundTimeout:0}s. Use a command that does not charge the player again.";
            Label(c, box, note, 11, tc.SubText, TextAnchor.UpperLeft, R(20, 148, 460, 198), false);

            Label(c, box, "CARD ICON", 12, tc.SubText, TextAnchor.MiddleLeft, R(20, 108, 170, 138));
            Btn(c, box, $"{UiCmd} pickopen icon:{item.Id}", tc.Accent, R(175, 108, 312, 138), "PICK ICON", 12, tc.AccentText);
            Btn(c, box, $"{cmd} icon", tc.Button, R(318, 108, 460, 138), "DEFAULT ICON", 12, tc.ButtonText);

            Label(c, box, "Type a value and press Enter to save each field.", 11, tc.SubText, TextAnchor.MiddleCenter, R(20, 58, 460, 78), false);
            Btn(c, box, $"{UiCmd} idel {item.Id}", tc.Danger, R(20, 16, 160, 52), "DELETE", 14, tc.DangerText);
            Btn(c, box, $"{UiCmd} done", tc.Accent, R(320, 16, 460, 52), "DONE", 14, tc.AccentText);
        }

        private void BuildEventMenu(CuiElementContainer c, ThemeColors tc)
        {
            string box = ModalBox(c, tc, 480, 330, "ADD EVENT LISTING");

            string rbState = RaidableBases == null ? $"<color={Rich(tc.Danger)}>NOT LOADED</color>" : "LOADED";
            string deState = DungeonEvents == null ? $"<color={Rich(tc.Danger)}>NOT LOADED</color>" : "LOADED";

            Label(c, box, $"RAIDABLE BASE  <size=11>{rbState}</size>", 14, tc.Accent, TextAnchor.MiddleLeft, R(20, 248, 460, 276));
            for (int d = 0; d < RbDifficulties.Length; d++)
            {
                int x = 20 + d * 88;
                Btn(c, box, $"{UiCmd} newrb {d}", tc.Button, R(x, 208, x + 82, 242), RbDifficulties[d], 11, tc.ButtonText);
            }

            Label(c, box, $"DUNGEON EVENT  <size=11>{deState}</size>", 14, tc.Accent, TextAnchor.MiddleLeft, R(20, 160, 460, 188));
            Label(c, box, "Type the DungeonEvents tier name exactly as in its config, then press Enter.", 11, tc.SubText, TextAnchor.MiddleLeft, R(20, 136, 460, 158), false);
            Input(c, box, "", $"{UiCmd} newde", tc.Card, tc.Text, R(20, 98, 460, 130));

            Label(c, box, "New event listings are charged by the event plugin by default. Set the matching RP cost in its config.", 11, tc.SubText, TextAnchor.MiddleLeft, R(20, 60, 460, 92), false);
            Btn(c, box, $"{UiCmd} eventmenu", tc.Danger, R(320, 16, 460, 52), "CANCEL", 14, tc.DangerText);
        }

        private void BuildPicker(CuiElementContainer c, UiState s, ThemeColors tc)
        {
            var list = FilteredPickList(s);
            int pages = Math.Max(1, (list.Count + PickPerPage - 1) / PickPerPage);
            s.PickPage = Mathf.Clamp(s.PickPage, 0, pages - 1);

            const int W = 900, H = 560;
            string title = s.PickTarget != null && s.PickTarget.StartsWith("icon") ? "SELECT CARD ICON" : "SELECT ITEM";
            string box = ModalBox(c, tc, W, H, title);

            // Search
            Label(c, box, "SEARCH", 12, tc.SubText, TextAnchor.MiddleLeft, R(20, 478, 86, 510));
            Input(c, box, s.PickSearch, $"{UiCmd} picksearch", tc.Card, tc.Text, R(90, 478, 360, 510));
            Btn(c, box, $"{UiCmd} picksearch", tc.Button, R(366, 478, 440, 510), "CLEAR", 11, tc.ButtonText);
            Label(c, box, $"{list.Count} ITEMS", 12, tc.SubText, TextAnchor.MiddleRight, R(450, 478, 880, 510));

            // Category filter
            var cats = PickList().Select(d => (int)d.category).Distinct().OrderBy(x => x).ToList();
            int n = cats.Count + 1;
            int bw = (W - 40 - (n - 1) * 4) / n;
            int bx = 20;
            Btn(c, box, $"{UiCmd} pickcat -1", s.PickCat == -1 ? tc.Accent : tc.Button, R(bx, 440, bx + bw, 470), "ALL", 9, s.PickCat == -1 ? tc.AccentText : tc.ButtonText);
            bx += bw + 4;
            foreach (var cat in cats)
            {
                bool on = s.PickCat == cat;
                Btn(c, box, $"{UiCmd} pickcat {cat}", on ? tc.Accent : tc.Button, R(bx, 440, bx + bw, 470), ((ItemCategory)cat).ToString().ToUpperInvariant(), 9, on ? tc.AccentText : tc.ButtonText);
                bx += bw + 4;
            }

            // Grid
            int start = s.PickPage * PickPerPage;
            for (int i = 0; i < PickPerPage && start + i < list.Count; i++)
            {
                var def = list[start + i];
                int col = i % PickPerRow, row = i / PickPerRow;
                int x = 20 + col * 86;
                int y2 = 430 - row * 90;
                string tile = Btn(c, box, $"{UiCmd} pick {def.shortname}", tc.Card, R(x, y2 - 86, x + 82, y2), null, 0, null);
                Icon(c, tile, def, 0, R(17, 28, 65, 76));
                Label(c, tile, def.displayName.english, 9, tc.Text, TextAnchor.MiddleCenter, R(2, 2, 80, 28), false);
            }
            if (list.Count == 0)
                Label(c, box, "No items match that search.", 16, tc.SubText, TextAnchor.MiddleCenter, R(20, 70, 880, 430));

            // Footer
            Btn(c, box, $"{UiCmd} pickpage -1", tc.Button, R(20, 18, 120, 52), "< PREV", 12, tc.ButtonText);
            Label(c, box, $"PAGE {s.PickPage + 1} / {pages}", 13, tc.Text, TextAnchor.MiddleCenter, R(124, 18, 236, 52));
            Btn(c, box, $"{UiCmd} pickpage 1", tc.Button, R(240, 18, 340, 52), "NEXT >", 12, tc.ButtonText);
            Label(c, box, "After picking, set the skin ID in the listing editor.", 11, tc.SubText, TextAnchor.MiddleCenter, R(350, 18, 750, 52), false);
            Btn(c, box, $"{UiCmd} pickclose", tc.Danger, R(760, 18, 880, 52), "CANCEL", 13, tc.DangerText);
        }

        private string ModalBox(CuiElementContainer c, ThemeColors tc, int w, int h, string title)
        {
            c.Add(new CuiElement { Name = UiModal, Parent = UiMain, Components = { new CuiImageComponent { Color = C("#000000A0"), BlocksRaycast = true }, Full() } });
            c.Add(new CuiElement
            {
                Name = UiModal + ".Box", Parent = UiModal,
                Components = { Img(tc.Background), new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = $"{-w / 2} {-h / 2}", OffsetMax = $"{w / 2} {h / 2}" } }
            });
            string box = UiModal + ".Box";
            string head = Box(c, box, tc.Header, R(0, h - 40, w, h));
            Box(c, box, tc.Accent, R(0, h - 43, w, h - 40));
            Label(c, head, title, 16, tc.Text, TextAnchor.MiddleLeft, R(14, 0, w - 14, 40));
            return box;
        }

        private void Field(CuiElementContainer c, string box, ThemeColors tc, int y, string label, string value, string command, int x = 0, int chars = 64)
        {
            Label(c, box, label, 12, tc.SubText, TextAnchor.MiddleLeft, R(20 + x, y, 170 + x, y + 30));
            Input(c, box, value, command, tc.Card, tc.Text, R(175 + x, y, 460 + x, y + 30), 14, chars);
        }

        // Right-hand column of the listing editor: access and purchase limits (all listing types).
        private void BuildAccessColumn(CuiElementContainer c, string box, ThemeColors tc, ShopItem item, string cmd)
        {
            const int x = 460;
            Box(c, box, tc.Accent, R(468, 60, 470, 430));
            Label(c, box, "ACCESS & LIMITS", 14, tc.Accent, TextAnchor.MiddleLeft, R(20 + x, 370, 460 + x, 400));
            Field(c, box, tc, 322, "PERMISSION", item.Permission, cmd + " perm", x);
            Field(c, box, tc, 282, "COOLDOWN (SEC)", item.Cooldown.ToString(), cmd + " cooldown", x);
            Field(c, box, tc, 242, "DAILY LIMIT", item.DailyLimit.ToString(), cmd + " daily", x);
            Field(c, box, tc, 202, "WIPE LIMIT", item.WipeLimit.ToString(), cmd + " wipe", x);
            Field(c, box, tc, 162, "CARD ICON", item.Icon, cmd + " icon", x);
            Label(c, box, "URL, skin ID, shortname, or Icons/file.png. Empty = item icon.", 10, tc.SubText, TextAnchor.UpperLeft, R(175 + x, 144, 460 + x, 160), false);

            Label(c, box, "ONE PER PURCHASE", 12, tc.SubText, TextAnchor.MiddleLeft, R(20 + x, 102, 170 + x, 132));
            Btn(c, box, cmd + " one", item.LimitToOne ? tc.Accent : tc.Button, R(175 + x, 102, 275 + x, 132), item.LimitToOne ? "YES" : "NO", 13, item.LimitToOne ? tc.AccentText : tc.ButtonText);

            Label(c, box, "STEAM OWNERSHIP", 12, tc.SubText, TextAnchor.MiddleLeft, R(20 + x, 62, 170 + x, 92));
            Btn(c, box, cmd + " owned", item.RequireOwnership ? tc.Accent : tc.Button, R(175 + x, 62, 340 + x, 92),
                item.RequireOwnership ? "REQUIRED" : "NOT REQUIRED", 12, item.RequireOwnership ? tc.AccentText : tc.ButtonText);
            Label(c, box, item.RequireOwnership ? "Only owners can buy" : "Anyone can buy", 10, tc.SubText, TextAnchor.MiddleLeft, R(346 + x, 62, 460 + x, 92), false);

            Btn(c, box, $"{UiCmd} description {item.Id}", tc.Button, R(20 + x, 20, 460 + x, 48), "DESCRIPTION & CARD INFO", 12, tc.ButtonText);
        }

        private void BuildItemEditor(CuiElementContainer c, UiState s, ThemeColors tc)
        {
            ShopCategory cat;
            var item = FindItem(s.EditItem, out cat);
            if (item == null) { s.EditItem = 0; return; }

            string box = ModalBox(c, tc, 940, 480, $"EDIT LISTING #{item.Id}  <size=12><color={Rich(tc.SubText)}>in {cat.Name}</color></size>");
            string cmd = $"{UiCmd} iset {item.Id}";

            string iconBg = Box(c, box, tc.Panel, R(20, 366, 84, 430));
            ListIcon(c, iconBg, item.Icon, CardIcon(item), IsEvent(item) ? 0 : item.Skin, R(4, 4, 60, 60));
            Btn(c, box, $"{UiCmd} imove {item.Id}", tc.Button, R(310, 382, 460, 414), "MOVE TO NEXT CAT", 11, tc.ButtonText);
            BuildAccessColumn(c, box, tc, item, cmd);

            if (IsEvent(item))
            {
                BuildEventEditorFields(c, box, tc, item, cmd);
                return;
            }

            Label(c, box, $"{item.Shortname}\n<size=11>skin {item.Skin}</size>", 14, tc.Text, TextAnchor.MiddleLeft, R(96, 366, 230, 430));
            Btn(c, box, $"{UiCmd} pickopen item:{item.Id}", tc.Accent, R(236, 382, 304, 414), "CHANGE", 11, tc.AccentText);

            Field(c, box, tc, 322, "SHOP DISPLAY NAME", item.DisplayName, cmd + " name");
            Field(c, box, tc, 282, "PRICE (RP)", item.Price.ToString(), cmd + " price");
            Field(c, box, tc, 242, "AMOUNT GIVEN", item.Amount.ToString(), cmd + " amount");
            Field(c, box, tc, 202, "ITEM SHORTNAME", item.Shortname, cmd + " shortname");
            Field(c, box, tc, 162, "SKIN ID", item.Skin.ToString(), cmd + " skin");
            Field(c, box, tc, 122, "RENAME ITEM TO", item.ItemName, cmd + " itemname");

            Label(c, box, "BLUEPRINT", 12, tc.SubText, TextAnchor.MiddleLeft, R(20, 82, 170, 112));
            Btn(c, box, cmd + " bp", item.Blueprint ? tc.Accent : tc.Button, R(175, 82, 275, 112), item.Blueprint ? "YES" : "NO", 13, item.Blueprint ? tc.AccentText : tc.ButtonText);
            if (item.Commands != null && item.Commands.Count > 0)
                Label(c, box, $"{item.Commands.Count} command(s) - edit in data file", 11, tc.SubText, TextAnchor.MiddleLeft, R(285, 82, 460, 112), false);

            Label(c, box, "Type a value and press Enter to save each field.", 11, tc.SubText, TextAnchor.MiddleCenter, R(20, 58, 460, 78), false);
            Btn(c, box, $"{UiCmd} idel {item.Id}", tc.Danger, R(20, 16, 160, 52), "DELETE", 14, tc.DangerText);
            Btn(c, box, $"{UiCmd} done", tc.Accent, R(320, 16, 460, 52), "DONE", 14, tc.AccentText);
        }

        private void BuildTradeEditor(CuiElementContainer c, UiState s, ThemeColors tc)
        {
            var ex = data.Exchanges.Find(x => x.Id == s.EditTrade);
            if (ex == null) { s.EditTrade = 0; return; }

            string box = ModalBox(c, tc, 480, 500, $"EDIT TRADE #{ex.Id}");
            string cmd = $"{UiCmd} tset {ex.Id}";
            string catLabel = string.IsNullOrEmpty(ex.Category) ? "OTHER" : ex.Category.ToUpperInvariant();
            Btn(c, box, $"{UiCmd} tcatnext {ex.Id}", tc.Button, R(250, 424, 460, 452), $"CATEGORY: {catLabel}  >", 11, tc.ButtonText);

            Label(c, box, "PLAYER GIVES", 13, tc.Accent, TextAnchor.MiddleLeft, R(20, 424, 460, 450));
            Field(c, box, tc, 392, "SHORTNAME", ex.InShortname, cmd + " in");
            Field(c, box, tc, 356, "AMOUNT", ex.InAmount.ToString(), cmd + " inamt");
            Field(c, box, tc, 320, "SKIN ID", ex.InSkin.ToString(), cmd + " inskin");

            Label(c, box, "PLAYER RECEIVES", 13, tc.Accent, TextAnchor.MiddleLeft, R(20, 284, 460, 310));
            Field(c, box, tc, 252, "SHORTNAME", ex.OutShortname, cmd + " out");
            Field(c, box, tc, 216, "AMOUNT", ex.OutAmount.ToString(), cmd + " outamt");
            Field(c, box, tc, 180, "SKIN ID", ex.OutSkin.ToString(), cmd + " outskin");
            Field(c, box, tc, 144, "RENAME ITEM TO", ex.OutName, cmd + " outname");

            Btn(c, box, $"{UiCmd} pickopen tin:{ex.Id}", tc.Accent, R(20, 100, 125, 132), "GIVES: PICK", 10, tc.AccentText);
            Btn(c, box, $"{UiCmd} theld {ex.Id} in", tc.Button, R(130, 100, 235, 132), "GIVES: HELD", 10, tc.ButtonText);
            Btn(c, box, $"{UiCmd} pickopen tout:{ex.Id}", tc.Accent, R(245, 100, 350, 132), "RECEIVES: PICK", 10, tc.AccentText);
            Btn(c, box, $"{UiCmd} theld {ex.Id} out", tc.Button, R(355, 100, 460, 132), "RECEIVES: HELD", 10, tc.ButtonText);

            Label(c, box, "Press Enter to save each field. Use 'rp' as the shortname for Server Rewards points.", 11, tc.SubText, TextAnchor.MiddleCenter, R(20, 62, 460, 92), false);
            Btn(c, box, $"{UiCmd} tdel {ex.Id}", tc.Danger, R(20, 16, 160, 52), "DELETE", 14, tc.DangerText);
            Btn(c, box, $"{UiCmd} done", tc.Accent, R(320, 16, 460, 52), "DONE", 14, tc.AccentText);
        }

        #endregion

        #region Kits

        // Kits tab is the wipe-unlock gate. default_unlocked is an Oxide group maintained by
        // NightshadeDefaultUnlocked. That group is granted nightshadeshop.wipelock, and shop
        // categories such as Halloween already require that permission through CanSee.
        private bool CanSeeKits(BasePlayer p) => CanSee(p, PermWipeLock);

        private void ChatOpenKits(BasePlayer p, string command, string[] args)
        {
            if (!CanUse(p)) { SendReply(p, Msg("NoPermission", p)); return; }
            if (!CanSeeKits(p))
            {
                SendReply(p, "The Kits tab opens with wipe unlock (default_unlocked).");
                return;
            }
            var s = S(p);
            s.Tab = 2;
            s.BuyItem = 0;
            s.BuyToken = null;
            s.EditItem = s.EditTrade = 0;
            s.EditKitId = null;
            s.PickTarget = null;
            s.EventMenu = false;
            s.DescriptionEdit = false;
            s.Status = "";
            OpenUi(p);
        }

        private KitDef FindKit(string id)
        {
            if (kitFile?.Kits == null || string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < kitFile.Kits.Count; i++)
                if (string.Equals(kitFile.Kits[i].Id, id, StringComparison.OrdinalIgnoreCase))
                    return kitFile.Kits[i];
            return null;
        }

        private static bool IsVipPerkPerm(string perm) =>
            string.Equals((perm ?? "").Trim(), PermVipPerk, StringComparison.OrdinalIgnoreCase);

        private static bool IsVipXPerm(string perm) =>
            string.Equals((perm ?? "").Trim(), PermVipXPerk, StringComparison.OrdinalIgnoreCase);

        private static string LockLabel(string perm)
        {
            if (IsVipPerkPerm(perm)) return "VIP ONLY";
            if (IsVipXPerm(perm)) return "VIPX ONLY";
            return "LOCKED";
        }

        private KitClaim ClaimOf(string userId, string kitId, bool create)
        {
            if (kitFile.Claims == null) kitFile.Claims = new Dictionary<string, Dictionary<string, KitClaim>>();
            Dictionary<string, KitClaim> map;
            if (!kitFile.Claims.TryGetValue(userId, out map) || map == null)
            {
                if (!create) return null;
                map = new Dictionary<string, KitClaim>();
                kitFile.Claims[userId] = map;
            }
            KitClaim claim;
            if (!map.TryGetValue(kitId, out claim) || claim == null)
            {
                if (!create) return null;
                claim = new KitClaim();
                map[kitId] = claim;
            }
            return claim;
        }

        private static DateTime AsUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc) return value;
            if (value.Kind == DateTimeKind.Local) return value.ToUniversalTime();
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private double CooldownLeft(BasePlayer p, KitDef kit)
        {
            if (kit == null || kit.CooldownSeconds <= 0 || p == null) return 0;
            var claim = ClaimOf(p.UserIDString, kit.Id, false);
            if (claim == null || claim.LastClaim == DateTime.MinValue) return 0;
            double left = kit.CooldownSeconds - (DateTime.UtcNow - AsUtc(claim.LastClaim)).TotalSeconds;
            return left > 0 ? left : 0;
        }

        private int UsesRemaining(BasePlayer p, KitDef kit)
        {
            if (kit == null || kit.MaxUsesPerWipe <= 0) return int.MaxValue;
            var claim = p == null ? null : ClaimOf(p.UserIDString, kit.Id, false);
            int used = claim?.WipeUses ?? 0;
            return Math.Max(0, kit.MaxUsesPerWipe - used);
        }

        private static string FormatSpan(double seconds)
        {
            if (seconds < 0) seconds = 0;
            int total = (int)Math.Ceiling(seconds);
            int days = total / 86400;
            int hours = (total % 86400) / 3600;
            int minutes = (total % 3600) / 60;
            if (days > 0) return hours > 0 ? days + "d " + hours + "h" : days + "d";
            if (hours > 0) return minutes > 0 ? hours + "h " + minutes + "m" : hours + "h";
            if (minutes > 0) return minutes + "m";
            return Math.Max(1, total) + "s";
        }

        private static string HoursText(int seconds)
        {
            double hours = seconds / 3600d;
            if (Math.Abs(hours - Math.Round(hours)) < 0.001d)
                return Math.Round(hours).ToString("0", CultureInfo.InvariantCulture);
            return hours.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private string KitStatusText(BasePlayer p, KitDef kit)
        {
            double left = CooldownLeft(p, kit);
            string cool = left <= 0 ? "Ready" : "Cooldown " + FormatSpan(left);
            string uses = kit.MaxUsesPerWipe <= 0
                ? "No wipe cap"
                : "Uses " + UsesRemaining(p, kit) + "/" + kit.MaxUsesPerWipe;
            string perm = string.IsNullOrWhiteSpace(kit.Permission) ? "Anyone with Kits access" : kit.Permission.Trim();
            return cool + "  |  " + uses + "  |  " + perm;
        }

        private bool HasKitPerm(BasePlayer p, KitDef kit)
        {
            if (p == null || kit == null) return false;
            string perm = (kit.Permission ?? "").Trim();
            if (perm.Length == 0) return true;
            return permission.UserHasPermission(p.UserIDString, perm);
        }

        private void ClaimKit(BasePlayer p, UiState s, string id)
        {
            if (!CanSeeKits(p))
            {
                SetStatus(s, "The Kits tab is available after wipe unlock.", true);
                Fx(p, FxDeny);
                return;
            }
            var kit = FindKit(id);
            if (kit == null)
            {
                SetStatus(s, "That kit no longer exists.", true);
                Fx(p, FxDeny);
                return;
            }
            string deny;
            if (!TryClaimKit(p, kit, out deny))
            {
                SetStatus(s, deny, true);
                Fx(p, FxDeny);
                return;
            }
            SetStatus(s, kit.Name + " claimed.", false);
            Fx(p, FxBuy);
        }

        private bool TryClaimKit(BasePlayer p, KitDef kit, out string deny)
        {
            deny = "";
            if (p == null || kit == null) { deny = "That kit no longer exists."; return false; }
            if (!CanSeeKits(p)) { deny = "The Kits tab is available after wipe unlock."; return false; }
            string perm = (kit.Permission ?? "").Trim();
            if (perm.Length > 0 && !permission.UserHasPermission(p.UserIDString, perm))
            {
                deny = IsVipPerkPerm(perm) ? "VIP only. You need " + PermVipPerk + "."
                    : IsVipXPerm(perm) ? "VIPx only. You need " + PermVipXPerk + "."
                    : "You need " + perm + " to claim this kit.";
                return false;
            }
            double left = CooldownLeft(p, kit);
            if (left > 0)
            {
                deny = kit.Name + " is on cooldown. Available in " + FormatSpan(left) + ".";
                return false;
            }
            if (UsesRemaining(p, kit) <= 0)
            {
                deny = "No uses left this wipe.";
                return false;
            }
            if (!TryGiveKit(p, kit))
            {
                deny = "Your inventory is full. Make space and try again. The cooldown was not used.";
                return false;
            }
            var claim = ClaimOf(p.UserIDString, kit.Id, true);
            claim.LastClaim = DateTime.UtcNow;
            if (kit.MaxUsesPerWipe > 0) claim.WipeUses++;
            SaveKits();
            Log(p.displayName + " (" + p.UserIDString + ") claimed kit " + kit.Id + " (" + kit.Name + ")");
            return true;
        }

        private bool TryGiveKit(BasePlayer p, KitDef kit)
        {
            var items = kit.Items ?? new List<KitStack>();
            var main = p.inventory.containerMain;
            var belt = p.inventory.containerBelt;
            int slotsNeeded = 0;
            bool any = false;
            for (int i = 0; i < items.Count; i++)
            {
                var stack = items[i];
                if (stack == null || stack.Amount < 1) continue;
                var def = Def(stack.Shortname);
                if (def == null)
                {
                    PrintWarning("Kit " + kit.Id + " has an unknown shortname: " + stack.Shortname);
                    continue;
                }
                any = true;
                int remaining = stack.Amount;
                remaining -= PartialStackRoom(main, def, stack.Skin);
                remaining -= PartialStackRoom(belt, def, stack.Skin);
                if (remaining > 0)
                {
                    int stackSize = Math.Max(1, def.stackable);
                    slotsNeeded += (remaining + stackSize - 1) / stackSize;
                }
            }
            if (!any) return false;
            int freeSlots = CountFreeSlots(main) + CountFreeSlots(belt);
            if (slotsNeeded > freeSlots) return false;
            for (int i = 0; i < items.Count; i++)
            {
                var stack = items[i];
                if (stack == null || stack.Amount < 1) continue;
                var def = Def(stack.Shortname);
                if (def == null) continue;
                var item = ItemManager.Create(def, stack.Amount, stack.Skin);
                if (item == null) continue;
                if (!p.inventory.GiveItem(item))
                    item.Drop(p.GetDropPosition(), p.GetDropVelocity());
            }
            return true;
        }

        private static int CountFreeSlots(ItemContainer container)
        {
            if (container == null) return 0;
            int free = 0;
            for (int i = 0; i < container.capacity; i++)
                if (container.GetSlot(i) == null) free++;
            return free;
        }

        private static int PartialStackRoom(ItemContainer container, ItemDefinition def, ulong skin)
        {
            if (container == null) return 0;
            int room = 0;
            for (int i = 0; i < container.itemList.Count; i++)
            {
                var slot = container.itemList[i];
                if (slot != null && slot.info == def && slot.skin == skin && slot.amount < slot.MaxStackable())
                    room += slot.MaxStackable() - slot.amount;
            }
            return room;
        }

        private bool SetKitField(KitDef kit, string field, string value)
        {
            if (kit == null) return false;
            value = value ?? "";
            switch (field)
            {
                case "name":
                    if (string.IsNullOrWhiteSpace(value)) return false;
                    kit.Name = value.Trim();
                    return true;
                case "icon":
                    kit.Icon = value.Trim();
                    return true;
                case "perm":
                    kit.Permission = value.Trim();
                    return true;
                case "cooldown":
                {
                    double hours;
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out hours) || hours < 0 || hours > 8760)
                        return false;
                    kit.CooldownSeconds = (int)Math.Round(hours * 3600d);
                    return true;
                }
                case "uses":
                {
                    int uses;
                    if (!int.TryParse(value, out uses) || uses < 0) return false;
                    kit.MaxUsesPerWipe = uses;
                    return true;
                }
            }
            return false;
        }

        private bool AddKitStack(KitDef kit, string shortname, string amountText, string skinText)
        {
            if (kit == null) return false;
            shortname = (shortname ?? "").Trim();
            if (Def(shortname) == null) return false;
            int amount;
            if (!int.TryParse(amountText, out amount) || amount < 1) return false;
            ulong skin = 0;
            if (!string.IsNullOrWhiteSpace(skinText) && !ulong.TryParse(skinText.Trim(), out skin)) return false;
            if (kit.Items == null) kit.Items = new List<KitStack>();
            for (int i = 0; i < kit.Items.Count; i++)
            {
                var existing = kit.Items[i];
                if (existing != null && string.Equals(existing.Shortname, shortname, StringComparison.OrdinalIgnoreCase) && existing.Skin == skin)
                {
                    existing.Amount += amount;
                    return true;
                }
            }
            kit.Items.Add(new KitStack { Shortname = shortname, Amount = amount, Skin = skin });
            return true;
        }

        private int CopyInventoryToKit(BasePlayer p, KitDef kit)
        {
            if (p?.inventory == null || kit == null) return 0;
            if (kit.Items == null) kit.Items = new List<KitStack>();
            var found = new List<KitStack>();
            TakeContainer(p.inventory.containerMain, found);
            TakeContainer(p.inventory.containerBelt, found);
            int added = 0;
            for (int i = 0; i < found.Count; i++)
            {
                var stack = found[i];
                bool merged = false;
                for (int n = 0; n < kit.Items.Count; n++)
                {
                    var existing = kit.Items[n];
                    if (existing != null && string.Equals(existing.Shortname, stack.Shortname, StringComparison.OrdinalIgnoreCase) && existing.Skin == stack.Skin)
                    {
                        existing.Amount += stack.Amount;
                        merged = true;
                        break;
                    }
                }
                if (!merged) kit.Items.Add(stack);
                added++;
            }
            return added;
        }

        private static void TakeContainer(ItemContainer container, List<KitStack> found)
        {
            if (container?.itemList == null) return;
            for (int i = 0; i < container.itemList.Count; i++)
            {
                var item = container.itemList[i];
                if (item?.info == null || item.amount < 1 || string.IsNullOrEmpty(item.info.shortname)) continue;
                bool merged = false;
                for (int n = 0; n < found.Count; n++)
                {
                    if (string.Equals(found[n].Shortname, item.info.shortname, StringComparison.OrdinalIgnoreCase) && found[n].Skin == item.skin)
                    {
                        found[n].Amount += item.amount;
                        merged = true;
                        break;
                    }
                }
                if (!merged) found.Add(new KitStack { Shortname = item.info.shortname, Amount = item.amount, Skin = item.skin });
            }
        }

        private void DrawTopTabs(CuiElementContainer c, string parent, BasePlayer p, UiState s, ThemeColors tc)
        {
            var entries = new List<KeyValuePair<int, string>>
            {
                new KeyValuePair<int, string>(0, "SHOP"),
                new KeyValuePair<int, string>(1, "EVENTS")
            };
            if (CanSeeKits(p)) entries.Add(new KeyValuePair<int, string>(2, "KITS"));
            if (CanExchange(p))
                entries.Add(new KeyValuePair<int, string>(3, string.IsNullOrEmpty(config.ExchangeTabName) ? "ADMIN SHOP" : config.ExchangeTabName));

            const int barW = 1100;
            const int margin = 8;
            const int gap = 6;
            int count = entries.Count;
            int usable = barW - margin * 2 - gap * Math.Max(0, count - 1);
            int width = Math.Max(1, usable / count);
            int extra = usable - width * count;
            int x = margin;
            for (int i = 0; i < count; i++)
            {
                int w = width + (i < extra ? 1 : 0);
                bool on = s.Tab == entries[i].Key;
                Btn(c, parent, UiCmd + " tab " + entries[i].Key, on ? tc.Accent : tc.Button, R(x, 6, x + w, 34), entries[i].Value, 15, on ? tc.AccentText : tc.ButtonText);
                x += w + gap;
            }
        }

        private void BuildKits(CuiElementContainer c, BasePlayer p, UiState s, ThemeColors tc)
        {
            string panel = Box(c, UiFrame, tc.Panel, R(16, 56, 1084, 510));
            Label(c, panel, "KITS", 20, tc.Text, TextAnchor.MiddleLeft, R(16, 414, 200, 446));
            Label(c, panel, "Open while wipe unlock (default_unlocked) is active.", 12, tc.SubText, TextAnchor.MiddleLeft, R(16, 392, 780, 414), false);
            if (s.Admin)
                Btn(c, panel, UiCmd + " kitnew", tc.Accent, R(900, 414, 1052, 444), "+ NEW KIT", 13, tc.AccentText);

            var list = kitFile?.Kits ?? new List<KitDef>();
            const int rowH = 156;
            const int gap = 8;
            string area = Scroll(c, panel, R(8, 8, 1060, 386), 378, Math.Max(1, list.Count) * (rowH + gap), tc);
            if (list.Count == 0)
            {
                Label(c, area, s.Admin ? "No kits yet. Click + NEW KIT." : "No kits right now.", 16, tc.SubText, TextAnchor.MiddleCenter, TL(0, 150, 1040, 40));
                return;
            }
            for (int i = 0; i < list.Count; i++)
                DrawKitCard(c, area, p, s, tc, list[i], i * (rowH + gap), rowH);
        }

        private void DrawKitCard(CuiElementContainer c, string parent, BasePlayer p, UiState s, ThemeColors tc, KitDef kit, int y, int height)
        {
            const int width = 1040;
            string row = Box(c, parent, tc.Card, TL(0, y, width, height));
            string icon = string.IsNullOrWhiteSpace(kit.Icon) ? FirstKitIcon(kit) : kit.Icon;
            string iconBg = Box(c, row, tc.Panel, R(12, 30, 116, 134));
            ListIcon(c, iconBg, icon, Def(FirstKitIcon(kit)), 0, R(6, 6, 98, 98));

            bool admin = s.Admin;
            int textRight = admin ? 750 : 876;
            Label(c, row, string.IsNullOrWhiteSpace(kit.Name) ? "KIT" : kit.Name.ToUpperInvariant(), 16, tc.Text, TextAnchor.MiddleLeft, R(128, 124, textRight, 148));
            Label(c, row, KitStatusText(p, kit), 11, tc.SubText, TextAnchor.MiddleLeft, R(128, 102, textRight, 124), false);

            DrawKitContents(c, row, tc, kit.Items, 128, 10, textRight - 128, 88);

            bool allowed = HasKitPerm(p, kit);
            double left = CooldownLeft(p, kit);
            int uses = UsesRemaining(p, kit);
            int claimX = admin ? 760 : 888;
            int claimR = admin ? 876 : 1024;
            if (!allowed)
            {
                string locked = Box(c, row, tc.Button, R(claimX, 48, claimR, 92));
                Label(c, locked, LockLabel(kit.Permission), 13, tc.SubText, TextAnchor.MiddleCenter, Full());
            }
            else if (left > 0)
            {
                string wait = Box(c, row, tc.Button, R(claimX, 48, claimR, 92));
                Label(c, wait, "COOLDOWN", 12, tc.SubText, TextAnchor.MiddleCenter, Full());
            }
            else if (uses <= 0)
            {
                string none = Box(c, row, tc.Button, R(claimX, 48, claimR, 92));
                Label(c, none, "NO USES", 12, tc.SubText, TextAnchor.MiddleCenter, Full());
            }
            else
                Btn(c, row, UiCmd + " kitclaim " + kit.Id, tc.Accent, R(claimX, 48, claimR, 92), "CLAIM", 14, tc.AccentText);

            if (admin)
            {
                Btn(c, row, UiCmd + " kitedit " + kit.Id, tc.Button, R(892, 100, 1024, 132), "EDIT", 12, tc.ButtonText);
                Btn(c, row, UiCmd + " kitdel " + kit.Id, tc.Danger, R(892, 12, 1024, 40), "DEL", 12, tc.DangerText);
            }
        }

        private static string FirstKitIcon(KitDef kit)
        {
            if (kit?.Items == null) return "supply.signal";
            for (int i = 0; i < kit.Items.Count; i++)
                if (kit.Items[i] != null && !string.IsNullOrEmpty(kit.Items[i].Shortname))
                    return kit.Items[i].Shortname;
            return "supply.signal";
        }

        private void DrawKitContents(CuiElementContainer c, string parent, ThemeColors tc, List<KitStack> items, int x, int y, int width, int height)
        {
            if (items == null || items.Count == 0)
            {
                Label(c, parent, "This kit is empty.", 12, tc.SubText, TextAnchor.MiddleLeft, R(x, y, x + width, y + height), false);
                return;
            }
            const int cell = 40;
            const int gap = 4;
            int stride = cell + gap;
            int cols = Math.Max(1, (width + gap) / stride);
            int rows = (items.Count + cols - 1) / cols;
            if (rows <= 2)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    int col = i % cols;
                    int row = i / cols;
                    int cx = x + col * stride;
                    int cy = y + height - (row + 1) * cell - row * gap;
                    DrawKitStack(c, parent, tc, items[i], cx, cy, cell);
                }
                return;
            }
            int contentW = items.Count * stride;
            string strip = HScroll(c, parent, R(x, y, x + width, y + height), contentW, height, tc);
            for (int i = 0; i < items.Count; i++)
                DrawKitStack(c, strip, tc, items[i], i * stride, Math.Max(0, height - cell - 6), cell);
        }

        private void DrawKitStack(CuiElementContainer c, string parent, ThemeColors tc, KitStack stack, int x, int y, int size)
        {
            if (stack == null) return;
            string cell = Box(c, parent, tc.Panel, R(x, y, x + size, y + size));
            var def = Def(stack.Shortname);
            if (def != null) Icon(c, cell, def, stack.Skin, R(2, 12, size - 2, size - 2));
            else Label(c, cell, "?", 14, tc.Danger, TextAnchor.MiddleCenter, R(0, 12, size, size));
            Label(c, cell, "x" + Math.Max(0, stack.Amount).ToString(CultureInfo.InvariantCulture), 9, tc.Text, TextAnchor.MiddleCenter, R(0, 0, size, 14));
        }

        private string HScroll(CuiElementContainer c, string parent, CuiRectTransformComponent rect, int contentW, int viewportH, ThemeColors tc)
        {
            string name = CuiHelper.GetGuid();
            int w = Math.Max(contentW, 1);
            c.Add(new CuiElement
            {
                Name = name,
                Parent = parent,
                Components =
                {
                    new CuiImageComponent { Color = "0 0 0 0", BlocksRaycast = true },
                    new CuiNeedsCursorComponent(),
                    new CuiScrollViewComponent
                    {
                        Horizontal = true,
                        Vertical = false,
                        MovementType = ScrollRect.MovementType.Clamped,
                        Elasticity = 0.1f,
                        Inertia = true,
                        DecelerationRate = 0.135f,
                        ScrollSensitivity = 40f,
                        ContentTransform = new CuiRectTransform { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "0 0", OffsetMax = w + " " + viewportH },
                        HorizontalScrollbar = new CuiScrollbar { Size = 4, AutoHide = true, HandleColor = C(tc.Accent), HighlightColor = C(tc.Accent), PressedColor = C(tc.Accent), TrackColor = C(tc.Panel) }
                    },
                    rect
                }
            });
            return name;
        }

        private void BuildKitEditor(CuiElementContainer c, UiState s, ThemeColors tc)
        {
            var kit = FindKit(s.EditKitId);
            if (kit == null) { s.EditKitId = null; return; }
            if (kit.Items == null) kit.Items = new List<KitStack>();
            string box = ModalBox(c, tc, 980, 580, "EDIT KIT  " + kit.Name);
            string cmd = UiCmd + " kitset " + kit.Id;

            Field(c, box, tc, 490, "NAME", kit.Name, cmd + " name", 0, 64);
            Field(c, box, tc, 450, "ICON", kit.Icon, cmd + " icon", 0, 256);
            Btn(c, box, UiCmd + " kiticonheld " + kit.Id, tc.Button, R(468, 450, 560, 480), "HELD ICON", 11, tc.ButtonText);
            string preview = Box(c, box, tc.Panel, R(568, 446, 606, 484));
            ListIcon(c, preview, string.IsNullOrWhiteSpace(kit.Icon) ? FirstKitIcon(kit) : kit.Icon, Def(FirstKitIcon(kit)), 0, R(4, 4, 34, 34));
            Field(c, box, tc, 410, "PERMISSION", kit.Permission, cmd + " perm", 0, 128);
            Label(c, box, "Empty = Kits access. vipperk = VIP ONLY. vipxperk = VIPX ONLY. Icon: URL, skin, shortname, or VIP_Daily.png in Icons/.", 10, tc.SubText, TextAnchor.MiddleLeft, R(175, 388, 960, 408), false);
            Field(c, box, tc, 350, "COOLDOWN (HOURS)", HoursText(kit.CooldownSeconds), cmd + " cooldown");
            Field(c, box, tc, 310, "MAX USES / WIPE", kit.MaxUsesPerWipe.ToString(), cmd + " uses");
            Label(c, box, "0 uses means no wipe cap. Cooldown still applies.", 10, tc.SubText, TextAnchor.MiddleLeft, R(175, 288, 560, 308), false);

            Label(c, box, "SHORTNAME", 10, tc.SubText, TextAnchor.MiddleLeft, R(20, 258, 110, 278));
            Input(c, box, s.KitShort, UiCmd + " kitdraft " + kit.Id + " short", tc.Card, tc.Text, R(20, 226, 220, 256), 12, 64);
            Label(c, box, "AMOUNT", 10, tc.SubText, TextAnchor.MiddleLeft, R(228, 258, 300, 278));
            Input(c, box, s.KitAmt, UiCmd + " kitdraft " + kit.Id + " amt", tc.Card, tc.Text, R(228, 226, 310, 256), 12, 8);
            Label(c, box, "SKIN", 10, tc.SubText, TextAnchor.MiddleLeft, R(318, 258, 430, 278));
            Input(c, box, s.KitSkin, UiCmd + " kitdraft " + kit.Id + " skin", tc.Card, tc.Text, R(318, 226, 470, 256), 12, 24);
            Btn(c, box, UiCmd + " kitadd " + kit.Id, tc.Accent, R(478, 226, 560, 256), "ADD", 12, tc.AccentText);
            Btn(c, box, UiCmd + " pickopen kitadd:" + kit.Id, tc.Button, R(568, 226, 680, 256), "PICK ITEM", 12, tc.ButtonText);
            Btn(c, box, UiCmd + " kitcopy " + kit.Id, tc.Button, R(688, 226, 880, 256), "COPY INVENTORY", 12, tc.ButtonText);

            Label(c, box, "CONTENTS", 13, tc.Accent, TextAnchor.MiddleLeft, R(20, 190, 200, 216));
            const int rowH = 36;
            int contentH = Math.Max(rowH, kit.Items.Count * rowH);
            string list = Scroll(c, box, R(16, 64, 964, 188), 124, contentH, tc);
            if (kit.Items.Count == 0)
                Label(c, list, "No items yet.", 13, tc.SubText, TextAnchor.MiddleLeft, TL(8, 8, 400, 28), false);
            for (int i = 0; i < kit.Items.Count; i++)
            {
                var stack = kit.Items[i];
                string line = Box(c, list, tc.Card, TL(0, i * rowH, 930, rowH - 2));
                var def = Def(stack.Shortname);
                if (def != null) Icon(c, line, def, stack.Skin, R(4, 2, 30, 30));
                Label(c, line, stack.Shortname, 12, tc.Text, TextAnchor.MiddleLeft, R(36, 0, 280, rowH - 2));
                Input(c, line, stack.Amount.ToString(), UiCmd + " kititem " + kit.Id + " " + i + " amt", tc.Panel, tc.Text, R(290, 3, 380, rowH - 5), 12, 8);
                Input(c, line, stack.Skin.ToString(), UiCmd + " kititem " + kit.Id + " " + i + " skin", tc.Panel, tc.Text, R(388, 3, 560, rowH - 5), 12, 24);
                Btn(c, line, UiCmd + " kitdelitem " + kit.Id + " " + i, tc.Danger, R(880, 4, 922, rowH - 6), "X", 12, tc.DangerText);
            }

            Btn(c, box, UiCmd + " kitdel " + kit.Id, tc.Danger, R(16, 14, 160, 50), "DELETE", 14, tc.DangerText);
            Btn(c, box, UiCmd + " done", tc.Accent, R(820, 14, 964, 50), "DONE", 14, tc.AccentText);
        }

        // Switches the four migrated kits off the old item-shortname icons, and points the
        // VIPx kits at nightshadeshop.vipxperk, only while those icons are still the originals.
        private bool ApplyStoredKitDefaults()
        {
            bool changed = false;
            changed |= RetargetMigratedKit(VipDailyId, "woodtea.advanced", DailyKitPng, null);
            changed |= RetargetMigratedKit(VipxDailyId, "healingtea.pure", DailyKitPng, PermVipXPerk);
            changed |= RetargetMigratedKit(VipFourId, "hazmatsuit", FourDayKitPng, null);
            changed |= RetargetMigratedKit(VipxFourId, "explosive.timed", FourDayKitPng, PermVipXPerk);
            return changed;
        }

        private bool RetargetMigratedKit(string id, string oldIcon, string png, string permIfStillDefault)
        {
            var kit = FindKit(id);
            if (kit == null) return false;
            string icon = (kit.Icon ?? "").Trim();
            bool stillDefault = icon.Length == 0 || string.Equals(icon, oldIcon, StringComparison.OrdinalIgnoreCase);
            if (!stillDefault) return false;
            bool changed = false;
            if (!string.Equals(icon, png, StringComparison.OrdinalIgnoreCase))
            {
                kit.Icon = png;
                changed = true;
            }
            if (!string.IsNullOrEmpty(permIfStillDefault))
            {
                string perm = (kit.Permission ?? "").Trim();
                if (!string.Equals(perm, permIfStillDefault, StringComparison.OrdinalIgnoreCase))
                {
                    kit.Permission = permIfStillDefault;
                    changed = true;
                }
            }
            return changed;
        }

        private string IconsDirectory()
        {
            return Path.Combine(Interface.Oxide.DataDirectory, "Nightshade", "NightshadeShop", "Icons");
        }

        private void EnsureIconsFolder()
        {
            string dir = IconsDirectory();
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        // "VIP_Daily.png" or "Icons/VIP_Daily.png" both mean a file in the Icons folder.
        private static string LocalPngName(string icon)
        {
            if (string.IsNullOrWhiteSpace(icon)) return null;
            string text = icon.Trim().Replace('\\', '/');
            if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;
            if (text.StartsWith("Icons/", StringComparison.OrdinalIgnoreCase))
                text = text.Substring("Icons/".Length);
            if (text.IndexOf('/') >= 0 || text.IndexOf("..", StringComparison.Ordinal) >= 0) return null;
            if (!text.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return null;
            return text;
        }

        private static bool IsLocalPng(string icon) => LocalPngName(icon) != null;

        // ImageLibrary stores the png and GetImage returns the id drawn by CuiRawImageComponent.Png.
        // The same Call form is used by NightshadeInfo and ServerRewards (file:// for a data-folder png).
        private string LocalPngId(string icon)
        {
            string name = LocalPngName(icon);
            if (name == null) return null;
            if (!ImageLibraryReady()) return null;
            if (!localPngQueued.Contains(name))
                QueueLocalPng(name);
            object stored = ImageLibrary.Call("HasImage", name, 0UL);
            if (!(stored is bool) || !(bool)stored) return null;
            string png = ImageLibrary.Call<string>("GetImage", name, 0UL);
            if (string.IsNullOrEmpty(png) || png == "0") return null;
            return png;
        }

        private bool ImageLibraryReady()
        {
            if (ImageLibrary != null && ImageLibrary.IsLoaded) return true;
            if (!imageLibraryWarned)
            {
                imageLibraryWarned = true;
                PrintWarning("ImageLibrary is not loaded. Local shop icons will use the default icon.");
            }
            return false;
        }

        private void QueueLocalPng(string name)
        {
            EnsureIconsFolder();
            string path = Path.Combine(IconsDirectory(), name);
            if (!File.Exists(path))
            {
                if (localPngWarned.Add(name))
                    PrintWarning("NightshadeShop icon file missing: " + name + ". Place it in data/Nightshade/NightshadeShop/Icons/. Using the default icon.");
                return;
            }
            try
            {
                if (new FileInfo(path).Length <= 0)
                {
                    if (localPngWarned.Add(name))
                        PrintWarning("NightshadeShop icon file is empty: " + name + ". Using the default icon.");
                    return;
                }
                string url = Path.Combine("file://" + Interface.Oxide.DataDirectory, "Nightshade", "NightshadeShop", "Icons", name);
                ImageLibrary.Call("AddImage", url, name, 0UL);
                localPngQueued.Add(name);
            }
            catch (Exception ex)
            {
                if (localPngWarned.Add(name))
                    PrintWarning("NightshadeShop could not read icon " + name + ": " + ex.Message + ". Using the default icon.");
            }
        }

        private void PreloadLocalIcons()
        {
            EnsureIconsFolder();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (kitFile?.Kits != null)
                foreach (var kit in kitFile.Kits)
                    NoteLocalIcon(names, kit.Icon);
            if (data?.Categories != null)
            {
                foreach (var cat in data.Categories)
                {
                    NoteLocalIcon(names, cat.Icon);
                    if (cat.Items == null) continue;
                    foreach (var item in cat.Items)
                        NoteLocalIcon(names, item.Icon);
                }
            }
            if (data?.TradeCategories != null)
                foreach (var trade in data.TradeCategories)
                    NoteLocalIcon(names, trade.Icon);
            foreach (var name in names)
                LocalPngId(name);
        }

        private static void NoteLocalIcon(HashSet<string> names, string icon)
        {
            string name = LocalPngName(icon);
            if (name != null) names.Add(name);
        }

        private bool MigrateVipPerkKits()
        {
            EnsureMigratedKit(VipDailyId, "VIP Daily Kit", DailyKitPng, PermVipPerk, 86400, DailyVipItems());
            EnsureMigratedKit(VipxDailyId, "VIPx Daily Kit", DailyKitPng, PermVipXPerk, 86400, DailyVipxItems());
            EnsureMigratedKit(VipFourId, "VIP 4-Day Kit", FourDayKitPng, PermVipPerk, 345600, FourVipItems());
            EnsureMigratedKit(VipxFourId, "VIPx 4-Day Kit", FourDayKitPng, PermVipXPerk, 345600, FourVipxItems());
            bool daily = ImportCooldownFile("VIPPerks_DailyKit", VipDailyId, VipxDailyId);
            bool four = ImportCooldownFile("VIPPerks_4DayKit", VipFourId, VipxFourId);
            if (daily && four)
                Puts("NightshadeShop migrated VIP perk kits into Kits.json.");
            return daily && four;
        }

        private void EnsureMigratedKit(string id, string name, string icon, string perm, int cooldown, KitStack[] items)
        {
            if (FindKit(id) != null) return;
            var kit = new KitDef
            {
                Id = id,
                Name = name,
                Icon = icon,
                Permission = perm,
                CooldownSeconds = cooldown,
                MaxUsesPerWipe = 0,
                Items = new List<KitStack>(items)
            };
            kitFile.Kits.Add(kit);
            int n;
            if (id.StartsWith("kit", StringComparison.Ordinal) && int.TryParse(id.Substring(3), out n) && n >= kitFile.NextId)
                kitFile.NextId = n + 1;
            if (kitFile.NextId < 5) kitFile.NextId = 5;
        }

        private bool ImportCooldownFile(string file, params string[] kitIds)
        {
            Dictionary<string, DateTime> rows = null;
            bool failed = false;
            try { rows = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, DateTime>>(file); }
            catch (Exception ex)
            {
                failed = true;
                PrintWarning("Could not read " + file + ": " + ex.Message);
            }
            if (failed) return false;
            if (rows == null || rows.Count == 0)
            {
                try
                {
                    var numeric = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, DateTime>>(file);
                    if (numeric != null)
                    {
                        rows = new Dictionary<string, DateTime>();
                        foreach (var pair in numeric)
                            rows[pair.Key.ToString(CultureInfo.InvariantCulture)] = pair.Value;
                    }
                }
                catch (Exception ex)
                {
                    PrintWarning("Could not read " + file + ": " + ex.Message);
                    return false;
                }
            }
            if (rows == null) return true;
            foreach (var pair in rows)
            {
                if (string.IsNullOrEmpty(pair.Key) || pair.Value == DateTime.MinValue) continue;
                Dictionary<string, KitClaim> map;
                if (!kitFile.Claims.TryGetValue(pair.Key, out map) || map == null)
                {
                    map = new Dictionary<string, KitClaim>();
                    kitFile.Claims[pair.Key] = map;
                }
                for (int i = 0; i < kitIds.Length; i++)
                {
                    if (map.ContainsKey(kitIds[i])) continue;
                    map[kitIds[i]] = new KitClaim { LastClaim = AsUtc(pair.Value), WipeUses = 0 };
                }
            }
            return true;
        }

        private static KitStack StackItem(string shortname, int amount, ulong skin) =>
            new KitStack { Shortname = shortname, Amount = amount, Skin = skin };

        private static KitStack[] DailyVipItems() => new[]
        {
            StackItem("advanceharvestingtea", 1, 0), StackItem("healingtea.advanced", 1, 0), StackItem("oretea.advanced", 1, 0),
            StackItem("scraptea.advanced", 1, 0), StackItem("woodtea.advanced", 1, 0), StackItem("lowgradefuel", 1000, 0),
            StackItem("pumpkin", 10, 0), StackItem("bandage", 5, 0), StackItem("largemedkit", 5, 0), StackItem("syringe.medical", 10, 0),
            StackItem("sewingkit", 10, 0), StackItem("sheetmetal", 10, 0), StackItem("roadsigns", 10, 0), StackItem("metalpipe", 5, 0),
            StackItem("cctv.camera", 2, 0), StackItem("tarp", 4, 0)
        };

        private static KitStack[] DailyVipxItems() => new[]
        {
            StackItem("radiationresisttea.pure", 1, 0), StackItem("purecraftingtea_quality", 1, 0), StackItem("healingtea.pure", 2, 0),
            StackItem("bandage", 10, 0), StackItem("largemedkit", 5, 0), StackItem("syringe.medical", 20, 0), StackItem("sewingkit", 20, 0),
            StackItem("sheetmetal", 10, 0), StackItem("roadsigns", 10, 0), StackItem("metalpipe", 10, 0), StackItem("supply.signal", 1, 0)
        };

        private static KitStack[] FourVipItems() => new[]
        {
            StackItem("hazmatsuit", 1, 0), StackItem("ammo.pistol", 100, 0), StackItem("keycard_green", 1, 0), StackItem("bandage", 5, 0),
            StackItem("bearmeat.cooked", 1, 0), StackItem("supply.signal", 1, 0), StackItem("supply.signal", 1, 2905355269UL),
            StackItem("supply.signal", 1, 2920175997UL)
        };

        private static KitStack[] FourVipxItems() => new[]
        {
            StackItem("explosive.timed", 2, 0), StackItem("gunpowder", 5, 0), StackItem("supply.signal", 1, 2905355269UL),
            StackItem("supply.signal", 1, 2920175997UL), StackItem("diesel_barrel", 5, 0), StackItem("keycard_green", 1, 0),
            StackItem("keycard_blue", 1, 0), StackItem("supply.signal", 2, 0), StackItem("syringe.medical", 40, 0)
        };

        #endregion
    }
}
