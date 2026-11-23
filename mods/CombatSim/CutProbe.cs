#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Combat;
using Godot;
using MelvorMods.Shared;

namespace CombatSim;

/// <summary>
/// Development (issue #59): every label, button with words and drop-down of the simulator's window, and of a panel open
/// beside it, against the room it has, so that a word cut at some size of the window is found by a log line and not in a
/// picture. Lines of sim.request (CombatSimMod.DevCutsCommand):
/// <list type="bullet">
/// <item>cuts=log: the window measured by parts, each with how many things it measured (SimView.DevMeasureCuts), and one
/// summary line with the tab, what is on show, the size, the counts by part and the verdict of "Dev: window"; two drawn
/// frames after the line, so that what the lines before it put on show is laid out.</item>
/// <item>cuts=every: the window built at every size from the smallest to the largest this screen allows, in the corner's
/// steps of 5%, with cuts=log and cuts=widest after each, as size=every does with size=log.</item>
/// <item>cuts=widest: the widest words the two lines of the issue can get. Every "Found in" name of the game's data, from
/// Item Sources' index the search has built (read once, with how long it took), against a row's room at this size; and
/// the line above the list with the setup's name stood in by one as wide as the line, in the simulator's words only,
/// measured two drawn frames later and made again from the game's names.</item>
/// <item>cuts=long, cuts=long=nopicture, cuts=long=off: the longest words the simulator's own lines can show, left on show
/// for cuts=log and shot= until cuts=long=off or the window is built again (SimView.DevLongOn): the setup's name in the
/// line above the list and in the earlier scans under an empty list as wide as their room, those scans' titles with the
/// game's two widest area names and their Top line with its widest enemy or dungeon name, and Fight with the widest name
/// of the game, or with "nopicture" the widest that shows without its picture at this size. In the simulator's words
/// only; cuts=long=off says whether the window grew meanwhile.</item>
/// <item>cuts=index: Item Sources' index read as Find upgrades' panel reads it the first time it opens, so that cuts=widest
/// measures the "Found in" names without a search; the next lines wait until it is read (15 s at most).</item>
/// <item>cuts=view=best, cuts=view=compare, cuts=view=fights: the newest earlier scan of that kind in History, shown as a
/// click on its row shows it; when there is none, one line says so and the lines after it, up to the next cuts=view=,
/// are left out. The request's put back shows the list it found again.</item>
/// <item>details=top, details=found, details=end: Details scrolled to its top, to its "Found in" heading, or to its end,
/// two drawn frames after the line.</item>
/// </list>
/// Words in capitals, for the orchestrator's search: CUT (a line wider than its room; for a label that wraps, a word
/// wider than its room; a line hidden by the label's height), SHORTENED WITH ... (a label that trims, the house way inside
/// the list only), PAST THE EDGE (it ends past the right edge of its part, a cell of the list past its own cell). Counts
/// of zero are written in words.
/// </summary>
public partial class CombatSimMod
{
    // A cuts= or details= line under way: it runs once the window has been drawn twice since the line and what it waits for
    // is ready (3 s at most, or as long as the line gives). The request waits meanwhile (DevRequests).
    (long Frame, long Since, string What, Action Then, Func<bool>? Ready, long Most)? _devCutsAfter;

    void DevCutsAfter(string what, Action then, Func<bool>? ready = null, long most = 3000) =>
        _devCutsAfter = ((long)Engine.GetFramesDrawn(), (long)Time.GetTicksMsec(), what, then, ready, most);

    bool DevCutsWaiting()
    {
        if (_devCutsAfter is not { } after) return false;
        bool drawn = (long)Engine.GetFramesDrawn() >= after.Frame + 2 && (after.Ready?.Invoke() ?? true);
        if (!drawn && (long)Time.GetTicksMsec() < after.Since + after.Most) return true;
        _devCutsAfter = null;
        if (!drawn)
            _ctx.Log(after.Ready == null ? $"Dev: cuts: {after.What}: the window was not drawn twice in 3 s; measured as it is laid out."
                : $"Dev: cuts: {after.What}: what it waits for was not ready in {after.Most / 1000} s; done as it is.");
        try { after.Then(); }
        catch (Exception e) { _ctx.Log($"Dev: cuts: {after.What} could not be done: {e.Message}"); }
        return _devCutsAfter != null;
    }

    // The cuts= and details= lines. False for a line that is none of them.
    bool DevCutsCommand(string command)
    {
        if (command.StartsWith("details=", StringComparison.Ordinal))
        {
            string where = command[8..].Trim();
            DevCutsAfter(command, () => _ctx.Log("Dev: cuts: " + (_view?.DevScrollDetails(where) ?? "no window")));
            return true;
        }
        if (!command.StartsWith("cuts=", StringComparison.Ordinal)) return false;
        string what = command[5..].Trim();
        if (_window is not { IsAlive: true, Visible: true, Minimised: false } || _view is not { } view)
        {
            _ctx.Log($"Dev: cuts: {command}: the simulator's window is not open (closed, folded or not made yet); nothing was measured.");
            return true;
        }
        switch (what)
        {
            case "log":
                DevCutsAfter(command, () =>
                {
                    if (_window is { IsAlive: true, Visible: true } && _view != null) _view.DevMeasureCuts(DevLogWindow("cuts=log"));
                });
                break;
            case "every":
            {
                // In front of the lines still to come.
                float largest = LargestScale(FooterHeight());
                var rest = _devCommands.ToList();
                _devCommands.Clear();
                for (int step = (int)MathF.Round(GameWindow.MinScale * 20); step <= (int)MathF.Round(largest * 20); step++)
                {
                    _devCommands.Enqueue($"scale={step / 20f:0.00}".Replace(',', '.'));
                    _devCommands.Enqueue("cuts=log");
                    _devCommands.Enqueue("cuts=widest");
                }
                foreach (string line in rest) _devCommands.Enqueue(line);
                _ctx.Log($"Dev: cuts=every: the window measured at every size from {GameWindow.MinScale:0.##} to {largest:0.##}, the largest on this screen, " +
                         "with the widest words at each (cuts=widest).");
                break;
            }
            case "widest":
            {
                DevWidestFoundIn(view);
                if (DevGameNames() is { } names) view.DevLogWidestNames(names.Fights, names.Areas, names.Read);
                else _ctx.Log("Dev: cuts: widest names: the game's combat areas could not be read; nothing measured.");
                var (stood, said) = view.DevStandInStart();
                _ctx.Log("Dev: cuts: widest above the list: " + said);
                if (stood) DevCutsAfter(command, () => _ctx.Log("Dev: cuts: widest above the list: " + (_view?.DevStandInEnd() ?? "the window was built again: nothing to measure or put back")));
                break;
            }
            case "close":
                _ctx.Log("Dev: cuts: " + view.DevClosePanel());
                break;
            case "long" or "long=nopicture":
            {
                var names = DevGameNames();
                _ctx.Log($"Dev: cuts: {what}: " + view.DevLongOn(what == "long=nopicture", names?.Fights, names?.Areas));
                break;
            }
            case "long=off":
                _ctx.Log("Dev: cuts: long=off: " + view.DevLongOff());
                break;
            case "index":
            {
                // Read as Find upgrades' panel reads it the first time it opens, beside the game; read only.
                bool before = _itemIndex != null;
                var clock = Stopwatch.StartNew();
                ReadItemSources();
                DevCutsAfter(command, () => _ctx.Log(_itemIndex == null
                        ? "Dev: cuts: index: Item Sources' index could not be read (see the line before); nothing for cuts=widest's 'Found in'."
                        : before ? "Dev: cuts: index: Item Sources' index was read already, as Find upgrades read it."
                        : $"Dev: cuts: index: Item Sources' index read as Find upgrades' panel reads it, beside the game, ready after {clock.ElapsedMilliseconds} ms; " +
                          "cuts=widest measures its 'Found in' names. Nothing of the player's is touched: it is the mod's own reading."),
                    () => _itemIndex != null || _itemIndexFailed, 15000);
                break;
            }
            default:
                if (what.StartsWith("view=", StringComparison.Ordinal))
                {
                    string kind = what[5..].Trim();
                    if (view.DevShowNewest(kind)) break;
                    // Its measures would be of another list: left out, up to the next scan to show.
                    int left = 0;
                    while (_devCommands.TryPeek(out string? next) && !next.StartsWith("cuts=view=", StringComparison.Ordinal)) { _devCommands.Dequeue(); left++; }
                    _ctx.Log($"Dev: cuts: {command}: the {left} lines after it, up to the next cuts=view= or the end, are left out.");
                    break;
                }
                _ctx.Log($"Dev: cuts: {command}: the lines are cuts=log, cuts=every, cuts=widest, cuts=close, cuts=long, cuts=long=nopicture, cuts=long=off, " +
                         "cuts=index, cuts=view=best|compare|fights and details=top|found|end.");
                break;
        }
        return true;
    }

    // Every name a "Found in" row can show: Item Sources' words (ItemLines.Describe) for every place of every item of the
    // game's data, from the index Find upgrades has read (ReadItemSources), measured against a row on show (SimView.
    // DevLogWidestFoundIn). Read once an index, then measured again at each size. Read only.
    (ItemIndex Index, List<ItemLine> Lines, string Read)? _devFoundInNames;

    void DevWidestFoundIn(SimView view)
    {
        if (_itemIndex is not { } index || _itemLines is not { } names)
        {
            _ctx.Log("Dev: cuts: widest Found in: Item Sources' index is not read yet (Find upgrades' panel reads it the first time it opens); nothing measured.");
            return;
        }
        if (_devFoundInNames is not { } known || !ReferenceEquals(known.Index, index))
        {
            var clock = Stopwatch.StartNew();
            var lines = new List<ItemLine>();
            int items = 0, places = 0, failed = 0;
            // A heading of one item's "Found in" with two rows of one name: two item upgrades that make the same item read
            // alike now that an upgrade is called by what it makes (review of turn 2).
            var twice = new List<string>();
            int headings = 0;
            foreach (var (item, entry) in index.Entries)
            {
                if (entry.Sources.Count == 0) continue;
                items++;
                var own = new List<ItemLine>();
                foreach (ItemRef place in entry.Sources)
                {
                    places++;
                    try { own.Add(names.Describe(place)); }
                    catch { failed++; }
                }
                lines.AddRange(own);
                foreach (var heading in own.GroupBy(l => l.Group))
                {
                    headings++;
                    foreach (var same in heading.GroupBy(l => l.Name).Where(g => g.Count() > 1))
                        twice.Add($"'{same.Key}' {same.Count()} times under '{heading.Key}' in the 'Found in' of {DevItemName(item)} ({string.Join(", ", same.Select(l => l.Ref.Owner.GetType().Name).Distinct())})");
                }
            }
            var distinct = lines.DistinctBy(l => (l.Group, l.Name)).ToList();
            clock.Stop();
            known = (index, distinct, $"{distinct.Count} names of {places} places of {items} items{(failed > 0 ? $" ({failed} could not be named)" : "")}, " +
                                      $"read from the index in {clock.ElapsedMilliseconds} ms");
            _devFoundInNames = known;
            // A name of one word with capitals inside it ("StuddedElkLeatherBootsU") is an id, not words a player reads
            // (review of check-1: the game's item upgrades, named now by what they make, ItemLines.NameOf).
            var ids = distinct.Where(l => DevLikeAnId(l.Name)).ToList();
            _ctx.Log("Dev: cuts: widest Found in: names that read like an id: " + (ids.Count == 0 ? "none"
                : $"{ids.Count} READ LIKE AN ID: " + string.Join(", ", ids.Take(12).Select(l => $"'{l.Name}' ({l.Ref.Owner.GetType().Name} under '{l.Group}')"))));
            _ctx.Log($"Dev: cuts: widest Found in: headings with two rows of one name, of {headings} headings of {items} items: " + (twice.Count == 0 ? "none"
                : $"{twice.Count} WITH TWO ROWS OF ONE NAME: " + string.Join("; ", twice.Take(12))));
        }
        view.DevLogWidestFoundIn(known.Lines, known.Read);
    }

    static bool DevLikeAnId(string name) => Regex.IsMatch(name, @"^[A-Za-z0-9]*[a-z][A-Z][A-Za-z0-9]*$");

    static string DevItemName(Item item)
    {
        try { return item.DisplayName.GetPluralText(1); }
        catch { return item.Id; }
    }

    // Every name a line of the list, Fight and Where can show, from the game's own combat areas, every one of them,
    // locked or not: the enemies' and the dungeons' names, and the areas'. Read once a character; read only.
    (object Combat, List<string> Fights, List<string> Areas, string Read)? _devGameNames;

    (List<string> Fights, List<string> Areas, string Read)? DevGameNames()
    {
        if (_combat is not { } combat) return null;
        if (_devGameNames is not { } known || !ReferenceEquals(known.Combat, combat))
        {
            var clock = Stopwatch.StartNew();
            var fights = new HashSet<string>();
            var areas = new HashSet<string>();
            try
            {
                foreach (ActiveCombatAreaCategory category in combat.Areas.ActiveCategories.Values)
                    foreach (ActiveCombatArea area in category.Areas)
                    {
                        areas.Add(area.DisplayName.Text);
                        if (area.Area is CombatArea open) foreach (Enemy enemy in open.Enemies) fights.Add(enemy.DisplayName.Text);
                        else if (area.Area is Dungeon) fights.Add(area.DisplayName.Text);
                    }
            }
            catch (Exception e)
            {
                _ctx.Log("Dev: cuts: widest names: the game's combat areas could not be read: " + e.Message);
                return null;
            }
            known = (combat, fights.ToList(), areas.ToList(), $"{fights.Count} enemy and dungeon names and {areas.Count} area names of the game, read in {clock.ElapsedMilliseconds} ms");
            _devGameNames = known;
        }
        return (known.Fights, known.Areas, known.Read);
    }
}

sealed partial class SimView
{
    const string DevShortened = "SHORTENED WITH ...";

    // cuts=widest: a setup's name as wide as the line above the list stands in for the real one there (ShowWords), in the
    // simulator's words only; null otherwise. The line's height before it, to say how much higher it made the line.
    (string Real, string Name)? _devStandIn;
    float _devStandInHigh;
    // Every "Found in" row made: its line, the label of its name, and the label of its place when it has a dot.
    readonly List<(ItemLine Line, Label Name, Label? Place)> _devSourceRows = new();

    // What one part of the window measured.
    sealed class DevTally
    {
        public int Measured, Cut, Past, Shortened;
        public readonly List<string> NotWhole = new();
    }

    /// <summary>Development: the part of the screen where Details is drawn, for the picture's read back; null when it is not on show.</summary>
    public Rect2? DevDetailsRect => _details.GetParent() is Control scroll && scroll.IsVisibleInTree() ? scroll.GetGlobalRect() : null;

    // Every label and button with words under this node that is on show, the node too.
    static IEnumerable<Control> DevWordsUnder(Node node, ICollection<Node>? skip = null)
    {
        if (skip != null && skip.Contains(node)) yield break;
        if (node is Control c && !c.IsQueuedForDeletion() && c.IsVisibleInTree() && (c is Label { Text.Length: > 0 } || c is Button { Text.Length: > 0 })) yield return c;
        foreach (Node child in node.GetChildren())
            foreach (Control inner in DevWordsUnder(child, skip))
                yield return inner;
    }

    // One label, button with words or drop-down on show against the room it has, by the measures there are: DevFit for a
    // label; AroundText and ChoiceWidth for a drop-down, as FitBeside and DevSpellWords measure; for a button its margins
    // (Margins) and ChoiceWidth; a column's name over the list as "Dev: columns ... titles cut short with ..." tells it
    // (TitleCut). Whole, CUT, or SHORTENED WITH ... where it trims, with whether the tooltip a player sees there holds every
    // word of it; and PAST THE EDGE where it ends past its part's right edge (edge), or a cell of the list past its own cell.
    void DevWords(Control c, float edge, DevTally tally)
    {
        bool cell = c.GetParent() is HBoxContainer box && (box == _headerCells || _rowCells.Contains(box));
        string verdict, said;
        string? whole = null;
        switch (c)
        {
            case Label label:
            {
                var (fits, fit) = DevFit(label);
                bool trims = label.TextOverrunBehavior != TextServer.OverrunBehavior.NoTrimming && label.AutowrapMode == TextServer.AutowrapMode.Off;
                verdict = fits ? "whole" : trims && label.GetVisibleLineCount() >= label.GetLineCount() ? DevShortened : "CUT";
                said = fit[..fit.LastIndexOf(':')];
                break;
            }
            case OptionButton drop:
            {
                float room = drop.Size.X - AroundText(drop), wide = ChoiceWidth(drop, drop.Text, drop.Icon != null);
                verdict = wide <= room + 0.01f ? "whole" : "CUT";
                said = $"the drop-down's '{drop.Text}' {wide:0.#}{(drop.Icon != null ? " with its picture" : "")} wide in {room:0.#}";
                break;
            }
            case Button title when cell:
            {
                StyleBox look = title.GetThemeStylebox("normal");
                float room = title.CustomMinimumSize.X - look.GetMargin(Side.Left) - look.GetMargin(Side.Right);
                verdict = !TitleCut(title) ? "whole" : title.TextOverrunBehavior != TextServer.OverrunBehavior.NoTrimming ? DevShortened : "CUT";
                said = $"the column's name '{title.Text}' {TitleWidth(title):0.#} wide in {room:0.#}";
                whole = title.HasMeta("column") ? ColumnOf(title.GetMeta("column").AsString())?.Name : null;
                break;
            }
            case Button button:
            {
                float room = button.Size.X - Margins(button), wide = ChoiceWidth(button, button.Text, button.Icon != null);
                verdict = wide <= room + 0.5f ? "whole" : button.TextOverrunBehavior != TextServer.OverrunBehavior.NoTrimming ? DevShortened : "CUT";
                said = $"the button '{button.Text}' {wide:0.#}{(button.Icon != null ? " with its picture" : "")} wide in {room:0.#}";
                break;
            }
            default:
                return;
        }
        tally.Measured++;
        float end = c.GetGlobalRect().End.X;
        bool past = cell ? c.Size.X > c.CustomMinimumSize.X + 0.5f : end > edge + 0.5f;
        if (verdict == "whole" && !past) return;
        string words = verdict == "whole" ? said : $"{verdict} {said}";
        if (verdict == DevShortened) words += DevTipHolds(c, whole) ? ", its tooltip holds every word of it" : ", ITS TOOLTIP DOES NOT HOLD IT WHOLE";
        if (past) words += cell ? $", PAST THE EDGE of its cell ({c.Size.X:0} wide where its cell is {c.CustomMinimumSize.X:0})" : $", PAST THE EDGE ({end:0} > {edge:0})";
        if (verdict == "CUT") tally.Cut++;
        if (verdict == DevShortened) tally.Shortened++;
        if (past) tally.Past++;
        tally.NotWhole.Add(words);
    }

    // The tooltip a player sees on it: its own, or the nearest one around it (the line of the list it is on). It holds the
    // whole when it has the whole name of a column's name over the list, or every word of the words shown.
    static bool DevTipHolds(Control c, string? whole)
    {
        string tip = "";
        for (Node? node = c; node is Control k; node = k.GetParent())
            if (k.TooltipText != "" && (k != c || k.MouseFilter != Control.MouseFilterEnum.Ignore)) { tip = k.TooltipText; break; }
        if (tip == "") return false;
        if (whole != null && tip.Contains(whole, StringComparison.OrdinalIgnoreCase)) return true;
        string words = c is Label l ? l.Text : c is Button b ? b.Text : "";
        return Regex.Matches(words, @"[\p{L}\p{N}]+").All(m => tip.Contains(m.Value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Development (cuts=log): every label, button with words and drop-down on show, by parts, each with how many it
    /// measured and every one that is not whole by its words, width and room; then one summary line. "window" is the
    /// verdict of the "Dev: window" line logged just before.
    /// </summary>
    public void DevMeasureCuts(string window)
    {
        if (!_window.IsAlive) return;
        const string head = "Dev: cuts";
        var counts = new List<string>();
        int cut = 0, past = 0, shortened = 0;
        void Part(string name, IEnumerable<Control> things, float edge, string nothing, string about = "", string? wider = null)
        {
            var tally = new DevTally();
            foreach (Control c in things.ToList()) DevWords(c, edge, tally);
            // A part made wider than it is planned: something in it asks for more room than it has.
            if (wider != null)
            {
                tally.Past++;
                tally.NotWhole.Add("PAST THE EDGE " + wider);
            }
            counts.Add($"{name} {tally.Measured}");
            (cut, past, shortened) = (cut + tally.Cut, past + tally.Past, shortened + tally.Shortened);
            if (tally.Measured == 0 && wider == null)
            {
                DevLog?.Invoke($"{head}: {name}: nothing on show, none measured ({nothing}){about}");
                return;
            }
            string verdict = tally.NotWhole.Count == 0 ? "every one whole and within its edge"
                : $"{tally.NotWhole.Count} not whole or past the edge ({DevNone(tally.Cut, "CUT")}, {DevNone(tally.Shortened, DevShortened)}, {DevNone(tally.Past, "PAST THE EDGE")})";
            DevLog?.Invoke($"{head}: {name}: {tally.Measured} measured{about}: {verdict}");
            if (tally.NotWhole.Count > 0) DevChunks($"{head}: {name}, not whole", tally.NotWhole);
        }
        static float End(Control c) => c.GetGlobalRect().End.X;

        // The window's title and the columns' tags, outside the three columns.
        Control three = _resultsColumn.GetParent<Control>();
        var bodies = three.GetChildren().OfType<Control>().Select(c => c.GetChildCount() > 1 ? c.GetChild(1) : c).Cast<Node>().ToHashSet();
        if (_picker != null) bodies.Add(_picker);
        Part("the title and the columns' tags", DevWordsUnder(_window.Root, bodies), End(_window.Root), "no title");

        // Setup.
        if (_devSetup is ({ } setupColumn, { } setupBody) && GodotObject.IsInstanceValid(setupBody))
        {
            float planned = _s.Px(SetupWidth);
            Part("the Setup column", DevWordsUnder(setupBody), End(setupBody), "the column was not found", $", the column {setupColumn.Size.X:0} wide where {planned:0} are planned",
                setupColumn.Size.X > planned + 0.5f ? $"the Setup column {setupColumn.Size.X:0} wide where {planned:0} are planned: a line in it asks for more" : null);
        }
        else Part("the Setup column", Array.Empty<Control>(), 0, "the column was not found");

        // The line above the list and the buttons beside it.
        Control top = _showing.GetParent<Control>();
        Part("the line above the list and the buttons beside it", DevWordsUnder(top), End(top.GetParent<Control>()), "-", DevShowingSaid());

        // The list's header: the name's button and every column's name, those moved out of sight too.
        Control headerRow = _headerCells.GetParent().GetParent().GetParent<Control>();
        int titles = _headerCells.GetChildren().OfType<Button>().Count(b => !b.IsQueuedForDeletion());
        Part("the list's header", DevWordsUnder(headerRow), End(headerRow), "-", $", {titles} columns' names, those out of sight measured against their own column");

        // The list's lines: every line, those below the part in sight too, and every cell against its own cell.
        var scroll = _rows.GetParent<Control>();
        var rows = _rows.GetChildren().OfType<Control>().Where(r => !r.IsQueuedForDeletion()).ToList();
        int below = rows.Count(r => r.GetGlobalRect().End.Y > scroll.GetGlobalRect().End.Y + 0.5f);
        Part("the list's lines", scroll.IsVisibleInTree() ? DevWordsUnder(_rows) : Array.Empty<Control>(), End(scroll), "the list is empty",
            $", {rows.Count} lines ({below} below the part in sight), every name, the words beside it and every cell");
        // Under it the earlier scans, their lines laid out by the mod (LayOutBeside) in RecentRoom less the words beside
        // them: the least room a label of theirs has more than it is laid out in, and how high the Results column asks to be.
        var recentRows = _recent.GetChildren().OfType<Button>().Where(r => !r.IsQueuedForDeletion() && r.IsVisibleInTree()).ToList();
        var recentWords = recentRows.SelectMany(r => r.FindChildren("*", "Label", true, false).OfType<Label>())
            .Where(l => l.SizeFlagsHorizontal.HasFlag(Control.SizeFlags.Expand)).ToList();
        float Spare(Label l)
        {
            float planned = RecentRoom() - l.GetThemeStylebox("normal").GetMinimumSize().X;
            if (l.GetParent() is Control line && l.GetIndex() + 1 < line.GetChildCount() && line.GetChild(l.GetIndex() + 1) is Label beside)
                planned -= MathF.Ceiling(beside.GetCombinedMinimumSize().X) + line.GetThemeConstant("separation");
            return l.Size.X - l.GetThemeStylebox("normal").GetMinimumSize().X - MathF.Floor(planned);
        }
        float spare = recentWords.Count == 0 ? 0 : recentWords.Min(Spare);
        string recentSaid = recentRows.Count == 0 ? ", no earlier scan under it"
            : $", {recentRows.Count} earlier scans under it, their words in {recentWords.Sum(l => l.GetLineCount())} lines, laid out in {RecentRoom():0.#} px less the words " +
              $"beside them; the least room a label of theirs has beyond it {spare:0.#} px{(spare < -0.5f ? " (LESS THAN IT IS LAID OUT IN)" : "")}; " +
              $"the Results column asks {_resultsColumn.GetCombinedMinimumSize().Y:0} px of height and has {_resultsColumn.Size.Y:0}";
        Part("the empty list", _emptyBox.IsVisibleInTree() ? DevWordsUnder(_emptyBox) : Array.Empty<Control>(), End(_emptyBox), "the list has lines", _emptyBox.IsVisibleInTree() ? recentSaid : "");

        // Details, in three parts: its head; the lines written straight into it (notes, "Compared with" and the setup's name
        // under it, the stance lines, the headings, the foot); its panels (a Stat line's name, number and note, the "Found
        // in" rows and their headings).
        var shown = _details.GetChildren().OfType<Control>().Where(c => !c.IsQueuedForDeletion() && c.Visible).ToList();
        Control? detailsHead = shown.FirstOrDefault() is HBoxContainer first && first.GetChildren().OfType<VBoxContainer>().Any() ? first : null;
        bool Outside(Control c) => c != detailsHead && (c is Label || (c is HBoxContainer && c.GetChildren().OfType<HSeparator>().Any()) || _devStanceLines.Contains(c));
        float right = End(_details);
        string follows = $", Details following {DevDetailsFollows()}";
        Part("Details' head", detailsHead != null ? DevWordsUnder(detailsHead) : Array.Empty<Control>(), right, "no line selected", follows);
        Part("Details' lines outside its panels", shown.Where(Outside).SelectMany(c => DevWordsUnder(c)), right, "Details is empty");
        int found = _devSourceRows.Count(r => GodotObject.IsInstanceValid(r.Name) && r.Name.IsVisibleInTree());
        Part("Details' panels", shown.Where(c => c != detailsHead && !Outside(c)).SelectMany(c => DevWordsUnder(c)), right, "Details has no panel",
            found > 0 ? $", {found} 'Found in' rows among them" : ", no 'Found in' row");
        DevSmallLines(head);

        // Fight, the words above it, and the buttons over it.
        Control detailsBody = _fightButton.GetParent<Control>();
        Part("Fight and the words above it", detailsBody.GetChildren().OfType<Control>().Where(c => c is not ScrollContainer).SelectMany(c => DevWordsUnder(c)), End(detailsBody), "no fight selected");

        // A panel open beside the window: Find upgrades', or another (History, Where, Columns, an item picker), with its
        // rows below the part in sight too. Its size against the size it is planned with.
        if (_picker is { } panel && GodotObject.IsInstanceValid(panel) && panel.IsVisibleInTree())
        {
            string name = _pickerKind == UpgradePanel ? "Find upgrades' panel" : $"the {_pickerKind} panel";
            Control content = panel.GetChildCount() > 0 && panel.GetChild(0) is Control inner ? inner : panel;
            bool larger = panel.Size.X > panel.CustomMinimumSize.X + 0.5f || panel.Size.Y > panel.CustomMinimumSize.Y + 0.5f;
            Part(name, DevWordsUnder(panel), End(content), "-", $", {panel.Size.X:0}x{panel.Size.Y:0} where {panel.CustomMinimumSize.X:0}x{panel.CustomMinimumSize.Y:0} are planned",
                larger ? $"the panel {panel.Size.X:0}x{panel.Size.Y:0} where {panel.CustomMinimumSize.X:0}x{panel.CustomMinimumSize.Y:0} are planned: a line in it asks for more" : null);
        }
        else Part("the panel beside the window", Array.Empty<Control>(), 0, "no panel open");

        float headerY = headerRow.GetGlobalRect().Position.Y;
        DevLog?.Invoke($"{head}: {(_state.BestMode ? "Best for" : "Fights")}, {DevOnShow()}, size {_s.Scale:0.##}: {string.Join(", ", counts)} measured: " +
                       $"{DevNone(cut, "CUT")}, {DevNone(past, "PAST THE EDGE")}, {DevNone(shortened, DevShortened)}; the list's header at y {headerY:0}, " +
                       $"the line above it {_showing.GetLineCount()} {(_showing.GetLineCount() == 1 ? "line" : "lines")} {_showing.Size.Y:0} px high; Dev: window: {window}");
        // So that a count of none is read for what it is.
        DevLog?.Invoke($"{head}: not measured by cuts=log: text fields and their placeholders (the search boxes), buttons with a picture and no words " +
                       "(the slots, the X, the fold button, the arrows), tooltips, the folded window's title strip, the enemy cards, Fight's own window " +
                       "(fight.request) and the game's own windows; a panel only while it is open (picker=, then cuts=close).");
    }

    // Every small line under a number of Details as SmallLine made it: the label, and its words before they were laid out.
    readonly List<(Label Label, string Words)> _devSmallLines = new();

    /// <summary>Development: a label's words on one line of the log, its spaces that do not break as plain ones (SmallLine).</summary>
    static string DevPlain(string text) => text.Replace(Tie, ' ').Replace("\n", " / ");

    /// <summary>
    /// Development (cuts=log, issue #63): Details' small lines (SmallLine) as Godot drew them, read from each label itself: a
    /// row per line of its characters (Label.GetCharacterBounds), checked against the number of lines Godot counts. For each
    /// that takes more than one row, where it breaks, each line the mod laid out judged by its own rows: where the mod parted
    /// it (at its dots, or a line of its own words), a part alone too wide for the room that wraps on (wider than FitsIn
    /// lets a line be), or Godot BROKE IT WHERE THE MOD DID NOT PART IT (a laid line the mod measured as fitting). One label
    /// can have both, and one too wide does not hide one broken beside it (review of turn 2). And ± PARTED FROM ITS NUMBER
    /// wherever a row ends in a ±. A small line on one row is counted.
    /// </summary>
    void DevSmallLines(string head)
    {
        var shown = _devSmallLines.Where(s => GodotObject.IsInstanceValid(s.Label) && !s.Label.IsQueuedForDeletion() && s.Label.IsVisibleInTree()).ToList();
        if (shown.Count == 0)
        {
            DevLog?.Invoke($"{head}: Details' small lines: none on show");
            return;
        }
        Font font = shown[0].Label.GetThemeFont("font");
        int one = 0, own = 0, atDots = 0, tooWide = 0, broken = 0, parted = 0, unread = 0;
        var said = new List<string>();
        foreach (var (label, words) in shown)
        {
            string[] laid = label.Text.Split('\n');
            Font its = label.GetThemeFont("font");
            int size = label.GetThemeFontSize("font_size");
            float room = label.Size.X - label.GetThemeStylebox("normal").GetMinimumSize().X;
            List<List<string>>? rows = DevDrawnLines(label);
            if (rows == null)
            {
                unread++;
                said.Add($"'{DevPlain(label.Text)}': ITS LINES COULD NOT BE READ (Godot counts {label.GetLineCount()})");
                continue;
            }
            var drawn = rows.SelectMany(r => r).ToList();
            bool apart = drawn.Any(l => l.EndsWith('±'));
            if (apart) parted++;
            if (drawn.Count == 1 && !apart) { one++; continue; }
            // Each laid line by its own rows: drawn on more than one, it wrapped on where the mod knew it too wide, and was
            // broken by Godot where the mod measured it as fitting.
            var wrappedOn = new List<string>();
            var brokenAt = new List<string>();
            for (int i = 0; i < laid.Length; i++)
                if (rows[i].Count > 1)
                    (FitsIn(its.GetStringSize(laid[i], HorizontalAlignment.Left, -1, size).X, room) ? brokenAt : wrappedOn).Add(laid[i]);
            var how = new List<string>();
            if (brokenAt.Count > 0)
            {
                broken++;
                how.Add($"BROKEN WHERE THE MOD DID NOT PART IT ('{DevPlain(string.Join("\n", brokenAt))}' of '{DevPlain(label.Text)}')");
            }
            if (wrappedOn.Count > 0)
            {
                tooWide++;
                how.Add($"wrapped on where a part alone is wider than its {room:0.#} px ('{DevPlain(string.Join("\n", wrappedOn))}')");
            }
            if (how.Count == 0 && label.Text == words) { own++; how.Add("lines of its own words"); }
            else if (how.Count == 0) { atDots++; how.Add("parted at its dots"); }
            said.Add($"'{string.Join("' / '", drawn.Select(l => l.Replace(Tie, ' ')))}' ({drawn.Count} lines, {string.Join("; ", how)}" +
                     $"{(apart ? "; ± PARTED FROM ITS NUMBER" : "")}; {label.Size.X:0} px wide)");
        }
        DevLog?.Invoke($"{head}: Details' small lines: {shown.Count} on show, {one} on one line, {own} in lines of their own words, {atDots} parted at their dots, " +
                       $"{tooWide} wrapped on where a part alone is too wide, " +
                       $"{(broken == 0 ? "none broken by Godot where the mod did not part them" : $"{broken} BROKEN WHERE THE MOD DID NOT PART THEM")}, " +
                       $"{(parted == 0 ? "no ± apart from its number" : $"{parted} with a ± PARTED FROM ITS NUMBER")}" +
                       (unread > 0 ? $", {unread} whose lines could not be read" : "") +
                       $"; the font {(font.HasChar(Tie) ? "has" : "HAS NO")} space that does not break, text server '{TextServerManager.GetPrimaryInterface()?.GetName()}'");
        if (said.Count > 0) DevChunks($"{head}: Details' small lines in more than one line", said);
    }

    // A label's rows as drawn, for each line of its text: its characters by the row they are drawn in
    // (Label.GetCharacterBounds), a "\n" of its text ending a line; null where the rows do not come to the number of lines
    // Godot counts, which it then cannot tell.
    static List<List<string>>? DevDrawnLines(Label label)
    {
        string text = label.Text;
        float apart = label.GetThemeFont("font").GetHeight(label.GetThemeFontSize("font_size")) / 2;
        var lines = new List<List<string>> { new() };
        var row = new System.Text.StringBuilder();
        float? at = null;
        void EndRow()
        {
            lines[^1].Add(row.ToString().Trim(' ', Tie));
            row.Clear();
        }
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                EndRow();
                lines.Add(new List<string>());
                at = null;
                continue;
            }
            Rect2 box = label.GetCharacterBounds(i);
            bool drawn = box.Size.X > 0 || box.Size.Y > 0;
            if (drawn && at is { } y && Math.Abs(box.Position.Y - y) > apart) EndRow();
            if (drawn) at = box.Position.Y;
            row.Append(text[i]);
        }
        EndRow();
        return lines.Sum(l => l.Count) == label.GetLineCount() ? lines : null;
    }

    /// <summary>Development (cuts=close): the panel open beside the window closed again, as its X closes it.</summary>
    public string DevClosePanel()
    {
        if (_picker == null || !GodotObject.IsInstanceValid(_picker)) return "cuts=close: no panel was open beside the window; nothing closed.";
        string name = _pickerKind == UpgradePanel ? "Find upgrades' panel" : $"the {_pickerKind} panel";
        ClosePicker();
        return $"cuts=close: put back: {name} closed again, as its X closes it.";
    }

    // "none CUT" would read as a cut to a search: a count of none is written without the word.
    static string DevNone(int count, string words) => count == 0 ? words switch
    {
        "CUT" => "none cut",
        "PAST THE EDGE" => "none past the edge",
        _ => "none shortened",
    } : $"{count} {words}";

    // The line above the list: its words, in how many lines; each part on one line or parted, by its own words and by its
    // widest while on show, and whether it keeps its lines with every form its words can take while on show (a parted line
    // that wraps on at its widest would add a line mid-scan); the room it is laid out in (ShowingRoom) against the room the
    // label has; its tooltip's first line.
    string DevShowingSaid()
    {
        Font font = _showing.GetThemeFont("font");
        int size = _showing.GetThemeFontSize("font_size");
        float Line(string words) => font.GetStringSize(words, HorizontalAlignment.Left, -1, size).X;
        float Wide(string words) => words.Split('\n').Select(Line).DefaultIfEmpty(0).Max();
        float room = _showing.Size.X - _showing.GetThemeStylebox("normal").GetMinimumSize().X, planned = ShowingRoom();
        var parts = _showingParts.Select((p, i) =>
        {
            float own = Wide(p.One), widest = p.Alike.Select(a => Wide(a.One)).Append(own).Max();
            string laid = p.Parted == p.One ? "one way" : own <= planned && widest <= planned ? "on one line" : own <= planned ? "parted for its widest words only" : "parted";
            // The lines of the form chosen (LaidOut), with its own words and with each of its other forms.
            bool one = OnOne(p, Line, planned);
            int now = LinesOf(one ? p.One : p.Parted, Line, planned).Count();
            (string Words, int Lines) most = p.Alike.Select(a => (Words: one ? a.One : a.Parted, Lines: LinesOf(one ? a.One : a.Parted, Line, planned).Count()))
                .DefaultIfEmpty((Words: "", Lines: now)).MaxBy(a => a.Lines);
            string keeps = most.Lines > now ? $"ITS LINES CAN CHANGE WHILE ON SHOW: {now} now, {most.Lines} with '{most.Words.Replace("\n", " | ")}'"
                : $"keeps its {now} {(now == 1 ? "line" : "lines")} with every form of its words";
            return $"part {i + 1} {laid}: its words on one line {own:0.#} px, at their widest while on show {widest:0.#}" +
                   (widest > own + 0.05f ? $" ('{p.Alike.Select(a => a.One).MaxBy(Wide)}')" : "") + $", {keeps}";
        });
        return $", the line '{_showing.Text.Replace("\n", " | ")}' in {_showing.GetLineCount()} {(_showing.GetLineCount() == 1 ? "line" : "lines")}: " +
               $"{string.Join("; ", parts)}; laid out in {planned:0.#} px, the label has {room:0.#}{(room + 0.5f < planned ? " (LESS THAN IT IS LAID OUT IN)" : "")}; " +
               $"its tooltip first '{_showing.TooltipText.Split('\n')[0]}'";
    }

    // The tab is said apart. The list on show, an earlier scan, the line Details follows, a panel.
    string DevOnShow()
    {
        string list = _state.ShowingUpgrades ? $"the items tried for {_state.Upgrades!.Target.Name}" : _state.ShownComparing ? "Compare, the fights of A and B" : "the fights of one setup";
        int lines = _rows.GetChildren().Count(n => !n.IsQueuedForDeletion());
        string scan = _state.Viewing is { } v ? $", an earlier scan '{v.Title}' ({v.Id})" : ", no earlier scan";
        string panel = _picker != null && GodotObject.IsInstanceValid(_picker) && _picker.IsVisibleInTree()
            ? $"the {(_pickerKind == UpgradePanel ? "Find upgrades" : _pickerKind)} panel open" : "no panel open";
        return $"{list} ({lines} lines){scan}, Details following {DevDetailsFollows()}, {panel}";
    }

    string DevDetailsFollows()
    {
        if (_state.ShowingUpgrades)
            return _upgradeOrdered.FirstOrDefault(l => l.Key == _upgradeShownKey) is { } line && _state.TryOf(line) is { } t ? $"the line of {t.ItemName}" : "no line";
        return _state.Selected is { } key ? $"the line of {key}" : "no line";
    }

    /// <summary>
    /// Development, for the picture of shot=: what the window shows, read from it: the tab, the list, an earlier scan, the
    /// line Details follows, where Details is scrolled and how many "Found in" rows are in sight, the line above the list.
    /// </summary>
    public string DevShows()
    {
        string scroll = "Details is not in sight";
        if (_details.GetParent() is ScrollContainer box && box.IsVisibleInTree())
        {
            float most = Math.Max(0, _details.Size.Y - box.Size.Y);
            Rect2 seen = box.GetGlobalRect();
            var rows = _devSourceRows.Where(r => GodotObject.IsInstanceValid(r.Name) && r.Name.IsVisibleInTree()).ToList();
            int inSight = rows.Count(r => seen.Encloses(r.Name.GetGlobalRect()));
            scroll = $"Details scrolled {box.ScrollVertical} of {most:0} px{(box.ScrollVertical == 0 ? " (at its top)" : box.ScrollVertical >= most - 0.5f ? " (at its end)" : "")}, " +
                     $"{(rows.Count == 0 ? "no 'Found in' row" : $"{inSight} of {rows.Count} 'Found in' rows in sight")}";
        }
        return $"{(_state.BestMode ? "Best for" : "Fights")}, {DevOnShow()}, size {_s.Scale:0.##}; {scroll}; the line above the list '{_showing.Text.Replace("\n", " | ")}'";
    }

    /// <summary>Development (details=top|found|end): Details scrolled to its top, to its "Found in" heading, or to its end.</summary>
    public string DevScrollDetails(string where)
    {
        if (_details.GetParent() is not ScrollContainer scroll || !scroll.IsVisibleInTree()) return $"details={where}: Details is not on show; nothing scrolled.";
        int to;
        switch (where)
        {
            case "top": to = 0; break;
            case "end": to = int.MaxValue; break;
            case "found":
                if (_details.GetChildren().OfType<HBoxContainer>().FirstOrDefault(h => !h.IsQueuedForDeletion() && h.GetChildren().OfType<HSeparator>().Any()
                        && h.GetChildren().OfType<Label>().Any(l => l.Text == "Found in")) is not { } heading)
                    return "details=found: Details has no 'Found in' heading on show; nothing scrolled.";
                to = (int)heading.Position.Y;
                break;
            default: return $"details={where}: the lines are details=top, details=found and details=end.";
        }
        scroll.ScrollVertical = to;
        return $"details={where}: Details scrolled to {scroll.ScrollVertical} of {Math.Max(0, _details.Size.Y - scroll.Size.Y):0} px" +
               (where == "found" ? $" (its 'Found in' heading at {to} px)" : "") + "; the picture after it says what is in sight.";
    }

    /// <summary>
    /// Development (cuts=view=best|compare|fights): the newest earlier scan of that kind in History, shown as a click on its
    /// row does (DevShowScan). False, with one line, when History holds none.
    /// </summary>
    public bool DevShowNewest(string kind)
    {
        Func<Scan, bool>? of = kind switch
        {
            "best" => s => s.BestMode && !s.Compare,
            "compare" => s => s.Compare,
            "fights" => s => !s.BestMode && !s.Compare,
            _ => null,
        };
        if (of == null)
        {
            DevLog?.Invoke($"Dev: cuts: view={kind}: the kinds are best, compare and fights.");
            return false;
        }
        int place = _state.History.FindIndex(s => of(s)) + 1;
        if (place == 0)
        {
            DevLog?.Invoke($"Dev: cuts: view={kind}: History holds no {kind} scan ({_state.History.Count} scans in all); nothing shown.");
            return false;
        }
        DevLog?.Invoke($"Dev: cuts: view={kind}: the newest {kind} scan is number {place} of History's {_state.History.Count}.");
        return DevShowScan(place);
    }

    /// <summary>
    /// Development (cuts=widest): the setup's name in the line above the list stood in by one as wide as the line itself,
    /// the setup's own words again and again, in the simulator's words only: no loadout is renamed. False, with why, when
    /// the line names no setup. DevStandInEnd measures it and makes the line again from the game's names.
    /// </summary>
    public (bool Stood, string Said) DevStandInStart()
    {
        var (stood, said) = DevStandInSet();
        if (stood) Refresh();
        return (stood, stood ? said + "; measured two drawn frames later." : said);
    }

    // The stand-in of the line above the list set, for the next refresh (DevStandInStart, DevLongOn).
    (bool Stood, string Said) DevStandInSet()
    {
        string real = _state.ShowingUpgrades && _state.Upgrades is { } u ? u.SetupLabel
            : _state.ShownComparing ? _state.ShownLabelA
            : _state.Viewing?.SetupLabel ?? (_state.PerStance ? _state.SetupLabel : _state.SetupName);
        if (real == "" || !_showingParts.Any(p => p.One.Contains(real, StringComparison.Ordinal)))
            return (false, $"the line '{string.Join(" | ", _showingParts.Select(p => p.One))}' does not name the setup '{real}'; nothing stood in.");
        float room = ShowingRoom();
        if (room <= 0) return (false, "the line above the list is not laid out; nothing stood in.");
        string name = DevAsWide(real, room);
        _devStandIn = (real, name);
        _devStandInHigh = _showing.Size.Y;
        return (true, $"the setup's name '{real}' stood in by '{name}', {DevWide(_showing, name):0.#} px where the line has {room:0.#}, in the simulator's words only");
    }

    static float DevWide(Control c, string words) => c.GetThemeFont("font").GetStringSize(words, HorizontalAlignment.Left, -1, c.GetThemeFontSize("font_size")).X;

    // A setup's name as wide as a room at least, in the line's font: its own words again and again.
    string DevAsWide(string real, float room)
    {
        string own = real == "" ? "Loadout" : real, name = own;
        while (DevWide(_showing, name) < room) name += " " + own;
        return name;
    }

    // cuts=long: the stand-ins of the rows under the empty list (the game's two widest area names, and its widest enemy or
    // dungeon name for their Top line; ScanRow) and of Fight (FitFightWords), and the window's size before them.
    (string Where, string Fight)? _devRecentStandIn;
    string? _devFightName;
    Vector2? _devLongBefore;

    /// <summary>
    /// Development (cuts=long, cuts=long=nopicture): the longest words the simulator's own lines can show, on show until
    /// cuts=long=off or the window is built again, in the simulator's words only: nothing of the game's or the player's is
    /// renamed. The setup's name in the line above the list as wide as the line (DevStandInSet); the earlier scans under an
    /// empty list with the widest title a scan can have ("A against B: " and the game's two widest area names, as ScopeLabel
    /// joins two), their setups as wide as a row (DevAsWide) and the game's widest enemy or dungeon name in their Top line;
    /// and Fight with the game's widest enemy or dungeon name, or (noPicture) the widest that shows without its picture at
    /// this size (FightFace). cuts=log then measures them, and shot= pictures them.
    /// </summary>
    public string DevLongOn(bool noPicture, IReadOnlyCollection<string>? fights, IReadOnlyCollection<string>? areas)
    {
        var said = new List<string>();
        _devLongBefore ??= _window.Root.Size;
        said.Add("above the list: " + DevStandInSet().Said);
        if (fights is not { Count: > 0 } || areas is not { Count: > 0 })
            said.Add("the game's names could not be read: the rows under the empty list and Fight keep their words");
        else
        {
            Font title = GameStyle.SemiBold ?? _showing.GetThemeFont("font");
            int titleSize = _s.Px(14);
            string where = string.Join(", ", areas.OrderByDescending(a => title.GetStringSize(a, HorizontalAlignment.Left, -1, titleSize).X).Take(2));
            string fight = fights.MaxBy(n => DevWide(_showing, n))!;
            _devRecentStandIn = (where, fight);
            _recentSignature = "#";
            said.Add($"the earlier scans under an empty list: titled 'A against B: {where}', their setups as wide as a row ({RecentRoom():0.#} px), " +
                     $"Top: '{fight}'{(_recent.IsVisibleInTree() ? "" : " (none on show now)")}");
            if (_fightTarget == null || !_fightButton.IsVisibleInTree()) said.Add("Fight: not on show, nothing stood in");
            else
            {
                bool Bare(string n) => FightFace("Fight " + n) is var face && face.Words != "Fight" && face.Picture == null;
                string? name = noPicture ? fights.Where(Bare).MaxBy(n => ChoiceWidth(_fightButton, "Fight " + n, false))
                    : fights.MaxBy(n => ChoiceWidth(_fightButton, "Fight " + n, true));
                _devFightName = name;
                said.Add(name == null ? "Fight: no name of the game shows without its picture at this size; Fight keeps its words"
                    : $"Fight with '{name}' ({(noPicture ? "the widest that shows without its picture" : "the widest of the game")}), " +
                      $"'Fight {name}' {ChoiceWidth(_fightButton, "Fight " + name, true):0.#} px with its picture, {ChoiceWidth(_fightButton, "Fight " + name, false):0.#} without, " +
                      $"in {_fightButton.Size.X - Margins(_fightButton):0.#}");
            }
        }
        Refresh();
        FitFightWords();
        if (_devFightName != null)
            said.Add($"Fight now shows '{_fightButton.Text}' {(_fightButton.Icon != null ? "with" : "without")} its picture, its tooltip first '{_fightButton.TooltipText.Split('\n')[0]}'");
        return string.Join("; ", said) + $". In the simulator's words only; the window {_devLongBefore.Value.X:0}x{_devLongBefore.Value.Y:0} before them. " +
               "cuts=log measures them, cuts=long=off puts them back.";
    }

    /// <summary>Development (cuts=long=off): the stand-ins of cuts=long taken away, and whether the window grew meanwhile.</summary>
    public string DevLongOff()
    {
        if (_devLongBefore is not { } before) return "nothing stood in.";
        Vector2 now = _window.Root.Size;
        _devLongBefore = null;
        _devStandIn = null;
        _devRecentStandIn = null;
        _devFightName = null;
        _recentSignature = "#";
        Refresh();
        FitFightWords();
        string grew = now.X > before.X + 0.5f || now.Y > before.Y + 0.5f
            ? $"THE WINDOW GREW with them, from {before.X:0}x{before.Y:0} to {now.X:0}x{now.Y:0}"
            : $"the window {now.X:0}x{now.Y:0} with them, as before them";
        return $"{grew}; put back: the line above the list, the earlier scans under an empty list and Fight made again from the game's names " +
               $"('{_showing.Text.Replace("\n", " | ")}', Fight '{_fightButton.Text}').";
    }

    public string DevStandInEnd()
    {
        if (_devStandIn == null) return "nothing stood in";
        var (whole, said) = DevFit(_showing);
        float higher = _showing.Size.Y - _devStandInHigh;
        string grew = higher > 0.5f ? $"{higher:0} px higher than with the setup's own name: the list sits that much lower for a name this long" : "no higher than with the setup's own name";
        string result = $"with the stand-in {said}{(whole ? "" : " (A WORD OF IT IS WIDER THAN THE LINE)")}; the line {grew}{DevShowingSaid()}";
        _devStandIn = null;
        Refresh();
        return $"{result}; put back: the line made again from the game's names: '{_showing.Text.Replace("\n", " | ")}'.";
    }

    /// <summary>
    /// Development (cuts=widest): the names of the game's data where words of the game are on show outside the two lines of
    /// the issue, against the room they have at this size (review of turn 1): the widest enemy's or dungeon's name on the
    /// narrowest line of the list on show (cut short with "...", the line's tooltip then starting with it, ShortWithTip), on
    /// Fight (how many names show without its picture, and how many "Fight", FitFightWords), and the widest area names on
    /// Where, one and two of them ("2 areas" then, FitWhereWords); and Compare's line above the list in Best for at its
    /// widest while a scan goes on, with the game's widest name and the widest words of a lead (PairCount, BestLine), laid
    /// out as it would be (LaidOut). Words in capitals: CUT for words a place would show cut, which none should.
    /// </summary>
    public void DevLogWidestNames(IReadOnlyCollection<string> fights, IReadOnlyCollection<string> areas, string read)
    {
        const string head = "Dev: cuts: widest names";
        static float WideIn(Control c, string words) => c.GetThemeFont("font").GetStringSize(words, HorizontalAlignment.Left, -1, c.GetThemeFontSize("font_size")).X;
        var said = new List<string>();
        int cut = 0;

        // The list: the name of each line on show, the narrowest (its room less the label's margins).
        var names = _rows.GetChildren().OfType<Control>().Where(r => !r.IsQueuedForDeletion() && r.GetChildCount() > 0)
            .Select(r => r.GetChild(0).GetChildren().OfType<Label>().FirstOrDefault()).OfType<Label>().Where(l => l.IsVisibleInTree() && l.Size.X > 0).ToList();
        if (_state.ShowingUpgrades || names.Count == 0) said.Add($"a line of the list: {(names.Count == 0 ? "no line on show" : "the list on show is of items")}, not measured");
        else
        {
            Label narrowest = names.MinBy(l => l.Size.X)!;
            float room = MathF.Floor(narrowest.Size.X - narrowest.GetThemeStylebox("normal").GetMinimumSize().X);
            string widest = fights.DefaultIfEmpty("").MaxBy(n => WideIn(narrowest, n))!;
            // By Godot's own rule, as ShortWithTip judges a name (Shortened).
            int longer = fights.Count(n => Shortened(narrowest, n));
            bool trims = narrowest.TextOverrunBehavior != TextServer.OverrunBehavior.NoTrimming;
            if (longer > 0 && !trims) cut++;
            said.Add($"a line of the list, the narrowest of {names.Count} ({room:0.#} px): the widest '{widest}' {WideIn(narrowest, widest):0.#}, {longer} of {fights.Count} wider: " +
                     (longer == 0 ? "every one whole" : trims ? $"those {DevShortened}, the line's tooltip then starting with the name" : $"{longer} CUT"));
        }

        // Fight: the face each name gets, as FitFightWords chooses it (FightFace): "Fight <name>" beside its picture, the same
        // words without the picture, or "Fight" beside its picture.
        if (_fightButton.IsVisibleInTree() && _fightButton.Size.X > 0 && fights.Count > 0)
        {
            float room = _fightButton.Size.X - Margins(_fightButton);
            string widest = fights.MaxBy(n => ChoiceWidth(_fightButton, "Fight " + n, true))!;
            var faces = fights.Select(n => (Name: n, Face: FightFace("Fight " + n))).ToList();
            var bare = faces.Where(f => f.Face.Words != "Fight" && f.Face.Picture == null).Select(f => f.Name).ToList();
            int pictured = faces.Count(f => f.Face.Words != "Fight" && f.Face.Picture != null), alone = faces.Count(f => f.Face.Words == "Fight");
            bool fits = ChoiceWidth(_fightButton, "Fight", _fightIcon != null) <= room + 0.5f;
            if (!fits) cut++;
            said.Add($"Fight ({room:0.#} px for its words and picture): 'Fight {widest}' {ChoiceWidth(_fightButton, "Fight " + widest, true):0.#} with its picture, " +
                     $"{ChoiceWidth(_fightButton, "Fight " + widest, false):0.#} without; of {fights.Count} names {pictured} show 'Fight <name>' beside the picture, " +
                     $"{bare.Count} without the picture{(bare.Count > 0 ? $" (the widest '{bare.MaxBy(n => ChoiceWidth(_fightButton, "Fight " + n, false))}')" : "")}, " +
                     $"{alone} show 'Fight' with the tooltip starting with the whole words" + (fits ? "" : ": 'Fight' alone is CUT"));
        }
        else said.Add("Fight: not on show, not measured");

        // Where: one area's name, and two (ScopeLabel: "A, B"), beside its arrow, as FitWhereWords measures them.
        if (_where.IsVisibleInTree() && _where.Size.X > 0 && areas.Count > 0)
        {
            float room = _where.Size.X - Margins(_where);
            bool icon = _where.Icon != null;
            var two = areas.OrderByDescending(a => ChoiceWidth(_where, a, icon)).Take(2).ToList();
            string pair = string.Join(", ", two);
            int longer = areas.Count(a => ChoiceWidth(_where, a, icon) > room + 0.5f);
            bool shorter = new[] { "1 area", "2 areas" }.All(w => ChoiceWidth(_where, w, icon) <= room + 0.5f);
            if (!shorter) cut++;
            said.Add($"Where ({room:0.#} px for its words and arrow): the widest area '{two[0]}' {ChoiceWidth(_where, two[0], icon):0.#}, {longer} of {areas.Count} wider; " +
                     $"the widest two '{pair}' {ChoiceWidth(_where, pair, icon):0.#}: " +
                     (ChoiceWidth(_where, pair, icon) <= room + 0.5f ? "whole" : shorter ? "those show '1 area' or '2 areas', the tooltip starting with the names" : "'2 areas' is CUT"));
        }
        else said.Add("Where: not on show, not measured");

        // Compare in Best for, during a scan of every fight of the game: the counts and the best line at their widest, the
        // counts under the widest name a column can have there (Best for names XP after the skill trained, TitleOf). The
        // Results column has Compare's room in Fights too (EnemyPart).
        if (fights.Count > 0 && (CountedColumn() ?? ColumnOf("xp")) is { } counted && ColumnOf("xp") is not null)
        {
            string most = WidestCount(fights.Count), name = fights.MaxBy(n => WideIn(_showing, n))!, lead = WidestLead();
            var count = CountWords(counted, most, most, most, most, most, true);
            string title = TitleOf(counted), widestTitle = _state.Skills.Select(s => s.Name).Append(title).MaxBy(t => WideIn(_showing, t))!;
            count = (widestTitle + count.One[title.Length..], widestTitle + count.Parted[title.Length..]);
            var parts = new[]
            {
                new ShownPart(count.One, count.Parted, Array.Empty<(string, string)>()),
                new ShownPart($"Best with A: {name}{lead}{Dot}Best with B: {name}{lead}", $"Best with A: {name}{lead}\nBest with B: {name}{lead}", Array.Empty<(string, string)>()),
            };
            // Without Back, which Compare in Best for does not show: the room it leaves when it is on show (ShowingRoom).
            float room = ShowingRoom() + (_back.Visible ? MathF.Ceiling(_back.GetCombinedMinimumSize().X) + _showing.GetParent<Control>().GetThemeConstant("separation") : 0);
            List<string> lines = LaidOut(parts, room);
            bool whole = lines.All(l => WideIn(_showing, l) <= room + 0.5f);
            if (!whole) cut++;
            said.Add($"Compare's line above the list in Best for at its widest ({room:0.#} px, no Back): '{string.Join(" | ", lines)}' in {lines.Count} lines: {(whole ? "every line whole" : "CUT")}");
        }
        DevLog?.Invoke($"{head}: {read}; size {_s.Scale:0.##}: {(cut == 0 ? "none cut" : $"{cut} CUT")}");
        DevChunks(head, said);
    }

    /// <summary>
    /// Development (cuts=widest): every name a "Found in" row can show, as SourceRow lays it out, against the room of the
    /// narrowest row on show at this size: on one line, or parted at its dot with the place under it, or wrapping on; CUT
    /// where a word is wider than its room. "read" says how many names were read, and how long it took.
    /// </summary>
    public void DevLogWidestFoundIn(IReadOnlyCollection<ItemLine> lines, string read)
    {
        const string head = "Dev: cuts: widest Found in";
        var rows = _devSourceRows.Where(r => GodotObject.IsInstanceValid(r.Name) && r.Name.IsVisibleInTree() && r.Name.Size.X > 0).ToList();
        if (rows.Count == 0)
        {
            DevLog?.Invoke($"{head}: {read}; no 'Found in' row on show to measure them against (click a line of Find upgrades first).");
            return;
        }
        static float RoomOf(Label l) => l.Size.X - l.GetThemeStylebox("normal").GetMinimumSize().X;
        Label nameLabel = rows.MinBy(r => RoomOf(r.Name)).Name;
        Font font = nameLabel.GetThemeFont("font");
        int size = nameLabel.GetThemeFontSize("font_size");
        float room = RoomOf(nameLabel);
        // The place under the name: its own font and size, and its row less the indent past the picture.
        Label? placeLabel = rows.Select(r => r.Place).OfType<Label>().FirstOrDefault(l => GodotObject.IsInstanceValid(l));
        Font placeFont = placeLabel?.GetThemeFont("font") ?? font;
        int placeSize = placeLabel?.GetThemeFontSize("font_size") ?? _s.Px(12);
        float indent = placeLabel?.GetParent() is MarginContainer under ? under.GetThemeConstant("margin_left") : 0;
        float placeRoom = nameLabel.GetParent().GetParent<Control>().Size.X - indent - (placeLabel?.GetThemeStylebox("normal").GetMinimumSize().X ?? 0);
        float Wide(Font f, int s, string words) => f.GetStringSize(words, HorizontalAlignment.Left, -1, s).X;
        IEnumerable<string> Words(string words) => words.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        int one = 0, parted = 0, wraps = 0;
        var cut = new List<string>();
        (string Name, float Width) widest = ("", 0), widestWord = ("", 0);
        foreach (ItemLine line in lines)
        {
            float wide = Wide(font, size, line.Name);
            if (wide > widest.Width) widest = (line.Name, wide);
            foreach (string word in Words(line.Name))
                if (Wide(font, size, word) is var w && w > widestWord.Width) widestWord = (word, w);
            if (wide <= room) { one++; continue; }
            var (name, place) = Parted(line) ?? (line.Name, "");
            if (place != "") parted++;
            if (Wide(font, size, name) > room || (place != "" && Wide(placeFont, placeSize, place) > placeRoom)) wraps++;
            foreach (string word in Words(name).Where(word => Wide(font, size, word) > room))
                cut.Add($"'{word}' of '{line.Name}' {Wide(font, size, word):0.#} wide in {room:0.#}");
            foreach (string word in Words(place).Where(word => Wide(placeFont, placeSize, word) > placeRoom))
                cut.Add($"'{word}' of '{line.Name}' under its name {Wide(placeFont, placeSize, word):0.#} wide in {placeRoom:0.#}");
        }
        string Laid(string words)
        {
            ItemLine? line = lines.FirstOrDefault(l => l.Name == words);
            if (line == null || Wide(font, size, words) <= room) return "on one line";
            if (Parted(line) is (string name, string place))
                return $"parted: '{name}' {Wide(font, size, name):0.#} in {room:0.#}, and under it '{place}' {Wide(placeFont, placeSize, place):0.#} in {placeRoom:0.#}" +
                       (Wide(font, size, name) > room || Wide(placeFont, placeSize, place) > placeRoom ? ", wrapping on" : "");
            return "wrapping on";
        }
        DevLog?.Invoke($"{head}: {read}; a row's room here {room:0.#} px (the narrowest of the {rows.Count} rows on show), a place's under its name {placeRoom:0.#}; " +
                       $"the widest '{widest.Name}' {widest.Width:0.#} px, {Laid(widest.Name)}; the widest word '{widestWord.Name}' {widestWord.Width:0.#} px; " +
                       $"{one} on one line, {parted} parted at their dot, {wraps} wrapping on: " + (cut.Count == 0 ? "every name whole" : $"{cut.Count} CUT"));
        if (cut.Count > 0) DevChunks(head + ", CUT", cut.Take(20));
    }
}
#endif
