using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Components.Equipment;
using GamesByMalcs.Melvor2.Components.Loadouts;
using GamesByMalcs.Melvor2.Components.MagicSpells;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Saves;
using Godot;

namespace CombatSim;

/// <summary>
/// Simulates fights without re-implementing the game's combat: a copy of the character is loaded into a
/// second, invisible <see cref="Game"/> and the game's own ticks are run on it, on background threads.
/// Every fight starts from a fresh copy of the same snapshot, so results do not leak into each other, and no
/// copy is ever saved.
/// </summary>
sealed class SimEngine
{
    public const string Gold = "MelvorBase:GoldPieces";
    internal const int TicksPerSecond = 60;
    // How often the fight is looked at (restarted after a death, supplies handed back, a cancel noticed).
    const int CheckEvery = 64;
    // Unlimited supplies: what a slot that holds a pile (arrows) is filled with, and what the copy's bank is
    // topped up to for food and runes before the fight. Both are handed back as they are used.
    const int Pile = 100_000, Stock = 1_000;
    // A character that dies within moments of every restart would otherwise spin through the whole fight.
    const int MaxRestarts = 5_000;
    const int MaxDeathsWithoutFoodOrKill = 10;
    // The save the game writes on request must be this fresh to be taken as the snapshot.
    const double SaveMaxAgeSeconds = 10;
    // A run is stopped when the game has grown by this much since it began: copies are not being released.
    const long MemoryLimit = 4L << 30;

    public static readonly string[] CombatSkills =
    {
        "MelvorBase:Attack", "MelvorBase:Strength", "MelvorBase:Defence", "MelvorBase:Hitpoints",
        "MelvorBase:Ranged", "MelvorBase:Magic", "MelvorBase:Prayer", "MelvorBase:Slayer",
    };

    // SaveManager is a static class, which an [UnsafeAccessor] cannot name: one reflection call per copy.
    static readonly MethodInfo? s_loadState = typeof(SaveManager).GetMethod("LoadGameStateFromStream",
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(Stream), typeof(Game)]);

    // The entities the game advances on every tick.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TickingEntities")]
    static extern List<ITickingEntity> TickingEntitiesOf(Game game);

    readonly Action<string> _log;
    CancellationTokenSource? _cancel;

    /// <summary>Filled by the background threads; the mod drains it on the game's own thread.</summary>
    public readonly ConcurrentQueue<SimResult> Finished = new();
    public volatile bool Running;
    public volatile int Total;
    public volatile string Current = "";
    /// <summary>Why the last run stopped by itself, in words for the player; "" when it did not.</summary>
    public volatile string Problem = "";
    int _done;
    public int Done => Volatile.Read(ref _done);

    /// <summary>
    /// Fights simulated side by side. Every fight is a game of its own, so they do not touch each other; half
    /// of the processor is left to the game that is being played.
    /// </summary>
    public static int DefaultWorkers => Math.Clamp(System.Environment.ProcessorCount / 2, 1, 4);

    public SimEngine(Action<string> log) => _log = log;
#if DEVTOOLS
    static WeakReference<Game>? s_lastSim;
    MusicScan _musicScan = null!;
#endif

    public static bool IsSupported => s_loadState != null;

    /// <summary>
    /// Call on the game's thread. Has the game write its save the way its autosave does, takes that file as
    /// the snapshot and starts simulating the targets, a few of them side by side.
    /// </summary>
    public bool Start(Game real, IReadOnlyList<SimTarget> targets, SimOptions options, int workers, out string error) =>
        Start(real, targets.Select(t => new SimJob(t, options)).ToList(), workers, out error);

    /// <summary>
    /// The same, with a setup of its own for each fight: two setups of the same fights (compare mode, issue #2), or
    /// any other list. Every job starts from the one snapshot this call takes, so the setups are compared on the
    /// same character.
    /// </summary>
    public bool Start(Game real, IReadOnlyList<SimJob> jobs, int workers, out string error)
    {
        error = "";
        if (Running) { error = "A simulation is already running."; return false; }
        if (!IsSupported) { error = "This game version loads saves differently: the simulator needs an update."; return false; }
        byte[] save;
        try
        {
            DateTime asked = DateTime.UtcNow;
            real.LocalSave();
            string? file = NewestSave();
            if (file == null) { error = "The character's save file was not found."; return false; }
            // Only the save that was just written: an older file is another moment, or another character.
            if (File.GetLastWriteTimeUtc(file) < asked.AddSeconds(-SaveMaxAgeSeconds))
            { error = "The game did not write a fresh save to simulate from."; return false; }
            save = File.ReadAllBytes(file);
        }
        catch (Exception e) { error = "Could not take a snapshot of the character: " + e.Message; return false; }

        while (Finished.TryDequeue(out _)) { }
        Volatile.Write(ref _done, 0);
        Total = jobs.Count;
        Current = "";
        Problem = "";
        Running = true;
        long memoryAtStart = GC.GetTotalMemory(false);
        var cancel = _cancel = new CancellationTokenSource();
        var waiting = new ConcurrentQueue<SimJob>(jobs);
        workers = Math.Clamp(workers, 1, Math.Max(1, jobs.Count));
        Volatile.Write(ref _shownLogged, 0);
        lock (_paired) _paired.Clear();
#if DEVTOOLS
        _musicScan = new MusicScan(_log);
        _devTwins.Clear();
        _devTakenIn.Clear();
        _devVerdictsWaiting.Clear();
        while (_devVerdictsDue.TryDequeue(out _)) { }
        _devReal = new WeakReference<Game>(real);
        // Two scans of the same setup start from the same snapshot only when nothing changed in between.
        _log($"Dev: snapshot {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(save), 0, 8)}, {save.Length:N0} bytes");
        Volatile.Write(ref _devGearLogged, 0);
        Volatile.Write(ref _devStanceLogged, 0);
        Volatile.Write(ref _devStanceMissingLogged, 0);
        Volatile.Write(ref _devKept, 0);
        Volatile.Write(ref _devSwitchedOff, 0);
        Volatile.Write(ref _devSetOnce, 0);
        Volatile.Write(ref _devNotKept, 0);
        Volatile.Write(ref _devSameDice, 0);
        Volatile.Write(ref _devOtherDice, 0);
#endif
        Task.Run(() =>
        {
            try
            {
                Task.WaitAll(Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
                {
                    while (!cancel.IsCancellationRequested && waiting.TryDequeue(out SimJob? job))
                    {
                        Current = job.Options.StatusName != "" ? job.Options.StatusName : job.Target.Name;
                        SimResult result = RunOne(save, job.Target, job.Options, cancel.Token);
                        if (cancel.IsCancellationRequested) break;   // a fight that was cut short is not a result
                        Finished.Enqueue(result);
                        Interlocked.Increment(ref _done);
                        // The last line of defence against a game version that keeps the copies alive.
                        if (GC.GetTotalMemory(false) - memoryAtStart > MemoryLimit && GC.GetTotalMemory(true) - memoryAtStart > MemoryLimit)
                        {
                            Problem = "Stopped: the simulated copies are not being released in this game version. Restart the game to free the memory.";
                            _log(Problem);
                            cancel.Cancel();
                        }
                    }
                })).ToArray());
            }
            catch (Exception e) { _log("The simulation stopped: " + e); }
            finally
            {
#if DEVTOOLS
                DevRunDice(cancel.IsCancellationRequested);
#endif
                // The parts of the fights with a side (a compare, a search of Find upgrades) are not needed once every
                // pair is made.
                lock (_paired) _paired.Clear();
#if DEVTOOLS
                _musicScan.Finish();
                long before = GC.GetTotalMemory(false);
                long ws = Process.GetCurrentProcess().WorkingSet64;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                _log($"Dev: managed {before >> 20:N0} MB -> {GC.GetTotalMemory(false) >> 20:N0} MB after a full collection; process {ws >> 20:N0} MB -> {Process.GetCurrentProcess().WorkingSet64 >> 20:N0} MB; server GC {System.Runtime.GCSettings.IsServerGC}, latency {System.Runtime.GCSettings.LatencyMode}");
                if (s_lastSim != null && s_lastSim.TryGetTarget(out Game? leaked))
                {
                    var clock = Stopwatch.StartNew();
                    _log("Dev: the last copy is still alive; " + LeakProbe.Find(leaked) + $" ({clock.Elapsed.TotalSeconds:0.0}s)");
                }
                else _log("Dev: the last copy was collected.");
#endif
                Running = false;
            }
        });
        return true;
    }

    public void Cancel() => _cancel?.Cancel();

    static string? NewestSave()
    {
        string root = Path.Combine(OS.GetUserDataDir(), "steam");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateFiles(root, "save_*.sav", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    static void LoadState(Stream stream, Game game)
    {
        try { s_loadState!.Invoke(null, [stream, game]); }
        catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); }
    }

    // A copy's combat action tells the game's audio manager which playlist to play whenever its state changes:
    // a fight that starts, a death, a restart. There is one audio manager, shared with the running game, so a
    // simulated fight changed the music the player hears, and the news of the new track reached the game's
    // music button from a background thread: with the hundreds of deaths of a long scan, a crash. The action
    // puts that handler on its own event when a save is loaded, and the handler does nothing else, so taking
    // it off again is all it takes. The event's list is private to the action; it is removed through the
    // event itself, as in Unhook.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = nameof(CombatActionComponent.StateChanged))]
    static extern ref EventHandler<ValueChangedEventArgs<CombatActionState>>? StateChangedOf(CombatActionComponent action);

    bool RemoveMusicHandler(CombatActionComponent action)
    {
        Delegate[] handlers = StateChangedOf(action)?.GetInvocationList() ?? [];
#if DEVTOOLS
        _musicScan.LogSubscribers(handlers, action);
#endif
        bool removed = false;
        foreach (var handler in handlers)
        {
            if (ReferenceEquals(handler.Target, action) && handler.Method.DeclaringType == typeof(CombatActionComponent)
                && handler.Method.Name == "StateChangedHandler")
            {
                action.StateChanged -= (EventHandler<ValueChangedEventArgs<CombatActionState>>)handler;
                removed = true;
            }
        }
        return removed;
    }

    int _loggedMusic;

    void CutMusic(CombatActionComponent action)
    {
        string reason;
        try
        {
            if (RemoveMusicHandler(action))
            {
#if DEVTOOLS
                _musicScan.CountCut();
#endif
                return;
            }
            reason = "its music handler was not found";
        }
        catch (Exception e) { reason = e.Message; }
        // Once per game session: the fights are still right, but they reach the game's music as they did before.
        if (Interlocked.Exchange(ref _loggedMusic, 1) == 0)
            _log($"A copy could not be cut off from the game's music (the game was updated?): {reason}. Simulated fights may change the music, and a very long scan with many deaths may be unsafe.");
    }

    SimResult RunOne(byte[] save, SimTarget target, SimOptions options, CancellationToken cancel)
    {
        var result = new SimResult { Target = target, SetupKey = options.SetupKey, SetupLabel = options.SetupLabel };
        var clock = Stopwatch.StartNew();
        Game? sim = null;
        // The dice kept in step, and the fight's parts, which a fight with a side (SimOptions.Side) keeps for the range of
        // its difference.
        SimDice.InStep? step = null;
        SimParts? parts = null;
        try
        {
            // A Game cannot take a save twice, so every fight gets a copy of its own (about a second).
            sim = new Game();
#if DEVTOOLS
            _musicScan.CountCopy();
#endif
            sim.LoadForSave();
            using (var stream = new MemoryStream(save)) LoadState(stream, sim);
            // The fight's own dice, or another key's kept in step (Find upgrades: the setup as it is; Compare: the fight's,
            // the same for A and B).
            string diceKey = options.FightDice(result.Target);
            SimDice.Set(sim, diceKey, _log);
            step = options.Dice != null ? SimDice.Keep(sim, diceKey, _log) : null;
#if DEVTOOLS
            SimDice.DevCheck? dice = SimDice.DevBefore(sim, _devReal != null && _devReal.TryGetTarget(out Game? real) ? real : null, diceKey, _log, result.Key);
#endif
            parts = Fight(sim, target, options, result, step, cancel);
#if DEVTOOLS
            dice?.After(step);
#endif
        }
        catch (Exception e)
        {
            result.Failed = true;
            result.Note = "The simulation failed: " + e.Message;
            _log($"{target.Name}: {e}");
        }
        finally
        {
            // The game releases its own Game objects the same way when a character is closed.
            try { sim?.Dispose(); } catch (Exception e) { _log("Releasing the copy failed: " + e.Message); }
            try
            {
                if (sim != null && Unhook(sim) == 0)
                    LogOnce("A simulated copy had no subscriptions to the game's settings to remove. That is fine if the game now releases them itself; if memory grows during a scan, the simulator needs an update.");
            }
            catch (Exception e) { LogOnce("Letting go of the copy failed (the game was updated?): " + e.Message); }
#if DEVTOOLS
            if (sim != null) s_lastSim = new WeakReference<Game>(sim);
#endif
            result.RealMilliseconds = clock.Elapsed.TotalMilliseconds;
        }
#if DEVTOOLS
        // Counted once the fight is over: dice in step that switched off on the way did not keep it in step.
        if (step is { Off: false }) Interlocked.Increment(ref _devKept);
        else if (step != null) Interlocked.Increment(ref _devSwitchedOff);
        else if (options.Dice != null) Interlocked.Increment(ref _devNotKept);
        else Interlocked.Increment(ref _devSetOnce);
#endif
        if (options.Side != null && !cancel.IsCancellationRequested) Pair(result, options, parts?.Kept, step);
#if DEVTOOLS
        if (!cancel.IsCancellationRequested) DevTwin(result);
#endif
        return result;
    }

    // The fights of this run with a side (SimOptions.Side) by the key of their dice, until the run is over: in Compare
    // (issue #57) A's and B's of one enemy, in Find upgrades (issue #63) the setup as it is and every item. Each with its
    // side and its parts (plain numbers, nothing of the copy) when its dice stayed in step to its end.
    readonly Dictionary<string, List<Paired>> _paired = new();

    sealed record Paired(SimResult Result, string Side, string Dice, SimParts.Record? Parts)
    {
#if DEVTOOLS
        // Development: whether its dice were kept in step, switched off on the way, and for how many ticks they were set;
        // and its name for the log: A or B, or in Find upgrades the item, or "the setup as it is" (SimOptions.StatusName).
        public bool DevInStep { get; init; }
        public bool DevOff { get; init; }
        public long DevSet { get; init; }
        public string DevName { get; init; } = "";
#endif
    }

    int _pairLogged;

    // Development and the log: a fight of a search of Find upgrades, whose two sides are the setup as it is and the items.
    static bool OfSearch(string? side) => side is SimOptions.SideNow or SimOptions.SideItem;

    // A fight with a side is over: it is paired with every fight of another side with the same dice that ended before it,
    // and keeps how much of their luck is left in the difference of their rates (SimSpread.Apart, SimParts.Apart) when both
    // stayed in step to their end. In Compare that is A's fight with B's; in Find upgrades an item's with the setup as it
    // is, as the items share a side and are never paired with each other. The list is made whole before it is put on the
    // result, which is not in Finished yet: the game's thread reads it only after that. Of the earlier result only what
    // never changes is read. Whatever goes wrong here costs the pair its share of luck (the two are then judged by their
    // own ranges) and one line in the log, never the fight.
    void Pair(SimResult result, SimOptions options, SimParts.Record? parts, SimDice.InStep? step)
    {
        try
        {
            string dice = options.FightDice(result.Target);
            var mine = new Paired(result, options.Side!, dice, step is { Off: false } ? parts : null)
            {
#if DEVTOOLS
                DevInStep = step != null, DevOff = step?.Off == true, DevSet = step?.Count ?? 0,
                DevName = OfSearch(options.Side) && options.StatusName != "" ? options.StatusName : options.Side!,
#endif
            };
            List<Paired> before;
            lock (_paired)
            {
                if (!_paired.TryGetValue(dice, out List<Paired>? list)) _paired[dice] = list = new();
                before = list.Where(p => p.Side != mine.Side).ToList();
                list.Add(mine);
            }
            List<SimApart>? apart = null;
            foreach (Paired other in before)
            {
                var left = !result.Failed && !other.Result.Failed && mine.Parts != null && other.Parts != null
                    ? SimParts.Apart(mine.Parts, other.Parts) : null;
                if (left is { } l)
                    (apart ??= new()).Add(new SimApart { Stance = other.Result.Target.StanceId, Ms = other.Result.RealMilliseconds, Parts = l.Parts, Left = l.Left });
#if DEVTOOLS
                DevPair(mine, other, left, options.Watch);
#endif
            }
            if (apart != null && result.Spread != null) result.Spread.Apart = apart;
        }
        catch (Exception e)
        {
            if (Interlocked.Exchange(ref _pairLogged, 1) == 0)
                _log(OfSearch(options.Side)
                    ? "The luck that an item of Find upgrades and the setup as it is shared could not be worked out: " + e.Message +
                      ". Its changes are judged by the two ranges side by side, as before."
                    : "The luck that A and B shared could not be worked out: " + e.Message +
                      ". Their fights are judged by their own ranges, as two setups side by side.");
        }
    }

#if DEVTOOLS
    // Development: every result in one line, and a fight that was asked for twice in one run side by side (the
    // "twice=" request of CombatSimMod), to see that the same setup gives the same numbers; so is A's fight beside
    // B's in the twin of Compare (compare=twin), where B is A under another key.
    readonly ConcurrentDictionary<string, (string Line, bool Twin)> _devTwins = new();
    // The game that is played, to tell that a copy's dice are its own.
    WeakReference<Game>? _devReal;

    /// <summary>Development: what B's setup key ends in when B is A under another key (compare=twin, SimCompare.cs).</summary>
    public const string DevTwinKey = "|twin";

    void DevTwin(SimResult result)
    {
        // Every number a result keeps, each exactly: the time in ticks, what the columns, Details and the enemy's card show
        // (the first death, when the food ran out, the hitpoints, the enemy's biggest hit and the game's own, issue #67, the
        // auto-eat point, the stance read back, the card's setup code), the ranges (SimSpread, without the share of luck left,
        // which only the fight of a pair that ended last keeps: B or A of a compare, an item of Find upgrades) and the note.
        // Not the real time it took, nor the setup's name.
        static string Exact(double? v) => v is { } x ? x.ToString("R") : "-";
        string xp = string.Join(" ", result.Xp.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key.Replace("MelvorBase:", "")}={x.Value:R}"));
        string ranges = result.Spread is not { } s ? "none"
            : string.Join(" ", (s.Half ?? new()).OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => $"{h.Key}={string.Join("/", h.Value.Select(Exact))}")) +
              $"; sparse {string.Join(" ", s.Sparse ?? new())}; items {string.Join(" ", (s.Items ?? new()).Select(Exact))}; hits {s.Hits}";
        string line = $"{Math.Round(result.Hours * 3600 * TicksPerSecond):0} ticks ({SimText.Clock(result.Hours * 3600)}), kills {result.Kills}, deaths {result.Deaths}, " +
                      $"first death {Exact(result.FirstDeathSeconds)}, runs {result.DungeonRuns}, food {result.FoodEaten}, food gone {Exact(result.FoodGoneSeconds)} at {result.FoodGoneKills} kills, " +
                      $"damage {result.DamageTaken}, healed {result.Healed}, lowest {result.LowestHp} of {result.MaxHp}, enemy's biggest hit {result.MaxEnemyHit?.ToString() ?? "-"} " +
                      $"(the game's {result.GameMaxHit?.ToString() ?? "-"}), " +
                      $"auto eat {Exact(result.AutoEatHp)}, stance {result.StanceId ?? "-"} '{result.Stance ?? "-"}'{(result.StanceChosen ? " chosen" : "")}" +
                      $"{(result.StanceMissing != null ? " missing " + result.StanceMissing : "")}, setup {result.Setup ?? "-"}, xp {xp}, gold {result.GoldPerHour * result.Hours:R}, " +
                      $"loot {string.Join(" ", result.Loot.Select(l => $"{l.Id.Replace("MelvorBase:", "")}={l.Quantity:R}"))}, used {string.Join(" ", result.Used.Select(u => $"{u.Id.Replace("MelvorBase:", "")}={u.Quantity:R}"))}, " +
                      $"runes {Exact(result.RunesUsed)}, ammo {Exact(result.AmmoUsed)}, prayer points {Exact(result.PrayerPointsUsed)}; ranges {ranges}; " +
                      (result.Failed ? "failed: " : "note: ") + result.Note.Or("none");
        _log($"Dev: result {result.Key}: {line}");
        // B of the twin is put beside A's result, whose key it has without the ending.
        bool twin = result.SetupKey.EndsWith(DevTwinKey, StringComparison.Ordinal);
        string key = twin ? SimResult.MakeKey(result.SetupKey[..^DevTwinKey.Length], result.Target.Key) : result.Key;
        if (_devTwins.TryAdd(key, (line, twin))) return;
        var first = _devTwins[key];
        string name = $"{result.Target.Name} {result.Target.StanceName}".TrimEnd();
        if (twin || first.Twin)
        {
            string a = twin ? first.Line : line, b = twin ? line : first.Line;
            _log(a == b
                ? $"Dev: twin: {name}: A and the same setup as B, with the dice of Compare: THE SAME NUMBERS, every number of the result line exactly."
                : $"Dev: twin: {name}: A and the same setup as B, with the dice of Compare: DIFFERENT.\n  A: {a}\n  B: {b}");
            return;
        }
        _log(first.Line == line
            ? $"Dev: the same fight twice, {result.Target.Name} {result.Target.StanceName}: the same numbers both times."
            : $"Dev: the same fight twice, {result.Target.Name} {result.Target.StanceName}: DIFFERENT.\n  first:  {first.Line}\n  second: {line}");
    }

    // Development: how the dice of the fights of a run were set (RunOne), counted as the fights end; and the pairs of a
    // compare, or of a search of Find upgrades, by the dice they really had (DevPair).
    int _devKept, _devSwitchedOff, _devSetOnce, _devNotKept, _devSameDice, _devOtherDice;

    // At the end of every run, one line: UNCHANGED when every fight had its dice set once from its own key, as a run of one
    // setup always had them; CHANGED when any was kept in step from another key (Compare, Find upgrades), or should have been.
    // Before it, a line for every dice key of which only one side had fights: in Compare a fight whose other setup had none
    // with its dice, in Find upgrades items without the setup as it is (or the other way round).
    void DevRunDice(bool cancelled)
    {
        bool search;
        lock (_paired)
        {
            search = _paired.Values.Any(list => list.Any(p => OfSearch(p.Side)));
            if (!cancelled)
                foreach (List<Paired> fight in _paired.Values.Where(list => list.Select(p => p.Side).Distinct().Count() == 1))
                {
                    Interlocked.Increment(ref _devOtherDice);
                    SimTarget target = fight[0].Result.Target;
                    _log(OfSearch(fight[0].Side)
                        ? $"Dev: upgrades dice: {target.Name} [{target.FightKey}]: {fight.Count} {(fight[0].Side == SimOptions.SideNow ? "fights of the setup as it is" : "fights of the items")} " +
                          $"({string.Join(", ", fight.Select(p => p.DevName))}) with the dice of {fight[0].Dice} and NO FIGHT OF THE OTHER SETUP " +
                          $"({(fight[0].Side == SimOptions.SideNow ? "no item" : "not the setup as it is")}): OTHER DICE"
                        : $"Dev: compare dice: {target.Name} [{target.FightKey}]: {fight.Count} of {fight[0].Side}'s fights with the dice of " +
                          $"{fight[0].Dice} and NO FIGHT OF THE OTHER SETUP: OTHER DICE");
                }
        }
        int kept = Volatile.Read(ref _devKept), off = Volatile.Read(ref _devSwitchedOff), once = Volatile.Read(ref _devSetOnce), notKept = Volatile.Read(ref _devNotKept);
        int same = Volatile.Read(ref _devSameDice), other = Volatile.Read(ref _devOtherDice);
        _log($"Dev: dice in this run: {kept} {(kept == 1 ? "fight" : "fights")} kept in step to their end, {once} with their dice set once from their own key" +
             (off > 0 ? $", {off} whose dice in step SWITCHED OFF on the way" : "") +
             (notKept > 0 ? $", {notKept} with the dice of another key set once only, as they COULD NOT BE KEPT IN STEP" : "") +
             $" (of {kept + off + once + notKept} fights){(same + other > 0 ? $"; pairs of {(search ? "an item and the setup as it is" : "a compare")}: {same} with the same dice, {other} with other dice" : "")}" +
             $": {(kept + off + notKept == 0 ? "UNCHANGED" : "CHANGED")}");
    }

    // Development: a pair of fights with the same dice, once both are over (Pair), on the thread of the one that ended last:
    // the dice each really had (the key they were set from, its seed, SimDice.SeedOf, and whether they stayed in step to the
    // end of the fight), SAME DICE only when both had the same key and both stayed in step, and how their parts were cut for
    // the share of luck left (SimParts.Apart, DevCut). In Compare's words, A and B; in Find upgrades' (issue #63), the item
    // and the setup as it is. Only what never changes once a fight is over is read here; the verdicts wait for the game's
    // thread (DevTakenIn).
    void DevPair(Paired mine, Paired other, (Dictionary<string, double?[]> Left, int Parts)? left, List<SimRate> watch)
    {
        bool search = OfSearch(mine.Side);
        // A is the first of Compare's two; in a search, the item, which is judged against the setup as it is.
        string first = search ? SimOptions.SideItem : "A";
        Paired a = mine.Side == first ? mine : other, b = mine.Side == first ? other : mine;
        static string Dice(Paired p) => $"{p.DevName} {p.Result.Key} from {p.Dice} (seed {SimDice.SeedOf(p.Dice):x16}), " +
            (!p.DevInStep ? "NOT KEPT IN STEP" : p.DevOff ? $"kept in step until it SWITCHED OFF after {p.DevSet:N0} ticks" : $"kept in step to the end, {p.DevSet:N0} ticks") +
            (p.Result.Failed ? ", FAILED" : "");
        bool same = a.Dice == b.Dice && a.DevInStep && !a.DevOff && b.DevInStep && !b.DevOff && !a.Result.Failed && !b.Result.Failed;
        if (same) Interlocked.Increment(ref _devSameDice); else Interlocked.Increment(ref _devOtherDice);
        string cut = a.Parts != null && b.Parts != null ? SimParts.DevCut(a.Parts, b.Parts)
            : $"NOT CUT: the parts of {a.DevName} {(a.Parts == null ? "were not kept" : "were kept")}, of {b.DevName} {(b.Parts == null ? "were not kept" : "were kept")}";
        string keptWith = !search ? $"{mine.Side}'s result" : mine == a ? "the item's result" : "the result of the setup as it is";
        _log($"Dev: {(search ? "upgrades" : "compare")} dice: {(search ? DevSearchName(a.DevName, a.Result) : DevPairName(a.Result, b.Result))}: {Dice(a)}; {Dice(b)}: {(same ? "SAME DICE" : "OTHER DICE")}; " +
             (left is { } l ? $"the share of luck left worked out for {l.Left.Count} quantities from {l.Parts} parts (raised {SimRanges.Noise(l.Parts):0.###} " +
                              $"times for its noise, SimRanges.Noise), kept with {keptWith}; "
                 : "NO SHARE OF LUCK, so they are judged by their own ranges; ") + cut);
        if (!a.Result.Failed && !b.Result.Failed) _devVerdictsDue.Enqueue((a.Result, b.Result, watch, search ? a.DevName : null));
    }

    static string DevPairName(SimResult a, SimResult b) => $"{a.Target.Name} [{a.Target.FightKey}]" +
        (a.Target.StanceName + b.Target.StanceName != "" ? $" ({a.Target.StanceName.Or("-")} / {b.Target.StanceName.Or("-")})" : "");

    // "Leather Boots against the setup as it is, Black Slime [area|enemy] (Stab)": a pair of a search, whose two fights have
    // the one fight, and its stance when its line names one (Best for).
    static string DevSearchName(string item, SimResult r) =>
        $"{item} against the setup as it is, {r.Target.Name} [{r.Target.FightKey}]{(r.Target.StanceName != "" ? $" ({r.Target.StanceName})" : "")}";

    // Development: the pairs whose verdict lines are due (DevPair), with the item's name for a pair of a search, and the
    // results the game's thread has taken in from Finished. A pair's lines are written once both of its results are taken
    // in: by then neither fight's thread writes to them, and the ranges are read on the game's thread, where the window
    // reads them.
    readonly ConcurrentQueue<(SimResult A, SimResult B, List<SimRate> Watch, string? Item)> _devVerdictsDue = new();
    readonly List<(SimResult A, SimResult B, List<SimRate> Watch, string? Item)> _devVerdictsWaiting = new();
    readonly HashSet<SimResult> _devTakenIn = new();

    /// <summary>
    /// Development: the mod took in this result from <see cref="Finished"/>, on the game's thread (CombatSimMod.Pump, and
    /// PumpUpgrades for a search).
    /// </summary>
    public void DevTakenIn(SimResult result)
    {
        _devTakenIn.Add(result);
        while (_devVerdictsDue.TryDequeue(out var due)) _devVerdictsWaiting.Add(due);
        var ready = _devVerdictsWaiting.Where(p => _devTakenIn.Contains(p.A) && _devTakenIn.Contains(p.B)).ToList();
        _devVerdictsWaiting.RemoveAll(ready.Contains);
        foreach (var (a, b, watch, item) in ready) DevVerdicts(a, b, watch, item);
    }

    // Development: a line for every number of a pair that the list's columns (SimOptions.Watch) and Details judge: each one's
    // number and own range, the verdict by the two own ranges side by side (BEFORE issue #57) and the one the window shows
    // (AFTER), and which rule decided. RULE SAME DICE: the difference against the bar, the two own halves added times the
    // share of their luck left raised by its noise (SimRanges.Apart, SimLuck), and by how much the shared dice lowered the
    // bar; RULE AS BEFORE: why (SimRanges.DevWhy), and the two ranges. Better.Neither: SAME, LUCK or APART, whichever way is
    // better. For a compare also the numbers its Details adds; for a search of Find upgrades (issue #63, item: the item's
    // name, a the item's result and b the setup as it is) the list's columns only, which are what an item's Details judges.
    void DevVerdicts(SimResult a, SimResult b, List<SimRate> watch, string? item)
    {
        string name = item != null ? DevSearchName(item, a) : DevPairName(a, b);
        static string F(double v) => v.ToString("0.###");
        static string Word(Verdict v) => v switch { Verdict.Same => "SAME", Verdict.Luck => "LUCK", _ => "APART" };
        var details = new[]
        {
            SimRate.PerHour(SimRanges.Kills), SimRate.PerHour(SimRanges.Xp), SimRate.PerHour(SimRanges.Gold), SimRate.PerHour(SimRanges.Deaths),
            SimRate.PerHour(SimRanges.Damage), SimRate.PerHour(SimRanges.Healed), SimRate.PerKill(SimRanges.Food),
        };
        var rates = item != null ? watch.Distinct() : watch.Concat(details)
            .Concat(a.Xp.Keys.Union(b.Xp.Keys).Order(StringComparer.Ordinal).Select(id => SimRate.PerHour(SimRanges.XpOf(id)))).Distinct();
        string head = item != null ? "Dev: upgrades verdict" : "Dev: verdict", sideA = item != null ? "with it" : "A", sideB = item != null ? "now" : "B";
        foreach (SimRate rate in rates)
        {
            if (SimRanges.Of(a, rate) is not { } ra || SimRanges.Of(b, rate) is not { } rb) continue;
            var (oa, ob, _) = SimRanges.Basis(a, b, rate);
            Verdict before = SimRanges.Judge(ra.Value, rb.Value, oa, ob, Better.Neither);
            Verdict after = SimRanges.Judge(a, b, rate, ra.Value, rb.Value, Better.Neither);
            double d = Math.Abs(ra.Value - rb.Value);
            string how;
            if (SimRanges.Apart(a, b, rate) is { } luck)
                how = $"RULE SAME DICE: |d| {F(d)} against a bar of {F(luck.Reach)}, {SimRanges.DevBar(luck, F)}";
            else
                how = $"RULE AS BEFORE ({SimRanges.DevWhy(a, b, rate)}): " +
                      (oa is { } x && ob is { } y ? $"|d| {F(d)}, the two ranges {F(x.Low)} to {F(x.High)} and {F(y.Low)} to {F(y.High)}" : "no two ranges");
            _log($"{head}: {name}: {$"{rate.Of} per {rate.Per}".ToLowerInvariant()}: {sideA} {F(ra.Value)} {SimRanges.Text(ra, F) ?? "(no range)"}, " +
                 $"{sideB} {F(rb.Value)} {SimRanges.Text(rb, F) ?? "(no range)"}: BEFORE {Word(before)}, AFTER {Word(after)}; {how}");
        }
    }
#endif

    // The fight; its parts, for the luck two fights with the same dice shared (SimParts.Kept: A and B of a compare, an item
    // and the setup as it is in Find upgrades), or null when it never got that far.
    SimParts? Fight(Game sim, SimTarget target, SimOptions options, SimResult result, SimDice.InStep? step, CancellationToken cancel)
    {
        if (!sim.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? ce) || ce is not CombatEntity combat ||
            !sim.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? be) || be is not BankEntity bank)
        { result.Failed = true; result.Note = "The copy has no combat or bank."; return null; }

        // Development: a build with MUSIC_BASELINE leaves the handler on, to measure what it did (MusicScan).
#if !(DEVTOOLS && MUSIC_BASELINE)
        CutMusic(combat.Action);
#endif
        BaseCombatArea? area = combat.Areas.ActiveAreas.Keys.FirstOrDefault(a => a.Id == target.AreaId);
        Enemy? enemy = target.EnemyId == null ? null : (area as CombatArea)?.Enemies.FirstOrDefault(x => x.Id == target.EnemyId);
        if (area == null || (target.EnemyId != null && enemy == null) || (target.EnemyId == null && area is not Dungeon))
        { result.Failed = true; result.Note = "This fight does not exist in the copy."; return null; }

        // Start from a clean slate: nothing running, the chosen equipment on, full health.
        sim.ActionManager.TryStopActive(true);
        StopPassiveSkills(sim);
        var notes = new List<string>();
        Wear(sim, combat, bank, options, notes);
        // An item that could not go on has its note already, and what follows from it is not said twice.
        if (notes.Count == 0 && options.Shown != null && NotAsShown(sim, combat, options.Shown) is { } differs)
        {
            notes.Add(differs.Note);
            if (Interlocked.Exchange(ref _shownLogged, 1) == 0) _log($"{target.Name}: {differs.Log}");
        }
        result.WearNote = string.Join(" ", notes);
        // The stance its line names (Best for), or else the one chosen in the Stance box of Fights (issue #51). Put on, it is
        // the result's until the stance read at the end says otherwise (ReadStance): a stance that cannot be read is not
        // lost for Fight. Not put on, the result names it as missing, and fights as the game has it.
        if ((target.StanceId ?? options.StanceId) is { } stanceId)
        {
            string stanceName = target.StanceId != null ? target.StanceName : options.StanceName;
            result.StanceChosen = Stand(combat, stanceId, stanceName, notes);
            if (result.StanceChosen) (result.StanceId, result.Stance) = (stanceId, stanceName);
            else result.StanceMissing = stanceName;
        }
        Cast(combat, bank, options, notes);
        // "Never runs out" has to hold from the first bite: food that is selected but down to a handful.
        Item? eaten = combat.Food.SelectedFood?.Item;
        if (options.UnlimitedSupplies && eaten != null) TopUp(bank, eaten, Stock);
        combat.Player.GlobalHealth.Set(combat.Player.GlobalHealth.Max);
        result.MaxHp = combat.Player.Health.Max;
        result.LowestHp = combat.Player.Health.Current;
        // What the enemy's card in the game's menu needs from this fight; nothing for a dungeon.
        using var card = enemy == null ? null : CardReadings.Start(sim, combat, bank, result, _log);

        var xpBefore = sim.LeveledEntities.Where(s => CombatSkills.Contains(s.Id)).ToDictionary(s => s.Id, s => (double)s.Experience.Xp);
        var loot = new Dictionary<string, Amount>();
        var used = new Dictionary<string, Amount>();
        // What was used since the last look, to hand back when supplies are unlimited.
        var bankDebt = new Dictionary<Item, int>();
        var ammoDebt = new Dictionary<Item, int>();
        var walletDebt = new Dictionary<Currency, long>();
        bool refilling = false;
        long ticks = 0;
        // The tick the dice in step count from (SimDice.InStep): the one after the last kill, or after a restart.
        long stepFrom = 0;
        double ammoUsed = 0;

        void Add(Dictionary<string, Amount> into, string id, string name, double quantity, double sellsFor, bool isCurrency) =>
            into[id] = into.TryGetValue(id, out Amount? old) ? old with { Quantity = old.Quantity + quantity } : new Amount(id, name, quantity, sellsFor, isCurrency);
        static double Price(Item item) => item.SellsFor is { Currency.Id: Gold } price ? price.Quantity : 0;
        static string ItemName(Item item) => item.DisplayName.GetPluralText(2);

        int deathsWithoutFoodOrKill = 0;
        combat.Enemy.Killed += (_, _) => { result.Kills++; deathsWithoutFoodOrKill = 0; stepFrom = ticks + 1; };
        combat.Areas.DungeonCompleted += (_, _) => result.DungeonRuns++;
        combat.Action.PlayerDied += (_, _) =>
        {
            result.Deaths++;
            if (!options.UnlimitedSupplies && eaten != null && result.FoodEaten > 0 && bank.Inventory.GetQuantity(eaten) == 0)
                deathsWithoutFoodOrKill++;
            else deathsWithoutFoodOrKill = 0;
            if (result.FirstDeathSeconds < 0) result.FirstDeathSeconds = ticks / (double)TicksPerSecond;
        };
        combat.Food.FoodEaten += (_, e) =>
        {
            result.FoodEaten += e.Quantity;
            result.Healed += e.Healing;
            deathsWithoutFoodOrKill = 0;
        };
        combat.Player.Health.CurrentChanged += (_, e) =>
        {
            if (e.NewValue < result.LowestHp) result.LowestHp = e.NewValue;
            if (e.NewValue < e.OldValue) result.DamageTaken += e.OldValue - e.NewValue;
        };
        combat.Loot.EnemyLooted += (_, e) =>
        {
            foreach (ItemQuantity item in e.Items) Add(loot, item.Item.Id, ItemName(item.Item), item.Quantity, Price(item.Item), false);
        };
        combat.Loot.DungeonLooted += (_, e) =>
        {
            foreach (ItemQuantity item in e.Items) Add(loot, item.Item.Id, ItemName(item.Item), item.Quantity, Price(item.Item), false);
        };
        // Items leaving the bank during a fight are supplies: food eaten, runes cast.
        bank.Inventory.ItemQuantityChanged += (_, e) =>
        {
            if (refilling || e.QuantityChange >= 0) return;
            Add(used, e.Item.Id, ItemName(e.Item), -e.QuantityChange, Price(e.Item), false);
            bankDebt[e.Item] = bankDebt.GetValueOrDefault(e.Item) - e.QuantityChange;
        };
        // Ammo is used from the equipment slot, not from the bank.
        combat.Equipment.AmmoConsumed += (_, e) =>
        {
            if (e.Item == null) return;
            Add(used, e.Item.Id, ItemName(e.Item), 1, Price(e.Item), false);
            ammoDebt[e.Item] = ammoDebt.GetValueOrDefault(e.Item) + 1;
            ammoUsed++;
        };
        // Money in is income (coins from drops, slayer coins); money out is a supply (prayer points).
        bank.Wallet.CurrencyQuantityChanged += (_, e) =>
        {
            if (refilling) return;
            long change = e.NewValue - e.OldValue;
            if (change > 0) Add(loot, e.Currency.Id, e.Currency.DisplayName.Text, change, 0, true);
            else if (change < 0)
            {
                Add(used, e.Currency.Id, e.Currency.DisplayName.Text, -change, 0, true);
                walletDebt[e.Currency] = walletDebt.GetValueOrDefault(e.Currency) - change;
            }
        };

        void Refill()
        {
            if (bankDebt.Count + ammoDebt.Count + walletDebt.Count == 0) return;
            refilling = true;
            try
            {
                foreach (var (item, quantity) in bankDebt) bank.Inventory.AddQuantity(item, quantity, AddItemFlags.None);
                foreach (var (item, quantity) in ammoDebt) combat.Equipment.TryAddQuantity(item, quantity);
                foreach (var (currency, quantity) in walletDebt) bank.Wallet.AddQuantity(currency, quantity);
            }
            finally { refilling = false; }
            bankDebt.Clear(); ammoDebt.Clear(); walletDebt.Clear();
        }

        bool Begin()
        {
            if (enemy != null) combat.Areas.SelectEnemy((CombatArea)area, enemy);
            else combat.Areas.SelectDungeon((Dungeon)area);
            // Selecting what is already selected switches it off, the way the game's button does.
            if (!sim.ActionManager.HasActiveAction)
            {
                if (enemy != null) combat.Areas.SelectEnemy((CombatArea)area, enemy);
                else combat.Areas.SelectDungeon((Dungeon)area);
            }
            return sim.ActionManager.HasActiveAction;
        }

        if (!Begin()) { result.Failed = true; result.Note = "The fight could not be started (requirements not met?)."; return null; }
        if (notes.Count > 0) result.Note = string.Join(" ", notes);
#if DEVTOOLS
        string devGear = DevGear(sim, combat, options, result, null);
        var devStance = DevStance(combat);
        // For the moments at which the game's number of the enemy's biggest hit changed (issue #67).
        if (card != null) card.DevClock = () => ticks;
#endif

        long total = (long)(options.Hours * 3600 * TicksPerSecond);
        // Simulated longer, the fight may go on to the longest duration, until its ranges are small enough (SimParts).
        // Never From bank: that fight is about how far the supplies get you in the time asked.
        long limit = options.Sure > 0 && options.UnlimitedSupplies ? Math.Max(total, (long)(options.MostHours * 3600 * TicksPerSecond)) : total;
        var parts = new SimParts(sim, combat, result, options, total, CheckEvery, xpBefore, loot, used, () => ammoUsed, _log);
        int deathsSeen = 0, restarts = 0;
        // A fight with a side (a compare, a search of Find upgrades) notes its totals at its kills (SimParts.Killed), to be cut
        // at the same kills as the other side's. That only reads what the fight has counted: it rolls nothing.
        bool marking = options.Side != null;
        int marked = 0;
        for (; ticks < limit; ticks++)
        {
            step?.Roll(result.Kills, result.Deaths, ticks - stepFrom);
            sim.PropagateTick();
            if (marking && result.Kills != marked) parts.Killed(ticks, marked = result.Kills);
            if (ticks % CheckEvery != 0) continue;
#if DEVTOOLS
            if (ticks == CheckEvery) DevGear(sim, combat, options, result, devGear);
#endif
            if (options.UnlimitedSupplies) Refill();
            card?.Sample();
            if (cancel.IsCancellationRequested) break;
            // Food from the bank can run out while the fight goes on; what follows is a fight without food.
            if (!options.UnlimitedSupplies && result.FoodGoneSeconds < 0 && eaten != null && result.FoodEaten > 0 && bank.Inventory.GetQuantity(eaten) == 0)
            {
                result.FoodGoneSeconds = ticks / (double)TicksPerSecond;
                result.FoodGoneKills = result.Kills;
                notes.Add($"Ran out of {ItemName(eaten)} after {Clock(ticks)}.");
                result.Note = string.Join(" ", notes);
            }
            // A part ends here when one is due; simulated longer, the fight ends with it once its ranges are small
            // enough, unless it has just ended by itself and is told below why.
            if (parts.Step(ticks) && sim.ActionManager.HasActiveAction) break;
            if (sim.ActionManager.HasActiveAction) continue;

            // The fight ended. After a death it goes on, to learn how often that happens; anything else
            // (out of ammo, runes, food) ends it here.
            bool died = result.Deaths > deathsSeen;
            deathsSeen = result.Deaths;
            if (!died)
            {
                // The food that ran out has its note already.
                Amount? empty = used.Values.FirstOrDefault(u => !u.IsCurrency && !(result.FoodGoneSeconds >= 0 && u.Id == eaten?.Id)
                                                                && sim.Data.GetDataRegistryOutput<Item>().TryGetObjectById(u.Id, out Item? item)
                                                                && bank.Inventory.GetQuantity(item) == 0 && combat.Equipment.GetEquippedQuantity(item) == 0);
                notes.Add(empty != null ? $"Ran out of {empty.Name} after {Clock(ticks)}." : $"The fight stopped after {Clock(ticks)}.");
                result.Note = string.Join(" ", notes);
                break;
            }
            if (deathsWithoutFoodOrKill >= MaxDeathsWithoutFoodOrKill)
            {
                // From the bank, with the food gone: nothing but dying is left, and its rates would say nothing.
                notes.Add($"Stopped at {Clock(ticks)} after {deathsWithoutFoodOrKill} deaths without food or a kill.");
                result.Note = string.Join(" ", notes);
                break;
            }
            if (++restarts > MaxRestarts || !Begin()) { notes.Add($"Could not go on after dying at {Clock(ticks)}."); result.Note = string.Join(" ", notes); break; }
            stepFrom = ticks + 1;
        }

        result.Hours = ticks / (3600.0 * TicksPerSecond);
        ReadStance(combat, result);
#if DEVTOOLS
        DevStanceLog(target, options, result, devStance, DevStance(combat));
#endif
        foreach (var skill in sim.LeveledEntities)
            if (xpBefore.TryGetValue(skill.Id, out double before) && skill.Experience.Xp - before > 0)
                result.Xp[skill.Id] = skill.Experience.Xp - before;
        result.Loot = loot.Values.OrderByDescending(l => l.IsCurrency ? double.MaxValue : l.Quantity * Math.Max(l.SellsFor, 0.001)).ToList();
        result.Used = used.Values.OrderByDescending(u => u.Quantity).ToList();
        result.AmmoUsed = ammoUsed;
        SortSupplies(combat, result);
        // The ranges are an extra: if they cannot be worked out, the fight's numbers still stand, without them.
        try { result.Spread = parts.Finish(ticks); }
        catch (Exception e)
        {
            if (Interlocked.Exchange(ref _rangesLogged, 1) == 0) _log("The ranges of a fight could not be worked out: " + e.Message);
        }
        return parts;
    }

    int _rangesLogged;

    int _suppliesLogged;

    // For the list's columns: which of the supplies used were runes and which prayer points, read once when the
    // fight is over. Runes are the items any spell or imbuement of the copy can cost (BaseMagicSpell.RuneCosts of
    // MagicSpellsComponent.ActiveSpells and ActiveImbuements), prayer points the currencies its prayers cost
    // (PrayerComponent.CostCurrencies); ammo was counted as the game's AmmoConsumed took it from the slot. Used
    // holds every piece that left the bank, so a From bank fight that stopped because its runes ran out has the
    // runes of the time until then. When the game cannot say, they stay unknown and the fight is still right.
    void SortSupplies(CombatEntity combat, SimResult result)
    {
        try
        {
            var (runes, prayer) = SupplyKinds(combat);
            result.RunesUsed = result.Used.Where(u => !u.IsCurrency && runes.Contains(u.Id)).Sum(u => u.Quantity);
            result.PrayerPointsUsed = result.Used.Where(u => u.IsCurrency && prayer.Contains(u.Id)).Sum(u => u.Quantity);
#if DEVTOOLS
            // To compare with "Used" in the details of the same line.
            static string Listed(IEnumerable<Amount> amounts)
            {
                var all = amounts.Select(u => $"{u.Id} {u.Quantity:0}").ToList();
                return all.Count == 0 ? "none" : string.Join(", ", all.Take(6)) + (all.Count > 6 ? $" and {all.Count - 6} more" : "");
            }
            _log($"Dev: supplies of {result.Target.Name} {result.Target.StanceName}: {runes.Count} kinds of rune known, prayer points are {string.Join(", ", prayer)}; " +
                 $"runes {result.RunesUsed:0} ({Listed(result.Used.Where(u => !u.IsCurrency && runes.Contains(u.Id)))}), prayer points {result.PrayerPointsUsed:0}, ammo {result.AmmoUsed:0}");
            _log($"Dev: supplies of {result.Target.Name} {result.Target.StanceName}, the rest of Used: " +
                 Listed(result.Used.Where(u => u.IsCurrency ? !prayer.Contains(u.Id) : !runes.Contains(u.Id))));
#endif
        }
        catch (Exception e)
        {
            if (Interlocked.Exchange(ref _suppliesLogged, 1) == 0)
                _log("Runes and prayer points could not be told apart from the other supplies (the game was updated?): " + e.Message);
        }
    }

    // Its own method, never inlined: a game update that removed these fails when it is called, inside the try.
    // Also read by SimParts, for the runes and prayer points of each part of the fight.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static (HashSet<string> Runes, HashSet<string> Prayer) SupplyKinds(CombatEntity combat) =>
        (combat.MagicSpells.ActiveSpells.Keys.SelectMany(s => s.RuneCosts)
             .Concat(combat.MagicSpells.ActiveImbuements.Keys.SelectMany(s => s.RuneCosts)).Select(c => c.Item.Id).ToHashSet(),
         combat.Prayer.CostCurrencies.Select(c => c.Id).ToHashSet());

    // Puts the chosen loadout on, then the single items that were swapped in. An item the character does
    // not own is handed to the copy first; the game's own equip call still decides whether it can be worn.
    // They go on in the order the left of the window works them out in (SimGear.Steps), so the copy ends with
    // what the slots there show: a two-handed weapon in both hands, a shield in place of one.
    static void Wear(Game sim, CombatEntity combat, BankEntity bank, SimOptions options, List<string> notes)
    {
        if (options.LoadoutId != null && combat.Loadouts.CurrentLoadout?.Data.Id != options.LoadoutId)
        {
            LoadoutSlot? loadout = combat.Loadouts.ActiveLoadouts.Keys.FirstOrDefault(l => l.Id == options.LoadoutId);
            if (loadout == null) notes.Add("The loadout no longer exists.");
            else combat.Loadouts.SwapToLoadout(loadout);
        }
        if (options.Overrides.Count == 0) return;
        var slots = sim.Data.GetDataRegistryOutput<EquipmentSlot>().GetAllObjects().ToList();
        var items = sim.Data.GetDataRegistryOutput<Item>();

        void PutOn(EquipmentSlot slot, string itemId)
        {
            if (!items.TryGetObjectById(itemId, out Item? item) || !item.Capabilities.TryGet(out EquipmentCapability? wearable)) return;
            // One of anything is handed to the copy, owned or not: that is what trying an item means. Things
            // worn in numbers (arrows) are a supply: a pile when supplies are unlimited, what you own otherwise.
            int quantity = 1;
            if (wearable.CanEquipQuantity)
            {
                quantity = options.UnlimitedSupplies ? Pile : bank.Inventory.GetQuantity(item);
                if (quantity == 0) { notes.Add($"You have no {item.DisplayName.GetPluralText(2)} in the bank to equip."); return; }
            }
            TopUp(bank, item, quantity);
            combat.ItemEquipping.EquipItem(item, quantity, slot);
            if (!combat.Equipment.IsItemEquipped(item))
                notes.Add($"{item.DisplayName.GetPluralText(1)} could not be equipped (requirements not met?).");
        }

#if DEVTOOLS
        // Development (raw=): the one change goes on as the game takes it by itself, the way it went before issue #46.
        if (options.DevRaw)
        {
            foreach (var (slot, itemId) in SimGear.Steps(slots, options.Overrides, _ => false))
                if (itemId == "") combat.ItemEquipping.UnequipSlot(slot);
                else PutOn(slot, itemId);
            return;
        }
#endif
        // What the loadout put on, read as the left side reads it (SimGear.Live).
        GearSet before = SimGear.Live(combat.Equipment, slots, null);
        foreach (var (slot, itemId) in SimGear.Steps(slots, options.Overrides, s => before[s.Id] is { FilledFrom: not null }))
        {
            if (itemId != "") PutOn(slot, itemId);
            // Only a slot that holds an item of its own is emptied: one that another slot's item fills (a two-handed
            // weapon's Offhand) keeps it, as on the left, and an empty one is left alone.
            else if (before[slot.Id] is { FilledFrom: null }) combat.ItemEquipping.UnequipSlot(slot);
        }
    }

    // Once a run: the first fight whose copy is not what the slots on the left show, in the log.
    int _shownLogged;

    // The copy against the slots on the left (issue #46). Wear takes the changes in the order the left side works them out
    // in, but the game's own equip call decides: where it took off more, or left on what the left side took off, the
    // result says so in one line. What fights is counted, not where: an item in two slots of its own is two (the same
    // ring in both ring slots), a two-handed weapon is one (SimGear's OwnItems), and two slots that traded their items
    // fight the same. The copy's slots are read once a fight, the way the left side reads the character's (SimGear.Live).
    // Null when both wear the same.
    static (string Note, string Log)? NotAsShown(Game sim, CombatEntity combat, IReadOnlyDictionary<string, string> shown)
    {
        var active = sim.Data.GetDataRegistryOutput<EquipmentSlot>().GetAllObjects().Where(s => combat.Equipment.TryGetActiveSlot(s, out _)).ToList();
        Dictionary<string, string> worn = SimGear.Live(combat.Equipment, active, null).OwnItems();
        static Dictionary<string, int> Counted(IEnumerable<string> ids) => ids.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        Dictionary<string, int> showing = Counted(shown.Values), wearing = Counted(worn.Values);
        var differ = showing.Keys.Union(wearing.Keys).Where(id => showing.GetValueOrDefault(id) != wearing.GetValueOrDefault(id)).ToList();
        if (differ.Count == 0) return null;

        var items = sim.Data.GetDataRegistryOutput<Item>();
        string Name(string id, int count = 1) => items.TryGetObjectById(id, out Item? item) ? item.DisplayName.GetPluralText(count) : id;
        static string Joined(List<string> names) => names.Count == 1 ? names[0] : string.Join(", ", names[..^1]) + " and " + names[^1];
        var without = differ.Where(id => wearing.GetValueOrDefault(id) == 0).Select(id => Name(id)).ToList();
        var with = differ.Where(id => showing.GetValueOrDefault(id) == 0).Select(id => Name(id)).ToList();
        var parts = new List<string>();
        if (without.Count > 0) parts.Add("without " + Joined(without));
        if (with.Count > 0) parts.Add("with " + Joined(with));
        // Worn, but not as often as shown: "with 1 Gold Ring instead of 2".
        parts.AddRange(differ.Where(id => showing.GetValueOrDefault(id) > 0 && wearing.GetValueOrDefault(id) > 0)
            .Select(id => $"with {wearing[id]} {Name(id, wearing[id])} instead of {showing[id]}"));
        string Slots(IReadOnlyDictionary<string, string> set) => string.Join(", ", set.Select(s => $"{s.Key}={Name(s.Value)}"));
        return ($"The copy did not wear what the slots on the left show: it fought {Joined(parts)}.",
            "the copy did not wear what the slots on the left showed: " +
            string.Join(", ", differ.Select(id => $"{Name(id)} shown {showing.GetValueOrDefault(id)}, worn {wearing.GetValueOrDefault(id)}")) +
            $". The slots on the left, each item in its own slot: {Slots(shown)}. The copy's: {Slots(worn)}");
    }

#if DEVTOOLS
    int _devGearLogged;

    // Development (issue #46): what the copy wears when its fight starts, slot by slot, against the slots on the left of
    // the window when Simulate was pressed (SimOptions.DevGear). Logged for the first fight of a run and for any fight
    // whose copy differs; a second into the fight it is read again, in case the game changed it by then.
    string DevGear(Game sim, CombatEntity combat, SimOptions options, SimResult result, string? atStart)
    {
        var slots = sim.Data.GetDataRegistryOutput<EquipmentSlot>().GetAllObjects().Where(s => combat.Equipment.TryGetActiveSlot(s, out _)).ToList();
        string copy = SimGear.Describe(SimGear.Live(combat.Equipment, slots, null), slots);
        if (atStart != null)
        {
            if (copy != atStart) _log($"Dev: gear of the copy for {result.Target.Name}, a second into the fight, CHANGED since its start: {copy}");
            return copy;
        }
        bool same = copy == options.DevGear;
        if (!same || Interlocked.Exchange(ref _devGearLogged, 1) == 0)
            _log($"Dev: gear of the copy for {result.Target.Name} at the start of its fight, with the changes " +
                 $"{(options.Overrides.Count == 0 ? "none" : string.Join(", ", options.Overrides.Select(o => o.Key + "=" + (o.Value == "" ? "nothing" : o.Value))))}: {copy}. " +
                 $"The left side: {options.DevGear}. {(same ? "THE SAME." : "THEY DIFFER.")}{(result.Note != "" ? " Note: " + result.Note : "")}");
        return copy;
    }
#endif

    // Selects one of the attack stances of the weapon that is worn (they decide which skills get the XP). True when the
    // copy is in it; otherwise the note says so, and it fights in the one the game has selected for the weapon.
    static bool Stand(CombatEntity combat, string stanceId, string stanceName, List<string> notes)
    {
        WeaponType? weapon = (combat.Equipment.CurrentWeapon ?? combat.Equipment.DefaultWeapon)?.Weapon?.Type;
        AttackStance? stance = weapon?.Stances.FirstOrDefault(s => s.Id == stanceId);
        if (weapon == null || stance == null) { notes.Add($"The stance {stanceName} is not available with this weapon."); return false; }
        combat.Player.SetSelectedStance(weapon, stance);
        return true;
    }

    int _stanceLogged;

    // The stance the copy fought in, for the result to name (issue #51): the one the game's combat used at the end of the
    // fight, read the way the enemy cards read it (CardSetup.Describe). The stance is an extra: when it cannot be read,
    // one line in the log, and the result keeps the stance it was put in (Fight), or goes without one.
    void ReadStance(CombatEntity combat, SimResult result)
    {
        try
        {
            if (FoughtIn(combat) is not { } stance) return;
            result.StanceId = stance.Id;
            result.Stance = stance.DisplayName.Text;
        }
        catch (Exception e)
        {
            if (Interlocked.Exchange(ref _stanceLogged, 1) == 0) _log("The stance of a fight could not be read (the game was updated?): " + e.Message);
        }
    }

    // Its own method, never inlined: a game update that removed it fails when it is called, inside the try.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static AttackStance? FoughtIn(CombatEntity combat) => combat.Player.Stats.AttackStance.Value;

#if DEVTOOLS
    int _devStanceLogged, _devStanceMissingLogged;

    // Development: the copy's stance in the two ways the game has it, the stat its combat uses (CharacterStats.AttackStance)
    // and the one selected for its weapon (PlayerComponent.GetSelectedStance), to see that they agree.
    static (string? Stat, string? Selected, string Words) DevStance(CombatEntity combat)
    {
        try
        {
            WeaponType? weapon = (combat.Equipment.CurrentWeapon ?? combat.Equipment.DefaultWeapon)?.Weapon?.Type;
            string? stat = FoughtIn(combat)?.Id, selected = weapon == null ? null : combat.Player.GetSelectedStance(weapon)?.Id;
            return (stat, selected, $"stat {stat ?? "-"}, selected for {weapon?.Id ?? "no weapon"} {selected ?? "-"}");
        }
        catch (Exception e) { return (null, null, "not readable: " + e.Message); }
    }

    // Development: what the stance of a fight was asked to be and what the copy fought in, when its fight began and when it
    // ended. Logged for the first fight of a run, for the first one whose weapon did not have the stance asked for, and for
    // every fight where these do not agree.
    void DevStanceLog(SimTarget target, SimOptions options, SimResult result,
        (string? Stat, string? Selected, string Words) atStart, (string? Stat, string? Selected, string Words) atEnd)
    {
        string? asked = target.StanceId ?? options.StanceId;
        bool agree = atStart.Stat == atEnd.Stat && atEnd.Stat == atEnd.Selected && atEnd.Stat == result.StanceId
                     && (!result.StanceChosen || asked == result.StanceId);
        bool firstMissing = result.StanceMissing != null && Interlocked.Exchange(ref _devStanceMissingLogged, 1) == 0;
        if (agree && !firstMissing && Interlocked.Exchange(ref _devStanceLogged, 1) != 0) return;
        _log($"Dev: stance of the copy for {result.Key}: asked {asked ?? "none"} ({(target.StanceId != null ? "by its line" : options.StanceId != null ? "in the Stance box" : "as the game has it")}), " +
             $"put on {result.StanceChosen}{(result.StanceMissing != null ? $" (the weapon has no {result.StanceMissing})" : "")}; when the fight began {atStart.Words}; when it ended {atEnd.Words}; " +
             $"the result says {result.StanceId ?? "-"} '{result.Stance ?? "-"}'{(result.StanceAsAsked ? "" : ", as the game has it")}. {(agree ? "THEY AGREE." : "THEY DO NOT AGREE.")}");
    }
#endif

    // Selects the spell to fight with. With unlimited supplies the copy is handed its runes, so a spell the
    // character cannot pay for right now can still be tried.
    static void Cast(CombatEntity combat, BankEntity bank, SimOptions options, List<string> notes)
    {
        if (options.SpellId != null)
        {
            MagicSpell? spell = combat.MagicSpells.ActiveSpells.Keys.FirstOrDefault(s => s.Id == options.SpellId);
            if (spell == null) { notes.Add("The spell no longer exists."); return; }
            combat.MagicSpells.SelectSpell(spell);
            if (combat.MagicSpells.SelectedSpell?.Data.Id != options.SpellId)
            {
                notes.Add($"{spell.DisplayName.Text} cannot be cast with this weapon.");
                return;
            }
        }
        if (!options.UnlimitedSupplies || combat.MagicSpells.SelectedSpell is not { } casting) return;
        foreach (ItemQuantity rune in casting.RuneCosts) TopUp(bank, rune.Item, rune.Quantity * Stock);
    }

    /// <summary>Makes sure the copy's bank holds at least this many of an item.</summary>
    static void TopUp(BankEntity bank, Item item, int atLeast)
    {
        int missing = atLeast - bank.Inventory.GetQuantity(item);
        if (missing > 0) bank.Inventory.AddQuantity(item, missing, AddItemFlags.None);
    }

    // ---- letting a copy go ---------------------------------------------------------------------------------

    // The game's settings live as long as the program, and the parts of a Game subscribe to their change
    // events without ever unsubscribing. That is harmless for the one game that is played, but every copy made
    // here would stay in memory for good (about 120 MB each). So the copy's handlers are taken off again.
    const string SettingsNamespace = "GamesByMalcs.Melvor2.Settings";
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    static List<FieldInfo>? s_settingsRoots;
    static readonly ConcurrentDictionary<Type, FieldInfo[]> s_settingsFields = new();
    int _logged;

    void LogOnce(string message)
    {
        if (Interlocked.Exchange(ref _logged, 1) == 0) _log(message);
    }

    static bool IsSettingsType(Type type) => type.Namespace is { } ns && ns.StartsWith(SettingsNamespace, StringComparison.Ordinal);

    static FieldInfo[] SettingsFields(Type type) => s_settingsFields.GetOrAdd(type, static t =>
    {
        var fields = new List<FieldInfo>();
        for (Type? at = t; at != null && at != typeof(object); at = at.BaseType)
            fields.AddRange(at.GetFields(Any | BindingFlags.Instance).Where(f => typeof(Delegate).IsAssignableFrom(f.FieldType) || IsSettingsType(f.FieldType)));
        return fields.ToArray();
    });

    /// <summary>Removes the copy's handlers from the events of the game's settings. Returns how many.</summary>
    static int Unhook(Game sim)
    {
        s_settingsRoots ??= typeof(Game).Assembly.GetTypes()
            .Where(t => IsSettingsType(t) && !t.ContainsGenericParameters)
            .SelectMany(t => t.GetFields(Any | BindingFlags.Static))
            .Where(f => !f.IsLiteral && IsSettingsType(f.FieldType))
            .ToList();

        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        foreach (FieldInfo root in s_settingsRoots)
            if (root.GetValue(null) is { } value && seen.Add(value)) queue.Enqueue(value);

        int removed = 0;
        while (queue.Count > 0)
        {
            object settings = queue.Dequeue();
            foreach (FieldInfo field in SettingsFields(settings.GetType()))
            {
                object? value = field.GetValue(settings);
                if (value is Delegate handlers)
                {
                    // The event's own "remove" is used: it is safe against another thread subscribing meanwhile.
                    MethodInfo? remove = field.DeclaringType?.GetEvent(field.Name, Any | BindingFlags.Instance)?.GetRemoveMethod(true);
                    if (remove == null) continue;
                    foreach (Delegate handler in handlers.GetInvocationList())
                        if (BelongsTo(handler.Target, sim)) { remove.Invoke(settings, [handler]); removed++; }
                }
                else if (value != null && seen.Add(value)) queue.Enqueue(value);
            }
        }
        return removed;
    }

    // A handler is the copy's when it sits on the copy itself, on one of its entities, or on a part of one.
    static bool BelongsTo(object? target, Game sim)
    {
        if (target == null) return false;
        if (ReferenceEquals(target, sim)) return true;
        // By name and type, not GetProperty: a part that re-declares "Entity" would make that call ambiguous.
        GameEntity? entity = target as GameEntity ?? target.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(p => p.Name == "Entity" && typeof(GameEntity).IsAssignableFrom(p.PropertyType) && p.GetIndexParameters().Length == 0)
            ?.GetValue(target) as GameEntity;
        return entity != null && sim.Entities.TryGetObjectById(entity.Id, out GameEntity? mine) && ReferenceEquals(mine, entity);
    }

    // Farming and Ranching run on their own beside combat and use items from the same bank: left running,
    // their wine and seeds would show up as supplies of the fight.
    static void StopPassiveSkills(Game sim)
    {
        try
        {
            var passive = new HashSet<object>(sim.PassiveActionManager.PassiveActions.Select(p => (object)p.Entity), ReferenceEqualityComparer.Instance);
            TickingEntitiesOf(sim).RemoveAll(entity => passive.Contains(entity));
        }
        catch (MissingMethodException) { }   // a game update moved the list: the fight is still right, "Used" is noisier
    }

    static string Clock(long ticks) => SimText.Clock(ticks / (double)TicksPerSecond);
}
