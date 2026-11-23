using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Entities.Woodcutting;
using GamesByMalcs.Melvor2.Utils;
using Godot;

namespace WoodcuttingRates;

/// <summary>How the card of a tree the Woodcutting page shows stands against the mod's window over it (Native.Place).</summary>
enum CardSight
{
    /// <summary>The whole card is inside its list, and the window covers none of it.</summary>
    Clear,
    /// <summary>A scroll of its list shows the whole card clear of the window: a click on its line makes it.</summary>
    Scroll,
    /// <summary>No scroll of its list shows the whole card clear of the window, though one shows it without the window.</summary>
    UnderWindow,
    /// <summary>No scroll of its list shows the whole card, window or not: a game window smaller than a card.</summary>
    NoRoom,
}

// Read-only access to the parts of the game's Woodcutting page it keeps to itself, in one place, as Mining Rates keeps
// its own (mods/MiningRates/Native.cs, from which this is copied, and Thieving Overview's before it). Only member names
// and types from tools/ApiDump are used here. A member that is gone after a game update throws when it is first used:
// for a click on a line that switches the clicks off with one line in the log (WoodcuttingRatesMod.ShowOnPage),
// elsewhere it stops the mod with one line.
internal static class Native
{
    // The Woodcutting page's card of every tree (WoodcuttingActiveView.TreeViews): the cards the page's list of trees
    // shows, each hidden while the page's category filter is on another category.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TreeViews")]
    internal static extern Dictionary<WoodcuttingTree, WoodcuttingTreeSelectView>? TreeViews(WoodcuttingActiveView page);

    // The page's category filter: its button for each category (WoodcuttingCategoryFilterView._categoryButtons), the
    // very buttons a player clicks to see one category.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_categoryButtons")]
    internal static extern ref Dictionary<WoodcuttingRecipeCategory, Button>? CategoryButtons(WoodcuttingCategoryFilterView filter);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_orConditions")]
    static extern ref List<OrConditionGroup> Alternatives(ConditionGroup group);

    /// <summary>The card of a tree on the Woodcutting page, or null when the page has none for it.</summary>
    internal static WoodcuttingTreeSelectView? Card(WoodcuttingActiveView page, WoodcuttingTree tree) =>
        TreeViews(page) is { } cards && cards.TryGetValue(tree, out WoodcuttingTreeSelectView? card) && GodotObject.IsInstanceValid(card) &&
        !card.IsQueuedForDeletion() ? card : null;

    /// <summary>The scrolling list a card is in: the nearest scroll container around it.</summary>
    internal static ScrollContainer? ScrollOf(Control card)
    {
        for (Node? node = card.GetParent(); node != null; node = node.GetParent())
            if (node is ScrollContainer scroll) return scroll;
        return null;
    }

    /// <summary>
    /// Where a control is drawn, in its viewport's own coordinates: through its canvas, so that a card of the page and
    /// the mod's window, which is on a canvas layer of its own (GameWindow.Layer), are measured alike.
    /// </summary>
    internal static Rect2 Drawn(Control control) => control.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, control.Size);

    // Less than a pixel at an edge is not a cover: the edges of the page's list and of the window let one through.
    const float Edge = 1;

    /// <summary>
    /// How a card the page shows stands in its list against the part of the screen the mod's window covers (null: no
    /// window on the screen), and the scroll of its list that shows the whole card clear of the window, the nearest to
    /// the scroll it has (for <see cref="CardSight.Scroll"/>; otherwise the scroll it has). The list scrolls up and
    /// down only: a card in a column the window lies across stands clear above or below the window, any other card
    /// anywhere in the list. Mining Rates' Native.Place, unchanged: the Woodcutting page's list of trees is a scroll
    /// container around a flow of cards, as the Mining page's list of rocks is.
    /// </summary>
    internal static CardSight Place(Control card, Rect2? cover, out int scroll)
    {
        Rect2 at = Drawn(card), view = card.GetViewportRect();
        int now = 0, least = 0, most = 0;
        // How far the card moves on the screen for one step of its list's scroll.
        float step = 0;
        if (ScrollOf(card) is { } list)
        {
            view = view.Intersection(Drawn(list));
            now = least = most = list.ScrollVertical;
            step = Math.Abs(list.GetGlobalTransformWithCanvas().Scale.Y);
            if (list.VerticalScrollMode != ScrollContainer.ScrollMode.Disabled && step > 0)
            {
                VScrollBar bar = list.GetVScrollBar();
                least = (int)Math.Ceiling(bar.MinValue);
                most = Math.Max(least, (int)Math.Floor(bar.MaxValue - bar.Page));
            }
        }
        scroll = now;
        bool across = cover is { } over && over.Position.X + Edge < at.End.X && at.Position.X < over.End.X - Edge;
        if (view.Grow(Edge).Encloses(at) && !(across && cover!.Value.Grow(-Edge).Intersects(at))) return CardSight.Clear;

        // Where the card's top may be: inside the list, and above or below a window that lies across its column, to the
        // pixel (the leeway above is only for a card that is there already). The list does not scroll sideways, so a
        // card cut off at a side is never whole.
        float top = view.Position.Y, bottom = view.End.Y, high = at.Size.Y;
        bool wide = at.Position.X >= view.Position.X - Edge && at.End.X <= view.End.X + Edge;
        (float From, float To) whole = (top, bottom - high);
        var spans = new List<(float From, float To)>();
        if (across)
        {
            Rect2 w = cover!.Value;
            spans.Add((top, Math.Min(bottom, w.Position.Y) - high));
            spans.Add((Math.Max(top, w.End.Y), bottom - high));
        }
        else spans.Add(whole);
        int? best = null;
        if (wide)
            foreach (var span in spans)
                if (Reach(span) is { } to && (best == null || Math.Abs(to - now) < Math.Abs(best.Value - now))) best = to;
        // A scroll to where it is would change nothing: a part of a pixel the test above did not let through.
        if (best is { } nearest) { if (nearest == now) return CardSight.Clear; scroll = nearest; return CardSight.Scroll; }
        return wide && Reach(whole) != null ? CardSight.UnderWindow : CardSight.NoRoom;

        // The scroll nearest to the one the list has that puts the card's top inside a span, within what the list can
        // scroll to; null when none does.
        int? Reach((float From, float To) span)
        {
            if (span.From > span.To) return null;
            if (step <= 0) return at.Position.Y >= span.From && at.Position.Y <= span.To ? now : null;
            double from = now + (at.Position.Y - span.To) / step, to = now + (at.Position.Y - span.From) / step;
            int first = Math.Max(least, (int)Math.Ceiling(from - 0.001)), last = Math.Min(most, (int)Math.Floor(to + 0.001));
            return first > last ? null : Math.Clamp(now, first, last);
        }
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
    /// What is still missing of these groups (a tree's and its category's), each once, in the game's words: a skill
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
    /// and the tree is only "Locked"; a member gone after an update still stops the mod.
    /// </summary>
    internal static string Words(Condition condition)
    {
        try { return StringUtils.StripBBCode(condition.GetDescription(false, false)).Trim(); }
        catch (Exception e) when (e is not (MissingMemberException or TypeLoadException)) { return ""; }
    }
}
