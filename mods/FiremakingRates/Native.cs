using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Components.CraftingAction;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.CustomControls;
using GamesByMalcs.Melvor2.Data.Recipes;
using GamesByMalcs.Melvor2.Entities.Firemaking;
using GamesByMalcs.Melvor2.InfoIcons;
using GamesByMalcs.Melvor2.InfoLabels;
using GamesByMalcs.Melvor2.InfoTables;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.Utils;
using Godot;

namespace FiremakingRates;

/// <summary>How a log's button in the Firemaking page's list stands against the mod's window over it (Native.Place).</summary>
enum CardSight
{
    /// <summary>The whole button is inside its list, and the window covers none of it.</summary>
    Clear,
    /// <summary>A scroll of its list shows the whole button clear of the window: a click on its line makes it.</summary>
    Scroll,
    /// <summary>No scroll of its list shows the whole button clear of the window, though one shows it without the window.</summary>
    UnderWindow,
    /// <summary>No scroll of its list shows the whole button, window or not: a game window smaller than a button.</summary>
    NoRoom,
    /// <summary>
    /// The page shows the log's category, but its list does not show the log's button now (none for it, or one not shown,
    /// such as one in a group of the list that is folded): a click selects the log, and no scroll brings its button.
    /// </summary>
    NotShown,
}

// Read-only access to the parts of the game's Firemaking page it keeps to itself, in one place, as Woodcutting Rates
// keeps its own (mods/WoodcuttingRates/Native.cs, from which this is copied, and Mining Rates' and Thieving Overview's
// before it). Only member names and types from tools/ApiDump are used here; nothing here is pressed or set. A member
// that is gone after a game update throws when it is first used: for a click on a line that switches the clicks off with
// one line in the log (FiremakingRatesMod.ClicksOff), elsewhere it stops the mod with one line.
internal static class Native
{
    // The page's part with its category buttons, its list of logs and the selected log (FiremakingActiveView.ActionView),
    // and the list of logs in it (FiremakingActionView.RecipeSelect, a CraftingRecipeSelect): the logs of the category the
    // page shows, each a button (CraftingRecipeSelect.Buttons; a log of a subcategory is in that subcategory's view,
    // RecipeSubcategoryView.Buttons).
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ActionView")]
    internal static extern FiremakingActionView? ActionView(FiremakingActiveView page);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RecipeSelect")]
    internal static extern CraftingRecipeSelect? RecipeSelect(FiremakingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Buttons")]
    static extern Dictionary<IActiveCraftingRecipe, CraftingRecipeButton>? Buttons(CraftingRecipeSelect select);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SubcategoryViews")]
    static extern Dictionary<RecipeSubcategory, RecipeSubcategoryView>? SubcategoryViews(CraftingRecipeSelect select);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Buttons")]
    static extern Dictionary<IActiveCraftingRecipe, CraftingRecipeButton>? Buttons(RecipeSubcategoryView view);

    // The page's own controls a click on a line goes through, as the player's own clicks there do (the orchestrator's
    // review 3: SelectCategory alone left two category buttons drawn pressed): its button of each category
    // (FiremakingActionView._categoryButtons, toggle buttons the player clicks to see a category), and the handlers of a
    // log's button's click (BaseButtonContainer.Pressed, an event of its own, not a signal), which a player's click on
    // the button raises.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_categoryButtons")]
    internal static extern ref Dictionary<ActiveFiremakingCategory, AnimatedButton>? CategoryButtons(FiremakingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "Pressed")]
    static extern ref Action? PressedHandlers(BaseButtonContainer button);

    // Where a click shows its result besides the list, read only for where the window first opens
    // (FiremakingRatesMod.FirstPlace), never pressed: the selected log's Input and Output (FiremakingActionView.Inputs,
    // Outputs), the page's burning label above its Start button with the selected log's name and time under it
    // (CurrentRecipe, SelectedLogName, LogIntervalLabel) and the chances' icons beside it (Doubling, Preservation), and
    // the bonfire's part (FiremakingActiveView.BonfireView). The Start button itself is never named.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Inputs")]
    static extern InputsContainer? Inputs(FiremakingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Outputs")]
    static extern OutputsContainer? Outputs(FiremakingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrentRecipe")]
    static extern Label? BurningLabel(FiremakingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SelectedLogName")]
    static extern Label? SelectedName(FiremakingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LogIntervalLabel")]
    static extern DoubleFormattedLabel? IntervalLabel(FiremakingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Doubling")]
    static extern DoublingIcon? DoublingIcon(FiremakingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Preservation")]
    static extern PreservationIcon? PreservationIcon(FiremakingActionView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_BonfireView")]
    static extern FiremakingBonfireView? BonfireView(FiremakingActiveView page);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_orConditions")]
    static extern ref List<OrConditionGroup> Alternatives(ConditionGroup group);

    /// <summary>The page's list of logs, or null when the page has none.</summary>
    internal static CraftingRecipeSelect? List(FiremakingActiveView page) =>
        ActionView(page) is { } view && GodotObject.IsInstanceValid(view) && RecipeSelect(view) is { } select && GodotObject.IsInstanceValid(select) ? select : null;

    /// <summary>Every button of the page's list of logs now, with its log: the logs of the category the page shows.</summary>
    internal static IEnumerable<(IActiveCraftingRecipe Log, CraftingRecipeButton Button)> Cards(FiremakingActiveView page)
    {
        if (List(page) is not { } select) yield break;
        var seen = new HashSet<CraftingRecipeButton>(ReferenceEqualityComparer.Instance);
        IEnumerable<KeyValuePair<IActiveCraftingRecipe, CraftingRecipeButton>> all = Buttons(select) ?? [];
        foreach (RecipeSubcategoryView view in SubcategoryViews(select)?.Values ?? Enumerable.Empty<RecipeSubcategoryView>())
            if (GodotObject.IsInstanceValid(view)) all = all.Concat(Buttons(view) ?? []);
        foreach (var (log, button) in all)
            if (log != null && GodotObject.IsInstanceValid(button) && !button.IsQueuedForDeletion() && seen.Add(button))
                yield return (log, button);
    }

    /// <summary>
    /// A part of the page where a click shows its result, as drawn now. Main: the selected log's Input and Output, which
    /// the window keeps clear wherever the lines keep their room; otherwise the burning part (the page's burning label,
    /// its Start button between it and the selected log's name and time) and the bonfire's, kept clear where it can.
    /// </summary>
    internal sealed record Part(string Name, Rect2 At, bool Main);

    /// <summary>The parts of the page where a click shows its result, those shown now (FiremakingRatesMod.FirstPlace).</summary>
    internal static List<Part> Parts(FiremakingActiveView page)
    {
        var parts = new List<Part>();
        if (ActionView(page) is { } view && GodotObject.IsInstanceValid(view))
        {
            void Add(string name, Control? control, bool main)
            {
                if (control != null && GodotObject.IsInstanceValid(control) && control.IsVisibleInTree() && Drawn(control) is { } at && at.HasArea())
                    parts.Add(new Part(name, at, main));
            }
            Add("Input", Inputs(view), true);
            Add("Output", Outputs(view), true);
            // The burning part: one box around the burning label above the Start button, the chances' icons, and the
            // selected log's name and time under it.
            Rect2? burning = null;
            foreach (Control? c in new Control?[] { BurningLabel(view), DoublingIcon(view), PreservationIcon(view), SelectedName(view), IntervalLabel(view) })
                if (c != null && GodotObject.IsInstanceValid(c) && c.IsVisibleInTree() && Drawn(c) is { } at && at.HasArea())
                    burning = burning is { } so ? so.Merge(at) : at;
            if (burning is { } box) parts.Add(new Part("the burning part", box, false));
        }
        if (BonfireView(page) is { } bonfire && GodotObject.IsInstanceValid(bonfire) && bonfire.IsVisibleInTree() && Drawn(bonfire) is { } fire && fire.HasArea())
            parts.Add(new Part("the bonfire", fire, false));
        return parts;
    }

    /// <summary>The page's own button of a category, or null when the page has none for it now.</summary>
    internal static AnimatedButton? CategoryButton(FiremakingActiveView page, ActiveFiremakingCategory category) =>
        ActionView(page) is { } view && GodotObject.IsInstanceValid(view) && CategoryButtons(view) is { } buttons &&
        buttons.TryGetValue(category, out AnimatedButton? button) && GodotObject.IsInstanceValid(button) && !button.IsQueuedForDeletion() ? button : null;

    /// <summary>
    /// A log's button in the page's list pressed the way a player's click presses it: the handlers of its click, which
    /// the button raises itself on a click (BaseButtonContainer.Pressed). False when it has none to call.
    /// </summary>
    internal static bool PressLogButton(CraftingRecipeButton button)
    {
        if (PressedHandlers(button) is not { } handlers) return false;
        handlers();
        return true;
    }

    /// <summary>The button of a log in the page's list, or null when the list does not have one for it now.</summary>
    internal static CraftingRecipeButton? Card(FiremakingActiveView page, ActiveFiremakingRecipe log) =>
        Cards(page).FirstOrDefault(c => ReferenceEquals(c.Log, log)).Button;

    /// <summary>The scrolling list a button is in: the nearest scroll container around it.</summary>
    internal static ScrollContainer? ScrollOf(Control card)
    {
        for (Node? node = card.GetParent(); node != null; node = node.GetParent())
            if (node is ScrollContainer scroll) return scroll;
        return null;
    }

    /// <summary>
    /// Where a control is drawn, in its viewport's own coordinates: through its canvas, so that a button of the page and
    /// the mod's window, which is on a canvas layer of its own (GameWindow.Layer), are measured alike.
    /// </summary>
    internal static Rect2 Drawn(Control control) => control.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, control.Size);

    // Less than a pixel at an edge is not a cover: the edges of the page's list and of the window let one through.
    const float Edge = 1;

    /// <summary>
    /// How a button the page shows stands in its list against the part of the screen the mod's window covers (null: no
    /// window on the screen), and the scroll of its list that shows the whole button clear of the window, the nearest to
    /// the scroll it has (for <see cref="CardSight.Scroll"/>; otherwise the scroll it has). The list scrolls up and
    /// down only: a button in a column the window lies across stands clear above or below the window, any other button
    /// anywhere in the list. Woodcutting Rates' Native.Place, unchanged: the Firemaking page's list of logs is a scroll
    /// container around a grid of buttons (CraftingRecipeSelect.ScrollContainer), as the Woodcutting page's list of trees
    /// is around a flow of cards.
    /// </summary>
    internal static CardSight Place(Control card, Rect2? cover, out int scroll)
    {
        Rect2 at = Drawn(card), view = card.GetViewportRect();
        int now = 0, least = 0, most = 0;
        // How far the button moves on the screen for one step of its list's scroll.
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

        // Where the button's top may be: inside the list, and above or below a window that lies across its column, to the
        // pixel (the leeway above is only for a button that is there already). The list does not scroll sideways, so a
        // button cut off at a side is never whole.
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

        // The scroll nearest to the one the list has that puts the button's top inside a span, within what the list can
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
    /// What is still missing of these groups (a log's and its category's), each once, in the game's words: a skill level
    /// both ask for is named once, at the higher of the two, where that skill is first named (Thieving Overview's
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
    /// and the log is only "Locked"; a member gone after an update still stops the mod.
    /// </summary>
    internal static string Words(Condition condition)
    {
        try { return StringUtils.StripBBCode(condition.GetDescription(false, false)).Trim(); }
        catch (Exception e) when (e is not (MissingMemberException or TypeLoadException)) { return ""; }
    }
}
