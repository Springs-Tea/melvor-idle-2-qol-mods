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
/// How the game may decide that a hit breaks a rock. Its names show a break bar (MiningRock.Progress, starting from
/// MiningActionStats.MinimumProspecting), a "+x%" the page floats after a hit (ActiveRockView.AddChanceIncreaseLabel)
/// and a break chance per rock (MiningActionStats.RockBreakChance); how they make a break is kept in the game's methods.
/// These are the readings the names leave open (the code review of turn 1, "Ore per hour from the game"), and the one
/// the page's own words state (<see cref="SameChanceUntilFull"/>). In each, a hit that does not break adds the rock's
/// break chance to the bar and a break puts the bar back to its minimum.
/// </summary>
enum BreakRule
{
    /// <summary>A hit breaks the rock at the chance the bar stands at before the hit; a full bar always breaks.</summary>
    BarBeforeHit,
    /// <summary>
    /// A hit first adds the break chance to the bar, and breaks the rock at the chance the bar then stands at; a full bar
    /// always breaks.
    /// </summary>
    BarAfterRaise,
    /// <summary>Every hit breaks the rock at its break chance, whatever the bar shows, and nothing else breaks it.</summary>
    SameChance,
    /// <summary>
    /// Every hit breaks the rock at its break chance; a hit that does not break adds it to the bar, a sum, and the hit
    /// that brings the sum to 100% breaks the rock. The card's break chance says so in the game's own words (<see
    /// cref="RockRates.GameWords"/>), and its tooltip calls that chance the rock's current chance (<see
    /// cref="RockRates.Settled"/>).
    /// </summary>
    SameChanceUntilFull,
}

/// <summary>What backs the rule the hour rests on.</summary>
enum RuleSource
{
    /// <summary>The Mining page's own words state it; the README quotes them.</summary>
    GameWords,
    /// <summary>
    /// Counted on a character's own mining by the development watch (miningwatch.request, DevWatch.cs); the window
    /// says "measured", never that it is the game's.
    /// </summary>
    Measured,
}

/// <summary>
/// What one hour at a rock gives, worked out from the game's own numbers for that rock: the stats the game keeps for
/// every rock (MiningRock.Stats, a MiningActionStats), the very ones its card on the Mining page shows. Whatever changes
/// those (level, equipment, potions, the Mastery Guild) changes the hour too. Built the way Fishing Rates builds a
/// spot's hour (mods/FishingRates/SpotRates.cs).
///
/// MiningRock.Hit and MiningActionComponent.RockMined mark hits and breaks. Runite Ore and Topaz Vein gave XP
/// only on breaks. MiningActionStats.Experience supplies the award and the rock's own break rate sets its hour.
/// The listed ore comes with a break, as the Topaz Vein bank counts showed. On-hit items come at their own chances.
/// </summary>
sealed class RockRates
{
    /// <summary>
    /// The first two sentences of the words the Mining page shows on a card's break chance
    /// (MiningRockSelectView.s_rockBreakChanceStatDescription), quoted in the player guides. The light
    /// check reads the game's words and says whether this is still them ("Dev: words").
    /// </summary>
    public const string GameWords = "Your chance to break the rock per hit.\nRock automatically breaks when a sum of 100% is reached.";

    /// <summary>
    /// The rule the hour rests on, and what backs it: set here, in this one place. The card's break chance says it in
    /// the game's words (check-2, "Dev: words"): "Your chance to break the rock per hit. / Rock automatically breaks when
    /// a sum of 100% is reached. Base chance for this Rock: {0:P2} / Current chance for this Rock: {1:P2}". The light
    /// check of 23 Nov 2026 (check-3, "Dev: break rule") read that tooltip on Copper Ore's card while its bar stood part
    /// filled: "Base chance for this Rock: 30.00% / Current chance for this Rock: 45.00%", against the recipe's base 0.3,
    /// the rock's break chance 0.45 (its RockBreakChance, the one the card shows) and its bar 0.375. The game's current
    /// chance is the rock's break chance, not the bar and not the bar with a hit's raise: a hit breaks the rock at that
    /// chance, one that does not adds it to the sum (the break bar, which grows by it: a "+x%" after the hit), and the hit
    /// that brings the sum to 100% breaks the rock. Every light check reads it again ("Dev: break rule", AS SETTLED);
    /// the watch (miningwatch.request, DevWatch.cs) can measure it on a character's own mining. Null here would leave the
    /// window with the time of a hit only.
    /// </summary>
    static readonly (BreakRule Rule, RuleSource Source)? Known = (BreakRule.SameChanceUntilFull, RuleSource.GameWords);

    /// <summary>The rule the hour rests on, or null while nothing has settled it (<see cref="Known"/>).</summary>
    public static (BreakRule Rule, RuleSource Source)? Settled => Known;

    /// <summary>The XP rule used for every rock, with the observations behind the XP column's tip.</summary>
    public sealed record BreakXp(string Measurement)
    {
        public double? PerHour(RockRates rock) => rock.UnknownBreakReward == null ? rock.XpEach * rock.BreaksPerHour : null;
        public string Tip => "Mining XP per hour, counting each rock's listed XP once per break.\n" + Measurement;
    }

    /// <summary>The measurements behind counting the rock's listed XP once per break.</summary>
    public static readonly BreakXp KnownXp = new("Runite Ore (85 breaks) and Topaz Vein (92) gave XP on breaks only.");

    /// <summary>
    /// One item a hit can bring. <see cref="Chance"/> is per hit; <see cref="Each"/> the average number a hit that
    /// brings it gives, doubling included.
    /// </summary>
    public sealed record Drop(Item Item, string Name, double Chance, int Quantity, double Each, double PerHour, double? GoldPerHour);

    /// <summary>
    /// One of the rock's own stone, essence or bar items that the character has a chance at, but that the game's list
    /// of a hit's items does not hold. The window does not count it: none was on the Mining page's cards in the light
    /// check of 23 Nov 2026, where no rock had a chance above 0, so how the game gives it is not known.
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
    /// <summary>The player hid this rock on the Mining page; it is listed only while the page lists hidden rocks.</summary>
    public bool Hidden { get; init; }
    /// <summary>A click on the line changes what the Mining page shows (MiningRatesMod.ClickChanges).</summary>
    public bool Clickable { get; set; }
    /// <summary>
    /// How the rock's card stands against the window when the Mining page shows it (Native.Place), null when it does
    /// not: the tooltip says when the page has no room to show it clear of the window.
    /// </summary>
    public CardSight? Sight { get; set; }

    /// <summary>The game's time of a hit, in seconds (MiningActionStats.Interval, kept in milliseconds).</summary>
    public double HitSeconds { get; private set; }
    /// <summary>The game gives a hit time: without one there is nothing to count.</summary>
    public bool HasTime => HitSeconds > 0.01 && double.IsFinite(HitSeconds);
    public double HitsPerHour => HasTime ? 3600 / HitSeconds : 0;

    /// <summary>The current break chance the rock's card shows, including bonuses above 100%.</summary>
    public double CardBreakChance { get; private set; }
    /// <summary>The chance used to count breaks, capped at certainty.</summary>
    public double BreakChance => Fraction(CardBreakChance);
    /// <summary>
    /// Where the game's minimum of the break bar stands (MiningActionStats.MinimumProspecting), as the game keeps it.
    /// The break chance and the bar are fractions (Mithril Ore's bar at 0.675 after three hits of 0.225); a minimum is
    /// counted as the same, the part of the bar a break leaves filled (the mod's reading of its name), only while it is
    /// a part of the bar (<see cref="MinimumCounted"/>).
    /// </summary>
    public double MinimumProspecting { get; private set; }
    /// <summary>
    /// The minimum is a part of the bar, from 0 to under 1. A guaranteed break needs no starting bar.
    /// </summary>
    public bool MinimumCounted => MinimumProspecting >= 0 && MinimumProspecting < 1;

    /// <summary>
    /// How many hits a break takes on average, by the settled rule (<see cref="Settled"/>) from the rock's own break
    /// chance and minimum; null while no rule is settled, when the minimum cannot be counted, and for a rock that would
    /// never break by it.
    /// </summary>
    public double? HitsPerBreak { get; private set; }

    /// <summary>Why the window cannot count the rewards from this rock's breaks, or null when it can.</summary>
    public string? UnknownBreakReward => BreakChance <= 0 ? "There is no usable break chance for this rock."
        : !double.IsFinite(MinimumProspecting) && BreakChance < 1 ? "There is no usable minimum break bar for this rock."
        : !MinimumCounted && HitsPerBreak == null ? $"The minimum break bar reads {Words.Number(MinimumProspecting)}, outside the range from 0 to under 1."
        : Ore == null || OreQuantity <= 0 ? "This rock has no listed break product with a positive quantity."
        : null;

    public double XpEach { get; private set; }
    /// <summary>Mining XP per hour: the rock's XP once a break.</summary>
    public double? XpPerHour => KnownXp.PerHour(this);

    public double Doubling { get; private set; }

    /// <summary>The rock's ore, which comes with a break (MiningRecipe.Product).</summary>
    public Item? Ore { get; private set; }
    public string OreName { get; private set; } = "";
    /// <summary>How many ore a break gives before doubling (MiningActionStats.ProductQuantity).</summary>
    public int OreQuantity { get; private set; }
    /// <summary>The average ore of a break: its quantity, doubled on the doubling chance (the mod's reading).</summary>
    public double OrePerBreak => OreQuantity * (1 + Doubling);
    public double? BreaksPerHour => HitsPerBreak is { } hits && hits > 0 && HasTime ? HitsPerHour / hits : null;
    public double? BreakOrePerHour => UnknownBreakReward == null ? BreaksPerHour * OrePerBreak : null;
    public double? OrePerHour => BreakOrePerHour + PerHit.Where(d => Equals(d.Item, Ore)).Sum(d => d.PerHour);
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
    /// What the items of an hour sell for, in gold: the ore and the items of a hit. Null while no rule for the hits a
    /// break takes is settled: the ore's hour is not known then.
    /// </summary>
    public double? GoldPerHour => BreakOrePerHour * (OrePrice ?? 0) + PerHitGoldPerHour;

    // The game keeps intervals in milliseconds (Fishing Rates found its catch times to match the game's to the
    // hundredth this way) and chances as fractions (0.1 is 10%).
    const double Ms = 1000;
    const string Gold = "MelvorBase:GoldPieces";

    /// <summary>
    /// A rock the Mining page shows: its own requirements to be seen, and those of its category, are met (Fishing Rates'
    /// SpotRates.Revealed), and a rock the player hid is shown only while the page lists hidden rocks.
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
        r.CardBreakChance = stats.RockBreakChance.Value;
        // As the game keeps it, not cut to a fraction: a minimum of 1 or more is not counted (MinimumCounted).
        r.MinimumProspecting = stats.MinimumProspecting.Value;
        r.XpEach = stats.Experience.XpToAdd.Value;
        r.Doubling = Chance(stats.DoublingChance);
        r.HitsPerBreak = Settled is { } settled && r.HasTime && r.BreakChance > 0
            ? ExpectedHits(settled.Rule, r.BreakChance, r.MinimumProspecting) : null;
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

    /// <summary>
    /// The hits a break takes on average by one reading of the break bar (<see cref="BreakRule"/>), from the rock's own
    /// break chance (what a hit that does not break adds to the bar) and the bar's minimum (where it stands after a
    /// break): the sum, over every hit of a break, of the chance that no hit before it broke the rock. Null when the rock
    /// would never break by it.
    /// </summary>
    public static double? ExpectedHits(BreakRule rule, double chance, double minimum)
    {
        if (rule == BreakRule.SameChance) return chance > 0 ? 1 / chance : null;
        if (rule == BreakRule.SameChanceUntilFull && chance >= 1) return 1;
        if (minimum is not (>= 0 and < 1)) return null;
        if (rule == BreakRule.SameChanceUntilFull)
        {
            if (chance <= 0) return null;
            // The hit that brings the sum to 100%: the k-th, the first with minimum + k * chance at 100%. A sum that is 100%
            // on paper is reached (10 hits of 10%), whatever the last digit of the arithmetic says.
            double sure = Math.Max(1, Math.Ceiling((1 - minimum) / chance - 1e-9));
            double miss = 1 - chance;
            // Every hit before the sure one breaks the rock at the chance: 1 + miss + miss^2 + ... over the sure hits.
            return (1 - Math.Pow(miss, sure)) / (1 - miss);
        }
        // Without a raise the bar stays at its minimum, which is then the chance of every hit.
        if (chance <= 0) return minimum > 0 ? 1 / minimum : null;
        double hits = 0, unbroken = 1;
        // The bar is full after at most 1 / chance hits, and a full bar always breaks: the loop ends by itself.
        for (int k = 0; unbroken > 1e-12; k++)
        {
            hits += unbroken;
            double bar = minimum + (rule == BreakRule.BarAfterRaise ? k + 1 : k) * chance;
            unbroken *= 1 - Math.Clamp(bar, 0, 1);
        }
        return hits;
    }

    static double? GoldOf(Item item) => item.SellsFor is { Currency: { Id: Gold }, Quantity: > 0 } price ? price.Quantity : null;

    static double Chance(ModifierStatNodes? nodes) => nodes == null ? 0 : Fraction(nodes.Total.Value);

    static double Fraction(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
