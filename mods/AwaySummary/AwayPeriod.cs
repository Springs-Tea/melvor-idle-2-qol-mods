using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.Toolbelt;
using GamesByMalcs.Melvor2.Components.Trackers;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Entities.OfflineProgress;
using GamesByMalcs.Melvor2.EventLog;
using GamesByMalcs.Melvor2.Localization;
using Godot;
using MelvorMods.Shared;

namespace AwaySummary;

sealed record Change(OfflineSnapshotCategory Category, string Name, Texture2D? Icon, double Amount,
    OfflineSnapshotMessage? Message);

// A stop or a defeat: the game's title, the game's description under it when there is one, and when it happened.
sealed record AwayEvent(TimeSpan At, string Title, string? Detail, Texture2D? Icon);

sealed record Summary(TimeSpan Duration, List<Change> Changes, List<AwayEvent> Events);

sealed class AwayPeriod(Game game, BankEntity bank, CombatEntity combat, Currency gold)
{
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly DateTime _start = DateTime.UtcNow;
    readonly Dictionary<string, (double Xp, int Level)> _skills = game.LeveledEntities.ToDictionary(
        s => s.Id, s => (s.Experience.Xp, s.Experience.Level));
    readonly long _gold = bank.Wallet.GetQuantity(gold);
    readonly Dictionary<Item, int> _bank = BankCounts(bank);
    readonly Dictionary<Item, (long Gained, long Used)> _changes = new();
    // Used up from the worn equipment and from the tool belts. The game's own summary lists them under the
    // equipment's and the tool belt's ItemUsedCategory, not under the bank's.
    readonly Dictionary<Item, long> _wornUsed = new();
    readonly Dictionary<Item, (long Count, ToolbeltComponent Belt)> _beltUsed = new();
    readonly List<AwayEvent> _events = new();
    readonly List<Stop> _stops = new();
    ulong _eventFrame;

    // A reason and its stop arrive in the same game update, in either order. PlayerDied is raised just
    // before combat stops. Match each signal once, even when several actions end in the same update.
    sealed record Stop(string Title, int Index, bool HasStop, bool HasReason, bool Death = false);

    public TimeSpan Elapsed => _clock.Elapsed;

    static Dictionary<Item, int> BankCounts(BankEntity bank) => bank.Inventory.Stacks
        .Select(stack => stack.Item).Distinct().ToDictionary(item => item, bank.Inventory.GetQuantity);

    public void BankChanged(Item item, int change)
    {
        var (gained, used) = _changes.GetValueOrDefault(item);
        if (change > 0) gained += change;
        else used -= (long)change;
        _changes[item] = (gained, used);
#if DEVTOOLS
        _bankEvents++;
#endif
    }

    // As in TrackerComponent's three equipable handlers, each consumption event means one item.
    public void WornUsed(Item item) => _wornUsed[item] = _wornUsed.GetValueOrDefault(item) + 1;

    // One line per item: an item used from two belts is one line, under the first belt's heading.
    public void BeltUsed(ToolbeltComponent belt, Item item) =>
        _beltUsed[item] = _beltUsed.TryGetValue(item, out var used) ? (used.Count + 1, used.Belt) : (1, belt);

    void BeginEvent()
    {
        ulong frame = Engine.GetProcessFrames();
        if (frame == _eventFrame) return;
        _eventFrame = frame;
        _stops.Clear();
    }

    public void Stopped(ActionComponent action)
    {
        BeginEvent();
        var entry = new ActionStoppedWithReasonEventLogEntry(action, string.Empty);
        string title = entry.EventTitle;
        int i = _stops.FindIndex(s => s.Title == title && !s.HasStop);
        if (i >= 0) _stops[i] = _stops[i] with { HasStop = true };
        else
        {
            _stops.Add(new Stop(title, _events.Count, true, false));
            _events.Add(new AwayEvent(Elapsed, title, null, entry.EventIcon));
        }
    }

    // The game's event log shows the title and the description apart; so does the summary.
    public void Reason(ActionStoppedWithReasonEventLogEntry entry)
    {
        BeginEvent();
        string title = entry.EventTitle;
        int i = _stops.FindLastIndex(s => s.Title == title && !s.HasReason);
        string reason = entry.GetPlainDescription();
        var note = new AwayEvent(entry.EventTime.ToUniversalTime() - _start, title,
            string.IsNullOrWhiteSpace(reason) ? null : reason, entry.EventIcon);
        if (i >= 0)
        {
            Stop stop = _stops[i];
            if (!stop.Death) _events[stop.Index] = note;
            _stops[i] = stop with { HasReason = true };
        }
        else
        {
            _stops.Add(new Stop(title, _events.Count, false, true));
            _events.Add(note);
        }
    }

    public void Died(ItemQuantity? lost)
    {
        BeginEvent();
        string title = new ActionStoppedWithReasonEventLogEntry(combat.Action, string.Empty).EventTitle;
        _stops.Add(new Stop(title, _events.Count, false, false, Death: true));
        string description = lost == null ? new LocalizedString("Despite your defeat, all your items remain safe.").Text
            : $"{new LocalizedString("Your defeat came at a cost:").Text} {lost.Quantity:N0} {lost.Item.DisplayName.GetPluralText(lost.Quantity)}";
        _events.Add(new AwayEvent(Elapsed, title, description, lost?.Item.Icon ?? combat.Icon));
    }

    static string ByName(Item item) => item.DisplayName.GetPluralText(1);

    public Summary Finish()
    {
        TimeSpan elapsed = Elapsed;
        // Gathered kind by kind, then put under the game's own headings in the game's order of them
        // (OfflineSnapshotCategory.Priority). The sort is stable: headings of equal priority keep the order
        // below, skills keep the game's order and items their order by name.
        var xp = new List<Change>();
        var levels = new List<Change>();
        foreach (var skill in game.LeveledEntities)
        {
            var experience = skill.Experience;
            var (before, level) = _skills[skill.Id];
            double gain = experience.Xp - before;
            int up = experience.Level - level;
            if (gain == 0 && up == 0) continue;
            var icon = skill.Icon ?? GameStyle.Icon(skill.Id[(skill.Id.IndexOf(':') + 1)..]);
            if (gain != 0)
            {
                var category = TrackerComponent.ExperienceGainCategory;
                xp.Add(new Change(category, skill.DisplayName.Text, icon, gain, gain < 0 ? null :
                    new ExperienceGainMessage(experience, gain, gain / elapsed.TotalHours, category, experience.Entity)));
            }
            if (up > 0)
            {
                var category = TrackerComponent.LevelGainCategory;
                levels.Add(new Change(category, skill.DisplayName.Text, icon, up,
                    new LevelGainMessage(experience, up, up / elapsed.TotalHours, category, experience.Entity)));
            }
        }

        var gained = new List<Change>();
        var used = new List<Change>();
        var after = BankCounts(bank);
        foreach (Item item in _bank.Keys.Union(after.Keys).Union(_changes.Keys).OrderBy(ByName))
        {
            long net = (long)after.GetValueOrDefault(item) - _bank.GetValueOrDefault(item);
            var (gain, use) = _changes.GetValueOrDefault(item);
            if (gain - use != net) (gain, use) = (Math.Max(net, 0), Math.Max(-net, 0));
            if (gain > 0) gained.Add(ItemChange(item, gain, elapsed, combat.Tracker.ItemGainCategory, bank));
            if (use > 0) used.Add(ItemChange(item, -use, elapsed, combat.Tracker.ItemUsedCategory, bank));
        }
        var worn = _wornUsed.OrderBy(pair => ByName(pair.Key)).Select(pair => ItemChange(pair.Key, -pair.Value,
            elapsed, combat.Equipment.ItemUsedCategory, combat.Equipment.Entity));
        var belts = _beltUsed.OrderBy(pair => ByName(pair.Key)).Select(pair => ItemChange(pair.Key, -pair.Value.Count,
            elapsed, pair.Value.Belt.ItemUsedCategory, pair.Value.Belt.Entity));

        var money = new List<Change>();
        long balance = bank.Wallet.GetQuantity(gold);
        long delta = balance - _gold;
        if (delta != 0)
        {
            var category = delta > 0 ? TrackerComponent.CurrencyGainCategory : TrackerComponent.CurrencyUseCategory;
            money.Add(new Change(category, gold.DisplayName.Text, gold.Icon, delta, delta > 0
                ? new CurrencyGainMessage(delta, delta / elapsed.TotalHours, gold, category, bank)
                : new CurrencyLossMessage(delta, delta / elapsed.TotalHours, gold, category, bank)));
        }
        var changes = xp.Concat(levels).Concat(gained).Concat(used).Concat(worn).Concat(belts).Concat(money)
            .OrderBy(change => change.Category.Priority).ToList();
#if DEVTOOLS
        if (Readings != null)
        {
            var skill = game.LeveledEntities.OrderByDescending(s => s.Experience.Xp != _skills[s.Id].Xp).FirstOrDefault();
            if (skill != null) Readings($"XP {skill.Id}: {_skills[skill.Id].Xp:R} -> {skill.Experience.Xp:R}");
            foreach (var item in _changes.Keys.Union(_bank.Keys).Union(after.Keys).Take(3))
                Readings($"bank {item.Id}: {_bank.GetValueOrDefault(item)} -> {after.GetValueOrDefault(item)}, heard {_changes.GetValueOrDefault(item)} (gained, used)");
            Readings($"gold: {_gold} -> {balance}; bank change events: {_bankEvents}; " +
                $"used from the equipment: {_wornUsed.Values.Sum()}, from tool belts: {_beltUsed.Values.Sum(v => v.Count)}");
        }
#endif
        return new Summary(elapsed, changes, _events.OrderBy(e => e.At).ToList());
    }

    static Change ItemChange(Item item, long delta, TimeSpan elapsed, OfflineSnapshotCategory category, GameEntity entity)
    {
        // The game takes a signed Int32. Preserve a very long absence's total in the plain row.
        OfflineSnapshotMessage? message = delta > int.MaxValue || delta < -int.MaxValue ? null : delta > 0
            ? new ItemGainMessage((int)delta, delta / elapsed.TotalHours, item, category, entity)
            : new ItemLossMessage((int)delta, delta / elapsed.TotalHours, item, category, entity);
        return new Change(category, item.DisplayName.GetPluralText((int)Math.Min(Math.Abs(delta), int.MaxValue)),
            item.Icon, delta, message);
    }

#if DEVTOOLS
    public Action<string>? Readings { get; init; }
    long _bankEvents;

    // A long list for the layout pictures: real items and icons, the game's rows, but made-up quantities. Each
    // of the game's item headings gets some, so the pictures show them all.
    public Summary LayoutSample(TimeSpan elapsed, int count)
    {
        var items = game.Data.GetDataRegistryOutput<Item>().GetAllObjects().Take(count).ToList();
        var belt = game.Entities.GetAllObjects().OfType<GamesByMalcs.Melvor2.Components.ToolbeltEquipping.IToolbeltEntity>()
            .Select(e => e.Toolbelt).FirstOrDefault();
        var rows = new List<Change>();
        for (int i = 0; i < items.Count; i++)
            rows.Add((i % 4) switch
            {
                0 => ItemChange(items[i], i + 1, elapsed, combat.Tracker.ItemGainCategory, bank),
                1 => ItemChange(items[i], -(i + 1), elapsed, combat.Tracker.ItemUsedCategory, bank),
                2 => ItemChange(items[i], -(i + 1), elapsed, combat.Equipment.ItemUsedCategory, combat.Equipment.Entity),
                _ when belt != null => ItemChange(items[i], -(i + 1), elapsed, belt.ItemUsedCategory, belt.Entity),
                _ => ItemChange(items[i], i + 1, elapsed, combat.Tracker.ItemGainCategory, bank),
            });
        return new Summary(elapsed, rows.OrderBy(change => change.Category.Priority).ToList(), []);
    }
#endif
}
