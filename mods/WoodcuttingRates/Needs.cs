using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Utils;

namespace WoodcuttingRates;

/// <summary>
/// What a locked tree still misses, in the game's own words. Copied from Thieving Overview (mods/ThievingOverview/
/// Native.cs: Missing, Needs, Words), which took it from Astrology Overview's Native.Missing: one clause per missing
/// requirement, an "or" with all its alternatives together, a skill level named once at the highest asked.
/// </summary>
static class Needs
{
    // The alternatives of a group ("this or that"). Private: one that is gone after a game update throws when it is first
    // used, which stops the mod with one line in the log, as in Thieving Overview.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_orConditions")]
    static extern ref List<OrConditionGroup> Alternatives(ConditionGroup group);

    /// <summary>
    /// What a group of requirements still misses, in the game's order: one clause per missing requirement, an "or" with
    /// all its alternatives together, a clause of which one alternative is met left out.
    /// </summary>
    static IEnumerable<Condition[]> Missing(ConditionGroup? group)
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
    /// What is still missing of these groups (a tree's and its category's), each once, in the game's words: a skill level
    /// both ask for is named once, at the higher of the two, where that skill is first named. "Locked" when the game puts
    /// none of them into words.
    /// </summary>
    public static string Of(IEnumerable<ConditionGroup?> groups)
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
    /// A requirement in the game's own words, without its colours. One the game cannot put into words leaves nothing,
    /// and the tree is only "Locked"; a member gone after an update still stops the mod.
    /// </summary>
    static string Words(Condition condition)
    {
        try { return StringUtils.StripBBCode(condition.GetDescription(false, false)).Trim(); }
        catch (Exception e) when (e is not (MissingMemberException or TypeLoadException)) { return ""; }
    }
}
