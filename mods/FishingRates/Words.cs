using System;
using System.Collections.Generic;
using System.Linq;

namespace FishingRates;

/// <summary>How the numbers are written, and the words of a line's tooltip.</summary>
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

    /// <summary>A catch's time as Recipe Rates writes one result's ("One every 4.25s"); a minute or more as a clock.</summary>
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
    /// How long a stock lasts, in whole minutes, as Recipe Rates writes "Materials for 2h 15m" (RecipeRatesMod.Whole):
    /// the bank changes with every catch, and a tooltip that ticks would be drawn again all the time.
    /// </summary>
    static string Whole(double seconds)
    {
        if (seconds >= 100 * 3600) return $"{seconds / 3600:N0}h";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m";
    }

    /// <summary>
    /// The whole hour at a spot: what is caught, with its chance and number, the bait, the gold. nameOf gives the
    /// game's name of a bonus that is not counted, when it has been read (BonusNames).
    /// </summary>
    public static string Tooltip(SpotRates s, Func<string, string?> nameOf)
    {
        var lines = new List<string> { $"{s.Name} ({s.Location})" };
        if (s.Locked) lines.Add(s.Missing.Count > 0 ? "Locked: " + string.Join("; ", s.Missing) : "Locked");
        if (s.OnPage) lines.Add("The spot the Fishing page shows.");
        if (!s.HasTime)
        {
            lines.Add("The game gives no catch time for this spot yet.");
            if (Click(s) is { } once) lines.AddRange(["", once]);
            return string.Join("\n", lines);
        }

        lines.Add(s.MaxSeconds - s.MinSeconds < 0.005
            ? $"One catch every {Seconds(s.Seconds)}: {Number(s.CatchesPerHour)} an hour."
            : $"One catch every {Seconds(s.MinSeconds)} to {Seconds(s.MaxSeconds)}, {Seconds(s.Seconds)} on average: {Number(s.CatchesPerHour)} an hour.");
        lines.Add($"Fishing XP: {Number(s.XpPerHour)} an hour, {Number(s.XpEach)} a catch.");

        lines.Add("");
        if (s.PriorityFish != null)
            lines.Add($"Fish on {Percent(s.FishChance)} of catches, {s.PriorityFish} as the priority fish: not counted.");
        else if (s.Fish.Count > 0)
        {
            lines.Add($"Fish on {Percent(s.FishChance)} of catches:");
            lines.AddRange(s.Fish.Select(Item));
        }
        if (s.Junk.Count > 0)
        {
            lines.Add($"Junk on {Percent(s.JunkChance)} of catches:");
            lines.AddRange(s.Junk.Select(Item));
        }
        if (s.Special.Count > 0)
        {
            lines.Add("Special items, rolled on every catch:");
            lines.AddRange(s.Special.Select(Item));
        }
        if (s.Doubling > 0) lines.Add($"The doubling chance ({Percent(s.Doubling)}) is counted in the fish and special items.");

        lines.Add("");
        if (s.GoldPerHour is not { } gold) lines.Add("Gold: not counted, with a priority fish.");
        else
        {
            // A rare item that sells for a fortune can be most of an hour's gold on average, but it comes seldom:
            // the part that does not depend on it is said too, for a player who wants gold he can count on.
            double rare = s.SpecialGoldPerHour;
            lines.Add(rare > 0
                ?$"Gold: {Number(gold)} an hour, what the catches sell for; {Number(rare)} of it from special items."
                : $"Gold: {Number(gold)} an hour, what the catches sell for.");
        }
        // How long the bank's bait lasts, never how many there are: that number changes with every catch.
        if (s.Bait != null && s.BaitPerHour is { } bait)
        {
            string kept = s.BaitKept > 0 ? $", {Percent(s.BaitKept)} preserved" : "";
            string stock = s.BaitInBank <= 0 ? "none is in the bank" : bait <= 0 ? "none is used up"
                : s.BaitInBank / bait * 3600 < 60 ? "what is in the bank lasts less than a minute"
                : $"what is in the bank lasts {Whole(s.BaitInBank / bait * 3600)}";
            lines.Add($"{s.Bait}: {Number(bait)} an hour{kept}; {stock}.");
        }
        // The game's own names, as the Fishing page lists them among your bonuses.
        foreach (SpotRates.Bonus bonus in s.NotCounted)
            lines.Add(nameOf(bonus.Key) is { } name ? $"Not counted: your {name}." : $"Not counted: {BonusNames.Kind(bonus.Key)}, which you have.");
        if (Click(s) is { } click) lines.AddRange(["", click]);
        return string.Join("\n", lines);
    }

    // What a click on the line does, when it changes what the Fishing page shows.
    static string? Click(SpotRates s) => !s.Clickable ? null : s.Click == SpotRates.Opens.Spot
        ? "Click to open this spot on the Fishing page. Fishing is not started."
        : $"Click to open {s.Location} on the Fishing page. Fishing is not started.";

    // "  Raw Shrimp, 48% of catches: 401", with the number a catch gives when it is not one.
    static string Item(SpotRates.Catch c)
    {
        string each = c.Min == c.Max ? c.Min == 1 ? "" : $" ({c.Min} a catch)" : $" ({c.Min} to {c.Max} a catch)";
        return $"  {c.Name}, {Percent(c.Chance)} of catches{each}: {Number(c.PerHour)}";
    }
}
