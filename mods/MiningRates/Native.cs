using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Entities.Mining;
using GamesByMalcs.Melvor2.Utils;
using Godot;

namespace MiningRates;

// Read-only access to the parts of the game's Mining page it keeps to itself, in one place, as Thieving Overview keeps
// its own (mods/ThievingOverview/Native.cs). Only member names and types from tools/ApiDump are used here. A member
// that is gone after a game update throws when it is first used: for a click on a line that switches the clicks off
// with one line in the log (MiningRatesMod.ShowOnPage), elsewhere it stops the mod with one line.
internal static class Native
{
    // The Mining page's card of every rock (MiningActiveView.RockViews): the cards the page's list of rocks shows, each
    // hidden while the page's category filter is on another category.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RockViews")]
    internal static extern Dictionary<MiningRock, MiningRockSelectView>? RockViews(MiningActiveView page);

    // The page's category filter: its button for each category (MiningCategoryFilterView._categoryButtons), the very
    // buttons a player clicks to see one category.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_categoryButtons")]
    internal static extern ref Dictionary<MiningRecipeCategory, Button>? CategoryButtons(MiningCategoryFilterView filter);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_orConditions")]
    static extern ref List<OrConditionGroup> Alternatives(ConditionGroup group);

    /// <summary>The card of a rock on the Mining page, or null when the page has none for it.</summary>
    internal static MiningRockSelectView? Card(MiningActiveView page, MiningRock rock) =>
        RockViews(page) is { } cards && cards.TryGetValue(rock, out MiningRockSelectView? card) && GodotObject.IsInstanceValid(card) &&
        !card.IsQueuedForDeletion() ? card : null;

    /// <summary>The scrolling list a card is in: the nearest scroll container around it.</summary>
    internal static ScrollContainer? ScrollOf(Control card)
    {
        for (Node? node = card.GetParent(); node != null; node = node.GetParent())
            if (node is ScrollContainer scroll) return scroll;
        return null;
    }

    /// <summary>The whole card is shown inside its list, as it is now.</summary>
    internal static bool InSight(Control card)
    {
        if (!card.IsVisibleInTree() || ScrollOf(card) is not { } scroll || !scroll.IsVisibleInTree()) return false;
        return scroll.GetGlobalRect().Grow(1).Encloses(card.GetGlobalRect());
    }

    /// <summary>
    /// What a group of requirements still misses, in the game's order: one clause per missing requirement, an "or" with
    /// all its alternatives together, a clause of which one alternative is met left out (Thieving Overview's
    /// Native.Missing).
    /// </summary>
    internal static IEnumerable<Condition[]> Missing(ConditionGroup? group)
    {
        if (group == null || group.AreMet) yield break;
        foreach (Condition condition in group.Conditions ?? Enumerable.Empty<Condition>())
        {
            if (condition.IsMet) continue;
            var alternatives = Alternatives(group)?.FirstOrDefault(g => g.Conditions.Contains(condition));
            if (alternatives == null) yield return [condition];
            else if (!alternatives.AreAnyMet && ReferenceEquals(condition, alternatives.Conditions[0]))
                yield return alternatives.Conditions.Cast<Condition>().ToArray();
        }
    }

    /// <summary>
    /// What is still missing of these groups (a rock's and its category's), each once, in the game's words: a skill
    /// level both ask for is named once, at the higher of the two, where that skill is first named (Thieving Overview's
    /// Native.Needs). "Locked" when the game puts none of them into words.
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

    /// <summary>The highest level of this skill the groups still ask for, or null.</summary>
    internal static int? LevelNeeded(IEnumerable<ConditionGroup?> groups, ExperienceComponent skill)
    {
        int? level = null;
        foreach (Condition[] clause in groups.SelectMany(Missing))
            if (clause is [LevelCondition needed] && ReferenceEquals(needed.Component, skill))
                level = Math.Max(level ?? 0, needed.Level);
        return level;
    }

    /// <summary>
    /// A requirement in the game's own words, without its colours. One the game cannot put into words leaves nothing,
    /// and the rock is only "Locked"; a member gone after an update still stops the mod.
    /// </summary>
    internal static string Words(Condition condition)
    {
        try { return StringUtils.StripBBCode(condition.GetDescription(false, false)).Trim(); }
        catch (Exception e) when (e is not (MissingMemberException or TypeLoadException)) { return ""; }
    }
}
