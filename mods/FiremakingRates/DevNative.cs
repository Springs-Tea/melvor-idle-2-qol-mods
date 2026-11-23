#if DEVTOOLS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Components.CategorizedCraftingAction;
using GamesByMalcs.Melvor2.Components.CraftingAction;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.CustomControls;
using GamesByMalcs.Melvor2.Entities.Firemaking;
using GamesByMalcs.Melvor2.Entities.Firemaking.Statistics;
using GamesByMalcs.Melvor2.GameMenus;
using GamesByMalcs.Melvor2.GameMenus.Pages;
using GamesByMalcs.Melvor2.InfoIcons;
using GamesByMalcs.Melvor2.InfoLabels;
using GamesByMalcs.Melvor2.InfoTables;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.Stats.Nodes;
using GamesByMalcs.Melvor2.Tooltips;
using Godot;

namespace FiremakingRates;

// Development only: the parts of the game's Firemaking page it keeps to itself, read (never pressed, never changed) by
// the requests in Dev.cs and DevWatch.cs, as Woodcutting Rates' DevNative.cs reads its page's. Member names and types
// from tools/ApiDump only; no method body is read. The page's Start button (FiremakingActionView.StartButton) and every
// control of the bonfire that adds logs, lights, upgrades or sets a quantity (AddLogsButton, ToggleBonfire, UpgradeButton,
// QuantitySlider, QuantitySpinBox, Quantity25Button, Quantity50Button, Quantity100Button) are not named here at all.
static class DevNative
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrentPage")]
    internal static extern (GamePage, ActiveGamePage)? CurrentPage(GameView view);

    // The page: its part of the bonfire (FiremakingActiveView.BonfireView); its part with the logs is Native.ActionView.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_BonfireView")]
    internal static extern FiremakingBonfireView? BonfireView(FiremakingActiveView page);

    // The part with the logs: its category buttons, and the selected log's panel as the page shows it.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_categoryButtons")]
    internal static extern ref Dictionary<ActiveFiremakingCategory, AnimatedButton>? CategoryButtons(FiremakingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CategoryButtonContainer")]
    internal static extern VBoxContainer? CategoryColumn(FiremakingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Inputs")]
    internal static extern InputsContainer? Inputs(FiremakingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Outputs")]
    internal static extern OutputsContainer? Outputs(FiremakingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrentRecipe")]
    internal static extern Label? BurningLabel(FiremakingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SelectedLogIcon")]
    internal static extern TextureRect? SelectedIcon(FiremakingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SelectedLogName")]
    internal static extern Label? SelectedName(FiremakingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LogIntervalLabel")]
    internal static extern DoubleFormattedLabel? IntervalLabel(FiremakingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_BonfireTimeLabel")]
    internal static extern DoubleFormattedLabel? BonfireTimeLabel(FiremakingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_BonfireTimeTooltip")]
    internal static extern TextTooltip? BonfireTimeTooltip(FiremakingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_StatsView")]
    internal static extern FiremakingActionStatsView? StatsView(FiremakingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Doubling")]
    internal static extern DoublingIcon? DoublingIcon(FiremakingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Preservation")]
    internal static extern PreservationIcon? PreservationIcon(FiremakingActionView view);

    // The page's own words, kept as LocalizedString fields (read as Woodcutting Rates' check read its own) or as constants
    // (Constant, by reflection).
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_burningRecipe")]
    internal static extern ref LocalizedString BurningWords(FiremakingActionView? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_notBurning")]
    internal static extern ref LocalizedString NotBurningWords(FiremakingActionView? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_logsInBank")]
    internal static extern ref LocalizedString LogsInBankWords(FiremakingBonfireView? owner);

    // The bonfire's part, read only: its words and its times.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_StatusLabel")]
    internal static extern Label? BonfireStatus(FiremakingBonfireView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TimeLeft")]
    internal static extern DoubleFormattedLabel? BonfireTimeLeft(FiremakingBonfireView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_MaxTime")]
    internal static extern DoubleFormattedLabel? BonfireMaxTime(FiremakingBonfireView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TimePerLogLabel")]
    internal static extern DoubleFormattedLabel? BonfireTimePerLog(FiremakingBonfireView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TimeAddedLabel")]
    internal static extern DoubleFormattedLabel? BonfireTimeAdded(FiremakingBonfireView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LogsInBank")]
    internal static extern RichTextLabel? BonfireLogsInBank(FiremakingBonfireView view);

    // The list of logs: its scroll, the category it shows, the grid of buttons without a subcategory.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ScrollContainer")]
    internal static extern ScrollContainer? ListScroll(CraftingRecipeSelect select);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CategoryName")]
    internal static extern Label? ListCategoryName(CraftingRecipeSelect select);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_UncategorisedRecipeContainer")]
    internal static extern GridContainer? ListGrid(CraftingRecipeSelect select);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ActiveCategory")]
    internal static extern IActiveCraftingRecipeCategory? ListCategory(CraftingRecipeSelect select);

    // A log's button in the list.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Recipe")]
    internal static extern IActiveCraftingRecipe? ButtonLog(CraftingRecipeButton button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RecipeName")]
    internal static extern Label? ButtonName(CraftingRecipeButton button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RecipeMedia")]
    internal static extern TextureRect? ButtonPicture(CraftingRecipeButton button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SkillXPPanel")]
    internal static extern SkillXPPanel? ButtonXp(CraftingRecipeButton button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LockedContainer")]
    internal static extern VBoxContainer? ButtonLocked(CraftingRecipeButton button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ProductQtyLabel")]
    internal static extern DoubleFormattedLabel? ButtonProductQuantity(CraftingRecipeButton button);
    // The handlers of a log button's click (BaseButtonContainer.Pressed is an event of its own, not a signal), as Fishing
    // Rates' request presses its spot buttons to compare them with a click on a line.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "Pressed")]
    internal static extern ref Action? PressedHandlers(BaseButtonContainer button);

    // An XP tag, its tooltip and the time it is bound to; a text tooltip's words.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SkillXpLabel")]
    internal static extern Int32FormattedLabel XpLabel(SkillXPPanel panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_XPTooltip")]
    internal static extern TextTooltip? XpTooltip(SkillXPPanel panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_IntervalStat")]
    internal static extern ValueStatNode<double>? XpInterval(SkillXPPanel panel);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_contentText")]
    internal static extern ref List<string>? Content(TextTooltip tooltip);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Title")]
    internal static extern Label TooltipTitle(TextTooltip tooltip);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_skillXpGained")]
    internal static extern ref LocalizedString XpGained(SkillXPPanel? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_xpPerSecond")]
    internal static extern ref LocalizedString XpPerSecond(SkillXPPanel? owner);

    // A row of a list of bonuses: the stat it shows, its value as written and its own tooltip.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Stats")]
    internal static extern ModifierStatNodes? RowStat(ModifierStatLabel row);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ValueText")]
    internal static extern DoubleLabel RowValue(ModifierStatLabel row);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Tooltip")]
    internal static extern TextTooltip? RowTooltip(ModifierStatLabel row);

    // The doubling and preservation icons of the selected log's panel: what each is bound to, its words.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Stats")]
    internal static extern ValueStatNode<double>? IconStat(ModifierStatsInfoIcon icon);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_statName")]
    internal static extern ref LocalizedString IconName(DoublingIcon? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_statDescription")]
    internal static extern ref LocalizedString IconWords(DoublingIcon? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_statName")]
    internal static extern ref LocalizedString IconName(PreservationIcon? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_statDescription")]
    internal static extern ref LocalizedString IconWords(PreservationIcon? owner);

    // An item as an icon shows it (Woodcutting Rates' DevNative reads its cards' icons the same way).
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_isChance")]
    internal static extern ref bool IconIsChance(ItemInfoIcon icon);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_quantityValue")]
    internal static extern ref double IconQuantity(ItemInfoIcon icon);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_chanceValue")]
    internal static extern ref double IconChance(ItemInfoIcon icon);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_tooltipTitleChance")]
    internal static extern ref LocalizedString ItemChanceTitle(ItemInfoIcon? owner);

    // The game's own names of its Firemaking statistics: "logs burnt", "fires lit" and the bonfire's.
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_displayName")]
    internal static extern ref LocalizedString StatisticName(LogsBurntStatistic? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_displayName")]
    internal static extern ref LocalizedString StatisticName(FiresLitStatistic? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_displayName")]
    internal static extern ref LocalizedString StatisticName(BonfireTimeFromFiresStatistic? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_displayName")]
    internal static extern ref LocalizedString StatisticName(BonfireTimeFromLogsStatistic? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_displayName")]
    internal static extern ref LocalizedString StatisticName(BonfireTimeStatistic? owner);

    /// <summary>
    /// A constant of the game's words, by the name ApiDump gives it, read by reflection: a constant is not a field an
    /// UnsafeAccessor can reach (Mining Rates' check-2: MissingFieldException). Throws, with the type and member named,
    /// when it is not there.
    /// </summary>
    internal static string Constant(Type type, string member)
    {
        FieldInfo field = type.GetField(member, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(type.Name, member);
        object? value = field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null);
        return value as string ?? throw new InvalidCastException($"{type.Name}.{member} holds no words");
    }

    /// <summary>
    /// The controls a game tooltip shows for (Tooltip.Targets, private, of a type of its own that cannot be named here,
    /// so read by reflection, by name), with its Labelling (public). Empty when they cannot be read.
    /// </summary>
    internal static List<Control> Targets(Tooltip tooltip)
    {
        var found = new List<Control>();
        try
        {
            if (typeof(Tooltip).GetProperty("Targets", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(tooltip) is IDictionary targets)
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
}
#endif
