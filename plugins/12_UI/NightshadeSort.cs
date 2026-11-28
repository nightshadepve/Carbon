// NightshadeSort
// Sort menu and Transfer All for storage containers the player is looting.
// Header buttons use the same bottom-center layout as Sort Button 2.7.0
// (plugins/12_UI/SortButton.cs): anchor 0.5 0, default X 476.5, orange order
// button 17px wide, green Sort button 79px wide, height 23. Y for a resizable
// panel is 113.5 + 62 * row count. Named panels keep that plugin's fixed Y.
// Sort Button's source is not modified. Overlapping containers are turned off
// in configs/SortButton.json.

using System;
using System.Collections.Generic;
using System.Globalization;
#if !NIGHTSHADE_SORT_TESTS
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;
#endif

namespace Carbon.Plugins
{
    // Sort order and container eligibility. No Rust types, so the same checks
    // can run under NIGHTSHADE_SORT_TESTS without the game host.
    public static class NightshadeSortRules
    {
        public const float HeaderLeft = 192.5f;
        public const float HeaderWidth = 380f;
        public const float CogWidth = 17f;
        public const float SortWidth = 79f;
        public const float DefaultButtonX = HeaderLeft + HeaderWidth - CogWidth - SortWidth;
        public const float BaseY = 113.5f;
        public const float RowHeight = 62f;

        public enum SortMode
        {
            CategorySkin = 0,
            Category = 1,
            Name = 2,
            Amount = 3
        }

        public struct SortKey
        {
            public int Category;
            public string Shortname;
            public string DisplayName;
            public ulong Skin;
            public int Amount;
            public int Position;
        }

        public struct ContainerQuery
        {
            public bool IsStorage;
            public bool IsVending;
            public bool IsShop;
            public bool IsOven;
            public bool IsRecycler;
            public bool IsComposter;
            public bool IsMixing;
            public bool IsLootCrate;
            public bool IsCorpse;
            public bool IsDropped;
            public bool IsDropBox;
            public bool PlayerBehind;
            public bool IsLocked;
            public bool InputBlocked;
            public bool StillLooting;
            public bool InRange;
            public bool Lootable;
            public bool IndustrialPowered;
            public bool IsPlayerMain;
            public bool IsPlayerBelt;
            public bool IsPlayerWear;
            public bool AllowCorpse;
            public bool AllowDropped;
            public bool AllowWorldLoot;
            public bool HasAllowList;
            public bool InAllowList;
            public bool InBlacklist;
            public int Capacity;
        }

        public struct UiRect
        {
            public float XMin;
            public float YMin;
            public float XMax;
            public float YMax;
        }

        public static int ClampMode(int mode)
        {
            if (mode < (int)SortMode.CategorySkin || mode > (int)SortMode.Amount)
                return (int)SortMode.CategorySkin;
            return mode;
        }

        public static int Compare(SortKey a, SortKey b, SortMode mode)
        {
            int cmp;
            switch (mode)
            {
                case SortMode.Category:
                    cmp = a.Category.CompareTo(b.Category);
                    if (cmp != 0) return cmp;
                    cmp = CompareText(a.DisplayName, b.DisplayName);
                    if (cmp != 0) return cmp;
                    cmp = CompareText(a.Shortname, b.Shortname);
                    if (cmp != 0) return cmp;
                    break;
                case SortMode.Name:
                    cmp = CompareText(a.DisplayName, b.DisplayName);
                    if (cmp != 0) return cmp;
                    cmp = CompareText(a.Shortname, b.Shortname);
                    if (cmp != 0) return cmp;
                    break;
                case SortMode.Amount:
                    cmp = b.Amount.CompareTo(a.Amount);
                    if (cmp != 0) return cmp;
                    cmp = CompareText(a.DisplayName, b.DisplayName);
                    if (cmp != 0) return cmp;
                    cmp = CompareText(a.Shortname, b.Shortname);
                    if (cmp != 0) return cmp;
                    break;
                default:
                    cmp = a.Category.CompareTo(b.Category);
                    if (cmp != 0) return cmp;
                    cmp = CompareText(a.Shortname, b.Shortname);
                    if (cmp != 0) return cmp;
                    cmp = a.Skin.CompareTo(b.Skin);
                    if (cmp != 0) return cmp;
                    cmp = b.Amount.CompareTo(a.Amount);
                    if (cmp != 0) return cmp;
                    break;
            }
            cmp = a.Skin.CompareTo(b.Skin);
            if (cmp != 0) return cmp;
            cmp = b.Amount.CompareTo(a.Amount);
            if (cmp != 0) return cmp;
            return a.Position.CompareTo(b.Position);
        }

        public static int CompareText(string a, string b)
        {
            if (a == null) a = "";
            if (b == null) b = "";
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // One stack the sorter may combine. Blocked items (contents, ammo,
        // attachments, or anything else vanilla would refuse) stay alone.
        // MaxStack is item.MaxStackable(), which already includes info.stackable
        // unless a stack-size plugin hook replaces it.
        public struct StackUnit
        {
            public int ItemId;
            public ulong Skin;
            public string Name;
            public string Text;
            public int Amount;
            public int MaxStack;
            public bool Blocked;
        }

        public static bool SameStack(StackUnit a, StackUnit b)
        {
            if (a.Blocked || b.Blocked) return false;
            if (a.ItemId == 0 || a.ItemId != b.ItemId) return false;
            if (a.Skin != b.Skin) return false;
            if (a.MaxStack <= 1 || b.MaxStack <= 1) return false;
            if (!string.Equals(a.Name ?? "", b.Name ?? "", StringComparison.Ordinal)) return false;
            if (!string.Equals(a.Text ?? "", b.Text ?? "", StringComparison.Ordinal)) return false;
            return true;
        }

        // Pour later partial stacks into earlier ones, up to MaxStack.
        // Returns false when the total amount changes.
        public static bool Consolidate(List<StackUnit> items)
        {
            if (items == null) return true;
            int before = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Amount > 0) before += items[i].Amount;
            }
            for (int i = 0; i < items.Count; i++)
            {
                StackUnit dest = items[i];
                if (dest.Amount <= 0 || dest.Blocked || dest.MaxStack <= 1) continue;
                for (int j = i + 1; j < items.Count && dest.Amount < dest.MaxStack; j++)
                {
                    StackUnit src = items[j];
                    if (src.Amount <= 0 || !SameStack(dest, src)) continue;
                    int room = dest.MaxStack - dest.Amount;
                    if (room <= 0) break;
                    int move = src.Amount < room ? src.Amount : room;
                    if (move <= 0) continue;
                    dest.Amount += move;
                    src.Amount -= move;
                    items[j] = src;
                }
                items[i] = dest;
            }
            int after = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Amount > 0) after += items[i].Amount;
            }
            return after == before;
        }

        public static int PositiveStacks(List<StackUnit> items)
        {
            int count = 0;
            if (items == null) return 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Amount > 0) count++;
            }
            return count;
        }

        // Backpack loot uses the same bottom-center header as a resizable box.
        // offsetY is the 1-row button. Each extra row lifts it by rowHeight so it
        // stays on that page's header. Anchor stays 0.5 0.
        public static void ShiftPackButtonY(float yMin, float yMax, int capacity, float baseY, float rowHeight, int maxRows, out float shiftedMin, out float shiftedMax)
        {
            int rows = RowCount(capacity, maxRows);
            float oneRow = baseY + rowHeight;
            float header = baseY + rowHeight * rows;
            float extra = header - oneRow;
            shiftedMin = yMin + extra;
            shiftedMax = yMax + extra;
        }

        public static bool IsSupportedTarget(ContainerQuery query)
        {
            if (query.InBlacklist) return false;
            if (query.HasAllowList && !query.InAllowList) return false;
            if (query.Capacity < 1) return false;
            if (query.IsVending || query.IsShop || query.IsOven || query.IsRecycler || query.IsComposter || query.IsMixing)
                return false;
            if (query.IsCorpse) return query.AllowCorpse;
            if (query.IsDropped) return query.AllowDropped;
            if (query.IsLootCrate) return query.AllowWorldLoot;
            return query.IsStorage;
        }

        public static bool CanAct(ContainerQuery query)
        {
            if (!query.StillLooting || !query.InRange || !query.Lootable) return false;
            if (query.IsLocked || query.InputBlocked) return false;
            if (query.IsPlayerMain || query.IsPlayerBelt || query.IsPlayerWear) return false;
            if (query.IsDropBox && !query.PlayerBehind) return false;
            return IsSupportedTarget(query);
        }

        public static bool ShowSortButtons(ContainerQuery query)
        {
            return CanAct(query) && !query.IndustrialPowered && query.Capacity > 1;
        }

        public static bool IsProtectedPlayerSlot(bool belt, bool wear)
        {
            return belt || wear;
        }

        public static int RowCount(int capacity, int maxRows)
        {
            if (maxRows < 1) maxRows = 1;
            if (capacity < 1) capacity = 1;
            int rows = 1 + (capacity - 1) / 6;
            if (rows < 1) rows = 1;
            if (rows > maxRows) rows = maxRows;
            return rows;
        }

        public static bool TryHeaderY(string panel, int capacity, float baseY, float rowHeight, int maxRows, IDictionary<string, float> panelY, out float y)
        {
            float mapped;
            if (panelY != null && !string.IsNullOrEmpty(panel) && panelY.TryGetValue(panel, out mapped))
            {
                y = mapped;
                return true;
            }
            y = baseY + rowHeight * RowCount(capacity, maxRows);
            return true;
        }

        public static float HeaderHeight(string panel, float fallback, IDictionary<string, float> heights)
        {
            float mapped;
            if (heights != null && !string.IsNullOrEmpty(panel) && heights.TryGetValue(panel, out mapped) && mapped > 0f)
                return mapped;
            if (fallback <= 0f) return 23f;
            return fallback;
        }

        // Sort Button shifts the header buttons left when an unpowered industrial
        // adaptor is attached and the configured X is still at the default edge.
        public static float ShiftedButtonX(float configuredX, bool industrialPresent, float shift)
        {
            if (shift < 0f) shift = 0f;
            if (industrialPresent && configuredX >= DefaultButtonX - shift)
                return DefaultButtonX - shift;
            return configuredX;
        }

        // explicitX < 0 places the menu just to the right of the loot header.
        // Clamp keeps that panel inside the 1280-wide CUI space.
        public static UiRect MenuRect(float headerTop, float buttonHeight, float headerRight, float gap, float explicitX, float width, float height, bool clamp, float screenLeft, float screenRight, float screenBottom, float screenTop)
        {
            if (width < 40f) width = 40f;
            if (height < 40f) height = 40f;
            float top = headerTop + buttonHeight;
            float left = explicitX >= 0f ? explicitX : headerRight + gap;
            if (clamp)
            {
                if (left + width > screenRight) left = screenRight - width;
                if (left < screenLeft) left = screenLeft;
            }
            float bottom = top - height;
            if (clamp)
            {
                if (bottom < screenBottom)
                {
                    bottom = screenBottom;
                    top = bottom + height;
                }
                if (top > screenTop)
                {
                    top = screenTop;
                    bottom = top - height;
                }
            }
            UiRect rect = new UiRect();
            rect.XMin = left;
            rect.YMin = bottom;
            rect.XMax = left + width;
            rect.YMax = top;
            return rect;
        }

        public static bool PrefabListed(string prefab, string shortName, IList<string> list)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
            {
                string entry = list[i];
                if (string.IsNullOrEmpty(entry)) continue;
                if (!string.IsNullOrEmpty(prefab) && string.Equals(entry, prefab, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrEmpty(shortName) && string.Equals(entry, shortName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static bool Check(out string error)
        {
            float y;
            var yMap = new Dictionary<string, float>();
            yMap["generic"] = 485.5f;
            yMap["genericsmall"] = 175.5f;
            if (!TryHeaderY("generic", 18, BaseY, RowHeight, 8, yMap, out y) || Math.Abs(y - 485.5f) > 0.01f)
            {
                error = "generic header";
                return false;
            }
            if (!TryHeaderY("generic_resizable", 48, BaseY, RowHeight, 8, yMap, out y) || Math.Abs(y - 609.5f) > 0.01f)
            {
                error = "large box header";
                return false;
            }
            if (!TryHeaderY("generic_resizable", 6, BaseY, RowHeight, 8, yMap, out y) || Math.Abs(y - 175.5f) > 0.01f)
            {
                error = "one row header";
                return false;
            }
            if (Math.Abs(DefaultButtonX - 476.5f) > 0.01f)
            {
                error = "button x";
                return false;
            }
            if (Math.Abs(ShiftedButtonX(476.5f, true, 49f) - 427.5f) > 0.01f)
            {
                error = "industrial x";
                return false;
            }
            if (Math.Abs(ShiftedButtonX(400f, true, 49f) - 400f) > 0.01f)
            {
                error = "industrial skip";
                return false;
            }

            SortKey bradley = Key(2, "supply.signal", "Bradley", 10UL, 1, 1);
            SortKey heli = Key(2, "supply.signal", "Heli", 20UL, 5, 0);
            if (Compare(bradley, heli, SortMode.CategorySkin) >= 0)
            {
                error = "skin order";
                return false;
            }
            SortKey bradleyStack = bradley;
            bradleyStack.Amount = 9;
            bradleyStack.Position = 2;
            if (Compare(bradleyStack, bradley, SortMode.CategorySkin) >= 0)
            {
                error = "amount within skin";
                return false;
            }
            SortKey earlier = Key(1, "zzz", "Zed", 0UL, 100, 0);
            if (Compare(earlier, bradley, SortMode.CategorySkin) >= 0)
            {
                error = "category group";
                return false;
            }
            if (Compare(bradley, heli, SortMode.Name) >= 0)
            {
                error = "name order";
                return false;
            }
            if (Compare(heli, bradley, SortMode.Amount) >= 0)
            {
                error = "amount mode";
                return false;
            }
            SortKey byNameA = Key(2, "aaa", "Heli", 1UL, 1, 0);
            SortKey byNameB = Key(2, "zzz", "Bradley", 1UL, 1, 1);
            if (Compare(byNameB, byNameA, SortMode.Category) >= 0)
            {
                error = "category display name";
                return false;
            }
            if (Compare(byNameA, byNameB, SortMode.CategorySkin) >= 0)
            {
                error = "category skin shortname";
                return false;
            }
            if (ClampMode(9) != 0 || ClampMode(2) != 2)
            {
                error = "mode clamp";
                return false;
            }

            ContainerQuery box = ReadyBox();
            if (!CanAct(box) || !ShowSortButtons(box))
            {
                error = "wood box";
                return false;
            }
            box.IsVending = true;
            if (CanAct(box)) { error = "vending"; return false; }
            box = ReadyBox();
            box.IsShop = true;
            if (CanAct(box)) { error = "shop"; return false; }
            box = ReadyBox();
            box.IsOven = true;
            if (CanAct(box)) { error = "oven"; return false; }
            box = ReadyBox();
            box.IsRecycler = true;
            if (CanAct(box)) { error = "recycler"; return false; }
            box = ReadyBox();
            box.IsComposter = true;
            if (CanAct(box)) { error = "composter"; return false; }
            box = ReadyBox();
            box.IsMixing = true;
            if (CanAct(box)) { error = "mixing"; return false; }
            box = ReadyBox();
            box.IsLootCrate = true;
            if (CanAct(box)) { error = "crate"; return false; }
            box.AllowWorldLoot = true;
            if (!CanAct(box)) { error = "crate allow"; return false; }
            box = ReadyBox();
            box.IsStorage = false;
            box.IsCorpse = true;
            if (CanAct(box)) { error = "corpse"; return false; }
            box.AllowCorpse = true;
            if (!CanAct(box)) { error = "corpse allow"; return false; }
            box = ReadyBox();
            box.IsStorage = false;
            box.IsDropped = true;
            if (CanAct(box)) { error = "dropped"; return false; }
            box.AllowDropped = true;
            if (!CanAct(box)) { error = "dropped allow"; return false; }
            box = ReadyBox();
            box.IsLocked = true;
            if (CanAct(box)) { error = "locked"; return false; }
            box = ReadyBox();
            box.InputBlocked = true;
            if (CanAct(box)) { error = "input blocked"; return false; }
            box = ReadyBox();
            box.IsDropBox = true;
            box.PlayerBehind = false;
            if (CanAct(box)) { error = "dropbox"; return false; }
            box.PlayerBehind = true;
            if (!CanAct(box)) { error = "dropbox behind"; return false; }
            box = ReadyBox();
            box.InBlacklist = true;
            if (CanAct(box)) { error = "blacklist"; return false; }
            box = ReadyBox();
            box.HasAllowList = true;
            if (CanAct(box)) { error = "allow list"; return false; }
            box.InAllowList = true;
            if (!CanAct(box)) { error = "allow list hit"; return false; }
            box = ReadyBox();
            box.StillLooting = false;
            if (CanAct(box)) { error = "not looting"; return false; }
            box = ReadyBox();
            box.InRange = false;
            if (CanAct(box)) { error = "range"; return false; }
            box = ReadyBox();
            box.Lootable = false;
            if (CanAct(box)) { error = "not lootable"; return false; }
            box = ReadyBox();
            box.IsPlayerBelt = true;
            if (CanAct(box)) { error = "belt"; return false; }
            box = ReadyBox();
            box.IsPlayerWear = true;
            if (CanAct(box)) { error = "wear"; return false; }
            box = ReadyBox();
            box.IsPlayerMain = true;
            if (CanAct(box)) { error = "main as target"; return false; }
            box = ReadyBox();
            box.IndustrialPowered = true;
            if (!CanAct(box) || ShowSortButtons(box)) { error = "industrial"; return false; }
            box = ReadyBox();
            box.Capacity = 1;
            if (!CanAct(box) || ShowSortButtons(box)) { error = "one slot"; return false; }
            if (!IsProtectedPlayerSlot(true, false) || !IsProtectedPlayerSlot(false, true) || IsProtectedPlayerSlot(false, false))
            {
                error = "reserved slots";
                return false;
            }

            UiRect menu = MenuRect(485.5f, 23f, 572.5f, 8f, -1f, 210f, 240f, true, -632f, 632f, 8f, 712f);
            if (menu.XMax > 632.01f || menu.XMin < -632f || menu.YMin < 7.9f || menu.XMax - menu.XMin < 209f || menu.XMin > 580f)
            {
                error = "menu clamp";
                return false;
            }

            var full = new List<string>();
            full.Add("assets/prefabs/deployable/woodenbox/woodbox_deployed.prefab");
            if (!PrefabListed("assets/prefabs/deployable/woodenbox/woodbox_deployed.prefab", "woodbox_deployed", full))
            {
                error = "prefab path";
                return false;
            }
            var shortList = new List<string>();
            shortList.Add("woodbox_deployed");
            if (!PrefabListed("assets/prefabs/deployable/woodenbox/woodbox_deployed.prefab", "woodbox_deployed", shortList))
            {
                error = "short prefab";
                return false;
            }
            if (PrefabListed("other.prefab", "other", full))
            {
                error = "prefab miss";
                return false;
            }

            var partial = new List<StackUnit>();
            partial.Add(Stack(1, 0UL, "", "", 40, 100, false));
            partial.Add(Stack(1, 0UL, "", "", 40, 100, false));
            partial.Add(Stack(1, 0UL, "", "", 30, 100, false));
            if (!Consolidate(partial) || PositiveStacks(partial) != 2 || AmountAt(partial, 0) != 100 || AmountAt(partial, 1) != 10)
            {
                error = "partial stacks";
                return false;
            }
            var skins = new List<StackUnit>();
            skins.Add(Stack(1, 5UL, "", "", 40, 100, false));
            skins.Add(Stack(1, 7UL, "", "", 25, 100, false));
            skins.Add(Stack(1, 5UL, "", "", 40, 100, false));
            if (!Consolidate(skins) || PositiveStacks(skins) != 2 || AmountAt(skins, 0) != 80 || SkinAt(skins, 0) != 5UL || AmountAt(skins, 1) != 25 || SkinAt(skins, 1) != 7UL)
            {
                error = "skin stacks";
                return false;
            }
            SortKey skinLow = Key(1, "wood", "Wood", SkinAt(skins, 0), AmountAt(skins, 0), 0);
            SortKey skinHigh = Key(1, "wood", "Wood", SkinAt(skins, 1), AmountAt(skins, 1), 1);
            if (Compare(skinLow, skinHigh, SortMode.CategorySkin) >= 0)
            {
                error = "skin sort after merge";
                return false;
            }
            var names = new List<StackUnit>();
            names.Add(Stack(1, 0UL, "Gift", "", 10, 100, false));
            names.Add(Stack(1, 0UL, "", "", 10, 100, false));
            names.Add(Stack(1, 0UL, "Gift", "", 10, 100, false));
            if (!Consolidate(names) || PositiveStacks(names) != 2 || AmountAt(names, 0) != 20 || AmountAt(names, 1) != 10)
            {
                error = "name stacks";
                return false;
            }
            var notes = new List<StackUnit>();
            notes.Add(Stack(2, 0UL, "", "alpha", 4, 10, false));
            notes.Add(Stack(2, 0UL, "", "beta", 4, 10, false));
            if (!Consolidate(notes) || PositiveStacks(notes) != 2)
            {
                error = "text stacks";
                return false;
            }
            var blocked = new List<StackUnit>();
            blocked.Add(Stack(3, 0UL, "", "", 5, 20, true));
            blocked.Add(Stack(3, 0UL, "", "", 5, 20, false));
            if (!Consolidate(blocked) || PositiveStacks(blocked) != 2)
            {
                error = "blocked stacks";
                return false;
            }
            var single = new List<StackUnit>();
            single.Add(Stack(4, 0UL, "", "", 1, 1, false));
            single.Add(Stack(4, 0UL, "", "", 1, 1, false));
            if (!Consolidate(single) || PositiveStacks(single) != 2)
            {
                error = "max one";
                return false;
            }
            float packMin;
            float packMax;
            ShiftPackButtonY(175.5f, 197.5f, 6, BaseY, RowHeight, 8, out packMin, out packMax);
            if (Math.Abs(packMin - 175.5f) > 0.01f || Math.Abs(packMax - 197.5f) > 0.01f)
            {
                error = "pack button one row";
                return false;
            }
            ShiftPackButtonY(175.5f, 197.5f, 48, BaseY, RowHeight, 8, out packMin, out packMax);
            if (Math.Abs(packMin - 609.5f) > 0.01f || Math.Abs(packMax - 631.5f) > 0.01f)
            {
                error = "pack button eight rows";
                return false;
            }

            error = "";
            return true;
        }

        private static StackUnit Stack(int itemId, ulong skin, string name, string text, int amount, int maxStack, bool blocked)
        {
            StackUnit unit = new StackUnit();
            unit.ItemId = itemId;
            unit.Skin = skin;
            unit.Name = name;
            unit.Text = text;
            unit.Amount = amount;
            unit.MaxStack = maxStack;
            unit.Blocked = blocked;
            return unit;
        }

        private static int AmountAt(List<StackUnit> items, int nth)
        {
            int seen = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Amount <= 0) continue;
                if (seen == nth) return items[i].Amount;
                seen++;
            }
            return -1;
        }

        private static ulong SkinAt(List<StackUnit> items, int nth)
        {
            int seen = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Amount <= 0) continue;
                if (seen == nth) return items[i].Skin;
                seen++;
            }
            return 0UL;
        }

        private static SortKey Key(int category, string shortname, string display, ulong skin, int amount, int position)
        {
            SortKey key = new SortKey();
            key.Category = category;
            key.Shortname = shortname;
            key.DisplayName = display;
            key.Skin = skin;
            key.Amount = amount;
            key.Position = position;
            return key;
        }

        private static ContainerQuery ReadyBox()
        {
            ContainerQuery query = new ContainerQuery();
            query.IsStorage = true;
            query.Capacity = 18;
            query.StillLooting = true;
            query.InRange = true;
            query.Lootable = true;
            query.PlayerBehind = true;
            return query;
        }
    }

#if !NIGHTSHADE_SORT_TESTS
    [Info("NightshadeSort", "Nightshade", "1.0.1")]
    [Description("Sort menu and Transfer All for storage containers.")]
    public class NightshadeSort : CarbonPlugin
    {
        private const string PermUse = "nightshadesort.use";
        private const string PermTransfer = "nightshadesort.transfer";
        private const string DataPath = "Nightshade/NightshadeSort/players";
        private const string UiCommand = "nightshadesort.ui";
        private const string UiHeader = "NightshadeSort.Header";
        private const string UiMenu = "NightshadeSort.Menu";
        private const string UiTransferMain = "NightshadeSort.TransferMain";
        private const string UiTransferPack = "NightshadeSort.TransferPack";
        private const string FontRegular = "robotocondensed-regular.ttf";
        private const string FontBold = "robotocondensed-bold.ttf";
        private const int CupboardSlots = 24;

        private PluginConfig _config;
        private PrefStore _store;
        private readonly Dictionary<ulong, Draft> _draft = new Dictionary<ulong, Draft>();
        private readonly HashSet<ulong> _busy = new HashSet<ulong>();
        private readonly Dictionary<ulong, ItemContainer> _openPack = new Dictionary<ulong, ItemContainer>();

        private class PluginConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Allow world loot crates")] public bool AllowWorldLoot = false;
            [JsonProperty("Allow corpse and NPC loot")] public bool AllowCorpse = false;
            [JsonProperty("Allow dropped containers")] public bool AllowDropped = false;
            [JsonProperty("Loot distance")] public float LootDistance = 8f;
            [JsonProperty("Chat feedback for Transfer All")] public bool ChatFeedback = true;
            [JsonProperty("Show backpack transfer when a backpack is equipped")] public bool ShowBackpack = true;
            [JsonProperty("Only these prefabs (empty allows every safe storage container)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> EnabledPrefabs = new List<string>();
            [JsonProperty("Blacklist prefabs", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Blacklist = new List<string>();

            [JsonProperty("UI parent layer")] public string Parent = "Overlay";
            [JsonProperty("Button anchor")] public string ButtonAnchor = "0.5 0";
            [JsonProperty("Button offset X")] public float ButtonX = NightshadeSortRules.DefaultButtonX;
            [JsonProperty("Industrial adaptor X shift")] public float IndustrialShift = 49f;
            [JsonProperty("Cog width")] public float CogWidth = NightshadeSortRules.CogWidth;
            [JsonProperty("Sort width")] public float SortWidth = NightshadeSortRules.SortWidth;
            [JsonProperty("Button height")] public float ButtonHeight = 23f;
            [JsonProperty("Base Y offset")] public float BaseY = NightshadeSortRules.BaseY;
            [JsonProperty("Y per row")] public float RowHeight = NightshadeSortRules.RowHeight;
            [JsonProperty("Max rows")] public int MaxRows = 8;
            [JsonProperty("Y offset by loot panel", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, float> PanelY = DefaultPanelY();
            [JsonProperty("Height by loot panel", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, float> PanelHeight = DefaultPanelHeight();
            [JsonProperty("Cog color")] public string CogColor = "0.75 0.43 0.18 0.8";
            [JsonProperty("Gear icon")] public string GearSprite = "assets/icons/gear.png";
            [JsonProperty("Gear icon color")] public string GearColor = "0.95 0.90 0.82 0.95";
            [JsonProperty("Sort color")] public string SortColor = "0.41 0.50 0.25 0.8";
            [JsonProperty("Sort text color")] public string SortTextColor = "0.77 0.92 0.67 0.8";

            [JsonProperty("Menu offset X (-1 places it to the right of the loot header)")] public float MenuX = -1f;
            [JsonProperty("Menu gap to the right of the header")] public float MenuGap = 8f;
            [JsonProperty("Menu width")] public float MenuWidth = 210f;
            [JsonProperty("Menu height")] public float MenuHeight = 240f;
            [JsonProperty("Clamp menu to the screen")] public bool ClampMenu = true;
            [JsonProperty("Screen left limit")] public float ScreenLeft = -632f;
            [JsonProperty("Screen right limit")] public float ScreenRight = 632f;
            [JsonProperty("Screen bottom limit")] public float ScreenBottom = 8f;
            [JsonProperty("Screen top limit")] public float ScreenTop = 712f;
            [JsonProperty("Menu background")] public string MenuBg = "0.11 0.11 0.11 0.96";
            [JsonProperty("Menu title bar")] public string MenuTitleBg = "0.18 0.16 0.14 0.98";
            [JsonProperty("Menu text")] public string MenuText = "0.86 0.84 0.80 1";
            [JsonProperty("Menu selected color")] public string MenuSelected = "0.32 0.24 0.16 0.98";
            [JsonProperty("Sort now color")] public string ApplyColor = "0.41 0.50 0.25 0.95";
            [JsonProperty("Cancel color")] public string CancelColor = "0.28 0.28 0.28 0.95";
            [JsonProperty("Close color")] public string CloseColor = "0.65 0.18 0.14 0.95";

            [JsonProperty("Main transfer anchor min")] public string MainAnchorMin = "0.5 0";
            [JsonProperty("Main transfer anchor max")] public string MainAnchorMax = "0.5 0";
            [JsonProperty("Main transfer offset min")] public string MainOffsetMin = "48 341";
            [JsonProperty("Main transfer offset max")] public string MainOffsetMax = "180 363";
            [JsonProperty("Backpack transfer anchor min")] public string PackAnchorMin = "0.5 0";
            [JsonProperty("Backpack transfer anchor max")] public string PackAnchorMax = "0.5 0";
            // 1-row backpack header (Y = Base Y + one row). Taller pages add Y per row.
            // Sits on the left of that header, short of the sort buttons at X 476.5.
            [JsonProperty("Backpack transfer offset min")] public string PackOffsetMin = "330 175.5";
            [JsonProperty("Backpack transfer offset max")] public string PackOffsetMax = "462 197.5";
            [JsonProperty("Transfer color")] public string TransferColor = "0.41 0.50 0.25 0.9";
            [JsonProperty("Transfer text color")] public string TransferText = "0.77 0.92 0.67 1";
        }

        private class PrefStore
        {
            public Dictionary<string, PlayerPref> Players = new Dictionary<string, PlayerPref>();
        }

        private class PlayerPref
        {
            public int Mode;
            public bool Merge = true;
            public bool Remember = true;
        }

        private class Draft
        {
            public int Mode;
            public bool Merge = true;
            public bool Remember = true;
            public bool MenuOpen;
        }

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermTransfer, this);
            LoadData();
            cmd.AddConsoleCommand(UiCommand, this, nameof(CmdUi));
            string error;
            if (!NightshadeSortRules.Check(out error))
                PrintError("NightshadeSort rules check failed: " + error);
        }

        private void Unload()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
                DestroyUi(player);
            _draft.Clear();
            _busy.Clear();
            _openPack.Clear();
        }

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null || _config == null || !_config.Enabled) return;
            NextTick(() =>
            {
                if (player == null || !player.IsConnected || entity == null || entity.IsDestroyed) return;
                Draw(player, entity);
            });
        }

        private void OnPlayerLootEnd(PlayerLoot inventory)
        {
            if (inventory == null) return;
            EndLoot(inventory.baseEntity);
        }

        private void OnLootEntityEnd(BasePlayer player, BaseCombatEntity entity)
        {
            EndLoot(player);
        }

        // Backpacks plugin. Fired when that player's own backpack loot panel opens.
        private void OnBackpackOpened(BasePlayer player, ulong ownerId, ItemContainer container)
        {
            if (player == null || container == null) return;
            if (player.userID != ownerId) return;
            _openPack[player.userID] = container;
            NextTick(() => ShowPackButton(player));
        }

        // Backpacks plugin. Hide the transfer button as soon as the panel closes
        // so it does not stay on the screen after the backpack UI is gone.
        private void OnBackpackClosed(BasePlayer player, ulong ownerId, ItemContainer container)
        {
            if (player == null) return;
            if (ownerId != 0UL && player.userID != ownerId) return;
            _openPack.Remove(player.userID);
            CuiHelper.DestroyUi(player, UiTransferPack);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            EndLoot(player);
        }

        // Chat args are string[]. ConsoleSystem.Arg.Args is Facepunch.StringView[]
        // on this host, so this entry point reads tokens with GetString.
        private void CmdUi(ConsoleSystem.Arg arg)
        {
            if (arg == null) return;
            BasePlayer player = arg.Player();
            if (player == null) return;
            string action = arg.GetString(0);
            if (string.IsNullOrEmpty(action)) return;
            if (action == "cog") { ToggleMenu(player); return; }
            if (action == "sort") { DirectSort(player); return; }
            if (action == "close" || action == "cancel") { CloseMenu(player); return; }
            if (action == "apply") { ApplyMenu(player); return; }
            if (action == "mode") { SetMode(player, arg.GetString(1)); return; }
            if (action == "merge") { ToggleDraft(player, true); return; }
            if (action == "remember") { ToggleDraft(player, false); return; }
            if (action == "transfer") { DoTransfer(player, arg.GetString(1) == "backpack"); return; }
        }

        private void EndLoot(BasePlayer player)
        {
            if (player == null) return;
            DestroyUi(player);
            _draft.Remove(player.userID);
            _busy.Remove(player.userID);
            _openPack.Remove(player.userID);
        }

        private bool HasPerm(BasePlayer player, string perm)
        {
            return player != null && permission.UserHasPermission(player.UserIDString, perm);
        }

        private bool Active()
        {
            return _config != null && _config.Enabled;
        }

        private void ToggleMenu(BasePlayer player)
        {
            if (!Active()) return;
            if (!HasPerm(player, PermUse))
            {
                Tell(player, "NoPermission");
                return;
            }
            BaseEntity entity;
            ItemContainer container;
            NightshadeSortRules.ContainerQuery query;
            if (!BuildContext(player, out entity, out container, out query)) return;
            if (!NightshadeSortRules.ShowSortButtons(query)) return;
            Draft draft = DraftOf(player.userID);
            draft.MenuOpen = !draft.MenuOpen;
            if (draft.MenuOpen)
            {
                PlayerPref pref = ReadPref(player.userID);
                draft.Mode = pref.Mode;
                draft.Merge = pref.Merge;
                draft.Remember = pref.Remember;
            }
            Draw(player, entity);
        }

        private void CloseMenu(BasePlayer player)
        {
            Draft draft;
            if (_draft.TryGetValue(player.userID, out draft) && draft != null)
                draft.MenuOpen = false;
            CuiHelper.DestroyUi(player, UiMenu);
        }

        private void SetMode(BasePlayer player, string value)
        {
            if (!Active() || !HasPerm(player, PermUse)) return;
            Draft draft = DraftOf(player.userID);
            if (!draft.MenuOpen) return;
            int mode;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out mode)) return;
            draft.Mode = NightshadeSortRules.ClampMode(mode);
            Redraw(player);
        }

        private void ToggleDraft(BasePlayer player, bool mergeFlag)
        {
            if (!Active() || !HasPerm(player, PermUse)) return;
            Draft draft = DraftOf(player.userID);
            if (!draft.MenuOpen) return;
            if (mergeFlag) draft.Merge = !draft.Merge;
            else draft.Remember = !draft.Remember;
            Redraw(player);
        }

        private void ApplyMenu(BasePlayer player)
        {
            if (!Active()) return;
            if (!HasPerm(player, PermUse))
            {
                Tell(player, "NoPermission");
                return;
            }
            Draft draft = DraftOf(player.userID);
            int mode = draft.Mode;
            bool merge = draft.Merge;
            Commit(player.userID, mode, merge, draft.Remember, true);
            draft.MenuOpen = false;
            SortOpen(player, mode, merge);
            CuiHelper.DestroyUi(player, UiMenu);
        }

        private void DirectSort(BasePlayer player)
        {
            if (!Active()) return;
            if (!HasPerm(player, PermUse))
            {
                Tell(player, "NoPermission");
                return;
            }
            Draft draft;
            if (_draft.TryGetValue(player.userID, out draft) && draft != null && draft.MenuOpen)
            {
                draft.MenuOpen = false;
                CuiHelper.DestroyUi(player, UiMenu);
            }
            PlayerPref pref = ReadPref(player.userID);
            SortOpen(player, pref.Mode, pref.Merge);
        }

        private void SortOpen(BasePlayer player, int mode, bool merge)
        {
            if (!Active() || player == null || !Enter(player.userID)) return;
            try
            {
                BaseEntity entity;
                ItemContainer ignored;
                NightshadeSortRules.ContainerQuery query;
                if (!BuildContext(player, out entity, out ignored, out query)) return;
                if (!NightshadeSortRules.CanAct(query) || query.IndustrialPowered) return;
                var targets = new List<ItemContainer>();
                CollectTargets(player, entity, targets);
                NightshadeSortRules.SortMode sortMode = (NightshadeSortRules.SortMode)NightshadeSortRules.ClampMode(mode);
                for (int i = 0; i < targets.Count; i++)
                {
                    ItemContainer target = targets[i];
                    if (target == null || target.capacity <= 1) continue;
                    if (target.IsLocked() || target.PlayerItemInputBlocked()) continue;
                    SortContainer(target, player, entity, sortMode, merge);
                }
            }
            finally
            {
                Leave(player.userID);
            }
        }

        private void DoTransfer(BasePlayer player, bool backpack)
        {
            if (!Active()) return;
            if (!HasPerm(player, PermTransfer))
            {
                Tell(player, "NoPermission");
                return;
            }
            if (player == null || !Enter(player.userID)) return;
            try
            {
                BaseEntity entity;
                ItemContainer primary;
                NightshadeSortRules.ContainerQuery query;
                if (!BuildContext(player, out entity, out primary, out query)) return;
                if (backpack && !_config.ShowBackpack) return;
                if (backpack && !query.InRange)
                {
                    int ignored;
                    if (PackOpen(player, out ignored)) query.InRange = true;
                }
                if (!NightshadeSortRules.CanAct(query)) return;
                ItemContainer source = backpack ? OpenBackpackContents(player) : player.inventory.containerMain;
                if (source == null || source.itemList == null) return;
                if (NightshadeSortRules.IsProtectedPlayerSlot(source == player.inventory.containerBelt, source == player.inventory.containerWear))
                    return;
                if (source == player.inventory.containerBelt || source == player.inventory.containerWear)
                    return;
                int started = source.itemList.Count;
                if (started == 0) return;
                var targets = new List<ItemContainer>();
                CollectTargets(player, entity, targets);
                for (int i = targets.Count - 1; i >= 0; i--)
                {
                    if (targets[i] == source) targets.RemoveAt(i);
                }
                if (targets.Count == 0 && player.inventory.containerMain != null && player.inventory.containerMain != source)
                    targets.Add(player.inventory.containerMain);
                int moved = MoveInto(player, source, targets);
                int left = source.itemList == null ? 0 : source.itemList.Count;
                if (!_config.ChatFeedback) return;
                if (moved == 0) Tell(player, "NoneMoved");
                else Tell(player, "Moved", moved.ToString(CultureInfo.InvariantCulture), left.ToString(CultureInfo.InvariantCulture));
            }
            finally
            {
                Leave(player.userID);
            }
        }

        private bool Enter(ulong id)
        {
            if (_busy.Contains(id)) return false;
            _busy.Add(id);
            return true;
        }

        private void Leave(ulong id)
        {
            _busy.Remove(id);
        }

        private bool BuildContext(BasePlayer player, out BaseEntity entity, out ItemContainer container, out NightshadeSortRules.ContainerQuery query)
        {
            entity = null;
            container = null;
            query = new NightshadeSortRules.ContainerQuery();
            if (player == null || player.IsDead() || player.inventory == null || player.inventory.loot == null)
                return false;
            entity = player.inventory.loot.entitySource;
            if (entity == null || entity.IsDestroyed) return false;
            container = PrimaryContainer(player, entity);
            if (container == null) return false;

            StorageContainer storage = entity as StorageContainer;
            query.IsStorage = storage != null;
            query.IsVending = entity is VendingMachine;
            query.IsShop = entity is ShopFront;
            query.IsOven = entity is BaseOven;
            query.IsRecycler = entity is Recycler;
            query.IsComposter = entity is Composter;
            query.IsMixing = entity is MixingTable;
            query.IsLootCrate = entity is LootContainer;
            query.IsCorpse = entity is LootableCorpse || entity is BaseCorpse;
            query.IsDropped = entity is DroppedItemContainer;
            query.IsDropBox = entity is DropBox;
            query.PlayerBehind = true;
            DropBox dropBox = entity as DropBox;
            if (dropBox != null)
                query.PlayerBehind = dropBox.PlayerBehind(player);
            query.Capacity = container.capacity;
            query.IsLocked = container.IsLocked()
                || container.HasFlag(ItemContainer.Flag.IsLocked)
                || container.HasFlag(ItemContainer.Flag.NoItemInput);
            query.InputBlocked = container.PlayerItemInputBlocked();
            query.StillLooting = player.inventory.loot.entitySource == entity;
            query.InRange = Within(player, entity);
            query.Lootable = storage == null || (storage.isLootable && storage.CanBeLooted(player));
            query.IsPlayerMain = container == player.inventory.containerMain;
            query.IsPlayerBelt = container == player.inventory.containerBelt;
            query.IsPlayerWear = container == player.inventory.containerWear;
            query.AllowCorpse = _config.AllowCorpse;
            query.AllowDropped = _config.AllowDropped;
            query.AllowWorldLoot = _config.AllowWorldLoot;
            string prefab = Text(entity.PrefabName);
            string shortName = Text(entity.ShortPrefabName);
            query.HasAllowList = _config.EnabledPrefabs != null && _config.EnabledPrefabs.Count > 0;
            query.InAllowList = NightshadeSortRules.PrefabListed(prefab, shortName, _config.EnabledPrefabs);
            query.InBlacklist = NightshadeSortRules.PrefabListed(prefab, shortName, _config.Blacklist);
            bool present;
            bool powered;
            IndustrialState(entity, out present, out powered);
            query.IndustrialPowered = powered;
            return true;
        }

        private bool Within(BasePlayer player, BaseEntity entity)
        {
            float max = _config.LootDistance;
            if (max <= 0f) return true;
            Vector3 delta = player.transform.position - entity.transform.position;
            return delta.sqrMagnitude <= max * max;
        }

        private ItemContainer PrimaryContainer(BasePlayer player, BaseEntity entity)
        {
            PlayerLoot loot = player.inventory.loot;
            if (loot == null || loot.containers == null) return null;
            ItemContainer fallback = null;
            for (int i = 0; i < loot.containers.Count; i++)
            {
                ItemContainer candidate = loot.containers[i];
                if (!UsableTarget(player, entity, candidate)) continue;
                if (candidate.entityOwner == entity) return candidate;
                if (fallback == null) fallback = candidate;
            }
            return fallback;
        }

        private void CollectTargets(BasePlayer player, BaseEntity entity, List<ItemContainer> into)
        {
            PlayerLoot loot = player.inventory.loot;
            if (loot == null || loot.containers == null) return;
            for (int i = 0; i < loot.containers.Count; i++)
            {
                ItemContainer candidate = loot.containers[i];
                if (!UsableTarget(player, entity, candidate)) continue;
                if (candidate.entityOwner != entity) continue;
                into.Add(candidate);
            }
            if (into.Count > 0) return;
            ItemContainer primary = PrimaryContainer(player, entity);
            if (primary != null) into.Add(primary);
        }

        private static bool UsableTarget(BasePlayer player, BaseEntity entity, ItemContainer candidate)
        {
            if (candidate == null || player == null || player.inventory == null) return false;
            if (candidate == player.inventory.containerMain) return false;
            if (candidate == player.inventory.containerBelt) return false;
            if (candidate == player.inventory.containerWear) return false;
            if (candidate.entityOwner != null && entity != null && candidate.entityOwner != entity) return false;
            return true;
        }

        private static void IndustrialState(BaseEntity entity, out bool present, out bool powered)
        {
            present = false;
            powered = false;
            StorageContainer storage = entity as StorageContainer;
            if (storage == null || !storage.allowSorting || storage.children == null) return;
            for (int i = 0; i < storage.children.Count; i++)
            {
                IndustrialStorageAdaptor adaptor = storage.children[i] as IndustrialStorageAdaptor;
                if (adaptor == null) continue;
                present = true;
                if (adaptor.IsPowered()) powered = true;
            }
        }

        private static int SlotLimit(BaseEntity entity, ItemContainer container)
        {
            int limit = container.capacity;
            if (limit < 0) limit = 0;
            if (entity is BuildingPrivlidge && limit > CupboardSlots) limit = CupboardSlots;
            return limit;
        }

        private void SortContainer(ItemContainer container, BasePlayer player, BaseEntity entity, NightshadeSortRules.SortMode mode, bool merge)
        {
            if (container == null || container.itemList == null) return;
            int slotLimit = SlotLimit(entity, container);
            if (slotLimit <= 1) return;
            var taken = new List<Item>();
            try
            {
                if (merge && !ConsolidateContainer(container, player, slotLimit))
                    PrintError("NightshadeSort merge changed the total item amount. Sorting the items that are still in the container.");
                for (int i = container.itemList.Count - 1; i >= 0; i--)
                {
                    Item item = container.itemList[i];
                    if (item == null || item.position >= slotLimit || item.position < 0) continue;
                    if (item.IsLocked()) continue;
                    int position = item.position;
                    item.RemoveFromContainer();
                    if (item.parent != null) continue;
                    // Removal clears the slot index. Keep it so equal items stay in their old order.
                    item.position = position;
                    taken.Add(item);
                }
                taken.Sort(delegate (Item a, Item b) { return CompareItems(a, b, mode); });
                for (int i = 0; i < taken.Count; i++)
                {
                    Item item = taken[i];
                    if (item == null || item.amount <= 0 || item.parent != null) continue;
                    bool placed = false;
                    for (int slot = 0; slot < slotLimit; slot++)
                    {
                        if (!item.MoveToContainer(container, slot, false, false, player, false)) continue;
                        placed = true;
                        break;
                    }
                    if (!placed)
                        item.MoveToContainer(container, -1, true, false, player, false);
                }
                container.MarkDirty();
            }
            catch (Exception ex)
            {
                PrintError("NightshadeSort could not finish sorting: " + ex.Message);
            }
            finally
            {
                for (int i = 0; i < taken.Count; i++)
                    ReturnItem(player, taken[i]);
            }
        }

        private static int CompareItems(Item a, Item b, NightshadeSortRules.SortMode mode)
        {
            return NightshadeSortRules.Compare(KeyOf(a), KeyOf(b), mode);
        }

        private static NightshadeSortRules.SortKey KeyOf(Item item)
        {
            NightshadeSortRules.SortKey key = new NightshadeSortRules.SortKey();
            if (item == null) return key;
            key.Amount = item.amount;
            key.Position = item.position;
            key.Skin = item.skin;
            if (item.info == null) return key;
            key.Category = (int)item.info.category;
            key.Shortname = Text(item.info.shortname);
            string display = "";
            if (item.info.displayName != null)
            {
                display = item.info.displayName.translated;
                if (string.IsNullOrEmpty(display)) display = item.info.displayName.english;
            }
            if (string.IsNullOrEmpty(display)) display = key.Shortname;
            key.DisplayName = display;
            return key;
        }

        // Combine partial stacks while they are still in the container so
        // MaxStackable, CanStack, and MoveToContainer see the real parent.
        private bool ConsolidateContainer(ItemContainer container, BasePlayer player, int slotLimit)
        {
            if (container == null || container.itemList == null) return true;
            int before = SumContainer(container);
            int guard = 0;
            bool progressed = true;
            while (progressed && guard < 12)
            {
                guard++;
                progressed = false;
                var items = new List<Item>(container.itemList);
                for (int i = 0; i < items.Count; i++)
                {
                    Item dest = items[i];
                    if (!InSlot(dest, container, slotLimit)) continue;
                    int max = StackCeiling(dest);
                    if (max <= 1 || dest.amount >= max) continue;
                    for (int j = i + 1; j < items.Count; j++)
                    {
                        if (!InSlot(dest, container, slotLimit)) break;
                        max = StackCeiling(dest);
                        if (dest.amount >= max) break;
                        Item src = items[j];
                        if (!InSlot(src, container, slotLimit) || !CanMergePair(dest, src)) continue;
                        int destBefore = dest.amount;
                        int srcBefore = src.amount;
                        src.MoveToContainer(container, dest.position, true, false, player, false);
                        bool changed = dest.amount != destBefore || src.amount != srcBefore || src.parent != container;
                        if (!changed)
                            changed = MergeAmounts(dest, src);
                        if (!changed) continue;
                        progressed = true;
                        if (src.amount <= 0 && src.parent == container)
                        {
                            src.amount = 0;
                            src.RemoveFromContainer();
                            src.Remove(0f);
                        }
                    }
                }
            }
            return SumContainer(container) == before;
        }

        private static bool InSlot(Item item, ItemContainer container, int slotLimit)
        {
            if (item == null || item.amount <= 0 || item.parent != container) return false;
            if (item.position < 0 || item.position >= slotLimit) return false;
            return true;
        }

        private static bool CanMergePair(Item dest, Item src)
        {
            if (dest == null || src == null || dest == src) return false;
            if (dest.info == null || src.info == null || dest.IsLocked() || src.IsLocked()) return false;
            if (!NightshadeSortRules.SameStack(Describe(dest), Describe(src))) return false;
            if (!dest.CanStack(src) || !src.CanStack(dest)) return false;
            return true;
        }

        private static NightshadeSortRules.StackUnit Describe(Item item)
        {
            NightshadeSortRules.StackUnit unit = new NightshadeSortRules.StackUnit();
            if (item == null || item.info == null)
            {
                unit.Blocked = true;
                return unit;
            }
            unit.ItemId = item.info.itemid;
            unit.Skin = item.skin;
            unit.Name = item.name ?? "";
            unit.Text = item.text ?? "";
            unit.Amount = item.amount;
            unit.MaxStack = StackCeiling(item);
            unit.Blocked = HasContents(item) || HasLoadedAmmo(item);
            return unit;
        }

        private static bool HasContents(Item item)
        {
            return item.contents != null && item.contents.itemList != null && item.contents.itemList.Count > 0;
        }

        private static bool HasLoadedAmmo(Item item)
        {
            BaseEntity held = item.GetHeldEntity();
            if (held == null) return false;
            BaseProjectile projectile = held as BaseProjectile;
            if (projectile != null && projectile.primaryMagazine != null && projectile.primaryMagazine.contents > 0) return true;
            FlameThrower flame = held as FlameThrower;
            if (flame != null && flame.ammo > 0) return true;
            Chainsaw saw = held as Chainsaw;
            if (saw != null && saw.ammo > 0) return true;
            return false;
        }

        private static int StackCeiling(Item item)
        {
            int max = item.MaxStackable();
            if (max <= 0 && item.info != null) max = item.info.stackable;
            return max;
        }

        private static bool MergeAmounts(Item dest, Item src)
        {
            if (dest == null || src == null || dest.parent == null) return false;
            if (!dest.CanStack(src) || !src.CanStack(dest)) return false;
            int max = StackCeiling(dest);
            int room = max - dest.amount;
            if (room <= 0 || src.amount <= 0) return false;
            int move = src.amount < room ? src.amount : room;
            if (move <= 0) return false;
            int sum = dest.amount + src.amount;
            dest.amount += move;
            src.amount -= move;
            if (dest.amount + src.amount != sum)
            {
                dest.amount -= move;
                src.amount += move;
                return false;
            }
            dest.MarkDirty();
            Interface.CallHook("OnItemStacked", dest, src, move);
            if (src.amount <= 0)
            {
                src.amount = 0;
                if (src.parent != null) src.RemoveFromContainer();
                src.Remove(0f);
            }
            else
            {
                src.MarkDirty();
            }
            return true;
        }

        private static int SumContainer(ItemContainer container)
        {
            int sum = 0;
            if (container == null || container.itemList == null) return 0;
            for (int i = 0; i < container.itemList.Count; i++)
            {
                Item item = container.itemList[i];
                if (item != null && item.amount > 0) sum += item.amount;
            }
            return sum;
        }

        private static void ReturnItem(BasePlayer player, Item item)
        {
            if (item == null || item.amount <= 0 || item.parent != null) return;
            if (player != null && player.inventory != null && player.inventory.containerMain != null
                && item.MoveToContainer(player.inventory.containerMain, -1, true, false, player, false))
                return;
            // BasePlayer.GiveItem returns void on this host. PlayerInventory.GiveItem returns bool.
            if (player != null && player.inventory != null && player.inventory.GiveItem(item)) return;
            Vector3 pos = player != null ? player.transform.position + Vector3.up : Vector3.zero;
            item.Drop(pos, Vector3.zero);
        }

        private int MoveInto(BasePlayer player, ItemContainer source, List<ItemContainer> targets)
        {
            if (source == null || source.itemList == null || targets == null) return 0;
            int moved = 0;
            var items = new List<Item>(source.itemList);
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                if (item == null || item.parent != source || item.amount <= 0 || item.IsLocked()) continue;
                int before = item.amount;
                for (int t = 0; t < targets.Count; t++)
                {
                    ItemContainer dest = targets[t];
                    if (dest == null || dest == source) continue;
                    if (dest.IsLocked() || dest.PlayerItemInputBlocked()) continue;
                    item.MoveToContainer(dest, -1, true, false, player, false);
                    if (item.parent != source || item.amount <= 0) break;
                }
                if (item.parent != source || item.amount < before) moved++;
            }
            source.MarkDirty();
            for (int t = 0; t < targets.Count; t++)
            {
                if (targets[t] != null) targets[t].MarkDirty();
            }
            return moved;
        }

        private void Redraw(BasePlayer player)
        {
            BaseEntity entity;
            ItemContainer container;
            NightshadeSortRules.ContainerQuery query;
            if (!BuildContext(player, out entity, out container, out query))
            {
                DestroyUi(player);
                return;
            }
            Draw(player, entity);
        }

        private void Draw(BasePlayer player, BaseEntity entity)
        {
            DestroyUi(player);
            try
            {
                if (player == null || entity == null || _config == null || !_config.Enabled) return;
                ItemContainer container;
                NightshadeSortRules.ContainerQuery query;
                BaseEntity open;
                if (!BuildContext(player, out open, out container, out query) || open != entity) return;
                if (!NightshadeSortRules.CanAct(query)) return;
                bool use = HasPerm(player, PermUse);
                bool transfer = HasPerm(player, PermTransfer);
                if (!use && !transfer) return;
                if (use && NightshadeSortRules.ShowSortButtons(query))
                {
                    float x;
                    float y;
                    float height;
                    HeaderPlacement(entity, container, out x, out y, out height);
                    DrawHeader(player, x, y, height);
                    Draft draft;
                    if (_draft.TryGetValue(player.userID, out draft) && draft != null && draft.MenuOpen)
                        DrawMenu(player, x, y, height, draft);
                }
                if (transfer)
                    DrawTransfer(player);
            }
            finally
            {
                ShowPackButton(player);
            }
        }

        private void HeaderPlacement(BaseEntity entity, ItemContainer container, out float x, out float y, out float height)
        {
            string panel = PanelName(entity);
            NightshadeSortRules.TryHeaderY(panel, container.capacity, _config.BaseY, _config.RowHeight, _config.MaxRows, _config.PanelY, out y);
            height = NightshadeSortRules.HeaderHeight(panel, _config.ButtonHeight, _config.PanelHeight);
            bool present;
            bool powered;
            IndustrialState(entity, out present, out powered);
            x = NightshadeSortRules.ShiftedButtonX(_config.ButtonX, present && !powered, _config.IndustrialShift);
        }

        private void DrawHeader(BasePlayer player, float x, float y, float height)
        {
            var ui = new CuiElementContainer();
            string anchor = string.IsNullOrEmpty(_config.ButtonAnchor) ? "0.5 0" : _config.ButtonAnchor;
            ui.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = anchor, AnchorMax = anchor, OffsetMin = F(x) + " " + F(y), OffsetMax = F(x) + " " + F(y) },
                CursorEnabled = false
            }, Parent(), UiHeader);

            string cog = ui.Add(new CuiPanel
            {
                Image = { Color = ColorOr(_config.CogColor, "0.75 0.43 0.18 0.8") },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "0 0", OffsetMax = F(_config.CogWidth) + " " + F(height) }
            }, UiHeader);
            ui.Add(new CuiElement
            {
                Parent = cog,
                Components =
                {
                    new CuiImageComponent { Sprite = string.IsNullOrEmpty(_config.GearSprite) ? "assets/icons/gear.png" : _config.GearSprite, Color = ColorOr(_config.GearColor, "1 1 1 0.95") },
                    new CuiRectTransformComponent { AnchorMin = "0.16 0.16", AnchorMax = "0.84 0.84" }
                }
            });
            ui.Add(new CuiButton
            {
                Button = { Color = "0 0 0 0", Command = UiCommand + " cog" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                Text = { Text = "" }
            }, cog);

            ui.Add(new CuiButton
            {
                Button = { Color = ColorOr(_config.SortColor, "0.41 0.50 0.25 0.8"), Command = UiCommand + " sort" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = F(_config.CogWidth) + " 0", OffsetMax = F(_config.CogWidth + _config.SortWidth) + " " + F(height) },
                Text = { Text = Lang(player, "Sort"), FontSize = 12, Align = TextAnchor.MiddleCenter, Color = ColorOr(_config.SortTextColor, "0.77 0.92 0.67 0.8"), Font = FontBold }
            }, UiHeader);
            CuiHelper.AddUi(player, ui);
        }

        private void DrawMenu(BasePlayer player, float x, float y, float height, Draft draft)
        {
            float headerRight = x + _config.CogWidth + _config.SortWidth;
            NightshadeSortRules.UiRect rect = NightshadeSortRules.MenuRect(
                y, height, headerRight, _config.MenuGap, _config.MenuX, _config.MenuWidth, _config.MenuHeight,
                _config.ClampMenu, _config.ScreenLeft, _config.ScreenRight, _config.ScreenBottom, _config.ScreenTop);
            var ui = new CuiElementContainer();
            string anchor = string.IsNullOrEmpty(_config.ButtonAnchor) ? "0.5 0" : _config.ButtonAnchor;
            ui.Add(new CuiPanel
            {
                Image = { Color = ColorOr(_config.MenuBg, "0.11 0.11 0.11 0.96") },
                RectTransform = { AnchorMin = anchor, AnchorMax = anchor, OffsetMin = F(rect.XMin) + " " + F(rect.YMin), OffsetMax = F(rect.XMax) + " " + F(rect.YMax) },
                CursorEnabled = false
            }, Parent(), UiMenu);
            ui.Add(new CuiPanel
            {
                Image = { Color = ColorOr(_config.MenuTitleBg, "0.18 0.16 0.14 0.98") },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 -26", OffsetMax = "0 0" }
            }, UiMenu);
            ui.Add(new CuiLabel
            {
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "10 -26", OffsetMax = "-28 0" },
                Text = { Text = Lang(player, "Title"), FontSize = 13, Align = TextAnchor.MiddleLeft, Color = ColorOr(_config.MenuText, "1 1 1 1"), Font = FontBold }
            }, UiMenu);
            ui.Add(new CuiButton
            {
                Button = { Color = ColorOr(_config.CloseColor, "0.65 0.18 0.14 0.95"), Command = UiCommand + " close" },
                RectTransform = { AnchorMin = "1 1", AnchorMax = "1 1", OffsetMin = "-22 -22", OffsetMax = "-4 -4" },
                Text = { Text = Lang(player, "Close"), FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 0.95", Font = FontBold }
            }, UiMenu);

            AddChoice(ui, player, 0, draft.Mode, "ModeCategorySkin");
            AddChoice(ui, player, 1, draft.Mode, "ModeCategory");
            AddChoice(ui, player, 2, draft.Mode, "ModeName");
            AddChoice(ui, player, 3, draft.Mode, "ModeAmount");
            AddCheck(ui, player, 0, draft.Merge, "merge", "Merge");
            AddCheck(ui, player, 1, draft.Remember, "remember", "Remember");

            ui.Add(new CuiButton
            {
                Button = { Color = ColorOr(_config.ApplyColor, "0.41 0.50 0.25 0.95"), Command = UiCommand + " apply" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0.5 0", OffsetMin = "8 8", OffsetMax = "-4 34" },
                Text = { Text = Lang(player, "SortNow"), FontSize = 12, Align = TextAnchor.MiddleCenter, Color = ColorOr(_config.SortTextColor, "0.77 0.92 0.67 1"), Font = FontBold }
            }, UiMenu);
            ui.Add(new CuiButton
            {
                Button = { Color = ColorOr(_config.CancelColor, "0.28 0.28 0.28 0.95"), Command = UiCommand + " cancel" },
                RectTransform = { AnchorMin = "0.5 0", AnchorMax = "1 0", OffsetMin = "4 8", OffsetMax = "-8 34" },
                Text = { Text = Lang(player, "Cancel"), FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.9 0.9 0.9 1", Font = FontRegular }
            }, UiMenu);
            CuiHelper.AddUi(player, ui);
        }

        private void AddChoice(CuiElementContainer ui, BasePlayer player, int mode, int selected, string langKey)
        {
            bool on = selected == mode;
            float top = -32f - mode * 28f;
            ui.Add(new CuiButton
            {
                Button = { Color = on ? ColorOr(_config.MenuSelected, "0.32 0.24 0.16 0.98") : "0 0 0 0", Command = UiCommand + " mode " + mode.ToString(CultureInfo.InvariantCulture) },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "8 " + F(top - 24f), OffsetMax = "-8 " + F(top) },
                Text = { Text = (on ? "(*) " : "( ) ") + Lang(player, langKey), FontSize = 12, Align = TextAnchor.MiddleLeft, Color = ColorOr(_config.MenuText, "0.86 0.84 0.80 1"), Font = FontRegular }
            }, UiMenu);
        }

        private void AddCheck(CuiElementContainer ui, BasePlayer player, int index, bool on, string action, string langKey)
        {
            float top = -148f - index * 28f;
            ui.Add(new CuiButton
            {
                Button = { Color = "0 0 0 0", Command = UiCommand + " " + action },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "8 " + F(top - 24f), OffsetMax = "-8 " + F(top) },
                Text = { Text = (on ? "[x] " : "[ ] ") + Lang(player, langKey), FontSize = 12, Align = TextAnchor.MiddleLeft, Color = ColorOr(_config.MenuText, "0.86 0.84 0.80 1"), Font = FontRegular }
            }, UiMenu);
        }

        private void DrawTransfer(BasePlayer player)
        {
            var ui = new CuiElementContainer();
            AddTransferButton(ui, Parent(), UiTransferMain, _config.MainAnchorMin, _config.MainAnchorMax, _config.MainOffsetMin, _config.MainOffsetMax, UiCommand + " transfer main", Lang(player, "Transfer"));
            CuiHelper.AddUi(player, ui);
        }

        // Drawn only while the backpack panel is open. Anchor 0.5 0.
        // Config offsets are the 1-row header rectangle (default 330 175.5 / 462 197.5).
        // Each extra row of that page adds Y per row, so the button stays on the header.
        private void ShowPackButton(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            CuiHelper.DestroyUi(player, UiTransferPack);
            if (!Active() || _config == null || !_config.ShowBackpack) return;
            if (!HasPerm(player, PermTransfer)) return;
            int capacity;
            if (!PackOpen(player, out capacity)) return;
            float x0;
            float y0;
            float x1;
            float y1;
            if (!PackRectangle(capacity, out x0, out y0, out x1, out y1)) return;
            string anchorMin = string.IsNullOrEmpty(_config.PackAnchorMin) ? "0.5 0" : _config.PackAnchorMin;
            string anchorMax = string.IsNullOrEmpty(_config.PackAnchorMax) ? "0.5 0" : _config.PackAnchorMax;
            var ui = new CuiElementContainer();
            AddTransferButton(ui, Parent(), UiTransferPack, anchorMin, anchorMax, F(x0) + " " + F(y0), F(x1) + " " + F(y1), UiCommand + " transfer backpack", Lang(player, "Transfer"));
            CuiHelper.AddUi(player, ui);
        }

        private bool PackRectangle(int capacity, out float x0, out float y0, out float x1, out float y1)
        {
            x0 = 330f;
            y0 = 175.5f;
            x1 = 462f;
            y1 = 197.5f;
            float parsedX0;
            float parsedY0;
            float parsedX1;
            float parsedY1;
            bool legacy = IsLegacyPackOffset(_config.PackOffsetMin, _config.PackOffsetMax);
            if (!legacy
                && TryPair(_config.PackOffsetMin, out parsedX0, out parsedY0)
                && TryPair(_config.PackOffsetMax, out parsedX1, out parsedY1))
            {
                x0 = parsedX0;
                y0 = parsedY0;
                x1 = parsedX1;
                y1 = parsedY1;
            }
            float shiftedMin;
            float shiftedMax;
            NightshadeSortRules.ShiftPackButtonY(y0, y1, capacity, _config.BaseY, _config.RowHeight, _config.MaxRows, out shiftedMin, out shiftedMax);
            y0 = shiftedMin;
            y1 = shiftedMax;
            if (x1 < x0)
            {
                float swap = x0;
                x0 = x1;
                x1 = swap;
            }
            if (y1 < y0)
            {
                float swap = y0;
                y0 = y1;
                y1 = swap;
            }
            return x1 - x0 >= 8f && y1 - y0 >= 8f;
        }

        private static bool IsLegacyPackOffset(string min, string max)
        {
            float x0;
            float y0;
            float x1;
            float y1;
            if (!TryPair(min, out x0, out y0) || !TryPair(max, out x1, out y1)) return false;
            return Math.Abs(x0 - 48f) < 0.11f && Math.Abs(y0 - 368f) < 0.11f
                && Math.Abs(x1 - 180f) < 0.11f && Math.Abs(y1 - 390f) < 0.11f;
        }

        private static bool TryPair(string value, out float x, out float y)
        {
            x = 0f;
            y = 0f;
            if (string.IsNullOrEmpty(value)) return false;
            string[] parts = value.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;
            return true;
        }

        private bool PackOpen(BasePlayer player, out int capacity)
        {
            capacity = 0;
            if (player == null) return false;
            ItemContainer open;
            if (_openPack.TryGetValue(player.userID, out open) && open != null)
            {
                capacity = open.capacity;
                return capacity > 0;
            }
            ItemContainer vanilla = VanillaBackpackContents(player);
            if (vanilla == null || player.inventory == null || player.inventory.loot == null) return false;
            PlayerLoot loot = player.inventory.loot;
            bool listed = loot.itemSource != null && loot.itemSource.contents == vanilla;
            if (!listed && loot.containers != null)
            {
                for (int i = 0; i < loot.containers.Count; i++)
                {
                    if (loot.containers[i] == vanilla)
                    {
                        listed = true;
                        break;
                    }
                }
            }
            if (!listed) return false;
            capacity = vanilla.capacity;
            return capacity > 0;
        }

        private ItemContainer OpenBackpackContents(BasePlayer player)
        {
            if (player == null) return null;
            ItemContainer open;
            if (_openPack.TryGetValue(player.userID, out open) && open != null) return open;
            int capacity;
            if (!PackOpen(player, out capacity)) return null;
            return VanillaBackpackContents(player);
        }

        private static ItemContainer VanillaBackpackContents(BasePlayer player)
        {
            if (player == null || player.inventory == null) return null;
            Item backpack = player.inventory.GetBackpackWithInventory();
            if (backpack == null) return null;
            return backpack.contents;
        }

        private void AddTransferButton(CuiElementContainer ui, string parent, string name, string anchorMin, string anchorMax, string offsetMin, string offsetMax, string command, string text)
        {
            ui.Add(new CuiButton
            {
                Button = { Color = ColorOr(_config.TransferColor, "0.41 0.50 0.25 0.9"), Command = command },
                RectTransform =
                {
                    AnchorMin = string.IsNullOrEmpty(anchorMin) ? "0.5 0" : anchorMin,
                    AnchorMax = string.IsNullOrEmpty(anchorMax) ? "0.5 0" : anchorMax,
                    OffsetMin = string.IsNullOrEmpty(offsetMin) ? "48 341" : offsetMin,
                    OffsetMax = string.IsNullOrEmpty(offsetMax) ? "180 363" : offsetMax
                },
                Text = { Text = text, FontSize = 10, Align = TextAnchor.MiddleCenter, Color = ColorOr(_config.TransferText, "0.77 0.92 0.67 1"), Font = FontBold }
            }, parent, name, null);
        }

        private void DestroyUi(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, UiHeader);
            CuiHelper.DestroyUi(player, UiMenu);
            CuiHelper.DestroyUi(player, UiTransferMain);
            CuiHelper.DestroyUi(player, UiTransferPack);
        }

        private string Parent()
        {
            return string.IsNullOrEmpty(_config.Parent) ? "Overlay" : _config.Parent;
        }

        private static string PanelName(BaseEntity entity)
        {
            StorageContainer storage = entity as StorageContainer;
            if (storage != null)
            {
                string panel = Text(storage.panelName);
                if (panel.Length > 0) return panel;
            }
            DroppedItemContainer dropped = entity as DroppedItemContainer;
            if (dropped != null)
            {
                string panel = Text(dropped.lootPanelName);
                if (panel.Length > 0) return panel;
            }
            LootableCorpse corpse = entity as LootableCorpse;
            if (corpse != null)
            {
                string panel = Text(corpse.lootPanelName);
                if (panel.Length > 0) return panel;
            }
            ContainerIOEntity io = entity as ContainerIOEntity;
            if (io != null)
            {
                string panel = Text(io.lootPanelName);
                if (panel.Length > 0) return panel;
            }
            return "generic_resizable";
        }

        private static string Text(object value)
        {
            return value == null ? "" : value.ToString();
        }

        private static string F(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string ColorOr(string value, string fallback)
        {
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        private string Lang(BasePlayer player, string key)
        {
            return lang.GetMessage(key, this, player == null ? null : player.UserIDString);
        }

        private void Tell(BasePlayer player, string key, params object[] args)
        {
            if (player == null) return;
            string body = Lang(player, key);
            if (args != null && args.Length > 0)
                body = string.Format(CultureInfo.InvariantCulture, body, args);
            player.ChatMessage(Lang(player, "Prefix") + body);
        }

        private Draft DraftOf(ulong id)
        {
            Draft draft;
            if (_draft.TryGetValue(id, out draft) && draft != null) return draft;
            draft = new Draft();
            PlayerPref pref = ReadPref(id);
            draft.Mode = pref.Mode;
            draft.Merge = pref.Merge;
            draft.Remember = pref.Remember;
            _draft[id] = draft;
            return draft;
        }

        private PlayerPref ReadPref(ulong id)
        {
            if (_store == null || _store.Players == null) return new PlayerPref();
            PlayerPref pref;
            string key = id.ToString(CultureInfo.InvariantCulture);
            if (_store.Players.TryGetValue(key, out pref) && pref != null)
            {
                pref.Mode = NightshadeSortRules.ClampMode(pref.Mode);
                return pref;
            }
            return new PlayerPref();
        }

        private void Commit(ulong id, int mode, bool merge, bool remember, bool writeChoice)
        {
            if (_store == null) _store = new PrefStore();
            if (_store.Players == null) _store.Players = new Dictionary<string, PlayerPref>();
            string key = id.ToString(CultureInfo.InvariantCulture);
            PlayerPref pref;
            if (!_store.Players.TryGetValue(key, out pref) || pref == null)
            {
                pref = new PlayerPref();
                _store.Players[key] = pref;
            }
            pref.Remember = remember;
            if (writeChoice && remember)
            {
                pref.Mode = NightshadeSortRules.ClampMode(mode);
                pref.Merge = merge;
            }
            SaveData();
        }

        private void LoadData()
        {
            try
            {
                _store = Interface.Oxide.DataFileSystem.ReadObject<PrefStore>(DataPath);
            }
            catch (Exception ex)
            {
                PrintWarning("NightshadeSort player data could not be read. Starting empty. " + ex.Message);
                _store = new PrefStore();
            }
            if (_store == null) _store = new PrefStore();
            if (_store.Players == null) _store.Players = new Dictionary<string, PlayerPref>();
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(DataPath, _store);
        }

        private static Dictionary<string, float> DefaultPanelY()
        {
            var map = new Dictionary<string, float>();
            map["dropboxcontents"] = 237.5f;
            map["furnace"] = 277f;
            map["generic"] = 485.5f;
            map["genericsmall"] = 175.5f;
            map["largefurnace"] = 395f;
            map["toolcupboard"] = 595f;
            map["vendingmachine.storage"] = 423.5f;
            return map;
        }

        private static Dictionary<string, float> DefaultPanelHeight()
        {
            var map = new Dictionary<string, float>();
            map["animal-storage"] = 21f;
            map["dropboxcontents"] = 21f;
            map["furnace"] = 21f;
            map["largefurnace"] = 21f;
            map["toolcupboard"] = 21.5f;
            map["vendingmachine.storage"] = 21f;
            return map;
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
                if (_config == null) throw new Exception("Config was empty.");
            }
            catch (Exception ex)
            {
                PrintWarning("Config could not be read. Loading defaults. " + ex.Message);
                LoadDefaultConfig();
            }
            if (NormalizeConfig()) SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private bool NormalizeConfig()
        {
            bool changed = false;
            if (_config.EnabledPrefabs == null)
            {
                _config.EnabledPrefabs = new List<string>();
                changed = true;
            }
            if (_config.Blacklist == null)
            {
                _config.Blacklist = new List<string>();
                changed = true;
            }
            if (_config.PanelY == null)
            {
                _config.PanelY = DefaultPanelY();
                changed = true;
            }
            else if (FillMissing(_config.PanelY, DefaultPanelY())) changed = true;
            if (_config.PanelHeight == null)
            {
                _config.PanelHeight = DefaultPanelHeight();
                changed = true;
            }
            else if (FillMissing(_config.PanelHeight, DefaultPanelHeight())) changed = true;
            if (_config.CogWidth < 8f) { _config.CogWidth = 17f; changed = true; }
            if (_config.SortWidth < 8f) { _config.SortWidth = 79f; changed = true; }
            if (_config.ButtonHeight < 8f) { _config.ButtonHeight = 23f; changed = true; }
            if (_config.MaxRows < 1) { _config.MaxRows = 8; changed = true; }
            if (_config.MenuWidth < 40f) { _config.MenuWidth = 210f; changed = true; }
            if (_config.MenuHeight < 40f) { _config.MenuHeight = 240f; changed = true; }
            if (string.IsNullOrEmpty(_config.Parent)) { _config.Parent = "Overlay"; changed = true; }
            return changed;
        }

        private static bool FillMissing(Dictionary<string, float> target, Dictionary<string, float> defaults)
        {
            bool changed = false;
            foreach (KeyValuePair<string, float> pair in defaults)
            {
                if (target.ContainsKey(pair.Key)) continue;
                target[pair.Key] = pair.Value;
                changed = true;
            }
            return changed;
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Prefix"] = "<color=#c9b27c>[Sort]</color> ",
                ["Sort"] = "Sort",
                ["Title"] = "SORT BY",
                ["Close"] = "X",
                ["ModeCategorySkin"] = "Category + Skin",
                ["ModeCategory"] = "Category",
                ["ModeName"] = "Name (A-Z)",
                ["ModeAmount"] = "Amount (biggest first)",
                ["Merge"] = "Merge partial stacks",
                ["Remember"] = "Remember my choice",
                ["SortNow"] = "SORT NOW",
                ["Cancel"] = "Cancel",
                ["Transfer"] = "TRANSFER ALL",
                ["NoPermission"] = "You do not have permission to do that.",
                ["Moved"] = "Moved {0} stacks. {1} could not fit.",
                ["NoneMoved"] = "Nothing could be moved into that container."
            }, this);
        }
    }
#endif

#if NIGHTSHADE_SORT_TESTS
    public static class NightshadeSortRulesTest
    {
        public static int Main()
        {
            string error;
            if (!NightshadeSortRules.Check(out error))
            {
                System.Console.Error.WriteLine(error);
                return 1;
            }
            System.Console.WriteLine("ok");
            return 0;
        }
    }
#endif
}
