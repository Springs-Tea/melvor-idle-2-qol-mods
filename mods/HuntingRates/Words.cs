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
    /// How long a stock lasts, in whole minutes, as Fishing Rates writes how long its bait lasts (its Words.Whole, itself
    /// Recipe Rates' "Materials for 2h 15m"): the bank changes with every try, and a tooltip that ticks would be drawn again
    /// all the time.
    /// </summary>
    static string Whole(double seconds)
    {
        if (seconds >= 100 * 3600) return $"{seconds / 3600:N0}h";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m";
    }

    // "what is in the bank lasts 4h 3m", as Fishing Rates says it of its bait.
    static string Lasts(long inBank, double perHour) => inBank <= 0 ? "none is in the bank" : perHour <= 0 ? "none is used up"
        : inBank / perHour * 3600 < 60 ? "what is in the bank lasts less than a minute" : $"what is in the bank lasts {Whole(inBank / perHour * 3600)}";

    // What keeps the time of a catch from being counted for an animal that can be hunted, in the words of the note and of
    // the tooltip; null when it is counted.
    static string? Unknown(AnimalRates a) =>
        a.SecondsPerCatch != null ? null
        : AnimalRates.HuntRule == null ? "how surveying, tracking and trapping join into a catch"
        : a.TrapFailBonuses == true ? "what your trap's bonuses after a failed try do"
        : a.TrapFailBonuses == null ? "your trap's bonuses after a failed try, which could not be read"
        : a.EscapeAdds == null ? "how many tracking steps an escape adds"
        : a.SurveysMost == null ? SurveysUnknown
        : "the time of a catch";

    const string SurveysUnknown = "how many surveys a hunt takes";

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
        if (open.Any(a => a.TrapsPerHour == null)) dashes.Add("Traps");
        if (dashes.Count == 0) return "";
        var unknown = open.Select(Unknown).OfType<string>().Distinct().ToList();
        // XP alone: the range the animal's card writes on the Hunting page, read once the page has shown that card.
        if (open.Any(a => a.SecondsPerCatch != null && a.XpPerHour == null && a.XpRange == null))
            unknown.Add("the XP range of a catch, which the animal's card writes on the Hunting page");
        if (unknown.Count == 0) return "";
        string columns = dashes.Count == 1 ? dashes[0] + " shows" : string.Join(", ", dashes.Take(dashes.Count - 1)) + " and " + dashes[^1] + " show";
        string said = unknown.Count == 1 ? unknown[0] : string.Join(", ", unknown.Take(unknown.Count - 1)) + ", or " + unknown[^1];
        // How such a line gets its numbers, in one sentence (review 7, point 1), when a line lacks only what the page
        // writes: the surveys come from the page's panel of the animal selected, and nowhere else, the XP range from
        // the animal's card or that panel; the window reads both whenever the page is on the screen and keeps them from
        // then on (HuntingRatesMod.ReadPage). Selecting the animal shows both.
        bool fromPage = open.Any(a => Unknown(a) == SurveysUnknown || (a.SecondsPerCatch != null && a.XpPerHour == null && a.XpRange == null));
        string fill = fromPage ? " Select an animal once on the Hunting page and its line keeps what the page writes." : "";
        return $"{columns} - where the window does not know {said}.{fill} Hover a line for the game's own numbers of each step.";
    }

    /// <summary>
    /// The whole hour of an animal, held to what a player needs to trust it (review 6, point 11), in the order and the words
    /// of Fishing Rates' and Woodcutting Rates' tooltips: the links of a hunt and the catch they make; what an hour brings
    /// (the XP, the journal XP, the output, the rare species in one line, the traps and bait with how long the bank's last,
    /// the gold); what is not counted, each in one line, as Fishing Rates says it ("Not counted: your ..."); then what a
    /// click does. An animal not discovered yet gives away nothing but what the page's list shows of it: its name as the
    /// page writes it, its location and its lock (review 1, fault 9). names gives the game's name of a chance where it has
    /// been read (BonusNames); noTool the page's words for an animal that needs no tool.
    /// </summary>
    public static string Tooltip(AnimalRates a, BonusNames names, string? noTool) => Joined(TooltipLines(a, names, noTool));

#if DEVTOOLS
    /// <summary>Development: the tooltip's lines longer than Fishing Rates' longest, before they are wrapped.</summary>
    public static List<string> DevOverlong(AnimalRates a, BonusNames names, string? noTool) =>
        TooltipLines(a, names, noTool).SelectMany(l => l.Split('\n')).Where(l => l.Length > MostWide).ToList();

    /// <summary>Development: a line as the tooltip wraps it.</summary>
    public static List<string> DevWrapped(string line) => Wrapped(line).ToList();
#endif

    static List<string> TooltipLines(AnimalRates a, BonusNames names, string? noTool)
    {
        var lines = new List<string> { a.Location.Length > 0 ? $"{a.Name} ({a.Location})" : a.Name };
        if (a.Locked) lines.Add(a.Needs == "Locked" ? "Locked" : "Locked: " + a.Needs);
        if (a.OnPage) lines.Add("The animal the Hunting page shows.");
        if (a.Hunted) lines.Add("The animal you are hunting now.");
        if (!a.Discovered)
        {
            lines.Add("Not discovered yet.");
            AddClick(lines, a);
            return lines;
        }
        // The tool in the card's own words once the page has shown the card, as the page writes a tool it lacks too; for an
        // animal that needs none, the page's own words for that, without "Tool:" (code review 2).
        string? tool = a.ToolWords ?? a.Tool ?? noTool;
        if (tool is { Length: > 0 }) lines.Add(a.Tool == null ? tool.TrimEnd('.') + "." : $"Tool: {tool}.");
        if (!a.HasTime)
        {
            lines.Add("The game gives no time for a step of this hunt yet.");
            AddClick(lines, a);
            return lines;
        }

        lines.Add("");
        lines.Add(SurveyLine(a));
        string steps = a.StepsMin == a.StepsMax ? $"{a.StepsMin:N0}" : $"{a.StepsMin:N0} to {a.StepsMax:N0}";
        lines.Add($"Tracking: {steps} steps of {Range(a.StepMinSeconds, a.StepMaxSeconds)} each.");
        // The trapping chance as the page's panel writes it, and the chance to try again by the page's name for it.
        lines.Add($"Trapping: {Range(a.TrapMinSeconds, a.TrapMaxSeconds)} a try, {Percent(a.TrapChance)} success" +
            (a.TrapFailBonuses == true ? " now: your trap changes it after a failed try." : a.ReattemptChance > 0 ? $"; {names.Of(BonusNames.Reattempt)} {Percent(a.ReattemptChance)}." : "."));
        lines.Add(EscapeLine(a));
        lines.Add(a.SecondsPerCatch is { } time && a.CatchesPerHour is { } catches && AnimalRates.HuntRule is { } hunt
            ? $"A catch every {Seconds(time)} on average, {How(hunt.Source)}: {Number(catches)} an hour."
            : $"A catch: not counted, the window does not know {Unknown(a)}.");

        lines.Add("");
        lines.Add(XpLine(a));
        // What the middle of the XP and of the output rests on, until a watch at a higher animal level settles what
        // levels add (review 7, point 3).
        if (a.XpPerCatch != null || a.Products.Any(d => d.PerHour != null)) lines.Add(MiddleLine());
        if (a.JournalTrackingXp + a.JournalTrappingXp > 0)
            lines.Add(a.JournalXpPerHour is { } journal ? $"Journal XP: {Number(journal)} an hour, {a.JournalTrackingXp:N0} a tracking step and {a.JournalTrappingXp:N0} a catch."
                : $"Journal XP: {a.JournalTrackingXp:N0} a tracking step and {a.JournalTrappingXp:N0} a catch.");
        if (a.Products.Count > 0)
        {
            lines.Add("Every catch, a chance of:");
            lines.AddRange(a.Products.Select(d => Item(d, "catch", "catches")));
            // As Fishing Rates and Woodcutting Rates say it, only where a number of the tooltip has it in: the output's
            // hour, or its gold.
            if (a.Doubling > 0 && (a.Products.Any(d => d.PerHour != null) || a.CatchGold > 0))
                lines.Add($"The doubling chance ({Percent(a.Doubling)}) is counted in the output.");
        }
        if (RareLine(a) is { } rare) lines.Add(rare);
        lines.Add(TrapLine(a));
        if (a.Bait != null) lines.Add(BaitLine(a));
        if (a.GoldPerHour is { } gold) lines.Add($"Gold: {Number(gold)} an hour, what the output sells for.");
        else if (a.CatchGold > 0) lines.Add($"Gold: {Number(a.CatchGold)} for the output of one catch.");
        lines.AddRange(NotCounted(a, names));
        AddClick(lines, a);
        return lines;
    }

    // A rare species in one line: each species with its share of the rare ones, "(locked)" for one that cannot come yet
    // (its extras not counted), and what it brings besides: "a collectable", or the extra's own name (review 6, point 11).
    static string? RareLine(AnimalRates a)
    {
        if (a.Rare.Count == 0 || a.RareChance <= 0) return null;
        static string Brings(AnimalRates.Drop d) => d.Collectable ? d.Found ? $"{d.Name} (a collectable, found)" : "a collectable" : d.Name;
        var species = a.Rare.Select(s => $"{s.Name} {Percent(s.Share)}" + (s.Locked.Length > 0 ? " (locked)" : "") +
            (s.Extras.Count > 0 ? " with " + string.Join(" and ", s.Extras.Select(Brings)) : ""));
        return $"A rare species on {Percent(a.RareChance)} of catches: {string.Join(", ", species)}.";
    }

    // What the hour does not count, each in one line, the game's name of a chance where it has been read: the chance to
    // skip a step (never seen in a watch), the preservation chance (never seen to keep a trap or bait), what surveying
    // finds (never seen yet), what the animal's level adds to the average (the page's own words), and a hunt without a
    // trap, which no watch has seen (review 6, point 9).
    static IEnumerable<string> NotCounted(AnimalRates a, BonusNames names)
    {
        if (a.SkipChance > 0) yield return $"Not counted: your {names.Of(BonusNames.Skip)} ({Percent(a.SkipChance)}): no step was seen skipped.";
        if (a.Preservation > 0 && (a.TrapsPerHour is > 0 || a.BaitPerHour != null))
            yield return $"Not counted: your {names.Of(BonusNames.Preservation)} ({Percent(a.Preservation)}): no trap or bait was seen kept.";
        if (a.SurveyFinds.Count > 0 && AnimalRates.SurveyFindsRule == null)
            yield return $"Not counted: {string.Join(" and ", a.SurveyFinds.Select(d => $"{d.Name} ({Percent(d.Chance)})"))} from surveying, never seen yet.";
        if (a.JournalLevel is > 1 && a.XpPerHour != null)
            yield return $"Not counted: what animal level {a.JournalLevel} adds to the average XP and output.";
        if (a.Trapless && a.SecondsPerCatch != null) yield return "Not watched: a hunt without a trap, counted as one with a trap.";
    }

    // The surveys of a hunt as the page writes them: one survey's time, and from one to the most the page's surveying
    // range holds, until the tracks are found; the middle of that is what a catch counts. Kept from an earlier game, it
    // says so (review 7, point 1).
    static string SurveyLine(AnimalRates a)
    {
        if (a.SurveysMost is not { } most) return $"Surveying: {Seconds(a.SurveySeconds)} a survey; how many, the page writes when its panel shows it.";
        string upTo = Math.Abs(most - Math.Round(most)) < 0.01 ? $"{Math.Round(most):N0}" : Number(most);
        string line = most < 1.005 ? $"Surveying: {Seconds(a.SurveySeconds)}, one survey until the tracks are found"
            : $"Surveying: {Seconds(a.SurveySeconds)} a survey, 1 to {upTo} until the tracks are found";
        return a.SurveysRemembered ? line + ",\n  " + LastWrote + "." : line + ".";
    }

    // Said of what the page wrote in an earlier game, kept in the mod's own file (HuntingRatesMod.Reading).
    const string LastWrote = "as the Hunting page last wrote it";

    // What the middle of the XP range, and of the output's ranges, was measured on (AnimalRates.XpMiddleMeasuredOn).
    static string MiddleLine()
    {
        var m = AnimalRates.XpMiddleMeasuredOn;
        return $"  The middle of XP and output was measured on {m.Catches} catches of {m.Animal}, level {m.FromLevel} to {m.ToLevel}.";
    }

    // An escape: back to tracking with more steps to go, how many by the readings no watch has ruled out, and how many
    // escapes a catch takes where that is counted.
    static string EscapeLine(AnimalRates a)
    {
        string per = a.PerCatch is { } k ? $", {Number(k.Escapes)} escapes a catch" : "";
        if (a.EscapeAdds is { } adds) return $"An escape: back to tracking, with {adds:N0} more steps{per}.";
        if (a.EscapeReadings is not { Count: > 0 } readings) return "An escape: back to tracking, with more steps to go.";
        return $"An escape: back to tracking, with {readings.Min():N0} to {readings.Max():N0} more steps; how many here is not known yet.";
    }

    // The trap selected and an hour of it, as Fishing Rates writes its bait: the hour, and how long the bank's last.
    static string TrapLine(AnimalRates a)
    {
        if (a.Trapless) return "Hunted without a trap, as you chose for this animal: no trap is used.";
        if (a.Trap == null) return a.TrapType != null ? $"Trap: {a.TrapType}, none selected." : "Trap: none.";
        string name = a.Trap.DisplayName.GetPluralText(1);
        return a.TrapsPerHour is { } traps ? $"Trap: {name}, one a try: {Number(traps)} an hour; {Lasts(a.TrapsInBank, traps)}."
            : $"Trap: {name}, one a try; {a.TrapsInBank:N0} in the bank.";
    }

    // The bait and an hour of it while it is switched on; without a trap its use was never watched, so it is not counted.
    static string BaitLine(AnimalRates a)
    {
        string name = a.Bait!.DisplayName.GetPluralText(2);
        if (!a.UseBait) return $"Bait: {name}, not used for this animal.";
        if (a.BaitPerHour is { } bait) return $"Bait: {name}, one a try: {Number(bait)} an hour; {Lasts(a.BaitInBank, bait)}.";
        return a.Trapless ? $"Bait: {name}, used; without a trap, how much is not known yet." : $"Bait: {name}, one a try; {a.BaitInBank:N0} in the bank.";
    }

    // The lines joined, each line longer than Fishing Rates' longest (94 characters) wrapped at its words, the rest of it
    // indented two spaces more than it (Godot's tooltip does not wrap: review 3, a rare species' lock of 114 characters).
    // A line is cut at a space the fewest brackets deep that leaves it short enough, so that a lock in brackets is kept
    // whole before anything inside it is cut, and "(Step 1)" never is (review 4); of those, between the parts of a list
    // where it can, else at the last (Cut).
    const int MostWide = 94;
    static string Joined(IEnumerable<string> lines) => string.Join("\n", lines.SelectMany(l => l.Split('\n')).SelectMany(Wrapped));

    static IEnumerable<string> Wrapped(string line)
    {
        if (line.Length <= MostWide) { yield return line; yield break; }
        string indent = new(' ', line.Length - line.TrimStart(' ').Length + 2);
        string rest = line;
        while (rest.Length > MostWide)
        {
            int cut = Cut(rest, indent.Length);
            if (cut < 0) break;
            yield return rest[..cut];
            rest = indent + rest[(cut + 1)..].TrimStart(' ');
        }
        yield return rest;
    }

    // Where to cut a line too long: of its spaces after the indent and within MostWide, those the fewest brackets deep;
    // of those the last one after a comma or a semicolon, when it leaves at least half the room on the first line, so
    // that a list (the rare species, each with its share and what it brings) is cut between its parts, never inside one
    // (review 9, point 3: check-8 cut "Ashcrest Chicken" from its 44.44%); else the last of them. When no space is
    // within MostWide, the first space after it (a word longer than the room stays whole).
    static int Cut(string line, int indent)
    {
        int best = -1, bestDepth = int.MaxValue, clause = -1, depth = 0;
        for (int i = 0; i < line.Length && i <= MostWide; i++)
        {
            char c = line[i];
            if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if (c == ' ' && i > indent && depth <= bestDepth)
            {
                if (depth < bestDepth) clause = -1;
                best = i;
                bestDepth = depth;
                if (i >= MostWide / 2 && line[i - 1] is ',' or ';') clause = i;
            }
        }
        return clause >= 0 ? clause : best >= 0 ? best : line.IndexOf(' ', MostWide);
    }

    // The XP as Fishing Rates writes it ("Fishing XP: 8,470 an hour, 17 a catch."): the XP of a catch is the middle of the
    // range the animal's card writes on the Hunting page, and comes with a catch only (XpRule, measured: lesson 55). The
    // game keeps that range in no member an animal has, only in the card's view, so before the page has ever shown the
    // card for this character there is the game's XP before it, and no hour. A range kept from an earlier game says so.
    static string XpLine(AnimalRates a)
    {
        if (a.XpRange is not { } range || a.XpMiddle is not { } middle)
            return $"Hunting XP: {Number(a.XpEach)} before the range its card writes, not shown on the page yet.";
        string shown = NumberRange(a.XpEach * range.Low, a.XpEach * range.High);
        string kept = a.XpRangeRemembered ? ",\n  " + LastWrote : "";
        if (a.XpPerCatch is not { } xp) return $"Hunting XP: {shown}{(a.XpRangeRemembered ? kept : ", as the page writes it")}; when it is added is not known yet.";
        return a.XpPerHour is { } hour ? $"Hunting XP: {Number(hour)} an hour, {Number(xp)} a catch, the middle of {shown}{kept}."
            : $"Hunting XP: {Number(xp)} a catch, the middle of {shown}{kept}.";
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

    // "  Feathers, 100% of catches (47 a catch): 1,104", as Fishing Rates and Woodcutting Rates write an item, with the
    // number one brings when it is not one and an hour of it where that is counted; a collectable says whether the journal
    // has it. "Each time" while what the output and the finds come with is not settled: no unit is said that nothing backs
    // (code review 2).
    static string Item(AnimalRates.Drop d, string one, string many)
    {
        bool settled = AnimalRates.OutputRule != null;
        string each = d.Quantity == 1 ? "" : settled ? $" ({d.Quantity:N0} a {one})" : $" ({d.Quantity:N0} each time)";
        string collectable = d.Collectable ? d.Found ? ", a collectable, found" : ", a collectable" : "";
        return $"  {d.Name}, {Percent(d.Chance)}{(settled ? $" of {many}" : "")}{each}{collectable}" + (d.PerHour is { } perHour ? $": {Number(perHour)}" : "");
    }
}
