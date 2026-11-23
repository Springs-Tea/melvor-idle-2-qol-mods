using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Equipment;
using GamesByMalcs.Melvor2.Components.Toolbelt;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Combat;
#if DEVTOOLS
using GamesByMalcs.Melvor2.Components.Characters;
#endif

namespace CombatSim;

/// <summary>
/// What an enemy's card in the game's combat areas menu needs from a simulated fight against that enemy, read
/// on the copy while it fights: its setup (<see cref="CardSetup"/>), the biggest hit the enemy can deal and the
/// HP at which auto eat steps in. Whatever of this a game update breaks switches the cards off with one line in
/// the log; the fight and its result go on as before.
/// </summary>
sealed class CardReadings : IDisposable
{
    static int s_off;

    /// <summary>The lines on the enemy cards are off for this session: something they need is gone.</summary>
    public static bool Off => Volatile.Read(ref s_off) != 0;

    public static void Stop(Action<string> log, Exception error)
    {
        if (Interlocked.Exchange(ref s_off, 1) == 0)
            log("The tags on the enemy cards are switched off (the game was updated?): " + error.Message);
    }

    /// <summary>Readings for a fight against one enemy; null while the cards are off.</summary>
    public static CardReadings? Start(Game sim, CombatEntity combat, BankEntity bank, SimResult result, Action<string> log)
    {
        if (Off) return null;
        try { return new CardReadings(sim, combat, bank, result, log); }
        catch (Exception e) { Stop(log, e); return null; }
    }

    readonly CombatEntity _combat;
    readonly BankEntity _bank;
    readonly SimResult _result;
    readonly Action<string> _log;
    readonly string _setup;
    // The game's AllAttacksDamageWatcher, kept as the IDisposable it also is: this class has to load, and the
    // fight to run, even in a game version without it.
    IDisposable? _watcher;
    // The highest of the game's readings: the number it settles at, however low its first reading was (issue #67: Black
    // Slime's first reading was 38 and it went to 44 in every fight of the same setup, while no single loss of HP passed 38).
    int? _maxHit;
    // What of the copy's own setup can run out during the fight and so let the enemy hit harder (issue #67): the prayers
    // seen on, and what was on its toolbelt (potions) when the fight began. A rise of the game's number while one of them
    // is gone came with a moment of the fight, and is not kept as the game's (_maxHitRose).
    readonly HashSet<string> _prayers = new(StringComparer.Ordinal);
    readonly List<(IActiveToolbeltSlot Slot, Item Item)> _toolbelt = new();
    // A rise above the highest reading so far, to be checked against what ran out: when the game changes its number and
    // when it switches a prayer off may come in either order, so it is checked at the rise and once more at the next look.
    bool _riseToCheck;
    bool _maxHitRose;
    int _biggestLoss;
    double? _autoEatHp;
    bool _autoEatUnknown;
#if DEVTOOLS
    readonly string _setupText;
    int _lastLow = -1, _healFrom = -1, _eatsLogged;
    /// <summary>Development: the tick of the fight, for when the game's number changed.</summary>
    public Func<long>? DevClock;
    long _devFirstAt = -1, _devSettledAt = -1, _devRanOutAt = -1;
    int? _devFirst, _devLast;
    int _devChanges, _devRises, _devSettledKills;
    string? _devRanOut;
    // The first changes of the game's number, each with what the fight was like at that moment.
    readonly List<string> _devSteps = new();
#endif

    CardReadings(Game sim, CombatEntity combat, BankEntity bank, SimResult result, Action<string> log)
    {
        _combat = combat;
        _bank = bank;
        _result = result;
        _log = log;
        string setup = CardSetup.Describe(sim, combat);
        _setup = CardSetup.Code(setup);
        foreach (ToolbeltSlot slot in sim.Data.GetDataRegistryOutput<ToolbeltSlot>().GetAllObjects())
            if (combat.Toolbelt.TryGetActiveSlot(slot, out var active) && active?.Equipped?.Item is { } item && active.Quantity > 0)
                _toolbelt.Add((active, item));
#if DEVTOOLS
        _setupText = setup;
        combat.Food.FoodEaten += (_, e) => Eaten(e.Healing);
#endif
        combat.Player.Health.CurrentChanged += HealthChanged;
    }

    // A loss of HP bigger than the game's maximum (a passive, a special attack, a burn) counts as the biggest hit.
    void HealthChanged(object? sender, ValueChangedEventArgs<int> e)
    {
        if (e.NewValue < e.OldValue)
        {
            _biggestLoss = Math.Max(_biggestLoss, e.OldValue - e.NewValue);
#if DEVTOOLS
            _lastLow = e.NewValue;
#endif
        }
#if DEVTOOLS
        else if (e.NewValue > e.OldValue) _healFrom = e.OldValue;
#endif
    }

    /// <summary>A look of the fight, every few ticks of it.</summary>
    public void Sample() => Look(true);

    // Also read when the game's maximum hit changes, between two looks.
    void Look(bool look)
    {
        if (Off) return;
        try { Read(look); }
        catch (Exception e) { Stop(_log, e); }
    }

    void Read(bool look)
    {
        // Between spawns there is no opponent, and only the enemy that was asked for counts.
        var enemy = _combat.Enemy;
        if (!enemy.HasTarget || enemy.Enemy?.Id != _result.Target.EnemyId || enemy.AvailableAttacks.Count == 0) return;
        if (_watcher == null)
        {
            // The game's own range of what this enemy's attacks can deal, across all of them.
            var watcher = new AllAttacksDamageWatcher(enemy);
            watcher.MaximumChanged += (_, _) => Look(false);
            _watcher = watcher;
        }
        foreach (var slot in _combat.Prayer.ActiveSlots.Values)
            if (slot.Active?.Data.Id is { } prayer) _prayers.Add(prayer);
        int maximum = ((AllAttacksDamageWatcher)_watcher).Maximum;
        bool rise = maximum > (_maxHit ?? int.MaxValue);
#if DEVTOOLS
        DevReading(maximum, rise);
#endif
        if (rise) _riseToCheck = true;
        _maxHit = Math.Max(_maxHit ?? 0, maximum);
        if (_riseToCheck && !_maxHitRose && RanOut() is { } gone)
        {
            _maxHitRose = true;
#if DEVTOOLS
            (_devRanOut, _devRanOutAt) = (gone, DevNow());
#endif
        }
        if (look) _riseToCheck = false;
        ReadAutoEat();
    }

    // The first prayer seen on that is off now, or toolbelt item the fight began with that is gone (its id), or null.
    string? RanOut()
    {
        var on = _combat.Prayer.ActiveSlots.Values.Select(s => s.Active?.Data.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (string prayer in _prayers)
            if (!on.Contains(prayer)) return prayer;
        foreach (var (slot, item) in _toolbelt)
            if (slot.Equipped?.Item != item || slot.Quantity <= 0) return item.Id;
        return null;
    }

    // Auto eat steps in when the share of HP left falls to the food's AutoEatThreshold. The game keeps the share
    // of HP left as Health.Percent; the same share of these HP is the point in HP. Without food left, or food
    // that does not heal, auto eat cannot help: the point is 0. The lowest point of the fight is kept.
    void ReadAutoEat()
    {
        var health = _combat.Player.Health;
        var food = _combat.Food;
        double point = 0;
        if (food.SelectedFood is { } selected && _bank.Inventory.GetQuantity(selected.Item) > 0
            && food.Stats is { } stats && stats.AutoEatHealing.Value > 0 && stats.AutoEatThreshold.Value > 0)
        {
            if (health.Current <= 0 || health.Percent <= 0) return;
            point = stats.AutoEatThreshold.Value * health.Current / health.Percent;
            // Not on the scale of Health.Percent after all: no point rather than a wrong one.
            if (point > health.Max) { _autoEatUnknown = true; return; }
        }
        _autoEatHp = Math.Min(_autoEatHp ?? point, point);
    }

    public void Dispose()
    {
        try { _watcher?.Dispose(); }
        catch (Exception e) { Stop(_log, e); }
        if (Off) return;
        _result.Setup = _setup;
        if (_maxHit != null || _biggestLoss > 0) _result.MaxEnemyHit = Math.Max(_maxHit ?? 0, _biggestLoss);
        // The game's number apart, so that a comparison can tell it from a loss seen in this fight (issue #67); none where it
        // rose after a prayer or toolbelt item of the copy ran out.
        _result.GameMaxHit = _maxHitRose ? null : _maxHit;
        _result.AutoEatHp = _autoEatUnknown ? null : _autoEatHp;
#if DEVTOOLS
        _result.SetupText = _setupText;
        string verdict = _result.Safe is { } safe ? safe ? "Safe" : "Risky" : "no verdict";
        string held = "";
        if (_maxHit != null)
        {
            string settled = _devRises == 0 ? $"SETTLED AT {_maxHit} at the first reading"
                : $"SETTLED AT {_maxHit} at {DevTime(_devSettledAt)}, kill {_devSettledKills}, after {_devRises} rise{(_devRises == 1 ? "" : "s")} above all before";
            string kept = _maxHitRose ? $"it rose after {_devRanOut} ran out (seen at {DevTime(_devRanOutAt)}): NOT KEPT AS THE GAME'S"
                                      : "no rise after a prayer or toolbelt item ran out: KEPT AS THE GAME'S";
            held = $" (first read {_devFirst} at {DevTime(_devFirstAt)}; {settled}; {_devChanges} changes in all; what can run out: prayers " +
                   $"{(_prayers.Count == 0 ? "none" : string.Join(", ", _prayers))}, toolbelt {(_toolbelt.Count == 0 ? "empty" : string.Join(", ", _toolbelt.Select(t => t.Item.Id)))}; {kept})";
        }
        _log($"Dev: enemy scan {_result.Target.AreaId}/{_result.Target.EnemyId} {_result.Target.StanceName}: deaths {_result.Deaths}, " +
             $"the game's biggest hit {_maxHit?.ToString() ?? "-"}{held}, biggest loss of HP {_biggestLoss}, auto eat at " +
             $"{(_autoEatUnknown ? "unknown" : _autoEatHp?.ToString("0.#") ?? "-")} HP of {_result.MaxHp} -> {verdict}; setup {_setup}");
        if (_devSteps.Count > 0)
            _log($"Dev: enemy scan {_result.Target.AreaId}/{_result.Target.EnemyId} {_result.Target.StanceName}, the game's biggest hit changed " +
                 $"{_devChanges} times; the first {_devSteps.Count}: {string.Join(" | ", _devSteps)}");
#endif
    }

#if DEVTOOLS
    // Development (issue #67): when the game's number changed, and what the fight was like then, to see why it rises. Never
    // throws: a fault here must not switch the cards off.
    void DevReading(int maximum, bool rise)
    {
        try
        {
            long now = DevNow();
            if (_devLast == null) (_devFirst, _devFirstAt) = (maximum, now);
            else if (maximum != _devLast)
            {
                _devChanges++;
                if (rise) (_devSettledAt, _devSettledKills, _devRises) = (now, _result.Kills, _devRises + 1);
                if (_devSteps.Count < 4) _devSteps.Add($"{_devLast} -> {maximum} at {DevTime(now)}{(rise ? ", above all before" : "")}: {DevMoment()}");
            }
            _devLast = maximum;
        }
        catch (Exception e) { if (_devSteps.Count < 4) _devSteps.Add("(could not be read: " + e.Message + ")"); }
    }

    long DevNow() => DevClock?.Invoke() ?? -1;

    static string DevTime(long tick) => tick < 0 ? "an unknown tick" : $"tick {tick} ({SimText.Clock(tick / (double)SimEngine.TicksPerSecond)})";

    // The fight at this moment: kills and deaths so far, the biggest loss of HP yet, the enemy's attacks (with their hits),
    // the effects on each side (and who put them there), the prayers on and what of the copy's ran out.
    string DevMoment()
    {
        static string Short(string? id) => id?.Replace("MelvorBase:", "") ?? "-";
        static string Each<T>(IEnumerable<T> items, Func<T, string> text) => items.Select(text).ToList() is { Count: > 0 } all ? string.Join(", ", all) : "none";
        var enemy = _combat.Enemy;
        string Effects(CharacterComponent who) => Each(_combat.CombatEffects.GetActiveEffectsForCharacter(who),
            e => Short(e.Data.Id) + (e.Source == _combat.Enemy ? " from the enemy" : e.Source == _combat.Player ? " from you" : ""));
        return $"kill {_result.Kills}, deaths {_result.Deaths}, biggest loss of HP yet {_biggestLoss}; the enemy's attacks " +
               $"{Each(enemy.AvailableAttacks, a => $"{Short(a.Attack.Id)} ({a.Attack.Hits.Count} hit{(a.Attack.Hits.Count == 1 ? "" : "s")}, " +
                                                     $"{a.Attack.OnHitEffects.Count + a.Attack.PreHitEffects.Count} effects, chance {a.Chance:0.##})")}; " +
               $"effects on you {Effects(_combat.Player)}; on the enemy {Effects(enemy)}; prayers on " +
               $"{Each(_combat.Prayer.ActiveSlots.Values.Select(s => s.Active?.Data.Id).OfType<string>(), Short)}; ran out {Short(RanOut())}";
    }
#endif

#if DEVTOOLS
    // Development: the first bites of each fight, to compare the point worked out above with where the copy
    // really ate. Which of the HP values is the one before the bite depends on the order of the game's events.
    void Eaten(long healing)
    {
        if (_eatsLogged >= 3) return;
        _eatsLogged++;
        try
        {
            var health = _combat.Player.Health;
            var stats = _combat.Food.Stats;
            _log($"Dev: auto eat against {_result.Target.EnemyId}: HP after the last loss {_lastLow}, last heal from {_healFrom}, " +
                 $"HP now {health.Current} of {health.Max}, Health.Percent {health.Percent}, AutoEatThreshold {stats?.AutoEatThreshold.Value}, " +
                 $"AutoEatLimit {stats?.AutoEatLimit.Value}, healing {healing}, point worked out {_autoEatHp?.ToString("0.#") ?? "-"} HP");
        }
        catch (Exception e) { _log("Dev: auto eat could not be read: " + e.Message); }
    }
#endif
}

/// <summary>
/// A character's setup as a player would name it: what is worn and on the toolbelt, the attack stance, the
/// spell, the food and the prayers. Levels are not part of it, so training does not turn every card grey. It
/// is read the same way on the live character and on a copy, so a scan with nothing changed since matches.
/// </summary>
static class CardSetup
{
    public static string Describe(Game game, CombatEntity combat)
    {
        var parts = new List<string>();
        foreach (EquipmentSlot slot in game.Data.GetDataRegistryOutput<EquipmentSlot>().GetAllObjects().OrderBy(s => s.Id, StringComparer.Ordinal))
            if (combat.Equipment.TryGetActiveSlot(slot, out var active) && active?.Equipped?.Item is { } item)
                parts.Add(slot.Id + "=" + item.Id);
        foreach (ToolbeltSlot slot in game.Data.GetDataRegistryOutput<ToolbeltSlot>().GetAllObjects().OrderBy(s => s.Id, StringComparer.Ordinal))
            if (combat.Toolbelt.TryGetActiveSlot(slot, out var active) && active?.Equipped?.Item is { } item)
                parts.Add("toolbelt " + slot.Id + "=" + item.Id);
        parts.Add("stance=" + combat.Player.Stats.AttackStance.Value?.Id);
        parts.Add("spell=" + combat.MagicSpells.SelectedSpell?.Data.Id);
        parts.Add("food=" + combat.Food.SelectedFood?.Item.Id);
        parts.Add("prayers=" + string.Join(",", combat.Prayer.ActiveSlots.Values.Select(s => s.Selected?.Data.Id)
            .OfType<string>().Order(StringComparer.Ordinal)));
        return string.Join("; ", parts);
    }

    /// <summary>Sixteen characters for the history file, instead of the whole text.</summary>
    public static string Code(string setup) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(setup)), 0, 8);
}
