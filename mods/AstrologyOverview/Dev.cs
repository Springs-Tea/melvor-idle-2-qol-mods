#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Entities.Astrology;
using GamesByMalcs.Melvor2.GameMenus.Pages;
using GamesByMalcs.Melvor2.Utils;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace AstrologyOverview;

// Development: a file "astrology.request" in the mod's folder (what it says is not read) runs the check below
// with nobody at the keyboard, one step about every half second, and writes "astrology.done" when it ends:
// "ok", or "failed at <step>: <why>". Every step logs what it read as "Dev: ..." lines in MelvorModLoader.log.
//   .\tools\dev-cycle.ps1 -Mod AstrologyOverview -Into AstrologyOverview -Request astrology.request -Produces astrology.done
//  1. Opens the Astrology page the way the sidebar does, if another page is open.
//  2. "Dev: strip": every round button in the strip beside this one (name, icon, tooltip); "Dev: words": the
//     game's words the rows use and the label each was read from; "Dev: label": every label of the constellation
//     screen with its text. Picture shot-astrology-entry.png.
//  3. Opens the game's list of constellations. shot-astrology-picker.png; "Dev: lock": for every constellation,
//     its card's lock panel and requirements next to the row's status and missing requirements ("MISMATCH"
//     marks a difference); shot-astrology-picker-locked.png with a revealed locked card scrolled into sight.
//  4. Clicks the round button. shot-astrology-overview.png and crop-astrology-overview.png (the window);
//     "Dev: row": every row as it is drawn; "Dev: size": the window against the screen and the bottom bar;
//     crop-astrology-overview-locked.png with a locked row in sight.
//  5. Rebuilds the window 20% smaller (crop-astrology-small.png), then asks for the largest size, which must
//     stop at the largest that fits above the bottom bar (crop-astrology-large.png), then goes back.
//  6. Rests the pointer on a row that opens; "Dev: tooltip" and shot-astrology-tooltip.png.
//  7. Clicks that row with the game's list open. "Dev: before click 1" and "after click 1" hold every
//     constellation's knowledge, nodes and choices, the study and its timer, and must be the same: only the
//     selection may change. shot-astrology-selected.png, and "Dev: labels" (below) for it.
//  8. Opens the overview again from the constellation screen (the list hidden):
//     crop-astrology-from-constellation.png, then clicks another row with the same before and after check;
//     shot-astrology-selected-2.png.
//  9. Opens every constellation that can be opened, in turn, through the row's own path, and logs "Dev: labels":
//     the game's "Next unlock at", major node line, nodes available, knowledge per study and interval next to the
//     overview's, "MISMATCH" where they differ; and "Dev: node names", the names the game's node views show.
//     Then it puts back the screen and constellation it started on. Unlocked nodes and the study must not change.
public partial class AstrologyOverviewMod
{
    const string RequestFile = "astrology.request", DoneFile = "astrology.done";
    readonly List<(string Name, Func<bool> Run)> _devSteps = new();
    int _devAt;
    ulong _devStarted;

    void DevReset()
    {
        _devSteps.Clear();
        _devAt = 0;
    }

    void DevRequest()
    {
        if (GameAccess.IsComputingOffline) return;
        string step = "";
        try
        {
            if (_devSteps.Count == 0)
            {
                string path = Path.Combine(_ctx.Directory, RequestFile);
                if (!File.Exists(path)) return;
                File.Delete(path);
                DevPlan();
                _ctx.Log($"Dev: Astrology check started, {_devSteps.Count} steps.");
            }
            step = _devSteps[_devAt].Name;
            if (!_devSteps[_devAt].Run())
            {
                if (Time.GetTicksMsec() - _devStarted >= 6000) throw new InvalidOperationException("timed out; " + DevState());
                return;
            }
            _devAt++;
            _devStarted = Time.GetTicksMsec();
            if (_devAt >= _devSteps.Count) DevEnd("ok");
        }
        catch (Exception e)
        {
            _ctx.Log($"Dev: '{step}' failed: {e}");
            DevEnd($"failed at {step}: {e.Message}");
        }
    }

    void DevEnd(string result)
    {
        _ctx.Log("Dev: Astrology check " + result);
        DevReset();
        File.WriteAllText(Path.Combine(_ctx.Directory, DoneFile), result);
    }

    string DevState() =>
        $"list shown={_picker?.IsVisibleInTree()}, constellation screen shown={_current?.IsVisibleInTree()}, overview open={IsOpen}, " +
        $"button lit={_opener?.ButtonPressed}, selected={_astrology?.Action.SelectedConstellation?.Constellation.Id ?? "none"}";

    void DevPlan()
    {
        DevReset();
        _devStarted = Time.GetTicksMsec();
        var action = _astrology!.Action;
        AstrologyConstellation? locked = null, first = null, second = null, startSelected = null;
        bool startOnList = false;
        int listScroll = 0;
        float chosen = _scale;
        string nodes = "";
        void Add(string name, Func<bool> run) => _devSteps.Add((name, run));

        Add("open the Astrology page", () =>
        {
            Close();
            if (_page?.IsVisibleInTree() == true) return true;
            var option = _game!.Layout.Sidebar.Options.FirstOrDefault(o =>
                o.Page is EntityPage p && ReferenceEquals(p.Entity, _astrology) &&
                o.Requirements?.AreMet != false && o.DisplayRequirements?.AreMet != false);
            if (option?.Page is not EntityPage page) throw new InvalidOperationException("Astrology is locked.");
            GameAccess.FindGameView()!.SetCurrentPage(page);
            return true;
        });
        Add("entry", () =>
        {
            if (_opener?.IsVisibleInTree() != true) return false;
            startOnList = _picker!.IsVisibleInTree();
            startSelected = action.SelectedConstellation?.Constellation;
            _ctx.Log($"Dev: started on {(startOnList ? "the list" : "the constellation screen")}, selected {startSelected?.Id ?? "none"}");
            DevStrip();
            DevWords();
            Shot("entry");
            return true;
        });
        Add("open the list", () =>
        {
            if (!_picker!.IsVisibleInTree()) _current!.SelectConstellationButton.EmitSignal(BaseButton.SignalName.Pressed);
            return true;
        });
        Add("list", () =>
        {
            if (!_picker!.IsVisibleInTree()) return false;
            Shot("picker");
            DevLocks();
            var cards = NativeCards();
            locked = ReadRows().FirstOrDefault(r => r.Status == UnlockStatus.Locked &&
                cards.TryGetValue(r.Constellation, out var card) && card.IsVisibleInTree())?.Constellation;
            if (locked != null && ListScroll() is { } scroller)
            {
                listScroll = scroller.ScrollVertical;
                scroller.EnsureControlVisible(cards[locked]);
                DevLabels("locked card " + locked.Id, Native.LockPanel(cards[locked]));
            }
            return true;
        });
        Add("locked card", () =>
        {
            if (locked == null) { _ctx.Log("Dev: no revealed locked card to compare."); return true; }
            Shot("picker-locked");
            if (ListScroll() is { } scroller) scroller.ScrollVertical = listScroll;
            return true;
        });
        Add("press the round button", () => { DevMouse(_opener!); return true; });
        Add("overview", () =>
        {
            if (!IsOpen) return false;
            if (!_opener!.ButtonPressed) throw new InvalidOperationException("The round button is not lit while the overview is open.");
            _scroll!.ScrollVertical = 0;
            return true;
        });
        Add("overview picture", () =>
        {
            Shot("overview", crop: true);
            DevRows("overview");
            DevSize("overview");
            CompareLabels();
            if (locked != null && _rows.TryGetValue(locked, out var row)) _scroll!.EnsureControlVisible(row.Root);
            return true;
        });
        Add("locked row", () =>
        {
            if (locked != null) Shot("overview-locked", crop: true);
            Resize(_window!.Style.Scale - 0.2f);
            return true;
        });
        Add("smaller", () =>
        {
            if (!IsOpen) return false;
            Shot("small", crop: true);
            DevSize("smaller");
            Resize(GameWindow.MaxScale);
            return true;
        });
        Add("largest", () =>
        {
            if (!IsOpen) return false;
            Shot("large", crop: true);
            DevSize("largest that fits");
            _scale = chosen;
            BuildWindow();
            Open();
            return true;
        });
        Add("point at a row", () =>
        {
            if (!IsOpen) return false;
            first = DevTarget(action.SelectedConstellation?.Constellation) ?? throw new InvalidOperationException("No row opens a constellation.");
            _scroll!.EnsureControlVisible(_rows[first].Root);
            return true;
        });
        Add("pointer", () => { DevPoint(_rows[first!].Button); return true; });
        Add("tooltip", () =>
        {
            Button hovered = _rows[first!].Button;
            string tooltip = GameStyle.DevTooltips(_ctx.Tree.Root);
            if (_ctx.Tree.Root.GuiGetHoveredControl() != hovered || hovered.TooltipText.Length == 0 ||
                !tooltip.Contains("'" + hovered.TooltipText + "'")) return false;
            _ctx.Log("Dev: tooltip " + tooltip);
            Shot("tooltip");
            return true;
        });
        Add("click from the list", () =>
        {
            if (!_picker!.IsVisibleInTree()) throw new InvalidOperationException("The game's list should be open behind the overview.");
            DevClick(first!, "1");
            return true;
        });
        Add("opened from the list", () => DevOpened(first!, "selected"));
        Add("open the overview from the constellation", () => { DevMouse(_opener!); return true; });
        Add("overview from the constellation", () =>
        {
            if (!IsOpen) return false;
            _scroll!.ScrollVertical = 0;
            return true;
        });
        Add("rows from the constellation", () =>
        {
            Shot("from-constellation", crop: true);
            DevRows("from the constellation");
            second = DevTarget(first);
            if (second == null) _ctx.Log("Dev: no row opens a constellation from the constellation screen.");
            else
            {
                if (second == first) _ctx.Log("Dev: only one constellation can be opened: the second click opens it again.");
                _scroll!.EnsureControlVisible(_rows[second].Root);
            }
            return true;
        });
        Add("click from the constellation", () =>
        {
            if (second == null) return true;
            if (_picker!.IsVisibleInTree()) throw new InvalidOperationException("The game's list is open: this click must start from the constellation screen.");
            DevClick(second, "2");
            return true;
        });
        Add("opened from the constellation", () => second == null || DevOpened(second, "selected-2"));
        foreach (var c in _astrology.Config.Constellations)
        {
            bool opened = false;
            Add("open " + c.Id, () =>
            {
                if (nodes == "") nodes = DevNodes();
                var row = ConstellationRow.Read(c, action);
                opened = NativeCards().TryGetValue(c, out var card) && CanOpen(row, card);
                if (opened) Select(c);
                else _ctx.Log($"Dev: labels | {c.Id} | not opened: {(c.DisplayRequirements?.AreMet == false ? "not revealed" : row.Status.ToString())}");
                return true;
            });
            Add("labels " + c.Id, () =>
            {
                if (!opened) return true;
                if (_current?.IsVisibleInTree() != true || _picker!.IsVisibleInTree() ||
                    action.SelectedConstellation?.Constellation != c) return false;
                Refresh();
                CompareLabels();
                DevNodeNames();
                return true;
            });
        }
        Add("put back", () =>
        {
            if (nodes != "" && DevNodes() != nodes)
                throw new InvalidOperationException("Unlocked nodes or the study changed while the constellations were opened.");
            if (startOnList)
            {
                if (!_picker!.IsVisibleInTree()) _current!.SelectConstellationButton.EmitSignal(BaseButton.SignalName.Pressed);
            }
            else if (startSelected != null && action.SelectedConstellation?.Constellation != startSelected) Select(startSelected);
            Close();
            return true;
        });
    }

    // A row that opens, another than the one given when there is one; a study time first.
    AstrologyConstellation? DevTarget(AstrologyConstellation? except) => ReadRows()
        .Where(r => _rows.TryGetValue(r.Constellation, out var view) && !view.Button.Disabled)
        .OrderBy(r => ReferenceEquals(r.Constellation, except)).ThenBy(r => r.Status != UnlockStatus.Study)
        .FirstOrDefault()?.Constellation;

    void DevClick(AstrologyConstellation c, string n)
    {
        var button = _rows[c].Button;
        if (button.Disabled || !button.IsVisibleInTree()) throw new InvalidOperationException("The row no longer opens " + c.Id + ".");
        string before = Snapshot($"before click {n}", c);
        button.EmitSignal(BaseButton.SignalName.Pressed);
        string after = Snapshot($"after click {n}", c);
        if (before != after) throw new InvalidOperationException("Study, timer or node state changed during the click.");
    }

    bool DevOpened(AstrologyConstellation c, string shot)
    {
        if (_current?.IsVisibleInTree() != true || _picker!.IsVisibleInTree()) return false;
        if (IsOpen) throw new InvalidOperationException("The overview is still open after the click.");
        if (_opener!.ButtonPressed) throw new InvalidOperationException("The round button is still lit after the click.");
        if (_astrology!.Action.SelectedConstellation?.Constellation != c) throw new InvalidOperationException("The click did not open " + c.Id + ".");
        _ctx.Log($"Dev: opened {c.Id}; {DevState()}");
        Refresh();
        CompareLabels();
        Shot(shot);
        return true;
    }

    ScrollContainer? ListScroll()
    {
        for (Node? parent = _cards!.GetParent(); parent != null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll) return scroll;
        return null;
    }

    // ---- what is logged ------------------------------------------------------------------------------------------

    static string DevTexture(Texture2D? texture) =>
        texture == null ? "none" : $"{texture.ResourcePath} {texture.GetWidth()}x{texture.GetHeight()}";

    string DevPath(Node? node) => node == null ? "nowhere" : _page?.GetPathTo(node).ToString() ?? node.GetPath().ToString();

    void DevStrip()
    {
        if (_opener?.GetParent() is not Node strip) return;
        foreach (Node child in strip.GetChildren())
            _ctx.Log($"Dev: strip | {child.Name} | {child.GetType().Name} | shown={(child as CanvasItem)?.IsVisibleInTree()} | " +
                $"icon {DevTexture((child as Button)?.Icon)} | tooltip '{(child as Control)?.TooltipText}'");
    }

    void DevWords()
    {
        _ctx.Log($"Dev: words | minor '{_words.Minor}' from {DevPath(Native.FirstWords(_current!.MinorNodeButton))} | " +
            $"major '{_words.Major}' from {DevPath(Native.FirstWords(_current.MajorNodeButton))} | " +
            $"bonus heading '{_words.BonusHeading}' from {DevPath(Native.BonusHeading(_current))} | " +
            $"enemy '{_words.Enemy}' from {DevPath(Native.EnemyLabel(Native.MinorBonuses(_current)))} | star {DevTexture(_words.Star)}");
        DevLabels("constellation screen", _current, Native.MinorNodes(_current), Native.MajorNodes(_current));
    }

    // Every label under a node, with its words; the node lists are left out (see DevNodeNames).
    void DevLabels(string what, Node root, params Node[] skip)
    {
        int count = 0;
        void Walk(Node node)
        {
            if (skip.Contains(node) || count >= 80) return;
            if (node is Label or RichTextLabel)
            {
                string text = node is Label label ? label.Text : ((RichTextLabel)node).GetParsedText();
                _ctx.Log($"Dev: label | {what} | {root.GetPathTo(node)} | {node.GetType().Name} | shown={((CanvasItem)node).IsVisibleInTree()} | '{text.Replace("\n", " / ")}'");
                count++;
            }
            foreach (Node child in node.GetChildren()) Walk(child);
        }
        Walk(root);
    }

    void DevLocks()
    {
        var cards = NativeCards();
        static IEnumerable<string> Texts(ConditionGroupView view) => view.FindChildren("*", "RichTextLabel", true, false)
            .OfType<RichTextLabel>().Select(l => l.GetParsedText().Trim()).Where(t => t.Length > 0);
        foreach (var c in _astrology!.Config.Constellations)
        {
            var row = ConstellationRow.Read(c, _astrology.Action);
            bool revealed = c.DisplayRequirements?.AreMet != false;
            if (!cards.TryGetValue(c, out var card))
            {
                _ctx.Log($"Dev: lock | {c.Id} | no card | row {(revealed ? row.Status.ToString() : "not listed")}");
                continue;
            }
            bool shown = InList(card), lockShown = InList(Native.LockPanel(card));
            bool same = shown ? lockShown == (row.Status == UnlockStatus.Locked) : !revealed;
            string game = lockShown ? string.Join(" / ", Texts(Native.PrimaryRequirements(card)).Concat(Texts(Native.SecondaryRequirements(card)))) : "";
            string ours = string.Join(" / ", Native.Missing(c.Requirements).Concat(Native.Missing(c.SecondaryRequirements))
                .Select(clause => string.Join(" or ", clause.Select(x => StringUtils.StripBBCode(x.GetDescription(false, false))))));
            _ctx.Log($"Dev: lock | {c.Id} | card shown={shown}, lock panel shown={lockShown}, words '{(lockShown ? Native.LockWords(card) : "")}' | " +
                $"row {(revealed ? row.Status.ToString() : "not listed")} | card requirements '{game}' | overview missing '{ours}'{(same ? "" : "  MISMATCH")}");
        }
    }

    void DevRows(string when)
    {
        foreach (var row in ReadRows())
        {
            if (!_rows.TryGetValue(row.Constellation, out var view)) continue;
            _astrology!.Action.ActiveConstellations.TryGetValue(row.Constellation, out var active);
            _ctx.Log($"Dev: row | {when} | {row.Constellation.Id} | {row.Status} | {view.DevText} | knowledge={active?.Knowledge:R}, " +
                $"knowledge/study={active?.Stats.Knowledge.Value:R}, interval ms={active?.Stats.Interval.Value:R}, time ms={row.Milliseconds:R}");
        }
    }

    void DevSize(string when)
    {
        var root = _window!.Root;
        Vector2 size = root.Size, screen = _ctx.Tree.Root.GetVisibleRect().Size, measured = _fullSize * _window.Style.Scale;
        float bar = FooterHeight(), bottom = root.GetGlobalRect().End.Y;
        _ctx.Log($"Dev: size | {when} | window {size.X:0}x{size.Y:0} at {_window.Style.Scale:0.##} (chosen {_scale:0.##}, largest {LargestScale(bar):0.##}), " +
            $"{_fullSize.X:0}x{_fullSize.Y:0} at 100% as measured; screen {screen.X:0}x{screen.Y:0}, bottom bar {bar:0}, window bottom at {bottom:0}" +
            (size.X > measured.X + 2 || size.Y > measured.Y + 2 ? "  LARGER THAN MEASURED" : "") +
            (bottom > screen.Y - bar + 0.5f ? "  OVER THE BOTTOM BAR" : ""));
    }

    // The game's labels on the constellation screen next to the overview's row, for the selected constellation.
    void CompareLabels()
    {
        var action = _astrology!.Action;
        if (action.SelectedConstellation is not { } active || !_rows.TryGetValue(active.Constellation, out var view))
        {
            _ctx.Log("Dev: labels | no selected row to compare.");
            return;
        }
        var c = active.Constellation;
        var row = ConstellationRow.Read(c, action);
        var minor = Native.MinorCost(_current!);
        var major = Native.MajorCost(_current!);
        Label minorAvailable = Native.MinorAvailable(_current!), majorAvailable = Native.MajorAvailable(_current!);
        // Shown on the constellation screen in its own tab (minor or major nodes), whichever tab is in front and
        // whether that screen is in front right now or the list is.
        bool Shown(CanvasItem item)
        {
            for (Node? node = item; node != null && node != _current && node != _current!.MinorNodeMainContainer &&
                 node != _current.MajorNodeMainContainer; node = node.GetParent())
                if (node is CanvasItem { Visible: false }) return false;
            return true;
        }
        if (!_current!.IsVisibleInTree()) _ctx.Log($"Dev: labels | {c.Id} | the constellation screen is behind the list: its labels may be from before.");
        bool minorShown = Shown(minor), majorShown = Shown(major);
        bool minorDone = active.MinorNodeUnlockCount >= c.MinorNodes.Count, majorDone = active.MajorNodeUnlockCount >= c.MajorNodes.Count;
        string Expected(int count, bool done) => done ? Native.AllUnlocked(null!).Text : Native.Available(null!).Format(count, count);
        bool AvailableSame(Label label, int count, bool done) => !Shown(label) || label.Text == Expected(count, done);
        string At(int index) => index >= 0 && index < c.MinorNodeUnlockKnowledge.Count ? c.MinorNodeUnlockKnowledge[index].ToString() : "none";
        int reached = c.ComputeAvailableMinorNodeCount(active.Knowledge);

        bool costSame = minorShown == view.Cost.Visible && (!minorShown || minor.Value == view.Cost.Value);
        _ctx.Log($"Dev: labels | {c.Id} | next unlock: game '{minor.GetParsedText()}' shown={minorShown} value={minor.Value} | " +
            $"overview '{(view.Cost.Visible ? view.Cost.GetParsedText() : "")}' shown={view.Cost.Visible} value={view.Cost.Value} | " +
            $"table at the count the knowledge reached [{reached}]={At(reached)}, at the available counter [{active.AvailableMinorNodeCount}]={At(active.AvailableMinorNodeCount)} | " +
            $"colour game {minor.GetThemeColor("default_color").ToHtml()}, overview {view.Cost.GetThemeColor("default_color").ToHtml()}; " +
            $"size game {minor.GetThemeFontSize("normal_font_size")}, overview {view.Cost.GetThemeFontSize("normal_font_size")}" +
            (costSame ? "" : "  MISMATCH"));
        bool majorSame = majorShown == view.MajorCost.Visible && (!majorShown || major.GetParsedText().Trim() == view.MajorCost.Text.Trim());
        _ctx.Log($"Dev: labels | {c.Id} | major node: game '{major.GetParsedText()}' shown={majorShown} | overview '{view.MajorCost.Text}' " +
            $"shown={view.MajorCost.Visible} | minor unlocked {active.MinorNodeUnlockCount}, major available {active.AvailableMajorNodeCount} of {c.MajorNodes.Count}, " +
            $"{c.MinorNodesPerMajorNode} minor per major" + (majorSame ? "" : "  MISMATCH"));
        bool availableSame = AvailableSame(minorAvailable, row.MinorChoices, minorDone) && AvailableSame(majorAvailable, row.MajorChoices, majorDone);
        _ctx.Log($"Dev: labels | {c.Id} | available: game minor '{minorAvailable.Text}' shown={Shown(minorAvailable)}, " +
            $"major '{majorAvailable.Text}' shown={Shown(majorAvailable)} | overview minor {row.MinorChoices}, major {row.MajorChoices} | " +
            $"counters {active.AvailableMinorNodeCount}/{active.AvailableMajorNodeCount}, unlocked {active.MinorNodeUnlockCount}/{active.MajorNodeUnlockCount}" +
            (availableSame ? "" : "  MISMATCH"));
        var perStudy = Native.KnowledgePerStudy(_current!);
        var interval = Native.Interval(_current!);
        _ctx.Log($"Dev: labels | {c.Id} | knowledge per study: game '{perStudy.Text}' ({perStudy.Value:R}), overview {active.Stats.Knowledge.Value:R} | " +
            $"interval: game {interval.Seconds:R} s, overview {active.Stats.Interval.Value:R} ms | knowledge {active.Knowledge:R} | " +
            $"overview time '{row.Time()}' ({row.Milliseconds:R} ms), studied={ReferenceEquals(active, action.StudiedConstellation)}, " +
            $"timer running={action.ActionTimer.IsRunning}, ticks left={action.ActionTimer.TicksLeft}");
    }

    // What the game's node views call each node of the selected constellation, next to the node's DisplayName.
    void DevNodeNames()
    {
        if (_astrology!.Action.SelectedConstellation?.Constellation is not { } c) return;
        var names = Native.MinorNodes(_current!).GetChildren().OfType<MinorNodeView>()
            .Select(v => $"{Native.NodeOf(v)?.Id}='{Native.NodeOf(v)?.DisplayName.Text}' shows '{Native.NameOf(v).Text}'")
            .Concat(Native.MajorNodes(_current!).GetChildren().OfType<MajorNodeView>()
                .Select(v => $"{Native.NodeOf(v)?.Id}='{Native.NodeOf(v)?.DisplayName.Text}' shows '{Native.NameOf(v).Text}'"));
        _ctx.Log($"Dev: node names | {c.Id} | {string.Join(" | ", names)}");
    }

    string Snapshot(string when, AstrologyConstellation target)
    {
        var action = _astrology!.Action;
        string timer = $"studied={action.StudiedConstellation?.Constellation.Id}, " +
            $"timer running={action.ActionTimer.IsRunning}, ticks left={action.ActionTimer.TicksLeft}";
        _ctx.Log($"Dev: {when} | target={target.Id}, selected={action.SelectedConstellation?.Constellation.Id}, {timer}");
        var lines = _astrology.Config.Constellations.Select(c =>
        {
            if (!action.ActiveConstellations.TryGetValue(c, out var a)) return c.Id + " | no active constellation";
            string minors = string.Join(", ", c.MinorNodes.Where(a.IsMinorNodeUnlocked).Select(n => n.Id));
            string majors = string.Join(", ", c.MajorNodes.Where(a.IsMajorNodeUnlocked).Select(n => n.Id));
            return $"{c.Id} | knowledge={a.Knowledge:R}, minor unlocked={a.MinorNodeUnlockCount} [{minors}], " +
                $"major unlocked={a.MajorNodeUnlockCount} [{majors}], available counters={a.AvailableMinorNodeCount}/{a.AvailableMajorNodeCount}";
        }).ToArray();
        foreach (string line in lines) _ctx.Log($"Dev: {when} | {line}");
        return timer + "\n" + string.Join("\n", lines);
    }

    // The nodes unlocked and the study, without what a running study changes (knowledge, the timer's ticks).
    string DevNodes()
    {
        var action = _astrology!.Action;
        return $"studied={action.StudiedConstellation?.Constellation.Id}, running={action.ActionTimer.IsRunning}; " +
            string.Join("; ", _astrology.Config.Constellations.Select(c => action.ActiveConstellations.TryGetValue(c, out var a)
                ? $"{c.Id} [{string.Join(", ", c.MinorNodes.Where(a.IsMinorNodeUnlocked).Select(n => n.Id))}] [{string.Join(", ", c.MajorNodes.Where(a.IsMajorNodeUnlocked).Select(n => n.Id))}]"
                : c.Id + " -"));
    }

    // ---- through the game's input, as a player's hand would --------------------------------------------------------

    // A press and a release of the left button on a control (ItemEncyclopediaMod.DevMouse).
    void DevMouse(Control control)
    {
        Vector2 spot = _ctx.Tree.Root.GetFinalTransform() * control.GetGlobalRect().GetCenter();
        Input.ParseInputEvent(new InputEventMouseMotion { Position = spot, GlobalPosition = spot });
        Input.ParseInputEvent(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
        Input.ParseInputEvent(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = false });
    }

    // The pointer comes to rest on the part of a row that is in sight, and nothing is pressed.
    void DevPoint(Control control)
    {
        var visible = control.GetGlobalRect().Intersection(_scroll!.GetGlobalRect());
        if (!visible.HasArea()) throw new InvalidOperationException("The row is outside the list.");
        Vector2 spot = _ctx.Tree.Root.GetFinalTransform() * visible.GetCenter();
        Input.ParseInputEvent(new InputEventMouseMotion { Position = spot, GlobalPosition = spot });
    }

    void Shot(string state, bool crop = false)
    {
        using var image = _ctx.Tree.Root.GetTexture().GetImage();
        void Save(Image picture, string file)
        {
            Error result = picture.SavePng(Path.Combine(_ctx.Directory, file));
            if (result != Error.Ok) throw new IOException(file + ": " + result);
            _ctx.Log("Dev: " + file);
        }
        Save(image, $"shot-astrology-{state}.png");
        if (!crop) return;
        // The picture is in the screen's pixels, the window in the game's own, which the game stretches.
        var frame = _ctx.Tree.Root.GetFinalTransform() * _window!.Root.GetGlobalRect().Grow(10);
        var part = ((Rect2I)frame).Intersection(new Rect2I(0, 0, image.GetWidth(), image.GetHeight()));
        if (!part.HasArea()) throw new InvalidOperationException("The overview is outside the picture.");
        using var region = image.GetRegion(part);
        Save(region, $"crop-astrology-{state}.png");
    }
}
#endif
