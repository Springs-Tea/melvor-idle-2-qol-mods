using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Components.Equipment;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Components.Loadouts;
using GamesByMalcs.Melvor2.Components.MagicSpells;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Utils;
#if DEVTOOLS
using System.IO;
using Godot;
using MelvorMods.Shared;
#endif

namespace CombatSim;

/// <summary>
/// "Wear this setup first" (issue #3): with the box above the Fight button ticked, Fight puts on what the selected
/// result was simulated with before the fight starts. This is the one place where a mod of this repository changes
/// the character, and it stays as small as the maintainer allowed it: only on the player's click; only through the
/// game's own player actions (<c>Loadouts.SwapToLoadout</c>, what the loadout buttons do; <c>ItemEquipping.EquipItem</c>,
/// which takes the piece from the bank and gives back what it replaces; <c>MagicSpells.SelectSpell</c>); only pieces
/// that are in the bank, and of ammunition no more than the bank holds. A slot whose piece is not there, whose piece
/// the game would not put there, or whose old piece the bank has no room for stays exactly as it is, and so does a slot
/// the simulation had empty: nothing is taken off unless a piece from the bank goes on for it.
///
/// The words above the button are a promise made from one plan (<see cref="PlanWear"/>), and the click keeps it: it
/// does nothing when the plan made at the click differs from the one on show, and before each step it asks the game
/// again and does less, never more, when it finds something else. Nothing is given, bought or made, nothing is put back
/// after the fight, and nothing of <c>SimEngine.Wear</c> or <c>TopUp</c> is used: they hand the copy items it does not
/// own, which here would create items. Food is not chosen here: a loadout brings what the game keeps with it.
/// </summary>
public partial class CombatSimMod
{
    /// <summary>Why the box is off for this session (a game member it needs is gone); "" while it works.</summary>
    string _wearOff = "";
    /// <summary>The character whose game actions <see cref="ProbeWear"/> last looked for.</summary>
    CombatEntity? _wearProbed;
    /// <summary>The plan whose words are on show above the Fight button: what a click may do, and no more.</summary>
    WearPlan? _wearShown;
    /// <summary>What the click under way has done so far, for its report also when it stops on an error.</summary>
    WearRun _wearRun = new();

    enum WearKind { Already, On, Skip, Keep }

    /// <summary>A piece in a slot, with its count; Many for what is worn in numbers (arrows).</summary>
    readonly record struct Held(Item Item, int Quantity, bool Many)
    {
        public string Words => Many ? $"{Quantity:N0} {Item.DisplayName.GetPluralText(Quantity)}" : Item.DisplayName.GetPluralText(1);
    }

    /// <summary>One slot of the simulated setup that was changed from the loadout's own piece.</summary>
    sealed class WearStep(EquipmentSlot slot, string itemId)
    {
        public readonly EquipmentSlot Slot = slot;
        /// <summary>The simulated piece; "" when the slot was simulated empty.</summary>
        public readonly string ItemId = itemId;
        public WearKind Kind;
        public Item? Item;
        public bool Many;
        /// <summary>On: how many go on (ammunition: what the bank holds).</summary>
        public int Quantity;
        /// <summary>What is in the slot before this step, as the plan sees it; Keep: the piece that stays.</summary>
        public Held? There;
        /// <summary>Skip: why, in words.</summary>
        public string Why = "";
        /// <summary>On: what goes back to the bank for it, from this slot and from the slots the game clears for it (a shield).</summary>
        public readonly List<Held> Off = new();

        public string Name => Item == null ? "an item" : Kind == WearKind.On ? new Held(Item, Quantity, Many).Words
            : Item.DisplayName.GetPluralText(Many ? 2 : 1);

        public WearStep Skip(string why)
        {
            Kind = WearKind.Skip;
            Why = why;
            return this;
        }
    }

    /// <summary>What Fight would do first: the loadout to change to, the changed slots, the spell, and the words for it.</summary>
    sealed class WearPlan
    {
        /// <summary>The fight and the setup the plan was made for (<see cref="SimView.ReportKey"/>).</summary>
        public string Key = "";
        /// <summary>The character it was made for.</summary>
        public CombatEntity? Combat;
        public LoadoutSlot? Loadout;
        public string LoadoutName = "";
        /// <summary>With a change of loadout: what that loadout keeps in each slot, as the game stored it.</summary>
        public Dictionary<string, Held>? Stored;
        /// <summary>The pieces that go on first, in the order the click puts them on; then the slots that stay.</summary>
        public readonly List<WearStep> Steps = new();
        public MagicSpell? Spell;
        public string SpellName = "";
        /// <summary>The spell Fight's own code selects for the setup that is worn (CombatSimMod.Fight); "" for none.</summary>
        public string FightSpell = "";
        /// <summary>Why nothing can be put on; "" when something can.</summary>
        public string Problem = "";
        /// <summary>Everything Fight will do, in words. <see cref="Lead"/>: when nothing goes on, why (its first sentence).</summary>
        public string Words = "", Lead = "";
        public bool Acts => Loadout != null || Spell != null || Steps.Any(s => s.Kind == WearKind.On);
    }

    /// <summary>What a click really did, read from the game after each action.</summary>
    sealed class WearRun
    {
        /// <summary>A game action was called: from here on, an error may have left the equipment half changed.</summary>
        public bool Acted;
        public string Wore = "", Selected = "";
        public readonly List<Held> On = new(), Off = new();
        /// <summary>The slots that stayed as the words said they would, in sentences without their full stop.</summary>
        public readonly List<string> Stayed = new();
        /// <summary>What did not happen as the words said, and why, in sentences without their full stop.</summary>
        public readonly List<string> Notes = new();
        /// <summary>Every piece the click may move, and their counts before it did anything.</summary>
        public List<Item> Pieces = new();
        public (Dictionary<string, int> Worn, Dictionary<string, int>? Kept)? Before;
    }

    /// <summary>The click's answer to Fight: what to tell the player, whether to start the fight, whether to keep the window open.</summary>
    sealed class WearOutcome
    {
        public string Report = "";
        /// <summary>False: something went wrong while putting the setup on, so the player reads what happened first.</summary>
        public bool Fight = true;
        /// <summary>A slot stayed as it was, or something did not go as the words said: the window stays open with the report.</summary>
        public bool Read;
    }

    // The inventory the game's equip action takes pieces from and gives them back to (BaseEquippingComponent.Inventory).
    // ProbeWear makes sure it is the bank's, so "in your bank" is true of it.
    IInventory Stock => _combat!.ItemEquipping.Inventory;

    // ---- before the click: the words above the Fight button -----------------------------------------------

    WearForecast ISimLookup.ForecastWear(SimTarget target)
    {
        _wearShown = null;
        if (_wearOff != "") return OffForecast();
        try { return Forecast(target); }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            SwitchWearOff(e);
            return OffForecast();
        }
        catch (Exception e)
        {
            if (_reported.Add("wear " + e.GetType().Name + e.Message)) _ctx.Log("Wear this setup first could not work out what to put on: " + e);
            return new WearForecast("Fight could not work out what to put on, so it puts nothing on.",
                "Fight could not work out what to put on. MelvorModLoader.log says why.");
        }
    }

    WearForecast OffForecast() => new("Wear this setup first does not work with this game version, so Fight puts nothing on.", _wearOff);

    // Its own method, never inlined: a game member that is gone fails when this is called, inside the try above.
    [MethodImpl(MethodImplOptions.NoInlining)]
    WearForecast Forecast(SimTarget target)
    {
        if (_combat != null && _bank != null && !ReferenceEquals(_wearProbed, _combat))
        {
            _wearProbed = _combat;
            ProbeWear(_combat, _bank);
            if (_wearOff != "") return OffForecast();
        }
        WearPlan? plan = PlanWear(target);
        if (plan == null) return new WearForecast("", "Open the simulator again once your character is loaded.");
        if (plan.Problem != "") return new WearForecast(plan.Problem + ", so Fight puts nothing on.", plan.Problem + ".");
#if DEVTOOLS
        if (plan.Words != _devWearWords)
        {
            _devWearWords = plan.Words;
            try { _ctx.Log("Dev: with Wear this setup first ticked, Fight would: " + plan.Words + " | " + string.Join(" | ", plan.Steps.Select(DevStep))); }
            catch (Exception e) { _ctx.Log("Dev: the plan's steps could not be read: " + e.Message); }
        }
#endif
        // The words are the whole text above Fight: what goes on, or why nothing does, and the rest of what Fight does.
        if (!plan.Acts) return new WearForecast(plan.Words, plan.Lead, Whole: true);
        _wearShown = plan;
        return new WearForecast(plan.Words, "", Whole: true);
    }

    // It only names the game's actions the click uses, without calling one, so that an action a game update removed
    // fails here, when the box is first shown, and the box is greyed before anybody clicks. The room checks and the
    // counts ask the inventory the equip action uses; it must be the bank's, as the words say.
    [MethodImpl(MethodImplOptions.NoInlining)]
    void ProbeWear(CombatEntity combat, BankEntity bank)
    {
        Action<LoadoutSlot> swap = combat.Loadouts.SwapToLoadout;
        Action<Item, int, EquipmentSlot> equip = combat.ItemEquipping.EquipItem;
        Action<MagicSpell> select = combat.MagicSpells.SelectSpell;
        IInventory stock = combat.ItemEquipping.Inventory;
        Func<InventoryTransaction, TransactionResult> room = stock.IsTransactionValid;
        GC.KeepAlive(swap);
        GC.KeepAlive(equip);
        GC.KeepAlive(select);
        GC.KeepAlive(room);
        bool banks = ReferenceEquals(stock, bank.Inventory);
#if DEVTOOLS
        _ctx.Log($"Dev: the game's equip action takes pieces from a {stock.GetType().Name}, which is {(banks ? "the bank's own inventory" : $"NOT the bank's inventory (a {bank.Inventory.GetType().Name})")}.");
#endif
        if (!banks)
            WearOff("This game version takes equipment from somewhere else than your bank: for now Fight starts with what you are wearing.",
                ": the game's equip action does not take pieces from the bank's inventory.");
    }

    // The simulated setup (the loadout, Overrides and SpellId the results were made with) against what is worn and
    // what the bank holds right now. The same plan makes the words before the click and is made again at the click.
    // Null while no character is loaded.
    [MethodImpl(MethodImplOptions.NoInlining)]
    WearPlan? PlanWear(SimTarget target)
    {
        if (_game == null || _combat == null || _bank == null) return null;
        var plan = new WearPlan { Key = SimView.ReportKey(_state, target), Combat = _combat };
        Dictionary<string, Held> now = WornNow(), worn = new(now);
        // Everything the steps send back to the bank, for the game's room check: nothing a step takes out of the bank
        // is counted as room, since the game may keep its place (a placeholder).
        var back = new List<Held>();
        string? current = _combat.Loadouts.CurrentLoadout?.Data.Id;
        if (_state.LoadoutId != "" && _state.LoadoutId != current)
        {
            // The key the game's own loadout buttons swap to, as the copy's SimEngine.Wear finds it.
            plan.Loadout = _combat.Loadouts.ActiveLoadouts.Keys.FirstOrDefault(l => l.Id == _state.LoadoutId);
            ActiveLoadoutSlot? shown = _combat.Loadouts.LoadoutDisplayOrder.FirstOrDefault(l => l.Data.Id == _state.LoadoutId);
            if (plan.Loadout == null || shown is not { IsUnlocked: true })
            {
                plan.Problem = "That loadout is no longer there";
                return plan;
            }
            // As BuildGear names it.
            plan.LoadoutName = string.IsNullOrWhiteSpace(shown.Name) ? shown.Data.DisplayName.Text : shown.Name;
            plan.Stored = WornIn(plan.Loadout);
            worn = new Dictionary<string, Held>(plan.Stored);
            // Whether the game keeps a loadout's pieces in the bank cannot be told from its names. If it does, the change
            // sends the pieces worn now that the loadout does not have back to the bank: so their room is asked for too.
            back.AddRange(now.Where(w => !worn.TryGetValue(w.Key, out Held stored) || stored.Item.Id != w.Value.Item.Id).Select(w => w.Value).DistinctBy(h => h.Item.Id));
            if (back.Count > 0 && Room(back) != TransactionResult.Success)
            {
                plan.Problem = $"Your bank has no room for what {plan.LoadoutName} takes off ({List(back.Select(h => h.Words))})";
                return plan;
            }
        }

        // The changed slots, in the order of the game's slots as the left of the window shows them.
        var slots = _game.Data.GetDataRegistryOutput<EquipmentSlot>();
        var changed = new List<(EquipmentSlot Slot, string ItemId)>();
        foreach (GearSlot gear in _state.Gear)
            if (_state.Overrides.TryGetValue(gear.SlotId, out string? itemId) && slots.TryGetObjectById(gear.SlotId, out EquipmentSlot? slot)) changed.Add((slot, itemId));

        // The pieces that go on, in the order the click puts them on. A piece that waits for another step (it is worn in
        // a slot that a later step clears, or only comes back to the bank with a later step: rings changing places) is
        // decided again after every step that goes on, so the words never keep or skip a piece another step takes off.
        var bank = new Dictionary<string, int>();
        var placed = new HashSet<string>();
        var done = new HashSet<string>();
        bool another = plan.Loadout != null;
        for (bool more = true; more;)
        {
            more = false;
            foreach (var (slot, itemId) in changed)
            {
                if (itemId == "" || done.Contains(slot.Id)) continue;
                WearStep step = Decide(slot, itemId, worn, bank, back, placed, another);
                if (step.Kind != WearKind.On) continue;
                TakeStep(step, worn, bank, back);
                placed.Add(step.Item!.Id);
                plan.Steps.Add(step);
                done.Add(slot.Id);
                more = true;
            }
        }
        // The other slots, against the slots and the bank as those steps leave them: what stays, and why.
        foreach (var (slot, itemId) in changed)
            if (!done.Contains(slot.Id)) plan.Steps.Add(itemId == "" ? Stays(slot, worn) : Decide(slot, itemId, worn, bank, back, placed, another));

        if (_state.SpellId != "")
        {
            MagicSpell? spell = _combat.MagicSpells.ActiveSpells.Keys.FirstOrDefault(s => s.Id == _state.SpellId);
            string name = _state.Spells.FirstOrDefault(s => s.Id == _state.SpellId)?.Name ?? spell?.DisplayName.Text ?? "";
            // For the setup that is worn, Fight's own code selects it after the box; the words name it all the same.
            if (_state.IsWornSetup) plan.FightSpell = spell != null ? name : "";
            // A loadout keeps a spell of its own, so after a change of loadout the spell is selected whatever it was.
            else if (spell != null && (plan.Loadout != null || _combat.MagicSpells.SelectedSpell?.Data.Id != _state.SpellId))
            {
                plan.Spell = spell;
                plan.SpellName = name;
            }
        }
        Say(plan, target);
        return plan;
    }

    // One changed slot, by the game's rules, against what is in the slots (now, or once the loadout is changed), what
    // the bank holds with the changes the steps before make (bank), the room the bank must keep for what those steps
    // send back (back), and the pieces they put on (placed), which no later step takes off again.
    WearStep Decide(EquipmentSlot slot, string itemId, Dictionary<string, Held> worn, Dictionary<string, int> bank, List<Held> back,
        HashSet<string> placed, bool another)
    {
        var step = new WearStep(slot, itemId);
        if (worn.TryGetValue(slot.Id, out Held there)) step.There = there;
        if (!_game!.Data.GetDataRegistryOutput<Item>().TryGetObjectById(itemId, out Item? item) || !item.Capabilities.TryGet(out EquipmentCapability? wearable))
            return step.Skip("no longer in the game");
        step.Item = item;
        step.Many = wearable.CanEquipQuantity;
        if (step.There?.Item.Id == item.Id)
        {
            // Ammunition that is in its slot already stays as it is: the pile in the bank is not added to it.
            step.Kind = WearKind.Already;
            return step;
        }
        // The game keeps one slot per piece: a piece worn elsewhere is never moved by this box.
        if (worn.Any(w => w.Key != slot.Id && w.Value.Item.Id == item.Id)) return step.Skip("worn in another slot");
        // The bank's count as the item picker reads it (ChoicesFor), in the inventory the equip action takes it from.
        int inBank = Stock.GetQuantity(item) + bank.GetValueOrDefault(item.Id);
        if (inBank <= 0) return step.Skip("not in your bank");
        step.Quantity = step.Many ? inBank : 1;
        if (wearable.Requirements is { AreMet: false } needs) return step.Skip(Unmet(needs));
        IEnumerable<string> cleared;
        if (another)
        {
            // Before a change of loadout the game can only say where an item goes among what is worn now. So what comes
            // off is only what can be known: what that loadout keeps in this slot, and in the slots the item fills too
            // (its own data: a two-handed weapon fills the shield's). If the game wants more at the click, it stays off.
            if (wearable.Type?.Id != slot.FitsType?.Id) return step.Skip("it does not go in that slot");
            cleared = (wearable.OccupiesSlots ?? Enumerable.Empty<EquipmentSlot>()).Select(s => s.Id).Prepend(slot.Id);
        }
        else
        {
            // The game's own list of where the item can go, and what it clears there.
            AvailableEquipmentSlot? place = _combat!.Equipment.GetAvailableSlotsForItem(item).FirstOrDefault(a => a.Slot?.Slot?.Id == slot.Id);
            if (place == null) return step.Skip("the game does not put it there now");
            cleared = (place.Replaces ?? new List<IActiveEquipmentSlot>()).Concat(_combat.Equipment.GetSlotsBlockingEquip(item, slot) ?? new List<IActiveEquipmentSlot>())
                .Select(s => s.Slot?.Id).OfType<string>().Prepend(slot.Id);
        }
        foreach (string id in cleared)
            if (worn.TryGetValue(id, out Held off) && step.Off.All(o => o.Item.Id != off.Item.Id)) step.Off.Add(off);
        // A piece put on for this setup by a step before stays on: this one is skipped instead.
        var undone = step.Off.Where(o => placed.Contains(o.Item.Id)).ToList();
        if (undone.Count > 0)
        {
            step.Off.Clear();
            return step.Skip("it would take off " + List(undone.Select(u => u.Words)));
        }
        // What comes off goes back to the bank: the game says whether there is room, before anything is done.
        if (step.Off.Count > 0 && Room(back.Concat(step.Off)) != TransactionResult.Success)
            return step.Skip("your bank has no room for " + List(step.Off.Select(o => o.Words)));
        step.Kind = WearKind.On;
        return step;
    }

    // What the steps after this one find: the slots as they will be, the bank without what went on and with what came
    // back.
    static void TakeStep(WearStep step, Dictionary<string, Held> worn, Dictionary<string, int> bank, List<Held> back)
    {
        Item item = step.Item!;
        bank[item.Id] = bank.GetValueOrDefault(item.Id) - step.Quantity;
        foreach (Held off in step.Off)
        {
            bank[off.Item.Id] = bank.GetValueOrDefault(off.Item.Id) + off.Quantity;
            back.Add(off);
            foreach (string id in worn.Where(w => w.Value.Item.Id == off.Item.Id).Select(w => w.Key).ToList()) worn.Remove(id);
        }
        var held = new Held(item, step.Quantity, step.Many);
        worn[step.Slot.Id] = held;
        if (item.Capabilities.TryGet(out EquipmentCapability? wearable))
            foreach (EquipmentSlot other in wearable.OccupiesSlots ?? Enumerable.Empty<EquipmentSlot>()) worn[other.Id] = held;
    }

    // A slot simulated empty. Only pieces from the bank go on, so what is worn there stays on. The second hand slot of a
    // two-handed weapon holds the weapon of the other slot: it is that slot's piece, never one of its own here. The game
    // says so by the slots the weapon fills (OccupiesSlots), or by the same piece sitting in a slot of its own kind too.
    static WearStep Stays(EquipmentSlot slot, Dictionary<string, Held> worn)
    {
        var step = new WearStep(slot, "") { Kind = WearKind.Already };
        if (!worn.TryGetValue(slot.Id, out Held there)) return step;
        if (there.Item.Capabilities.TryGet(out EquipmentCapability? wearable) && (wearable.OccupiesSlots?.Any(s => s.Id == slot.Id) == true ||
            (wearable.Type?.Id != slot.FitsType?.Id && worn.Any(w => w.Key != slot.Id && w.Value.Item.Id == there.Item.Id))))
            return step;
        step.There = there;
        step.Kind = WearKind.Keep;
        return step;
    }

    // The words above Fight, from the whole plan. With something to put on, one sentence of every step: "Wears Loadout 2,
    // puts on Adamant Sword and 1,250 Bronze Arrows in place of Iron Sword, skips Gold Ring (not in your bank), switches to
    // the Stab stance, if your weapon has it, then fights Scarecrow." With nothing, why first, then what Fight still does,
    // in the words SimView.FillDetails uses for it.
    void Say(WearPlan plan, SimTarget target)
    {
        var on = plan.Steps.Where(s => s.Kind == WearKind.On).ToList();
        var skip = plan.Steps.Where(s => s.Kind == WearKind.Skip).ToList();
        var keep = plan.Steps.Where(s => s.Kind == WearKind.Keep).Select(s => s.There!.Value).DistinctBy(h => h.Item.Id).ToList();
        string? putOn = on.Count > 0 ? PutOn(on.Select(s => new Held(s.Item!, s.Quantity, s.Many)).ToList(), on.SelectMany(s => s.Off).ToList(), "puts on ") : null;
        string? skips = skip.Count > 0 ? "skips " + List(skip.Select(s => $"{s.Name} ({s.Why})")) : null;
        string? keeps = keep.Count > 0 ? $"keeps {List(keep.Select(h => h.Words))} on (simulated without {(keep.Count > 1 || keep[0] is { Many: true, Quantity: not 1 } ? "them" : "it")})" : null;
        string? select = plan.Spell != null ? "selects " + plan.SpellName : plan.FightSpell != "" ? "selects " + plan.FightSpell : null;
        string? stance = target.StanceName != "" ? $"switches to the {target.StanceName} stance, if your weapon has it" : null;
        if (plan.Acts)
        {
            var parts = new[] { plan.Loadout != null ? "wears " + plan.LoadoutName : null, putOn, skips, keeps, select, stance }.OfType<string>().ToList();
            parts.Add((target.IsDungeon ? "then enters " : "then fights ") + target.Name);
            plan.Lead = "";
            plan.Words = Capital(string.Join(", ", parts)) + ".";
            return;
        }
        var stays = new[] { skips, keeps }.OfType<string>().ToList();
        plan.Lead = stays.Count > 0 ? "Puts nothing on: " + string.Join(", ", stays) + "."
            : _state.Overrides.Count == 0 ? "Nothing to put on: you are wearing this loadout."
            : "Nothing to put on: you are wearing this loadout and the items swapped in here.";
        plan.Words = string.Join(" ", new[] { stance, select }.OfType<string>().Select(t => Capital(t) + ".").Prepend(plan.Lead));
    }

    // "puts on Silver Ring and Gold Ring in place of Bronze Ring": what goes back to the bank and stays there. A piece
    // that comes off and goes on again elsewhere (a ring moved to the other ring slot) is only named once.
    static string PutOn(List<Held> on, List<Held> off, string verb)
    {
        var back = off.Where(o => on.All(p => p.Item.Id != o.Item.Id)).DistinctBy(o => o.Item.Id).ToList();
        return verb + List(on.Select(p => p.Words)) + (back.Count > 0 ? " in place of " + List(back.Select(o => o.Words)) : "");
    }

    // The game's own words for the requirements that are not met ("Level 40 Attack"), as the item tooltips show them.
    static string Unmet(ConditionGroup needs)
    {
        try
        {
            var words = needs.Conditions.Where(c => c is { IsOr: false, IsMet: false }).Select(c => StringUtils.StripBBCode(c.GetDescription(false, false)))
                .Where(w => !string.IsNullOrWhiteSpace(w)).ToList();
            if (words.Count > 0) return string.Join(", ", words);
        }
        catch (Exception) { }   // the plain words below
        return "you do not meet its requirements";
    }

    // Whether the bank takes all of these back, as the game itself checks a change to it before making it. The
    // transaction is only asked about, never carried out; and only what comes in is in it, so the room a piece that
    // goes out may free is never counted on.
    TransactionResult Room(IEnumerable<Held> adds)
    {
        IInventory stock = Stock;
        var transaction = new InventoryTransaction(stock);
        foreach (var piece in adds.GroupBy(a => a.Item)) transaction.Add(piece.Key, Math.Max(1, piece.Sum(a => a.Quantity)));
#if DEVTOOLS
        foreach (Item filler in _devFillers) transaction.Add(filler, 1);
#endif
        return stock.IsTransactionValid(transaction);
    }

    Dictionary<string, Held> WornNow()
    {
        var worn = new Dictionary<string, Held>();
        foreach (EquipmentSlot slot in _game!.Data.GetDataRegistryOutput<EquipmentSlot>().GetAllObjects())
            if (_combat!.Equipment.TryGetActiveSlot(slot, out IActiveEquipmentSlot? active) && active?.Equipped is { Item: { } item } equipped)
                worn[slot.Id] = new Held(item, active.Quantity, equipped.Equipment?.CanEquipQuantity == true);
        return worn;
    }

    // What a saved loadout puts on, as the game stored it when it was last worn (read as BuildGear reads it): a two-handed
    // weapon in both hand slots, as the slots of the worn loadout show it. Empty when it cannot be read; BuildGear has
    // said so in the log then, and offers no other loadout.
    Dictionary<string, Held> WornIn(LoadoutSlot loadout)
    {
        var worn = new Dictionary<string, Held>();
        try
        {
            if (SavedLoadouts(_combat!.Equipment).TryGetValue(loadout, out EquipmentLoadout? saved))
                foreach (var (slot, stored) in saved.Slots)
                    if (stored.Equipped is { Item: { } item } equipped) worn[slot.Id] = new Held(item, stored.Quantity, equipped.Equipment?.CanEquipQuantity == true);
        }
        catch (MissingMethodException) { }
        return worn;
    }

    // ---- the click -------------------------------------------------------------------------------------------

    /// <summary>
    /// Fight's first step, once it knows the fight can be started: with the box ticked, puts the simulated setup on as
    /// the words above the button said, and says what really happened. Null when the box has nothing to do (unticked,
    /// an earlier scan on show, or the words on show said it puts nothing on): Fight then goes on exactly as without it.
    /// </summary>
    WearOutcome? WearForFight(SimTarget target, BaseCombatArea area)
    {
        if (!_state.WearSetup || _state.Viewing != null || _wearOff != "" || _combat == null) return null;
        // Nothing changes for a fight that cannot be started: the checks Fight makes after this.
        bool exists = target.EnemyId != null ? area is CombatArea open && open.Enemies.Any(e => e.Id == target.EnemyId) : area is Dungeon;
        if (!exists || _wearShown is not { } shown || !ReferenceEquals(shown.Combat, _combat)) return null;
        _wearRun = new WearRun();
        WearOutcome outcome;
        try { outcome = Wear(shown, target); }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            SwitchWearOff(e);
            outcome = Stopped(e);
        }
        catch (Exception e)
        {
            _ctx.Log("Wear this setup first stopped: " + e);
            outcome = Stopped(e);
        }
        _ctx.Log("Wear this setup first: " + outcome.Report);
        // A stop after the equipment changed: the counts go to the log in every build, so that a player's log shows
        // whether anything went missing.
        if (!outcome.Fight && _wearRun.Acted) LogCounts(_wearRun);
#if DEVTOOLS
        // Also after a stop: the counts say whether anything was created or lost on the way.
        if (_wearRun.Acted) DevWear("after", shown);
#endif
        return outcome;
    }

    // An error on the way: whether the equipment changed is known from whether a game action was called.
    WearOutcome Stopped(Exception e) => Outcome(_wearRun, _wearRun.Acted
        ? "Putting on the setup stopped half way: look at your equipment before you fight. MelvorModLoader.log says why"
        : e is MissingMemberException or TypeLoadException ? "Nothing was changed: this game version puts on equipment differently, so Wear this setup first is off for now"
        : "Nothing was changed: Fight could not put the setup on. MelvorModLoader.log says why", fight: false);

    [MethodImpl(MethodImplOptions.NoInlining)]
    WearOutcome Wear(WearPlan shown, SimTarget target)
    {
        WearRun run = _wearRun;
        CombatEntity combat = _combat!;
        // The words the player read are the promise. If what they were made from changed since (an item sold, a slot
        // changed by hand), nothing is done and the words are written again.
        WearPlan? now = PlanWear(target);
        if (now == null || !SamePlan(shown, now))
            return Outcome(run, "Your equipment or bank changed after the words above Fight were written, so nothing was put on. " +
                               "Read them again, then press Fight", fight: false);
        _ctx.Log($"Wear this setup first, for {target.Name}: {shown.Words}");
        // Every piece the click may move, counted before anything is done.
        run.Pieces = MayMove(now.Stored, shown.Steps);
        run.Before = Sums(run.Pieces);
#if DEVTOOLS
        DevWear("before", shown);
#endif
        if (shown.Loadout != null)
        {
            run.Acted = true;
            combat.Loadouts.SwapToLoadout(shown.Loadout);
            if (combat.Loadouts.CurrentLoadout?.Data.Id != shown.Loadout.Id)
                // The items swapped in were changes to that loadout: on another one they would be another setup.
                return Outcome(run, $"The game had not changed to {shown.LoadoutName} when Fight went on, so nothing else was put on: look at your equipment", fight: false);
            // The steps below are decided on the slots: they must hold what the loadout keeps, as the words were made from.
            string missing = NotAsStored(now.Stored ?? new Dictionary<string, Held>(), WornNow());
            if (missing != "")
            {
                _ctx.Log($"Wear this setup first: after the change to {shown.LoadoutName} the slots do not hold what it keeps: {missing}.");
                return Outcome(run, $"The game had not finished changing to {shown.LoadoutName} when Fight went on ({missing}), so nothing else was put on: " +
                                    "look at your equipment", fight: false);
            }
            string lost = Lost(run.Pieces, run.Before.Value, Sums(run.Pieces));
            if (lost != "") return Outcome(run, $"After the change to {shown.LoadoutName} the game counts {lost}: look at your bank and equipment. MelvorModLoader.log has the counts", fight: false);
            run.Wore = shown.LoadoutName;
#if DEVTOOLS
            DevWear("after the loadout change", shown);
#endif
        }

        // Every item named by a step, and every slot a step changed: the rest of the setup is compared at the end.
        var named = new HashSet<string>();
        foreach (WearStep promised in shown.Steps)
        {
            foreach (Item piece in new[] { promised.Item, promised.There?.Item }.Concat(promised.Off.Select(o => (Item?)o.Item)).OfType<Item>()) named.Add(piece.Id);
            // The slots the words said would stay as they are: the report says so too.
            if (promised.Kind == WearKind.Skip) run.Stayed.Add($"{promised.Name} did not go on: {promised.Why}");
            if (promised.Kind == WearKind.Keep && promised.There is { } kept)
                run.Stayed.Add($"{kept.Words} stayed on: simulated without {(kept is { Many: true, Quantity: not 1 } ? "them" : "it")}");
            if (promised.Kind != WearKind.On) continue;
            // Asked again of the game, on what is really there now.
            Dictionary<string, Held> worn = WornNow();
            WearStep live = Decide(promised.Slot, promised.ItemId, worn, new Dictionary<string, int>(), new List<Held>(), new HashSet<string>(), another: false);
            if (live.Kind == WearKind.Already) continue;   // on already: the loadout brought it
            string why = Differs(promised, live);
            if (why != "")
            {
                // Less than the words said, never more: this piece stays off.
                run.Notes.Add($"{promised.Item!.DisplayName.GetPluralText(promised.Many ? 2 : 1)} did not go on: {why}");
                continue;
            }
            Item item = live.Item!;
            var pieces = worn.Values.Select(h => h.Item).Append(item).DistinctBy(i => i.Id).ToList();
            var before = Sums(pieces);
            run.Acted = true;
            combat.ItemEquipping.EquipItem(item, Math.Min(promised.Quantity, live.Quantity), promised.Slot);
            Dictionary<string, Held> after = WornNow();
            string lost = Lost(pieces, before, Sums(pieces));
            if (lost != "") return Outcome(run, $"After {item.DisplayName.GetPluralText(1)} the game counts {lost}: look at your bank and equipment. MelvorModLoader.log has the counts", fight: false);
            if (!after.TryGetValue(promised.Slot.Id, out Held on) || on.Item.Id != item.Id)
                return Outcome(run, $"The game did not put {promised.Name} on, so nothing else was put on", fight: false);
            run.On.Add(on);
            // What came off, read from the slots: into the bank, as the counts above say.
            var unsaid = new List<Held>();
            foreach (Held was in worn.Values.DistinctBy(h => h.Item.Id))
            {
                if (was.Item.Id == item.Id || combat.Equipment.IsItemEquipped(was.Item)) continue;
                run.Off.Add(was);
                if (promised.Off.All(o => o.Item.Id != was.Item.Id)) unsaid.Add(was);
            }
            if (unsaid.Count > 0)
                return Outcome(run, $"The game also took off {List(unsaid.Select(u => u.Words))} for it, which the words above Fight did not say, " +
                                    "so nothing else was put on. It is in your bank", fight: false);
        }

        if (shown.Spell != null && combat.MagicSpells.SelectedSpell?.Data.Id != shown.Spell.Id)
        {
            // Selected only when it is not: the game has a private DeselectSpell, so selecting may be a switch.
            run.Acted = true;
            combat.MagicSpells.SelectSpell(shown.Spell);
            if (combat.MagicSpells.SelectedSpell?.Data.Id != shown.Spell.Id)
                return Outcome(run, $"The game did not select {shown.SpellName}", fight: false);
            run.Selected = shown.SpellName;
        }

        // The rest of the setup simulated here (the slots on the left of the window) against what is on now.
        var onNow = WornNow().Values.Select(h => h.Item).DistinctBy(i => i.Id).ToList();
        var simulated = _state.Gear.Where(g => g.ItemId != null).ToList();
        foreach (GearSlot gear in simulated.DistinctBy(g => g.ItemId))
            if (!named.Contains(gear.ItemId!) && onNow.All(i => i.Id != gear.ItemId)) run.Notes.Add($"{gear.ItemName} is not on");
        foreach (Item piece in onNow)
            if (!named.Contains(piece.Id) && simulated.All(g => g.ItemId != piece.Id))
                run.Notes.Add($"{piece.DisplayName.GetPluralText(1)} is on, the simulation was without it");
        return Outcome(run, "", fight: true);
    }

    // The same steps with the same pieces; counts may differ (arrows used in a fight on, loot coming in): the click
    // puts on no more than the words said.
    static bool SamePlan(WearPlan shown, WearPlan now) =>
        shown.Key == now.Key && ReferenceEquals(shown.Combat, now.Combat) && now.Problem == "" && now.Acts &&
        shown.Loadout?.Id == now.Loadout?.Id && shown.Spell?.Id == now.Spell?.Id &&
        (shown.Stored ?? new()).Select(s => s.Key + "=" + s.Value.Item.Id).ToHashSet().SetEquals((now.Stored ?? new()).Select(s => s.Key + "=" + s.Value.Item.Id)) &&
        shown.Steps.Count == now.Steps.Count && shown.Steps.Zip(now.Steps).All(p =>
            p.First.Slot.Id == p.Second.Slot.Id && p.First.Kind == p.Second.Kind && p.First.Item?.Id == p.Second.Item?.Id &&
            p.First.There?.Item.Id == p.Second.There?.Item.Id &&
            p.First.Off.Select(o => o.Item.Id).ToHashSet().SetEquals(p.Second.Off.Select(o => o.Item.Id)));

    // Why the step the game would take now is not the one the words promised; "" when it is (or does less).
    static string Differs(WearStep promised, WearStep live)
    {
        if (live.Kind != WearKind.On) return live.Why != "" ? live.Why : "it is no longer as the words above Fight said";
        var more = live.Off.Where(o => promised.Off.All(p => p.Item.Id != o.Item.Id)).ToList();
        return more.Count == 0 ? "" : $"it would also take off {List(more.Select(m => m.Words))}, which the words above Fight did not say";
    }

    // "" when every slot holds what the loadout keeps there; else what differs, in words ("Adamant 2H Sword is not on").
    static string NotAsStored(Dictionary<string, Held> stored, Dictionary<string, Held> worn)
    {
        var missing = stored.Values.Where(s => worn.Values.All(w => w.Item.Id != s.Item.Id)).DistinctBy(s => s.Item.Id).Select(s => s.Item.DisplayName.GetPluralText(1) + " is not on");
        var extra = worn.Values.Where(w => stored.Values.All(s => s.Item.Id != w.Item.Id)).DistinctBy(w => w.Item.Id).Select(w => w.Item.DisplayName.GetPluralText(1) + " is still on");
        var moved = stored.Where(s => worn.TryGetValue(s.Key, out Held w) && w.Item.Id != s.Value.Item.Id && worn.Values.Any(o => o.Item.Id == s.Value.Item.Id))
            .Select(s => s.Value.Item.DisplayName.GetPluralText(1) + " is in another slot");
        return List(missing.Concat(extra).Concat(moved));
    }

    // Every piece a click may move: what is worn, what the loadout it changes to keeps, what its steps put on and take off.
    List<Item> MayMove(Dictionary<string, Held>? stored, List<WearStep> steps) =>
        WornNow().Values.Select(h => h.Item).Concat(stored?.Values.Select(h => h.Item) ?? Enumerable.Empty<Item>())
            .Concat(steps.Select(s => s.Item).OfType<Item>()).Concat(steps.SelectMany(s => s.Off).Select(o => o.Item))
            .DistinctBy(i => i.Id).ToList();

    // Of each piece: what the bank holds plus what is worn, and that plus what the other saved loadouts keep (as the
    // game stored them) and what the game set aside for any loadout (RejectedItems). Putting a piece on moves it between
    // the bank and the slots, so the first stays the same; a change of loadout must leave one of the two the same,
    // depending on whether the game keeps a loadout's pieces in the bank or with the loadout (it keeps them with the
    // loadout: the first sum changes then). Every piece counts once wherever it is: the game's own count of what is worn
    // (GetEquippedQuantity) counts a two-handed weapon once, and so does Owners in a saved loadout, which keeps the weapon
    // in both hand slots. Kept is null when the saved loadouts cannot be read.
    (Dictionary<string, int> Worn, Dictionary<string, int>? Kept) Sums(List<Item> pieces)
    {
        IInventory stock = Stock;
        var worn = pieces.ToDictionary(i => i.Id, i => stock.GetQuantity(i) + _combat!.Equipment.GetEquippedQuantity(i));
        try { return (worn, Kept(worn)); }
        catch (MissingMemberException) { return (worn, null); }
    }

    // The second sum: bank plus worn, plus each saved loadout but the one worn now, each piece once, plus what the game
    // set aside for any loadout. Its own method, never inlined: a game member that is gone fails when this is called,
    // inside the try of Sums.
    [MethodImpl(MethodImplOptions.NoInlining)]
    Dictionary<string, int> Kept(Dictionary<string, int> worn)
    {
        var kept = new Dictionary<string, int>(worn);
        string? current = _combat!.Loadouts.CurrentLoadout?.Data.Id;
        foreach (var (loadout, saved) in SavedLoadouts(_combat.Equipment))
        {
            if (loadout.Id != current) Add(kept, Owners(Read(saved)));
            foreach (var (_, slot) in saved.Slots)
                if (slot.RejectedItems is { Item: { } aside } rejected && kept.ContainsKey(aside.Id)) kept[aside.Id] += rejected.Quantity;
        }
        return kept;
    }

    /// <summary>One slot of a set of equipment, for the counts: its piece, the quantity the game keeps there, and whether
    /// the game marks the slot as holding another slot's piece (IsOccupied).</summary>
    readonly record struct Slotted(EquipmentSlot Slot, Item Item, int Quantity, bool Marked);

    // The slots of a set of equipment whose piece is counted: all but the second hand of a two-handed weapon, which holds
    // the weapon of the other hand (with a quantity of -1, in a saved loadout as in the slots worn). The rule of
    // SimGear.Link (issue #46): a slot holds another slot's piece when another slot of the set holds the same item and
    // the game marks this one as occupied, or that item fills this slot too (EquipmentCapability.OccupiesSlots). One
    // slot of each item always stays, and two of the same ring are two.
    static List<Slotted> Owners(List<Slotted> set)
    {
        var owners = new List<Slotted>(set);
        foreach (Slotted slot in set)
            if (owners.Any(o => o.Slot.Id != slot.Slot.Id && o.Item.Id == slot.Item.Id && (slot.Marked || Fills(o.Item, slot.Slot)))) owners.Remove(slot);
        return owners;
    }

    static bool Fills(Item item, EquipmentSlot slot) =>
        item.Capabilities.TryGet(out EquipmentCapability? wearable) && wearable.OccupiesSlots?.Any(s => s.Id == slot.Id) == true;

    // Adds the pieces of these slots to the counts of the pieces in it, each as many as its slot holds (one, or a pile of
    // ammunition).
    static void Add(Dictionary<string, int> counts, IEnumerable<Slotted> slots)
    {
        foreach (Slotted slot in slots)
            if (counts.ContainsKey(slot.Item.Id)) counts[slot.Item.Id] += Math.Max(1, slot.Quantity);
    }

    // A saved loadout's slots that hold a piece, as the game stored them.
    static List<Slotted> Read(EquipmentLoadout saved)
    {
        var set = new List<Slotted>();
        foreach (var (slot, stored) in saved.Slots)
            if (stored.Equipped?.Item is { } item) set.Add(new Slotted(slot, item, stored.Quantity, Marked(stored)));
        return set;
    }

    // Whether the game marks a saved loadout's slot as holding another slot's piece. False when this game version has
    // no such mark: the items' OccupiesSlots still say which slots a two-handed weapon fills.
    static bool Marked(EquipmentSlotLoadout slot)
    {
        try { return Occupied(slot); }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException) { return false; }
    }

    // Its own method, never inlined: a game member that is gone fails when this is called, inside the try above.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool Occupied(EquipmentSlotLoadout slot) => slot.IsOccupied;

    // "" when no piece was created or lost; else the pieces whose count changed, in words ("Iron Sword: 1, was 2").
    string Lost(List<Item> pieces, (Dictionary<string, int> Worn, Dictionary<string, int>? Kept) before, (Dictionary<string, int> Worn, Dictionary<string, int>? Kept) after)
    {
        bool Same(Dictionary<string, int>? a, Dictionary<string, int>? b) => a != null && b != null && pieces.All(p => a[p.Id] == b[p.Id]);
        if (Same(before.Worn, after.Worn) || Same(before.Kept, after.Kept)) return "";
        var changed = pieces.Where(p => before.Worn[p.Id] != after.Worn[p.Id]).ToList();
        _ctx.Log("Wear this setup first: a count changed. " + Counts(pieces, before, after));
        return List(changed.Select(p => $"{p.DisplayName.GetPluralText(2)}: {after.Worn[p.Id]:N0}, was {before.Worn[p.Id]:N0}"));
    }

    static string Counts(List<Item> pieces, (Dictionary<string, int> Worn, Dictionary<string, int>? Kept) before, (Dictionary<string, int> Worn, Dictionary<string, int>? Kept) after) =>
        "In the bank and worn, before: " + string.Join(", ", pieces.Select(p => $"{p.Id}={before.Worn[p.Id]}")) +
        "; after: " + string.Join(", ", pieces.Select(p => $"{p.Id}={after.Worn[p.Id]}")) +
        (before.Kept != null && after.Kept != null ? "; with the other loadouts, before: " + string.Join(", ", pieces.Select(p => $"{p.Id}={before.Kept[p.Id]}")) +
            "; after: " + string.Join(", ", pieces.Select(p => $"{p.Id}={after.Kept[p.Id]}")) : "");

    void LogCounts(WearRun run)
    {
        try
        {
            if (run.Before is not { } before) return;
            _ctx.Log("Wear this setup first stopped after changing the equipment. " + Counts(run.Pieces, before, Sums(run.Pieces)));
        }
        catch (Exception e) { _ctx.Log("Wear this setup first stopped after changing the equipment; the counts could not be read: " + e.Message); }
    }

    // "Wore Loadout 2, put on Adamant Sword in place of Iron Sword. Gold Ring did not go on: not in your bank." Every
    // name and count in it was read from the game after its action, or is a slot the words said would stay.
    static WearOutcome Outcome(WearRun run, string stop, bool fight)
    {
        var parts = new List<string>();
        if (run.Wore != "") parts.Add("wore " + run.Wore);
        if (run.On.Count > 0) parts.Add(PutOn(run.On, run.Off, "put on "));
        if (run.Selected != "") parts.Add("selected " + run.Selected);
        var text = new List<string>();
        if (parts.Count > 0) text.Add(Capital(string.Join(", ", parts)) + ".");
        text.AddRange(run.Stayed.Concat(run.Notes).Select(n => Capital(n) + "."));
        if (stop != "") text.Add(Capital(stop) + ".");
        if (!fight) text.Add("The fight was not started.");
        return new WearOutcome { Report = string.Join(" ", text), Fight = fight, Read = !fight || run.Stayed.Count > 0 || run.Notes.Count > 0 };
    }

    // The window stays open with what really happened above the button, until it is closed or another fight or setup
    // is selected. Whether the fight is on is read from the game once Fight has selected it.
    void ShowWearReport(SimTarget target, WearOutcome outcome)
    {
        bool on = outcome.Fight && _combat != null && _combat.Action.IsStarted && (target.EnemyId != null
            ? _combat.Areas.SelectedEnemy?.Id == target.EnemyId : _combat.Areas.CurrentArea?.Area?.Id == target.AreaId);
        _state.FightReport = string.Join(" ", new[] { on ? (target.IsDungeon ? "Fighting in " : "Fighting ") + target.Name + "." : "", outcome.Report }.Where(t => t != ""));
        _state.FightReportKey = SimView.ReportKey(_state, target);
        _view?.Refresh();
    }

    void SwitchWearOff(Exception e) =>
        WearOff("This game version puts on equipment differently: for now Fight starts with what you are wearing.", " (the game was updated?): " + e.Message);

    // One line in the log, and the box puts nothing on for the rest of the session; Fight goes on working as unticked.
    void WearOff(string why, string line)
    {
        if (_wearOff != "") return;
        _wearOff = why;
        _wearShown = null;
        _ctx.Log("Wear this setup first is switched off" + line + " Fight starts with what is worn, as without the box.");
    }

    static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // "A", "A and B", "A, B and C".
    static string List(IEnumerable<string> names)
    {
        var all = names.ToList();
        return all.Count <= 1 ? string.Concat(all) : string.Join(", ", all.Take(all.Count - 1)) + " and " + all[^1];
    }

#if DEVTOOLS
    // ---- development ---------------------------------------------------------------------------------------

    string _devWearWords = "";
    /// <summary>fight.request "full": items the bank has no stack of, added to every room check (and never to the bank), so the bank looks full.</summary>
    readonly List<Item> _devFillers = new();
    List<Item> _devWearPieces = new();
    (Dictionary<string, int> Worn, Dictionary<string, int>? Kept) _devWearBefore;

    // Whether the game is still changing loadouts (a private member, read for the log only).
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_IsSwappingLoadout")]
    static extern bool DevSwappingLoadout(EquipmentComponent equipment);

    // Every slot with its piece and quantity, the loadout, the spell, whether a fight is on, and the two counts of
    // every piece that is worn or part of the simulated setup. Before anything is done, also the plan, step by step,
    // with what the game says about each piece; afterwards, each count against before. A log line that cannot be
    // read never stops the click: the development build must put on what a player's build puts on.
    void DevWear(string when, WearPlan? plan)
    {
        try { DevWearRead(when, plan); }
        catch (Exception e) { _ctx.Log($"Dev: wear {when}: could not be read: {e.Message}"); }
    }

    void DevWearRead(string when, WearPlan? plan)
    {
        var items = _game!.Data.GetDataRegistryOutput<Item>();
        var slots = new List<string>();
        var pieces = new Dictionary<string, Item>();
        foreach (EquipmentSlot slot in _game.Data.GetDataRegistryOutput<EquipmentSlot>().GetAllObjects())
        {
            if (!_combat!.Equipment.TryGetActiveSlot(slot, out IActiveEquipmentSlot? active) || active == null) continue;
            Item? item = active.Equipped?.Item;
            slots.Add($"{slot.Id}={(item == null ? "-" : $"{item.Id} x{active.Quantity}")}{(active.IsOccupied && item == null ? " (occupied)" : "")}");
            if (item != null) pieces[item.Id] = item;
        }
        if (when == "before")
        {
            foreach (string id in _state.Gear.Select(g => g.ItemId).Concat(_state.Overrides.Values).OfType<string>())
                if (id != "" && items.TryGetObjectById(id, out Item? piece)) pieces[piece.Id] = piece;
            if (plan?.Loadout != null) foreach (Held stored in WornIn(plan.Loadout).Values) pieces[stored.Item.Id] = stored.Item;
            _devWearPieces = pieces.Values.ToList();
        }
        else foreach (Item piece in _devWearPieces) pieces[piece.Id] = piece;
        var list = pieces.Values.ToList();
        var sums = Sums(list);
        IInventory stock = Stock;
        string swapping;
        try { swapping = DevSwappingLoadout(_combat!.Equipment).ToString(); }
        catch (Exception e) { swapping = "(cannot be read: " + e.GetType().Name + ")"; }
        _ctx.Log($"Dev: wear {when}: loadout {_combat!.Loadouts.CurrentLoadout?.Data.Id ?? "-"}, still changing loadouts {swapping}, fight on {_combat.Action.IsStarted}, " +
                 $"spell {_combat.MagicSpells.SelectedSpell?.Data.Id ?? "-"}; slots {string.Join(", ", slots)}; " +
                 $"bank {string.Join(", ", list.Select(i => $"{i.Id}={stock.GetQuantity(i)}"))}; stacks {stock.StackCount} of {stock.MaxStacks}");
        _ctx.Log($"Dev: wear {when}: bank plus worn {string.Join(", ", sums.Worn.Select(o => $"{o.Key}={o.Value}"))}; " +
                 $"with the other saved loadouts {(sums.Kept == null ? "(cannot be read)" : string.Join(", ", sums.Kept.Select(o => $"{o.Key}={o.Value}")))}");
        if (when == "before")
        {
            _devWearBefore = sums;
            _ctx.Log($"Dev: wear plan: loadout {plan?.Loadout?.Id ?? "-"} ({plan?.LoadoutName}) keeping " +
                     (plan?.Stored == null ? "-" : string.Join(", ", plan.Stored.Select(s => $"{s.Key}={s.Value.Item.Id} x{s.Value.Quantity}"))) + "; simulated " +
                     string.Join(", ", _state.Gear.Select(g => $"{g.SlotId}={g.ItemId ?? "-"}{(g.Changed ? "*" : "")}")) + $"; spell {plan?.Spell?.Id ?? "-"}, Fight's own spell '{plan?.FightSpell}'");
            foreach (WearStep step in plan?.Steps ?? new List<WearStep>()) _ctx.Log("Dev: wear step " + DevStep(step));
            return;
        }
        var (worn, kept) = _devWearBefore;
        _ctx.Log($"Dev: wear {when}, each piece against before: bank plus worn {DevChanges(_devWearPieces, worn, sums.Worn)}; " +
                 $"with the other loadouts {DevChanges(_devWearPieces, kept, sums.Kept)}");
    }

    // "all the same", or "CHANGED MelvorBase:IronSword 2 -> 1, ...".
    static string DevChanges(List<Item> pieces, Dictionary<string, int>? before, Dictionary<string, int>? now)
    {
        if (before == null || now == null) return "(cannot be read)";
        var changed = pieces.Where(p => before.GetValueOrDefault(p.Id) != now.GetValueOrDefault(p.Id))
            .Select(p => $"{p.Id} {before.GetValueOrDefault(p.Id)} -> {now.GetValueOrDefault(p.Id)}").ToList();
        return changed.Count == 0 ? "all the same" : "CHANGED " + string.Join(", ", changed);
    }

    static string DevSums(Dictionary<string, int> sums) => string.Join(", ", sums.Select(s => $"{s.Key}={s.Value}"));

    // The slots worn now that hold a piece, as Read gives a saved loadout's, with the game's IsOccupied of each.
    List<Slotted> DevReadWorn()
    {
        var set = new List<Slotted>();
        foreach (EquipmentSlot slot in _game!.Data.GetDataRegistryOutput<EquipmentSlot>().GetAllObjects())
            if (_combat!.Equipment.TryGetActiveSlot(slot, out IActiveEquipmentSlot? active) && active?.Equipped?.Item is { } item)
                set.Add(new Slotted(slot, item, active.Quantity, active.IsOccupied));
        return set;
    }

    // The slots of a set that Owners leaves out, and why: "MelvorBase:Offhand=MelvorBase:WillowBow x-1 (IsOccupied, in
    // the OccupiesSlots of the one in MelvorBase:Weapon)".
    static string DevNotCounted(List<Slotted> set)
    {
        var owners = Owners(set);
        var left = set.Where(s => !owners.Contains(s)).Select(s =>
        {
            Slotted owner = owners.First(o => o.Item.Id == s.Item.Id);
            var why = new[] { s.Marked ? "IsOccupied" : null, Fills(owner.Item, s.Slot) ? "in the OccupiesSlots of the one in " + owner.Slot.Id : null }.OfType<string>();
            return $"{s.Slot.Id}={s.Item.Id} x{s.Quantity} ({string.Join(", ", why)})";
        }).ToList();
        return left.Count == 0 ? "none" : string.Join(", ", left);
    }

    // fight.request and fight-press.request, before anything is pressed: the two sums of the count check for the change
    // of loadout the words above Fight promise, now and as they would be after it, worked out without changing anything.
    // After the change the slots hold what that loadout keeps, counted once as the game counts what is worn; the loadout
    // worn now keeps what is in the slots now; the bank stays as it is, because the game keeps a loadout's pieces with
    // it (seen in the game: at a change of loadout the bank's counts stayed as they were, the slots changed). The second
    // sum then comes out the same exactly when the slots worn now, counted by Owners, give the game's own count of them
    // (GetEquippedQuantity): the line says that too, the same for the game's stored copy of the loadout worn now, and
    // what the old count, every slot that holds a piece, would have said.
    void DevDryCount(string file)
    {
        try { DevDryCountRead(file); }
        catch (Exception e) { _ctx.Log($"Dev: {file}: the dry count could not be read: {e.Message}"); }
    }

    void DevDryCountRead(string file)
    {
        if (_wearShown is not { Loadout: { } change } plan)
        {
            _ctx.Log($"Dev: {file}: dry count: the words above Fight change no loadout, so there is no change of loadout to count.");
            return;
        }
        List<Item> pieces = MayMove(plan.Stored, plan.Steps);
        var now = Sums(pieces);
        EquipmentComponent equipment = _combat!.Equipment;
        IInventory stock = Stock;
        string? current = _combat.Loadouts.CurrentLoadout?.Data.Id;
        Dictionary<LoadoutSlot, EquipmentLoadout> saved = SavedLoadouts(equipment);
        List<Slotted> live = DevReadWorn(), target = saved.TryGetValue(change, out EquipmentLoadout? keeps) ? Read(keeps) : new();
        List<Slotted>? copy = saved.FirstOrDefault(s => s.Key.Id == current).Value is { } mine ? Read(mine) : null;
        var others = saved.Where(s => s.Key.Id != current && s.Key.Id != change.Id).Select(s => (s.Key.Id, Set: Read(s.Value))).ToList();
        Dictionary<string, int> Count(IEnumerable<Slotted> slots)
        {
            var counts = pieces.ToDictionary(p => p.Id, _ => 0);
            Add(counts, slots);
            return counts;
        }
        Dictionary<string, int> Total(params Dictionary<string, int>[] parts) => pieces.ToDictionary(p => p.Id, p => parts.Sum(d => d[p.Id]));
        var bank = pieces.ToDictionary(p => p.Id, p => stock.GetQuantity(p));
        var worn = pieces.ToDictionary(p => p.Id, p => equipment.GetEquippedQuantity(p));
        var aside = pieces.ToDictionary(p => p.Id, _ => 0);
        foreach (EquipmentLoadout loadout in saved.Values)
            foreach (EquipmentSlotLoadout slot in loadout.Slots.Values)
                if (slot.RejectedItems is { Item: { } item } rejected && aside.ContainsKey(item.Id)) aside[item.Id] += rejected.Quantity;
        var targetOnce = Count(Owners(target));
        var afterWorn = Total(bank, targetOnce);
        var afterKept = Total(bank, targetOnce, Count(Owners(live)), Count(others.SelectMany(o => Owners(o.Set))), aside);
        // The old count: every slot of a saved loadout that holds a piece, a two-handed weapon twice.
        var oldNow = Total(bank, worn, Count(target), Count(others.SelectMany(o => o.Set)), aside);
        var oldAfter = Total(bank, targetOnce, Count(live), Count(others.SelectMany(o => o.Set)), aside);
        // As Lost decides: either sum the same.
        bool passes = pieces.All(p => now.Worn[p.Id] == afterWorn[p.Id]) || (now.Kept != null && pieces.All(p => now.Kept[p.Id] == afterKept[p.Id]));
        var sets = new List<string> { "worn now " + DevNotCounted(live), $"{plan.LoadoutName} {DevNotCounted(target)}" };
        sets.AddRange(others.Select(o => $"{o.Id} {DevNotCounted(o.Set)}"));
        sets.Add($"the game's stored copy of the loadout worn now, {current ?? "-"}, {(copy == null ? "(none)" : DevNotCounted(copy))}");
        _ctx.Log($"Dev: {file}: dry count for the change to {plan.LoadoutName} ({change.Id}), worked out without changing anything. " +
                 $"Slots not counted again, holding another slot's piece: {string.Join("; ", sets)}. " +
                 $"Set aside for a loadout (RejectedItems): {(aside.Values.All(v => v == 0) ? "none" : DevSums(aside))}.");
        _ctx.Log($"Dev: {file}: dry count, bank plus worn: now {DevSums(now.Worn)}; after the change {DevSums(afterWorn)}: {DevChanges(pieces, now.Worn, afterWorn)} " +
                 "(a change of loadout moves pieces between the slots and the loadouts, so this one may change).");
        _ctx.Log($"Dev: {file}: dry count, with the other loadouts: now {(now.Kept == null ? "(cannot be read)" : DevSums(now.Kept))}; after the change {DevSums(afterKept)}: " +
                 $"{DevChanges(pieces, now.Kept, afterKept)}, so the count check after the change {(passes ? "passes and the click goes on" : "WOULD STOP THE CLICK")}. " +
                 $"What is worn now, counted once, against the game's own count of it (GetEquippedQuantity): {DevChanges(pieces, worn, Count(Owners(live)))}; " +
                 $"the game's stored copy of {current ?? "-"} counted once against the same: {(copy == null ? "(no copy)" : DevChanges(pieces, worn, Count(Owners(copy))))}. " +
                 $"The old count, every slot that holds a piece, with the other loadouts: now {DevSums(oldNow)}; after the change {DevSums(oldAfter)}: {DevChanges(pieces, oldNow, oldAfter)}.");
    }

    string DevStep(WearStep step)
    {
        string text = $"{step.Slot.Id} ('{step.Slot.DisplayName.Text}'): {step.Kind} {step.Item?.Id ?? "-"} x{step.Quantity} '{step.Name}' {step.Why}; " +
                      $"there {(step.There is { } there ? there.Item.Id + " x" + there.Quantity : "-")}; back to the bank [{string.Join(", ", step.Off.Select(o => o.Item.Id + " x" + o.Quantity))}]";
        if (step.Item == null) return text;
        EquipmentComponent equipment = _combat!.Equipment;
        string Slot(IActiveEquipmentSlot s) => $"{s.Slot?.Id}={s.Equipped?.Item?.Id ?? "-"}";
        string where = string.Join("; ", equipment.GetAvailableSlotsForItem(step.Item)
            .Select(a => $"{a.Slot?.Slot?.Id} replacing {string.Join("+", (a.Replaces ?? new List<IActiveEquipmentSlot>()).Select(Slot))}"));
        string blocking = string.Join(", ", (equipment.GetSlotsBlockingEquip(step.Item, step.Slot) ?? new List<IActiveEquipmentSlot>()).Select(Slot));
        string data = step.Item.Capabilities.TryGet(out EquipmentCapability? wearable)
            ? $"occupies {string.Join("+", wearable.OccupiesSlots?.Select(s => s.Id) ?? Enumerable.Empty<string>())}, type {wearable.Type?.Id}, slot fits {step.Slot.FitsType?.Id}, quantity {wearable.CanEquipQuantity}, " +
              $"requirements met {wearable.Requirements?.AreMet}{(wearable.Requirements is { AreMet: false } needs ? $" ('{Unmet(needs)}')" : "")}"
            : "not wearable";
        return text + $"; the game's slots for it (among what is worn now): {where}; blocking here: {blocking}; {data}; bank {Stock.GetQuantity(step.Item)}; " +
               $"room for what comes off: {(step.Off.Count == 0 ? "-" : Room(step.Off).ToString())}";
    }

    // fight.request "full": the bank as if it had no free stack left, for the room checks only. Fillers are items the
    // bank has no stack of at all: GetStackCount also counts a placeholder, which needs no new stack. The game's own
    // room check is asked about as many fillers as the bank has free stacks, and about one more; both answers are logged.
    void DevFillBank()
    {
        _devFillers.Clear();
        IInventory stock = Stock;
        int free = Math.Max(0, stock.MaxStacks - stock.StackCount);
        var spare = _game!.Data.GetDataRegistryOutput<Item>().GetAllObjects().Where(i => stock.GetStackCount(i) == 0 && stock.GetQuantity(i) == 0).Take(free + 1).ToList();
        TransactionResult Ask(int count)
        {
            var transaction = new InventoryTransaction(stock);
            foreach (Item item in spare.Take(count)) transaction.Add(item, 1);
            return stock.IsTransactionValid(transaction);
        }
        TransactionResult fits = Ask(free), over = Ask(free + 1);
        bool full = spare.Count > free && fits == TransactionResult.Success && over != TransactionResult.Success;
        _ctx.Log($"Dev: the bank has {stock.StackCount} of {stock.MaxStacks} stacks, {free} free; {spare.Count} items with no stack in it (no placeholder either) were taken. " +
                 $"The game's room check for {Math.Min(free, spare.Count)} of them: {fits}; for {Math.Min(free + 1, spare.Count)}: {over}. " +
                 (full ? "So every room check of this request sees a full bank. Nothing is added: the checks are only asked, never carried out."
                       : "THE BANK COULD NOT BE MADE TO LOOK FULL: this request does not show the full-bank case. Nothing was added."));
        if (full) _devFillers.AddRange(spare.Take(free));
    }

    // Development: two request files in the mod's folder for "Wear this setup first". The simulator must hold
    // results of the setup on its left (a sim.request first). A file holds part of a result's name (empty: the selected
    // line), then options after "|": "full" makes every room check see a full bank (nothing is added to it), "min" or
    // "max" takes the picture at the smallest or largest window size. The line is found among every result of the setup
    // on the left; for one simulated in the other tab (Fights or Best for) the tab is switched, and its area ticked, until
    // the request ends. Run one request at a time: a sim.request meanwhile would move the window.
    //   fight.request        ticks the box as a click does, logs the words above Fight, the plan slot by slot, for a
    //                        change of loadout the count check worked out dry (DevDryCount), the game's slot names and
    //                        loadout descriptions, the window's size and each column's height and how the box is drawn;
    //                        saves shot-fight-plan.png (pointer off the box) and shot-fight-plan-hover.png (pointer on
    //                        it, with its tooltip), each with its crop-*.png, then puts everything back. Nothing of the
    //                        character changes.
    //   fight-press.request  CHANGES THE CHARACTER. Ticks the box and presses Fight, so the simulated setup goes on
    //                        from the bank and the fight starts. It logs what it is about to do first, with the same
    //                        dry count, so that the log has it next to the real one; the wear itself logs every slot
    //                        and the counts of each piece before, after the loadout change and at the end ("Dev: wear
    //                        ..."), and once more 1.5 s after the press. Then shot-fight-report.png,
    //                        and the box goes back as it was. Nothing is put back on the character. Only for the
    //                        orchestrator, with the maintainer's OK, on a character set up for it.
    const string DevPlanFile = "fight.request", DevPressFile = "fight-press.request";
    string _devWearFile = "", _devWearFight = "";
    int _devWearStep;
    long _devWearAt;
    bool _devWearWasOn, _devWearBest;
    float _devWearScale = -1;
    (HashSet<string> Fight, HashSet<string> Best) _devWearAreas = (new(), new());

    void DevWearRequests()
    {
        long now = (long)Time.GetTicksMsec();
        if (_devWearFile != "")
        {
            if (now >= _devWearAt) DevWearNext(now);
            return;
        }
        // Not while a sim.request is at work: both move the window and its choices.
        if (_devCommands.Count > 0 || _devShotAt != -1 || _devPickerAt != -1 || _devSettings != null) return;
        foreach (string file in new[] { DevPlanFile, DevPressFile })
        {
            string path = Path.Combine(_ctx.Directory, file);
            if (!File.Exists(path)) continue;
            string text = File.ReadAllText(path).Trim();
            File.Delete(path);
            DevWearStart(file, text, now);
            return;
        }
    }

    void DevWearStart(string file, string text, long now)
    {
        string[] parts = text.Split('|', StringSplitOptions.TrimEntries);
        string pick = parts[0];
        var options = parts.Skip(1).Select(o => o.ToLowerInvariant()).ToHashSet();
        if (_window is not { IsAlive: true, Visible: true }) Open();
        if (_window is not { IsAlive: true } || _view == null) { _ctx.Log($"Dev: {file}: the simulator could not be opened; nothing was done."); return; }
        _window.SetMinimised(false);
        _devWearWasOn = _state.WearSetup;
        _devWearBest = _state.BestMode;
        _devWearAreas = (_state.FightAreas.ToHashSet(), _state.BestAreas.ToHashSet());
        // From here on the request ends in DevWearEnd, which puts the box, the tab, the areas, the bank's look and the size back.
        _devWearFile = file;
        _devWearFight = "";
        bool rebuild = false;
        if (pick != "")
        {
            var mine = _state.Results.Values.Where(r => r.SetupKey == _state.SetupKey && !r.Failed).ToList();
            var found = mine.Where(r => r.Target.Name.Contains(pick, StringComparison.OrdinalIgnoreCase)).ToList();
            if (found.Count == 0)
            {
                string Names(IEnumerable<SimResult> results) => string.Join(", ", results.Select(r => r.Target.Name + (r.Target.StanceId != null ? " (each stance)" : "")).Distinct());
                _ctx.Log($"Dev: {file}: no result of the setup on the left ({_state.SetupLabel}) is called '{pick}'. Its results: {(mine.Count == 0 ? "none" : Names(mine))}. " +
                         $"Other setups: {string.Join(" / ", _state.Results.Values.Where(r => r.SetupKey != _state.SetupKey).GroupBy(r => r.SetupLabel).Select(g => g.Key + ": " + Names(g)))}. Nothing was done.");
                DevWearEnd();
                return;
            }
            // A result of the other tab (Best for tries each stance, Fights the one selected) is shown in that tab.
            SimResult line = found.FirstOrDefault(r => (r.Target.StanceId != null) == _state.PerStance) ?? found[0];
            bool perStance = line.Target.StanceId != null;
            if (perStance != _state.PerStance)
            {
                _state.BestMode = perStance;
                rebuild = true;
            }
            // An area the player's "Where" leaves out is ticked until the request ends.
            if (_state.ScopeAreas.Count > 0) _state.ScopeAreas.Add(line.Target.AreaId);
            // In Best for the table shows each fight in the stance that ranks best: that line.
            line = _state.Lines().FirstOrDefault(r => r.Target.FightKey == line.Target.FightKey && !r.Failed) ?? line;
            _state.Viewing = null;
            _state.Selected = line.Target.Key;
            _state.Pinned = true;
            _devWearFight = "Fight " + line.Target.Name;
            _ctx.Log($"Dev: {file}: '{pick}' is {line.Target.Key} of {line.SetupLabel}, shown in the {(_state.BestMode ? "Best for" : "Fights")} tab" +
                     (_state.BestMode != _devWearBest ? " (switched for this request)" : "") + ".");
        }
        if (options.Contains("min") || options.Contains("max"))
        {
            _devWearScale = _settings.Scale;
            _settings.Scale = options.Contains("min") ? GameWindow.MinScale : LargestScale(FooterHeight());
            rebuild = true;
        }
        if (rebuild)
        {
            Build();
            _window!.Visible = true;
        }
        if (options.Contains("full")) DevFillBank();
        _view!.Refresh();
        Button fight = _view.DevFightButton;
        // What is on screen, read back before anything else.
        if (!fight.IsVisibleInTree())
        {
            _ctx.Log($"Dev: {file}: there is no Fight button on show (no result selected?); nothing was done.");
            DevWearEnd();
            return;
        }
        if (_devWearFight != "" && fight.Text != _devWearFight)
        {
            _ctx.Log($"Dev: {file}: the button on show says '{fight.Text}', not '{_devWearFight}'; nothing was done.");
            DevWearEnd();
            return;
        }
        _ctx.Log($"Dev: {file}: the selected fight is '{fight.Text}'; 'Wear this setup first' is {DevBox()}; the words above Fight: '{DevNote()}'");
        if (file == DevPressFile)
            _ctx.Log($"Dev: {file}: THIS CHANGES THE CHARACTER. Next: tick the box if it is not ticked, read back the words above Fight, " +
                     $"then press '{fight.Text}' if the box can put something on. Nothing is put back afterwards.");
        // The box is clicked once the window is laid out (a window opened just now is not yet), as sim.request
        // waits before its pickers.
        _devWearStep = 0;
        _devWearAt = now + 500;
    }

    void DevWearNext(long now)
    {
        string file = _devWearFile;
        if (_view == null || _window is not { IsAlive: true } window)
        {
            _ctx.Log($"Dev: {file}: the simulator window is gone (another character?); the request stopped.");
            DevWearEnd();
            return;
        }
        Button fight = _view.DevFightButton, box = _view.DevWearBox;
        void Next(int step, long wait)
        {
            _devWearStep = step;
            _devWearAt = now + wait;
        }
        switch (_devWearStep)
        {
            case 0:
                if (!box.ButtonPressed) DevClick(box, "Wear this setup first");
                Next(1, 500);
                return;
            case 1:
                _ctx.Log($"Dev: {file}: 'Wear this setup first' is now {DevBox()} (setting {_state.WearSetup}); the words above Fight: '{DevNote()}'");
                if (!box.ButtonPressed || !_state.WearSetup)
                {
                    _ctx.Log($"Dev: {file}: the box did not tick; no picture was taken and Fight was not pressed.");
                    DevWearEnd();
                    return;
                }
                DevDryCount(file);
                if (file == DevPlanFile)
                {
                    DevLayout(file);
                    // The picture of the box as it rests: the click left the pointer on it, which lights it up.
                    DevPoint(window.Root.GetGlobalRect().Position + new Vector2(6, 6));
                    Next(2, 600);
                    return;
                }
                if (_view.DevWearOff != "")
                {
                    _ctx.Log($"Dev: {file}: the box puts nothing on here: Fight was not pressed.");
                    DevWearEnd();
                    return;
                }
                _ctx.Log($"Dev: {file}: pressing '{fight.Text}' now. It will: {DevNote()}");
                DevClick(fight, "Fight");
                Next(10, 1500);
                return;
            case 2:
                _ctx.Log($"Dev: {file}: the box as it rests: {_view.DevWearLook()}");
                DevShot("fight-plan");
                Next(3, 700);
                return;
            case 3:
                // The pointer on the box, as a player reads why: its look then, and its tooltip.
                DevPoint(box.GetGlobalRect().GetCenter());
                Next(4, 1500);
                return;
            case 4:
                _ctx.Log($"Dev: {file}: the box with the pointer on it: {_view.DevWearLook()}; tooltips up: {GameStyle.DevTooltips(_ctx.Tree.Root)}");
                DevShot("fight-plan-hover");
                Next(5, 700);
                return;
            case 5:
                DevPoint(window.Root.GetGlobalRect().Position + new Vector2(6, 6));
                Next(99, 300);
                return;
            case 10:
                _ctx.Log($"Dev: {file}: 1.5 s after the press the window is {(window.Visible ? "open" : "closed")}; the report: '{_state.FightReport}'; " +
                         $"the words above Fight: '{DevNote()}'");
                DevWear("1.5 s after the press", null);
                if (window.Visible) DevShot("fight-report");
                Next(99, 1000);
                return;
            default:
                DevWearEnd();
                return;
        }
    }

    // The pointer to a spot of the game's screen, as a moved mouse would put it, without a click.
    void DevPoint(Vector2 at)
    {
        Vector2 spot = _ctx.Tree.Root.GetFinalTransform() * at;
        Input.ParseInputEvent(new InputEventMouseMotion { Position = spot, GlobalPosition = spot });
    }

    // What the picture is to be compared with: the game's names for the slots and for what its loadouts hold (its own
    // words, LoadoutComponent.GetLoadoutDescriptions), how many lines the words above Fight take, the window's size
    // against the size LargestScale allows at this size (HeightAt, as DevRequests compares it), and what each column
    // needs (SimView.DevLogThreeColumns, as the Columns requests log it), so it shows which one sets the window's height.
    void DevLayout(string file)
    {
        _ctx.Log($"Dev: {file}: the game's slots: " + string.Join(", ", _game!.Data.GetDataRegistryOutput<EquipmentSlot>().GetAllObjects().Select(s => $"{s.Id}='{s.DisplayName.Text}'")));
        try
        {
            foreach (ActiveLoadoutSlot loadout in _combat!.Loadouts.LoadoutDisplayOrder.Where(l => l.IsUnlocked))
                _ctx.Log($"Dev: {file}: the game describes '{loadout.Name}' ({loadout.Data.Id}) as: " + string.Join(" / ", _combat.Loadouts.GetLoadoutDescriptions(loadout)
                    .Select(d => $"{d.CategoryName?.Text}: {string.Join(", ", (d.Contents ?? new()).Select(c => ((GamesByMalcs.Melvor2.Data.INamedObject)c).DisplayName?.Text))}")));
        }
        catch (Exception e) { _ctx.Log($"Dev: {file}: the game's loadout descriptions could not be read: {e.Message}"); }
        Label note = _view!.DevFightNote, report = _view.DevWearReport;
        float scale = _window!.Style.Scale, wide = FullWidth * scale, high = HeightAt(scale);
        Vector2 size = _window.Root.Size, full = size / scale;
        _ctx.Log($"Dev: {file}: the words above Fight take {(note.IsVisibleInTree() ? note.GetLineCount() : 0)} lines, the report {(report.IsVisibleInTree() ? report.GetLineCount() : 0)}; " +
                 $"the details list keeps {_view.DevDetailsList.Size.Y:0} px; window {size.X:0}x{size.Y:0} at size {scale:0.##}, {full.X:0}x{full.Y:0} at full size; " +
                 $"LargestScale allows {wide:0.#}x{high:0.#} at this size{(size.X > wide + 0.5f || size.Y > high + 0.5f ? "  LARGER THAN LargestScale ALLOWS" : ", it fits")}; " +
                 $"the window asks for {_window.Root.GetCombinedMinimumSize().Y:0} high ({_window.Root.GetCombinedMinimumSize().Y / scale:0} at full size)");
        _view.DevThreeColumns($"Dev: {file}");
    }

    // The box, the tab, the ticked areas, the window's size and the bank's look go back to how they were before the
    // request (the mod's own settings, written again). The character is not touched.
    void DevWearEnd()
    {
        string file = _devWearFile;
        _devWearFile = "";
        _devFillers.Clear();
        bool rebuild = false;
        if (_devWearScale > 0)
        {
            _settings.Scale = _devWearScale;
            _devWearScale = -1;
            rebuild = true;
        }
        var (fightAreas, bestAreas) = _devWearAreas;
        if (_state.WearSetup != _devWearWasOn || _state.BestMode != _devWearBest || !_state.FightAreas.SetEquals(fightAreas) || !_state.BestAreas.SetEquals(bestAreas))
        {
            _state.WearSetup = _devWearWasOn;
            _state.BestMode = _devWearBest;
            _state.FightAreas = fightAreas;
            _state.BestAreas = bestAreas;
            SaveChoices();
            rebuild = true;
        }
        if (_window is { IsAlive: true } window)
        {
            bool shown = window.Visible;
            if (rebuild) Build();
            _window!.Visible = shown;
            _view?.Refresh();
        }
        _ctx.Log($"Dev: {file}: done; 'Wear this setup first' is back to {(_devWearWasOn ? "ticked" : "not ticked")}, the tab, the areas and the window's size as they were" +
                 (file == DevPressFile ? "; the equipment stays as it is now: nothing is put back." : "; nothing of the character was changed."));
    }

    string DevBox() => _view is not { } view ? "(no window)"
        : $"{(view.DevWearBox.ButtonPressed ? "ticked" : "not ticked")}{(view.DevWearOff != "" ? $", putting nothing on ({view.DevWearOff})" : "")}";

    string DevNote() => _view is { } view && view.DevFightNote.IsVisibleInTree() ? view.DevFightNote.Text : "(no words above Fight)";
#endif
}
