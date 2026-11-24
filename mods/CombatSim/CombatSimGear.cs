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

    // The card's equipment IDs omit quantities, so spending an arrow leaves the setup alone.
    sealed record Worn(string Loadout, string Equipment, string WeaponType, string Stance, string Spell);
    Worn? _lastWorn;
    const int WaitMilliseconds = 5000;
    (string Loadout, SimTarget Target, ulong Until)? _fightSwap;
    const string WaitingForLoadout = "The game is still changing loadouts. Wait for the new setup.";
    const string LoadoutTimedOut = "\"Wear this setup first\" timed out waiting for the loadout change. Check your equipment.";
    // Until the player answers, reapply the original picks to each new worn setup.
    (SimSetup Left, SimSetup Other, string WornLoadout)? _pendingEquipment;
    bool _chooseGear => _pendingEquipment.HasValue;
    bool _cannotReadWorn;
    string _wornDescription = "", _keepDescription = "";

    // A saved loadout can have results before the player first wears it. Its initial default uses
    // the same key as that unread default. Returning to any observed default brings its rows back.
    sealed record LoadoutDefault(string First, string Current)
    {
        public string? ChangedTo => Current == First ? null : Current;
    }
    readonly Dictionary<(string Loadout, string Weapon), LoadoutDefault> _loadoutStances = new();
    readonly Dictionary<string, LoadoutDefault> _loadoutSpells = new();
    static void Remember<T>(Dictionary<T, LoadoutDefault> defaults, T key, string value) where T : notnull =>
        defaults[key] = defaults.TryGetValue(key, out var seen) ? seen with { Current = value } : new(value, value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    Worn WornSetup()
    {
        var kind = WornWeaponKind();
        return new(_combat!.Loadouts.CurrentLoadout?.Data.Id ?? "", string.Join("; ", CardSetup.Equipment(_game!, _combat)),
            kind?.Id ?? "", kind == null ? "" : SelectedStance(_combat, kind)?.Id ?? "",
            _combat.MagicSpells.SelectedSpell?.Data.Id ?? "");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    string DescribeWorn()
    {
        var loadout = _combat!.Loadouts.CurrentLoadout;
        string name = string.IsNullOrWhiteSpace(loadout?.Name) ? loadout?.Data.DisplayName.Text ?? "Current equipment" : loadout.Name;
        var weapon = _combat.Equipment.CurrentWeapon ?? _combat.Equipment.DefaultWeapon;
        return string.Join(", ", new[] { name, weapon?.Item?.DisplayName.GetPluralText(1),
            weapon?.Weapon?.Type is { } type ? SelectedStance(_combat, type)?.DisplayName.Text : null }
            .Where(w => !string.IsNullOrEmpty(w)));
    }

    bool CheckWorn(bool fromFight = false)
    {
        if (_game == null || _combat == null || _cannotReadWorn) return false;
        try { return FollowWorn(fromFight); }
        catch (Exception e)
        {
            _cannotReadWorn = true;
            _fightSwap = null;
            if (_state.Message == WaitingForLoadout) _state.Message = "";
            _pendingEquipment = null;
            _ctx.Log($"Automatic equipment following is off for this character: {e.GetType().Name}: {e.Message}");
            return true;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    bool FollowWorn(bool fromFight)
    {
        var swap = _fightSwap;
        bool clearWait = swap != null && _state.Message == WaitingForLoadout;
        if (swap is { } pending)
        {
            if (IsSwappingLoadout(_combat!.Equipment))
            {
                if (Time.GetTicksMsec() < pending.Until) return false;
                _fightSwap = null;
                _state.Message = LoadoutTimedOut;
                _ctx.Log($"{_state.Message} Waited {WaitMilliseconds} ms for {pending.Loadout}.");
                return true;
            }
            fromFight = _combat.Loadouts.CurrentLoadout?.Data.Id == pending.Loadout;
            _fightSwap = null;
            if (clearWait) _state.Message = "";
        }
        Worn now = WornSetup();
        Worn? before = _lastWorn;
        if (before == now) return clearWait;
        _wornDescription = DescribeWorn();
        Remember(_loadoutStances, (now.Loadout, now.WeaponType), now.Stance);
        Remember(_loadoutSpells, now.Loadout, now.Spell);
        if (before == null) { _lastWorn = now; return false; }

        // A side that used the worn loadout follows it. Only an independently chosen loadout
        // stays fixed. BuildGear applies the side's own picks to that base and checks the stance and spell.
        var choices = _pendingEquipment ?? (_state.OnLeft, _state.Other, before.Loadout);
        var (left, other, wornBefore) = choices;
        string oldKey = _state.SetupKey;
        string oldOtherKey = _state.KeyOf(_state.Other);
        bool showedUpgrades = _state.ShowingUpgrades;
        if (other.BaseKey != "")
        {
            try
            {
                FollowSide(other, wornBefore, now.Loadout);
                _state.Other = _state.OnLeft;
            }
            catch
            {
                _state.PutOnLeft(left);
                throw;
            }
        }
        FollowSide(left, wornBefore, now.Loadout);
        var worn = SimGear.Live(_combat!.Equipment, _gearSlots, _ctx.Log).OwnItems();
        string mine = DescribeChoices(_state.OnLeft, now, worn);
        string theirs = _state.Compare ? DescribeChoices(_state.Other, now, worn) : "";
        _pendingEquipment = !fromFight && (mine != "" || theirs != "") ? choices : null;
        if (_chooseGear && _state.Compare)
        {
            string a = _state.ShowingB ? theirs : mine, b = _state.ShowingB ? mine : theirs;
            _keepDescription = $"A: {(a == "" ? "worn setup" : a)}\nB: {(b == "" ? "worn setup" : b)}";
        }
        else _keepDescription = mine;
        if ((_run != null || _state.Results.Count != 0 || _state.Upgrades != null) &&
            (oldKey != _state.SetupKey || (_state.Compare && oldOtherKey != _state.KeyOf(_state.Other))))
            _state.EquipmentNotice = EquipmentNotice(showedUpgrades);
        _lastWorn = now;
        if (fromFight && swap is { } done && _state.FightReport != "")
            _state.FightReportKey = SimView.ReportKey(_state, done.Target);
#if DEVTOOLS
        _ctx.Log($"Dev: worn setup changed from {before} to {now}. " +
            (_chooseGear ? "Waiting for the player's setup choice." : "Following the worn setup."));
#endif
        return true;
    }

    string DescribeChoices(SimSetup side, Worn now, IReadOnlyDictionary<string, string> worn)
    {
        var words = new List<string>();
        if (side.LoadoutId != now.Loadout)
            words.Add(_state.Loadouts.FirstOrDefault(l => l.Id == side.LoadoutId).Name ?? side.LoadoutId);
        var items = _game!.Data.GetDataRegistryOutput<Item>();
        string NameOf(string? id) => id == null ? "empty"
            : items.TryGetObjectById(id, out Item? item) ? item.DisplayName.GetPluralText(1) : id;
        foreach (var slot in _gearSlots)
        {
            string? before = worn.GetValueOrDefault(slot.Id);
            string? after = side.Shown?.GetValueOrDefault(slot.Id);
            if (before != after) words.Add($"{slot.DisplayName.Text}: {NameOf(before)} to {NameOf(after)}");
        }
        if (side.StanceId != "" && side.StanceId != now.Stance)
            words.Add(side.Stances.First(s => s.Id == side.StanceId).Name + " stance");
        if (side.SpellId != "" && side.SpellId != now.Spell)
            words.Add(_combat!.MagicSpells.ActiveSpells.Keys.FirstOrDefault(s => s.Id == side.SpellId)?.DisplayName.Text ?? side.SpellId);
        return string.Join("\n", words);
    }

    void FollowSide(SimSetup side, string before, string now)
    {
        _state.PutOnLeft(side);
        foreach (string slot in side.Displaced) _state.Overrides.Remove(slot);
        if (side.LoadoutId == before) _state.LoadoutId = now;
        BuildGear();
    }

    void ChooseGear(bool keep)
    {
        if (CheckWorn()) { RebuildVisible(); return; } // The player must see the choices this click answers.
        if (!_chooseGear) return;
        _pendingEquipment = null;
        if (!keep)
        {
            bool showedUpgrades = _state.ShowingUpgrades;
            _state.PutOnLeft(SimSetup.None);
            if (_state.Compare) _state.ForgetCompare();
            BuildGear();
            if (_run != null || _state.Results.Count != 0 || _state.Upgrades != null)
                _state.EquipmentNotice = EquipmentNotice(showedUpgrades);
        }
        RebuildVisible();
    }

    string EquipmentNotice(bool showedUpgrades) => showedUpgrades && !_state.ShowingUpgrades
        ? "The upgrade search used the previous setup."
        : "Setup changed. Earlier scans are in History.";

    void RebuildVisible()
    {
        Build();
        _window!.Visible = true;
    }

    bool ReadyForSetup(string? action = null)
    {
        bool changed = CheckWorn();
        if (_fightSwap != null)
        {
            _state.Message = WaitingForLoadout;
            _view?.Refresh();
            return false;
        }
        if (changed && _state.Message == LoadoutTimedOut)
        {
            RebuildVisible();
            return false;
        }
        if (changed && action != null)
            _state.Message = $"Setup was refreshed. Check the equipment before you press {action} again.";
        if (changed) RebuildVisible();
        return !_chooseGear && !(action != null && changed);
    }

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
        // What the setup on the left shows, kept with it when it leaves the left in compare mode (SimSetup.Shown).
        _state.ShownSlots = worn.OwnItems();
        _state.EquipmentCode = SimState.CodeOf(_state.ShownSlots);
        // Picking a shield can remove a two-handed weapon. That removal was a consequence of the
        // pick, not a request to empty the Weapon slot if a one-handed sword is worn later.
        _state.Displaced = Array.Empty<string>();
        if (overrides.ContainsValue("") && overrides.Values.Any(id => id != ""))
        {
            var picks = overrides.Where(o => o.Value != "").ToDictionary();
            var withPicks = SimGear.Resolve(loadout, picks, _gearSlots,
                id => items.TryGetObjectById(id, out Item? item) ? item : null, _ctx.Log);
            _state.Displaced = overrides.Where(o => o.Value == "" && withPicks[o.Key] is not { FilledFrom: null })
                .Select(o => o.Key).ToArray();
        }
#if DEVTOOLS
        _state.DevSlots = DevGearText();
#endif

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
        DevLogGear(_devGearFor ?? "the slots on the left were made", saved);
#endif
        return wielded;
    }

    void SetOverrides(Dictionary<string, string> overrides)
    {
        _state.Overrides.Clear();
        foreach (var (slot, item) in overrides) _state.Overrides[slot] = item;
    }

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
        _ctx.Log($"Dev: gear: what the character wears now: {SimGear.Describe(SimGear.Live(_combat!.Equipment, _gearSlots, _ctx.Log), _gearSlots)}; " +
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
        string wearing = SimGear.Describe(SimGear.Live(_combat!.Equipment, _gearSlots, _ctx.Log), _gearSlots);
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
    //                        as one of "simulate" is (DevSimulate). In compare mode it is the setup on the left that
    //                        gets the change; the other one is simulated as it is
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

    // For a raw= scan, the options of the setup on the left get its one change instead of the player's. Each setup's
    // options already have the slots it shows, for SimEngine.DevGear (RunOptions, SimSetup.DevSlots).
    void DevSimulateOptions(SimOptions options)
    {
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
        if (grid is not { } rect && views.Count > 0) _ctx.Log($"Dev: gear: crop-{name}-game.png not taken: none of the game's {views.Count} slot views is on show. Open the combat screen for it.");
        // The game's grid from the very frame of the picture (issue #59: it was the frame before).
        DevShot(name, grid is { } at ? (image, screen) => DevGameCrop(image, screen, name, at) : null);
    }

    // crop-<name>-game.png: the game's equipment grid alone, with room around it for its tooltip, from the picture of shot-<name>.
    void DevGameCrop(Image image, Transform2D screen, string name, Rect2 rect, float room = 12)
    {
        try
        {
            Rect2 frame = screen * rect.Grow(room);
            var whole = new Rect2I(0, 0, image.GetWidth(), image.GetHeight());
            Rect2I part = new Rect2I((int)frame.Position.X, (int)frame.Position.Y, (int)frame.Size.X, (int)frame.Size.Y).Intersection(whole);
            if (part.Size.X <= 0 || part.Size.Y <= 0) { _ctx.Log($"Dev: gear: crop-{name}-game.png not taken: the grid is off the screen."); return; }
            using Image crop = image.GetRegion(part);
            crop.SavePng(System.IO.Path.Combine(_ctx.Directory, $"crop-{name}-game.png"));
            bool covered = _window is { IsAlive: true, Visible: true } && _window.Root.GetGlobalRect().Intersects(rect);
            _ctx.Log($"Dev: gear: crop-{name}-game.png is the game's equipment, {part.Position.X},{part.Position.Y} {part.Size.X}x{part.Size.Y}, from the frame of shot-{name}.png" +
                     (covered ? "; THE SIMULATOR'S WINDOW LIES OVER IT: close the window first (\"close\")." : "; nothing of the simulator lies over it."));
        }
        catch (Exception e) { _ctx.Log($"Dev: gear: crop-{name}-game.png could not be taken: {e.Message}"); }
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
            DevShot("hover", (image, screen) => DevGameCrop(image, screen, "hover", area));
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
