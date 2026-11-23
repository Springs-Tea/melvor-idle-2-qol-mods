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

    // ---- dice kept in step (Find upgrades, issue #4; Compare, issue #57) -----------------------------------

    static int s_stepOff;

    /// <summary>
    /// The copy's dice kept in step with this key from now on (<see cref="InStep"/>), for a fight whose dice come from
    /// another key than its own (SimOptions.Dice: Find upgrades' setup as it is, Compare's fight). Null when they cannot
    /// be: then one line in the log, and the fights have the same dice at their start only.
    /// </summary>
    public static InStep? Keep(Game sim, string key, Action<string> log)
    {
        if (Volatile.Read(ref s_off) != 0 || Volatile.Read(ref s_stepOff) != 0) return null;
        try { return new InStep(DiceFor(sim), key, log); }
        catch (Exception e)
        {
            StepOff(log, e);
            return null;
        }
    }

    // Once a game session: the fights are still right, but an item's numbers, and A's and B's, again carry luck of their own.
    static void StepOff(Action<string> log, Exception e)
    {
        if (Interlocked.Exchange(ref s_stepOff, 1) == 0)
            log($"Find upgrades and Compare could not keep the dice of their fights in step (the game was updated?): {e.Message}. " +
                "The fights they compare now share the same dice only at their start, so a small difference may be luck.");
    }

    // Its own methods, never inlined: a game update that removed these fails when they are called, inside the try.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Xoshiro256StarStarRandom DiceFor(Game sim) => DiceOf(sim);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Restore(Xoshiro256StarStarRandom dice, ulong s0, ulong s1, ulong s2, ulong s3) =>
        dice.RestoreState(new Xoshiro256StarStarRandom.State(s0, s1, s2, s3));

    /// <summary>
    /// Dice kept in step (issue #4). Find upgrades compares every item with one fight of the setup as it is. With dice
    /// of its own, each fight had luck of its own, and one unlucky fight of the setup put nearly every item ahead of it:
    /// in its second check, 14 of 16 boots came out above the setup (on average 266 kills an hour against its 244), while
    /// the very same setup with other dice made 268. The same seed at the start is not enough: a copy rolls one set of
    /// dice for everything, and the first roll that turns out otherwise with the item (a hit that now misses, and so no
    /// roll for its damage) moves every roll after it. So the dice are set again before every tick, from the key and the
    /// moment of the fight: the kills and deaths so far and the ticks since the last kill (or since the fight went on after
    /// a death). Every fight of a search then meets its first, second, third enemy with the same rolls at the same moment,
    /// whatever the item, and what differs comes from the item. The state is the SHA-256 of the key and that moment: its
    /// 32 bytes are the four numbers of the game's dice (Xoshiro256StarStarRandom.State). One hash a tick costs about
    /// 0.4 µs (measured outside the game): about 22 ms in a quarter of an hour of fighting, beside a copy's second or two.
    /// Compare (issue #57) keeps A's and B's fights of an enemy in step the same way, from the key of that fight
    /// (CombatSimMod.CompareDice), so that luck the two setups meet alike cannot put one of them ahead.
    /// </summary>
    public sealed class InStep
    {
        readonly Xoshiro256StarStarRandom _dice;
        readonly Action<string> _log;
        // The key's own SHA-256, then the moment: kills, deaths and ticks, written in place before every tick. 48 bytes,
        // one block of the hash whatever the key's length.
        readonly byte[] _moment = new byte[48];
        const int At = 32;
        bool _off;

        internal InStep(Xoshiro256StarStarRandom dice, string key, Action<string> log)
        {
            _dice = dice;
            _log = log;
            SHA256.HashData(Encoding.UTF8.GetBytes(key), _moment);
        }

        /// <summary>How many times the dice were set, once before every tick: for the development lines.</summary>
        public long Count { get; private set; }
        public bool Off => _off;

        /// <summary>Before a tick: the dice of this moment of the fight.</summary>
        public void Roll(int kills, int deaths, long ticksSince)
        {
            if (_off) return;
            try
            {
                Span<byte> moment = _moment;
                BinaryPrimitives.WriteInt32LittleEndian(moment[At..], kills);
                BinaryPrimitives.WriteInt32LittleEndian(moment[(At + 4)..], deaths);
                BinaryPrimitives.WriteInt64LittleEndian(moment[(At + 8)..], ticksSince);
                Span<byte> hash = stackalloc byte[32];
                SHA256.HashData(moment, hash);
                ulong s0 = BinaryPrimitives.ReadUInt64LittleEndian(hash), s1 = BinaryPrimitives.ReadUInt64LittleEndian(hash[8..]),
                      s2 = BinaryPrimitives.ReadUInt64LittleEndian(hash[16..]), s3 = BinaryPrimitives.ReadUInt64LittleEndian(hash[24..]);
                // All four 0 is the one state these dice never leave; a hash gives it about never.
                if ((s0 | s1 | s2 | s3) == 0) s0 = 1;
                Restore(_dice, s0, s1, s2, s3);
                Count++;
            }
            catch (Exception e)
            {
                _off = true;
                StepOff(_log, e);
            }
        }
    }

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
        // The fight (its result's key) and the key of its dice: another fight's in a search for upgrades, the fight's in Compare.
        readonly string _name, _key, _before, _start;
        readonly Game _sim;
        readonly CombatEntity? _combat;
        readonly Xoshiro256StarStarRandom _game;
        readonly Xoshiro256StarStarRandom.State _dice;
        readonly List<(Xoshiro256StarStarRandom Dice, Xoshiro256StarStarRandom.State State)> _opening = new(), _thieving = new();
        // What happened during the fight that a scan a minute later could have met at another moment.
        readonly List<string> _levels = new(), _events = new();

        internal DevCheck(Game sim, Game? real, string key, Action<string> log, string name)
        {
            _log = log;
            _name = name;
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
            _before = (_name != _key ? $"the dice of {_key}, " : "") + $"seed {SeedOf(_key):x16}, the game's dice start at {Hex(_dice)}; on the game's dice: {string.Join("; ", parts)}";
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

        /// <summary>
        /// Two lines in the log, once the fight is over: the dice and which moved, and whether they were kept in step
        /// (<paramref name="step"/>, Find upgrades and Compare); the character before and after.
        /// </summary>
        public void After(InStep? step)
        {
            try
            {
                int Moved(List<(Xoshiro256StarStarRandom Dice, Xoshiro256StarStarRandom.State State)> dice) => dice.Count(d => !d.Dice.GetState().Equals(d.State));
                Xoshiro256StarStarRandom.State end = _game.GetState();
                string kept = step == null ? "" : step.Off ? $"; kept in step: SWITCHED OFF after {step.Count:N0} ticks" : $"; kept in step: set again before each of {step.Count:N0} ticks";
                _log($"Dev: dice of {_name}: {_before}; after the fight the game's dice {(end.Equals(_dice) ? "did not move" : "moved, to " + Hex(end))}, " +
                     $"item opening dice moved: {Moved(_opening)} of {_opening.Count}, thieving dice moved: {Moved(_thieving)} of {_thieving.Count}{kept}");
                string came;
                lock (_levels) lock (_events)
                    came = (_levels.Count == 0 ? "no level up" : "level ups " + string.Join(", ", _levels)) + "; " +
                           (_events.Count == 0 ? "nothing else came" : string.Join(", ", _events.GroupBy(e => e).Select(g => g.Count() == 1 ? g.Key : $"{g.Key} x{g.Count()}")));
                _log($"Dev: copy of {_name}: took {_start}");
                _log($"Dev: copy of {_name}: during the fight {came}; at the end {Character()}");
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

    /// <summary>
    /// Development: <see cref="DevCheck"/> for a copy whose dice were just set from <paramref name="key"/>, named in the log
    /// by its fight (<paramref name="name"/>, its result's key); null (and one line) when it cannot be made.
    /// </summary>
    public static DevCheck? DevBefore(Game sim, Game? real, string key, Action<string> log, string name)
    {
        try { return new DevCheck(sim, real, key, log, name); }
        catch (Exception e) { log("Dev: the dice could not be watched: " + e.Message); return null; }
    }
#endif
}
