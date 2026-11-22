using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.InfoIcons;
using GamesByMalcs.Melvor2.InfoLabels;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.Modifiers;
using Godot;
using MelvorMods.Shared;

namespace SkillBonuses;

/// <summary>
/// What the window shows: the skill on the paper tag, the recipe the numbers are for (a drop-down when the page
/// has several), and every stat of the game's panel, in the panel's order, with its total as the page shows it
/// and under it each source of it, the largest first. Laid out like the Item Encyclopedia's details: a heading
/// per group, then one of the game's stat lines per source (EncyclopediaView.Lines and LineRow).
/// </summary>
sealed class BonusList
{
    const float Width = 400;

    sealed record Source(string Name, Texture2D? Icon, double Value, string Text);
    sealed record Stat(string Name, Texture2D? Icon, string Total, List<Source> Sources, bool Other)
    {
        public string Key => $"{Name}={Total}:{Other}:{string.Join(",", Sources.Select(s => s.Name + "=" + s.Text))}";
    }

    readonly GameWindow _window;
    readonly GameStyle _s;
    readonly OptionButton _picker;
    readonly Control _single;
    readonly TextureRect _singleIcon;
    readonly Label _singleName;
    readonly ScrollContainer _scroll;
    readonly VBoxContainer _rows;
    readonly Control _holder;
    readonly Pictures _pictures;
    ModifierStatLabel? _formatter;
    string _choicesKey = "\u0001", _rowsKey = "\u0001", _fitKey = "";

    /// <summary>Hidden: the game's row that formats the numbers, and the copies of card panels.</summary>
    public Node Holder => _holder;
    /// <summary>The window's size with one line of the list: what has to fit on the screen.</summary>
    public Vector2 Fixed { get; private set; }
    /// <summary>Development: the lines shown, one per stat.</summary>
    public IEnumerable<string> Shown => _shown.Select(s => s.Key);
    List<Stat> _shown = new();

    public BonusList(GameWindow window, Game game, string skill, Action<int> choose, Action<string> log)
    {
        _window = window;
        _s = window.Style;
        _pictures = new Pictures(game, log);
        Control column = _s.Column(skill, out VBoxContainer body, Width);
        window.Body.AddChild(column);

        // Whose numbers these are: the one recipe of the page, or a choice of its trees, rocks, targets or plots.
        _picker = _s.Dropdown();
        _picker.ClipText = true;
        _picker.FitToLongestItem = false;
        _picker.FocusMode = Control.FocusModeEnum.None;
        _picker.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _picker.TooltipText = "Show the bonuses of another one on this page";
        _picker.AddThemeConstantOverride("icon_max_width", _s.Px(16));
        _picker.GetPopup().AddThemeConstantOverride("icon_max_width", _s.Px(20));
        _picker.ItemSelected += index => choose((int)index);
        body.AddChild(_picker);

        var single = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(0, _s.Px(24)) }, 6);
        single.Alignment = BoxContainer.AlignmentMode.Center;
        _singleIcon = _s.Picture(null, 20);
        single.AddChild(_singleIcon);
        _singleName = _s.Text("", GameStyle.TextPaper, 14, GameStyle.Bold);
        _singleName.ClipText = true;
        _singleName.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        single.AddChild(_singleName);
        _single = single;
        body.AddChild(single);

        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _rows = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 4);
        _scroll.AddChild(_rows);
        body.AddChild(_scroll);

        _holder = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        body.AddChild(_holder);
    }

    public void SetChoices(List<Choice> choices, int selected)
    {
        string key = string.Join("\n", choices.Select(c => c.Name));
        if (key != _choicesKey)
        {
            _choicesKey = key;
            _picker.Clear();
            foreach (Choice choice in choices)
                if (_pictures.Shown(choice.Icon) is { } icon) _picker.AddIconItem(icon, choice.Name); else _picker.AddItem(choice.Name);
        }
        _picker.Visible = choices.Count > 1;
        _single.Visible = choices.Count == 1;
        if (choices.Count == 1)
        {
            _singleName.Text = choices[0].Name;
            _singleName.TooltipText = choices[0].Name;
            _singleIcon.Texture = _pictures.Shown(choices[0].Icon);
            _singleIcon.Visible = _singleIcon.Texture != null;
        }
        // Select does not raise ItemSelected: only the player's choice does.
        if (choices.Count > 1 && _picker.Selected != selected) _picker.Select(selected);
    }

    // ---- the stats ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Shows the stats of these rows of a game panel; <paramref name="note"/> instead when there are none. With
    /// <paramref name="pageText"/> each total is the text the row shows on the page; a copy's rows are not drawn
    /// and may not have written theirs yet, so their totals are formatted the same way the rows format them.
    /// </summary>
    public void Show(List<ModifierStatLabel> labels, ModifierStatView panel, bool pageText, string? note)
    {
        var stats = new List<Stat>();
        foreach (ModifierStatLabel label in labels)
            if (Read(label, panel, pageText) is { } stat) stats.Add(stat);
        string key = stats.Count == 0 ? "note:" + note : string.Join("|", stats.Select(s => s.Key));
        if (key == _rowsKey) return;
        _rowsKey = key;
        _shown = stats;
        foreach (Node old in _rows.GetChildren()) { _rows.RemoveChild(old); old.QueueFree(); }
        if (stats.Count == 0) { if (note != null) _rows.AddChild(Note(note)); return; }
        foreach (Stat stat in stats)
        {
            bool quiet = stat.Sources.Count == 0 && !stat.Other;
            _rows.AddChild(Heading(stat, quiet));
            if (quiet) continue;
            var box = _s.Spaced(new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass }, 2);
            foreach (Source source in stat.Sources) box.AddChild(Line(source.Icon, source.Name, source.Text, false));
            if (stat.Other) box.AddChild(Line(null, "Other", "", true));
            _rows.AddChild(box);
        }
    }

    // One stat: the game's own name, picture and format, the total the page shows, and the sources of the very
    // stat nodes the page shows.
    Stat? Read(ModifierStatLabel label, ModifierStatView panel, bool pageText)
    {
        if (SkillStats.Nodes(label) is not { } nodes) return null;
        ModifierValueFormat format = label.ValueFormat;
        string name = SkillStats.Name(label)?.Text is { Length: > 0 } shown ? shown : label.StatName;
        Texture2D? icon = _pictures.Shown(SkillStats.Icon(label)?.Texture);
        double total = nodes.Total.Value;
        // The page's own text; a row that has not written it yet is formatted the same way.
        string totalText = pageText && SkillStats.Number(label)?.Text is { Length: > 0 } page ? page : Format(total, format, panel, label);
        IReadOnlySet<ModifierEntry> entries = nodes.Sources.Value ?? new HashSet<ModifierEntry>();
        string zero = Format(0, format, panel, label);
        // One line per source: what one source gives this stat in several parts is one bonus, and so are sources
        // of the same kind and name. What adds nothing at the game's precision is left out.
        var sources = entries
            .Where(e => e.Source != null)
            .GroupBy(e => (e.Source.DisplayName.Text, e.Source.GetType()))
            .Select(g => (Name: g.Key.Text, g.First().Source, Value: g.Sum(e => e.Value)))
            .Select(s => new Source(s.Name, _pictures.Of(s.Source), s.Value, Format(s.Value, format, panel, label)))
            .Where(s => s.Text != zero)
            .OrderByDescending(s => Math.Abs(s.Value)).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // "Other" only when the difference shows at the page's own precision: the sum of the parts, formatted
        // the way the total is, reads differently from the total.
        bool other = Format(entries.Sum(e => e.Value), format, panel, label) != Format(total, format, panel, label);
        return new Stat(name, icon, totalText, sources, other);
    }

    // ---- numbers in the game's format -----------------------------------------------------------------------------

    /// <summary>
    /// A value written the way the game's stats panel writes it: an unbound copy of the game's own row is given
    /// the value and its text is read back. It is never shown and never bound to anything.
    /// </summary>
    string Format(double value, ModifierValueFormat format, ModifierStatView panel, ModifierStatLabel like)
    {
        if (_formatter == null || !GodotObject.IsInstanceValid(_formatter))
        {
            PackedScene? scene = SkillStats.LabelScene(panel) ?? (like.SceneFilePath is { Length: > 0 } path ? GameStyle.Load<PackedScene>(path) : null);
            _formatter = scene?.Instantiate() as ModifierStatLabel ?? throw new InvalidOperationException("the game's stat row could not be made");
            _holder.AddChild(_formatter);
        }
        _formatter.ValueFormat = format;
        _formatter.SetValue(value);
        DoubleLabel number = SkillStats.Number(_formatter) ?? throw new InvalidOperationException("the game's stat row has no number");
        // The game's number labels write their text on their next frame: written now, so the row can be reused.
        number._Process(0);
        return number.Text;
    }

    // ---- the window's height ---------------------------------------------------------------------------------------

    /// <summary>
    /// As high as what it holds, and never higher than <paramref name="room"/>, the screen above the game's
    /// bottom bar: a longer list scrolls.
    /// </summary>
    public void Fit(float room)
    {
        if (!_window.IsAlive || _window.Minimised) return;
        // Only when something changed: the lines, the recipe line, the room.
        string key = $"{_rowsKey}|{_choicesKey}|{_picker.Visible}|{room}";
        if (key == _fitKey) return;
        _fitKey = key;
        float content = _rows.GetCombinedMinimumSize().Y;
        float floor = _rows.GetChildCount() > 0 && _rows.GetChild(0) is Control first ? Math.Min(content, first.GetCombinedMinimumSize().Y) : content;
        _scroll.CustomMinimumSize = new Vector2(0, floor);
        Fixed = _window.Root.GetCombinedMinimumSize();
        float list = Math.Max(floor, Math.Min(content, room - (Fixed.Y - floor)));
        _scroll.CustomMinimumSize = new Vector2(0, MathF.Floor(list));
        _window.Root.ResetSize();
    }

    // ---- small pieces -------------------------------------------------------------------------------------------------

    // "[icon] Stat name                      total", the heading of a group as in the Item Encyclopedia's details.
    Control Heading(Stat stat, bool quiet)
    {
        var title = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass, CustomMinimumSize = new Vector2(0, _s.Px(24)) }, 6);
        title.AddChild(_s.Picture(stat.Icon, 16));
        Label name = Clipped(_s.Text(stat.Name, quiet ? GameStyle.TextDim : GameStyle.TextPaper, 14, GameStyle.Bold));
        name.TooltipText = stat.Name;
        title.AddChild(name);
        Label total = _s.Text(stat.Total, quiet ? GameStyle.TextDim : GameStyle.TextWhite, 14, GameStyle.Bold);
        total.HorizontalAlignment = HorizontalAlignment.Right;
        title.AddChild(total);
        return title;
    }

    // "[icon] Source                         value", on one of the game's stat lines.
    Control Line(Texture2D? icon, string name, string value, bool other)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        panel.AddThemeStyleboxOverride("panel", _s.StatLine());
        var line = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass }, 6);
        // Without a picture the place stays, empty: the names of all lines start in one column.
        line.AddChild(_s.Picture(icon, 20));
        Label what = Clipped(_s.Text(name, other ? GameStyle.TextDim : (Color?)null));
        what.TooltipText = other ? "The bonuses listed here do not add up to the total. The game names no source for the rest." : name;
        line.AddChild(what);
        Label number = _s.Text(value, GameStyle.TextWhite, 14, GameStyle.Bold);
        number.HorizontalAlignment = HorizontalAlignment.Right;
        number.CustomMinimumSize = new Vector2(_s.Px(54), 0);
        line.AddChild(number);
        panel.AddChild(line);
        return panel;
    }

    /// <summary>A label that takes the room there is and ends in "..." when its text is longer.</summary>
    static Label Clipped(Label label)
    {
        label.ClipText = true;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.MouseFilter = Control.MouseFilterEnum.Pass;
        return label;
    }

#if DEVTOOLS
    /// <summary>Development: a control of the window to click through the game's input.</summary>
    public Control? DevControl(string what) => what switch
    {
        "picker" => _picker.IsVisibleInTree() ? _picker : null,
        "row" => _rows.GetChildren().OfType<VBoxContainer>().FirstOrDefault()?.GetChildOrNull<Control>(0),
        "heading" => _rows.GetChildCount() > 0 ? _rows.GetChild(0) as Control : null,
        _ => null,
    };
#endif

    Label Note(string text)
    {
        Label label = _s.Text(text, GameStyle.TextGrey, 13);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.CustomMinimumSize = new Vector2(_s.Px(Width - 34), 0);
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        return label;
    }
}
