#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GamesByMalcs.Melvor2.Components.ActionUntilX;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.CraftingAction;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.CustomControls;
using GamesByMalcs.Melvor2.Entities.Firemaking;
using GamesByMalcs.Melvor2.Entities.Firemaking.Statistics;
using GamesByMalcs.Melvor2.GameMenus;
using GamesByMalcs.Melvor2.GameMenus.Pages;
using GamesByMalcs.Melvor2.InfoIcons;
using GamesByMalcs.Melvor2.InfoLabels;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.Stats.Nodes;
using GamesByMalcs.Melvor2.Tooltips;
using GamesByMalcs.Melvor2.Utils;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace FiremakingRates;

// Development: a file "firemaking.request" in the mod's folder runs the light check below with nobody at the keyboard,
// one step about every half second, and writes "firemaking.done" when it ends: "ok" or "failed at <step>: <why>", and in
// both cases what was put back. Every step logs what it read as one-line "Dev: ..." entries in MelvorModLoader.log. It is
// short (about a minute and a half when nobody moves the mouse): no simulation, a few pictures. A second request,
// "firemakingwatch.request", only listens while the character burns logs (DevWatch.cs); one waits for the other.
//   pwsh .claude\codex\check-mod.ps1 -Worktree <worktree> -Mod FiremakingRates -Request firemaking.request -Produces firemaking.done -Out <folder>
// The request file may say "fail=<step>": that step then fails on purpose, to show that a failed request puts
// everything back (-Text 'fail=click'). Built on Woodcutting Rates' request as merged (mods/WoodcuttingRates/Dev.cs, after
// its six turns and four code reviews), for the Firemaking page.
// It only looks, points, and clicks the mod's own controls and, in step 9, a log's line and the page's own category and
// log buttons for the same log, always with the pointer: nothing is started or stopped, the page's Start button and every
// control of the bonfire are never named, pressed or pointed at, no stop condition is set, nothing is spent. What the
// request changes on the page is its selected category, its selected log and the scroll of its list of logs, in step 9
// only, and it puts each back through the game's public members a click on a line uses (FiremakingActionComponent.
// SelectCategory, SelectRecipe), never by setting what the page keeps behind them. The rule of lesson 56: nothing is
// selected while a log burns. Whether one burns is read again (LogRates.IsBurning, the widest test) on the tick of every
// press and of every put-back; while one burns, the click is one that only scrolls, the page's own buttons are not
// pressed, and a selection that waits for its put-back is not put back, said NOT PUT BACK. A click is
// made in two steps (DevPress): the pointer is put on the control, and a later step presses only when the game says the
// pointer is on that control; a control of the game under the mod's own window is pointed at beside it, or not at all
// (said at once). Nothing is pointed at or pressed while somebody is at the mouse: a step waits until the real pointer
// has been still for 5 s, and after 90 s the request ends, which puts everything back. A picture of something just shown
// is taken two drawn frames later, and its pixels prove what it shows (DevChanged). The pictures go into the mod's folder
// (shot-firemaking-*.png, crop-firemaking-*.png).
//  1. Remembers the page on the screen, whether the window was open and folded, the round button, settings.json as it
//     is on disk, the page's selected category and log and the scroll of its list of logs (as the members say and as the
//     page draws them), and what firemaking is doing (IsStarted, CreatingRecipe, the bonfire lit or out, the stop
//     condition of any action-until component).
//  2. Opens the Firemaking page the way the sidebar does, if another page is open, and reads back that it is shown;
//     stops with one line when it cannot be opened that way (then open it by hand and send the request again).
//  3. "Dev: strip": every round button in the strip. Closes the window if it was open.
//  4. "Dev: page words": rests the pointer on the parts of the page the game shows a tooltip for, as a player reads them:
//     the selected log's XP tag in the page's list, the doubling and preservation icons of the selected log's panel, and
//     its bonfire time, each on the control the game's tooltip shows for (Tooltip.Targets, Labelling), until the game
//     shows it filled, and logs it word for word, with every number in it named against the log's numbers
//     (shot-firemaking-xp-tooltip.png). A tooltip that is not shown in 20 s is one line, and the request goes on.
//  5. A picture of the screen without the window, for the proofs; presses the round button, and opens the window up
//     with its own button if it opened folded; the round button must be lit.
//  6. "Dev: row": every line's numbers as the game gives them (raw) and as counted, as drawn (with colours), and its
//     tooltip. "Dev: recipe rates": Recipe Rates' number under each log of the page's list (its RecipeRatesLabel) and
//     its block in the XP tag's tooltip, beside ours (DIFFER), or one line that Recipe Rates is not loaded. "Dev: game":
//     the selected log's panel (its name, interval, bonfire time, inputs and outputs, its doubling and preservation
//     icons, its list of bonuses) and the game's own list of what it makes (GetProducedRecipeItems,
//     GetProducedRecipeCurrencies) beside ours, DIFFER on a difference; every button of the page's list (its name, its
//     XP tag and the time it is bound to, its locked part) and the page's order of them against ours; every category
//     button against the categories the window lists. "Dev: words": the page's own words for the rules the hour rests
//     on. "Dev: hidden": every log not listed, and why. "Dev: lit". "Dev: tips". "Dev: room": the window's place, the
//     page's list and every log's button against it, and how much of the selected log's panel the window covers. "Dev:
//     cut" over every label of the picture. crop-firemaking-window.png and, when the list scrolls,
//     crop-firemaking-window-end.png.
//  7. Rests the pointer on a line, one whose button has no room beside the window when there is one, then on the name
//     of the column with the longest tip: shot-firemaking-tooltip.png, shot-firemaking-header-tooltip.png and "Dev:
//     tooltip". Then clicks "XP": "Dev: order" and crop-firemaking-sorted.png.
//  8. (the end of the list, as part of 6)
//  9. "Dev: click": only when the page shows a category and a log, so that both can be put back. While nothing burns: clicks
//     a line of a log the page does not show, first an unlocked one of another category. While a log burns: clicks only a
//     line whose click scrolls the page's list (a log of the category the page shows whose button is out of sight), or
//     none, and SELECTED WHILE A LOG BURNS marks a selection that changed anyway. When the burning changed between the
//     choice and the press, the click is not made. Reads back the page's selected category and log, what the page shows
//     in its own words (the selected log's name, the list's category), the lit line, the window, and what firemaking is
//     doing before and after, with every game event about them heard meanwhile (the action's Started and Stopped, its
//     timer's Started and Stopped apart from a fire's own restart, CreatingRecipeChanged, the bonfire's IsActiveChanged):
//     BURNING STARTED, BURNING STOPPED, THE LOG BURNING CHANGED, THE FIRE WAS RESET, THE BONFIRE CHANGED, THE STOP CHANGED,
//     NOT OPENED, THE PAGE SHOWS ANOTHER LOG and THE WINDOW CLOSED mark what must not happen. Picture
//     shot-firemaking-clicked.png, the log's button proved beside the window. Puts the selection back and, a tick after
//     the page settled, the scroll; then, only while nothing burns and only from his own selection, presses the page's
//     own category button and the page's own button of the log with the pointer as a player would, and compares (DIFFER,
//     EVENTS DIFFER); then puts the selection and the scroll back again. The category press waits for a still mouse;
//     once it is made, the request's selection stands on his page, and nothing waits for his hand any more: the log
//     button is scrolled clear if it must be, and pressed only while nobody is at the mouse and within LogPressMost;
//     every way out of the comparison (a hand at the mouse, a log burning, the button gone, disabled, out of sight, the
//     press not made) puts the selection back at once through the members, and the scroll on the next step. Each part is put back only while it is
//     still exactly what the request left, read once the page settled; anything else is the player's and is left, said in
//     one line (the rule of step 11).
// 10. "Dev: size": the window at its smallest and at its largest size (crop-firemaking-smallest.png,
//     crop-firemaking-largest.png), LARGER or OVER THE BOTTOM BAR when it does not fit, and "Dev: cut" at both. Closes
//     the window with its X: shot-firemaking-closed.png.
// 11. Puts back settings.json byte for byte from the copy read in step 1 (sort, size, place, fold), the window open or
//     closed and folded as it was, the page's selected category and log (in the click's order, each read again after
//     every member call and put back once more when the other call moved it; never while a log burns), and the scroll of
//     its list a tick later, each only if it is still as the request left it (otherwise the player changed it, and it is
//     left as he did); reads
//     the Firemaking page back while it is still on the screen; then the page that was on the screen, only if the
//     request opened Firemaking itself, and the pointer where the real one is. Woodcutting Rates' one rule for each part
//     of the page, each on its own: what the request left on a part is read from the page once the page has settled
//     after the request's own press (DevSettle, counted in process frames, so that a game folded down to the taskbar does
//     not stall it), and at the put-back the part is read again: still exactly that, it is put back; otherwise the player
//     changed it, and it is his: left, said in one line, never taken as the request's own, and it does not stop another
//     part's put-back (a category of his takes the scroll with it: the list is his category's then). The keyboard focus
//     is taken off a control right after the request's own press of it, if it has it (DevFocusOff); nothing is given
//     back. Every part (settings.json, the window, the selection, the scroll, the page, the pointer) is put back in its
//     own try, with one line: a part that fails is NOT AS IT WAS, and the next part runs all the same. The category and
//     the log are one selection here: a change of his to either lets go of both, and of the scroll, since putting one
//     back through the members may move the other.
// 12. Reads the rest back from the screen, the game and the disk, what firemaking is doing too, and says in its last line
//     what was put back, with NOT AS IT WAS for anything that is not, and for any put-back on the way that was not, and
//     NOT READ BACK for what could not be read. What the player changed himself is said as his, and is no fault.
// A step that fails jumps to 11: a failed request puts everything back too. If the mod stops while a request runs, or
// another character is opened, the done file says so, settings.json is written back from the copy and read again by the
// window, and the done line names where the request left the selection of its character (DevStopped, DevReset).
public partial class FiremakingRatesMod
{
    const string RequestFile = "firemaking.request", DoneFile = "firemaking.done";
    // Woodcutting Rates' and Fishing Rates' times: how long the real pointer must be still before a step points or
    // presses, and how long a step waits for that before the request ends.
    const ulong StillFor = 5000, GiveUpAfter = 90000, StepTime = 6000, PointerStepTime = GiveUpAfter + 10000;
    // How long the pointer rests on a part of the page for the game's tooltip before that tooltip is given up.
    const ulong TipWait = 20000;
    // The most the comparison's press of a log's button may take while the request's selection stands on his page (its
    // settle, a scroll, the aim and the press): past it the selection is put back and the comparison given up.
    const ulong LogPressMost = 5000;
    // A press of the request's that changes the page settles this many process frames after it, and a click on a line
    // waits at most this many for the window's own scroll to the log's button (ScrollToCard counts drawn frames).
    const long SettleFrames = 2, ScrollWaitFrames = 60;

    // Fishing Rates' longest tip of a column's name, line by line, and the longest line of its tooltips, in characters
    // (its Gold tip and its bait line, counted): Godot's tooltip does not wrap, so a longer line is a wider tooltip.
    const int FishingTipMost = 82, FishingTooltipMost = 94;

    readonly List<(string Name, Func<bool> Run, ulong Timeout)> _devSteps = new();
    int _devAt, _devPutBackAt;
    ulong _devStarted;
    long _devFrameMark;
    string? _devFailure, _devFailOnPurpose, _devPutBackWords;
    bool _devPutBackOk, _devPutBackRead, _devOpenedPage;
    // Put-backs on the way whose line says NOT AS IT WAS, and presses not made: the done line carries them, not only the
    // log (Mining Rates' code review of turn 3, fault 7).
    readonly List<string> _devOnTheWay = new();
    // The screen without the window, before it was opened: what the pictures of the window are proved against.
    Image? _devWithout;
    // settings.json as the request found it on disk (null: not there), for DevStopped too.
    string? _devSettingsCopy;
    bool _devSettingsRead;
    // Woodcutting Rates' one rule for each part of the page the request changes, here the selected category, the selected
    // log and the scroll of the list of logs, each on its own: what the request left on a part is read from the page
    // itself once the page has settled after the request's own press (_devSettle), and kept (null: the request owns
    // nothing of that part). At the put-back the part is read again: still exactly what the request left, it is put back;
    // otherwise the player changed it, and it is his: left, said in one line, never taken as the request's own.
    sealed record DevOwn<T>(T Value);
    DevOwn<ActiveFiremakingCategory?>? _devOwnCategory;
    DevOwn<ActiveFiremakingRecipe?>? _devOwnLog;
    DevOwn<int?>? _devOwnScroll;
    // The category the owned scroll was read under: the list scrolled is that category's, so once the page shows another
    // category, whoever put it there, the scroll is the player's list's and is never written (Woodcutting Rates' review 4).
    ActiveFiremakingCategory? _devScrollUnder;
    // A press of the request's that changes the page, not settled yet: the process frame it was made in, and whether it
    // was a change of the request's (Both: what it left on every part is read once it settled) or a put-back's press (only
    // the scroll the request still owns is read again: the press laid the list out anew).
    (long Frame, bool Both)? _devSettle;
    // The tick the last settled reading was taken in: the scroll is put back on a later tick, after it is read again.
    long _devTick, _devSettledTick = -1;
    // The page just before the request's own press that changed it, while nothing of the request's was left on it: what
    // every put-back goes back to (null: the request has changed nothing). _devOriginWait is the page as it is on every
    // tick until such a press is made (Mining Rates' code review of turn 3, fault 6).
    DevPageWas? _devOrigin, _devOriginWait;
    // What of the page the request let go of because the player changed it ("category", "log", "scroll").
    readonly HashSet<string> _devHis = new();
    // The control the request has just pressed with the pointer, and the frame: once the press has been handled, the
    // keyboard focus is taken off it if it has it (Woodcutting Rates' review 3: nothing is given back with GrabFocus).
    (Control Target, long Frame)? _devFocusAfter;
    // The Firemaking of the character the request runs for: when another character is opened meanwhile, the done line
    // names where the request left that one's selection.
    FiremakingActionComponent? _devAction;

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
        if (_devBurns != null) DevWatchEnd("stopped: another character was opened", summarise: false);
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
        if (_devBurns != null) DevWatchEnd("stopped: the mod stopped", summarise: false);
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

    // Where the request left the page's selection of its own character, when it could not put it back: the game keeps it
    // in the character's Firemaking (FiremakingActionComponent.SelectedCategory, SelectedRecipe; the component is written
    // to the save, WriteTo), so it may stay there.
    string DevLeftOn()
    {
        try
        {
            if (_devAction == null) return "the page's selection (no character read)";
            string now = $"{_devAction.SelectedCategory?.Data.Id ?? "none"} / {_devAction.SelectedRecipe?.Recipe.Id ?? "none"}";
            string was = _devOrigin is { } origin ? $"{origin.Category?.Data.Id ?? "none"} / {origin.Log?.Recipe.Id ?? "none"}" : "unchanged by the request";
            string left = _devOwnCategory != null || _devOwnLog != null
                ? $"the request had left it on {_devOwnCategory?.Value?.Data.Id ?? "(not its own)"} / {_devOwnLog?.Value?.Recipe.Id ?? "(not its own)"}"
                : "the request had nothing of its own left on it";
            return $"the selected category and log of the character the request ran for, now {now} ({left}; before the request's first change {was}), " +
                "which the game may keep in that character's save, and the scroll of its list";
        }
        catch (Exception e) { return $"the page's selection (not read: {e.GetType().Name})"; }
    }

    void DevRequest()
    {
        if (GameAccess.IsComputingOffline || _stopped) return;
        // The silent watch runs on its own; a light check waits for it to end, and the other way round.
        if (_devBurns != null) { DevWatchTick(); return; }
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
                DevLog($"Firemaking Rates check started, {_devPutBackAt} steps and the put-back; a step waits {StepTime / 1000} s at most, " +
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
            // next step (Woodcutting Rates' code review of turn 2, fault 4).
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
        // A scroll of the window's own still waiting for drawn frames is dropped: nothing moves his list after the request.
        _scrollTo = null;
        DevLog("Firemaking Rates check " + result);
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

    static string Id(ActiveFiremakingCategory? category) => category?.Data.Id ?? "none";
    static string Id(ActiveFiremakingRecipe? log) => log?.Recipe.Id ?? "none";

    // What the request found, to be put back as it was. Look: the page's selection as the page draws it (DevLook).
    sealed record DevBefore(GamePage? Page, bool Wanted, bool Shown, bool? Folded, bool? Lit, string? Settings,
        ActiveFiremakingCategory? Category, ActiveFiremakingRecipe? Log, int? Scroll, string? Look);

    // The Firemaking page's selected category and log, its list's scroll, and the selection as drawn at one moment.
    sealed record DevPageWas(ActiveFiremakingCategory? Category, ActiveFiremakingRecipe? Log, int? Scroll, string? Look);

    DevPageWas DevPageNow() => new(_firemaking!.Action.SelectedCategory, _firemaking.Action.SelectedRecipe, DevScrollNow(), DevLook());

    bool DevOwnsAny => _devOwnCategory != null || _devOwnLog != null || _devOwnScroll != null;

    // A press the request makes on the Firemaking page (a line, a category button, a log's button), on every tick until it
    // is made. It waits for the request's own last press to settle. While nothing of the request's is left on the page,
    // the page as it is on that tick is taken as what the put-backs go back to, so a selection the player chose while the
    // press waited for his hand stays his. While the request owns a part of the page and that part is not exactly what it
    // left, the player changed it: it is his, the request lets go of it, and the press is not made (null), with one line,
    // so that the request never presses over a page he is using; the done line says so. True once it was made: what it
    // leaves is read once the page has settled (DevSettle).
    bool? DevPagePress(Func<bool> press, DevBefore before, string what)
    {
        if (_devSettle != null) return false;
        bool owns = DevOwnsAny;
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
            DevPageWas was = _devOrigin ?? new DevPageWas(before.Category, before.Log, before.Scroll, before.Look);
            _devOrigin = _devOriginWait ?? DevPageNow();
            _devHis.Clear();
            if (!ReferenceEquals(_devOrigin.Category, was.Category) || !ReferenceEquals(_devOrigin.Log, was.Log) || _devOrigin.Scroll != was.Scroll)
                DevLog($"click | the page just before {what}: {Id(_devOrigin.Category)} / {Id(_devOrigin.Log)}, scrolled to {_devOrigin.Scroll?.ToString() ?? "-"}; " +
                    $"not as the request found it ({Id(was.Category)} / {Id(was.Log)}, scrolled to {was.Scroll?.ToString() ?? "-"}): the player changed it meanwhile, " +
                    "and that is what is put back");
        }
        _devSettle = ((long)Engine.GetProcessFrames(), true);
        return true;
    }

    // Each part the request owns, read again: one that is not exactly what the request left is the player's. The request
    // lets go of it (nothing of it is put back, and the read back says so). The category and the log are one selection on
    // this page: a change of his to either lets go of both, and of the scroll, since a member call that puts one back may
    // move the other (SelectRecipe may bring its own category, SelectCategory may move the log), and the list scrolled is
    // then his. A scroll of his touches no other part. Returns the parts he changed, in words.
    List<string> DevHisNow()
    {
        var moved = new List<string>();
        var action = _firemaking!.Action;
        bool categoryHis = _devOwnCategory is { } category && !ReferenceEquals(action.SelectedCategory, category.Value);
        bool logHis = _devOwnLog is { } log && !ReferenceEquals(action.SelectedRecipe, log.Value);
        if (categoryHis || logHis)
        {
            moved.Add($"the selection (now {Id(action.SelectedCategory)} / {Id(action.SelectedRecipe)}, the request left " +
                $"{(_devOwnCategory is { } c ? Id(c.Value) : "-")} / {(_devOwnLog is { } l ? Id(l.Value) : "-")})");
            if (_devOwnCategory != null) { _devOwnCategory = null; _devHis.Add("category"); }
            if (_devOwnLog != null) { _devOwnLog = null; _devHis.Add("log"); }
            if (_devOwnScroll != null)
            {
                moved.Add("the scroll with it (the list is his selection's now)");
                _devOwnScroll = null;
                _devHis.Add("scroll");
            }
        }
        if (_devOwnScroll != null && !ReferenceEquals(action.SelectedCategory, _devScrollUnder))
        {
            moved.Add($"the scroll (the page shows {Id(action.SelectedCategory)} now, the request's scroll was of {Id(_devScrollUnder)})");
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

    // Read on every frame while a press of the request's waits to settle (OnFrame), so that what the request left is
    // read a few frames after its press, not up to a tick later: a change of his in that gap would be taken as the
    // request's (Woodcutting Rates' code review of turn 4, its accepted risk, narrowed as it proposed).
    void DevSettleNow()
    {
        try { DevSettle(); }
        catch (Exception e) { _devSettle = null; DevLog($"page | what the request left could not be read: {e.GetType().Name}: {e.Message}"); }
    }

    // What the request's own last press left on the page, read once the page has settled: SettleFrames process frames
    // after the press (the layout needs no drawing, so a game folded down to the taskbar does not stall it: Woodcutting
    // Rates' code review of turn 4, fault 2), and after the window's own scroll to a log's button when the press was a
    // click on a line, waited for ScrollWaitFrames at most. A change of the request's: every part is its own now. A
    // put-back's press: the scroll it still owns is read again, since the press laid the list out anew.
    void DevSettle()
    {
        if (_devSettle is not { } settle) return;
        long now = (long)Engine.GetProcessFrames();
        if (now < settle.Frame + SettleFrames || (_scrollTo != null && now < settle.Frame + ScrollWaitFrames)) return;
        _devSettle = null;
        _devSettledTick = _devTick;
        if (_firemaking == null || _page == null || !GodotObject.IsInstanceValid(_page)) return;
        var action = _firemaking.Action;
        if (settle.Both)
        {
            _devOwnCategory = new(action.SelectedCategory);
            _devOwnLog = new(action.SelectedRecipe);
            _devOwnScroll = new(DevScrollNow());
            _devScrollUnder = action.SelectedCategory;
        }
        else if (_devOwnScroll != null)
        {
            _devOwnScroll = new(DevScrollNow());
            _devScrollUnder = action.SelectedCategory;
        }
        DevLog($"page | settled after the request's {(settle.Both ? "press" : "put-back")}: the request owns the category " +
            $"{(_devOwnCategory is { } c ? Id(c.Value) : "(none)")}, the log {(_devOwnLog is { } l ? Id(l.Value) : "(none)")} and the scroll " +
            $"{(_devOwnScroll is { } s ? s.Value?.ToString() ?? "-" : "(none)")}");
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
        _devOwnCategory = null;
        _devOwnLog = null;
        _devOwnScroll = null;
        _devSettle = null;
        _devSettledTick = -1;
        _devOrigin = _devOriginWait = null;
        _devOnTheWay.Clear();
        _devHis.Clear();
        _devFocusAfter = null;
        _devWithout?.Dispose();
        _devWithout = null;
        _devStarted = Time.GetTicksMsec();
        _devAction = _firemaking!.Action;
        GameView view = GameAccess.FindGameView() ?? throw new InvalidOperationException("No character is open.");
        var action = _firemaking!.Action;
        // settings.json as it is on disk, the copy everything of the window is put back from (lesson 50).
        _devSettingsCopy = File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : null;
        _devSettingsRead = true;
        var before = new DevBefore(DevNative.CurrentPage(view)?.Item1, _wanted, _window is { IsAlive: true, Visible: true },
            _window is { IsAlive: true } ? _window.Minimised : null, GodotObject.IsInstanceValid(_opener) ? _opener!.ButtonPressed : null,
            _devSettingsCopy, action.SelectedCategory, action.SelectedRecipe, DevScrollNow(), DevLook());
        string burningAtStart = DevBurning();
        void Add(string name, Func<bool> run, ulong timeout = StepTime) => _devSteps.Add((name, run, timeout));
        DevLog($"before: page {before.Page?.Id ?? "none"}, window {(before.Wanted ? "open" : "closed")}{(before.Shown ? " and on the screen" : "")}" +
            $"{(before.Folded == true ? ", folded" : "")}, round button {(before.Lit is { } lit ? lit ? "lit" : "unlit" : "not made yet")}, settings.json " +
            (before.Settings == null ? "not there" : $"{before.Settings.Length} characters: {before.Settings}") +
            $" | {burningAtStart}; the page's category {Id(before.Category)} and log {Id(before.Log)}, drawn {before.Look ?? "not read yet"}, " +
            $"the list of logs scrolled to {before.Scroll?.ToString() ?? "not found"} | the real pointer at {DisplayServer.MouseGetPosition()}");

        Add("open the Firemaking page", () =>
        {
            if (PageShown) return true;
            if (_devOpenedPage) return false;   // read back on the next ticks: the page is found once a second
            var option = _game!.Layout.Sidebar.Options.FirstOrDefault(o =>
                o.Page is EntityPage p && ReferenceEquals(p.Entity, _firemaking) &&
                o.Requirements?.AreMet != false && o.DisplayRequirements?.AreMet != false);
            if (option?.Page is not EntityPage page)
                throw new InvalidOperationException("Firemaking cannot be opened from the sidebar: open the Firemaking page by hand and send the request again.");
            view.SetCurrentPage(page);
            _devOpenedPage = true;
            return false;
        });
        Add("the round button", () =>
        {
            if (!PageShown || _opener?.IsVisibleInTree() != true) return false;
            // The list of a page the request had to open is read now that it is there.
            if (before.Scroll == null) before = before with { Scroll = DevScrollNow() };
            if (before.Look == null) before = before with { Look = DevLook() };
            DevLog($"page | the page is {DevNative.CurrentPage(view)?.Item1?.Id ?? "none"}, the Firemaking page is shown: {PageShown}; its list of logs scrolled to " +
                $"{before.Scroll?.ToString() ?? "not found"}; its selection drawn {before.Look ?? "NOT FOUND"}");
            DevStrip();
            if (IsOpen) Close();
            return true;
        });
        // The game's own words on the page's parts, filled in by the game, before the window is open over the page.
        List<DevAim>? aims = null;
        int aimAt = 0;
        Add("the page's words", () =>
        {
            if (aims == null)
            {
                if (!DevFramesDrawn()) return false;
                aims = DevAims();
                aimAt = 0;
                _devTipSince = 0;
                _devFrameMark = 0;
                if (aims.Count == 0) { DevLog("page words | nothing to point at (no log selected, or no part of the page in sight): the page's tooltips are not read this time"); return true; }
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
        }, GiveUpAfter + 4 * TipWait + 10000);
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
            if (rows == 0) throw new InvalidOperationException("The window lists no log.");
            DevRecipeRates();
            DevGame();
            DevWords();
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
            // A line whose button has no room beside the window first, so that the picture shows what its tooltip says then.
            target = (lines.FirstOrDefault(l => !l.Log.Locked && l.Log.Sight is CardSight.UnderWindow or CardSight.NoRoom).Button ??
                lines.FirstOrDefault(l => !l.Log.Locked).Button ?? lines.FirstOrDefault().Button)
                ?? throw new InvalidOperationException("No line to point at.");
            LogRates pointed = lines.First(l => l.Button == target).Log;
            DevLog($"tooltip | the line pointed at: {pointed.Active.Recipe.Id}, {DevCardSight(Native.Card(_page!, pointed.Active), false)}");
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

        // A click on the line of a log the page does not show, then the page's own buttons for the same log, to compare.
        // The page's selection is put back after each, and its list's scroll on a later tick.
        LogRates? clicked = null;
        DevClickEvents? events = null;
        Image? beforeClick = null;
        string burningBefore = "", ours = "", ourEvents = "", pageBack = "";
        bool categoryChanged = false, compareSkipped = false, comparePutBack = false, clickedWhileBurning = false, scrolledForButton = false;
        ActiveFiremakingCategory? categoryBeforeClick = null;
        ActiveFiremakingRecipe? logBeforeClick = null;

        // The scroll put back on its own, a tick after the page settled from the selection's put-back (DevScrollBack), and
        // one line that says the selection's put-back and the scroll's together.
        void AddScrollBack(string name, Func<bool> skip, string head)
        {
            Add(name, () =>
            {
                if (skip()) return true;
                // The scroll is read again on a later tick than the reading of what the request left (DevSettle).
                if (_devOwnScroll != null && (_devSettle != null || _devSettledTick >= _devTick)) return false;
                string scrollWords;
                try { scrollWords = DevScrollBack(); }
                catch (Exception e) { scrollWords = $"the scroll NOT AS IT WAS: its put-back failed ({e.GetType().Name}: {e.Message})"; }
                string said = DevJoin(pageBack, scrollWords);
                if (said.Length == 0) said = "nothing of the request's is left on the page";
                DevLog($"{head} {said}; drawn {DevLook() ?? "NOT FOUND"}");
                if (said.Contains("NOT AS IT WAS") || said.Contains("NOT PUT BACK")) _devOnTheWay.Add($"{head} {said}");
                pageBack = "";
                return true;
            });
        }
        Add("a line to click", () =>
        {
            var action = _firemaking!.Action;
            // A selection the request cannot put back is never made: a log is never taken off the page (DeselectRecipe is
            // never called), and no member shows no category.
            if (before.Log == null || before.Category == null || action.SelectedRecipe == null || action.SelectedCategory == null)
            {
                DevLog($"click | the page shows the category {Id(action.SelectedCategory)} and the log {Id(action.SelectedRecipe)}: a click would leave a selection the " +
                    "request cannot take off (DeselectRecipe is never called), so no line is clicked. Select a log on the Firemaking page by hand and send the request again.");
                return true;
            }
            Refresh();
            clickedWhileBurning = DevBurningWide();
            if (clickedWhileBurning)
            {
                // Lesson 56: while a log burns nothing is selected; the only click made is one that only scrolls.
                clicked = _list!.DevLines().Select(l => l.Log).FirstOrDefault(t => t.Click == Opens.Scroll && t.Clickable);
                if (clicked == null)
                {
                    DevLog("click | a log burns: a click selects nothing, and no line's click would scroll the page's list (every button of the category shown is in sight " +
                        "beside the window, or not shown): no line is clicked, and the page's own buttons are not pressed");
                    return true;
                }
            }
            else
            {
                var lines = _list!.DevLines().Select(l => l.Log).Where(t => t.Clickable && !t.OnPage && !t.Burning).ToList();
                // First an unlocked log of another category than the page shows (the category and the log change), then
                // another log of the same category, then a locked one (its category only), then any line a click changes.
                clicked = lines.FirstOrDefault(t => t.Click == Opens.Log && !t.CategoryOnPage) ?? lines.FirstOrDefault(t => t.Click == Opens.Log) ??
                    lines.FirstOrDefault(t => t.Click == Opens.Category && !t.CategoryOnPage) ?? _list.DevLines().Select(l => l.Log).FirstOrDefault(t => t.Clickable);
            }
            if (clicked == null) { DevLog("click | no line opens something a click would change: none is clicked."); return true; }
            _list!.DevScroll.EnsureControlVisible(DevLineOf(clicked));
            burningBefore = DevBurning();
            categoryBeforeClick = action.SelectedCategory;
            logBeforeClick = action.SelectedRecipe;
            DevLog($"click | the line of {clicked.Active.Recipe.Id} ({clicked.Active.Category?.Id ?? "no category"}{(clicked.OnPage ? ", the log the page shows" : "")}, " +
                $"a click opens {clicked.Click}, a log burns: {clickedWhileBurning}); before: {DevPageState(clicked.Active)}; {burningBefore}");
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
            // Read again on the tick of the press: when a log began or stopped burning since the line was chosen, its click
            // would do something else than chosen, and it is not made.
            if (DevBurningWide() != clickedWhileBurning)
            {
                string words = $"the click on the line of {clicked.Active.Recipe.Id} NOT MADE: a log {(clickedWhileBurning ? "no longer burns" : "burns now")}, which changes what the click does";
                if (_devAim != null) { _devAim = null; DevPointerBack(); }
                DevLog("click | " + words);
                _devOnTheWay.Add(words);
                events?.Dispose();
                events = null;
                clicked = null;
                return true;
            }
            events ??= new DevClickEvents(_firemaking!, DevLog);
            LogRates line = clicked;
            bool? pressed = DevPagePress(() => DevClickLine(line), before, $"the click on the line of {line.Active.Recipe.Id}");
            if (pressed != null) return pressed.Value;
            events.Dispose();
            events = null;
            clicked = null;
            return true;
        }, PointerStepTime);
        Add("the log shown", () =>
        {
            if (clicked == null) return true;
            // The list scrolls two drawn frames after the click (ScrollToCard); the picture two frames later. What the
            // click left is read once the page has settled (DevSettle), and the put-back reads it again a tick later.
            if (_devSettle != null || _devSettledTick >= _devTick || !DevFramesDrawn()) return false;
            var action = _firemaking!.Action;
            ActiveFiremakingRecipe log = clicked.Active;
            Refresh();
            CraftingRecipeButton? card = Native.Card(_page!, log);
            var lit = _list!.DevLines().Where(l => l.Log.OnPage).Select(l => l.Log.Active.Recipe.Id).ToList();
            string burning = DevBurning();
            ours = DevSelectionState(log);
            ourEvents = string.Join(", ", events!.Heard());
            categoryChanged = !ReferenceEquals(action.SelectedCategory, categoryBeforeClick);
            bool opened = ReferenceEquals(action.SelectedCategory?.Data, log.Category) && (clicked.Click != Opens.Log || ReferenceEquals(action.SelectedRecipe, log));
            string shownName = DevPanelName();
            bool nameRight = clicked.Click != Opens.Log || DevWordsOf(shownName) == DevWordsOf(log.Recipe.DisplayName.Text);
            string sight = card == null ? "  ITS BUTTON IS NOT IN THE PAGE'S LIST" : !card.IsVisibleInTree() ? "  ITS BUTTON IS NOT SHOWN"
                : Native.Place(card, Cover(), out _) switch
                {
                    CardSight.Clear => "",
                    CardSight.Scroll => "  NOT IN SIGHT: a scroll would still show its button clear of the window",
                    _ => DevLineOf(clicked).TooltipText.Contains("has no room") ? "  its button has no room beside the window, as the line's tooltip now says"
                        : "  NOT IN SIGHT, AND THE LINE'S TOOLTIP DOES NOT SAY THERE IS NO ROOM",
                };
            var litMeant = action.SelectedRecipe is { } selected && _list.DevLines().Any(l => ReferenceEquals(l.Log.Active, selected)) ? [selected.Recipe.Id] : new List<string>();
            // While a log burns the click only scrolls: the page's selection must be exactly as before it.
            string selectedWhileBurning = clickedWhileBurning && (!ReferenceEquals(action.SelectedCategory, categoryBeforeClick) || !ReferenceEquals(action.SelectedRecipe, logBeforeClick))
                ? $"  SELECTED WHILE A LOG BURNS (before {Id(categoryBeforeClick)} / {Id(logBeforeClick)})" : "";
            if (clickedWhileBurning) { opened = true; nameRight = true; }
            DevLog($"click | after the click on the line of {log.Recipe.Id}: {DevPageState(log)}; the page's own words: the selected log '{shownName}'; " +
                $"the game's events [{ourEvents}], {events.Fires} fires burnt meanwhile; {burning}" + selectedWhileBurning + (opened ? "" : "  NOT OPENED") + (nameRight ? "" : "  THE PAGE SHOWS ANOTHER LOG") + sight +
                DevBurningMarks(burningBefore, burning) + DevEventMarks(ourEvents) +
                (lit.SequenceEqual(litMeant) ? "" : $"  THE LIT LINE IS [{string.Join(", ", lit)}]") + (IsOpen ? "" : "  THE WINDOW CLOSED"));
            Shot("clicked");
            if (card != null && card.IsVisibleInTree() && beforeClick != null) DevLog("click | the log's button in shot-firemaking-clicked.png: " + DevProofBeside(beforeClick, card, "its button"));
            DevLog("click | the selected log's panel against the window: " + DevPanelCover());
            events.Dispose();
            events = null;
            pageBack = DevSelectionBack();
            return true;
        });
        AddScrollBack("the page back after the click", () => clicked == null, "click | the page's selection and scroll put back for the page's own buttons:");
        Add("the page's category button", () =>
        {
            if (clicked == null) return true;
            var action = _firemaking!.Action;
            // Lesson 56: the page's own buttons select, and nothing is selected while a log burns, read on every tick of
            // this step, the tick of the press too; a pointer already put on the button is taken back.
            if (clickedWhileBurning || DevBurningWide())
            {
                compareSkipped = true;
                if (_devAim != null) { _devAim = null; DevPointerBack(); }
                DevLog("click | a log burns: the page's own category and log buttons are not pressed, and the click is not compared with them");
                return true;
            }
            // He is using the page: the comparison, a development check only, is not made over it (Woodcutting Rates' code
            // review of turn 4, polish).
            if (_devHis.Count > 0 && !compareSkipped)
            {
                compareSkipped = true;
                DevLog($"click | the player changed the page meanwhile ({string.Join(", ", _devHis)}): the page's own buttons are not pressed, and the click is not compared");
                return true;
            }
            // The comparison starts only from his own selection: while the click's is not back yet, it is not made.
            if (_devOwnCategory != null || _devOwnLog != null)
            {
                compareSkipped = true;
                DevLog("click | the click's selection is not back on the page yet: the page's own buttons are not pressed, and the click is not compared; the final put-back tries again");
                return true;
            }
            if (!categoryChanged) { DevLog("click | the click left the category as it was: no category button to compare it with."); return true; }
            ActiveFiremakingCategory category = action.ActiveCategories[clicked.Active.Category];
            if (ReferenceEquals(action.SelectedCategory, category)) return true;
            Button button = DevCategoryButton(category) ?? throw new InvalidOperationException($"the page has no category button for {category.Data.Id}");
            if (_devAim != button && !DevSight(button).HasArea())
            {
                compareSkipped = true;
                DevLog($"click | the page's own category button of {category.Data.Id} lies under the mod's own window: it is not pressed, and the click is not compared with it");
                return true;
            }
            events ??= new DevClickEvents(_firemaking!, DevLog);
            bool? pressed = DevPagePress(() => DevPress(button, $"the page's own category button of {category.Data.Id} ('{button.Text}')"),
                before, $"the press of the page's own category button of {category.Data.Id}, to compare");
            if (pressed != null) return pressed.Value;
            compareSkipped = true;
            events.Dispose();
            events = null;
            return true;
        }, PointerStepTime);
        // Once the comparison's category press is made, the request's selection stands on his page until it is put back.
        // From then on nothing waits for his hand, and every way out of the comparison puts the selection back at once,
        // through the game's members, which need no free mouse, and its scroll on the next step (the code review of turn
        // 2, fault 1). A selection that cannot be put back because a log burns now is said NOT PUT BACK (lesson 56).
        void GiveUpComparison(string why)
        {
            compareSkipped = true;
            if (_devAim != null) { _devAim = null; DevPointerBack(); }
            events?.Dispose();
            events = null;
            if (!DevOwnsAny)
            {
                DevLog($"click | {why}; nothing of the request's is on the page");
                return;
            }
            comparePutBack = true;
            pageBack = DevSelectionBack();
            DevLog($"click | {why}; the selection put back at once: {pageBack}");
        }
        Add("the page's log button", () =>
        {
            if (clicked == null || compareSkipped) return true;
            if (_devSettle != null || _devSettledTick >= _devTick) return false;
            var action = _firemaking!.Action;
            ActiveFiremakingRecipe log = clicked.Active;
            // Nothing more to press: the comparison is read in the next step, which puts the selection back.
            if (clicked.Click != Opens.Log || ReferenceEquals(action.SelectedRecipe, log)) return true;
            // The request's selection stands on his page (its category press): no waiting on his hand from here on.
            bool stands = _devOwnCategory != null || _devOwnLog != null;
            // Lesson 56, read again on every tick of this step, the tick of the press too.
            if (DevBurningWide()) { GiveUpComparison($"a log burns now: the page's own button of {log.Recipe.Id} is not pressed, and the click is not compared with it"); return true; }
            if (!ReferenceEquals(action.SelectedCategory?.Data, log.Category))
            {
                GiveUpComparison($"the page shows the category {Id(action.SelectedCategory)}, not {log.Category?.Id}: the log's own button is not there to press, and the click is not compared");
                return true;
            }
            if (Native.Card(_page!, log) is not { } button)
            {
                GiveUpComparison($"the page's list has no button for {log.Recipe.Id}: it is not pressed, and the click is not compared");
                return true;
            }
            if (_devAim != button)
            {
                if (button.Disabled || !button.IsVisibleInTree())
                {
                    GiveUpComparison($"the page's own button of {log.Recipe.Id} is {(button.Disabled ? "disabled" : "not shown")}: it is not pressed, and the click is not compared with it");
                    return true;
                }
                if (!DevSight(button).HasArea())
                {
                    // Out of sight beside the window: the page's list is scrolled first so that the button stands clear, as
                    // ScrollToCard does, only while the scroll is still what the request left (its own); what that scroll
                    // leaves is the request's own, read again once the page settled.
                    if (!scrolledForButton && _devOwnScroll is { } own && DevScrollNow() == own.Value && Native.ScrollOf(button) is { } list &&
                        Native.Place(button, Cover(), out int to) == CardSight.Scroll)
                    {
                        scrolledForButton = true;
                        list.ScrollVertical = to;
                        _devSettle = ((long)Engine.GetProcessFrames(), false);
                        DevLog($"click | the page's own button of {log.Recipe.Id} lies under the mod's window: its list scrolled to {to} first, as a click on a line scrolls it");
                        return false;
                    }
                    GiveUpComparison($"the page's own button of {log.Recipe.Id} is not in sight beside the mod's window, and no scroll of the request's brings it: it is not pressed, and the click is not compared with it");
                    return true;
                }
            }
            if (stands && DevHandNow())
            {
                GiveUpComparison($"somebody is at the mouse: the page's own button of {log.Recipe.Id} is not pressed, so that the request's selection does not wait on his page, and the click is not compared with it");
                return true;
            }
            if (stands && Time.GetTicksMsec() - _devStarted > LogPressMost)
            {
                GiveUpComparison($"the page's own button of {log.Recipe.Id} could not be pressed within {LogPressMost / 1000} s (the pointer was not on it): the click is not compared with it");
                return true;
            }
            events ??= new DevClickEvents(_firemaking!, DevLog);
            bool? pressed = DevPagePress(() => DevPress(button, $"the page's own button of {log.Recipe.Id}"),
                before, $"the press of the page's own button of {log.Recipe.Id}, to compare");
            if (pressed != null) return pressed.Value;
            GiveUpComparison($"the press of the page's own button of {log.Recipe.Id} was not made");
            return true;
        }, PointerStepTime);
        Add("the page's buttons compared", () =>
        {
            if (clicked == null || compareSkipped || events == null) return true;
            if (_devSettle != null || _devSettledTick >= _devTick || !DevFramesDrawn()) return false;
            string game = DevSelectionState(clicked.Active), gameEvents = string.Join(", ", events.Heard()), burning = DevBurning();
            DevLog($"click | after the page's own buttons: {game}; the page's own words: the selected log '{DevPanelName()}'; the game's events [{gameEvents}], " +
                $"{events.Fires} fires burnt meanwhile; {burning}" +
                DevBurningMarks(burningBefore, burning) + DevEventMarks(gameEvents) + " | " +
                (game == ours ? "the page and the action as after the click on the line" : $"DIFFER from the click on the line ({ours})") + "; " +
                (DevSameEvents(gameEvents, ourEvents) ? "the same events" : $"EVENTS DIFFER from the click on the line ([{ourEvents}])"));
            events.Dispose();
            events = null;
            comparePutBack = true;
            pageBack = DevSelectionBack();
            return true;
        });
        AddScrollBack("the page back after its own buttons", () => clicked == null || !comparePutBack, "click | the page's selection and scroll put back:");
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
            if (open != null) DevLog("shot-firemaking-closed.png | where the window was: " + DevChanged(open, now, openAt, "the window's place", "gone from the picture", "STILL IN THE PICTURE"));
            return true;
        });

        _devPutBackAt = _devSteps.Count;
        // What the read-backs say, in order: the Firemaking page's selection and scroll are read before the page that was
        // on the screen comes back, the rest at the end.
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
        // next part runs all the same. carry: a NOT AS IT WAS in its words goes into the done line (the selection's words
        // go there later with its scroll's, in AddScrollBack).
        void Part(string part, Func<string> put, bool carry = true)
        {
            try
            {
                string words = put();
                DevLog($"put back | {part}: {words}");
                if (carry && words.Contains("NOT AS IT WAS")) _devOnTheWay.Add($"{part}: {words}");
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
        // The selection in a step of its own, so that settings.json and the window above never wait: a press of the
        // request's that has not settled yet is waited for, and the selection is read again a tick later. Then the scroll
        // on its own. The page that was on the screen comes back only after both.
        Add("put back the page's selection", () =>
        {
            if (_devSettle != null || _devSettledTick >= _devTick) return false;
            pageBack = "";
            Part("the page's selection", () =>
            {
                pageBack = DevSelectionBack();
                return pageBack;
            }, carry: false);
            return true;
        });
        AddScrollBack("put back the page's scroll", () => false, "put back | the page's selection and scroll:");
        Add("read back the Firemaking page", () =>
        {
            var action = _firemaking!.Action;
            // What the put-backs went back to: the page just before the request's first change, else as it was at the start.
            DevPageWas want = _devOrigin ?? new DevPageWas(before.Category, before.Log, before.Scroll, before.Look);
            if (!PageShown)
            {
                said.Add($"the Firemaking page's selection and scroll NOT READ BACK: the page on the screen is {DevNative.CurrentPage(view)?.Item1?.Id ?? "none"}, the player's");
                readAll = false;
                return true;
            }
            string when = _devOrigin != null && (!ReferenceEquals(_devOrigin.Category, before.Category) || !ReferenceEquals(_devOrigin.Log, before.Log) || _devOrigin.Scroll != before.Scroll)
                ? " (as the player left it before the request's first change)" : "";
            SayPage(ReferenceEquals(action.SelectedCategory, want.Category), "category", $"the page's category {Id(want.Category)}{when}", Id(action.SelectedCategory));
            SayPage(ReferenceEquals(action.SelectedRecipe, want.Log), "log", $"the page's log {Id(want.Log)}{when}", Id(action.SelectedRecipe));
            // A selection of the request's that is still on his page (NOT PUT BACK while a log burns, or a put-back that did
            // not take): what he should select by hand, in the page's names, so that one card can tell him (review 2, polish).
            bool categoryLeft = !ReferenceEquals(action.SelectedCategory, want.Category) && !_devHis.Contains("category");
            bool logLeft = !ReferenceEquals(action.SelectedRecipe, want.Log) && !_devHis.Contains("log");
            if (categoryLeft || logLeft)
                said.Add($"TO SELECT BY HAND on the Firemaking page: the category '{want.Category?.Data.DisplayName.Text ?? "-"}' and the log " +
                    $"'{want.Log?.Recipe.DisplayName.Text ?? "-"}' (it shows '{action.SelectedCategory?.Data.DisplayName.Text ?? "-"}' and '{action.SelectedRecipe?.Recipe.DisplayName.Text ?? "-"}')");
            // The selection as the page draws it too, not only the members (Mining Rates' code review of turn 1, fault 2).
            string? look = DevLook();
            SayPage(look != null && look == want.Look, _devHis.Contains("category") ? "category" : "log", $"the page's selection drawn {want.Look ?? "(not read at the start)"}", look ?? "not found");
            int? scroll = DevScrollNow();
            SayPage(scroll == want.Scroll, "scroll", $"the page's list of logs scrolled to {want.Scroll?.ToString() ?? "-"}", scroll?.ToString() ?? "-");
            return true;
        });
        Add("put back the page", () =>
        {
            Part("the page", () =>
            {
                if (before.Page == null || !_devOpenedPage) return "as it was: the request did not open Firemaking";
                GamePage? now = DevNative.CurrentPage(view)?.Item1;
                if (ReferenceEquals(now, before.Page)) return $"{before.Page.Id}, as it was";
                // Only the page the request opened itself: a page the player went to meanwhile is his.
                if (!PageShown) return $"left on {now?.Id ?? "none"}: the player went there from the Firemaking page the request opened, and it is his";
                view.SetCurrentPage(before.Page);
                return $"back to {before.Page.Id}, the page on the screen before the request opened Firemaking";
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
            string burningNow = DevBurning(bonfire: false);
            Say(burningNow == DevBurning(bonfire: false, from: burningAtStart), DevBurning(bonfire: false, from: burningAtStart), burningNow);
            bool litNow = _firemaking!.Bonfire.IsActive, litBefore = burningAtStart.Contains("the bonfire lit");
            if (litNow == litBefore) said.Add($"the bonfire {(litNow ? "lit" : "out")} as it was");
            else if (litBefore && _firemaking.Bonfire.DurationTimer.TimeLeft <= 0) said.Add("the bonfire went out by itself, its time run out (no fault of the request's)");
            else Say(false, $"the bonfire {(litBefore ? "lit" : "out")}", litNow ? "lit" : "out");
            said.Add("the pointer back where the real one is");
            // A put-back on the way that was not as it was counts, even when the last one mended it; a press not made is
            // said, and is no fault.
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
            // "fail=click" fails right after the click on a line, so that what the click left on the page (its category,
            // log and scroll) is put back by the final put-back (the code review of turn 1, fault 5).
            if (name == "click") name = "the log shown";
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

    bool DevBurningNow() => _game!.ActionManager.ActiveActions.Any(a => ReferenceEquals(a.Entity, _firemaking));

    // A log burns, by the widest test (LogRates.IsBurning), read again before every press and every put-back (lesson 56).
    bool DevBurningWide() => LogRates.IsBurning(_firemaking!, _game);

    // The stop condition and goal of every action-until component of Firemaking, if the game gives it one: no click of the
    // request's may set or clear one.
    string DevStop()
    {
        var stops = _firemaking!.Components.Values.OfType<ActionUntilXComponent>()
            .Select(u => $"{u.StopCondition?.GetType().Name ?? "no condition"}/{(u.StopGoal != null ? "a goal" : "no goal")}").ToList();
        return stops.Count == 0 ? "none (no action-until component)" : string.Join(", ", stops);
    }

    // What firemaking is doing: no click of the request may change it. Without the bonfire, which goes out by itself
    // when its time runs out (the final read back says it apart); from: those words read before, without the bonfire.
    string DevBurning(bool bonfire = true, string? from = null)
    {
        if (from != null) return Regex.Replace(from, @", the bonfire (lit|out)", "");
        var action = _firemaking!.Action;
        return $"firemaking: started {action.IsStarted}, active {DevBurningNow()}, the log burnt {Id(action.CreatingRecipe)}" +
            (bonfire ? $", the bonfire {(_firemaking.Bonfire.IsActive ? "lit" : "out")}" : "") + $", the stop {DevStop()}";
    }

    // What changed in what firemaking is doing, in the words that mark what must not happen.
    static string DevBurningMarks(string before, string after)
    {
        if (before == after) return "";
        string Part(string all, string key) => Regex.Match(all, key + @" ([\w:/ ()-]+?)(,|$)").Groups[1].Value;
        string marks = "";
        if (Part(before, "started") == "False" && Part(after, "started") == "True") marks += "  BURNING STARTED";
        if (Part(before, "started") == "True" && Part(after, "started") == "False") marks += "  BURNING STOPPED";
        if (Part(before, "the log burnt") != Part(after, "the log burnt")) marks += "  THE LOG BURNING CHANGED";
        if (Part(before, "the bonfire") != Part(after, "the bonfire")) marks += "  THE BONFIRE CHANGED";
        if (Part(before, "the stop") != Part(after, "the stop")) marks += "  THE STOP CHANGED";
        return marks.Length > 0 ? marks : "  FIREMAKING CHANGED";
    }

    // The events heard that must not come with a click.
    static string DevEventMarks(string events) => events.Contains("THE FIRE'S TIMER") ? "  THE FIRE WAS RESET (its timer started or stopped)" : "";

    // ---- a click on a line: the Firemaking page's selection and scroll ----------------------------------------------

    // Where the page's list of logs is scrolled to, or null when the list is not found.
    int? DevScrollNow()
    {
        try { return _page != null && GodotObject.IsInstanceValid(_page) && Native.List(_page) is { } list && DevNative.ListScroll(list) is { } scroll ? scroll.ScrollVertical : null; }
        catch (Exception) { return null; }
    }

    // The page's own words for the selected log: its panel's name label.
    string DevPanelName()
    {
        try { return _page != null && Native.ActionView(_page) is { } view && DevNative.SelectedName(view) is { } name && name.IsVisibleInTree() ? name.Text : "(not shown)"; }
        catch (Exception e) { return $"(not read: {e.GetType().Name})"; }
    }

    // The page's category button of a category (FiremakingActionView._categoryButtons), or null.
    Button? DevCategoryButton(ActiveFiremakingCategory category) =>
        Native.ActionView(_page!) is { } view && DevNative.CategoryButtons(view) is { } buttons && buttons.TryGetValue(category, out AnimatedButton? b) && GodotObject.IsInstanceValid(b) ? b : null;

    // The page's selection as the page draws it: its category buttons drawn pressed, the category its list shows, and the
    // selected log's name in its panel. Null when the page's parts are not there.
    string? DevLook()
    {
        try
        {
            if (_page == null || !GodotObject.IsInstanceValid(_page) || Native.ActionView(_page) is not { } view) return null;
            var pressed = (DevNative.CategoryButtons(view) ?? new Dictionary<ActiveFiremakingCategory, AnimatedButton>())
                .Where(p => GodotObject.IsInstanceValid(p.Value) && p.Value.ButtonPressed).Select(p => p.Key.Data.Id);
            string list = Native.List(_page) is { } select && DevNative.ListCategoryName(select) is { } name ? name.Text : "-";
            return $"category buttons pressed [{string.Join(", ", pressed)}], the list's category '{list}', the selected log '{DevPanelName()}'";
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException or NullReferenceException) { return null; }
    }

    // The page's selection, its look, and the log's button.
    string DevSelectionState(ActiveFiremakingRecipe log)
    {
        var action = _firemaking!.Action;
        CraftingRecipeButton? card = Native.Card(_page!, log);
        int cards = Native.Cards(_page!).Count();
        return $"the category {Id(action.SelectedCategory)}, the log {Id(action.SelectedRecipe)}, drawn {DevLook() ?? "NOT FOUND"}, {cards} buttons in the list, " +
            $"the button of {log.Recipe.Id} " + (card == null ? "not in the list" : card.IsVisibleInTree() ? "shown" : "hidden");
    }

    string DevPageState(ActiveFiremakingRecipe log) =>
        DevSelectionState(log) + $", {DevCardSight(Native.Card(_page!, log), true)}, the list scrolled to {DevScrollNow()?.ToString() ?? "-"}";

    // How a log's button stands against the mod's window, as a click on its line reads it (Native.Place); with places:
    // the button's, its list's and the window's, all as drawn (Native.Drawn), the window's read from its frame (Cover).
    string DevCardSight(CraftingRecipeButton? card, bool places)
    {
        if (card == null) return "its button not in the page's list";
        if (!card.IsVisibleInTree()) return "its button not shown";
        CardSight sight = Native.Place(card, Cover(), out int to);
        string said = sight switch
        {
            CardSight.Clear => "its button in sight, clear of the window",
            CardSight.Scroll => $"its button not in sight clear of the window, a scroll to {to} shows it",
            CardSight.UnderWindow => "its button not in sight clear of the window, and no scroll shows it beside the window",
            _ => "its button not in sight, and no scroll shows it whole",
        };
        if (!places) return said;
        string list = Native.ScrollOf(card) is { } scroll
            ? $"its list at {Native.Drawn(scroll)} scrolled to {scroll.ScrollVertical} of {scroll.GetVScrollBar().MinValue:0}..{Math.Max(0, scroll.GetVScrollBar().MaxValue - scroll.GetVScrollBar().Page):0}"
            : "no list";
        return $"{said} (the button at {Native.Drawn(card)}, {list}, the window at {Cover()?.ToString() ?? "none, not on the screen"})";
    }

    // How much of the selected log's panel the window covers, part by part (its name, its time, its bonfire time, its
    // list of bonuses, its inputs and outputs): a click opens a log there, and the player must see it.
    string DevPanelCover()
    {
        if (Native.ActionView(_page!) is not { } view) return "the page's part with the logs not found";
        Rect2? cover = Cover();
        var parts = new List<string>();
        foreach (var (name, control) in new (string, Control?)[] { ("the selected log's name", DevNative.SelectedName(view)), ("its time", DevNative.IntervalLabel(view)),
                     ("its bonfire time", DevNative.BonfireTimeLabel(view)), ("its list of bonuses", DevNative.StatsView(view)), ("its inputs", DevNative.Inputs(view)),
                     ("its outputs", DevNative.Outputs(view)) })
        {
            if (control == null || !GodotObject.IsInstanceValid(control) || !control.IsVisibleInTree()) { parts.Add($"{name} not shown"); continue; }
            Rect2 at = Native.Drawn(control);
            double under = cover is { } w && at.Area > 0 ? at.Intersection(w).Area / at.Area : 0;
            parts.Add($"{name} at {at}, {under:P0} under the window" + (under > 0.5 ? "  COVERED" : ""));
        }
        return string.Join("; ", parts);
    }

    // Every line's button against the window, and what its line says of it: the tooltip of a line whose button has no room
    // says so (NOT SAID otherwise); for the log the page shows already, the line is clickable only when a scroll shows
    // its button clear (MISMATCH otherwise). The window's place, the page's list and the selected log's panel are said
    // once.
    void DevRoom(string when)
    {
        var said = new List<string>();
        CraftingRecipeButton? any = null;
        foreach (var (t, button, _, _) in _list!.DevLines())
        {
            CraftingRecipeButton? card = Native.Card(_page!, t.Active);
            string sight = card is { } c && c.IsVisibleInTree() ? DevCardSight(card, false) : card == null ? "its button not in the list (another category)" : "its button hidden";
            if (card != null && card.IsVisibleInTree()) any ??= card;
            bool room = t.Sight is CardSight.UnderWindow or CardSight.NoRoom, tells = button.TooltipText.Contains("has no room");
            // A line whose click can only scroll: any line while a log burns, or the log shown, or a locked one of the
            // category shown.
            bool scrollOnly = t.Click == Opens.Scroll || (t.CategoryOnPage && (t.OnPage || t.Click == Opens.Category) && t.Click != Opens.Nothing);
            string marks = (scrollOnly && card != null && card.IsVisibleInTree() && t.Clickable != (t.Sight == CardSight.Scroll) ? "  MISMATCH: its line clickable " + t.Clickable : "") +
                (room && !tells ? "  NOT SAID in its tooltip" : "") + (!room && tells ? "  SAID in its tooltip WITH ROOM" : "");
            said.Add($"{t.Active.Recipe.LocalId}: {sight}, its line clickable {t.Clickable}{marks}");
        }
        string places = any != null ? DevCardSight(any, true) : "no button shown";
        // Where it opened: at the first place (FirstPlace) when the player had not placed it, or at his own.
        string opened = _firstPlace is { } first ? $"opened at the first place {first} (nothing saved by the player)"
            : _settings is { X: { } x, Y: { } y } ? $"at the player's own place ({x:0}, {y:0}) from settings.json" : "in the middle (no first place could be measured)";
        string strip = _opener?.GetParent() is Control buttons ? Native.Drawn(buttons).ToString() : "not found";
        DevLog($"room | {when} | the window at {Cover()?.ToString() ?? "none, not on the screen"}, from its frame {(_window is { IsAlive: true } w ? w.Root.GetGlobalRect().ToString() : "-")} " +
            $"at {(_window is { IsAlive: true } s ? s.Style.Scale.ToString("0.##") : "-")}, {opened}; the strip of round buttons at {strip}; the first button shown: {places}");
        DevLog($"room | {when} | {string.Join("; ", said)}");
        DevLog($"room | {when} | the selected log's panel: {DevPanelCover()}");
        var lines = _list.DevLines().Select(l => l.Log).ToList();
        int shown = lines.Count(t => t.Sight != null), clear = lines.Count(t => t.Sight == CardSight.Clear), scroll = lines.Count(t => t.Sight == CardSight.Scroll),
            under = lines.Count(t => t.Sight == CardSight.UnderWindow), none = lines.Count(t => t.Sight == CardSight.NoRoom);
        DevLog($"room | {when} | of the {shown} buttons the page's list shows, {clear + scroll} can be seen whole beside the window ({clear} in sight, {scroll} after a scroll), " +
            $"{under} have no room beside it, {none} no room at all; {lines.Count(t => t.Clickable)} of the {lines.Count} lines are clickable");
    }

    // The page's selection as it was just before the request's first change (_devOrigin), by Woodcutting Rates' one rule:
    // a part the request owns is read again; still exactly what the request left, it is put back; otherwise the player
    // changed it, and it is his (DevHisNow lets go of the whole selection and the scroll then). Through the game's public
    // members a click on a line uses, in the click's order, category then log (SelectCategory, SelectRecipe), never by
    // setting what the page keeps behind them. Both parts are read again after every member call, and when one call moved
    // the other part, that part is put back once more (the code review of turn 1, fault 4); a part that is still not as
    // it was stays the request's own, so that the final put-back tries again. Never while a log burns (lesson 56, read on
    // this tick): the selection is then NOT PUT BACK and stays the request's own. A log is never taken off (DeselectRecipe
    // is never called) and no member shows no category: when the page showed none, that part is NOT AS IT WAS (the click
    // step makes no click then). A scroll of the window's own to a log's button still waiting is dropped, so that it
    // cannot move the list after the put-back (fault 10). The scroll is put back on its own afterwards (DevScrollBack).
    string DevSelectionBack()
    {
        _scrollTo = null;
        if (_devOrigin is not { } origin || !DevOwnsAny) return "nothing of the request's is left on the page";
        if (_page == null || !GodotObject.IsInstanceValid(_page)) return "the Firemaking page is gone: its selection and scroll NOT AS IT WAS (not put back)";
        var action = _firemaking!.Action;
        var said = new List<string>();
        foreach (string part in DevHisNow()) said.Add($"{part} is the player's, and is left");
        bool CategoryWrong() => _devOwnCategory != null && !ReferenceEquals(action.SelectedCategory, origin.Category);
        bool LogWrong() => _devOwnLog != null && !ReferenceEquals(action.SelectedRecipe, origin.Log);
        if ((CategoryWrong() || LogWrong()) && DevBurningWide())
            return DevJoin(said.Append($"the selection NOT PUT BACK: a log burns now, and selecting is not done while one burns; it stays " +
                $"{Id(action.SelectedCategory)} / {Id(action.SelectedRecipe)} (before the request's first change {Id(origin.Category)} / {Id(origin.Log)}), the request's own " +
                "for the final put-back").ToArray());
        if (CategoryWrong() && origin.Category == null)
        {
            _devOwnCategory = null;
            said.Add($"the category NOT AS IT WAS: it stays {Id(action.SelectedCategory)}, since the page showed none before and no member shows none");
        }
        if (LogWrong() && origin.Log == null)
        {
            _devOwnLog = null;
            said.Add($"the log NOT AS IT WAS: it stays {Id(action.SelectedRecipe)}, since the page showed none before and a log is never taken off (DeselectRecipe is never called)");
        }
        bool pressed = false;
        var calls = new List<string>();
        // Twice at most: the second round puts back what the first round's other call moved.
        for (int round = 0; round < 2 && (CategoryWrong() || LogWrong()); round++)
        {
            if (CategoryWrong())
            {
                action.SelectCategory(origin.Category!);
                calls.Add($"SelectCategory({Id(origin.Category)}): now {Id(action.SelectedCategory)} / {Id(action.SelectedRecipe)}");
                pressed = true;
            }
            if (LogWrong())
            {
                action.SelectRecipe(origin.Log!);
                calls.Add($"SelectRecipe({Id(origin.Log)}): now {Id(action.SelectedCategory)} / {Id(action.SelectedRecipe)}");
                pressed = true;
            }
        }
        // A part still not as it was stays the request's own, with what the request's own put-back left on it, so that a
        // later reading never takes that for a change of his (Woodcutting Rates' review 3: the request's own write stays
        // its own).
        if (_devOwnCategory != null)
        {
            if (!CategoryWrong()) { _devOwnCategory = null; said.Add(pressed ? $"the category put back to {Id(origin.Category)}" : "the category as it was"); }
            else
            {
                _devOwnCategory = new(action.SelectedCategory);
                said.Add($"the category NOT AS IT WAS: it reads {Id(action.SelectedCategory)}, which stays the request's own for the final put-back");
            }
        }
        if (_devOwnLog != null)
        {
            if (!LogWrong()) { _devOwnLog = null; said.Add(pressed ? $"the log put back to {Id(origin.Log)}" : "the log as it was"); }
            else
            {
                _devOwnLog = new(action.SelectedRecipe);
                said.Add($"the log NOT AS IT WAS: it reads {Id(action.SelectedRecipe)}, which stays the request's own for the final put-back");
            }
        }
        if (calls.Count > 0) said.Add($"by [{string.Join("; ", calls)}]");
        // The put-back lays the list out anew: the scroll the request still owns is read again once it settled.
        if (pressed) _devSettle = ((long)Engine.GetProcessFrames(), false);
        return DevJoin(said.ToArray());
    }

    // The scroll the request owns, read again on a later tick than what it left was read (DevSettle; the step waits for
    // it): still exactly that, it is put back to where it was before the request's first change; otherwise the player
    // moved it, and it is his, and left. While the category is still the request's and not put back, the scroll is left
    // with it, for the final put-back. When the write does not take, what the request's own write left stays its own.
    string DevScrollBack()
    {
        if (_devOwnScroll is not { } own || _devOrigin is not { } origin) return "";
        if (_page == null || !GodotObject.IsInstanceValid(_page)) return "the Firemaking page is gone: its scroll NOT AS IT WAS (not put back)";
        if (_devOwnCategory is { } category && !ReferenceEquals(category.Value, origin.Category))
            return "the scroll left with the request's category, which is not put back: both stay the request's own for the final put-back";
        var action = _firemaking!.Action;
        if (!ReferenceEquals(action.SelectedCategory, _devScrollUnder))
        {
            _devOwnScroll = null;
            _devHis.Add("scroll");
            return $"the scroll is left: the page shows {Id(action.SelectedCategory)} now, and the request's scroll was of {Id(_devScrollUnder)}, so the list on the screen is the player's";
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
        if (origin.Scroll is not { } back || Native.List(_page) is not { } list || DevNative.ListScroll(list) is not { } scroll)
        {
            _devOwnScroll = null;
            return "the scroll NOT AS IT WAS: where it stood before the request's first change was not read, so it is not written";
        }
        scroll.ScrollVertical = back;
        int? after = DevScrollNow();
        if (after == back)
        {
            _devOwnScroll = null;
            return $"the scroll put back to {back}";
        }
        _devOwnScroll = new(after);
        _devScrollUnder = action.SelectedCategory;
        return $"the scroll NOT AS IT WAS (put back to {back}, it reads {after?.ToString() ?? "-"}); what the request's write left stays its own";
    }

    Button DevLineOf(LogRates log) => _list!.DevLines().First(l => ReferenceEquals(l.Log.Active, log.Active)).Button;

    // The line is pressed the way a player's hand would (DevPress), once the list has scrolled it into sight.
    bool DevClickLine(LogRates log)
    {
        Button line = DevLineOf(log);
        if (!DevSight(line).HasArea()) return false;
        return DevPress(line, $"the line of {log.Name}");
    }

    // The same events in another order are the same.
    static bool DevSameEvents(string a, string b) =>
        a.Split(", ", StringSplitOptions.RemoveEmptyEntries).Order().SequenceEqual(b.Split(", ", StringSplitOptions.RemoveEmptyEntries).Order());

    // The game's events about the page's selection and about firemaking, listened to while one click is made: the
    // category and the log selected, the log burnt, the action started or stopped, the fire's own timer started or
    // stopped (a reset of the burn in progress), the bonfire lit or put out. A fire that burns meanwhile fires its timer,
    // which may start it again in the same frame: that is the fire going on, counted apart (Fires), never a reset, and it
    // is left out of what is compared, since a fire may burn during one click and not during the other. Each listener
    // runs inside a try; they are taken off by Dispose, which Stop and Detach reach through DevUnwatchAll.
    sealed class DevClickEvents : IDisposable
    {
        public readonly List<string> Seen = new();
        readonly List<(long Frame, string Kind)> _timer = new();
        readonly List<Action> _off = new();
        static DevClickEvents? s_current;

        /// <summary>The fires that burnt while the click was listened to.</summary>
        public int Fires => _timer.Count(t => t.Kind == "fired");

        /// <summary>What was heard, with the fire's timer started or stopped in a frame without a fire of its own.</summary>
        public List<string> Heard()
        {
            var all = new List<string>(Seen);
            var fired = _timer.Where(t => t.Kind == "fired").Select(t => t.Frame).ToHashSet();
            foreach (var (frame, kind) in _timer)
                if (kind != "fired" && !fired.Contains(frame)) all.Add($"THE FIRE'S TIMER {kind.ToUpperInvariant()}");
            return all;
        }

        public DevClickEvents(FiremakingEntity firemaking, Action<string> log)
        {
            s_current?.Dispose();
            s_current = this;
            var action = firemaking.Action;
            var bonfire = firemaking.Bonfire;
            var timer = action.ActionTimer;
            void Safe(Action add)
            {
                try { add(); }
                catch (Exception e) { log("click events | a listener failed: " + e.Message); }
            }
            EventHandler<ValueChangedEventArgs<ActiveFiremakingCategory>> category = (_, e) => Safe(() => Seen.Add($"the category {Id(e.NewValue)}"));
            EventHandler<ValueChangedEventArgs<ActiveFiremakingRecipe>> selected = (_, e) => Safe(() => Seen.Add($"the log {Id(e.NewValue)}"));
            EventHandler<ValueChangedEventArgs<ActiveFiremakingRecipe>> creating = (_, e) => Safe(() => Seen.Add($"THE LOG BURNING CHANGED to {Id(e.NewValue)}"));
            EventHandler<ActionEventArgs> started = (_, _) => Safe(() => Seen.Add("BURNING STARTED")),
                stopped = (_, _) => Safe(() => Seen.Add("BURNING STOPPED"));
            EventHandler timerStarted = (_, _) => Safe(() => _timer.Add(((long)Engine.GetProcessFrames(), "started"))),
                timerStopped = (_, _) => Safe(() => _timer.Add(((long)Engine.GetProcessFrames(), "stopped"))),
                timerFired = (_, _) => Safe(() => _timer.Add(((long)Engine.GetProcessFrames(), "fired")));
            EventHandler<ValueChangedEventArgs<bool>> lit = (_, e) => Safe(() => Seen.Add($"THE BONFIRE {(e.NewValue ? "LIT" : "PUT OUT")}"));
            action.SelectedCategoryChanged += category;
            action.SelectedRecipeChanged += selected;
            action.CreatingRecipeChanged += creating;
            action.Started += started;
            action.Stopped += stopped;
            timer.Started += timerStarted;
            timer.Stopped += timerStopped;
            timer.Fired += timerFired;
            bonfire.IsActiveChanged += lit;
            _off.Add(() =>
            {
                action.SelectedCategoryChanged -= category;
                action.SelectedRecipeChanged -= selected;
                action.CreatingRecipeChanged -= creating;
                action.Started -= started;
                action.Stopped -= stopped;
                timer.Started -= timerStarted;
                timer.Stopped -= timerStopped;
                timer.Fired -= timerFired;
                bonfire.IsActiveChanged -= lit;
            });
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
        DevUnwatchBurns();
    }

    // ---- what is logged ------------------------------------------------------------------------------------------

    static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    static string R(double? value) => value is { } v ? R(v) : "-";

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
            FiremakingActionStats s = t.Active.Stats;
            FiremakingRecipe recipe = t.Active.Recipe;
            string Items(IEnumerable<ValueStatNode<GamesByMalcs.Melvor2.Data.Items.ItemQuantityChance>>? nodes) =>
                string.Join(", ", (nodes ?? []).Select(n => $"{n?.Value?.Item?.Id} {R(n?.Value?.Chance ?? double.NaN)} x{n?.Value?.Quantity}"));
            DevLog($"row | {when} | {recipe.Id} ({recipe.Category?.Id}) | locked={t.Locked}, level={t.Level}, needs '{t.Needs}', on the page={t.OnPage}, burning={t.Burning}, " +
                $"its category on the page={t.CategoryOnPage}, a log burns={t.AnyBurning}, a click opens {t.Click}, clickable={t.Clickable}, its button {t.Sight?.ToString() ?? "-"} | " +
                $"raw: interval {R(s.Interval.Value)} ms (the recipe's {recipe.BaseInterval}), XP {R(s.Experience.XpToAdd.Value)} (the recipe's {recipe.BaseExperience}), " +
                $"item cost [{string.Join(", ", (s.ItemCost?.Values ?? []).Select(n => $"{n?.Value?.Item?.Id} x{n?.Value?.Quantity}"))}], currency cost " +
                $"[{string.Join(", ", (s.CurrencyCost?.Values ?? []).Select(n => $"{n?.Value?.Currency?.Id} x{n?.Value?.Quantity}"))}], product chances [{Items(s.ProductChances?.Values)}], " +
                $"the crafting stats' products [{Items(((ICraftingActionStats)s).Products?.Values)}]" +
                (Items(s.ProductChances?.Values) == Items(((ICraftingActionStats)s).Products?.Values) ? "" : "  DIFFER") +
                $", currency reward {s.CurrencyReward?.Value?.Currency?.Id} x{s.CurrencyReward?.Value?.Quantity} (the recipe's {recipe.CurrencyReward?.Currency?.Id} x{recipe.CurrencyReward?.Quantity}), " +
                $"doubling {R(s.DoublingChance?.Total.Value ?? double.NaN)}, preservation {R(s.PreservationChance?.Total.Value ?? double.NaN)}, " +
                $"bonfire time {R(FiremakingBonfireComponent.GetBonfireTimeForRecipe(t.Active))} | counted: {R(t.Seconds)} s a fire, {R(t.FiresPerHour)} fires/h, " +
                $"XP {R(t.XpEach)} a fire = {R(t.XpPerHour)}/h, logs burnt {t.Log?.Item.Id} {R(t.LogsPerHour)}/h (kept {R(t.PreservationChance)}), " +
                $"cost {R(t.CostPerHour)} gold/h [{string.Join(", ", t.Costs.Select(c => $"{c.Item.Id} {R(c.PerHour)}/h at {R(c.Price)} = {R(c.GoldPerHour)}"))}; " +
                $"{string.Join(", ", t.CurrencyCosts.Select(c => $"{c.Currency.Id} {R(c.PerHour)}/h"))}], coins {t.Coins?.Id} {t.CoinsEach} a fire = {R(t.CoinsPerHour)}/h, " +
                $"items [{string.Join(", ", t.Products.Select(p => $"{p.Item.Id} {R(p.Chance)} x{p.Quantity} each {R(p.Each)} = {R(p.PerHour)}/h, gold {R(p.GoldPerHour)}/h"))}], " +
                $"gold {R(t.GoldPerHour)}/h, the game's bonfire time (raw, not shown) {R(t.BonfireRaw)}, the bank's stock lasts {R(t.SecondsOfStock)} s | " +
                $"events [{string.Join(", ", t.Events.Select(e => $"{e.Name}: {e.Reward}"))}] | picture {DevTexture(picture)}" +
                (picture != null && _pictures!.IsStandIn(picture) ? "  STAND-IN DRAWN" : ""));
            DevLog($"row | {when} | drawn {drawn}");
            DevLog($"row | {when} | tooltip '{button.TooltipText}'");
        }
        var logs = ReadLogs();
        var action = _firemaking!.Action;
        DevLog($"rows | {when} | {count} lines, {DevRowsInSight()} in sight; the game has {action.ActiveRecipes.Count} logs in {action.ActiveCategories.Count} categories, " +
            $"{logs.Count} listed, {logs.Count(t => t.Locked)} locked; columns [{string.Join(", ", _list!.DevColumns)}]; the rules: XP {DevRule(LogRates.XpRule)}, " +
            $"logs {DevRule(LogRates.LogsRule)}, items {DevRule(LogRates.ItemsRule)}, coins {DevRule(LogRates.CoinsRule)}, bonfire {DevRule(LogRates.BonfireRule)}; " +
            $"a log burns now (the widest test): {DevBurningWide()}, so a click {(DevBurningWide() ? "only scrolls" : "selects")}" + (count != logs.Count ? "  MISMATCH" : ""));
        return DevRowsInSight();
    }

    static string DevRule(Settled? rule) => rule == null ? "NOT SETTLED" : rule.Source.ToString();

    // Recipe Rates' numbers for the same logs (mods/RecipeRates): its number under each log's XP tag in the page's list
    // (its RecipeRatesLabel, the label "Number") and its block in the XP tag's tooltip ("Per hour (estimate)"), beside ours
    // written as Recipe Rates writes numbers (Rates.Number and Rates.Compact, copied below for the comparison only).
    void DevRecipeRates()
    {
        int found = 0;
        var lines = new List<string>();
        foreach (LogRates t in ReadLogs())
        {
            if (Native.Card(_page!, t.Active) is not { } card || !card.IsVisibleInTree()) continue;
            SkillXPPanel? xp = DevNative.ButtonXp(card);
            if (xp == null) continue;
            string under = xp.FindChild("RecipeRatesLabel", true, false) is Node label && label.GetNodeOrNull<Label>("Number") is { } number && ((CanvasItem)label).Visible ? number.Text : "";
            string block = DevRecipeRatesBlock(DevNative.XpTooltip(xp));
            if (under.Length == 0 && block.Length == 0) continue;
            found++;
            var said = new List<string>();
            if (t.XpPerHour is { } ours)
                said.Add(under.Length == 0 ? "no number under the tag" : under == RecipeRatesNumber(ours) || under == RecipeRatesCompact(ours)
                    ? $"under the tag '{under}', ours {R(ours)}: the same" : $"under the tag '{under}', ours {R(ours)} ('{RecipeRatesNumber(ours)}')  DIFFER");
            if (block.Length > 0)
            {
                var read = block.Split('\n').Select(l => l.Split(": ", 2)).Where(p => p.Length == 2).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1].Trim());
                void Hold(string name, string? theirs, double? value, string sign = "")
                {
                    if (theirs == null) { said.Add($"'{name}' not in its block"); return; }
                    string want = value is { } v ? sign + RecipeRatesNumber(v) : "-";
                    string bare = theirs.Split(' ')[0];
                    said.Add(bare == want ? $"'{name}' {theirs}: the same" : $"'{name}' {theirs}, ours {want}  DIFFER");
                }
                Hold("Skill XP", read.GetValueOrDefault("Skill XP"), t.XpPerHour);
                if (t.Coins != null) Hold(t.CoinsName, read.GetValueOrDefault(t.CoinsName), t.CoinsPerHour, "+");
                foreach (LogRates.Product p in t.Products) Hold(p.Name, read.GetValueOrDefault(p.Name), p.PerHour, "+");
                foreach (LogRates.Cost c in t.Costs) Hold(c.Name, read.GetValueOrDefault(c.Name), c.PerHour is { } h ? -h : null);
                if (t.ItemsGoldPerHour >= 1) Hold("Sell value", read.GetValueOrDefault("Sell value"), t.ItemsGoldPerHour);
                string? every = block.Split('\n').FirstOrDefault(l => l.StartsWith("One every "));
                said.Add(every == null ? "no 'One every'" : every == $"One every {t.Seconds:0.##}s" ? $"'{every}': the same" : $"'{every}', ours {t.Seconds:0.##}s  DIFFER");
                said.Add($"its block '{block}'");
            }
            lines.Add($"{t.Active.Recipe.LocalId}: {string.Join("; ", said)}");
        }
        DevLog(found == 0 ? "recipe rates | Recipe Rates is not loaded, or writes nothing on the page's list: no RecipeRatesLabel under the XP tags and no block in their tooltips"
            : $"recipe rates | {found} logs of the page's list: {string.Join(" | ", lines)}");
    }

    // Recipe Rates' block in a tooltip of the game's, without its colours, or "".
    static string DevRecipeRatesBlock(TextTooltip? tooltip)
    {
        if (tooltip == null) return "";
        string? block = (DevNative.Content(tooltip) ?? []).FirstOrDefault(l => l.Contains("Per hour (estimate)"));
        return block == null ? "" : string.Join("\n", DevWordsOf(block).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    // Recipe Rates' Rates.Number and Rates.Compact (mods/RecipeRates/Rates.cs), copied only to hold its numbers against ours.
    static string RecipeRatesNumber(double value) => Math.Abs(value) switch
    {
        >= 1_000_000 => (value / 1_000_000).ToString("0.##") + "M",
        >= 100_000 => (value / 1000).ToString("0.#") + "K",
        >= 100 => value.ToString("N0"),
        _ => value.ToString("0.#"),
    };

    static string RecipeRatesCompact(double value) => Math.Abs(value) switch
    {
        >= 1_000_000 => (value / 1_000_000).ToString("0.#") + "M",
        >= 10_000 => (value / 1000).ToString("0") + "K",
        >= 1000 => (value / 1000).ToString("0.#") + "K",
        _ => value.ToString("0"),
    };

    // The longest line of every tip of the window in characters, against Fishing Rates' longest: Godot's tooltip does
    // not wrap, so a longer line makes a wider tooltip than Fishing Rates' (Mining Rates' code review of turn 1, fault 3).
    void DevTips(string when)
    {
        static string Longest(string text) => text.Split('\n').OrderByDescending(l => l.Length).First();
        static string Over(string line, int most) => line.Length > most ? $"  LONGER THAN FISHING RATES' {most}" : "";
        var heads = _list!.DevHeaders.Select(h => (h.Text, Line: Longest(h.TooltipText))).ToList();
        var lines = _list.DevLines().Select(l => (l.Log.Active.Recipe.LocalId, Line: Longest(l.Button.TooltipText))).ToList();
        string longest = heads.Select(h => h.Line).Concat(lines.Select(l => l.Line)).OrderByDescending(l => l.Length).First();
        DevLog($"tips | {when} | the names' tips, the longest line of each: {string.Join(", ", heads.Select(h => $"{h.Text} {h.Line.Length}{Over(h.Line, FishingTipMost)}"))}" +
            $" | the lines' tooltips: {string.Join(", ", lines.Select(l => $"{l.LocalId} {l.Line.Length}{Over(l.Line, FishingTooltipMost)}"))}" +
            $" | the longest of all, {longest.Length}: '{longest}'");
    }

    // The game's own labels for the selected log's panel and for every button of the page's list, next to ours; the
    // page's order of buttons and its category buttons.
    void DevGame()
    {
        var action = _firemaking!.Action;
        var logs = new Dictionary<ActiveFiremakingRecipe, LogRates>(ReadLogs().Select(t => KeyValuePair.Create(t.Active, t)), ReferenceEqualityComparer.Instance);
        FiremakingActionView view = Native.ActionView(_page!) ?? throw new InvalidOperationException("The Firemaking page has no part with its logs.");
        try { DevPanel(view, logs); }
        catch (Exception e) { DevLog($"game | the selected log's panel could not be read: {e.GetType().Name}: {e.Message}"); }
        // Every button of the page's list.
        var cards = Native.Cards(_page!).ToList();
        DevLog($"game | the page's list shows the category {Id(action.SelectedCategory)} ('{(Native.List(_page!) is { } select && DevNative.ListCategoryName(select) is { } name ? name.Text : "-")}'), " +
            $"{cards.Count} buttons ({cards.Count(c => c.Button.IsVisibleInTree())} shown)");
        foreach (var (log, button) in cards)
        {
            try
            {
                if (log is not ActiveFiremakingRecipe active) { DevLog($"game | a button bound to {log.GetType().Name}, not a log of Firemaking"); continue; }
                DevButton(active, button, logs.TryGetValue(active, out LogRates? listed) ? listed : LogRates.Read(active, _firemaking, _bank, Shown, false, DevBurningWide()));
            }
            catch (Exception e) { DevLog($"game | a button could not be read: {e.GetType().Name}: {e.Message}"); }
        }
        // The page's own order of the buttons it shows, top to bottom and left to right, against the window's.
        var order = cards.Where(c => c.Button.IsVisibleInTree()).OrderBy(c => Native.Drawn(c.Button).Position.Y).ThenBy(c => Native.Drawn(c.Button).Position.X)
            .Select(c => (c.Log as ActiveFiremakingRecipe)?.Recipe.Id ?? "?").ToList();
        var oursOrder = logs.Values.Where(t => t.CategoryOnPage && Native.Card(_page!, t.Active) is { } c && c.IsVisibleInTree()).Select(t => t.Active.Recipe.Id).ToList();
        DevLog($"game | the page's order of the buttons it shows [{string.Join(", ", order)}]; the window's order of the same [{string.Join(", ", oursOrder)}]" +
            (order.SequenceEqual(oursOrder) ? "" : "  DIFFER"));
        // Every category button against the categories the window lists a log of.
        var listedCategories = new HashSet<object>(logs.Values.Select(t => (object)t.Active.Category!), ReferenceEqualityComparer.Instance);
        DevLog("game | the page's category buttons: " + string.Join(" | ", (DevNative.CategoryButtons(view) ?? new Dictionary<ActiveFiremakingCategory, AnimatedButton>()).Select(p =>
        {
            bool shown = GodotObject.IsInstanceValid(p.Value) && p.Value.IsVisibleInTree(), ours = listedCategories.Contains(p.Key.Data);
            return $"{p.Key.Data.Id} '{p.Value.Text}' shown={shown}, disabled={p.Value.Disabled}, pressed={p.Value.ButtonPressed}, " +
                $"its logs unlocked {p.Key.UnlockedRecipeCount} of {p.Key.TotalRecipeCount}, only shown with recipes available={p.Key.Data.OnlyShowWhenRecipesAvailable}, " +
                $"the window lists a log of it={ours}" + (shown == ours ? "" : "  DIFFER");
        })));
    }

    // The selected log's panel: its name, its time, its bonfire time (and the bonfire's own time per log), what it uses
    // and makes, its doubling and preservation icons and its list of bonuses, against ours; and the game's own list of
    // what the log makes against ours.
    void DevPanel(FiremakingActionView view, Dictionary<ActiveFiremakingRecipe, LogRates> logs)
    {
        var action = _firemaking!.Action;
        if (action.SelectedRecipe is not { } active)
        {
            DevLog($"game | the page shows no log: its panel '{DevPanelName()}', the burning label '{DevNative.BurningLabel(view)?.Text}'");
            return;
        }
        LogRates t = logs.TryGetValue(active, out LogRates? listed) ? listed : LogRates.Read(active, _firemaking, _bank, Shown, false, DevBurningWide());
        DoubleFormattedLabel? interval = DevNative.IntervalLabel(view), bonfire = DevNative.BonfireTimeLabel(view);
        FiremakingBonfireView? bonfireView = DevNative.BonfireView(_page!);
        DoubleFormattedLabel? perLog = bonfireView != null ? DevNative.BonfireTimePerLog(bonfireView) : null;
        // A label's number against ours, in seconds or in milliseconds: which it is.
        static string Unit(DoubleFormattedLabel? label, double seconds) => label == null ? "not found"
            : $"'{label.Text}' ({R(label.Value)}): " + (Math.Abs(label.Value - seconds) < 0.0005 ? "ours in seconds" : Math.Abs(label.Value - seconds * 1000) < 0.5 ? "ours in milliseconds" : $"ours {R(seconds)} s  DIFFER");
        // A label's number against the game's raw number, unit unknown: which reading of the raw number it shows. These
        // settle LogRates.BonfireRule's unit; the window shows nothing of the bonfire until then.
        static string Raw(DoubleFormattedLabel? label, double raw) => label == null ? "not found"
            : $"'{label.Text}' ({R(label.Value)}): " + (Math.Abs(label.Value - raw) < 1e-6 ? "the raw number as it is"
                : Math.Abs(label.Value - raw / 1000) < 1e-6 ? "the raw number over 1000 (THE RAW NUMBER IS IN MILLISECONDS IF THE LABEL IS IN SECONDS)"
                : Math.Abs(label.Value - raw * 1000) < 1e-3 ? "the raw number times 1000" : "NONE OF THE RAW NUMBER'S READINGS");
        DevLog($"game | the selected log {active.Recipe.Id}: its panel '{DevPanelName()}'{(DevWordsOf(DevPanelName()) == DevWordsOf(active.Recipe.DisplayName.Text) ? "" : "  ANOTHER NAME")}, " +
            $"its picture {DevTexture(DevNative.SelectedIcon(view)?.Texture)} (ours {DevTexture(t.Picture)}), the burning label '{DevNative.BurningLabel(view)?.Text}' | " +
            $"time {Unit(interval, t.Seconds)} | the game's bonfire time of the log (raw) {R(t.BonfireRaw)}: the page's bonfire time {Raw(bonfire, t.BonfireRaw)}, " +
            $"the bonfire's time per log {Raw(perLog, t.BonfireRaw)} | " +
            $"the bonfire: {(bonfireView == null ? "its part not found" : $"'{DevNative.BonfireStatus(bonfireView)?.Text}', time left '{DevNative.BonfireTimeLeft(bonfireView)?.Text}', " + $"most '{DevNative.BonfireMaxTime(bonfireView)?.Text}', time added '{DevNative.BonfireTimeAdded(bonfireView)?.Text}', '{DevWordsOf(DevNative.BonfireLogsInBank(bonfireView)?.Text ?? "")}'")}; " +
            $"its timer {R(_firemaking.Bonfire.DurationTimer.TimeLeft)} of {R(_firemaking.Bonfire.DurationTimer.MaxTime)}, the bonfire {_firemaking.Bonfire.CurrentBonfire?.Id} " +
            $"most {_firemaking.Bonfire.CurrentBonfire?.MaxDuration}, lit {_firemaking.Bonfire.IsActive}");
        DevLog($"game | the selected log's inputs [{DevIcons(DevNative.Inputs(view))}] and outputs [{DevIcons(DevNative.Outputs(view))}]; ours: uses " +
            $"[{string.Join(", ", t.Costs.Select(c => $"{c.Item.Id} x{c.Quantity}"))}], makes [{string.Join(", ", t.Products.Select(p => $"{p.Item.Id} x{p.Quantity} at {R(p.Chance)}"))}], coins {t.Coins?.Id} x{t.CoinsEach}");
        // The game's own list of what the log makes (public members, by their names reads).
        var items = action.GetProducedRecipeItems(active)?.Select(i => i.Id).Order().ToList() ?? new List<string>();
        var currencies = action.GetProducedRecipeCurrencies(active)?.Select(c => c.Id).Order().ToList() ?? new List<string>();
        var oursItems = t.Products.Select(p => p.Item.Id).Distinct().Order().ToList();
        var oursCurrencies = (t.Coins != null ? [t.Coins.Id] : new List<string>()).Order().ToList();
        DevLog($"game | what it makes by the game's own list: items [{string.Join(", ", items)}], currencies [{string.Join(", ", currencies)}]; ours items " +
            $"[{string.Join(", ", oursItems)}], currencies [{string.Join(", ", oursCurrencies)}]" +
            (items.SequenceEqual(oursItems) ? "" : "  ITEMS DIFFER") + (currencies.SequenceEqual(oursCurrencies) ? "" : "  CURRENCIES DIFFER"));
        foreach (var (what, icon, nodes, value) in new (string, ModifierStatsInfoIcon?, ModifierStatNodes?, double)[]
                 { ("doubling", DevNative.DoublingIcon(view), active.Stats.DoublingChance, t.DoublingChance), ("preservation", DevNative.PreservationIcon(view), active.Stats.PreservationChance, t.PreservationChance) })
        {
            if (icon == null || !GodotObject.IsInstanceValid(icon)) { DevLog($"game | the {what} icon: not found"); continue; }
            ValueStatNode<double>? bound = null;
            try { bound = DevNative.IconStat(icon); } catch (Exception) { }
            DevLog($"game | the {what} icon: shown={icon.IsVisibleInTree()}, bound to {(bound == null ? "nothing read" : ReferenceEquals(bound, nodes?.Total) ? $"the log's {what} chance ({R(bound.Value)})" : $"another value ({R(bound.Value)})")}, " +
                $"ours {R(value)}; its tooltip '{DevTooltipText(icon.Tooltip)}'");
        }
        DevStatRows("the selected log's panel", DevNative.StatsView(view), active);
    }

    // What an inputs or outputs part of the panel shows: every item icon with its quantity and chance, and its words.
    static string DevIcons(Control? box)
    {
        if (box == null || !GodotObject.IsInstanceValid(box)) return "not found";
        var icons = box.FindChildren("*", "", true, false).OfType<ItemInfoIcon>().Where(i => i.IsVisibleInTree())
            .Select(i => $"{i.Item?.Id ?? "?"} x{R(DevNative.IconQuantity(i))}{(DevNative.IconIsChance(i) ? $" at {R(DevNative.IconChance(i))}" : "")}");
        var words = box.FindChildren("*", "", true, false).OfType<Label>().Where(l => l.IsVisibleInTree() && l.Text.Length > 0).Select(l => $"'{l.Text}'");
        return $"{string.Join(", ", icons)}; words {string.Join(" ", words)}";
    }

    // One button of the page's list: its name, its picture, its locked part, its XP tag (its number, the time it is bound
    // to, its tooltip's words with every number in it named), against ours.
    void DevButton(ActiveFiremakingRecipe active, CraftingRecipeButton button, LogRates t)
    {
        SkillXPPanel? xp = DevNative.ButtonXp(button);
        Int32FormattedLabel? tag = xp != null ? DevNative.XpLabel(xp) : null;
        var bound = xp != null ? DevNative.XpInterval(xp) : null;
        string words = xp != null ? DevTooltipText(DevNative.XpTooltip(xp)) : "no XP tag";
        DevLog($"game | button {active.Recipe.Id} '{DevNative.ButtonName(button)?.Text}'{(ReferenceEquals(DevNative.ButtonLog(button), active) ? "" : "  BOUND TO ANOTHER LOG")} | " +
            $"shown={button.IsVisibleInTree()}, {DevCardSight(button, false)}, locked part shown={DevNative.ButtonLocked(button)?.IsVisibleInTree()} (ours locked={t.Locked}) | " +
            $"its picture {DevTexture(DevNative.ButtonPicture(button)?.Texture)}, ours {DevTexture(t.Picture)} | product quantity '{DevNative.ButtonProductQuantity(button)?.Text}' | " +
            $"XP: the tag '{tag?.Text}' ({tag?.Value}), ours {R(t.XpEach)} a fire: {(tag == null ? "no tag" : DevXpSame(tag.Value, t.XpEach))}; the tag is bound to " +
            $"{(bound == null ? "no time" : ReferenceEquals(bound, active.Stats.Interval) ? "the log's time (FiremakingActionStats.Interval)" : $"another time ({R(bound.Value)} ms)")}; " +
            $"its tooltip '{words}' | {DevXpIn(words, t)}");
    }

    // The game's tag is a whole number (Int32FormattedLabel), while its XP is not: the tag is ours cut down to a whole
    // number (Woodcutting Rates' check-4: 57.99999999999999 shows "+57").
    static string DevXpSame(int game, double ours)
    {
        int down = (int)Math.Floor(ours);
        if (game == down) return ours - down < 1e-9 ? "the same" : "the tag is ours rounded down";
        if (game == (int)Math.Floor(ours + 1e-9)) return "the same, to the last digit of the arithmetic";
        return game == (int)Math.Round(ours) ? "the tag is ours rounded" : "DIFFER";
    }

    // Which rule the game's own XP tooltip follows: every number in it against the XP per second and per hour with the
    // XP on every fire (ours), as Woodcutting Rates' check read its tags' words.
    static string DevXpIn(string tooltip, LogRates ours)
    {
        var numbers = Regex.Matches(tooltip, @"\d[\d,]*(?:\.\d+)?").Select(m => double.Parse(m.Value.Replace(",", ""), CultureInfo.InvariantCulture)).ToList();
        if (!ours.HasTime) return $"numbers in it [{string.Join(", ", numbers.Select(R))}]";
        double perSecond = ours.XpEach / ours.Seconds;
        bool Near(double value, double room) => numbers.Any(n => Math.Abs(n - value) <= room);
        return $"numbers in it [{string.Join(", ", numbers.Select(R))}]; XP per second on every fire {R(perSecond)}: " +
            (Near(perSecond, Math.Max(0.006, 0.002 * perSecond)) ? "IN IT" : "not in it") + $"; XP an hour on every fire (ours) {R(ours.XpPerHour)}: " +
            (ours.XpPerHour is { } hour && Near(hour, Math.Max(1, 0.002 * hour)) ? "IN IT, the game's XP of an hour is ours" : "not in it") +
            $"; ours a fire {R(ours.XpEach)} is {(Near(ours.XpEach, 0.006) ? "in it" : "not in it")}";
    }

    // The rows of a list of a log's bonuses: each stat's name and words as the page writes them, its value as written and
    // as the game keeps it, against the log's own stat; and the row's own tooltip.
    void DevStatRows(string where, FiremakingActionStatsView? list, ActiveFiremakingRecipe log)
    {
        if (list == null || !GodotObject.IsInstanceValid(list)) { DevLog($"stats | {where}: no list"); return; }
        var rows = list.FindChildren("*", "", true, false).OfType<ModifierStatLabel>().ToList();
        if (rows.Count == 0) { DevLog($"stats | {where}: the list has no rows"); return; }
        DevLog($"stats | {where}: " + string.Join(" | ", rows.Select(r => DevRow(r, log))));
    }

    static string DevRow(ModifierStatLabel r, ActiveFiremakingRecipe log)
    {
        FiremakingActionStats s = log.Stats;
        var mine = new Dictionary<ModifierStatNodes, string>(ReferenceEqualityComparer.Instance);
        foreach (var (name, nodes) in new[] { ("doubling", s.DoublingChance), ("preservation", s.PreservationChance), ("interval %", s.PercentInterval), ("interval flat", s.FlatInterval),
                     ("ingredient cost %", s.PercentIngredientCost), ("currency reward flat", s.FlatCurrencyReward), ("currency reward %", s.PercentCurrencyReward),
                     ("XP %", s.Experience.PercentXPGained), ("XP flat", s.Experience.FlatXPGained) })
            if (nodes != null) mine.TryAdd(nodes, name);
        ModifierStatNodes? stat = null;
        DoubleLabel? value = null;
        string own = "";
        try { stat = DevNative.RowStat(r); } catch (Exception) { }
        try { value = DevNative.RowValue(r); } catch (Exception) { }
        try { own = DevTooltipText(DevNative.RowTooltip(r)); } catch (Exception e) { own = $"not read ({e.GetType().Name})"; }
        double shown = value?.Value ?? double.NaN;
        string unit = stat == null ? "" : Math.Abs(shown - stat.Total.Value) < 1e-6 ? ", the row shows the stat as it is"
            : Math.Abs(shown - stat.Total.Value * 100) < 1e-4 ? ", the row shows the stat times 100 (percent)" : ", the row's number DIFFERS from the stat";
        string of = stat == null ? "no stat" : mine.TryGetValue(stat, out string? n) ? $"the log's {n} ({R(stat.Total.Value)}){unit}" : "ANOTHER LOG'S OR NONE OF OURS";
        return $"'{DevWordsOf(r.StatName ?? "")}' value '{value?.Text}' ({R(shown)}), format {r.ValueFormat}, shown={r.IsVisibleInTree()}, {of}, " +
            $"its words '{DevWordsOf(r.StatTooltipContent ?? "")}', its own tooltip '{own}'";
    }

    // The page's own words for the rules the hour rests on and for what the window writes: its words for doubling,
    // preservation, the interval, skill XP, ingredient cost and product quantity, its icons', its words for burning and
    // the bonfire, the names of the game's Firemaking statistics (logs burnt, fires lit, the bonfire's), the XP tag's;
    // and the window's own words held against them.
    void DevWords()
    {
        string Read(string what, string members, Func<string> words)
        {
            try { return $"{what} '{DevWordsOf(words())}'"; }
            catch (Exception e) { return $"{what} not read ({members}: {e.GetType().Name})"; }
        }
        static string Tr(string key) => key + (TranslationServer.Translate(key).ToString() is { } t && t != key ? $" = {t}" : "");
        static string Constant(Type type, string member) => Tr(DevNative.Constant(type, member));
        Type modifiers = typeof(ModifierStatView), page = typeof(FiremakingActionView), fire = typeof(FiremakingBonfireView);
        var read = new[]
        {
            Read("doubling", "ModifierStatView.DoublingChanceTitle and ...Tooltip", () => Constant(modifiers, "DoublingChanceTitle") + ": " + Constant(modifiers, "DoublingChanceTooltip")),
            Read("preservation", "ModifierStatView.PreservationChanceTitle and ...Tooltip", () => Constant(modifiers, "PreservationChanceTitle") + ": " + Constant(modifiers, "PreservationChanceTooltip")),
            Read("the interval", "ModifierStatView.IntervalTooltip", () => Constant(modifiers, "IntervalTooltip")),
            Read("skill XP", "ModifierStatView.SkillXpGainedTooltip", () => Constant(modifiers, "SkillXpGainedTooltip")),
            Read("ingredient cost", "ModifierStatView.PercentIngredientCostTitle and ...Tooltip",
                () => Constant(modifiers, "PercentIngredientCostTitle") + ": " + Constant(modifiers, "PercentIngredientCostTooltip")),
            Read("product quantity", "ModifierStatView.FlatProductQuantityTitle and ...Tooltip",
                () => Constant(modifiers, "FlatProductQuantityTitle") + ": " + Constant(modifiers, "FlatProductQuantityTooltip")),
            Read("the doubling icon", "DoublingIcon.s_statName and s_statDescription", () => DevNative.IconName((DoublingIcon?)null).Text + ": " + DevNative.IconWords((DoublingIcon?)null).Text),
            Read("the preservation icon", "PreservationIcon.s_statName and s_statDescription",
                () => DevNative.IconName((PreservationIcon?)null).Text + ": " + DevNative.IconWords((PreservationIcon?)null).Text),
            Read("burning", "FiremakingActionView.s_burningRecipe and s_notBurning", () => DevNative.BurningWords(null).Text + " / " + DevNative.NotBurningWords(null).Text),
            Read("the page's button", "FiremakingActionView.StartBurning and StopBurning", () => Constant(page, "StartBurning") + " / " + Constant(page, "StopBurning")),
            Read("the bonfire time", "FiremakingActionView.BonfireTime and BonfireTimeDescription", () => Constant(page, "BonfireTime") + ": " + Constant(page, "BonfireTimeDescription")),
            Read("the bonfire", "FiremakingBonfireView.Active, InActive and s_logsInBank", () => Constant(fire, "Active") + " / " + Constant(fire, "InActive") + " / " + DevNative.LogsInBankWords(null).Text),
            Read("logs burnt", "LogsBurntStatistic.s_displayName", () => DevNative.StatisticName((LogsBurntStatistic?)null).Text),
            Read("fires lit", "FiresLitStatistic.s_displayName", () => DevNative.StatisticName((FiresLitStatistic?)null).Text),
            Read("bonfire time from fires", "BonfireTimeFromFiresStatistic.s_displayName", () => DevNative.StatisticName((BonfireTimeFromFiresStatistic?)null).Text),
            Read("bonfire time from logs", "BonfireTimeFromLogsStatistic.s_displayName", () => DevNative.StatisticName((BonfireTimeFromLogsStatistic?)null).Text),
            Read("bonfire time", "BonfireTimeStatistic.s_displayName", () => DevNative.StatisticName((BonfireTimeStatistic?)null).Text),
            Read("the XP tag", "SkillXPPanel.s_skillXpGained and s_xpPerSecond", () => DevNative.XpGained(null).Text + " / " + DevNative.XpPerSecond(null).Text),
            Read("an item's chance", "ItemInfoIcon.s_tooltipTitleChance", () => DevNative.ItemChanceTitle(null).Text),
        };
        DevLog("words | " + string.Join(" | ", read));
        // The window's own words against the game's: "Logs burnt", "fires", "Burning", "Bonfire time".
        static string Flat(string text) => Regex.Replace(text, @"\s+", " ").Trim();
        string heard = Flat(string.Join(" ", read));
        string Ours(string word) => heard.Contains(word, StringComparison.OrdinalIgnoreCase) ? $"'{word}' is in the game's words read here" : $"'{word}' NOT IN THE GAME'S WORDS read here";
        DevLog($"words | the window's words: {Ours("Logs burnt")}; {Ours("fires")}; {Ours("Burning")}; {Ours("Bonfire time")}; {Ours("Doubling")}; {Ours("Preservation")}");
        DevLog($"bonus names | {string.Join(", ", new[] { LogRates.Doubling, LogRates.Preservation }.Select(k => _bonusNames.Of(k) is { } name ? $"{k}: the game's '{name}'" : $"{k}: NOT READ, the tooltip says '{BonusNames.Kind(k)}'"))}");
    }

    // Every log of the game that is not listed, and why; and every listed log the page's list does not show under its
    // own category.
    void DevHidden()
    {
        var action = _firemaking!.Action;
        var listed = new HashSet<ActiveFiremakingRecipe>(ReadLogs().Select(t => t.Active), ReferenceEqualityComparer.Instance);
        int hidden = 0;
        foreach (var (recipe, active) in action.ActiveRecipes)
        {
            CraftingRecipeButton? card = Native.Card(_page!, active);
            if (listed.Contains(active))
            {
                if (ReferenceEquals(action.SelectedCategory?.Data, active.Category) && card is not { } shown) DevLog($"hidden | {recipe.Id} is listed, and the page's list of its category has no button for it  NOT ON THE PAGE");
                continue;
            }
            hidden++;
            ActiveFiremakingCategory? category = active.Category != null && action.ActiveCategories.TryGetValue(active.Category, out ActiveFiremakingCategory? c) ? c : null;
            DevLog($"hidden | {recipe.Id} '{recipe.DisplayName.Text}' ({active.Category?.Id}) | the log is shown when: {DevConditions(recipe.DisplayRequirements)} | " +
                $"its category is shown when: {DevConditions(active.Category?.DisplayRequirements)}, only with recipes available {active.Category?.OnlyShowWhenRecipesAvailable}, " +
                $"its unlocked logs {category?.UnlockedRecipeCount} | to burn it: {DevConditions(recipe.Requirements)} | in the game's list {action.Config.Recipes.Contains(recipe)}, " +
                $"in its category's list {category?.Recipes.Contains(active)} | its button " + (card == null ? "none" : card.IsVisibleInTree() ? "SHOWN ON THE PAGE" : "hidden"));
        }
        DevLog($"hidden | {hidden} of the game's {action.ActiveRecipes.Count} logs are not listed");
    }

    static string DevConditions(ConditionGroup? group) => group == null || group.Conditions == null || group.Conditions.Count == 0 ? "always"
        : $"{(group.AreMet ? "met" : "NOT MET")}: " + string.Join("; ", group.Conditions.Select(c => $"{(c.IsMet ? "met" : "not met")}{(c.IsOr ? " (or)" : "")} '{DevWordsOf(c.GetDescription(false, false))}'"));

    // The log the page shows against the lit line, and the log burnt against the line that says so.
    void DevLit()
    {
        var action = _firemaking!.Action;
        var lit = _list!.DevLines().Where(l => l.Log.OnPage).Select(l => l.Log.Active).ToList();
        bool listed = action.SelectedRecipe is { } selected && _list.DevLines().Any(l => ReferenceEquals(l.Log.Active, selected));
        bool right = listed ? lit.Count == 1 && ReferenceEquals(lit[0], action.SelectedRecipe) : lit.Count == 0;
        var burning = _list.DevLines().Where(l => l.Log.Burning).Select(l => l.Log.Active.Recipe.Id).ToList();
        DevLog($"lit | the page's log {Id(action.SelectedRecipe)}{(action.SelectedRecipe != null && !listed ? " (not listed)" : "")}, its panel '{DevPanelName()}', " +
            $"the log burnt {Id(action.CreatingRecipe)}, firemaking now: {DevBurningNow()} | the lit line{(lit.Count == 1 ? " is" : "s are")} " +
            $"{(lit.Count == 0 ? "none" : string.Join(", ", lit.Select(t => t.Recipe.Id)))} (drawn lit: {string.Join(", ", _list.DevLines().Where(l => l.Drawn.EndsWith("lit=True")).Select(l => l.Log.Active.Recipe.LocalId))})" +
            (right ? "" : "  MISMATCH") + $" | the lines that say they are burnt [{string.Join(", ", burning)}]" +
            (burning.SequenceEqual(DevBurningWide() && action.CreatingRecipe != null && _list.DevLines().Any(l => ReferenceEquals(l.Log.Active, action.CreatingRecipe)) ? [action.CreatingRecipe.Recipe.Id] : new List<string>()) ? "" : "  MISMATCH"));
    }

    string DevOrder(string when)
    {
        string order = string.Join(", ", _list!.DevLines().Select(l => l.Log.Active.Recipe.LocalId));
        DevLog($"order | {when} | {order}");
        return order;
    }

    string DevInSight()
    {
        Rect2 sight = _list!.DevScroll.GetGlobalRect();
        return string.Join(", ", _list.DevLines().Where(l => l.Button.IsVisibleInTree() && l.Button.GetGlobalRect().Intersects(sight)).Select(l => l.Log.Active.Recipe.LocalId));
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
    // (mods/CombatSim/CutProbe.cs, DevWords): whole, CUT (a name cut by its label's ClipText is CUT too), SHORTENED WITH
    // ... where the game trims it with its tooltip holding it, and PAST THE EDGE where it ends past the edge of its line or
    // of the window (Woodcutting Rates' DevCut).
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

    // ---- the game's own words on the page's parts, as a player reads them by resting the pointer on them -----------

    // One part of the page the pointer rests on for the game's tooltip, and what is kept from tick to tick (Woodcutting
    // Rates' DevAim): where the pointer was put, the tooltip's words when it was first seen shown, and the screen before
    // the pointer came (for the proof of the picture).
    sealed class DevAim(string what, ActiveFiremakingRecipe log, Control aim, string aimed, Tooltip? tooltip, Func<string> words, string? shot)
    {
        public readonly string What = what, Aimed = aimed;
        public readonly ActiveFiremakingRecipe Log = log;
        public readonly Control Aim = aim;
        public readonly Tooltip? Tooltip = tooltip;
        public readonly Func<string> Words = words;
        public readonly string? Shot = shot;
        public Image? Before;
        public Vector2 At;
        public string? First;
    }

    // The parts of the page to rest the pointer on, for the log the page shows, each with the control the game's tooltip
    // shows for: its XP tag in the page's list (SkillXPPanel's XPTooltip), and in its panel its doubling and preservation
    // icons (ModifierStatsInfoIcon.Tooltip) and its bonfire time (FiremakingActionView.BonfireTimeTooltip). Only parts in
    // sight; none when the page shows no log. Never the Start button nor anything of the bonfire's part.
    List<DevAim> DevAims()
    {
        var aims = new List<DevAim>();
        var action = _firemaking!.Action;
        if (action.SelectedRecipe is not { } log || Native.ActionView(_page!) is not { } view) return aims;
        // Where to rest the pointer for a tooltip: one of its targets in sight, around or inside the given part first.
        Control? Aim(Tooltip? tooltip, Control part, out string aimed)
        {
            var targets = tooltip == null ? new List<Control>() : DevNative.Targets(tooltip).Where(c => c.IsVisibleInTree() && DevSight(c).HasArea()).ToList();
            Control? found = targets.FirstOrDefault(c => c == part || c.IsAncestorOf(part) || part.IsAncestorOf(c)) ?? targets.FirstOrDefault();
            if (found == null && part.IsVisibleInTree() && DevSight(part).HasArea()) found = part;
            aimed = found == null ? "nothing in sight" : targets.Contains(found) ? $"the tooltip's target {DevName(found)}" : $"{DevName(found)} (no target of its tooltip found in sight)";
            return found;
        }
        if (Native.Card(_page!, log) is { } card && DevNative.ButtonXp(card) is { } xp && DevNative.XpTooltip(xp) is { } xpTip && Aim(xpTip, xp, out string xpAimed) is { } xpAim)
            aims.Add(new DevAim("the XP tag of its button in the list", log, xpAim, xpAimed, xpTip, () => DevTipWords(xpTip), "xp-tooltip"));
        else DevLog("page words | the selected log's XP tag in the list is not in sight (or has no tooltip): not pointed at");
        foreach (var (what, icon) in new (string, ModifierStatsInfoIcon?)[] { ("its doubling icon", DevNative.DoublingIcon(view)), ("its preservation icon", DevNative.PreservationIcon(view)) })
        {
            if (icon == null || !GodotObject.IsInstanceValid(icon) || !icon.IsVisibleInTree()) { DevLog($"page words | {what}: not shown, not pointed at"); continue; }
            if (icon.Tooltip is not { } tip) { DevLog($"page words | {what}: it has no tooltip (ModifierStatsInfoIcon.Tooltip), not pointed at"); continue; }
            if (Aim(tip, icon, out string aimed) is { } aim) aims.Add(new DevAim(what, log, aim, aimed, tip, () => DevTipWords(tip), null));
        }
        if (DevNative.BonfireTimeTooltip(view) is { } bonfire && DevNative.BonfireTimeLabel(view) is { } label && Aim(bonfire, label, out string bonfireAimed) is { } bonfireAim)
            aims.Add(new DevAim("its bonfire time", log, bonfireAim, bonfireAimed, bonfire, () => DevTipWords(bonfire), null));
        else DevLog("page words | its bonfire time is not in sight (or has no tooltip): not pointed at");
        DevLog($"page words | {aims.Count} parts to point at for {log.Recipe.Id}: {string.Join("; ", aims.Select(a => $"{a.What} at {a.Aimed}"))}");
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

    // The game says the pointer is on the control: on it or on a part of it, or on a control around it that the pointer
    // goes through to at the spot it was put.
    bool DevPointerOn(Control aim, Vector2 at)
    {
        Control? over = _ctx.Tree.Root.GuiGetHoveredControl();
        if (over == null || !GodotObject.IsInstanceValid(aim)) return false;
        return over == aim || aim.IsAncestorOf(over) || (over.IsAncestorOf(aim) && aim.GetGlobalRect().HasPoint(at));
    }

    // The game's tooltip says it is shown, and not for another control than the one aimed at or the one under the spot
    // the pointer was put (Tooltip.IsShown and CurrentTarget, public members by ApiDump).
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

    // Rests the pointer on one part of the page until the game shows its tooltip for it, filled, then reads it two drawn
    // frames later, only while the pointer is on it, and pictures it when the part asks for a picture. A tooltip not
    // shown in 20 s is one line, and the request goes on. True when this part is done.
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
        catch (Exception e) { content = ""; DevLog($"page words | {a.What}: its words could not be read ({e.GetType().Name}: {e.Message})"); }
        bool on = DevPointerOn(a.Aim, a.At);
        string state = "";
        bool shown = a.Tooltip != null && DevShownFor(a.Tooltip, a.Aim, a.At, out state);
        if (!(on && shown && content.Length > 0))
        {
            a.First = null;
            if (Time.GetTicksMsec() - _devTipSince >= TipWait)
            {
                DevLog($"page words | {a.What} of {a.Log.Recipe.Id}: its tooltip was not shown filled in {TipWait / 1000} s with the pointer on " +
                    $"{DevName(_ctx.Tree.Root.GuiGetHoveredControl())}, aimed at {a.Aimed} (the tooltip: {state}; it holds '{content}'): not read this time");
                DevPointerBack();
                return true;
            }
            if (!on)
            {
                Rect2 rect = DevSight(a.Aim);
                if (!rect.HasArea())
                {
                    DevLog($"page words | {a.What} of {a.Log.Recipe.Id} went out of sight: not read this time");
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
        DevLog($"page words | {a.What} of {a.Log.Recipe.Id}, the pointer on {a.Aimed}, the tooltip {state}: '{content}'" +
            (content == a.First ? "" : $"  ITS WORDS CHANGED while read (first seen: '{a.First}')") + $" | {DevNumbersIn(content, a.Log)}");
        if (a.Shot != null)
        {
            Shot(a.Shot);
            if (a.Before != null && a.Tooltip != null)
            {
                using var shot = _ctx.Tree.Root.GetTexture().GetImage();
                DevLog($"shot-firemaking-{a.Shot}.png | " + (DevDrawn(a.Tooltip, out string where) is { } drawn
                    ? DevChangedPixels(a.Before, shot, drawn, $"the game's tooltip: {where}", "VISIBLE", "NOT VISIBLE")
                    : $"NOT PROVED: where the game's tooltip is drawn is not known ({where})"));
            }
        }
        DevPointerBack();
        return true;
    }

    // Every number in a tooltip's words, named against the log's numbers, so that words stating a rule of the hour can be
    // read for what their numbers are (Mining Rates' break rule read "Base chance" and "Current chance" this way).
    string DevNumbersIn(string words, ActiveFiremakingRecipe log)
    {
        LogRates t = LogRates.Read(log, _firemaking!, _bank, Shown, false, DevBurningWide());
        var known = new List<(string Name, double Value)>
        {
            ("the log's time in seconds", t.Seconds), ("the log's time in milliseconds", t.Seconds * 1000), ("the fires of an hour", t.FiresPerHour),
            ("the XP of a fire", t.XpEach), ("the XP of an hour (ours)", t.XpPerHour ?? double.NaN), ("the XP of a second", t.HasTime ? t.XpEach / t.Seconds : double.NaN),
            ("the doubling chance", t.DoublingChance), ("the preservation chance", t.PreservationChance), ("the logs burnt an hour (ours)", t.LogsPerHour ?? double.NaN),
            ("the game's bonfire time of the log (raw)", t.BonfireRaw), ("the raw bonfire time over 1000", t.BonfireRaw / 1000),
            ("the raw bonfire time times the fires of an hour", t.BonfireRaw * t.FiresPerHour), ("... over 1000", t.BonfireRaw * t.FiresPerHour / 1000),
            ("... over 60,000 (minutes of milliseconds)", t.BonfireRaw * t.FiresPerHour / 60000),
            ("the coins of a fire", t.CoinsEach), ("the coins of an hour", t.CoinsPerHour ?? double.NaN), ("the bonfire's most time (raw)", _firemaking!.Bonfire.DurationTimer.MaxTime),
            ("the bonfire's most time over 1000", _firemaking.Bonfire.DurationTimer.MaxTime / 1000), ("the bonfire's time left (raw)", _firemaking.Bonfire.DurationTimer.TimeLeft),
        };
        var said = new List<string>();
        foreach (Match m in Regex.Matches(words, @"-?\d[\d,]*(?:\.\d+)?\s*%?"))
        {
            string raw = m.Value.Trim();
            bool percent = raw.EndsWith('%');
            if (!double.TryParse(raw.TrimEnd('%').Trim().Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) continue;
            var names = known.Where(k => double.IsFinite(k.Value) && (Math.Abs(k.Value - n) <= 1e-6 + 0.0005 * Math.Abs(n) || (percent && Math.Abs(k.Value * 100 - n) <= 0.006)))
                .Select(k => k.Name).Distinct().ToList();
            said.Add($"{raw}: {(names.Count == 0 ? "none of the log's numbers" : string.Join(" = ", names))}");
        }
        return said.Count == 0 ? "no number in it" : "its numbers [" + string.Join("; ", said) + "]";
    }

    // Where the words of a game's tooltip are drawn, in the picture's own pixels: its labels that are shown, each through
    // its canvas and its viewport (Viewport.GetScreenTransform). Null when none is shown.
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

    // ---- through the game's input, as a player's hand would (Woodcutting Rates' and Fishing Rates' Dev.cs) ------------

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

    // Somebody was at the mouse within StillFor: what DevHandAtMouse would wait for, read without waiting or saying it.
    bool DevHandNow() => _devHandAt != 0 && Time.GetTicksMsec() - _devHandAt < StillFor;

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
    // the page's list of logs when it is in there, and, for a control of the game, beside the mod's own window, which lies
    // over the page: the pointer put on the window's part would never be on the control.
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

    // The pointer rests on a control and the game's tooltip comes up; its picture is shot-firemaking-<shot>.png. A hand at
    // the mouse moves it away: then it waits and points again. A tooltip that has not come up in 20 s of pointing is one
    // line, and the request goes on.
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
                DevLog($"shot-firemaking-{shot}.png | " + DevChanged(before, now, at, "the tooltip's place", "VISIBLE", "NOT VISIBLE"));
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
    // mod's own window (DevSight): the window lies over the Firemaking page, and a button it covers is not in the picture.
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
            Save(image, $"shot-firemaking-{state}.png");
            using var region = image.GetRegion(part);
            Save(region, $"crop-firemaking-{state}.png", proof);
            return;
        }
        Save(image, $"shot-firemaking-{state}.png");
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
        Error result = region.SavePng(Path.Combine(_ctx.Directory, "crop-firemaking-strip.png"));
        if (result != Error.Ok) throw new IOException("crop-firemaking-strip.png: " + result);
        DevLog($"crop-firemaking-strip.png | the button at {_opener.GetGlobalRect()}, lit={_opener.ButtonPressed}, {strip.GetChildCount()} buttons in the strip{HandNote}");
    }
}
#endif
