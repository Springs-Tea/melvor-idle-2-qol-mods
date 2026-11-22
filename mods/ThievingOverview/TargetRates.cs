using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2.Components.CombatFood;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Data.DropTables;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Thieving;
using GamesByMalcs.Melvor2.Random;
using GamesByMalcs.Melvor2.Stats.Nodes;
using Godot;

namespace ThievingOverview;

/// <summary>One item a target gives, from its common or its rare rewards, per hour of stealing there.</summary>
/// <param name="Table">"Common" or "Rare", as View Possible Rewards shows them apart.</param>
/// <param name="GoldEach">What one sells for, in gold; 0 when it does not sell for gold.</param>
sealed record Drop(Item Item, string Table, double PerHour, double GoldEach);

/// <summary>
/// What the game's own labels prove about the units of its Thieving numbers. A number whose unit is not proven
/// is not shown (the numbers made from it show "-"), rather than shown a hundred times off.
/// </summary>
sealed class Units
{
    /// <summary>The success chance is a share of 1: a card on the Thieving screen writes it through a percent format.</summary>
    public bool SuccessProven;
    /// <summary>
    /// The common, rare and coin chances are shares of 1: View Possible Rewards writes all three through percent
    /// formats, and while it is open it writes the very numbers the game keeps for the target.
    /// </summary>
    public bool LootProven;
    public string SuccessWhy = "the Thieving screen has not shown a success chance yet", LootWhy = "View Possible Rewards was not found";
}

/// <summary>
/// One table of View Possible Rewards as the game showed it for a target, with the fortune the target had then: each
/// row's share of its table and its quantities, after the game's own changes for the fortune. Kept for the session,
/// so the window counts the same chances as the game's window shows while the fortune stays the same.
/// </summary>
sealed class RewardsReading
{
    public sealed record Row(Item Item, double Share, int Min, int Max);
    public double Fortune;
    public readonly Dictionary<string, List<Row>> Tables = new();
}

/// <summary>
/// The food the character has chosen for Thieving, and where auto eat steps in. Read once per look at the
/// targets: the same for all of them.
/// </summary>
sealed class ThievingFood
{
    public Item? Item;
    public Texture2D? Icon;
    /// <summary>The HP one piece heals when auto eat eats it (the game's AutoEatHealing).</summary>
    public int Healing;
    /// <summary>The chance that a piece eaten is not used up (the game's FoodPreservationChance).</summary>
    public double Preserved;
    public long InBank;
    /// <summary>
    /// The HP at which auto eat steps in: 0 when it cannot help (no food selected, none in the bank, no auto eat),
    /// null when it cannot be told.
    /// </summary>
    public double? AutoEatHp;
    public int Hp, MaxHp;
    public bool Selected => Item != null;

    /// <summary>The food's name in the game's own singular or plural for this many.</summary>
    public string Name(long count) => Item?.DisplayName.GetPluralText((int)Math.Clamp(count, 0, int.MaxValue)) ?? "";

    /// <summary>
    /// Read as the Combat Simulator reads it for a fight (CombatSim's CardReadings.ReadAutoEat): auto eat steps in
    /// when the share of HP left falls to the food's AutoEatThreshold. The game keeps the share of HP left as
    /// Health.Percent; the same share of these HP is the point in HP.
    /// </summary>
    public static ThievingFood Read(ThievingEntity thieving, BankEntity? bank)
    {
        var food = new ThievingFood();
        var health = thieving.Health;
        food.Hp = health.Current;
        food.MaxHp = health.Max;
        FoodComponent component = thieving.Food;
        if (component.SelectedFood is not { Item: { } item } || component.Stats is not { } stats)
        {
            food.AutoEatHp = 0;
            return food;
        }
        food.Item = item;
        food.Icon = item.Icon;
        food.Healing = Math.Max(0, stats.AutoEatHealing.Value);
        food.Preserved = Chance(stats.FoodPreservationChance);
        food.InBank = bank?.Inventory.GetQuantity(item) ?? 0;
        if (food.InBank <= 0 || food.Healing <= 0 || stats.AutoEatThreshold.Value <= 0) food.AutoEatHp = 0;
        else if (health.Current > 0 && health.Percent > 0)
        {
            double point = stats.AutoEatThreshold.Value * health.Current / health.Percent;
            // Not on the scale of Health.Percent after all: no point rather than a wrong one.
            food.AutoEatHp = point > health.Max ? null : point;
        }
        return food;
    }

    // A chance of the game's modifiers is a share of 1 (RecipeRates' Rates.Chance).
    internal static double Chance(ModifierStatNodes? nodes) => nodes == null ? 0 : Math.Clamp(nodes.Total.Value, 0, 1);
}

/// <summary>
/// What one hour of stealing from a target gives, costs and risks, worked out from the numbers the game keeps for
/// the character (ActiveThievingRecipe.Stats, ThievingActionStats): they already contain the level, stealth and
/// perception, equipment, distractions and every other bonus, so the hour moves with them. Chances count as their
/// long-run average, as Recipe Rates counts them. No chance or formula of the game's is made again here: only the
/// division of an hour by the time a try takes, and sums and products of the game's chances. What the game's numbers
/// do not tell is left out ("-"), with the reason in <see cref="ItemsWhy"/> and the tooltip.
/// </summary>
sealed class TargetRates
{
    public required ActiveThievingRecipe Active { get; init; }
    public ThievingRecipe Recipe => Active.Recipe;
    public required ThievingLocation Location { get; init; }
    /// <summary>
    /// The game shows this target as "???" on the Thieving screen: its place or its own requirements are not met
    /// yet. The window shows it as the game's cards do, as the maintainer chose (22 Nov 2026, "??? like the game"):
    /// "???", neither its name nor its picture nor what it gives; what it still needs stays on its line. This is the
    /// one place that decides it.
    /// </summary>
    public bool Hidden;
    /// <summary>The target can be stolen from now: its place and itself are unlocked.</summary>
    public bool Reachable;
    /// <summary>What it still needs, in the game's words for its requirements ("Locked" when it does not say).</summary>
    public string Needs = "";
    /// <summary>
    /// A heist: its rewards come over several stages (the game's MaxStages) and on completing them. How many tries
    /// a whole heist takes is not a number the game gives, so its XP and rewards are not counted.
    /// </summary>
    public bool IsHeist;
    public int Stages;

    /// <summary>The chance that one try succeeds, as a share of 1; NaN while its unit is not proven.</summary>
    public double Success = double.NaN;
    /// <summary>The success chance as the game keeps it, before any reading of its unit.</summary>
    public double SuccessRaw;
    public double IntervalSeconds, StunSeconds;
    public double StunAvoided, DamageAvoided, Doubling, Fortune;
    /// <summary>A try and, on average, the stun after a failure; 0 when it cannot be worked out.</summary>
    public double SecondsPerTry;
    public double TriesPerHour, SuccessesPerHour, FailuresPerHour, HitsPerHour;
    public double? XpPerHour;
    public Currency? Coin;
    public double? CoinsPerHour;
    /// <summary>The items are counted; when not, <see cref="ItemsWhy"/> says why (a heist, a fortune, a unit).</summary>
    public bool ItemsKnown;
    public string ItemsWhy = "";
    /// <summary>True when the items come from View Possible Rewards as the game showed it (a fortune is counted).</summary>
    public bool FromRewardsWindow;
    public readonly List<Drop> Drops = new();
    /// <summary>What the items sell for, and coins plus that, in gold per hour; null when the items are not counted.</summary>
    public double? LootValuePerHour, GoldPerHour;
    public bool HasRareTable;
    /// <summary>Tries per hour that give a rare item; null when that is not counted.</summary>
    public double? RarePerHour;
    /// <summary>The most one failure can take off your HP: the HP bar's own max hit for the target you steal from,
    /// the target's Damage (the max hit its card shows) for the others.</summary>
    public int MaxHit;
    public bool MaxHitFromBar;
    /// <summary>Food eaten per hour with every failure that hurts counted at the max hit; null when not counted.</summary>
    public double? FoodPerHour;
    /// <summary>Safe: no failure can take you from the auto eat point to 0 HP. Null when that cannot be told.</summary>
    public bool? Safe;
    public string SafeWords = "";

    public bool SuccessKnown => !double.IsNaN(Success);
    public bool Valid => SecondsPerTry > 0;

    // The game keeps intervals in milliseconds (RecipeRates' Rates).
    const double Ms = 1000;

    /// <param name="reading">View Possible Rewards as the game last showed it for this target, if it was opened.</param>
    /// <param name="barMaxHit">The HP bar's max hit, for the target being stolen from.</param>
    public static TargetRates Read(ActiveThievingRecipe active, ThievingLocation location, bool placeOpen, ThievingFood food, Units units,
        RewardsReading? reading, int? barMaxHit)
    {
        ThievingRecipe recipe = active.Recipe;
        ThievingActionStats s = active.Stats;
        var r = new TargetRates { Active = active, Location = location };
        // The Thieving screen writes "???" for a target until its own requirements are met (its card, in a place
        // that can be chosen); a place that cannot be chosen shows none of its targets.
        r.Hidden = !placeOpen || recipe.Requirements?.AreMet == false;
        r.Reachable = placeOpen && recipe.Requirements?.AreMet != false && recipe.AccessRequirements?.AreMet != false;
        if (!r.Reachable)
            r.Needs = Native.Needs(placeOpen ? [recipe.Requirements, recipe.AccessRequirements] : [location.Requirements, recipe.Requirements, recipe.AccessRequirements]);
        r.Stages = s.MaxStages.Value;
        r.IsHeist = recipe.CompletionDropTable is { Items.Count: > 0 } || r.Stages > 1 || recipe.Stages > 1;

        r.SuccessRaw = s.SuccessChance.Value;
        if (units.SuccessProven) r.Success = Math.Clamp(r.SuccessRaw, 0, 1);
        r.IntervalSeconds = s.Interval.Value / Ms;
        r.StunSeconds = Math.Max(0, s.StunInterval.Value / Ms);
        r.StunAvoided = ThievingFood.Chance(s.StunAvoidanceChance);
        r.DamageAvoided = ThievingFood.Chance(s.DamageAvoidanceChance);
        r.Doubling = ThievingFood.Chance(s.DoublingChance);
        r.Fortune = s.Fortune.Value;
        r.MaxHit = barMaxHit is > 0 ? barMaxHit.Value : Math.Max(0, recipe.Damage);
        r.MaxHitFromBar = barMaxHit is > 0;
        r.HasRareTable = recipe.RareDropTable is { Items.Count: > 0 };

        double perTry = r.SuccessKnown ? r.IntervalSeconds + (1 - r.Success) * (1 - r.StunAvoided) * r.StunSeconds : double.NaN;
        if (perTry > 0.01 && double.IsFinite(perTry))
        {
            r.SecondsPerTry = perTry;
            r.TriesPerHour = 3600 / perTry;
            r.SuccessesPerHour = r.TriesPerHour * r.Success;
            r.FailuresPerHour = r.TriesPerHour - r.SuccessesPerHour;
            r.HitsPerHour = r.FailuresPerHour * (1 - r.DamageAvoided);
            r.Rewards(recipe, s, units, reading);
        }
        else r.ItemsWhy = r.SuccessKnown ? "the game gives no time for a try here" : units.SuccessWhy;
        r.Judge(food);
        return r;
    }

    // XP, coins and items, each counted per successful try: the XP the card shows for a try, the coins and the
    // common and rare tables at their chances (Recipe Rates' reading of a chance per action).
    void Rewards(ThievingRecipe recipe, ThievingActionStats s, Units units, RewardsReading? reading)
    {
        if (IsHeist)
        {
            ItemsWhy = $"a heist gives its XP and rewards over {Stages} stages and on completing them, and how many tries a whole heist takes is not a number the game gives";
            return;
        }
        XpPerHour = s.Experience.XpToAdd.Value * SuccessesPerHour;
        if (!units.LootProven)
        {
            ItemsWhy = units.LootWhy;
            return;
        }

        // Coins: a chance per success, of an amount between the least and the most the game says.
        if (s.CurrencyReward.Value is { Currency: { } coin } reward && reward.MaxQuantity > 0)
        {
            Coin = coin;
            CoinsPerHour = SuccessesPerHour * Math.Clamp(s.CurrencyLootChance.Value, 0, 1) * (reward.MinQuantity + reward.MaxQuantity) / 2.0;
        }
        double common = Math.Clamp(s.CommonLootChance.Value, 0, 1), rare = Math.Clamp(s.RareLootChance.Value, 0, 1);
        if (HasRareTable) RarePerHour = SuccessesPerHour * rare;

        // Items: what View Possible Rewards showed for this target with the fortune it has now; else, without a
        // fortune, the game's tables as they are (GetItemChance, the data's quantities); else nothing.
        if (reading != null && reading.Fortune == Fortune)
        {
            FromRewardsWindow = true;
            AddRows(reading, "Common", common);
            AddRows(reading, "Rare", rare);
        }
        else if (Fortune == 0)
        {
            AddTable(recipe.CommonDropTable, common, "Common");
            AddTable(recipe.RareDropTable, rare, "Rare");
        }
        else
        {
            ItemsWhy = $"the game counts a fortune of {Fortune:0.##} here, which changes what each try gives; open this target's View Possible Rewards once and the window takes the game's chances from there";
            return;
        }
        ItemsKnown = true;
        LootValuePerHour = Drops.Sum(d => d.PerHour * d.GoldEach);
        GoldPerHour = LootValuePerHour + (Coin?.Id == CurrencyIds.GoldPieces ? CoinsPerHour ?? 0 : 0);
    }

    // Each item at its share of the table and the average of its quantities; the chance to double doubles the
    // items of a try, as Recipe Rates counts it for products.
    void AddTable(IReadOnlyWeightedTable<WeightedItemDrop>? table, double chance, string name)
    {
        if (table == null) return;
        foreach (WeightedItemDrop drop in table.Items)
            if (drop.Item != null)
                Drops.Add(new Drop(drop.Item, name, PerHour(chance * table.GetItemChance(drop), drop.MinQuantity, drop.MaxQuantity), GoldEach(drop.Item)));
    }

    void AddRows(RewardsReading reading, string name, double chance)
    {
        if (!reading.Tables.TryGetValue(name, out var rows)) return;
        foreach (RewardsReading.Row row in rows)
            Drops.Add(new Drop(row.Item, name, PerHour(chance * row.Share, row.Min, row.Max), GoldEach(row.Item)));
    }

    /// <summary>How many per hour, for a chance per successful try and a range of quantities.</summary>
    public double PerHour(double chancePerSuccess, int min, int max) =>
        SuccessesPerHour * chancePerSuccess * (Math.Max(0, min) + Math.Max(min, max)) / 2.0 * (1 + Doubling);

    static double GoldEach(Item item) =>
        item.SellsFor is { Currency.Id: var id, Quantity: > 0 } price && id == CurrencyIds.GoldPieces ? price.Quantity : 0;

    // Safe or Risky in the Combat Simulator's sense (its Safe column and the enemy cards): can one hit at this
    // target's highest damage take you from the point where auto eat steps in to 0 HP? While the success chance is
    // not known, a failure is counted as possible.
    void Judge(ThievingFood food)
    {
        if (food.Selected && food.Healing > 0 && Valid)
            FoodPerHour = HitsPerHour <= 0 || MaxHit <= 0 ? 0 : HitsPerHour * MaxHit / food.Healing * (1 - food.Preserved);
        if ((SuccessKnown && Success >= 1) || MaxHit <= 0 || DamageAvoided >= 1)
        {
            Safe = true;
            SafeWords = MaxHit <= 0 ? "Safe: a failure here does no damage." : DamageAvoided >= 1 ? "Safe: you avoid all damage here." : "Safe: you never fail here.";
            return;
        }
        if (food.AutoEatHp is not { } eat)
        {
            Safe = null;
            SafeWords = "Where auto eat steps in cannot be told right now.";
            return;
        }
        Safe = MaxHit < eat;
        SafeWords = eat <= 0 ? "Risky: auto eat cannot heal you here (no food chosen for Thieving, none of it in the bank, or no auto eat)."
            : Safe == true ? $"Safe: the biggest hit ({MaxHit:N0}) stays under your auto eat point ({eat:#,0.#} HP)."
            : $"Risky: the biggest hit ({MaxHit:N0}) is not under your auto eat point ({eat:#,0.#} HP): one failure can take you to 0 HP.";
    }

    /// <summary>Of one item, per hour, from the common and the rare rewards together; null when not counted.</summary>
    public double? PerHourOf(Item item)
    {
        if (!ItemsKnown) return null;
        var drops = Drops.Where(d => ReferenceEquals(d.Item, item)).ToList();
        return drops.Count == 0 ? null : drops.Sum(d => d.PerHour);
    }

    /// <summary>Whether the target has this item in its tables, counted or not.</summary>
    public bool Gives(Item item) =>
        Recipe.CommonDropTable?.Items.Any(d => ReferenceEquals(d.Item, item)) == true ||
        Recipe.RareDropTable?.Items.Any(d => ReferenceEquals(d.Item, item)) == true ||
        Recipe.CompletionDropTable?.Items.Any(d => ReferenceEquals(d.Item, item)) == true;

    // ---- words and numbers ------------------------------------------------------------------------------

    /// <summary>The Combat Simulator's number format (SimView.Fmt).</summary>
    public static string Fmt(double value) => Math.Abs(value) switch
    {
        >= 999_950 => (value / 1_000_000).ToString("0.##") + "M",
        >= 100_000 => (value / 1000).ToString("0.#") + "K",
        >= 100 => value.ToString("N0"),
        >= 10 => value.ToString("0.#"),
        0 => "0",
        _ => value.ToString("0.##"),
    };

    /// <summary>
    /// How many of an item per hour, in one form wherever it is written: as a number from one an hour up, and under
    /// one an hour as the time between two, as the Rare column writes the time until a rare item ("4h 10m").
    /// </summary>
    public static string Rate(double perHour) => perHour >= 1 ? Fmt(perHour) : perHour > 0 ? Clock(3600 / perHour) : "0";

    /// <summary>The same in words: "12.3 per hour", "one every 4h 10m".</summary>
    public static string RateWords(double perHour) =>
        perHour >= 1 ? Fmt(perHour) + " per hour" : perHour > 0 ? "one every " + Clock(3600 / perHour) : "none";

    /// <summary>"3s", "5m", "5m 3s", "2h", "2h 10m" (CombatSim's SimText.Clock).</summary>
    public static string Clock(double seconds)
    {
        if (seconds >= 100 * 3600) return $"{seconds / 3600:N0}h";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? (t.Minutes == 0 ? $"{(int)t.TotalHours}h" : $"{(int)t.TotalHours}h {t.Minutes}m")
             : t.TotalMinutes >= 1 ? (t.Seconds == 0 ? $"{t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s") : $"{t.Seconds}s";
    }

    /// <summary>
    /// A time of a try as the Thieving screen writes the time on a card ("3.00s", IntervalDisplay's two decimals);
    /// a minute or more as the clock of the other times.
    /// </summary>
    public static string Seconds(double seconds) => seconds < 59.995 ? $"{seconds:0.00}s" : Clock(seconds);

    /// <summary>A chance as the Thieving screen writes the success chance ("60.06%", the game's own P2).</summary>
    public static string Percent(double chance) => chance.ToString("P2");
}
