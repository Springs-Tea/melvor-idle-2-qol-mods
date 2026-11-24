using System;
using System.Collections.Generic;
using System.Linq;

namespace HuntingRates;

/// <summary>
/// How the numbers are written, and the words of a line's tooltip: Woodcutting Rates' Words
/// (mods/WoodcuttingRates/Words.cs), for animals. No line is longer than Fishing Rates' longest (94 characters):
/// Godot's tooltip does not wrap.
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

    // "2s to 4s", or "3s" when the game's range is one time.
    static string Range(double low, double high) => high - low < 0.005 ? Seconds(low) : $"{Seconds(low)} to {Seconds(high)}";

    // "12 to 18", or "15" when the range is one number.
    static string NumberRange(double low, double high) => high - low < 0.05 ? Number(low) : $"{Number(low)} to {Number(high)}";

    /// <summary>
    /// Why whole columns of the window show "-", said once, under the list, as Thieving Overview says why (OverviewView's
    /// note), or "" when no line of an animal that can be hunted has one; and where the game's own numbers are meanwhile.
    /// It says what the window does not know, never what the game does not say: nobody has read all the game's words.
    /// </summary>
    public static string Note(IEnumerable<AnimalRates> animals)
    {
        var open = animals.Where(a => !a.Locked && a.HasTime && a.Discovered).ToList();
        if (open.Count == 0) return "";
        var dashes = new List<string>();
        if (open.Any(a => a.SecondsPerCatch == null)) dashes.AddRange(["Time", "Catches"]);
        if (open.Any(a => a.XpPerHour == null)) dashes.Add("XP");
        if (open.Any(a => a.GoldPerHour == null)) dashes.Add("Gold");
        if (open.Any(a => !a.Trapless && a.TrapsPerHour == null)) dashes.Add("Traps");
        if (dashes.Count == 0) return "";
        var unknown = new List<string>();
        if (AnimalRates.ChainRule == null) unknown.Add("how surveying, tracking and trapping join into a catch");
        if (AnimalRates.XpRule == null) unknown.Add("when the XP is added");
        if (AnimalRates.OutputRule == null) unknown.Add("when the output comes");
        if (AnimalRates.UseRule == null) unknown.Add("when traps and bait are used up");
        string columns = dashes.Count == 1 ? dashes[0] + " shows" : string.Join(", ", dashes.Take(dashes.Count - 1)) + " and " + dashes[^1] + " show";
        string said = unknown.Count == 0 ? "the XP range the page writes for some animals"
            : unknown.Count == 1 ? unknown[0] : string.Join(", ", unknown.Take(unknown.Count - 1)) + ", or " + unknown[^1];
        return $"{columns} -: the window does not know {said} yet. Hover a line for the game's own numbers of each step.";
    }

    /// <summary>
    /// The game's numbers of an animal, grouped the way the Hunting page's panel groups them: the three steps of a hunt
    /// (its interval displays and the trapping chance), the XP, the output, the trap and the bait; then the catch and the
    /// gold as far as the window counts them, and what a click does. An animal not discovered yet gives away nothing but
    /// what the page's list shows of it: its name as the page writes it, its location and its lock (review 1, fault 9).
    /// names gives the game's name of a chance where it has been read (BonusNames); noTool the page's words for an animal
    /// that needs no tool.
    /// </summary>
    public static string Tooltip(AnimalRates a, BonusNames names, string? noTool)
    {
        var lines = new List<string> { a.Location.Length > 0 ? $"{a.Name} ({a.Location})" : a.Name };
        if (a.Locked) lines.Add(a.Needs == "Locked" ? "Locked" : "Locked: " + a.Needs);
        if (a.OnPage) lines.Add("The animal the Hunting page shows.");
        if (a.Hunted) lines.Add("The animal you are hunting now.");
        if (!a.Discovered)
        {
            lines.Add("Not discovered yet.");
            AddClick(lines, a);
            return string.Join("\n", lines);
        }
        // The tool in the card's own words once the page has shown the card, as the page writes a tool it lacks too; for an
        // animal that needs none, the page's own words for that, without "Tool:" (code review 2).
        string? tool = a.ToolWords ?? a.Tool ?? noTool;
        if (tool is { Length: > 0 }) lines.Add(a.Tool == null ? tool.TrimEnd('.') + "." : $"Tool: {tool}.");
        if (!a.HasTime)
        {
            lines.Add("The game gives no time for a step of this hunt yet.");
            AddClick(lines, a);
            return string.Join("\n", lines);
        }

        lines.Add("");
        lines.Add($"Surveying: {Seconds(a.SurveySeconds)} a survey.");
        string steps = a.StepsMin == a.StepsMax ? $"{a.StepsMin:N0}" : $"{a.StepsMin:N0} to {a.StepsMax:N0}";
        lines.Add($"Tracking: {steps} steps of {Range(a.StepMinSeconds, a.StepMaxSeconds)}, {Percent(a.TrackChance)} success each.");
        if (a.SkipChance > 0) lines.Add($"  {names.Of(BonusNames.Skip)}: {Percent(a.SkipChance)}");
        lines.Add($"Trapping: {Range(a.TrapMinSeconds, a.TrapMaxSeconds)} a try, {Percent(a.TrapChance)} success" +
            (a.Hunted ? " now: a failed try may change it." : "."));
        if (a.ReattemptChance > 0) lines.Add($"  {names.Of(BonusNames.Reattempt)}: {Percent(a.ReattemptChance)}");
        lines.Add(a.SecondsPerCatch is { } time && a.CatchesPerHour is { } catches && AnimalRates.ChainRule is { } chain
            ? $"A catch every {Seconds(time)} on average, {How(chain.Source)}: {Number(catches)} an hour."
            : "A catch: not counted, the window does not know how these steps join.");

        lines.Add("");
        lines.Add(XpLine(a));
        if (a.JournalTrackingXp + a.JournalTrappingXp > 0)
            lines.Add($"Journal XP, base: {a.JournalTrackingXp:N0} tracking, {a.JournalTrappingXp:N0} trapping.");
        if (a.Products.Count > 0)
        {
            lines.Add(AnimalRates.OutputRule == null ? "Output, each at its chance (on a catch: the window's reading):" : "Every catch, a chance of:");
            lines.AddRange(a.Products.Select(d => Item(d, "catch", "catches")));
        }
        if (a.Rare.Count > 0 && a.RareChance > 0)
        {
            lines.Add($"A rare species, {Percent(a.RareChance)}:");
            foreach (AnimalRates.Species s in a.Rare)
            {
                lines.Add($"  {s.Name}, {Percent(s.Share)} of them" + (s.Locked.Length > 0 ? $" (locked: {s.Locked})" : "") + (s.Extras.Count > 0 ? ", with:" : ""));
                lines.AddRange(s.Extras.Select(d => "  " + Item(d, "catch", "catches")));
            }
        }
        if (a.SurveyFinds.Count > 0)
        {
            lines.Add("Surveying can find:");
            lines.AddRange(a.SurveyFinds.Select(d => Item(d, "survey", "surveys")));
        }
        if (a.Doubling > 0 && a.Products.Count > 0)
            lines.Add($"{names.Of(BonusNames.Doubling)}: {Percent(a.Doubling)}, counted in the output only (the window's reading).");

        lines.Add("");
        lines.Add(a.Trapless ? "Hunted without a trap, as you chose for this animal."
            : a.Trap != null ? $"Trap: {a.Trap.DisplayName.GetPluralText(1)}{(a.TrapType != null ? $" ({a.TrapType})" : "")}, {a.TrapsInBank:N0} in the bank" +
                (a.TrapsPerHour is { } traps ? $", {Number(traps)} an hour." : ".")
            : a.TrapType != null ? $"Trap: {a.TrapType}, none selected." : "Trap: none.");
        if (a.Bait != null)
            lines.Add($"Bait: {a.Bait.DisplayName.GetPluralText(2)}, " + (a.UseBait ? $"used, {a.BaitInBank:N0} in the bank" +
                (a.BaitPerHour is { } bait ? $", {Number(bait)} an hour." : ".") : "not used for this animal."));
        if (a.Preservation > 0 && AnimalRates.UseRule == null)
            lines.Add($"  {names.Of(BonusNames.Preservation)}: {Percent(a.Preservation)}, what it keeps is not known yet");

        if (a.GoldPerHour is { } gold) lines.AddRange(["", $"Gold: {Number(gold)} an hour, what the output, rare extras and finds sell for."]);
        else if (a.CatchGold > 0) lines.AddRange(["", $"Gold: {Number(a.CatchGold)} for the output of one catch, by the window's reading."]);
        if (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        AddClick(lines, a);
        return string.Join("\n", lines);
    }

    // The XP as the page writes it (its XP tag, a range once the page has shown it) and, while no watch has settled it,
    // that when it is added is not known (lesson 55); by the settled rule, the XP of a catch and of an hour.
    static string XpLine(AnimalRates a)
    {
        if (a.XpRange is not { } range || a.XpMiddle is not { } middle)
            return $"XP: {Number(a.XpEach)} Hunting XP before its range, which the page has not shown yet.";
        string shown = NumberRange(a.XpEach * range.Low, a.XpEach * range.High);
        return a.XpPerCatch is { } xp
            ? $"Hunting XP: {shown} a catch, {Number(xp)} in the middle" + (a.XpPerHour is { } hour ? $"; {Number(hour)} an hour." : ".")
            : $"XP: {shown} Hunting XP, as the page writes it; when it is added is not known yet.";
    }

    // What backs a rule, in plain words: the number is always worked out by the mod, from the animal's own numbers by a
    // rule the game states or one measured on a character's hunting.
    public static string How(RuleSource source) => source == RuleSource.Measured ? "worked out by a rule measured in the game" : "worked out by the game's own rule";

    // What a click on the line does, by the rule of HuntingRatesMod.ShowOnPage (Fishing Rates' words while nothing runs);
    // otherwise why it does nothing: the hunt that runs, the card's button the page keeps disabled, or no room for the
    // card beside this window. The panel of the animal selected, under the window, is said on its own line.
    static void AddClick(List<string> lines, AnimalRates a)
    {
        var said = new List<string>();
        if (a.Clickable)
            said.Add(a.HuntRuns ? "Click to show it on the Hunting page. While you hunt, nothing is selected."
                : a.Click == AnimalRates.Opens.Animal ? "Click to open this animal on the Hunting page. Hunting is not started."
                : $"Click to open {a.Location} on the Hunting page. Hunting is not started.");
        else if (a.HuntBlocks) said.Add("While you hunt, a click selects nothing on the Hunting page.");
        else if (a.CardBlocked) said.Add("The Hunting page does not let this animal be selected now.");
        else if (a.Sight is CardSight.UnderWindow) said.Add("The Hunting page has no room for this animal's card beside this window:\ndrag the window aside.");
        else if (a.Sight is CardSight.NoRoom) said.Add("The Hunting page has no room to show this animal's whole card.");
        if (a.PanelCovered && a.Click == AnimalRates.Opens.Animal && !a.HuntRuns) said.Add("The page's panel of the animal selected lies under this window.");
        if (said.Count > 0) lines.AddRange(["", string.Join("\n", said)]);
    }

    // "  Raw Hare Meat, 50% (2 each time): 120", with the number one brings when it is not one, and per hour where it is
    // counted; a collectable says whether the journal has it. "Each time" while what the output and the finds come with is
    // the window's reading (OutputRule not settled): no unit is said that nothing backs (code review 2).
    static string Item(AnimalRates.Drop d, string one, string many)
    {
        string each = d.Quantity == 1 ? "" : AnimalRates.OutputRule == null ? $" ({d.Quantity:N0} each time)" : $" ({d.Quantity:N0} a {one})";
        string collectable = d.Collectable ? d.Found ? ", a collectable, found" : ", a collectable" : "";
        return $"  {d.Name}, {Percent(d.Chance)}{each}{collectable}" + (d.PerHour is { } perHour ? $": {Number(perHour)} an hour" : "");
    }
}
