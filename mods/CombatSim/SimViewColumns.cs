using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MelvorMods.Shared;

namespace CombatSim;

/// <summary>
/// The columns of the simulator's list (issue #19): which numbers it shows and in which order, chosen in a
/// picker that floats next to the window like "Where" and History; the sorting by a column, kept by its key and
/// shown with the game's own arrow; the numbers moving sideways a whole column at a time under the enemy's name
/// when there are more than the window has room for; and "Compared with", which follows the same columns. The
/// window keeps the width it always had, whichever columns it shows.
/// </summary>
sealed partial class SimView
{
    const string NameKey = "name";

    List<SimColumn>? _catalog, _shown;
    Dictionary<string, SimColumn>? _byKey;
    List<SimColumn> Catalog => _catalog ??= SimColumns.All(_state);
    SimColumn? ColumnOf(string key) => (_byKey ??= Catalog.ToDictionary(c => c.Key)).GetValueOrDefault(key);

    Control _resultsColumn = null!, _sidewaysRow = null!;
    HBoxContainer _headerCells = null!;
    HScrollBar? _sideways;
    Button _columnsButton = null!;
    LineEdit? _columnsSearch;
    Action? _syncColumnsPicker;
    // The numbers of every line, moved sideways together with those of the header.
    readonly List<HBoxContainer> _rowCells = new();
    // In pixels of this window: where each shown column starts and how wide it is; all the numbers of a line; the
    // room they have; how far they are moved; and how much of the room the whole columns in sight from there take.
    readonly List<(float Start, float Width)> _spans = new();
    float _cellsWidth, _numbersWidth, _offset, _inSight;

    /// <summary>The columns of the list, from left to right.</summary>
    List<SimColumn> Shown()
    {
        if (_shown != null) return _shown;
        var shown = _state.Columns.Select(ColumnOf).OfType<SimColumn>().ToList();
        // "Best for" ranks the fights by the XP of the skill you train: that column is always there, first when it
        // was not chosen. It is named after the skill, as before.
        if (_state.BestMode && shown.All(c => c.Key != "xp") && ColumnOf("xp") is { } xp) shown.Insert(0, xp);
        if (shown.Count == 0) shown.AddRange(SimColumns.Default.Select(ColumnOf).OfType<SimColumn>());
        return _shown = shown;
    }

    // ---- the width ------------------------------------------------------------------------------------------

    float EnemyPart => _state.PerStance ? 320 : 250;

    /// <summary>
    /// The middle column's width at full size: always the one it has with the seven columns the list starts with,
    /// as before the columns could be chosen, so the window never covers more of the game than it did and never
    /// moves while columns are ticked. Fewer or narrower columns leave the room to the enemy's name; more than fit
    /// move sideways (<see cref="SidewaysBar"/>).
    /// </summary>
    float ResultsWidth()
    {
        _spans.Clear();
        float x = 0, gap = _s.Px(6);
        foreach (SimColumn column in Shown())
        {
            _spans.Add((x, _s.Px(column.Width)));
            x += _s.Px(column.Width) + gap;
        }
        _cellsWidth = Math.Max(0, x - gap);
        // The room is the default columns' own, every width rounded as theirs are: at every size they fit exactly.
        List<SimColumn> defaults = SimColumns.Default.Select(ColumnOf).OfType<SimColumn>().ToList();
        float room = defaults.Sum(c => _s.Px(c.Width)) + gap * Math.Max(0, defaults.Count - 1);
        _numbersWidth = Math.Min(_cellsWidth, Math.Max(room, _spans.Select(s => s.Width).DefaultIfEmpty(0).Max()));
        return EnemyPart + defaults.Sum(c => c.Width + 6) + 30;
    }

    // ---- the header and the lines ---------------------------------------------------------------------------

    /// <summary>
    /// The numbers of a line (or of the header) sit in a strip of their own, as wide as the room they have. Only
    /// whole columns are in sight, at the strip's right end like the numbers that always fitted; the bar under the
    /// list moves them sideways, and the enemy's name stays put.
    /// </summary>
    Control NumbersStrip(out HBoxContainer cells)
    {
        var strip = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(_numbersWidth, 0) };
        var sight = new Control { ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore };
        var box = cells = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 6);
        // As high as the line; placed by hand, the strip is no container.
        foreach (Control part in new Control[] { sight, box })
        {
            part.AnchorTop = 0;
            part.AnchorBottom = 1;
            part.OffsetTop = part.OffsetBottom = 0;
        }
        sight.AddChild(box);
        strip.AddChild(sight);
        Place(box);
        // The strip is as high as its numbers, so the line is too, as when they stood in it themselves.
        box.MinimumSizeChanged += () =>
        {
            if (GodotObject.IsInstanceValid(strip) && GodotObject.IsInstanceValid(box))
                strip.CustomMinimumSize = new Vector2(strip.CustomMinimumSize.X, box.GetCombinedMinimumSize().Y);
        };
        return strip;
    }

    void Place(HBoxContainer cells)
    {
        if (cells.GetParent() is Control sight)
        {
            sight.OffsetLeft = _numbersWidth - _inSight;
            sight.OffsetRight = _numbersWidth;
        }
        cells.OffsetLeft = -_offset;
        cells.OffsetRight = _cellsWidth - _offset;
    }

    void FillHeader()
    {
        foreach (Node old in _headerCells.GetChildren())
        {
            _headerCells.RemoveChild(old);
            old.QueueFree();
        }
        _headers.RemoveAll(h => !GodotObject.IsInstanceValid(h) || h.GetMeta("column").AsString() != NameKey);
        foreach (SimColumn column in Shown())
        {
            Button title = HeaderButton(column.Title, column.Help, column.Width, column.Key);
            // Never wider than its column, so the names stay over their numbers (MakeRoom).
            title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            _headerCells.AddChild(title);
        }
        Control strip = _headerCells.GetParent().GetParent<Control>();
        strip.CustomMinimumSize = new Vector2(_numbersWidth, strip.CustomMinimumSize.Y);
        SizeBar();
        ApplyOffset();
    }

    Control RowCells(SimResult result)
    {
        Control strip = NumbersStrip(out HBoxContainer cells);
        foreach (SimColumn column in Shown())
        {
            Label cell = result.Failed
                ? _s.Text("-", GameStyle.TextDim)
                : _s.Text(column.Text(result), column.Tint(result), 14, GameStyle.SemiBold);
            cell.HorizontalAlignment = HorizontalAlignment.Right;
            cell.CustomMinimumSize = new Vector2(_s.Px(column.Width), 0);
            cell.MouseFilter = Control.MouseFilterEnum.Ignore;
            if (column.Trim)
            {
                cell.ClipText = true;
                cell.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            }
            cells.AddChild(cell);
        }
        _rowCells.Add(cells);
        return strip;
    }

    // ---- sideways -------------------------------------------------------------------------------------------

    /// <summary>Under the numbers, as wide as their strip: there only when they do not all fit.</summary>
    Control SidewaysBar(ScrollContainer rows)
    {
        // The rows' own padding on the left; on the right, theirs and the list's scroll bar, as over the header.
        var margin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        margin.AddThemeConstantOverride("margin_left", _s.Px(8));
        margin.AddThemeConstantOverride("margin_right", _s.Px(8 + 12));
        var line = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        line.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        var bar = _sideways = new HScrollBar { MinValue = 0, Step = 1, FocusMode = Control.FocusModeEnum.None };
        bar.TooltipText = "More numbers than the window has room for: move them sideways. The enemy stays in place.";
        bar.ValueChanged += value =>
        {
            _offset = (float)value;
            ApplyOffset();
        };
#if DEVTOOLS
        _barLook = StyleBar(bar);
#else
        StyleBar(bar);
#endif
        // As thick as the list's own scroll bar, whatever the pieces it was given say.
        ScrollBar upright = rows.GetVScrollBar();
        upright.Resized += () =>
        {
            if (GodotObject.IsInstanceValid(bar) && GodotObject.IsInstanceValid(upright))
                bar.CustomMinimumSize = new Vector2(bar.CustomMinimumSize.X, upright.Size.X);
        };
        line.AddChild(bar);
        margin.AddChild(line);
        _sidewaysRow = margin;
        margin.Visible = false;
        SizeBar();

        // Shift with the wheel, or a sideways swipe, moves them a column at a time too.
        rows.GuiInput += input =>
        {
            if (_cellsWidth <= _numbersWidth || input is not InputEventMouseButton { Pressed: true } wheel) return;
            int by = wheel.ButtonIndex switch
            {
                MouseButton.WheelLeft => -1,
                MouseButton.WheelRight => 1,
                MouseButton.WheelUp when wheel.ShiftPressed => -1,
                MouseButton.WheelDown when wheel.ShiftPressed => 1,
                _ => 0,
            };
            if (by == 0) return;
            _offset = by > 0
                ? _spans.Select(s => s.Start).FirstOrDefault(s => s > _offset + 0.5f, _offset)
                : _spans.Select(s => s.Start).LastOrDefault(s => s < _offset - 0.5f, 0);
            ApplyOffset();
            rows.AcceptEvent();
        };
        return margin;
    }

    void SizeBar()
    {
        if (_sideways is not { } bar) return;
        // From the first column to the furthest the numbers can move, the room in sight being one page. The end
        // first: a page is never longer than the range it is in.
        bar.MaxValue = MostOffset + _numbersWidth;
        bar.Page = _numbersWidth;
        bar.CustomMinimumSize = new Vector2(_numbersWidth, bar.CustomMinimumSize.Y);
    }

    // The game's theme gives the scroll bars of its lists their look. Where it has nothing of its own for a sideways
    // bar, this one takes the upright bar's pieces, turned on their side.
    static string StyleBar(HScrollBar bar)
    {
        if (ThemeDB.GetProjectTheme() is not { } game) return "no theme of the game's";
        var said = new List<string>();
        foreach (string piece in new[] { "scroll", "scroll_focus", "grabber", "grabber_highlight", "grabber_pressed" })
        {
            if (game.HasStylebox(piece, "HScrollBar") && game.GetStylebox(piece, "HScrollBar") is not StyleBoxEmpty)
            {
                said.Add($"{piece} the game's sideways {game.GetStylebox(piece, "HScrollBar").GetType().Name}");
                continue;
            }
            if (!game.HasStylebox(piece, "VScrollBar")) { said.Add($"{piece} none"); continue; }
            StyleBox upright = game.GetStylebox(piece, "VScrollBar");
            StyleBox? turned = Sideways(upright);
            if (turned != null) bar.AddThemeStyleboxOverride(piece, turned);
            said.Add($"{piece} {(turned != null ? "turned" : "not turned")} {upright.GetType().Name} {(upright as StyleBoxTexture)?.Texture?.ResourcePath}");
        }
        return string.Join(", ", said);
    }

    /// <summary>
    /// An upright piece of a scroll bar turned a quarter to the left: what was its top is its left end. Its
    /// picture, margins and corners turn with it. Null when it cannot be turned.
    /// </summary>
    static StyleBox? Sideways(StyleBox box)
    {
        try
        {
            if (box.Duplicate() is not StyleBox turned) return null;
            // Quarter to the left: top -> left, right -> top, bottom -> right, left -> bottom.
            (turned.ContentMarginLeft, turned.ContentMarginTop, turned.ContentMarginRight, turned.ContentMarginBottom) =
                (box.ContentMarginTop, box.ContentMarginRight, box.ContentMarginBottom, box.ContentMarginLeft);
            switch (turned)
            {
                case StyleBoxTexture texture when box is StyleBoxTexture from:
                    (texture.TextureMarginLeft, texture.TextureMarginTop, texture.TextureMarginRight, texture.TextureMarginBottom) =
                        (from.TextureMarginTop, from.TextureMarginRight, from.TextureMarginBottom, from.TextureMarginLeft);
                    (texture.ExpandMarginLeft, texture.ExpandMarginTop, texture.ExpandMarginRight, texture.ExpandMarginBottom) =
                        (from.ExpandMarginTop, from.ExpandMarginRight, from.ExpandMarginBottom, from.ExpandMarginLeft);
                    (texture.AxisStretchHorizontal, texture.AxisStretchVertical) = (from.AxisStretchVertical, from.AxisStretchHorizontal);
                    if (from.Texture?.GetImage() is { } image && !image.IsEmpty())
                    {
                        if (image.IsCompressed()) image.Decompress();
                        int width = image.GetWidth();
                        image.Rotate90(ClockDirection.Counterclockwise);
                        texture.Texture = ImageTexture.CreateFromImage(image);
                        // A part of the picture: the same part, turned. (x, y) goes to (y, width - x).
                        Rect2 region = from.RegionRect;
                        if (region.HasArea())
                            texture.RegionRect = new Rect2(region.Position.Y, width - region.End.X, region.Size.Y, region.Size.X);
                    }
                    break;
                case StyleBoxFlat flat when box is StyleBoxFlat from:
                    (flat.BorderWidthLeft, flat.BorderWidthTop, flat.BorderWidthRight, flat.BorderWidthBottom) =
                        (from.BorderWidthTop, from.BorderWidthRight, from.BorderWidthBottom, from.BorderWidthLeft);
                    (flat.ExpandMarginLeft, flat.ExpandMarginTop, flat.ExpandMarginRight, flat.ExpandMarginBottom) =
                        (from.ExpandMarginTop, from.ExpandMarginRight, from.ExpandMarginBottom, from.ExpandMarginLeft);
                    (flat.CornerRadiusTopLeft, flat.CornerRadiusTopRight, flat.CornerRadiusBottomRight, flat.CornerRadiusBottomLeft) =
                        (from.CornerRadiusTopRight, from.CornerRadiusBottomRight, from.CornerRadiusBottomLeft, from.CornerRadiusTopLeft);
                    break;
                case StyleBoxLine line when box is StyleBoxLine from:
                    line.Vertical = !from.Vertical;
                    break;
            }
            return turned;
        }
        catch { return null; }
    }

    // The furthest the numbers can move: to the first column from which all the rest are in sight.
    float MostOffset => _spans.Select(s => s.Start).Where(start => _cellsWidth - start <= _numbersWidth + 0.5f).DefaultIfEmpty(0).Min();

    void ApplyOffset()
    {
        // A whole column at a time: the start of the column nearest to where they were moved.
        float most = MostOffset, wanted = Math.Clamp(_offset, 0, most);
        _offset = _spans.Select(s => s.Start).Where(start => start <= most + 0.5f).DefaultIfEmpty(0).MinBy(start => Math.Abs(start - wanted));
        // The whole columns from there that fit in the room; one cut at its right edge is left out of sight.
        float end = _spans.Where(InSight).Select(s => s.Start + s.Width).DefaultIfEmpty(_offset + _numbersWidth).Max();
        _inSight = end - _offset;
        _rowCells.RemoveAll(c => !GodotObject.IsInstanceValid(c) || c.IsQueuedForDeletion());
        foreach (HBoxContainer cells in _rowCells) Place(cells);
        Place(_headerCells);
        // A column's name out of sight takes no clicks.
        var titles = _headerCells.GetChildren().OfType<Button>().Where(b => !b.IsQueuedForDeletion()).ToList();
        for (int i = 0; i < titles.Count; i++)
            titles[i].MouseFilter = i < _spans.Count && InSight(_spans[i]) ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore;
        if (_sideways is { } bar && Math.Abs(bar.Value - _offset) > 0.5) bar.SetValueNoSignal(_offset);
    }

    bool InSight((float Start, float Width) span) => span.Start >= _offset - 0.5f && span.Start + span.Width <= _offset + _numbersWidth + 0.5f;

    // ---- sorting --------------------------------------------------------------------------------------------

    /// <summary>
    /// The column the list is sorted by: the chosen one while it is shown (it stays chosen when it is taken out
    /// and comes back with it), XP otherwise, and nothing when XP is not shown either.
    /// </summary>
    string? SortedBy()
    {
        if (_state.SortKey == NameKey || Shown().Any(c => c.Key == _state.SortKey)) return _state.SortKey;
        return Shown().Any(c => c.Key == "xp") ? "xp" : null;
    }

    // Without the chosen column the list goes by XP, best first.
    bool SortedDescending => SortedBy() != _state.SortKey || _state.SortDescending;

    // The first click on a column puts the best fights on top, the next turns the order around.
    void SortBy(string key)
    {
        bool descending = SortedBy() == key ? !SortedDescending : key != NameKey && ColumnOf(key)?.Better != Better.Less;
        _state.SortKey = key;
        _state.SortDescending = descending;
        _tableSignature = "";
        Refresh();
    }

    List<SimResult> Sorted(List<SimResult> results)
    {
        string? key = SortedBy();
        bool descending = SortedDescending;
        if (key == NameKey)
        {
            var byName = results.OrderBy(r => r.Target.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (descending) byName.Reverse();
            return byName;
        }
        Func<SimResult, double?> value = key != null && ColumnOf(key) is { } column ? column.Value : r => _state.Xp(r);
        var simulated = results.Where(r => !r.Failed).Select(r => (Result: r, Value: value(r))).ToList();
        var numbers = simulated.Where(x => x.Value != null);
        // Equal numbers are common (every fight that eats no food): the better experience first among them. A fight
        // that killed nothing is the worst there is for a number per kill (SimColumn): last, or first turned round.
        var ordered = (descending ? numbers.OrderByDescending(x => x.Value) : numbers.OrderBy(x => x.Value))
            .ThenByDescending(x => _state.Xp(x.Result)).Select(x => x.Result).ToList();
        // A number that is missing (not a dungeon, an earlier scan that did not record it), and a fight that could not
        // be simulated at all: last, whichever the direction.
        ordered.AddRange(simulated.Where(x => x.Value == null).OrderByDescending(x => _state.Xp(x.Result)).Select(x => x.Result));
        ordered.AddRange(results.Where(r => r.Failed));
        return ordered;
    }

    void PaintHeaders()
    {
        // The lines made before the last ones (FillRows makes them again whenever the list changes).
        _rowCells.RemoveAll(c => !GodotObject.IsInstanceValid(c) || c.IsQueuedForDeletion());
        string? sorted = SortedBy();
        Texture2D? arrow = Arrow(SortedDescending ? "down" : "up");
        foreach (Button header in _headers)
        {
            if (!GodotObject.IsInstanceValid(header)) continue;
            string key = header.GetMeta("column").AsString();
            // The XP column says whose experience it is.
            if (key == "xp") header.Text = _state.ShownBest && _state.ShownSkillName != "" ? _state.ShownSkillName : "XP";
            Color color = key == sorted ? GameStyle.TextGold : GameStyle.TextGrey;
            foreach (string name in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
                header.AddThemeColorOverride(name, color);
            if (key != NameKey) MakeRoom(header, ColumnOf(key), key == sorted ? arrow : null);
            header.QueueRedraw();   // the arrow
        }
    }

    static readonly string[] TitleStates = { "normal", "hover", "pressed", "hover_pressed", "disabled" };

    /// <summary>
    /// A number's name stands on the right of its column, where the numbers under it end, with the margins of the
    /// game's theme. The name the list is sorted by moves left, as far as its column has room, so that the arrow
    /// after it ends where the numbers end: name and arrow plainly belong to that column, not to the one before.
    /// No margin on the left: the name is right-aligned, and one longer than its column (at the smallest window
    /// sizes) is cut short with "..." and named whole in its tooltip, instead of pushing the names after it out
    /// of line with their numbers.
    /// </summary>
    void MakeRoom(Button title, SimColumn? column, Texture2D? arrow)
    {
        string look = $"{title.Text}|{arrow?.GetWidth()}";
        if (title.HasMeta("room") && title.GetMeta("room").AsString() == look) return;
        title.SetMeta("room", look);
        // A pixel to spare, so a name that just fits is not cut short by a fraction of one.
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
        if (column != null)
            title.TooltipText = TitleCut(title) && !column.Help.StartsWith(column.Name, StringComparison.OrdinalIgnoreCase)
                ? column.Name + "\n" + column.Help : column.Help;
    }

    static float TitleWidth(Button title) =>
        title.GetThemeFont("font").GetStringSize(title.Text, HorizontalAlignment.Left, -1, title.GetThemeFontSize("font_size")).X;

    // The name does not fit between its margins: it is cut short with "...".
    static bool TitleCut(Button title)
    {
        StyleBox box = title.GetThemeStylebox("normal");
        return TitleWidth(title) > title.CustomMinimumSize.X - box.GetMargin(Side.Left) - box.GetMargin(Side.Right) + 0.5f;
    }

    /// <summary>
    /// The direction the list is sorted in, after the name of its column: the game's drop-down arrow, pointing down
    /// for the most first and up for the least first. After the enemy's name, which is on the left of its column;
    /// at the right end of a number's column, where the numbers end, with its name moved left for it (MakeRoom).
    /// Drawn over the button.
    /// </summary>
    void DrawSortArrow(Button header)
    {
        if (!header.HasMeta("column") || header.GetMeta("column").AsString() is not { } key || key != SortedBy()) return;
        if (Arrow(SortedDescending ? "down" : "up") is not { } arrow) return;
        Vector2 at = ArrowAt(header, key, arrow);
        header.DrawTextureRect(arrow, new Rect2(at, arrow.GetSize()), false);
    }

    // Right after the end of the name, with the gap the game's theme puts between a button's words and its picture.
    static Vector2 ArrowAt(Button header, string key, Texture2D arrow)
    {
        StyleBox box = header.GetThemeStylebox("normal");
        float gap = header.GetThemeConstant("h_separation");
        float x = key == NameKey
            ? box.GetMargin(Side.Left) + TitleWidth(header) + gap
            : header.Size.X - box.GetMargin(Side.Right) + gap;
        return new Vector2(MathF.Round(x), MathF.Round((header.Size.Y - arrow.GetHeight()) / 2));
    }

    // The game's drop-down arrow (GameStyle.DropArrow) at this window's size, the way the drop-downs of the window
    // have it (GameStyle.Dropdown): as it is ("down") or turned ("up", "left", "right"). Null when the game has no
    // such arrow any more; the sort is then shown by the gold name alone, and the moves by "<" and ">".
    readonly Dictionary<string, Texture2D?> _arrows = new();

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
                else if (way == "left") image.Rotate90(ClockDirection.Clockwise);
                else if (way == "right") image.Rotate90(ClockDirection.Counterclockwise);
                if (Math.Abs(_s.Scale - 1f) >= 0.01f)
                    image.Resize(_s.Px(image.GetWidth()), _s.Px(image.GetHeight()), Image.Interpolation.Lanczos);
                arrow = ImageTexture.CreateFromImage(image);
            }
        }
        catch { arrow = null; }
        return _arrows[way] = arrow;
    }

    // ---- "Compared with" ------------------------------------------------------------------------------------

    /// <summary>
    /// This setup against another, column by column, by the simulator's one rule (<see cref="Judge"/>): green where
    /// this one is better, red where it is worse, grey with "could be luck" where the two ranges overlap. The columns
    /// in which the two are the same share one line at the end, so a long list stays short to read.
    /// </summary>
    IEnumerable<Control> Compared(SimResult r, SimResult other)
    {
        var same = new List<string>();
        foreach (SimColumn column in Shown())
        {
            // A number one of the two does not have (nothing killed, not a dungeon, not recorded) is left out.
            if (column.Value(r) is not { } mine || column.Value(other) is not { } theirs || !double.IsFinite(mine) || !double.IsFinite(theirs)) continue;
            string name = NameOf(column);
            Verdict verdict = Judge(column, r, other);
            string? luck = verdict == Verdict.Luck ? SimRanges.Words(verdict) : null;
            if (!column.Percent)
            {
                // This setup's own words or number, coloured against the other's.
                yield return Stat(name, column.Text(r), Tint(verdict), luck);
                continue;
            }
            if (verdict == Verdict.Same)
            {
                same.Add(name);
                continue;
            }
            // There is no percent of nothing: this setup's own number against the other's 0.
            string text = theirs == 0 ? column.Text(r) + " vs 0" : Change(mine, theirs);
            yield return Stat(name, text, Tint(verdict), luck);
        }
        if (same.Count > 0) yield return Wrapped("The same: " + string.Join(", ", same), 250, GameStyle.TextGrey, 12);
    }

    /// <summary>
    /// This setup's number in a column against another setup's, by the one rule of <see cref="SimRanges.Judge"/>:
    /// better or worse only when their ranges do not overlap. For "Compared with", and for the setups side by side
    /// of issue #2.
    /// </summary>
    Verdict Judge(SimColumn column, SimResult r, SimResult other) =>
        column.Value(r) is { } mine && column.Value(other) is { } theirs && double.IsFinite(mine) && double.IsFinite(theirs)
            ? SimRanges.Judge(r, other, column.Rate?.Invoke(), mine, theirs, column.Better)
            : Verdict.Differs;

    // Green when better, red when worse; grey when it could be luck; white for the same, or a number neither way better.
    static Color Tint(Verdict verdict) => verdict switch
    {
        Verdict.Better => Good,
        Verdict.Worse => Bad,
        Verdict.Luck => GameStyle.TextGrey,
        _ => GameStyle.TextWhite,
    };

    // In "Best for" the XP column is the trained skill's, and named after it.
    string NameOf(SimColumn column) =>
        column.Key == "xp" && _state.ShownBest && _state.ShownSkillName != "" ? _state.ShownSkillName + " XP" : column.Name;

    // ---- the picker -----------------------------------------------------------------------------------------

    Button ColumnsButton()
    {
        _columnsButton = _s.GameButton("Columns", "TexturedButton4x", 12);
        _columnsButton.TooltipText = "Choose which numbers the list shows, and in which order";
        _columnsButton.Pressed += OpenColumns;
        return _columnsButton;
    }

    void OpenColumns()
    {
        bool wasOpen = _pickerKind == "columns";
        ClosePicker();
        if (wasOpen) return;   // the button is a switch, as History's is
        const float width = 560, height = 520, orderWidth = 200;
        VBoxContainer column = StartPicker("columns", "Columns", width, height);

        // One click puts a set of columns in place of the ticks; they can be changed afterwards.
        var presets = _s.Spaced(new HBoxContainer(), 4);
        foreach (var (name, tip, keys) in SimColumns.Presets)
        {
            Button preset = _s.GameButton(name, "TexturedButton4x", 12);
            preset.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            preset.FocusMode = Control.FocusModeEnum.None;
            preset.TooltipText = tip;
            preset.Pressed += () => SetColumns(PresetKeys(name, keys));
            presets.AddChild(preset);
        }
        column.AddChild(presets);

        var sides = _s.Spaced(new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill }, 12);
        column.AddChild(sides);

        // Left: every number the simulator has, in groups that fold, with a tick each.
        var numbers = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 6);
        var search = _columnsSearch = new LineEdit { PlaceholderText = "Search numbers...", ClearButtonEnabled = true };
        search.AddThemeFontSizeOverride("font_size", _s.Px(14));
        numbers.AddChild(search);
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var list = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 2);
        scroll.AddChild(list);
        numbers.AddChild(scroll);
        Label none = _s.Text("No number matches.", GameStyle.TextGrey, 13);
        none.HorizontalAlignment = HorizontalAlignment.Center;
        none.Visible = false;
        numbers.AddChild(none);
        sides.AddChild(numbers);

        // Right: the columns as the list shows them, from left to right.
        var shownSide = _s.Spaced(new VBoxContainer { CustomMinimumSize = new Vector2(_s.Px(orderWidth), 0) }, 6);
        shownSide.AddChild(Caption("In the list, from left to right"));
        var orderScroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var order = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 2);
        orderScroll.AddChild(order);
        shownSide.AddChild(orderScroll);
        Label bestNote = Wrapped("Best for always shows the XP of the skill you train, first when it is not ticked.", orderWidth, GameStyle.TextDim, 11);
        shownSide.AddChild(bestNote);
        sides.AddChild(shownSide);

        var foot = _s.Spaced(new HBoxContainer(), 8);
        Label summary = _s.Text("", GameStyle.TextGrey, 12);
        summary.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        summary.ClipText = true;
        foot.AddChild(summary);
        Button reset = _s.GameButton("Reset", "TexturedButton4x", 13);
        reset.TooltipText = "Back to the columns the list starts with: Kills, XP, Gold, Food, Food/kill, Deaths, Low HP";
        reset.Pressed += () => SetColumns(SimColumns.Default);
        foot.AddChild(reset);
        Button done = _s.GameButton("Done", "TexturedButton4x", 13);
        done.Pressed += ClosePicker;
        foot.AddChild(done);
        column.AddChild(foot);

        var ticks = new List<(Button Tick, SimColumn Column)>();
        var headings = new List<(Button Heading, string Group)>();
        bool syncing = false;

        void Filter()
        {
            string text = search.Text.Trim();
            foreach (var (tick, c) in ticks)
                tick.Visible = text == "" ? !_state.FoldedColumnGroups.Contains(c.Group)
                    : c.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || c.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                      || c.Group.Contains(text, StringComparison.OrdinalIgnoreCase);
            foreach (var (heading, group) in headings)
            {
                // While searching, every match is in sight, folded or not.
                heading.Visible = text == "" || ticks.Any(t => t.Column.Group == group && t.Tick.Visible);
                heading.Icon = Arrow(text == "" && _state.FoldedColumnGroups.Contains(group) ? "right" : "down");
            }
            none.Visible = text != "" && ticks.All(t => !t.Tick.Visible);
        }

        void FillOrder()
        {
            foreach (Node old in order.GetChildren()) old.QueueFree();
            var keys = _state.Columns.Where(k => ColumnOf(k) != null).ToList();
            for (int i = 0; i < keys.Count; i++)
            {
                SimColumn c = ColumnOf(keys[i])!;
                int at = i;
                var line = _s.Spaced(new HBoxContainer(), 4);
                Label title = _s.Text(c.Title, GameStyle.TextPaper, 13);
                title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                title.ClipText = true;
                title.TooltipText = c.Name;
                title.MouseFilter = Control.MouseFilterEnum.Pass;
                line.AddChild(title);
                line.AddChild(MoveButton("left", "Move it left in the list", at > 0, () => SetColumns(Moved(keys, at, -1))));
                line.AddChild(MoveButton("right", "Move it right in the list", at < keys.Count - 1, () => SetColumns(Moved(keys, at, 1))));
                line.AddChild(SmallButton("X", "TexturedButtonCancel2x", "Take it out of the list", keys.Count > 1, () => SetColumns(keys.Where((_, j) => j != at))));
                order.AddChild(line);
            }
        }

        void Sync()
        {
            syncing = true;
            foreach (var (tick, c) in ticks)
                if (tick.ButtonPressed != _state.Columns.Contains(c.Key)) tick.ButtonPressed = !tick.ButtonPressed;   // its own Toggled swaps the box
            syncing = false;
            FillOrder();
            int count = _state.Columns.Count(k => ColumnOf(k) != null);
            summary.Text = $"{count} of {Catalog.Count} shown" + (_cellsWidth > _numbersWidth ? "  ·  the list scrolls sideways" : "");
            bestNote.Visible = _state.BestMode && !_state.Columns.Contains("xp");
        }

        foreach (string group in SimColumns.Groups)
        {
            var members = Catalog.Where(c => c.Group == group).ToList();
            if (members.Count == 0) continue;
            // The headings of "Where" (OpenAreas), with the game's drop-down arrow, turned when the group is folded.
            var heading = new Button
            {
                Text = group, Flat = true, Alignment = HorizontalAlignment.Left, FocusMode = Control.FocusModeEnum.None,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand, TooltipText = $"Fold or open {group}",
            };
            heading.AddThemeFontSizeOverride("font_size", _s.Px(12));
            if (GameStyle.SemiBold is { } font) heading.AddThemeFontOverride("font", font);
            foreach (string name in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
                heading.AddThemeColorOverride(name, name == "font_hover_color" ? GameStyle.TextWhite : GameStyle.TextGold);
            heading.AddThemeConstantOverride("h_separation", _s.Px(6));
            heading.Pressed += () =>
            {
                if (!_state.FoldedColumnGroups.Remove(group)) _state.FoldedColumnGroups.Add(group);
                Filter();
            };
            list.AddChild(heading);
            headings.Add((heading, group));

            foreach (SimColumn c in members)
            {
                Button tick = _s.TickBox(c.Name, _state.Columns.Contains(c.Key));
                tick.TooltipText = c.About;
                tick.Toggled += on =>
                {
                    if (syncing) return;
                    SetColumns(on ? _state.Columns.Append(c.Key) : _state.Columns.Where(k => k != c.Key));
                };
                list.AddChild(tick);
                ticks.Add((tick, c));
            }
        }
        search.TextChanged += _ => Filter();
        _syncColumnsPicker = Sync;
#if DEVTOOLS
        _devPicker = () => new[]
        {
            $"{headings.Count(h => h.Heading.Visible)} groups in sight ({string.Join(", ", headings.Where(h => h.Heading.Visible).Select(h => h.Group + (_state.FoldedColumnGroups.Contains(h.Group) ? " folded" : "")))})",
            $"{ticks.Count(t => t.Tick.Visible)} of {ticks.Count} numbers in sight, {ticks.Count(t => t.Tick.ButtonPressed)} ticked; search '{search.Text}'; " +
            $"\"{summary.Text}\"; panel {_picker?.GetGlobalRect()}",
            "order: " + string.Join(" | ", order.GetChildren().Where(n => !n.IsQueuedForDeletion()).Select(n => n.GetChild<Label>(0).Text)),
            $"arrows: fold {DevArrow("down")} and {DevArrow("right")}, moves {DevArrow("left")} and {DevArrow("right")}, " +
            $"first move button '{order.GetChildren().Where(n => !n.IsQueuedForDeletion()).Select(n => n.GetChild<Button>(1)).FirstOrDefault()?.Icon?.GetSize()}'",
        };
#endif

        Sync();
        Filter();
        PlacePicker(_columnsButton, width, height, below: true);
        search.GrabFocus();
    }

    Button SmallButton(string text, string look, string tip, bool enabled, Action pressed)
    {
        Button button = _s.GameButton(text, look, 12);
        button.CustomMinimumSize = new Vector2(_s.Px(24), _s.Px(24));
        button.FocusMode = Control.FocusModeEnum.None;
        button.TooltipText = tip;
        button.Disabled = !enabled;
        button.Pressed += pressed;
        return button;
    }

    // The game's arrow, turned the way the column moves; "<" or ">" only when the game has no arrow.
    Button MoveButton(string way, string tip, bool enabled, Action pressed)
    {
        Texture2D? arrow = Arrow(way);
        Button button = SmallButton(arrow != null ? "" : way == "left" ? "<" : ">", "TexturedButton4x", tip, enabled, pressed);
        button.Icon = arrow;
        button.IconAlignment = HorizontalAlignment.Center;
        return button;
    }

    static List<string> Moved(List<string> keys, int at, int by)
    {
        var moved = keys.ToList();
        (moved[at], moved[at + by]) = (moved[at + by], moved[at]);
        return moved;
    }

    // Training adds the XP of each combat skill that gained any in the list on show; in "Best for" not the skill
    // that is trained, whose XP the XP column already is, under the skill's name.
    List<string> PresetKeys(string name, string[] keys)
    {
        var chosen = keys.ToList();
        if (name != SimColumns.TrainingPreset) return chosen;
        var gained = _state.Lines().Where(r => !r.Failed).SelectMany(r => r.Xp.Where(x => x.Value > 0).Select(x => x.Key)).ToHashSet();
        if (_state.ShownBest) gained.Remove(_state.ShownSkillId);
        chosen.AddRange(SimEngine.CombatSkills.Where(gained.Contains).Select(id => SimColumns.SkillPrefix + id));
        return chosen;
    }

    /// <summary>Shows these columns, in this order: at least one, and only those this version knows.</summary>
    void SetColumns(IEnumerable<string> keys)
    {
        List<string> chosen = keys.Where(k => ColumnOf(k) != null).Distinct().ToList();
        if (chosen.Count > 0 && !chosen.SequenceEqual(_state.Columns))
        {
            _state.Columns = chosen;
            _changed();
            ApplyColumns();
        }
        // Also when nothing changed: the last tick that was taken off goes back on.
        if (_pickerKind == "columns") _syncColumnsPicker?.Invoke();
    }

    // Only the list is made again. The window keeps its width (ResultsWidth), so it neither grows nor moves.
    void ApplyColumns()
    {
        _shown = null;
        ResultsWidth();
        FillHeader();
        _tableSignature = _detailsSignature = "";
        Refresh();
#if DEVTOOLS
        _window.Root.GetTree().CreateTimer(0.1).Timeout += () => DevLogColumns("the columns changed");
#endif
    }

#if DEVTOOLS
    /// <summary>Development: the mod's log, for the lines below. Set by CombatSimMod.</summary>
    internal Action<string>? DevLog;
    Func<string[]>? _devPicker;
    string _barLook = "";

    // Development, in the picker field of sim.request (pickers joined by "+"):
    //   columns               opens the Columns picker, as its button does
    //   columns=<set>         shows a preset by name ("money"), "all", or column keys joined by "," ("kills,safe,maxHit")
    //   find=<text>           types into the open picker's search box
    //   fold=<group>          folds or opens a group of the open picker, as a click on its name does, and back 3 s later
    //   hscroll=<pixels>|end  moves the numbers sideways, to the column nearest to that
    // Each logs, a moment later, what the list and the picker really show, read back from them, and the window's size.
    bool DevColumns(string request)
    {
        if (request == "columns")
        {
            if (_pickerKind != "columns") OpenColumns();
        }
        else if (request.StartsWith("columns=", StringComparison.Ordinal))
        {
            string set = request[8..].Trim();
            var preset = SimColumns.Presets.FirstOrDefault(p => p.Name.Equals(set, StringComparison.OrdinalIgnoreCase));
            List<string> keys = set.Equals("all", StringComparison.OrdinalIgnoreCase) ? Catalog.Select(c => c.Key).ToList()
                : preset.Name != null ? PresetKeys(preset.Name, preset.Keys)
                : set.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var unknown = keys.Where(k => ColumnOf(k) == null).ToList();
            if (unknown.Count > 0) DevChunks("Dev: columns: there is no column " + string.Join(", ", unknown) + "; the keys are", Catalog.Select(c => c.Key));
            SetColumns(keys);
        }
        else if (request.StartsWith("find=", StringComparison.Ordinal) || request.StartsWith("fold=", StringComparison.Ordinal))
        {
            if (_pickerKind != "columns" || _columnsSearch is not { } search || !GodotObject.IsInstanceValid(search))
            {
                DevLog?.Invoke($"Dev: columns: {request[..5]} needs the Columns picker open (\"columns+{request}\").");
                return true;
            }
            if (request.StartsWith("find=", StringComparison.Ordinal))
            {
                search.Text = request[5..];
                search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text);
            }
            else if (_picker?.FindChildren("*", "Button", true, false).OfType<Button>().FirstOrDefault(b => b.Flat && b.Text.Equals(request[5..], StringComparison.OrdinalIgnoreCase)) is { } heading)
            {
                string group = heading.Text;
                heading.EmitSignal(BaseButton.SignalName.Pressed);
                // Back as it was once the picture is taken (1.5 s after the request): the request leaves the picker as it found it.
                _window.Root.GetTree().CreateTimer(3).Timeout += () =>
                {
                    if (GodotObject.IsInstanceValid(heading)) heading.EmitSignal(BaseButton.SignalName.Pressed);
                    else if (!_state.FoldedColumnGroups.Remove(group)) _state.FoldedColumnGroups.Add(group);
                };
            }
            else DevLog?.Invoke($"Dev: columns: no group {request[5..]} in the picker; the groups are {string.Join(", ", SimColumns.Groups)}");
        }
        else if (request.StartsWith("hscroll=", StringComparison.Ordinal))
        {
            string to = request[8..].Trim();
            _offset = to == "end" ? _cellsWidth : float.TryParse(to, out float px) ? px : 0;
            ApplyOffset();
        }
        else return false;
        _window.Root.GetTree().CreateTimer(0.3).Timeout += () => DevLogColumns(request);
        return true;
    }

    // The log keeps lines short: a long list goes over several, each numbered.
    void DevChunks(string head, IEnumerable<string> items)
    {
        string line = "";
        int part = 1;
        foreach (string item in items)
        {
            if (line != "" && head.Length + line.Length + item.Length > 320)
            {
                DevLog?.Invoke($"{head} ({part++}): {line}");
                line = "";
            }
            line += (line == "" ? "" : " | ") + item;
        }
        if (line != "") DevLog?.Invoke($"{head} ({part}): {line}");
    }

    string DevArrow(string way) => Arrow(way) is { } arrow ? $"{way} {arrow.GetWidth()}x{arrow.GetHeight()}" : $"{way} none";

    void DevLogColumns(string why)
    {
        if (!_window.IsAlive || !GodotObject.IsInstanceValid(_headerCells)) return;
        string head = $"Dev: columns ({why})";
        var titles = _headerCells.GetChildren().OfType<Button>().Where(b => !b.IsQueuedForDeletion()).ToList();
        string? sorted = SortedBy();
        Button? sortedTitle = _headers.FirstOrDefault(h => GodotObject.IsInstanceValid(h) && h.GetMeta("column").AsString() == sorted);
        Texture2D? arrow = Arrow(SortedDescending ? "down" : "up");
        DevLog?.Invoke($"{head}: {_state.Columns.Count} chosen, {titles.Count} in the header, {titles.Count(t => t.MouseFilter == Control.MouseFilterEnum.Stop)} in sight; " +
                       $"sorted by {sorted ?? "XP (not shown)"}, {(SortedDescending ? "most" : "least")} first; " +
                       $"numbers {_cellsWidth:0} px in {_numbersWidth:0} px of room, moved {_offset:0} of {MostOffset:0}, {_inSight:0} px in sight");
        if (sortedTitle == null || arrow == null) DevLog?.Invoke($"{head} arrow: none");
        else
        {
            // Where the arrow is against its own name and its own column: after the name, inside the column.
            Vector2 at = ArrowAt(sortedTitle, sorted!, arrow);
            StyleBox box = sortedTitle.GetThemeStylebox("normal");
            float nameEnd = sorted == NameKey ? box.GetMargin(Side.Left) + TitleWidth(sortedTitle) : sortedTitle.Size.X - box.GetMargin(Side.Right);
            float past = at.X + arrow.GetWidth() - sortedTitle.Size.X;
            DevLog?.Invoke($"{head} arrow: {(SortedDescending ? "down" : "up")} {arrow.GetWidth()}x{arrow.GetHeight()} at {at} of '{sortedTitle.Text}' {sortedTitle.Size.X:0}x{sortedTitle.Size.Y:0}; " +
                           $"the name ends at {nameEnd:0}, {at.X - nameEnd:0} px before the arrow; " +
                           (sorted == NameKey ? "the enemy's column" : past <= 0.5f ? $"the arrow ends {-past:0} px inside its column, where the numbers end" : $"the arrow reaches {past:0} px past its column"));
        }
        DevChunks(head + " header, [out of sight]", titles.Select(b => b.MouseFilter == Control.MouseFilterEnum.Stop ? b.Text : $"[{b.Text}]"));
        if (_rowCells.FirstOrDefault(c => GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion()) is { } cells)
            DevChunks(head + " first line", cells.GetChildren().OfType<Label>().Select(l => l.Text + (l.GetCombinedMinimumSize().X > l.CustomMinimumSize.X + 0.5f ? " (wider than its column)" : "")));
        // A name never pushes the numbers out of line any more (MakeRoom); one that does not fit is cut short.
        var wide = titles.Where(b => b.GetCombinedMinimumSize().X > b.CustomMinimumSize.X + 0.5f)
            .Select(b => $"{b.Text} needs {b.GetCombinedMinimumSize().X:0} px of {b.CustomMinimumSize.X:0}").ToList();
        DevChunks(head + " titles wider than their column", wide.Count == 0 ? new List<string> { "none" } : wide);
        var cut = titles.Where(TitleCut).Select(b =>
        {
            StyleBox box = b.GetThemeStylebox("normal");
            return $"{b.Text} ({TitleWidth(b):0} px of words in {b.CustomMinimumSize.X - box.GetMargin(Side.Left) - box.GetMargin(Side.Right):0})";
        }).ToList();
        DevChunks(head + " titles cut short with ...", cut.Count == 0 ? new List<string> { "none" } : cut);
        var lines = _rows.GetChildren().Where(n => !n.IsQueuedForDeletion()).ToList();
        DevLog?.Invoke($"{head}: {lines.Count} lines; " +
                       $"bar {(_sidewaysRow.Visible && _sideways != null ? $"shown {_sideways.Size.X:0}x{_sideways.Size.Y:0} at {_sideways.GetGlobalRect().Position}, value {_sideways.Value:0} of {_sideways.MaxValue - _sideways.Page:0}" : "hidden")}");
        DevChunks(head + " bar pieces", _barLook.Split(", "));
        float screen = _window.Root.GetViewportRect().Size.X;
        Rect2 window = _window.Root.GetGlobalRect();
        DevLog?.Invoke($"{head}: window {window.Size.X:0}x{window.Size.Y:0} at size {_s.Scale:0.##}, from {window.Position.X:0} to {window.End.X:0} of a screen {screen:0} wide, " +
                       $"{window.Size.X / _s.Scale:0} at full size; middle column {_resultsColumn.Size.X:0}");
        DevLogThreeColumns(head);
        if (_pickerKind == "columns" && _devPicker != null)
            foreach (string part in _devPicker()) DevChunks(head + " picker", part.Split(" | "));
    }

    /// <summary>
    /// Development: what each of the window's three columns needs, at this size and at full size, so the window's
    /// width and height can be traced to the column that sets them; and the lines of the Setup column whose buttons
    /// and drop-downs keep the margins of the game's theme (top and bottom) at every size, as do the theme's lines.
    /// </summary>
    void DevLogThreeColumns(string head)
    {
        if (_resultsColumn.GetParent() is not Control three) return;
        string[] names = { "Setup", "Results", "Details" };
        var needs = three.GetChildren().OfType<Control>().Select((c, i) =>
        {
            Vector2 need = c.GetCombinedMinimumSize();
            return $"{(i < names.Length ? names[i] : "?")} {need.X:0}x{need.Y:0} ({need.X / _s.Scale:0}x{need.Y / _s.Scale:0} at full size)";
        });
        DevLog?.Invoke($"{head}: the columns need {string.Join(", ", needs)}; they are {three.Size.Y:0} high, at least {three.CustomMinimumSize.Y:0}");
        if (three.GetChildCount() == 0 || three.GetChild(0) is not Control setup || setup.GetChildCount() < 2
            || setup.GetChild(1) is not PanelContainer panel || panel.GetChildCount() == 0 || panel.GetChild(0) is not VBoxContainer body)
        {
            DevLog?.Invoke($"{head}: the Setup column's lines were not found");
            return;
        }
        var lines = new List<string>();
        float kept = 0;
        foreach (Control line in body.GetChildren().OfType<Control>().Where(c => c.IsVisibleInTree()))
        {
            float margins = 0;
            string name = "";
            foreach (Control c in line.FindChildren("*", "Control", true, false).OfType<Control>().Prepend(line).Where(c => c.IsVisibleInTree()))
            {
                // Buttons with words: the gear's slots are drawn by the mod at the window's size.
                if (c is Button { Text.Length: > 0 } button)
                {
                    StyleBox box = button.GetThemeStylebox("normal");
                    margins = Math.Max(margins, box.GetMargin(Side.Top) + box.GetMargin(Side.Bottom));
                    if (name == "") name = button.Text;
                }
                else if (c is HSeparator separator)
                {
                    margins = Math.Max(margins, separator.GetThemeConstant("separation"));
                    if (name == "") name = "the line of a section";
                }
            }
            if (margins <= 0) continue;
            kept += margins;
            lines.Add($"{name} {margins:0}");
        }
        lines.Add($"together {kept:0} px, as {kept / _s.Scale:0} at full size");
        DevChunks(head + " Setup lines with margins of the game's theme", lines);
    }
#endif
}
