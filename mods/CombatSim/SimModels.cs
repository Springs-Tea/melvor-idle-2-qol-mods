using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace CombatSim;

/// <summary>One thing to fight: an enemy of an area, or a whole dungeon.</summary>
sealed record SimTarget(string AreaId, string? EnemyId, string Name, string AreaName, string CategoryName, int CombatLevel)
{
    /// <summary>The attack stance to fight in; null is the one that is selected now.</summary>
    public string? StanceId { get; init; }
    public string StanceName { get; init; } = "";

    [JsonIgnore] public bool IsDungeon => EnemyId == null;
    /// <summary>The fight, whatever the stance.</summary>
    [JsonIgnore] public string FightKey => EnemyId == null ? AreaId : AreaId + "|" + EnemyId;
    [JsonIgnore] public string Key => StanceId == null ? FightKey : FightKey + "@" + StanceId;
}

sealed class SimOptions
{
    public double Hours = 1;
    /// <summary>
    /// Food, ammo, runes and prayer points are handed back as they are used, so the rates are those of a
    /// fight that never runs dry. Off: the fight uses what is in the bank and ends when something runs out.
    /// </summary>
    public bool UnlimitedSupplies = true;
    /// <summary>The saved loadout to fight with; null is whatever is worn right now.</summary>
    public string? LoadoutId;
    /// <summary>Equipment slot id -> item id to wear there instead ("" is nothing).</summary>
    public Dictionary<string, string> Overrides = new();
    /// <summary>The spell to cast; null is the one that is selected already.</summary>
    public string? SpellId;
    /// <summary>Identifies this equipment setup, and names it for people.</summary>
    public string SetupKey = "", SetupLabel = "";
    /// <summary>
    /// Simulate longer (issue #5), with unlimited supplies only: after <see cref="Hours"/> the fight goes on, a part
    /// at a time (SimParts), until the range of every rate of <see cref="Watch"/> is within this share of it (0.02 is
    /// ± 2%), or within the next of <see cref="SureSizes"/> it can still reach, and <see cref="MostHours"/> at most.
    /// 0: the fight lasts <see cref="Hours"/>.
    /// </summary>
    public double Sure;
    /// <summary>The sizes "Go on until" offers, as shares (0.01 to 0.1): the steps a rate that cannot reach <see cref="Sure"/> falls back on.</summary>
    public double[] SureSizes = Array.Empty<double>();
    public double MostHours = 12;
    /// <summary>The rates the list shows, whose ranges a longer fight waits for.</summary>
    public List<SimRate> Watch = new();
}

/// <summary>An amount of something, by the game's id, with what one of it sells for (0 when unknown).</summary>
sealed record Amount(string Id, string Name, double Quantity, double SellsFor, bool IsCurrency);

/// <summary>What happened in one simulated fight. Totals are for <see cref="Hours"/> of game time.</summary>
sealed class SimResult
{
    public SimTarget Target = null!;
    public string SetupKey = "", SetupLabel = "";
    [JsonIgnore] public string Key => MakeKey(SetupKey, Target.Key);
    public static string MakeKey(string setupKey, string targetKey) => setupKey + "#" + targetKey;

    /// <summary>Game hours that were actually simulated (less than asked when the fight could not go on).</summary>
    public double Hours;
    public int Kills, Deaths, DungeonRuns;
    /// <summary>Seconds of game time until the first death, or -1.</summary>
    public double FirstDeathSeconds = -1;
    public long DamageTaken, Healed;
    public int FoodEaten, LowestHp, MaxHp;
    /// <summary>
    /// For the enemy cards (<see cref="CardReadings"/>): a short code of the copy's equipment, stance, spell, food
    /// and prayers; the biggest hit the enemy could or did deal; the HP at which auto eat steps in (0: it cannot).
    /// Null in scans made before the cards, and when a reading was not possible.
    /// </summary>
    public string? Setup;
    public int? MaxEnemyHit;
    public double? AutoEatHp;
    /// <summary>Safe: no death, and no hit reaches the auto-eat point. Null when that cannot be told.</summary>
    [JsonIgnore] public bool? Safe => Deaths > 0 ? false : MaxEnemyHit is { } hit && AutoEatHp is { } eat ? hit < eat : null;
#if DEVTOOLS
    /// <summary>Development: the setup the code stands for, to show what differs from the one worn.</summary>
    [JsonIgnore] public string? SetupText;
#endif
    /// <summary>
    /// With supplies from the bank: seconds of game time until the last piece of food was eaten, or -1 when
    /// the food did not run out, and the kills made by then. The fight goes on without food after that.
    /// </summary>
    public double FoodGoneSeconds = -1;
    public int FoodGoneKills;
    /// <summary>Skill id -> XP gained.</summary>
    public Dictionary<string, double> Xp = new();
    public List<Amount> Loot = new(), Used = new();
    /// <summary>
    /// Of <see cref="Used"/>: the runes, the ammo and the prayer points, told apart the way the game tells them
    /// apart (SimEngine.SortSupplies). Null in scans made before they were counted, and when the game could not
    /// say: the list shows "-" for them then.
    /// </summary>
    public double? RunesUsed, AmmoUsed, PrayerPointsUsed;
    /// <summary>How far the rates could be off, for their ranges (SimRanges). Null in scans made before the ranges.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SimSpread? Spread;
    public string Note = "";
    public bool Failed;
    public double RealMilliseconds;

    public double PerHour(double total) => Hours > 0 ? total / Hours : 0;
    [JsonIgnore] public double KillsPerHour => PerHour(Kills);
    [JsonIgnore] public double DeathsPerHour => PerHour(Deaths);
    [JsonIgnore] public double XpPerHour => PerHour(Xp.Values.Sum());
    /// <summary>Coins dropped plus what the dropped items sell for.</summary>
    [JsonIgnore] public double GoldPerHour => PerHour(Loot.Sum(l => l.IsCurrency ? (l.Id == SimEngine.Gold ? l.Quantity : 0) : l.Quantity * l.SellsFor));
    [JsonIgnore] public double CoinsPerHour => PerHour(Loot.Where(l => l.IsCurrency && l.Id == SimEngine.Gold).Sum(l => l.Quantity));
    [JsonIgnore] public double LootValuePerHour => PerHour(Loot.Where(l => !l.IsCurrency).Sum(l => l.Quantity * l.SellsFor));
    [JsonIgnore] public double FoodPerHour => PerHour(FoodEaten);
    /// <summary>Pieces of food eaten for each kill; infinity when nothing was killed, the worst there is.</summary>
    [JsonIgnore] public double FoodPerKill => Kills > 0 ? (double)FoodEaten / Kills : double.PositiveInfinity;
    [JsonIgnore] public double HealedPerKill => Kills > 0 ? (double)Healed / Kills : 0;
    [JsonIgnore] public double LowestHpFraction => MaxHp > 0 ? (double)LowestHp / MaxHp : 1;
    [JsonIgnore] public double SecondsPerKill => Kills > 0 ? Hours * 3600 / Kills : 0;
}

/// <summary>An area or a dungeon that can be chosen in "Where".</summary>
sealed record AreaChoice(string AreaId, string Name, string CategoryName, int Fights, bool IsDungeon, int MinLevel, int MaxLevel);

/// <summary>
/// One press of Simulate, kept so it can be looked at again without running it: what was asked and what came
/// out. It stands on its own: the equipment and the unlocked areas may have changed since.
/// </summary>
sealed class Scan
{
    public string Id = "";
    /// <summary>When it finished, in UTC.</summary>
    public DateTime At;
    public bool BestMode;
    public string TrainSkillId = "", TrainSkillName = "";
    /// <summary>The areas it covered, in words.</summary>
    public string Where = "";
    public double Hours;
    public bool Unlimited;
    public string SetupKey = "", SetupLabel = "";
    /// <summary>Fights the run was asked for; more than the results when it was stopped.</summary>
    public int Asked;
    public List<SimResult> Results = new();

    Dictionary<string, SimResult>? _byKey;
    [JsonIgnore] public Dictionary<string, SimResult> ByKey => _byKey ??= Results.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Last());
    [JsonIgnore] public bool Stopped => Results.Count < Asked;
    [JsonIgnore] public int Fights => Results.Select(r => r.Target.FightKey).Distinct().Count();
    [JsonIgnore] public string Title => BestMode ? "Best for " + (TrainSkillName == "" ? "combat XP" : TrainSkillName) : Where;

    /// <summary>XP per hour as this scan ranked it.</summary>
    public double Rank(SimResult r) => BestMode && TrainSkillId != "" ? r.PerHour(r.Xp.GetValueOrDefault(TrainSkillId)) : r.XpPerHour;
    [JsonIgnore] public SimResult? Top => Results.Where(r => !r.Failed).OrderByDescending(Rank).FirstOrDefault();
}

static class SimText
{
    /// <summary>"3s", "5m", "5m 3s", "2h", "2h 10m".</summary>
    public static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? (t.Minutes == 0 ? $"{(int)t.TotalHours}h" : $"{(int)t.TotalHours}h {t.Minutes}m")
             : t.TotalMinutes >= 1 ? (t.Seconds == 0 ? $"{t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s") : $"{t.Seconds}s";
    }
}
