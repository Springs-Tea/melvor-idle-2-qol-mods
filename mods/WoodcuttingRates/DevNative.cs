#if DEVTOOLS
using System;
using System.Collections.Generic;
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
// the requests in Dev.cs and DevWatch.cs. Member names and types from tools/ApiDump only; no method body is read.
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

    // The category filter: a button per category, the row they stand in (with the "All" button), "Show hidden".
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_categoryButtons")]
    internal static extern ref Dictionary<WoodcuttingRecipeCategory, Button>? CategoryButtons(WoodcuttingCategoryFilterView filter);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CategoryFilterList")]
    internal static extern HBoxContainer CategoryRow(WoodcuttingCategoryFilterView filter);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ShowHiddenCheckBox")]
    internal static extern CheckBox ShowHidden(WoodcuttingCategoryFilterView filter);

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
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_WoodcuttingStats")]
    internal static extern WoodcuttingActionStatsView Stats(WoodcuttingTreeSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_OvergrowthEffectContainer")]
    internal static extern HBoxContainer Overgrowth(WoodcuttingTreeSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_UnlockedContainer")]
    internal static extern Control Unlocked(WoodcuttingTreeSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LockedContainer")]
    internal static extern Control LockedPart(WoodcuttingTreeSelectView card);

    // The big panel: the tree being cut.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Tree")]
    internal static extern WoodcuttingTree? Tree(ActiveTreeView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TreeName")]
    internal static extern Label Name(ActiveTreeView panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NoTreeSelectedPanel")]
    internal static extern PanelContainer NoTree(ActiveTreeView panel);
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

    // A row of a list of bonuses: the stat it shows and its value as written.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Stats")]
    internal static extern ModifierStatNodes? RowStat(ModifierStatLabel row);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ValueText")]
    internal static extern DoubleLabel RowValue(ModifierStatLabel row);

    // An overgrowth effect's icon on a card.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_EffectName")]
    internal static extern Label EffectName(OvergrowthEffectIcon icon);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_StacksLabel")]
    internal static extern Int32Label EffectStacks(OvergrowthEffectIcon icon);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_StacksLeftLabel")]
    internal static extern Int32FormattedLabel EffectStacksLeft(OvergrowthEffectIcon icon);

    // The words the page gives a tree's chances, its doubling, its XP tag and the icons of the game's other tree card.
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "DoubleDamageChanceTitle")]
    internal static extern ref string DoubleDamageTitle(WoodcuttingActionStatsView? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "DoubleDamageChanceTooltip")]
    internal static extern ref string DoubleDamageTip(WoodcuttingActionStatsView? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "InstantCutChanceTitle")]
    internal static extern ref string InstantCutTitle(WoodcuttingActionStatsView? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "InstantCutChanceTooltip")]
    internal static extern ref string InstantCutTip(WoodcuttingActionStatsView? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "WeakSpotChanceTitle")]
    internal static extern ref string WeakSpotTitle(WoodcuttingActionStatsView? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "WeakSpotChanceTooltip")]
    internal static extern ref string WeakSpotTip(WoodcuttingActionStatsView? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "DoublingChanceTitle")]
    internal static extern ref string DoublingTitle(ModifierStatView? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "DoublingChanceTooltip")]
    internal static extern ref string DoublingTip(ModifierStatView? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_skillXpGained")]
    internal static extern ref LocalizedString? XpGained(SkillXPPanel? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_xpPerSecond")]
    internal static extern ref LocalizedString? XpPerSecond(SkillXPPanel? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_statName")]
    internal static extern ref LocalizedString? IconName(DoublingIcon? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_statDescription")]
    internal static extern ref LocalizedString? IconWords(DoublingIcon? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_statName")]
    internal static extern ref LocalizedString? IconName(TreeDoubleDamageIcon? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_statDescription")]
    internal static extern ref LocalizedString? IconWords(TreeDoubleDamageIcon? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_statName")]
    internal static extern ref LocalizedString? IconName(TreeFellChanceIcon? owner);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_statDescription")]
    internal static extern ref LocalizedString? IconWords(TreeFellChanceIcon? owner);

    /// <summary>The words of one of the game's static strings, read where it is kept and translated as the game shows it.</summary>
    internal static string Said(Func<string?> read)
    {
        try
        {
            string? key = read();
            if (key == null) return "(none)";
            string shown = TranslationServer.Translate(key).ToString();
            return shown == key ? $"'{key}'" : $"'{shown}' (kept as '{key}')";
        }
        catch (Exception e) { return $"(could not be read: {e.GetType().Name})"; }
    }

    internal static string Said(Func<LocalizedString?> read)
    {
        try { return read() is { } words ? $"'{words.Text}'" : "(none)"; }
        catch (Exception e) { return $"(could not be read: {e.GetType().Name})"; }
    }
}
#endif
