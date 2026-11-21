using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GamesByMalcs.Melvor2;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Components.Equipment;
using GamesByMalcs.Melvor2.Components.Loadouts;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Combat;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace CombatSim;

/// <summary>
/// A combat simulator. It does not re-implement the game's combat: it loads a copy of your character into a
/// second, invisible game and runs the game's own combat on it, in the background, then shows what an hour
/// of each fight gives (kills, XP, gold, loot), uses (food, ammo, runes) and risks (deaths, lowest health).
/// Nothing the copy does is saved. A round button on the right edge of the screen opens it.
/// </summary>
public class CombatSimMod : IMod, ISimLookup
{
    sealed class Settings
    {
        public float? X { get; set; }
        public float? Y { get; set; }
        public float Scale { get; set; } = 1f;
        public string Scope { get; set; } = "";
        public int Duration { get; set; } = 1;
        public bool Unlimited { get; set; } = true;
    }

    // What each saved loadout holds is kept inside the equipment component; only the one that is worn right
    // now can be read from the slots themselves.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Loadouts")]
    static extern Dictionary<LoadoutSlot, EquipmentLoadout> SavedLoadouts(EquipmentComponent equipment);

    ModContext _ctx = null!;
    Settings _settings = new();
    SimEngine _engine = null!;
    readonly SimState _state = new();
    Game? _game;
    CombatEntity? _combat;
    BankEntity? _bank;
    GameWindow? _window;
    SimView? _view;
    Button? _stripButton;
    readonly Dictionary<string, Texture2D?> _targetIcons = new();
    int _frame;
    bool _stripMissingLogged, _wasRunning;

    public void OnLoad(ModContext ctx)
    {
        _ctx = ctx;
        _settings = ReadSettings();
        _state.ScopeAreaId = _settings.Scope;
        _state.Duration = _settings.Duration;
        _state.Unlimited = _settings.Unlimited;
        _engine = new SimEngine(ctx.Log);
        ctx.Frame += OnFrame;
        ctx.Log("Ready. The round button on the right edge of the screen opens the simulator.");
    }

    void OnFrame()
    {
        if (++_frame % 10 != 0) return;
        try
        {
            TrackCharacter();
            if (_game == null) return;
            if (_frame % 60 == 0) UpdateStripButton();
            Pump();
#if DEBUG
            DevRequests();
#endif
        }
        catch (Exception e)
        {
            _ctx.Log("Error: " + e);
        }
    }

    // CurrentGame is null on the main menu and a new object for every character that is opened.
    void TrackCharacter()
    {
        Game? now = GameAccess.CurrentGame;
        if (now is not { IsSaveLoaded: true }) now = null;
        if (ReferenceEquals(now, _game)) return;
        _engine.Cancel();
        _game = now;
        _combat = null;
        _bank = null;
        _stripButton = null;
        _window?.Free();
        _window = null;
        _view = null;
        _state.Results.Clear();
        _state.Catalog.Clear();
        _state.Selected = null;
        _state.Done = _state.Total = 0;
        if (now == null) return;
        _combat = now.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? c) ? c as CombatEntity : null;
        _bank = now.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? b) ? b as BankEntity : null;
    }

    // ---- the catalogue: what can be fought ----------------------------------------------------------------

    void BuildCatalog()
    {
        _state.Catalog.Clear();
        _state.Scopes.Clear();
        _targetIcons.Clear();
        if (_combat == null) return;
        var scopes = new List<(string, string)>();
        foreach (ActiveCombatAreaCategory category in _combat.Areas.ActiveCategories.Values)
        {
            string categoryName = category.Category.DisplayName.Text;
            foreach (ActiveCombatArea area in category.Areas)
            {
                if (!area.UnlockRequirements.AreMet) continue;
                string areaName = area.DisplayName.Text, areaId = area.Area.Id;
                int before = _state.Catalog.Count;
                if (area.Area is CombatArea fights)
                {
                    foreach (Enemy enemy in fights.Enemies)
                    {
                        var target = new SimTarget(areaId, enemy.Id, enemy.DisplayName.Text, areaName, categoryName, enemy.CombatLevel);
                        if (_targetIcons.ContainsKey(target.Key)) continue;
                        _state.Catalog.Add(target);
                        _targetIcons[target.Key] = ((IDescribedObject)enemy).Icon;
                    }
                }
                else if (area.Area is Dungeon)
                {
                    var target = new SimTarget(areaId, null, areaName, areaName, categoryName, 0);
                    if (_targetIcons.ContainsKey(target.Key)) continue;
                    _state.Catalog.Add(target);
                    _targetIcons[target.Key] = area.Icon;
                }
                if (_state.Catalog.Count > before) scopes.Add(($"{categoryName}: {areaName}", areaId));
            }
        }
        _state.Scopes.Add(($"Every unlocked area ({_state.Catalog.Count} fights)", ""));
        _state.Scopes.AddRange(scopes);
        if (_state.Scopes.All(s => s.AreaId != _state.ScopeAreaId)) _state.ScopeAreaId = "";

        BuildGear();

        var food = _combat.Food.SelectedFood;
        _state.FoodMissing = food == null;
        _state.FoodLine = food == null ? "No food selected: you will not heal."
            : $"Food: {food.Item.DisplayName.GetPluralText(2)} x{_bank?.Inventory.GetQuantity(food.Item) ?? 0:N0}";
    }

    // The loadouts to choose from, and the slots of the chosen one with the player's changes on top.
    void BuildGear()
    {
        _state.Loadouts.Clear();
        _state.Gear.Clear();
        if (_combat == null || _game == null) return;
        string? worn = _combat.Loadouts.CurrentLoadout?.Data.Id;
        foreach (ActiveLoadoutSlot loadout in _combat.Loadouts.LoadoutDisplayOrder)
        {
            if (!loadout.IsUnlocked) continue;
            string name = string.IsNullOrWhiteSpace(loadout.Name) ? loadout.Data.DisplayName.Text : loadout.Name;
            _state.Loadouts.Add((loadout.Data.Id, loadout.Data.Id == worn ? name + " (worn now)" : name));
        }
        if (_state.Loadouts.All(l => l.Id != _state.LoadoutId))
        {
            _state.LoadoutId = worn ?? _state.Loadouts.FirstOrDefault().Id ?? "";
            _state.Overrides.Clear();
        }

        // Another loadout's items come from what the game stored when it was last worn.
        EquipmentLoadout? saved = null;
        if (_state.LoadoutId != worn)
        {
            try
            {
                LoadoutSlot? key = _combat.Loadouts.ActiveLoadouts.Keys.FirstOrDefault(l => l.Id == _state.LoadoutId);
                if (key != null) SavedLoadouts(_combat.Equipment).TryGetValue(key, out saved);
            }
            catch (MissingMethodException) { _ctx.Log("Saved loadouts cannot be read in this game version: showing what is worn now."); }
        }

        var items = _game.Data.GetDataRegistryOutput<Item>();
        foreach (EquipmentSlot slot in _game.Data.GetDataRegistryOutput<EquipmentSlot>().GetAllObjects())
        {
            if (!_combat.Equipment.TryGetActiveSlot(slot, out IActiveEquipmentSlot? active) || active == null) continue;
            Item? item = active.Equipped?.Item;
            int quantity = active.Quantity;
            if (saved != null)
            {
                item = null; quantity = 0;
                if (saved.Slots.TryGetValue(slot, out EquipmentSlotLoadout? stored)) { item = stored.Equipped?.Item; quantity = stored.Quantity; }
            }
            bool changed = _state.Overrides.TryGetValue(slot.Id, out string? other);
            if (changed)
            {
                item = other != "" && items.TryGetObjectById(other!, out Item? swapped) ? swapped : null;
                quantity = 0;
            }
            _state.Gear.Add(new GearSlot(slot.Id, slot.DisplayName.Text, active.GridPosition.X, active.GridPosition.Y,
                item?.Id, item?.DisplayName.GetPluralText(1) ?? "", quantity, item?.Icon, slot.FitsType?.Icon, changed));
        }
    }

    List<GearChoice> ISimLookup.ChoicesFor(GearSlot slot)
    {
        var choices = new List<GearChoice>();
        if (_game == null || _combat == null) return choices;
        if (!_game.Data.GetDataRegistryOutput<EquipmentSlot>().TryGetObjectById(slot.SlotId, out EquipmentSlot? target)) return choices;
        foreach (Item item in _game.Data.GetDataRegistryOutput<Item>().GetAllObjects())
        {
            if (!item.Capabilities.TryGet(out EquipmentCapability? wearable) || wearable.Type?.Id != target.FitsType?.Id) continue;
            int owned = (_bank?.Inventory.GetQuantity(item) ?? 0) + _combat.Equipment.GetEquippedQuantity(item);
            choices.Add(new GearChoice(item.Id, item.DisplayName.GetPluralText(1), item.Icon, owned, wearable.Requirements?.AreMet ?? true));
        }
        return choices;
    }

    Texture2D? ISimLookup.TargetIcon(SimTarget target) => _targetIcons.GetValueOrDefault(target.Key);

    Texture2D? ISimLookup.AmountIcon(Amount amount)
    {
        if (_game == null) return null;
        if (amount.IsCurrency)
            return _game.Data.GetDataRegistryOutput<Currency>().TryGetObjectById(amount.Id, out Currency? currency) ? currency.Icon : null;
        return _game.Data.GetDataRegistryOutput<Item>().TryGetObjectById(amount.Id, out Item? item) ? item.Icon : null;
    }

    (string Name, Texture2D? Icon) ISimLookup.Skill(string id)
    {
        string local = id[(id.IndexOf(':') + 1)..];
        var skill = _game?.LeveledEntities.FirstOrDefault(s => s.Id == id);
        return (skill?.DisplayName.Text ?? local, GameStyle.Icon(local));
    }

    // ---- the window ---------------------------------------------------------------------------------------

    void Open()
    {
        if (_game == null) return;
        if (!_engine.Running) BuildCatalog();   // unlocks, food and levels may have changed since last time
        if (_window is not { IsAlive: true }) Build();
        _window!.Visible = true;
        _view!.Refresh();
        SyncStripButton();
    }

    void Close()
    {
        if (_window is { IsAlive: true }) _window.Visible = false;
        SyncStripButton();
    }

    void Build()
    {
        _window?.Free();
        Vector2? at = _settings is { X: { } x, Y: { } y } ? new Vector2(x, y) : null;
        _window = new GameWindow(_ctx.Tree, "Combat Simulator", _settings.Scale, at);
        _window.CloseRequested += Close;
        _window.Moved += position => { _settings.X = position.X; _settings.Y = position.Y; WriteSettings(); };
        _window.ResizeRequested += scale =>
        {
            // The same window again at another size; what was simulated so far is kept.
            _settings.Scale = scale;
            _settings.X = _window!.Position.X;
            _settings.Y = _window.Position.Y;
            WriteSettings();
            Build();
            _window!.Visible = true;
        };
        _view = new SimView(_window, _state, this, Simulate, _engine.Cancel, SaveChoices, BuildGear);
    }

    void SaveChoices()
    {
        _settings.Scope = _state.ScopeAreaId;
        _settings.Duration = _state.Duration;
        _settings.Unlimited = _state.Unlimited;
        WriteSettings();
    }

    void Simulate()
    {
        if (_game == null) return;
        var targets = _state.InScope.ToList();
        var options = new SimOptions
        {
            Hours = SimState.Durations[Math.Clamp(_state.Duration, 0, SimState.Durations.Length - 1)].Hours,
            UnlimitedSupplies = _state.Unlimited,
            LoadoutId = _state.LoadoutId == "" ? null : _state.LoadoutId,
            Overrides = new Dictionary<string, string>(_state.Overrides),
            SetupKey = _state.SetupKey,
            SetupLabel = _state.SetupLabel,
        };
        _state.Message = "";
        if (!_engine.Start(_game, targets, options, out string error)) { _state.Message = error; _ctx.Log(error); }
        else
        {
            // Old numbers for these fights would sit next to new ones until they are replaced.
            foreach (SimTarget target in targets) _state.Results.Remove(SimResult.MakeKey(options.SetupKey, target.Key));
            _ctx.Log($"Simulating {targets.Count} fights, {options.Hours:0.##} h each, supplies {(options.UnlimitedSupplies ? "unlimited" : "from bank")}, with {options.SetupLabel}.");
        }
        Pump(force: true);
    }

    // Results come from the background thread; the window is only touched here, on the game's thread.
    void Pump(bool force = false)
    {
        bool changed = force;
        while (_engine.Finished.TryDequeue(out SimResult? result))
        {
            _state.Results[result.Key] = result;
            changed = true;
        }
        bool running = _engine.Running;
        if (running != _state.Running || _state.Done != _engine.Done || _state.Current != _engine.Current) changed = true;
        _state.Running = running;
        _state.Done = _engine.Done;
        _state.Total = _engine.Total;
        _state.Current = _engine.Current;
        if (_wasRunning && !running)
            _ctx.Log($"Simulation finished: {_state.Done} of {_state.Total} fights.");
        _wasRunning = running;
        if (changed && _window is { IsAlive: true, Visible: true }) _view?.Refresh();
    }

    // ---- the button in the game's strip of round buttons on the right edge -------------------------------

    void UpdateStripButton()
    {
        if (_stripButton != null && GodotObject.IsInstanceValid(_stripButton)) { SyncStripButton(); return; }
        _stripButton = null;
        if (GameAccess.FindGameView()?.FindChild("FavouritesContainer", true, false) is not BoxContainer strip)
        {
            if (!_stripMissingLogged) _ctx.Log("The game's widget strip was not found (the game was updated?).");
            _stripMissingLogged = true;
            return;
        }
        _stripButton = new Button
        {
            Name = "CombatSimButton",
            ThemeTypeVariation = "SidebarCategoryButton",
            CustomMinimumSize = new Vector2(36, 36),
            Icon = GameStyle.Icon("Statistics") ?? GameStyle.Icon("AttacksIcon") ?? GameStyle.Icon("Slayer"),
            ExpandIcon = true,
            IconAlignment = HorizontalAlignment.Center,
            ToggleMode = true,
            TooltipText = "Combat Simulator: what an hour of each fight gives, uses and risks",
        };
        _stripButton.Toggled += open =>
        {
            bool isOpen = _window is { IsAlive: true, Visible: true };
            if (open && !isOpen) Open();
            else if (!open && isOpen) Close();
        };
        strip.AddChild(_stripButton);
        // The style's own "pressed" look is a wider circle that is cut off at this size: keep the resting shape.
        if (_stripButton.GetThemeStylebox("normal") is StyleBoxFlat resting)
        {
            var open = (StyleBoxFlat)resting.Duplicate();
            open.BgColor = new Color("275124");
            _stripButton.AddThemeStyleboxOverride("pressed", open);
            _stripButton.AddThemeStyleboxOverride("hover_pressed", open);
        }
        SyncStripButton();
    }

    void SyncStripButton()
    {
        if (_stripButton != null && GodotObject.IsInstanceValid(_stripButton))
            _stripButton.SetPressedNoSignal(_window is { IsAlive: true, Visible: true });
    }

    // ---- settings.json in the mod's folder ----------------------------------------------------------------

    string SettingsFile => Path.Combine(_ctx.Directory, "settings.json");

    Settings ReadSettings()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile)) ?? new(); }
        catch { return new(); }   // no file yet, or a broken one
    }

    void WriteSettings()
    {
        try { File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) { _ctx.Log("Could not save settings: " + e.Message); }
    }

#if DEBUG
    // Development: a file "sim.request" opens the window and starts a run; when it is done, a picture of the
    // game goes into the mod's folder and the table into the log. The file holds
    //   <part of an area's name, or "all">|<duration index>|<1 unlimited, 0 from bank>|<row to select>
    int _devShotAt = -1;
    string? _devSelect, _devPicker;

    void DevRequests()
    {
        string request = Path.Combine(_ctx.Directory, "sim.request");
        if (File.Exists(request))
        {
            string[] parts = File.ReadAllText(request).Trim().Split('|');
            File.Delete(request);
            Open();
            string where = parts[0].Trim();
            var scope = _state.Scopes.FirstOrDefault(s => s.AreaId != "" && s.Label.Contains(where, StringComparison.OrdinalIgnoreCase));
            _state.ScopeAreaId = where.Equals("all", StringComparison.OrdinalIgnoreCase) || scope.AreaId == null ? "" : scope.AreaId;
            if (parts.Length > 1 && int.TryParse(parts[1], out int duration)) _state.Duration = duration;
            if (parts.Length > 2) _state.Unlimited = parts[2].Trim() != "0";
            // ...|<part of a loadout name>|<slot whose picker to open>|<slot=item id to swap in>
            if (parts.Length > 4 && parts[4].Trim() != "" && _state.Loadouts.FirstOrDefault(l => l.Name.Contains(parts[4].Trim(), StringComparison.OrdinalIgnoreCase)) is { Id: not null } loadout)
            { _state.LoadoutId = loadout.Id; _state.Overrides.Clear(); }
            if (parts.Length > 6 && parts[6].Contains('='))
            {
                string[] swap = parts[6].Split('=');
                GearSlot? slot = _state.Gear.FirstOrDefault(g => g.SlotName.Contains(swap[0].Trim(), StringComparison.OrdinalIgnoreCase));
                if (slot != null) _state.Overrides[slot.SlotId] = swap[1].Trim();
            }
            BuildGear();
            _devPicker = parts.Length > 5 ? parts[5].Trim() : null;
            _ctx.Log("Dev: loadouts = " + string.Join(" / ", _state.Loadouts.Select(l => l.Name)) + "; gear = " +
                     string.Join(", ", _state.Gear.Select(g => $"{g.SlotName}:{(g.ItemId == null ? "-" : g.ItemName)}{(g.Changed ? "*" : "")}")));
            Build();
            _window!.Visible = true;
            Simulate();
            _devSelect = parts.Length > 3 ? parts[3].Trim() : null;
            _devShotAt = -2;   // armed
            _ctx.Log("Dev: scopes = " + string.Join(" / ", _state.Scopes.Select(s => s.Label)));
        }
        if (_devShotAt == -2 && !_engine.Running && _state.Done > 0)
        {
            if (_devSelect != null && _state.Results.Values.FirstOrDefault(r => r.Target.Name.Contains(_devSelect, StringComparison.OrdinalIgnoreCase)) is { } pick)
            { _state.Selected = pick.Target.Key; Build(); _window!.Visible = true; }
            if (!string.IsNullOrEmpty(_devPicker)) _view?.DevOpenPicker(_devPicker);
            _devShotAt = _frame + 90;
        }
        if (_devShotAt > 0 && _frame >= _devShotAt)
        {
            _devShotAt = -1;
            _ctx.Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(_ctx.Directory, "shot-sim.png"));
            _ctx.Log("Dev: saved shot-sim.png");
            foreach (SimResult r in _state.Results.Values.OrderByDescending(r => r.XpPerHour))
                _ctx.Log($"Dev: [{r.SetupLabel}] {r.Target.Name,-22} kills {r.KillsPerHour,7:N0}  xp {r.XpPerHour,8:N0}  gold {r.GoldPerHour,8:N0}  food {r.FoodPerHour,6:N1}  deaths {r.DeathsPerHour,5:N1}  " +
                         $"lowHP {r.LowestHpFraction,4:P0}  {r.RealMilliseconds,5:N0} ms  {r.Note}");
        }
    }
#endif
}
