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
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Combat;

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
    int? _maxHit;
    int _biggestLoss;
    double? _autoEatHp;
    bool _autoEatUnknown;
#if DEVTOOLS
    readonly string _setupText;
    int _lastLow = -1, _healFrom = -1, _eatsLogged;
#endif

    CardReadings(Game sim, CombatEntity combat, BankEntity bank, SimResult result, Action<string> log)
    {
        _combat = combat;
        _bank = bank;
        _result = result;
        _log = log;
        string setup = CardSetup.Describe(sim, combat);
        _setup = CardSetup.Code(setup);
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

    /// <summary>Called every few ticks of the fight, and when the game's maximum hit changes.</summary>
    public void Sample()
    {
        if (Off) return;
        try { Read(); }
        catch (Exception e) { Stop(_log, e); }
    }

    void Read()
    {
        // Between spawns there is no opponent, and only the enemy that was asked for counts.
        var enemy = _combat.Enemy;
        if (!enemy.HasTarget || enemy.Enemy?.Id != _result.Target.EnemyId || enemy.AvailableAttacks.Count == 0) return;
        if (_watcher == null)
        {
            // The game's own range of what this enemy's attacks can deal, across all of them.
            var watcher = new AllAttacksDamageWatcher(enemy);
            watcher.MaximumChanged += (_, _) => Sample();
            _watcher = watcher;
        }
        _maxHit = Math.Max(_maxHit ?? 0, ((AllAttacksDamageWatcher)_watcher).Maximum);
        ReadAutoEat();
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
        _result.AutoEatHp = _autoEatUnknown ? null : _autoEatHp;
#if DEVTOOLS
        _result.SetupText = _setupText;
        string verdict = _result.Safe is { } safe ? safe ? "Safe" : "Risky" : "no verdict";
        _log($"Dev: enemy scan {_result.Target.AreaId}/{_result.Target.EnemyId} {_result.Target.StanceName}: deaths {_result.Deaths}, " +
             $"the game's biggest hit {_maxHit?.ToString() ?? "-"}, biggest loss of HP {_biggestLoss}, auto eat at " +
             $"{(_autoEatUnknown ? "unknown" : _autoEatHp?.ToString("0.#") ?? "-")} HP of {_result.MaxHp} -> {verdict}; setup {_setup}");
#endif
    }

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
