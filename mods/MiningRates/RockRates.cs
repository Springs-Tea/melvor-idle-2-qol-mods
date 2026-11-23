using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Mining;
using GamesByMalcs.Melvor2.Stats.Nodes;
using Godot;

namespace MiningRates;

/// <summary>
/// What one hour at a rock gives, worked out from the game's own numbers for that rock: the stats the game keeps for
/// every rock (MiningRock.Stats, a MiningActionStats), the very ones its card on the Mining page shows. Whatever changes
/// those (level, equipment, potions, the Mastery Guild) changes the hour too. Built the way Fishing Rates builds a
/// spot's hour (mods/FishingRates/SpotRates.cs).
///
/// A hit is the game's action: its time is the rock's interval, and every hit gives the XP of the rock's XP tag (the game
/// says "The Skill XP gained per action", and its event of a hit, RockMinedEventArgs, is its ActionPerformedEventArgs).
/// The ore comes with a break. How many hits a break takes is kept in the game's methods, and neither a member nor the
/// page's own words give it, so everything that rests on it (the time of an ore, ore and gold per hour) is null, shown
/// as "-", until a rule backed by the game takes the place of <see cref="HitsPerBreak"/>.
/// </summary>
sealed class RockRates
{
    /// <summary>
    /// One item a hit can bring. <see cref="Chance"/> is per hit; <see cref="Each"/> the average number a hit that
    /// brings it gives, doubling included.
    /// </summary>
    public sealed record Drop(Item Item, string Name, double Chance, int Quantity, double Each, double PerHour, double? GoldPerHour);

    /// <summary>
    /// One of the rock's own stone, essence or bar items that the character has a chance at, but that the game's list
    /// of a hit's items does not hold: whether it comes on a hit or on a break, the game does not say by its names.
    /// </summary>
    public sealed record Side(Item Item, string Name, double Chance);

    /// <summary>A bonus of the character that changes the hour in a way the mod does not count.</summary>
    /// <param name="Key">Which of the game's stats it is, the key of its name (BonusNames).</param>
    /// <param name="Nodes">The game's stat, to find the name the Mining page gives it.</param>
    public sealed record Bonus(string Key, ModifierStatNodes Nodes);

    public const string Shatter = "ShatterChance", MoreOre = "AdditionalProductQuantityChance";

    public required MiningRock Rock { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    /// <summary>The rock's own picture, as its card on the Mining page shows it; null for the game's grey stand-in.</summary>
    public Texture2D? Picture { get; init; }

    /// <summary>The character cannot mine this rock yet.</summary>
    public bool Locked { get; private set; }
    /// <summary>The Mining level a locked rock needs, when a level is what it is missing.</summary>
    public int? Level { get; private set; }
    /// <summary>What a locked rock is missing, in the game's own words, each once (Native.Needs).</summary>
    public string Needs { get; private set; } = "";
    /// <summary>
    /// The rock the Mining page shows in its big panel, the one being mined (MiningActionComponent.CurrentRock).
    /// </summary>
    public bool OnPage { get; init; }
    /// <summary>The player hid this rock on the Mining page; it is listed only while the page's "Show hidden" is ticked.</summary>
    public bool Hidden { get; init; }
    /// <summary>A click on the line changes what the Mining page shows (MiningRatesMod.ClickChanges).</summary>
    public bool Clickable { get; set; }

    /// <summary>The game's time of a hit, in seconds (MiningActionStats.Interval, kept in milliseconds).</summary>
    public double HitSeconds { get; private set; }
    /// <summary>The game gives a hit time: without one there is nothing to count.</summary>
    public bool HasTime => HitSeconds > 0.01 && double.IsFinite(HitSeconds);
    public double HitsPerHour => HasTime ? 3600 / HitSeconds : 0;

    /// <summary>The rock's break chance as the game keeps it and its card shows it (MiningActionStats.RockBreakChance).</summary>
    public double BreakChance { get; private set; }
    /// <summary>Where the game's minimum of the break bar stands (MiningActionStats.MinimumProspecting).</summary>
    public double MinimumProspecting { get; private set; }

    /// <summary>
    /// How many hits a break takes. The game keeps how the break bar grows and when it breaks in its methods; neither a
    /// member nor the Mining page's own words give a number for it, and the mod does not make up a rule of its own
    /// (pull request #61 did, and its review rejected exactly that). Null: the numbers that rest on it show "-".
    /// </summary>
    public double? HitsPerBreak => null;

    public double XpEach { get; private set; }
    /// <summary>Mining XP per hour: the XP of the rock's XP tag on every hit.</summary>
    public double XpPerHour => XpEach * HitsPerHour;

    public double Doubling { get; private set; }

    /// <summary>The rock's ore, which comes with a break (MiningRecipe.Product).</summary>
    public Item? Ore { get; private set; }
    public string OreName { get; private set; } = "";
    /// <summary>How many ore a break gives before doubling (MiningActionStats.ProductQuantity).</summary>
    public int OreQuantity { get; private set; }
    /// <summary>The average ore of a break: its quantity, doubled on the doubling chance.</summary>
    public double OrePerBreak => OreQuantity * (1 + Doubling);
    public double? BreaksPerHour => HitsPerBreak is { } hits && hits > 0 && HasTime ? HitsPerHour / hits : null;
    public double? OrePerHour => BreaksPerHour * OrePerBreak;
    /// <summary>The time of one ore: an hour over the ore of an hour.</summary>
    public double? SecondsPerOre => OrePerHour is { } ore && ore > 0 ? 3600 / ore : null;
    /// <summary>What one ore sells for, in gold; null when it sells for nothing or not for gold.</summary>
    public double? OrePrice { get; private set; }

    /// <summary>The items of a hit (MiningActionStats.OnHitItemChances), each by the game's chance per hit.</summary>
    public List<Drop> PerHit { get; } = new();
    public double PerHitGoldPerHour => PerHit.Sum(d => d.GoldPerHour ?? 0);
    public List<Side> NotCountedItems { get; } = new();
    /// <summary>Bonuses of the character that change the hour in a way the mod does not count.</summary>
    public List<Bonus> NotCounted { get; } = new();

    /// <summary>
    /// What the items of an hour sell for, in gold: the ore and the items of a hit. Null while the ore of an hour is not
    /// known and the ore sells for gold; the items of a hit alone are <see cref="PerHitGoldPerHour"/>.
    /// </summary>
    public double? GoldPerHour => OrePrice is null ? PerHitGoldPerHour : OrePerHour is { } ore ? ore * OrePrice.Value + PerHitGoldPerHour : null;

    // The game keeps intervals in milliseconds (Fishing Rates found its catch times to match the game's to the
    // hundredth this way) and chances as fractions (0.1 is 10%).
    const double Ms = 1000;
    const string Gold = "MelvorBase:GoldPieces";

    /// <summary>
    /// A rock the Mining page shows: its own requirements to be seen, and those of its category, are met (Fishing Rates'
    /// SpotRates.Revealed), and a rock the player hid is shown only while the page's "Show hidden" is ticked.
    /// </summary>
    public static bool Listed(MiningRock rock, MiningActionComponent action) =>
        rock.Recipe.DisplayRequirements?.AreMet != false && rock.Category?.DisplayRequirements?.AreMet != false &&
        (!rock.IsHidden || action.ShowHiddenRocks);

    /// <param name="shown">A picture of the game as the list shows it: null for the grey stand-in (GamePictures.Shown).</param>
    public static RockRates Read(MiningRock rock, MiningEntity mining, Func<Texture2D?, Texture2D?> shown)
    {
        MiningRecipe recipe = rock.Recipe;
        MiningActionStats stats = rock.Stats;
        var r = new RockRates
        {
            Rock = rock, Name = recipe.DisplayName.Text, Category = rock.Category?.DisplayName.Text ?? "",
            Picture = shown(recipe.Media ?? recipe.Icon),
            OnPage = ReferenceEquals(mining.Action.CurrentRock, rock),
            Hidden = rock.IsHidden,
        };
        ConditionGroup?[] groups = [rock.Category?.Requirements, recipe.Requirements];
        if (groups.Any(g => g?.AreMet == false))
        {
            r.Locked = true;
            r.Needs = Native.Needs(groups);
            r.Level = Native.LevelNeeded(groups, mining.Experience);
        }

        r.HitSeconds = stats.Interval.Value / Ms;
        r.BreakChance = Fraction(stats.RockBreakChance.Value);
        r.MinimumProspecting = Fraction(stats.MinimumProspecting.Value);
        r.XpEach = stats.Experience.XpToAdd.Value;
        r.Doubling = Chance(stats.DoublingChance);
        double hits = r.HitsPerHour;

        if (recipe.Product?.Item is { } ore)
        {
            r.Ore = ore;
            r.OreName = ore.DisplayName.GetPluralText(2);
            r.OreQuantity = Math.Max(0, stats.ProductQuantity.Value);
            r.OrePrice = GoldOf(ore);
        }

        // The items of a hit, by the game's own chances with the character's bonuses (not the recipe's base
        // RandomProductsPerHit). The game passes the doubling chance along when it adds them
        // (MiningActionComponent.AddRandomItems), so it doubles them, as Fishing Rates counts its special items.
        foreach (var node in stats.OnHitItemChances?.Values ?? [])
        {
            if (node.Value is not { Item: { } item } drop || drop.Quantity <= 0) continue;
            double chance = Fraction(drop.Chance), each = drop.Quantity * (1 + r.Doubling), perHour = hits * chance * each;
            r.PerHit.Add(new Drop(item, item.DisplayName.GetPluralText(2), chance, drop.Quantity, each, perHour,
                GoldOf(item) is { } price ? perHour * price : null));
        }

        // The rock's own stone, essence and bar: each with the character's chance at it, never every stone or essence of
        // the skill (the review of pull request #61, fault 4). Counted where the game lists it among a hit's items.
        foreach (var (item, nodes) in new[] { (recipe.StoneItem, stats.StoneItemChance), (recipe.EssenceItem, stats.EssenceItemChance), (recipe.BarItem, stats.BarItemChance) })
            if (item != null && Chance(nodes) > 0 && !r.PerHit.Any(d => ReferenceEquals(d.Item, item)))
                r.NotCountedItems.Add(new Side(item, item.DisplayName.GetPluralText(2), Chance(nodes)));

        // Bonuses that change what a hit or a break brings in ways the game keeps to itself; the tooltip names them the
        // way the Mining page's list of bonuses does (BonusNames).
        foreach (var (key, nodes) in new[] { (Shatter, stats.ShatterChance), (MoreOre, stats.AdditionalProductQuantityChance) })
            if (nodes != null && Chance(nodes) > 0) r.NotCounted.Add(new Bonus(key, nodes));
        return r;
    }

    static double? GoldOf(Item item) => item.SellsFor is { Currency: { Id: Gold }, Quantity: > 0 } price ? price.Quantity : null;

    static double Chance(ModifierStatNodes? nodes) => nodes == null ? 0 : Fraction(nodes.Total.Value);

    static double Fraction(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
