using System;
using System.Collections.Generic;
using System.Linq;

namespace WoodcuttingRates;

/// <summary>How the numbers are written, and the words of a line's tooltip, as Fishing Rates writes them (mods/FishingRates/Words.cs).</summary>
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
    /// Why whole columns of the window show "-", under the list as Thieving Overview says it (OverviewView's note), or ""
    /// when none do.
    /// </summary>
    public static string Note(IEnumerable<TreeRates> trees) => trees.Any(t => t.HasTime && t.LogsPerHour == null)
        ? "Time, Logs and Gold show - for now: the game gives no number for the damage of a hit, so the hits it takes to cut a tree are not counted."
        : "";

    /// <summary>
    /// The whole hour at a tree, as far as it is counted: the time of a hit, the XP, the tree's hit points and what changes
    /// the hits a cut takes, the logs, what a hit and a cut can bring, the overgrowth on it now. nameOf gives the game's
    /// name of a chance the window does not count, when it has been read (BonusNames).
    /// </summary>
    public static string Tooltip(TreeRates t, Func<string, string?> nameOf)
    {
        var lines = new List<string> { t.Category.Length > 0 ? $"{t.Name} ({t.Category})" : t.Name };
        if (t.Locked) lines.Add("Locked: " + t.Needs);
        if (t.OnPage) lines.Add(t.BeingCut ? "The tree being cut." : "The tree the Woodcutting page shows in its big panel.");
        if (t.HiddenByPlayer) lines.Add("Hidden by you on the Woodcutting page.");
        if (!t.HasTime)
        {
            lines.Add("The game gives no time for a hit on this tree yet.");
            if (Click(t) is { } once) lines.AddRange(["", once]);
            return string.Join("\n", lines);
        }

        lines.Add($"One hit every {Seconds(t.HitSeconds)}: {Number(t.HitsPerHour)} an hour.");
        lines.Add($"Woodcutting XP: {Number(t.XpPerHour)} an hour, {Number(t.XpEach)} a hit.");
        lines.Add($"Hit points: {t.HitPoints:N0}. The hits a cut takes are not counted: the game gives no number for the damage of a hit.");
        if (t.HitChances.Count > 0)
        {
            // The game's own names, as the Woodcutting page lists them among a tree's bonuses.
            var said = t.HitChances.Select(b => $"{nameOf(b.Key) ?? BonusNames.Kind(b.Key)} ({Percent(b.Value)})").ToList();
            string list = said.Count == 1 ? said[0] : string.Join(", ", said.Take(said.Count - 1)) + " and " + said[^1];
            lines.Add($"Your {list} change {(said.Count == 1 ? "it" : "them")} too.");
        }

        lines.Add("");
        if (t.Log != null) lines.Add($"Logs: {t.LogName}, {t.LogQuantity:N0} at a time; not counted per hour.");
        if (t.PerHit.Count > 0)
        {
            lines.Add("Every hit, a chance of:");
            lines.AddRange(t.PerHit.Select(d => Item(d, "hit", "hits")));
        }
        if (t.PerCut.Count > 0)
        {
            lines.Add("Every cut, a chance of:");
            lines.AddRange(t.PerCut.Select(d => Item(d, "cut", "cuts")));
        }
        if (t.Doubling > 0 && t.PerHit.Count + t.PerCut.Count > 0) lines.Add($"The doubling chance ({Percent(t.Doubling)}) is counted in these items.");
        lines.Add("Gold: not counted, as the logs and the cuts of an hour are not.");

        if (t.Effects.Count > 0)
        {
            lines.Add("");
            lines.Add("Overgrowth on this tree now: " + string.Join(", ", t.Effects.Select(e => $"{e.Name} ({e.StacksLeft:N0} {(e.StacksLeft == 1 ? "stack" : "stacks")} left)")) +
                $". {(t.Effects.Count == 1 ? "Its" : "Their")} bonuses are in the numbers above.");
        }
        if (Click(t) is { } click) lines.AddRange(["", click]);
        return string.Join("\n", lines);
    }

    // What a click on the line does, when it changes what the Woodcutting page shows.
    static string? Click(TreeRates t) => t.Clickable ? "Click to show this tree on the Woodcutting page. Woodcutting is not started." : null;

    // "  Bird Nest, 0.5% of hits: 6", with the number one brings when it is not one, and per hour where it is counted.
    static string Item(TreeRates.Drop d, string one, string many)
    {
        string each = d.Quantity == 1 ? "" : $" ({d.Quantity:N0} a {one})";
        return $"  {d.Name}, {Percent(d.Chance)} of {many}{each}" + (d.PerHour is { } perHour ? $": {Number(perHour)}" : "");
    }
}
