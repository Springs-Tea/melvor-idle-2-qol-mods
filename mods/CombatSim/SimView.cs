using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MelvorMods.Shared;

namespace CombatSim;

/// <summary>One equipment slot as the simulator will fight with it. Changed: it has a change of its own (SimState.Overrides).</summary>
sealed record GearSlot(string SlotId, string SlotName, int Column, int Row, string? ItemId, string ItemName, int Quantity,
    Texture2D? Icon, Texture2D? EmptyIcon, bool Changed)
{
    /// <summary>The id of the slot whose item fills this one too (the Weapon, for a two-handed weapon's Offhand); "" when the item is worn here.</summary>
    public string FilledFrom { get; init; } = "";
    /// <summary>That slot's name, for people.</summary>
    public string FilledFromName { get; init; } = "";
    /// <summary>The game's tint for the picture of such a slot (CombatSimMod.FilledLook); null when it is not known.</summary>
    public Color? FilledTint { get; init; }
}

/// <summary>An item that fits a slot.</summary>
sealed record GearChoice(string ItemId, string Name, Texture2D? Icon, int Owned, bool CanWear);

/// <summary>A spell the weapon of the setup can cast.</summary>
sealed record SpellChoice(string Id, string Name, string Book, Texture2D? Icon);

/// <summary>What the view shows and remembers; owned by the mod, so it survives a rebuild of the window.</summary>
sealed partial class SimState
{
    public List<SimTarget> Catalog = new();
    /// <summary>Every unlocked area and dungeon, in the game's order.</summary>
    public List<AreaChoice> Areas = new();
    /// <summary>Results by <see cref="SimResult.Key"/>: one per equipment setup and fight.</summary>
    public Dictionary<string, SimResult> Results = new();
    /// <summary>The areas chosen in "Where", one choice per mode; none chosen is every unlocked area.</summary>
    public HashSet<string> FightAreas = new(), BestAreas = new();
    public HashSet<string> ScopeAreas => BestMode ? BestAreas : FightAreas;
    /// <summary>Earlier scans of this character, newest first.</summary>
    public List<Scan> History = new();
    /// <summary>An earlier scan that is on show instead of the current results.</summary>
    public Scan? Viewing;
    public int Duration = 1;
    /// <summary>"Go on until": simulate each fight on until every range in the list is within this many percent; 0 is off.</summary>
    public double Sure;
    /// <summary>The sizes "Go on until" offers, in percent; 0 is off ("Time is up").</summary>
    public static readonly double[] SureSizes = { 0, 1, 2, 5, 10 };
    public bool Unlimited = true;
    /// <summary>The newest result of each fight on its enemy card in the game's combat areas menu.</summary>
    public bool ShowEnemyCards = true;
    /// <summary>Fights simulated side by side.</summary>
    public int Workers = 1;

    public List<(string Id, string Name)> Loadouts = new();
    public string LoadoutId = "";
    /// <summary>Equipment slot id -> item id to wear there instead of the loadout's ("" is nothing).</summary>
    public Dictionary<string, string> Overrides = new();
    public List<GearSlot> Gear = new();
    /// <summary>The attack stances of the weapon in <see cref="Gear"/>.</summary>
    public List<(string Id, string Name)> Stances = new();
    /// <summary>
    /// The stance chosen in the Stance box of Fights (issue #51), one of <see cref="Stances"/>; "" is "As the game has it",
    /// the one the game has selected for the weapon. Best for tries every stance and does not use it.
    /// </summary>
    public string StanceId = "";
    /// <summary>
    /// The stance "As the game has it" stands for, when the game can tell: the one it has selected now for the weapon of
    /// the setup, which is the loadout worn and a weapon of the kind worn. Null otherwise.
    /// </summary>
    public (string Id, string Name)? StanceNow;
    /// <summary>The spells that weapon can cast; empty when it casts none.</summary>
    public List<SpellChoice> Spells = new();
    /// <summary>The spell to fight with; "" is the one the loadout has selected.</summary>
    public string SpellId = "";
    /// <summary>What the "" choice stands for, in words.</summary>
    public string SpellDefault = "";

    /// <summary>
    /// False: what the fights of the chosen areas give. True: look for the best place to train, by trying
    /// every one of those fights in every stance and ranking them by the XP of <see cref="TrainSkillId"/>.
    /// </summary>
    public bool BestMode;
    /// <summary>The skill to rank by in best mode; "" is all combat XP together.</summary>
    public string TrainSkillId = "";
    public List<(string Id, string Name)> Skills = new();

    /// <summary>Key of the selected fight (a <see cref="SimTarget.Key"/>).</summary>
    public string? Selected;
    /// <summary>The player clicked a row: until then the details follow whatever is on top.</summary>
    public bool Pinned;
    /// <summary>The column the list is sorted by, by its key (<see cref="SimColumn.Key"/>), "name" for the enemy.</summary>
    public string SortKey = "xp";
    public bool SortDescending = true;
    /// <summary>The keys of the columns the list shows, from left to right; saved in settings.json.</summary>
    public List<string> Columns = SimColumns.Default.ToList();
    /// <summary>The groups the player folded in the columns picker, for as long as the game runs.</summary>
    public HashSet<string> FoldedColumnGroups = new();
    public string FoodLine = "";
    public bool FoodMissing;
    /// <summary>The worn loadout's food in the bank, as of opening the window; -1 when it is not known.</summary>
    public long FoodInBank = -1;
    public string FoodName = "", FoodLoadoutId = "";
    public bool Running;
    public int Done, Total;
    public const string ErrorText = "Error";
    public string CountText(string done) => $"{done} of {Total}";
    public string FightCount => CountText(Done.ToString());
    public string DoneText => $"Done: {Total} {(Total == 1 ? "fight" : "fights")}";
    public string ScanStatus => Message != "" ? ErrorText : Total == 0 ? "" : Running ? FightCount
        : Done == Total ? DoneText : "Stopped: " + FightCount;
    public string Current = "", Message = "";

    public static readonly (string Label, double Hours)[] Durations =
        { ("15 minutes", 0.25), ("1 hour", 1), ("4 hours", 4), ("12 hours", 12) };

    public IEnumerable<SimTarget> InScope => ScopeAreas.Count == 0 ? Catalog : Catalog.Where(t => ScopeAreas.Contains(t.AreaId));

    /// <summary>The chosen areas in words: one or two by name, more as a number.</summary>
    public string ScopeLabel
    {
        get
        {
            var names = Areas.Where(a => ScopeAreas.Contains(a.AreaId)).Select(a => a.Name).ToList();
            return names.Count switch
            {
                0 => "Every unlocked area",
                1 => names[0],
                2 => names[0] + ", " + names[1],
                _ => $"{names.Count} areas",
            };
        }
    }

    /// <summary>Best mode tries every stance of the weapon, when it has more than one.</summary>
    public bool PerStance => BestMode && Stances.Count > 1;

    /// <summary>What a run simulates: in best mode every fight once per stance.</summary>
    public List<SimTarget> ToSimulate()
    {
        if (!PerStance) return InScope.ToList();
        return InScope.SelectMany(t => Stances.Select(s => t with { StanceId = s.Id, StanceName = s.Name })).ToList();
    }

    // What is on show: the current choices, or those of the earlier scan that is being looked at.
    public bool ShownBest => Viewing?.BestMode ?? BestMode;
    public string ShownSkillId => Viewing != null ? Viewing.TrainSkillId : TrainSkillId;
    public string ShownSkillName => Viewing != null ? Viewing.TrainSkillName : Skills.FirstOrDefault(k => k.Id == TrainSkillId).Name ?? "";
    public string ShownSetupKey => Viewing?.SetupKey ?? SetupKey;
    public string ShownSetupLabel => Viewing?.SetupLabel ?? SetupLabel;
    public IReadOnlyDictionary<string, SimResult> ShownResults => Viewing != null ? Viewing.ByKey : Results;

    /// <summary>XP per hour as the table ranks it: of the trained skill in best mode, all combat XP otherwise.</summary>
    public double Xp(SimResult r) => ShownBest && ShownSkillId != "" ? r.PerHour(r.Xp.GetValueOrDefault(ShownSkillId)) : r.XpPerHour;
    /// <summary>The rate <see cref="Xp"/> is, for its range.</summary>
    public SimRate XpRate => SimRate.PerHour(ShownBest && ShownSkillId != "" ? SimRanges.XpOf(ShownSkillId) : SimRanges.Xp);

    /// <summary>The table's lines: one per fight; in best mode, the stance that trains the skill best.</summary>
    public List<SimResult> Lines()
    {
        if (ShowingUpgrades) return UpgradeLines();   // the items tried for one fight (SimUpgrades.cs)
        if (ShownComparing) return PairLines();   // two setups (SimCompare.cs)
        IEnumerable<SimResult> pool;
        if (Viewing != null) pool = Viewing.Results;
        else
        {
            var fights = InScope.Select(t => t.FightKey).ToHashSet();
            string setup = SetupKey;
            // A fight tried per stance and the same fight in the stance of the moment are different runs.
            bool perStance = PerStance;
            pool = Results.Values.Where(r => r.SetupKey == setup && fights.Contains(r.Target.FightKey) && (r.Target.StanceId != null) == perStance);
        }
        if (!ShownBest) return pool.ToList();
        return pool.GroupBy(r => r.Target.FightKey).Select(g => g.OrderByDescending(r => r.Failed ? -1 : Xp(r)).First()).ToList();
    }

    public string SetupKey => KeyWith(Overrides);
    /// <summary>The key of this loadout, spell and stance with these changes: a pick in a slot's picker (Find upgrades tries them).</summary>
    public string KeyWith(IReadOnlyDictionary<string, string> overrides) => GearKey(overrides) + StanceKey(ChosenStance);
    // Without the stance: what SimSetup.BaseKey keeps, the tab on show adding the stance (KeyOf).
    string GearKey(IReadOnlyDictionary<string, string> overrides) =>
        LoadoutId + ";" + string.Join(",", overrides.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => o.Key + "=" + o.Value))
        + (SpellId == "" ? "" : ";spell=" + SpellId);
    // A stance chosen makes a setup of its own, as a spell does; "As the game has it" keeps the key it always had.
    static string StanceKey((string Id, string Name)? stance) => stance is { } chosen ? ";stance=" + chosen.Id : "";

    /// <summary>
    /// The stance a run of Fights fights the setup on the left in (issue #51): the one chosen in its Stance box. Null with
    /// "As the game has it", and in Best for, which tries every stance.
    /// </summary>
    public (string Id, string Name)? ChosenStance => ChosenOf(StanceId, Stances);

    (string Id, string Name)? ChosenOf(string stanceId, IEnumerable<(string Id, string Name)> stances) =>
        BestMode || stanceId == "" ? null : stances.FirstOrDefault(s => s.Id == stanceId) is { Id: not null } stance ? stance : null;

    /// <summary>The setup on the left, named with its stance when one is chosen: "Loadout 1, Defensive stance".</summary>
    public string SetupName => SimText.WithStance(SetupLabel, ChosenStance?.Name ?? "");
    public string SetupLabel
    {
        get
        {
            string name = Loadouts.FirstOrDefault(l => l.Id == LoadoutId).Name ?? "Current equipment";
            if (Overrides.Count > 0) name += $" + {Overrides.Count} {(Overrides.Count == 1 ? "change" : "changes")}";
            return SpellId == "" ? name : name + ", " + (Spells.FirstOrDefault(s => s.Id == SpellId)?.Name ?? "another spell");
        }
    }

    /// <summary>The equipment simulated is what the character has on, item for item.</summary>
    public bool IsWornSetup => Overrides.Count == 0 && LoadoutId != "" && LoadoutId == WornLoadoutId;
    /// <summary>The loadout the character has on, as of the last time the window was opened.</summary>
    public string WornLoadoutId = "";

    /// <summary>"Wear this setup first" above the Fight button (FightSetup.cs): off until the player ticks it.</summary>
    public bool WearSetup;
    /// <summary>What the last Fight with that box ticked really put on, for the fight and setup of FightReportKey.</summary>
    public string FightReport = "", FightReportKey = "";
}

/// <summary>Pictures, names and choices from the running game, looked up on the game's thread.</summary>
interface ISimLookup
{
    Texture2D? TargetIcon(SimTarget target);
    Texture2D? AreaIcon(string areaId);
    Texture2D? AmountIcon(Amount amount);
    /// <summary>The game's own picture of an attack stance, by its id; null when the game has none.</summary>
    Texture2D? StanceIcon(string stanceId);
    (string Name, Texture2D? Icon) Skill(string id);
    /// <summary>Every item of the game that fits the slot.</summary>
    List<GearChoice> ChoicesFor(GearSlot slot);
    /// <summary>What Fight would put on first with "Wear this setup first" ticked (FightSetup.cs).</summary>
    WearForecast ForecastWear(SimTarget target);
    /// <summary>A click in the slot's item picker: back to the loadout's item (null), nothing (""), or an item, by the game's rules.</summary>
    void Pick(GearSlot slot, string? itemId);
}

/// <summary>
/// The simulator's window: what to simulate and with which equipment on the left, one line per fight in the
/// middle (click a column to sort, a line for its details), everything about the selected fight on the right.
/// All numbers are per hour of game time.
/// </summary>
sealed partial class SimView
{
    internal static readonly Color Bad = new("ff4500"), Good = new("5fd35f");

    // The columns of the list (which numbers, in which order, sorted how) are in SimViewColumns.cs.

    readonly GameWindow _window;
    readonly GameStyle _s;
    readonly SimState _state;
    readonly ISimLookup _lookup;
    readonly Action _simulate, _cancel, _changed, _gearChanged, _rebuild, _clearHistory;
    readonly Action<SimTarget> _fight;
    Button _fightButton = null!, _modeFights = null!, _modeBest = null!;
    Label _fightNote = null!;
    // The selected line's fight, and the same as Fight starts it: with the stance chosen for its result (FightAs).
    SimTarget? _fightTarget, _fightAs;

    OptionButton _duration = null!, _loadout = null!;
    OptionButton? _train, _spell;
    Button _where = null!, _unlimited = null!, _fromBank = null!, _go = null!, _reset = null!, _historyButton = null!, _back = null!;
    Label _food = null!, _supplyHint = null!, _estimate = null!, _status = null!, _empty = null!, _showing = null!;
    ProgressBar _progress = null!;
    VBoxContainer _rows = null!, _details = null!, _emptyBox = null!, _recent = null!;
    Control _gearHolder = null!, _spellHolder = null!;
    PanelContainer? _picker;
    string _pickerKind = "";
    readonly List<Button> _headers = new();
    string _tableSignature = "", _detailsSignature = "", _recentSignature = "";

    public SimView(GameWindow window, SimState state, ISimLookup lookup, Action simulate, Action cancel, Action changed, Action gearChanged, Action rebuild,
        Action<SimTarget> fight, Action clearHistory)
    {
        _fight = fight;
        _clearHistory = clearHistory;
        _window = window; _s = window.Style; _state = state; _lookup = lookup;
        _simulate = simulate; _cancel = cancel; _changed = changed; _gearChanged = gearChanged; _rebuild = rebuild;

        var columns = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass, CustomMinimumSize = new Vector2(0, _s.Px(640)) }, 10);
        columns.AddChild(BuildSetup());
        columns.AddChild(BuildResults());
        columns.AddChild(BuildDetails());
        window.Body.AddChild(columns);
        window.Moved += _ => ClosePicker();   // the picker does not travel with the window
        Refresh();
    }

    // ---- left: what to simulate, and with what -------------------------------------------------------------

    const float SetupWidth = 244;

    Control BuildSetup()
    {
        Control column = _s.Column("Setup", out VBoxContainer body, SetupWidth);

        // Two questions the window answers: "what does this area give me" and "where should I train".
        var modes = _s.Spaced(new HBoxContainer(), 4);
        var modeGroup = new ButtonGroup();
        Button fights = Toggle("Fights", modeGroup, "What the fights of the areas you choose give, use and risk");
        Button best = Toggle("Best for", modeGroup, "Find the best place to train a skill: every fight of the areas you choose is tried in every attack stance of your weapon");
        fights.ButtonPressed = !_state.BestMode;
        best.ButtonPressed = _state.BestMode;
        fights.Pressed += () => { if (_state.BestMode) { _state.BestMode = false; _state.Selected = null; _state.Viewing = null; _changed(); _rebuild(); } };
        best.Pressed += () => { if (!_state.BestMode) { _state.BestMode = true; _state.Selected = null; _state.Viewing = null; _changed(); _rebuild(); } };
        modes.AddChild(_modeFights = fights);
        modes.AddChild(_modeBest = best);
        body.AddChild(modes);

        if (_state.BestMode)
        {
            body.AddChild(Caption("Train"));
            _train = _s.Dropdown();
            _train.ClipText = true;
            _train.TooltipText = "Fights are ranked by the experience they give in this skill";
            _train.AddItem("All combat XP");
            foreach (var (_, name) in _state.Skills) _train.AddItem(name);
            _train.Selected = Math.Max(0, _state.Skills.FindIndex(k => k.Id == _state.TrainSkillId) + 1);
            _train.ItemSelected += index =>
            {
                _state.TrainSkillId = index == 0 ? "" : _state.Skills[(int)index - 1].Id;
                ShowCurrent();
                _changed();
                Refresh();
            };
            body.AddChild(_train);
        }

        body.AddChild(Caption("Where"));
        _where = WhereButton();
        _where.Pressed += OpenAreas;
        body.AddChild(_where);

        // Spaced like the rows of two buttons above and below it, so its two drop-downs line up with them.
        var timing = _s.Spaced(new HBoxContainer(), 4);
        var left = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 6);
        left.AddChild(Caption("Each fight lasts"));
        _duration = _s.Dropdown();
        _duration.TooltipText = "Game time, not waiting time: an hour of fighting takes well under a second";
        foreach (var (label, _) in SimState.Durations) _duration.AddItem(label);
        _duration.Selected = Math.Clamp(_state.Duration, 0, SimState.Durations.Length - 1);
        _duration.ItemSelected += index => { _state.Duration = (int)index; _changed(); Refresh(); };
        left.AddChild(_duration);
        timing.AddChild(left);
        timing.AddChild(SureBox());
        body.AddChild(timing);

        body.AddChild(Caption("Supplies"));
        var supplies = _s.Spaced(new HBoxContainer(), 4);
        var group = new ButtonGroup();
        _unlimited = Toggle("Unlimited", group, "Food, ammo, runes and prayer points never run out: the rates of a fight that is kept supplied");
        _fromBank = Toggle("From bank", group, "Uses what you have right now: the fight ends when something runs out");
        _unlimited.ButtonPressed = _state.Unlimited;
        _fromBank.ButtonPressed = !_state.Unlimited;
        _unlimited.Pressed += () => { _state.Unlimited = true; _changed(); Refresh(); };
        _fromBank.Pressed += () => { _state.Unlimited = false; _changed(); Refresh(); };
        supplies.AddChild(_unlimited);
        supplies.AddChild(_fromBank);
        body.AddChild(supplies);
        _supplyHint = Wrapped("", SetupWidth - 20, GameStyle.TextGrey, 12);
        body.AddChild(_supplyHint);

        body.AddChild(EquipmentHeading());   // with the Compare box (SimViewCompare.cs)
        _loadout = _s.Dropdown();
        _loadout.TooltipText = "Fight with one of your saved loadouts. Simulate two of them to compare, or tick Compare to run two at once.";
        _loadout.ClipText = true;
        foreach (var (_, name) in _state.Loadouts) _loadout.AddItem(name);
        _loadout.Selected = Math.Max(0, _state.Loadouts.FindIndex(l => l.Id == _state.LoadoutId));
        _loadout.ItemSelected += index =>
        {
            _state.LoadoutId = _state.Loadouts[(int)index].Id;
            _state.Overrides.Clear();   // the changes were made to the other loadout's items
            ShowCurrent();
            ClosePicker();
            _gearChanged();
            FillGear();
            Refresh();
        };
        body.AddChild(LoadoutRow(_loadout));   // in compare mode after the A and B buttons

        _gearHolder = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        body.AddChild(_gearHolder);
        // In Fights, the stance to fight in over the spell, both of the weapon in the slots (SimViewStance.cs).
        _stanceHolder = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        body.AddChild(_stanceHolder);
        _spellHolder = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        body.AddChild(_spellHolder);
        _reset = _s.GameButton("Undo changes", "TexturedButton4x", 12);
        _reset.TooltipText = "Back to the loadout's own items";
        _reset.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        _reset.Pressed += () => { _state.Overrides.Clear(); ShowCurrent(); ClosePicker(); _gearChanged(); FillGear(); Refresh(); };
        body.AddChild(_reset);
        _food = Wrapped("", SetupWidth - 20, GameStyle.TextGrey, 12);
        _food.HorizontalAlignment = HorizontalAlignment.Center;
        body.AddChild(_food);
        FillGear();

        body.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });

        _estimate = Wrapped("", SetupWidth - 20, GameStyle.TextGrey, 12);
        _estimate.HorizontalAlignment = HorizontalAlignment.Center;
        body.AddChild(_estimate);
        _go = _s.GameButton("Simulate", "TexturedActionButtonGreen8x", 18);
        _go.CustomMinimumSize = new Vector2(0, _s.Px(44));
        _go.Pressed += () => { ClosePicker(); if (_state.Running) _cancel(); else { ShowCurrent(); _simulate(); } };
        body.AddChild(_go);
        _progress = new ProgressBar { ShowPercentage = false, MinValue = 0, MaxValue = 1, Step = 0, CustomMinimumSize = new Vector2(0, _s.Px(6)), MouseFilter = Control.MouseFilterEnum.Ignore };
        _progress.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color("41454c") });
        _progress.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = new Color("cd964f") });
        body.AddChild(_progress);
        _status = Wrapped("", SetupWidth - 20, GameStyle.TextGrey, 12);
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        _status.CustomMinimumSize = new Vector2(_s.Px(SetupWidth - 20), _s.Px(18));
        body.AddChild(_status);
        return column;
    }

    // The slots sit where the game's own equipment panel has them. A changed slot has a gold frame.
    void FillGear()
    {
        foreach (Node old in _gearHolder.GetChildren()) old.QueueFree();
        _reset.Visible = _state.Overrides.Count > 0;
        _food.Text = _state.FoodLine;
        _food.AddThemeColorOverride("font_color", _state.FoodMissing ? Bad : GameStyle.TextGrey);
        FillChoices();   // the Stance and the Spell box (SimViewStance.cs)
        if (_state.Gear.Count == 0) return;

        int minColumn = _state.Gear.Min(g => g.Column), minRow = _state.Gear.Min(g => g.Row);
        int columns = _state.Gear.Max(g => g.Column) - minColumn + 1, rows = _state.Gear.Max(g => g.Row) - minRow + 1;
        var grid = new GridContainer { Columns = columns };
        grid.AddThemeConstantOverride("h_separation", _s.Px(4));
        grid.AddThemeConstantOverride("v_separation", _s.Px(4));
        int size = _s.Px(44);
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                GearSlot? slot = _state.Gear.FirstOrDefault(g => g.Column - minColumn == column && g.Row - minRow == row);
                if (slot == null) { grid.AddChild(new Control { CustomMinimumSize = new Vector2(size, size), MouseFilter = Control.MouseFilterEnum.Ignore }); continue; }
                grid.AddChild(BuildSlot(slot, size));
            }
        _gearHolder.AddChild(grid);
    }

    // Only there when the weapon of the setup casts spells: which one to fight with.
    void FillSpells()
    {
        foreach (Node old in _spellHolder.GetChildren()) old.QueueFree();
        _spell = null;
        _spellCaption = null;
        _spellHolder.Visible = _state.Spells.Count > 0;
        if (_state.Spells.Count == 0) return;

        var row = _s.Spaced(new HBoxContainer(), 8);
        row.AddChild(_spellCaption = Caption("Spell"));
        OptionButton spell = _spell = _s.Dropdown();
        spell.ClipText = true;
        spell.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        spell.TooltipText = "The spell to fight with. Simulate two of them to compare, or tick Compare to run two at once.";
        spell.AddThemeConstantOverride("icon_max_width", _s.Px(16));
        spell.GetPopup().AddThemeConstantOverride("icon_max_width", _s.Px(20));
        spell.AddItem(_state.SpellDefault);
        spell.SetItemMetadata(0, "");
        string book = "";
        foreach (SpellChoice choice in _state.Spells)
        {
            if (choice.Book != book) { book = choice.Book; spell.AddSeparator(book); }
            if (choice.Icon != null) spell.AddIconItem(choice.Icon, choice.Name); else spell.AddItem(choice.Name);
            spell.SetItemMetadata(spell.ItemCount - 1, choice.Id);
        }
        for (int i = 0; i < spell.ItemCount; i++)
            if (!spell.IsItemSeparator(i) && spell.GetItemMetadata(i).AsString() == _state.SpellId) spell.Selected = i;
        spell.ItemSelected += index =>
        {
            _state.SpellId = spell.GetItemMetadata((int)index).AsString();
            ShowCurrent();
            Refresh();
        };
        spell.Disabled = _state.Running;
        row.AddChild(spell);
        _spellHolder.AddChild(row);
    }

    internal static readonly Color EmptyTint = new(1, 1, 1, 0.22f);

    Control BuildSlot(GearSlot slot, int size)
    {
        var button = new Button
        {
            CustomMinimumSize = new Vector2(size, size), FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            TooltipText = $"{slot.SlotName}: {(slot.ItemId == null ? "empty" : slot.ItemName)}{(slot.Changed ? " (changed)" : "")}\nClick to try another item",
        };
        // The game's item frame: dark, thin warm border; gold when the slot was changed.
        foreach (string state in new[] { "normal", "hover", "pressed", "disabled", "focus" })
        {
            var frame = new StyleBoxFlat
            {
                BgColor = state == "hover" ? new Color("39302d") : new Color("15110f"),
                BorderColor = slot.Changed ? GameStyle.Highlight : state == "hover" ? new Color("8d8c86") : new Color("524943"),
            };
            frame.SetBorderWidthAll(slot.Changed ? 2 : 1);
            frame.SetCornerRadiusAll(2);
            button.AddThemeStyleboxOverride(state, frame);
        }
        // A button does not arrange its children: the picture is placed by hand, in the middle.
        int inner = _s.Px(34);
        TextureRect picture = _s.Picture(slot.Icon ?? slot.EmptyIcon, 34);
        picture.Size = new Vector2(inner, inner);
        picture.Position = new Vector2((size - inner) / 2, (size - inner) / 2);
        if (slot.Icon == null) picture.Modulate = EmptyTint;
        // Filled by another slot's item (a two-handed weapon's Offhand): its picture, tinted as the game's equipment
        // screen tints it, or dimmed as an empty slot when the game's tint is not known.
        else if (slot.FilledFrom != "") picture.Modulate = slot.FilledTint ?? EmptyTint;
        button.AddChild(picture);
        if (slot.Quantity > 1)
        {
            Label quantity = _s.Text(Fmt(slot.Quantity), GameStyle.TextWhite, 10, GameStyle.Bold);
            quantity.MouseFilter = Control.MouseFilterEnum.Ignore;
            quantity.Position = new Vector2(_s.Px(3), size - _s.Px(15));
            quantity.AddThemeColorOverride("font_outline_color", Colors.Black);
            quantity.AddThemeConstantOverride("outline_size", _s.Px(3));
            button.AddChild(quantity);
        }
        button.Pressed += () => OpenPicker(slot, button);
        return button;
    }

    // ---- the floating panels ---------------------------------------------------------------------------

    // One small panel at a time floats next to the window: the items of a slot, the areas, the earlier scans.
    void ClosePicker()
    {
        if (_picker != null && GodotObject.IsInstanceValid(_picker)) _picker.QueueFree();
        _picker = null;
        _pickerKind = "";
    }

    /// <summary>Starts a floating panel with a title and a close button; returns the column to fill.</summary>
    VBoxContainer StartPicker(string kind, string title, float width, float height)
    {
        ClosePicker();
        _pickerKind = kind;
        var panel = _picker = new PanelContainer { CustomMinimumSize = new Vector2(_s.Px(width), _s.Px(height)), ZIndex = 10 };
        panel.AddThemeStyleboxOverride("panel", _s.WindowPanel());
        var column = _s.Spaced(new VBoxContainer(), 6);
        panel.AddChild(column);

        var head = _s.Spaced(new HBoxContainer(), 6);
        Label name = _s.Text(title, GameStyle.TextWhite, 16, GameStyle.Bold);
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        head.AddChild(name);
        Button close = _s.GameButton("X", "TexturedButtonCancel2x", 14);
        close.CustomMinimumSize = new Vector2(_s.Px(24), _s.Px(24));
        close.Pressed += ClosePicker;
        head.AddChild(close);
        column.AddChild(head);
        return column;
    }

    /// <summary>Shows the panel next to (or under) what opened it, kept on the screen.</summary>
    void PlacePicker(Control anchor, float width, float height, bool below = false)
    {
        if (_picker is not { } panel) return;
        _window.Layer.AddChild(panel);
        Vector2 screen = panel.GetViewportRect().Size, size = new(_s.Px(width), _s.Px(height));
        Vector2 at = below
            ? anchor.GlobalPosition + new Vector2(anchor.Size.X - size.X, anchor.Size.Y + _s.Px(6))
            : anchor.GlobalPosition + new Vector2(anchor.Size.X + _s.Px(10), -_s.Px(60));
        panel.Position = new Vector2(Math.Clamp(at.X, 0, Math.Max(0, screen.X - size.X)), Math.Clamp(at.Y, 0, Math.Max(0, screen.Y - size.Y)));
    }

    // ---- where to fight: any number of areas ---------------------------------------------------------------

    // Looks like the drop-downs next to it, but opens a list in which several areas can be ticked.
    Button WhereButton()
    {
        var button = new Button
        {
            ThemeTypeVariation = "OptionButton", ClipText = true, Alignment = HorizontalAlignment.Left,
            IconAlignment = HorizontalAlignment.Right, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            TooltipText = "Choose one area, several, or all of them. An area simulates every enemy in it; a dungeon is simulated as a whole.",
        };
        if (GameStyle.Load<Theme>("res://Assets/Styles/Themes/custom_dropdown_1.tres") is { } theme)
        {
            button.Theme = theme;
            if (theme.HasIcon("arrow", "OptionButton")) button.Icon = theme.GetIcon("arrow", "OptionButton");
        }
        if (GameStyle.SemiBold is { } font) button.AddThemeFontOverride("font", font);
        button.AddThemeFontSizeOverride("font_size", _s.Px(12));
        return button;
    }

    void OpenAreas()
    {
        bool wasOpen = _pickerKind == "areas";
        ClosePicker();
        if (wasOpen || _state.Running) return;   // the button is a switch
        const float width = 350, height = 500;
        VBoxContainer column = StartPicker("areas", "Where to fight", width, height);
        HashSet<string> chosen = _state.ScopeAreas;

        var search = new LineEdit { PlaceholderText = "Search areas...", ClearButtonEnabled = true };
        search.AddThemeFontSizeOverride("font_size", _s.Px(14));
        column.AddChild(search);

        Button everything = Toggle("Every unlocked area", null, "Nothing ticked means everything: new areas are included as you unlock them");
        column.AddChild(everything);

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var list = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 2);
        scroll.AddChild(list);
        column.AddChild(scroll);
        Label none = _s.Text("No area matches.", GameStyle.TextGrey, 13);
        none.HorizontalAlignment = HorizontalAlignment.Center;
        none.Visible = false;
        column.AddChild(none);

        var foot = _s.Spaced(new HBoxContainer(), 8);
        Label summary = _s.Text("", GameStyle.TextGrey, 12);
        summary.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        summary.ClipText = true;
        foot.AddChild(summary);
        Button done = _s.GameButton("Done", "TexturedButton4x", 13);
        done.Pressed += ClosePicker;
        foot.AddChild(done);
        column.AddChild(foot);

        var rows = new List<(Button Row, AreaChoice Area)>();
        var headers = new List<(Button Header, string Category)>();

        void Sync()
        {
            foreach (var (row, area) in rows) row.SetPressedNoSignal(chosen.Contains(area.AreaId));
            everything.SetPressedNoSignal(chosen.Count == 0);
            int fights = _state.Areas.Where(a => chosen.Count == 0 || chosen.Contains(a.AreaId)).Sum(a => a.Fights);
            summary.Text = chosen.Count == 0 ? $"Everything  ·  {fights} fights"
                : $"{chosen.Count} {(chosen.Count == 1 ? "area" : "areas")}  ·  {fights} {(fights == 1 ? "fight" : "fights")}";
        }

        void Changed()
        {
            Sync();
            ShowCurrent();
            _changed();
            Refresh();
        }

        everything.Pressed += () => { chosen.Clear(); Changed(); };

        foreach (var category in _state.Areas.GroupBy(a => a.CategoryName))
        {
            // The heading ticks or clears its whole category.
            var header = new Button
            {
                Text = category.Key, Flat = true, Alignment = HorizontalAlignment.Left, FocusMode = Control.FocusModeEnum.None,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                TooltipText = $"Tick or clear all of {category.Key}",
            };
            header.AddThemeFontSizeOverride("font_size", _s.Px(12));
            if (GameStyle.SemiBold is { } font) header.AddThemeFontOverride("font", font);
            foreach (string name in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
                header.AddThemeColorOverride(name, name == "font_hover_color" ? GameStyle.TextWhite : GameStyle.TextGold);
            var members = category.Select(a => a.AreaId).ToList();
            header.Pressed += () =>
            {
                if (members.All(chosen.Contains)) foreach (string id in members) chosen.Remove(id);
                else foreach (string id in members) chosen.Add(id);
                Changed();
            };
            list.AddChild(header);
            headers.Add((header, category.Key));

            foreach (AreaChoice area in category)
            {
                string note = area.IsDungeon ? "Dungeon"
                    : $"{area.Fights} {(area.Fights == 1 ? "fight" : "fights")}  ·  Lv {(area.MinLevel == area.MaxLevel ? area.MinLevel.ToString() : area.MinLevel + "-" + area.MaxLevel)}";
                Button row = PickerRow(area.Name, _lookup.AreaIcon(area.AreaId), note, GameStyle.TextWhite, null);
                row.ToggleMode = true;
                row.Toggled += on =>
                {
                    if (on) chosen.Add(area.AreaId); else chosen.Remove(area.AreaId);
                    Changed();
                };
                list.AddChild(row);
                rows.Add((row, area));
            }
        }

        search.TextChanged += _ =>
        {
            string text = search.Text.Trim();
            foreach (var (row, area) in rows)
                row.Visible = text == "" || area.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || area.CategoryName.Contains(text, StringComparison.OrdinalIgnoreCase);
            foreach (var (header, category) in headers) header.Visible = rows.Any(r => r.Area.CategoryName == category && r.Row.Visible);
            none.Visible = rows.All(r => !r.Row.Visible);
        };

        Sync();
        PlacePicker(_where, width, height);
        search.GrabFocus();
    }

    // ---- earlier scans --------------------------------------------------------------------------------------

    /// <summary>Back from an earlier scan to the current results.</summary>
    void ShowCurrent()
    {
        if (LeaveUpgrades()) return;   // from the items tried for a fight: back to the fights, that one selected (SimViewUpgrades.cs)
        _state.Selected = null;
        _state.Pinned = false;
        _tableSignature = "";
        if (_state.Viewing == null) return;
        _state.Viewing = null;
        _detailsSignature = "";
    }

    void Show(Scan scan)
    {
        _state.Viewing = scan;
        _state.Selected = null;
        _state.Pinned = false;
        // An earlier scan is ranked the way it was asked: best first.
        _state.SortKey = "xp";
        _state.SortDescending = true;
        _tableSignature = _detailsSignature = "";
        ClosePicker();
        Refresh();
    }

    void OpenHistory()
    {
        bool wasOpen = _pickerKind == "history";
        ClosePicker();
        if (wasOpen) return;
        const float width = 400;
        int shown = Math.Clamp(_state.History.Count, 1, 6);
        // The last 28: the tick box for the enemy cards, its 18 px box with 2 px above and below, and the 6 px gap.
        float height = 104 + shown * 69 + 28;
        VBoxContainer column = StartPicker("history", "Earlier scans", width, height);

        if (_state.History.Count == 0)
        {
            Label nothing = Wrapped("Nothing yet. Every simulation you run is kept here, the last " + SimHistory.Kept + " of them, so you can look at it again without waiting.", width - 30, GameStyle.TextGrey, 13);
            nothing.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            column.AddChild(nothing);
        }
        else
        {
            var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var list = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 3);
            scroll.AddChild(list);
            column.AddChild(scroll);
            foreach (Scan scan in _state.History) list.AddChild(ScanRow(scan));

            var foot = _s.Spaced(new HBoxContainer(), 8);
            Label kept = Wrapped($"The last {SimHistory.Kept} are kept. Old numbers: your character has moved on since.", width - 110, GameStyle.TextDim, 11);
            kept.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            foot.AddChild(kept);
            Button clear = _s.GameButton("Clear", "TexturedButton4x", 12);
            clear.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            clear.TooltipText = "Forget every earlier scan of this character";
            bool sure = false;
            clear.Pressed += () =>
            {
                if (!sure) { sure = true; clear.Text = "Really?"; return; }
                ShowCurrent();
                _clearHistory();
                ClosePicker();
                Refresh();
            };
            foot.AddChild(clear);
            column.AddChild(foot);
        }
        // The cards in the game's menu show the newest of these scans, so they are switched on and off here.
        Button cards = _s.TickBox("Show on enemy cards in the combat areas menu", _state.ShowEnemyCards);
        cards.TooltipText = "Each enemy card in the game's combat areas menu shows the newest scan of that fight\n" +
                            "made with what you have on now: Safe or Risky, XP and deaths per hour";
        cards.Toggled += on => { _state.ShowEnemyCards = on; _changed(); };
        column.AddChild(cards);
        PlacePicker(_historyButton, width, height, below: true);
    }

    // "Best for Ranged                      2 h ago"
    // "Every unlocked area · 1 hour · Loadout 1"
    // "Top: Scarecrow 4,188 XP/h           126 fights"
    Button ScanRow(Scan scan)
    {
        var row = new Button
        {
            ThemeTypeVariation = "OutlineToggleButton", CustomMinimumSize = new Vector2(0, _s.Px(66)),
            FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            ToggleMode = true, ButtonPressed = ReferenceEquals(_state.Viewing, scan),
            TooltipText = $"{scan.At.ToLocalTime():d MMM yyyy, HH:mm}\n{scan.Where}\nwith {scan.Setups}",
        };
        row.Pressed += () => Show(scan);
        var lines = _s.Spaced(new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, -1);
        lines.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.Minsize, _s.Px(8));
        row.AddChild(lines);

        Control Line(string left, Color leftColor, int leftSize, Font? leftFont, string right, Color rightColor)
        {
            var line = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 8);
            Label a = _s.Text(left, leftColor, leftSize, leftFont);
            a.ClipText = true;
            a.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            a.MouseFilter = Control.MouseFilterEnum.Ignore;
            line.AddChild(a);
            if (right != "")
            {
                Label b = _s.Text(right, rightColor, 12);
                b.MouseFilter = Control.MouseFilterEnum.Ignore;
                line.AddChild(b);
            }
            return line;
        }

        string duration = SimState.Durations.FirstOrDefault(d => Math.Abs(d.Hours - scan.Hours) < 1e-6).Label ?? $"{scan.Hours:0.##} h";
        string what = (scan.BestMode ? scan.Where + "  ·  " : "") + duration + (scan.Unlimited ? "" : ", from bank") + "  ·  " + scan.Setups;
        SimResult? top = scan.Top;
        string best = top == null ? "Nothing could be fought"
            : $"Top: {top.Target.Name}{(top.Target.StanceName != "" ? " (" + top.Target.StanceName + ")" : "")}  {Fmt(scan.Rank(top))} XP/h" +
              (scan.Compare ? (top.SetupKey == scan.SetupKey ? " with A" : " with B") : "");
        int fights = scan.Fights;
        lines.AddChild(Line(scan.Title, GameStyle.TextWhite, 14, GameStyle.SemiBold, Ago(scan.At), GameStyle.TextGrey));
        lines.AddChild(Line(what, GameStyle.TextGrey, 12, null, "", GameStyle.TextGrey));
        lines.AddChild(Line(best, GameStyle.TextPaper, 12, null, $"{fights} {(fights == 1 ? "fight" : "fights")}{(scan.Stopped ? ", stopped" : "")}", GameStyle.TextDim));
        return row;
    }

    internal static string Ago(DateTime utc)
    {
        TimeSpan age = DateTime.UtcNow - utc;
        DateTime local = utc.ToLocalTime();
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalHours < 12 ? $"{(int)age.TotalHours} h ago"
            : local.Date == DateTime.Now.Date ? $"today {local:HH:mm}"
            : local.Date == DateTime.Now.Date.AddDays(-1) ? $"yesterday {local:HH:mm}"
            : local.ToString("d MMM, HH:mm");
    }

    // ---- the item picker: every item of the game that fits the slot ---------------------------------------

    void OpenPicker(GearSlot slot, Control anchor)
    {
        ClosePicker();
        if (_state.Running) return;
        const float width = 330, height = 470;
        VBoxContainer column = StartPicker("items", slot.SlotName, width, height);

        var search = new LineEdit { PlaceholderText = "Search items...", ClearButtonEnabled = true };
        search.AddThemeFontSizeOverride("font_size", _s.Px(14));
        column.AddChild(search);
        Button ownedOnly = Toggle("Only items I own", null, "Hide items that are neither in your bank nor equipped");
        ownedOnly.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        column.AddChild(ownedOnly);
        // Another slot's item fills this one (a two-handed weapon): what is picked here takes it off (SimGear.Put).
        if (slot.FilledFrom != "")
            column.AddChild(Wrapped($"{slot.ItemName} in the {slot.FilledFromName} slot needs this slot too. Picking an item here takes it off.", width - 30, GameStyle.TextGrey, 12));

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var list = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 2);
        scroll.AddChild(list);
        column.AddChild(scroll);
        Label none = _s.Text("No item matches.", GameStyle.TextGrey, 13);
        none.HorizontalAlignment = HorizontalAlignment.Center;
        none.Visible = false;
        column.AddChild(none);

        void Choose(string? itemId)
        {
            _lookup.Pick(slot, itemId);   // back to the loadout's own (null), nothing (""), an item: by the game's rules
            ShowCurrent();
            ClosePicker();
            _gearChanged();
            FillGear();
            Refresh();
        }

        var rows = new List<(Button Row, string Name, bool Owned)>();
        if (slot.Changed) list.AddChild(PickerRow("Back to the loadout's item", null, "", GameStyle.TextGold, () => Choose(null)));
        // A slot that another slot's item fills is emptied through that slot (SimGear.TakeOff).
        if (slot.ItemId != null && slot.FilledFrom == "") list.AddChild(PickerRow("Nothing (leave it empty)", null, "", GameStyle.TextGrey, () => Choose("")));
        // What you own first, then the rest of the game, each by name.
        foreach (GearChoice choice in _lookup.ChoicesFor(slot).OrderByDescending(c => c.Owned > 0).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            bool worn = choice.ItemId == slot.ItemId;
            string right = worn ? "worn" : choice.Owned > 0 ? "x" + Fmt(choice.Owned) : "not owned";
            Color tint = worn ? Good : choice.Owned > 0 ? GameStyle.TextWhite : GameStyle.TextDim;
            Button row = PickerRow(choice.Name, choice.Icon, right, tint, () => Choose(choice.ItemId));
            if (!choice.CanWear) row.TooltipText = "You do not meet this item's requirements: the simulation will not be able to equip it";
            if (!choice.CanWear) row.Modulate = new Color(1, 0.75f, 0.7f);
            list.AddChild(row);
            rows.Add((row, choice.Name, choice.Owned > 0));
        }

        void Filter()
        {
            string text = search.Text.Trim();
            int shown = 0;
            foreach (var (row, name, owned) in rows)
            {
                row.Visible = (text == "" || name.Contains(text, StringComparison.OrdinalIgnoreCase)) && (!ownedOnly.ButtonPressed || owned);
                if (row.Visible) shown++;
            }
            none.Visible = shown == 0;
        }
        search.TextChanged += _ => Filter();
        ownedOnly.Toggled += _ => Filter();

        PlacePicker(anchor, width, height);
        search.GrabFocus();
    }

    Button PickerRow(string name, Texture2D? icon, string right, Color tint, Action? chosen)
    {
        var row = new Button
        {
            ThemeTypeVariation = "OutlineToggleButton", CustomMinimumSize = new Vector2(0, _s.Px(32)),
            FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        if (chosen != null) row.Pressed += chosen;
        var line = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 6);
        line.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.Minsize, _s.Px(6));
        row.AddChild(line);
        line.AddChild(_s.Picture(icon, 24));
        Label label = _s.Text(name, tint);
        label.ClipText = true;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        line.AddChild(label);
        if (right != "")
        {
            Label note = _s.Text(right, tint == GameStyle.TextWhite ? GameStyle.TextGrey : tint, 12);
            note.MouseFilter = Control.MouseFilterEnum.Ignore;
            line.AddChild(note);
        }
        return row;
    }

    // ---- middle: one line per fight ------------------------------------------------------------------------

    Control BuildResults()
    {
        float width = ResultsWidth();
        Control column = _resultsColumn = _s.Column("Results, per hour", out VBoxContainer body, width);
        column.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        // What the table shows, and the way to the earlier scans.
        var top = _s.Spaced(new HBoxContainer(), 6);
        _showing = _s.Text("", GameStyle.TextGrey, 12);
        _showing.ClipText = true;
        _showing.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _showing.MouseFilter = Control.MouseFilterEnum.Pass;
        top.AddChild(_showing);
        _back = _s.GameButton("Back to now", "TexturedButton4x", 12);
        _back.TooltipText = "Leave this earlier scan and show the current results";
        _back.Pressed += () => { ShowCurrent(); ClosePicker(); Refresh(); };
        top.AddChild(_back);
        top.AddChild(ColumnsButton());
        _historyButton = _s.GameButton("History", "TexturedButton4x", 12);
        _historyButton.TooltipText = "Look at an earlier scan again, without simulating anything";
        _historyButton.Pressed += OpenHistory;
        top.AddChild(_historyButton);
        body.AddChild(top);

        var header = _s.Spaced(new HBoxContainer(), 6);
        var headerMargin = new MarginContainer();
        headerMargin.AddThemeConstantOverride("margin_left", _s.Px(8));
        headerMargin.AddThemeConstantOverride("margin_right", _s.Px(8 + 12));   // the rows' own padding and the scroll bar
        headerMargin.AddChild(header);
        header.AddChild(HeaderButton("Enemy", "Sort by name", 0, NameKey));
        header.AddChild(NumbersStrip(out _headerCells));
        FillHeader();
        body.AddChild(headerMargin);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowAlways,
        };
        _rows = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 2);
        scroll.AddChild(_rows);
        body.AddChild(scroll);
        body.AddChild(SidewaysBar(scroll));

        // Nothing to show yet: say what to do, and offer the last scans right here.
        _emptyBox = _s.Spaced(new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center }, 10);
        _empty = Wrapped("", width - 60, GameStyle.TextGrey, 14);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _emptyBox.AddChild(_empty);
        _recent = _s.Spaced(new VBoxContainer(), 3);
        var recentMargin = new MarginContainer();
        recentMargin.AddThemeConstantOverride("margin_left", _s.Px(40));
        recentMargin.AddThemeConstantOverride("margin_right", _s.Px(40));
        recentMargin.AddChild(_recent);
        _emptyBox.AddChild(recentMargin);
        body.AddChild(_emptyBox);
        return column;
    }

    Button HeaderButton(string title, string tip, float width, string key)
    {
        bool name = key == NameKey;
        var button = new Button
        {
            Text = title, Flat = true, TooltipText = tip, FocusMode = Control.FocusModeEnum.None,
            Alignment = name ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        if (name) button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        else button.CustomMinimumSize = new Vector2(_s.Px(width), 0);
        button.AddThemeFontSizeOverride("font_size", _s.Px(12));
        if (GameStyle.SemiBold is { } font) button.AddThemeFontOverride("font", font);
        button.Pressed += () => SortBy(key);
        button.Draw += () => DrawSortArrow(button);
        button.SetMeta("column", key);
        _headers.Add(button);
        return button;
    }

    void FillRows()
    {
        List<SimResult> ordered = Sorted(_state.Lines());
        MatchCompare(ordered);   // the header's cells for one setup or two (SimViewCompare.cs)
        MatchUpgrades(ordered);   // or for the items tried for a fight (SimViewUpgrades.cs)
        PaintHeaders();

        string signature = _state.Viewing?.Id + "#" + _state.ShownSetupKey + "#" + _state.ShownComparing + _state.ShownBest + _state.ShownSkillId + "#" + string.Join("|", ordered.Select(LineKey)) + "#" + _state.Selected + "#" + _state.SortKey + _state.SortDescending;
        if (signature != _tableSignature)
        {
            _tableSignature = signature;
            foreach (Node old in _rows.GetChildren()) old.QueueFree();
            // On Best for a fight has one line, its best stance so far. When a better stance of the fight you clicked
            // replaces that line during a scan, the fight stays selected on its new line, open or folded.
            SimTarget? pinned = _state.Pinned && _state.Selected is { } key
                ? _state.ShownResults.GetValueOrDefault(SimResult.MakeKey(_state.ShownSetupKey, key))?.Target : null;
            SimResult? match = pinned == null ? null : ordered.FirstOrDefault(r =>
                r.Target.Key == pinned.Key || (_state.ShownBest && r.Target.FightKey == pinned.FightKey));
            if (_state.ShownComparing) match = PinnedLine(ordered);
            _state.Pinned = match != null;
            _state.Selected = (match ?? ordered.FirstOrDefault())?.Target.Key;
            var group = new ButtonGroup();
            foreach (SimResult result in ordered) _rows.AddChild(BuildRow(result, group));
        }

        bool any = ordered.Count > 0;
        _rows.GetParent<Control>().Visible = any;
        _sidewaysRow.Visible = any && _cellsWidth > _numbersWidth;
        _emptyBox.Visible = !any;
        if (_state.Viewing is { } scan)
        {
            _showing.Text = $"Earlier scan, {Ago(scan.At)}:  {scan.Title}  ·  {scan.SetupLabel}";
            _showing.TooltipText = $"{scan.At.ToLocalTime():d MMM yyyy, HH:mm}. These numbers are from then: your levels and equipment may have changed.";
        }
        else
        {
            _showing.Text = _state.PerStance ? $"Best stance for each fight, with: {_state.SetupLabel}" : "With: " + _state.SetupName;
            _showing.TooltipText = "";
        }
        _showing.AddThemeColorOverride("font_color", _state.Viewing != null ? GameStyle.TextGold : GameStyle.TextGrey);
        _back.Visible = _state.Viewing != null;
        _historyButton.Text = _state.History.Count > 0 ? $"History ({_state.History.Count})" : "History";

        bool otherSetups = _state.Results.Values.Any(r => r.SetupKey != _state.SetupKey);
        _empty.Text = _state.Running ? "Simulating..." : _state.Catalog.Count == 0
            ? "No area is unlocked yet."
            : otherSetups ? "Nothing simulated with this equipment yet.\n\nPress Simulate to compare it with what you ran before."
            : _state.BestMode ? "Choose the skill you want to train and where on the left, then press Simulate.\n\nEvery fight is tried in each attack stance of your weapon, and the fights are ranked by what they give that skill."
            : "Choose where to fight and with which equipment on the left, then press Simulate.\n\nEvery fight is run with the game's own combat, on a hidden copy of your character. Nothing is saved.";
        if (_state.ShownComparing) FillPairShowing(ordered);   // the wins of each setup, and the words when there is nothing
        if (_state.ShowingUpgrades) FillUpgradeShowing(ordered);   // the fight the items are tried for, and how far it is

        // The last scans, one click away, while there is nothing else to look at.
        string recent = any || _state.Running ? "" : string.Join("|", _state.History.Take(3).Select(h => h.Id));
        if (recent != _recentSignature)
        {
            _recentSignature = recent;
            foreach (Node old in _recent.GetChildren()) old.QueueFree();
            if (recent != "")
            {
                Label caption = _s.Text("Or look at an earlier scan", GameStyle.TextDim, 12);
                caption.HorizontalAlignment = HorizontalAlignment.Center;
                _recent.AddChild(caption);
                foreach (Scan earlier in _state.History.Take(3)) _recent.AddChild(ScanRow(earlier));
            }
        }
    }

    Control BuildRow(SimResult result, ButtonGroup group)
    {
        if (_state.ShowingUpgrades) return BuildUpgradeRow(result, group);   // an item tried (SimViewUpgrades.cs)
        var row = new Button
        {
            ThemeTypeVariation = "OutlineToggleButton", ToggleMode = true, ButtonGroup = group,
            ButtonPressed = result.Target.Key == _state.Selected,
            CustomMinimumSize = new Vector2(0, _s.Px(30)), FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            TooltipText = RowTip(result),
        };
        row.Pressed += () => { _state.Selected = result.Target.Key; _state.Pinned = true; FillDetails(); };

        var line = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 6);
        line.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.Minsize, _s.Px(8));
        row.AddChild(line);

        line.AddChild(_s.Picture(_lookup.TargetIcon(result.Target), 20));
        Label name = _s.Text(result.Target.Name, LineFailed(result) ? GameStyle.TextDim : GameStyle.TextWhite);
        name.ClipText = true;
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        name.MouseFilter = Control.MouseFilterEnum.Ignore;
        line.AddChild(name);
        if (StanceText(result) is { Length: > 0 } stanceName)   // none in compare mode (SimViewCompare.cs)
        {
            Label stance = _s.Text(stanceName, GameStyle.TextPaper, 12);
            stance.MouseFilter = Control.MouseFilterEnum.Ignore;
            line.AddChild(stance);
        }
        Label level = _s.Text(result.Target.IsDungeon ? "Dungeon" : "Lv " + result.Target.CombatLevel, GameStyle.TextDim, 12);
        level.CustomMinimumSize = new Vector2(_s.Px(36), 0);
        level.HorizontalAlignment = HorizontalAlignment.Right;
        level.MouseFilter = Control.MouseFilterEnum.Ignore;
        line.AddChild(level);

        line.AddChild(RowCells(result));
        return row;
    }

    // ---- right: everything about one fight -----------------------------------------------------------------

    Control BuildDetails()
    {
        const float width = 290;
        Control column = _s.Column("Details", out VBoxContainer body, width);
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _details = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(_s.Px(width - 30), 0) }, 4);
        scroll.AddChild(_details);
        body.AddChild(scroll);

        // Below the list, always in sight: what else to wear for the selected fight, and go and fight it.
        body.AddChild(BuildUpgradeButton());   // Find upgrades (SimViewUpgrades.cs)
        body.AddChild(BuildWearBox(width - 30));
        _fightNote = Wrapped("", width - 30, GameStyle.TextGrey, 11);
        _fightNote.HorizontalAlignment = HorizontalAlignment.Center;
        body.AddChild(_fightNote);
        _fightButton = _s.GameButton("Fight", "TexturedButtonCancel8x", 18);
        _fightButton.CustomMinimumSize = new Vector2(0, _s.Px(46));
        _fightButton.Icon = GameStyle.Icon("Combat");
        _fightButton.ExpandIcon = true;
        _fightButton.AddThemeConstantOverride("icon_max_width", _s.Px(26));
        _fightButton.AddThemeConstantOverride("h_separation", _s.Px(8));
        _fightButton.ClipText = true;
        _fightButton.Pressed += () => { if (_fightAs != null) _fight(_fightAs); };
        body.AddChild(_fightButton);
        // A slow breath of light, so the way out of the numbers and into the fight is easy to find.
        Tween glow = _fightButton.CreateTween().SetLoops();
        glow.TweenProperty(_fightButton, "self_modulate", new Color(1.22f, 1.1f, 1.1f), 0.9).SetTrans(Tween.TransitionType.Sine);
        glow.TweenProperty(_fightButton, "self_modulate", Colors.White, 0.9).SetTrans(Tween.TransitionType.Sine);
        return column;
    }

    // The Fight button, its words and "Wear this setup first", for the result Fight would start.
    void FillFight(SimResult? r)
    {
        // The button follows the selection even when the list itself did not change.
        _fightTarget = r is { Failed: false } ? r.Target : null;
        _fightAs = r is { Failed: false } ? FightAs(r) : null;
        _fightButton.Visible = _fightTarget != null;
        _fightNote.Visible = _fightTarget != null;
        if (_fightTarget != null && _fightAs != null)
        {
            _fightButton.Text = "Fight " + _fightTarget.Name;
            _fightButton.TooltipText = _fightTarget.IsDungeon ? $"Enter {_fightTarget.Name} now" : $"Start fighting {_fightTarget.Name} in {_fightTarget.AreaName} now";
            // What pressing it will and will not do, piece by piece: the same pieces the mod acts on.
            bool viewing = _state.Viewing != null, worn = !viewing && _state.IsWornSetup;
            _fightNote.Text = string.Join(" ", new[]
            {
                viewing ? "These numbers are from an earlier scan." : "",
                worn ? "" : "Starts with what you are wearing now, not the equipment simulated here.",
                _fightAs.StanceName != "" ? $"Switches to the {_fightAs.StanceName} stance, if your weapon has it." : "",
                worn && _state.SpellId != "" ? $"Selects {_state.Spells.FirstOrDefault(s => s.Id == _state.SpellId)?.Name ?? "the spell"}." : "",
            }.Where(t => t != ""));
            _fightNote.Visible = _fightNote.Text != "";
        }
        FillWear();
        FillUpgradeButton();   // for the same fight (SimViewUpgrades.cs)
    }

    void FillDetails()
    {
        if (_state.ShownComparing) { FillPairDetails(); return; }   // two setups (SimViewCompare.cs)
        if (FillUpgradeDetails()) return;   // an item tried for the fight (SimViewUpgrades.cs)
        var shown = _state.ShownResults;
        SimResult? r = _state.Selected != null ? shown.GetValueOrDefault(SimResult.MakeKey(_state.ShownSetupKey, _state.Selected)) : null;
        var others = r == null ? new List<SimResult>()
            : shown.Values.Where(o => o.Target.Key == r.Target.Key && o.SetupKey != r.SetupKey && !o.Failed).ToList();
        string signature = r == null ? "none" : _state.Viewing?.Id + r.Key + r.RealMilliseconds + _state.ShownSkillId + "/" + string.Join(",", others.Select(o => o.Key + o.RealMilliseconds))
            + "/" + shown.Values.Count(o => o.SetupKey == r.SetupKey && o.Target.FightKey == r.Target.FightKey && o.Target.StanceId != null);
        FillFight(r);
        if (signature == _detailsSignature) return;
        _detailsSignature = signature;
        foreach (Node old in _details.GetChildren()) old.QueueFree();
        if (r == null)
        {
            Label hint = Wrapped("Select a fight to see its experience, loot, supplies and how safe it is.", 250, GameStyle.TextGrey, 13);
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            _details.AddChild(hint);
            return;
        }

        // Who, where, with what: every line whole (Whole).
        var head = _s.Spaced(new HBoxContainer(), 10);
        head.AddChild(HeadPicture(_lookup.TargetIcon(r.Target)));
        var title = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 0);
        title.AddChild(Whole(_s.Text(r.Target.Name, GameStyle.TextWhite, 16, GameStyle.Bold)));
        title.AddChild(Whole(_s.Text(r.Target.IsDungeon ? r.Target.CategoryName : $"{r.Target.AreaName}  ·  Level {r.Target.CombatLevel}", GameStyle.TextGrey, 12)));
        title.AddChild(Whole(_s.Text(r.SetupLabel, GameStyle.TextDim, 12)));
        // Under the setup's name, the stance it was fought in (SimViewStance.cs).
        if (StanceLine(r) is { } stance) title.AddChild(stance);
        head.AddChild(title);
        _details.AddChild(head);

        if (r.Note != "") _details.AddChild(Wrapped(r.Note, 250, r.Failed ? Bad : GameStyle.TextGold, 12));
        if (r.Failed) return;

        // The same fight in the other stances of the weapon.
        var stances = r.Target.StanceId == null ? new List<SimResult>()
            : shown.Values.Where(o => o.SetupKey == r.SetupKey && o.Target.FightKey == r.Target.FightKey && o.Target.Key != r.Target.Key && o.Target.StanceId != null && !o.Failed).ToList();
        if (stances.Count > 0)
        {
            _details.AddChild(_s.Section("Other stances"));
            foreach (SimResult other in stances.OrderByDescending(_state.Xp))
            {
                // Three skills do not fit on one line, and a line that wraps by itself may part a skill from its
                // number: two to a line.
                var skills = other.Xp.OrderByDescending(x => x.Value).Take(3).Select(x => $"{_lookup.Skill(x.Key).Name} {Fmt(other.PerHour(x.Value))}");
                _details.AddChild(Stat(other.Target.StanceName, Fmt(_state.Xp(other)), null,
                    string.Join("\n", new[] { Ranged(other, _state.XpRate) }.Concat(skills.Chunk(2).Select(line => string.Join("  ·  ", line))).OfType<string>())));
            }
        }

        // The same fight with other equipment, when it was simulated: this one against each of them.
        if (others.Count > 0)
        {
            _details.AddChild(_s.Section("Compared with"));
            foreach (SimResult other in others.OrderByDescending(o => o.XpPerHour).Take(4))
            {
                // The other setup's name on a line of its own: it can be long.
                Label versus = Whole(_s.Text(other.SetupLabel, GameStyle.TextGrey, 12));
                versus.TooltipText = other.SetupLabel;
                versus.MouseFilter = Control.MouseFilterEnum.Pass;
                _details.AddChild(versus);
                // When the stance tells the two apart: its stance under its name, as this one's is under the name above.
                if (StanceAgainst(r, other) is { } otherStance) _details.AddChild(otherStance);
                // The columns of the list, each the way it is better.
                foreach (Control line in Compared(r, other)) _details.AddChild(line);
            }
        }

        _details.AddChild(_s.Section("Per hour"));
        _details.AddChild(Stat("Kills", Fmt(r.KillsPerHour), null,
            Notes(Ranged(r, SimRate.PerHour(SimRanges.Kills)), r.Kills > 0 ? $"one every {r.SecondsPerKill:0.#}s" : null)));
        if (r.Target.IsDungeon) _details.AddChild(Stat("Dungeon runs", Fmt(r.PerHour(r.DungeonRuns)), null, Ranged(r, SimRate.PerHour(SimRanges.Runs))));
        _details.AddChild(Stat("Combat XP", Fmt(r.XpPerHour), null, Ranged(r, SimRate.PerHour(SimRanges.Xp))));
        _details.AddChild(Stat("Gold", Fmt(r.GoldPerHour), GameStyle.TextGold,
            Notes(Ranged(r, SimRate.PerHour(SimRanges.Gold)), $"{Fmt(r.CoinsPerHour)} coins + {Fmt(r.LootValuePerHour)} loot")));

        if (r.Xp.Count > 0)
        {
            _details.AddChild(_s.Section("Experience"));
            foreach (var (id, xp) in r.Xp.OrderByDescending(x => x.Value))
            {
                var (skill, icon) = _lookup.Skill(id);
                _details.AddChild(Stat(skill, Fmt(r.PerHour(xp)), null, Ranged(r, SimRate.PerHour(SimRanges.XpOf(id))), icon));
            }
        }

        _details.AddChild(_s.Section("Survival"));
        _details.AddChild(Stat("Deaths", r.Deaths == 0 ? "none" : Fmt(r.DeathsPerHour), r.Deaths == 0 ? Good : Bad, DeathsNote(r)));
        _details.AddChild(Stat("Lowest hitpoints", $"{r.LowestHp:N0} / {r.MaxHp:N0}",
            r.LowestHpFraction < 0.15 ? Bad : r.LowestHpFraction < 0.35 ? GameStyle.TextGold : GameStyle.TextWhite, r.LowestHpFraction.ToString("P0")));
        _details.AddChild(Stat("Damage taken", Fmt(r.PerHour(r.DamageTaken)), null, Ranged(r, SimRate.PerHour(SimRanges.Damage))));
        _details.AddChild(Stat("Healed by food", Fmt(r.PerHour(r.Healed)), null,
            Notes(Ranged(r, SimRate.PerHour(SimRanges.Healed)), $"{Fmt(r.FoodPerHour)} eaten")));
        if (r.Kills > 0)
        {
            double perKill = r.FoodPerKill;
            _details.AddChild(Stat("Food per kill", PerKill(perKill), null, Notes(Ranged(r, SimRate.PerKill(SimRanges.Food), PerKill), EveryKills(perKill))));
            if (r.FoodEaten > 0) _details.AddChild(Stat("Healed per kill", Fmt(r.HealedPerKill), null, Ranged(r, SimRate.PerKill(SimRanges.Healed))));
            // How far the food in the bank goes here; only known for the loadout the character wears.
            // An earlier scan is of another day's fight; today's bank says nothing about it. A fight that
            // ate the bank empty has counted it instead: see below.
            if (perKill > 0 && r.FoodGoneSeconds < 0 && _state.Viewing == null && _state.FoodInBank >= 0 && r.SetupKey.Split(';')[0] == _state.FoodLoadoutId)
            {
                // Whole kills: the piece that is left over does not finish another one.
                double kills = Math.Floor(_state.FoodInBank / perKill);
                _details.AddChild(Stat("Your food lasts", kills >= 1 || _state.FoodInBank == 0 ? $"{Fmt(kills)} {(kills == 1 ? "kill" : "kills")}" : "less than 1 kill",
                    null, LastsNote(r, _state.FoodInBank, $"{_state.FoodInBank:N0} {_state.FoodName} in the bank")));
            }
        }
        else if (r.FoodEaten > 0) _details.AddChild(Stat("Food per kill", "-", GameStyle.TextDim, "nothing was killed"));
        // From the bank, and the food ran out: the kills until then were counted, no estimate is needed.
        if (r.FoodGoneSeconds >= 0)
            _details.AddChild(Stat("Your food lasts", $"{Fmt(r.FoodGoneKills)} {(r.FoodGoneKills == 1 ? "kill" : "kills")}", Bad,
                $"ran out after {Clock(r.FoodGoneSeconds)}"));

        if (r.Used.Count > 0)
        {
            _details.AddChild(_s.Section("Used"));
            foreach (Amount used in r.Used.Take(8))
                _details.AddChild(Stat(used.Name, "-" + Fmt(r.PerHour(used.Quantity)), Bad, Ranged(r, used), _lookup.AmountIcon(used)));
        }

        var drops = r.Loot.Where(l => !l.IsCurrency || l.Id != SimEngine.Gold).ToList();
        if (drops.Count > 0)
        {
            _details.AddChild(_s.Section("Loot"));
            foreach (Amount drop in drops.Take(12))
                _details.AddChild(Stat(drop.Name, "+" + Fmt(r.PerHour(drop.Quantity)), null,
                    Notes(Ranged(r, drop), drop.SellsFor > 0 ? $"{Fmt(r.PerHour(drop.Quantity * drop.SellsFor))} gold" : null), _lookup.AmountIcon(drop)));
            if (drops.Count > 12) _details.AddChild(_s.Text($"and {drops.Count - 12} more", GameStyle.TextDim, 12));
        }

        Label foot = Wrapped($"{Clock(r.Hours * 3600)} of fighting simulated in {r.RealMilliseconds / 1000:0.0}s.{RangesFoot(r)}", 250, GameStyle.TextDim, 11);
        foot.HorizontalAlignment = HorizontalAlignment.Center;
        _details.AddChild(new Control { CustomMinimumSize = new Vector2(0, _s.Px(4)), MouseFilter = Control.MouseFilterEnum.Ignore });
        _details.AddChild(foot);
    }

    // "+12%" when this is more than that.
    static string Change(double mine, double theirs)
    {
        if (theirs <= 0) return mine > 0 ? "new" : "same";
        double change = mine / theirs - 1;
        return Math.Abs(change) < 0.005 ? "same" : change.ToString("+0%;-0%");
    }

    // "[icon] Name                          value"
    // "                                     note"
    Control Stat(string name, string value, Color? tint = null, string? note = null, Texture2D? icon = null)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", _s.StatLine());
        var rows = _s.Spaced(new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, -2);
        var line = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 6);
        if (icon != null) line.AddChild(_s.Picture(icon, 18));
        Label label = _s.Text(name);
        label.ClipText = true;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        line.AddChild(label);
        Label number = _s.Text(value, tint ?? GameStyle.TextWhite, 14, GameStyle.Bold);
        number.HorizontalAlignment = HorizontalAlignment.Right;
        line.AddChild(number);
        rows.AddChild(line);
        // Under the whole line: a long note must not squeeze the name.
        if (note != null)
        {
            Label small = _s.Text(note, GameStyle.TextGrey, 11);
            small.HorizontalAlignment = HorizontalAlignment.Right;
            // Wrapped, not cut: cut, a note that is too long lost its beginning, being right-aligned.
            small.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            rows.AddChild(small);
        }
        panel.AddChild(rows);
        return panel;
    }

    // ---- keeping it current --------------------------------------------------------------------------------

    public void Refresh()
    {
        if (!_window.IsAlive) return;
        var (_, hours) = SimState.Durations[Math.Clamp(_state.Duration, 0, SimState.Durations.Length - 1)];
        int fights = _state.ToSimulate().Count;

        _supplyHint.Text = _state.Unlimited
            ? "Never runs out: what a fight gives and uses while you keep it supplied."
            : "Uses your bank: how far your food, ammo and runes get you.";
        // With the stances the line is about as wide as the column and broke inside the time ("about 2" | "min"): the
        // time goes on a line of its own.
        _estimate.Text = fights == 0 ? "Nothing to simulate here"
            : $"{fights} {(fights == 1 ? "fight" : "fights")}{(_state.PerStance ? $" ({_state.Stances.Count} stances each)\n" : "  ·  ")}{Estimate(fights, hours)}";

        _go.Text = _state.Running ? "Cancel" : "Simulate";
        _go.ThemeTypeVariation = _state.Running ? "TexturedButtonCancel8x" : "TexturedActionButtonGreen8x";
        _go.Disabled = !_state.Running && fights == 0;
        _modeFights.Disabled = _modeBest.Disabled = _where.Disabled = _duration.Disabled = _loadout.Disabled = _unlimited.Disabled = _fromBank.Disabled = _reset.Disabled = _state.Running;
        _sure.Disabled = _state.Running || !_state.Unlimited;
        if (_train != null) _train.Disabled = _state.Running;
        if (_spell != null && GodotObject.IsInstanceValid(_spell)) _spell.Disabled = _state.Running;
        if (_stance != null && GodotObject.IsInstanceValid(_stance)) _stance.Disabled = _state.Running;
        _where.Text = _state.ScopeLabel;
        _progress.Value = _state.Total > 0 ? (double)_state.Done / _state.Total : 0;
        _progress.Modulate = _state.Running || _state.Done > 0 ? Colors.White : new Color(1, 1, 1, 0.35f);
        _status.Text = _state.Message != "" ? _state.Message
            : _state.Running && _state.Current != "" ? $"{_state.ScanStatus}  ·  {_state.Current}" : _state.ScanStatus;
        _status.AddThemeColorOverride("font_color", _state.Message != "" ? Bad : GameStyle.TextGrey);
        RefreshPair(hours);   // compare mode: the fights of A and B, and the Compare, A and B buttons (SimViewCompare.cs)

        FillRows();
        FillDetails();
    }

#if DEVTOOLS
    public Button? DevFirstRow => _rows.GetChildren().OfType<Button>().FirstOrDefault(b => !b.IsQueuedForDeletion());

    /// <summary>
    /// Development: opens the areas, the earlier scans, the newest scan, or the item picker of a slot;
    /// "sort=Food/kill" is one click on that column's name, "details=end" scrolls the details to their last line.
    /// The Columns picker and the columns' own requests are in DevColumns (SimViewColumns.cs).
    /// </summary>
    public void DevOpenPicker(string slotName)
    {
        if (slotName == "details=end") { if (_details.GetParent() is ScrollContainer scroll) scroll.ScrollVertical = int.MaxValue; return; }
        if (DevColumns(slotName) || DevRanges(slotName) || DevPairs(slotName) || DevUpgrades(slotName)) return;
        if (slotName.StartsWith("sort=", StringComparison.Ordinal))
        {
            _headers.FirstOrDefault(h => h.Text.Equals(slotName[5..], StringComparison.OrdinalIgnoreCase))?.EmitSignal(BaseButton.SignalName.Pressed);
            return;
        }
        if (slotName == "where") { OpenAreas(); return; }
        if (slotName == "history") { OpenHistory(); return; }
        if (slotName == "view") { if (_state.History.FirstOrDefault() is { } newest) Show(newest); return; }
        if (slotName.StartsWith("spell=", StringComparison.Ordinal))
        {
            _state.SpellId = _state.Spells.FirstOrDefault(s => s.Name.Contains(slotName[6..], StringComparison.OrdinalIgnoreCase))?.Id ?? "";
            FillChoices();
            Refresh();
            return;
        }
        GearSlot? slot = _state.Gear.FirstOrDefault(g => g.SlotName.Contains(slotName, StringComparison.OrdinalIgnoreCase));
        if (slot != null) OpenPicker(slot, _gearHolder);
    }
#endif

    Label Caption(string text)
    {
        Label label = _s.Text(text, GameStyle.TextGrey, 12, GameStyle.SemiBold);
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        return label;
    }

    Label Wrapped(string text, float width, Color color, int size)
    {
        Label label = _s.Text(text, color, size);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(_s.Px(width), 0);
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        return label;
    }

    // The picture of a Details head: at the top, beside the name, however many lines the head grows to (Compare's six, a
    // line that takes two), as the Item Encyclopedia's Details head keeps its picture at the top (EncyclopediaView.PlainHead).
    TextureRect HeadPicture(Texture2D? texture)
    {
        TextureRect picture = _s.Picture(texture, 44);
        picture.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        return picture;
    }

    // A line of a Details head, or a setup's name under "Compared with": whole at every size of the window, never cut.
    // Words that do not fit go on to a second line, as Wrapped's do; it asks for no width of its own, so beside the head's
    // picture it takes the room that is left.
    static Label Whole(Label label)
    {
        label.ClipText = false;
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        return label;
    }

    // A Whole line whose words go on one line when they fit the room it has, and otherwise on two lines parted where they
    // read best, as given ("Weapon" over "in place of Iron Sword"), rather than wherever the room ends; a second line that
    // is still too wide wraps on. Measured again whenever its width or its theme changes, as FitBeside measures a box.
    static Label OneOrTwo(Label label, string oneLine, string twoLines)
    {
        Whole(label).Text = oneLine;
        void Fit()
        {
            if (!GodotObject.IsInstanceValid(label) || label.Size.X <= 0) return;
            float room = label.Size.X - label.GetThemeStylebox("normal").GetMinimumSize().X;
            float wide = label.GetThemeFont("font").GetStringSize(oneLine, HorizontalAlignment.Left, -1, label.GetThemeFontSize("font_size")).X;
            string fits = wide <= room ? oneLine : twoLines;
            if (label.Text != fits) label.Text = fits;
        }
        label.Resized += Fit;
        label.ThemeChanged += Fit;
        return label;
    }

    Button Toggle(string text, ButtonGroup? group, string tip)
    {
        Button button = _s.GameButton(text, "OutlineToggleButton", 13);
        button.ToggleMode = true;
        if (group != null) button.ButtonGroup = group;
        button.TooltipText = tip;
        button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        button.FocusMode = Control.FocusModeEnum.None;
        return button;
    }

    public static string Fmt(double value) => Math.Abs(value) switch
    {
        >= 999_950 => (value / 1_000_000).ToString("0.##") + "M",   // from where "0.#K" would round up to 1000K
        >= 100_000 => (value / 1000).ToString("0.#") + "K",
        >= 100 => value.ToString("N0"),
        >= 10 => value.ToString("0.#"),
        0 => "0",
        _ => value.ToString("0.##"),
    };

    // Food per kill is mostly a small fraction: three decimals where Fmt would round it to 0.
    internal static string PerKill(double value) => value switch
    {
        0 => "0",
        >= 1 => Fmt(value),
        >= 0.001 => value.ToString("0.###"),
        _ => "<0.001",
    };

    // "one piece every 125 kills" is easier to picture than 0.008. Nothing to add from one piece per kill up.
    static string? EveryKills(double perKill)
    {
        double kills = perKill > 0 ? 1 / perKill : 0;
        return kills >= 9.95 ? $"one piece every {kills:N0} kills" : kills >= 1.05 ? $"one piece every {kills:0.#} kills" : null;
    }

    static string Clock(double seconds) => SimText.Clock(seconds);
}
