using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MelvorMods.Shared;

namespace CombatSim;

/// <summary>One equipment slot as the simulator will fight with it.</summary>
sealed record GearSlot(string SlotId, string SlotName, int Column, int Row, string? ItemId, string ItemName, int Quantity,
    Texture2D? Icon, Texture2D? EmptyIcon, bool Changed);

/// <summary>An item that fits a slot.</summary>
sealed record GearChoice(string ItemId, string Name, Texture2D? Icon, int Owned, bool CanWear);

/// <summary>What the view shows and remembers; owned by the mod, so it survives a rebuild of the window.</summary>
sealed class SimState
{
    public List<SimTarget> Catalog = new();
    /// <summary>Label and area id of every choice in "Where" ("" is everything).</summary>
    public List<(string Label, string AreaId)> Scopes = new();
    /// <summary>Results by <see cref="SimResult.Key"/>: one per equipment setup and fight.</summary>
    public Dictionary<string, SimResult> Results = new();
    public string ScopeAreaId = "";
    public int Duration = 1;
    public bool Unlimited = true;

    public List<(string Id, string Name)> Loadouts = new();
    public string LoadoutId = "";
    /// <summary>Equipment slot id -> item id to wear there instead of the loadout's ("" is nothing).</summary>
    public Dictionary<string, string> Overrides = new();
    public List<GearSlot> Gear = new();

    /// <summary>Key of the selected fight (a <see cref="SimTarget.Key"/>).</summary>
    public string? Selected;
    public int SortColumn = 1;
    public bool SortDescending = true;
    public string FoodLine = "";
    public bool FoodMissing;
    public bool Running;
    public int Done, Total;
    public string Current = "", Message = "";

    public static readonly (string Label, double Hours)[] Durations =
        { ("15 minutes", 0.25), ("1 hour", 1), ("4 hours", 4), ("12 hours", 12) };

    public IEnumerable<SimTarget> InScope => Catalog.Where(t => ScopeAreaId == "" || t.AreaId == ScopeAreaId);

    public string SetupKey => LoadoutId + ";" + string.Join(",", Overrides.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => o.Key + "=" + o.Value));
    public string SetupLabel
    {
        get
        {
            string name = Loadouts.FirstOrDefault(l => l.Id == LoadoutId).Name ?? "Current equipment";
            return Overrides.Count == 0 ? name : $"{name} + {Overrides.Count} {(Overrides.Count == 1 ? "change" : "changes")}";
        }
    }

    public SimResult? ResultFor(SimTarget target) => Results.GetValueOrDefault(SimResult.MakeKey(SetupKey, target.Key));
}

/// <summary>Pictures, names and choices from the running game, looked up on the game's thread.</summary>
interface ISimLookup
{
    Texture2D? TargetIcon(SimTarget target);
    Texture2D? AmountIcon(Amount amount);
    (string Name, Texture2D? Icon) Skill(string id);
    /// <summary>Every item of the game that fits the slot.</summary>
    List<GearChoice> ChoicesFor(GearSlot slot);
}

/// <summary>
/// The simulator's window: what to simulate and with which equipment on the left, one line per fight in the
/// middle (click a column to sort, a line for its details), everything about the selected fight on the right.
/// All numbers are per hour of game time.
/// </summary>
sealed class SimView
{
    static readonly Color Bad = new("ff4500"), Good = new("5fd35f");

    sealed record Column(string Title, string Tip, float Width, Func<SimResult, double> Value, Func<SimResult, string> Text, Func<SimResult, Color> Tint);

    static readonly Column[] Columns =
    {
        new("Kills", "Enemies killed per hour", 58, r => r.KillsPerHour, r => Fmt(r.KillsPerHour), _ => GameStyle.TextWhite),
        new("XP", "Combat experience per hour, all combat skills together", 66, r => r.XpPerHour, r => Fmt(r.XpPerHour), _ => GameStyle.TextWhite),
        new("Gold", "Coins dropped plus what the dropped items sell for, per hour", 66, r => r.GoldPerHour, r => Fmt(r.GoldPerHour), _ => GameStyle.TextGold),
        new("Food", "Food eaten per hour", 52, r => r.FoodPerHour, r => Fmt(r.FoodPerHour), _ => GameStyle.TextWhite),
        new("Deaths", "Times you die per hour", 60, r => r.DeathsPerHour, r => r.Deaths == 0 ? "0" : Fmt(r.DeathsPerHour),
            r => r.Deaths == 0 ? GameStyle.TextDim : Bad),
        new("Low HP", "The lowest your hitpoints got, as a share of the maximum", 62, r => r.LowestHpFraction, r => r.LowestHpFraction.ToString("P0"),
            r => r.Deaths > 0 || r.LowestHpFraction < 0.15 ? Bad : r.LowestHpFraction < 0.35 ? GameStyle.TextGold : GameStyle.TextWhite),
    };

    readonly GameWindow _window;
    readonly GameStyle _s;
    readonly SimState _state;
    readonly ISimLookup _lookup;
    readonly Action _simulate, _cancel, _changed, _gearChanged;

    OptionButton _scope = null!, _duration = null!, _loadout = null!;
    Button _unlimited = null!, _fromBank = null!, _go = null!, _reset = null!;
    Label _supplyHint = null!, _estimate = null!, _status = null!, _empty = null!, _showing = null!;
    ProgressBar _progress = null!;
    VBoxContainer _rows = null!, _details = null!;
    Control _gearHolder = null!;
    PanelContainer? _picker;
    readonly List<Button> _headers = new();
    string _tableSignature = "", _detailsSignature = "";

    public SimView(GameWindow window, SimState state, ISimLookup lookup, Action simulate, Action cancel, Action changed, Action gearChanged)
    {
        _window = window; _s = window.Style; _state = state; _lookup = lookup;
        _simulate = simulate; _cancel = cancel; _changed = changed; _gearChanged = gearChanged;

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

        body.AddChild(Caption("Where"));
        _scope = _s.Dropdown();
        _scope.TooltipText = "An area simulates every enemy in it; a dungeon is simulated as a whole";
        _scope.ClipText = true;
        foreach (var (label, _) in _state.Scopes) _scope.AddItem(label);
        _scope.Selected = Math.Max(0, _state.Scopes.FindIndex(s => s.AreaId == _state.ScopeAreaId));
        _scope.ItemSelected += index =>
        {
            _state.ScopeAreaId = _state.Scopes[(int)index].AreaId;
            _state.Selected = null;
            _changed();
            Refresh();
        };
        body.AddChild(_scope);

        var timing = _s.Spaced(new HBoxContainer(), 6);
        var left = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 6);
        left.AddChild(Caption("Each fight lasts"));
        _duration = _s.Dropdown();
        _duration.TooltipText = "Game time, not waiting time: an hour of fighting takes well under a second";
        foreach (var (label, _) in SimState.Durations) _duration.AddItem(label);
        _duration.Selected = Math.Clamp(_state.Duration, 0, SimState.Durations.Length - 1);
        _duration.ItemSelected += index => { _state.Duration = (int)index; _changed(); Refresh(); };
        left.AddChild(_duration);
        timing.AddChild(left);
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

        body.AddChild(_s.Section("Equipment"));
        _loadout = _s.Dropdown();
        _loadout.TooltipText = "Fight with one of your saved loadouts. Simulate two of them to compare.";
        _loadout.ClipText = true;
        foreach (var (_, name) in _state.Loadouts) _loadout.AddItem(name);
        _loadout.Selected = Math.Max(0, _state.Loadouts.FindIndex(l => l.Id == _state.LoadoutId));
        _loadout.ItemSelected += index =>
        {
            _state.LoadoutId = _state.Loadouts[(int)index].Id;
            _state.Overrides.Clear();   // the changes were made to the other loadout's items
            ClosePicker();
            _gearChanged();
            FillGear();
            Refresh();
        };
        body.AddChild(_loadout);

        _gearHolder = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        body.AddChild(_gearHolder);
        _reset = _s.GameButton("Undo changes", "TexturedButton4x", 12);
        _reset.TooltipText = "Back to the loadout's own items";
        _reset.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        _reset.Pressed += () => { _state.Overrides.Clear(); ClosePicker(); _gearChanged(); FillGear(); Refresh(); };
        body.AddChild(_reset);
        Label food = Wrapped(_state.FoodLine, SetupWidth - 20, _state.FoodMissing ? Bad : GameStyle.TextGrey, 12);
        food.HorizontalAlignment = HorizontalAlignment.Center;
        body.AddChild(food);
        FillGear();

        body.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });

        _estimate = Wrapped("", SetupWidth - 20, GameStyle.TextGrey, 12);
        _estimate.HorizontalAlignment = HorizontalAlignment.Center;
        body.AddChild(_estimate);
        _go = _s.GameButton("Simulate", "TexturedActionButtonGreen8x", 18);
        _go.CustomMinimumSize = new Vector2(0, _s.Px(44));
        _go.Pressed += () => { ClosePicker(); if (_state.Running) _cancel(); else _simulate(); };
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
                BorderColor = slot.Changed ? new Color("ffd16d") : state == "hover" ? new Color("8d8c86") : new Color("524943"),
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
        if (slot.Icon == null) picture.Modulate = new Color(1, 1, 1, 0.22f);
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

    // ---- the item picker: every item of the game that fits the slot ---------------------------------------

    void ClosePicker()
    {
        if (_picker != null && GodotObject.IsInstanceValid(_picker)) _picker.QueueFree();
        _picker = null;
    }

    void OpenPicker(GearSlot slot, Control anchor)
    {
        ClosePicker();
        if (_state.Running) return;
        const float width = 330, height = 470;
        var panel = _picker = new PanelContainer { CustomMinimumSize = new Vector2(_s.Px(width), _s.Px(height)), ZIndex = 10 };
        panel.AddThemeStyleboxOverride("panel", _s.WindowPanel());
        var column = _s.Spaced(new VBoxContainer(), 6);
        panel.AddChild(column);

        var head = _s.Spaced(new HBoxContainer(), 6);
        Label title = _s.Text(slot.SlotName, GameStyle.TextWhite, 16, GameStyle.Bold);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        head.AddChild(title);
        Button close = _s.GameButton("X", "TexturedButtonCancel2x", 14);
        close.CustomMinimumSize = new Vector2(_s.Px(24), _s.Px(24));
        close.Pressed += ClosePicker;
        head.AddChild(close);
        column.AddChild(head);

        var search = new LineEdit { PlaceholderText = "Search items...", ClearButtonEnabled = true };
        search.AddThemeFontSizeOverride("font_size", _s.Px(14));
        column.AddChild(search);
        Button ownedOnly = Toggle("Only items I own", null, "Hide items that are neither in your bank nor equipped");
        ownedOnly.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        column.AddChild(ownedOnly);

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
            if (itemId == null) _state.Overrides.Remove(slot.SlotId);   // back to the loadout's own
            else _state.Overrides[slot.SlotId] = itemId;
            ClosePicker();
            _gearChanged();
            FillGear();
            Refresh();
        }

        var rows = new List<(Button Row, string Name, bool Owned)>();
        if (slot.Changed) list.AddChild(PickerRow("Back to the loadout's item", null, "", GameStyle.TextGold, () => Choose(null)));
        if (slot.ItemId != null) list.AddChild(PickerRow("Nothing (leave it empty)", null, "", GameStyle.TextGrey, () => Choose("")));
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

        _window.Layer.AddChild(panel);
        // Next to the slot that was clicked, kept on the screen.
        Vector2 screen = panel.GetViewportRect().Size, size = new(_s.Px(width), _s.Px(height));
        Vector2 at = anchor.GlobalPosition + new Vector2(anchor.Size.X + _s.Px(10), -_s.Px(60));
        panel.Position = new Vector2(Math.Clamp(at.X, 0, Math.Max(0, screen.X - size.X)), Math.Clamp(at.Y, 0, Math.Max(0, screen.Y - size.Y)));
        search.GrabFocus();
    }

    Button PickerRow(string name, Texture2D? icon, string right, Color tint, Action chosen)
    {
        var row = new Button
        {
            ThemeTypeVariation = "OutlineToggleButton", CustomMinimumSize = new Vector2(0, _s.Px(32)),
            FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        row.Pressed += chosen;
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
        float width = 250 + Columns.Sum(c => c.Width + 6) + 30;
        Control column = _s.Column("Results, per hour", out VBoxContainer body, width);
        column.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        _showing = _s.Text("", GameStyle.TextGrey, 12);
        _showing.HorizontalAlignment = HorizontalAlignment.Center;
        _showing.MouseFilter = Control.MouseFilterEnum.Ignore;
        body.AddChild(_showing);

        var header = _s.Spaced(new HBoxContainer(), 6);
        var headerMargin = new MarginContainer();
        headerMargin.AddThemeConstantOverride("margin_left", _s.Px(8));
        headerMargin.AddThemeConstantOverride("margin_right", _s.Px(8 + 12));   // the rows' own padding and the scroll bar
        headerMargin.AddChild(header);
        header.AddChild(HeaderButton("Enemy", "Sort by name", 0, -1));
        for (int i = 0; i < Columns.Length; i++) header.AddChild(HeaderButton(Columns[i].Title, Columns[i].Tip, Columns[i].Width, i));
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

        _empty = Wrapped("", width - 60, GameStyle.TextGrey, 14);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        body.AddChild(_empty);
        return column;
    }

    Button HeaderButton(string title, string tip, float width, int column)
    {
        var button = new Button
        {
            Text = title, Flat = true, TooltipText = tip, FocusMode = Control.FocusModeEnum.None,
            Alignment = column < 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        if (column < 0) button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        else button.CustomMinimumSize = new Vector2(_s.Px(width), 0);
        button.AddThemeFontSizeOverride("font_size", _s.Px(12));
        if (GameStyle.SemiBold is { } font) button.AddThemeFontOverride("font", font);
        button.Pressed += () =>
        {
            if (_state.SortColumn == column) _state.SortDescending = !_state.SortDescending;
            else { _state.SortColumn = column; _state.SortDescending = column >= 0; }
            _tableSignature = "";
            Refresh();
        };
        button.SetMeta("column", column);
        _headers.Add(button);
        return button;
    }

    void FillRows()
    {
        var results = _state.InScope.Select(_state.ResultFor).Where(r => r != null).Select(r => r!).ToList();
        IEnumerable<SimResult> sorted = _state.SortColumn < 0
            ? results.OrderBy(r => r.Target.Name, StringComparer.OrdinalIgnoreCase)
            : results.OrderBy(r => r.Failed ? double.MinValue : Columns[_state.SortColumn].Value(r));
        var ordered = (_state.SortDescending ? sorted.Reverse() : sorted).ToList();

        foreach (Button header in _headers)
        {
            bool active = header.GetMeta("column").AsInt32() == _state.SortColumn;
            Color color = active ? GameStyle.TextGold : GameStyle.TextGrey;
            foreach (string name in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
                header.AddThemeColorOverride(name, color);
        }

        string signature = _state.SetupKey + "#" + string.Join("|", ordered.Select(r => r.Target.Key + r.RealMilliseconds)) + "#" + _state.Selected + "#" + _state.SortColumn + _state.SortDescending;
        if (signature != _tableSignature)
        {
            _tableSignature = signature;
            foreach (Node old in _rows.GetChildren()) old.QueueFree();
            if (_state.Selected == null || ordered.All(r => r.Target.Key != _state.Selected))
                _state.Selected = ordered.FirstOrDefault()?.Target.Key;
            var group = new ButtonGroup();
            foreach (SimResult result in ordered) _rows.AddChild(BuildRow(result, group));
        }

        bool any = ordered.Count > 0;
        _rows.GetParent<Control>().Visible = any;
        _empty.Visible = !any;
        _showing.Text = "With: " + _state.SetupLabel;
        bool otherSetups = _state.Results.Values.Any(r => r.SetupKey != _state.SetupKey);
        _empty.Text = _state.Running ? "Simulating..." : _state.Catalog.Count == 0
            ? "No area is unlocked yet."
            : otherSetups ? "Nothing simulated with this equipment yet.\n\nPress Simulate to compare it with what you ran before."
            : "Choose where to fight and with which equipment on the left, then press Simulate.\n\nEvery fight is run with the game's own combat, on a hidden copy of your character. Nothing is saved.";
    }

    Control BuildRow(SimResult result, ButtonGroup group)
    {
        var row = new Button
        {
            ThemeTypeVariation = "OutlineToggleButton", ToggleMode = true, ButtonGroup = group,
            ButtonPressed = result.Target.Key == _state.Selected,
            CustomMinimumSize = new Vector2(0, _s.Px(30)), FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            TooltipText = result.Note,
        };
        row.Pressed += () => { _state.Selected = result.Target.Key; FillDetails(); };

        var line = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 6);
        line.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.Minsize, _s.Px(8));
        row.AddChild(line);

        line.AddChild(_s.Picture(_lookup.TargetIcon(result.Target), 20));
        Label name = _s.Text(result.Target.Name, result.Failed ? GameStyle.TextDim : GameStyle.TextWhite);
        name.ClipText = true;
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        name.MouseFilter = Control.MouseFilterEnum.Ignore;
        line.AddChild(name);
        Label level = _s.Text(result.Target.IsDungeon ? "Dungeon" : "Lv " + result.Target.CombatLevel, GameStyle.TextDim, 12);
        level.MouseFilter = Control.MouseFilterEnum.Ignore;
        line.AddChild(level);

        foreach (Column column in Columns)
        {
            Label cell = result.Failed
                ? _s.Text("-", GameStyle.TextDim)
                : _s.Text(column.Text(result), column.Tint(result), 14, GameStyle.SemiBold);
            cell.HorizontalAlignment = HorizontalAlignment.Right;
            cell.CustomMinimumSize = new Vector2(_s.Px(column.Width), 0);
            cell.MouseFilter = Control.MouseFilterEnum.Ignore;
            line.AddChild(cell);
        }
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
        return column;
    }

    void FillDetails()
    {
        SimResult? r = _state.Selected != null ? _state.Results.GetValueOrDefault(SimResult.MakeKey(_state.SetupKey, _state.Selected)) : null;
        var others = r == null ? new List<SimResult>()
            : _state.Results.Values.Where(o => o.Target.Key == r.Target.Key && o.SetupKey != r.SetupKey && !o.Failed).ToList();
        string signature = r == null ? "none" : r.Key + r.RealMilliseconds + "/" + string.Join(",", others.Select(o => o.Key + o.RealMilliseconds));
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

        // Who, where, with what.
        var head = _s.Spaced(new HBoxContainer(), 10);
        head.AddChild(_s.Picture(_lookup.TargetIcon(r.Target), 44));
        var title = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 0);
        Label name = _s.Text(r.Target.Name, GameStyle.TextWhite, 16, GameStyle.Bold);
        name.ClipText = true;
        title.AddChild(name);
        title.AddChild(_s.Text(r.Target.IsDungeon ? r.Target.CategoryName : $"{r.Target.AreaName}  ·  Level {r.Target.CombatLevel}", GameStyle.TextGrey, 12));
        Label with = _s.Text(r.SetupLabel, GameStyle.TextDim, 12);
        with.ClipText = true;
        title.AddChild(with);
        head.AddChild(title);
        _details.AddChild(head);

        if (r.Note != "") _details.AddChild(Wrapped(r.Note, 250, r.Failed ? Bad : GameStyle.TextGold, 12));
        if (r.Failed) return;

        // The same fight with other equipment, when it was simulated: this one against each of them.
        if (others.Count > 0)
        {
            _details.AddChild(_s.Section("Compared with"));
            foreach (SimResult other in others.OrderByDescending(o => o.XpPerHour).Take(4))
            {
                // The other setup's name on a line of its own: it can be long.
                Label versus = _s.Text(other.SetupLabel, GameStyle.TextGrey, 12);
                versus.ClipText = true;
                versus.TooltipText = other.SetupLabel;
                versus.MouseFilter = Control.MouseFilterEnum.Pass;
                _details.AddChild(versus);
                _details.AddChild(Stat("Experience", Change(r.XpPerHour, other.XpPerHour), Tint(r.XpPerHour, other.XpPerHour)));
                _details.AddChild(Stat("Kills", Change(r.KillsPerHour, other.KillsPerHour), Tint(r.KillsPerHour, other.KillsPerHour)));
                _details.AddChild(Stat("Gold", Change(r.GoldPerHour, other.GoldPerHour), Tint(r.GoldPerHour, other.GoldPerHour)));
                // Less food eaten is the good direction.
                _details.AddChild(Stat("Food eaten", Change(r.FoodPerHour, other.FoodPerHour), Tint(other.FoodPerHour, r.FoodPerHour)));
            }
        }

        _details.AddChild(_s.Section("Per hour"));
        _details.AddChild(Stat("Kills", Fmt(r.KillsPerHour), null, r.Kills > 0 ? $"one every {r.SecondsPerKill:0.#}s" : null));
        if (r.Target.IsDungeon) _details.AddChild(Stat("Dungeon runs", Fmt(r.PerHour(r.DungeonRuns))));
        _details.AddChild(Stat("Combat XP", Fmt(r.XpPerHour)));
        _details.AddChild(Stat("Gold", Fmt(r.GoldPerHour), GameStyle.TextGold, $"{Fmt(r.CoinsPerHour)} coins + {Fmt(r.LootValuePerHour)} loot"));

        if (r.Xp.Count > 0)
        {
            _details.AddChild(_s.Section("Experience"));
            foreach (var (id, xp) in r.Xp.OrderByDescending(x => x.Value))
            {
                var (skill, icon) = _lookup.Skill(id);
                _details.AddChild(Stat(skill, Fmt(r.PerHour(xp)), null, null, icon));
            }
        }

        _details.AddChild(_s.Section("Survival"));
        _details.AddChild(Stat("Deaths", r.Deaths == 0 ? "none" : Fmt(r.DeathsPerHour), r.Deaths == 0 ? Good : Bad,
            r.Deaths > 0 ? $"first after {Clock(r.FirstDeathSeconds)}" : null));
        _details.AddChild(Stat("Lowest hitpoints", $"{r.LowestHp:N0} / {r.MaxHp:N0}",
            r.LowestHpFraction < 0.15 ? Bad : r.LowestHpFraction < 0.35 ? GameStyle.TextGold : GameStyle.TextWhite, r.LowestHpFraction.ToString("P0")));
        _details.AddChild(Stat("Damage taken", Fmt(r.PerHour(r.DamageTaken))));
        _details.AddChild(Stat("Healed by food", Fmt(r.PerHour(r.Healed)), null, $"{Fmt(r.FoodPerHour)} eaten"));

        if (r.Used.Count > 0)
        {
            _details.AddChild(_s.Section("Used"));
            foreach (Amount used in r.Used.Take(8))
                _details.AddChild(Stat(used.Name, "-" + Fmt(r.PerHour(used.Quantity)), Bad, null, _lookup.AmountIcon(used)));
        }

        var drops = r.Loot.Where(l => !l.IsCurrency || l.Id != SimEngine.Gold).ToList();
        if (drops.Count > 0)
        {
            _details.AddChild(_s.Section("Loot"));
            foreach (Amount drop in drops.Take(12))
                _details.AddChild(Stat(drop.Name, "+" + Fmt(r.PerHour(drop.Quantity)), null,
                    drop.SellsFor > 0 ? $"{Fmt(r.PerHour(drop.Quantity * drop.SellsFor))} gold" : null, _lookup.AmountIcon(drop)));
            if (drops.Count > 12) _details.AddChild(_s.Text($"and {drops.Count - 12} more", GameStyle.TextDim, 12));
        }

        Label foot = Wrapped($"{Clock(r.Hours * 3600)} of fighting simulated in {r.RealMilliseconds / 1000:0.0}s.", 250, GameStyle.TextDim, 11);
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

    static Color Tint(double mine, double theirs) =>
        theirs <= 0 || Math.Abs(mine / theirs - 1) < 0.005 ? GameStyle.TextWhite : mine > theirs ? Good : Bad;

    // "[icon] Name                          value"
    // "                                     note"
    Control Stat(string name, string value, Color? tint = null, string? note = null, Texture2D? icon = null)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", _s.StatLine());
        var line = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 6);
        if (icon != null) line.AddChild(_s.Picture(icon, 18));
        Label label = _s.Text(name);
        label.ClipText = true;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        line.AddChild(label);
        var right = _s.Spaced(new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, -2);
        Label number = _s.Text(value, tint ?? GameStyle.TextWhite, 14, GameStyle.Bold);
        number.HorizontalAlignment = HorizontalAlignment.Right;
        right.AddChild(number);
        if (note != null)
        {
            Label small = _s.Text(note, GameStyle.TextGrey, 11);
            small.HorizontalAlignment = HorizontalAlignment.Right;
            right.AddChild(small);
        }
        line.AddChild(right);
        panel.AddChild(line);
        return panel;
    }

    // ---- keeping it current --------------------------------------------------------------------------------

    public void Refresh()
    {
        if (!_window.IsAlive) return;
        var (_, hours) = SimState.Durations[Math.Clamp(_state.Duration, 0, SimState.Durations.Length - 1)];
        int fights = _state.InScope.Count();

        _supplyHint.Text = _state.Unlimited
            ? "Never runs out: what a fight gives and uses while you keep it supplied."
            : "Uses your bank: how far your food, ammo and runes get you.";
        // A copy of the character takes about a second per fight; the fighting itself is the small part.
        double seconds = fights * (1.25 + 0.25 * hours);
        _estimate.Text = fights == 0 ? "Nothing to simulate here"
            : $"{fights} {(fights == 1 ? "fight" : "fights")}  ·  about {(seconds < 90 ? $"{Math.Max(1, Math.Round(seconds))}s" : $"{Math.Round(seconds / 60)} min")}";

        _go.Text = _state.Running ? "Cancel" : "Simulate";
        _go.ThemeTypeVariation = _state.Running ? "TexturedButtonCancel8x" : "TexturedActionButtonGreen8x";
        _go.Disabled = !_state.Running && fights == 0;
        _scope.Disabled = _duration.Disabled = _loadout.Disabled = _unlimited.Disabled = _fromBank.Disabled = _reset.Disabled = _state.Running;
        _progress.Value = _state.Total > 0 ? (double)_state.Done / _state.Total : 0;
        _progress.Modulate = _state.Running || _state.Done > 0 ? Colors.White : new Color(1, 1, 1, 0.35f);
        _status.Text = _state.Message != "" ? _state.Message
            : _state.Running ? $"{_state.Done} of {_state.Total}  ·  {_state.Current}"
            : _state.Total > 0 && _state.Done >= _state.Total ? $"Done: {_state.Total} {(_state.Total == 1 ? "fight" : "fights")}" : "";
        _status.AddThemeColorOverride("font_color", _state.Message != "" ? Bad : GameStyle.TextGrey);

        FillRows();
        FillDetails();
    }

    /// <summary>Development: opens the item picker of the first slot whose name contains the text.</summary>
    public void DevOpenPicker(string slotName)
    {
        GearSlot? slot = _state.Gear.FirstOrDefault(g => g.SlotName.Contains(slotName, StringComparison.OrdinalIgnoreCase));
        if (slot != null) OpenPicker(slot, _gearHolder);
    }

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
        >= 1_000_000 => (value / 1_000_000).ToString("0.##") + "M",
        >= 100_000 => (value / 1000).ToString("0.#") + "K",
        >= 100 => value.ToString("N0"),
        >= 10 => value.ToString("0.#"),
        0 => "0",
        _ => value.ToString("0.##"),
    };

    static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? (t.Minutes == 0 ? $"{(int)t.TotalHours}h" : $"{(int)t.TotalHours}h {t.Minutes}m")
             : t.TotalMinutes >= 1 ? (t.Seconds == 0 ? $"{t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s") : $"{t.Seconds}s";
    }
}
