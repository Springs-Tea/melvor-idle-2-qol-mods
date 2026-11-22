using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Random;
#if DEVTOOLS
using System.Reflection;
using GamesByMalcs.Melvor2.Components;
using GamesByMalcs.Melvor2.Components.Equipment;
using GamesByMalcs.Melvor2.Components.ItemOpening;
using GamesByMalcs.Melvor2.Components.SlayerTasks;
using GamesByMalcs.Melvor2.Components.Toolbelt;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Entities.Thieving;
using GamesByMalcs.Melvor2.Entities.TimeAndWeather;
#endif

namespace CombatSim;

/// <summary>
/// The dice of a simulated fight (issue #5). A copy comes with the dice of the moment its snapshot was taken, so the
/// same fight came out a little different every time Simulate was pressed. Set from the fight and the setup
/// instead, the same setup and the same character give the same numbers again; the order in which the workers take
/// the fights does not matter. The dice are the game's own (Game.RNG, a Xoshiro256StarStarRandom), seeded the game's
/// own way (its constructor from a 64-bit number). The number is a hash of the result's key (SimResult.Key: the
/// setup and the fight), the same in every start of the game, as string.GetHashCode is not.
/// <para>
/// The dice are not all a copy takes from the moment of its snapshot: the character's XP (and so when a level comes
/// during the fight), its slayer task and how far it is, its potions and their charges, a rare event's bonus and the
/// clock of the next one, the time of day. While the character plays, those move on, and a scan a minute later can
/// come out a little different (DevCheck logs them, to compare two scans).
/// </para>
/// </summary>
static class SimDice
{
    // Private to the game: the dice every part of a Game rolls with.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RNG")]
    static extern Xoshiro256StarStarRandom DiceOf(Game game);

    static int s_off;

    /// <summary>Sets the copy's dice for this fight. Call right after its save is loaded.</summary>
    public static void Set(Game sim, string key, Action<string> log)
    {
        if (Volatile.Read(ref s_off) != 0) return;
        try { Seed(sim, key); }
        catch (Exception e)
        {
            // The fights are still right; only the same setup no longer gives the very same numbers twice.
            if (Interlocked.Exchange(ref s_off, 1) == 0)
                log($"The dice of the simulated fights could not be set (the game was updated?): {e.Message}. The same setup may now give slightly different numbers each time.");
        }
    }

    // Its own method, never inlined: a game update that removed these fails when it is called, inside the try.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Seed(Game sim, string key) => DiceOf(sim).RestoreState(new Xoshiro256StarStarRandom(SeedOf(key)).GetState());

    /// <summary>The first eight bytes of the key's SHA-256.</summary>
    public static ulong SeedOf(string key) => BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

#if DEVTOOLS
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RNG")]
    static extern IRandom DiceOfSlot(ActiveEquipmentSlot slot);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RNG")]
    static extern IRandom DiceOfMaster(ActiveSlayerMaster master);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RNG")]
    static extern IRandom DiceOfToolbelt(ActiveToolbeltSlot slot);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ItemPRNGs")]
    static extern Dictionary<Item, Xoshiro256StarStarRandom> DiceOfOpening(ItemOpeningComponent opening);

    // Game.Core is private and of a type the mod cannot name: its dice are read by reflection, for this check only.
    static object? CoreDice(Game game)
    {
        object? core = typeof(Game).GetProperty("Core", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(game);
        return core?.GetType().GetProperty("RNG", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(core);
    }

    /// <summary>
    /// Development: which dice a fight uses, and what the copy took from the character as it was. Before the fight:
    /// the game's dice as they were set; that they are the copy's own (not the played game's, nor its core's); whether
    /// the parts of combat, every equipment and toolbelt slot and every slayer master roll the same dice; the state of
    /// the other dice the game has (opening items, thieving); and the character's levels and XP, hitpoints, slayer
    /// tasks, rare event, time of day, weather and toolbelt. After it: which dice moved, what of that changed during
    /// the fight, and the levels, tasks and rare events that came. Two scans of the same setup that came out different
    /// show here what differed between their copies. Never changes a result: everything it does is in a try.
    /// </summary>
    public sealed class DevCheck
    {
        readonly Action<string> _log;
        readonly string _key, _before, _start;
        readonly Game _sim;
        readonly CombatEntity? _combat;
        readonly Xoshiro256StarStarRandom _game;
        readonly Xoshiro256StarStarRandom.State _dice;
        readonly List<(Xoshiro256StarStarRandom Dice, Xoshiro256StarStarRandom.State State)> _opening = new(), _thieving = new();
        // What happened during the fight that a scan a minute later could have met at another moment.
        readonly List<string> _levels = new(), _events = new();

        internal DevCheck(Game sim, Game? real, string key, Action<string> log)
        {
            _log = log;
            _key = key;
            _sim = sim;
            _game = DiceOf(sim);
            _dice = _game.GetState();
            string Same(object? component) => component is IGameComponent c ? (ReferenceEquals(c.RNG, _game) ? "yes" : c.RNG?.GetType().Name ?? "none") : "not a component";
            string Mine(object? dice) => dice == null ? "none" : ReferenceEquals(dice, _game) ? "yes" : dice.GetType().Name;
            var parts = new List<string>
            {
                // The copy rolls dice of its own: "SHARED" here would mean the seeding reached into the played game.
                $"the played game's dice: {(real == null ? "unknown" : ReferenceEquals(DiceOf(real), _game) ? "SHARED" : "not these")}, " +
                $"its core's: {(real == null ? "unknown" : ReferenceEquals(CoreDice(real), _game) ? "SHARED" : "not these")}, the copy's core rolls these: {Mine(CoreDice(sim))}",
            };
            if (sim.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? ce) && ce is CombatEntity combat)
            {
                _combat = combat;
                parts.Add($"action {Same(combat.Action)}, player {Same(combat.Player)}, enemy {Same(combat.Enemy)}, equipment {Same(combat.Equipment)}");
                int slots = 0, game = 0;
                var other = new List<string>();
                foreach (EquipmentSlot slot in sim.Data.GetDataRegistryOutput<EquipmentSlot>().GetAllObjects())
                {
                    if (!combat.Equipment.TryGetActiveSlot(slot, out IActiveEquipmentSlot? active) || active is not ActiveEquipmentSlot found) continue;
                    slots++;
                    IRandom dice = DiceOfSlot(found);
                    if (ReferenceEquals(dice, _game)) game++; else other.Add($"{slot.Id} {dice?.GetType().Name ?? "none"}");
                }
                parts.Add($"equipment slots on the game's dice: {game} of {slots}{(other.Count > 0 ? " (others: " + string.Join(", ", other) + ")" : "")}");
                parts.Add("toolbelt slots: " + Try(() => Toolbelt(sim, combat, dice: true)));
                parts.Add("slayer masters: " + Try(() => string.Join(", ", Masters(combat).Select(m => $"{Short(m.Data.Id)} {Mine(DiceOfMaster(m))}"))));
                Try(() => { Watch(combat); return ""; });
            }
            if (sim.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? be) && be is BankEntity bank)
                foreach (Xoshiro256StarStarRandom dice in DiceOfOpening(bank.ItemOpening).Values) _opening.Add((dice, dice.GetState()));
            foreach (ThievingEntity thieving in sim.Entities.GetAllObjects().OfType<ThievingEntity>())
                foreach (var recipe in thieving.Action.ActiveRecipes.Values) _thieving.Add((recipe.PRNG, recipe.PRNG.GetState()));
            _before = $"seed {SeedOf(_key):x16}, the game's dice start at {Hex(_dice)}; on the game's dice: {string.Join("; ", parts)}";
            _start = Character();
        }

        // Level ups, slayer tasks done and rare events that come during the fight: each comes at a moment that
        // depends on where the character stood when the snapshot was taken.
        void Watch(CombatEntity combat)
        {
            foreach (var skill in _sim.LeveledEntities.Where(s => SimEngine.CombatSkills.Contains(s.Id)))
            {
                string id = Short(skill.Id) ?? "";
                skill.Experience.LevelChanged += (_, e) => { lock (_levels) _levels.Add($"{id} {e.OldValue}->{e.NewValue}"); };
            }
            foreach (ActiveSlayerMaster master in Masters(combat))
                master.TaskCompleted += (_, _) => { lock (_events) _events.Add("slayer task done"); };
            combat.RareEvents.EventObserved += (_, _) => { lock (_events) _events.Add("rare event"); };
            combat.RareEvents.TimedEventTimerFired += (_, _) => { lock (_events) _events.Add("timed rare event"); };
            combat.TimeOfDay.CurrentSegmentChanged += (_, e) => { lock (_events) _events.Add($"time of day {Short(e.NewValue?.Id)}"); };
        }

        // The state of the character the copy took over, in one line: levels and XP, hitpoints, tasks, rare event,
        // time and weather, toolbelt.
        string Character()
        {
            var said = new List<string>
            {
                "levels " + Try(() => string.Join(", ", _sim.LeveledEntities.Where(s => SimEngine.CombatSkills.Contains(s.Id))
                    .Select(s => $"{Short(s.Id)} {s.Experience.Level} ({s.Experience.Xp:0.##} xp)"))),
            };
            if (_combat is { } combat)
            {
                said.Add("hitpoints " + Try(() => $"{combat.Player.Health.Current}/{combat.Player.Health.Max}"));
                said.Add("slayer " + Try(() => string.Join(", ", Masters(combat).Select(m =>
                    $"{Short(m.Data.Id)} {Short(m.CurrentTask?.Id) ?? "no task"} {m.TaskProgress}/{m.TaskLength}{(m.AutoSlayerEnabled ? " auto" : "")}"))));
                said.Add("rare bonus " + Try(() => combat.RareEvents.ActiveBonuses is { } bonus
                    ? $"{Short(bonus.Event.Id)} {(bonus.IsActivated ? "on" : "off")} {bonus.ChargesLeft} charges" : "none"));
                said.Add("next timed rare event in " + Try(() => $"{combat.RareEvents.TimedEventTimer.TicksLeft} ticks ({combat.RareEvents.TimedEventTimer.State})"));
                said.Add("time of day " + Try(() => $"{combat.TimeOfDay.CurrentTime:hh\\:mm\\:ss} {Short(combat.TimeOfDay.CurrentSegment?.Id)}"));
                said.Add("toolbelt " + Try(() => Toolbelt(_sim, combat, dice: false)));
            }
            said.Add("weather " + Try(() => Short(_sim.Entities.GetAllObjects().OfType<TimeAndWeatherEntity>().FirstOrDefault()?.Weather.CurrentWeather?.Id) ?? "none"));
            return string.Join("; ", said);
        }

        // Every toolbelt slot with something in it: the item and how many, or whether it rolls the game's dice.
        string Toolbelt(Game sim, CombatEntity combat, bool dice)
        {
            var slots = new List<string>();
            foreach (ToolbeltSlot slot in sim.Data.GetDataRegistryOutput<ToolbeltSlot>().GetAllObjects())
            {
                if (!combat.Toolbelt.TryGetActiveSlot(slot, out IActiveToolbeltSlot? active) || active?.Equipped == null) continue;
                slots.Add(dice ? $"{Short(active.Equipped.Item.Id)} {(active is ActiveToolbeltSlot found ? (ReferenceEquals(DiceOfToolbelt(found), _game) ? "yes" : DiceOfToolbelt(found)?.GetType().Name ?? "none") : "?")}"
                               : $"{Short(active.Equipped.Item.Id)} x{active.Quantity}");
            }
            return slots.Count == 0 ? "empty" : string.Join(", ", slots);
        }

        /// <summary>Two lines in the log, once the fight is over: the dice and which moved; the character before and after.</summary>
        public void After()
        {
            try
            {
                int Moved(List<(Xoshiro256StarStarRandom Dice, Xoshiro256StarStarRandom.State State)> dice) => dice.Count(d => !d.Dice.GetState().Equals(d.State));
                Xoshiro256StarStarRandom.State end = _game.GetState();
                _log($"Dev: dice of {_key}: {_before}; after the fight the game's dice {(end.Equals(_dice) ? "did not move" : "moved, to " + Hex(end))}, " +
                     $"item opening dice moved: {Moved(_opening)} of {_opening.Count}, thieving dice moved: {Moved(_thieving)} of {_thieving.Count}");
                string came;
                lock (_levels) lock (_events)
                    came = (_levels.Count == 0 ? "no level up" : "level ups " + string.Join(", ", _levels)) + "; " +
                           (_events.Count == 0 ? "nothing else came" : string.Join(", ", _events.GroupBy(e => e).Select(g => g.Count() == 1 ? g.Key : $"{g.Key} x{g.Count()}")));
                _log($"Dev: copy of {_key}: took {_start}");
                _log($"Dev: copy of {_key}: during the fight {came}; at the end {Character()}");
            }
            catch (Exception e) { _log("Dev: the dice check failed after the fight: " + e.Message); }
        }

        static string Hex(Xoshiro256StarStarRandom.State state) => $"{state.S0:x16}";
    }

    static string? Short(string? id) => id?.Replace("MelvorBase:", "");

    // One piece of a development line: what went wrong in its place, never the whole line lost.
    static string Try(Func<string> read)
    {
        try { return read(); }
        catch (Exception e) { return $"({e.GetType().Name})"; }
    }

    static IEnumerable<ActiveSlayerMaster> Masters(CombatEntity combat) =>
        combat.SlayerTasks?.ActiveMasters.Values ?? Enumerable.Empty<ActiveSlayerMaster>();

    /// <summary>Development: <see cref="DevCheck"/> for a copy whose dice were just set; null (and one line) when it cannot be made.</summary>
    public static DevCheck? DevBefore(Game sim, Game? real, string key, Action<string> log)
    {
        try { return new DevCheck(sim, real, key, log); }
        catch (Exception e) { log("Dev: the dice could not be watched: " + e.Message); return null; }
    }
#endif
}
