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
using GamesByMalcs.Melvor2.Components.MagicSpells;
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
        /// <summary>Before areas could be ticked: the one area that was chosen.</summary>
        public string Scope { get; set; } = "";
        public List<string> Areas { get; set; } = new();
        public List<string> BestAreas { get; set; } = new();
        /// <summary>Fights simulated side by side, 1 to 8; 0 lets the mod choose (half of the processor, four at most).</summary>
        public int Workers { get; set; }
        public int Duration { get; set; } = 1;
        public bool Unlimited { get; set; } = true;
        public bool BestMode { get; set; }
        public string TrainSkill { get; set; } = "";
    }

    // What each saved loadout holds is kept inside the equipment component; only the one that is worn right
    // now can be read from the slots themselves.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Loadouts")]
    static extern Dictionary<LoadoutSlot, EquipmentLoadout> SavedLoadouts(EquipmentComponent equipment);

    ModContext _ctx = null!;
    Settings _settings = new();
    SimEngine _engine = null!;
    SimHistory _history = null!;
    bool _historyRead;
    /// <summary>The run that is under way: what it was asked, and the keys of the results it will bring.</summary>
    Scan? _run;
    List<string> _runKeys = new();
    readonly System.Diagnostics.Stopwatch _runClock = new();
    readonly Dictionary<string, Texture2D?> _areaIcons = new();
    readonly SimState _state = new();
    Game? _game;
    CombatEntity? _combat;
    BankEntity? _bank;
    GameWindow? _window;
    SimView? _view;
    Button? _stripButton;
    readonly Dictionary<string, Texture2D?> _targetIcons = new();
    int _frame;
    bool _stripMissingLogged, _wasRunning, _loadoutsUnreadable;
    readonly HashSet<string> _reported = new();

    public void OnLoad(ModContext ctx)
    {
        _ctx = ctx;
        _settings = ReadSettings();
        if (_settings.Scope != "") { _settings.Areas = new List<string> { _settings.Scope }; _settings.Scope = ""; }
        _state.FightAreas = _settings.Areas.ToHashSet();
        _state.BestAreas = _settings.BestAreas.ToHashSet();
        _state.Duration = _settings.Duration;
        _state.Unlimited = _settings.Unlimited;
        _state.BestMode = _settings.BestMode;
        _state.TrainSkillId = _settings.TrainSkill;
        _engine = new SimEngine(ctx.Log);
        _state.Workers = _settings.Workers > 0 ? Math.Min(_settings.Workers, 8) : SimEngine.DefaultWorkers;
        _history = new SimHistory(ctx.Directory, ctx.Log);
        _state.History = _history.Scans;
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
#if DEVTOOLS
            DevRequests();
#endif
        }
        catch (Exception e)
        {
            // Whatever keeps going wrong would otherwise be written six times a second.
            if (_reported.Add(e.GetType().Name + e.Message)) _ctx.Log("Error: " + e);
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
        _state.Viewing = null;
        _state.Done = _state.Total = 0;
        _state.Overrides.Clear();
        _state.LoadoutId = _state.WornLoadoutId = _state.SpellId = _state.Message = "";
        _history.Close();
        _historyRead = false;
        _run = null;
        if (now == null) return;
        _combat = now.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? c) ? c as CombatEntity : null;
        _bank = now.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? b) ? b as BankEntity : null;
    }

    // ---- the catalogue: what can be fought ----------------------------------------------------------------

    void BuildCatalog()
    {
        _state.Catalog.Clear();
        _state.Areas.Clear();
        _targetIcons.Clear();
        _areaIcons.Clear();
        if (_combat == null) return;
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
                if (_state.Catalog.Count == before || _areaIcons.ContainsKey(areaId)) continue;
                var added = _state.Catalog.Skip(before).ToList();
                bool whole = area.Area is Dungeon;
                _state.Areas.Add(new AreaChoice(areaId, areaName, categoryName, added.Count, whole,
                    whole ? 0 : added.Min(t => t.CombatLevel), whole ? 0 : added.Max(t => t.CombatLevel)));
                // An open area has no picture of its own: its strongest enemy stands for it.
                _areaIcons[areaId] = whole || area.Area is not CombatArea ? area.Icon
                    : _targetIcons.GetValueOrDefault(added.OrderByDescending(t => t.CombatLevel).First().Key) ?? area.Icon;
            }
        }
        // An area that is gone (a game update) must not stay ticked where it cannot be seen.
        _state.FightAreas.RemoveWhere(id => !_areaIcons.ContainsKey(id));
        _state.BestAreas.RemoveWhere(id => !_areaIcons.ContainsKey(id));

        BuildGear();
    }

    // The loadouts to choose from, and the slots of the chosen one with the player's changes on top.
    void BuildGear()
    {
        _state.Loadouts.Clear();
        _state.Gear.Clear();
        if (_combat == null || _game == null) return;
        string? worn = _combat.Loadouts.CurrentLoadout?.Data.Id;
        _state.WornLoadoutId = worn ?? "";
        foreach (ActiveLoadoutSlot loadout in _combat.Loadouts.LoadoutDisplayOrder)
        {
            // What another loadout holds could not be read: only the worn one can be shown truthfully.
            if (!loadout.IsUnlocked || (_loadoutsUnreadable && loadout.Data.Id != worn)) continue;
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
            catch (MissingMethodException)
            {
                _ctx.Log("Saved loadouts cannot be read in this game version: only the worn one is offered.");
                _loadoutsUnreadable = true;
                _state.Loadouts.RemoveAll(l => l.Id != worn);
                _state.LoadoutId = worn ?? "";
                _state.Overrides.Clear();
            }
        }

        var items = _game.Data.GetDataRegistryOutput<Item>();
        WeaponType? weapon = null;
        WeaponCapability? wielded = null;
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
            if (item != null && item.Capabilities.TryGet(out WeaponCapability? held)) { weapon = held.Type; wielded = held; }
        }

        // The game keeps a food choice per loadout; only the worn one's can be read.
        var food = _combat.Food.SelectedFood;
        bool wornNow = _state.LoadoutId == worn;
        _state.FoodMissing = wornNow && food == null;
        _state.FoodLine = !wornNow ? "Food: as that loadout has it"
            : food == null ? "No food selected: you will not heal."
            : $"Food: {food.Item.DisplayName.GetPluralText(2)} x{_bank?.Inventory.GetQuantity(food.Item) ?? 0:N0}";
        // For "Your food lasts" in the details: the food of the worn loadout is the only one that can be read.
        _state.FoodInBank = food != null && _bank != null ? _bank.Inventory.GetQuantity(food.Item) : -1;
        _state.FoodName = food?.Item.DisplayName.GetPluralText(2) ?? "";
        _state.FoodLoadoutId = worn ?? "";

        // The attack stances belong to the weapon of this setup; bare hands have their own.
        weapon ??= _combat.Equipment.DefaultWeapon?.Weapon?.Type;
        _state.Stances.Clear();
        if (weapon != null) _state.Stances.AddRange(weapon.Stances.Select(s => (s.Id, s.DisplayName.Text)));

        // So do the spells: a weapon casts those of its elements, of the books that are unlocked.
        _state.Spells.Clear();
        try
        {
            if (wielded is { Elements.Count: > 0 })
                foreach (ActiveSpellBook book in _combat.MagicSpells.ActiveSpellBooks.Values)
                {
                    if (book.Data.Requirements is { AreMet: false }) continue;
                    foreach (ActiveMagicSpell spell in book.Spells)
                        if (wielded.Elements.Contains(spell.Element) && spell.UnlockRequirements is not { AreMet: false })
                            _state.Spells.Add(new SpellChoice(spell.Data.Id, spell.DisplayName.Text, book.DisplayName.Text, spell.Icon));
                }
        }
        catch (Exception e) { _ctx.Log("The spells could not be listed (the game was updated?): " + e.Message); }
        if (_state.Spells.All(s => s.Id != _state.SpellId)) _state.SpellId = "";
        string? casting = _state.LoadoutId == worn && _state.Overrides.Count == 0 ? _combat.MagicSpells.SelectedSpell?.DisplayName.Text : null;
        _state.SpellDefault = casting != null ? $"As now: {casting}" : "As the loadout has it";

        _state.Skills.Clear();
        foreach (string id in SimEngine.CombatSkills)
            if (_game.LeveledEntities.FirstOrDefault(s => s.Id == id) is { } skill) _state.Skills.Add((id, skill.DisplayName.Text));
        if (_state.Skills.All(k => k.Id != _state.TrainSkillId)) _state.TrainSkillId = "";
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

    Texture2D? ISimLookup.TargetIcon(SimTarget target) => _targetIcons.GetValueOrDefault(target.FightKey);

    Texture2D? ISimLookup.AreaIcon(string areaId) => _areaIcons.GetValueOrDefault(areaId);

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
        // Unlocks, equipment, food and levels may have changed since last time: the window is built again
        // from them. During a run it is left alone.
        bool refreshed = !_engine.Running;
        if (refreshed) BuildCatalog();
        if (!_historyRead)
        {
            _historyRead = true;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            _history.Open(_game.CharacterName);
            if (_history.Scans.Count > 0) _ctx.Log($"{_history.Scans.Count} earlier scans read in {clock.ElapsedMilliseconds} ms.");
        }
        if (refreshed || _window is not { IsAlive: true }) Build();
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
        // The game's bar along the bottom stays in sight: the window is placed, and kept, above it.
        float footer = FooterHeight();
        _window = new GameWindow(_ctx.Tree, "Combat Simulator", Math.Min(_settings.Scale, LargestScale(footer)), at, bottomInset: footer);
        _window.CloseRequested += Close;
        _window.Moved += position => { _settings.X = position.X; _settings.Y = position.Y; WriteSettings(); };
        _window.ResizeRequested += scale =>
        {
            // The same window again at another size; what was simulated so far is kept.
            _settings.Scale = Math.Min(scale, LargestScale(FooterHeight()));
            _settings.X = _window!.Position.X;
            _settings.Y = _window.Position.Y;
            WriteSettings();
            Build();
            _window!.Visible = true;
        };
        _view = new SimView(_window, _state, this, Simulate, _engine.Cancel, SaveChoices, BuildGear, () => { Build(); _window!.Visible = true; }, Fight, _history.Clear);
    }

    // How high the game's bar along the bottom of the screen is (levels, rates, the music button).
    float FooterHeight()
    {
        if (GameAccess.FindGameView()?.FindChild("Footer", true, false) is Control footer)
            return footer.IsVisibleInTree() ? footer.GetGlobalRect().Size.Y : 0;

        const string missing = "The game's bottom bar was not found (the game was updated?): the simulator's window may lie over it.";
        if (_reported.Add(missing)) _ctx.Log(missing);
        return 0;
    }

    // At full size the window is at most 1408 wide (Best for, with a stance on every line) and 889 high (Best
    // for, with the Train and the Spell box); at other sizes a few pixels more or less than that in proportion,
    // every length being rounded on its own. It is never larger than the screen above the game's bottom bar,
    // whatever size was chosen on another screen or dragged to: the largest size that fits, in the same steps
    // of 5% the corner resizes in.
    const float FullWidth = 1414, FullHeight = 895;

    float LargestScale(float footer)
    {
        Vector2 screen = _ctx.Tree.Root.GetVisibleRect().Size;
        float fits = Math.Min(screen.X / FullWidth, (screen.Y - footer) / FullHeight);
        return Math.Clamp(MathF.Floor(fits * 20f) / 20f, GameWindow.MinScale, GameWindow.MaxScale);
    }

    void SaveChoices()
    {
        _settings.Areas = _state.FightAreas.OrderBy(a => a, StringComparer.Ordinal).ToList();
        _settings.BestAreas = _state.BestAreas.OrderBy(a => a, StringComparer.Ordinal).ToList();
        _settings.Duration = _state.Duration;
        _settings.Unlimited = _state.Unlimited;
        _settings.BestMode = _state.BestMode;
        _settings.TrainSkill = _state.TrainSkillId;
        WriteSettings();
    }

    void Simulate()
    {
        if (_game == null) return;
        var targets = _state.ToSimulate();
        var options = new SimOptions
        {
            Hours = SimState.Durations[Math.Clamp(_state.Duration, 0, SimState.Durations.Length - 1)].Hours,
            UnlimitedSupplies = _state.Unlimited,
            LoadoutId = _state.LoadoutId == "" ? null : _state.LoadoutId,
            Overrides = new Dictionary<string, string>(_state.Overrides),
            SpellId = _state.SpellId == "" ? null : _state.SpellId,
            SetupKey = _state.SetupKey,
            SetupLabel = _state.SetupLabel,
        };
        _state.Message = "";
        _state.Viewing = null;
        if (!_engine.Start(_game, targets, options, _state.Workers, out string error)) { _state.Message = error; _ctx.Log(error); }
        else
        {
            // Old numbers for these fights would sit next to new ones until they are replaced.
            foreach (SimTarget target in targets) _state.Results.Remove(SimResult.MakeKey(options.SetupKey, target.Key));
            _runKeys = targets.Select(t => SimResult.MakeKey(options.SetupKey, t.Key)).ToList();
            _run = new Scan
            {
                Id = DateTime.UtcNow.Ticks.ToString(),
                BestMode = _state.BestMode,
                TrainSkillId = _state.BestMode ? _state.TrainSkillId : "",
                TrainSkillName = _state.BestMode ? _state.Skills.FirstOrDefault(k => k.Id == _state.TrainSkillId).Name ?? "" : "",
                Where = _state.ScopeLabel,
                Hours = options.Hours,
                Unlimited = options.UnlimitedSupplies,
                SetupKey = options.SetupKey,
                SetupLabel = options.SetupLabel,
                Asked = targets.Count,
            };
            _ctx.Log($"Simulating {targets.Count} fights, {options.Hours:0.##} h each, supplies {(options.UnlimitedSupplies ? "unlimited" : "from bank")}, with {options.SetupLabel}, {Math.Min(_state.Workers, targets.Count)} at a time.");
            _runClock.Restart();
        }
        Pump(force: true);
    }

    // The real thing: start the selected fight in the game, the way its own area menu does. Equipment is
    // left as it is; the stance the result was simulated with is selected when the weapon has it.
    void Fight(SimTarget target)
    {
        if (_combat == null) return;
        try
        {
            var (area, active) = _combat.Areas.ActiveAreas.FirstOrDefault(a => a.Key.Id == target.AreaId);
            // An earlier scan may be of another character with the same name: only what this one has unlocked.
            if (area == null || active is not { UnlockRequirements.AreMet: true }) { _state.Message = "That area is not available."; _view?.Refresh(); return; }

            if (target.StanceId != null)
            {
                WeaponType? weapon = (_combat.Equipment.CurrentWeapon ?? _combat.Equipment.DefaultWeapon)?.Weapon?.Type;
                AttackStance? stance = weapon?.Stances.FirstOrDefault(s => s.Id == target.StanceId);
                if (weapon != null && stance != null) _combat.Player.SetSelectedStance(weapon, stance);
            }

            // The spell that was simulated, when the equipment is the one that is worn.
            if (_state.Viewing == null && _state.SpellId != "" && _state.IsWornSetup &&
                _combat.MagicSpells.ActiveSpells.Keys.FirstOrDefault(s => s.Id == _state.SpellId) is { } spell)
                _combat.MagicSpells.SelectSpell(spell);

            // Selecting what is already being fought would stop it: the game's button is a switch.
            bool fighting = _combat.Action.IsStarted;
            if (target.EnemyId != null && area is CombatArea open && open.Enemies.FirstOrDefault(e => e.Id == target.EnemyId) is { } enemy)
            {
                if (!(fighting && ReferenceEquals(_combat.Areas.SelectedEnemy, enemy))) _combat.Areas.SelectEnemy(open, enemy);
            }
            else if (area is Dungeon dungeon)
            {
                if (!(fighting && ReferenceEquals(_combat.Areas.CurrentArea?.Area, dungeon))) _combat.Areas.SelectDungeon(dungeon);
            }
            else { _state.Message = "That fight is not available."; _view?.Refresh(); return; }

            _state.Message = "";
            _ctx.Log($"Fight started from the simulator: {target.Name}{(target.StanceName != "" ? " (" + target.StanceName + ")" : "")}.");
            Close();   // out of the way: the fight is on
        }
        catch (Exception e)
        {
            _state.Message = "The fight could not be started.";
            _ctx.Log("Could not start the fight: " + e);
            _view?.Refresh();
        }
    }

    // Results come from the background thread; the window is only touched here, on the game's thread.
    void Pump(bool force = false)
    {
        bool changed = force;
        // Asked first: once the engine says it stopped, everything it made is already in the queue.
        bool running = _engine.Running;
        while (_engine.Finished.TryDequeue(out SimResult? result))
        {
            if (_run == null) continue;   // left over from a run that was dropped (another character was opened)
            _state.Results[result.Key] = result;
            changed = true;
        }
        if (running != _state.Running || _state.Done != _engine.Done || _state.Current != _engine.Current) changed = true;
        _state.Running = running;
        _state.Done = _engine.Done;
        _state.Total = _engine.Total;
        _state.Current = _engine.Current;
        if (_wasRunning && !running)
        {
            _ctx.Log($"Simulation finished: {_state.Done} of {_state.Total} fights in {_runClock.Elapsed.TotalSeconds:0.0}s.");
            KeepScan();
            if (_engine.Problem != "") _state.Message = _engine.Problem;
            changed = true;
        }
        _wasRunning = running;
        if (changed && _window is { IsAlive: true, Visible: true }) _view?.Refresh();
    }

    // A run that brought at least one result goes into the history, also when it was stopped half way.
    void KeepScan()
    {
        if (_run is not { } scan) return;
        _run = null;
        scan.Results = _runKeys.Select(k => _state.Results.GetValueOrDefault(k)).Where(r => r != null).Select(r => r!).ToList();
        if (scan.Results.Count == 0) return;
        scan.At = DateTime.UtcNow;
        _history.Add(scan);
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
        // The strip may be the one of the character before: its button goes, this one takes its place.
        if (strip.GetNodeOrNull("CombatSimButton") is { } stale) { stale.Name = "CombatSimButtonGone"; stale.QueueFree(); }
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
        return ModFiles.ReadJson<Settings>(SettingsFile, _ctx.Log);
    }

    void WriteSettings()
    {
        ModFiles.WriteJson(SettingsFile, _settings, _ctx.Log);
    }

#if DEVTOOLS
    // Development: a file "sim.request" opens the window and starts a run; when it is done, a picture of the
    // game goes into the mod's folder and the table into the log. The file holds
    //   <where>|<duration index>|<1 unlimited, 0 from bank>|<row to select>
    // where: "all", parts of area names joined by "+", "best <skill>" or "best <skill>@<areas>", or "open" to
    // only open the window (with "where", "history" or "view" as the picker further on). Pickers are joined by
    // "+"; "sort=<column>" among them clicks that column's name, twice to turn the order around, and
    // "details=end" scrolls the details down.
    int _devShotAt = -1;
    string? _devSelect, _devPicker;
    bool _devNoRun;
    int _devPickerAt = -1;

    void DevRequests()
    {
        string request = Path.Combine(_ctx.Directory, "sim.request");
        if (File.Exists(request))
        {
            string[] parts = File.ReadAllText(request).Trim().Split('|');
            File.Delete(request);
            Open();
            string where = parts[0].Trim();
            // "w1:<where>" runs the fights one at a time, "w4:" four side by side.
            if (where.Length > 2 && where[0] == 'w' && char.IsDigit(where[1]) && where[2] == ':') { _state.Workers = where[1] - '0'; where = where[3..]; }
            _devNoRun = where.Equals("open", StringComparison.OrdinalIgnoreCase);
            if (!_devNoRun)
            {
                _state.BestMode = where.StartsWith("best", StringComparison.OrdinalIgnoreCase);
                string areas = where;
                if (_state.BestMode)
                {
                    int at = where.IndexOf('@');
                    string skill = at < 0 ? where : where[..at];
                    areas = at < 0 ? "all" : where[(at + 1)..];
                    _state.TrainSkillId = _state.Skills.FirstOrDefault(k => skill.Trim().EndsWith(k.Name, StringComparison.OrdinalIgnoreCase)).Id ?? "";
                }
                _state.ScopeAreas.Clear();
                if (!areas.Equals("all", StringComparison.OrdinalIgnoreCase))
                    foreach (string piece in areas.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        foreach (AreaChoice area in _state.Areas.Where(a => a.Name.Contains(piece, StringComparison.OrdinalIgnoreCase)))
                            _state.ScopeAreas.Add(area.AreaId);
            }
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
            _state.SpellId = parts.Length > 7 ? _state.Spells.FirstOrDefault(s => s.Name.Contains(parts[7].Trim(), StringComparison.OrdinalIgnoreCase))?.Id ?? "" : "";
            _ctx.Log("Dev: spells = " + string.Join(", ", _state.Spells.Select(s => s.Book + "/" + s.Name)) + "; chosen = " + _state.SpellId);
            _devPicker = parts.Length > 5 ? parts[5].Trim() : null;
            _ctx.Log("Dev: loadouts = " + string.Join(" / ", _state.Loadouts.Select(l => l.Name)) + "; gear = " +
                     string.Join(", ", _state.Gear.Select(g => $"{g.SlotName}:{(g.ItemId == null ? "-" : g.ItemName)}{(g.Changed ? "*" : "")}")));
            Build();
            _window!.Visible = true;
            if (!_devNoRun) Simulate();
            _devSelect = parts.Length > 3 ? parts[3].Trim() : null;
            _devShotAt = -2;   // armed
            _ctx.Log("Dev: areas = " + string.Join(" / ", _state.Areas.Select(a => $"{a.CategoryName}: {a.Name} ({a.Fights})")));
        }
        if (_devShotAt == -2 && !_engine.Running && (_devNoRun || _state.Done > 0))
        {
            if (!string.IsNullOrEmpty(_devSelect) && _state.Results.Values.FirstOrDefault(r => r.Target.Name.Contains(_devSelect, StringComparison.OrdinalIgnoreCase)) is { } pick)
            { _state.Selected = pick.Target.Key; _state.Pinned = true; Build(); _window!.Visible = true; }
            _devPickerAt = string.IsNullOrEmpty(_devPicker) ? -1 : _frame + 30;   // once the window is laid out
            _devShotAt = _frame + 90;
        }
        if (_devPickerAt > 0 && _frame >= _devPickerAt)
        {
            _devPickerAt = -1;
            foreach (string what in _devPicker!.Split('+')) _view?.DevOpenPicker(what);
        }
        if (_devShotAt > 0 && _frame >= _devShotAt)
        {
            _devShotAt = -1;
            _ctx.Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(_ctx.Directory, "shot-sim.png"));
            _ctx.Log("Dev: saved shot-sim.png");
            Rect2 frame = _window!.Root.GetGlobalRect();
            foreach (Node child in _window.Layer.GetChildren())
                if (child is PanelContainer { Visible: true } panel && panel != _window.Root) frame = frame.Merge(panel.GetGlobalRect());
            _ctx.Log($"Dev: rect = {(int)frame.Position.X},{(int)frame.Position.Y},{(int)frame.End.X},{(int)frame.End.Y}");
            // The sizes LargestScale goes by must cover the window as it really is.
            Vector2 full = _window.Root.Size / _window.Style.Scale, screen = _ctx.Tree.Root.GetVisibleRect().Size;
            float bar = FooterHeight();
            _ctx.Log($"Dev: window {_window.Root.Size.X:0}x{_window.Root.Size.Y:0} at size {_window.Style.Scale:0.##} (chosen {_settings.Scale:0.##}, largest {LargestScale(bar):0.##}), {full.X:0}x{full.Y:0} at full size" +
                     $"{(full.X > FullWidth || full.Y > FullHeight ? $"  LARGER THAN THE {FullWidth}x{FullHeight} LargestScale GOES BY" : "")}; screen {screen.X:0}x{screen.Y:0}, bottom bar {bar:0}, window bottom at {_window.Root.GetGlobalRect().End.Y:0}");
            foreach (SimResult r in _state.Results.Values.OrderByDescending(r => r.XpPerHour))
                _ctx.Log($"Dev: [{r.SetupLabel}] {r.Target.Name + " " + r.Target.StanceName,-30} {string.Join(" ", r.Xp.Select(x => x.Key.Replace("MelvorBase:", "") + "=" + (x.Value / Math.Max(r.Hours, 1e-9)).ToString("N0")))} kills {r.KillsPerHour,7:N0}  xp {r.XpPerHour,8:N0}  gold {r.GoldPerHour,8:N0}  food {r.FoodPerHour,6:N1}  deaths {r.DeathsPerHour,5:N1}  " +
                         $"eaten {r.FoodEaten} / killed {r.Kills} = {r.FoodPerKill:0.####}  healed {r.Healed}  food gone {(r.FoodGoneSeconds < 0 ? "-" : $"{r.FoodGoneSeconds:0}s, {r.FoodGoneKills} kills")}  " +
                         $"lowHP {r.LowestHpFraction,4:P0}  {r.RealMilliseconds,5:N0} ms  {r.Note}");
        }
    }
#endif
}
