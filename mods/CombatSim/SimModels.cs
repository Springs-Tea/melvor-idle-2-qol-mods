using System.Collections.Generic;
using System.Linq;

namespace CombatSim;

/// <summary>One thing to fight: an enemy of an area, or a whole dungeon.</summary>
sealed record SimTarget(string AreaId, string? EnemyId, string Name, string AreaName, string CategoryName, int CombatLevel)
{
    public bool IsDungeon => EnemyId == null;
    public string Key => EnemyId == null ? AreaId : AreaId + "|" + EnemyId;
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
    /// <summary>Identifies this equipment setup, and names it for people.</summary>
    public string SetupKey = "", SetupLabel = "";
}

/// <summary>An amount of something, by the game's id, with what one of it sells for (0 when unknown).</summary>
sealed record Amount(string Id, string Name, double Quantity, double SellsFor, bool IsCurrency);

/// <summary>What happened in one simulated fight. Totals are for <see cref="Hours"/> of game time.</summary>
sealed class SimResult
{
    public SimTarget Target = null!;
    public string SetupKey = "", SetupLabel = "";
    public string Key => MakeKey(SetupKey, Target.Key);
    public static string MakeKey(string setupKey, string targetKey) => setupKey + "#" + targetKey;

    /// <summary>Game hours that were actually simulated (less than asked when the fight could not go on).</summary>
    public double Hours;
    public int Kills, Deaths, DungeonRuns;
    /// <summary>Seconds of game time until the first death, or -1.</summary>
    public double FirstDeathSeconds = -1;
    public long DamageTaken, Healed;
    public int FoodEaten, LowestHp, MaxHp;
    /// <summary>Skill id -> XP gained.</summary>
    public Dictionary<string, double> Xp = new();
    public List<Amount> Loot = new(), Used = new();
    public string Note = "";
    public bool Failed;
    public double RealMilliseconds;

    public double PerHour(double total) => Hours > 0 ? total / Hours : 0;
    public double KillsPerHour => PerHour(Kills);
    public double DeathsPerHour => PerHour(Deaths);
    public double XpPerHour => PerHour(Xp.Values.Sum());
    /// <summary>Coins dropped plus what the dropped items sell for.</summary>
    public double GoldPerHour => PerHour(Loot.Sum(l => l.IsCurrency ? (l.Id == SimEngine.Gold ? l.Quantity : 0) : l.Quantity * l.SellsFor));
    public double CoinsPerHour => PerHour(Loot.Where(l => l.IsCurrency && l.Id == SimEngine.Gold).Sum(l => l.Quantity));
    public double LootValuePerHour => PerHour(Loot.Where(l => !l.IsCurrency).Sum(l => l.Quantity * l.SellsFor));
    public double FoodPerHour => PerHour(FoodEaten);
    public double LowestHpFraction => MaxHp > 0 ? (double)LowestHp / MaxHp : 1;
    public double SecondsPerKill => Kills > 0 ? Hours * 3600 / Kills : 0;
}
