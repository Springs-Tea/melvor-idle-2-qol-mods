using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2.Components.CraftingAction;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Utils;

namespace ItemSources;

// Poll just the inputs this plan read, without subscribing to the character's events.
sealed class MakeInputs
{
    readonly List<Func<bool>> _checks = new();
    public bool Changed => _checks.Any(check => !check());

    public T Read<T>(Func<T> read)
    {
        T before = read();
        _checks.Add(() => EqualityComparer<T>.Default.Equals(before, read()));
        return before;
    }
}

sealed record MakeCost(INamespacedObject Material, long Quantity);

/// <summary>A line under a step: a few words, or a requirement group not met yet, for the game's own view of it.</summary>
sealed record MakeNote(string Text, ConditionGroup? Requirements = null);

// FromBar: the time would come from the bottom bar's rate for the item (gathered, dropped, stolen...).
sealed record MakeStep(string Group, IReadOnlyList<MakeNote> Notes, ICraftingRecipe? Recipe = null, long Yield = 0,
    IReadOnlyList<MakeCost>? Costs = null, double? Seconds = null, int Bottles = 1, double SetupSeconds = 0,
    double? FooterRate = null, bool FromBar = false, bool Locked = false, int Choices = 0)
{
    public double? TimeFor(long actions) => actions * Seconds
        + MakePlan.Batches(actions, Bottles) * SetupSeconds;
}

sealed class MakePlan
{
    // What a cell shows where there is nothing: as the Combat Simulator's table writes it.
    public const string Nothing = "-";

    public sealed class Row(INamespacedObject material, MakeStep step, long bank)
    {
        public readonly INamespacedObject Material = material;
        public readonly MakeStep Step = step;
        public readonly long Bank = bank;
        public string Name => Material is Item i ? i.DisplayName.GetPluralText(1) : ((Currency)Material).DisplayName.Text;
        public bool Complete, Cycle;
        // The row whose recipe gives this one too: the actions (and their time) are counted there.
        public Row? SharedWith;
        public long Needed, Left, Actions;
        public double? Seconds;

        /// <summary>The bank already has enough: nothing is left to make or get.</summary>
        public bool Covered => Left == 0;

        // No time where nothing is left, where the shared recipe's row has it, or where nothing gives one; "No
        // rate" only where the bottom bar could give one and has none for the item right now.
        public string Time => Covered ? Nothing
            : Seconds is { } s && s > 0 && s < TimeSpan.MaxValue.TotalSeconds
                ? TimeFormatUtils.FormatMostSignificantUnits(TimeSpan.FromSeconds(s), 2, TimeUnits.Seconds, true, true, false)
            : Seconds == null && Step.FromBar ? "No rate" : Nothing;
    }

    readonly List<Row> _rows = new();
    public IEnumerable<Row> Rows => _rows.Where(r => r.Needed > 0);

    // Ceiling division without overflowing on the largest wallet balance.
    internal static long Batches(long amount, long yield) => amount / yield + (amount % yield == 0 ? 0 : 1);

    public MakePlan(MakeRecipes recipes, Item target, int quantity, MakeInputs inputs)
    {
        var seen = new Dictionary<INamespacedObject, Row>();
        var selected = recipes.Selected(inputs);
        Row Visit(INamespacedObject material)
        {
            if (seen.TryGetValue(material, out var known)) return known;
            var row = new Row(material, recipes.Read(material, selected, inputs), inputs.Read(() => recipes.InBank(material)));
            seen.Add(material, row);
            if (row.Step.Costs != null && (!ReferenceEquals(material, target) || quantity > row.Bank))
                foreach (var cost in row.Step.Costs)
                    if (!Visit(cost.Material).Complete) row.Cycle = true;
            row.Complete = true;
            _rows.Add(row);
            return row;
        }

        Visit(target).Needed = quantity;
        // Per recipe: the actions counted so far, and the row that counted them first.
        var batches = new Dictionary<ICraftingRecipe, (long Actions, Row First)>();
        // Consumers first: all branches contribute demand before a material spends its bank stock.
        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            Row row = _rows[i];
            if (row.Needed == 0) continue;
            if (row.Material is Item && row.Needed > int.MaxValue) throw new OverflowException();
            row.Left = Math.Max(0, row.Needed - row.Bank);
            if (row.Left == 0) { row.Seconds = 0; continue; }
            if (row.Cycle || row.Step.Yield == 0) continue;

            row.Actions = Batches(row.Left, row.Step.Yield);
            long previous = 0;
            if (row.Step.Costs != null && row.Step.Recipe is { } recipe)
            {
                Row first = row;
                if (batches.TryGetValue(recipe, out var batch)) (previous, first, row.SharedWith) = (batch.Actions, batch.First, batch.First);
                long extra = Math.Max(0, row.Actions - previous);
                batches[recipe] = (Math.Max(row.Actions, previous), first);
                foreach (var cost in row.Step.Costs)
                    checked { seen[cost.Material].Needed += extra * cost.Quantity; }
            }
            row.Seconds = row.Actions <= previous ? 0 : row.Step.TimeFor(row.Actions) - row.Step.TimeFor(previous);
            if (row.Seconds >= TimeSpan.MaxValue.TotalSeconds) throw new OverflowException();
        }
    }
}
