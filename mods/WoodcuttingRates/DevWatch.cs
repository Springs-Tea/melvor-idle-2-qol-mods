#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Components.Trackers;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Woodcutting;
using Godot;
using MelvorModLoader;

namespace WoodcuttingRates;

// Development: a file "woodcuttingwatch.request" in the mod's folder starts a silent watch of the game's woodcutting, and
// "woodcuttingwatch.done" is written when it ends. The file holds a number of minutes (10 when empty), which the watch
// logs: that is how long it runs at most, the request's own choice. It opens nothing, clicks nothing, does not wait on
// the mouse and changes nothing: it only listens to the game's own events, as Fishing Rates' DevWatch listens to its
// catches (mods/FishingRates/Dev.cs). If the character is not cutting a tree when it starts, it says so in one line and
// ends. Otherwise every hit is one "Dev: hit" line (the tree, the damage, double, weak spot, the tree's hit points before
// and after, the XP and the items the game added in the same frame) and every cut is in the line of its frame (instant
// or not, what it added, the overgrowth it put on). It ends when the minutes are up, or after a minute without a hit,
// with one summary line and the verdicts in capitals, and its last line says it stopped listening and changed nothing.
//   pwsh .claude\codex\check-mod.ps1 -Worktree <worktree> -Mod WoodcuttingRates -Request woodcuttingwatch.request -Produces woodcuttingwatch.done -Text 10 -Seconds 900 -Out ...
// Every listener is guarded (a fault of the watch never reaches the game's tick) and is taken off when the watch ends,
// when the mod stops and when another character is opened (DevUnlisten).
public partial class WoodcuttingRatesMod
{
    const string WatchFile = "woodcuttingwatch.request", WatchDoneFile = "woodcuttingwatch.done";
    const ulong QuietFor = 60000;

    // What the game did in one frame: a hit, and with it, or alone, a cut; the XP, the items and the overgrowth of it.
    // Everything one action of the game does happens in one tick of the game, so in one frame.
    sealed class DevMoment
    {
        public ulong Frame, At;
        public readonly List<(WoodcuttingTree Tree, int Damage, bool Double, bool Weak, int MaxHp)> Hits = new();
        public readonly List<(string Tree, int Old, int New)> Health = new();
        public readonly List<(WoodcuttingTree Tree, bool Instant)> Cuts = new();
        public double Xp;
        public readonly Dictionary<Item, int> Items = new(ReferenceEqualityComparer.Instance);
        public readonly List<string> Effects = new();
    }

    sealed class DevHitWatch
    {
        public ulong Since, Minutes, LastHit;
        public readonly List<Action> Off = new();
        public DevMoment? Open;
        public readonly List<DevMoment> Moments = new();
        public bool Failed;
        public int TwoHitsInAFrame;
        public string Start = "";
        public IReadOnlyTrackerPeriod? Period;
        public double TrackerXp;
        public readonly Dictionary<Item, int> TrackerItems = new(ReferenceEqualityComparer.Instance);
    }

    DevHitWatch? _devHits;

    void DevWatchRequest()
    {
        if (GameAccess.IsComputingOffline || _stopped || _woodcutting == null) return;
        try
        {
            if (_devHits is not { } w)
            {
                string path = Path.Combine(_ctx.Directory, WatchFile);
                if (!File.Exists(path)) return;
                string asked = File.ReadAllText(path).Trim();
                File.Delete(path);
                DevWatchStart(asked);
                return;
            }
            // A frame that has passed is written out; the watch ends when its minutes are up or after a minute without a hit.
            if (w.Open is { } open && open.Frame != Engine.GetProcessFrames()) DevFlushMoment(w);
            ulong now = Time.GetTicksMsec();
            if (now - w.Since >= w.Minutes * 60000) DevWatchEnd($"its {w.Minutes} minutes are up");
            else if (now - w.LastHit >= QuietFor) DevWatchEnd($"no hit for {QuietFor / 1000} s (woodcutting {(_woodcutting.Action.IsStarted ? "started" : "stopped")})");
        }
        catch (Exception e)
        {
            // A fault of the watch ends the watch, never the mod.
            DevLog($"watch | failed: {e.GetType().Name}: {e.Message}");
            if (_devHits != null) DevWatchEnd("it failed (the line before)", summary: false);
            else File.WriteAllText(Path.Combine(_ctx.Directory, WatchDoneFile), "failed before it began: " + e.Message);
        }
    }

    void DevWatchStart(string asked)
    {
        ulong minutes = asked.Length == 0 ? 10 : ulong.TryParse(asked, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong m) && m is > 0 and <= 120 ? m : 10;
        var action = _woodcutting!.Action;
        DevLog($"watch | started: up to {minutes} minutes (the request file said '{asked}'{(asked.Length == 0 ? ", so 10" : "")}), or until a minute without a hit; only listening, nothing is changed");
        if (!action.IsStarted || action.CuttingTree == null)
        {
            DevLog($"watch | the character is not cutting a tree ({DevCutting()}): nothing to watch. Start cutting a tree by hand and send the request again.");
            File.WriteAllText(Path.Combine(_ctx.Directory, WatchDoneFile), "not cutting: nothing watched, nothing changed");
            return;
        }
        var w = _devHits = new DevHitWatch { Since = Time.GetTicksMsec(), Minutes = minutes, LastHit = Time.GetTicksMsec(), Start = DevCutting() };
        ExperienceComponent xp = _woodcutting.Experience;
        EventHandler<TreeDamagedEventArgs> damaged = (_, e) => DevWatchEvent(w, () =>
        {
            DevMoment m = DevMomentNow(w);
            if (m.Hits.Count > 0)
            {
                // Two hits in one frame (the game caught up after a slow frame): the second starts a moment of its own.
                w.TwoHitsInAFrame++;
                DevFlushMoment(w);
                m = DevMomentNow(w);
            }
            m.Hits.Add((e.Tree, e.DamageDealt, e.DoubleDamage, e.IsWeakSpot, e.Tree?.Stats.MaxHealth.Value ?? 0));
            w.LastHit = Time.GetTicksMsec();
        });
        EventHandler<TreeCutEventArgs> cut = (_, e) => DevWatchEvent(w, () => DevMomentNow(w).Cuts.Add((e.Tree, e.Instant)));
        EventHandler<ItemAddedEventArgs> item = (_, e) => DevWatchEvent(w, () =>
        {
            if (e.Item == null) return;
            var items = DevMomentNow(w).Items;
            items[e.Item] = items.GetValueOrDefault(e.Item) + e.Quantity;
        });
        EventHandler<ValueChangedEventArgs<double>> gained = (_, e) => DevWatchEvent(w, () => DevMomentNow(w).Xp += e.NewValue - e.OldValue);
        action.TreeDamaged += damaged;
        action.TreeCut += cut;
        action.ItemAdded += item;
        xp.XpChanged += gained;
        w.Off.Add(() =>
        {
            action.TreeDamaged -= damaged;
            action.TreeCut -= cut;
            action.ItemAdded -= item;
            xp.XpChanged -= gained;
        });
        foreach (WoodcuttingTree tree in action.Trees.Values)
        {
            if (tree?.Health is not { } health) continue;
            EventHandler<ValueChangedEventArgs<int>> hp = (_, e) => DevWatchEvent(w, () => DevMomentNow(w).Health.Add((tree.Recipe.LocalId, e.OldValue, e.NewValue)));
            EventHandler<OvergrowthEffectEventArgs> added = (_, e) => DevWatchEvent(w, () => DevMomentNow(w).Effects.Add($"{tree.Recipe.LocalId} gets {e.Effect?.Effect?.LocalId} ({e.Effect?.StacksLeft} stacks)"));
            EventHandler<OvergrowthEffectEventArgs> removed = (_, e) => DevWatchEvent(w, () => DevMomentNow(w).Effects.Add($"{tree.Recipe.LocalId} loses {e.Effect?.Effect?.LocalId}"));
            health.CurrentChanged += hp;
            tree.EffectAdded += added;
            tree.EffectRemoved += removed;
            w.Off.Add(() =>
            {
                health.CurrentChanged -= hp;
                tree.EffectAdded -= added;
                tree.EffectRemoved -= removed;
            });
        }
        // The game's own tracker over the same minutes: its longest period, read now and at the end.
        try
        {
            w.Period = _woodcutting.Tracker?.Periods?.OrderByDescending(p => p.CycleDuration).FirstOrDefault();
            if (w.Period != null)
            {
                w.TrackerXp = w.Period.Experience.TryGetValue(xp, out var list) ? list.Total : 0;
                foreach (var (key, samples) in w.Period.Items) if (key != null) w.TrackerItems[key] = samples.Total;
            }
        }
        catch (Exception e) { DevLog("watch | the game's tracker could not be read: " + e.Message); w.Period = null; }
        WoodcuttingTree tree0 = action.CuttingTree;
        TreeRates ours = TreeRates.Read(tree0, _woodcutting, null, Shown);
        DevLog($"watch | {w.Start}; the tree {tree0.Recipe.Id}: hit points {tree0.Health?.Current} of {ours.HitPoints}, ours a hit every {R(ours.HitSeconds)} s, XP {R(ours.XpEach)} a hit, " +
            $"chances double {R(ours.DoubleDamageChance)} weak {R(ours.WeakSpotChance)} instant {R(ours.InstantCutChance)} doubling {R(ours.Doubling)}, logs {ours.Log?.Id} x{ours.LogQuantity}, " +
            $"per hit [{string.Join(", ", ours.PerHit.Select(DevDrop))}], per cut [{string.Join(", ", ours.PerCut.Select(DevDrop))}]; the game's two numbers of a weak spot " +
            $"{WoodcuttingActionComponent.BaseWeakSpotDamage} and {R(WoodcuttingActionComponent.MaxHitpointsWeakSpotDamage)}; the tracker's period " +
            (w.Period == null ? "none" : $"{R(w.Period.CycleDuration)} ({w.Period.SampleCount} samples of {R(w.Period.SamplePeriod)}): XP {R(w.TrackerXp)} so far"));
    }

    DevMoment DevMomentNow(DevHitWatch w)
    {
        ulong frame = Engine.GetProcessFrames();
        if (w.Open is { } open && open.Frame == frame) return open;
        if (w.Open != null) DevFlushMoment(w);
        return w.Open = new DevMoment { Frame = frame, At = Time.GetTicksMsec() };
    }

    // A guarded listener: a fault reading the game's event never reaches the game's tick; the rest are then not read.
    void DevWatchEvent(DevHitWatch w, Action read)
    {
        if (w.Failed || !ReferenceEquals(_devHits, w) || GameAccess.IsComputingOffline) return;
        try { read(); }
        catch (Exception e)
        {
            w.Failed = true;
            _ctx.Log($"Dev: watch | reading the game's event failed, the rest are not read: {e.GetType().Name}: {e.Message}");
        }
    }

    void DevFlushMoment(DevHitWatch w)
    {
        if (w.Open is not { } m) return;
        w.Open = null;
        if (w.Moments.Count < 5000) w.Moments.Add(m);
        string hits = string.Join("; ", m.Hits.Select(h => $"{h.Tree?.Recipe.LocalId}: damage {h.Damage}{(h.Double ? ", DOUBLE" : "")}{(h.Weak ? ", WEAK SPOT" : "")} of {h.MaxHp} hit points"));
        string health = string.Join(", ", m.Health.Select(h => $"{h.Tree} {h.Old} -> {h.New}"));
        string cuts = string.Join(", ", m.Cuts.Select(c => $"{c.Tree?.Recipe.LocalId}{(c.Instant ? " INSTANT" : "")}"));
        string items = string.Join(", ", m.Items.Select(i => $"{i.Key.Id} x{i.Value}"));
        DevLog($"{(m.Hits.Count > 0 ? "hit" : "cut")} | {(hits.Length > 0 ? hits : "no hit")} | hit points {(health.Length > 0 ? health : "unchanged")} | " +
            $"{(cuts.Length > 0 ? "CUT " + cuts : "no cut")} | XP +{R(m.Xp)} | items [{items}]{(m.Effects.Count > 0 ? " | overgrowth: " + string.Join(", ", m.Effects) : "")}");
    }

    void DevWatchUnlisten()
    {
        if (_devHits is not { } w) return;
        foreach (Action off in w.Off)
        {
            try { off(); } catch { }
        }
        w.Off.Clear();
    }

    // The summary: what the game did against the window's numbers, one verdict in capitals per question.
    void DevWatchEnd(string why, bool summary = true)
    {
        if (_devHits is not { } w) return;
        DevFlushMoment(w);
        DevWatchUnlisten();
        _devHits = null;
        if (!summary) DevLog($"watch | no summary: {why}");
        else
        {
            try { DevWatchSummary(w, why); }
            catch (Exception e) { DevLog($"watch | the summary could not be made: {e.GetType().Name}: {e.Message}"); }
        }
        DevLog($"watch | stopped listening ({why}); nothing was changed: no click, no page, no setting, {(_woodcutting != null ? DevCutting() : "no character")}");
        File.WriteAllText(Path.Combine(_ctx.Directory, WatchDoneFile), $"ended: {why}; nothing changed");
    }

    void DevWatchSummary(DevHitWatch w, string why)
    {
        var hits = w.Moments.SelectMany(m => m.Hits).ToList();
        var cuts = w.Moments.SelectMany(m => m.Cuts).ToList();
        double seconds = (Time.GetTicksMsec() - w.Since) / 1000.0;
        if (hits.Count == 0) { DevLog($"watch | summary: no hit in {seconds:0} s ({why}), {cuts.Count} cuts: nothing to compare"); return; }
        // The tree most hits went to; the comparisons are of it.
        WoodcuttingTree tree = hits.GroupBy(h => h.Tree).OrderByDescending(g => g.Count()).First().Key;
        TreeRates ours = TreeRates.Read(tree, _woodcutting!, null, Shown);
        var mine = w.Moments.Where(m => m.Hits.Any(h => ReferenceEquals(h.Tree, tree)) || m.Cuts.Any(c => ReferenceEquals(c.Tree, tree))).ToList();
        var hitOnly = mine.Where(m => m.Hits.Count > 0 && m.Cuts.Count == 0).ToList();
        var withCut = mine.Where(m => m.Cuts.Count > 0).ToList();
        var treeHits = hits.Where(h => ReferenceEquals(h.Tree, tree)).ToList();
        var treeCuts = cuts.Where(c => ReferenceEquals(c.Tree, tree)).ToList();
        int instant = treeCuts.Count(c => c.Instant);

        // The time of a hit.
        var times = mine.Where(m => m.Hits.Count > 0).Select(m => m.At).ToList();
        string time = times.Count < 2 ? "too few hits to time"
            : $"a hit every {R((times[^1] - times[0]) / 1000.0 / (times.Count - 1))} s measured, ours {R(ours.HitSeconds)} s";

        // Hits per cut, the hits of each cut counted from the cut before (the first cut's hits are cut short by the start).
        var perCut = new List<int>();
        int since = 0;
        bool first = true;
        foreach (DevMoment m in mine)
        {
            since += m.Hits.Count(h => ReferenceEquals(h.Tree, tree));
            if (m.Cuts.Count == 0) continue;
            if (!first) perCut.Add(since);
            first = false;
            since = 0;
        }
        string hitsPerCut = perCut.Count == 0 ? "NO WHOLE CUT SEEN"
            : $"HITS PER CUT MEASURED {R(perCut.Average())} over {perCut.Count} whole cuts [{string.Join(" ", perCut)}], ours {(ours.HitsPerCut is { } h ? R(h) : "- (not counted)")}";

        // The damage of each kind of hit, against the tree's hit points and the game's two numbers of a weak spot.
        string Kind(string name, Func<(WoodcuttingTree Tree, int Damage, bool Double, bool Weak, int MaxHp), bool> of)
        {
            var some = treeHits.Where(of).ToList();
            return some.Count == 0 ? $"{name}: none" : $"{name}: {some.Count}, damage [{string.Join(" ", some.Select(x => x.Damage).Distinct().Order())}] of [{string.Join(" ", some.Select(x => x.MaxHp).Distinct().Order())}] hit points";
        }
        var ordinary = treeHits.Where(x => !x.Double && !x.Weak).Select(x => x.Damage).Distinct().ToList();
        var doubled = treeHits.Where(x => x.Double && !x.Weak).ToList();
        var weak = treeHits.Where(x => x.Weak && !x.Double).ToList();
        string ordinaryVerdict = ordinary.Count == 0 ? "NO ORDINARY HIT SEEN" : ordinary.Count == 1 ? $"AN ORDINARY HIT DEALS {ordinary[0]}" : $"ORDINARY HITS DIFFER ({string.Join(" ", ordinary.Order())})";
        string doubleVerdict = doubled.Count == 0 ? "NO DOUBLE HIT SEEN" : ordinary.Count != 1 ? "DOUBLE HITS NOT COMPARED"
            : doubled.All(x => x.Damage == 2 * ordinary[0]) ? "A DOUBLE HIT DEALS TWICE AN ORDINARY ONE" : "A DOUBLE HIT DOES NOT DEAL TWICE AN ORDINARY ONE";
        int b = WoodcuttingActionComponent.BaseWeakSpotDamage;
        double f = WoodcuttingActionComponent.MaxHitpointsWeakSpotDamage;
        var candidates = new (string Name, Func<int, double> Of)[]
        {
            ("the first + the second x the hit points, rounded down", hp => b + Math.Floor(f * hp)),
            ("rounded to the nearest", hp => b + Math.Round(f * hp, MidpointRounding.AwayFromZero)),
            ("rounded up", hp => b + Math.Ceiling(f * hp)),
            ("the first + the second x the hit points, not rounded", hp => b + f * hp),
        };
        string weakVerdict = weak.Count == 0 ? "NO WEAK SPOT SEEN" : candidates.FirstOrDefault(c => weak.All(x => Math.Abs(c.Of(x.MaxHp) - x.Damage) < 1e-9)) is { Name: { } match }
            ? $"A WEAK SPOT DEALS {match} ({weak.Count} of {weak.Count} match{(weak.Select(x => x.MaxHp).Distinct().Count() == 1 && Math.Abs(f * weak[0].MaxHp % 1) < 1e-9 ? "; the hit points give no fraction, so the rounding is NOT DECIDED" : "")})"
            : "A WEAK SPOT IS NOT EXPLAINED BY THE GAME'S TWO NUMBERS";
        DevLog($"watch | summary | {seconds:0} s ({why}), the tree {tree.Recipe.Id}: {treeHits.Count} hits, {treeCuts.Count} cuts ({instant} instant), {time} | {hitsPerCut} | " +
            $"{Kind("ordinary", x => !x.Double && !x.Weak)}; {Kind("double", x => x.Double && !x.Weak)}; {Kind("weak spot", x => x.Weak && !x.Double)}; {Kind("double weak spot", x => x.Double && x.Weak)} | " +
            $"{ordinaryVerdict} | {doubleVerdict} | {weakVerdict} | measured shares: double {R(treeHits.Count(x => x.Double) / (double)treeHits.Count)} (ours {R(ours.DoubleDamageChance)}), " +
            $"weak spot {R(treeHits.Count(x => x.Weak) / (double)treeHits.Count)} (ours {R(ours.WeakSpotChance)}), instant cuts per hit {R(instant / (double)treeHits.Count)} (ours {R(ours.InstantCutChance)}); " +
            $"instant cuts with a hit in their frame {withCut.Count(m => m.Cuts.Any(c => c.Instant) && m.Hits.Count > 0)} of {instant}; two hits in one frame {w.TwoHitsInAFrame}");

        // XP: on every hit, or on a cut only.
        int hitXp = hitOnly.Count(m => m.Xp > 0), cutXp = withCut.Count(m => m.Xp > 0);
        string xpVerdict = hitOnly.Count >= 3 && hitXp == hitOnly.Count ? "XP ON EVERY HIT" : hitOnly.Count >= 3 && hitXp == 0 && cutXp > 0 ? "XP ON A CUT ONLY" : "XP NOT DECIDED";
        double xpEach = hitOnly.Count == 0 ? 0 : hitOnly.Average(m => m.Xp);
        DevLog($"watch | summary | XP: {hitXp} of {hitOnly.Count} frames with a hit and no cut added XP, {R(xpEach)} on average (ours {R(ours.XpEach)} a hit: " +
            $"{(Math.Abs(xpEach - ours.XpEach) < 0.01 ? "the same" : "DIFFER")}); {cutXp} of {withCut.Count} frames with a cut added XP, {R(withCut.Count == 0 ? 0 : withCut.Average(m => m.Xp))} on average | {xpVerdict}");

        // The logs: with every hit, with a cut only, or by the damage of a hit.
        Item? log = tree.Recipe.Product?.Item;
        if (log != null)
        {
            int hitLogs = hitOnly.Count(m => m.Items.ContainsKey(log)), cutLogs = withCut.Count(m => m.Items.ContainsKey(log));
            bool byDamage = hitLogs > 0 && hitOnly.Where(m => m.Items.ContainsKey(log)).All(m => m.Items[log] % Math.Max(1, m.Hits.Sum(x => x.Damage)) == 0 && m.Items[log] >= m.Hits.Sum(x => x.Damage)) &&
                hitOnly.Any(m => m.Hits.Sum(x => x.Damage) > 1);
            string logsVerdict = hitOnly.Count >= 3 && hitLogs == hitOnly.Count ? byDamage ? "LOGS ON EVERY HIT, AS MANY AS ITS DAMAGE" : "LOGS ON EVERY HIT"
                : hitOnly.Count >= 3 && hitLogs == 0 && cutLogs > 0 ? "LOGS ON A CUT ONLY" : "LOGS NOT DECIDED";
            DevLog($"watch | summary | logs ({log.Id}, ours x{ours.LogQuantity} at a time, doubling {R(ours.Doubling)}): in {hitLogs} of {hitOnly.Count} frames with a hit and no cut " +
                $"[{string.Join(" ", hitOnly.Where(m => m.Items.ContainsKey(log)).Take(20).Select(m => $"{m.Items[log]}/dmg {m.Hits.Sum(x => x.Damage)}"))}], " +
                $"in {cutLogs} of {withCut.Count} frames with a cut [{string.Join(" ", withCut.Where(m => m.Items.ContainsKey(log)).Take(20).Select(m => m.Items[log]))}]; " +
                $"{w.Moments.Sum(m => m.Items.GetValueOrDefault(log))} in all, {R(w.Moments.Sum(m => m.Items.GetValueOrDefault(log)) / seconds * 3600)} an hour measured, ours " +
                $"{(ours.LogsPerHour is { } l ? R(l) : "- (not counted)")} | {logsVerdict}");
        }

        // Every other item: per hit against our chances of a hit, per cut against our chances of a cut.
        var others = w.Moments.SelectMany(m => m.Items.Keys).Where(i => !ReferenceEquals(i, log)).Distinct(ReferenceEqualityComparer.Instance).Cast<Item>()
            .Concat(ours.PerHit.Select(d => d.Item)).Concat(ours.PerCut.Select(d => d.Item)).Distinct(ReferenceEqualityComparer.Instance).Cast<Item>().ToList();
        var said = new List<string>();
        foreach (Item item in others)
        {
            int inHits = hitOnly.Sum(m => m.Items.GetValueOrDefault(item)), inCuts = withCut.Sum(m => m.Items.GetValueOrDefault(item));
            double expectHits = ours.PerHit.Where(d => ReferenceEquals(d.Item, item)).Sum(d => d.Chance * d.Each) * hitOnly.Sum(m => m.Hits.Count);
            double expectCuts = (ours.PerCut.Where(d => ReferenceEquals(d.Item, item)).Sum(d => d.Chance * d.Each) + ours.PerHit.Where(d => ReferenceEquals(d.Item, item)).Sum(d => d.Chance * d.Each)) * withCut.Count;
            bool far = Math.Abs(inHits - expectHits) > 3 * Math.Sqrt(expectHits) + 2 || Math.Abs(inCuts - expectCuts) > 3 * Math.Sqrt(expectCuts) + 2;
            said.Add($"{item.Id}: {inHits} in frames with a hit and no cut (ours {R(expectHits)} with our chance of a hit), {inCuts} in frames with a cut " +
                $"(ours {R(expectCuts)} with our chances of a hit and a cut){(far ? "  FAR FROM OURS" : "")}");
        }
        DevLog($"watch | summary | other items: {(said.Count == 0 ? "none came and none is in our lists" : string.Join("; ", said))}; overgrowth put on: " +
            string.Join(", ", w.Moments.SelectMany(m => m.Effects).DefaultIfEmpty("none")));

        // The game's own tracker over the same minutes.
        if (w.Period is { } period)
        {
            double xpNow = period.Experience.TryGetValue(_woodcutting!.Experience, out var list) ? list.Total : 0;
            double xpSeen = w.Moments.Sum(m => m.Xp);
            var items = new List<string>();
            foreach (var (key, samples) in period.Items)
            {
                if (key == null) continue;
                int gained = samples.Total - w.TrackerItems.GetValueOrDefault(key);
                int seen = w.Moments.Sum(m => m.Items.GetValueOrDefault(key));
                if (gained != 0 || seen != 0) items.Add($"{key.Id} {gained} (the watch {seen}){(gained == seen ? "" : "  DIFFER")}");
            }
            DevLog($"watch | summary | the game's tracker over the same {seconds:0} s: Woodcutting XP {R(xpNow - w.TrackerXp)} (the watch {R(xpSeen)}" +
                $"{(Math.Abs(xpNow - w.TrackerXp - xpSeen) < 0.5 ? "" : "  DIFFER")}; per hour {R((xpNow - w.TrackerXp) / seconds * 3600)}, ours {R(ours.XpPerHour)}) | items {string.Join(", ", items)}");
        }
        else DevLog("watch | summary | the game's tracker was not read");
    }
}
#endif
