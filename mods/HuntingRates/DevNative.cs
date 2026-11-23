#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Entities.Hunting;
using GamesByMalcs.Melvor2.GameMenus;
using GamesByMalcs.Melvor2.GameMenus.Pages;
using GamesByMalcs.Melvor2.InfoLabels;
using GamesByMalcs.Melvor2.InfoTables;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.StatDisplays;
using GamesByMalcs.Melvor2.Stats.Nodes;
using GamesByMalcs.Melvor2.Tooltips;
using Godot;

namespace HuntingRates;

// Development only: the parts of the game's Hunting page it keeps to itself, read (never changed) by the requests in
// Dev.cs and DevWatch.cs, as Woodcutting Rates' DevNative reads its page's. Member names and types from tools/ApiDump
// only; no method body is read. The only page buttons named here are the ones the light check presses with the pointer
// as a player would: a location's button (HuntingLocationSelect.Buttons) and an animal's card button
// (HuntingAnimalTrackingView.ToggleButton), which select, and the journal's and the hunt's view buttons. The panel's
// button that starts a hunt (CreateRecipeButton), the stop button of the hunt in progress (StopButton), and the trap,
// bait and trapless controls are not named at all.
static class DevNative
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrentPage")]
    internal static extern (GamePage, ActiveGamePage)? CurrentPage(GameView view);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ViewJournalButton")]
    internal static extern Button JournalButton(HuntingTrackingView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrentRecipeContainer")]
    internal static extern HuntingCurrentRecipeContainer? Current(HuntingTrackingView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_HuntingMainContainer")]
    internal static extern HBoxContainer? Main(HuntingTrackingView view);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Buttons")]
    internal static extern Dictionary<HuntingLocation, HuntingLocationButton>? LocationButtons(HuntingLocationSelect select);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ToggleButton")]
    internal static extern Button CardButton(HuntingAnimalTrackingView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TrackingTool")]
    internal static extern Label CardTool(HuntingAnimalTrackingView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TrapType")]
    internal static extern Label CardTrapType(HuntingAnimalTrackingView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_UnlockedContainer")]
    internal static extern HBoxContainer CardUnlocked(HuntingAnimalTrackingView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LockedContainer")]
    internal static extern ConditionGroupView CardLocked(HuntingAnimalTrackingView card);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RecipeName")]
    internal static extern Label PanelName(HuntingSelectedRecipeContainer panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SelectedContainer")]
    internal static extern MarginContainer PanelShown(HuntingSelectedRecipeContainer panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SurveyingInterval")]
    internal static extern IntervalRangeDisplay PanelSurvey(HuntingSelectedRecipeContainer panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TrackingInterval")]
    internal static extern IntervalRangeDisplay PanelTrack(HuntingSelectedRecipeContainer panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TrappingInterval")]
    internal static extern IntervalRangeDisplay PanelTrap(HuntingSelectedRecipeContainer panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TrappingChanceLabel")]
    internal static extern PercentLabel PanelTrapChance(HuntingSelectedRecipeContainer panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_StatsView")]
    internal static extern HuntingActionStatsView PanelStats(HuntingSelectedRecipeContainer panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_OutputContainer")]
    internal static extern OutputsContainer PanelOutputs(HuntingSelectedRecipeContainer panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_AnimalTool")]
    internal static extern Label PanelTool(HuntingSelectedRecipeContainer panel);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrentAnimalName")]
    internal static extern Label CurrentName(HuntingCurrentRecipeContainer current);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CreatingContainer")]
    internal static extern MarginContainer CurrentShown(HuntingCurrentRecipeContainer current);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_XpToAdd")]
    internal static extern DoubleFormattedLabel XpLabel(ExperienceStatsXPView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_XpToAddMin")]
    internal static extern DoubleFormattedLabel XpMinLabel(ExperienceStatsXPView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_XpToAddMax")]
    internal static extern DoubleLabel XpMaxLabel(ExperienceStatsXPView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_XpToAddRangeContainer")]
    internal static extern HBoxContainer XpRangeBox(ExperienceStatsXPView view);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Stats")]
    internal static extern ModifierStatNodes? RowStat(ModifierStatLabel row);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ValueText")]
    internal static extern DoubleLabel? RowValue(ModifierStatLabel row);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Tooltip")]
    internal static extern TextTooltip? RowTooltip(ModifierStatLabel row);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_contentText")]
    internal static extern ref List<string>? Content(TextTooltip tooltip);

    // The animal's own counters of a hunt that the game keeps to itself, only read by the watch.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_surveyFails")]
    internal static extern ref int SurveyFails(ActiveHuntingAnimal animal);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_noTrapSelected")]
    internal static extern ref LocalizedString NoTrapWords(HuntingAnimalSelectedTrapView? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_huntingStoppedNoTrap")]
    internal static extern ref LocalizedString StoppedNoTrapWords(ActiveHuntingAnimal? owner);

    /// <summary>
    /// The page's own words that are constants of a type, read by reflection by name (a constant has no field to reach
    /// otherwise), with what is not there named: the titles and tooltips of HuntingActionStatsView, the formats of the
    /// interval displays, the XP view's format and the stat entry's ranks.
    /// </summary>
    internal static IEnumerable<(string Name, string Words)> Constants(Type type)
    {
        foreach (FieldInfo field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                     .Where(f => f.FieldType == typeof(string) || f.FieldType == typeof(LocalizedString)))
        {
            string words;
            try
            {
                object? value = field.GetValue(null);
                words = value is LocalizedString localized ? localized.Text ?? "" : value as string ?? "";
            }
            catch (Exception e) { words = $"(not read: {e.GetType().Name})"; }
            yield return (field.Name, words);
        }
    }
}
#endif
