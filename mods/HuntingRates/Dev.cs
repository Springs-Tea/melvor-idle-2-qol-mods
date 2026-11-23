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
using GamesByMalcs.Melvor2.CustomControls;
using GamesByMalcs.Melvor2.Entities.Hunting;
using GamesByMalcs.Melvor2.GameMenus;
using GamesByMalcs.Melvor2.GameMenus.Pages;
using GamesByMalcs.Melvor2.InfoLabels;
using GamesByMalcs.Melvor2.Settings;
using GamesByMalcs.Melvor2.StatDisplays;
using GamesByMalcs.Melvor2.Tooltips;
using GamesByMalcs.Melvor2.Utils;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace HuntingRates;

// Development: a file "hunting.request" in the mod's folder runs the light check below with nobody at the keyboard, one
// step about every half second, and writes "hunting.done" when it ends: "ok" or "failed at <step>: <why>", and in both
// cases what was put back. Every step logs what it read as one-line "Dev: ..." entries in MelvorModLoader.log. It is
// short (about a minute when nobody moves the mouse): no simulation, a few pictures. A second request,
// "huntingwatch.request", only listens while the character hunts (DevWatch.cs); one waits for the other.
//   pwsh .claude\codex\check-mod.ps1 -Worktree <worktree> -Mod HuntingRates -Request hunting.request -Produces hunting.done -Out <folder>
// The request file may say "fail=<step>": that step then fails on purpose, to show that a failed request puts
// everything back (-Text 'fail=click'). Built on Woodcutting Rates' request (mods/WoodcuttingRates/Dev.cs) as merged,
// after its six turns and three code reviews, for the Hunting page; its put-back rule is the same, its parts are the
// Hunting page's: the location selected, the animal selected, the journal or the hunt shown, the scroll of the list.
// It only looks, points and clicks the mod's own controls and, in step 9, the Hunting page's own location button and
// animal card button, which select and start nothing: no hunt is started, stopped or moved on, no trap, bait or trapless
// choice is touched (they are read before and after every press: CHOICES CHANGED), and the buttons that start or stop
// a hunt are not named in this file. What is put back is the page as it was just before the request's own first change
// to it, so a selection the player made after the request began stays his. A click is made in two steps (DevPress): the
// pointer is put on the control, and a later step presses only when the game says the pointer is on that control; a
// control of the game under the mod's own window is pointed at beside it, or not at all (said at once). Nothing is
// pointed at or pressed while somebody is at the mouse: a step waits until the real pointer has been still for 5 s, and
// after 90 s the request ends, which puts everything back. A picture of something just shown is taken two drawn frames
// later, and its pixels prove what it shows (DevChanged). The pictures go into the mod's folder (shot-hunting-*.png,
// crop-hunting-*.png).
//  1. Remembers the page on the screen, whether the window was open and folded, the round button, settings.json as it
//     is on disk, the page's location and animal selected, whether it shows the journal, the scroll of its list of
//     animals, the hunt in progress (IsStarted, CurrentAnimal, its State, CurrentStep, RequiredSteps, Tracks,
//     TrapFailCount, the trap in use), and every animal's trap, bait and trapless choice.
//  2. Opens the Hunting page the way the sidebar does, if another page is open, and reads back that it is shown; stops
//     with one line when it cannot be opened that way (then open it by hand and send the request again).
//  3. "Dev: strip": every round button in the strip. Closes the window if it was open. A picture of the screen without
//     the window, for the proofs. Presses the round button, and opens the window up with its own button if it opened
//     folded; the round button must be lit.
//  4. "Dev: row": every line's numbers as the game gives them (raw) and as counted, the time of a catch by every reading
//     of the chain (AnimalRates.Readings), as drawn (with colours), and its tooltip. "Dev: game": for the animal the
//     page's panel shows, the game's own labels (the three intervals, the trapping chance, the XP and its range, the
//     tool, the outputs, its list of bonuses) beside ours, DIFFER on a difference, and every card of the page's list (its
//     name, picture, tool, trap, XP) beside the line, and the page's order of locations and animals against ours.
//     "Dev: words": the page's own words for the rules the hour rests on (the constants of its views read by reflection,
//     the rows and tooltips of its list of bonuses as filled in for the animal selected). "Dev: spiders": the game's
//     spider setting and which picture each spider's line shows. "Dev: hidden": every animal not listed, and why, and the
//     page's location buttons against ours. "Dev: discovery": every animal not discovered yet, as the page draws its card
//     and as the line shows it. "Dev: lit". "Dev: tips". "Dev: room": the window's place, the panel, and every card
//     against them. "Dev: cut" over every label of the picture. crop-hunting-window.png and, when the list scrolls,
//     crop-hunting-window-end.png.
//  5. One tooltip (shot-hunting-tooltip.png, "Dev: tooltip"), and the tooltip of the name with the longest tip
//     (shot-hunting-header-tooltip.png). A tooltip that does not come up in 20 s is one line, and the request goes on.
//  6. One sort: a click on the XP column's name; "Dev: order" and crop-hunting-sorted.png.
//  7. "Dev: click": one click on a line of an animal at another location than the page's, when there is one. Reads back
//     the page's location and animal selected, what the page shows in its own words (the panel's name), the lit line,
//     the window, and the hunt in progress before and after, with the game's events raised during the click itself
//     (DevClickEvents): HUNT STARTED, HUNT STOPPED, HUNT CHANGED, CHOICES CHANGED, NOT OPENED, THE PAGE SHOWS ANOTHER
//     ANIMAL and THE WINDOW CLOSED mark what must not happen. Picture shot-hunting-clicked.png. Puts the selection back,
//     then presses the game's own location button and animal button for the same animal with the pointer, as a player
//     would, and compares (DIFFER, EVENTS DIFFER); then puts the selection back again. When the click changed the hunt,
//     the game's buttons are not pressed. Each put-back follows the rule of step 9.
//  8. "Dev: size": the window at its smallest and at its largest size (crop-hunting-smallest.png, crop-hunting-largest.png),
//     LARGER or OVER THE BOTTOM BAR when it does not fit, and "Dev: cut" at both. Closes the window with its X:
//     shot-hunting-closed.png.
//  9. Puts back settings.json byte for byte from the copy read in step 1 (sort, size, place, fold), the window open or
//     closed and folded as it was, and the page's parts: the journal or the hunt shown (by the page's own buttons), the
//     location and the animal selected (by the members the click uses), the scroll of its list two drawn frames later.
//     One rule for each part, each on its own (Woodcutting Rates' review 3): what the request left on a part is read from
//     the page once it has settled after the request's own press (DevSettle), and at the put-back the part is read
//     again: still exactly that, it is put back; otherwise the player changed it, and it is his: left, said in one line,
//     never taken as the request's own. A location of his takes the animal and the scroll with it (they belong to the
//     location shown). No wait on the mouse decides whose a value is. The keyboard focus is taken off a control right
//     after the request's own press of it (DevFocusOff); nothing is given back. Every part is put back in its own try,
//     with one line: a part that fails is NOT AS IT WAS, and the next part runs all the same.
// 10. Reads it back from the screen, the game and the disk, the hunt in progress and every animal's choices too, then the
//     page that was on the screen (only if the request opened Hunting itself) and the pointer where the real one is, and
//     says in its last line what was put back, with NOT AS IT WAS for anything that is not, NOT READ BACK for what could
//     not be read. What the player changed himself is said as his, and is no fault.
// A step that fails jumps to 9: a failed request puts everything back too. If the mod stops while a request runs, or
// another character is opened, the done file says so and settings.json is written back from the copy (DevStopped,
// DevReset).
public partial class HuntingRatesMod
{
    const string RequestFile = "hunting.request", DoneFile = "hunting.done";
    // Woodcutting Rates' times (CombatGoals/DevWindowShots.cs before it): how long the real pointer must be still before a
    // step points or presses, and how long a step waits for that before the request ends.
    const ulong StillFor = 5000, GiveUpAfter = 90000, StepTime = 6000, PointerStepTime = GiveUpAfter + 10000;
    const ulong TipWait = 20000;
    // Fishing Rates' longest tip of a column's name, line by line, and the longest line of its tooltips, in characters.
    const int FishingTipMost = 82, FishingTooltipMost = 94;

    readonly List<(string Name, Func<bool> Run, ulong Timeout)> _devSteps = new();
    int _devAt, _devPutBackAt;
    ulong _devStarted;
    long _devFrameMark;
    string? _devFailure, _devFailOnPurpose, _devPutBackWords;
    bool _devPutBackOk, _devPutBackRead, _devOpenedPage;
    // Put-backs on the way whose line says NOT AS IT WAS, and presses not made: the done line carries them.
    readonly List<string> _devOnTheWay = new();
    Image? _devWithout;
    string? _devSettingsCopy;
    bool _devSettingsRead;
    // True while a click on a line runs (ShowOnPage): the game's events raised then are the click's own.
    bool _devInClick;

    // The parts of the Hunting page the request can change, each owned on its own (Woodcutting Rates' rule): what the
    // request left is read from the page once it has settled after the request's own press (_devSettle), and kept (null:
    // the request owns nothing of that part). The scroll belongs to the list of one location (_devScrollUnder).
    sealed record DevOwn<T>(T Value);
    DevOwn<ActiveHuntingLocation?>? _devOwnLocation;
    DevOwn<ActiveHuntingAnimal?>? _devOwnAnimal;
    DevOwn<bool>? _devOwnJournal;
    DevOwn<int?>? _devOwnScroll;
    ActiveHuntingLocation? _devScrollUnder;
    (long Frame, bool Both)? _devSettle;
    long _devTick, _devSettledTick = -1;
    DevPageWas? _devOrigin, _devOriginWait;
    readonly HashSet<string> _devHis = new();
    (Control Target, long Frame)? _devFocusAfter;
    HuntingActionComponent? _devAction;

    // What the request found, to be put back as it was.
    sealed record DevBefore(GamePage? Page, bool Wanted, bool Shown, bool? Folded, bool? Lit, string? Settings, DevPageWas Hunting);

    // The Hunting page's selection, view and scroll at one moment (_devOrigin).
    sealed record DevPageWas(ActiveHuntingLocation? Location, ActiveHuntingAnimal? Animal, bool Journal, int? Scroll);

    DevPageWas DevPageNow() => new(_hunting!.Action.SelectedLocation, _hunting.Action.SelectedAnimal, DevJournalNow(), DevScrollNow());

    void DevReset()
    {
        if (_devSteps.Count > 0)
        {
            DevUnwatchAll();
            string settings = DevSettingsBack();
            DevEnd($"stopped: another character was opened; put back: {settings}; not put back: {DevLeftOn()}, the window of the character before (freed " +
                "with it), its page and the pointer  NOT ALL AS IT WAS");
        }
        if (_devHunts != null) DevWatchEnd("stopped: another character was opened", summarise: false);
        _devSteps.Clear();
        _devAt = 0;
        _devAction = null;
        _devSettle = null;
        _devFocusAfter = null;
    }

    void DevStopped()
    {
        DevUnwatchAll();
        if (_devHunts != null) DevWatchEnd("stopped: the mod stopped", summarise: false);
        if (_devSteps.Count == 0) return;
        string settings = DevSettingsBack();
        string step = _devAt < _devSteps.Count ? _devSteps[_devAt].Name : "the end";
        DevEnd($"failed at {step}: the mod stopped; put back: {settings}; not put back: {DevLeftOn()}, the window, the page and the pointer  NOT ALL AS IT WAS");
    }

    // settings.json written back byte for byte from the copy the request read at its start, and read again by the window.
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
    // in the character's Hunting (HuntingActionComponent.SelectedLocation, SelectedAnimal; the component is written to the
    // save, WriteTo), so it may stay there.
    string DevLeftOn()
    {
        try
        {
            if (_devAction == null) return "the page's selection (no character read)";
            string was = _devOrigin is { } origin ? $"{origin.Location?.Location?.Id ?? "none"} / {origin.Animal?.Animal.Id ?? "none"}" : "unchanged by the request";
            return $"the selection of the character the request ran for, now {_devAction.SelectedLocation?.Location?.Id ?? "none"} / " +
                $"{_devAction.SelectedAnimal?.Animal.Id ?? "none"} (before the request's first change {was}), which the game may keep in that character's save";
        }
        catch (Exception e) { return $"the page's selection (not read: {e.GetType().Name})"; }
    }

    void DevRequest()
    {
        if (GameAccess.IsComputingOffline || _stopped) return;
        if (_devHunts != null) { DevWatchTick(); return; }
        DevWatchHand();
        _devTick++;
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
                DevLog($"Hunting Rates check started, {_devPutBackAt} steps and the put-back; a step waits {StepTime / 1000} s at most, " +
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
                _devFailure = $"failed at {step}: {e.Message}";
                _devAt = _devPutBackAt;
                _devStarted = Time.GetTicksMsec();
                _devHandSinceStep = false;
                _devFrameMark = 0;
                _devAim = null;
                _devMisses = 0;
                return;
            }
            // A step of the put-back failed: what it puts back is NOT AS IT WAS, said once, and the put-back goes on.
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
        string words = _devPutBackWords ?? "the read back did not run to its end; on the way: " +
            (_devOnTheWay.Count == 0 ? "nothing said" : string.Join(" | ", _devOnTheWay));
        DevEnd($"{_devFailure ?? "ok"}; put back: {words}{(_devPutBackOk ? "" : "  NOT ALL AS IT WAS")}{(_devPutBackRead ? "" : "  NOT ALL READ BACK")}");
    }

    void DevEnd(string result)
    {
        DevLog("Hunting Rates check " + result);
        _devSteps.Clear();
        _devAt = 0;
        _devWithout?.Dispose();
        _devWithout = null;
        _devSettingsRead = false;
        _devSettingsCopy = null;
        File.WriteAllText(Path.Combine(_ctx.Directory, DoneFile), result);
    }

    // Two frames drawn since the step began to wait: the picture is the frame the game drew last (lesson 43).
    bool DevFramesDrawn()
    {
        long now = (long)Engine.GetFramesDrawn();
        if (_devFrameMark == 0) { _devFrameMark = now; return false; }
        return now >= _devFrameMark + 2;
    }

    string DevState() =>
        $"page shown={PageShown}, button shown={_opener?.IsVisibleInTree()}, lit={_opener?.ButtonPressed}, window open={IsOpen}, " +
        $"folded={_window?.Minimised}, sort={_settings.Sort} {(_settings.Descending ? "descending" : "ascending")}";

    void DevLog(string text) => _ctx.Log("Dev: " + One(text));

    static string One(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", " / ");

    static string DevJoin(params string[] parts) => string.Join(", ", parts.Where(p => p.Length > 0));

    // ---- the page's parts the request owns (Woodcutting Rates' rule, for the Hunting page) ------------------------------

    bool? DevJournalNowOrNull()
    {
        try { return JournalShown(); }
        catch (Exception) { return null; }
    }

    bool DevJournalNow() => DevJournalNowOrNull() ?? false;

    // Where the page's list of animals is scrolled to, or null when the list is in no scrolling container.
    int? DevScrollNow()
    {
        try { return DevListScroll() is { } scroll ? scroll.ScrollVertical : null; }
        catch (Exception) { return null; }
    }

    ScrollContainer? DevListScroll() =>
        _page != null && GodotObject.IsInstanceValid(_page) && Native.TrackingView(_page) is { } view && Native.AnimalList(view) is { } list ? Native.ScrollOf(list) : null;

    bool DevOwnsAny => _devOwnLocation != null || _devOwnAnimal != null || _devOwnJournal != null || _devOwnScroll != null;

    // A press the request makes on the Hunting page (a line, a location button, an animal's card), on every tick until it
    // is made. It waits for the request's own last press to settle. While nothing of the request's is left on the page,
    // the page as it is on that tick is taken as what the put-backs go back to. While the request owns a part that is not
    // exactly what it left, the player changed it: the request lets go of it and the press is not made (null).
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
            DevPageWas was = _devOrigin ?? before.Hunting;
            _devOrigin = _devOriginWait ?? DevPageNow();
            _devHis.Clear();
            if (_devOrigin != was)
                DevLog($"click | the page just before {what}: {DevPageWords(_devOrigin)}; not as the request found it ({DevPageWords(was)}): the player changed it " +
                    "meanwhile, and that is what is put back");
        }
        _devSettle = ((long)Engine.GetFramesDrawn(), true);
        return true;
    }

    static string DevPageWords(DevPageWas p) =>
        $"the location {p.Location?.Location?.Id ?? "none"}, the animal {p.Animal?.Animal.Id ?? "none"}, the {(p.Journal ? "journal" : "hunt")} shown, " +
        $"the list scrolled to {p.Scroll?.ToString() ?? "-"}";

    // Each part the request owns, read again: one that is not exactly what the request left is the player's, and is let
    // go of. A location of his takes the animal and the scroll with it: they belong to the location shown.
    List<string> DevHisNow()
    {
        var moved = new List<string>();
        var action = _hunting!.Action;
        if (_devOwnLocation is { } location && !ReferenceEquals(action.SelectedLocation, location.Value))
        {
            moved.Add($"the location (now {action.SelectedLocation?.Location?.Id ?? "none"}, the request left {location.Value?.Location?.Id ?? "none"})");
            _devOwnLocation = null;
            _devHis.Add("location");
            if (_devOwnAnimal != null) { moved.Add("the animal with it"); _devOwnAnimal = null; _devHis.Add("animal"); }
            if (_devOwnScroll != null) { moved.Add("the scroll with it"); _devOwnScroll = null; _devHis.Add("scroll"); }
        }
        if (_devOwnAnimal is { } animal && !ReferenceEquals(action.SelectedAnimal, animal.Value))
        {
            moved.Add($"the animal (now {action.SelectedAnimal?.Animal.Id ?? "none"}, the request left {animal.Value?.Animal.Id ?? "none"})");
            _devOwnAnimal = null;
            _devHis.Add("animal");
        }
        if (_devOwnJournal is { } journal && DevJournalNowOrNull() is { } now && now != journal.Value)
        {
            moved.Add($"the view (now the {(now ? "journal" : "hunt")}, the request left the {(journal.Value ? "journal" : "hunt")})");
            _devOwnJournal = null;
            _devHis.Add("view");
        }
        if (_devOwnScroll != null && !ReferenceEquals(action.SelectedLocation, _devScrollUnder))
        {
            moved.Add("the scroll (the list is of another location now)");
            _devOwnScroll = null;
            _devHis.Add("scroll");
        }
        if (_devOwnScroll is { } scroll && DevScrollNow() is var at && at != scroll.Value)
        {
            moved.Add($"the scroll (now {at?.ToString() ?? "-"}, the request left {scroll.Value?.ToString() ?? "-"})");
            _devOwnScroll = null;
            _devHis.Add("scroll");
        }
        return moved;
    }

    // What the request's own last press left on the page, read once the page has settled: two drawn frames after the
    // press, and after the window's own scroll to a card. A change of the request's: every part is its own now. A put-back
    // of the request's: the scroll it still owns is read again, since the list was laid out anew.
    void DevSettle()
    {
        if (_devSettle is not { } settle || _scrollTo != null || (long)Engine.GetFramesDrawn() < settle.Frame + 2) return;
        _devSettle = null;
        _devSettledTick = _devTick;
        if (_hunting == null || _page == null || !GodotObject.IsInstanceValid(_page)) return;
        var action = _hunting.Action;
        if (settle.Both)
        {
            _devOwnLocation = new(action.SelectedLocation);
            _devOwnAnimal = new(action.SelectedAnimal);
            _devOwnJournal = DevJournalNowOrNull() is { } journal ? new(journal) : null;
            _devOwnScroll = new(DevScrollNow());
            _devScrollUnder = action.SelectedLocation;
        }
        else if (_devOwnScroll != null)
        {
            _devOwnScroll = new(DevScrollNow());
            _devScrollUnder = action.SelectedLocation;
        }
        DevLog($"page | settled after the request's {(settle.Both ? "press" : "put-back")}: the request owns the location " +
            $"{(_devOwnLocation is { } l ? l.Value?.Location?.Id ?? "none" : "(none)")}, the animal {(_devOwnAnimal is { } a ? a.Value?.Animal.Id ?? "none" : "(none)")}, " +
            $"the view {(_devOwnJournal is { } j ? j.Value ? "journal" : "hunt" : "(none)")} and the scroll {(_devOwnScroll is { } s ? s.Value?.ToString() ?? "-" : "(none)")}");
    }

    void DevFocusOff()
    {
        if (_devFocusAfter is not { } after || (long)Engine.GetFramesDrawn() < after.Frame + 1) return;
        _devFocusAfter = null;
        Control? owner = _ctx.Tree.Root.GuiGetFocusOwner();
        if (owner == null || !GodotObject.IsInstanceValid(after.Target) || (owner != after.Target && !after.Target.IsAncestorOf(owner))) return;
        owner.ReleaseFocus();
        DevLog($"focus | the keyboard focus taken off {DevName(owner)}, which the request's press gave it");
    }

    // The page's parts as they were just before the request's first change (_devOrigin), each on its own by the rule
    // above: the view through the page's own buttons (the hunt's or the journal's, pressed by their signal as a click on a
    // line presses the hunt's); the location and the animal through the members a click on a line uses
    // (HuntingActionComponent.SelectLocation, SelectAnimal). A part that could not be put back stays the request's own,
    // for the final put-back. The scroll goes back on its own afterwards (DevScrollBack).
    string DevPageBack()
    {
        if (_devOrigin is not { } origin || !DevOwnsAny) return "nothing of the request's is left on the page";
        if (_page == null || !GodotObject.IsInstanceValid(_page)) return "the Hunting page is gone: its selection NOT AS IT WAS (not put back)";
        var action = _hunting!.Action;
        var said = new List<string>();
        foreach (string part in DevHisNow()) said.Add($"{part} is the player's, and is left");
        if (_devOwnJournal is { } journal)
        {
            if (journal.Value == origin.Journal) { _devOwnJournal = null; said.Add($"the {(origin.Journal ? "journal" : "hunt")} shown, as it was"); }
            else if (Native.TrackingView(_page) is { } view && (origin.Journal ? DevNative.JournalButton(view) : Native.HuntButton(view)) is { } button)
            {
                if (button.ToggleMode && !button.ButtonPressed) button.ButtonPressed = true;
                button.EmitSignal(BaseButton.SignalName.Pressed);
                bool took = DevJournalNow() == origin.Journal;
                if (took) _devOwnJournal = null;
                said.Add(took ? $"the {(origin.Journal ? "journal" : "hunt")} shown again with the page's own button"
                    : $"the view NOT AS IT WAS: the page's own button did not show the {(origin.Journal ? "journal" : "hunt")}");
            }
            else said.Add("the view NOT AS IT WAS: the page's own button was not found");
        }
        bool changed = false;
        if (_devOwnLocation is { } location)
        {
            if (ReferenceEquals(action.SelectedLocation, origin.Location)) { _devOwnLocation = null; said.Add("the location as it was"); }
            else
            {
                try
                {
                    action.SelectLocation(origin.Location!);
                    changed = true;
                    // What the game did with the animal on the request's own put-back is the request's, not his.
                    if (_devOwnAnimal != null) _devOwnAnimal = new(action.SelectedAnimal);
                    bool took = ReferenceEquals(action.SelectedLocation, origin.Location);
                    if (took) _devOwnLocation = null;
                    said.Add(took ? $"the location put back to {origin.Location?.Location?.Id ?? "none"}" : $"the location NOT AS IT WAS (now {action.SelectedLocation?.Location?.Id ?? "none"})");
                }
                catch (Exception e) { said.Add($"the location NOT AS IT WAS: SelectLocation failed ({e.GetType().Name}: {e.Message})"); }
            }
        }
        if (_devOwnAnimal is { } animal)
        {
            if (ReferenceEquals(action.SelectedAnimal, origin.Animal)) { _devOwnAnimal = null; said.Add("the animal as it was"); }
            else if (_devOwnLocation != null) said.Add("the animal left with the request's location, which is not put back: both stay the request's own");
            else
            {
                try
                {
                    action.SelectAnimal(origin.Animal!);
                    changed = true;
                    bool took = ReferenceEquals(action.SelectedAnimal, origin.Animal);
                    if (took) _devOwnAnimal = null;
                    else _devOwnAnimal = new(action.SelectedAnimal);
                    said.Add(took ? $"the animal put back to {origin.Animal?.Animal.Id ?? "none"}" : $"the animal NOT AS IT WAS (now {action.SelectedAnimal?.Animal.Id ?? "none"})");
                }
                catch (Exception e) { said.Add($"the animal NOT AS IT WAS: SelectAnimal({origin.Animal?.Animal.Id ?? "none"}) failed ({e.GetType().Name}: {e.Message})"); }
            }
        }
        // The list was laid out anew: the scroll the request still owns is read again once it settled.
        if (changed) _devSettle = ((long)Engine.GetFramesDrawn(), false);
        return DevJoin(said.ToArray());
    }

    // The scroll the request owns, read again on a later tick than what it left was read: still exactly that, it is put
    // back to where it was before the request's first change; otherwise the player moved it, and it is left.
    string DevScrollBack()
    {
        if (_devOwnScroll is not { } own || _devOrigin is not { } origin) return "";
        if (_devOwnLocation != null) return "the scroll left with the request's location, which is not put back: both stay the request's own";
        if (!ReferenceEquals(_hunting!.Action.SelectedLocation, _devScrollUnder))
        {
            _devOwnScroll = null;
            _devHis.Add("scroll");
            return "the scroll is left: the page shows another location's list now, the player's";
        }
        int? now = DevScrollNow();
        if (now != own.Value)
        {
            _devOwnScroll = null;
            _devHis.Add("scroll");
            return $"the scroll {now?.ToString() ?? "-"} is not what the request left ({own.Value?.ToString() ?? "-"}): the player moved it, and it is left";
        }
        if (now == origin.Scroll) { _devOwnScroll = null; return "the scroll as it was"; }
        if (origin.Scroll is not { } back || !ReferenceEquals(_hunting.Action.SelectedLocation, origin.Location))
        {
            _devOwnScroll = null;
            return origin.Scroll == null ? "the scroll NOT AS IT WAS: where it stood was not read, so it is not written" : "the scroll left: the page's location is not the one it was read under";
        }
        if (DevListScroll() is not { } list) { _devOwnScroll = null; return "the scroll NOT AS IT WAS: the list is not found"; }
        list.ScrollVertical = back;
        int? after = DevScrollNow();
        if (after == back) { _devOwnScroll = null; return $"the scroll put back to {back}"; }
        _devOwnScroll = new(after);
        _devScrollUnder = _hunting.Action.SelectedLocation;
        return $"the scroll NOT AS IT WAS (put back to {back}, it reads {after?.ToString() ?? "-"}); what the request's write left stays its own";
    }

    // ---- the steps ---------------------------------------------------------------------------------------------------

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
        _devOwnLocation = null;
        _devOwnAnimal = null;
        _devOwnJournal = null;
        _devOwnScroll = null;
        _devSettle = null;
        _devSettledTick = -1;
        _devOrigin = _devOriginWait = null;
        _devOnTheWay.Clear();
        _devHis.Clear();
        _devFocusAfter = null;
        _devPressFrame = -1;
        _devWithout?.Dispose();
        _devWithout = null;
        _devStarted = Time.GetTicksMsec();
        _devAction = _hunting!.Action;
        GameView view = GameAccess.FindGameView() ?? throw new InvalidOperationException("No character is open.");
        var action = _hunting.Action;
        _devSettingsCopy = File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : null;
        _devSettingsRead = true;
        var before = new DevBefore(DevNative.CurrentPage(view)?.Item1, _wanted, _window is { IsAlive: true, Visible: true },
            _window is { IsAlive: true } ? _window.Minimised : null, GodotObject.IsInstanceValid(_opener) ? _opener!.ButtonPressed : null,
            _devSettingsCopy, DevPageNow());
        string huntAtStart = DevHunt(), choicesAtStart = DevChoices();
        void Add(string name, Func<bool> run, ulong timeout = StepTime) => _devSteps.Add((name, run, timeout));
        DevLog($"before: page {before.Page?.Id ?? "none"}, window {(before.Wanted ? "open" : "closed")}{(before.Shown ? " and on the screen" : "")}" +
            $"{(before.Folded == true ? ", folded" : "")}, round button {(before.Lit is { } lit ? lit ? "lit" : "unlit" : "not made yet")}, settings.json " +
            (before.Settings == null ? "not there" : $"{before.Settings.Length} characters: {before.Settings}") +
            $" | {huntAtStart} | the page: {DevPageWords(before.Hunting)} | the choices: {choicesAtStart} | the real pointer at {DisplayServer.MouseGetPosition()}");

        Add("open the Hunting page", () =>
        {
            if (PageShown) return true;
            if (_devOpenedPage) return false;
            var option = _game!.Layout.Sidebar.Options.FirstOrDefault(o =>
                o.Page is EntityPage p && ReferenceEquals(p.Entity, _hunting) && o.Requirements?.AreMet != false && o.DisplayRequirements?.AreMet != false);
            if (option?.Page is not EntityPage page)
                throw new InvalidOperationException("Hunting cannot be opened from the sidebar: open the Hunting page by hand and send the request again.");
            view.SetCurrentPage(page);
            _devOpenedPage = true;
            return false;
        });
        Add("the round button", () =>
        {
            if (!PageShown || _opener?.IsVisibleInTree() != true) return false;
            // The page's own parts (its view and the scroll of its list) of a page the request had to open are read now
            // that they are there; its selection is the character's, read at the start.
            if (_devOpenedPage) before = before with { Hunting = before.Hunting with { Journal = DevJournalNow(), Scroll = DevScrollNow() } };
            DevLog($"page | the page is {DevNative.CurrentPage(view)?.Item1?.Id ?? "none"}, the Hunting page is shown: {PageShown}; {DevPageWords(before.Hunting)}");
            DevStrip();
            if (IsOpen) Close();
            return true;
        });
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
            Refresh();
            int rows = DevRows("open");
            if (rows == 0) throw new InvalidOperationException("The window lists no animal.");
            DevLog($"note | under the list: '{_list!.DevNote}'");
            DevGame();
            DevCards();
            DevWords();
            DevSpiders();
            DevHidden();
            DevDiscovery();
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
            // An animal that can be hunted first, with its whole hour in the tooltip.
            target = (lines.FirstOrDefault(l => !l.Animal.Locked && l.Animal.Discovered).Button ?? lines.FirstOrDefault().Button)
                ?? throw new InvalidOperationException("No line to point at.");
            DevLog($"tooltip | the line pointed at: {lines.First(l => l.Button == target).Animal.Animal.Id}");
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

        // A click on a line of an animal at another location than the page's, then the page's own location button and
        // the animal's card button for the same animal, to compare. The selection is put back after each.
        AnimalRates? clicked = null;
        DevClickEvents? events = null;
        Image? beforeClick = null;
        string huntBefore = "", choicesBefore = "", ours = "", ourEvents = "", pageBack = "";
        bool huntChanged = false, compareSkipped = false;

        void AddPageBack(string name, Func<bool> skip, string head)
        {
            Add($"{name}: the selection", () =>
            {
                if (skip()) return true;
                if (_devSettle != null || _devSettledTick >= _devTick) return false;
                pageBack = DevPageBack();
                return true;
            });
            Add($"{name}: the scroll", () =>
            {
                if (skip()) return true;
                if (_devOwnScroll != null && (_devSettle != null || _devSettledTick >= _devTick)) return false;
                string scrollWords;
                try { scrollWords = DevScrollBack(); }
                catch (Exception e) { scrollWords = $"the scroll NOT AS IT WAS: its put-back failed ({e.GetType().Name}: {e.Message})"; }
                string said = DevJoin(pageBack, scrollWords);
                DevLog($"{head} {said}; now {DevPageWords(DevPageNow())}");
                if (said.Contains("NOT AS IT WAS")) _devOnTheWay.Add($"{head} {said}");
                pageBack = "";
                return true;
            });
        }

        Add("a line to click", () =>
        {
            var location = _hunting!.Action.SelectedLocation;
            var lines = _list!.DevLines().Select(l => l.Animal).Where(a => a.Clickable && a.Click == AnimalRates.Opens.Animal).ToList();
            // An animal at another location than the page's first: the click then changes both, as the issue asks.
            clicked = lines.FirstOrDefault(a => !ReferenceEquals(a.Active.Location, location)) ?? lines.FirstOrDefault();
            if (clicked == null) { DevLog("click | no line opens an animal a click would change: none is clicked."); return true; }
            _list.DevScroll.EnsureControlVisible(DevLineOf(clicked));
            huntBefore = DevHunt();
            choicesBefore = DevChoices();
            DevLog($"click | the line of {clicked.Animal.Id} at {clicked.Active.Location?.Location?.Id}{(clicked.OnPage ? ", the animal the page shows" : "")}; " +
                $"before: {DevSelection(clicked)}; {huntBefore}");
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
            events ??= new DevClickEvents(_hunting!.Action, () => _devInClick, DevLog);
            AnimalRates line = clicked;
            bool? pressed = DevPagePress(() => DevClickLine(line), before, $"the click on the line of {line.Animal.Id}");
            if (pressed != null) return pressed.Value;
            events.Dispose();
            events = null;
            clicked = null;
            return true;
        }, PointerStepTime);
        Add("the animal shown", () =>
        {
            if (clicked == null) return true;
            if (_scrollTo != null || _devSettle != null || _devSettledTick >= _devTick || !DevFramesDrawn()) return false;
            var action = _hunting!.Action;
            Refresh();
            string hunt = DevHunt(), choices = DevChoices();
            ours = DevSelection(clicked);
            ourEvents = string.Join(", ", events!.Seen);
            var during = events.During.ToList();
            huntChanged = during.Any(e => e.StartsWith("HUNT"));
            bool opened = ReferenceEquals(action.SelectedLocation, clicked.Active.Location) && ReferenceEquals(action.SelectedAnimal, clicked.Active);
            string panelName = DevPanelName();
            var lit = _list!.DevLines().Where(l => l.Animal.OnPage).Select(l => l.Animal.Animal.Id).ToList();
            HuntingAnimalTrackingView? card = Native.Card(_page!, clicked.Active);
            string sight = card == null ? "no card in the page's list" : DevCardSight(card, true);
            DevLog($"click | after the click on the line of {clicked.Animal.Id}: {ours}; the panel names '{panelName}'; {sight}; the game's events [{ourEvents}], " +
                $"during the click itself [{string.Join(", ", during)}]; {hunt}" + DevHuntMarks(huntBefore, hunt) + (huntChanged ? "  HUNT CHANGED (by an event during the click)" : "") +
                (choices == choicesBefore ? "" : $"  CHOICES CHANGED ({choices})") + (opened ? "" : "  NOT OPENED") +
                (panelName.Length > 0 && !string.Equals(panelName, clicked.Animal.DisplayName.Text, StringComparison.OrdinalIgnoreCase) && clicked.Discovered ? "  THE PAGE SHOWS ANOTHER ANIMAL" : "") +
                (lit.SequenceEqual(new[] { clicked.Animal.Id }) ? "" : $"  THE LIT LINE IS [{string.Join(", ", lit)}]") +
                (IsOpen ? "" : "  THE WINDOW CLOSED"));
            Shot("clicked");
            if (beforeClick != null)
            {
                if (card != null) DevLog("click | the animal's card in shot-hunting-clicked.png: " + DevProofBeside(beforeClick, card, "the card"));
                if (Native.TrackingView(_page!) is { } tv && Native.Panel(tv) is { } panel && panel.IsVisibleInTree())
                    DevLog("click | the panel in shot-hunting-clicked.png: " + DevProofBeside(beforeClick, panel, "the panel"));
            }
            events.Dispose();
            events = null;
            return true;
        });
        AddPageBack("the page back after the click", () => clicked == null, "click | the selection put back for the page's own buttons:");
        // The page's own buttons, pressed with the pointer as a player would: the location's button, then the animal's card
        // button. Not while the click changed the hunt: the game's buttons would change it again.
        Control? locationButton = null;
        Add("the page's own location button", () =>
        {
            if (clicked == null) return true;
            if (huntChanged) { compareSkipped = true; DevLog("click | the click changed the hunt in progress: the page's own buttons are not pressed, to change nothing more"); return true; }
            var action = _hunting!.Action;
            if (ReferenceEquals(action.SelectedLocation, clicked.Active.Location)) { DevLog("click | the page shows the animal's location already: its button is not pressed"); return true; }
            if (locationButton == null)
            {
                var select = Native.TrackingView(_page!) is { } tv ? Native.LocationSelect(tv) : null;
                locationButton = select != null && DevNative.LocationButtons(select) is { } buttons && clicked.Active.Location?.Location is { } place &&
                    buttons.TryGetValue(place, out HuntingLocationButton? b) && GodotObject.IsInstanceValid(b) ? b : null;
                if (locationButton == null) throw new InvalidOperationException($"the page has no location button of {clicked.Active.Location?.Location?.Id}");
                if (!DevSight(locationButton).HasArea())
                {
                    compareSkipped = true;
                    DevLog($"click | the page's own location button of {clicked.Active.Location?.Location?.Id} lies under the mod's own window: it is not pressed, and the click is not compared with it");
                    return true;
                }
            }
            events ??= new DevClickEvents(action, DevInGamePress, DevLog);
            bool? pressed = DevPagePress(() => DevGamePress(locationButton, $"the page's own location button of {clicked.Active.Location?.Location?.Id}"),
                before, "the press of the page's own location button, to compare");
            if (pressed != null) return pressed.Value;
            compareSkipped = true;
            return true;
        }, PointerStepTime);
        Button? cardButton = null;
        Add("the page's own animal button", () =>
        {
            if (clicked == null || compareSkipped) return true;
            if (_devSettle != null || _devSettledTick >= _devTick || !DevFramesDrawn()) return false;
            var action = _hunting!.Action;
            if (cardButton == null)
            {
                DevLog($"click | after the page's own location button: {DevSelection(clicked)}");
                if (ReferenceEquals(action.SelectedAnimal, clicked.Active)) { DevLog("click | the location's button selected the animal already: its card's button is not pressed"); return true; }
                HuntingAnimalTrackingView card = Native.Card(_page!, clicked.Active) ?? throw new InvalidOperationException($"the page's list has no card of {clicked.Animal.Id}");
                cardButton = DevNative.CardButton(card);
                // The page's list is not scrolled for it: that would be a change of the page the request does not own.
                if (!DevSight(cardButton).HasArea())
                {
                    compareSkipped = true;
                    DevLog($"click | the card's button of {clicked.Animal.Id} is out of sight or under the mod's own window: it is not pressed, and the click is not compared with it");
                    return true;
                }
            }
            events ??= new DevClickEvents(action, DevInGamePress, DevLog);
            bool? pressed = DevPagePress(() => DevGamePress(cardButton, $"the card's button of {clicked.Animal.Id}"), before, "the press of the card's own button, to compare");
            if (pressed != null) return pressed.Value;
            compareSkipped = true;
            return true;
        }, PointerStepTime);
        Add("the page's buttons compared", () =>
        {
            if (clicked == null || compareSkipped) { events?.Dispose(); events = null; return true; }
            if (_devSettle != null || _devSettledTick >= _devTick || !DevFramesDrawn()) return false;
            string game = DevSelection(clicked), gameEvents = string.Join(", ", events?.Seen ?? new List<string>()), hunt = DevHunt(), choices = DevChoices();
            var during = events?.During.ToList() ?? new List<string>();
            DevLog($"click | after the page's own buttons: {game}; the game's events [{gameEvents}], in the frames of the presses [{string.Join(", ", during)}]; {hunt}" +
                DevHuntMarks(huntBefore, hunt) + (during.Any(e => e.StartsWith("HUNT")) ? "  HUNT CHANGED (by the game's own buttons)" : "") +
                (choices == choicesBefore ? "" : $"  CHOICES CHANGED ({choices})") + " | " +
                (game == ours ? "the page as after the click on the line" : $"DIFFER from the click on the line ({ours})") + "; " +
                (DevSameEvents(gameEvents, ourEvents) ? "the same events" : $"EVENTS DIFFER from the click on the line ([{ourEvents}])"));
            events?.Dispose();
            events = null;
            return true;
        });
        AddPageBack("the page back after its own buttons", () => clicked == null || compareSkipped, "click | the selection put back:");
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
            if (open != null) DevLog("shot-hunting-closed.png | where the window was: " + DevChanged(open, now, openAt, "the window's place", "gone from the picture", "STILL IN THE PICTURE"));
            return true;
        });

        _devPutBackAt = _devSteps.Count;
        var said = new List<string>();
        bool readOk = true, readAll = true;
        void Say(bool same, string what, string now)
        {
            readOk &= same;
            said.Add(same ? $"{what} as it was" : $"{what} NOT AS IT WAS (now {now})");
        }
        void SayPage(bool same, string part, string what, string now)
        {
            if (!same && _devHis.Contains(part)) said.Add($"{what}: left as the player changed it (now {now})");
            else Say(same, what, now);
        }
        // One part of the put-back in its own try: one line, NOT AS IT WAS for a part that failed, and the next part runs.
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
        AddPageBack("put back the page", () => false, "put back | the page's selection, view and scroll:");
        Add("read back the Hunting page", () =>
        {
            var action = _hunting!.Action;
            DevPageWas want = _devOrigin ?? before.Hunting;
            if (!PageShown)
            {
                said.Add($"the Hunting page's selection NOT READ BACK: the page on the screen is {DevNative.CurrentPage(view)?.Item1?.Id ?? "none"}, the player's");
                readAll = false;
                return true;
            }
            string when = _devOrigin != null && _devOrigin != before.Hunting ? " (as the player left it before the request's first change)" : "";
            SayPage(ReferenceEquals(action.SelectedLocation, want.Location), "location", $"the page's location {want.Location?.Location?.Id ?? "none"}{when}",
                action.SelectedLocation?.Location?.Id ?? "none");
            SayPage(ReferenceEquals(action.SelectedAnimal, want.Animal), "animal", $"the page's animal {want.Animal?.Animal.Id ?? "none"}", action.SelectedAnimal?.Animal.Id ?? "none");
            SayPage(DevJournalNow() == want.Journal, "view", $"the {(want.Journal ? "journal" : "hunt")} shown", DevJournalNow() ? "the journal" : "the hunt");
            SayPage(DevScrollNow() == want.Scroll, "scroll", $"the page's list scrolled to {want.Scroll?.ToString() ?? "-"}", DevScrollNow()?.ToString() ?? "-");
            return true;
        });
        Add("put back the page that was shown", () =>
        {
            Part("the page", () =>
            {
                if (before.Page == null || !_devOpenedPage) return "as it was: the request did not open Hunting";
                GamePage? now = DevNative.CurrentPage(view)?.Item1;
                if (ReferenceEquals(now, before.Page)) return $"{before.Page.Id}, as it was";
                if (!PageShown) return $"left on {now?.Id ?? "none"}: the player went there from the Hunting page the request opened, and it is his";
                view.SetCurrentPage(before.Page);
                return $"back to {before.Page.Id}, the page on the screen before the request opened Hunting";
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
            // The hunt goes on by itself while the request runs: whether it is started, which animal and the trap in use
            // are what must not have changed; its state and steps move.
            string huntNow = DevHunt();
            Say(DevHuntMarks(huntAtStart, huntNow).Length == 0, $"the hunt ({DevHuntKey(huntAtStart)})", DevHuntKey(huntNow));
            Say(DevChoices() == choicesAtStart, "every animal's trap, bait and trapless choice", DevChoices());
            said.Add("the pointer back where the real one is");
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

    bool DevHuntingNow() => _game!.ActionManager.ActiveActions.Any(a => ReferenceEquals(a.Entity, _hunting));

    // The hunt in progress: no click of the request may change it. Its state and steps move by themselves.
    string DevHunt()
    {
        var action = _hunting!.Action;
        ActiveHuntingAnimal? hunted = action.CurrentAnimal;
        return $"hunting: started {action.IsStarted}, active {DevHuntingNow()}, the animal hunted {hunted?.Animal.Id ?? "none"}, the trap in use {action.CurrentTrap?.Id ?? "none"}" +
            (hunted == null ? "" : $"; its state {hunted.State}, step {hunted.CurrentStep} of {hunted.RequiredSteps}, tracks {hunted.Tracks}, trap fails {hunted.TrapFailCount}");
    }

    // The part of the hunt that must stay: started, the animal, the trap in use.
    static string DevHuntKey(string hunt) => hunt.Split(';')[0];

    static string DevHuntMarks(string before, string after)
    {
        string Part(string all, string key) => Regex.Match(all, key + @" ([\w:]+)").Groups[1].Value;
        string marks = "";
        if (Part(before, "started") == "False" && Part(after, "started") == "True") marks += "  HUNT STARTED";
        if (Part(before, "started") == "True" && Part(after, "started") == "False") marks += "  HUNT STOPPED";
        if (Part(before, "animal hunted") != Part(after, "animal hunted") || Part(before, "trap in use") != Part(after, "trap in use")) marks += "  HUNT CHANGED";
        return marks;
    }

    // Every animal's choices that are the player's: its trap, its bait and hunting it without a trap, and whether traps
    // swap by themselves (HuntingActionComponent.AutoSwapToNextTrap).
    string DevChoices()
    {
        var action = _hunting!.Action;
        return $"auto swap {action.AutoSwapToNextTrap}; " + string.Join(", ", action.Animals.Values.Where(a => a != null)
            .Select(a => $"{a.Animal.LocalId} {a.SelectedTrap?.LocalId ?? "-"}/{(a.UseBait ? "bait" : "no bait")}/{(a.TraplessEnabled ? "trapless" : "trap")}"));
    }

    // The page's selection and what it shows: the location and the animal, the view, the panel's name, the list's card.
    string DevSelection(AnimalRates a)
    {
        var action = _hunting!.Action;
        HuntingAnimalTrackingView? card = Native.Card(_page!, a.Active);
        return $"the location {action.SelectedLocation?.Location?.Id ?? "none"}, the animal {action.SelectedAnimal?.Animal.Id ?? "none"}, the {(DevJournalNow() ? "journal" : "hunt")} shown, " +
            $"the list holds {Native.Cards(_page!).Count()} cards, the card of {a.Animal.Id} " + (card == null ? "not in it" : $"pressed {DevNative.CardButton(card).ButtonPressed}");
    }

    string DevPanelName()
    {
        try { return Native.TrackingView(_page!) is { } view && Native.Panel(view) is { } panel && panel.IsVisibleInTree() ? DevNative.PanelName(panel).Text : ""; }
        catch (Exception) { return ""; }
    }

    string DevCardSight(HuntingAnimalTrackingView card, bool places)
    {
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
        string list = Native.ScrollOf(card) is { } scroll ? $"its list at {Native.Drawn(scroll)} scrolled to {scroll.ScrollVertical}" : "in no scrolling list";
        return $"{said} (the card at {Native.Drawn(card)}, {list}, the window at {Cover()?.ToString() ?? "none"})";
    }

    Button DevLineOf(AnimalRates a) => _list!.DevLines().First(l => ReferenceEquals(l.Animal.Active, a.Active)).Button;

    bool DevClickLine(AnimalRates a)
    {
        Button line = DevLineOf(a);
        if (!DevSight(line).HasArea()) return false;
        return DevPress(line, $"the line of {a.Name}");
    }

    // A game button pressed with the pointer (DevPress), with the frame it was pressed in: the game's events of that frame
    // are the press's (DevClickEvents).
    long _devPressFrame = -1;
    bool _devGamePressing;
    bool DevGamePress(Control target, string what)
    {
        _devGamePressing = true;
        try
        {
            if (!DevPress(target, what)) return false;
        }
        finally { _devGamePressing = false; }
        _devPressFrame = (long)Engine.GetProcessFrames();
        return true;
    }

    // While the request's press of a game button is dispatched, or in its frame or the next (an input the game handles at
    // the next flush): the game's events then are the press's own, and a hunt's event there counts against the button.
    bool DevInGamePress()
    {
        long frame = (long)Engine.GetProcessFrames();
        return _devGamePressing || (_devPressFrame >= 0 && (frame == _devPressFrame || frame == _devPressFrame + 1));
    }

    static bool DevSameEvents(string a, string b) =>
        a.Split(", ", StringSplitOptions.RemoveEmptyEntries).Order().SequenceEqual(b.Split(", ", StringSplitOptions.RemoveEmptyEntries).Order());

    // The game's events about the page's selection and about the hunt, listened to while one click is made: those raised
    // while the click itself runs (now()) are its own; the hunt's other events are the hunt going on by itself. Each
    // listener runs inside a try; they are taken off by Dispose, which Stop and Detach reach through DevUnwatchAll.
    sealed class DevClickEvents : IDisposable
    {
        public readonly List<string> Seen = new();
        public readonly List<string> During = new();
        readonly List<Action> _off = new();
        static DevClickEvents? s_current;

        public DevClickEvents(HuntingActionComponent action, Func<bool> now, Action<string> log)
        {
            s_current?.Dispose();
            s_current = this;
            void Safe(Action add)
            {
                try { add(); }
                catch (Exception e) { log("click events | a listener failed: " + e.Message); }
            }
            void Saw(string what, bool hunt)
            {
                if (!hunt) Seen.Add(what);
                if (now()) During.Add(what);
            }
            EventHandler<ValueChangedEventArgs<ActiveHuntingLocation>> location = (_, e) => Safe(() => Saw($"the location {e.NewValue?.Location?.Id ?? "none"}", false));
            EventHandler<ValueChangedEventArgs<ActiveHuntingAnimal>> animal = (_, e) => Safe(() => Saw($"the animal {e.NewValue?.Animal.Id ?? "none"}", false));
            EventHandler<ValueChangedEventArgs<ActiveHuntingAnimal>> hunted = (_, e) => Safe(() => Saw($"HUNT: the animal hunted {e.NewValue?.Animal.Id ?? "none"}", true));
            EventHandler<ValueChangedEventArgs<GamesByMalcs.Melvor2.Data.Items.Item>> trap = (_, e) => Safe(() => Saw($"HUNT: the trap in use {e.NewValue?.Id ?? "none"}", true));
            EventHandler<ValueChangedEventArgs<bool>> swap = (_, e) => Safe(() => Saw($"HUNT: auto swap {e.NewValue}", true));
            EventHandler<ActionEventArgs> started = (_, _) => Safe(() => Saw("HUNT STARTED", true)), stopped = (_, _) => Safe(() => Saw("HUNT STOPPED", true));
            action.SelectedLocationChanged += location;
            action.SelectedAnimalChanged += animal;
            action.CurrentAnimalChanged += hunted;
            action.CurrentTrapChanged += trap;
            action.AutoSwapToNextTrapChanged += swap;
            action.Started += started;
            action.Stopped += stopped;
            _off.Add(() =>
            {
                action.SelectedLocationChanged -= location;
                action.SelectedAnimalChanged -= animal;
                action.CurrentAnimalChanged -= hunted;
                action.CurrentTrapChanged -= trap;
                action.AutoSwapToNextTrapChanged -= swap;
                action.Started -= started;
                action.Stopped -= stopped;
            });
            foreach (ActiveHuntingAnimal a in action.Animals.Values)
            {
                if (a == null) continue;
                string id = a.Animal.LocalId;
                EventHandler<ValueChangedEventArgs<HuntState>> state = (_, e) => Safe(() => Saw($"HUNT: {id} state {e.NewValue}", true));
                EventHandler<ValueChangedEventArgs<int>> step = (_, e) => Safe(() => Saw($"HUNT: {id} step {e.NewValue}", true)),
                    steps = (_, e) => Safe(() => Saw($"HUNT: {id} steps {e.NewValue}", true)),
                    tracks = (_, e) => Safe(() => Saw($"HUNT: {id} tracks {e.NewValue}", true)),
                    fails = (_, e) => Safe(() => Saw($"HUNT: {id} trap fails {e.NewValue}", true));
                EventHandler<ValueChangedEventArgs<GamesByMalcs.Melvor2.Data.Items.Item>> chosen = (_, e) => Safe(() => Saw($"CHOICES: {id} trap {e.NewValue?.Id ?? "none"}", false));
                EventHandler<ValueChangedEventArgs<bool>> bait = (_, e) => Safe(() => Saw($"CHOICES: {id} bait {e.NewValue}", false)),
                    trapless = (_, e) => Safe(() => Saw($"CHOICES: {id} trapless {e.NewValue}", false));
                a.StateChanged += state;
                a.CurrentStepChanged += step;
                a.RequiredStepsChanged += steps;
                a.TracksChanged += tracks;
                a.TrapFailCountChanged += fails;
                a.SelectedTrapChanged += chosen;
                a.UseBaitChanged += bait;
                a.TraplessEnabledChanged += trapless;
                _off.Add(() =>
                {
                    a.StateChanged -= state;
                    a.CurrentStepChanged -= step;
                    a.RequiredStepsChanged -= steps;
                    a.TracksChanged -= tracks;
                    a.TrapFailCountChanged -= fails;
                    a.SelectedTrapChanged -= chosen;
                    a.UseBaitChanged -= bait;
                    a.TraplessEnabledChanged -= trapless;
                });
            }
        }

        public void Dispose()
        {
            foreach (Action off in _off) off();
            _off.Clear();
            if (s_current == this) s_current = null;
        }

        public static void DisposeCurrent() => s_current?.Dispose();
    }

    void DevUnwatchAll()
    {
        DevClickEvents.DisposeCurrent();
        DevUnwatchHunts();
    }

    // ---- what is logged ------------------------------------------------------------------------------------------

    static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    static string R(double? value) => value is { } v ? R(v) : "-";

    static string DevTexture(Texture2D? texture) =>
        texture == null ? "none" : $"{(texture.ResourcePath is { Length: > 0 } path ? path : "a " + texture.GetType().Name)} {texture.GetWidth()}x{texture.GetHeight()}";

    static string DevName(Control? control) => control == null ? "nothing" : $"{control.GetType().Name} '{control.Name}'";

    static string DevWordsOf(string text) => StringUtils.StripBBCode(text).Trim();

    void DevStrip()
    {
        if (_opener?.GetParent() is not Node strip) return;
        foreach (Node child in strip.GetChildren())
            DevLog($"strip | {child.Name} | {child.GetType().Name} | shown={(child as CanvasItem)?.IsVisibleInTree()} | " +
                $"icon {DevTexture((child as Button)?.Icon)} | tooltip '{(child as Control)?.TooltipText}'");
    }

    static string DevReadingName(ChainRule rule) =>
        $"{rule.Surveys}/{rule.Step}/{rule.Skip}/{rule.Trap}" + (AnimalRates.ChainRule?.Reading == rule ? " (THE SETTLED RULE)" : "");

    // Every line: what the game gives (raw), what the mod counted from it, the time of a catch by every reading of the
    // chain, what is drawn, and its tooltip.
    int DevRows(string when)
    {
        int count = 0;
        foreach (var (a, button, drawn, picture) in _list!.DevLines())
        {
            count++;
            HuntingActionStats s = a.Active.Stats;
            HuntingAnimal h = a.Animal;
            string Items(GamesByMalcs.Melvor2.Stats.Nodes.ValuesStatNode<GamesByMalcs.Melvor2.Data.Items.ItemQuantityChance>? node) =>
                string.Join(", ", (node?.Values ?? []).Select(n => $"{n?.Value?.Item?.Id} {R(n?.Value?.Chance ?? double.NaN)} x{n?.Value?.Quantity}"));
            DevLog($"row | {when} | {h.Id} at {a.Active.Location?.Location?.Id} | locked={a.Locked}, level={a.Level}, needs '{a.Needs}', on the page={a.OnPage}, " +
                $"hunted={a.Hunted}, discovered={a.Discovered}, a click opens {a.Click}, clickable={a.Clickable} | raw: survey {R(s.SurveyInterval.Value)} ms " +
                $"(the location's {a.Active.Location?.Location?.BaseSurveyInterval}), steps {s.TrackingSteps.Value} (base {h.BaseMinTrackingSteps}..{h.BaseMaxTrackingSteps}), " +
                $"step {s.TrackingInterval.Value} ms (base {h.BaseMinTrackInterval}..{h.BaseMaxTrackInterval}), tracking {R(s.TrackingSuccessChance.Value)} (base {R(h.BaseTrackSuccessChance)}), " +
                $"skip {R(s.SkipTrackingStepChance?.Total.Value ?? double.NaN)}, trap {s.TrappingInterval.Value} ms (base {h.BaseMinTrapInterval}..{h.BaseMaxTrapInterval}), " +
                $"trapping {R(s.TrappingSuccessChance.Value)} (base {R(h.BaseTrapSuccessChance)}), reattempt {R(s.TrappingReattemptChance?.Total.Value ?? double.NaN)}, " +
                $"rare {R(s.RareSpeciesChance.Value)} (base {R(h.BaseRareSpeciesChance)}), doubling {R(s.DoublingChance?.Total.Value ?? double.NaN)}, " +
                $"preservation {R(s.PreservationChance?.Total.Value ?? double.NaN)}, XP {R(s.Experience.XpToAdd.Value)} (base {h.BaseExperience}), insights {R(a.Active.Location?.Location?.BaseInsightsChance ?? double.NaN)}, " +
                $"survey XP base {a.Active.Location?.Location?.BaseSurveyingExperience}, journal {h.BaseJournalTrackingExperience}/{h.BaseJournalTrappingExperience} at level {a.JournalLevel}, " +
                $"products [{Items(s.Products)}], survey finds [{Items(s.SurveyingProducts)}], tool {h.Tool?.Id ?? "none"}, trap type {h.TrapType?.Id ?? "none"}, " +
                $"its trap {a.Trap?.Id ?? "none"} ({a.TrapsInBank} in the bank), trapless {a.Trapless} (enabled by the game {h.EnableTrapless}), bait {h.PreferredBait?.Id ?? "none"} " +
                $"used {a.UseBait} ({a.BaitInBank} in the bank), can track it {_hunting!.Action.CanTrackAnimal(a.Active)} | counted: XP range {(a.XpRange is { } r ? $"{R(r.Low)}..{R(r.High)}" : "NOT READ")} " +
                $"so {R(a.XpPerCatch)} a catch, catch gold {R(a.CatchGold)}, survey gold {R(a.SurveyGold)}, the chain {(AnimalRates.ChainRule is { } c ? $"{c.Reading}, {c.Source}" : "NOT SETTLED")}, " +
                $"time per catch {R(a.SecondsPerCatch)}, catches/h {R(a.CatchesPerHour)}, XP/h {R(a.XpPerHour)}, gold/h {R(a.GoldPerHour)}, traps/h {R(a.TrapsPerHour)}, bait/h {R(a.BaitPerHour)} | " +
                $"rare [{string.Join(", ", a.Rare.Select(sp => $"{sp.Name} {R(sp.Share)}{(sp.Locked.Length > 0 ? " locked" : "")} with [{string.Join(", ", sp.Extras.Select(DevDrop))}]"))}] | " +
                $"picture {DevTexture(picture)} tint {a.PictureTint}{(picture != null && _pictures!.IsStandIn(picture) ? "  STAND-IN DRAWN" : "")}");
            DevLog($"row | {when} | {h.Id} | the time of a catch by each reading: " + string.Join(", ", AnimalRates.Readings.Select(rule =>
                $"{DevReadingName(rule)} {(AnimalRates.Chain(rule, a) is { } t ? $"{R(Math.Round(t.Seconds, 3))} s ({R(Math.Round(t.Surveys, 3))} surveys)" : "never")}")));
            DevLog($"row | {when} | drawn {drawn}");
            DevLog($"row | {when} | tooltip '{button.TooltipText}'");
        }
        var animals = ReadAnimals();
        DevLog($"rows | {when} | {count} lines, {DevRowsInSight()} in sight; the game has {_hunting!.Action.Animals.Count} animals at {_hunting.Action.Locations.Count} locations, " +
            $"{animals.Count} listed, {animals.Count(a => a.Locked)} locked, {animals.Count(a => !a.Discovered)} not discovered; columns [{string.Join(", ", _list.DevColumns)}]; " +
            $"the chain by {(AnimalRates.ChainRule is { } chain ? $"{chain.Reading}, {chain.Source}" : "NO RULE")}, the uses by {(AnimalRates.UseRule is { } use ? $"{use.Reading}, {use.Source}" : "NO RULE")}" +
            (count != animals.Count ? "  MISMATCH" : ""));
        return DevRowsInSight();
    }

    static string DevDrop(AnimalRates.Drop d) =>
        $"{d.Item.Id} chance {R(d.Chance)} x{d.Quantity} each {R(d.Each)}{(d.Collectable ? $" collectable found={d.Found}" : "")} = {R(d.PerHour)}/h, " +
        $"sells {d.Item.SellsFor?.Quantity} {d.Item.SellsFor?.Currency?.Id}, gold {R(d.GoldPerHour)}/h";

    // The longest line of every tip of the window in characters, against Fishing Rates' longest.
    void DevTips(string when)
    {
        static string Longest(string text) => text.Split('\n').OrderByDescending(l => l.Length).First();
        static string Over(string line, int most) => line.Length > most ? $"  LONGER THAN FISHING RATES' {most}" : "";
        var heads = _list!.DevHeaders.Select(h => (h.Text, Line: Longest(h.TooltipText))).ToList();
        var lines = _list.DevLines().Select(l => (l.Animal.Animal.LocalId, Line: Longest(l.Button.TooltipText))).ToList();
        string longest = heads.Select(h => h.Line).Concat(lines.Select(l => l.Line)).OrderByDescending(l => l.Length).First();
        DevLog($"tips | {when} | the names' tips, the longest line of each: {string.Join(", ", heads.Select(h => $"{h.Text} {h.Line.Length}{Over(h.Line, FishingTipMost)}"))}" +
            $" | the lines' tooltips: {string.Join(", ", lines.Select(l => $"{l.LocalId} {l.Line.Length}{Over(l.Line, FishingTooltipMost)}"))}" +
            $" | the longest of all, {longest.Length}: '{longest}'");
    }

    // The page's panel of the animal selected, its own labels beside ours (DIFFER), and its list of bonuses as filled in.
    void DevGame()
    {
        try
        {
            var action = _hunting!.Action;
            if (Native.TrackingView(_page!) is not { } view || Native.Panel(view) is not { } panel)
            {
                DevLog("game | the page's panel of the animal selected NOT FOUND");
                return;
            }
            ActiveHuntingAnimal? selected = action.SelectedAnimal;
            if (selected == null || !panel.IsVisibleInTree() || !DevNative.PanelShown(panel).IsVisibleInTree())
            {
                DevLog($"game | the panel shows no animal (selected {selected?.Animal.Id ?? "none"}, panel shown {panel.IsVisibleInTree()})");
                return;
            }
            AnimalRates ours = ReadAnimal(selected);
            string Same(double game, double mine, double tolerance) => Math.Abs(game - mine) <= tolerance ? "" : "  DIFFER";
            IntervalRangeDisplay survey = DevNative.PanelSurvey(panel), track = DevNative.PanelTrack(panel), trap = DevNative.PanelTrap(panel);
            var chance = DevNative.PanelTrapChance(panel);
            var xp = Native.XpViewOf(panel);
            string xpWords = xp == null ? "no XP view" : $"XP label '{DevNative.XpLabel(xp).Text}' (value {R(DevNative.XpLabel(xp).Value)}), range shown {DevNative.XpRangeBox(xp).IsVisibleInTree()} " +
                $"'{DevNative.XpMinLabel(xp).Text}' to '{DevNative.XpMaxLabel(xp).Text}' ({R(DevNative.XpMinLabel(xp).Value)} to {R(DevNative.XpMaxLabel(xp).Value)}), multipliers " +
                $"{R(Native.XpLow(xp))}..{R(Native.XpHigh(xp))}, bound to the animal's stats {ReferenceEquals(Native.XpOf(xp), selected.Stats.Experience)}";
            string xpSame = xp != null && ours.XpRange is { } range ? Same(DevNative.XpMinLabel(xp).Value, ours.XpEach * range.Low, 0.51) + Same(DevNative.XpMaxLabel(xp).Value, ours.XpEach * range.High, 0.51) : "";
            DevLog($"game | the panel of {selected.Animal.Id}: its name '{DevNative.PanelName(panel).Text}', tool '{DevNative.PanelTool(panel).Text}' | surveying {R(survey.MinSeconds)}..{R(survey.MaxSeconds)} s " +
                $"'{DevLabels(survey)}' against ours {R(ours.SurveySeconds)}{Same(survey.MinSeconds, ours.SurveySeconds, 0.005)} | tracking {R(track.MinSeconds)}..{R(track.MaxSeconds)} s " +
                $"'{DevLabels(track)}' against ours {R(ours.StepMinSeconds)}..{R(ours.StepMaxSeconds)}{Same(track.MinSeconds, ours.StepMinSeconds, 0.005)}{Same(track.MaxSeconds, ours.StepMaxSeconds, 0.005)} | " +
                $"trapping {R(trap.MinSeconds)}..{R(trap.MaxSeconds)} s '{DevLabels(trap)}' against ours {R(ours.TrapMinSeconds)}..{R(ours.TrapMaxSeconds)}" +
                $"{Same(trap.MinSeconds, ours.TrapMinSeconds, 0.005)}{Same(trap.MaxSeconds, ours.TrapMaxSeconds, 0.005)} | trapping chance '{chance.Text}' (value {R(chance.Value)}) against ours " +
                $"{R(ours.TrapChance)} | {xpWords}, ours {R(ours.XpEach)} x the range{xpSame} | the interval formats '{survey.IntervalFormat}', tooltips '{survey.TooltipText}' '{track.TooltipText}' '{trap.TooltipText}'");
            DevLog($"game | the panel's outputs: {DevTexts(DevNative.PanelOutputs(panel))} | ours: products [{string.Join(", ", ours.Products.Select(DevDrop))}], rare " +
                $"{R(ours.RareChance)} [{string.Join(", ", ours.Rare.Select(sp => sp.Name))}], survey finds [{string.Join(", ", ours.SurveyFinds.Select(DevDrop))}]");
            foreach (ModifierStatLabel row in DevNative.PanelStats(panel).FindChildren("*", "", true, false).OfType<ModifierStatLabel>())
                DevLog($"game | the panel's bonus '{DevWordsOf(row.StatName ?? "")}' = '{DevNative.RowValue(row)?.Text}' shown {row.IsVisibleInTree()}, its tooltip '{DevWordsOf(row.StatTooltipContent ?? "")}' " +
                    $"/ '{(DevNative.RowTooltip(row) is { } tip ? DevWordsOf(string.Join(" / ", DevNative.Content(tip) ?? [])) : "")}', bound to {DevStatName(DevNative.RowStat(row), selected.Stats)}");
            if (DevNative.Current(view) is { } current)
                DevLog($"game | the panel of the hunt in progress: shown {current.IsVisibleInTree()}, hunting shown {DevNative.CurrentShown(current).IsVisibleInTree()}, " +
                    $"its name '{DevNative.CurrentName(current).Text}', at {Native.Drawn(current)}; the panel of the animal selected at {Native.Drawn(panel)}");
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException or NullReferenceException or InvalidCastException)
        {
            DevLog($"game | the page's panel could not be read: {e.GetType().Name}: {e.Message}");
        }
    }

    static string DevStatName(GamesByMalcs.Melvor2.Stats.Nodes.ModifierStatNodes? nodes, HuntingActionStats stats) =>
        nodes == null ? "nothing" : ReferenceEquals(nodes, stats.SkipTrackingStepChance) ? "SkipTrackingStepChance"
        : ReferenceEquals(nodes, stats.TrappingReattemptChance) ? "TrappingReattemptChance" : ReferenceEquals(nodes, stats.DoublingChance) ? "DoublingChance"
        : ReferenceEquals(nodes, stats.PreservationChance) ? "PreservationChance" : "another stat";

    // Every label's words under a control, in order.
    static string DevLabels(Control control) => string.Join(" ", control.FindChildren("*", "Label", true, false).OfType<Label>().Where(l => l.IsVisibleInTree()).Select(l => l.Text));

    // Every label's words and every picture under a control, in order.
    static string DevTexts(Control control) => string.Join(", ", control.FindChildren("*", "", true, false).OfType<Control>().Where(c => c.IsVisibleInTree())
        .Select(c => c is Label l ? $"'{l.Text}'" : c is TextureRect t && t.Texture != null ? DevTexture(t.Texture) : "").Where(w => w.Length > 0));

    // Every card of the page's list against its line, and the page's order of locations and animals against the window's.
    void DevCards()
    {
        try
        {
            var lines = new Dictionary<ActiveHuntingAnimal, (AnimalRates Animal, Button Button, string Drawn, Texture2D? Picture)>(ReferenceEqualityComparer.Instance);
            foreach (var l in _list!.DevLines()) lines[l.Animal.Active] = l;
            foreach (HuntingAnimalTrackingView card in Native.Cards(_page!))
            {
                ActiveHuntingAnimal? animal = Native.AnimalOf(card);
                SettingsIcon? icon = Native.IconOf(card);
                var xp = Native.XpViewOf(card);
                string line = animal != null && lines.TryGetValue(animal, out var l) ? $"the line '{l.Animal.Name}' {DevTexture(l.Picture)} tint {l.Animal.PictureTint}" : "NO LINE";
                DevLog($"card | {animal?.Animal.Id ?? "no animal"} discovered {animal?.JournalEntry?.IsDiscovered} | the card's name '{Native.NameOf(card)?.Text}', picture " +
                    $"{DevTexture(icon?.Texture)} shown {icon?.IsVisibleInTree()} modulate {icon?.Modulate}/{icon?.SelfModulate}, spider icon {icon?.IsArachnophobiaModeIcon}, " +
                    $"tool '{DevNative.CardTool(card).Text}', trap '{DevNative.CardTrapType(card).Text}', XP '{(xp == null ? "-" : DevLabels(xp))}' multipliers " +
                    $"{(xp == null ? "-" : $"{R(Native.XpLow(xp))}..{R(Native.XpHigh(xp))}")}, unlocked part {DevNative.CardUnlocked(card).IsVisibleInTree()}, locked part " +
                    $"{DevNative.CardLocked(card).IsVisibleInTree()}, button pressed {DevNative.CardButton(card).ButtonPressed} | {line}");
            }
            var action = _hunting!.Action;
            string pageOrder = string.Join(", ", Native.Cards(_page!).Select(c => Native.AnimalOf(c)?.Animal.LocalId ?? "?"));
            string ourOrder = string.Join(", ", ReadAnimals().Where(a => ReferenceEquals(a.Active.Location, action.SelectedLocation)).Select(a => a.Animal.LocalId));
            DevLog($"order | the page's list of {action.SelectedLocation?.Location?.Id ?? "none"}: [{pageOrder}]; ours: [{ourOrder}]{(pageOrder == ourOrder ? "" : "  DIFFER")}");
            if (Native.TrackingView(_page!) is { } view && Native.LocationSelect(view) is { } select && DevNative.LocationButtons(select) is { } buttons)
            {
                var shown = buttons.Where(b => GodotObject.IsInstanceValid(b.Value) && b.Value.IsVisibleInTree()).OrderBy(b => b.Value.GetIndex()).Select(b => b.Key.LocalId).ToList();
                var listed = _hunting.Config.Locations.Where(l => action.Locations.TryGetValue(l, out var a) && AnimalRates.LocationListed(a)).Select(l => l.LocalId).ToList();
                DevLog($"order | the page's location buttons shown: [{string.Join(", ", shown)}]; ours: [{string.Join(", ", listed)}]{(shown.SequenceEqual(listed) ? "" : "  DIFFER")}");
            }
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException or NullReferenceException)
        {
            DevLog($"card | the page's list could not be read: {e.GetType().Name}: {e.Message}");
        }
    }

    // The page's own words for the rules the hour rests on: the constants of its views, by name.
    void DevWords()
    {
        foreach (Type type in new[] { typeof(HuntingActionStatsView), typeof(ModifierStatView), typeof(IntervalRangeDisplay), typeof(HuntingAnimalTrackingView),
                     typeof(HuntingSelectedRecipeContainer), typeof(HuntingCurrentRecipeContainer), typeof(HuntingAnimalSelectedTrapView), typeof(ActiveHuntingAnimal),
                     typeof(HuntingAnimalExperienceView), typeof(AnimalSpeciesView), typeof(AnimalStatEntryView), typeof(HuntingAnimalStatEntry),
                     typeof(HuntingActionComponent), typeof(HuntingLocationButton), typeof(HuntingSelectTrapView), typeof(HuntingUseBaitView), typeof(TraplessHuntingToggleView) })
        {
            try
            {
                var words = DevNative.Constants(type).ToList();
                DevLog($"words | {type.Name}: " + (words.Count == 0 ? "no words of its own" : string.Join(" | ", words.Select(w => $"{w.Name} '{DevWordsOf(w.Words)}'"))));
            }
            catch (Exception e) { DevLog($"words | {type.Name}: not read ({e.GetType().Name}: {e.Message})"); }
        }
        DevLog($"words | the words the list uses: no tool '{_noTool ?? "NOT READ"}', not discovered '{_hiddenWord ?? "NOT READ"}'");
    }

    // The game's spider setting, the picture it shows for a spider, and every spider's line.
    void DevSpiders()
    {
        bool? setting = null;
        try { setting = LocalSettingsManager.Settings?.Interface?.ArachnophobiaMode; } catch (Exception) { }
        var spiders = _list!.DevLines().Where(l => l.Animal.Animal.IsScarySpider).ToList();
        DevLog($"spiders | the game's setting {(setting is { } s ? s ? "on" : "off" : "NOT READ")}, the picture it shows for a spider {DevTexture(_spiderPicture)} | " +
            (spiders.Count == 0 ? "no spider is listed" : string.Join("; ", spiders.Select(l =>
                $"{l.Animal.Animal.Id}: the line shows {DevTexture(l.Picture)}{(setting != false && l.Picture != null && ReferenceEquals(l.Picture, l.Animal.Animal.Icon) ? "  A SPIDER IS SHOWN" : "")}"))));
    }

    void DevHidden()
    {
        var action = _hunting!.Action;
        var listed = new HashSet<ActiveHuntingAnimal>(ReadAnimals().Select(a => a.Active), ReferenceEqualityComparer.Instance);
        int hidden = 0;
        foreach (var (animal, active) in action.Animals)
        {
            if (active == null || listed.Contains(active)) continue;
            hidden++;
            DevLog($"hidden | {animal.Id} '{animal.DisplayName.Text}' at {active.Location?.Location?.Id} | the animal is shown when: {DevConditions(animal.DisplayRequirements)} | " +
                $"its location is shown when: {DevConditions(active.Location?.Location?.DisplayRequirements)}, only with an animal unlocked " +
                $"{active.Location?.Location?.OnlyShowWhenRecipesAvailable} ({active.Location?.UnlockedRecipeCount} unlocked) | to hunt it: {DevConditions(animal.Requirements)}");
        }
        DevLog($"hidden | {hidden} of the game's {action.Animals.Count} animals are not listed");
    }

    static string DevConditions(ConditionGroup? group) => group == null || group.Conditions == null || group.Conditions.Count == 0 ? "always"
        : $"{(group.AreMet ? "met" : "NOT MET")}: " + string.Join("; ", group.Conditions.Select(c => $"{(c.IsMet ? "met" : "not met")}{(c.IsOr ? " (or)" : "")} '{DevWordsOf(c.GetDescription(false, false))}'"));

    // Every animal not discovered yet: what the page's card draws, and what its line shows.
    void DevDiscovery()
    {
        var lines = _list!.DevLines().Where(l => !l.Animal.Discovered).ToList();
        DevLog($"discovery | {lines.Count} lines of animals not discovered yet" + (lines.Count == 0 ? "" : ": " + string.Join("; ", lines.Select(l =>
        {
            HuntingAnimalTrackingView? card = Native.Card(_page!, l.Animal.Active);
            string page = card == null ? "no card on the page now" : $"its card '{Native.NameOf(card)?.Text}' {DevTexture(Native.IconOf(card)?.Texture)} modulate {Native.IconOf(card)?.Modulate}";
            return $"{l.Animal.Animal.Id} (the game's name '{l.Animal.Animal.DisplayName.Text}'): the line '{l.Animal.Name}' {DevTexture(l.Picture)} tint {l.Animal.PictureTint}; {page}" +
                (card != null && Native.NameOf(card)?.Text != l.Animal.Name ? "  DIFFER" : "");
        }))));
    }

    void DevLit()
    {
        var action = _hunting!.Action;
        var lit = _list!.DevLines().Where(l => l.Animal.OnPage).Select(l => l.Animal.Animal.Id).ToList();
        bool listed = action.SelectedAnimal is { } s && _list.DevLines().Any(l => ReferenceEquals(l.Animal.Active, s));
        bool right = listed ? lit.Count == 1 && lit[0] == action.SelectedAnimal!.Animal.Id : lit.Count == 0;
        DevLog($"lit | the animal selected {action.SelectedAnimal?.Animal.Id ?? "none"}, the panel names '{DevPanelName()}', the animal hunted {action.CurrentAnimal?.Animal.Id ?? "none"} " +
            $"(hunting {DevHuntingNow()}) | the lit line{(lit.Count == 1 ? " is" : "s are")} {(lit.Count == 0 ? "none" : string.Join(", ", lit))}" +
            $" (drawn lit: {string.Join(", ", _list.DevLines().Where(l => l.Drawn.EndsWith("lit=True")).Select(l => l.Animal.Animal.LocalId))})" + (right ? "" : "  MISMATCH"));
    }

    string DevOrder(string when)
    {
        string order = string.Join(", ", _list!.DevLines().Select(l => l.Animal.Animal.LocalId));
        DevLog($"order | {when} | {order}");
        return order;
    }

    string DevInSight()
    {
        Rect2 sight = _list!.DevScroll.GetGlobalRect();
        return string.Join(", ", _list.DevLines().Where(l => l.Button.IsVisibleInTree() && l.Button.GetGlobalRect().Intersects(sight)).Select(l => l.Animal.Animal.LocalId));
    }

    // The window's place against the page: the panel of the animal selected, the location buttons, and every card.
    void DevRoom(string when)
    {
        string opened = _firstPlace is { } first ? $"opened at the first place {first} (nothing saved by the player)"
            : _settings is { X: { } x, Y: { } y } ? $"at the player's own place ({x:0}, {y:0}) from settings.json" : "in the middle (no first place could be measured)";
        string strip = _opener?.GetParent() is Control buttons ? Native.Drawn(buttons).ToString() : "not found";
        string parts = "the page's parts not found";
        try
        {
            if (Native.TrackingView(_page!) is { } view)
                parts = $"the page's view at {Native.Drawn(view)}; the panel of the animal selected " +
                    (Native.Panel(view) is { } panel ? $"at {Native.Drawn(panel)} shown {panel.IsVisibleInTree()}, {(Native.Covered(panel, Cover()) ? "UNDER THE WINDOW" : "clear of the window")}" : "NOT FOUND") +
                    "; the location buttons " + (Native.LocationSelect(view) is { } select ? $"at {Native.Drawn(select)}, {(Native.Covered(select, Cover()) ? "under the window" : "clear of the window")}" : "NOT FOUND") +
                    "; the list " + (Native.AnimalList(view) is { } list ? $"at {Native.Drawn(list)}, {(Native.ScrollOf(list) is { } s ? $"in a scrolling list at {Native.Drawn(s)}" : "in no scrolling list")}" : "NOT FOUND");
        }
        catch (Exception e) { parts = $"the page's parts not read: {e.Message}"; }
        DevLog($"room | {when} | the window at {Cover()?.ToString() ?? "none, not on the screen"} at {(_window is { IsAlive: true } w ? w.Style.Scale.ToString("0.##") : "-")}, {opened}; " +
            $"the strip of round buttons at {strip}; {parts}");
        var lines = _list!.DevLines().Select(l => l.Animal).ToList();
        DevLog($"room | {when} | " + string.Join("; ", lines.Where(a => a.Sight != null || a.LocationOnPage).Select(a =>
            $"{a.Animal.LocalId}: {(Native.Card(_page!, a.Active) is { } card ? DevCardSight(card, false) : "no card")}, clickable {a.Clickable}" +
            (a.Sight is CardSight.UnderWindow or CardSight.NoRoom && !DevLineOf(a).TooltipText.Contains("has no room") && !a.Clickable ? "  NOT SAID in its tooltip" : ""))));
        DevLog($"room | {when} | of the {lines.Count(a => a.Sight != null)} cards the page shows, {lines.Count(a => a.Sight is CardSight.Clear or CardSight.Scroll)} can be seen whole beside " +
            $"the window; {lines.Count(a => a.Clickable)} of the {lines.Count} lines are clickable; the panel {(lines.Any(a => a.PanelCovered) ? "UNDER THE WINDOW" : "clear")}");
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
    // (mods/CombatSim/CutProbe.cs): whole, CUT (a name cut by ClipText is CUT too), SHORTENED WITH ..., PAST THE EDGE
    // (Woodcutting Rates' DevCut, unchanged).
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

    // ---- through the game's input, as a player's hand would (Woodcutting Rates' Dev.cs, unchanged) ----------------------

    Vector2I _devPointer;
    bool _devPointerSeen, _devHandSinceStep;
    ulong _devHandAt, _devGuardSince, _devTipSince;
    Control? _devAim;
    Vector2 _devAimAt;
    int _devMisses;
    const int MostMisses = 10;

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
        _devFocusAfter = (target, (long)Engine.GetFramesDrawn());
        DevLog($"pressed {what} at {_devAimAt}, the pointer on {DevName(over)}");
        return true;
    }

    // The part of a control that can be seen: on the screen, inside the window's list when it is a line of it, inside the
    // page's scrolling list when it is in there, and, for a control of the game, beside the mod's own window.
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

    bool DevUnderWindow(Control control) => _window is { IsAlive: true, Visible: true } window && control != window.Root &&
        !window.Root.IsAncestorOf(control) && control.GetGlobalRect().Intersects(window.Root.GetGlobalRect());

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
            if (!DevFramesDrawn()) return false;
            DevLog($"tooltip | {shot} | {tooltip}");
            Shot(shot);
            if (before != null && DevTooltipRect() is { } at)
            {
                using var now = _ctx.Tree.Root.GetTexture().GetImage();
                DevLog($"shot-hunting-{shot}.png | " + DevChanged(before, now, at, "the tooltip's place", "VISIBLE", "NOT VISIBLE"));
            }
            DevPointerBack();
            return true;
        }
        if (Time.GetTicksMsec() - _devTipSince < TipWait) return false;
        DevLog($"tooltip | {shot} | NOT SHOWN in 20 s with the pointer on {DevName(over)}; its words are in the 'row' and 'tips' lines, and there is no picture of it");
        DevPointerBack();
        return true;
    }

    string DevProofBeside(Image before, Control control, string what)
    {
        Rect2 whole = control.GetGlobalRect(), seen = DevSight(control);
        double under = whole.Area > 0 ? 1 - seen.Area / whole.Area : 0;
        string share = $"{what} at {whole}, {under:P0} of it under the mod's window or out of sight";
        if (!seen.HasArea()) return $"NOT PROVED: {share}";
        using var now = _ctx.Tree.Root.GetTexture().GetImage();
        return $"{share}; " + DevChanged(before, now, seen, what + " beside the window", "VISIBLE", "NOT VISIBLE");
    }

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

    void DevPointerBack()
    {
        Vector2 real = DisplayServer.MouseGetPosition() - DisplayServer.WindowGetPosition();
        Input.ParseInputEvent(new InputEventMouseMotion { Position = real, GlobalPosition = real });
    }

    string HandNote => _devHandSinceStep ? "; A HAND WAS AT THE MOUSE since the step before: this picture may show what it did" : "";

    // ---- pictures, and what their pixels prove ------------------------------------------------------------------------

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
            var frame = _ctx.Tree.Root.GetFinalTransform() * window.Root.GetGlobalRect().Grow(10);
            var part = ((Rect2I)frame).Intersection(new Rect2I(0, 0, image.GetWidth(), image.GetHeight()));
            if (!part.HasArea()) throw new InvalidOperationException("The window is outside the picture.");
            string proof = _devWithout == null ? " | NOT PROVED: no picture of the screen without the window"
                : " | " + DevChanged(_devWithout, image, window.Root.GetGlobalRect(), "the window", "VISIBLE", "NOT VISIBLE");
            Save(image, $"shot-hunting-{state}.png");
            using var region = image.GetRegion(part);
            Save(region, $"crop-hunting-{state}.png", proof);
            return;
        }
        Save(image, $"shot-hunting-{state}.png");
    }

    string DevChanged(Image before, Image after, Rect2 place, string what, string yes, string no) =>
        DevChangedPixels(before, after, _ctx.Tree.Root.GetFinalTransform() * place, what, yes, no);

    static string DevChangedPixels(Image before, Image after, Rect2 screen, string what, string yes, string no)
    {
        if (before.GetSize() != after.GetSize()) return $"{no}: the two pictures differ in size ({before.GetSize()} and {after.GetSize()}), so they cannot be compared";
        Rect2I at = ((Rect2I)screen).Intersection(new Rect2I(0, 0, after.GetWidth(), after.GetHeight()));
        if (!at.HasArea()) return $"{no}: {what} is outside the picture";
        long changed = 0, all = 0;
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

    void ShotStrip()
    {
        if (_opener?.GetParent() is not Control strip || !_opener.IsVisibleInTree()) throw new InvalidOperationException("The round button is not on the screen.");
        using var image = _ctx.Tree.Root.GetTexture().GetImage();
        var frame = _ctx.Tree.Root.GetFinalTransform() * strip.GetGlobalRect().Grow(12);
        var part = ((Rect2I)frame).Intersection(new Rect2I(0, 0, image.GetWidth(), image.GetHeight()));
        if (!part.HasArea()) throw new InvalidOperationException("The strip is outside the picture.");
        using var region = image.GetRegion(part);
        Error result = region.SavePng(Path.Combine(_ctx.Directory, "crop-hunting-strip.png"));
        if (result != Error.Ok) throw new IOException("crop-hunting-strip.png: " + result);
        DevLog($"crop-hunting-strip.png | the button at {_opener.GetGlobalRect()}, lit={_opener.ButtonPressed}, {strip.GetChildCount()} buttons in the strip{HandNote}");
    }
}
#endif
