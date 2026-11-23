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

    /// <summary>
    /// Why whole columns of the window show "-", said once, under the list, as Thieving Overview says why (OverviewView's
    /// note), or "" when no line of an animal that can be hunted has one. It says what the window does not know, never
    /// what the game does not say: nobody has read all the game's words.
    /// </summary>
    public static string Note(IEnumerable<AnimalRates> animals)
    {
        var open = animals.Where(a => !a.Locked && a.HasTime).ToList();
        if (open.Count == 0) return "";
        var unknown = new List<string>();
        if (AnimalRates.ChainRule == null) unknown.Add("how surveying, tracking and trapping join into a catch");
        if (open.Any(a => a.XpPerCatch == null)) unknown.Add("the XP of a catch within the range the page writes");
        if (AnimalRates.UseRule == null) unknown.Add("when traps and bait are used up");
        if (unknown.Count == 0) return "";
        var dashes = new List<string>();
        if (open.Any(a => a.SecondsPerCatch == null)) dashes.AddRange(["Time", "Catches"]);
        if (open.Any(a => a.XpPerHour == null)) dashes.Add("XP");
        if (open.Any(a => a.GoldPerHour == null)) dashes.Add("Gold");
        if (open.Any(a => !a.Trapless && a.TrapsPerHour == null)) dashes.Add("Traps");
        if (dashes.Count == 0) return "";
        string columns = dashes.Count == 1 ? dashes[0] + " shows" : string.Join(", ", dashes.Take(dashes.Count - 1)) + " and " + dashes[^1] + " show";
        string said = unknown.Count == 1 ? unknown[0] : string.Join(", ", unknown.Take(unknown.Count - 1)) + ", or " + unknown[^1];
        return $"{columns} -: the window does not know {said}. Hover a line for the game's numbers of each step.";
    }

    /// <summary>
    /// The whole hour at an animal, as far as it is counted: the game's numbers of every link of a catch, the catch by the
    /// settled chain, the XP, what a catch and a survey bring, the trap, the bait, the gold. names gives the game's name
    /// of a chance where it has been read (BonusNames); noTool the page's words for an animal that needs no tool.
    /// </summary>
    public static string Tooltip(AnimalRates a, BonusNames names, string? noTool)
    {
        var lines = new List<string> { a.Location.Length > 0 ? $"{a.Name} ({a.Location})" : a.Name };
        if (a.Locked) lines.Add(a.Needs == "Locked" ? "Locked" : "Locked: " + a.Needs);
        if (a.OnPage) lines.Add("The animal the Hunting page shows.");
        if (a.Hunted) lines.Add("The animal you are hunting now.");
        if (!a.Discovered) lines.Add("Not discovered yet.");
        if (a.Tool != null) lines.Add($"Tool: {a.Tool}.");
        else if (noTool is { Length: > 0 }) lines.Add(noTool.TrimEnd('.') + ".");
        if (!a.HasTime)
        {
            lines.Add("The game gives no time for a step of this hunt yet.");
            if (Click(a) is { } once) lines.AddRange(["", once]);
            return string.Join("\n", lines);
        }

        // Every link, in the game's numbers.
        lines.Add("");
        lines.Add($"Surveying: one survey every {Seconds(a.SurveySeconds)}, until the tracks are found.");
        string steps = a.StepsMin == a.StepsMax ? $"{a.StepsMin:N0}" : $"{a.StepsMin:N0} to {a.StepsMax:N0}";
        lines.Add($"Tracking: {steps} steps of {Range(a.StepMinSeconds, a.StepMaxSeconds)} each, {Percent(a.TrackChance)} success a step.");
        if (a.SkipChance > 0) lines.Add($"  {names.Of(BonusNames.Skip)}: {Percent(a.SkipChance)}");
        lines.Add($"Trapping: {Range(a.TrapMinSeconds, a.TrapMaxSeconds)} a try, {Percent(a.TrapChance)} success.");
        if (a.ReattemptChance > 0) lines.Add($"  {names.Of(BonusNames.Reattempt)}: {Percent(a.ReattemptChance)}");
        lines.Add(a.SecondsPerCatch is { } time && a.CatchesPerHour is { } catches && AnimalRates.ChainRule is { } chain
            ? $"A catch every {Seconds(time)} on average, {How(chain.Source)}: {Number(catches)} an hour."
            : "A catch: not counted, the window does not know how these steps join into one.");

        // The XP, as the page writes it: a range, and ours in its middle.
        if (a.XpRange is { } range && a.XpPerCatch is { } xp)
        {
            double low = a.XpEach * range.Low, high = a.XpEach * range.High;
            string each = high - low < 0.05 ? Number(xp) : $"{Number(low)} to {Number(high)}, {Number(xp)} in the middle,";
            lines.Add(a.XpPerHour is { } perHour ? $"Hunting XP: {each} a catch; {Number(perHour)} an hour." : $"Hunting XP: {each} a catch.");
        }
        else lines.Add($"Hunting XP: {Number(a.XpEach)} a catch before its own range, which the page has not shown yet.");
        if (a.SurveyXpBase > 0) lines.Add($"Surveying XP: {Number(a.SurveyXpBase)} a survey at this location, base, not counted.");
        if (a.JournalTrackingXp + a.JournalTrappingXp > 0)
            lines.Add($"Journal XP of this animal, base: {a.JournalTrackingXp:N0} tracking, {a.JournalTrappingXp:N0} trapping" +
                (a.JournalLevel is { } level ? $" (journal level {level:N0})." : "."));

        lines.Add("");
        if (a.Products.Count > 0)
        {
            lines.Add("Every catch, a chance of:");
            lines.AddRange(a.Products.Select(d => Item(d, "catch", "catches")));
        }
        if (a.Rare.Count > 0 && a.RareChance > 0)
        {
            lines.Add($"A rare species on {Percent(a.RareChance)} of catches:");
            foreach (AnimalRates.Species s in a.Rare)
            {
                lines.Add($"  {s.Name}, {Percent(s.Share)} of the rare ones" + (s.Locked.Length > 0 ? $" (locked: {s.Locked})" : "") + (s.Extras.Count > 0 ? ", with:" : ""));
                lines.AddRange(s.Extras.Select(d => "  " + Item(d, "catch", "catches")));
            }
        }
        if (a.SurveyFinds.Count > 0)
        {
            lines.Add("Every survey, a chance of:");
            lines.AddRange(a.SurveyFinds.Select(d => Item(d, "survey", "surveys")));
        }
        if (a.Doubling > 0 && a.Products.Count > 0)
            lines.Add($"The {names.Of(BonusNames.Doubling).ToLowerInvariant()} ({Percent(a.Doubling)}) is counted in a catch's products only.");

        lines.Add("");
        lines.Add(a.Trapless ? "Hunted without a trap, as you chose for this animal."
            : a.Trap != null ? $"Trap: {a.Trap.DisplayName.GetPluralText(1)}{(a.TrapType != null ? $" ({a.TrapType})" : "")}, {a.TrapsInBank:N0} in the bank" +
                (a.TrapsPerHour is { } traps ? $", {Number(traps)} an hour." : ".")
            : a.TrapType != null ? $"Trap: {a.TrapType}, none selected." : "Trap: none.");
        if (a.Bait != null)
            lines.Add($"Bait: {a.Bait.DisplayName.GetPluralText(2)}, " + (a.UseBait ? $"used, {a.BaitInBank:N0} in the bank" +
                (a.BaitPerHour is { } bait ? $", {Number(bait)} an hour." : ".") : "not used for this animal."));
        if (a.Preservation > 0 && AnimalRates.UseRule == null) lines.Add($"  {names.Of(BonusNames.Preservation)}: {Percent(a.Preservation)}, not counted");

        if (a.GoldPerHour is { } gold) lines.AddRange(["", $"Gold: {Number(gold)} an hour, what the products, rare extras and finds sell for."]);
        else if (a.CatchGold > 0) lines.AddRange(["", $"Gold: {Number(a.CatchGold)} a catch, what its products and rare extras sell for."]);
        if (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (Click(a) is { } click) lines.AddRange(["", click]);
        return string.Join("\n", lines);
    }

    // What backs a rule, in plain words: the number is always worked out by the mod, from the animal's own numbers by a
    // rule the game states or one measured on a character's hunting.
    public static string How(RuleSource source) => source == RuleSource.Measured ? "worked out by a rule measured in the game" : "worked out by the game's own rule";

    // What a click on the line does, when it changes what the Hunting page shows (Fishing Rates' words); otherwise why
    // the animal's card cannot be shown clear of this window, and nothing when it is in sight already. The panel of the
    // animal selected, under the window, is said on its own line: a click's animal shows there.
    static string? Click(AnimalRates a)
    {
        var said = new List<string>();
        if (a.Clickable)
            said.Add(a.Click == AnimalRates.Opens.Animal ? "Click to open this animal on the Hunting page. Hunting is not started."
                : $"Click to open {a.Location} on the Hunting page. Hunting is not started.");
        else if (a.Sight is CardSight.UnderWindow) said.Add("The Hunting page has no room for this animal's card beside this window:\ndrag the window aside.");
        else if (a.Sight is CardSight.NoRoom) said.Add("The Hunting page has no room to show this animal's whole card.");
        if (a.PanelCovered && a.Click == AnimalRates.Opens.Animal) said.Add("The page's panel of the animal selected lies under this window.");
        return said.Count == 0 ? null : string.Join("\n", said);
    }

    // "  Raw Hare Meat, 50% of catches (2 a catch): 120", with the number one brings when it is not one, and per hour
    // where it is counted; a collectable says whether the journal has it.
    static string Item(AnimalRates.Drop d, string one, string many)
    {
        string each = d.Quantity == 1 ? "" : $" ({d.Quantity:N0} a {one})";
        string collectable = d.Collectable ? d.Found ? ", a collectable, found" : ", a collectable" : "";
        return $"  {d.Name}, {Percent(d.Chance)} of {many}{each}{collectable}" + (d.PerHour is { } perHour ? $": {Number(perHour)}" : "");
    }
}
