#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GamesByMalcs.Melvor2.Components.CraftingAction;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Firemaking;
using Godot;
using MelvorModLoader;

namespace FiremakingRates;

// Development: a file "firemakingwatch.request" in the mod's folder starts a silent watch of the character's burning, and
// "firemakingwatch.done" says how it ended. The file holds a number of minutes (10 when it is empty), which is logged. It
// opens nothing, clicks nothing, does not wait on the mouse and changes nothing: it only listens to the game's own events,
// as Woodcutting Rates' watch listens to its hits (mods/WoodcuttingRates/DevWatch.cs): a fire (FiremakingActionComponent.
// RecipeCreated), its product (PrimaryProductCreated), a log kept (ItemPreserved), a Firemaking event (EventObserved), the
// action's ItemAdded, the bonfire's time added by a fire (FiremakingBonfireComponent.FireTimeAdded) and by logs put on it
// by hand (LogsAdded), and the Firemaking XP (ExperienceComponent.XpChanged). The items and coins are the bank's counts of
// every item and currency the logs use, make or may bring by an event, read after each frame with a fire, as Woodcutting
// Rates' watch and Fishing Rates' bait check read their counts (InventoryComponent.GetQuantity, the wallet's GetQuantity);
// the bank's own ItemQuantityChanged is heard beside them. If the character is not burning a log when it starts, it says
// so in one line and ends. It runs alone: a light check sent meanwhile waits for it to end.
//   pwsh .claude\codex\check-mod.ps1 -Worktree <worktree> -Mod FiremakingRates -Request firemakingwatch.request -Produces firemakingwatch.done -Text 10 -Seconds 900 -Out <folder>
// Every frame with a fire is one "Dev: burn" line: the log, the game's interval, the order of the game's events, the XP,
// the log used or kept, the coins and items by the bank's counts, the bonfire time added and the bonfire's time left, an
// event with its reward. It ends when the minutes are up or after a minute without a fire, with, for every log burnt, one
// line per question, each with a verdict in capitals; NOT DECIDED below MostBurns fires. The questions are the parts of
// the rule the window's hour rests on (LogRates.XpRule, LogsRule, ItemsRule, CoinsRule, BonfireRule): what a fire gives in
// XP, whether it uses its log or keeps it at the preservation chance, its coins, its items, whether it adds the log's
// bonfire time and how the bonfire's most time caps it, and every event seen with the seconds and fires between them.
// Then the game's own tracker (FiremakingEntity.Tracker) over its periods beside ours. Its last line says it stopped
// listening and changed nothing. Every listener runs inside a try, and all are taken off when it ends, on Stop (with the
// done file, DevStopped) and on another character (DevUnwatchAll).
public partial class FiremakingRatesMod
{
    const string WatchRequestFile = "firemakingwatch.request", WatchDoneFile = "firemakingwatch.done";
    // A minute without a fire ends the watch.
    const ulong WatchIdle = 60000;
    // Fewer fires than this decide nothing: Woodcutting Rates' and Mining Rates' watch floor (about 30 before a verdict).
    // At 2 to 5 s a log, ten minutes give 120 to 300 fires.
    const int MostBurns = 30;
    // Between two fires the bank's counts are read this often, so that what changes in it without a fire (an item sold by
    // hand) is told apart from what a fire brings.
    const ulong BankReadEvery = 250;

    // What the game did in one frame: a fire, and with it the XP, the items, the coins, the bonfire time and an event.
    // Everything one fire of the game does happens in one tick of the game, so in one frame.
    sealed class DevMoment
    {
        public long Frame;
        public ulong At;
        public readonly List<(ActiveFiremakingRecipe? Log, double Interval, bool Success)> Burns = new();
        public readonly List<int> Primary = new();
        public readonly List<(Item Item, int Quantity)> Preserved = new();
        public readonly List<(FiremakingEvent? Event, int Reward)> Events = new();
        public readonly List<double> FireTime = new(), LogsTime = new();
        public double Xp;
        public int XpChanges;
        // What the bank's counts and the wallet moved by, read after the frame (DevBankRead): what the fire took and brought.
        public readonly List<(Item Item, int Quantity)> Items = new();
        public readonly List<(Currency Currency, long Quantity)> Coins = new();
        // Witnesses beside the counts: the bank's own event of a count that changed, and the action's ItemAdded.
        public readonly List<(Item Item, int Change)> BankEvents = new();
        public readonly List<(Item Item, int Quantity)> Added = new();
        // The bonfire's time left once the frame is over, and its most.
        public double BonfireLeft, BonfireMost;
        public bool BonfireLit;
        // The game's events of this frame in the order they came.
        public readonly List<string> Order = new();

        public int ItemsOf(Item item) => Items.Where(i => ReferenceEquals(i.Item, item)).Sum(i => i.Quantity);
        public long CoinsOf(Currency currency) => Coins.Where(c => ReferenceEquals(c.Currency, currency)).Sum(c => c.Quantity);
    }

    sealed class DevBurnWatch
    {
        public ulong Since, LastBurn, Length;
        public FiremakingActionComponent Action = null!;
        public FiremakingBonfireComponent Bonfire = null!;
        public readonly List<Action> Off = new();
        public readonly List<DevMoment> Moments = new();
        // Every log's numbers when the watch began, to see whether they changed while it ran.
        public readonly Dictionary<ActiveFiremakingRecipe, string> Start = new(ReferenceEqualityComparer.Instance);
        public DevMoment? Open;
        public bool Failed;
        public int TwoBurnsInAFrame;
        // XP and bonfire time heard in frames without a fire.
        public double XpOutside, FireTimeOutside, LogsTimeOutside;
        // Every Firemaking event heard, in a frame with a fire or not: when, which, its reward, and the fires before it.
        public readonly List<(ulong At, FiremakingEvent? Event, int Reward, int Fires)> EventsSeen = new();
        public int Fires;
        // The bank (null: not found), every item and currency the logs use, make or may bring, their counts at the start
        // and at the last read, and what the counts moved by between fires.
        public BankEntity? Bank;
        public readonly List<Item> Watched = new();
        public readonly List<Currency> Wallet = new();
        public readonly Dictionary<Item, int> StartCounts = new(ReferenceEqualityComparer.Instance), Counts = new(ReferenceEqualityComparer.Instance),
            Outside = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<Currency, long> WalletStart = new(ReferenceEqualityComparer.Instance), WalletCounts = new(ReferenceEqualityComparer.Instance),
            WalletOutside = new(ReferenceEqualityComparer.Instance);
        public ulong LastRead;
    }

    DevBurnWatch? _devBurns;

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
        var action = _firemaking!.Action;
        DevLog($"watch | {minutes} minutes asked{(asked.Length == 0 ? " (the request was empty)" : $" (the request said '{asked}')")}, or until a minute without a fire; " +
            $"only listening, nothing is changed; {DevBurning()}");
        if (!LogRates.IsBurning(action) || !DevBurningNow())
        {
            const string not = "not burning when the watch began: nothing was listened to, nothing changed. Start burning a log by hand and send the request again.";
            DevLog("watch | " + not);
            File.WriteAllText(Path.Combine(_ctx.Directory, WatchDoneFile), not);
            return;
        }
        var w = new DevBurnWatch { Action = action, Bonfire = _firemaking.Bonfire, Since = Time.GetTicksMsec(), Length = (ulong)minutes * 60000 };
        w.LastBurn = w.Since;
        foreach (ActiveFiremakingRecipe log in action.ActiveRecipes.Values) if (log != null) w.Start[log] = DevLogStats(log);
        DevBankStart(w);
        _devBurns = w;
        DevWatchListen(w);
        ActiveFiremakingRecipe burnt = action.CreatingRecipe!;
        LogRates ours = LogRates.Read(burnt, _firemaking, _bank, Shown, false);
        var mine = ours.Costs.Select(c => c.Item).Concat(ours.Products.Select(p => p.Item)).Distinct(ReferenceEqualityComparer.Instance).Cast<Item>();
        DevLog($"watch | listening; the log burnt {burnt.Recipe.Id}, its numbers {w.Start[burnt]}; the bonfire {(w.Bonfire.IsActive ? "lit" : "out")}, " +
            $"time left {R(w.Bonfire.DurationTimer.TimeLeft)} of {R(w.Bonfire.DurationTimer.MaxTime)} ({w.Bonfire.CurrentBonfire?.Id}, its most {w.Bonfire.CurrentBonfire?.MaxDuration}); " +
            (w.Bank == null ? "the bank (MelvorBase:Bank) NOT FOUND: the items and coins are not read"
                : $"the items and coins read from the bank's counts: {w.Watched.Count} items and {w.Wallet.Count} currencies of all logs, this log's " +
                  $"[{string.Join(", ", mine.Select(i => $"{i.Id} {w.StartCounts.GetValueOrDefault(i)}"))}]" +
                  (ours.Coins is { } coins ? $", {coins.Id} {w.WalletStart.GetValueOrDefault(coins)}" : "")));
        DevTracker("at the start", null);
    }

    // The bank, every item and currency the logs use, make or may bring by an event, with their counts now (Woodcutting
    // Rates' DevBankStart: the action's ItemAdded did not come there, the bank's counts did).
    void DevBankStart(DevBurnWatch w)
    {
        w.Bank = _bank;
        if (w.Bank == null) return;
        void Watch(Item? item)
        {
            if (item == null || w.Counts.ContainsKey(item)) return;
            w.Watched.Add(item);
            w.Counts[item] = w.StartCounts[item] = w.Bank!.Inventory.GetQuantity(item);
        }
        void WatchCoins(Currency? currency)
        {
            if (currency == null || w.WalletCounts.ContainsKey(currency)) return;
            w.Wallet.Add(currency);
            w.WalletCounts[currency] = w.WalletStart[currency] = w.Bank!.Wallet.GetQuantity(currency);
        }
        foreach (ActiveFiremakingRecipe log in w.Action.ActiveRecipes.Values)
        {
            if (log == null) continue;
            FiremakingActionStats s = log.Stats;
            foreach (var node in s.ItemCost?.Values ?? []) Watch(node?.Value?.Item);
            foreach (var node in s.ProductChances?.Values ?? []) Watch(node?.Value?.Item);
            foreach (var node in s.CurrencyCost?.Values ?? []) WatchCoins(node?.Value?.Currency);
            WatchCoins(s.CurrencyReward?.Value?.Currency);
        }
        foreach (FiremakingEvent e in w.Action.Config.Events ?? Enumerable.Empty<FiremakingEvent>()) Watch(e?.Reward?.Item);
        w.LastRead = Time.GetTicksMsec();
    }

    // The bank's count of every watched item and currency against the last read: what moved goes to the moment, or, with
    // none (a frame without a fire), to what changed outside them.
    static void DevBankRead(DevBurnWatch w, DevMoment? m)
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
        foreach (Currency currency in w.Wallet)
        {
            long now = w.Bank.Wallet.GetQuantity(currency), was = w.WalletCounts[currency];
            if (now == was) continue;
            w.WalletCounts[currency] = now;
            if (m != null) m.Coins.Add((currency, now - was));
            else w.WalletOutside[currency] = w.WalletOutside.GetValueOrDefault(currency) + now - was;
        }
    }

    // A log's numbers as the game has them now, with every bonus of the character.
    static string DevLogStats(ActiveFiremakingRecipe log)
    {
        FiremakingActionStats s = log.Stats;
        return $"interval {R(s.Interval.Value)} ms, XP {R(s.Experience.XpToAdd.Value)}, cost [{string.Join(", ", (s.ItemCost?.Values ?? []).Select(n => $"{n?.Value?.Item?.Id} x{n?.Value?.Quantity}"))}], " +
            $"preservation {R(s.PreservationChance?.Total.Value ?? 0)}, doubling {R(s.DoublingChance?.Total.Value ?? 0)}, " +
            $"products [{string.Join(", ", (s.ProductChances?.Values ?? []).Select(n => $"{n?.Value?.Item?.Id} {R(n?.Value?.Chance ?? 0)} x{n?.Value?.Quantity}"))}], " +
            $"coins {s.CurrencyReward?.Value?.Currency?.Id} x{s.CurrencyReward?.Value?.Quantity}, bonfire time {R(FiremakingBonfireComponent.GetBonfireTimeForRecipe(log))}";
    }

    // The game's events, each listener inside a try: one that fails says so once and the watch ends on the next tick.
    void DevWatchListen(DevBurnWatch w)
    {
        var action = w.Action;
        var bonfire = w.Bonfire;
        ExperienceComponent experience = _firemaking!.Experience;
        void Safe(Action listen)
        {
            if (w.Failed || !ReferenceEquals(_devBurns, w) || GameAccess.IsComputingOffline) return;
            try { listen(); }
            catch (Exception e)
            {
                w.Failed = true;
                DevLog("watch | a listener failed, the watch ends: " + e.Message);
            }
        }
        EventHandler<CraftingRecipeCreatedEventArgs<ActiveFiremakingRecipe>> burnt = (_, e) => Safe(() =>
        {
            DevMoment m = DevMomentNow(w);
            if (m.Burns.Count > 0)
            {
                // Two fires in one frame (the game caught up after a slow frame): the second starts a moment of its own.
                w.TwoBurnsInAFrame++;
                DevFlushMoment(w);
                m = DevMomentNow(w);
            }
            m.Burns.Add((e.Recipe, e.Interval, e.IsSuccess));
            m.Order.Add("fire");
        });
        EventHandler<PrimaryProductCreatedEventArgs<ActiveFiremakingRecipe>> primary = (_, e) => Safe(() =>
        {
            DevMoment m = DevMomentNow(w);
            m.Primary.Add(e.Quantity);
            m.Order.Add("product");
        });
        EventHandler<ItemPreservedEventArgs> kept = (_, e) => Safe(() =>
        {
            if (e.Item == null) return;
            DevMoment m = DevMomentNow(w);
            m.Preserved.Add((e.Item, e.Quantity));
            m.Order.Add("kept");
        });
        EventHandler<FiremakingEventObservedEventArgs> observed = (_, e) => Safe(() =>
        {
            DevMoment m = DevMomentNow(w);
            m.Events.Add((e.Event, e.RewardQuantity));
            m.Order.Add("event");
        });
        EventHandler<ItemAddedEventArgs> added = (_, e) => Safe(() =>
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
        EventHandler<BonfireTimeAddedEventArgs> fireTime = (_, e) => Safe(() =>
        {
            DevMoment m = DevMomentNow(w);
            m.FireTime.Add(e.TimeAdded);
            m.Order.Add("fire time");
        });
        EventHandler<BonfireLogsAddedEventArgs> logsTime = (_, e) => Safe(() =>
        {
            DevMoment m = DevMomentNow(w);
            m.LogsTime.Add(e.TimeAdded);
            m.Order.Add("logs on the bonfire");
        });
        EventHandler<ValueChangedEventArgs<double>> xp = (_, e) => Safe(() =>
        {
            DevMoment m = DevMomentNow(w);
            m.Xp += e.NewValue - e.OldValue;
            m.XpChanges++;
            m.Order.Add("xp");
        });
        action.RecipeCreated += burnt;
        action.PrimaryProductCreated += primary;
        action.ItemPreserved += kept;
        action.EventObserved += observed;
        action.ItemAdded += added;
        bonfire.FireTimeAdded += fireTime;
        bonfire.LogsAdded += logsTime;
        experience.XpChanged += xp;
        w.Off.Add(() =>
        {
            action.RecipeCreated -= burnt;
            action.PrimaryProductCreated -= primary;
            action.ItemPreserved -= kept;
            action.EventObserved -= observed;
            action.ItemAdded -= added;
            bonfire.FireTimeAdded -= fireTime;
            bonfire.LogsAdded -= logsTime;
            experience.XpChanged -= xp;
        });
        if (w.Bank is { } bank)
        {
            bank.Inventory.ItemQuantityChanged += banked;
            w.Off.Add(() => bank.Inventory.ItemQuantityChanged -= banked);
        }
    }

    // The moment of this frame: a new one when the frame has changed, after the one before is logged.
    DevMoment DevMomentNow(DevBurnWatch w)
    {
        long frame = (long)Engine.GetProcessFrames();
        if (w.Open is { } open && open.Frame == frame) return open;
        DevFlushMoment(w);
        return w.Open = new DevMoment { Frame = frame, At = Time.GetTicksMsec() };
    }

    void DevFlushMoment(DevBurnWatch w)
    {
        if (w.Open is not { } m) return;
        w.Open = null;
        bool kept = m.Burns.Count > 0;
        // Read after the frame of the moment, when all it brought is in the bank (DevWatchTick, or the next frame's first
        // event).
        DevBankRead(w, kept ? m : null);
        w.Fires += m.Burns.Count;
        foreach (var (e, reward) in m.Events) w.EventsSeen.Add((m.At, e, reward, w.Fires));
        m.BonfireLeft = w.Bonfire.DurationTimer.TimeLeft;
        m.BonfireMost = w.Bonfire.DurationTimer.MaxTime;
        m.BonfireLit = w.Bonfire.IsActive;
        if (!kept)
        {
            w.XpOutside += m.Xp;
            w.FireTimeOutside += m.FireTime.Sum();
            w.LogsTimeOutside += m.LogsTime.Sum();
            if (m.Events.Count == 0 && m.LogsTime.Count == 0) return;
        }
        else
        {
            if (w.Moments.Count < 20000) w.Moments.Add(m);
            w.LastBurn = m.At;
        }
        string burns = string.Join("; ", m.Burns.Select(b => $"{b.Log?.Recipe.LocalId}: the game's interval {R(b.Interval)}{(b.Success ? "" : ", NOT A SUCCESS")}"));
        DevLog($"{(kept ? "burn" : "no fire")} | {(burns.Length > 0 ? burns : "no fire")} | events [{string.Join(" ", m.Order)}] | XP +{R(m.Xp)} in {m.XpChanges} changes | " +
            $"by the bank's counts [{string.Join(", ", m.Items.Select(i => $"{i.Item.Id} {i.Quantity:+0;-0}, bank now {w.Counts.GetValueOrDefault(i.Item)}"))}" +
            $"{(m.Coins.Count > 0 ? "; " : "")}{string.Join(", ", m.Coins.Select(c => $"{c.Currency.Id} {c.Quantity:+0;-0}"))}]" +
            (m.Preserved.Count > 0 ? $" | kept [{string.Join(", ", m.Preserved.Select(p => $"{p.Item.Id} x{p.Quantity}"))}]" : "") +
            (m.Primary.Count > 0 ? $" | the primary product x{string.Join(", x", m.Primary)}" : "") +
            $" | the bonfire {(m.BonfireLit ? "lit" : "out")}, time added [{string.Join(", ", m.FireTime.Select(R))}], time left {R(m.BonfireLeft)} of {R(m.BonfireMost)}" +
            (m.LogsTime.Count > 0 ? $" | logs put on the bonfire [{string.Join(", ", m.LogsTime.Select(R))}]" : "") +
            (m.Events.Count > 0 ? $" | EVENT [{string.Join(", ", m.Events.Select(e => $"{e.Event?.Id} '{e.Event?.DisplayName.Text}' reward x{e.Reward}"))}]" : "") +
            (m.BankEvents.Count > 0 ? $" | the bank's event [{string.Join(", ", m.BankEvents.Select(i => $"{i.Item.Id} {i.Change:+0;-0}"))}]" : "") +
            (m.Added.Count > 0 ? $" | the action's ItemAdded [{string.Join(", ", m.Added.Select(i => $"{i.Item.Id} x{i.Quantity}"))}]" : ""));
    }

    // Every tick while the watch runs: the last frame is logged, and the watch ends when its minutes are up, after a
    // minute without a fire, or when a listener failed.
    void DevWatchTick()
    {
        if (_devBurns is not { } w) return;
        if (w.Open != null && w.Open.Frame != (long)Engine.GetProcessFrames()) DevFlushMoment(w);
        ulong now = Time.GetTicksMsec();
        // Between fires, what moves in the bank came with no fire.
        if (w.Open == null && now - w.LastRead >= BankReadEvery) DevBankRead(w, null);
        if (w.Failed) DevWatchEnd("a listener failed");
        else if (now - w.Since >= w.Length) DevWatchEnd($"the {w.Length / 60000} minutes are up");
        else if (now - w.LastBurn >= WatchIdle) DevWatchEnd($"a minute without a fire (firemaking {(w.Action.IsStarted ? "started" : "stopped")})");
    }

    void DevUnwatchBurns()
    {
        if (_devBurns is not { } w) return;
        foreach (Action off in w.Off)
        {
            try { off(); }
            catch (Exception) { }
        }
        w.Off.Clear();
    }

    // The summary: for every log burnt, one line per question with its verdict; then the tracker, then the last line.
    // Without a summary when another character was opened or the mod stopped: the logs of the one watched may be gone.
    void DevWatchEnd(string why, bool summarise = true)
    {
        if (_devBurns is not { } w) return;
        DevFlushMoment(w);
        DevUnwatchBurns();
        _devBurns = null;
        double seconds = (Time.GetTicksMsec() - w.Since) / 1000.0;
        int burns = w.Moments.Sum(m => m.Burns.Count);
        var verdicts = new List<string>();
        try
        {
            DevLog($"watch | ended after {seconds:0} s: {why}; {burns} fires seen at " +
                $"[{string.Join(", ", w.Moments.SelectMany(m => m.Burns).GroupBy(b => b.Log?.Recipe.LocalId ?? "?").Select(g => $"{g.Key} {g.Count()}"))}]; " +
                $"two fires in one frame {w.TwoBurnsInAFrame} times; XP outside the fires {R(w.XpOutside)}, bonfire time added outside them {R(w.FireTimeOutside)}, " +
                $"by logs put on the bonfire {R(w.LogsTimeOutside)}");
            DevBankSummary(w);
            if (!summarise || _firemaking == null) verdicts.Add("NO SUMMARY");
            else
            {
                foreach (var group in w.Moments.SelectMany(m => m.Burns.Select(b => b.Log)).Where(l => l != null).GroupBy(l => l, ReferenceEqualityComparer.Instance)
                    .OrderByDescending(g => g.Count()))
                {
                    var log = (ActiveFiremakingRecipe)group.Key!;
                    var mine = w.Moments.Where(m => m.Burns.Any(b => ReferenceEquals(b.Log, log))).ToList();
                    var said = DevWatchSummary(mine, log, LogRates.Read(log, _firemaking, _bank, Shown, false), w, w.Start.GetValueOrDefault(log, "not read"), seconds);
                    verdicts.Add($"{log.Recipe.LocalId}: {string.Join(", ", said)}");
                }
                if (verdicts.Count == 0) verdicts.Add("NO FIRE SEEN");
                DevEventsSummary(w, seconds);
                DevTracker($"at the end, after {seconds:0} s", w.Moments);
            }
        }
        catch (Exception e) { DevLog("watch | the summary failed: " + e.Message); verdicts.Add("THE SUMMARY FAILED"); }
        const string last = "stopped listening to the game's events; nothing was changed: no click, no page, no setting";
        DevLog($"watch | {last}; {(_firemaking != null ? DevBurning() : "no character")}");
        File.WriteAllText(Path.Combine(_ctx.Directory, WatchDoneFile), $"{why}; {burns} fires in {seconds:0} s; {string.Join("; ", verdicts)}; {last}");
    }

    // The items and coins over the whole watch: what the bank's counts moved by in frames with a fire and outside them,
    // each count from the start to the end, and the bank's own event beside the counts.
    void DevBankSummary(DevBurnWatch w)
    {
        if (w.Bank == null)
        {
            DevLog("watch | the items | NOT READ: the bank (MelvorBase:Bank) was not found");
            return;
        }
        DevBankRead(w, null);
        var counted = DevSum(w.Moments.SelectMany(m => m.Items));
        var heard = DevSum(w.Moments.SelectMany(m => m.BankEvents));
        static string Of(Dictionary<Item, int> sum) => "[" + string.Join(", ", sum.Select(p => $"{p.Key.Id} {p.Value:+0;-0}")) + "]";
        bool same = counted.All(p => heard.GetValueOrDefault(p.Key) == p.Value) && heard.Where(p => w.Counts.ContainsKey(p.Key)).All(p => counted.GetValueOrDefault(p.Key) == p.Value);
        DevLog($"watch | the items | read from the bank's counts of {w.Watched.Count} items: in frames with a fire {Of(counted)}; outside them {Of(w.Outside)}; from the start " +
            $"to the end [{string.Join(", ", w.Watched.Where(i => w.Counts[i] != w.StartCounts[i]).Select(i => $"{i.Id} {w.StartCounts[i]} -> {w.Counts[i]}"))}] | " +
            $"the bank's own event in those frames {Of(heard)}: {(heard.Count == 0 && counted.Count == 0 ? "nothing either way" : same ? "THE SAME AS THE COUNTS" : "NOT THE SAME AS THE COUNTS")} | " +
            $"the coins: in frames with a fire [{string.Join(", ", w.Wallet.Select(c => $"{c.Id} {w.Moments.Sum(m => m.CoinsOf(c)):+0;-0}"))}], outside them " +
            $"[{string.Join(", ", w.WalletOutside.Select(p => $"{p.Key.Id} {p.Value:+0;-0}"))}] | the action's ItemAdded {w.Moments.Sum(m => m.Added.Count)} times");
    }

    static Dictionary<Item, int> DevSum(IEnumerable<(Item Item, int Quantity)> items)
    {
        var sum = new Dictionary<Item, int>(ReferenceEqualityComparer.Instance);
        foreach (var (item, quantity) in items) sum[item] = sum.GetValueOrDefault(item) + quantity;
        return sum;
    }

    List<string> DevWatchSummary(List<DevMoment> moments, ActiveFiremakingRecipe log, LogRates ours, DevBurnWatch w, string start, double seconds)
    {
        var verdicts = new List<string>();
        string id = log.Recipe.Id;
        void Say(string question, string verdict, string words)
        {
            verdicts.Add(verdict);
            DevLog($"watch | {id} | {question} | {verdict}: {words}");
        }
        int n = moments.Count;
        bool enough = n >= MostBurns;
        string few = enough ? "" : $" (fewer than {MostBurns} fires decide nothing)";
        static bool Near(double seen, double expect) => Math.Abs(seen - expect) <= 3 * Math.Sqrt(Math.Max(0, expect)) + 2;

        // The log's numbers at the start and at the end: a level up while it ran changes them.
        string end = DevLogStats(log);
        DevLog($"watch | {id} | the log's numbers | at the start {start}; at the end {end}" + (start == end ? "" : "  CHANGED WHILE IT RAN"));

        // The time of a fire: from one fire to the next, and the game's own interval with each.
        var times = moments.Select(m => m.At).ToList();
        var intervals = moments.SelectMany(m => m.Burns.Where(b => ReferenceEquals(b.Log, log)).Select(b => b.Interval)).Distinct().Order().ToList();
        DevLog($"watch | {id} | the time of a fire | " + (times.Count < 2 ? "too few fires to time" : $"{R((times[^1] - times[0]) / 1000.0 / (times.Count - 1))} s measured over {times.Count} fires") +
            $"; the game's interval with each fire [{string.Join(", ", intervals.Select(R))}]; ours {R(ours.Seconds)} s");

        // XP: on every fire, as Recipe Rates counts it (LogRates.XpRule).
        int xpOn = moments.Count(m => m.Xp > 1e-9);
        double xpEach = n > 0 ? moments.Average(m => m.Xp) : double.NaN;
        bool xpSame = Math.Abs(xpEach - ours.XpEach) <= Math.Max(0.01, 0.01 * ours.XpEach);
        Say("XP", !enough ? "NOT DECIDED" : xpOn >= 0.9 * n && xpSame ? "XP ON EVERY FIRE, AS OURS (THE WINDOW'S RULE)"
                : xpOn >= 0.9 * n ? "XP ON EVERY FIRE, ANOTHER AMOUNT (NOT THE WINDOW'S RULE)" : "XP NOT ON EVERY FIRE (NOT THE WINDOW'S RULE)",
            $"XP in {xpOn} of {n} frames with a fire, {R(xpEach)} a fire on average, ours {R(ours.XpEach)}; XP outside the fires {R(w.XpOutside)}; measured " +
            $"{R(moments.Sum(m => m.Xp) / Math.Max(1, seconds) * 3600)} an hour, ours {R(ours.XpPerHour)}{few}");

        // The log: used, or kept at the preservation chance (LogRates.LogsRule).
        if (ours.Log is { } cost)
        {
            Item item = cost.Item;
            int used = moments.Count(m => m.ItemsOf(item) == -cost.Quantity), keptByCount = moments.Count(m => m.ItemsOf(item) == 0),
                other = n - used - keptByCount, keptByEvent = moments.Count(m => m.Preserved.Any(p => ReferenceEquals(p.Item, item)));
            double expectKept = n * ours.PreservationChance;
            bool fits = Math.Abs(keptByCount - expectKept) <= 3 * Math.Sqrt(Math.Max(0, n * ours.PreservationChance * (1 - ours.PreservationChance))) + 1;
            Say("the logs", !enough ? "NOT DECIDED" : other > 0 ? "A FIRE TAKES ANOTHER NUMBER OF LOGS (NOT THE WINDOW'S RULE)"
                    : fits ? "A FIRE USES ITS LOG, KEPT AT THE PRESERVATION CHANCE (THE WINDOW'S RULE)" : "LOGS KEPT FAR FROM OURS (NOT THE WINDOW'S RULE)",
                $"{item.Id}: {used} fires took {cost.Quantity}, {keptByCount} took none, {other} another number " +
                $"[{string.Join(" ", moments.GroupBy(m => m.ItemsOf(item)).OrderBy(g => g.Key).Select(g => $"{g.Key:+0;-0} x{g.Count()}"))}]; the game's ItemPreserved on {keptByEvent}; " +
                $"ours kept {R(ours.PreservationChance)} of fires = {R(expectKept)}; measured {R(-moments.Sum(m => m.ItemsOf(item)) / Math.Max(1, seconds) * 3600)} burnt an hour, " +
                $"ours {R(ours.LogsPerHour)}{few}");
        }

        // The coins (LogRates.CoinsRule): CurrencyReward on every fire, not doubled.
        if (ours.Coins is { } coins)
        {
            var got = moments.Select(m => m.CoinsOf(coins)).ToList();
            int right = got.Count(c => c == ours.CoinsEach);
            Say("the coins", !enough ? "NOT DECIDED" : right == n ? "COINS ON EVERY FIRE, AS OURS (THE WINDOW'S RULE)"
                    : got.All(c => c == ours.CoinsEach || c == 2 * ours.CoinsEach) ? "COINS ON EVERY FIRE, SOMETIMES DOUBLED (NOT THE WINDOW'S RULE)" : "COINS DIFFER (NOT THE WINDOW'S RULE)",
                $"{coins.Id} [{string.Join(" ", got.GroupBy(c => c).OrderBy(g => g.Key).Select(g => $"{g.Key:+0;-0} x{g.Count()}"))}] in {n} fires, ours {ours.CoinsEach} a fire{few}");
        }
        else DevLog($"watch | {id} | the coins | the log gives none by ours; in frames with a fire [{string.Join(", ", w.Wallet.Select(c => $"{c.Id} {moments.Sum(m => m.CoinsOf(c)):+0;-0}"))}]");

        // The items burning makes (LogRates.ItemsRule): every product against our chances, doubling counted.
        foreach (LogRates.Product p in ours.Products)
        {
            int seen = moments.Sum(m => m.ItemsOf(p.Item));
            double expect = n * p.Chance * p.Each;
            DevLog($"watch | {id} | item {p.Item.Id} | {seen} in {n} fires, ours {R(expect)} ({R(p.Chance)} of fires, {R(p.Each)} each with the doubling chance)" +
                (enough && !Near(seen, expect) ? "  FAR FROM OURS" : ""));
        }
        if (ours.Products.Count > 0)
            Say("the items", !enough ? "NOT DECIDED" : ours.Products.All(p => Near(moments.Sum(m => m.ItemsOf(p.Item)), n * p.Chance * p.Each)) ? "THE ITEMS AS OURS (THE WINDOW'S RULE)" : "ITEMS FAR FROM OURS",
                $"{ours.Products.Count} items on our list; the primary product's event on {moments.Count(m => m.Primary.Count > 0)} fires " +
                $"[{string.Join(" ", moments.SelectMany(m => m.Primary).GroupBy(q => q).Select(g => $"x{g.Key} {g.Count()} times"))}]{few}");

        // The bonfire (LogRates.BonfireRule): does every fire add the log's bonfire time, and does the bonfire's most cap it?
        double ourTime = FiremakingBonfireComponent.GetBonfireTimeForRecipe(log);
        var lit = moments.Where(m => m.BonfireLit).ToList();
        int added = lit.Count(m => m.FireTime.Count > 0), sameTime = lit.Count(m => m.FireTime.Any(t => Math.Abs(t - ourTime) <= Math.Max(1e-6, 1e-4 * Math.Abs(ourTime))));
        bool capped = lit.Any(m => m.BonfireMost > 0 && m.BonfireLeft >= m.BonfireMost - Math.Max(1, ourTime));
        Say("the bonfire", lit.Count < MostBurns ? (lit.Count == 0 ? "NOT DECIDED (THE BONFIRE WAS NOT LIT)" : "NOT DECIDED")
                : added == 0 ? "BURNING ADDS NO BONFIRE TIME" : sameTime >= 0.9 * lit.Count ? "BURNING ADDS BONFIRE TIME, THE LOG'S AS THE PAGE SHOWS IT"
                : added >= 0.9 * lit.Count ? "BURNING ADDS ANOTHER BONFIRE TIME" : "NOT DECIDED",
            $"{lit.Count} of {n} fires with the bonfire lit; time added on {added} of them [{string.Join(" ", lit.SelectMany(m => m.FireTime).GroupBy(t => t).Select(g => $"{R(g.Key)} x{g.Count()}"))}], " +
            $"the log's bonfire time {R(ourTime)} (GetBonfireTimeForRecipe); time added on fires with the bonfire out [{string.Join(" ", moments.Where(m => !m.BonfireLit).SelectMany(m => m.FireTime).Select(R))}]; " +
            $"the bonfire's time left from {R(moments.FirstOrDefault()?.BonfireLeft ?? double.NaN)} to {R(moments.LastOrDefault()?.BonfireLeft ?? double.NaN)}, its most " +
            $"{R(moments.LastOrDefault()?.BonfireMost ?? double.NaN)} (the bonfire's MaxDuration {w.Bonfire.CurrentBonfire?.MaxDuration})" +
            (capped ? "; THE MOST WAS REACHED: the time left stood at its most" : "; the most was not reached"));
        return verdicts;
    }

    // Every Firemaking event seen: its name, its reward, and the seconds and fires since the one before (or the start).
    void DevEventsSummary(DevBurnWatch w, double seconds)
    {
        var seen = new List<string>();
        ulong last = w.Since;
        int before = 0;
        foreach (var (at, e, reward, fires) in w.EventsSeen)
        {
            seen.Add($"{e?.Id} '{e?.DisplayName.Text}' x{reward} after {(at - last) / 1000.0:0.#} s and {fires - before} fires (its chance {R(e?.Chance ?? double.NaN)})");
            last = at;
            before = fires;
        }
        int total = w.Fires, count = w.EventsSeen.Count;
        DevLog($"watch | the events | {(count == 0 ? "NO EVENT SEEN" : $"{count} EVENTS SEEN")} in {seconds:0} s and {total} fires" +
            (count > 0 ? $" ({R(count / (double)Math.Max(1, total))} a fire, {R(count / Math.Max(1, seconds))} a second): {string.Join("; ", seen)}" : "") +
            "; how often the game rolls them is not counted by the window");
    }

    // The game's own tracker of Firemaking (FiremakingEntity.Tracker): over each of its periods the Firemaking XP, the
    // items and the currencies, and an hour by it beside ours, if its times are in seconds (the log says which numbers it
    // read).
    void DevTracker(string when, List<DevMoment>? moments)
    {
        try
        {
            var tracker = _firemaking!.Tracker;
            ExperienceComponent experience = _firemaking.Experience;
            var items = moments?.SelectMany(m => m.Items.Select(i => i.Item)).Distinct().ToList() ?? new List<Item>();
            var currencies = moments?.SelectMany(m => m.Coins.Select(c => c.Currency)).Distinct().ToList() ?? new List<Currency>();
            LogRates? ours = _firemaking.Action.CreatingRecipe is { } log ? LogRates.Read(log, _firemaking, _bank, Shown, false) : null;
            int i = 0;
            foreach (var period in tracker.Periods)
            {
                double xp = period.Experience.TryGetValue(experience, out var list) ? list.Total : 0;
                double covered = Math.Min(period.CycleDuration, period.Experience.RecordedCount * period.SamplePeriod);
                string perHour = covered > 0 ? R(xp / covered * 3600) : "-";
                DevLog($"tracker | {when} | period {i++}: cycle {R(period.CycleDuration)}, {period.SampleCount} samples of {R(period.SamplePeriod)}, " +
                    $"{period.Experience.RecordedCount} recorded | Firemaking XP {R(xp)}, an hour by it (if in seconds) {perHour}, ours {(ours == null ? "-" : R(ours.XpPerHour))} | " +
                    $"items [{string.Join(", ", items.Select(item => $"{item.Id} {(period.Items.TryGetValue(item, out var l) ? l.Total : 0)}"))}] | " +
                    $"currencies [{string.Join(", ", currencies.Select(c => $"{c.Id} {(period.Currencies.TryGetValue(c, out var l) ? l.Total : 0)}"))}]");
            }
            if (i == 0) DevLog($"tracker | {when} | the tracker has no periods");
        }
        catch (Exception e) { DevLog($"tracker | {when} | could not be read: {e.Message}"); }
    }
}
#endif
