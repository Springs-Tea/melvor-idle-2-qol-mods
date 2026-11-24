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
    /// <summary>
    /// The attack stance chosen in the Stance box of Fights (issue #51), for a fight whose target names none (a line of
    /// Best for names its own); null is the one the game has selected for the weapon. Its name is for the notes.
    /// </summary>
    public string? StanceId;
    public string StanceName = "";
    /// <summary>
    /// Identifies this equipment setup (its stance included), and names it for people: the loadout, the changes and the
    /// spell. The stance chosen is named apart (<see cref="StanceName"/>, SimText.WithStance).
    /// </summary>
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
    /// <summary>
    /// What the slots on the left show, slot id -> item id of each item in its own slot (a two-handed weapon once, in the
    /// Weapon slot; a slot that is not in it is empty or filled by another slot's item): the copy must fight with it, and
    /// the result says so when it does not (SimEngine.NotAsShown). Null: not checked.
    /// </summary>
    public Dictionary<string, string>? Shown;
    /// <summary>
    /// The dice of the fight are set from this key instead of the result's own, and kept in step with it all fight
    /// (SimDice.InStep). Find upgrades (issue #4) gives every fight of a search the key of the setup as it is, so every
    /// item is fought with the same luck as the setup. Compare (issue #57) gives A's and B's fights of an enemy the key of
    /// that fight (CombatSimMod.CompareDice), so A and B meet the same dice. Null: the dice of the choices for this fight (FightDice), set once when the
    /// copy is loaded. Fights with the same dice are paired by their <see cref="Side"/>.
    /// </summary>
    public string? Dice;
    /// <summary>
    /// Which of two setups this fight is for, when its <see cref="Dice"/> are shared: in Compare (issue #57) "A" or "B"; in
    /// Find upgrades (issue #63) <see cref="SideNow"/> for the setup as it is and <see cref="SideItem"/> for every item tried.
    /// When it and a fight of the other side with the same dice are both over, SimEngine works out from their parts how much
    /// of their luck is left in their difference (SimSpread.Apart), which lowers the bar the two are judged by. Fights of the
    /// same side are never paired, so each item is paired with the setup as it is only. Null in a scan of one setup, and for
    /// a fight with dice of its own.
    /// </summary>
    public string? Side;
    /// <summary>Find upgrades' two sides (issue #63): the setup as it is, and the items tried on top of it.</summary>
    public const string SideNow = "now", SideItem = "item";
    /// <summary>What the status line names while this fight runs (the item Find upgrades tries); "" is the fight's name.</summary>
    public string StatusName = "";
#if DEVTOOLS
    /// <summary>Development: the slots on the left when Simulate was pressed (SimGear.Describe), for SimEngine.DevGear.</summary>
    public string DevGear = "";
    /// <summary>Development: the one change is put on as the game takes it by itself, as before issue #46 (raw=).</summary>
    public bool DevRaw;
#endif

    // The original dice for an ordinary fight depend on the choices the player made. Observing
    // another worn stance changes which results the window shows, never the seed of a repeat run.
    public string FightDice(SimTarget target)
    {
        if (Dice != null) return Dice;
        string picks = string.Join(",", Overrides.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));
        string setup = LoadoutId + ";" + picks + (SpellId == null ? "" : ";spell=" + SpellId)
            + (StanceId == null ? "" : ";stance=" + StanceId);
        return SimResult.MakeKey(setup, target.Key);
    }

    /// <summary>
    /// The same options with the dice of <paramref name="dice"/> (<see cref="Dice"/>) for one <paramref name="side"/>
    /// (<see cref="Side"/>): the fights of one enemy in Compare. Every field is copied, the development ones too; the copies
    /// share the dictionaries and lists, which a fight only reads. Find upgrades makes its own (CombatSimMod.TryOptions).
    /// </summary>
    public SimOptions WithDice(string dice, string side)
    {
        var copy = (SimOptions)MemberwiseClone();
        copy.Dice = dice;
        copy.Side = side;
        return copy;
    }
}

/// <summary>
/// One fight to simulate and the setup to fight it with. A run is a list of them, all from one snapshot of the
/// character (SimEngine.Start): one setup for every fight, or two setups fight by fight in compare mode (issue #2).
/// </summary>
sealed record SimJob(SimTarget Target, SimOptions Options)
{
    /// <summary>The key its result will have (<see cref="SimResult.Key"/>).</summary>
    public string Key => SimResult.MakeKey(Options.SetupKey, Target.Key);
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
    /// <summary>
    /// The enemy's biggest hit as the game gives it alone (AllAttacksDamageWatcher.Maximum, issue #67), the highest it
    /// settled at in the fight: where <see cref="MaxEnemyHit"/> is no more than this, it is the game's own number, the same
    /// in every fight of the setup; where it is more, it is a loss of hitpoints seen in the fight, one moment of it
    /// (SimRanges.GameHit). Null in scans made before it was kept, whose biggest hit counts as one moment, when the game gave
    /// none, and when the game's number rose after a prayer or toolbelt item (a potion) of the copy ran out (CardReadings),
    /// at a moment of the fight. Not written when null, so a file of before loads as it did.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? GameMaxHit;
    /// <summary>
    /// The attack stance the copy fought in, as the game names it, and its id: read from the copy when the fight is over
    /// (SimEngine.ReadStance, issue #51), or the one it was put in when that could not be read. Null in scans made before
    /// the stance was kept, and when the game could not say.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stance, StanceId;
    /// <summary>
    /// The copy was put in a stance that was asked for: the one its line names (Best for), or the one chosen in the Stance
    /// box of Fights. False: it fought in the one the game had selected for the weapon. Not written when false.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool StanceChosen;
    /// <summary>
    /// The name of a stance that was asked for and that the weapon the copy wore does not have (a weapon of another kind
    /// tried in Find upgrades): it fought in the one the game had selected for that weapon instead. Null otherwise.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StanceMissing;
    // A scan made before the stance was kept: only a line of Best for names one, the one it was fought in.
    bool StanceFromLine => Stance == null && StanceMissing == null && Target.StanceId != null;
    /// <summary>The stance to name for it: the one it fought in, or in a scan made before that was kept, the one its line names; "" when neither is known.</summary>
    [JsonIgnore] public string StanceShown => Stance ?? (StanceFromLine ? Target.StanceName : "");
    /// <summary>The id of <see cref="StanceShown"/>; null when it is not known.</summary>
    [JsonIgnore] public string? StanceIdShown => Stance != null ? StanceId : StanceFromLine ? Target.StanceId : null;
    /// <summary>
    /// It fought in a stance that was asked for, by its line (Best for) or in the Stance box: Fight switches to it. A line
    /// of Best for in a scan made before the stance was kept is taken to have fought in its own. False: as the game has it.
    /// </summary>
    [JsonIgnore] public bool StanceAsAsked => StanceChosen || StanceFromLine;
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
    /// <summary>
    /// The part of <see cref="Note"/> that says the copy did not put on what it was asked to (SimEngine.Wear, NotAsShown):
    /// an item that could not be equipped, or other equipment than the slots show. "" when it wore it. Find upgrades
    /// (SimUpgrades.cs) does not judge such a trial. Not kept in the history.
    /// </summary>
    [JsonIgnore] public string WearNote = "";
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
    /// <summary>
    /// A scan of two setups (compare mode, issue #2): setup B's key and name, <see cref="SetupKey"/> being A's. Null in
    /// a scan of one setup, which every scan made before compare mode is: that is how the two are told apart, and an
    /// old file loads as it always did. Not written when null, so a scan of one setup is the file it always was.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SetupKeyB, SetupLabelB;
    /// <summary>Fights the run was asked for; more than the results when it was stopped.</summary>
    public int Asked;
    public List<SimResult> Results = new();

    Dictionary<string, SimResult>? _byKey;
    [JsonIgnore] public Dictionary<string, SimResult> ByKey => _byKey ??= Results.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Last());
    [JsonIgnore] public bool Stopped => Results.Count < Asked;
    [JsonIgnore] public int Fights => Results.Select(r => r.Target.FightKey).Distinct().Count();
    [JsonIgnore] public bool Compare => SetupKeyB != null;
    [JsonIgnore] public string Title => (Compare ? "A against B: " : "") + (BestMode ? "Best for " + (TrainSkillName == "" ? "combat XP" : TrainSkillName) : Where);
    /// <summary>The setup it was made with, or the two: "A: Warrior, B: Mage".</summary>
    [JsonIgnore] public string Setups => Compare ? $"A: {SetupLabel}, B: {SetupLabelB}" : SetupLabel;

    /// <summary>XP per hour as this scan ranked it.</summary>
    public double Rank(SimResult r) => BestMode && TrainSkillId != "" ? r.PerHour(r.Xp.GetValueOrDefault(TrainSkillId)) : r.XpPerHour;
    [JsonIgnore] public SimResult? Top => Results.Where(r => !r.Failed).OrderByDescending(Rank).FirstOrDefault();
}

static class SimText
{
    /// <summary>
    /// A setup's name with the stance chosen for it in Fights (issue #51), as its spell is named in it: "Loadout 1, Fire
    /// Strike, Defensive stance". The name alone when none was chosen.
    /// </summary>
    public static string WithStance(string label, string stanceName) => stanceName == "" ? label : $"{label}, {stanceName} stance";

    /// <summary>"3s", "5m", "5m 3s", "2h", "2h 10m".</summary>
    public static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? (t.Minutes == 0 ? $"{(int)t.TotalHours}h" : $"{(int)t.TotalHours}h {t.Minutes}m")
             : t.TotalMinutes >= 1 ? (t.Seconds == 0 ? $"{t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s") : $"{t.Seconds}s";
    }
}
