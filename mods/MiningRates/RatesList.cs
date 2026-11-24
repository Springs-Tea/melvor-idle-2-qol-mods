using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2.Entities.Mining;
using Godot;
using MelvorMods.Shared;

namespace MiningRates;

/// <summary>Which way a number is better (CombatSim's Better). The first click on its column puts the best rocks on top.</summary>
enum Better { More, Less, Neither }

/// <summary>
/// One number of the list, as the Combat Simulator's columns are made (mods/CombatSim/SimColumns.cs, SimColumn) and
/// Fishing Rates' are (mods/FishingRates/RatesList.cs, RateColumn): its short name over the list, what it is in the
/// name's tooltip, its width at full size, the number of a rock (null for none) and which way it is better.
/// </summary>
sealed record RateColumn(string Key, string Title, string Tip, float Width, Func<RockRates, double?> Value, Func<double, string> Format, Better Better)
{
    /// <summary>What the name over the list says when the pointer rests on it, with the Combat Simulator's last line.</summary>
    public string Help => Tip + "\n" + Better switch
    {
        Better.More => "More is better",
        Better.Less => "Less is better",
        _ => "Neither more nor less is better",
    };

    /// <summary>
    /// The columns, left to right: the issue's (#70), with Fishing Rates' names and widths where they mean the same.
    /// Mastery XP is not a column: Mining in this game keeps no mastery XP by the game's names (its Mastery Guild counts
    /// ranks, points and tasks). The time of an ore, the ore and the gold rest on the hits a break takes, by the rule the
    /// card's break chance states (RockRates.Settled). Without a rule they would
    /// not be columns at all, rather than three columns of "-", and Time would be the time of a hit, as Fishing Rates' is
    /// the time of a catch. A tip's line is never longer than Fishing Rates' longest (82 characters): Godot's tooltip does
    /// not wrap.
    /// </summary>
    public static List<RateColumn> All()
    {
        var xp = new RateColumn("xp", "XP", RockRates.KnownXp.Tip, 66,
            r => r.HasTime ? r.XpPerHour : null, Words.Number, Better.More);
        if (RockRates.Settled == null)
            return
            [
                new("time", "Time", "Seconds for one hit, with your bonuses", 58, r => r.HasTime ? r.HitSeconds : null, Words.Seconds, Better.Less),
                xp,
            ];
        const string topaz = "\nTopaz Vein added gems to the bank on 92 of 92 breaks and 0 of 708 other hits.";
        return
        [
            new("time", "Time", "Seconds per ore or gem, with your bonuses and all the hits a break takes." + topaz, 58,
                r => r.HasTime ? r.SecondsPerOre : null, Words.Seconds, Better.Less),
            new("ore", "Ore", "Ore or gems per hour, including any of the same item from hits." + topaz, 66,
                r => r.HasTime ? r.OrePerHour : null, Words.Number, Better.More),
            xp,
            new("gold", "Gold", "What the break rewards and items from hits sell for in an hour." + topaz, 66,
                r => r.HasTime ? r.GoldPerHour : null, Words.Number, Better.More),
        ];
    }
}

/// <summary>
/// The window's list: one line per rock the game shows, in a titled column like the Combat Simulator's results
/// (mods/CombatSim/SimView.cs, BuildResults), copied from Fishing Rates' list (mods/FishingRates/RatesList.cs): the
/// names over the numbers sort the list, the game's own arrow beside the one it is sorted by (SimViewColumns.cs). The
/// best number of each column is in the gold of Recipe Rates' best recipe (GameStyle.Highlight). A rock that cannot be
/// mined yet is greyed, with the level it needs. Each line starts with the rock's own picture, as its card on the
/// Mining page shows it; a rock the game has no picture for shows none, never the game's grey stand-in. A click on a
/// line shows that rock on the Mining page (MiningRatesMod.ShowOnPage); it starts nothing.
/// </summary>
sealed class RatesList
{
    /// <summary>The first column: the rocks in the order of the Mining page.</summary>
    public const string NameKey = "name";
    // The part of a line left of the numbers at full size: the Combat Simulator's enemy part with the stance after the
    // name (SimViewColumns.EnemyPart, its one 20 px picture included), here the category after the rock's name.
    const float EnemyPart = 320, Picture = 20;
    // A line is the Combat Simulator's (SimView.BuildRow): 30 high, 2 apart. More lines than this scroll.
    const float RowHeight = 30, RowGap = 2;
    const int MostRows = 14;

    sealed class Row
    {
        public Button Button = null!;
        public TextureRect Picture = null!;
        public Label Name = null!, Place = null!, Level = null!;
        public readonly List<Label> Cells = new();
        // The look the line was given (lit, opens something): null until it is in the tree and has the game's theme.
        public (bool Lit, bool Clickable)? Look;
        public RockRates? Rock;
    }

    readonly GameStyle _s;
    readonly List<RateColumn> _columns;
    readonly Func<string, string?> _bonusName;
    readonly Action<string, bool> _sorted;
    readonly Action<RockRates> _clicked;
    readonly List<Button> _headers = new();
    readonly Dictionary<MiningRock, Row> _rows = new(ReferenceEqualityComparer.Instance);
    readonly ScrollContainer _scroll;
    readonly VBoxContainer _lines;
    readonly Label _empty;
    readonly Dictionary<string, Texture2D?> _arrows = new();
    int _shownRows = -1;
    string _painted = "";

    public Control Root { get; }
    public string Sort { get; private set; }
    public bool Descending { get; private set; }

    /// <param name="bonusName">The game's name of a bonus that is not counted, once it has been read (BonusNames).</param>
    /// <param name="clicked">A line was clicked: its rock, to show on the Mining page.</param>
    public RatesList(GameStyle style, List<RateColumn> columns, string sort, bool descending,
        Func<string, string?> bonusName, Action<string, bool> sorted, Action<RockRates> clicked)
    {
        _s = style;
        _columns = columns;
        _bonusName = bonusName;
        _sorted = sorted;
        _clicked = clicked;
        Sort = sort;
        Descending = descending;

        float width = EnemyPart + columns.Sum(c => c.Width + 6) + 30;
        Root = _s.Column("Rocks, per hour", out VBoxContainer body, width);
        Root.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        // The names over the numbers, with the rows' own padding and the scroll bar's room (SimView.BuildResults).
        var header = _s.Spaced(new HBoxContainer(), 6);
        var headerMargin = new MarginContainer();
        headerMargin.AddThemeConstantOverride("margin_left", _s.Px(8));
        headerMargin.AddThemeConstantOverride("margin_right", _s.Px(8 + 12));
        headerMargin.AddChild(header);
        header.AddChild(HeaderButton("Rock", "The rocks you can see on the Mining page, in its order", 0, NameKey));
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

        _empty = _s.Text("No rock found yet.", GameStyle.TextGrey);
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
    /// Shows these rocks, given in the order of the Mining page. True when the list has another number of lines in
    /// sight than before: the window is to be fitted to it.
    /// </summary>
    public bool Show(IReadOnlyList<RockRates> rocks)
    {
        PaintHeaders();
        var gone = new HashSet<MiningRock>(_rows.Keys, ReferenceEqualityComparer.Instance);
        var best = Best(rocks);
        int index = 0;
        foreach (RockRates rock in Sorted(rocks))
        {
            gone.Remove(rock.Rock);
            if (!_rows.TryGetValue(rock.Rock, out Row? row))
            {
                row = BuildRow();
                _lines.AddChild(row.Button);
                _rows.Add(rock.Rock, row);
            }
            Fill(row, rock, best);
            if (row.Button.GetIndex() != index) _lines.MoveChild(row.Button, index);
            index++;
        }
        // A rock the game no longer shows (another character's, hidden by the player, or hidden by a game update).
        foreach (MiningRock rock in gone)
        {
            Button old = _rows[rock].Button;
            _lines.RemoveChild(old);
            old.QueueFree();
            _rows.Remove(rock);
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

    string SortedBy() => _columns.Any(c => c.Key == Sort) ? Sort : NameKey;
    bool SortedDescending => SortedBy() == Sort && Descending;

    // The first click on a column puts the best rocks on top, the next turns the order around.
    void SortBy(string key)
    {
        bool descending = SortedBy() == key ? !SortedDescending : key != NameKey && _columns.First(c => c.Key == key).Better != Better.Less;
        Sort = key;
        Descending = descending;
        _sorted(key, descending);
    }

    // Rocks that can be mined, by the number; equal numbers in the page's order. A rock without the number next, and the
    // rocks still locked last, in the page's order whichever way the list is turned.
    List<RockRates> Sorted(IReadOnlyList<RockRates> rocks)
    {
        string key = SortedBy();
        if (key == NameKey)
        {
            var order = rocks.ToList();
            if (SortedDescending) order.Reverse();
            return order;
        }
        RateColumn column = _columns.First(c => c.Key == key);
        var page = rocks.Select((rock, i) => (rock, i)).ToDictionary(x => x.rock, x => x.i);
        var numbered = rocks.Where(r => !r.Locked && column.Value(r) is { } v && double.IsFinite(v)).ToList();
        var ordered = (SortedDescending ? numbered.OrderByDescending(r => column.Value(r)) : numbered.OrderBy(r => column.Value(r)))
            .ThenBy(r => page[r]).ToList();
        ordered.AddRange(rocks.Where(r => !r.Locked && !numbered.Contains(r)));
        ordered.AddRange(rocks.Where(r => r.Locked));
        return ordered;
    }

    // The best number of every column among the rocks that can be mined, when there is a choice (RecipeRatesMod.Update).
    Dictionary<string, double> Best(IReadOnlyList<RockRates> rocks)
    {
        var best = new Dictionary<string, double>();
        foreach (RateColumn column in _columns.Where(c => c.Better != Better.Neither))
        {
            var values = rocks.Where(r => !r.Locked).Select(column.Value).OfType<double>().Where(double.IsFinite).ToList();
            if (values.Count > 1) best[column.Key] = column.Better == Better.More ? values.Max() : values.Min();
        }
        return best;
    }

    // Equal to the best, as far as the numbers can tell: two rocks with the same times are both the best.
    static bool IsBest(double value, double best) => Math.Abs(value - best) <= 1e-9 + 0.0005 * Math.Abs(best);

    // ---- the lines ---------------------------------------------------------------------------------------------------

    // [rock] Copper Rock  Ores   Lv 15     3s    1,200
    Row BuildRow()
    {
        var row = new Row();
        // The Combat Simulator's line (SimView.BuildRow): the game's outlined row button behind an icon, a name and the
        // numbers. A click shows the rock on the Mining page (Restyle has its looks).
        row.Button = new Button
        {
            ThemeTypeVariation = "OutlineToggleButton", CustomMinimumSize = new Vector2(0, _s.Px(RowHeight)),
            FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.Arrow,
        };
        row.Button.Pressed += () =>
        {
            if (row.Rock is { Clickable: true } rock) _clicked(rock);
        };
        var line = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 6);
        line.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.Minsize, _s.Px(8));
        row.Button.AddChild(line);

        line.AddChild(row.Picture = _s.Picture(null, (int)Picture));
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

    void Fill(Row row, RockRates rock, Dictionary<string, double> best)
    {
        row.Rock = rock;
        Restyle(row, rock.OnPage, rock.Clickable);
        // A locked rock is greyed in its words; its picture is kept, as the Item Encyclopedia keeps the picture of an
        // item never found (EncyclopediaView.Fill).
        if (row.Picture.Texture != rock.Picture) row.Picture.Texture = rock.Picture;
        Set(row.Name, rock.Name, rock.Locked ? GameStyle.TextDim : GameStyle.TextWhite);
        Set(row.Place, rock.Category, rock.Locked ? GameStyle.TextDim : GameStyle.TextPaper);
        Set(row.Level, !rock.Locked ? "" : rock.Level is { } level ? "Lv " + level : "Locked", GameStyle.TextDim);
        row.Level.Visible = row.Level.Text.Length > 0;
        for (int i = 0; i < _columns.Count; i++)
        {
            RateColumn column = _columns[i];
            double? value = column.Value(rock);
            bool number = value is { } v && double.IsFinite(v);
            Color color = !number || rock.Locked ? GameStyle.TextDim
                : best.TryGetValue(column.Key, out double top) && IsBest(value!.Value, top) ? GameStyle.Highlight : GameStyle.TextWhite;
            Set(row.Cells[i], number ? column.Format(value!.Value) : "-", color);
        }
        string tip = Words.Tooltip(rock, _bonusName);
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

    // The rock the Mining page shows has the look of a selected line in the Combat Simulator (its pressed one), so it can
    // be found in a sorted list. A line a click shows has the theme's own looks, lighter under the pointer, with the
    // pointing hand, as Astrology Overview's rows that open a constellation (OverviewRowView.Update); one that a click
    // changes nothing for keeps its resting look in every state, with the arrow (Fishing Rates' Restyle). Unlike Fishing
    // Rates' lit line, this one can be clicked: the card of the rock being mined can be out of sight on the Mining page,
    // and a click then scrolls to it. It keeps its pressed look, lighter under the pointer (the theme's hover_pressed).
    void Restyle(Row row, bool lit, bool clickable)
    {
        var look = (Lit: lit, Clickable: clickable);
        if (row.Look == look || !row.Button.IsInsideTree()) return;
        // The theme's own look, not one this line was given before.
        foreach (string state in RowStates) row.Button.RemoveThemeStyleboxOverride(state);
        if (!look.Clickable)
        {
            StyleBox resting = row.Button.GetThemeStylebox(lit ? "pressed" : "normal");
            foreach (string state in RowStates) row.Button.AddThemeStyleboxOverride(state, resting);
        }
        else if (lit)
        {
            StyleBox pressed = row.Button.GetThemeStylebox("pressed"), hover = row.Button.GetThemeStylebox("hover_pressed");
            foreach (string state in RowStates) row.Button.AddThemeStyleboxOverride(state, state is "hover" or "hover_pressed" ? hover : pressed);
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

    // A number's name stands on the right of its column, where its numbers end; the one the list is sorted by moves left
    // so that the arrow after it ends there (SimViewColumns.MakeRoom).
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

    // GameStyle.DropArrow at this window's size, turned for "up" (SimViewColumns.Arrow). Null when the game has no such
    // arrow any more: the sort is then shown by the gold name alone.
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
    /// <summary>Development: the lines as they are drawn, top to bottom, with the picture each shows.</summary>
    public IEnumerable<(RockRates Rock, Button Button, string Drawn, Texture2D? Picture)> DevLines() => _lines.GetChildren().OfType<Button>()
        .Where(b => !b.IsQueuedForDeletion())
        .Select(b => _rows.Values.First(r => r.Button == b))
        .Select(r => (r.Rock!, r.Button,
            $"'{r.Name.Text}' ({Colour(r.Name)}) '{r.Place.Text}' ({Colour(r.Place)}) '{r.Level.Text}' | " +
            string.Join(" | ", _columns.Select((c, i) => $"{c.Title} '{r.Cells[i].Text}' ({Colour(r.Cells[i])})")) +
            $" | a click shows something={r.Look?.Clickable}, cursor {r.Button.MouseDefaultCursorShape} | lit={r.Look?.Lit}",
            r.Picture.IsVisibleInTree() ? r.Picture.Texture : null));

    /// <summary>Development: the name over a column, by its key.</summary>
    public Button? DevHeader(string key) => _headers.FirstOrDefault(h => h.GetMeta("column").AsString() == key);

    /// <summary>Development: the keys of the columns, left to right.</summary>
    public IEnumerable<string> DevColumns => _columns.Select(c => c.Key);

    public IReadOnlyList<Button> DevHeaders => _headers;

    public ScrollContainer DevScroll => _scroll;

    static string Colour(Label label)
    {
        Color c = label.GetThemeColor("font_color");
        return c == GameStyle.Highlight ? "gold" : c == GameStyle.TextDim ? "dim" : c == GameStyle.TextWhite ? "white" : c == GameStyle.TextPaper ? "paper" : c.ToHtml();
    }
#endif
}
