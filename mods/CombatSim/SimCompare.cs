using System;
using System.Collections.Generic;
using System.Linq;

namespace CombatSim;

/// <summary>
/// One equipment setup as the left of the window describes it (SimState.LoadoutId, Overrides, SpellId and StanceId), with
/// the key and name of its loadout, changes and spell (BaseKey, BaseLabel) and the attack stances of its weapon, as they
/// were when it was last on the left. Its stance is added to them by the tab on show (SimState.KeyOf and NameOf): Fights
/// fights in the stance chosen, Best for tries every stance.
/// </summary>
sealed record SimSetup(string LoadoutId, IReadOnlyDictionary<string, string> Overrides, string SpellId, string BaseKey, string BaseLabel,
    IReadOnlyList<(string Id, string Name)> Stances)
{
    /// <summary>No second setup: compare mode was not switched on since the character was opened.</summary>
    public static readonly SimSetup None = new("", new Dictionary<string, string>(), "", "", "", Array.Empty<(string, string)>());

    /// <summary>The stance chosen in its Stance box (issue #51); "" is "As the game has it".</summary>
    public string StanceId { get; init; } = "";
    public string? DefaultStance { get; init; }
    public IReadOnlyList<string> Displaced { get; init; } = Array.Empty<string>();

    /// <summary>
    /// What its slots showed when it was last on the left, each item in its own slot (<see cref="SimState.ShownSlots"/>):
    /// the copy of every fight with this setup is checked against it (SimOptions.Shown, issue #46). Null: not checked.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Shown { get; init; }
#if DEVTOOLS
    /// <summary>Development: the same slots in words (SimGear.Describe), for SimEngine.DevGear.</summary>
    public string DevSlots { get; init; } = "";
#endif
}

/// <summary>
/// Compare mode (issue #2): two setups, A and B. One press of Simulate runs every fight with both, from the same
/// snapshot of the character, and the list has one line per fight with the two side by side (SimViewCompare.cs). The
/// left of the window shows one of them with the controls it always had, and they change the fields they always
/// changed (LoadoutId, Overrides, SpellId, StanceId); the other setup waits in <see cref="Other"/>. Like the setup itself,
/// none of it is saved: it lasts as long as the character is open.
/// </summary>
sealed partial class SimState
{
    /// <summary>The Compare box is ticked: the A and B buttons are on the left, and Simulate runs both.</summary>
    public bool Compare;
    /// <summary>The left shows setup B; otherwise A. It stays as it is when the box is unticked.</summary>
    public bool ShowingB;
    /// <summary>The setup the left does not show.</summary>
    public SimSetup Other = SimSetup.None;

    /// <summary>
    /// What the slots on the left show, each item in its own slot (GearSet.OwnItems): made by CombatSimMod.ReadGear with
    /// the slots, and replaced, never changed, when they are made again. Null while there are none.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ShownSlots;
    public IReadOnlyList<string> Displaced = Array.Empty<string>();
#if DEVTOOLS
    /// <summary>Development: the same in words (SimGear.Describe).</summary>
    public string DevSlots = "";
#endif

    /// <summary>The setup on the left, as it is now, with what its slots show.</summary>
    public SimSetup OnLeft => new(LoadoutId, new Dictionary<string, string>(Overrides), SpellId, GearKey(EquipmentCode), SetupLabel, Stances.ToList())
    {
        StanceId = StanceId,
        DefaultStance = DefaultStance,
        Shown = ShownSlots,
        Displaced = Displaced,
#if DEVTOOLS
        DevSlots = DevSlots,
#endif
    };
    public SimSetup A => ShowingB ? Other : OnLeft;
    public SimSetup B => ShowingB ? OnLeft : Other;
    /// <summary>"A" or "B": the setup on the left.</summary>
    public string LeftSide => ShowingB ? "B" : "A";
    /// <summary>A and B are the same setup: there is nothing to compare, and Simulate waits until one of them changes.</summary>
    public bool SameSetups => KeyOf(Other) == SetupKey;

    /// <summary>The stance a run of the tab on show fights this setup in: the one chosen in Fights, as <see cref="ChosenStance"/> is for the left.</summary>
    public (string Id, string Name)? ChosenOf(SimSetup setup) => ChosenOf(setup.StanceId, setup.Stances);
    /// <summary>The key of a setup's results in the tab on show: its loadout, changes and spell, and in Fights the stance chosen.</summary>
    public string KeyOf(SimSetup setup) => setup.BaseKey + StanceKey(ChosenOf(setup), setup.DefaultStance);
    /// <summary>A setup's name in the tab on show, as <see cref="SetupName"/> is the left's.</summary>
    public string NameOf(SimSetup setup) => SimText.WithStance(setup.BaseLabel, ChosenOf(setup)?.Name ?? "");

    /// <summary>The Compare box. The first time it is ticked the left is setup A, and B starts as a copy of it.</summary>
    public void SetCompare(bool on)
    {
        Compare = on;
        if (!on || Other.BaseKey != "") return;
        Other = OnLeft;
        ShowingB = false;
    }

    /// <summary>The A or B button: the setup on the left and the other one change places.</summary>
    public void SwapSides()
    {
        SimSetup left = OnLeft;
        PutOnLeft(Other);
        Other = left;
        ShowingB = !ShowingB;
    }

    /// <summary>Puts a setup on the left. The mod's BuildGear then reads its slots, stances and spells from the game.</summary>
    public void PutOnLeft(SimSetup setup)
    {
        LoadoutId = setup.LoadoutId;
        Overrides.Clear();
        foreach (var (slot, item) in setup.Overrides) Overrides[slot] = item;
        SpellId = setup.SpellId;
        StanceId = setup.StanceId;
    }

    /// <summary>Another character: one setup again.</summary>
    public void ForgetCompare()
    {
        Compare = ShowingB = false;
        Other = SimSetup.None;
    }

    /// <summary>Best mode tries every stance of this setup's weapon, when it has more than one.</summary>
    public bool PerStanceOf(SimSetup setup) => BestMode && setup.Stances.Count > 1;

    /// <summary>What a run simulates with this setup: in best mode every fight once per stance of its weapon.</summary>
    public List<SimTarget> ToSimulate(SimSetup setup) => !PerStanceOf(setup) ? InScope.ToList()
        : InScope.SelectMany(t => setup.Stances.Select(s => t with { StanceId = s.Id, StanceName = s.Name })).ToList();

    // ---- the lines of the list ------------------------------------------------------------------------------

    /// <summary>
    /// The list compares two setups: the box is ticked, or an earlier scan of two setups is on show. Not while the items
    /// tried for one fight are (SimUpgrades.cs): they are tried with the setup on the left.
    /// </summary>
    public bool ShownComparing => !ShowingUpgrades && (Viewing?.Compare ?? Compare);
    public string ShownLabelA => Viewing?.SetupLabel ?? NameOf(A);
    public string ShownLabelB => Viewing != null ? Viewing.SetupLabelB ?? "" : NameOf(B);

    // The two results of each fight on the list, by its FightKey, as Lines() found them last.
    readonly Dictionary<string, (SimResult? A, SimResult? B)> _pairs = new();

    /// <summary>A line's result with setup A and with setup B; either may be missing.</summary>
    public (SimResult? A, SimResult? B) Pair(SimResult line) => _pairs.GetValueOrDefault(line.Target.FightKey);

    /// <summary>The same for a selected line (<see cref="Selected"/>), whichever stance its key names.</summary>
    public (SimResult? A, SimResult? B) PairOf(string targetKey) => _pairs.GetValueOrDefault(FightOf(targetKey));

    /// <summary>The fight of a <see cref="SimTarget.Key"/>: the key without its stance.</summary>
    public static string FightOf(string targetKey) => targetKey.IndexOf('@') is var at and >= 0 ? targetKey[..at] : targetKey;

    /// <summary>
    /// The lines of the list in compare mode: one result per fight, A's, or B's where A has none; <see cref="Pair"/>
    /// gives both. In best mode each setup's best stance of the fight, as <see cref="Lines"/> picks it for one setup.
    /// None while A and B are the same setup: the list would set one setup's numbers beside themselves, and the words
    /// in its place say what to do (SimView.FillPairShowing).
    /// </summary>
    List<SimResult> PairLines()
    {
        _pairs.Clear();
        if (Viewing == null && SameSetups) return new List<SimResult>();
        IEnumerable<SimResult> pool;
        Func<SimResult, bool> ofA, ofB;
        if (Viewing is { } scan)
        {
            string a = scan.SetupKey, b = scan.SetupKeyB ?? "";
            pool = scan.Results;
            ofA = r => r.SetupKey == a;
            ofB = r => r.SetupKey == b;
        }
        else
        {
            SimSetup a = A, b = B;
            bool aStances = PerStanceOf(a), bStances = PerStanceOf(b);
            string keyA = KeyOf(a), keyB = KeyOf(b);
            var fights = InScope.Select(t => t.FightKey).ToHashSet();
            pool = Results.Values.Where(r => fights.Contains(r.Target.FightKey));
            // A fight tried per stance and the same fight in the stance of the moment are different runs.
            ofA = r => r.SetupKey == keyA && (r.Target.StanceId != null) == aStances;
            ofB = r => r.SetupKey == keyB && (r.Target.StanceId != null) == bStances;
        }
        SimResult? Pick(IEnumerable<SimResult> results) =>
            ShownBest ? results.OrderByDescending(r => r.Failed ? -1 : Xp(r)).FirstOrDefault() : results.FirstOrDefault();
        var lines = new List<SimResult>();
        foreach (var fight in pool.GroupBy(r => r.Target.FightKey))
        {
            SimResult? withA = Pick(fight.Where(ofA)), withB = Pick(fight.Where(ofB));
            if (withA == null && withB == null) continue;
            _pairs[fight.Key] = (withA, withB);
            lines.Add(withA ?? withB!);
        }
        return lines;
    }
}

public partial class CombatSimMod
{
    /// <summary>
    /// What one press of Simulate runs: every fight with the setup on the left; in compare mode with A and with B, fight
    /// by fight, so that a line has its second number soon after its first, all from the one snapshot SimEngine.Start
    /// takes. When A and B are the same setup (the window keeps Simulate off then) the setup runs once.
    /// </summary>
    List<SimJob> RunJobs()
    {
        if (!_state.Compare || _state.SameSetups)
        {
            SimOptions options = RunOptions();
#if DEVTOOLS
            DevSimulateOptions(options);   // raw= changes the setup on the left (CombatSimGear.cs)
#endif
            return _state.ToSimulate().Select(t => new SimJob(t, options)).ToList();
        }
        SimSetup a = _state.A, b = _state.B;
        SimOptions withA = RunOptions(a), withB = RunOptions(b);
#if DEVTOOLS
        DevSimulateOptions(_state.ShowingB ? withB : withA);   // before the copies with the dice of each fight are made
#endif
        return CompareJobs(a, b, withA, withB);
    }

    /// <summary>
    /// The dice both fights of an enemy have in Compare (issue #57): the key of the fight alone, without a setup and without
    /// a stance. A and B are kept in step with it all fight (SimOptions.Dice, SimDice.InStep, as Find upgrades fights its
    /// items), and so are the stances each tries in Best for: they meet the same rolls at the same kill for as long as they
    /// act alike, and the luck they met alike lowers the bar A against B is judged by (SimRanges.Apart). As it names no
    /// setup, swapping A and B changes nothing, and a setup comes out the same against any other. "compare" is no
    /// setup's key: every one holds a ";" (SimState.GearKey).
    /// </summary>
    internal static string CompareDice(string fightKey) => SimResult.MakeKey("compare", fightKey);

    /// <summary>
    /// A's and B's fights, fight by fight, each with the dice of its fight (<see cref="CompareDice"/>) and its side, A or B
    /// (SimOptions.Side). Every fight has options of its own for them, copies of A's and of B's.
    /// </summary>
    List<SimJob> CompareJobs(SimSetup a, SimSetup b, SimOptions withA, SimOptions withB)
    {
        ILookup<string, SimTarget> ofB = _state.ToSimulate(b).ToLookup(t => t.FightKey);
        var jobs = new List<SimJob>();
        foreach (var fight in _state.ToSimulate(a).GroupBy(t => t.FightKey))
        {
            string dice = CompareDice(fight.Key);
            SimOptions diceA = withA.WithDice(dice, "A"), diceB = withB.WithDice(dice, "B");
            jobs.AddRange(fight.Select(t => new SimJob(t, diceA)));
            jobs.AddRange(ofB[fight.Key].Select(t => new SimJob(t, diceB)));
        }
        return jobs;
    }

    /// <summary>
    /// The setup the left does not show, read again from the game when the window opens, as the left reads its own: a
    /// loadout renamed, put on or changed since, or gone (BuildGear then falls back on the one worn), gets the name, key
    /// and stances the left would show for it.
    /// </summary>
    void DescribeOther()
    {
        if (_state.Other.BaseKey == "") return;
        SimSetup left = _state.OnLeft;
        _state.PutOnLeft(_state.Other);
#if DEVTOOLS
        _devGearFor = $"setup {(_state.ShowingB ? "A" : "B")}, read again on open; it is not on the left";
#endif
        BuildGear();
#if DEVTOOLS
        _devGearFor = null;
#endif
        _state.Other = _state.OnLeft;
        _state.PutOnLeft(left);
        BuildGear();
    }

#if DEVTOOLS
    // What the next lines of the slots (CombatSimGear.DevLogGear) are about, when not the setup on the left.
    string? _devGearFor;
#endif

#if DEVTOOLS
    // Development, lines of sim.request (DevWindowCommand); put back with the rest at the end of the request:
    //   compare=<loadout>|<slot=item id>|<spell>|<side>
    //                                              ticks Compare, with setup A as the left has it, and makes setup B as
    //                                              the B button and the controls under it would: a copy of A (also when
    //                                              a B was made before), with that loadout (part of its name; empty: A's),
    //                                              that item swapped in (part of a slot's name = an item id; empty: none)
    //                                              and that spell (part of its name; empty: as the loadout has it). Then A
    //                                              is on the left again, or B with "b" as the fourth part (for a raw= scan
    //                                              of B's, or a stance= line that gives B another stance than A's).
    //   compare=off                                unticks Compare
    //   compare=twin                               the twin (issue #57): every fight where the simulator is set to fight,
    //                                              with the setup on the left as A and as B, B under another key
    //                                              (SimEngine.DevTwinKey), the jobs and their dice made by Compare's own
    //                                              CompareJobs. Run outside the window, as twice= is: nothing goes into
    //                                              the list or History, and the Compare box, A and B stay as they are.
    //                                              SimEngine.DevTwin logs every fight's two results side by side.
    //   compare=check|<what>|<fights>              reads A and B back before a run: Compare ticked, B different from A
    //                                              in exactly <what> (loadout, item, spell or stance; a stance chosen that
    //                                              is the one the game has is no difference) and <fights> fights for each
    //                                              setup. Otherwise one line, and the rest of the request is dropped.
    //   side=a, side=b                             a click on the A or the B button
    //   wear=on, wear=off                          a click on "Wear this setup first" when it is not so already: a setting
    //                                              of the mod, put back with the others, so that the words above Fight
    //                                              say what it would put on. Fight is not pressed.
    // Each logs both setups.
    void DevCompare(string what)
    {
        Open();
        if (_window is not { IsAlive: true } || _view == null) { _ctx.Log("Dev: compare: the simulator could not be opened."); return; }
        if (what.Equals("twin", StringComparison.OrdinalIgnoreCase)) { DevCompareTwin(); return; }
        if (what.StartsWith("check", StringComparison.OrdinalIgnoreCase)) { DevCompareCheck(what); return; }
        if (what.Equals("off", StringComparison.OrdinalIgnoreCase)) _state.SetCompare(false);
        else
        {
            string[] parts = what.Split('|', StringSplitOptions.TrimEntries);
            _state.SetCompare(true);
            // B starts as a copy of A, as the first tick of the box makes it, also after a B of the player's own.
            if (_state.ShowingB)
            {
                _state.SwapSides();
                BuildGear();
            }
            _state.Other = _state.OnLeft;
            _state.SwapSides();
            BuildGear();
            if (parts[0] != "" && _state.Loadouts.FirstOrDefault(l => l.Name.Contains(parts[0], StringComparison.OrdinalIgnoreCase)) is { Id: not null } loadout)
            {
                _state.LoadoutId = loadout.Id;
                _state.Overrides.Clear();
                BuildGear();
            }
            if (parts.Length > 1 && parts[1].Split('=') is [var slotName, var itemId] &&
                _state.Gear.FirstOrDefault(g => g.SlotName.Contains(slotName.Trim(), StringComparison.OrdinalIgnoreCase)) is { } slot)
            {
                _state.Overrides[slot.SlotId] = itemId.Trim();
                BuildGear();
            }
            _state.SpellId = parts.Length > 2 && parts[2] != "" ? _state.Spells.FirstOrDefault(s => s.Name.Contains(parts[2], StringComparison.OrdinalIgnoreCase))?.Id ?? "" : "";
            if (!(parts.Length > 3 && parts[3].Equals("b", StringComparison.OrdinalIgnoreCase)))
            {
                _state.SwapSides();
                BuildGear();
            }
        }
        Build();
        _window!.Visible = true;
        _window.SetMinimised(false);
        _ctx.Log("Dev: compare: " + DevCompareState());
    }

    void DevSide(string side)
    {
        bool b = side.Equals("b", StringComparison.OrdinalIgnoreCase);
        DevClick(_view?.DevSideButton(b), "setup " + (b ? "B" : "A"));
        // The click reaches the button on a later frame.
        _ctx.Tree.CreateTimer(0.3).Timeout += () => _ctx.Log($"Dev: compare: after a click on {(b ? "B" : "A")}: " + DevCompareState());
    }

    void DevTickWear(string to)
    {
        bool on = to.Equals("on", StringComparison.OrdinalIgnoreCase);
        if (_view == null) { _ctx.Log("Dev: compare: wear: the simulator is not open."); return; }
        if (_view.DevWearBox.ButtonPressed != on) DevClick(_view.DevWearBox, "Wear this setup first");
        _ctx.Tree.CreateTimer(0.3).Timeout += () => _ctx.Log($"Dev: compare: Wear this setup first is {(_state.WearSetup ? "ticked" : "not ticked")} " +
                                                             $"(asked {(on ? "on" : "off")}); the words above Fight: '{(_view?.DevFightNote.Text ?? "-")}'");
    }

    // "on, A on the left; A Warrior (key ...), stances 3, stance as the game has it; B Mage (key ...), stances 1, ...".
    string DevCompareState()
    {
        SimSetup a = _state.A, b = _state.B;
        string Setup(string name, SimSetup s) => $"{name} {_state.NameOf(s)} (key {_state.KeyOf(s)}), {s.Stances.Count} stances, " +
                                                 $"stance {(s.StanceId == "" ? "as the game has it" : s.StanceId)}{(s.StanceId != "" && _state.ChosenOf(s) == null ? " (not used in this tab)" : "")}, " +
                                                 $"{_state.ToSimulate(s).Count} fights";
        return (_state.Compare ? $"on, {_state.LeftSide} on the left" : $"off, {_state.LeftSide} on the left") +
               (_state.Other.BaseKey == "" ? "; no second setup" : $"; {Setup("A", a)}; {Setup("B", b)}{(_state.SameSetups ? "; A AND B ARE THE SAME SETUP" : "")}");
    }

    // compare=check|<what>|<fights>: what B differs from A in, read from the two setups as Simulate would run them, and how
    // many fights each has. The stance each fights in is its own: the one chosen for it, or the one the game has for its
    // weapon, which the first choice of the Stance box names when it can tell (SimState.StanceNow, read by BuildGear for
    // the setup on the left; for the other one with it on the left for a moment, as DescribeOther reads it), or else "as the
    // game has it, not read". The stance is a difference of its own only between two setups with the stances of one weapon:
    // a weapon of another kind brings its own. A request that is not what it means to be stops here.
    void DevCompareCheck(string what)
    {
        string[] parts = what.Split('|', StringSplitOptions.TrimEntries);
        string asked = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
        int fights = parts.Length > 2 && int.TryParse(parts[2], out int n) ? n : -1;
        SimSetup a = _state.A, b = _state.B;
        string? leftNow = _state.StanceNow?.Id, otherNow = null;
        if (_state.Other.BaseKey != "" && _state.ChosenOf(_state.Other) == null)
        {
            SimSetup left = _state.OnLeft;
            _devGearFor = $"setup {(_state.ShowingB ? "A" : "B")}, read for compare=check; it is not on the left";
            _state.PutOnLeft(_state.Other);
            BuildGear();
            otherNow = _state.StanceNow?.Id;
            _devGearFor = null;
            _state.PutOnLeft(left);
            BuildGear();
        }
        string Stance(SimSetup s, bool onLeft) =>
            _state.ChosenOf(s)?.Id ?? (onLeft ? leftNow : otherNow) ?? "as the game has it, not read";
        string stanceA = Stance(a, !_state.ShowingB), stanceB = Stance(b, _state.ShowingB);
        static string Changes(SimSetup s) => string.Join(" ", s.Overrides.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => $"{o.Key}={o.Value}"));
        var differ = new List<string>();
        if (a.LoadoutId != b.LoadoutId) differ.Add("loadout");
        if (Changes(a) != Changes(b)) differ.Add("item");
        if (a.SpellId != b.SpellId) differ.Add("spell");
        bool oneWeapon = a.Stances.Select(s => s.Id).SequenceEqual(b.Stances.Select(s => s.Id));
        if (oneWeapon && stanceA != stanceB) differ.Add("stance");
        int withA = _state.ToSimulate(a).Count, withB = _state.ToSimulate(b).Count;
        bool ok = _state.Compare && differ is [var only] && only == asked && withA == fights && withB == fights;
        _ctx.Log($"Dev: compare: check: Compare {(_state.Compare ? "ticked" : "NOT TICKED")}; A {_state.NameOf(a)} (loadout {a.LoadoutId}, changes {Changes(a).Or("none")}, " +
                 $"spell {a.SpellId.Or("as the loadout has it")}, stance {stanceA}), {withA} fights; B {_state.NameOf(b)} (loadout {b.LoadoutId}, changes {Changes(b).Or("none")}, " +
                 $"spell {b.SpellId.Or("as the loadout has it")}, stance {stanceB}), {withB} fights; {(oneWeapon ? "the stances of one weapon" : "the stances of other weapons")}; B differs from A in {(differ.Count == 0 ? "nothing" : string.Join(", ", differ))}, " +
                 $"asked: {asked.Or("-")}, {fights} fights each: {(ok ? "AS ASKED" : "NOT AS ASKED; the rest of the request is dropped")}");
        if (!ok) _devCommands.Clear();
    }

    // compare=twin: what Simulate never runs (SameSetups keeps it off), the same setup as A and as B. B is A under another
    // key, as Find upgrades' "again" check is the setup as it is under another (UpgradeSearch.DevAgainKey), so that both
    // have results of their own; the jobs and their dice are Compare's (CompareJobs). With the same dice, every fight must
    // come out with the very same numbers, and every verdict "the same". No _run: the results go neither into the list nor
    // into History (as twice=). SimEngine logs, once both fights of an enemy are over, their dice (DevPair) and whether every
    // number is the same (DevTwin), and once the game's thread has taken in both, the verdicts between them (DevVerdicts).
    void DevCompareTwin()
    {
        if (_game == null || _engine.Running) { _ctx.Log("Dev: compare: twin: no character, or a run is going on."); return; }
        SimSetup a = _state.OnLeft;
        SimOptions withA = RunOptions(a), withB = RunOptions(a);
        withB.SetupKey += SimEngine.DevTwinKey;
        withB.SetupLabel += ", twin";
        List<SimJob> jobs = CompareJobs(a, a, withA, withB);
        if (jobs.Count == 0) { _ctx.Log("Dev: compare: twin: no fight where the simulator is set to fight."); return; }
        if (!_engine.Start(_game, jobs, _state.Workers, out string error)) { _ctx.Log("Dev: compare: twin: " + error); return; }
        _ctx.Log($"Dev: compare: twin: {jobs.Count} fights, {withA.Hours:0.##} h each, {(withA.Sure > 0 ? $"or on until {SimRanges.Size(withA.Sure * 100)} sure" : "Go on until Time is up")}, " +
                 $"{(_state.BestMode ? "Best for" : "Fights")}, A {_state.NameOf(a)} (key {withA.SetupKey}) " +
                 $"and B the same setup under the key {withB.SetupKey}, {Math.Min(_state.Workers, jobs.Count)} at a time from one snapshot, outside the window.");
    }
#endif
}
