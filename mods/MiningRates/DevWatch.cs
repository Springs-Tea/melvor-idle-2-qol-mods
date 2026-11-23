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
// action says them, the break bar before and after, the XP and the items it added. The events of one hit come in one
// frame, and are told apart from the next hit by the frame. It ends when the minutes are up or after a minute without a
// hit, with one summary line per question, each with a verdict in capitals, and the game's own tracker
// (MiningEntity.Tracker) over its periods beside ours. Its last line says it stopped listening and changed nothing.
// Every listener runs inside a try, and all are taken off when it ends, on Stop and on another character
// (DevUnwatchAll).
public partial class MiningRatesMod
{
    const string WatchRequestFile = "miningwatch.request", WatchDoneFile = "miningwatch.done";
    // A minute without a hit ends the watch.
    const ulong WatchIdle = 60000;

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
        public readonly List<(string Rock, double Old, double New)> Progress = new();
        public double Xp;
        public int XpChanges;
        public readonly List<(Item Item, int Quantity)> Items = new();

        public bool IsHit => Hits > 0 || Mined;
        public bool Breaks => Broken || Success;
        public bool Shattered => HitShattered || MinedShattered;
    }

    sealed class DevHitWatch
    {
        public ulong Since, LastHit, Length;
        public MiningActionComponent Action = null!;
        public readonly List<Action> Off = new();
        public readonly List<DevHit> Hits = new();
        public readonly Dictionary<string, double> Bar = new();
        public DevHit? Open;
        public bool Failed;
    }

    DevHitWatch? _devHits;

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
        foreach (MiningRock rock in action.Rocks.Values) w.Bar[rock.Recipe.Id] = rock.Progress;
        _devHits = w;
        DevWatchListen(w);
        DevLog($"watch | listening; the rock mined {action.CurrentRock.Recipe.Id}, its break bar at {R(action.CurrentRock.Progress)}");
        DevTracker("at the start", null);
    }

    // The game's events, each listener inside a try: one that fails says so once and the watch ends on the next tick.
    void DevWatchListen(DevHitWatch w)
    {
        var action = w.Action;
        ExperienceComponent experience = _mining!.Experience;
        void Safe(Action listen)
        {
            if (w.Failed) return;
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
            EventHandler<RockHitEventArgs> hit = (_, e) => Safe(() =>
            {
                DevHit h = DevHitNow(w, id);
                h.Hits++;
                h.Success |= e.Success;
                h.HitShattered |= e.WasShattered;
            });
            EventHandler<ValueChangedEventArgs<double>> bar = (_, e) => Safe(() =>
            {
                DevHit h = DevHitNow(w, id);
                h.Before ??= e.OldValue;
                h.Progress.Add((id, e.OldValue, e.NewValue));
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
        });
        EventHandler<ValueChangedEventArgs<double>> xp = (_, e) => Safe(() =>
        {
            DevHit h = DevHitNow(w, null);
            h.Xp += e.NewValue - e.OldValue;
            h.XpChanges++;
        });
        EventHandler<ItemAddedEventArgs> item = (_, e) => Safe(() =>
        {
            if (e.Item != null) DevHitNow(w, null).Items.Add((e.Item, e.Quantity));
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
    DevHit DevHitNow(DevHitWatch w, string? rock)
    {
        long frame = (long)Engine.GetProcessFrames();
        if (w.Open == null || w.Open.Frame != frame)
        {
            DevFlushHit(w);
            w.Open = new DevHit { Frame = frame, At = Time.GetTicksMsec() };
        }
        if (rock != null && w.Open.Rock == "?")
        {
            w.Open.Rock = rock;
            w.Open.Before ??= w.Bar.TryGetValue(rock, out double bar) ? bar : null;
        }
        return w.Open;
    }

    void DevFlushHit(DevHitWatch w)
    {
        if (w.Open is not { } h) return;
        w.Open = null;
        if (h.IsHit)
        {
            w.Hits.Add(h);
            w.LastHit = h.At;
        }
        DevLog($"{(h.IsHit ? "hit" : "other")} | {h.Rock} | hit events {h.Hits}: success {h.Success}, shattered {h.HitShattered} | the action: " +
            (h.Mined ? $"broken {h.Broken}, early {h.Early}, shattered {h.MinedShattered}" : "no event") +
            $" | the break bar {(h.Before is { } b ? R(b) : "?")} -> [{string.Join(", ", h.Progress.Select(p => $"{R(p.Old)}->{R(p.New)}{(p.Rock == h.Rock ? "" : " of " + p.Rock)}"))}]" +
            $" | XP +{R(h.Xp)} in {h.XpChanges} changes | items [{string.Join(", ", h.Items.Select(i => $"{i.Item.Id} x{i.Quantity}"))}]" +
            (h.Hits > 1 ? $"  {h.Hits} HITS IN ONE FRAME" : ""));
    }

    // Every tick while the watch runs: the last hit is logged, and the watch ends when its minutes are up, after a
    // minute without a hit, or when a listener failed.
    void DevWatchTick()
    {
        if (_devHits is not { } w) return;
        if (w.Open != null && w.Open.Frame != (long)Engine.GetProcessFrames()) DevFlushHit(w);
        ulong now = Time.GetTicksMsec();
        if (w.Failed) DevWatchEnd("a listener failed");
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

    // The summary: one line per question, each with its verdict, then the tracker, then the last line. Without a summary
    // when another character was opened: the rocks of the one watched are gone.
    void DevWatchEnd(string why, bool summarise = true)
    {
        if (_devHits is not { } w) return;
        DevFlushHit(w);
        DevUnwatchHits();
        _devHits = null;
        double seconds = (Time.GetTicksMsec() - w.Since) / 1000.0;
        var verdicts = new List<string>();
        try
        {
            DevLog($"watch | ended after {seconds:0} s: {why}; {w.Hits.Count} hits seen at [{string.Join(", ", w.Hits.GroupBy(h => h.Rock).Select(g => $"{g.Key} {g.Count()}"))}]");
            if (!summarise) verdicts.Add("NO SUMMARY");
            else if (w.Hits.GroupBy(h => h.Rock).OrderByDescending(g => g.Count()).FirstOrDefault() is { } most &&
                _mining!.Action.Rocks.Values.FirstOrDefault(r => r.Recipe.Id == most.Key) is { } rock)
                verdicts = DevWatchSummary(most.ToList(), RockRates.Read(rock, _mining, Shown), seconds);
            else verdicts.Add("NO HIT SEEN");
            if (summarise) DevTracker($"at the end, after {seconds:0} s", w.Hits);
        }
        catch (Exception e) { DevLog("watch | the summary failed: " + e.Message); verdicts.Add("THE SUMMARY FAILED"); }
        const string last = "stopped listening to the game's events; nothing was changed";
        DevLog("watch | " + last);
        File.WriteAllText(Path.Combine(_ctx.Directory, WatchDoneFile), $"{why}; {w.Hits.Count} hits in {seconds:0} s; {string.Join("; ", verdicts)}; {last}");
    }

    List<string> DevWatchSummary(List<DevHit> hits, RockRates ours, double seconds)
    {
        var verdicts = new List<string>();
        void Say(string question, string verdict, string words)
        {
            verdicts.Add(verdict);
            DevLog($"watch | {question} | {verdict}: {words}");
        }
        int n = hits.Count;
        var breaks = hits.Where(h => h.Breaks).ToList();
        var whole = hits.Where(h => !h.Breaks).ToList();
        string rock = ours.Rock.Recipe.Id;

        // How many hits a break takes.
        Say("hits a break takes", breaks.Count >= 3 ? "MEASURED" : "NOT DECIDED",
            $"{n} hits on {rock}, {breaks.Count} breaks ({breaks.Count(h => h.Broken)} by the action's word, {breaks.Count(h => h.Success)} by the hit's), " +
            $"{breaks.Count(h => h.Early)} of them early; {(breaks.Count > 0 ? R((double)n / breaks.Count) : "-")} hits a break measured; ours " +
            $"{(ours.HitsPerBreak is { } mine ? R(mine) : "- (the game does not show it)")}; the game's break chance {R(ours.BreakChance)}, minimum {R(ours.MinimumProspecting)}");

        // How the break bar moves on a hit that does not break the rock.
        var steps = whole.Select(h => h.Progress.Where(p => p.Rock == rock).Sum(p => p.New - p.Old)).ToList();
        string stepWords = string.Join(", ", steps.GroupBy(s => Math.Round(s, 4)).OrderBy(g => g.Key).Select(g => $"{R(g.Key)} x{g.Count()}"));
        Say("the break bar on a hit that does not break", steps.Count == 0 ? "NOT DECIDED"
                : steps.All(s => Math.Abs(s - ours.BreakChance) < 1e-3) ? "IT GROWS BY THE BREAK CHANCE" : "IT GROWS BY ANOTHER AMOUNT",
            $"{steps.Count} such hits, steps [{stepWords}]; the break chance {R(ours.BreakChance)}");

        // Where the bar starts again after a break.
        var after = breaks.Select(h => h.Progress.Where(p => p.Rock == rock).Select(p => (double?)p.New).LastOrDefault()).OfType<double>().ToList();
        Say("the break bar after a break", after.Count == 0 ? "NOT DECIDED"
                : after.All(a => Math.Abs(a - ours.MinimumProspecting) < 1e-3) ? "IT STARTS AGAIN AT THE MINIMUM" : "IT STARTS AGAIN AT ANOTHER VALUE",
            $"after {after.Count} breaks [{string.Join(", ", after.GroupBy(a => Math.Round(a, 4)).Select(g => $"{R(g.Key)} x{g.Count()}"))}]; the minimum {R(ours.MinimumProspecting)}");

        // Which share of the hits broke the rock, by where the bar stood before the hit.
        var shares = hits.Where(h => h.Before != null).GroupBy(h => Math.Min(3, (int)Math.Floor(h.Before!.Value * 4)))
            .OrderBy(g => g.Key).Select(g => $"bar {g.Key * 25}-{g.Key * 25 + 25}%: {g.Count(h => h.Breaks)} of {g.Count()} broke");
        DevLog($"watch | breaks by the bar before the hit | {string.Join("; ", shares)}; full bars before a hit: {hits.Count(h => h.Before >= 1 - 1e-9)}");

        // XP on every hit, or on a break.
        var plain = hits.Where(h => !h.Shattered).ToList();
        int xpOnWhole = whole.Count(h => h.Xp > 1e-9), xpOnBreak = breaks.Count(h => h.Xp > 1e-9);
        double xpEach = plain.Count > 0 ? plain.Sum(h => h.Xp) / plain.Count : double.NaN;
        Say("XP", whole.Count >= 3 && xpOnWhole >= 0.9 * whole.Count ? "XP ON EVERY HIT"
                : whole.Count >= 3 && xpOnWhole == 0 && xpOnBreak > 0 ? "XP ON A BREAK ONLY" : "NOT DECIDED",
            $"XP on {xpOnWhole} of {whole.Count} hits that did not break and on {xpOnBreak} of {breaks.Count} that broke; {R(xpEach)} a hit without shatter, " +
            $"ours {R(ours.XpEach)} a hit: {(Math.Abs(xpEach - ours.XpEach) < 0.01 ? "the same" : "DIFFER")}; measured {R(hits.Sum(h => h.Xp) / Math.Max(1, seconds) * 3600)} an hour, ours {R(ours.XpPerHour)}");

        // The ore: on a break only, or on every hit; and how many a break brings.
        if (ours.Ore is { } ore)
        {
            int OreOf(DevHit h) => h.Items.Where(i => ReferenceEquals(i.Item, ore)).Sum(i => i.Quantity);
            int oreOnWhole = whole.Count(h => OreOf(h) > 0), oreOnBreak = breaks.Count(h => OreOf(h) > 0);
            var plainBreaks = breaks.Where(h => !h.Shattered).ToList();
            double perBreak = plainBreaks.Count > 0 ? plainBreaks.Average(h => (double)OreOf(h)) : double.NaN;
            Say("the ore", whole.Count >= 3 && oreOnWhole == 0 && oreOnBreak > 0 ? "ORE ON A BREAK ONLY"
                    : whole.Count >= 3 && oreOnWhole >= 0.9 * whole.Count ? "ORE ON EVERY HIT" : "NOT DECIDED",
                $"{ore.Id} on {oreOnWhole} of {whole.Count} hits that did not break and on {oreOnBreak} of {breaks.Count} that broke; {R(perBreak)} a break without shatter " +
                $"[{string.Join(", ", plainBreaks.GroupBy(OreOf).OrderBy(g => g.Key).Select(g => $"{g.Key} x{g.Count()}"))}], ours {R(ours.OrePerBreak)} " +
                $"({ours.OreQuantity} doubled on {R(ours.Doubling)}); measured {R(hits.Sum(OreOf) / Math.Max(1, seconds) * 3600)} an hour, ours {(ours.OrePerHour is { } o ? R(o) : "-")}");
        }

        // Every other item, a hit at a time, against ours.
        foreach (var group in hits.SelectMany(h => h.Items.Select(i => (Hit: h, i.Item, i.Quantity))).Where(x => !ReferenceEquals(x.Item, ours.Ore)).GroupBy(x => x.Item))
        {
            int total = group.Sum(x => x.Quantity), onBreaks = group.Where(x => x.Hit.Breaks).Sum(x => x.Quantity);
            RockRates.Drop? counted = ours.PerHit.FirstOrDefault(d => ReferenceEquals(d.Item, group.Key));
            DevLog($"watch | item {group.Key.Id} | {total} in {n} hits ({R((double)total / n)} a hit), {onBreaks} of them on a break; ours " +
                (counted != null ? $"{R(counted.Chance * counted.Each)} a hit" : ours.NotCountedItems.Any(s => ReferenceEquals(s.Item, group.Key)) ? "not counted (its hit or break not known)" : "NONE: NOT IN OUR LIST"));
        }
        foreach (RockRates.Drop drop in ours.PerHit.Where(d => !hits.Any(h => h.Items.Any(i => ReferenceEquals(i.Item, d.Item)))))
            DevLog($"watch | item {drop.Item.Id} | none in {n} hits; ours {R(drop.Chance * drop.Each)} a hit");

        // Shatter: what a shattered hit brought.
        var shattered = hits.Where(h => h.Shattered).ToList();
        Say("shatter", shattered.Count == 0 ? "NO SHATTER SEEN" : shattered.All(h => h.Breaks) ? "SHATTER SEEN ON A BREAK" : "SHATTER SEEN ON A HIT THAT DID NOT BREAK",
            shattered.Count == 0 ? $"the shatter chance {R(ours.Rock.Stats.ShatterChance?.Total.Value ?? 0)}"
                : $"{shattered.Count} shattered: [{string.Join("; ", shattered.Select(h => $"XP +{R(h.Xp)}, items {string.Join(" ", h.Items.Select(i => $"{i.Item.Id} x{i.Quantity}"))}"))}]; " +
                  $"the game's multipliers: ore x{MiningActionComponent.ShatterProductMultiplier}, XP x{R(MiningActionComponent.ShatterExperienceMultiplier)}");
        return verdicts;
    }

    // The game's own tracker of Mining (MiningEntity.Tracker): over each of its periods the Mining XP and the items, and
    // an hour by it beside ours, if its times are in seconds (the log says which numbers it read).
    void DevTracker(string when, List<DevHit>? hits)
    {
        try
        {
            var tracker = _mining!.Tracker;
            ExperienceComponent experience = _mining.Experience;
            var items = hits?.SelectMany(h => h.Items.Select(i => i.Item)).Distinct().ToList() ?? new List<Item>();
            RockRates? ours = _mining.Action.CurrentRock is { } rock ? RockRates.Read(rock, _mining, Shown) : null;
            int i = 0;
            foreach (var period in tracker.Periods)
            {
                double xp = period.Experience.TryGetValue(experience, out var list) ? list.Total : 0;
                double covered = Math.Min(period.CycleDuration, period.Experience.RecordedCount * period.SamplePeriod);
                string perHour = covered > 0 ? R(xp / covered * 3600) : "-";
                DevLog($"tracker | {when} | period {i++}: cycle {R(period.CycleDuration)}, {period.SampleCount} samples of {R(period.SamplePeriod)}, " +
                    $"{period.Experience.RecordedCount} recorded | Mining XP {R(xp)}, an hour by it (if in seconds) {perHour}, ours {(ours == null ? "-" : R(ours.XpPerHour))} | " +
                    $"items [{string.Join(", ", items.Select(item => $"{item.Id} {(period.Items.TryGetValue(item, out var l) ? l.Total : 0)}"))}]");
            }
            if (i == 0) DevLog($"tracker | {when} | the tracker has no periods");
        }
        catch (Exception e) { DevLog($"tracker | {when} | could not be read: {e.Message}"); }
    }
}
#endif
