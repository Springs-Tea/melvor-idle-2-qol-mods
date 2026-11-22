using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.Actions.Conditions;
using GamesByMalcs.Melvor2.Components.ActionUntilX;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Components.Trackers;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Core.TickTimer;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.GameMenus.Footer;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace CombatGoals;

/// <summary>
/// While a skilling action runs towards a stop the player set, a part at the end of the game's footer says how
/// long until that stop ends the action, worded as the footer words its own times: "1m 38s until Stop Condition".
/// Nothing more: the footer already shows the XP per hour, the time to a level and when the materials run out.
/// The pace is measured over the footer's own time period, the time is written by the footer's own formatter in
/// the footer's own fonts, and the tooltip starts with the stop's text as the game's stop panel shows it.
/// </summary>
sealed class SkillingAction(ModContext ctx)
{
    internal const string TagName = "CombatGoalsStop";
    static readonly StringName TagNodeName = TagName;

    // How long the footer's content must end further left before the time moves left after it. Its numbers change
    // width by a pixel or two from one second to the next; the time does not follow every such step back and
    // forth, but it never stays far from the content for longer than this. A choice of the mod, not the game's.
    const ulong HoldLeftMs = 3000;

    // The words after the time. The footer writes "42m 39s left to Level 45" and "23h 22m 14s until Rune Essence
    // depleted"; the skill's page heads the stop with "Stop Condition:".
    internal const string Words = "until Stop Condition";

    // Private members of the game's views: what the footer shows and where, the stop panel's text, and the
    // footer's time-to-level formatter.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Tracker")]
    internal static extern TrackerComponent? TrackerOf(TrackerComponentFooterView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrentActionName")]
    internal static extern Label? ActionNameOf(TrackerComponentFooterView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrentActionIcon")]
    static extern TextureRect? ActionIconOf(TrackerComponentFooterView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_MainScrollContainer")]
    internal static extern ScrollContainer? ScrollOf(TrackerComponentFooterView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ExperienceView")]
    static extern TrackerExperienceView? ExperienceOf(TrackerComponentFooterView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TimeToLevel")]
    static extern RichTextLabel? TimeToLevelOf(TrackerExperienceView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_UntilX")]
    internal static extern ActionUntilXComponent? UntilXOf(ActionUntilXStopView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_StopDescription")]
    internal static extern RichTextLabel? StopTextOf(ActionUntilXStopView view);
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "FormatTimeToLevel")]
    internal static extern string FormatTimeToLevel(TrackerExperienceView? type, double timeRequired);

    // FormatTimeToLevel is given milliseconds. The game check of turn 3 settled it at run time: one hour read
    // "1h 0m" only when given as 3,600,000 (the development log's "one hour through FormatTimeToLevel" part).
    internal const double FormatterUnitsPerSecond = 1000;

    // The footer's look as the time copies it: the frame of the action's part, the icon's size and the gap after
    // it, and the fonts and colours of the footer's own "42m 39s left to".
    readonly record struct Look(StyleBox? Frame, float Icon, int Gap, Font? TimeFont, int TimeSize, Font? WordsFont, int WordsSize,
        Color Colour, Color Outline, Color Shadow, int OutlineSize, int ShadowX, int ShadowY, int ShadowOutline);

    // Where the time was last laid, the footer's room then, and why it was left out, if it was.
    readonly record struct Spot(Rect2 At, Rect2 Room, string? Kind, string? Why);

    readonly Tempo _looking = new(CombatGoalsMod.SampleEveryMs);
    readonly GameStyle _style = new();
    readonly RateHistory _progress = new();
    readonly HashSet<string> _told = new();
    readonly List<Container> _watched = new();
    Action? _sorted;
    TrackerComponentFooterView? _footer;
    GameFooter? _bar;
    ActionUntilXStopView? _panel;
    PanelContainer? _tag;
    HBoxContainer? _row, _phrase;
    TextureRect? _clock;
    Label? _time, _words;
    Look? _look;
    Spot? _placed;
    object? _stop, _action, _recipe;
    string _stopText = "";
    double _remaining = double.NaN, _estimate = double.NaN;
    ulong _lastLook, _estimatedAt;
    // In the footer's content, from where it starts: where what it draws ends, where its row of parts ends, the
    // gap after a part's bar, and the time's left edge.
    float _widest, _x = float.NaN, _contentEnd = float.NaN, _rowEnd = float.NaN, _gap;
    // Since when the content has ended further left than the time's place asks, and the furthest right the time
    // would have been placed in that while.
    ulong? _leftSince;
    float _leftMost;
    // _wanted: there is a time to show. _shown: it has a place. _dirty: something changed since it was laid out.
    bool _disabled, _wanted, _shown, _nameShown, _dirty, _followQueued;

    public static void Start(ModContext ctx)
    {
        var part = new SkillingAction(ctx);
        ctx.Frame += part.OnFrame;
#if DEVTOOLS
        Current = part;
#endif
    }

    void OnFrame()
    {
        if (_disabled || !_looking.Due()) return;
        try { Update(); }
        catch (Exception e) { Disable(e); }
    }

    // One line, then nothing; combat goes on without this part.
    void Disable(Exception e)
    {
        _disabled = true;
        try
        {
            Unwatch();
            if (GodotObject.IsInstanceValid(_tag)) _tag!.QueueFree();
        }
        catch (Exception) { }
        ctx.Log("Skilling stop estimates disabled (the game was updated?): " + e);
    }

    // What the game no longer has, or what keeps the time out of the footer, is said once per kind, however often
    // it comes back (DropWindows.Tell in Item Sources does the same).
    void Tell(string kind, string message)
    {
        if (_told.Add(kind)) ctx.Log(message);
    }

    void Update()
    {
        ulong now = Time.GetTicksMsec();
        // Combat's rule: a longer gap between two looks means the game was paused or computed offline
        // progress, which did not arrive at a pace anyone could measure.
        bool gap = _lastLook != 0 && now - _lastLook > CombatGoalsMod.GapResetMs;
        _lastLook = now;
        var game = GameAccess.CurrentGame;
        if (game is not { IsSaveLoaded: true } || GameAccess.IsComputingOffline)
        {
            Forget();
            _footer = null;
            _bar = null;
            _panel = null;
            return;
        }

        if (!GodotObject.IsInstanceValid(_footer) || !_footer!.IsInsideTree()) FindFooter(game);
        TrackerComponent? tracker = _footer == null ? null : TrackerOf(_footer);
        // The time belongs to the action whose name the footer shows, the one whose tracker that is.
        ActionComponent? action = tracker == null ? null : game.ActionManager.ActiveActions.FirstOrDefault(
            a => a.Entity is ITrackedEntity t && ReferenceEquals(t.Tracker, tracker));
        var until = action?.Entity.Components.Values.OfType<ActionUntilXComponent>().FirstOrDefault();
        var goal = until?.StopGoal;
        var condition = goal == null ? until?.StopCondition : null;
        object? stop = (object?)goal ?? condition;
#if DEVTOOLS
        Reading = $"footer tracker={tracker?.Entity.Id} passive={tracker?.IsPassive} action={action?.Entity.Id}/{action?.CurrentDescription?.Id} " +
                  $"stop={stop?.GetType().Name ?? "none"}";
#endif
        if (stop == null || goal?.IsComplete == true || condition?.IsMet == true)
        {
            Forget();
            return;
        }

        // Another stop, another recipe or a gap: what was measured is about something else.
        if (gap || !ReferenceEquals(stop, _stop) || !ReferenceEquals(action, _action) || !ReferenceEquals(action!.CurrentDescription, _recipe))
            Forget();
        _stop = stop;
        _action = action;
        _recipe = action!.CurrentDescription;

        double remaining = goal != null ? goal.MaxProgress - goal.CurrentProgress : Remaining(condition!);
        if (double.IsNaN(remaining))
        {
            // A kind of stop this cannot measure (one a game update added): nothing rather than a guess.
            Tell("kind:" + stop.GetType().Name, $"The stop's time is not shown for stops of the kind {stop.GetType().Name}: how far they are cannot be read.");
            Hide();
            return;
        }
        double period = FooterPeriodSeconds(tracker!);
        if (!(period > 0))
        {
            Tell("period", "The stop's time is not shown: the footer's time period could not be read (the game was updated?).");
            Hide();
            return;
        }
        if (double.IsInfinity(remaining) || remaining < 0)
        {
            Hide();
            return;
        }
        _progress.Add(now, -remaining, period);
        double rate = _progress.PerSecond(now, period);
#if DEVTOOLS
        Reading += $" remaining={remaining:R} progress/s={rate:R} period={period:R}s";
#endif
        if (!(rate > 0))
        {
            _estimate = double.NaN;
            Hide();
            return;
        }
        // Progress comes one action at a time. The time is worked out again when it moves and counts down in
        // between, so it does not rise between two actions and drop at each one.
        if (double.IsNaN(_estimate) || remaining != _remaining)
        {
            _estimate = remaining / rate;
            _estimatedAt = now;
        }
        _remaining = remaining;
        double seconds = _estimate - (now - _estimatedAt) / 1000.0;
#if DEVTOOLS
        Reading += $" seconds={seconds:0.###}";
#endif
        // The estimate ran out before the stop was reached (the last item is on its way, or the pace slowed):
        // nothing until the next one lands, rather than "0s" or a guess.
        if (!(seconds > 0) || !double.IsFinite(seconds))
        {
            Hide();
            return;
        }
        Show(seconds, StopText(until!));
    }

    // The footer's tracker, found again whenever the game's screen was built anew. Not finding it is said once, and
    // only where it matters: the game's screen is set up and an action runs towards a stop.
    void FindFooter(GamesByMalcs.Melvor2.Game game)
    {
        var view = GameAccess.FindGameView();
        _bar = view?.Footer;
        _footer = _bar == null ? null : GameAccess.FindNode<TrackerComponentFooterView>(_bar);
        if (_footer != null || view == null || !view.IsNodeReady() || !StopRunning(game)) return;
        Tell("footer", _bar == null
            ? "The stop's time is not shown: the game's footer was not found (the game was updated?)."
            : "The stop's time is not shown: the footer's tracker was not found (the game was updated?).");
    }

    static bool StopRunning(GamesByMalcs.Melvor2.Game game) => game.ActionManager.ActiveActions.Any(a =>
        a.Entity.Components.Values.OfType<ActionUntilXComponent>().Any(u => u.StopGoal != null || u.StopCondition != null));

    // How far the stop still is, in the units its progress is counted in (XP for a level, so uneven level
    // steps do not bend the pace).
    static double Remaining(Condition condition) => condition switch
    {
        ItemOwnedCondition c => c.Quantity - (double)c.Component.Inventory.GetQuantity(c.Item),
        MaxItemOwnedCondition c => c.Component.Inventory.GetQuantity(c.Item) - (double)c.Quantity,
        CurrencyOwnedCondition c => c.Quantity - (double)c.Component.Wallet.GetQuantity(c.Currency),
        MaxCurrencyOwnedCondition c => c.Component.Wallet.GetQuantity(c.Currency) - (double)c.Quantity,
        LevelCondition c => c.Component.Table.GetXpForLevel(c.Level) - c.Component.Xp,
        _ => double.NaN,
    };

    // The footer's own time period (its drop-down), only read: the pace is averaged over the same stretch of
    // play as the footer's rates. A development request writes it next to the drop-down's text.
    internal static double FooterPeriodSeconds(TrackerComponent tracker)
    {
        var periods = tracker.Periods;
        int index = tracker.SelectedPeriodIndex;
        return index >= 0 && index < periods.Count ? periods[index].CycleTicks * TickTimer.TicksToMS(1) / 1000 : 0;
    }

    // The stop's text as the game's stop panel shows it. Looking for the panel walks the pages under the
    // game's screen, so only while it is not known (and this runs once a second). Until it is found the
    // tooltip has no stop text: nothing of the mod's own stands in for the game's words.
    string StopText(ActionUntilXComponent until)
    {
        if (!GodotObject.IsInstanceValid(_panel) || !ReferenceEquals(UntilXOf(_panel!), until))
            _panel = GameAccess.FindGameView()?.EntityViewContainer is { } pages ? FindPanel(pages, until) : null;
        if (_panel != null && StopTextOf(_panel) is { } text) _stopText = Plain(text.GetParsedText());
        return _stopText;
    }

    // The panel draws the stop's item picture inside its sentence, and its plain text keeps blanks where the
    // picture is ("Create 11 / 60   Chaos Rune"): one space each, as the sentence reads without the picture.
    internal static string Plain(string parsed) => Regex.Replace(parsed.Replace('\uFFFC', ' '), @"\s+", " ").Trim();

    static ActionUntilXStopView? FindPanel(Node node, ActionUntilXComponent until)
    {
        if (node is ActionUntilXStopView panel && ReferenceEquals(UntilXOf(panel), until)) return panel;
        for (int i = 0, n = node.GetChildCount(); i < n; i++)
            if (FindPanel(node.GetChild(i), until) is { } found) return found;
        return null;
    }

    // The footer's formatter writes its whole phrase with markup, "[b]1m 38s[/b] left to". The time is the bold
    // part; the footer draws it bold, and so does this (its font is the footer's, see ReadLook).
    internal static string? TimeOnly(string formatted)
    {
        int open = formatted.IndexOf("[b]", StringComparison.Ordinal);
        int close = open < 0 ? -1 : formatted.IndexOf("[/b]", open + 3, StringComparison.Ordinal);
        if (close < 0) return null;
        string time = Regex.Replace(formatted[(open + 3)..close], @"\[[^\]]*\]", "").Trim();
        return time == "" ? null : time;
    }

    void Forget()
    {
        _progress.Clear();
        _stop = _action = _recipe = null;
        _stopText = "";
        _remaining = _estimate = double.NaN;
        _widest = 0;
#if DEVTOOLS
        if (!float.IsNaN(_x)) NotePlace(_x, float.NaN, "forgotten: the stop or the recipe changed, ended, or the game was away");
#endif
        _x = _contentEnd = _rowEnd = float.NaN;
        _leftSince = null;
        _placed = null;
        if (GodotObject.IsInstanceValid(_row) && _row!.CustomMinimumSize != Vector2.Zero) _row.CustomMinimumSize = Vector2.Zero;
        Hide();
    }

    void Hide()
    {
        _wanted = false;
        _shown = false;
        if (GodotObject.IsInstanceValid(_tag) && _tag!.Visible) _tag.Visible = false;
    }

    void Show(double seconds, string stopText)
    {
        Label? name = ActionNameOf(_footer!);
        if (name == null || !GodotObject.IsInstanceValid(name))
        {
            Tell("name", "The stop's time is not shown: the footer's action name was not found (the game was updated?).");
            Hide();
            return;
        }
        // The game's formatter gets the time left as it is: it writes "less than 1s" in the last second and ">999d"
        // past the longest time the footer writes, and both are shown as it writes them.
        string formatted = FormatTimeToLevel(null, seconds * FormatterUnitsPerSecond);
#if DEVTOOLS
        Formatted = formatted;
#endif
        if (TimeOnly(formatted) is not { } time)
        {
            // Only a time above zero gets here, and the footer writes every such time in bold: this is a game
            // that changed, not a moment of play.
            Tell("format", "The footer's time was not understood, so the stop's time is not shown: " + formatted);
            Hide();
            return;
        }
        if (!GodotObject.IsInstanceValid(_tag) || _tag!.GetParent() != name) Build(name);
        if (ApplyLook(ReadLook(name))) _dirty = true;
        if (_time!.Text != time) _time.Text = time;
        // The time grows shorter and longer as it counts down: the part keeps its widest width for this stop, the
        // room it needs at the end of the footer does not come and go, and the words stay right after the time.
        float need = _row!.GetMinimumSize().X;
        if (need > _widest)
        {
            _widest = need;
            _row.CustomMinimumSize = new Vector2(_widest, 0);
            _dirty = true;
        }
        string tooltip = (stopText == "" ? "" : stopText + "\n") + $"{time} until the stop ends the action, at the recent pace.";
        if (_tag!.TooltipText != tooltip) _tag.TooltipText = tooltip;
        if (!_wanted || name.IsVisibleInTree() != _nameShown) _dirty = true;
        _wanted = true;
        // Laid out again only when something changed: the footer laid itself out (OnFooterSorted), the time grew,
        // its look changed, or the footer's name came or went. While the footer's content ends further left than
        // the time's place, the time looks again each second whether it is time to move left after it.
        if (_dirty) Lay(full: true);
        else if (_leftSince != null) Lay(full: false);
        else ApplyVisible();
    }

    // The part: a clock, the time in bold and the words, framed like the footer's part that names the action. It
    // is laid over the footer as a child of the action's name, as Kills in Combat lays its tag over the enemy's
    // name (KillsInCombatMod.EnsureTag), so no row of the game's makes room for it and nothing of the game's moves.
    // Being the name's child, it is only shown while the footer shows that name.
    void Build(Label name)
    {
        if (GodotObject.IsInstanceValid(_tag)) _tag!.QueueFree();
        _look = null;
        _placed = null;
        _widest = 0;
#if DEVTOOLS
        if (!float.IsNaN(_x)) NotePlace(_x, float.NaN, "built again on the footer's action name");
#endif
        _x = _contentEnd = _rowEnd = float.NaN;
        _leftSince = null;
        _shown = false;
        // The game's own clock, the one it draws beside a duration ("2.00s" on a recipe).
        Texture2D? picture = GameStyle.Icon("TimeInterval_48");
        if (picture == null) Tell("look:clock", "The game's clock picture was not found; the stop's time is shown without it.");
        var tag = new PanelContainer { Name = TagName, MouseFilter = Control.MouseFilterEnum.Pass, Visible = false, Theme = _style.TooltipTheme() };
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        TextureRect clock = _style.Picture(picture, 16);
        clock.Visible = picture != null;
        var phrase = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var time = new Label { VerticalAlignment = VerticalAlignment.Center };
        var words = new Label { Text = Words, VerticalAlignment = VerticalAlignment.Center };
        phrase.AddChild(time);
        phrase.AddChild(words);
        row.AddChild(clock);
        row.AddChild(phrase);
        tag.AddChild(row);
        name.AddChild(tag);
        (_tag, _row, _phrase, _clock, _time, _words) = (tag, row, phrase, clock, time, words);
        Watch(name);
    }

    // The footer's own look, read from its nodes on every look, so it follows a change of the game's interface or
    // theme: the frame of the part that names the action, the size of the action's icon and the gap after it, and
    // the fonts of the footer's "42m 39s left to", bold for the time and plain for the words. A piece that is gone
    // after a game update keeps the house's look (GameStyle) for that piece and says so once.
    Look ReadLook(Label name)
    {
        PanelContainer? section = Piece("frame around the action's name", () => SectionOf(name, ScrollOf(_footer!)));
        BoxContainer? nameRow = Piece("row of the action's name", () => name.GetParent() as BoxContainer);
        TextureRect? actionIcon = Piece("action icon", () => ActionIconOf(_footer!));
        RichTextLabel? words = Piece("time to a level", () => ExperienceOf(_footer!) is { } xp ? TimeToLevelOf(xp) : null);

        StyleBox? frame = section?.GetThemeStylebox("panel");
        // Else GameStyle.CountTag's gap between its icon and its number.
        int gap = nameRow?.GetThemeConstant("separation") ?? 4;
        // The footer draws the action's icon 16 wide at the game's own size (CurrentActionIcon in check 3's layout
        // line). Before the icon is laid out, the size read last is kept.
        float icon = _look?.Icon ?? 16;
        if (actionIcon != null && actionIcon.Size.X > 0 && actionIcon.Size.Y > 0) icon = Math.Min(actionIcon.Size.X, actionIcon.Size.Y);
        if (words == null)
            return new Look(frame, icon, gap, GameStyle.Bold, 14, null, 14, GameStyle.TextWhite, default, default, 0, 0, 0, 0);
        return new Look(frame, icon, gap,
            words.GetThemeFont("bold_font"), words.GetThemeFontSize("bold_font_size"),
            words.GetThemeFont("normal_font"), words.GetThemeFontSize("normal_font_size"),
            words.GetThemeColor("default_color"), words.GetThemeColor("font_outline_color"), words.GetThemeColor("font_shadow_color"),
            words.GetThemeConstant("outline_size"), words.GetThemeConstant("shadow_offset_x"), words.GetThemeConstant("shadow_offset_y"),
            words.GetThemeConstant("shadow_outline_size"));
    }

    // A node of the footer the look is read from; null, said once, when it is gone after a game update.
    T? Piece<T>(string what, Func<T?> read) where T : class
    {
        T? piece = null;
        try { piece = read(); }
        catch (MissingMemberException) { }
        if (piece == null) Tell("look:" + what, $"The footer's {what} was not found (the game was updated?); the stop's time uses the mods' own look for it.");
        return piece;
    }

    // Only what changed is set again. Returns whether anything did: the part may need another size and place.
    bool ApplyLook(Look look)
    {
        if (_look is { } old && Same(old, look)) return false;
        if (_look is not { } before || Id(before.Frame) != Id(look.Frame))
            _tag!.AddThemeStyleboxOverride("panel", look.Frame?.Duplicate() as StyleBox ?? new StyleBoxEmpty());
        _row!.AddThemeConstantOverride("separation", look.Gap);
        _clock!.CustomMinimumSize = new Vector2(look.Icon, look.Icon);
        Dress(_time!, look.TimeFont, look.TimeSize, look);
        Dress(_words!, look.WordsFont, look.WordsSize, look);
        // Between the bold time and the words, one space of the words' font, as in the footer's own line.
        Font font = _words!.GetThemeFont("font");
        _phrase!.AddThemeConstantOverride("separation", (int)MathF.Round(font.GetStringSize(" ", HorizontalAlignment.Left, -1, look.WordsSize).X));
        _look = look;
        _widest = 0;
        _row.CustomMinimumSize = Vector2.Zero;
#if DEVTOOLS
        LookReading = $"frame {(look.Frame == null ? "none" : Describe(look.Frame))}, icon {look.Icon}px, gap {look.Gap}, space {_phrase.GetThemeConstant("separation")}, " +
                      $"time font {look.TimeFont?.ResourcePath} {look.TimeSize}px, words font {look.WordsFont?.ResourcePath} {look.WordsSize}px, " +
                      $"colour #{look.Colour.ToHtml()}, outline {look.OutlineSize} #{look.Outline.ToHtml()}, shadow {look.ShadowX},{look.ShadowY} #{look.Shadow.ToHtml()}";
#endif
        return true;
    }

    // The game's resources are compared as the objects they are in the game, whatever wraps them here.
    static bool Same(Look a, Look b) =>
        Id(a.Frame) == Id(b.Frame) && Id(a.TimeFont) == Id(b.TimeFont) && Id(a.WordsFont) == Id(b.WordsFont) &&
        a with { Frame = null, TimeFont = null, WordsFont = null } == b with { Frame = null, TimeFont = null, WordsFont = null };

    static ulong Id(GodotObject? resource) => resource?.GetInstanceId() ?? 0;

    static void Dress(Label label, Font? font, int size, Look look)
    {
        if (font != null) label.AddThemeFontOverride("font", font);
        else label.RemoveThemeFontOverride("font");
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", look.Colour);
        label.AddThemeColorOverride("font_outline_color", look.Outline);
        label.AddThemeColorOverride("font_shadow_color", look.Shadow);
        label.AddThemeConstantOverride("outline_size", look.OutlineSize);
        label.AddThemeConstantOverride("shadow_offset_x", look.ShadowX);
        label.AddThemeConstantOverride("shadow_offset_y", look.ShadowY);
        label.AddThemeConstantOverride("shadow_outline_size", look.ShadowOutline);
    }

    // The part of the footer that holds the action's name: the first framed box around it.
    static PanelContainer? SectionOf(Label name, Node? scroll)
    {
        for (Node? up = name.GetParent(); up != null && up != scroll; up = up.GetParent())
            if (up is PanelContainer section) return section;
        return null;
    }

    // The footer's containers lay out their parts again whenever one of them changes width, appears or goes, and
    // when the game window is resized: the ones that hold the action's name, up to the part of the footer that
    // scrolls, tell this through their sort_children signal.
    void Watch(Label name)
    {
        Unwatch();
        ScrollContainer? scroll = ScrollOf(_footer!);
        if (scroll == null || !scroll.IsAncestorOf(name))
        {
            Tell("follow", "The part of the footer that scrolls does not hold the action's name; the stop's time follows the footer once a second only.");
            return;
        }
        _sorted ??= OnFooterSorted;
        for (Node? up = name.GetParent(); up != null; up = up.GetParent())
        {
            if (up is Container box)
            {
                box.SortChildren += _sorted;
                _watched.Add(box);
            }
            if (up == scroll) break;
        }
    }

    void Unwatch()
    {
        foreach (Container box in _watched)
            if (GodotObject.IsInstanceValid(box)) box.SortChildren -= _sorted;
        _watched.Clear();
    }

    // The time follows the footer's new layout in the same frame, before anything is drawn: the call is put at the
    // end of the layout work under way (Callable.CallDeferred, as MakeBox.FitAfterLayout in Item Sources does), so
    // the parts are where they will be drawn. Several layouts in one frame make one call.
    void OnFooterSorted()
    {
        if (_disabled || !_wanted || GameAccess.IsComputingOffline) return;
        _dirty = true;   // the next look goes through the whole footer again
        if (_followQueued) return;
        _followQueued = true;
        try { Callable.From(Follow).CallDeferred(); }
        catch (Exception e) { Disable(e); }
    }

    void Follow()
    {
        _followQueued = false;
        if (_disabled || !_wanted || GameAccess.IsComputingOffline) return;
#if DEVTOOLS
        Follows++;
#endif
        try { Lay(full: false); }
        catch (Exception e) { Disable(e); }
    }

    void Lay(bool full)
    {
        if (full) _dirty = false;
#if DEVTOOLS
        if (full) FullLays++;
#endif
        if (!GodotObject.IsInstanceValid(_tag) || _tag!.GetParent() is not Label name || !GodotObject.IsInstanceValid(_footer)) return;
        bool before = _shown;
        _shown = Place(name, full);
        ApplyVisible();
        if (_shown && !before) Tell("shown", "The time until the stop is shown at the end of the footer.");
    }

    void ApplyVisible()
    {
        bool visible = _wanted && _shown;
#if DEVTOOLS
        visible &= !HideForPicture;
#endif
        if (GodotObject.IsInstanceValid(_tag) && _tag!.Visible != visible) _tag.Visible = visible;
    }

    // Past the last thing the footer's tracker draws, by the gap the footer leaves after a part's bar, at the height
    // of the action's part: Item Sources places its tag past a row the same way (DropWindows.PastRow). Left out where
    // the part of the footer that scrolls has no room left for it (a narrow game window), where a part would cut it
    // off, or where it would cover anything of the game's (DropWindows.Covers).
    //
    // It follows the footer's content both ways, as the footer's own parts do. Right at once, so it never lies over
    // a part that grew. Left only once the content has ended further left for HoldLeftMs, and then to the furthest
    // right it ended in that while: the footer's numbers change width by a pixel or two every second, and the time
    // does not step back and forth with each of them. One wrong reading can so never leave a hole for longer.
    //
    // full: everything the tracker draws is looked through (on the one-second look). Otherwise, as the footer lays
    // itself out between two looks, only its row of parts is looked at: when that row now ends elsewhere, the part
    // that grew or shrank moved what comes after it by as much, and so the content's end moved too.
    bool Place(Label name, bool full)
    {
        _nameShown = name.IsVisibleInTree();
        if (!_nameShown) return false;   // the footer is folded or hides its tracker: the time goes with the name
        ScrollContainer? scroll = ScrollOf(_footer!);
        if (scroll == null || !GodotObject.IsInstanceValid(_bar))
        {
            Tell("scroll", "The stop's time is not shown: the part of the footer that scrolls was not found (the game was updated?).");
            return false;
        }
        PanelContainer? section = SectionOf(name, scroll);
        // The box that holds the action's coloured bar and its part (ActionContainer in check 3's layout line).
        var barRow = section?.GetParent() as BoxContainer;
        // The row of the footer's parts (the action, the XP, the bank, ...): what the scrolling part holds.
        Control? row = RowOf(name, scroll);
        // Kept from where the footer's content starts, so scrolling the footer does not count as moving.
        float origin = scroll.GlobalPosition.X - scroll.ScrollHorizontal;
        float rowEnd = row == null ? float.NaN : RowEnd(row) - origin;
        if (full || float.IsNaN(_contentEnd))
        {
            float end = float.NegativeInfinity;
            Control? last = null;
            EndOfDrawn(scroll, name, name.GetGlobalRect(), ref end, ref last);
            if (!float.IsFinite(end))
            {
                Tell("end", "The stop's time is not shown: nothing the footer's tracker draws was found (the game was updated?).");
                return false;
            }
            if (row == null) Tell("parts", "The row of the footer's parts was not found; the stop's time follows the footer once a second only.");
            _contentEnd = end - origin;
            _rowEnd = rowEnd;
            // The gap the footer leaves between a part's coloured bar and what the part shows, the separation of the
            // box that holds the action's bar and its part: the time follows the last part as a part's content
            // follows its bar. Else the gap GameStyle.PlaceAfterText leaves after a text.
            _gap = barRow?.GetThemeConstant("separation") ?? 12;
#if DEVTOOLS
            _contentBy = last == null ? "nothing" : DescribePart(last, scroll);
            _cause = $"a look: the content ends at {end:0}, the right edge of {_contentBy}";
#endif
        }
        else if (!float.IsNaN(rowEnd) && !float.IsNaN(_rowEnd) && rowEnd != _rowEnd)
        {
#if DEVTOOLS
            _cause = $"a layout: the row of parts '{row!.Name}' ends at {origin + rowEnd:0}, it ended at {origin + _rowEnd:0}";
#endif
            _contentEnd += rowEnd - _rowEnd;
            _rowEnd = rowEnd;
        }

        Rect2 band = section?.GetGlobalRect() ?? name.GetGlobalRect();
        Vector2 size = _tag!.GetCombinedMinimumSize();
        size.Y = Math.Max(size.Y, band.Size.Y);
        float y = MathF.Round(band.Position.Y + (band.Size.Y - size.Y) / 2);
        float want = MathF.Round(_contentEnd + _gap);
        Rect2 room = scroll.GetGlobalRect();
#if DEVTOOLS
        float was = _x;
        string why = _cause;
#endif
        if (float.IsNaN(_x) || want >= _x || !room.Encloses(new Rect2(new Vector2(origin + _x, y), size)))
        {
#if DEVTOOLS
            if (!float.IsNaN(_x) && want < _x) why = "the place it had has no room left in the footer; " + _cause;
#endif
            _x = want;
            _leftSince = null;
        }
        else
        {
            ulong now = Time.GetTicksMsec();
            if (_leftSince is not { } since)
            {
                _leftSince = now;
                _leftMost = want;
            }
            else
            {
                _leftMost = Math.Max(_leftMost, want);
                if (now - since >= HoldLeftMs)
                {
#if DEVTOOLS
                    why = $"the content ended further left for {now - since} ms, so to the furthest right it asked for in that while (last {_cause})";
#endif
                    _x = _leftMost;
                    _leftSince = null;
                }
            }
        }
#if DEVTOOLS
        _origin = origin;
        _want = want;
        if (_x != was) NotePlace(was, _x, why);
#endif
        var at = new Rect2(new Vector2(origin + _x, y), size);
        bool moved = _placed is not { } then || then.At != at || then.Room != room;
        // Nothing of the footer is looked through again when nothing moved and the footer did not lay itself out.
        if (full || moved)
        {
            (string Kind, string Why)? leftOut = !room.Encloses(at) ? ("room", "where the footer has no room left at its end (a narrow game window)")
                : CutBy(name, scroll, at) is { } edge ? ("cut", $"where {edge} would cut it off")
                : Covers(at) is { } what ? ("cover", "where it would cover " + what) : null;
            _placed = new Spot(at, room, leftOut?.Kind, leftOut?.Why);
        }
        Spot spot = _placed!.Value;
#if DEVTOOLS
        PlaceReading = $"{(full ? "looked through" : "followed the row")}: {PlaceNow}; " +
                       $"row of parts {(row == null ? "not found" : $"'{row.Name}' ends at {origin + rowEnd:0}")}, gap {_gap} " +
                       $"({(barRow == null ? "none found, 12" : "the separation of " + barRow.Name)}), " +
                       $"content starts at {origin:0} (scrolled {scroll.ScrollHorizontal}), at {at}, room {room} ({scroll.Name}), band {band} ({section?.Name}): " +
                       $"{(spot.Why == null ? "shown" : "left out " + spot.Why)}; layouts followed {Follows}, looks {FullLays}, place changes {PlaceChanges}, " +
                       $"containers watched {_watched.Count}";
#endif
        if (spot.Kind != null)
        {
            Tell("left out:" + spot.Kind, $"The stop's time is left out of the footer {spot.Why}.");
            return false;
        }
        if (_tag.Size != size) _tag.Size = size;
        if (_tag.GlobalPosition != at.Position) _tag.GlobalPosition = at.Position;
        return true;
    }

    // The row of the footer's parts: the node the part of the footer that scrolls holds, on the way up from the
    // action's name (MainHBoxContainer in check 3's layout line).
    static Control? RowOf(Label name, ScrollContainer scroll)
    {
        for (Node? up = name; up != null; up = up.GetParent())
            if (up.GetParent() == scroll) return up as Control;
        return null;
    }

    // Where the footer's row of parts ends: the right edge of the last part it shows. A part set to take the room
    // that is left ends where the row ends, whatever it holds, so it is not counted: the time would drift right
    // with the width of the game window.
    static float RowEnd(Control parts)
    {
        float end = parts.GlobalPosition.X;
        foreach (Node child in parts.GetChildren())
            if (child is Control { Visible: true } part && !part.IsQueuedForDeletion() && !part.SizeFlagsHorizontal.HasFlag(Control.SizeFlags.Expand))
                end = Math.Max(end, part.GetGlobalRect().End.X);
        return end;
    }

    // Never the footer's content: the time itself, and an earlier one or a part of the game's that is freed at the
    // end of this frame (DropWindows.Under in Item Sources skips those too).
    bool NotFooter(Control control) => control == _tag || control.Name == TagNodeName || control.IsQueuedForDeletion();

    // A part between the time and the footer's scrolling part that cuts off what it holds where the time would be.
    static string? CutBy(Label name, ScrollContainer scroll, Rect2 at)
    {
        for (Node? up = name; up != null && up != scroll; up = up.GetParent())
            if (up is Control { ClipContents: true } clip && !clip.GetGlobalRect().Encloses(at)) return $"{clip.GetType().Name} '{clip.Name}'";
        return null;
    }

    // The right edge of everything drawn in the footer's tracker, but the frames and backgrounds behind the
    // action's whole name. What is hidden (the time too, while a development picture hides it) is not looked into.
    void EndOfDrawn(Node node, Label name, Rect2 nameAt, ref float end, ref Control? last)
    {
        for (int i = 0, n = node.GetChildCount(); i < n; i++)
        {
            if (node.GetChild(i) is not Control { Visible: true } control || NotFooter(control)) continue;
            Rect2 rect = control.GetGlobalRect();
            bool behindName = control != name && rect.Encloses(nameAt);
            if (Drawn(control) && rect.Area > 0 && !behindName && rect.End.X > end)
            {
                end = rect.End.X;
                last = control;
            }
            EndOfDrawn(control, name, nameAt, ref end, ref last);
        }
    }

    // What of the game's the time would lie on, anywhere in the footer: something drawn that does not lie behind
    // the whole of it as a background. As DropWindows.Covers, a container is only looked into where it touches the
    // time's rectangle: what it lays out stays inside it.
    string? Covers(Rect2 at)
    {
        return Find(_bar!);

        string? Find(Node node)
        {
            for (int i = 0, n = node.GetChildCount(); i < n; i++)
            {
                if (node.GetChild(i) is not Control { Visible: true } control || NotFooter(control)) continue;
                Rect2 rect = control.GetGlobalRect();
                bool touches = rect.Intersects(at);
                if (Drawn(control) && touches && !rect.Encloses(at)) return $"{control.GetType().Name} '{control.Name}'";
                if ((touches || control is not Container) && Find(control) is { } inside) return inside;
            }
            return null;
        }
    }

    // A part that draws something: words, a picture, a button, a bar, a line or a frame.
    static bool Drawn(Control control) => control switch
    {
        Label words => words.Text.Trim() != "",
        RichTextLabel words => words.Text.Trim() != "",
        TextureRect picture => picture.Texture != null,
        BaseButton or Godot.Range or LineEdit or Separator or ColorRect or NinePatchRect or Godot.Panel => true,
        PanelContainer framed => framed.GetThemeStylebox("panel") is { } box && box is not StyleBoxEmpty,
        _ => false,
    };

#if DEVTOOLS
    // What set the content's end at the last look, and what last moved it (a look or a layout).
    string _contentBy = "nothing yet", _cause = "nothing yet";
    float _origin = float.NaN, _want = float.NaN;
    readonly List<string> _changes = new();
    int _changesDropped;

    // Every change of the time's place, with what changed it, until a development line takes them.
    void NotePlace(float from, float to, string why)
    {
        PlaceChanges++;
        if (_changes.Count >= 12)
        {
            _changes.RemoveAt(0);
            _changesDropped++;
        }
        string Left(float x) => float.IsNaN(x) ? "none" : $"{_origin + x:0}";
        _changes.Add($"{Left(from)} -> {Left(to)} by {why}");
    }

    /// <summary>Development: the changes of the time's place since the last line that took them.</summary>
    internal string TakePlaceChanges()
    {
        string taken = _changes.Count == 0 ? "none"
            : (_changesDropped > 0 ? $"{_changesDropped} earlier ones left out; " : "") + string.Join("; ", _changes);
        _changes.Clear();
        _changesDropped = 0;
        return taken;
    }

    /// <summary>Development: where the time is, where the content ends and by what, and whether it waits to move left.</summary>
    internal string PlaceNow => float.IsNaN(_x) ? "no place" :
        $"placed at {_origin + _x:0}, asked for {_origin + _want:0}, content ends at {_origin + _contentEnd:0} (at the last look, the right edge of {_contentBy})" +
        (_leftSince is { } since ? $", further left for {Time.GetTicksMsec() - since} ms of {HoldLeftMs} (furthest right asked for {_origin + _leftMost:0})" : "");

    // A part of the footer, where it is and how wide, and where it sits under the part that scrolls.
    static string DescribePart(Control part, Node scroll)
    {
        Rect2 rect = part.GetGlobalRect();
        return $"{part.GetType().Name} '{part.Name}' {rect.Position.X:0}+{rect.Size.X:0} ({scroll.GetPathTo(part)})" +
               (part.Modulate.A < 1 || part.SelfModulate.A < 1 ? $" alpha {part.Modulate.A:0.##}/{part.SelfModulate.A:0.##}" : "");
    }

    internal static SkillingAction? Current { get; private set; }
    /// <summary>Development: hides the time for one picture, to see whether the footer moves without it.</summary>
    internal static bool HideForPicture { get; set; }
    internal string Reading { get; private set; } = "not looked yet";
    internal string PlaceReading { get; private set; } = "not placed yet";
    internal string LookReading { get; private set; } = "not built yet";
    internal string Formatted { get; private set; } = "";
    internal int FullLays { get; private set; }
    internal int Follows { get; private set; }
    internal int PlaceChanges { get; private set; }
    internal PanelContainer? Tag => GodotObject.IsInstanceValid(_tag) ? _tag : null;
    internal Label? TimeLabel => GodotObject.IsInstanceValid(_time) ? _time : null;
    internal Label? WordsLabel => GodotObject.IsInstanceValid(_words) ? _words : null;
    internal ActionUntilXStopView? Panel => GodotObject.IsInstanceValid(_panel) ? _panel : null;
    internal TrackerComponentFooterView? Footer => GodotObject.IsInstanceValid(_footer) ? _footer : null;
    internal GameFooter? Bar => GodotObject.IsInstanceValid(_bar) ? _bar : null;
    internal object? Stop => _stop;

    static string Describe(StyleBox box) => box switch
    {
        StyleBoxFlat f => $"StyleBoxFlat bg #{f.BgColor.ToHtml()} border #{f.BorderColor.ToHtml()} " +
                          $"{f.BorderWidthLeft},{f.BorderWidthTop},{f.BorderWidthRight},{f.BorderWidthBottom} " +
                          $"margins {f.ContentMarginLeft},{f.ContentMarginTop},{f.ContentMarginRight},{f.ContentMarginBottom}",
        StyleBoxTexture t => $"StyleBoxTexture {t.Texture?.ResourcePath}",
        _ => box.GetType().Name,
    };
#endif
}
