using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2.Entities.Hunting;
using Godot;
using MelvorMods.Shared;

namespace HuntingRates;

/// <summary>Which way a number is better (CombatSim's Better). The first click on its column puts the best animals on top.</summary>
enum Better { More, Less, Neither }

/// <summary>
/// One number of the list, as Woodcutting Rates and Fishing Rates make their columns (mods/WoodcuttingRates/RatesList.cs,
/// RateColumn), which made them as the Combat Simulator's are (mods/CombatSim/SimColumns.cs, SimColumn): its short name
/// over the list, what it is in the name's tooltip, its width at full size, the number of an animal (null for none) and
/// which way it is better.
/// </summary>
sealed record RateColumn(string Key, string Title, string Tip, float Width, Func<AnimalRates, double?> Value, Func<double, string> Format, Better Better)
{
    /// <summary>What the name over the list says when the pointer rests on it, with the Combat Simulator's last line.</summary>
    public string Help => Tip + "\n" + Better switch
    {
        Better.More => "More is better",
        Better.Less => "Less is better",
        _ => "Neither more nor less is better",
    };

    /// <summary>
    /// The columns, left to right: the issue's (#72), with Fishing Rates' names and widths where they mean the same, and
    /// Traps where Fishing Rates has Bait (what an hour uses: every hunt with a trap needs one; the bait, which only some
    /// animals have switched on, is in the tooltip). Mastery XP is not a column: Hunting keeps no mastery XP per action by
    /// the game's names (its Mastery Guild, HuntingEntity.MasteryGuild, counts ranks, points and tasks); an animal's
    /// journal XP has base values only (HuntingAnimal.BaseJournalTrackingExperience, BaseJournalTrappingExperience), which
    /// the tooltip names. Every column rests on how a hunt's links join (AnimalRates.ChainRule); Traps also on when traps
    /// are used up (UseRule), XP on the XP of a catch within the page's range. While a rule is not settled, what rests on it
    /// is "-" and the note under the list says why, once. A tip's line is never longer than Fishing Rates' longest (82
    /// characters): Godot's tooltip does not wrap.
    /// </summary>
    public static List<RateColumn> For() =>
    [
        new("time", "Time", string.Join("\n", ChainTip("Seconds for one catch: surveying, tracking and trapping, escapes too")), 58,
            a => a.SecondsPerCatch, Words.Seconds, Better.Less),
        new("catches", "Catches", string.Join("\n", ChainTip("Animals caught per hour")), 66, a => a.CatchesPerHour, Words.Number, Better.More),
        new("xp", "XP", string.Join("\n", ChainTip("Hunting XP per hour: the XP of a catch, the middle of the page's range")), 66,
            a => a.XpPerHour, Words.Number, Better.More),
        new("gold", "Gold", string.Join("\n", ChainTip("What the catches and surveys of an hour bring, in gold")), 66,
            a => a.GoldPerHour, Words.Number, Better.More),
        // Every hunt with a trap uses them, so the fastest animal uses the most: that is no reason to call any the best.
        new("traps", "Traps", string.Join("\n", ChainTip("Traps used per hour; none for an animal hunted without one")), 58,
            a => a.Trapless ? 0 : a.TrapsPerHour, Words.Number, Better.Neither),
    ];

    // A column's tip, and what backs the chain of a catch once it is settled: the game's words quoted, or what was measured.
    static IEnumerable<string> ChainTip(string first)
    {
        yield return first;
        if (AnimalRates.ChainRule is not { } chain) yield break;
        yield return "How the steps of a hunt join: " + Words.How(chain.Source);
        if (chain.Source == RuleSource.Measured && chain.Seen.Length > 0) yield return "Measured: " + chain.Seen;
        if (chain.Quote.Length > 0)
            foreach (string line in chain.Quote.Split('\n'))
                yield return $"\"{line}\"";
    }
}

/// <summary>
/// The window's list: one line per animal the Hunting page shows, in a titled column. Copied from Woodcutting Rates' list
/// (mods/WoodcuttingRates/RatesList.cs), itself Mining Rates' and Fishing Rates' (mods/FishingRates/RatesList.cs), which
/// is made like the Combat Simulator's results (mods/CombatSim/SimView.cs, BuildResults): the names over the numbers sort
/// the list, the game's own arrow beside the one it is sorted by (SimViewColumns.cs). The best number of each column is
/// in the gold of Recipe Rates' best recipe (GameStyle.Highlight). An animal that cannot be hunted yet is greyed, with
/// the level it needs. Each line starts with the animal's picture as the Hunting page's list shows it (its own, the
/// page's for a spider while the game's spider setting is on, and for an animal not discovered yet what the page draws),
/// never the game's grey stand-in, then its name and its location, as Fishing Rates writes a spot's location. A click on
/// a line shows that animal on the Hunting page (HuntingRatesMod.ShowOnPage); it starts nothing. Under the list, why
/// whole columns show "-", as Thieving Overview says it (mods/ThievingOverview/OverviewView.cs, its note).
/// </summary>
sealed class RatesList
{
    /// <summary>The first column: the animals in the order of the Hunting page.</summary>
    public const string NameKey = "name";
    // The part of a line left of the numbers at full size: Fishing Rates' with one picture, the Combat Simulator's enemy
    // part with the stance after the name (SimViewColumns.EnemyPart, its one 20 px picture included), here the
    // location after the animal's name, as Fishing Rates writes a spot's.
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
        public AnimalRates? Animal;
    }

    readonly GameStyle _s;
    readonly List<RateColumn> _columns;
    readonly BonusNames _names;
    readonly Func<string?> _noTool;
    readonly Action<string, bool> _sorted;
    readonly Action<AnimalRates> _clicked;
    readonly List<Button> _headers = new();
    readonly Dictionary<ActiveHuntingAnimal, Row> _rows = new(ReferenceEqualityComparer.Instance);
    readonly ScrollContainer _scroll;
    readonly VBoxContainer _lines;
    readonly Label _empty, _note;
    readonly Dictionary<string, Texture2D?> _arrows = new();
    int _shownRows = -1;
    bool? _noteShown;
    string _painted = "";

    public Control Root { get; }
    public string Sort { get; private set; }
    public bool Descending { get; private set; }

    /// <param name="names">The game's names of an animal's chances, once they have been read (BonusNames).</param>
    /// <param name="noTool">The page's words for an animal that needs no tool, or null.</param>
    /// <param name="clicked">A line was clicked: its animal, to show on the Hunting page.</param>
    public RatesList(GameStyle style, List<RateColumn> columns, string sort, bool descending,
        BonusNames names, Func<string?> noTool, Action<string, bool> sorted, Action<AnimalRates> clicked)
    {
        _s = style;
        _columns = columns;
        _names = names;
        _noTool = noTool;
        _sorted = sorted;
        _clicked = clicked;
        Sort = sort;
        Descending = descending;

        float width = EnemyPart + columns.Sum(c => c.Width + 6) + 30;
        Root = _s.Column("Animals, per hour", out VBoxContainer body, width);
        Root.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        // The names over the numbers, with the rows' own padding and the scroll bar's room (SimView.BuildResults).
        var header = _s.Spaced(new HBoxContainer(), 6);
        var headerMargin = new MarginContainer();
        headerMargin.AddThemeConstantOverride("margin_left", _s.Px(8));
        headerMargin.AddThemeConstantOverride("margin_right", _s.Px(8 + 12));
        headerMargin.AddChild(header);
        header.AddChild(HeaderButton("Animal", "The animals you can see on the Hunting page, in its order", 0, NameKey));
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

        _empty = _s.Text("No animal found yet.", GameStyle.TextGrey);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.Hide();
        body.AddChild(_empty);

        // Why whole columns show "-", when they do, said once (OverviewView's note: wrapped, grey, 12).
        _note = _s.Text("", GameStyle.TextGrey, 12);
        _note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _note.CustomMinimumSize = new Vector2(_s.Px(width - 30), 0);
        _note.MouseFilter = Control.MouseFilterEnum.Ignore;
        _note.HorizontalAlignment = HorizontalAlignment.Center;
        _note.Hide();
        body.AddChild(_note);
    }

    public int ScrollAt
    {
        get => _scroll.ScrollVertical;
        set => _scroll.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, value);
    }

    /// <summary>
    /// Shows these animals, given in the order of the Hunting page, and the note under them ("" for none). True when the
    /// list has another number of lines in sight than before, or the note came or went: the window is to be fitted to it.
    /// </summary>
    public bool Show(IReadOnlyList<AnimalRates> animals, string note)
    {
        PaintHeaders();
        var gone = new HashSet<ActiveHuntingAnimal>(_rows.Keys, ReferenceEqualityComparer.Instance);
        var best = Best(animals);
        int index = 0;
        foreach (AnimalRates animal in Sorted(animals))
        {
            gone.Remove(animal.Active);
            if (!_rows.TryGetValue(animal.Active, out Row? row))
            {
                row = BuildRow();
                _lines.AddChild(row.Button);
                _rows.Add(animal.Active, row);
            }
            Fill(row, animal, best);
            if (row.Button.GetIndex() != index) _lines.MoveChild(row.Button, index);
            index++;
        }
        // An animal the page no longer shows (another character's, or one hidden again by the game).
        foreach (ActiveHuntingAnimal animal in gone)
        {
            Button old = _rows[animal].Button;
            _lines.RemoveChild(old);
            old.QueueFree();
            _rows.Remove(animal);
        }
        _empty.Visible = index == 0;
        _scroll.Visible = index > 0;
        // Other words may take another number of lines: the window is fitted again then too.
        bool said = _note.Text != note || _noteShown != (note != "");
        if (_note.Text != note) _note.Text = note;
        _note.Visible = note != "";
        _noteShown = _note.Visible;
        int shown = Math.Clamp(index, 1, MostRows);
        if (shown == _shownRows && !said) return false;
        _shownRows = shown;
        _scroll.CustomMinimumSize = new Vector2(0, _s.Px(RowHeight) * shown + _s.Px(RowGap) * (shown - 1));
        return true;
    }

    // ---- sorting (SimViewColumns.cs: SortedBy, SortBy, Sorted) -------------------------------------------------------

    // The column the list is sorted by while it is shown; the page's order otherwise.
    string SortedBy() => _columns.Any(c => c.Key == Sort) ? Sort : NameKey;
    bool SortedDescending => SortedBy() == Sort && Descending;

    // The first click on a column puts the best animals on top, the next turns the order around.
    void SortBy(string key)
    {
        bool descending = SortedBy() == key ? !SortedDescending : key != NameKey && _columns.First(c => c.Key == key).Better != Better.Less;
        Sort = key;
        Descending = descending;
        _sorted(key, descending);
    }

    // Animals that can be hunted, by the number; equal numbers in the page's order. An animal without the number next,
    // and the animals still locked last, in the page's order whichever way the list is turned.
    List<AnimalRates> Sorted(IReadOnlyList<AnimalRates> animals)
    {
        string key = SortedBy();
        if (key == NameKey)
        {
            var order = animals.ToList();
            if (SortedDescending) order.Reverse();
            return order;
        }
        RateColumn column = _columns.First(c => c.Key == key);
        var page = animals.Select((animal, i) => (animal, i)).ToDictionary(x => x.animal, x => x.i);
        var numbered = animals.Where(t => !t.Locked && column.Value(t) is { } v && double.IsFinite(v)).ToList();
        var ordered = (SortedDescending ? numbered.OrderByDescending(t => column.Value(t)) : numbered.OrderBy(t => column.Value(t)))
            .ThenBy(t => page[t]).ToList();
        ordered.AddRange(animals.Where(t => !t.Locked && !numbered.Contains(t)));
        ordered.AddRange(animals.Where(t => t.Locked));
        return ordered;
    }

    // The best number of every column among the animals that can be hunted, when there is a choice (RecipeRatesMod.Update).
    Dictionary<string, double> Best(IReadOnlyList<AnimalRates> animals)
    {
        var best = new Dictionary<string, double>();
        foreach (RateColumn column in _columns.Where(c => c.Better != Better.Neither))
        {
            var values = animals.Where(t => !t.Locked).Select(column.Value).OfType<double>().Where(double.IsFinite).ToList();
            if (values.Count > 1) best[column.Key] = column.Better == Better.More ? values.Max() : values.Min();
        }
        return best;
    }

    // Equal to the best, as far as the numbers can tell: two animals with the same numbers are both the best.
    static bool IsBest(double value, double best) => Math.Abs(value - best) <= 1e-9 + 0.0005 * Math.Abs(best);

    // ---- the lines ---------------------------------------------------------------------------------------------------

    // [animal] Hare  Grasslands   Lv 15      -    -    -    -    -
    Row BuildRow()
    {
        var row = new Row();
        // The Combat Simulator's line (SimView.BuildRow): the game's outlined row button behind an icon, a name and the
        // numbers. A click shows the animal on the Hunting page (Restyle has its looks).
        row.Button = new Button
        {
            ThemeTypeVariation = "OutlineToggleButton", CustomMinimumSize = new Vector2(0, _s.Px(RowHeight)),
            FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.Arrow,
        };
        row.Button.Pressed += () =>
        {
            if (row.Animal is { Clickable: true } animal) _clicked(animal);
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

    void Fill(Row row, AnimalRates animal, Dictionary<string, double> best)
    {
        row.Animal = animal;
        Restyle(row, animal.OnPage, animal.Clickable);
        // A locked animal is greyed in its words; its picture is kept, as the Item Encyclopedia keeps the picture of an item
        // never found (EncyclopediaView.Fill). The picture is drawn in the colour the page draws it in (an animal not
        // discovered yet: HuntingRatesMod.Look).
        if (row.Picture.Texture != animal.Picture) row.Picture.Texture = animal.Picture;
        if (row.Picture.SelfModulate != animal.PictureTint) row.Picture.SelfModulate = animal.PictureTint;
        Set(row.Name, animal.Name, animal.Locked ? GameStyle.TextDim : GameStyle.TextWhite);
        Set(row.Place, animal.Location, animal.Locked ? GameStyle.TextDim : GameStyle.TextPaper);
        Set(row.Level, !animal.Locked ? "" : animal.Level is { } level ? "Lv " + level : "Locked", GameStyle.TextDim);
        row.Level.Visible = row.Level.Text.Length > 0;
        for (int i = 0; i < _columns.Count; i++)
        {
            RateColumn column = _columns[i];
            double? value = column.Value(animal);
            bool number = value is { } v && double.IsFinite(v);
            Color color = !number || animal.Locked ? GameStyle.TextDim
                : best.TryGetValue(column.Key, out double top) && IsBest(value!.Value, top) ? GameStyle.Highlight : GameStyle.TextWhite;
            Set(row.Cells[i], number ? column.Format(value!.Value) : "-", color);
        }
        string tip = Words.Tooltip(animal, _names, _noTool());
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

    // The animal the Hunting page's panel shows, the one selected there, has the look of a selected line in the Combat
    // Simulator (its pressed one), so it can be found in a sorted list, as Fishing Rates lights the spot its page shows. A
    // line a click shows something for has the theme's own looks, lighter under the pointer, with the pointing hand, as
    // Astrology Overview's rows that open a constellation (OverviewRowView.Update); one that a click changes nothing for
    // keeps its resting look in every state, with the arrow (Fishing Rates' RatesList.Restyle). As in Woodcutting Rates,
    // the lit line can be clicked too: the card of the animal selected can be out of sight in the page's list, or the
    // page can show the journal, and a click then shows it. It keeps its pressed look, lighter under the pointer (the
    // theme's hover_pressed).
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
    /// <summary>Development: the lines as they are drawn, top to bottom, with the picture each shows.</summary>
    public IEnumerable<(AnimalRates Animal, Button Button, string Drawn, Texture2D? Picture)> DevLines() => _lines.GetChildren().OfType<Button>()
        .Where(b => !b.IsQueuedForDeletion())
        .Select(b => _rows.Values.First(r => r.Button == b))
        .Select(r => (r.Animal!, r.Button,
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
    public string DevNote => _note.Visible ? _note.Text : "";
    public Label DevNoteLabel => _note;

    static string Colour(Label label)
    {
        Color c = label.GetThemeColor("font_color");
        return c == GameStyle.Highlight ? "gold" : c == GameStyle.TextDim ? "dim" : c == GameStyle.TextWhite ? "white" : c == GameStyle.TextPaper ? "paper" : c.ToHtml();
    }
#endif
}
