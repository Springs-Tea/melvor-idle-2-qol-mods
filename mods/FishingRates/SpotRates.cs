using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Fishing;
using GamesByMalcs.Melvor2.Stats.Nodes;
using GamesByMalcs.Melvor2.Utils;
using Godot;

namespace FishingRates;

/// <summary>
/// What one hour at a fishing spot gives, worked out from the game's own numbers for that spot: the stats the
/// game keeps for every spot (ActiveFishingRecipe.Stats, a FishingActionStats), the very ones its spot list
/// shows. Whatever changes those (level, equipment, potions, the Mastery Guild, the selected bait) changes the
/// hour too. What is the mod's own: the middle of the game's catch time range as the time of a catch, and every
/// chance counted as its long-run average, the way Recipe Rates counts them (mods/RecipeRates/Rates.cs). What was
/// measured in the game: a catch takes one bait from the bank, a preserved one too (<see cref="PreservedBaitStays"/>).
/// </summary>
sealed class SpotRates
{
    /// <summary>
    /// One thing a catch can bring. <see cref="Chance"/> is per catch; <see cref="Each"/> the average number a
    /// catch that brings it gives, doubling included where it counts.
    /// </summary>
    public sealed record Catch(Item Item, string Name, double Chance, int Min, int Max, double Each, double PerHour, double? GoldPerHour);

    /// <summary>A bonus of the character that changes the hour in a way the mod does not count.</summary>
    /// <param name="Key">Which of the game's stats it is, the key of its name (BonusNames).</param>
    /// <param name="Nodes">The game's stat, to find the name the Fishing page gives it.</param>
    public sealed record Bonus(string Key, ModifierStatNodes Nodes);

    public const string JunkToGold = "JunkToGP", JunkReroll = "JunkRerollChance", SecondSpecial = "SecondSpecialItemChance";

    public required ActiveFishingRecipe Active { get; init; }
    public required string Name { get; init; }
    public required string Location { get; init; }
    /// <summary>
    /// The pictures of the spot's fish, in the order of its table: what the game's own spot buttons show
    /// (FishingSpotSelectButton.AvailableFishContainer). A spot has no picture of its own that the game shows. A fish
    /// the game has only its grey stand-in for is left out (GamePictures).
    /// </summary>
    public required List<Texture2D> Pictures { get; init; }

    /// <summary>The character cannot fish here yet.</summary>
    public bool Locked { get; private set; }
    /// <summary>The Fishing level a locked spot needs, when a level is what it is missing.</summary>
    public int? Level { get; private set; }
    /// <summary>What a locked spot is missing, in the game's own words.</summary>
    public List<string> Missing { get; } = new();
    /// <summary>
    /// The spot the Fishing page shows: the selected spot of the location the page is on
    /// (FishingActionComponent.CurrentLocation, ActiveFishingLocation.SelectedRecipe), the one its Start button
    /// fishes.
    /// </summary>
    public bool OnPage { get; init; }
    /// <summary>The Fishing page is on the spot's location.</summary>
    public bool LocationOnPage { get; init; }

    /// <summary>What a click on a spot's line opens on the Fishing page.</summary>
    public enum Opens { Nothing, Location, Spot }

    /// <summary>What a click on the line opens, as it was when the spot was read (<see cref="ClickOpens"/>).</summary>
    public Opens Click { get; private set; }

    /// <summary>A click on the line changes what the Fishing page shows.</summary>
    public bool Clickable => Click == Opens.Spot ? !OnPage : Click == Opens.Location && !LocationOnPage;

    /// <summary>The character fishes at the spot's location now (<see cref="FishesAt"/>): a click selects no spot there.</summary>
    public bool FishingHere { get; private set; }
    /// <summary>
    /// He fishes and the game does not say where (no FishingActionComponent.CurrentRecipe): every location counts as his,
    /// and the tooltip names none.
    /// </summary>
    public bool FishingWhereUnknown { get; private set; }

    /// <summary>Whether the character fishes now, and the location of the spot he fishes (null when the game does not say).</summary>
    public readonly record struct Fished(bool Now, FishingLocation? At);

    /// <summary>
    /// Whether the character fishes now, by the widest test the game's members allow (Firemaking Rates' LogRates.IsBurning):
    /// Fishing's action is started (FishingActionComponent.IsStarted), or it is among the game's running actions
    /// (Game.ActionManager.ActiveActions); and the location of the spot it fishes (FishingActionComponent.CurrentRecipe).
    /// Read on every tick for the lines, and again at the moment of a click (FishingRatesMod.OpenOnPage).
    /// </summary>
    public static Fished FishingNow(FishingEntity fishing, Game? game)
    {
        bool now = fishing.Action.IsStarted || game?.ActionManager.ActiveActions.Any(a => ReferenceEquals(a.Entity, fishing)) == true;
        return new(now, now ? fishing.Action.CurrentRecipe?.Location : null);
    }

    /// <summary>
    /// The character fishes at this spot's location now; while he fishes and the game does not say where, at every location,
    /// so that nothing is selected anywhere then.
    /// </summary>
    public static bool FishesAt(ActiveFishingRecipe active, Fished fished) =>
        fished.Now && (fished.At == null || ReferenceEquals(fished.At, active.Location));

    /// <summary>
    /// What a click on a spot's line opens: the spot; only its location while the spot is locked; nothing while its
    /// location is locked, since the player cannot open that one either (the location's own requirements, which the
    /// page's location buttons show as locked). While the character fishes at the spot's location (<paramref name="fished"/>,
    /// read at the moment of the click), only that location too: no spot is selected where he fishes (lesson 56: select
    /// only while the skill is idle). What has been seen in the game (check-2 of the job fishing-bait, its log's lines 175 to
    /// 191, 23 Nov 2026): while he fished at Rocky Shores (Shallow Shores), opening Windy Coastline and selecting Windswept
    /// Shores there, pressing that spot's own button, and opening Glowing Depths each left his fishing as it was ("fishing:
    /// started True, active True, the spot fished MelvorBase:RockyShores" after each). Selecting another spot at the
    /// location he fishes at has never been seen: the game may switch or stop his fishing then, so the click never does it.
    /// </summary>
    public static Opens ClickOpens(ActiveFishingRecipe active, Fished fished) =>
        active.Location.Requirements?.AreMet == false ? Opens.Nothing
        : active.Recipe.Requirements?.AreMet == false ? Opens.Location
        : FishesAt(active, fished) ? Opens.Location
        : Opens.Spot;

    /// <summary>The game's catch time range, in seconds.</summary>
    public double MinSeconds { get; private set; }
    public double MaxSeconds { get; private set; }
    /// <summary>Ours: the middle of the range.</summary>
    public double Seconds => (MinSeconds + MaxSeconds) / 2;
    /// <summary>The game gives a catch time: without one there is nothing to count.</summary>
    public bool HasTime => Seconds > 0.01 && double.IsFinite(Seconds);
    public double CatchesPerHour => HasTime ? 3600 / Seconds : 0;

    public double XpEach { get; private set; }
    public double XpPerHour => XpEach * CatchesPerHour;

    public double FishChance { get; private set; }
    public double JunkChance { get; private set; }
    public double Doubling { get; private set; }
    public List<Catch> Fish { get; } = new();
    public List<Catch> Junk { get; } = new();
    public List<Catch> Special { get; } = new();

    /// <summary>What the catches of an hour sell for, in gold. Null when a priority fish is set: see <see cref="PriorityFish"/>.</summary>
    public double? GoldPerHour { get; private set; }
    /// <summary>The part of <see cref="GoldPerHour"/> that comes from special items, rare ones included.</summary>
    public double SpecialGoldPerHour => Special.Sum(c => c.GoldPerHour ?? 0);
    /// <summary>
    /// The fish the player gave priority to at this spot. Which fish come then is the game's to decide, and the
    /// mod does not know how: the fish and the gold of the hour are left out rather than guessed.
    /// </summary>
    public string? PriorityFish { get; private set; }
    /// <summary>Bonuses of the character that change the hour in a way the mod does not count.</summary>
    public List<Bonus> NotCounted { get; } = new();

    /// <summary>
    /// Whether a bait the game preserves stays in the bank, and what backs it: set here, in this one place. Measured by
    /// the development watch (baitwatch.request, DevBaitWatch.cs) on the maintainer's fishing at Rapid Runs with Mackerel
    /// Bait (check-4 of the job fishing-bait, 24 Nov 2026, issue #64): 29 catches, each with the game's "bait used" event
    /// (FishingBaitComponent.BaitConsumed) and 3 of them also with its "bait preserved" one (BaitPreserved); the bank's
    /// Mackerel Bait went from 2,521 to 2,492, down by one on every catch, the 3 preserved ones included ("PRESERVED BAIT
    /// IS USED UP ANYWAY"). So every catch takes one bait from the bank, and the game's chance to preserve it
    /// (FishingBaitStats.PreservationChance) saves none in this game version. A game update may change that: the same
    /// watch tells it ("PRESERVED BAIT STAYS IN THE BANK"), and then this, <see cref="BaitSeen"/> and the guides change.
    /// </summary>
    public static readonly bool PreservedBaitStays = false;

    /// <summary>What the watch counted, as the Bait column's tip says it after "Measured: " (Woodcutting Rates' rules).</summary>
    public const string BaitSeen = "the bank lost one bait on 29 of 29 catches, the 3 preserved ones too";

    /// <summary>The bait selected on the Fishing page, or null.</summary>
    public string? Bait { get; private set; }
    /// <summary>
    /// The game's chance to preserve the bait (FishingBaitStats.PreservationChance): taken off the bait of an hour only
    /// while <see cref="PreservedBaitStays"/>; otherwise only named in the tooltip, as not counted.
    /// </summary>
    public double BaitPreservation { get; private set; }
    /// <summary>The bait an hour takes from the bank.</summary>
    public double? BaitPerHour { get; private set; }
    public long BaitInBank { get; private set; }

    // The game keeps intervals in milliseconds (Recipe Rates' Rates.Ms; the game's own catch times matched these to
    // the hundredth in the first game check) and chances as fractions (0.1 is 10%).
    const double Ms = 1000;
    const string Gold = "MelvorBase:GoldPieces";

    /// <summary>A spot the game shows: its own requirements to be seen, and those of its location, are met.</summary>
    public static bool Revealed(ActiveFishingRecipe active) =>
        active.Recipe.DisplayRequirements?.AreMet != false && active.Location.DisplayRequirements?.AreMet != false;

    /// <summary>The spot the Fishing page shows, or null.</summary>
    public static ActiveFishingRecipe? ShownOnPage(FishingEntity fishing) => fishing.Action.CurrentLocation?.SelectedRecipe;

    /// <param name="shown">A picture of the game as the list shows it: null for the grey stand-in (GamePictures.Shown).</param>
    /// <param name="fished">Whether the character fishes, and where (<see cref="FishingNow"/>), read once for every line.</param>
    public static SpotRates Read(ActiveFishingRecipe active, FishingEntity fishing, BankEntity? bank, ActiveFishingRecipe? onPage,
        Func<Texture2D?, Texture2D?> shown, Fished fished)
    {
        FishingRecipe recipe = active.Recipe;
        FishingLocation location = active.Location;
        FishingActionStats stats = active.Stats;
        var table = recipe.RandomProducts;
        var r = new SpotRates
        {
            Active = active, Name = recipe.DisplayName.Text, Location = location.DisplayName.Text,
            Pictures = (table?.Items ?? Enumerable.Empty<FishingProduct>()).Select(p => shown(p.Item?.Icon)).OfType<Texture2D>().ToList(),
            OnPage = ReferenceEquals(onPage, active),
            LocationOnPage = ReferenceEquals(fishing.Action.CurrentLocation?.Location, location),
        };
        r.ReadLock(recipe.Requirements, location.Requirements, fishing.Experience);
        r.FishingHere = FishesAt(active, fished);
        r.FishingWhereUnknown = fished.Now && fished.At == null;
        r.Click = ClickOpens(active, fished);

        var (min, max) = stats.Interval.Value;
        r.MinSeconds = min / Ms;
        r.MaxSeconds = max / Ms;
        // Every catch, fish or junk, is counted with the XP the spot's XP tag shows (FishingActionStats.Experience).
        r.XpEach = stats.Experience.XpToAdd.Value;
        r.FishChance = Fraction(stats.FishChance.Value);
        r.JunkChance = Fraction(stats.JunkChance.Value);
        r.Doubling = Chance(stats.DoublingChance);
        double perHour = r.CatchesPerHour;

        if (active.PriorityProduct is { } priority && location.PriorityRequirements?.AreMet != false)
            r.PriorityFish = priority.DisplayName.GetPluralText(2);

        // A fish comes from the spot's weighted table, by the game's own chance for each (GetItemChance), as Item
        // Sources reads every weighted table (mods/Shared/ItemIndex.cs, AddTable); doubling doubles it.
        if (r.PriorityFish == null)
        {
            foreach (FishingProduct product in table?.Items ?? Enumerable.Empty<FishingProduct>())
            {
                if (product.Item is not { } item) continue;
                double chance = r.FishChance * Fraction(table!.GetItemChance(product));
                r.Fish.Add(Line(item, chance, product.MinQuantity, product.MaxQuantity, 1 + r.Doubling, perHour));
            }
        }
        if (recipe.JunkItem is { } junk && r.JunkChance > 0)
            r.Junk.Add(Line(junk, r.JunkChance, 1, 1, 1, perHour));
        // The special items come on top of a catch: the Fishing page says "Each Item is rolled individually per
        // catch attempt". They are rolled with the doubling chance too (the game passes it to RollForSpecialItem).
        foreach (var node in stats.SpecialProductChances?.Values ?? [])
            if (node.Value is { Item: { } item } special && special.Quantity > 0)
                r.Special.Add(Line(item, Fraction(special.Chance), special.Quantity, special.Quantity, 1 + r.Doubling, perHour));

        r.GoldPerHour = r.PriorityFish != null ? null : r.Fish.Concat(r.Junk).Concat(r.Special).Sum(c => c.GoldPerHour ?? 0);

        // Bonuses that change what a catch brings in ways the game keeps to itself; the tooltip names them the way
        // the Fishing page's list of bonuses does (BonusNames).
        foreach (var (key, nodes) in new[] { (JunkToGold, stats.JunkToGP), (JunkReroll, stats.JunkRerollChance), (SecondSpecial, stats.SecondSpecialItemChance) })
            if (nodes != null && Chance(nodes) > 0) r.NotCounted.Add(new Bonus(key, nodes));

        // One bait from the bank for every catch, a preserved one too, as measured (PreservedBaitStays); the bait is the
        // skill's, the same at every spot.
        FishingBaitComponent baits = fishing.BaitComponent;
        if (baits.SelectedBait is { } bait)
        {
            r.Bait = bait.DisplayName.GetPluralText(2);
            r.BaitPreservation = Chance(baits.Stats?.PreservationChance);
            r.BaitPerHour = perHour * (PreservedBaitStays ? 1 - r.BaitPreservation : 1);
            r.BaitInBank = bank?.Inventory.GetQuantity(bait) ?? 0;
        }
        return r;
    }

    static Catch Line(Item item, double chance, int min, int max, double times, double catchesPerHour)
    {
        double each = (min + Math.Max(min, max)) / 2.0 * times;
        double perHour = catchesPerHour * chance * each;
        double? gold = item.SellsFor is { Currency: { Id: Gold }, Quantity: > 0 } price ? perHour * price.Quantity : null;
        return new Catch(item, item.DisplayName.GetPluralText(2), chance, min, Math.Max(min, max), each, perHour, gold);
    }

    // Locked while the spot's own requirements or those of its location are not met. The words are the game's
    // (Condition.GetDescription), as Item Sources writes what a recipe is missing (MakeRecipes.Words).
    void ReadLock(ConditionGroup? own, ConditionGroup? location, ExperienceComponent fishing)
    {
        foreach (ConditionGroup group in new[] { own, location }.OfType<ConditionGroup>().Distinct())
        {
            if (group.AreMet) continue;
            Locked = true;
            foreach (Condition condition in group.Conditions ?? Enumerable.Empty<Condition>())
            {
                if (condition.IsMet || condition.IsOr) continue;
                if (condition is LevelCondition level && ReferenceEquals(level.Component, fishing))
                    Level = Math.Max(Level ?? 0, level.Level);
                string words = StringUtils.StripBBCode(condition.GetDescription(false, false)).Trim();
                if (words.Length > 0 && !Missing.Contains(words)) Missing.Add(words);
            }
        }
    }

    static double Chance(ModifierStatNodes? nodes) => nodes == null ? 0 : Fraction(nodes.Total.Value);

    static double Fraction(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
