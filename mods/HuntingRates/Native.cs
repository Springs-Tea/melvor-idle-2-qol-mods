using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.CustomControls;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Hunting;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.Modifiers;
using GamesByMalcs.Melvor2.StatDisplays;
using GamesByMalcs.Melvor2.Utils;
using Godot;

namespace HuntingRates;

/// <summary>How the card of an animal the Hunting page lists stands against the mod's window over it (Native.Place).</summary>
enum CardSight
{
    /// <summary>The whole card is inside its list, and the window covers none of it.</summary>
    Clear,
    /// <summary>A scroll of its list shows the whole card clear of the window: a click on its line makes it.</summary>
    Scroll,
    /// <summary>No scroll of its list shows the whole card clear of the window, though one shows it without the window.</summary>
    UnderWindow,
    /// <summary>No scroll of its list shows the whole card, window or not.</summary>
    NoRoom,
}

// Read-only access to the parts of the game's Hunting page it keeps to itself, in one place, as Woodcutting Rates keeps
// its own (mods/WoodcuttingRates/Native.cs, from which Place, Drawn, Missing and Needs are copied unchanged). Only
// member names and types from tools/ApiDump are used here. A member that is gone after a game update throws when it is
// first used: for a click on a line that switches the clicks off with one line in the log (HuntingRatesMod.ClicksOff),
// for what the list reads from the page (the look of an animal not discovered, the XP range, the surveying range) that
// is given up with one line, elsewhere it stops the mod with one line. Nothing here presses or changes anything by itself
// (the panel's surveying range, written for another animal by WriteSurveying, is written back for its own animal in the
// same call of HuntingRatesMod.AskSurveys, before anything is drawn); the page's own
// controls a click on a line goes through (a location's button, an animal's card button, the hunt's view button) are
// pressed only by HuntingRatesMod, by the rule of its ShowOnPage. The page's buttons that start or stop a hunt
// (HuntingSelectedRecipeContainer.CreateRecipeButton, HuntingCurrentRecipeContainer.StopButton) and its trap, bait and
// trapless controls are not named at all.
internal static class Native
{
    // The page's own view, with its location buttons, its list of the location's animals, the panel of the animal
    // selected, and the journal (HuntingActiveView.TrackingView and the parts of HuntingTrackingView).
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TrackingView")]
    internal static extern HuntingTrackingView? TrackingView(HuntingActiveView page);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_AnimalListContainer")]
    internal static extern VBoxContainer? AnimalList(HuntingTrackingView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SelectedRecipeContainer")]
    internal static extern HuntingSelectedRecipeContainer? Panel(HuntingTrackingView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LocationSelect")]
    internal static extern HuntingLocationSelect? LocationSelect(HuntingTrackingView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_JournalView")]
    internal static extern HuntingJournalView? Journal(HuntingTrackingView view);
    // The page's own buttons that show the hunt or the journal: a click on a line presses the hunt's as a player would
    // when the page shows the journal (HuntingRatesMod.HuntView), and draws both again when the view does not follow.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ViewHuntingButton")]
    internal static extern Button? HuntButton(HuntingTrackingView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ViewJournalButton")]
    internal static extern Button? JournalButton(HuntingTrackingView view);
    // The panel of the hunt in progress, which the window's first place keeps clear too.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrentRecipeContainer")]
    internal static extern HuntingCurrentRecipeContainer? Current(HuntingTrackingView view);

    // The page's own controls a click on a line selects through, as a player's own click there would (lesson 56): a
    // location's button (HuntingLocationSelect.Buttons; a BaseButtonContainer, whose Pressed is a C# event of its own,
    // raised on a click by its private InternalPressed) and an animal's card button (HuntingAnimalTrackingView
    // .ToggleButton, a Godot button).
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Buttons")]
    internal static extern Dictionary<HuntingLocation, HuntingLocationButton>? LocationButtons(HuntingLocationSelect select);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "Pressed")]
    internal static extern ref Action? PressedEvent(BaseButtonContainer button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ToggleButton")]
    internal static extern Button? CardButton(HuntingAnimalTrackingView card);
    // The tool as the card writes it (HuntingAnimalTrackingView.TrackingTool): the line's tooltip says it in the card's words.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TrackingTool")]
    internal static extern Label? ToolOf(HuntingAnimalTrackingView card);

    // An animal's card in the page's list: which animal it shows, and its name and picture as drawn.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Animal")]
    internal static extern ActiveHuntingAnimal? AnimalOf(HuntingAnimalTrackingView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_AnimalName")]
    internal static extern Label? NameOf(HuntingAnimalTrackingView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_AnimalIcon")]
    internal static extern SettingsIcon? IconOf(HuntingAnimalTrackingView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ExperienceStatsView")]
    internal static extern ExperienceStatsXPView? XpViewOf(HuntingAnimalTrackingView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ExperienceStatsView")]
    internal static extern ExperienceStatsXPView? XpViewOf(HuntingSelectedRecipeContainer panel);

    // The range the page writes an animal's XP in: the multipliers its XP view is bound with, and the stats it shows.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_minRangeMultiplier")]
    internal static extern ref double? XpLow(ExperienceStatsXPView view);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_maxRangeMultiplier")]
    internal static extern ref double? XpHigh(ExperienceStatsXPView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Experience")]
    internal static extern ExperienceStats? XpOf(ExperienceStatsXPView view);

    // The page's words for an animal that needs no tool (HuntingAnimalTrackingView.s_noTool).
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_noTool")]
    internal static extern ref LocalizedString NoToolWords(HuntingAnimalTrackingView? card);

    // The time of surveying as the panel of the animal selected writes it, a range from one survey to the most a hunt
    // takes ("Surveying: 3.84s - 11.52s"; HuntingSelectedRecipeContainer.SurveyingInterval, its MinSeconds and MaxSeconds).
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SurveyingInterval")]
    internal static extern IntervalRangeDisplay? SurveyOf(HuntingSelectedRecipeContainer panel);
    // How the panel writes that range for an animal (HuntingSelectedRecipeContainer.UpdateSurveyingInterval, a private
    // method that takes the animal), and the animal it was last filled in for (LastSelectedRecipe). No member of the
    // game's keeps the most surveys of a hunt (ApiDump: HuntingActionStats, HuntingAnimal, HuntingLocation,
    // ActiveHuntingAnimal and ActiveHuntingLocation hold only the time of one survey): the panel works it out when it
    // writes. HuntingRatesMod.AskSurveys has it written for every animal in turn and then for its own animal again, as
    // Recipe Rates has a game tooltip draw itself again (RecipeRatesMod.Redraw).
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "UpdateSurveyingInterval")]
    internal static extern void WriteSurveying(HuntingSelectedRecipeContainer panel, ActiveHuntingAnimal active);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LastSelectedRecipe")]
    internal static extern ActiveHuntingAnimal? PanelAnimal(HuntingSelectedRecipeContainer panel);

    // What a trap's bonuses after a failed try hold (HuntingTrapCapability.FailBonuses, a ModifierTemplate whose values
    // are internal: ModifierTemplate._baseValues).
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_baseValues")]
    static extern ref List<ComponentModifierValue>? TemplateValues(ModifierTemplate template);

    /// <summary>The bonuses a failed try with a trap puts on the animal (HuntingTrapCapability.FailBonuses), or null for none.</summary>
    internal static ModifierTemplate? FailTemplate(Item trap) =>
        trap.Capabilities.TryGet(out HuntingTrapCapability? capability) ? capability?.FailBonuses : null;

    /// <summary>
    /// A template of bonuses holds values, so that a failed try with its trap changes the next try's chance. Null when its
    /// values cannot be read (the game was updated?): the caller says so in one line and counts no catch with that trap
    /// (HuntingRatesMod.FailBonuses).
    /// </summary>
    internal static bool? HasValues(ModifierTemplate template)
    {
        try { return TemplateValues(template) is { Count: > 0 }; }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException) { return null; }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_orConditions")]
    static extern ref List<OrConditionGroup> Alternatives(ConditionGroup group);

    /// <summary>The cards of the page's list of animals, those it holds now (the animals of the location it shows).</summary>
    internal static IEnumerable<HuntingAnimalTrackingView> Cards(HuntingActiveView page) =>
        TrackingView(page) is { } view && AnimalList(view) is { } list
            ? list.GetChildren().OfType<HuntingAnimalTrackingView>().Where(c => GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion())
            : Enumerable.Empty<HuntingAnimalTrackingView>();

    /// <summary>A location's button on the page, or null when the page has none for it.</summary>
    internal static HuntingLocationButton? LocationButton(HuntingActiveView page, HuntingLocation? location) =>
        location != null && TrackingView(page) is { } view && LocationSelect(view) is { } select && LocationButtons(select) is { } buttons &&
        buttons.TryGetValue(location, out HuntingLocationButton? button) && GodotObject.IsInstanceValid(button) ? button : null;

    /// <summary>The card of an animal in the page's list, or null when the list holds none for it.</summary>
    internal static HuntingAnimalTrackingView? Card(HuntingActiveView page, ActiveHuntingAnimal animal) =>
        Cards(page).FirstOrDefault(c => ReferenceEquals(AnimalOf(c), animal));

    /// <summary>
    /// The game's own word for something not found yet, as its journal writes an undiscovered species
    /// (AnimalSpeciesView.HiddenText, a private constant read by its name), or null when it cannot be read.
    /// </summary>
    internal static string? HiddenWord()
    {
        try
        {
            // A constant has no field an accessor can reach: its value is read from the metadata (Woodcutting Rates'
            // DevNative.Constant, after Mining Rates' check-2).
            FieldInfo? field = typeof(AnimalSpeciesView).GetField("HiddenText", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            object? value = field == null ? null : field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null);
            return value is string { Length: > 0 } word ? StringUtils.StripBBCode(word).Trim() : null;
        }
        catch (Exception e) when (e is not (MissingMemberException or TypeLoadException)) { return null; }
    }

    /// <summary>The scrolling list a control is in: the nearest scroll container around it.</summary>
    internal static ScrollContainer? ScrollOf(Control card)
    {
        for (Node? node = card.GetParent(); node != null; node = node.GetParent())
            if (node is ScrollContainer scroll) return scroll;
        return null;
    }

    /// <summary>
    /// Where a control is drawn, in its viewport's own coordinates: through its canvas, so that a part of the page and
    /// the mod's window, which is on a canvas layer of its own (GameWindow.Layer), are measured alike.
    /// </summary>
    internal static Rect2 Drawn(Control control) => control.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, control.Size);

    // Less than a pixel at an edge is not a cover: the edges of the page's list and of the window let one through.
    const float Edge = 1;

    /// <summary>A part of the page is covered by the window (null: no window on the screen), beyond its edges.</summary>
    internal static bool Covered(Control part, Rect2? cover) => cover is { } over && over.Grow(-Edge).Intersects(Drawn(part));

    /// <summary>
    /// How a card the page shows stands in its list against the part of the screen the mod's window covers (null: no
    /// window on the screen), and the scroll of its list that shows the whole card clear of the window, the nearest to
    /// the scroll it has (for <see cref="CardSight.Scroll"/>; otherwise the scroll it has). Woodcutting Rates'
    /// Native.Place, unchanged; a card in no scrolling list is in sight where it is, or not.
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
        if (best is { } nearest) { if (nearest == now) return CardSight.Clear; scroll = nearest; return CardSight.Scroll; }
        return wide && Reach(whole) != null ? CardSight.UnderWindow : CardSight.NoRoom;

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
    /// What is still missing of these groups (an animal's and its location's), each once, in the game's words: a skill
    /// level both ask for is named once, at the higher of the two (Thieving Overview's Native.Needs). "Locked" when the
    /// game puts none of them into words.
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

    /// <summary>A requirement in the game's own words, without its colours; "" when the game cannot put it into words.</summary>
    internal static string Words(Condition condition)
    {
        try { return StringUtils.StripBBCode(condition.GetDescription(false, false)).Trim(); }
        catch (Exception e) when (e is not (MissingMemberException or TypeLoadException)) { return ""; }
    }
}
