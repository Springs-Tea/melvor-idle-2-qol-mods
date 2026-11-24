using System;
using System.Collections.Generic;
using System.Linq;

namespace FiremakingRates;

/// <summary>
/// How the numbers are written, and the words of a line's tooltip: Woodcutting Rates' Words (mods/WoodcuttingRates/
/// Words.cs), itself Fishing Rates', for logs. No line is longer than Fishing Rates' longest (94 characters): Godot's
/// tooltip does not wrap.
/// </summary>
static class Words
{
    /// <summary>The Combat Simulator's numbers (SimView.Fmt): 8.4, 857, 8,570, 123.5K, 1.2M.</summary>
    public static string Number(double value) => Math.Abs(value) switch
    {
        >= 999_950 => (value / 1_000_000).ToString("0.##") + "M",
        >= 100_000 => (value / 1000).ToString("0.#") + "K",
        >= 100 => value.ToString("N0"),
        >= 10 => value.ToString("0.#"),
        0 => "0",
        _ => value.ToString("0.##"),
    };

    /// <summary>A time as Recipe Rates writes one result's ("One every 4.25s"); a minute or more as a clock.</summary>
    public static string Seconds(double seconds) => seconds < 59.995 ? $"{seconds:0.##}s" : Clock(seconds);

    /// <summary>The Combat Simulator's clock (SimText.Clock): "1h 5m", "2m 30s", "45s".</summary>
    static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? (t.Minutes == 0 ? $"{(int)t.TotalHours}h" : $"{(int)t.TotalHours}h {t.Minutes}m")
             : t.TotalMinutes >= 1 ? (t.Seconds == 0 ? $"{t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s") : $"{t.Seconds}s";
    }

    /// <summary>
    /// How long a stock lasts, in whole minutes, as Recipe Rates' "Materials for" writes it: the bank changes with every
    /// log burnt, and a line that ticks would redraw the tooltip all the time.
    /// </summary>
    static string Whole(double seconds)
    {
        if (seconds >= 100 * 3600) return $"{seconds / 3600:N0}h";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m";
    }

    /// <summary>0.48 -> "48%", 0.0125 -> "1.25%"; a chance too small for that is "under 0.01%", never "0%".</summary>
    public static string Percent(double fraction)
    {
        string text = (fraction * 100).ToString("0.##");
        return text == "0" && fraction > 0 ? "under 0.01%" : text + "%";
    }

    /// <summary>What backs a rule, in plain words, for a column's tip.</summary>
    public static string How(RuleSource source) => source switch
    {
        RuleSource.GameWords => "by the game's own words",
        RuleSource.Measured => "measured in the game",
        _ => "as Recipe Rates counts it",
    };

    /// <summary>
    /// The whole hour of burning a log, as far as it is counted: the time of a log and the fires of an hour, the XP, the
    /// logs burnt and how long the bank's last, what a fire gives (coins, items with their chances), the gold of both,
    /// and the events the window does not count, by the game's names. Nothing of the bonfire: what a fire adds to it is
    /// not settled (LogRates.BonfireRule). nameOf gives the game's name of a chance, when it has been read (BonusNames).
    /// </summary>
    public static string Tooltip(LogRates t, Func<string, string?> nameOf)
    {
        var lines = new List<string> { t.CategoryName.Length > 0 ? $"{t.Name} ({t.CategoryName})" : t.Name };
        if (t.Locked) lines.Add(t.Needs == "Locked" ? "Locked" : "Locked: " + t.Needs);
        if (t.OnPage) lines.Add("The log the Firemaking page shows.");
        if (t.Burning) lines.Add("The log you are burning.");
        if (!t.HasTime)
        {
            lines.Add("The game gives no time for this log yet.");
            if (Click(t) is { } once) lines.AddRange(["", once]);
            return string.Join("\n", lines);
        }

        lines.Add($"One fire every {Seconds(t.Seconds)}: {Number(t.FiresPerHour)} fires an hour.");
        lines.Add(t.XpPerHour is { } xp ? $"Firemaking XP: {Number(xp)} an hour, {Number(t.XpEach)} a fire." : $"Firemaking XP: {Number(t.XpEach)} a fire.");

        // What a burn uses: the logs that leave the bank, how long the bank's stock lasts, and what else it uses.
        lines.Add("");
        if (t.Log is { } log)
            lines.Add(log.PerHour is { } logs ? $"Logs burnt: {Number(logs)} {log.Name} an hour" + (t.StockRead ? $"; {Stock(t)}." : ".") : $"Logs burnt: {log.Name}, {log.Quantity:N0} a fire.");
        foreach (LogRates.Cost other in t.Costs.Skip(1))
            lines.Add(other.PerHour is { } n ? $"Also used: {Number(n)} {other.Name} an hour." : $"Also used: {other.Name}, {other.Quantity:N0} a fire.");
        foreach (LogRates.CurrencyCost paid in t.CurrencyCosts)
            lines.Add(paid.PerHour is { } n ? $"Also costs: {Number(n)} {paid.Name} an hour." : $"Also costs: {paid.Quantity:N0} {paid.Name} a fire.");
        if (t.Log == null && t.CurrencyCosts.Count == 0) lines.Add("A fire of this log uses nothing.");
        if (t.PreservationChance > 0 && t.Costs.Count + t.CurrencyCosts.Count > 0)
            lines.Add($"{Chance(t, LogRates.Preservation, nameOf)} ({Percent(t.PreservationChance)}) is counted in what is used.");

        // What a fire gives: the coins (on every fire, by the rule) and the items at their chances, the doubling chance.
        lines.Add("");
        if (t.Coins != null || t.Products.Count > 0)
        {
            lines.Add("What a fire gives:");
            if (t.Coins != null)
                lines.Add($"  {t.CoinsEach:N0} {t.CoinsName} a fire" + (t.CoinsPerHour is { } coins ? $": {Number(coins)}" : ""));
            lines.AddRange(t.Products.Select(Item));
            if (t.DoublingChance > 0 && t.Products.Count > 0)
                lines.Add($"{Chance(t, LogRates.Doubling, nameOf)} ({Percent(t.DoublingChance)}) is counted in the items, not the coins.");
        }

        // The gold of both: what burning gives, and what the logs burnt would have sold for.
        lines.Add("");
        if (t.GoldPerHour is { } gold)
        {
            bool coinsGold = t.CoinsPerHour is > 0 && gold > t.ItemsGoldPerHour + 1e-9, itemsGold = t.ItemsGoldPerHour > 0;
            lines.Add(gold <= 0 ? "Gold: burning this log gives no gold, and no item that sells."
                : $"Gold: {Number(gold)} an hour, " + (coinsGold && itemsGold ? "the coins and what the items sell for." : coinsGold ? "the coins." : "what the items sell for."));
        }
        if (t.CostPerHour is { } cost)
            lines.Add(cost <= 0 ? "Cost: none, what is burnt sells for no gold."
                : $"Cost: {Number(cost)} gold an hour, what the logs burnt would sell for" + (t.CurrencyCosts.Any(c => c.Gold) ? ", and the gold used." : "."));

        // The game's events that may come while this log burns: the window does not know how often the game rolls them.
        if (t.Events.Count > 0)
        {
            lines.Add("");
            lines.Add("May also come while a log burns, not counted:");
            lines.AddRange(t.Events.Select(e => e.Reward.Length > 0 ? $"  {e.Name}: {e.Reward}" : $"  {e.Name}"));
        }
        while (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (Click(t) is { } click) lines.AddRange(["", click]);
        return string.Join("\n", lines);
    }

    // "the bank has enough for 2h 15m", from the bank's stock at this pace, in whole minutes (Recipe Rates' "Materials for").
    static string Stock(LogRates t) => t.SecondsOfStock switch
    {
        null => "none is used up",
        <= 0 => "the bank has not enough for one fire",
        < 60 => "the bank has enough for less than a minute",
        { } seconds when double.IsPositiveInfinity(seconds) => "none is used up",
        { } seconds => $"the bank has enough for {Whole(seconds)}",
    };

    // The game's name of a chance, as the Firemaking page lists it among the selected log's bonuses, or what it is.
    static string Chance(LogRates t, string key, Func<string, string?> nameOf) =>
        t.Chances.Any(c => c.Key == key) && nameOf(key) is { } name ? name : BonusNames.Kind(key);

    // What a click on the line does, when it changes what the Firemaking page shows; while a log burns, that a click
    // selects nothing (the rule of LogRates.ClickOpens); and, when the page's list has no room for the log's button clear
    // of this window, that it has none (Woodcutting Rates' review 4), or that the list does not show it. The sentence of
    // no room is on two lines: on one it would be wider than Fishing Rates' widest. "burn" is the mod's word until the
    // light check has read the page's own ("Dev: words").
    static string? Click(LogRates t)
    {
        string? room = t.Sight switch
        {
            CardSight.UnderWindow => "The Firemaking page has no room for this log's button beside this window:\ndrag the window aside.",
            CardSight.NoRoom => "The Firemaking page has no room to show this log's whole button.",
            CardSight.NotShown => "The Firemaking page's list does not show this log's button now.",
            _ => null,
        };
        string? click = t.Click == Opens.Scroll
            ? t.Clickable ? "While you burn a log, a click only scrolls the Firemaking page's list to this log."
                : t.OnPage ? null : "While you burn a log, a click selects nothing on the Firemaking page."
            : t.AnyBurning ? "While you burn a log, a click selects nothing on the Firemaking page."
            : !t.Clickable ? null
            : t.Click == Opens.Log && !t.OnPage ? "Click to open this log on the Firemaking page. Burning is not started."
            : t.Click != Opens.Nothing && !t.CategoryOnPage ? $"Click to open {t.CategoryName} on the Firemaking page. Burning is not started."
            : "Click to show this log in the Firemaking page's list.";
        return click != null && room != null ? click + "\n" + room : click ?? room;
    }

    // "  Ash, 50% of logs: 792", with the number one brings when it is not one, and per hour where it is counted.
    static string Item(LogRates.Product p)
    {
        string each = p.Quantity == 1 ? "" : $" ({p.Quantity:N0} a fire)";
        return $"  {p.Name}, {Percent(p.Chance)} of fires{each}" + (p.PerHour is { } perHour ? $": {Number(perHour)}" : "");
    }
}
