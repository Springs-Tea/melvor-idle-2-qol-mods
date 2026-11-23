using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Woodcutting;
using GamesByMalcs.Melvor2.Stats.Nodes;
using Godot;

namespace WoodcuttingRates;

/// <summary>What a tree's logs come with, by a reading of the game's rule (<see cref="TreeRates.LogsRule"/>).</summary>
enum LogsWith
{
    /// <summary>Every hit brings the logs, as many as the tree's quantity.</summary>
    EveryHit,
    /// <summary>A cut brings them.</summary>
    Cut,
    /// <summary>
    /// A hit brings them for every hit point it takes from the tree, so a cut brings them for every hit point of the tree:
    /// a reading of the name of one of the card's two lists (PerHealthGrantsContainer, by its name a list per hit point).
    /// Which items that list shows, and the words over it, the light check reads ("Dev: game").
    /// </summary>
    HitPoint,
}

/// <summary>
/// What the items of a hit come with (<see cref="TreeRates.HitItemsRule"/>): the game's name for their list is
/// "OnHitItemChances", once a hit, while one of the card's two lists is named PerHealthGrantsContainer, by its name per
/// hit point; which items that list shows, and the words over it, the light check reads ("Dev: game"). The same evidence
/// that settles what the logs come with settles these (the code review of turn 2, fault 5).
/// </summary>
enum HitItemsWith
{
    /// <summary>Every hit rolls each item once, at its chance.</summary>
    EveryHit,
    /// <summary>Every hit point a hit takes rolls each item once, so a cut rolls them for every hit point of the tree.</summary>
    HitPoint,
}

/// <summary>Which way a weak spot's share of the tree's hit points is made a whole number of hit points.</summary>
enum Rounding { Down, Nearest, Up }

/// <summary>
/// How much a hit takes from a tree, a reading of the parts of the game's rule that its numbers leave open (the brief
/// of issue #71: an ordinary hit's damage is no value of the game's, and how a weak spot's damage is made from the
/// game's two numbers for it is kept in its code): an ordinary hit takes <paramref name="Damage"/> hit points; a weak
/// spot takes the game's base (WoodcuttingActionComponent.BaseWeakSpotDamage) and its share of the tree's hit points
/// (MaxHitpointsWeakSpotDamage), rounded the given way, instead of the hit's damage or on top of it; a double hit takes
/// twice what it would; an instant cut cuts the tree with that hit.
/// </summary>
sealed record HitRule(int Damage, Rounding WeakSpotRounding, bool WeakSpotAdded);

/// <summary>What backs a part of the rule the hour rests on.</summary>
enum RuleSource
{
    /// <summary>The Woodcutting page's own words state it; the column's tip and the README quote them.</summary>
    GameWords,
    /// <summary>
    /// Counted on a character's own woodcutting by the development watch (woodcuttingwatch.request, DevWatch.cs); the
    /// window says "measured", never that it is the game's.
    /// </summary>
    Measured,
}

/// <summary>A part of the game's rule, settled: the reading, what backs it, and the game's words that state it ("" for none).</summary>
sealed record Settled<T>(T Reading, RuleSource Source, string Quote);

/// <summary>
/// What one hour at a tree gives, worked out from the game's own numbers for that tree: the stats the game keeps for
/// every tree (WoodcuttingTree.Stats, a WoodcuttingActionStats), the very ones its card on the Woodcutting page shows.
/// Whatever changes those (level, equipment, potions, the Mastery Guild, the overgrowth on the tree now) changes the hour
/// too. Built the way Mining Rates builds a rock's hour (mods/MiningRates/RockRates.cs).
///
/// A hit is the game's action: its time is the tree's interval. That every hit gives the XP of the tree's XP tag is the
/// mod's reading of the game's words for that tag ("The Skill XP gained per action.", read on the same SkillXPPanel by
/// Mining Rates' light check) and of its names (its event of a hit, TreeDamagedEventArgs, is its ActionPerformedEventArgs).
/// What a hit takes from the tree, and so how many hits a cut takes, rests on a rule (<see cref="HitsRule"/>), and so does
/// what the logs come with (<see cref="LogsRule"/>); while a part is not settled, what rests on it is null and the window
/// shows "-", never a formula of the mod's own (the review of pull request #61). The items of a hit are counted once a hit
/// until what they come with is settled (<see cref="HitItemsRule"/>): the tooltip names that reading, and the gold, which
/// would rest on it, is not counted.
/// </summary>
sealed class TreeRates
{
    /// <summary>
    /// What the logs come with, and what backs it: set here, in this one place, once the light check's "Dev: words" and
    /// "Dev: card stats" (the words the page shows on a card and on its lists) or the watch's "LOGS ON ..." settle it.
    /// Null: the logs of an hour, the time of a log and the gold are not counted.
    /// </summary>
    static readonly Settled<LogsWith>? KnownLogs = null;

    /// <summary>
    /// How much a hit takes from a tree, and what backs it: set here, in this one place, once the page's words or the
    /// watch's counts ("AN ORDINARY HIT DEALS n", "A WEAK SPOT DEALS ...", "A DOUBLE HIT DEALS TWICE ...") settle it.
    /// Null: the hits a cut takes, the cuts of an hour and what rests on them are not counted.
    /// </summary>
    static readonly Settled<HitRule>? KnownHits = null;

    /// <summary>
    /// What the items of a hit come with, and what backs it: set here, in this one place, with the same evidence as the
    /// logs (the words over the card's list that shows them and what it is bound to, "Dev: game"; the watch's "ITEMS OF A
    /// HIT ..."). Null: they are counted once a hit, the window's reading of the game's name for their list
    /// (OnHitItemChances), and the line's tooltip says so; the gold, which would rest on that reading, is not counted.
    /// </summary>
    static readonly Settled<HitItemsWith>? KnownHitItems = null;

    /// <summary>What the logs come with, or null while nothing has settled it (<see cref="KnownLogs"/>).</summary>
    public static Settled<LogsWith>? LogsRule => KnownLogs;

    /// <summary>What the items of a hit come with, or null while nothing has settled it (<see cref="KnownHitItems"/>).</summary>
    public static Settled<HitItemsWith>? HitItemsRule => KnownHitItems;

    /// <summary>How much a hit takes, or null while nothing has settled it (<see cref="KnownHits"/>).</summary>
    public static Settled<HitRule>? HitsRule => KnownHits;

    /// <summary>
    /// One thing a hit or a cut can bring besides the logs: <see cref="Chance"/> per hit or per cut, <see cref="Each"/>
    /// the average number it gives when it comes, doubling included, and an hour of it where the hits or cuts of an hour
    /// are counted.
    /// </summary>
    public sealed record Drop(Item Item, string Name, double Chance, int Quantity, double Each, double? PerHour, double? GoldPerHour);

    /// <summary>A chance of the tree's that changes the hits a cut takes.</summary>
    /// <param name="Key">Which of the game's stats it is, the key of its name (BonusNames).</param>
    /// <param name="Nodes">The game's stat, to find the name the Woodcutting page gives it.</param>
    public sealed record Bonus(string Key, ModifierStatNodes Nodes, double Value);

    /// <summary>An overgrowth effect on the tree now: its name in the game's words and the stacks it has left.</summary>
    public sealed record Effect(string Name, int StacksLeft);

    public const string DoubleDamage = "DoubleDamageChance", WeakSpot = "WeakSpotChance", InstantCut = "InstantCutChance";

    public required WoodcuttingTree Tree { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    /// <summary>The tree's own picture, as its card shows it; null for the game's grey stand-in (GamePictures).</summary>
    public Texture2D? Picture { get; init; }

    /// <summary>The character cannot cut this tree yet.</summary>
    public bool Locked { get; private set; }
    /// <summary>The Woodcutting level a locked tree needs, when a level is what it is missing.</summary>
    public int? Level { get; private set; }
    /// <summary>What a locked tree is missing, in the game's own words, each once (Native.Needs).</summary>
    public string Needs { get; private set; } = "";
    /// <summary>The player hid this tree on the Woodcutting page; it is listed only while the page lists hidden trees.</summary>
    public bool Hidden { get; init; }
    /// <summary>
    /// The tree the Woodcutting page shows in its big panel, the one being cut (WoodcuttingActionComponent.CuttingTree):
    /// the lit line.
    /// </summary>
    public bool OnPage { get; init; }
    /// <summary>A click on the line changes what the Woodcutting page shows (WoodcuttingRatesMod.ClickChanges).</summary>
    public bool Clickable { get; set; }
    /// <summary>
    /// How the tree's card stands against the window when the Woodcutting page shows it (Native.Place), null when it
    /// does not: the tooltip says when the page has no room to show it clear of the window.
    /// </summary>
    public CardSight? Sight { get; set; }

    /// <summary>The game's time of a hit, in seconds (WoodcuttingActionStats.Interval, kept in milliseconds).</summary>
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

    /// <summary>The XP of every hit: the game's XP of the tree (WoodcuttingActionStats.Experience).</summary>
    public double XpEach { get; private set; }
    /// <summary>Woodcutting XP per hour: the XP of the tree's XP tag on every hit.</summary>
    public double XpPerHour => XpEach * HitsPerHour;

    /// <summary>The tree's logs (WoodcuttingRecipe.Product) and how many come at a time (WoodcuttingActionStats.ProductQuantity).</summary>
    public Item? Log { get; private set; }
    public string LogName { get; private set; } = "";
    public int LogQuantity { get; private set; }
    /// <summary>What one log sells for, in gold; null when it sells for nothing or not for gold.</summary>
    public double? LogPrice { get; private set; }

    /// <summary>
    /// How many hits a cut takes on average, by the settled rule (<see cref="HitsRule"/>) from the tree's own hit points
    /// and chances; null while no rule is settled, and for a tree that would never be cut by it.
    /// </summary>
    public double? HitsPerCut { get; private set; }
    public double? CutsPerHour => HitsPerCut is { } hits && hits > 0 && HasTime ? HitsPerHour / hits : null;

    /// <summary>The logs of an hour on average, by the settled rules; null while what they rest on is not settled.</summary>
    public double? LogsPerHour { get; private set; }
    /// <summary>The time of one log: an hour over the logs of an hour.</summary>
    public double? SecondsPerLog => LogsPerHour is { } logs && logs > 0 ? 3600 / logs : null;

    /// <summary>What a hit can bring besides (WoodcuttingActionStats.OnHitItemChances), with your bonuses.</summary>
    public List<Drop> PerHit { get; } = new();
    /// <summary>What a cut can bring besides (WoodcuttingActionStats.OnCutItemChances), with your bonuses.</summary>
    public List<Drop> PerCut { get; } = new();
    /// <summary>
    /// What the items of a hit sell for in an hour; null while one that sells is not counted, or is counted only by the
    /// window's reading (<see cref="HitItemsRule"/> not settled).
    /// </summary>
    public double? PerHitGoldPerHour => PerHit.Any(d => GoldOf(d.Item) != null && (d.GoldPerHour == null || HitItemsRule == null)) ? null
        : PerHit.Sum(d => d.GoldPerHour ?? 0);
    public double? PerCutGoldPerHour => PerCut.Any(d => GoldOf(d.Item) != null && d.GoldPerHour == null) ? null : PerCut.Sum(d => d.GoldPerHour ?? 0);

    /// <summary>
    /// What the logs and the items of an hour sell for, in gold. Null while a part of it that sells is not counted by a
    /// settled rule: the logs, the items of a hit, or the items of a cut while the cuts of an hour are not.
    /// </summary>
    public double? GoldPerHour => !HasTime || (LogPrice != null && LogsPerHour == null) || PerHitGoldPerHour is not { } hits || PerCutGoldPerHour is not { } cuts ? null
        : (LogPrice is { } price ? LogsPerHour!.Value * price : 0) + hits + cuts;

    /// <summary>The tree's chances that change the hits a cut takes, the ones above zero.</summary>
    public List<Bonus> HitChances { get; } = new();
    /// <summary>The overgrowth effects on the tree now; their bonuses are in the tree's numbers above.</summary>
    public List<Effect> Effects { get; } = new();

    // The game keeps intervals in milliseconds (Fishing Rates' catch times matched the game's to the hundredth this way,
    // Mining Rates' card times too) and chances as fractions (0.1 is 10%).
    const double Ms = 1000;
    const string Gold = "MelvorBase:GoldPieces";

    /// <summary>
    /// A tree the Woodcutting page shows: its own requirements to be seen, and those of its category, are met (Mining
    /// Rates' RockRates.Listed), and a tree the player hid is shown only while the page lists hidden trees
    /// (WoodcuttingTree.IsHidden, WoodcuttingActionComponent.ShowHiddenTrees).
    /// </summary>
    public static bool Listed(WoodcuttingTree tree, WoodcuttingActionComponent action) =>
        tree.Recipe.DisplayRequirements?.AreMet != false && tree.Category?.DisplayRequirements?.AreMet != false &&
        (!tree.IsHidden || action.ShowHiddenTrees);

    /// <param name="shown">A picture of the game as the list shows it: null for the grey stand-in (GamePictures.Shown).</param>
    public static TreeRates Read(WoodcuttingTree tree, WoodcuttingEntity woodcutting, Func<Texture2D?, Texture2D?> shown)
    {
        WoodcuttingRecipe recipe = tree.Recipe;
        WoodcuttingActionStats stats = tree.Stats;
        var r = new TreeRates
        {
            Tree = tree, Name = recipe.DisplayName.Text, Category = tree.Category?.DisplayName.Text ?? "",
            // The tree's own picture, the one its card shows; its icon when the game has no picture of it.
            Picture = shown(recipe.Media) ?? shown(recipe.Icon),
            OnPage = ReferenceEquals(woodcutting.Action.CuttingTree, tree),
            Hidden = tree.IsHidden,
        };
        ConditionGroup?[] groups = [tree.Category?.Requirements, recipe.Requirements];
        if (groups.Any(g => g?.AreMet == false))
        {
            r.Locked = true;
            r.Needs = Native.Needs(groups);
            r.Level = Native.LevelNeeded(groups, woodcutting.Experience);
        }

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
        r.HitsPerCut = HitsRule is { } hits && r.HasTime ? ExpectedHits(hits.Reading, r) : null;

        if (recipe.Product?.Item is { } log)
        {
            r.Log = log;
            r.LogName = log.DisplayName.GetPluralText(2);
            r.LogQuantity = Math.Max(0, stats.ProductQuantity.Value);
            r.LogPrice = GoldOf(log);
        }
        // The doubling chance doubles the logs: the mod's reading of the game's words for it, "Your chance to double the
        // output" (DoublingIcon, read by Mining Rates' light check), as Mining Rates doubles its ore.
        double logsEach = r.LogQuantity * (1 + r.Doubling);
        r.LogsPerHour = !r.HasTime || r.Log == null || LogsRule is not { } logs ? null : logs.Reading switch
        {
            LogsWith.EveryHit => r.HitsPerHour * logsEach,
            LogsWith.Cut => r.CutsPerHour * logsEach,
            _ => r.CutsPerHour * r.HitPoints * logsEach,
        };

        // The items a hit and a cut can bring, with the character's bonuses (not the recipe's base lists). The game hands
        // the doubling chance to what adds them (WoodcuttingActionComponent.AddRandomItems takes the item chances and the
        // doubling chance), so it doubles them, as Fishing Rates and Mining Rates count their items of a hit. The items of
        // a hit come once a hit, or for every hit point a hit takes (HitItemsRule); once a hit until that is settled.
        double? rolls = !r.HasTime ? null : HitItemsRule?.Reading == HitItemsWith.HitPoint ? r.CutsPerHour * r.HitPoints : r.HitsPerHour;
        foreach (var node in stats.OnHitItemChances?.Values ?? [])
            if (node?.Value is { Item: { } item } drop && drop.Quantity > 0)
                r.PerHit.Add(Line(item, Fraction(drop.Chance), drop.Quantity, 1 + r.Doubling, rolls));
        foreach (var node in stats.OnCutItemChances?.Values ?? [])
            if (node?.Value is { Item: { } item } drop && drop.Quantity > 0)
                r.PerCut.Add(Line(item, Fraction(drop.Chance), drop.Quantity, 1 + r.Doubling, r.CutsPerHour));

        foreach (ActiveOvergrowthEffect effect in tree.ActiveEffects?.Values ?? Enumerable.Empty<ActiveOvergrowthEffect>())
            if (effect?.Effect is { } kind)
                r.Effects.Add(new Effect(kind.DisplayName.Text, effect.StacksLeft));
        return r;
    }

    static Drop Line(Item item, double chance, int quantity, double times, double? timesPerHour)
    {
        double each = quantity * times;
        double? perHour = timesPerHour * chance * each;
        double? gold = perHour is { } n && GoldOf(item) is { } price ? n * price : null;
        return new Drop(item, item.DisplayName.GetPluralText(2), chance, quantity, each, perHour, gold);
    }

    /// <summary>
    /// The damage of each kind of hit by a reading of the rule, with its chance: ordinary, double, weak spot, and both,
    /// a double and a weak spot being rolled apart (the game's event of a hit has a flag for each,
    /// TreeDamagedEventArgs.DoubleDamage and IsWeakSpot).
    /// </summary>
    public static List<(double Chance, int Damage)> Outcomes(HitRule rule, int hitPoints, double doubleDamage, double weakSpot)
    {
        double share = WoodcuttingActionComponent.MaxHitpointsWeakSpotDamage * hitPoints;
        int part = rule.WeakSpotRounding switch
        {
            Rounding.Down => (int)Math.Floor(share + 1e-9),
            Rounding.Up => (int)Math.Ceiling(share - 1e-9),
            _ => (int)Math.Round(share, MidpointRounding.AwayFromZero),
        };
        int weak = WoodcuttingActionComponent.BaseWeakSpotDamage + part + (rule.WeakSpotAdded ? rule.Damage : 0);
        double d = Fraction(doubleDamage), w = Fraction(weakSpot);
        return
        [
            ((1 - d) * (1 - w), rule.Damage),
            (d * (1 - w), 2 * rule.Damage),
            ((1 - d) * w, weak),
            (d * w, 2 * weak),
        ];
    }

    /// <summary>The hits a cut of this tree takes on average by a reading of the rule; null when it would never be cut.</summary>
    public static double? ExpectedHits(HitRule rule, TreeRates t) =>
        ExpectedHits(t.HitPoints, Outcomes(rule, t.HitPoints, t.DoubleDamageChance, t.WeakSpotChance), t.InstantCutChance);

    /// <summary>
    /// The hits a cut takes on average, from the tree's hit points, the damage of each kind of hit with its chance, and
    /// the chance that a hit cuts the tree at once: the sum over the hit points left of the chance of reaching them, the
    /// arithmetic our review of pull request #61 called sound once every damage rule comes from the game. Null when the
    /// tree would never be cut (no hit takes anything and no hit cuts it at once).
    /// </summary>
    public static double? ExpectedHits(int hitPoints, IReadOnlyList<(double Chance, int Damage)> outcomes, double instant)
    {
        if (hitPoints <= 0) return 1;
        if (hitPoints > 1_000_000) return null;
        double go = 1 - Fraction(instant);
        double still = go * outcomes.Where(o => o.Damage <= 0).Sum(o => o.Chance);
        if (still >= 1 - 1e-12) return null;
        // left[h]: the hits still to come with h hit points left.
        var left = new double[hitPoints + 1];
        for (int h = 1; h <= hitPoints; h++)
        {
            double more = 0;
            foreach (var (chance, damage) in outcomes)
                if (damage > 0) more += chance * left[Math.Max(0, h - damage)];
            left[h] = (1 + go * more) / (1 - still);
        }
        return left[hitPoints];
    }

    static double? GoldOf(Item item) => item.SellsFor is { Currency: { Id: Gold }, Quantity: > 0 } price ? price.Quantity : null;

    static double Chance(ModifierStatNodes? nodes) => nodes == null ? 0 : Fraction(nodes.Total.Value);

    static double Fraction(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
