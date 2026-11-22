using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.ArtisanAction;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Components.CraftingAction;
using GamesByMalcs.Melvor2.Components.Milestones;
using GamesByMalcs.Melvor2.Components.Trackers;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Agility;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Entities.Cooking;
using GamesByMalcs.Melvor2.Entities.Farming;
using GamesByMalcs.Melvor2.Entities.Firemaking;
using GamesByMalcs.Melvor2.Entities.Fishing;
using GamesByMalcs.Melvor2.Entities.Herblore;
using GamesByMalcs.Melvor2.Entities.Hunting;
using GamesByMalcs.Melvor2.Entities.Mining;
using GamesByMalcs.Melvor2.Entities.Thieving;
using GamesByMalcs.Melvor2.Entities.Woodcutting;
using GamesByMalcs.Melvor2.Stats.Nodes;
using Godot;
using MelvorMods.Shared;

namespace ItemSources;

sealed class MakeRecipes
{
    readonly ItemIndex _index;
    readonly ItemLines _lines;
    readonly BankEntity _bank;
    readonly Dictionary<ICraftingRecipe, IActiveCraftingRecipe> _active = new();
    readonly List<Func<ICraftingRecipe?>> _selected = new();
    readonly Dictionary<object, TrackerComponent> _trackers = new();
    readonly Dictionary<(TrackerComponent, Item), double> _bar = new();
    Control? _footer;
    bool _readBar;
    readonly TrackerComponent? _combat;
    readonly FarmingPassiveComponent? _farming;

    public MakeRecipes(Game game, ItemIndex index, ItemLines lines)
    {
        _index = index;
        _lines = lines;
        _bank = game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? bank) && bank is BankEntity b
            ? b : throw new InvalidOperationException("the character's bank was not found");
        // The active recipes are already bound by the game, as in RecipeRatesMod.MapRecipes.
        foreach (GameEntity entity in game.Entities.GetAllObjects())
        {
            if (entity is CombatEntity combat) _combat = combat.Tracker;
            foreach (var component in entity.Components.Values)
            {
                switch (component)
                {
                    case ArtisanActionComponent a: Add(a.ActiveRecipes.Values, () => a.SelectedRecipe?.Recipe); break;
                    case CookingActionComponent c: Add(c.ActiveRecipes.Values, () => c.SelectedRecipe?.Recipe); break;
                    case HerbloreActionComponent h: Add(h.ActiveRecipes.Values, () => h.SelectedRecipe?.Recipe); break;
                    case FiremakingActionComponent f: Add(f.ActiveRecipes.Values, () => f.SelectedRecipe?.Recipe); break;
                    case FarmingGrainMillComponent m: Add(m.ActiveRecipes.Values, () => m.SelectedRecipe?.Recipe); break;
                    case FarmingPassiveComponent farm: _farming = farm; break;
                    case MiningActionComponent mine: Track(mine.Rocks.Keys, () => mine.CurrentRock?.Recipe); break;
                    case WoodcuttingActionComponent wood: Track(wood.Trees.Keys, () => wood.CuttingTree?.Recipe); break;
                    case FishingActionComponent fish: Track(fish.Recipes.Keys, () => fish.CurrentRecipe?.Recipe); break;
                    case HuntingActionComponent hunt: Track(hunt.Animals.Keys, null); break;
                    case AgilityActionComponent agility: Track(agility.Courses.Keys, null); break;
                    case ThievingActionComponent thief: Track(thief.ActiveRecipes.Keys, null); break;
                }
            }

            void Track(IEnumerable<object> sources, Func<ICraftingRecipe?>? selection)
            {
                if (entity is ITrackedEntity tracked)
                    foreach (var source in sources) _trackers[source] = tracked.Tracker;
                if (selection != null) _selected.Add(selection);
            }
        }
    }

    void Add(IEnumerable<IActiveCraftingRecipe> recipes, Func<ICraftingRecipe?> selected)
    {
        foreach (var active in recipes) _active[active.Recipe] = active;
        _selected.Add(selected);
    }

    public long InBank(INamespacedObject material) => material is Item item
        ? _bank.Inventory.GetQuantity(item) : _bank.Wallet.GetQuantity((Currency)material);

    // Make is offered for what a skill makes from materials. Ores, logs and fish are gathered: they come in as
    // steps of what is made from them, and have no Make of their own.
    public static bool MakesItem(ItemRef source) => source.Owner is ArtisanRecipe or HerbloreRecipe or FiremakingRecipe
        or FarmingRecipe or GrainMillRecipe;

    // A recipe a step can follow: what a skill makes, and what a gathering skill gives (timed by the bottom bar).
    static bool IsRecipe(ItemRef source) => MakesItem(source) || source.Owner is WoodcuttingRecipe or MiningRecipe or FishingRecipe;

    public HashSet<ICraftingRecipe> Selected(MakeInputs inputs)
    {
        var selected = _selected.Select(f => inputs.Read(f)).OfType<ICraftingRecipe>().ToHashSet();
        if (_farming != null)
            foreach (var plot in _farming.ActivePlots.Values)
                if (inputs.Read(() => plot.SelectedRecipe) is { } crop) selected.Add(crop.Recipe);
        return selected;
    }

    public MakeStep Read(INamespacedObject material, HashSet<ICraftingRecipe> selected, MakeInputs inputs)
    {
        if (material is not Item item) return new("Currency", []);
        var sources = _index.For(item)?.Sources ?? [];
        var notes = new List<MakeNote>();
        // Of several recipes: the one selected in its skill, else one that always gives the item (a chance
        // product cannot be planned), in the index's order.
        var choices = sources.Where(IsRecipe).OrderByDescending(r => r.Chance >= 1).Select(r => r.Owner)
            .OfType<ICraftingRecipe>().Distinct().ToList();
        var pick = choices.FirstOrDefault(selected.Contains) ?? choices.FirstOrDefault();
        if (pick == null)
        {
            var places = ItemSourcesMod.SortedLines(sources, _lines);
            int shown = 0, elsewhere = places.Count;
            foreach (var group in places.GroupBy(l => l.Group))
            {
                int room = Math.Min(ItemSourcesMod.MaxPerGroup, ItemSourcesMod.MaxLines - shown);
                if (room == 0) break;
                elsewhere -= group.Count();
                foreach (var place in group.Take(room))
                {
                    notes.Add(new(group.Key + ": " + place.Name));
                    if (place.Ref.Owner is ThievingRecipe t)
                        notes.AddRange(Unmet(inputs, t.Requirements, t.AccessRequirements, t.Category?.Requirements));
                    shown++;
                }
                if (group.Count() > room) notes.Add(new($"and {group.Count() - room:N0} more"));
            }
            if (elsewhere > 0) notes.Add(new($"and {elsewhere:N0} more elsewhere"));
            // In the words of Item Sources' own "Found in" list.
            notes.Insert(0, new(places.Count == 0 ? "Nothing in the game's data gives this item." : "Get this item separately."));
            return FromFooter(places.FirstOrDefault()?.Group ?? "Items", sources.Select(r => r.Owner));
        }

        string skill = _lines.SkillOf(pick);
        bool gathered = pick is WoodcuttingRecipe or MiningRecipe or FishingRecipe;
        // The bottom bar's rate for a gathered item is the same whichever rock, tree or spot gives it.
        if (choices.Count > 1 && !gathered)
            notes.Add(new(selected.Contains(pick) ? $"The recipe selected in {skill}, one of {choices.Count:N0}."
                : $"One of {choices.Count:N0} recipes. The plan follows the one you select in " +
                  string.Join(" or ", choices.Select(_lines.SkillOf).Distinct()) + "."));
        var category = (pick as IMilestone)?.Category;
        var unmet = Unmet(inputs, pick.Requirements, pick.DisplayRequirements, category?.Requirements, category?.DisplayRequirements);
        notes.AddRange(unmet);
        bool locked = unmet.Count > 0;
        if (gathered) return FromFooter(skill, [pick]) with { Locked = locked };

        MakeStep Step(long yield = 0, List<MakeCost>? costs = null, double? seconds = null, int bottles = 1, double setup = 0) =>
            new(skill, notes, pick, yield, costs, seconds, bottles, setup, Locked: locked, Choices: choices.Count);

        ICraftingActionStats? stats = null;
        _active.TryGetValue(pick, out var active);
        if (pick is FarmingRecipe farm && _farming != null)
        {
            // Never create preview stats: read only the stats the plot already owns.
            foreach (var plot in _farming.ActivePlots.Values.OrderByDescending(p => p.SelectedRecipe?.Recipe == farm))
            {
                var crop = plot.SelectedRecipe?.Recipe == farm ? plot.SelectedRecipe : inputs.Read(() => plot.PlantedRecipe);
                if (crop?.Recipe != farm) continue;
                stats = crop.Stats;
                // One plot's harvest and growing time, planting after planting: more plots of it are faster.
                notes.Add(new(inputs.Read(() => crop.Stats.Regrows.Value) > 0
                    ? "Time for one plot, one harvest after another. Regrowth is not counted."
                    : "Time for one plot, one harvest after another."));
                break;
            }
        }
        else stats = active?.Stats;
        if (stats == null)
        {
            notes.Add(new(pick is FarmingRecipe ? "No plot has this crop. Select it on a plot to plan it."
                : "This recipe is not planned. Get this item separately."));
            return Step();
        }
        if (stats is CookingActionStats cooking && inputs.Read(() => cooking.SuccessChance.Value) < 1)
        {
            notes.Add(new("Cooking can fail. There is no guaranteed quantity to plan."));
            return Step();
        }

        long yield = 0;
        if (stats is FarmingActionStats or GrainMillStats)
        {
            var product = inputs.Read(() => stats.Product?.Value);
            if (product?.Item == item) yield = product.Quantity;
        }
        else
            foreach (var product in Values(stats.Products, inputs))
                if (product.Item == item && product.Chance >= 1) yield = checked(yield + product.Quantity);
        if (yield <= 0)
        {
            notes.Add(new("This recipe has no guaranteed output for this item."));
            return Step();
        }

        if (stats.ItemCost == null && stats.CurrencyCost == null)
        {
            notes.Add(new("Recipe costs are unavailable. Get this item separately."));
            return Step();
        }
        var costs = new List<MakeCost>();
        foreach (var cost in Values(stats.ItemCost, inputs))
            if (cost.Quantity > 0) costs.Add(new(cost.Item, cost.Quantity));
        foreach (var cost in Values(stats.CurrencyCost, inputs))
            if (cost.Quantity > 0) costs.Add(new(cost.Currency, cost.Quantity));

        if (costs.Count == 0) notes.Add(new("No materials are needed for this recipe."));

        const double ms = TimeSpan.MillisecondsPerSecond;
        if (stats is HerbloreActionStats h && active is ActiveHerbloreRecipe potion)
        {
            // Preparing, heating and stirring once for every batch of bottles, then each bottle, as the game's
            // own herblore values say (Recipe Rates' Rates.For reads them the same way).
            double setup = inputs.Read(() => h.PreparingInterval.Value + h.HeatingInterval.Value + h.StirringInterval.Value);
            int bottles = inputs.Read(() => potion.MaxBottledCount);
            if (bottles <= 0) return Step(yield, costs);
            return Step(yield, costs, inputs.Read(() => h.BottlingInterval.Value) / ms, bottles, setup / ms);
        }
        double interval = inputs.Read(() => stats.Interval.Value);
        if (stats is ArtisanActionStats artisan) interval *= inputs.Read(() => artisan.Steps.Value);
        return Step(yield, costs, interval > 0 ? interval / ms : null);

        MakeStep FromFooter(string group, IEnumerable<object> owners)
        {
            var trackers = owners.Select(o => o is Enemy or Dungeon ? _combat : _trackers.GetValueOrDefault(o))
                .OfType<TrackerComponent>().Distinct().ToArray();
            double? rate = trackers.Length == 0 ? null : inputs.Read(() => FooterRate(trackers, item));
            return new(group, notes, pick, 1, Seconds: rate > 0 ? TimeSpan.SecondsPerHour / rate : null,
                FooterRate: rate, FromBar: trackers.Length > 0, Choices: choices.Count);
        }
    }

    // Quantity records are immutable to readers. The game can replace a value or change the number of nodes.
    static IEnumerable<T> Values<T>(ValuesStatNode<T>? nodes, MakeInputs inputs)
    {
        if (nodes == null) yield break;
        int count = inputs.Read(() => nodes.Values.Count);
        for (int i = 0; i < count; i++)
        {
            int slot = i;
            if (inputs.Read(() => nodes.Values[slot].Value) is { } value) yield return value;
        }
    }

    // The requirement groups the character does not meet yet, each shown in the game's own view of it. A group
    // that only repeats what an earlier one says (a recipe and its category asking for the same level) is left
    // out. Only the groups' public members are read: how a group keeps its "or" alternatives is the view's.
    static List<MakeNote> Unmet(MakeInputs inputs, params ConditionGroup?[] groups)
    {
        var unmet = new List<MakeNote>();
        var said = new HashSet<string>();
        foreach (var group in groups.OfType<ConditionGroup>().Distinct())
        {
            if (inputs.Read(() => group.AreMet)) continue;
            var words = Words(group);
            if (words.Count > 0 && words.All(said.Contains)) continue;
            said.UnionWith(words);
            unmet.Add(new("", group));
        }
        return unmet;
    }

    // What a group's conditions say, without colours: to compare groups, and for the development log. A group
    // that cannot be read this way is still shown; the game's view reads it by itself.
    internal static List<string> Words(ConditionGroup group)
    {
        try
        {
            return (group.Conditions ?? Enumerable.Empty<Condition>()).Where(c => c is { IsOr: false })
                .Select(c => c.GetDescription(false, false)).ToList();
        }
        catch (Exception) { return []; }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Tracker")]
    static extern TrackerComponent? BoundTracker(BaseTrackerRateView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Item")]
    static extern Item? BoundItem(GainLossInfoView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_PerHourRate")]
    static extern double PerHour(GainLossInfoView view);

    // Read the game's finished rate, after its current-action filter and period selection (including Combat).
    // Only inspect the footer while a plan is open. Hidden, pooled and unbound rows cannot supply a rate.
    public void BeginRead(Control? footer)
    {
        _footer = footer;
        _readBar = false;
    }

    double? FooterRate(TrackerComponent[] trackers, Item item)
    {
        if (!_readBar)
        {
            _bar.Clear();
            if (_footer != null) Walk(_footer, null);
            _readBar = true;
        }
        foreach (var tracker in trackers)
            if (_bar.TryGetValue((tracker, item), out double rate) && double.IsFinite(rate)) return rate;
        return null;

        void Walk(Node node, TrackerComponent? tracker)
        {
            if (node is CanvasItem c && !c.IsVisibleInTree()) return;
            if (node is TrackerBankView bankView) tracker = BoundTracker(bankView);
            if (node is GainLossInfoView gain)
            {
                if (tracker != null && BoundItem(gain) is { } item) _bar[(tracker, item)] = PerHour(gain);
                return;
            }
            foreach (Node child in node.GetChildren()) Walk(child, tracker);
        }
    }
}
