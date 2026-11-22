using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Data.Recipes;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Combat;
using Godot;
using MelvorMods.Shared;

namespace ItemEncyclopedia;

/// <summary>What the window shows and remembers; owned by the mod, so it survives a rebuild of the window.</summary>
sealed class ViewState
{
    public Query Query = new();
    public bool FiltersOpen;
    /// <summary>The filter groups that are folded away.</summary>
    public HashSet<string> Folded = new() { "weapons", "skills", "sources" };
    public Entry? Selected;
    /// <summary>The items looked at before this one, for the Back button.</summary>
    public readonly List<Entry> Back = new();
    public int Scroll;
    public bool IndexFailed;

    public const int RecentKept = 5;
    /// <summary>The last few different searches and filter choices the player settled on, newest first.</summary>
    public List<Query> Recent = new();

    /// <summary>
    /// Keeps what is asked right now as the newest of the recent ones. Called when the player has done
    /// something with the result (opened an item, cleared the filters, put the window away), so the steps on
    /// the way to a choice, "Ranged" before "Ranged, Bow", are not kept. True when the list changed.
    /// </summary>
    public bool Note(Query asked)
    {
        if (!asked.Narrows) return false;
        string key = asked.Key();
        if (Recent.Count > 0 && Recent[0].Key() == key) return false;
        Recent.RemoveAll(r => r.Key() == key);
        Recent.Insert(0, asked.Copy());
        if (Recent.Count > RecentKept) Recent.RemoveRange(RecentKept, Recent.Count - RecentKept);
        return true;
    }
}

/// <summary>
/// The encyclopedia's window: a search box and the sort order on top, the filters on the left when they are
/// opened, every item that matches in the middle, and everything about the selected one on the right.
/// </summary>
sealed class EncyclopediaView
{
    // The scene of the game's own item panel: the details show the item with it, so every stat, bonus and
    // requirement reads exactly as in the game's tooltips.
    const string ItemPanelScene = "res://Entities/Bank/item_info_panel.tscn";
    const int Rows = 16, GroupLines = 30;
    const float FiltersWidth = 292, DetailsWidth = 440, Width = 1280, Height = 620;

    sealed class Row
    {
        public Button Button = null!;
        public TextureRect Icon = null!;
        public Label Name = null!, Note = null!;
        public Entry? Entry;
    }

    readonly GameWindow _window;
    readonly GameStyle _s;
    readonly Catalog _catalog;
    readonly ViewState _state;
    readonly Action _changed, _rebuild;
    readonly Action<string> _log;
    Query Q => _state.Query;

    LineEdit _search = null!;
    Button _filtersButton = null!, _reset = null!, _back = null!;
    Label _count = null!, _empty = null!;
    VScrollBar _scroll = null!;
    VBoxContainer _details = null!;
    VBoxContainer? _recent;
    ScrollContainer _detailsScroll = null!;
    readonly List<Row> _pool = new();
    sealed record FilterGroup(Control Content, TextureRect Arrow, Label Name, Control Tag, Label Number, Func<int> Count);
    readonly List<FilterGroup> _groups = new();
    readonly Dictionary<string, string> _names = new();
    List<Entry> _found = new();
    int _columns;
    ItemInfoPanel? _gamePanel;
    string _shownHaving = "";
    static bool s_panelFailed, s_detailsFailed;

    public EncyclopediaView(GameWindow window, Catalog catalog, ViewState state, Action changed, Action rebuild, Action<string> log)
    {
        _window = window; _s = window.Style; _catalog = catalog; _state = state; _changed = changed; _rebuild = rebuild; _log = log;
        foreach (Choice choice in catalog.Types.Concat(catalog.Slots).Concat(catalog.Styles).Concat(catalog.Weapons).Concat(catalog.Skills)
                     .Concat(catalog.Sources).Concat(catalog.Areas.SelectMany(a => a.Areas)))
            _names[choice.Id] = choice.Name;
        // A choice that is gone (a game update, another character) must not narrow the list from where it cannot
        // be seen. Where something comes from is only known once the game's data has been read.
        foreach (HashSet<string> chosen in new[] { Q.Types, Q.Slots, Q.Styles, Q.Weapons, Q.Skills }) chosen.RemoveWhere(id => !_names.ContainsKey(id));
        if (catalog.Index != null)
        {
            Q.Sources.RemoveWhere(id => !_names.ContainsKey(id));
            if (!_names.ContainsKey(Q.Area)) Q.Area = "";
        }

        window.Body.AddChild(BuildBar());
        var columns = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass, CustomMinimumSize = new Vector2(_s.Px(Width), _s.Px(Height)) }, 10);
        if (state.FiltersOpen) columns.AddChild(BuildFilters());
        columns.AddChild(BuildItems());
        columns.AddChild(BuildDetails());
        window.Body.AddChild(columns);
        Refresh();
        FillDetails();
    }

    /// <summary>Lets go of the game's item panel before the window is thrown away.</summary>
    public void Release()
    {
        if (_gamePanel != null && GodotObject.IsInstanceValid(_gamePanel)) { try { _gamePanel.Unbind(); } catch { } }
        _gamePanel = null;
    }

    // ---- the bar on top: search, sort order, the way to the filters --------------------------------------------

    Control BuildBar()
    {
        var bar = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass }, 8);
        _search = _s.SearchBox("Search by name or description...");
        _search.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _search.Text = Q.Search;

        _search.TextChanged += text => { Q.Search = text; _state.Scroll = 0; Changed(); };
        bar.AddChild(_search);

        bar.AddChild(Caption("Sort by"));
        OptionButton sort = _s.Dropdown();
        sort.TooltipText = "The order of the list";
        foreach (string name in Catalog.SortNames) sort.AddItem(name);
        sort.Selected = Math.Clamp(Q.Sort, 0, Catalog.SortNames.Length - 1);
        sort.CustomMinimumSize = new Vector2(_s.Px(170), 0);
        sort.ItemSelected += index => { Q.Sort = (int)index; _state.Scroll = 0; Changed(); };
        bar.AddChild(sort);

        _filtersButton = _s.GameButton("Filters", "OutlineToggleButton", 13);
        _filtersButton.ToggleMode = true;
        _filtersButton.ButtonPressed = _state.FiltersOpen;
        _filtersButton.FocusMode = Control.FocusModeEnum.None;
        _filtersButton.CustomMinimumSize = new Vector2(_s.Px(120), 0);
        _filtersButton.Icon = GameStyle.Icon("filter");
        _filtersButton.AddThemeConstantOverride("icon_max_width", _s.Px(14));
        _filtersButton.AddThemeConstantOverride("h_separation", _s.Px(6));
        _filtersButton.TooltipText = "Narrow the list by kind, slot, combat style, weapon, skill or where it comes from";
        _filtersButton.Pressed += () => { _state.FiltersOpen = !_state.FiltersOpen; _changed(); _rebuild(); };
        bar.AddChild(_filtersButton);

        _reset = _s.GameButton("Reset", "TexturedButton4x", 12);
        _reset.TooltipText = "Clear the search and every filter";
        _reset.Pressed += () => { _state.Note(Q); Q.Reset(); _state.Scroll = 0; _changed(); _rebuild(); };
        bar.AddChild(_reset);
        return bar;
    }

    // ---- left: the filters ---------------------------------------------------------------------------------------

    Control BuildFilters()
    {
        Control column = _s.Column("Filters", out VBoxContainer body, FiltersWidth);
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var list = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 10);
        // Room for the scroll bar, so it does not lie on the choices.
        var room = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        room.AddThemeConstantOverride("margin_right", _s.Px(10));
        room.AddChild(list);
        scroll.AddChild(room);
        body.AddChild(scroll);

        // What was asked before, one click away. The group is only there once there is something in it.
        _recent = Group(list, "recent", "Recent", () => 0);
        FillRecent();

        // Yours: what the character has to do with the items.
        VBoxContainer show = Group(list, "show", "Show", () => (Q.Owned ? 1 : 0) + (Q.Found ? 1 : 0) + (Q.Missing ? 1 : 0) + (Q.SpoilerFree ? 1 : 0));
        GridContainer ticks = Grid(show, 2);
        Button owned = Tick(ticks, "Owned", "Only what is in your bank or equipped right now", Q.Owned);
        Button found = Tick(ticks, "Found", "Only what you have found so far, as the game's completion log counts it", Q.Found);
        Button missing = Tick(ticks, "Not found yet", "Only what you have never had: what is still missing from the completion log", Q.Missing);
        Button spoilers = Tick(ticks, "No spoilers", "Items you have not found yet stay in the list as ???, without their picture and details", Q.SpoilerFree);
        owned.Toggled += on => { Q.Owned = on; Narrowed(); };
        // Found and not found yet cannot both be asked for: ticking one clears the other.
        found.Toggled += on => { Q.Found = on; if (on && missing.ButtonPressed) missing.ButtonPressed = false; else Narrowed(); };
        missing.Toggled += on => { Q.Missing = on; if (on && found.ButtonPressed) found.ButtonPressed = false; else Narrowed(); };
        spoilers.Toggled += on => { Q.SpoilerFree = on; Narrowed(); FillDetails(); };

        Choices(list, "types", "Kind", _catalog.Types, Q.Types, 2);
        Choices(list, "slots", "Slot", _catalog.Slots, Q.Slots, 3);
        Choices(list, "styles", "Combat style", _catalog.Styles, Q.Styles, 3);
        Choices(list, "weapons", "Weapon", _catalog.Weapons, Q.Weapons, 2);

        // A skill gives items, takes items and has tools: which of the three is meant can be said.
        VBoxContainer skills = Group(list, "skills", "Skill", () => Q.Skills.Count);
        var modes = _s.Spaced(new HBoxContainer(), 2);
        var modeGroup = new ButtonGroup();
        string[] tips =
        {
            "Everything to do with the skill", "What the skill gives: made, gathered, dropped or sold there",
            "What the skill takes: its ingredients and costs", "What you work with: the skill's tools, potions and clothes; for Combat, weapons and armour",
        };
        for (int i = 0; i < Catalog.SkillModes.Length; i++)
        {
            int mode = i;
            Button button = _s.GameButton(Catalog.SkillModes[i], "OutlineToggleButton", 11);
            button.ToggleMode = true;
            button.ButtonGroup = modeGroup;
            button.ButtonPressed = Q.SkillMode == i;
            button.FocusMode = Control.FocusModeEnum.None;
            button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            button.TooltipText = tips[i];
            button.Pressed += () => { Q.SkillMode = mode; Narrowed(); };
            modes.AddChild(button);
        }
        skills.AddChild(modes);
        Fill(Grid(skills, 2), _catalog.Skills, Q.Skills);

        VBoxContainer sources = Group(list, "sources", "Comes from", () => Q.Sources.Count + (Q.Area != "" ? 1 : 0));
        if (_catalog.Index == null)
            sources.AddChild(Note(_state.IndexFailed ? "The game's data could not be read: see MelvorModLoader.log." : "Reading the game's data..."));
        Fill(Grid(sources, 2), _catalog.Sources, Q.Sources);
        OptionButton area = _s.Dropdown();
        area.ClipText = true;
        area.FitToLongestItem = false;
        area.TooltipText = "Only what drops in one area or dungeon: from its enemies, or as its reward";
        area.AddItem("Drops in: any area or dungeon");
        area.SetItemMetadata(0, "");
        foreach (var (category, areas) in _catalog.Areas)
        {
            area.AddSeparator(category);
            foreach (Choice choice in areas)
            {
                area.AddItem(choice.Name);
                area.SetItemMetadata(area.ItemCount - 1, choice.Id);
                if (choice.Id == Q.Area) area.Selected = area.ItemCount - 1;
            }
        }
        area.Visible = _catalog.Index != null;
        area.ItemSelected += index => { Q.Area = area.GetItemMetadata((int)index).AsString(); Narrowed(); };
        sources.AddChild(area);
        return column;
    }

    void FillRecent()
    {
        if (_recent == null || !GodotObject.IsInstanceValid(_recent)) return;
        foreach (Node old in _recent.GetChildren()) { _recent.RemoveChild(old); old.QueueFree(); }
        int shown = 0;
        foreach (Query earlier in _state.Recent)
        {
            string words = string.Join(", ", Words(earlier));
            if (words == "") continue;   // every choice of it is gone from the game
            shown++;
            var row = new Button
            {
                ThemeTypeVariation = "OutlineToggleButton", CustomMinimumSize = new Vector2(0, _s.Px(28)), FocusMode = Control.FocusModeEnum.None,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand, TooltipText = words + "\nClick to ask this again",
            };
            var line = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 5);
            line.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.Minsize, _s.Px(5));
            row.AddChild(line);
            if (GameStyle.Icon("history") is { } clock) line.AddChild(_s.Picture(clock, 16));
            Label name = Clipped(_s.Text(words, GameStyle.TextWhite, 12));
            name.MouseFilter = Control.MouseFilterEnum.Ignore;
            line.AddChild(name);
            row.Pressed += () =>
            {
                // What is asked now goes into the list too, so it is one click back.
                _state.Note(Q);
                Q.Take(earlier);
                _state.Note(Q);
                _state.Scroll = 0;
                _changed();
                _rebuild();
            };
            _recent.AddChild(row);
        }
        _recent.GetParent<Control>().Visible = shown > 0;
    }

    /// <summary>What a search and its filters ask, in words: "rune", Owned, Ranged, Bow, Murkwood Lair.</summary>
    List<string> Words(Query q)
    {
        var words = new List<string>();
        if (q.Search.Trim() != "") words.Add("\"" + q.Search.Trim() + "\"");
        if (q.Owned) words.Add("Owned");
        if (q.Found) words.Add("Found");
        if (q.Missing) words.Add("Not found yet");
        foreach (HashSet<string> set in new[] { q.Types, q.Slots, q.Styles, q.Weapons, q.Skills, q.Sources })
        {
            var names = set.Select(id => _names.GetValueOrDefault(id, "")).Where(n => n != "").OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            // "Cooking (uses it)": which side of the skill was meant.
            if (ReferenceEquals(set, q.Skills) && q.SkillMode != 0 && names.Count > 0) names[^1] += $" ({Catalog.SkillModes[q.SkillMode].ToLowerInvariant()})";
            words.AddRange(names);
        }
        if (q.Area != "" && _names.TryGetValue(q.Area, out string? where)) words.Add(where);
        return words;
    }

    /// <summary>The player did something with what the list shows: what was asked is worth keeping.</summary>
    public void NoteAsked()
    {
        if (!_state.Note(Q)) return;
        FillRecent();
        _changed();
    }

    void Choices(VBoxContainer list, string id, string title, List<Choice> choices, HashSet<string> chosen, int columns) =>
        Fill(Grid(Group(list, id, title, () => chosen.Count), columns), choices, chosen);

    void Fill(GridContainer grid, List<Choice> choices, HashSet<string> chosen)
    {
        foreach (Choice choice in choices)
        {
            Button chip = Chip(choice, chosen.Contains(choice.Id));
            chip.Toggled += on => { if (on) chosen.Add(choice.Id); else chosen.Remove(choice.Id); Narrowed(); };
            grid.AddChild(chip);
        }
    }

    GridContainer Grid(Container into, int columns)
    {
        var grid = new GridContainer { Columns = columns, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", _s.Px(4));
        grid.AddThemeConstantOverride("v_separation", _s.Px(4));
        into.AddChild(grid);
        return grid;
    }

    // A group of filters under its heading: "v Kind                    2", and the game's fading line.
    // The heading folds the group away; the number says how many of its choices are ticked.
    VBoxContainer Group(VBoxContainer list, string id, string title, Func<int> count)
    {
        var box = _s.Spaced(new VBoxContainer(), 4);
        var header = new Button
        {
            CustomMinimumSize = new Vector2(0, _s.Px(24)), FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, TooltipText = "Show or fold away",
        };
        foreach (string state in new[] { "normal", "hover", "pressed", "focus" }) header.AddThemeStyleboxOverride(state, new StyleBoxEmpty());
        var line = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 6);
        line.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        header.AddChild(line);
        TextureRect arrow = _s.Picture(GameStyle.DropArrow, 10);
        arrow.PivotOffset = new Vector2(_s.Px(10), _s.Px(10)) / 2;
        line.AddChild(arrow);
        Label name = _s.Text(title, GameStyle.TextPaper, 14, GameStyle.Bold);
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        line.AddChild(name);
        PanelContainer tag = _s.CountTag(null, out Label number);
        tag.MouseFilter = Control.MouseFilterEnum.Ignore;
        tag.GetNode<Control>("Row").GetChild<Control>(0).Visible = false;   // the tag's place for a picture
        number.AddThemeColorOverride("font_color", GameStyle.Highlight);
        number.AddThemeFontSizeOverride("font_size", _s.Px(12));
        line.AddChild(tag);
        box.AddChild(header);
        box.AddChild(_s.FadingLine());

        var content = _s.Spaced(new VBoxContainer { Visible = !_state.Folded.Contains(id) || count() > 0 }, 6);
        box.AddChild(content);
        header.Pressed += () =>
        {
            content.Visible = !content.Visible;
            if (content.Visible) _state.Folded.Remove(id); else _state.Folded.Add(id);
            SyncGroups();
            _changed();
        };
        header.MouseEntered += () => name.AddThemeColorOverride("font_color", GameStyle.TextWhite);
        header.MouseExited += SyncGroups;
        _groups.Add(new FilterGroup(content, arrow, name, tag, number, count));
        list.AddChild(box);
        return content;
    }

    void SyncGroups()
    {
        foreach (FilterGroup group in _groups)
        {
            int n = group.Count();
            group.Arrow.RotationDegrees = group.Content.Visible ? 0 : -90;
            group.Arrow.Modulate = new Color(1, 1, 1, group.Content.Visible ? 0.9f : 0.55f);
            group.Name.AddThemeColorOverride("font_color", n > 0 ? GameStyle.TextGold : GameStyle.TextPaper);
            group.Tag.Visible = n > 0;
            group.Number.Text = n.ToString();
        }
    }

    Button Tick(GridContainer grid, string text, string tip, bool on)
    {
        Button box = _s.TickBox(text, on);
        box.TooltipText = tip;
        box.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        grid.AddChild(box);
        return box;
    }

    // One choice: a picture and a name on one of the game's outlined buttons, as wide as its column.
    Button Chip(Choice choice, bool on)
    {
        var chip = new Button
        {
            ThemeTypeVariation = "OutlineToggleButton", ToggleMode = true, ButtonPressed = on, FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, CustomMinimumSize = new Vector2(0, _s.Px(28)),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = choice.Tip == "" ? choice.Name : choice.Name + "\n" + choice.Tip,
        };
        var line = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 5);
        line.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.Minsize, _s.Px(5));
        chip.AddChild(line);
        if (choice.Icon != null) line.AddChild(_s.Picture(choice.Icon, 18));
        Label name = Clipped(_s.Text(choice.Name, GameStyle.TextWhite, 12));
        name.MouseFilter = Control.MouseFilterEnum.Ignore;
        if (choice.Icon == null) name.HorizontalAlignment = HorizontalAlignment.Center;
        line.AddChild(name);
        return chip;
    }

    /// <summary>A filter was ticked or cleared: the list starts from its top again.</summary>
    void Narrowed()
    {
        _state.Scroll = 0;
        Changed();
    }

    // ---- middle: the items -----------------------------------------------------------------------------------------

    Control BuildItems()
    {
        Control column = _s.Column("Items", out VBoxContainer body);
        column.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _count = Clipped(_s.Text("", GameStyle.TextGrey, 12));
        _count.MouseFilter = Control.MouseFilterEnum.Pass;
        body.AddChild(_count);

        // A fixed set of rows that is filled again as the list moves: the game has thousands of items.
        _columns = _state.FiltersOpen ? 2 : 3;
        var holder = _s.Spaced(new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill }, 4);
        var stack = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, ClipContents = true };
        stack.GuiInput += OnWheel;
        var grid = new GridContainer { Columns = _columns };
        grid.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        grid.AddThemeConstantOverride("h_separation", _s.Px(4));
        grid.AddThemeConstantOverride("v_separation", _s.Px(2));
        for (int i = 0; i < Rows * _columns; i++) grid.AddChild(BuildRow());
        stack.AddChild(grid);
        // The rows share the height there is: text does not shrink in exact step with the window.
        stack.Resized += () =>
        {
            float each = MathF.Floor((stack.Size.Y - (Rows - 1) * _s.Px(2)) / Rows);
            if (each < 10) return;
            foreach (Row row in _pool) row.Button.CustomMinimumSize = new Vector2(0, each);
        };
        _empty = _s.Text("", GameStyle.TextGrey, 14);
        _empty.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.Minsize, _s.Px(30));
        _empty.MouseFilter = Control.MouseFilterEnum.Ignore;
        stack.AddChild(_empty);
        holder.AddChild(stack);

        _scroll = new VScrollBar { MinValue = 0, Step = 1, Page = Rows, CustomMinimumSize = new Vector2(_s.Px(12), 0) };
        _scroll.ValueChanged += value => { _state.Scroll = (int)value; Fill(); };
        holder.AddChild(_scroll);
        body.AddChild(holder);
        return column;
    }

    Button BuildRow()
    {
        var row = new Row();
        var button = row.Button = new Button
        {
            ThemeTypeVariation = "OutlineToggleButton", ToggleMode = true, CustomMinimumSize = new Vector2(0, _s.Px(32)),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        var line = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 6);
        line.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.Minsize, _s.Px(6));
        button.AddChild(line);
        line.AddChild(row.Icon = _s.Picture(null, 24));
        row.Name = Clipped(_s.Text(""));
        row.Name.MouseFilter = Control.MouseFilterEnum.Ignore;
        line.AddChild(row.Name);
        row.Note = _s.Text("", GameStyle.TextPaper, 12);
        row.Note.MouseFilter = Control.MouseFilterEnum.Ignore;
        line.AddChild(row.Note);
        button.Pressed += () => { if (row.Entry != null) { NoteAsked(); Select(row.Entry, false); } };
        button.GuiInput += OnWheel;
        _pool.Add(row);
        return button;
    }

    void OnWheel(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true } wheel) return;
        if (wheel.ButtonIndex != MouseButton.WheelDown && wheel.ButtonIndex != MouseButton.WheelUp) return;
        _scroll.Value += wheel.ButtonIndex == MouseButton.WheelDown ? 3 : -3;
        // Handled here: a row passes the wheel on to the list behind it, which would move it a second time.
        _scroll.GetViewport().SetInputAsHandled();
    }

    bool Hidden(Entry entry) => Q.SpoilerFree && !entry.Found;

    void Fill()
    {
        int first = (int)_scroll.Value * _columns;
        for (int i = 0; i < _pool.Count; i++)
        {
            Row row = _pool[i];
            Entry? entry = first + i < _found.Count ? _found[first + i] : null;
            row.Entry = entry;
            // A row without an item keeps its place, so the others keep their width.
            row.Button.Modulate = entry == null ? Colors.Transparent : Colors.White;
            row.Button.MouseFilter = entry == null ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Stop;
            if (entry == null) { row.Button.SetPressedNoSignal(false); row.Button.TooltipText = ""; continue; }
            bool hidden = Hidden(entry);
            row.Icon.Texture = entry.Item.Icon;
            row.Icon.Modulate = hidden ? new Color(0, 0, 0, 0.75f) : Colors.White;
            row.Name.Text = hidden ? "???" : entry.Name;
            // White for what you have, grey for what you have had, dim for what you never found.
            row.Name.AddThemeColorOverride("font_color", entry.Owned > 0 ? GameStyle.TextWhite : entry.Found ? GameStyle.TextGrey : GameStyle.TextDim);
            row.Note.Text = hidden ? "" : Q.Sort switch
            {
                3 or 4 => entry.Level > 0 ? "Lv " + entry.Level : "",
                5 or 6 => entry.Price > 0 ? entry.Price.ToString("N0") : "",
                _ => entry.Owned > 0 ? "x" + Amount(entry.Owned) : "",
            };
            row.Note.AddThemeColorOverride("font_color", Q.Sort is 5 or 6 ? GameStyle.TextGold : GameStyle.TextPaper);
            row.Button.TooltipText = hidden ? "Not found yet" : entry.Owned > 0 ? $"{entry.Name}\nYou have {entry.Owned:N0}" : entry.Name;
            row.Button.SetPressedNoSignal(ReferenceEquals(entry, _state.Selected));
        }
    }

    /// <summary>Filters and sorts again, and shows the result.</summary>
    public void Refresh()
    {
        if (!_window.IsAlive) return;
        _found = _catalog.Find(Q);
        int lines = (_found.Count + _columns - 1) / _columns;
        _scroll.MaxValue = Math.Max(lines, Rows);
        _scroll.Modulate = lines > Rows ? Colors.White : new Color(1, 1, 1, 0.25f);
        _scroll.SetValueNoSignal(Math.Clamp(_state.Scroll, 0, Math.Max(0, lines - Rows)));
        _state.Scroll = (int)_scroll.Value;
        Fill();

        bool waiting = _catalog.Index == null && (Q.Sources.Count > 0 || Q.Area != "");
        _empty.Visible = _found.Count == 0;
        _empty.Text = waiting && !_state.IndexFailed ? "Reading the game's data..."
            : waiting ? "The game's data could not be read: see MelvorModLoader.log."
            : "No item matches.\n\nTry fewer filters, or press Reset.";

        // The filters in words; the search is in its box, right above.
        List<string> chosen = Words(Q);
        if (Q.Search.Trim() != "") chosen.RemoveAt(0);
        _count.Text = $"{_found.Count:N0} of {_catalog.Items.Count:N0} items" + (chosen.Count > 0 ? "   ·   " + string.Join(", ", chosen) : "");
        _count.TooltipText = chosen.Count > 0 ? "Filters: " + string.Join(", ", chosen) : "";
        _filtersButton.Text = Q.Active > 0 ? $"Filters ({Q.Active})" : "Filters";
        _reset.Disabled = Q.Active == 0 && Q.Search == "";
        SyncGroups();
    }

    void Changed()
    {
        Refresh();
        _changed();
    }

    /// <summary>What the character owns or has found is different: the rows and the details say so.</summary>
    public void OwnedChanged()
    {
        if (!_window.IsAlive) return;
        if (Q.Owned || Q.Found || Q.Missing || Q.Sort == 7) Refresh(); else Fill();
        // The details are built again only when it is their item that changed: the player may be reading them.
        if (_state.Selected != null && Having(_state.Selected) != _shownHaving) FillDetails();
    }

    static string Having(Entry entry) => $"{entry.InBank}/{entry.Worn}/{entry.Found}";

    public void Select(Entry entry, bool remember)
    {
        if (ReferenceEquals(entry, _state.Selected)) { Fill(); return; }
        if (remember && _state.Selected != null) _state.Back.Add(_state.Selected);
        else if (!remember) _state.Back.Clear();
        _state.Selected = entry;
        Fill();
        FillDetails();
        _changed();
    }

    // ---- right: everything about one item ------------------------------------------------------------------------

    Control BuildDetails()
    {
        Control column = _s.Column("Details", out VBoxContainer body, DetailsWidth);
        _back = _s.GameButton("Back", "TexturedButton4x", 12);
        _back.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        _back.Pressed += () =>
        {
            if (_state.Back.Count == 0) return;
            _state.Selected = _state.Back[^1];
            _state.Back.RemoveAt(_state.Back.Count - 1);
            Fill();
            FillDetails();
            _changed();
        };
        body.AddChild(_back);
        _detailsScroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _details = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 4);
        _detailsScroll.AddChild(_details);
        body.AddChild(_detailsScroll);
        return column;
    }

    float DetailsInner => DetailsWidth - 20 - 14;   // the column's margins and the scroll bar

    void FillDetails()
    {
        try { ShowDetails(); }
        catch (Exception e)
        {
            // A part of the game the details read is gone: say so once, and leave the list working.
            if (!s_detailsFailed) _log("The details of an item could not be shown (the game was updated?): " + e);
            s_detailsFailed = true;
            Release();
            foreach (Node half in _details.GetChildren()) { _details.RemoveChild(half); half.QueueFree(); }
            _details.AddChild(Note("The details could not be shown: see MelvorModLoader.log."));
        }
    }

    void ShowDetails()
    {
        if (!_window.IsAlive) return;
        Release();
        foreach (Node old in _details.GetChildren()) { _details.RemoveChild(old); old.QueueFree(); }
        _detailsScroll.ScrollVertical = 0;
        _back.Visible = _state.Back.Count > 0;
        if (_state.Back.Count > 0) _back.TooltipText = "Back to " + (Hidden(_state.Back[^1]) ? "the item before" : _state.Back[^1].Name);

        Entry? entry = _state.Selected;
        _shownHaving = entry == null ? "" : Having(entry);
        if (entry == null)
        {
            _details.AddChild(Note("Select an item to see what it does, what it takes to use it, where it comes from and what it is used in."));
            return;
        }
        if (Hidden(entry))
        {
            _details.AddChild(PlainHead(entry, hidden: true));
            _details.AddChild(Note("You have not found this item yet. Switch \"No spoilers\" off in the filters to see it."));
            return;
        }

        if (!AddGameHead(entry)) _details.AddChild(PlainHead(entry, hidden: false));

        _details.AddChild(_s.Section("You have"));
        _details.AddChild(Stat("In the bank", entry.InBank.ToString("N0"), entry.InBank > 0 ? GameStyle.TextWhite : GameStyle.TextDim));
        if (entry.Worn > 0) _details.AddChild(Stat("Equipped", entry.Worn.ToString("N0")));
        if (entry.Owned > 1 && entry.Price > 0)
            _details.AddChild(Stat("Sells for, all together", (entry.Owned * entry.Price).ToString("N0"), GameStyle.TextGold));
        _details.AddChild(Stat("Completion log", entry.Found ? "found" : "not found yet", entry.Found ? GameStyle.TextWhite : GameStyle.TextDim));

        Lines(entry, found: true);
        Lines(entry, found: false);
    }

    // The game's own item panel, bound to the item the way the game's tooltip binds it.
    bool AddGameHead(Entry entry)
    {
        if (s_panelFailed) return false;
        try { MakeGameHead(entry); return true; }
        catch (Exception e)
        {
            s_panelFailed = true;
            _log("The game's item panel could not be used (the game was updated?): a plain one is shown instead. " + e.Message);
            Release();
            foreach (Node half in _details.GetChildren()) { _details.RemoveChild(half); half.QueueFree(); }
            return false;
        }
    }

    // In a method of its own: a member of the panel that is gone in a game version fails when this is first
    // run, and is caught above.
    [MethodImpl(MethodImplOptions.NoInlining)]
    void MakeGameHead(Entry entry)
    {
        if (GameStyle.Load<PackedScene>(ItemPanelScene)?.Instantiate() is not ItemInfoPanel panel) throw new InvalidOperationException("the item panel's scene was not found");
        // The panel is the game's, at the game's size: it is scaled with the window instead of rebuilt, and a
        // plain holder takes its place in the layout at the scaled size.
        float scale = _s.Scale, width = _s.Px(DetailsInner);
        var holder = new Control { MouseFilter = Control.MouseFilterEnum.Pass, CustomMinimumSize = new Vector2(width, _s.Px(120)) };
        panel.ShowIcon = true;
        panel.ShowOpenableCapability = true;
        panel.Scale = new Vector2(scale, scale);
        holder.AddChild(panel);
        _details.AddChild(holder);
        _gamePanel = panel;
        void Fit()
        {
            if (!GodotObject.IsInstanceValid(panel) || !GodotObject.IsInstanceValid(holder)) return;
            float height = panel.GetCombinedMinimumSize().Y;
            panel.Position = Vector2.Zero;
            panel.Size = new Vector2(width / scale, height);
            holder.CustomMinimumSize = new Vector2(width, MathF.Ceiling(height * scale));
        }
        panel.MinimumSizeChanged += Fit;
        // Bound once it is in the scene: the panel builds itself when it is ready.
        if (_catalog.Inventory is { } inventory) panel.Bind(entry.Item, inventory); else panel.Bind(entry.Item);
        Fit();
    }

    Control PlainHead(Entry entry, bool hidden)
    {
        var head = _s.Spaced(new HBoxContainer(), 12);
        var frame = new PanelContainer { CustomMinimumSize = new Vector2(_s.Px(64), _s.Px(64)), SizeFlagsVertical = Control.SizeFlags.ShrinkBegin };
        frame.AddThemeStyleboxOverride("panel", _s.ItemFrame());
        TextureRect picture = _s.Picture(entry.Item.Icon, 48);
        if (hidden) picture.Modulate = new Color(0, 0, 0, 0.75f);
        frame.AddChild(picture);
        head.AddChild(frame);
        var text = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 4);
        var title = _s.Spaced(new HBoxContainer(), 8);
        title.AddChild(_s.Text(hidden ? "???" : entry.Name, GameStyle.TextWhite, 20, GameStyle.Bold));
        if (!hidden && entry.Price > 0) title.AddChild(_s.NoteTag(entry.Price.ToString("N0")));
        text.AddChild(title);
        text.AddChild(_s.FadingLine());
        string? description = hidden ? null : entry.Item.Description?.Text;
        if (!string.IsNullOrWhiteSpace(description)) text.AddChild(Wrapped(description, GameStyle.TextWhite, 14));
        head.AddChild(text);
        return head;
    }

    // "Found in" and "Used in": every place, under the skill or the kind of place it belongs to.
    void Lines(Entry entry, bool found)
    {
        _details.AddChild(_s.Section(found ? "Found in" : "Used in"));
        ItemIndex? index = _catalog.Index;
        if (index == null)
        {
            _details.AddChild(Note(_state.IndexFailed ? "The game's data could not be read: see MelvorModLoader.log." : "Reading the game's data..."));
            return;
        }
        List<ItemRef> refs = (found ? index.For(entry.Item)?.Sources : index.For(entry.Item)?.Uses) ?? new List<ItemRef>();
        if (refs.Count == 0)
        {
            _details.AddChild(Note(found ? "Nothing in the game's data gives this item." : "Nothing in the game's data uses this item."));
            return;
        }
        // Combat first (what people ask about), then the rest by name; the likeliest on top inside a group.
        var groups = refs.Select(_catalog.Lines.Describe)
            .OrderBy(l => l.Group == "Combat" ? 0 : 1).ThenBy(l => l.Group, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(l => l.Chance).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .GroupBy(l => l.Group).ToList();
        bool chances = found && refs.Any(r => r.Chance < 0.9995);
        foreach (var group in groups)
        {
            var lines = group.ToList();
            // A skill has a picture of its own name; the few headings that are not skills borrow a fitting one.
            string plain = group.Key.Replace(" ", "");
            Texture2D? icon = GameStyle.Icon(plain) ?? (GroupIcons.TryGetValue(plain, out string? borrowed) ? GameStyle.Icon(borrowed) : null);
            var title = _s.Spaced(new HBoxContainer { CustomMinimumSize = new Vector2(0, _s.Px(24)) }, 6);
            if (icon != null) title.AddChild(_s.Picture(icon, 16));
            title.AddChild(_s.Text(lines.Count > 1 ? $"{group.Key}  ({lines.Count})" : group.Key, GameStyle.TextDim, 14, GameStyle.Bold));
            _details.AddChild(title);

            var box = _s.Spaced(new VBoxContainer(), 2);
            _details.AddChild(box);
            int shown = Math.Min(lines.Count, GroupLines);
            for (int i = 0; i < shown; i++) box.AddChild(LineRow(lines[i], found, chances, icon));
            if (lines.Count <= shown) continue;
            // A rune is used in hundreds of recipes: the rest of a long group comes when it is asked for.
            Button more = _s.GameButton($"Show all {lines.Count}", "TexturedButton4x", 12);
            more.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            more.Pressed += () =>
            {
                more.QueueFree();
                for (int i = shown; i < lines.Count; i++) box.AddChild(LineRow(lines[i], found, chances, icon));
            };
            box.AddChild(more);
        }
    }

    static readonly Dictionary<string, string> GroupIcons = new()
    {
        ["Opening"] = "OpenChest256", ["Upgrading"] = "upgrade", ["ItemUpgrade"] = "upgrade", ["Items"] = "Bank",
        ["MagicSpell"] = "Magic", ["Imbuement"] = "MagicImbuements", ["ArtisanRecipe"] = "SkillIcon",
    };

    // "[icon] Name                     5-10   22%"
    // "       Lv 20  ·  where, or what it is made from, each a way to that item"
    Control LineRow(ItemLine line, bool found, bool chances, Texture2D? groupIcon)
    {
        object owner = line.Ref.Owner;
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        panel.AddThemeStyleboxOverride("panel", _s.StatLine());
        var rows = _s.Spaced(new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass }, 2);
        panel.AddChild(rows);
        var top = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass }, 6);
        rows.AddChild(top);

        bool recipe = owner is not (Enemy or Dungeon or Item);
        string name = owner switch { Enemy enemy => enemy.DisplayName.Text, Dungeon dungeon => dungeon.DisplayName.Text, _ => line.Name };
        Texture2D? picture = line.Icon;
        var made = recipe ? _catalog.Gives.GetValueOrDefault(owner) : null;
        // An upgrade has no name of its own in the game's data, only an id: it is called by what it makes.
        if (recipe && made is { Count: > 0 } && owner.GetType().GetProperty("DisplayName") == null)
        {
            name = made[0].Item.DisplayName.GetPluralText(1);
            picture ??= made[0].Item.Icon;
        }
        // The line is the way to another item when it is one: a chest that holds this item, or the one thing
        // a recipe makes out of it.
        Entry? leadsTo = owner is Item holder ? _catalog.ByItem.GetValueOrDefault(holder)
            : !found && made is { Count: 1 } ? _catalog.ByItem.GetValueOrDefault(made[0].Item) : null;
        if (leadsTo != null)
        {
            Button link = ItemLink(leadsTo, "");
            link.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
            top.AddChild(link);
            top.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        }
        else
        {
            top.AddChild(_s.Picture(picture ?? groupIcon, 20));
            Label what = Clipped(_s.Text(name));
            what.TooltipText = name;
            what.MouseFilter = Control.MouseFilterEnum.Pass;
            top.AddChild(what);
        }
        if (line.Quantity != "") top.AddChild(_s.Text(line.Quantity, GameStyle.TextGrey, 13));
        if (chances)
        {
            bool always = line.Chance >= 0.9995;
            Label chance = _s.Text(ItemLines.Percent(line.Chance), always ? GameStyle.TextDim : GameStyle.TextWhite, always ? 13 : 14, always ? null : GameStyle.Bold);
            chance.HorizontalAlignment = HorizontalAlignment.Right;
            chance.CustomMinimumSize = new Vector2(_s.Px(54), 0);
            top.AddChild(chance);
        }

        switch (owner)
        {
            case Enemy enemy:
                List<string> areas = _catalog.Lines.AreasOf(enemy);
                rows.AddChild(Small($"Level {enemy.CombatLevel:N0}" + (areas.Count > 0 ? "  ·  " + string.Join(", ", areas) : "")));
                break;
            case Dungeon:
                rows.AddChild(Small("Dungeon reward"));
                break;
            case Item:
                break;
            default:
                // Under a recipe that gives the item: what it is made from. Under one that takes it: what it makes.
                var others = (found ? _catalog.Takes.GetValueOrDefault(owner) : made) ?? new List<(Item Item, ItemRef Ref)>();
                // What belongs to something larger says so: the paths of every agility course have the same few
                // names, and it is the course that tells them apart and asks for the level.
                object? within = _catalog.Parents.GetValueOrDefault(owner);
                var (level, skill) = LevelOf(owner);
                if (level == 0 && within != null) (level, skill) = LevelOf(within);
                List<CurrencyQuantity> money = found ? MoneyOf(owner) : new List<CurrencyQuantity>();

                var words = new List<string>();
                if (within != null && ItemLines.NameOf(within) is var place && place != name) words.Add(place);
                if (level > 0) words.Add(skill == "" || skill == line.Group ? $"Lv {level}" : $"{skill} Lv {level}");
                var links = new List<Control>();
                if (leadsTo != null)
                {
                    // The one product is the line itself: only how many of it come out is left to say.
                    ItemRef product = others[0].Ref;
                    if (product.Max > 1) words.Add("makes " + (product.Max > product.Min ? $"{product.Min:N0}-{product.Max:N0}" : product.Min.ToString("N0")));
                }
                else
                {
                    foreach (CurrencyQuantity cost in money)
                    {
                        PanelContainer tag = _s.NoteTag($"{cost.Quantity:N0} {cost.Currency.DisplayNameShort?.Text ?? cost.Currency.DisplayName.Text}");
                        tag.TooltipText = cost.Currency.DisplayName.Text;
                        links.Add(tag);
                    }
                    foreach (var (item, r) in others.OrderBy(o => o.Item.DisplayName.GetPluralText(1), StringComparer.OrdinalIgnoreCase))
                        if (_catalog.ByItem.TryGetValue(item, out Entry? other))
                            links.Add(ItemLink(other, r.Max > r.Min ? $"{r.Min:N0}-{r.Max:N0} " : r.Min > 1 ? $"{r.Min:N0} " : ""));
                    if (links.Count > 0) words.Add(found ? "from" : "makes");
                }
                if (words.Count == 0 && links.Count == 0) break;

                var flow = new HFlowContainer { MouseFilter = Control.MouseFilterEnum.Pass };
                flow.AddThemeConstantOverride("h_separation", _s.Px(4));
                flow.AddThemeConstantOverride("v_separation", _s.Px(3));
                flow.AddChild(_s.Text(string.Join("  ·  ", words), GameStyle.TextGrey, 12));
                foreach (Control link in links) flow.AddChild(link);
                var indent = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Pass };
                indent.AddThemeConstantOverride("margin_left", _s.Px(26));
                indent.AddChild(flow);
                rows.AddChild(indent);
                break;
        }
        return panel;
    }

    /// <summary>A label that takes the room there is and ends in "..." when its text is longer.</summary>
    static Label Clipped(Label label)
    {
        label.ClipText = true;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return label;
    }

    /// <summary>A small tag with an item's picture and name that opens that item, with Back leading here again.</summary>
    Button ItemLink(Entry entry, string amount)
    {
        bool hidden = Hidden(entry);
        Button link = _s.LinkTag(hidden ? null : entry.Item.Icon, hidden ? amount + "???" : amount + entry.Name);
        link.TooltipText = hidden ? "Not found yet" : "Open " + entry.Name;
        link.Pressed += () => Select(entry, remember: true);
        return link;
    }

    // The level a recipe, a purchase or a thieving target asks for: every one of them keeps it in "Requirements".
    // The skill is named with it: a purchase in the shop can ask for a Slayer level.
    static (int Level, string Skill) LevelOf(object owner)
    {
        try
        {
            if (owner.GetType().GetProperty("Requirements")?.GetValue(owner) is not ConditionGroup { Conditions: { } conditions }) return (0, "");
            int level = 0;
            string skill = "";
            foreach (Condition condition in conditions)
                if (condition is LevelCondition asked && asked.Level > level)
                {
                    level = asked.Level;
                    skill = asked.Component?.Entity?.DisplayName.Text ?? "";
                }
            return (level, skill);
        }
        catch { return (0, ""); }
    }

    // What a purchase or an upgrade costs besides items: gold, or another of the game's currencies.
    static List<CurrencyQuantity> MoneyOf(object owner)
    {
        try
        {
            foreach (string property in new[] { "Costs", "Ingredients" })
                if (owner.GetType().GetProperty(property)?.GetValue(owner) is RecipeIngredients { Currencies: { } currencies })
                    return currencies.Where(c => c is { Currency: not null, Quantity: > 0 }).ToList();
        }
        catch { }
        return new List<CurrencyQuantity>();
    }

    // ---- small pieces ------------------------------------------------------------------------------------------------

    // "Name                          value", on one of the game's stat lines.
    Control Stat(string name, string value, Color? tint = null)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", _s.StatLine());
        var line = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 6);
        Label label = _s.Text(name);
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        line.AddChild(label);
        line.AddChild(_s.Text(value, tint ?? GameStyle.TextWhite, 14, GameStyle.Bold));
        panel.AddChild(line);
        return panel;
    }

    Label Caption(string text)
    {
        Label label = _s.Text(text, GameStyle.TextGrey, 12, GameStyle.SemiBold);
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        return label;
    }

    Label Wrapped(string text, Color color, int size)
    {
        Label label = _s.Text(text, color, size);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(_s.Px(120), 0);
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        return label;
    }

    Label Note(string text)
    {
        Label label = Wrapped(text, GameStyle.TextGrey, 13);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        return label;
    }

    Control Small(string text)
    {
        Label label = Wrapped(text, GameStyle.TextGrey, 12);
        var indent = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        indent.AddThemeConstantOverride("margin_left", _s.Px(26));
        indent.AddChild(label);
        return indent;
    }

    // How many, the way the bank writes it on an item: 685, 1.13K, 54.37K. The game's own formatter; should
    // it be gone in a game version, the plain number.
    static string Amount(int value)
    {
        if (!s_plainAmounts)
        {
            try { return GameAmount(value); }
            catch { s_plainAmounts = true; }
        }
        return value.ToString("N0");
    }

    static bool s_plainAmounts;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static string GameAmount(int value) => GamesByMalcs.Melvor2.Core.NumberFormatter.Format(value, 2);

#if DEVTOOLS
    /// <summary>Development: the rows as they are on the screen, for the log.</summary>
    public string DevRows(int count) => string.Join(" | ", _found.Take(count).Select(e => e.Name));
    public int DevCount => _found.Count;
    /// <summary>Development: presses the first "Show all" of the details, or the first link to another item.</summary>
    public void DevPress(string what)
    {
        Button? Find(Node node, Func<Button, bool> match)
        {
            if (node is Button button && match(button)) return button;
            foreach (Node child in node.GetChildren()) if (Find(child, match) is { } hit) return hit;
            return null;
        }
        Button? target = what == "more" ? Find(_details, b => b.Text.StartsWith("Show all", StringComparison.Ordinal))
            : what == "back" ? _back
            : Find(_details, b => b.TooltipText.StartsWith("Open ", StringComparison.Ordinal) && (what == "link" || b.TooltipText.Contains(what, StringComparison.OrdinalIgnoreCase)));
        _log($"Dev: press '{what}' -> {(target == null ? "nothing found" : target.Text == "" ? target.TooltipText : target.Text)}");
        target?.EmitSignal(BaseButton.SignalName.Pressed);
    }
    public void DevScroll(int rows) => _scroll.Value = rows;
    /// <summary>Development: the middle of a row of the list, of the search box, or of the first button with this text.</summary>
    public Vector2? DevWhere(string what)
    {
        if (what == "search") return _search.GetGlobalRect().GetCenter();
        if (what.StartsWith("row", StringComparison.Ordinal) && int.TryParse(what[3..], out int row) && row < _pool.Count) return _pool[row].Button.GetGlobalRect().GetCenter();
        Button? Find(Node node)
        {
            if (node is Button { Visible: true } button && button.IsVisibleInTree() && (button.Text == what || button.Text.StartsWith(what + " (", StringComparison.Ordinal)
                    || button.FindChildren("*", "Label", true, false).OfType<Label>().Any(l => l.Text == what))) return button;
            foreach (Node child in node.GetChildren()) if (Find(child) is { } hit) return hit;
            return null;
        }
        return Find(_window.Root)?.GetGlobalRect().GetCenter();
    }
    public string DevSearchText => _search.Text;
    public string DevRecent => string.Join(" / ", _state.Recent.Select(r => string.Join(", ", Words(r))));
    public void DevScrollDetails(int pixels) => _detailsScroll.ScrollVertical = pixels;
#endif
}
