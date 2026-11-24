#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Entities.Fishing;
using GamesByMalcs.Melvor2.GameMenus;
using GamesByMalcs.Melvor2.GameMenus.Pages;
using GamesByMalcs.Melvor2.InfoLabels;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.StatDisplays;
using GamesByMalcs.Melvor2.Tooltips;
using GamesByMalcs.Melvor2.Utils;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace FishingRates;

// Development: a file "fishing.request" in the mod's folder runs the check below with nobody at the keyboard, one
// step about every half second, and writes "fishing.done" when it ends: "ok" or "failed at <step>: <why>", and in
// both cases what was put back. Every step logs what it read as one-line "Dev: ..." entries in MelvorModLoader.log.
//   .\tools\dev-cycle.ps1 -Mod FishingRates -Into FishingRates -Request fishing.request -Produces fishing.done
// The request file may say "fail=<step>": that step then fails on purpose, to show that a failed request puts
// everything back (-Text 'fail=tooltip').
// It only looks, points and clicks the mod's own controls: no bait or priority fish is selected, nothing is started
// or stopped. The page's location and spot change only in step 7, and are put back. A click is made in two steps,
// as the Item Encyclopedia's request makes them (ItemEncyclopediaMod.DevAim, DevPressAimed): the pointer is put on
// the control, and a later step presses only when the game says the pointer is on that control. Nothing is pointed
// at or pressed while somebody is at the mouse (CombatGoals' WatchHand): a step waits until the real pointer has
// been still for 5 s, and after 90 s the request ends, which puts everything back. The pictures go into the mod's
// folder (shot-fishing-*.png, crop-fishing-*.png).
//  1. Remembers the page on the screen, whether the window was open and folded, the round button, settings.json as
//     it is on disk, the Fishing page's location and every location's selected spot, and what fishing is doing.
//     Starts listening to the game's catches (only listening: see 9).
//  2. Opens the Fishing page the way the sidebar does, if another page is open, and reads back that it is shown;
//     stops with one line when Fishing cannot be opened that way (then open it by hand and send the request again).
//  3. "Dev: strip": every round button in the strip. Closes the window if it was open, presses the round button,
//     and opens the window up with its own button if it opened folded; the round button must be lit.
//  4. "Dev: row": every line's numbers as the game gives them (raw) and as counted, what is drawn and its tooltip;
//     "Dev: pictures": what each line shows at its start, against the game's pictures of the spot's fish (the game's
//     grey picture for a missing one is never drawn: STAND-IN DRAWN or MISMATCH when the line is not as meant);
//     "Dev: hidden": every spot of the game that is not listed, and why; "Dev: size"; "Dev: lit": the spot the
//     page shows against the lit line; "Dev: game": the game's own labels for the page's spot and for every
//     button of its spot list (catch time, XP and its tooltip, fish and junk chance, their pictures) next to the
//     mod's numbers ("DIFFER" marks a difference); "Dev: locations"; "Dev: priority", "Dev: bait", "Dev: page
//     words", "Dev: page bonuses", "Dev: bonus names". crop-fishing-window.png, crop-fishing-strip.png, and
//     crop-fishing-window-end.png with the list scrolled to its end, so every line is in one of the pictures.
//  5. Rests the pointer on a line: "Dev: tooltip" and shot-fishing-tooltip.png. A tooltip that does not come up
//     in 20 s is one line, and the request goes on.
//  6. Clicks "Gold" (crop-fishing-sorted-gold.png), again (the other way round), then "Time"
//     (crop-fishing-sorted-time.png), each with "Dev: order"; a locked line in sight (crop-fishing-locked.png).
//  7. "Dev: click": clicks the line of a spot that can be fished, at another location than the page's when there
//     is one, and reads back the page's location and spot, what the page shows in its own words, the lit line, the
//     game's events and what fishing is doing (shot-fishing-clicked-spot.png). Puts the page's selection back,
//     presses the game's own location button and spot button for the same spot, and compares ("DIFFER", "EVENTS
//     DIFFER"). Puts the selection back, clicks a locked line (its location opens, its spot is not selected;
//     shot-fishing-clicked-locked.png) and puts the selection back again. FISHING CHANGED, FISHING STARTED or
//     FISHING STOPPED, NOT OPENED, THE PAGE SHOWS ANOTHER SPOT and THE WINDOW CLOSED mark what must not happen.
//  8. Folds the window with its button (crop-fishing-folded.png, the round button unlit) and opens it again;
//     builds it at the smallest and at the largest size (crop-fishing-smallest.png, crop-fishing-largest.png,
//     "Dev: size", which says LARGER or OVER THE BOTTOM BAR when it does not fit); closes it with its X
//     (shot-fishing-closed.png).
//  9. "Dev: catches": while the character is fishing, what the game's catches brought since the request began (up to
//     8 catches or 45 s): fish, junk, special items, how many at a time, the XP each catch added and the bait, next
//     to the mod's numbers. Read from the game's own events (RecipeFished, SpecialItemFished, XpChanged,
//     BaitConsumed, BaitPreserved); nothing is changed.
// 10. Puts back settings.json exactly as it was (sort, size, place, fold), the window open or closed and folded as
//     it was, the Fishing page's location and every location's spot, the page, and the pointer where the real one
//     is, and stops listening to the game.
// 11. Reads all of it back from the screen, the game and the disk, what fishing is doing too, and says in the last
//     line what was put back, with NOT AS IT WAS for anything that is not.
// A step that fails jumps to 10: a failed request puts everything back too.
public partial class FishingRatesMod
{
    const string RequestFile = "fishing.request", DoneFile = "fishing.done";
    // CombatGoals/DevWindowShots.cs: how long the real pointer must be still before a step points or presses, and
    // how long a step waits for that before the request ends.
    const ulong StillFor = 5000, GiveUpAfter = 90000, StepTime = 6000, PointerStepTime = GiveUpAfter + 10000;

    readonly List<(string Name, Func<bool> Run, ulong Timeout)> _devSteps = new();
    int _devAt, _devPutBackAt;
    ulong _devStarted;
    string? _devFailure, _devFailOnPurpose, _devPutBackWords;
    bool _devPutBackOk;

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrentPage")]
    static extern (GamePage, ActiveGamePage)? CurrentPage(GameView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ActionView")]
    static extern FishingActionView ActionView(FishingActiveView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RecipeView")]
    static extern FishingRecipeView RecipeView(FishingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LocationSelect")]
    static extern FishingLocationSelect LocationSelect(FishingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_OptionViews")]
    static extern Dictionary<ActiveFishingLocation, FishingLocationOption> LocationOptions(FishingLocationSelect select);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LocationName")]
    static extern Label LocationName(FishingLocationOption option);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SpotsUnlocked")]
    static extern Int32OutOfLabel SpotsUnlocked(FishingLocationOption option);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_AvailableProducts")]
    static extern FishingAvailableProductsView LocationFish(FishingLocationOption option);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LockedContainer")]
    static extern VBoxContainer LocationLocked(FishingLocationOption option);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Recipe")]
    static extern ActiveFishingRecipe? ShownRecipe(FishingRecipeView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_IntervalRange")]
    static extern IntervalRangeDisplay Interval(FishingRecipeView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SkillXPPanel")]
    static extern SkillXPPanel XpPanel(FishingRecipeView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_FishChance")]
    static extern PercentLabel FishChance(FishingRecipeView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_JunkChance")]
    static extern PercentLabel JunkChance(FishingRecipeView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_PrioritySelect")]
    static extern FishingPrioritySelect PrioritySelect(FishingRecipeView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_FishingSpotSelectView")]
    static extern FishingSpotSelectView SpotSelect(FishingRecipeView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_FishingSpotButtons")]
    static extern Dictionary<ActiveFishingRecipe, FishingSpotSelectButton> SpotButtons(FishingSpotSelectView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_IntervalRange")]
    static extern IntervalRangeDisplay Interval(FishingSpotSelectButton button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SkillXPPanel")]
    static extern SkillXPPanel XpPanel(FishingSpotSelectButton button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_FishChance")]
    static extern PercentLabel FishChance(FishingSpotSelectButton button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_JunkChance")]
    static extern PercentLabel JunkChance(FishingSpotSelectButton button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_FishingSpotLabel")]
    static extern Label SpotLabel(FishingSpotSelectButton button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_AvailableFishContainer")]
    static extern HBoxContainer SpotFish(FishingSpotSelectButton button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_UnlockedContainer")]
    static extern VBoxContainer SpotUnlocked(FishingSpotSelectButton button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LockedContainer")]
    static extern VBoxContainer SpotLocked(FishingSpotSelectButton button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_IntervalLabel")]
    static extern Label IntervalLabel(BaseIntervalDisplay display);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SkillXpLabel")]
    static extern Int32FormattedLabel XpLabel(SkillXPPanel panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_XPTooltip")]
    static extern TextTooltip? XpTooltip(SkillXPPanel panel);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_contentText")]
    static extern ref List<string> Content(TextTooltip tooltip);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_InfoTooltip")]
    static extern TextTooltip? PriorityInfo(FishingPrioritySelect select);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrentPriority")]
    static extern Label CurrentPriority(FishingPrioritySelect select);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrentBaitName")]
    static extern Label CurrentBait(FishingBaitSelect select);
    // What a click on a line is compared with: the spot the action itself keeps as selected, the page's own words for
    // its location and spot, and the handlers of a game spot button's click (BaseButtonContainer.Pressed is an event
    // of its own, not a signal).
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SelectedRecipe")]
    static extern ActiveFishingRecipe? ActionSelected(FishingActionComponent action);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrentLocationName")]
    static extern Label LocationLabel(FishingLocationSelect select);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RecipeName")]
    static extern Label RecipeName(FishingRecipeView view);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "Pressed")]
    static extern ref Action? PressedHandlers(GamesByMalcs.Melvor2.CustomControls.BaseButtonContainer button);

    void DevReset()
    {
        // Another character while a request runs: its listening is let go, and the request says it stopped.
        if (_devSteps.Count > 0)
        {
            DevUnwatch();
            DevEnd("stopped: another character was opened, nothing more was put back");
        }
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
                DevLog($"Fishing Rates check started, {_devPutBackAt} steps and the put-back.");
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
            DevUnwatch();
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
        DevLog("Fishing Rates check " + result);
        _devSteps.Clear();
        _devAt = 0;
        File.WriteAllText(Path.Combine(_ctx.Directory, DoneFile), result);
    }

    string DevState() =>
        $"page shown={PageShown}, button shown={_opener?.IsVisibleInTree()}, lit={_opener?.ButtonPressed}, window open={IsOpen}, " +
        $"folded={_window?.Minimised}, sort={_settings.Sort} {(_settings.Descending ? "descending" : "ascending")}";

    // One entry, one line: the check keeps only the log's lines that carry the mod's name, so words of the game
    // with line breaks in them (its tooltips) are joined with " / ".
    void DevLog(string text) => _ctx.Log("Dev: " + One(text));

    static string One(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", " / ");

    // What the request found, to be put back as it was.
    sealed record DevBefore(GamePage? Page, bool Wanted, bool Shown, bool? Folded, bool? Lit, string? Settings);

    void DevPlan(string asked)
    {
        _devSteps.Clear();
        _devAt = _devPutBackAt = 0;
        _devFailure = _devFailOnPurpose = _devPutBackWords = null;
        _devPutBackOk = false;
        _devGuardSince = _devTipSince = 0;
        _devAim = null;
        _devStarted = Time.GetTicksMsec();
        GameView view = GameAccess.FindGameView() ?? throw new InvalidOperationException("No character is open.");
        var before = new DevBefore(CurrentPage(view)?.Item1, _wanted, _window is { IsAlive: true, Visible: true },
            _window is { IsAlive: true } ? _window.Minimised : null, GodotObject.IsInstanceValid(_opener) ? _opener!.ButtonPressed : null,
            File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : null);
        Button? target = null;
        string firstBefore = "";
        void Add(string name, Func<bool> run, ulong timeout = StepTime) => _devSteps.Add((name, run, timeout));

        var action = _fishing!.Action;
        // The Fishing page's selection, which the clicks on lines change and the put-back puts back, and what fishing
        // is doing, which nothing of the request may change.
        DevSelection selection = DevSelectionNow();
        string fishingAtStart = DevFishingState();
        DevLog($"before: page {before.Page?.Id ?? "none"}, window {(before.Wanted ? "open" : "closed")}{(before.Shown ? " and on the screen" : "")}" +
            $"{(before.Folded == true ? ", folded" : "")}, round button {(before.Lit is { } lit ? lit ? "lit" : "unlit" : "not made yet")}, settings.json " +
            (before.Settings == null ? "not there" : $"{before.Settings.Length} characters: {before.Settings}") +
            $" | fishing now: {DevFishingNow()}, the spot fished {action.CurrentRecipe?.Recipe.Id ?? "none"}, the page's location " +
            $"{action.CurrentLocation?.Location.Id ?? "none"} and its spot {action.CurrentLocation?.SelectedRecipe?.Recipe.Id ?? "none"}, " +
            $"the action's own selected spot {selection.ActionSpot?.Recipe.Id ?? "none"}, every location's spot [{DevSpots(selection)}] | " +
            $"the real pointer at {DisplayServer.MouseGetPosition()}");
        DevWatch();

        Add("open the Fishing page", () =>
        {
            if (PageShown) return true;
            if (_devOpenedPage) return false;   // read back on the next ticks: the page is found once a second
            var option = _game!.Layout.Sidebar.Options.FirstOrDefault(o =>
                o.Page is EntityPage p && ReferenceEquals(p.Entity, _fishing) &&
                o.Requirements?.AreMet != false && o.DisplayRequirements?.AreMet != false);
            if (option?.Page is not EntityPage page)
                throw new InvalidOperationException("Fishing cannot be opened from the sidebar: open the Fishing page by hand and send the request again.");
            view.SetCurrentPage(page);
            _devOpenedPage = true;
            return false;
        });
        Add("the round button", () =>
        {
            if (!PageShown || _opener?.IsVisibleInTree() != true) return false;
            DevLog($"page | the page is {CurrentPage(view)?.Item1?.Id ?? "none"}, the Fishing page is shown: {PageShown}");
            DevStrip();
            if (IsOpen) Close();
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
            if (rows == 0) throw new InvalidOperationException("The window lists no fishing spot.");
            DevPictures();
            DevHidden();
            DevSize("open");
            DevGame();
            DevLocations();
            DevBonuses();
            Shot("window", crop: true);
            ShotStrip();
            // The rest of the list, for the picture of its end.
            _list!.DevScroll.ScrollVertical = (int)_list.DevScroll.GetVScrollBar().MaxValue;
            return true;
        });
        Add("the end of the list", () =>
        {
            var scroll = _list!.DevScroll;
            if (scroll.GetVScrollBar().MaxValue > scroll.GetVScrollBar().Page && scroll.ScrollVertical == 0) return false;
            DevLog($"in sight at the end of the list: {DevInSight()}");
            Shot("window-end", crop: true);
            scroll.ScrollVertical = 0;
            return true;
        });
        Add("point at a line", () =>
        {
            if (_list!.DevScroll.ScrollVertical != 0) return false;
            var lines = _list.DevLines().ToList();
            target = (lines.FirstOrDefault(l => !l.Spot.Locked).Button ?? lines.FirstOrDefault().Button)
                ?? throw new InvalidOperationException("No line to point at.");
            _list.DevScroll.EnsureControlVisible(target);
            _devTipSince = 0;
            return true;
        });
        Add("tooltip", () => DevTooltip(target!), PointerStepTime);
        // Each click is waited for as a change of the sort, whatever the sort was before it.
        (string Key, bool Descending) sortWas = default;
        Add("click Gold", () =>
        {
            if (firstBefore.Length == 0)
            {
                firstBefore = DevOrder("before, " + _settings.Sort + (_settings.Descending ? " descending" : " ascending"));
                sortWas = (_settings.Sort, _settings.Descending);
            }
            return DevPress(_list!.DevHeader("gold") ?? throw new InvalidOperationException("There is no Gold column."), "the name Gold");
        }, PointerStepTime);
        Add("sorted by gold", () =>
        {
            if (_settings.Sort != "gold" || (_settings.Sort, _settings.Descending) == sortWas) return false;
            Refresh();
            DevOrder("gold, " + (_settings.Descending ? "most first" : "least first"));
            Shot("sorted-gold", crop: true);
            sortWas = (_settings.Sort, _settings.Descending);
            return true;
        });
        Add("click Gold again", () => DevPress(_list!.DevHeader("gold")!, "the name Gold"), PointerStepTime);
        Add("gold the other way", () =>
        {
            if (_settings.Sort != "gold" || _settings.Descending == sortWas.Descending) return false;
            Refresh();
            DevOrder("gold, " + (_settings.Descending ? "most first" : "least first"));
            return true;
        });
        Add("click Time", () => DevPress(_list!.DevHeader("time") ?? throw new InvalidOperationException("There is no Time column."), "the name Time"), PointerStepTime);
        Add("sorted by time", () =>
        {
            if (_settings.Sort != "time") return false;
            Refresh();
            DevOrder("time, " + (_settings.Descending ? "longest first" : "shortest first"));
            Shot("sorted-time", crop: true);
            return true;
        });
        Add("a locked line", () =>
        {
            var locked = _list!.DevLines().FirstOrDefault(l => l.Spot.Locked).Button;
            if (locked == null) { DevLog("no locked spot is listed: no picture of one."); target = null; return true; }
            _list.DevScroll.EnsureControlVisible(locked);
            target = locked;
            return true;
        });
        Add("locked picture", () =>
        {
            if (target is { } locked && _list!.DevLines().Any(l => l.Button == locked && l.Spot.Locked))
            {
                if (!locked.GetGlobalRect().Intersects(_list.DevScroll.GetGlobalRect())) return false;
                DevLog($"locked | in sight: {DevInSight()}");
                Shot("locked", crop: true);
            }
            return true;
        });

        // A click on a line whose spot can be fished, preferably at another location than the page's so that both of
        // the page's clicks are made, then the game's own two buttons for the same spot, to compare; then a click on a
        // locked line. The page's selection is put back after each.
        SpotRates? clicked = null;
        DevSelectionEvents? events = null;
        string fishingBefore = "", ours = "", ourEvents = "";
        Add("a line to click", () =>
        {
            var lines = _list!.DevLines().Select(l => l.Spot).ToList();
            clicked = lines.FirstOrDefault(s => s.Click == SpotRates.Opens.Spot && !s.LocationOnPage)
                ?? lines.FirstOrDefault(s => s.Click == SpotRates.Opens.Spot && !s.OnPage);
            if (clicked == null) { DevLog("click | no line opens another spot than the page's: none is clicked."); return true; }
            _list.DevScroll.EnsureControlVisible(DevLineOf(clicked));
            fishingBefore = DevFishingState();
            DevLog($"click | the line of {clicked.Active.Recipe.Id} at {clicked.Active.Location.Id}; before: {DevClickResult()}; {fishingBefore}");
            return true;
        });
        Add("click the line", () =>
        {
            if (clicked == null) return true;
            events ??= new DevSelectionEvents(_fishing!.Action);
            return DevClickLine(clicked);
        }, PointerStepTime);
        Add("the spot opened", () =>
        {
            if (clicked == null) return true;
            var action = _fishing!.Action;
            ActiveFishingRecipe spot = clicked.Active;
            bool opened = ReferenceEquals(action.CurrentLocation?.Location, spot.Location) && ReferenceEquals(action.CurrentLocation?.SelectedRecipe, spot);
            if (!opened && Time.GetTicksMsec() - _devStarted < 3000) return false;
            Refresh();
            var lit = _list!.DevLines().Where(l => l.Spot.OnPage).Select(l => l.Spot.Active.Recipe.Id).ToList();
            string fishing = DevFishingState();
            ours = DevClickResult();
            ourEvents = string.Join(", ", events!.Seen);
            DevLog($"click | after the click on the line of {spot.Recipe.Id}: {ours}; the game's events [{ourEvents}]; {fishing}" +
                (opened ? "" : "  NOT OPENED") + (fishing == fishingBefore ? "" : "  FISHING CHANGED") +
                (ReferenceEquals(ShownRecipe(RecipeView(ActionView(_page!))), spot) ? "" : "  THE PAGE SHOWS ANOTHER SPOT") +
                (lit.SequenceEqual([spot.Recipe.Id]) ? "" : $"  THE LIT LINE IS [{string.Join(", ", lit)}]") +
                (IsOpen ? "" : "  THE WINDOW CLOSED"));
            Shot("clicked-spot");
            events.Dispose();
            events = null;
            DevLog("click | the page's selection put back for the game's own buttons: " + DevSelectionBack(selection));
            return true;
        });
        Add("the game's location button", () =>
        {
            if (clicked == null) return true;
            var action = _fishing!.Action;
            ActiveFishingLocation place = action.Locations[clicked.Active.Location];
            events = new DevSelectionEvents(action);
            if (ReferenceEquals(action.CurrentLocation, place)) { DevLog("click | the game's location button is not pressed: the page is on that location"); return true; }
            TextureButton button = DevLocationButton(place);
            DevLog($"click | the game's location button of {place.Location.Id} (disabled={button.Disabled}, shown={button.IsVisibleInTree()}) is pressed through its own signal");
            button.EmitSignal(BaseButton.SignalName.Pressed);
            return true;
        });
        Add("the game's spot button", () =>
        {
            if (clicked == null) return true;
            var buttons = SpotButtons(SpotSelect(RecipeView(ActionView(_page!))));
            if (!buttons.TryGetValue(clicked.Active, out FishingSpotSelectButton? button) || !GodotObject.IsInstanceValid(button) || button.IsQueuedForDeletion())
            {
                if (Time.GetTicksMsec() - _devStarted < 3000) return false;
                throw new InvalidOperationException($"the game's spot list has no button for {clicked.Active.Recipe.Id}");
            }
            Action? handlers = PressedHandlers(button);
            DevLog($"click | the game's spot button of {clicked.Active.Recipe.Id} is pressed through the {handlers?.GetInvocationList().Length ?? 0} handlers of its click");
            handlers?.Invoke();
            return true;
        });
        Add("the game's buttons compared", () =>
        {
            if (clicked == null) return true;
            string game = DevClickResult(), gameEvents = string.Join(", ", events!.Seen);
            string fishing = DevFishingState();
            DevLog($"click | after the game's own buttons: {game}; the game's events [{gameEvents}]; {fishing}" +
                (fishing == fishingBefore ? "" : "  FISHING CHANGED") + " | " +
                (game == ours ? "the page and the action as after the click on the line" : $"DIFFER from the click on the line ({ours})") + "; " +
                (DevSameEvents(gameEvents, ourEvents) ? "the same events" : $"EVENTS DIFFER from the click on the line ([{ourEvents}])"));
            events.Dispose();
            events = null;
            DevLog("click | the page's selection put back: " + DevSelectionBack(selection));
            return true;
        });
        SpotRates? locked = null;
        Add("a locked line to click", () =>
        {
            var lines = _list!.DevLines().Select(l => l.Spot).ToList();
            locked = lines.FirstOrDefault(s => s.Locked && s.Click == SpotRates.Opens.Location && !s.LocationOnPage) ?? lines.FirstOrDefault(s => s.Locked);
            if (locked == null) { DevLog("click | no locked line: none is clicked."); return true; }
            _list.DevScroll.EnsureControlVisible(DevLineOf(locked));
            fishingBefore = DevFishingState();
            var place = _fishing!.Action.Locations[locked.Active.Location];
            TextureButton button = DevLocationButton(place);
            DevLog($"click | the locked line of {locked.Active.Recipe.Id} at {place.Location.Id}: a click opens {locked.Click}" +
                $"{(locked.LocationOnPage ? ", the page is on its location already" : "")}; the game's own location button disabled={button.Disabled}" +
                (button.Disabled == (locked.Click == SpotRates.Opens.Nothing) ? "" : "  NOT AS THE GAME'S BUTTON") +
                $"; before: {DevClickResult()}; {fishingBefore}");
            return true;
        });
        Add("click the locked line", () =>
        {
            if (locked == null) return true;
            events ??= new DevSelectionEvents(_fishing!.Action);
            return DevClickLine(locked);
        }, PointerStepTime);
        Add("the locked line's location", () =>
        {
            if (locked == null) return true;
            var action = _fishing!.Action;
            ActiveFishingRecipe spot = locked.Active;
            ActiveFishingLocation place = action.Locations[spot.Location];
            bool opens = locked.Click == SpotRates.Opens.Location && !locked.LocationOnPage, on = ReferenceEquals(action.CurrentLocation, place);
            if (opens && !on && Time.GetTicksMsec() - _devStarted < 3000) return false;
            ActiveFishingRecipe? was = selection.Spots.First(s => ReferenceEquals(s.Location, place)).Spot;
            string fishing = DevFishingState();
            DevLog($"click | after the click on the locked line of {spot.Recipe.Id}: {DevClickResult()}; the game's events [{string.Join(", ", events!.Seen)}]; {fishing} | " +
                (opens ? on ? "its location opened" : "its location NOT OPENED" : ReferenceEquals(action.CurrentLocation, selection.Location) ? "nothing changed, as meant" : "THE PAGE CHANGED") +
                $"; its location's spot {place.SelectedRecipe?.Recipe.Id ?? "none"}: " +
                (ReferenceEquals(place.SelectedRecipe, was) ? "as before" : ReferenceEquals(place.SelectedRecipe, spot) ? "THE LOCKED SPOT WAS SELECTED" : "CHANGED") +
                (fishing == fishingBefore ? "" : "  FISHING CHANGED") + (IsOpen ? "" : "  THE WINDOW CLOSED"));
            Shot("clicked-locked");
            events.Dispose();
            events = null;
            DevLog("click | the page's selection put back: " + DevSelectionBack(selection));
            return true;
        });
        Add("fold", () => DevPress(DevFoldButton(), "the window's fold button"), PointerStepTime);
        Add("folded", () =>
        {
            if (_window?.Minimised != true) return false;
            if (_opener?.ButtonPressed != false) throw new InvalidOperationException("The round button is still lit while the window is folded.");
            Shot("folded", crop: true);
            return true;
        });
        Add("open up again", () => DevPress(DevFoldButton(), "the window's fold button"), PointerStepTime);
        Add("opened up again", () =>
        {
            if (_window?.Minimised != false || _opener?.ButtonPressed != true || _fitPending) return false;
            Resize(GameWindow.MinScale);
            return true;
        });
        Add("smallest", () =>
        {
            if (!IsOpen) return false;
            DevSize("smallest");
            Shot("smallest", crop: true);
            Resize(GameWindow.MaxScale);
            return true;
        });
        Add("largest", () =>
        {
            if (!IsOpen) return false;
            DevSize("largest that fits");
            Shot("largest", crop: true);
            return true;
        });
        Add("close with the X", () => DevPress(_window!.Root.FindChildren("*", "Button", true, false).OfType<Button>().First(b => b.Text == "X"), "the window's X"),
            PointerStepTime);
        Add("closed with the X", () =>
        {
            if (IsOpen || _window is { IsAlive: true, Visible: true }) return false;
            if (_opener?.ButtonPressed != false) throw new InvalidOperationException("The round button is still lit after the X.");
            Shot("closed");
            return true;
        });
        Add("catches", () =>
        {
            if (_devWatch is { } watch && watch.Catches < 8 && DevFishingNow() && Time.GetTicksMsec() - watch.Since < 45000) return false;
            DevCatches();
            return true;
        }, 50000);

        _devPutBackAt = _devSteps.Count;
        Add("put back", () =>
        {
            DevUnwatch();
            events?.Dispose();
            events = null;
            DevSelectionBack(selection);
            if (before.Settings == null) File.Delete(SettingsFile);
            else ModFiles.WriteWhole(SettingsFile, before.Settings);
            _settings = ModFiles.ReadJson<Settings>(SettingsFile, _ctx.Log);
            _settings.Sort ??= RatesList.NameKey;
            _window?.Free();
            _window = null;
            _list = null;
            _builtFor = null;
            _fitPending = false;
            _wanted = before.Wanted;
            if (before.Page != null && !ReferenceEquals(CurrentPage(view)?.Item1, before.Page)) view.SetCurrentPage(before.Page);
            Sync();
            SyncStripButton();
            DevPointerBack();
            return true;
        });
        Add("read back", () =>
        {
            // The page changes on the game's own time: read back once it has, or after a few seconds anyway.
            GamePage? page = CurrentPage(view)?.Item1;
            if (before.Page != null && !ReferenceEquals(page, before.Page) && Time.GetTicksMsec() - _devStarted < 4000) return false;
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
            Say(_devWatch == null, "the game's catches no longer listened to", "still listened to");
            string differs = DevSelectionDiffers(selection);
            Say(differs.Length == 0, $"the Fishing page's location {selection.Location?.Location.Id ?? "none"} and every location's spot", differs);
            ActiveFishingRecipe? actionSpot = ActionSelected(_fishing!.Action);
            Say(ReferenceEquals(actionSpot, selection.ActionSpot), $"the action's own selected spot {selection.ActionSpot?.Recipe.Id ?? "none"}", actionSpot?.Recipe.Id ?? "none");
            string fishingNow = DevFishingState();
            Say(fishingNow == fishingAtStart, $"fishing: {fishingAtStart}", fishingNow);
            said.Add("the pointer back where the real one is");
            _devPutBackWords = string.Join("; ", said) + $"; the order before the clicks was {(firstBefore.Length > 0 ? firstBefore : "not read")}";
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
        _devOpenedPage = false;
    }

    bool _devOpenedPage;

    Button DevFoldButton() => _window!.Root.FindChildren("*", "Button", true, false).OfType<Button>()
        .FirstOrDefault(b => b.TooltipText is "Fold down to the title" or "Open the window again")
        ?? throw new InvalidOperationException("The window has no fold button.");

    bool DevFishingNow() => _game!.ActionManager.ActiveActions.Any(a => ReferenceEquals(a.Entity, _fishing));

    // ---- a click on a line: the Fishing page's selection ---------------------------------------------------------------

    // The page's location, the spot selected at every location, and the spot the action itself keeps as selected.
    sealed record DevSelection(ActiveFishingLocation? Location, List<(ActiveFishingLocation Location, ActiveFishingRecipe? Spot)> Spots,
        ActiveFishingRecipe? ActionSpot);

    DevSelection DevSelectionNow()
    {
        var action = _fishing!.Action;
        return new(action.CurrentLocation, action.Locations.Values.Select(l => (l, l.SelectedRecipe)).ToList(), ActionSelected(action));
    }

    static string DevSpots(DevSelection selection) =>
        string.Join(", ", selection.Spots.Select(s => $"{s.Location.Location.Id} {s.Spot?.Recipe.Id ?? "none"}"));

    // The page's selection as it was, through the members a click on a line uses: every location's spot, then the
    // location. Says what is not as it was.
    string DevSelectionBack(DevSelection before)
    {
        var action = _fishing!.Action;
        foreach (var (location, spot) in before.Spots)
            if (spot != null && !ReferenceEquals(location.SelectedRecipe, spot)) location.SelectRecipe(spot);
        if (before.Location != null && !ReferenceEquals(action.CurrentLocation, before.Location)) action.SetCurrentLocation(before.Location);
        string differs = DevSelectionDiffers(before);
        ActiveFishingRecipe? actionSpot = ActionSelected(action);
        return (differs.Length == 0 ? "as it was" : "NOT AS IT WAS: " + differs) +
            (ReferenceEquals(actionSpot, before.ActionSpot) ? "" : $"; the action's own selected spot NOT AS IT WAS ({actionSpot?.Recipe.Id ?? "none"})");
    }

    // What of the page's selection is not as it was, or "".
    string DevSelectionDiffers(DevSelection before)
    {
        var action = _fishing!.Action;
        var differs = new List<string>();
        if (!ReferenceEquals(action.CurrentLocation, before.Location)) differs.Add($"the location {action.CurrentLocation?.Location.Id ?? "none"}");
        foreach (var (location, spot) in before.Spots)
            if (!ReferenceEquals(location.SelectedRecipe, spot)) differs.Add($"{location.Location.Id} has {location.SelectedRecipe?.Recipe.Id ?? "none"} selected");
        return string.Join(", ", differs);
    }

    // What fishing is doing: no click on a line may change it.
    string DevFishingState()
    {
        var action = _fishing!.Action;
        return $"fishing: started {action.IsStarted}, active {DevFishingNow()}, the spot fished {action.CurrentRecipe?.Recipe.Id ?? "none"}";
    }

    // What a click left: the page's location and spot as the game keeps them, the action's own selected spot, and what
    // the page shows in its own words.
    string DevClickResult()
    {
        var action = _fishing!.Action;
        FishingActionView actionView = ActionView(_page!);
        FishingRecipeView recipeView = RecipeView(actionView);
        return $"the location {action.CurrentLocation?.Location.Id ?? "none"} with {action.CurrentLocation?.SelectedRecipe?.Recipe.Id ?? "none"} selected, " +
            $"the action's own selected spot {ActionSelected(action)?.Recipe.Id ?? "none"}, the page shows the location " +
            $"'{LocationLabel(LocationSelect(actionView)).Text}' and the spot '{RecipeName(recipeView).Text}' ({ShownRecipe(recipeView)?.Recipe.Id ?? "none"})";
    }

    // The game's own button of a location on the Fishing page (FishingLocationOption.Button).
    TextureButton DevLocationButton(ActiveFishingLocation place) =>
        LocationOptions(LocationSelect(ActionView(_page!))).TryGetValue(place, out FishingLocationOption? option) ? option.Button
        : throw new InvalidOperationException($"the Fishing page has no location button for {place.Location.Id}");

    Button DevLineOf(SpotRates spot) => _list!.DevLines().First(l => ReferenceEquals(l.Spot.Active, spot.Active)).Button;

    // The line is pressed the way a player's hand would (DevPress), once the list has scrolled it into sight.
    bool DevClickLine(SpotRates spot)
    {
        Button line = DevLineOf(spot);
        if (!DevSight(line).HasArea()) return false;
        return DevPress(line, $"the line of {spot.Name}");
    }

    // The same events in another order are the same.
    static bool DevSameEvents(string a, string b) =>
        a.Split(", ", StringSplitOptions.RemoveEmptyEntries).Order().SequenceEqual(b.Split(", ", StringSplitOptions.RemoveEmptyEntries).Order());

    // The game's events about the page's selection and about fishing, listened to while one click is made.
    sealed class DevSelectionEvents : IDisposable
    {
        public readonly List<string> Seen = new();
        readonly List<Action> _off = new();

        public DevSelectionEvents(FishingActionComponent action)
        {
            EventHandler<ValueChangedEventArgs<ActiveFishingLocation>> location = (_, e) => Seen.Add($"the page's location {e.NewValue?.Location.Id ?? "none"}");
            EventHandler<ValueChangedEventArgs<ActiveFishingRecipe>> fished = (_, e) => Seen.Add($"THE SPOT FISHED {e.NewValue?.Recipe.Id ?? "none"}");
            EventHandler<GamesByMalcs.Melvor2.Components.Actions.ActionEventArgs> started = (_, _) => Seen.Add("FISHING STARTED"), stopped = (_, _) => Seen.Add("FISHING STOPPED");
            action.CurrentLocationChanged += location;
            action.CurrentRecipeChanged += fished;
            action.Started += started;
            action.Stopped += stopped;
            _off.Add(() =>
            {
                action.CurrentLocationChanged -= location;
                action.CurrentRecipeChanged -= fished;
                action.Started -= started;
                action.Stopped -= stopped;
            });
            foreach (ActiveFishingLocation place in action.Locations.Values)
            {
                EventHandler<ValueChangedEventArgs<ActiveFishingRecipe>> spot = (_, e) => Seen.Add($"{place.Location.Id} selects {e.NewValue?.Recipe.Id ?? "none"}");
                place.SelectedRecipeChanged += spot;
                _off.Add(() => place.SelectedRecipeChanged -= spot);
            }
        }

        public void Dispose()
        {
            foreach (Action off in _off) off();
            _off.Clear();
        }
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
        foreach (var (s, button, drawn, _) in _list!.DevLines())
        {
            count++;
            FishingActionStats stats = s.Active.Stats;
            var (min, max) = stats.Interval.Value;
            var table = s.Active.Recipe.RandomProducts;
            string rawFish = string.Join(", ", (table?.Items ?? Enumerable.Empty<FishingProduct>()).Select(p => $"{p.Item?.Id} {R(table!.GetItemChance(p))}"));
            string rawSpecial = string.Join(", ", (stats.SpecialProductChances?.Values ?? []).Select(n => $"{n.Value?.Item?.Id} {R(n.Value?.Chance ?? double.NaN)} x{n.Value?.Quantity}"));
            DevLog($"row | {when} | {s.Active.Recipe.Id} at {s.Active.Location.Id} | locked={s.Locked}, level={s.Level}, missing '{string.Join("; ", s.Missing)}', " +
                $"on the page={s.OnPage} | interval ms {R(min)}-{R(max)} -> {R(s.Seconds)} s, {R(s.CatchesPerHour)} catches/h | XP {R(s.XpEach)} each, {R(s.XpPerHour)}/h | " +
                $"raw: fish chance {R(stats.FishChance.Value)}, junk chance {R(stats.JunkChance.Value)}, doubling {R(stats.DoublingChance?.Total.Value ?? double.NaN)}, " +
                $"the table's chances [{rawFish}], special [{rawSpecial}], bait preservation {R(_fishing!.BaitComponent.Stats?.PreservationChance?.Total.Value ?? double.NaN)} | " +
                $"counted: fish {R(s.FishChance)}, junk {R(s.JunkChance)}, doubling {R(s.Doubling)} | fish [{string.Join(", ", s.Fish.Select(DevCatch))}] " +
                $"junk [{string.Join(", ", s.Junk.Select(DevCatch))}] special [{string.Join(", ", s.Special.Select(DevCatch))}] | " +
                $"gold {(s.GoldPerHour is { } g ? R(g) : "-")}/h, {R(s.SpecialGoldPerHour)} of it from special items | " +
                $"bait {s.Bait ?? "none"} {(s.BaitPerHour is { } b ? R(b) : "-")}/h kept {R(s.BaitKept)}, {s.BaitInBank} in the bank | " +
                $"priority {s.PriorityFish ?? "none"} | not counted [{string.Join(", ", s.NotCounted.Select(n => $"{n.Key} {R(n.Nodes.Total.Value)}"))}]");
            DevLog($"row | {when} | drawn {drawn}");
            DevLog($"row | {when} | tooltip '{button.TooltipText}'");
        }
        var spots = ReadSpots();
        DevLog($"rows | {when} | {count} lines, {DevRowsInSight()} in sight; the game has {_fishing!.Action.Recipes.Count} spots, {spots.Count} shown, " +
            $"{spots.Count(s => s.Locked)} locked" + (count != spots.Count ? "  MISMATCH" : ""));
        return DevRowsInSight();
    }

    static string DevCatch(SpotRates.Catch c) =>
        $"{c.Item.Id} chance {R(c.Chance)} x{c.Min}-{c.Max} each {R(c.Each)} = {R(c.PerHour)}/h, sells {c.Item.SellsFor?.Quantity} {c.Item.SellsFor?.Currency?.Id}, gold {(c.GoldPerHour is { } g ? R(g) : "-")}/h";

    // What each line shows at its start, against the game's pictures of the spot's fish: the grey stand-in for a
    // missing one is never drawn ("STAND-IN DRAWN" when it is), every other one is, in the order of the spot's table
    // ("MISMATCH" when not). Also the spot's own picture, which the lines do not use (the grey stand-in on every spot
    // in the first game check).
    void DevPictures()
    {
        GamePictures pictures = _pictures!;
        bool StandIn(Texture2D? t) => t != null && pictures.IsStandIn(t);
        string Picture(Texture2D? t) => DevTexture(t) + (StandIn(t) ? " (the grey stand-in)" : "");
        DevLog($"pictures | the game's grey picture for a missing one is {DevTexture(pictures.DevStandIn)}; a line has room for {_list!.DevPictureRoom}");
        foreach (var (s, _, _, drawn) in _list.DevLines())
        {
            var fish = (s.Active.Recipe.RandomProducts?.Items ?? Enumerable.Empty<FishingProduct>()).Select(p => p.Item?.Icon).ToList();
            var meant = fish.Where(t => t != null && !StandIn(t)).Take(_list.DevPictureRoom).ToList();
            int without = fish.Count(t => t == null || StandIn(t));
            bool same = drawn.Count == meant.Count && drawn.Zip(meant).All(p => p.First?.GetInstanceId() == p.Second?.GetInstanceId());
            DevLog($"pictures | {s.Active.Recipe.Id}{(s.Locked ? " (locked)" : "")} | drawn [{string.Join(", ", drawn.Select(Picture))}]" +
                $" | the game's pictures of its fish [{string.Join(", ", fish.Select(Picture))}]" +
                (without > 0 ? $", {without} without a picture of its own: none drawn for {(without == 1 ? "it" : "them")}" : "") +
                (drawn.Any(StandIn) ? "  STAND-IN DRAWN" : "") + (same ? "" : "  MISMATCH") +
                $" | the spot's own {Picture(s.Active.Recipe.Icon)}");
        }
    }

    // Every spot of the game that is not listed, and why: what the game asks before it shows the spot or its location.
    void DevHidden()
    {
        var listed = new HashSet<ActiveFishingRecipe>(ReadSpots().Select(s => s.Active), ReferenceEqualityComparer.Instance);
        var action = _fishing!.Action;
        int hidden = 0;
        foreach (var (recipe, active) in action.Recipes)
        {
            if (listed.Contains(active)) continue;
            hidden++;
            FishingLocation location = active.Location;
            bool inConfig = _fishing.Config.Locations.Any(l => ReferenceEquals(l, location));
            bool inAction = action.Locations.TryGetValue(location, out ActiveFishingLocation? where);
            DevLog($"hidden | {recipe.Id} '{recipe.DisplayName.Text}' at {location.Id} '{location.DisplayName.Text}' | " +
                $"the spot is shown when: {DevConditions(recipe.DisplayRequirements)} | its location is shown when: {DevConditions(location.DisplayRequirements)} | " +
                $"to fish it: {DevConditions(recipe.Requirements)} | the location in the game's list {inConfig}, has the spot {(inAction ? where!.Recipes.Contains(active) : false)}");
        }
        DevLog($"hidden | {hidden} of the game's {action.Recipes.Count} spots are not listed");
    }

    static string DevConditions(ConditionGroup? group) => group == null || group.Conditions == null || group.Conditions.Count == 0 ? "always"
        : $"{(group.AreMet ? "met" : "NOT MET")}: " + string.Join("; ", group.Conditions.Select(c => $"{(c.IsMet ? "met" : "not met")}{(c.IsOr ? " (or)" : "")} '{DevWords(c.GetDescription(false, false))}'"));

    string DevOrder(string when)
    {
        string order = string.Join(", ", _list!.DevLines().Select(l => l.Spot.Active.Recipe.LocalId));
        DevLog($"order | {when} | {order}");
        return order;
    }

    string DevInSight()
    {
        Rect2 sight = _list!.DevScroll.GetGlobalRect();
        return string.Join(", ", _list.DevLines().Where(l => l.Button.IsVisibleInTree() && l.Button.GetGlobalRect().Intersects(sight)).Select(l => l.Spot.Active.Recipe.LocalId));
    }

    // The game's own labels for the spot the Fishing page shows, and for the spot buttons of its list, next to ours.
    void DevGame()
    {
        FishingRecipeView recipeView = RecipeView(ActionView(_page!));
        var action = _fishing!.Action;
        ActiveFishingRecipe? shown = ShownRecipe(recipeView);
        var lit = _list!.DevLines().Where(l => l.Spot.OnPage).Select(l => l.Spot.Active).ToList();
        bool litRight = shown != null && lit.Count == 1 && ReferenceEquals(lit[0], shown);
        DevLog($"lit | the page's own view shows {shown?.Recipe.Id ?? "none"}; the page's location {action.CurrentLocation?.Location.Id ?? "none"} has " +
            $"{action.CurrentLocation?.SelectedRecipe?.Recipe.Id ?? "none"} selected; the spot fished {action.CurrentRecipe?.Recipe.Id ?? "none"}, fishing now: {DevFishingNow()} | " +
            $"the lit line{(lit.Count == 1 ? " is" : "s are")} {(lit.Count == 0 ? "none" : string.Join(", ", lit.Select(a => a.Recipe.Id)))}" +
            $" (drawn lit: {string.Join(", ", _list.DevLines().Where(l => l.Drawn.EndsWith("lit=True")).Select(l => l.Spot.Active.Recipe.LocalId))})" +
            (litRight ? "" : "  MISMATCH"));
        if (shown == null) { DevLog("game | the Fishing page shows no spot."); return; }
        DevCompare("the page's spot", shown, Interval(recipeView), XpPanel(recipeView), FishChance(recipeView), JunkChance(recipeView));
        try
        {
            var buttons = SpotButtons(SpotSelect(recipeView));
            DevLog($"game | the game's spot list has {buttons.Count} buttons bound");
            foreach (var (spot, button) in buttons)
            {
                DevCompare("spot button", spot, Interval(button), XpPanel(button), FishChance(button), JunkChance(button));
                HBoxContainer fish = SpotFish(button);
                DevLog($"game | spot button {spot.Recipe.Id} | its name '{SpotLabel(button).Text}' | its fish [{DevDrawn(fish)}], {fish.GetThemeConstant("separation")} apart | " +
                    $"the unlocked part shown={SpotUnlocked(button).IsVisibleInTree()}, the locked part shown={SpotLocked(button).IsVisibleInTree()}");
            }
        }
        catch (Exception e) { DevLog("game | the game's spot list could not be read: " + e.Message); }

        FishingPrioritySelect priority = PrioritySelect(recipeView);
        TextTooltip? info = PriorityInfo(priority);
        DevLog($"priority | shown={priority.IsVisibleInTree()} | current '{CurrentPriority(priority).Text}' | spot's priority {shown.PriorityProduct?.Id ?? "none"}, " +
            $"requirements met={shown.Location.PriorityRequirements?.AreMet} | the game's words: '{DevTooltipText(info)}'");
        var baits = _fishing.BaitComponent;
        DevLog($"bait | the page shows '{(recipeView.BaitSelect == null ? "-" : CurrentBait(recipeView.BaitSelect).Text)}' | selected {baits.SelectedBait?.Id ?? "none"}, " +
            $"preservation {R(baits.Stats?.PreservationChance?.Total.Value ?? 0)}, in the bank {(baits.SelectedBait == null ? 0 : _bank?.Inventory.GetQuantity(baits.SelectedBait))}, " +
            $"bait items [{string.Join(", ", baits.BaitItems.Select(b => b.Id))}]");
        // What the page itself says about catches, such as how special items are rolled.
        var words = recipeView.FindChildren("*", "Label", true, false).OfType<Label>()
            .Where(l => l.IsVisibleInTree() && l.Text.Contains("catch", StringComparison.OrdinalIgnoreCase)).Select(l => $"'{l.Text}'").Distinct();
        DevLog($"page words | {string.Join(", ", words)}");
    }

    static string DevTooltipText(TextTooltip? tooltip) => tooltip == null ? "" : DevWords(string.Join(" / ", Content(tooltip) ?? []));

    // Pictures and words drawn inside a part of the game's screen, in order.
    static string DevDrawn(Node? box)
    {
        if (box == null) return "none";
        var found = new List<string>();
        void Walk(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is TextureRect picture) found.Add($"{DevTexture(picture.Texture)} drawn {picture.Size.X:0}x{picture.Size.Y:0}{(picture.IsVisibleInTree() ? "" : " (hidden)")}");
                else if (child is Label label) found.Add($"'{label.Text}'{(label.IsVisibleInTree() ? "" : " (hidden)")}");
                Walk(child);
            }
        }
        Walk(box);
        return string.Join(", ", found);
    }

    void DevCompare(string what, ActiveFishingRecipe spot, IntervalRangeDisplay interval, SkillXPPanel xp, PercentLabel fish, PercentLabel junk)
    {
        SpotRates ours = SpotRates.Read(spot, _fishing!, _bank, SpotRates.ShownOnPage(_fishing!), Shown);
        Int32FormattedLabel xpLabel = XpLabel(xp);
        bool timeSame = Math.Abs(interval.MinSeconds - ours.MinSeconds) < 0.0005 && Math.Abs(interval.MaxSeconds - ours.MaxSeconds) < 0.0005;
        // The game's tag is a whole number (Int32FormattedLabel), while its XP is not (ExperienceComponent.Xp is a Double).
        int down = (int)Math.Floor(ours.XpEach + 1e-9);
        string xpSame = xpLabel.Value == down ? Math.Abs(ours.XpEach - down) < 1e-9 ? "the same" : "the tag is ours rounded down" : "DIFFER";
        DevLog($"game | {what} {spot.Recipe.Id} '{ours.Name}' | time: game {R(interval.MinSeconds)}-{R(interval.MaxSeconds)} s '{IntervalLabel(interval).Text}', " +
            $"ours {R(ours.MinSeconds)}-{R(ours.MaxSeconds)} s, {R(ours.Seconds)} s on average{(timeSame ? "" : "  DIFFER")} | " +
            $"XP: game '{((Node)xpLabel as Label)?.Text}' ({xpLabel.Value}), ours {R(ours.XpEach)} each: {xpSame} | " +
            $"the game's XP tooltip '{DevTooltipText(XpTooltip(xp))}' | {DevXpPerSecond(DevTooltipText(XpTooltip(xp)), ours)} | " +
            $"fish: game '{fish.Text}' ({R(fish.Value)}), ours {R(ours.FishChance)} (raw {R(spot.Stats.FishChance.Value)}): {DevChanceSame(fish.Value, ours.FishChance)} | " +
            $"junk: game '{junk.Text}' ({R(junk.Value)}), ours {R(ours.JunkChance)} (raw {R(spot.Stats.JunkChance.Value)}): {DevChanceSame(junk.Value, ours.JunkChance)} | " +
            $"shown={interval.IsVisibleInTree()}");
    }

    static string DevChanceSame(double game, double ours) =>
        Math.Abs(game - ours) < 1e-6 ? "the same, a fraction" : Math.Abs(game - ours * 100) < 1e-4 ? "the same, the label keeps percent" : "DIFFER";

    // Which rule the game's own XP tooltip follows: every number in it against the XP per second over the middle of
    // the catch time range (ours), over its shortest time and over its longest.
    static string DevXpPerSecond(string tooltip, SpotRates ours)
    {
        var numbers = Regex.Matches(tooltip, @"\d[\d,]*(?:\.\d+)?").Select(m => double.Parse(m.Value.Replace(",", ""), CultureInfo.InvariantCulture)).ToList();
        if (!ours.HasTime || ours.MinSeconds <= 0) return $"numbers in it [{string.Join(", ", numbers.Select(R))}]";
        double middle = ours.XpEach / ours.Seconds, shortest = ours.XpEach / ours.MinSeconds, longest = ours.XpEach / ours.MaxSeconds;
        bool Near(double value) => numbers.Any(n => Math.Abs(n - value) <= Math.Max(0.006, 0.002 * value));
        string match = Near(middle) ? "XP per second over the middle of the range, ours" : Near(shortest) ? "XP per second over the shortest time"
            : Near(longest) ? "XP per second over the longest time" : "NO NUMBER IN IT MATCHES an XP per second of ours";
        return $"numbers in it [{string.Join(", ", numbers.Select(R))}]; XP per second over the middle {R(middle)}, the shortest {R(shortest)}, " +
            $"the longest {R(longest)}: it has {match}; ours a catch {R(ours.XpEach)} is {(Near(ours.XpEach) ? "in it" : "not in it")}";
    }

    // The location buttons at the top of the Fishing page: their names, their "spots unlocked" and the fish they show.
    void DevLocations()
    {
        try
        {
            var options = LocationOptions(LocationSelect(ActionView(_page!)));
            foreach (var (location, option) in options)
                DevLog($"locations | {location.Location.Id} '{LocationName(option).Text}' | spots '{((Node)SpotsUnlocked(option) as Label)?.Text}' | " +
                    $"locked part shown={LocationLocked(option).IsVisibleInTree()} | its fish [{DevDrawn(LocationFish(option))}] | " +
                    $"the game: {location.Recipes.Count} spots, products {location.AvailableProducts.Count}, unlocked [{string.Join(", ", location.UnlockedProducts.Select(i => i.Id))}]");
        }
        catch (Exception e) { DevLog("locations | could not be read: " + e.Message); }
    }

    // The Fishing page's own list of bonuses, and the names the tooltip took from it (BonusNames).
    void DevBonuses()
    {
        if (GameAccess.FindNode<FishingActionStatsView>(_page) is { } list)
        {
            var rows = list.FindChildren("*", "", true, false).OfType<ModifierStatLabel>()
                .Select(r => $"'{DevWords(r.StatName ?? "")}'{(r.IsVisibleInTree() ? "" : " (hidden)")}");
            DevLog($"page bonuses | {string.Join(", ", rows)}");
        }
        else DevLog("page bonuses | the page has no list of bonuses");
        var keys = ReadSpots().SelectMany(s => s.NotCounted).Select(b => b.Key).Distinct().ToList();
        DevLog($"bonus names | {(keys.Count == 0 ? "no bonus that is not counted" : string.Join(", ", keys.Select(k => _bonusNames.Of(k) is { } name ? $"{k}: the game's '{name}'" : $"{k}: NOT READ, the tooltip says '{BonusNames.Kind(k)}'")))}");
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

    // ---- the game's catches, listened to while the request runs -----------------------------------------------------

    sealed class DevCatchWatch
    {
        public readonly ulong Since = Time.GetTicksMsec();
        public FishingActionComponent Action = null!;
        public ExperienceComponent Experience = null!;
        public FishingBaitComponent Bait = null!;
        public EventHandler<RecipeFishedEventArgs> Fished = null!;
        public EventHandler<SpecialItemFishedEventArgs> Special = null!;
        public EventHandler<ValueChangedEventArgs<double>> Xp = null!;
        public EventHandler<BaitConsumedEventArgs> Used = null!, Kept = null!;
        public int Catches, Fish, Junk, MoreThanOne, Specials, XpChanges, BaitUsed, BaitKept;
        public double XpAdded;
        public readonly HashSet<string> Spots = new();
    }

    DevCatchWatch? _devWatch;

    void DevWatch()
    {
        DevUnwatch();
        var w = new DevCatchWatch { Action = _fishing!.Action, Experience = _fishing.Experience, Bait = _fishing.BaitComponent };
        w.Fished = (_, e) =>
        {
            w.Catches++;
            bool junk = ReferenceEquals(e.Item, e.Recipe?.Recipe.JunkItem);
            if (junk) w.Junk++; else w.Fish++;
            if (e.Quantity > 1) w.MoreThanOne++;
            w.Spots.Add(e.Recipe?.Recipe.Id ?? "?");
            DevLog($"catch | {e.Recipe?.Recipe.Id}: {e.Item?.Id} x{e.Quantity}{(junk ? " (junk)" : "")}");
        };
        w.Special = (_, e) =>
        {
            w.Specials++;
            DevLog($"catch | {e.Recipe?.Recipe.Id}: special item {e.SpecialItem?.Item?.Id} x{e.SpecialItem?.Quantity}");
        };
        w.Xp = (_, e) =>
        {
            w.XpChanges++;
            w.XpAdded += e.NewValue - e.OldValue;
            DevLog($"catch | Fishing XP +{R(e.NewValue - e.OldValue)}");
        };
        w.Used = (_, e) => { w.BaitUsed += e.Quantity; DevLog($"catch | bait {e.Item?.Id} used x{e.Quantity}"); };
        w.Kept = (_, e) => { w.BaitKept += e.Quantity; DevLog($"catch | bait {e.Item?.Id} preserved x{e.Quantity}"); };
        w.Action.RecipeFished += w.Fished;
        w.Action.SpecialItemFished += w.Special;
        w.Experience.XpChanged += w.Xp;
        w.Bait.BaitConsumed += w.Used;
        w.Bait.BaitPreserved += w.Kept;
        _devWatch = w;
    }

    void DevUnwatch()
    {
        if (_devWatch is not { } w) return;
        _devWatch = null;
        w.Action.RecipeFished -= w.Fished;
        w.Action.SpecialItemFished -= w.Special;
        w.Experience.XpChanged -= w.Xp;
        w.Bait.BaitConsumed -= w.Used;
        w.Bait.BaitPreserved -= w.Kept;
    }

    // What the game's catches brought, next to the mod's numbers for the spot fished.
    void DevCatches()
    {
        if (_devWatch is not { } w) return;
        double seconds = (Time.GetTicksMsec() - w.Since) / 1000.0;
        if (w.Catches == 0)
        {
            DevLog($"catches | none in {seconds:0} s (fishing now: {DevFishingNow()}): to check the hour against the game, start fishing by hand and send the request again.");
            return;
        }
        string ours = "";
        if (_fishing!.Action.CurrentRecipe is { } fished)
        {
            SpotRates s = SpotRates.Read(fished, _fishing, _bank, SpotRates.ShownOnPage(_fishing), Shown);
            double xpEach = w.XpAdded / w.Catches;
            ours = $" | ours at {fished.Recipe.Id}: XP {R(s.XpEach)} a catch, the game's {R(xpEach)} a catch: {(Math.Abs(xpEach - s.XpEach) < 0.01 ? "the same" : "DIFFER")}; " +
                $"fish on {R(s.FishChance)} of catches, junk on {R(s.JunkChance)}; doubling {R(s.Doubling)}; bait kept {R(s.BaitKept)}; " +
                $"one bait for every catch: {(w.BaitUsed + w.BaitKept == w.Catches ? "yes" : $"NO ({w.BaitUsed + w.BaitKept} for {w.Catches})")}";
        }
        DevLog($"catches | {w.Catches} in {seconds:0} s at [{string.Join(", ", w.Spots)}]: {w.Fish} fish, {w.Junk} junk, {w.MoreThanOne} of them more than one at a time; " +
            $"{w.Specials} special items; Fishing XP +{R(w.XpAdded)} in {w.XpChanges} changes; bait used {w.BaitUsed}, preserved {w.BaitKept}{ours}");
    }

    // ---- through the game's input, as a player's hand would --------------------------------------------------------

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
            DevLog("tooltip " + tooltip);
            Shot("tooltip");
            DevPointerBack();
            return true;
        }
        if (Time.GetTicksMsec() - _devTipSince < 20000) return false;
        DevLog($"tooltip | NOT SHOWN in 20 s with the pointer on {DevName(over)}; its words are in the 'row ... tooltip' lines, and there is no picture of it");
        DevPointerBack();
        return true;
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

    // A picture of the whole screen, and with crop one of the window alone. A picture of a window that is not there,
    // or of a list with no line in sight, is not taken.
    void Shot(string state, bool crop = false)
    {
        using var image = _ctx.Tree.Root.GetTexture().GetImage();
        void Save(Image picture, string file)
        {
            Error result = picture.SavePng(Path.Combine(_ctx.Directory, file));
            if (result != Error.Ok) throw new IOException(file + ": " + result);
            DevLog(file + HandNote);
        }
        if (crop)
        {
            if (_window is not { IsAlive: true, Visible: true } window) throw new InvalidOperationException("The window is not on the screen for " + state + ".");
            if (!window.Minimised && DevRowsInSight() == 0) throw new InvalidOperationException("No line of the list is in sight for " + state + ".");
            // The picture is in the screen's pixels, the window in the game's own, which the game stretches.
            var frame = _ctx.Tree.Root.GetFinalTransform() * window.Root.GetGlobalRect().Grow(10);
            var part = ((Rect2I)frame).Intersection(new Rect2I(0, 0, image.GetWidth(), image.GetHeight()));
            if (!part.HasArea()) throw new InvalidOperationException("The window is outside the picture.");
            Save(image, $"shot-fishing-{state}.png");
            using var region = image.GetRegion(part);
            Save(region, $"crop-fishing-{state}.png");
            return;
        }
        Save(image, $"shot-fishing-{state}.png");
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
        Error result = region.SavePng(Path.Combine(_ctx.Directory, "crop-fishing-strip.png"));
        if (result != Error.Ok) throw new IOException("crop-fishing-strip.png: " + result);
        DevLog($"crop-fishing-strip.png | the button at {_opener.GetGlobalRect()}, lit={_opener.ButtonPressed}, {strip.GetChildCount()} buttons in the strip{HandNote}");
    }
}
#endif
