using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace CombatSim;

/// <summary>What a rate is counted against: an hour of game time, a kill, or a whole run of a dungeon.</summary>
enum Per { Hour, Kill, Run }

/// <summary>
/// A rate the simulator shows: one of the quantities of <see cref="SimRanges"/>, per hour, per kill or per run. A
/// column of the list and a line of the details name the rate they show, so that its range can be found.
/// </summary>
sealed record SimRate(string Of, Per Per)
{
    public static SimRate PerHour(string of) => new(of, Per.Hour);
    public static SimRate PerKill(string of) => new(of, Per.Kill);
    public static SimRate PerRun(string of) => new(of, Per.Run);
}

/// <summary>
/// The 95% range of a rate, in the rate's own units. <see cref="Even"/>: the value plus or minus the same amount,
/// shown as "± 2%". Otherwise from <see cref="Low"/> to <see cref="High"/>, shown as "0.62 to 8.77", or "up to 3.69"
/// when the value is 0: the range of a count, or one that was cut at 0. 0 to 0 is a quantity that was 0 all fight
/// and is no count: there is nothing to say about it, and it is shown without a range.
/// </summary>
readonly record struct SimRange(double Value, double Low, double High, bool Even)
{
    /// <summary>Half the range as a share of the value: 0.02 is "± 2%". Infinity for a value of 0.</summary>
    public double Share => Value != 0 ? (High - Low) / 2 / Math.Abs(Value) : double.PositiveInfinity;

    public bool Overlaps(SimRange other) => Low <= other.High && other.Low <= High;
}

/// <summary>
/// A rate of two fights of a compare (issue #57), A's less B's: their <see cref="Difference"/>; the halves of the two own
/// ranges; the <see cref="Share"/> of their luck that is left in the difference, measured from <see cref="Parts"/> parts
/// (SimParts.Apart); and how far luck could have put them apart, <see cref="Reach"/>. Apart is better or worse only beyond
/// it (SimRanges.Apart).
/// </summary>
readonly record struct SimLuck(double Difference, double HalfA, double HalfB, double Share, int Parts)
{
    /// <summary>
    /// How much of their luck is taken as left: the share raised by how far chance alone could have lowered it
    /// (<see cref="SimRanges.Noise"/>), never more than all of it. 1 where the share was measured from too few parts.
    /// </summary>
    public double Left => Share * SimRanges.Noise(Parts) is var left && left < 1 ? left : 1;

    /// <summary>The two own halves added, as two ranges laid side by side ask, times the luck left: never more than that bar.</summary>
    public double Reach => (HalfA + HalfB) * Left;
}

/// <summary>One number of a setup against the same number of another (<see cref="SimRanges.Judge"/>).</summary>
enum Verdict
{
    /// <summary>Under half a percent apart.</summary>
    Same,
    /// <summary>
    /// The two ranges overlap, or for two fights of a compare the difference is within the reach of luck
    /// (<see cref="SimLuck"/>): a longer fight, or other dice, could turn it round.
    /// </summary>
    Luck,
    Better,
    Worse,
    /// <summary>Apart, for a number of which neither more nor less is better.</summary>
    Differs,
}

/// <summary>
/// How far a fight's rates could be off (issue #5), kept with its result and in the history file: what the parts of
/// the fight (<see cref="SimParts"/>) said, worked out once when the fight ended. Only these few numbers are kept, not
/// the parts themselves, so an earlier scan's file grows by little.
/// </summary>
sealed class SimSpread
{
    /// <summary>
    /// Quantity (<see cref="SimRanges.Kills"/> and the rest) -> half of the even 95% range of its rate per hour, per kill
    /// and per run, in that order and in the rate's own units, to three figures; null where the rate is not shown or
    /// could not be told. A quantity that was 0 all fight is left out.
    /// </summary>
    public Dictionary<string, double?[]>? Half;
    /// <summary>The counts that some part of the fight had none of: their ranges are Garwood's too (<see cref="SimRanges.Range"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Sparse;
    /// <summary>Half of the even 95% range per hour of each item of the result's Loot, then of its Used, in their order.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<double?>? Items;
    /// <summary>How many times the character lost hitpoints: the count behind the damage taken (SimRanges.Judge).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Hits;
    /// <summary>
    /// Compare (issue #57): how much of the luck of this fight and of each of the other setup's fights of the same enemy,
    /// which were fought with the same dice, is left in the difference of their rates (SimEngine.Pair). Kept with the fight
    /// of the two that ended last. Null in a scan of one setup and in scans made before.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SimApart>? Apart;
}

/// <summary>
/// Two fights of a compare (issue #57): how much of their luck is left in the difference of their rates
/// (SimParts.Apart), which lowers the bar for "better" by the luck they met alike (SimRanges.Apart).
/// </summary>
sealed class SimApart
{
    /// <summary>
    /// The other fight: its stance in Best for (its SimTarget.StanceId; null in Fights, where a stance is part of the setup),
    /// and its RealMilliseconds, which tells it from any other result of the same enemy, as the list tells its lines apart
    /// (SimView.LineKey). Its enemy is this fight's and its setup the other one, so neither is written again.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stance;
    public double Ms;
    /// <summary>How many parts the shares were measured from (SimParts.Apart): the fewer, the more chance moves them (SimRanges.Noise).</summary>
    public int Parts;
    /// <summary>
    /// Quantity -> the share of the luck left per hour, per kill and per run, in the order of <see cref="SimSpread.Half"/>, to
    /// two figures: 0 when the two met all of it alike, about 1 when they met none of it alike; null where it could not be told.
    /// </summary>
    public Dictionary<string, double?[]> Left = new();
}

/// <summary>
/// How sure a rate is (issue #5). This is statistics, not a rule of the game: no number of the game is worked out
/// here, only how far a rate the game's own combat produced could be off.
/// <para>
/// The method is "batch means": a fight is cut into parts of even game time (<see cref="SimParts.Count"/> of them),
/// and how much the parts differ says how far the whole could be from the true rate. For a rate y per x (per hour:
/// x is each part's game time; per kill: its kills; per run: its runs), with k parts:
/// R = Σy / Σx, and the 95% range is R ± t · √(k · Σ(yᵢ − R·xᵢ)² / (k − 1)) / Σx, the ratio form of the standard
/// error, where t is Student's t at 97.5% with k − 1 degrees of freedom (<see cref="T975"/>).
/// </para>
/// <para>
/// A count (kills, deaths, runs, food, ammo, runes, prayer points) that some part had none of is too rare for the
/// parts alone: it also gets Garwood's exact Poisson range of its total n over the hours, kills or runs,
/// χ²(2.5%; 2n)/2 to χ²(97.5%; 2n + 2)/2 (<see cref="CountRange"/>), and shows the wider of the two. So a count of 0
/// has a range too ("0 deaths in an hour" is not "never"), and 0 against a handful is luck, not a win. Any other
/// quantity that was 0 all fight (no damage taken) has the range 0 to 0 and shows none; against another setup it is
/// judged by the count behind it, unless it is a skill's XP the setup does not train at all, a real 0
/// (<see cref="Basis"/>).
/// </para>
/// <para>
/// Two fights of a compare (issue #57) were fought with the same dice, kept in step. Luck they met alike cannot put one
/// ahead of the other, so A against B is judged as strictly as two ranges side by side, less the luck they shared: apart
/// when their difference is more than the two own halves added, times the share of their luck that is left in the
/// difference (<see cref="Apart(SimResult, SimResult, SimRate)"/>, SimParts.Apart). That share is measured from the two
/// fights and could come out low by chance alone, so it is first raised by its own noise (<see cref="Noise"/>): two
/// fights that met nothing alike are coloured by luck as often as by two ranges side by side, and the rule never greys
/// what that rule colours. Each one's own range stays as it is.
/// </para>
/// </summary>
static class SimRanges
{
    // ---- the quantities a fight's parts count (SimParts.Totals) ----------------------------------------------
    public const string Kills = "kills", Deaths = "deaths", Runs = "runs", Food = "food", Damage = "damage",
        Healed = "healed", Coins = "coins", Loot = "loot", Cost = "cost", Runes = "runes", Ammo = "ammo", Prayer = "prayer";
    /// <summary>Not counted but worked out from the others: game time, coins and loot, gold less supplies, all XP.</summary>
    public const string Seconds = "seconds", Gold = "gold", NetGold = "netGold", Xp = "xp";
    const string XpPrefix = "xp:", BaseNamespace = "MelvorBase:";

    /// <summary>The XP of one skill: "xp:Defence" for the game's own skills, short in the history file.</summary>
    public static string XpOf(string skillId) =>
        XpPrefix + (skillId.StartsWith(BaseNamespace, StringComparison.Ordinal) ? skillId[BaseNamespace.Length..] : skillId);
    public static bool IsXp(string quantity) => quantity.StartsWith(XpPrefix, StringComparison.Ordinal);

    /// <summary>Things that happen one at a time: a count of 0 is not "never" (see the class).</summary>
    static readonly HashSet<string> Counts = new() { Kills, Deaths, Runs, Food, Ammo, Runes, Prayer };
    public static bool IsCount(string quantity) => Counts.Contains(quantity);

    /// <summary>
    /// Whether the window shows this quantity per hour, per kill or per run (the columns and the details), so that a
    /// result keeps only the halves it can be asked for.
    /// </summary>
    public static bool Shown(string quantity, Per per) => per switch
    {
        Per.Hour => quantity != Seconds,
        Per.Kill => quantity is Seconds or Damage or Food or Runes or Ammo or Prayer or Gold or Loot or Healed or Xp || IsXp(quantity),
        _ => quantity is Seconds or Deaths or Gold,
    };

    // Student's t at 97.5% for 1 to 30 degrees of freedom, from scipy.stats.t.ppf(0.975, df), rounded to four places.
    // A fight has at most 19 parts and 18 degrees of freedom (SimParts); past 30 the normal 1.96 is close enough.
    static readonly double[] T975 =
    {
        12.7062, 4.3027, 3.1824, 2.7764, 2.5706, 2.4469, 2.3646, 2.306, 2.2622, 2.2281,
        2.201, 2.1788, 2.1604, 2.1448, 2.1314, 2.1199, 2.1098, 2.1009, 2.093, 2.086,
        2.0796, 2.0739, 2.0687, 2.0639, 2.0595, 2.0555, 2.0518, 2.0484, 2.0452, 2.0423,
    };
    // The normal distribution's 97.5% point, scipy.stats.norm.ppf(0.975) = 1.959964.
    const double Z975 = 1.959964;
    // 2.5% on each side: a 95% range.
    const double Tail = 0.025;
    // Two numbers less than half a percent apart are the same, as "Compared with" always said.
    const double SameShare = 0.005;

    static double T(int degrees) => degrees >= 1 && degrees <= T975.Length ? T975[degrees - 1] : Z975;

    /// <summary>Each part's game time in hours: all as long as the first, the last what was left.</summary>
    public static double[] Durations(int count, int ticks, double hours)
    {
        double part = ticks / (3600.0 * SimEngine.TicksPerSecond);
        var durations = new double[count];
        for (int i = 0; i < count; i++) durations[i] = i < count - 1 ? part : Math.Max(0, hours - (count - 1) * part);
        return durations;
    }

    /// <summary>
    /// Half the 95% range of Σy / Σx over the parts, in units of y per x; null with fewer than two parts or nothing
    /// to divide by. Exact even when the parts are not all as long: the ratio form weighs each by its x.
    /// </summary>
    public static double? HalfWidth(IReadOnlyList<double> y, IReadOnlyList<double> x)
    {
        int k = y.Count;
        if (k < 2 || x.Count != k) return null;
        double sumX = 0, sumY = 0;
        for (int i = 0; i < k; i++) { sumX += x[i]; sumY += y[i]; }
        if (sumX <= 0) return null;
        double ratio = sumY / sumX, squares = 0;
        for (int i = 0; i < k; i++) squares += (y[i] - ratio * x[i]) * (y[i] - ratio * x[i]);
        return T(k - 1) * Math.Sqrt(squares / (k - 1) * k) / sumX;
    }

    /// <summary>
    /// Half the 95% range of Σa / Σxa − Σb / Σxb, for two fights cut into parts at the same kills or the same ticks (issue
    /// #57, SimParts.Apart). The same method: each part's term is its share of the one rate, (aᵢ − Rₐ·xaᵢ) / Σxa, less its
    /// share of the other, and the range is t · √(k · Σ(termᵢ)² / (k − 1)). Where the two met the same luck in a part, the
    /// two shares move together and cancel; where they did not, both count, and two fights with nothing in common come out
    /// at about the root of the sum of the squares of their own halves. With nothing of b, exactly <see cref="HalfWidth(IReadOnlyList{double}, IReadOnlyList{double})"/>
    /// of a; never more than the two own halves added. Null with fewer than two parts, parts that do not pair, or nothing
    /// to divide by.
    /// </summary>
    public static double? HalfWidth(IReadOnlyList<double> a, IReadOnlyList<double> xa, IReadOnlyList<double> b, IReadOnlyList<double> xb)
    {
        int k = a.Count;
        if (k < 2 || xa.Count != k || b.Count != k || xb.Count != k) return null;
        double sumXa = 0, sumA = 0, sumXb = 0, sumB = 0;
        for (int i = 0; i < k; i++) { sumXa += xa[i]; sumA += a[i]; sumXb += xb[i]; sumB += b[i]; }
        if (sumXa <= 0 || sumXb <= 0) return null;
        double ra = sumA / sumXa, rb = sumB / sumXb, squares = 0;
        for (int i = 0; i < k; i++)
        {
            double term = (a[i] - ra * xa[i]) / sumXa - (b[i] - rb * xb[i]) / sumXb;
            squares += term * term;
        }
        return T(k - 1) * Math.Sqrt(squares / (k - 1) * k);
    }

    /// <summary>
    /// Three figures, for the history file: the display writes at most two (± 7.9%), and the halves themselves are
    /// far less sure than that (with ten parts, about a quarter either way). The share of luck left of a compare keeps
    /// two (SimParts.Apart): it only scales a half, and is less sure still.
    /// </summary>
    public static double? Figures(double? value, int figures = 3)
    {
        if (value is not { } v || !double.IsFinite(v) || v < 0) return null;
        return v == 0 ? 0 : Math.Round(v, Math.Clamp(figures - 1 - (int)Math.Floor(Math.Log10(v)), 0, 15));
    }

    /// <summary>
    /// How far chance alone can lower a share of luck left (SimParts.Apart) measured from <paramref name="parts"/> parts,
    /// as the factor that takes its low point back to 1. Two fights that met no luck alike have a share of about 1, but
    /// measured: over k parts its square spreads by 1/√k (half of it is Beta((k − 1)/2, (k − 1)/2) for two fights of equal
    /// spread, and spreads less for two of unequal spread), and in that model it comes out below 1 − 1.96/√k in one such
    /// pair in forty. Measured on two models of the parts, with 10 to 64 of them, gold keeps to that. Kills, XP and kill
    /// time come out low more often where a part holds few kills: in about one pair in fifteen with parts of one kill
    /// (fights of fewer than 64 kills), one in twenty-five with parts of two (fewer than 128) and one in thirty with parts
    /// of four. Both fights end at the same tick inside a kill not yet finished, and that last part reads as luck met
    /// alike. Times this factor, their share lowers the bar in few pairs and by little, so they are coloured by luck about
    /// as often as by two ranges side by side, within that rule's own noise; a share that the luck they met alike took far
    /// below 1 still lowers the bar by most of that. Infinity, nothing lowered, below 4 parts.
    /// </summary>
    public static double Noise(int parts)
    {
        double low = parts > 0 ? 1 - Z975 / Math.Sqrt(parts) : 0;
        return low > 0 ? 1 / Math.Sqrt(low) : double.PositiveInfinity;
    }

    /// <summary>
    /// The range of a rate. <paramref name="total"/> gives the total of any quantity but <see cref="Seconds"/> over the
    /// whole fight, <paramref name="half"/> the half of a rate's even range, <paramref name="sparse"/> whether some part
    /// had none of a count. Null when there is nothing to divide by or nothing to tell.
    /// </summary>
    public static SimRange? Range(SimRate rate, Func<string, double> total, double hours, Func<SimRate, double?> half, Func<string, bool> sparse)
    {
        if (!(hours > 0) || !double.IsFinite(hours)) return null;
        double over = rate.Per switch { Per.Kill => total(Kills), Per.Run => total(Runs), _ => hours };
        double sum = rate.Of == Seconds ? hours * 3600 : total(rate.Of);
        if (!(over > 0) || !double.IsFinite(over) || !double.IsFinite(sum)) return null;
        double value = sum / over;
        bool count = IsCount(rate.Of);
        // None of it all fight: a count still has a range (up to 3.69 for 0 deaths in an hour), anything else has none.
        if (sum == 0) return count ? Poisson(0, 0, over) : new SimRange(0, 0, 0, false);

        SimRange? even = null;
        if (half(rate) is { } h && double.IsFinite(h) && h >= 0)
        {
            // Nothing but gold less supplies can be below 0: a range that would reach under it stops at 0.
            bool cut = rate.Of != NetGold && value - h < 0;
            even = new SimRange(value, cut ? 0 : value - h, value + h, !cut);
        }
        if (!count || (even != null && !sparse(rate.Of))) return even;
        return Poisson(value, Math.Round(sum), over, even);
    }

    // Garwood's range of a count of n over hours, kills or runs; the wider of it and the parts' range.
    static SimRange Poisson(double value, double n, double over, SimRange? even = null)
    {
        var (low, high) = CountRange(n);
        low /= over;
        high /= over;
        if (even is { } e) { low = Math.Min(low, e.Low); high = Math.Max(high, e.High); }
        return new SimRange(value, low, high, false);
    }

    // A result's ranges are worked out once, on the game's thread, when the list or the details first need them.
    static readonly ConditionalWeakTable<SimResult, Dictionary<SimRate, SimRange?>> s_known = new();

    /// <summary>The 95% range of one of a result's rates; null when it has none (a scan made before the ranges).</summary>
    public static SimRange? Of(SimResult r, SimRate rate)
    {
        if (r.Failed || r.Spread is not { } spread) return null;
        Dictionary<SimRate, SimRange?> known = s_known.GetOrCreateValue(r);
        if (known.TryGetValue(rate, out SimRange? range)) return range;
        // A history file that was damaged or edited by hand has no ranges, quietly: never an error on every line.
        try { range = Range(rate, q => Total(r, q), r.Hours, x => Stored(spread, x), q => spread.Sparse?.Contains(q) == true); }
        catch (Exception) { range = null; }
        return known[rate] = range;
    }

    /// <summary>The half of a rate's even range as a result keeps it; null when it has none or it is not a number.</summary>
    public static double? Stored(SimSpread spread, SimRate rate) =>
        spread.Half != null && spread.Half.TryGetValue(rate.Of, out double?[]? halves) && halves != null && halves.Length > (int)rate.Per
            && halves[(int)rate.Per] is { } h && double.IsFinite(h) && h >= 0 ? h : null;

    /// <summary>A result's total of a quantity over the whole fight, as its columns have it (SimColumns, SimResult).</summary>
    public static double Total(SimResult r, string quantity)
    {
        double coins = 0, loot = 0, cost = 0;
        if (quantity is Coins or Loot or Gold or NetGold or Cost)
        {
            foreach (Amount drop in r.Loot)
                if (!drop.IsCurrency) loot += drop.Quantity * drop.SellsFor;
                else if (drop.Id == SimEngine.Gold) coins += drop.Quantity;
            foreach (Amount used in r.Used) cost += used.Quantity * used.SellsFor;
        }
        return quantity switch
        {
            Kills => r.Kills, Deaths => r.Deaths, Runs => r.DungeonRuns, Food => r.FoodEaten, Damage => r.DamageTaken, Healed => r.Healed,
            Ammo => r.AmmoUsed ?? 0, Runes => r.RunesUsed ?? 0, Prayer => r.PrayerPointsUsed ?? 0,
            Coins => coins, Loot => loot, Cost => cost, Gold => coins + loot, NetGold => coins + loot - cost,
            Xp => r.Xp.Values.Sum(),
            _ when IsXp(quantity) => r.Xp.Where(x => XpOf(x.Key) == quantity).Sum(x => x.Value),
            _ => double.NaN,
        };
    }

    /// <summary>
    /// The range per hour of an item dropped or used (SimSpread.Items, of the result or the one given); null in a scan
    /// made before the ranges.
    /// </summary>
    public static SimRange? OfItem(SimResult r, Amount amount, SimSpread? spread = null)
    {
        if (r.Failed || (spread ?? r.Spread)?.Items is not { } items || items.Count != r.Loot.Count + r.Used.Count || !(r.Hours > 0)) return null;
        int at = r.Loot.FindIndex(a => ReferenceEquals(a, amount));
        if (at < 0 && r.Used.FindIndex(a => ReferenceEquals(a, amount)) is var used and >= 0) at = r.Loot.Count + used;
        if (at < 0 || items[at] is not { } half || !double.IsFinite(half) || half < 0) return null;
        double value = r.PerHour(amount.Quantity);
        bool cut = value - half < 0;
        return new SimRange(value, cut ? 0 : value - half, value + half, !cut);
    }

    /// <summary>
    /// Garwood's exact 95% range of the mean of a Poisson count of which n were seen: the mean under which n or
    /// fewer has a chance of 2.5% (the top), and the one under which n or more has (the bottom). Found by halving on
    /// the Poisson sum up to n = 100; above that, the Wilson-Hilferty cube root of the same χ² points. Checked
    /// against scipy.stats.chi2 for n = 0 to 399 and up to 100,000: never more than 0.003% off.
    /// </summary>
    public static (double Low, double High) CountRange(double count)
    {
        int n = (int)Math.Max(0, Math.Min(count, int.MaxValue - 1));
        if (n > 100)
        {
            double a = n + 1;
            return (n * Math.Pow(1 - 1 / (9.0 * n) - Z975 / (3 * Math.Sqrt(n)), 3),
                    a * Math.Pow(1 - 1 / (9 * a) + Z975 / (3 * Math.Sqrt(a)), 3));
        }
        double high = Halve(mean => AtMost(n, mean), Tail, n, n + 10 + 10 * Math.Sqrt(n + 1));
        double low = n == 0 ? 0 : Halve(mean => AtMost(n - 1, mean), 1 - Tail, 0, n);
        return (low, high);
    }

    // The chance of n or fewer of a Poisson count with this mean.
    static double AtMost(int n, double mean)
    {
        double term = Math.Exp(-mean), sum = term;
        for (int i = 1; i <= n; i++) { term *= mean / i; sum += term; }
        return Math.Min(1, sum);
    }

    // The mean, between low and high, at which a chance that falls as the mean grows reaches the target.
    static double Halve(Func<double, double> chance, double target, double low, double high)
    {
        for (int i = 0; i < 100; i++)
        {
            double middle = (low + high) / 2;
            if (chance(middle) > target) low = middle; else high = middle;
        }
        return (low + high) / 2;
    }

    // ---- comparing two setups ---------------------------------------------------------------------------------

    /// <summary>
    /// The simulator's one rule for "is this number better than that one" (issue #5), for "Compared with" and for
    /// two setups side by side (issue #2). Under half a percent apart: the same. With a range on both: better or
    /// worse only when the ranges do not overlap, otherwise it could be luck. A result without ranges (a scan made
    /// before them, a number that has none) keeps the rule of before: apart is better or worse.
    /// </summary>
    public static Verdict Judge(double mine, double theirs, SimRange? myRange, SimRange? theirRange, Better better) =>
        Judge(mine, theirs, myRange is { } a && theirRange is { } b && a.Overlaps(b), better);

    /// <summary>The same rule for two fights of a compare: it could be luck while the difference is within the reach of luck.</summary>
    public static Verdict Judge(double mine, double theirs, SimLuck luck, Better better) =>
        Judge(mine, theirs, Math.Abs(luck.Difference) <= luck.Reach, better);

    static Verdict Judge(double mine, double theirs, bool luck, Better better)
    {
        if (theirs == 0 ? mine == 0 : Math.Abs(mine / theirs - 1) < SameShare) return Verdict.Same;
        if (luck) return Verdict.Luck;
        bool more = mine > theirs;
        return better switch
        {
            Better.More => more ? Verdict.Better : Verdict.Worse,
            Better.Less => more ? Verdict.Worse : Verdict.Better,
            _ => Verdict.Differs,
        };
    }

    /// <summary>
    /// <see cref="Judge(double, double, SimRange?, SimRange?, Better)"/> for one rate of two results, as "Compared with"
    /// and two setups side by side use it: for two fights of a compare by the reach of luck between them
    /// (<see cref="Apart(SimResult, SimResult, SimRate)"/>), otherwise by the ranges <see cref="Basis"/> gives.
    /// </summary>
    public static Verdict Judge(SimResult mine, SimResult theirs, SimRate? rate, double myValue, double theirValue, Better better)
    {
        if (rate == null) return Judge(myValue, theirValue, null, null, better);
        if (Apart(mine, theirs, rate) is { } luck) return Judge(myValue, theirValue, luck, better);
        var (a, b, _) = Basis(mine, theirs, rate);
        return Judge(myValue, theirValue, a, b, better);
    }

    // Worked out once for each pair of results and rate, on the game's thread, as Of does for one result.
    static readonly ConditionalWeakTable<SimResult, Dictionary<(SimResult Theirs, SimRate Rate), SimLuck?>> s_apart = new();

    /// <summary>
    /// A rate of two fights of a compare (issue #57), mine less theirs, and the reach of luck between them
    /// (<see cref="SimLuck.Reach"/>): the two own halves added, the bar of two ranges side by side, times the share of their
    /// luck that is left in their difference (<see cref="SimApart.Left"/>) raised by its own noise (<see cref="Noise"/>),
    /// never more than 1. The luck they met alike lowers the bar; with nothing met alike it is lowered so seldom and by so
    /// little that luck is coloured as often as by the bar of before. Null where the two are judged by their own ranges as
    /// before (<see cref="Basis"/>): two results that were not fought with the same dice (a scan of one setup, Find
    /// upgrades, a scan made before), fights that lasted other times (Go on until, From bank) or whose dice could not be
    /// kept in step to the end, and the rates that Basis does not judge by their own ranges either: a count that some part
    /// of either had none of, too rare for the parts alone (Garwood's range), and a quantity one of them had none of,
    /// judged by the counts behind it or as a real 0.
    /// </summary>
    public static SimLuck? Apart(SimResult mine, SimResult theirs, SimRate rate)
    {
        if (mine.Failed || theirs.Failed || (mine.Spread?.Apart == null && theirs.Spread?.Apart == null)) return null;
        Dictionary<(SimResult, SimRate), SimLuck?> known = s_apart.GetOrCreateValue(mine);
        if (known.TryGetValue((theirs, rate), out SimLuck? luck)) return luck;
        return known[(theirs, rate)] = Reach(mine, theirs, rate);
    }

    // The rule of Apart, step by step: the first step that fails leaves the two to their own ranges. A development build
    // notes here which step that was (DevWhy), so that its lines say what decided and cannot drift from the rule.
    static SimLuck? Reach(SimResult mine, SimResult theirs, SimRate rate)
    {
#if DEVTOOLS
        s_devWhy = "";
#endif
        if ((Shared(mine, theirs, rate) ?? Shared(theirs, mine, rate)) is not { } shared)
        {
#if DEVTOOLS
            s_devWhy = SameDice(mine, theirs) ? "NO SHARE OF LUCK FOR THIS NUMBER" : "NOT FOUGHT WITH THE SAME DICE, or not paired";
#endif
            return null;
        }
        if (Of(mine, rate) is not { } a || Of(theirs, rate) is not { } b)
        {
#if DEVTOOLS
            s_devWhy = "no range";
#endif
            return null;
        }
        if (IsNone(a) || IsNone(b))
        {
#if DEVTOOLS
            s_devWhy = "one of them had none of it";
#endif
            return null;
        }
        if (IsCount(rate.Of) && (Sparse(mine, rate.Of) || Sparse(theirs, rate.Of)))
        {
#if DEVTOOLS
            s_devWhy = "a count that some part had none of";
#endif
            return null;
        }
        // The halves the two own ranges were made of (Range): where those overlap, |a - b| <= ha + hb.
        if (Stored(mine.Spread!, rate) is not { } ha || Stored(theirs.Spread!, rate) is not { } hb)
        {
#if DEVTOOLS
            s_devWhy = "no half of its own";
#endif
            return null;
        }
        return new SimLuck(a.Value - b.Value, ha, hb, shared.Share, shared.Parts);
    }

    // The share of luck left of r's rate against other's and the parts it was measured from, as r keeps them when it ended
    // after other; null when it keeps none, also quietly in a history file that was damaged or edited by hand, as Of has it.
    static (double Share, int Parts)? Shared(SimResult r, SimResult other, SimRate rate) =>
        Entry(r, other) is { Left: { } kept } entry && kept.TryGetValue(rate.Of, out double?[]? shares) && shares != null
            && shares.Length > (int)rate.Per && shares[(int)rate.Per] is { } share && double.IsFinite(share) && share >= 0
            ? (share, entry.Parts) : null;

    // The entry r keeps of other when r ended after it (SimEngine.Pair): a fight of the other setup against the same enemy,
    // told from the rest by its stance and its real time; null when r keeps none.
    static SimApart? Entry(SimResult r, SimResult other) =>
        r.Spread?.Apart is { } list && r.SetupKey != other.SetupKey && r.Target.FightKey == other.Target.FightKey
            ? list.FirstOrDefault(x => x != null && x.Ms == other.RealMilliseconds && x.Stance == other.Target.StanceId) : null;

    /// <summary>Whether two results were fought with the same dice as A and B of one compare, and paired (SimEngine.Pair).</summary>
    public static bool SameDice(SimResult a, SimResult b) =>
        !a.Failed && !b.Failed && (Entry(a, b) ?? Entry(b, a)) != null;

    static bool Sparse(SimResult r, string quantity) => r.Spread?.Sparse?.Contains(quantity) == true;

#if DEVTOOLS
    // Development: the step of Reach that left two results to their own ranges, as its last call on this thread noted it.
    [ThreadStatic] static string? s_devWhy;

    /// <summary>
    /// Development: why <see cref="Apart(SimResult, SimResult, SimRate)"/> leaves two results to their own ranges, in the
    /// words of the step of the rule that did (Reach), for the engine's and the window's verdict lines; "" where it does not.
    /// </summary>
    public static string DevWhy(SimResult mine, SimResult theirs, SimRate rate)
    {
        Reach(mine, theirs, rate);
        return s_devWhy ?? "";
    }

    /// <summary>
    /// Development: the numbers behind a bar of the same dice (SimLuck), for the engine's and the window's verdict lines:
    /// the two own halves, the luck left, the share and the parts it was measured from, its noise, and by how much the
    /// shared dice lowered the bar.
    /// </summary>
    public static string DevBar(SimLuck luck, Func<double, string> format) =>
        $"the own halves {format(luck.HalfA)} + {format(luck.HalfB)} = {format(luck.HalfA + luck.HalfB)} times {luck.Left:0.###} of their luck left " +
        $"(share {luck.Share:0.###} from {luck.Parts} parts, raised {Noise(luck.Parts):0.###} times for its noise): the shared dice LOWERED THE BAR BY {(1 - luck.Left) * 100:0}%";
#endif

    /// <summary>
    /// The ranges two results' rate is judged by. Most often each one's own. A quantity that was 0 all fight and is no
    /// count (no damage taken) has no range of its own; against the other setup's number, the two are judged by how
    /// often what makes it up happened: hits for damage, food for healing, pieces of supplies for their cost, kills for
    /// gold and XP. So no damage against a few hits is luck, and against a hit every few seconds it is not.
    /// <para>
    /// Except for a real 0 (<see cref="RealZero"/>): a skill's XP that a fight never gained while it gained XP in other
    /// skills. That setup does not train the skill, so no longer fight would turn it round; the two are judged without
    /// ranges, and any XP at all is better. Counting kills for it compared how often each killed, not the XP: "Magic XP
    /// 7,300 vs 0, could be luck", Mage against Warrior, with 358 and 407 kills an hour.
    /// </para>
    /// </summary>
    public static (SimRange? Mine, SimRange? Theirs, bool RealZero) Basis(SimResult mine, SimResult theirs, SimRate rate)
    {
        SimRange? a = Of(mine, rate), b = Of(theirs, rate);
        // A scan made before the ranges has none: the rule of before, also against a 0.
        if (a == null || b == null || IsNone(a) == IsNone(b)) return (a, b, false);
        if (RealZero(IsNone(a) ? mine : theirs, rate)) return (null, null, true);
        return Behind(mine, rate) is { } ea && Behind(theirs, rate) is { } eb ? (ea, eb, false) : (a, b, false);
    }

    /// <summary>
    /// Whether a result's 0 of a skill's XP is a real 0. The XP of a skill comes with the blows of the attack style
    /// that trains it, with prayers, with a slayer task: a fight that gained XP in other skills landed blows, and when
    /// none of them gave this skill any, the setup does not train it (Magic XP with a sword, Defence XP in an
    /// accurate stance, Prayer XP without prayers, Slayer XP without a task for the enemy). Every other 0 could be luck.
    /// </summary>
    static bool RealZero(SimResult r, SimRate rate) => IsXp(rate.Of) && Total(r, rate.Of) == 0 && r.Xp.Values.Any(xp => xp > 0);

    static bool IsNone(SimRange? range) => range is { Low: 0, High: 0 };

    // Garwood's range of the count behind a quantity that is no count, per hour, kill or run; null when there is none.
    static SimRange? Behind(SimResult r, SimRate rate)
    {
        double? events = rate.Of switch
        {
            Damage => r.Spread?.Hits,
            Healed => r.FoodEaten,
            Cost => r.Used.Where(u => !u.IsCurrency).Sum(u => u.Quantity),
            Coins or Loot or Gold or NetGold or Xp => r.Kills,
            _ when IsXp(rate.Of) => r.Kills,
            _ => null,
        };
        double over = rate.Per switch { Per.Kill => r.Kills, Per.Run => r.DungeonRuns, _ => r.Hours };
        if (events is not { } n || !(n >= 0) || !(over > 0)) return null;
        return Poisson(n / over, Math.Round(n), over);
    }

    /// <summary>The verdict in words: "better", "worse", "could be luck", "the same"; nothing for one that only differs.</summary>
    public static string Words(Verdict verdict) => verdict switch
    {
        Verdict.Better => "better",
        Verdict.Worse => "worse",
        Verdict.Luck => "could be luck",
        Verdict.Same => "the same",
        _ => "",
    };

    // ---- in words -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The range alone, for the small line of a details line and the tooltip of a line: "± 2%", "0.62 to 8.77" in the
    /// rate's format, "up to 3.69" for a count of 0; null for a quantity that was 0 all fight and is no count.
    /// </summary>
    public static string? Text(SimRange range, Func<double, string> format)
    {
        if (range.Low == 0 && range.High == 0) return null;
        if (PlusMinus(range)) return "± " + Percent(range.Share);
        return range.Value == 0 && range.Low == 0 ? "up to " + format(range.High) : format(range.Low) + " to " + format(range.High);
    }

    // "± 2%" only for an even range that stays on one side of 0; a range that reaches 0 says where it goes.
    static bool PlusMinus(SimRange range) => range.Even && (range.Low > 0 || range.High < 0);

    // Two figures at most, as the game writes its percentages; a very steady number is "± 0.04%", never "± 0%". Also
    // how much better one setup is than the other in compare mode ("A by 23%", SimViewCompare.cs).
    internal static string Percent(double share)
    {
        double percent = share * 100;
        return percent switch
        {
            >= 9.95 => percent.ToString("0"),
            >= 0.095 => percent.ToString("0.#"),
            >= 0.0095 => percent.ToString("0.##"),
            _ => "<0.01",
        } + "%";
    }

    // Percent with more figures than it writes, all of them shown: "64.4%" or "64.0%" for "64%", "4.62%" for "4.6%".
    static string Percent(double share, int more)
    {
        double percent = share * 100;
        int figures = percent switch { >= 9.95 => 0, >= 0.095 => 1, _ => 2 };
        return percent.ToString("F" + (figures + more)) + "%";
    }

    /// <summary>
    /// A lead and its ± (issue #57), both by <see cref="Percent(double)"/>, so that the larger never reads as the same as
    /// the smaller: where the two come out alike but are not the same number ("A by 64% ± 64%" for a lead of 64.4% over a
    /// ± of 64.1%), both get one more figure, and two where one is not enough. For Details and "Compared with" alike.
    /// </summary>
    internal static (string Lead, string Reach) Percents(double lead, double reach)
    {
        string a = Percent(lead), b = Percent(reach);
        for (int more = 1; more <= 2 && a == b && lead != reach && double.IsFinite(lead) && double.IsFinite(reach); more++)
            (a, b) = (Percent(lead, more), Percent(reach, more));
        return (a, b);
    }

    /// <summary>A size as the "Go on until" box offers it: "± 2%".</summary>
    public static string Size(double percent) => $"± {percent:0.#}%";
}
