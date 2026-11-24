using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Hunting;
using GamesByMalcs.Melvor2.Stats.Nodes;
using GamesByMalcs.Melvor2;
using Godot;

namespace HuntingRates;

/// <summary>How many surveys a hunt takes until its tracks are found, a reading of <see cref="HuntRule"/>.</summary>
enum SurveysPerHunt
{
    /// <summary>
    /// The middle of the page's surveying range: the Hunting page's panel of the animal selected writes the time of
    /// surveying as a range, from one survey to several ("Surveying: 3.84s - 11.52s" is one to three surveys of 3.84 s),
    /// and the window takes its middle, as it takes the middle of every other range of the game. The hour uses this one.
    /// </summary>
    Middle,
    /// <summary>
    /// Each survey finds the tracks at the animal's tracking success chance (HuntingActionStats.TrackingSuccessChance, by its
    /// name: no tracking step was ever seen to fail, so the chance must decide something else), and the last survey of the
    /// page's range always does. The watch holds the surveys it counts against this reading too.
    /// </summary>
    ChanceUpToMost,
}

/// <summary>
/// How many tracking steps an escape adds to the steps still needed: which of the game's numbers it is. Every number the
/// game's members give that is 4 on Yellowtail Bird (steps 4 to 8, its own 4 to 8 before bonuses), where 7 of 7 escapes
/// added 4 (check-3-watch), is a reading: the fewest, the width and half the most, each with your bonuses
/// (HuntingActionStats.TrackingSteps) and before them (HuntingAnimal.BaseMinTrackingSteps, BaseMaxTrackingSteps). The
/// readings that 4 rules out (the most, the middle, the steps a hunt needs at its start) are not here. On most other
/// animals the readings differ, so the window counts an escape there only where every reading still open gives one number
/// (<see cref="HuntRule.Escape"/>); the watch on such an animal rules readings out.
/// </summary>
enum EscapeSteps
{
    /// <summary>The fewest tracking steps of the animal, with your bonuses (the low end of HuntingActionStats.TrackingSteps).</summary>
    Fewest,
    /// <summary>The most tracking steps less the fewest, with your bonuses (the width of HuntingActionStats.TrackingSteps).</summary>
    Width,
    /// <summary>Half the most tracking steps with your bonuses, rounded down.</summary>
    HalfMost,
    /// <summary>The animal's own fewest steps, before your bonuses (HuntingAnimal.BaseMinTrackingSteps).</summary>
    BaseFewest,
    /// <summary>The animal's own most steps less its own fewest, before your bonuses (BaseMaxTrackingSteps - BaseMinTrackingSteps).</summary>
    BaseWidth,
    /// <summary>Half the animal's own most steps before your bonuses (HuntingAnimal.BaseMaxTrackingSteps), rounded down.</summary>
    HalfBaseMost,
    /// <summary>
    /// 4 on every animal, no member of the game's: a fixed number of steps is a rule only once it is measured on at least
    /// two animals whose step ranges differ (the orchestrator's decision, review 6), so this reading alone never counts an
    /// escape (<see cref="AnimalRates.FourMeasuredOn"/>).
    /// </summary>
    Four,
}

/// <summary>
/// How the links of a hunt join into a catch, as the silent watch saw them on a character's own hunting (check-3-watch,
/// 24 Nov 2026: 15 minutes of Yellowtail Bird). A hunt starts with surveys until the tracks are found
/// (<see cref="Surveys"/>); then tracking, every step one tracking interval and never failing; then a try of the trap at
/// the trapping chance. A failed try is tried again at the reattempt chance, by the game's own words for it, and otherwise
/// the animal escapes: back to tracking, with more steps to go (<see cref="Escape"/>, the readings no watch has ruled out
/// yet), then another try. A catch ends the hunt, and the next one starts with surveying. The chances are taken as they
/// are now: a trap whose bonuses change the chance after a failed try is not counted.
/// </summary>
sealed record HuntRule(SurveysPerHunt Surveys, EscapeSteps[] Escape);

/// <summary>
/// What a try of the trap takes from the bank: one trap and one bait, whether it catches or not (measured), and less what
/// the preservation chance keeps only when <see cref="Kept"/>. Kept is false: no try was seen to keep one, and Fishing
/// Rates measured that a preserved bait is taken from the bank all the same (issue #64), so the chance is named as not
/// counted.
/// </summary>
sealed record UseRule(bool Kept);

/// <summary>When the Hunting XP comes (lesson 55: measured, never assumed).</summary>
enum XpWith
{
    /// <summary>With a catch, once, and with no other step of a hunt.</summary>
    Catch,
}

/// <summary>When the output of an animal (HuntingActionStats.Products) comes.</summary>
enum OutputWith
{
    /// <summary>With a catch, each item at its own chance.</summary>
    Catch,
}

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
/// A catch is a chain of links (<see cref="HuntRule"/>): surveys (SurveyInterval each, as many as the page's surveying
/// range holds, its middle), tracking (TrackingSteps steps of TrackingInterval each, the middle of each range), tries of
/// the trap (TrappingInterval, the middle of its range, at TrappingSuccessChance), and escapes that add steps
/// (TrappingReattemptChance keeps a failed try from being one). Every number of a link is the game's; how the links join
/// was measured by the silent watch. Where a number the chain needs is not known for an animal (how many steps an escape
/// adds there, how many surveys the page writes for it, what a trap's bonuses after a failed try do), what rests on it is
/// null and the window shows "-", never a number of the mod's own or another animal's (the review of pull request #61,
/// reviews 5 and 6 of this job). An animal not discovered yet has no hour at all: its line gives nothing away.
/// </summary>
sealed class AnimalRates
{
    /// <summary>
    /// How the links of a hunt join, and what backs it: set here, in this one place. Measured by the silent watch on 15
    /// minutes of Yellowtail Bird (check-3-watch, 24 Nov 2026): the surveys of 6 hunts by the game's own count of failed
    /// surveys (2, 1, 2, 3, 1, 1), never more than the page's three; 62 tracking steps, each moved on by one after
    /// one interval, none failed and none skipped; 13 tries of the trap, 6 caught and 7 failed; every failed try ended in
    /// an escape back to tracking, never to surveying, each adding exactly 4 steps (6 to 10 to 14 to 18, 8 to 12, 4 to 8,
    /// 4 to 8, 8 to 12); a catch started the next hunt with surveying. No failed try was tried again: 0.4 were expected at
    /// the reattempt chance of 6%, whose own words state the rule ("The chance to reattempt a trapping action when it
    /// fails", the panel's bonus tooltip, check-5): the words state the reattempt, the escape was measured. Which number
    /// of the game an escape's 4 is, is open: every reading of <see cref="EscapeSteps"/> gives 4 on Yellowtail Bird.
    /// </summary>
    static readonly Settled<HuntRule>? KnownHunt = new(new HuntRule(SurveysPerHunt.Middle, Enum.GetValues<EscapeSteps>()), RuleSource.Measured,
        "The chance to reattempt a trapping action when it fails",
        "62 steps, 13 tries, 7 escapes and 6 catches of Yellowtail Bird");

    /// <summary>
    /// The animals a fixed number of steps for an escape (<see cref="EscapeSteps.Four"/>) was measured on. It becomes a
    /// rule only when the list holds two animals whose step ranges differ; until then a line whose escape rests on it alone
    /// is "-" (review 6, the orchestrator's decision). Yellowtail Bird: 7 of 7 escapes added 4 (check-3-watch).
    /// </summary>
    public static readonly string[] FourMeasuredOn = ["Yellowtail Bird"];

    /// <summary>
    /// What a try takes from the bank, and what backs it: set here, in this one place. Measured by the same watch: 13 tries
    /// took 13 traps and 13 Seed Bait, one of each on every try, caught or escaped, by the bank's counts, and none was kept.
    /// The preservation chance (2% there; a Reinforced trap brings 20% of its own, a Fortified one 40%, check-5 "Dev:
    /// game") is not counted: what it keeps was never measured here, and Fishing Rates measured that a preserved bait is
    /// taken from the bank all the same (issue #64). Its tip says "preserved or not", as Fishing Rates' does now.
    /// </summary>
    static readonly Settled<UseRule>? KnownUse = new(new UseRule(false), RuleSource.Measured, "",
        "13 traps and 13 bait on 13 tries of Yellowtail Bird");

    /// <summary>
    /// When the XP comes, and what backs it: set here, in this one place. Measured by the same watch: Hunting XP on 6 of 6
    /// catches and in none of the 75 other frames with an event of a hunt (no XP for a survey, a step or a failed try).
    /// The XP of a catch lies within the range the animal's card writes (359.6 to 589.7 against 233.6 to 700.9), 467.7 on
    /// average against the middle of the range, 467.25: the window counts the middle (<see cref="XpMiddle"/>). The page
    /// says "Animal Levels increase the average Skill XP & Item quantity output." (its panel, check-6), and Yellowtail
    /// Bird went from level 1 (check-5) to 7 (check-6) around that watch: how much a level raises the average is not
    /// known, which the XP tip says.
    /// </summary>
    static readonly Settled<XpWith>? KnownXp = new(XpWith.Catch, RuleSource.Measured, "Animal Levels increase the average Skill XP & Item quantity output.",
        "XP on 6 of 6 catches and in none of 75 other frames");

    /// <summary>
    /// What surveying finds (HuntingActionStats.SurveyingProducts), and what backs it: not counted. No find came in the
    /// watch's 10 surveys (a 10% chance each: 1 expected), and the game has a switch by its name that may turn them off
    /// (HuntingActionComponent._allowSurveyingProducts, private). Null until a watch decides it ("the survey finds").
    /// </summary>
    public static readonly Settled<bool>? SurveyFindsRule = null;

    /// <summary>
    /// When the output comes, and what backs it: set here, in this one place. Measured by the same watch: the animal's
    /// output came on 6 of 6 catches and at no other time (Feathers 46.7 a catch against the window's 47.94, one Nomel
    /// Herb Seed in 6 catches against 0.2 a catch).
    /// </summary>
    static readonly Settled<OutputWith>? KnownOutput = new(OutputWith.Catch, RuleSource.Measured, "", "output on 6 of 6 catches and at no other time");

    /// <summary>How the links of a hunt join, or null while nothing has settled it (<see cref="KnownHunt"/>).</summary>
    public static Settled<HuntRule>? HuntRule => KnownHunt;

    /// <summary>What a try uses up, or null while nothing has settled it (<see cref="KnownUse"/>).</summary>
    public static Settled<UseRule>? UseRule => KnownUse;

    /// <summary>When the XP is added, or null while nothing has settled it (<see cref="KnownXp"/>).</summary>
    public static Settled<XpWith>? XpRule => KnownXp;

    /// <summary>When the output comes, or null while nothing has settled it (<see cref="KnownOutput"/>).</summary>
    public static Settled<OutputWith>? OutputRule => KnownOutput;

    /// <summary>
    /// One thing a catch or a survey can bring: <see cref="Chance"/> per catch (per survey for a survey's), how many it
    /// gives when it comes (<see cref="Each"/>, doubling included where it is counted), and an hour of it where the
    /// catches or surveys of an hour are counted.
    /// </summary>
    public sealed record Drop(Item Item, string Name, double Chance, int Quantity, double Each, bool Collectable, bool Found, double? PerHour, double? GoldPerHour);

    /// <summary>A rare species: its share of the rare ones by the table's weights, and the extra products it brings.</summary>
    public sealed record Species(string Name, double Share, string Locked, List<Drop> Extras);

    /// <summary>What one catch takes on average by a reading of the chain: its time, and its surveys, steps, tries and escapes.</summary>
    public readonly record struct Catch(double Seconds, double Surveys, double Steps, double Tries, double Escapes);

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
    /// <summary>The page's panel of the animal selected lies under the window, where a click's animal would show.</summary>
    public bool PanelCovered { get; set; }
    /// <summary>A hunt runs (<see cref="IsHunting"/>): a click only shows, and selects nothing.</summary>
    public bool HuntRuns { get; init; }
    /// <summary>While a hunt runs, a click would select on this line, and so does nothing (HuntingRatesMod.ClickChanges).</summary>
    public bool HuntBlocks { get; set; }
    /// <summary>The page keeps this animal's card button disabled: a click does not select it.</summary>
    public bool CardBlocked { get; set; }
    /// <summary>The tool as the animal's card on the page writes it, once the page has shown that card (null before).</summary>
    public string? ToolWords { get; set; }

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
    /// <summary>
    /// The most surveys a hunt takes as the Hunting page writes it for this animal: the high end of its surveying range
    /// over the low end (HuntingSelectedRecipeContainer.SurveyingInterval, an IntervalRangeDisplay, read by
    /// HuntingRatesMod.ReadSurveyRange while the page's panel shows this animal); null until it has. No member of the game's
    /// gives it (ApiDump: HuntingActionStats, HuntingAnimal, HuntingLocation and ActiveHuntingLocation have only the time of
    /// one survey), and another animal's is never taken for it (review 6).
    /// </summary>
    public double? SurveysMost { get; private set; }
    /// <summary>
    /// The location's chance by the name of insights (HuntingLocation.BaseInsightsChance): logged, and no reading of a
    /// survey any more (the watch saw 1.7 surveys a hunt, not 200).
    /// </summary>
    public double Insights { get; private set; }
    /// <summary>
    /// The location's base XP of a survey (HuntingLocation.BaseSurveyingExperience), without bonuses: the watch saw no
    /// XP for a survey.
    /// </summary>
    public double SurveyXpBase { get; private set; }
    /// <summary>The steps of tracking, the game's range (HuntingActionStats.TrackingSteps).</summary>
    public int StepsMin { get; private set; }
    public int StepsMax { get; private set; }
    /// <summary>
    /// The animal's own fewest and most steps before bonuses (HuntingAnimal.BaseMinTrackingSteps, BaseMaxTrackingSteps), for
    /// the readings of an escape.
    /// </summary>
    public int BaseStepsMin { get; private set; }
    public int BaseStepsMax { get; private set; }
    /// <summary>Ours: every number of steps in the game's range as likely, so its middle.</summary>
    public double StepsMiddle => (StepsMin + StepsMax) / 2.0;
    /// <summary>One tracking step, in seconds, the game's range (HuntingActionStats.TrackingInterval).</summary>
    public double StepMinSeconds { get; private set; }
    public double StepMaxSeconds { get; private set; }
    /// <summary>Ours: the middle of the range.</summary>
    public double StepSeconds => (StepMinSeconds + StepMaxSeconds) / 2;
    /// <summary>
    /// The tracking success chance (HuntingActionStats.TrackingSuccessChance): no tracking step fails, so it is held
    /// against the surveys of a hunt only.
    /// </summary>
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
    /// <summary>
    /// The trap selected for the animal has bonuses that a failed try puts on it (HuntingTrapCapability.FailBonuses): the
    /// chance of the next try is not the one now, and the window does not count a catch. Null when they could not be read
    /// (Native.HasFailBonuses, one line in the log): no catch is counted then either.
    /// </summary>
    public bool? TrapFailBonuses { get; private set; }

    /// <summary>The game gives a time for every link: without them there is nothing to count.</summary>
    public bool HasTime => SurveySeconds > 0.01 && StepMaxSeconds > 0.01 && TrapMaxSeconds > 0.01 &&
        double.IsFinite(SurveySeconds + StepMinSeconds + StepMaxSeconds + TrapMinSeconds + TrapMaxSeconds) && StepsMax >= Math.Max(0, StepsMin);

    /// <summary>
    /// The steps an escape adds here by every reading no watch has ruled out (<see cref="HuntRule.Escape"/>), and the one
    /// number when they all agree; null when they do not, or nothing is settled. A fixed 4 left alone counts only once it
    /// is measured on two animals whose step ranges differ (<see cref="FourMeasuredOn"/>).
    /// </summary>
    public int? EscapeAdds
    {
        get
        {
            if (HuntRule is not { } hunt || EscapeReadings is not { Count: > 0 } readings || readings.Any(n => n != readings[0])) return null;
            bool fourAlone = hunt.Reading.Escape.All(r => r == EscapeSteps.Four);
            return fourAlone && FourMeasuredOn.Length < 2 ? null : readings[0];
        }
    }

    /// <summary>The steps an escape adds by each reading still open, in the order of <see cref="EscapeSteps"/>.</summary>
    public List<int>? EscapeReadings => HuntRule is { } hunt ? hunt.Reading.Escape.Select(reading => EscapeStepsBy(reading, this)).ToList() : null;

    /// <summary>The steps an escape adds by one reading, from this animal's own numbers.</summary>
    public static int EscapeStepsBy(EscapeSteps reading, AnimalRates a) =>
        EscapeStepsBy(reading, a.StepsMin, a.StepsMax, a.BaseStepsMin, a.BaseStepsMax);

    /// <summary>The steps an escape adds by one reading: the steps with your bonuses, and the animal's own before them.</summary>
    public static int EscapeStepsBy(EscapeSteps reading, int fewest, int most, int baseFewest, int baseMost) => reading switch
    {
        EscapeSteps.Fewest => fewest,
        EscapeSteps.Width => most - fewest,
        EscapeSteps.HalfMost => most / 2,
        EscapeSteps.BaseFewest => baseFewest,
        EscapeSteps.BaseWidth => baseMost - baseFewest,
        EscapeSteps.HalfBaseMost => baseMost / 2,
        _ => 4,
    };

    // ---- what a catch gives ---------------------------------------------------------------------------------------------

    /// <summary>The XP the game adds for the animal before a catch's own share (HuntingActionStats.Experience.XpToAdd).</summary>
    public double XpEach { get; private set; }
    /// <summary>
    /// The range the Hunting page writes the XP of a catch in, as the multipliers its XP view is bound with
    /// (ExperienceStatsXPView), read from the page (HuntingRatesMod.XpRange); null until the page has shown them.
    /// </summary>
    public (double Low, double High)? XpRange { get; init; }
    /// <summary>The middle of the page's range of the XP, ours; null until the range is read.</summary>
    public double? XpMiddle => XpRange is { } r ? XpEach * (r.Low + r.High) / 2 : null;
    /// <summary>
    /// The XP of a catch by the settled rule (<see cref="XpRule"/>), the middle of the page's range; null until both
    /// are known.
    /// </summary>
    public double? XpPerCatch => XpRule != null ? XpMiddle : null;
    /// <summary>The animal's journal XP of a tracking step and of a catch, the game's base values: what the watch measured.</summary>
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

    /// <summary>A catch on average by the settled chain; null while what it rests on is not known for this animal.</summary>
    public Catch? PerCatch { get; private set; }
    /// <summary>The time of a catch on average by the settled chain; null while it is not known.</summary>
    public double? SecondsPerCatch => PerCatch?.Seconds;
    public double? CatchesPerHour => SecondsPerCatch is { } s && s > 0 ? 3600 / s : null;
    /// <summary>Surveys a catch takes on average by the settled chain.</summary>
    public double? SurveysPerCatch => PerCatch?.Surveys;
    public double? XpPerHour => CatchesPerHour is { } c && XpPerCatch is { } xp ? c * xp : null;
    /// <summary>The journal XP of an hour: its base XP for every tracking step and for every catch, as the watch measured.</summary>
    public double? JournalXpPerHour => CatchesPerHour is { } c && PerCatch is { } k ? c * (k.Steps * JournalTrackingXp + JournalTrappingXp) : null;
    /// <summary>What the products and rare extras of a catch sell for in gold (collectables apart).</summary>
    public double CatchGold { get; private set; }
    /// <summary>What a survey's finds would sell for: not in the gold while <see cref="SurveyFindsRule"/> is not settled.</summary>
    public double SurveyGold { get; private set; }
    public double? GoldPerHour => CatchesPerHour is { } c && OutputRule != null
        ? c * CatchGold + (SurveyFindsRule != null ? c * (SurveysPerCatch ?? 0) * SurveyGold : 0) : null;
    /// <summary>Traps of an hour: one a try, preserved or not (<see cref="UseRule"/>); none for an animal hunted without a trap.</summary>
    public double? TrapsPerHour { get; private set; }
    /// <summary>Bait of an hour: one a try while it is switched on, preserved or not; null when not counted.</summary>
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

    /// <summary>
    /// A hunt runs, by the one test the window and its development request share (lesson 56; Firemaking Rates' IsBurning,
    /// which errs on the safe side): Hunting is started (ActionComponent.IsStarted), or it is among the game's running
    /// actions (ActionManager.ActiveActions), or the animal hunted is in a state other than idle (HuntState). While it
    /// runs, nothing is selected.
    /// </summary>
    public static bool IsHunting(HuntingEntity hunting, Game? game) =>
        hunting.Action.IsStarted || hunting.Action.CurrentAnimal is { State: not HuntState.Idle } ||
        game?.ActionManager.ActiveActions.Any(a => ReferenceEquals(a.Entity, hunting)) == true;

    public static bool LocationListed(ActiveHuntingLocation? location) =>
        location?.Location is { } l && l.DisplayRequirements?.AreMet != false && (!l.OnlyShowWhenRecipesAvailable || location.UnlockedRecipeCount > 0);

    /// <param name="look">The name, picture and its colour the list shows (HuntingRatesMod.Look).</param>
    /// <param name="surveysMost">The most surveys a hunt takes as the page wrote it for this animal, or null (HuntingRatesMod.SurveysFor).</param>
    /// <param name="failBonuses">Whether a trap has bonuses after a failed try, null when they cannot be read (HuntingRatesMod.FailBonuses).</param>
    /// <param name="hidden">The game's word for something not found yet, for a rare species never trapped and a collectable never found.</param>
    /// <param name="huntRuns">A hunt runs (<see cref="IsHunting"/>), read once for every line.</param>
    public static AnimalRates Read(ActiveHuntingAnimal active, HuntingEntity hunting, (string Name, Texture2D? Picture, Color Tint) look,
        (double, double)? xpRange, double? surveysMost, Func<Item, bool?> failBonuses, Func<Item, long> inBank, string hidden, bool huntRuns)
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
            Hunted = huntRuns && ReferenceEquals(action.CurrentAnimal, active),
            HuntRuns = huntRuns,
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
        if (surveysMost is { } most && double.IsFinite(most) && most >= 1) r.SurveysMost = most;
        r.Insights = Fraction(location?.BaseInsightsChance ?? 0);
        r.SurveyXpBase = location?.BaseSurveyingExperience ?? 0;
        var (stepsMin, stepsMax) = stats.TrackingSteps.Value;
        r.StepsMin = Math.Max(0, stepsMin);
        r.StepsMax = Math.Max(r.StepsMin, stepsMax);
        r.BaseStepsMin = animal.BaseMinTrackingSteps;
        r.BaseStepsMax = Math.Max(r.BaseStepsMin, animal.BaseMaxTrackingSteps);
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
        r.TrapFailBonuses = r.Trapless || r.Trap == null ? false : failBonuses(r.Trap);
        r.Bait = animal.PreferredBait;
        r.UseBait = active.UseBait && r.Bait != null;
        r.BaitInBank = r.Bait != null ? inBank(r.Bait) : 0;

        // The chain by the settled rule, where everything it needs is known for this animal. An animal not discovered yet
        // gives nothing away: no hour at all (review 6, fault 8), as its tooltip shows nothing of its hunt either.
        if (HuntRule is { } hunt && r.Discovered && r.TrapFailBonuses == false && r.EscapeAdds is { } escape)
            r.PerCatch = Chain(r, hunt.Reading.Surveys, escape);
        // One trap and one bait a try, preserved or not (UseRule); none of either is counted without the chain.
        if (r.Trapless && r.Discovered) r.TrapsPerHour = 0;
        if (UseRule is { } use && r.CatchesPerHour is { } perHour && r.PerCatch is { } k)
        {
            double each = k.Tries * (use.Reading.Kept ? 1 - r.Preservation : 1) * perHour;
            if (!r.Trapless) r.TrapsPerHour = each;
            // Bait without a trap was never watched: not counted then.
            if (r.UseBait && !r.Trapless) r.BaitPerHour = each;
        }
        // An hour of an item rests on the catches of an hour and on what the output comes with (OutputRule).
        double? catches = OutputRule != null ? r.CatchesPerHour : null;

        // A catch's products, with the character's bonuses (not the animal's base list). The doubling chance doubles them,
        // as Fishing Rates and Woodcutting Rates count theirs: the window's reading of the game's words for it, "Your
        // chance to double the output" (the panel's bonus tooltip, check-5). A product whose number the page writes as a
        // range ("23 - 70" for 47 Feathers) comes as many times as the catch's XP is of the animal's XP (the watch: 6 of 6
        // catches), so its middle is the game's quantity.
        var found = hunting.Journal?.CollectablesFound;
        foreach (var node in stats.Products?.Values ?? [])
            if (node?.Value is { Item: { } item } product && product.Quantity > 0)
                r.Products.Add(Line(item, Fraction(product.Chance), product.Quantity, 1 + r.Doubling, false, false, catches));
        // What surveying finds is not counted until a watch has seen it (SurveyFindsRule): listed, with no hour.
        r.SurveyFinds.AddRange(SurveyDrops(stats, r, SurveyFindsRule != null ? catches : null));
        // A rare species comes with the game's chance, which one by the table's weights (Item Sources reads a weighted table
        // so: mods/Shared/ItemIndex.cs, AddTable), with its extra products at their own chances. A collectable found once
        // is named as found and its gold is never counted: the journal keeps it (HuntingJournalComponent.CollectablesFound).
        // As the journal shows them, a species never trapped (its species list follows HuntingJournalComponent
        // .SpeciesTrapped) and a collectable never found (its list of HiddenItemIcon follows what was found) are not named:
        // the game's own word for something not found yet stands for them (AnimalSpeciesView.HiddenText). A species still
        // locked (its own requirements) cannot be caught yet, by its lock: its extras are listed and not counted.
        var trapped = hunting.Journal?.SpeciesTrapped;
        if (animal.RareSpecies is { } table)
            foreach (WeightedHuntingAnimalSpecies weighted in table.Items ?? Enumerable.Empty<WeightedHuntingAnimalSpecies>())
            {
                if (weighted?.Species is not { } species) continue;
                double share = Fraction(table.GetItemChance(weighted));
                bool open = species.Requirements?.AreMet != false;
                var extras = new List<Drop>();
                foreach (HuntingAnimalSpeciesProduct extra in species.ExtraProducts ?? Enumerable.Empty<HuntingAnimalSpeciesProduct>())
                    if (extra?.Item is { } item && extra.Quantity > 0)
                    {
                        bool got = extra.IsCollectable && found != null && found.TryGetValue(item, out bool f) && f;
                        Drop drop = Line(item, r.RareChance * share * Fraction(extra.Chance), extra.Quantity, 1, extra.IsCollectable, got, open ? catches : null);
                        extras.Add(extra.IsCollectable && !got ? drop with { Name = hidden } : drop);
                    }
                string locked = open ? "" : Native.Needs([species.Requirements]);
                bool seen = trapped == null || trapped.GetValue(species) > 0;
                r.Rare.Add(new Species(seen ? species.DisplayName.Text : hidden, share, locked, extras));
            }
        r.CatchGold = r.Products.Concat(r.Rare.Where(s => s.Locked.Length == 0).SelectMany(s => s.Extras)).Where(d => !d.Collectable)
            .Sum(d => GoldOf(d.Item) is { } price ? d.Chance * d.Each * price : 0);
        r.SurveyGold = r.SurveyFinds.Sum(d => GoldOf(d.Item) is { } price ? d.Chance * d.Each * price : 0);
        return r;
    }

    // What a survey can bring, an hour of it by the surveys of an hour where those are counted (not while SurveyFindsRule
    // is not settled). Not doubled: the window does not know whether the doubling chance reaches a survey.
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
    /// What a catch takes on average by a reading of how many surveys a hunt takes and a number of steps an escape adds,
    /// from the game's numbers of each link: the middle of each of the game's ranges of time and of steps; every try of
    /// the trap catching at the trapping chance, so 1 over it tries a catch; every failed try not tried again (the
    /// reattempt chance) an escape. Null when the page's surveying range is not read, or no catch would ever come.
    /// </summary>
    public static Catch? Chain(AnimalRates a, SurveysPerHunt reading, double escapeSteps)
    {
        if (!a.HasTime || a.SurveysMost is not { } most || most < 1 || a.TrapChance <= 1e-9) return null;
        double surveys = reading == SurveysPerHunt.Middle ? (1 + most) / 2 : SurveysUpToMost(a.TrackChance, most);
        double tries = 1 / a.TrapChance;
        double escapes = (tries - 1) * (1 - a.ReattemptChance);
        double steps = a.StepsMiddle + escapes * escapeSteps;
        double seconds = surveys * a.SurveySeconds + steps * a.StepSeconds + tries * a.TrapSeconds;
        return double.IsFinite(seconds) && seconds > 0 ? new Catch(seconds, surveys, steps, tries, escapes) : null;
    }

    /// <summary>
    /// The surveys a hunt takes on average when each finds the tracks at a chance and the last of the page's range
    /// always does.
    /// </summary>
    public static double SurveysUpToMost(double chance, double most)
    {
        int last = Math.Max(1, (int)Math.Round(most));
        double sum = 0, miss = 1;
        for (int k = 0; k < last; k++)
        {
            sum += miss;
            miss *= 1 - chance;
        }
        return sum;
    }

    static double? GoldOf(Item item) => item.SellsFor is { Currency: { Id: Gold }, Quantity: > 0 } price ? price.Quantity : null;

    /// <summary>What an item sells for in gold, or null.</summary>
    public static double? PriceOf(Item item) => GoldOf(item);

    static double Chance(ModifierStatNodes? nodes) => nodes == null ? 0 : Fraction(nodes.Total.Value);

    static double Fraction(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
