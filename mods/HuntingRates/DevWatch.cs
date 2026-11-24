#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Hunting;
using Godot;
using MelvorModLoader;

namespace HuntingRates;

// Development: a file "huntingwatch.request" in the mod's folder starts a silent watch of the character's hunting, and
// "huntingwatch.done" says how it ended. The file holds a number of minutes (15 when it is empty), which is logged. It
// opens nothing, clicks nothing, does not wait on the mouse and changes nothing: it only listens to the game's own events,
// as Woodcutting Rates' watch listens to its hits (mods/WoodcuttingRates/DevWatch.cs): every animal's StateChanged,
// TracksChanged, CurrentStepChanged, RequiredStepsChanged, TrapFailCountChanged, AnimalTrapped and AnimalEscaped; the
// action's CurrentTrapChanged, CurrentAnimalChanged and ItemAdded; the Hunting XP (ExperienceComponent.XpChanged) and the
// hunted animal's journal XP (HuntingAnimalJournalEntry.Experience.XpChanged). The items, traps and bait are the bank's
// counts of every item an animal can give or use, read on the frame after each frame with an event and four times a second
// between,
// as Woodcutting Rates' watch reads them (the action's ItemAdded was never raised there, nor in check-3-watch; it is
// logged beside the counts as a witness). The game's own clock is read at every event too: its tick
// (TrackerComponent.CurrentTick, with the tracker periods' ticks over their milliseconds for the length of one), and the
// interval its action timer runs (ActionComponent.ActionTimer, WaitTime, read on the frames between two events, where it
// can only be the link's in progress: DevWatchFrame, on every process frame), so that a step that took longer on the real
// clock than the page's longest (check-3-watch: 4 of 62) can be told from a step the game itself made longer. If the
// character is not hunting when it starts, it says so in one line and ends. It runs alone: a light check sent meanwhile
// waits for it to end.
//   pwsh .claude\codex\check-mod.ps1 -Worktree <worktree> -Mod HuntingRates -Request huntingwatch.request -Produces huntingwatch.done -Text 30 -Seconds 2100 -Out <folder>
// At its start it says, from the window's own numbers for the animal hunted, how many catches, tries, failed tries,
// escapes, steps and hunts the minutes asked will bring, the count of each that 9 watches of 10 reach (DevReach: the
// hunt played by the window's own numbers), and NOT ENOUGH, with the minutes to ask, for a count they do not reach
// (lesson 59, review 6). The page is read on every tick while the watch runs, so the surveys and the XP range of the
// animal come in whenever the Hunting page is on the screen. Every frame with an event is one "Dev: hunt" line, with
// the time since the line before on the real clock and on the game's. It ends when the minutes are up or after two
// minutes without an event of a hunt, with one line per question for every animal hunted, each with a verdict in
// capitals. A link that held without exception is judged on its events (every step, every try, every escape), not on
// whole hunts; a mean or a chance is judged with a tolerance from its own spread (twice its standard error), never a
// fixed share. The questions are the links of a catch the window's hour rests on (AnimalRates.HuntRule, UseRule,
// XpRule, OutputRule): the surveys of a hunt against both readings of SurveysPerHunt and the page's most; a tracking
// step (moved on by one, never back but for the reset after a catch, none skipped) and its time against the page's
// range, on the real clock and the game's; the steps a hunt needs; a try against the trapping chance; a failed try
// against the reattempt chance; an escape against every reading of EscapeSteps, naming the readings that still tie; the
// traps and bait of a try against the preservation chance; the XP and the journal XP; the XP of a catch beside the
// animal's level and the size the journal recorded (the page says levels raise the average); the survey finds, by the
// bank's counts over the whole watch, against the surveys counted; the output of a catch against the catch's share of
// the XP; the time of each hunt against its own surveys, steps and tries (tight: the escapes, which make most of a
// hunt's spread, are taken out); and the time of a catch against the window's by every reading still open, within the
// larger of the spread seen and the one the window's numbers give a hunt (the start line says how close the minutes
// asked can hold it). Then the game's own tracker (HuntingEntity.Tracker) for every period that lies inside the watch,
// spent on one animal and holding at least FewestCatches catches, beside the window's hour, each number within twice
// its own spread (DevPeriodSpread). Its last line says it stopped listening and changed nothing. Every listener runs
// inside a try, and all are taken off when it ends, on Stop (with the done file, DevStopped) and on another character
// (DevUnwatchAll).
public partial class HuntingRatesMod
{
    const string WatchRequestFile = "huntingwatch.request", WatchDoneFile = "huntingwatch.done";
    // Two minutes without an event of a hunt end the watch: the longest link of a hunt (a step of 28 s) is far shorter.
    const ulong WatchIdle = 120000;
    const ulong BankReadEvery = 250;
    // The fewest events of a kind a verdict is judged on (lesson 59: each verdict counts its own events). Fewer, and the
    // verdict is NOT DECIDED with the number seen; the watch says at its start whether the minutes asked reach them.
    const int FewestSteps = 20, FewestTries = 10, FewestFails = 10, FewestEscapes = 3, FewestHunts = 5, FewestCatches = 5;

    // What the game did in one frame, and the game's clock then.
    sealed class DevMoment
    {
        public long Frame;
        public ulong At;
        public long? Tick;
        public ActiveHuntingAnimal? Animal;
        public readonly List<string> Order = new();
        public (HuntState From, HuntState To)? State;
        public readonly List<(int From, int To)> Steps = new();
        public int? Required, Tracks;
        public (int From, int To)? TrapFails;
        public bool Trapped, Escaped;
        public double Xp, JournalXp;
        public int XpChanges, JournalXpChanges;
        public readonly List<(Item Item, int Quantity)> Items = new();
        public readonly List<(Item Item, int Quantity)> Added = new();
        public readonly List<string> Other = new();
    }

    // One step, one try of the trap: its time on the real clock and on the game's (NaN when not read), the interval the
    // game's action timer ran for it, and the page's range when it began.
    sealed record DevLink(double Real, double Game, double Set, double Min, double Max);
    sealed record DevTry(string Outcome, DevLink Time, double Chance, double Reattempt, double Preservation, int Trap, int Bait, bool BaitOn);
    sealed record DevEscape(int Was, int Now, HuntState? After, Dictionary<EscapeSteps, int> Readings);

    // One hunt, from a catch (or the watch's start) to the next catch.
    sealed class DevHuntSeen
    {
        public bool Whole;
        public ulong StartAt;
        public long? StartTick;
        public int? Surveys, Required, RequiredMin, RequiredMax;
        public double SurveyReal = double.NaN, SurveyGame = double.NaN;
        public int Steps, Tries, Escapes;
        // The middles of the page's ranges of every step and try of the hunt, each as it was when the link began, the time of
        // one survey, and the spread the ranges give the hunt (each link anywhere in its range).
        public double Middles, LinkVariance, SurveyEach = double.NaN;
        public double Real = double.NaN, Game = double.NaN;
    }

    // Everything seen of one animal.
    sealed class DevSeen
    {
        public ActiveHuntingAnimal Animal = null!;
        public readonly List<(DevLink Time, int By)> Steps = new();
        public int StepsBack, Resets;
        public readonly List<DevTry> Tries = new();
        public readonly List<DevEscape> Escapes = new();
        public readonly List<DevHuntSeen> Hunts = new();
        public DevHuntSeen Now = new();
        // A catch's XP, the animal's XP before its share, the card's multipliers, the animal's level then, and the size the
        // journal recorded (HuntingAnimalJournalEntry.RecentStatEntries, the StatPercentage at the end of the list nearer the
        // catch's share; NaN when none was read): what the page says a level raises (review 6, point 10).
        public readonly List<(double Xp, double Each, double Low, double High, int? Level, double Stat)> CatchXp = new();
        public int XpElsewhere;
        public double XpElsewhereSum;
        public readonly List<double> JournalStep = new(), JournalCatch = new();
        public int JournalElsewhere;
        public readonly List<(Dictionary<Item, int> Items, double Share)> Catches = new();
        public readonly Dictionary<Item, int> OutputElsewhere = new(ReferenceEqualityComparer.Instance);
        public int TrapElsewhere, BaitElsewhere;
    }

    sealed class DevHuntWatch
    {
        public ulong Since, LastEvent, Length;
        public long? SinceTick;
        public double TickMs = double.NaN;
        public HuntingActionComponent Action = null!;
        public readonly List<Action> Off = new();
        public readonly List<DevMoment> Moments = new();
        public readonly Dictionary<ActiveHuntingAnimal, DevSeen> Seen = new(ReferenceEqualityComparer.Instance);
        public DevMoment? Open;
        public ulong LastLine;
        public bool Failed, AnimalChanged;
        // The last frame with an event: when, the game's tick, and what ran from then on (the interval of the action timer,
        // the page's ranges, the chances, the steps needed), for the link that ends at the next event.
        public ulong LastAt;
        public long? LastTick;
        public long LastEventFrame;
        public bool HaveLast;
        public double LastSet = double.NaN, LastChance, LastReattempt, LastPreservation;
        public (double Min, double Max) LastStep, LastTrap;
        public int LastRequired;
        public GamesByMalcs.Melvor2.Entities.Bank.BankEntity? Bank;
        public readonly List<Item> Watched = new();
        public readonly Dictionary<Item, int> StartCounts = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<Item, int> Counts = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<Item, int> Outside = new(ReferenceEqualityComparer.Instance);
        public ulong LastRead;
        public readonly Dictionary<ActiveHuntingAnimal, string> Start = new(ReferenceEqualityComparer.Instance);
    }

    DevHuntWatch? _devHunts;

    void DevWatchRequest()
    {
        string path = Path.Combine(_ctx.Directory, WatchRequestFile);
        if (!File.Exists(path)) return;
        string asked = File.ReadAllText(path).Trim();
        File.Delete(path);
        int minutes = 15;
        if (asked.Length > 0 && !(int.TryParse(asked, NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes) && minutes is >= 1 and <= 60))
        {
            DevLog($"watch | the request's '{asked}' is not a number of minutes from 1 to 60: it watches 15");
            minutes = 15;
        }
        var action = _hunting!.Action;
        DevLog($"watch | {minutes} minutes asked{(asked.Length == 0 ? " (the request was empty)" : $" (the request said '{asked}')")}, or until two minutes without an event " +
            $"of a hunt; only listening, nothing is changed; {DevHunt()}");
        if (!action.IsStarted || !DevHuntingNow() || action.CurrentAnimal == null)
        {
            const string not = "not hunting when the watch began: nothing was listened to, nothing changed. Start hunting an animal by hand and send the request again.";
            DevLog("watch | " + not);
            File.WriteAllText(Path.Combine(_ctx.Directory, WatchDoneFile), not);
            return;
        }
        var w = new DevHuntWatch { Action = action, Since = Time.GetTicksMsec(), Length = (ulong)minutes * 60000 };
        w.LastEvent = w.LastLine = w.Since;
        w.TickMs = DevTickMs(out string tickWords);
        w.SinceTick = DevTick();
        foreach (ActiveHuntingAnimal animal in action.Animals.Values)
            if (animal != null) w.Start[animal] = DevAnimalStats(animal);
        DevBankStart(w);
        _devHunts = w;
        DevWatchListen(w);
        ActiveHuntingAnimal hunted = action.CurrentAnimal;
        // The hunt in progress when the watch begins is not whole: its hunt counts from its catch on. What runs now is the
        // last "event" a link is timed from, but only once a first event has been seen, so a link is never timed from the
        // watch's own start.
        DevRemember(w, hunted, null);
        w.HaveLast = false;
        ReadPage();
        AnimalRates ours = ReadAnimal(hunted);
        DevLog($"watch | listening; the animal hunted {hunted.Animal.Id} at {hunted.Location?.Location?.Id}, in state {hunted.State}, step {hunted.CurrentStep} of {hunted.RequiredSteps}, " +
            $"tracks {hunted.Tracks}, trap fails {hunted.TrapFailCount}, survey fails {DevSurveyFails(hunted)}; its numbers {w.Start[hunted]}; the page's surveying range " +
            $"{(ours.SurveysMost is { } most ? $"1 to {R(Math.Round(most, 3))} surveys" : "NOT READ (the Hunting page's panel has not shown this animal; the watch reads it whenever the page is on the screen)")}; " +
            $"the XP range of a catch {(ours.XpRange is { } range ? $"{R(range.Low)}..{R(range.High)}" : "NOT READ (the Hunting page has not shown this animal's card)")}; " +
            $"the game's clock: {tickWords}; " +
            (w.Bank == null ? "the bank NOT FOUND: the items, traps and bait are not read"
                : $"read from the bank's counts: {w.Watched.Count} items, this animal's trap {ours.Trap?.Id ?? "none"} {(ours.Trap != null ? w.StartCounts.GetValueOrDefault(ours.Trap) : 0)}, " +
                  $"bait {ours.Bait?.Id ?? "none"} ({(ours.UseBait ? "used" : "not used")}) {(ours.Bait != null ? w.StartCounts.GetValueOrDefault(ours.Bait) : 0)}"));
        DevWatchEnough(ours, minutes);
        DevTracker("at the start", w, null);
    }

    // What the minutes asked bring by the window's own numbers for the animal hunted, and the minutes each verdict needs
    // (lesson 59). A verdict's count is ENOUGH only when the minutes asked reach it in at least 9 watches of 10, not on
    // average (review 6, point 7): the watch plays the hunt by the window's own numbers (DevReach) and takes the count
    // that 9 of 10 of those watches reach; NOT ENOUGH says the minutes that would.
    void DevWatchEnough(AnimalRates ours, int minutes)
    {
        var readings = DevReadings(ours);
        if (readings.Count == 0)
        {
            DevLog($"watch | what {minutes} minutes give: NOT KNOWN, the window cannot count a catch of {ours.Animal.LocalId} ({DevWhyNot(ours)}); the verdicts judge what comes");
            return;
        }
        var slowest = readings.OrderByDescending(r => r.Catch.Seconds).First();
        AnimalRates.Catch k = slowest.Catch;
        double perMinute = 60 / k.Seconds, catches = minutes * perMinute;
        double fails = k.Tries - 1;
        // A number of minutes as the window writes a time: "4m 30s", "1h 5m".
        static string Minutes(double minutes) => !double.IsFinite(minutes) || minutes <= 0 ? "never" : Words.Seconds(minutes * 60);
        var needs = new List<(string What, int Count, double PerMinute)>
        {
            ("tracking steps", FewestSteps, perMinute * k.Steps), ("tries of the trap", FewestTries, perMinute * k.Tries),
            ("failed tries", FewestFails, perMinute * fails), ("escapes", FewestEscapes, perMinute * k.Escapes),
            // The hunt in progress at the start is not whole: one catch more than the hunts.
            ("whole hunts", FewestHunts + 1, perMinute), ("catches", FewestCatches, perMinute),
        };
        // The count 9 watches of 10 reach in the minutes asked, by the window's numbers at the slowest reading, and for a
        // count that falls short, the fewest whole minutes (up to 60) that reach it 9 times in 10.
        var reach = DevReach(ours, slowest.Escape, minutes);
        int Low(int index) => reach[index];
        var short_ = needs.Select((n, i) => (n.What, n.Count, Reached: Low(i))).Where(n => n.Reached < n.Count)
            .Select(n => (n.What, n.Count, n.Reached, Ask: DevAskMinutes(ours, slowest.Escape, needs.FindIndex(x => x.What == n.What), n.Count))).ToList();
        // Telling the two readings of a hunt's surveys apart: their gap against twice the standard error of a mean of hunts,
        // the spread of a hunt's surveys by the reading of the chance up to the page's most.
        double middle = ours.SurveysMost is { } most ? (1 + most) / 2 : double.NaN;
        double chance = ours.SurveysMost is { } most2 ? AnimalRates.SurveysUpToMost(ours.TrackChance, most2) : double.NaN;
        double spread = ours.SurveysMost is { } most3 ? DevSurveySpread(ours.TrackChance, most3) : double.NaN;
        double gap = Math.Abs(middle - chance);
        double huntsApart = gap > 1e-9 && double.IsFinite(spread) ? Math.Ceiling(Math.Pow(4 * spread / gap, 2)) : double.NaN;
        // How many failed tries it takes to see a try again at least once in 20 watches of 21 at the reattempt chance, and
        // how many tries to see a trap kept at the preservation chance.
        static double SeeOnce(double p) => p > 0 && p < 1 ? Math.Ceiling(Math.Log(0.05) / Math.Log(1 - p)) : double.NaN;
        double failsToSee = SeeOnce(ours.ReattemptChance), triesToSee = SeeOnce(ours.Preservation);
        var escapeValues = readings.Select(r => r.Escape).Distinct().ToList();
        // How closely the whole hunts of the minutes asked can hold the time of a catch: twice the standard error of a mean of
        // that many hunts, a hunt's spread by the window's own numbers (DevHuntSpread).
        double huntSpread = DevHuntSpread(ours, slowest.Escape), wholeHunts = Math.Max(1, catches - 1);
        double within = 2 * huntSpread / Math.Sqrt(wholeHunts);
        DevLog($"watch | what {minutes} minutes of {ours.Animal.LocalId} give by the window's numbers: a catch every " +
            $"{string.Join(" or ", readings.Select(r => $"{R(Math.Round(r.Catch.Seconds, 1))} s ({r.Name})"))}; at the slowest about {R(Math.Round(catches, 1))} catches, " +
            $"{R(Math.Round(catches * k.Tries, 1))} tries, {R(Math.Round(catches * fails, 1))} failed tries, {R(Math.Round(catches * k.Escapes, 1))} escapes, " +
            $"{R(Math.Round(catches * k.Steps, 1))} steps and {R(Math.Round(catches, 1))} hunts | each verdict needs: " +
            string.Join(", ", needs.Select((n, i) => $"{n.Count} {n.What} ({Minutes(n.PerMinute > 0 ? n.Count / n.PerMinute : 0)} on average; 9 watches of 10 reach {Low(i)})")) +
            $" | the surveys of a hunt: the two readings, {R(Math.Round(middle, 3))} and {R(Math.Round(chance, 3))} a hunt, need about {R(huntsApart)} hunts " +
            $"({(double.IsFinite(huntsApart) ? Minutes(huntsApart / perMinute) : "-")}) to be told apart: this watch says whether each fits" +
            $" | a failed try again at the reattempt chance {R(ours.ReattemptChance)}: seen at least once in about {R(failsToSee)} failed tries " +
            $"({(double.IsFinite(failsToSee) && fails > 0 ? Minutes(failsToSee / (perMinute * fails)) : "-")}) | a trap or bait kept at the preservation chance " +
            $"{R(ours.Preservation)}: seen at least once in about {R(triesToSee)} tries ({(double.IsFinite(triesToSee) ? Minutes(triesToSee / (perMinute * k.Tries)) : "-")})" +
            $" | an escape's steps: {(escapeValues.Count == 1 ? $"every reading gives {escapeValues[0]} on this animal, so it cannot tell them apart" : $"the readings give {string.Join(", ", ours.EscapeReadings!.Distinct())} here: one escape tells them apart")}" +
            $" | the time of a catch: a hunt's spread is {R(Math.Round(huntSpread, 1))} s by the window's numbers, so {R(Math.Round(wholeHunts, 1))} whole hunts hold it within " +
            $"{R(Math.Round(within, 1))} s ({R(Math.Round(100 * within / k.Seconds, 1))}%): a reading further off than that is ruled out; the links are judged closer, each on its events" +
            DevFindsReach(ours, perMinute * k.Surveys, minutes) +
            (short_.Count == 0 ? " | ENOUGH for every verdict but the surveys' readings, in 9 watches of 10"
                : $" | NOT ENOUGH for: {string.Join(", ", short_.Select(n => $"{n.What} (9 watches of 10 reach {n.Reached} of {n.Count}; ask {(n.Ask is { } ask ? ask.ToString(CultureInfo.InvariantCulture) : "more than 60")} minutes)"))}"));
    }

    // How surveying's finds can be judged in the minutes asked: their chance on every survey, the finds expected, and the
    // minutes after which no find at all would say "never" (3 expected: none then happens in 1 watch of 20).
    static string DevFindsReach(AnimalRates a, double surveysPerMinute, int minutes)
    {
        double perSurvey = a.SurveyFinds.Sum(d => d.Chance);
        if (perSurvey <= 0) return " | the survey finds: this animal's surveys find nothing";
        double expected = minutes * surveysPerMinute * perSurvey;
        return $" | the survey finds: {R(Math.Round(perSurvey, 3))} a survey, about {R(Math.Round(expected, 1))} expected in {minutes} minutes; none at all says " +
            $"\"never\" once 3 are expected ({Words.Seconds(3 / (surveysPerMinute * perSurvey) * 60)})";
    }

    // The counts of a watch of these minutes that 9 watches of 10 reach, in the order of DevWatchEnough's needs (tracking
    // steps, tries, failed tries, escapes, whole hunts plus one, catches): the hunt played 1,000 times by the window's own
    // numbers (the surveys by the chance up to the page's most, every number of steps in its range and every time in its
    // range as likely, every try catching at the trapping chance, a failed try tried again at the reattempt chance, else an
    // escape of this many steps), each watch starting at a random moment of a hunt. A fixed seed: the same numbers give
    // the same line.
    static int[] DevReach(AnimalRates a, int escape, double minutes, int runs = 1000)
    {
        var random = new Random(72);
        var counts = new List<int[]>(runs);
        double length = minutes * 60;
        double Between(double low, double high) => low + random.NextDouble() * (high - low);
        for (int run = 0; run < runs; run++)
        {
            var seen = new int[6];
            // Where the watch starts within the first hunt; events before it are not counted.
            double t = 0, start = double.NaN, catchAt = double.NaN;
            bool counting = false;
            for (int hunt = 0; hunt < 100_000 && (!counting || t < start + length); hunt++)
            {
                double huntStart = t;
                int most = Math.Max(1, (int)Math.Round(a.SurveysMost ?? 1));
                for (int k = 1; k <= most; k++)
                {
                    t += a.SurveySeconds;
                    if (k == most || random.NextDouble() < a.TrackChance) break;
                }
                int steps = random.Next(a.StepsMin, a.StepsMax + 1);
                var events = new List<(double At, int Kind)>();
                for (int i = 0; i < steps; i++) { t += Between(a.StepMinSeconds, a.StepMaxSeconds); events.Add((t, 0)); }
                while (true)
                {
                    t += Between(a.TrapMinSeconds, a.TrapMaxSeconds);
                    if (random.NextDouble() < a.TrapChance) { events.Add((t, 1)); events.Add((t, 5)); break; }
                    events.Add((t, 1));
                    events.Add((t, 2));
                    if (random.NextDouble() < a.ReattemptChance) continue;
                    events.Add((t, 3));
                    for (int i = 0; i < escape; i++) { t += Between(a.StepMinSeconds, a.StepMaxSeconds); events.Add((t, 0)); }
                }
                if (!counting)
                {
                    start = huntStart + random.NextDouble() * (t - huntStart);
                    counting = true;
                }
                foreach (var (at, kind) in events)
                    if (at >= start && at < start + length) seen[kind]++;
                // A whole hunt starts at a catch inside the watch and ends at a catch inside it.
                if (t >= start && t < start + length)
                {
                    if (!double.IsNaN(catchAt)) seen[4]++;
                    catchAt = t;
                }
            }
            counts.Add(seen);
        }
        // The order of the needs: steps, tries, failed tries, escapes, whole hunts (one more: the hunt in progress at the
        // start is not whole, so a "whole hunts" need of n is n + 1 catches; here the whole hunts are counted as such, plus
        // one), catches.
        int Tenth(int kind) => counts.Select(c => c[kind]).OrderBy(x => x).ElementAt(runs / 10);
        return [Tenth(0), Tenth(1), Tenth(2), Tenth(3), Tenth(4) + 1, Tenth(5)];
    }

    // The fewest whole minutes, in steps of five up to 60, in which 9 watches of 10 reach this need; null beyond 60.
    static int? DevAskMinutes(AnimalRates a, int escape, int need, int count)
    {
        for (int minutes = 5; minutes <= 60; minutes += 5)
            if (DevReach(a, escape, minutes, 400)[need] >= count) return minutes;
        return null;
    }

    // Every way the window could count a catch of this animal by the readings still open: both readings of the surveys of a
    // hunt, and every reading of an escape's steps (EscapeSteps), each with its name.
    static List<(string Name, int Escape, AnimalRates.Catch Catch)> DevReadings(AnimalRates a)
    {
        var all = new List<(string, int, AnimalRates.Catch)>();
        if (a.TrapFailBonuses != false) return all;
        foreach (SurveysPerHunt surveys in Enum.GetValues<SurveysPerHunt>())
            foreach (var group in Enum.GetValues<EscapeSteps>().GroupBy(reading => AnimalRates.EscapeStepsBy(reading, a)))
                if (AnimalRates.Chain(a, surveys, group.Key) is { } k)
                    all.Add(($"{surveys}, an escape {group.Key} by {string.Join("/", group)}", group.Key, k));
        return all;
    }

    static string DevWhyNot(AnimalRates a) => a.TrapFailBonuses == true ? "its trap has bonuses after a failed try"
        : a.TrapFailBonuses == null ? "its trap's bonuses after a failed try could not be read"
        : a.SurveysMost == null ? "the page's surveying range is not read" : !a.HasTime ? "the game gives no time" : "no catch would come";

    // The spread of a hunt's time (its standard deviation, in seconds) by the window's numbers, for an escape of this many
    // steps: the surveys (by the chance up to the page's most), the steps needed (every number in the range as likely),
    // the fails before a catch (each try catching at the trapping chance), the escapes among them (not tried again at the
    // reattempt chance), and every step and try anywhere in its range; the steps and the tries move together with the
    // fails. NaN when the window cannot count the animal.
    static double DevHuntSpread(AnimalRates a, double escape)
    {
        if (a.SurveysMost is not { } most || a.TrapChance <= 1e-9) return double.NaN;
        double q = a.TrapChance, r = a.ReattemptChance;
        double fails = (1 - q) / q, failsVar = (1 - q) / (q * q);
        double escapesVar = fails * r * (1 - r) + (1 - r) * (1 - r) * failsVar;
        int values = a.StepsMax - a.StepsMin + 1;
        double steps = a.StepsMiddle + escape * fails * (1 - r), stepsVar = (values * values - 1) / 12.0 + escape * escape * escapesVar;
        double step = a.StepSeconds, stepVar = Math.Pow(a.StepMaxSeconds - a.StepMinSeconds, 2) / 12;
        double trap = a.TrapSeconds, trapVar = Math.Pow(a.TrapMaxSeconds - a.TrapMinSeconds, 2) / 12;
        double survey = DevSurveySpread(a.TrackChance, most) * a.SurveySeconds;
        double variance = survey * survey + steps * stepVar + step * step * stepsVar + (fails + 1) * trapVar + trap * trap * failsVar +
            2 * step * trap * escape * (1 - r) * failsVar;
        return Math.Sqrt(Math.Max(0, variance));
    }

    // The spread of a hunt's surveys when each finds the tracks at a chance and the last of the page's range always does.
    static double DevSurveySpread(double chance, double most)
    {
        int last = Math.Max(1, (int)Math.Round(most));
        double mean = 0, square = 0, reach = 1;
        for (int k = 1; k <= last; k++)
        {
            double p = k == last ? reach : reach * chance;
            mean += k * p;
            square += k * k * p;
            reach *= 1 - chance;
        }
        return Math.Sqrt(Math.Max(0, square - mean * mean));
    }

    static int? DevSurveyFails(ActiveHuntingAnimal animal)
    {
        try { return DevNative.SurveyFails(animal); }
        catch (Exception) { return null; }
    }

    // The game's tick now (TrackerComponent.CurrentTick), or null when it cannot be read.
    long? DevTick()
    {
        try { return _hunting!.Tracker.CurrentTick; }
        catch (Exception) { return null; }
    }

    // How long one tick of the game is, in milliseconds: a tracker period's milliseconds over its ticks, and the action
    // timer's interval over its ticks beside it.
    double DevTickMs(out string words)
    {
        double byTracker = double.NaN, byTimer = double.NaN;
        try
        {
            if (_hunting!.Tracker.Periods.FirstOrDefault(p => p.CycleTicks > 0) is { } period) byTracker = period.CycleDuration / period.CycleTicks;
        }
        catch (Exception) { }
        try
        {
            var timer = _hunting!.Action.ActionTimer;
            if (timer.WaitTicks > 0) byTimer = timer.WaitTime / timer.WaitTicks;
        }
        catch (Exception) { }
        words = $"one tick {R(byTracker)} ms by the tracker, {R(byTimer)} ms by the action timer, the tick now {DevTick()?.ToString() ?? "NOT READ"}";
        return double.IsFinite(byTracker) ? byTracker : byTimer;
    }

    // The bank and every item an animal can give or use (its products, survey finds, rare extras, its traps and its bait),
    // with its count now (Woodcutting Rates' DevBankStart).
    void DevBankStart(DevHuntWatch w)
    {
        w.Bank = _game != null && _game.Entities.TryGetObjectById("MelvorBase:Bank", out GamesByMalcs.Melvor2.Entities.GameEntity? bank)
            ? bank as GamesByMalcs.Melvor2.Entities.Bank.BankEntity : null;
        if (w.Bank == null) return;
        foreach (ActiveHuntingAnimal animal in w.Action.Animals.Values)
        {
            if (animal == null) continue;
            AnimalRates a = ReadAnimal(animal);
            var items = a.Products.Select(d => d.Item).Concat(a.SurveyFinds.Select(d => d.Item)).Concat(a.Rare.SelectMany(s => s.Extras).Select(d => d.Item))
                .Concat(animal.CompatibleTraps ?? (IReadOnlyList<Item>)Array.Empty<Item>()).Append(a.Trap).Append(a.Bait);
            foreach (Item? item in items)
                if (item != null && !w.Counts.ContainsKey(item))
                {
                    int count = w.Bank.Inventory.GetQuantity(item);
                    w.Watched.Add(item);
                    w.Counts[item] = w.StartCounts[item] = count;
                }
        }
        w.LastRead = Time.GetTicksMsec();
    }

    static void DevBankRead(DevHuntWatch w, DevMoment? m)
    {
        if (w.Bank == null) return;
        w.LastRead = Time.GetTicksMsec();
        foreach (Item item in w.Watched)
        {
            int now = w.Bank.Inventory.GetQuantity(item), was = w.Counts[item];
            if (now == was) continue;
            w.Counts[item] = now;
            if (m != null) m.Items.Add((item, now - was));
            else w.Outside[item] = w.Outside.GetValueOrDefault(item) + now - was;
        }
    }

    static string DevAnimalStats(ActiveHuntingAnimal a)
    {
        HuntingActionStats s = a.Stats;
        return $"survey {R(s.SurveyInterval.Value)} ms, steps {s.TrackingSteps.Value} (base fewest {a.Animal.BaseMinTrackingSteps}), step {s.TrackingInterval.Value} ms, " +
            $"tracking chance {R(s.TrackingSuccessChance.Value)}, skip {R(s.SkipTrackingStepChance?.Total.Value ?? 0)}, trap {s.TrappingInterval.Value} ms at {R(s.TrappingSuccessChance.Value)}, " +
            $"reattempt {R(s.TrappingReattemptChance?.Total.Value ?? 0)}, rare {R(s.RareSpeciesChance.Value)}, XP {R(s.Experience.XpToAdd.Value)}, doubling " +
            $"{R(s.DoublingChance?.Total.Value ?? 0)}, preservation {R(s.PreservationChance?.Total.Value ?? 0)}, trap {a.SelectedTrap?.Id ?? "none"}, bait {a.UseBait}, trapless {a.TraplessEnabled}";
    }

    void DevWatchListen(DevHuntWatch w)
    {
        var action = w.Action;
        ExperienceComponent experience = _hunting!.Experience;
        void Safe(Action listen)
        {
            if (w.Failed || !ReferenceEquals(_devHunts, w) || GameAccess.IsComputingOffline) return;
            try { listen(); }
            catch (Exception e)
            {
                w.Failed = true;
                DevLog("watch | a listener failed, the watch ends: " + e.Message);
            }
        }
        foreach (ActiveHuntingAnimal animal in action.Animals.Values)
        {
            if (animal == null) continue;
            ActiveHuntingAnimal a = animal;
            EventHandler<ValueChangedEventArgs<HuntState>> state = (_, e) => Safe(() =>
            {
                DevMoment m = DevMomentNow(w, a);
                m.State = (m.State?.From ?? e.OldValue, e.NewValue);
                m.Order.Add("state");
            });
            EventHandler<ValueChangedEventArgs<int>> step = (_, e) => Safe(() =>
            {
                DevMoment m = DevMomentNow(w, a);
                m.Steps.Add((e.OldValue, e.NewValue));
                m.Order.Add("step");
            });
            EventHandler<ValueChangedEventArgs<int>> required = (_, e) => Safe(() =>
            {
                DevMoment m = DevMomentNow(w, a);
                m.Required = e.NewValue;
                m.Order.Add("required");
            });
            EventHandler<ValueChangedEventArgs<int>> tracks = (_, e) => Safe(() =>
            {
                DevMoment m = DevMomentNow(w, a);
                m.Tracks = e.NewValue;
                m.Order.Add("tracks");
            });
            EventHandler<ValueChangedEventArgs<int>> fails = (_, e) => Safe(() =>
            {
                DevMoment m = DevMomentNow(w, a);
                m.TrapFails = (m.TrapFails?.From ?? e.OldValue, e.NewValue);
                m.Order.Add("trap fails");
                // The trapping chance after a failed try, and what the failed try has put on the animal: the trap's fail
                // bonuses may change the chance from one try to the next (code review 1, fault 7).
                if (e.NewValue > e.OldValue) DevLog($"hunt | {a.Animal.LocalId} after a failed try: {DevTrapNow(a)}");
            });
            EventHandler<HuntingAnimalEventArgs> trapped = (_, _) => Safe(() =>
            {
                DevMoment m = DevMomentNow(w, a);
                m.Trapped = true;
                m.Order.Add("TRAPPED");
            });
            EventHandler<HuntingAnimalEventArgs> escaped = (_, _) => Safe(() =>
            {
                DevMoment m = DevMomentNow(w, a);
                m.Escaped = true;
                m.Order.Add("ESCAPED");
            });
            a.StateChanged += state;
            a.CurrentStepChanged += step;
            a.RequiredStepsChanged += required;
            a.TracksChanged += tracks;
            a.TrapFailCountChanged += fails;
            a.AnimalTrapped += trapped;
            a.AnimalEscaped += escaped;
            w.Off.Add(() =>
            {
                a.StateChanged -= state;
                a.CurrentStepChanged -= step;
                a.RequiredStepsChanged -= required;
                a.TracksChanged -= tracks;
                a.TrapFailCountChanged -= fails;
                a.AnimalTrapped -= trapped;
                a.AnimalEscaped -= escaped;
            });
            if (a.JournalEntry?.Experience is { } journal)
            {
                EventHandler<ValueChangedEventArgs<double>> journalXp = (_, e) => Safe(() =>
                {
                    DevMoment m = DevMomentNow(w, a);
                    m.JournalXp += e.NewValue - e.OldValue;
                    m.JournalXpChanges++;
                    m.Order.Add("journal xp");
                });
                journal.XpChanged += journalXp;
                w.Off.Add(() => journal.XpChanged -= journalXp);
            }
        }
        EventHandler<ValueChangedEventArgs<double>> xp = (_, e) => Safe(() =>
        {
            DevMoment m = DevMomentNow(w, action.CurrentAnimal);
            m.Xp += e.NewValue - e.OldValue;
            m.XpChanges++;
            m.Order.Add("xp");
        });
        EventHandler<ItemAddedEventArgs> item = (_, e) => Safe(() =>
        {
            if (e.Item == null) return;
            DevMoment m = DevMomentNow(w, action.CurrentAnimal);
            m.Added.Add((e.Item, e.Quantity));
            m.Order.Add("item");
        });
        EventHandler<ValueChangedEventArgs<ActiveHuntingAnimal>> hunted = (_, e) => Safe(() =>
        {
            DevMoment m = DevMomentNow(w, e.NewValue);
            m.Other.Add($"the animal hunted {e.NewValue?.Animal.Id ?? "none"}");
            m.Order.Add("animal");
            w.AnimalChanged = true;
        });
        EventHandler<ValueChangedEventArgs<Item>> trap = (_, e) => Safe(() =>
        {
            DevMoment m = DevMomentNow(w, action.CurrentAnimal);
            m.Other.Add($"the trap in use {e.NewValue?.Id ?? "none"}");
            m.Order.Add("trap");
        });
        experience.XpChanged += xp;
        action.ItemAdded += item;
        action.CurrentAnimalChanged += hunted;
        action.CurrentTrapChanged += trap;
        w.Off.Add(() =>
        {
            experience.XpChanged -= xp;
            action.ItemAdded -= item;
            action.CurrentAnimalChanged -= hunted;
            action.CurrentTrapChanged -= trap;
        });
    }

    DevMoment DevMomentNow(DevHuntWatch w, ActiveHuntingAnimal? animal)
    {
        long frame = (long)Engine.GetProcessFrames();
        if (w.Open is { } open && open.Frame == frame) { open.Animal ??= animal; return open; }
        DevFlushMoment(w);
        w.LastEvent = Time.GetTicksMsec();
        return w.Open = new DevMoment { Frame = frame, At = Time.GetTicksMsec(), Tick = DevTick(), Animal = animal };
    }

    // What runs from this frame on, for the link that ends at the next event: the interval of the game's action timer, the
    // page's ranges and chances, the steps needed.
    void DevRemember(DevHuntWatch w, ActiveHuntingAnimal a, DevMoment? m)
    {
        HuntingActionStats s = a.Stats;
        w.LastAt = m?.At ?? Time.GetTicksMsec();
        w.LastTick = m?.Tick ?? DevTick();
        w.LastEventFrame = m?.Frame ?? (long)Engine.GetProcessFrames();
        w.HaveLast = true;
        // The next link's interval is read in the frames after this one (DevWatchFrame).
        w.LastSet = double.NaN;
        var (stepMin, stepMax) = s.TrackingInterval.Value;
        var (trapMin, trapMax) = s.TrappingInterval.Value;
        w.LastStep = (stepMin / 1000, stepMax / 1000);
        w.LastTrap = (trapMin / 1000, trapMax / 1000);
        w.LastChance = s.TrappingSuccessChance.Value;
        w.LastReattempt = s.TrappingReattemptChance?.Total.Value ?? 0;
        w.LastPreservation = s.PreservationChance?.Total.Value ?? 0;
        w.LastRequired = a.RequiredSteps;
    }

    void DevFlushMoment(DevHuntWatch w)
    {
        if (w.Open is not { } m) return;
        w.Open = null;
        DevBankRead(w, m);
        if (w.Moments.Count < 20000) w.Moments.Add(m);
        ActiveHuntingAnimal? a = m.Animal;
        double real = (m.At - w.LastLine) / 1000.0;
        w.LastLine = m.At;
        // The link that ends in this frame: from the last frame with an event, on the real clock and on the game's.
        double linkReal = w.HaveLast ? (m.At - w.LastAt) / 1000.0 : double.NaN;
        double linkGame = w.HaveLast && m.Tick is { } tick && w.LastTick is { } lastTick && double.IsFinite(w.TickMs) ? (tick - lastTick) * w.TickMs / 1000 : double.NaN;
        // The interval the game's action timer ran for the link that ends here, read in the frames between (DevWatchFrame).
        double set = w.LastSet;
        string said = "";
        if (a != null)
        {
            DevSeen seen = w.Seen.TryGetValue(a, out DevSeen? known) ? known : w.Seen[a] = new DevSeen { Animal = a };
            said = DevJudgeMoment(w, seen, m, linkReal, linkGame);
            DevRemember(w, a, m);
        }
        DevLog($"hunt | +{real:0.###} s{(double.IsFinite(linkGame) ? $" (the game's {linkGame:0.###} s)" : "")} | {a?.Animal.LocalId ?? "?"}" +
            (m.State is { } s ? $" | state {s.From} -> {s.To}" : "") +
            (m.Steps.Count > 0 ? $" | step {string.Join(", ", m.Steps.Select(x => $"{x.From} -> {x.To}"))}" : "") + (m.Required is { } r ? $" | steps needed {r}" : "") +
            (m.Tracks is { } t ? $" | tracks {t}" : "") + (m.TrapFails is { } f ? $" | trap fails {f.To}" : "") + (m.Trapped ? " | CAUGHT" : "") + (m.Escaped ? " | ESCAPED" : "") +
            $" | events [{string.Join(" ", m.Order)}]" + (m.XpChanges > 0 ? $" | XP +{R(m.Xp)} in {m.XpChanges}" : "") + (m.JournalXpChanges > 0 ? $" | journal XP +{R(m.JournalXp)}" : "") +
            (m.Items.Count > 0 ? $" | the bank's counts [{string.Join(", ", m.Items.Select(i => $"{i.Item.Id} {i.Quantity:+0;-0}, now {w.Counts.GetValueOrDefault(i.Item)}"))}]" : "") +
            (m.Added.Count > 0 ? $" | ItemAdded [{string.Join(", ", m.Added.Select(i => $"{i.Item.Id} x{i.Quantity}"))}]" : "") +
            (m.Other.Count > 0 ? " | " + string.Join(", ", m.Other) : "") + (a != null ? $" | survey fails {DevSurveyFails(a)?.ToString() ?? "-"}" : "") +
            (double.IsFinite(set) ? $" | the action timer ran {set:0.###} s for it" : "") + said);
    }

    // What one frame of the hunted animal was, by its events, and what it adds to what was seen: a step, a try of the trap
    // (caught, escaped, or tried again), an escape, the end of surveying, a catch. Returns what the line adds.
    string DevJudgeMoment(DevHuntWatch w, DevSeen seen, DevMoment m, double linkReal, double linkGame)
    {
        ActiveHuntingAnimal a = seen.Animal;
        var words = new List<string>();
        DevHuntSeen hunt = seen.Now;
        int trapDelta = a.SelectedTrap is { } trapItem ? m.Items.Where(i => ReferenceEquals(i.Item, trapItem)).Sum(i => i.Quantity) : 0;
        int baitDelta = a.Animal.PreferredBait is { } baitItem ? m.Items.Where(i => ReferenceEquals(i.Item, baitItem)).Sum(i => i.Quantity) : 0;

        // Steps: moved on (by one, or more: a skipped step), back (not the reset to 0 of a catch).
        int by = 0;
        foreach (var (from, to) in m.Steps)
        {
            if (to > from) by += to - from;
            else if (m.Trapped && to == 0) seen.Resets++;
            else if (to < from) { seen.StepsBack++; words.Add($"A STEP WENT BACK {from} -> {to}"); }
        }
        if (by > 0)
        {
            seen.Steps.Add((new DevLink(linkReal, linkGame, w.LastSet, w.LastStep.Min, w.LastStep.Max), by));
            hunt.Steps += by;
            hunt.Middles += by * (w.LastStep.Min + w.LastStep.Max) / 2;
            hunt.LinkVariance += by * Math.Pow(w.LastStep.Max - w.LastStep.Min, 2) / 12;
            if (by > 1) words.Add($"MOVED ON {by} STEPS");
            if (m.JournalXpChanges > 0) seen.JournalStep.Add(m.JournalXp / by);
        }

        // The end of surveying: the surveys of the hunt by the game's own count of failed surveys, and the steps it needs.
        if (m.State is { From: HuntState.Surveying, To: not HuntState.Surveying })
        {
            if (DevSurveyFails(a) is { } failed) hunt.Surveys = failed + 1;
            hunt.SurveyEach = a.Stats.SurveyInterval.Value / 1000;
            hunt.SurveyReal = hunt.Whole ? (m.At - hunt.StartAt) / 1000.0 : double.NaN;
            hunt.SurveyGame = hunt.Whole && m.Tick is { } tick && hunt.StartTick is { } start && double.IsFinite(w.TickMs) ? (tick - start) * w.TickMs / 1000 : double.NaN;
            var (fewest, most) = a.Stats.TrackingSteps.Value;
            (hunt.Required, hunt.RequiredMin, hunt.RequiredMax) = (a.RequiredSteps, fewest, most);
            words.Add($"surveys {hunt.Surveys?.ToString() ?? "NOT READ"}, steps needed {a.RequiredSteps} of {fewest}..{most}");
        }

        // A try of the trap: caught, escaped, or failed and tried again (a fail with no escape and no change of state).
        string? outcome = m.Trapped ? "caught" : m.Escaped ? "escaped" : m.TrapFails is { } tf && tf.To > tf.From && m.State == null ? "TRIED AGAIN" : null;
        if (outcome != null)
        {
            var link = new DevLink(linkReal, linkGame, w.LastSet, w.LastTrap.Min, w.LastTrap.Max);
            seen.Tries.Add(new DevTry(outcome, link, w.LastChance, w.LastReattempt, w.LastPreservation, trapDelta, baitDelta, a.UseBait));
            hunt.Tries++;
            hunt.Middles += (w.LastTrap.Min + w.LastTrap.Max) / 2;
            hunt.LinkVariance += Math.Pow(w.LastTrap.Max - w.LastTrap.Min, 2) / 12;
            words.Add($"a try {outcome}, the trap {trapDelta:+0;-0}, the bait {baitDelta:+0;-0}");
        }
        else
        {
            seen.TrapElsewhere += trapDelta;
            seen.BaitElsewhere += baitDelta;
            if (trapDelta != 0 || baitDelta != 0) words.Add($"TRAP OR BAIT USED OUTSIDE A TRY ({trapDelta:+0;-0}, {baitDelta:+0;-0})");
        }

        // An escape: where it went and how many steps it added, against every reading of EscapeSteps.
        if (m.Escaped)
        {
            int now = m.Required ?? a.RequiredSteps;
            var (fewest, most) = a.Stats.TrackingSteps.Value;
            // Every reading the window offers (AnimalRates.EscapeStepsBy), from the numbers at this escape.
            var readings = Enum.GetValues<EscapeSteps>().ToDictionary(r => r, r =>
                AnimalRates.EscapeStepsBy(r, fewest, most, a.Animal.BaseMinTrackingSteps, Math.Max(a.Animal.BaseMinTrackingSteps, a.Animal.BaseMaxTrackingSteps)));
            seen.Escapes.Add(new DevEscape(w.LastRequired, now, m.State?.To, readings));
            hunt.Escapes++;
            words.Add($"an escape to {m.State?.To.ToString() ?? "NO CHANGE OF STATE"}, steps needed {w.LastRequired} -> {now} (+{now - w.LastRequired}; the readings " +
                $"{string.Join(", ", readings.Select(r => $"{r.Key} {r.Value}"))})");
        }

        // A catch: its XP against the page's range, its journal XP, its output against its share of the XP; the hunt ends,
        // and the next one starts here.
        if (m.Trapped)
        {
            AnimalRates ours = ReadAnimal(a);
            double each = a.Stats.Experience.XpToAdd.Value;
            var (low, high) = ours.XpRange ?? (double.NaN, double.NaN);
            int? level = a.JournalEntry?.Experience?.Level;
            double stat = double.NaN;
            // Which end of the journal's list is the newest is not known by the names: the end nearer this catch's share is taken,
            // and the line says which.
            string end = "-";
            try
            {
                if (a.JournalEntry?.RecentStatEntries is { Count: > 0 } entries && each > 0)
                {
                    double first = entries[0].StatPercentage, last = entries[^1].StatPercentage, share = m.Xp / each;
                    (stat, end) = Math.Abs(last - share) <= Math.Abs(first - share) ? (last, "last") : (first, "first");
                }
            }
            catch (Exception) { }
            seen.CatchXp.Add((m.Xp, each, low, high, level, stat));
            words.Add($"its share of the animal's XP {R(Math.Round(m.Xp / each, 4))} at animal level {level?.ToString() ?? "-"}, the journal's size {R(Math.Round(stat, 4))} ({end} of its list)");
            if (m.JournalXpChanges > 0) seen.JournalCatch.Add(m.JournalXp);
            var output = new Dictionary<Item, int>(ReferenceEqualityComparer.Instance);
            foreach (var (item, quantity) in m.Items)
                if (quantity > 0) output[item] = output.GetValueOrDefault(item) + quantity;
            seen.Catches.Add((output, each > 0 ? m.Xp / each : double.NaN));
            if (hunt.Whole)
            {
                hunt.Real = (m.At - hunt.StartAt) / 1000.0;
                hunt.Game = m.Tick is { } tick && hunt.StartTick is { } start && double.IsFinite(w.TickMs) ? (tick - start) * w.TickMs / 1000 : double.NaN;
                seen.Hunts.Add(hunt);
                words.Add($"a whole hunt of {R(Math.Round(hunt.Real, 3))} s ({R(Math.Round(hunt.Game, 3))} s on the game's clock): {hunt.Surveys?.ToString() ?? "-"} surveys, " +
                    $"{hunt.Steps} steps, {hunt.Tries} tries, {hunt.Escapes} escapes");
            }
            seen.Now = new DevHuntSeen { Whole = true, StartAt = m.At, StartTick = m.Tick };
        }
        else
        {
            if (m.XpChanges > 0) { seen.XpElsewhere++; seen.XpElsewhereSum += m.Xp; words.Add("XP OUTSIDE A CATCH"); }
            if (m.JournalXpChanges > 0 && by == 0) { seen.JournalElsewhere++; words.Add("JOURNAL XP OUTSIDE A STEP OR A CATCH"); }
            foreach (var (item, quantity) in m.Items)
                if (quantity > 0) seen.OutputElsewhere[item] = seen.OutputElsewhere.GetValueOrDefault(item) + quantity;
        }
        return words.Count == 0 ? "" : " | " + string.Join("; ", words);
    }

    // On every process frame while the watch listens: the frame with an event is closed as soon as it is past (its bank
    // counts read right after it), and the interval the game's action timer runs is read in the frames between two events,
    // where it can only be the one of the link in progress: at an event the game may have set the next one already, or
    // not yet, and nothing tells which.
    void DevWatchFrame(long frame)
    {
        if (_devHunts is not { } w || w.Failed) return;
        try
        {
            if (w.Open != null && w.Open.Frame != frame) DevFlushMoment(w);
            if (w.Open == null && frame > w.LastEventFrame) w.LastSet = w.Action.ActionTimer.WaitTime / 1000;
        }
        catch (Exception e)
        {
            w.Failed = true;
            DevLog("watch | reading a frame failed, the watch ends: " + e.Message);
        }
    }

    void DevWatchTick()
    {
        if (_devHunts is not { } w) return;
        if (w.Open != null && w.Open.Frame != (long)Engine.GetProcessFrames()) DevFlushMoment(w);
        ulong now = Time.GetTicksMsec();
        if (w.Open == null && now - w.LastRead >= BankReadEvery) DevBankRead(w, null);
        // The Hunting page's surveying and XP ranges, read whenever the page is on the screen during the watch (ReadPage only
        // reads), not only at its start and end (review 6).
        try { ReadPage(); }
        catch (Exception e) { DevLog($"watch | the page could not be read: {e.GetType().Name}: {e.Message}"); }
        if (w.Failed) DevWatchEnd("a listener failed");
        else if (now - w.Since >= w.Length) DevWatchEnd($"the {w.Length / 60000} minutes are up");
        else if (now - w.LastEvent >= WatchIdle) DevWatchEnd($"two minutes without an event of a hunt (hunting {(w.Action.IsStarted ? "started" : "stopped")})");
    }

    void DevUnwatchHunts()
    {
        if (_devHunts is not { } w) return;
        foreach (Action off in w.Off)
        {
            try { off(); }
            catch (Exception) { }
        }
        w.Off.Clear();
    }

    // The summary: for every animal hunted, one line per question with its verdict; then the tracker, then the last line.
    void DevWatchEnd(string why, bool summarise = true)
    {
        if (_devHunts is not { } w) return;
        double seconds = (Time.GetTicksMsec() - w.Since) / 1000.0;
        var verdicts = new List<string>();
        try
        {
            // A frame still open is judged only when the watch ends on its own; on another character or a stopped mod it is
            // dropped with one line, since the game read now is no longer the one it was seen on.
            if (summarise) DevFlushMoment(w);
            else if (w.Open != null) DevLog("watch | the last frame with an event is not judged: the watch ends on another character or a stopped mod");
        }
        catch (Exception e) { verdicts.Add($"the last frame failed: {e.GetType().Name}: {e.Message}"); }
        w.Open = null;
        DevUnwatchHunts();
        _devHunts = null;
        try
        {
            DevLog($"watch | ended after {seconds:0} s: {why}; {w.Moments.Count} frames with an event; animals hunted [{string.Join(", ", w.Seen.Keys.Select(k => k.Animal.LocalId))}]" +
                (w.AnimalChanged ? "; the animal hunted CHANGED during the watch" : "; one animal throughout"));
            DevItemsSummary(w);
            if (!summarise || _hunting == null) verdicts.Add("NO SUMMARY");
            else
            {
                ReadPage();
                foreach (DevSeen seen in w.Seen.Values.OrderByDescending(s => s.Tries.Count))
                {
                    AnimalRates ours = ReadAnimal(seen.Animal);
                    var said = DevHuntSummary(w, seen, ours, w.Start.GetValueOrDefault(seen.Animal, "not read"));
                    verdicts.Add($"{seen.Animal.Animal.LocalId}: {string.Join(" / ", said)}");
                }
                DevTracker("at the end", w, seconds);
            }
        }
        catch (Exception e) { verdicts.Add($"the summary failed: {e.GetType().Name}: {e.Message}"); }
        string result = $"watched {seconds:0} s ({why}); {string.Join(" | ", verdicts)}; stopped listening, nothing changed";
        DevLog("watch | " + result);
        File.WriteAllText(Path.Combine(_ctx.Directory, WatchDoneFile), result);
    }

    // A mean and twice its standard error (from its own spread); NaN for the tolerance with fewer than two values.
    static (double Mean, double Tolerance, double Sd) DevMean(IEnumerable<double> values)
    {
        var list = values.Where(double.IsFinite).ToList();
        if (list.Count == 0) return (double.NaN, double.NaN, double.NaN);
        double mean = list.Average();
        if (list.Count < 2) return (mean, double.NaN, double.NaN);
        double sd = Math.Sqrt(list.Sum(v => (v - mean) * (v - mean)) / (list.Count - 1));
        return (mean, 2 * sd / Math.Sqrt(list.Count), sd);
    }

    // A count of events at a chance against what was seen: twice the standard error of the count, at least one.
    static (double Expected, double Tolerance) DevCount(IEnumerable<double> chances)
    {
        var list = chances.ToList();
        double expected = list.Sum(), variance = list.Sum(p => p * (1 - p));
        return (expected, Math.Max(1, 2 * Math.Sqrt(variance)));
    }

    // Every question about one animal, each with its verdict in capitals.
    List<string> DevHuntSummary(DevHuntWatch w, DevSeen seen, AnimalRates ours, string start)
    {
        var verdicts = new List<string>();
        string id = ours.Animal.LocalId;
        void Say(string question, string verdict, string detail)
        {
            DevLog($"watch | {id} | {question} | {verdict} | {detail}");
            verdicts.Add(verdict);
        }
        string N(double value, int digits = 3) => R(Math.Round(value, digits));
        int caught = seen.Tries.Count(t => t.Outcome == "caught");
        DevLog($"watch | {id} | {seen.Hunts.Count} whole hunts, {seen.Steps.Count} frames with a step, {seen.Tries.Count} tries ({caught} caught), {seen.Escapes.Count} escapes; " +
            $"its numbers at the start {start}, now {DevAnimalStats(ours.Active)}");

        // The surveys of a hunt, by the game's own count of failed surveys: never more than the page's most (a link that
        // holds on every hunt), and their mean against both readings, within twice its standard error.
        var surveyed = seen.Hunts.Concat([seen.Now]).Where(h => h.Surveys != null).ToList();
        var counts = surveyed.Select(h => (double)h.Surveys!.Value).ToList();
        var (surveyMean, surveyTol, _) = DevMean(counts);
        double middle = ours.SurveysMost is { } most ? (1 + most) / 2 : double.NaN;
        double chance = ours.SurveysMost is { } most2 ? AnimalRates.SurveysUpToMost(ours.TrackChance, most2) : double.NaN;
        int over = ours.SurveysMost is { } most3 ? counts.Count(c => c > most3 + 1e-6) : 0;
        var surveyFits = new List<string>();
        if (double.IsFinite(surveyTol) && Math.Abs(surveyMean - middle) <= surveyTol) surveyFits.Add("THE MIDDLE OF THE PAGE'S RANGE");
        if (double.IsFinite(surveyTol) && Math.Abs(surveyMean - chance) <= surveyTol) surveyFits.Add("THE CHANCE UP TO THE PAGE'S MOST");
        Say("the surveys of a hunt", counts.Count < FewestHunts ? $"NOT DECIDED ({counts.Count} HUNTS SURVEYED)" : ours.SurveysMost == null ? "NOT DECIDED (THE PAGE'S RANGE NOT READ)"
                : (over > 0 ? $"{over} HUNTS TOOK MORE SURVEYS THAN THE PAGE'S MOST, " : "NO HUNT TOOK MORE THAN THE PAGE'S MOST, ") +
                  (surveyFits.Count == 2 ? "BOTH READINGS FIT" : surveyFits.Count == 1 ? $"{surveyFits[0]} FITS" : "NO READING FITS"),
            $"[{string.Join(", ", counts)}], {N(surveyMean)} a hunt (twice its standard error {N(surveyTol)}); the middle of the page's range {N(middle)}, " +
            $"the tracking chance {R(ours.TrackChance)} up to the page's most {(ours.SurveysMost is { } m4 ? N(m4) : "NOT READ")} {N(chance)}; a survey took " +
            $"{N(DevMean(surveyed.Where(h => h.Surveys > 0).Select(h => h.SurveyReal / h.Surveys!.Value)).Mean)} s on the real clock, " +
            $"{N(DevMean(surveyed.Where(h => h.Surveys > 0).Select(h => h.SurveyGame / h.Surveys!.Value)).Mean)} s on the game's, against {R(ours.SurveySeconds)} s");

        // A tracking step: moved on by one, never back (the resets after a catch apart), none skipped: judged on every step.
        int steps = seen.Steps.Count, more = seen.Steps.Count(s => s.By > 1);
        Say("a tracking step", steps < FewestSteps ? $"NOT DECIDED ({steps} STEPS)"
                : seen.StepsBack > 0 ? $"{seen.StepsBack} STEPS WENT BACK" : more > 0 ? $"{more} OF {steps} MOVED ON MORE THAN ONE STEP" : "EVERY STEP MOVED ON ONE, NONE SKIPPED",
            $"{steps} frames with a step, {seen.Steps.Sum(s => s.By)} steps moved on, {more} by more than one, {seen.StepsBack} back, {seen.Resets} resets to 0 with a catch (not " +
            $"counted as back); the skip chance {R(ours.SkipChance)}: {N(steps * ours.SkipChance, 2)} skipped steps expected");
        DevLinkTimes("the time of a step", steps < FewestSteps, seen.Steps.Select(s => s.Time).Where((_, i) => seen.Steps[i].By == 1).ToList(), Say);

        // The steps a hunt needs, at its start: within the game's range, and their mean against its middle.
        var needed = surveyed.Where(h => h.Required != null).ToList();
        var (neededMean, neededTol, _) = DevMean(needed.Select(h => (double)h.Required!.Value));
        double neededMiddle = needed.Count == 0 ? double.NaN : needed.Average(h => (h.RequiredMin!.Value + h.RequiredMax!.Value) / 2.0);
        int outside = needed.Count(h => h.Required < h.RequiredMin || h.Required > h.RequiredMax);
        Say("the steps a hunt needs", needed.Count < FewestHunts ? $"NOT DECIDED ({needed.Count} HUNTS)"
                : (outside > 0 ? $"{outside} OUTSIDE THE GAME'S RANGE, " : "ALL IN THE GAME'S RANGE, ") +
                  (double.IsFinite(neededTol) && Math.Abs(neededMean - neededMiddle) <= neededTol ? "AS ITS MIDDLE" : "NOT AS ITS MIDDLE"),
            $"[{string.Join(", ", needed.Select(h => h.Required))}], {N(neededMean)} a hunt (twice its standard error {N(neededTol)}) against the middle {N(neededMiddle)}");

        // A try: caught at the trapping chance (a count against its chances), and its time.
        var tries = seen.Tries;
        var (caughtExpected, caughtTol) = DevCount(tries.Select(t => t.Chance));
        Say("a try of the trap", tries.Count < FewestTries ? $"NOT DECIDED ({tries.Count} TRIES)"
                : Math.Abs(caught - caughtExpected) <= caughtTol ? "CAUGHT AS THE TRAPPING CHANCE" : "NOT AS THE TRAPPING CHANCE",
            $"{caught} of {tries.Count} caught, {N(caughtExpected, 2)} expected at the trapping chance (twice its standard error {N(caughtTol, 2)})");
        DevLinkTimes("the time of a try", tries.Count < FewestTries, tries.Select(t => t.Time).ToList(), Say);

        // A failed try: tried again at the reattempt chance, otherwise an escape.
        var failed = tries.Where(t => t.Outcome != "caught").ToList();
        int again = failed.Count(t => t.Outcome == "TRIED AGAIN");
        var (againExpected, againTol) = DevCount(failed.Select(t => t.Reattempt));
        Say("a failed try", failed.Count < FewestFails ? $"NOT DECIDED ({failed.Count} FAILED TRIES)"
                : Math.Abs(again - againExpected) <= againTol ? (again == 0 ? "AS THE REATTEMPT CHANCE (NEVER TRYING AGAIN FITS TOO)" : "TRIED AGAIN AS THE REATTEMPT CHANCE")
                : "NOT AS THE REATTEMPT CHANCE",
            $"{failed.Count} failed tries: {again} tried again, {failed.Count - again} escaped; {N(againExpected, 2)} tried again expected at the reattempt chance " +
            $"(twice its standard error {N(againTol, 2)})");

        // An escape: back to tracking, with the steps of one reading of EscapeSteps, judged on every escape.
        var escapes = seen.Escapes;
        bool allTracking = escapes.All(e => e.After == HuntState.Tracking);
        var fitting = Enum.GetValues<EscapeSteps>().Where(r => escapes.All(e => e.Now - e.Was == e.Readings[r])).ToList();
        bool apart = escapes.Any(e => e.Readings.Values.Distinct().Count() > 1);
        // The readings that fit and still tie: they give the same number at every escape seen, so this animal cannot tell
        // them apart; the verdict names each group, and a fixed 4 left alone is a rule only on two animals (review 6).
        var ties = fitting.GroupBy(r => string.Join(",", escapes.Select(e => e.Readings[r]))).Select(g => string.Join(" = ", g.Select(f => f.ToString().ToUpperInvariant()))).ToList();
        string fourAlone = fitting.SequenceEqual([EscapeSteps.Four]) ? "; ONLY THE FIXED 4 FITS: A RULE ONLY ONCE TWO ANIMALS WITH OTHER STEP RANGES SHOW IT" : "";
        Say("an escape", escapes.Count < FewestEscapes ? $"NOT DECIDED ({escapes.Count} ESCAPES)"
                : (allTracking ? "EVERY ESCAPE WENT BACK TO TRACKING, " : "AN ESCAPE WENT ELSEWHERE, ") +
                  (fitting.Count == 0 ? "NO READING FITS EVERY ESCAPE" : !apart ? "THE READINGS CANNOT BE TOLD APART ON THIS ANIMAL"
                      : $"READINGS THAT FIT EVERY ESCAPE: {string.Join(", ", fitting.Select(f => f.ToString().ToUpperInvariant()))}" +
                        (ties.Count < fitting.Count || fitting.Count > 1 ? $"; STILL TIED HERE: {string.Join("; ", ties)}" : "") + fourAlone),
            $"{escapes.Count} escapes, followed by [{string.Join(", ", escapes.Select(e => e.After?.ToString() ?? "no change"))}], steps added [{string.Join(", ", escapes.Select(e => e.Now - e.Was))}]; " +
            $"the readings here [{string.Join(", ", Enum.GetValues<EscapeSteps>().Select(r => $"{r} {(escapes.Count > 0 ? escapes[^1].Readings[r] : AnimalRates.EscapeStepsBy(r, ours))}"))}]");

        // Traps and bait: one each on every try (a link judged on every try), and what is kept against the preservation chance.
        string Used(string what, Func<DevTry, int> delta, IEnumerable<DevTry> on, int elsewhere)
        {
            var list = on.ToList();
            int one = list.Count(t => delta(t) == -1), kept = list.Count(t => delta(t) == 0), other = list.Count - one - kept;
            var (keptExpected, keptTol) = DevCount(list.Select(t => t.Preservation));
            string verdict = list.Count < FewestTries ? $"NOT DECIDED ({list.Count} TRIES)"
                : (other > 0 ? $"{other} TRIES USED ANOTHER NUMBER OF {what.ToUpperInvariant()}" : $"ONE {what.ToUpperInvariant()} A TRY") +
                  (Math.Abs(kept - keptExpected) <= keptTol ? (kept == 0 ? ", NONE KEPT, AS THE PRESERVATION CHANCE ALLOWS" : ", KEPT AS THE PRESERVATION CHANCE") : ", NOT KEPT AS THE PRESERVATION CHANCE") +
                  (elsewhere != 0 ? $", {elsewhere} OUTSIDE A TRY" : "");
            Say(what, verdict, $"{list.Count} tries: {one} used one, {kept} used none, {other} another number; {N(keptExpected, 2)} kept expected at the preservation chance " +
                $"(twice its standard error {N(keptTol, 2)}); {elsewhere} used outside a try");
            return verdict;
        }
        if (!ours.Trapless) Used("trap", t => t.Trap, tries, seen.TrapElsewhere);
        else Say("trap", "NOT DECIDED (HUNTED WITHOUT A TRAP)", "");
        if (tries.Any(t => t.BaitOn)) Used("bait", t => t.Bait, tries.Where(t => t.BaitOn), seen.BaitElsewhere);
        else Say("bait", "NOT DECIDED (NO BAIT USED)", "");

        // XP: with a catch only (judged on every frame), every catch's XP within the page's range, the mean against its middle.
        var xps = seen.CatchXp;
        int inRange = xps.Count(x => double.IsFinite(x.Low) && x.Xp >= x.Each * x.Low - 0.5 && x.Xp <= x.Each * x.High + 0.5);
        var (shareMean, shareTol, _) = DevMean(xps.Select(x => x.Xp / x.Each));
        double rangeMiddle = ours.XpRange is { } xr ? (xr.Low + xr.High) / 2 : double.NaN;
        Say("XP", xps.Count < FewestCatches ? $"NOT DECIDED ({xps.Count} CATCHES)"
                : (seen.XpElsewhere > 0 ? "XP OUTSIDE A CATCH TOO, " : "XP ONLY WITH A CATCH, ") +
                  (!double.IsFinite(rangeMiddle) ? "THE PAGE'S RANGE NOT READ" : inRange == xps.Count ? "EVERY CATCH IN THE PAGE'S RANGE" : $"{xps.Count - inRange} CATCHES OUTSIDE THE PAGE'S RANGE") +
                  (double.IsFinite(shareTol) && double.IsFinite(rangeMiddle) ? Math.Abs(shareMean - rangeMiddle) <= shareTol ? ", AS ITS MIDDLE" : ", NOT AS ITS MIDDLE" : ""),
            $"catches [{string.Join(", ", xps.Select(x => N(x.Xp, 1)))}], a catch's XP over the animal's {N(shareMean)} on average (twice its standard error {N(shareTol)}) against the " +
            $"middle of the page's multipliers {N(rangeMiddle)}; {seen.XpElsewhere} frames with XP outside a catch, {N(seen.XpElsewhereSum, 1)} XP");
        // The page says "Animal Levels increase the average Skill XP & Item quantity output.": the share of each catch beside
        // the animal's level and the size the journal recorded, so that a later turn can count what a level adds.
        var levels = xps.Select(x => x.Level).Distinct().ToList();
        int sameAsStat = xps.Count(x => double.IsFinite(x.Stat) && Math.Abs(x.Xp / x.Each - x.Stat) <= 0.005);
        Say("XP and the animal's level", xps.Count < FewestCatches ? $"NOT DECIDED ({xps.Count} CATCHES)"
                : (double.IsFinite(shareTol) && double.IsFinite(rangeMiddle) && Math.Abs(shareMean - rangeMiddle) <= shareTol ? "THE MIDDLE HOLDS" : "THE MIDDLE DOES NOT HOLD") +
                  $" AT ANIMAL LEVEL {string.Join(" TO ", new[] { levels.Min(), levels.Max() }.Distinct().Select(l => l?.ToString() ?? "-"))}" +
                  (sameAsStat == xps.Count ? ", A CATCH'S SHARE IS THE SIZE THE JOURNAL RECORDED" : ""),
            $"[{string.Join(", ", xps.Select(x => $"{N(x.Xp / x.Each)} at {x.Level?.ToString() ?? "-"}, size {N(x.Stat)}"))}]; the share equals the journal's size on {sameAsStat} of {xps.Count}");
        Say("journal XP", seen.JournalStep.Count + seen.JournalCatch.Count < FewestCatches ? "NOT DECIDED"
                : seen.JournalStep.All(j => Math.Abs(j - ours.JournalTrackingXp) < 1e-6) && seen.JournalCatch.All(j => Math.Abs(j - ours.JournalTrappingXp) < 1e-6) && seen.JournalElsewhere == 0
                    ? "THE GAME'S BASES, A STEP AND A CATCH" : "NOT THE GAME'S BASES",
            $"a step [{string.Join(", ", seen.JournalStep.Distinct().Select(j => N(j)))}] on {seen.JournalStep.Count} frames, a catch [{string.Join(", ", seen.JournalCatch.Distinct().Select(j => N(j)))}] " +
            $"on {seen.JournalCatch.Count}, {seen.JournalElsewhere} elsewhere; the game's bases {ours.JournalTrackingXp} and {ours.JournalTrappingXp}");

        // The survey finds: what the bank gained of each item this animal's surveys can find, over the whole watch, against
        // the surveys counted by the game's own count and each item's chance (a count at its chance: twice its spread, at
        // least one). No find where 3 or more were expected is "never"; where fewer were, nothing is decided.
        int surveysCounted = surveyed.Sum(h => h.Surveys ?? 0);
        double findsExpected = surveysCounted * ours.SurveyFinds.Sum(d => d.Chance * d.Each);
        int findsSeen = ours.SurveyFinds.Select(d => d.Item).Distinct().Sum(item => w.Bank == null ? 0 : Math.Max(0, w.Counts.GetValueOrDefault(item) - w.StartCounts.GetValueOrDefault(item)));
        double findsTol = Math.Max(1, 2 * Math.Sqrt(findsExpected));
        Say("the survey finds", ours.SurveyFinds.Count == 0 ? "NOT DECIDED (THIS ANIMAL'S SURVEYS FIND NOTHING)" : w.Bank == null ? "NOT DECIDED (THE BANK NOT READ)"
                : findsSeen == 0 ? findsExpected >= 3 ? $"NO FIND IN {surveysCounted} SURVEYS: NEVER" : $"NOT DECIDED (NONE, {N(findsExpected, 2)} EXPECTED)"
                : Math.Abs(findsSeen - findsExpected) <= findsTol ? "FINDS AS THEIR CHANCE ON EVERY SURVEY" : "FINDS NOT AS THEIR CHANCE",
            $"{findsSeen} found in the bank over the watch, {N(findsExpected, 2)} expected on the {surveysCounted} surveys counted (within {N(findsTol, 2)}); " +
            $"[{string.Join(", ", ours.SurveyFinds.Select(d => $"{d.Item.Id} {R(d.Chance)} x{d.Quantity}"))}]");

        // The output of a catch: each product against the game's quantity times the catch's share of the XP, rounded down
        // (judged on every catch), and its mean against the window's.
        var products = ours.Products.Where(d => d.Chance >= 0.999).ToList();
        int follow = seen.Catches.Count(c => products.All(d => c.Items.GetValueOrDefault(d.Item) is var got &&
            (got == (int)Math.Floor(d.Quantity * c.Share + 1e-9) || got == 2 * (int)Math.Floor(d.Quantity * c.Share + 1e-9))));
        var output = ours.Products.Select(d => (d.Item, Ours: d.Chance * d.Each, Seen: DevMean(seen.Catches.Select(c => (double)c.Items.GetValueOrDefault(d.Item))))).ToList();
        bool asOurs = output.All(o => double.IsFinite(o.Seen.Tolerance) && Math.Abs(o.Seen.Mean - o.Ours) <= Math.Max(o.Seen.Tolerance, 0.5));
        Say("the output of a catch", seen.Catches.Count < FewestCatches ? $"NOT DECIDED ({seen.Catches.Count} CATCHES)"
                : (products.Count == 0 ? "" : follow == seen.Catches.Count ? "IT FOLLOWS THE CATCH'S SHARE OF THE XP, ROUNDED DOWN, " : $"{seen.Catches.Count - follow} CATCHES DO NOT FOLLOW THE CATCH'S XP, ") +
                  (asOurs ? "AS OURS" : "NOT AS OURS") + (seen.OutputElsewhere.Count > 0 ? ", ITEMS OUTSIDE A CATCH" : ""),
            $"{string.Join(", ", output.Select(o => $"{o.Item.Id} {N(o.Seen.Mean)} a catch (twice its standard error {N(o.Seen.Tolerance)}) against ours {N(o.Ours)}"))}; " +
            $"the products that always come followed the catch's XP on {follow} of {seen.Catches.Count} catches; outside a catch [{string.Join(", ", seen.OutputElsewhere.Select(o => $"{o.Key.Id} +{o.Value}"))}]");

        // The time of a hunt by its own links: each whole hunt against its own surveys, steps and tries at the middles of the
        // page's ranges. The number of escapes, which makes most of a hunt's spread, is taken out, so this is tight: it says
        // whether the links add up to the hunt, on the game's clock where it was read.
        var hunts = seen.Hunts;
        bool onGame = hunts.Count > 0 && hunts.All(h => double.IsFinite(h.Game));
        var residuals = hunts.Where(h => h.Surveys != null && double.IsFinite(h.SurveyEach)).Select(h => (onGame ? h.Game : h.Real) - (h.Surveys!.Value * h.SurveyEach + h.Middles)).ToList();
        var (residual, residualTol, _) = DevMean(residuals);
        // The spread the page's ranges alone give a hunt of these links (each step and try anywhere in its range).
        double linkSpread = hunts.Count == 0 ? double.NaN : Math.Sqrt(hunts.Average(h => h.LinkVariance));
        double residualMost = Math.Max(double.IsFinite(residualTol) ? residualTol : 0, 2 * linkSpread / Math.Sqrt(Math.Max(1, residuals.Count)));
        Say("the time of a hunt by its links", residuals.Count < FewestHunts ? $"NOT DECIDED ({residuals.Count} WHOLE HUNTS)"
                : Math.Abs(residual) <= residualMost ? "EVERY HUNT ADDS UP FROM ITS SURVEYS, STEPS AND TRIES"
                : $"A HUNT TAKES {R(Math.Round(residual, 2))} S {(residual > 0 ? "MORE" : "LESS")} THAN ITS LINKS",
            $"{residuals.Count} whole hunts on the {(onGame ? "game's" : "real")} clock, each less its surveys at the time of one and the middles of the page's ranges " +
            $"of its steps and tries, each as it was when the link began: [{string.Join(", ", residuals.Select(x => N(x, 2)))}], {N(residual, 2)} s on average, within {N(residualMost, 2)} s " +
            $"(twice the larger of its standard error and the one the page's ranges give)");

        // The time of a catch: the whole hunts' mean against the window's by every reading still open, within twice the
        // standard error of a mean of hunts. A hunt's spread is the larger of the one seen and the one the reading itself
        // gives (DevHuntSpread): a few hunts with fewer escapes than usual show a spread far too small (check-3-watch: 5
        // whole hunts with 3 escapes among them, where 6 were to be expected).
        var (gameMean, _, gameSd) = DevMean(hunts.Select(h => h.Game));
        var (realMean, _, realSd) = DevMean(hunts.Select(h => h.Real));
        double measured = onGame ? gameMean : realMean, seenSd = onGame ? gameSd : realSd;
        var readings = DevReadings(ours);
        double Tolerance(int escape) => 2 * Math.Max(double.IsFinite(seenSd) ? seenSd : 0, DevHuntSpread(ours, escape)) / Math.Sqrt(Math.Max(1, hunts.Count));
        var fits = readings.Where(r => Math.Abs(r.Catch.Seconds - measured) <= Tolerance(r.Escape)).ToList();
        bool windows = fits.Any(r => r.Name.StartsWith(nameof(SurveysPerHunt.Middle)) && ours.EscapeAdds == r.Escape);
        // By the links seen: the hunt's mean surveys, steps and tries at the times seen for each.
        double bySeen = DevMean(hunts.Select(h => (double)(h.Surveys ?? 0))).Mean * DevMean(surveyed.Where(h => h.Surveys > 0).Select(h => (onGame ? h.SurveyGame : h.SurveyReal) / h.Surveys!.Value)).Mean
            + DevMean(hunts.Select(h => (double)h.Steps)).Mean * DevMean(seen.Steps.Where(st => st.By == 1).Select(st => onGame ? st.Time.Game : st.Time.Real)).Mean
            + DevMean(hunts.Select(h => (double)h.Tries)).Mean * DevMean(tries.Select(t => onGame ? t.Time.Game : t.Time.Real)).Mean;
        Say("the time of a catch", hunts.Count < FewestHunts ? $"NOT DECIDED ({hunts.Count} WHOLE HUNTS)"
                : readings.Count == 0 ? $"NOT DECIDED (THE WINDOW CANNOT COUNT IT: {DevWhyNot(ours).ToUpperInvariant()})"
                : fits.Count == 0 ? "NO READING FITS" : $"A READING FITS{(windows ? " (THE WINDOW'S AMONG THEM)" : "")}: {string.Join("; ", fits.Select(f => f.Name.ToUpperInvariant()))}",
            $"{hunts.Count} whole hunts, {N(measured, 2)} s a catch on the {(onGame ? "game's" : "real")} clock, {N(realMean, 2)} s on the real one; by the links seen {N(bySeen, 2)} s; " +
            $"the window's {(ours.SecondsPerCatch is { } sec ? N(sec, 2) + " s" : "-")}; every reading, with the tolerance from the larger spread: " +
            string.Join(", ", readings.Select(r => $"{r.Name} {N(r.Catch.Seconds, 2)} (within {N(Tolerance(r.Escape), 2)})")) +
            $"; a hunt's spread seen {N(seenSd, 2)} s, by the window's numbers {N(readings.Count > 0 ? DevHuntSpread(ours, readings[0].Escape) : double.NaN, 2)} s");
        return verdicts;
    }

    // The time of a link (a step, a try) against the page's range and its middle, on the real clock and the game's: every
    // interval the game's action timer ran within the page's range (a link judged on every event), the mean on the game's
    // clock against the middle within twice its standard error, and the real clock against the game's.
    static void DevLinkTimes(string what, bool few, List<DevLink> links, Action<string, string, string> say)
    {
        var timed = links.Where(l => double.IsFinite(l.Real)).ToList();
        var set = timed.Where(l => double.IsFinite(l.Set) && l.Set > 0).ToList();
        int setOutside = set.Count(l => l.Set < l.Min - 0.0005 || l.Set > l.Max + 0.0005);
        int realOver = timed.Count(l => l.Real > l.Max + 0.0005), gameOver = timed.Count(l => double.IsFinite(l.Game) && l.Game > l.Max + 0.06);
        var (gameMean, gameTol, _) = DevMean(timed.Select(l => l.Game));
        var (realMean, realTol, _) = DevMean(timed.Select(l => l.Real));
        double middle = timed.Count == 0 ? double.NaN : timed.Average(l => (l.Min + l.Max) / 2);
        double realSum = timed.Where(l => double.IsFinite(l.Game)).Sum(l => l.Real), gameSum = timed.Where(l => double.IsFinite(l.Game)).Sum(l => l.Game);
        double longer = gameSum > 0 ? realSum / gameSum - 1 : double.NaN;
        string clock = !double.IsFinite(longer) ? "THE GAME'S CLOCK NOT READ" : Math.Abs(longer) <= 0.01 ? "THE REAL CLOCK RAN AS THE GAME'S"
            : longer > 0 ? $"THE REAL CLOCK RAN {Math.Round(longer * 100, 1).ToString(CultureInfo.InvariantCulture)}% LONGER THAN THE GAME'S" : "THE REAL CLOCK RAN SHORTER THAN THE GAME'S";
        double mean = double.IsFinite(gameMean) ? gameMean : realMean, tol = double.IsFinite(gameMean) ? gameTol : realTol;
        say(what, few ? $"NOT DECIDED ({timed.Count} TIMED)"
                : (set.Count == 0 ? "THE GAME'S INTERVALS NOT READ, " : setOutside == 0 ? "EVERY INTERVAL OF THE GAME IN THE PAGE'S RANGE, " : $"{setOutside} INTERVALS OF THE GAME OUTSIDE THE PAGE'S RANGE, ") +
                  (double.IsFinite(tol) && Math.Abs(mean - middle) <= tol ? "AS ITS MIDDLE, " : "NOT AS ITS MIDDLE, ") + clock,
            $"{timed.Count} timed: {R(Math.Round(mean, 3))} s on the {(double.IsFinite(gameMean) ? "game's" : "real")} clock (twice its standard error {R(Math.Round(tol, 3))}) against the " +
            $"middle {R(Math.Round(middle, 3))}; {R(Math.Round(realMean, 3))} s on the real clock; {realOver} longer than the page's longest on the real clock, {gameOver} on the game's; " +
            $"the game's intervals read for {set.Count}, {R(Math.Round(set.Count > 0 ? set.Average(l => l.Set) : double.NaN, 3))} s on average; the real against the game's " +
            $"[{string.Join(", ", timed.Where(l => l.Real > l.Max + 0.0005).Take(12).Select(l => $"{l.Real:0.###} / {l.Game:0.###} (set {l.Set:0.###}, longest {l.Max:0.###})"))}]");
    }

    // The hunted animal's trapping chance now, its chance to reattempt, the bonuses a failed try has put on it, and what
    // its trap's fail bonuses hold.
    string DevTrapNow(ActiveHuntingAnimal a)
    {
        try
        {
            string fail;
            try { fail = DevNative.ActiveFailBonuses(a) is { } f ? $"{f.Item1?.Id}: {DevNative.Values(f.Item2)}" : "none"; }
            catch (Exception e) when (e is MissingMemberException or TypeLoadException) { fail = $"not read ({e.GetType().Name})"; }
            string trap = a.SelectedTrap is { } item && item.Capabilities.TryGet(out HuntingTrapCapability? cap) ? $"{item.Id}, fail bonuses {DevNative.Values(cap.FailBonuses)}" : "none";
            return $"trapping chance {R(a.Stats.TrappingSuccessChance.Value)}, reattempt {R(a.Stats.TrappingReattemptChance?.Total.Value ?? 0)}, trap fails {a.TrapFailCount}, " +
                $"the fail bonuses on it now {fail}, its trap {trap}";
        }
        catch (Exception e) { return $"not read ({e.GetType().Name}: {e.Message})"; }
    }

    // Each watched item's count from the start to the end, what moved in frames with an event and outside them.
    void DevItemsSummary(DevHuntWatch w)
    {
        if (w.Bank == null) { DevLog("watch | the items | the bank was not found: nothing read"); return; }
        var moved = w.Watched.Where(i => w.Counts[i] != w.StartCounts[i] || w.Outside.ContainsKey(i)).ToList();
        int added = w.Moments.Sum(m => m.Added.Count);
        DevLog($"watch | the items | {w.Watched.Count} watched; moved: [{string.Join(", ", moved.Select(i => $"{i.Id} {w.StartCounts[i]} -> {w.Counts[i]} " +
            $"(in frames with an event {w.Moments.Sum(m => m.Items.Where(x => ReferenceEquals(x.Item, i)).Sum(x => x.Quantity)):+0;-0}, outside {w.Outside.GetValueOrDefault(i):+0;-0})"))}]; " +
            $"the action's ItemAdded came {added} times");
    }

    // The game's own tracker, period by period: its Hunting XP and items of an hour. At the end, a period that lies inside
    // the watch, all of it spent on the one animal hunted throughout, is put beside the window's hour for that animal. Each
    // number is judged within twice its own spread over the period (DevPeriodSpread: the catches the period holds and their
    // own spread, and what one catch brings of that number and its spread), and a period that holds fewer than
    // FewestCatches catches by the window's hour is not judged (review 6, point 1: a one-minute period holds less than one
    // catch of Yellowtail Bird, and a seed at 20% a catch comes 0 or 2 times in ten minutes as often as once). The periods
    // keep milliseconds (a cycle of 600,000 is ten minutes): an hour is 3,600,000 of them (check-3-watch logged them as
    // seconds).
    void DevTracker(string when, DevHuntWatch w, double? watched)
    {
        try
        {
            var tracker = _hunting!.Tracker;
            ExperienceComponent experience = _hunting.Experience;
            ActiveHuntingAnimal? hunted = w.Seen.Count == 1 ? w.Seen.Keys.First() : _hunting.Action.CurrentAnimal;
            AnimalRates? ours = hunted != null ? ReadAnimal(hunted) : null;
            DevSeen? seen = hunted != null && w.Seen.TryGetValue(hunted, out DevSeen? s) ? s : null;
            var items = new List<Item>();
            if (ours != null)
            {
                if (ours.Trap != null) items.Add(ours.Trap);
                if (ours.Bait != null) items.Add(ours.Bait);
                items.AddRange(ours.Products.Select(d => d.Item));
            }
            items = items.Distinct().ToList();
            // The spread of a catch's time, as a share of it: the larger of the one the whole hunts show and the one the window's
            // numbers give (a third when they cannot be counted).
            var huntTimes = seen?.Hunts.Select(h => h.Real).Where(double.IsFinite).ToList() ?? new List<double>();
            var (mean, _, sd) = DevMean(huntTimes);
            double ownCv = ours?.SecondsPerCatch is { } perCatch && ours.EscapeAdds is { } escape ? DevHuntSpread(ours, escape) / perCatch : double.NaN;
            double cv = Math.Max(huntTimes.Count >= 3 && mean > 0 ? sd / mean : 0, double.IsFinite(ownCv) ? ownCv : 1.0 / 3);
            int i = 0;
            foreach (var period in tracker.Periods)
            {
                double xp = period.Experience.TryGetValue(experience, out var list) ? list.Total : 0;
                int recorded = period.Experience.RecordedCount;
                double covered = Math.Min(period.CycleDuration, recorded * period.SamplePeriod);
                double Hour(double total) => covered > 0 ? total / covered * 3_600_000 : double.NaN;
                bool inside = watched is { } length && period.CycleDuration <= length * 1000 - period.SamplePeriod && covered >= period.CycleDuration - 1 &&
                    !w.AnimalChanged && w.Seen.Count == 1;
                string compared = "";
                if (watched != null)
                {
                    if (!inside) compared = watched * 1000 < period.CycleDuration ? " | starts before the watch: not compared" : w.AnimalChanged || w.Seen.Count != 1 ? " | not one animal throughout: not compared" : " | not compared";
                    else if (ours?.CatchesPerHour is { } perHour && ours.PerCatch is { } k)
                    {
                        // The catches the period holds by the window's hour; too few, and nothing is judged.
                        double catches = perHour * period.CycleDuration / 3_600_000;
                        if (catches < FewestCatches) compared = $" | inside the watch, but about {R(Math.Round(catches, 1))} catches by the window's hour: too few, not judged";
                        else
                        {
                            // Each number with its own tolerance: twice its spread over the period, as an hour.
                            string Beside(string name, double game, double? mine, (double Mean, double Variance) one)
                            {
                                if (mine is not { } m) return $"{name} {R(Math.Round(game, 1))} against ours -";
                                double tolerance = 2 * DevPeriodSpread(catches, cv, one) * 3_600_000 / period.CycleDuration;
                                return $"{name} {R(Math.Round(game, 1))} against ours {R(Math.Round(m, 1))} within {R(Math.Round(tolerance, 1))}" +
                                    (Math.Abs(game - m) <= tolerance ? "" : "  OUTSIDE ITS SPREAD");
                            }
                            // What one catch brings, its mean and its spread: the XP by the card's multipliers (every share in
                            // the range as likely, wider than the 6 catches of check-3-watch showed); the tries of a catch, each
                            // catching at the trapping chance, for the traps and the bait; an item at its chance, as many as
                            // its quantity times the catch's share, doubled at the doubling chance.
                            var (low, high) = ours.XpRange ?? (1, 1);
                            double middle = (low + high) / 2, shareSquare = middle * middle + Math.Pow(high - low, 2) / 12;
                            double xpEach = ours.XpEach;
                            var beside = new List<string> { Beside("Hunting XP", Hour(xp), ours.XpPerHour, (xpEach * middle, xpEach * xpEach * (shareSquare - middle * middle))) };
                            double q = Math.Max(1e-9, ours.TrapChance);
                            (double, double) tries = (k.Tries, (1 - q) / (q * q));
                            foreach (Item item in items)
                            {
                                double total = period.Items.TryGetValue(item, out var l) ? l.Total : 0;
                                if (ReferenceEquals(item, ours.Trap)) { beside.Add(Beside(item.Id, Hour(total), -ours.TrapsPerHour, tries)); continue; }
                                if (ReferenceEquals(item, ours.Bait)) { beside.Add(Beside(item.Id, Hour(total), ours.UseBait ? -ours.BaitPerHour : 0, ours.UseBait ? tries : (0, 0))); continue; }
                                var drops = ours.Products.Where(d => ReferenceEquals(d.Item, item)).ToList();
                                double each = drops.Sum(d => d.Chance * d.Each);
                                double square = drops.Sum(d => d.Chance * d.Quantity * d.Quantity * shareSquare * (1 + 3 * ours.Doubling));
                                beside.Add(Beside(item.Id, Hour(total), drops.Any(d => d.PerHour == null) ? null : drops.Sum(d => d.PerHour), (each, Math.Max(0, square - each * each))));
                            }
                            bool within = !beside.Any(b => b.Contains("OUTSIDE ITS SPREAD"));
                            compared = $" | inside the watch, {hunted!.Animal.LocalId} throughout, about {R(Math.Round(catches, 1))} catches by the window's hour, a hunt's spread " +
                                $"{R(Math.Round(cv, 3))} of it; an hour by it beside ours, each within twice its own spread: {string.Join(", ", beside)} | {(within ? "WITHIN ITS SPREAD" : "OUTSIDE ITS SPREAD")}";
                        }
                    }
                    else compared = " | inside the watch, but the window cannot count this animal's hour: not compared";
                }
                DevLog($"tracker | {when} | period {i++}: cycle {R(period.CycleDuration)} ms ({R(period.CycleTicks)} ticks), {period.SampleCount} samples of {R(period.SamplePeriod)} ms, " +
                    $"{recorded} recorded | Hunting XP {R(xp)}, an hour by it {R(Math.Round(Hour(xp), 1))} | items [{string.Join(", ", items.Select(item => $"{item.Id} {(period.Items.TryGetValue(item, out var l) ? l.Total : 0)}"))}]" +
                    compared);
            }
            if (i == 0) DevLog($"tracker | {when} | the tracker has no periods");
        }
        catch (Exception e) { DevLog($"tracker | {when} | could not be read: {e.Message}"); }
    }

    // The spread (one standard deviation) of a number's total over a period that holds this many catches on average, a
    // catch's time spreading by cv of it, and one catch bringing a mean and a variance of the number: the catches' own
    // spread (a renewal count: its variance is about the catches times cv squared) and what each brings.
    static double DevPeriodSpread(double catches, double cv, (double Mean, double Variance) one) =>
        Math.Sqrt(Math.Max(0, catches * one.Variance + one.Mean * one.Mean * catches * cv * cv));
}
#endif
