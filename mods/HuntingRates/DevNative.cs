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
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Modifiers;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.StatDisplays;
using GamesByMalcs.Melvor2.Stats.Nodes;
using GamesByMalcs.Melvor2.Tooltips;
using Godot;

namespace HuntingRates;

// Development only: the parts of the game's Hunting page it keeps to itself, read (never changed) by the requests in
// Dev.cs and DevWatch.cs, as Woodcutting Rates' DevNative reads its page's. Member names and types from tools/ApiDump
// only; no method body is read. The page's own controls the light check presses (a location's button, a card's button,
// the view buttons) are named in Native.cs, where the window's click uses them too. The panel's button that starts a
// hunt (CreateRecipeButton), the stop button of the hunt in progress (StopButton), and the trap, bait and trapless
// controls are not named at all.
static class DevNative
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrentPage")]
    internal static extern (GamePage, ActiveGamePage)? CurrentPage(GameView view);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_HuntingMainContainer")]
    internal static extern HBoxContainer? Main(HuntingTrackingView view);

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
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Title")]
    internal static extern Label TooltipTitle(TextTooltip tooltip);

    // The deferred reset of the running action, by its name (HuntingActionComponent.ShouldResetAction, internal): the
    // hunt guard reads it on every tick (code review 1, fault 2). Null when it cannot be read.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ShouldResetAction")]
    static extern bool ShouldResetAction(HuntingActionComponent action);
    internal static bool? ShouldReset(HuntingActionComponent action)
    {
        try { return ShouldResetAction(action); }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException) { return null; }
    }

    // The bonuses a failed try of the trap has put on the animal now (ActiveHuntingAnimal._activeFailBonuses), and the
    // values a modifier template holds (ModifierTemplate._baseValues, internal): a trap's TrapBonuses and FailBonuses
    // (HuntingTrapCapability), read only (code review 1, fault 7).
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_activeFailBonuses")]
    internal static extern ref (Item, ModifierValues)? ActiveFailBonuses(ActiveHuntingAnimal animal);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_baseValues")]
    internal static extern ref List<ComponentModifierValue>? TemplateValues(ModifierTemplate template);

    /// <summary>A set of modifier values in a line: each stat by its name and its value.</summary>
    internal static string Values(ModifierValues? values) => values == null ? "none"
        : values.Component.Count == 0 && values.Global.Count == 0 ? "empty"
        : string.Join(", ", values.Component.Select(c => $"{c.Stat?.Name} {c.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}")) +
          (values.Global.Count > 0 ? $" and {values.Global.Count} global" : "");

    /// <summary>What a template holds, in a line.</summary>
    internal static string Values(ModifierTemplate? template)
    {
        if (template == null) return "none";
        try
        {
            var values = TemplateValues(template);
            return values == null ? "not read" : values.Count == 0 ? "empty"
                : string.Join(", ", values.Select(c => $"{c.Stat?.Name} {c.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}"));
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException) { return $"not read ({e.GetType().Name})"; }
    }

    /// <summary>
    /// The controls a game tooltip shows for (Tooltip.Targets, private, of a type of its own that cannot be named here, so
    /// read by reflection, by name), with its Labelling (public); Woodcutting Rates' DevNative.Targets. Empty when they
    /// cannot be read.
    /// </summary>
    internal static List<Control> Targets(Tooltip tooltip)
    {
        var found = new List<Control>();
        try
        {
            if (typeof(Tooltip).GetProperty("Targets", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(tooltip) is System.Collections.IDictionary targets)
                found.AddRange(targets.Keys.OfType<Control>().Where(GodotObject.IsInstanceValid));
        }
        catch (Exception) { }
        try
        {
            if (tooltip.Labelling is { } labelling && GodotObject.IsInstanceValid(labelling) && !found.Contains(labelling)) found.Add(labelling);
        }
        catch (Exception) { }
        return found;
    }

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
                // A constant's value is in the metadata, not in a field (Woodcutting Rates' DevNative.Constant).
                object? value = field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null);
                words = value is LocalizedString localized ? localized.Text ?? "" : value as string ?? "";
            }
            catch (Exception e) { words = $"(not read: {e.GetType().Name})"; }
            yield return (field.Name, words);
        }
    }
}
#endif
