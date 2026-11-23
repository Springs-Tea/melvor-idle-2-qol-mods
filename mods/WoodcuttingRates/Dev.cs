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
using GamesByMalcs.Melvor2.Tooltips;
using GamesByMalcs.Melvor2.Utils;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace WoodcuttingRates;

// Development: a file "woodcutting.request" in the mod's folder runs the light check below with nobody at the keyboard,
// one step about every half second, and writes "woodcutting.done" when it ends: "ok" or "failed at <step>: <why>", and
// in both cases what was put back. Every step logs what it read as one-line "Dev: ..." entries in MelvorModLoader.log.
// It is short (about a minute and a half when nobody moves the mouse): no simulation, a few pictures. A second request,
// "woodcuttingwatch.request", only listens while the character cuts a tree (DevWatch.cs); one waits for the other.
//   pwsh .claude\codex\check-mod.ps1 -Worktree <worktree> -Mod WoodcuttingRates -Request woodcutting.request -Produces woodcutting.done -Out <folder>
// The request file may say "fail=<step>": that step then fails on purpose, to show that a failed request puts
// everything back (-Text 'fail=click'). Built on Mining Rates' request (mods/MiningRates/Dev.cs), after its five turns
// and three code reviews, for the Woodcutting page.
// It only looks, points and clicks the mod's own controls and, in step 8, the Woodcutting page's own category buttons: no
// tree is selected, cut, hidden or shown, nothing is started or stopped, and the page's box for hidden trees
// (ShowHiddenTrees) is only read, before and after every press of a game button (SHOW HIDDEN CHANGED if it moved). In step
// 4 the pointer only rests on a card's parts, for the game's own tooltips. The page's category filter and the scroll of
// its list of trees change only in step 8, and are put back through the page's own buttons, never through the member
// behind them: first by the button's signal, as a click on a line presses it, and when that did not set the filter, by
// the pointer on that same button, as a player would; NOT AS IT WAS only after both. The page's "All" button is found by
// the game's own word for it (WoodcuttingCategoryFilterView.s_all), never guessed, and when it is not found the request
// does not leave the filter on a category in the first place. What is put back is the page as it was just before the
// request's own first change to it, so a filter the player chose after the request began stays his. A click is made in
// two steps (DevPress): the pointer is put on the control, and a later step presses only when the game says the pointer
// is on that control; a control of the game under the mod's own window is pointed at beside it, or not at all (said at
// once). Nothing is pointed at or pressed while somebody is at the mouse: a step waits until the real pointer has been
// still for 5 s, and after 90 s the request ends, which puts everything back. A picture of something just shown is taken
// two drawn frames later, and its pixels prove what it shows (DevChanged). The pictures go into the mod's folder
// (shot-woodcutting-*.png, crop-woodcutting-*.png).
//  1. Remembers the page on the screen, whether the window was open and folded, the round button, settings.json as it
//     is on disk, the page's category filter (the member and the page's buttons and cards as drawn), the scroll of its
//     list of trees, the box for hidden trees, and what woodcutting is doing (IsStarted, CuttingTree).
//  2. Opens the Woodcutting page the way the sidebar does, if another page is open, and reads back that it is shown;
//     stops with one line when it cannot be opened that way (then open it by hand and send the request again).
//  3. "Dev: strip": every round button in the strip. Closes the window if it was open.
//  4. "Dev: card stats": on a card wholly in sight (never the tree being cut while woodcutting runs: its hit points move
//     with every hit), rests the pointer on each part of the card the game shows a tooltip for, as a player reads them:
//     its list of bonuses (the stats icon: double damage, weak spot, instant cut, doubling, ... as the game fills them in
//     for that tree), its hit points, and its XP tag, each on the control the game's tooltip shows for (Tooltip.Targets,
//     Labelling), until the game shows it filled, and logs it word for word, with every number in it named against the
//     tree's numbers and the game's two numbers of a weak spot. shot-woodcutting-stats-tooltip.png, proved over the
//     tooltip's words where they are drawn. A tooltip that is not shown in 20 s is one line, and the request goes on.
//     These are the words the rules of the hour (TreeRates.LogsRule, HitsRule) are to be settled by.
//  5. A picture of the screen without the window, for the proofs; presses the round button, and opens the window up
//     with its own button if it opened folded; the round button must be lit.
//  6. "Dev: row": every line's numbers as the game gives them (raw) and as counted, the hits a cut takes by each reading
//     of the rule (TreeRates.ExpectedHits), as drawn (with colours), and its tooltip. "Dev: game": for every card on the
//     page, the game's own labels (interval, hit points, XP and its tooltip, the items and XP of its two lists with the
//     words over each and the tree's stats each is bound to, its picture, its overgrowth, its list of bonuses where it is
//     filled) beside ours, DIFFER on a difference, the page's own order of cards against ours, and the big panel's.
//     "Dev: words": the page's own words for the rules the hour rests on (the constants read by reflection, the member
//     named when one is not there), and the filter's words and buttons. "Dev: effects": every overgrowth effect on a tree,
//     and whether the tree's stats hold it. "Dev: hidden": every tree not listed, and why. "Dev: lit". "Dev: tips": the
//     longest line of every tip against Fishing Rates' longest. "Dev: cut" over every label of the picture.
//     crop-woodcutting-window.png and, when the list scrolls, crop-woodcutting-window-end.png. "Dev: room": the
//     window's place as drawn, the page's list, and every line's card against them (Native.Place): in sight clear of the
//     window, a scroll that shows it clear, or no room beside the window; MISMATCH when the line is clickable otherwise
//     than that says, NOT SAID when a line without room does not say so in its tooltip.
//  7. Rests the pointer on a line, one whose card has no room beside the window when there is one (its tooltip says
//     so), then on the name of the column with the longest tip: shot-woodcutting-tooltip.png,
//     shot-woodcutting-header-tooltip.png and "Dev: tooltip". A tooltip that does not come up in 20 s is one line, and the
//     request goes on. Then clicks "XP": "Dev: order" and crop-woodcutting-sorted.png.
//  8. "Dev: click": on a page whose filter is on all categories, first presses the page's own button of one category
//     with the pointer, as a player would, so that a card can be under another category (only when the page's "All"
//     button is found, to put it back). Then clicks a line, first one whose card the page shows and a scroll brings
//     clear of the mod's window, else one whose card is under another category, and reads back the page's category
//     filter, how the card stands against the window (the card, its list and the window as drawn), the lit line, the
//     window and what woodcutting is doing (shot-woodcutting-clicked.png, the card proved over its part beside the
//     window). CUTTING STARTED, CUTTING STOPPED, THE TREE CUT CHANGED, NOT SHOWN (the card is not clear of the window,
//     and the filter did not change or the line's tooltip does not say there is no room), THE WINDOW CLOSED and SHOW
//     HIDDEN CHANGED mark what must not happen; NOT CLEAR OF THE WINDOW is a card of another category the page has no
//     room for beside the window, which the line's tooltip then says. Puts the filter back (by hand if its signal did
//     not), and the scroll on its own, presses the page's own category button for the same category with the pointer
//     as a player would, and compares (DIFFER); then puts the filter and the scroll back again, each only if it is still
//     exactly what the request left (the rule of step 10). Until each of these presses is made, the page as it is then is
//     what it puts back; a press over a part the player changed since a press of the request's is not made (NOT MADE),
//     so what he chose stays.
//  9. "Dev: size": the window at its smallest and at its largest size (crop-woodcutting-smallest.png,
//     crop-woodcutting-largest.png), LARGER or OVER THE BOTTOM BAR when it does not fit, and "Dev: cut" at both. Closes
//     the window with its X: shot-woodcutting-closed.png.
// 10. Puts back settings.json byte for byte from the copy read in step 1 (sort, size, place, fold), the window open or
//     closed and folded as it was, the page's category filter (by hand if its signal did not) and its scroll two drawn
//     frames later, each only if it is still as the request left it (otherwise the player changed it, and it is left as
//     he did); reads the Woodcutting page's filter and scroll back while it is still on the screen; then the page that
//     was on the screen, only if the request opened Woodcutting itself, and the pointer where the real one is. One rule
//     for each part of the page, the filter and the scroll, each on its own: what the request left on a part is read
//     from the page once the page has settled after the request's own press (DevSettle), and at the put-back the part is
//     read again: still exactly that, it is put back; otherwise the player changed it, and it is his: left, said in one
//     line, never taken as the request's own, and it does not stop the other part's put-back. No wait on the mouse
//     decides whose a value is. The keyboard focus is taken off a control right after the request's own press of it, if
//     it has it (DevFocusOff); nothing is given back, and nothing at the end can move a list. Every part (settings.json,
//     the window, the filter, the scroll, the page, the pointer) is put back in its own try, with one line: a part that
//     fails is NOT AS IT WAS, and the next part runs all the same; a put-back step that fails is said, and the put-back
//     goes on with the next step.
// 11. Reads the rest back from the screen, the game and the disk, what woodcutting is doing too, and says in its last line
//     what was put back, with NOT AS IT WAS for anything that is not, and for any put-back on the way that was not, and
//     NOT READ BACK for what could not be read (the done line then ends NOT ALL AS IT WAS, NOT ALL READ BACK). What the
//     player changed himself is said as his, and is no fault.
// A step that fails jumps to 10: a failed request puts everything back too. If the mod stops while a request runs, or
// another character is opened, the done file says so, settings.json is written back from the copy and read again by the
// window, and the done line names where the request left the filter of its character (DevStopped, DevReset).
public partial class WoodcuttingRatesMod
{
    const string RequestFile = "woodcutting.request", DoneFile = "woodcutting.done";
    // CombatGoals/DevWindowShots.cs, Fishing Rates and Mining Rates: how long the real pointer must be still before a step
    // points or presses, and how long a step waits for that before the request ends.
    const ulong StillFor = 5000, GiveUpAfter = 90000, StepTime = 6000, PointerStepTime = GiveUpAfter + 10000;
    // How long the pointer rests on one part of a card for the game's tooltip before that tooltip is given up.
    const ulong TipWait = 20000;

    // Fishing Rates' longest tip of a column's name, line by line, and the longest line of its tooltips, in characters
    // (its Gold tip and its bait line, counted): Godot's tooltip does not wrap, so a longer line is a wider tooltip.
    const int FishingTipMost = 82, FishingTooltipMost = 94;

    readonly List<(string Name, Func<bool> Run, ulong Timeout)> _devSteps = new();
    int _devAt, _devPutBackAt;
    ulong _devStarted;
    long _devFrameMark;
    string? _devFailure, _devFailOnPurpose, _devPutBackWords;
    bool _devPutBackOk, _devPutBackRead, _devOpenedPage;
    // Put-backs on the way whose line says NOT AS IT WAS or SHOW HIDDEN CHANGED, and presses not made: the done line
    // carries them, not only the log (Mining Rates' code review of turn 3, fault 7).
    readonly List<string> _devOnTheWay = new();
    // The screen without the window, before it was opened: what the pictures of the window are proved against.
    Image? _devWithout;
    // settings.json as the request found it on disk (null: not there), for DevStopped too.
    string? _devSettingsCopy;
    bool _devSettingsRead;
    // One rule for each part of the Woodcutting page the request changes, the filter and the scroll, each on its own
    // (review 3 of the orchestrator): what the request left on a part is read from the page itself once the page has
    // settled after the request's own press (_devSettle), and kept (_devOwnFilter, _devOwnScroll; null: the request
    // owns nothing of that part). At the put-back the part is read again: still exactly what the request left, it is put
    // back; otherwise the player changed it, and it is his: left, said in one line, never taken as the request's own. A
    // part of his does not touch the other part's put-back. No wait on the mouse decides whose a value is: the press
    // guard (DevHandAtMouse) only keeps the request's presses off his hand.
    sealed record DevOwn<T>(T Value);
    DevOwn<WoodcuttingRecipeCategory?>? _devOwnFilter;
    DevOwn<int?>? _devOwnScroll;
    // The filter the owned scroll was read under: a scroll belongs to the list of one category, so once the page shows
    // another category than this one, whoever put it there, the scroll is the player's list's and is never written
    // (review 4: a category click of his after the request's filter was back).
    WoodcuttingRecipeCategory? _devScrollUnder;
    // A press of the request's that changes the page, not settled yet: the frame it was made in, and whether it was a
    // change of the request's (Both: what it left on both parts is read once it settled) or a put-back's press (only the
    // scroll the request still owns is read again: the press laid the list out anew). Read on the first tick at least two
    // drawn frames later, and once the window's own scroll to a card is done (DevSettle).
    (long Frame, bool Both)? _devSettle;
    // The tick the last settled reading was taken in: the scroll is put back on a later tick, after it is read again.
    long _devTick, _devSettledTick = -1;
    // The Woodcutting page just before the request's own press that changed its filter or scroll, while nothing of the
    // request's was left on it: what every put-back goes back to (null: the request has changed neither). The page at
    // the start of the request may differ, if the player changed it in the first steps: that change is his, and stays.
    // _devOriginWait is the page as it is on every tick until such a press is made (Mining Rates' code review of turn 3,
    // fault 6).
    DevPageWas? _devOrigin, _devOriginWait;
    // A put-back whose signal press did not set the filter: the same button, to be pressed with the pointer as a player
    // would, before the filter is said NOT AS IT WAS (Mining Rates' code review of turn 2, fault 1).
    (Button Button, WoodcuttingRecipeCategory? Filter)? _devHandBack;
    // The page's box for hidden trees just before the last press of a game button (DevHiddenMoved).
    bool _devHiddenBefore;
    // What of the page the request let go of because the player changed it ("filter", "scroll"): the read back says so,
    // and does not count it against the put-back.
    readonly HashSet<string> _devHis = new();
    // The control the request has just pressed with the pointer, and the frame: once the press has been handled, the
    // keyboard focus is taken off it if it has it, so that a key of his does not press it again (review 3: nothing is
    // given back with GrabFocus, and nothing at the end can move a list after the read back).
    (Control Target, long Frame)? _devFocusAfter;
    // The Woodcutting of the character the request runs for: when another character is opened meanwhile, the done line
    // names where the request left that one's filter (the code review of turn 2, fault 3).
    WoodcuttingActionComponent? _devAction;

    void DevReset()
    {
        // Another character while a request runs: its listening is let go, settings.json is written back from the copy
        // (the window's settings are the same for every character), and the request says what it could not put back.
        if (_devSteps.Count > 0)
        {
            DevUnwatchAll();
            string settings = DevSettingsBack();
            DevEnd($"stopped: another character was opened; put back: {settings}; not put back: {DevLeftOn()}, the window of the character before (freed " +
                "with it), its page and the pointer  NOT ALL AS IT WAS");
        }
        if (_devHits != null) DevWatchEnd("stopped: another character was opened", summarise: false);
        _devSteps.Clear();
        _devAt = 0;
        _devAction = null;
        _devSettle = null;
        _devFocusAfter = null;
    }

    // The mod stops (Stop) while a request runs: the done file says so, so that the check does not wait its whole time
    // for it, and settings.json is written back from the copy. Nothing else is touched: the game's parts may be gone.
    void DevStopped()
    {
        DevUnwatchAll();
        if (_devHits != null) DevWatchEnd("stopped: the mod stopped", summarise: false);
        if (_devSteps.Count == 0) return;
        string settings = DevSettingsBack();
        string step = _devAt < _devSteps.Count ? _devSteps[_devAt].Name : "the end";
        DevEnd($"failed at {step}: the mod stopped; put back: {settings}; not put back: {DevLeftOn()}, the window, the page and the pointer  NOT ALL AS IT WAS");
    }

    // settings.json written back byte for byte from the copy the request read at its start, and read again by the window,
    // so that a window built afterwards opens as the player had it (sort, size, place, fold).
    string DevSettingsBack()
    {
        if (!_devSettingsRead) return "settings.json not read by the request";
        try
        {
            if (_devSettingsCopy == null) File.Delete(SettingsFile);
            else ModFiles.WriteWhole(SettingsFile, _devSettingsCopy);
            _settings = ModFiles.ReadJson<Settings>(SettingsFile, _ctx.Log);
            _settings.Sort ??= RatesList.NameKey;
            return _devSettingsCopy == null ? "settings.json not there, as it was not at the start"
                : "settings.json written back byte for byte from the copy, and read again by the window";
        }
        catch (Exception e) { return $"settings.json NOT AS IT WAS: {e.GetType().Name}: {e.Message}"; }
    }

    // Where the request left the page's category filter of its own character, when it could not put it back: the game
    // keeps that filter in the character's Woodcutting (WoodcuttingActionComponent.SelectedCategory; the component is
    // written to the save, WriteTo), so it may stay there.
    string DevLeftOn()
    {
        try
        {
            if (_devAction == null) return "the page's filter (no character read)";
            string now = _devAction.SelectedCategory?.Id ?? "all";
            string was = _devOrigin is { } origin ? origin.Filter?.Id ?? "all" : "unchanged by the request";
            string left = _devOwnFilter is { } l ? $"the request had left it on {l.Value?.Id ?? "all"}" : "the request had nothing of its own left on it";
            return $"the category filter of the character the request ran for, now on {now} ({left}; before the request's first change it was {was}), " +
                "which the game may keep in that character's save, and the scroll of its list";
        }
        catch (Exception e) { return $"the page's filter (not read: {e.GetType().Name})"; }
    }

    void DevRequest()
    {
        if (GameAccess.IsComputingOffline || _stopped) return;
        // The silent watch runs on its own; a light check waits for it to end, and the other way round.
        if (_devHits != null) { DevWatchTick(); return; }
        DevWatchHand();
        _devTick++;
        // Before any step: the keyboard focus off a control the request has just pressed, and what the request's own last
        // press left on the page, once the page has settled. Both run whatever step comes next, a failed one too.
        try { DevFocusOff(); }
        catch (Exception e) { _devFocusAfter = null; DevLog($"focus | not read: {e.GetType().Name}: {e.Message}"); }
        try { DevSettle(); }
        catch (Exception e) { _devSettle = null; DevLog($"page | what the request left could not be read: {e.GetType().Name}: {e.Message}"); }
        string step = "";
        try
        {
            if (_devSteps.Count == 0)
            {
                string path = Path.Combine(_ctx.Directory, RequestFile);
                if (!File.Exists(path)) { DevWatchRequest(); return; }
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
            try { DevPointerBack(); } catch (Exception) { }
            if (_devPutBackAt == 0 || _devSteps.Count == 0)
            {
                DevUnwatchAll();
                DevEnd($"failed before it began: {e.Message}");
                return;
            }
            if (_devAt < _devPutBackAt)
            {
                // What the steps before changed is put back all the same, and read back.
                _devFailure = $"failed at {step}: {e.Message}";
                _devAt = _devPutBackAt;
                _devStarted = Time.GetTicksMsec();
                _devHandSinceStep = false;
                _devFrameMark = 0;
                _devAim = null;
                _devMisses = 0;
                return;
            }
            // A step of the put-back failed: what it puts back is NOT AS IT WAS, said once, and the put-back goes on with the
            // next step, so that one failure never leaves the rest as the request left it (the code review of turn 2,
            // fault 4). A step that fails on every tick moves on all the same.
            string words = $"the put-back's step '{step}' NOT AS IT WAS: it failed ({e.GetType().Name}: {e.Message}), and the put-back goes on";
            DevLog("put back | " + words);
            _devOnTheWay.Add(words);
            DevNext();
        }
    }

    void DevNext()
    {
        _devAt++;
        _devStarted = Time.GetTicksMsec();
        _devHandSinceStep = false;
        _devAim = null;
        _devMisses = 0;
        _devFrameMark = 0;
        if (_devAt < _devSteps.Count) return;
        // The last step reads everything back and says it; when it failed itself, what went wrong on the way is said.
        string words = _devPutBackWords ?? "the read back did not run to its end; on the way: " +
            (_devOnTheWay.Count == 0 ? "nothing said" : string.Join(" | ", _devOnTheWay));
        DevEnd($"{_devFailure ?? "ok"}; put back: {words}{(_devPutBackOk ? "" : "  NOT ALL AS IT WAS")}{(_devPutBackRead ? "" : "  NOT ALL READ BACK")}");
    }

    void DevEnd(string result)
    {
        DevLog("Woodcutting Rates check " + result);
        _devSteps.Clear();
        _devAt = 0;
        _devWithout?.Dispose();
        _devWithout = null;
        _devSettingsRead = false;
        _devSettingsCopy = null;
        File.WriteAllText(Path.Combine(_ctx.Directory, DoneFile), result);
    }

    // Two frames drawn since the step began to wait: the picture is the frame the game drew last, so a picture in the
    // same tick as a change never holds it (Thieving Overview's Risky tag, lesson 43).
    bool DevFramesDrawn()
    {
        long now = (long)Engine.GetFramesDrawn();
        if (_devFrameMark == 0) { _devFrameMark = now; return false; }
        return now >= _devFrameMark + 2;
    }

    string DevState() =>
        $"page shown={PageShown}, button shown={_opener?.IsVisibleInTree()}, lit={_opener?.ButtonPressed}, window open={IsOpen}, " +
        $"folded={_window?.Minimised}, sort={_settings.Sort} {(_settings.Descending ? "descending" : "ascending")}";

    // One entry, one line: the check keeps only the log's lines that carry the mod's name, so words of the game with
    // line breaks in them (its tooltips) are joined with " / ".
    void DevLog(string text) => _ctx.Log("Dev: " + One(text));

    static string One(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", " / ");

    static string DevJoin(params string[] parts) => string.Join(", ", parts.Where(p => p.Length > 0));

    // What the request found, to be put back as it was. Look: the page's filter as the page draws it (DevFilterLook).
    sealed record DevBefore(GamePage? Page, bool Wanted, bool Shown, bool? Folded, bool? Lit, string? Settings,
        WoodcuttingRecipeCategory? Filter, int? Scroll, string? Look);

    // The Woodcutting page's filter, scroll and filter as drawn at one moment (_devOrigin).
    sealed record DevPageWas(WoodcuttingRecipeCategory? Filter, int? Scroll, string? Look);

    DevPageWas DevPageNow() => new(_woodcutting!.Action.SelectedCategory, DevScrollNow(), DevFilterLook());

    // A press the request makes on the Woodcutting page (a category button, a line), on every tick until it is made.
    // It waits for the request's own last press to settle. While nothing of the request's is left on the page, the page as
    // it is on that tick is taken as what the put-backs go back to, so a filter or scroll the player chose while the press
    // waited for his hand stays his (Mining Rates' code review of turn 3, fault 6). While the request owns a part of the
    // page and that part is not exactly what it left, the player changed it: it is his, the request lets go of it, and the
    // press is not made (null), with one line, so that the request never presses over a page he is using; the done line
    // says so. True once it was made: what it leaves is read once the page has settled (DevSettle).
    bool? DevPagePress(Func<bool> press, DevBefore before, string what)
    {
        if (_devSettle != null) return false;
        bool owns = _devOwnFilter != null || _devOwnScroll != null;
        if (owns)
        {
            var moved = DevHisNow();
            if (moved.Count > 0)
            {
                string words = $"{what} NOT MADE: the player changed {string.Join(" and ", moved)} while the press waited for the mouse, which stays his";
                DevLog("click | " + words);
                _devOnTheWay.Add(words);
                return null;
            }
        }
        else _devOriginWait = DevPageNow();
        if (!press()) return false;
        if (!owns)
        {
            DevPageWas was = _devOrigin ?? new DevPageWas(before.Filter, before.Scroll, before.Look);
            _devOrigin = _devOriginWait ?? DevPageNow();
            // What the player changed earlier is in this origin now: it is put back as his, not left aside.
            _devHis.Clear();
            if (!ReferenceEquals(_devOrigin.Filter, was.Filter) || _devOrigin.Scroll != was.Scroll || _devOrigin.Look != was.Look)
                DevLog($"click | the page just before {what}: the filter {_devOrigin.Filter?.Id ?? "all"}, drawn {_devOrigin.Look ?? "-"}, " +
                    $"scrolled to {_devOrigin.Scroll?.ToString() ?? "-"}; not as the request found it (the filter {was.Filter?.Id ?? "all"}, drawn {was.Look ?? "-"}, " +
                    $"scrolled to {was.Scroll?.ToString() ?? "-"}): the player changed it meanwhile, and that is what is put back");
        }
        _devSettle = ((long)Engine.GetFramesDrawn(), true);
        return true;
    }

    // Each part the request owns, read again: one that is not exactly what the request left is the player's. The request
    // lets go of it (nothing of it is put back, and the read back says so). A scroll of his does not touch the filter; a
    // filter of his does take the scroll with it, because the list scrolled is then the one of his category (review 4).
    // Returns the parts he changed, in words.
    List<string> DevHisNow()
    {
        var moved = new List<string>();
        var action = _woodcutting!.Action;
        if (_devOwnFilter is { } filter && !ReferenceEquals(action.SelectedCategory, filter.Value))
        {
            moved.Add($"the filter (now {action.SelectedCategory?.Id ?? "all"}, the request left {filter.Value?.Id ?? "all"})");
            _devOwnFilter = null;
            _devHis.Add("filter");
            if (_devOwnScroll != null)
            {
                moved.Add("the scroll with it (the list is his category's now)");
                _devOwnScroll = null;
                _devHis.Add("scroll");
            }
        }
        if (_devOwnScroll != null && !ReferenceEquals(action.SelectedCategory, _devScrollUnder))
        {
            moved.Add($"the scroll (the page shows {action.SelectedCategory?.Id ?? "all"} now, the request's scroll was of {_devScrollUnder?.Id ?? "all"})");
            _devOwnScroll = null;
            _devHis.Add("scroll");
        }
        if (_devOwnScroll is { } scroll && DevScrollNow() is var now && now != scroll.Value)
        {
            moved.Add($"the scroll (now {now?.ToString() ?? "-"}, the request left {scroll.Value?.ToString() ?? "-"})");
            _devOwnScroll = null;
            _devHis.Add("scroll");
        }
        return moved;
    }

    // What the request's own last press left on the page, read once the page has settled: at least two drawn frames after
    // the press, and after the window's own scroll to a card (ScrollToCard) when the press was a click on a line. A change
    // of the request's: both parts are its own now. A put-back's press: the scroll it still owns is read again, since the
    // press laid the list out anew; the filter it put back is no longer its own.
    void DevSettle()
    {
        if (_devSettle is not { } settle || _scrollTo != null || (long)Engine.GetFramesDrawn() < settle.Frame + 2) return;
        _devSettle = null;
        _devSettledTick = _devTick;
        if (_woodcutting == null || _page == null || !GodotObject.IsInstanceValid(_page)) return;
        var action = _woodcutting.Action;
        if (settle.Both)
        {
            _devOwnFilter = new(action.SelectedCategory);
            _devOwnScroll = new(DevScrollNow());
            _devScrollUnder = action.SelectedCategory;
        }
        else if (_devOwnScroll != null)
        {
            _devOwnScroll = new(DevScrollNow());
            _devScrollUnder = action.SelectedCategory;
        }
        DevLog($"page | settled after the request's {(settle.Both ? "press" : "put-back's press")}: the request owns the filter " +
            $"{(_devOwnFilter is { } f ? f.Value?.Id ?? "all" : "(none)")} and the scroll {(_devOwnScroll is { } s ? s.Value?.ToString() ?? "-" : "(none)")}");
    }

    // The keyboard focus off the control the request has just pressed, if that control (or a part of it) has it, once the
    // press has been handled: a key of his would press it again. Nothing is given back.
    void DevFocusOff()
    {
        if (_devFocusAfter is not { } after || (long)Engine.GetFramesDrawn() < after.Frame + 1) return;
        _devFocusAfter = null;
        Control? owner = _ctx.Tree.Root.GuiGetFocusOwner();
        if (owner == null || !GodotObject.IsInstanceValid(after.Target) || (owner != after.Target && !after.Target.IsAncestorOf(owner))) return;
        owner.ReleaseFocus();
        DevLog($"focus | the keyboard focus taken off {DevName(owner)}, which the request's press gave it");
    }

    void DevPlan(string asked)
    {
        _devSteps.Clear();
        _devAt = _devPutBackAt = 0;
        _devFailure = _devFailOnPurpose = _devPutBackWords = null;
        _devPutBackOk = _devPutBackRead = _devOpenedPage = false;
        _devGuardSince = _devTipSince = 0;
        _devFrameMark = 0;
        _devAim = null;
        _devMisses = 0;
        _devOwnFilter = null;
        _devOwnScroll = null;
        _devSettle = null;
        _devSettledTick = -1;
        _devOrigin = _devOriginWait = null;
        _devOnTheWay.Clear();
        _devHandBack = null;
        _devHis.Clear();
        _devFocusAfter = null;
        _devWithout?.Dispose();
        _devWithout = null;
        _devStarted = Time.GetTicksMsec();
        _devAction = _woodcutting!.Action;
        GameView view = GameAccess.FindGameView() ?? throw new InvalidOperationException("No character is open.");
        var action = _woodcutting!.Action;
        // settings.json as it is on disk, the copy everything of the window is put back from (lesson 50).
        _devSettingsCopy = File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : null;
        _devSettingsRead = true;
        var before = new DevBefore(DevNative.CurrentPage(view)?.Item1, _wanted, _window is { IsAlive: true, Visible: true },
            _window is { IsAlive: true } ? _window.Minimised : null, GodotObject.IsInstanceValid(_opener) ? _opener!.ButtonPressed : null,
            _devSettingsCopy, action.SelectedCategory, DevScrollNow(), DevFilterLook());
        string cuttingAtStart = DevCutting();
        bool hiddenAtStart = action.ShowHiddenTrees;
        void Add(string name, Func<bool> run, ulong timeout = StepTime) => _devSteps.Add((name, run, timeout));
        DevLog($"before: page {before.Page?.Id ?? "none"}, window {(before.Wanted ? "open" : "closed")}{(before.Shown ? " and on the screen" : "")}" +
            $"{(before.Folded == true ? ", folded" : "")}, round button {(before.Lit is { } lit ? lit ? "lit" : "unlit" : "not made yet")}, settings.json " +
            (before.Settings == null ? "not there" : $"{before.Settings.Length} characters: {before.Settings}") +
            $" | {cuttingAtStart}; the page's category filter {before.Filter?.Id ?? "all"}, drawn {before.Look ?? "not read yet"}, " +
            $"the list of trees scrolled to {before.Scroll?.ToString() ?? "not found"}, the box for hidden trees {(hiddenAtStart ? "ticked" : "not ticked")} " +
            $"({action.HiddenTreeCount} trees hidden by the player) | the real pointer at {DisplayServer.MouseGetPosition()}");

        Add("open the Woodcutting page", () =>
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
            // The list of trees and the filter of a page the request had to open are read now that they are there.
            if (before.Scroll == null) before = before with { Scroll = DevScrollNow() };
            if (before.Look == null) before = before with { Look = DevFilterLook() };
            DevLog($"page | the page is {DevNative.CurrentPage(view)?.Item1?.Id ?? "none"}, the Woodcutting page is shown: {PageShown}; its list of trees scrolled to " +
                $"{before.Scroll?.ToString() ?? "not found"}; its filter drawn {before.Look ?? "NOT FOUND"}");
            DevStrip();
            if (IsOpen) Close();
            return true;
        });
        // The game's own words on a card's parts, filled in by the game, before the window is open over the page.
        List<DevAim>? aims = null;
        int aimAt = 0;
        Add("the card's stats", () =>
        {
            if (aims == null)
            {
                if (!DevFramesDrawn()) return false;
                WoodcuttingTreeSelectView? card = DevStatsCard(out string why);
                if (card == null) { DevLog($"card stats | no card can be pointed at ({why}): the card's tooltips are not read from the page this time"); return true; }
                aims = DevAims(card);
                aimAt = 0;
                _devTipSince = 0;
                _devFrameMark = 0;
            }
            while (aimAt < aims.Count)
            {
                if (!DevAimStep(aims[aimAt])) return false;
                aims[aimAt].Before?.Dispose();
                aims[aimAt].Before = null;
                aimAt++;
                _devTipSince = 0;
                _devFrameMark = 0;
            }
            DevPointerBack();
            return true;
        }, GiveUpAfter + 3 * TipWait + 10000);
        Add("the screen without the window", () =>
        {
            if (!DevFramesDrawn()) return false;
            _devWithout = _ctx.Tree.Root.GetTexture().GetImage();
            DevLog("the screen without the window is kept, to prove the pictures of the window against it");
            return true;
        });
        Add("press the round button", () => DevPress(_opener!, "the round button"), PointerStepTime);
        Add("opened", () => IsOpen);
        Add("open it up", () => _window!.Minimised ? DevPress(DevFoldButton(), "the window's fold button") : true, PointerStepTime);
        Add("opened up", () =>
        {
            if (!IsOpen || _window!.Minimised || _fitPending) return false;
            if (_opener?.ButtonPressed != true) throw new InvalidOperationException("The round button is not lit while the window is open.");
            return true;
        });
        Add("numbers", () =>
        {
            int rows = DevRows("open");
            if (rows == 0) throw new InvalidOperationException("The window lists no tree.");
            DevLog($"note | under the list: '{_list!.DevNote}'");
            DevGame();
            DevWords();
            DevEffects();
            DevHidden();
            DevLit();
            DevTips("open");
            DevSize("open");
            DevRoom("open");
            return true;
        });
        Add("picture of the window", () =>
        {
            if (!DevFramesDrawn()) return false;
            DevCut("open");
            Shot("window", crop: true);
            ShotStrip();
            // The rest of the list, for the picture of its end.
            var bar = _list!.DevScroll.GetVScrollBar();
            if (bar.MaxValue > bar.Page) _list.DevScroll.ScrollVertical = (int)bar.MaxValue;
            else DevLog($"the list does not scroll ({_list.DevLines().Count()} lines): no picture of its end");
            return true;
        });
        Add("the end of the list", () =>
        {
            var scroll = _list!.DevScroll;
            var bar = scroll.GetVScrollBar();
            if (bar.MaxValue <= bar.Page) return true;
            if (scroll.ScrollVertical == 0 || !DevFramesDrawn()) return false;
            DevLog($"in sight at the end of the list: {DevInSight()}");
            DevCut("the end of the list");
            Shot("window-end", crop: true);
            scroll.ScrollVertical = 0;
            return true;
        });
        Button? target = null, header = null;
        Image? beforeTip = null, beforeHeader = null;
        Add("point at a line", () =>
        {
            if (_list!.DevScroll.ScrollVertical != 0) return false;
            var lines = _list.DevLines().ToList();
            // A line whose card has no room beside the window first, so that the picture shows what its tooltip says then.
            target = (lines.FirstOrDefault(l => !l.Tree.Locked && l.Tree.Sight is CardSight.UnderWindow or CardSight.NoRoom).Button ??
                lines.FirstOrDefault(l => !l.Tree.Locked).Button ?? lines.FirstOrDefault().Button)
                ?? throw new InvalidOperationException("No line to point at.");
            TreeRates pointed = lines.First(l => l.Button == target).Tree;
            DevLog($"tooltip | the line pointed at: {pointed.Tree.Recipe.Id}, {DevCardSight(Native.Card(_page!, pointed.Tree), false)}");
            _list.DevScroll.EnsureControlVisible(target);
            _devTipSince = 0;
            return true;
        });
        Add("the screen before the tooltip", () =>
        {
            if (!DevFramesDrawn()) return false;
            beforeTip = _ctx.Tree.Root.GetTexture().GetImage();
            return true;
        });
        Add("tooltip", () => DevTooltip(target!, beforeTip, "tooltip"), PointerStepTime);
        // The name over a column whose tip is the longest: its tooltip is the widest of the names'.
        Add("point at a column's name", () =>
        {
            header = _list!.DevHeaders.OrderByDescending(h => h.TooltipText.Split('\n').Max(l => l.Length)).First();
            DevLog($"tooltip | the name with the longest tip: '{header.Text}'");
            _devTipSince = 0;
            return true;
        });
        Add("the screen before the name's tooltip", () =>
        {
            if (!DevFramesDrawn()) return false;
            beforeHeader = _ctx.Tree.Root.GetTexture().GetImage();
            return true;
        });
        Add("the name's tooltip", () => DevTooltip(header!, beforeHeader, "header-tooltip"), PointerStepTime);

        // Each click is waited for as a change of the sort, whatever the sort was before it.
        (string Key, bool Descending) sortWas = default;
        string firstBefore = "";
        Add("click XP", () =>
        {
            if (firstBefore.Length == 0)
            {
                firstBefore = DevOrder("before, " + _settings.Sort + (_settings.Descending ? " descending" : " ascending"));
                sortWas = (_settings.Sort, _settings.Descending);
            }
            return DevPress(_list!.DevHeader("xp") ?? throw new InvalidOperationException("There is no XP column."), "the name XP");
        }, PointerStepTime);
        Add("sorted by XP", () =>
        {
            if (_settings.Sort != "xp" || (_settings.Sort, _settings.Descending) == sortWas) return false;
            Refresh();
            DevOrder("XP, " + (_settings.Descending ? "most first" : "least first"));
            return true;
        });
        Add("picture sorted", () =>
        {
            if (!DevFramesDrawn()) return false;
            Shot("sorted", crop: true);
            return true;
        });

        // A click on a line whose tree's card is out of sight clear of the window, or under another category, then the
        // page's own category button for the same category, to compare. The page's filter is put back after each, and its
        // scroll two drawn frames later.
        TreeRates? clicked = null;
        DevClickEvents? events = null;
        Image? beforeClick = null;
        string cuttingBefore = "", ours = "", ourEvents = "", pageBack = "", handWords = "";
        bool filterChanged = false, compareSkipped = false;
        WoodcuttingRecipeCategory? startFrom = null, filterBeforeClick = null;
        Button? startButton = null;

        // After a put-back of the page's filter by its button's signal (DevFilterBack): when the signal did not set the
        // filter, the same button pressed with the pointer as a player would, and read back (Mining Rates' code review
        // of turn 2, fault 1); then the scroll, put back on its own (DevScrollBack), and one line that says all of it. The
        // press by hand reads the filter again on every tick before it points and before it presses: a filter he changed
        // is his, and the press is not made; a scroll of his does not stop it (review 3: one part does not touch the
        // other).
        void AddPageBack(string name, Func<bool> skip, string head)
        {
            Add($"{name}: the button by hand", () =>
            {
                if (skip() || _devHandBack is not { } hand) return true;
                var action = _woodcutting!.Action;
                if (_devOwnFilter is not { } own || !ReferenceEquals(action.SelectedCategory, own.Value))
                {
                    _devHandBack = null;
                    if (_devOwnFilter != null) { _devOwnFilter = null; _devHis.Add("filter"); }
                    handWords = $"the filter left on {action.SelectedCategory?.Id ?? "all"}, as the player changed it while the press by hand waited for the mouse: " +
                        $"the page's own '{hand.Button.Text}' button is not pressed";
                    return true;
                }
                try
                {
                    if (!GodotObject.IsInstanceValid(hand.Button) || !hand.Button.IsVisibleInTree())
                        throw new InvalidOperationException("the button is no longer on the screen");
                    if (!DevGamePress(hand.Button, $"the page's own '{hand.Button.Text}' button, to put the filter back by hand")) return false;
                    // The press lays the list out anew: the scroll the request still owns is read again once it settled.
                    _devSettle = ((long)Engine.GetFramesDrawn(), false);
                    return true;
                }
                catch (InvalidOperationException e)
                {
                    // The filter stays the request's own: the final put-back tries again.
                    _devHandBack = null;
                    handWords = $"the filter NOT AS IT WAS: the pointer could not press the page's own button either ({e.Message})";
                    return true;
                }
            }, PointerStepTime);
            Add($"{name}: read back by hand", () =>
            {
                if (skip() || _devHandBack is not { } hand) return true;
                var action = _woodcutting!.Action;
                if (!ReferenceEquals(action.SelectedCategory, hand.Filter) && Time.GetTicksMsec() - _devStarted < 3000) return false;
                _devHandBack = null;
                bool back = ReferenceEquals(action.SelectedCategory, hand.Filter);
                // Back: nothing of the request's is left on the filter. Not back: the request still owns what it left, and the
                // final put-back tries again.
                if (back) _devOwnFilter = null;
                handWords = (back ? $"the filter put back with the pointer on the page's own '{hand.Button.Text}' button"
                    : $"the filter NOT AS IT WAS after the signal and the pointer on the page's own '{hand.Button.Text}' button ({action.SelectedCategory?.Id ?? "all"})")
                    + DevHiddenMoved("the put-back's press of the page's own button");
                return true;
            });
            Add($"{name}: the scroll", () =>
            {
                if (skip()) return true;
                // The scroll is read again on a later tick than the reading of what the request left (DevSettle).
                if (_devOwnScroll != null && (_devSettle != null || _devSettledTick >= _devTick)) return false;
                // The scroll in its own try: a failure is its own NOT AS IT WAS, and the filter's words are said all the same.
                string scrollWords;
                try { scrollWords = DevScrollBack(); }
                catch (Exception e) { scrollWords = $"the scroll NOT AS IT WAS: its put-back failed ({e.GetType().Name}: {e.Message})"; }
                string said = DevJoin(pageBack, handWords, scrollWords);
                DevLog($"{head} {said}; drawn {DevFilterLook() ?? "NOT FOUND"}");
                // Into the done line too, even when a later put-back mends it (Mining Rates' code review of turn 3, fault 7).
                if (said.Contains("NOT AS IT WAS") || said.Contains("SHOW HIDDEN CHANGED")) _devOnTheWay.Add($"{head} {said}");
                pageBack = handWords = "";
                return true;
            });
        }
        // The press of the page's own button of a category, on every tick until it is made (DevPagePress).
        bool StartPress(Button button, WoodcuttingRecipeCategory category) =>
            DevPagePress(() => DevGamePress(button, $"the page's own category button of {category.Id} ('{button.Text}'), to start the click from it"),
                before, $"the press of the page's own category button of {category.Id}") != false;
        Add("a category to start from", () =>
        {
            var action = _woodcutting!.Action;
            if (startButton != null)
            {
                // The filter went to a category while the press waited for the mouse: the player's, and the click starts
                // from it (Mining Rates' code review of turn 3, fault 6).
                if (action.SelectedCategory != null)
                {
                    DevLog($"click | the page's filter went to {action.SelectedCategory.Id} while its own button of {startFrom!.Id} waited for the mouse: " +
                        "the player's, so that button is not pressed, and the click starts from his filter");
                    startButton = null;
                    startFrom = null;
                    return true;
                }
                return StartPress(startButton, startFrom!);
            }
            if (action.SelectedCategory != null) { DevLog($"click | the page's filter is on {action.SelectedCategory.Id}: the click starts from it"); return true; }
            // On all categories no card is under another category: the page's own button of a category is pressed first,
            // as a player would, so that the click on a line of another category has a filter to change. Only when the
            // page's "All" button is found by the game's word: it is what puts the filter back at the end.
            if (DevAllButton(out string why) == null)
            {
                DevLog($"click | the page's filter is on all categories, and {why}: no category is pressed, so that the request never has to guess " +
                    "the button that puts the filter back; the click only scrolls");
                return true;
            }
            var trees = _list!.DevLines().Select(l => l.Tree).Where(t => t.Tree.Category != null).ToList();
            int under = 0;
            foreach (var (category, button) in Native.CategoryButtons(DevNative.Filter(_page!)) ?? new Dictionary<WoodcuttingRecipeCategory, Button>())
            {
                if (!GodotObject.IsInstanceValid(button) || button.Disabled || !button.IsVisibleInTree()) continue;
                if (!trees.Any(t => !ReferenceEquals(t.Tree.Category, category))) continue;
                // A button under the mod's own window is never pressed (the pointer would land on the window).
                if (!DevSight(button).HasArea()) { under++; continue; }
                startFrom = category;
                startButton = button;
                return StartPress(button, category);
            }
            DevLog(under > 0
                ? $"click | the page's filter is on all categories, and the {under} category buttons that could start the click lie under the mod's own window: none is pressed, the click only scrolls"
                : "click | the page's filter is on all categories and no category button can be pressed: the click only scrolls");
            return true;
        }, PointerStepTime);
        Add("the category to start from", () =>
        {
            if (startFrom == null) return true;
            var action = _woodcutting!.Action;
            if (!ReferenceEquals(action.SelectedCategory, startFrom) && Time.GetTicksMsec() - _devStarted < 3000) return false;
            // What it left is read once the page has laid its cards out for the filter, which can move its scroll
            // (DevSettle): the click then tells the player's own change from it (DevPagePress).
            if (_devSettle != null) return false;
            DevLog($"click | after the page's own button of {startFrom.Id}: the filter {action.SelectedCategory?.Id ?? "all"}, drawn {DevFilterLook() ?? "NOT FOUND"}" +
                (ReferenceEquals(action.SelectedCategory, startFrom) ? "" : "  NOT SET BY ITS OWN BUTTON") + DevHiddenMoved("the page's own category button") +
                "; put back at the end");
            Refresh();
            return true;
        });
        Add("a line to click", () =>
        {
            var lines = _list!.DevLines().Select(l => l.Tree).Where(t => t.Clickable).ToList();
            // First a line whose card the page shows and a scroll brings clear of the window, what this click must do
            // (Mining Rates' review 4); then one under another category.
            clicked = lines.FirstOrDefault(t => t.Sight == CardSight.Scroll) ?? lines.FirstOrDefault(t => Native.Card(_page!, t.Tree) is { Visible: false }) ??
                lines.FirstOrDefault();
            if (clicked == null) { DevLog("click | no line shows something a click would change: none is clicked."); return true; }
            _list.DevScroll.EnsureControlVisible(DevLineOf(clicked));
            cuttingBefore = DevCutting();
            filterBeforeClick = _woodcutting!.Action.SelectedCategory;
            DevLog($"click | the line of {clicked.Tree.Recipe.Id} ({clicked.Tree.Category?.Id ?? "no category"}{(clicked.OnPage ? ", the tree the page shows" : "")}); " +
                $"before: {DevPageState(clicked.Tree)}; {cuttingBefore}");
            return true;
        });
        Add("the screen before the click", () =>
        {
            if (clicked == null) return true;
            if (!DevFramesDrawn()) return false;
            beforeClick = _ctx.Tree.Root.GetTexture().GetImage();
            return true;
        });
        Add("click the line", () =>
        {
            if (clicked == null) return true;
            events ??= new DevClickEvents(_woodcutting!.Action, DevLog);
            _devHiddenBefore = _woodcutting!.Action.ShowHiddenTrees;
            TreeRates line = clicked;
            bool? pressed = DevPagePress(() => DevClickLine(line), before, $"the click on the line of {line.Tree.Recipe.Id}");
            if (pressed != null) return pressed.Value;
            // Not made: the steps of the click and its comparison let it be.
            events.Dispose();
            events = null;
            clicked = null;
            return true;
        }, PointerStepTime);
        Add("the tree shown", () =>
        {
            if (clicked == null) return true;
            // The list of trees scrolls two drawn frames after the click (ScrollToCard); the picture two frames later. What
            // the click left is read once the page has settled (DevSettle), and the put-back reads it again a tick later.
            if (_scrollTo != null || _devSettle != null || _devSettledTick >= _devTick || !DevFramesDrawn()) return false;
            var action = _woodcutting!.Action;
            WoodcuttingTree tree = clicked.Tree;
            Refresh();
            WoodcuttingTreeSelectView? card = Native.Card(_page!, tree);
            CardSight? sight = card != null && card.IsVisibleInTree() ? Native.Place(card, Cover(), out _) : null;
            // A card the page has no room for beside the window can only come after a click that changed the filter;
            // then the line's tooltip must say so (ScrollToCard).
            string tip = DevLineOf(clicked).TooltipText;
            string shown = sight switch
            {
                CardSight.Clear => "",
                CardSight.UnderWindow or CardSight.NoRoom when !ReferenceEquals(filterBeforeClick, action.SelectedCategory) && tip.Contains("has no room") =>
                    "  NOT CLEAR OF THE WINDOW: the page has no room for it there, as the line's tooltip now says",
                CardSight.UnderWindow or CardSight.NoRoom => "  NOT SHOWN: no room for it clear of the window" + (tip.Contains("has no room") ? "" : ", AND THE LINE'S TOOLTIP DOES NOT SAY SO"),
                CardSight.Scroll => "  NOT SHOWN: a scroll would still show it clear of the window",
                _ => "  NOT SHOWN",
            };
            var lit = _list!.DevLines().Where(l => l.Tree.OnPage).Select(l => l.Tree.Tree.Recipe.Id).ToList();
            string cutting = DevCutting();
            ours = DevFilterState(tree);
            ourEvents = string.Join(", ", events!.Seen);
            filterChanged = !ReferenceEquals(action.SelectedCategory, filterBeforeClick);
            var litMeant = new List<string>();
            if (action.CuttingTree is { } cut && _list.DevLines().Any(l => ReferenceEquals(l.Tree.Tree, cut))) litMeant.Add(cut.Recipe.Id);
            DevLog($"click | after the click on the line of {tree.Recipe.Id}: {DevPageState(tree)}; the game's events [{ourEvents}]; {cutting}" +
                shown + DevCuttingMarks(cuttingBefore, cutting) +
                (lit.SequenceEqual(litMeant) ? "" : $"  THE LIT LINE IS [{string.Join(", ", lit)}]") +
                (IsOpen ? "" : "  THE WINDOW CLOSED") + DevHiddenMoved("the click on the line"));
            Shot("clicked");
            if (card != null && beforeClick != null) DevLog("click | the card in shot-woodcutting-clicked.png: " + DevProofBeside(beforeClick, card, "the card"));
            events.Dispose();
            events = null;
            pageBack = DevFilterBack();
            return true;
        });
        AddPageBack("the page back after the click", () => clicked == null, "click | the page's filter and scroll put back for the page's own button:");
        Add("the page's category button", () =>
        {
            if (clicked == null) return true;
            if (!filterChanged) { DevLog("click | the click left the category filter as it was: no category button to compare it with."); return true; }
            WoodcuttingRecipeCategory category = clicked.Tree.Category!;
            Button button = (Native.CategoryButtons(DevNative.Filter(_page!)) is { } buttons && buttons.TryGetValue(category, out Button? b) ? b : null)
                ?? throw new InvalidOperationException($"the page has no category button for {category.Id}");
            if (_devAim != button && !DevSight(button).HasArea())
            {
                compareSkipped = true;
                DevLog($"click | the page's own category button of {category.Id} lies under the mod's own window: it is not pressed, and the click is not compared with it");
                return true;
            }
            events ??= new DevClickEvents(_woodcutting!.Action, DevLog);
            bool? pressed = DevPagePress(() => DevGamePress(button, $"the page's own category button of {category.Id} ('{button.Text}')"),
                before, $"the press of the page's own category button of {category.Id}, to compare");
            if (pressed != null) return pressed.Value;
            compareSkipped = true;
            events.Dispose();
            events = null;
            return true;
        }, PointerStepTime);
        Add("the page's button compared", () =>
        {
            if (clicked == null || !filterChanged || compareSkipped) return true;
            if (_devSettle != null || _devSettledTick >= _devTick || !DevFramesDrawn()) return false;
            string game = DevFilterState(clicked.Tree), gameEvents = string.Join(", ", events!.Seen), cutting = DevCutting();
            DevLog($"click | after the page's own category button: {game}; the game's events [{gameEvents}]; {cutting}" +
                DevCuttingMarks(cuttingBefore, cutting) + DevHiddenMoved("the page's own category button") + " | " +
                (game == ours ? "the filter as after the click on the line" : $"DIFFER from the click on the line ({ours})") + "; " +
                (DevSameEvents(gameEvents, ourEvents) ? "the same events" : $"EVENTS DIFFER from the click on the line ([{ourEvents}])"));
            events.Dispose();
            events = null;
            pageBack = DevFilterBack();
            return true;
        });
        AddPageBack("the page back after its own button", () => clicked == null || !filterChanged || compareSkipped, "click | the page's filter and scroll put back:");
        Add("smallest", () =>
        {
            Resize(GameWindow.MinScale);
            return true;
        });
        Add("picture smallest", () =>
        {
            if (!IsOpen || _fitPending || !DevFramesDrawn()) return false;
            DevSize("smallest");
            DevCut("smallest");
            Shot("smallest", crop: true);
            Resize(GameWindow.MaxScale);
            _devFrameMark = 0;
            return true;
        });
        Add("picture largest", () =>
        {
            if (!IsOpen || _fitPending || !DevFramesDrawn()) return false;
            DevSize("largest that fits");
            DevCut("largest that fits");
            Shot("largest", crop: true);
            return true;
        });
        Rect2 openAt = default;
        Image? open = null;
        Add("close with the X", () =>
        {
            if (open == null)
            {
                openAt = _window!.Root.GetGlobalRect();
                open = _ctx.Tree.Root.GetTexture().GetImage();
            }
            return DevPress(_window!.Root.FindChildren("*", "Button", true, false).OfType<Button>().First(b => b.Text == "X"), "the window's X");
        }, PointerStepTime);
        Add("closed with the X", () =>
        {
            if (IsOpen || _window is { IsAlive: true, Visible: true }) return false;
            if (_opener?.ButtonPressed != false) throw new InvalidOperationException("The round button is still lit after the X.");
            if (!DevFramesDrawn()) return false;
            Shot("closed");
            using var now = _ctx.Tree.Root.GetTexture().GetImage();
            if (open != null) DevLog("shot-woodcutting-closed.png | where the window was: " + DevChanged(open, now, openAt, "the window's place", "gone from the picture", "STILL IN THE PICTURE"));
            return true;
        });

        _devPutBackAt = _devSteps.Count;
        // What the read-backs say, in order: the Woodcutting page's filter and scroll are read before the page that was on
        // the screen comes back (Mining Rates' code review of turn 2, fault 2), the rest at the end.
        var said = new List<string>();
        bool readOk = true, readAll = true;
        void Say(bool same, string what, string now)
        {
            readOk &= same;
            said.Add(same ? $"{what} as it was" : $"{what} NOT AS IT WAS (now {now})");
        }
        // A part of the page the request left alone because the player changed it is said as his, and is no fault.
        void SayPage(bool same, string part, string what, string now)
        {
            if (!same && _devHis.Contains(part)) said.Add($"{what}: left as the player changed it (now {now})");
            else Say(same, what, now);
        }
        // One part of the put-back in its own try: one line, NOT AS IT WAS for a part that failed or did not take, and the
        // next part runs all the same (the code review of turn 2, fault 4). carry: a NOT AS IT WAS in its words goes into
        // the done line (the filter's words go there later with its scroll's, in AddPageBack).
        void Part(string part, Func<string> put, bool carry = true)
        {
            try
            {
                string words = put();
                DevLog($"put back | {part}: {words}");
                if (carry && (words.Contains("NOT AS IT WAS") || words.Contains("SHOW HIDDEN CHANGED"))) _devOnTheWay.Add($"{part}: {words}");
            }
            catch (Exception e)
            {
                string words = $"{part} NOT AS IT WAS: its put-back failed ({e.GetType().Name}: {e.Message}), and the put-back goes on";
                DevLog("put back | " + words);
                _devOnTheWay.Add(words);
            }
        }
        Add("put back", () =>
        {
            Part("the game's events", () =>
            {
                events?.Dispose();
                events = null;
                return "no longer listened to";
            });
            Part("settings.json", DevSettingsBack);
            Part("the window", () =>
            {
                _window?.Free();
                _window = null;
                _list = null;
                _fitPending = false;
                _wanted = before.Wanted;
                Sync();
                SyncStripButton();
                return $"{(before.Wanted ? "open" : "closed")}, as the request found it, with what settings.json holds";
            });
            return true;
        });
        // The filter in a step of its own, so that settings.json and the window above never wait: a press of the request's
        // that has not settled yet is waited for (what it left is read by DevSettle, a request that failed right after a
        // press too), and the filter is read again a tick later. Then through the page's own buttons (by hand in the next
        // steps if their signal did not set it), and the scroll on its own. The page that was on the screen comes back
        // only after both.
        Add("put back the page's filter", () =>
        {
            if (_devSettle != null || _devSettledTick >= _devTick) return false;
            pageBack = "";
            Part("the page's filter", () =>
            {
                pageBack = DevFilterBack();
                return pageBack;
            }, carry: false);
            return true;
        });
        AddPageBack("put back the page's filter and scroll", () => false, "put back | the page's filter and scroll:");
        Add("read back the Woodcutting page", () =>
        {
            var action = _woodcutting!.Action;
            // What the put-backs went back to: the page just before the request's first change, else as it was at the start.
            DevPageWas want = _devOrigin ?? new DevPageWas(before.Filter, before.Scroll, before.Look);
            string? look = DevFilterLook();
            int? scroll = DevScrollNow();
            if (!PageShown)
            {
                // The player left the Woodcutting page while the request ran (the request opens no other page before this
                // step): nothing could be checked, which the done line says (Mining Rates' code review of turn 3, fault 7).
                said.Add($"the Woodcutting page's filter and scroll NOT READ BACK: the page on the screen is {DevNative.CurrentPage(view)?.Item1?.Id ?? "none"}, the player's");
                readAll = false;
                return true;
            }
            string when = _devOrigin != null && (!ReferenceEquals(_devOrigin.Filter, before.Filter) || _devOrigin.Scroll != before.Scroll || _devOrigin.Look != before.Look)
                ? " (as the player left it before the request's first change)" : "";
            SayPage(ReferenceEquals(action.SelectedCategory, want.Filter), "filter", $"the page's category filter {want.Filter?.Id ?? "all"}{when}",
                action.SelectedCategory?.Id ?? "all");
            // The filter as the page draws it too, not only the member (Mining Rates' code review of turn 1, fault 2).
            SayPage(look != null && look == want.Look, "filter", $"the page's filter drawn {want.Look ?? "(not read at the start)"}", look ?? "not found");
            SayPage(scroll == want.Scroll, "scroll", $"the page's list of trees scrolled to {want.Scroll?.ToString() ?? "-"}", scroll?.ToString() ?? "-");
            return true;
        });
        Add("put back the page", () =>
        {
            Part("the page", () =>
            {
                if (before.Page == null || !_devOpenedPage) return "as it was: the request did not open Woodcutting";
                GamePage? now = DevNative.CurrentPage(view)?.Item1;
                if (ReferenceEquals(now, before.Page)) return $"{before.Page.Id}, as it was";
                // Only the page the request opened itself: a page the player went to meanwhile is his.
                if (!PageShown) return $"left on {now?.Id ?? "none"}: the player went there from the Woodcutting page the request opened, and it is his";
                view.SetCurrentPage(before.Page);
                return $"back to {before.Page.Id}, the page on the screen before the request opened Woodcutting";
            });
            Part("the window on that page", () =>
            {
                Sync();
                SyncStripButton();
                return IsOpen ? "on the screen" : "not on the screen";
            });
            Part("the pointer", () =>
            {
                DevPointerBack();
                return "back where the real one is";
            });
            return true;
        });
        Add("read back", () =>
        {
            // The page changes on the game's own time: read back once it has, or after a few seconds anyway.
            GamePage? page = DevNative.CurrentPage(view)?.Item1;
            if (before.Page != null && !ReferenceEquals(page, before.Page) && Time.GetTicksMsec() - _devStarted < 4000) return false;
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
            Say(action.ShowHiddenTrees == hiddenAtStart, $"the box for hidden trees {(hiddenAtStart ? "ticked" : "not ticked")}", action.ShowHiddenTrees ? "ticked" : "not ticked");
            string cuttingNow = DevCutting();
            Say(cuttingNow == cuttingAtStart, cuttingAtStart, cuttingNow);
            said.Add("the pointer back where the real one is");
            // A put-back on the way that was not as it was counts, even when the last one mended it; a press not made
            // is said, and is no fault.
            var notOk = _devOnTheWay.Where(w => !w.Contains(" NOT MADE: ")).ToList();
            if (notOk.Count > 0) said.Add($"on the way NOT AS IT WAS: {string.Join(" | ", notOk)}");
            if (_devOnTheWay.Count > notOk.Count) said.Add($"not made: {string.Join(" | ", _devOnTheWay.Where(w => w.Contains(" NOT MADE: ")))}");
            _devPutBackWords = string.Join("; ", said) + $"; the order before the click on XP was {(firstBefore.Length > 0 ? firstBefore : "not read")}";
            _devPutBackOk = readOk && notOk.Count == 0;
            _devPutBackRead = readAll;
            DevLog("put back: " + _devPutBackWords);
            return true;
        });

        foreach (string line in asked.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("fail=")) { DevLog($"the request's line '{line}' is not understood and left out."); continue; }
            string name = line["fail=".Length..].Trim();
            // "fail=click" names the click on a line.
            if (name == "click") name = "click the line";
            if (_devSteps.Take(_devPutBackAt).Any(s => s.Name == name))
            {
                _devFailOnPurpose = name;
                DevLog($"the request asks the step '{name}' to fail on purpose, to show the put-back.");
            }
            else DevLog($"no step is called '{name}', so none fails on purpose. The steps: {string.Join(", ", _devSteps.Take(_devPutBackAt).Select(s => s.Name))}.");
        }
    }

    Button DevFoldButton() => _window!.Root.FindChildren("*", "Button", true, false).OfType<Button>()
        .FirstOrDefault(b => b.TooltipText is "Fold down to the title" or "Open the window again")
        ?? throw new InvalidOperationException("The window has no fold button.");

    bool DevCuttingNow() => _game!.ActionManager.ActiveActions.Any(a => ReferenceEquals(a.Entity, _woodcutting));

    // What woodcutting is doing: no click of the request may change it.
    string DevCutting()
    {
        var action = _woodcutting!.Action;
        return $"woodcutting: started {action.IsStarted}, active {DevCuttingNow()}, the tree cut {action.CuttingTree?.Recipe.Id ?? "none"}";
    }

    // What changed in what woodcutting is doing, in the words that mark what must not happen.
    static string DevCuttingMarks(string before, string after)
    {
        if (before == after) return "";
        string Part(string all, string key) => Regex.Match(all, key + @" ([\w:]+)").Groups[1].Value;
        string marks = "";
        if (Part(before, "started") == "False" && Part(after, "started") == "True") marks += "  CUTTING STARTED";
        if (Part(before, "started") == "True" && Part(after, "started") == "False") marks += "  CUTTING STOPPED";
        if (Part(before, "tree cut") != Part(after, "tree cut")) marks += "  THE TREE CUT CHANGED";
        return marks.Length > 0 ? marks : "  WOODCUTTING CHANGED";
    }

    // ---- a click on a line: the Woodcutting page's filter and scroll ------------------------------------------------

    // Where the page's list of trees is scrolled to, or null when the list is not found.
    int? DevScrollNow()
    {
        try { return _page != null && GodotObject.IsInstanceValid(_page) && DevNative.CardScroll(_page) is { } scroll ? scroll.ScrollVertical : null; }
        catch (Exception) { return null; }
    }

    // The page's filter, its buttons as they are drawn, and the tree's card.
    string DevFilterState(WoodcuttingTree tree)
    {
        var action = _woodcutting!.Action;
        WoodcuttingCategoryFilterView filter = DevNative.Filter(_page!);
        string buttons = string.Join(", ", DevNative.CategoryRow(filter).FindChildren("*", "Button", true, false).OfType<Button>()
            .Select(b => $"'{b.Text}'{(b.ButtonPressed ? " pressed" : "")}{(b.IsVisibleInTree() ? "" : " (hidden)")}"));
        WoodcuttingTreeSelectView? card = Native.Card(_page!, tree);
        int cards = Native.TreeViews(_page!)?.Values.Count(c => GodotObject.IsInstanceValid(c) && c.Visible) ?? -1;
        return $"the filter {action.SelectedCategory?.Id ?? "all"}, its buttons [{buttons}], {cards} cards shown, the card of {tree.Recipe.Id} " +
            (card == null ? "NOT ON THE PAGE" : card.Visible ? "shown" : "hidden");
    }

    string DevPageState(WoodcuttingTree tree) =>
        DevFilterState(tree) + $", {DevCardSight(Native.Card(_page!, tree), true)}, the list scrolled to {DevScrollNow()?.ToString() ?? "-"}";

    // How a card stands against the mod's window, as a click on its line reads it (Native.Place); with places: the
    // card's, its list's and the window's, all as drawn (Native.Drawn), the window's read from its frame (Cover).
    string DevCardSight(WoodcuttingTreeSelectView? card, bool places)
    {
        if (card == null) return "no card";
        if (!card.IsVisibleInTree()) return "its card not shown";
        CardSight sight = Native.Place(card, Cover(), out int to);
        string said = sight switch
        {
            CardSight.Clear => "its card in sight, clear of the window",
            CardSight.Scroll => $"its card not in sight clear of the window, a scroll to {to} shows it",
            CardSight.UnderWindow => "its card not in sight clear of the window, and no scroll shows it beside the window",
            _ => "its card not in sight, and no scroll shows it whole",
        };
        if (!places) return said;
        string list = Native.ScrollOf(card) is { } scroll
            ? $"its list at {Native.Drawn(scroll)} scrolled to {scroll.ScrollVertical} of {scroll.GetVScrollBar().MinValue:0}..{Math.Max(0, scroll.GetVScrollBar().MaxValue - scroll.GetVScrollBar().Page):0}"
            : "no list";
        return $"{said} (the card at {Native.Drawn(card)}, {list}, the window at {Cover()?.ToString() ?? "none, not on the screen"})";
    }

    // Every line's card against the window, and what its line says of it: a line is clickable for a card the page shows
    // only when a scroll shows it clear (MISMATCH otherwise), and the tooltip of a line whose card has no room says so
    // (NOT SAID otherwise). The window's place and the page's list are said once.
    void DevRoom(string when)
    {
        var said = new List<string>();
        WoodcuttingTreeSelectView? any = null;
        foreach (var (t, button, _, _) in _list!.DevLines())
        {
            WoodcuttingTreeSelectView? card = Native.Card(_page!, t.Tree);
            string sight = card is { Visible: true } ? DevCardSight(card, false) : card == null ? "no card" : "its card under another category";
            if (card is { Visible: true }) any ??= card;
            bool room = t.Sight is CardSight.UnderWindow or CardSight.NoRoom, tells = button.TooltipText.Contains("has no room");
            string marks = (card is { Visible: true } && card.IsVisibleInTree() && t.Clickable != (t.Sight == CardSight.Scroll) ? "  MISMATCH: its line clickable " + t.Clickable : "") +
                (room && !tells ? "  NOT SAID in its tooltip" : "") + (!room && tells ? "  SAID in its tooltip WITH ROOM" : "");
            said.Add($"{t.Tree.Recipe.LocalId}: {sight}, its line clickable {t.Clickable}{marks}");
        }
        string places = any != null ? DevCardSight(any, true) : "no card shown";
        // Where it opened: at the first place (FirstPlace) when the player had not placed it, or at his own.
        string opened = _firstPlace is { } first ? $"opened at the first place {first} (nothing saved by the player)"
            : _settings is { X: { } x, Y: { } y } ? $"at the player's own place ({x:0}, {y:0}) from settings.json" : "in the middle (no first place could be measured)";
        string strip = _opener?.GetParent() is Control buttons ? Native.Drawn(buttons).ToString() : "not found";
        DevLog($"room | {when} | the window at {Cover()?.ToString() ?? "none, not on the screen"}, from its frame {(_window is { IsAlive: true } w ? w.Root.GetGlobalRect().ToString() : "-")} " +
            $"at {(_window is { IsAlive: true } s ? s.Style.Scale.ToString("0.##") : "-")}, {opened}; the strip of round buttons at {strip}; the first card shown: {places}");
        DevLog($"room | {when} | {string.Join("; ", said)}");
        // How many lines a click can show where the window is: the review's aim is the most possible.
        var lines = _list.DevLines().Select(l => l.Tree).ToList();
        int shown = lines.Count(t => t.Sight != null), clear = lines.Count(t => t.Sight == CardSight.Clear), scroll = lines.Count(t => t.Sight == CardSight.Scroll),
            under = lines.Count(t => t.Sight == CardSight.UnderWindow), none = lines.Count(t => t.Sight == CardSight.NoRoom);
        DevLog($"room | {when} | of the {shown} cards the page shows, {clear + scroll} can be seen whole beside the window ({clear} in sight, {scroll} after a scroll), " +
            $"{under} have no room beside it, {none} no room at all; {lines.Count(t => t.Clickable)} of the {lines.Count} lines are clickable");
    }

    // The page's filter as the page draws it: the buttons of its filter shown pressed (its box for hidden trees apart)
    // and the number of cards it shows. Null when the page's parts are not there.
    string? DevFilterLook()
    {
        try
        {
            if (_page == null || !GodotObject.IsInstanceValid(_page)) return null;
            WoodcuttingCategoryFilterView filter = DevNative.Filter(_page);
            CheckBox box = DevNative.ShowHidden(filter);
            var pressed = filter.FindChildren("*", "Button", true, false).OfType<Button>()
                .Where(b => b != box && b.ButtonPressed).Select(b => $"'{b.Text}'");
            int cards = Native.TreeViews(_page)?.Values.Count(c => GodotObject.IsInstanceValid(c) && c.Visible) ?? -1;
            return $"buttons pressed [{string.Join(", ", pressed)}], {cards} cards shown";
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException or NullReferenceException) { return null; }
    }

    // The page's "All" button, found by the game's own word for it (WoodcuttingCategoryFilterView.s_all): the one button
    // of the page's filter whose words are that word, never its box for hidden trees (ShowHiddenCheckBox) nor any other
    // check box. Null, and why, when there is not exactly one: the request then never guesses a button (Mining Rates'
    // code review of turn 1, fault 1).
    Button? DevAllButton(out string why)
    {
        why = "";
        WoodcuttingCategoryFilterView filter = DevNative.Filter(_page!);
        string word;
        try { word = DevWordsOf(DevNative.AllWords(null).Text ?? ""); }
        catch (MissingMemberException) { why = "the game's word for its All button (s_all) could not be read"; return null; }
        if (word.Length == 0) { why = "the game's word for its All button (s_all) is empty"; return null; }
        CheckBox box = DevNative.ShowHidden(filter);
        var found = filter.FindChildren("*", "Button", true, false).OfType<Button>()
            .Where(b => b is not CheckBox && b != box && string.Equals(DevWordsOf(b.Text), word, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count == 1) return found[0];
        why = found.Count == 0 ? $"no button of the page's filter reads the game's word '{word}'" : $"{found.Count} buttons of the page's filter read the game's word '{word}'";
        return null;
    }

    // A game button pressed the way a click on a line presses a category's (CategoryFilter): a toggle button first goes
    // down, which lets the others of its group go, then its pressed signal. True when the filter followed; when it did
    // not, every button of its group is drawn again as it was, so that no button is left drawn pressed that the filter
    // does not follow (the code review of turn 3, fault 2).
    static bool DevSignalPress(Button button, Func<bool> followed)
    {
        var group = button.ButtonGroup?.GetButtons().OfType<BaseButton>().ToList() ?? new List<BaseButton>();
        if (!group.Contains(button)) group.Add(button);
        var drawn = group.Select(b => (Button: b, Pressed: b.ButtonPressed)).ToList();
        if (button.ToggleMode && !button.ButtonPressed) button.ButtonPressed = true;
        button.EmitSignal(BaseButton.SignalName.Pressed);
        if (followed()) return true;
        foreach (var (b, pressed) in drawn)
            if (GodotObject.IsInstanceValid(b) && b.ButtonPressed != pressed) b.SetPressedNoSignal(pressed);
        return false;
    }

    // A game button pressed with the pointer (DevPress), with the page's box for hidden trees read just before it: the
    // step after reads it again (DevHiddenMoved).
    bool DevGamePress(Control target, string what)
    {
        _devHiddenBefore = _woodcutting!.Action.ShowHiddenTrees;
        return DevPress(target, what);
    }

    // The page's box for hidden trees is the player's: after a press of a game button, against just before it.
    string DevHiddenMoved(string what) => _woodcutting!.Action.ShowHiddenTrees == _devHiddenBefore ? ""
        : $"  SHOW HIDDEN CHANGED by {what} (it was {(_devHiddenBefore ? "ticked" : "not ticked")})";

    // The page's filter and scroll as they were just before the request's first change (_devOrigin), each part on its own,
    // by one rule (review 3 of the orchestrator): a part the request owns is read again; still exactly what the request
    // left, it is put back; otherwise the player changed it, and it is his: left, said, never taken as the request's own,
    // and it does not stop the other part's put-back. The filter goes back through the page's own buttons: the
    // category's, or the "All" button found by the game's word (DevAllButton); never through the member behind them, and
    // never a guessed button. First by the button's signal, as a click on a line presses a category's; when that does not
    // set the filter, its buttons are drawn again as they were and the same button is left for the pointer
    // (_devHandBack, pressed by the steps after: AddPageBack). The scroll is put back on its own afterwards
    // (DevScrollBack). A part the request cannot put back stays its own, so that the final put-back tries again and no
    // later press takes the request's leftover for the player's choice.
    string DevFilterBack()
    {
        _devHandBack = null;
        if (_devOrigin is not { } origin || (_devOwnFilter == null && _devOwnScroll == null)) return "nothing of the request's is left on the page";
        if (_page == null || !GodotObject.IsInstanceValid(_page)) return "the Woodcutting page is gone: its filter and scroll NOT AS IT WAS (not put back)";
        var action = _woodcutting!.Action;
        var said = new List<string>();
        foreach (string part in DevHisNow()) said.Add($"{part} is the player's, and is left");
        if (_devOwnFilter is { } own)
        {
            if (ReferenceEquals(own.Value, origin.Filter))
            {
                _devOwnFilter = null;
                said.Add("the filter as it was");
            }
            else
            {
                string why = "";
                Button? button = origin.Filter == null ? DevAllButton(out why)
                    : Native.CategoryButtons(DevNative.Filter(_page)) is { } buttons && buttons.TryGetValue(origin.Filter, out Button? b) && GodotObject.IsInstanceValid(b) ? b : null;
                if (button == null)
                    said.Add($"the filter NOT AS IT WAS: it stays on {own.Value?.Id ?? "all"}, as the request left it, since " +
                        (origin.Filter == null ? why : $"the page has no button of {origin.Filter.Id}") + "; it is never set behind the page's buttons, " +
                        "and stays the request's own for the final put-back");
                else
                {
                    bool hidden = action.ShowHiddenTrees;
                    bool took = DevSignalPress(button, () => ReferenceEquals(action.SelectedCategory, origin.Filter));
                    string moved = action.ShowHiddenTrees == hidden ? "" : "  SHOW HIDDEN CHANGED by the put-back's press";
                    if (took)
                    {
                        _devOwnFilter = null;
                        // The press lays the list out anew: the scroll the request still owns is read again once it settled.
                        _devSettle = ((long)Engine.GetFramesDrawn(), false);
                        said.Add($"the filter put back with the page's own '{button.Text}' button{moved}");
                    }
                    else
                    {
                        // Pressed with the pointer next, as a player would; the filter stays the request's own until then.
                        _devHandBack = (button, origin.Filter);
                        said.Add($"the signal of the page's own '{button.Text}' button left the filter on {action.SelectedCategory?.Id ?? "all"}, and its buttons " +
                            $"are drawn again as they were: the button is pressed with the pointer next{moved}");
                    }
                }
            }
        }
        return DevJoin(said.ToArray());
    }

    // The scroll the request owns, read again on a later tick than what it left was read (DevSettle; the step waits for
    // it): still exactly that, it is put back to where it was before the request's first change; otherwise the player
    // moved it, and it is his, and left. While the filter is still the request's and not put back (its button not
    // found, its press by hand not made or not taken), the scroll is left with it, for the final put-back. When the write
    // does not take, what the request's own write left stays its own.
    string DevScrollBack()
    {
        if (_devOwnScroll is not { } own || _devOrigin is not { } origin) return "";
        if (_page == null || !GodotObject.IsInstanceValid(_page)) return "the Woodcutting page is gone: its scroll NOT AS IT WAS (not put back)";
        if (_devOwnFilter is { } filter && !ReferenceEquals(filter.Value, origin.Filter))
            return "the scroll left with the request's filter, which is not put back: both stay the request's own for the final put-back";
        if (!ReferenceEquals(_woodcutting!.Action.SelectedCategory, _devScrollUnder))
        {
            _devOwnScroll = null;
            _devHis.Add("scroll");
            return $"the scroll is left: the page shows {_woodcutting.Action.SelectedCategory?.Id ?? "all"} now, and the request's scroll was of " +
                $"{_devScrollUnder?.Id ?? "all"}, so the list on the screen is the player's";
        }
        int? now = DevScrollNow();
        if (now != own.Value)
        {
            _devOwnScroll = null;
            _devHis.Add("scroll");
            return $"the scroll {now?.ToString() ?? "-"} is not what the request left ({own.Value?.ToString() ?? "-"}): the player moved it, and it is left";
        }
        if (now == origin.Scroll)
        {
            _devOwnScroll = null;
            return "the scroll as it was";
        }
        if (origin.Scroll is not { } back)
        {
            _devOwnScroll = null;
            return "the scroll NOT AS IT WAS: where it stood before the request's first change was not read, so it is not written";
        }
        DevNative.CardScroll(_page).ScrollVertical = back;
        int? after = DevScrollNow();
        if (after == back)
        {
            _devOwnScroll = null;
            return $"the scroll put back to {back}";
        }
        _devOwnScroll = new(after);
        _devScrollUnder = _woodcutting!.Action.SelectedCategory;
        return $"the scroll NOT AS IT WAS (put back to {back}, it reads {after?.ToString() ?? "-"}); what the request's write left stays its own";
    }

    Button DevLineOf(TreeRates tree) => _list!.DevLines().First(l => ReferenceEquals(l.Tree.Tree, tree.Tree)).Button;

    // The line is pressed the way a player's hand would (DevPress), once the list has scrolled it into sight.
    bool DevClickLine(TreeRates tree)
    {
        Button line = DevLineOf(tree);
        if (!DevSight(line).HasArea()) return false;
        return DevPress(line, $"the line of {tree.Name}");
    }

    // The same events in another order are the same.
    static bool DevSameEvents(string a, string b) =>
        a.Split(", ", StringSplitOptions.RemoveEmptyEntries).Order().SequenceEqual(b.Split(", ", StringSplitOptions.RemoveEmptyEntries).Order());

    // The game's events about the page's filter and about woodcutting, listened to while one click is made. Each listener
    // runs inside a try; they are taken off by Dispose, which Stop and Detach reach through DevUnwatchAll.
    sealed class DevClickEvents : IDisposable
    {
        public readonly List<string> Seen = new();
        readonly List<Action> _off = new();
        static DevClickEvents? s_current;

        public DevClickEvents(WoodcuttingActionComponent action, Action<string> log)
        {
            s_current?.Dispose();
            s_current = this;
            void Safe(Action add)
            {
                try { add(); }
                catch (Exception e) { log("click events | a listener failed: " + e.Message); }
            }
            EventHandler<ValueChangedEventArgs<WoodcuttingRecipeCategory>> filter = (_, e) => Safe(() => Seen.Add($"the filter {e.NewValue?.Id ?? "all"}"));
            EventHandler<ValueChangedEventArgs<WoodcuttingTree>> cut = (_, e) => Safe(() => Seen.Add($"THE TREE CUT CHANGED to {e.NewValue?.Recipe.Id ?? "none"}"));
            EventHandler<ValueChangedEventArgs<bool>> hidden = (_, e) => Safe(() => Seen.Add($"SHOW HIDDEN CHANGED to {e.NewValue}"));
            EventHandler<ActionEventArgs> started = (_, _) => Safe(() => Seen.Add("CUTTING STARTED")),
                stopped = (_, _) => Safe(() => Seen.Add("CUTTING STOPPED"));
            action.SelectedCategoryChanged += filter;
            action.CuttingTreeChanged += cut;
            action.ShowHiddenTreesChanged += hidden;
            action.Started += started;
            action.Stopped += stopped;
            _off.Add(() =>
            {
                action.SelectedCategoryChanged -= filter;
                action.CuttingTreeChanged -= cut;
                action.ShowHiddenTreesChanged -= hidden;
                action.Started -= started;
                action.Stopped -= stopped;
            });
            foreach (WoodcuttingTree tree in action.Trees.Values)
            {
                EventHandler<ValueChangedEventArgs<bool>> hid = (_, e) => Safe(() => Seen.Add($"A TREE WAS {(e.NewValue ? "HIDDEN" : "SHOWN")}: {tree.Recipe.Id}"));
                tree.IsHiddenChanged += hid;
                _off.Add(() => tree.IsHiddenChanged -= hid);
            }
        }

        public void Dispose()
        {
            foreach (Action off in _off) off();
            _off.Clear();
            if (s_current == this) s_current = null;
        }

        /// <summary>Takes off the listeners of a click that is still being listened to.</summary>
        public static void DisposeCurrent() => s_current?.Dispose();
    }

    // Every listener a request hangs on the game: on Stop, on another character and at the put-back.
    void DevUnwatchAll()
    {
        DevClickEvents.DisposeCurrent();
        DevUnwatchHits();
    }

    // ---- what is logged ------------------------------------------------------------------------------------------

    static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    static string DevTexture(Texture2D? texture) =>
        texture == null ? "none" : $"{(texture.ResourcePath is { Length: > 0 } path ? path : "a " + texture.GetType().Name)} {texture.GetWidth()}x{texture.GetHeight()}";

    static string DevName(Control? control) => control == null ? "nothing" : $"{control.GetType().Name} '{control.Name}'";

    static string DevWordsOf(string text) => StringUtils.StripBBCode(text).Trim();

    static string DevTooltipText(TextTooltip? tooltip) => tooltip == null ? "none" : DevWordsOf(string.Join(" / ", DevNative.Content(tooltip) ?? []));

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
            DevLog($"row | {when} | {t.Tree.Recipe.Id} ({t.Tree.Category?.Id}) | locked={t.Locked}, level={t.Level}, needs '{t.Needs}', on the page={t.OnPage}, " +
                $"hidden by the player={t.Hidden}, a click shows it={t.Clickable} | raw: interval {R(s.Interval.Value)} ms (the recipe's {t.Tree.Recipe.BaseInterval}), " +
                $"max health {s.MaxHealth.Value} (the recipe's {t.Tree.Recipe.BaseMaxHealth}; now {t.Tree.Health?.Current}), XP {R(s.Experience.XpToAdd.Value)} " +
                $"(the recipe's {t.Tree.Recipe.BaseExperience}), product {t.Tree.Recipe.Product?.Item?.Id} x{s.ProductQuantity.Value} (base {t.Tree.Recipe.Product?.Quantity}), " +
                $"doubling {R(s.DoublingChance?.Total.Value ?? double.NaN)}, double damage {R(s.DoubleDamageChance?.Total.Value ?? double.NaN)}, " +
                $"weak spot {R(s.WeakSpotChance?.Total.Value ?? double.NaN)}, instant cut {R(s.InstantCutChance?.Total.Value ?? double.NaN)}, " +
                $"on hit [{rawHit}], on cut [{rawCut}], " +
                $"overgrowth [{string.Join(", ", s.OvergrowthStats?.List?.Select(o => $"{o.Effect?.Value?.Id} chance {R(o.ApplicationChance?.Total.Value ?? double.NaN)}") ?? [])}] | " +
                $"counted: {R(t.HitSeconds)} s a hit, {R(t.HitsPerHour)} hits/h, XP {R(t.XpEach)} a hit = {R(t.XpPerHour)}/h, hit points {t.HitPoints}, " +
                $"chances double {R(t.DoubleDamageChance)} weak {R(t.WeakSpotChance)} instant {R(t.InstantCutChance)} doubling {R(t.Doubling)}, " +
                $"hits a cut {(t.HitsPerCut is { } h ? R(h) : "-")} (the rule {(TreeRates.HitsRule is { } hr ? $"{hr.Reading}, {hr.Source}" : "not settled")}; by each reading [{DevReadings(t)}]), " +
                $"logs {t.Log?.Id} x{t.LogQuantity} (what they come with {(TreeRates.LogsRule is { } lr ? $"{lr.Reading}, {lr.Source}" : "not settled")}; with every hit it would be " +
                $"{R(t.HitsPerHour * t.LogQuantity * (1 + t.Doubling))}/h), logs/h {(t.LogsPerHour is { } l ? R(l) : "-")}, time per log {(t.SecondsPerLog is { } sl ? R(sl) : "-")}, " +
                $"log price {(t.LogPrice is { } p ? R(p) : "-")}, gold/h {(t.GoldPerHour is { } g ? R(g) : "-")} | per hit, " +
                $"{(TreeRates.HitItemsRule is { } ir ? $"by {ir.Reading}, {ir.Source}" : "NO RULE: once a hit, the window's reading")} [{string.Join(", ", t.PerHit.Select(DevDrop))}] | " +
                $"per cut [{string.Join(", ", t.PerCut.Select(DevDrop))}] | effects [{string.Join(", ", t.Effects.Select(e => $"{e.Name} {e.StacksLeft} left"))}] | " +
                $"picture {DevTexture(picture)}{(picture != null && _pictures!.IsStandIn(picture) ? "  STAND-IN DRAWN" : "")}");
            DevLog($"row | {when} | drawn {drawn}");
            DevLog($"row | {when} | tooltip '{button.TooltipText}'");
        }
        var trees = ReadTrees();
        DevLog($"rows | {when} | {count} lines, {DevRowsInSight()} in sight; the game has {_woodcutting!.Action.Trees.Count} trees, {trees.Count} listed, " +
            $"{trees.Count(t => t.Locked)} locked, {trees.Count(t => t.Hidden)} hidden by the player (the box for hidden trees {(_woodcutting.Action.ShowHiddenTrees ? "ticked" : "not ticked")}); " +
            $"columns [{string.Join(", ", _list!.DevColumns)}]; the logs by {(TreeRates.LogsRule is { } logs ? $"{logs.Reading}, {logs.Source}" : "NO RULE")}, " +
            $"the hits a cut takes by {(TreeRates.HitsRule is { } hits ? $"{hits.Reading}, {hits.Source}" : "NO RULE")}, " +
            $"the items of a hit by {(TreeRates.HitItemsRule is { } items ? $"{items.Reading}, {items.Source}" : "NO RULE (counted once a hit, the window's reading)")}" +
            (count != trees.Count ? "  MISMATCH" : ""));
        return DevRowsInSight();
    }

    static string DevDrop(TreeRates.Drop d) =>
        $"{d.Item.Id} chance {R(d.Chance)} x{d.Quantity} each {R(d.Each)} = {(d.PerHour is { } p ? R(p) : "-")}/h, sells {d.Item.SellsFor?.Quantity} {d.Item.SellsFor?.Currency?.Id}, " +
        $"gold {(d.GoldPerHour is { } g ? R(g) : "-")}/h";

    // The readings of how much a hit takes from a tree that the brief leaves open, with an ordinary hit of 1: a weak spot
    // instead of the hit's damage or on top of it, its share of the hit points rounded down, to the nearest or up.
    internal static readonly HitRule[] DevHitReadings = new[] { false, true }
        .SelectMany(added => Enum.GetValues<Rounding>().Select(rounding => new HitRule(1, rounding, added))).ToArray();

    static string DevReadingName(HitRule rule) => $"{(rule.WeakSpotAdded ? "added" : "instead")} {rule.WeakSpotRounding.ToString().ToLowerInvariant()}";

    // The hits a cut takes by each reading, from the tree's own hit points and chances, and the damage of a weak spot by it.
    static string DevReadings(TreeRates t) => string.Join(", ", DevHitReadings.Select(rule =>
        $"{DevReadingName(rule)} {(TreeRates.ExpectedHits(rule, t) is { } hits ? R(Math.Round(hits, 4)) : "never")} (a weak spot {TreeRates.Outcomes(rule, t.HitPoints, 0, 1)[2].Damage})"));

    // The longest line of every tip of the window in characters, against Fishing Rates' longest: Godot's tooltip does
    // not wrap, so a longer line makes a wider tooltip than Fishing Rates' (Mining Rates' code review of turn 1, fault 3).
    void DevTips(string when)
    {
        static string Longest(string text) => text.Split('\n').OrderByDescending(l => l.Length).First();
        static string Over(string line, int most) => line.Length > most ? $"  LONGER THAN FISHING RATES' {most}" : "";
        var heads = _list!.DevHeaders.Select(h => (h.Text, Line: Longest(h.TooltipText))).ToList();
        var lines = _list.DevLines().Select(l => (l.Tree.Tree.Recipe.LocalId, Line: Longest(l.Button.TooltipText))).ToList();
        string longest = heads.Select(h => h.Line).Concat(lines.Select(l => l.Line)).OrderByDescending(l => l.Length).First();
        DevLog($"tips | {when} | the names' tips, the longest line of each: {string.Join(", ", heads.Select(h => $"{h.Text} {h.Line.Length}{Over(h.Line, FishingTipMost)}"))}" +
            $" | the lines' tooltips: {string.Join(", ", lines.Select(l => $"{l.LocalId} {l.Line.Length}{Over(l.Line, FishingTooltipMost)}"))}" +
            $" | the longest of all, {longest.Length}: '{longest}'");
    }

    // The game's own labels for every card of the page and for its big panel, next to ours; and the page's order.
    void DevGame()
    {
        var action = _woodcutting!.Action;
        var trees = new Dictionary<WoodcuttingTree, TreeRates>(ReadTrees().Select(t => KeyValuePair.Create(t.Tree, t)), ReferenceEqualityComparer.Instance);
        var cards = Native.TreeViews(_page!) ?? throw new InvalidOperationException("The Woodcutting page has no cards.");
        var all = _page!.FindChildren("*", "", true, false);
        // Which of the game's two tree cards the page shows (the brief's question).
        DevLog($"game | the page has {cards.Count} cards in its own dictionary; in its tree {all.OfType<WoodcuttingTreeSelectView>().Count()} WoodcuttingTreeSelectView " +
            $"({all.OfType<WoodcuttingTreeSelectView>().Count(c => c.IsVisibleInTree())} shown) and {all.OfType<WoodcuttingTreeView>().Count()} WoodcuttingTreeView; " +
            $"its category filter {action.SelectedCategory?.Id ?? "all"}");
        foreach (var (tree, card) in cards)
        {
            if (!GodotObject.IsInstanceValid(card)) continue;
            try { DevCard(tree, card, trees.TryGetValue(tree, out TreeRates? listed) ? listed : TreeRates.Read(tree, _woodcutting, Shown)); }
            catch (Exception e) { DevLog($"game | card of {tree.Recipe.Id} could not be read: {e.GetType().Name}: {e.Message}"); }
        }
        // The rules settled by the cards' own words, against every card the page shows, in one line.
        var held = cards.Where(c => GodotObject.IsInstanceValid(c.Value) && c.Value.IsVisibleInTree())
            .Select(c => (c.Key.Recipe.LocalId, Words: DevRuleWords(c.Value, c.Key))).ToList();
        var notHeld = held.Where(h => h.Words.Contains("NOT AS SETTLED")).ToList();
        DevLog($"game | the settled rules against the cards' lists: {held.Count - notHeld.Count} of the {held.Count} cards shown as settled (the logs under " +
            $"'{TreeRates.LogsRule?.Quote}', the items of a hit under '{TreeRates.HitItemsRule?.Quote}')" +
            (notHeld.Count == 0 ? "" : $"  NOT AS SETTLED on [{string.Join("; ", notHeld.Select(h => $"{h.LocalId}: {h.Words}"))}]"));
        // The page's own order of cards, left to right and top to bottom, against the window's.
        var order = DevNative.CardBox(_page!).GetChildren().OfType<WoodcuttingTreeSelectView>().Where(c => c.Visible).Select(c => DevNative.Tree(c)?.Recipe.Id ?? "?").ToList();
        var oursOrder = ReadTrees().Where(t => Native.Card(_page!, t.Tree) is { Visible: true }).Select(t => t.Tree.Recipe.Id).ToList();
        DevLog($"game | the page's order of the cards it shows [{string.Join(", ", order)}]; the window's order of the same [{string.Join(", ", oursOrder)}]" +
            (order.SequenceEqual(oursOrder) ? "" : "  DIFFER"));
        try { DevBigPanel(); }
        catch (Exception e) { DevLog($"game | the big panel could not be read: {e.GetType().Name}: {e.Message}"); }
    }

    void DevCard(WoodcuttingTree tree, WoodcuttingTreeSelectView card, TreeRates t)
    {
        IntervalDisplay interval = DevNative.Interval(card);
        Int32Label hp = DevNative.HitPoints(card);
        SkillXPPanel xp = DevNative.Xp(card);
        Int32FormattedLabel xpLabel = DevNative.XpLabel(xp);
        string xpWords = DevTooltipText(DevNative.XpTooltip(xp));
        var bound = DevNative.XpInterval(xp);
        Texture2D? shown = DevNative.Picture(card).Texture;
        DevLog($"game | card {tree.Recipe.Id} '{DevNative.Name(card).Text}' ('{DevNative.CategoryName(card).Text}')" +
            $"{(ReferenceEquals(DevNative.Tree(card), tree) ? "" : "  BOUND TO ANOTHER TREE")} | shown={card.Visible}, {DevCardSight(card, false)}, " +
            $"unlocked part shown={DevNative.Unlocked(card).IsVisibleInTree()}, locked part shown={DevNative.LockedPart(card).IsVisibleInTree()} (ours locked={t.Locked}), " +
            $"hide button pressed={DevNative.HideButton(card).ButtonPressed} (ours hidden={t.Hidden}) | " +
            $"time: game {R(interval.Seconds)} s '{DevNative.IntervalLabel(interval).Text}', ours {R(t.HitSeconds)} s{(Math.Abs(interval.Seconds - t.HitSeconds) < 0.0005 ? "" : "  DIFFER")} | " +
            $"hit points: game {hp.Value} '{hp.Text}', ours {t.HitPoints}{(hp.Value == t.HitPoints ? "" : "  DIFFER")} | " +
            $"XP: game '{xpLabel.Text}' ({xpLabel.Value}), ours {R(t.XpEach)} a hit: {DevXpSame(xpLabel.Value, t.XpEach)}; the XP tag is bound to " +
            $"{(bound == null ? "no time" : ReferenceEquals(bound, tree.Stats.Interval) ? "the tree's time of a hit (WoodcuttingActionStats.Interval)" : $"another time ({R(bound.Value)} ms)")}; " +
            $"its tooltip '{xpWords}' | {DevXpPerSecond(xpWords, t)} | " +
            $"per hit: {DevGrants(DevNative.PerHit(card), tree)} | per cut: {DevGrants(DevNative.PerCut(card), tree)} | " +
            $"the settled rules against its lists: {DevRuleWords(card, tree)} | " +
            $"its picture {DevTexture(shown)}, ours {DevTexture(t.Picture)}" +
            (t.Picture == null || ReferenceEquals(shown, t.Picture) || shown?.ResourcePath == t.Picture.ResourcePath ? "" : "  MISMATCH") +
            $" | its overgrowth [{DevEffectIcons(DevNative.Overgrowth(card))}]");
        DevStatRows($"card {tree.Recipe.Id}", DevNative.Stats(card), tree);
    }

    // The rules settled by the card's own words, held against this card: the list that holds the logs (bound to the
    // tree's ProductQuantity) must be headed by the quote of TreeRates.LogsRule ("Full Cut"), and the one that holds the
    // items of a hit (bound to its OnHitItemChances) by the quote of HitItemsRule ("Per Hit"). AS SETTLED, or NOT AS
    // SETTLED with the headings read.
    static string DevRuleWords(WoodcuttingTreeSelectView card, WoodcuttingTree tree)
    {
        WoodcuttingActionStats stats = tree.Stats;
        GrantsContainer[] lists = [DevNative.PerHit(card), DevNative.PerCut(card)];
        string Check(string what, Func<GrantsContainer, bool> holds, RuleSource? source, string? quote)
        {
            if (source != RuleSource.GameWords || string.IsNullOrEmpty(quote)) return $"{what}: no rule settled by the card's words";
            GrantsContainer? list = lists.FirstOrDefault(holds);
            if (list == null) return $"{what} on neither of its lists  NOT AS SETTLED";
            var heads = DevListWords(list);
            return heads.Contains(quote) ? $"{what} under '{quote}', AS SETTLED" : $"{what} under [{string.Join(", ", heads.Select(h => $"'{h}'"))}]  NOT AS SETTLED ('{quote}')";
        }
        return Check("the logs", g => DevNative.GrantQuantities(g)?.Keys.Any(k => ReferenceEquals(k, stats.ProductQuantity)) == true,
                TreeRates.LogsRule?.Source, TreeRates.LogsRule?.Quote) + "; " +
            Check("the items of a hit", g => DevNative.GrantChances(g)?.Keys.Any(k => ReferenceEquals(k, stats.OnHitItemChances)) == true,
                TreeRates.HitItemsRule?.Source, TreeRates.HitItemsRule?.Quote);
    }

    // The words the card writes over one of its lists: the labels beside it, not those of its own items.
    static List<string> DevListWords(GrantsContainer list) =>
        (list.GetParent()?.FindChildren("*", "Label", true, false).OfType<Label>() ?? [])
        .Where(l => !list.IsAncestorOf(l) && l.Text.Length > 0).Select(l => DevWordsOf(l.Text)).ToList();

    // The big panel: the tree it shows, its time, health, XP and the two lists of what a hit and a cut bring.
    void DevBigPanel()
    {
        ActiveTreeView panel = DevNative.BigPanel(_page!);
        WoodcuttingTree? tree = DevNative.Tree(panel);
        if (tree == null || !DevNative.TreeShown(panel).IsVisibleInTree())
        {
            DevLog($"game | the big panel shows no tree (its 'no tree' panel shown={DevNative.NoTree(panel).IsVisibleInTree()}); the tree cut {_woodcutting!.Action.CuttingTree?.Recipe.Id ?? "none"}");
            return;
        }
        TreeRates big = TreeRates.Read(tree, _woodcutting!, Shown);
        Int32OutOfLabel health = DevNative.Health(panel);
        DevLog($"game | the big panel: {tree.Recipe.Id} '{DevNative.Name(panel).Text}' (the tree cut {_woodcutting!.Action.CuttingTree?.Recipe.Id ?? "none"}) | " +
            $"time: game {R(DevNative.Interval(panel).Seconds)} s '{DevNative.IntervalLabel(DevNative.Interval(panel)).Text}', ours {R(big.HitSeconds)} s" +
            $"{(Math.Abs(DevNative.Interval(panel).Seconds - big.HitSeconds) < 0.0005 ? "" : "  DIFFER")} | health '{health.Text}' ({health.Current} of {health.Maximum}, " +
            $"the tree's {tree.Health?.Current} of {big.HitPoints}) | XP: game {DevNative.XpLabel(DevNative.Xp(panel)).Value}, ours {R(big.XpEach)}: " +
            $"{DevXpSame(DevNative.XpLabel(DevNative.Xp(panel)).Value, big.XpEach)} | its XP tooltip '{DevTooltipText(DevNative.XpTooltip(DevNative.Xp(panel)))}' | " +
            $"{DevXpPerSecond(DevTooltipText(DevNative.XpTooltip(DevNative.Xp(panel))), big)} | per hit: {DevGrants(DevNative.PerHit(panel), tree)}" +
            $"{(DevNative.PerHitNothing(panel).IsVisibleInTree() ? $" '{DevNative.PerHitNothing(panel).Text}'" : "")} | per cut: {DevGrants(DevNative.PerCut(panel), tree)}");
        DevStatRows("the big panel", DevNative.Stats(panel), tree);
    }

    // What a card's list shows, item by item, the words the page writes over it, and which of the tree's own stats it is
    // bound to: the game's own word on what comes with a hit and what with a cut (Mining Rates' DevGrants). The logs are
    // the tree's ProductQuantity.
    static string DevGrants(GrantsContainer grants, WoodcuttingTree tree)
    {
        WoodcuttingActionStats stats = tree.Stats;
        var shown = new List<string>();
        foreach (Node node in grants.FindChildren("*", "", true, false))
        {
            if (node is ItemInfoIcon icon)
                shown.Add($"{icon.Item?.Id ?? "?"} x{R(DevNative.IconQuantity(icon))}{(DevNative.IconIsChance(icon) ? $" at {R(DevNative.IconChance(icon))}" : "")}{(icon.IsVisibleInTree() ? "" : " (hidden)")}");
            else if (node is SkillExperienceIcon xp)
                shown.Add($"XP '{DevWordsOf(string.Join(" ", xp.FindChildren("*", "Label", true, false).OfType<Label>().Select(l => l.Text)))}'{(xp.IsVisibleInTree() ? "" : " (hidden)")}");
        }
        var bound = new List<string>();
        foreach (var key in DevNative.GrantQuantities(grants)?.Keys ?? Enumerable.Empty<ValueStatNode<int>>())
            bound.Add(ReferenceEquals(key, stats.ProductQuantity) ? "the tree's ProductQuantity (THE LOGS)" : $"another quantity ({key.Value})");
        foreach (var key in DevNative.GrantXp(grants)?.Keys ?? Enumerable.Empty<ExperienceStats>())
            bound.Add(ReferenceEquals(key, stats.Experience) ? "the tree's Experience (THE XP)" : "another XP");
        foreach (var key in DevNative.GrantChances(grants)?.Keys ?? Enumerable.Empty<ValuesStatNode<GamesByMalcs.Melvor2.Data.Items.ItemQuantityChance>>())
            bound.Add(ReferenceEquals(key, stats.OnHitItemChances) ? $"the tree's OnHitItemChances ({key.Values.Count})"
                : ReferenceEquals(key, stats.OnCutItemChances) ? $"the tree's OnCutItemChances ({key.Values.Count})"
                : $"another list of chances [{string.Join(", ", key.Values.Select(v => $"{v.Value?.Item?.Id} {R(v.Value?.Chance ?? double.NaN)}"))}]");
        // The words over the list: the labels beside it in its parent, not those of its own items.
        var over = (grants.GetParent()?.FindChildren("*", "Label", true, false).OfType<Label>() ?? [])
            .Where(l => !grants.IsAncestorOf(l) && l.Text.Length > 0).Select(l => $"'{l.Text}'{(l.IsVisibleInTree() ? "" : " (hidden)")}");
        return $"[the words beside it {string.Join(" ", over)}; shows {string.Join(", ", shown)}; bound to {string.Join(", ", bound)}]";
    }

    // The game's tag is a whole number (Int32FormattedLabel), while its XP is not (ExperienceComponent.Xp is a Double).
    // The tag is ours cut down to a whole number: 57.99999999999999 shows "+57" (check-4: Wood Debris and Treant
    // Remains), as 9.28 shows "+9"; a value a hair under a whole number is not lifted to it first.
    static string DevXpSame(int game, double ours)
    {
        int down = (int)Math.Floor(ours);
        if (game == down) return ours - down < 1e-9 ? "the same" : "the tag is ours rounded down";
        if (game == (int)Math.Floor(ours + 1e-9)) return "the same, to the last digit of the arithmetic";
        return game == (int)Math.Round(ours) ? "the tag is ours rounded" : "DIFFER";
    }

    // Which rule the game's own XP tooltip follows: every number in it against the XP per second and per hour with the XP
    // on every hit (ours). Mining Rates' check read its words: "... This equates to roughly {0:N0} XP/hr" where a time is
    // bound to the tag.
    static string DevXpPerSecond(string tooltip, TreeRates ours)
    {
        var numbers = Regex.Matches(tooltip, @"\d[\d,]*(?:\.\d+)?").Select(m => double.Parse(m.Value.Replace(",", ""), CultureInfo.InvariantCulture)).ToList();
        if (!ours.HasTime) return $"numbers in it [{string.Join(", ", numbers.Select(R))}]";
        double perSecond = ours.XpEach / ours.HitSeconds;
        bool Near(double value, double room) => numbers.Any(n => Math.Abs(n - value) <= room);
        return $"numbers in it [{string.Join(", ", numbers.Select(R))}]; XP per second on every hit {R(perSecond)}: " +
            (Near(perSecond, Math.Max(0.006, 0.002 * perSecond)) ? "IN IT" : "not in it") + $"; XP an hour on every hit {R(ours.XpPerHour)}: " +
            (Near(ours.XpPerHour, Math.Max(1, 0.002 * ours.XpPerHour)) ? "IN IT, the game's XP of an hour is ours" : "not in it") +
            $"; ours a hit {R(ours.XpEach)} is {(Near(ours.XpEach, 0.006) ? "in it" : "not in it")}";
    }

    // The rows of a list of a tree's bonuses: each stat's name and words as the page writes them, its value as written
    // and as the game keeps it, against the tree's own stat; and the row's own tooltip. A card's list is filled when its
    // tooltip is shown (step 4 shows one).
    void DevStatRows(string where, WoodcuttingActionStatsView? list, WoodcuttingTree tree)
    {
        if (list == null || !GodotObject.IsInstanceValid(list)) { DevLog($"stats | {where}: no list"); return; }
        var rows = list.FindChildren("*", "", true, false).OfType<ModifierStatLabel>().ToList();
        if (rows.Count == 0) { DevLog($"stats | {where}: the list has no rows (a card's is filled when its tooltip is shown)"); return; }
        DevLog($"stats | {where}: " + string.Join(" | ", rows.Select(r => DevRow(r, tree))));
    }

    static string DevRow(ModifierStatLabel r, WoodcuttingTree tree)
    {
        WoodcuttingActionStats s = tree.Stats;
        var mine = new Dictionary<ModifierStatNodes, string>(ReferenceEqualityComparer.Instance);
        foreach (var (name, nodes) in new[] { ("double damage", s.DoubleDamageChance), ("weak spot", s.WeakSpotChance), ("instant cut", s.InstantCutChance), ("doubling", s.DoublingChance),
                     ("interval %", s.PercentInterval), ("interval flat", s.FlatInterval), ("max health %", s.PercentMaxHealth), ("max health flat", s.FlatMaxHealth),
                     ("product quantity", s.FlatProductQuantity), ("XP %", s.Experience.PercentXPGained), ("XP flat", s.Experience.FlatXPGained) })
            if (nodes != null) mine.TryAdd(nodes, name);
        ModifierStatNodes? stat = null;
        DoubleLabel? value = null;
        string own = "";
        try { stat = DevNative.RowStat(r); } catch (Exception) { }
        try { value = DevNative.RowValue(r); } catch (Exception) { }
        try { own = DevTooltipText(DevNative.RowTooltip(r)); } catch (Exception e) { own = $"not read ({e.GetType().Name})"; }
        double shown = value?.Value ?? double.NaN;
        // The unit of a chance, as Fishing Rates' DevChanceSame tells it: the row's number against the game's stat.
        string unit = stat == null ? "" : Math.Abs(shown - stat.Total.Value) < 1e-6 ? ", the row shows the stat as it is"
            : Math.Abs(shown - stat.Total.Value * 100) < 1e-4 ? ", the row shows the stat times 100 (percent)" : ", the row's number DIFFERS from the stat";
        string of = stat == null ? "no stat" : mine.TryGetValue(stat, out string? n) ? $"the tree's {n} ({R(stat.Total.Value)}){unit}" : "ANOTHER TREE'S OR NONE OF OURS";
        return $"'{DevWordsOf(r.StatName ?? "")}' value '{value?.Text}' ({R(shown)}), format {r.ValueFormat}, shown={r.IsVisibleInTree()}, {of}, " +
            $"its words '{DevWordsOf(r.StatTooltipContent ?? "")}', its own tooltip '{own}'";
    }

    static string DevEffectIcons(Node? box)
    {
        if (box == null || !GodotObject.IsInstanceValid(box)) return "none";
        return string.Join(", ", box.GetChildren().OfType<OvergrowthEffectIcon>().Select(i =>
            $"'{DevNative.EffectName(i).Text}' stacks '{DevNative.EffectStacks(i).Text}' left '{DevNative.EffectStacksLeft(i).Text}' shown={i.IsVisibleInTree()}"));
    }

    // The page's own words for the rules the hour rests on: the words it gives a tree's chances, its doubling, its
    // interval and XP, the words of the game's other tree card's icons, the XP tag's and the item icon's chance title,
    // and the filter's; a quote of a settled rule against them; and the filter's buttons.
    void DevWords()
    {
        // A word that cannot be read says which member was tried, and the rest is read all the same.
        string Read(string what, string members, Func<string> words)
        {
            try { return $"{what} '{DevWordsOf(words())}'"; }
            catch (Exception e) { return $"{what} not read ({members}: {e.GetType().Name})"; }
        }
        static string Tr(string key) => key + (TranslationServer.Translate(key).ToString() is { } t && t != key ? $" = {t}" : "");
        static string Constant(Type type, string member) => Tr(DevNative.Constant(type, member));
        Type stats = typeof(WoodcuttingActionStatsView), modifiers = typeof(ModifierStatView);
        var read = new[]
        {
            Read("double damage", "WoodcuttingActionStatsView.DoubleDamageChanceTitle and ...Tooltip",
                () => Constant(stats, "DoubleDamageChanceTitle") + ": " + Constant(stats, "DoubleDamageChanceTooltip")),
            Read("weak spot", "WoodcuttingActionStatsView.WeakSpotChanceTitle and ...Tooltip",
                () => Constant(stats, "WeakSpotChanceTitle") + ": " + Constant(stats, "WeakSpotChanceTooltip")),
            Read("instant cut", "WoodcuttingActionStatsView.InstantCutChanceTitle and ...Tooltip",
                () => Constant(stats, "InstantCutChanceTitle") + ": " + Constant(stats, "InstantCutChanceTooltip")),
            Read("doubling", "ModifierStatView.DoublingChanceTitle and ...Tooltip",
                () => Constant(modifiers, "DoublingChanceTitle") + ": " + Constant(modifiers, "DoublingChanceTooltip")),
            Read("the interval", "ModifierStatView.IntervalTooltip", () => Constant(modifiers, "IntervalTooltip")),
            Read("skill XP", "ModifierStatView.SkillXpGainedTooltip", () => Constant(modifiers, "SkillXpGainedTooltip")),
            Read("more logs", "ModifierStatView.FlatProductQuantityTitle and ...Tooltip",
                () => Constant(modifiers, "FlatProductQuantityTitle") + ": " + Constant(modifiers, "FlatProductQuantityTooltip")),
            Read("the other card's doubling icon", "DoublingIcon.s_statName and s_statDescription",
                () => DevNative.IconName((DoublingIcon?)null).Text + ": " + DevNative.IconWords((DoublingIcon?)null).Text),
            Read("its double damage icon", "TreeDoubleDamageIcon.s_statName and s_statDescription",
                () => DevNative.IconName((TreeDoubleDamageIcon?)null).Text + ": " + DevNative.IconWords((TreeDoubleDamageIcon?)null).Text),
            Read("its fell chance icon", "TreeFellChanceIcon.s_statName and s_statDescription",
                () => DevNative.IconName((TreeFellChanceIcon?)null).Text + ": " + DevNative.IconWords((TreeFellChanceIcon?)null).Text),
            Read("the XP tag", "SkillXPPanel.s_skillXpGained and s_xpPerSecond", () => DevNative.XpGained(null).Text + " / " + DevNative.XpPerSecond(null).Text),
            Read("an XP icon", "SkillExperienceIcon.s_skillXP", () => DevNative.SkillXpIconWords(null).Text),
            Read("an item's chance", "ItemInfoIcon.s_tooltipTitleChance and TooltipTitle",
                () => DevNative.ItemChanceTitle(null).Text + " / " + Constant(typeof(ItemInfoIcon), "TooltipTitle")),
            Read("the filter's All button", "WoodcuttingCategoryFilterView.s_all", () => DevNative.AllWords(null).Text),
            Read("the filter's box for hidden trees", "WoodcuttingCategoryFilterView.s_showHidden", () => DevNative.ShowHiddenWords(null).Text),
        };
        DevLog("words | " + string.Join(" | ", read));
        DevLog($"words | the game's two numbers of a weak spot: BaseWeakSpotDamage {WoodcuttingActionComponent.BaseWeakSpotDamage}, " +
            $"MaxHitpointsWeakSpotDamage {R(WoodcuttingActionComponent.MaxHitpointsWeakSpotDamage)}; an ordinary hit's damage is no member of the game's by ApiDump's names");
        // The quote of a settled rule, still in the game's words, or not (after a game update).
        static string Flat(string text) => Regex.Replace(text, @"\s+", " ").Trim();
        string heard = Flat(string.Join(" ", read));
        // The logs' and the items' rules quote the headings of the cards' lists, held against every card in "Dev: game".
        var (quote, source) = (TreeRates.HitsRule?.Quote, TreeRates.HitsRule?.Source);
        DevLog("words | the quote of the hits rule: " + (source != RuleSource.GameWords || string.IsNullOrEmpty(quote) ? "none (the rule is not settled by the game's words)"
            : heard.Contains(Flat(quote), StringComparison.Ordinal) ? $"'{quote}' is in the game's words read here" : $"'{quote}' NOT IN THE GAME'S WORDS read here (the card's are in 'card stats')"));
        // Every button of the page's filter, and which is which: the All button the put-back would press is found by the
        // game's word only (DevAllButton).
        try
        {
            WoodcuttingCategoryFilterView filter = DevNative.Filter(_page!);
            CheckBox box = DevNative.ShowHidden(filter);
            var categories = Native.CategoryButtons(filter) ?? new Dictionary<WoodcuttingRecipeCategory, Button>();
            Button? all = DevAllButton(out string why);
            DevLog("words | the filter's buttons: " + string.Join(" | ", filter.FindChildren("*", "Button", true, false).OfType<Button>().Select(b =>
                $"{b.GetType().Name} '{b.Text}'{(b.ToggleMode ? " toggle" : "")}{(b.ButtonPressed ? " pressed" : "")}{(b.IsVisibleInTree() ? "" : " (hidden)")}" +
                (b == box ? ", the box for hidden trees" : "") + (b == all ? ", the All button by the game's word" : "") +
                (categories.FirstOrDefault(c => c.Value == b).Key is { } c ? $", the button of {c.Id}" : ""))) +
                (all == null ? $" | NO ALL BUTTON: {why}" : ""));
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException or NullReferenceException)
        {
            DevLog("words | the filter's buttons could not be read: " + e.Message);
        }
        DevLog($"bonus names | {string.Join(", ", new[] { TreeRates.DoubleDamage, TreeRates.WeakSpot, TreeRates.InstantCut }.Select(k => _bonusNames.Of(k) is { } name ? $"{k}: the game's '{name}'" : $"{k}: NOT READ, the tooltip says '{BonusNames.Kind(k)}'"))}");
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
                string bonuses = string.Join("; ", effect.Bonuses?.Component?.Select(v => DevWordsOf(v.GetDescription(1))) ?? []);
                DevLog($"effects | {tree.Recipe.Id}: {effect.Effect?.Id} '{effect.Effect?.DisplayName.Text}', {effect.StacksLeft} stacks left, for the category " +
                    $"{effect.Effect?.AppliesToCategory?.Id ?? "any"} | its bonuses '{bonuses}' | in the tree's stats: " +
                    (held.Count > 0 ? string.Join(", ", held) : "THE EFFECT IS IN NONE OF THE TREE'S STATS") +
                    $" | the tree now: interval {R(s.Interval.Value)} ms, max health {s.MaxHealth.Value}, XP {R(s.Experience.XpToAdd.Value)}, quantity {s.ProductQuantity.Value}");
            }
        }
        DevLog($"effects | {found} overgrowth effects on the game's {action.Trees.Count} trees; the effects a cut may put on, per listed tree: " +
            string.Join("; ", ReadTrees().Select(t => $"{t.Tree.Recipe.LocalId} [{string.Join(", ", t.Tree.Stats.OvergrowthStats?.List?.Select(o => $"{o.Effect?.Value?.LocalId} {R(o.ApplicationChance?.Total.Value ?? 0)}") ?? [])}]")));
    }

    // Every tree of the game that is not listed, and why; and every listed tree the page does not show under all.
    void DevHidden()
    {
        var action = _woodcutting!.Action;
        var listed = new HashSet<WoodcuttingTree>(ReadTrees().Select(t => t.Tree), ReferenceEqualityComparer.Instance);
        int hidden = 0;
        foreach (var (recipe, tree) in action.Trees)
        {
            WoodcuttingTreeSelectView? card = Native.Card(_page!, tree);
            if (listed.Contains(tree))
            {
                if (action.SelectedCategory == null && card is { Visible: false }) DevLog($"hidden | {recipe.Id} is listed, and its card is hidden under all categories  NOT ON THE PAGE");
                continue;
            }
            hidden++;
            DevLog($"hidden | {recipe.Id} '{recipe.DisplayName.Text}' ({tree.Category?.Id}) | the tree is shown when: {DevConditions(recipe.DisplayRequirements)} | " +
                $"its category is shown when: {DevConditions(tree.Category?.DisplayRequirements)} | to cut it: {DevConditions(recipe.Requirements)} | " +
                $"hidden by the player {tree.IsHidden}, the box for hidden trees {(action.ShowHiddenTrees ? "ticked" : "not ticked")} | in the game's list " +
                $"{action.Config.Recipes.Contains(recipe)} | its card " + (card == null ? "none" : card.Visible ? "SHOWN ON THE PAGE" : "hidden"));
        }
        DevLog($"hidden | {hidden} of the game's {action.Trees.Count} trees are not listed; the page counts {action.HiddenTreeCount} hidden by the player");
    }

    static string DevConditions(ConditionGroup? group) => group == null || group.Conditions == null || group.Conditions.Count == 0 ? "always"
        : $"{(group.AreMet ? "met" : "NOT MET")}: " + string.Join("; ", group.Conditions.Select(c => $"{(c.IsMet ? "met" : "not met")}{(c.IsOr ? " (or)" : "")} '{DevWordsOf(c.GetDescription(false, false))}'"));

    // The tree the page's big panel shows against the lit line.
    void DevLit()
    {
        var action = _woodcutting!.Action;
        WoodcuttingTree? panel = null;
        try { panel = DevNative.Tree(DevNative.BigPanel(_page!)); } catch (Exception) { }
        var lit = _list!.DevLines().Where(l => l.Tree.OnPage).Select(l => l.Tree.Tree).ToList();
        // The tree cut is lit only when the window lists it: one he hid, with the box for hidden trees not ticked, is not
        // listed, and then no line is lit (as the click step reads it).
        bool listed = action.CuttingTree is { } cut && _list.DevLines().Any(l => ReferenceEquals(l.Tree.Tree, cut));
        bool right = listed ? lit.Count == 1 && ReferenceEquals(lit[0], action.CuttingTree) : lit.Count == 0;
        DevLog($"lit | the tree cut {action.CuttingTree?.Recipe.Id ?? "none"}{(action.CuttingTree != null && !listed ? " (not listed)" : "")}, " +
            $"the big panel's {panel?.Recipe.Id ?? "none"}, woodcutting now: {DevCuttingNow()} | " +
            $"the lit line{(lit.Count == 1 ? " is" : "s are")} {(lit.Count == 0 ? "none" : string.Join(", ", lit.Select(t => t.Recipe.Id)))}" +
            $" (drawn lit: {string.Join(", ", _list.DevLines().Where(l => l.Drawn.EndsWith("lit=True")).Select(l => l.Tree.Tree.Recipe.LocalId))})" +
            (right ? "" : "  MISMATCH"));
    }

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

    // Every label and button with words in the part of the window a picture shows, measured the Combat Simulator's way
    // (mods/CombatSim/CutProbe.cs, DevWords): whole, CUT (a name cut by its label's ClipText is CUT too, a wrapped note
    // with more lines than it shows), SHORTENED WITH ... where the game trims it with its tooltip holding it, and PAST THE
    // EDGE where it ends past the edge of its line or of the window (Mining Rates' DevCut).
    void DevCut(string when)
    {
        var root = _window!.Root;
        Rect2 sight = _list!.DevScroll.GetGlobalRect();
        float edge = root.GetGlobalRect().End.X;
        int measured = 0, cut = 0, past = 0, shortened = 0;
        var notWhole = new List<string>();
        foreach (Control c in root.FindChildren("*", "", true, false).OfType<Control>().Prepend(root))
        {
            if (c.IsQueuedForDeletion() || !c.IsVisibleInTree()) continue;
            string text = c is Label l ? l.Text : c is Button b ? b.Text : "";
            if (text.Length == 0) continue;
            // Lines of the list out of sight are not in the picture.
            if (_list.DevScroll.IsAncestorOf(c) && !c.GetGlobalRect().Intersects(sight)) continue;
            Font font = c.GetThemeFont("font");
            int size = c.GetThemeFontSize("font_size");
            float wide = text.Split('\n').Select(line => font.GetStringSize(line, HorizontalAlignment.Left, -1, size).X).DefaultIfEmpty(0).Max();
            string verdict;
            float room;
            if (c is Label label)
            {
                room = label.Size.X - label.GetThemeStylebox("normal").GetMinimumSize().X;
                bool wraps = label.AutowrapMode != TextServer.AutowrapMode.Off;
                verdict = wraps ? label.GetVisibleLineCount() >= label.GetLineCount() ? "whole" : "CUT"
                    : wide <= room + 0.5f ? (label.ClipText && wide > label.Size.X ? "CUT" : "whole")
                    : label.TextOverrunBehavior != TextServer.OverrunBehavior.NoTrimming ? "SHORTENED WITH ..." : "CUT";
            }
            else
            {
                var button = (Button)c;
                StyleBox look = button.GetThemeStylebox("normal");
                float box = button.HasMeta("column") && button.GetMeta("column").AsString() != RatesList.NameKey ? button.CustomMinimumSize.X : button.Size.X;
                room = box - look.GetMargin(Side.Left) - look.GetMargin(Side.Right) - (button.Icon != null ? button.Icon.GetWidth() : 0);
                verdict = wide <= room + 0.5f ? "whole" : button.TextOverrunBehavior != TextServer.OverrunBehavior.NoTrimming ? "SHORTENED WITH ..." : "CUT";
            }
            measured++;
            // The edge of its line in the list, or of the window.
            float end = c.GetGlobalRect().End.X, limit = edge;
            for (Node? n = c.GetParent(); n != null && n != root; n = n.GetParent())
                if (n is Button line && _list.DevScroll.IsAncestorOf(line)) { limit = line.GetGlobalRect().End.X; break; }
            bool beyond = end > limit + 0.5f;
            if (verdict == "whole" && !beyond) continue;
            if (verdict == "CUT") cut++;
            if (verdict.StartsWith("SHORTENED")) shortened++;
            if (beyond) past++;
            string tip = "";
            for (Node? n = c; n is Control k; n = k.GetParent())
                if (k.TooltipText != "") { tip = k.TooltipText; break; }
            notWhole.Add($"{verdict} '{One(text)}' {wide:0.#} wide in {room:0.#}" +
                (verdict.StartsWith("SHORTENED") ? tip.Contains(text, StringComparison.OrdinalIgnoreCase) ? ", its tooltip holds it" : ", ITS TOOLTIP DOES NOT HOLD IT WHOLE" : "") +
                (beyond ? $", PAST THE EDGE ({end:0} > {limit:0})" : ""));
        }
        DevLog($"cut | {when} | {measured} measured at {_window.Style.Scale:0.##}: " + (notWhole.Count == 0 ? "every one whole and within its edge"
            : $"{notWhole.Count} not whole or past the edge ({cut} CUT, {shortened} SHORTENED WITH ..., {past} PAST THE EDGE): {string.Join("; ", notWhole)}"));
    }

    // ---- the game's own words on a card's parts, as a player reads them by resting the pointer on them -------------

    // The card whose parts the pointer rests on: unlocked and wholly in sight, never the tree being cut while woodcutting
    // runs (its hit points and the page's big panel move with every hit; Mining Rates' code review of turn 3, fault 1);
    // best one whose tree has the most chances that change the hits a cut takes (a list of bonuses may show only the
    // stats that are not 0), equals in the page's order.
    WoodcuttingTreeSelectView? DevStatsCard(out string why)
    {
        why = "";
        var action = _woodcutting!.Action;
        WoodcuttingTree? cut = action.IsStarted || DevCuttingNow() ? action.CuttingTree : null;
        var cards = DevNative.CardBox(_page!).GetChildren().OfType<WoodcuttingTreeSelectView>()
            .Where(c => GodotObject.IsInstanceValid(c) && c.Visible && DevNative.Tree(c) != null && !ReferenceEquals(DevNative.Tree(c), cut) &&
                !DevNative.LockedPart(c).IsVisibleInTree() && c.IsVisibleInTree() && Native.Place(c, Cover(), out _) == CardSight.Clear &&
                DevNative.StatsIcon(c).IsVisibleInTree() && DevSight(DevNative.StatsIcon(c)).HasArea()).ToList();
        if (cards.Count == 0)
        {
            why = cut == null ? "no card of a tree that can be cut is wholly in sight"
                : $"no card of a tree that can be cut, other than {cut.Recipe.Id} being cut, is wholly in sight";
            return null;
        }
        int Score(WoodcuttingTreeSelectView card)
        {
            WoodcuttingActionStats s = DevNative.Tree(card)!.Stats;
            return new[] { s.DoubleDamageChance, s.WeakSpotChance, s.InstantCutChance }.Count(n => n != null && n.Total.Value > 0);
        }
        return cards.OrderByDescending(Score).First();
    }

    // One part of a card the pointer rests on for the game's tooltip, and what is kept from tick to tick: where the
    // pointer was put, the tooltip's words when it was first seen shown, and the screen before the pointer came (for the
    // proof of the picture). Tooltip null: the part has only Godot's own tooltip text. Words: the game's words only (their
    // numbers are named, and they must not move while read); Detail: what the request adds to them in the log (null for
    // nothing), such as the tree's stat each row of a list of bonuses is bound to.
    sealed class DevAim(string what, WoodcuttingTree tree, Control aim, string aimed, Tooltip? tooltip, Func<string> words, string? shot, Func<string>? detail = null)
    {
        public readonly string What = what, Aimed = aimed;
        public readonly WoodcuttingTree Tree = tree;
        public readonly Control Aim = aim;
        public readonly Tooltip? Tooltip = tooltip;
        public readonly Func<string> Words = words;
        public readonly Func<string>? Detail = detail;
        public readonly string? Shot = shot;
        public Image? Before;
        public Vector2 At;
        public string? First;
    }

    // The parts of the card to rest the pointer on, each with the control the game's tooltip shows for: its list of
    // bonuses (WoodcuttingStatsIcon, WoodcuttingStatsTooltip), its hit points (TreeMaxHP, when a tooltip of the game shows
    // for it or a part around it on the card), its XP tag (SkillXPPanel's XPTooltip). Logs every tooltip of the card with
    // its targets, and every word the card shows, once.
    List<DevAim> DevAims(WoodcuttingTreeSelectView card)
    {
        WoodcuttingTree tree = DevNative.Tree(card)!;
        var aims = new List<DevAim>();
        var tips = card.FindChildren("*", "", true, false).OfType<Tooltip>().ToList();
        DevLog($"card stats | the card of {tree.Recipe.Id}: every word it shows [{string.Join(" ", card.FindChildren("*", "", true, false).OfType<Control>()
            .Where(c => c.IsVisibleInTree() && (c is Label { Text.Length: > 0 } || c is RichTextLabel { Text.Length: > 0 })).Select(c => $"'{One(c is Label l ? l.Text : DevWordsOf(((RichTextLabel)c).Text))}'"))}] | " +
            $"its tooltips: {string.Join("; ", tips.Select(t => $"{DevName(t)} for [{string.Join(", ", DevNative.Targets(t).Select(DevName))}]"))}");

        // Where to rest the pointer for a tooltip: one of its targets that is a part of the card in sight, around or inside
        // the given part first, else the given part.
        Control Aim(Tooltip? tooltip, Control fallback, out string aimed)
        {
            var targets = tooltip == null ? new List<Control>() : DevNative.Targets(tooltip)
                .Where(c => (c == card || card.IsAncestorOf(c)) && c.IsVisibleInTree() && DevSight(c).HasArea()).ToList();
            Control? found = targets.FirstOrDefault(c => c == fallback || c.IsAncestorOf(fallback) || fallback.IsAncestorOf(c)) ?? targets.FirstOrDefault();
            aimed = found != null ? $"the tooltip's target {DevName(found)}" : $"{DevName(fallback)} (no target of its tooltip found in sight on the card)";
            return found ?? fallback;
        }

        Tooltip? stats = null;
        try { stats = DevNative.StatsTooltip(card); } catch (Exception e) { DevLog($"card stats | its stats tooltip could not be read (WoodcuttingTreeSelectView.WoodcuttingStatsTooltip: {e.GetType().Name})"); }
        Control icon = DevNative.StatsIcon(card);
        if (stats == null) DevLog("card stats | the card has no stats tooltip (WoodcuttingTreeSelectView.WoodcuttingStatsTooltip): its list of bonuses is not pointed at");
        else
        {
            Control statsAim = Aim(stats, icon, out string statsAimed);
            aims.Add(new DevAim("the card's list of bonuses", tree, statsAim, statsAimed, stats, () => DevStatsWords(card, stats), "stats-tooltip",
                () => DevStatsBound(card, tree)));
        }

        // The hit points: a tooltip of the card's whose target is the label or a part around it on the card, or the
        // label's own tooltip text.
        Int32Label hp = DevNative.HitPoints(card);
        Control? hpPart = null;
        Tooltip? hpTip = null;
        foreach (Tooltip t in tips)
            foreach (Control c in DevNative.Targets(t))
                if (c != card && card.IsAncestorOf(c) && (c == hp || c.IsAncestorOf(hp)) && (hpPart == null || hpPart.IsAncestorOf(c))) { hpPart = c; hpTip = t; }
        Control? textPart = null;
        for (Node? n = hp; n != null && n != card; n = n.GetParent())
            if (n is Control k && k.TooltipText.Length > 0) { textPart = k; break; }
        if (hpTip != null && hpPart != null)
            aims.Add(new DevAim("its hit points", tree, hpPart, $"the tooltip's target {DevName(hpPart)}, around the label {DevName(hp)}", hpTip, () => DevTipWords(hpTip), null));
        else if (textPart != null)
            aims.Add(new DevAim("its hit points", tree, textPart, $"{DevName(textPart)} with Godot's own tooltip text", null, () => textPart.TooltipText, null));
        else DevLog($"card stats | its hit points '{hp.Text}': no tooltip of the card's has the label {DevName(hp)} or a part around it as a target, and none has tooltip text: not pointed at");

        SkillXPPanel xp = DevNative.Xp(card);
        TextTooltip? xpTip = DevNative.XpTooltip(xp);
        if (xpTip == null) DevLog("card stats | its XP tag has no tooltip (SkillXPPanel.XPTooltip): not pointed at");
        else
        {
            Control xpAim = Aim(xpTip, xp, out string xpAimed);
            aims.Add(new DevAim("its XP tag", tree, xpAim, xpAimed, xpTip, () => DevTipWords(xpTip), null));
        }
        return aims;
    }

    // The words of a game's tooltip: a text tooltip's title and content, or every word it draws.
    static string DevTipWords(Tooltip? tooltip)
    {
        if (tooltip == null) return "";
        if (tooltip is TextTooltip text)
        {
            string title = "";
            try { title = DevWordsOf(DevNative.TooltipTitle(text).Text ?? ""); } catch (Exception) { }
            string content = DevWordsOf(string.Join("\n", DevNative.Content(text) ?? []));
            return title.Length > 0 && !content.StartsWith(title, StringComparison.Ordinal) ? title + "\n" + content : content;
        }
        return string.Join("\n", tooltip.FindChildren("*", "", true, false).OfType<Control>().Where(c => c.IsVisibleInTree())
            .Select(c => c is Label l ? l.Text : c is RichTextLabel r ? DevWordsOf(r.Text) : "").Where(w => w.Length > 0));
    }

    // The card's list of bonuses as its tooltip shows it, in the game's words only: every row the game filled in (its
    // name, its value as written, its words and its own tooltip's), then every other word the tooltip draws.
    static string DevStatsWords(WoodcuttingTreeSelectView card, Tooltip? tooltip)
    {
        var rows = new List<string>();
        WoodcuttingActionStatsView? list = DevStatsList(card);
        foreach (ModifierStatLabel r in DevShownRows(list))
        {
            string value = "", own = "";
            try { value = DevNative.RowValue(r).Text; } catch (Exception) { }
            try { own = DevTooltipText(DevNative.RowTooltip(r)); } catch (Exception) { }
            rows.Add($"['{DevWordsOf(r.StatName ?? "")}' '{value}': '{DevWordsOf(r.StatTooltipContent ?? "")}'{(own.Length > 0 && own != "none" ? $", its own tooltip '{own}'" : "")}]");
        }
        if (tooltip != null)
            rows.AddRange(tooltip.FindChildren("*", "", true, false).OfType<Control>()
                .Where(c => c.IsVisibleInTree() && (list == null || !list.IsAncestorOf(c)) && (c is Label { Text.Length: > 0 } || c is RichTextLabel { Text.Length: > 0 }))
                .Select(c => $"'{(c is Label l ? l.Text : DevWordsOf(((RichTextLabel)c).Text))}'"));
        return rows.Count == 0 ? "" : string.Join("\n", rows);
    }

    // The same rows with the tree's stat each is bound to and its value as the game keeps it (DevRow).
    static string DevStatsBound(WoodcuttingTreeSelectView card, WoodcuttingTree tree) =>
        "the rows bound: " + string.Join(" | ", DevShownRows(DevStatsList(card)).Select(r => DevRow(r, tree)));

    static WoodcuttingActionStatsView? DevStatsList(WoodcuttingTreeSelectView card)
    {
        try { return DevNative.Stats(card) is { } list && GodotObject.IsInstanceValid(list) ? list : null; }
        catch (Exception) { return null; }
    }

    static IEnumerable<ModifierStatLabel> DevShownRows(WoodcuttingActionStatsView? list) =>
        list == null ? [] : list.FindChildren("*", "", true, false).OfType<ModifierStatLabel>().Where(r => r.IsVisibleInTree());

    // The game says the pointer is on the control: on it or on a part of it, or on a control around it that the pointer
    // goes through to at the spot it was put (a label lets the pointer through to the control under it).
    bool DevPointerOn(Control aim, Vector2 at)
    {
        Control? over = _ctx.Tree.Root.GuiGetHoveredControl();
        if (over == null || !GodotObject.IsInstanceValid(aim)) return false;
        return over == aim || aim.IsAncestorOf(over) || (over.IsAncestorOf(aim) && aim.GetGlobalRect().HasPoint(at));
    }

    // The game's tooltip says it is shown, and not for another control than the one aimed at or the one under the spot
    // the pointer was put (Tooltip.IsShown and CurrentTarget, public members by ApiDump); when they cannot be read, its
    // being on the screen is taken, and said (Mining Rates' DevShownFor).
    static bool DevShownFor(Tooltip tooltip, Control aim, Vector2 at, out string said)
    {
        try
        {
            bool shown = tooltip.IsShown || tooltip.IsVisibleInTree();
            Control? target = tooltip.CurrentTarget;
            bool other = target != null && GodotObject.IsInstanceValid(target) && target != aim && !target.IsAncestorOf(aim) && !aim.IsAncestorOf(target) &&
                !target.GetGlobalRect().HasPoint(at);
            said = $"shown {tooltip.IsShown}, on the screen {tooltip.IsVisibleInTree()}, for {(target == null ? "no target" : DevName(target))}{(other ? "  ANOTHER CONTROL" : "")}";
            return shown && !other;
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            said = $"Tooltip.IsShown or CurrentTarget not read ({e.GetType().Name}), on the screen {tooltip.IsVisibleInTree()}";
            return tooltip.IsVisibleInTree();
        }
    }

    // Rests the pointer on one part of the card until the game shows its tooltip for it, filled, then reads it two drawn
    // frames later, only while the pointer is on it, and pictures it when it is the list of bonuses. Its words when it was
    // first seen shown are held against the read: ITS WORDS CHANGED when they moved. A tooltip not shown in 20 s is one
    // line, and the request goes on. True when this part is done.
    bool DevAimStep(DevAim a)
    {
        if (DevHandAtMouse(a.What)) { _devTipSince = 0; a.First = null; return false; }
        if (_devTipSince == 0)
        {
            _devTipSince = Time.GetTicksMsec();
            a.Before ??= _ctx.Tree.Root.GetTexture().GetImage();
        }
        string content;
        try { content = DevWordsOf(a.Words()); }
        catch (Exception e) { content = ""; DevLog($"card stats | {a.What}: its words could not be read ({e.GetType().Name}: {e.Message})"); }
        bool on = DevPointerOn(a.Aim, a.At);
        string state;
        bool shown;
        if (a.Tooltip != null) shown = DevShownFor(a.Tooltip, a.Aim, a.At, out state);
        else
        {
            string up = GameStyle.DevTooltips(_ctx.Tree.Root);
            string first = a.Aim.TooltipText.Split('\n')[0];
            shown = first.Length > 0 && up.Contains(first);
            state = shown ? "Godot's tooltip shown" : "Godot's tooltip not shown";
        }
        if (!(on && shown && content.Length > 0))
        {
            a.First = null;
            if (Time.GetTicksMsec() - _devTipSince >= TipWait)
            {
                DevLog($"card stats | {a.What} of {a.Tree.Recipe.Id}: its tooltip was not shown filled in {TipWait / 1000} s with the pointer on " +
                    $"{DevName(_ctx.Tree.Root.GuiGetHoveredControl())}, aimed at {a.Aimed} (the tooltip: {state}; it holds '{content}'): not read this time");
                DevPointerBack();
                return true;
            }
            if (!on)
            {
                Rect2 rect = DevSight(a.Aim);
                if (!rect.HasArea())
                {
                    DevLog($"card stats | {a.What} of {a.Tree.Recipe.Id} went out of sight: not read this time");
                    return true;
                }
                a.At = rect.GetCenter();
                DevPointer(a.At);
            }
            return false;
        }
        if (a.First == null)
        {
            a.First = content;
            _devFrameMark = 0;
        }
        // Two drawn frames after it came up, for the picture.
        if (!DevFramesDrawn()) return false;
        string detail = "";
        try { detail = a.Detail?.Invoke() ?? ""; }
        catch (Exception e) { detail = $"not read ({e.GetType().Name})"; }
        DevLog($"card stats | {a.What} of {a.Tree.Recipe.Id}, the pointer on {a.Aimed}, the tooltip {state}: '{content}'" +
            (content == a.First ? "" : $"  ITS WORDS CHANGED while read (first seen: '{a.First}')") + $" | {DevNumbersIn(content, a.Tree)}" +
            (detail.Length > 0 ? $" | {detail}" : ""));
        if (a.Shot != null)
        {
            Shot(a.Shot);
            if (a.Before != null && a.Tooltip != null)
            {
                using var shot = _ctx.Tree.Root.GetTexture().GetImage();
                DevLog($"shot-woodcutting-{a.Shot}.png | " + (DevDrawn(a.Tooltip, out string where) is { } drawn
                    ? DevChangedPixels(a.Before, shot, drawn, $"the game's tooltip: {where}", "VISIBLE", "NOT VISIBLE")
                    : $"NOT PROVED: where the game's tooltip is drawn is not known ({where})"));
            }
        }
        DevPointerBack();
        return true;
    }

    // Every number in a tooltip's words, named against the tree's numbers and the game's two numbers of a weak spot, so
    // that words stating how much a hit takes from a tree can be read for what their numbers are (Mining Rates' break
    // rule read "Base chance" and "Current chance" this way).
    string DevNumbersIn(string words, WoodcuttingTree tree)
    {
        TreeRates t = TreeRates.Read(tree, _woodcutting!, Shown);
        int b = WoodcuttingActionComponent.BaseWeakSpotDamage;
        double f = WoodcuttingActionComponent.MaxHitpointsWeakSpotDamage;
        var known = new List<(string Name, double Value)>
        {
            ("the game's base weak spot damage (BaseWeakSpotDamage)", b), ("the game's share of hit points of a weak spot (MaxHitpointsWeakSpotDamage)", f),
            ("the tree's hit points", t.HitPoints), ("the tree's time of a hit in seconds", t.HitSeconds), ("the tree's XP of a hit", t.XpEach),
            ("the tree's XP an hour with XP on every hit (ours)", t.XpPerHour), ("the tree's hits an hour", t.HitsPerHour),
            ("the tree's double damage chance", t.DoubleDamageChance), ("the tree's weak spot chance", t.WeakSpotChance),
            ("the tree's instant cut chance", t.InstantCutChance), ("the tree's doubling chance", t.Doubling), ("the tree's logs at a time", t.LogQuantity),
            ("the share of the tree's hit points", f * t.HitPoints),
            ("a weak spot by the base and the share rounded down", b + Math.Floor(f * t.HitPoints + 1e-9)),
            ("... rounded to the nearest", b + Math.Round(f * t.HitPoints, MidpointRounding.AwayFromZero)), ("... rounded up", b + Math.Ceiling(f * t.HitPoints - 1e-9)),
        };
        var said = new List<string>();
        foreach (Match m in Regex.Matches(words, @"-?\d[\d,]*(?:\.\d+)?\s*%?"))
        {
            string raw = m.Value.Trim();
            bool percent = raw.EndsWith('%');
            if (!double.TryParse(raw.TrimEnd('%').Trim().Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) continue;
            var names = known.Where(k => Math.Abs(k.Value - n) <= 1e-6 + 0.0005 * Math.Abs(n) || (percent && Math.Abs(k.Value * 100 - n) <= 0.006))
                .Select(k => k.Name).Distinct().ToList();
            said.Add($"{raw}: {(names.Count == 0 ? "none of the tree's numbers" : string.Join(" = ", names))}");
        }
        return said.Count == 0 ? "no number in it" : "its numbers [" + string.Join("; ", said) + "]";
    }

    // Where the words of a game's tooltip are drawn, in the picture's own pixels: its labels that are shown, each through
    // its canvas and its viewport (Viewport.GetScreenTransform), not the tooltip's own place in the root (Mining Rates'
    // check-3: the tooltip, drawn where the real pointer was, proved "outside the picture" by its own place). Null when
    // none is shown.
    static Rect2? DevDrawn(BaseTooltip tooltip, out string where)
    {
        Rect2? drawn = null;
        int parts = 0;
        foreach (Control c in tooltip.FindChildren("*", "", true, false).OfType<Control>())
        {
            if (c is not (RichTextLabel or Label) || !c.IsVisibleInTree() || c.Size.X < 1 || c.Size.Y < 1) continue;
            Rect2 at = c.GetViewport().GetScreenTransform() * c.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, c.Size);
            drawn = drawn is { } so ? so.Merge(at) : at;
            parts++;
        }
        string content;
        try { content = $"its content at {tooltip.LastContentPosition}"; }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException) { content = "its content's place not read"; }
        where = $"{parts} of its labels drawn at {drawn?.ToString() ?? "-"} in the picture's pixels; its own place {tooltip.GetGlobalRect()}, {content}";
        return drawn;
    }

    // ---- through the game's input, as a player's hand would (Fishing Rates' and Mining Rates' Dev.cs) -----------------

    Vector2I _devPointer;
    bool _devPointerSeen, _devHandSinceStep;
    ulong _devHandAt, _devGuardSince, _devTipSince;
    Control? _devAim;
    Vector2 _devAimAt;
    // How often in a row the game said the pointer is on another control than the one aimed at: something lies over it,
    // and after MostMisses the step says so at once instead of waiting out its time.
    int _devMisses;
    const int MostMisses = 10;

    // Every tick of a request: where the real pointer is, and whether a mouse button is down. Both are the system's own,
    // which the request's pushed pointer does not change (CombatGoals/DevWindowShots.cs, WatchHand).
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

    // True while a step that points or presses waits for a hand to leave the mouse; after GiveUpAfter the request ends,
    // which puts everything back (CombatGoals/DevWindowShots.cs, HandAtMouse).
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
    // control that is in sight, and a later tick presses only when the game says the pointer is on that control, so the
    // click can never land on a control of the game's beside it or over it. True once it was pressed.
    bool DevPress(Control target, string what)
    {
        if (!GodotObject.IsInstanceValid(target) || !target.IsVisibleInTree()) throw new InvalidOperationException($"{what} is not on the screen to be clicked.");
        if (DevHandAtMouse(what)) return false;
        Rect2 rect = DevSight(target);
        if (!rect.HasArea())
            throw new InvalidOperationException($"no part of {what} is in sight{(DevUnderWindow(target) ? ": it lies under the mod's own window" : "")}.");
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
            if (++_devMisses >= MostMisses)
                throw new InvalidOperationException($"the pointer was put on {what} {MostMisses} times, and each time the game said it is on {DevName(over)}: nothing was pressed");
            DevLog($"the pointer is on {DevName(over)}, not on {what}: nothing was pressed, it is put there again");
            DevPointer(_devAimAt);
            return false;
        }
        Vector2 spot = _ctx.Tree.Root.GetFinalTransform() * _devAimAt;
        Input.ParseInputEvent(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
        Input.ParseInputEvent(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = false });
        _devAim = null;
        _devMisses = 0;
        // A pressed control may take the keyboard focus: it is taken off once the press has been handled (DevFocusOff).
        _devFocusAfter = (target, (long)Engine.GetFramesDrawn());
        DevLog($"pressed {what} at {_devAimAt}, the pointer on {DevName(over)}");
        return true;
    }

    // The part of a control that can be seen: on the screen, inside the window's list when it is a line of it, inside
    // the page's list of trees when it is in there, and, for a control of the game, beside the mod's own window, which
    // lies over the page: the pointer put on the window's part would never be on the control.
    Rect2 DevSight(Control control)
    {
        Rect2 rect = control.GetGlobalRect().Intersection(_ctx.Tree.Root.GetVisibleRect());
        if (_list != null && GodotObject.IsInstanceValid(_list.DevScroll) && _list.DevScroll.IsAncestorOf(control))
            rect = rect.Intersection(_list.DevScroll.GetGlobalRect());
        else if (Native.ScrollOf(control) is { } scroll)
            rect = rect.Intersection(scroll.GetGlobalRect());
        if (DevUnderWindow(control)) rect = DevBeside(rect, _window!.Root.GetGlobalRect());
        return rect;
    }

    // A control that is not the mod's own window's, with the window on the screen over its place.
    bool DevUnderWindow(Control control) => _window is { IsAlive: true, Visible: true } window && control != window.Root &&
        !window.Root.IsAncestorOf(control) && control.GetGlobalRect().Intersects(window.Root.GetGlobalRect());

    // The largest part of a place that a cover leaves free: left, right, above or below it. Empty when it covers all.
    static Rect2 DevBeside(Rect2 place, Rect2 cover)
    {
        if (!place.Intersects(cover)) return place;
        Rect2[] parts =
        [
            new Rect2(place.Position, new Vector2(cover.Position.X - place.Position.X, place.Size.Y)),
            new Rect2(new Vector2(cover.End.X, place.Position.Y), new Vector2(place.End.X - cover.End.X, place.Size.Y)),
            new Rect2(place.Position, new Vector2(place.Size.X, cover.Position.Y - place.Position.Y)),
            new Rect2(new Vector2(place.Position.X, cover.End.Y), new Vector2(place.Size.X, place.End.Y - cover.End.Y)),
        ];
        return parts.Where(p => p.Size.X > 0 && p.Size.Y > 0).OrderByDescending(p => p.Size.X * p.Size.Y).FirstOrDefault();
    }

    // The pointer rests on a control and the game's tooltip comes up; its picture is shot-woodcutting-<shot>.png. A hand
    // at the mouse moves it away: then it waits and points again. A tooltip that has not come up in 20 s of pointing is
    // one line, and the request goes on.
    bool DevTooltip(Button target, Image? before, string shot)
    {
        if (DevHandAtMouse("the tooltip")) { _devTipSince = 0; return false; }
        if (_devTipSince == 0) _devTipSince = Time.GetTicksMsec();
        Control? over = _ctx.Tree.Root.GuiGetHoveredControl();
        if (over == null || (over != target && !target.IsAncestorOf(over)))
        {
            Rect2 rect = DevSight(target);
            if (!rect.HasArea()) throw new InvalidOperationException($"No part of '{target.Text}' is in sight for its tooltip.");
            DevPointer(rect.GetCenter());
            return false;
        }
        string tooltip = GameStyle.DevTooltips(_ctx.Tree.Root);
        string first = target.TooltipText.Split('\n')[0];
        if (first.Length > 0 && tooltip.Contains(first))
        {
            // Two drawn frames after it came up, for the picture.
            if (!DevFramesDrawn()) return false;
            DevLog($"tooltip | {shot} | {tooltip}");
            Shot(shot);
            if (before != null && DevTooltipRect() is { } at)
            {
                using var now = _ctx.Tree.Root.GetTexture().GetImage();
                DevLog($"shot-woodcutting-{shot}.png | " + DevChanged(before, now, at, "the tooltip's place", "VISIBLE", "NOT VISIBLE"));
            }
            DevPointerBack();
            return true;
        }
        if (Time.GetTicksMsec() - _devTipSince < TipWait) return false;
        DevLog($"tooltip | {shot} | NOT SHOWN in 20 s with the pointer on {DevName(over)}; its words are in the 'row' and 'tips' lines, and there is no picture of it");
        DevPointerBack();
        return true;
    }

    // A control in the picture just taken against a picture taken before it came, over the part of it that is beside the
    // mod's own window (DevSight): the window lies over the Woodcutting page, and a card it covers is not in the picture.
    string DevProofBeside(Image before, Control control, string what)
    {
        Rect2 whole = control.GetGlobalRect(), seen = DevSight(control);
        double under = whole.Area > 0 ? 1 - seen.Area / whole.Area : 0;
        string share = $"{what} at {whole}, {under:P0} of it under the mod's window or out of sight";
        if (!seen.HasArea()) return $"NOT PROVED: {share}";
        using var now = _ctx.Tree.Root.GetTexture().GetImage();
        return $"{share}; " + DevChanged(before, now, seen, what + " beside the window", "VISIBLE", "NOT VISIBLE");
    }

    // Where the Godot tooltip that is up lies, in the root's own coordinates (GameStyle.DevTooltips finds it the same way).
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

    // ---- pictures, and what their pixels prove ------------------------------------------------------------------------

    // A picture of the whole screen, and with crop one of the window alone, with the window's pixels against the screen
    // without it (DevChanged). A picture of a window that is not there, or of a list with no line in sight, is not taken.
    // A picture's name is letters, digits, - and _ only (lesson 49).
    void Shot(string state, bool crop = false)
    {
        if (!Regex.IsMatch(state, "^[A-Za-z0-9_-]+$")) throw new InvalidOperationException($"'{state}' is no name for a picture: letters, digits, - and _ only.");
        using var image = _ctx.Tree.Root.GetTexture().GetImage();
        void Save(Image picture, string file, string proof = "")
        {
            Error result = picture.SavePng(Path.Combine(_ctx.Directory, file));
            if (result != Error.Ok) throw new IOException(file + ": " + result);
            DevLog(file + proof + HandNote);
        }
        if (crop)
        {
            if (_window is not { IsAlive: true, Visible: true } window) throw new InvalidOperationException("The window is not on the screen for " + state + ".");
            if (!window.Minimised && DevRowsInSight() == 0) throw new InvalidOperationException("No line of the list is in sight for " + state + ".");
            // The picture is in the screen's pixels, the window in the game's own, which the game stretches.
            var frame = _ctx.Tree.Root.GetFinalTransform() * window.Root.GetGlobalRect().Grow(10);
            var part = ((Rect2I)frame).Intersection(new Rect2I(0, 0, image.GetWidth(), image.GetHeight()));
            if (!part.HasArea()) throw new InvalidOperationException("The window is outside the picture.");
            string proof = _devWithout == null ? " | NOT PROVED: no picture of the screen without the window"
                : " | " + DevChanged(_devWithout, image, window.Root.GetGlobalRect(), "the window", "VISIBLE", "NOT VISIBLE");
            Save(image, $"shot-woodcutting-{state}.png");
            using var region = image.GetRegion(part);
            Save(region, $"crop-woodcutting-{state}.png", proof);
            return;
        }
        Save(image, $"shot-woodcutting-{state}.png");
    }

    // How many of the pixels of a place (in the root's coordinates) differ between two pictures of the screen: a quarter
    // or more is "yes" (something else is drawn there now), as Thieving Overview proves its Risky tag (DevSeen).
    string DevChanged(Image before, Image after, Rect2 place, string what, string yes, string no) =>
        DevChangedPixels(before, after, _ctx.Tree.Root.GetFinalTransform() * place, what, yes, no);

    // The same over a place given in the picture's own pixels (DevDrawn).
    static string DevChangedPixels(Image before, Image after, Rect2 screen, string what, string yes, string no)
    {
        if (before.GetSize() != after.GetSize()) return $"{no}: the two pictures differ in size ({before.GetSize()} and {after.GetSize()}), so they cannot be compared";
        Rect2I at = ((Rect2I)screen).Intersection(new Rect2I(0, 0, after.GetWidth(), after.GetHeight()));
        if (!at.HasArea()) return $"{no}: {what} is outside the picture";
        long changed = 0, all = 0;
        // Every second pixel each way: enough to tell, and a quarter of the work.
        for (int y = at.Position.Y; y < at.End.Y; y += 2)
            for (int x = at.Position.X; x < at.End.X; x += 2)
            {
                Color a = before.GetPixel(x, y), b = after.GetPixel(x, y);
                all++;
                if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 0.1f) changed++;
            }
        double share = all == 0 ? 0 : (double)changed / all;
        return $"{(share >= 0.25 ? yes : no)}: {share:P0} of the {all:N0} pixels read at {at} ({what}) differ from the picture before";
    }

    int DevRowsInSight()
    {
        Rect2 sight = _list!.DevScroll.GetGlobalRect();
        return _list.DevLines().Count(l => l.Button.IsVisibleInTree() && l.Button.GetGlobalRect().Intersects(sight));
    }

    // The strip of round buttons around the mod's own, which must be on the screen and lit.
    void ShotStrip()
    {
        if (_opener?.GetParent() is not Control strip || !_opener.IsVisibleInTree()) throw new InvalidOperationException("The round button is not on the screen.");
        using var image = _ctx.Tree.Root.GetTexture().GetImage();
        var frame = _ctx.Tree.Root.GetFinalTransform() * strip.GetGlobalRect().Grow(12);
        var part = ((Rect2I)frame).Intersection(new Rect2I(0, 0, image.GetWidth(), image.GetHeight()));
        if (!part.HasArea()) throw new InvalidOperationException("The strip is outside the picture.");
        using var region = image.GetRegion(part);
        Error result = region.SavePng(Path.Combine(_ctx.Directory, "crop-woodcutting-strip.png"));
        if (result != Error.Ok) throw new IOException("crop-woodcutting-strip.png: " + result);
        DevLog($"crop-woodcutting-strip.png | the button at {_opener.GetGlobalRect()}, lit={_opener.ButtonPressed}, {strip.GetChildCount()} buttons in the strip{HandNote}");
    }
}
#endif
