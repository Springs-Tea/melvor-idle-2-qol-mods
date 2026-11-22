using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Components.CombatFood;
using GamesByMalcs.Melvor2.Components.CombatLevels;
using GamesByMalcs.Melvor2.Components.CombatStats;
using GamesByMalcs.Melvor2.Components.Equipment;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Components.ItemBurying;
using GamesByMalcs.Melvor2.Components.Toolbelt;
using GamesByMalcs.Melvor2.Components.ToolbeltEquipping;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Combat;
using Godot;
using MelvorMods.Shared;

namespace ItemEncyclopedia;

/// <summary>One thing that can be ticked in a group of filters.</summary>
sealed record Choice(string Id, string Name, Texture2D? Icon = null, string Tip = "");

/// <summary>One item of the game, with everything the filters ask about worked out once.</summary>
sealed class Entry(Item item, int order)
{
    public readonly Item Item = item;
    /// <summary>The item's place in the game's own order (the completion log's).</summary>
    public readonly int Order = order;
    public readonly string Name = item.DisplayName.GetPluralText(1);
    /// <summary>Name and description in lower case, for the search box.</summary>
    public string Search = "";
    /// <summary>Ids of the <see cref="Catalog.Types"/> this item is.</summary>
    public readonly HashSet<string> Types = new();
    public EquipmentType? Slot;
    public WeaponType? Weapon;
    public bool Wearable;
    public ToolbeltCapability? Belt;
    /// <summary>Ids of the combat styles the item is for.</summary>
    public readonly HashSet<string> Styles = new();
    /// <summary>Ids of the skills (the game's bank tags) that give the item, take it, or work with it.</summary>
    public readonly HashSet<string> MadeBy = new(), UsedBy = new(), ToolFor = new();
    /// <summary>The highest level asked for to wear or use it; 0 when none is.</summary>
    public int Level;
    public long Price;
    /// <summary>Ids of the <see cref="Catalog.Sources"/> it comes from. Empty until the game's data is read.</summary>
    public readonly HashSet<string> Sources = new();
    /// <summary>The areas and dungeons it drops in.</summary>
    public readonly HashSet<BaseCombatArea> Places = new(ReferenceEqualityComparer.Instance);

    // The character's side of it; kept current while the window is open.
    public int InBank, Worn;
    public bool Found;
    public int Owned => InBank + Worn;
}

/// <summary>What the player asked to see. Written to the settings as it is.</summary>
sealed class Query
{
    public string Search { get; set; } = "";
    public HashSet<string> Types { get; set; } = new();
    public HashSet<string> Slots { get; set; } = new();
    public HashSet<string> Styles { get; set; } = new();
    public HashSet<string> Weapons { get; set; } = new();
    public HashSet<string> Skills { get; set; } = new();
    /// <summary>What a ticked skill means: 0 anything to do with it, 1 it gives the item, 2 it takes it, 3 it works with it.</summary>
    public int SkillMode { get; set; }
    public HashSet<string> Sources { get; set; } = new();
    /// <summary>Id of the one area or dungeon the item must drop in; "" is anywhere.</summary>
    public string Area { get; set; } = "";
    public bool Owned { get; set; }
    public bool Found { get; set; }
    public bool Missing { get; set; }
    /// <summary>Items that were never found keep their place in the list, as "???".</summary>
    public bool SpoilerFree { get; set; }
    public int Sort { get; set; }

    /// <summary>How many filters narrow the list (the search and the sort order are not filters).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int Active => Types.Count + Slots.Count + Styles.Count + Weapons.Count + Skills.Count + Sources.Count
                         + (Area != "" ? 1 : 0) + (Owned ? 1 : 0) + (Found ? 1 : 0) + (Missing ? 1 : 0);

    /// <summary>The search or a filter is narrowing the list.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Narrows => Active > 0 || Search.Trim() != "";

    /// <summary>The same for two questions that would find the same items: the search and the filters, in any order.</summary>
    public string Key()
    {
        static string Of(HashSet<string> set) => string.Join(",", set.OrderBy(id => id, StringComparer.Ordinal));
        return string.Join("|", Search.Trim().ToLowerInvariant(), Of(Types), Of(Slots), Of(Styles), Of(Weapons), Of(Skills),
            Skills.Count > 0 ? SkillMode : 0, Of(Sources), Area, Owned, Found, Missing);
    }

    /// <summary>The search and the filters of this question, without the sort order and the spoiler setting.</summary>
    public Query Copy()
    {
        var copy = new Query();
        copy.Take(this);
        return copy;
    }

    /// <summary>Asks what <paramref name="other"/> asks; the sort order and the spoiler setting stay.</summary>
    public void Take(Query other)
    {
        Search = other.Search.Trim();
        foreach (var (mine, theirs) in new[] { (Types, other.Types), (Slots, other.Slots), (Styles, other.Styles), (Weapons, other.Weapons), (Skills, other.Skills), (Sources, other.Sources) })
        {
            if (ReferenceEquals(mine, theirs)) continue;
            mine.Clear();
            mine.UnionWith(theirs);
        }
        SkillMode = other.SkillMode;
        Area = other.Area;
        Owned = other.Owned; Found = other.Found; Missing = other.Missing;
    }

    public void Reset()
    {
        Search = "";
        Types.Clear(); Slots.Clear(); Styles.Clear(); Weapons.Clear(); Skills.Clear(); Sources.Clear();
        SkillMode = 0;
        Area = "";
        Owned = Found = Missing = false;
    }
}

/// <summary>
/// Every item of the game and the groups it can be found by, all of it read from the game's data: what an
/// item is comes from its capabilities (the game's word for "can be worn", "can be eaten", "goes on the tool
/// belt"...), its slot and weapon kind from the equipment data, its skills from the bank tags the game shows
/// as "Found in" and "Used in", and where it drops from the shared <see cref="ItemIndex"/>.
/// </summary>
sealed class Catalog
{
    public static readonly string[] SortNames =
    {
        "Game order", "Name (A-Z)", "Name (Z-A)", "Level (low first)", "Level (high first)",
        "Sell price (high first)", "Sell price (low first)", "Owned (most first)",
    };

    public static readonly string[] SkillModes = { "Any", "Gives it", "Uses it", "Tools" };

    public readonly List<Entry> Items = new();
    public readonly Dictionary<Item, Entry> ByItem = new(ReferenceEqualityComparer.Instance);
    public readonly List<Choice> Types = new(), Slots = new(), Styles = new(), Weapons = new(), Skills = new(), Sources = new();
    /// <summary>The areas and dungeons something drops in, under the game's own headings.</summary>
    public readonly List<(string Category, List<Choice> Areas)> Areas = new();
    public readonly ItemLines Lines;
    public ItemIndex? Index { get; private set; }
    /// <summary>The bank, for the game's item panel to count from.</summary>
    public GamesByMalcs.Melvor2.Components.Inventories.IInventory? Inventory => _bank?.Inventory;
    /// <summary>What each recipe, purchase, chest or enemy gives and takes: the other side of the index.</summary>
    public readonly Dictionary<object, List<(Item Item, ItemRef Ref)>> Gives = new(ReferenceEqualityComparer.Instance), Takes = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// What a recipe-like thing belongs to, when the game keeps it inside something larger: a path of an agility
    /// course, whose own name ("Simple Path") says little without the course.
    /// </summary>
    public Dictionary<object, object> Parents { get; private set; } = new(ReferenceEqualityComparer.Instance);

    readonly BankEntity? _bank;
    readonly CombatEntity? _combat;
    readonly Dictionary<string, BaseCombatArea> _areaById = new();
    readonly Dictionary<ToolbeltSlotType, List<ToolbeltEquippingComponent>> _belts = new(ReferenceEqualityComparer.Instance);
    // What says "this is for a melee fighter": a level only that style trains, an accuracy only it attacks with.
    readonly Dictionary<ExperienceComponent, CombatStyle?> _styleOfSkill = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<CombatStat, CombatStyle?> _styleOfStat = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<AmmoType, HashSet<string>> _stylesOfAmmo = new(ReferenceEqualityComparer.Instance);
    readonly List<CombatStyle> _styles = new();

    public Catalog(Game game)
    {
        Lines = new ItemLines(game);
        _bank = game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? b) ? b as BankEntity : null;
        _combat = game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? c) ? c as CombatEntity : null;

        ReadStyles(game);
        var tags = game.Data.GetDataRegistryOutput<BankTagCategory>().GetAllObjects().ToList();
        string? combatTag = tags.FirstOrDefault(t => t.LocalId == _combat?.LocalId)?.Id;

        // The game's own order where it has one (the completion log), the order of its data otherwise.
        var all = game.Data.GetDataRegistryOutput<Item>().GetAllObjects().ToList();
        var order = new Dictionary<Item, int>(ReferenceEqualityComparer.Instance);
        try
        {
            if (_bank?.Inventory.ItemCompletion?.ItemDisplayOrder is { } shown)
                foreach (Item item in shown) order.TryAdd(item, order.Count);
        }
        catch { }   // the data's order will do
        foreach (Item item in all) order.TryAdd(item, order.Count);

        // Ammunition is for whoever shoots it: asked from the weapons before the items are sorted out.
        foreach (Item item in all)
            if (item.Capabilities.TryGet(out WeaponCapability? weapon) && weapon is { RequiredAmmoType: { } ammo, Type: { } kind })
            {
                if (!_stylesOfAmmo.TryGetValue(ammo, out HashSet<string>? styles)) _stylesOfAmmo[ammo] = styles = new HashSet<string>();
                foreach (AttackStance stance in kind.Stances) if (stance.CombatStyle != null) styles.Add(stance.CombatStyle.Id);
            }

        var typeNames = new Dictionary<string, string>();
        var slotTypes = game.Data.GetDataRegistryOutput<EquipmentType>().GetAllObjects().ToList();
        foreach (Item item in all)
        {
            // What the game keeps out of its completion log is unfinished or its own (bare fists, test items).
            if (item.IgnoreCompletion) continue;
            var entry = new Entry(item, order[item]);
            entry.Search = (entry.Name + "\n" + item.Description?.Text).ToLowerInvariant();
            entry.Price = item.SellsFor?.Quantity ?? 0;
            Classify(entry, slotTypes, typeNames, combatTag);
            foreach (ItemBankTag tag in item.BankTags)
            {
                if (tag.BankTag == null) continue;
                if (tag.IsProduct) entry.MadeBy.Add(tag.BankTag.Id);
                if (tag.IsIngredient) entry.UsedBy.Add(tag.BankTag.Id);
            }
            Items.Add(entry);
            ByItem[item] = entry;
        }
        Items.Sort((a, b) => a.Order.CompareTo(b.Order));

        // Only what at least one item is: a choice that can never find anything is noise.
        foreach (string id in TypeOrder.Concat(typeNames.Keys.Where(k => !TypeOrder.Contains(k) && k != Material).OrderBy(k => typeNames[k], StringComparer.OrdinalIgnoreCase)).Append(Material))
            // A kind is pictured by the first item of it in the game's order: a sword for the weapons, a log for the materials.
            if (typeNames.TryGetValue(id, out string? name))
                Types.Add(new Choice(id, name, Items.FirstOrDefault(e => e.Types.Contains(id))?.Item.Icon, TypeTips.GetValueOrDefault(id, "Goes on a skill's tool belt")));
        foreach (EquipmentType slot in slotTypes)
            if (Items.Any(e => ReferenceEquals(e.Slot, slot))) Slots.Add(new Choice(slot.Id, slot.DisplayName.Text, slot.Icon));
        foreach (CombatStyle style in _styles)
            if (Items.Any(e => e.Styles.Contains(style.Id))) Styles.Add(new Choice(style.Id, style.DisplayName.Text, style.Icon));
        foreach (WeaponType kind in game.Data.GetDataRegistryOutput<WeaponType>().GetAllObjects().OrderBy(k => k.DisplayName.Text, StringComparer.OrdinalIgnoreCase))
            if (Items.FirstOrDefault(e => ReferenceEquals(e.Weapon, kind)) is { } first) Weapons.Add(new Choice(kind.Id, kind.DisplayName.Text, first.Item.Icon));
        foreach (BankTagCategory tag in tags.OrderBy(t => t.DisplayName.Text, StringComparer.OrdinalIgnoreCase))
            if (Items.Any(e => e.MadeBy.Contains(tag.Id) || e.UsedBy.Contains(tag.Id) || e.ToolFor.Contains(tag.Id)))
                Skills.Add(new Choice(tag.Id, tag.DisplayName.Text, tag.Icon));
        RefreshOwned();
    }

    // ---- what an item is -------------------------------------------------------------------------------------

    // The fixed kinds first, in the order a player thinks of them; the tool belt's own categories follow.
    static readonly string[] TypeOrder = { "weapon", "armour", "jewellery", "ammo", "food", "chest", "bones", "trap", "bait", "compost", "outfit" };
    // What each kind means, for the pointer: the kinds are this mod's words for what the game calls capabilities.
    static readonly Dictionary<string, string> TypeTips = new()
    {
        ["weapon"] = "Held to attack with", ["armour"] = "Worn for protection: helmets, bodies, legs, boots, gloves, capes and shields",
        ["jewellery"] = "Rings and amulets", ["ammo"] = "Shot or thrown by a ranged weapon", ["food"] = "Eaten in combat to heal",
        ["chest"] = "Opened in the bank for what is inside: chests, packs, gifts", ["bones"] = "Buried in the bank for prayer points",
        ["trap"] = "Set in Hunting", ["bait"] = "Used in Fishing", ["compost"] = "Spread on a farming plot",
        ["outfit"] = "Clothes that go on a skill's tool belt", [Material] = "Made, sold or spent, but not worn or used by itself: ores, logs, bars, runes, seeds",
    };
    const string Material = "material";

    void Classify(Entry entry, List<EquipmentType> slotTypes, Dictionary<string, string> typeNames, string? combatTag)
    {
        Item item = entry.Item;
        void Is(string id, string name) { entry.Types.Add(id); typeNames.TryAdd(id, name); }

        item.Capabilities.TryGet(out WeaponCapability? weapon);
        if (item.Capabilities.TryGet(out EquipmentCapability? worn) && worn != null)
        {
            entry.Wearable = true;
            entry.Slot = worn.Type;
            entry.Weapon = weapon?.Type;
            entry.Level = Math.Max(entry.Level, LevelOf(worn.Requirements));
            // The game names the slot, not the family: a ring and an amulet are told by the slot's id.
            if (weapon != null) Is("weapon", WeaponCapability.TypeDisplayName);
            else if (worn.AmmoType != null || worn.Type?.LocalId == "Ammo") Is("ammo", "Ammo");
            else if (worn.Type?.LocalId is "Ring" or "Amulet") Is("jewellery", "Jewellery");
            else Is("armour", "Armour");
            entry.Styles.UnionWith(StylesOf(worn, weapon));
            if (combatTag != null) entry.ToolFor.Add(combatTag);
        }
        if (item.Capabilities.TryGet(out ToolbeltCapability? belt) && belt?.FitsInSlot != null)
        {
            entry.Belt = belt;
            entry.Level = Math.Max(entry.Level, LevelOf(belt.Requirements));
            ToolbeltSlotCategory? category = belt.FitsInSlot.Category;
            // The clothes of a skill sit in categories named like the equipment slots: one kind, told apart by slot.
            EquipmentType? asSlot = category == null ? null : slotTypes.FirstOrDefault(s => s.LocalId == category.LocalId);
            if (asSlot != null) { Is("outfit", "Skilling outfit"); entry.Slot ??= asSlot; }
            else if (category != null) Is("belt:" + category.Id, Spaced(category.DisplayName.Text));
            else Is("belt", "Tool belt");
            foreach (ToolbeltEquippingComponent component in BeltsFor(belt.FitsInSlot))
                entry.ToolFor.Add(component.Entity.Id);
        }
        if (item.Capabilities.TryGet(out FoodCapability? _)) Is("food", FoodCapability.TypeDisplayName);
        if (item.Capabilities.TryGet(out OpenableCapability? chest))
        {
            Is("chest", "Chest");
            entry.Level = Math.Max(entry.Level, LevelOf(chest?.Requirements));
        }
        if (item.Capabilities.TryGet(out BuryCapability? _)) Is("bones", "Bones");
        if (item.Capabilities.TryGet(out HuntingTrapCapability? _)) Is("trap", HuntingTrapCapability.TypeDisplayName);
        if (item.Capabilities.TryGet(out FishingBaitCapability? _)) Is("bait", FishingBaitCapability.TypeDisplayName);
        if (item.Capabilities.TryGet(out CompostCapability? _)) Is("compost", CompostCapability.TypeDisplayName);
        // Nothing the game can do with it but make, sell or spend it: ores, logs, bars, runes, seeds.
        if (entry.Types.Count == 0) Is(Material, "Material");
    }

    // One of the game's categories is written without its space ("SightingsLog").
    static string Spaced(string name) => name.Contains(' ') ? name : System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");

    static int LevelOf(ConditionGroup? requirements)
    {
        int level = 0;
        if (requirements?.Conditions == null) return 0;
        foreach (Condition condition in requirements.Conditions)
            if (condition is LevelCondition asked) level = Math.Max(level, asked.Level);
        return level;
    }

    List<ToolbeltEquippingComponent> BeltsFor(ToolbeltSlotType slot)
    {
        if (_belts.TryGetValue(slot, out List<ToolbeltEquippingComponent>? known)) return known;
        List<ToolbeltEquippingComponent>? found = null;
        try { found = _bank?.ToolbeltComponents.GetComponentsWithSlot(slot); }
        catch { }   // a tool whose skill is not known: it is still listed, under its kind
        return _belts[slot] = found ?? new List<ToolbeltEquippingComponent>();
    }

    // ---- melee, ranged, magic --------------------------------------------------------------------------------

    void ReadStyles(Game game)
    {
        _styles.AddRange(game.Data.GetDataRegistryOutput<CombatStyle>().GetAllObjects());
        // A level that only one style fights with (Attack and Strength for melee, Ranged, Magic) stands for it;
        // Defence and Hitpoints serve all three and say nothing.
        foreach (CombatStyle style in _styles)
            foreach (CombatLevelRatio ratio in style.AccuracyRatingLevels.Concat(style.CombatLevelRatios))
            {
                if (ratio.Level?.Component is not { } skill) continue;
                if (!_styleOfSkill.TryGetValue(skill, out CombatStyle? other)) _styleOfSkill[skill] = style;
                else if (!ReferenceEquals(other, style)) _styleOfSkill[skill] = null;
            }
        foreach (AttackStance stance in game.Data.GetDataRegistryOutput<AttackStance>().GetAllObjects())
        {
            if (stance.AccuracyStat == null || stance.CombatStyle == null) continue;
            if (!_styleOfStat.TryGetValue(stance.AccuracyStat, out CombatStyle? other)) _styleOfStat[stance.AccuracyStat] = stance.CombatStyle;
            else if (!ReferenceEquals(other, stance.CombatStyle)) _styleOfStat[stance.AccuracyStat] = null;
        }
    }

    // The game has no field for "this is a ranger's body armour": it is worked out from what it does have.
    //   A weapon: the styles of the stances its weapon kind fights in.
    //   Ammunition: the styles of the weapons that shoot it.
    //   Anything else worn: the style whose own level it asks for (a Magic level for a wizard's robe); failing
    //   that, the styles whose accuracy it raises; failing that, the styles it does not hold back (leather
    //   takes accuracy from melee and magic, so it is for ranged). What favours nobody belongs to no style.
    HashSet<string> StylesOf(EquipmentCapability worn, WeaponCapability? weapon)
    {
        var styles = new HashSet<string>();
        if (weapon?.Type != null)
        {
            foreach (AttackStance stance in weapon.Type.Stances) if (stance.CombatStyle != null) styles.Add(stance.CombatStyle.Id);
            return styles;
        }
        if (worn.AmmoType != null && _stylesOfAmmo.TryGetValue(worn.AmmoType, out HashSet<string>? shooters)) return shooters;

        if (worn.Requirements?.Conditions != null)
            foreach (Condition condition in worn.Requirements.Conditions)
                if (condition is LevelCondition { Component: { } skill } && _styleOfSkill.GetValueOrDefault(skill) is { } trained) styles.Add(trained.Id);
        if (styles.Count > 0 || worn.Stats?.Stats == null) return styles;

        var accuracy = new Dictionary<CombatStyle, double>(ReferenceEqualityComparer.Instance);
        foreach (CombatStatValue value in worn.Stats.Stats)
            if (value.Stat != null && _styleOfStat.GetValueOrDefault(value.Stat) is { } style) accuracy[style] = accuracy.GetValueOrDefault(style) + value.Value;
        foreach (var (style, sum) in accuracy) if (sum > 0) styles.Add(style.Id);
        if (styles.Count == 0 && accuracy.Values.Any(sum => sum < 0))
            foreach (CombatStyle style in _styles) if (accuracy.GetValueOrDefault(style) >= 0) styles.Add(style.Id);
        return styles;
    }

    // ---- where it comes from: filled in when the game's data has been read ---------------------------------

    public void Attach(ItemIndex index, Dictionary<object, object> parents)
    {
        Index = index;
        Parents = parents;
        var kinds = new HashSet<string>();
        foreach (var (item, known) in index.Entries)
        {
            foreach (ItemRef r in known.Sources) Put(Gives, r.Owner, item, r);
            foreach (ItemRef r in known.Uses) Put(Takes, r.Owner, item, r);
            if (!ByItem.TryGetValue(item, out Entry? entry)) continue;
            foreach (ItemRef r in known.Sources)
            {
                entry.Sources.Add(KindOf(r));
                if (r.Owner is Enemy enemy) entry.Places.UnionWith(Lines.PlacesOf(enemy));
                else if (r.Owner is Dungeon dungeon) entry.Places.Add(dungeon);
            }
        }
        foreach (Entry entry in Items)
        {
            if (entry.Sources.Count == 0) entry.Sources.Add("none");
            kinds.UnionWith(entry.Sources);
        }
        Sources.Clear();
        foreach (Choice kind in SourceKinds) if (kinds.Contains(kind.Id)) Sources.Add(kind with { Icon = GameStyle.Icon(SourceIcons[kind.Id]) });

        Areas.Clear();
        _areaById.Clear();
        var dropsIn = new HashSet<BaseCombatArea>(Items.SelectMany(e => e.Places), ReferenceEqualityComparer.Instance);
        if (_combat == null) return;
        foreach (ActiveCombatAreaCategory category in _combat.Areas.ActiveCategories.Values)
        {
            var areas = new List<Choice>();
            foreach (ActiveCombatArea area in category.Areas)
            {
                if (!dropsIn.Contains(area.Area) || !_areaById.TryAdd(area.Area.Id, area.Area)) continue;
                areas.Add(new Choice(area.Area.Id, area.DisplayName.Text));
            }
            if (areas.Count > 0) Areas.Add((category.Category.DisplayName.Text, areas));
        }
    }

    /// <summary>
    /// Finds what each holder of items belongs to. The game says it the other way round: a course lists its
    /// paths. So every named piece of content is asked for the lists of content it keeps, and what is in
    /// them is its own. Reads data only; run beside the game, with the index.
    /// </summary>
    public static Dictionary<object, object> FindParents(Game game, ItemIndex index)
    {
        var owners = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (ItemEntry known in index.Entries.Values)
            foreach (ItemRef r in known.Sources.Concat(known.Uses))
                if (r.Owner is not (Enemy or Item or BaseCombatArea)) owners.Add(r.Owner);
        var parents = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
        var lists = new Dictionary<Type, System.Reflection.PropertyInfo[]>();
        foreach (object registry in game.Data.Database.DataClassRegistries.Values)
        {
            System.Collections.IEnumerable? objects;
            try { objects = registry.GetType().GetMethod("GetAllObjects", Type.EmptyTypes)?.Invoke(registry, null) as System.Collections.IEnumerable; }
            catch { continue; }
            if (objects == null) continue;
            foreach (object holder in objects)
            {
                if (holder is not GamesByMalcs.Melvor2.Data.INamedObject) continue;
                Type type = holder.GetType();
                if (!lists.TryGetValue(type, out var kept))
                    lists[type] = kept = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && ListOfContent(p.PropertyType)).ToArray();
                foreach (var property in kept)
                {
                    object? value;
                    try { value = property.GetValue(holder); }
                    catch { continue; }
                    if (value is not System.Collections.IEnumerable list) continue;
                    foreach (object? piece in list)
                        if (piece != null && !ReferenceEquals(piece, holder) && owners.Contains(piece)) parents.TryAdd(piece, holder);
                }
            }
        }
        return parents;
    }

    // "A list of registered pieces of content": only such a property is read, never one that could load a
    // picture or reach into the running game.
    static bool ListOfContent(Type type) =>
        type.IsGenericType && type.GetGenericArguments() is [{ IsClass: true } piece] && typeof(GamesByMalcs.Melvor2.Data.INamespacedObject).IsAssignableFrom(piece)
        && typeof(System.Collections.IEnumerable).IsAssignableFrom(type);

    static void Put(Dictionary<object, List<(Item, ItemRef)>> map, object owner, Item item, ItemRef r)
    {
        if (!map.TryGetValue(owner, out List<(Item, ItemRef)>? list)) map[owner] = list = new List<(Item, ItemRef)>();
        list.Add((item, r));
    }

    static readonly Choice[] SourceKinds =
    {
        new("combat", "Enemy drop", null, "Dropped by an enemy, or the reward of a dungeon"),
        new("skill", "Skill", null, "Made, gathered, grown or caught with a skill"),
        new("shop", "Shop", null, "Bought in the shop"),
        new("chest", "Chest", null, "Found by opening another item"),
        new("thieving", "Thieving", null, "Stolen with Thieving"),
        new("quest", "Quest", null, "The reward of a quest"),
        new("upgrade", "Upgrade", null, "Made by upgrading another item"),
        new("none", "No known source", null, "Nothing in the game's data gives this item"),
    };

    // The game's own pictures for them, by their names in its Icons folder.
    static readonly Dictionary<string, string> SourceIcons = new()
    {
        ["combat"] = "Combat", ["skill"] = "SkillIcon", ["shop"] = "Shop", ["chest"] = "OpenChest256", ["thieving"] = "Thieving",
        ["quest"] = "Quests", ["upgrade"] = "upgrade", ["none"] = "question",
    };

    // By the name of the class that holds the item, so a class that is gone after a game update only moves
    // its items under "Skill" instead of stopping the mod.
    static string KindOf(ItemRef r) => r.Owner switch
    {
        Enemy or Dungeon => "combat",
        Item => r.Path.Contains("Upgrad", StringComparison.OrdinalIgnoreCase) ? "upgrade" : "chest",
        _ => r.Owner.GetType().Name switch
        {
            "ShopPurchase" => "shop",
            "ThievingRecipe" => "thieving",
            "Quest" => "quest",
            "ItemUpgrade" => "upgrade",
            _ => "skill",
        },
    };

    // ---- the character's side ----------------------------------------------------------------------------------

    /// <summary>Reads what the character has and has found. True when something is different from last time.</summary>
    public bool RefreshOwned()
    {
        if (_bank == null) return false;
        bool changed = false;
        var completion = _bank.Inventory.ItemCompletion;
        var foundCounts = _bank.Statistics.ItemFound;
        foreach (Entry entry in Items)
        {
            int bank = _bank.Inventory.GetQuantity(entry.Item), worn = 0;
            if (entry.Wearable && _combat != null) worn += _combat.Equipment.GetEquippedQuantity(entry.Item);
            if (entry.Belt?.FitsInSlot != null)
                foreach (ToolbeltEquippingComponent belt in BeltsFor(entry.Belt.FitsInSlot)) worn += belt.Toolbelt.GetEquippedQuantity(entry.Item);
            bool found = bank + worn > 0 || (completion != null ? completion.IsItemFound(entry.Item) : foundCounts.GetValue(entry.Item) > 0);
            if (bank == entry.InBank && worn == entry.Worn && found == entry.Found) continue;
            entry.InBank = bank;
            entry.Worn = worn;
            entry.Found = found;
            changed = true;
        }
        return changed;
    }

    // ---- finding ------------------------------------------------------------------------------------------------

    public List<Entry> Find(Query q)
    {
        string[] words = q.Search.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        BaseCombatArea? area = q.Area == "" ? null : _areaById.GetValueOrDefault(q.Area);
        var found = new List<Entry>();
        foreach (Entry e in Items)
        {
            if (q.Owned && e.Owned <= 0) continue;
            if (q.Found && !e.Found) continue;
            if (q.Missing && e.Found) continue;
            if (q.Types.Count > 0 && !q.Types.Overlaps(e.Types)) continue;
            if (q.Slots.Count > 0 && (e.Slot == null || !q.Slots.Contains(e.Slot.Id))) continue;
            if (q.Styles.Count > 0 && !q.Styles.Overlaps(e.Styles)) continue;
            if (q.Weapons.Count > 0 && (e.Weapon == null || !q.Weapons.Contains(e.Weapon.Id))) continue;
            if (q.Skills.Count > 0 && !OfSkill(e, q)) continue;
            if (q.Sources.Count > 0 && !q.Sources.Overlaps(e.Sources)) continue;
            if (q.Area != "" && (area == null || !e.Places.Contains(area))) continue;
            if (words.Length > 0)
            {
                // An item that is still "???" has no name to be found by.
                if (q.SpoilerFree && !e.Found) continue;
                if (!words.All(e.Search.Contains)) continue;
            }
            found.Add(e);
        }
        Comparison<Entry>? by = q.Sort switch
        {
            1 => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            2 => (a, b) => string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase),
            3 => (a, b) => a.Level.CompareTo(b.Level),
            4 => (a, b) => b.Level.CompareTo(a.Level),
            5 => (a, b) => b.Price.CompareTo(a.Price),
            6 => (a, b) => a.Price.CompareTo(b.Price),
            7 => (a, b) => b.Owned.CompareTo(a.Owned),
            _ => null,
        };
        // The list is in the game's order already; equal items keep that order.
        if (by != null) found.Sort((a, b) => by(a, b) is var c && c != 0 ? c : a.Order.CompareTo(b.Order));
        return found;
    }

    static bool OfSkill(Entry e, Query q) => q.SkillMode switch
    {
        1 => q.Skills.Overlaps(e.MadeBy),
        2 => q.Skills.Overlaps(e.UsedBy),
        3 => q.Skills.Overlaps(e.ToolFor),
        _ => q.Skills.Overlaps(e.MadeBy) || q.Skills.Overlaps(e.UsedBy) || q.Skills.Overlaps(e.ToolFor),
    };
}
