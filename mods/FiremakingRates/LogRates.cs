using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.CraftingAction;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Firemaking;
using GamesByMalcs.Melvor2.Stats.Nodes;
using Godot;

namespace FiremakingRates;

/// <summary>What backs a part of the rule the hour rests on.</summary>
enum RuleSource
{
    /// <summary>
    /// The arithmetic Recipe Rates counts the same log's hour by (mods/RecipeRates/Rates.cs, its FiremakingActionStats
    /// case): the window's numbers are the same as the ones Recipe Rates writes under every log on the Firemaking page.
    /// It is a reading of the game's members, not the game's words and not measured; the silent watch
    /// (firemakingwatch.request, DevWatch.cs) holds it against a character's own burning.
    /// </summary>
    RecipeRates,
    /// <summary>The Firemaking page's own words state it; the column's tip and the README quote them.</summary>
    GameWords,
    /// <summary>Counted on a character's own burning by the silent watch; the window says "measured".</summary>
    Measured,
}

/// <summary>
/// A part of the rule the hour rests on, settled: what backs it, the game's words it rests on ("" for none) and, for a
/// measured part, what was counted ("" for none).
/// </summary>
sealed record Settled(RuleSource Source, string Quote = "", string Seen = "");

/// <summary>What a click on a log's line may do on the Firemaking page (<see cref="LogRates.ClickOpens"/>).</summary>
enum Opens
{
    /// <summary>
    /// Nothing: the log's category is locked, so the player cannot open it either; or a log burns and the line's log is
    /// of another category than the page shows.
    /// </summary>
    Nothing,
    /// <summary>A log burns: nothing is selected, and a click only scrolls the page's list to the log's button.</summary>
    Scroll,
    /// <summary>Only the log's category: the log itself is locked.</summary>
    Category,
    /// <summary>The log's category and the log.</summary>
    Log,
}

/// <summary>
/// What one hour of burning a log gives and costs, worked out from the game's own numbers for that log: its stats
/// (ActiveFiremakingRecipe.Stats, a FiremakingActionStats), which already hold every bonus of the character (level,
/// equipment, potions, the Mastery Guild, the bonfire), so the hour moves with them. Counted the way Recipe Rates counts
/// the same log (mods/RecipeRates/Rates.cs, its FiremakingActionStats case), so that where both show a number it is the
/// same number: one log burnt every interval, the XP of the log's XP tag with every burn, the products at their chances
/// with the doubling chance, the coins of CurrencyReward with every burn and not doubled, and the log kept at the
/// preservation chance. Built the way Woodcutting Rates builds a tree's hour (mods/WoodcuttingRates/TreeRates.cs).
/// </summary>
sealed class LogRates
{
    /// <summary>
    /// The arithmetic of a burn, each part set here, in this one place, with what backs it. Every part is Recipe Rates'
    /// rule (RuleSource.RecipeRates): no watch has counted a character's burning yet, and the page's words read by the
    /// light check ("Dev: words", "Dev: page words") may settle a part as the game's own. Null would show "-" for what
    /// rests on that part, never a formula of the mod's own (the review of pull request #61).
    /// </summary>
    /// <remarks>XP: the XP of the log's XP tag (FiremakingActionStats.Experience.XpToAdd) with every log burnt.</remarks>
    public static readonly Settled? XpRule = new(RuleSource.RecipeRates);
    /// <summary>
    /// The logs: every burn takes the log's cost (FiremakingActionStats.ItemCost, CurrencyCost), and a burn keeps it at
    /// the preservation chance (PreservationChance): the logs that leave the bank.
    /// </summary>
    public static readonly Settled? LogsRule = new(RuleSource.RecipeRates);
    /// <summary>
    /// The items burning makes: every burn rolls each of the log's products at its chance (the crafting stats' Products, the
    /// member Recipe Rates reads), as many as its quantity, and the doubling chance (DoublingChance) doubles them. What the doubling doubles is Recipe Rates' reading
    /// too: the products, not the coins.
    /// </summary>
    public static readonly Settled? ItemsRule = new(RuleSource.RecipeRates);
    /// <summary>The coins: every burn gives the log's CurrencyReward, not doubled (Recipe Rates' "Made" line of it).</summary>
    public static readonly Settled? CoinsRule = new(RuleSource.RecipeRates);
    /// <summary>
    /// What a fire does to the bonfire: whether every fire adds the game's bonfire time of the log
    /// (FiremakingBonfireComponent.GetBonfireTimeForRecipe), in which unit, and how the bonfire's own most
    /// (FiremakingBonfire.MaxDuration) limits it. Not settled: the names of the game's members say a fire may add time
    /// (FiremakingBonfireComponent.FireTimeAdded, the statistic BonfireTimeFromFiresStatistic), but nothing read says how
    /// much, nor the unit of the game's number. Until the page's own label ("Dev: game") or the watch settle it, the
    /// window shows nothing of the bonfire (the code review of turn 1, fault 7); the request logs the raw numbers.
    /// </summary>
    public static readonly Settled? BonfireRule = null;

    /// <summary>One thing a burn uses: a log (or another item), as many as its cost, kept at the preservation chance.</summary>
    public sealed record Cost(Item Item, string Name, int Quantity, double UsedEach, double? PerHour, double? Price, double? GoldPerHour);

    /// <summary>A currency a burn costs, as many as its cost, kept at the preservation chance.</summary>
    public sealed record CurrencyCost(Currency Currency, string Name, long Quantity, double UsedEach, double? PerHour, bool Gold);

    /// <summary>
    /// One item a burn can make: <see cref="Chance"/> per burn, <see cref="Each"/> the average number it gives when it
    /// comes, doubling included, and an hour of it.
    /// </summary>
    public sealed record Product(Item Item, string Name, double Chance, int Quantity, double Each, double? PerHour, double? GoldPerHour);

    /// <summary>A Firemaking event of the game's that may come while this log burns: not counted.</summary>
    public sealed record Event(string Name, string Reward);

    /// <summary>A chance of the log's, named by the game's name on the Firemaking page (BonusNames).</summary>
    /// <param name="Key">Which of the game's stats it is, the key of its name.</param>
    /// <param name="Nodes">The game's stat, to find the name the Firemaking page gives it.</param>
    public sealed record Bonus(string Key, ModifierStatNodes Nodes, double Value);

    public const string Doubling = "DoublingChance", Preservation = "PreservationChance";

    public required ActiveFiremakingRecipe Active { get; init; }
    public required string Name { get; init; }
    /// <summary>The log's category, the way Fishing Rates writes a spot's location: only when the page has more than one.</summary>
    public required string Category { get; init; }
    /// <summary>The log's category by name, for the tooltip.</summary>
    public required string CategoryName { get; init; }
    /// <summary>The log's own picture, as the page shows it; null for the game's grey stand-in (GamePictures).</summary>
    public Texture2D? Picture { get; init; }

    /// <summary>The character cannot burn this log yet.</summary>
    public bool Locked { get; private set; }
    /// <summary>The Firemaking level a locked log needs, when a level is what it is missing.</summary>
    public int? Level { get; private set; }
    /// <summary>What a locked log is missing, in the game's own words, each once (Native.Needs).</summary>
    public string Needs { get; private set; } = "";
    /// <summary>The log the Firemaking page shows (FiremakingActionComponent.SelectedRecipe): the lit line.</summary>
    public bool OnPage { get; init; }
    /// <summary>The log the character is burning (FiremakingActionComponent.CreatingRecipe, while a log burns).</summary>
    public bool Burning { get; init; }
    /// <summary>A log burns now (<see cref="IsBurning"/>): a click selects nothing, and the tooltip says so.</summary>
    public bool AnyBurning { get; init; }
    /// <summary>The page shows the log's category (FiremakingActionComponent.SelectedCategory): its list has the log's button.</summary>
    public bool CategoryOnPage { get; init; }
    /// <summary>What a click on the line opens (<see cref="ClickOpens"/>).</summary>
    public Opens Click { get; private set; }
    /// <summary>A click on the line changes what the Firemaking page shows (FiremakingRatesMod.ClickChanges).</summary>
    public bool Clickable { get; set; }
    /// <summary>
    /// How the log's button in the page's list stands against the window, when the page lists it (Native.Place): the
    /// tooltip says when the page has no room to show it clear of the window. Null when the page does not list it.
    /// </summary>
    public CardSight? Sight { get; set; }

    /// <summary>The game's time to burn one log, in seconds (FiremakingActionStats.Interval, kept in milliseconds).</summary>
    public double Seconds { get; private set; }
    /// <summary>The game gives a time for a log: without one there is nothing to count.</summary>
    public bool HasTime => Seconds > 0.01 && double.IsFinite(Seconds);
    /// <summary>The fires an hour lights: one log is burnt every interval.</summary>
    public double FiresPerHour => HasTime ? 3600 / Seconds : 0;

    /// <summary>The XP of the log's XP tag (FiremakingActionStats.Experience.XpToAdd): the XP of a burn, by <see cref="XpRule"/>.</summary>
    public double XpEach { get; private set; }
    public double? XpPerHour => HasTime && XpRule != null ? XpEach * FiresPerHour : null;

    public double PreservationChance { get; private set; }
    public double DoublingChance { get; private set; }

    /// <summary>What a burn uses: its logs first (FiremakingActionStats.ItemCost), with your bonuses.</summary>
    public List<Cost> Costs { get; } = new();
    /// <summary>The currencies a burn costs (FiremakingActionStats.CurrencyCost), with your bonuses.</summary>
    public List<CurrencyCost> CurrencyCosts { get; } = new();
    /// <summary>The log the recipe burns: the first item of its cost.</summary>
    public Cost? Log => Costs.FirstOrDefault();
    /// <summary>The logs an hour burns: those that leave the bank, the preserved ones not counted.</summary>
    public double? LogsPerHour => Log?.PerHour;
    /// <summary>
    /// What the logs an hour burns would sell for, and the gold a burn costs: the gold a player gives up by burning them
    /// instead of selling them. A cost that sells for no gold counts nothing here (the tooltip names it).
    /// </summary>
    public double? CostPerHour => !HasTime || LogsRule == null ? null
        : Costs.Sum(c => c.GoldPerHour ?? 0) + CurrencyCosts.Where(c => c.Gold).Sum(c => c.PerHour ?? 0);
    /// <summary>How long the bank's stock lasts at this pace, in seconds; null when the log uses nothing (Recipe Rates' SecondsOfMaterials).</summary>
    public double? SecondsOfStock { get; private set; }
    /// <summary>The bank was read for <see cref="SecondsOfStock"/>: without it the tooltip says nothing of the stock.</summary>
    public bool StockRead { get; private set; }

    /// <summary>The items a burn can make (ICraftingActionStats.Products, as Recipe Rates reads them), with your bonuses.</summary>
    public List<Product> Products { get; } = new();
    /// <summary>The coins a burn gives (FiremakingActionStats.CurrencyReward), with your bonuses; null for none.</summary>
    public Currency? Coins { get; private set; }
    public string CoinsName { get; private set; } = "";
    public long CoinsEach { get; private set; }
    public double? CoinsPerHour => Coins != null && HasTime && CoinsRule != null ? CoinsEach * FiresPerHour : null;
    bool CoinsAreGold => Coins?.Id == Gold;
    /// <summary>
    /// What burning gives an hour, in gold: the gold coins of the burns and what the items it makes sell for. Null while
    /// a part of it that gives gold is not counted by a settled rule.
    /// </summary>
    public double? GoldPerHour
    {
        get
        {
            if (!HasTime) return null;
            double gold = 0;
            if (CoinsAreGold && CoinsEach > 0)
            {
                if (CoinsPerHour is not { } coins) return null;
                gold += coins;
            }
            foreach (Product p in Products)
            {
                if (GoldOf(p.Item) == null) continue;
                if (p.GoldPerHour is not { } sells) return null;
                gold += sells;
            }
            return gold;
        }
    }
    /// <summary>What the items of an hour sell for, in gold, apart from the coins (the tooltip says the two apart).</summary>
    public double ItemsGoldPerHour => Products.Sum(p => p.GoldPerHour ?? 0);

    /// <summary>
    /// The game's bonfire time of the log as the game gives it (GetBonfireTimeForRecipe), its unit not known: not shown
    /// to the player while <see cref="BonfireRule"/> is not settled, only logged by the development requests.
    /// </summary>
    public double BonfireRaw { get; private set; }

    /// <summary>The Firemaking events that may come while this log burns: not counted, named by the game's names.</summary>
    public List<Event> Events { get; } = new();
    /// <summary>The log's doubling and preservation chances, the ones above zero, by the game's names (BonusNames).</summary>
    public List<Bonus> Chances { get; } = new();

    // The game keeps intervals in milliseconds (Recipe Rates' "One every 2.00s" is 2000, Fishing Rates' catch times
    // matched the game's to the hundredth this way) and chances as fractions (0.1 is 10%).
    const double Ms = 1000;
    const string Gold = "MelvorBase:GoldPieces";

    /// <summary>
    /// A log the Firemaking page shows: its own requirements to be seen and those of its category are met, and a category
    /// the game shows only while it has recipes the character can make (RecipeCategory.OnlyShowWhenRecipesAvailable) has
    /// one (ActiveFiremakingCategory.UnlockedRecipeCount). That last part is the mod's reading of the page's handlers'
    /// names (FiremakingActionView.CategoryUnlockedCategoryCountChangedHandler, UpdateCategoryVisibility); the light check
    /// holds every category against the page's own buttons ("Dev: hidden").
    /// </summary>
    public static bool Listed(ActiveFiremakingRecipe active, ActiveFiremakingCategory category) =>
        active.Recipe.DisplayRequirements?.AreMet != false && category.Data.DisplayRequirements?.AreMet != false &&
        (!category.Data.OnlyShowWhenRecipesAvailable || category.UnlockedRecipeCount > 0);

    /// <summary>
    /// What a click on a log's line may do, the rule decided by the orchestrator (lesson 56, its review of turn 1). While
    /// nothing burns: the log; only its category while the log is locked; nothing while its category is locked, since the
    /// player cannot open that one either (Fishing Rates' SpotRates.ClickOpens). While a log burns (burning, read with
    /// <see cref="IsBurning"/>): no selecting member is called at all, since a selection changes what the page's Start
    /// button and the bonfire's "Add logs" use next, and no member's name rules out that it resets the fire in progress
    /// (the code review of turn 1); a line of the category the page shows only scrolls the page's list to its log's
    /// button, any other line does nothing.
    /// </summary>
    public static Opens ClickOpens(ActiveFiremakingRecipe active, FiremakingActionComponent action, bool burning)
    {
        if (burning) return ReferenceEquals(action.SelectedCategory?.Data, active.Category) ? Opens.Scroll : Opens.Nothing;
        return active.Category?.Requirements?.AreMet == false ? Opens.Nothing
            : active.Recipe.Requirements?.AreMet == false ? Opens.Category
            : Opens.Log;
    }

    /// <summary>
    /// A log burns, by the widest test the game's members allow (the code review of turn 1, fault 1): Firemaking's action
    /// is started, or it has a log it burns (CreatingRecipe), or it is among the game's running actions
    /// (Game.ActionManager.ActiveActions). Read on every tick for the lines and again at the moment of a click.
    /// </summary>
    public static bool IsBurning(FiremakingEntity firemaking, Game? game) =>
        firemaking.Action.IsStarted || firemaking.Action.CreatingRecipe != null ||
        game?.ActionManager.ActiveActions.Any(a => ReferenceEquals(a.Entity, firemaking)) == true;

    /// <param name="bank">The bank, for how long its stock lasts (null: not said).</param>
    /// <param name="shown">A picture of the game as the list shows it: null for the grey stand-in (GamePictures.Shown).</param>
    /// <param name="categories">The page has more than one category: the line writes the log's.</param>
    /// <param name="burning">A log burns now (<see cref="IsBurning"/>), read once for every line.</param>
    public static LogRates Read(ActiveFiremakingRecipe active, FiremakingEntity firemaking, BankEntity? bank,
        Func<Texture2D?, Texture2D?> shown, bool categories, bool burning)
    {
        FiremakingRecipe recipe = active.Recipe;
        FiremakingActionStats stats = active.Stats;
        var action = firemaking.Action;
        string category = recipe.Category?.DisplayName.Text ?? "";
        // The log's own picture, the one the page's list shows; the log item's when the game has none for the recipe.
        Item? burnt = (stats.ItemCost?.Values ?? []).Select(n => n?.Value?.Item).FirstOrDefault(i => i != null);
        var r = new LogRates
        {
            Active = active, Name = recipe.DisplayName.Text, CategoryName = category, Category = categories ? category : "",
            Picture = shown(recipe.Icon) ?? (burnt != null ? shown(burnt.Icon) : null),
            OnPage = ReferenceEquals(action.SelectedRecipe, active),
            Burning = burning && ReferenceEquals(action.CreatingRecipe, active),
            AnyBurning = burning,
            CategoryOnPage = ReferenceEquals(action.SelectedCategory?.Data, recipe.Category),
        };
        ConditionGroup?[] groups = [recipe.Category?.Requirements, recipe.Requirements];
        if (groups.Any(g => g?.AreMet == false))
        {
            r.Locked = true;
            r.Needs = Native.Needs(groups);
            r.Level = Native.LevelNeeded(groups, firemaking.Experience);
        }
        r.Click = ClickOpens(active, action, burning);

        r.Seconds = stats.Interval.Value / Ms;
        r.XpEach = stats.Experience.XpToAdd.Value;
        r.PreservationChance = Chance(stats.PreservationChance);
        r.DoublingChance = Chance(stats.DoublingChance);
        foreach (var (key, nodes, value) in new[] { (Doubling, stats.DoublingChance, r.DoublingChance), (Preservation, stats.PreservationChance, r.PreservationChance) })
            if (nodes != null && value > 0) r.Chances.Add(new Bonus(key, nodes, value));
        double fires = r.FiresPerHour;
        double? burns = r.HasTime ? fires : null;
        double spent = 1 - r.PreservationChance;

        // What a burn uses, with the character's bonuses (Recipe Rates: the cost times one less the preservation chance).
        double? used = LogsRule != null ? burns : null;
        foreach (var node in stats.ItemCost?.Values ?? [])
            if (node?.Value is { Item: { } item } cost && cost.Quantity > 0)
            {
                double each = cost.Quantity * spent;
                double? perHour = used * each;
                double? price = GoldOf(item);
                r.Costs.Add(new Cost(item, item.DisplayName.GetPluralText(2), cost.Quantity, each, perHour, price, perHour * price));
            }
        foreach (var node in stats.CurrencyCost?.Values ?? [])
            if (node?.Value is { Currency: { } currency } cost && cost.Quantity > 0)
                r.CurrencyCosts.Add(new CurrencyCost(currency, currency.DisplayName.Text, cost.Quantity, cost.Quantity * spent, used * cost.Quantity * spent, currency.Id == Gold));
        r.StockRead = bank != null && r.HasTime;
        r.SecondsOfStock = r.StockRead ? StockSeconds(r, bank!) : null;

        // What a burn makes: Recipe Rates' arithmetic on the member it reads (ICraftingActionStats.Products, which
        // FiremakingActionStats keeps as an explicit member), skipping what it skips (nothing on average); the request
        // holds it against FiremakingActionStats.ProductChances (the code review of turn 1, fault 9).
        double? made = ItemsRule != null ? burns : null;
        foreach (var node in ((ICraftingActionStats)stats).Products?.Values ?? [])
            if (node?.Value is { Item: { } item } product)
            {
                double chance = Fraction(product.Chance), each = product.Quantity * (1 + r.DoublingChance);
                if (!(product.Quantity * chance * (1 + r.DoublingChance) > 0)) continue;
                double? perHour = made * chance * each;
                r.Products.Add(new Product(item, item.DisplayName.GetPluralText(2), chance, product.Quantity, each, perHour,
                    GoldOf(item) is { } price ? perHour * price : null));
            }
        if (stats.CurrencyReward?.Value is { Currency: { } paid, Quantity: > 0 } reward)
        {
            r.Coins = paid;
            r.CoinsName = paid.DisplayName.Text;
            r.CoinsEach = reward.Quantity;
        }

        // The game's bonfire time of the log, raw: not shown while BonfireRule is not settled.
        r.BonfireRaw = FiremakingBonfireComponent.GetBonfireTimeForRecipe(active);

        foreach (FiremakingEvent e in action.Config.Events ?? Enumerable.Empty<FiremakingEvent>())
        {
            if (e == null || e.Requirements?.AreMet == false || (e.Category != null && !ReferenceEquals(e.Category, recipe.Category))) continue;
            string gives = e.Reward is { Item: { } item, Quantity: > 0 } prize ? $"{prize.Quantity:N0} {item.DisplayName.GetPluralText(prize.Quantity)}" : "";
            r.Events.Add(new Event(e.DisplayName.Text, gives));
        }
        return r;
    }

    // How long the bank's stock lasts at this pace (Recipe Rates' Rates.SecondsOfMaterials, unchanged in its arithmetic):
    // starting a burn needs the whole cost, and over time the preservation chance stretches the stock. 0: not enough to
    // start one.
    static double? StockSeconds(LogRates r, BankEntity bank)
    {
        double? burns = null;
        void Take(double have, double needed, double usedEach)
        {
            if (have >= needed && usedEach < 1e-9) return;
            double possible = have < needed ? 0 : have / usedEach;
            burns = burns == null ? possible : Math.Min(burns.Value, possible);
        }
        foreach (Cost c in r.Costs) Take(bank.Inventory.GetQuantity(c.Item), c.Quantity, c.UsedEach);
        foreach (CurrencyCost c in r.CurrencyCosts) Take(bank.Wallet.GetQuantity(c.Currency), c.Quantity, c.UsedEach);
        return burns * r.Seconds;
    }

    static double? GoldOf(Item item) => item.SellsFor is { Currency: { Id: Gold }, Quantity: > 0 } price ? price.Quantity : null;

    static double Chance(ModifierStatNodes? nodes) => nodes == null ? 0 : Fraction(nodes.Total.Value);

    static double Fraction(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
