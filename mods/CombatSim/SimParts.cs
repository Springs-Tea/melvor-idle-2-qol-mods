using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Entities.Combat;

namespace CombatSim;

/// <summary>
/// Cuts a simulated fight into parts of even game time and notes what each part did (issue #5): kills, deaths,
/// food, damage, healing, each skill's XP, coins, loot, supplies, and every item that dropped or was used. The
/// ranges of the rates come from how much the parts differ (<see cref="SimRanges"/>); when the fight is over, only
/// the halves of those ranges are kept with its result (<see cref="SimSpread"/>), not the parts. It looks at the
/// fight only when SimEngine already does, every CheckEvery ticks, and reads the totals only at the end of a part:
/// a scan costs no more than before.
/// <para>
/// Simulating longer (<see cref="SimOptions.Sure"/>): after the asked time the fight goes on, a part at a time,
/// until the range of every rate the list shows is within the size the player chose, or until
/// <see cref="SimOptions.MostHours"/>. A range narrows with the square root of the time (four times the time, half
/// the range), so it can be told whether a rate could still get there by the longest time. One that could not is
/// waited for until it is within the next size the box offers that it could reach; one that could not reach even
/// the largest (a few deaths, gold that hangs on a rare drop) holds nothing back, at any size. So a stricter size
/// waits for everything a looser one waits for, and never ends a fight sooner: the same fight, from the same dice,
/// is the same up to the moment the looser size lets it end. A rate of 0 has no size in percent and holds nothing
/// back either. Every time there are twice <see cref="Count"/> parts, each two become one, so a fight has between
/// 10 and 19 of them.
/// </para>
/// </summary>
sealed class SimParts
{
    /// <summary>The parts a fight of the asked length is cut into: ten, the usual least for batch means.</summary>
    public const int Count = 10;
    // Every item of the loot and of the supplies is counted too, for its own line in the details.
    const string ItemKey = "item:", UsedKey = "used:";

    readonly Game _sim;
    readonly CombatEntity _combat;
    readonly SimResult _result;
    readonly IReadOnlyDictionary<string, double> _xpBefore;
    readonly Dictionary<string, Amount> _loot, _used;
    readonly Func<double> _ammo;
    readonly long _asked;
    readonly double _sure, _mostHours;
    // The sizes a rate may be held to, smallest first: the chosen one and the larger ones the box offers.
    readonly double[] _sizes;
    readonly List<SimRate> _watch;
    readonly Action<string> _log;
    readonly Dictionary<string, List<double>> _of = new();
    Dictionary<string, double> _before = new();
    int _ticks, _count, _hits;
    long _last;
    (HashSet<string> Runes, HashSet<string> Prayer)? _kinds;
    bool _kindsRead;
    // At the last look at a longer fight: the widest watched range that held it back, the rates held to a larger size
    // than the chosen one, and those that could not reach even the largest.
    (SimRate? Rate, double Share, double Size) _widest;
    List<(SimRate Rate, double Size)> _looser = new();
    List<SimRate> _outOfReach = new();

    public SimParts(Game sim, CombatEntity combat, SimResult result, SimOptions options, long asked, int checkEvery,
        IReadOnlyDictionary<string, double> xpBefore, Dictionary<string, Amount> loot, Dictionary<string, Amount> used,
        Func<double> ammo, Action<string> log)
    {
        _sim = sim;
        _combat = combat;
        _result = result;
        _xpBefore = xpBefore;
        _loot = loot;
        _used = used;
        _ammo = ammo;
        _asked = asked;
        _sure = options.UnlimitedSupplies ? options.Sure : 0;
        _sizes = options.SureSizes.Where(size => size > _sure).Prepend(_sure).Order().ToArray();
        _mostHours = options.MostHours;
        _watch = options.Watch;
        _log = log;
        // A whole number of looks, so a part ends exactly where the fight is looked at.
        _ticks = (int)Math.Max(checkEvery, Math.Round(asked / (double)Count / checkEvery) * checkEvery);
        // The hits behind the damage taken, as SimEngine counts the damage: every loss of hitpoints.
        combat.Player.Health.CurrentChanged += (_, e) => { if (e.NewValue < e.OldValue) _hits++; };
    }

    /// <summary>
    /// Called at every look at the fight. Ends a part when one is due; true when the fight is simulated longer and
    /// every watched range is now within its size (see <see cref="Sure"/>), so the fight may end here.
    /// </summary>
    public bool Step(long ticks)
    {
        if (ticks - _last < _ticks) return false;
        Close(ticks, intoLast: false);
        return _sure > 0 && ticks >= _asked && Sure();
    }

    /// <summary>
    /// The fight is over at this tick: how sure its rates are, for its result. What is left after the last part
    /// becomes a part of its own, or joins the last one when it is shorter than half a part. Call after the result's
    /// hours, loot and supplies are set.
    /// </summary>
    public SimSpread? Finish(long ticks)
    {
        if (ticks > _last) Close(ticks, intoLast: _count > 0 && ticks - _last < _ticks / 2.0);
        if (_count == 0) return null;
        double[] durations = SimRanges.Durations(_count, _ticks, _result.Hours);
        double[]? kills = Get(SimRanges.Kills), runs = _result.Target.IsDungeon ? Get(SimRanges.Runs) : null;
        var spread = new SimSpread { Half = new(), Hits = _hits };
        foreach (string quantity in Quantities())
        {
            if (PartsOf(quantity, durations) is not { } y || y.All(v => v == 0)) continue;
            double?[] halves =
            {
                SimRanges.Shown(quantity, Per.Hour) ? SimRanges.Figures(SimRanges.HalfWidth(y, durations)) : null,
                SimRanges.Shown(quantity, Per.Kill) && kills != null ? SimRanges.Figures(SimRanges.HalfWidth(y, kills)) : null,
                SimRanges.Shown(quantity, Per.Run) && runs != null ? SimRanges.Figures(SimRanges.HalfWidth(y, runs)) : null,
            };
            int kept = halves.Length;
            while (kept > 0 && halves[kept - 1] == null) kept--;
            if (kept > 0) spread.Half[quantity] = halves[..kept];
            if (SimRanges.IsCount(quantity) && y.Any(v => v == 0)) (spread.Sparse ??= new()).Add(quantity);
        }
        spread.Items = _result.Loot.Select(a => ItemKey + a.Id).Concat(_result.Used.Select(a => UsedKey + a.Id))
            .Select(key => Get(key) is { } parts ? SimRanges.Figures(SimRanges.HalfWidth(parts, durations)) : null).ToList();
#if DEVTOOLS
        DevLog(ticks, spread, durations);
#endif
        return spread;
    }

    // The quantities counted, and those worked out from them; not the single items.
    IEnumerable<string> Quantities() =>
        _of.Keys.Where(k => !k.StartsWith(ItemKey, StringComparison.Ordinal) && !k.StartsWith(UsedKey, StringComparison.Ordinal))
            .Concat(new[] { SimRanges.Gold, SimRanges.NetGold, SimRanges.Xp, SimRanges.Seconds });

    double[]? Get(string key) => _of.TryGetValue(key, out List<double>? parts) && parts.Count == _count ? parts.ToArray() : null;

    // How much of a quantity each part had, also of those worked out from the counted ones (SimRanges.Total).
    double[]? PartsOf(string quantity, double[] durations)
    {
        double[] Or0(string key) => Get(key) ?? new double[_count];
        double[] Sum(IEnumerable<double[]> lists)
        {
            var sum = new double[_count];
            foreach (double[] list in lists)
                for (int i = 0; i < _count; i++) sum[i] += list[i];
            return sum;
        }
        return quantity switch
        {
            SimRanges.Seconds => durations.Select(d => d * 3600).ToArray(),
            SimRanges.Gold => Sum(new[] { Or0(SimRanges.Coins), Or0(SimRanges.Loot) }),
            SimRanges.NetGold => Sum(new[] { Or0(SimRanges.Coins), Or0(SimRanges.Loot), Or0(SimRanges.Cost).Select(c => -c).ToArray() }),
            SimRanges.Xp => Sum(_of.Keys.Where(SimRanges.IsXp).Select(Or0)),
            _ => Get(quantity),
        };
    }

    // The totals at the last part, also of those worked out from them, as SimRanges.Total gives them for a result.
    double TotalNow(string quantity) => quantity switch
    {
        SimRanges.Gold => _before.GetValueOrDefault(SimRanges.Coins) + _before.GetValueOrDefault(SimRanges.Loot),
        SimRanges.NetGold => _before.GetValueOrDefault(SimRanges.Coins) + _before.GetValueOrDefault(SimRanges.Loot) - _before.GetValueOrDefault(SimRanges.Cost),
        SimRanges.Xp => _before.Where(t => SimRanges.IsXp(t.Key)).Sum(t => t.Value),
        _ => _before.GetValueOrDefault(quantity),
    };

    void Close(long ticks, bool intoLast)
    {
        Dictionary<string, double> now = Totals();
        foreach (var (key, total) in now)
        {
            double part = total - _before.GetValueOrDefault(key);
            if (!_of.TryGetValue(key, out List<double>? parts)) _of[key] = parts = Enumerable.Repeat(0.0, _count).ToList();
            if (intoLast && parts.Count == _count) parts[^1] += part;
            else parts.Add(part);
        }
        if (!intoLast) _count++;
        // Every quantity has a number for every part (one that was not read this time had none).
        foreach (List<double> parts in _of.Values)
            while (parts.Count < _count) parts.Add(0);
        _before = now;
        _last = ticks;
        if (_count == 2 * Count) Merge();
    }

    // Two parts become one, so the parts of a long fight stay few and each is long enough to say something.
    void Merge()
    {
        foreach (List<double> parts in _of.Values)
        {
            var merged = new List<double>(Count);
            for (int i = 0; i + 1 < parts.Count; i += 2) merged.Add(parts[i] + parts[i + 1]);
            parts.Clear();
            parts.AddRange(merged);
        }
        _count = Count;
        _ticks *= 2;
    }

    // Go on until: is every watched range within its size? A rate's size is the chosen one, or the next larger one the
    // box offers when the longest time could not bring it within the chosen one; a rate that could not reach even the
    // largest has none. Every size of a looser choice is as large or larger, so whenever a stricter choice may end the
    // fight here, a looser one may too: a looser choice never runs longer.
    bool Sure()
    {
        double hours = _last / (3600.0 * SimEngine.TicksPerSecond);
        double[] durations = SimRanges.Durations(_count, _ticks, hours);
        double[]? kills = Get(SimRanges.Kills), runs = Get(SimRanges.Runs);
        double? Half(SimRate rate) => PartsOf(rate.Of, durations) is { } y
            && (rate.Per switch { Per.Kill => kills, Per.Run => runs, _ => durations }) is { } x ? SimRanges.HalfWidth(y, x) : null;
        bool Sparse(string quantity) => Get(quantity)?.Any(v => v == 0) ?? true;
        // The share a range would have at the longest time, for each share it has now.
        double reach = Math.Sqrt(hours / Math.Max(hours, _mostHours));
        _widest = (null, 0, 0);
        _looser = new();
        _outOfReach = new();
        foreach (SimRate rate in _watch)
        {
            // A rate of 0, or one with nothing to divide by, has no size in percent: it holds nothing back.
            if (SimRanges.Range(rate, TotalNow, hours, Half, Sparse) is not { Value: not 0 } range) continue;
            int step = Array.FindIndex(_sizes, size => range.Share * reach <= size);
            if (step < 0) { _outOfReach.Add(rate); continue; }
            double held = _sizes[step];
            if (step > 0) _looser.Add((rate, held));
            if (range.Share <= held) continue;
            // The rate furthest from its size, for the development log.
            if (_widest.Rate == null || range.Share / held > _widest.Share / _widest.Size) _widest = (rate, range.Share, held);
        }
        return _widest.Rate == null;
    }

    // The totals so far, as the result will have them (SimResult, SimColumns): read at the end of every part.
    Dictionary<string, double> Totals()
    {
        var totals = new Dictionary<string, double>
        {
            [SimRanges.Kills] = _result.Kills, [SimRanges.Deaths] = _result.Deaths, [SimRanges.Runs] = _result.DungeonRuns,
            [SimRanges.Food] = _result.FoodEaten, [SimRanges.Damage] = _result.DamageTaken, [SimRanges.Healed] = _result.Healed,
            [SimRanges.Ammo] = _ammo(),
        };
        foreach (var skill in _sim.LeveledEntities)
            if (_xpBefore.TryGetValue(skill.Id, out double before)) totals[SimRanges.XpOf(skill.Id)] = skill.Experience.Xp - before;

        // Gold is the coins and what the items sell for (SimResult.GoldPerHour); supplies cost what they sell for.
        double coins = 0, loot = 0, cost = 0, runes = 0, prayer = 0;
        foreach (Amount drop in _loot.Values)
        {
            if (!drop.IsCurrency) loot += drop.Quantity * drop.SellsFor;
            else if (drop.Id == SimEngine.Gold) coins += drop.Quantity;
            totals[ItemKey + drop.Id] = drop.Quantity;
        }
        var kinds = Kinds();
        foreach (Amount used in _used.Values)
        {
            cost += used.Quantity * used.SellsFor;
            if (kinds is { } k && (used.IsCurrency ? k.Prayer.Contains(used.Id) : k.Runes.Contains(used.Id)))
            {
                if (used.IsCurrency) prayer += used.Quantity; else runes += used.Quantity;
            }
            totals[UsedKey + used.Id] = used.Quantity;
        }
        totals[SimRanges.Coins] = coins;
        totals[SimRanges.Loot] = loot;
        totals[SimRanges.Cost] = cost;
        // Runes and prayer points are told apart as the result's columns tell them (SimEngine.SortSupplies).
        if (kinds != null)
        {
            totals[SimRanges.Runes] = runes;
            totals[SimRanges.Prayer] = prayer;
        }
        return totals;
    }

    // The spells and prayers of the copy do not change during the fight: read once. When the game cannot say, the
    // runes and prayer points have no parts, as their columns have no number (SimResult.RunesUsed).
    (HashSet<string> Runes, HashSet<string> Prayer)? Kinds()
    {
        if (_kindsRead) return _kinds;
        _kindsRead = true;
        try { _kinds = SimEngine.SupplyKinds(_combat); }
        catch (Exception) { _kinds = null; }   // SimEngine.SortSupplies says so in the log, once
        return _kinds;
    }

#if DEVTOOLS
    // Development: the parts, what was kept, and the ranges of the main rates, to compare with the tooltip of the line
    // and the details; for a longer fight, why it ended where it did.
    void DevLog(long ticks, SimSpread spread, double[] durations)
    {
        string name = $"{_result.Target.Name} {_result.Target.StanceName}".Trim();
        static string Of(SimRate rate) => $"{rate.Of} per {rate.Per}".ToLowerInvariant();
        string longer = "";
        if (_sure > 0)
        {
            string time = SimText.Clock(ticks / (double)SimEngine.TicksPerSecond), size = SimRanges.Size(_sure * 100);
            static string Percent(double share) => SimRanges.Size(share * 100);
            string looser = _looser.Count == 0 ? "" : "; held to a larger size, which the longest time could reach: " +
                string.Join(", ", _looser.Select(l => $"{Of(l.Rate)} {Percent(l.Size)}"));
            string reach = (_outOfReach.Count == 0 ? "none out of reach" : $"could not reach even {Percent(_sizes[^1])} by the longest time, so not waited for: " +
                string.Join(", ", _outOfReach.Select(Of))) + looser;
            longer = ticks < _asked ? $"; ended by itself at {time}, before the {SimText.Clock(_asked / (double)SimEngine.TicksPerSecond)} asked"
                : _widest.Rate is { } held ? $"; stopped at {time} with {Of(held)} still {Percent(_widest.Share)}, wider than its {Percent(_widest.Size)} ({reach})"
                : $"; went on to {time}, until every watched range was within {size} or its larger size ({reach})";
        }
        _log($"Dev: parts of {name}: {_count} of {SimText.Clock(_ticks / (double)SimEngine.TicksPerSecond)}, " +
             $"{SimText.Clock(ticks / (double)SimEngine.TicksPerSecond)} in all{longer}; watched: {string.Join(", ", _watch.Select(Of))}");
        string Parts(string key) => PartsOf(key, durations) is { } parts && parts.Any(v => v != 0) ? string.Join(" ", parts.Select(p => p.ToString("0.#"))) : "all 0";
        _log($"Dev: parts of {name}: kills {Parts(SimRanges.Kills)}; deaths {Parts(SimRanges.Deaths)}; food {Parts(SimRanges.Food)}; " +
             $"coins {Parts(SimRanges.Coins)}; loot {Parts(SimRanges.Loot)}; kept {System.Text.Json.JsonSerializer.Serialize(spread, new System.Text.Json.JsonSerializerOptions { IncludeFields = true }).Length} bytes, " +
             $"sparse: {(spread.Sparse == null ? "none" : string.Join(", ", spread.Sparse))}");
        string Line(string label, SimRate rate, Func<double, string> format) =>
            SimRanges.Range(rate, q => SimRanges.Total(_result, q), _result.Hours, r => SimRanges.Stored(spread, r), q => spread.Sparse?.Contains(q) == true) is { } r
                ? $"{label} {format(r.Value)} {SimRanges.Text(r, format) ?? "(none)"}" : $"{label} -";
        _log($"Dev: ranges of {name}: " + string.Join(", ", new[]
        {
            Line("kills/h", SimRate.PerHour(SimRanges.Kills), SimView.Fmt), Line("xp/h", SimRate.PerHour(SimRanges.Xp), SimView.Fmt),
            Line("gold/h", SimRate.PerHour(SimRanges.Gold), SimView.Fmt), Line("food/h", SimRate.PerHour(SimRanges.Food), SimView.Fmt),
            Line("deaths/h", SimRate.PerHour(SimRanges.Deaths), SimView.Fmt), Line("damage/h", SimRate.PerHour(SimRanges.Damage), SimView.Fmt),
            Line("food/kill", SimRate.PerKill(SimRanges.Food), SimView.PerKill), Line("cost/h", SimRate.PerHour(SimRanges.Cost), SimView.Fmt),
        }) + "; items: " + string.Join(", ", _result.Loot.Concat(_result.Used).Take(8)
            .Select(a => $"{a.Name} {SimView.Fmt(_result.PerHour(a.Quantity))} " + (SimRanges.OfItem(_result, a, spread) is { } r ? SimRanges.Text(r, SimView.Fmt) : "-"))));
    }
#endif
}
