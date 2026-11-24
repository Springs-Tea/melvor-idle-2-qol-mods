#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.CustomControls;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Hunting;
using Godot;

namespace HuntingRates;

// Development: the parts of the Hunting page the light check (Dev.cs) changes, who owns each, and how each is put back;
// the game's events around its presses; and the guard behind its claim that the hunt was left as it was.
//
// The rule, Woodcutting Rates' (its review 3) for the Hunting page, and lesson 56 on top of it:
// - The parts are the location selected, the animal selected, the view (the journal or the hunt) and the scroll of the
//   page's list of animals. What the request left on a part is read from the page once it has settled after the
//   request's own press (DevSettle), and kept. At the put-back the part is read again: still exactly that, it is put
//   back; otherwise the player changed it, and it is his: left, said in one line, never taken as the request's own.
// - Both ways (code review 1, fault 3): the animal and the scroll belong to the location shown, and are let go of when
//   the page shows another location; a location at which an animal of his stands is his too.
// - Every press of the page is a press of its own controls, with the pointer as a player would or, for a put-back, by
//   the control's own signal or event as a click on a line presses it; never through the members behind them, never
//   with a value the page's controls cannot make (no "none").
// - A press that selects is made only while no hunt runs, read again right before it (lesson 56). While a hunt runs,
//   such a press, and such a put-back, is not made and is said: NOT PUT BACK when it was the request's own.
// - The hunt guard (DevHuntGuard) listens from the request's start to its end, and judges the hunt's events frame by
//   frame (code review 2, fault 3): HUNT STARTED, HUNT STOPPED, HUNT CHANGED (the animal hunted, the trap in use) and
//   HUNT RESET (the hunted animal going back with no catch or escape in that frame, the action's timer stopped and not
//   started again in that frame, HuntingActionComponent.ShouldResetAction set). In the window of a press of the
//   request's (from the press to its settled reading) they are the request's fault; outside every window they are the
//   player's or the game's (a trap swapped when one ran out), the baseline, and said so.
public partial class HuntingRatesMod
{
    sealed record DevOwn<T>(T Value);
    DevOwn<ActiveHuntingLocation?>? _devOwnLocation;
    DevOwn<ActiveHuntingAnimal?>? _devOwnAnimal;
    DevOwn<bool>? _devOwnJournal;
    DevOwn<int?>? _devOwnScroll;
    // The location an owned animal and an owned scroll were read under.
    ActiveHuntingLocation? _devAnimalUnder, _devScrollUnder;
    // A press of the request's not settled yet: the process frame and the time it was made in, and whether it was a change
    // of the request's (every part read as its own once it settled) or a put-back's (only what it still owns read again).
    // Process frames, not drawn ones: a minimised game draws none (Woodcutting Rates' code review 4, fault 2), and after
    // SettleAtMost the reading is taken anyway.
    (long Frame, ulong At, bool Both)? _devSettle;
    const ulong SettleAtMost = 3000;
    long _devTick, _devSettledTick = -1;
    DevPageWas? _devOrigin, _devOriginWait;
    readonly HashSet<string> _devHis = new();
    (Control Target, long Frame)? _devFocusAfter;
    HuntingActionComponent? _devAction;
    DevHuntGuard? _devGuard;

    // The Hunting page's selection, view and scroll at one moment.
    sealed record DevPageWas(ActiveHuntingLocation? Location, ActiveHuntingAnimal? Animal, bool Journal, int? Scroll);

    DevPageWas DevPageNow() => new(_hunting!.Action.SelectedLocation, _hunting.Action.SelectedAnimal, DevJournalNow(), DevScrollNow());

    static string DevPageWords(DevPageWas p) =>
        $"the location {p.Location?.Location?.Id ?? "none"}, the animal {p.Animal?.Animal.Id ?? "none"}, the {(p.Journal ? "journal" : "hunt")} shown, " +
        $"the list scrolled to {p.Scroll?.ToString() ?? "-"}";

    bool? DevJournalNowOrNull()
    {
        try { return JournalShown(); }
        catch (Exception) { return null; }
    }

    bool DevJournalNow() => DevJournalNowOrNull() ?? false;

    int? DevScrollNow()
    {
        try { return DevListScroll() is { } scroll ? scroll.ScrollVertical : null; }
        catch (Exception) { return null; }
    }

    ScrollContainer? DevListScroll() =>
        _page != null && GodotObject.IsInstanceValid(_page) && Native.TrackingView(_page) is { } view && Native.AnimalList(view) is { } list ? Native.ScrollOf(list) : null;

    bool DevOwnsAny => _devOwnLocation != null || _devOwnAnimal != null || _devOwnJournal != null || _devOwnScroll != null;

    // The parts of the page that are the request's now, or a press of its own still settling, in words; "" for none.
    string DevOursWords()
    {
        var parts = new List<string>();
        if (_devOwnLocation != null) parts.Add("location");
        if (_devOwnAnimal != null) parts.Add("animal");
        if (_devOwnJournal != null) parts.Add("view");
        if (_devOwnScroll != null) parts.Add("scroll");
        if (_devSettle != null) parts.Add("press settling");
        return string.Join(", ", parts);
    }

    // A hunt runs, by the window's own test (AnimalRates.IsHunting): read again right before every press that selects and
    // every put-back of a selection (lesson 56).
    bool DevHuntRuns => HuntRuns();

    // The test and its three parts, for the log.
    string DevHuntRunsWords()
    {
        var action = _hunting!.Action;
        return $"a hunt runs by the window's test: {HuntRuns()} (started {action.IsStarted}, among the game's running actions {DevHuntingNow()}, " +
            $"the animal hunted {action.CurrentAnimal?.Animal.Id ?? "none"} in state {action.CurrentAnimal?.State.ToString() ?? "-"})";
    }

    // The page is still laying itself out after a press: the request's own, or a click on a line in its steps.
    bool DevSettling => _devSettle != null || _clickStep != null || _viewCheck != null || _scrollTo != null;

    // A press the request makes on the Hunting page (a line, the page's own view, location or card button), on every tick
    // until it is made. It waits for the last press to settle. While nothing of the request's is left on the page, the page
    // as it is on that tick is what the put-backs go back to; while it owns a part the player has changed since, the press
    // is not made (null), so that the request never presses over a page he is using. True once it was made.
    bool? DevPagePress(Func<bool> press, DevPageWas start, string what)
    {
        if (DevSettling) return false;
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
        _devGuard?.Open(what);
        if (!press())
        {
            _devGuard?.Close();
            return false;
        }
        if (!owns)
        {
            DevPageWas was = _devOrigin ?? start;
            _devOrigin = _devOriginWait ?? DevPageNow();
            _devHis.Clear();
            if (_devOrigin != was)
                DevLog($"click | the page just before {what}: {DevPageWords(_devOrigin)}; not as the request found it ({DevPageWords(was)}): the player changed it " +
                    "meanwhile, and that is what is put back");
        }
        _devSettle = ((long)Engine.GetProcessFrames(), Time.GetTicksMsec(), true);
        return true;
    }

    // Each part the request owns, read again; one that is not exactly what the request left is the player's, and is let
    // go of, both ways: a location of his takes the animal and the scroll with it, an animal of his at the location the
    // page shows takes that location with it, and an animal or a scroll read under another location than the page shows
    // now belongs to a list that is no longer there. Returns the parts he changed, in words. scroll: false leaves the scroll
    // to the settled reading, for the early put-back of the animal two process frames after a location's press, when the
    // list may not have laid itself out yet (DevAnimalBackSoon).
    List<string> DevHisNow(bool scroll = true)
    {
        var moved = new List<string>();
        var action = _hunting!.Action;
        void Let(string part, string words)
        {
            moved.Add(words);
            _devHis.Add(part);
            switch (part)
            {
                case "location": _devOwnLocation = null; break;
                case "animal": _devOwnAnimal = null; break;
                case "view": _devOwnJournal = null; break;
                default: _devOwnScroll = null; break;
            }
        }
        if (_devOwnLocation is { } location && !ReferenceEquals(action.SelectedLocation, location.Value))
        {
            Let("location", $"the location (now {action.SelectedLocation?.Location?.Id ?? "none"}, the request left {location.Value?.Location?.Id ?? "none"})");
            if (_devOwnAnimal != null) Let("animal", "the animal with it");
            if (_devOwnScroll != null) Let("scroll", "the scroll with it");
        }
        if (_devOwnAnimal != null && !ReferenceEquals(action.SelectedLocation, _devAnimalUnder))
            Let("animal", "the animal (the page shows another location than the one it was read under)");
        if (_devOwnAnimal is { } animal && !ReferenceEquals(action.SelectedAnimal, animal.Value))
        {
            Let("animal", $"the animal (now {action.SelectedAnimal?.Animal.Id ?? "none"}, the request left {animal.Value?.Animal.Id ?? "none"})");
            if (_devOwnLocation != null && action.SelectedAnimal is { } his && ReferenceEquals(his.Location, action.SelectedLocation))
                Let("location", "the location his animal stands at");
        }
        if (_devOwnJournal is { } journal && DevJournalNowOrNull() is { } now && now != journal.Value)
            Let("view", $"the view (now the {(now ? "journal" : "hunt")}, the request left the {(journal.Value ? "journal" : "hunt")})");
        if (!scroll) return moved;
        if (_devOwnScroll != null && !ReferenceEquals(action.SelectedLocation, _devScrollUnder))
            Let("scroll", "the scroll (the list is of another location now)");
        if (_devOwnScroll is { } own && DevScrollNow() is var at && at != own.Value)
            Let("scroll", $"the scroll (now {at?.ToString() ?? "-"}, the request left {own.Value?.ToString() ?? "-"})");
        return moved;
    }

    // What the request's own last press left on the page, read once the page has settled: two process frames after the
    // press and after a click's own steps, or after SettleAtMost anyway. A change of the request's: every part is its own
    // now. A put-back of the request's: the scroll and the animal it still owns are read again, the list being new. The
    // guard's window of that press is closed a tick later, and what the hunt did in it is said.
    void DevSettle()
    {
        if (_devSettle is not { } settle) return;
        bool late = Time.GetTicksMsec() - settle.At >= SettleAtMost;
        if (!late && (_clickStep != null || _viewCheck != null || _scrollTo != null || (long)Engine.GetProcessFrames() < settle.Frame + 2)) return;
        _devSettle = null;
        _devSettledTick = _devTick;
        if (late) DevLog($"page | the page had not settled {SettleAtMost / 1000} s after the request's press: what it left is read as it is now");
        if (_hunting == null || _page == null || !GodotObject.IsInstanceValid(_page)) return;
        var action = _hunting.Action;
        if (settle.Both)
        {
            _devOwnLocation = new(action.SelectedLocation);
            _devOwnAnimal = new(action.SelectedAnimal);
            _devAnimalUnder = action.SelectedLocation;
            _devOwnJournal = DevJournalNowOrNull() is { } journal ? new(journal) : null;
            _devOwnScroll = new(DevScrollNow());
            _devScrollUnder = action.SelectedLocation;
        }
        else
        {
            if (_devOwnScroll != null) { _devOwnScroll = new(DevScrollNow()); _devScrollUnder = action.SelectedLocation; }
            if (_devOwnAnimal != null) { _devOwnAnimal = new(action.SelectedAnimal); _devAnimalUnder = action.SelectedLocation; }
        }
        DevLog($"page | settled after the request's {(settle.Both ? "press" : "put-back")}: the request owns the location " +
            $"{(_devOwnLocation is { } l ? l.Value?.Location?.Id ?? "none" : "(none)")}, the animal {(_devOwnAnimal is { } a ? a.Value?.Animal.Id ?? "none" : "(none)")}, " +
            $"the view {(_devOwnJournal is { } j ? j.Value ? "journal" : "hunt" : "(none)")} and the scroll {(_devOwnScroll is { } s ? s.Value?.ToString() ?? "-" : "(none)")}");
    }

    // The guard's window of the last press is closed on the tick after its settled reading.
    void DevGuardTick()
    {
        if (_devGuard == null) return;
        _devGuard.Poll(DevNative.ShouldReset(_hunting!.Action));
        if (_devGuard.IsOpen && _devSettle == null && _devSettledTick >= 0 && _devTick > _devSettledTick && !DevSettling)
        {
            DevLog($"hunt | {_devGuard.Close()}");
        }
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

    // A hand is at the mouse now: the real pointer moved, or a button was down, within StillFor (DevWatchHand).
    bool DevHandNow() => _devHandAt != 0 && Time.GetTicksMsec() - _devHandAt < StillFor;

    // What the player is to select by hand when a selection of his could not be put back (Firemaking Rates' words).
    string DevByHand() => _devOrigin is { } origin
        ? $"TO SELECT BY HAND on the Hunting page: the location '{origin.Location?.Location?.DisplayName.Text ?? "-"}' and the animal '{origin.Animal?.Animal.DisplayName.Text ?? "-"}'"
        : "";

    // ---- the put-back of the page's parts, each on its own, through the page's own controls ----------------------------

    // The view the request owns, put back by the page's own button of the view it was (the journal's or the hunt's),
    // pressed by its signal as a click on a line presses the hunt's; judged once the page has settled (DevViewBackRead).
    string DevViewBack()
    {
        if (_devOwnJournal is not { } journal || _devOrigin is not { } origin) return "";
        if (journal.Value == origin.Journal) { _devOwnJournal = null; return $"the {(origin.Journal ? "journal" : "hunt")} shown, as it was"; }
        if (_page == null || Native.TrackingView(_page) is not { } view) return "the view NOT AS IT WAS: the page's view is not found";
        Button? button = origin.Journal ? Native.JournalButton(view) : Native.HuntButton(view);
        if (!CanPress(button)) return $"the view NOT AS IT WAS: the page's own {(origin.Journal ? "journal" : "hunt")} button cannot be pressed";
        Button? other = origin.Journal ? Native.HuntButton(view) : Native.JournalButton(view);
        _devViewDrawn = (button!.ButtonPressed, other?.ButtonPressed ?? false);
        _devGuard?.Open("the put-back of the view");
        if (button.ToggleMode && !button.ButtonPressed) button.ButtonPressed = true;
        button.EmitSignal(BaseButton.SignalName.Pressed);
        _devSettle = ((long)Engine.GetProcessFrames(), Time.GetTicksMsec(), false);
        return $"the page's own {(origin.Journal ? "journal" : "hunt")} button pressed to show it again";
    }

    (bool Pressed, bool Other)? _devViewDrawn;

    // Once settled: the view as it was, or NOT AS IT WAS with both view buttons drawn again as they were before the press.
    string DevViewBackRead()
    {
        if (_devViewDrawn is not { } drawn || _devOrigin is not { } origin) return "";
        _devViewDrawn = null;
        if (DevJournalNow() == origin.Journal) { _devOwnJournal = null; return $"the {(origin.Journal ? "journal" : "hunt")} shown again"; }
        if (_page != null && Native.TrackingView(_page) is { } view)
        {
            Button? button = origin.Journal ? Native.JournalButton(view) : Native.HuntButton(view);
            Button? other = origin.Journal ? Native.HuntButton(view) : Native.JournalButton(view);
            if (button != null && button.ButtonPressed != drawn.Pressed) button.SetPressedNoSignal(drawn.Pressed);
            if (other != null && other.ButtonPressed != drawn.Other) other.SetPressedNoSignal(drawn.Other);
        }
        return "the view NOT AS IT WAS: the page did not follow its own button, and both view buttons are drawn again as they were";
    }

    // The location the request owns, put back by the page's own button of the location it was, while no hunt runs. The
    // animal of the player's, if one was selected and not the request's, is read before and after: the game changing it
    // with the location is NOT AS IT WAS (code review 1, fault 3).
    string DevLocationBack()
    {
        if (_devOwnLocation is not { } own || _devOrigin is not { } origin) return "";
        var action = _hunting!.Action;
        if (ReferenceEquals(action.SelectedLocation, origin.Location)) { _devOwnLocation = null; return "the location as it was"; }
        if (origin.Location == null)
        {
            _devOwnLocation = null;
            return $"the location NOT PUT BACK: there was none selected before, and the page's own controls cannot make none (left on {own.Value?.Location?.Id ?? "none"})";
        }
        if (DevHuntRuns)
        {
            _devOwnLocation = null;
            return $"the location NOT PUT BACK: a hunt runs, and nothing is selected while one runs (left on {action.SelectedLocation?.Location?.Id ?? "none"}); {DevByHand()}";
        }
        ActiveHuntingAnimal? his = _devOwnAnimal == null ? action.SelectedAnimal : null;
        _devGuard?.Open("the put-back of the location");
        bool took = DevSignalLocation(origin.Location);
        _devSettle = ((long)Engine.GetProcessFrames(), Time.GetTicksMsec(), false);
        if (!took) return $"the location NOT AS IT WAS: the page's own button of {origin.Location.Location?.Id} did not open it (it stays the request's own)";
        _devOwnLocation = null;
        // What the request's own location press left is the request's own at once, as DevSettle's put-back branch takes it:
        // the animal the page shows now, read under the location it shows now, and the scroll under that location (its
        // value is read again once the page has settled). So the early put-back of the animal sees as his only a change he
        // made after this press (the code review of turn 4, fault 1).
        if (_devOwnAnimal != null)
        {
            _devOwnAnimal = new(action.SelectedAnimal);
            _devAnimalUnder = action.SelectedLocation;
            // The animal goes back two process frames later, once the page has laid out the location's cards, as the
            // window's own click steps (ClickStepNow): its card stands for a moment at most (the third code review).
            _devAnimalBackFrame = (long)Engine.GetProcessFrames() + 2;
        }
        if (_devOwnScroll != null) _devScrollUnder = action.SelectedLocation;
        string animal = his != null && !ReferenceEquals(action.SelectedAnimal, his)
            ? $"; the animal the player had picked, {his.Animal.Id}, NOT AS IT WAS: the game changed it to {action.SelectedAnimal?.Animal.Id ?? "none"} with the location" : "";
        return $"the location put back to {origin.Location.Location?.Id} with the page's own button{animal}";
    }

    // The put-back of the animal two process frames after its location's (DevLocationBack): run from OnFrame, its words kept
    // for the put-back's step, which says them. Every check of DevAnimalBack runs as on the tick: the player's changes, the
    // hunt, the location shown.
    long _devAnimalBackFrame = -1;
    string _devAnimalBackWords = "";

    void DevAnimalBackSoon()
    {
        _devAnimalBackFrame = -1;
        if (_stopped || _hunting == null || _page == null || !GodotObject.IsInstanceValid(_page)) return;
        try
        {
            string his = string.Join(", ", DevHisNow(scroll: false).Select(p => $"{p} is the player's, and is left"));
            _devAnimalBackWords = DevJoin(_devAnimalBackWords, his, DevAnimalBack());
        }
        catch (Exception e) { _devAnimalBackWords = DevJoin(_devAnimalBackWords, $"the animal NOT AS IT WAS: its put-back failed ({e.GetType().Name}: {e.Message})"); }
    }

    // The animal the request owns, put back by its card's own button once the page shows its location, while no hunt runs.
    string DevAnimalBack()
    {
        if (_devOwnAnimal is not { } own || _devOrigin is not { } origin) return "";
        var action = _hunting!.Action;
        if (ReferenceEquals(action.SelectedAnimal, origin.Animal)) { _devOwnAnimal = null; return "the animal as it was"; }
        if (origin.Animal == null)
        {
            _devOwnAnimal = null;
            return $"the animal NOT PUT BACK: there was none selected before, and the page's own controls cannot make none (left on {own.Value?.Animal.Id ?? "none"})";
        }
        if (_devOwnLocation != null) return "the animal left with the request's location, which is not put back: both stay the request's own";
        if (DevHuntRuns)
        {
            _devOwnAnimal = null;
            return $"the animal NOT PUT BACK: a hunt runs, and nothing is selected while one runs (left on {action.SelectedAnimal?.Animal.Id ?? "none"}); {DevByHand()}";
        }
        if (!ReferenceEquals(action.SelectedLocation, origin.Animal.Location))
        {
            _devOwnAnimal = null;
            return $"the animal NOT PUT BACK: its card is at {origin.Animal.Location?.Location?.Id}, and the page shows {action.SelectedLocation?.Location?.Id ?? "none"}; {DevByHand()}";
        }
        _devGuard?.Open("the put-back of the animal");
        bool took = DevSignalCard(origin.Animal);
        _devSettle = ((long)Engine.GetProcessFrames(), Time.GetTicksMsec(), false);
        if (!took) return $"the animal NOT AS IT WAS: its card's own button did not select {origin.Animal.Animal.Id} (it stays the request's own)";
        _devOwnAnimal = null;
        return $"the animal put back to {origin.Animal.Animal.Id} with its card's own button";
    }

    // The scroll the request owns, read again: still exactly that, it is put back to where it was before the request's
    // first change; otherwise the player moved it, and it is left (Woodcutting Rates' DevScrollBack). A scroll only shows.
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

    // The page's own location button pressed by its own event, as a click on a line presses it (PressLocation), without
    // switching the window's click off; the button drawn again as it was when the page does not follow.
    bool DevSignalLocation(ActiveHuntingLocation location)
    {
        HuntingLocationButton? button = Native.LocationButton(_page!, location.Location);
        if (!CanPress(button)) return false;
        bool drawn = button!.ButtonPressed;
        if (button.ToggleMode && !button.ButtonPressed) button.ButtonPressed = true;
        Native.PressedEvent(button)?.Invoke();
        if (ReferenceEquals(_hunting!.Action.SelectedLocation, location)) return true;
        if (GodotObject.IsInstanceValid(button) && button.ButtonPressed != drawn) button.SetPressedNoSignal(drawn);
        return false;
    }

    // An animal's card button pressed by its signal, as a click on a line presses it (PressCard), without switching the
    // window's click off; the buttons of its group drawn again as they were when the page does not follow.
    bool DevSignalCard(ActiveHuntingAnimal animal)
    {
        if (Native.Card(_page!, animal) is not { } card || Native.CardButton(card) is not { } button || !CanPress(button)) return false;
        var group = button.ButtonGroup?.GetButtons().OfType<BaseButton>().ToList() ?? new List<BaseButton>();
        if (!group.Contains(button)) group.Add(button);
        var drawn = group.Select(b => (Button: b, Pressed: b.ButtonPressed)).ToList();
        if (button.ToggleMode && !button.ButtonPressed) button.ButtonPressed = true;
        button.EmitSignal(BaseButton.SignalName.Pressed);
        if (ReferenceEquals(_hunting!.Action.SelectedAnimal, animal)) return true;
        foreach (var (b, pressed) in drawn)
            if (GodotObject.IsInstanceValid(b) && b.ButtonPressed != pressed) b.SetPressedNoSignal(pressed);
        return false;
    }

    // ---- the hunt, the click, and the game's events ----------------------------------------------------------------------

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

    // The page's selection and what it shows: the location and the animal, the view and both view buttons as drawn, the
    // panel's name, the list's card and its button.
    string DevSelection(AnimalRates a)
    {
        var action = _hunting!.Action;
        HuntingAnimalTrackingView? card = Native.Card(_page!, a.Active);
        string buttons = Native.TrackingView(_page!) is { } view
            ? $"the hunt button drawn {(Native.HuntButton(view)?.ButtonPressed == true ? "pressed" : "up")}, the journal button drawn {(Native.JournalButton(view)?.ButtonPressed == true ? "pressed" : "up")}"
            : "the view's buttons not found";
        return $"the location {action.SelectedLocation?.Location?.Id ?? "none"}, the animal {action.SelectedAnimal?.Animal.Id ?? "none"}, the {(DevJournalNow() ? "journal" : "hunt")} shown " +
            $"({buttons}), the panel names '{DevPanelName()}', the list holds {Native.Cards(_page!).Count()} cards, the card of {a.Animal.Id} " +
            (card == null ? "not in it" : $"pressed {Native.CardButton(card)?.ButtonPressed}");
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
    // and the next are the press's (DevClickEvents).
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

    bool DevInGamePress()
    {
        long frame = (long)Engine.GetProcessFrames();
        return _devGamePressing || (_devPressFrame >= 0 && (frame == _devPressFrame || frame == _devPressFrame + 1));
    }

    static bool DevSameEvents(string a, string b) =>
        a.Split(", ", StringSplitOptions.RemoveEmptyEntries).Order().SequenceEqual(b.Split(", ", StringSplitOptions.RemoveEmptyEntries).Order());

    // The game's events about the page's selection and about the hunt, listened to while one click is made: those raised
    // while the click itself runs (now()) are its own. Each listener runs inside a try; they are taken off by Dispose,
    // which Stop and Detach reach through DevUnwatchAll.
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
            action.SelectedLocationChanged += location;
            action.SelectedAnimalChanged += animal;
            _off.Add(() =>
            {
                action.SelectedLocationChanged -= location;
                action.SelectedAnimalChanged -= animal;
            });
            foreach (ActiveHuntingAnimal a in action.Animals.Values)
            {
                if (a == null) continue;
                string id = a.Animal.LocalId;
                EventHandler<ValueChangedEventArgs<Item>> chosen = (_, e) => Safe(() => Saw($"CHOICES: {id} trap {e.NewValue?.Id ?? "none"}", false));
                EventHandler<ValueChangedEventArgs<bool>> bait = (_, e) => Safe(() => Saw($"CHOICES: {id} bait {e.NewValue}", false)),
                    trapless = (_, e) => Safe(() => Saw($"CHOICES: {id} trapless {e.NewValue}", false));
                a.SelectedTrapChanged += chosen;
                a.UseBaitChanged += bait;
                a.TraplessEnabledChanged += trapless;
                _off.Add(() =>
                {
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

    // The hunt guard: from the request's start to its end, every event of the hunt, gathered per process frame and judged
    // per frame, as the watch groups its moments (code review 2, fault 3): the order of the game's events within one tick
    // is not known, so a frame is read whole. In a frame:
    // - the hunted animal going back to surveying, or a step or the tracks going down, is HUNT RESET only when no catch or
    //   escape came in the same frame, and its hunt had not ended in an earlier one (a hunt that ended stays ended until
    //   the next one leaves surveying);
    // - the action's timer stopping is HUNT RESET only when it does not start again in the same frame (a timer the game
    //   restarts for every step stops and starts in one frame);
    // - HUNT STARTED, HUNT STOPPED, HUNT CHANGED (the animal hunted, the trap in use) and CHOICES CHANGED as they come.
    // A frame's marks are the request's fault when the frame fell in the window of one of its presses (Open to Close);
    // outside every window they are the player's or the game's, and are kept as the baseline the read back names.
    sealed class DevHuntGuard : IDisposable
    {
        // One process frame's events of the hunt.
        sealed class Frame
        {
            public long Number;
            public bool InWindow;
            public bool Ended, Back, Down, Left, TimerStopped, TimerStarted;
            public readonly List<string> Backs = new(), Marks = new(), Course = new();
        }

        readonly List<Action> _off = new();
        readonly List<string> _window = new(), _windowCourse = new();
        readonly List<string> _outside = new();
        readonly Func<ActiveHuntingAnimal?> _hunted;
        readonly Action<string> _log;
        Frame? _frame;
        string _press = "";
        bool _ended, _resetSeen;
        int _outsideFrames;
        public bool IsOpen { get; private set; }
        public bool AnyInPress { get; private set; }
        /// <summary>Every mark that came in a press's window, with the press: what the read back holds against the request.</summary>
        public readonly List<string> Marked = new();
        static DevHuntGuard? s_current;

        public DevHuntGuard(HuntingActionComponent action, Action<string> log)
        {
            s_current?.Dispose();
            s_current = this;
            _hunted = () => action.CurrentAnimal;
            _log = log;
            void Safe(Action add)
            {
                try { add(); }
                catch (Exception e) { log("hunt guard | a listener failed: " + e.Message); }
            }
            EventHandler<ActionEventArgs> started = (_, _) => Safe(() => Now().Marks.Add("HUNT STARTED")),
                stopped = (_, _) => Safe(() => Now().Marks.Add("HUNT STOPPED"));
            EventHandler<ValueChangedEventArgs<ActiveHuntingAnimal>> animal = (_, e) => Safe(() => Now().Marks.Add($"HUNT CHANGED: the animal hunted {e.NewValue?.Animal.Id ?? "none"}"));
            EventHandler<ValueChangedEventArgs<Item>> trap = (_, e) => Safe(() => Now().Marks.Add($"HUNT CHANGED: the trap in use {e.NewValue?.Id ?? "none"}"));
            EventHandler timerStarted = (_, _) => Safe(() => { Frame f = Now(); f.TimerStarted = true; f.Course.Add("the timer started"); }),
                timerStopped = (_, _) => Safe(() => { Frame f = Now(); f.TimerStopped = true; f.Course.Add("the timer stopped"); });
            action.Started += started;
            action.Stopped += stopped;
            action.CurrentAnimalChanged += animal;
            action.CurrentTrapChanged += trap;
            var timer = action.ActionTimer;
            if (timer != null)
            {
                timer.Started += timerStarted;
                timer.Stopped += timerStopped;
            }
            _off.Add(() =>
            {
                action.Started -= started;
                action.Stopped -= stopped;
                action.CurrentAnimalChanged -= animal;
                action.CurrentTrapChanged -= trap;
                if (timer != null)
                {
                    timer.Started -= timerStarted;
                    timer.Stopped -= timerStopped;
                }
            });
            foreach (ActiveHuntingAnimal a in action.Animals.Values)
            {
                if (a == null) continue;
                ActiveHuntingAnimal watched = a;
                bool Hunted() => ReferenceEquals(_hunted(), watched);
                string id = watched.Animal.LocalId;
                EventHandler<ValueChangedEventArgs<HuntState>> state = (_, e) => Safe(() =>
                {
                    if (!Hunted()) return;
                    Frame f = Now();
                    f.Course.Add($"{id} {e.OldValue} -> {e.NewValue}");
                    if (e.NewValue == HuntState.Surveying && e.OldValue != HuntState.Surveying)
                    {
                        f.Back = true;
                        f.Backs.Add($"{id} went back to surveying from {e.OldValue}");
                    }
                    // A hunt that ended stays ended until the next one leaves surveying: noted in the frame, and cleared when
                    // the frame is judged, after its marks (the third code review).
                    if (e.OldValue == HuntState.Surveying && e.NewValue != HuntState.Surveying) f.Left = true;
                });
                EventHandler<ValueChangedEventArgs<int>> step = (_, e) => Safe(() =>
                {
                    if (!Hunted() || e.NewValue >= e.OldValue) return;
                    Frame f = Now();
                    f.Down = true;
                    f.Backs.Add($"{id} step {e.OldValue} -> {e.NewValue}");
                });
                // Tracks going down are the hunt's course, not a reset, until the watch shows what Tracks is (it may be used up
                // as tracking starts; the third code review).
                EventHandler<ValueChangedEventArgs<int>> tracks = (_, e) => Safe(() =>
                {
                    if (!Hunted() || e.NewValue >= e.OldValue) return;
                    Now().Course.Add($"{id} tracks {e.OldValue} -> {e.NewValue}");
                });
                EventHandler<HuntingAnimalEventArgs> ended = (_, _) => Safe(() =>
                {
                    Frame f = Now();
                    f.Ended = true;
                    f.Course.Add($"{id} caught or escaped");
                });
                EventHandler<ValueChangedEventArgs<Item>> chosen = (_, e) => Safe(() => Now().Marks.Add($"CHOICES CHANGED: {id} trap {e.NewValue?.Id ?? "none"}"));
                EventHandler<ValueChangedEventArgs<bool>> bait = (_, e) => Safe(() => Now().Marks.Add($"CHOICES CHANGED: {id} bait {e.NewValue}")),
                    trapless = (_, e) => Safe(() => Now().Marks.Add($"CHOICES CHANGED: {id} trapless {e.NewValue}"));
                watched.StateChanged += state;
                watched.CurrentStepChanged += step;
                watched.TracksChanged += tracks;
                watched.AnimalTrapped += ended;
                watched.AnimalEscaped += ended;
                watched.SelectedTrapChanged += chosen;
                watched.UseBaitChanged += bait;
                watched.TraplessEnabledChanged += trapless;
                _off.Add(() =>
                {
                    watched.StateChanged -= state;
                    watched.CurrentStepChanged -= step;
                    watched.TracksChanged -= tracks;
                    watched.AnimalTrapped -= ended;
                    watched.AnimalEscaped -= ended;
                    watched.SelectedTrapChanged -= chosen;
                    watched.UseBaitChanged -= bait;
                    watched.TraplessEnabledChanged -= trapless;
                });
            }
        }

        // The frame of now: a new one when the process frame has changed, after the one before is judged.
        Frame Now()
        {
            long number = (long)Engine.GetProcessFrames();
            if (_frame is { } open && open.Number == number) { open.InWindow |= IsOpen; return open; }
            Judge();
            return _frame = new Frame { Number = number, InWindow = IsOpen };
        }

        // A frame read whole: its marks, and where they go.
        void Judge()
        {
            if (_frame is not { } f) return;
            _frame = null;
            var marks = new List<string>(f.Marks);
            if ((f.Back || f.Down) && !f.Ended && !_ended)
                marks.Add($"HUNT RESET: {string.Join(", ", f.Backs)} without a catch or an escape in that frame");
            if (f.TimerStopped && !f.TimerStarted) marks.Add("HUNT RESET: the action's timer stopped and did not start again in that frame");
            if (f.Ended) _ended = true;
            else if (f.Left) _ended = false;
            if (f.InWindow)
            {
                _window.AddRange(marks);
                if (f.Course.Count > 0) _windowCourse.Add(string.Join(" ", f.Course));
            }
            else if (marks.Count > 0)
            {
                _outside.AddRange(marks);
                _outsideFrames++;
            }
        }

        // Every tick: the frame before is judged once it is over, and HuntingActionComponent.ShouldResetAction (internal,
        // read by its name) is read: set in a press's window, a reset of the request's; set outside one, the game's.
        public void Poll(bool? reset)
        {
            if (_frame is { } open && open.Number != (long)Engine.GetProcessFrames()) Judge();
            if (reset != true) { _resetSeen = false; return; }
            if (_resetSeen) return;
            _resetSeen = true;
            const string words = "HUNT RESET: HuntingActionComponent.ShouldResetAction is set";
            if (IsOpen) _window.Add(words);
            else _outside.Add(words);
        }

        public void Open(string press)
        {
            if (IsOpen) _log("hunt | " + Close());
            Judge();
            IsOpen = true;
            _press = press;
            _window.Clear();
            _windowCourse.Clear();
        }

        // The words of the window that closes: what the hunt did from the press to its settled reading.
        public string Close()
        {
            if (!IsOpen) return "";
            Judge();
            IsOpen = false;
            if (_window.Count > 0) AnyInPress = true;
            Marked.AddRange(_window.Select(m => $"{m} (after {_press})"));
            string course = string.Join(" | ", _windowCourse);
            return $"from {_press} to its settled reading: " + (_window.Count == 0 ? "the hunt changed nothing of its own, nothing marked" : string.Join(", ", _window)) +
                (course.Length > 0 ? $" (its own course, frame by frame: {course})" : "");
        }

        // What the hunt did outside every press's window, the baseline: the player's or the game's.
        public string Outside
        {
            get
            {
                Judge();
                return _outside.Count == 0 ? "nothing marked" : $"{_outside.Count} marks in {_outsideFrames} frames: {string.Join(", ", _outside.Distinct())}";
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
        DevHuntGuard.DisposeCurrent();
        _devGuard = null;
        DevUnwatchHunts();
    }
}
#endif
