using System;
using System.Linq;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Entities.Astrology;
using GamesByMalcs.Melvor2.Localization;
using Godot;
using MelvorMods.Shared;

namespace AstrologyOverview;

/// <summary>
/// One constellation in the list, a row like the Item Encyclopedia's (EncyclopediaView.BuildRow): the game's
/// outlined row button behind the constellation's icon and name, with the study time on the right. Under them,
/// in small grey lines, what the next unlock needs and what every minor node gives, or what a locked
/// constellation is missing, in the game's own words and views.
/// </summary>
internal sealed class OverviewRowView
{
    public PanelContainer Root { get; } = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    public Button Button { get; }
    public DoubleFormattedRichTextLabel Cost { get; }
    public Label MajorCost { get; }
    readonly string _displayName;
    readonly GameWords _words;
    readonly Label _name, _time, _ready, _bonus, _note;
    readonly Control _timeBox;
    readonly VBoxContainer _requirements;
    Condition[] _missing = [];
    Color _nameColor = GameStyle.TextPaper;
    bool _styled;

    public OverviewRowView(GameStyle style, AstrologyConstellation c, GameWords words, DoubleFormattedRichTextLabel nativeCost)
    {
        _displayName = c.DisplayName.Text;
        _words = words;
        Root.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        Button = style.GameButton("", "OutlineToggleButton");
        Button.FocusMode = Control.FocusModeEnum.None;
        Root.AddChild(Button);

        // The panel sizes the button to what is laid over it, wrapped names and the game's requirement views too.
        var margin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        foreach (string side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride("margin_" + side, style.Px(6));
        Root.AddChild(margin);
        var line = style.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 6);
        margin.AddChild(line);
        TextureRect icon = style.Picture(c.Icon, 24);
        icon.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        line.AddChild(icon);
        var column = style.Spaced(new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        }, 2);
        line.AddChild(column);
        Label Text(Container into, string text, Color color, int size, Font? font = null)
        {
            var label = style.Text(text, color, size, font);
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.MouseFilter = Control.MouseFilterEnum.Ignore;
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            into.AddChild(label);
            return label;
        }

        // [name ........................ (clock) 4m 44s]
        var head = style.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 8);
        column.AddChild(head);
        _name = Text(head, _displayName, GameStyle.TextPaper, 14, GameStyle.Bold);
        var timeBox = style.Spaced(new HBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
        }, 4);
        // The game's interval icon, the one Combat Goals shows beside its period.
        if (GameStyle.Icon("TimeInterval_48") is { } clock) timeBox.AddChild(style.Picture(clock, 16));
        _time = style.Text("", GameStyle.TextPaper, 14, GameStyle.SemiBold);
        _time.MouseFilter = Control.MouseFilterEnum.Ignore;
        timeBox.AddChild(_time);
        head.AddChild(timeBox);
        _timeBox = timeBox;

        _ready = Text(column, "", GameStyle.TextPaper, 13, GameStyle.SemiBold);
        // A copy of the constellation screen's "Next unlock at ... Knowledge": its format, fonts and colour.
        Cost = new DoubleFormattedRichTextLabel
        {
            FormatString = nativeCost.FormatString, BbcodeEnabled = nativeCost.BbcodeEnabled,
            FitContent = true, ScrollActive = false, AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        foreach (string weight in new[] { "normal", "bold" })
        {
            string font = weight + "_font";
            Cost.AddThemeFontOverride(font, nativeCost.GetThemeFont(font));
            Cost.AddThemeFontSizeOverride(font + "_size", style.Px(nativeCost.GetThemeFontSize(font + "_size")));
        }
        foreach (string color in new[] { "default_color", "font_outline_color", "font_shadow_color" })
            Cost.AddThemeColorOverride(color, nativeCost.GetThemeColor(color));
        foreach (string length in new[] { "outline_size", "shadow_offset_x", "shadow_offset_y", "line_separation" })
            Cost.AddThemeConstantOverride(length, (int)MathF.Round(nativeCost.GetThemeConstant(length) * style.Scale));
        column.AddChild(Cost);
        MajorCost = Text(column, "", GameStyle.TextGrey, 12);
        // What every minor node gives, under the constellation screen's own heading for it.
        string bonus = ConstellationRow.Bonuses(c.PerMinorNodeBonuses, words);
        _bonus = Text(column, bonus == "" ? "" : words.BonusHeading is { } heading ? GameWords.Lead(heading, bonus) : "Every minor node gives " + bonus,
            GameStyle.TextGrey, 12);
        _note = Text(column, "", GameStyle.TextGrey, 12);
        _requirements = style.Spaced(new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 2);
        column.AddChild(_requirements);
    }

    /// <summary>
    /// <paramref name="canOpen"/>: a click opens the constellation. A row that cannot be opened keeps the
    /// resting look of the others, without the pointing hand and the light under the pointer: it is not
    /// unavailable, only not a way there. <paramref name="lockWords"/>: the words of the game's card over its
    /// requirements, for a locked one.
    /// </summary>
    public void Update(ConstellationRow row, bool best, bool canOpen, string? lockWords)
    {
        if (!_styled && Button.IsInsideTree())
        {
            Button.AddThemeStyleboxOverride("disabled", Button.GetThemeStylebox("normal"));
            _styled = true;
        }
        Button.Disabled = !canOpen;
        Button.MouseDefaultCursorShape = canOpen ? Control.CursorShape.PointingHand : Control.CursorShape.Arrow;
        Button.TooltipText = canOpen ? "Open " + _displayName : "";

        bool locked = row.Status == UnlockStatus.Locked, complete = row.Status == UnlockStatus.Complete;
        Color name = locked ? GameStyle.TextGrey : best ? GameStyle.Highlight : GameStyle.TextPaper;
        if (name != _nameColor)
        {
            _name.AddThemeColorOverride("font_color", name);
            _nameColor = name;
        }
        Set(_time, row.Time());
        _timeBox.Visible = _time.Visible;
        Set(_ready, row.Ready(_words));
        Cost.Visible = row.KnowledgeGoal.HasValue && !locked && !complete;
        if (row.KnowledgeGoal is int goal && Cost.Value != goal) Cost.Value = goal;
        Set(MajorCost, row.MajorCost);
        _bonus.Visible = _bonus.Text.Length > 0 && !locked && !complete;
        Set(_note, complete ? Native.AllUnlocked(null!).Text : locked ? lockWords ?? "" : "");
        if (locked || _missing.Length != 0) UpdateRequirements(row);
    }

    static void Set(Label label, string text)
    {
        if (label.Text != text) label.Text = text;
        label.Visible = text.Length > 0;
    }

    // The game's own requirement view, once for each requirement still missing (an "or" keeps its alternatives
    // together), rebuilt only when what is missing changes.
    void UpdateRequirements(ConstellationRow row)
    {
        var c = row.Constellation;
        var clauses = row.Status == UnlockStatus.Locked
            ? Native.Missing(c.Requirements).Concat(Native.Missing(c.SecondaryRequirements)).ToArray()
            : [];
        var missing = clauses.SelectMany(group => group).ToArray();
        if (_missing.SequenceEqual(missing)) return;
        _missing = missing;
        Release();
        foreach (var clause in clauses)
        {
            var view = new ConditionGroupView { ColourConditions = true, MouseFilter = Control.MouseFilterEnum.Ignore };
            _requirements.AddChild(view);
            view.Bind(new ConditionGroup(clause));
            foreach (var child in view.FindChildren("*", "Control", true, false).OfType<Control>())
                child.MouseFilter = Control.MouseFilterEnum.Ignore;
        }
    }

    /// <summary>Lets go of the game's requirement views before the row is thrown away.</summary>
    public void Release()
    {
        foreach (Node child in _requirements.GetChildren())
        {
            if (child is ConditionGroupView view) view.Unbind();
            _requirements.RemoveChild(child);
            child.QueueFree();
        }
    }

#if DEVTOOLS
    public string DevText => $"name '{_name.Text}' ({(_nameColor == GameStyle.Highlight ? "gold" : _nameColor == GameStyle.TextGrey ? "grey" : "paper")}) | " +
        $"time '{(_time.Visible ? _time.Text : "")}' | ready '{(_ready.Visible ? _ready.Text.Replace("\n", " / ") : "")}' | " +
        $"cost '{(Cost.Visible ? Cost.GetParsedText() : "")}' | major '{(MajorCost.Visible ? MajorCost.Text : "")}' | " +
        $"bonus '{(_bonus.Visible ? _bonus.Text : "")}' | note '{(_note.Visible ? _note.Text : "")}' | requirements {_requirements.GetChildCount()} | " +
        $"opens={!Button.Disabled} | tooltip '{Button.TooltipText}'";
#endif
}
