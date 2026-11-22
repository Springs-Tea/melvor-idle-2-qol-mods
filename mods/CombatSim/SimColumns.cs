using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MelvorMods.Shared;

namespace CombatSim;

/// <summary>Which way a number is better. The first click on its column puts the best fights on top.</summary>
enum Better { More, Less, Neither }

/// <summary>
/// One number the list can show as a column (issue #19). <see cref="Value"/> is what the list sorts by and what
/// "Compared with" compares. Two kinds of "-" in a cell:
/// <list type="bullet">
/// <item>The fight has no such number because nothing was killed, or no run of the dungeon was finished. That is a
/// fact about the fight and the worst there is: the value is infinity on the bad side (as
/// <see cref="SimResult.FoodPerKill"/> always was), so it goes last on the first click and first on the second.</item>
/// <item>The number is missing: the fight is not a dungeon, or the scan was made before the number was recorded.
/// The value is null, and the line goes last whichever way the list is sorted. Never a wrong 0.</item>
/// </list>
/// </summary>
/// <param name="Title">The column's short name, over the list.</param>
/// <param name="Name">Its whole name: in the picker, in "Compared with", and first in the tooltip of a shortened title.</param>
/// <param name="Width">At full size, as the columns that were there before were given.</param>
/// <param name="Percent">"Compared with" says the change in percent; false: this setup's own number.</param>
/// <param name="Trim">Words that can be longer than the column: cut with "...".</param>
/// <param name="Dash">When the column shows "-" other than for a fight that could not be simulated.</param>
/// <param name="Rate">The rate the column shows, for its 95% range (issue #5, <see cref="SimRanges"/>); null for a
/// number that has none: the lowest HP, a hit, a first death, a drop, a time.</param>
/// <param name="Format">How the column writes a number, for the ends of a range; <see cref="SimView.Fmt"/> when null.</param>
/// <param name="Way">The last line of <see cref="About"/> in place of the one <see cref="Better"/> gives: for a column of
/// compare mode that says which setup is better, of which "more" is not "better" (SimViewCompare.cs).</param>
sealed record SimColumn(string Key, string Group, string Title, string Name, string Tip, float Width,
    Func<SimResult, double?> Value, Func<SimResult, string> Text, Func<SimResult, Color> Tint, Better Better,
    bool Percent = true, bool Trim = false, string Dash = "", Func<SimRate>? Rate = null, Func<double, string>? Format = null,
    string? Way = null)
{
    /// <summary>
    /// The tooltip of the column's name over the list: its whole name first when the title is short for it (and the
    /// tip does not start with it), then <see cref="About"/>.
    /// </summary>
    public string Help => Title == Name || Tip.StartsWith(Name, StringComparison.OrdinalIgnoreCase) ? About : Name + "\n" + About;

    /// <summary>What the number is, when it shows "-", and which way is better: the tooltip of its tick in the picker.</summary>
    public string About => string.Join("\n", new[]
    {
        Tip,
        Dash == "" ? "" : "Shows - " + Dash,
        Way ?? Better switch
        {
            Better.More => "More is better",
            Better.Less => "Less is better",
            _ => "Neither more nor less is better",
        },
    }.Where(line => line != ""));
}

/// <summary>
/// Every number the simulator can show in its list, in the picker's groups, and the sets of them one click
/// puts in place. Each comes from what a simulated fight recorded (<see cref="SimResult"/>), worked out with
/// nothing but a division or a sum: no formula of the game's is made again here.
/// </summary>
static class SimColumns
{
    public const string Offence = "Offence", Defence = "Defence", Supplies = "Supplies", Economy = "Economy",
        Experience = "Experience", Dungeons = "Dungeons", Time = "Time";
    public static readonly string[] Groups = { Offence, Defence, Supplies, Economy, Experience, Dungeons, Time };

    /// <summary>The list as it was before the columns could be chosen, in that order: what everybody starts with.</summary>
    public static readonly string[] Default = { "kills", "xp", "gold", "food", "foodPerKill", "deaths", "lowHp" };

    /// <summary>The XP of one combat skill: this and the skill's id.</summary>
    public const string SkillPrefix = "xp:";

    public static readonly (string Name, string Tip, string[] Keys)[] Presets =
    {
        ("Default", "The list as it starts: kills, XP, gold, food, food per kill, deaths and the lowest HP", Default),
        ("Idle", "Can I leave this running? Safe or risky, deaths, the first death, the lowest HP, the enemy's biggest hit, food per kill",
            new[] { "safe", "deaths", "firstDeath", "lowHp", "maxHit", "foodPerKill" }),
        ("Money", "Where is the gold? Gold, gold per kill, coins, loot, what the supplies cost, the gold left after them, the best drop",
            new[] { "gold", "goldPerKill", "coins", "loot", "cost", "netGold", "bestDrop" }),
        ("Supplies", "What does it use? Food, food per kill, runes, ammo, prayer points, and what they cost",
            new[] { "food", "foodPerKill", "runes", "ammo", "prayer", "cost" }),
        ("Training", "Where do I level? XP, XP per kill, and the XP of each combat skill that gained any in the list",
            new[] { "xp", "xpPerKill" }),
        ("Dungeons", "Runs per hour, the time a run takes, deaths and gold per run, XP and deaths",
            new[] { "runs", "runTime", "deathsPerRun", "goldPerRun", "xp", "deaths" }),
    };

    /// <summary>The preset that adds the skills that gained XP to its own columns.</summary>
    public const string TrainingPreset = "Training";

    // Made on first use, not with the class: SimState's own defaults come from here.
    static HashSet<string>? s_known;

    /// <summary>
    /// The columns as settings.json has them: those this version knows, each once. Nothing saved, or nothing
    /// left of it, is the default list.
    /// </summary>
    public static List<string> Read(IEnumerable<string>? saved)
    {
        HashSet<string> known = s_known ??= All(new SimState()).Select(c => c.Key).ToHashSet();
        var keys = (saved ?? Default).Where(known.Contains).Distinct().ToList();
        return keys.Count > 0 ? keys : Default.ToList();
    }

    /// <summary>What goes into settings.json: nothing while the list is the default one, so a later default reaches it.</summary>
    public static List<string>? Write(List<string> keys) => keys.SequenceEqual(Default) ? null : keys.ToList();

    const string NoKill = "when nothing was killed";
    const string NotCounted = "in an earlier scan made before they were counted";
    const string NoRun = "for a fight that is not a dungeon, and when no run was finished";

    // "-" for no number, dim; the number in the column's format and colour otherwise.
    static SimColumn Number(string key, string group, string title, string name, string tip, float width,
        Func<SimResult, double?> value, Better better, Func<double, string>? format = null, Func<SimResult, double, Color>? tint = null,
        bool percent = true, string dash = "", Func<SimRate>? rate = null) =>
        new(key, group, title, name, tip, width, value,
            r => value(r) is { } v && double.IsFinite(v) ? (format ?? SimView.Fmt)(v) : "-",
            r => value(r) is { } v && double.IsFinite(v) ? tint?.Invoke(r, v) ?? GameStyle.TextWhite : GameStyle.TextDim,
            better, percent, Dash: dash, Rate: rate, Format: format);

    // The rates of the columns, by what they count (SimRanges) and what per.
    static Func<SimRate> EachHour(string of) => () => SimRate.PerHour(of);
    static Func<SimRate> EachKill(string of) => () => SimRate.PerKill(of);
    static Func<SimRate> EachRun(string of) => () => SimRate.PerRun(of);

    // Nothing killed, or no run finished: the worst there is (see SimColumn).
    static double Worst(Better better) => better == Better.Less ? double.PositiveInfinity : double.NegativeInfinity;
    static double? PerKill(SimResult r, double? total, Better better) => total is not { } t ? null : r.Kills > 0 ? t / r.Kills : Worst(better);
    static double? PerRun(SimResult r, double? total, Better better) =>
        !r.Target.IsDungeon || total is not { } t ? null : r.DungeonRuns > 0 ? t / r.DungeonRuns : Worst(better);
    static double? Hourly(SimResult r, double? total) => total is { } t ? r.PerHour(t) : null;

    /// <summary>What the supplies the fight used sell for, in gold: food, runes, ammo, potions. Currencies have no price.</summary>
    static double Cost(SimResult r) => r.Used.Sum(u => u.Quantity * u.SellsFor);

    // As "one every 2.4s" in the details; a minute or more as the clock of the other times ("1m 5s").
    static string Seconds(double seconds) => seconds < 59.95 ? $"{seconds:0.#}s" : SimText.Clock(seconds);

    /// <summary>
    /// Every column, in the picker's order. The XP columns read <paramref name="state"/>: in "Best for" the XP
    /// column is the XP of the skill that is trained.
    /// </summary>
    public static List<SimColumn> All(SimState state)
    {
        var columns = new List<SimColumn>
        {
            // ---- Offence
            Number("kills", Offence, "Kills", "Kills", "Enemies killed per hour", 58, r => r.KillsPerHour, Better.More,
                rate: EachHour(SimRanges.Kills)),
            Number("killTime", Offence, "Kill time", "Time per kill",
                "Seconds of game time for each kill, waiting for the next enemy included", 66,
                r => PerKill(r, r.Hours * 3600, Better.Less), Better.Less, Seconds, dash: NoKill, rate: EachKill(SimRanges.Seconds)),

            // ---- Defence
            new("deaths", Defence, "Deaths", "Deaths", "Times you die per hour", 60, r => r.DeathsPerHour,
                r => r.Deaths == 0 ? "0" : SimView.Fmt(r.DeathsPerHour), r => r.Deaths == 0 ? GameStyle.TextDim : SimView.Bad, Better.Less,
                Rate: EachHour(SimRanges.Deaths)),
            new("firstDeath", Defence, "First death", "First death",
                "How far into the fight you died for the first time; none when you never died", 76,
                // Never dying is the best there is: it sorts above the latest first death.
                r => r.Deaths > 0 ? r.FirstDeathSeconds : double.MaxValue,
                r => r.Deaths > 0 ? SimText.Clock(r.FirstDeathSeconds) : "none",
                r => r.Deaths > 0 ? SimView.Bad : GameStyle.TextDim, Better.More, Percent: false),
            new("lowHp", Defence, "Low HP", "Lowest hitpoints", "The lowest your hitpoints got, as a share of the maximum", 62,
                r => r.LowestHpFraction, r => r.LowestHpFraction.ToString("P0"),
                r => r.Deaths > 0 || r.LowestHpFraction < 0.15 ? SimView.Bad : r.LowestHpFraction < 0.35 ? GameStyle.TextGold : GameStyle.TextWhite,
                Better.More, Percent: false),
            // The words and colours of the enemy's card in the combat areas menu (EnemyCards.Show).
            new("safe", Defence, "Safe", "Safe or risky",
                "Safe: you did not die, and no hit of this enemy reaches the point where auto eat steps in, as on the " +
                "enemy's card in the combat areas menu", 58,
                r => r.Safe is { } safe ? safe ? 1 : 0 : null,
                r => r.Safe is { } safe ? safe ? "Safe" : "Risky" : "-",
                r => r.Safe is { } safe ? safe ? GameStyle.Good : GameStyle.Bad : GameStyle.TextDim, Better.More, Percent: false,
                Dash: "for a dungeon, and in an earlier scan made before the enemy cards"),
            Number("maxHit", Defence, "Max hit", "Enemy's biggest hit",
                "The most the enemy's attacks can deal, as the game says, or a bigger loss of hitpoints seen in the fight", 58,
                r => r.MaxEnemyHit, Better.Less, dash: "for a dungeon, and in an earlier scan made before the enemy cards"),
            Number("damageTaken", Defence, "Dmg taken", "Damage taken", "Hitpoints you lose per hour", 66,
                r => r.PerHour(r.DamageTaken), Better.Less, rate: EachHour(SimRanges.Damage)),
            Number("damagePerKill", Defence, "Dmg/kill", "Damage taken per kill", "Hitpoints you lose for each kill", 62,
                r => PerKill(r, r.DamageTaken, Better.Less), Better.Less, dash: NoKill, rate: EachKill(SimRanges.Damage)),
            Number("healed", Defence, "Healed", "Healed by food", "Hitpoints food healed per hour", 60,
                r => r.PerHour(r.Healed), Better.Neither, rate: EachHour(SimRanges.Healed)),

            // ---- Supplies
            Number("food", Supplies, "Food", "Food eaten", "Food eaten per hour", 52, r => r.FoodPerHour, Better.Less,
                rate: EachHour(SimRanges.Food)),
            Number("foodPerKill", Supplies, "Food/kill", "Food per kill", "Food eaten per kill: the less, the longer your food lasts", 66,
                r => PerKill(r, r.FoodEaten, Better.Less), Better.Less, SimView.PerKill, dash: NoKill, rate: EachKill(SimRanges.Food)),
            Number("runes", Supplies, "Runes", "Runes used", "Runes used per hour by your spells", 60,
                r => Hourly(r, r.RunesUsed), Better.Less, dash: NotCounted, rate: EachHour(SimRanges.Runes)),
            Number("runesPerKill", Supplies, "Runes/kill", "Runes per kill", "Runes per kill used by your spells", 70,
                r => PerKill(r, r.RunesUsed, Better.Less), Better.Less, SimView.PerKill, dash: NoKill + ", or " + NotCounted,
                rate: EachKill(SimRanges.Runes)),
            Number("ammo", Supplies, "Ammo", "Ammo used", "Ammo used per hour: arrows, bolts and the like", 58,
                r => Hourly(r, r.AmmoUsed), Better.Less, dash: NotCounted, rate: EachHour(SimRanges.Ammo)),
            Number("ammoPerKill", Supplies, "Ammo/kill", "Ammo per kill", "Ammo per kill: arrows, bolts and the like", 68,
                r => PerKill(r, r.AmmoUsed, Better.Less), Better.Less, SimView.PerKill, dash: NoKill + ", or " + NotCounted,
                rate: EachKill(SimRanges.Ammo)),
            Number("prayer", Supplies, "Prayer pts", "Prayer points used", "Prayer points used per hour by your prayers", 70,
                r => Hourly(r, r.PrayerPointsUsed), Better.Less, dash: NotCounted, rate: EachHour(SimRanges.Prayer)),
            Number("prayerPerKill", Supplies, "Pts/kill", "Prayer points per kill", "Prayer points per kill used by your prayers", 62,
                r => PerKill(r, r.PrayerPointsUsed, Better.Less), Better.Less, SimView.PerKill, dash: NoKill + ", or " + NotCounted,
                rate: EachKill(SimRanges.Prayer)),
            Number("cost", Supplies, "Cost", "Supplies cost",
                "Supplies cost: what the food, runes, ammo and potions used in an hour sell for. Prayer points have no price", 60,
                r => r.PerHour(Cost(r)), Better.Less, tint: (_, _) => GameStyle.TextGold, rate: EachHour(SimRanges.Cost)),

            // ---- Economy
            Number("gold", Economy, "Gold", "Gold", "Coins dropped plus what the dropped items sell for, per hour", 66,
                r => r.GoldPerHour, Better.More, tint: (_, _) => GameStyle.TextGold, rate: EachHour(SimRanges.Gold)),
            Number("goldPerKill", Economy, "Gold/kill", "Gold per kill", "Gold per kill: coins and what the dropped items sell for", 66,
                r => PerKill(r, r.GoldPerHour * r.Hours, Better.More), Better.More, tint: (_, _) => GameStyle.TextGold, dash: NoKill,
                rate: EachKill(SimRanges.Gold)),
            Number("coins", Economy, "Coins", "Coins", "Coins dropped per hour", 60, r => r.CoinsPerHour, Better.More,
                tint: (_, _) => GameStyle.TextGold, rate: EachHour(SimRanges.Coins)),
            Number("loot", Economy, "Loot", "Loot value", "Loot value: what the dropped items sell for, per hour", 60, r => r.LootValuePerHour,
                Better.More, tint: (_, _) => GameStyle.TextGold, rate: EachHour(SimRanges.Loot)),
            Number("lootPerKill", Economy, "Loot/kill", "Loot value per kill", "Loot value per kill: what the items dropped for each kill sell for", 66,
                r => PerKill(r, r.LootValuePerHour * r.Hours, Better.More), Better.More, tint: (_, _) => GameStyle.TextGold, dash: NoKill,
                rate: EachKill(SimRanges.Loot)),
            Number("netGold", Economy, "Net gold", "Gold after supplies", "Gold after supplies: gold per hour less what the supplies used sell for", 66,
                r => r.GoldPerHour - r.PerHour(Cost(r)), Better.More, tint: (_, v) => v < 0 ? SimView.Bad : GameStyle.TextGold,
                percent: false, rate: EachHour(SimRanges.NetGold)),
            new("bestDrop", Economy, "Best drop", "Most valuable drop", "Most valuable drop: the item that sells for the most of all that dropped", 110,
                r => BestDrop(r)?.SellsFor ?? 0, r => BestDrop(r)?.Name ?? "none", r => BestDrop(r) != null ? GameStyle.TextPaper : GameStyle.TextDim,
                Better.More, Percent: false, Trim: true),
            Number("items", Economy, "Items", "Kinds of items dropped",
                "Kinds of items dropped in the whole simulated time: the bank slots they need. Not per hour", 52,
                r => r.Loot.Count(l => !l.IsCurrency), Better.Neither),

            // ---- Experience
            Number("xp", Experience, "XP", "Experience",
                "Experience per hour: all combat skills together, or the skill you are training", 66, r => state.Xp(r), Better.More,
                rate: () => state.XpRate),
            Number("xpPerKill", Experience, "XP/kill", "Experience per kill", "Experience per kill, of the XP column", 62,
                r => PerKill(r, state.Xp(r) * r.Hours, Better.More), Better.More, dash: NoKill, rate: () => SimRate.PerKill(state.XpRate.Of)),
        };
        foreach (string id in SimEngine.CombatSkills)
        {
            string skill = state.Skills.FirstOrDefault(k => k.Id == id).Name ?? id[(id.IndexOf(':') + 1)..];
            columns.Add(Number(SkillPrefix + id, Experience, skill, skill + " XP", $"{skill} XP per hour", 66,
                r => r.PerHour(r.Xp.GetValueOrDefault(id)), Better.More, rate: EachHour(SimRanges.XpOf(id))));
        }
        columns.AddRange(new[]
        {
            // ---- Dungeons
            Number("runs", Dungeons, "Runs", "Dungeon runs", "Whole runs of the dungeon per hour", 52,
                r => r.Target.IsDungeon ? r.PerHour(r.DungeonRuns) : null, Better.More, dash: "for a fight that is not a dungeon",
                rate: EachHour(SimRanges.Runs)),
            Number("runTime", Dungeons, "Run time", "Time per run", "Game time for each run of the dungeon, deaths included", 66,
                r => PerRun(r, r.Hours * 3600, Better.Less), Better.Less, SimText.Clock, dash: NoRun, rate: EachRun(SimRanges.Seconds)),
            Number("deathsPerRun", Dungeons, "Deaths/run", "Deaths per run", "Deaths per run: times you die for each run of the dungeon", 72,
                r => PerRun(r, r.Deaths, Better.Less), Better.Less, SimView.PerKill, dash: NoRun, rate: EachRun(SimRanges.Deaths)),
            Number("goldPerRun", Dungeons, "Gold/run", "Gold per run", "Gold per run: coins and what the dropped items sell for", 66,
                r => PerRun(r, r.GoldPerHour * r.Hours, Better.More), Better.More, tint: (_, _) => GameStyle.TextGold, dash: NoRun,
                rate: EachRun(SimRanges.Gold)),

            // ---- Time
            Number("fought", Time, "Fought", "Time simulated",
                "The game time this fight was simulated for: less than asked when it had to stop", 62,
                r => r.Hours * 3600, Better.More, SimText.Clock),
            Number("simTime", Time, "Sim time", "Time it took to simulate", "Real seconds this fight took to simulate", 62,
                r => r.RealMilliseconds / 1000, Better.Neither, s => $"{s:0.0}s", (_, _) => GameStyle.TextGrey),
        });
        return columns;
    }

    /// <summary>The item that sells for the most of all that dropped; null when no item dropped.</summary>
    static Amount? BestDrop(SimResult r) => r.Loot.Where(l => !l.IsCurrency && l.Quantity > 0).MaxBy(l => l.SellsFor);
}
