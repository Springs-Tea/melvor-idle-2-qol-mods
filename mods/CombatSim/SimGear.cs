using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using GamesByMalcs.Melvor2.Components.Equipment;
using GamesByMalcs.Melvor2.Data.Items;

namespace CombatSim;

/// <summary>
/// What one slot holds: an item and how many. <paramref name="FilledFrom"/> is null when the item is worn in this slot,
/// and the id of the slot it is worn in when it only fills this one too (the Offhand of a two-handed weapon).
/// </summary>
readonly record struct GearPiece(Item Item, int Quantity, string? FilledFrom);

/// <summary>
/// An item in a slot as the game has it: how many the game keeps there, and whether the game marks the slot as holding
/// another slot's item (IsOccupied). What <see cref="SimGear.FilledFrom"/> reads, for the slots on the left and for
/// "Wear this setup first" (FightSetup.cs) alike.
/// </summary>
readonly record struct Slotted(EquipmentSlot Slot, Item Item, int Quantity, bool Marked);

/// <summary>
/// What is worn, slot by slot, by the slot's id. A slot that is not in it is empty. <see cref="Known"/>: the slots it was
/// read in (the character's), the only ones an item can fill.
/// </summary>
sealed class GearSet(HashSet<string> known)
{
    public readonly Dictionary<string, GearPiece> Slots = new();
    public readonly HashSet<string> Known = known;

    public GearPiece? this[string slotId] => Slots.TryGetValue(slotId, out GearPiece piece) ? piece : null;

    public GearSet Copy()
    {
        var copy = new GearSet(Known);
        foreach (var (slot, piece) in Slots) copy.Slots[slot] = piece;
        return copy;
    }

    /// <summary>
    /// Slot id -> item id of every item worn in its own slot: a two-handed weapon once, in the Weapon slot. For comparing
    /// what two sets of equipment fight with (SimOptions.Shown).
    /// </summary>
    public Dictionary<string, string> OwnItems() =>
        Slots.Where(s => s.Value.FilledFrom == null).ToDictionary(s => s.Key, s => s.Value.Item.Id);
}

/// <summary>
/// The equipment the simulator fights with, by the game's own rules for an item that fills more than one slot
/// (issue #46), and the one place of the mod that knows them. Which slots another slot's item fills is the game's
/// data, nothing of the mod's own: the slots the game marks as occupied (IActiveEquipmentSlot.IsOccupied for what is
/// worn, EquipmentSlotLoadout.IsOccupied for a saved loadout) and the slots of the worn item's
/// EquipmentCapability.OccupiesSlots (<see cref="FillsToo"/>). A two-handed weapon is worn in the Weapon slot and fills
/// the Offhand too.
/// <list type="bullet">
/// <item>Putting an item on takes off what is in every slot it fills, each of those items from every slot that one
/// fills: a two-handed weapon takes the shield off, a shield the two-handed weapon (<see cref="Put"/>).</item>
/// <item>Taking an item off empties every slot it fills. A slot that another slot's item fills is not emptied on its own
/// (<see cref="TakeOff"/>).</item>
/// </list>
/// The player's changes (SimState.Overrides) are kept in one shape only (<see cref="Overrides"/>), and the left of the
/// window (<see cref="Resolve"/>) and the copy that fights (SimEngine.Wear) take them in the same order
/// (<see cref="Steps"/>), so the copy ends with what the slots on the left show. "Wear this setup first" (FightSetup.cs)
/// reads the same rule through <see cref="FilledFrom"/> and <see cref="FillsToo"/>. When the game no longer has these
/// members, one line in the log and <see cref="Off"/>: no slot is filled from another, every slot is on its own, as
/// before issue #46, on the left, in the item picker and on the copy alike, and "Wear this setup first" switches off.
/// </summary>
static class SimGear
{
    static int s_off;
    static string? s_unlogged;

    /// <summary>The game's members for slots filled from another slot are gone: every slot is on its own.</summary>
    public static bool Off => Volatile.Read(ref s_off) != 0;

    static void Stop(Exception e, Action<string>? log)
    {
        if (Interlocked.Exchange(ref s_off, 1) != 0) return;
        string line = "Two-handed weapons cannot be read in this game version (the game was updated?): " + e.Message +
                      ". The simulator's equipment slots work one by one, as before.";
        if (log != null) log(line);
        else s_unlogged = line;   // found on a copy: written by the next call that has the log
    }

    static void Report(Action<string>? log)
    {
        if (log != null && Interlocked.Exchange(ref s_unlogged, null) is { } line) log(line);
    }

    // ---- reading what is worn ---------------------------------------------------------------------------------

    /// <summary>What an equipment component has on, the character's or a copy's, in the slots given.</summary>
    public static GearSet Live(EquipmentComponent equipment, IReadOnlyCollection<EquipmentSlot> slots, Action<string>? log)
    {
        Report(log);
        var found = new List<Slotted>();
        foreach (EquipmentSlot slot in slots)
            if (equipment.TryGetActiveSlot(slot, out IActiveEquipmentSlot? active) && active?.Equipped?.Item is { } item)
                found.Add(new Slotted(slot, item, active.Quantity, IsFilled(active, log)));
        return Link(found, slots, log);
    }

    /// <summary>What a saved loadout holds, as the game stored it when it was last worn, in the slots given.</summary>
    public static GearSet Stored(EquipmentLoadout loadout, IReadOnlyCollection<EquipmentSlot> slots, Action<string>? log)
    {
        Report(log);
        var found = new List<Slotted>();
        foreach (EquipmentSlot slot in slots)
            if (loadout.Slots.TryGetValue(slot, out EquipmentSlotLoadout? stored) && stored?.Equipped?.Item is { } item)
                found.Add(new Slotted(slot, item, stored.Quantity, IsFilled(stored, log)));
        return Link(found, slots, log);
    }

    // Every item where FilledFrom has it, and a slot of OccupiesSlots that the game kept no item in filled too. Off:
    // every item is worn where it is, as before issue #46.
    static GearSet Link(List<Slotted> found, IReadOnlyCollection<EquipmentSlot> slots, Action<string>? log)
    {
        var set = new GearSet(slots.Select(s => s.Id).ToHashSet());
        Dictionary<string, string> from = FilledFrom(found, log);
        foreach (Slotted f in found)
            set.Slots[f.Slot.Id] = from.TryGetValue(f.Slot.Id, out string? owner) ? new GearPiece(f.Item, 0, owner) : new GearPiece(f.Item, f.Quantity, null);
        foreach (var (owner, piece) in set.Slots.Where(s => s.Value.FilledFrom == null).ToList())
            foreach (string other in FillsToo(piece.Item, owner, log))
                if (set[other] == null && set.Known.Contains(other)) set.Slots[other] = new GearPiece(piece.Item, 0, owner);
        if (!Off) return set;
        // Off now, or on the way (a member gone while this was read): every item where it is.
        var own = new GearSet(set.Known);
        foreach (Slotted f in found) own.Slots[f.Slot.Id] = new GearPiece(f.Item, f.Quantity, null);
        return own;
    }

    /// <summary>
    /// The one rule for a slot that holds another slot's item (the Offhand of a two-handed weapon): of each such slot, the
    /// id of the slot its item is worn in; a slot with an item of its own is not in it. By the game's data only: an item
    /// is worn in a slot that holds it and that the game does not mark, one its OccupiesSlots does not list first (the
    /// Weapon slot). Another slot that holds the same item is filled from there when the game marks it or the item's
    /// OccupiesSlots lists it; otherwise it holds an item of its own (the same ring twice). Empty when <see cref="Off"/>.
    /// </summary>
    public static Dictionary<string, string> FilledFrom(IReadOnlyCollection<Slotted> found, Action<string>? log = null)
    {
        var filled = new Dictionary<string, string>();
        var owners = new Dictionary<string, string>();
        foreach (Slotted f in found.Where(f => !f.Marked).OrderBy(f => OccupiesOf(f.Item, log).Contains(f.Slot.Id)))   // a stable order
            owners.TryAdd(f.Item.Id, f.Slot.Id);
        foreach (Slotted f in found)
            if (owners.GetValueOrDefault(f.Item.Id) is { } owner && owner != f.Slot.Id && (f.Marked || FillsToo(f.Item, owner, log).Contains(f.Slot.Id)))
                filled[f.Slot.Id] = owner;
        return Off ? new() : filled;
    }

    /// <summary>The slot holds the item of another slot (the game's IsOccupied). False when the game cannot say.</summary>
    public static bool IsFilled(IActiveEquipmentSlot slot, Action<string>? log = null)
    {
        if (Off) return false;
        try { return Occupied(slot); }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException) { Stop(e, log); return false; }
    }

    /// <summary>The same for a slot of a saved loadout, as the game stored it (EquipmentSlotLoadout.IsOccupied).</summary>
    public static bool IsFilled(EquipmentSlotLoadout slot, Action<string>? log = null)
    {
        if (Off) return false;
        try { return Occupied(slot); }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException) { Stop(e, log); return false; }
    }

    // Their own methods, never inlined: a game update that removed these fails when they are called, inside the try.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool Occupied(IActiveEquipmentSlot slot) => slot.IsOccupied;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool Occupied(EquipmentSlotLoadout slot) => slot.IsOccupied;

    /// <summary>
    /// The other slots an item fills when it is worn in this one: the game's EquipmentCapability.OccupiesSlots. None when
    /// <see cref="Off"/>.
    /// </summary>
    public static List<string> FillsToo(Item item, string slotId, Action<string>? log = null) =>
        OccupiesOf(item, log).Where(slot => slot != slotId).Distinct().ToList();

    // The item's OccupiesSlots as the game lists them; none when Off or when the game no longer has them.
    static List<string> OccupiesOf(Item item, Action<string>? log)
    {
        if (Off) return new();
        try { return Occupies(item); }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException) { Stop(e, log); return new(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static List<string> Occupies(Item item) =>
        item.Capabilities.TryGet(out EquipmentCapability? wearable) && wearable.OccupiesSlots is { } slots
            ? slots.Select(slot => slot.Id).ToList() : new();

    /// <summary>Both hold the same: nothing, or the same item worn in the same slot.</summary>
    public static bool Same(GearPiece? a, GearPiece? b) =>
        a is { } x ? b is { } y && x.Item.Id == y.Item.Id && x.FilledFrom == y.FilledFrom : b == null;

    // ---- changing what is worn -------------------------------------------------------------------------------

    /// <summary>Takes off the item worn in this slot, from every slot it fills. A slot another slot's item fills stays.</summary>
    public static void TakeOff(GearSet worn, string slotId)
    {
        if (worn[slotId] is { FilledFrom: null }) Remove(worn, slotId);
    }

    // The item worn in this slot, out of it and out of every slot it fills.
    static void Remove(GearSet worn, string owner)
    {
        foreach (string slot in worn.Slots.Where(s => s.Key == owner || s.Value.FilledFrom == owner).Select(s => s.Key).ToList())
            worn.Slots.Remove(slot);
    }

    /// <summary>
    /// Puts an item on, as equipping it in the game does: whatever is in its slot and in the slots it fills comes off
    /// first, each from every slot it fills.
    /// </summary>
    public static void Put(GearSet worn, EquipmentSlot slot, Item item, int quantity, Action<string>? log = null)
    {
        List<string> fills = FillsToo(item, slot.Id, log).Where(worn.Known.Contains).ToList();
        foreach (string target in fills.Prepend(slot.Id))
            if (worn[target] is { } there) Remove(worn, there.FilledFrom ?? target);
        worn.Slots[slot.Id] = new GearPiece(item, quantity, null);
        foreach (string target in fills) worn.Slots[target] = new GearPiece(item, 0, slot.Id);
    }

    /// <summary>
    /// Puts back what the loadout has in this slot, and in the slots tied to it now or in the loadout: both hands, when
    /// a two-handed weapon is or was in them.
    /// </summary>
    public static void Restore(GearSet worn, GearSet loadout, IEnumerable<EquipmentSlot> slots, string slotId)
    {
        var tied = new HashSet<string> { slotId };
        foreach (GearSet side in new[] { worn, loadout })
        {
            if (side[slotId] is not { } piece) continue;
            string owner = piece.FilledFrom ?? slotId;
            tied.Add(owner);
            foreach (var (slot, other) in side.Slots)
                if (other.FilledFrom == owner) tied.Add(slot);
        }
        foreach (string slot in tied)
            if (worn[slot] is { } there) Remove(worn, there.FilledFrom ?? slot);
        foreach (EquipmentSlot slot in slots)
            if (tied.Contains(slot.Id) && loadout[slot.Id] is { FilledFrom: null } own) Put(worn, slot, own.Item, own.Quantity);
    }

    // ---- the changes, and the order they go on in -------------------------------------------------------------

    /// <summary>
    /// The changes that turn the loadout into what is worn, in their one shape: the item, for a slot whose own item is
    /// another; "", for a slot whose own item has to go (it is to be empty, or another slot's item is to fill it). A slot
    /// that another slot's item fills, in the loadout or now, needs nothing of its own: it follows that slot. These are
    /// the slots with the gold frame, and "+ N changes" counts them.
    /// </summary>
    public static Dictionary<string, string> Overrides(GearSet loadout, GearSet worn, IEnumerable<EquipmentSlot> slots)
    {
        var changes = new Dictionary<string, string>();
        foreach (EquipmentSlot slot in slots)
        {
            GearPiece? was = loadout[slot.Id], now = worn[slot.Id];
            if (now is { FilledFrom: null } own)
            {
                if (was is not { FilledFrom: null } before || before.Item.Id != own.Item.Id) changes[slot.Id] = own.Item.Id;
            }
            else if (was is { FilledFrom: null }) changes[slot.Id] = "";
        }
        return changes;
    }

    /// <summary>
    /// The order in which the changes go on, on the left of the window and on the copy alike: first the slots that are
    /// to be empty, then the items, in the game's order of the slots, those for a slot that another slot's item fills
    /// (the Offhand of a two-handed weapon) after the rest. <paramref name="filled"/> is asked before anything changes.
    /// </summary>
    public static List<(EquipmentSlot Slot, string ItemId)> Steps(IEnumerable<EquipmentSlot> slots,
        IReadOnlyDictionary<string, string> overrides, Func<EquipmentSlot, bool> filled)
    {
        var changed = slots.Where(s => overrides.ContainsKey(s.Id)).Select(s => (Slot: s, ItemId: overrides[s.Id], Filled: filled(s))).ToList();
        return changed.Where(c => c.ItemId == "")
            .Concat(changed.Where(c => c.ItemId != "" && !c.Filled))
            .Concat(changed.Where(c => c.ItemId != "" && c.Filled))
            .Select(c => (c.Slot, c.ItemId)).ToList();
    }

    /// <summary>
    /// The loadout with the changes on, taken in the order of <see cref="Steps"/>, as the copy takes them. A change whose
    /// item is no longer in the game is left out. The items come from the item picker, which lists only those that fit
    /// the slot (CombatSimMod.ChoicesFor).
    /// </summary>
    public static GearSet Resolve(GearSet loadout, IReadOnlyDictionary<string, string> overrides, IEnumerable<EquipmentSlot> slots,
        Func<string, Item?> itemOf, Action<string>? log)
    {
        GearSet worn = loadout.Copy();
        foreach (var (slot, itemId) in Steps(slots, overrides, s => worn[s.Id] is { FilledFrom: not null }))
        {
            if (itemId == "") TakeOff(worn, slot.Id);
            else if (itemOf(itemId) is { } item) Put(worn, slot, item, 0, log);
        }
        return worn;
    }

#if DEVTOOLS
    /// <summary>Development: "Offhand: Adamant 2H Sword (from MelvorBase:Weapon), Ammo: -, ..." in the slots' order.</summary>
    public static string Describe(GearSet worn, IEnumerable<EquipmentSlot> slots) => string.Join(", ", slots.Select(slot =>
        $"{slot.DisplayName.Text}: " + (worn[slot.Id] is { } piece
            ? piece.Item.DisplayName.GetPluralText(1) + (piece.FilledFrom != null ? $" (from {piece.FilledFrom})" : "")
            : "-")));

    /// <summary>Development: the item's own OccupiesSlots, as the game lists them (also its own slot, if listed).</summary>
    public static string DevOccupies(Item item)
    {
        try { return "[" + string.Join(", ", Occupies(item)) + "]"; }
        catch (Exception e) { return "not readable: " + e.Message; }
    }
#endif
}
