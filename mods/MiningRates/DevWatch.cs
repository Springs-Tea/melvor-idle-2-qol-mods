#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.Trackers;
using GamesByMalcs.Melvor2.Entities.Bank;
using MelvorModLoader;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Mining;
using Godot;

namespace MiningRates;

// Development: a file "miningwatch.request" in the mod's folder starts a silent watch of the character's mining, and
// "miningwatch.done" says how it ended. The file holds a number of minutes (10 when it is empty), which is logged. It
// opens nothing, clicks nothing, does not wait on the mouse and changes nothing: it only listens to the game's own
// events, as Fishing Rates' DevWatch listens to catches (mods/FishingRates/Dev.cs): a rock's hit (MiningRock.Hit), its
// break bar (MiningRock.ProgressChanged), the game's action of a hit (MiningActionComponent.RockMined), the Mining XP
// (ExperienceComponent.XpChanged) and the items the action adds (ActionComponent.ItemAdded). If the character is not
// mining when it starts, it says so in one line and ends.
//   pwsh .claude\codex\check-mod.ps1 -Worktree <worktree> -Mod MiningRates -Request miningwatch.request -Produces miningwatch.done -Text 10 -Seconds 900 -Out <folder>
// Every hit is one "Dev: hit" line: the rock, success and shatter as the hit says them, broken, early and shatter as the
// action says them, the rock's break chance and minimum at that hit, the break bar before and after, the order of the
// game's events, the XP and the items it added. The events of one hit come in one frame, and are told apart from the
// next hit by the frame. It ends when the minutes are up or after a minute without a hit, with, for every rock hit, one
// line per question, each with a verdict in capitals; NOT DECIDED below MostBreaks breaks. The question the window's
// rule is checked on (RockRates.Known, from the game's words) is how the game decides a break (RockRates.BreakRule):
// every reading's expected breaks over the hits seen, from each hit's own bar, against the breaks seen, and the hits no
// reading allows. Then the game's own tracker
// (MiningEntity.Tracker) over its periods beside ours. Its last line says it stopped listening and changed nothing.
// Every listener runs inside a try, and all are taken off when it ends, on Stop (with the done file, DevStopped) and on
// another character (DevUnwatchAll).
public partial class MiningRatesMod
{
    const string WatchRequestFile = "miningwatch.request", WatchDoneFile = "miningwatch.done";
    // A minute without a hit ends the watch.
    const ulong WatchIdle = 60000;
    // Fewer breaks than this decide nothing about the break: with 40 breaks the two closest readings (one hit a break
    // apart) are about 3.7 standard errors apart at a 10% break chance (the code review of turn 1, worked out there).
    const int MostBreaks = 30;

    // What one hit brought: every event of the game that came in the same frame.
    sealed class DevHit
    {
        public long Frame;
        public ulong At;
        public string Rock = "?";
        public int Hits;
        public bool Success, HitShattered;
        public bool Mined, Broken, Early, MinedShattered;
        public double? Before;
        // The rock's break chance and the bar's minimum as the game had them at the hit (a level up changes them).
        public double Chance = double.NaN, Minimum = double.NaN;
        public readonly List<(string Rock, double Old, double New)> Progress = new();
        public double Xp;
        public int XpChanges;
        public int? OreBefore, OreAfter;
        public int? BankGain => OreAfter - OreBefore;
        public readonly List<(Item Item, int Quantity)> Items = new();
        // The game's events of this frame in the order they came: hit, bar, mined, xp, item.
        public readonly List<string> Order = new();

        public bool IsHit => Hits > 0 || Mined;
        // A break by the action's own word when the action said it, else by the hit's.
        public bool Breaks => Mined ? Broken : Success;
        public bool Shattered => HitShattered || MinedShattered;
    }

    sealed class DevHitWatch
    {
        public ulong Since, LastHit, Length;
        public MiningActionComponent Action = null!;
        public readonly List<Action> Off = new();
        public readonly List<DevHit> Hits = new();
        public readonly Dictionary<string, double> Bar = new();
        // Every rock's numbers when the watch began, to see whether they changed while it ran.
        public readonly Dictionary<string, string> Start = new();
        public DevHit? Open;
        public BankInventoryComponent? Bank;
        public readonly Dictionary<string, Item> Products = new();
        public readonly Dictionary<Item, int> BankCounts = new();
        public long SameRockTick;
        public readonly Dictionary<IReadOnlyTrackerPeriod, long> SampleEnded = new();
        public bool Interrupted, Failed;
    }

    DevHitWatch? _devHits;

    // A missing bank or tracker member ends this request independently of the player window.
    void DevRunWatch(Action run)
    {
        try { run(); }
        catch (Exception e)
        {
            DevUnwatchHits();
            _devHits = null;
            string words = $"watch failed: {e.GetType().Name}: {e.Message}; listeners removed";
            DevLog("watch | " + words);
            File.WriteAllText(Path.Combine(_ctx.Directory, WatchDoneFile), words);
        }
    }

    // A watch asked for, when no light check runs.
    void DevWatchRequest()
    {
        string path = Path.Combine(_ctx.Directory, WatchRequestFile);
        if (!File.Exists(path)) return;
        string asked = File.ReadAllText(path).Trim();
        File.Delete(path);
        int minutes = 10;
        if (asked.Length > 0 && !(int.TryParse(asked, NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes) && minutes is >= 1 and <= 60))
        {
            DevLog($"watch | the request's '{asked}' is not a number of minutes from 1 to 60: it watches 10");
            minutes = 10;
        }
        var action = _mining!.Action;
        DevLog($"watch | {minutes} minutes asked{(asked.Length == 0 ? " (the request was empty)" : "")}; {DevMiningState()}");
        if (!action.IsStarted || !DevMiningNow() || action.CurrentRock == null)
        {
            const string not = "not mining when the watch began: nothing was listened to. Start mining a rock by hand and send the request again.";
            DevLog("watch | " + not);
            File.WriteAllText(Path.Combine(_ctx.Directory, WatchDoneFile), not);
            return;
        }
        var w = new DevHitWatch { Action = action, Since = Time.GetTicksMsec(), Length = (ulong)minutes * 60000 };
        w.LastHit = w.Since;
        w.SameRockTick = _mining.Tracker.CurrentTick;
        w.Bank = _game!.Entities.GetAllObjects().OfType<BankEntity>().FirstOrDefault()?.Inventory;
        foreach (MiningRock rock in action.Rocks.Values)
        {
            if (rock.Recipe.Product?.Item is { } item)
            {
                w.Products[rock.Recipe.Id] = item;
                if (w.Bank != null) w.BankCounts[item] = w.Bank.GetQuantity(item);
            }
            w.Bar[rock.Recipe.Id] = rock.Progress;
            w.Start[rock.Recipe.Id] = DevRockStats(rock);
        }
        _devHits = w;
        DevWatchListen(w);
        DevLog($"watch | listening; the rock mined {action.CurrentRock.Recipe.Id}, its break bar at {R(action.CurrentRock.Progress)}; its numbers {w.Start[action.CurrentRock.Recipe.Id]}");
        RockRates rate = RockRates.Read(action.CurrentRock, _mining, Shown);
        double hours = minutes / 60.0;
        double? breaks = rate.BreaksPerHour * hours;
        DevLog($"watch | forecast | {rate.Name}: {minutes} minutes give about {rate.HitsPerHour * hours:0.#} hits and " +
            $"{(breaks is { } count ? count.ToString("0.#") : "an unknown number of")} breaks. The break-rule comparison needs {MostBreaks} breaks. " +
            (breaks == null ? "There is no break-rate estimate for this rock."
            : breaks < MostBreaks ? $"Ask at least {Math.Ceiling(MostBreaks / rate.BreaksPerHour!.Value * 60)} minutes at this rate."
            : "The requested time covers that estimate.") +
            " XP and bank timing need both breaks and other hits." +
            (breaks is { } expected && expected > 0 && expected < 1
                ? $" Allow about {Math.Ceiling(60 / rate.BreaksPerHour!.Value)} minutes for one break." : "") +
            (rate.HitsPerBreak == 1 ? " This rock is expected to break every hit, so those timings cannot be distinguished."
            : breaks is { } b && rate.HitsPerHour * hours - b < 1
                ? $" Allow about {Math.Ceiling(60 / (rate.HitsPerHour - rate.BreaksPerHour!.Value))} minutes for one non-breaking hit." : "") +
            (rate.BreaksPerHour < MostBreaks ? " Even the 60-minute request limit is below the comparison estimate." : "") +
            (w.Bank == null ? " The bank could not be read." : ""));
        DevTracker("at the start", w);
    }

    // A rock's numbers as the game has them now, with every bonus of the character.
    static string DevRockStats(MiningRock rock)
    {
        MiningActionStats s = rock.Stats;
        return $"interval {R(s.Interval.Value)} ms, break chance {R(s.RockBreakChance.Value)}, minimum {R(s.MinimumProspecting.Value)}, " +
            $"XP {R(s.Experience.XpToAdd.Value)}, product {rock.Recipe.Product?.Item?.Id ?? "none"} x{s.ProductQuantity.Value}, on-hit [{string.Join(", ", (s.OnHitItemChances?.Values ?? []).Select(n => $"{n.Value?.Item?.Id} x{n.Value?.Quantity} at {n.Value?.Chance}"))}], doubling {R(s.DoublingChance?.Total.Value ?? 0)}, " +
            $"more ore {R(s.AdditionalProductQuantityChance?.Total.Value ?? 0)}, shatter {R(s.ShatterChance?.Total.Value ?? 0)}";
    }

    // The game's events, each listener inside a try: one that fails says so once and the watch ends on the next tick.
    void DevWatchListen(DevHitWatch w)
    {
        var action = w.Action;
        ExperienceComponent experience = _mining!.Experience;
        void Safe(Action listen)
        {
            if (w.Failed || GameAccess.IsComputingOffline) return;
            try { listen(); }
            catch (Exception e)
            {
                w.Failed = true;
                DevLog("watch | a listener failed, the watch ends: " + e.Message);
            }
        }
        foreach (MiningRock rock in action.Rocks.Values)
        {
            string id = rock.Recipe.Id;
            MiningRock watched = rock;
            EventHandler<RockHitEventArgs> hit = (_, e) => Safe(() =>
            {
                DevHit h = DevHitNow(w, id);
                h.Hits++;
                h.Success |= e.Success;
                h.HitShattered |= e.WasShattered;
                h.Chance = watched.Stats.RockBreakChance.Value;
                h.Minimum = watched.Stats.MinimumProspecting.Value;
                h.Order.Add("hit");
            });
            EventHandler<ValueChangedEventArgs<double>> bar = (_, e) => Safe(() =>
            {
                DevHit h = DevHitNow(w, id);
                h.Before ??= e.OldValue;
                h.Progress.Add((id, e.OldValue, e.NewValue));
                h.Order.Add("bar");
                w.Bar[id] = e.NewValue;
            });
            rock.Hit += hit;
            rock.ProgressChanged += bar;
            w.Off.Add(() =>
            {
                rock.Hit -= hit;
                rock.ProgressChanged -= bar;
            });
        }
        EventHandler<RockMinedEventArgs> mined = (_, e) => Safe(() =>
        {
            DevHit h = DevHitNow(w, e.Rock?.Recipe.Id ?? "?");
            h.Mined = true;
            h.Broken |= e.WasBroken;
            h.Early |= e.WasEarly;
            h.MinedShattered |= e.WasShattered;
            h.Order.Add("mined");
        });
        EventHandler<ValueChangedEventArgs<double>> xp = (_, e) => Safe(() =>
        {
            DevHit h = DevHitNow(w, null);
            h.Xp += e.NewValue - e.OldValue;
            h.XpChanges++;
            h.Order.Add("xp");
        });
        EventHandler<ItemAddedEventArgs> item = (_, e) => Safe(() =>
        {
            if (e.Item == null) return;
            DevHit h = DevHitNow(w, null);
            h.Items.Add((e.Item, e.Quantity));
            h.Order.Add("item");
        });
        // As in Fishing Rates' DevBaitWatch, a bank change can precede the mining events in its frame.
        // Close the preceding frame with the old count in that case. Changes between hits get their own frame.
        EventHandler<ItemQuantityChangedEventArgs> bank = (_, e) => Safe(() =>
        {
            if (e.Item == null || !w.BankCounts.ContainsKey(e.Item)) return;
            DevHit h = DevHitNow(w, null, e);
            h.Order.Add($"bank {e.Item.Id} {e.OldValue}->{e.NewValue}");
        });
        if (w.Bank != null)
        {
            w.Bank.ItemQuantityChanged += bank;
            w.Off.Add(() => w.Bank.ItemQuantityChanged -= bank);
        }
        var tracker = _mining.Tracker;
        foreach (var period in tracker.Periods)
        {
            EventHandler finished = (_, _) => Safe(() => w.SampleEnded[period] = tracker.CurrentTick);
            period.SampleFinished += finished;
            w.Off.Add(() => period.SampleFinished -= finished);
        }
        void NewRun()
        {
            w.Interrupted = true;
            w.SameRockTick = tracker.CurrentTick;
        }
        EventHandler<ActionEventArgs> reset = (_, _) => NewRun();
        EventHandler<ValueChangedEventArgs<MiningRock>> changed = (_, _) => NewRun();
        action.Started += reset;
        action.Stopped += reset;
        action.CurrentRockChanged += changed;
        w.Off.Add(() =>
        {
            action.Started -= reset;
            action.Stopped -= reset;
            action.CurrentRockChanged -= changed;
        });
        action.RockMined += mined;
        experience.XpChanged += xp;
        action.ItemAdded += item;
        w.Off.Add(() =>
        {
            action.RockMined -= mined;
            experience.XpChanged -= xp;
            action.ItemAdded -= item;
        });
    }

    // The hit of this frame: a new one when the frame has changed, after the one before is logged.
    DevHit DevHitNow(DevHitWatch w, string? rock, ItemQuantityChangedEventArgs? change = null)
    {
        long frame = (long)Engine.GetProcessFrames();
        if (w.Open == null || w.Open.Frame != frame)
        {
            DevFlushHit(w, change);
            w.Open = new DevHit { Frame = frame, At = Time.GetTicksMsec() };
        }
        if (rock != null && w.Open.Rock == "?")
        {
            w.Open.Rock = rock;
            w.Open.Before ??= w.Bar.TryGetValue(rock, out double bar) ? bar : null;
        }
        return w.Open;
    }

    void DevFlushHit(DevHitWatch w, ItemQuantityChangedEventArgs? change = null)
    {
        if (w.Open is not { } h) return;
        w.Open = null;
        if (w.Bank != null)
        {
            Item? product = w.Products.GetValueOrDefault(h.Rock);
            foreach (Item item in w.BankCounts.Keys)
            {
                int before = w.BankCounts[item];
                int after = Equals(change?.Item, item) ? change.OldValue : w.Bank.GetQuantity(item);
                w.BankCounts[item] = after;
                if (Equals(item, product))
                {
                    h.OreBefore = before;
                    h.OreAfter = after;
                }
            }
        }
        if (h.IsHit)
        {
            w.Hits.Add(h);
            w.LastHit = h.At;
        }
        DevLog($"{(h.IsHit ? "hit" : "other")} | {h.Rock} | hit events {h.Hits}: success {h.Success}, shattered {h.HitShattered} | the action: " +
            (h.Mined ? $"broken {h.Broken}, early {h.Early}, shattered {h.MinedShattered}" : "no event") +
            $" | break chance {R(h.Chance)}, minimum {R(h.Minimum)}" +
            $" | the break bar {(h.Before is { } b ? R(b) : "?")} -> [{string.Join(", ", h.Progress.Select(p => $"{R(p.Old)}->{R(p.New)}{(p.Rock == h.Rock ? "" : " of " + p.Rock)}"))}]" +
            $" | events [{string.Join(" ", h.Order)}] | XP +{R(h.Xp)} in {h.XpChanges} changes | items [{string.Join(", ", h.Items.Select(i => $"{i.Item.Id} x{i.Quantity}"))}]" +
            $" | bank product {w.Products.GetValueOrDefault(h.Rock)?.Id ?? "-"}: {h.OreBefore?.ToString() ?? "-"} before, {h.OreAfter?.ToString() ?? "-"} after" +
            (h.Hits > 1 ? $"  {h.Hits} HITS IN ONE FRAME" : ""));
    }

    // Every tick while the watch runs: the last hit is logged, and the watch ends when its minutes are up, after a
    // minute without a hit, or when a listener failed.
    void DevWatchTick()
    {
        if (_devHits is not { } w) return;
        if (w.Open != null && w.Open.Frame != (long)Engine.GetProcessFrames()) DevFlushHit(w);
        else if (w.Open == null && w.Bank != null && w.Action.CurrentRock?.Recipe.Product?.Item is { } ore)
            w.BankCounts[ore] = w.Bank.GetQuantity(ore);
        ulong now = Time.GetTicksMsec();
        if (w.Failed) DevWatchEnd("a listener failed", summarise: false);
        else if (now - w.Since >= w.Length) DevWatchEnd($"the {w.Length / 60000} minutes are up");
        else if (now - w.LastHit >= WatchIdle) DevWatchEnd("a minute without a hit");
    }

    void DevUnwatchHits()
    {
        if (_devHits is not { } w) return;
        foreach (Action off in w.Off)
        {
            try { off(); }
            catch (Exception) { }
        }
        w.Off.Clear();
    }

    // The summary: for every rock hit, one line per question with its verdict; then the tracker, then the last line.
    // Without a summary when another character was opened or the mod stopped: the rocks of the one watched may be gone.
    void DevWatchEnd(string why, bool summarise = true)
    {
        if (_devHits is not { } w) return;
        if (summarise && !w.Failed) DevFlushHit(w);
        DevUnwatchHits();
        _devHits = null;
        double seconds = (Time.GetTicksMsec() - w.Since) / 1000.0;
        var verdicts = new List<string>();
        try
        {
            DevLog($"watch | ended after {seconds:0} s: {why}; {w.Hits.Count} hits seen at [{string.Join(", ", w.Hits.GroupBy(h => h.Rock).Select(g => $"{g.Key} {g.Count()}"))}]");
            if (!summarise) verdicts.Add("NO SUMMARY");
            else
            {
                foreach (var group in w.Hits.GroupBy(h => h.Rock).OrderByDescending(g => g.Count()))
                {
                    if (_mining!.Action.Rocks.Values.FirstOrDefault(r => r.Recipe.Id == group.Key) is not { } rock) continue;
                    var said = DevWatchSummary(group.ToList(), RockRates.Read(rock, _mining, Shown), w.Start.GetValueOrDefault(group.Key, "not read"), w.Interrupted ? null : seconds);
                    verdicts.Add($"{rock.Recipe.LocalId}: {string.Join(", ", said)}");
                }
                if (verdicts.Count == 0) verdicts.Add("NO HIT SEEN");
                DevTracker($"at the end, after {seconds:0} s", w);
            }
        }
        catch (Exception e) { DevLog("watch | the summary failed: " + e.Message); verdicts.Add("THE SUMMARY FAILED"); }
        const string last = "stopped listening to the game's events; nothing was changed";
        DevLog("watch | " + last);
        File.WriteAllText(Path.Combine(_ctx.Directory, WatchDoneFile), $"{why}; {w.Hits.Count} hits in {seconds:0} s; {string.Join("; ", verdicts)}; {last}");
    }

    // The breaks a reading of the break bar expects over these hits, each hit at the chance the reading gives it, against
    // the breaks seen: how many standard errors apart (z), and how many hits the reading does not allow at all (a break at
    // a chance of 0, or no break at a chance of 1).
    static (double Expected, double Z, int Impossible) DevFit(IReadOnlyList<DevHit> hits, Func<DevHit, double> chance)
    {
        double expected = 0, variance = 0;
        int seen = 0, impossible = 0;
        foreach (DevHit h in hits)
        {
            double p = Math.Clamp(chance(h), 0, 1);
            expected += p;
            variance += p * (1 - p);
            if (h.Breaks) seen++;
            if ((h.Breaks && p <= 0) || (!h.Breaks && p >= 1)) impossible++;
        }
        double z = variance > 0 ? (seen - expected) / Math.Sqrt(variance) : Math.Abs(seen - expected) < 1e-9 ? 0 : double.PositiveInfinity;
        return (expected, z, impossible);
    }

    // The chance a reading gives a hit, from the bar before it and the rock's break chance at it (both fractions). By the
    // game's words (SameChanceUntilFull) a hit whose raise brings the sum to 100% breaks for sure, as RockRates.ExpectedHits
    // counts it (a sum of 100% on paper is reached).
    static double DevReadingChance(BreakRule rule, DevHit h)
    {
        double chance = Math.Clamp(double.IsFinite(h.Chance) ? h.Chance : 0, 0, 1), bar = h.Before ?? 0;
        return rule switch
        {
            BreakRule.BarBeforeHit => bar,
            BreakRule.BarAfterRaise => bar + chance,
            BreakRule.SameChanceUntilFull => bar + chance >= 1 - 1e-9 ? 1 : chance,
            _ => chance,
        };
    }

    static string DevReadingWords(BreakRule rule) => rule switch
    {
        BreakRule.BarBeforeHit => "THE BAR BEFORE THE HIT",
        BreakRule.BarAfterRaise => "THE BAR AFTER THE RAISE",
        BreakRule.SameChanceUntilFull => "THE SAME CHANCE UNTIL THE SUM IS FULL",
        _ => "THE SAME CHANCE ON EVERY HIT",
    };

    List<string> DevWatchSummary(List<DevHit> hits, RockRates ours, string start, double? seconds)
    {
        var verdicts = new List<string>();
        string rock = ours.Rock.Recipe.Id;
        void Say(string question, string verdict, string words)
        {
            verdicts.Add(verdict);
            DevLog($"watch | {rock} | {question} | {verdict}: {words}");
        }
        int n = hits.Count;
        var breaks = hits.Where(h => h.Breaks).ToList();
        var whole = hits.Where(h => !h.Breaks).ToList();
        bool enough = breaks.Count >= MostBreaks;
        string fewer = enough ? "" : $" (fewer than {MostBreaks} breaks decide nothing)";

        // The rock's numbers at the start and at the end: a level up while it ran changes them.
        string end = DevRockStats(ours.Rock);
        DevLog($"watch | {rock} | the rock's numbers | at the start {start}; at the end {end}" + (start == end ? "" : "  CHANGED WHILE IT RAN"));

        // What the hit's Success says, against the action's own word of a break.
        DevLog($"watch | {rock} | the hit's Success against the action's WasBroken | both {hits.Count(h => h.Success && h.Broken)}, " +
            $"Success only {hits.Count(h => h.Success && h.Mined && !h.Broken)}, WasBroken only {hits.Count(h => !h.Success && h.Broken)}, " +
            $"neither {hits.Count(h => h.Mined && !h.Success && !h.Broken)}, no action event {hits.Count(h => !h.Mined)}");

        // How many hits a break takes: from one break to the next, so that the hits before the first break seen (a bar
        // that was already part filled) do not count.
        var cycles = new List<int>();
        int last = -1;
        for (int i = 0; i < n; i++)
        {
            if (!hits[i].Breaks) continue;
            if (last >= 0) cycles.Add(i - last);
            last = i;
        }
        double mean = cycles.Count > 0 ? cycles.Average() : double.NaN;
        double se = cycles.Count > 1 ? Math.Sqrt(cycles.Sum(c => (c - mean) * (c - mean)) / (cycles.Count - 1) / cycles.Count) : double.NaN;
        Say("hits a break takes", enough ? "MEASURED" : "NOT DECIDED",
            $"{n} hits, {breaks.Count} breaks, {breaks.Count(h => h.Early)} of them early; {cycles.Count} from one break to the next: {R(mean)} hits on average " +
            $"(standard error {R(se)}); by the readings from the game's numbers at the end [{DevReadings(ours)}]; " +
            $"the window's {(ours.HitsPerBreak is { } mine ? R(mine) : "- (no rule settled)")}{fewer}");

        // Which reading of the bar the breaks follow: every hit at the chance the reading gives it.
        var known = hits.Where(h => h.Before != null && double.IsFinite(h.Chance)).ToList();
        var fits = new List<BreakRule>();
        var words = new List<string>();
        foreach (BreakRule rule in Enum.GetValues<BreakRule>())
        {
            var (expected, z, impossible) = DevFit(known, h => DevReadingChance(rule, h));
            bool fit = Math.Abs(z) <= 2 && impossible == 0;
            if (fit) fits.Add(rule);
            words.Add($"{rule}: {R(Math.Round(expected, 2))} expected, {R(Math.Round(z, 2))} standard errors off, {impossible} hits it does not allow{(fit ? ", fits" : "")}");
        }
        Say("the readings", !enough ? "NOT DECIDED" : fits.Count == 1 ? DevReadingWords(fits[0]) + " FITS" : fits.Count == 0 ? "NO READING FITS" : "MORE THAN ONE READING FITS",
            $"over {known.Count} hits with a bar and a break chance read, {known.Count(h => h.Breaks)} breaks seen; {string.Join("; ", words)}; " +
            $"breaks on a hit whose bar stood at 0: {known.Count(h => h.Breaks && h.Before <= 1e-9)}, hits on a full bar that did not break: " +
            $"{known.Count(h => !h.Breaks && h.Before >= 1 - 1e-9)}{fewer}");

        // How the break bar moves on a hit that does not break the rock, against the break chance at that hit.
        var steps = whole.Select(h => (Step: h.Progress.Where(p => p.Rock == rock).Sum(p => p.New - p.Old), h.Chance)).ToList();
        string stepWords = string.Join(", ", steps.GroupBy(s => Math.Round(s.Step, 4)).OrderBy(g => g.Key).Select(g => $"{R(g.Key)} x{g.Count()}"));
        Say("the bar on a hit that does not break", steps.Count == 0 ? "NOT DECIDED"
                : steps.All(s => Math.Abs(s.Step - s.Chance) < 1e-3) ? "IT GROWS BY THE BREAK CHANCE" : "IT GROWS BY ANOTHER AMOUNT",
            $"{steps.Count} such hits, steps [{stepWords}]; the break chance [{string.Join(", ", steps.Select(s => R(s.Chance)).Distinct())}]");

        // Where the bar stands after a break, against the minimum at that hit.
        var after = breaks.Select(h => (Bar: h.Progress.Where(p => p.Rock == rock).Select(p => (double?)p.New).LastOrDefault(), h.Minimum))
            .Where(a => a.Bar != null).ToList();
        Say("the bar after a break", after.Count == 0 ? "NOT DECIDED"
                : after.All(a => Math.Abs(a.Bar!.Value - a.Minimum) < 1e-3) ? "IT STARTS AGAIN AT THE MINIMUM" : "IT STARTS AGAIN AT ANOTHER VALUE",
            $"after {after.Count} breaks [{string.Join(", ", after.GroupBy(a => Math.Round(a.Bar!.Value, 4)).Select(g => $"{R(g.Key)} x{g.Count()}"))}]; " +
            $"the minimum [{string.Join(", ", after.Select(a => R(a.Minimum)).Distinct())}]");

        // The bar's own events in the frame of a break: does it rise first and then go back, or only go back? A rise
        // first means the hit raises the bar before the game rolls the break.
        string Shape(DevHit h)
        {
            var moves = h.Progress.Where(p => p.Rock == rock).Select(p => p.New - p.Old).ToList();
            return moves.Count == 0 ? "none" : moves.Count == 1 ? moves[0] < 0 ? "back only" : moves[0] > 0 ? "up only" : "unchanged"
                : moves[0] > 0 && moves[^1] < 0 ? "up, then back" : string.Join(" then ", moves.Select(m => m < 0 ? "back" : m > 0 ? "up" : "unchanged"));
        }
        var shapes = breaks.GroupBy(Shape).OrderByDescending(g => g.Count()).ToList();
        Say("the bar's events on a break", breaks.Count == 0 ? "NOT DECIDED" : shapes.Count > 1 ? "MIXED"
                : shapes[0].Key == "up, then back" ? "IT RISES, THEN GOES BACK" : shapes[0].Key == "back only" ? "IT ONLY GOES BACK" : "ANOTHER WAY",
            $"[{string.Join(", ", shapes.Select(g => $"{g.Key} x{g.Count()}"))}]; the game's events of a break in their order " +
            $"[{string.Join(", ", breaks.GroupBy(h => string.Join(" ", h.Order)).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} x{g.Count()}"))}]");

        // Early breaks against the bar before the hit.
        static string Range(IEnumerable<DevHit> some)
        {
            var bars = some.Where(h => h.Before != null).Select(h => h.Before!.Value).ToList();
            return bars.Count == 0 ? "-" : $"{R(bars.Min())} to {R(bars.Max())}";
        }
        DevLog($"watch | {rock} | early breaks | {breaks.Count(h => h.Early)} early, the bar before them {Range(breaks.Where(h => h.Early))}; " +
            $"{breaks.Count(h => !h.Early)} not early, the bar before them {Range(breaks.Where(h => !h.Early))}");

        // Which share of the hits broke the rock, by where the bar stood before the hit.
        var shares = hits.Where(h => h.Before != null).GroupBy(h => Math.Min(3, (int)Math.Floor(h.Before!.Value * 4)))
            .OrderBy(g => g.Key).Select(g => $"bar {g.Key * 25}-{g.Key * 25 + 25}%: {g.Count(h => h.Breaks)} of {g.Count()} broke");
        DevLog($"watch | {rock} | breaks by the bar before the hit | {string.Join("; ", shares)}; full bars before a hit: {hits.Count(h => h.Before >= 1 - 1e-9)}");

        // Judge timing on the individual hits and compare amounts on unshattered breaks.
        var plainBreaks = breaks.Where(h => !h.Shattered).ToList();
        int xpOnWhole = whole.Count(h => h.Xp > 0), xpOnBreak = breaks.Count(h => h.Xp > 0);
        bool singles = hits.All(h => h.Hits <= 1);
        bool both = singles && whole.Count > 0 && breaks.Count > 0;
        double? xpEach = plainBreaks.Count > 0 ? plainBreaks.Average(h => h.Xp) : null;
        string xpTiming = !both ? "NOT DECIDED" : xpOnWhole == 0 && xpOnBreak == breaks.Count ? "XP ON A BREAK ONLY"
            : xpOnWhole == whole.Count && xpOnBreak == breaks.Count ? "XP ON EVERY HIT" : "OTHER XP TIMING";
        Say("XP", xpTiming, $"XP on {xpOnBreak} of {breaks.Count} breaks and {xpOnWhole} of {whole.Count} other hits; " +
            $"{R(xpEach)} per plain break, listed {R(ours.XpEach)}; measured {R(hits.Sum(h => h.Xp) / seconds * 3600)} an hour over uninterrupted mining, ours {R(ours.XpPerHour)}");

        // The bank is independent of ActionComponent.ItemAdded. A zero counts only when both quantities were read.
        if (ours.Ore is { } ore)
        {
            bool readable = singles && hits.All(h => h.BankGain is >= 0);
            double? average = readable && plainBreaks.Count > 0 ? plainBreaks.Average(h => (double)h.BankGain!.Value) : null;
            int oreOnBreak = breaks.Count(h => h.BankGain > 0), oreOnWhole = whole.Count(h => h.BankGain > 0);
            string verdict = !readable || breaks.Count == 0 ? "NOT DECIDED"
                : oreOnBreak == 0 ? "NO BANK GAIN ON OBSERVED BREAKS"
                : !both ? "BANK GAIN ON BREAKS; NO OTHER HITS TO COMPARE"
                : oreOnBreak == breaks.Count && oreOnWhole == 0 ? "BANK GAIN ON BREAKS ONLY"
                : oreOnBreak == breaks.Count && oreOnWhole == whole.Count ? "BANK GAIN ON EVERY HIT" : "MIXED BANK GAINS";
            Say("the ore", verdict, $"{ore.Id}: bank increased on {oreOnBreak} of {breaks.Count} breaks and {oreOnWhole} of {whole.Count} other hits; " +
                $"bank change per plain break [{string.Join(", ", plainBreaks.GroupBy(h => h.BankGain).Select(g => $"{g.Key?.ToString() ?? "unread"} x{g.Count()}"))}], " +
                $"average {R(average)}, " +
                $"window {R(ours.OrePerBreak)} per break; action item events gave {hits.Sum(h => h.Items.Where(i => Equals(i.Item, ore)).Sum(i => i.Quantity))} total" +
                (ours.PerHit.Any(d => Equals(d.Item, ore)) ? "; the on-hit list also includes this product, so the bank combines both rewards" : ""));
            // Compare the ordinary break product only where doubling is the sole counted source of variation.
            if (average is { } observed && start == end && ours.UnknownBreakReward == null &&
                !ours.PerHit.Any(d => Equals(d.Item, ore) && d.Chance > 0) && !ours.NotCounted.Any(b => b.Key == RockRates.MoreOre))
            {
                double seOre = ours.OreQuantity * Math.Sqrt(ours.Doubling * (1 - ours.Doubling) / plainBreaks.Count);
                bool possible = plainBreaks.All(h => h.BankGain == ours.OreQuantity || h.BankGain == 2L * ours.OreQuantity);
                Say("ore per plain break", possible && Math.Abs(observed - ours.OrePerBreak) <= 2 * seOre ? "FITS" : "DIFFERS",
                    $"bank {R(observed)}, estimate {R(ours.OrePerBreak)}, standard error {R(seOre)} over {plainBreaks.Count} plain breaks");
            }
            else Say("ore per plain break", "NOT DECIDED", "a clean sample needs a stable break quantity and doubling chance, with no other source of this product");
        }
        else Say("the ore", "NOT DECIDED", "the recipe has no break product to count in the bank");

        // Every other item, a hit at a time, against ours.
        foreach (var group in hits.SelectMany(h => h.Items.Select(i => (Hit: h, i.Item, i.Quantity))).Where(x => !ReferenceEquals(x.Item, ours.Ore)).GroupBy(x => x.Item))
        {
            int total = group.Sum(x => x.Quantity), onBreaks = group.Where(x => x.Hit.Breaks).Sum(x => x.Quantity);
            RockRates.Drop? counted = ours.PerHit.FirstOrDefault(d => ReferenceEquals(d.Item, group.Key));
            DevLog($"watch | {rock} | item {group.Key.Id} | {total} in {n} hits ({R((double)total / n)} a hit), {onBreaks} of them on a break; ours " +
                (counted != null ? $"{R(counted.Chance * counted.Each)} a hit" : ours.NotCountedItems.Any(s => ReferenceEquals(s.Item, group.Key)) ? "not counted (its hit or break not known)" : "NONE: NOT IN OUR LIST"));
        }
        foreach (RockRates.Drop drop in ours.PerHit.Where(d => !hits.Any(h => h.Items.Any(i => ReferenceEquals(i.Item, d.Item)))))
            DevLog($"watch | {rock} | item {drop.Item.Id} | none in {n} hits; ours {R(drop.Chance * drop.Each)} a hit");

        // Shatter: what a shattered hit brought, against the game's own multipliers.
        var shattered = hits.Where(h => h.Shattered).ToList();
        Say("shatter", shattered.Count == 0 ? "NO SHATTER SEEN" : shattered.All(h => h.Breaks) ? "SHATTER SEEN ON A BREAK" : "SHATTER SEEN ON A HIT THAT DID NOT BREAK",
            shattered.Count == 0 ? $"the shatter chance {R(ours.Rock.Stats.ShatterChance?.Total.Value ?? 0)}"
                : $"{shattered.Count} shattered: [{string.Join("; ", shattered.Select(h => $"XP +{R(h.Xp)}, items {string.Join(" ", h.Items.Select(i => $"{i.Item.Id} x{i.Quantity}"))}"))}]; " +
                  $"the game's multipliers: ore x{MiningActionComponent.ShatterProductMultiplier}, XP x{R(MiningActionComponent.ShatterExperienceMultiplier)}; " +
                  $"listed per break {R(ours.XpEach)} XP, a break {ours.OreQuantity} ore");
        return verdicts;
    }

    // The game's own tracker of Mining (MiningEntity.Tracker): over each of its periods the Mining XP and the items, and
    // an hour by it beside ours, only for time observed continuously on that rock. Tracker periods are in milliseconds.
    void DevTracker(string when, DevHitWatch watch)
    {
        try
        {
            var tracker = _mining!.Tracker;
            ExperienceComponent experience = _mining.Experience;
            var items = watch.Hits.SelectMany(h => h.Items.Select(i => i.Item)).ToHashSet();
            if (watch.Action.CurrentRock?.Recipe.Product?.Item is { } product) items.Add(product);
            RockRates? ours = _mining.Action.CurrentRock is { } rock ? RockRates.Read(rock, _mining, Shown) : null;
            int i = 0;
            foreach (var period in tracker.Periods)
            {
                double xp = period.Experience.TryGetValue(experience, out var list) ? list.Total : 0;
                double covered = Math.Min(period.CycleDuration, period.Experience.RecordedCount * period.SamplePeriod);
                string perHour = covered > 0 ? R(xp / covered * TimeSpan.FromHours(1).TotalMilliseconds) : "-";
                // Only attribute a completed period to one rock. The newest sample may end before this
                // tick. Until its boundary was seen, allow a full extra sample when looking back.
                long endTick = watch.SampleEnded.GetValueOrDefault(period, tracker.CurrentTick - period.SampleTicks);
                long fromTick = endTick - Math.Min(period.CycleTicks, period.Experience.RecordedCount * period.SampleTicks);
                var markers = tracker.Markers;
                var first = markers.LastOrDefault(m => m.Tick <= fromTick);
                bool describesRock = ours != null && first != null && Equals(first.Description, ours.Rock.Recipe) &&
                    markers.Where(m => m.Tick > fromTick).All(m => Equals(m.Description, ours.Rock.Recipe));
                bool sameRock = watch.Action.IsStarted && ours != null && covered > 0 &&
                    (fromTick >= watch.SameRockTick || describesRock);
                DevLog($"tracker | {when} | period {i++}: cycle {R(period.CycleDuration)}, {period.SampleCount} samples of {R(period.SamplePeriod)}, " +
                    $"{period.Experience.RecordedCount} recorded | Mining XP {R(xp)}, over {R(covered)} ms, an hour by it {perHour}, ours {(sameRock ? $"{R(ours!.XpPerHour)} at {ours.Name}" : "- (period not observed on one rock)")} | " +
                    $"items [{string.Join(", ", items.Select(item => $"{item.Id} {(period.Items.TryGetValue(item, out var l) ? l.Total : 0)}"))}]");
            }
            if (i == 0) DevLog($"tracker | {when} | the tracker has no periods");
        }
        catch (Exception e) { DevLog($"tracker | {when} | could not be read: {e.Message}"); }
    }
}
#endif
