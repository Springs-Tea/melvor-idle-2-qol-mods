using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Components.Characters;
using GamesByMalcs.Melvor2.Components.DropTables;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Components.ItemOpening;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Entities.Thieving;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.Utils;
using Godot;

namespace ThievingOverview;

// Read-only access to the parts of the game's Thieving screen it keeps to itself, in one place. Only member names
// and types from tools/ApiDump are used here. A member that is gone after a game update throws when it is first
// used: in the window that stops the mod with one line in the log, in the marks on the game's screen it switches
// the marks off (ThievingOverviewMod.Marks).
internal static class Native
{
    // The Thieving page: its main view (the targets, the HP bar) and its View Possible Rewards window.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ActionView")]
    internal static extern ThievingActionView ActionView(ThievingActiveView page);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ThievingDropsModal")]
    internal static extern ViewThievingDropsModal Rewards(ThievingActiveView page);

    // View Possible Rewards, as Item Sources reads it (ItemSources/DropWindows.cs): the target it shows, its tables,
    // and the chances of its sections, which it writes through the game's own formats.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Active")]
    internal static extern ActiveThievingRecipe? Active(ViewThievingDropsModal window);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CommonDropTableView")]
    internal static extern DropTableView CommonTable(ViewThievingDropsModal window);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RareDropTableView")]
    internal static extern DropTableView RareTable(ViewThievingDropsModal window);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CompletionDropTableView")]
    internal static extern DropTableView CompletionTable(ViewThievingDropsModal window);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrencyChanceDoubleLabel")]
    internal static extern DoubleFormattedLabel CoinChance(ViewThievingDropsModal window);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CommonDoubleLabel")]
    internal static extern DoubleFormattedLabel CommonChance(ViewThievingDropsModal window);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RareDoubleLabel")]
    internal static extern DoubleFormattedLabel RareChance(ViewThievingDropsModal window);

    // The coins' "9 - 57" under the currency's chance: its tooltip says the coins per hour.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrencyInt32OutOfLabel")]
    internal static extern Int32OutOfLabel CoinQuantity(ViewThievingDropsModal window);

    // One row of a drop table: its chance as the window shows it, and the quantities it shows; the quantity's cell
    // carries the tooltip with the number per hour.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_DropChance")]
    internal static extern PercentLabel Chance(ItemDropView row);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ItemQuantity")]
    internal static extern Label Quantity(ItemDropView row);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_quantityMin")]
    internal static extern ref int QuantityMin(ItemDropView row);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_quantityMax")]
    internal static extern ref int QuantityMax(ItemDropView row);

    // A target's card on the Thieving screen: which target it shows, its success chance as the game writes it, and
    // its name, after which the Risky tag goes.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Active")]
    internal static extern ActiveThievingRecipe? Active(ThievingRecipeView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SuccessChance")]
    internal static extern DoubleFormattedRichTextLabel SuccessChance(ThievingRecipeView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RecipeName")]
    internal static extern Label CardName(ThievingRecipeView card);

    /// <summary>The target cards under a node of the Thieving screen; a card's own parts are not looked through.</summary>
    internal static IEnumerable<ThievingRecipeView> Cards(Node root)
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is ThievingRecipeView card) yield return card;
            else foreach (ThievingRecipeView inner in Cards(child)) yield return inner;
        }
    }

    // The page's own Select Location: one view per place, each with the Select button a player clicks.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LocationSelect")]
    internal static extern ThievingSelectLocationView LocationSelect(ThievingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_locationViews")]
    internal static extern ref Dictionary<ActiveThievingLocation, ThievingLocationSelectView>? PlaceViews(ThievingSelectLocationView select);

    // The damage the HP bar marks as the max hit: the game's own watcher, bound to the target being stolen from.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Damage")]
    internal static extern DamageWatcher? BarDamage(CharacterHealthProgressBar bar);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_orConditions")]
    static extern ref List<OrConditionGroup> Alternatives(ConditionGroup group);

    /// <summary>
    /// What a group of requirements still misses, in the game's order: one clause per missing requirement, an
    /// "or" with all its alternatives together, a clause of which one alternative is met left out
    /// (AstrologyOverview's Native.Missing).
    /// </summary>
    internal static IEnumerable<Condition[]> Missing(ConditionGroup? group)
    {
        if (group == null || group.AreMet) yield break;
        foreach (Condition condition in group.Conditions)
        {
            if (condition.IsMet) continue;
            var alternatives = Alternatives(group).FirstOrDefault(g => g.Conditions.Contains(condition));
            if (alternatives == null) yield return [condition];
            else if (!alternatives.AreAnyMet && ReferenceEquals(condition, alternatives.Conditions[0]))
                yield return alternatives.Conditions.Cast<Condition>().ToArray();
        }
    }

    /// <summary>
    /// What is still missing of these groups (a place's and its target's), each once, in the game's words: a skill
    /// level the place and the target both ask for is named once, at the higher of the two, where that skill is
    /// first named. "Locked" when the game puts none of them into words.
    /// </summary>
    internal static string Needs(IEnumerable<ConditionGroup?> groups)
    {
        List<Condition[]> clauses = groups.SelectMany(Missing).ToList();
        var highest = new Dictionary<ExperienceComponent, LevelCondition>(ReferenceEqualityComparer.Instance);
        foreach (Condition[] clause in clauses)
            if (clause is [LevelCondition level] && level.Component is { } skill && (!highest.TryGetValue(skill, out LevelCondition? top) || level.Level > top.Level))
                highest[skill] = level;
        var words = new List<string>();
        var named = new HashSet<ExperienceComponent>(ReferenceEqualityComparer.Instance);
        foreach (Condition[] clause in clauses)
        {
            Condition[] said = clause;
            if (clause is [LevelCondition level] && level.Component is { } skill)
            {
                if (!named.Add(skill)) continue;
                said = [highest[skill]];
            }
            string text = string.Join(" or ", said.Select(Words).Where(w => w != ""));
            if (text != "" && !words.Contains(text)) words.Add(text);
        }
        return words.Count == 0 ? "Locked" : string.Join(", ", words);
    }

    /// <summary>
    /// A requirement in the game's own words, without its colours. One the game cannot put into words leaves
    /// nothing, and the target is only "Locked"; a member gone after an update still stops the mod.
    /// </summary>
    internal static string Words(Condition condition)
    {
        try { return StringUtils.StripBBCode(condition.GetDescription(false, false)).Trim(); }
        catch (Exception e) when (e is not (MissingMemberException or TypeLoadException)) { return ""; }
    }

    /// <summary>
    /// True when a label writes its number through a .NET percent format and nothing else ("{0:P2}" writes 0.875 as
    /// "87.50%"): the number it is given is then a share of 1. Item Sources proves the unit of the loot chances the
    /// same way (DropWindows.ShareOf); any other format proves nothing.
    /// </summary>
    internal static bool WritesShare(string? composite) =>
        NumberFormat(composite) is ['P' or 'p', .. var digits] && digits.All(char.IsAsciiDigit);

    /// <summary>
    /// The format a label gives its number, read by .NET's own composite formatting: "P2" in "{0:P2}", "" for a
    /// plain "{0}". Null when the label writes no number or its format cannot be read (ItemSources' DropWindows).
    /// </summary>
    internal static string? NumberFormat(string? composite)
    {
        if (string.IsNullOrEmpty(composite)) return null;
        var seen = new FormatSeen();
        try { _ = string.Format(seen, composite, 0d); }
        catch (FormatException) { return null; }
        return seen.Format;
    }

    sealed class FormatSeen : IFormatProvider, ICustomFormatter
    {
        public string? Format;
        public object? GetFormat(Type? type) => type == typeof(ICustomFormatter) ? this : null;
        string ICustomFormatter.Format(string? format, object? arg, IFormatProvider? provider)
        {
            Format ??= format ?? "";
            return "";
        }
    }

    /// <summary>The rows of a drop table, also those inside a part of it.</summary>
    internal static IEnumerable<ItemDropView> Rows(Node table)
    {
        foreach (Node node in table.GetChildren())
        {
            if (node.IsQueuedForDeletion()) continue;
            if (node is ItemDropView row) yield return row;
            else foreach (ItemDropView inside in Rows(node)) yield return inside;
        }
    }

#if DEVTOOLS
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_IntervalDisplay")]
    internal static extern GamesByMalcs.Melvor2.StatDisplays.IntervalDisplay Interval(ThievingRecipeView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SkillXPLabel")]
    internal static extern DoubleFormattedLabel Xp(ThievingRecipeView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_MaxHit")]
    internal static extern Int32FormattedRichTextLabel MaxHit(ThievingRecipeView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Stealth")]
    internal static extern DoubleFormattedRichTextLabel Stealth(ThievingRecipeView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Perception")]
    internal static extern DoubleFormattedRichTextLabel Perception(ThievingRecipeView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Fortune")]
    internal static extern DoubleFormattedRichTextLabel Fortune(ThievingRecipeView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Stages")]
    internal static extern Int32OutOfLabel Stages(ThievingRecipeView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ViewRewardsButton")]
    internal static extern Button RewardsButton(ThievingRecipeView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RecipeName")]
    internal static extern Label RecipeName(ViewThievingDropsModal window);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_StageCompletionLabels")]
    internal static extern Label[]? StageLabels(ViewThievingDropsModal window);
    // The HP bar's own marks of the auto eat point and of the max hit, and its own words for them.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_AutoEatIndicator")]
    internal static extern Control? AutoEatMark(CharacterHealthProgressBar bar);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_MaxHitIndicator")]
    internal static extern Control? MaxHitMark(CharacterHealthProgressBar bar);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_autoEatThreshold")]
    internal static extern ref LocalizedString AutoEatWords(CharacterHealthProgressBar? bar);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_maxHitThieving")]
    internal static extern ref LocalizedString MaxHitWords(CharacterHealthProgressBar? bar);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_maxHitTitleThieving")]
    internal static extern ref LocalizedString MaxHitTitle(CharacterHealthProgressBar? bar);
    // The screen in front, private to the game's view (CombatSim's EnemyCards.CurrentPageOf).
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrentPage")]
    internal static extern (GamesByMalcs.Melvor2.GameMenus.Pages.GamePage, GamesByMalcs.Melvor2.GameMenus.Pages.ActiveGamePage)? CurrentPage(
        GamesByMalcs.Melvor2.GameMenus.GameView view);
#endif
}
