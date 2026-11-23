using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Hunting;
using GamesByMalcs.Melvor2.Stats.Nodes;
using Godot;

namespace HuntingRates;

/// <summary>How many surveys a hunt takes before its tracks are found (a reading of <see cref="ChainRule"/>).</summary>
enum SurveysPerHunt
{
    /// <summary>One survey finds the tracks.</summary>
    One,
    /// <summary>Every survey finds them at the location's chance (HuntingLocation.BaseInsightsChance, by its name only).</summary>
    InsightsChance,
}

/// <summary>What a tracking step that does not succeed costs (a reading of <see cref="ChainRule"/>).</summary>
enum FailedStep
{
    /// <summary>The step is tried again, one more tracking interval.</summary>
    Repeats,
    /// <summary>The hunt ends there, and the next one starts with surveying.</summary>
    EndsHunt,
}

/// <summary>What the chance to skip a tracking step does (a reading of <see cref="ChainRule"/>).</summary>
enum SkippedStep
{
    /// <summary>A skipped step is done at once, without its interval.</summary>
    DoneAtOnce,
    /// <summary>It changes nothing in the time.</summary>
    NotCounted,
}

/// <summary>What a trap that does not catch the animal leads to (a reading of <see cref="ChainRule"/>).</summary>
enum FailedTrap
{
    /// <summary>The trap is tried again at the chance to reattempt; otherwise the animal escapes and a new hunt starts.</summary>
    ReattemptElseEscape,
    /// <summary>The animal escapes and a new hunt starts, whatever the chance to reattempt.</summary>
    Escape,
}

/// <summary>
/// How the links of a hunt join into a catch: the parts of the game's rule that its numbers leave open (the brief of
/// issue #72). Every number of each link is the game's (the animal's HuntingActionStats); how they join is not in any
/// of them. A hunt is surveying until the tracks are found, then tracking for the animal's number of steps, then
/// trapping until it is caught or escapes; a catch or an escape starts the next hunt with surveying.
/// </summary>
sealed record ChainRule(SurveysPerHunt Surveys, FailedStep Step, SkippedStep Skip, FailedTrap Trap);

/// <summary>When a trap is used up (a reading of <see cref="UseRule"/>).</summary>
enum TrapUse { EveryAttempt, FailedAttempt, Catch, Escape, Never }

/// <summary>When bait is used up while it is switched on for the animal (a reading of <see cref="UseRule"/>).</summary>
enum BaitUse { Hunt, Catch, TrapAttempt }

/// <summary>
/// What the traps and the bait of an hour are, a reading of what the game's names leave open: when each is used up,
/// and whether the preservation chance (HuntingActionStats.PreservationChance) keeps it.
/// </summary>
sealed record UseRule(TrapUse Trap, bool TrapKept, BaitUse Bait, bool BaitKept);

/// <summary>What backs a part of the rule the hour rests on.</summary>
enum RuleSource
{
    /// <summary>The Hunting page's own words state it; the column's tip and the README quote them.</summary>
    GameWords,
    /// <summary>Counted on a character's own hunting by the development watch (huntingwatch.request, DevWatch.cs).</summary>
    Measured,
}

/// <summary>
/// A part of the game's rule, settled: the reading, what backs it, the game's words that state it ("" for none, one
/// quote a line), and for a measured part what was counted ("" for none). Woodcutting Rates' Settled
/// (mods/WoodcuttingRates/TreeRates.cs).
/// </summary>
sealed record Settled<T>(T Reading, RuleSource Source, string Quote, string Seen = "");

/// <summary>
/// What one hour at an animal gives, worked out from the game's own numbers for that animal: the stats the game keeps
/// for every animal (ActiveHuntingAnimal.Stats, a HuntingActionStats), the ones the Hunting page's panel shows for the
/// animal selected there. Whatever changes those (level, equipment, potions, the animal's journal level, its trap, its
/// bait, hunting without a trap) changes the hour too. Built the way Woodcutting Rates builds a tree's hour
/// (mods/WoodcuttingRates/TreeRates.cs).
///
/// A catch is a chain of links: surveying (SurveyInterval), tracking (TrackingSteps steps of TrackingInterval each, at
/// TrackingSuccessChance, with SkipTrackingStepChance), trapping (TrappingInterval at TrappingSuccessChance, with
/// TrappingReattemptChance). Every number of a link is the game's; how the links join (<see cref="ChainRule"/>), when
/// traps and bait are used up (<see cref="UseRule"/>), and what the XP of a catch is within the range the page writes,
/// rest on rules that are not settled yet: what rests on them is null and the window shows "-", never a formula of the
/// mod's own (the review of pull request #61). The light check reads the page's own words for them, and the silent
/// watch (huntingwatch.request) counts them on a character's own hunting.
/// </summary>
sealed class AnimalRates
{
    /// <summary>
    /// How the links of a hunt join, and what backs it: set here, in this one place. Not settled: no member of the game
    /// gives it, and nobody has read the page's words or watched a hunt yet. The light check logs the page's words
    /// ("Dev: words") and the time of a catch by every reading (<see cref="Readings"/>); the watch measures each link.
    /// </summary>
    static readonly Settled<ChainRule>? KnownChain = null;

    /// <summary>When traps and bait are used up, and what backs it: set here, in this one place. Not settled.</summary>
    static readonly Settled<UseRule>? KnownUse = null;

    /// <summary>How the links of a hunt join, or null while nothing has settled it (<see cref="KnownChain"/>).</summary>
    public static Settled<ChainRule>? ChainRule => KnownChain;

    /// <summary>When traps and bait are used up, or null while nothing has settled it (<see cref="KnownUse"/>).</summary>
    public static Settled<UseRule>? UseRule => KnownUse;

    /// <summary>Every reading of the chain the brief leaves open, for the light check and the watch to hold against.</summary>
    public static readonly ChainRule[] Readings =
        (from surveys in Enum.GetValues<SurveysPerHunt>()
         from step in Enum.GetValues<FailedStep>()
         from skip in Enum.GetValues<SkippedStep>()
         from trap in Enum.GetValues<FailedTrap>()
         select new ChainRule(surveys, step, skip, trap)).ToArray();

    /// <summary>
    /// One thing a catch or a survey can bring: <see cref="Chance"/> per catch (per survey for a survey's), how many it
    /// gives when it comes (<see cref="Each"/>, doubling included where it is counted), and an hour of it where the
    /// catches or surveys of an hour are counted.
    /// </summary>
    public sealed record Drop(Item Item, string Name, double Chance, int Quantity, double Each, bool Collectable, bool Found, double? PerHour, double? GoldPerHour);

    /// <summary>A rare species: its share of the rare ones by the table's weights, and the extra products it brings.</summary>
    public sealed record Species(string Name, double Share, string Locked, List<Drop> Extras);

    public required ActiveHuntingAnimal Active { get; init; }
    public HuntingAnimal Animal => Active.Animal;
    /// <summary>The animal's name as the list shows it: the game's, or the page's own words for an animal not discovered.</summary>
    public required string Name { get; init; }
    public required string Location { get; init; }
    /// <summary>The picture the list shows: the game's own, the page's for a spider while the game's spider setting is on.</summary>
    public Texture2D? Picture { get; init; }
    /// <summary>How the page draws the picture (its colour over it), for an animal not discovered yet.</summary>
    public Color PictureTint { get; init; } = Colors.White;
    /// <summary>The animal's journal entry says it has been discovered (HuntingAnimalJournalEntry.IsDiscovered).</summary>
    public bool Discovered { get; init; } = true;

    /// <summary>The character cannot hunt this animal yet.</summary>
    public bool Locked { get; private set; }
    /// <summary>The Hunting level a locked animal needs, when a level is what it is missing.</summary>
    public int? Level { get; private set; }
    /// <summary>What a locked animal is missing, in the game's own words, each once (Native.Needs).</summary>
    public string Needs { get; private set; } = "";
    /// <summary>The animal the Hunting page's panel shows (HuntingActionComponent.SelectedAnimal): the lit line.</summary>
    public bool OnPage { get; init; }
    /// <summary>The Hunting page shows the animal's location (HuntingActionComponent.SelectedLocation).</summary>
    public bool LocationOnPage { get; init; }
    /// <summary>The animal being hunted now (HuntingActionComponent.CurrentAnimal, with Hunting started).</summary>
    public bool Hunted { get; init; }

    /// <summary>What a click on an animal's line opens on the Hunting page.</summary>
    public enum Opens { Nothing, Location, Animal }

    /// <summary>What a click on the line opens, as it was when the animal was read (<see cref="ClickOpens"/>).</summary>
    public Opens Click { get; private set; }
    /// <summary>A click on the line changes what the Hunting page shows (HuntingRatesMod.ClickChanges).</summary>
    public bool Clickable { get; set; }
    /// <summary>How the animal's card in the page's list stands against the window, when the page shows its location.</summary>
    public CardSight? Sight { get; set; }
    /// <summary>The page's panel of the selected animal lies under the window, where a click's animal would show.</summary>
    public bool PanelCovered { get; set; }

    /// <summary>
    /// What a click on an animal's line opens: the animal; only its location while the animal is locked; nothing while
    /// its location is locked, which the player cannot open either (Fishing Rates' SpotRates.ClickOpens).
    /// </summary>
    public static Opens ClickOpens(ActiveHuntingAnimal active) =>
        active.Location?.Location?.Requirements?.AreMet == false ? Opens.Nothing
        : active.Animal.Requirements?.AreMet == false ? Opens.Location
        : Opens.Animal;

    // ---- the links of a catch, the game's numbers ---------------------------------------------------------------------

    /// <summary>One survey, in seconds (HuntingActionStats.SurveyInterval, kept in milliseconds).</summary>
    public double SurveySeconds { get; private set; }
    /// <summary>The location's chance by the name of insights (HuntingLocation.BaseInsightsChance): what it is, is open.</summary>
    public double Insights { get; private set; }
    /// <summary>The location's base XP of a survey (HuntingLocation.BaseSurveyingExperience), without bonuses.</summary>
    public double SurveyXpBase { get; private set; }
    /// <summary>The steps of tracking, the game's range (HuntingActionStats.TrackingSteps).</summary>
    public int StepsMin { get; private set; }
    public int StepsMax { get; private set; }
    /// <summary>One tracking step, in seconds, the game's range (HuntingActionStats.TrackingInterval).</summary>
    public double StepMinSeconds { get; private set; }
    public double StepMaxSeconds { get; private set; }
    /// <summary>Ours: the middle of the range.</summary>
    public double StepSeconds => (StepMinSeconds + StepMaxSeconds) / 2;
    public double TrackChance { get; private set; }
    public double SkipChance { get; private set; }
    /// <summary>One try of the trap, in seconds, the game's range (HuntingActionStats.TrappingInterval).</summary>
    public double TrapMinSeconds { get; private set; }
    public double TrapMaxSeconds { get; private set; }
    /// <summary>Ours: the middle of the range.</summary>
    public double TrapSeconds => (TrapMinSeconds + TrapMaxSeconds) / 2;
    public double TrapChance { get; private set; }
    public double ReattemptChance { get; private set; }
    public double RareChance { get; private set; }
    public double Doubling { get; private set; }
    public double Preservation { get; private set; }

    /// <summary>The game gives a time for every link: without them there is nothing to count.</summary>
    public bool HasTime => SurveySeconds > 0.01 && StepMaxSeconds > 0.01 && TrapMaxSeconds > 0.01 &&
        double.IsFinite(SurveySeconds + StepMinSeconds + StepMaxSeconds + TrapMinSeconds + TrapMaxSeconds) && StepsMax >= Math.Max(0, StepsMin);

    // ---- what a catch gives ---------------------------------------------------------------------------------------------

    /// <summary>The XP the game adds for the animal before a catch's own share (HuntingActionStats.Experience.XpToAdd).</summary>
    public double XpEach { get; private set; }
    /// <summary>
    /// The range the Hunting page writes the XP of a catch in, as the multipliers its XP view is bound with
    /// (ExperienceStatsXPView), read from the page (HuntingRatesMod.XpRange); null until the page has shown them.
    /// </summary>
    public (double Low, double High)? XpRange { get; init; }
    /// <summary>The XP of a catch, ours: the middle of the page's range; null until the range is read.</summary>
    public double? XpPerCatch => XpRange is { } r ? XpEach * (r.Low + r.High) / 2 : null;
    /// <summary>The animal's journal XP for tracking and for trapping, the game's base values (no bonuses known).</summary>
    public uint JournalTrackingXp { get; private set; }
    public uint JournalTrappingXp { get; private set; }
    public int? JournalLevel { get; private set; }

    /// <summary>What a catch brings (HuntingActionStats.Products), with your bonuses.</summary>
    public List<Drop> Products { get; } = new();
    /// <summary>What a survey can bring (HuntingActionStats.SurveyingProducts), with your bonuses.</summary>
    public List<Drop> SurveyFinds { get; } = new();
    /// <summary>The rare species a catch can be (HuntingAnimal.RareSpecies), by the table's weights.</summary>
    public List<Species> Rare { get; } = new();

    // ---- what it uses ---------------------------------------------------------------------------------------------------

    public string? Tool { get; private set; }
    public string? TrapType { get; private set; }
    public Item? Trap { get; private set; }
    public long TrapsInBank { get; private set; }
    /// <summary>The player hunts this animal without a trap (ActiveHuntingAnimal.TraplessEnabled).</summary>
    public bool Trapless { get; private set; }
    public Item? Bait { get; private set; }
    /// <summary>Bait is switched on for this animal (ActiveHuntingAnimal.UseBait).</summary>
    public bool UseBait { get; private set; }
    public long BaitInBank { get; private set; }

    // ---- the hour, by the settled rules -------------------------------------------------------------------------------

    /// <summary>The time of a catch on average by the settled chain; null while it is not settled.</summary>
    public double? SecondsPerCatch { get; private set; }
    public double? CatchesPerHour => SecondsPerCatch is { } s && s > 0 ? 3600 / s : null;
    /// <summary>Surveys a catch takes on average by the settled chain, escaped hunts included.</summary>
    public double? SurveysPerCatch { get; private set; }
    public double? XpPerHour => CatchesPerHour is { } c && XpPerCatch is { } xp ? c * xp : null;
    /// <summary>What the products and rare extras of a catch sell for in gold (collectables apart); null when one does not count.</summary>
    public double CatchGold { get; private set; }
    public double SurveyGold { get; private set; }
    public double? GoldPerHour => CatchesPerHour is { } c ? c * CatchGold + c * (SurveysPerCatch ?? 0) * SurveyGold : null;
    public double? TrapsPerHour { get; private set; }
    public double? BaitPerHour { get; private set; }

    // The game keeps intervals in milliseconds (Fishing Rates' catch times matched the game's to the hundredth this way,
    // Woodcutting Rates' too) and chances as fractions (0.1 is 10%). The light check holds both against the page's own.
    const double Ms = 1000;
    const string Gold = "MelvorBase:GoldPieces";

    /// <summary>
    /// An animal the Hunting page can show: its own requirements to be seen and its location's are met, and the
    /// location is shown at all (a location that waits for an animal to unlock shows none: RecipeCategory
    /// .OnlyShowWhenRecipesAvailable). Fishing Rates' SpotRates.Revealed; the light check holds it against the page's
    /// location buttons ("Dev: hidden").
    /// </summary>
    public static bool Listed(ActiveHuntingAnimal active) =>
        active.Animal.DisplayRequirements?.AreMet != false && LocationListed(active.Location);

    public static bool LocationListed(ActiveHuntingLocation? location) =>
        location?.Location is { } l && l.DisplayRequirements?.AreMet != false && (!l.OnlyShowWhenRecipesAvailable || location.UnlockedRecipeCount > 0);

    /// <param name="look">The name, picture and its colour the list shows (HuntingRatesMod.Look).</param>
    /// <param name="hidden">The game's word for something not found yet, for a rare species never trapped and a collectable never found.</param>
    public static AnimalRates Read(ActiveHuntingAnimal active, HuntingEntity hunting, (string Name, Texture2D? Picture, Color Tint) look,
        (double, double)? xpRange, Func<Item, long> inBank, string hidden)
    {
        HuntingAnimal animal = active.Animal;
        HuntingActionStats stats = active.Stats;
        HuntingActionComponent action = hunting.Action;
        HuntingLocation? location = active.Location?.Location;
        var r = new AnimalRates
        {
            Active = active, Name = look.Name, Location = location?.DisplayName.Text ?? "",
            Picture = look.Picture, PictureTint = look.Tint,
            Discovered = active.JournalEntry?.IsDiscovered != false,
            OnPage = ReferenceEquals(action.SelectedAnimal, active),
            LocationOnPage = ReferenceEquals(action.SelectedLocation, active.Location),
            Hunted = action.IsStarted && ReferenceEquals(action.CurrentAnimal, active),
            XpRange = xpRange,
        };
        ConditionGroup?[] groups = [location?.Requirements, animal.Requirements];
        if (groups.Any(g => g?.AreMet == false))
        {
            r.Locked = true;
            r.Needs = Native.Needs(groups);
            r.Level = Native.LevelNeeded(groups, hunting.Experience);
        }
        r.Click = ClickOpens(active);

        r.SurveySeconds = stats.SurveyInterval.Value / Ms;
        r.Insights = Fraction(location?.BaseInsightsChance ?? 0);
        r.SurveyXpBase = location?.BaseSurveyingExperience ?? 0;
        var (stepsMin, stepsMax) = stats.TrackingSteps.Value;
        r.StepsMin = stepsMin;
        r.StepsMax = Math.Max(stepsMin, stepsMax);
        var (stepMin, stepMax) = stats.TrackingInterval.Value;
        r.StepMinSeconds = stepMin / Ms;
        r.StepMaxSeconds = Math.Max(stepMin, stepMax) / Ms;
        r.TrackChance = Fraction(stats.TrackingSuccessChance.Value);
        r.SkipChance = Chance(stats.SkipTrackingStepChance);
        var (trapMin, trapMax) = stats.TrappingInterval.Value;
        r.TrapMinSeconds = trapMin / Ms;
        r.TrapMaxSeconds = Math.Max(trapMin, trapMax) / Ms;
        r.TrapChance = Fraction(stats.TrappingSuccessChance.Value);
        r.ReattemptChance = Chance(stats.TrappingReattemptChance);
        r.RareChance = Fraction(stats.RareSpeciesChance.Value);
        r.Doubling = Chance(stats.DoublingChance);
        r.Preservation = Chance(stats.PreservationChance);

        r.XpEach = stats.Experience.XpToAdd.Value;
        r.JournalTrackingXp = animal.BaseJournalTrackingExperience;
        r.JournalTrappingXp = animal.BaseJournalTrappingExperience;
        r.JournalLevel = active.JournalEntry?.Experience?.Level;

        r.Tool = animal.Tool?.DisplayName.Text;
        r.TrapType = animal.TrapType?.DisplayName.Text;
        r.Trapless = active.TraplessEnabled;
        r.Trap = active.SelectedTrap;
        r.TrapsInBank = r.Trap != null ? inBank(r.Trap) : 0;
        r.Bait = animal.PreferredBait;
        r.UseBait = active.UseBait && r.Bait != null;
        r.BaitInBank = r.Bait != null ? inBank(r.Bait) : 0;

        // The chain by the settled rule; null while it is not settled.
        if (ChainRule is { } chain && r.HasTime && Chain(chain.Reading, r) is { } time)
        {
            r.SecondsPerCatch = time.Seconds;
            r.SurveysPerCatch = time.Surveys;
            if (UseRule is { } use) (r.TrapsPerHour, r.BaitPerHour) = Uses(use.Reading, chain.Reading, r) is { } u ? (u.Traps, u.Bait) : (null, null);
        }
        double? catches = r.CatchesPerHour;

        // A catch's products, with the character's bonuses (not the animal's base list). The doubling chance doubles them,
        // as Fishing Rates and Woodcutting Rates count theirs: the window's reading of the game's words for it, "Your
        // chance to double the output" (DoublingIcon, read by Mining Rates' light check).
        var found = hunting.Journal?.CollectablesFound;
        foreach (var node in stats.Products?.Values ?? [])
            if (node?.Value is { Item: { } item } product && product.Quantity > 0)
                r.Products.Add(Line(item, Fraction(product.Chance), product.Quantity, 1 + r.Doubling, false, false, catches));
        r.SurveyFinds.AddRange(SurveyDrops(stats, r, catches));
        // A rare species comes with the game's chance, which one by the table's weights (Item Sources reads a weighted table
        // so: mods/Shared/ItemIndex.cs, AddTable), with its extra products at their own chances. A collectable found once
        // is named as found and its gold is never counted: the journal keeps it (HuntingJournalComponent.CollectablesFound).
        // As the journal shows them, a species never trapped (its species list follows HuntingJournalComponent
        // .SpeciesTrapped) and a collectable never found (its list of HiddenItemIcon follows what was found) are not named:
        // the game's own word for something not found yet stands for them (AnimalSpeciesView.HiddenText).
        var trapped = hunting.Journal?.SpeciesTrapped;
        if (animal.RareSpecies is { } table)
            foreach (WeightedHuntingAnimalSpecies weighted in table.Items ?? Enumerable.Empty<WeightedHuntingAnimalSpecies>())
            {
                if (weighted?.Species is not { } species) continue;
                double share = Fraction(table.GetItemChance(weighted));
                var extras = new List<Drop>();
                foreach (HuntingAnimalSpeciesProduct extra in species.ExtraProducts ?? Enumerable.Empty<HuntingAnimalSpeciesProduct>())
                    if (extra?.Item is { } item && extra.Quantity > 0)
                    {
                        bool got = extra.IsCollectable && found != null && found.TryGetValue(item, out bool f) && f;
                        Drop drop = Line(item, r.RareChance * share * Fraction(extra.Chance), extra.Quantity, 1, extra.IsCollectable, got, catches);
                        extras.Add(extra.IsCollectable && !got ? drop with { Name = hidden } : drop);
                    }
                string locked = species.Requirements?.AreMet == false ? Native.Needs([species.Requirements]) : "";
                bool seen = trapped == null || trapped.GetValue(species) > 0;
                r.Rare.Add(new Species(seen ? species.DisplayName.Text : hidden, share, locked, extras));
            }
        r.CatchGold = r.Products.Concat(r.Rare.SelectMany(s => s.Extras)).Where(d => !d.Collectable).Sum(d => GoldOf(d.Item) is { } price ? d.Chance * d.Each * price : 0);
        r.SurveyGold = r.SurveyFinds.Sum(d => GoldOf(d.Item) is { } price ? d.Chance * d.Each * price : 0);
        return r;
    }

    // What a survey can bring, an hour of it by the surveys of an hour where those are counted. Not doubled: the window
    // does not know whether the doubling chance reaches a survey (said in the tooltip).
    static IEnumerable<Drop> SurveyDrops(HuntingActionStats stats, AnimalRates r, double? catches)
    {
        double? surveys = catches * r.SurveysPerCatch;
        foreach (var node in stats.SurveyingProducts?.Values ?? [])
            if (node?.Value is { Item: { } item } find && find.Quantity > 0)
                yield return Line(item, Fraction(find.Chance), find.Quantity, 1, false, false, surveys);
    }

    static Drop Line(Item item, double chance, int quantity, double times, bool collectable, bool found, double? timesPerHour)
    {
        double each = quantity * times;
        double? perHour = collectable ? null : timesPerHour * chance * each;
        double? gold = perHour is { } n && GoldOf(item) is { } price ? n * price : null;
        return new Drop(item, item.DisplayName.GetPluralText(2), chance, quantity, each, collectable, found, perHour, gold);
    }

    /// <summary>
    /// The time of a catch on average by a reading of the chain, and the surveys a catch takes, from the game's numbers
    /// of each link: the middle of each of the game's ranges of time, every number of tracking steps in its range as
    /// likely, and every chance as its long-run average. Null when no catch would ever come by that reading.
    /// </summary>
    public static (double Seconds, double Surveys)? Chain(ChainRule rule, AnimalRates a)
    {
        if (!a.HasTime) return null;
        double surveys = rule.Surveys == SurveysPerHunt.One ? 1 : a.Insights > 0 ? 1 / a.Insights : double.NaN;
        if (!double.IsFinite(surveys)) return null;
        double p = a.TrackChance, s = rule.Skip == SkippedStep.DoneAtOnce ? a.SkipChance : 0, t = a.StepSeconds;
        double q = a.TrapChance, again = rule.Trap == FailedTrap.ReattemptElseEscape ? a.ReattemptChance : 0;
        // Trapping, once tracking is done: the tries it takes and the chance that it ends in a catch.
        double fails = (1 - q) * again;
        if (fails >= 1 - 1e-12) return null;
        double tries = 1 / (1 - fails), caught = q / (1 - fails);
        // Tracking, over every number of steps in the game's range: its time, and the chance that it reaches the trap.
        double tracking = 0, reached = 0;
        int counts = 0;
        for (int steps = Math.Max(0, a.StepsMin); steps <= a.StepsMax; steps++, counts++)
        {
            if (rule.Step == FailedStep.Repeats)
            {
                // Each step skipped at once on s, else tried until it succeeds; a step never succeeds without a chance.
                if (steps > 0 && s < 1 && p <= 0) return null;
                tracking += steps == 0 || s >= 1 ? 0 : steps * (1 - s) * t / p;
                reached += 1;
            }
            else
            {
                double pass = s + (1 - s) * p, reach = 1, time = 0;
                for (int k = 0; k < steps; k++)
                {
                    time += reach * (1 - s) * t;
                    reach *= pass;
                }
                tracking += time;
                reached += reach;
            }
            if (counts > 10_000) return null;
        }
        if (counts == 0) return null;
        tracking /= counts;
        reached /= counts;
        double hunt = surveys * a.SurveySeconds + tracking + reached * tries * a.TrapSeconds;
        double catchChance = reached * caught;
        if (catchChance <= 1e-12) return null;
        return (hunt / catchChance, surveys / catchChance);
    }

    /// <summary>
    /// Traps and bait of an hour by a reading of when each is used up, under a reading of the chain; the traps are none
    /// when the animal is hunted without a trap, the bait none when it is not switched on.
    /// </summary>
    public static (double? Traps, double? Bait)? Uses(UseRule use, ChainRule chain, AnimalRates a)
    {
        if (Chain(chain, a) is not { } time || time.Seconds <= 0) return null;
        double hunts = HuntsPerCatch(chain, a) ?? double.NaN;
        if (!double.IsFinite(hunts)) return null;
        double catches = 3600 / time.Seconds, tries = TriesPerCatch(chain, a) ?? double.NaN;
        // The hunts that reach the trap and end in an escape: all of them but the one that ends in the catch.
        double escapes = (TrapsReachedPerCatch(chain, a) ?? double.NaN) - 1;
        double traps = a.Trapless ? 0 : use.Trap switch
        {
            TrapUse.EveryAttempt => tries,
            TrapUse.FailedAttempt => tries - 1,
            TrapUse.Catch => 1,
            TrapUse.Escape => escapes,
            _ => 0,
        } * (use.TrapKept ? 1 - a.Preservation : 1);
        double bait = !a.UseBait ? 0 : use.Bait switch
        {
            BaitUse.Hunt => hunts,
            BaitUse.Catch => 1,
            _ => tries,
        } * (use.BaitKept ? 1 - a.Preservation : 1);
        return (double.IsFinite(traps) ? traps * catches : null, double.IsFinite(bait) ? bait * catches : null);
    }

    /// <summary>The hunts a catch takes on average by a reading of the chain (one, and the escaped ones).</summary>
    public static double? HuntsPerCatch(ChainRule rule, AnimalRates a) => Chain(rule, a) is { } time
        ? time.Surveys / (rule.Surveys == SurveysPerHunt.One ? 1 : 1 / a.Insights) : null;

    /// <summary>The hunts a catch takes that reach the trap, on average by a reading of the chain (the catch's included).</summary>
    public static double? TrapsReachedPerCatch(ChainRule rule, AnimalRates a)
    {
        double q = a.TrapChance, again = rule.Trap == FailedTrap.ReattemptElseEscape ? a.ReattemptChance : 0;
        double fails = (1 - q) * again;
        if (fails >= 1 - 1e-12) return null;
        double caught = q / (1 - fails);
        return caught > 1e-12 ? 1 / caught : null;
    }

    /// <summary>The tries of the trap a catch takes on average by a reading of the chain.</summary>
    public static double? TriesPerCatch(ChainRule rule, AnimalRates a)
    {
        double q = a.TrapChance, again = rule.Trap == FailedTrap.ReattemptElseEscape ? a.ReattemptChance : 0;
        double fails = (1 - q) * again;
        if (fails >= 1 - 1e-12 || TrapsReachedPerCatch(rule, a) is not { } reached) return null;
        return reached / (1 - fails);
    }

    static double? GoldOf(Item item) => item.SellsFor is { Currency: { Id: Gold }, Quantity: > 0 } price ? price.Quantity : null;

    /// <summary>What an item sells for in gold, or null.</summary>
    public static double? PriceOf(Item item) => GoldOf(item);

    static double Chance(ModifierStatNodes? nodes) => nodes == null ? 0 : Fraction(nodes.Total.Value);

    static double Fraction(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
