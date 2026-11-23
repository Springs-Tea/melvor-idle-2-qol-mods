using System;
using System.Collections.Generic;
using System.Linq;

namespace WoodcuttingRates;

/// <summary>
/// How the numbers are written, and the words of a line's tooltip: Mining Rates' Words (mods/MiningRates/Words.cs), for
/// trees. No line is longer than Fishing Rates' longest (94 characters): Godot's tooltip does not wrap.
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

    /// <summary>0.48 -> "48%", 0.0125 -> "1.25%"; a chance too small for that is "under 0.01%", never "0%".</summary>
    public static string Percent(double fraction)
    {
        string text = (fraction * 100).ToString("0.##");
        return text == "0" && fraction > 0 ? "under 0.01%" : text + "%";
    }

    /// <summary>
    /// Why whole columns of the window show "-", said once, under the list, as Thieving Overview says why (OverviewView's
    /// note), or "" when no line of a tree that can be cut has one. It says what the window does not know, never what
    /// the game does not say: nobody has read all the game's words.
    /// </summary>
    public static string Note(IEnumerable<TreeRates> trees)
    {
        var open = trees.Where(t => !t.Locked && t.HasTime).ToList();
        bool logs = open.Any(t => t.Log != null && t.LogsPerHour == null);
        if (!logs && !open.Any(t => t.GoldPerHour == null)) return "";
        var unknown = new List<string>();
        if (TreeRates.HitsRule == null) unknown.Add("how much a hit takes from a tree");
        if (TreeRates.LogsRule == null && logs) unknown.Add("what its logs come with");
        return (logs ? "Time, Logs and Gold show -" : "Gold shows - where a cut can bring items that sell") +
            $": the window does not know {string.Join(", or ", unknown)}.";
    }

    /// <summary>
    /// The whole hour at a tree, as far as it is counted: the time of a hit, the XP, the tree's hit points and the cuts by
    /// the settled rule, the chances that change the hits a cut takes, the logs, what a hit and a cut can bring, the
    /// gold, the overgrowth on it now. nameOf gives the game's name of a chance, when it has been read (BonusNames).
    /// </summary>
    public static string Tooltip(TreeRates t, Func<string, string?> nameOf)
    {
        var lines = new List<string> { t.Category.Length > 0 ? $"{t.Name} ({t.Category})" : t.Name };
        if (t.Locked) lines.Add(t.Needs == "Locked" ? "Locked" : "Locked: " + t.Needs);
        if (t.Hidden) lines.Add("You hid this tree on the Woodcutting page.");
        if (t.OnPage) lines.Add("The tree the Woodcutting page shows.");
        if (!t.HasTime)
        {
            lines.Add("The game gives no time for a hit on this tree yet.");
            if (Click(t) is { } once) lines.AddRange(["", once]);
            return string.Join("\n", lines);
        }

        lines.Add($"One hit every {Seconds(t.HitSeconds)}: {Number(t.HitsPerHour)} hits an hour.");
        lines.Add($"Woodcutting XP: {Number(t.XpPerHour)} an hour, {Number(t.XpEach)} a hit.");
        lines.Add($"Hit points: {t.HitPoints:N0}, with your bonuses.");
        if (t.HitsPerCut is { } hits && t.CutsPerHour is { } cuts && TreeRates.HitsRule is { } rule)
            lines.Add($"A cut every {Number(hits)} hits on average, {How(rule.Source)}: {Number(cuts)} an hour.");
        if (t.HitChances.Count > 0)
        {
            // The game's own names, as the Woodcutting page lists them among a tree's bonuses.
            lines.Add(t.HitsPerCut != null ? "Counted in the hits a cut takes:" : "Your chances that change the hits a cut takes, not counted:");
            lines.AddRange(t.HitChances.Select(b => $"  {nameOf(b.Key) ?? BonusNames.Kind(b.Key)}: {Percent(b.Value)}"));
        }

        lines.Add("");
        if (t.Log != null) lines.Add(LogLine(t));
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
        // Only where a number of the tooltip has it in.
        bool logsDoubled = t.LogsPerHour != null, itemsDoubled = t.PerHit.Count + t.PerCut.Count > 0;
        if (t.Doubling > 0 && (logsDoubled || itemsDoubled))
            lines.Add($"The doubling chance ({Percent(t.Doubling)}) is counted in the {(logsDoubled ? itemsDoubled ? "logs and items" : "logs" : "items")}.");

        if (t.GoldPerHour is { } gold)
        {
            lines.Add("");
            double others = t.PerHitGoldPerHour + (t.PerCutGoldPerHour ?? 0);
            lines.Add(t.LogPrice != null && others > 0
                ? $"Gold: {Number(gold)} an hour, what the logs and items sell for; {Number(others)} of it from the other items."
                : $"Gold: {Number(gold)} an hour, what {(t.LogPrice != null ? "the logs sell" : "the items sell")} for.");
        }

        if (t.Effects.Count > 0)
        {
            lines.Add("");
            lines.Add("Overgrowth on this tree now, its bonuses in the numbers above:");
            lines.AddRange(t.Effects.Select(e => $"  {e.Name}, {e.StacksLeft:N0} {(e.StacksLeft == 1 ? "stack" : "stacks")} left"));
        }
        if (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (Click(t) is { } click) lines.AddRange(["", click]);
        return string.Join("\n", lines);
    }

    // The logs: what they come with and an hour of them, by the settled rule; otherwise how many come at a time.
    static string LogLine(TreeRates t)
    {
        if (TreeRates.LogsRule is not { } rule) return $"Logs: {t.LogName}, {t.LogQuantity:N0} at a time, with your bonuses.";
        string with = rule.Reading switch
        {
            LogsWith.EveryHit => "a hit",
            LogsWith.Cut => "a cut",
            _ => "for each hit point a hit takes",
        };
        return $"Logs: {t.LogName}, {t.LogQuantity:N0} {with}" + (t.LogsPerHour is { } logs ? $": {Number(logs)} an hour." : ".");
    }

    // What backs a rule, in plain words: the number is always worked out by the mod, from the tree's own numbers by a rule
    // the game states or one measured on a character's woodcutting (Mining Rates' code review of turn 2, fault 3).
    public static string How(RuleSource source) => source == RuleSource.Measured ? "worked out by a rule measured in the game" : "worked out by the game's own rule";

    // What a click on the line does, when it changes what the Woodcutting page shows; otherwise why it cannot show the
    // tree's card when the page has no room for it clear of this window (Mining Rates' review 4), and nothing when it is in
    // sight already. The sentence of no room is on two lines: on one it would be wider than Fishing Rates' widest.
    static string? Click(TreeRates t) => t.Clickable ? "Click to show this tree on the Woodcutting page. Woodcutting is not started." : t.Sight switch
    {
        CardSight.UnderWindow => "The Woodcutting page has no room for this tree's card beside this window:\ndrag the window aside.",
        CardSight.NoRoom => "The Woodcutting page has no room to show this tree's whole card.",
        _ => null,
    };

    // "  Bird Nest, 0.5% of hits: 7", with the number one brings when it is not one, and per hour where it is counted.
    static string Item(TreeRates.Drop d, string one, string many)
    {
        string each = d.Quantity == 1 ? "" : $" ({d.Quantity:N0} a {one})";
        return $"  {d.Name}, {Percent(d.Chance)} of {many}{each}" + (d.PerHour is { } perHour ? $": {Number(perHour)}" : "");
    }
}
