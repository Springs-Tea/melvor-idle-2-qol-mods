using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.CustomControls;
using GamesByMalcs.Melvor2.Entities.Astrology;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.Modifiers;
using Godot;

namespace AstrologyOverview;

/// <summary>
/// The words and the star picture the overview takes from the game's own Astrology screen, read once when the
/// page is found. Plain words of the mod's own stand in for any the screen does not have.
/// </summary>
internal sealed record GameWords(string Minor, string Major, string? BonusHeading, string Enemy, Texture2D? Star)
{
    public static readonly GameWords Plain = new("Minor", "Major", null, "Enemy", null);

    /// <summary>"Heading: text", without a second colon when the game's heading already ends in one.</summary>
    public static string Lead(string heading, string text) => heading + (heading.EndsWith(':') ? " " : ": ") + text;
}

// Read-only access to the game's constellation list, its constellation screen and its grouped requirements.
// Only member names and types from tools/ApiDump are used here.
internal static class Native
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ConstellationContainer")]
    internal static extern HFlowContainer Cards(ConstellationSelectView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Constellation")]
    internal static extern ActiveAstrologyConstellation? BoundTo(SelectConstellationView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SelectButton")]
    internal static extern AnimatedButton Select(SelectConstellationView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LockedContainer")]
    internal static extern Container LockPanel(SelectConstellationView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NextMinorNodeCost")]
    internal static extern DoubleFormattedRichTextLabel MinorCost(CurrentConstellationView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_AvailableMinorNodes")]
    internal static extern Label MinorAvailable(CurrentConstellationView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_AvailableMajorNodes")]
    internal static extern Label MajorAvailable(CurrentConstellationView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ConstellationName")]
    static extern Label ConstellationName(CurrentConstellationView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_BonesesPerMinorNode")]
    internal static extern ModifierValuesView MinorBonuses(CurrentConstellationView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_MajorStarTexture")]
    static extern Texture2D? MajorStar(CurrentConstellationView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_MinorStarTexture")]
    static extern Texture2D? MinorStar(CurrentConstellationView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_AppliedToEnemyLabel")]
    internal static extern Label? EnemyLabel(ModifierValuesView view);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_nodesAvailableToUnlock")]
    internal static extern ref PluralString Available(CurrentConstellationView type);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_allNodesUnlocked")]
    internal static extern ref LocalizedString AllUnlocked(CurrentConstellationView type);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_minorNodesForMajorNode")]
    internal static extern ref PluralString MajorCostWords(CurrentConstellationView type);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_orConditions")]
    static extern ref List<OrConditionGroup> Alternatives(ConditionGroup group);

    // Keep the card's order. If an alternative has been met, its whole clause is satisfied.
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

    // ---- the game's words, read off its own screen --------------------------------------------------------------

    /// <summary>
    /// The tab names of the minor and major nodes, the heading over the bonus every minor node gives, the
    /// game's word for bonuses that apply to the enemy, and the star the constellation screen draws a major node
    /// with. Dev.cs logs each of them with the place it was read from.
    /// </summary>
    internal static GameWords ReadWords(CurrentConstellationView view)
    {
        var plain = GameWords.Plain;
        var bonuses = MinorBonuses(view);
        return new GameWords(
            Words(FirstWords(view.MinorNodeButton)) ?? plain.Minor,
            Words(FirstWords(view.MajorNodeButton)) ?? plain.Major,
            Words(BonusHeading(view)),
            Words(EnemyLabel(bonuses)) ?? plain.Enemy,
            MajorStar(view) ?? MinorStar(view));
    }

    /// <summary>The words over a locked card's requirements, if the card has any.</summary>
    internal static string? LockWords(SelectConstellationView card) => Words(FirstWords(LockPanel(card)));

    static string? Words(Label? label) => label?.Text.Trim() is { Length: > 0 } text ? text : null;

    // A label with fixed words: a plain Label of the scene. The game's own label classes (numbers, counts,
    // formatted text) change with the constellation, and words with a digit in them are a count.
    static bool IsWords(Node node, ISet<Node>? skip) =>
        node.GetType() == typeof(Label) && skip?.Contains(node) != true && ((Label)node).Text.Trim() is { Length: > 0 } text &&
        !text.Any(char.IsDigit);

    /// <summary>The first label with fixed words under <paramref name="root"/>, past any requirement views.</summary>
    internal static Label? FirstWords(Node? root, ISet<Node>? skip = null)
    {
        if (root == null) return null;
        foreach (Node child in root.GetChildren())
        {
            if (child is ConditionGroupView or ConditionView) continue;
            if (IsWords(child, skip)) return (Label)child;
            if (FirstWords(child, skip) is { } found) return found;
        }
        return null;
    }

    static Label? LastWords(Node node, ISet<Node> skip)
    {
        if (node is ConditionGroupView or ConditionView) return null;
        for (int i = node.GetChildCount() - 1; i >= 0; i--)
            if (LastWords(node.GetChild(i), skip) is { } found) return found;
        return IsWords(node, skip) ? (Label)node : null;
    }

    /// <summary>
    /// The heading the constellation screen shows over its bonuses per minor node: the nearest label with fixed
    /// words before that list, in its own box or the one around it. The screen's changing labels are left out.
    /// </summary>
    internal static Label? BonusHeading(CurrentConstellationView view)
    {
        var skip = new HashSet<Node> { MinorAvailable(view), MajorAvailable(view), ConstellationName(view) };
        Node node = MinorBonuses(view);
        for (int up = 0; up < 2 && node.GetParent() is { } parent; up++, node = parent)
            for (int i = node.GetIndex() - 1; i >= 0; i--)
                if (LastWords(parent.GetChild(i), skip) is { } heading) return heading;
        return null;
    }

#if DEVTOOLS
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NextMajorNodeCost")]
    internal static extern RichTextLabel MajorCost(CurrentConstellationView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_KnowledgePerStudy")]
    internal static extern DoubleFormattedLabel KnowledgePerStudy(CurrentConstellationView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_IntervalDisplay")]
    internal static extern GamesByMalcs.Melvor2.StatDisplays.IntervalDisplay Interval(CurrentConstellationView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_MinorNodesContainer")]
    internal static extern VBoxContainer MinorNodes(CurrentConstellationView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_MajorNodesContainer")]
    internal static extern VBoxContainer MajorNodes(CurrentConstellationView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Node")]
    internal static extern AstrologyNode? NodeOf(MinorNodeView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NodeName")]
    internal static extern Label NameOf(MinorNodeView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Node")]
    internal static extern AstrologyNode? NodeOf(MajorNodeView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NodeName")]
    internal static extern Label NameOf(MajorNodeView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_PrimaryUnlockRequirements")]
    internal static extern ConditionGroupView PrimaryRequirements(SelectConstellationView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SecondaryUnlockRequirements")]
    internal static extern ConditionGroupView SecondaryRequirements(SelectConstellationView card);
#endif
}
