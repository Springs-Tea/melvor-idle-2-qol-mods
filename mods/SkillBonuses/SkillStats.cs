using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.ArtisanAction;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Entities.Agility;
using GamesByMalcs.Melvor2.Entities.Astrology;
using GamesByMalcs.Melvor2.Entities.Cooking;
using GamesByMalcs.Melvor2.Entities.Farming;
using GamesByMalcs.Melvor2.Entities.Firemaking;
using GamesByMalcs.Melvor2.Entities.Fishing;
using GamesByMalcs.Melvor2.Entities.Herblore;
using GamesByMalcs.Melvor2.Entities.Hunting;
using GamesByMalcs.Melvor2.Entities.Mining;
using GamesByMalcs.Melvor2.Entities.Ranching;
using GamesByMalcs.Melvor2.Entities.Thieving;
using GamesByMalcs.Melvor2.Entities.Woodcutting;
using GamesByMalcs.Melvor2.GameMenus;
using GamesByMalcs.Melvor2.GameMenus.Pages;
using GamesByMalcs.Melvor2.InfoLabels;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.Stats.Nodes;
using Godot;
using MelvorMods.Shared;

namespace SkillBonuses;

/// <summary>
/// One thing on a skill page whose bonuses the window can show: the recipe of the page's stats panel, or one
/// card (a tree, a rock, a thieving target, a farm plot) whose stats the game keeps in the card's hover tooltip.
/// </summary>
sealed class Choice(string name, Texture2D? icon, ModifierStatView panel, Node? card = null, object? model = null)
{
    public string Name { get; } = name;
    public Texture2D? Icon { get; } = icon;
    /// <summary>The game's own stats panel.</summary>
    public ModifierStatView Panel { get; } = panel;
    /// <summary>The card, when the panel is in its hover tooltip, and what the card is bound to.</summary>
    public Node? Card { get; } = card;
    public object? Model { get; } = model;
}

// Everything the mod reads from the game's skill pages, in one place. Private members are reached with
// [UnsafeAccessor]; one that is gone after a game update throws when it is first used, which stops the mod.
static class SkillStats
{
    // The loader's GameAccess.CurrentGame getter, applied to the view that is already found.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Game")]
    internal static extern Game? GameOf(GameView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrentPage")]
    internal static extern (GamePage, ActiveGamePage)? Page(GameView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Stats")]
    internal static extern ModifierStatNodes? Nodes(ModifierStatLabel label);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_StatNameLabel")]
    internal static extern Label? Name(ModifierStatLabel label);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Icon")]
    internal static extern TextureRect? Icon(ModifierStatLabel label);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ValueText")]
    internal static extern DoubleLabel? Number(ModifierStatLabel label);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_StatLabelScene")]
    internal static extern PackedScene? LabelScene(ModifierStatView view);

    // ---- the cards whose stats are in a hover tooltip -----------------------------------------------------------

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_WoodcuttingStats")]
    internal static extern WoodcuttingActionStatsView? Panel(WoodcuttingTreeSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_MiningStats")]
    internal static extern MiningActionStatsView? Panel(MiningRockSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_StatsView")]
    internal static extern ThievingActionStatsView? Panel(ThievingRecipeView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_StatsView")]
    internal static extern FarmingActionStatsView? Panel(FarmingPlotView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Tree")]
    static extern WoodcuttingTree? Tree(WoodcuttingTreeSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Rock")]
    static extern MiningRock? Rock(MiningRockSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Mining")]
    static extern MiningActionComponent? Mining(MiningRockSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Active")]
    static extern ActiveThievingRecipe? Active(ThievingRecipeView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Plot")]
    static extern ActiveFarmingPlot? Plot(FarmingPlotView card);

    internal static bool IsCard(Node node) => node is WoodcuttingTreeSelectView or MiningRockSelectView or ThievingRecipeView or FarmingPlotView;

    // A card that is not bound to anything yet (being built, or kept spare by the page) is no choice.
    static Choice? FromCard(Node node) => node switch
    {
        WoodcuttingTreeSelectView card when Panel(card) is { } panel && Tree(card) is { } tree =>
            new Choice(tree.Recipe.DisplayName.Text, IconOf(tree.Recipe), panel, card, tree),
        MiningRockSelectView card when Panel(card) is { } panel && Rock(card) is { } rock =>
            new Choice(rock.Recipe.DisplayName.Text, IconOf(rock.Recipe), panel, card, rock),
        ThievingRecipeView card when Panel(card) is { } panel && Active(card) is { } active =>
            new Choice(active.Recipe.DisplayName.Text, IconOf(active.Recipe), panel, card, active),
        // The panel of a plot shows the crop that grows there, or the one chosen for it. An empty plot has none.
        FarmingPlotView card when Panel(card) is { } panel && Plot(card) is { } plot && (plot.PlantedRecipe ?? plot.SelectedRecipe) is { } crop =>
            new Choice($"{plot.Plot.DisplayName.Text} / {crop.Recipe.DisplayName.Text}", IconOf(crop.Recipe), panel, card, plot),
        _ => null,
    };

    /// <summary>The card whose action the skill is doing now (or did last): the window opens on it.</summary>
    internal static bool InProgress(Choice choice) => choice.Model switch
    {
        WoodcuttingTree tree => ReferenceEquals(tree.Component?.CuttingTree, tree),
        ActiveThievingRecipe active => ReferenceEquals(active.Component?.CurrentRecipe, active),
        MiningRock rock => choice.Card is MiningRockSelectView card && ReferenceEquals(Mining(card)?.CurrentRock, rock),
        _ => false,
    };

    // ---- the pages with one stats panel for the recipe they show ------------------------------------------------

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LastSelectedRecipe")]
    static extern ActiveArtisanRecipe? Recipe(ArtisanActionStatsView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LastSelectedRecipe")]
    static extern ActiveCookingRecipe? Recipe(CookingActionStatsView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LastSelectedRecipe")]
    static extern ActiveFiremakingRecipe? Recipe(FiremakingActionStatsView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LastSelectedRecipe")]
    static extern ActiveHerbloreRecipe? Recipe(HerbloreActionStatsView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Recipe")]
    static extern WoodcuttingTree? Recipe(WoodcuttingActionStatsView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Recipe")]
    static extern MiningRock? Recipe(MiningActionStatsView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Recipe")]
    static extern ActiveFishingRecipe? Recipe(FishingActionStatsView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Recipe")]
    static extern ActiveAstrologyConstellation? Recipe(AstrologyActionStatsView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Recipe")]
    static extern ActiveHuntingAnimal? Recipe(HuntingActionStatsView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Active")]
    static extern ActiveThievingRecipe? Recipe(ThievingActionStatsView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Active")]
    static extern ActiveAgilityCoursePath? Path(AgilityActionStatsView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Plot")]
    static extern ActiveFarmingPlot? Plot(FarmingActionStatsView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Animal")]
    static extern ActiveRanchingAnimal? Animal(RanchingActionStatsView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Animal")]
    static extern ActiveRanchingAnimal? Animal(RanchingAnimalStatsView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Enclosure")]
    static extern ActiveRanchingEnclosure? Enclosure(RanchingEnclosureStatsView view);

    /// <summary>What a bound panel shows the stats of, by the game's own name and picture; null when it cannot be told.</summary>
    internal static (string Name, Texture2D? Icon)? Describe(ModifierStatView view, List<ModifierStatLabel> labels) => view switch
    {
        ArtisanActionStatsView v => Named(Recipe(v)?.Recipe),
        CookingActionStatsView v => Named(Recipe(v)?.Recipe),
        FiremakingActionStatsView v => Named(Recipe(v)?.Recipe),
        HerbloreActionStatsView v => Named(Recipe(v)?.Recipe),
        WoodcuttingActionStatsView v => Named(Recipe(v)?.Recipe),
        MiningActionStatsView v => Named(Recipe(v)?.Recipe),
        FishingActionStatsView v => Named(Recipe(v)?.Recipe),
        AstrologyActionStatsView v => Named(Recipe(v)?.Constellation),
        HuntingActionStatsView v => Named(Recipe(v)?.Animal),
        ThievingActionStatsView v => Named(Recipe(v)?.Recipe),
        AgilityActionStatsView v when Path(v) is { } path =>
            ($"{path.Course.Course.DisplayName.Text} / {path.Path.DisplayName.Text}", IconOf(path.Course.Course)),
        FarmingActionStatsView v when Plot(v) is { } plot => Crop(plot, labels),
        RanchingActionStatsView v when Animal(v) is { } animal => (animal.DisplayName.Text, animal.Icon),
        RanchingAnimalStatsView v when Animal(v) is { } animal => (animal.DisplayName.Text, animal.Icon),
        RanchingEnclosureStatsView v => Named(Enclosure(v)?.CurrentEnclosure),
        _ => null,
    };

    static (string, Texture2D?)? Named(INamedObject? thing) => thing == null ? null : (thing.DisplayName.Text, IconOf(thing));
    static Texture2D? IconOf(object? thing) => (thing as IDescribedObject)?.Icon;

    // A plot can have a crop growing and another one chosen. Which of the two the panel shows is told by its
    // stat nodes: they belong to one of the two crops.
    static readonly System.Reflection.PropertyInfo[] CropStats = typeof(FarmingActionStats).GetProperties()
        .Where(p => p.PropertyType == typeof(ModifierStatNodes)).ToArray();

    static (string, Texture2D?)? Crop(ActiveFarmingPlot plot, List<ModifierStatLabel> labels)
    {
        foreach (var crop in new[] { plot.PlantedRecipe, plot.SelectedRecipe })
        {
            if (crop?.Stats == null) continue;
            var nodes = CropStats.Select(p => p.GetValue(crop.Stats)).ToHashSet();
            if (labels.Any(label => Nodes(label) is { } node && nodes.Contains(node)))
                return ($"{plot.Plot.DisplayName.Text} / {crop.Recipe.DisplayName.Text}", IconOf(crop.Recipe));
        }
        return null;
    }

    static Choice? FromPanel(ModifierStatView panel, string fallback)
    {
        var labels = Labels(panel);
        if (labels.Count == 0) return null;   // not bound to a recipe yet
        var (name, icon) = Describe(panel, labels) ?? (fallback, null);
        return new Choice(name, icon, panel);
    }

    // ---- finding them ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Everything on the page whose bonuses can be shown, in the page's order. Only what is on the page: hidden
    /// tabs, categories and closed tooltips are skipped, except the cards' own panels. A panel that shows the
    /// same as one before it is left out; cards of the same name (plots of one kind, growing the same crop) are
    /// told apart by their place on the page.
    /// </summary>
    internal static List<Choice> Find(Node page, string fallback)
    {
        var found = new List<Choice>();
        var names = new HashSet<string>();
        void Walk(Node node)
        {
            if (node.IsQueuedForDeletion() || node is CanvasItem { Visible: false }) return;
            if (IsCard(node))
            {
                if (!node.IsNodeReady() || FromCard(node) is not { } card) return;
                string name = card.Name;
                for (int n = 2; !names.Add(name); n++) name = $"{card.Name} ({n})";
                found.Add(name == card.Name ? card : new Choice(name, card.Icon, card.Panel, card.Card, card.Model));
            }
            else if (node is ModifierStatView panel) { if (FromPanel(panel, fallback) is { } shown && names.Add(shown.Name)) found.Add(shown); }
            else for (int i = 0; i < node.GetChildCount(); i++) Walk(node.GetChild(i));
        }
        Walk(page);
        return found;
    }

    /// <summary>The rows of a panel that are bound to a stat, in the panel's order.</summary>
    internal static List<ModifierStatLabel> Labels(ModifierStatView panel)
    {
        var labels = new List<ModifierStatLabel>();
        // The panel keeps spare rows of its own, hidden. Only the rows' own visibility counts: the panel itself
        // may be inside a closed tooltip.
        void Collect(Node node)
        {
            for (int i = 0; i < node.GetChildCount(); i++)
            {
                Node child = node.GetChild(i);
                if (child.IsQueuedForDeletion() || child is CanvasItem { Visible: false }) continue;
                if (child is ModifierStatLabel label) { if (Nodes(label) != null) labels.Add(label); }
                else Collect(child);
            }
        }
        Collect(panel);
        return labels;
    }
}

/// <summary>
/// The game keeps the stats of a tree, a rock, a thieving target or a farm plot in the card's hover tooltip, and
/// may fill that panel only when the tooltip is first shown. Until then the window reads a copy of the same
/// panel, bound to the same tree or plot the way the card binds its own: the game's own view still lists, names
/// and formats the stats. The copy is never shown, takes no input, and is let go of with the window, the way
/// the Item Encyclopedia lets go of the game's item panel (EncyclopediaView.Release).
/// </summary>
sealed class PanelCopy
{
    public Choice For { get; }
    public ModifierStatView View { get; }

    PanelCopy(Choice choice, ModifierStatView view) { For = choice; View = view; }

    public static PanelCopy Make(Choice choice, Node holder)
    {
        ModifierStatView source = choice.Panel;
        ModifierStatView? view = null;
        if (source.SceneFilePath is { Length: > 0 } path && GameStyle.Load<PackedScene>(path) is { } scene)
            view = scene.Instantiate() as ModifierStatView;
        // Without its signals: the copy must not reach anything the game's panel is connected to.
        view ??= source.Duplicate((int)(Node.DuplicateFlags.Scripts | Node.DuplicateFlags.UseInstantiation)) as ModifierStatView;
        if (view == null) throw new InvalidOperationException($"{source.GetType().Name} could not be copied");
        holder.AddChild(view);
        bool bound = (view, choice.Model) switch
        {
            (IView<WoodcuttingTree> v, WoodcuttingTree tree) => Bind(v, tree),
            (IView<MiningRock> v, MiningRock rock) => Bind(v, rock),
            (IView<ActiveThievingRecipe> v, ActiveThievingRecipe active) => Bind(v, active),
            (IView<ActiveFarmingPlot> v, ActiveFarmingPlot plot) => Bind(v, plot),
            _ => false,
        };
        if (!bound)
        {
            view.QueueFree();
            throw new InvalidOperationException($"{view.GetType().Name} does not take a {choice.Model?.GetType().Name}");
        }
        // A view that only remembers what it is bound to makes its rows when it is updated.
        if (SkillStats.Labels(view).Count == 0) ((IView)view).Update();
        return new PanelCopy(choice, view);
    }

    static bool Bind<T>(IView<T> view, T model) { view.Bind(model); return true; }

    /// <summary>Unbinds the copy and every row of it from the game's stats, and frees it.</summary>
    public void Release(Action<string> log)
    {
        if (!GodotObject.IsInstanceValid(View)) return;
        try
        {
            // UnhandleEvents first: removing a handler that was never added does nothing, one that was must go.
            ((IView)View).UnhandleEvents();
            ((IView)View).Unbind();
            foreach (ModifierStatLabel label in View.FindChildren("*", "", true, false).OfType<ModifierStatLabel>())
            {
                if (SkillStats.Nodes(label) == null) continue;
                label.UnhandleEvents();
                label.Unbind();
            }
        }
        catch (Exception e) { log("A copy of a card's stats panel could not be unbound: " + e.Message); }
        View.QueueFree();
    }
}
