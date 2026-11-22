using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Localization;
using Godot;

namespace MelvorMods.Shared;

/// <summary>
/// One line of an item's "Found in" or "Used in" list, in words: the heading it goes under ("Combat", the
/// name of a skill, "Opening"...), who holds the item, how many and how likely.
/// </summary>
sealed record ItemLine(string Group, string Name, Texture2D? Icon, string Quantity, double Chance, ItemRef Ref);

/// <summary>
/// Names what an <see cref="ItemIndex"/> found: the enemy and where it lives, the recipe and its skill, the
/// chest. One per open character, used on the game's thread only; what it looks up in the running game (the
/// areas, the skills' recipes) is looked up once, when the first line needs it.
/// </summary>
sealed class ItemLines(Game game)
{
    Dictionary<object, string>? _skillOfRecipe;
    Dictionary<Enemy, List<BaseCombatArea>>? _areasOfEnemy;

    public ItemLine Describe(ItemRef r)
    {
        string quantity = r.Max > r.Min ? $"{r.Min:N0}-{r.Max:N0}" : r.Min > 1 ? $"x{r.Min:N0}" : "";
        switch (r.Owner)
        {
            case Enemy enemy:
                List<string> areas = AreasOf(enemy);
                string where = areas.Count == 0 ? "" : "  ·  " + areas[0] + (areas.Count > 1 ? $" +{areas.Count - 1}" : "");
                return new ItemLine("Combat", enemy.DisplayName.Text + where, ((IDescribedObject)enemy).Icon, quantity, r.Chance, r);
            case Dungeon dungeon:
                return new ItemLine("Combat", dungeon.DisplayName.Text + "  ·  dungeon reward", dungeon.Icon, quantity, r.Chance, r);
            case Item holder:
                string kind = r.Path.Contains("Openable", StringComparison.OrdinalIgnoreCase) || r.Path.Contains("DropTable", StringComparison.OrdinalIgnoreCase) ? "Opening"
                            : r.Path.Contains("Upgrad", StringComparison.OrdinalIgnoreCase) ? "Upgrading" : "Items";
                return new ItemLine(kind, holder.DisplayName.GetPluralText(1), holder.Icon, quantity, r.Chance, r);
            default:
                return new ItemLine(SkillOf(r.Owner), NameOf(r.Owner), (r.Owner as IDescribedObject)?.Icon, quantity, r.Chance, r);
        }
    }

    public static string Percent(double chance) =>
        chance >= 0.9995 ? "always" : chance >= 0.1 ? chance.ToString("0.#%") : chance >= 0.001 ? chance.ToString("0.##%")
        : chance > 0 ? $"1 in {1 / chance:N0}" : "-";

    // A guaranteed drop takes one try. Zero means no count fits: the target is unreachable or too large.
    public static int Tries(double chance, double share)
    {
        if (share <= 0) return 1;
        if (chance <= 0 || share > 1) return 0;
        if (chance >= share) return 1;
        if (share == 1) return 0;
        double count = Math.Ceiling(LogMiss(share) / LogMiss(chance));
        return count <= int.MaxValue ? (int)count : 0;

        // For rare drops, recover the precision lost when subtracting the probability from one.
        static double LogMiss(double p)
        {
            double miss = 1 - p;
            return miss == 1 ? -p : Math.Log(miss) * (p / (1 - miss));
        }
    }

    public static string? Luck(ItemLine line)
    {
        if (line.Chance is not (> 0 and < 0.9995)) return null;
        int half = Tries(line.Chance, 0.5), nineInTen = Tries(line.Chance, 0.9);
        if (half == 0 || nineInTen == 0) return null;
        string Count(int n) => line.Ref.Owner switch
        {
            Enemy => n == 1 ? "1 kill" : $"{n:N0} kills",
            Dungeon => n == 1 ? "1 run" : $"{n:N0} runs",
            _ => n == 1 ? "1 try" : $"{n:N0} tries",
        };
        // A coin toss or better: "half the time within 1 try" would say less than the chance itself.
        return half == 1 ? $"9 times in 10 within {Count(nineInTen)}."
            : $"Half the time within {Count(half)}. 9 times in 10 within {nineInTen:N0}.";
    }

    public static string NameOf(object owner)
    {
        object? name = owner.GetType().GetProperty("DisplayName")?.GetValue(owner);
        string? text = name switch
        {
            IPluralString plural => plural.GetPluralText(1),
            ILocalizedString localized => localized.Text,
            string s => s,
            _ => null,
        };
        return !string.IsNullOrWhiteSpace(text) ? text : (owner as INamespacedObject)?.Id.Split(':').Last() ?? owner.GetType().Name;
    }

    // The skill a recipe belongs to: asked from the skills themselves where they list their recipes, read
    // from the class's place in the game's code otherwise ("...Entities.Thieving.ThievingRecipe").
    public string SkillOf(object owner)
    {
        if (_skillOfRecipe == null)
        {
            _skillOfRecipe = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
            foreach (GameEntity entity in game.Entities.GetAllObjects())
                foreach (var component in entity.Components.Values)
                {
                    if (component.GetType().GetProperty("ActiveRecipes")?.GetValue(component) is not IDictionary recipes) continue;
                    string skill = NameOf(entity);
                    foreach (object recipe in recipes.Keys) _skillOfRecipe.TryAdd(recipe, skill);
                }
        }
        if (_skillOfRecipe.TryGetValue(owner, out string? known)) return known;
        Type type = owner.GetType();
        Match inEntities = Regex.Match(type.Namespace ?? "", @"\.Entities\.(\w+)");
        if (inEntities.Success) return inEntities.Groups[1].Value;
        // "ItemUpgrade" -> "Item Upgrade"
        return Regex.Replace(type.Name, "(?<=[a-z])(?=[A-Z])", " ");
    }

    /// <summary>The names of the areas and dungeons an enemy is fought in, in the game's order.</summary>
    public List<string> AreasOf(Enemy enemy) => PlacesOf(enemy).Select(a => a.DisplayName.Text).Distinct().ToList();

    /// <summary>The areas and dungeons an enemy is fought in.</summary>
    public List<BaseCombatArea> PlacesOf(Enemy enemy)
    {
        if (_areasOfEnemy == null)
        {
            _areasOfEnemy = new Dictionary<Enemy, List<BaseCombatArea>>(ReferenceEqualityComparer.Instance);
            void Put(Enemy e, BaseCombatArea area)
            {
                if (!_areasOfEnemy.TryGetValue(e, out List<BaseCombatArea>? list)) _areasOfEnemy[e] = list = new List<BaseCombatArea>();
                if (!list.Contains(area)) list.Add(area);
            }
            if (game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? c) && c is CombatEntity combat)
                foreach (BaseCombatArea area in combat.Areas.ActiveAreas.Keys)
                {
                    if (area is CombatArea open) foreach (Enemy e in open.Enemies) Put(e, area);
                    else if (area is Dungeon dungeon)
                        foreach (DungeonEnemies wave in dungeon.Enemies)
                            foreach (WeightedEnemy weighted in wave.Enemies.Items) Put(weighted.Enemy, area);
                }
        }
        return _areasOfEnemy.GetValueOrDefault(enemy) ?? new List<BaseCombatArea>();
    }
}
