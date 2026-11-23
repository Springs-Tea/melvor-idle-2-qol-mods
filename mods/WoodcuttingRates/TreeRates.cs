using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Woodcutting;
using GamesByMalcs.Melvor2.Stats.Nodes;
using Godot;

namespace WoodcuttingRates;

/// <summary>
/// What one hour at a tree gives, worked out from the game's own numbers for that tree: the stats the game keeps for
/// every tree (WoodcuttingTree.Stats, a WoodcuttingActionStats), the very ones its tree cards show. Whatever changes those
/// (level, equipment, potions, the Mastery Guild, the overgrowth on the tree now) changes the hour too.
///
/// A tree takes several hits to cut. How much damage a hit deals is not a value of the game: an ordinary hit's damage is
/// nowhere among its members, and how a weak spot's damage is made from the game's two numbers for it
/// (WoodcuttingActionComponent.BaseWeakSpotDamage, MaxHitpointsWeakSpotDamage) is a rule in its code. So the hits a cut
/// takes are not counted, and neither is anything that rests on them: the logs of an hour, the time per log, the items
/// of a cut per hour and the gold. Those are null here and "-" in the window, never a formula of the mod's own (issue
/// #71). The development watch (woodcuttingwatch.request) measures what the game does, for a later turn to settle.
///
/// What is counted: the time of a hit (the game's Interval), the hits of an hour, the XP of every hit, and the items a
/// hit can bring. What is the mod's own there: every chance counted as its long-run average, the way Recipe Rates
/// counts them (mods/RecipeRates/Rates.cs).
/// </summary>
sealed class TreeRates
{
    /// <summary>
    /// One thing a hit or a cut can bring: <see cref="Chance"/> per hit or per cut, <see cref="Each"/> the average
    /// number it gives when it comes, doubling included. Per hour only where the hits or cuts of an hour are counted.
    /// </summary>
    public sealed record Drop(Item Item, string Name, double Chance, int Quantity, double Each, double? PerHour, double? GoldPerHour);

    /// <summary>A chance of the tree's that changes the hits a cut takes, which the window does not count.</summary>
    /// <param name="Key">Which of the game's stats it is, the key of its name (BonusNames).</param>
    /// <param name="Nodes">The game's stat, to find the name the Woodcutting page gives it.</param>
    public sealed record Bonus(string Key, ModifierStatNodes Nodes, double Value);

    /// <summary>An overgrowth effect on the tree now: its name in the game's words and the stacks it has left.</summary>
    public sealed record Effect(string Name, int StacksLeft);

    /// <summary>What the Woodcutting page shows of a tree's card: shown under the page's category filter, and in sight in its list.</summary>
    public readonly record struct Card(bool Shown, bool InSight);

    /// <summary>What a click on a tree's line changes on the Woodcutting page.</summary>
    public enum Opens { Nothing, Category, Scroll }

    public const string DoubleDamage = "DoubleDamageChance", WeakSpot = "WeakSpotChance", InstantCut = "InstantCutChance";

    public required WoodcuttingTree Tree { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    /// <summary>The tree's own picture, as its card shows it; null for the game's grey stand-in (GamePictures).</summary>
    public required Texture2D? Picture { get; init; }

    /// <summary>The character cannot cut this tree yet.</summary>
    public bool Locked { get; private set; }
    /// <summary>The Woodcutting level a locked tree needs, when a level is what it is missing.</summary>
    public int? Level { get; private set; }
    /// <summary>What a locked tree is missing, each requirement once, in the game's own words (Needs.Of).</summary>
    public string Needs { get; private set; } = "";
    /// <summary>The player hid the tree on the Woodcutting page; it is listed only while the page's "Show hidden" is ticked.</summary>
    public bool HiddenByPlayer { get; init; }
    /// <summary>
    /// The tree the Woodcutting page's big panel shows, the one being cut (WoodcuttingActionComponent.CuttingTree):
    /// the lit line.
    /// </summary>
    public bool OnPage { get; init; }
    /// <summary>The tree of the big panel is being cut now: woodcutting is started.</summary>
    public bool BeingCut { get; init; }

    /// <summary>What a click on the line changes, as it was when the tree was read (<see cref="ClickOpens"/>).</summary>
    public Opens Click { get; private set; }
    /// <summary>A click on the line changes what the Woodcutting page shows. Never on the tree its big panel shows.</summary>
    public bool Clickable => Click != Opens.Nothing && !OnPage;

    /// <summary>The time of one hit, in seconds: the game's Interval, with your bonuses.</summary>
    public double HitSeconds { get; private set; }
    /// <summary>The game gives a time for a hit: without one there is nothing to count.</summary>
    public bool HasTime => HitSeconds > 0.01 && double.IsFinite(HitSeconds);
    public double HitsPerHour => HasTime ? 3600 / HitSeconds : 0;

    /// <summary>The tree's hit points, with your bonuses (WoodcuttingActionStats.MaxHealth).</summary>
    public int HitPoints { get; private set; }
    public double DoubleDamageChance { get; private set; }
    public double WeakSpotChance { get; private set; }
    public double InstantCutChance { get; private set; }
    public double Doubling { get; private set; }

    /// <summary>The XP of every hit: the game's XP tag of the tree (WoodcuttingActionStats.Experience).</summary>
    public double XpEach { get; private set; }
    public double XpPerHour => XpEach * HitsPerHour;

    /// <summary>The tree's logs (WoodcuttingRecipe.Product) and how many come at a time (WoodcuttingActionStats.ProductQuantity).</summary>
    public Item? Log { get; private set; }
    public string LogName { get; private set; } = "";
    public int LogQuantity { get; private set; }

    /// <summary>What a hit can bring besides (WoodcuttingActionStats.OnHitItemChances), with your bonuses.</summary>
    public List<Drop> PerHit { get; } = new();
    /// <summary>What a cut can bring besides (WoodcuttingActionStats.OnCutItemChances), with your bonuses.</summary>
    public List<Drop> PerCut { get; } = new();
    public double PerHitGoldPerHour => PerHit.Sum(d => d.GoldPerHour ?? 0);

    /// <summary>The hits a cut takes: not counted (see the class), so null.</summary>
    public double? HitsPerCut => null;
    /// <summary>Logs per hour: not counted while the hits a cut takes are not, so null.</summary>
    public double? LogsPerHour => null;
    /// <summary>How long one log takes, every hit of a cut counted in: null while logs per hour are.</summary>
    public double? SecondsPerLog => LogsPerHour is > 0 and { } logs ? 3600 / logs : null;
    /// <summary>What the logs and items of an hour sell for, in gold: null while the logs and the cuts of an hour are.</summary>
    public double? GoldPerHour => null;

    /// <summary>The tree's chances that change the hits a cut takes, the ones above zero.</summary>
    public List<Bonus> HitChances { get; } = new();
    /// <summary>The overgrowth effects on the tree now; their bonuses are in the tree's numbers above.</summary>
    public List<Effect> Effects { get; } = new();

    // The game keeps intervals in milliseconds (Recipe Rates' Rates.Ms; Fishing Rates' catch times matched the game's
    // to the hundredth) and chances as fractions (0.1 is 10%).
    const double Ms = 1000;
    const string Gold = "MelvorBase:GoldPieces";

    /// <summary>A tree the game shows: its own requirements to be seen are met (WoodcuttingRecipe.DisplayRequirements).</summary>
    public static bool Revealed(WoodcuttingTree tree) => tree.Recipe.DisplayRequirements?.AreMet != false;

    /// <summary>
    /// A tree the Woodcutting page lists: one the game shows, and, when the player hid it with its hide button, only while
    /// the page's "Show hidden" box is ticked (WoodcuttingTree.IsHidden, WoodcuttingActionComponent.ShowHiddenTrees). The
    /// window lists the same trees.
    /// </summary>
    public static bool Listed(WoodcuttingTree tree, WoodcuttingActionComponent action) => Revealed(tree) && (!tree.IsHidden || action.ShowHiddenTrees);

    /// <summary>
    /// A category the player can choose on the Woodcutting page: shown, its requirements met, and, for one the page
    /// shows only while it has trees to cut, one that has (RecipeCategory.OnlyShowWhenRecipesAvailable,
    /// WoodcuttingActionComponent.GetAvailableRecipeCount). The development request compares this with the page's own
    /// category buttons.
    /// </summary>
    public static bool CategoryOpen(WoodcuttingRecipeCategory? category, WoodcuttingActionComponent action) =>
        category != null && category.Requirements?.AreMet != false && category.DisplayRequirements?.AreMet != false &&
        (!category.OnlyShowWhenRecipesAvailable || action.GetAvailableRecipeCount(category) > 0);

    /// <summary>
    /// What a click on a tree's line changes on the Woodcutting page: the page's category filter, when the tree's card is
    /// not shown under it and its category can be chosen; else the page's list scrolled to the card, when it is shown but
    /// not in sight; else nothing. When the page's cards cannot be read (<paramref name="card"/> null), only the filter,
    /// when the page is on another category than the tree's.
    /// </summary>
    public static Opens ClickOpens(WoodcuttingTree tree, WoodcuttingActionComponent action, Card? card)
    {
        bool other = action.SelectedCategory != null && !ReferenceEquals(action.SelectedCategory, tree.Category);
        if (card is { } c)
        {
            if (c.Shown) return c.InSight ? Opens.Nothing : Opens.Scroll;
            return !ReferenceEquals(action.SelectedCategory, tree.Category) && CategoryOpen(tree.Category, action) ? Opens.Category : Opens.Nothing;
        }
        return other && CategoryOpen(tree.Category, action) ? Opens.Category : Opens.Nothing;
    }

    /// <param name="card">The tree's card on the Woodcutting page, or null when the page's cards cannot be read.</param>
    /// <param name="shown">A picture of the game as the list shows it: null for the grey stand-in (GamePictures.Shown).</param>
    public static TreeRates Read(WoodcuttingTree tree, WoodcuttingEntity woodcutting, Card? card, Func<Texture2D?, Texture2D?> shown)
    {
        WoodcuttingRecipe recipe = tree.Recipe;
        WoodcuttingActionComponent action = woodcutting.Action;
        WoodcuttingActionStats stats = tree.Stats;
        var r = new TreeRates
        {
            Tree = tree, Name = recipe.DisplayName.Text, Category = tree.Category?.DisplayName.Text ?? "",
            // The tree's own picture, the one its card shows; its icon when the game has no picture of it.
            Picture = shown(recipe.Media) ?? shown(recipe.Icon),
            HiddenByPlayer = tree.IsHidden,
            OnPage = ReferenceEquals(action.CuttingTree, tree),
            BeingCut = ReferenceEquals(action.CuttingTree, tree) && action.IsStarted,
        };
        r.ReadLock(recipe.Requirements, tree.Category?.Requirements, woodcutting.Experience);
        r.Click = ClickOpens(tree, action, card);

        r.HitSeconds = stats.Interval.Value / Ms;
        r.HitPoints = stats.MaxHealth.Value;
        r.XpEach = stats.Experience.XpToAdd.Value;
        r.Doubling = Chance(stats.DoublingChance);
        r.DoubleDamageChance = Chance(stats.DoubleDamageChance);
        r.WeakSpotChance = Chance(stats.WeakSpotChance);
        r.InstantCutChance = Chance(stats.InstantCutChance);
        foreach (var (key, nodes, value) in new[] { (DoubleDamage, stats.DoubleDamageChance, r.DoubleDamageChance), (WeakSpot, stats.WeakSpotChance, r.WeakSpotChance),
                     (InstantCut, stats.InstantCutChance, r.InstantCutChance) })
            if (nodes != null && value > 0) r.HitChances.Add(new Bonus(key, nodes, value));

        if (recipe.Product?.Item is { } log)
        {
            r.Log = log;
            r.LogName = log.DisplayName.GetPluralText(2);
            r.LogQuantity = stats.ProductQuantity.Value;
        }

        // The items a hit and a cut can bring, with the character's bonuses (not the recipe's base lists). The doubling
        // chance doubles them: the game hands it to the method that adds them (WoodcuttingActionComponent.AddRandomItems
        // takes the item chances and the doubling chance), as Fishing Rates doubles its special items.
        double hits = r.HitsPerHour;
        foreach (var node in stats.OnHitItemChances?.Values ?? [])
            if (node?.Value is { Item: { } item } drop && drop.Quantity > 0)
                r.PerHit.Add(Line(item, Fraction(drop.Chance), drop.Quantity, 1 + r.Doubling, r.HasTime ? hits : null));
        foreach (var node in stats.OnCutItemChances?.Values ?? [])
            if (node?.Value is { Item: { } item } drop && drop.Quantity > 0)
                r.PerCut.Add(Line(item, Fraction(drop.Chance), drop.Quantity, 1 + r.Doubling, null));

        foreach (ActiveOvergrowthEffect effect in tree.ActiveEffects?.Values ?? Enumerable.Empty<ActiveOvergrowthEffect>())
            if (effect?.Effect is { } kind)
                r.Effects.Add(new Effect(kind.DisplayName.Text, effect.StacksLeft));
        return r;
    }

    static Drop Line(Item item, double chance, int quantity, double times, double? timesPerHour)
    {
        double each = quantity * times;
        double? perHour = timesPerHour * chance * each;
        double? gold = perHour is { } n && item.SellsFor is { Currency: { Id: Gold }, Quantity: > 0 } price ? n * price.Quantity : null;
        return new Drop(item, item.DisplayName.GetPluralText(2), chance, quantity, each, perHour, gold);
    }

    // Locked while the tree's own requirements or those of its category are not met. The level is the Woodcutting level
    // the line shows, as Fishing Rates writes it (SpotRates.ReadLock); the words are every missing requirement once, the
    // game's own, as Thieving Overview writes them (Needs.Of).
    void ReadLock(ConditionGroup? own, ConditionGroup? category, ExperienceComponent woodcutting)
    {
        var groups = new[] { own, category }.OfType<ConditionGroup>().Distinct().ToList();
        if (groups.All(g => g.AreMet)) return;
        Locked = true;
        foreach (ConditionGroup group in groups)
            foreach (Condition condition in group.Conditions ?? Enumerable.Empty<Condition>())
                if (!condition.IsMet && !condition.IsOr && condition is LevelCondition level && ReferenceEquals(level.Component, woodcutting))
                    Level = Math.Max(Level ?? 0, level.Level);
        Needs = WoodcuttingRates.Needs.Of(groups);
    }

    static double Chance(ModifierStatNodes? nodes) => nodes == null ? 0 : Fraction(nodes.Total.Value);

    static double Fraction(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
