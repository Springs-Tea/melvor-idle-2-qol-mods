#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Entities.Woodcutting;
using GamesByMalcs.Melvor2.GameMenus;
using GamesByMalcs.Melvor2.GameMenus.Pages;
using GamesByMalcs.Melvor2.InfoIcons;
using GamesByMalcs.Melvor2.InfoLabels;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.Modifiers;
using GamesByMalcs.Melvor2.StatDisplays;
using GamesByMalcs.Melvor2.Stats.Nodes;
using GamesByMalcs.Melvor2.Utils;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace WoodcuttingRates;

// Development: a file "woodcutting.request" in the mod's folder runs the light check below with nobody at the keyboard,
// one step about every half second, and writes "woodcutting.done" when it ends: "ok" or "failed at <step>: <why>", and
// in both cases what was put back. Every step logs what it read as one-line "Dev: ..." entries in MelvorModLoader.log.
// Made in the shape of Fishing Rates' request (mods/FishingRates/Dev.cs); the pictures are taken as Thieving Overview's
// warning tag is (mods/ThievingOverview/Dev.cs): two drawn frames after what they show was shown, and proved from their
// pixels. No simulation; about a minute when nobody moves the mouse.
//   pwsh .claude\codex\check-mod.ps1 -Worktree <worktree> -Mod WoodcuttingRates -Request woodcutting.request -Produces woodcutting.done ...
// The request file may say "fail=<step>": that step then fails on purpose, to show that a failed request puts
// everything back (-Text 'fail=click').
// It only looks, points and clicks the mod's own controls: nothing is cut, no tree is selected, hidden or changed,
// nothing is started or stopped, and the page's "Show hidden" is never touched. The one thing of the game's it changes,
// the page's category filter and the scroll of its list of trees (a click on a line, step 7, and the page's own
// category button it is compared with), it puts back, each only while it is still as the request left it. A click is
// made in two steps, as Fishing Rates' request makes them (DevPress): the pointer is put on the control, and a later step
// presses only when the game says the pointer is on that control. Nothing is pointed at or pressed while somebody is at
// the mouse: a step waits until the real pointer has been still for 5 s, and after 90 s the request ends, which puts
// everything back. The pictures go into the mod's folder (shot-woodcutting-*.png, crop-woodcutting-*.png).
//  1. Remembers the page on the screen, whether the window was open and folded, the round button, settings.json as it
//     is on disk, the page's category filter, the scroll of its list of trees, its "Show hidden", and what woodcutting
//     is doing (started, the tree being cut).
//  2. Opens the Woodcutting page the way the sidebar does if another page is open, and reads back that it is shown;
//     stops with one line when it cannot (then open it by hand and send the request again).
//  3. "Dev: strip": every round button in the strip. Closes the window if it was open, pictures the screen without it
//     (for the pixel proofs), presses the round button, opens the window up with its own button if it opened folded;
//     the round button must be lit (crop-woodcutting-strip.png).
//  4. "Dev: row": every line's numbers as the game gives them (raw) and as counted, as drawn (with colours), and its
//     tooltip. "Dev: game": for every card on the page, the game's own labels (time of a hit, hit points, XP and its
//     tooltip, the items of its two lists and which list the logs are on, its stats rows) beside ours, DIFFER on a
//     difference. "Dev: words": the page's own words for the rules the hour rests on. "Dev: effects": every overgrowth
//     effect on a tree, its stacks, its bonuses, and whether the tree's stat values hold it. "Dev: hidden": every tree not
//     listed, and why. "Dev: category": the page's category buttons against the window's CategoryOpen. "Dev: page order".
//     "Dev: lit". "Dev: cut" over every label of the window. crop-woodcutting-window.png and, when the list scrolls,
//     crop-woodcutting-window-end.png.
//  5. Rests the pointer on a line: "Dev: tooltip" and shot-woodcutting-tooltip.png. A tooltip that does not come up in
//     20 s is one line, and the request goes on.
//  6. Clicks the name "XP" over the list: "Dev: order" and crop-woodcutting-sorted.png.
//  7. "Dev: click": clicks one line, a tree whose card is under another category if there is one, else one whose card
//     is out of sight. Reads back the page's category filter, whether the card is in sight now, the lit line, the window,
//     the game's events and what woodcutting is doing before and after; CUTTING STARTED, CUTTING STOPPED, THE TREE CUT
//     CHANGED, NOT SHOWN and THE WINDOW CLOSED mark what must not happen. shot-woodcutting-clicked.png. Then puts the
//     filter and scroll back as they were before the click, presses the page's own category button for the same
//     category, and compares (DIFFER); then puts the filter and scroll back, each only if still as the request left it.
//  8. "Dev: size": the window at its smallest and at its largest size (crop-woodcutting-smallest.png,
//     crop-woodcutting-largest.png), LARGER or OVER THE BOTTOM BAR when it does not fit, and "Dev: cut" at both.
//  9. Closes the window with its X: shot-woodcutting-closed.png.
// 10. Puts back settings.json byte for byte from the copy taken in step 1, the window open or closed and folded as it
//     was, the page (only when the request opened Woodcutting and it is still on the screen), and the pointer where the
//     real one is; stops listening.
// 11. Reads all of it back from the screen, the game and the disk, and says in the last line what was put back, with
//     NOT AS IT WAS for anything that is not.
// A step that fails jumps to 10: a failed request puts everything back too.
public partial class WoodcuttingRatesMod
{
    const string RequestFile = "woodcutting.request", DoneFile = "woodcutting.done";
    // Fishing Rates' (CombatGoals/DevWindowShots.cs): how long the real pointer must be still before a step points or
    // presses, and how long a step waits for that before the request ends.
    const ulong StillFor = 5000, GiveUpAfter = 90000, StepTime = 6000, PointerStepTime = GiveUpAfter + 10000;

    readonly List<(string Name, Func<bool> Run, ulong Timeout)> _devSteps = new();
    int _devAt, _devPutBackAt;
    ulong _devStarted;
    string? _devFailure, _devFailOnPurpose, _devPutBackWords;
    bool _devPutBackOk, _devOpenedPage;
    DevBefore? _devBefore;
    DevPageEvents? _devEvents;
    // The screen without the window, and the picture before the one being taken: what the pixel proofs compare with.
    DevPicture? _devBase, _devLast;
    // The frame something was last shown in; a picture of it waits until two more have been drawn.
    long _devShownAt;
    // The page's category filter and the scroll of its list as the request left them, to put back only what is still so.
    (bool Set, WoodcuttingRecipeCategory? Category, int Scroll) _devLeft;

    // What the request found, to be put back as it was (the page, the window, settings.json), or only read back (what
    // woodcutting is doing and the page's "Show hidden", which the request never changes).
    sealed record DevBefore(GamePage? Page, bool Wanted, bool Shown, bool? Folded, bool? Lit, string? Settings,
        bool ShowHidden, bool Cutting, WoodcuttingTree? Tree);

    void DevReset()
    {
        // Another character while a request runs: its listening is let go, and the request says it stopped.
        if (_devSteps.Count > 0)
        {
            DevUnlisten();
            DevEnd("stopped: another character was opened, nothing more was put back");
        }
        if (_devHits != null) DevWatchEnd("stopped: another character was opened", summary: false);
        _devSteps.Clear();
        _devAt = 0;
    }

    void DevRequest()
    {
        if (GameAccess.IsComputingOffline || _stopped) return;
        DevWatchHand();
        string step = "";
        try
        {
            if (_devSteps.Count == 0)
            {
                string path = Path.Combine(_ctx.Directory, RequestFile);
                if (!File.Exists(path)) return;
                string asked = File.ReadAllText(path);
                File.Delete(path);
                DevPlan(asked);
                DevLog($"Woodcutting Rates check started, {_devPutBackAt} steps and the put-back; a step waits {StepTime / 1000} s at most, " +
                    $"{PointerStepTime / 1000} s when it points or presses (the request's own times, nothing of the player's).");
            }
            step = _devSteps[_devAt].Name;
            if (_devAt < _devPutBackAt && step == _devFailOnPurpose)
                throw new InvalidOperationException("failed on purpose, as the request asked, to show the put-back");
            if (!_devSteps[_devAt].Run())
            {
                if (Time.GetTicksMsec() - _devStarted >= _devSteps[_devAt].Timeout) throw new InvalidOperationException("timed out; " + DevState());
                return;
            }
            DevNext();
        }
        catch (Exception e)
        {
            DevLog($"'{step}' failed: {e.GetType().Name}: {e.Message} at {string.Join(" < ", (e.StackTrace ?? "").Split('\n').Take(3).Select(l => l.Trim()))}");
            if (_devAt < _devPutBackAt)
            {
                // What the steps before changed is put back all the same, and read back.
                _devFailure = $"failed at {step}: {e.Message}";
                _devAt = _devPutBackAt;
                _devStarted = Time.GetTicksMsec();
                _devHandSinceStep = false;
                return;
            }
            _devEvents?.Dispose();
            _devEvents = null;
            DevEnd(_devPutBackAt == 0 ? $"failed before it began: {e.Message}"
                : $"{_devFailure ?? "ok until the put-back"}; the put-back FAILED at {step}: {e.Message}");
        }
    }

    void DevNext()
    {
        _devAt++;
        _devStarted = Time.GetTicksMsec();
        _devHandSinceStep = false;
        _devAim = null;
        if (_devAt < _devSteps.Count) return;
        DevEnd($"{_devFailure ?? "ok"}; put back: {_devPutBackWords}{(_devPutBackOk ? "" : "  NOT ALL AS IT WAS")}");
    }

    void DevEnd(string result)
    {
        DevLog("Woodcutting Rates check " + result);
        _devSteps.Clear();
        _devAt = 0;
        _devBefore = null;
        _devBase?.Dispose();
        _devLast?.Dispose();
        _devBase = _devLast = null;
        File.WriteAllText(Path.Combine(_ctx.Directory, DoneFile), result);
    }

    // The mod stopped while a request ran: settings.json is put back at least, and the request says what was not.
    void DevAbandon()
    {
        DevUnlisten();
        if (_devHits != null) DevWatchEnd("stopped: the mod stopped (see its 'Stopped' line)");
        if (_devSteps.Count == 0) return;
        string settings = "settings.json left as it was";
        if (_devBefore is { } before)
        {
            if (before.Settings == null) { if (File.Exists(SettingsFile)) File.Delete(SettingsFile); }
            else ModFiles.WriteWhole(SettingsFile, before.Settings);
            settings = "settings.json put back";
        }
        DevEnd($"stopped: the mod stopped (see its 'Stopped' line); {settings}; the window, the page's category and its scroll NOT put back");
    }

    // Every listener of the requests taken off the game's events: on the put-back, and when the mod stops or another
    // character is opened (Detach).
    void DevUnlisten()
    {
        _devEvents?.Dispose();
        _devEvents = null;
        DevWatchUnlisten();
    }

    string DevState() =>
        $"page shown={PageShown}, button shown={_opener?.IsVisibleInTree()}, lit={_opener?.ButtonPressed}, window open={IsOpen}, " +
        $"folded={_window?.Minimised}, sort={_settings.Sort} {(_settings.Descending ? "descending" : "ascending")}, {DevCutting()}";

    // One entry, one line: the check keeps only the log's lines that carry the mod's name, so words of the game
    // with line breaks in them (its tooltips) are joined with " / ".
    void DevLog(string text) => _ctx.Log("Dev: " + One(text));

    static string One(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", " / ");

    void DevPlan(string asked)
    {
        _devSteps.Clear();
        _devAt = _devPutBackAt = 0;
        _devFailure = _devFailOnPurpose = _devPutBackWords = null;
        _devPutBackOk = _devOpenedPage = false;
        _devGuardSince = _devTipSince = 0;
        _devAim = null;
        _devLeft = default;
        _devBase?.Dispose();
        _devLast?.Dispose();
        _devBase = _devLast = null;
        _devStarted = Time.GetTicksMsec();
        GameView view = GameAccess.FindGameView() ?? throw new InvalidOperationException("No character is open.");
        var action = _woodcutting!.Action;
        var before = _devBefore = new DevBefore(DevNative.CurrentPage(view)?.Item1, _wanted, _window is { IsAlive: true, Visible: true },
            _window is { IsAlive: true } ? _window.Minimised : null, GodotObject.IsInstanceValid(_opener) ? _opener!.ButtonPressed : null,
            File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : null, action.ShowHiddenTrees, action.IsStarted, action.CuttingTree);
        string cuttingAtStart = DevCutting();
        DevLog($"before: page {before.Page?.Id ?? "none"}, window {(before.Wanted ? "open" : "closed")}{(before.Shown ? " and on the screen" : "")}" +
            $"{(before.Folded == true ? ", folded" : "")}, round button {(before.Lit is { } lit ? lit ? "lit" : "unlit" : "not made yet")}, settings.json " +
            (before.Settings == null ? "not there" : $"{before.Settings.Length} characters: {before.Settings}") +
            $" | the page's category filter {action.SelectedCategory?.Id ?? "none"}, its list scrolled to {DevScrollNow()?.ToString() ?? "unknown (the page is not built yet)"}, " +
            $"Show hidden {action.ShowHiddenTrees} | {cuttingAtStart} | the real pointer at {DisplayServer.MouseGetPosition()}");

        Button? target = null;
        string orderBefore = "";
        void Add(string name, Func<bool> run, ulong timeout = StepTime) => _devSteps.Add((name, run, timeout));

        Add("open the page", () =>
        {
            if (PageShown) return true;
            if (_devOpenedPage) return false;   // read back on the next ticks: the page is found once a second
            var option = _game!.Layout.Sidebar.Options.FirstOrDefault(o =>
                o.Page is EntityPage p && ReferenceEquals(p.Entity, _woodcutting) &&
                o.Requirements?.AreMet != false && o.DisplayRequirements?.AreMet != false);
            if (option?.Page is not EntityPage page)
                throw new InvalidOperationException("Woodcutting cannot be opened from the sidebar: open the Woodcutting page by hand and send the request again.");
            view.SetCurrentPage(page);
            _devOpenedPage = true;
            return false;
        });
        Add("the round button", () =>
        {
            if (!PageShown || _opener?.IsVisibleInTree() != true) return false;
            DevLog($"page | the page is {DevNative.CurrentPage(view)?.Item1?.Id ?? "none"}, the Woodcutting page is shown: {PageShown}, " +
                $"{(_devOpenedPage ? "opened by the request through the sidebar" : "it was open already")}");
            DevStrip();
            if (IsOpen) Close();
            DevShown();
            return true;
        });
        Add("the screen without the window", () =>
        {
            if (!DevDrawnTwice) return false;
            _devBase = DevPicture.OfScreen(_ctx.Tree);
            DevLog($"base | the screen without the window, {_devBase.Width}x{_devBase.Height}, for the pixel proofs of the pictures after it");
            return true;
        });
        Add("press the round button", () => DevPress(_opener!, "the round button"), PointerStepTime);
        Add("opened", () => IsOpen);
        Add("open it up", () => _window!.Minimised ? DevPress(DevFoldButton(), "the window's fold button") : true, PointerStepTime);
        Add("opened up", () =>
        {
            if (!IsOpen || _window!.Minimised || _fitPending) return false;
            if (_opener?.ButtonPressed != true) throw new InvalidOperationException("The round button is not lit while the window is open.");
            DevShown();
            return true;
        });
        Add("numbers", () =>
        {
            if (!DevDrawnTwice) return false;
            int rows = DevRows("open");
            if (rows == 0) throw new InvalidOperationException("The window lists no tree in sight.");
            DevLog($"note | under the list: '{_list!.DevNote}'");
            DevGame();
            DevWords();
            DevEffects();
            DevHidden();
            DevCategories();
            DevPageOrder();
            DevLit();
            DevSize("open");
            DevCuts("open");
            Shot("window", crop: true);
            ShotStrip();
            // The rest of the list, for the picture of its end.
            var scroll = _list.DevScroll;
            if (scroll.GetVScrollBar().MaxValue > scroll.GetVScrollBar().Page)
            {
                scroll.ScrollVertical = (int)scroll.GetVScrollBar().MaxValue;
                DevShown();
            }
            return true;
        });
        Add("the end of the list", () =>
        {
            var scroll = _list!.DevScroll;
            if (scroll.GetVScrollBar().MaxValue <= scroll.GetVScrollBar().Page) { DevLog("the list does not scroll: every line is in crop-woodcutting-window.png"); return true; }
            if (scroll.ScrollVertical == 0 || !DevDrawnTwice) return false;
            DevLog($"in sight at the end of the list: {DevInSight()}");
            Shot("window-end", crop: true);
            scroll.ScrollVertical = 0;
            return true;
        });
        Add("point at a line", () =>
        {
            if (_list!.DevScroll.ScrollVertical != 0) return false;
            var lines = _list.DevLines().ToList();
            target = (lines.FirstOrDefault(l => !l.Tree.Locked).Button ?? lines.FirstOrDefault().Button)
                ?? throw new InvalidOperationException("No line to point at.");
            _list.DevScroll.EnsureControlVisible(target);
            _devTipSince = 0;
            DevShown();
            return true;
        });
        bool tipBase = false;
        Add("tooltip", () =>
        {
            if (!DevDrawnTwice) return false;
            // The picture before the pointer rests on the line, what the tooltip's picture is compared with.
            if (!tipBase)
            {
                _devLast?.Dispose();
                _devLast = DevPicture.OfScreen(_ctx.Tree);
                tipBase = true;
            }
            return DevTooltip(target!);
        }, PointerStepTime);
        (string Key, bool Descending) sortWas = default;
        Add("click XP", () =>
        {
            if (orderBefore.Length == 0)
            {
                orderBefore = DevOrder("before, " + _settings.Sort + (_settings.Descending ? " descending" : " ascending"));
                sortWas = (_settings.Sort, _settings.Descending);
            }
            return DevPress(_list!.DevHeader("xp") ?? throw new InvalidOperationException("There is no XP column."), "the name XP");
        }, PointerStepTime);
        bool sortSeen = false;
        Add("sorted by XP", () =>
        {
            if (!sortSeen)
            {
                if (_settings.Sort != "xp" || (_settings.Sort, _settings.Descending) == sortWas) return false;
                Refresh();
                DevShown();
                sortSeen = true;
                return false;
            }
            if (!DevDrawnTwice) return false;
            DevOrder("XP, " + (_settings.Descending ? "most first" : "least first"));
            Shot("sorted", crop: true, sortedBy: "xp");
            return true;
        });

        // A click on a line: the page's category filter on the tree's category, or its list scrolled to the tree's card.
        TreeRates? clicked = null;
        string cuttingBefore = "", ours = "";
        WoodcuttingRecipeCategory? categoryBefore = null, categoryOurs = null;
        int scrollBefore = 0;
        bool clickPressed = false, beforeClick = false;
        Add("a line to click", () =>
        {
            Refresh();
            var lines = _list!.DevLines().Select(l => l.Tree).ToList();
            clicked = lines.FirstOrDefault(t => t.Clickable && t.Click == TreeRates.Opens.Category)
                ?? lines.FirstOrDefault(t => t.Clickable && t.Click == TreeRates.Opens.Scroll);
            if (clicked == null)
            {
                DevLog("click | no line's click changes the Woodcutting page (every card is shown and in sight, or the page shows every category): none is clicked. " +
                    "To check a click, choose one category on the page by hand and send the request again.");
                return true;
            }
            _list.DevScroll.EnsureControlVisible(DevLineOf(clicked));
            cuttingBefore = DevCutting();
            categoryBefore = _woodcutting!.Action.SelectedCategory;
            scrollBefore = DevScrollNow() ?? 0;
            DevLog($"click | the line of {clicked.Tree.Recipe.Id} in {clicked.Tree.Category?.Id ?? "no category"}: a click {clicked.Click} " +
                $"(its card {DevCardState(clicked.Tree)}); before: {DevPageNow()}; {cuttingBefore}");
            DevShown();
            return true;
        });
        Add("click", () =>
        {
            if (clicked == null) return true;
            if (!DevDrawnTwice) return false;
            // The picture before the click, what the picture after it is compared with.
            if (!beforeClick)
            {
                _devLast?.Dispose();
                _devLast = DevPicture.OfScreen(_ctx.Tree);
                beforeClick = true;
            }
            _devEvents ??= new DevPageEvents(_woodcutting!.Action, DevLog);
            clickPressed = DevClickLine(clicked);
            return clickPressed;
        }, PointerStepTime);
        Add("clicked", () =>
        {
            if (clicked == null) return true;
            var action = _woodcutting!.Action;
            WoodcuttingTree tree = clicked.Tree;
            TreeRates.Card? card = CardOf(tree, Cards());
            bool shown = card is { Shown: true, InSight: true };
            // The card is scrolled into sight two drawn frames after the click; the picture two more after that.
            if ((!shown || _scrollTo != null) && Time.GetTicksMsec() - _devStarted < 3000) return false;
            if (_devShownAt == 0 || _scrollTo != null) { DevShown(); return false; }
            if (!DevDrawnTwice) return false;
            Refresh();
            var lit = _list!.DevLines().Where(l => l.Tree.OnPage).Select(l => l.Tree.Tree.Recipe.Id).ToList();
            string cutting = DevCutting();
            categoryOurs = action.SelectedCategory;
            ours = DevPageNow();
            DevLog($"click | after the click on the line of {tree.Recipe.Id}: {ours}; its card {DevCardState(tree)}; the game's events [{string.Join(", ", _devEvents?.Seen ?? [])}]; {cutting}" +
                (shown ? "" : "  NOT SHOWN") + DevCuttingMarks(cuttingBefore, cutting) +
                $"; the lit line{(lit.Count == 1 ? " is" : "s are")} [{string.Join(", ", lit)}], the tree being cut {action.CuttingTree?.Recipe.Id ?? "none"}" +
                (lit.SequenceEqual(action.CuttingTree == null ? [] : [action.CuttingTree.Recipe.Id]) ? "" : "  THE LIT LINE IS NOT THE TREE BEING CUT") +
                (IsOpen ? "" : "  THE WINDOW CLOSED"));
            Shot("clicked", listOf: tree);
            _devEvents?.Dispose();
            _devEvents = null;
            DevSetLeft();
            // The page as it was before the click, for the page's own button to start from the same place.
            DevLog("click | the page's filter and scroll put back for the page's own button: " + DevPutPageBack(categoryBefore, scrollBefore, "the page's own button"));
            DevShown();
            return true;
        });
        Add("the page's own button", () =>
        {
            if (clicked == null) return true;
            if (ReferenceEquals(categoryOurs, categoryBefore))
            {
                DevLog("click | the click changed no category (it scrolled the page's list only): the page's own category button is not pressed.");
                return true;
            }
            if (!DevDrawnTwice) return false;
            _devEvents = new DevPageEvents(_woodcutting!.Action, DevLog);
            Button button = DevCategoryButton(clicked.Tree.Category) ?? throw new InvalidOperationException($"the page has no category button for {clicked.Tree.Category?.Id}");
            string how = DevPressGameButton(button);
            // What the put-back may change back, should a step after this one fail.
            DevSetLeft();
            DevLog($"click | the page's own category button '{button.Text}' of {clicked.Tree.Category?.Id} (shown={button.IsVisibleInTree()}, disabled={button.Disabled}) is pressed {how}");
            DevShown();
            return true;
        });
        Add("the page's own button compared", () =>
        {
            if (clicked == null || ReferenceEquals(categoryOurs, categoryBefore)) return true;
            if (!DevDrawnTwice) return false;
            var action = _woodcutting!.Action;
            string cutting = DevCutting();
            DevLog($"click | after the page's own button: {DevPageNow()}; the game's events [{string.Join(", ", _devEvents?.Seen ?? [])}]; {cutting}" +
                DevCuttingMarks(cuttingBefore, cutting) + " | " +
                (ReferenceEquals(action.SelectedCategory, categoryOurs) ? "the page's category as after the click on the line"
                    : $"DIFFER from the click on the line: the page's button left {action.SelectedCategory?.Id ?? "none"}, the click {categoryOurs?.Id ?? "none"}"));
            _devEvents?.Dispose();
            _devEvents = null;
            DevSetLeft();
            DevLog("click | the page's filter and scroll put back: " + DevPutPageBack(categoryBefore, scrollBefore, "the click"));
            return true;
        });
        Add("smallest", () =>
        {
            if (!IsOpen) return false;
            Resize(GameWindow.MinScale);
            DevShown();
            return true;
        });
        Add("smallest picture", () =>
        {
            if (!IsOpen || _fitPending || !DevDrawnTwice) return false;
            DevSize("smallest");
            DevCuts("smallest");
            Shot("smallest", crop: true);
            Resize(GameWindow.MaxScale);
            DevShown();
            return true;
        });
        Add("largest picture", () =>
        {
            if (!IsOpen || _fitPending || !DevDrawnTwice) return false;
            DevSize("largest that fits");
            DevCuts("largest that fits");
            Shot("largest", crop: true);
            return true;
        });
        Rect2? lastFrame = null;
        Add("close with the X", () =>
        {
            lastFrame = _window!.Root.GetGlobalRect();
            bool pressed = DevPress(_window.Root.FindChildren("*", "Button", true, false).OfType<Button>().First(b => b.Text == "X"), "the window's X");
            if (pressed) _devShownAt = 0;
            return pressed;
        }, PointerStepTime);
        Add("closed with the X", () =>
        {
            if (IsOpen || _window is { IsAlive: true, Visible: true }) return false;
            if (_opener?.ButtonPressed != false) throw new InvalidOperationException("The round button is still lit after the X.");
            if (_devShownAt == 0) { DevShown(); return false; }
            if (!DevDrawnTwice) return false;
            Shot("closed", closedAt: lastFrame);
            return true;
        });

        _devPutBackAt = _devSteps.Count;
        Add("put back", () =>
        {
            // The light check's own listeners only: a watch that runs beside it keeps its own.
            _devEvents?.Dispose();
            _devEvents = null;
            // A click was made but a step failed before it was read back: what the page shows now is taken as the click's.
            if (clickPressed && !_devLeft.Set)
            {
                DevSetLeft();
                DevLog("put back | the click on a line was made, and not read back: the page's filter and scroll as they are now are taken as the click's");
            }
            string page = _devLeft.Set ? DevPutPageBack(categoryBefore, scrollBefore, "the request") : "the page's category and scroll were not changed by the request";
            DevLog("put back | the page's filter and scroll: " + page);
            if (before.Settings == null) { if (File.Exists(SettingsFile)) File.Delete(SettingsFile); }
            else ModFiles.WriteWhole(SettingsFile, before.Settings);
            _settings = ModFiles.ReadJson<Settings>(SettingsFile, _ctx.Log);
            _settings.Sort ??= RatesList.NameKey;
            _window?.Free();
            _window = null;
            _list = null;
            _fitPending = false;
            _wanted = before.Wanted;
            // The page only when the request opened Woodcutting and it is still the page on the screen: a page the player
            // opened meanwhile is his.
            GamePage? now = DevNative.CurrentPage(view)?.Item1;
            if (_devOpenedPage && before.Page != null && PageShown && !ReferenceEquals(now, before.Page)) view.SetCurrentPage(before.Page);
            else if (_devOpenedPage && !PageShown) DevLog($"put back | the page is {now?.Id ?? "none"} now, not Woodcutting: the player opened it meanwhile, so it is left");
            Sync();
            SyncStripButton();
            DevPointerBack();
            return true;
        });
        Add("read back", () =>
        {
            // The page changes on the game's own time: read back once it has, or after a few seconds anyway.
            GamePage? page = DevNative.CurrentPage(view)?.Item1;
            if (_devOpenedPage && before.Page != null && !ReferenceEquals(page, before.Page) && Time.GetTicksMsec() - _devStarted < 4000) return false;
            var said = new List<string>();
            bool ok = true;
            void Say(bool same, string what, string now)
            {
                ok &= same;
                said.Add(same ? $"{what} as it was" : $"{what} NOT AS IT WAS (now {now})");
            }
            Say(before.Page == null || ReferenceEquals(page, before.Page), $"the page {before.Page?.Id ?? "none"}", page?.Id ?? "none");
            bool shown = _window is { IsAlive: true, Visible: true };
            Say(_wanted == before.Wanted && shown == before.Shown,
                $"the window {(before.Wanted ? "open" : "closed")}{(before.Shown ? " on the screen" : "")}", $"{(_wanted ? "open" : "closed")}{(shown ? " on the screen" : "")}");
            if (before.Shown && shown) Say(_window!.Minimised == before.Folded, $"folded {before.Folded}", $"folded {_window.Minimised}");
            bool lit = GodotObject.IsInstanceValid(_opener) && _opener!.ButtonPressed;
            Say(lit == (before.Lit ?? false), $"the round button {(before.Lit == true ? "lit" : "unlit")}", lit ? "lit" : "unlit");
            string? settings = File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : null;
            Say(settings == before.Settings, before.Settings == null ? "settings.json not there" : "settings.json byte for byte",
                settings == null ? "not there" : $"{settings.Length} characters");
            var action = _woodcutting!.Action;
            if (_devLeft.Set)
            {
                Say(ReferenceEquals(action.SelectedCategory, categoryBefore), $"the page's category filter {categoryBefore?.Id ?? "none"}", action.SelectedCategory?.Id ?? "none");
                int? scroll = DevScrollNow();
                Say(scroll == null || scroll == scrollBefore, $"the scroll of the page's list {scrollBefore}", scroll?.ToString() ?? "unknown");
            }
            else said.Add("the page's category filter and scroll not changed by the request");
            Say(action.ShowHiddenTrees == before.ShowHidden, $"the page's Show hidden {before.ShowHidden}", action.ShowHiddenTrees.ToString());
            string cuttingNow = DevCutting();
            Say(cuttingNow == cuttingAtStart, $"woodcutting: {cuttingAtStart}", cuttingNow);
            Say(_devEvents == null, "the game's events no longer listened to by the light check", "still listened to");
            said.Add("the pointer back where the real one is");
            _devPutBackWords = string.Join("; ", said) + $"; the order before the sort was {(orderBefore.Length > 0 ? orderBefore : "not read")}";
            _devPutBackOk = ok;
            DevLog("put back: " + _devPutBackWords);
            return true;
        });

        foreach (string line in asked.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("fail=")) { DevLog($"the request's line '{line}' is not understood and left out."); continue; }
            string name = line["fail=".Length..].Trim();
            if (_devSteps.Take(_devPutBackAt).Any(s => s.Name == name))
            {
                _devFailOnPurpose = name;
                DevLog($"the request asks the step '{name}' to fail on purpose, to show the put-back.");
            }
            else DevLog($"no step is called '{name}', so none fails on purpose. The steps: {string.Join(", ", _devSteps.Take(_devPutBackAt).Select(s => s.Name))}.");
        }
        _devShownAt = 0;
    }

    // ---- the frames drawn since something was shown (lesson 43) -----------------------------------------------------------

    // A picture is the frame the game drew last: one of something just shown waits until two frames were drawn since.
    void DevShown() => _devShownAt = Engine.GetFramesDrawn();

    bool DevDrawnTwice => _devShownAt > 0 && Engine.GetFramesDrawn() >= _devShownAt + 2;

    Button DevFoldButton() => _window!.Root.FindChildren("*", "Button", true, false).OfType<Button>()
        .FirstOrDefault(b => b.TooltipText is "Fold down to the title" or "Open the window again")
        ?? throw new InvalidOperationException("The window has no fold button.");

    // ---- the Woodcutting page: what woodcutting is doing, its filter and scroll ------------------------------------------

    // What woodcutting is doing: no step of the request may change it.
    string DevCutting()
    {
        var action = _woodcutting!.Action;
        bool active = _game!.ActionManager.ActiveActions.Any(a => ReferenceEquals(a, action));
        return $"woodcutting: started {action.IsStarted}, active {active}, the tree being cut {action.CuttingTree?.Recipe.Id ?? "none"}";
    }

    static string DevCuttingMarks(string before, string after)
    {
        if (before == after) return "";
        string Part(string all, string key) => Regex.Match(all, key + @" ([\w:]+)").Groups[1].Value;
        string marks = "";
        if (Part(before, "started") == "False" && Part(after, "started") == "True") marks += "  CUTTING STARTED";
        if (Part(before, "started") == "True" && Part(after, "started") == "False") marks += "  CUTTING STOPPED";
        if (Part(before, "being cut") != Part(after, "being cut")) marks += "  THE TREE CUT CHANGED";
        return marks.Length > 0 ? marks : "  WOODCUTTING CHANGED";
    }

    // The scroll of the page's list of trees, or null when the page has none yet.
    int? DevScrollNow()
    {
        if (_page == null || !GodotObject.IsInstanceValid(_page)) return null;
        try { return DevNative.CardScroll(_page) is { } scroll && GodotObject.IsInstanceValid(scroll) ? scroll.ScrollVertical : null; }
        catch (Exception e) { DevLog("page | the page's list could not be read: " + e.Message); return null; }
    }

    string DevPageNow()
    {
        var action = _woodcutting!.Action;
        return $"the page's category filter {action.SelectedCategory?.Id ?? "none"}, its list scrolled to {DevScrollNow()?.ToString() ?? "unknown"}";
    }

    string DevCardState(WoodcuttingTree tree) => CardOf(tree, Cards()) is { } c
        ? $"{(c.Shown ? "shown" : "not shown")}{(c.Shown ? c.InSight ? ", in sight" : ", out of sight" : "")}"
        : "not read";

    // The page's filter and scroll as the request leaves them now: what the put-back may change back.
    void DevSetLeft() => _devLeft = (true, _woodcutting!.Action.SelectedCategory, DevScrollNow() ?? 0);

    // The page's filter and its list's scroll back to these, each only while it is still as the request left it: one the
    // player changed meanwhile is his, and left.
    string DevPutPageBack(WoodcuttingRecipeCategory? category, int scroll, string after)
    {
        var action = _woodcutting!.Action;
        var said = new List<string>();
        if (!ReferenceEquals(action.SelectedCategory, _devLeft.Category)) said.Add($"the category filter is {action.SelectedCategory?.Id ?? "none"}, not as {after} left it: changed meanwhile, LEFT");
        else if (!ReferenceEquals(action.SelectedCategory, category))
        {
            action.SelectedCategory = category;
            said.Add($"the category filter back to {category?.Id ?? "none"}");
        }
        else said.Add($"the category filter {category?.Id ?? "none"} as it was");
        int? now = DevScrollNow();
        if (now == null) said.Add("the list's scroll unknown");
        else if (now != _devLeft.Scroll) said.Add($"the list's scroll is {now}, not as {after} left it ({_devLeft.Scroll}): changed meanwhile, LEFT");
        else if (now != scroll)
        {
            DevNative.CardScroll(_page!).ScrollVertical = scroll;
            said.Add($"the list's scroll back to {scroll}");
        }
        else said.Add($"the list's scroll {scroll} as it was");
        _devLeft = (true, action.SelectedCategory, DevScrollNow() ?? 0);
        return string.Join(", ", said);
    }

    // The page's own button for a category (WoodcuttingCategoryFilterView's buttons), or null.
    Button? DevCategoryButton(WoodcuttingRecipeCategory? category)
    {
        if (category == null || _page == null) return null;
        var buttons = DevNative.CategoryButtons(DevNative.Filter(_page));
        return buttons != null && buttons.TryGetValue(category, out Button? button) && GodotObject.IsInstanceValid(button) ? button : null;
    }

    // A game button pressed through the signal its own handler listens to: "pressed" when something listens to it, else
    // "toggled" (a button of a group). Says which.
    static string DevPressGameButton(Button button)
    {
        int pressed = button.GetSignalConnectionList(BaseButton.SignalName.Pressed).Count, toggled = button.GetSignalConnectionList(BaseButton.SignalName.Toggled).Count;
        if (pressed > 0 || toggled == 0)
        {
            button.EmitSignal(BaseButton.SignalName.Pressed);
            return $"through its own 'pressed' signal ({pressed} listening; 'toggled' has {toggled})";
        }
        button.EmitSignal(BaseButton.SignalName.Toggled, true);
        return $"through its own 'toggled' signal ({toggled} listening; 'pressed' has none)";
    }

    // The game's events about the page and about woodcutting, listened to while a click is made. Every handler is
    // guarded, so a fault of the request never reaches the game's tick, and the listeners are taken off on Dispose,
    // which the put-back, Stop and Detach all call (DevUnlisten).
    sealed class DevPageEvents : IDisposable
    {
        public readonly List<string> Seen = new();
        readonly List<Action> _off = new();
        readonly Action<string> _log;
        bool _failed;

        public DevPageEvents(WoodcuttingActionComponent action, Action<string> log)
        {
            _log = log;
            EventHandler<ValueChangedEventArgs<WoodcuttingRecipeCategory>> category = (_, e) => Guard(() => Seen.Add($"the page's category {e.NewValue?.Id ?? "none"}"));
            EventHandler<ValueChangedEventArgs<WoodcuttingTree>> tree = (_, e) => Guard(() => Seen.Add($"THE TREE CUT CHANGED to {e.NewValue?.Recipe.Id ?? "none"}"));
            EventHandler<ValueChangedEventArgs<bool>> hidden = (_, e) => Guard(() => Seen.Add($"SHOW HIDDEN CHANGED to {e.NewValue}"));
            EventHandler<ActionEventArgs> started = (_, _) => Guard(() => Seen.Add("CUTTING STARTED")), stopped = (_, _) => Guard(() => Seen.Add("CUTTING STOPPED"));
            action.SelectedCategoryChanged += category;
            action.CuttingTreeChanged += tree;
            action.ShowHiddenTreesChanged += hidden;
            action.Started += started;
            action.Stopped += stopped;
            _off.Add(() =>
            {
                action.SelectedCategoryChanged -= category;
                action.CuttingTreeChanged -= tree;
                action.ShowHiddenTreesChanged -= hidden;
                action.Started -= started;
                action.Stopped -= stopped;
            });
        }

        void Guard(Action read)
        {
            if (_failed) return;
            try { read(); }
            catch (Exception e)
            {
                _failed = true;
                _log($"event | reading the game's event failed, the rest are not read: {e.GetType().Name}: {e.Message}");
            }
        }

        public void Dispose()
        {
            foreach (Action off in _off) off();
            _off.Clear();
        }
    }

    Button DevLineOf(TreeRates tree) => _list!.DevLines().First(l => ReferenceEquals(l.Tree.Tree, tree.Tree)).Button;

    // The line is pressed the way a player's hand would (DevPress), once the list has scrolled it into sight.
    bool DevClickLine(TreeRates tree)
    {
        Button line = DevLineOf(tree);
        if (!DevSight(line).HasArea()) return false;
        bool pressed = DevPress(line, $"the line of {tree.Name}");
        if (pressed) _devShownAt = 0;
        return pressed;
    }

    // ---- what is logged ------------------------------------------------------------------------------------------

    static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    static string DevTexture(Texture2D? texture) =>
        texture == null ? "none" : $"{(texture.ResourcePath is { Length: > 0 } path ? path : "a " + texture.GetType().Name)} {texture.GetWidth()}x{texture.GetHeight()}";

    static string DevName(Control? control) => control == null ? "nothing" : $"{control.GetType().Name} '{control.Name}'";

    static string DevWords(string text) => StringUtils.StripBBCode(text).Trim();

    void DevStrip()
    {
        if (_opener?.GetParent() is not Node strip) return;
        foreach (Node child in strip.GetChildren())
            DevLog($"strip | {child.Name} | {child.GetType().Name} | shown={(child as CanvasItem)?.IsVisibleInTree()} | " +
                $"icon {DevTexture((child as Button)?.Icon)} | tooltip '{(child as Control)?.TooltipText}'");
    }

    // Every line: what the game gives (raw), what the mod counted from it, what is drawn, and its tooltip.
    int DevRows(string when)
    {
        int count = 0;
        foreach (var (t, button, drawn, picture) in _list!.DevLines())
        {
            count++;
            WoodcuttingActionStats s = t.Tree.Stats;
            string rawHit = string.Join(", ", (s.OnHitItemChances?.Values ?? []).Select(n => $"{n?.Value?.Item?.Id} {R(n?.Value?.Chance ?? double.NaN)} x{n?.Value?.Quantity}"));
            string rawCut = string.Join(", ", (s.OnCutItemChances?.Values ?? []).Select(n => $"{n?.Value?.Item?.Id} {R(n?.Value?.Chance ?? double.NaN)} x{n?.Value?.Quantity}"));
            string baseHit = string.Join(", ", t.Tree.Recipe.RandomProductsPerHit?.Select(p => $"{p.Item?.Id} {R(p.Chance)} x{p.Quantity}") ?? []);
            string baseCut = string.Join(", ", t.Tree.Recipe.RandomProductsPerCut?.Select(p => $"{p.Item?.Id} {R(p.Chance)} x{p.Quantity}") ?? []);
            DevLog($"row | {when} | {t.Tree.Recipe.Id} in {t.Tree.Category?.Id ?? "no category"} | locked={t.Locked}, level={t.Level}, needs '{t.Needs}', hidden by the player={t.HiddenByPlayer}, " +
                $"being cut={t.OnPage}, a click {t.Click} (clickable {t.Clickable}) | raw: interval {R(s.Interval.Value)} ms (the recipe's {t.Tree.Recipe.BaseInterval}), " +
                $"max health {s.MaxHealth.Value} (the recipe's {t.Tree.Recipe.BaseMaxHealth}; now {t.Tree.Health?.Current}), XP to add {R(s.Experience.XpToAdd.Value)} " +
                $"(the recipe's {t.Tree.Recipe.BaseExperience}), product {t.Tree.Recipe.Product?.Item?.Id} x{t.Tree.Recipe.Product?.Quantity} -> quantity {s.ProductQuantity.Value}, " +
                $"doubling {R(s.DoublingChance?.Total.Value ?? double.NaN)}, double damage {R(s.DoubleDamageChance?.Total.Value ?? double.NaN)}, " +
                $"weak spot {R(s.WeakSpotChance?.Total.Value ?? double.NaN)}, instant cut {R(s.InstantCutChance?.Total.Value ?? double.NaN)}, " +
                $"on hit [{rawHit}] (the recipe's [{baseHit}]), on cut [{rawCut}] (the recipe's [{baseCut}]), " +
                $"overgrowth [{string.Join(", ", s.OvergrowthStats?.List?.Select(o => $"{o.Effect?.Value?.Id} chance {R(o.ApplicationChance?.Total.Value ?? double.NaN)}") ?? [])}] | " +
                $"counted: {R(t.HitSeconds)} s a hit, {R(t.HitsPerHour)} hits/h, XP {R(t.XpEach)} a hit = {R(t.XpPerHour)}/h, hit points {t.HitPoints}, " +
                $"chances double {R(t.DoubleDamageChance)} weak {R(t.WeakSpotChance)} instant {R(t.InstantCutChance)} doubling {R(t.Doubling)}, " +
                $"logs {t.Log?.Id} x{t.LogQuantity} per hour {(t.LogsPerHour is { } l ? R(l) : "-")}, hits per cut {(t.HitsPerCut is { } h ? R(h) : "-")}, " +
                $"per hit [{string.Join(", ", t.PerHit.Select(DevDrop))}], per cut [{string.Join(", ", t.PerCut.Select(DevDrop))}], gold {(t.GoldPerHour is { } g ? R(g) : "-")}/h " +
                $"({R(t.PerHitGoldPerHour)} of it would come from the items of hits) | effects [{string.Join(", ", t.Effects.Select(e => $"{e.Name} {e.StacksLeft} left"))}] | " +
                $"picture {DevTexture(picture)} (the recipe's media {DevTexture(t.Tree.Recipe.Media)}, icon {DevTexture(t.Tree.Recipe.Icon)})" +
                (picture != null && _pictures!.IsStandIn(picture) ? "  STAND-IN DRAWN" : ""));
            DevLog($"row | {when} | drawn {drawn}");
            DevLog($"row | {when} | tooltip '{button.TooltipText}'");
        }
        var trees = ReadTrees();
        DevLog($"rows | {when} | {count} lines, {DevRowsInSight()} in sight; the game has {_woodcutting!.Action.Trees.Count} trees, {trees.Count} listed, " +
            $"{trees.Count(t => t.Locked)} locked, {trees.Count(t => t.HiddenByPlayer)} hidden by the player (Show hidden {_woodcutting.Action.ShowHiddenTrees})" +
            (count != trees.Count ? "  MISMATCH" : ""));
        return DevRowsInSight();
    }

    static string DevDrop(TreeRates.Drop d) =>
        $"{d.Item.Id} chance {R(d.Chance)} x{d.Quantity} each {R(d.Each)} = {(d.PerHour is { } p ? R(p) : "-")}/h, sells {d.Item.SellsFor?.Quantity} {d.Item.SellsFor?.Currency?.Id}, " +
        $"gold {(d.GoldPerHour is { } g ? R(g) : "-")}/h";

    string DevOrder(string when)
    {
        string order = string.Join(", ", _list!.DevLines().Select(l => l.Tree.Tree.Recipe.LocalId));
        DevLog($"order | {when} | {order}");
        return order;
    }

    string DevInSight()
    {
        Rect2 sight = _list!.DevScroll.GetGlobalRect();
        return string.Join(", ", _list.DevLines().Where(l => l.Button.IsVisibleInTree() && l.Button.GetGlobalRect().Intersects(sight)).Select(l => l.Tree.Tree.Recipe.LocalId));
    }

    int DevRowsInSight()
    {
        Rect2 sight = _list!.DevScroll.GetGlobalRect();
        return _list.DevLines().Count(l => l.Button.IsVisibleInTree() && l.Button.GetGlobalRect().Intersects(sight));
    }

    // Pictures and words drawn inside a part of the game's screen, in order: items by their id, XP icons, labels.
    static string DevDrawn(Node? box)
    {
        if (box == null || !GodotObject.IsInstanceValid(box)) return "none";
        var found = new List<string>();
        void Walk(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is ItemInfoIcon item) found.Add($"[item {item.Item?.Id}{(item.IsVisibleInTree() ? "" : " (hidden)")}: {DevLabels(item)}]");
                else if (child is SkillExperienceIcon xp) found.Add($"[XP{(xp.IsVisibleInTree() ? "" : " (hidden)")}: {DevLabels(xp)}]");
                else if (child is TextureRect picture) found.Add($"{DevTexture(picture.Texture)}{(picture.IsVisibleInTree() ? "" : " (hidden)")}");
                else if (child is Label label) found.Add($"'{label.Text}'{(label.IsVisibleInTree() ? "" : " (hidden)")}");
                else if (child is RichTextLabel rich) found.Add($"'{DevWords(rich.Text)}'{(rich.IsVisibleInTree() ? "" : " (hidden)")}");
                if (child is not (ItemInfoIcon or SkillExperienceIcon)) Walk(child);
            }
        }
        Walk(box);
        return string.Join(", ", found);
    }

    static string DevLabels(Node node) => string.Join(" ", node.FindChildren("*", "Label", true, false).OfType<Label>().Where(l => l.IsVisibleInTree() && l.Text != "").Select(l => $"'{l.Text}'"));

    // The game's own labels for every tree card on the page, and for its big panel, next to ours.
    void DevGame()
    {
        var cards = Cards();
        if (cards == null) { DevLog("game | the page's cards could not be read"); return; }
        var ours = new Dictionary<WoodcuttingTree, TreeRates>(ReferenceEqualityComparer.Instance);
        foreach (TreeRates t in ReadTrees()) ours[t.Tree] = t;
        var types = _page!.FindChildren("*", "", true, false);
        DevLog($"game | the page shows {types.OfType<WoodcuttingTreeSelectView>().Count()} WoodcuttingTreeSelectView cards " +
            $"({types.OfType<WoodcuttingTreeSelectView>().Count(c => c.IsVisibleInTree())} on show) and {types.OfType<WoodcuttingTreeView>().Count()} WoodcuttingTreeView cards; " +
            $"its own dictionary has {cards.Count} cards");
        foreach (var (tree, card) in cards)
        {
            try { DevCard(tree, card, ours.GetValueOrDefault(tree)); }
            catch (Exception e) { DevLog($"game | card of {tree.Recipe.Id} could not be read: {e.GetType().Name}: {e.Message}"); }
        }
        try { DevBigPanel(); }
        catch (Exception e) { DevLog($"game | the big panel could not be read: {e.GetType().Name}: {e.Message}"); }
    }

    void DevCard(WoodcuttingTree tree, WoodcuttingTreeSelectView card, TreeRates? ours)
    {
        TreeRates t = ours ?? TreeRates.Read(tree, _woodcutting!, CardOf(tree, Cards()), Shown);
        IntervalDisplay interval = DevNative.Interval(card);
        int hp = DevNative.HitPoints(card).Value;
        SkillXPPanel xp = DevNative.Xp(card);
        Int32FormattedLabel xpLabel = DevNative.XpLabel(xp);
        int down = (int)Math.Floor(t.XpEach + 1e-9);
        string xpSame = xpLabel.Value == down ? Math.Abs(t.XpEach - down) < 1e-9 ? "the same" : "the tag is ours rounded down" : "DIFFER";
        string xpWords = DevTooltipText(DevNative.XpTooltip(xp));
        var bound = DevNative.XpInterval(xp);
        GrantsContainer perHit = DevNative.PerHit(card), perCut = DevNative.PerCut(card);
        string logsOn = DevWhereIs(t.Log, perHit, perCut);
        // The items of each list of the card, the logs left out, against ours.
        string Items(GrantsContainer list, List<TreeRates.Drop> mine, string name)
        {
            var shown = (list.IconsContainer?.FindChildren("*", "", true, false).OfType<ItemInfoIcon>() ?? []).Where(i => i.Item != null && !ReferenceEquals(i.Item, t.Log))
                .Select(i => i.Item!.Id).Distinct().Order().ToList();
            var ours = mine.Select(d => d.Item.Id).Distinct().Order().ToList();
            return shown.SequenceEqual(ours) ? $"the {name} items as ours" : $"the {name} items DIFFER: the card's [{string.Join(", ", shown)}], ours [{string.Join(", ", ours)}]";
        }
        string xpOn = DevXpIn(perHit) ? "the per-hit list" : DevXpIn(perCut) ? "the per-cut list" : "neither list";
        DevLog($"game | card {tree.Recipe.Id} '{DevNative.Name(card).Text}' in '{DevNative.CategoryName(card).Text}' | shown={card.IsVisibleInTree()}, {DevCardState(tree)}, " +
            $"unlocked part shown={DevNative.Unlocked(card).IsVisibleInTree()}, locked part shown={DevNative.LockedPart(card).IsVisibleInTree()} (ours locked={t.Locked}) | " +
            $"its tree {(ReferenceEquals(DevNative.Tree(card), tree) ? "is the dictionary's" : "IS ANOTHER")} | " +
            $"time: game {R(interval.Seconds)} s '{DevNative.IntervalLabel(interval).Text}', ours {R(t.HitSeconds)} s{(Math.Abs(interval.Seconds - t.HitSeconds) < 0.0005 ? "" : "  DIFFER")} | " +
            $"hit points: game {hp} '{DevNative.HitPoints(card).Text}', ours {t.HitPoints}{(hp == t.HitPoints ? "" : "  DIFFER")} | " +
            $"XP: game '{xpLabel.Text}' ({xpLabel.Value}), ours {R(t.XpEach)} a hit: {xpSame}; the XP tag is bound to " +
            $"{(bound == null ? "no time" : ReferenceEquals(bound, tree.Stats.Interval) ? "the tree's time of a hit (WoodcuttingActionStats.Interval)" : $"another time ({R(bound.Value)} ms)")}; " +
            $"its tooltip '{xpWords}' | {DevXpPerSecond(xpWords, t)} | " +
            $"per hit: [{DevDrawn(perHit.IconsContainer)}] | per cut: [{DevDrawn(perCut.IconsContainer)}] | the logs ({t.Log?.Id}) are on {logsOn}, the XP on {xpOn}; " +
            $"{Items(perHit, t.PerHit, "per-hit")}; {Items(perCut, t.PerCut, "per-cut")} | " +
            $"its picture {DevTexture(DevNative.Picture(card).Texture)}, ours {DevTexture(t.Picture)}" +
            (DevNative.Picture(card).Texture is { } shown && t.Picture is { } mine && !ReferenceEquals(shown, mine) && shown.ResourcePath != mine.ResourcePath ? "  MISMATCH" : "") +
            $" | its overgrowth [{DevEffectIcons(DevNative.Overgrowth(card))}]");
        DevStatRows($"card {tree.Recipe.Id}", DevNative.Stats(card), tree);
    }

    // The big panel: the tree being cut, its time, health, XP and the two lists of what a hit and a cut bring.
    void DevBigPanel()
    {
        ActiveTreeView panel = DevNative.BigPanel(_page!);
        WoodcuttingTree? tree = DevNative.Tree(panel);
        DevLog($"game | the big panel: no tree panel shown={DevNative.NoTree(panel).IsVisibleInTree()}, its tree {tree?.Recipe.Id ?? "none"} '{DevNative.Name(panel).Text}' " +
            $"(the tree being cut {_woodcutting!.Action.CuttingTree?.Recipe.Id ?? "none"}) | time '{DevNative.IntervalLabel(DevNative.Interval(panel)).Text}' ({R(DevNative.Interval(panel).Seconds)} s) | " +
            $"health '{DevNative.Health(panel).Text}' ({DevNative.Health(panel).Current} of {DevNative.Health(panel).Maximum}) | " +
            $"XP '{DevNative.XpLabel(DevNative.Xp(panel)).Text}', its tooltip '{DevTooltipText(DevNative.XpTooltip(DevNative.Xp(panel)))}' | " +
            $"per hit: [{DevDrawn(DevNative.PerHit(panel).IconsContainer)}], its 'nothing' label '{DevNative.PerHitNothing(panel).Text}' shown={DevNative.PerHitNothing(panel).IsVisibleInTree()} | " +
            $"per cut: [{DevDrawn(DevNative.PerCut(panel).IconsContainer)}] | every word of the panel: {DevDrawn(panel)}");
        if (tree != null) DevStatRows("the big panel", DevNative.Stats(panel), tree);
    }

    // Where the logs are on a card: on its per-hit list, its per-cut list, both or neither.
    static string DevWhereIs(GamesByMalcs.Melvor2.Data.Items.Item? log, GrantsContainer perHit, GrantsContainer perCut)
    {
        if (log == null) return "no list (the tree has no product)";
        bool In(GrantsContainer list) => list.IconsContainer?.FindChildren("*", "", true, false).OfType<ItemInfoIcon>().Any(i => ReferenceEquals(i.Item, log)) == true;
        bool hit = In(perHit), cut = In(perCut);
        return hit && cut ? "BOTH LISTS" : hit ? "the per-hit list (LOGS ON THE PER-HIT LIST)" : cut ? "the per-cut list (LOGS ON THE PER-CUT LIST)" : "NEITHER LIST";
    }

    static bool DevXpIn(GrantsContainer list) => list.IconsContainer?.FindChildren("*", "", true, false).OfType<SkillExperienceIcon>().Any() == true;

    static string DevTooltipText(GamesByMalcs.Melvor2.Tooltips.TextTooltip? tooltip) =>
        tooltip == null ? "" : DevWords(string.Join(" / ", DevNative.Content(tooltip) ?? []));

    // Which rule the game's own XP tooltip follows: every number in it against the XP per second over the time of a hit
    // (ours: XP on every hit), as Fishing Rates' DevXpPerSecond reads its tag.
    static string DevXpPerSecond(string tooltip, TreeRates ours)
    {
        var numbers = Regex.Matches(tooltip, @"\d[\d,]*(?:\.\d+)?").Select(m => double.Parse(m.Value.Replace(",", ""), CultureInfo.InvariantCulture)).ToList();
        if (!ours.HasTime) return $"numbers in it [{string.Join(", ", numbers.Select(R))}]";
        double perHit = ours.XpEach / ours.HitSeconds;
        bool Near(double value) => numbers.Any(n => Math.Abs(n - value) <= Math.Max(0.006, 0.002 * value));
        return $"numbers in it [{string.Join(", ", numbers.Select(R))}]; XP per second with XP on every hit {R(perHit)}: " +
            (Near(perHit) ? "IN IT, the game counts the XP on every hit" : "NOT IN IT") + $"; ours a hit {R(ours.XpEach)} is {(Near(ours.XpEach) ? "in it" : "not in it")}";
    }

    // The rows of a list of a tree's bonuses: each stat's name and words as the page writes them, its value as written
    // and as the game keeps it, against the tree's own stat.
    void DevStatRows(string where, WoodcuttingActionStatsView? list, WoodcuttingTree tree)
    {
        if (list == null || !GodotObject.IsInstanceValid(list)) { DevLog($"stats | {where}: no list"); return; }
        var rows = list.FindChildren("*", "", true, false).OfType<ModifierStatLabel>().ToList();
        if (rows.Count == 0) { DevLog($"stats | {where}: the list has no rows (it is filled when it is shown)"); return; }
        WoodcuttingActionStats s = tree.Stats;
        var mine = new Dictionary<ModifierStatNodes, string>(ReferenceEqualityComparer.Instance);
        foreach (var (name, nodes) in new[] { ("double damage", s.DoubleDamageChance), ("weak spot", s.WeakSpotChance), ("instant cut", s.InstantCutChance), ("doubling", s.DoublingChance),
                     ("interval %", s.PercentInterval), ("interval flat", s.FlatInterval), ("max health %", s.PercentMaxHealth), ("max health flat", s.FlatMaxHealth),
                     ("product quantity", s.FlatProductQuantity), ("XP %", s.Experience.PercentXPGained), ("XP flat", s.Experience.FlatXPGained) })
            if (nodes != null) mine.TryAdd(nodes, name);
        DevLog($"stats | {where}: " + string.Join(" | ", rows.Select(r =>
        {
            var nodes = DevNative.RowStat(r);
            double shown = DevNative.RowValue(r).Value;
            // The unit of a chance, as Fishing Rates' DevChanceSame tells it: the row's number against the game's stat.
            string unit = nodes == null ? "" : Math.Abs(shown - nodes.Total.Value) < 1e-6 ? ", the row shows the stat as it is"
                : Math.Abs(shown - nodes.Total.Value * 100) < 1e-4 ? ", the row shows the stat times 100 (percent)" : ", the row's number DIFFERS from the stat";
            string of = nodes == null ? "no stat" : mine.TryGetValue(nodes, out string? n) ? $"the tree's {n} ({R(nodes.Total.Value)}){unit}" : "ANOTHER TREE'S OR NONE OF OURS";
            return $"'{DevWords(r.StatName ?? "")}' value '{DevNative.RowValue(r).Text}' ({R(DevNative.RowValue(r).Value)}), format {r.ValueFormat}, shown={r.IsVisibleInTree()}, " +
                $"{of}, its words '{DevWords(r.StatTooltipContent ?? "")}'";
        })));
    }

    static string DevEffectIcons(Node? box)
    {
        if (box == null || !GodotObject.IsInstanceValid(box)) return "none";
        return string.Join(", ", box.GetChildren().OfType<OvergrowthEffectIcon>().Select(i =>
            $"'{DevNative.EffectName(i).Text}' stacks '{DevNative.EffectStacks(i).Text}' left '{DevNative.EffectStacksLeft(i).Text}' shown={i.IsVisibleInTree()}"));
    }

    // The page's own words for the rules the hour rests on: the words it gives a tree's chances, its doubling and its XP
    // tag, those of the game's other tree card's icons, and every list of a tree's bonuses on the page.
    void DevWords()
    {
        DevLog("words | the stats list's double damage " + DevNative.Said(() => DevNative.DoubleDamageTitle(null)) + ": " + DevNative.Said(() => DevNative.DoubleDamageTip(null)) +
            " | weak spot " + DevNative.Said(() => DevNative.WeakSpotTitle(null)) + ": " + DevNative.Said(() => DevNative.WeakSpotTip(null)) +
            " | instant cut " + DevNative.Said(() => DevNative.InstantCutTitle(null)) + ": " + DevNative.Said(() => DevNative.InstantCutTip(null)) +
            " | doubling " + DevNative.Said(() => DevNative.DoublingTitle(null)) + ": " + DevNative.Said(() => DevNative.DoublingTip(null)));
        DevLog("words | the XP tag " + DevNative.Said(() => DevNative.XpGained(null)) + ", " + DevNative.Said(() => DevNative.XpPerSecond(null)) +
            " | the other tree card's doubling icon " + DevNative.Said(() => DevNative.IconName((DoublingIcon?)null)) + ": " + DevNative.Said(() => DevNative.IconWords((DoublingIcon?)null)) +
            " | its double damage icon " + DevNative.Said(() => DevNative.IconName((TreeDoubleDamageIcon?)null)) + ": " + DevNative.Said(() => DevNative.IconWords((TreeDoubleDamageIcon?)null)) +
            " | its fell chance icon " + DevNative.Said(() => DevNative.IconName((TreeFellChanceIcon?)null)) + ": " + DevNative.Said(() => DevNative.IconWords((TreeFellChanceIcon?)null)));
        DevLog($"words | the game's two numbers of a weak spot: BaseWeakSpotDamage {WoodcuttingActionComponent.BaseWeakSpotDamage}, " +
            $"MaxHitpointsWeakSpotDamage {R(WoodcuttingActionComponent.MaxHitpointsWeakSpotDamage)}; how they make a weak spot's damage, and an ordinary hit's damage, " +
            "are not values: the watch (woodcuttingwatch.request) measures them");
        var lists = _page!.FindChildren("*", "", true, false).OfType<WoodcuttingActionStatsView>().ToList();
        DevLog($"words | {lists.Count} lists of a tree's bonuses on the page, {lists.Count(l => l.FindChildren("*", "", true, false).OfType<ModifierStatLabel>().Any())} of them filled; " +
            $"the chances' names read for the tooltip: {string.Join(", ", new[] { TreeRates.DoubleDamage, TreeRates.WeakSpot, TreeRates.InstantCut }.Select(k => $"{k} {(_bonusNames.Of(k) is { } n ? $"'{n}'" : "NOT READ")}"))}");
    }

    // Every overgrowth effect on a tree now: its stacks, its bonuses in the game's words, and whether the tree's own
    // stats hold it (a source of one of them is the effect): the numbers of the window are the tree's stats.
    void DevEffects()
    {
        var action = _woodcutting!.Action;
        int found = 0;
        foreach (WoodcuttingTree tree in action.Trees.Values)
        {
            foreach (ActiveOvergrowthEffect effect in tree.ActiveEffects?.Values ?? Enumerable.Empty<ActiveOvergrowthEffect>())
            {
                found++;
                WoodcuttingActionStats s = tree.Stats;
                var held = new List<string>();
                foreach (var (name, nodes) in new[] { ("interval %", s.PercentInterval), ("interval flat", s.FlatInterval), ("max health %", s.PercentMaxHealth),
                             ("max health flat", s.FlatMaxHealth), ("product quantity", s.FlatProductQuantity), ("doubling", s.DoublingChance), ("double damage", s.DoubleDamageChance),
                             ("weak spot", s.WeakSpotChance), ("instant cut", s.InstantCutChance), ("XP %", s.Experience.PercentXPGained), ("XP flat", s.Experience.FlatXPGained) })
                {
                    foreach (ModifierEntry entry in nodes?.Sources?.Value ?? (IReadOnlySet<ModifierEntry>)new HashSet<ModifierEntry>())
                        if (ReferenceEquals(entry.Source, effect.Effect) || ReferenceEquals(entry.Source, effect) || entry.Source?.GetType().Name.Contains("Overgrowth") == true)
                            held.Add($"{name} {R(entry.Value)} from {entry.Source?.GetType().Name} '{entry.Source?.DisplayName?.Text}' (total {R(nodes!.Total.Value)})");
                }
                string bonuses = string.Join("; ", effect.Bonuses?.Component?.Select(v => DevWords(v.GetDescription(1))) ?? []);
                DevLog($"effects | {tree.Recipe.Id}: {effect.Effect?.Id} '{effect.Effect?.DisplayName.Text}', {effect.StacksLeft} stacks left, for the category " +
                    $"{effect.Effect?.AppliesToCategory?.Id ?? "any"} | its bonuses '{bonuses}' | in the tree's stats: " +
                    (held.Count > 0 ? string.Join(", ", held) : "THE EFFECT IS IN NONE OF THE TREE'S STATS") +
                    $" | the tree now: interval {R(s.Interval.Value)} ms, max health {s.MaxHealth.Value}, XP {R(s.Experience.XpToAdd.Value)}, quantity {s.ProductQuantity.Value}");
            }
        }
        DevLog($"effects | {found} overgrowth effects on the game's {action.Trees.Count} trees; the effects a cut may put on, per tree: " +
            string.Join("; ", action.Trees.Values.Where(TreeRates.Revealed).Select(t => $"{t.Recipe.LocalId} [{string.Join(", ", t.Stats.OvergrowthStats?.List?.Select(o => $"{o.Effect?.Value?.LocalId} {R(o.ApplicationChance?.Total.Value ?? 0)}") ?? [])}]")));
    }

    // Every tree of the game that is not listed, and why; and every listed one whose card the page's filter hides now.
    void DevHidden()
    {
        var action = _woodcutting!.Action;
        var listed = new HashSet<WoodcuttingTree>(ReadTrees().Select(t => t.Tree), ReferenceEqualityComparer.Instance);
        var cards = Cards();
        int hidden = 0;
        foreach (var (recipe, tree) in action.Trees)
        {
            if (listed.Contains(tree)) continue;
            hidden++;
            DevLog($"hidden | {recipe.Id} '{recipe.DisplayName.Text}' in {tree.Category?.Id} | shown by the game when: {DevConditions(recipe.DisplayRequirements)} | " +
                $"hidden by the player {tree.IsHidden}, Show hidden {action.ShowHiddenTrees} | to cut it: {DevConditions(recipe.Requirements)} | " +
                $"in the game's list {action.Config.Recipes.Contains(recipe)} | its card {(cards != null && cards.TryGetValue(tree, out var c) ? c.IsVisibleInTree() ? "SHOWN ON THE PAGE" : "not shown" : "none")}");
        }
        DevLog($"hidden | {hidden} of the game's {action.Trees.Count} trees are not listed; listed but not shown under the page's filter now: " +
            string.Join(", ", listed.Where(t => cards != null && cards.TryGetValue(t, out var c) && !c.IsVisibleInTree()).Select(t => t.Recipe.LocalId)));
    }

    static string DevConditions(ConditionGroup? group) => group == null || group.Conditions == null || group.Conditions.Count == 0 ? "always"
        : $"{(group.AreMet ? "met" : "NOT MET")}: " + string.Join("; ", group.Conditions.Select(c => $"{(c.IsMet ? "met" : "not met")}{(c.IsOr ? " (or)" : "")} '{DevWords(c.GetDescription(false, false))}'"));

    // The page's category buttons: each one's words, whether it is shown, pressed or disabled, against CategoryOpen.
    void DevCategories()
    {
        var action = _woodcutting!.Action;
        var filter = DevNative.Filter(_page!);
        var buttons = DevNative.CategoryButtons(filter) ?? new Dictionary<WoodcuttingRecipeCategory, Button>();
        var known = new HashSet<Button>(buttons.Values);
        var said = buttons.Select(b => $"{b.Key.Id} '{b.Value.Text}' shown={b.Value.IsVisibleInTree()} pressed={b.Value.ButtonPressed} disabled={b.Value.Disabled}, " +
            $"ours {(TreeRates.CategoryOpen(b.Key, action) ? "open" : "closed")}{((TreeRates.CategoryOpen(b.Key, action) == (b.Value.IsVisibleInTree() && !b.Value.Disabled)) ? "" : "  DIFFER")}");
        var others = DevNative.CategoryRow(filter).GetChildren().OfType<Button>().Where(b => !known.Contains(b))
            .Select(b => $"'{b.Text}' shown={b.IsVisibleInTree()} pressed={b.ButtonPressed} (no category: the page's 'all'?)");
        CheckBox hidden = DevNative.ShowHidden(filter);
        DevLog($"category | the page's filter {action.SelectedCategory?.Id ?? "none"} | {string.Join(" | ", said.Concat(others))} | " +
            $"Show hidden '{hidden.Text}' ticked={hidden.ButtonPressed} (the game's {action.ShowHiddenTrees}), {action.HiddenTreeCount} trees hidden by the player");
    }

    // The order of the cards on the page against the order of the lines in the page's order.
    void DevPageOrder()
    {
        var box = DevNative.CardBox(_page!);
        var page = box.GetChildren().OfType<WoodcuttingTreeSelectView>().Where(c => !c.IsQueuedForDeletion()).Select(c => DevNative.Tree(c)).OfType<WoodcuttingTree>()
            .Where(t => TreeRates.Listed(t, _woodcutting!.Action)).Select(t => t.Recipe.LocalId).ToList();
        var ours = ReadTrees().Select(t => t.Tree.Recipe.LocalId).ToList();
        DevLog($"page order | the page's cards {string.Join(", ", page)} | the window's order {string.Join(", ", ours)}" + (page.SequenceEqual(ours) ? " | the same" : "  MISMATCH"));
    }

    void DevLit()
    {
        var action = _woodcutting!.Action;
        var lit = _list!.DevLines().Where(l => l.Tree.OnPage).Select(l => l.Tree.Tree.Recipe.Id).ToList();
        var drawn = _list.DevLines().Where(l => l.Drawn.EndsWith("lit=True")).Select(l => l.Tree.Tree.Recipe.Id).ToList();
        WoodcuttingTree? panel = null;
        try { panel = DevNative.Tree(DevNative.BigPanel(_page!)); } catch { }
        DevLog($"lit | the tree being cut {action.CuttingTree?.Recipe.Id ?? "none"}, started {action.IsStarted}; the big panel shows {panel?.Recipe.Id ?? "none"} | " +
            $"the lit line{(lit.Count == 1 ? " is" : "s are")} {(lit.Count == 0 ? "none" : string.Join(", ", lit))} (drawn lit: {string.Join(", ", drawn)})" +
            (lit.SequenceEqual(action.CuttingTree == null ? [] : [action.CuttingTree.Recipe.Id]) && drawn.SequenceEqual(lit) ? "" : "  MISMATCH"));
    }

    void DevSize(string when)
    {
        var root = _window!.Root;
        Vector2 size = root.Size, screen = _ctx.Tree.Root.GetVisibleRect().Size, measured = _fullSize * _window.Style.Scale;
        float bar = FooterHeight(), bottom = root.GetGlobalRect().End.Y;
        DevLog($"size | {when} | window {size.X:0}x{size.Y:0} at {root.GetGlobalRect().Position} at {_window.Style.Scale:0.##} " +
            $"(chosen {_settings.Scale?.ToString("0.##") ?? "none"}, largest {LargestScale(bar):0.##}), {_fullSize.X:0}x{_fullSize.Y:0} at 100% as measured; " +
            $"screen {screen.X:0}x{screen.Y:0}, bottom bar {bar:0}, window bottom at {bottom:0}" +
            (size.X > measured.X + 2 || size.Y > measured.Y + 2 ? "  LARGER THAN MEASURED" : "") +
            (bottom > screen.Y - bar + 0.5f ? "  OVER THE BOTTOM BAR" : ""));
    }

    // Every label and every button with words in the window, the lines out of sight too, against the room it has, the
    // Combat Simulator's way (mods/CombatSim/CutProbe.cs, DevWords; SimViewStance.cs, DevFit): whole; CUT (a line wider
    // than its room, a name ClipText cuts, a column's name its trim shortens, a line its label's height does not show);
    // PAST THE EDGE (a number wider than its column, or anything ending past its line or the window).
    void DevCuts(string when)
    {
        var root = _window!.Root;
        var notWhole = new List<string>();
        var lineButtons = _list!.DevLines().Select(l => l.Button).ToHashSet();
        int measured = 0, cut = 0, past = 0;
        foreach (Control c in DevWordsUnder(root))
        {
            measured++;
            string words;
            bool whole;
            if (c is Label label)
            {
                Font font = label.GetThemeFont("font");
                int size = label.GetThemeFontSize("font_size");
                float Wide(string s) => font.GetStringSize(s, HorizontalAlignment.Left, -1, size).X;
                float room = label.Size.X - label.GetThemeStylebox("normal").GetMinimumSize().X;
                bool wraps = label.AutowrapMode != TextServer.AutowrapMode.Off;
                float widest = label.Text.Split('\n').Select(Wide).DefaultIfEmpty(0).Max();
                float word = label.Text.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(Wide).DefaultIfEmpty(0).Max();
                int lines = label.GetLineCount(), shown = label.GetVisibleLineCount();
                whole = shown >= lines && (wraps ? word : widest) <= room + 0.5f;
                words = $"'{One(label.Text)}' {(wraps ? word : widest):0.#} wide in {room:0.#}, {lines} line(s), {shown} shown{(label.ClipText ? ", clipped" : "")}";
            }
            else
            {
                var button = (Button)c;
                StyleBox look = button.GetThemeStylebox("normal");
                float room = button.Size.X - look.GetMargin(Side.Left) - look.GetMargin(Side.Right) - (button.Icon != null ? button.GetThemeConstant("h_separation") + button.Icon.GetWidth() : 0);
                float wide = button.GetThemeFont("font").GetStringSize(button.Text, HorizontalAlignment.Left, -1, button.GetThemeFontSize("font_size")).X;
                whole = wide <= room + 0.5f;
                words = $"the button '{button.Text}' {wide:0.#} wide in {room:0.#}";
            }
            // A number of a line is laid out in its column's width; anything else ends inside its line or the window.
            bool cell = c is Label { CustomMinimumSize.X: > 0 } && c.GetParent() is HBoxContainer;
            Control edgeOf = DevLineAround(c, lineButtons) ?? root;
            float end = c.GetGlobalRect().End.X, edge = edgeOf.GetGlobalRect().End.X;
            bool beyond = cell ? c.Size.X > c.CustomMinimumSize.X + 0.5f : end > edge + 0.5f;
            if (whole && !beyond) continue;
            if (!whole) cut++;
            if (beyond) past++;
            notWhole.Add((whole ? "" : "CUT ") + words + (beyond ? cell ? $", PAST THE EDGE of its column ({c.Size.X:0} wide where it is {c.CustomMinimumSize.X:0})" : $", PAST THE EDGE ({end:0} > {edge:0})" : ""));
        }
        DevLog($"cut | {when} | {measured} labels and buttons with words measured, the lines out of sight too, at size {_window.Style.Scale:0.##}: " +
            $"{(cut == 0 ? "none cut" : $"{cut} CUT")}, {(past == 0 ? "none past the edge" : $"{past} PAST THE EDGE")}" +
            (notWhole.Count > 0 ? " | " + string.Join(" | ", notWhole) : ""));
        DevLog($"cut | {when} | not measured: the tooltips (their words are in the 'row ... tooltip' lines), the window's X and fold buttons (pictures, no words), " +
            "the round button and the game's own screen");
    }

    // The line of the list a control is on, or null.
    static Control? DevLineAround(Control c, HashSet<Button> lines)
    {
        for (Node? node = c.GetParent(); node != null; node = node.GetParent())
            if (node is Button line && lines.Contains(line)) return line;
        return null;
    }

    static IEnumerable<Control> DevWordsUnder(Node node)
    {
        if (node is Control c && !c.IsQueuedForDeletion() && c.IsVisibleInTree() && (c is Label { Text.Length: > 0 } || c is Button { Text.Length: > 0 })) yield return c;
        foreach (Node child in node.GetChildren())
            foreach (Control inner in DevWordsUnder(child))
                yield return inner;
    }

    // ---- through the game's input, as a player's hand would (Fishing Rates' Dev.cs) ------------------------------------

    Vector2I _devPointer;
    bool _devPointerSeen, _devHandSinceStep;
    ulong _devHandAt, _devGuardSince, _devTipSince;
    Control? _devAim;
    Vector2 _devAimAt;

    // Every tick of a request: where the real pointer is, and whether a mouse button is down. Both are the system's
    // own, which the request's pushed pointer does not change (CombatGoals/DevWindowShots.cs, WatchHand).
    void DevWatchHand()
    {
        Vector2I pointer = DisplayServer.MouseGetPosition();
        if ((_devPointerSeen && pointer != _devPointer) || DisplayServer.MouseGetButtonState() != default)
        {
            _devHandAt = Time.GetTicksMsec();
            _devHandSinceStep = true;
        }
        _devPointer = pointer;
        _devPointerSeen = true;
    }

    // True while a step that points or presses waits for a hand to leave the mouse; after GiveUpAfter the request
    // ends, which puts everything back (CombatGoals/DevWindowShots.cs, HandAtMouse).
    bool DevHandAtMouse(string what)
    {
        ulong now = Time.GetTicksMsec();
        if (_devHandAt == 0 || now - _devHandAt >= StillFor)
        {
            if (_devGuardSince != 0) DevLog($"the real pointer has been still for {StillFor / 1000} s: {what} goes on after {(now - _devGuardSince) / 1000.0:0} s");
            _devGuardSince = 0;
            return false;
        }
        if (_devGuardSince == 0)
        {
            _devGuardSince = now;
            DevLog($"the real pointer moves or a mouse button is down, somebody is at the mouse: {what} waits until it has been still for {StillFor / 1000} s");
        }
        else if (now - _devGuardSince > GiveUpAfter) throw new InvalidOperationException($"somebody was at the mouse for {GiveUpAfter / 1000} s: {what} did not run");
        _devAim = null;
        return true;
    }

    // A player's click in two steps (ItemEncyclopediaMod.DevAim, DevPressAimed): the pointer is put on the part of the
    // control that is in sight, and a later tick presses only when the game says the pointer is on that control, so
    // the click can never land on a control of the game's beside it or over it. True once it was pressed.
    bool DevPress(Control target, string what)
    {
        if (!GodotObject.IsInstanceValid(target) || !target.IsVisibleInTree()) throw new InvalidOperationException($"{what} is not on the screen to be clicked.");
        if (DevHandAtMouse(what)) return false;
        Rect2 rect = DevSight(target);
        if (!rect.HasArea()) throw new InvalidOperationException($"no part of {what} is in sight.");
        if (_devAim != target || !rect.HasPoint(_devAimAt))
        {
            _devAim = target;
            _devAimAt = rect.GetCenter();
            DevPointer(_devAimAt);
            return false;
        }
        Control? over = _ctx.Tree.Root.GuiGetHoveredControl();
        if (over == null || (over != target && !target.IsAncestorOf(over)))
        {
            DevLog($"the pointer is on {DevName(over)}, not on {what}: nothing was pressed, it is put there again");
            DevPointer(_devAimAt);
            return false;
        }
        Vector2 spot = _ctx.Tree.Root.GetFinalTransform() * _devAimAt;
        Input.ParseInputEvent(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
        Input.ParseInputEvent(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = false });
        _devAim = null;
        DevLog($"pressed {what} at {_devAimAt}, the pointer on {DevName(over)}");
        return true;
    }

    // The part of a control that can be seen: on the screen, and inside the list when it is a line of it.
    Rect2 DevSight(Control control)
    {
        Rect2 rect = control.GetGlobalRect().Intersection(_ctx.Tree.Root.GetVisibleRect());
        if (_list != null && GodotObject.IsInstanceValid(_list.DevScroll) && _list.DevScroll.IsAncestorOf(control))
            rect = rect.Intersection(_list.DevScroll.GetGlobalRect());
        return rect;
    }

    // The pointer rests on a line and the game's tooltip comes up. A hand at the mouse moves it away: then it waits
    // and points again. A tooltip that has not come up in 20 s of pointing is one line, and the request goes on.
    bool DevTooltip(Button target)
    {
        if (DevHandAtMouse("the tooltip")) { _devTipSince = 0; return false; }
        if (_devTipSince == 0) _devTipSince = Time.GetTicksMsec();
        Control? over = _ctx.Tree.Root.GuiGetHoveredControl();
        if (over == null || (over != target && !target.IsAncestorOf(over)))
        {
            Rect2 rect = DevSight(target);
            if (!rect.HasArea()) throw new InvalidOperationException("The line is outside the list.");
            DevPointer(rect.GetCenter());
            return false;
        }
        string tooltip = GameStyle.DevTooltips(_ctx.Tree.Root);
        string first = target.TooltipText.Split('\n')[0];
        if (first.Length > 0 && tooltip.Contains(first))
        {
            // The tooltip came up: its picture two drawn frames later.
            if (_devTipShownAt == 0) { _devTipShownAt = Engine.GetFramesDrawn(); return false; }
            if (Engine.GetFramesDrawn() < _devTipShownAt + 2) return false;
            _devTipShownAt = 0;
            DevLog("tooltip " + tooltip);
            Shot("tooltip", tooltip: true);
            DevPointerBack();
            return true;
        }
        if (Time.GetTicksMsec() - _devTipSince < 20000) return false;
        DevLog($"tooltip | NOT SHOWN in 20 s with the pointer on {DevName(over)}; its words are in the 'row ... tooltip' lines, and there is no picture of it");
        DevPointerBack();
        return true;
    }

    long _devTipShownAt;

    void DevPointer(Vector2 at)
    {
        Vector2 spot = _ctx.Tree.Root.GetFinalTransform() * at;
        Input.ParseInputEvent(new InputEventMouseMotion { Position = spot, GlobalPosition = spot });
    }

    // The game follows the pushed pointer until the real one moves: it is put back where the real pointer is, so no
    // tooltip of the request stays up (CombatGoals/DevStopShots.cs, PointerBack). The real pointer is not moved.
    void DevPointerBack()
    {
        Vector2 real = DisplayServer.MouseGetPosition() - DisplayServer.WindowGetPosition();
        Input.ParseInputEvent(new InputEventMouseMotion { Position = real, GlobalPosition = real });
    }

    // Said in the line of a picture taken after a hand was at the mouse.
    string HandNote => _devHandSinceStep ? "; A HAND WAS AT THE MOUSE since the step before: this picture may show what it did" : "";

    // ---- the pictures, proved from their pixels (ThievingOverview/Dev.cs, DevSeen) -------------------------------------

    /// <summary>A picture of the screen as RGBA bytes, for the pixel proofs.</summary>
    sealed class DevPicture : IDisposable
    {
        public readonly Image Image;
        public readonly int Width, Height;
        public readonly byte[] Data;

        DevPicture(Image image)
        {
            if (image.GetFormat() != Image.Format.Rgba8) image.Convert(Image.Format.Rgba8);
            Image = image;
            Width = image.GetWidth();
            Height = image.GetHeight();
            Data = image.GetData();
        }

        public static DevPicture OfScreen(SceneTree tree) => new(tree.Root.GetTexture().GetImage());

        public Color At(int x, int y)
        {
            int i = (y * Width + x) * 4;
            return Color.Color8(Data[i], Data[i + 1], Data[i + 2]);
        }

        public void Dispose() => Image.Dispose();
    }

    // Where a part of the game's screen lies in a picture of it: in the game's own pixels, stretched as the game
    // stretches the screen, cut to the picture (ThievingOverviewMod.DevPixels).
    Rect2I DevPixels(Rect2 global, DevPicture picture) =>
        ((Rect2I)(_ctx.Tree.Root.GetFinalTransform() * global)).Intersection(new Rect2I(0, 0, picture.Width, picture.Height));

    // The share of a part's pixels that differ between two pictures of the screen; -1 when they cannot be compared.
    static double DevChanged(DevPicture a, DevPicture? b, Rect2I r)
    {
        if (b == null || a.Width != b.Width || a.Height != b.Height || !r.HasArea()) return -1;
        int count = 0;
        for (int y = r.Position.Y; y < r.End.Y; y++)
            for (int x = r.Position.X; x < r.End.X; x++)
            {
                int i = (y * a.Width + x) * 4;
                // A pixel the same frame draws the same way twice; a little room for rounding.
                if (Math.Max(Math.Abs(a.Data[i] - b.Data[i]), Math.Max(Math.Abs(a.Data[i + 1] - b.Data[i + 1]), Math.Abs(a.Data[i + 2] - b.Data[i + 2]))) > 2) count++;
            }
        return count / (double)Math.Max(1, r.Size.X * r.Size.Y);
    }

    // How many of a part's pixels are drawn in a colour (the gold of the name a list is sorted by).
    static int DevInk(DevPicture p, Rect2I r, Color colour)
    {
        int count = 0;
        for (int y = r.Position.Y; y < r.End.Y; y++)
            for (int x = r.Position.X; x < r.End.X; x++)
            {
                Color c = p.At(x, y);
                if (Math.Abs(c.R - colour.R) < 0.12f && Math.Abs(c.G - colour.G) < 0.12f && Math.Abs(c.B - colour.B) < 0.12f) count++;
            }
        return count;
    }

    static string Share(double share) => share < 0 ? "cannot be compared" : $"{share:P0}";

    // A picture of the whole screen, and with crop one of the window alone, each proved from its pixels against the screen
    // without the window and the picture before: the window in it, the gold name the list is sorted by, the tooltip, the
    // page's list after a click, the window gone. A picture of a window that is not there, or of a list with no line in
    // sight, is not taken.
    void Shot(string state, bool crop = false, string? sortedBy = null, bool tooltip = false, WoodcuttingTree? listOf = null, Rect2? closedAt = null)
    {
        if (!Regex.IsMatch(state, "^[A-Za-z0-9_-]+$")) throw new InvalidOperationException($"'{state}' is no name for a picture: letters, digits, - and _ only.");
        var picture = DevPicture.OfScreen(_ctx.Tree);
        var proof = new List<string>();
        if (crop || sortedBy != null)
        {
            if (_window is not { IsAlive: true, Visible: true } window) { picture.Dispose(); throw new InvalidOperationException("The window is not on the screen for " + state + "."); }
            if (!window.Minimised && DevRowsInSight() == 0) { picture.Dispose(); throw new InvalidOperationException("No line of the list is in sight for " + state + "."); }
            Rect2I at = DevPixels(window.Root.GetGlobalRect(), picture);
            double changed = DevChanged(picture, _devBase, at);
            proof.Add(changed >= 0.25 ? $"THE WINDOW IS IN THE PICTURE: {Share(changed)} of its {at.Size.X * at.Size.Y:N0} pixels at {at} differ from the screen without it"
                : $"THE WINDOW IS NOT IN THE PICTURE: {Share(changed)} of its pixels at {at} differ from the screen without it");
            proof.Add($"{DevRowsInSight()} lines in sight: {DevInSight()}");
        }
        if (sortedBy != null && _list!.DevHeader(sortedBy) is { } header && _list.DevHeader(RatesList.NameKey) is { } name)
        {
            int gold = DevInk(picture, DevPixels(header.GetGlobalRect(), picture), GameStyle.TextGold), other = DevInk(picture, DevPixels(name.GetGlobalRect(), picture), GameStyle.TextGold);
            proof.Add((gold >= 15 && gold > 3 * other ? "THE NAME IT IS SORTED BY IS GOLD" : "THE NAME IT IS SORTED BY IS NOT GOLD") +
                $": {gold} gold pixels in '{header.Text}', {other} in '{name.Text}'");
        }
        if (tooltip && DevTooltipRect() is { } tip)
        {
            Rect2I at = DevPixels(tip, picture);
            double changed = DevChanged(picture, _devLast, at);
            proof.Add(changed >= 0.25 ? $"THE TOOLTIP IS IN THE PICTURE: {Share(changed)} of its pixels at {at} differ from the picture before it came up"
                : $"THE TOOLTIP IS NOT IN THE PICTURE: {Share(changed)} of its pixels at {at} differ from the picture before");
        }
        if (listOf != null && Cards() is { } cards && cards.TryGetValue(listOf, out var card) && ListOf(card) is { } list)
        {
            Rect2I at = DevPixels(list.GetGlobalRect(), picture);
            double changed = DevChanged(picture, _devLast, at);
            bool inSight = list.GetGlobalRect().Grow(1).Encloses(card.GetGlobalRect());
            proof.Add((changed >= 0.05 ? "THE PAGE'S LIST CHANGED IN THE PICTURE" : "THE PAGE'S LIST DID NOT CHANGE IN THE PICTURE") +
                $": {Share(changed)} of its pixels at {at} differ from the picture before the click; the card at {card.GetGlobalRect()} is {(inSight ? "inside" : "NOT INSIDE")} the list at {list.GetGlobalRect()}");
        }
        if (closedAt is { } was)
        {
            Rect2I at = DevPixels(was, picture);
            double fromLast = DevChanged(picture, _devLast, at), fromBase = DevChanged(picture, _devBase, at);
            proof.Add((fromLast >= 0.25 ? "THE WINDOW IS GONE FROM THE PICTURE" : "THE WINDOW IS STILL IN THE PICTURE") +
                $": {Share(fromLast)} of the pixels where it was differ from the picture with it, {Share(fromBase)} from the screen without it at the start");
        }
        Save(picture.Image, $"shot-woodcutting-{state}.png", proof);
        if (crop && _window is { IsAlive: true } shown)
        {
            var part = DevPixels(shown.Root.GetGlobalRect().Grow(10), picture);
            if (!part.HasArea()) { picture.Dispose(); throw new InvalidOperationException("The window is outside the picture."); }
            using var region = picture.Image.GetRegion(part);
            Save(region, $"crop-woodcutting-{state}.png", null);
        }
        _devLast?.Dispose();
        _devLast = picture;
    }

    void Save(Image image, string file, List<string>? proof)
    {
        Error result = image.SavePng(Path.Combine(_ctx.Directory, file));
        if (result != Error.Ok) throw new IOException(file + ": " + result);
        DevLog(file + (proof is { Count: > 0 } ? " | " + string.Join(" | ", proof) : "") + HandNote);
    }

    // The place of the Godot tooltip that is up, in the game's own pixels, or null.
    Rect2? DevTooltipRect()
    {
        Rect2? found = null;
        void Walk(Node node)
        {
            if (found != null) return;
            if (node is PopupPanel { Visible: true } popup) { found = new Rect2(popup.Position, popup.Size); return; }
            foreach (Node child in node.GetChildren(true)) Walk(child);
        }
        Walk(_ctx.Tree.Root);
        return found;
    }

    // The strip of round buttons around the mod's own, which must be on the screen and lit.
    void ShotStrip()
    {
        if (_opener?.GetParent() is not Control strip || !_opener.IsVisibleInTree()) throw new InvalidOperationException("The round button is not on the screen.");
        using var picture = DevPicture.OfScreen(_ctx.Tree);
        var part = DevPixels(strip.GetGlobalRect().Grow(12), picture);
        if (!part.HasArea()) throw new InvalidOperationException("The strip is outside the picture.");
        Rect2I button = DevPixels(_opener.GetGlobalRect(), picture);
        double changed = DevChanged(picture, _devBase, button);
        using var region = picture.Image.GetRegion(part);
        Save(region, "crop-woodcutting-strip.png", [$"the button at {_opener.GetGlobalRect()}, lit={_opener.ButtonPressed}, {strip.GetChildCount()} buttons in the strip; " +
            $"{Share(changed)} of the button's pixels differ from the screen when it was unlit" + (changed >= 0.05 ? " (THE LIT BUTTON IS IN THE PICTURE)" : " (THE BUTTON LOOKS AS UNLIT)")]);
    }
}
#endif
