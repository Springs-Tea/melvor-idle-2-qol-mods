using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2.Components.ArtisanAction;
using GamesByMalcs.Melvor2.Components.CraftingAction;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Cooking;
using GamesByMalcs.Melvor2.Entities.Firemaking;
using GamesByMalcs.Melvor2.Entities.Herblore;
using GamesByMalcs.Melvor2.Stats.Nodes;

namespace RecipeRates;

/// <summary>
/// What one hour of making a recipe gives and costs, worked out from the game's own numbers before anything
/// is made. Those numbers already contain every bonus (equipment, potions, mastery...), so the estimate
/// moves with them. Chances (doubling, preservation, skipped steps, failing) count as their long-run average.
/// </summary>
sealed class Rates
{
    /// <summary>
    /// One thing that is made or used. <see cref="PerReward"/> is the long-run average, positive for gains;
    /// <see cref="Needed"/> is how many must be in the bank to start one (0 for things that are made).
    /// </summary>
    public sealed record Line(string Name, double PerReward, long Needed, Item? Item, Currency? Currency);

    /// <summary>Seconds per "reward": the moment XP, products and costs are applied.</summary>
    public double SecondsPerReward;
    public double RewardsPerHour => 3600 / SecondsPerReward;
    public double XpPerHour;
    public readonly List<Line> Made = new(), Used = new();
    /// <summary>What the products of one hour sell for, in the game's main currency. 0 when unknown.</summary>
    public double SellValuePerHour;
    public string SellCurrency = "";
    /// <summary>The chance that an attempt succeeds, when the skill can fail (Cooking); otherwise 1.</summary>
    public double Success = 1;

    // The game keeps intervals in milliseconds (a 2.00s step is 2000) and chances as fractions (0.1 is 10%).
    const double Ms = 1000;
    const string Gold = "MelvorBase:GoldPieces";

    public static Rates? For(IActiveCraftingRecipe active)
    {
        ICraftingActionStats stats = active.Stats;
        var r = new Rates();
        double doubling = 0;
        double kept = Chance(stats.PreservationChance), keptOnFail = kept;

        switch (stats)
        {
            // Herblore: prepare, heat and stir once, then bottle N times. XP, ingredients and potions are
            // all applied per bottling step, so one reward carries its share of the three first stages.
            case HerbloreActionStats h when active is ActiveHerbloreRecipe potion:
                int bottles = Math.Max(1, potion.MaxBottledCount);
                r.SecondsPerReward = ((h.PreparingInterval.Value + h.HeatingInterval.Value + h.StirringInterval.Value) / bottles + h.BottlingInterval.Value) / Ms;
                doubling = Chance(h.DoublingChance);
                break;

            // Smithing, Crafting, Fletching, Runecrafting, Beastcrafting: an item takes several steps of one
            // interval each, and a step can be skipped by chance.
            case ArtisanActionStats a:
                r.SecondsPerReward = a.Interval.Value * Math.Max(1, a.Steps.Value) * (1 - Chance(a.SkipStepChance)) / Ms;
                doubling = Chance(a.DoublingChance);
                break;

            // Cooking can fail: a failed attempt takes the time and (unless preserved) the ingredients, and
            // gives neither food nor XP.
            case CookingActionStats c:
                r.SecondsPerReward = c.Interval.Value / Ms;
                r.Success = Math.Clamp(c.SuccessChance.Value, 0, 1);
                doubling = Chance(c.DoublingChance);
                keptOnFail = Chance(c.PreservationChanceOnFail);
                break;

            case FiremakingActionStats f:
                r.SecondsPerReward = f.Interval.Value / Ms;
                doubling = Chance(f.DoublingChance);
                if (f.CurrencyReward?.Value is { Currency: { } paid, Quantity: > 0 } reward)
                    r.Made.Add(new Line(paid.DisplayName.Text, reward.Quantity, 0, null, paid));
                break;

            default:
                r.SecondsPerReward = stats.Interval.Value / Ms;
                break;
        }
        if (!(r.SecondsPerReward > 0.01) || double.IsInfinity(r.SecondsPerReward)) return null;

        r.XpPerHour = stats.Experience.XpToAdd.Value * r.Success * r.RewardsPerHour;

        foreach (var node in stats.Products?.Values ?? [])   // some skills have no item product
        {
            if (node.Value is not { Item: { } item } product) continue;
            double amount = product.Quantity * Math.Clamp(product.Chance, 0, 1) * (1 + doubling) * r.Success;
            if (amount <= 0) continue;
            r.Made.Add(new Line(item.DisplayName.GetPluralText(2), amount, 0, item, null));
            if (item.SellsFor is { Currency: { Id: Gold } gold, Quantity: > 0 } price)
            {
                r.SellValuePerHour += amount * price.Quantity * r.RewardsPerHour;
                r.SellCurrency = gold.DisplayNameShort?.Text is { Length: > 0 } shortName ? shortName : gold.DisplayName.Text;
            }
        }

        // Ingredients are kept by chance; a failed attempt has a chance of its own.
        double spent = r.Success * (1 - kept) + (1 - r.Success) * (1 - keptOnFail);
        foreach (var node in stats.ItemCost?.Values ?? [])
            if (node.Value is { Item: { } item } cost && cost.Quantity > 0)
                r.Used.Add(new Line(item.DisplayName.GetPluralText(2), -cost.Quantity * spent, cost.Quantity, item, null));
        foreach (var node in stats.CurrencyCost?.Values ?? [])
            if (node.Value is { Currency: { } currency } cost && cost.Quantity > 0)
                r.Used.Add(new Line(currency.DisplayName.Text, -cost.Quantity * spent, cost.Quantity, null, currency));
        return r;
    }

    /// <summary>
    /// How many seconds the bank's stock lasts, or null when the recipe uses nothing. 0 means there is not
    /// enough to start a single one.
    /// </summary>
    public double? SecondsOfMaterials(BankEntity bank)
    {
        double? rewards = null;
        foreach (Line line in Used)
        {
            double have = line.Item != null ? bank.Inventory.GetQuantity(line.Item)
                        : line.Currency != null ? bank.Wallet.GetQuantity(line.Currency) : 0;
            // Starting one needs the whole amount; over time, preservation stretches the stock.
            double possible = have < line.Needed ? 0 : have / Math.Max(-line.PerReward, 1e-9);
            rewards = rewards == null ? possible : Math.Min(rewards.Value, possible);
        }
        return rewards * SecondsPerReward;
    }

    static double Chance(ModifierStatNodes? nodes) => nodes == null ? 0 : Math.Clamp(nodes.Total.Value, 0, 1);

    /// <summary>12 -> "12", 5760 -> "5,760", 123456 -> "123.5K".</summary>
    public static string Number(double value) => Math.Abs(value) switch
    {
        >= 1_000_000 => (value / 1_000_000).ToString("0.##") + "M",
        >= 100_000 => (value / 1000).ToString("0.#") + "K",
        >= 100 => value.ToString("N0"),
        _ => value.ToString("0.#"),
    };

    /// <summary>The shortest readable form: 5760 -> "5.8K".</summary>
    public static string Compact(double value) => Math.Abs(value) switch
    {
        >= 1_000_000 => (value / 1_000_000).ToString("0.#") + "M",
        >= 10_000 => (value / 1000).ToString("0") + "K",
        >= 1000 => (value / 1000).ToString("0.#") + "K",
        _ => value.ToString("0"),
    };

    public static string Duration(double seconds)
    {
        if (seconds >= 100 * 3600) return $"{seconds / 3600:N0}h";
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }
}
