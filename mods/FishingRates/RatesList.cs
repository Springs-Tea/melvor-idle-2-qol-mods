using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2.Entities.Fishing;
using Godot;
using MelvorMods.Shared;

namespace FishingRates;

/// <summary>Which way a number is better (CombatSim's Better). The first click on its column puts the best spots on top.</summary>
enum Better { More, Less, Neither }

/// <summary>
/// One number of the list, as the Combat Simulator's columns are made (mods/CombatSim/SimColumns.cs, SimColumn):
/// its short name over the list, what it is in the name's tooltip, its width at full size, the number of a spot
/// (null for none) and which way it is better.
/// </summary>
sealed record RateColumn(string Key, string Title, string Tip, float Width, Func<SpotRates, double?> Value, Func<double, string> Format, Better Better)
{
    /// <summary>What the name over the list says when the pointer rests on it, with the Combat Simulator's last line.</summary>
    public string Help => Tip + "\n" + Better switch
    {
        Better.More => "More is better",
        Better.Less => "Less is better",
        _ => "Neither more nor less is better",
    };

    /// <summary>
    /// The columns, left to right. Bait only while a bait is selected on the Fishing page: without one no spot
    /// uses any. Its tip says what backs it, the measured rule of SpotRates.PreservedBaitStays, the way Woodcutting
    /// Rates' XP column says "Measured: ..." (mods/WoodcuttingRates/RatesList.cs, XpTip). Mastery XP is not a column:
    /// Fishing in this game has no mastery XP (its Mastery Guild counts tasks, not XP). A tip's line stays within 82
    /// characters, the Gold tip's: other mods hold their tips to it (Godot's tooltip does not wrap).
    /// </summary>
    public static List<RateColumn> For(string? bait)
    {
        var columns = new List<RateColumn>
        {
            new("time", "Time", "Seconds for one catch: the middle of the spot's catch time, with your bonuses", 58,
                s => s.HasTime ? s.Seconds : null, Words.Seconds, Better.Less),
            new("catches", "Catches", "Catches per hour, fish and junk together", 66,
                s => s.HasTime ? s.CatchesPerHour : null, Words.Number, Better.More),
            new("xp", "XP", "Fishing XP per hour", 66, s => s.HasTime ? s.XpPerHour : null, Words.Number, Better.More),
            new("gold", "Gold", "What the catches of an hour sell for, rare special items at their long-run average", 66,
                s => s.HasTime ? s.GoldPerHour : null, Words.Number, Better.More),
        };
        // Every catch uses one, so the fastest spot uses the most: that is no reason to call any of them the best.
        if (bait != null)
        {
            string tip = (SpotRates.PreservedBaitStays
                ? $"{bait} taken from the bank per hour, less what is preserved"
                : $"{bait} taken from the bank per hour: one every catch, preserved or not") + "\nMeasured: " + SpotRates.BaitSeen;
            columns.Add(new("bait", "Bait", tip, 58, s => s.HasTime ? s.BaitPerHour : null, Words.Number, Better.Neither));
        }
        return columns;
    }
}

/// <summary>
/// The window's list: one line per fishing spot the game shows, in a titled column like the Combat Simulator's
/// results (mods/CombatSim/SimView.cs, BuildResults): the names over the numbers sort the list, the game's own
/// arrow beside the one it is sorted by (SimViewColumns.cs). The best number of each column is in the gold of
/// Recipe Rates' best recipe (GameStyle.Highlight). A spot that cannot be fished yet is greyed, with the level it
/// needs. Each line starts with the pictures of the spot's fish, as the game's own spot buttons show them; a fish the
/// game has no picture for shows none, never the game's grey stand-in. A click on a line opens that spot on the
/// Fishing page (FishingRatesMod.OpenOnPage); it starts nothing.
/// </summary>
sealed class RatesList
{
    /// <summary>The first column: the spots in the order of the Fishing page.</summary>
    public const string NameKey = "name";
    // The part of a line left of the numbers at full size: the Combat Simulator's enemy part with the stance after
    // the name (SimViewColumns.EnemyPart, its one 20 px picture included), here the location after the spot's name,
    // and the room for the spot's other fish pictures, 20 px each and 2 apart.
    const float EnemyPart = 320, Picture = 20, PictureGap = 2;
    // A line is the Combat Simulator's (SimView.BuildRow): 30 high, 2 apart. More lines than this scroll.
    const float RowHeight = 30, RowGap = 2;
    const int MostRows = 14;

    sealed class Row
    {
        public Button Button = null!;
        public readonly List<TextureRect> Pictures = new();
        public Label Name = null!, Place = null!, Level = null!;
        public readonly List<Label> Cells = new();
        // The look the line was given (lit, opens something): null until it is in the tree and has the game's theme.
        public (bool Lit, bool Clickable)? Look;
        public SpotRates? Spot;
    }

    readonly GameStyle _s;
    readonly List<RateColumn> _columns;
    readonly int _pictures;
    readonly Func<string, string?> _bonusName;
    readonly Action<string, bool> _sorted;
    readonly Action<SpotRates> _clicked;
    readonly List<Button> _headers = new();
    readonly Dictionary<ActiveFishingRecipe, Row> _rows = new(ReferenceEqualityComparer.Instance);
    readonly ScrollContainer _scroll;
    readonly VBoxContainer _lines;
    readonly Label _empty;
    readonly Dictionary<string, Texture2D?> _arrows = new();
    int _shownRows = -1;
    string _painted = "";

    public Control Root { get; }
    public string Sort { get; private set; }
    public bool Descending { get; private set; }

    /// <param name="pictures">Room for this many fish pictures at the start of a line: the most fish a spot has.</param>
    /// <param name="bonusName">The game's name of a bonus that is not counted, once it has been read (BonusNames).</param>
    /// <param name="clicked">A line was clicked: its spot, to open on the Fishing page.</param>
    public RatesList(GameStyle style, List<RateColumn> columns, int pictures, string sort, bool descending,
        Func<string, string?> bonusName, Action<string, bool> sorted, Action<SpotRates> clicked)
    {
        _s = style;
        _columns = columns;
        _pictures = Math.Max(1, pictures);
        _bonusName = bonusName;
        _sorted = sorted;
        _clicked = clicked;
        Sort = sort;
        Descending = descending;

        float namePart = EnemyPart + (_pictures - 1) * (Picture + PictureGap);
        float width = namePart + columns.Sum(c => c.Width + 6) + 30;
        Root = _s.Column("Fishing spots, per hour", out VBoxContainer body, width);
        Root.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        // The names over the numbers, with the rows' own padding and the scroll bar's room (SimView.BuildResults).
        var header = _s.Spaced(new HBoxContainer(), 6);
        var headerMargin = new MarginContainer();
        headerMargin.AddThemeConstantOverride("margin_left", _s.Px(8));
        headerMargin.AddThemeConstantOverride("margin_right", _s.Px(8 + 12));
        headerMargin.AddChild(header);
        header.AddChild(HeaderButton("Fishing spot", "The fishing spots you can see on the Fishing page, in its order", 0, NameKey));
        foreach (RateColumn column in columns)
        {
            Button title = HeaderButton(column.Title, column.Help, column.Width, column.Key);
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
        _lines = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, RowGap);
        _scroll.AddChild(_lines);
        body.AddChild(_scroll);

        _empty = _s.Text("No fishing spot found yet.", GameStyle.TextGrey);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.Hide();
        body.AddChild(_empty);
    }

    public int ScrollAt
    {
        get => _scroll.ScrollVertical;
        set => _scroll.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, value);
    }

    /// <summary>
    /// Shows these spots, given in the order of the Fishing page. True when the list has another number of lines
    /// in sight than before: the window is to be fitted to it.
    /// </summary>
    public bool Show(IReadOnlyList<SpotRates> spots)
    {
        PaintHeaders();
        var gone = new HashSet<ActiveFishingRecipe>(_rows.Keys, ReferenceEqualityComparer.Instance);
        var best = Best(spots);
        int index = 0;
        foreach (SpotRates spot in Sorted(spots))
        {
            gone.Remove(spot.Active);
            if (!_rows.TryGetValue(spot.Active, out Row? row))
            {
                row = BuildRow();
                _lines.AddChild(row.Button);
                _rows.Add(spot.Active, row);
            }
            Fill(row, spot, best);
            if (row.Button.GetIndex() != index) _lines.MoveChild(row.Button, index);
            index++;
        }
        // A spot the game no longer shows (another character's, or hidden again by a game update).
        foreach (ActiveFishingRecipe active in gone)
        {
            Button old = _rows[active].Button;
            _lines.RemoveChild(old);
            old.QueueFree();
            _rows.Remove(active);
        }
        _empty.Visible = index == 0;
        _scroll.Visible = index > 0;
        int shown = Math.Clamp(index, 1, MostRows);
        if (shown == _shownRows) return false;
        _shownRows = shown;
        _scroll.CustomMinimumSize = new Vector2(0, _s.Px(RowHeight) * shown + _s.Px(RowGap) * (shown - 1));
        return true;
    }

    // ---- sorting (SimViewColumns.cs: SortedBy, SortBy, Sorted) -------------------------------------------------------

    // The column the list is sorted by while it is shown; the page's order otherwise (the bait column after the
    // bait was taken off).
    string SortedBy() => _columns.Any(c => c.Key == Sort) ? Sort : NameKey;
    bool SortedDescending => SortedBy() == Sort && Descending;

    // The first click on a column puts the best spots on top, the next turns the order around.
    void SortBy(string key)
    {
        bool descending = SortedBy() == key ? !SortedDescending : key != NameKey && _columns.First(c => c.Key == key).Better != Better.Less;
        Sort = key;
        Descending = descending;
        _sorted(key, descending);
    }

    // Spots that can be fished, by the number; equal numbers in the page's order. A spot without the number next,
    // and the spots still locked last, in the page's order whichever way the list is turned.
    List<SpotRates> Sorted(IReadOnlyList<SpotRates> spots)
    {
        string key = SortedBy();
        if (key == NameKey)
        {
            var order = spots.ToList();
            if (SortedDescending) order.Reverse();
            return order;
        }
        RateColumn column = _columns.First(c => c.Key == key);
        var page = spots.Select((spot, i) => (spot, i)).ToDictionary(x => x.spot, x => x.i);
        var numbered = spots.Where(s => !s.Locked && column.Value(s) is { } v && double.IsFinite(v)).ToList();
        var ordered = (SortedDescending ? numbered.OrderByDescending(s => column.Value(s)) : numbered.OrderBy(s => column.Value(s)))
            .ThenBy(s => page[s]).ToList();
        ordered.AddRange(spots.Where(s => !s.Locked && !numbered.Contains(s)));
        ordered.AddRange(spots.Where(s => s.Locked));
        return ordered;
    }

    // The best number of every column among the spots that can be fished, when there is a choice (RecipeRatesMod.Update).
    Dictionary<string, double> Best(IReadOnlyList<SpotRates> spots)
    {
        var best = new Dictionary<string, double>();
        foreach (RateColumn column in _columns.Where(c => c.Better != Better.Neither))
        {
            var values = spots.Where(s => !s.Locked).Select(column.Value).OfType<double>().Where(double.IsFinite).ToList();
            if (values.Count > 1) best[column.Key] = column.Better == Better.More ? values.Max() : values.Min();
        }
        return best;
    }

    // Equal to the best, as far as the numbers can tell: two spots with the same times are both the best.
    static bool IsBest(double value, double best) => Math.Abs(value - best) <= 1e-9 + 0.0005 * Math.Abs(best);

    // ---- the lines ---------------------------------------------------------------------------------------------------

    // [fish][fish] Shallow Pools  Shallow Shores   Lv 20      4.25s    847    8,470    1,694    847
    Row BuildRow()
    {
        var row = new Row();
        // The Combat Simulator's line (SimView.BuildRow): the game's outlined row button behind an icon, a name and
        // the numbers. A click opens the spot on the Fishing page (Restyle has its looks).
        row.Button = new Button
        {
            ThemeTypeVariation = "OutlineToggleButton", CustomMinimumSize = new Vector2(0, _s.Px(RowHeight)),
            FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.Arrow,
        };
        row.Button.Pressed += () =>
        {
            if (row.Spot is { Clickable: true } spot) _clicked(spot);
        };
        var line = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 6);
        line.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.Minsize, _s.Px(8));
        row.Button.AddChild(line);

        // The spot's fish, left to right in the order of its table, in a box as wide as the most fish a spot has, so
        // the names line up.
        var fish = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, PictureGap);
        fish.CustomMinimumSize = new Vector2(_s.Px(Picture) * _pictures + _s.Px(PictureGap) * (_pictures - 1), 0);
        for (int i = 0; i < _pictures; i++)
        {
            TextureRect picture = _s.Picture(null, (int)Picture);
            fish.AddChild(picture);
            row.Pictures.Add(picture);
        }
        line.AddChild(fish);
        row.Name = Quiet(_s.Text(""));
        row.Name.ClipText = true;
        row.Name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        line.AddChild(row.Name);
        line.AddChild(row.Place = Quiet(_s.Text("", GameStyle.TextPaper, 12)));
        row.Level = Quiet(_s.Text("", GameStyle.TextDim, 12));
        row.Level.CustomMinimumSize = new Vector2(_s.Px(36), 0);
        row.Level.HorizontalAlignment = HorizontalAlignment.Right;
        line.AddChild(row.Level);
        foreach (RateColumn column in _columns)
        {
            Label cell = Quiet(_s.Text("", GameStyle.TextWhite, 14, GameStyle.SemiBold));
            cell.HorizontalAlignment = HorizontalAlignment.Right;
            cell.CustomMinimumSize = new Vector2(_s.Px(column.Width), 0);
            line.AddChild(cell);
            row.Cells.Add(cell);
        }
        return row;
    }

    static Label Quiet(Label label)
    {
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        return label;
    }

    void Fill(Row row, SpotRates spot, Dictionary<string, double> best)
    {
        row.Spot = spot;
        Restyle(row, spot.OnPage, spot.Clickable);
        // A locked spot is greyed in its words; its pictures are kept, as the Item Encyclopedia keeps the picture of
        // an item never found (EncyclopediaView.Fill).
        for (int i = 0; i < row.Pictures.Count; i++)
        {
            Texture2D? picture = i < spot.Pictures.Count ? spot.Pictures[i] : null;
            if (row.Pictures[i].Texture != picture) row.Pictures[i].Texture = picture;
        }
        Set(row.Name, spot.Name, spot.Locked ? GameStyle.TextDim : GameStyle.TextWhite);
        Set(row.Place, spot.Location, spot.Locked ? GameStyle.TextDim : GameStyle.TextPaper);
        Set(row.Level, !spot.Locked ? "" : spot.Level is { } level ? "Lv " + level : "Locked", GameStyle.TextDim);
        row.Level.Visible = row.Level.Text.Length > 0;
        for (int i = 0; i < _columns.Count; i++)
        {
            RateColumn column = _columns[i];
            double? value = column.Value(spot);
            bool number = value is { } v && double.IsFinite(v);
            Color color = !number || spot.Locked ? GameStyle.TextDim
                : best.TryGetValue(column.Key, out double top) && IsBest(value!.Value, top) ? GameStyle.Highlight : GameStyle.TextWhite;
            Set(row.Cells[i], number ? column.Format(value!.Value) : "-", color);
        }
        string tip = Words.Tooltip(spot, _bonusName);
        if (row.Button.TooltipText != tip) row.Button.TooltipText = tip;
    }

    static void Set(Label label, string text, Color color)
    {
        if (label.Text != text) label.Text = text;
        // Through the meta, not the theme: asking a label for its colour override every half second costs more.
        if (!label.HasMeta("colour") || label.GetMeta("colour").AsColor() != color)
        {
            label.AddThemeColorOverride("font_color", color);
            label.SetMeta("colour", color);
        }
    }

    // The spot the Fishing page shows has the look of a selected line in the Combat Simulator (its pressed one), so it
    // can be found in a sorted list. A line a click opens has the theme's own looks, lighter under the pointer, with the
    // pointing hand, as Astrology Overview's rows that open a constellation (OverviewRowView.Update); one that a click
    // changes nothing for keeps its resting look in every state, with the arrow.
    void Restyle(Row row, bool lit, bool clickable)
    {
        var look = (Lit: lit, Clickable: clickable && !lit);
        if (row.Look == look || !row.Button.IsInsideTree()) return;
        // The theme's own look, not one this line was given before.
        foreach (string state in RowStates) row.Button.RemoveThemeStyleboxOverride(state);
        if (!look.Clickable)
        {
            StyleBox resting = row.Button.GetThemeStylebox(lit ? "pressed" : "normal");
            foreach (string state in RowStates) row.Button.AddThemeStyleboxOverride(state, resting);
        }
        row.Button.MouseDefaultCursorShape = look.Clickable ? Control.CursorShape.PointingHand : Control.CursorShape.Arrow;
        row.Look = look;
    }

    static readonly string[] RowStates = { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" };

    // ---- the names over the numbers (SimView.HeaderButton, SimViewColumns.PaintHeaders, MakeRoom, DrawSortArrow) -----

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

    // Only when the sort changed: the names are not painted again twice a second.
    void PaintHeaders()
    {
        string sorted = SortedBy(), painted = sorted + SortedDescending;
        if (painted == _painted) return;
        _painted = painted;
        Texture2D? arrow = Arrow(SortedDescending ? "down" : "up");
        foreach (Button header in _headers)
        {
            string key = header.GetMeta("column").AsString();
            Color color = key == sorted ? GameStyle.TextGold : GameStyle.TextGrey;
            foreach (string name in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
                header.AddThemeColorOverride(name, color);
            if (key != NameKey) MakeRoom(header, key == sorted ? arrow : null);
            header.QueueRedraw();   // the arrow
        }
    }

    static readonly string[] TitleStates = { "normal", "hover", "pressed", "hover_pressed", "disabled" };

    // A number's name stands on the right of its column, where its numbers end; the one the list is sorted by moves
    // left so that the arrow after it ends there (SimViewColumns.MakeRoom).
    void MakeRoom(Button title, Texture2D? arrow)
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
    }

    static float TitleWidth(Button title) =>
        title.GetThemeFont("font").GetStringSize(title.Text, HorizontalAlignment.Left, -1, title.GetThemeFontSize("font_size")).X;

    // The game's drop-down arrow after the name the list is sorted by: down for the most first, up for the least.
    void DrawSortArrow(Button header)
    {
        if (!header.HasMeta("column") || header.GetMeta("column").AsString() is not { } key || key != SortedBy()) return;
        if (Arrow(SortedDescending ? "down" : "up") is not { } arrow) return;
        StyleBox box = header.GetThemeStylebox("normal");
        float gap = header.GetThemeConstant("h_separation");
        float x = key == NameKey ? box.GetMargin(Side.Left) + TitleWidth(header) + gap : header.Size.X - box.GetMargin(Side.Right) + gap;
        header.DrawTextureRect(arrow, new Rect2(new Vector2(MathF.Round(x), MathF.Round((header.Size.Y - arrow.GetHeight()) / 2)), arrow.GetSize()), false);
    }

    // GameStyle.DropArrow at this window's size, turned for "up" (SimViewColumns.Arrow). Null when the game has no
    // such arrow any more: the sort is then shown by the gold name alone.
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

#if DEVTOOLS
    /// <summary>Development: the lines as they are drawn, top to bottom, with the pictures they show.</summary>
    public IEnumerable<(SpotRates Spot, Button Button, string Drawn, List<Texture2D?> Pictures)> DevLines() => _lines.GetChildren().OfType<Button>()
        .Where(b => !b.IsQueuedForDeletion())
        .Select(b => _rows.Values.First(r => r.Button == b))
        .Select(r => (r.Spot!, r.Button,
            $"'{r.Name.Text}' ({Colour(r.Name)}) '{r.Place.Text}' '{r.Level.Text}' | " +
            string.Join(" | ", _columns.Select((c, i) => $"{c.Title} '{r.Cells[i].Text}' ({Colour(r.Cells[i])})")) +
            $" | a click opens something={r.Look?.Clickable}, cursor {r.Button.MouseDefaultCursorShape} | lit={r.Look?.Lit}",
            r.Pictures.Where(p => p.IsVisibleInTree() && p.Texture != null).Select(p => (Texture2D?)p.Texture).ToList()));

    /// <summary>Development: how many fish pictures a line has room for.</summary>
    public int DevPictureRoom => _pictures;

    /// <summary>Development: the name over a column, by its key.</summary>
    public Button? DevHeader(string key) => _headers.FirstOrDefault(h => h.GetMeta("column").AsString() == key);

    /// <summary>Development: the names over the list, left to right.</summary>
    public IReadOnlyList<Button> DevHeaders => _headers;

    public ScrollContainer DevScroll => _scroll;

    static string Colour(Label label)
    {
        Color c = label.GetThemeColor("font_color");
        return c == GameStyle.Highlight ? "gold" : c == GameStyle.TextDim ? "dim" : c == GameStyle.TextWhite ? "white" : c == GameStyle.TextPaper ? "paper" : c.ToHtml();
    }
#endif
}
