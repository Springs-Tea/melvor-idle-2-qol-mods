using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Data.DropTables;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Random;
using Godot;

namespace MelvorMods.Shared;

/// <summary>
/// One place an item comes from or goes to: the game object that holds it (an enemy, a recipe, a chest...),
/// how many, and how likely. <see cref="Chance"/> is 1 for "always".
/// </summary>
sealed record ItemRef(object Owner, string Path, double Chance, int Min, int Max);

sealed class ItemEntry
{
    public readonly List<ItemRef> Sources = new(), Uses = new();
}

/// <summary>
/// Where every item of the game comes from and where it is used, read from the game's own data.
///
/// The game keeps this knowledge spread over its content: an enemy's drop tables, a recipe's products and
/// ingredients, a thieving target's loot, what a chest holds... Rather than know each of those classes by
/// name (and miss the ones a game update adds), every registered data object is walked once, looking for
/// the few types the game uses to say "this item, this many, this likely". The name of the property that
/// led there tells whether the item is given (Products, Drops, Rewards...) or taken (Costs, Ingredients...).
/// </summary>
sealed class ItemIndex
{
    static readonly Regex Taken = new("Cost|Ingredient|Input|Consum|Fuel|Bait|Requir|Sacrific|Offer|Payment|Seed|Eats|Treats|RootItem", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Given = new("Product|Drop|Reward|Loot|Collectable|Table|Output|Fixed|Yield|Harvest|Result|Upgraded|Gives|Provides|Junk|^Item$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    const int MaxDepth = 6;

    readonly Dictionary<Item, ItemEntry> _entries = new(ReferenceEqualityComparer.Instance);
    // Built and read by the one thread that builds the index.
    readonly Dictionary<Type, PropertyInfo[]> _properties = new();
#if DEVTOOLS
    /// <summary>Development: property paths that held items but said neither "given" nor "taken".</summary>
    public readonly HashSet<string> Unclassified = new();
#endif
    public int Owners { get; private set; }

    public ItemEntry? For(Item item) => _entries.GetValueOrDefault(item);
    /// <summary>Every item that has a source or a use.</summary>
    public IReadOnlyDictionary<Item, ItemEntry> Entries => _entries;
    public int Count => _entries.Count;

    /// <summary>Reads data only; safe to run beside the game.</summary>
    public static ItemIndex Build(Game game)
    {
        var index = new ItemIndex();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        // Every registry of the game's content: enemies, areas, recipes, items, shop purchases, and whatever a
        // later version adds. Only content: the other registries hold the character's live state
        // (statistics, skills), where an item is merely counted or carried.
        foreach (object registry in game.Data.Database.DataClassRegistries.Values)
        {
            IEnumerable? objects;
            try { objects = registry.GetType().GetMethod("GetAllObjects", Type.EmptyTypes)?.Invoke(registry, null) as IEnumerable; }
            catch { continue; }
            if (objects == null) continue;
            foreach (object owner in objects)
            {
                if (owner == null || !seen.Add(owner)) continue;
                index.Owners++;
                var visited = new HashSet<object>(ReferenceEqualityComparer.Instance) { owner };
                index.WalkProperties(owner, owner, "", 1, 0, visited);
            }
        }
        foreach (ItemEntry entry in index._entries.Values)
        {
            Merge(entry.Sources);
            Merge(entry.Uses);
        }
        return index;
    }

    void WalkProperties(object owner, object holder, string path, double chance, int depth, HashSet<object> visited)
    {
        foreach (PropertyInfo property in PropertiesOf(holder.GetType()))
        {
            object? value;
            try { value = property.GetValue(holder); }
            catch { continue; }
            if (value != null) Walk(owner, value, path == "" ? property.Name : path + "." + property.Name, chance, depth, visited);
        }
    }

    void Walk(object owner, object value, string path, double chance, int depth, HashSet<object> visited)
    {
        switch (value)
        {
            case Item item:
                Add(item, owner, path, chance, 1, 1);
                return;
            case ItemQuantityRange range:   // also a weighted drop met outside its table
                Add(range.Item, owner, path, chance, range.MinQuantity, range.MaxQuantity);
                return;
            case ItemQuantityChance maybe:
                Add(maybe.Item, owner, path, chance * Math.Clamp(maybe.Chance, 0, 1), maybe.Quantity, maybe.Quantity);
                return;
            case ItemQuantity some:
                Add(some.Item, owner, path, chance, some.Quantity, some.Quantity);
                return;
            case ItemDropTableChance table:
                AddTable(table.Table, owner, path, chance * Math.Clamp(table.Chance, 0, 1));
                return;
            case IReadOnlyWeightedTable<WeightedItemDrop> drops:
                AddTable(drops, owner, path, chance);
                return;
            case string:
                return;
            case IDictionary map:
                foreach (object? element in map.Values)
                    if (element != null) Walk(owner, element, path, chance, depth, visited);
                foreach (object? key in map.Keys)
                    if (key is Item) Walk(owner, key, path, chance, depth, visited);
                return;
            case IEnumerable list:
                int n = 0;
                foreach (object? element in list)
                {
                    if (element != null) Walk(owner, element, path, chance, depth, visited);
                    if (++n > 5000) break;
                }
                return;
        }
        // Into the object's own parts, but not across to another registered object (an enemy's passive, a
        // recipe's category): that one is walked as an owner of its own.
        Type type = value.GetType();
        if (depth >= MaxDepth || !IsGameData(type) || value is INamespacedObject || !visited.Add(value)) return;
        WalkProperties(owner, value, path, chance, depth + 1, visited);
    }

    void AddTable(IReadOnlyWeightedTable<WeightedItemDrop>? table, object owner, string path, double chance)
    {
        if (table == null) return;
        foreach (WeightedItemDrop drop in table.Items)
            Add(drop.Item, owner, path, chance * table.GetItemChance(drop), drop.MinQuantity, drop.MaxQuantity);
    }

    void Add(Item? item, object owner, string path, double chance, int min, int max)
    {
        if (item == null || ReferenceEquals(item, owner) || chance <= 0) return;
        bool taken = Taken.IsMatch(path);
        if (!taken && !Given.IsMatch(path))
        {
#if DEVTOOLS
            Unclassified.Add($"{owner.GetType().Name}: {path}");
#endif
            return;
        }
        if (!_entries.TryGetValue(item, out ItemEntry? entry)) _entries[item] = entry = new ItemEntry();
        (taken ? entry.Uses : entry.Sources).Add(new ItemRef(owner, path, Math.Min(chance, 1), Math.Max(min, 0), Math.Max(max, min)));
    }

    // The same owner can hold an item twice (a common and a rare table): one line, the chances together.
    static void Merge(List<ItemRef> refs)
    {
        if (refs.Count < 2) return;
        var merged = refs.GroupBy(r => r.Owner, ReferenceEqualityComparer.Instance)
            .Select(g => g.Count() == 1 ? g.First()
                : new ItemRef(g.Key!, g.First().Path, 1 - g.Aggregate(1.0, (miss, r) => miss * (1 - r.Chance)), g.Min(r => r.Min), g.Max(r => r.Max)))
            .ToList();
        refs.Clear();
        refs.AddRange(merged);
    }

    static bool IsGameData(Type type)
    {
        if (type.IsPrimitive || type.IsEnum || typeof(GodotObject).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type)) return false;
        // A piece of content can point at the running game (a trigger names the component it listens to), and
        // from there the bank and everything in it is one step away. That is state, not content.
        if (typeof(GamesByMalcs.Melvor2.Components.IGameComponent).IsAssignableFrom(type) || typeof(GamesByMalcs.Melvor2.Entities.IGameEntity).IsAssignableFrom(type)
            || typeof(Game).IsAssignableFrom(type)) return false;
        string simple = type.Name;
        if (simple.EndsWith("Manager", StringComparison.Ordinal) || simple.EndsWith("Statistic", StringComparison.Ordinal) || simple.EndsWith("System", StringComparison.Ordinal)
            || simple.EndsWith("Inventory", StringComparison.Ordinal) || simple.EndsWith("Wallet", StringComparison.Ordinal)) return false;
        string? space = type.Namespace;
        if (space == null || !space.StartsWith("GamesByMalcs.Melvor2", StringComparison.Ordinal)) return false;
        // Texts, requirements and bonuses mention items too, but neither give nor take them.
        return !space.Contains(".Localization") && !space.Contains(".Conditions") && !space.Contains(".Modifiers") && !space.Contains(".Stats")
            && !space.Contains(".Saves") && !space.Contains(".Audio");
    }

    PropertyInfo[] PropertiesOf(Type type)
    {
        if (_properties.TryGetValue(type, out PropertyInfo[]? known)) return known;
        return _properties[type] = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && Worth(p.PropertyType))
            .ToArray();
    }

    // Never touch a getter that returns a picture, a sound or a plain value: some load their resource on
    // first use, and none can hold an item.
    static bool Worth(Type type) =>
        !type.IsPrimitive && !type.IsEnum && type != typeof(string) && type != typeof(decimal) && type != typeof(Type)
        && !typeof(GodotObject).IsAssignableFrom(type) && !typeof(Delegate).IsAssignableFrom(type)
        && (type.Namespace == null || !type.Namespace.StartsWith("Godot", StringComparison.Ordinal));
}
