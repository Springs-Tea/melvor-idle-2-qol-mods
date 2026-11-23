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
using GamesByMalcs.Melvor2.Entities.Mining;
using GamesByMalcs.Melvor2.GameMenus;
using GamesByMalcs.Melvor2.GameMenus.Pages;
using GamesByMalcs.Melvor2.InfoIcons;
using GamesByMalcs.Melvor2.InfoLabels;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.StatDisplays;
using GamesByMalcs.Melvor2.Tooltips;
using GamesByMalcs.Melvor2.Utils;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace MiningRates;

// Development: a file "mining.request" in the mod's folder runs the light check below with nobody at the keyboard, one
// step about every half second, and writes "mining.done" when it ends: "ok" or "failed at <step>: <why>", and in both
// cases what was put back. Every step logs what it read as one-line "Dev: ..." entries in MelvorModLoader.log. It is
// short (about a minute when nobody moves the mouse): no simulation, a few pictures. A second request,
// "miningwatch.request", only listens while the character mines (DevWatch.cs).
//   pwsh .claude\codex\check-mod.ps1 -Worktree <worktree> -Mod MiningRates -Request mining.request -Produces mining.done -Out <folder>
// The request file may say "fail=<step>": that step then fails on purpose, to show that a failed request puts
// everything back (-Text 'fail=click the line').
// It only looks, points and clicks the mod's own controls and, in step 7, the Mining page's own category buttons: no rock is
// selected, hidden or shown, nothing is started or stopped, and the page's "Show hidden" box is only read. The page's
// category filter and the scroll of its list of rocks change only in step 7, and are put back. Built on Fishing Rates'
// request (mods/FishingRates/Dev.cs): a click is made in two steps (DevPress): the pointer is put on the control, and a
// later step presses only when the game says the pointer is on that control. Nothing is pointed at or pressed while
// somebody is at the mouse: a step waits until the real pointer has been still for 5 s, and after 90 s the request
// ends, which puts everything back. A picture of something just shown is taken two drawn frames later, and its pixels
// prove what it shows (DevProof). The pictures go into the mod's folder (shot-mining-*.png, crop-mining-*.png).
//  1. Remembers the page on the screen, whether the window was open and folded, the round button, settings.json as it
//     is on disk, the Mining page's category filter and the scroll of its list of rocks, the "Show hidden" box, and what
//     mining is doing (MiningActionComponent.IsStarted, CurrentRock).
//  2. Opens the Mining page the way the sidebar does, if another page is open, and reads back that it is shown; stops
//     with one line when Mining cannot be opened that way (then open it by hand and send the request again).
//  3. "Dev: strip": every round button in the strip. Closes the window if it was open (a picture of the screen without
//     it, for the proofs), presses the round button, and opens the window up with its own button if it opened folded;
//     the round button must be lit.
//  4. "Dev: row": every line's numbers as the game gives them (raw) and as counted, as drawn (with colours), and its
//     tooltip. "Dev: game": for every card on the page, the game's own labels (interval, XP and its tooltip, break
//     chance, the items and XP of its per-hit and per-break lists, its picture) beside ours, DIFFER on a difference, and
//     the page's own order of cards against ours. "Dev: words": the page's own words for the rules the hour rests on.
//     "Dev: hidden": every rock not listed, and why. "Dev: lit". "Dev: cut" over every label of the picture.
//     crop-mining-window.png and, when the list scrolls, crop-mining-window-end.png with the list at its end.
//  5. Rests the pointer on a line: shot-mining-tooltip.png and "Dev: tooltip". A tooltip that does not come up in 20 s
//     is one line, and the request goes on.
//  6. Clicks "XP": "Dev: order" and crop-mining-sorted.png.
//  7. "Dev: click": on a page whose filter is on all categories, first presses the page's own button of one category
//     with the pointer, as a player would, so that a card can be under another category. Then clicks a line whose
//     rock's card is under another category or out of sight, and reads back the
//     page's category filter, whether the card is now in sight, the lit line, the window and what mining is doing
//     (shot-mining-clicked.png). MINING STARTED, MINING STOPPED, THE ROCK MINED CHANGED, NOT SHOWN and THE WINDOW CLOSED
//     mark what must not happen. Puts the filter and the scroll back, presses the page's own category button for the
//     same category with the pointer as a player would, and compares (DIFFER); then puts the filter and the scroll back
//     again, each only if it is still as the request left it.
//  8. "Dev: size": the window at its smallest and at its largest size (crop-mining-smallest.png,
//     crop-mining-largest.png), LARGER or OVER THE BOTTOM BAR when it does not fit, and "Dev: cut" at both.
//  9. Closes the window with its X: shot-mining-closed.png.
// 10. Puts back settings.json byte for byte from the copy read in step 1 (sort, size, place, fold), the window open or
//     closed and folded as it was, the page's category filter and scroll and the page on the screen (each only if it is
//     still as the request left it: otherwise the player changed it, and it is left as he did), and the pointer where
//     the real one is.
// 11. Reads all of it back from the screen, the game and the disk, what mining is doing too, and says in its last line
//     what was put back, with NOT AS IT WAS for anything that is not.
// A step that fails jumps to 10: a failed request puts everything back too.
public partial class MiningRatesMod
{
    const string RequestFile = "mining.request", DoneFile = "mining.done";
    // CombatGoals/DevWindowShots.cs and Fishing Rates: how long the real pointer must be still before a step points or
    // presses, and how long a step waits for that before the request ends.
    const ulong StillFor = 5000, GiveUpAfter = 90000, StepTime = 6000, PointerStepTime = GiveUpAfter + 10000;

    readonly List<(string Name, Func<bool> Run, ulong Timeout)> _devSteps = new();
    int _devAt, _devPutBackAt;
    ulong _devStarted;
    long _devFrameMark;
    string? _devFailure, _devFailOnPurpose, _devPutBackWords;
    bool _devPutBackOk, _devOpenedPage;
    // The screen without the window, before it was opened: what the pictures of the window are proved against.
    Image? _devWithout;

    // ---- the game's own parts of the Mining page, read only (names and types from tools/ApiDump) -----------------------

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrentPage")]
    static extern (GamePage, ActiveGamePage)? CurrentPage(GameView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ActiveRock")]
    static extern ActiveRockView ActiveRock(MiningActiveView page);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CategoryFilterView")]
    static extern MiningCategoryFilterView FilterView(MiningActiveView page);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_MiningRockScrollContainer")]
    static extern ScrollContainer RockScroll(MiningActiveView page);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RockSelectContainer")]
    static extern HFlowContainer RockList(MiningActiveView page);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CategoryFilterList")]
    static extern HBoxContainer FilterList(MiningCategoryFilterView filter);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ShowHiddenCheckBox")]
    static extern CheckBox ShowHiddenBox(MiningCategoryFilterView filter);

    // A rock's card.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Rock")]
    static extern MiningRock? CardRock(MiningRockSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RockName")]
    static extern Label CardName(MiningRockSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RockMedia")]
    static extern TextureRect CardPicture(MiningRockSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CategoryName")]
    static extern Label CardCategory(MiningRockSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_IntervalRange")]
    static extern IntervalDisplay CardInterval(MiningRockSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SkillXPPanel")]
    static extern SkillXPPanel CardXp(MiningRockSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RockBreakLabel")]
    static extern PercentLabel CardBreak(MiningRockSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RockBreakChanceTooltip")]
    static extern TextTooltip? CardBreakTooltip(MiningRockSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_PerHitGrantsContainer")]
    static extern GrantsContainer CardPerHit(MiningRockSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_PerHitNothingLabel")]
    static extern Label CardPerHitNothing(MiningRockSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_PerBreakGrantsContainer")]
    static extern GrantsContainer CardPerBreak(MiningRockSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LockedContainer")]
    static extern PanelContainer CardLocked(MiningRockSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ActiveBackground")]
    static extern PanelContainer CardActive(MiningRockSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ToggleVisibilityButton")]
    static extern TextureButton CardHideButton(MiningRockSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_MineButton")]
    static extern GamesByMalcs.Melvor2.CustomControls.AnimatedButton CardMineButton(MiningRockSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_MiningStats")]
    static extern MiningActionStatsView CardStats(MiningRockSelectView card);

    // The page's big panel of the rock being mined.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Rock")]
    static extern MiningRock? PanelRock(ActiveRockView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RockName")]
    static extern Label PanelName(ActiveRockView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_IntervalRange")]
    static extern IntervalDisplay PanelInterval(ActiveRockView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SkillXPPanel")]
    static extern SkillXPPanel PanelXp(ActiveRockView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RockBreakProgress")]
    static extern PercentLabel PanelProgress(ActiveRockView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_GrantsPerHit")]
    static extern GrantsContainer PanelPerHit(ActiveRockView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_GrantsPerBreak")]
    static extern GrantsContainer PanelPerBreak(ActiveRockView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ProgressTooltip")]
    static extern TextTooltip? PanelProgressTooltip(ActiveRockView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_MiningStats")]
    static extern MiningActionStatsView PanelStats(ActiveRockView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RockSelectedPanel")]
    static extern VBoxContainer PanelShown(ActiveRockView panel);

    // A time as the game writes it, an XP tag, and the words of a tooltip.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_IntervalLabel")]
    static extern Label IntervalLabel(BaseIntervalDisplay display);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SkillXpLabel")]
    static extern Int32FormattedLabel XpLabel(SkillXPPanel panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_XPTooltip")]
    static extern TextTooltip? XpTooltip(SkillXPPanel panel);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_contentText")]
    static extern ref List<string> Content(TextTooltip tooltip);

    // One item of a card's per-hit or per-break list, as the card shows it.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_isChance")]
    static extern ref bool IconIsChance(ItemInfoIcon icon);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_quantityValue")]
    static extern ref double IconQuantity(ItemInfoIcon icon);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_chanceValue")]
    static extern ref double IconChance(ItemInfoIcon icon);
    // Which of the game's stats a list shows.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ItemQuantityIcons")]
    static extern Dictionary<GamesByMalcs.Melvor2.Stats.Nodes.ValueStatNode<int>, ItemInfoIcon>? GrantQuantities(GrantsContainer grants);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ExperienceIcons")]
    static extern Dictionary<ExperienceStats, SkillExperienceIcon>? GrantXp(GrantsContainer grants);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ItemChanceGroups")]
    static extern Dictionary<GamesByMalcs.Melvor2.Stats.Nodes.ValuesStatNode<GamesByMalcs.Melvor2.Data.Items.ItemQuantityChance>, ItemInfoIconGroup>? GrantChances(GrantsContainer grants);

    // The page's own words for the rules the hour rests on (read and logged only).
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_rockBreakChanceStatDescription")]
    static extern ref LocalizedString BreakChanceWords(MiningRockSelectView? card);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_startMining")]
    static extern ref LocalizedString StartWords(MiningRockSelectView? card);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_stop")]
    static extern ref LocalizedString StopWords(MiningRockSelectView? card);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "ProgressTooltipText")]
    static extern ref string ProgressWords(ActiveRockView? panel);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "PercentIncreaseRockBreakChanceTitle")]
    static extern ref string IncreaseTitle(MiningActionStatsView? stats);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "PercentIncreaseRockBreakChanceTooltip")]
    static extern ref string IncreaseWords(MiningActionStatsView? stats);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "ShatterChanceTitle")]
    static extern ref string ShatterTitle(MiningActionStatsView? stats);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "ShatterChanceTooltip")]
    static extern ref string ShatterWords(MiningActionStatsView? stats);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "MinRockBreakBarChanceTitle")]
    static extern ref string MinimumTitle(MiningActionStatsView? stats);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "MinRockBreakBarChanceTooltip")]
    static extern ref string MinimumWords(MiningActionStatsView? stats);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_breakText")]
    static extern ref LocalizedString BreakLabelWords(ChanceIncreaseIconLabel? label);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_shatterText")]
    static extern ref LocalizedString ShatterLabelWords(ChanceIncreaseIconLabel? label);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_statName")]
    static extern ref LocalizedString BreakIconName(RockBreakIcon? icon);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_statDescription")]
    static extern ref LocalizedString BreakIconWords(RockBreakIcon? icon);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_statName")]
    static extern ref LocalizedString DoublingName(DoublingIcon? icon);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_statDescription")]
    static extern ref LocalizedString DoublingWords(DoublingIcon? icon);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_skillXpGained")]
    static extern ref LocalizedString XpGainedWords(SkillXPPanel? panel);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_xpPerSecond")]
    static extern ref LocalizedString XpPerSecondWords(SkillXPPanel? panel);

    void DevReset()
    {
        // Another character while a request runs: its listening is let go, and the request says it stopped.
        if (_devSteps.Count > 0)
        {
            DevUnwatchAll();
            DevEnd("stopped: another character was opened, nothing more was put back");
        }
        if (_devHits != null) DevWatchEnd("stopped: another character was opened", summarise: false);
        _devSteps.Clear();
        _devAt = 0;
    }

    void DevRequest()
    {
        if (GameAccess.IsComputingOffline || _stopped) return;
        // The silent watch runs on its own; a light check waits for it to end, and the other way round.
        if (_devHits != null) { DevWatchTick(); return; }
        DevWatchHand();
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
                DevLog($"Mining Rates check started, {_devPutBackAt} steps and the put-back.");
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
                _devFrameMark = 0;
                return;
            }
            DevUnwatchAll();
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
        _devFrameMark = 0;
        if (_devAt < _devSteps.Count) return;
        DevEnd($"{_devFailure ?? "ok"}; put back: {_devPutBackWords}{(_devPutBackOk ? "" : "  NOT ALL AS IT WAS")}");
    }

    void DevEnd(string result)
    {
        DevLog("Mining Rates check " + result);
        _devSteps.Clear();
        _devAt = 0;
        _devWithout?.Dispose();
        _devWithout = null;
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

    // What the request found, to be put back as it was.
    sealed record DevBefore(GamePage? Page, bool Wanted, bool Shown, bool? Folded, bool? Lit, string? Settings,
        MiningRecipeCategory? Filter, int? Scroll);

    void DevPlan(string asked)
    {
        _devSteps.Clear();
        _devAt = _devPutBackAt = 0;
        _devFailure = _devFailOnPurpose = _devPutBackWords = null;
        _devPutBackOk = _devOpenedPage = false;
        _devGuardSince = _devTipSince = 0;
        _devFrameMark = 0;
        _devAim = null;
        _devWithout?.Dispose();
        _devWithout = null;
        _devStarted = Time.GetTicksMsec();
        GameView view = GameAccess.FindGameView() ?? throw new InvalidOperationException("No character is open.");
        var action = _mining!.Action;
        var before = new DevBefore(CurrentPage(view)?.Item1, _wanted, _window is { IsAlive: true, Visible: true },
            _window is { IsAlive: true } ? _window.Minimised : null, GodotObject.IsInstanceValid(_opener) ? _opener!.ButtonPressed : null,
            File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : null, action.SelectedCagtegory, DevScrollNow());
        string miningAtStart = DevMiningState();
        bool hiddenAtStart = action.ShowHiddenRocks;
        // What the request itself left on the page last, so that the put-back changes only that (null: nothing yet).
        (MiningRecipeCategory? Filter, int? Scroll)? left = null;
        void Add(string name, Func<bool> run, ulong timeout = StepTime) => _devSteps.Add((name, run, timeout));
        DevLog($"before: page {before.Page?.Id ?? "none"}, window {(before.Wanted ? "open" : "closed")}{(before.Shown ? " and on the screen" : "")}" +
            $"{(before.Folded == true ? ", folded" : "")}, round button {(before.Lit is { } lit ? lit ? "lit" : "unlit" : "not made yet")}, settings.json " +
            (before.Settings == null ? "not there" : $"{before.Settings.Length} characters: {before.Settings}") +
            $" | {miningAtStart}; the page's category filter {before.Filter?.Id ?? "all"}, the list of rocks scrolled to {before.Scroll?.ToString() ?? "not found"}, " +
            $"Show hidden {(hiddenAtStart ? "ticked" : "not ticked")} ({action.HiddenRockCount} rocks hidden by the player) | the real pointer at {DisplayServer.MouseGetPosition()}");

        Add("open the Mining page", () =>
        {
            if (PageShown) return true;
            if (_devOpenedPage) return false;   // read back on the next ticks: the page is found once a second
            var option = _game!.Layout.Sidebar.Options.FirstOrDefault(o =>
                o.Page is EntityPage p && ReferenceEquals(p.Entity, _mining) &&
                o.Requirements?.AreMet != false && o.DisplayRequirements?.AreMet != false);
            if (option?.Page is not EntityPage page)
                throw new InvalidOperationException("Mining cannot be opened from the sidebar: open the Mining page by hand and send the request again.");
            view.SetCurrentPage(page);
            _devOpenedPage = true;
            return false;
        });
        Add("the round button", () =>
        {
            if (!PageShown || _opener?.IsVisibleInTree() != true) return false;
            // The list of rocks of a page the request had to open is read now that it is there.
            if (before.Scroll == null) before = before with { Scroll = DevScrollNow() };
            DevLog($"page | the page is {CurrentPage(view)?.Item1?.Id ?? "none"}, the Mining page is shown: {PageShown}; its list of rocks scrolled to {before.Scroll?.ToString() ?? "not found"}");
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
            int rows = DevRows("open");
            if (rows == 0) throw new InvalidOperationException("The window lists no rock.");
            DevGame();
            DevWords();
            DevHidden();
            DevLit();
            DevSize("open");
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
        Button? target = null;
        Image? beforeTip = null;
        Add("point at a line", () =>
        {
            if (_list!.DevScroll.ScrollVertical != 0) return false;
            var lines = _list.DevLines().ToList();
            target = (lines.FirstOrDefault(l => !l.Rock.Locked).Button ?? lines.FirstOrDefault().Button)
                ?? throw new InvalidOperationException("No line to point at.");
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
        Add("tooltip", () => DevTooltip(target!, beforeTip), PointerStepTime);

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

        // A click on a line whose rock's card is under another category, or out of sight, then the page's own category
        // button for the same category, to compare. The page's filter and scroll are put back after each.
        RockRates? clicked = null;
        DevClickEvents? events = null;
        Image? beforeClick = null;
        string miningBefore = "", ours = "", ourEvents = "";
        bool filterChanged = false;
        MiningRecipeCategory? startFrom = null, filterBeforeClick = null;
        Add("a category to start from", () =>
        {
            var action = _mining!.Action;
            if (action.SelectedCagtegory != null) { DevLog($"click | the page's filter is on {action.SelectedCagtegory.Id}: the click starts from it"); return true; }
            // On all categories no card is under another category: the page's own button of a category is pressed first,
            // as a player would, so that the click on a line of another category has a filter to change. Put back at the end.
            var rocks = _list!.DevLines().Select(l => l.Rock).Where(r => !r.OnPage && r.Rock.Category != null).ToList();
            foreach (var (category, button) in Native.CategoryButtons(FilterView(_page!)) ?? new Dictionary<MiningRecipeCategory, Button>())
            {
                if (!GodotObject.IsInstanceValid(button) || button.Disabled || !button.IsVisibleInTree()) continue;
                if (!rocks.Any(r => !ReferenceEquals(r.Rock.Category, category))) continue;
                startFrom = category;
                return DevPress(button, $"the page's own category button of {category.Id} ('{button.Text}'), to start the click from it");
            }
            DevLog("click | the page's filter is on all categories and no category button can be pressed: the click only scrolls");
            return true;
        }, PointerStepTime);
        Add("the category to start from", () =>
        {
            if (startFrom == null) return true;
            var action = _mining!.Action;
            if (!ReferenceEquals(action.SelectedCagtegory, startFrom) && Time.GetTicksMsec() - _devStarted < 3000) return false;
            left = (action.SelectedCagtegory, DevScrollNow());
            DevLog($"click | after the page's own button of {startFrom.Id}: the filter {action.SelectedCagtegory?.Id ?? "all"}" +
                (ReferenceEquals(action.SelectedCagtegory, startFrom) ? "" : "  NOT SET BY ITS OWN BUTTON") + "; put back at the end");
            Refresh();
            return true;
        });
        Add("a line to click", () =>
        {
            var lines = _list!.DevLines().Select(l => l.Rock).Where(r => r.Clickable).ToList();
            clicked = lines.FirstOrDefault(r => Native.Card(_page!, r.Rock) is { Visible: false }) ?? lines.FirstOrDefault();
            if (clicked == null) { DevLog("click | no line shows something a click would change: none is clicked."); return true; }
            _list.DevScroll.EnsureControlVisible(DevLineOf(clicked));
            miningBefore = DevMiningState();
            filterBeforeClick = _mining!.Action.SelectedCagtegory;
            DevLog($"click | the line of {clicked.Rock.Recipe.Id} ({clicked.Rock.Category?.Id ?? "no category"}); before: {DevPageState(clicked.Rock)}; {miningBefore}");
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
            events ??= new DevClickEvents(_mining!.Action, DevLog);
            return DevClickLine(clicked);
        }, PointerStepTime);
        Add("the rock shown", () =>
        {
            if (clicked == null) return true;
            // The list of rocks scrolls two drawn frames after the click (ScrollToCard); the picture two frames later.
            if (_scrollTo != null || !DevFramesDrawn()) return false;
            var action = _mining!.Action;
            MiningRock rock = clicked.Rock;
            Refresh();
            MiningRockSelectView? card = Native.Card(_page!, rock);
            bool inSight = card != null && Native.InSight(card);
            var lit = _list!.DevLines().Where(l => l.Rock.OnPage).Select(l => l.Rock.Rock.Recipe.Id).ToList();
            string mining = DevMiningState();
            ours = DevFilterState(rock);
            ourEvents = string.Join(", ", events!.Seen);
            filterChanged = !ReferenceEquals(action.SelectedCagtegory, filterBeforeClick);
            var litMeant = new List<string>();
            if (action.CurrentRock is { } mined && _list.DevLines().Any(l => ReferenceEquals(l.Rock.Rock, mined))) litMeant.Add(mined.Recipe.Id);
            DevLog($"click | after the click on the line of {rock.Recipe.Id}: {DevPageState(rock)}; the game's events [{ourEvents}]; {mining}" +
                (inSight ? "" : "  NOT SHOWN") + (mining == miningBefore ? "" : "  MINING CHANGED") +
                (lit.SequenceEqual(litMeant) ? "" : $"  THE LIT LINE IS [{string.Join(", ", lit)}]") +
                (IsOpen ? "" : "  THE WINDOW CLOSED"));
            Shot("clicked");
            if (card != null && beforeClick != null) DevLog("click | the card in shot-mining-clicked.png: " + DevProof(beforeClick, card, "the card"));
            events.Dispose();
            events = null;
            left = (action.SelectedCagtegory, DevScrollNow());
            DevLog("click | the page's filter and scroll put back for the page's own button: " + DevPageBack(before, ref left));
            return true;
        });
        Add("the page's category button", () =>
        {
            if (clicked == null) return true;
            if (!filterChanged) { DevLog("click | the click left the category filter as it was: no category button to compare it with."); return true; }
            MiningRecipeCategory category = clicked.Rock.Category!;
            Button button = (Native.CategoryButtons(FilterView(_page!)) is { } buttons && buttons.TryGetValue(category, out Button? b) ? b : null)
                ?? throw new InvalidOperationException($"the page has no category button for {category.Id}");
            events ??= new DevClickEvents(_mining!.Action, DevLog);
            return DevPress(button, $"the page's own category button of {category.Id} ('{button.Text}')");
        }, PointerStepTime);
        Add("the page's button compared", () =>
        {
            if (clicked == null || !filterChanged) return true;
            if (!DevFramesDrawn()) return false;
            var action = _mining!.Action;
            string game = DevFilterState(clicked.Rock), gameEvents = string.Join(", ", events!.Seen), mining = DevMiningState();
            DevLog($"click | after the page's own category button: {game}; the game's events [{gameEvents}]; {mining}" +
                (mining == miningBefore ? "" : "  MINING CHANGED") + " | " +
                (game == ours ? "the filter as after the click on the line" : $"DIFFER from the click on the line ({ours})") + "; " +
                (DevSameEvents(gameEvents, ourEvents) ? "the same events" : $"EVENTS DIFFER from the click on the line ([{ourEvents}])"));
            events.Dispose();
            events = null;
            left = (action.SelectedCagtegory, DevScrollNow());
            DevLog("click | the page's filter and scroll put back: " + DevPageBack(before, ref left));
            return true;
        });
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
            if (open != null) DevLog("shot-mining-closed.png | where the window was: " + DevChanged(open, now, openAt, "the window's place", "gone from the picture", "STILL IN THE PICTURE"));
            return true;
        });

        _devPutBackAt = _devSteps.Count;
        Add("put back", () =>
        {
            events?.Dispose();
            events = null;
            if (before.Settings == null) File.Delete(SettingsFile);
            else ModFiles.WriteWhole(SettingsFile, before.Settings);
            _settings = ModFiles.ReadJson<Settings>(SettingsFile, _ctx.Log);
            _settings.Sort ??= RatesList.NameKey;
            _window?.Free();
            _window = null;
            _list = null;
            _fitPending = false;
            _wanted = before.Wanted;
            string page = DevPageBack(before, ref left);
            DevLog("put back | the page's filter and scroll: " + page);
            if (before.Page != null && _devOpenedPage && !ReferenceEquals(CurrentPage(view)?.Item1, before.Page))
            {
                // Only the page the request opened itself: a page the player went to meanwhile is his.
                if (PageShown) view.SetCurrentPage(before.Page);
                else DevLog($"put back | the page is no longer Mining, which the request opened: the player went to {CurrentPage(view)?.Item1?.Id ?? "none"}, and it is left there");
            }
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
            var action = _mining!.Action;
            Say(ReferenceEquals(action.SelectedCagtegory, before.Filter), $"the page's category filter {before.Filter?.Id ?? "all"}", action.SelectedCagtegory?.Id ?? "all");
            int? scroll = DevScrollNow();
            Say(scroll == before.Scroll, $"the page's list of rocks scrolled to {before.Scroll?.ToString() ?? "-"}", scroll?.ToString() ?? "-");
            Say(action.ShowHiddenRocks == hiddenAtStart, $"Show hidden {(hiddenAtStart ? "ticked" : "not ticked")}", action.ShowHiddenRocks ? "ticked" : "not ticked");
            string miningNow = DevMiningState();
            Say(miningNow == miningAtStart, miningAtStart, miningNow);
            said.Add("the pointer back where the real one is");
            _devPutBackWords = string.Join("; ", said) + $"; the order before the click on XP was {(firstBefore.Length > 0 ? firstBefore : "not read")}";
            _devPutBackOk = ok;
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

    bool DevMiningNow() => _game!.ActionManager.ActiveActions.Any(a => ReferenceEquals(a.Entity, _mining));

    // What mining is doing: no click of the request may change it.
    string DevMiningState()
    {
        var action = _mining!.Action;
        return $"mining: started {action.IsStarted}, active {DevMiningNow()}, the rock mined {action.CurrentRock?.Recipe.Id ?? "none"}";
    }

    // ---- a click on a line: the Mining page's filter and scroll -------------------------------------------------------

    // Where the page's list of rocks is scrolled to, or null when the list is not found.
    int? DevScrollNow()
    {
        try { return _page != null && GodotObject.IsInstanceValid(_page) && RockScroll(_page) is { } scroll ? scroll.ScrollVertical : null; }
        catch (Exception) { return null; }
    }

    // The page's filter, its buttons as they are drawn, and the rock's card.
    string DevFilterState(MiningRock rock)
    {
        var action = _mining!.Action;
        MiningCategoryFilterView filter = FilterView(_page!);
        string buttons = string.Join(", ", FilterList(filter).FindChildren("*", "Button", true, false).OfType<Button>()
            .Select(b => $"'{b.Text}'{(b.ButtonPressed ? " pressed" : "")}{(b.IsVisibleInTree() ? "" : " (hidden)")}"));
        MiningRockSelectView? card = Native.Card(_page!, rock);
        int cards = Native.RockViews(_page!)?.Values.Count(c => GodotObject.IsInstanceValid(c) && c.Visible) ?? -1;
        return $"the filter {action.SelectedCagtegory?.Id ?? "all"}, its buttons [{buttons}], {cards} cards shown, the card of {rock.Recipe.Id} " +
            (card == null ? "NOT ON THE PAGE" : card.Visible ? "shown" : "hidden");
    }

    string DevPageState(MiningRock rock)
    {
        MiningRockSelectView? card = Native.Card(_page!, rock);
        return DevFilterState(rock) + $", in sight {(card != null && Native.InSight(card))}, the list scrolled to {DevScrollNow()?.ToString() ?? "-"}";
    }

    // The page's filter and scroll as they were, each only if it is still as the request left it: otherwise the player
    // changed it meanwhile, and it is left as he did. The filter is set back through the page's own buttons, the way a
    // click on a line sets it (CategoryFilter), or for all categories through the page's "All" button.
    string DevPageBack(DevBefore before, ref (MiningRecipeCategory? Filter, int? Scroll)? left)
    {
        if (left is not { } was) return "nothing to put back: the request changed neither";
        var action = _mining!.Action;
        var said = new List<string>();
        if (ReferenceEquals(action.SelectedCagtegory, before.Filter)) said.Add("the filter as it was");
        else if (!ReferenceEquals(action.SelectedCagtegory, was.Filter))
            said.Add($"the filter {action.SelectedCagtegory?.Id ?? "all"} is not what the request left ({was.Filter?.Id ?? "all"}): the player changed it, and it is left");
        else
        {
            if (before.Filter != null) CategoryFilter(before.Filter);
            else DevAllButton();
            said.Add(ReferenceEquals(action.SelectedCagtegory, before.Filter) ? "the filter put back" : $"the filter NOT AS IT WAS ({action.SelectedCagtegory?.Id ?? "all"})");
        }
        int? scroll = DevScrollNow();
        if (scroll == before.Scroll) said.Add("the scroll as it was");
        else if (scroll != was.Scroll) said.Add($"the scroll {scroll} is not what the request left ({was.Scroll}): the player moved it, and it is left");
        else if (before.Scroll is { } back)
        {
            RockScroll(_page!).ScrollVertical = back;
            said.Add($"the scroll put back to {back}");
        }
        left = null;
        return string.Join(", ", said);
    }

    // The page's "All" button: the one of its filter buttons that is not a category's. Pressed as a click on a line
    // presses a category's (CategoryFilter); if the filter is still on a category, it is set to all through the member.
    void DevAllButton()
    {
        MiningCategoryFilterView filter = FilterView(_page!);
        var categories = Native.CategoryButtons(filter)?.Values.ToHashSet() ?? new HashSet<Button>();
        Button? all = FilterList(filter).FindChildren("*", "Button", true, false).OfType<Button>().FirstOrDefault(b => !categories.Contains(b));
        if (all != null)
        {
            if (all.ToggleMode && !all.ButtonPressed) all.ButtonPressed = true;
            all.EmitSignal(BaseButton.SignalName.Pressed);
        }
        if (_mining!.Action.SelectedCagtegory != null)
        {
            DevLog($"put back | the page's All button {(all == null ? "was not found" : $"'{all.Text}' did not set all categories")}: set through the member");
            _mining.Action.SelectedCagtegory = null;
        }
    }

    Button DevLineOf(RockRates rock) => _list!.DevLines().First(l => ReferenceEquals(l.Rock.Rock, rock.Rock)).Button;

    // The line is pressed the way a player's hand would (DevPress), once the list has scrolled it into sight.
    bool DevClickLine(RockRates rock)
    {
        Button line = DevLineOf(rock);
        if (!DevSight(line).HasArea()) return false;
        return DevPress(line, $"the line of {rock.Name}");
    }

    // The same events in another order are the same.
    static bool DevSameEvents(string a, string b) =>
        a.Split(", ", StringSplitOptions.RemoveEmptyEntries).Order().SequenceEqual(b.Split(", ", StringSplitOptions.RemoveEmptyEntries).Order());

    // The game's events about the page's filter and about mining, listened to while one click is made. Each listener
    // runs inside a try; they are taken off by Dispose, which Stop and Detach reach through DevUnwatchAll.
    sealed class DevClickEvents : IDisposable
    {
        public readonly List<string> Seen = new();
        readonly List<Action> _off = new();
        static DevClickEvents? s_current;

        public DevClickEvents(MiningActionComponent action, Action<string> log)
        {
            s_current?.Dispose();
            s_current = this;
            void Safe(Action add)
            {
                try { add(); }
                catch (Exception e) { log("click events | a listener failed: " + e.Message); }
            }
            EventHandler<ValueChangedEventArgs<MiningRecipeCategory>> filter = (_, e) => Safe(() => Seen.Add($"the filter {e.NewValue?.Id ?? "all"}"));
            EventHandler<ValueChangedEventArgs<MiningRock>> mined = (_, e) => Safe(() => Seen.Add($"THE ROCK MINED CHANGED to {e.NewValue?.Recipe.Id ?? "none"}"));
            EventHandler<ValueChangedEventArgs<bool>> hidden = (_, e) => Safe(() => Seen.Add($"SHOW HIDDEN CHANGED to {e.NewValue}"));
            EventHandler<GamesByMalcs.Melvor2.Components.Actions.ActionEventArgs> started = (_, _) => Safe(() => Seen.Add("MINING STARTED")),
                stopped = (_, _) => Safe(() => Seen.Add("MINING STOPPED"));
            action.SelectedCagtegoryChanged += filter;
            action.CurrentRockChanged += mined;
            action.ShowHiddenRocksChanged += hidden;
            action.Started += started;
            action.Stopped += stopped;
            _off.Add(() =>
            {
                action.SelectedCagtegoryChanged -= filter;
                action.CurrentRockChanged -= mined;
                action.ShowHiddenRocksChanged -= hidden;
                action.Started -= started;
                action.Stopped -= stopped;
            });
            foreach (MiningRock rock in action.Rocks.Values)
            {
                EventHandler<ValueChangedEventArgs<bool>> hid = (_, e) => Safe(() => Seen.Add($"A ROCK WAS {(e.NewValue ? "HIDDEN" : "SHOWN")}: {rock.Recipe.Id}"));
                rock.IsHiddenChanged += hid;
                _off.Add(() => rock.IsHiddenChanged -= hid);
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

    static string DevTooltipText(TextTooltip? tooltip) => tooltip == null ? "none" : DevWordsOf(string.Join(" / ", Content(tooltip) ?? []));

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
        foreach (var (r, button, drawn, picture) in _list!.DevLines())
        {
            count++;
            MiningActionStats stats = r.Rock.Stats;
            string rawHit = string.Join(", ", (stats.OnHitItemChances?.Values ?? []).Select(n => $"{n.Value?.Item?.Id} {R(n.Value?.Chance ?? double.NaN)} x{n.Value?.Quantity}"));
            DevLog($"row | {when} | {r.Rock.Recipe.Id} ({r.Rock.Category?.Id}) | locked={r.Locked}, level={r.Level}, needs '{r.Needs}', on the page={r.OnPage}, " +
                $"hidden by the player={r.Hidden}, a click shows it={r.Clickable} | raw: interval {R(stats.Interval.Value)} ms, break chance {R(stats.RockBreakChance.Value)}, " +
                $"minimum prospecting {R(stats.MinimumProspecting.Value)}, the rock's progress now {R(r.Rock.Progress)}, XP {R(stats.Experience.XpToAdd.Value)}, " +
                $"product {r.Rock.Recipe.Product?.Item?.Id} x{stats.ProductQuantity.Value} (base {r.Rock.Recipe.Product?.Quantity}), doubling {R(stats.DoublingChance?.Total.Value ?? double.NaN)}, " +
                $"more ore {R(stats.AdditionalProductQuantityChance?.Total.Value ?? double.NaN)}, shatter {R(stats.ShatterChance?.Total.Value ?? double.NaN)} " +
                $"(the game's multipliers: ore x{MiningActionComponent.ShatterProductMultiplier}, XP x{R(MiningActionComponent.ShatterExperienceMultiplier)}), " +
                $"stone {r.Rock.Recipe.StoneItem?.Id ?? "none"} {R(stats.StoneItemChance?.Total.Value ?? double.NaN)}, essence {r.Rock.Recipe.EssenceItem?.Id ?? "none"} " +
                $"{R(stats.EssenceItemChance?.Total.Value ?? double.NaN)}, bar {r.Rock.Recipe.BarItem?.Id ?? "none"} {R(stats.BarItemChance?.Total.Value ?? double.NaN)}, " +
                $"items of a hit [{rawHit}], the recipe's base product chance {R(r.Rock.Recipe.BaseProductChance)}, base interval {r.Rock.Recipe.BaseInterval} | " +
                $"counted: {R(r.HitSeconds)} s a hit, {R(r.HitsPerHour)} hits/h, break chance {R(r.BreakChance)}, hits a break {(r.HitsPerBreak is { } h ? R(h) : "-")}, " +
                $"XP {R(r.XpEach)} a hit = {R(r.XpPerHour)}/h, ore {r.OreQuantity} a break x(1+{R(r.Doubling)}) = {R(r.OrePerBreak)}, ore/h {(r.OrePerHour is { } o ? R(o) : "-")}, " +
                $"time per ore {(r.SecondsPerOre is { } s ? R(s) : "-")}, ore price {(r.OrePrice is { } p ? R(p) : "-")}, gold/h {(r.GoldPerHour is { } g ? R(g) : "-")} " +
                $"({R(r.PerHitGoldPerHour)} of it from the items of a hit) | items of a hit [{string.Join(", ", r.PerHit.Select(d => $"{d.Item.Id} {R(d.Chance)} x{d.Quantity} each {R(d.Each)} = {R(d.PerHour)}/h, gold {(d.GoldPerHour is { } dg ? R(dg) : "-")}/h"))}] | " +
                $"not counted [{string.Join(", ", r.NotCountedItems.Select(n => $"{n.Item.Id} {R(n.Chance)}").Concat(r.NotCounted.Select(n => $"{n.Key} {R(n.Nodes.Total.Value)}")))}] | " +
                $"picture {DevTexture(picture)}{(picture != null && _pictures!.IsStandIn(picture) ? "  STAND-IN DRAWN" : "")}");
            DevLog($"row | {when} | drawn {drawn}");
            DevLog($"row | {when} | tooltip '{button.TooltipText}'");
        }
        var rocks = ReadRocks();
        DevLog($"rows | {when} | {count} lines, {DevRowsInSight()} in sight; the game has {_mining!.Action.Rocks.Count} rocks, {rocks.Count} listed, " +
            $"{rocks.Count(r => r.Locked)} locked" + (count != rocks.Count ? "  MISMATCH" : ""));
        return DevRowsInSight();
    }

    // The game's own labels for every card of the page and for its big panel, next to ours; and the page's order.
    void DevGame()
    {
        var action = _mining!.Action;
        var rocks = new Dictionary<MiningRock, RockRates>(ReadRocks().Select(r => KeyValuePair.Create(r.Rock, r)), ReferenceEqualityComparer.Instance);
        var cards = Native.RockViews(_page!) ?? throw new InvalidOperationException("The Mining page has no cards.");
        DevLog($"game | the page has {cards.Count} cards; its category filter {action.SelectedCagtegory?.Id ?? "all"}");
        foreach (var (rock, card) in cards)
        {
            if (!GodotObject.IsInstanceValid(card)) continue;
            RockRates ours = rocks.TryGetValue(rock, out RockRates? listed) ? listed : RockRates.Read(rock, _mining, Shown);
            IntervalDisplay interval = CardInterval(card);
            SkillXPPanel xp = CardXp(card);
            PercentLabel chance = CardBreak(card);
            Texture2D? shown = CardPicture(card).Texture;
            bool sameRock = ReferenceEquals(CardRock(card), rock);
            DevLog($"game | card {rock.Recipe.Id} '{CardName(card).Text}' ('{CardCategory(card).Text}'){(sameRock ? "" : "  BOUND TO ANOTHER ROCK")} | " +
                $"shown={card.Visible}, in sight={Native.InSight(card)}, locked part shown={CardLocked(card).IsVisibleInTree()}, being mined={CardActive(card).IsVisibleInTree()}, " +
                $"hide button pressed={CardHideButton(card).ButtonPressed}, its button reads '{CardMineButton(card).Text}' | " +
                $"time: game {R(interval.Seconds)} s '{IntervalLabel(interval).Text}', ours {R(ours.HitSeconds)} s{(Math.Abs(interval.Seconds - ours.HitSeconds) < 0.0005 ? "" : "  DIFFER")} | " +
                $"XP: game '{((Node)XpLabel(xp) as Label)?.Text}' ({XpLabel(xp).Value}), ours {R(ours.XpEach)} a hit: {DevXpSame(XpLabel(xp).Value, ours.XpEach)} | " +
                $"the game's XP tooltip '{DevTooltipText(XpTooltip(xp))}' | {DevXpPerSecond(DevTooltipText(XpTooltip(xp)), ours)} | " +
                $"break chance: game '{chance.Text}' ({R(chance.Value)}), ours {R(ours.BreakChance)} (raw {R(rock.Stats.RockBreakChance.Value)}): {DevChanceSame(chance.Value, ours.BreakChance)}; " +
                $"its tooltip '{DevTooltipText(CardBreakTooltip(card))}' | per hit [{DevGrants(CardPerHit(card), rock)}]{(CardPerHitNothing(card).IsVisibleInTree() ? $" '{CardPerHitNothing(card).Text}'" : "")} | " +
                $"per break [{DevGrants(CardPerBreak(card), rock)}] | its picture {DevTexture(shown)}, ours {DevTexture(ours.Picture)}" +
                (ours.Picture == null || ReferenceEquals(shown, ours.Picture) || shown?.ResourcePath == ours.Picture.ResourcePath ? "" : "  MISMATCH"));
        }
        // The page's own order of cards, left to right and top to bottom, against the window's.
        var order = RockList(_page!).GetChildren().OfType<MiningRockSelectView>().Where(c => c.Visible).Select(c => CardRock(c)?.Recipe.Id ?? "?").ToList();
        var oursOrder = ReadRocks().Where(r => Native.Card(_page!, r.Rock) is { Visible: true }).Select(r => r.Rock.Recipe.Id).ToList();
        DevLog($"game | the page's order of the cards it shows [{string.Join(", ", order)}]; the window's order of the same [{string.Join(", ", oursOrder)}]" +
            (order.SequenceEqual(oursOrder) ? "" : "  DIFFER"));

        ActiveRockView panel = ActiveRock(_page!);
        if (PanelRock(panel) is not { } mined || !PanelShown(panel).IsVisibleInTree()) { DevLog("game | the page's big panel shows no rock."); return; }
        RockRates big = RockRates.Read(mined, _mining, Shown);
        DevLog($"game | the big panel: {mined.Recipe.Id} '{PanelName(panel).Text}' | time: game {R(PanelInterval(panel).Seconds)} s, ours {R(big.HitSeconds)} s" +
            $"{(Math.Abs(PanelInterval(panel).Seconds - big.HitSeconds) < 0.0005 ? "" : "  DIFFER")} | XP: game {XpLabel(PanelXp(panel)).Value}, ours {R(big.XpEach)}: {DevXpSame(XpLabel(PanelXp(panel)).Value, big.XpEach)} | " +
            $"its XP tooltip '{DevTooltipText(XpTooltip(PanelXp(panel)))}' | the break bar: '{PanelProgress(panel).Text}' ({R(PanelProgress(panel).Value)}), the rock's progress {R(mined.Progress)}, " +
            $"minimum {R(big.MinimumProspecting)} | the bar's tooltip '{DevTooltipText(PanelProgressTooltip(panel))}' | per hit [{DevGrants(PanelPerHit(panel), mined)}] | " +
            $"per break [{DevGrants(PanelPerBreak(panel), mined)}]");
    }

    // What a card's list shows, item by item, and which of the rock's own stats it is bound to: the game's own word on
    // what comes with a hit and what with a break.
    static string DevGrants(GrantsContainer grants, MiningRock rock)
    {
        MiningActionStats stats = rock.Stats;
        var shown = new List<string>();
        foreach (Node node in grants.FindChildren("*", "", true, false))
        {
            if (node is ItemInfoIcon icon)
                shown.Add($"{icon.Item?.Id ?? "?"} x{R(IconQuantity(icon))}{(IconIsChance(icon) ? $" at {R(IconChance(icon))}" : "")}{(icon.IsVisibleInTree() ? "" : " (hidden)")}");
            else if (node is SkillExperienceIcon xp)
                shown.Add($"XP '{DevWordsOf(string.Join(" ", xp.FindChildren("*", "Label", true, false).OfType<Label>().Select(l => l.Text)))}'{(xp.IsVisibleInTree() ? "" : " (hidden)")}");
        }
        var bound = new List<string>();
        foreach (var key in GrantQuantities(grants)?.Keys ?? Enumerable.Empty<GamesByMalcs.Melvor2.Stats.Nodes.ValueStatNode<int>>())
            bound.Add(ReferenceEquals(key, stats.ProductQuantity) ? "the rock's ProductQuantity" : $"another quantity ({key.Value})");
        foreach (var key in GrantXp(grants)?.Keys ?? Enumerable.Empty<ExperienceStats>())
            bound.Add(ReferenceEquals(key, stats.Experience) ? "the rock's Experience" : "another XP");
        foreach (var key in GrantChances(grants)?.Keys ?? Enumerable.Empty<GamesByMalcs.Melvor2.Stats.Nodes.ValuesStatNode<GamesByMalcs.Melvor2.Data.Items.ItemQuantityChance>>())
            bound.Add(ReferenceEquals(key, stats.OnHitItemChances) ? $"the rock's OnHitItemChances ({key.Values.Count})"
                : $"another list of chances [{string.Join(", ", key.Values.Select(v => $"{v.Value?.Item?.Id} {R(v.Value?.Chance ?? double.NaN)}"))}]");
        return $"shows {string.Join(", ", shown)}; bound to {string.Join(", ", bound)}";
    }

    // The game's tag is a whole number (Int32FormattedLabel), while its XP is not (ExperienceComponent.Xp is a Double).
    static string DevXpSame(int game, double ours)
    {
        int down = (int)Math.Floor(ours + 1e-9);
        return game == down ? Math.Abs(ours - down) < 1e-9 ? "the same" : "the tag is ours rounded down"
            : game == (int)Math.Round(ours) ? "the tag is ours rounded" : "DIFFER";
    }

    static string DevChanceSame(double game, double ours) =>
        Math.Abs(game - ours) < 1e-6 ? "the same, a fraction" : Math.Abs(game - ours * 100) < 1e-4 ? "the same, the label keeps percent" : "DIFFER";

    // Which rule the game's own XP tooltip follows: every number in it against the XP per second of a hit (ours), and of
    // a hit's XP over the time of a hit and a break (which would show XP once a break).
    static string DevXpPerSecond(string tooltip, RockRates ours)
    {
        var numbers = Regex.Matches(tooltip, @"\d[\d,]*(?:\.\d+)?").Select(m => double.Parse(m.Value.Replace(",", ""), CultureInfo.InvariantCulture)).ToList();
        if (!ours.HasTime) return $"numbers in it [{string.Join(", ", numbers.Select(R))}]";
        double perHit = ours.XpEach / ours.HitSeconds;
        bool Near(double value) => numbers.Any(n => Math.Abs(n - value) <= Math.Max(0.006, 0.002 * value));
        return $"numbers in it [{string.Join(", ", numbers.Select(R))}]; XP per second on every hit {R(perHit)}: " +
            (Near(perHit) ? "IN IT, the game counts the XP of a hit over the time of a hit" : "NOT IN IT") +
            $"; ours a hit {R(ours.XpEach)} is {(Near(ours.XpEach) ? "in it" : "not in it")}";
    }

    // The page's own words for the rules the hour rests on: the break chance, the break bar, shatter, the minimum, the
    // doubling, the XP tag; and the rows of its list of bonuses with their tooltips.
    void DevWords()
    {
        string Read(string what, Func<string> words)
        {
            try { return $"{what} '{DevWordsOf(words())}'"; }
            catch (Exception e) { return $"{what} could not be read ({e.GetType().Name})"; }
        }
        static string Tr(string key) => key + (TranslationServer.Translate(key).ToString() is { } t && t != key ? $" = {t}" : "");
        DevLog("words | " + string.Join(" | ", new[]
        {
            Read("the card's break chance", () => BreakChanceWords(null).Text),
            Read("the card's start", () => StartWords(null).Text),
            Read("the card's stop", () => StopWords(null).Text),
            Read("the break bar's tooltip", () => Tr(ProgressWords(null))),
            Read("the rock break chance increase", () => Tr(IncreaseTitle(null)) + ": " + Tr(IncreaseWords(null))),
            Read("shatter", () => Tr(ShatterTitle(null)) + ": " + Tr(ShatterWords(null))),
            Read("the minimum break bar", () => Tr(MinimumTitle(null)) + ": " + Tr(MinimumWords(null))),
            Read("after a hit: a break", () => BreakLabelWords(null).Text),
            Read("after a hit: a shatter", () => ShatterLabelWords(null).Text),
            Read("the rock break icon", () => BreakIconName(null).Text + ": " + BreakIconWords(null).Text),
            Read("the doubling icon", () => DoublingName(null).Text + ": " + DoublingWords(null).Text),
            Read("the XP tag", () => XpGainedWords(null).Text + " / " + XpPerSecondWords(null).Text),
        }));
        void Rows(string what, MiningActionStatsView? stats)
        {
            if (stats == null || !GodotObject.IsInstanceValid(stats)) { DevLog($"words | {what}: none"); return; }
            var rows = stats.FindChildren("*", "", true, false).OfType<ModifierStatLabel>()
                .Select(r => $"'{DevWordsOf(r.StatName ?? "")}' {(r.IsVisibleInTree() ? "" : "(hidden) ")}tooltip '{DevWordsOf(r.StatTooltipContent ?? "")}'");
            DevLog($"words | {what}: {string.Join(" | ", rows)}");
        }
        ActiveRockView panel = ActiveRock(_page!);
        Rows("the big panel's list of bonuses", PanelStats(panel));
        var locked = ReadRocks().Where(r => r.Locked).Select(r => r.Rock).ToHashSet(ReferenceEqualityComparer.Instance);
        var (rock, card) = (Native.RockViews(_page!) ?? new Dictionary<MiningRock, MiningRockSelectView>())
            .Where(c => GodotObject.IsInstanceValid(c.Value) && c.Value.Visible && !locked.Contains(c.Key)).Select(c => ((MiningRock?)c.Key, (MiningRockSelectView?)c.Value)).FirstOrDefault();
        Rows($"the list of bonuses of the card of {rock?.Recipe.Id ?? "no rock"}", card == null ? null : CardStats(card));
        var keys = ReadRocks().SelectMany(r => r.NotCounted).Select(b => b.Key).Distinct().ToList();
        DevLog($"bonus names | {(keys.Count == 0 ? "no bonus that is not counted" : string.Join(", ", keys.Select(k => _bonusNames.Of(k) is { } name ? $"{k}: the game's '{name}'" : $"{k}: NOT READ, the tooltip says '{BonusNames.Kind(k)}'")))}");
    }

    // Every rock of the game that is not listed, and why; and every listed rock the page does not show under all.
    void DevHidden()
    {
        var action = _mining!.Action;
        var listed = new HashSet<MiningRock>(ReadRocks().Select(r => r.Rock), ReferenceEqualityComparer.Instance);
        int hidden = 0;
        foreach (var (recipe, rock) in action.Rocks)
        {
            MiningRockSelectView? card = Native.Card(_page!, rock);
            if (listed.Contains(rock))
            {
                if (action.SelectedCagtegory == null && card is { Visible: false }) DevLog($"hidden | {recipe.Id} is listed, and its card is hidden under all categories  NOT ON THE PAGE");
                continue;
            }
            hidden++;
            DevLog($"hidden | {recipe.Id} '{recipe.DisplayName.Text}' ({rock.Category?.Id}) | the rock is shown when: {DevConditions(recipe.DisplayRequirements)} | " +
                $"its category is shown when: {DevConditions(rock.Category?.DisplayRequirements)} | to mine it: {DevConditions(recipe.Requirements)} | " +
                $"hidden by the player {rock.IsHidden}, Show hidden {(action.ShowHiddenRocks ? "ticked" : "not ticked")} | its card " +
                (card == null ? "none" : card.Visible ? "SHOWN ON THE PAGE" : "hidden"));
        }
        DevLog($"hidden | {hidden} of the game's {action.Rocks.Count} rocks are not listed; the page counts {action.HiddenRockCount} hidden by the player");
    }

    static string DevConditions(ConditionGroup? group) => group == null || group.Conditions == null || group.Conditions.Count == 0 ? "always"
        : $"{(group.AreMet ? "met" : "NOT MET")}: " + string.Join("; ", group.Conditions.Select(c => $"{(c.IsMet ? "met" : "not met")}{(c.IsOr ? " (or)" : "")} '{DevWordsOf(c.GetDescription(false, false))}'"));

    // The rock the page's big panel shows against the lit line.
    void DevLit()
    {
        var action = _mining!.Action;
        MiningRock? panel = PanelRock(ActiveRock(_page!));
        var lit = _list!.DevLines().Where(l => l.Rock.OnPage).Select(l => l.Rock.Rock).ToList();
        bool right = action.CurrentRock == null ? lit.Count == 0 : lit.Count == 1 && ReferenceEquals(lit[0], action.CurrentRock);
        DevLog($"lit | the rock mined {action.CurrentRock?.Recipe.Id ?? "none"}, the big panel's {panel?.Recipe.Id ?? "none"}, mining now: {DevMiningNow()} | " +
            $"the lit line{(lit.Count == 1 ? " is" : "s are")} {(lit.Count == 0 ? "none" : string.Join(", ", lit.Select(r => r.Recipe.Id)))}" +
            $" (drawn lit: {string.Join(", ", _list.DevLines().Where(l => l.Drawn.EndsWith("lit=True")).Select(l => l.Rock.Rock.Recipe.LocalId))})" +
            (right ? "" : "  MISMATCH"));
    }

    string DevOrder(string when)
    {
        string order = string.Join(", ", _list!.DevLines().Select(l => l.Rock.Rock.Recipe.LocalId));
        DevLog($"order | {when} | {order}");
        return order;
    }

    string DevInSight()
    {
        Rect2 sight = _list!.DevScroll.GetGlobalRect();
        return string.Join(", ", _list.DevLines().Where(l => l.Button.IsVisibleInTree() && l.Button.GetGlobalRect().Intersects(sight)).Select(l => l.Rock.Rock.Recipe.LocalId));
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
    // ... where the game trims it with its tooltip holding it, and PAST THE EDGE where it ends past the edge of its line
    // or of the window.
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
                    : wide <= room + 0.5f ? "whole" : label.TextOverrunBehavior != TextServer.OverrunBehavior.NoTrimming ? "SHORTENED WITH ..." : "CUT";
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

    // ---- through the game's input, as a player's hand would (Fishing Rates' Dev.cs) -----------------------------------

    Vector2I _devPointer;
    bool _devPointerSeen, _devHandSinceStep;
    ulong _devHandAt, _devGuardSince, _devTipSince;
    Control? _devAim;
    Vector2 _devAimAt;

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

    // The part of a control that can be seen: on the screen, inside the window's list when it is a line of it, and
    // inside the page's list of rocks when it is in there.
    Rect2 DevSight(Control control)
    {
        Rect2 rect = control.GetGlobalRect().Intersection(_ctx.Tree.Root.GetVisibleRect());
        if (_list != null && GodotObject.IsInstanceValid(_list.DevScroll) && _list.DevScroll.IsAncestorOf(control))
            rect = rect.Intersection(_list.DevScroll.GetGlobalRect());
        else if (Native.ScrollOf(control) is { } scroll)
            rect = rect.Intersection(scroll.GetGlobalRect());
        return rect;
    }

    // The pointer rests on a line and the game's tooltip comes up. A hand at the mouse moves it away: then it waits and
    // points again. A tooltip that has not come up in 20 s of pointing is one line, and the request goes on.
    bool DevTooltip(Button target, Image? before)
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
            // Two drawn frames after it came up, for the picture.
            if (!DevFramesDrawn()) return false;
            DevLog("tooltip " + tooltip);
            Shot("tooltip");
            if (before != null && DevTooltipRect() is { } at)
            {
                using var now = _ctx.Tree.Root.GetTexture().GetImage();
                DevLog("shot-mining-tooltip.png | " + DevChanged(before, now, at, "the tooltip's place", "VISIBLE", "NOT VISIBLE"));
            }
            DevPointerBack();
            return true;
        }
        if (Time.GetTicksMsec() - _devTipSince < 20000) return false;
        DevLog($"tooltip | NOT SHOWN in 20 s with the pointer on {DevName(over)}; its words are in the 'row ... tooltip' lines, and there is no picture of it");
        DevPointerBack();
        return true;
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
    // without it (DevProof). A picture of a window that is not there, or of a list with no line in sight, is not taken.
    void Shot(string state, bool crop = false)
    {
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
            Save(image, $"shot-mining-{state}.png");
            using var region = image.GetRegion(part);
            Save(region, $"crop-mining-{state}.png", proof);
            return;
        }
        Save(image, $"shot-mining-{state}.png");
    }

    // A control in the picture just taken against a picture taken before it came: DevChanged over its place.
    string DevProof(Image before, Control control, string what)
    {
        using var now = _ctx.Tree.Root.GetTexture().GetImage();
        return DevChanged(before, now, control.GetGlobalRect(), what, "VISIBLE", "NOT VISIBLE");
    }

    // How many of the pixels of a place (in the root's coordinates) differ between two pictures of the screen: a quarter
    // or more is "yes" (something else is drawn there now), as Thieving Overview proves its Risky tag (DevSeen).
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
        Error result = region.SavePng(Path.Combine(_ctx.Directory, "crop-mining-strip.png"));
        if (result != Error.Ok) throw new IOException("crop-mining-strip.png: " + result);
        DevLog($"crop-mining-strip.png | the button at {_opener.GetGlobalRect()}, lit={_opener.ButtonPressed}, {strip.GetChildCount()} buttons in the strip{HandNote}");
    }
}
#endif
