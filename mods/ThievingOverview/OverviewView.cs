using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Thieving;
using Godot;
using MelvorMods.Shared;

namespace ThievingOverview;

/// <summary>
/// The window's content: one titled column like the Combat Simulator's "Results, per hour" (SimView.BuildResults),
/// with the choices above the list (Targets or Best for, this place or every place, which item), the column names
/// that sort it, one line per target, and the food chosen for Thieving under it. A line's tooltip is the whole hour.
/// The lines are made once and updated in place, so a tooltip that is up stays up while its numbers change. The view
/// is made for one question (Targets or Best for) and one item: another one makes the window again.
/// </summary>
sealed class OverviewView
{
    // The width of the target's part of a line at full size in Best for: the Combat Simulator's enemy part when a
    // word follows the name on its line (SimViewColumns.EnemyPart, with a stance; here the place). The window is as
    // wide for Targets, where the room of the item's column goes to the names, as the simulator keeps its width
    // between Fights and Best for (SimViewColumns.ResultsWidth). The list's height: the Item Encyclopedia's details
    // column (EncyclopediaView.Height, as Astrology Overview has it). The two buttons of a question: as wide as they
    // are in the simulator's Setup column (SimView.SetupWidth, less its margins). A line: the simulator's, 30 high
    // and 2 apart, its picture 20 (SimView.BuildRow).
    const float NamePart = 320, ListHeight = 620, QuestionWidth = 244 - 20, RowHeight = 30, RowGap = 2, PictureSize = 20;

    /// <summary>The list's width at full size, the same for both questions.</summary>
    public static float Width => NamePart + Columns.ItemWidth + 6 + Columns.For(null).Sum(c => c.Width + 6) + 30;

    sealed class Line
    {
        public required Button Root;
        public required TextureRect Picture;
        public required VBoxContainer Names;
        public required HBoxContainer Top;
        public required Label Name, Kind, Place, Needs;
        public required Label[] Cells;
        // The row's own looks, read once it is on the screen, and which it was given: the target being stolen from,
        // and whether a click on it does something.
        public StyleBox? Resting, Pressed;
        public (bool Current, bool Clickable)? Look;
    }

    readonly GameWindow _window;
    readonly GameStyle _s;
    readonly Settings _settings;
    readonly Action _save, _refresh, _rebuild;
    readonly Action<Action> _guard;
    readonly Func<ThievingLocation, bool> _canOpen;
    readonly Action<ThievingLocation> _open;
    readonly Func<ThievingRecipe, string?> _record;
    readonly List<Column> _columns;
    readonly List<Item> _items;
    readonly Item? _item;
    // The question this view was made for. Changing it makes the window again (SetMode), so a view never shows
    // what it was not built for.
    readonly bool _bestFor;
    Button? _here, _everywhere;
    Label _empty = null!, _note = null!, _food = null!;
    ScrollContainer _scroll = null!;
    VBoxContainer _rows = null!;
    readonly List<Button> _headers = new();
    readonly Dictionary<ActiveThievingRecipe, Line> _lines = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<string, Texture2D?> _arrows = new();

    /// <param name="items">What Best for can be asked about: every item the listed targets give.</param>
    /// <param name="record">The character's own record at a target, in the game's words, for the tooltip.</param>
    /// <param name="save">Writes the settings.</param>
    /// <param name="refresh">Shows the numbers again, with the choices as they are now.</param>
    /// <param name="rebuild">Makes the window again, for another question or item.</param>
    /// <param name="guard">Runs what the game's layout calls back, as the mod runs a click (ThievingOverviewMod.Click).</param>
    /// <param name="canOpen">Whether a click on a line can show that place on the Thieving page.</param>
    /// <param name="open">Shows a place on the Thieving page, as the page's own Select Location does.</param>
    public OverviewView(GameWindow window, Settings settings, List<Item> items, Func<ThievingRecipe, string?> record,
        Action save, Action refresh, Action rebuild, Action<Action> guard, Func<ThievingLocation, bool> canOpen, Action<ThievingLocation> open)
    {
        _canOpen = canOpen;
        _open = open;
        _window = window;
        _s = window.Style;
        _settings = settings;
        _items = items;
        _record = record;
        _save = save;
        _refresh = refresh;
        _rebuild = rebuild;
        _guard = guard;
        _bestFor = settings.BestFor;
        _item = _bestFor ? items.FirstOrDefault(i => i.Id == settings.Item) ?? items.FirstOrDefault() : null;
        _columns = Columns.For(_item);
        if (_columns.All(c => c.Key != SortKey) && SortKey != Columns.NameKey)
        {
            SortKey = _bestFor ? Columns.ItemKey : "xp";
            SortDescending = true;
        }
        Build();
    }

    // Each question keeps its own order: going to Best for and back finds Targets sorted as it was.
    string SortKey
    {
        get => _bestFor ? _settings.BestSort : _settings.Sort;
        set { if (_bestFor) _settings.BestSort = value; else _settings.Sort = value; }
    }

    bool SortDescending
    {
        get => _bestFor ? _settings.BestDescending : _settings.Descending;
        set { if (_bestFor) _settings.BestDescending = value; else _settings.Descending = value; }
    }

    // ---- building ---------------------------------------------------------------------------------------------

    void Build()
    {
        Control column = _s.Column("Targets, per hour", out VBoxContainer body, Width);
        column.CustomMinimumSize = new Vector2(_s.Px(Width), _s.Px(ListHeight));
        column.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _window.Body.AddChild(column);

        // The two questions the window answers, as the Combat Simulator's Fights and Best for (SimView.BuildSetup).
        var top = _s.Spaced(new HBoxContainer(), 10);
        var modes = _s.Spaced(new HBoxContainer { CustomMinimumSize = new Vector2(_s.Px(QuestionWidth), 0) }, 4);
        var modeGroup = new ButtonGroup();
        Button targets = Toggle("Targets", modeGroup, "What an hour at each target gives, uses and risks");
        Button best = Toggle("Best for", modeGroup, "Choose an item and see the targets that give it, the most per hour first");
        targets.ButtonPressed = !_bestFor;
        best.ButtonPressed = _bestFor;
        targets.Pressed += () => SetMode(false);
        best.Pressed += () => SetMode(true);
        modes.AddChild(targets);
        modes.AddChild(best);
        top.AddChild(modes);
        if (!_bestFor)
        {
            var places = _s.Spaced(new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 4);
            var placeGroup = new ButtonGroup();
            _here = Toggle("This place", placeGroup, "The targets of the place the Thieving screen shows");
            _everywhere = Toggle("Every place", placeGroup, "Every target of every place you have found, in one list");
            _here.ClipText = true;
            _here.ButtonPressed = !_settings.AllPlaces;
            _everywhere.ButtonPressed = _settings.AllPlaces;
            _here.Pressed += () => SetPlaces(false);
            _everywhere.Pressed += () => SetPlaces(true);
            places.AddChild(_here);
            places.AddChild(_everywhere);
            top.AddChild(places);
        }
        else top.AddChild(ItemChoice());
        body.AddChild(top);

        // The column names, over the numbers they sort (SimView.BuildResults).
        var header = _s.Spaced(new HBoxContainer(), 6);
        var headerMargin = new MarginContainer();
        headerMargin.AddThemeConstantOverride("margin_left", _s.Px(8));
        headerMargin.AddThemeConstantOverride("margin_right", _s.Px(8 + 12));   // the rows' own padding and the scroll bar
        headerMargin.AddChild(header);
        header.AddChild(HeaderButton("Target", "Sort by name", 0, Columns.NameKey));
        foreach (Column c in _columns)
        {
            Button title = HeaderButton(c.Title, c.Help, c.Width, c.Key);
            title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            header.AddChild(title);
        }
        body.AddChild(headerMargin);

        _scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowAlways,
        };
        _rows = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, RowGap);
        _scroll.AddChild(_rows);
        body.AddChild(_scroll);

        _empty = Wrapped("", Width - 60, GameStyle.TextGrey, 14);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _empty.Visible = false;
        body.AddChild(_empty);

        // Why whole columns show "-" for now, when they do.
        _note = Wrapped("", Width - 30, GameStyle.TextGrey, 12);
        _note.HorizontalAlignment = HorizontalAlignment.Center;
        _note.Visible = false;
        body.AddChild(_note);

        // What Safe and Food are worked out with, as the Combat Simulator writes its food under the equipment.
        _food = Wrapped("", Width - 30, GameStyle.TextGrey, 12);
        _food.HorizontalAlignment = HorizontalAlignment.Center;
        body.AddChild(_food);
    }

    // Best for: one of the items the targets give, with its picture, as the simulator's spell box lists its spells
    // (SimView.FillSpells).
    Control ItemChoice()
    {
        var row = _s.Spaced(new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 8);
        Label caption = _s.Text("Item", GameStyle.TextGrey, 12, GameStyle.SemiBold);
        caption.MouseFilter = Control.MouseFilterEnum.Ignore;
        row.AddChild(caption);
        OptionButton box = _s.Dropdown();
        box.ClipText = true;
        box.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        box.TooltipText = "The item to find: the targets that give it are listed, the most per hour first";
        box.AddThemeConstantOverride("icon_max_width", _s.Px(16));
        box.GetPopup().AddThemeConstantOverride("icon_max_width", _s.Px(20));
        foreach (Item item in _items)
        {
            if (item.Icon != null) box.AddIconItem(item.Icon, item.DisplayName.GetPluralText(1));
            else box.AddItem(item.DisplayName.GetPluralText(1));
            box.SetItemMetadata(box.ItemCount - 1, item.Id);
        }
        box.Selected = _item != null ? _items.IndexOf(_item) : -1;
        box.Disabled = _items.Count == 0;
        box.ItemSelected += index =>
        {
            _settings.Item = box.GetItemMetadata((int)index).AsString();
            _settings.BestSort = Columns.ItemKey;
            _settings.BestDescending = true;
            _save();
            _rebuild();
        };
        row.AddChild(box);
        return row;
    }

    // Another question is another view: the window is made again for it (and nothing is shown with this one).
    void SetMode(bool bestFor)
    {
        if (_bestFor == bestFor) return;
        _settings.BestFor = bestFor;
        _save();
        _rebuild();
    }

    void SetPlaces(bool all)
    {
        if (_settings.AllPlaces == all) return;
        _settings.AllPlaces = all;
        _save();
        _refresh();
    }

    Button HeaderButton(string title, string tip, float width, string key)
    {
        bool name = key == Columns.NameKey;
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

    // One line: the game's outlined row (SimView.BuildRow) behind the target's picture and name, what kind of target
    // it is when it is a heist, the place it is in when every place is listed, and the numbers. A target that cannot
    // be stolen from yet is greyed, with what it needs under its name; one the Thieving screen still shows as "???"
    // is "???" here too, without its picture (the Item Encyclopedia's "No spoilers"). A click shows the target's place
    // on the Thieving page (ThievingOverviewMod.OpenPlace); a line whose click does nothing (its place is the one shown,
    // or cannot be opened yet) keeps its resting look under the pointer (OverviewRowView.Update in Astrology Overview).
    // The target being stolen from has the lit outline of a selected line, the row's pressed look, as Fishing Rates
    // lights the spot the Fishing page shows (RatesList.Restyle).
    Line BuildLine(ThievingLocation location)
    {
        var row = new Button
        {
            ThemeTypeVariation = "OutlineToggleButton", CustomMinimumSize = new Vector2(0, _s.Px(RowHeight)),
            FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.Arrow,
        };
        row.Pressed += () => _open(location);
        var line = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 6);
        line.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.Minsize, _s.Px(8));
        row.AddChild(line);
        TextureRect picture = _s.Picture(null, (int)PictureSize);
        line.AddChild(picture);

        var names = _s.Spaced(new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, -2);
        names.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        var top = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 6);
        Label name = Quiet(_s.Text("", GameStyle.TextWhite));
        name.ClipText = true;
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        top.AddChild(name);
        Label kind = Quiet(_s.Text("", GameStyle.TextDim, 12));
        top.AddChild(kind);
        Label place = Quiet(_s.Text("", GameStyle.TextPaper, 12));
        top.AddChild(place);
        names.AddChild(top);
        // What a locked target needs, whole: it wraps onto as many lines as it takes, and the line grows with it.
        Label needs = Quiet(_s.Text("", GameStyle.TextDim, 12));
        needs.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        names.AddChild(needs);
        line.AddChild(names);

        var cells = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 6);
        var labels = new Label[_columns.Count];
        for (int i = 0; i < _columns.Count; i++)
        {
            Label cell = Quiet(_s.Text("", GameStyle.TextWhite, 14, GameStyle.SemiBold));
            cell.HorizontalAlignment = HorizontalAlignment.Right;
            cell.CustomMinimumSize = new Vector2(_s.Px(_columns[i].Width), 0);
            cells.AddChild(cell);
            labels[i] = cell;
        }
        line.AddChild(cells);
        var made = new Line { Root = row, Picture = picture, Names = names, Top = top, Name = name, Kind = kind, Place = place, Needs = needs, Cells = labels };
        // The words wrap once the line has its width, which the game's layout gives it after this: the line is
        // measured again then.
        needs.MinimumSizeChanged += () => _guard(() => FitRow(made));
        return made;
    }

    static Label Quiet(Label label)
    {
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        return label;
    }

    // As high as what the line holds: a one-line row is the simulator's 30, and the words under a locked target's
    // name add their own height to it, measured as the game lays them out.
    void FitRow(Line line)
    {
        if (!GodotObject.IsInstanceValid(line.Root)) return;
        float extra = line.Needs.Visible ? line.Names.GetCombinedMinimumSize().Y - line.Top.GetCombinedMinimumSize().Y : 0;
        float height = _s.Px(RowHeight) + Math.Max(0, MathF.Ceiling(extra));
        if (line.Root.CustomMinimumSize.Y != height) line.Root.CustomMinimumSize = new Vector2(0, height);
    }

    // ---- showing ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The targets to show, the food they are judged with, the place the Thieving screen shows (if any), the target
    /// being stolen from, and why whole columns show "-" for now ("" when none do).
    /// </summary>
    public void Show(IReadOnlyList<TargetRates> all, ThievingFood food, ThievingLocation? here, ActiveThievingRecipe? current, string note)
    {
        if (!_window.IsAlive) return;
        bool everywhere = _bestFor || _settings.AllPlaces || here == null;
        if (_here != null && _everywhere != null)
        {
            string hereName = here?.DisplayName.Text ?? "This place";
            if (_here.Text != hereName) _here.Text = hereName;
            _here.Disabled = here == null;
            _here.TooltipText = here == null ? "No place is chosen on the Thieving screen" : $"The targets of {here.DisplayName.Text}, the place the Thieving screen shows";
            // Without a place on the screen every place is listed, and the buttons say so.
            if (_here.ButtonPressed == everywhere) _here.SetPressedNoSignal(!everywhere);
            if (_everywhere.ButtonPressed != everywhere) _everywhere.SetPressedNoSignal(everywhere);
        }
        var shown = all.Where(r => _bestFor ? _item != null && r.Gives(_item) : everywhere || ReferenceEquals(r.Location, here)).ToList();
        List<TargetRates> ordered = Sorted(shown);
        PaintHeaders();
        Dictionary<string, double> best = Best(ordered);

        // The lines of targets no longer listed go first, so the others can be put in their order by index.
        var keep = new HashSet<ActiveThievingRecipe>(ordered.Select(r => r.Active), ReferenceEqualityComparer.Instance);
        foreach (var gone in _lines.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _rows.RemoveChild(_lines[gone].Root);
            _lines[gone].Root.QueueFree();
            _lines.Remove(gone);
        }
        for (int index = 0; index < ordered.Count; index++)
        {
            TargetRates r = ordered[index];
            if (!_lines.TryGetValue(r.Active, out Line? line))
            {
                line = BuildLine(r.Location);
                _rows.AddChild(line.Root);
                _lines[r.Active] = line;
            }
            Update(line, r, best, everywhere, food, ReferenceEquals(r.Active, current), here);
            if (line.Root.GetIndex() != index) _rows.MoveChild(line.Root, index);
        }

        _scroll.Visible = ordered.Count > 0;
        _empty.Visible = ordered.Count == 0;
        string empty = _bestFor ? _item != null ? "None of the targets you have found gives this item." : "None of the targets you have found gives an item."
            : all.Count == 0 ? "No target has been found yet." : "This place has no target you have found yet.";
        if (_empty.Text != empty) _empty.Text = empty;
        if (_note.Text != note) _note.Text = note;
        _note.Visible = note != "";
        SetText(_food, FoodWords(food), food.Selected && food.InBank > 0 && food.AutoEatHp is not 0d ? GameStyle.TextGrey : GameStyle.Bad);
    }

    void Update(Line line, TargetRates r, Dictionary<string, double> best, bool everywhere, ThievingFood food, bool current, ThievingLocation? here)
    {
        bool shownThere = ReferenceEquals(r.Location, here), opens = !shownThere && _canOpen(r.Location);
        Look(line, current, opens);
        bool open = r.Reachable;
        Texture2D? picture = r.Hidden ? null : r.Recipe.Media ?? r.Recipe.Icon;
        if (line.Picture.Texture != picture) line.Picture.Texture = picture;
        SetText(line.Name, r.Hidden ? "???" : r.Recipe.DisplayName.Text, open ? GameStyle.TextWhite : GameStyle.TextDim);
        SetText(line.Kind, r.IsHeist ? $"Heist, {r.Stages} stages" : "", GameStyle.TextDim);
        line.Kind.Visible = r.IsHeist;
        SetText(line.Place, r.Location.DisplayName.Text, open ? GameStyle.TextPaper : GameStyle.TextDim);
        line.Place.Visible = everywhere;
        SetText(line.Needs, r.Needs, GameStyle.TextDim);
        line.Needs.Visible = !open;
        FitRow(line);
        for (int i = 0; i < _columns.Count; i++)
        {
            Column c = _columns[i];
            bool marked = open && best.TryGetValue(c.Key, out double top) && c.Value(r) is { } v && Same(v, top);
            SetText(line.Cells[i], c.Text(r), !open ? GameStyle.TextDim : marked ? GameStyle.Highlight : c.Tint(r));
        }
        // What a click does, in Fishing Rates' words (Words.Click), and nothing when it changes nothing: on a target of the
        // place the page shows, and of a place that cannot be opened yet.
        string tip = Tip(r, food, best, current) +
            (opens ? $"\n\nClick to open {r.Location.DisplayName.Text} on the Thieving page. Thieving is not started." : "");
        if (line.Root.TooltipText != tip) line.Root.TooltipText = tip;
    }

    static readonly string[] RowStates = { "normal", "hover", "pressed", "hover_pressed", "disabled" };

    // The target being stolen from: the row's pressed look, always. A line a click does something with: the game's own
    // looks of the row, under the pointer too, as the Combat Simulator's lines. Any other: its resting look, always.
    void Look(Line line, bool current, bool clickable)
    {
        if (line.Resting == null)
        {
            if (!line.Root.IsInsideTree()) return;
            line.Resting = line.Root.GetThemeStylebox("normal");
            line.Pressed = line.Root.GetThemeStylebox("pressed");
        }
        if (line.Look == (current, clickable)) return;
        line.Look = (current, clickable);
        line.Root.MouseDefaultCursorShape = clickable ? Control.CursorShape.PointingHand : Control.CursorShape.Arrow;
        foreach (string state in RowStates)
        {
            if (current) line.Root.AddThemeStyleboxOverride(state, line.Pressed!);
            else if (clickable) line.Root.RemoveThemeStyleboxOverride(state);
            else line.Root.AddThemeStyleboxOverride(state, line.Resting);
        }
    }

    // Equal to the best, as far as the numbers can tell (Fishing Rates' IsBest).
    static bool Same(double a, double b) => Math.Abs(a - b) <= 1e-9 + 0.0005 * Math.Abs(b);

    static void SetText(Label label, string text, Color color)
    {
        if (label.Text != text) label.Text = text;
        if (label.GetThemeColor("font_color") != color) label.AddThemeColorOverride("font_color", color);
    }

    // The best number of each column among the targets that can be stolen from, when they do not all have it
    // (Recipe Rates marks the best recipe of a list only when there is a choice).
    Dictionary<string, double> Best(List<TargetRates> lines)
    {
        var best = new Dictionary<string, double>();
        foreach (Column c in _columns.Where(c => c.Marks && c.Better != Better.Neither))
        {
            var values = lines.Where(r => r.Reachable).Select(c.Value).OfType<double>().Where(double.IsFinite).ToList();
            if (values.Count < 2) continue;
            double most = values.Max(), least = values.Min();
            if (Same(least, most)) continue;
            best[c.Key] = c.Better == Better.More ? most : least;
        }
        return best;
    }

    // ---- the whole hour, in a line's tooltip ------------------------------------------------------------------

    string Tip(TargetRates r, ThievingFood food, Dictionary<string, double> best, bool current)
    {
        var tip = new StringBuilder();
        tip.Append(r.Hidden ? "???" : r.Recipe.DisplayName.Text).Append("  ·  ").Append(r.Location.DisplayName.Text);
        if (r.Recipe.RecipeType is { } kind) tip.Append("  ·  ").Append(kind.DisplayName.Text);
        if (r.IsHeist) tip.Append($", {r.Stages} stages");
        if (!r.Reachable) tip.Append("\nLocked: ").Append(r.Needs);
        // The lit line, said as Fishing Rates says its own ("The spot the Fishing page shows.").
        if (current) tip.Append("\nThe target you are stealing from.");

        if (!r.SuccessKnown) tip.Append($"\nThe success chance's unit is not known yet: {UnitWords}.");
        else
        {
            tip.Append($"\n{TargetRates.Percent(r.Success)} success  ·  {TargetRates.Seconds(r.IntervalSeconds)} a try");
            if (r.Success < 1 && r.StunSeconds > 0)
                tip.Append($", {TargetRates.Seconds(r.StunSeconds)} stunned after a failure{(r.StunAvoided > 0 ? $" ({TargetRates.Percent(r.StunAvoided)} avoided)" : "")}");
        }
        if (r.Valid)
        {
            tip.Append($"\n\nPer hour: {TargetRates.Fmt(r.TriesPerHour)} tries, {TargetRates.Fmt(r.SuccessesPerHour)} successful");
            if (r.XpPerHour is { } xp) tip.Append($"\nThieving XP: {TargetRates.Fmt(xp)}");
            if (r.Coin != null && r.CoinsPerHour is > 0 and var coins) tip.Append($"\n{r.Coin.DisplayName.Text}: {TargetRates.Fmt(coins)}");
            if (r.ItemsKnown)
            {
                if (r.Hidden) tip.Append("\nWhat it gives shows here once the Thieving screen shows the target.");
                else
                    foreach (string table in new[] { "Common", "Rare" })
                    {
                        var items = r.Drops.Where(d => d.Table == table && d.PerHour > 0).OrderByDescending(d => d.PerHour).ToList();
                        foreach (Drop d in items.Take(12))
                            tip.Append($"\n{d.Item.DisplayName.GetPluralText(2)}: {TargetRates.RateWords(d.PerHour)}{(table == "Rare" ? " (rare)" : "")}" +
                                (d.GoldEach > 0 ? $"  ·  {TargetRates.Fmt(d.PerHour * d.GoldEach)} gold" : ""));
                        if (items.Count > 12) tip.Append($"\nand {items.Count - 12} more {table.ToLowerInvariant()} items");
                    }
                if (r.GoldPerHour is { } gold)
                    tip.Append($"\nGold: {TargetRates.Fmt(gold)}  ({TargetRates.Fmt(gold - (r.LootValuePerHour ?? 0))} coins + {TargetRates.Fmt(r.LootValuePerHour ?? 0)} loot)");
                if (r.FromRewardsWindow && r.Fortune != 0)
                    tip.Append($"\nThe items as View Possible Rewards shows them with your fortune of {r.Fortune:0.##}.");
            }
            else tip.Append("\nNot counted: ").Append(r.ItemsWhy).Append('.');
            if (r.RarePerHour is > 0 and var rare) tip.Append($"\nRare item: {TargetRates.RateWords(rare)}");
        }
        if (r.IsHeist && !r.Hidden && r.Recipe.CompletionDropTable is { Items.Count: > 0 } completion)
        {
            var names = completion.Items.Where(d => d.Item != null).Select(d => d.Item.DisplayName.GetPluralText(1)).Distinct().ToList();
            tip.Append($"\nWhen the heist is done: {string.Join(", ", names.Take(4))}{(names.Count > 4 ? $" and {names.Count - 4} more" : "")}.");
        }

        tip.Append('\n');
        if (r.Valid && r.FailuresPerHour > 0 && r.MaxHit > 0)
        {
            tip.Append($"\nFailures: {TargetRates.Fmt(r.FailuresPerHour)} per hour, up to {r.MaxHit:N0} damage each");
            if (r.DamageAvoided > 0) tip.Append($", {TargetRates.Percent(r.DamageAvoided)} of it avoided");
        }
        else if (!r.Valid && r.MaxHit > 0) tip.Append($"\nA failure does up to {r.MaxHit:N0} damage.");
        if (r.FoodPerHour is { } eaten && food.Selected)
        {
            tip.Append($"\nFood: about {TargetRates.Fmt(eaten)} {food.Name(eaten == 1 ? 1 : 2)} per hour");
            if (eaten > 0 && food.InBank > 0) tip.Append($"; your {food.InBank:N0} last about {TargetRates.Clock(food.InBank / eaten * 3600)}");
        }
        tip.Append('\n').Append(r.SafeWords);
        if (_record(r.Recipe) is { } record) tip.Append("\n\n").Append(record);

        // Why a number is in gold.
        var marked = _columns.Where(c => best.TryGetValue(c.Key, out double top) && r.Reachable && c.Value(r) is { } v && Same(v, top)).Select(c => c.Title).ToList();
        if (marked.Count > 0) tip.Append($"\nBest of this list: {string.Join(", ", marked)}");
        return tip.ToString();
    }

    const string UnitWords = "it shows once the Thieving screen has shown a success chance (choose a place there)";

    static string FoodWords(ThievingFood food)
    {
        if (!food.Selected) return "No food is chosen for Thieving: every target that can hit you is Risky, and Food shows -.";
        if (food.InBank <= 0) return $"{food.Name(1)} is chosen for Thieving, but there is none in the bank: every target that can hit you is Risky.";
        if (food.AutoEatHp is 0d) return $"{food.Name(1)} is chosen for Thieving, but auto eat cannot use it: every target that can hit you is Risky, and Food shows -.";
        string point = food.AutoEatHp is { } hp ? $"auto eat steps in at {hp:#,0} HP" : "where auto eat steps in cannot be told right now";
        return $"Food for Thieving: {food.InBank:N0} {food.Name(food.InBank)} in the bank, {food.Healing:N0} HP each; {point}. You have {food.Hp:N0} of {food.MaxHp:N0} HP.";
    }

    // ---- sorting, as the simulator sorts (SimViewColumns.SortBy, Sorted, PaintHeaders) ---------------------------

    Column? ColumnOf(string key) => _columns.FirstOrDefault(c => c.Key == key);

    // The first click on a column puts the best targets on top, the next turns the order around.
    void SortBy(string key)
    {
        bool descending = SortKey == key ? !SortDescending : key != Columns.NameKey && ColumnOf(key)?.Better != Better.Less;
        SortKey = key;
        SortDescending = descending;
        _save();
        _refresh();
    }

    // The targets that can be stolen from first, then the greyed ones, each by the chosen column; a target without
    // the number last among them, whichever way the list is sorted. Equal numbers go by XP.
    List<TargetRates> Sorted(List<TargetRates> lines)
    {
        string key = SortKey;
        bool descending = SortDescending;
        IEnumerable<TargetRates> Order(IEnumerable<TargetRates> part)
        {
            if (key == Columns.NameKey)
            {
                // "???" sorts as the game writes it, not by the name it hides.
                var byName = part.OrderBy(r => r.Hidden ? "???" : r.Recipe.DisplayName.Text, StringComparer.OrdinalIgnoreCase).ToList();
                if (descending) byName.Reverse();
                return byName;
            }
            Func<TargetRates, double?> value = ColumnOf(key)?.Value ?? (r => r.XpPerHour);
            var known = part.Where(r => value(r) != null);
            var ordered = (descending ? known.OrderByDescending(r => value(r)) : known.OrderBy(r => value(r))).ThenByDescending(r => r.XpPerHour ?? -1);
            return ordered.Concat(part.Where(r => value(r) == null).OrderByDescending(r => r.XpPerHour ?? -1));
        }
        return Order(lines.Where(r => r.Reachable)).Concat(Order(lines.Where(r => !r.Reachable))).ToList();
    }

    void PaintHeaders()
    {
        Texture2D? arrow = Arrow(SortDescending ? "down" : "up");
        foreach (Button header in _headers)
        {
            if (!GodotObject.IsInstanceValid(header)) continue;
            string key = header.GetMeta("column").AsString();
            Color color = key == SortKey ? GameStyle.TextGold : GameStyle.TextGrey;
            foreach (string name in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
                if (header.GetThemeColor(name) != color) header.AddThemeColorOverride(name, color);
            if (key != Columns.NameKey) MakeRoom(header, ColumnOf(key), key == SortKey ? arrow : null);
            header.QueueRedraw();   // the arrow
        }
    }

    static readonly string[] TitleStates = { "normal", "hover", "pressed", "hover_pressed", "disabled" };

    // A number's name stands on the right of its column, where the numbers end; the name the list is sorted by
    // moves left so that its arrow ends there (SimViewColumns.MakeRoom).
    void MakeRoom(Button title, Column? column, Texture2D? arrow)
    {
        string look = $"{title.Text}|{arrow?.GetWidth()}";
        if (title.HasMeta("room") && title.GetMeta("room").AsString() == look) return;
        title.SetMeta("room", look);
        float room = MathF.Floor(title.CustomMinimumSize.X - MathF.Ceiling(TitleWidth(title)) - 1), gap = title.GetThemeConstant("h_separation");
        foreach (string state in TitleStates)
        {
            title.RemoveThemeStyleboxOverride(state);
            StyleBox theme = title.GetThemeStylebox(state);
            float right = theme.GetMargin(Side.Right);
            if (arrow != null) right = Math.Max(right, Math.Min(room, arrow.GetWidth() + gap));
            title.AddThemeStyleboxOverride(state, new StyleBoxEmpty
            {
                ContentMarginLeft = 0, ContentMarginTop = theme.GetMargin(Side.Top),
                ContentMarginRight = right, ContentMarginBottom = theme.GetMargin(Side.Bottom),
            });
        }
        if (column != null) title.TooltipText = TitleCut(title) ? column.Title + "\n" + column.Help : column.Help;
    }

    static float TitleWidth(Button title) =>
        title.GetThemeFont("font").GetStringSize(title.Text, HorizontalAlignment.Left, -1, title.GetThemeFontSize("font_size")).X;

    static bool TitleCut(Button title)
    {
        StyleBox box = title.GetThemeStylebox("normal");
        return TitleWidth(title) > title.CustomMinimumSize.X - box.GetMargin(Side.Left) - box.GetMargin(Side.Right) + 0.5f;
    }

    // The game's drop-down arrow after the sorted column's name, down for the most first (SimViewColumns.DrawSortArrow).
    void DrawSortArrow(Button header)
    {
        if (!header.HasMeta("column") || header.GetMeta("column").AsString() is not { } key || key != SortKey) return;
        if (Arrow(SortDescending ? "down" : "up") is not { } arrow) return;
        StyleBox box = header.GetThemeStylebox("normal");
        float gap = header.GetThemeConstant("h_separation");
        float x = key == Columns.NameKey ? box.GetMargin(Side.Left) + TitleWidth(header) + gap : header.Size.X - box.GetMargin(Side.Right) + gap;
        var at = new Vector2(MathF.Round(x), MathF.Round((header.Size.Y - arrow.GetHeight()) / 2));
        header.DrawTextureRect(arrow, new Rect2(at, arrow.GetSize()), false);
    }

    Texture2D? Arrow(string way)
    {
        if (_arrows.TryGetValue(way, out Texture2D? made) && (made == null || GodotObject.IsInstanceValid(made))) return made;
        Texture2D? arrow = null;
        try
        {
            Image? image = GameStyle.DropArrow?.GetImage();
            if (image != null && !image.IsEmpty())
            {
                if (image.IsCompressed()) image.Decompress();
                if (way == "up") image.Rotate180();
                if (Math.Abs(_s.Scale - 1f) >= 0.01f)
                    image.Resize(_s.Px(image.GetWidth()), _s.Px(image.GetHeight()), Image.Interpolation.Lanczos);
                arrow = ImageTexture.CreateFromImage(image);
            }
        }
        catch { arrow = null; }
        return _arrows[way] = arrow;
    }

    // ---- small parts --------------------------------------------------------------------------------------------

    Label Wrapped(string text, float width, Color color, int size)
    {
        Label label = _s.Text(text, color, size);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(_s.Px(width), 0);
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        return label;
    }

    // The simulator's on/off buttons (SimView.Toggle).
    Button Toggle(string text, ButtonGroup group, string tip)
    {
        Button button = _s.GameButton(text, "OutlineToggleButton", 13);
        button.ToggleMode = true;
        button.ButtonGroup = group;
        button.TooltipText = tip;
        button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        button.FocusMode = Control.FocusModeEnum.None;
        return button;
    }

    public ScrollContainer Scroll => _scroll;

#if DEVTOOLS
    /// <summary>Development: every line as it is drawn, top to bottom, with its look.</summary>
    public IEnumerable<(Button Root, string Drawn, bool Hidden, bool Locked, ActiveThievingRecipe Target, bool Clickable)> DevLines()
    {
        foreach (Node child in _rows.GetChildren())
        {
            if (child is not Button root || root.IsQueuedForDeletion()) continue;
            var (target, line) = _lines.FirstOrDefault(l => l.Value.Root == root);
            if (line == null) continue;
            string cells = string.Join(" | ", line.Cells.Select((cell, i) => $"{_columns[i].Key}='{cell.Text}' ({Colour(cell)})"));
            string drawn = $"'{line.Name.Text}' ({Colour(line.Name)}){(line.Kind.Visible ? $" <{line.Kind.Text}>" : "")}{(line.Place.Visible ? $" [{line.Place.Text}]" : "")}" +
                $"{(line.Needs.Visible ? $" ({line.Needs.Text}; {line.Needs.GetLineCount()} line(s))" : "")} picture={(line.Picture.Texture != null ? "yes" : "none")} " +
                $"height {root.Size.Y:0} (min {root.CustomMinimumSize.Y:0}, holds {line.Names.GetCombinedMinimumSize().Y:0}){(line.Look?.Current == true ? " (stealing now)" : "")}" +
                $"{(line.Look?.Clickable == true ? " (a click opens its place)" : "")} | {cells}";
            yield return (root, drawn, line.Name.Text == "???", line.Needs.Visible, target, line.Look?.Clickable == true);
        }
    }

    static string Colour(Label label)
    {
        Color c = label.GetThemeColor("font_color");
        return c == GameStyle.Highlight ? "gold" : c == GameStyle.TextDim ? "dim" : c == GameStyle.TextWhite ? "white" : c == GameStyle.TextPaper ? "paper"
            : c == GameStyle.TextGrey ? "grey" : c == GameStyle.Good ? "green" : c == GameStyle.Bad ? "red" : c.ToHtml();
    }

    public string DevFoodLine => _food.Text;
    public string DevNote => _note.Visible ? _note.Text : "";
    public string DevEmpty => _empty.Visible ? _empty.Text : "";
    public string DevChoices => $"best for={_bestFor}, item={_item?.Id ?? "-"}, every place={_settings.AllPlaces}, sort={SortKey} {(SortDescending ? "down" : "up")}, " +
        $"columns {string.Join(",", _columns.Select(c => c.Key))}";

    /// <summary>Development: presses one of the window's own buttons, as a click does.</summary>
    public void DevPress(string what)
    {
        Button? button = what switch
        {
            "here" => _here,
            "everywhere" => _everywhere,
            _ => _window.Body.FindChildren("*", "Button", true, false).OfType<Button>().FirstOrDefault(b => b.Text == what),
        };
        if (button == null || button.Disabled) throw new InvalidOperationException($"no button '{what}' can be pressed in the window");
        if (button.ToggleMode) button.ButtonPressed = true;
        button.EmitSignal(BaseButton.SignalName.Pressed);
    }
#endif
}
