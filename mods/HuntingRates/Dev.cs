#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.CustomControls;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Hunting;
using GamesByMalcs.Melvor2.GameMenus;
using GamesByMalcs.Melvor2.GameMenus.Pages;
using GamesByMalcs.Melvor2.InfoLabels;
using GamesByMalcs.Melvor2.Settings;
using GamesByMalcs.Melvor2.StatDisplays;
using GamesByMalcs.Melvor2.Utils;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace HuntingRates;

// Development: a file "hunting.request" in the mod's folder runs the light check below with nobody at the keyboard, one
// step about every half second, and writes "hunting.done" when it ends: "ok" or "failed at <step>: <why>", and in both
// cases what was put back. Every step logs what it read as one-line "Dev: ..." entries in MelvorModLoader.log. It is
// short (about a minute and a half when nobody moves the mouse): no simulation, a few pictures. A second request,
// "huntingwatch.request", only listens while the character hunts (DevWatch.cs); one waits for the other.
//   pwsh .claude\codex\check-mod.ps1 -Worktree <worktree> -Mod HuntingRates -Request hunting.request -Produces hunting.done -Out <folder>
// The request file may say "fail=<step>": that step then fails on purpose, to show that a failed request puts
// everything back; "fail=click" fails the step right after the click, so that the put-back of what the click changed is
// shown (code review 1). Built on Woodcutting Rates' request (mods/WoodcuttingRates/Dev.cs) as merged; the page's parts it
// changes, who owns each, how each is put back and the hunt guard are in DevPage.cs, the pointer on the page's parts in
// DevAims.cs.
// Lesson 56: nothing is selected while a hunt runs. "A hunt runs" (AnimalRates.IsHunting, the window's own test) is read again right
// before every press that selects and every put-back of a selection; while one runs, those are not made and are said
// (NOT PUT BACK for what the request owned). The check is meant to run while the character is not hunting; run while he
// hunts, it only proves that a click on a line selects nothing and leaves the hunt alone.
// What it presses: the mod's own controls; the Hunting page's own journal button, location buttons and cards' buttons,
// with the pointer as a player would (DevPress: the pointer is put on the control, a later step presses only when the game
// says the pointer is on it; nothing while somebody is at the mouse, waiting until the real pointer has been still for
// 5 s, and ending the request after 90 s, which puts everything back). The put-backs press the same controls by their own
// signal or event, as the window's click does. The buttons that start or stop a hunt and the trap, bait and trapless
// controls are never named or pressed; every animal's trap, bait and trapless choice is watched throughout (CHOICES
// CHANGED). The game's methods it calls besides the window's own: GameView.SetCurrentPage (to open the Hunting page) and
// the read-only ones the window calls. The pictures go into the mod's folder (shot-hunting-*.png, crop-hunting-*.png),
// each two drawn frames after what it shows, with its pixels read back (DevChanged).
//  1. Remembers the page on the screen, whether the window was open and folded, the round button, settings.json as it
//     is on disk, the page's location and animal selected, whether it shows the journal, the scroll of its list of
//     animals, the hunt in progress (IsStarted, CurrentAnimal, its State, CurrentStep, RequiredSteps, Tracks,
//     TrapFailCount, the trap in use), every animal's trap, bait and trapless choice; and starts the hunt guard.
//  2. Opens the Hunting page the way the sidebar does, if another page is open, and reads back that it is shown; stops
//     with one line when it cannot be opened that way. "Dev: strip". Closes the window if it was open.
//  3. "Dev: card stats": rests the pointer on every part of an animal's card and of the page's panel of the animal
//     selected that the game shows a tooltip for (DevAims.cs), and logs each tooltip word for word, with its numbers
//     named; shot-hunting-panel-tooltip.png.
//  4. A picture of the screen without the window; presses the round button, and opens the window up with its own button
//     if it opened folded; the round button must be lit.
//  5. "Dev: row" (raw, counted, the time of a catch by every reading still open, drawn, tooltip), "Dev: game" (the
//     panel's labels beside ours, its surveying range against the most surveys the window counts, its bonus rows, and the
//     animal's trap, bait, trapless, journal and fail bonuses beside its stats), "Dev: card" (every card: name, picture,
//     tool as drawn with its colour, its button), "order", "words", "spiders", "hidden", "discovery", "lit", "tips",
//     "heads" (every name's whole tip), "wrap" (every tooltip line longer than Fishing Rates' longest, WRAPPED), "size",
//     "room". "Dev: cut" over every label of the picture; crop-hunting-window.png and, when the list scrolls,
//     crop-hunting-window-end.png.
//  6. The tooltip of a line whose hour is counted, when there is one (shot-hunting-tooltip.png), and the tooltip of the
//     name with the longest tip (shot-hunting-header-tooltip.png). Then a click on "XP": "Dev: order" and
//     crop-hunting-sorted.png.
//  7. "Dev: journal": presses the page's own journal button with the pointer (shot-hunting-journal.png), so that the click
//     after has to show the hunt again by the page's own button.
//  8. "Dev: click": a click on a line. While no hunt runs: a line of an animal at another location than the page's, which
//     the click opens and selects through the page's own controls; read back: the location, the animal, the view and both
//     view buttons as drawn, the panel's name, the lit line, the window, the game's events during the click itself, and
//     the hunt guard over the click's window. NOT OPENED, THE HUNT IS NOT SHOWN, THE PAGE SHOWS ANOTHER ANIMAL, THE
//     WINDOW CLOSED and the guard's marks are what must not happen. While a hunt runs: a line of another location, which
//     must select nothing (THE CLICK SELECTED WHILE A HUNT RUNS). shot-hunting-clicked.png. Puts the page back, then,
//     while no hunt runs and the player has changed nothing, presses the page's own location button and the card's
//     button for the same animal with the pointer, compares (DIFFER, EVENTS DIFFER), and puts the page back again.
//  9. "Dev: size": the window at its smallest and at its largest size (crop-hunting-smallest.png,
//     crop-hunting-largest.png), and "Dev: cut" at both. Closes the window with its X: shot-hunting-closed.png.
// 10. Puts back settings.json byte for byte from the copy read in step 1, the window as it was, and the page's parts, each
//     by the rule of DevPage.cs, each in its own try: the view by the page's own view button, judged once settled; the
//     location by its own button and the animal by its card's own button, only while no hunt runs; the scroll.
// 11. Reads it all back, then puts back the page that was on the screen (only if the request opened Hunting itself) and
//     the pointer, and says in its last line what was put back, NOT AS IT WAS for anything that is not, NOT PUT BACK for
//     what lesson 56 kept it from putting back, NOT READ BACK for what could not be read. A change of the hunt or of a
//     choice is the request's fault only when the guard saw it in the window of one of its presses; otherwise it is said
//     as the player's or the game's.
// A step that fails jumps to 10. If the mod stops while a request runs, or another character is opened, the done file says
// so and settings.json is written back from the copy (DevStopped, DevReset).
public partial class HuntingRatesMod
{
    const string RequestFile = "hunting.request", DoneFile = "hunting.done";
    // Woodcutting Rates' times: how long the real pointer must be still before a step points or presses, and how long a
    // step waits for that before the request ends.
    const ulong StillFor = 5000, GiveUpAfter = 90000, StepTime = 6000, PointerStepTime = GiveUpAfter + 10000;
    const ulong TipWait = 20000;
    // The comparison's card press, once the request's location stands on his page: given up after this long without it.
    const ulong CardPressMost = 5000;
    // Fishing Rates' longest tip of a column's name, line by line, and the longest line of its tooltips, in characters.
    const int FishingTipMost = 82, FishingTooltipMost = 94;

    readonly List<(string Name, Func<bool> Run, ulong Timeout)> _devSteps = new();
    int _devAt, _devPutBackAt;
    ulong _devStarted;
    long _devFrameMark;
    string? _devFailure, _devFailOnPurpose, _devPutBackWords;
    bool _devPutBackOk, _devPutBackRead, _devOpenedPage;
    // Put-backs on the way whose line says NOT AS IT WAS or NOT PUT BACK, and presses not made: the done line carries them.
    readonly List<string> _devOnTheWay = new();
    Image? _devWithout;
    string? _devSettingsCopy;
    bool _devSettingsRead;
    // True while a click on a line runs its presses (ShowOnPage, ClickStepNow): the game's events raised then are the click's.
    bool _devInClick;

    // What the request found, to be put back as it was.
    sealed record DevBefore(GamePage? Page, bool Wanted, bool Shown, bool? Folded, bool? Lit, string? Settings, DevPageWas Hunting);

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
        try { DevGuardTick(); }
        catch (Exception e) { DevLog($"hunt | the guard could not be read: {e.GetType().Name}: {e.Message}"); }
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
        DevHuntGuard.DisposeCurrent();
        _devGuard = null;
        _devAnimalBackFrame = -1;
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
        _devAnimalUnder = _devScrollUnder = null;
        _devViewDrawn = null;
        _devSettle = null;
        _devSettledTick = -1;
        _devOrigin = _devOriginWait = null;
        _devOnTheWay.Clear();
        _devHis.Clear();
        _devFocusAfter = null;
        _devPressFrame = -1;
        _devAnimalBackFrame = -1;
        _devAnimalBackWords = "";
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
        _devGuard = new DevHuntGuard(action, DevLog);
        void Add(string name, Func<bool> run, ulong timeout = StepTime) => _devSteps.Add((name, run, timeout));
        DevLog($"before: page {before.Page?.Id ?? "none"}, window {(before.Wanted ? "open" : "closed")}{(before.Shown ? " and on the screen" : "")}" +
            $"{(before.Folded == true ? ", folded" : "")}, round button {(before.Lit is { } lit ? lit ? "lit" : "unlit" : "not made yet")}, settings.json " +
            (before.Settings == null ? "not there" : $"{before.Settings.Length} characters: {before.Settings}") +
            $" | {huntAtStart} | the page: {DevPageWords(before.Hunting)} | the choices: {choicesAtStart} | the real pointer at {DisplayServer.MouseGetPosition()}" +
            $" | {DevHuntRunsWords()}" + (DevHuntRuns ? " | A HUNT RUNS: nothing will be selected, and the click only proves that it selects nothing" : ""));

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
        // The game's own words on the parts of a card and of the panel, before the window is open over the page.
        List<DevAim>? aims = null;
        int aimAt = 0;
        ulong aimsSince = 0;
        Add("the parts' own words", () =>
        {
            if (aims == null)
            {
                if (!DevFramesDrawn()) return false;
                aims = DevAims(out string why);
                if (why.Length > 0) DevLog($"card stats | {why}");
                DevLog($"card stats | {aims.Count} parts to rest the pointer on, {AimWait / 1000} s at most each and {AimsMost / 1000} s in all");
                aimAt = 0;
                aimsSince = Time.GetTicksMsec();
                _devTipSince = 0;
                _devFrameMark = 0;
            }
            while (aimAt < aims.Count)
            {
                // About a minute in all, whatever the tooltips do: the rest is said and left.
                if (Time.GetTicksMsec() - aimsSince > AimsMost)
                {
                    DevLog($"card stats | the step's {AimsMost / 1000} s are up: not pointed at [{string.Join(", ", aims.Skip(aimAt).Select(a => a.What))}]");
                    foreach (DevAim left in aims.Skip(aimAt)) left.Before?.Dispose();
                    break;
                }
                if (!DevAimStep(aims[aimAt])) return false;
                aims[aimAt].Before?.Dispose();
                aims[aimAt].Before = null;
                aimAt++;
                _devTipSince = 0;
                _devFrameMark = 0;
            }
            DevPointerBack();
            return true;
        }, GiveUpAfter + AimsMost + 10000);
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
            // A line with its hour counted, so the picture shows the hour's words; else the first one that can be hunted.
            target = (lines.FirstOrDefault(l => !l.Animal.Locked && l.Animal.Discovered && l.Animal.SecondsPerCatch != null).Button
                ?? lines.FirstOrDefault(l => !l.Animal.Locked && l.Animal.Discovered).Button ?? lines.FirstOrDefault().Button)
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

        // The journal by the page's own button, then a click on a line, then (while no hunt runs) the page's own location
        // and card buttons for the same animal, to compare. The page is put back after each, by the rule of DevPage.cs.
        AnimalRates? clicked = null;
        DevClickEvents? events = null;
        Image? beforeClick = null;
        Button? journalButton = null;
        string huntBefore = "", choicesBefore = "", ours = "", ourEvents = "";
        bool huntingAtClick = false, compareSkipped = false;
        bool Waiting() => DevSettling || _devSettledTick >= _devTick;

        // The put-back of the page's parts in steps, each part on its own and in its own try, each step after the page has
        // settled from the one before: the location, then the animal, whose card is laid out only once the location is
        // shown, then the scroll, and the view last, so that the location buttons and the cards, which are part of the
        // hunt's view, are still on the screen for the selection's put-back (code review 2, fault 2); then one line that
        // says all of it. Whether it runs is decided at its first step: skip() is read once, so that a put-back that has
        // begun ends.
        void AddPageBack(string name, Func<bool> skip, string head)
        {
            string words = "";
            bool run = false;
            void Put(string part, Func<string> put)
            {
                try
                {
                    string said = put();
                    if (said.Length > 0) words = DevJoin(words, said);
                }
                catch (Exception e) { words = DevJoin(words, $"{part} NOT AS IT WAS: its put-back failed ({e.GetType().Name}: {e.Message})"); }
            }
            void His() => Put("what the player changed", () => string.Join(", ", DevHisNow().Select(p => $"{p} is the player's, and is left")));
            Add($"{name}: the location", () =>
            {
                if (Waiting()) return false;
                run = !skip();
                if (!run) return true;
                words = "";
                His();
                Put("the location", DevLocationBack);
                return true;
            });
            Add($"{name}: the animal", () =>
            {
                if (!run) return true;
                // The animal is put back two process frames after its location (DevAnimalBackSoon), not on this tick: its
                // words are taken here, and anything it could not do is tried again now.
                if (_devAnimalBackFrame >= 0 || Waiting()) return false;
                // His changes read again right before a press of the step's own: it never presses his old animal's card
                // over one he has just picked (the code review of turn 4, fault 2).
                His();
                Put("the animal", () =>
                {
                    string soon = _devAnimalBackWords;
                    _devAnimalBackWords = "";
                    return DevJoin(soon, DevAnimalBack());
                });
                return true;
            });
            Add($"{name}: the scroll", () =>
            {
                if (!run) return true;
                if (Waiting()) return false;
                Put("the scroll", DevScrollBack);
                return true;
            });
            Add($"{name}: the view", () =>
            {
                if (!run) return true;
                if (Waiting()) return false;
                His();
                Put("the view", DevViewBack);
                return true;
            });
            Add($"{name}: the view read back", () =>
            {
                if (!run) return true;
                if (Waiting()) return false;
                Put("the view", DevViewBackRead);
                DevLog($"{head} {(words.Length > 0 ? words : "nothing of the request's was left on the page")}; now {DevPageWords(DevPageNow())}");
                if (words.Contains("NOT AS IT WAS") || words.Contains("NOT PUT BACK")) _devOnTheWay.Add($"{head} {words}");
                words = "";
                run = false;
                return true;
            });
        }

        Add("the journal by its own button", () =>
        {
            // The page's own view: when it shows the hunt, its journal button is pressed with the pointer, so that the click
            // after has to show the hunt again by the page's own button (code review 1, fault 5). This only shows a view of
            // the page, and is made while a hunt runs too.
            if (journalButton == null)
            {
                if (DevJournalNow()) { DevLog("journal | the page shows its journal already: the click starts from it"); return true; }
                journalButton = Native.TrackingView(_page!) is { } tv ? Native.JournalButton(tv) : null;
                if (journalButton == null || !CanPress(journalButton) || !DevSight(journalButton).HasArea())
                {
                    DevLog("journal | the page's own journal button cannot be pressed (not found, disabled, or under the mod's window): the click starts from the hunt");
                    journalButton = null;
                    return true;
                }
            }
            Button button = journalButton;
            bool? pressed = DevPagePress(() => DevGamePress(button, "the page's own journal button"), before.Hunting, "the press of the page's own journal button");
            if (pressed != null) return pressed.Value;
            journalButton = null;
            return true;
        }, PointerStepTime);
        Add("the journal shown", () =>
        {
            if (journalButton == null) return true;
            if (Waiting() || !DevFramesDrawn()) return false;
            bool shown = DevJournalNow();
            DevLog($"journal | after the page's own journal button: the {(shown ? "journal" : "hunt")} shown{(shown ? "" : "  THE JOURNAL IS NOT SHOWN")}, {DevViewButtons()}");
            Refresh();
            Shot("journal");
            return true;
        });
        Add("a line to click", () =>
        {
            var action = _hunting!.Action;
            huntingAtClick = DevHuntRuns;
            ActiveHuntingLocation? location = action.SelectedLocation;
            var lines = _list!.DevLines().Select(l => l.Animal).Where(a => a.Click == AnimalRates.Opens.Animal).ToList();
            // No hunt: a clickable line of an animal at another location first, which the click opens and selects. A hunt:
            // a line at another location, which the click must leave alone (lesson 56).
            clicked = huntingAtClick
                ? lines.FirstOrDefault(a => !ReferenceEquals(a.Active.Location, location)) ?? lines.FirstOrDefault()
                : lines.Where(a => a.Clickable).OrderBy(a => ReferenceEquals(a.Active.Location, location) ? 1 : 0).FirstOrDefault();
            if (clicked == null) { DevLog("click | no line to click: none is clicked."); return true; }
            _list.DevScroll.EnsureControlVisible(DevLineOf(clicked));
            huntBefore = DevHunt();
            choicesBefore = DevChoices();
            DevLog($"click | the line of {clicked.Animal.Id} at {clicked.Active.Location?.Location?.Id}, clickable {clicked.Clickable}" +
                (huntingAtClick ? ", A HUNT RUNS: the click must select nothing" : "") + $"; before: {DevSelection(clicked)}; {huntBefore}");
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
            // "A hunt runs" is read at the press itself, not when the line was chosen: the press waits for his hand, and he
            // may start a hunt meanwhile (code review 2).
            bool? pressed = DevPagePress(() =>
            {
                huntingAtClick = DevHuntRuns;
                return DevClickLine(line);
            }, before.Hunting, $"the click on the line of {line.Animal.Id}");
            if (pressed != null) return pressed.Value;
            events.Dispose();
            events = null;
            clicked = null;
            return true;
        }, PointerStepTime);
        Add("the animal shown", () =>
        {
            if (clicked == null) return true;
            if (Waiting() || !DevFramesDrawn()) return false;
            var action = _hunting!.Action;
            Refresh();
            string hunt = DevHunt(), choices = DevChoices();
            ours = DevSelection(clicked);
            ourEvents = string.Join(", ", events!.Seen);
            DevPageWas start = _devOrigin ?? before.Hunting;
            bool opened = ReferenceEquals(action.SelectedLocation, clicked.Active.Location) && ReferenceEquals(action.SelectedAnimal, clicked.Active);
            bool moved = !ReferenceEquals(action.SelectedLocation, start.Location) || !ReferenceEquals(action.SelectedAnimal, start.Animal);
            string panelName = DevPanelName();
            var lit = _list!.DevLines().Where(l => l.Animal.OnPage).Select(l => l.Animal.Animal.Id).ToList();
            var litMeant = action.SelectedAnimal is { } s && _list.DevLines().Any(l => ReferenceEquals(l.Animal.Active, s)) ? new[] { s.Animal.Id } : Array.Empty<string>();
            HuntingAnimalTrackingView? card = Native.Card(_page!, clicked.Active);
            string marks = (huntingAtClick ? moved ? "  THE CLICK SELECTED WHILE A HUNT RUNS" : "" : opened ? "" : "  NOT OPENED") +
                (DevJournalNow() ? "  THE HUNT IS NOT SHOWN" : "") +
                (opened && clicked.Discovered && panelName.Length > 0 && !string.Equals(panelName, clicked.Animal.DisplayName.Text, StringComparison.OrdinalIgnoreCase) ? "  THE PAGE SHOWS ANOTHER ANIMAL" : "") +
                (choices == choicesBefore ? "" : $"  CHOICES CHANGED ({choices})") + (lit.SequenceEqual(litMeant) ? "" : $"  THE LIT LINE IS [{string.Join(", ", lit)}]") +
                (IsOpen ? "" : "  THE WINDOW CLOSED");
            DevLog($"click | after the click on the line of {clicked.Animal.Id}: {ours}; {(card == null ? "no card in the page's list" : DevCardSight(card, true))}; the game's events " +
                $"[{ourEvents}], during the click's own presses [{string.Join(", ", events.During)}]; {hunt} (before: {DevHuntKey(huntBefore)}; what the hunt did in the " +
                $"click's window is the next 'hunt' line){marks}");
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
        // Whenever the request owns a part of the page, the journal its own step opened too when no line could be clicked
        // (the third code review: a view the request left is put back as a selection is).
        AddPageBack("the page back after the click", () => !DevOwnsAny, "click | the page put back for the page's own buttons:");
        // The page's own buttons, pressed with the pointer as a player would: the location's button, then the card's button.
        // Only while no hunt runs, read again before each, and only while the player has changed nothing of the page
        // (Woodcutting Rates' code review 4: the comparison is a development check, never made over a page he uses).
        Control? locationButton = null;
        Button? cardButton = null;
        bool scrolledForCard = false;
        ulong cardStepSince = 0;
        bool CompareOff(string why)
        {
            compareSkipped = true;
            events?.Dispose();
            events = null;
            DevLog($"click | the page's own buttons are not pressed: {why}");
            return true;
        }
        // Once the comparison's location press is made, the request's location stands on his page until it is put back.
        // From then on nothing waits for his hand, and every way out of the comparison gives it up and puts the location
        // back on that very tick, by the page's own location button's event, which needs no free mouse; the animal, the
        // scroll and the view follow in the steps right after ("the page back after its own buttons", which runs whenever
        // the request owns a part). Firemaking Rates' GiveUpComparison (its code review 2, fault 1; this job's code review
        // 2, fault 1). A selection a running hunt keeps from being put back is NOT PUT BACK (lesson 56).
        bool GiveUpComparison(string why)
        {
            compareSkipped = true;
            if (_devAim != null) { _devAim = null; DevPointerBack(); }
            events?.Dispose();
            events = null;
            if (!DevOwnsAny)
            {
                DevLog($"click | {why}; nothing of the request's is on the page");
                return true;
            }
            string back;
            try
            {
                string his = string.Join(", ", DevHisNow().Select(p => $"{p} is the player's, and is left"));
                back = DevJoin(his, DevLocationBack());
            }
            catch (Exception e) { back = $"the location NOT AS IT WAS: its put-back failed ({e.GetType().Name}: {e.Message})"; }
            DevLog($"click | {why}; the comparison is given up, and the location put back at once: {(back.Length > 0 ? back : "nothing to put back of it")}");
            if (back.Contains("NOT AS IT WAS") || back.Contains("NOT PUT BACK")) _devOnTheWay.Add("click | the comparison given up: " + back);
            return true;
        }
        Add("the page's own location button", () =>
        {
            if (clicked == null || compareSkipped) return true;
            if (Waiting()) return false;
            if (huntingAtClick || DevHuntRuns) return CompareOff("a hunt runs, and nothing is selected while one runs (lesson 56)");
            if (_devHis.Count > 0) return CompareOff($"the player changed the page meanwhile ({string.Join(", ", _devHis)})");
            // The click's put-back left a part of the request's on the page: it is put back first (the step after), and no
            // comparison is made over it.
            if (DevOwnsAny) return CompareOff("a part of the request's is still on the page after the click's put-back");
            var action = _hunting!.Action;
            if (ReferenceEquals(action.SelectedLocation, clicked.Active.Location)) { DevLog("click | the page shows the animal's location already: its button is not pressed"); return true; }
            if (locationButton == null)
            {
                locationButton = Native.LocationButton(_page!, clicked.Active.Location?.Location);
                if (locationButton == null || !CanPress(locationButton)) return CompareOff($"the page's own location button of {clicked.Active.Location?.Location?.Id} cannot be pressed");
                if (!DevSight(locationButton).HasArea()) return CompareOff($"the page's own location button of {clicked.Active.Location?.Location?.Id} lies under the mod's own window");
            }
            events ??= new DevClickEvents(action, DevInGamePress, DevLog);
            Control button = locationButton;
            bool? pressed = DevPagePress(() => DevGamePress(button, $"the page's own location button of {clicked.Active.Location?.Location?.Id}"),
                before.Hunting, "the press of the page's own location button, to compare");
            if (pressed != null) return pressed.Value;
            return CompareOff("the player changed the page while the press waited for the mouse");
        }, PointerStepTime);
        Add("the page's own animal button", () =>
        {
            if (clicked == null || compareSkipped) return true;
            if (Waiting() || !DevFramesDrawn()) return false;
            var action = _hunting!.Action;
            ActiveHuntingAnimal animal = clicked.Active;
            // The request's location stands on his page once its location press was made: nothing waits on his hand.
            bool stands = DevOwnsAny;
            if (cardStepSince == 0) cardStepSince = Time.GetTicksMsec();
            // Lesson 56, read again on every tick of this step, the tick of the press too.
            if (DevHuntRuns) return GiveUpComparison("a hunt runs now, and nothing is selected while one runs (lesson 56)");
            if (cardButton == null)
            {
                DevLog($"click | after the page's own location button: {DevSelection(clicked)}");
                if (ReferenceEquals(action.SelectedAnimal, animal)) { DevLog("click | the location's button selected the animal already: its card's button is not pressed"); return true; }
                if (!ReferenceEquals(action.SelectedLocation, animal.Location))
                    return GiveUpComparison($"the page shows {action.SelectedLocation?.Location?.Id ?? "no location"}, not {animal.Location?.Location?.Id}: the card's own button is not there to press");
            }
            HuntingAnimalTrackingView? card = Native.Card(_page!, animal);
            cardButton = card != null ? Native.CardButton(card) : null;
            if (card == null || cardButton == null) return GiveUpComparison($"the page's list has no card or card button of {animal.Animal.Id}");
            if (_devAim != cardButton)
            {
                if (!CanPress(cardButton)) return GiveUpComparison($"the card's button of {animal.Animal.Id} is {(cardButton.Disabled ? "disabled" : "not shown")}");
                if (!DevSight(cardButton).HasArea())
                {
                    // Out of sight beside the window: the page's list is scrolled first, as the window's click scrolls it
                    // (ScrollToCard), only while the scroll is still what the request left (its own); what that scroll leaves
                    // is the request's own, read again once the page settled.
                    if (!scrolledForCard && _devOwnScroll is { } own && DevScrollNow() == own.Value && Native.ScrollOf(card) is { } list &&
                        Native.Place(card, Cover(), out int to) == CardSight.Scroll)
                    {
                        scrolledForCard = true;
                        list.ScrollVertical = to;
                        _devSettle = ((long)Engine.GetProcessFrames(), Time.GetTicksMsec(), false);
                        DevLog($"click | the card of {animal.Animal.Id} is out of sight beside the mod's window: the page's list, the request's own scroll, scrolled to {to} first");
                        return false;
                    }
                    return GiveUpComparison($"the card's button of {animal.Animal.Id} is not in sight beside the mod's window, and no scroll of the request's brings it");
                }
            }
            if (stands && DevHandNow())
                return GiveUpComparison($"somebody is at the mouse: the card's button of {animal.Animal.Id} is not pressed, so that the request's location does not wait on his page");
            if (stands && Time.GetTicksMsec() - cardStepSince > CardPressMost)
                return GiveUpComparison($"the card's button of {animal.Animal.Id} could not be pressed within {CardPressMost / 1000} s (the pointer was not on it)");
            events ??= new DevClickEvents(action, DevInGamePress, DevLog);
            Button button = cardButton;
            bool? pressed = DevPagePress(() => DevGamePress(button, $"the card's button of {animal.Animal.Id}"), before.Hunting, "the press of the card's own button, to compare");
            if (pressed != null) return pressed.Value;
            return GiveUpComparison("the press of the card's own button was not made");
        }, PointerStepTime);
        Add("the page's buttons compared", () =>
        {
            if (clicked == null || compareSkipped) return true;
            if (Waiting() || !DevFramesDrawn()) return false;
            string game = DevSelection(clicked), gameEvents = string.Join(", ", events?.Seen ?? new List<string>()), hunt = DevHunt(), choices = DevChoices();
            DevLog($"click | after the page's own buttons: {game}; the game's events [{gameEvents}], in the frames of the presses [{string.Join(", ", events?.During ?? new List<string>())}]; {hunt}" +
                (choices == choicesBefore ? "" : $"  CHOICES CHANGED ({choices})") + " | " +
                (game == ours ? "the page as after the click on the line" : $"DIFFER from the click on the line ({ours})") + "; " +
                (DevSameEvents(gameEvents, ourEvents) ? "the same events" : $"EVENTS DIFFER from the click on the line ([{ourEvents}])"));
            events?.Dispose();
            events = null;
            return true;
        });
        // Whenever the request owns a part of the page after the comparison, finished or given up (code review 2, fault 1).
        AddPageBack("the page back after its own buttons", () => clicked == null || !DevOwnsAny, "click | the page put back:");
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
        void Part(string part, Func<string> put)
        {
            try
            {
                string words = put();
                DevLog($"put back | {part}: {words}");
                if (words.Contains("NOT AS IT WAS")) _devOnTheWay.Add($"{part}: {words}");
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
        AddPageBack("put back the page", () => false, "put back | the page:");
        Add("read back the Hunting page", () =>
        {
            if (Waiting()) return false;
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
            said.Add(DevViewButtons());
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
            // The hunt and the choices: the request's fault only when the guard saw a change in the window of one of its
            // presses; otherwise a change is the player's or the game's (a trap swapped when one ran out), said as such.
            _devGuard?.Poll(DevNative.ShouldReset(_hunting!.Action));
            if (_devGuard is { IsOpen: true }) DevLog($"hunt | {_devGuard.Close()}");
            var marked = _devGuard?.Marked ?? new List<string>();
            string huntNow = DevHunt(), choicesNow = DevChoices();
            var huntMarks = marked.Where(m => m.StartsWith("HUNT")).ToList();
            var choiceMarks = marked.Where(m => m.StartsWith("CHOICES")).ToList();
            string outside = _devGuard?.Outside ?? "";
            if (huntMarks.Count > 0) Say(false, "the hunt", string.Join(" | ", huntMarks));
            else if (DevHuntMarks(huntAtStart, huntNow).Length > 0)
                said.Add($"the hunt changed ({DevHuntMarks(huntAtStart, huntNow).Trim()}: {DevHuntKey(huntNow)}) outside every press of the request: the player's or the game's ({outside})");
            else Say(true, $"the hunt ({DevHuntKey(huntAtStart)})", "");
            said.Add($"the hunt outside every press of the request, the baseline: {outside}");
            if (choiceMarks.Count > 0) Say(false, "every animal's trap, bait and trapless choice", string.Join(" | ", choiceMarks));
            else if (choicesNow != choicesAtStart) said.Add($"a choice changed outside every press of the request: the player's or the game's (now {choicesNow})");
            else Say(true, "every animal's trap, bait and trapless choice", "");
            said.Add("the pointer back where the real one is");
            var notOk = _devOnTheWay.Where(w => !w.Contains(" NOT MADE: ")).ToList();
            if (notOk.Count > 0) said.Add($"on the way NOT AS IT WAS or NOT PUT BACK: {string.Join(" | ", notOk)}");
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
            // "fail=click" fails the step right after the click, so that what the click changed is put back by the failure
            // path (code review 1).
            if (name == "click") name = "the animal shown";
            if (_devSteps.Take(_devPutBackAt).Any(s => s.Name == name))
            {
                _devFailOnPurpose = name;
                DevLog($"the request asks the step '{name}' to fail on purpose, to show the put-back.");
            }
            else DevLog($"no step is called '{name}', so none fails on purpose. The steps: {string.Join(", ", _devSteps.Take(_devPutBackAt).Select(s => s.Name))}.");
        }
    }

    // Both view buttons of the page as drawn.
    string DevViewButtons()
    {
        try
        {
            return Native.TrackingView(_page!) is { } view
                ? $"the hunt button drawn {(Native.HuntButton(view)?.ButtonPressed == true ? "pressed" : "up")}, the journal button drawn {(Native.JournalButton(view)?.ButtonPressed == true ? "pressed" : "up")}"
                : "the view's buttons not found";
        }
        catch (Exception e) { return $"the view's buttons not read ({e.GetType().Name})"; }
    }

    Button DevFoldButton() => _window!.Root.FindChildren("*", "Button", true, false).OfType<Button>()
        .FirstOrDefault(b => b.TooltipText is "Fold down to the title" or "Open the window again")
        ?? throw new InvalidOperationException("The window has no fold button.");

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

    // Every line: what the game gives (raw), what the mod counted from it, the time of a catch by every reading still open
    // (DevReadings: the surveys of a hunt, the steps an escape adds), what is drawn, and its tooltip.
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
                $"the page's surveys 1 to {R(a.SurveysMost)}, base steps {h.BaseMinTrackingSteps}..{h.BaseMaxTrackingSteps}, " +
                $"its trap's bonuses after a failed try {a.TrapFailBonuses}, " +
                $"products [{Items(s.Products)}], survey finds [{Items(s.SurveyingProducts)}], tool {h.Tool?.Id ?? "none"}, trap type {h.TrapType?.Id ?? "none"}, " +
                $"its trap {a.Trap?.Id ?? "none"} ({a.TrapsInBank} in the bank), trapless {a.Trapless} (enabled by the game {h.EnableTrapless}), bait {h.PreferredBait?.Id ?? "none"} " +
                $"used {a.UseBait} ({a.BaitInBank} in the bank), its card's button {(Native.Card(_page!, a.Active) is { } cd && Native.CardButton(cd) is { } cb ? $"disabled {cb.Disabled}" : "not on the page")} | counted: XP range {(a.XpRange is { } r ? $"{R(r.Low)}..{R(r.High)}" : "NOT READ")} " +
                $"the middle {R(a.XpMiddle)} (a catch by the XP rule {R(a.XpPerCatch)}), catch gold {R(a.CatchGold)}, survey gold {R(a.SurveyGold)}, an escape adds " +
                $"[{string.Join(", ", a.EscapeReadings ?? [])}] by [{string.Join(", ", Enum.GetValues<EscapeSteps>())}], so {R(a.EscapeAdds)}, a catch " +
                $"{(a.PerCatch is { } k ? $"{R(Math.Round(k.Surveys, 3))} surveys, {R(Math.Round(k.Steps, 3))} steps, {R(Math.Round(k.Tries, 3))} tries, {R(Math.Round(k.Escapes, 3))} escapes" : "NOT COUNTED")}, " +
                $"time per catch {R(a.SecondsPerCatch)}, catches/h {R(a.CatchesPerHour)}, XP/h {R(a.XpPerHour)}, journal XP/h {R(a.JournalXpPerHour)}, gold/h {R(a.GoldPerHour)}, " +
                $"traps/h {R(a.TrapsPerHour)}, bait/h {R(a.BaitPerHour)} | " +
                $"rare [{string.Join(", ", a.Rare.Select(sp => $"{sp.Name} {R(sp.Share)}{(sp.Locked.Length > 0 ? " locked" : "")} with [{string.Join(", ", sp.Extras.Select(DevDrop))}]"))}] | " +
                $"picture {DevTexture(picture)} tint {a.PictureTint}{(picture != null && _pictures!.IsStandIn(picture) ? "  STAND-IN DRAWN" : "")}");
            var readings = DevReadings(a);
            DevLog($"row | {when} | {h.Id} | the time of a catch by each reading still open: " + (readings.Count == 0 ? $"none ({DevWhyNot(a)})"
                : string.Join(", ", readings.Select(r => $"{r.Name} {R(Math.Round(r.Catch.Seconds, 3))} s"))));
            DevLog($"row | {when} | drawn {drawn}");
            DevLog($"row | {when} | tooltip '{button.TooltipText}'");
        }
        var animals = ReadAnimals();
        DevLog($"rows | {when} | {count} lines, {DevRowsInSight()} in sight; the game has {_hunting!.Action.Animals.Count} animals at {_hunting.Action.Locations.Count} locations, " +
            $"{animals.Count} listed, {animals.Count(a => a.Locked)} locked, {animals.Count(a => !a.Discovered)} not discovered; columns [{string.Join(", ", _list.DevColumns)}]; " +
            $"the hunt by {(AnimalRates.HuntRule is { } hunt ? $"{hunt.Reading.Surveys} and an escape by [{string.Join(", ", hunt.Reading.Escape)}], {hunt.Source}" : "NO RULE")}, " +
            $"the uses by {(AnimalRates.UseRule is { } use ? $"{use.Reading}, {use.Source}" : "NO RULE")}, the XP by {(AnimalRates.XpRule is { } xp ? $"{xp.Reading}, {xp.Source}" : "NO RULE")}, " +
            $"the output by {(AnimalRates.OutputRule is { } output ? $"{output.Reading}, {output.Source}" : "NO RULE")}; {animals.Count(a => a.SecondsPerCatch != null)} lines with a catch counted" +
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
        // Every name's whole tip, as a player reads it on the name over a column.
        foreach (Button head in _list.DevHeaders)
            DevLog($"heads | {when} | '{head.Text}': '{head.TooltipText}'");
        // The lines of a tooltip longer than Fishing Rates' longest before they are wrapped at their words (Words.Joined):
        // WRAPPED with the line and how many lines it became.
        var wrapped = _list.DevLines().SelectMany(l => Words.DevOverlong(l.Animal, _bonusNames, _noTool).Select(line => (l.Animal.Animal.LocalId, Line: line))).ToList();
        DevLog($"wrap | {when} | " + (wrapped.Count == 0 ? $"no tooltip line is longer than {FishingTooltipMost} characters: none wrapped"
            : string.Join(" | ", wrapped.Select(w => $"{w.LocalId} WRAPPED, {w.Line.Length} characters: '{w.Line}'"))));
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
                $"'{DevLabels(survey)}' against ours {R(ours.SurveySeconds)}{Same(survey.MinSeconds, ours.SurveySeconds, 0.005)}, the most surveys " +
                $"{(survey.MinSeconds > 0 ? R(Math.Round(survey.MaxSeconds / survey.MinSeconds, 4)) : "-")} against ours {R(ours.SurveysMost)}" +
                $"{(survey.MinSeconds > 0 && ours.SurveysMost is { } most ? Same(survey.MaxSeconds / survey.MinSeconds, most, 0.001) : "  NOT READ")}" +
                $" | tracking {R(track.MinSeconds)}..{R(track.MaxSeconds)} s " +
                $"'{DevLabels(track)}' against ours {R(ours.StepMinSeconds)}..{R(ours.StepMaxSeconds)}{Same(track.MinSeconds, ours.StepMinSeconds, 0.005)}{Same(track.MaxSeconds, ours.StepMaxSeconds, 0.005)} | " +
                $"trapping {R(trap.MinSeconds)}..{R(trap.MaxSeconds)} s '{DevLabels(trap)}' against ours {R(ours.TrapMinSeconds)}..{R(ours.TrapMaxSeconds)}" +
                $"{Same(trap.MinSeconds, ours.TrapMinSeconds, 0.005)}{Same(trap.MaxSeconds, ours.TrapMaxSeconds, 0.005)} | trapping chance '{chance.Text}' (value {R(chance.Value)}) against ours " +
                $"{R(ours.TrapChance)} | {xpWords}, ours {R(ours.XpEach)} x the range{xpSame} | the interval formats '{survey.IntervalFormat}', tooltips '{survey.TooltipText}' '{track.TooltipText}' '{trap.TooltipText}'");
            DevLog($"game | the panel's outputs: {DevTexts(DevNative.PanelOutputs(panel))} | ours: products [{string.Join(", ", ours.Products.Select(DevDrop))}], rare " +
                $"{R(ours.RareChance)} [{string.Join(", ", ours.Rare.Select(sp => sp.Name))}], survey finds [{string.Join(", ", ours.SurveyFinds.Select(DevDrop))}]");
            foreach (ModifierStatLabel row in DevNative.PanelStats(panel).FindChildren("*", "", true, false).OfType<ModifierStatLabel>())
                DevLog($"game | the panel's bonus '{DevWordsOf(row.StatName ?? "")}' = '{DevNative.RowValue(row)?.Text}' shown {row.IsVisibleInTree()}, its tooltip '{DevWordsOf(row.StatTooltipContent ?? "")}' " +
                    $"/ '{(DevNative.RowTooltip(row) is { } tip ? DevWordsOf(string.Join(" / ", DevNative.Content(tip) ?? [])) : "")}', bound to {DevStatName(DevNative.RowStat(row), selected.Stats)}");
            DevBonuses(selected);
            if (Native.Current(view) is { } current)
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

    // The tool as the card draws it: its words, its colour, and whether it is shown (code review 1, fault 10: the window
    // follows what the card does with a tool the player lacks, once this is seen).
    static string DevToolDrawn(HuntingAnimalTrackingView card)
    {
        Label? tool = Native.ToolOf(card);
        if (tool == null) return "NOT FOUND";
        return $"'{tool.Text}' in {tool.GetThemeColor("font_color").ToHtml()} (modulate {tool.Modulate.ToHtml()}/{tool.SelfModulate.ToHtml()}), shown {tool.IsVisibleInTree()}";
    }

    // The bonus sets of the animal beside its stats (code review 1, fault 7): its trap's (ActiveHuntingAnimal
    // .SelectedTrapBonuses), its bait's, hunting it without a trap, its journal levels', the bonuses a failed try has put on
    // it now (_activeFailBonuses), and what its trap's capability holds (HuntingTrapCapability.TrapBonuses, FailBonuses).
    // Whether the stats hold them is read against the stats' own numbers in the same line.
    void DevBonuses(ActiveHuntingAnimal a)
    {
        try
        {
            string trapBonuses = a.SelectedTrapBonuses == null ? "none" : string.Join("; ", a.SelectedTrapBonuses.Select(t => $"{t.Key?.Id}: {DevNative.Values(t.Value)}"));
            string levels = a.AppliedLevelBonuses == null ? "none"
                : string.Join("; ", a.AppliedLevelBonuses.Select(l => $"level {l.Key}: {string.Join(" + ", l.Value.Select(DevNative.Values))}"));
            string fail;
            try { fail = DevNative.ActiveFailBonuses(a) is { } f ? $"{f.Item1?.Id}: {DevNative.Values(f.Item2)}" : "none"; }
            catch (Exception e) when (e is MissingMemberException or TypeLoadException) { fail = $"not read ({e.GetType().Name})"; }
            string capability = a.SelectedTrap is { } trap && trap.Capabilities.TryGet(out HuntingTrapCapability? cap)
                ? $"its trap's type {cap.TrapType?.Id}, trap bonuses {DevNative.Values(cap.TrapBonuses)}, fail bonuses {DevNative.Values(cap.FailBonuses)}" : "no trap selected, or no trap capability";
            HuntingActionStats s = a.Stats;
            DevLog($"game | the bonus sets of {a.Animal.Id}: its trap's [{trapBonuses}], bait {DevNative.Values(a.BaitBonuses)} (used {a.UseBait}), trapless {DevNative.Values(a.TraplessBonuses)} " +
                $"(on {a.TraplessEnabled}), journal levels [{levels}], a failed try's now {fail}, {capability} | the stats now: trapping {R(s.TrappingSuccessChance.Value)} " +
                $"(base {R(a.Animal.BaseTrapSuccessChance)}), tracking {R(s.TrackingSuccessChance.Value)} (base {R(a.Animal.BaseTrackSuccessChance)}), trap {s.TrappingInterval.Value} ms, " +
                $"step {s.TrackingInterval.Value} ms, steps {s.TrackingSteps.Value}, survey {R(s.SurveyInterval.Value)} ms, rare {R(s.RareSpeciesChance.Value)}, XP {R(s.Experience.XpToAdd.Value)}");
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException or NullReferenceException or InvalidCastException)
        {
            DevLog($"game | the bonus sets of {a.Animal.Id} could not be read: {e.GetType().Name}: {e.Message}");
        }
    }

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
                // The line's colour against the card's: a black shape on the page is one on the line too (HuntingRatesMod.Look),
                // and the journal's count of catches beside it, which the window reads it from where no card was drawn.
                Color cardTint = icon != null ? icon.Modulate * icon.SelfModulate : Colors.White;
                string line = animal != null && lines.TryGetValue(animal, out var l) ? $"the line '{l.Animal.Name}' {DevTexture(l.Picture)} tint {l.Animal.PictureTint}" +
                    (l.Animal.PictureTint.IsEqualApprox(cardTint) ? "" : "  CARD AND LINE DIFFER") : "NO LINE";
                DevLog($"card | {animal?.Animal.Id ?? "no animal"} discovered {animal?.JournalEntry?.IsDiscovered}, trapped {animal?.JournalEntry?.TimesTrapped} times" +
                    $"{(animal?.JournalEntry is { IsDiscovered: true } entry && (entry.TimesTrapped == 0) != (cardTint.R + cardTint.G + cardTint.B < 0.05f) ? "  NOT AS THE READING (BLACK WHILE NEVER TRAPPED)" : "")}" +
                    $" | the card's name '{Native.NameOf(card)?.Text}', picture " +
                    $"{DevTexture(icon?.Texture)} shown {icon?.IsVisibleInTree()} modulate {icon?.Modulate}/{icon?.SelfModulate}, spider icon {icon?.IsArachnophobiaModeIcon}, " +
                    $"tool {DevToolDrawn(card)} (the animal's tool {animal?.Animal.Tool?.Id ?? "none"}), trap '{DevNative.CardTrapType(card).Text}', XP '{(xp == null ? "-" : DevLabels(xp))}' multipliers " +
                    $"{(xp == null ? "-" : $"{R(Native.XpLow(xp))}..{R(Native.XpHigh(xp))}")}, unlocked part {DevNative.CardUnlocked(card).IsVisibleInTree()}, locked part " +
                    $"{DevNative.CardLocked(card).IsVisibleInTree()}, its button {(Native.CardButton(card) is { } b ? $"pressed {b.ButtonPressed}, disabled {b.Disabled}" : "NOT FOUND")} | {line}");
            }
            var action = _hunting!.Action;
            string pageOrder = string.Join(", ", Native.Cards(_page!).Select(c => Native.AnimalOf(c)?.Animal.LocalId ?? "?"));
            string ourOrder = string.Join(", ", ReadAnimals().Where(a => ReferenceEquals(a.Active.Location, action.SelectedLocation)).Select(a => a.Animal.LocalId));
            DevLog($"order | the page's list of {action.SelectedLocation?.Location?.Id ?? "none"}: [{pageOrder}]; ours: [{ourOrder}]{(pageOrder == ourOrder ? "" : "  DIFFER")}");
            if (Native.TrackingView(_page!) is { } view && Native.LocationSelect(view) is { } select && Native.LocationButtons(select) is { } buttons)
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
    // How the first place ranked its four corners (HuntingRatesMod.FirstPlace), for "Dev: room": which won, and why.
    string _devFirstPlaceWords = "the first place not measured";

    void DevRoom(string when)
    {
        DevLog($"room | {when} | {_devFirstPlaceWords}");
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

    static string DevChangedPixels(Image before, Image after, Rect2 screen, string what, string yes, string no, double share0 = 0.25)
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
        return $"{(share >= share0 ? yes : no)}: {share:P0} of the {all:N0} pixels read at {at} ({what}) differ from the picture before (at least {share0:P0} for {yes})";
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
