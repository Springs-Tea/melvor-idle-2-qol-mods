#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
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
// everything back (-Text 'fail=click' fails immediately after the line was pressed).
// It only looks, points and clicks the mod's own controls and, in step 8, the Mining page's own category buttons: no rock is
// selected, hidden or shown, nothing is started or stopped, and the page's box for hidden rocks (ShowHiddenRocks) is only
// read, before and after every press of a game button (SHOW HIDDEN CHANGED if it moved). In step 4 the pointer only
// rests on a card's break chance, for the game's own tooltip. The page's category filter and the scroll of its list of
// rocks change only in step 8, and are put back through the page's own buttons, never through the member behind them:
// the filter is restored with its own button, and the scroll is read again after the cards settle. The request uses
// the player's starting category so no setup filter stands while a pointer step waits. Built on Fishing Rates' request
// (mods/FishingRates/Dev.cs): a click is made in two steps (DevPress): the pointer is put on the control, and a later step
// presses only when the game says the pointer is on that control; a control of the game under the mod's own window is
// pointed at beside it, or not at all (said at once). Nothing is pointed at or pressed while somebody is at the mouse: a
// step waits until the real pointer has been still for 5 s, and after 90 s the request ends, which puts everything back.
// A picture of something just shown is taken two drawn frames later, and its pixels prove what it shows (DevChanged). The
// pictures go into the mod's folder (shot-mining-*.png, crop-mining-*.png).
//  1. Remembers the page on the screen, whether the window was open and folded, the round button, settings.json as it
//     is on disk, the Mining page's category filter (the member and the page's buttons and cards as drawn), the scroll of
//     its list of rocks, the box for hidden rocks, and what mining is doing (MiningActionComponent.IsStarted, CurrentRock).
//  2. Opens the Mining page the way the sidebar does, if another page is open, and reads back that it is shown; stops
//     with one line when Mining cannot be opened that way (then open it by hand and send the request again).
//  3. "Dev: strip": every round button in the strip. Closes the window if it was open.
//  4. "Dev: break rule": rests the pointer on the break chance of a card in sight (never the rock being mined while
//     mining runs), preferably one whose rock's break bar is part filled, on the control the game's tooltip labels
//     (Tooltip.Labelling), until the game shows its tooltip for it, and reads the tooltip's "Base chance" and "Current
//     chance", each by the game's own words just before it, against the rock's numbers taken on the tick it was first
//     seen shown: which of them the game calls its current chance reads again the rule the window rests on
//     (RockRates.Known, settled by check-3), AS SETTLED or NOT AS SETTLED, and NOT DECIDED when those numbers moved
//     before the read. shot-mining-break-tooltip.png, proved over the tooltip's words where they are drawn. A tooltip
//     that is not shown in 20 s is one line, and the request goes on.
//  5. A picture of the screen without the window, for the proofs; presses the round button, and opens the window up
//     with its own button if it opened folded; the round button must be lit.
//  6. "Dev: row": every line's numbers as the game gives them (raw) and as counted, the hits a break takes by the rule
//     and by each other reading of the break bar (RockRates.ExpectedHits), as drawn (with colours), and its tooltip.
//     "Dev: game": for every card on the page, the game's own labels (interval, XP and its tooltip, break chance, the
//     items and XP of its per-hit and per-break lists, its picture) beside ours, DIFFER on a difference, the page's own
//     order of cards against ours, and the big panel's break bar and minimum when it shows a rock. "Dev: minimum": the
//     minimum break bar of every rock that has one. "Dev: words": the page's own words for the rules the hour rests on
//     (the ones the game keeps as constants read by reflection, the member named when one is not there), whether the
//     guide still quotes the game's words, and the filter's words and buttons. "Dev: hidden": every rock not
//     listed, and why. "Dev: lit". "Dev: tips": the longest line of every tip against Fishing Rates' longest. "Dev: cut"
//     over every label of the picture. crop-mining-window.png and, when the list scrolls, crop-mining-window-end.png.
//     "Dev: room": the window's place as drawn, the page's list, and every line's card against them (Native.Place):
//     in sight clear of the window, a scroll that shows it clear, or no room beside the window; MISMATCH when the line
//     is clickable otherwise than that says, NOT SAID when a line without room does not say so in its tooltip.
//  7. Rests the pointer on a line, one whose card has no room beside the window when there is one (its tooltip says
//     so), then on the name of the column with the longest tip: shot-mining-tooltip.png, shot-mining-header-tooltip.png
//     and "Dev: tooltip". A tooltip that does not come up in 20 s is one line, and the request goes on. Then clicks
//     "XP": "Dev: order" and crop-mining-sorted.png.
//  8. Clicks a line, reads its card, the category, the scroll and the mining events, and puts the page back.
//     If the click changed the category, compares it with the page's own button after the put-back.
//  9. "Dev: size": the window at its smallest and at its largest size (crop-mining-smallest.png,
//     crop-mining-largest.png), LARGER or OVER THE BOTTOM BAR when it does not fit, and "Dev: cut" at both. Closes the
//     window with its X: shot-mining-closed.png.
// 10. Puts back settings.json byte for byte from the copy read in step 1 (sort, size, place, fold), the window open or
//     closed and folded as it was, the page's category filter and its scroll two drawn
//     frames later, each only if it is still as the request left it (otherwise the player changed it, and it is left as
//     he did); reads the Mining page's filter and scroll back while it is still on the screen; then the page that was on
//     the screen, only if the request opened Mining itself, and the pointer where the real one is.
// 11. Reads the rest back from the screen, the game and the disk, what mining is doing too, and says in its last line
//     what was put back, with NOT AS IT WAS for anything that is not, and for any put-back on the way that was not,
//     and NOT READ BACK for what could not be read (the done line then ends NOT ALL AS IT WAS, NOT ALL READ BACK).
// A step that fails jumps to 10: a failed request puts everything back too. If the mod stops while a request runs, the
// done file says so, and settings.json is written back from the copy (DevStopped).
public partial class MiningRatesMod
{
    const string RequestFile = "mining.request", DoneFile = "mining.done";
    // CombatGoals/DevWindowShots.cs and Fishing Rates: how long the real pointer must be still before a step points or
    // presses, and how long a step waits for that before the request ends.
    const ulong StillFor = 5000, GiveUpAfter = 90000, StepTime = 6000, PointerStepTime = GiveUpAfter + 10000;

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
    // carries them, not only the log (the code review of turn 3, fault 7).
    readonly List<string> _devOnTheWay = new();
    // The screen without the window, before it was opened: what the pictures of the window are proved against.
    Image? _devWithout;
    // settings.json as the request found it on disk (null: not there), for DevStopped too.
    string? _devSettingsCopy;
    bool _devSettingsRead;
    string? _devSettingsLeft;
    bool _devWantedLeft;
    int? _devListScrollLeft;
    long? _devListSettle;
    string? DevSettingsNow() => File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : null;
    int? DevWindowScroll() => _list?.ScrollAt;
    // One rule for each part of the Mining page the request changes, the filter and the scroll, each on its own
    // (review 3 of the orchestrator): what the request left on a part is read from the page itself once the page has
    // settled after the request's own press (_devSettle), and kept (_devOwnFilter, _devOwnScroll; null: the request
    // owns nothing of that part). At the put-back the part is read again: still exactly what the request left, it is put
    // back; otherwise the player changed it, and it is his: left, said in one line, never taken as the request's own. A
    // part of his does not touch the other part's put-back. No wait on the mouse decides whose a value is: the press
    // guard (DevHandAtMouse) only keeps the request's presses off his hand.
    sealed record DevOwn<T>(T Value);
    DevOwn<MiningRecipeCategory?>? _devOwnFilter;
    DevOwn<int?>? _devOwnScroll;
    // The filter the owned scroll was read under: a scroll belongs to the list of one category, so once the page shows
    // another category than this one, whoever put it there, the scroll is the player's list's and is never written
    // (review 4: a category click of his after the request's filter was back).
    MiningRecipeCategory? _devScrollUnder;
    // A press of the request's that changes the page, not settled yet: the frame it was made in, and whether it was a
    // change of the request's (Both: what it left on both parts is read once it settled) or a put-back's press (only the
    // scroll the request still owns is read again: the press laid the list out anew). Read on the first tick at least two
    // drawn frames later, and once the window's own scroll to a card is done (DevSettle).
    (long Frame, bool Both, MiningRecipeCategory? Filter)? _devSettle;
    // The tick the last settled reading was taken in: the scroll is put back on a later tick, after it is read again.
    long _devSettledFrame = -1;
    // The Mining page just before the request's own press that changed its filter or scroll, while nothing of the
    // request's was left on it: what every put-back goes back to (null: the request has changed neither). The page at
    // the start of the request may differ, if the player changed it in the first steps: that change is his, and stays.
    DevPageWas? _devOrigin;
    // The page's box for hidden rocks just before the last press of a game button (DevHiddenMoved).
    bool _devHiddenBefore;
    // What of the page the request let go of because the player changed it ("filter", "scroll"): the read back says so,
    // and does not count it against the put-back.
    readonly HashSet<string> _devHis = new();
    // The control the request has just pressed with the pointer, and the frame: once the press has been handled, the
    // keyboard focus is taken off it if it has it, so that a key of his does not press it again (review 3: nothing is
    // given back with GrabFocus, and nothing at the end can move a list after the read back).
    (Control Target, long Frame)? _devFocusAfter;
    // The Mining of the character the request runs for: when another character is opened meanwhile, the done line
    // names where the request left that one's filter (the code review of turn 2, fault 3).
    MiningActionComponent? _devAction;

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
    // The filter's own words: its "All" button's, by which the request finds that button, and its box's for hidden rocks.
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_all")]
    static extern ref LocalizedString AllWords(MiningCategoryFilterView? filter);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_showHidden")]
    static extern ref LocalizedString ShowHiddenWords(MiningCategoryFilterView? filter);

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
    // The big panel's break bar, and the mark of its minimum drawn on it: their values against their range show the
    // unit the game keeps the bar and its minimum in.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RockBreakProgressBar")]
    static extern TextureProgressBar PanelBar(ActiveRockView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_MinRockBreakProgress")]
    static extern TextureProgressBar PanelMinimumBar(ActiveRockView panel);

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
    // ActiveRockView.ProgressTooltipText, MiningActionStatsView's PercentIncreaseRockBreakChanceTitle and ...Tooltip,
    // ShatterChanceTitle and ...Tooltip, MinRockBreakBarChanceTitle and ...Tooltip, and ChanceIncreaseIconLabel.PercentFormat
    // are the game's constants (ApiDump lists them as static String fields): an UnsafeAccessor cannot reach a constant
    // (check-2: MissingFieldException), so they are read by reflection, by name (DevConstant).
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

    // A constant of the game's words, by the name ApiDump gives it, read by reflection: a constant is not a field an
    // UnsafeAccessor can reach. Throws, with the type and member named, when it is not there.
    static string DevConstant(Type type, string member)
    {
        FieldInfo field = type.GetField(member, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(type.Name, member);
        object? value = field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null);
        return value as string ?? throw new InvalidCastException($"{type.Name}.{member} holds no words");
    }

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
            if (DevSettingsNow() != _devSettingsLeft)
            {
                _devHis.Add("settings");
                _settings = ModFiles.ReadJson<Settings>(SettingsFile, _ctx.Log);
                _settings.Sort ??= RatesList.NameKey;
                return "settings.json left as the player changed it";
            }
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
    // keeps that filter in the character's Mining (MiningActionComponent.SelectedCagtegory; the component is
    // written to the save, WriteTo), so it may stay there.
    string DevLeftOn()
    {
        try
        {
            if (_devAction == null) return "the page's filter (no character read)";
            string now = _devAction.SelectedCagtegory?.Id ?? "all";
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
        if (_devHits != null) { DevRunWatch(DevWatchTick); return; }
        DevWatchHand();
        // Before any step: the keyboard focus off a control the request has just pressed, and what the request's own last
        // press left on the page, once the page has settled. Both run whatever step comes next, a failed one too.
        try { DevFocusOff(); }
        catch (Exception e)
        {
            _devFocusAfter = null;
            string words = $"the focus NOT AS IT WAS: {e.Message}";
            _devOnTheWay.Add(words);
            DevLog("focus | " + words);
        }
        string step = "";
        try
        {
            if (_devSteps.Count == 0)
            {
                string path = Path.Combine(_ctx.Directory, RequestFile);
                if (!File.Exists(path)) { DevRunWatch(DevWatchRequest); return; }
                string asked = File.ReadAllText(path);
                File.Delete(path);
                DevPlan(asked);
                DevLog($"Mining Rates check started, {_devPutBackAt} steps and the put-back; a step waits {StepTime / 1000} s at most, " +
                    $"{PointerStepTime / 1000} s when it points or presses (the request's own times, nothing of the player's).");
            }
            step = _devSteps[_devAt].Name;
            if (_devAt < _devPutBackAt && step == _devFailOnPurpose)
                throw new InvalidOperationException("failed on purpose, as the request asked, to show the put-back");
            bool checking = _devAt < _devPutBackAt;
            if (checking && _devListSettle is { } frame)
            {
                if ((long)Engine.GetProcessFrames() < frame + 2) return;
                _devListScrollLeft = DevWindowScroll();
                _devListSettle = null;
            }
            if (checking && (DevSettingsNow() != _devSettingsLeft || _wanted != _devWantedLeft ||
                DevWindowScroll() != _devListScrollLeft))
                throw new InvalidOperationException("The window or its settings changed since the request's last step. Leaving the player's change in place.");
            bool finished;
            var oldList = _list;
            try { finished = _devSteps[_devAt].Run(); }
            finally
            {
                if (checking)
                {
                    _devSettingsLeft = DevSettingsNow();
                    _devWantedLeft = _wanted;
                    _devListScrollLeft = DevWindowScroll();
                    if (_list != oldList) _devListSettle = (long)Engine.GetProcessFrames();
                }
            }
            if (!finished)
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
        DevLog("Mining Rates check " + result);
        _devSteps.Clear();
        _devAt = 0;
        _devWithout?.Dispose();
        _devWithout = null;
        _devSettingsRead = false;
        _devListSettle = null;
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
        MiningRecipeCategory? Filter, int? Scroll, string? Look);

    // The Mining page's filter, scroll and filter as drawn at one moment (_devOrigin).
    sealed record DevPageWas(MiningRecipeCategory? Filter, int? Scroll, string? Look);

    DevPageWas DevPageNow() => new(_mining!.Action.SelectedCagtegory, DevScrollNow(), DevFilterLook());

    // Only press on a page with none of the request's previous changes standing. Capture the
    // origin immediately before the actual click, after any wait for the player's hand.
    bool DevPagePress(Func<bool> press)
    {
        if (_devSettle != null || _devOwnFilter != null || _devOwnScroll != null)
            throw new InvalidOperationException("The previous page change has not been put back.");
        DevPageWas origin = DevPageNow();
        if (!press()) return false;
        _devOrigin = origin;
        _devHis.Remove("filter");
        _devHis.Remove("scroll");
        _devSettle = ((long)Engine.GetProcessFrames(), true, _mining!.Action.SelectedCagtegory);
        return true;
    }

    // Each part the request owns, read again: one that is not exactly what the request left is the player's. The request
    // lets go of it (nothing of it is put back, and the read back says so). A scroll of his does not touch the filter; a
    // filter of his does take the scroll with it, because the list scrolled is then the one of his category (review 4).
    // Returns the parts he changed, in words.
    List<string> DevHisNow()
    {
        var moved = new List<string>();
        var action = _mining!.Action;
        if (_devOwnFilter is { } filter && !ReferenceEquals(action.SelectedCagtegory, filter.Value))
        {
            moved.Add($"the filter (now {action.SelectedCagtegory?.Id ?? "all"}, the request left {filter.Value?.Id ?? "all"})");
            _devOwnFilter = null;
            _devHis.Add("filter");
            if (_devOwnScroll != null)
            {
                moved.Add("the scroll with it (the list is his category's now)");
                _devOwnScroll = null;
                _devHis.Add("scroll");
            }
        }
        if (_devOwnScroll != null && !ReferenceEquals(action.SelectedCagtegory, _devScrollUnder))
        {
            moved.Add($"the scroll (the page shows {action.SelectedCagtegory?.Id ?? "all"} now, the request's scroll was of {_devScrollUnder?.Id ?? "all"})");
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
    void DevSettleNow()
    {
        try { DevSettle(); }
        catch (Exception e)
        {
            _devSettle = null;
            string words = $"the page NOT AS IT WAS: what the request left could not be read ({e.Message})";
            _devOnTheWay.Add(words);
            DevLog("page | " + words);
            if (_devAt < _devPutBackAt) { _devFailure = words; _devAt = _devPutBackAt; }
        }
    }

    void DevSettle()
    {
        if (_devSettle is not { } settle) return;
        // Firemaking Rates' DevSettle also lets a pending scroll expire if drawing stops.
        const int settleFrames = 2, scrollWaitFrames = 60;
        long frame = (long)Engine.GetProcessFrames();
        if (frame < settle.Frame + settleFrames || (_scrollTo != null && frame < settle.Frame + scrollWaitFrames)) return;
        if (_scrollTo != null)
        {
            DevLog("page | the pending scroll did not finish while the page was drawn and was cancelled");
            _scrollTo = null;
        }
        _devSettle = null;
        _devSettledFrame = frame;
        if (_mining == null || _page == null || !GodotObject.IsInstanceValid(_page)) return;
        var action = _mining.Action;
        if (!ReferenceEquals(action.SelectedCagtegory, settle.Filter))
        {
            _devOwnFilter = null;
            _devOwnScroll = null;
            _devHis.Add("filter");
            _devHis.Add("scroll");
            DevLog("page | the category changed after the request's press. Its filter and scroll stay with the player.");
            return;
        }
        if (settle.Both)
        {
            _devOwnFilter = ReferenceEquals(action.SelectedCagtegory, _devOrigin?.Filter) ? null : new(action.SelectedCagtegory);
            int? scroll = DevScrollNow();
            _devOwnScroll = _devOwnFilter == null && scroll == _devOrigin?.Scroll ? null : new(scroll);
            _devScrollUnder = action.SelectedCagtegory;
        }
        else if (_devOwnScroll != null)
        {
            _devOwnScroll = new(DevScrollNow());
            _devScrollUnder = action.SelectedCagtegory;
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
        _devSettledFrame = -1;
        _devOrigin = null;
        _devOnTheWay.Clear();
        _devHis.Clear();
        _devFocusAfter = null;
        _devWithout?.Dispose();
        _devWithout = null;
        _devStarted = Time.GetTicksMsec();
        _devAction = _mining!.Action;
        GameView view = GameAccess.FindGameView() ?? throw new InvalidOperationException("No character is open.");
        var action = _mining!.Action;
        // settings.json as it is on disk, the copy everything of the window is put back from (lesson 50).
        _devSettingsCopy = _devSettingsLeft = DevSettingsNow();
        _devWantedLeft = _wanted;
        int? originalWindowScroll = _devListScrollLeft = DevWindowScroll();
        int? putWindowScroll = originalWindowScroll;
        _devListSettle = null;
        _devSettingsRead = true;
        var before = new DevBefore(CurrentPage(view)?.Item1, _wanted, _window is { IsAlive: true, Visible: true },
            _window is { IsAlive: true } ? _window.Minimised : null, GodotObject.IsInstanceValid(_opener) ? _opener!.ButtonPressed : null,
            _devSettingsCopy, action.SelectedCagtegory, DevScrollNow(), DevFilterLook());
        string miningAtStart = DevMiningState();
        bool hiddenAtStart = action.ShowHiddenRocks;
        void Add(string name, Func<bool> run, ulong timeout = StepTime) => _devSteps.Add((name, run, timeout));
        DevLog($"before: page {before.Page?.Id ?? "none"}, window {(before.Wanted ? "open" : "closed")}{(before.Shown ? " and on the screen" : "")}" +
            $"{(before.Folded == true ? ", folded" : "")}, round button {(before.Lit is { } lit ? lit ? "lit" : "unlit" : "not made yet")}, settings.json " +
            (before.Settings == null ? "not there" : $"{before.Settings.Length} characters: {before.Settings}") +
            $" | {miningAtStart}; the page's category filter {before.Filter?.Id ?? "all"}, drawn {before.Look ?? "not read yet"}, " +
            $"the list of rocks scrolled to {before.Scroll?.ToString() ?? "not found"}, the box for hidden rocks {(hiddenAtStart ? "ticked" : "not ticked")} " +
            $"({action.HiddenRockCount} rocks hidden by the player) | the real pointer at {DisplayServer.MouseGetPosition()}");

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
            // The list of rocks and the filter of a page the request had to open are read now that they are there.
            if (before.Scroll == null) before = before with { Scroll = DevScrollNow() };
            if (before.Look == null) before = before with { Look = DevFilterLook() };
            DevLog($"page | the page is {CurrentPage(view)?.Item1?.Id ?? "none"}, the Mining page is shown: {PageShown}; its list of rocks scrolled to " +
                $"{before.Scroll?.ToString() ?? "not found"}; its filter drawn {before.Look ?? "NOT FOUND"}");
            DevStrip();
            if (IsOpen) Close();
            return true;
        });
        // The game's own numbers in the card's break chance tooltip, before the window is open over the page.
        DevBreakRead? breakRead = null;
        Add("the card's break chance", () =>
        {
            if (breakRead == null)
            {
                if (!DevFramesDrawn()) return false;
                MiningRockSelectView? card = DevBreakCard(out string why);
                if (card == null) { DevLog($"break rule | no card's break chance can be pointed at ({why}): the rule is not read from the page this time"); return true; }
                Control aim = DevBreakAim(card, out string aimed);
                breakRead = new DevBreakRead(card, aim, aimed, DevTooltipText(CardBreakTooltip(card)), _ctx.Tree.Root.GetTexture().GetImage());
                _devTipSince = 0;
                _devFrameMark = 0;   // the two drawn frames before its picture are counted afresh
            }
            return DevBreakTooltip(breakRead);
        }, PointerStepTime);
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
            target = (lines.FirstOrDefault(l => !l.Rock.Locked && l.Rock.Sight is CardSight.UnderWindow or CardSight.NoRoom).Button ??
                lines.FirstOrDefault(l => !l.Rock.Locked).Button ?? lines.FirstOrDefault().Button)
                ?? throw new InvalidOperationException("No line to point at.");
            DevLog($"tooltip | the line pointed at: {lines.First(l => l.Button == target).Rock.Rock.Recipe.Id}, " +
                DevCardSight(Native.Card(_page!, lines.First(l => l.Button == target).Rock.Rock), false));
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
            DevCut("sorted");
            Shot("sorted", crop: true);
            return true;
        });

        // A click on a line whose rock's card is under another category, or out of sight, then the page's own category
        // button for the same category, to compare. The page's filter is put back after each, and its scroll two drawn
        // frames later.
        RockRates? clicked = null;
        DevClickEvents? events = null;
        Image? beforeClick = null;
        string miningBefore = "", ours = "", ourEvents = "", pageBack = "";
        bool filterChanged = false, compareSkipped = false;
        MiningRecipeCategory? filterBeforeClick = null;

        void AddPageBack(string name, Func<bool> skip, string head)
        {
            Add(name, () =>
            {
                if (skip()) return true;
                if (_devSettle != null || _devSettledFrame >= (long)Engine.GetProcessFrames()) return false;
                string scroll;
                try { scroll = DevScrollBack(); }
                catch (Exception e) { scroll = $"the scroll NOT AS IT WAS: {e.Message}"; }
                string words = DevJoin(pageBack, scroll);
                if (words.Contains("NOT AS IT WAS") || words.Contains("SHOW HIDDEN CHANGED")) _devOnTheWay.Add(words);
                DevLog($"{head} {words}");
                pageBack = "";
                return true;
            });
        }

        // Use the filter the player already has. A setup press would leave its category on screen while
        // the line's pointer press waits for the player's hand.
        Add("a line to click", () =>
        {
            var lines = _list!.DevLines().Select(l => l.Rock).Where(r => r.Clickable).ToList();
            // First a line whose card the page shows and a scroll brings clear of the window, what this click must do
            // (review 4); then one under another category, as check-3 clicked.
            clicked = lines.FirstOrDefault(r => r.Sight == CardSight.Scroll) ?? lines.FirstOrDefault(r => Native.Card(_page!, r.Rock) is { Visible: false }) ??
                lines.FirstOrDefault();
            if (clicked == null && _devFailOnPurpose == "the rock shown") throw new InvalidOperationException("No clickable line for fail=click.");
            if (clicked == null) { DevLog("click | no line shows something a click would change: none is clicked."); return true; }
            _list.DevScroll.EnsureControlVisible(DevLineOf(clicked));
            miningBefore = DevMiningState();
            filterBeforeClick = _mining!.Action.SelectedCagtegory;
            DevLog($"click | the line of {clicked.Rock.Recipe.Id} ({clicked.Rock.Category?.Id ?? "no category"}{(clicked.OnPage ? ", the rock being mined" : "")}); " +
                $"before: {DevPageState(clicked.Rock)}; {miningBefore}");
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
            _devHiddenBefore = _mining!.Action.ShowHiddenRocks;
            RockRates line = clicked;
            return DevPagePress(() => DevClickLine(line));
        }, PointerStepTime);
        Add("the rock shown", () =>
        {
            if (clicked == null) return true;
            // The list of rocks scrolls two drawn frames after the click (ScrollToCard); the picture two frames later.
            if (_scrollTo != null || _devSettle != null || _devSettledFrame >= (long)Engine.GetProcessFrames() || !DevFramesDrawn()) return false;
            var action = _mining!.Action;
            MiningRock rock = clicked.Rock;
            Refresh();
            MiningRockSelectView? card = Native.Card(_page!, rock);
            CardSight? sight = card != null && card.IsVisibleInTree() ? Native.Place(card, Cover(), out _) : null;
            // A card the page has no room for beside the window can only come after a click that changed the filter;
            // then the line's tooltip must say so (ScrollToCard).
            string tip = DevLineOf(clicked).TooltipText;
            string shown = sight switch
            {
                CardSight.Clear => "",
                CardSight.UnderWindow or CardSight.NoRoom when !ReferenceEquals(filterBeforeClick, action.SelectedCagtegory) && tip.Contains("has no room") =>
                    "  NOT CLEAR OF THE WINDOW: the page has no room for it there, as the line's tooltip now says",
                CardSight.UnderWindow or CardSight.NoRoom => "  NOT SHOWN: no room for it clear of the window" + (tip.Contains("has no room") ? "" : ", AND THE LINE'S TOOLTIP DOES NOT SAY SO"),
                CardSight.Scroll => "  NOT SHOWN: a scroll would still show it clear of the window",
                _ => "  NOT SHOWN",
            };
            var lit = _list!.DevLines().Where(l => l.Rock.OnPage).Select(l => l.Rock.Rock.Recipe.Id).ToList();
            string mining = DevMiningState();
            ours = DevFilterState(rock);
            ourEvents = string.Join(", ", events!.Seen);
            filterChanged = !ReferenceEquals(action.SelectedCagtegory, filterBeforeClick);
            var litMeant = new List<string>();
            if (action.CurrentRock is { } mined && _list.DevLines().Any(l => ReferenceEquals(l.Rock.Rock, mined))) litMeant.Add(mined.Recipe.Id);
            DevLog($"click | after the click on the line of {rock.Recipe.Id}: {DevPageState(rock)}; the game's events [{ourEvents}]; {mining}" +
                shown + (mining == miningBefore ? "" : "  MINING CHANGED") +
                (lit.SequenceEqual(litMeant) ? "" : $"  THE LIT LINE IS [{string.Join(", ", lit)}]") +
                (IsOpen ? "" : "  THE WINDOW CLOSED") + DevHiddenMoved("the click on the line"));
            if (sight == CardSight.Clear && beforeClick != null && card != null)
            {
                string proof = DevProofBeside(beforeClick, card, "the card");
                DevLog("click | " + proof);
                if (proof.Contains("; VISIBLE:")) { DevCut("clicked"); Shot("clicked"); }
                else throw new InvalidOperationException("The rock card could not be proved in the picture.");
            }
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
            MiningRecipeCategory category = clicked.Rock.Category!;
            Button button = (Native.CategoryButtons(FilterView(_page!)) is { } buttons && buttons.TryGetValue(category, out Button? b) ? b : null)
                ?? throw new InvalidOperationException($"the page has no category button for {category.Id}");
            if (_devAim != button && !DevSight(button).HasArea())
            {
                compareSkipped = true;
                DevLog($"click | the page's own category button of {category.Id} lies under the mod's own window: it is not pressed, and the click is not compared with it");
                return true;
            }
            events ??= new DevClickEvents(_mining!.Action, DevLog);
            return DevPagePress(() => DevGamePress(button, $"the page's own category button of {category.Id} ('{button.Text}')"));
        }, PointerStepTime);
        Add("the page's button compared", () =>
        {
            if (clicked == null || !filterChanged || compareSkipped) return true;
            if (_devSettle != null || _devSettledFrame >= (long)Engine.GetProcessFrames() || !DevFramesDrawn()) return false;
            var action = _mining!.Action;
            string game = DevFilterState(clicked.Rock), gameEvents = string.Join(", ", events!.Seen), mining = DevMiningState();
            DevLog($"click | after the page's own category button: {game}; the game's events [{gameEvents}]; {mining}" +
                (mining == miningBefore ? "" : "  MINING CHANGED") + DevHiddenMoved("the page's own category button") + " | " +
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
            using var now = _ctx.Tree.Root.GetTexture().GetImage();
            string proof = open == null ? "no picture before closing"
                : DevChanged(open, now, openAt, "the window's place", "gone", "STILL IN THE PICTURE");
            if (!proof.StartsWith("gone:")) throw new InvalidOperationException(proof);
            DevLog("closed | " + proof);
            Shot("closed");
            return true;
        });

        _devPutBackAt = _devSteps.Count;
        // What the read-backs say, in order: the Mining page's filter and scroll are read before the page that was on the
        // screen comes back (the code review of turn 2, fault 2), the rest at the end.
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
                if (part is "the pointer" or "the keyboard focus") said.Add($"{part}: {words}");
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
                bool wanted = _wanted == _devWantedLeft ? before.Wanted : _wanted;
                if (_wanted != _devWantedLeft) _devHis.Add("window");
                if (DevWindowScroll() != _devListScrollLeft)
                {
                    putWindowScroll = DevWindowScroll();
                    _devHis.Add("window scroll");
                }
                _window?.Free();
                _window = null;
                _list = null;
                _fitPending = false;
                _wanted = wanted;
                Sync();
                SyncStripButton();
                // A closed window may still have had a remembered scroll before the request.
                if (_list == null && putWindowScroll != null && PageShown) { Build(); Sync(); }
                if (_list != null && putWindowScroll is { } scroll) _list.ScrollAt = scroll;
                return $"{(wanted ? "open" : "closed")}, with the restored settings and list scroll";
            });
            return true;
        });
        // The filter in a step of its own, so that settings.json and the window above never wait: a press of the request's
        // that has not settled yet is waited for (what it left is read by DevSettle, a request that failed right after a
        // press too), and the filter is read again a tick later. Then through the page's own button and the scroll on its own. The page that was on the screen comes back
        // only after both.
        Add("put back the page's filter", () =>
        {
            if (_devSettle != null || _devSettledFrame >= (long)Engine.GetProcessFrames()) return false;
            pageBack = "";
            Part("the page's filter", () =>
            {
                pageBack = DevFilterBack();
                return pageBack;
            }, carry: false);
            return true;
        });
        AddPageBack("put back the page's filter and scroll", () => false, "put back | the page's filter and scroll:");
        Add("read back the Mining page", () =>
        {
            var action = _mining!.Action;
            // What the put-backs went back to: the page just before the request's first change, else as it was at the start.
            DevPageWas want = _devOrigin ?? new DevPageWas(before.Filter, before.Scroll, before.Look);
            string? look = DevFilterLook();
            int? scroll = DevScrollNow();
            if (!PageShown)
            {
                // The player left the Mining page while the request ran (the request opens no other page before this
                // step): nothing could be checked, which the done line says (Mining Rates' code review of turn 3, fault 7).
                said.Add($"the Mining page's filter and scroll NOT READ BACK: the page on the screen is {CurrentPage(view)?.Item1?.Id ?? "none"}, the player's");
                readAll = false;
                return true;
            }
            string when = _devOrigin != null && (!ReferenceEquals(_devOrigin.Filter, before.Filter) || _devOrigin.Scroll != before.Scroll || _devOrigin.Look != before.Look)
                ? " (as the player left it before the request's first change)" : "";
            SayPage(ReferenceEquals(action.SelectedCagtegory, want.Filter), "filter", $"the page's category filter {want.Filter?.Id ?? "all"}{when}",
                action.SelectedCagtegory?.Id ?? "all");
            // The filter as the page draws it too, not only the member (Mining Rates' code review of turn 1, fault 2).
            SayPage(look != null && look == want.Look, "filter", $"the page's filter drawn {want.Look ?? "(not read at the start)"}", look ?? "not found");
            SayPage(scroll == want.Scroll, "scroll", $"the page's list of rocks scrolled to {want.Scroll?.ToString() ?? "-"}", scroll?.ToString() ?? "-");
            return true;
        });
        Add("put back the page", () =>
        {
            Part("the page", () =>
            {
                if (before.Page == null || !_devOpenedPage) return "as it was: the request did not open Mining";
                GamePage? now = CurrentPage(view)?.Item1;
                if (ReferenceEquals(now, before.Page)) return $"{before.Page.Id}, as it was";
                // Only the page the request opened itself: a page the player went to meanwhile is his.
                if (!PageShown)
                {
                    _devHis.Add("page");
                    return $"left on {now?.Id ?? "none"}: the player went there from the Mining page the request opened, and it is his";
                }
                view.SetCurrentPage(before.Page);
                return $"back to {before.Page.Id}, the page on the screen before the request opened Mining";
            });
            Part("the window on that page", () =>
            {
                Sync();
                SyncStripButton();
                return IsOpen ? "on the screen" : "not on the screen";
            });
            Part("the keyboard focus", () =>
            {
                DevFocusOff();
                return "released from the request's pressed control if it still held focus";
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
            GamePage? page = CurrentPage(view)?.Item1;
            if (!_devHis.Contains("page") && before.Page != null && !ReferenceEquals(page, before.Page) && Time.GetTicksMsec() - _devStarted < 4000) return false;
            SayPage(before.Page == null || ReferenceEquals(page, before.Page), "page", $"the page {before.Page?.Id ?? "none"}", page?.Id ?? "none");
            bool shown = _window is { IsAlive: true, Visible: true };
            SayPage(_wanted == before.Wanted && shown == before.Shown, "window",
                $"the window {(before.Wanted ? "open" : "closed")}{(before.Shown ? " on the screen" : "")}", $"{(_wanted ? "open" : "closed")}{(shown ? " on the screen" : "")}");
            if (before.Shown && shown) SayPage(_window!.Minimised == before.Folded, "settings", $"folded {before.Folded}", $"folded {_window.Minimised}");
            bool lit = GodotObject.IsInstanceValid(_opener) && _opener!.ButtonPressed;
            SayPage(lit == (before.Lit ?? false), "window", $"the round button {(before.Lit == true ? "lit" : "unlit")}", lit ? "lit" : "unlit");
            string? settings = File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : null;
            SayPage(settings == before.Settings, "settings", before.Settings == null ? "settings.json not there" : "settings.json byte for byte",
                settings == null ? "not there" : $"{settings.Length} characters");
            var action = _mining!.Action;
            Say(action.ShowHiddenRocks == hiddenAtStart, $"the box for hidden rocks {(hiddenAtStart ? "ticked" : "not ticked")}", action.ShowHiddenRocks ? "ticked" : "not ticked");
            string miningNow = DevMiningState();
            Say(miningNow == miningAtStart, miningAtStart, miningNow);
            if (putWindowScroll != null && _list != null)
                SayPage(DevWindowScroll() == originalWindowScroll, "window scroll", "the rate window list scroll", DevWindowScroll()?.ToString() ?? "-");
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
            if (name == "click") name = "the rock shown";
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

    string DevPageState(MiningRock rock) =>
        DevFilterState(rock) + $", {DevCardSight(Native.Card(_page!, rock), true)}, the list scrolled to {DevScrollNow()?.ToString() ?? "-"}";

    // How a card stands against the mod's window, as a click on its line reads it (Native.Place); with places: the
    // card's, its list's and the window's, all as drawn (Native.Drawn), the window's read from its frame (Cover).
    string DevCardSight(MiningRockSelectView? card, bool places)
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

    // Recorded by Build, before a later drag or fold can change the settings.
    string _devOpened = "";

    // Every line's card against the window, and what its line says of it: a line is clickable for a card the page shows
    // only when a scroll shows it clear (MISMATCH otherwise), and the tooltip of a line whose card has no room says so
    // (NOT SAID otherwise). The window's place and the page's list are said once.
    void DevRoom(string when)
    {
        var said = new List<string>();
        MiningRockSelectView? any = null;
        foreach (var (r, button, _, _) in _list!.DevLines())
        {
            MiningRockSelectView? card = Native.Card(_page!, r.Rock);
            string sight = card is { Visible: true } ? DevCardSight(card, false) : card == null ? "no card" : "its card under another category";
            if (card is { Visible: true }) any ??= card;
            bool room = r.Sight is CardSight.UnderWindow or CardSight.NoRoom, tells = button.TooltipText.Contains("has no room");
            string marks = (card is { Visible: true } && card.IsVisibleInTree() && r.Clickable != (r.Sight == CardSight.Scroll) ? "  MISMATCH: its line clickable " + r.Clickable : "") +
                (room && !tells ? "  NOT SAID in its tooltip" : "") + (!room && tells ? "  SAID in its tooltip WITH ROOM" : "");
            said.Add($"{r.Rock.Recipe.LocalId}: {sight}, its line clickable {r.Clickable}{marks}");
        }
        string places = any != null ? DevCardSight(any, true) : "no card shown";
        DevLog($"room | {when} | the window at {Cover()?.ToString() ?? "none, not on the screen"}, from its frame {(_window is { IsAlive: true } w ? w.Root.GetGlobalRect().ToString() : "-")} " +
            $"at {(_window is { IsAlive: true } s ? s.Style.Scale.ToString("0.##") : "-")}; {_devOpened}; the first card shown: {places}");
        DevLog($"room | {when} | {string.Join("; ", said)}");
    }

    // The page's filter as the page draws it: the buttons of its filter shown pressed (its box for hidden rocks apart)
    // and the number of cards it shows. Null when the page's parts are not there.
    string? DevFilterLook()
    {
        try
        {
            if (_page == null || !GodotObject.IsInstanceValid(_page)) return null;
            MiningCategoryFilterView filter = FilterView(_page);
            CheckBox box = ShowHiddenBox(filter);
            var pressed = filter.FindChildren("*", "Button", true, false).OfType<Button>()
                .Where(b => b != box && b.ButtonPressed).Select(b => $"'{b.Text}'");
            int cards = Native.RockViews(_page)?.Values.Count(c => GodotObject.IsInstanceValid(c) && c.Visible) ?? -1;
            return $"buttons pressed [{string.Join(", ", pressed)}], {cards} cards shown";
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException or NullReferenceException) { return null; }
    }

    // The page's "All" button, found by the game's own word for it (MiningCategoryFilterView.s_all): the one button of
    // the page's filter whose words are that word, never its box for hidden rocks (ShowHiddenCheckBox) nor any other
    // check box. Null, and why, when there is not exactly one: the request then never guesses a button (the code review
    // of turn 1, fault 1).
    Button? DevAllButton(out string why)
    {
        why = "";
        MiningCategoryFilterView filter = FilterView(_page!);
        string word;
        try { word = DevWordsOf(AllWords(null).Text ?? ""); }
        catch (MissingMemberException) { why = "the game's word for its All button (s_all) could not be read"; return null; }
        if (word.Length == 0) { why = "the game's word for its All button (s_all) is empty"; return null; }
        CheckBox box = ShowHiddenBox(filter);
        var found = filter.FindChildren("*", "Button", true, false).OfType<Button>()
            .Where(b => b is not CheckBox && b != box && string.Equals(DevWordsOf(b.Text), word, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count == 1) return found[0];
        why = found.Count == 0 ? $"no button of the page's filter reads the game's word '{word}'" : $"{found.Count} buttons of the page's filter read the game's word '{word}'";
        return null;
    }

    // A game button pressed the way a click on a line presses a category's (CategoryFilter): a toggle button first goes
    // down, which lets the others of its group go, then its pressed signal.
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

    // A game button pressed with the pointer (DevPress), with the page's box for hidden rocks read just before it: the
    // step after reads it again (DevHiddenMoved).
    bool DevGamePress(Control target, string what)
    {
        _devHiddenBefore = _mining!.Action.ShowHiddenRocks;
        return DevPress(target, what);
    }

    // The page's box for hidden rocks is the player's: after a press of a game button, against just before it.
    string DevHiddenMoved(string what) => _mining!.Action.ShowHiddenRocks == _devHiddenBefore ? ""
        : $"  SHOW HIDDEN CHANGED by {what} (it was {(_devHiddenBefore ? "ticked" : "not ticked")})";

    // Restore the filter through its own button. A failed signal leaves the filter owned for a later
    // retry. Do not aim a pointer while the request's filter stands.
    string DevFilterBack()
    {
        if (_devOrigin is not { } origin || (_devOwnFilter == null && _devOwnScroll == null)) return "nothing of the request's is left on the page";
        if (_page == null || !GodotObject.IsInstanceValid(_page)) return "the Mining page is gone: its filter and scroll NOT AS IT WAS (not put back)";
        var action = _mining!.Action;
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
                    : Native.CategoryButtons(FilterView(_page)) is { } buttons && buttons.TryGetValue(origin.Filter, out Button? b) && GodotObject.IsInstanceValid(b) ? b : null;
                if (button == null)
                    said.Add($"the filter NOT AS IT WAS: it stays on {own.Value?.Id ?? "all"}, as the request left it, since " +
                        (origin.Filter == null ? why : $"the page has no button of {origin.Filter.Id}") + "; it is never set behind the page's buttons, " +
                        "and stays the request's own for the final put-back");
                else
                {
                    bool hidden = action.ShowHiddenRocks;
                    bool took = DevSignalPress(button, () => ReferenceEquals(action.SelectedCagtegory, origin.Filter));
                    string moved = action.ShowHiddenRocks == hidden ? "" : "  SHOW HIDDEN CHANGED by the put-back's press";
                    if (took)
                    {
                        _devOwnFilter = null;
                        // The press lays the list out anew: the scroll the request still owns is read again once it settled.
                        _devSettle = ((long)Engine.GetProcessFrames(), false, action.SelectedCagtegory);
                        said.Add($"the filter put back with the page's own '{button.Text}' button{moved}");
                    }
                    else
                    {
                        said.Add($"the signal of the page's own '{button.Text}' button left the filter on {action.SelectedCagtegory?.Id ?? "all"}, and its buttons " +
                            $"are drawn again as they were; the filter is NOT AS IT WAS{moved}");
                    }
                }
            }
        }
        return DevJoin(said.ToArray());
    }

    // The scroll the request owns, read again on a later tick than what it left was read (DevSettle; the step waits for
    // it): still exactly that, it is put back to where it was before the request's first change; otherwise the player
    // moved it, and it is his, and left. While the filter is still the request's and not put back (its button not
    // found or its signal did not take), the scroll is left with it, for the final put-back. When the write
    // does not take, what the request's own write left stays its own.
    string DevScrollBack()
    {
        if (_devOwnScroll is not { } own || _devOrigin is not { } origin) return "";
        if (_page == null || !GodotObject.IsInstanceValid(_page)) return "the Mining page is gone: its scroll NOT AS IT WAS (not put back)";
        if (_devOwnFilter is { } filter && !ReferenceEquals(filter.Value, origin.Filter))
            return "the scroll left with the request's filter, which is not put back: both stay the request's own for the final put-back";
        if (!ReferenceEquals(_mining!.Action.SelectedCagtegory, _devScrollUnder))
        {
            _devOwnScroll = null;
            _devHis.Add("scroll");
            return $"the scroll is left: the page shows {_mining.Action.SelectedCagtegory?.Id ?? "all"} now, and the request's scroll was of " +
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
        RockScroll(_page).ScrollVertical = back;
        int? after = DevScrollNow();
        if (after == back)
        {
            _devOwnScroll = null;
            return $"the scroll put back to {back}";
        }
        _devOwnScroll = new(after);
        _devScrollUnder = _mining!.Action.SelectedCagtegory;
        return $"the scroll NOT AS IT WAS (put back to {back}, it reads {after?.ToString() ?? "-"}); what the request's write left stays its own";
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

    static string R(double? value) => value is { } n ? R(n) : "-";

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
                $"counted: {R(r.HitSeconds)} s a hit, {R(r.HitsPerHour)} hits/h, break chance {R(r.BreakChance)}, hits a break {(r.HitsPerBreak is { } h ? R(h) : "-")} " +
                $"(the rule {(RockRates.Settled is { } settled ? $"{settled.Rule}, {settled.Source}" : "not settled")}; by each reading [{DevReadings(r)}]), " +
                $"XP {R(r.XpEach)} a break = {R(r.XpPerHour)}/h, ore {r.OreQuantity} a break x(1+{R(r.Doubling)}) = {R(r.OrePerBreak)}, ore/h {(r.OrePerHour is { } o ? R(o) : "-")}, " +
                $"time per ore {(r.SecondsPerOre is { } s ? R(s) : "-")}, ore price {(r.OrePrice is { } p ? R(p) : "-")}, gold/h {(r.GoldPerHour is { } g ? R(g) : "-")} " +
                $"({R(r.PerHitGoldPerHour)} of it from the items of a hit) | items of a hit [{string.Join(", ", r.PerHit.Select(d => $"{d.Item.Id} {R(d.Chance)} x{d.Quantity} each {R(d.Each)} = {R(d.PerHour)}/h, gold {(d.GoldPerHour is { } dg ? R(dg) : "-")}/h"))}] | " +
                $"not counted [{string.Join(", ", r.NotCountedItems.Select(n => $"{n.Item.Id} {R(n.Chance)}").Concat(r.NotCounted.Select(n => $"{n.Key} {R(n.Nodes.Total.Value)}")))}] | " +
                $"picture {DevTexture(picture)}{(picture != null && _pictures!.IsStandIn(picture) ? "  STAND-IN DRAWN" : "")}");
            DevLog($"row | {when} | drawn {drawn}");
            DevLog($"row | {when} | tooltip '{button.TooltipText}'");
        }
        var rocks = ReadRocks();
        DevLog($"rows | {when} | {count} lines, {DevRowsInSight()} in sight; the game has {_mining!.Action.Rocks.Count} rocks, {rocks.Count} listed, " +
            $"{rocks.Count(r => r.Locked)} locked; columns [{string.Join(", ", _list!.DevColumns)}]; the hits a break take by " +
            (RockRates.Settled is { } rule ? $"{rule.Rule}, {rule.Source}" : "NO RULE") + (count != rocks.Count ? "  MISMATCH" : ""));
        // The unit of the bar's minimum is seen only where it is not 0 (the code review of turn 2): every rock that has one.
        var minimums = rocks.Where(r => Math.Abs(r.MinimumProspecting) > 1e-12).ToList();
        DevLog("minimum | " + (minimums.Count == 0 ? "every rock's minimum break bar is 0: its unit is not seen in this check"
            : string.Join("; ", minimums.Select(r => $"{r.Rock.Recipe.LocalId} {R(r.Rock.Stats.MinimumProspecting.Value)} (break chance {R(r.BreakChance)}, the bar now " +
                $"{R(r.Rock.Progress)}){(r.MinimumCounted ? ", counted as a part of the bar" : "  NOT COUNTED: not a part of the bar")}"))));
        return DevRowsInSight();
    }

    // The hits a break takes by each reading of the bar, where the rock's chance and minimum are usable.
    static string DevReadings(RockRates r) => r.BreakChance <= 0 ? "unusable break chance" : string.Join(", ", Enum.GetValues<BreakRule>().Select(rule =>
        $"{rule} {(RockRates.ExpectedHits(rule, r.BreakChance, r.MinimumProspecting) is { } hits ? R(hits) : "-")}"));

    // The longest line of every tip of the window in characters, against Fishing Rates' longest: Godot's tooltip does
    // not wrap, so a longer line makes a wider tooltip than Fishing Rates' (the code review of turn 1, fault 3).
    void DevTips(string when)
    {
        static string Longest(string text) => text.Split('\n').OrderByDescending(l => l.Length).First();
        static string Over(string line, int most) => line.Length > most ? $"  LONGER THAN FISHING RATES' {most}" : "";
        var heads = _list!.DevHeaders.Select(h => (h.Text, Line: Longest(h.TooltipText))).ToList();
        var lines = _list.DevLines().Select(l => (l.Rock.Rock.Recipe.LocalId, Line: Longest(l.Button.TooltipText))).ToList();
        string longest = heads.Select(h => h.Line).Concat(lines.Select(l => l.Line)).OrderByDescending(l => l.Length).First();
        DevLog($"tips | {when} | the names' tips, the longest line of each: {string.Join(", ", heads.Select(h => $"{h.Text} {h.Line.Length}{Over(h.Line, FishingTipMost)}"))}" +
            $" | the lines' tooltips: {string.Join(", ", lines.Select(l => $"{l.LocalId} {l.Line.Length}{Over(l.Line, FishingTooltipMost)}"))}" +
            $" | the longest of all, {longest.Length}: '{longest}'");
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
                $"shown={card.Visible}, {DevCardSight(card, false)}, locked part shown={CardLocked(card).IsVisibleInTree()}, being mined={CardActive(card).IsVisibleInTree()}, " +
                $"hide button pressed={CardHideButton(card).ButtonPressed}, its button reads '{CardMineButton(card).Text}' | " +
                $"time: game {R(interval.Seconds)} s '{IntervalLabel(interval).Text}', ours {R(ours.HitSeconds)} s{(Math.Abs(interval.Seconds - ours.HitSeconds) < 0.0005 ? "" : "  DIFFER")} | " +
                $"XP: game '{((Node)XpLabel(xp) as Label)?.Text}' ({XpLabel(xp).Value}), ours {R(ours.XpEach)} per break when counted: {DevXpSame(XpLabel(xp).Value, ours.XpEach)} | " +
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
            $"minimum {R(big.MinimumProspecting)} | {DevBars(panel)} | the bar's tooltip '{DevTooltipText(PanelProgressTooltip(panel))}' | per hit [{DevGrants(PanelPerHit(panel), mined)}] | " +
            $"per break [{DevGrants(PanelPerBreak(panel), mined)}]");
    }

    // The big panel's break bar and the mark of its minimum as the page draws them, value against range: with the rock's
    // progress and minimum beside them, the unit the game keeps the minimum in (the code review of turn 2).
    static string DevBars(ActiveRockView panel)
    {
        static string Bar(Func<TextureProgressBar> bar)
        {
            try { return bar() is { } b ? $"{R(b.Value)} of {R(b.MinValue)} to {R(b.MaxValue)}{(b.IsVisibleInTree() ? "" : " (hidden)")}" : "none"; }
            catch (Exception e) when (e is MissingMemberException or TypeLoadException) { return $"not read ({e.GetType().Name})"; }
        }
        return $"the bar drawn {Bar(() => PanelBar(panel))}, its minimum drawn {Bar(() => PanelMinimumBar(panel))} " +
            "(ActiveRockView.RockBreakProgressBar, MinRockBreakProgress)";
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

    // Read both candidate rates from the XP tag's tooltip. Its wording is independent of the watch.
    static string DevXpPerSecond(string tooltip, RockRates ours)
    {
        var numbers = Regex.Matches(tooltip, @"\d[\d,]*(?:\.\d+)?").Select(m => double.Parse(m.Value.Replace(",", ""), CultureInfo.InvariantCulture)).ToList();
        if (!ours.HasTime) return $"numbers in it [{string.Join(", ", numbers.Select(R))}]";
        double perHit = ours.XpEach / ours.HitSeconds;
        bool Near(double value) => numbers.Any(n => Math.Abs(n - value) <= Math.Max(0.006, 0.002 * value));
        return $"numbers in it [{string.Join(", ", numbers.Select(R))}]; XP per second on every hit {R(perHit)}: " +
            (Near(perHit) ? "IN IT, the game counts the XP of a hit over the time of a hit" : "NOT IN IT") +
            $"; ours per second counting breaks {R(ours.XpPerHour / 3600)}: " +
            (ours.XpPerHour is { } xp && Near(xp / 3600) ? "IN IT" : "NOT IN IT");
    }

    // The page's own words for the rules the hour rests on: the break chance, the break bar, shatter, the minimum, the
    // doubling, the XP tag; and the rows of its list of bonuses with their tooltips.
    void DevWords()
    {
        // A word that cannot be read says which member was tried, and the rest is read all the same.
        string Read(string what, string members, Func<string> words)
        {
            try { return $"{what} '{DevWordsOf(words())}'"; }
            catch (Exception e) { return $"{what} not read ({members}: {e.GetType().Name})"; }
        }
        static string Tr(string key) => key + (TranslationServer.Translate(key).ToString() is { } t && t != key ? $" = {t}" : "");
        static string Constant(Type type, string member) => Tr(DevConstant(type, member));
        Type stats = typeof(MiningActionStatsView);
        DevLog("words | " + string.Join(" | ", new[]
        {
            Read("the card's break chance", "MiningRockSelectView.s_rockBreakChanceStatDescription", () => BreakChanceWords(null).Text),
            Read("the card's start", "MiningRockSelectView.s_startMining", () => StartWords(null).Text),
            Read("the card's stop", "MiningRockSelectView.s_stop", () => StopWords(null).Text),
            Read("the break bar's tooltip", "ActiveRockView.ProgressTooltipText", () => Constant(typeof(ActiveRockView), "ProgressTooltipText")),
            Read("the rock break chance increase", "MiningActionStatsView.PercentIncreaseRockBreakChanceTitle and ...Tooltip",
                () => Constant(stats, "PercentIncreaseRockBreakChanceTitle") + ": " + Constant(stats, "PercentIncreaseRockBreakChanceTooltip")),
            Read("shatter", "MiningActionStatsView.ShatterChanceTitle and ...Tooltip",
                () => Constant(stats, "ShatterChanceTitle") + ": " + Constant(stats, "ShatterChanceTooltip")),
            Read("the minimum break bar", "MiningActionStatsView.MinRockBreakBarChanceTitle and ...Tooltip",
                () => Constant(stats, "MinRockBreakBarChanceTitle") + ": " + Constant(stats, "MinRockBreakBarChanceTooltip")),
            Read("after a hit: the raise", "ChanceIncreaseIconLabel.PercentFormat", () => Constant(typeof(ChanceIncreaseIconLabel), "PercentFormat")),
            Read("after a hit: a break", "ChanceIncreaseIconLabel.s_breakText", () => BreakLabelWords(null).Text),
            Read("after a hit: a shatter", "ChanceIncreaseIconLabel.s_shatterText", () => ShatterLabelWords(null).Text),
            Read("the rock break icon", "RockBreakIcon.s_statName and s_statDescription", () => BreakIconName(null).Text + ": " + BreakIconWords(null).Text),
            Read("the doubling icon", "DoublingIcon.s_statName and s_statDescription", () => DoublingName(null).Text + ": " + DoublingWords(null).Text),
            Read("the XP tag", "SkillXPPanel.s_skillXpGained and s_xpPerSecond", () => XpGainedWords(null).Text + " / " + XpPerSecondWords(null).Text),
            Read("the filter's All button", "MiningCategoryFilterView.s_all", () => AllWords(null).Text),
            Read("the filter's box for hidden rocks", "MiningCategoryFilterView.s_showHidden", () => ShowHiddenWords(null).Text),
        }));
        // Check the guide's quote of the card's break chance after a game update.
        try
        {
            static string Flat(string text) => Regex.Replace(text, @"\s+", " ").Trim();
            string game = Flat(DevWordsOf(BreakChanceWords(null).Text)), quote = Flat(RockRates.GameWords);
            DevLog($"words | the break rule quoted in the guide '{quote}': " + (game.StartsWith(quote, StringComparison.Ordinal) ? "the game's words, as they begin"
                : game.Contains(quote, StringComparison.Ordinal) ? "in the game's words" : $"NOT THE GAME'S WORDS ('{game}')"));
        }
        catch (Exception e) { DevLog($"words | the guide's break rule could not be compared with the game's words (MiningRockSelectView.s_rockBreakChanceStatDescription: {e.GetType().Name})"); }
        // Every button of the page's filter, and which is which: the All button the put-back would press is found by the
        // game's word only (DevAllButton).
        try
        {
            MiningCategoryFilterView filter = FilterView(_page!);
            CheckBox box = ShowHiddenBox(filter);
            var categories = Native.CategoryButtons(filter) ?? new Dictionary<MiningRecipeCategory, Button>();
            Button? all = DevAllButton(out string why);
            DevLog("words | the filter's buttons: " + string.Join(" | ", filter.FindChildren("*", "Button", true, false).OfType<Button>().Select(b =>
                $"{b.GetType().Name} '{b.Text}'{(b.ToggleMode ? " toggle" : "")}{(b.ButtonPressed ? " pressed" : "")}{(b.IsVisibleInTree() ? "" : " (hidden)")}" +
                (b == box ? ", the box for hidden rocks" : "") + (b == all ? ", the All button by the game's word" : "") +
                (categories.FirstOrDefault(c => c.Value == b).Key is { } c ? $", the button of {c.Id}" : ""))) +
                (all == null ? $" | NO ALL BUTTON: {why}" : ""));
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException or NullReferenceException)
        {
            DevLog("words | the filter's buttons could not be read: " + e.Message);
        }
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
            if (verdict != "whole" || beyond) cut++;
            if (verdict.StartsWith("SHORTENED")) shortened++;
            if (beyond) past++;
            string tip = "";
            for (Node? n = c; n is Control k; n = k.GetParent())
                if (k.TooltipText != "") { tip = k.TooltipText; break; }
            notWhole.Add($"{(verdict.StartsWith("SHORTENED") || beyond && verdict == "whole" ? "CUT: " : "")}{verdict} '{One(text)}' {wide:0.#} wide in {room:0.#}" +
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
        if (_devOwnFilter != null || _devOwnScroll != null || _devSettle != null)
            throw new InvalidOperationException($"A page change still belongs to the request. Skipping {what} and putting it back.");
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
        Input.FlushBufferedEvents();
        _devFocusAfter = (target, (long)Engine.GetFramesDrawn());
        DevLog($"pressed {what} at {_devAimAt}, the pointer on {DevName(over)}");
        return true;
    }

    // The part of a control that can be seen: on the screen, inside the window's list when it is a line of it, inside
    // the page's list of rocks when it is in there, and, for a control of the game, beside the mod's own window, which
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

    // The pointer rests on a control and the game's tooltip comes up; its picture is shot-mining-<shot>.png. A hand at the
    // mouse moves it away: then it waits and points again. A tooltip that has not come up in 20 s of pointing is one
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
            if (before == null || DevTooltipRect() is not { } at)
                throw new InvalidOperationException("The tooltip could not be located in the picture.");
            using var now = _ctx.Tree.Root.GetTexture().GetImage();
            string proof = DevChanged(before, now, at, "the tooltip's place", "VISIBLE", "NOT VISIBLE");
            DevLog($"tooltip | {shot}: {proof}");
            if (!proof.StartsWith("VISIBLE:")) throw new InvalidOperationException(proof);
            Shot(shot);
            DevPointerBack();
            return true;
        }
        if (Time.GetTicksMsec() - _devTipSince < 20000) return false;
        DevLog($"tooltip | {shot} | NOT SHOWN in 20 s with the pointer on {DevName(over)}; its words are in the 'row' and 'tips' lines, and there is no picture of it");
        DevPointerBack();
        return true;
    }

    // ---- the rule in the game's own numbers: a card's break chance tooltip --------------------------------------------

    // The card whose break chance the pointer rests on: unlocked and wholly in sight, and never the rock being mined while
    // mining runs: a hit between the fill and the read would move its bar (the code review of turn 3, fault 1). Best is
    // one whose rock's break bar is part filled, apart from its break chance, with the bar and a hit's raise under 100%
    // (at 100% or more it would read as the sure break: fault 2), so that "Current chance" can only be one of the rock's
    // break chance, the bar, or the bar with a hit's raise; and one whose break chance differs from its recipe's base
    // (MiningRecipe.BaseProductChance), so that "Base chance" tells which it is. Equals keep the page's order.
    MiningRockSelectView? DevBreakCard(out string why)
    {
        why = "";
        var action = _mining!.Action;
        MiningRock? mined = action.IsStarted || DevMiningNow() ? action.CurrentRock : null;
        var cards = RockList(_page!).GetChildren().OfType<MiningRockSelectView>()
            .Where(c => GodotObject.IsInstanceValid(c) && c.Visible && CardRock(c) != null && !ReferenceEquals(CardRock(c), mined) &&
                !CardLocked(c).IsVisibleInTree() && c.IsVisibleInTree() && Native.Place(c, Cover(), out _) == CardSight.Clear &&
                CardBreak(c).IsVisibleInTree() && DevSight(CardBreak(c)).HasArea()).ToList();
        if (cards.Count == 0)
        {
            why = mined == null ? "no card of a rock that can be mined is wholly in sight"
                : $"no card of a rock that can be mined, other than {mined.Recipe.Id} being mined, is wholly in sight";
            return null;
        }
        static bool Apart(double a, double b) => Math.Abs(a - b) > DevNear * 4;
        int Score(MiningRockSelectView card)
        {
            DevRockNumbers n = DevNumbersOf(CardRock(card)!);
            bool part = n.Bar > DevNear * 4 && Apart(n.Bar, n.Chance);
            bool apart = part && n.Chance > DevNear * 4 && n.Bar + n.Chance < 1 - DevNear * 4;
            return (apart ? 4 : part ? 2 : 0) + (Apart(n.Recipe, n.Chance) ? 1 : 0);
        }
        return cards.OrderByDescending(Score).First();
    }

    // How near a number read from the tooltip must be to one of the rock's: its "{0:P2}" shows hundredths of a percent.
    const double DevNear = 0.00006;

    // The rock's numbers the tooltip is read against, as they were on one tick: its recipe's base, its break chance and
    // its break bar.
    readonly record struct DevRockNumbers(double Recipe, double Chance, double Bar);

    static DevRockNumbers DevNumbersOf(MiningRock rock) => new(rock.Recipe.BaseProductChance, rock.Stats.RockBreakChance.Value, rock.Progress);

    static string DevNumbersText(DevRockNumbers n) => $"the recipe's base {R(n.Recipe)}, its break chance {R(n.Chance)}, its break bar {R(n.Bar)}";

    // What step 4 keeps from tick to tick: the card, the control the pointer rests on, the tooltip's words and the screen
    // before the pointer came, and the rock's numbers on the last tick the tooltip was not shown and on the first it was.
    sealed class DevBreakRead(MiningRockSelectView card, Control aim, string aimed, string contentBefore, Image before)
    {
        public readonly MiningRockSelectView Card = card;
        public readonly Control Aim = aim;
        public readonly string Aimed = aimed, ContentBefore = contentBefore;
        public Image? Before = before;
        public Vector2 At;
        public DevRockNumbers? Unshown, Shown;
        public string ShownContent = "";
    }

    // Where the pointer rests for the card's break chance tooltip: the control the game's tooltip labels
    // (Tooltip.Labelling, a public member by ApiDump) when it is a part of the card in sight, else the card's break
    // chance label; named in the log (the code review of turn 3, fault 5).
    Control DevBreakAim(MiningRockSelectView card, out string aimed)
    {
        PercentLabel label = CardBreak(card);
        Control? labelling = null;
        string why;
        try
        {
            labelling = CardBreakTooltip(card)?.Labelling;
            why = labelling == null ? "Tooltip.Labelling is not set" : "";
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException) { why = $"Tooltip.Labelling could not be read ({e.GetType().Name})"; }
        if (labelling != null && GodotObject.IsInstanceValid(labelling) && card.IsAncestorOf(labelling) && labelling.IsVisibleInTree() && DevSight(labelling).HasArea())
        {
            aimed = $"the tooltip's Labelling, {DevName(labelling)}" + (labelling == label ? ", the card's break chance label"
                : labelling.IsAncestorOf(label) ? $", around the card's break chance label {DevName(label)}" : "");
            return labelling;
        }
        if (labelling != null && why.Length == 0)
            why = $"its Labelling, {DevName(labelling)}, is {(GodotObject.IsInstanceValid(labelling) && card.IsAncestorOf(labelling) ? "not in sight" : "not a part of the card")}";
        aimed = $"the card's break chance label {DevName(label)} ({why})";
        return label;
    }

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
    // being on the screen is taken, and said.
    static bool DevShownFor(TextTooltip? tooltip, Control aim, Vector2 at, out string said)
    {
        if (tooltip == null) { said = "the card has no tooltip"; return false; }
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

    // Rests the pointer on the card's break chance until the game shows its tooltip for it, filled (the card fills it when
    // it is shown for its target: MiningRockSelectView.RockBreakChanceTooltipShownForTargetHandler, by its name), then
    // reads its numbers and pictures it, only while the pointer is on it. The rock's numbers are taken on the tick the
    // tooltip is first seen shown, against the tick before and the read: NOT DECIDED when they moved. A tooltip not shown
    // in 20 s is one line, and the request goes on.
    bool DevBreakTooltip(DevBreakRead s)
    {
        if (DevHandAtMouse("the card's break chance")) { _devTipSince = 0; s.Unshown = s.Shown = null; return false; }
        if (_devTipSince == 0) _devTipSince = Time.GetTicksMsec();
        MiningRock rock = CardRock(s.Card) ?? throw new InvalidOperationException("The card has no rock.");
        TextTooltip? tooltip = CardBreakTooltip(s.Card);
        DevRockNumbers now = DevNumbersOf(rock);
        string content = tooltip == null ? "" : DevTooltipText(tooltip);
        bool on = DevPointerOn(s.Aim, s.At), shownFor = DevShownFor(tooltip, s.Aim, s.At, out string state);
        if (!(on && shownFor && content.Length > 0 && (tooltip!.IsVisibleInTree() || content != s.ContentBefore)))
        {
            s.Shown = null;
            s.Unshown = now;
            if (Time.GetTicksMsec() - _devTipSince >= 20000)
            {
                DevLog($"break rule | the break chance tooltip of the card of {rock.Recipe.Id} was not shown filled in 20 s with the pointer on " +
                    $"{DevName(_ctx.Tree.Root.GuiGetHoveredControl())}, aimed at {s.Aimed} (the tooltip: {state}; it holds '{content}'): the rule is not read from the page this time");
                return DevBreakDone(s);
            }
            if (!on)
            {
                Rect2 rect = DevSight(s.Aim);
                if (!rect.HasArea())
                {
                    DevLog($"break rule | the break chance of the card of {rock.Recipe.Id} went out of sight: the rule is not read from the page this time");
                    return DevBreakDone(s);
                }
                s.At = rect.GetCenter();
                DevPointer(s.At);
            }
            return false;
        }
        TextTooltip tip = tooltip!;
        // The first tick it is seen shown: the rock's numbers then, and the words.
        if (s.Shown == null)
        {
            s.Shown = now;
            s.ShownContent = content;
            _devFrameMark = 0;
        }
        // Two drawn frames after it came up, for the picture.
        if (!DevFramesDrawn()) return false;
        var moved = new List<string>();
        if (s.Unshown is { } unshown && unshown != s.Shown) moved.Add($"the tick before it was shown: {DevNumbersText(unshown)}");
        if (now != s.Shown) moved.Add($"at the read: {DevNumbersText(now)}");
        if (content != s.ShownContent) moved.Add($"its words at the read: '{content}'");
        // Its parts joined by line breaks, as the game's words are written, for the reading (the log joins them with " / ").
        string whole = DevWordsOf(string.Join("\n", Content(tip) ?? []));
        DevRockNumbers first = s.Shown.Value;
        DevLog($"break rule | the card of {rock.Recipe.Id}, the pointer on {s.Aimed}, the tooltip {state}: its label '{CardBreak(s.Card).Text}', its tooltip '{content}'" +
            $"{(tip.IsVisibleInTree() ? "" : " (filled, not seen on the screen)")} | " +
            DevBreakRule(first, whole, moved.Count == 0 ? "" : $"the rock's numbers when it was first seen shown: {DevNumbersText(first)}; {string.Join("; ", moved)}"));
        if (s.Before != null && DevDrawn(tip, out _) is { } drawn)
        {
            using var shot = _ctx.Tree.Root.GetTexture().GetImage();
            string proof = DevChangedPixels(s.Before, shot, drawn, "the game's break tooltip", "VISIBLE", "NOT VISIBLE");
            DevLog("break rule | " + proof);
            if (proof.StartsWith("VISIBLE:")) Shot("break-tooltip");
        }
        else DevLog("break rule | the tooltip could not be located in the picture");
        return DevBreakDone(s);
    }

    bool DevBreakDone(DevBreakRead s)
    {
        s.Before?.Dispose();
        s.Before = null;
        DevPointerBack();
        return true;
    }

    // The tooltip's "Base chance" and "Current chance" read against the rock's numbers, each number found by the words just
    // before it on its own line of the game's words (s_rockBreakChanceStatDescription), back to the end of a sentence, with
    // its percent sign on either side: a title holding the first sentence still reads (the code review of turn 3, fault 4).
    // What the game calls its current chance names the rule: the rock's break chance (SameChanceUntilFull), the bar with a
    // hit's raise (BarAfterRaise) or the bar (BarBeforeHit); AS SETTLED or NOT AS SETTLED against RockRates.Settled, and NOT
    // DECIDED when the rock's numbers moved while it was read.
    static string DevBreakRule(DevRockNumbers n, string content, string moved)
    {
        static string Flat(string text) => Regex.Replace(text, @"\s+", " ").Trim();
        string format;
        try { format = DevWordsOf(BreakChanceWords(null).Text); }
        catch (Exception e) { return $"NOT READ: the game's words (MiningRockSelectView.s_rockBreakChanceStatDescription) could not be read ({e.GetType().Name})"; }
        var before = new Dictionary<string, string>();
        foreach (string line in format.Replace("\r", "").Split('\n'))
            foreach (Match m in Regex.Matches(line, @"\{(\d+)(?::[^}]*)?\}"))
            {
                string head = line[..m.Index];
                int from = Math.Max(head.LastIndexOf('}') + 1, Math.Max(head.LastIndexOf(". ", StringComparison.Ordinal), head.LastIndexOf("! ", StringComparison.Ordinal)) + 1);
                before[m.Groups[1].Value] = Flat(head[from..]);
            }
        string flat = Flat(content);
        double? Read(string index)
        {
            if (!before.TryGetValue(index, out string? words) || words.Length == 0) return null;
            Match m = Regex.Match(flat, Regex.Escape(words) + @"\s*(?:%\s*(?<n>-?[\d.,]*\d)|(?<n>-?[\d.,]*\d)\s*%)", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            string shown = m.Groups["n"].Value;
            string number = shown.Contains('.') ? shown.Replace(",", "") : shown.Replace(',', '.');
            return double.Parse(number, NumberStyles.Float, CultureInfo.InvariantCulture) / 100;
        }
        string Words(string index) => before.TryGetValue(index, out string? words) ? $"'{words}'" : "none";
        if (Read("0") is not { } shownBase || Read("1") is not { } current)
            return $"NOT READ: the tooltip has no number after the game's words {Words("0")} and {Words("1")} (its words '{Flat(format)}')";
        string Is(double shown)
        {
            var names = new List<string>();
            if (Math.Abs(shown - n.Recipe) <= DevNear) names.Add("the recipe's base (MiningRecipe.BaseProductChance)");
            if (Math.Abs(shown - n.Chance) <= DevNear) names.Add("the rock's break chance (RockBreakChance)");
            if (Math.Abs(shown - n.Bar) <= DevNear) names.Add("the break bar (MiningRock.Progress)");
            if (Math.Abs(shown - Math.Min(1, n.Bar + n.Chance)) <= DevNear) names.Add("the bar with a hit's raise");
            return names.Count == 0 ? "NONE OF THE ROCK'S NUMBERS" : string.Join(" = ", names);
        }
        bool isChance = Math.Abs(current - n.Chance) <= DevNear, isBar = Math.Abs(current - n.Bar) <= DevNear,
            isRaised = Math.Abs(current - Math.Min(1, n.Bar + n.Chance)) <= DevNear;
        BreakRule? rule = moved.Length > 0 ? null
            : isChance && !isBar && !isRaised ? BreakRule.SameChanceUntilFull
            : isRaised && !isChance && !isBar ? BreakRule.BarAfterRaise
            : isBar && !isChance && !isRaised ? BreakRule.BarBeforeHit : null;
        string words = moved.Length > 0 ? $"NOT DECIDED: the rock's numbers moved while the tooltip was read ({moved})" : rule switch
        {
            BreakRule.SameChanceUntilFull => "THE GAME'S CURRENT CHANCE IS THE ROCK'S BREAK CHANCE, not its bar",
            BreakRule.BarAfterRaise => "THE GAME'S CURRENT CHANCE IS THE BAR WITH A HIT'S RAISE",
            BreakRule.BarBeforeHit => "THE GAME'S CURRENT CHANCE IS THE BAR",
            _ => "NOT DECIDED by this card: its numbers do not tell the readings apart",
        };
        string settled = rule == null ? "" : RockRates.Settled?.Rule == rule ? $" ({rule}), AS SETTLED" : $" ({rule}), NOT AS SETTLED (the window has {RockRates.Settled?.Rule.ToString() ?? "no rule"})";
        return $"the rock's numbers when the tooltip was first seen shown: {DevNumbersText(n)} | " +
            $"{Words("0")} {R(shownBase)}: {Is(shownBase)} | {Words("1")} {R(current)}: {Is(current)} | {words}{settled}";
    }

    // Where the words of a game's tooltip are drawn, in the picture's own pixels: its labels that are shown, each through
    // its canvas and its viewport (Viewport.GetScreenTransform), not the tooltip's own place in the root (check-3: the
    // tooltip, drawn where the real pointer was, proved "outside the picture" by its own place). Null when none is shown.
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

    // A control in the picture just taken against a picture taken before it came, over the part of it that is beside the
    // mod's own window (DevSight): the window lies over the Mining page, and a card it covers is not in the picture
    // (check-2: the card of a click was proved over its whole place, most of it under the window).
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
            if (!proof.StartsWith(" | VISIBLE:")) throw new InvalidOperationException(proof);
            Save(image, $"shot-mining-{state}.png");
            using var region = image.GetRegion(part);
            Save(region, $"crop-mining-{state}.png", proof);
            return;
        }
        Save(image, $"shot-mining-{state}.png");
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
        if (_devWithout == null || !_opener.ButtonPressed) throw new InvalidOperationException("No unlit reference for the lit round button.");
        string proof = DevChanged(_devWithout, image, _opener.GetGlobalRect(), "the lit round button", "VISIBLE", "NOT VISIBLE");
        if (!proof.StartsWith("VISIBLE:")) throw new InvalidOperationException(proof);
        using var region = image.GetRegion(part);
        Error result = region.SavePng(Path.Combine(_ctx.Directory, "crop-mining-strip.png"));
        if (result != Error.Ok) throw new IOException("crop-mining-strip.png: " + result);
        DevLog($"crop-mining-strip.png | the button at {_opener.GetGlobalRect()}, lit={_opener.ButtonPressed}, {strip.GetChildCount()} controls in the strip, {proof}{HandNote}");
    }
}
#endif
