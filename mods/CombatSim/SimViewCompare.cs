using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MelvorMods.Shared;

namespace CombatSim;

/// <summary>
/// Compare mode (issue #2) in the simulator's window, the state being SimState's (SimCompare.cs).
/// <list type="bullet">
/// <item>Left: the game's tick box "Compare" at the end of the Equipment heading (GameStyle.TickBox, as History has
/// one) and, while it is ticked, the buttons A and B in front of the loadout, in the look of the mode buttons
/// (<see cref="Toggle"/>). The one that is down is the setup the loadout, the slots and the spell show and change.</item>
/// <item>Middle: every column of the list as three cells, in the list's own header buttons and rows: first which of the
/// two setups is better and by how much ("A by 23%"), under the column's own name, then its number with A and with B.
/// The better side is green and the worse red by the simulator's one rule (<see cref="Judge"/>: the ranges of issue #5,
/// a real 0 included; for A and B, which meet the same dice, the bar lowered by the luck they met alike, issue #57; a
/// number without a range only where the deaths, kills or runs, or the game's own numbers, decide it, issue #67), grey
/// where it could be luck. The three cells of a column move sideways and come into sight
/// together. The lines have the room of Fights and name no stance (SimViewColumns.EnemyPart). Above the list, how many
/// fights each setup wins in the column the list is sorted by, and in Best for where each setup does best.</item>
/// <item>Right: the selected fight with both setups, a Details line per number (the look of <see cref="Stat"/>), and
/// Fight, which is for the setup on the left.</item>
/// </list>
/// </summary>
sealed partial class SimView
{
    // The cells of a column in compare mode, by the column's key and one of these: which of the two is better, its
    // number with A, with B.
    const string Versus = "@vs", WithA = "@a", WithB = "@b";
    const int PairCellCount = 3;
    // What tells B from A, in the words of the tab on show: Best for has no Stance box, and tries every stance (issue #51).
    string WhatChanges => _state.BestMode ? "its loadout, an item or its spell" : "its loadout, an item, its stance or its spell";
    // What the same dice do (issue #57), in no more words than the verdicts beside them bear out: luck both meet alike
    // cannot put one ahead, so the bar for green and red is lowered by it, the more the more alike the two fight.
    string CompareTip => "Compare two setups, A and B. Simulate runs every fight with both, from the same moment of your\n" +
                         "character and with the same dice: the more alike the two fight, the smaller the difference\n" +
                         "that can be told from luck. The list shows each fight with which one is better, A's number and B's.\n" +
                         $"The first time, B starts as a copy of A: press B, then change {WhatChanges}.";
    string SideTip => (_state.BestMode ? "The loadout, the slots and the spell below" : "The loadout, the slots, the stance and the spell below") +
                      " show and change the setup whose button is down.\nFight is for that setup too.";

    Button _compareBox = null!;
    Button? _sideA, _sideB;
    // The kind of list the header's cells were made for, and in Best for the fight each setup does best in.
    bool _headerComparing;
    string? _bestA, _bestB;

    // ---- left: the Compare box and the A and B buttons -----------------------------------------------------

    // "──── Equipment ────  ☐ Compare": the section's heading with the game's tick box at its end, where it takes no
    // room from the column's other lines. The box hangs on the heading's line (Hang): the heading keeps the height of its
    // words at every size (the box asked one pixel more at size 0.6, check-1).
    Control EquipmentHeading()
    {
        Control heading = _s.Section("Equipment");
        Button box = _compareBox = _s.TickBox("Compare", _state.Compare);
        // Margins at the sides only, read from the box's own style.
        foreach (string look in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" })
            if (box.GetThemeStylebox(look) is { } style)
                box.AddThemeStyleboxOverride(look, new StyleBoxEmpty { ContentMarginLeft = style.GetMargin(Side.Left), ContentMarginRight = style.GetMargin(Side.Right) });
        // Off while a scan runs, as the other choices are: its words then in the game's dim grey, as GameStyle.LinkTag
        // greys its own, and its picture in the tint the game's theme gives a button that cannot be used.
        box.AddThemeColorOverride("font_disabled_color", GameStyle.TextDim);
        box.TooltipText = CompareTip;
        box.Toggled += SetCompare;
        heading.AddChild(Hang(box));
        return heading;
    }

    // A control hung in a holder as wide as it that asks for no height of its own: the row it stands in keeps the height
    // the rest of the row gives it. The control fills that height, as in the row itself; where it asks for more (a pixel
    // or two), it reaches over the row's edges by half of it on each side, into the spacing between the rows. Compare
    // mode makes the Setup column no higher at any size (CombatSimMod.FullHeight).
    static Control Hang(Control control)
    {
        var holder = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        (control.AnchorLeft, control.AnchorRight, control.AnchorTop, control.AnchorBottom) = (0, 1, 0, 1);
        (control.OffsetLeft, control.OffsetRight, control.OffsetTop, control.OffsetBottom) = (0, 0, 0, 0);
        control.GrowVertical = Control.GrowDirection.Both;   // taller than the row: centred on it
        holder.AddChild(control);
        void Fit()
        {
            if (GodotObject.IsInstanceValid(holder) && GodotObject.IsInstanceValid(control))
                holder.CustomMinimumSize = new Vector2(control.GetCombinedMinimumSize().X, 0);
        }
        control.MinimumSizeChanged += Fit;
        Fit();
        return holder;
    }

    // In compare mode the loadout's drop-down comes after the A and B buttons, spaced like the rows of two buttons. The
    // buttons hang beside it (Hang): the row keeps the drop-down's height (they asked 1 px more at 0.75, 2 at 0.6).
    Control LoadoutRow(OptionButton loadout)
    {
        if (!_state.Compare) return loadout;
        var row = _s.Spaced(new HBoxContainer(), 4);
        var group = new ButtonGroup();
        Button a = _sideA = SideButton("A", group, false), b = _sideB = SideButton("B", group, true);
        row.AddChild(Hang(a));
        row.AddChild(Hang(b));
        loadout.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(loadout);
        // Godot's own fit to the longest choice asks about 30 px more than the words (SimViewRanges.FitChoices); beside A
        // and B that made the Setup column wider at size 0.75 than with one setup (176 px for 168, check-1). So the
        // drop-down asks for its longest loadout's words with its margins and arrow, measured as FitChoices measures
        // them, and never for more than the column has for it beside A and B: a name longer than that is cut, where with
        // one setup the column would grow for it.
        loadout.FitToLongestItem = false;
        void Fit()
        {
            if (!GodotObject.IsInstanceValid(loadout) || !GodotObject.IsInstanceValid(a) || !GodotObject.IsInstanceValid(b)) return;
            float room = SetupRoom - a.GetCombinedMinimumSize().X - b.GetCombinedMinimumSize().X - 2 * row.GetThemeConstant("separation");
            float asks = MathF.Ceiling(Longest(loadout).Width + AroundText(loadout));
            loadout.CustomMinimumSize = new Vector2(Math.Max(0, Math.Min(asks, MathF.Floor(room))), 0);
        }
        Fit();
        // In the window its theme is the one it is drawn with.
        loadout.ThemeChanged += Fit;
        return row;
    }

    // As wide as its letter (its holder is not stretched): the loadout's name gets the rest of the row.
    Button SideButton(string side, ButtonGroup group, bool b)
    {
        Button button = Toggle(side, group, SideTip);
        button.ButtonPressed = _state.ShowingB == b;
        button.Pressed += () => ShowSide(b);
        return button;
    }

    void SetCompare(bool on)
    {
        if (_state.Running || on == _state.Compare) return;
        ClosePicker();
        ShowCurrent();
        _state.SetCompare(on);
        _rebuild();   // the A and B buttons, and the list's cells
    }

    // The A or the B button: the setup on the left and the other one change places, as a choice of loadout changes the
    // slots (the loadout's ItemSelected in BuildSetup).
    void ShowSide(bool b)
    {
        if (_state.Running || _state.ShowingB == b) return;
        ClosePicker();
        if (_state.Viewing != null) ShowCurrent();
        _state.SwapSides();
        _gearChanged();   // CombatSimMod.BuildGear: the slots, stances and spells of the setup now on the left
        _loadout.Selected = Math.Max(0, _state.Loadouts.FindIndex(l => l.Id == _state.LoadoutId));
        FillGear();
        Refresh();
    }

    // From Refresh: the Compare box and the A and B buttons are off while a scan runs, as the other choices are; the line
    // above Simulate counts the fights of both setups, and Simulate waits while A and B are the same. That line is one
    // short line in every case: a second one, with a one-stance setup and its Spell box on the left and B tried in each
    // stance, would make the Setup column higher than any single setup does (CombatSimMod.FullHeight).
    void RefreshPair(double hours)
    {
        _compareBox.Disabled = _state.Running;
        if (!_state.Compare || _sideA == null || _sideB == null) return;
        SimSetup a = _state.A, b = _state.B;
        _sideA.Disabled = _sideB.Disabled = _state.Running;
        _sideA.TooltipText = $"Setup A: {_state.NameOf(a)}\n{SideTip}";
        _sideB.TooltipText = $"Setup B: {_state.NameOf(b)}\n{SideTip}";
        int withA = _state.ToSimulate(a).Count, fights = withA + _state.ToSimulate(b).Count;
        _estimate.Text = withA == 0 ? "Nothing to simulate here"
            : _state.SameSetups ? "A and B are the same setup"
            : $"{fights} fights, A and B  ·  {Estimate(fights, hours)}";
        _go.Disabled = !_state.Running && (withA == 0 || _state.SameSetups);
    }

    // ---- middle: the cells -----------------------------------------------------------------------------------

    /// <summary>The column a cell of compare mode belongs to: "xp" for "xp@a"; a column's own key for itself.</summary>
    static string BaseOf(string key) => key.IndexOf('@') is var at and >= 0 ? key[..at] : key;

    // The sort key for the kind of list on show: a column's key stands for its first cell in compare mode (the fights
    // where the choice matters most first) and for what an item adds to it in the items tried for a fight
    // (SimViewUpgrades.cs), and a cell's key for its column in a list of one setup.
    string SortKeyOnShow(string key) =>
        key == NameKey ? key : _state.ShowingUpgrades ? BaseOf(key) + Gain : !_state.ShownComparing ? BaseOf(key)
        : key.Contains('@') && !key.EndsWith(Gain, StringComparison.Ordinal) ? key : BaseOf(key) + Versus;

    List<SimColumn>? _pairCatalog;
    /// <summary>The three cells of every column (<see cref="ColumnOf"/> finds them by their keys).</summary>
    List<SimColumn> PairCatalog => _pairCatalog ??= Catalog.SelectMany(c => new[] { BetterCell(c), SideCell(c, false), SideCell(c, true) }).ToList();

    /// <summary>A column's three cells, left to right: which is better, then A's number and B's.</summary>
    List<SimColumn> PairCells(SimColumn c) => new[] { Versus, WithA, WithB }.Select(s => ColumnOf(c.Key + s)).OfType<SimColumn>().ToList();

    // A column's number with one setup, in the column's own words, coloured by the other setup's; "A" or "B" over it,
    // after the column's name over the first cell.
    SimColumn SideCell(SimColumn c, bool b)
    {
        string side = b ? "B" : "A", other = b ? "A" : "B";
        SimResult? Of(SimResult line) => Usable(b ? _state.Pair(line).B : _state.Pair(line).A);
        return c with
        {
            Key = c.Key + (b ? WithB : WithA),
            Title = side,
            Name = c.Name + " with " + side,
            Tip = $"{c.Tip}, with setup {side}\nGreen where it is better than with {other}, red where it is worse, grey where that could be luck",
            Value = line => Of(line) is { } r ? c.Value(r) : null,
            Text = line => Of(line) is { } r ? c.Text(r) : "-",
            Tint = line => Of(line) is { } r ? SideTint(c, _state.Pair(line), b) ?? c.Tint(r) : GameStyle.TextDim,
            Dash = (c.Dash == "" ? "" : c.Dash + ", and ") + $"when setup {side} has no result for the fight",
        };
    }

    // Which of the two is better, and by how much, under the column's own name: the first thing a line says about the
    // column. The list sorts by it with the fights where the choice matters most first (Matters).
    SimColumn BetterCell(SimColumn c)
    {
        string how = !c.Percent ? ""
            : c.Better == Better.Less ? ", and by how much. \"A by 23%\": 23% less with A than with B"
            : c.Better == Better.More ? ", and by how much. \"A by 23%\": 23% more with A than with B; \"A ×4\": four times as much"
            : ", and by how much. \"A +12%\": 12% more with A than with B; \"A ×4\": four times as much";
        return c with
        {
            Key = c.Key + Versus,
            Tip = $"{c.Name}: which setup {(c.Better == Better.Neither ? "has more" : "is better")}{how}\nGrey where that could be luck",
            // As wide as the column, or as its widest words, whichever is wider: never cut.
            Width = Math.Max(c.Width, BetterWidth),
            Value = line => Matters(c, _state.Pair(line)),
            Text = line => Lead(c, _state.Pair(line)).Text,
            Tint = line => Lead(c, _state.Pair(line)).Tint,
            Better = Better.Neither,
            Percent = false,
            Trim = true,
            Dash = "when one of the two setups has no result or no number for the fight",
            Rate = null,
            Format = null,
            Way = "The first click puts the fights where the choice matters most on top: clear wins by how far apart, then those that could be luck, then the same",
        };
    }

    // Every word a first cell can show at its widest (Lead, Times), measured in the list's own font at this window's own
    // font size, which is rounded on its own at every size, a pixel to spare; given at full size, as SimColumn.Width
    // is. So no first cell is cut at any size, whatever the two numbers are (review of turn 1: "A by 60733%").
    static readonly string[] LeadWords = { "B by 100%", "B by 9.9%", "B ×999+", "B ×9.9", "B +100%", "B +9.9%", "the same" };
    float? _betterWidth;
    float BetterWidth => _betterWidth ??= (MathF.Ceiling(Widest(GameStyle.SemiBold, _s.Px(14), LeadWords)) + 1) / _s.Scale;

    // The widest of these words in one of the game's fonts at a font size of this window, in its pixels.
    static float Widest(Font? font, int size, params string[] words)
    {
        Font measure = font ?? ThemeDB.FallbackFont;
        return words.Max(w => measure.GetStringSize(w, HorizontalAlignment.Left, -1, size).X);
    }

    static SimResult? Usable(SimResult? r) => r is { Failed: false } ? r : null;

    // A column's two numbers for a line, when both setups have a result and a number.
    static (SimResult A, SimResult B, double VA, double VB)? Values(SimColumn c, (SimResult? A, SimResult? B) pair) =>
        Usable(pair.A) is { } a && Usable(pair.B) is { } b && c.Value(a) is { } va && c.Value(b) is { } vb ? (a, b, va, vb) : null;

    // A against B by the simulator's one rule (SimRanges.Judged, as Judge has it), with what decided it: the ranges, the same
    // dice, a real 0; a number that is the worst there is (no food per kill when nothing was killed) by the kills or the runs
    // of the two, the same as another such; a number without a range by what decides it, or could be luck (issue #67).
    static (Verdict Verdict, Ground By) PairJudged(SimColumn c, SimResult a, SimResult b, double va, double vb) =>
        SimRanges.Judged(a, b, c.Rate?.Invoke(), va, vb, c.Better, c.Unranged);

    static Verdict PairVerdict(SimColumn c, SimResult a, SimResult b, double va, double vb) => PairJudged(c, a, b, va, vb).Verdict;

    (Verdict Verdict, Ground By)? PairJudged(SimColumn c, (SimResult? A, SimResult? B) pair) =>
        Values(c, pair) is { } v ? PairJudged(c, v.A, v.B, v.VA, v.VB) : null;

    Verdict? PairVerdict(SimColumn c, (SimResult? A, SimResult? B) pair) => PairJudged(c, pair)?.Verdict;

    // One side's colour, as "Compared with" colours a number (Tint): green where it is the better one, red where the worse,
    // grey where that could be luck; null where neither is (the same, or a number of which neither more nor less is
    // better): the column's own colour then.
    Color? SideTint(SimColumn c, (SimResult? A, SimResult? B) pair, bool b) => PairVerdict(c, pair) switch
    {
        null or Verdict.Same or Verdict.Differs => null,
        Verdict.Better => Tint(b ? Verdict.Worse : Verdict.Better),
        Verdict.Worse => Tint(b ? Verdict.Better : Verdict.Worse),
        { } verdict => Tint(verdict),
    };

    // "A by 23%": the setup that is ahead and by how much; "A ×4" from twice as much up; "A +12%" for a number neither
    // way better, which A has more of; the letter alone where there is no percent (Low HP, Safe, a number against 0);
    // "the same". White where one is ahead, grey where that could be luck, dim for the same. The percent as written
    // beside its ± where one is given (SameDiceLead).
    (string Text, Color Tint, Verdict? Verdict) Lead(SimColumn c, (SimResult? A, SimResult? B) pair, string? percent = null)
    {
        if (Values(c, pair) is not { } v) return ("-", GameStyle.TextDim, null);
        Verdict verdict = PairVerdict(c, v.A, v.B, v.VA, v.VB);
        if (verdict == Verdict.Same) return ("the same", GameStyle.TextDim, verdict);
        bool aAhead = verdict == Verdict.Better || (verdict != Verdict.Worse && Ahead(c.Better, v.VA, v.VB));
        string side = aAhead ? "A" : "B", text = side;
        if (Share(c, v.VA, v.VB) is { } share)
        {
            percent ??= SimRanges.Percent(share);
            text = share >= 1 ? $"{side} ×{Times(1 + share)}" : c.Better == Better.Neither ? $"{side} +{percent}" : $"{side} by {percent}";
        }
        return (text, verdict == Verdict.Luck ? GameStyle.TextGrey : GameStyle.TextWhite, verdict);
    }

    // "×4", "×2.5", "×12": how many times as much, as the percents are written (SimRanges.Percent: a decimal under 10);
    // "×999+" past that, a number against a handful (the Magic XP of a sword), so the words never outgrow their cell.
    static string Times(double ratio) => ratio < 9.95 ? ratio.ToString("0.#") : ratio < 999.5 ? ratio.ToString("0") : "999+";

    static bool Ahead(Better better, double va, double vb) => better == Better.Less ? va < vb : va > vb;

    // How much better the one ahead is, against the other's number: the larger as a share above the smaller, or where less
    // is better, how much less the smaller is. Null where there is no percent: a column "Compared with" gives its own
    // number in, a number that is the worst there is, and a number against 0, as "Compared with" says "vs 0" there.
    static double? Share(SimColumn c, double va, double vb)
    {
        if (!c.Percent || !double.IsFinite(va) || !double.IsFinite(vb)) return null;
        double high = Math.Max(va, vb), low = Math.Min(va, vb);
        if (low <= 0) return null;
        return c.Better == Better.Less ? 1 - low / high : high / low - 1;
    }

    // A and B of one compare, judged by the same dice (issue #57, SimRanges.Apart): the lead and how far luck could move
    // it, "A by 48% ± 47%", on the base of its percent (Share), so that a side is green or red exactly when its lead is
    // larger than its ±, and with one more figure where the two would read alike ("A by 64.4% ± 64.1%",
    // SimRanges.Percents). "A ×4 ± 1.5" for a lead in times, "A by 1,234 ± 2,000" in the column's own format where there
    // is no percent (Net gold). Null where the two are not judged by the same dice, and where they are the same.
    string? SameDiceLead(SimColumn c, (SimResult? A, SimResult? B) pair)
    {
        if (Values(c, pair) is not { } v || c.Rate?.Invoke() is not { } rate || SimRanges.Apart(v.A, v.B, rate) is not { } luck) return null;
        string? percent = null, reach = null;
        if (Share(c, v.VA, v.VB) is { } share)
        {
            double of = luck.Reach / (c.Better == Better.Less ? Math.Max(v.VA, v.VB) : Math.Min(v.VA, v.VB));
            if (share >= 1) reach = of.ToString(of < 0.095 ? "0.##" : of < 9.95 ? "0.#" : "0");
            else (percent, reach) = SimRanges.Percents(share, of);
        }
        var (lead, _, verdict) = Lead(c, pair, percent);
        if (verdict is null or Verdict.Same || !double.IsFinite(v.VA) || !double.IsFinite(v.VB)) return null;
        if (reach == null)
        {
            Func<double, string> format = c.Format ?? Fmt;
            lead = $"{lead} by {format(Math.Abs(v.VA - v.VB))}";
            reach = format(luck.Reach);
        }
        return $"{lead} ± {reach}{(verdict == Verdict.Luck ? ", could be luck" : "")}";
    }

    // How far apart the two are, as Share has it, or the plain difference where there is no percent; infinity against
    // none, or when only one is the worst there is. Null when one setup has no result or no number.
    static double? Apart(SimColumn c, (SimResult? A, SimResult? B) pair)
    {
        if (Values(c, pair) is not { } v) return null;
        bool fa = double.IsFinite(v.VA), fb = double.IsFinite(v.VB);
        if (!fa || !fb) return fa == fb ? 0 : double.PositiveInfinity;
        if (v.VA == v.VB) return 0;
        return c.Percent ? Share(c, v.VA, v.VB) ?? double.PositiveInfinity : Math.Abs(v.VA - v.VB);
    }

    // For sorting by a first cell, the fights where the choice matters most first: those where one setup is clearly
    // better (or, for a number neither way better, clearly has more), by how far apart the two are; then those that could
    // be luck, the same way; then those that are the same. Null, last, when one setup has no result or no number.
    double? Matters(SimColumn c, (SimResult? A, SimResult? B) pair)
    {
        if (Values(c, pair) is not { } v || Apart(c, pair) is not { } apart) return null;
        int tier = PairVerdict(c, v.A, v.B, v.VA, v.VB) switch { Verdict.Same => 0, Verdict.Luck => 1, _ => 2 };
        // How far apart, folded below 1 so that no fight of a tier sorts past one of the tier above; infinity is 1.
        return tier + (double.IsPositiveInfinity(apart) ? 1 : apart / (1 + apart));
    }

    // ---- middle: the lines -----------------------------------------------------------------------------------

    /// <summary>A line that could not be simulated: in compare mode with neither setup.</summary>
    bool LineFailed(SimResult line)
    {
        if (_state.ShowingUpgrades) return UpgradeLineFailed(line);   // also an item not tried (SimViewUpgrades.cs)
        if (!_state.ShownComparing) return line.Failed;
        var (a, b) = _state.Pair(line);
        return Usable(a) == null && Usable(b) == null;
    }

    // What makes a line of the list, for its signature: its result, in compare mode both.
    string LineKey(SimResult line)
    {
        if (_state.ShowingUpgrades) return line.Key + line.RealMilliseconds;   // every item tried is a setup of its own
        if (!_state.ShownComparing) return line.Target.Key + line.RealMilliseconds;
        var (a, b) = _state.Pair(line);
        return $"{a?.Key}{a?.RealMilliseconds}/{b?.Key}{b?.RealMilliseconds}";
    }

    // The clicked fight stays selected on its line, whichever setup's stance the line's key names.
    SimResult? PinnedLine(List<SimResult> lines) =>
        _state.Pinned && _state.Selected is { } key ? lines.FirstOrDefault(l => l.Target.FightKey == SimState.FightOf(key)) : null;

    /// <summary>The results of the lines: in compare mode both of each.</summary>
    IEnumerable<SimResult> BothSides(IEnumerable<SimResult> lines) =>
        !_state.ShownComparing ? lines : lines.SelectMany(l => new[] { _state.Pair(l).A, _state.Pair(l).B }).OfType<SimResult>();

    // The stance on a line, as with one setup. None in compare mode: its lines have the room of Fights, and the enemy's
    // name keeps all of it (EnemyPart). The line's tooltip and Details name each setup's stance.
    string StanceText(SimResult line) => _state.ShownComparing ? "" : line.Target.StanceName;

    // Before the list is filled: the header's cells for the kind of list on show (an earlier scan of two setups is
    // compared whatever the box says, and one of one setup is not), and in Best for each setup's best fight. Over a list
    // of two setups the line above it is one or two lines, whole as every list's (ShowWords).
    void MatchCompare(List<SimResult> lines)
    {
        if (_headerComparing != _state.ShownComparing)
        {
            _offset = 0;
            ResultsWidth();
            FillHeader();
            _tableSignature = "";
        }
        _bestA = _bestB = null;
        if (!_state.ShownComparing || !_state.ShownBest) return;
        _bestA = lines.Select(l => Usable(_state.Pair(l).A)).OfType<SimResult>().MaxBy(_state.Xp)?.Target.FightKey;
        _bestB = lines.Select(l => Usable(_state.Pair(l).B)).OfType<SimResult>().MaxBy(_state.Xp)?.Target.FightKey;
    }

    // The tooltip of a line: each setup's stance (the one it fought in, and whether it was asked for, issue #51), the 95%
    // ranges of each, where the same dice decide each lead with its ± (issue #57), then the notes of the runs.
    string PairTip(SimResult line)
    {
        var (a, b) = _state.Pair(line);
        string Stance(SimResult? r) => r == null ? "no result" : r.StanceShown == "" ? AsGame : StanceWords(r);
        var lines = new List<string>();
        if (a?.StanceShown is { Length: > 0 } || b?.StanceShown is { Length: > 0 })
        {
            if (Stance(a) == Stance(b)) lines.Add($"Stance with A and B: {Stance(a)}");
            else lines.AddRange(new[] { $"Stance with A: {Stance(a)}", $"Stance with B: {Stance(b)}" });
        }
        foreach (var (side, r) in new[] { ("A", a), ("B", b) })
            if (r != null) lines.AddRange(RangeLines(r, "95% ranges with " + side));
        // Without ", could be luck": the grey of the line says it, and the ± shows it.
        var leads = BaseShown().Select(c => SameDiceLead(c, (a, b)) is { } same ? $"{TitleOf(c)} {same.Replace(", could be luck", "")}" : null).OfType<string>().ToList();
        if (leads.Count > 0)
        {
            lines.Add("The lead ± luck, with the same dice");
            lines.AddRange(leads.Chunk(3).Select(three => string.Join("  ·  ", three)));
        }
        foreach (var (side, r) in new[] { ("A", a), ("B", b) })
            if (r is { Note.Length: > 0 }) lines.Add($"{side}: {r.Note}");
        return string.Join("\n", lines);
    }

    // ---- middle: the line above the list ---------------------------------------------------------------------

    // The column the wins are counted in: the one the list is sorted by, XP when it is sorted by name.
    SimColumn? CountedColumn()
    {
        string key = BaseOf(SortedBy() ?? "xp");
        List<SimColumn> shown = BaseShown();
        return shown.FirstOrDefault(c => c.Key == key) ?? shown.FirstOrDefault(c => c.Key == "xp") ?? shown.FirstOrDefault();
    }

    // In place of "With: ...", one or two lines: how many fights each setup wins, by the cells' own verdicts; under it in
    // Best for where each setup does best and which wins there; for an earlier scan its name first, as History has it.
    // The tooltip names the two setups, and in Best for says the best of each in whole sentences.
    void FillPairShowing(List<SimResult> lines)
    {
        // The counts, or before them the two setups' names, parted at their dot where they do not fit on one line; each
        // part of the line laid out by its widest words while a scan goes on (ShowWords).
        ShownPart what = PairCount(lines) ?? new ShownPart($"A: {_state.ShownLabelA}{Dot}B: {_state.ShownLabelB}", $"A: {_state.ShownLabelA}\nB: {_state.ShownLabelB}", Array.Empty<(string, string)>());
        string setups = $"A: {_state.ShownLabelA}\nB: {_state.ShownLabelB}" + (_state.ShownBest ? BestWords(lines) : "");
        if (_state.Viewing is { } scan)
        {
            string Line(string ago) => $"Earlier scan, {ago}:  {scan.Title}";
            ShowWords(new ShownPart(Line(Ago(scan.At)), Line(Ago(scan.At)), AgoWords(scan.At).Select(ago => (Line(ago), Line(ago))).ToArray()), what);
            _showing.TooltipText = $"{scan.At.ToLocalTime():d MMM yyyy, HH:mm}. These numbers are from then: your levels and equipment may have changed.\n{setups}";
        }
        else
        {
            if (_state.ShownBest && BestLine(lines) is { } best) ShowWords(what, best);
            else ShowWords(what);
            _showing.TooltipText = setups;
        }
        if (lines.Count > 0 || _state.Running || _state.Catalog.Count == 0) return;
        _empty.Text = _state.SameSetups
            ? $"A and B are the same setup.\n\nPress B on the left, change {WhatChanges}, then press Simulate."
            : "Nothing simulated with these two setups yet.\n\nPress Simulate: every fight is run with A and with B, from the same moment of your character and with the same dice.";
    }

    // "XP: A is better in 7 of 12 fights, B in 4, the same in 1", by the name over the column; null when no fight has a
    // number with both setups. Laid out by its widest words while the scan goes on: every part there, every count with as
    // many digits as the fights the list can have, each digit the widest (WidestCount), so that it keeps its lines from
    // the first result to the end of the scan and the list under it does not move.
    ShownPart? PairCount(List<SimResult> lines)
    {
        if (CountedColumn() is not { } c) return null;
        int a = 0, b = 0, same = 0, luck = 0, n = 0;
        foreach (SimResult line in lines)
        {
            if (Values(c, _state.Pair(line)) is not { } v) continue;
            n++;
            switch (PairVerdict(c, v.A, v.B, v.VA, v.VB))
            {
                case Verdict.Better: a++; break;
                case Verdict.Worse: b++; break;
                case Verdict.Same: same++; break;
                case Verdict.Luck: luck++; break;
                default: if (Ahead(c.Better, v.VA, v.VB)) a++; else b++; break;
            }
        }
        if (n == 0) return null;
        // The fights the list can have: an earlier scan's are on it already; a scan of the current setups has the chosen ones.
        string most = WidestCount(_state.Viewing != null ? lines.Count : Math.Max(lines.Count, _state.InScope.Count()));
        var (one, parted) = CountWords(c, $"{a}", $"{b}", same > 0 ? $"{same}" : null, luck > 0 ? $"{luck}" : null, $"{n}", n != 1);
        return new ShownPart(one, parted, new[] { CountWords(c, most, most, most, most, most, true) });
    }

    // The counts in words, on one line and parted after A's: "XP: A is better in 7 of 12 fights," over "B in 4, the same in
    // 1, could be luck in 2".
    (string One, string Parted) CountWords(SimColumn c, string a, string b, string? same, string? luck, string n, bool fights)
    {
        bool neither = c.Better == Better.Neither;
        string first = TitleOf(c) + ": " + (neither ? $"more with A in {a} of {n} {(fights ? "fights" : "fight")}" : $"A is better in {a} of {n} {(fights ? "fights" : "fight")}");
        var rest = new List<string> { neither ? $"with B in {b}" : $"B in {b}" };
        if (same != null) rest.Add($"the same in {same}");
        if (luck != null) rest.Add($"could be luck in {luck}");
        return ($"{first}, {string.Join(", ", rest)}", $"{first},\n{string.Join(", ", rest)}");
    }

    // "Best with A: Black Slime, A ×5.5  ·  Best with B: Rainbow Slime, A ×3.5": where each setup gets the most XP
    // of the skill trained, and which setup is better there, in the words of the XP column's first cell; null when neither
    // setup has a result. Parted at its dot like the setups' names ("Best with A: ..." over "Best with B: ..."), one alone
    // after its colon, and laid out by its widest words while a scan goes on: the widest name of the fights the list can
    // have (WidestName) with the widest words of a lead (WidestLead), so that it keeps its lines and the list under it does
    // not move (issue #59).
    ShownPart? BestLine(List<SimResult> lines)
    {
        if (ColumnOf("xp") is not { } xp) return null;
        string name = WidestName(lines), lead = WidestLead();
        (string, string)[] alike =
        {
            ($"Best with A: {name}{lead}{Dot}Best with B: {name}{lead}", $"Best with A: {name}{lead}\nBest with B: {name}{lead}"),
            ($"Best with A and B: {name}{lead}", $"Best with A and B:\n{name}{lead}"),
        };
        string? There(string? fight)
        {
            if (lines.FirstOrDefault(l => l.Target.FightKey == fight) is not { } line) return null;
            var (lead, _, verdict) = Lead(xp, _state.Pair(line));
            string said = verdict switch
            {
                null => "",
                Verdict.Same => ", the same",
                _ => ", " + (lead.Length == 1 ? lead + " is better" : lead) + (verdict == Verdict.Luck ? ", could be luck" : ""),
            };
            return line.Target.Name + said;
        }
        if (_bestA != null && _bestA == _bestB)
            return There(_bestA) is { } both ? new ShownPart("Best with A and B: " + both, "Best with A and B:\n" + both, alike) : null;
        var sides = new[] { ("A", There(_bestA)), ("B", There(_bestB)) }.Where(s => s.Item2 != null).Select(s => (Side: s.Item1, Words: s.Item2!)).ToList();
        if (sides.Count == 0) return null;
        if (sides.Count == 1) return new ShownPart($"Best with {sides[0].Side}: {sides[0].Words}", $"Best with {sides[0].Side}:\n{sides[0].Words}", alike);
        var each = sides.Select(s => $"Best with {s.Side}: {s.Words}").ToList();
        return new ShownPart(string.Join(Dot, each), string.Join("\n", each), alike);
    }

    // The widest name of the fights the list can have, in the line's font: an earlier scan's own lines, otherwise the
    // fights chosen as well. Worked out again only when the fights to choose from or the size change.
    (string Key, string Name) _widestName = ("", "");

    string WidestName(List<SimResult> lines)
    {
        IEnumerable<string> names = lines.Select(l => l.Target.Name);
        string key = $"{_s.Scale}#{_state.Viewing?.Id}#{_state.Catalog.Count}#{string.Join(",", _state.ScopeAreas.Order(StringComparer.Ordinal))}";
        if (_state.Viewing == null) names = names.Concat(_state.InScope.Select(t => t.Name));
        else key += "#" + lines.Count;
        if (_widestName.Key == key) return _widestName.Name;
        Font font = _showing.GetThemeFont("font");
        int size = _showing.GetThemeFontSize("font_size");
        string widest = names.DefaultIfEmpty("").MaxBy(n => font.GetStringSize(n, HorizontalAlignment.Left, -1, size).X)!;
        _widestName = (key, widest);
        return widest;
    }

    // The widest words a lead can take after a fight's name in the best line, as Lead and BestLine write them: ", A by
    // 0.99%, could be luck", ", B ×999+", ", the same"..., every digit the widest of the line's font (WidestCount). They
    // depend on the line's font and size only: worked out once a size, as WidestName is.
    (float Scale, string Words) _widestLead = (0, "");

    string WidestLead()
    {
        if (_widestLead.Scale == _s.Scale && _widestLead.Words != "") return _widestLead.Words;
        string d = WidestCount(1), dd = WidestCount(10), ddd = WidestCount(100);
        var leads = new List<string> { ", the same" };
        foreach (string side in new[] { "A", "B" })
            foreach (string lead in new[] { side + " is better", $"{side} ×{d}.{d}", $"{side} ×{ddd}", $"{side} ×999+", $"{side} by {ddd}%", $"{side} by {d}.{d}%",
                                            $"{side} by {d}.{dd}%", $"{side} by <0.01%", $"{side} +{ddd}%", $"{side} +{d}.{d}%", $"{side} +{d}.{dd}%", $"{side} +<0.01%" })
                foreach (string luck in new[] { "", ", could be luck" })
                    leads.Add($", {lead}{luck}");
        Font font = _showing.GetThemeFont("font");
        int size = _showing.GetThemeFontSize("font_size");
        string widest = leads.MaxBy(words => font.GetStringSize(words, HorizontalAlignment.Left, -1, size).X)!;
        // Kept only once the line has the window's font, which it has inside the window.
        if (_showing.IsInsideTree()) _widestLead = (_s.Scale, widest);
        return widest;
    }

    // "Best with A: Scarecrow (Stab), 13,815 Ranged XP an hour. There: A by 23%", a line for each setup.
    string BestWords(List<SimResult> lines)
    {
        if (ColumnOf("xp") is not { } xp) return "";
        string skill = _state.ShownSkillName != "" ? _state.ShownSkillName + " XP" : "combat XP";
        var said = new List<string>();
        foreach (var (side, fight) in new[] { ("A", _bestA), ("B", _bestB) })
        {
            if (lines.FirstOrDefault(l => l.Target.FightKey == fight) is not { } line) continue;
            var pair = _state.Pair(line);
            if (Usable(side == "A" ? pair.A : pair.B) is not { } r) continue;
            var (lead, _, verdict) = Lead(xp, pair);
            string stance = r.Target.StanceName != "" ? $" ({r.Target.StanceName})" : "";
            said.Add($"Best with {side}: {r.Target.Name}{stance}, {Fmt(_state.Xp(r))} {skill} an hour. There: {lead}{(verdict == Verdict.Luck ? ", could be luck" : "")}");
        }
        return said.Count == 0 ? "" : "\n" + string.Join("\n", said);
    }

    // ---- right: the selected fight with both setups ----------------------------------------------------------

    // All combat XP together, as Details has it ("xp" is the trained skill's in Best for).
    static readonly SimColumn CombatXp = new("combatXp", SimColumns.Experience, "Combat XP", "Combat XP", "Combat XP per hour", 0,
        r => r.XpPerHour, r => Fmt(r.XpPerHour), _ => GameStyle.TextWhite, Better.More, Rate: () => SimRate.PerHour(SimRanges.Xp));

    void FillPairDetails()
    {
        var (a, b) = _state.Selected is { } key ? _state.PairOf(key) : (null, null);
        bool viewing = _state.Viewing != null;
        // Fight is for the setup on the left: its words, its stance and its spell, and the setup "Wear this setup first"
        // puts on, exactly as with one setup (FightSetup.cs). For an earlier scan, A's.
        bool forB = viewing ? Usable(a) == null && Usable(b) != null : _state.ShowingB;
        string side = forB ? "B" : "A";
        FillFight(forB ? b : a);
        string lead = viewing ? $"Fight is for setup {side} of that scan." : $"Fight is for setup {side}, the one on the left.";
        if (_fightTarget != null)
        {
            _fightNote.Text = _fightNote.Text == "" ? lead : lead + " " + _fightNote.Text;
            _fightNote.Visible = true;
        }
        // The other setup has a result for this fight and the one on the left none: say so, rather than no Fight at all.
        else if (!viewing && (Usable(a) ?? Usable(b)) != null)
        {
            _fightNote.Text = lead + ((forB ? b : a) == null ? $" Setup {side} has no result for this fight yet: press Simulate."
                : $" Setup {side} could not be simulated here.");
            _fightNote.Visible = true;
        }

        string signature = "pair#" + _state.Viewing?.Id + "#" + a?.Key + a?.RealMilliseconds + "/" + b?.Key + b?.RealMilliseconds + "#" + _state.ShownSkillId;
        if (signature == _detailsSignature) return;
        _detailsSignature = signature;
        foreach (Node old in _details.GetChildren()) old.QueueFree();
        if ((a ?? b) is not { } any)
        {
            Label hint = Wrapped("Select a fight to see setup A and setup B side by side.", 250, GameStyle.TextGrey, 13);
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            _details.AddChild(hint);
            return;
        }

        // Who, where, and the two setups, each with the stance it fought in under its name (issue #51), as one setup's
        // Details has it (StanceLine): a stance and a setup's name do not fit on one line of the column, a swapped weapon's
        // "+ 1 change" least of all. A result's own name is that of its equipment and spell; a setup without a result here
        // is named with the stance chosen for it. The stance line adds "as the game has it" only where the other setup
        // fought in the same stance because it was asked for: two setups told apart by that alone would otherwise read the
        // same. Every line whole (Whole).
        var head = _s.Spaced(new HBoxContainer(), 10);
        head.AddChild(HeadPicture(_lookup.TargetIcon(any.Target)));
        var title = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 0);
        title.AddChild(Whole(_s.Text(any.Target.Name, GameStyle.TextWhite, 16, GameStyle.Bold)));
        title.AddChild(Whole(_s.Text(any.Target.IsDungeon ? any.Target.CategoryName : $"{any.Target.AreaName}  ·  Level {any.Target.CombatLevel}", GameStyle.TextGrey, 12)));
        foreach (var (letter, r, other, label) in new[] { ("A", a, b, _state.ShownLabelA), ("B", b, a, _state.ShownLabelB) })
        {
            Label with = Whole(_s.Text($"{letter}: {r?.SetupLabel ?? label}", GameStyle.TextDim, 12));
            with.TooltipText = with.Text;
            with.MouseFilter = Control.MouseFilterEnum.Pass;
            title.AddChild(with);
            bool apartByAsking = r != null && other != null && other.StanceIdShown == r.StanceIdShown && !SameStance(r, other);
            if (r != null && StanceLine(r, fight: false, asGame: apartByAsking && !r.StanceAsAsked) is { } stance) title.AddChild(stance);
        }
        head.AddChild(title);
        _details.AddChild(head);

        foreach (var (letter, r) in new[] { ("A", a), ("B", b) })
            if (r is { Note.Length: > 0 }) _details.AddChild(Wrapped($"{letter}: {r.Note}", 250, r.Failed ? Bad : GameStyle.TextGold, 12));
        SimResult? ra = Usable(a), rb = Usable(b);
        if (ra == null && rb == null) return;

        // The numbers judged, by the names their lines show, for the foot.
        var judged = new List<(SimColumn Column, string Name)>();
#if DEVTOOLS
        _devRowNotes.Clear();
#endif
        void Row(SimColumn c, string? name = null, Texture2D? icon = null)
        {
            _details.AddChild(PairRow(c, ra, rb, name, icon));
            judged.Add((c, name ?? c.Name));
        }

        _details.AddChild(_s.Section("Per hour"));
        _details.AddChild(PairCaption());
        foreach (string column in new[] { "kills", "runs" })
            if (ColumnOf(column) is { } c && (column != "runs" || any.Target.IsDungeon)) Row(c);
        Row(CombatXp);
        if (ColumnOf("gold") is { } gold) Row(gold);

        var skills = new[] { ra, rb }.OfType<SimResult>().SelectMany(r => r.Xp.Keys).Distinct()
            .OrderByDescending(id => new[] { ra, rb }.OfType<SimResult>().Max(r => r.PerHour(r.Xp.GetValueOrDefault(id)))).ToList();
        if (skills.Count > 0)
        {
            _details.AddChild(_s.Section("Experience"));
            foreach (string id in skills)
            {
                if (ColumnOf(SimColumns.SkillPrefix + id) is not { } c) continue;
                var (skill, icon) = _lookup.Skill(id);
                Row(c, skill, icon);
            }
        }

        _details.AddChild(_s.Section("Survival"));
        foreach (string column in new[] { "deaths", "lowHp", "damageTaken", "healed", "foodPerKill" })
            if (ColumnOf(column) is { } c) Row(column == "deaths" ? DeathsAsDetails(c) : c);

        var used = new[] { ra, rb }.OfType<SimResult>().SelectMany(r => r.Used).DistinctBy(u => u.Id).ToList();
        if (used.Count > 0)
        {
            _details.AddChild(_s.Section("Used"));
            foreach (Amount item in used.OrderByDescending(u => new[] { ra, rb }.OfType<SimResult>().Max(r => UsedPerHour(r, u.Id))).Take(8))
                _details.AddChild(PairRow(UsedColumn(item), ra, rb, item.Name, _lookup.AmountIcon(item), judged: false));
        }

        string time = ra != null && rb != null && Math.Abs(ra.Hours - rb.Hours) < 1e-9 ? $"{Clock(ra.Hours * 3600)} of fighting simulated with each setup"
            : string.Join(", ", new[] { ("A", ra), ("B", rb) }.Where(s => s.Item2 != null).Select(s => $"{Clock(s.Item2!.Hours * 3600)} with {s.Item1}")) + " of fighting simulated";
        bool ranged = ra?.Spread != null && rb?.Spread != null;
        // Both ways a number is judged (SimRanges.Judged), the second only where A and B met the same dice (issue #57); and
        // where a line above is green beside red by anything else (issue #67: the deaths, the kills, the runs, the game's own
        // numbers, a real 0), that the line says what decided it, which it does under its numbers (PairRow, Decided), as Find
        // upgrades' foot says it (ColourFoot). So the foot is true of every colour above it. "Green beside red": a verdict
        // colours the two sides as a pair (SideTint), one green and the other red, which a line's own colours never do: Deaths
        // "none" green on both sides, a red Lowest hitpoints on both, a red supply used beside a dim one (DeathsAsDetails,
        // UsedColumn). No "grey could be luck": a grey line says so itself, and why where it has no range. A scan made before
        // the ranges is judged without them, its rates apart better or worse as before them. With one setup's result alone
        // nothing is judged: no rule, and no word of ranges where that result has them.
        bool lineSays = ra != null && rb != null && judged.Any(j => PairJudged(j.Column, (ra, rb)) is { Verdict: Verdict.Better or Verdict.Worse } v
                                                                    && Reason(v.By) != null);
        string rule = ra != null && rb != null
            ? ranged ? ColourFoot("Green beside red", "the lead", SimRanges.SameDice(ra, rb), lineSays)
                : "Made before the ranges were counted, so judged without them: simulate again for them."
            : (ra ?? rb)?.Spread == null ? "Made before the ranges were counted: simulate again for them." : "";
        Label foot = Wrapped($"{time}." + (rule != "" ? " " + rule : "") + (viewing ? "" : " Untick Compare for the loot of the setup on the left, item by item."),
            250, GameStyle.TextDim, 11);
#if DEVTOOLS
        _devFoot = foot;
        _devFootLines = ra != null && rb != null
            ? judged.Select(j => (j.Name, PairJudged(j.Column, (ra, rb)) ?? (Verdict.Differs, Ground.None)))
                .Select(j => (j.Name, j.Item2.Verdict, j.Item2.By, _devRowNotes.GetValueOrDefault(j.Name))).ToList()
            : new();
#endif
        foot.HorizontalAlignment = HorizontalAlignment.Center;
        _details.AddChild(new Control { CustomMinimumSize = new Vector2(0, _s.Px(4)), MouseFilter = Control.MouseFilterEnum.Ignore });
        _details.AddChild(foot);
    }

    // Deaths in Details' own words, as with one setup: "none" in green, a number in red.
    static SimColumn DeathsAsDetails(SimColumn deaths) => deaths with
    {
        Text = r => r.Deaths == 0 ? "none" : Fmt(r.DeathsPerHour),
        Tint = r => r.Deaths == 0 ? Good : Bad,
    };

    static double UsedPerHour(SimResult r, string id) => r.PerHour(r.Used.Where(u => u.Id == id).Sum(u => u.Quantity));

    // A supply used, per hour, as Details writes it ("-1,200" in red); what one setup does not use is 0.
    static SimColumn UsedColumn(Amount item) => new("used:" + item.Id, SimColumns.Supplies, item.Name, item.Name, "", 0,
        r => UsedPerHour(r, item.Id), r => UsedPerHour(r, item.Id) is var q and > 0 ? "-" + Fmt(q) : "0",
        r => UsedPerHour(r, item.Id) > 0 ? Bad : GameStyle.TextDim, Better.Less);

    // "[icon] Name                1,204     1,150"
    // "      A by 5%  ·  A ± 6.1%, B ± 7.2%"
    // One number of the fight with both setups, in the look of a Details line (Stat), coloured as the list's cells; under
    // it, as one setup's Details has its range under its number, which is better and by how much (in the words of the
    // list's first cell) and each setup's 95% range. Where the same dice decide (issue #57), the lead with its own ± in
    // their place: "A by 48% ± 47%" (SameDiceLead). A number without a range that could be luck says why, "A, could be
    // luck, no range", and a line green beside red by anything but the ranges or the same dice says what decided it, "A, by
    // the deaths" (issue #67, Decided).
    Control PairRow(SimColumn c, SimResult? a, SimResult? b, string? name = null, Texture2D? icon = null, bool judged = true)
    {
        var pair = (A: a, B: b);
        Color Own(SimResult? r) => r != null ? c.Tint(r) : GameStyle.TextDim;
        Color tintA = (judged ? SideTint(c, pair, false) : null) ?? Own(a), tintB = (judged ? SideTint(c, pair, true) : null) ?? Own(b);
        var (lead, _, verdict) = Lead(c, pair);
        var notes = new List<string>();
        if (judged && SameDiceLead(c, pair) is { } same) notes.Add(same);
        else
        {
            if (judged && verdict is { } v and not Verdict.Same)
                notes.Add(Decided(v, PairJudged(c, pair)?.By ?? Ground.None) is { } why ? $"{lead}, {why}" : lead);
            string? Range(SimResult? r) => r != null && RangeOf(c, r) is { } range ? SimRanges.Text(range, c.Format ?? Fmt) : null;
            string? rangeA = Range(a), rangeB = Range(b);
            if (rangeA != null || rangeB != null) notes.Add($"A {rangeA ?? "-"}, B {rangeB ?? "-"}");
        }
#if DEVTOOLS
        if (judged) _devRowNotes[name ?? c.Name] = string.Join(Dot, notes);
#endif

        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", _s.StatLine());
        var rows = _s.Spaced(new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, -2);
        rows.AddChild(PairLine(name ?? c.Name, icon, (a != null ? c.Text(a) : "-", tintA), (b != null ? c.Text(b) : "-", tintB), 14, GameStyle.Bold));
        // Wrapped, not cut, and parted at its dot, as Stat's own note (SmallLine).
        if (notes.Count > 0) rows.AddChild(SmallLine(string.Join(Dot, notes)));
        panel.AddChild(rows);
        return panel;
    }

    // Over the first line: "A" and "B" over their numbers, with the margins of a Details line.
    Control PairCaption()
    {
        StyleBox line = _s.StatLine();
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxEmpty { ContentMarginLeft = line.GetMargin(Side.Left), ContentMarginRight = line.GetMargin(Side.Right) });
        panel.AddChild(PairLine("", null, ("A", GameStyle.TextGrey), ("B", GameStyle.TextGrey), 12, GameStyle.SemiBold));
        return panel;
    }

    // The widest number a Details line of two setups shows (Fmt, PerKill, the lowest hitpoints, "none"), measured in
    // Details' own font at this window's font size, in its pixels, a pixel to spare.
    float? _pairNumber;
    float PairNumber => _pairNumber ??= MathF.Ceiling(Widest(GameStyle.Bold, _s.Px(14), "99,999", "999.9K", "99.99M", "<0.001", "-1,200", "none", "100%")) + 1;

    // A name, and the two numbers right-aligned in cells as wide as the widest number.
    HBoxContainer PairLine(string name, Texture2D? icon, (string Text, Color Tint) a, (string Text, Color Tint) b, int size, Font? font)
    {
        var line = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 6);
        if (icon != null) line.AddChild(_s.Picture(icon, 18));
        Label label = _s.Text(name);
        label.ClipText = true;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        line.AddChild(label);
        foreach (var (text, tint) in new[] { a, b })
        {
            Label number = _s.Text(text, tint, size, font);
            number.HorizontalAlignment = HorizontalAlignment.Right;
            number.CustomMinimumSize = new Vector2(PairNumber, 0);
            line.AddChild(number);
        }
        return line;
    }

#if DEVTOOLS
    public Button? DevSideButton(bool b) => b ? _sideB : _sideA;

    // Development, in the picker field of sim.request:
    //   pairs   logs, a moment later, what compare mode shows: the line above the list and its tooltip, the line above
    //           Simulate, the sort, every line of the list with each column's first cell (and what it went by: for a
    //           number without a range or the worst there is, what decided it, issue #67) and two numbers with their
    //           colours, the widest words of the first cells against their room, the words above Fight, the Details as
    //           shown with the sentence of its foot that covers each green or red line and what each line says of its
    //           verdict, the tooltips of the column names with what each says of ranges, and the Setup column: the
    //           heading's height against its words' with the Compare box, and the widths the A and B buttons and the
    //           loadout's drop-down ask for beside each other.
    bool DevPairs(string request)
    {
        if (request != "pairs") return false;
        _window.Root.GetTree().CreateTimer(0.3).Timeout += DevLogPairs;
        return true;
    }

    static string DevTint(Color c) => c == Good ? "green" : c == Bad ? "red" : c == GameStyle.TextGrey ? "grey" : c == GameStyle.TextDim ? "dim"
        : c == GameStyle.TextWhite ? "white" : c == GameStyle.TextGold ? "gold" : "#" + c.ToHtml(false);

    void DevLogPairs()
    {
        if (!_window.IsAlive) return;
        const string head = "Dev: compare";
        DevLog?.Invoke($"{head}: a list of {(_state.ShownComparing ? "two setups" : "one setup")}, the box {(_state.Compare ? "ticked" : "not ticked")}, {_state.LeftSide} on the left, " +
                       $"earlier scan {_state.Viewing?.Id ?? "-"}; A {_state.ShownLabelA}; B {_state.ShownLabelB}{(_state.Compare && _state.SameSetups ? "; A AND B ARE THE SAME SETUP" : "")}");
        DevLog?.Invoke($"{head}: above the list '{_showing.Text.Replace("\n", " | ")}' ({_showing.GetLineCount()} lines, {_showing.Size.X:0}x{_showing.Size.Y:0}, " +
                       $"{(_showing.TextOverrunBehavior == TextServer.OverrunBehavior.TrimEllipsis ? "cut with ..." : "not cut")}); its tooltip '{_showing.TooltipText.Replace("\n", " | ")}'");
        DevLog?.Invoke($"{head}: above Simulate '{_estimate.Text.Replace("\n", " | ")}', Simulate {(_go.Disabled ? "greyed out" : "can be pressed")}; " +
                       $"sorted by {SortedBy() ?? "-"}, {(SortedDescending ? "most" : "least")} first; wins counted in {CountedColumn()?.Key ?? "-"}; best with A {_bestA ?? "-"}, with B {_bestB ?? "-"}");
        List<SimResult> lines = Sorted(_state.Lines());
        // The cells of two setups; a list of one setup has none.
        foreach (SimResult line in _state.ShownComparing ? lines.Take(30) : Enumerable.Empty<SimResult>())
        {
            var pair = _state.Pair(line);
            var cells = new List<string> { $"{line.Target.Name} [{StanceText(line)}]{(LineFailed(line) ? " FAILED" : "")}" };
            foreach (SimColumn c in BaseShown())
            {
                var cellA = ColumnOf(c.Key + WithA)!;
                var cellB = ColumnOf(c.Key + WithB)!;
                var (lead, leadTint, verdict) = Lead(c, pair);
                // A number without a range, or the worst there is: what decided it (issue #67, DevNoRange).
                string why = Values(c, pair) is { } v
                    ? $"{v.VA:0.####} vs {v.VB:0.####}, apart {Apart(c, pair):0.####}, sorts as {Matters(c, pair):0.####}" +
                      (DevNoRange(c, v.A, v.B, v.VA, v.VB) is { } unranged ? "; " + unranged : "")
                    : "no two numbers";
                cells.Add($"{c.Key}: '{lead}' ({DevTint(leadTint)}, {verdict?.ToString() ?? "-"}; {why}), A {cellA.Text(line)} ({DevTint(cellA.Tint(line))}), B {cellB.Text(line)} ({DevTint(cellB.Tint(line))})");
            }
            DevChunks(head + " line", cells);
        }
        // The widest first cell on the list against its room, in this window's font and pixels.
        if (_state.ShownComparing && GameStyle.SemiBold is { } semi)
        {
            var widest = lines.SelectMany(l => BaseShown().Select(c => (Column: ColumnOf(c.Key + Versus)!, Words: Lead(c, _state.Pair(l)).Text)))
                .Select(x => (x.Column, x.Words, Width: semi.GetStringSize(x.Words, HorizontalAlignment.Left, -1, _s.Px(14)).X))
                .OrderByDescending(x => x.Width - _s.Px(x.Column.Width)).FirstOrDefault();
            DevLog?.Invoke($"{head}: first cells {BetterWidth:0.#} wide at full size ({_s.Px(BetterWidth)} px here) for the widest words they can show; " +
                           (widest.Column == null ? "no line on show" : $"on the list the tightest is '{widest.Words}' {widest.Width:0.#} px in {_s.Px(widest.Column.Width)} px of {widest.Column.Key}: " +
                           (widest.Width > _s.Px(widest.Column.Width) + 0.01f ? "CUT" : "whole")));
        }
        DevLog?.Invoke($"{head}: {lines.Count} lines; Fight '{_fightButton.Text}' {(_fightButton.Visible ? "shown" : "hidden")} for {_fightTarget?.Key ?? "-"}; " +
                       $"above it '{_fightNote.Text}'; Wear this setup first {(_wearBox.ButtonPressed ? "ticked" : "not ticked")}{(_wearOff != "" ? ", greyed: " + _wearOff : "")}");
        var said = new List<string>();
        foreach (Node node in _details.GetChildren().Where(n => !n.IsQueuedForDeletion()))
        {
            var labels = node.FindChildren("*", "Label", true, false).OfType<Label>().ToList();
            if (node is Label alone) labels.Insert(0, alone);
            // Each label's words, and its colour where it is not the plain white or grey.
            string Said(Label l)
            {
                string tint = l.HasThemeColorOverride("font_color") ? DevTint(l.GetThemeColor("font_color")) : "white";
                return l.Text.Replace("\n", " / ") + (tint is "white" or "grey" ? "" : $" [{tint}]");
            }
            var words = labels.Where(l => l.Text != "").Select(Said).ToList();
            if (words.Count > 0) said.Add(string.Join(" ~ ", words));
        }
        DevChunks(head + " details", said);
        DevLogFoot(head);
        DevLogNoRangeTips(head);
        // A number of Details wider than its cell would make the column wider.
        var wideNumbers = _details.FindChildren("*", "Label", true, false).OfType<Label>()
            .Where(l => l.CustomMinimumSize.X > 0 && l.GetMinimumSize().X > l.CustomMinimumSize.X + 0.5f).Select(l => $"'{l.Text}' {l.GetMinimumSize().X:0} in {l.CustomMinimumSize.X:0}").ToList();
        DevLog?.Invoke($"{head}: details numbers in cells {PairNumber:0} px wide; wider than their cell: {(wideNumbers.Count == 0 ? "none" : "WIDER " + string.Join(", ", wideNumbers))}");

        Control holder = (Control)_compareBox.GetParent(), heading = (Control)holder.GetParent();
        Label? title = heading.GetChildren().OfType<Label>().FirstOrDefault();
        float asks = heading.GetCombinedMinimumSize().Y, wordsHigh = title?.GetCombinedMinimumSize().Y ?? 0;
        DevLog?.Invoke($"{head}: Setup: the Equipment heading {heading.Size.X:0}x{heading.Size.Y:0} asks {asks:0.#} high, its words {wordsHigh:0.#}; the Compare box " +
                       $"{_compareBox.Size.X:0}x{_compareBox.Size.Y:0} (asks {_compareBox.GetCombinedMinimumSize().X:0}x{_compareBox.GetCombinedMinimumSize().Y:0}) at y {_compareBox.Position.Y:0.#} " +
                       $"in its holder {holder.Size.X:0}x{holder.Size.Y:0}: " +
                       (asks > wordsHigh + 0.5f ? "THE BOX MAKES THE HEADING HIGHER" : "the heading is no higher for the box") +
                       $"; {(_compareBox.Disabled ? "greyed, its words #" + _compareBox.GetThemeColor("font_disabled_color").ToHtml(false) : "can be clicked")}");
        if (_sideA != null && _sideB != null && _loadout.GetParent() is Control row)
        {
            Font font = _loadout.GetThemeFont("font");
            float text = font.GetStringSize(_loadout.Text, HorizontalAlignment.Left, -1, _loadout.GetThemeFontSize("font_size")).X, room = _loadout.Size.X - AroundText(_loadout);
            float column = SetupRoom;
            DevLog?.Invoke($"{head}: Setup: A {_sideA.Size.X:0}x{_sideA.Size.Y:0} ({(_sideA.ButtonPressed ? "down" : "up")}, asks {_sideA.GetCombinedMinimumSize().X:0}), " +
                           $"B {_sideB.Size.X:0}x{_sideB.Size.Y:0} ({(_sideB.ButtonPressed ? "down" : "up")}, asks {_sideB.GetCombinedMinimumSize().X:0}), " +
                           $"the loadout {_loadout.Size.X:0}x{_loadout.Size.Y:0} asks {_loadout.GetCombinedMinimumSize().X:0} (its longest '{Longest(_loadout).Text}' {Longest(_loadout).Width:0.#} " +
                           $"and {AroundText(_loadout):0.#} around it; Godot itself {_loadout.GetMinimumSize().X:0}); '{_loadout.Text}' {text:0.#} wide in {room:0.#}: {(text > room + 0.01f ? "CUT" : "whole")}; " +
                           $"their row {row.Size.X:0}x{row.Size.Y:0} asks {row.GetCombinedMinimumSize().X:0} of the column's {column:0}: " +
                           (row.GetCombinedMinimumSize().X > column + 0.5f ? "WIDER THAN THE COLUMN" : "within the column"));
        }
        DevLogThreeColumns(head);
    }
#endif
}
