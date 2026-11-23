#if DEVTOOLS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Entities.Woodcutting;
using GamesByMalcs.Melvor2.GameMenus;
using GamesByMalcs.Melvor2.GameMenus.Pages;
using GamesByMalcs.Melvor2.InfoIcons;
using GamesByMalcs.Melvor2.InfoLabels;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.StatDisplays;
using GamesByMalcs.Melvor2.Stats.Nodes;
using GamesByMalcs.Melvor2.Tooltips;
using Godot;

namespace WoodcuttingRates;

// Development only: the parts of the game's Woodcutting page it keeps to itself, read (never pressed, never changed) by
// the requests in Dev.cs and DevWatch.cs, as Mining Rates' Dev.cs reads its page's. Member names and types from
// tools/ApiDump only; no method body is read. A card's button that starts cutting (CutButton) and the big panel's stop
// button are not named here at all.
static class DevNative
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrentPage")]
    internal static extern (GamePage, ActiveGamePage)? CurrentPage(GameView view);

    // The page: its category filter, its list of cards and the scroll around it, its big panel.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CategoryFilterView")]
    internal static extern WoodcuttingCategoryFilterView Filter(WoodcuttingActiveView page);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TreeSelectContainer")]
    internal static extern HFlowContainer CardBox(WoodcuttingActiveView page);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TreeScrollContainer")]
    internal static extern ScrollContainer CardScroll(WoodcuttingActiveView page);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ActiveTree")]
    internal static extern ActiveTreeView BigPanel(WoodcuttingActiveView page);

    // The category filter: the row its buttons stand in, its box for hidden trees, and its own words: its "All"
    // button's, by which the request finds that button, and its box's.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CategoryFilterList")]
    internal static extern HBoxContainer CategoryRow(WoodcuttingCategoryFilterView filter);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ShowHiddenCheckBox")]
    internal static extern CheckBox ShowHidden(WoodcuttingCategoryFilterView filter);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_all")]
    internal static extern ref LocalizedString AllWords(WoodcuttingCategoryFilterView? filter);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_showHidden")]
    internal static extern ref LocalizedString ShowHiddenWords(WoodcuttingCategoryFilterView? filter);

    // A tree's card on the page.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Tree")]
    internal static extern WoodcuttingTree? Tree(WoodcuttingTreeSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TreeName")]
    internal static extern Label Name(WoodcuttingTreeSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CategoryName")]
    internal static extern Label CategoryName(WoodcuttingTreeSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TreeMedia")]
    internal static extern TextureRect Picture(WoodcuttingTreeSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_PerHealthGrantsContainer")]
    internal static extern GrantsContainer PerHit(WoodcuttingTreeSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_PerCutGrantsContainer")]
    internal static extern GrantsContainer PerCut(WoodcuttingTreeSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_IntervalRange")]
    internal static extern IntervalDisplay Interval(WoodcuttingTreeSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TreeMaxHP")]
    internal static extern Int32Label HitPoints(WoodcuttingTreeSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SkillXPPanel")]
    internal static extern SkillXPPanel Xp(WoodcuttingTreeSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_WoodcuttingStatsIcon")]
    internal static extern TextureRect StatsIcon(WoodcuttingTreeSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_WoodcuttingStatsTooltip")]
    internal static extern Tooltip? StatsTooltip(WoodcuttingTreeSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_WoodcuttingStats")]
    internal static extern WoodcuttingActionStatsView Stats(WoodcuttingTreeSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_OvergrowthEffectContainer")]
    internal static extern HBoxContainer Overgrowth(WoodcuttingTreeSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_UnlockedContainer")]
    internal static extern Control Unlocked(WoodcuttingTreeSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LockedContainer")]
    internal static extern Control LockedPart(WoodcuttingTreeSelectView card);
    // Read for its pressed state only: the card's hide button (pressed while the player hid the tree).
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ToggleVisibilityButton")]
    internal static extern TextureButton HideButton(WoodcuttingTreeSelectView card);

    // The big panel: the tree being cut.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Tree")]
    internal static extern WoodcuttingTree? Tree(ActiveTreeView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TreeName")]
    internal static extern Label Name(ActiveTreeView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NoTreeSelectedPanel")]
    internal static extern PanelContainer NoTree(ActiveTreeView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TreeSelectedPanel")]
    internal static extern VBoxContainer TreeShown(ActiveTreeView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_PerHealthGrantsContainer")]
    internal static extern GrantsContainer PerHit(ActiveTreeView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_PerHitNothingLabel")]
    internal static extern Label PerHitNothing(ActiveTreeView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_PerCutGrantsContainer")]
    internal static extern GrantsContainer PerCut(ActiveTreeView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_IntervalRange")]
    internal static extern IntervalDisplay Interval(ActiveTreeView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SkillXPPanel")]
    internal static extern SkillXPPanel Xp(ActiveTreeView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TreeHealthProgress")]
    internal static extern Int32OutOfLabel Health(ActiveTreeView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_WoodcuttingStats")]
    internal static extern WoodcuttingActionStatsView Stats(ActiveTreeView panel);

    // An interval as it is written, an XP tag, its tooltip and the time it is bound to.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_IntervalLabel")]
    internal static extern Label IntervalLabel(BaseIntervalDisplay display);
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

    // A row of a list of bonuses: the stat it shows, its value as written and its own tooltip.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Stats")]
    internal static extern ModifierStatNodes? RowStat(ModifierStatLabel row);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ValueText")]
    internal static extern DoubleLabel RowValue(ModifierStatLabel row);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Tooltip")]
    internal static extern TextTooltip? RowTooltip(ModifierStatLabel row);

    // One item of a card's per-hit or per-cut list, as the card shows it, and which of the game's stats a list shows
    // (Mining Rates' Dev.cs reads its cards' lists the same way).
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_isChance")]
    internal static extern ref bool IconIsChance(ItemInfoIcon icon);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_quantityValue")]
    internal static extern ref double IconQuantity(ItemInfoIcon icon);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_chanceValue")]
    internal static extern ref double IconChance(ItemInfoIcon icon);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ItemQuantityIcons")]
    internal static extern Dictionary<ValueStatNode<int>, ItemInfoIcon>? GrantQuantities(GrantsContainer grants);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ExperienceIcons")]
    internal static extern Dictionary<ExperienceStats, SkillExperienceIcon>? GrantXp(GrantsContainer grants);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ItemChanceGroups")]
    internal static extern Dictionary<ValuesStatNode<GamesByMalcs.Melvor2.Data.Items.ItemQuantityChance>, ItemInfoIconGroup>? GrantChances(GrantsContainer grants);

    // An overgrowth effect's icon on a card.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_EffectName")]
    internal static extern Label EffectName(OvergrowthEffectIcon icon);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_StacksLabel")]
    internal static extern Int32Label EffectStacks(OvergrowthEffectIcon icon);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_StacksLeftLabel")]
    internal static extern Int32FormattedLabel EffectStacksLeft(OvergrowthEffectIcon icon);

    // The page's own words kept as LocalizedString fields (read as Mining Rates' check read its own: s_all worked there).
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_skillXpGained")]
    internal static extern ref LocalizedString XpGained(SkillXPPanel? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_xpPerSecond")]
    internal static extern ref LocalizedString XpPerSecond(SkillXPPanel? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_statName")]
    internal static extern ref LocalizedString IconName(DoublingIcon? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_statDescription")]
    internal static extern ref LocalizedString IconWords(DoublingIcon? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_statName")]
    internal static extern ref LocalizedString IconName(TreeDoubleDamageIcon? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_statDescription")]
    internal static extern ref LocalizedString IconWords(TreeDoubleDamageIcon? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_statName")]
    internal static extern ref LocalizedString IconName(TreeFellChanceIcon? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_statDescription")]
    internal static extern ref LocalizedString IconWords(TreeFellChanceIcon? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_tooltipTitleChance")]
    internal static extern ref LocalizedString ItemChanceTitle(ItemInfoIcon? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_skillXP")]
    internal static extern ref LocalizedString SkillXpIconWords(SkillExperienceIcon? owner);

    /// <summary>
    /// A constant of the game's words, by the name ApiDump gives it, read by reflection: a constant is not a field an
    /// UnsafeAccessor can reach (Mining Rates' check-2: MissingFieldException), which turn 1 of this mod tried for
    /// WoodcuttingActionStatsView's titles and tooltips. Throws, with the type and member named, when it is not there.
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
