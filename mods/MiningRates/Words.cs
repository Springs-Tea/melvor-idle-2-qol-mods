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
    /// The whole hour at a rock: the time of a hit, the breaks by the game's rule (RockRates.Settled), the XP, the ore of
    /// a break and the items of a hit with their chance and number, and the gold. nameOf gives the game's name of a bonus
    /// that is not counted, when it has been read (BonusNames). No line is longer than Fishing Rates' longest (94
    /// characters): Godot's tooltip does not wrap.
    /// </summary>
    public static string Tooltip(RockRates r, Func<string, string?> nameOf)
    {
        var lines = new List<string> { r.Category.Length > 0 ? $"{r.Name} ({r.Category})" : r.Name };
        if (r.Locked) lines.Add(r.Needs == "Locked" ? "Locked" : "Locked: " + r.Needs);
        if (r.Hidden) lines.Add("You hid this rock on the Mining page.");
        if (r.OnPage) lines.Add("The rock the Mining page shows.");
        if (!r.HasTime)
        {
            lines.Add("The game gives no time for a hit on this rock yet.");
            if (Click(r) is { } once) lines.AddRange(["", once]);
            return string.Join("\n", lines);
        }

        lines.Add($"One hit every {Seconds(r.HitSeconds)}: {Number(r.HitsPerHour)} hits an hour.");
        if (r.HitsPerBreak is { } hits && r.BreaksPerHour is { } breaks && RockRates.Settled is { } settled)
            lines.Add(hits == 1 ? $"A break every hit: {Number(breaks)} an hour."
                : $"A break every {Number(hits)} hits on average, {How(settled.Source)}: {Number(breaks)} an hour.");
        if (r.UnknownBreakReward is { } reason)
        {
            lines.Add(reason);
            lines.Add("The window cannot count XP, ore, time per ore or gold for this rock (-).");
        }
        // The minimum moves the sure break forward: where it is counted, it is said, as the mod's reading of its name.
        if (r.HitsPerBreak != null && r.BreakChance < 1 && r.MinimumCounted && r.MinimumProspecting > 0)
            lines.Add($"Your minimum break bar ({Percent(r.MinimumProspecting)}) is counted as where the bar starts.");
        if (r.XpPerHour is { } xp)
            lines.Add($"Mining XP: {Number(xp)} an hour, {Number(r.XpEach)} a break.");

        lines.Add("");
        if (r.BreakOrePerHour is { } ore)
        {
            // The ore of a break, and of an hour where the breaks are counted.
            lines.Add($"Every break (break chance {r.CardBreakChance:P2} a hit):");
            lines.Add($"  {r.OreName}, {r.OreQuantity} a break: {Number(ore)} an hour");
            lines.Add("Time and Ore count this product on each break. Gold includes its sell value.");
            const string gemVein = "Gem Vein";
            if (r.Category == gemVein || r.Rock.Category?.DisplayName.Message == gemVein)
                lines.Add("On Topaz Vein, the bank gained one gem on 82 breaks and two on 10 (1.109 per break).");
        }
        if (r.PerHit.Count > 0)
        {
            lines.Add("Every hit:");
            lines.AddRange(r.PerHit.Select(Item));
        }
        // Only where a number of the tooltip has it in.
        if (r.Doubling > 0 && (r.OrePerHour != null || r.PerHit.Count > 0))
            lines.Add($"The doubling chance ({Percent(r.Doubling)}) is counted in {(r.OrePerHour != null ? "the ore and " : "")}the items of a hit.");

        lines.Add("");
        if (r.GoldPerHour is { } gold)
            lines.Add(r.OrePrice != null && r.PerHitGoldPerHour > 0
                ? $"Gold: {Number(gold)} an hour, what the ore and items sell for; {Number(r.PerHitGoldPerHour)} of it from items of a hit."
                : $"Gold: {Number(gold)} an hour, what {(r.OrePrice != null ? "the ore sells" : "the items of a hit sell")} for.");
        if (r.NotCountedItems.Count > 0)
        {
            lines.Add("Not counted, the rock's own stone, essence or bar you have a chance at:");
            lines.AddRange(r.NotCountedItems.Select(side => $"  {side.Name}, {Percent(side.Chance)}"));
        }
        // The game's own names, as the Mining page lists them among your bonuses.
        foreach (RockRates.Bonus bonus in r.NotCounted)
            lines.Add(nameOf(bonus.Key) is { } name ? $"Not counted: your {name}." : $"Not counted: {BonusNames.Kind(bonus.Key)}, which you have.");
        if (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (Click(r) is { } click) lines.AddRange(["", click]);
        return string.Join("\n", lines);
    }

    // What backs the hits a break takes, in plain words: the number is always worked out by the mod, from the rock's own
    // break chance by a rule the game states or one measured on a character's mining (the code review of turn 2, fault 3).
    public static string How(RuleSource source) => source == RuleSource.Measured ? "worked out by a rule measured in the game" : "worked out by the game's own rule";

    // What a click on the line does, when it changes what the Mining page shows; otherwise why it cannot show the rock's
    // card when the page has no room for it clear of this window (review 4), and nothing when it is in sight already.
    static string? Click(RockRates r) => r.Clickable ? "Click to show this rock on the Mining page. Mining is not started." : r.Sight switch
    {
        CardSight.UnderWindow => "The Mining page has no room for this rock's card beside this window: drag the window aside.",
        CardSight.NoRoom => "The Mining page has no room to show this rock's whole card.",
        _ => null,
    };

    // "  Topaz, 1% of hits: 12", with the number a hit gives when it is not one.
    static string Item(RockRates.Drop d)
    {
        string each = d.Quantity == 1 ? "" : $" ({d.Quantity} a hit)";
        return $"  {d.Name}, {Percent(d.Chance)} of hits{each}: {Number(d.PerHour)}";
    }
}
