#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GamesByMalcs.Melvor2.Entities.Thieving;
using GamesByMalcs.Melvor2.GameMenus;
using GamesByMalcs.Melvor2.GameMenus.Pages;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace ThievingOverview;

// Development: a file "thieving.request" in the mod's folder runs the check below with nobody at the keyboard, one
// step about every quarter second, and writes "thieving.done" when it ends: "ok" or "failed at <step>: <why>", and in
// both cases what was put back. Every step logs what it read as one-line "Dev: ..." entries in MelvorModLoader.log.
//   .\tools\dev-cycle.ps1 -Mod ThievingOverview -Into ThievingOverview -Request thieving.request -Produces thieving.done
// The request file may say "fail=<step>": that step then fails on purpose, to show that a failed request puts
// everything back (-Text 'fail=tooltip').
// It only looks, points, opens screens and clicks the mod's own controls: no target or distraction is chosen, nothing
// is started or stopped, nothing is eaten. The one thing of the game's it changes, the place the Thieving page shows
// (a click on a line, step 5b), it puts back with the page's own Select button. A click on the round button or the fold button is made in two steps,
// as the Item Encyclopedia's request makes them (ItemEncyclopediaMod.DevAim, DevPressAimed): the pointer is put on it,
// and a later step presses only when the game says the pointer is on it. Nothing is pointed at or pressed while
// somebody is at the mouse (CombatGoals/DevWindowShots.cs, WatchHand): such a step waits until the real pointer has
// been still for 5 s, and after 90 s the request ends, which puts everything back. Pictures go into the mod's folder.
//  1. Remembers the page on the screen, the window (open, on the screen, folded), the round button, settings.json as
//     it is on disk and whether View Possible Rewards was open. Starts listening to the game's Thieving events (only
//     listening, guarded: see 12).
//  2. Opens the Thieving page the way the sidebar does, if another page is open, and reads back that it is shown.
//  3. "Dev: strip", "Dev: food", "Dev: bar" (the HP bar's own damage watcher, its auto eat and max hit marks and their
//     place on the bar next to ours, the game's words for them), "Dev: card" for every target card on the screen (the
//     game's name ("???"), success chance and its format, GetSuccessChance, time and its format, XP, max hit, stages,
//     fortune next to the window's numbers, "MISMATCH" where they differ), "Dev: place" (what is listed and what is
//     not, and why), "Dev: success chance unit", "Dev: loot chance units". shot-thieving-page.png.
//  4. Presses the round button and opens the window up if it opened folded; the button must be lit.
//  5. This place, every place (crop-thieving-window.png, crop-thieving-every-place.png), a locked line in sight
//     (crop-thieving-locked.png), Best for the item most targets give (crop-thieving-best-for.png, and the window's width
//     against Targets': "WIDTH CHANGED" when it differs), back to Targets ("NOT AS BEFORE" when its order was lost).
//     "Dev: line" for every line as it is drawn, "Dev: size".
//  5b. Clicks (two steps, as above) a line of another place that can be opened: the Thieving page must show that place,
//     and the character must do what it did before ("CHANGED" otherwise, and the request fails); shot-thieving-clicked.png.
//     Then shows the page's place again with that place's own Select button. While the character is stealing no line
//     is clicked, unless the request file says "click=stealing".
//  6. Rests the pointer on a line: "Dev: tooltip" and shot-thieving-tooltip.png. A tooltip that does not come up in
//     20 s is one line, and the request goes on.
//  7. Folds the window with its button (crop-thieving-folded.png, the round button unlit) and opens it again.
//  8. When the request began on another page: opens that page (the window and the button must step aside,
//     shot-thieving-away.png) and the Thieving page again (the window must come back where it was).
//  9. Puts the window away, opens View Possible Rewards of a card on the screen with the card's own button (one with a
//     fortune if there is one, else one that is no heist, else a heist), and logs "Dev: rate" for every reward and
//     the coins: the chance, its share, the quantities, the number per hour, the tooltip over the quantity (its place
//     against the quantity's cell, its words, "NO TOOLTIP" or "OUTSIDE THE WINDOW"), the window's own number for the
//     same item ("MISMATCH window") and the number from the game's tables alone ("MISMATCH tables", when the game
//     counts no fortune). shot-thieving-rewards.png: the window as the game drew it, nothing of the mod's on it. Then
//     rests the pointer on the first quantity (as on a line in 6): "Dev: rate tooltip" and
//     shot-thieving-rewards-tooltip.png. Closes the window with its X.
// 10. The Risky tag on the card of the target being stolen from: the real one when that target is risky, else a
//     PREVIEW of it on a card on the screen (said in the log; a preview says "Risky" whatever the target is). The tag is
//     first held back, and a picture is taken of the card without it; then the mod shows it, and a second picture is
//     taken. A picture is of the frame the game drew last, so each is taken only once two frames have been drawn since
//     the tag went or came: taken at once, as in check 3, it showed the frame before the tag. "Dev: warning" with the
//     card, the name, the tag's place and what it would cover, and what the pixels say: the share of the tag's pixels
//     that differ from the picture without it, against the name beside it, which must not change ("VISIBLE" or
//     "NOT VISIBLE"), and how its edge and its words stand out ("READABLE" or "HARD TO READ"). shot-thieving-warning.png,
//     crop-thieving-warning.png (the card) and crop-thieving-warning-without.png (the card without the tag).
// 11. "Dev: tries": while the character is stealing, up to 8 of the game's tries or 45 s: the time between two tries
//     and the share of successes next to the window's Time and Success, and "Dev: event" for what each try gave (XP,
//     items, coins, the heist's stage, a stun and its length, damage taken).
// 12. Puts back settings.json exactly as it was, the window open or closed and folded as it was, View Possible Rewards
//     closed if it was, the Thieving page's place, the page, the pointer where the real one is, lets go of the Risky tag
//     (no preview, not held back), and stops listening.
// 13. Reads all of it back from the screen and the disk, and says in the last line what was put back, with
//     NOT AS IT WAS for anything that is not.
// A step that fails jumps to 12: a failed request puts everything back too.
public partial class ThievingOverviewMod
{
    const string RequestFile = "thieving.request", DoneFile = "thieving.done";
    // CombatGoals/DevWindowShots.cs: how long the real pointer must be still before a step points or presses, and
    // how long a step waits for that before the request ends.
    const ulong StillFor = 5000, GiveUpAfter = 90000, StepTime = 6000, PointerStepTime = GiveUpAfter + 10000, TriesTime = 50000;

    readonly List<(string Name, Func<bool> Run, ulong Timeout)> _devSteps = new();
    int _devAt, _devPutBackAt, _devLootTold;
    ulong _devStarted;
    string? _devFailure, _devFailOnPurpose, _devPutBackWords;
    bool _devPutBackOk, _devOpenedPage, _devListenFailed;
    DevBefore? _devBefore;
    ThievingActionComponent? _devListened;
    readonly List<string> _devEvents = new();
    readonly List<(ulong At, bool Success, string Recipe)> _devTries = new();

    // What the request found, to be put back as it was (the place the Thieving page shows), or only read back (what
    // the character is doing, which the request never changes).
    sealed record DevBefore(GamePage? Page, bool Wanted, bool Shown, bool? Folded, bool? Lit, string? Settings, bool RewardsOpen,
        ActiveThievingLocation? Place, bool Stealing, ActiveThievingRecipe? Target);

    bool _devClickWhileStealing;

    void DevReset()
    {
        // Another character while a request runs: its listening is let go, and the request says it stopped.
        if (_devSteps.Count > 0)
        {
            DevListen(false);
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
                DevLog($"Thieving Overview check started, {_devPutBackAt} steps and the put-back.");
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
            DevListen(false);
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
        DevLog("Thieving Overview check " + result);
        _devSteps.Clear();
        _devAt = 0;
        _devBefore = null;
        File.WriteAllText(Path.Combine(_ctx.Directory, DoneFile), result);
    }

    // The mod stopped while a request ran: settings.json is put back at least, and the request says what was not.
    void DevAbandon()
    {
        if (_devSteps.Count == 0) return;
        DevListen(false);
        string settings = "settings.json left as it was";
        if (_devBefore is { } before)
        {
            if (before.Settings == null) { if (File.Exists(SettingsFile)) File.Delete(SettingsFile); }
            else ModFiles.WriteWhole(SettingsFile, before.Settings);
            settings = "settings.json put back";
        }
        DevEnd($"stopped: the mod stopped (see its 'Stopped' line); {settings}; the window, the page and View Possible Rewards NOT put back");
    }

    string DevState() =>
        $"page shown={PageShown}, button shown={_opener?.IsVisibleInTree()}, lit={_opener?.ButtonPressed}, window open={IsOpen}, folded={_window?.Minimised}, " +
        $"{_view?.DevChoices}, rewards shown={(_page != null ? Native.Rewards(_page).IsVisibleInTree() : null)}, {DevDoing()}";

    string DevDoing()
    {
        var action = _thieving?.Action;
        if (action == null) return "no thieving";
        return $"thieving started={action.IsStarted}, target={action.CurrentRecipe?.Recipe.Id ?? "none"}, stun={action.StunState}, " +
            $"place shown={action.SelectedCategory?.Data.Id ?? "none"}";
    }

    // One entry, one line: the check keeps only the log's lines that carry the mod's name, so words of the game
    // with line breaks in them (its tooltips) are joined with " / ".
    void DevLog(string text) => _ctx.Log("Dev: " + One(text));

    static string One(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", " / ");

    void DevPlan(string asked)
    {
        _devSteps.Clear();
        _devAt = _devPutBackAt = 0;
        _devFailure = _devFailOnPurpose = _devPutBackWords = null;
        _devPutBackOk = _devOpenedPage = _devListenFailed = false;
        _devGuardSince = _devTipSince = 0;
        _devAim = null;
        _devPlacePressed = null;
        _devEvents.Clear();
        _devTries.Clear();
        _devStarted = Time.GetTicksMsec();
        GameView view = GameAccess.FindGameView() ?? throw new InvalidOperationException("No character is open.");
        var doing = _thieving!.Action;
        var before = _devBefore = new DevBefore(Native.CurrentPage(view)?.Item1, _wanted, _window is { IsAlive: true, Visible: true },
            _window is { IsAlive: true } ? _window.Minimised : null, GodotObject.IsInstanceValid(_opener) ? _opener!.ButtonPressed : null,
            File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : null, _page != null && Native.Rewards(_page).IsVisibleInTree(),
            doing.SelectedCategory, doing.IsStarted, doing.CurrentRecipe);
        _devClickWhileStealing = false;
        DevLog($"before: page {before.Page?.Id ?? "none"}, window {(before.Wanted ? "open" : "closed")}{(before.Shown ? " and on the screen" : "")}" +
            $"{(before.Folded == true ? ", folded" : "")}, round button {(before.Lit is { } lit ? lit ? "lit" : "unlit" : "not made yet")}, View Possible Rewards " +
            $"{(before.RewardsOpen ? "open" : "closed")}, settings.json {(before.Settings == null ? "not there" : $"{before.Settings.Length} characters: {before.Settings}")} | " +
            $"{DevDoing()} | the real pointer at {DisplayServer.MouseGetPosition()}");
        DevListen(true);

        Button? target = null;
        float targetsWidth = 0;
        string targetsSort = "";
        bool previewed = false;
        // The picture of the card while the tag is held back, and the count of frames drawn when the tag went or came.
        Image? withoutTag = null;
        long framesAt = 0;
        ThievingRecipe? rewardsOf = null;
        Control? rewardsTip = null;
        int rewardsLooks = 0;
        GamePage? away = null, thievingPage = null;
        Vector2 placeBefore = default;
        void Add(string name, Func<bool> run, ulong timeout = StepTime) => _devSteps.Add((name, run, timeout));
        ThievingFood Food() => ThievingFood.Read(_thieving!, _bank);

        Add("open the Thieving page", () =>
        {
            if (PageShown) return true;
            if (_devOpenedPage) return false;   // read back on the next ticks: the page is found once a second
            var option = _game!.Layout.Sidebar.Options.Concat(_game.Layout.Sidebar.LegacyOptions).FirstOrDefault(o =>
                o.Page is EntityPage p && ReferenceEquals(p.Entity, _thieving) &&
                o.Requirements?.AreMet != false && o.DisplayRequirements?.AreMet != false);
            if (option?.Page is not EntityPage page)
                throw new InvalidOperationException("Thieving cannot be opened from the sidebar: open the Thieving page by hand and send the request again.");
            view.SetCurrentPage(page);
            _devOpenedPage = true;
            DevLog($"opened the Thieving page the way the sidebar does ({before.Page?.Id ?? "no page"} was in front)");
            return false;
        });
        Add("page", () =>
        {
            if (!PageShown || _opener?.IsVisibleInTree() != true) return false;
            DevLog($"page | the page is {Native.CurrentPage(view)?.Item1?.Id ?? "none"}, the Thieving page is shown: {PageShown}");
            ProveUnits();
            DevStrip();
            DevFood();
            DevBar("page");
            if (DevCards() == 0) DevLog("card | no target card is on the Thieving screen (no place chosen?): nothing of the game's to compare with.");
            DevPlaces();
            Shot("page");
            if (IsOpen) Close();
            return true;
        });
        Add("press the round button", () => DevPress(_opener!, "the round button"), PointerStepTime);
        Add("opened", () => IsOpen);
        Add("open it up", () => _window!.Minimised ? DevPress(DevFoldButton(), "the window's fold button") : true, PointerStepTime);
        Add("this place", () =>
        {
            if (!IsOpen || _window!.Minimised) return false;
            if (_opener?.ButtonPressed != true) throw new InvalidOperationException("The round button is not lit while the window is open.");
            // The window's own buttons, pressed as a click does; each choice is looked at on the next tick.
            if (_settings.BestFor) { _view!.DevPress("Targets"); return false; }
            if (_settings.AllPlaces && _thieving!.Action.SelectedCategory != null) { _view!.DevPress("here"); return false; }
            Refresh();
            if (DevLines("this place") > 0) Shot("window", crop: true);
            DevSize("this place");
            targetsWidth = _window.Root.Size.X;
            return true;
        });
        Add("every place", () =>
        {
            if (!_settings.AllPlaces) { _view!.DevPress("everywhere"); return false; }
            Refresh();
            if (DevLines("every place") > 0) Shot("every-place", crop: true);
            DevStats();
            targetsSort = $"{_settings.Sort} {(_settings.Descending ? "down" : "up")}";
            return true;
        });
        Add("a locked line", () =>
        {
            var locked = _view!.DevLines().FirstOrDefault(l => l.Hidden || l.Locked).Root;
            if (locked == null) { DevLog("locked | no locked target is listed: no picture of one."); target = null; return true; }
            _view.Scroll.EnsureControlVisible(locked);
            target = locked;
            return true;
        });
        Add("locked picture", () =>
        {
            if (target is not { } locked || !GodotObject.IsInstanceValid(locked)) return true;
            if (!_view!.Scroll.GetGlobalRect().Encloses(locked.GetGlobalRect())) { _view.Scroll.EnsureControlVisible(locked); return false; }
            DevLog($"locked | in sight: {string.Join(" / ", _view.DevLines().Where(l => l.Root.GetGlobalRect().Intersects(_view.Scroll.GetGlobalRect())).Select(l => l.Drawn))}");
            Shot("locked", crop: true);
            _view.Scroll.ScrollVertical = 0;
            return true;
        });
        Add("best for", () =>
        {
            if (!_settings.BestFor)
            {
                // The item most targets give.
                var item = ReadAll(Food()).SelectMany(r => BestForItems().Where(r.Gives)).GroupBy(i => i.Id).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
                if (item == null) { DevLog("best for | no target gives an item: no Best for picture."); return true; }
                _settings.Item = item;
                _view!.DevPress("Best for");
                return false;
            }
            if (!IsOpen || _view == null || !_view.DevChoices.StartsWith("best for=True")) return false;
            Refresh();
            if (DevLines("best for " + _settings.Item) > 0) Shot("best-for", crop: true);
            DevSize("best for");
            float width = _window!.Root.Size.X;
            DevLog($"best for | the window is {width:0} wide, {targetsWidth:0} with Targets{(Math.Abs(width - targetsWidth) > 0.5f ? "  WIDTH CHANGED" : ": the same")}");
            return true;
        });
        Add("back to Targets", () =>
        {
            if (_settings.BestFor) { _view!.DevPress("Targets"); return false; }
            if (!IsOpen || _view == null || !_view.DevChoices.StartsWith("best for=False")) return false;
            Refresh();
            int lines = _view.DevLines().Count();
            string sort = $"{_settings.Sort} {(_settings.Descending ? "down" : "up")}";
            DevLog($"back to Targets | {lines} lines, sorted by {sort}{(targetsSort == "" || sort == targetsSort ? ", as before Best for" : $"  NOT AS BEFORE Best for ({targetsSort})")}");
            if (lines == 0) throw new InvalidOperationException("The window lists nothing after coming back from Best for.");
            return true;
        });
        // A click on a line of another place: the page must show that place, and nothing the character does may change.
        ThievingLocation? clicked = null;
        ActiveThievingLocation? placeWas = null;
        ActiveThievingRecipe? chosenWas = null;
        Button? clickedRow = null;
        Add("click a line", () =>
        {
            var action = _thieving!.Action;
            if (action.IsStarted && !_devClickWhileStealing)
            {
                DevLog("click | the character is stealing: no line is clicked (send 'click=stealing' to click one while stealing, to see that the theft goes on).");
                return true;
            }
            var line = _view!.DevLines().FirstOrDefault(l => l.Clickable);
            if (line.Root == null) { DevLog("click | no line of another place that can be opened is listed: nothing is clicked."); return true; }
            clicked = line.Target.Recipe.Category;
            placeWas = action.SelectedCategory;
            chosenWas = action.SelectedRecipe;
            clickedRow = line.Root;
            _view.Scroll.EnsureControlVisible(clickedRow);
            DevLog($"click | a line of {clicked.Id} ({line.Drawn}); the page shows {placeWas?.Data.Id ?? "no place"}; {DevDoing()}");
            return true;
        });
        Add("press the line", () => clicked == null || DevPress(clickedRow!, "a line of " + clicked.Id), PointerStepTime);
        Add("the place opened", () =>
        {
            if (clicked == null) return true;
            var action = _thieving!.Action;
            if (!ReferenceEquals(action.SelectedCategory?.Data, clicked)) return false;
            bool same = action.IsStarted == before.Stealing && ReferenceEquals(action.CurrentRecipe, before.Target);
            DevLog($"click | the page shows {clicked.Id} now (it showed {placeWas?.Data.Id ?? "no place"}); stealing {action.IsStarted}, from " +
                $"{action.CurrentRecipe?.Recipe.Id ?? "nothing"} (before: {before.Stealing}, from {before.Target?.Recipe.Id ?? "nothing"}){(same ? ", unchanged" : "  CHANGED")}; " +
                $"the page's chosen target {action.SelectedRecipe?.Recipe.Id ?? "none"} (before: {chosenWas?.Recipe.Id ?? "none"}); the window still open: {IsOpen}");
            Shot("clicked");
            if (!same) throw new InvalidOperationException("A click on a line changed what the character is doing.");
            return true;
        });
        Add("the place back", () =>
        {
            if (clicked == null) return true;
            return DevPlaceBack(placeWas, "click");
        });
        Add("point at a line", () =>
        {
            var lines = _view!.DevLines().ToList();
            target = (lines.FirstOrDefault(l => !l.Locked).Root ?? lines.FirstOrDefault().Root) ?? throw new InvalidOperationException("No line to point at.");
            _view.Scroll.ScrollVertical = 0;
            _devTipSince = 0;
            return true;
        });
        Add("tooltip", () => DevTooltip(target!), PointerStepTime);
        Add("fold", () => DevPress(DevFoldButton(), "the window's fold button"), PointerStepTime);
        Add("folded", () =>
        {
            if (_window?.Minimised != true) return false;
            if (_opener?.ButtonPressed != false) throw new InvalidOperationException("The round button is still lit while the window is folded.");
            Shot("folded", crop: true);
            return true;
        });
        Add("open up again", () => DevPress(DevFoldButton(), "the window's fold button"), PointerStepTime);
        Add("opened up again", () => _window?.Minimised == false && _opener?.ButtonPressed == true);
        Add("leave the page", () =>
        {
            thievingPage = Native.CurrentPage(view)?.Item1;
            away = before.Page != null && !ReferenceEquals(before.Page, thievingPage) ? before.Page : null;
            if (away == null) { DevLog("step aside | the request began on the Thieving page: leaving it is not tried."); return true; }
            placeBefore = _window!.Position;
            view.SetCurrentPage(away);
            return true;
        });
        Add("away", () =>
        {
            if (away == null) return true;
            if (PageShown) return false;
            bool windowShown = _window is { IsAlive: true, Visible: true }, buttonShown = _opener?.IsVisibleInTree() == true;
            DevLog($"step aside | on {Native.CurrentPage(view)?.Item1?.Id ?? "none"}: the window {(windowShown ? "STILL SHOWN" : "put away")}, the round button " +
                $"{(buttonShown ? "STILL SHOWN" : "gone")}, the window still wanted: {_wanted}");
            Shot("away");
            view.SetCurrentPage(thievingPage!);
            return true;
        });
        Add("back on the page", () =>
        {
            if (away == null) return true;
            if (!PageShown || !IsOpen) return false;
            Vector2 at = _window!.Position;
            DevLog($"step aside | back on the Thieving page: the window open again {(at.DistanceTo(placeBefore) < 0.5f ? "at the same place" : $"MOVED from {placeBefore} to {at}")}, " +
                $"folded={_window.Minimised}, lit={_opener?.ButtonPressed}");
            return true;
        });
        Add("open rewards", () =>
        {
            // The window is put away so it covers nothing of the game's window; the put-back brings it back.
            Close();
            DevPointerBack();
            var cards = Cards(Native.ActionView(_page!)).Where(c => c.IsVisibleInTree() && Native.Active(c) != null &&
                Native.RewardsButton(c) is { Disabled: false } b && b.IsVisibleInTree()).ToList();
            var food = Food();
            // A heist's rewards get no tooltips: one with a fortune first, else any other, and a heist only when it is all there is.
            bool Heist(ThievingRecipeView c) => RatesOf(Native.Active(c)!, food).IsHeist;
            ThievingRecipeView? card = cards.FirstOrDefault(c => Native.Active(c)!.Stats.Fortune.Value != 0 && !Heist(c))
                ?? cards.FirstOrDefault(c => !Heist(c)) ?? cards.FirstOrDefault();
            if (card == null) { DevLog("rewards | no target card on the screen has a rewards button that can be pressed."); return true; }
            rewardsOf = Native.Active(card)!.Recipe;
            rewardsLooks = _marks!.DevLooks;
            Button button = Native.RewardsButton(card);
            button.EmitSignal(BaseButton.SignalName.Pressed);
            DevLog($"rewards | pressed '{button.Text}' of {rewardsOf.Id} (fortune {Native.Active(card)!.Stats.Fortune.Value:R}); {DevDoing()}");
            return true;
        });
        Add("rewards", () =>
        {
            if (rewardsOf == null) return true;
            var window = Native.Rewards(_page!);
            if (!window.IsVisibleInTree() || Native.Active(window)?.Recipe != rewardsOf) return false;
            // A few looks of the marks after it opened: the first may come before the game has laid the window out.
            if (_marks!.DevLooks - rewardsLooks < 3) return false;
            var s = Native.Active(window)!.Stats;
            string Section(GamesByMalcs.Melvor2.Localization.DoubleFormattedLabel label, double stat) =>
                $"'{label.Text}' (format '{label.FormatString}', value {label.Value:R}, the game's {stat:R})";
            DevLog($"rewards | '{Native.RecipeName(window).Text}': common {Section(Native.CommonChance(window), s.CommonLootChance.Value)}, rare " +
                $"{Section(Native.RareChance(window), s.RareLootChance.Value)}, coins {Section(Native.CoinChance(window), s.CurrencyLootChance.Value)} " +
                $"(reward {s.CurrencyReward.Value?.MinQuantity}-{s.CurrencyReward.Value?.MaxQuantity}), fortune {s.Fortune.Value:R}, doubling {s.DoublingChance.Total.Value:R}, " +
                $"completion table shown {Native.CompletionTable(window).IsVisibleInTree()}, the stage words {DevStageWords(window)}; kept for the window: " +
                (_readings.TryGetValue(rewardsOf, out RewardsReading? reading)
                    ? $"fortune {reading.Fortune:R}, " + string.Join(", ", reading.Tables.Select(t => $"{t.Key} {t.Value.Count} rows")) : "nothing"));
            Rect2 frame = window.GetGlobalRect();
            foreach (string rate in _marks.DevRates)
                DevLog("rate | " + rate);
            var tips = _marks.DevTargets.Values.Where(t => GodotObject.IsInstanceValid(t) && t.IsVisibleInTree()).ToList();
            var outside = tips.Where(t => !frame.Encloses(t.GetGlobalRect())).ToList();
            DevLog($"rate | the window {frame}, {tips.Count} quantities with a tooltip, none of them drawn" +
                (outside.Count > 0 ? $"  OUTSIDE THE WINDOW: {string.Join(", ", outside.Select(t => t.GetGlobalRect()))}" : ""));
            // The window as the game drew it: the tooltips draw nothing until the pointer rests on one.
            Shot("rewards");
            rewardsTip = tips.OrderBy(t => t.GetGlobalRect().Position.Y).ThenBy(t => t.GetGlobalRect().Position.X).FirstOrDefault();
            if (rewardsTip == null) DevLog("rate tooltip | no quantity has a tooltip in this window: none is pointed at.");
            _devTipSince = 0;
            return true;
        });
        Add("rewards tooltip", () => rewardsTip == null || DevTooltip(rewardsTip, "rewards-tooltip", "rate tooltip"), PointerStepTime);
        Add("close rewards", () =>
        {
            if (rewardsOf == null) return true;
            var window = Native.Rewards(_page!);
            if (!window.IsVisibleInTree()) return true;
            if (window.CloseButton is { Disabled: false } close && close.IsVisibleInTree()) close.EmitSignal(BaseButton.SignalName.Pressed);
            else window.Close();
            return false;   // read back on the next tick
        });
        Add("warning", () =>
        {
            var action = _thieving!.Action;
            var food = Food();
            TargetRates? real = action.IsStarted && action.CurrentRecipe is { } current ? RatesOf(current, food) : null;
            // Held back first, the real tag too, for a picture of the card without it (the next step).
            _marks!.DevHold(true);
            framesAt = (long)Engine.GetFramesDrawn();
            if (real is { Safe: false }) { DevLog($"warning | the real one: {real.Recipe.Id} is risky. {real.SafeWords}"); previewed = true; return true; }
            // A card on the screen whose name the game shows: the card of the target being stolen from if it is there.
            var cards = Cards(Native.ActionView(_page!)).Where(c => c.IsVisibleInTree() && Native.Active(c) != null && Native.CardName(c).Text != "???").ToList();
            var card = cards.FirstOrDefault(c => ReferenceEquals(Native.Active(c), action.CurrentRecipe)) ?? cards.FirstOrDefault();
            if (card == null) { DevLog("warning | no card on the screen shows a target's name: no picture."); _marks.DevHold(false); return true; }
            _marks.DevPreview = RatesOf(Native.Active(card)!, food);
            previewed = true;
            DevLog($"warning | a PREVIEW on the card of {_marks.DevPreview.Recipe.Id}: the character is {(real == null ? "not stealing" : $"stealing from {real.Recipe.Id}, which is {(real.Safe == true ? "safe" : "of unknown safety")}")}, " +
                $"so the real tag is not on show. The preview says \"Risky\" whatever the target is; its own verdict: {_marks.DevPreview.SafeWords}");
            return true;
        });
        // The card without the tag: once the card is found and the tag's place worked out, two frames after it was held back.
        Add("warning without", () =>
        {
            if (!previewed) return true;
            var card = _marks!.DevCard;
            if (card == null || !GodotObject.IsInstanceValid(card))
            {
                // The card is looked for once a second; the real target's card is not there while the page shows another place.
                if (Time.GetTicksMsec() - _devStarted < 3000) return false;
                DevLog($"warning | NO CARD: no card on the screen shows {_thieving!.Action.CurrentRecipe?.Recipe.Id ?? "the target"} (the page shows another place?): no tag to picture.");
                _marks.DevPreview = null;
                _marks.DevHold(false);
                previewed = false;
                return true;
            }
            if (!_marks.DevPlaced || _marks.DevTag?.Visible == true || (long)Engine.GetFramesDrawn() < framesAt + 2) return false;
            withoutTag?.Dispose();
            withoutTag = _ctx.Tree.Root.GetTexture().GetImage();
            // Let go: the mod shows the tag on its next round, by its own rules, and this step only looks.
            _marks.DevHold(false);
            framesAt = 0;
            return true;
        });
        Add("warning picture", () =>
        {
            if (!previewed) return true;
            var card = _marks!.DevCard;
            if (card == null || !GodotObject.IsInstanceValid(card) || !card.IsVisibleInTree())
                throw new InvalidOperationException("The card the tag was on went from the screen between the two pictures.");
            var tag = _marks.DevTag;
            bool shown = tag?.IsVisibleInTree() == true, left = _marks.DevWarning.Contains("LEFT OUT");
            if (!shown && !left) return false;
            // The picture is of the frame drawn last: the tag is on it only once a frame has been drawn after it came.
            if (framesAt == 0) { framesAt = (long)Engine.GetFramesDrawn(); return false; }
            if ((long)Engine.GetFramesDrawn() < framesAt + 2) return false;
            using var image = _ctx.Tree.Root.GetTexture().GetImage();
            string seen = shown ? DevSeen(image, withoutTag!, tag!, Native.CardName(card)) : "not on the picture, as it is left out";
            DevLog($"warning | {(_marks.DevPreview != null ? "PREVIEW | " : "")}{_marks.DevWarning}" + (shown ? $" | words '{DevWords(tag!)}' | tooltip '{tag!.TooltipText}'" : "") +
                $" | the picture: {seen}");
            SaveShot(image, "shot-thieving-warning.png");
            Crop(image, card, "crop-thieving-warning.png");
            Crop(withoutTag!, card, "crop-thieving-warning-without.png");
            withoutTag!.Dispose();
            withoutTag = null;
            _marks.DevPreview = null;
            return true;
        });
        Add("tries", () =>
        {
            bool stealing = _thieving!.Action.IsStarted;
            if (stealing && _devTries.Count < 8 && Time.GetTicksMsec() - _devStarted < 45000) return false;
            DevTries(stealing);
            return true;
        }, TriesTime);

        _devPutBackAt = _devSteps.Count;
        Add("put back", () =>
        {
            DevListen(false);
            if (_marks != null) { _marks.DevPreview = null; _marks.DevHold(false); }
            withoutTag?.Dispose();
            withoutTag = null;
            if (_page != null && Native.Rewards(_page) is { } rewards && rewards.IsVisibleInTree() && !before.RewardsOpen) rewards.Close();
            if (before.Settings == null) { if (File.Exists(SettingsFile)) File.Delete(SettingsFile); }
            else ModFiles.WriteWhole(SettingsFile, before.Settings);
            _settings = ModFiles.ReadJson<Settings>(SettingsFile, _ctx.Log);
            _window?.Free();
            _window = null;
            _view = null;
            _wanted = before.Wanted;
            // The page's place first, while the page is still in front; with its own Select button, as the click did.
            DevPlaceBack(before.Place, "put back");
            if (before.Page != null && !ReferenceEquals(Native.CurrentPage(view)?.Item1, before.Page)) view.SetCurrentPage(before.Page);
            Sync();
            SyncStripButton();
            DevPointerBack();
            return true;
        });
        Add("read back", () =>
        {
            // The page changes on the game's own time: read back once it has, or after a few seconds anyway.
            GamePage? page = Native.CurrentPage(view)?.Item1;
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
            var action = _thieving!.Action;
            Say(ReferenceEquals(action.SelectedCategory, before.Place), $"the Thieving page's place {before.Place?.Data.Id ?? "none"}", action.SelectedCategory?.Data.Id ?? "none");
            Say(action.IsStarted == before.Stealing && ReferenceEquals(action.CurrentRecipe, before.Target),
                $"the character {(before.Stealing ? "stealing from " + before.Target?.Recipe.Id : "not stealing")}", action.IsStarted ? "stealing from " + action.CurrentRecipe?.Recipe.Id : "not stealing");
            bool rewardsOpen = _page != null && Native.Rewards(_page).IsVisibleInTree();
            Say(rewardsOpen == before.RewardsOpen, $"View Possible Rewards {(before.RewardsOpen ? "open" : "closed")}", rewardsOpen ? "open" : "closed");
            string? settings = File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : null;
            Say(settings == before.Settings, before.Settings == null ? "settings.json not there" : "settings.json byte for byte",
                settings == null ? "not there" : $"{settings.Length} characters");
            Say(_devListened == null, "the game's Thieving no longer listened to", "still listened to");
            Say(_marks?.DevPreview == null, "no preview of the warning", "a preview still set");
            Say(_marks?.DevHeld != true, "the Risky tag not held back", "held back");
            said.Add("the pointer back where the real one is");
            _devPutBackWords = string.Join("; ", said) + $"; the character: {DevDoing()}";
            _devPutBackOk = ok;
            DevLog("put back: " + _devPutBackWords);
            return true;
        });

        foreach (string line in asked.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line == "click=stealing")
            {
                _devClickWhileStealing = true;
                DevLog("the request asks to click a line even while the character is stealing, to see that the theft goes on.");
                continue;
            }
            if (!line.StartsWith("fail=")) { DevLog($"the request's line '{line}' is not understood and left out."); continue; }
            string name = line["fail=".Length..].Trim();
            if (_devSteps.Take(_devPutBackAt).Any(s => s.Name == name))
            {
                _devFailOnPurpose = name;
                DevLog($"the request asks the step '{name}' to fail on purpose, to show the put-back.");
            }
            else DevLog($"no step is called '{name}', so none fails on purpose. The steps: {string.Join(", ", _devSteps.Take(_devPutBackAt).Select(s => s.Name))}.");
        }
    }

    ActiveThievingLocation? _devPlacePressed;

    // The page's place as it was, with that place's own Select button in the page's Select Location, as a player's
    // click there. True once the page shows it, or when there is nothing that can be put back.
    bool DevPlaceBack(ActiveThievingLocation? place, string when)
    {
        var action = _thieving!.Action;
        if (ReferenceEquals(action.SelectedCategory, place)) { _devPlacePressed = null; return true; }
        if (place == null)
        {
            DevLog($"{when} | the page showed no place before: no button shows none, so it stays on {action.SelectedCategory?.Data.Id} (NOT AS IT WAS)");
            return true;
        }
        if (_devPlacePressed == place) return false;   // pressed: read back on the next tick
        if (_page == null || PlaceButton(place) is not { } button)
        {
            DevLog($"{when} | {place.Data.Id} has no Select button that can be pressed: the page's place is NOT put back");
            return true;
        }
        button.EmitSignal(BaseButton.SignalName.Pressed);
        _devPlacePressed = place;
        DevLog($"{when} | pressed the Select button of {place.Data.Id} in the Thieving page's Select Location");
        return ReferenceEquals(action.SelectedCategory, place);
    }

    Button DevFoldButton() => _window!.Root.FindChildren("*", "Button", true, false).OfType<Button>()
        .FirstOrDefault(b => b.TooltipText is "Fold down to the title" or "Open the window again")
        ?? throw new InvalidOperationException("The window has no fold button.");

    // ---- the game's own events while the request runs (only listened to; every handler is guarded, so a fault
    // of the request never reaches the game's tick) ----------------------------------------------------------------

    void DevListen(bool on)
    {
        if (on)
        {
            if (_devListened != null || _thieving == null) return;
            _devListened = _thieving.Action;
            _devListened.ThievingActionPerformed += DevPerformed;
            _devListened.ThievingActionComplete += DevComplete;
            _devListened.Stunned += DevStunned;
            _devListened.StunEnded += DevStunEnded;
            _devListened.DamageTaken += DevDamage;
        }
        else if (_devListened != null)
        {
            _devListened.ThievingActionPerformed -= DevPerformed;
            _devListened.ThievingActionComplete -= DevComplete;
            _devListened.Stunned -= DevStunned;
            _devListened.StunEnded -= DevStunEnded;
            _devListened.DamageTaken -= DevDamage;
            _devListened = null;
        }
    }

    void DevEvent(Action read)
    {
        if (_devListenFailed) return;
        try { read(); }
        catch (Exception e)
        {
            _devListenFailed = true;
            _ctx.Log($"Dev: event | reading the game's event failed, the rest are not read: {e.GetType().Name}: {e.Message}");
        }
    }

    void DevPerformed(object? sender, ThievingActionPerformedEventArgs e) => DevEvent(() =>
    {
        if (_devTries.Count < 200) _devTries.Add((Time.GetTicksMsec(), e.WasSuccessful, e.ActiveRecipe.Recipe.Id));
        if (_devEvents.Count >= 40) return;
        _devEvents.Add($"try at {e.ActiveRecipe.Recipe.Id}: success={e.WasSuccessful}, interval {e.ActionInterval.TotalMilliseconds:0} ms, " +
            $"xp {string.Join(",", e.ExperienceGained.Select(x => $"{x.Key.Entity?.Id}/{x.Key.Id} {x.Value:0.###}"))}, " +
            $"items {string.Join(", ", e.ItemsGained.Select(i => $"{i.Item.Id} x{i.Quantity}"))}, coins {string.Join(", ", e.CurrenciesGained.Select(c => $"{c.Currency.Id} x{c.Quantity}"))}, " +
            $"stage {e.ActiveRecipe.CurrentStage} of {e.ActiveRecipe.Stats.MaxStages.Value} (complete {e.ActiveRecipe.IsComplete}) | the window counts xp per success " +
            $"{e.ActiveRecipe.Stats.Experience.XpToAdd.Value:R}, interval {e.ActiveRecipe.Stats.Interval.Value:R} ms");
    });

    void DevComplete(object? sender, ThievingRecipeEventArgs e) => DevEvent(() =>
    {
        if (_devEvents.Count < 40) _devEvents.Add($"complete at {e.Recipe?.Recipe.Id}: stage {e.Recipe?.CurrentStage} of {e.Recipe?.Stats.MaxStages.Value}");
    });

    void DevStunned(object? sender, ThievingActiveRecipeEventArgs e) => DevEvent(() =>
    {
        if (_devEvents.Count < 40) _devEvents.Add($"stunned at {e.ActiveRecipe.Recipe.Id}; the window counts {e.ActiveRecipe.Stats.StunInterval.Value:R} ms");
    });

    void DevStunEnded(object? sender, ThievingStunEndedEventArgs e) => DevEvent(() =>
    {
        if (_devEvents.Count < 40) _devEvents.Add($"stun ended at {e.ActiveRecipe.Recipe.Id} after {e.StunInterval.TotalMilliseconds:0} ms");
    });

    void DevDamage(object? sender, ThievingDamageTakenEventArgs e) => DevEvent(() =>
    {
        if (_devEvents.Count < 40) _devEvents.Add($"damage taken at {e.ActiveRecipe.Recipe.Id}: {e.DamageTaken} (its Damage {e.ActiveRecipe.Recipe.Damage}); HP now {_thieving?.Health.Current}");
    });

    // The game's tries against the window's Time and Success: the time between the first and the last try seen,
    // over the tries in between, and the share that succeeded.
    void DevTries(bool stealing)
    {
        foreach (string e in _devEvents) DevLog("event | " + e);
        if (_devTries.Count == 0)
        {
            DevLog(stealing ? "tries | the character is stealing, but no try came in 45 s." : "tries | the character is not stealing: no try of the game's to measure (start one by hand to have Time and Success checked).");
            return;
        }
        var food = ThievingFood.Read(_thieving!, _bank);
        foreach (var group in _devTries.GroupBy(t => t.Recipe))
        {
            var tries = group.ToList();
            var active = _thieving!.Action.ActiveRecipes.Values.FirstOrDefault(a => a.Recipe.Id == group.Key);
            TargetRates? r = active == null ? null : RatesOf(active, food);
            if (tries.Count < 2) { DevLog($"tries | {group.Key}: one try, too few to measure"); continue; }
            double span = (tries[^1].At - tries[0].At) / 1000.0, between = span / (tries.Count - 1);
            double share = tries.Count(t => t.Success) / (double)tries.Count;
            DevLog($"tries | {group.Key}: {tries.Count} tries in {span:0.#} s, {between:0.###} s from one to the next (the window: {r?.SecondsPerTry:0.###} s a try), " +
                $"{share:P0} successful (the window: {(r?.SuccessKnown == true ? TargetRates.Percent(r.Success) : "unknown")}); a few tries vary a lot: this checks the size, not the decimals");
        }
    }

    // ---- what is logged ---------------------------------------------------------------------------------------------

    void DevStrip()
    {
        if (_opener?.GetParent() is not Node strip) return;
        foreach (Node child in strip.GetChildren())
            DevLog($"strip | {child.Name} | {child.GetType().Name} | shown={(child as CanvasItem)?.IsVisibleInTree()} | " +
                $"icon {(child as Button)?.Icon?.ResourcePath ?? "none"} | tooltip '{(child as Control)?.TooltipText}'");
    }

    void DevFood()
    {
        var food = ThievingFood.Read(_thieving!, _bank);
        var stats = _thieving!.Food.Stats;
        var health = _thieving.Health;
        DevLog($"food | {(food.Selected ? food.Name(1) : "none chosen")}, in bank {food.InBank} ('{food.Name(food.InBank)}'), auto eat healing {food.Healing}, preserved {food.Preserved:R}, " +
            $"threshold {stats?.AutoEatThreshold.Value:R}, limit {stats?.AutoEatLimit.Value:R}, efficiency {stats?.AutoEatEfficiency.Value:R}, healing {stats?.Healing.Value} | " +
            $"HP {health.Current} of {health.Max}, Percent {health.Percent:R} -> auto eat at {food.AutoEatHp?.ToString("0.#") ?? "unknown"} HP");
    }

    // The HP bar: the damage its max hit mark comes from, and its two marks next to the window's numbers.
    void DevBar(string when)
    {
        var bar = Native.ActionView(_page!).PlayerHealthProgressBar;
        if (bar == null) { DevLog($"bar | {when} | no HP bar"); return; }
        var damage = Native.BarDamage(bar);
        var food = ThievingFood.Read(_thieving!, _bank);
        Rect2 rect = bar.GetGlobalRect();
        string Mark(Control? mark) => mark == null ? "none" : $"{(mark.IsVisibleInTree() ? "shown" : "hidden")} at {mark.GetGlobalRect()}, its middle at " +
            $"{(mark.GetGlobalRect().GetCenter().X - rect.Position.X) / Math.Max(1, rect.Size.X):P1} of the bar";
        var current = _thieving!.Action.CurrentRecipe;
        int max = damage?.Maximum ?? 0;
        double hp = Math.Max(1, food.MaxHp);
        DevLog($"bar | {when} | {rect} | the game's damage watcher {damage?.GetType().Name ?? "none"}: {damage?.Minimum}-{damage?.Maximum} for {current?.Recipe.Id ?? "no target"} " +
            $"(its Damage {current?.Recipe.Damage.ToString() ?? "-"}) | the game's auto eat mark {Mark(Native.AutoEatMark(bar))}; ours at {food.AutoEatHp?.ToString("0.#") ?? "-"} HP = " +
            $"{(food.AutoEatHp ?? 0) / hp:P1} of {food.MaxHp} | the game's max hit mark {Mark(Native.MaxHitMark(bar))}; the max hit {max} = {max / hp:P1} of the HP, " +
            $"{(food.Hp - max) / hp:P1} after one such hit at {food.Hp} | the game's words: '{Native.AutoEatWords(null)?.Text}', '{Native.MaxHitWords(null)?.Text}', " +
            $"'{Native.MaxHitTitle(null)?.Text}'");
    }

    // Every target card on the screen: the game's numbers next to the window's.
    int DevCards()
    {
        var food = ThievingFood.Read(_thieving!, _bank);
        int count = 0;
        foreach (ThievingRecipeView card in Cards(Native.ActionView(_page!)))
        {
            if (!card.IsVisibleInTree() || Native.Active(card) is not { } active) continue;
            count++;
            var r = RatesOf(active, food);
            var s = active.Stats;
            var success = Native.SuccessChance(card);
            string successText = success.GetParsedText();
            double? said = DevPercent(successText);
            double gameChance = _thieving!.Action.GetSuccessChance(active);
            var interval = Native.Interval(card);
            double gameSeconds = interval.Seconds, gameXp = Native.Xp(card).Value;
            int gameHit = Native.MaxHit(card).Value;
            bool canSteal = _thieving.Action.CanThieveRecipe(active);
            string name = Native.CardName(card).Text;
            var stages = Native.Stages(card);
            var mismatch = new List<string>();
            if (Math.Abs(gameSeconds - r.IntervalSeconds) > 0.005) mismatch.Add("interval");
            if (Math.Abs(gameXp - s.Experience.XpToAdd.Value) > 1e-6) mismatch.Add("xp");
            if (gameHit != r.MaxHit && !r.MaxHitFromBar) mismatch.Add("max hit");
            // The card writes two decimals of a percent: ours must round to what it says.
            if (r.SuccessKnown && said is { } words && Math.Abs(words - r.Success) > 0.00005 + 1e-9) mismatch.Add("success against the card's words");
            if (canSteal != r.Reachable) mismatch.Add("reachable");
            if ((name == "???") != r.Hidden) mismatch.Add("hidden");
            DevLog($"card | {active.Recipe.Id} | game: name '{name}', success '{successText}' ({success.Value:R}, format '{success.FormatString}'), GetSuccessChance {gameChance:R}, " +
                $"time {gameSeconds:R} s ('{DevWords(interval)}', format '{interval.IntervalFormat}'), xp '{Native.Xp(card).Text}' ({gameXp:R}), max hit {gameHit} " +
                $"('{Native.MaxHit(card).GetParsedText()}'), stealth '{Native.Stealth(card).GetParsedText()}', perception '{Native.Perception(card).GetParsedText()}', " +
                $"fortune '{Native.Fortune(card).GetParsedText()}', stages {stages.Current}/{stages.Maximum} (shown {stages.IsVisibleInTree()}), can steal {canSteal} | " +
                $"window: success {(r.SuccessKnown ? r.Success.ToString("R") : "unknown")} (the game keeps {r.SuccessRaw:R}; the card's words read as {said?.ToString("R") ?? "nothing"}), " +
                $"time {r.IntervalSeconds:R} s + stun {r.StunSeconds:R} s = {r.SecondsPerTry:0.###} s a try, xp per success {s.Experience.XpToAdd.Value:R}, max hit {r.MaxHit}" +
                $"{(r.MaxHitFromBar ? " (the bar's)" : "")}, hidden {r.Hidden}, reachable {r.Reachable} '{r.Needs}', heist {r.IsHeist} ({r.Stages} stages, the recipe's {active.Recipe.Stages}), " +
                $"items {(r.ItemsKnown ? r.FromRewardsWindow ? "counted from View Possible Rewards" : "counted from the game's tables" : "not counted: " + r.ItemsWhy)}, " +
                $"stats dirty {s.SuccessChance.Dirty}/{s.Interval.Dirty} | stun avoided {r.StunAvoided:R}, damage avoided {r.DamageAvoided:R}, doubling {r.Doubling:R}, " +
                $"skip stage {s.SkipStageChance.Total.Value:R}, same area {s.SameAreaPickpocketChance.Total.Value:R}, fortune {s.Fortune.Value:R} (flat {s.FlatFortune.Total.Value:R}, " +
                $"under success {s.FlatFortuneUnderSuccess.Total.Value:R})" + (mismatch.Count == 0 ? "" : "  MISMATCH " + string.Join(", ", mismatch)));
        }
        return count;
    }

    // "Success Chance: 60.06%" as a share: 0.6006. Null when it has no percent in it.
    static double? DevPercent(string text) =>
        Regex.Match(text, @"(\d[\d.,]*)\s*%") is { Success: true } m && double.TryParse(m.Groups[1].Value, NumberStyles.Number, CultureInfo.CurrentCulture, out double value)
            ? value / 100 : null;

    // Every place and what of it is listed: a place or target the game does not show yet is not.
    void DevPlaces()
    {
        var action = _thieving!.Action;
        foreach (ThievingLocation place in _thieving.Config.Categories)
        {
            bool shown = place.DisplayRequirements?.AreMet != false && action.ActiveLocations.ContainsKey(place);
            var recipes = action.ActiveLocations.TryGetValue(place, out ActiveThievingLocation? active) ? active.Recipes : [];
            var hidden = recipes.Where(r => r.Recipe.DisplayRequirements?.AreMet == false).Select(r => r.Recipe.Id).ToList();
            DevLog($"place | {place.Id}: {(shown ? "listed" : "NOT LISTED (the game does not show it yet)")}, open {place.Requirements?.AreMet != false}, " +
                $"{recipes.Count} targets, {hidden.Count} not shown by the game yet{(hidden.Count > 0 ? ": " + string.Join(", ", hidden) : "")}");
        }
    }

    void DevStats()
    {
        var all = ReadAll(ThievingFood.Read(_thieving!, _bank));
        // The stats of a target the screen does not show may wait to be worked out again (StatNode.Dirty).
        var dirty = all.Where(r => r.Active.Stats.SuccessChance.Dirty || r.Active.Stats.Interval.Dirty || r.Active.Stats.Experience.XpToAdd.Dirty)
            .Select(r => r.Recipe.Id).ToList();
        DevLog($"stats | {all.Count} targets, {dirty.Count} with a success, interval or XP the game has marked to work out again" +
            (dirty.Count > 0 ? ": " + string.Join(", ", dirty) : ""));
        foreach (var heist in all.Where(r => r.IsHeist))
            DevLog($"heist | {heist.Recipe.Id}: {heist.Stages} stages, xp per success {heist.Active.Stats.Experience.XpToAdd.Value:R}, success {(heist.SuccessKnown ? heist.Success.ToString("R") : "unknown")}, " +
                $"{heist.SecondsPerTry:0.###} s a try, completion rewards {heist.Recipe.CompletionDropTable?.Items.Count ?? 0}, common {heist.Recipe.CommonDropTable?.Items.Count ?? 0}, " +
                $"rare {heist.Recipe.RareDropTable?.Items.Count ?? 0}, coins {heist.Active.Stats.CurrencyReward.Value?.MinQuantity}-{heist.Active.Stats.CurrencyReward.Value?.MaxQuantity} " +
                $"at {heist.Active.Stats.CurrencyLootChance.Value:R}; the window shows XP and rewards as - ({heist.ItemsWhy})");
    }

    // Every line as it is drawn; with none, one line says so and no picture is taken.
    int DevLines(string when)
    {
        DevLog($"window | {when} | {_view!.DevChoices} | food line '{_view.DevFoodLine}'{(_view.DevNote != "" ? $" | note '{_view.DevNote}'" : "")}" +
            $"{(_view.DevEmpty != "" ? $" | empty '{_view.DevEmpty}'" : "")}");
        int n = 0;
        foreach (var line in _view.DevLines()) { DevLog($"line | {when} | {line.Drawn}"); n++; }
        DevLog($"window | {when} | {n} lines{(n == 0 ? ", so no picture" : "")}");
        return n;
    }

    void DevSize(string when)
    {
        var root = _window!.Root;
        Vector2 size = root.Size, screen = _ctx.Tree.Root.GetVisibleRect().Size;
        float bar = FooterHeight(), bottom = root.GetGlobalRect().End.Y;
        DevLog($"size | {when} | window {size.X:0}x{size.Y:0} at {_window.Style.Scale:0.##} (chosen {_settings.Scale:0.##}, largest {LargestScale(bar):0.##}), " +
            $"{_fullSize.X:0}x{_fullSize.Y:0} at 100% as measured, the list {OverviewView.Width:0} wide at 100%; screen {screen.X:0}x{screen.Y:0}, bottom bar {bar:0}, " +
            $"window bottom at {bottom:0}" + (bottom > screen.Y - bar + 0.5f ? "  OVER THE BOTTOM BAR" : ""));
    }

    static string DevStageWords(ViewThievingDropsModal window)
    {
        Label[]? labels = Native.StageLabels(window);
        return labels == null ? "none" : "[" + string.Join(" / ", labels.Where(l => l != null && GodotObject.IsInstanceValid(l)).Select(l => $"'{l.Text}'{(l.IsVisibleInTree() ? "" : " (hidden)")}")) + "]";
    }

    static string DevWords(Node node) => string.Join(" ", node.FindChildren("*", "Label", true, false).OfType<Label>().Where(l => l.IsVisibleInTree()).Select(l => l.Text));

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

    static string DevName(Control? control) => control == null ? "nothing" : $"{control.GetType().Name} '{control.Name}'";

    // The part of a control that can be seen: on the screen, and inside the list when it is a line of it.
    Rect2 DevSight(Control control)
    {
        Rect2 rect = control.GetGlobalRect().Intersection(_ctx.Tree.Root.GetVisibleRect());
        if (_view != null && GodotObject.IsInstanceValid(_view.Scroll) && _view.Scroll.IsAncestorOf(control))
            rect = rect.Intersection(_view.Scroll.GetGlobalRect());
        return rect;
    }

    // The pointer rests on a control (a line of the list, a quantity in View Possible Rewards) and its tooltip comes up.
    // A hand at the mouse moves it away: then it waits and points again. A tooltip that has not come up in 20 s of
    // pointing is one line, and the request goes on.
    bool DevTooltip(Control target, string shot = "tooltip", string what = "tooltip")
    {
        if (!GodotObject.IsInstanceValid(target) || !target.IsVisibleInTree()) throw new InvalidOperationException($"the {what}'s control is not on the screen.");
        if (DevHandAtMouse("the " + what)) { _devTipSince = 0; return false; }
        if (_devTipSince == 0) _devTipSince = Time.GetTicksMsec();
        Control? over = _ctx.Tree.Root.GuiGetHoveredControl();
        if (over == null || (over != target && !target.IsAncestorOf(over)))
        {
            Rect2 rect = DevSight(target);
            if (!rect.HasArea()) throw new InvalidOperationException($"no part of the {what}'s control is in sight.");
            DevPointer(rect.GetCenter());
            return false;
        }
        string tooltip = GameStyle.DevTooltips(_ctx.Tree.Root);
        string first = target.TooltipText.Split('\n')[0];
        if (first.Length > 0 && tooltip.Contains(first))
        {
            DevLog($"{what} | the pointer on {DevName(over)} at {target.GetGlobalRect()} | {tooltip}");
            Shot(shot);
            DevPointerBack();
            return true;
        }
        if (Time.GetTicksMsec() - _devTipSince < 20000) return false;
        DevLog($"{what} | NOT SHOWN in 20 s with the pointer on {DevName(over)}; its words: '{target.TooltipText}'; no picture of it");
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
        SaveShot(image, $"shot-thieving-{state}.png");
        if (!crop) return;
        if (_window is not { IsAlive: true, Visible: true } window) throw new InvalidOperationException("The window is not on the screen for " + state + ".");
        if (!window.Minimised && _view!.DevLines().Count(l => l.Root.IsVisibleInTree() && l.Root.GetGlobalRect().Intersects(_view.Scroll.GetGlobalRect())) == 0)
            throw new InvalidOperationException("No line of the list is in sight for " + state + ".");
        Crop(image, window.Root, $"crop-thieving-{state}.png");
    }

    void SaveShot(Image picture, string file)
    {
        Error result = picture.SavePng(Path.Combine(_ctx.Directory, file));
        if (result != Error.Ok) throw new IOException(file + ": " + result);
        DevLog(file + HandNote);
    }

    // ---- the Risky tag read from the picture ------------------------------------------------------------------------

    // Where a part of a control lies in a picture of the screen: in the game's own pixels on whatever canvas layer it is
    // drawn (ItemEncyclopediaMod.DevCanvasRect), stretched as the game stretches the screen, cut to the picture.
    Rect2I DevPixels(Control control, Image image, Rect2? part = null)
    {
        Rect2 screen = _ctx.Tree.Root.GetFinalTransform() * (control.GetGlobalTransformWithCanvas() * (part ?? new Rect2(Vector2.Zero, control.Size)));
        return ((Rect2I)screen).Intersection(new Rect2I(0, 0, image.GetWidth(), image.GetHeight()));
    }

    // The tag as the picture shows it, against the picture of the same place while it was held back.
    //   VISIBLE: a quarter or more of its pixels differ there, while the name beside it, which the tag does not touch, is
    //   the same in both pictures (so what differs is the tag, not the card). A tag on another ground changes nearly all
    //   of its pixels; one on a ground of its own colour still changes its edge, its picture and its words.
    //   READABLE: WCAG's contrast ratios, 4.5:1 for each of its words against its own ground and 3:1 for its edge (the
    //   edge of a thing) against the card around it.
    string DevSeen(Image with, Image without, PanelContainer tag, Label name)
    {
        if (with.GetSize() != without.GetSize()) return $"NOT VISIBLE: the two pictures differ in size ({with.GetSize()} and {without.GetSize()}), so they cannot be compared";
        Rect2I at = DevPixels(tag, with);
        if (!at.HasArea()) return "NOT VISIBLE: the tag's place is outside the picture";
        double changed = Changed(at);
        // The name's own text, left of the tag: the label reaches under the tag, so only its part before the tag is read.
        Rect2I nameAt = DevPixels(name, with);
        nameAt = new Rect2I(nameAt.Position, Math.Max(0, Math.Min(nameAt.Size.X, at.Position.X - 2 - nameAt.Position.X)), nameAt.Size.Y);
        double nameChanged = nameAt.HasArea() ? Changed(nameAt) : 1;
        string seen = changed >= 0.25 && nameChanged <= 0.01 ? "VISIBLE" : "NOT VISIBLE";
        seen += $": {changed:P0} of the tag's {at.Size.X * at.Size.Y:N0} pixels at {at} differ from the picture without it, {nameChanged:P0} of the name's beside it" +
            (nameChanged > 0.01 ? " (the card changed between the two pictures: they prove nothing)" : "");

        // Its own ground: inside its edge, left of its picture (the tag's margin, GameStyle.CountTag).
        var row = tag.GetNodeOrNull<Control>("Row");
        float margin = row?.Position.X ?? 6;
        Rect2I groundAt = DevPixels(tag, with, new Rect2(2, 3, Math.Max(1, margin - 3), Math.Max(1, tag.Size.Y - 6)));
        Color ground = Mean(with, groundAt.HasArea() ? groundAt : at), card = Mean(without, at);
        // Its edge: the strongest of the outer three pixel columns on each side, over the middle half of its height.
        int top = at.Position.Y + at.Size.Y / 4, bottom = at.End.Y - at.Size.Y / 4;
        Color edge = card;
        foreach (int x in new[] { at.Position.X, at.Position.X + 1, at.Position.X + 2, at.End.X - 3, at.End.X - 2, at.End.X - 1 })
            for (int y = top; y < bottom; y++)
                if (x >= at.Position.X && x < at.End.X && Contrast(with.GetPixel(x, y), card) > Contrast(edge, card)) edge = with.GetPixel(x, y);
        double edgeRatio = Contrast(edge, card);
        bool readable = edgeRatio >= 3;
        var words = new List<string>();
        foreach (Label label in row?.GetChildren().OfType<Label>().Where(l => l.Visible && l.Text != "") ?? [])
        {
            // The core of its letters: the pixel that stands out most from the tag's ground.
            Rect2I wordAt = DevPixels(label, with);
            Color ink = ground;
            for (int y = wordAt.Position.Y; y < wordAt.End.Y; y++)
                for (int x = wordAt.Position.X; x < wordAt.End.X; x++)
                    if (Contrast(with.GetPixel(x, y), ground) > Contrast(ink, ground)) ink = with.GetPixel(x, y);
            double ratio = Contrast(ink, ground);
            readable &= ratio >= 4.5;
            words.Add($"'{label.Text}' #{ink.ToHtml(false)} {ratio:0.0}:1 on the tag's ground");
        }
        if (words.Count == 0) readable = false;
        return seen + $" | {(readable ? "READABLE" : "HARD TO READ")}: the tag's ground #{ground.ToHtml(false)} on the card's #{card.ToHtml(false)}, its edge #{edge.ToHtml(false)} " +
            $"{edgeRatio:0.0}:1 on the card (3:1 wanted), {(words.Count > 0 ? string.Join(", ", words) : "no words found")} (4.5:1 wanted)";

        double Changed(Rect2I r)
        {
            int count = 0;
            for (int y = r.Position.Y; y < r.End.Y; y++)
                for (int x = r.Position.X; x < r.End.X; x++)
                {
                    Color a = with.GetPixel(x, y), b = without.GetPixel(x, y);
                    // A pixel the same frame draws the same way twice; a little room for rounding.
                    if (Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B))) > 2 / 255f) count++;
                }
            return count / (double)Math.Max(1, r.Size.X * r.Size.Y);
        }
    }

    static Color Mean(Image image, Rect2I r)
    {
        double red = 0, green = 0, blue = 0;
        int n = 0;
        for (int y = r.Position.Y; y < r.End.Y; y++)
            for (int x = r.Position.X; x < r.End.X; x++)
            {
                Color c = image.GetPixel(x, y);
                red += c.R; green += c.G; blue += c.B;
                n++;
            }
        return n == 0 ? Colors.Black : new Color((float)(red / n), (float)(green / n), (float)(blue / n));
    }

    // WCAG 2's contrast ratio of two colours on the screen, from their relative luminance.
    static double Contrast(Color a, Color b)
    {
        static double Linear(float c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        static double Luminance(Color c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    void Crop(Image image, Control part, string file)
    {
        // The picture is in the screen's pixels, the control in the game's own, which the game stretches.
        var frame = _ctx.Tree.Root.GetFinalTransform() * part.GetGlobalRect().Grow(10);
        var region = ((Rect2I)frame).Intersection(new Rect2I(0, 0, image.GetWidth(), image.GetHeight()));
        if (!region.HasArea()) throw new InvalidOperationException(file + ": the part is outside the picture.");
        using var cropped = image.GetRegion(region);
        Error result = cropped.SavePng(Path.Combine(_ctx.Directory, file));
        if (result != Error.Ok) throw new IOException(file + ": " + result);
        DevLog(file + HandNote);
    }
}
#endif
