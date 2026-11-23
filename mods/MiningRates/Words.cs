using System;
using System.Collections.Generic;
using System.Linq;

namespace MiningRates;

/// <summary>How the numbers are written, and the words of a line's tooltip (Fishing Rates' Words, for rocks).</summary>
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

    /// <summary>0.48 -> "48%", 0.0125 -> "1.25%"; a chance too small for that is "under 0.01%", never "0%".</summary>
    public static string Percent(double fraction)
    {
        string text = (fraction * 100).ToString("0.##");
        return text == "0" && fraction > 0 ? "under 0.01%" : text + "%";
    }

    /// <summary>
    /// The whole hour at a rock: the time of a hit, the break, the XP, the ore of a break and the items of a hit with
    /// their chance and number, the gold. nameOf gives the game's name of a bonus that is not counted, when it has been
    /// read (BonusNames).
    /// </summary>
    public static string Tooltip(RockRates r, Func<string, string?> nameOf)
    {
        var lines = new List<string> { r.Category.Length > 0 ? $"{r.Name} ({r.Category})" : r.Name };
        if (r.Locked) lines.Add(r.Needs == "Locked" ? "Locked" : "Locked: " + r.Needs);
        if (r.Hidden) lines.Add("Hidden on the Mining page: listed while its Show hidden box is ticked.");
        if (r.OnPage) lines.Add("The rock the Mining page shows.");
        if (!r.HasTime)
        {
            lines.Add("The game gives no time for a hit on this rock yet.");
            if (Click(r) is { } once) lines.AddRange(["", once]);
            return string.Join("\n", lines);
        }

        lines.Add($"One hit every {Seconds(r.HitSeconds)}: {Number(r.HitsPerHour)} hits an hour.");
        lines.Add(r.HitsPerBreak is { } hits
            ? $"A break every {Number(hits)} hits on average; break chance {Percent(r.BreakChance)}."
            : $"Break chance {Percent(r.BreakChance)}, as its card shows it. How many hits a break takes, the game does not show: the ore, its time and the gold are - for now.");
        lines.Add($"Mining XP: {Number(r.XpPerHour)} an hour, {Number(r.XpEach)} a hit.");

        lines.Add("");
        if (r.Ore != null)
        {
            lines.Add("Every break:");
            lines.Add($"  {r.OreName}, {r.OreQuantity} a break: {(r.OrePerHour is { } ore ? Number(ore) : "-")}");
        }
        if (r.PerHit.Count > 0)
        {
            lines.Add("Every hit:");
            lines.AddRange(r.PerHit.Select(Item));
        }
        if (r.Doubling > 0) lines.Add($"The doubling chance ({Percent(r.Doubling)}) is counted in the ore and the items of a hit.");

        lines.Add("");
        if (r.GoldPerHour is { } gold)
            lines.Add(r.OrePrice != null && r.PerHitGoldPerHour > 0
                ? $"Gold: {Number(gold)} an hour, what the ore and the items sell for; {Number(r.PerHitGoldPerHour)} of it from the items of a hit."
                : $"Gold: {Number(gold)} an hour, what {(r.OrePrice != null ? "the ore sells" : "the items of a hit sell")} for.");
        else
            lines.Add(r.PerHitGoldPerHour > 0
                ? $"Gold: -, the ore's hour is not known; the items of a hit sell for {Number(r.PerHitGoldPerHour)} an hour."
                : "Gold: -, the ore's hour is not known.");
        foreach (RockRates.Side side in r.NotCountedItems)
            lines.Add($"Not counted: {side.Name}, {Percent(side.Chance)}; whether it comes on a hit or a break, the game does not say.");
        // The game's own names, as the Mining page lists them among your bonuses.
        foreach (RockRates.Bonus bonus in r.NotCounted)
            lines.Add(nameOf(bonus.Key) is { } name ? $"Not counted: your {name}." : $"Not counted: {BonusNames.Kind(bonus.Key)}, which you have.");
        if (Click(r) is { } click) lines.AddRange(["", click]);
        return string.Join("\n", lines);
    }

    // What a click on the line does, when it changes what the Mining page shows.
    static string? Click(RockRates r) => r.Clickable ? "Click to show this rock on the Mining page. Mining is not started." : null;

    // "  Topaz, 1% of hits: 12", with the number a hit gives when it is not one.
    static string Item(RockRates.Drop d)
    {
        string each = d.Quantity == 1 ? "" : $" ({d.Quantity} a hit)";
        return $"  {d.Name}, {Percent(d.Chance)} of hits{each}: {Number(d.PerHour)}";
    }
}
