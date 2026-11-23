using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MelvorMods.Shared;

namespace CombatSim;

/// <summary>
/// How sure a result is (issue #5), in the simulator's window: the 95% range of every rate, in the tooltip of its
/// line in the list and on the small line under it in Details (the look of the details' own notes), and the
/// "Go on until" box beside "Each fight lasts" (a drop-down of the game's, like the one beside it). The ranges come
/// from <see cref="SimRanges"/>; "Compared with" judges by them (SimViewColumns.cs). Neither the list nor the window
/// gets any wider for them.
/// </summary>
sealed partial class SimView
{
    OptionButton _sure = null!;

    // Beside "Each fight lasts", in the same row: the window gets no higher for it. Caption and choice read as one
    // phrase: "Go on until ± 2% sure", "Go on until time is up". The two boxes share the row in halves and line up with
    // "Fights | Best for" above and "Unlimited | From bank" below (the row's spacing is theirs, SimView.BuildSetup).
    Control SureBox()
    {
        var box = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 6);
        box.AddChild(Caption("Go on until"));
        _sure = _s.Dropdown();
        string most = SimText.Clock(SimState.Durations[^1].Hours * 3600), largest = SimRanges.Size(SimState.SureSizes[^1]);
        _sure.TooltipText = $"{SureItem(0)}: each fight lasts the time on the left.\n" +
                            $"{SureItem(SimState.SureSizes[1])} to {SureItem(SimState.SureSizes[^1])}: then it goes on until every number in the list\n" +
                            $"is known to that size, {most} of fighting at most. A number that {most} cannot bring\n" +
                            $"that far goes on to the next larger size it can reach; one that cannot reach\n" +
                            $"even {largest} (a few deaths, gold from a rare drop) does not hold it back.\n" +
                            "A smaller size never ends a fight sooner than a larger one.\n" +
                            "Not with supplies From bank: those fights are about how far your supplies go.";
        foreach (double size in SimState.SureSizes) _sure.AddItem(SureItem(size));
        FitChoices(_sure);
        FitChoices(_duration);
        _sure.Selected = Math.Max(0, Array.IndexOf(SimState.SureSizes, _state.Sure));
        _sure.ItemSelected += index =>
        {
            _state.Sure = SimState.SureSizes[(int)index];
            _changed();
            Refresh();
        };
        box.AddChild(_sure);
        return box;
    }

    /// <summary>A choice of the "Go on until" box: "Time is up", "± 2% sure".</summary>
    internal static string SureItem(double size) => size == 0 ? "Time is up" : SimRanges.Size(size) + " sure";

    // A drop-down of the row is as wide as its longest choice needs, and otherwise gets half of the row. Godot's own fit
    // to the longest choice (FitToLongestItem) asked for about 30 px more than the words, measured in the game: 117 px
    // at size 0.9 for "15 minutes", which is 60 px wide with 23 px of margins and arrow around it (90 for 38 at 0.6,
    // 132 for 76 at 1.15). Two such boxes did not fit the Setup column, and the box that got the rest cut its words
    // ("± 10% sur"). So a box asks for none of its words (FitToLongestItem off with
    // ClipText, as the Item Encyclopedia's area list and Skill Bonuses' list do). Its least width is its longest choice
    // instead, measured in its own font and size, with the margins, arrow and h_separation of the game's drop-down
    // theme around it: no choice can be cut, at any size, whichever one is chosen.
    static void FitChoices(OptionButton box)
    {
        box.ClipText = true;
        box.FitToLongestItem = false;
        void Fit() => box.CustomMinimumSize = new Vector2(MathF.Ceiling(Longest(box).Width + AroundText(box)), 0);
        Fit();
        // In the window its theme is the one it is drawn with, and the layout direction is known.
        box.ThemeChanged += Fit;
    }

    // The widest choice of a drop-down in its own font and size, and its width.
    static (string Text, float Width) Longest(OptionButton box)
    {
        Font font = box.GetThemeFont("font");
        int size = box.GetThemeFontSize("font_size");
        return Enumerable.Range(0, box.ItemCount).Select(i => box.GetItemText(i))
            .Select(text => (text, font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X))
            .DefaultIfEmpty(("", 0f)).MaxBy(choice => choice.Item2);
    }

    // The looks a drop-down is drawn in; their margins frame its text.
    static readonly string[] Looks = { "normal", "hover", "pressed", "hover_pressed", "disabled" };

    // What a drop-down keeps free around its text: the margins of its widest look (left to right, or right to left
    // in a window laid out that way), its arrow, and the h_separation Godot keeps between the text and the arrow.
    static float AroundText(OptionButton box) => Margins(box) + Arrow(box);

    // Also a button's, for its words (cuts=log, CutProbe.cs).
    static float Margins(Button box)
    {
        bool mirrored = box.IsInsideTree() && box.IsLayoutRtl();
        float widest = 0;
        foreach (string name in Looks)
            if (box.HasThemeStylebox(mirrored ? name + "_mirrored" : name) && box.GetThemeStylebox(mirrored ? name + "_mirrored" : name) is { } look)
                widest = Math.Max(widest, look.GetMargin(Side.Left) + look.GetMargin(Side.Right));
        return widest;
    }

    static float Arrow(OptionButton box) =>
        box.HasThemeIcon("arrow") && box.GetThemeIcon("arrow") is { } arrow ? arrow.GetWidth() + Math.Max(0, box.GetThemeConstant("h_separation")) : 0;

    // Fights go on past their time only with unlimited supplies (SimEngine.Fight).
    bool GoesOn => _state.Sure > 0 && _state.Unlimited;

    /// <summary>
    /// How long the run will take, after the number of fights: "about 30s"; when fights go on until they are sure,
    /// the most it can take, "up to 2 min".
    /// </summary>
    string Estimate(int fights, double hours)
    {
        double most = SimState.Durations[^1].Hours;
        return GoesOn && hours < most ? "up to " + Took(fights, most) : "about " + Took(fights, hours);
    }

    // A copy of the character takes about a second per fight; the fighting itself is the small part.
    // Fights run side by side share the processor with the game: four of them are worth about one and a half.
    // Find upgrades runs fewer at a time than a scan (SimUpgrades.cs, UpgradeSearch.Workers).
    string Took(int fights, double hours, int? workers = null)
    {
        double seconds = 1 + fights * (1.0 + 0.2 * hours) / (1 + 0.15 * (Math.Max(1, Math.Min(workers ?? _state.Workers, fights)) - 1));
        return seconds < 90 ? $"{Math.Max(1, Math.Round(seconds))}s" : $"{Math.Round(seconds / 60)} min";
    }

    /// <summary>The rates the list shows, whose ranges "Go on until" waits for.</summary>
    public List<SimRate> WatchedRates() => Shown().Select(c => c.Rate?.Invoke()).OfType<SimRate>().Distinct().ToList();

    /// <summary>The range of a column's number for a result; null when the column has none, or the result.</summary>
    SimRange? RangeOf(SimColumn column, SimResult r) =>
        column.Rate?.Invoke() is { } rate && column.Value(r) is { } value && double.IsFinite(value) ? SimRanges.Of(r, rate) : null;

    // The tooltip of a line: the stance it was fought in (issue #51, SimViewStance.cs), then the range of each number of
    // the list, three to a line, in the list's order, by the names over the list; then the note of the run. The numbers
    // themselves are on the line already.
    //   Stance: Accurate, as the game has it
    //   95% ranges
    //   Kills ± 7.9%  ·  XP ± 6.3%  ·  Gold ± 14%
    //   Food ± 15%  ·  Food/kill ± 18%  ·  Deaths up to 3.69
    string RowTip(SimResult r)
    {
        if (_state.ShownComparing) return PairTip(r);   // both setups (SimViewCompare.cs)
        var lines = new List<string>();
        if (StanceTipLine(r) is { } stance) lines.Add(stance);
        lines.AddRange(RangeLines(r, "95% ranges"));
        if (r.Note != "") lines.Add(r.Note);
        return string.Join("\n", lines);
    }

    // The ranges of a result's numbers in the list, under a heading; nothing when it has none.
    List<string> RangeLines(SimResult r, string heading)
    {
        var ranges = new List<string>();
        if (!r.Failed)
            foreach (SimColumn column in BaseShown())
                if (RangeOf(column, r) is { } range && SimRanges.Text(range, column.Format ?? Fmt) is { } text)
                    ranges.Add($"{TitleOf(column)} {text}");
        var lines = new List<string>();
        if (ranges.Count > 0)
        {
            lines.Add(heading);
            lines.AddRange(ranges.Chunk(3).Select(three => string.Join("  ·  ", three)));
        }
        return lines;
    }

    // A column's name over the list: in "Best for" the XP column is named after the skill (as in SimViewColumns).
    string TitleOf(SimColumn column) => column.Key == "xp" && _state.ShownBest && _state.ShownSkillName != "" ? _state.ShownSkillName : column.Title;

    /// <summary>The range of a rate for the small line of a details line: "± 2%", "0.62 to 8.77"; null when it has none.</summary>
    string? Ranged(SimResult r, SimRate rate, Func<double, string>? format = null) =>
        SimRanges.Of(r, rate) is { } range ? SimRanges.Text(range, format ?? Fmt) : null;

    /// <summary>The range of an item dropped or used, per hour; null in a scan made before the ranges.</summary>
    static string? Ranged(SimResult r, Amount amount) =>
        SimRanges.OfItem(r, amount) is { } range ? SimRanges.Text(range, Fmt) : null;

    /// <summary>The parts of a small line, joined the way the details join them; null when there is none.</summary>
    static string? Notes(params string?[] parts)
    {
        var said = parts.Where(p => !string.IsNullOrEmpty(p)).ToList();
        return said.Count == 0 ? null : string.Join("  ·  ", said);
    }

    // The small line of Deaths: with none, how many an hour would still fit what was seen ("none" is not "never").
    string? DeathsNote(SimResult r) => r.Deaths == 0
        ? Ranged(r, SimRate.PerHour(SimRanges.Deaths)) is { } upTo ? upTo + " an hour" : null
        : Notes(Ranged(r, SimRate.PerHour(SimRanges.Deaths)), $"first after {Clock(r.FirstDeathSeconds)}");

    // "Your food lasts": the kills the food in the bank is enough for at the two ends of the range of food per kill,
    // on a line of its own above the food in the bank, so neither is cut in the narrow column.
    string LastsNote(SimResult r, long food, string inBank)
    {
        if (food <= 0 || SimRanges.Of(r, SimRate.PerKill(SimRanges.Food)) is not { High: > 0 } range) return inBank;
        string fewest = Fmt(Math.Floor(food / range.High));
        return (range.Low > 0 ? $"{fewest} to {Fmt(Math.Floor(food / range.Low))} kills" : $"{fewest} kills or more") + "\n" + inBank;
    }

    // After the time it took, at the end of the details: what the ranges are, or that this result has none.
    static string RangesFoot(SimResult r) =>
        r.Spread != null ? " Ranges like these hold the true number 95 times in 100." : " Made before the ranges were counted: simulate again for them.";

#if DEVTOOLS
    // Development, in the picker field of sim.request:
    //   hover=<part of an enemy's name>   moves the pointer onto that line (or "first"; "sure" for the Go on until box),
    //                                     so its tooltip comes up in the picture; 1.2 s later the tooltip that is up is
    //                                     logged
    //   ranges                            logs the tooltip of the first lines and every line of the details as shown, the
    //                                     verdicts of "Compared with" with what each line says of its verdict, and the
    //                                     tooltips of the column names with what each says of ranges
    bool DevRanges(string request)
    {
        if (request.StartsWith("hover=", StringComparison.Ordinal))
        {
            string name = request[6..].Trim();
            Control? row = name == "sure" ? _sure : _rows.GetChildren().OfType<Button>().Where(b => !b.IsQueuedForDeletion())
                .FirstOrDefault(b => name == "first" || b.FindChildren("*", "Label", true, false).OfType<Label>().Any(l => l.Text.Contains(name, StringComparison.OrdinalIgnoreCase)));
            if (row == null || !row.IsVisibleInTree())
            {
                DevLog?.Invoke($"Dev: ranges: no line {name} in sight to hover");
                return true;
            }
            Vector2 spot = _window.Root.GetTree().Root.GetFinalTransform() * row.GetGlobalRect().GetCenter();
            Input.ParseInputEvent(new InputEventMouseMotion { Position = spot, GlobalPosition = spot });
            string what = row == _sure ? "the Go on until box" : $"the line of {row.FindChildren("*", "Label", true, false).OfType<Label>().FirstOrDefault()?.Text}";
            DevLog?.Invoke($"Dev: ranges: the pointer is on {what}; its tooltip says: {row.TooltipText.Replace("\n", " | ")}");
            _window.Root.GetTree().CreateTimer(1.2).Timeout += () =>
            {
                if (_window.IsAlive) DevLog?.Invoke("Dev: ranges: tooltip up: " + GameStyle.DevTooltips(_window.Root.GetTree().Root).Replace("\n", " | "));
            };
            return true;
        }
        if (request != "ranges") return false;
        var rows = _rows.GetChildren().OfType<Button>().Where(b => !b.IsQueuedForDeletion()).Take(3).ToList();
        foreach (Button row in rows) DevLog?.Invoke("Dev: ranges: line tooltip: " + row.TooltipText.Replace("\n", " | "));
        var lines = new List<string>();
        foreach (Node line in _details.GetChildren().Where(n => !n.IsQueuedForDeletion()))
        {
            var words = line.FindChildren("*", "Label", true, false).OfType<Label>().Select(l => DevPlain(l.Text)).Where(t => t != "").ToList();
            if (line is Label alone) words.Insert(0, DevPlain(alone.Text));
            if (words.Count > 0) lines.Add(string.Join(" ~ ", words));
        }
        DevChunks("Dev: ranges: details", lines);
        // The small lines that wrapped (a Label with more lines than its "\n" give): what the column could not hold.
        int wrapped = _details.FindChildren("*", "Label", true, false).OfType<Label>()
            .Count(l => l.AutowrapMode != TextServer.AutowrapMode.Off && l.GetLineCount() > l.Text.Count(c => c == '\n') + 1);
        DevLog?.Invoke($"Dev: ranges: Go on until = {_sure.Text} ({_state.Sure}, {(_sure.Disabled ? "greyed out" : "can be chosen")}), estimate '{_estimate.Text.Replace("\n", " | ")}' ({_estimate.GetLineCount()} lines), " +
                       $"box {_sure.Size.X:0}x{_sure.Size.Y:0} beside the duration's {_duration.Size.X:0}x{_duration.Size.Y:0}, their row {_sure.GetParent().GetParent<Control>().Size.X:0} wide; " +
                       $"details labels that wrapped: {wrapped}");
        Control setup = _sure.GetParent().GetParent<Control>().GetParent<Control>();
        DevLog?.Invoke($"Dev: ranges: the row of drop-downs ({(_state.BestMode ? "Best for" : "Fights")}, size {_s.Scale:0.##}) in a Setup column {setup.Size.X:0} wide inside " +
                       $"(it asks for {setup.GetCombinedMinimumSize().X:0}); {DevLinedUp(setup)}");
        DevLog?.Invoke($"Dev: ranges: Each fight lasts {DevRoom(_duration)}");
        DevLog?.Invoke($"Dev: ranges: Go on until {DevRoom(_sure)}");
        DevCompared();
        DevLogNoRangeTips("Dev: ranges:");
        return true;
    }

    // Development: where the two boxes of the row start and end in the Setup column, against the two buttons of the rows
    // above and below them.
    string DevLinedUp(Control setup)
    {
        float left = setup.GetGlobalRect().Position.X;
        string Span(Control a, Control b) =>
            $"{a.GetGlobalRect().Position.X - left:0} to {a.GetGlobalRect().End.X - left:0} | {b.GetGlobalRect().Position.X - left:0} to {b.GetGlobalRect().End.X - left:0}";
        string boxes = Span(_duration, _sure), above = Span(_modeFights, _modeBest), below = Span(_unlimited, _fromBank);
        return $"boxes {boxes}, Fights | Best for {above}, Unlimited | From bank {below}: " +
               (boxes == above && boxes == below ? "lined up" : "NOT LINED UP");
    }

    // Development: a drop-down's caption, its width (what it asks for, its least width for its longest choice, what
    // Godot itself asks), the room its text gets in its widest look, and every choice with its width, whole or cut.
    string DevRoom(OptionButton box)
    {
        float room = box.Size.X - AroundText(box);
        Font font = box.GetThemeFont("font");
        int fontSize = box.GetThemeFontSize("font_size");
        var choices = Enumerable.Range(0, box.ItemCount).Select(i => box.GetItemText(i))
            .Select(text => (Text: text, Width: font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X)).ToList();
        var cut = choices.Where(c => c.Width > room + 0.01f).ToList();
        Label? caption = box.GetParent().GetChildren().OfType<Label>().FirstOrDefault();
        var row = box.GetParent().GetParent<HBoxContainer>();
        float half = (row.Size.X - row.GetThemeConstant("separation")) / 2;
        return $"(caption '{caption?.Text}' {(caption == null ? 0 : caption.GetCombinedMinimumSize().X):0} wide): box is {box.Size.X:0} " +
               $"(half the row {half:0.#}, asks {box.GetCombinedMinimumSize().X:0}, least {box.CustomMinimumSize.X:0}, Godot itself {box.GetMinimumSize().X:0}), " +
               $"margins {Margins(box):0.#}, arrow and h_separation {Arrow(box):0}, font {fontSize}; room for text {room:0.#}; choices " +
               string.Join(", ", choices.Select(c => $"'{c.Text}' {c.Width:0.#}")) + ": " +
               (cut.Count == 0 ? "every choice whole" : "CUT " + string.Join(", ", cut.Select(c => $"'{c.Text}' by {c.Width - room:0.#}")));
    }

    // Development: the verdicts of "Compared with" for the selected line, as its colours show them, what each line shows
    // (ComparedLines: the change and its small line, as drawn), and what each went by: for B of the same compare the same
    // dice (issue #57: the difference against the bar, the two own halves added times the share of luck left raised by its
    // noise, SimRanges.Apart), otherwise the two ranges, the counts behind a quantity one of them had none of, or a real 0
    // (SimRanges.Basis).
    void DevCompared()
    {
        var shown = _state.ShownResults;
        if (_state.Selected == null || shown.GetValueOrDefault(SimResult.MakeKey(_state.ShownSetupKey, _state.Selected)) is not { } r) return;
        foreach (SimResult other in shown.Values.Where(o => o.Target.Key == r.Target.Key && o.SetupKey != r.SetupKey && !o.Failed))
        {
            var said = new List<string>();
            var lines = ComparedLines(r, other).GroupBy(l => l.Name).ToDictionary(g => g.Key, g => g.First());
            foreach (SimColumn column in BaseShown())
            {
                if (column.Value(r) is not { } mine || column.Value(other) is not { } theirs || !double.IsFinite(mine) || !double.IsFinite(theirs)) continue;
                string why = DevRuleOf(column, r, other, mine, theirs, column.Percent);
                bool listed = lines.TryGetValue(NameOf(column), out var line);
                string drawn = listed ? line.Text == null ? "in The same" : $"shows '{line.Text}'" + (line.Note != null ? $" over '{line.Note}'" : "") : "not shown";
                // What its small line must say of its verdict (issue #67): what decided it, or why it could be luck.
                var (verdict, by) = Judged(column, r, other);
                string says = listed && line.Text != null ? DevSaid(verdict, by, line.Note) : "";
                said.Add($"{NameOf(column)} {column.Text(r)} vs {column.Text(other)} {verdict}, {drawn} ({why}){(says != "" ? "; " + says : "")}");
            }
            DevLog?.Invoke($"Dev: ranges: compared with {other.SetupLabel}: {string.Join("; ", said)}");
        }
    }

    // Development: what a verdict of the window between two results went by, for "Compared with" (DevCompared) and Find
    // upgrades (issue #63, DevLogUpgrades): with the same dice, the difference against the bar (SimRanges.Apart, DevBar),
    // and BOTH READ where SimRanges.Percent alone would write the change, a share of theirs when it is in percent, and its ±
    // alike, which SimRanges.Percents tells apart; otherwise the step of the rule that left them to their own ranges
    // (SimRanges.DevWhy) and the two ranges, the counts behind a quantity one of them had none of, or a real 0
    // (SimRanges.Basis). For a number without a range of its own, a number without a rate, the worst there is or a real 0,
    // then what decided it (issue #67, DevNoRange).
    string DevRuleOf(SimColumn column, SimResult r, SimResult other, double mine, double theirs, bool percent)
    {
        string why = column.Rate?.Invoke() is { } rate && double.IsFinite(mine) && double.IsFinite(theirs)
            ? DevRateRule(rate, r, other, mine, theirs, percent) : "no rate";
        return DevNoRange(column, r, other, mine, theirs) is { } unranged ? (why == "no rate" ? unranged : $"{why}; {unranged}") : why;
    }

    // Development: what a rate's verdict between two results went by (DevRuleOf).
    static string DevRateRule(SimRate rate, SimResult r, SimResult other, double mine, double theirs, bool percent)
    {
        if (SimRanges.Apart(r, other, rate) is { } luck)
        {
            string why = $"RULE SAME DICE: |d| {Fmt(Math.Abs(luck.Difference))} against a bar of {Fmt(luck.Reach)}, {SimRanges.DevBar(luck, Fmt)}";
            // A change and a ± that SimRanges.Percent alone would write alike, which SimRanges.Percents tells apart.
            string alike = percent && theirs > 0 ? SimRanges.Percent(Math.Abs(mine / theirs - 1)) : "";
            if (alike != "" && alike == SimRanges.Percent(luck.Reach / theirs)) why += $"; BOTH READ '{alike}' BY PERCENT ALONE";
            return why;
        }
        var (a, b, realZero) = SimRanges.Basis(r, other, rate);
        bool behind = a != null && b != null && (a != SimRanges.Of(r, rate) || b != SimRanges.Of(other, rate));
        string Ends(SimRange? x) => x is { } e ? $"{Fmt(e.Low)} to {Fmt(e.High)}" : "none";
        return $"RULE AS BEFORE ({SimRanges.DevWhy(r, other, rate).Or("-")}): " +
               (realZero ? "a real 0" : a == null || b == null ? "no ranges" : $"{(behind ? "the counts behind it " : "")}{Ends(a)} against {Ends(b)}");
    }

    // Development (issue #67): for a number judged without a range of its own (a number without a rate, the worst there is
    // on one side, a real 0, a rate a fight was too short to have a range for) both numbers and what decided it, in the
    // words of the rule's step (SimRanges.Judged): NO RANGE: ONE MOMENT, BY THE DEATHS, BY THE KILLS or BY THE RUNS with that
    // line's verdict and what it went by, THE GAME'S NUMBERS with the biggest hits and auto-eat points read from the two
    // results, REAL 0, or A FIGHT TOO SHORT FOR ONE; then the verdict of the rule before this issue and the one the window
    // shows, BEFORE <verdict>, AFTER <verdict>. SURE WITHOUT A RANGE where such a number is green or red by none of these: a
    // fault, which must never appear. Null for a rate judged by its ranges, by the same dice or as a scan made before the
    // ranges, whose verdict this issue left as it was.
    static string? DevNoRange(SimColumn column, SimResult r, SimResult other, double mine, double theirs)
    {
        SimRate? rate = column.Rate?.Invoke();
        var (after, by) = SimRanges.Judged(r, other, rate, mine, theirs, column.Better, column.Unranged);
        bool finite = double.IsFinite(mine) && double.IsFinite(theirs);
        if (rate != null && finite && by is not (Ground.RealZero or Ground.Short)) return null;
        static string N(double v) => double.IsFinite(v) ? v.ToString("0.####") : v > 0 ? "infinity" : "-infinity";
        static string Up(Verdict v) => v.ToString().ToUpperInvariant();
        static string Hit(SimResult x) => $"{x.MaxEnemyHit?.ToString() ?? "-"} (the game's {x.GameMaxHit?.ToString() ?? "-"})";
        // The line with a range that decided it: its two numbers, its verdict by the one rule and what that went by.
        static string Line(string what, SimRate line, SimResult a, SimResult b, double va, double vb, Better better) =>
            $"{what} {N(va)} against {N(vb)} an hour {Up(SimRanges.Judge(a, b, line, va, vb, better))} ({DevRateRule(line, a, b, va, vb, false)})";
        string said = by switch
        {
            Ground.Moment => "NO RANGE: ONE MOMENT",
            Ground.Deaths => "NO RANGE: BY THE DEATHS, " +
                             Line("deaths", SimRate.PerHour(SimRanges.Deaths), r, other, r.DeathsPerHour, other.DeathsPerHour, Better.Less),
            Ground.Kills => "NO RANGE: BY THE KILLS, " +
                            Line("kills", SimRate.PerHour(SimRanges.Kills), r, other, r.KillsPerHour, other.KillsPerHour, Better.More),
            Ground.Runs => "NO RANGE: BY THE RUNS, " +
                           Line("runs", SimRate.PerHour(SimRanges.Runs), r, other, r.PerHour(r.DungeonRuns), other.PerHour(other.DungeonRuns), Better.More),
            Ground.Game => $"NO RANGE: THE GAME'S NUMBERS, the enemy's biggest hit {Hit(r)} against {Hit(other)}, " +
                           $"auto eat at {r.AutoEatHp?.ToString("0.#") ?? "-"} against {other.AutoEatHp?.ToString("0.#") ?? "-"} HP" +
                           $" (game-safe {SimRanges.GameSafe(r)?.ToString() ?? "-"} against {SimRanges.GameSafe(other)?.ToString() ?? "-"})",
            Ground.RealZero => "NO RANGE: REAL 0",
            Ground.Short => "NO RANGE: A FIGHT TOO SHORT FOR ONE",
            _ => "no range, nothing to decide: the same, or neither way better",
        };
        bool sure = by is Ground.Deaths or Ground.Kills or Ground.Runs or Ground.Game or Ground.RealZero or Ground.Short;
        return $"{N(mine)} against {N(theirs)}: {said}; BEFORE {Up(SimRanges.DevBefore(r, other, rate, mine, theirs, column.Better))}, AFTER {Up(after)}" +
               (after is Verdict.Better or Verdict.Worse && !sure ? "; SURE WITHOUT A RANGE" : "");
    }

    // Development (issue #67): the foot of Details on show (Compare's, or an item's of Find upgrades) as it is drawn, read
    // from its label, and for every green or red line above it the part of those words that covers it: the two ranges, the
    // same dice, a scan judged without ranges, or "or where a line says what decided it" together with the words its line
    // says (Decided). NOT COVERED BY THE FOOT where no part does: a fault. Every grey line above it with what its line says
    // of why it could be luck, NOT SAID ON ITS LINE where it does not (DevSaid): a fault too.
    Label? _devFoot;
    List<(string Name, Verdict Verdict, Ground By, string? Note)> _devFootLines = new();
    // The small line of each Details line judged, by the name it shows, as the code wrote it (PairRow, FillUpgradeDetails).
    readonly Dictionary<string, string> _devRowNotes = new();

    void DevLogFoot(string head)
    {
        if (_devFoot is not { } foot || !GodotObject.IsInstanceValid(foot) || foot.IsQueuedForDeletion() || !foot.IsInsideTree())
        {
            DevLog?.Invoke($"{head} foot: none on show");
            return;
        }
        string words = foot.Text;
        DevLog?.Invoke($"{head} foot as drawn: '{words}'");
        string? Part(string part) => words.Contains(part, StringComparison.Ordinal) ? part : null;
        var said = new List<string>();
        foreach (var (name, verdict, by, note) in _devFootLines.Where(l => l.Verdict is Verdict.Better or Verdict.Worse or Verdict.Luck))
        {
            string line = DevSaid(verdict, by, note);
            if (verdict == Verdict.Luck)
            {
                said.Add($"{name} grey by {by}: {line}");
                continue;
            }
            bool byLine = Reason(by) != null && !line.StartsWith("NOT SAID", StringComparison.Ordinal);
            string? part = Part("judged without them")
                ?? (by == Ground.Ranges ? Part("95% ranges do not overlap")
                    : by == Ground.SameDice ? Part("with the same dice where")
                    : byLine ? Part(LineSays) : null);
            said.Add($"{name} {verdict.ToString().ToUpperInvariant()} by {by}: " + (part != null ? $"covered by '{part}'" : "NOT COVERED BY THE FOOT") +
                     (line != "" ? "; " + line : ""));
        }
        DevChunks(head + " foot covers", said.Count > 0 ? said : new List<string> { "no green, red or grey line above it" });
    }

    // Development (issue #67): whether a line's small line says what its verdict needs it to say (Decided): what decided a
    // green or red one that the ranges or the same dice did not, or why a grey one could be luck. "its line says '...'" where
    // it does, NOT SAID ON ITS LINE (a fault) where it does not, "" where nothing needs saying.
    static string DevSaid(Verdict verdict, Ground by, string? note) => Decided(verdict, by) is not { } words ? ""
        : note != null && DevPlain(note).Contains(words, StringComparison.Ordinal) ? $"its line says '{words}'" : $"NOT SAID ON ITS LINE: '{words}'";

    // Development (issue #67): the tooltip of every column's name on show, read from its button, with what it says of
    // ranges: a column whose number is judged without a range must have its clause (SimColumn.NoRangeTip), ITS CLAUSE OF NO
    // RANGE IS MISSING otherwise; every other ("Comes from", a rate, a number neither way better) must say nothing of it, A
    // CLAUSE OF NO RANGE WHERE IT DOES NOT BELONG otherwise. Both are faults.
    void DevLogNoRangeTips(string head)
    {
        var tips = _headers.Where(h => GodotObject.IsInstanceValid(h) && !h.IsQueuedForDeletion() && h.HasMeta("column"))
            .Select(h => (Header: h, Column: ColumnOf(h.GetMeta("column").AsString()))).Where(x => x.Column != null)
            .Select(x =>
            {
                bool wants = x.Column!.NoRangeTip != "", says = x.Header.TooltipText.Contains("No range:", StringComparison.Ordinal);
                string verdict = wants ? says ? "its clause of no range" : "ITS CLAUSE OF NO RANGE IS MISSING"
                    : says ? "A CLAUSE OF NO RANGE WHERE IT DOES NOT BELONG" : "nothing of ranges";
                return $"'{x.Header.Text}': {x.Header.TooltipText.Replace("\n", " / ")} ({verdict})";
            }).ToList();
        DevChunks(head + " tooltips of the names", tips.Count > 0 ? tips : new List<string> { "none on show" });
    }
#endif
}
