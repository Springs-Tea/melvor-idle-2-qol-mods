using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Components.Equipment;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Combat;
using Godot;
using MelvorMods.Shared;

namespace CombatSim;

/// <summary>
/// One item tried in one slot by Find upgrades (issue #4): put on the setup on the left the way a pick in that slot's
/// item picker puts it on (SimGear.Put, #46's rule for both hands), so the copy fights with what the slots would then
/// show. An item whose requirements the character does not meet is not tried: the game does not let the copy wear it
/// either (SimEngine.Wear). <see cref="Line"/> is what the list shows for it: its result, or a stand-in until then.
/// </summary>
sealed class UpgradeTry(string slotId, string slotName, string itemId, string itemName, Texture2D? icon, int inBank, int worn)
{
    public readonly string SlotId = slotId, SlotName = slotName, ItemId = itemId, ItemName = itemName;
    public readonly Texture2D? Icon = icon;
    /// <summary>How many of it the bank holds, and how many the character wears now, when the search was chosen.</summary>
    public readonly int InBank = inBank, Worn = worn;
    public bool Owned => InBank + Worn > 0;
    /// <summary>The character meets its requirements (the item picker's CanWear): it is tried. Otherwise it can only be listed.</summary>
    public bool Tried = true;
    /// <summary>The requirements not met, in the game's words (FightSetup's Unmet); "" when it can be worn, or when the game gave none.</summary>
    public string Needs = "";
    /// <summary>What it goes on in place of: "in place of Iron Sword and Bronze Shield", "in an empty slot".</summary>
    public string InPlaceOf = "";
    /// <summary>The setup it is tried in, keyed as the left would key it after that pick (SimState.KeyWith).</summary>
    public string SetupKey = "";
    public Dictionary<string, string> Overrides = new(), Shown = new();
#if DEVTOOLS
    /// <summary>Development: the slots it would show on the left (SimGear.Describe), for SimEngine.DevGear.</summary>
    public string DevSlots = "";
#endif
    public SimResult? Result;
    public SimResult Line = null!;

    /// <summary>
    /// The same pick for a search of its own: the plan is kept for the panel's next count, and a search keeps its results,
    /// also the player's last one that a development request puts aside and back.
    /// </summary>
    public UpgradeTry Copy() => new(SlotId, SlotName, ItemId, ItemName, Icon, InBank, Worn)
    {
        Tried = Tried, Needs = Needs, InPlaceOf = InPlaceOf, SetupKey = SetupKey, Overrides = Overrides, Shown = Shown,
#if DEVTOOLS
        DevSlots = DevSlots,
#endif
    };
}

/// <summary>Every item that fits a slot of the setup on the left, to try or to list; and how many picks were left out.</summary>
sealed record UpgradePlan(List<UpgradeTry> Tries, int Skipped);

/// <summary>
/// Which items a search tries (issue #4), chosen before it runs and kept in settings.json: the slots, where the items come
/// from, a part of their name, and whether the items the character cannot wear yet are listed (they are never tried).
/// Nothing ticked in a group is no choice in it: every slot, every item.
/// </summary>
sealed class UpgradeChoice
{
    public HashSet<string> Slots = new(), Kinds = new();
    public string Search = "";
    public bool CannotWear;
}

/// <summary>
/// The choice applied to the items of a fight: what a press would try and list, and how many each tick of the panel
/// stands for (the items that pass the other groups, in its slot or of its kind). <see cref="Waiting"/>: a kind that
/// needs to know where items come from is ticked and that is still being read.
/// </summary>
sealed record UpgradePick(List<UpgradeTry> ToTry, List<UpgradeTry> CannotWear, Dictionary<string, int> PerSlot,
    List<(string Id, string Name, string Tip)> Kinds, Dictionary<string, int> PerKind, bool Known, bool Failed, bool Waiting)
{
    public int Slots => ToTry.Select(t => t.SlotId).Distinct().Count();
    public bool TooMany => ToTry.Count > UpgradeSearch.MaxItems;
    public bool TooManyToList => CannotWear.Count > UpgradeSearch.MaxListed;
}

/// <summary>Where an item comes from, as Item Sources says it: a few words for the list, the headings for its tooltip, the places, and what to say without one.</summary>
sealed record UpgradeSource(string Short, string Tip, IReadOnlyList<ItemLine> Lines, string Say);

/// <summary>
/// One press of Find upgrades: the selected fight once with the setup as it is, then once per item chosen, all from one
/// snapshot of the character (SimEngine.Start), so that every item is compared with what you have from the same moment.
/// All of them are fought alike: the same length (never "Go on until"), and the same luck, the dice of the setup as it is
/// kept in step all fight (SimOptions.Dice, SimDice.InStep), so a difference comes from the item and not from the dice.
/// Each item's fight is paired with the fight of the setup as it is, and with nothing else (SimOptions.Side: SideItem
/// against SideNow, SimEngine.Pair), so that its change is judged as Compare judges B against A (issue #63): by the bar
/// of two ranges side by side, lowered by the luck the two met alike (SimRanges.Apart).
/// None of it goes into SimState.Results or the history: there it would fill "Compared with" in Details and the scans.
/// A search is kept only here, in SimState.Upgrades, until another character is opened: never in History or settings.json.
/// </summary>
sealed class UpgradeSearch(SimTarget target, SimState state, SimOptions options)
{
    /// <summary>
    /// The most items one press tries: 41 fights, "about 44s" by the estimate at a quarter of an hour each, one at a
    /// time. The first check of Find upgrades tried every item of every slot, 196 fights and over four minutes of a busy
    /// game; a choice that needs more is narrowed first.
    /// </summary>
    public const int MaxItems = 40;
    /// <summary>The most items it lists that cannot be worn yet: each is a line of the list to build, and no fight.</summary>
    public const int MaxListed = 60;
    /// <summary>
    /// Fights at a time. A search runs while you play: one copy at a time leaves the game the rest of the processor and
    /// far less memory to clear, and costs little time, as a copy is mostly loading (the estimate's own rule: four at a
    /// time are worth about one and a half). A scan of the list keeps the number chosen for it.
    /// </summary>
    public const int Workers = 1;
    public const string Status = "the setup as it is";

    public readonly string Id = DateTime.UtcNow.Ticks.ToString();
    public readonly SimTarget Target = target;
    /// <summary>
    /// The setup on the left when it was pressed, as it is: what every item is put on top of. Named with the stance chosen in
    /// Fights (issue #51), which every item is fought in too.
    /// </summary>
    public readonly string SetupKey = state.SetupKey, SetupLabel = state.SetupName, LoadoutId = state.LoadoutId, SpellId = state.SpellId;
    public readonly string? StanceId = state.ChosenStance?.Id;
    readonly Dictionary<string, string> _overrides = new(state.Overrides);
    public readonly bool BestMode = state.BestMode;
    public readonly SimOptions Options = options;
    public readonly List<UpgradeTry> Tries = new();
    readonly Dictionary<string, UpgradeTry> _byKey = new();
    // The same by the line itself, which the list asks for many times a refresh.
    readonly Dictionary<SimResult, UpgradeTry> _byLine = new(ReferenceEqualityComparer.Instance);
    /// <summary>The fight with the setup as it is, from the same snapshot as the items.</summary>
    public SimResult? Baseline;
    /// <summary>What the list shows, made again at most every two seconds while the results come in (CombatSimMod.PumpUpgrades).</summary>
    public List<SimResult> Lines = new();
    public bool Running, Stopped;
    /// <summary>When it finished, in UTC.</summary>
    public DateTime At;
    /// <summary>The choice it was made with, in words: "Boots, Gloves  ·  Owned  ·  "steel"", or every item that fits a slot.</summary>
    public string Chosen = "";

    public string BaseKey => SimResult.MakeKey(SetupKey, Target.Key);
    public int Asked => Tries.Count(t => t.Tried);
    public int Answered => Tries.Count(t => t.Tried && t.Result != null);

    public void Add(UpgradeTry t)
    {
        // An item that is not tried has a key of its own all the same, for its line: it never reaches the engine.
        string setup = t.Tried ? t.SetupKey : $"{SetupKey}|cannot {t.SlotId}={t.ItemId}";
        t.Line = new SimResult { Target = Target, SetupKey = setup, SetupLabel = SetupLabel, Failed = !t.Tried, Note = t.Needs };
        Tries.Add(t);
        _byKey[t.Line.Key] = t;
        _byLine[t.Line] = t;
    }

    public UpgradeTry? Of(SimResult line) => _byLine.TryGetValue(line, out UpgradeTry? t) ? t : _byKey.GetValueOrDefault(line.Key);

    /// <summary>A finished fight of this search: the setup as it is, or one of the items.</summary>
    public bool Take(SimResult r)
    {
        if (r.Key == BaseKey) { Baseline = r; return true; }
#if DEVTOOLS
        if (r.SetupKey == SetupKey + DevAgainKey) { DevAgain = r; return true; }
        if (r.SetupKey == SetupKey + DevLuckKey) { DevLuck = r; return true; }
#endif
        if (!_byKey.TryGetValue(r.Key, out UpgradeTry? t)) return false;
        t.Result = t.Line = r;
        _byLine[r] = t;
        return true;
    }

    /// <summary>
    /// The lines: the items tried so far; once it has ended, those listed that cannot be worn yet after them. While it runs
    /// the list is made again every two seconds, and only of what has numbers.
    /// </summary>
    public void Publish() => Lines = Tries.Where(t => t.Tried && t.Result != null)
        .Concat(Running ? Enumerable.Empty<UpgradeTry>() : Tries.Where(t => !t.Tried)).Select(t => t.Line).ToList();

    /// <summary>The setup on the left is still the one the items were put on (its key, without making it).</summary>
    public bool Matches(SimState state) => state.LoadoutId == LoadoutId && state.SpellId == SpellId && state.ChosenStance?.Id == StanceId
                                           && state.Overrides.Count == _overrides.Count
                                           && state.Overrides.All(o => _overrides.TryGetValue(o.Key, out string? v) && v == o.Value);

#if DEVTOOLS
    // Development: the setup as it is twice more. "Again" goes on as an item does, its own piece of a slot put on again by
    // the game's equip call, with the dice of the setup as it is: it must come out with the very same numbers, or the
    // items are not fought with the same luck as the setup. "Luck" is the setup as it is with dice of its own, kept in step
    // the same way: how far luck alone moves its numbers, which every item used to carry on top of what it changes.
    public const string DevAgainKey = "|again", DevLuckKey = "|luck";
    public SimResult? DevAgain, DevLuck;
    /// <summary>Development: what "again" put on again, in words.</summary>
    public string DevAgainWhat = "";
#endif
}

/// <summary>What the view asks of Find upgrades (issue #4); the mod answers.</summary>
interface IUpgradeLookup
{
    /// <summary>Find upgrades is switched off: something it reads is gone after a game update (one line in the log).</summary>
    bool UpgradesOff { get; }
    /// <summary>
    /// What a press would try and list for this fight with the choice of the panel (SimState.UpgradeChoice). The first time
    /// a character asks, where items come from starts being read, beside the game.
    /// </summary>
    UpgradePick PickFor(SimTarget target);
    void FindUpgrades(SimTarget target);
    void StopUpgrades();
    UpgradeSource SourceOf(string itemId);
}

sealed partial class SimState
{
    /// <summary>The last search for upgrades, until another character is opened.</summary>
    public UpgradeSearch? Upgrades;
    /// <summary>Its items are in the middle instead of the fights; the "Back to the fights" button leaves them.</summary>
    public bool UpgradesShown;
    /// <summary>Which items Find upgrades tries: chosen in its panel, kept in settings.json.</summary>
    public UpgradeChoice UpgradeChoice = new();
    /// <summary>The line of the items the player clicked (a <see cref="SimResult.Key"/>); until then the top one.</summary>
    public string? UpgradeSelected;
    public bool UpgradePinned;

    /// <summary>
    /// The list in the middle is the items tried for one fight: shown, no earlier scan on show, and the setup on the left
    /// and the tab the ones they were tried with.
    /// </summary>
    public bool ShowingUpgrades => UpgradesShown && Viewing == null && Upgrades is { } u && u.BestMode == BestMode && u.Matches(this);

    public UpgradeTry? TryOf(SimResult line) => Upgrades?.Of(line);

    List<SimResult> UpgradeLines() => Upgrades?.Lines.ToList() ?? new List<SimResult>();
}

/// <summary>
/// Find upgrades (issue #4): what the items of the game that the character could wear would add to the selected fight,
/// one slot at a time, on top of the setup on the left. The player chooses which (slots, where they come from, a name)
/// before it runs, and one press tries at most <see cref="UpgradeSearch.MaxItems"/>. It uses what the simulator has: the
/// items that fit a slot (<see cref="ISimLookup.ChoicesFor"/>), a pick as the item picker makes it (SimGear), a run of any
/// list of fights from one snapshot (SimEngine.Start), and for where an item comes from Item Sources' index (ItemIndex,
/// ItemLines). It only shows: nothing is bought, equipped or given, and the copies are never saved.
/// </summary>
public partial class CombatSimMod : IUpgradeLookup
{
    // The search under way; null when the engine runs a scan or nothing.
    UpgradeSearch? _upgradeRun;
    readonly Stopwatch _upgradeClock = new();
    // The list of items is made again at most every two seconds while their results come in: a line of the list takes
    // about two milliseconds to build (check-1: 732 lines in 1.4 s), and the game must stay smooth meanwhile.
    readonly Tempo _upgradeShow = new(2000);
    // Every item that fits a slot of the setup on the left: made when the panel or a press needs it, again when the slots
    // on the left are made again.
    (string Key, GearSet? Worn, UpgradePlan Plan)? _upgradePlan;

    // Where items come from: Item Sources' index of the game's data, read once a character, beside the game, the first
    // time the panel of Find upgrades is opened (as ItemSourcesMod.Open reads it).
    volatile ItemIndex? _itemIndex;
    ItemLines? _itemLines;
    bool _itemIndexAsked;
    volatile bool _itemIndexFailed;
    int _itemIndexArrived;
    readonly Dictionary<string, UpgradeSource> _sources = new();
    // Of every item asked about: the kinds of place it comes from (ItemLines.KindOf) and the areas and dungeons it drops in.
    readonly Dictionary<string, (HashSet<string> Kinds, HashSet<string> Places)> _kinds = new();
    bool _sourcesLogged;

    void ForgetUpgrades()
    {
        _upgradeRun = null;
        _state.Upgrades = null;
        _state.UpgradesShown = _state.UpgradePinned = false;
        _state.UpgradeSelected = null;
        _upgradePlan = null;
        _itemIndex = null;
        _itemLines = null;
        _itemIndexAsked = false;
        _itemIndexFailed = false;
        _sources.Clear();
        _kinds.Clear();
#if DEVTOOLS
        _devUpgrades = null;
#endif
    }

    /// <summary>The choice of the panel as settings.json has it, as OnLoad and the put back of a request read it.</summary>
    void ReadUpgradeChoice() => _state.UpgradeChoice = new UpgradeChoice
    {
        // A file written by hand may say null.
        Slots = (_settings.UpgradeSlots ?? new()).ToHashSet(), Kinds = (_settings.UpgradeKinds ?? new()).ToHashSet(),
        Search = _settings.UpgradeText ?? "", CannotWear = _settings.UpgradeCannotWear,
    };

    void WriteUpgradeChoice()
    {
        UpgradeChoice c = _state.UpgradeChoice;
        _settings.UpgradeSlots = c.Slots.OrderBy(s => s, StringComparer.Ordinal).ToList();
        _settings.UpgradeKinds = c.Kinds.OrderBy(k => k, StringComparer.Ordinal).ToList();
        _settings.UpgradeText = c.Search;
        _settings.UpgradeCannotWear = c.CannotWear;
    }

    // ---- what to try ----------------------------------------------------------------------------------------

    // Something Find upgrades reads is gone after a game update: one line in the log, and the button is not shown.
    bool _upgradesOff;

    bool IUpgradeLookup.UpgradesOff => _upgradesOff;

    // The panel and a press ask for it: a game member that is gone must not break the window, nor be told every time.
    UpgradePlan SafePlan()
    {
        if (_upgradePlan is { } known && known.Key == _state.SetupKey && ReferenceEquals(known.Worn, _gearWorn)) return known.Plan;
        if (!_upgradesOff)
            try
            {
#if DEVTOOLS
                var clock = Stopwatch.StartNew();
#endif
                UpgradePlan plan = PlanUpgrades();
                _upgradePlan = (_state.SetupKey, _gearWorn, plan);
#if DEVTOOLS
                _ctx.Log($"Dev: upgrades: planned {plan.Tries.Count} items ({plan.Tries.Count(t => t.Tried)} that can be worn) for {_state.SetupLabel} in {clock.Elapsed.TotalMilliseconds:0.0} ms");
#endif
                return plan;
            }
            catch (Exception e)
            {
                _upgradesOff = true;
                _ctx.Log("Find upgrades is switched off (the game was updated?): " + e.Message + ". The rest of the simulator works as before.");
            }
        return new UpgradePlan(new List<UpgradeTry>(), 0);
    }

    /// <summary>
    /// Every item that fits a slot of the setup on the left, slot by slot as the item picker lists them (ChoicesFor), but
    /// the one the slot holds: each put on the slots as a pick would (SimGear.Put), which gives its changes in their one
    /// shape (SimGear.Overrides), what the slots then show and what it goes on in place of. Two picks that come to the
    /// same setup are tried once.
    /// </summary>
    UpgradePlan PlanUpgrades()
    {
        var tries = new List<UpgradeTry>();
        int skipped = 0;
        if (_game == null || _combat == null || _gearWorn == null || _gearLoadout == null) return new UpgradePlan(tries, 0);
        var items = _game.Data.GetDataRegistryOutput<Item>();
        var setups = new HashSet<string> { _state.SetupKey };
        foreach (GearSlot slot in _state.Gear)
        {
            if (_gearSlots.FirstOrDefault(s => s.Id == slot.SlotId) is not { } target) continue;
            var choices = ((ISimLookup)this).ChoicesFor(slot).OrderByDescending(c => c.Owned > 0).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
            foreach (GearChoice choice in choices)
            {
                // What the slot holds of its own already is the setup as it is.
                if (choice.ItemId == slot.ItemId && slot.FilledFrom == "") { skipped++; continue; }
                if (!items.TryGetObjectById(choice.ItemId, out Item? item)) continue;
                int bank = _bank?.Inventory.GetQuantity(item) ?? 0;
                var t = new UpgradeTry(slot.SlotId, slot.SlotName, item.Id, choice.Name, choice.Icon, bank, Math.Max(0, choice.Owned - bank));
                if (!choice.CanWear)
                {
                    t.Tried = false;
                    // The game's words for what is missing, or none: the sentences around them say the rest.
                    t.Needs = item.Capabilities.TryGet(out EquipmentCapability? wearable) && wearable?.Requirements is { AreMet: false } needs ? Unmet(needs, "") : "";
                    tries.Add(t);
                    continue;
                }
                GearSet with = _gearWorn.Copy();
                SimGear.Put(with, target, item, 0, _ctx.Log);
                Dictionary<string, string> overrides = SimGear.Overrides(_gearLoadout, with, _gearSlots);
                string key = _state.KeyWith(overrides);
                if (!setups.Add(key)) { skipped++; continue; }
                t.SetupKey = key;
                t.Overrides = overrides;
                t.Shown = with.OwnItems();
                t.InPlaceOf = InPlaceOf(_gearWorn, with, target.Id, id => items.TryGetObjectById(id, out Item? i) ? i.DisplayName.GetPluralText(1) : id);
#if DEVTOOLS
                t.DevSlots = SimGear.Describe(with, _gearSlots);
#endif
                tries.Add(t);
            }
        }
        return new UpgradePlan(tries, skipped);
    }

    // "in place of Iron Sword", "in place of Iron Sword and Bronze Shield" (a two-handed weapon takes the shield off too),
    // "in an empty slot"; a slot the pick leaves empty says so: a shield in place of a two-handed weapon empties the Weapon
    // slot, as on the left.
    string InPlaceOf(GearSet before, GearSet after, string slotId, Func<string, string> nameOf)
    {
        string SlotName(string id) => _gearSlots.FirstOrDefault(s => s.Id == id)?.DisplayName.Text ?? id;
        var off = before.Slots.Where(s => s.Value.FilledFrom == null && !(after[s.Key] is { FilledFrom: null } now && now.Item.Id == s.Value.Item.Id))
            .Select(s => nameOf(s.Value.Item.Id)).Distinct().ToList();
        // A slot that held an item of its own and holds nothing now; the Offhand a two-handed weapon filled is not one.
        var emptied = before.Slots.Where(s => s.Key != slotId && s.Value.FilledFrom == null && after[s.Key] == null).Select(s => SlotName(s.Key)).Distinct().ToList();
        string words = off.Count == 0 ? SimView.EmptySlot : "in place of " + Joined(off);
        return emptied.Count == 0 ? words : $"{words}, which leaves the {Joined(emptied)} {(emptied.Count == 1 ? "slot" : "slots")} empty";
    }

    static string Joined(List<string> names) => names.Count == 1 ? names[0] : string.Join(", ", names[..^1]) + " and " + names[^1];

    // ---- which of them: the choice of the panel -------------------------------------------------------------

    /// <summary>The kinds of place the panel offers: what you have, this fight's area, then Item Sources' kinds (ItemLines.Kinds).</summary>
    internal const string Owned = "owned", ThisArea = "area";

    UpgradePick IUpgradeLookup.PickFor(SimTarget target)
    {
        ReadItemSources();
        return Pick(SafePlan(), target, _state.UpgradeChoice);
    }

    // The items of the plan that pass the choice, and for every tick of the panel how many it stands for: the items that
    // pass the other groups and are in its slot or of its kind. Where items come from is known once it has been read;
    // until then only "Owned" can be told, and a press waits for it. When it could not be read, the other kinds are left
    // out of the choice and the panel says so.
    UpgradePick Pick(UpgradePlan plan, SimTarget target, UpgradeChoice choice)
    {
        bool known = _itemIndex != null && _itemLines != null, failed = !known && _itemIndexFailed;
        var slotIds = plan.Tries.Select(t => t.SlotId).ToHashSet();
        var slots = choice.Slots.Where(slotIds.Contains).ToHashSet();
        var kinds = choice.Kinds.Where(k => k == Owned || !failed).ToHashSet();
        bool waiting = !known && kinds.Any(k => k != Owned);
        string[] words = choice.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        bool Has(UpgradeTry t, string kind) => kind == Owned ? t.Owned
            : known && (kind == ThisArea ? KindsOf(t.ItemId).Places.Contains(target.AreaId) : KindsOf(t.ItemId).Kinds.Contains(kind));
        bool InKinds(UpgradeTry t) => kinds.Count == 0 || kinds.Any(k => Has(t, k));
        bool InSlots(UpgradeTry t) => slots.Count == 0 || slots.Contains(t.SlotId);
        bool Named(UpgradeTry t) => words.All(w => t.ItemName.Contains(w, StringComparison.OrdinalIgnoreCase));

        var offered = new List<(string Id, string Name, string Tip)>
        {
            (Owned, "Owned", "In your bank or worn now"),
        };
        if (known)
        {
            offered.Add((ThisArea, "Drops in " + target.AreaName, $"Dropped by an enemy of {target.AreaName}, or its reward"));
            var present = plan.Tries.SelectMany(t => KindsOf(t.ItemId).Kinds).ToHashSet();
            offered.AddRange(ItemLines.Kinds.Where(k => present.Contains(k.Id)).Select(k => (k.Id, k.Name, k.Tip)));
        }
        var perSlot = new Dictionary<string, int>();
        var perKind = offered.ToDictionary(k => k.Id, _ => 0);
        var toTry = new List<UpgradeTry>();
        var cannot = new List<UpgradeTry>();
        foreach (UpgradeTry t in plan.Tries)
        {
            if (!Named(t)) continue;
            bool inSlots = InSlots(t), inKinds = InKinds(t);
            if (t.Tried)
            {
                if (inKinds) perSlot[t.SlotId] = perSlot.GetValueOrDefault(t.SlotId) + 1;
                if (inSlots)
                    foreach (var (id, _, _) in offered)
                        if (Has(t, id)) perKind[id]++;
            }
            if (inSlots && inKinds) (t.Tried ? toTry : cannot).Add(t);
        }
        return new UpgradePick(toTry, cannot, perSlot, offered, perKind, known, failed, waiting);
    }

    // The choice in words, for the tooltip of the line above the list: the slots and kinds ticked, and the name searched for.
    string ChoiceWords(UpgradePick pick, UpgradeChoice c)
    {
        var parts = new List<string>();
        var slots = _state.Gear.Where(g => c.Slots.Contains(g.SlotId)).Select(g => g.SlotName).Distinct().ToList();
        var kinds = pick.Kinds.Where(k => c.Kinds.Contains(k.Id)).Select(k => k.Name).ToList();
        if (slots.Count > 0) parts.Add(string.Join(", ", slots));
        if (kinds.Count > 0) parts.Add(string.Join(", ", kinds));
        if (c.Search.Trim() != "") parts.Add($"\"{c.Search.Trim()}\"");
        return parts.Count == 0 ? "every item that fits a slot" : string.Join("  ·  ", parts);
    }

    // Read once an item, on the game's thread: the places that give it (ItemIndex) as Item Sources' kinds, and the areas
    // and dungeons where an enemy drops it or that give it as their reward (ItemLines.PlacesOf).
    (HashSet<string> Kinds, HashSet<string> Places) KindsOf(string itemId)
    {
        if (_kinds.TryGetValue(itemId, out var known)) return known;
        var kinds = new HashSet<string>();
        var places = new HashSet<string>();
        try
        {
            if (_game != null && _itemIndex is { } index && _itemLines is { } names
                && _game.Data.GetDataRegistryOutput<Item>().TryGetObjectById(itemId, out Item? item) && index.For(item) is { } entry)
                foreach (ItemRef r in entry.Sources)
                {
                    kinds.Add(ItemLines.KindOf(r));
                    if (r.Owner is Enemy enemy) places.UnionWith(names.PlacesOf(enemy).Select(a => a.Id));
                    else if (r.Owner is Dungeon dungeon) places.Add(dungeon.Id);
                }
        }
        catch (Exception e)
        {
            // One odd piece of the game's data leaves that item without its kinds, not the panel without its choices.
            if (!_sourcesLogged) _ctx.Log($"Where {itemId} comes from could not be told (the game was updated?): {e.Message}");
            _sourcesLogged = true;
        }
        if (kinds.Count == 0) kinds.Add("none");
        return _kinds[itemId] = (kinds, places);
    }

    // ---- the search -----------------------------------------------------------------------------------------

    void IUpgradeLookup.FindUpgrades(SimTarget target)
    {
        // Also a scan that has ended but whose end has not been taken in yet (Pump).
        if (_game == null || _engine.Running || _run != null || _upgradesOff) return;
        UpgradePlan plan = SafePlan();
        if (_upgradesOff) { RefreshShown(); return; }
        UpgradePick pick = Pick(plan, target, _state.UpgradeChoice);
        // The panel does not offer the press then; a request of the development helpers could.
        if (pick.ToTry.Count == 0 || pick.TooMany || pick.Waiting)
        {
            _ctx.Log($"Find upgrades was not started: {(pick.Waiting ? "where items come from is still being read" : $"{pick.ToTry.Count} items chosen, 1 to {UpgradeSearch.MaxItems} can be tried")}.");
            return;
        }
        // Each item one fight of the length chosen, as the setup as it is: never on until sure, and with the dice of the
        // setup as it is kept in step, so all are fought alike and with the same luck. The setup as it is on one side and
        // every item on the other: each item is paired with it, and the items are not paired with each other.
        SimOptions options = RunOptions();
        options.Sure = 0;
        options.Dice = SimResult.MakeKey(options.SetupKey, target.Key);
        options.Side = SimOptions.SideNow;
        options.StatusName = UpgradeSearch.Status;
        var search = new UpgradeSearch(target, _state, options) { Chosen = ChoiceWords(pick, _state.UpgradeChoice) };
        var jobs = new List<SimJob> { new(target, options) };
        foreach (UpgradeTry t in pick.ToTry)
        {
            search.Add(t.Copy());
            SimOptions with = TryOptions(options, t.SetupKey, $"{options.SetupLabel}, {t.ItemName} in {t.SlotName}", t.Overrides, t.Shown, t.ItemName,
                SimOptions.SideItem);
#if DEVTOOLS
            with.DevGear = t.DevSlots;   // what the slots would show with it, for SimEngine.DevGear
#endif
            jobs.Add(new SimJob(target, with));
        }
        if (_state.UpgradeChoice.CannotWear && !pick.TooManyToList)
            foreach (UpgradeTry t in pick.CannotWear) search.Add(t.Copy());
#if DEVTOOLS
        DevTwinJobs(search, jobs, pick);
#endif
        ReadItemSources();
        _state.Message = "";
        if (!_engine.Start(_game, jobs, UpgradeSearch.Workers, out string error))
        {
            _state.Message = error;
            _ctx.Log(error);
            RefreshShown();
            return;
        }
        search.Running = true;
        _upgradeRun = _state.Upgrades = search;
        _state.UpgradesShown = true;
        _state.Viewing = null;
        _state.UpgradeSelected = null;
        _state.UpgradePinned = false;
        _upgradeClock.Restart();
        // In the words of a scan's line ("Simulating N fights"), which a scan's end answers ("Simulation finished").
        _ctx.Log($"Simulating {jobs.Count} fights to find upgrades for {target.Name}{(target.StanceName != "" ? " (" + target.StanceName + ")" : "")}: " +
                 $"{pick.ToTry.Count} items in {pick.Slots} {(pick.Slots == 1 ? "slot" : "slots")} and the setup as it is, {options.Hours:0.##} h each, " +
                 $"supplies {(options.UnlimitedSupplies ? "unlimited" : "from bank")}, with {search.SetupLabel}, {UpgradeSearch.Workers} at a time.");
#if DEVTOOLS
        DevLogPlan(search, plan, pick, jobs);
#endif
        PumpUpgrades();
    }

    void IUpgradeLookup.StopUpgrades()
    {
        if (_upgradeRun != null) _engine.Cancel();
    }

    // The setup of the search with other changes: an item on it, as Simulate would run it after that pick (RunOptions). Its
    // side is said at every call (SimOptions.Side): an item's pairs it with the setup as it is; null pairs it with nothing.
    static SimOptions TryOptions(SimOptions with, string setupKey, string label, IReadOnlyDictionary<string, string> overrides,
        IReadOnlyDictionary<string, string>? shown, string status, string? side) => new()
    {
        Hours = with.Hours,
        UnlimitedSupplies = with.UnlimitedSupplies,
        LoadoutId = with.LoadoutId,
        Overrides = new Dictionary<string, string>(overrides),
        SpellId = with.SpellId,
        StanceId = with.StanceId,
        StanceName = with.StanceName,
        SetupKey = setupKey,
        SetupLabel = label,
        Sure = with.Sure,
        SureSizes = with.SureSizes,
        MostHours = with.MostHours,
        Watch = with.Watch,
        Shown = shown == null ? null : new Dictionary<string, string>(shown),
        Dice = with.Dice,
        Side = side,
        StatusName = status,
#if DEVTOOLS
        DevGear = with.DevGear,
#endif
    };

    /// <summary>
    /// From Pump, while a search is under way: its results go to the search, never to the current results or the
    /// history. True while it runs, and on the tick it ends.
    /// </summary>
    bool PumpUpgrades()
    {
        bool indexed = Interlocked.Exchange(ref _itemIndexArrived, 0) == 1;
        if (_upgradeRun is not { } search)
        {
            if (indexed)
            {
                _view?.UpgradesChanged();
                RefreshShown();
            }
            return false;
        }
        // Asked first: once the engine says it stopped, everything it made is already in the queue.
        bool running = _engine.Running, took = false;
        while (_engine.Finished.TryDequeue(out SimResult? result))
        {
#if DEVTOOLS
            _engine.DevTakenIn(result);   // the verdict lines of an item and the setup as it is, as Pump's of a compare
#endif
            took |= search.Take(result);
        }
        bool runningChanged = running != _state.Running;
        bool changed = took || indexed || runningChanged || _state.Done != _engine.Done || _state.Current != _engine.Current;
        _state.Running = running;
        _state.Done = _engine.Done;
        _state.Total = _engine.Total;
        _state.Current = _engine.Current;
        if (!running)
        {
            _upgradeRun = null;
            search.Running = false;
            search.Stopped = _engine.Done < _engine.Total;
            search.At = DateTime.UtcNow;
            _ctx.Log($"Simulation finished: {_engine.Done} fights in {_upgradeClock.Elapsed.TotalSeconds:0.0}s, to find upgrades for {search.Target.Name}" +
                     (search.Stopped ? $", stopped after {search.Answered} of {search.Asked} items." : "."));
            if (_engine.Problem != "") _state.Message = _engine.Problem;
#if DEVTOOLS
            DevLogFinished(search);
#endif
        }
        if (!running || indexed || (took && _upgradeShow.Due()))
        {
            search.Publish();
            _view?.UpgradesChanged();
        }
        if (runningChanged) KeepTitleRoom();
        if (changed) RefreshShown();
        return true;
    }

    // The window is only touched on the game's thread, and folded only its title (as Pump does).
    void RefreshShown()
    {
        if (_window is not { IsAlive: true, Visible: true }) return;
        if (_window.Minimised)
        {
            _refreshOnUnfold = true;
            UpdateTitle();
        }
        else _view?.Refresh();
    }

    // ---- where an item comes from ---------------------------------------------------------------------------

    void ReadItemSources()
    {
        if (_itemIndexAsked || _game is not { } game) return;
        _itemIndexAsked = true;
        _itemLines = new ItemLines(game);
        // Reading the whole of the game's data takes a moment: beside the game, not in its way (ItemSourcesMod.Open).
        Task.Run(() =>
        {
            try
            {
                var clock = Stopwatch.StartNew();
                ItemIndex index = ItemIndex.Build(game);
                if (!ReferenceEquals(game, _game)) return;
                _itemIndex = index;
                _ctx.Log($"Where items come from: read {index.Owners:N0} game objects in {clock.ElapsedMilliseconds:N0} ms, sources known for {index.Count:N0} items.");
            }
            catch (Exception e)
            {
                _ctx.Log("Where items come from could not be read (the game was updated?): " + e.Message + ". Find upgrades lists the items without it.");
                if (ReferenceEquals(game, _game)) _itemIndexFailed = true;
            }
            Interlocked.Exchange(ref _itemIndexArrived, 1);
        });
    }

    UpgradeSource IUpgradeLookup.SourceOf(string itemId)
    {
        if (_itemIndex is not { } index || _itemLines is not { } names || _game == null)
            return _itemIndexFailed ? new UpgradeSource("-", "", Array.Empty<ItemLine>(), "The game's data could not be read: see MelvorModLoader.log.")
                : new UpgradeSource("...", "", Array.Empty<ItemLine>(), "Reading the game's data...");
        if (_sources.TryGetValue(itemId, out UpgradeSource? known)) return known;
        UpgradeSource made;
        try
        {
            List<ItemRef> refs = _game.Data.GetDataRegistryOutput<Item>().TryGetObjectById(itemId, out Item? item) ? index.For(item)?.Sources ?? new() : new();
            // Combat first, then the rest by name, the likeliest on top inside a group: as Item Sources' tooltip.
            List<ItemLine> lines = names.Sorted(refs);
            var groups = lines.GroupBy(l => l.Group).Select(g => (Group: g.Key, Count: g.Count())).ToList();
            made = lines.Count == 0 ? new UpgradeSource("No source", "", lines, "Nothing in the game's data gives this item.")
                : new UpgradeSource(string.Join(", ", groups.Select(g => g.Group)),
                    "Found in\n" + string.Join("\n", groups.Select(g => g.Count == 1 ? g.Group : $"{g.Group}  ({g.Count})")) +
                    "\nClick the line: Details lists every place.", lines, "");
        }
        catch (Exception e)
        {
            // One odd piece of the game's data leaves that item without its sources, not the list without its items.
            if (!_sourcesLogged) _ctx.Log($"Where {itemId} comes from could not be told (the game was updated?): {e.Message}");
            _sourcesLogged = true;
            made = new UpgradeSource("-", "", Array.Empty<ItemLine>(), "Where it comes from could not be read: see MelvorModLoader.log.");
        }
        return _sources[itemId] = made;
    }

#if DEVTOOLS
    // ---- development ----------------------------------------------------------------------------------------

    // What a request found before its first line, to put back at its end (PutBackWindow): the player's last search and
    // how the list was shown. The request starts without it, so that its own presses open the panel; its own searches
    // are not his.
    (UpgradeSearch? Search, bool Shown, string? Selected, bool Pinned)? _devUpgrades;

    void DevKeepUpgrades()
    {
        _devUpgrades = (_state.Upgrades, _state.UpgradesShown, _state.UpgradeSelected, _state.UpgradePinned);
        if (_upgradeRun != null) return;   // his own search runs: it stays his, and on show
        _state.Upgrades = null;
        _state.UpgradesShown = false;
        _view?.UpgradesChanged();
    }

    string DevPutBackUpgrades()
    {
        if (_devUpgrades is not { } kept) return "nothing kept";
        _devUpgrades = null;
        string before = DevUpgradeState();
        (_state.Upgrades, _state.UpgradesShown, _state.UpgradeSelected, _state.UpgradePinned) = kept;
        _view?.UpgradesChanged();
        return $"{before} put back to {DevUpgradeState()}";
    }

    string DevUpgradeState() => _state.Upgrades is not { } u ? "no search" :
        $"search {u.Id} for {u.Target.Name} ({u.Asked} items, {(u.Running ? "running" : u.Stopped ? "stopped" : "done")}), list {(_state.UpgradesShown ? "shown" : "not shown")}";

    static string DevChoice(UpgradeChoice c) =>
        $"slots [{string.Join(", ", c.Slots)}], kinds [{string.Join(", ", c.Kinds)}], search '{c.Search}', list what cannot be worn {c.CannotWear}";

    /// <summary>
    /// Development (upgrades=bankweapon, issue #63): a weapon that fights unlike the setup as it is, and fights at all. Of
    /// the items Find upgrades would try in the slot of the setup's weapon (SafePlan): one in your bank that the character
    /// can wear, that needs no ammunition (WeaponCapability.RequiredAmmoType) and whose stances all fight in a combat style of
    /// the setup's weapon (AttackStance.CombatStyle: melee for a sword), at another attack speed. The weapon is the one in
    /// the slot named Weapon, as stance=weapon finds it (DevStanceWeapon). The speed is the one stat of the weapon's
    /// EquipmentCapability.Stats whose id or name says interval or speed, read from the game's items; every stat of the
    /// setup's weapon is logged with it, so the choice can be checked. More than one such stat, or none, and nothing is
    /// chosen; a weapon of the bank that lacks the stat is "no speed read", never a speed of 0 (review of turn 2). Of those
    /// left, the one furthest from the setup's speed whose name, typed into the panel's search box, leaves the fewest items
    /// to try in that slot (at most 4). Its name, or null with why not. Read only: nothing is put on.
    /// </summary>
    string DevBankWeapon(out string? name)
    {
        name = null;
        if (_game == null) return "no character open";
        var items = _game.Data.GetDataRegistryOutput<Item>();
        // The setup's own weapon: the slot named Weapon on the left.
        if (_state.Gear.FirstOrDefault(g => g.SlotName.Equals("Weapon", StringComparison.OrdinalIgnoreCase)) is not { } slot)
            return "THE SETUP HOLDS NO WEAPON: no Weapon slot on the left";
        if (slot is not { ItemId: { } wornId, FilledFrom: "" } || !items.TryGetObjectById(wornId, out Item? worn)
            || !worn.Capabilities.TryGet(out WeaponCapability? wornWeapon) || wornWeapon == null
            || !worn.Capabilities.TryGet(out EquipmentCapability? wornGear) || wornGear == null)
            return $"THE SETUP HOLDS NO WEAPON: its Weapon slot holds {(slot.ItemId == null ? "nothing" : $"{slot.ItemName} ({slot.ItemId}), no weapon")}";
        static HashSet<string> Styles(WeaponCapability w) => w.Type.Stances.Select(s => s.CombatStyle?.Id).OfType<string>().ToHashSet();
        HashSet<string> styles = Styles(wornWeapon);
        var stats = wornGear.Stats.Stats;
        string all = string.Join(", ", stats.Select(s => $"{s.Stat.Id} '{s.Stat.DisplayName.Text}' {s.Value:0.###}"));
        var speeds = stats.Select(s => s.Stat).Where(s => s.Id.Contains("Interval", StringComparison.OrdinalIgnoreCase) || s.Id.Contains("Speed", StringComparison.OrdinalIgnoreCase)
            || s.DisplayName.Text.Contains("Interval", StringComparison.OrdinalIgnoreCase) || s.DisplayName.Text.Contains("Speed", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(s => s.Id).ToList();
        string setup = $"the setup's weapon {worn.DisplayName.GetPluralText(1)} ({worn.Id}) in {slot.SlotName}, styles [{string.Join(", ", styles)}]; its stats: {all}";
        if (speeds.Count == 0) return $"{setup}; NO ATTACK SPEED READ: no stat of it says interval or speed";
        if (speeds.Count > 1) return $"{setup}; NO ATTACK SPEED READ: {speeds.Count} of its stats say interval or speed ({string.Join(", ", speeds.Select(s => s.Id))}), so none is taken";
        var speed = speeds[0];
        double wornSpeed = wornGear.Stats.GetCombatStatTotal(speed);
        UpgradePlan plan = SafePlan();
        var inSlot = plan.Tries.Where(t => t.SlotId == slot.SlotId && t.Tried).ToList();
        var looked = new List<string>();
        var fit = new List<(UpgradeTry Try, double Speed, int Leaves)>();
        foreach (UpgradeTry t in inSlot.Where(t => t.InBank > 0))
        {
            if (!items.TryGetObjectById(t.ItemId, out Item? item) || !item.Capabilities.TryGet(out WeaponCapability? w) || w == null
                || !item.Capabilities.TryGet(out EquipmentCapability? e) || e == null)
            {
                looked.Add($"{t.ItemName}: not a weapon");
                continue;
            }
            HashSet<string> its = Styles(w);
            // Its speed only where it has the stat: a weapon without it is not read as 0, the furthest of all from any speed.
            double? at = e.Stats.Stats.Any(s => s.Stat.Id == speed.Id) ? e.Stats.GetCombatStatTotal(speed) : null;
            string[] words = t.ItemName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int leaves = inSlot.Count(o => words.All(word => o.ItemName.Contains(word, StringComparison.OrdinalIgnoreCase)));
            string why = w.RequiredAmmoType != null ? "needs ammunition"
                : its.Count == 0 || !its.IsSubsetOf(styles) ? $"another style [{string.Join(", ", its)}]"
                : at is not { } itsSpeed ? $"no speed read (no {speed.Id})"
                : Math.Abs(itsSpeed - wornSpeed) < 1e-9 ? "the same speed"
                : leaves > 4 ? $"its name leaves {leaves} items"
                : "fits";
            looked.Add($"{t.ItemName} x{t.InBank} speed {at?.ToString("0.###") ?? "-"}: {why}");
            if (why == "fits" && at is { } chosenSpeed) fit.Add((t, chosenSpeed, leaves));
        }
        string read = $"{setup}; speed: {speed.Id} '{speed.DisplayName.Text}' {wornSpeed:0.###}; of {inSlot.Count} items it would try there, in your bank: " +
                      (looked.Count == 0 ? "none" : string.Join(" | ", looked));
        if (fit.Count == 0) return read + "; NO WEAPON OF ANOTHER SPEED IN YOUR BANK";
        var chosen = fit.OrderByDescending(f => Math.Abs(f.Speed - wornSpeed)).ThenBy(f => f.Leaves).ThenBy(f => f.Try.ItemName, StringComparer.OrdinalIgnoreCase).First();
        name = chosen.Try.ItemName;
        return read + $"; chose {name} (speed {chosen.Speed:0.###} against {wornSpeed:0.###}), whose name leaves {chosen.Leaves} {(chosen.Leaves == 1 ? "item" : "items")} in {slot.SlotName}";
    }

    // The setup as it is twice more (UpgradeSearch.DevAgainKey, DevLuckKey). "Again": the piece of the first slot tried
    // that holds one of its own, one at a time (not a pile of arrows), put on again through the game's equip call as an
    // item goes on, with the search's dice, and paired with the setup as it is as an item is (SimOptions.SideItem). "Luck":
    // dice of its own, kept in step the same way, and no side: nothing shares its dice, so it is judged as before.
    void DevTwinJobs(UpgradeSearch search, List<SimJob> jobs, UpgradePick pick)
    {
        SimOptions o = search.Options;
        var overrides = new Dictionary<string, string>(o.Overrides);
        search.DevAgainWhat = "nothing put on again: no slot holds a piece of its own";
        foreach (string slotId in pick.ToTry.Select(t => t.SlotId).Concat(_state.Gear.Select(g => g.SlotId)).Distinct())
            if (_state.Gear.FirstOrDefault(g => g.SlotId == slotId) is { ItemId: { } own, FilledFrom: "", Quantity: <= 1 } slot)
            {
                overrides[slotId] = own;
                search.DevAgainWhat = $"{slot.ItemName} put on again in {slot.SlotName}";
                break;
            }
        jobs.Add(new SimJob(search.Target, TryOptions(o, o.SetupKey + UpgradeSearch.DevAgainKey, o.SetupLabel + ", again", overrides, o.Shown, "the setup as it is, again",
            SimOptions.SideItem)));
        SimOptions luck = TryOptions(o, o.SetupKey + UpgradeSearch.DevLuckKey, o.SetupLabel + ", other dice", o.Overrides, o.Shown, "the setup as it is, other dice", null);
        luck.Dice = SimResult.MakeKey(luck.SetupKey, search.Target.Key);
        jobs.Add(new SimJob(search.Target, luck));
    }

    // The count of items it will try, and what it read and decided for them, before the first fight; the side of each fight
    // as its options have it (SimOptions.Side), which decides what is paired with what.
    void DevLogPlan(UpgradeSearch search, UpgradePlan plan, UpgradePick pick, List<SimJob> jobs)
    {
        SimOptions o = search.Options;
        int fights = jobs.Count;
        _ctx.Log($"Dev: upgrades: for {search.Target.Name} [{search.Target.Key}] with {search.SetupLabel} (key {search.SetupKey}), {o.Hours:0.##} h, supplies {(o.UnlimitedSupplies ? "unlimited" : "from bank")}, " +
                 $"sure {o.Sure:0.###}, dice of {o.Dice} kept in step, {UpgradeSearch.Workers} at a time (a scan {_state.Workers}); " +
                 $"sides: the setup as it is '{o.Side}', every item and again " +
                 $"'{string.Join("/", jobs.Skip(1).Where(j => !j.Options.SetupKey.EndsWith(UpgradeSearch.DevLuckKey, StringComparison.Ordinal)).Select(j => j.Options.Side ?? "none").Distinct())}', " +
                 $"luck '{jobs.FirstOrDefault(j => j.Options.SetupKey.EndsWith(UpgradeSearch.DevLuckKey, StringComparison.Ordinal))?.Options.Side ?? "none"}'; choice {DevChoice(_state.UpgradeChoice)}: " +
                 $"{pick.ToTry.Count} items to try in {pick.Slots} slots, {search.Tries.Count(t => !t.Tried)} listed without trying ({pick.CannotWear.Count} cannot be worn in the choice), " +
                 $"of {plan.Tries.Count} in the plan ({plan.Skipped} left out: held there already, or the same setup as another pick); {fights} fights with the setup as it is " +
                 $"and its two checks (again: {search.DevAgainWhat}). Estimate: {_view?.DevUpgradeEstimate(pick.ToTry.Count + 1) ?? "-"}");
        DevChunksLog("Dev: upgrades: per slot", pick.ToTry.GroupBy(t => t.SlotName).Select(g => $"{g.Key} {g.Count()}"));
        DevChunksLog("Dev: upgrades: cannot wear", search.Tries.Where(t => !t.Tried).Take(30).Select(t => $"{t.ItemName} ({t.SlotName}): '{t.Needs}'"));
        // Every pick that takes more than its own slot's item off or empties a slot (two-handed weapons, shields), and the first few others.
        var shown = pick.ToTry.Where(t => t.InPlaceOf.Contains(" and ") || t.InPlaceOf.Contains("which leaves", StringComparison.Ordinal)).Take(12)
            .Concat(pick.ToTry.Take(6)).Distinct();
        foreach (UpgradeTry t in shown)
            _ctx.Log($"Dev: upgrades: try {t.ItemName} ({t.ItemId}) in {t.SlotId}, {t.InPlaceOf}; bank {t.InBank}, worn {t.Worn}; changes {DevChanges(t.Overrides)}, key {t.SetupKey}; " +
                     $"the slots would show: {t.DevSlots}");
    }

    void DevLogFinished(UpgradeSearch search)
    {
        SimResult? b = search.Baseline;
        var tried = search.Tries.Where(t => t.Result != null).ToList();
        _ctx.Log($"Dev: upgrades: {(search.Stopped ? "stopped" : "finished")} after {_engine.Done} of {_engine.Total} fights in {_upgradeClock.Elapsed.TotalSeconds:0.0}s; the setup as it is: " +
                 (b == null ? "NO RESULT" : $"{(b.Failed ? "FAILED " + b.Note : $"kills {b.KillsPerHour:N0}, xp {b.XpPerHour:N0}, gold {b.GoldPerHour:N0}, deaths {b.DeathsPerHour:0.##}, food {b.FoodPerHour:0.#}{(b.Note != "" ? ", note: " + b.Note : "")}")}") +
                 $"; {tried.Count} items have a result: {tried.Count(t => t.Result!.Failed)} failed, {tried.Count(t => t.Result!.WearNote != "")} not worn as asked, " +
                 $"{tried.Count(t => t.Result!.Note != "" && t.Result.WearNote == "" && !t.Result.Failed)} with another note");
        DevChunksLog("Dev: upgrades: not worn as asked", tried.Where(t => t.Result!.WearNote != "").Take(20).Select(t => $"{t.ItemName} ({t.SlotName}): {t.Result!.WearNote}"));
        DevChunksLog("Dev: upgrades: other notes", tried.Where(t => t.Result!.Note != "" && t.Result.WearNote == "").Take(20).Select(t => $"{t.ItemName} ({t.SlotName}): {t.Result!.Note}"));
        if (b != null)
        {
            DevChunksLog("Dev: upgrades: kills per hour against " + b.KillsPerHour.ToString("N0"),
                tried.Where(t => !t.Result!.Failed && t.Result.WearNote == "").OrderByDescending(t => t.Result!.KillsPerHour).Take(15)
                    .Select(t => $"{t.ItemName} ({t.SlotName}) {t.Result!.KillsPerHour:N0}"));
            var plain = tried.Where(t => !t.Result!.Failed && t.Result.WearNote == "").Select(t => t.Result!.KillsPerHour).ToList();
            if (plain.Count > 0)
                _ctx.Log($"Dev: upgrades: the items' kills per hour: {plain.Count} of them, on average {plain.Average():N1}, {plain.Count(k => k > b.KillsPerHour)} above the setup as it is, " +
                         $"{plain.Count(k => k < b.KillsPerHour)} below, {plain.Count(k => k == b.KillsPerHour)} the same");
        }
        string twins = _view?.DevUpgradeTwins(search) ?? "the window is not open: no columns to judge by";
        _ctx.Log("Dev: upgrades: the setup as it is, checked: " + twins);
    }

    void DevChunksLog(string head, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0) { _ctx.Log(head + ": none"); return; }
        string line = "";
        int part = 1;
        foreach (string item in list)
        {
            if (line != "" && head.Length + line.Length + item.Length > 320) { _ctx.Log($"{head} ({part++}): {line}"); line = ""; }
            line += (line == "" ? "" : " | ") + item;
        }
        if (line != "") _ctx.Log($"{head} ({part}): {line}");
    }

    // Lines of sim.request (DevWindowCommand); none of them changes the character, and the put back at the end of the
    // request brings back the player's own last search, how its list was shown, and the choice of the panel:
    //   upgrades=<part of an enemy's name>   selects that fight's line in the list on show, as a click on it does, and
    //                                        presses Find upgrades under Details as the player's click does: the panel
    //                                        opens (or the items come back on show, when the button says Show upgrades)
    //   upgrades=slot=<part of a slot name>, upgrades=from=<part of a kind's words>, upgrades=cannot
    //                                        a click on that tick of the panel
    //   upgrades=search=<words>              types the words into the panel's search box ("upgrades=search=" empties it)
    //   upgrades=clear                       unticks every slot and kind and empties the search box, as those clicks do
    //   upgrades=bankweapon                  types into the search box the name of a weapon in your bank that fights unlike
    //                                        the setup's weapon: the same combat style, no ammunition, another attack speed
    //                                        (DevBankWeapon); without one, one line and the rest of the request is dropped
    //   upgrades=go=<most>                   presses Simulate in the panel when its own line counts <most> items or fewer;
    //                                        otherwise, or without a most, one line with the count and the rest of the
    //                                        request is dropped (the maintainer plays while it runs); follow with
    //                                        wait=finished
    //   upgrades=panel                       logs what the panel shows: its ticks, their counts, the line and the button
    //   upgrades=stop                        presses the button when it says Stop
    //   upgrades=back                        presses "Back to the fights"
    //   upgrades=line=<part of an item name> presses that item's line, as a click does
    //   upgrades=hover=<part of an item name> rests the pointer on that line's "Comes from"; a moment later its tooltip
    //   upgrades=log                         logs what the list, its header and Details show (also "upgrades" in the
    //                                        picker field of a run line)
    void DevUpgradesCommand(string what)
    {
        if (_view == null || _window is not { IsAlive: true, Visible: true }) Open();
        if (_view == null) { _ctx.Log("Dev: upgrades: the simulator could not be opened."); return; }
        _window!.SetMinimised(false);
        if (what is "stop" or "back")
        {
            _ctx.Log("Dev: upgrades: " + _view.DevUpgradePress(what));
            return;
        }
        if (what == "log") { _view.DevLogUpgrades(); return; }
        if (what.StartsWith("line=", StringComparison.Ordinal) || what.StartsWith("hover=", StringComparison.Ordinal))
        {
            _ctx.Log("Dev: upgrades: " + _view.DevUpgradeLine(what));
            return;
        }
        if (what == "bankweapon")
        {
            string found = DevBankWeapon(out string? weapon);
            if (weapon == null)
            {
                _ctx.Log($"Dev: upgrades: bankweapon: {found}; the rest of the request is dropped.");
                _devCommands.Clear();
                return;
            }
            _ctx.Log($"Dev: upgrades: bankweapon: {found}; " + _view.DevUpgradeChoice("search=" + weapon));
            return;
        }
        if (what == "go" || what.StartsWith("go=", StringComparison.Ordinal))
        {
            // Never more items than the request names: the count is read back from the panel's own line before the press.
            int most = 0;
            bool number = what.Length > 3 && int.TryParse(what[3..], out most), named = number && most > 0;
            int? count = _view.DevChoiceCount();
            if (!named || count is not { } n || n > most)
            {
                _ctx.Log($"Dev: upgrades: {what}: NOT PRESSED: " + (!number ? "the line names no most (upgrades=go=<most>)"
                    : !named ? $"a most of {most} lets no item be tried (upgrades=go=<most>, 1 or more)"
                    : count == null ? "the panel's line gives no count" : $"the panel counts {count} items, more than the {most} the request names") +
                         $"; the rest of the request is dropped. {_view.DevUpgradeChoice("panel")}");
                _devCommands.Clear();
                return;
            }
            string pressed = _view.DevUpgradeChoice("go");
            if (_upgradeRun == null)
            {
                _ctx.Log($"Dev: upgrades: {what}: the panel counts {n} items, but NO SEARCH STARTED ({pressed}); the rest of the request is dropped.");
                _devCommands.Clear();
                return;
            }
            _ctx.Log($"Dev: upgrades: {what}: the panel counts {n} items, at most {most} named; {pressed}");
            return;
        }
        if (what is "cannot" or "clear" or "panel" || what.StartsWith("slot=", StringComparison.Ordinal)
            || what.StartsWith("from=", StringComparison.Ordinal) || what.StartsWith("search=", StringComparison.Ordinal))
        {
            _ctx.Log("Dev: upgrades: " + _view.DevUpgradeChoice(what));
            return;
        }
        // A fight of the list on show, selected as a click on its line does.
        SimResult? pick = _state.Lines().FirstOrDefault(r => r.Target.Name.Contains(what, StringComparison.OrdinalIgnoreCase) && !r.Failed);
        if (pick == null)
        {
            _ctx.Log($"Dev: upgrades: no fight called {what} in the list on show ({_state.Lines().Count} lines); the rest of the request is dropped.");
            _devCommands.Clear();
            return;
        }
        _state.Selected = pick.Target.Key;
        _state.Pinned = true;
        _view.Refresh();
        _ctx.Log($"Dev: upgrades: selected {pick.Target.Name} [{pick.Target.Key}]; " + _view.DevUpgradePress("find"));
    }
#endif
}
