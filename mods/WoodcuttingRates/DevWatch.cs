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
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Woodcutting;
using Godot;
using MelvorModLoader;

namespace WoodcuttingRates;

// Development: a file "woodcuttingwatch.request" in the mod's folder starts a silent watch of the character's
// woodcutting, and "woodcuttingwatch.done" says how it ended. The file holds a number of minutes (10 when it is empty),
// which is logged. It opens nothing, clicks nothing, does not wait on the mouse and changes nothing: it only listens to
// the game's own events, as Mining Rates' watch listens to its hits (mods/MiningRates/DevWatch.cs): a hit
// (WoodcuttingActionComponent.TreeDamaged), a cut (TreeCut), a tree's hit points (RecipeHealth.CurrentChanged), its
// overgrowth (WoodcuttingTree.EffectAdded, EffectRemoved) and the Woodcutting XP (ExperienceComponent.XpChanged). The items
// are the bank's counts of every item the trees can give, read after each frame with a hit or a cut (DevBankRead), as
// Fishing Rates' bait check read its bait: the first watch took them from the action's ItemAdded, which the game never
// raised on 597 hits and 88 cuts (check-4-watch). That event and the bank's own ItemQuantityChanged are still heard, and
// logged beside the counts. If the character is not cutting a tree when it starts, it says so in one line and ends. It
// runs alone: a light check sent meanwhile waits for it to end.
//   pwsh .claude\codex\check-mod.ps1 -Worktree <worktree> -Mod WoodcuttingRates -Request woodcuttingwatch.request -Produces woodcuttingwatch.done -Text 10 -Seconds 900 -Out <folder>
// Every frame with a hit or a cut is one "Dev: hit" line: the tree, the damage, double and weak spot as the game says
// them, the tree's hit points before and after, the cut (instant or not), the order of the game's events, the XP, the
// items by the bank's counts (and what the two events said), the overgrowth it put on. It ends when the minutes are up or after a minute without a hit, with, for
// every tree hit, one line per question, each with a verdict in capitals; NOT DECIDED below MostCuts whole cuts or
// MostHits frames. The questions are the parts of the rule the window's hour rests on (TreeRates.LogsRule, HitsRule,
// HitItemsRule): how much an ordinary, a double and a weak spot hit takes (against the game's two numbers of a weak spot
// and each reading of TreeRates.HitRule), the hits a cut takes against each reading's, what the logs, the items of a hit
// (once a hit, or for every hit point it takes) and the XP come with. Then the game's
// own tracker (WoodcuttingEntity.Tracker) over its periods beside ours. Its last line says it stopped listening and
// changed nothing. Every listener runs inside a try, and all are taken off when it ends, on Stop (with the done file,
// DevStopped) and on another character (DevUnwatchAll).
public partial class WoodcuttingRatesMod
{
    const string WatchRequestFile = "woodcuttingwatch.request", WatchDoneFile = "woodcuttingwatch.done";
    // A minute without a hit ends the watch.
    const ulong WatchIdle = 60000;
    // Fewer whole cuts than this decide nothing about the hits a cut takes, and fewer frames of a kind nothing about what
    // they bring: Mining Rates' watch floor (its code review of turn 1: about 30 breaks before a verdict). At about 3 s a
    // hit and 5 to 10 hits a cut, ten minutes give 200 hits and 20 to 40 cuts.
    const int MostCuts = 30, MostHits = 30;

    // What the game did in one frame: a hit, and with it, or alone, a cut; the XP, the items and the overgrowth of it.
    // Everything one action of the game does happens in one tick of the game, so in one frame.
    sealed class DevMoment
    {
        public long Frame;
        public ulong At;
        public readonly List<(WoodcuttingTree Tree, int Damage, bool Double, bool Weak, int MaxHp)> Hits = new();
        public readonly List<(WoodcuttingTree Tree, int Old, int New)> Health = new();
        public readonly List<(WoodcuttingTree Tree, bool Instant)> Cuts = new();
        public double Xp;
        public int XpChanges;
        // What the bank's counts of the watched items moved by, read after the frame (DevBankRead): the items it brought.
        public readonly List<(Item Item, int Quantity)> Items = new();
        // Two witnesses beside the counts: the bank's own event of a count that changed (InventoryComponent.ItemQuantityChanged)
        // and the action's ItemAdded, which the first watch listened to and never heard (check-4-watch: items [] on 597 hits).
        public readonly List<(Item Item, int Change)> BankEvents = new();
        public readonly List<(Item Item, int Quantity)> Added = new();
        public readonly List<string> Effects = new();
        // The game's events of this frame in the order they came: hit, health, cut, xp, bank, item, effect.
        public readonly List<string> Order = new();

        public int ItemsOf(Item item) => Items.Where(i => ReferenceEquals(i.Item, item)).Sum(i => i.Quantity);
    }

    sealed class DevHitWatch
    {
        public ulong Since, LastHit, Length;
        public WoodcuttingActionComponent Action = null!;
        public readonly List<Action> Off = new();
        public readonly List<DevMoment> Moments = new();
        // Every tree's numbers when the watch began, to see whether they changed while it ran.
        public readonly Dictionary<WoodcuttingTree, string> Start = new(ReferenceEqualityComparer.Instance);
        public DevMoment? Open;
        public bool Failed;
        public int TwoHitsInAFrame;
        // The bank (null: not found, the items are not read), every item the trees can give (their logs and the items on
        // their cards' lists), its count at the start and at the last read, and what the counts moved by between moments.
        public BankEntity? Bank;
        public readonly List<Item> Watched = new();
        public readonly Dictionary<Item, int> StartCounts = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<Item, int> Counts = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<Item, int> Outside = new(ReferenceEqualityComparer.Instance);
        public ulong LastRead;
    }

    // Between two moments the bank's counts are read this often, so that what changes in it without a hit or a cut (an
    // item sold by hand) is told apart from what a hit brings.
    const ulong BankReadEvery = 250;

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
        var action = _woodcutting!.Action;
        DevLog($"watch | {minutes} minutes asked{(asked.Length == 0 ? " (the request was empty)" : $" (the request said '{asked}')")}, or until a minute without a hit; " +
            $"only listening, nothing is changed; {DevCutting()}");
        if (!action.IsStarted || !DevCuttingNow() || action.CuttingTree == null)
        {
            const string not = "not cutting when the watch began: nothing was listened to, nothing changed. Start cutting a tree by hand and send the request again.";
            DevLog("watch | " + not);
            File.WriteAllText(Path.Combine(_ctx.Directory, WatchDoneFile), not);
            return;
        }
        var w = new DevHitWatch { Action = action, Since = Time.GetTicksMsec(), Length = (ulong)minutes * 60000 };
        w.LastHit = w.Since;
        foreach (WoodcuttingTree tree in action.Trees.Values) w.Start[tree] = DevTreeStats(tree);
        DevBankStart(w);
        _devHits = w;
        DevWatchListen(w);
        WoodcuttingTree cut = action.CuttingTree;
        TreeRates cutting = TreeRates.Read(cut, _woodcutting, Shown);
        var mine = new[] { cutting.Log }.Concat(cutting.PerHit.Select(d => d.Item)).Concat(cutting.PerCut.Select(d => d.Item)).OfType<Item>().Distinct(ReferenceEqualityComparer.Instance).Cast<Item>();
        DevLog($"watch | listening; the tree cut {cut.Recipe.Id}, its hit points {cut.Health?.Current} of {cut.Stats.MaxHealth.Value}; its numbers {w.Start[cut]}; " +
            $"the game's two numbers of a weak spot {WoodcuttingActionComponent.BaseWeakSpotDamage} and {R(WoodcuttingActionComponent.MaxHitpointsWeakSpotDamage)}; " +
            (w.Bank == null ? "the bank (MelvorBase:Bank) NOT FOUND: the items are not read"
                : $"the items read from the bank's counts, as Fishing Rates' bait check read its bait: {w.Watched.Count} items of all trees, this tree's " +
                  $"[{string.Join(", ", mine.Select(i => $"{i.Id} {w.StartCounts.GetValueOrDefault(i)}"))}]"));
        DevTracker("at the start", null);
    }

    // The bank and every item the trees can give, with its count now. The first watch took the items from the action's
    // ItemAdded event, which the game did not raise once on 597 hits and 88 cuts (check-4-watch), though a cut's logs
    // always come; the bank's count of an item, which Fishing Rates' bait check read after every catch ("bank now 2789"),
    // is what this one reads (InventoryComponent.GetQuantity), with no event to depend on.
    void DevBankStart(DevHitWatch w)
    {
        w.Bank = _game != null && _game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? bank) ? bank as BankEntity : null;
        if (w.Bank == null) return;
        foreach (WoodcuttingTree tree in w.Action.Trees.Values)
        {
            if (tree == null) continue;
            TreeRates t = TreeRates.Read(tree, _woodcutting!, Shown);
            foreach (Item? item in new[] { t.Log }.Concat(t.PerHit.Select(d => d.Item)).Concat(t.PerCut.Select(d => d.Item)))
                if (item != null && !w.Counts.ContainsKey(item))
                {
                    int count = w.Bank.Inventory.GetQuantity(item);
                    w.Watched.Add(item);
                    w.Counts[item] = w.StartCounts[item] = count;
                }
        }
        w.LastRead = Time.GetTicksMsec();
    }

    // The bank's count of every watched item against the last read: what moved goes to the moment, or, with none (a frame
    // without a hit or a cut), to what changed outside them.
    static void DevBankRead(DevHitWatch w, DevMoment? m)
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

    // A tree's numbers as the game has them now, with every bonus of the character.
    static string DevTreeStats(WoodcuttingTree tree)
    {
        WoodcuttingActionStats s = tree.Stats;
        return $"interval {R(s.Interval.Value)} ms, hit points {s.MaxHealth.Value}, XP {R(s.Experience.XpToAdd.Value)}, logs x{s.ProductQuantity.Value}, " +
            $"doubling {R(s.DoublingChance?.Total.Value ?? 0)}, double damage {R(s.DoubleDamageChance?.Total.Value ?? 0)}, weak spot {R(s.WeakSpotChance?.Total.Value ?? 0)}, " +
            $"instant cut {R(s.InstantCutChance?.Total.Value ?? 0)}, overgrowth [{string.Join(", ", tree.ActiveEffects?.Values.Select(e => $"{e.Effect?.LocalId} {e.StacksLeft}") ?? [])}]";
    }

    // The game's events, each listener inside a try: one that fails says so once and the watch ends on the next tick.
    void DevWatchListen(DevHitWatch w)
    {
        var action = w.Action;
        ExperienceComponent experience = _woodcutting!.Experience;
        void Safe(Action listen)
        {
            if (w.Failed || !ReferenceEquals(_devHits, w) || GameAccess.IsComputingOffline) return;
            try { listen(); }
            catch (Exception e)
            {
                w.Failed = true;
                DevLog("watch | a listener failed, the watch ends: " + e.Message);
            }
        }
        EventHandler<TreeDamagedEventArgs> damaged = (_, e) => Safe(() =>
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
            m.Order.Add("hit");
        });
        EventHandler<TreeCutEventArgs> cut = (_, e) => Safe(() =>
        {
            DevMoment m = DevMomentNow(w);
            m.Cuts.Add((e.Tree, e.Instant));
            m.Order.Add("cut");
        });
        EventHandler<ItemAddedEventArgs> item = (_, e) => Safe(() =>
        {
            if (e.Item == null) return;
            DevMoment m = DevMomentNow(w);
            m.Added.Add((e.Item, e.Quantity));
            m.Order.Add("item");
        });
        EventHandler<ItemQuantityChangedEventArgs> banked = (_, e) => Safe(() =>
        {
            if (e.Item == null) return;
            DevMoment m = DevMomentNow(w);
            m.BankEvents.Add((e.Item, e.QuantityChange));
            m.Order.Add("bank");
        });
        EventHandler<ValueChangedEventArgs<double>> xp = (_, e) => Safe(() =>
        {
            DevMoment m = DevMomentNow(w);
            m.Xp += e.NewValue - e.OldValue;
            m.XpChanges++;
            m.Order.Add("xp");
        });
        action.TreeDamaged += damaged;
        action.TreeCut += cut;
        action.ItemAdded += item;
        experience.XpChanged += xp;
        w.Off.Add(() =>
        {
            action.TreeDamaged -= damaged;
            action.TreeCut -= cut;
            action.ItemAdded -= item;
            experience.XpChanged -= xp;
        });
        if (w.Bank is { } bank)
        {
            bank.Inventory.ItemQuantityChanged += banked;
            w.Off.Add(() => bank.Inventory.ItemQuantityChanged -= banked);
        }
        foreach (WoodcuttingTree tree in action.Trees.Values)
        {
            if (tree?.Health is not { } health) continue;
            WoodcuttingTree watched = tree;
            EventHandler<ValueChangedEventArgs<int>> hp = (_, e) => Safe(() =>
            {
                DevMoment m = DevMomentNow(w);
                m.Health.Add((watched, e.OldValue, e.NewValue));
                m.Order.Add("health");
            });
            EventHandler<OvergrowthEffectEventArgs> added = (_, e) => Safe(() =>
            {
                DevMoment m = DevMomentNow(w);
                m.Effects.Add($"{watched.Recipe.LocalId} gets {e.Effect?.Effect?.LocalId} ({e.Effect?.StacksLeft} stacks)");
                m.Order.Add("effect");
            });
            EventHandler<OvergrowthEffectEventArgs> removed = (_, e) => Safe(() =>
            {
                DevMoment m = DevMomentNow(w);
                m.Effects.Add($"{watched.Recipe.LocalId} loses {e.Effect?.Effect?.LocalId}");
                m.Order.Add("effect");
            });
            health.CurrentChanged += hp;
            tree.EffectAdded += added;
            tree.EffectRemoved += removed;
            w.Off.Add(() =>
            {
                health.CurrentChanged -= hp;
                watched.EffectAdded -= added;
                watched.EffectRemoved -= removed;
            });
        }
    }

    // The moment of this frame: a new one when the frame has changed, after the one before is logged.
    DevMoment DevMomentNow(DevHitWatch w)
    {
        long frame = (long)Engine.GetProcessFrames();
        if (w.Open is { } open && open.Frame == frame) return open;
        DevFlushMoment(w);
        return w.Open = new DevMoment { Frame = frame, At = Time.GetTicksMsec() };
    }

    void DevFlushMoment(DevHitWatch w)
    {
        if (w.Open is not { } m) return;
        w.Open = null;
        bool kept = m.Hits.Count > 0 || m.Cuts.Count > 0;
        // Read after the frame of the moment, when all it brought is in the bank (DevWatchTick, or the next frame's
        // first event).
        DevBankRead(w, kept ? m : null);
        if (kept)
        {
            if (w.Moments.Count < 20000) w.Moments.Add(m);
            if (m.Hits.Count > 0) w.LastHit = m.At;
        }
        if (!kept && m.Effects.Count == 0) return;
        string hits = string.Join("; ", m.Hits.Select(h => $"{h.Tree?.Recipe.LocalId}: damage {h.Damage}{(h.Double ? ", DOUBLE" : "")}{(h.Weak ? ", WEAK SPOT" : "")} of {h.MaxHp} hit points"));
        string health = string.Join(", ", m.Health.Select(h => $"{h.Tree.Recipe.LocalId} {h.Old} -> {h.New}"));
        string cuts = string.Join(", ", m.Cuts.Select(c => $"{c.Tree?.Recipe.LocalId}{(c.Instant ? " INSTANT" : "")}"));
        DevLog($"{(m.Hits.Count > 0 ? "hit" : "cut")} | {(hits.Length > 0 ? hits : "no hit")} | hit points {(health.Length > 0 ? health : "unchanged")} | " +
            $"{(cuts.Length > 0 ? "CUT " + cuts : "no cut")} | events [{string.Join(" ", m.Order)}] | XP +{R(m.Xp)} in {m.XpChanges} changes | " +
            $"items by the bank's counts [{string.Join(", ", m.Items.Select(i => $"{i.Item.Id} {i.Quantity:+0;-0}, bank now {w.Counts.GetValueOrDefault(i.Item)}"))}]" +
            (m.BankEvents.Count > 0 ? $" | the bank's event [{string.Join(", ", m.BankEvents.Select(i => $"{i.Item.Id} {i.Change:+0;-0}"))}]" : "") +
            (m.Added.Count > 0 ? $" | the action's ItemAdded [{string.Join(", ", m.Added.Select(i => $"{i.Item.Id} x{i.Quantity}"))}]" : "") +
            (m.Effects.Count > 0 ? " | overgrowth: " + string.Join(", ", m.Effects) : ""));
    }

    // Every tick while the watch runs: the last frame is logged, and the watch ends when its minutes are up, after a
    // minute without a hit, or when a listener failed.
    void DevWatchTick()
    {
        if (_devHits is not { } w) return;
        if (w.Open != null && w.Open.Frame != (long)Engine.GetProcessFrames()) DevFlushMoment(w);
        ulong now = Time.GetTicksMsec();
        // Between moments, what moves in the bank came with no hit or cut.
        if (w.Open == null && now - w.LastRead >= BankReadEvery) DevBankRead(w, null);
        if (w.Failed) DevWatchEnd("a listener failed");
        else if (now - w.Since >= w.Length) DevWatchEnd($"the {w.Length / 60000} minutes are up");
        else if (now - w.LastHit >= WatchIdle) DevWatchEnd($"a minute without a hit (woodcutting {(w.Action.IsStarted ? "started" : "stopped")})");
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

    // The summary: for every tree hit, one line per question with its verdict; then the tracker, then the last line.
    // Without a summary when another character was opened or the mod stopped: the trees of the one watched may be gone.
    void DevWatchEnd(string why, bool summarise = true)
    {
        if (_devHits is not { } w) return;
        DevFlushMoment(w);
        DevUnwatchHits();
        _devHits = null;
        double seconds = (Time.GetTicksMsec() - w.Since) / 1000.0;
        int hits = w.Moments.Sum(m => m.Hits.Count);
        var verdicts = new List<string>();
        try
        {
            DevLog($"watch | ended after {seconds:0} s: {why}; {hits} hits and {w.Moments.Sum(m => m.Cuts.Count)} cuts seen at " +
                $"[{string.Join(", ", w.Moments.SelectMany(m => m.Hits).GroupBy(h => h.Tree?.Recipe.LocalId ?? "?").Select(g => $"{g.Key} {g.Count()}"))}]; " +
                $"two hits in one frame {w.TwoHitsInAFrame} times");
            DevBankSummary(w);
            if (!summarise || _woodcutting == null) verdicts.Add("NO SUMMARY");
            else
            {
                foreach (var group in w.Moments.SelectMany(m => m.Hits.Select(h => h.Tree)).Where(t => t != null).GroupBy(t => t, ReferenceEqualityComparer.Instance)
                    .OrderByDescending(g => g.Count()))
                {
                    var tree = (WoodcuttingTree)group.Key!;
                    var mine = w.Moments.Where(m => m.Hits.Any(h => ReferenceEquals(h.Tree, tree)) || m.Cuts.Any(c => ReferenceEquals(c.Tree, tree))).ToList();
                    var said = DevWatchSummary(mine, tree, TreeRates.Read(tree, _woodcutting, Shown), w.Start.GetValueOrDefault(tree, "not read"), seconds);
                    verdicts.Add($"{tree.Recipe.LocalId}: {string.Join(", ", said)}");
                }
                if (verdicts.Count == 0) verdicts.Add("NO HIT SEEN");
                DevTracker($"at the end, after {seconds:0} s", w.Moments);
            }
        }
        catch (Exception e) { DevLog("watch | the summary failed: " + e.Message); verdicts.Add("THE SUMMARY FAILED"); }
        const string last = "stopped listening to the game's events; nothing was changed: no click, no page, no setting";
        DevLog($"watch | {last}; {(_woodcutting != null ? DevCutting() : "no character")}");
        File.WriteAllText(Path.Combine(_ctx.Directory, WatchDoneFile), $"{why}; {hits} hits in {seconds:0} s; {string.Join("; ", verdicts)}; {last}");
    }

    // The items over the whole watch: what the bank's counts moved by in frames with a hit or a cut and outside them, each
    // count from the start to the end, and the two events beside the counts, so that the next watch says whether the
    // bank's own event would have done (it is not relied on).
    void DevBankSummary(DevHitWatch w)
    {
        if (w.Bank == null)
        {
            DevLog("watch | the items | NOT READ: the bank (MelvorBase:Bank) was not found");
            return;
        }
        DevBankRead(w, null);
        var counted = DevSum(w.Moments.SelectMany(m => m.Items));
        var heard = DevSum(w.Moments.SelectMany(m => m.BankEvents));
        var added = DevSum(w.Moments.SelectMany(m => m.Added));
        static string Of(Dictionary<Item, int> sum) => "[" + string.Join(", ", sum.Select(p => $"{p.Key.Id} {p.Value:+0;-0}")) + "]";
        bool same = counted.All(p => heard.GetValueOrDefault(p.Key) == p.Value) &&
                    heard.Where(p => w.Counts.ContainsKey(p.Key)).All(p => counted.GetValueOrDefault(p.Key) == p.Value);
        var unwatched = heard.Keys.Where(i => !w.Counts.ContainsKey(i)).ToList();
        DevLog($"watch | the items | read from the bank's counts of {w.Watched.Count} items (InventoryComponent.GetQuantity, as Fishing Rates' bait check read " +
            $"its bait): in frames with a hit or a cut {Of(counted)}; outside them {Of(w.Outside)}; from the start to the end " +
            $"[{string.Join(", ", w.Watched.Where(i => w.Counts[i] != w.StartCounts[i]).Select(i => $"{i.Id} {w.StartCounts[i]} -> {w.Counts[i]}"))}] | " +
            $"the bank's own event in those frames {Of(heard)}: {(heard.Count == 0 && counted.Count == 0 ? "nothing either way" : same ? "THE SAME AS THE COUNTS" : "NOT THE SAME AS THE COUNTS")}" +
            (unwatched.Count > 0 ? $", items it named that are not watched [{string.Join(", ", unwatched.Select(i => i.Id))}]" : "") +
            $" | the action's ItemAdded {w.Moments.Sum(m => m.Added.Count)} times {Of(added)}");
    }

    static Dictionary<Item, int> DevSum(IEnumerable<(Item Item, int Quantity)> items)
    {
        var sum = new Dictionary<Item, int>(ReferenceEqualityComparer.Instance);
        foreach (var (item, quantity) in items) sum[item] = sum.GetValueOrDefault(item) + quantity;
        return sum;
    }

    // The hits a cut takes by a reading, from the tree's numbers at the end, with an ordinary hit of the damage measured.
    static double? DevExpected(HitRule rule, TreeRates t) => TreeRates.ExpectedHits(rule, t);

    List<string> DevWatchSummary(List<DevMoment> moments, WoodcuttingTree tree, TreeRates ours, string start, double seconds)
    {
        var verdicts = new List<string>();
        string id = tree.Recipe.Id;
        void Say(string question, string verdict, string words)
        {
            verdicts.Add(verdict);
            DevLog($"watch | {id} | {question} | {verdict}: {words}");
        }
        var hits = moments.SelectMany(m => m.Hits.Where(h => ReferenceEquals(h.Tree, tree))).ToList();
        var cuts = moments.SelectMany(m => m.Cuts.Where(c => ReferenceEquals(c.Tree, tree))).ToList();
        var hitOnly = moments.Where(m => m.Hits.Count > 0 && m.Cuts.Count == 0).ToList();
        var withCut = moments.Where(m => m.Cuts.Count > 0).ToList();
        int b = WoodcuttingActionComponent.BaseWeakSpotDamage;
        double f = WoodcuttingActionComponent.MaxHitpointsWeakSpotDamage;

        // The tree's numbers at the start and at the end: a level up or an overgrowth while it ran changes them.
        string end = DevTreeStats(tree);
        DevLog($"watch | {id} | the tree's numbers | at the start {start}; at the end {end}" + (start == end ? "" : "  CHANGED WHILE IT RAN"));

        // The time of a hit.
        var times = moments.Where(m => m.Hits.Count > 0).Select(m => m.At).ToList();
        DevLog($"watch | {id} | the time of a hit | " + (times.Count < 2 ? "too few hits to time"
            : $"{R((times[^1] - times[0]) / 1000.0 / (times.Count - 1))} s measured over {times.Count} hits, ours {R(ours.HitSeconds)} s"));

        // How much an ordinary hit takes.
        var ordinary = hits.Where(h => !h.Double && !h.Weak).ToList();
        var damages = ordinary.Select(h => h.Damage).Distinct().Order().ToList();
        int? d = ordinary.Count >= 10 && damages.Count == 1 ? damages[0] : null;
        Say("an ordinary hit", ordinary.Count < 10 ? "NOT DECIDED" : d is { } one ? $"AN ORDINARY HIT DEALS {one}" : "ORDINARY HITS DIFFER",
            $"{ordinary.Count} ordinary hits, damage [{string.Join(" ", ordinary.GroupBy(h => h.Damage).OrderBy(g => g.Key).Select(g => $"{g.Key} x{g.Count()}"))}] of " +
            $"[{string.Join(" ", ordinary.Select(h => h.MaxHp).Distinct().Order())}] hit points{(ordinary.Count < 10 ? " (fewer than 10 decide nothing)" : "")}");

        // A double hit.
        var doubled = hits.Where(h => h.Double && !h.Weak).ToList();
        Say("a double hit", doubled.Count == 0 ? "NO DOUBLE HIT SEEN" : d == null ? "NOT DECIDED"
                : doubled.All(h => h.Damage == 2 * d) ? "A DOUBLE HIT DEALS TWICE AN ORDINARY ONE" : "A DOUBLE HIT DEALS ANOTHER AMOUNT",
            $"{doubled.Count} double hits, damage [{string.Join(" ", doubled.GroupBy(h => h.Damage).OrderBy(g => g.Key).Select(g => $"{g.Key} x{g.Count()}"))}]; " +
            $"the chance {R(ours.DoubleDamageChance)}, seen on {R(hits.Count == 0 ? 0 : hits.Count(h => h.Double) / (double)hits.Count)} of the hits");

        // A weak spot, against each reading of the game's two numbers (with the ordinary damage measured, or 1).
        int ordinaryDamage = d ?? 1;
        var readings = TreeRatesReadings(ordinaryDamage);
        var weak = hits.Where(h => h.Weak && !h.Double).ToList();
        var weakDouble = hits.Where(h => h.Weak && h.Double).ToList();
        // A reading's weak spot need not be a whole number (not rounded): the game's damage is one, so such a reading fits
        // only where the share of the hit points is whole.
        static bool Same(double ours, int game) => Math.Abs(ours - game) < 1e-6;
        var fitting = readings.Where(r => weak.Count > 0 && weak.All(h => Same(TreeRates.WeakSpotDamage(r, h.MaxHp), h.Damage))).ToList();
        bool fraction = weak.Any(h => Math.Abs(f * h.MaxHp - Math.Round(f * h.MaxHp)) > 1e-9);
        Say("a weak spot", weak.Count == 0 ? "NO WEAK SPOT SEEN" : fitting.Count == 0 ? "A WEAK SPOT IS NOT EXPLAINED BY THE GAME'S TWO NUMBERS"
                : fitting.Count == 1 ? $"A WEAK SPOT DEALS THE BASE AND THE SHARE OF HIT POINTS {DevReadingName(fitting[0]).ToUpperInvariant()}"
                : $"A WEAK SPOT FITS {fitting.Count} READINGS",
            $"{weak.Count} weak spots, damage [{string.Join(" ", weak.Select(h => $"{h.Damage}/{h.MaxHp} hp"))}], the game's numbers {b} and {R(f)}; " +
            $"the readings that fit [{string.Join(", ", fitting.Select(DevReadingName))}]" + (weak.Count > 0 && !fraction ? "; the hit points give no fraction, so the rounding is NOT DECIDED" : "") +
            $"; double weak spots [{string.Join(" ", weakDouble.Select(h => $"{h.Damage}/{h.MaxHp} hp"))}]" +
            (weakDouble.Count > 0 && fitting.Count > 0 ? weakDouble.All(h => Same(2 * TreeRates.WeakSpotDamage(fitting[0], h.MaxHp), h.Damage)) ? ", TWICE A WEAK SPOT" : ", NOT TWICE A WEAK SPOT" : ""));

        // The hit points against the damage of a hit (a cut puts them back to the full ones).
        var steps = moments.Where(m => m.Hits.Count == 1 && m.Cuts.Count == 0 && ReferenceEquals(m.Hits[0].Tree, tree))
            .Select(m => (Damage: m.Hits[0].Damage, Down: m.Health.Where(x => ReferenceEquals(x.Tree, tree)).Sum(x => x.Old - x.New))).ToList();
        Say("the hit points on a hit that does not cut", steps.Count == 0 ? "NOT DECIDED"
                : steps.All(s => s.Down == s.Damage) ? "THEY GO DOWN BY THE DAMAGE" : "THEY GO DOWN BY ANOTHER AMOUNT",
            $"{steps.Count} such hits, [{string.Join(" ", steps.GroupBy(s => $"{s.Damage}->{s.Down}").Select(g => $"damage {g.Key} x{g.Count()}"))}]");

        // How many hits a cut takes: from one cut to the next, so that the hits before the first cut seen do not count.
        var cycles = new List<int>();
        int since = 0;
        bool first = true;
        foreach (DevMoment m in moments)
        {
            since += m.Hits.Count(h => ReferenceEquals(h.Tree, tree));
            if (!m.Cuts.Any(c => ReferenceEquals(c.Tree, tree))) continue;
            if (!first) cycles.Add(since);
            first = false;
            since = 0;
        }
        double mean = cycles.Count > 0 ? cycles.Average() : double.NaN;
        double se = cycles.Count > 1 ? Math.Sqrt(cycles.Sum(c => (c - mean) * (c - mean)) / (cycles.Count - 1) / cycles.Count) : double.NaN;
        bool enough = cycles.Count >= MostCuts;
        var fits = new List<HitRule>();
        var words = new List<string>();
        foreach (HitRule rule in readings)
        {
            double? expected = DevExpected(rule, ours);
            double z = expected is { } e && double.IsFinite(se) && se > 0 ? (mean - e) / se : double.NaN;
            bool fit = double.IsFinite(z) && Math.Abs(z) <= 2;
            if (fit) fits.Add(rule);
            words.Add($"{DevReadingName(rule)} {(expected is { } x ? R(Math.Round(x, 3)) : "never")}{(double.IsFinite(z) ? $" ({R(Math.Round(z, 2))} standard errors off{(fit ? ", fits" : "")})" : "")}");
        }
        Say("hits a cut takes", !enough ? "NOT DECIDED" : fits.Count == 0 ? "NO READING FITS" : fits.Count == readings.Count ? "EVERY READING FITS"
                : $"THE READINGS THAT FIT: {string.Join(", ", fits.Select(DevReadingName)).ToUpperInvariant()}",
            $"MEASURED {R(mean)} hits on average over {cycles.Count} whole cuts (standard error {R(se)}), {cuts.Count(c => c.Instant)} of {cuts.Count} cuts instant; " +
            $"by the readings with an ordinary hit of {ordinaryDamage}{(d == null ? " (not measured)" : "")}, from the tree's numbers at the end [{string.Join("; ", words)}]; " +
            $"the window's {(ours.HitsPerCut is { } h ? R(h) : "- (no rule settled)")}{(enough ? "" : $" (fewer than {MostCuts} whole cuts decide nothing)")}");

        // Instant cuts: a cut in a frame whose hit did not take the last hit points.
        DevLog($"watch | {id} | instant cuts | {cuts.Count(c => c.Instant)} instant of {cuts.Count} cuts over {hits.Count} hits ({R(hits.Count == 0 ? 0 : cuts.Count(c => c.Instant) / (double)hits.Count)} a hit), " +
            $"the chance {R(ours.InstantCutChance)} a hit; instant cuts in a frame with a hit {withCut.Count(m => m.Cuts.Any(c => c.Instant) && m.Hits.Count > 0)}");

        // XP on every hit, or on a cut only (the window's rule, TreeRates.XpRule, is the second: check-4-watch).
        int xpOnHit = hitOnly.Count(m => m.Xp > 1e-9), xpOnCut = withCut.Count(m => m.Xp > 1e-9);
        double xpEach = hitOnly.Count > 0 ? hitOnly.Average(m => m.Xp) : double.NaN;
        double xpCut = withCut.Count > 0 ? withCut.Average(m => m.Xp) : double.NaN;
        string xpSeen = hitOnly.Count < MostHits ? "NOT DECIDED" : xpOnHit >= 0.9 * hitOnly.Count ? "XP ON EVERY HIT" : xpOnHit == 0 && xpOnCut > 0 ? "XP ON A CUT ONLY" : "NOT DECIDED";
        string xpOurs = TreeRates.XpRule is { } xpRule ? xpRule.Reading == XpWith.Cut ? "XP ON A CUT ONLY" : "XP ON EVERY HIT" : "";
        Say("XP", xpSeen + (xpOurs.Length == 0 || xpSeen == "NOT DECIDED" ? "" : xpSeen == xpOurs ? " (THE WINDOW'S RULE)" : " (NOT THE WINDOW'S RULE)"),
            $"XP on {xpOnHit} of {hitOnly.Count} frames with a hit and no cut, on {xpOnCut} of {withCut.Count} with a cut; {R(xpEach)} a hit without a cut, {R(xpCut)} a frame with a cut, " +
            $"ours {R(ours.XpEach)} a cut: {(Math.Abs(xpCut - ours.XpEach) < 0.01 ? "the same" : "DIFFER")}; measured {R(moments.Sum(m => m.Xp) / Math.Max(1, seconds) * 3600)} an hour, " +
            $"ours {R(ours.XpPerHour)}{(hitOnly.Count < MostHits ? $" (fewer than {MostHits} frames decide nothing)" : "")}");

        // The logs: with every hit, with every hit for each hit point it takes, or with a cut only.
        if (ours.Log is { } log)
        {
            int q = ours.LogQuantity;
            var plain = hitOnly.Where(m => m.Hits.Count == 1).ToList();
            int logsOnHit = plain.Count(m => m.ItemsOf(log) > 0), logsOnCut = withCut.Count(m => m.ItemsOf(log) > 0);
            // A doubling may double a hit's logs: the plain and the doubled number both fit.
            bool PerHit(DevMoment m) => m.ItemsOf(log) == q || m.ItemsOf(log) == 2 * q;
            bool PerPoint(DevMoment m) => m.ItemsOf(log) == q * m.Hits[0].Damage || m.ItemsOf(log) == 2 * q * m.Hits[0].Damage;
            var told = plain.Where(m => m.ItemsOf(log) > 0 && m.Hits[0].Damage != 1).ToList();
            string verdict = plain.Count < MostHits ? "NOT DECIDED"
                : logsOnHit >= 0.9 * plain.Count ? told.Count == 0 ? "LOGS ON EVERY HIT (no hit took more than 1 hit point, so per hit point is NOT DECIDED)"
                    : told.All(PerPoint) && !told.All(PerHit) ? "LOGS ON EVERY HIT, FOR EACH HIT POINT IT TAKES" : told.All(PerHit) ? "LOGS ON EVERY HIT" : "LOGS ON EVERY HIT, IN ANOTHER NUMBER"
                : logsOnHit == 0 && logsOnCut > 0 ? "LOGS ON A CUT ONLY" : "NOT DECIDED";
            // Against the window's rule (TreeRates.LogsRule, the card's "Full Cut").
            string logsOurs = TreeRates.LogsRule?.Reading switch { LogsWith.Cut => "LOGS ON A CUT ONLY", LogsWith.EveryHit => "LOGS ON EVERY HIT", LogsWith.HitPoint => "LOGS ON EVERY HIT, FOR EACH HIT POINT IT TAKES", _ => "" };
            if (logsOurs.Length > 0 && verdict != "NOT DECIDED") verdict += verdict == logsOurs ? " (THE WINDOW'S RULE)" : " (NOT THE WINDOW'S RULE)";
            Say("the logs", verdict,
                $"{log.Id} in {logsOnHit} of {plain.Count} frames with one hit and no cut [{string.Join(" ", plain.Where(m => m.ItemsOf(log) > 0).GroupBy(m => $"{m.ItemsOf(log)}/damage {m.Hits[0].Damage}").Select(g => $"{g.Key} x{g.Count()}"))}], " +
                $"in {logsOnCut} of {withCut.Count} frames with a cut [{string.Join(" ", withCut.GroupBy(m => m.ItemsOf(log)).OrderBy(g => g.Key).Select(g => $"{g.Key} x{g.Count()}"))}]; " +
                $"the tree's quantity {q}, doubling {R(ours.Doubling)}; {moments.Sum(m => m.ItemsOf(log))} in all, measured {R(moments.Sum(m => m.ItemsOf(log)) / Math.Max(1, seconds) * 3600)} an hour, " +
                $"ours {(ours.LogsPerHour is { } l ? R(l) : "- (no rule settled)")}, with every hit it would be {R(ours.HitsPerHour * q * (1 + ours.Doubling))}" +
                (plain.Count < MostHits ? $" (fewer than {MostHits} frames decide nothing)" : ""));
        }

        // Every other item: per hit against our chances of a hit, once a hit and for every hit point a hit takes (the same
        // evidence that settles what the logs come with settles what the items of a hit come with: the code review of
        // turn 2, fault 5), and per cut against our chances of a cut.
        int hitsAlone = hitOnly.Sum(m => m.Hits.Count(h => ReferenceEquals(h.Tree, tree)));
        int pointsAlone = hitOnly.Sum(m => m.Hits.Where(h => ReferenceEquals(h.Tree, tree)).Sum(h => h.Damage));
        static bool Fits(double seen, double expect) => Math.Abs(seen - expect) <= 3 * Math.Sqrt(Math.Max(0, expect)) + 2;
        var hitItems = new List<(bool Once, bool Point)>();
        var others = moments.SelectMany(m => m.Items.Select(i => i.Item)).Where(i => !ReferenceEquals(i, ours.Log))
            .Concat(ours.PerHit.Select(x => x.Item)).Concat(ours.PerCut.Select(x => x.Item)).Distinct(ReferenceEqualityComparer.Instance).Cast<Item>().ToList();
        foreach (Item item in others)
        {
            int inHits = hitOnly.Sum(m => m.ItemsOf(item)), inCuts = withCut.Sum(m => m.ItemsOf(item));
            double onHit = ours.PerHit.Where(x => ReferenceEquals(x.Item, item)).Sum(x => x.Chance * x.Each);
            double expectHits = onHit * hitsAlone, expectPoints = onHit * pointsAlone;
            double expectCuts = (ours.PerCut.Where(x => ReferenceEquals(x.Item, item)).Sum(x => x.Chance * x.Each) + onHit) * withCut.Count;
            bool once = Fits(inHits, expectHits), point = Fits(inHits, expectPoints);
            if (onHit > 0) hitItems.Add((once, point));
            bool far = (!once && !point) || !Fits(inCuts, expectCuts);
            DevLog($"watch | {id} | item {item.Id} | {inHits} in frames with a hit and no cut (ours {R(expectHits)} once a hit, {R(expectPoints)} for every hit point " +
                $"a hit takes), {inCuts} in frames with a cut (ours {R(expectCuts)} by our chances of a hit and a cut){(far ? "  FAR FROM OURS" : "")}");
        }
        if (hitItems.Count > 0)
            Say("the items of a hit", hitOnly.Count < MostHits ? "NOT DECIDED" : pointsAlone == hitsAlone ? "NOT DECIDED (no hit took more than 1 hit point)"
                    : hitItems.All(x => x.Once && !x.Point) ? "ITEMS OF A HIT ONCE A HIT"
                    : hitItems.All(x => x.Point && !x.Once) ? "ITEMS OF A HIT FOR EACH HIT POINT IT TAKES" : "NOT DECIDED",
                $"{hitItems.Count} items on our list of a hit: {hitItems.Count(x => x.Once)} fit once a hit, {hitItems.Count(x => x.Point)} fit for every hit point; " +
                $"{hitsAlone} hits taking {pointsAlone} hit points in frames without a cut{(hitOnly.Count < MostHits ? $" (fewer than {MostHits} frames decide nothing)" : "")}");
        DevLog($"watch | {id} | overgrowth put on or taken off | {string.Join(", ", moments.SelectMany(m => m.Effects).DefaultIfEmpty("none"))}");
        return verdicts;
    }

    // The readings of TreeRates.HitRule with an ordinary hit of the given damage (Dev.cs, DevHitReadings).
    static List<HitRule> TreeRatesReadings(int damage) => DevHitReadings.Select(r => r with { Damage = damage }).ToList();

    // The game's own tracker of Woodcutting (WoodcuttingEntity.Tracker): over each of its periods the Woodcutting XP and
    // the items, and an hour by it beside ours, if its times are in seconds (the log says which numbers it read).
    void DevTracker(string when, List<DevMoment>? moments)
    {
        try
        {
            var tracker = _woodcutting!.Tracker;
            ExperienceComponent experience = _woodcutting.Experience;
            var items = moments?.SelectMany(m => m.Items.Select(i => i.Item)).Distinct().ToList() ?? new List<Item>();
            TreeRates? ours = _woodcutting.Action.CuttingTree is { } tree ? TreeRates.Read(tree, _woodcutting, Shown) : null;
            int i = 0;
            foreach (var period in tracker.Periods)
            {
                double xp = period.Experience.TryGetValue(experience, out var list) ? list.Total : 0;
                double covered = Math.Min(period.CycleDuration, period.Experience.RecordedCount * period.SamplePeriod);
                string perHour = covered > 0 ? R(xp / covered * 3600) : "-";
                DevLog($"tracker | {when} | period {i++}: cycle {R(period.CycleDuration)}, {period.SampleCount} samples of {R(period.SamplePeriod)}, " +
                    $"{period.Experience.RecordedCount} recorded | Woodcutting XP {R(xp)}, an hour by it (if in seconds) {perHour}, ours {(ours == null ? "-" : R(ours.XpPerHour))} | " +
                    $"items [{string.Join(", ", items.Select(item => $"{item.Id} {(period.Items.TryGetValue(item, out var l) ? l.Total : 0)}"))}]");
            }
            if (i == 0) DevLog($"tracker | {when} | the tracker has no periods");
        }
        catch (Exception e) { DevLog($"tracker | {when} | could not be read: {e.Message}"); }
    }
}
#endif
