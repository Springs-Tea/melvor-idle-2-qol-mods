#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Bank;
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
//   pwsh .claude\codex\check-mod.ps1 -Worktree <worktree> -Mod FishingRates -Request fishing.request -Produces fishing.done -Out <folder>
// The request file may say "fail=<step>": that step then fails on purpose, to show that a failed request puts
// everything back. "fail=click" fails right after the click on a line (the step "the spot opened"), so that what the
// click left on the page is put back by the final put-back (-Text 'fail=click').
// What it presses, always with the pointer (DevPress): the mod's own round button, fold button, X, column names and
// lines, and in step 7 the Fishing page's own location button and spot button for the spot a line opened, to compare;
// never the page's Start button, its bait or priority fish, or anything that starts or stops fishing. No game control
// is pressed through its signal or its handlers. A press is made in two steps (ItemEncyclopediaMod.DevAim,
// DevPressAimed): the pointer is put on the part of the control in sight, beside the mod's own window for a control of
// the game under it, and a later tick presses only when the game says the pointer is on that control. Nothing is
// pointed at or pressed while somebody is at the mouse (CombatGoals' WatchHand): a step waits until the real pointer has
// been still for 5 s, and after 90 s the request ends, which puts everything back. The keyboard focus is taken off a
// control right after the request's own press of it, if it has it (DevFocusOff). A picture of something just shown is
// taken two drawn frames later (DevFramesDrawn). The pictures go into the mod's folder (shot-fishing-*.png,
// crop-fishing-*.png).
// What it changes of the game: only the Fishing page's location and the spot selected at a location, in step 7, through
// the click on a line (FishingRatesMod.OpenOnPage) and the page's own buttons, and it puts them back through the
// members a click on a line uses (ActiveFishingLocation.SelectRecipe, FishingActionComponent.SetCurrentLocation). The
// rule of lesson 56, the product's own (SpotRates.FishingNow, FishesAt, ClickOpens), read again on the tick of every
// press and every put-back: while the character fishes (the action started or running), no spot is selected at the
// location he fishes at (the product's click on a line of that location only opens the location, a spot there is not
// put back: NOT PUT BACK), and the page's own location and spot buttons are not pressed at all. Woodcutting Rates' rule
// for what it puts back (lesson 54), part by part: a part is the page's location, or the spot selected at one location.
// What the request's own press changed is read once the page settled after it (DevSettle, two process frames), each
// part with what it was on the tick of the press; at the put-back each part is read again, and put back only while it
// is still exactly what the request left. A part the player changed is his alone: left, said in one line, never taken
// as the request's own; the other parts are still put back. A spot he picks at the location the page shows takes that
// location with it, as a category of his takes the scroll with it in Woodcutting Rates: the page is not moved from
// under the spot he chose. A part a call of the put-back moves becomes the request's and is put back too; when what a
// press left could not be read, the done line says that something of the request's MAY BE LEFT, never that nothing is.
// While a part of the request's selection stands on his page, nothing waits for his hand (lesson 57): a press that
// would come then is made only while nobody is at the mouse and within OwnedPressMost, otherwise it is not made and the
// selection is put back at once, and any other step that would wait for his hand puts it back first.
//  1. Remembers the page on the screen, whether the window was open and folded, the round button, settings.json as
//     it is on disk (the copy everything of the window is put back from), the Fishing page's location and every
//     location's selected spot, and what fishing is doing. Starts listening to the game's catches (only listening).
//  2. Opens the Fishing page the way the sidebar does, if another page is open, and reads back that it is shown;
//     stops with one line when Fishing cannot be opened that way (then open it by hand and send the request again).
//  3. "Dev: strip": every round button in the strip. Closes the window if it was open, presses the round button,
//     and opens the window up with its own button if it opened folded; the round button must be lit. A window that
//     is open again by the time the press would come (the player's own click while the step waited for his hand) is
//     not pressed: that press would close it.
//  4. "Dev: row": every line's numbers as the game gives them (raw) and as counted, what is drawn and its tooltip;
//     "Dev: pictures": what each line shows at its start, against the game's pictures of the spot's fish (the game's
//     grey picture for a missing one is never drawn: STAND-IN DRAWN or MISMATCH when the line is not as meant);
//     "Dev: hidden": every spot of the game that is not listed, and why; "Dev: size"; "Dev: lit": the spot the
//     page shows against the lit line; "Dev: game": the game's own labels for the page's spot and for every
//     button of its spot list (catch time, XP and its tooltip, fish and junk chance, their pictures) next to the
//     mod's numbers ("DIFFER" marks a difference); "Dev: locations"; "Dev: priority"; "Dev: bait": the selected
//     bait, the bank's count, the preservation chance with where it comes from in the game's own words, and what
//     the window counts of it; "Dev: page words", "Dev: page bonuses", "Dev: bonus names"; "Dev: tips": the
//     longest line of every tip against the 82 and 94 characters the other rate windows hold theirs to (LONGER
//     when one is longer). Two drawn frames later, "Dev: cut" over every label and name of the picture (CUT, with
//     "wider than its column" for a number of the list whose words are wider than its column; SHORTENED WITH ...,
//     PAST THE EDGE), crop-fishing-window.png, crop-fishing-strip.png, and crop-fishing-window-end.png with the list
//     scrolled to its end (with its own "Dev: cut"), so every line is in one of the pictures.
//  5. Rests the pointer on a line, the one of the spot being fished when there is one: "Dev: tooltip" and
//     shot-fishing-tooltip.png; then on the name over the Bait column (over Gold, the longest tip, when no bait is
//     selected): shot-fishing-bait-tooltip.png. Each picture is taken two drawn frames after the tooltip came up, and
//     its pixels where the tooltip lies are read against a picture of the screen taken just before the pointer went
//     onto the control, after any wait for his hand (VISIBLE or NOT VISIBLE). A tooltip that does not come up in 20 s
//     is one line, and the request goes on.
//  6. Clicks "Gold" (crop-fishing-sorted-gold.png), again (the other way round), then "Time"
//     (crop-fishing-sorted-time.png), each with "Dev: order"; a locked line in sight (crop-fishing-locked.png).
//  7. "Dev: click": clicks the line of a spot that can be fished, at another location than the page's when there is
//     one, and never one at the location he fishes at while he fishes. When that changed between the choice and the
//     press, the click is not made. Reads back the page's location and spot, what the page shows in its own words, the
//     lit line, the game's events and what fishing is doing (shot-fishing-clicked-spot.png). While he fishes, then
//     clicks a line at the location he fishes at, another spot than the one he fishes when there is one: by the
//     product's rule it only opens that location (the page shows the first click's location then) and selects no spot
//     there; read back the same way (shot-fishing-clicked-fished.png), "no spot selected, as meant" or A SPOT WAS
//     SELECTED WHERE HE FISHES (in the done line too), and NOT MADE when, on the tick of the press, the product's
//     click would select there. That click is made while the first click's selection stands, so it never waits for his
//     hand: NOT MADE and the selection put back at once otherwise. Puts the selection back. Then, only while he does
//     not fish and only from his own selection, presses the page's own location button and spot button for the same
//     spot with the pointer, and compares ("DIFFER", "EVENTS DIFFER"); a disabled button, one under the mod's window
//     or out of sight is not pressed, and the click is not compared. The location press waits for a still mouse; once
//     it is made, the request's selection stands on his page, and nothing waits for his hand any more: the spot button
//     is pressed only while nobody is at the mouse and within OwnedPressMost, and every way out of the comparison puts
//     the selection back at once. Then clicks a locked line (its location opens, its spot is not selected;
//     shot-fishing-clicked-locked.png) and puts the selection back again. A press that would come after the player
//     changed the page while it waited for his hand is not made (NOT MADE). CHANGED FISHING, FISHING STARTED or
//     FISHING STOPPED, NOT OPENED, THE PAGE SHOWS ANOTHER SPOT and THE WINDOW CLOSED mark what must not happen;
//     CHANGED FISHING also goes into the done line.
//  8. Folds the window with its button (crop-fishing-folded.png, the round button unlit) and opens it again;
//     builds it at the smallest and at the largest size (crop-fishing-smallest.png, crop-fishing-largest.png,
//     "Dev: size", which says LARGER or OVER THE BOTTOM BAR when it does not fit, and "Dev: cut"); closes it with
//     its X (shot-fishing-closed.png).
//  9. "Dev: catches": while the character is fishing, what the game's catches brought since the request began (up to
//     8 catches or 45 s): fish, junk, special items, how many at a time, the XP each catch added and the bait, next
//     to the mod's numbers. The XP a catch is compared spot by spot, for every spot a catch came from (the player may
//     change spots while the request runs). Read from the game's own events (RecipeFished, SpecialItemFished,
//     XpChanged, BaitConsumed, BaitPreserved, the bank's ItemQuantityChanged); nothing is changed. The bait is judged
//     catch by catch: the bank's change of the bait selected when the request began, in the game frame of each catch
//     (DevBaitWatch's rule: a catch's events come in one frame), against what the Bait column counts for that catch
//     (SpotRates.PreservedBaitStays); a change of the bank apart from a catch (bait bought or sold) is said and not
//     judged. "as the Bait column counts", or THE BAIT COLUMN COUNTS OTHERWISE. The bait watch (DevBaitWatch.cs) is
//     the longer look at the same question.
// 10. Puts back, each part in its own try with one line (a part that fails is NOT AS IT WAS, and the next part runs all
//     the same): the game's events no longer listened to; settings.json byte for byte from the copy (sort, size, place,
//     fold); the window open or closed as it was; the page's selection (by the rule above, a tick after the request's
//     last press settled; never a spot at the location he fishes at while he fishes); reads the Fishing page's
//     selection back while it is still on the screen; then the page that was on the screen, only when the request
//     opened Fishing itself and Fishing is still shown; the window on that page; the keyboard focus off the mod's own
//     controls; the pointer where the real one is.
// 11. Reads the rest back from the screen, the game and the disk, what fishing is doing too, and says in its last line
//     what was put back, with NOT AS IT WAS for anything that is not, and for any put-back on the way that was not, and
//     TO SELECT BY HAND for a selection of the request's still on his page. What the player changed himself is said as
//     his, once, and is no fault; a state a press of the request's left is never said as his.
// A step that fails jumps to 10: a failed request puts everything back too; a step of the put-back that fails is NOT AS
// IT WAS, and the put-back goes on. If the mod stops while a request runs, or another character is opened, the done file
// says so, settings.json is written back from the copy and read again by the window, and the done line names where the
// request left the selection of its character (DevStopped, DevReset).
public partial class FishingRatesMod
{
    const string RequestFile = "fishing.request", DoneFile = "fishing.done";
    // CombatGoals/DevWindowShots.cs: how long the real pointer must be still before a step points or presses, and
    // how long a step waits for that before the request ends.
    const ulong StillFor = 5000, GiveUpAfter = 90000, StepTime = 6000, PointerStepTime = GiveUpAfter + 10000;
    // The most a press may take while a part of the request's selection stands on his page (its settle, the aim and the
    // press): past it the press is not made and the selection is put back at once (Firemaking Rates' LogPressMost).
    const ulong OwnedPressMost = 5000;
    // A press of the request's that changes the page settles this many process frames after it, and what it left is read
    // then (Firemaking Rates' SettleFrames: process frames, so that a game folded down to the taskbar does not stall it).
    const long SettleFrames = 2;

    // The longest line of a column's tip and of a line's tooltip, in characters, that Mining, Woodcutting and Firemaking
    // Rates hold theirs to (their FishingTipMost, FishingTooltipMost): Fishing Rates' Gold tip, and its bait line at its
    // longest before issue #64 ("12.5% preserved", the longest bait name, "less than a minute"). A longer line here would
    // make their limit wrong. Godot's tooltip does not wrap: a longer line is a wider tooltip.
    const int TipMost = 82, TooltipMost = 94;

    readonly List<(string Name, Func<bool> Run, ulong Timeout)> _devSteps = new();
    int _devAt, _devPutBackAt;
    ulong _devStarted;
    long _devFrameMark;
    string? _devFailure, _devFailOnPurpose, _devPutBackWords;
    bool _devPutBackOk, _devPutBackRead, _devOpenedPage;
    // Put-backs on the way whose line says NOT AS IT WAS or NOT PUT BACK, presses of the request's that changed fishing
    // (CHANGED FISHING), and presses not made: the done line carries them, not only the log.
    readonly List<string> _devOnTheWay = new();
    // settings.json as the request found it on disk (null: not there), for DevReset and DevStopped too.
    string? _devSettingsCopy;
    bool _devSettingsRead;
    // The page's selection when the request began, for the read back.
    DevSelection? _devStart;
    // Woodcutting Rates' rule, part by part (the code review of turn 4, fault 2): the parts of the page's selection a press
    // of the request's changed, each with what it was just before that press (Origin, what its put-back goes back to) and
    // what the press left on it (Left, read once the page settled). A part is the page's location, or the spot selected at
    // one location. At every put-back and before every later press each part is read again: still exactly what the request
    // left, it is the request's and is put back; otherwise the player changed that part, and that part alone is his: left,
    // said in one line, never taken as the request's own. In the checks of this job every member call raised only its own
    // event (check-1 and check-2: "the page's location ...", "<location> selects ..."), so one call is not taken to move
    // another part; a put-back that does is said, NOT AS IT WAS (DevSelectionBack).
    sealed record DevOwned<T>(T Origin, T Left);
    DevOwned<ActiveFishingLocation?>? _devOwnLocation;
    readonly Dictionary<ActiveFishingLocation, DevOwned<ActiveFishingRecipe?>> _devOwnSpots = new(ReferenceEqualityComparer.Instance);
    // The page's selection on the tick of the request's last press, what the press changed is read against once it settled.
    DevSelection? _devPressFrom;
    // The parts the player changed while they were the request's, in words: his, left as he made them.
    readonly List<string> _devHisParts = new();
    // The spot selected at the location the page shows, as the request last read it while that location was its own (after
    // its press settled, and after a put-back): a spot he picks there takes the location with it (DevHisNow).
    ActiveFishingRecipe? _devShownSpot;
    // What a press of the request's left could not be read (a settle that threw), in words: something of the request's may
    // be left on his page, and the read back never says that nothing is.
    string? _devUnread;
    // What the last put-back before a wait for his hand said, so that a leftover that cannot be put back is carried into the
    // done line once, not at every new wait.
    string? _devLastNet;
    // A press of the request's that changes the page, not settled yet: the process frame it was made in.
    long? _devSettle;
    // The tick the last settled reading was taken in: a put-back reads the page again on a later tick.
    long _devTick, _devSettledTick = -1;
    // A press of the request's changed what fishing does: the read back never calls that state the player's.
    bool _devFishingChanged;
    // The control the request has just pressed with the pointer, and the drawn frame: once the press has been handled,
    // the keyboard focus is taken off it if it has it (Firemaking Rates' DevFocusOff; nothing is given back).
    (Control Target, long Frame)? _devFocusAfter;
    // The game's events about the page's selection and about fishing, heard while one click is made; taken off by a
    // switch of character and by Stop too.
    DevSelectionEvents? _devEvents;
    // The Fishing of the character the request runs for: after a switch, the done line names where its selection was left.
    FishingActionComponent? _devAction;

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
    // What a click on a line is compared with: the spot the action itself keeps as selected, and the page's own words for
    // its location and spot.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SelectedRecipe")]
    static extern ActiveFishingRecipe? ActionSelected(FishingActionComponent action);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrentLocationName")]
    static extern Label LocationLabel(FishingLocationSelect select);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RecipeName")]
    static extern Label RecipeName(FishingRecipeView view);

    void DevReset()
    {
        // Another character while a request runs: its listening is let go, settings.json is written back from the copy
        // (the window's settings are the same for every character), and the done line says what could not be put back.
        if (_devSteps.Count > 0)
        {
            DevUnwatch();
            DevEventsOff();
            string settings = DevSettingsBack();
            DevEnd($"stopped: another character was opened; put back: {settings}; not put back: {DevLeftOn()}, the window of the " +
                "character before (freed with it), its page and the pointer  NOT ALL AS IT WAS");
        }
        _devSteps.Clear();
        _devAt = 0;
        _devAction = null;
        _devSettle = null;
        _devFocusAfter = null;
        DevBaitWatchEnd("stopped: another character was opened");
    }

    // The mod stops (Stop) while a request runs: the done file says so, so that the check does not wait its whole time for
    // it, and settings.json is written back from the copy. Nothing else is touched: the game's parts may be gone.
    void DevStopped()
    {
        DevUnwatch();
        DevEventsOff();
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

    // Where the request left the Fishing page's selection of its own character, when it could not put it back: the game
    // keeps it in the character's Fishing (FishingActionComponent.CurrentLocation, ActiveFishingLocation.SelectedRecipe),
    // so it may stay there. Every part the request still owns is named, with what to select by hand.
    string DevLeftOn()
    {
        try
        {
            if (_devAction == null) return "the Fishing page's selection (no character read)";
            string owned = DevOwnsAny ? $"the request's own parts still on it: {DevOwnedWords(_devAction)}; TO SELECT BY HAND: {DevHand()}"
                : _devUnread != null ? "something of the request's MAY BE LEFT on it: what one of its presses left could not be read"
                : "nothing of the request's was on it";
            return $"the Fishing page's location and spots of the character the request ran for, now the location {DevId(_devAction.CurrentLocation)} " +
                $"({owned}), which the game may keep in that character's save";
        }
        catch (Exception e) { return $"the Fishing page's selection (not read: {e.GetType().Name})"; }
    }

    void DevEventsOff()
    {
        _devEvents?.Dispose();
        _devEvents = null;
    }

    void DevRequest()
    {
        if (GameAccess.IsComputingOffline || _stopped) return;
        // The bait watch only listens, and runs beside the check below or alone (DevBaitWatch.cs).
        DevBaitWatchTick();
        DevWatchHand();
        _devTick++;
        // Before any step: the keyboard focus off a control the request has just pressed, and what the request's own last
        // press left on the page, once the page has settled. Both run whatever step comes next, a failed one too.
        try { DevFocusOff(); }
        catch (Exception e) { _devFocusAfter = null; DevLog($"focus | not read: {e.GetType().Name}: {e.Message}"); }
        DevSettleNow();
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
                DevLog($"Fishing Rates check started, {_devPutBackAt} steps and the put-back; a step waits {StepTime / 1000} s at most, " +
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
                DevUnwatch();
                DevEventsOff();
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
            // next step (Firemaking Rates' Dev.cs; Woodcutting Rates' code review of turn 2, fault 4).
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
        DevLog("Fishing Rates check " + result);
        _devSteps.Clear();
        _devAt = 0;
        _devSettingsRead = false;
        _devSettingsCopy = null;
        _devTipBefore?.Dispose();
        _devTipBefore = null;
        File.WriteAllText(Path.Combine(_ctx.Directory, DoneFile), result);
    }

    // Read on every frame while a press of the request's waits to settle (OnFrame), and before every step, so that what
    // the request left is read a few frames after its press, not up to a tick later: a change of his in that gap would be
    // taken as the request's (Firemaking Rates' DevSettleNow).
    void DevSettleNow()
    {
        try { DevSettle(); }
        catch (Exception e)
        {
            // That press's changes are owned by nobody now, so they could be neither put back nor named: the done line says
            // that something of the request's may be left (the code review of turn 5, fault 3).
            _devSettle = null;
            _devPressFrom = null;
            _devUnread = $"what a press of the request's left could not be read ({e.GetType().Name}: {e.Message}): something of the request's MAY BE LEFT on the Fishing page, NOT AS IT WAS";
            DevLog("page | " + _devUnread);
            _devOnTheWay.Add(_devUnread);
        }
    }

    // What the request's own last press left of the page's selection, read once the page has settled, SettleFrames process
    // frames after the press. A part the press changed (against the page on the tick of the press) becomes the request's,
    // with what it was then as its origin; a part the request owns already keeps its origin and takes what the press left.
    // A part the press did not change, and that the request does not own, stays the player's.
    void DevSettle()
    {
        if (_devSettle is not { } frame || (long)Engine.GetProcessFrames() < frame + SettleFrames) return;
        _devSettle = null;
        _devSettledTick = _devTick;
        if (_fishing == null || !ReferenceEquals(_fishing.Action, _devAction)) return;
        DevSelection now = DevSelectionNow(), from = _devPressFrom ?? now;
        _devPressFrom = null;
        if (_devOwnLocation is { } location) _devOwnLocation = location with { Left = now.Location };
        else if (!ReferenceEquals(now.Location, from.Location)) _devOwnLocation = new(from.Location, now.Location);
        foreach (var (place, spot) in now.Spots)
        {
            if (_devOwnSpots.TryGetValue(place, out var own)) _devOwnSpots[place] = own with { Left = spot };
            else if (DevSpotOf(from, place) is var was && !ReferenceEquals(was, spot)) _devOwnSpots[place] = new(was, spot);
        }
        _devShownSpot = now.Location?.SelectedRecipe;
        DevLog($"page | settled after the request's press: the request owns {(DevOwnsAny ? DevOwnedWords(_fishing.Action) : "nothing of the page (the press changed nothing)")}");
    }

    // The keyboard focus off the control the request has just pressed, if that control (or a part of it) has it, once the
    // press has been handled: a key of his would press it again (Firemaking Rates' DevFocusOff). Nothing is given back.
    void DevFocusOff()
    {
        if (_devFocusAfter is not { } after || (long)Engine.GetFramesDrawn() < after.Frame + 1) return;
        _devFocusAfter = null;
        Control? owner = _ctx.Tree.Root.GuiGetFocusOwner();
        if (owner == null || !GodotObject.IsInstanceValid(after.Target) || (owner != after.Target && !after.Target.IsAncestorOf(owner))) return;
        owner.ReleaseFocus();
        DevLog($"focus | the keyboard focus taken off {DevName(owner)}, which the request's press gave it");
    }

    // Two frames drawn since the step began to wait: the picture is the frame the game drew last, so a picture in the
    // same tick as a change never holds it (Mining Rates' Dev.cs, DevFramesDrawn; lesson 43).
    bool DevFramesDrawn()
    {
        long now = (long)Engine.GetFramesDrawn();
        if (_devFrameMark == 0) { _devFrameMark = now; return false; }
        return now >= _devFrameMark + 2;
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
        _devPutBackOk = _devPutBackRead = _devOpenedPage = false;
        _devGuardSince = _devTipSince = 0;
        _devFrameMark = 0;
        _devAim = null;
        _devMisses = 0;
        _devOnTheWay.Clear();
        _devOwnLocation = null;
        _devOwnSpots.Clear();
        _devPressFrom = null;
        _devHisParts.Clear();
        _devShownSpot = null;
        _devUnread = _devLastNet = null;
        _devFishingChanged = false;
        _devSettle = null;
        _devSettledTick = -1;
        _devFocusAfter = null;
        DevEventsOff();
        _devTipBefore?.Dispose();
        _devTipBefore = null;
        _devStarted = Time.GetTicksMsec();
        GameView view = GameAccess.FindGameView() ?? throw new InvalidOperationException("No character is open.");
        _devAction = _fishing!.Action;
        // settings.json as it is on disk, the copy everything of the window is put back from (lesson 50).
        _devSettingsCopy = File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : null;
        _devSettingsRead = true;
        var before = new DevBefore(CurrentPage(view)?.Item1, _wanted, _window is { IsAlive: true, Visible: true },
            _window is { IsAlive: true } ? _window.Minimised : null, GodotObject.IsInstanceValid(_opener) ? _opener!.ButtonPressed : null,
            _devSettingsCopy);
        Button? target = null;
        string firstBefore = "";
        void Add(string name, Func<bool> run, ulong timeout = StepTime) => _devSteps.Add((name, run, timeout));

        var action = _fishing!.Action;
        // The Fishing page's selection, which the clicks on lines change and the put-back puts back, and what fishing is
        // doing, which nothing of the request may change.
        _devStart = DevSelectionNow();
        string fishingAtStart = DevFishingState();
        DevLog($"before: page {before.Page?.Id ?? "none"}, window {(before.Wanted ? "open" : "closed")}{(before.Shown ? " and on the screen" : "")}" +
            $"{(before.Folded == true ? ", folded" : "")}, round button {(before.Lit is { } lit ? lit ? "lit" : "unlit" : "not made yet")}, settings.json " +
            (before.Settings == null ? "not there" : $"{before.Settings.Length} characters: {before.Settings}") +
            $" | fishing now: {DevFishingNow()}, the spot fished {action.CurrentRecipe?.Recipe.Id ?? "none"}, the page's location " +
            $"{action.CurrentLocation?.Location.Id ?? "none"} and its spot {action.CurrentLocation?.SelectedRecipe?.Recipe.Id ?? "none"}, " +
            $"the action's own selected spot {_devStart.ActionSpot?.Recipe.Id ?? "none"}, every location's spot [{DevSpots(_devStart)}] | " +
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
        // Read again on every tick until the press: a window the player opened himself while the step waited for his hand
        // would be closed by it (check-3 of fishing-bait: "pressed the round button" after a 50 s wait, then the window
        // closed and 'opened' timed out).
        Add("press the round button", () =>
        {
            if (!IsOpen) return DevPress(_opener!, "the round button");
            DevLog("the window is open again without the request's press: the player opened it while the step waited, so the round button is not pressed");
            return true;
        }, PointerStepTime);
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
            DevTips("open");
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
        Button? header = null;
        Add("point at a line", () =>
        {
            if (_list!.DevScroll.ScrollVertical != 0) return false;
            var lines = _list.DevLines().ToList();
            // The spot being fished first, then the one the page shows, then any that can be fished: the bait lines of its
            // tooltip then hold the numbers of what the character is doing.
            ActiveFishingRecipe? fished = DevFishingNow() ? _fishing!.Action.CurrentRecipe : null;
            target = (lines.FirstOrDefault(l => fished != null && ReferenceEquals(l.Spot.Active, fished)).Button
                ?? lines.FirstOrDefault(l => l.Spot.OnPage && !l.Spot.Locked).Button
                ?? lines.FirstOrDefault(l => !l.Spot.Locked).Button ?? lines.FirstOrDefault().Button)
                ?? throw new InvalidOperationException("No line to point at.");
            DevLog($"tooltip | the line pointed at: {lines.First(l => l.Button == target).Spot.Active.Recipe.Id}" +
                $" (the spot being fished: {fished?.Recipe.Id ?? "none"}, the bait selected: {_fishing!.BaitComponent.SelectedBait?.Id ?? "none"})");
            _list.DevScroll.EnsureControlVisible(target);
            _devTipSince = 0;
            return true;
        });
        Add("tooltip", () => DevTooltip(target!, "tooltip"), PointerStepTime);
        // The name over the Bait column; without a bait, the name whose tip is the longest (Gold), whose tooltip is the
        // widest of the names'.
        Add("point at a column's name", () =>
        {
            header = _list!.DevHeader("bait");
            if (header == null)
            {
                header = _list.DevHeaders.OrderByDescending(h => h.TooltipText.Split('\n').Max(l => l.Length)).First();
                DevLog($"tooltip | no bait is selected on the Fishing page, so the window has no Bait column: the name with the longest tip, '{header.Text}', instead");
            }
            else DevLog($"tooltip | the name over the Bait column");
            _devTipSince = 0;
            return true;
        });
        Add("the name's tooltip", () => DevTooltip(header!, header!.GetMeta("column").AsString() == "bait" ? "bait-tooltip" : "header-tooltip"),
            PointerStepTime);
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
            if (_settings.Sort != "gold" || (_settings.Sort, _settings.Descending) == sortWas || !DevFramesDrawn()) return false;
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
            if (_settings.Sort != "time" || !DevFramesDrawn()) return false;
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
                if (!locked.GetGlobalRect().Intersects(_list.DevScroll.GetGlobalRect()) || !DevFramesDrawn()) return false;
                DevLog($"locked | in sight: {DevInSight()}");
                Shot("locked", crop: true);
            }
            return true;
        });

        // Step 7. A click on a line whose spot can be fished, preferably at another location than the page's so that both of
        // the page's changes are made, never at the location he fishes at while he fishes (lesson 56); then, only while he
        // does not fish and from his own selection, the page's own two buttons for the same spot, to compare; then a click
        // on a locked line. The selection is put back after each, by the rule of DevSelectionBack.
        SpotRates? clicked = null;
        string fishingBefore = "", ours = "", ourEvents = "";
        bool compareSkipped = false, gamePressed = false;
        ActiveFishingLocation? comparePlace = null;
        // A fresh ear for the game's events, on every tick until a press is made, so that what the player does while the
        // press waits for his hand is never heard as the press's.
        void EventsOn()
        {
            DevEventsOff();
            _devEvents = new DevSelectionEvents(_fishing!.Action);
        }
        // A put-back on the way: one line, and in the done line when it is not as it was (DevPutBackNow).
        void PageBack(string head) => DevPutBackNow(head);
        // What fishing does, before a press of the request's and after it settled: a change is the press's, a fault of the
        // request's (CHANGED FISHING), never the player's in the read back.
        string FishingMarks(string before, string now, string what)
        {
            if (now == before) return "";
            _devFishingChanged = true;
            _devOnTheWay.Add($"{what} CHANGED FISHING ({before}; now {now})");
            return "  FISHING CHANGED";
        }
        Add("a line to click", () =>
        {
            var lines = _list!.DevLines().Select(l => l.Spot).ToList();
            bool fishing = DevFishing();
            var open = lines.Where(s => s.Click == SpotRates.Opens.Spot && !s.OnPage && DevMaySelectAt(s.Active)).ToList();
            clicked = open.FirstOrDefault(s => !s.LocationOnPage) ?? open.FirstOrDefault();
            if (clicked == null)
            {
                DevLog($"click | no line opens another spot than the page's{(fishing ? $" outside the location fished ({DevId(DevFishedAt())})" : "")}: none is clicked.");
                return true;
            }
            _list.DevScroll.EnsureControlVisible(DevLineOf(clicked));
            DevLog($"click | the line of {clicked.Active.Recipe.Id} at {clicked.Active.Location.Id}; fishing now: {fishing}" +
                (fishing ? $" at {DevId(DevFishedAt())}, so no line of that location is clicked" : "") + $"; before: {DevClickResult()}; {DevFishingState()}");
            return true;
        });
        Add("click the line", () =>
        {
            if (clicked == null) return true;
            // Read again on the tick of the press: when he started fishing at the line's location since it was chosen, its
            // click would select a spot where he fishes, and it is not made.
            if (!DevMaySelectAt(clicked.Active))
            {
                string words = $"the click on the line of {clicked.Active.Recipe.Id} NOT MADE: the character fishes at its location now";
                if (_devAim != null) { _devAim = null; DevPointerBack(); }
                DevLog("click | " + words);
                _devOnTheWay.Add(words);
                DevEventsOff();
                clicked = null;
                return true;
            }
            string words2 = DevClickResult();
            fishingBefore = DevFishingState();
            EventsOn();
            SpotRates line = clicked;
            bool? pressed = DevPagePress(() => DevClickLine(line), $"the click on the line of {line.Active.Recipe.Id}");
            if (pressed == true) DevLog($"click | just before the press: {words2}; {fishingBefore}");
            if (pressed != null) return pressed.Value;
            DevEventsOff();
            clicked = null;
            return true;
        }, PointerStepTime);
        Add("the spot opened", () =>
        {
            if (clicked == null) return true;
            // What the click left is read once the page has settled (DevSettle), and the put-back reads it again a tick later.
            if (_devSettle != null || _devSettledTick >= _devTick || !DevFramesDrawn()) return false;
            var action = _fishing!.Action;
            ActiveFishingRecipe spot = clicked.Active;
            bool opened = ReferenceEquals(action.CurrentLocation?.Location, spot.Location) && ReferenceEquals(action.CurrentLocation?.SelectedRecipe, spot);
            Refresh();
            var lit = _list!.DevLines().Where(l => l.Spot.OnPage).Select(l => l.Spot.Active.Recipe.Id).ToList();
            string fishing = DevFishingState();
            ours = DevClickResult();
            ourEvents = _devEvents == null ? "" : string.Join(", ", _devEvents.Seen);
            DevLog($"click | after the click on the line of {spot.Recipe.Id}: {ours}; the game's events [{ourEvents}]; {fishing}" +
                (opened ? "" : "  NOT OPENED") + FishingMarks(fishingBefore, fishing, $"the click on the line of {spot.Recipe.Id}") +
                (ReferenceEquals(ShownRecipe(RecipeView(ActionView(_page!))), spot) ? "" : "  THE PAGE SHOWS ANOTHER SPOT") +
                (lit.SequenceEqual([spot.Recipe.Id]) ? "" : $"  THE LIT LINE IS [{string.Join(", ", lit)}]") +
                (IsOpen ? "" : "  THE WINDOW CLOSED"));
            Shot("clicked-spot");
            DevEventsOff();
            return true;
        });
        // While he fishes: a click on a line at the location he fishes at, which by the product's rule (SpotRates.ClickOpens)
        // only opens that location, when the page shows another, and selects no spot there. It is made right after the
        // click above, while the page still shows that click's location, so that the opening is seen too; its put-back is
        // the one below. Read again on the tick of the press: the line's click as the product reads it on the click must
        // select nothing, or the click is not made.
        SpotRates? there = null;
        ActiveFishingLocation? fishedPlace = null;
        ActiveFishingRecipe? fishedSpotBefore = null;
        bool thereOpens = false;
        Add("a line where he fishes", () =>
        {
            fishedPlace = DevFishedAt();
            if (!DevFishing() || fishedPlace == null)
            {
                DevLog($"click | where he fishes: {(DevFishing() ? "he fishes, but the game does not say where" : "he does not fish")}, so no line of the location he fishes at is clicked");
                return true;
            }
            ActiveFishingRecipe? fished = _fishing!.Action.CurrentRecipe;
            var lines = _list!.DevLines().Select(l => l.Spot).Where(s => s.FishingHere && !s.Locked).ToList();
            // Another spot than the one he fishes, so that a selection would show; else the one he fishes.
            there = lines.FirstOrDefault(s => !ReferenceEquals(s.Active, fished)) ?? lines.FirstOrDefault();
            if (there == null) { DevLog($"click | where he fishes: no line of {fishedPlace.Location.Id} is listed, so none is clicked"); return true; }
            _list.DevScroll.EnsureControlVisible(DevLineOf(there));
            string sentence = DevLineOf(there).TooltipText.Split('\n').LastOrDefault() ?? "";
            DevLog($"click | where he fishes: the line of {there.Active.Recipe.Id} at {fishedPlace.Location.Id}, where he fishes {DevId(fished)}; a click opens " +
                $"{there.Click}, clickable={there.Clickable}; its tooltip's last line '{sentence}'; before: {DevClickResult()}; {DevFishingState()}");
            return true;
        });
        Add("click the line where he fishes", () =>
        {
            if (there == null) return true;
            SpotRates.Opens opens = SpotRates.ClickOpens(there.Active, SpotRates.FishingNow(_fishing!, _game));
            if (opens == SpotRates.Opens.Spot)
            {
                string words = $"the click on the line of {there.Active.Recipe.Id} NOT MADE: he no longer fishes at its location, so the click would select its spot";
                if (_devAim != null) { _devAim = null; DevPointerBack(); }
                DevLog("click | " + words);
                _devOnTheWay.Add(words);
                DevEventsOff();
                there = null;
                return true;
            }
            string words2 = DevClickResult();
            fishingBefore = DevFishingState();
            fishedSpotBefore = fishedPlace!.SelectedRecipe;
            thereOpens = opens == SpotRates.Opens.Location && !ReferenceEquals(_fishing!.Action.CurrentLocation, fishedPlace);
            EventsOn();
            SpotRates line = there;
            bool? pressed = DevPagePress(() => DevClickLine(line), $"the click on the line of {line.Active.Recipe.Id}, where he fishes");
            if (pressed == true) DevLog($"click | where he fishes, just before the press: {words2}; {fishingBefore}; a click opens its location: {thereOpens}");
            if (pressed != null) return pressed.Value;
            DevEventsOff();
            there = null;
            return true;
        }, PointerStepTime);
        // A spot selected at the location he fishes at, by the product's click: the breach of lesson 56 this click is made to
        // show never happens, so it goes into the done line too.
        string SpotSelectedThere(ActiveFishingRecipe? now)
        {
            string words = $"the click on the line of {there!.Active.Recipe.Id}: A SPOT WAS SELECTED WHERE HE FISHES ({fishedPlace!.Location.Id}: {DevId(now)}, was {DevId(fishedSpotBefore)})";
            _devOnTheWay.Add(words + ", NOT AS IT WAS");
            return $"A SPOT WAS SELECTED WHERE HE FISHES (was {DevId(fishedSpotBefore)})";
        }
        Add("the location where he fishes", () =>
        {
            if (there == null) return true;
            if (_devSettle != null || _devSettledTick >= _devTick || !DevFramesDrawn()) return false;
            var action = _fishing!.Action;
            string fishing = DevFishingState();
            bool on = ReferenceEquals(action.CurrentLocation, fishedPlace);
            ActiveFishingRecipe? spotNow = fishedPlace!.SelectedRecipe;
            DevLog($"click | after the click on the line of {there.Active.Recipe.Id}, where he fishes: {DevClickResult()}; the game's events " +
                $"[{(_devEvents == null ? "" : string.Join(", ", _devEvents.Seen))}]; {fishing} | " +
                (thereOpens ? on ? $"{fishedPlace.Location.Id} opened" : $"{fishedPlace.Location.Id} NOT OPENED" : "the page was on that location: nothing to open") +
                $"; its spot {DevId(spotNow)}: " + (ReferenceEquals(spotNow, fishedSpotBefore) ? "no spot selected, as meant" : SpotSelectedThere(spotNow)) +
                FishingMarks(fishingBefore, fishing, $"the click on the line of {there.Active.Recipe.Id}, where he fishes") + (IsOpen ? "" : "  THE WINDOW CLOSED"));
            Shot("clicked-fished");
            DevEventsOff();
            return true;
        });
        Add("the page back after the clicks", () =>
        {
            if (clicked == null && there == null) return true;
            if (_devSettle != null || _devSettledTick >= _devTick) return false;
            PageBack("the page's selection put back for the game's own buttons:");
            return true;
        });
        // Once the comparison's location press is made, the request's selection stands on his page until it is put back.
        // From then on nothing waits for his hand, and every way out of the comparison puts the selection back at once,
        // through the members a click on a line uses, which need no free mouse (Firemaking Rates' GiveUpComparison).
        void GiveUpComparison(string why)
        {
            compareSkipped = true;
            if (_devAim != null) { _devAim = null; DevPointerBack(); }
            DevEventsOff();
            if (!DevOwnsAny) { DevLog($"click | {why}; nothing of the request's is on the page"); return; }
            PageBack($"{why}; the selection put back at once:");
        }
        Add("the game's location button", () =>
        {
            if (clicked == null) return true;
            // Lesson 56, read on every tick of this step, the tick of the press too: the page's own buttons select, and
            // what selecting does to a running catch is never found out on his character.
            if (DevFishing())
            {
                compareSkipped = true;
                if (_devAim != null) { _devAim = null; DevPointerBack(); }
                DevLog("click | the character fishes: the page's own location and spot buttons are not pressed, and the click is not compared with them");
                return true;
            }
            // He is using the page: the comparison, a development check only, is not made over it.
            if (_devHisParts.Count > 0)
            {
                compareSkipped = true;
                DevLog("click | the player changed the page meanwhile: the page's own buttons are not pressed, and the click is not compared");
                return true;
            }
            // The comparison starts only from his own selection: while the click's is not back yet, it is not made.
            if (DevOwnsAny)
            {
                compareSkipped = true;
                DevLog("click | the click's selection is not back on the page yet: the page's own buttons are not pressed, and the click is not compared; the final put-back tries again");
                return true;
            }
            var action = _fishing!.Action;
            if (!action.Locations.TryGetValue(clicked.Active.Location, out ActiveFishingLocation? place))
                throw new InvalidOperationException($"the game has no location {clicked.Active.Location.Id}");
            comparePlace = place;
            if (ReferenceEquals(action.CurrentLocation, place))
            {
                DevLog($"click | the page is on {place.Location.Id} already: its own location button is not pressed");
                return true;
            }
            TextureButton button = DevLocationButton(place);
            // Disabled and shown read on every tick of this step, the tick of the press too.
            if (button.Disabled || !button.IsVisibleInTree())
            {
                compareSkipped = true;
                if (_devAim != null) { _devAim = null; DevPointerBack(); }
                DevLog($"click | the page's own location button of {place.Location.Id} is {(button.Disabled ? "disabled" : "not shown")}: it is not pressed, and the click is not compared");
                return true;
            }
            if (_devAim != button)
            {
                if (!DevSight(button).HasArea())
                {
                    compareSkipped = true;
                    DevLog($"click | the page's own location button of {place.Location.Id} lies under the mod's own window or out of sight: it is not pressed, and the click is not compared");
                    return true;
                }
            }
            fishingBefore = DevFishingState();
            EventsOn();
            bool? pressed = DevPagePress(() => DevPress(button, $"the page's own location button of {place.Location.Id}"),
                $"the press of the page's own location button of {place.Location.Id}, to compare");
            if (pressed == true) gamePressed = true;
            if (pressed != null) return pressed.Value;
            GiveUpComparison($"the press of the page's own location button of {place.Location.Id} was not made");
            return true;
        }, PointerStepTime);
        Add("the game's spot button", () =>
        {
            if (clicked == null || compareSkipped) return true;
            if (_devSettle != null || _devSettledTick >= _devTick) return false;
            var action = _fishing!.Action;
            ActiveFishingRecipe spot = clicked.Active;
            ActiveFishingLocation place = comparePlace ?? throw new InvalidOperationException("no location to compare at");
            // The request's location stands on his page (its location press): no waiting on his hand from here on.
            bool stands = DevOwnsAny;
            // Lesson 56, read again on every tick of this step, the tick of the press too.
            if (DevFishing()) { GiveUpComparison($"the character fishes now: the page's own spot button of {spot.Recipe.Id} is not pressed, and the click is not compared"); return true; }
            if (!ReferenceEquals(action.CurrentLocation, place))
            {
                GiveUpComparison($"the page shows the location {DevId(action.CurrentLocation)}, not {place.Location.Id}: the spot button is not there to press, and the click is not compared");
                return true;
            }
            if (ReferenceEquals(place.SelectedRecipe, spot))
            {
                DevLog($"click | {place.Location.Id} has {spot.Recipe.Id} selected already: its own spot button is not pressed");
                return true;
            }
            var buttons = SpotButtons(SpotSelect(RecipeView(ActionView(_page!))));
            if (!buttons.TryGetValue(spot, out FishingSpotSelectButton? button) || !GodotObject.IsInstanceValid(button) || button.IsQueuedForDeletion())
            {
                GiveUpComparison($"the page's spot list has no button for {spot.Recipe.Id}: it is not pressed, and the click is not compared");
                return true;
            }
            // Disabled and shown read on every tick of this step, the tick of the press too.
            if (button.Disabled || !button.IsVisibleInTree())
            {
                GiveUpComparison($"the page's own spot button of {spot.Recipe.Id} is {(button.Disabled ? "disabled" : "not shown")}: it is not pressed, and the click is not compared");
                return true;
            }
            if (_devAim != button)
            {
                if (!DevSight(button).HasArea())
                {
                    GiveUpComparison($"the page's own spot button of {spot.Recipe.Id} lies under the mod's own window or out of sight: it is not pressed, and the click is not compared");
                    return true;
                }
            }
            if (stands && DevHandNow())
            {
                GiveUpComparison($"somebody is at the mouse: the page's own spot button of {spot.Recipe.Id} is not pressed, so that the request's selection does not wait on his page, and the click is not compared");
                return true;
            }
            if (stands && Time.GetTicksMsec() - _devStarted > OwnedPressMost)
            {
                GiveUpComparison($"the page's own spot button of {spot.Recipe.Id} could not be pressed within {OwnedPressMost / 1000} s: the click is not compared");
                return true;
            }
            // From his own selection (no location press was needed) the ear is fresh on every tick until the press; after the
            // location press it keeps what that press brought.
            if (!stands)
            {
                fishingBefore = DevFishingState();
                EventsOn();
            }
            bool? pressed = DevPagePress(() => DevPress(button, $"the page's own spot button of {spot.Recipe.Id}"),
                $"the press of the page's own spot button of {spot.Recipe.Id}, to compare");
            if (pressed == true) gamePressed = true;
            if (pressed != null) return pressed.Value;
            GiveUpComparison($"the press of the page's own spot button of {spot.Recipe.Id} was not made");
            return true;
        }, PointerStepTime);
        Add("the game's buttons compared", () =>
        {
            if (clicked == null || compareSkipped) return true;
            if (!gamePressed)
            {
                DevLog("click | none of the page's own buttons needed a press: nothing to compare");
                return true;
            }
            if (_devSettle != null || _devSettledTick >= _devTick || !DevFramesDrawn()) return false;
            string game = DevClickResult(), gameEvents = _devEvents == null ? "" : string.Join(", ", _devEvents.Seen);
            string fishing = DevFishingState();
            DevLog($"click | after the page's own buttons: {game}; the game's events [{gameEvents}]; {fishing}" +
                FishingMarks(fishingBefore, fishing, "the press of the page's own buttons") + " | " +
                (game == ours ? "the page and the action as after the click on the line" : $"DIFFER from the click on the line ({ours})") + "; " +
                (DevSameEvents(gameEvents, ourEvents) ? "the same events" : $"EVENTS DIFFER from the click on the line ([{ourEvents}])"));
            DevEventsOff();
            PageBack("the page's selection put back:");
            return true;
        });
        SpotRates? locked = null;
        Add("a locked line to click", () =>
        {
            var lines = _list!.DevLines().Select(l => l.Spot).ToList();
            locked = lines.FirstOrDefault(s => s.Locked && s.Click == SpotRates.Opens.Location && !s.LocationOnPage) ?? lines.FirstOrDefault(s => s.Locked);
            if (locked == null) { DevLog("click | no locked line: none is clicked."); return true; }
            _list.DevScroll.EnsureControlVisible(DevLineOf(locked));
            var place = _fishing!.Action.Locations[locked.Active.Location];
            TextureButton button = DevLocationButton(place);
            DevLog($"click | the locked line of {locked.Active.Recipe.Id} at {place.Location.Id}: a click opens {locked.Click}" +
                $"{(locked.LocationOnPage ? ", the page is on its location already" : "")}; the page's own location button disabled={button.Disabled}" +
                (button.Disabled == (locked.Click == SpotRates.Opens.Nothing) ? "" : "  NOT AS THE GAME'S BUTTON") +
                $"; before: {DevClickResult()}; {DevFishingState()}");
            return true;
        });
        DevSelection? lockedFrom = null;
        Add("click the locked line", () =>
        {
            if (locked == null) return true;
            lockedFrom = DevSelectionNow();
            string words = DevClickResult();
            fishingBefore = DevFishingState();
            EventsOn();
            SpotRates line = locked;
            bool? pressed = DevPagePress(() => DevClickLine(line), $"the click on the locked line of {line.Active.Recipe.Id}");
            if (pressed == true) DevLog($"click | just before the press: {words}; {fishingBefore}");
            if (pressed != null) return pressed.Value;
            DevEventsOff();
            locked = null;
            return true;
        }, PointerStepTime);
        Add("the locked line's location", () =>
        {
            if (locked == null) return true;
            if (_devSettle != null || _devSettledTick >= _devTick || !DevFramesDrawn()) return false;
            var action = _fishing!.Action;
            ActiveFishingRecipe spot = locked.Active;
            ActiveFishingLocation place = action.Locations[spot.Location];
            DevSelection from = lockedFrom ?? _devStart!;
            bool opens = locked.Click == SpotRates.Opens.Location && !locked.LocationOnPage, on = ReferenceEquals(action.CurrentLocation, place);
            ActiveFishingRecipe? was = from.Spots.FirstOrDefault(s => ReferenceEquals(s.Location, place)).Spot;
            string fishing = DevFishingState();
            DevLog($"click | after the click on the locked line of {spot.Recipe.Id}: {DevClickResult()}; the game's events [{(_devEvents == null ? "" : string.Join(", ", _devEvents.Seen))}]; {fishing} | " +
                (opens ? on ? "its location opened" : "its location NOT OPENED" : ReferenceEquals(action.CurrentLocation, from.Location) ? "nothing changed, as meant" : "THE PAGE CHANGED") +
                $"; its location's spot {place.SelectedRecipe?.Recipe.Id ?? "none"}: " +
                (ReferenceEquals(place.SelectedRecipe, was) ? "as before" : ReferenceEquals(place.SelectedRecipe, spot) ? "THE LOCKED SPOT WAS SELECTED" : "CHANGED") +
                FishingMarks(fishingBefore, fishing, $"the click on the locked line of {spot.Recipe.Id}") + (IsOpen ? "" : "  THE WINDOW CLOSED"));
            Shot("clicked-locked");
            DevEventsOff();
            PageBack("the page's selection put back:");
            return true;
        });
        Add("fold", () => DevPress(DevFoldButton(), "the window's fold button"), PointerStepTime);
        Add("folded", () =>
        {
            if (_window?.Minimised != true || !DevFramesDrawn()) return false;
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
            if (!IsOpen || _fitPending || !DevFramesDrawn()) return false;
            DevSize("smallest");
            DevCut("smallest");
            Shot("smallest", crop: true);
            Resize(GameWindow.MaxScale);
            return true;
        });
        Add("largest", () =>
        {
            if (!IsOpen || _fitPending || !DevFramesDrawn()) return false;
            DevSize("largest that fits");
            DevCut("largest that fits");
            Shot("largest", crop: true);
            return true;
        });
        Add("close with the X", () => DevPress(_window!.Root.FindChildren("*", "Button", true, false).OfType<Button>().First(b => b.Text == "X"), "the window's X"),
            PointerStepTime);
        Add("closed with the X", () =>
        {
            if (IsOpen || _window is { IsAlive: true, Visible: true } || !DevFramesDrawn()) return false;
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
        // What the read-backs say, in order: the Fishing page's selection is read before the page that was on the screen
        // comes back, the rest at the end.
        var said = new List<string>();
        bool readOk = true, readAll = true;
        void Say(bool same, string what, string now)
        {
            readOk &= same;
            said.Add(same ? $"{what} as it was" : $"{what} NOT AS IT WAS (now {now})");
        }
        // One part of the put-back in its own try: one line, NOT AS IT WAS for a part that failed or did not take, and the
        // next part runs all the same (Firemaking Rates' Dev.cs, Part).
        void Part(string part, Func<string> put)
        {
            try
            {
                string words = put();
                DevLog($"put back | {part}: {words}");
                if (words.Contains("NOT AS IT WAS") || words.Contains("NOT PUT BACK")) _devOnTheWay.Add($"{part}: {words}");
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
                DevEventsOff();
                DevUnwatch();
                return "no longer listened to";
            });
            Part("settings.json", DevSettingsBack);
            Part("the window", () =>
            {
                _window?.Free();
                _window = null;
                _list = null;
                _builtFor = null;
                _fitPending = false;
                _wanted = before.Wanted;
                Sync();
                SyncStripButton();
                return $"{(before.Wanted ? "open" : "closed")}, as the request found it, with what settings.json holds";
            });
            return true;
        });
        // The selection in a step of its own, so that settings.json and the window above never wait: a press of the
        // request's that has not settled yet is waited for, and the selection is read again a tick later.
        Add("put back the page's selection", () =>
        {
            if (_devSettle != null || _devSettledTick >= _devTick) return false;
            Part("the page's selection", DevSelectionBack);
            return true;
        });
        Add("read back the Fishing page", () =>
        {
            if (_fishing == null || !ReferenceEquals(_fishing.Action, _devAction))
            {
                said.Add("the Fishing page's selection NOT READ BACK: another character is open");
                readAll = false;
                return true;
            }
            var action = _fishing.Action;
            DevSelection now = DevSelectionNow();
            string differs = DevDiffers(_devStart!, now);
            // One verdict for the selection: a part of the request's still on his page is NOT AS IT WAS, with what to select
            // by hand; with nothing of the request's left, the selection is as it was, or as the player left it.
            if (DevOwnsAny)
            {
                readOk = false;
                said.Add($"the request's own parts STILL ON THE FISHING PAGE, NOT AS IT WAS: {DevOwnedWords(action)}");
                said.Add($"TO SELECT BY HAND on the Fishing page: {DevHand()}");
            }
            else if (_devUnread != null)
            {
                // A press whose changes could not be read: they may be on the page, owned by nobody.
                readOk = false;
                said.Add($"the Fishing page's location and spots{(differs.Length > 0 ? $" ({differs})" : "")}: something of the request's MAY BE LEFT on them, " +
                    "since what one of its presses left could not be read, NOT AS IT WAS");
            }
            else if (differs.Length == 0) Say(true, "the Fishing page's location and every location's spot", "");
            else said.Add($"the Fishing page's location and spots as the player left them ({differs}): nothing of the request's is left on them");
            if (_devHisParts.Count > 0) said.Add($"the player changed parts of the request's selection while they stood, which were left as he made them: {string.Join("; ", _devHisParts)}");
            // The action's own selected spot: no member of the request's sets it; one verdict too.
            ActiveFishingRecipe? actionSpot = ActionSelected(action);
            if (ReferenceEquals(actionSpot, _devStart!.ActionSpot)) Say(true, $"the action's own selected spot {DevId(_devStart.ActionSpot)}", "");
            else if (DevFishingState() != fishingAtStart && !_devFishingChanged)
                said.Add($"the action's own selected spot {DevId(actionSpot)}: it follows the player's fishing, which he changed (it was {DevId(_devStart.ActionSpot)})");
            else Say(false, $"the action's own selected spot {DevId(_devStart.ActionSpot)}", DevId(actionSpot));
            return true;
        });
        Add("put back the page", () =>
        {
            Part("the page", () =>
            {
                if (before.Page == null || !_devOpenedPage) return "as it was: the request did not open Fishing";
                GamePage? now = CurrentPage(view)?.Item1;
                if (ReferenceEquals(now, before.Page)) return $"{before.Page.Id}, as it was";
                // Only the page the request opened itself: a page the player went to meanwhile is his.
                if (!PageShown) return $"left on {now?.Id ?? "none"}: the player went there from the Fishing page the request opened, and it is his";
                view.SetCurrentPage(before.Page);
                return $"back to {before.Page.Id}, the page on the screen before the request opened Fishing";
            });
            Part("the window on that page", () =>
            {
                Sync();
                SyncStripButton();
                return IsOpen ? "on the screen" : "not on the screen";
            });
            Part("the focus", DevFocusBack);
            Part("the pointer", () =>
            {
                DevPointerBack();
                return "back where the real one is";
            });
            return true;
        });
        Add("read back", () =>
        {
            // The page changes on the game's own time: read back once it has, or after a few seconds anyway. The request
            // changes the page only by opening Fishing; another page than the one it found, other than Fishing, is his.
            GamePage? page = CurrentPage(view)?.Item1;
            bool back = before.Page == null || ReferenceEquals(page, before.Page);
            if (!back && _devOpenedPage && PageShown && Time.GetTicksMsec() - _devStarted < 4000) return false;
            if (back) Say(true, $"the page {before.Page?.Id ?? "none"}", "");
            else if (_devOpenedPage && PageShown) Say(false, $"the page {before.Page?.Id ?? "none"}", page?.Id ?? "none");
            else said.Add($"the page {page?.Id ?? "none"}: the player went there during the request (it was {before.Page?.Id ?? "none"}), so he was left there");
            bool shown = _window is { IsAlive: true, Visible: true };
            // An open window is on the screen whenever the Fishing page is (Sync), and put away on any other page.
            bool shownMeant = before.Wanted && PageShown;
            Say(_wanted == before.Wanted && shown == shownMeant,
                $"the window {(before.Wanted ? "open" : "closed")}{(shownMeant ? " on the screen" : before.Wanted ? ", put away on another page" : "")}",
                $"{(_wanted ? "open" : "closed")}{(shown ? " on the screen" : "")}");
            if (before.Shown && shown) Say(_window!.Minimised == before.Folded, $"folded {before.Folded}", $"folded {_window.Minimised}");
            // The round button is lit while the window is open on the screen and not folded (SyncStripButton).
            bool lit = GodotObject.IsInstanceValid(_opener) && _opener!.ButtonPressed, litMeant = shownMeant && _window is { IsAlive: true, Minimised: false };
            Say(lit == litMeant, $"the round button {(litMeant ? "lit" : "unlit")}, as the window", lit ? "lit" : "unlit");
            string? settings = File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : null;
            Say(settings == before.Settings, before.Settings == null ? "settings.json not there" : "settings.json byte for byte",
                settings == null ? "not there" : $"{settings.Length} characters");
            Say(_devWatch == null, "the game's catches no longer listened to", "still listened to");
            // What fishing does, one verdict: as it was; changed by a press of the request's (a fault, on the way too); or
            // the player's, changed outside the request's presses.
            string fishingNow = DevFishingState();
            if (fishingNow == fishingAtStart) Say(true, fishingAtStart, "");
            else if (_devFishingChanged) Say(false, fishingAtStart, $"{fishingNow}, after a press of the request's changed it (see on the way)");
            else said.Add($"{fishingNow}: the player's, changed outside the request's presses (it was {fishingAtStart})");
            said.Add("the pointer back where the real one is");
            // A put-back on the way that was not as it was counts, even when a later one mended it; a press not made is
            // said, and is no fault.
            var notOk = _devOnTheWay.Where(w => !w.Contains(" NOT MADE: ")).ToList();
            if (notOk.Count > 0) said.Add($"on the way NOT AS IT WAS: {string.Join(" | ", notOk)}");
            if (_devOnTheWay.Count > notOk.Count) said.Add($"not made: {string.Join(" | ", _devOnTheWay.Where(w => w.Contains(" NOT MADE: ")))}");
            _devPutBackWords = string.Join("; ", said) + $"; the order before the clicks was {(firstBefore.Length > 0 ? firstBefore : "not read")}";
            _devPutBackOk = readOk && notOk.Count == 0;
            _devPutBackRead = readAll;
            DevLog("put back: " + _devPutBackWords);
            return true;
        });

        foreach (string line in asked.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("fail=")) { DevLog($"the request's line '{line}' is not understood and left out."); continue; }
            string name = line["fail=".Length..].Trim();
            // "fail=click" fails right after the click on a line, so that what the click left on the page is put back by the
            // final put-back (the code review of turn 2, minor point b; Firemaking Rates' fail=click).
            if (name == "click") name = "the spot opened";
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

    bool DevFishingNow() => _game!.ActionManager.ActiveActions.Any(a => ReferenceEquals(a.Entity, _fishing));

    // The character fishes, by the product's own test (SpotRates.FishingNow: the action started, or running; lesson 56).
    bool DevFishing() => SpotRates.FishingNow(_fishing!, _game).Now;

    // The location whose spot the character fishes, while he fishes; null otherwise, or when the spot fished is not known.
    ActiveFishingLocation? DevFishedAt() =>
        SpotRates.FishingNow(_fishing!, _game).At is { } at && _fishing!.Action.Locations.TryGetValue(at, out ActiveFishingLocation? place) ? place : null;

    // Whether a spot may be selected at this location now, by the product's rule for a click (SpotRates.FishesAt): always
    // while he does not fish; while he fishes only at another location than the one he fishes at, and nowhere when that
    // location is not known (lesson 56).
    bool DevMaySelectAt(ActiveFishingLocation? place)
    {
        SpotRates.Fished fished = SpotRates.FishingNow(_fishing!, _game);
        if (!fished.Now) return true;
        return place != null && fished.At != null && !ReferenceEquals(place.Location, fished.At);
    }

    bool DevMaySelectAt(ActiveFishingRecipe spot) => !SpotRates.FishesAt(spot, SpotRates.FishingNow(_fishing!, _game));

    // The keyboard focus off the mod's own controls (the round button, the window), which only the request's presses can
    // have given it now that the window was built again; nothing is given back.
    string DevFocusBack()
    {
        Control? owner = _ctx.Tree.Root.GuiGetFocusOwner();
        if (owner == null) return "no control has the keyboard focus";
        bool ours = owner == _opener || (_window is { IsAlive: true } window && (owner == window.Root || window.Root.IsAncestorOf(owner)));
        if (!ours) return $"left on {DevName(owner)}, not a control of the mod's";
        owner.ReleaseFocus();
        return $"taken off {DevName(owner)}";
    }

    // ---- a click on a line: the Fishing page's selection ---------------------------------------------------------------

    // The page's location, the spot selected at every location, and the spot the action itself keeps as selected.
    sealed record DevSelection(ActiveFishingLocation? Location, List<(ActiveFishingLocation Location, ActiveFishingRecipe? Spot)> Spots,
        ActiveFishingRecipe? ActionSpot);

    DevSelection DevSelectionNow(FishingActionComponent? of = null)
    {
        var action = of ?? _fishing!.Action;
        return new(action.CurrentLocation, action.Locations.Values.Select(l => (l, l.SelectedRecipe)).ToList(), ActionSelected(action));
    }

    static string DevId(ActiveFishingLocation? location) => location?.Location.Id ?? "none";
    static string DevId(ActiveFishingRecipe? spot) => spot?.Recipe.Id ?? "none";

    static string DevSpots(DevSelection selection) =>
        string.Join(", ", selection.Spots.Select(s => $"{s.Location.Location.Id} {DevId(s.Spot)}"));

    // What of a selection is not as it was: each part that differs, as it is now and as it was ("" when none).
    static string DevDiffers(DevSelection was, DevSelection now)
    {
        var differs = new List<string>();
        if (!ReferenceEquals(now.Location, was.Location)) differs.Add($"the location {DevId(now.Location)} (was {DevId(was.Location)})");
        foreach (var (location, spot) in was.Spots)
        {
            ActiveFishingRecipe? other = now.Spots.FirstOrDefault(s => ReferenceEquals(s.Location, location)).Spot;
            if (!ReferenceEquals(other, spot)) differs.Add($"{location.Location.Id} has {DevId(other)} selected (was {DevId(spot)})");
        }
        return string.Join(", ", differs);
    }

    // What to select by hand to get back what the request still owns, in the page's own names: every spot, then the location.
    string DevHand()
    {
        var parts = new List<string>();
        foreach (var (place, own) in _devOwnSpots)
            parts.Add(own.Origin == null ? $"at '{place.Location.DisplayName.Text}' no spot (it had none; it cannot be taken off)"
                : $"at '{place.Location.DisplayName.Text}' the spot '{own.Origin.Recipe.DisplayName.Text}'");
        if (_devOwnLocation is { } location)
            parts.Add(location.Origin == null ? "no location (it showed none)" : $"then the location '{location.Origin.Location.DisplayName.Text}'");
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }

    static string DevJoin(params string[] parts) => string.Join("; ", parts.Where(p => p.Length > 0));

    bool DevOwnsAny => _devOwnLocation != null || _devOwnSpots.Count > 0;

    static ActiveFishingRecipe? DevSpotOf(DevSelection selection, ActiveFishingLocation place) =>
        selection.Spots.FirstOrDefault(s => ReferenceEquals(s.Location, place)).Spot;

    // The parts the request owns, as they are now against what they were before its press.
    string DevOwnedWords(FishingActionComponent action)
    {
        var words = new List<string>();
        if (_devOwnLocation is { } location) words.Add($"the location {DevId(action.CurrentLocation)} (was {DevId(location.Origin)})");
        foreach (var (place, own) in _devOwnSpots) words.Add($"{place.Location.Id}'s spot {DevId(place.SelectedRecipe)} (was {DevId(own.Origin)})");
        return string.Join(", ", words);
    }

    // A press the request makes that changes the Fishing page (a line, the page's own location or spot button), on every
    // tick until it is made (Firemaking Rates' DevPagePress). It waits for the request's own last press to settle. The page
    // on the tick of the press is what its changes are read against (DevSettle). While a part of the request's selection
    // stands on his page, nothing waits for his hand (lesson 57; the code review of turn 4, fault 1): when he changed a part
    // of it (that part is his), when somebody is at the mouse, or when the press has not come within OwnedPressMost, the
    // press is not made (null) and what the request still owns is put back at once, through the members, which need no
    // free mouse. True once it was made: what it leaves is read once the page has settled.
    bool? DevPagePress(Func<bool> press, string what)
    {
        if (_devSettle != null) return false;
        if (DevOwnsAny)
        {
            var his = DevHisNow();
            string why = his.Count > 0 ? $"the player changed {string.Join(", ", his)}, which stays his"
                : DevHandNow() ? "somebody is at the mouse"
                : Time.GetTicksMsec() - _devStarted > OwnedPressMost ? $"the press did not come within {OwnedPressMost / 1000} s" : "";
            if (why.Length > 0)
            {
                if (_devAim != null) { _devAim = null; DevPointerBack(); }
                DevLog($"click | {what} NOT MADE: {why} while the request's selection stood on his page");
                _devOnTheWay.Add($"{what} NOT MADE: {why}");
                DevPutBackNow($"{what} not made: the request's selection put back at once:");
                return null;
            }
        }
        DevSelection from = DevSelectionNow();
        if (!press()) return false;
        _devPressFrom = from;
        _devSettle = (long)Engine.GetProcessFrames();
        return true;
    }

    // The safety net's put-back before a wait for his hand: as DevPutBackNow, but a leftover that cannot be put back is carried
    // into the done line once, not again at every new wait while it says the same.
    void DevPutBackNet(string head)
    {
        string back;
        try { back = DevSelectionBack(); }
        catch (Exception e) { back = $"the selection NOT AS IT WAS: its put-back failed ({e.GetType().Name}: {e.Message})"; }
        DevLog($"click | {head} {back}");
        if ((back.Contains("NOT AS IT WAS") || back.Contains("NOT PUT BACK")) && back != _devLastNet) _devOnTheWay.Add($"{head} {back}");
        _devLastNet = back;
    }

    // The request's selection put back at once, with one line, and in the done line when it is not as it was.
    void DevPutBackNow(string head)
    {
        string back;
        try { back = DevSelectionBack(); }
        catch (Exception e) { back = $"the selection NOT AS IT WAS: its put-back failed ({e.GetType().Name}: {e.Message})"; }
        DevLog($"click | {head} {back}");
        if (back.Contains("NOT AS IT WAS") || back.Contains("NOT PUT BACK")) _devOnTheWay.Add($"{head} {back}");
    }

    // Every part the request owns, read again: a part that is not exactly what the request left, the player changed. That
    // part alone is his: the request lets go of it (nothing of it is put back), says it, and the read back names it as his.
    // Returns the parts he changed, in words.
    List<string> DevHisNow()
    {
        var his = new List<string>();
        if (_fishing == null || !ReferenceEquals(_fishing.Action, _devAction)) return his;
        var action = _fishing.Action;
        // A spot he picks at the location the page shows takes that location with it: putting the location back would move
        // the page from under the spot he chose (the code review of turn 5, fault 1; Woodcutting Rates lets a category of
        // his take the scroll with it). Read against the spot shown there when the request last read the location as its own.
        if (_devOwnLocation is { } shown && ReferenceEquals(action.CurrentLocation, shown.Left) &&
            !ReferenceEquals(action.CurrentLocation?.SelectedRecipe, _devShownSpot))
        {
            his.Add($"the location {DevId(action.CurrentLocation)} with the spot he picked there ({DevId(action.CurrentLocation?.SelectedRecipe)}, " +
                $"the request left {DevId(_devShownSpot)}), which is seen only on that location");
            _devOwnLocation = null;
        }
        if (_devOwnLocation is { } location && !ReferenceEquals(action.CurrentLocation, location.Left))
        {
            his.Add($"the location (now {DevId(action.CurrentLocation)}, the request left {DevId(location.Left)})");
            _devOwnLocation = null;
        }
        foreach (var (place, own) in _devOwnSpots.ToList())
        {
            if (ReferenceEquals(place.SelectedRecipe, own.Left)) continue;
            his.Add($"{place.Location.Id}'s spot (now {DevId(place.SelectedRecipe)}, the request left {DevId(own.Left)})");
            _devOwnSpots.Remove(place);
        }
        _devHisParts.AddRange(his);
        return his;
    }

    // The parts of the page's selection the request owns put back, each to what it was just before the press that changed
    // it, and each only while it is still exactly what the request left (DevHisNow; a part he changed is his, and left),
    // through the members a click on a line uses (ActiveFishingLocation.SelectRecipe, FishingActionComponent.
    // SetCurrentLocation), never by setting what the game keeps behind them. Every spot first, then the location, read again
    // after the calls, and a second round for a part that is still not as it was. The rule of lesson 56, read on this tick:
    // while the character fishes, no spot is selected at the location he fishes at; such a part is NOT PUT BACK. A part
    // that is still not as it was stays the request's own, with what the put-back left on it, so that a later reading never
    // takes that for a change of his, and the read back names it to select by hand. A part the request does not own that
    // a call of the put-back moved is said NOT AS IT WAS.
    string DevSelectionBack()
    {
        if (!DevOwnsAny) return "nothing of the request's is left on the page";
        if (_fishing == null || !ReferenceEquals(_fishing.Action, _devAction)) return "another character is open: the request's selection NOT AS IT WAS (not put back)";
        var action = _fishing.Action;
        var said = DevHisNow().Select(h => $"{h} is the player's, and is left").ToList();
        var calls = new List<string>();
        var blocked = new Dictionary<string, string>();
        var moved = new List<string>();
        // After every member call, in the same tick (no input of his comes between): every part the request owns takes what
        // is on it now, so that a put-back that throws midway leaves no part looking changed by him; a part it does not own
        // that the call moved becomes the request's, with what it was before the call as its origin, and is said.
        DevSelection last = DevSelectionNow();
        void After(string call)
        {
            calls.Add(call);
            DevSelection now = DevSelectionNow();
            if (_devOwnLocation is { } owned) _devOwnLocation = owned with { Left = now.Location };
            else if (!ReferenceEquals(now.Location, last.Location))
            {
                moved.Add($"the location {DevId(now.Location)} (was {DevId(last.Location)})");
                _devOwnLocation = new(last.Location, now.Location);
            }
            foreach (var (place, spot) in now.Spots)
            {
                if (_devOwnSpots.TryGetValue(place, out var own)) _devOwnSpots[place] = own with { Left = spot };
                else if (DevSpotOf(last, place) is var was && !ReferenceEquals(was, spot))
                {
                    moved.Add($"{place.Location.Id}'s spot {DevId(spot)} (was {DevId(was)})");
                    _devOwnSpots[place] = new(was, spot);
                }
            }
            _devShownSpot = now.Location?.SelectedRecipe;
            last = now;
        }
        for (int round = 0; round < 2; round++)
        {
            bool called = false;
            foreach (var place in _devOwnSpots.Keys.ToList())
            {
                var own = _devOwnSpots[place];
                if (ReferenceEquals(place.SelectedRecipe, own.Origin)) continue;
                string key = place.Location.Id;
                if (own.Origin == null) { blocked[key] = $"{key}'s spot {DevId(place.SelectedRecipe)} NOT AS IT WAS: it had none, and no member selects none"; continue; }
                if (!DevMaySelectAt(place)) { blocked[key] = $"{key}'s spot {DevId(place.SelectedRecipe)} NOT PUT BACK to {own.Origin.Recipe.Id}: the character fishes there, and no spot is selected there while he fishes"; continue; }
                place.SelectRecipe(own.Origin);
                After($"{key}.SelectRecipe({own.Origin.Recipe.Id})");
                called = true;
            }
            if (_devOwnLocation is { } location && !ReferenceEquals(action.CurrentLocation, location.Origin))
            {
                if (location.Origin == null) blocked["location"] = $"the location {DevId(action.CurrentLocation)} NOT AS IT WAS: the page showed none, and no member shows none";
                else
                {
                    action.SetCurrentLocation(location.Origin);
                    After($"SetCurrentLocation({location.Origin.Location.Id})");
                    called = true;
                }
            }
            if (!called) break;
        }
        // Each part the request owns, read again: back to its origin, it is let go; otherwise it stays the request's own
        // with what is on it now.
        DevSelection end = DevSelectionNow();
        var back = new List<string>();
        var left = new List<string>();
        if (_devOwnLocation is { } ownedLocation)
        {
            if (ReferenceEquals(end.Location, ownedLocation.Origin)) { back.Add($"the location {DevId(ownedLocation.Origin)}"); _devOwnLocation = null; }
            else { left.Add(blocked.GetValueOrDefault("location") ?? $"the location {DevId(end.Location)} NOT AS IT WAS (was {DevId(ownedLocation.Origin)})"); _devOwnLocation = ownedLocation with { Left = end.Location }; }
        }
        foreach (var place in _devOwnSpots.Keys.ToList())
        {
            var own = _devOwnSpots[place];
            ActiveFishingRecipe? spot = DevSpotOf(end, place);
            if (ReferenceEquals(spot, own.Origin)) { back.Add($"{place.Location.Id}'s spot {DevId(own.Origin)}"); _devOwnSpots.Remove(place); }
            else
            {
                left.Add(blocked.GetValueOrDefault(place.Location.Id) ?? $"{place.Location.Id}'s spot {DevId(spot)} NOT AS IT WAS (was {DevId(own.Origin)})");
                _devOwnSpots[place] = own with { Left = spot };
            }
        }
        _devShownSpot = end.Location?.SelectedRecipe;
        if (back.Count > 0) said.Add($"{(calls.Count > 0 ? "put back" : "as it was")}: {string.Join(", ", back)}");
        if (calls.Count > 0) said.Add($"by [{string.Join("; ", calls)}]");
        if (moved.Count > 0) said.Add($"A CALL OF THE PUT-BACK MOVED A PART THAT WAS NOT THE REQUEST'S, NOT AS IT WAS: {string.Join(", ", moved)}; it is the request's now, and put back like its own");
        if (left.Count > 0) said.Add($"{string.Join("; ", left)}; what is left stays the request's own for the final put-back");
        return DevJoin(said.ToArray());
    }

    // What fishing is doing: no press of the request's may change it.
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
                $"bait {s.Bait ?? "none"} {(s.BaitPerHour is { } b ? R(b) : "-")}/h, preservation {R(s.BaitPreservation)} " +
                $"{(SpotRates.PreservedBaitStays ? "taken off" : "not counted")}, {s.BaitInBank} in the bank | " +
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
        // Where the preservation chance comes from, in the game's own words: each source's name and the game's description
        // of what it gives (ModifierEntry.Source, IModifierValue.GetDescription), for the words the tooltip uses for it.
        string sources;
        try
        {
            var entries = baits.Stats?.PreservationChance?.Sources.Value;
            sources = entries == null || entries.Count == 0 ? "none" : string.Join(", ", entries.Select(e =>
                $"{e.Source?.DisplayName.Text ?? "no source"}: '{DevWords(e.BaseValue?.GetDescription(e.Multiplier) ?? "")}' ({R(e.Value)})"));
        }
        catch (Exception e) { sources = $"could not be read: {e.GetType().Name}: {e.Message}"; }
        DevLog($"bait | the page shows '{(recipeView.BaitSelect == null ? "-" : CurrentBait(recipeView.BaitSelect).Text)}' | selected {baits.SelectedBait?.Id ?? "none"}, " +
            $"preservation {R(baits.Stats?.PreservationChance?.Total.Value ?? 0)}, in the bank {(baits.SelectedBait == null ? 0 : _bank?.Inventory.GetQuantity(baits.SelectedBait))}, " +
            $"bait items [{string.Join(", ", baits.BaitItems.Select(b => b.Id))}] | the preservation comes from [{sources}] | the window counts " +
            (SpotRates.PreservedBaitStays ? "the preservation, taken off the bait of an hour" : $"one bait a catch, the preservation not counted (measured: {SpotRates.BaitSeen})"));
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
        SpotRates ours = SpotRates.Read(spot, _fishing!, _bank, SpotRates.ShownOnPage(_fishing!), Shown, SpotRates.FishingNow(_fishing!, _game));
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

    // The longest line of every tip of the window in characters, against the 82 and 94 the other rate windows hold
    // theirs to (TipMost, TooltipMost; Mining Rates' Dev.cs, DevTips).
    void DevTips(string when)
    {
        static string Longest(string text) => text.Split('\n').OrderByDescending(l => l.Length).First();
        static string Over(string line, int most) => line.Length > most ? $"  LONGER THAN THE {most} THE OTHER RATE WINDOWS HOLD TO" : "";
        var heads = _list!.DevHeaders.Select(h => (h.Text, Line: Longest(h.TooltipText))).ToList();
        var lines = _list.DevLines().Select(l => (l.Spot.Active.Recipe.LocalId, Line: Longest(l.Button.TooltipText))).ToList();
        string longest = heads.Select(h => h.Line).Concat(lines.Select(l => l.Line)).OrderByDescending(l => l.Length).First();
        DevLog($"tips | {when} | the names' tips, the longest line of each: {string.Join(", ", heads.Select(h => $"{h.Text} {h.Line.Length}{Over(h.Line, TipMost)}"))}" +
            $" | the lines' tooltips: {string.Join(", ", lines.Select(l => $"{l.LocalId} {l.Line.Length}{Over(l.Line, TooltipMost)}"))}" +
            $" | the longest of all, {longest.Length}: '{longest}'");
    }

    // Every label and button with words in the part of the window a picture shows, measured the Combat Simulator's way
    // (Mining Rates' Dev.cs, DevCut; mods/CombatSim/CutProbe.cs): whole, CUT (a name cut by its label's ClipText is CUT
    // too), SHORTENED WITH ... where it is trimmed with its tooltip holding it, and PAST THE EDGE where it ends past the
    // edge of its line or of the window.
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
            bool cell = false;
            if (c is Label label)
            {
                // A number of the list (a label given its column's width, RatesList.BuildRow) grows to hold its words, so
                // its own size never shows it cut: it is measured against its column's width (the code review of turn 2,
                // fault 5). A number wider than that pushes the rest of its line out of line with the names above it.
                cell = label.CustomMinimumSize.X > 0 && _list.DevScroll.IsAncestorOf(label);
                room = (cell ? Math.Min(label.Size.X, label.CustomMinimumSize.X) : label.Size.X) - label.GetThemeStylebox("normal").GetMinimumSize().X;
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
            notWhole.Add($"{verdict} '{One(text)}' {wide:0.#} wide in {room:0.#}{(cell && verdict == "CUT" ? ", wider than its column" : "")}" +
                (verdict.StartsWith("SHORTENED") ? tip.Contains(text, StringComparison.OrdinalIgnoreCase) ? ", its tooltip holds it" : ", ITS TOOLTIP DOES NOT HOLD IT WHOLE" : "") +
                (beyond ? $", PAST THE EDGE ({end:0} > {limit:0})" : ""));
        }
        DevLog($"cut | {when} | {measured} measured at {_window.Style.Scale:0.##}: " + (notWhole.Count == 0 ? "every one whole and within its edge"
            : $"{notWhole.Count} not whole or past the edge ({cut} CUT, {shortened} SHORTENED WITH ..., {past} PAST THE EDGE): {string.Join("; ", notWhole)}"));
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
        public double XpAdded, XpWithoutSpot;
        // The bait selected when the watch began, the bank it is counted in, and the game's events of every process frame
        // with a catch or a change of that bait in the bank: the bait is judged catch by catch, as the bait watch does.
        public Item? BaitAtStart;
        public BankInventoryComponent? Bank;
        public EventHandler<ItemQuantityChangedEventArgs>? OnBank;
        public readonly Dictionary<ulong, DevCatchFrame> Frames = new();
        public int Offline;

        // The events of the frame this runs in.
        public DevCatchFrame Now()
        {
            ulong frame = Engine.GetProcessFrames();
            if (!Frames.TryGetValue(frame, out DevCatchFrame? at)) Frames[frame] = at = new DevCatchFrame();
            return at;
        }
        public readonly HashSet<string> Spots = new();
        // The catches and the XP of every spot fished, so that each spot's XP a catch is compared with its own numbers.
        public readonly Dictionary<ActiveFishingRecipe, DevSpotTally> BySpot = new(ReferenceEqualityComparer.Instance);

        public DevSpotTally Of(ActiveFishingRecipe spot)
        {
            if (!BySpot.TryGetValue(spot, out DevSpotTally? tally)) BySpot[spot] = tally = new DevSpotTally();
            return tally;
        }
    }

    // One game frame of the watch: its catches, its "bait used" and "bait preserved" events (and whether any was of another
    // bait than the one the watch began with), and the bank's change of that bait in it.
    sealed class DevCatchFrame
    {
        public int Catches, Used, Kept, Bank;
        public bool OtherBait;
    }

    sealed class DevSpotTally
    {
        public int Catches;
        public double Xp;
    }

    DevCatchWatch? _devWatch;

    void DevWatch()
    {
        DevUnwatch();
        var w = new DevCatchWatch { Action = _fishing!.Action, Experience = _fishing.Experience, Bait = _fishing.BaitComponent };
        w.BaitAtStart = w.Bait.SelectedBait;
        // A count of the frames must never break the game's catch: anything that goes wrong only leaves it out.
        void InFrame(Action<DevCatchFrame> count)
        {
            try
            {
                if (GameAccess.IsComputingOffline) { w.Offline++; return; }
                count(w.Now());
            }
            catch (Exception) { }
        }
        w.Fished = (_, e) =>
        {
            InFrame(f => f.Catches++);
            w.Catches++;
            bool junk = ReferenceEquals(e.Item, e.Recipe?.Recipe.JunkItem);
            if (junk) w.Junk++; else w.Fish++;
            if (e.Quantity > 1) w.MoreThanOne++;
            w.Spots.Add(e.Recipe?.Recipe.Id ?? "?");
            if (e.Recipe != null) w.Of(e.Recipe).Catches++;
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
            // The game adds a catch's XP in the frame of the catch, while the spot is the one fished (in the checks of
            // 23 Nov the XP came just before RecipeFished): the XP goes to the spot fished at that moment.
            if (w.Action.CurrentRecipe is { } fished) w.Of(fished).Xp += e.NewValue - e.OldValue;
            else w.XpWithoutSpot += e.NewValue - e.OldValue;
            DevLog($"catch | Fishing XP +{R(e.NewValue - e.OldValue)} at {w.Action.CurrentRecipe?.Recipe.Id ?? "no spot fished"}");
        };
        w.Used = (_, e) =>
        {
            InFrame(f => { f.Used += e.Quantity; f.OtherBait |= !Equals(e.Item, w.BaitAtStart); });
            w.BaitUsed += e.Quantity;
            DevLog($"catch | bait {e.Item?.Id} used x{e.Quantity}");
        };
        w.Kept = (_, e) =>
        {
            InFrame(f => { f.Kept += e.Quantity; f.OtherBait |= !Equals(e.Item, w.BaitAtStart); });
            w.BaitKept += e.Quantity;
            DevLog($"catch | bait {e.Item?.Id} preserved x{e.Quantity}");
        };
        w.Action.RecipeFished += w.Fished;
        w.Action.SpecialItemFished += w.Special;
        w.Experience.XpChanged += w.Xp;
        w.Bait.BaitConsumed += w.Used;
        w.Bait.BaitPreserved += w.Kept;
        // The bank's own changes of the bait the watch began with (BankInventoryComponent.ItemQuantityChanged, as the bait
        // watch hears them), only listened to.
        if (w.BaitAtStart != null && _bank != null)
        {
            w.Bank = _bank.Inventory;
            w.OnBank = (_, e) =>
            {
                if (Equals(e.Item, w.BaitAtStart)) InFrame(f => f.Bank += e.NewValue - e.OldValue);
            };
            w.Bank.ItemQuantityChanged += w.OnBank;
        }
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
        if (w.Bank != null && w.OnBank != null) w.Bank.ItemQuantityChanged -= w.OnBank;
    }

    // What the game's catches brought, next to the mod's numbers: the XP a catch of every spot a catch came from against
    // that spot's own, and the rest against the spot fished now.
    void DevCatches()
    {
        if (_devWatch is not { } w) return;
        double seconds = (Time.GetTicksMsec() - w.Since) / 1000.0;
        if (w.Catches == 0)
        {
            DevLog($"catches | none in {seconds:0} s (fishing now: {DevFishingNow()}): to check the hour against the game, start fishing by hand and send the request again.");
            return;
        }
        var xp = new List<string>();
        foreach (var (spot, tally) in w.BySpot)
        {
            SpotRates s = SpotRates.Read(spot, _fishing!, _bank, SpotRates.ShownOnPage(_fishing!), Shown, SpotRates.FishingNow(_fishing!, _game));
            if (tally.Catches == 0) { xp.Add($"Fishing XP +{R(tally.Xp)} at {spot.Recipe.Id} WITH NO CATCH there"); continue; }
            double xpEach = tally.Xp / tally.Catches;
            xp.Add($"ours at {spot.Recipe.Id}: XP {R(s.XpEach)} a catch, the game's {R(xpEach)} a catch over {tally.Catches} catches: " +
                (Math.Abs(xpEach - s.XpEach) < 0.01 ? "the same" : "DIFFER"));
        }
        if (w.XpWithoutSpot != 0) xp.Add($"Fishing XP +{R(w.XpWithoutSpot)} WHILE NO SPOT WAS FISHED");
        string ours = "";
        if (_fishing!.Action.CurrentRecipe is { } fished)
        {
            SpotRates s = SpotRates.Read(fished, _fishing, _bank, SpotRates.ShownOnPage(_fishing), Shown, SpotRates.FishingNow(_fishing, _game));
            ours = $" | ours at {fished.Recipe.Id}, the spot fished now: " +
                $"fish on {R(s.FishChance)} of catches, junk on {R(s.JunkChance)}; doubling {R(s.Doubling)}; bait preservation {R(s.BaitPreservation)}";
        }
        DevLog($"catches | {w.Catches} in {seconds:0} s at [{string.Join(", ", w.Spots)}]: {w.Fish} fish, {w.Junk} junk, {w.MoreThanOne} of them more than one at a time; " +
            $"{w.Specials} special items; Fishing XP +{R(w.XpAdded)} in {w.XpChanges} changes | {DevCaughtBait(w)} | " +
            $"{string.Join("; ", xp)}{ours}");
    }

    // The bait of the watched catches: the game's "bait used" events against the catches (one each in every game check
    // so far), and, catch by catch, the bank's change of the bait selected when the watch began in the frame of the catch
    // against what the Bait column counts for that catch (SpotRates.PreservedBaitStays): down one, or the same for a
    // preserved one while preserved bait stays. A change of the bank apart from a catch (bait bought or sold, or any other
    // way) is said and not judged, nor is a frame with more than one catch or with another bait's events.
    string DevCaughtBait(DevCatchWatch w)
    {
        string events = $"bait used {w.BaitUsed}, preserved {w.BaitKept}: " +
            (w.BaitUsed == w.Catches ? "a \"bait used\" for every catch" : $"NOT ONE \"BAIT USED\" FOR EVERY CATCH ({w.BaitUsed} for {w.Catches})");
        if (w.BaitAtStart is not { } bait) return events + "; no bait was selected when the request began";
        if (w.Bank == null) return events + "; the bank was not found";
        var frames = w.Frames.Values.ToList();
        var judged = frames.Where(f => f.Catches == 1 && !f.OtherBait).ToList();
        static int Meant(DevCatchFrame f) => SpotRates.PreservedBaitStays && f.Kept > 0 ? 0 : -1;
        var otherwise = judged.Where(f => f.Bank != Meant(f)).Select(f => $"{f.Bank:+0;-0;0} on a catch with {f.Kept} preserved").ToList();
        int apart = frames.Count(f => f.Catches == 0 && f.Bank != 0), several = frames.Count(f => f.Catches > 1),
            other = frames.Count(f => f.Catches == 1 && f.OtherBait);
        string verdict = judged.Count == 0 ? $"no catch with {bait.Id} to judge the bank on"
            : otherwise.Count == 0 ? $"on every one of the {judged.Count} catches with {bait.Id} the bank's count went as the Bait column counts " +
                $"({(SpotRates.PreservedBaitStays ? "down one, the same on a preserved one" : "down one")}), the {judged.Count(f => f.Kept > 0)} preserved ones included"
            : $"THE BAIT COLUMN COUNTS OTHERWISE on {otherwise.Count} of {judged.Count} catches with {bait.Id}: {string.Join(", ", otherwise.Take(5))}";
        return events + "; " + verdict +
            (apart > 0 ? $"; the bank's {bait.Id} also changed apart from a catch in {apart} frames, by {frames.Where(f => f.Catches == 0).Sum(f => f.Bank):+0;-0;0} in all (bought, sold or another way: not judged)" : "") +
            (several > 0 ? $"; {several} frames with more than one catch, not judged" : "") +
            (other > 0 ? $"; {other} catches with another bait, not judged" : "") +
            (w.Offline > 0 ? $"; {w.Offline} events while the game caught up offline, left out" : "");
    }

    // ---- through the game's input, as a player's hand would --------------------------------------------------------

    Vector2I _devPointer;
    bool _devPointerSeen, _devHandSinceStep;
    ulong _devHandAt, _devGuardSince, _devTipSince;
    Control? _devAim;
    Vector2 _devAimAt;
    // How often in a row the game said the pointer is on another control than the one aimed at: something lies over it,
    // and after MostMisses the step says so at once instead of waiting out its time (Firemaking Rates' Dev.cs).
    int _devMisses;
    const int MostMisses = 10;
    // The screen just before the pointer went onto the control whose tooltip is pictured, taken after any wait for his
    // hand: what the tooltip's picture is proved against (the code review of turn 2, minor point c).
    Image? _devTipBefore;

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

    // Somebody was at the mouse within StillFor: what DevHandAtMouse would wait for, read without waiting or saying it.
    bool DevHandNow() => _devHandAt != 0 && Time.GetTicksMsec() - _devHandAt < StillFor;

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
            // Nothing waits for his hand while a part of the request's selection stands on his page (lesson 57): it is put back
            // at once, before the wait (a part that cannot be put back stays named for the final put-back).
            if (DevOwnsAny && _devSettle == null) DevPutBackNet($"somebody is at the mouse while the request's selection stood on his page, before {what} waits:");
        }
        else if (now - _devGuardSince > GiveUpAfter) throw new InvalidOperationException($"somebody was at the mouse for {GiveUpAfter / 1000} s: {what} did not run");
        _devAim = null;
        return true;
    }

    // A player's click in two steps (ItemEncyclopediaMod.DevAim, DevPressAimed; Firemaking Rates' DevPress): the pointer
    // is put on the part of the control that is in sight, beside the mod's own window for a control of the game under it,
    // and a later tick presses only when the game says the pointer is on that control, so the click can never land on a
    // control of the game's beside it or over it. The keyboard focus is taken off it once the press has been handled
    // (DevFocusOff). True once it was pressed.
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

    // The part of a control that can be seen: on the screen, inside every scrolling list it is in (the window's list, the
    // Fishing page's), and, for a control of the game, beside the mod's own window, which lies over the page: the pointer
    // put on the window's part would never be on the control (Firemaking Rates' DevSight).
    Rect2 DevSight(Control control)
    {
        Rect2 rect = control.GetGlobalRect().Intersection(_ctx.Tree.Root.GetVisibleRect());
        for (Node? n = control.GetParent(); n != null; n = n.GetParent())
            if (n is ScrollContainer scroll) rect = rect.Intersection(scroll.GetGlobalRect());
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

    // The pointer rests on a control and the game's tooltip comes up; its picture is shot-fishing-<shot>.png, taken two
    // drawn frames after it came up, and its pixels where the tooltip lies are read against the screen just before the
    // pointer went onto the control (Mining Rates' Dev.cs, DevTooltip). That picture is taken on the tick the pointer is
    // put there, two drawn frames after the step began or after a hand moved it away, so a wait for his hand never makes
    // it old. A tooltip that has not come up in 20 s of pointing is one line, and the request goes on.
    bool DevTooltip(Button target, string shot)
    {
        if (DevHandAtMouse("the tooltip")) { _devTipSince = 0; _devFrameMark = 0; return false; }
        if (_devTipSince == 0) _devTipSince = Time.GetTicksMsec();
        Control? over = _ctx.Tree.Root.GuiGetHoveredControl();
        if (over == null || (over != target && !target.IsAncestorOf(over)))
        {
            Rect2 rect = DevSight(target);
            if (!rect.HasArea()) throw new InvalidOperationException($"No part of '{target.Text}' is in sight for its tooltip.");
            if (!DevFramesDrawn()) return false;
            _devTipBefore?.Dispose();
            _devTipBefore = _ctx.Tree.Root.GetTexture().GetImage();
            DevPointer(rect.GetCenter());
            _devFrameMark = 0;
            return false;
        }
        Image? before = _devTipBefore;
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
                DevLog($"shot-fishing-{shot}.png | " + DevChanged(before, now, at, "the tooltip's place", "VISIBLE", "NOT VISIBLE"));
            }
            else DevLog($"shot-fishing-{shot}.png | NOT PROVEN: {(before == null ? "no picture of the screen before it" : "the tooltip's place was not found")}");
            _devTipBefore?.Dispose();
            _devTipBefore = null;
            DevPointerBack();
            return true;
        }
        if (Time.GetTicksMsec() - _devTipSince < 20000) return false;
        DevLog($"tooltip | {shot} | NOT SHOWN in 20 s with the pointer on {DevName(over)}; its words are in the 'row' and 'tips' lines, and there is no picture of it");
        DevPointerBack();
        return true;
    }

    // Where the Godot tooltip that is up lies, in the root's own coordinates (GameStyle.DevTooltips finds it the same way;
    // Mining Rates' Dev.cs, DevTooltipRect).
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

    // How many of the pixels of a place (in the root's coordinates) differ between two pictures of the screen: a quarter
    // or more is "yes" (something else is drawn there now), as Mining Rates proves its tooltips (DevChanged).
    string DevChanged(Image before, Image after, Rect2 place, string what, string yes, string no)
    {
        if (before.GetSize() != after.GetSize()) return $"{no}: the two pictures differ in size ({before.GetSize()} and {after.GetSize()}), so they cannot be compared";
        Rect2 screen = _ctx.Tree.Root.GetFinalTransform() * place;
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
