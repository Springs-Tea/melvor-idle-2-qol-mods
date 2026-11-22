using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Components.Equipment;
using GamesByMalcs.Melvor2.Components.ItemEquipping;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Combat;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace CombatSim;

/// <summary>
/// The slots on the left of the simulator's window, as the game's own rules have them (issue #46): a two-handed weapon
/// fills both hands, and the Offhand shows it the way the game's equipment screen does. The rules are in SimGear; this
/// reads the loadout, puts the player's changes on, keeps them in their one shape and turns a click in the item
/// picker into a change.
/// </summary>
public partial class CombatSimMod
{
    // What the slots on the left were last made from: the loadout's own items, the same with the changes on, and the
    // slots, in the game's order. A pick in the item picker starts from them.
    GearSet? _gearLoadout, _gearWorn;
    List<EquipmentSlot> _gearSlots = new();
    readonly FilledLook _filledLook = new();

    /// <summary>
    /// Fills SimState.Gear with the loadout's items (<paramref name="saved"/>: as that loadout was stored, else what is
    /// worn) and the changes on top, and returns the weapon of that setup. The changes are put into their one shape on
    /// the way (SimGear.Overrides), so the copy fights with what the slots show, and a slot has the gold frame exactly
    /// when it has a change of its own: the frames and "+ N changes" tell the same.
    /// </summary>
    WeaponCapability? ReadGear(EquipmentLoadout? saved)
    {
        var slots = new List<(EquipmentSlot Slot, IActiveEquipmentSlot Active)>();
        foreach (EquipmentSlot slot in _game!.Data.GetDataRegistryOutput<EquipmentSlot>().GetAllObjects())
            if (_combat!.Equipment.TryGetActiveSlot(slot, out IActiveEquipmentSlot? active) && active != null) slots.Add((slot, active));
        _gearSlots = slots.Select(s => s.Slot).ToList();

        GearSet loadout = saved != null ? SimGear.Stored(saved, _gearSlots, _ctx.Log) : SimGear.Live(_combat!.Equipment, _gearSlots, _ctx.Log);
        var items = _game.Data.GetDataRegistryOutput<Item>();
        GearSet worn = SimGear.Resolve(loadout, _state.Overrides, _gearSlots, id => items.TryGetObjectById(id, out Item? item) ? item : null, _ctx.Log);
        Dictionary<string, string> overrides = SimGear.Overrides(loadout, worn, _gearSlots);
        if (overrides.Count != _state.Overrides.Count || overrides.Any(o => _state.Overrides.GetValueOrDefault(o.Key) != o.Value))
        {
#if DEVTOOLS
            _ctx.Log($"Dev: gear: the changes {DevChanges(_state.Overrides)} are kept as {DevChanges(overrides)}");
#endif
            SetOverrides(overrides);
        }
        _gearLoadout = loadout;
        _gearWorn = worn;

        // The game's tint is looked for only when a slot is filled from another: a one-handed setup pays nothing.
        Color? tint = worn.Slots.Values.Any(p => p.FilledFrom != null) ? _filledLook.Tint(_ctx.Log) : null;
        WeaponCapability? wielded = null;
        foreach (var (slot, active) in slots)
        {
            GearPiece? piece = worn[slot.Id];
            Item? item = piece?.Item;
            string from = piece?.FilledFrom ?? "";
            _state.Gear.Add(new GearSlot(slot.Id, slot.DisplayName.Text, active.GridPosition.X, active.GridPosition.Y,
                item?.Id, item?.DisplayName.GetPluralText(1) ?? "", piece?.Quantity ?? 0, item?.Icon, slot.FitsType?.Icon,
                _state.Overrides.ContainsKey(slot.Id))
            {
                FilledFrom = from,
                FilledFromName = from == "" ? "" : _gearSlots.FirstOrDefault(s => s.Id == from)?.DisplayName.Text ?? from,
                FilledTint = from == "" ? null : tint,
            });
            if (from == "" && item != null && item.Capabilities.TryGet(out WeaponCapability? weapon)) wielded = weapon;
        }
#if DEVTOOLS
        DevLogGear("the slots on the left were made", saved);
#endif
        return wielded;
    }

    void SetOverrides(Dictionary<string, string> overrides)
    {
        _state.Overrides.Clear();
        foreach (var (slot, item) in overrides) _state.Overrides[slot] = item;
    }

    /// <summary>What the slots on the left show, for the copy to be checked against (SimOptions.Shown).</summary>
    Dictionary<string, string>? ShownGear() => _gearWorn?.OwnItems();

    // A click in a slot's item picker: back to the loadout's own (null), nothing (""), or one of the items the picker
    // lists for the slot (ChoicesFor), put on the slots on the left as equipping it in the game would (SimGear.Put); the
    // view then makes the slots again (BuildGear).
    void ISimLookup.Pick(GearSlot slot, string? itemId)
    {
        EquipmentSlot? target = _gearSlots.FirstOrDefault(s => s.Id == slot.SlotId);
        if (_game == null || _gearLoadout == null || _gearWorn == null || target == null) return;
        GearSet worn = _gearWorn.Copy();
        if (itemId == null) SimGear.Restore(worn, _gearLoadout, _gearSlots, target.Id);
        else if (itemId == "") SimGear.TakeOff(worn, target.Id);
        else if (_game.Data.GetDataRegistryOutput<Item>().TryGetObjectById(itemId, out Item? item))
            SimGear.Put(worn, target, item, 0, _ctx.Log);
        else return;
#if DEVTOOLS
        DevPicked(target, itemId, worn);
#endif
        SetOverrides(SimGear.Overrides(_gearLoadout, worn, _gearSlots));
    }

    /// <summary>
    /// How the game's equipment screen tints the picture of a slot that another slot's item fills: the OccupiedColor the
    /// game's combat screen gives its slot views, read from the scene without making it (as BankButton reads the bank's
    /// button): CombatActiveView.ScenePath, its Equipment (an EquipmentGrid), that grid's EquipmentSlotViewScene, and the
    /// OccupiedColor that scene sets on its EquipmentSlotView. Read once a session, the first time a slot is filled, so
    /// it is there before the player has opened the combat screen. Should the scene leave the colour to the game's code,
    /// it is read from one of the game's slot views once the game has made them. Null until then, and when the game no
    /// longer has it: the view then dims the picture as it dims an empty slot's.
    /// </summary>
    sealed class FilledLook
    {
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_OccupiedColor")]
        internal static extern Color OccupiedColorOf(EquipmentSlotView view);

        // The game's views are looked for at most once a second, and only while a slot on the left is filled.
        readonly Tempo _search = new(1000);
        Color? _tint;
        bool _sceneRead, _gone;
#if DEVTOOLS
        public string DevFrom = "not looked for yet";
        public Color? DevScene;
#endif

        public Color? Tint(Action<string> log)
        {
            if (_tint != null || _gone) return _tint;
            if (!_sceneRead)
            {
                _sceneRead = true;
#if DEVTOOLS
                var clock = System.Diagnostics.Stopwatch.StartNew();
#endif
                string scene = "";
                try { _tint = FromScene(out scene); }
                catch (Exception e)
                {
                    log("The game's tint for a slot that a two-handed weapon fills could not be read from its combat screen " +
                        $"(the game was updated?): {e.Message}. The simulator dims that slot until the game's equipment screen has been opened.");
                }
#if DEVTOOLS
                DevScene = _tint;
                scene = scene.Or("its path not read");
                DevFrom = (_tint is { } read ? $"#{read.ToHtml()}, the OccupiedColor of the game's combat screen scene ({scene})"
                    : $"not in the game's combat screen scene ({scene}); looked for in its slot views") + $", the scene read in {clock.Elapsed.TotalMilliseconds:0.0} ms";
#endif
                if (_tint != null) return _tint;
            }
            if (!_search.Due()) return null;
            try { _tint = FromView(); }
            catch (Exception e) when (e is MissingMemberException or TypeLoadException)
            {
                _gone = true;
                log($"The game's tint for a slot that a two-handed weapon fills was not found (the game was updated?): {e.Message}. The simulator dims that slot.");
            }
#if DEVTOOLS
            DevFrom = _tint is { } found ? $"#{found.ToHtml()}, the OccupiedColor of one of the game's EquipmentSlotView" : _gone ? "gone"
                : "not in the game's combat screen scene, and no EquipmentSlotView of the game found yet";
#endif
            return _tint;
        }

        // Their own methods, never inlined: a game update that removed these fails when they are called, inside the try.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static Color? FromScene(out string scene)
        {
            scene = CombatActiveView.ScenePath;
            SceneData.Spot grid = SceneData.Follow(SceneData.Root(scene), "Equipment");
            SceneData.Spot view = SceneData.Scene(grid, "EquipmentSlotViewScene");
            return SceneData.TryProperty(view, "OccupiedColor", out Variant value) && value.VariantType == Variant.Type.Color ? value.AsColor() : null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static Color? FromView() => GameAccess.FindNode<EquipmentSlotView>(GameAccess.FindGameView()) is { } view ? OccupiedColorOf(view) : null;
    }

#if DEVTOOLS
    // ---- development -------------------------------------------------------------------------------------------

    static string DevChanges(IReadOnlyDictionary<string, string> overrides) =>
        overrides.Count == 0 ? "{}" : "{" + string.Join(", ", overrides.Select(o => $"{o.Key}={(o.Value == "" ? "nothing" : o.Value)}")) + "}";

    /// <summary>The left side, as SimEngine's gear line compares it with the copy at the start of its fight.</summary>
    string DevGearText() => _gearWorn == null ? "" : SimGear.Describe(_gearWorn, _gearSlots);

    void DevLogGear(string why, EquipmentLoadout? saved = null)
    {
        if (_gearWorn == null || _gearLoadout == null || _combat == null) return;
        _ctx.Log($"Dev: gear ({why}): the left side: {SimGear.Describe(_gearWorn, _gearSlots)}");
        _ctx.Log($"Dev: gear: changes {DevChanges(_state.Overrides)}, label \"{_state.SetupLabel}\", gold frames on {string.Join(", ", _state.Gear.Where(g => g.Changed).Select(g => g.SlotName)).Or("none")}; " +
                 $"filled from another slot: {string.Join(", ", _state.Gear.Where(g => g.FilledFrom != "").Select(g => $"{g.SlotName} by {g.ItemName} from {g.FilledFromName}, tint {(g.FilledTint is { } t ? "#" + t.ToHtml() : "none (the empty slot's)")}")).Or("none")}; " +
                 $"the tint: {_filledLook.DevFrom}; two-handed weapons {(SimGear.Off ? "OFF (every slot on its own)" : "read")}");
        foreach (var (slot, piece) in _gearWorn.Slots.Where(s => s.Value.FilledFrom == null))
        {
            List<string> fills = SimGear.FillsToo(piece.Item, slot);
            if (fills.Count > 0)
                _ctx.Log($"Dev: gear: {piece.Item.DisplayName.GetPluralText(1)} ({piece.Item.Id}) in {slot} also fills {string.Join(", ", fills)}; its OccupiesSlots {SimGear.DevOccupies(piece.Item)}");
        }
        _ctx.Log($"Dev: gear: the loadout's own: {SimGear.Describe(_gearLoadout, _gearSlots)}");
        _ctx.Log($"Dev: gear: what the character wears now: {SimGear.Describe(SimGear.Live(_combat.Equipment, _gearSlots, _ctx.Log), _gearSlots)}; " +
                 $"slots the game marks occupied: {string.Join(", ", _gearSlots.Where(s => _combat.Equipment.TryGetActiveSlot(s, out IActiveEquipmentSlot? a) && a != null && SimGear.IsFilled(a)).Select(s => s.Id)).Or("none")}");
        if (saved != null)
            _ctx.Log($"Dev: gear: the saved loadout's slots the game marks occupied: {string.Join(", ", saved.Slots.Where(s => s.Value?.IsOccupied == true).Select(s => s.Key.Id)).Or("none")}");
    }

    // A pick, before and after, and what the game says putting that item on would replace, when the slots on the left
    // before the pick are what the character wears (EquipmentComponent.GetAvailableSlotsForItem: only a question).
    void DevPicked(EquipmentSlot slot, string? itemId, GearSet after)
    {
        string what = itemId == null ? "back to the loadout's" : itemId == "" ? "nothing" : itemId;
        var gone = _gearWorn!.Slots.Where(s => !SimGear.Same(s.Value, after[s.Key])).Select(s => $"{s.Key}: {s.Value.Item.DisplayName.GetPluralText(1)}").ToList();
        _ctx.Log($"Dev: gear: pick {slot.Id} = {what}. Before: {SimGear.Describe(_gearWorn, _gearSlots)}. After: {SimGear.Describe(after, _gearSlots)}. " +
                 $"Out of their slot: {string.Join(", ", gone).Or("nothing")}");
        if (itemId is null or "" || _combat == null || !_game!.Data.GetDataRegistryOutput<Item>().TryGetObjectById(itemId, out Item? item)) return;
        string wearing = SimGear.Describe(SimGear.Live(_combat.Equipment, _gearSlots, _ctx.Log), _gearSlots);
        if (wearing != SimGear.Describe(_gearWorn, _gearSlots))
        {
            _ctx.Log("Dev: gear: not compared with the game: the slots on the left before the pick are not what the character wears.");
            return;
        }
        try
        {
            var answers = _combat.Equipment.GetAvailableSlotsForItem(item).Select(a =>
                $"{a.Slot?.Slot.Id} replaces [{string.Join(", ", (a.Replaces ?? new()).Select(r => $"{r.Slot.Id} ({r.Equipped?.Item.DisplayName.GetPluralText(1) ?? "-"}{(SimGear.IsFilled(r) ? ", occupied" : "")})"))}]");
            _ctx.Log($"Dev: gear: the game says, for what the character wears, {item.DisplayName.GetPluralText(1)} can go into: {string.Join("; ", answers).Or("no slot")}");
        }
        catch (Exception e) { _ctx.Log("Dev: gear: the game could not be asked: " + e.Message); }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Slot")]
    static extern IActiveEquipmentSlot? SlotOfView(EquipmentSlotView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Icon")]
    static extern TextureRect? IconOfView(EquipmentSlotView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Tooltip")]
    static extern EquipmentSlotTooltip? TooltipOfView(EquipmentSlotView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SlotLabel")]
    static extern Label? SlotLabelOf(EquipmentSlotTooltip tooltip);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NameLabel")]
    static extern Label? NameLabelOf(EquipmentSlotTooltip tooltip);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_DescriptionLabel")]
    static extern Label? DescriptionLabelOf(EquipmentSlotTooltip tooltip);

    // Further lines of sim.request (CombatSimMod.DevWindowCommand), for issue #46. None of them changes the character:
    //   pick=<slot>=<item>   opens that slot's item picker as a click on the slot does and presses its row as the
    //                        player's click does: <item> is an id or a part of a name of an item that fits (the ones the
    //                        character owns first, as the picker lists them), "" for Nothing, "*" for Back to the
    //                        loadout's item. Then it reads back the slots as drawn. No such row: one line, and the rest
    //                        of the request is dropped
    //   picker=<slot>        opens that slot's item picker, as a click on the slot does
    //   gear=<name>          logs the slots on the left, the open picker's words and the game's own equipment screen as
    //                        it is drawn (its tint and tooltips), then shot-<name>.png and crop-<name>.png, and
    //                        crop-<name>-game.png of the game's equipment grid when it is on show. It leaves the window
    //                        as it is: after "close" the pictures are of the game alone
    //   hover=<slot>         rests the pointer on the game's own slot view of that slot on its equipment screen, as a
    //                        player's pointer would (nothing is clicked); a second later it logs the game's tooltip and
    //                        takes shot-hover.png and crop-hover-game.png. Put it last: the next lines do not wait
    //   raw=<slot>=<item>    simulates, with the loadout and only that change (as pick=, without "*"), put on the copy
    //                        as the game takes it by itself, the way the simulator did before issue #46; the gear line
    //                        of the fight compares the copy with what the slots on the left show for that pick. The
    //                        slots on the left stay as they are; the scan is named "(raw ...)" and is the request's,
    //                        as one of "simulate" is (DevSimulate)
    bool DevGearCommand(string command)
    {
        if (command.StartsWith("pick=", StringComparison.Ordinal)) DevPick(command[5..]);
        else if (command.StartsWith("raw=", StringComparison.Ordinal)) DevRawScan(command[4..]);
        else if (command.StartsWith("picker=", StringComparison.Ordinal))
        {
            if (_window is not { IsAlive: true, Visible: true }) Open();
            _view?.DevOpenPicker(command[7..].Trim());
            _ctx.Log($"Dev: gear: asked to open the item picker of '{command[7..].Trim()}'; {DevPickerText()}");
        }
        else if (command.StartsWith("gear=", StringComparison.Ordinal)) DevGearShot(command[5..].Trim());
        else if (command.StartsWith("hover=", StringComparison.Ordinal)) DevHover(command[6..].Trim());
        else return false;
        return true;
    }

    // "<slot>=<item>": the slot of the left side, the item as the picker would pass it (null for "*", "" for nothing)
    // and the words of its row in the picker. False, with one line in the log, when there is no such slot or item, or a
    // scan is running.
    bool DevParsePick(string command, string request, out GearSlot slot, out string? itemId, out string row)
    {
        int at = request.IndexOf('=');
        string slotName = at < 0 ? request.Trim() : request[..at].Trim(), wanted = at < 0 ? "" : request[(at + 1)..].Trim();
        if (_window is not { IsAlive: true, Visible: true }) Open();
        _window?.SetMinimised(false);
        slot = _state.Gear.FirstOrDefault(g => g.SlotName.Equals(slotName, StringComparison.OrdinalIgnoreCase))
            ?? _state.Gear.FirstOrDefault(g => g.SlotName.Contains(slotName, StringComparison.OrdinalIgnoreCase))!;
        itemId = wanted == "*" ? null : wanted;
        row = wanted == "*" ? "Back to the loadout's item" : wanted == "" ? "Nothing (leave it empty)" : "";
        if (slot == null || _state.Running)
        {
            _ctx.Log($"Dev: gear: {command}={request}: {(slot == null ? "no slot is called that; the slots are " + string.Join(", ", _state.Gear.Select(g => g.SlotName)) : "a scan is running")}.");
            return false;
        }
        if (wanted is "" or "*") return true;
        // An id, or the first item of the picker's list whose name holds the words.
        var choices = ((ISimLookup)this).ChoicesFor(slot).OrderByDescending(c => c.Owned > 0).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        GearChoice? choice = choices.FirstOrDefault(c => c.ItemId == wanted)
            ?? choices.FirstOrDefault(c => c.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            ?? choices.FirstOrDefault(c => c.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        if (choice == null)
        {
            _ctx.Log($"Dev: gear: {command}={request}: no item that fits {slot.SlotName} is called that.");
            return false;
        }
        itemId = choice.ItemId;
        row = choice.Name;
        return true;
    }

    // The one change of a raw= scan and the slots on the left that the same pick on the loadout gives, for
    // DevSimulateOptions.
    (string Slot, string Item, GearSet Left)? _devRaw;

    void DevRawScan(string request)
    {
        if (!DevParsePick("raw", request, out GearSlot slot, out string? itemId, out _)) return;
        EquipmentSlot? target = _gearSlots.FirstOrDefault(s => s.Id == slot.SlotId);
        if (itemId == null || _gearLoadout == null || target == null || _game == null)
        {
            _ctx.Log($"Dev: gear: raw={request}: takes an item or nothing, not \"*\".");
            return;
        }
        GearSet left = _gearLoadout.Copy();
        if (itemId == "") SimGear.TakeOff(left, target.Id);
        else if (_game.Data.GetDataRegistryOutput<Item>().TryGetObjectById(itemId, out Item? item)) SimGear.Put(left, target, item, 0, _ctx.Log);
        _devRaw = (target.Id, itemId, left);
        _ctx.Log($"Dev: gear: raw={request}: the loadout with {target.Id} = {(itemId == "" ? "nothing" : itemId)} only, put on the copy as the game " +
                 $"takes it by itself. The slots on the left would show: {SimGear.Describe(left, _gearSlots)}");
        // As a "simulate" line starts it: the scan is the request's, which the put back takes out again.
        DevSimulate();
    }

    // Simulate's options, for SimEngine.DevGear; for a raw= scan its one change instead of the player's.
    void DevSimulateOptions(SimOptions options)
    {
        options.DevGear = DevGearText();
        if (_devRaw is not { } raw) return;
        _devRaw = null;
        options.Overrides = new Dictionary<string, string> { [raw.Slot] = raw.Item };
        options.DevRaw = true;
        options.DevGear = SimGear.Describe(raw.Left, _gearSlots);
        options.Shown = raw.Left.OwnItems();
        options.SetupKey += $";raw {raw.Slot}={raw.Item}";
        options.SetupLabel += $" (raw {raw.Slot}={(raw.Item == "" ? "nothing" : raw.Item)})";
    }

    // pick=: the player's way, through the window that is there. The picker is opened as a click on the slot opens it,
    // its row pressed as a click presses it (SimView.Choose: Pick, then the slots are made and drawn again), and the
    // drawn slots are read back and compared with what the slots were made from.
    void DevPick(string request)
    {
        if (!DevParsePick("pick", request, out GearSlot slot, out _, out string row) || _view == null)
        {
            _ctx.Log("Dev: gear: the rest of the request is dropped: no picture of a pick that did not happen.");
            _devCommands.Clear();
            return;
        }
        string before = _state.SetupLabel;
        _view.DevOpenItems(slot);
        if (!_view.DevPressRow(row))
        {
            _ctx.Log($"Dev: gear: pick={request}: the {slot.SlotName} picker has no row \"{row}\" ({DevPickerText()}). Nothing was pressed; the rest of the request is dropped.");
            _view.DevClosePicker();
            _devCommands.Clear();
            return;
        }
        var drawn = _view.DevDrawnSlots();
        // By name: the grid is drawn row by row, the slots are made in the game's order.
        static string Names(IEnumerable<string> names) => string.Join(", ", names.Order(StringComparer.Ordinal)).Or("none");
        Color dimmed = SimView.EmptyTint;
        string gold = Names(drawn.Where(d => d.Gold).Select(d => d.Name));
        string tinted = Names(drawn.Where(d => d.Tint != null).Select(d => $"{d.Name} #{d.Tint}"));
        string goldState = Names(_state.Gear.Where(g => g.Changed).Select(g => g.SlotName));
        string tintState = Names(_state.Gear.Where(g => g.FilledFrom != "" || g.ItemId == null)
            .Select(g => $"{g.SlotName} #{(g.ItemId == null ? dimmed : g.FilledTint ?? dimmed).ToHtml()}"));
        bool same = drawn.Count == _state.Gear.Count && gold == goldState && tinted == tintState;
        _ctx.Log($"Dev: gear: pick={request}: pressed \"{row}\" in the {slot.SlotName} picker; \"{before}\" became \"{_view.DevShowing}\". Drawn: {drawn.Count} slots " +
                 $"({string.Join(", ", drawn.Select(d => $"{d.Name}: {d.Picture}"))}); gold frames on {gold}; pictures tinted: {tinted}. " +
                 $"Made from: {_state.Gear.Count} slots, gold {goldState}, tinted {tintState}. {(same ? "THE SAME." : "THE DRAWN SLOTS DIFFER.")}");
    }

    // The words of the floating panel next to the window, when one is open: the item picker's title, note and rows.
    string DevPickerText()
    {
        if (_window is not { IsAlive: true } window) return "no window";
        var panels = window.Layer.GetChildren().OfType<PanelContainer>().Where(p => p != window.Root && p.Visible && !p.IsQueuedForDeletion()).ToList();
        if (panels.Count == 0) return "no picker is open";
        var labels = panels[0].FindChildren("*", "Label", true, false).OfType<Label>().Where(l => l.IsVisibleInTree() && l.Text != "").Select(l => l.Text).ToList();
        return $"the picker shows {labels.Count} words: " + string.Join(" | ", labels.Take(14)) + (labels.Count > 14 ? $" | and {labels.Count - 14} more" : "");
    }

    // The game's own slot views, wherever the game has made them.
    List<EquipmentSlotView> DevGameViews()
    {
        var views = new List<EquipmentSlotView>();
        DevFind(GameAccess.FindGameView() ?? (Node)_ctx.Tree.Root, views);
        return views;
    }

    // Every slot view of the game's equipment screen, as it is drawn: what it holds, its picture's tint against the
    // scene's and the tint the left side uses, and its tooltip's words. Returns the grid of those on show.
    Rect2? DevLogGameSlots(List<EquipmentSlotView> views)
    {
        Rect2? grid = null;
        string ours = _state.Gear.FirstOrDefault(g => g.FilledFrom != "")?.FilledTint is { } t ? "#" + t.ToHtml() : "none (no filled slot on the left, or dimmed)";
        foreach (EquipmentSlotView view in views)
        {
            IActiveEquipmentSlot? slot = SlotOfView(view);
            TextureRect? icon = IconOfView(view);
            bool shown = view.IsVisibleInTree(), occupied = slot != null && SimGear.IsFilled(slot);
            EquipmentSlotTooltip? tip = TooltipOfView(view);
            string words = tip == null ? "no tooltip" : $"tooltip (on show {tip.IsVisibleInTree()}): slot \"{SlotLabelOf(tip)?.Text}\", name \"{NameLabelOf(tip)?.Text}\", " +
                                                        $"description \"{DescriptionLabelOf(tip)?.Text}\"";
            _ctx.Log($"Dev: gear: the game's slot view {view.Name} of {slot?.Slot.Id ?? "no slot"}: {slot?.Equipped?.Item.DisplayName.GetPluralText(1) ?? "-"} x{slot?.Quantity}" +
                     $"{(occupied ? ", occupied" : "")}; on show {shown}; picture {icon?.Texture?.ResourcePath ?? "-"} tinted #{icon?.Modulate.ToHtml()}, self #{icon?.SelfModulate.ToHtml()}, " +
                     $"the view #{view.Modulate.ToHtml()} / self #{view.SelfModulate.ToHtml()}; its OccupiedColor #{FilledLook.OccupiedColorOf(view).ToHtml()}, " +
                     $"the scene's {(_filledLook.DevScene is { } scene ? "#" + scene.ToHtml() : "none")}, the left side's {ours}; {words}; at {view.GetGlobalRect()}");
            if (shown) grid = grid is { } g ? g.Merge(view.GetGlobalRect()) : view.GetGlobalRect();
        }
        if (views.Count == 0) _ctx.Log("Dev: gear: the game has made no equipment screen yet in this session: open the combat screen by hand for the game's own slots.");
        return grid;
    }

    // The window is left as it is: closed ("close" before it), the picture is of the game alone.
    void DevGearShot(string name)
    {
        DevLogGear("gear=" + name);
        _ctx.Log("Dev: gear: " + DevPickerText());
        var views = DevGameViews();
        Rect2? grid = DevLogGameSlots(views);
        DevShot(name);
        if (grid is not { } rect)
        {
            if (views.Count > 0) _ctx.Log($"Dev: gear: crop-{name}-game.png not taken: none of the game's {views.Count} slot views is on show. Open the combat screen for it.");
            return;
        }
        DevGameCrop(name, rect);
    }

    // crop-<name>-game.png: the game's equipment grid alone, with room around it for its tooltip.
    void DevGameCrop(string name, Rect2 rect, float room = 12)
    {
        void Capture()
        {
            RenderingServer.FramePostDraw -= Capture;
            try
            {
                using Image image = _ctx.Tree.Root.GetTexture().GetImage();
                Rect2 frame = _ctx.Tree.Root.GetFinalTransform() * rect.Grow(room);
                var whole = new Rect2I(0, 0, image.GetWidth(), image.GetHeight());
                Rect2I part = new Rect2I((int)frame.Position.X, (int)frame.Position.Y, (int)frame.Size.X, (int)frame.Size.Y).Intersection(whole);
                if (part.Size.X <= 0 || part.Size.Y <= 0) { _ctx.Log($"Dev: gear: crop-{name}-game.png not taken: the grid is off the screen."); return; }
                using Image crop = image.GetRegion(part);
                crop.SavePng(System.IO.Path.Combine(_ctx.Directory, $"crop-{name}-game.png"));
                bool covered = _window is { IsAlive: true, Visible: true } && _window.Root.GetGlobalRect().Intersects(rect);
                _ctx.Log($"Dev: gear: crop-{name}-game.png is the game's equipment, {part.Position.X},{part.Position.Y} {part.Size.X}x{part.Size.Y}" +
                         (covered ? "; THE SIMULATOR'S WINDOW LIES OVER IT: close the window first (\"close\")." : "; nothing of the simulator lies over it."));
            }
            catch (Exception e) { _ctx.Log($"Dev: gear: crop-{name}-game.png could not be taken: {e.Message}"); }
        }
        RenderingServer.FramePostDraw += Capture;
    }

    // hover=<slot>: the pointer onto the game's own slot view, as a player's pointer; the game shows its tooltip.
    void DevHover(string slotName)
    {
        EquipmentSlotView? view = DevGameViews().FirstOrDefault(v => v.IsVisibleInTree() && SlotOfView(v)?.Slot.DisplayName.Text.Contains(slotName, StringComparison.OrdinalIgnoreCase) == true);
        if (view == null)
        {
            _ctx.Log($"Dev: gear: hover={slotName}: no slot view of the game called that is on show. Open the combat screen by hand; the rest of the request is dropped.");
            _devCommands.Clear();
            return;
        }
        Vector2 spot = _ctx.Tree.Root.GetFinalTransform() * view.GetGlobalRect().GetCenter();
        Input.ParseInputEvent(new InputEventMouseMotion { Position = spot, GlobalPosition = spot });
        bool covered = _window is { IsAlive: true, Visible: true } && _window.Root.GetGlobalRect().Intersects(view.GetGlobalRect());
        _ctx.Log($"Dev: gear: hover={slotName}: the pointer rests on the game's slot view {view.Name} ({SlotOfView(view)?.Slot.Id}) at {spot}" +
                 (covered ? "; THE SIMULATOR'S WINDOW LIES OVER IT: close the window first (\"close\")." : "."));
        // A second for the game's tooltip to come up, then its words and the pictures.
        _ctx.Tree.CreateTimer(1.0).Timeout += () =>
        {
            if (!GodotObject.IsInstanceValid(view)) return;
            Rect2 area = view.GetGlobalRect();
            if (TooltipOfView(view) is { } tip && tip.IsVisibleInTree()) area = area.Merge(tip.GetGlobalRect());
            DevLogGameSlots(new List<EquipmentSlotView> { view });
            DevShot("hover");
            DevGameCrop("hover", area);
        };
    }

    static void DevFind<T>(Node node, List<T> into) where T : Node
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is T found) into.Add(found);
            DevFind(child, into);
        }
    }
#endif
}

#if DEVTOOLS
static class DevText
{
    /// <summary>The text, or <paramref name="none"/> when it is empty.</summary>
    public static string Or(this string text, string none) => text == "" ? none : text;
}

// Development: what pick= needs of the window (CombatSimGear.cs, DevPick).
sealed partial class SimView
{
    /// <summary>The item picker of the slot, opened as a click on the slot opens it.</summary>
    public void DevOpenItems(GearSlot slot) => OpenPicker(slot, _gearHolder);

    public void DevClosePicker() => ClosePicker();

    /// <summary>Presses the row of the open picker with these words, as a click does. False when there is none.</summary>
    public bool DevPressRow(string words)
    {
        if (_picker == null || !GodotObject.IsInstanceValid(_picker)) return false;
        Button? row = _picker.FindChildren("*", "Button", true, false).OfType<Button>()
            .FirstOrDefault(b => b.IsVisibleInTree() && b.FindChildren("*", "Label", true, false).OfType<Label>().FirstOrDefault()?.Text == words);
        row?.EmitSignal(BaseButton.SignalName.Pressed);
        return row != null;
    }

    /// <summary>The slots as drawn on the left: the slot's name (from its tooltip), the gold frame, the picture's tint
    /// when it is not plain, and the picture.</summary>
    public List<(string Name, bool Gold, string? Tint, string Picture)> DevDrawnSlots() =>
        _gearHolder.GetChildren().Where(c => !c.IsQueuedForDeletion()).SelectMany(grid => grid.GetChildren()).OfType<Button>()
            .Select(b =>
            {
                TextureRect? picture = b.GetChildren().OfType<TextureRect>().FirstOrDefault();
                bool gold = b.GetThemeStylebox("normal") is StyleBoxFlat frame && frame.BorderColor == GameStyle.Highlight;
                string? tint = picture == null || picture.Modulate == Colors.White ? null : picture.Modulate.ToHtml();
                return (b.TooltipText.Split(':')[0], gold, tint, System.IO.Path.GetFileNameWithoutExtension(picture?.Texture?.ResourcePath ?? "-"));
            }).ToList();

    /// <summary>The line over the results: what the table is "With:".</summary>
    public string DevShowing => _showing.Text;
}
#endif
