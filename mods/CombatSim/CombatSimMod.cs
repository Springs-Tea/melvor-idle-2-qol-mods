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
public partial class CombatSimMod : IMod, ISimLookup
{
    const string WindowTitle = "Combat Simulator";
    const string StatusPrefix = "Simulator · ";

    sealed class Settings
    {
        public float? X { get; set; }
        public float? Y { get; set; }
        public float Scale { get; set; } = 1f;
        public bool Minimised { get; set; }
        public float? FoldedRight { get; set; }
        /// <summary>Before areas could be ticked: the one area that was chosen.</summary>
        public string Scope { get; set; } = "";
        public List<string> Areas { get; set; } = new();
        public List<string> BestAreas { get; set; } = new();
        /// <summary>Fights simulated side by side, 1 to 8; 0 lets the mod choose (half of the processor, four at most).</summary>
        public int Workers { get; set; }
        public int Duration { get; set; } = 1;
        /// <summary>"Go on until": the size in percent, one of SimState.SureSizes; 0 is "Time is up" (off).</summary>
        public double Sure { get; set; }
        public bool Unlimited { get; set; } = true;
        public bool ShowEnemyCards { get; set; } = true;
        /// <summary>"Wear this setup first" above the Fight button: off until the player ticks it.</summary>
        public bool WearSetupFirst { get; set; }
        public bool BestMode { get; set; }
        public string TrainSkill { get; set; } = "";
        /// <summary>The list's columns by key, left to right; null is the default list (SimColumns.Default).</summary>
        public List<string>? Columns { get; set; }
        /// <summary>Which items Find upgrades tries (SimUpgrades.cs, UpgradeChoice): slot ids and kinds of source, none ticked is all.</summary>
        public List<string> UpgradeSlots { get; set; } = new();
        public List<string> UpgradeKinds { get; set; } = new();
        public string UpgradeText { get; set; } = "";
        public bool UpgradeCannotWear { get; set; }
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
    readonly Tempo _tick = new(250), _search = new(1000);
    bool _stripMissingLogged, _refreshOnUnfold, _loadoutsUnreadable;
    string _title = WindowTitle;
    readonly HashSet<string> _reported = new();

    public void OnLoad(ModContext ctx)
    {
        _ctx = ctx;
        _settings = ReadSettings();
        if (_settings.Scope != "") { _settings.Areas = new List<string> { _settings.Scope }; _settings.Scope = ""; }
        _state.FightAreas = _settings.Areas.ToHashSet();
        _state.BestAreas = _settings.BestAreas.ToHashSet();
        _state.Duration = _settings.Duration;
        _state.Sure = SimState.SureSizes.Contains(_settings.Sure) ? _settings.Sure : 0;
        _state.Unlimited = _settings.Unlimited;
        _state.ShowEnemyCards = _settings.ShowEnemyCards;
        _state.WearSetup = _settings.WearSetupFirst;
        _state.BestMode = _settings.BestMode;
        _state.TrainSkillId = _settings.TrainSkill;
        _state.Columns = SimColumns.Read(_settings.Columns);
        ReadUpgradeChoice();   // SimUpgrades.cs
        _engine = new SimEngine(ctx.Log);
        _state.Workers = _settings.Workers > 0 ? Math.Min(_settings.Workers, 8) : SimEngine.DefaultWorkers;
        _history = new SimHistory(ctx.Directory, ctx.Log);
        _state.History = _history.Scans;
        ctx.Frame += OnFrame;
        // The lines on the game's enemy cards are an extra: whatever stops them leaves the simulator running.
        try { StartCards(); }
        catch (Exception e) { CardReadings.Stop(ctx.Log, e); }
        ctx.Log("Ready. The round button on the right edge of the screen opens the simulator.");
    }

    void OnFrame()
    {
        try
        {
            if (_search.Due())
            {
                TrackCharacter();
                if (_game != null) UpdateStripButton();
            }
            if (_game == null) return;
            if (!_tick.Due()) return;
            Pump();
#if DEVTOOLS
            DevRequests();
            DevWearRequests();
#endif
        }
        catch (Exception e)
        {
            // Whatever keeps going wrong would otherwise be written four times a second.
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
        _state.Running = false;
        _state.Current = "";
        _state.Overrides.Clear();
        _state.ForgetCompare();
        ForgetUpgrades();   // SimUpgrades.cs: the items tried, and where items come from, were the character's before
        _state.LoadoutId = _state.WornLoadoutId = _state.SpellId = _state.StanceId = _state.Message = _state.FightReport = "";
        _wearShown = null;   // FightSetup.cs: the words on show were for the character before
        _history.Close();
        _historyRead = false;
        _run = null;
#if DEVTOOLS
        _devCommands.Clear();
        _devShotAt = _devPickerAt = -1;
        // What was kept of the character before is not this one's to put back (the settings are the mod's own).
        _devSession = null;
        _devStarted.Clear();
#endif
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
        DescribeOther();   // compare mode's other setup (SimCompare.cs)
    }

    // The loadouts to choose from, and the slots of the chosen one with the player's changes on top.
    void BuildGear()
    {
        _state.Loadouts.Clear();
        _state.Gear.Clear();
        _gearLoadout = _gearWorn = null;   // CombatSimGear.cs: made again by ReadGear, for this character
        _state.ShownSlots = null;
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

        // Slot by slot, with a two-handed weapon in both hands as the game has it (CombatSimGear.cs).
        WeaponCapability? wielded = ReadGear(saved);
        WeaponType? weapon = wielded?.Type;

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
        // The stance chosen in Fights follows the weapon (issue #51): one that a weapon of another kind does not have goes
        // back to "As the game has it", as a spell the weapon cannot cast does below. With one stance there is no choice.
        if (_state.Stances.Count < 2 || _state.Stances.All(s => s.Id != _state.StanceId)) _state.StanceId = "";
        // What "As the game has it" stands for, as the Spell box's "As now: ..." does: the stance the game has selected for
        // the weapon, when the setup is the loadout worn with a weapon of the kind worn. Another loadout keeps a stance of
        // its own, which cannot be read.
        _state.StanceNow = null;
        if (weapon != null && _state.LoadoutId == worn && weapon.Id == WornWeaponKind()?.Id)
        {
            try
            {
                if (SelectedStance(_combat, weapon) is { } now && _state.Stances.Any(s => s.Id == now.Id))
                    _state.StanceNow = (now.Id, now.DisplayName.Text);
            }
            catch (Exception e)
            {
                if (_reported.Add("stance now")) _ctx.Log("The attack stance selected now could not be read (the game was updated?): " + e.Message);
            }
        }

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

    // The game's own picture of a stance (AttackStance.Icon, as its combat screen's stance buttons show it), by its id in
    // the game's data: also for a stance of an earlier scan's weapon. Without it the stance is named in words alone.
    Texture2D? ISimLookup.StanceIcon(string stanceId)
    {
        if (_game == null) return null;
        try { return StanceOf(_game, stanceId)?.Icon; }
        catch (Exception e)
        {
            if (_reported.Add("stance icon")) _ctx.Log("The pictures of the attack stances could not be read (the game was updated?): " + e.Message);
            return null;
        }
    }

    // Its own method, never inlined: a game update that moved the stances fails when it is called, inside the try.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static AttackStance? StanceOf(Game game, string stanceId) =>
        game.Data.GetDataRegistryOutput<AttackStance>().TryGetObjectById(stanceId, out AttackStance? stance) ? stance : null;

    // The kind of weapon the character holds now, bare hands included, as Fight reads it to switch the stance.
    WeaponType? WornWeaponKind() => _combat == null ? null : (_combat.Equipment.CurrentWeapon ?? _combat.Equipment.DefaultWeapon)?.Weapon?.Type;

    // The stance the game has selected for a kind of weapon (PlayerComponent.GetSelectedStance). Its own method, never
    // inlined, for the same reason as StanceOf.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static AttackStance? SelectedStance(CombatEntity? combat, WeaponType weapon) => combat?.Player.GetSelectedStance(weapon);

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

    void ToggleWindow()
    {
        if (_window is not { IsAlive: true, Visible: true }) Open();
        else if (_window.Minimised) _window.SetMinimised(false);
        else Close();
    }

    void Open()
    {
        if (_game == null) return;
        // Unlocks, equipment, food and levels may have changed since last time: the window is built again
        // from them. During a run it is left alone, also while upgrades are being found (SimUpgrades.cs).
        bool refreshed = (_run == null && _upgradeRun == null) || !_engine.Running;
        if (refreshed) BuildCatalog();
        ReadHistory();
        if (refreshed || _window is not { IsAlive: true }) Build();
        _window!.Visible = true;
        UpdateTitle();
        _view!.Refresh();
        SyncStripButton();
    }

    void Close()
    {
        _state.FightReport = "";   // what the last Fight put on was read; next time the words say what Fight will do
        if (_window is { IsAlive: true }) _window.Visible = false;
        SyncStripButton();
    }

    // The earlier scans are read the first time the window or an enemy card needs them.
    void ReadHistory()
    {
        if (_historyRead || _game == null) return;
        _historyRead = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _history.Open(_game.CharacterName);
        if (_history.Scans.Count > 0) _ctx.Log($"{_history.Scans.Count} earlier scans read in {clock.ElapsedMilliseconds} ms.");
    }

    // Its own method, never inlined: when a game update has removed a type the cards use, loading them fails
    // here, where OnLoad catches it, and not in OnLoad itself.
    [MethodImpl(MethodImplOptions.NoInlining)]
    void StartCards()
    {
        var cards = new EnemyCards(_ctx, _history, () => _game, () => _combat, () => _state.ShowEnemyCards, ReadHistory, ShowFromCard);
#if DEVTOOLS
        cards.DevWindowAside = DevCardsAside;
        cards.DevWindowBack = DevCardsBack;
#endif
        _ctx.Frame += cards.OnFrame;
    }

    // A click on an enemy card's line: the simulator opens on the scan the line comes from, with that fight
    // selected, and the area is ticked for the next Simulate.
    void ShowFromCard(Scan scan, SimResult result)
    {
        if (_game == null) return;
        _state.ScopeAreas.Add(result.Target.AreaId);
        _state.Viewing = scan;
        // In a Best for scan the table has one line per fight, the stance that ranks best.
        _state.Selected = !scan.BestMode ? result.Target.Key
            : scan.Results.Where(r => r.Target.FightKey == result.Target.FightKey && !r.Failed)
                .OrderByDescending(scan.Rank).Select(r => r.Target.Key).FirstOrDefault() ?? result.Target.Key;
        _state.Pinned = true;
        Open();
        _window!.SetMinimised(false);
        SaveChoices();
    }

    void Build()
    {
        _window?.Free();
        Vector2? at = _settings is { X: { } x, Y: { } y } ? new Vector2(x, y) : null;
        // The game's bar along the bottom stays in sight: the window is placed, and kept, above it.
        float footer = FooterHeight();
        _title = Title(_settings.Minimised);
        _refreshOnUnfold = false;
        _window = new GameWindow(_ctx.Tree, _title, Math.Min(_settings.Scale, LargestScale(footer)), at,
            canMinimise: true, minimised: _settings.Minimised, bottomInset: footer, foldedRight: _settings.FoldedRight);
        _window.CloseRequested += Close;
        _window.MinimisedChanged += minimised =>
        {
            // GameWindow raises Moved next, which saves the fold state and its matching position together.
            _settings.Minimised = minimised;
            KeepTitleRoom();
            UpdateTitle();
            if (!minimised && _refreshOnUnfold)
            {
                _view!.Refresh();
                _refreshOnUnfold = false;
            }
            SyncStripButton();
#if DEVTOOLS
            LogFold();
#endif
        };
        _window.Moved += position =>
        {
            _settings.X = position.X;
            _settings.Y = position.Y;
            _settings.FoldedRight = _window.Minimised ? _window.Root.GetRect().End.X : null;
            WriteSettings();
        };
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
        KeepTitleRoom();
#if DEVTOOLS
        _view.DevLog = _ctx.Log;
        LogFold();
#endif
    }

    string Title(bool folded) => folded && _state.ScanStatus is { Length: > 0 } status
        ? StatusPrefix + status : WindowTitle;

    // The folded title keeps one width from a scan's first count to its end, so the strip and its buttons stay
    // where they are. It fits its title again once the window has been opened after the scan.
    void KeepTitleRoom()
    {
        if (_window is not { IsAlive: true }) return;
        if (_state.Running) _window.KeepRoomForTitles(ScanTitles());
        else if (!_window.Minimised) _window.KeepRoomForTitles(Array.Empty<string>());
    }

    // Every title the folded window can show during this scan and at its end: the count, with each digit as
    // often as the total has digits (so no digit has to be the widest), Done and Error. Not Stopped: a scan is
    // cancelled in the open window, and that is where the paper fits its title again.
    IEnumerable<string> ScanTitles()
    {
        yield return StatusPrefix + _state.DoneText;
        yield return StatusPrefix + SimState.ErrorText;
        int digits = _state.Total.ToString().Length;
        for (char digit = '0'; digit <= '9'; digit++)
            yield return StatusPrefix + _state.CountText(new string(digit, digits));
    }

    void UpdateTitle()
    {
        string title = Title(_window!.Minimised);
        if (title == _title) return;
        _window.SetTitle(title);
        _title = title;
#if DEVTOOLS
        _ctx.Log($"Dev: simulator title = {title}, running = {_state.Running}, fights = {_state.FightCount}, folded = {_window.Minimised}, " +
                 $"position = {_window.Position}, size = {_window.Root.Size}, {_window.DevTitleLayout}");
#endif
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
    // for, with the Train and the Spell box); larger, a few pixels more or less than that in proportion, every
    // length being rounded on its own. It is never larger than the screen above the game's bottom bar,
    // whatever size was chosen on another screen or dragged to: the largest size that fits, in the same steps
    // of 5% the corner resizes in.
    //
    // Below full size the height is not quite in proportion. The Setup column sets it, and its buttons and drop-downs
    // keep the margins of the game's theme at every size: the same Best for list was 544 px high at size 0.6 and
    // 780 at 0.9, which is 787 px that shrink with the window and 72 that do not (907 and 867 at full size). With
    // the Spell box, one drop-down more, and the rounding of every length at the sizes between, 100 of the 895 are
    // counted as keeping their size.
    //
    // Fights with the Stance and the Spell box (issue #51) stays under that at every size: its Stance box is one row of a
    // caption beside a drop-down, where Best for has Train's caption over its drop-down (one caption and one spacing more,
    // with the same drop-down's margins), and its line above Simulate is one line where Best for's is two. Neither box
    // makes the Setup column wider (SimViewStance.FitBeside).
    const float FullWidth = 1414, FullHeight = 895, KeptHeight = 100;

    float LargestScale(float footer)
    {
        Vector2 screen = _ctx.Tree.Root.GetVisibleRect().Size;
        float room = screen.Y - footer;
        float high = room >= FullHeight ? room / FullHeight : (room - KeptHeight) / (FullHeight - KeptHeight);
        float fits = Math.Min(screen.X / FullWidth, high);
        return Math.Clamp(MathF.Floor(fits * 20f) / 20f, GameWindow.MinScale, GameWindow.MaxScale);
    }

    /// <summary>How high the window may be at this size, by the rule LargestScale goes by.</summary>
    static float HeightAt(float scale) => scale >= 1 ? FullHeight * scale : (FullHeight - KeptHeight) * scale + KeptHeight;

    void SaveChoices()
    {
        _settings.Areas = _state.FightAreas.OrderBy(a => a, StringComparer.Ordinal).ToList();
        _settings.BestAreas = _state.BestAreas.OrderBy(a => a, StringComparer.Ordinal).ToList();
        _settings.Duration = _state.Duration;
        _settings.Sure = _state.Sure;
        _settings.Unlimited = _state.Unlimited;
        _settings.ShowEnemyCards = _state.ShowEnemyCards;
        _settings.WearSetupFirst = _state.WearSetup;
        _settings.BestMode = _state.BestMode;
        _settings.TrainSkill = _state.TrainSkillId;
        _settings.Columns = SimColumns.Write(_state.Columns);
        WriteUpgradeChoice();   // SimUpgrades.cs
        WriteSettings();
    }

    // What a run is asked, from the choices on the left; with the setup on the left unless another is given (A and B in
    // compare mode, SimCompare.cs).
    SimOptions RunOptions(SimSetup? setup = null)
    {
        SimSetup with = setup ?? _state.OnLeft;
        // In Fights the stance chosen for the setup (issue #51); none in Best for, whose lines name their own.
        (string Id, string Name)? stance = _state.ChosenOf(with);
        return new()
        {
            Hours = SimState.Durations[Math.Clamp(_state.Duration, 0, SimState.Durations.Length - 1)].Hours,
            UnlimitedSupplies = _state.Unlimited,
            LoadoutId = with.LoadoutId == "" ? null : with.LoadoutId,
            Overrides = new Dictionary<string, string>(with.Overrides),
            SpellId = with.SpellId == "" ? null : with.SpellId,
            StanceId = stance?.Id,
            StanceName = stance?.Name ?? "",
            SetupKey = _state.KeyOf(with),
            SetupLabel = with.BaseLabel,
            // "Go on until": the longest duration is as far as a fight goes on; never From bank (SimEngine.Fight).
            Sure = _state.Unlimited ? _state.Sure / 100 : 0,
            SureSizes = SimState.SureSizes.Where(size => size > 0).Select(size => size / 100).ToArray(),
            MostHours = SimState.Durations[^1].Hours,
            Watch = _view?.WatchedRates() ?? new List<SimRate>(),
            // What the slots show for this setup (#46), as they did when it was last on the left (SimSetup.Shown).
            Shown = with.Shown == null ? null : new Dictionary<string, string>(with.Shown),
#if DEVTOOLS
            DevGear = with.DevSlots,
#endif
        };
    }

    void Simulate()
    {
        if (_game == null) return;
        // First back to the current results: the XP column of an earlier scan may be of another skill (RunOptions).
        _state.Viewing = null;
        // The setup on the left; in compare mode A and B, fight by fight (SimCompare.cs). B's options, when there are two.
        List<SimJob> jobs = RunJobs();
        SimOptions options = jobs.FirstOrDefault()?.Options ?? RunOptions();
        SimOptions? b = jobs.Select(j => j.Options).FirstOrDefault(o => o != options);
#if DEVTOOLS
        // raw= changes the setup on the left (CombatSimGear.cs).
        DevSimulateOptions(b != null && _state.ShowingB ? b : options);
#endif
        _state.Message = "";
        if (!_engine.Start(_game, jobs, _state.Workers, out string error)) { _state.Message = error; _ctx.Log(error); }
        else
        {
            // Old numbers for these fights would sit next to new ones until they are replaced.
            foreach (SimJob job in jobs) _state.Results.Remove(job.Key);
            _runKeys = jobs.Select(j => j.Key).ToList();
            _run = new Scan
            {
                Id = DateTime.UtcNow.Ticks.ToString(),
                BestMode = _state.BestMode,
                TrainSkillId = _state.BestMode ? _state.TrainSkillId : "",
                TrainSkillName = _state.BestMode ? _state.Skills.FirstOrDefault(k => k.Id == _state.TrainSkillId).Name ?? "" : "",
                Where = _state.ScopeLabel,
                Hours = options.Hours,
                Unlimited = options.UnlimitedSupplies,
                // A scan names its setups with the stance chosen for them, so History tells them apart (issue #51).
                SetupKey = options.SetupKey,
                SetupLabel = SimText.WithStance(options.SetupLabel, options.StanceName),
                SetupKeyB = b?.SetupKey,
                SetupLabelB = b == null ? null : SimText.WithStance(b.SetupLabel, b.StanceName),
                Asked = jobs.Count,
            };
            _ctx.Log($"Simulating {jobs.Count} fights, {options.Hours:0.##} h each" +
                     (options.Sure > 0 ? $" or on until {SimRanges.Size(_state.Sure)} sure ({options.MostHours:0.##} h at most)" : "") +
                     $", supplies {(options.UnlimitedSupplies ? "unlimited" : "from bank")}, with {(b == null ? _run.SetupLabel : $"A {_run.SetupLabel} and B {_run.SetupLabelB}")}, " +
                     $"{Math.Min(_state.Workers, jobs.Count)} at a time.");
            _runClock.Restart();
        }
        Pump(force: true);
    }

    // The real thing: start the selected fight in the game, the way its own area menu does. Equipment is
    // left as it is unless "Wear this setup first" is ticked (FightSetup.cs); the stance the result was
    // simulated with is selected when the weapon has it, and only when it was asked for: the target of a Best for
    // line names it, and SimView.FightAs names the one chosen in the Stance box of Fights (issue #51).
    void Fight(SimTarget target)
    {
        if (_combat == null) return;
        // What the box put on first, when it was ticked and had something to do; null otherwise.
        WearOutcome? wore = null;
        try
        {
            var (area, active) = _combat.Areas.ActiveAreas.FirstOrDefault(a => a.Key.Id == target.AreaId);
            // An earlier scan may be of another character with the same name: only what this one has unlocked.
            if (area == null || active is not { UnlockRequirements.AreMet: true }) { _state.Message = "That area is not available."; _view?.Refresh(); return; }

            wore = WearForFight(target, area);
            // Something went wrong on the way: the player reads what happened before any fight.
            if (wore is { Fight: false }) { ShowWearReport(target, wore); return; }

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
            if (wore is { Read: true }) ShowWearReport(target, wore);   // the window stays, saying what did not go as said
            else Close();   // out of the way: the fight is on
        }
        catch (Exception e)
        {
            _state.Message = "The fight could not be started.";
            _ctx.Log("Could not start the fight: " + e);
            if (wore != null) ShowWearReport(target, wore);   // what went on; the status line says the fight did not start
            _view?.Refresh();
        }
    }

    // Results come from the background thread; the window is only touched here, on the game's thread.
    void Pump(bool force = false)
    {
        if (PumpUpgrades()) return;   // the run under way finds upgrades: its results are not scans (SimUpgrades.cs)
        bool changed = force;
        // Asked first: once the engine says it stopped, everything it made is already in the queue.
        bool running = _engine.Running && _run != null;
        while (_engine.Finished.TryDequeue(out SimResult? result))
        {
            if (_run == null) continue;   // left over from a run that was dropped (another character was opened)
            _state.Results[result.Key] = result;
            changed = true;
        }
        bool runningChanged = running != _state.Running;
        if (runningChanged) changed = true;
        _state.Running = running;
        // The engine keeps its last counters after a character switch. Only take this character's run.
        if (_run != null)
        {
            int done = _engine.Done;
            string current = _engine.Current;
            if (_state.Done != done || _state.Current != current) changed = true;
            _state.Done = done;
            _state.Total = _engine.Total;
            _state.Current = current;
            if (!running)
            {
                _ctx.Log($"Simulation finished: {_state.FightCount} fights in {_runClock.Elapsed.TotalSeconds:0.0}s.");
                KeepScan();
                if (_engine.Problem != "") _state.Message = _engine.Problem;
                changed = true;
            }
        }
        if (runningChanged) KeepTitleRoom();
        if (changed && _window is { IsAlive: true, Visible: true })
        {
            if (_window.Minimised)
            {
                _refreshOnUnfold = true;
                UpdateTitle();
            }
            else _view?.Refresh();
        }
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
#if DEVTOOLS
        DevKept(scan);
#endif
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
        _stripButton.Pressed += ToggleWindow;
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
            _stripButton.SetPressedNoSignal(_window is { IsAlive: true, Visible: true, Minimised: false });
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
    void LogFold(string? picture = null)
    {
        string state = $"simulator folded = {_window!.Minimised}, settings = {_settings.Minimised}, title = {_title}, " +
            $"tab = {(_state.BestMode ? "Best for" : "Fights")}, list = {_state.Viewing?.Title ?? "current"}, " +
            $"sort = {_state.SortKey}, descending = {_state.SortDescending}, columns = {_state.Columns.Count}, selected = {_state.Selected ?? "-"}, pinned = {_state.Pinned}, " +
            $"position = {_window.Position}, size = {_window.Root.Size}, right = {_window.Root.GetGlobalRect().End.X}, scale = {_window.Style.Scale}, " +
            $"{_window.DevTitleLayout}, visible = {_window.Visible}, running = {_state.Running}, fights = {_state.FightCount}";
        _ctx.Log(picture == null ? "Dev: " + state : $"Dev: saved {picture}: {state}");
        if (picture != null) File.WriteAllText(Path.Combine(_ctx.Directory, Path.ChangeExtension(picture, ".txt")), state);
    }

    // Development: a file "sim.request" opens the window and starts a run; when it is done, a picture of the
    // game goes into the mod's folder and the table into the log. The file holds
    //   <where>|<duration index>|<1 unlimited, 0 from bank>|<row to select>
    // where: "all", parts of area names joined by "+", "best <skill>" or "best <skill>@<areas>", or "open" to
    // only open the window (with "where", "history" or "view" as the picker further on). Pickers are joined by
    // "+"; "sort=<column>" among them clicks that column's name, twice to turn the order around, and
    // "details=end" scrolls the details down.
    long _devShotAt = -1;
    string? _devSelect, _devPicker;
    long _devPickerAt = -1;
    readonly Queue<string> _devCommands = new();
    // The settings as they were before the request, and the rest of the player's simulator it may change: put back
    // once its last line has run and its runs are over.
    Settings? _devSettings;
    DevSession? _devSession;

    // What a request may change that settings.json does not hold: the earlier scans (a run goes into History, and can
    // push the oldest out), the current results and the line about the last run, and the setup, selection and sort
    // of the window. Made: the ids of the scans the request's own runs put into History, the only ones it takes out
    // again; PlayerMade: those of the player's own runs that ended meanwhile, which stay.
    sealed record DevSession((string? Folder, List<Scan> Scans) History, Dictionary<string, SimResult> Results, int Done, int Total,
        string Message, string LoadoutId, Dictionary<string, string> Overrides, string SpellId, string StanceId, string? Selected, bool Pinned,
        Scan? Viewing, string SortKey, bool SortDescending, (bool On, bool ShowingB, SimSetup Other) Compare)
    {
        public readonly List<string> Made = new(), PlayerMade = new();
    }

    // The runs a request started itself (DevSimulate), until their scan goes into History (DevKept).
    readonly HashSet<Scan> _devStarted = new();

    // A run the request starts. Its scan is the request's: the only kind the put back takes out of History. A run the
    // player started by his own click, also one that was going when the request began, is his.
    void DevSimulate()
    {
        Scan? before = _run;
        Simulate();
        if (_run != null && !ReferenceEquals(_run, before)) _devStarted.Add(_run);
    }

    // A scan has just gone into History: note whose it is, at that moment.
    void DevKept(Scan scan)
    {
        bool request = _devStarted.Remove(scan);
        if (_devSession is not { } session) return;
        (request ? session.Made : session.PlayerMade).Add(scan.Id);
        _ctx.Log($"Dev: history: scan {scan.Id} kept, {(request ? "the request's own" : "the player's own (not the request's)")}.");
    }

    void DevRequests()
    {
        string request = Path.Combine(_ctx.Directory, "sim.request");
        // The pictures of stance=shot are taken first: nothing else changes the window, nor is it built again, meanwhile.
        if (DevStanceShooting()) return;
        if (_devSettings != null && _devCommands.Count == 0 && _devShotAt == -1 && _devPickerAt == -1
            && _run == null && !_state.Running && !_engine.Running) DevPutBack();
        if (_devCommands.Count == 0 && File.Exists(request))
        {
            _devSettings ??= JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(_settings));
            if (_devSession == null)
            {
                // As Open reads it, so that what is kept is what the player has, not an empty list.
                ReadHistory();
                _devSession = new DevSession(_history.DevKeep(), new Dictionary<string, SimResult>(_state.Results), _state.Done, _state.Total,
                    _state.Message, _state.LoadoutId, new Dictionary<string, string>(_state.Overrides), _state.SpellId, _state.StanceId, _state.Selected,
                    _state.Pinned, _state.Viewing, _state.SortKey, _state.SortDescending, (_state.Compare, _state.ShowingB, _state.Other));
                DevKeepUpgrades();   // SimUpgrades.cs
            }
            foreach (string line in File.ReadAllLines(request))
                if (line.Trim() != "") _devCommands.Enqueue(line.Trim());
            File.Delete(request);
        }
        long now = (long)Time.GetTicksMsec();
        while (_devCommands.TryPeek(out string? command))
        {
            if (command == "wait=progress" && _state.Running && _state.Done == 0) return;
            // Also the run of "twice=", which is not the window's.
            if (command == "wait=finished" && (_state.Running || _engine.Running)) return;
            _devCommands.Dequeue();
            if (command == "wait=progress" && (!_state.Running || _state.Done == _state.Total))
            {
                _ctx.Log("Dev: no partial scan to photograph. Choose more fights.");
                _devCommands.Clear();
                return;
            }
            if (command is "wait=progress" or "wait=finished") continue;
            if (command == "simulate") { DevSimulate(); continue; }
            if (DevWindowCommand(command)) return;
            bool prepare = command.StartsWith("setup=", StringComparison.Ordinal);
            if (!prepare && !command.Contains('|'))
            {
                // A mistyped command would otherwise be read as area names that match nothing: a scan of everything.
                _ctx.Log($"Dev: \"{command}\" is neither a command nor <where>|<duration>|...; the rest of the request is dropped.");
                _devCommands.Clear();
                return;
            }
            string[] parts = (prepare ? command[6..] : command).Split('|');
            Open();
            string where = parts[0].Trim();
            // "w1:<where>" runs the fights one at a time, "w4:" four side by side.
            if (where.Length > 2 && where[0] == 'w' && char.IsDigit(where[1]) && where[2] == ':') { _state.Workers = where[1] - '0'; where = where[3..]; }
            bool noRun = where.Equals("open", StringComparison.OrdinalIgnoreCase);
            if (!noRun)
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
            // ...|<spell>|<part of a stance's name>: the stance of Fights (issue #51); empty or none, As the game has it.
            // The stance= line chooses one in the box itself, as the player's click does.
            _state.StanceId = parts.Length > 8 && parts[8].Trim() != ""
                ? _state.Stances.FirstOrDefault(s => s.Name.Contains(parts[8].Trim(), StringComparison.OrdinalIgnoreCase)).Id ?? "" : "";
            _ctx.Log("Dev: stances = " + string.Join(", ", _state.Stances.Select(s => $"{s.Name} ({s.Id})")) + "; chosen = " + (_state.StanceId == "" ? "as the game has it" : _state.StanceId));
            _devPicker = parts.Length > 5 ? parts[5].Trim() : null;
            _ctx.Log("Dev: loadouts = " + string.Join(" / ", _state.Loadouts.Select(l => l.Name)) + "; gear = " +
                     string.Join(", ", _state.Gear.Select(g => $"{g.SlotName}:{(g.ItemId == null ? "-" : g.ItemName)}{(g.Changed ? "*" : "")}")));
            Build();
            _window!.Visible = true;
            _window.SetMinimised(false);
            if (!prepare && !noRun) DevSimulate();
            _devSelect = parts.Length > 3 ? parts[3].Trim() : null;
            _devPickerAt = -1;
            _devShotAt = prepare ? -1 : -2;
            _ctx.Log("Dev: areas = " + string.Join(" / ", _state.Areas.Select(a => $"{a.CategoryName}: {a.Name} ({a.Fights})")));
            break;
        }
        if (_devShotAt == -2 && !_state.Running && !_engine.Running)
        {
            if (_window is not { IsAlive: true, Visible: true }) Open();
            _window!.SetMinimised(false);
            if (!string.IsNullOrEmpty(_devSelect) && _state.Results.Values.FirstOrDefault(r => r.Target.Name.Contains(_devSelect, StringComparison.OrdinalIgnoreCase)) is { } pick)
            { _state.Selected = pick.Target.Key; _state.Pinned = true; Build(); _window!.Visible = true; }
            // Once the window is laid out: the 30 and 90 frames these waits were, at 60 frames a second.
            _devPickerAt = string.IsNullOrEmpty(_devPicker) ? -1 : now + 500;
            _devShotAt = now + 1500;
        }
        if (_devPickerAt > 0 && now >= _devPickerAt)
        {
            _devPickerAt = -1;
            foreach (string what in _devPicker!.Split('+')) _view?.DevOpenPicker(what);
        }
        if (_devShotAt > 0 && now >= _devShotAt)
        {
            _devShotAt = -1;
            DevShot("sim");
            Rect2 frame = _window!.Root.GetGlobalRect();
            foreach (Node child in _window.Layer.GetChildren())
                if (child is PanelContainer { Visible: true } panel && panel != _window.Root) frame = frame.Merge(panel.GetGlobalRect());
            _ctx.Log($"Dev: rect = {(int)frame.Position.X},{(int)frame.Position.Y},{(int)frame.End.X},{(int)frame.End.Y}");
            // The sizes LargestScale goes by must cover the window as it really is, at every size.
            Vector2 full = _window.Root.Size / _window.Style.Scale, screen = _ctx.Tree.Root.GetVisibleRect().Size;
            float bar = FooterHeight(), scale = _window.Style.Scale, wide = FullWidth * scale, high = HeightAt(scale);
            _ctx.Log($"Dev: window {_window.Root.Size.X:0}x{_window.Root.Size.Y:0} at size {scale:0.##} (chosen {_settings.Scale:0.##}, largest {LargestScale(bar):0.##}), {full.X:0}x{full.Y:0} at full size; " +
                     $"LargestScale allows {wide:0.#}x{high:0.#} at this size" +
                     $"{(_window.Root.Size.X > wide + 0.5f || _window.Root.Size.Y > high + 0.5f ? "  LARGER THAN LargestScale ALLOWS" : ", it fits")}; screen {screen.X:0}x{screen.Y:0}, bottom bar {bar:0}, window bottom at {_window.Root.GetGlobalRect().End.Y:0}");
            foreach (SimResult r in _state.Results.Values.OrderByDescending(r => r.XpPerHour))
                _ctx.Log($"Dev: [{r.SetupLabel}] {r.Target.Name + " " + r.Target.StanceName,-30} {string.Join(" ", r.Xp.Select(x => x.Key.Replace("MelvorBase:", "") + "=" + (x.Value / Math.Max(r.Hours, 1e-9)).ToString("N0")))} kills {r.KillsPerHour,7:N0}  xp {r.XpPerHour,8:N0}  gold {r.GoldPerHour,8:N0}  food {r.FoodPerHour,6:N1}  deaths {r.DeathsPerHour,5:N1}  " +
                         $"eaten {r.FoodEaten} / killed {r.Kills} = {r.FoodPerKill:0.####}  healed {r.Healed}  food gone {(r.FoodGoneSeconds < 0 ? "-" : $"{r.FoodGoneSeconds:0}s, {r.FoodGoneKills} kills")}  " +
                         $"lowHP {r.LowestHpFraction,4:P0}  {r.RealMilliseconds,5:N0} ms  {r.Note}");
        }
    }

    // Further lines in sim.request run one after another, also while the workers are running:
    //   setup=<run request>   the setup of a run request, without starting it; "simulate" starts it
    //   minimise, close, button   a click on the fold button, on the X, on the round button on the right edge
    //   wait=progress, wait=finished   the next lines wait for the first finished fight, or for the end
    //   cancel, select=first, sort=<column>, scale=min|max
    //   shot=<name>   shot-<name>.png and crop-<name>.png once drawn, the window's state in shot-<name>.txt
    //   sure=<percent>   chooses that size in "Go on until" (0 is "Time is up"), as the box does; put back at the end
    //   twice=<part of an enemy's name, or first>   simulates that fight of the current setup twice side by side, from one
    //                 snapshot, outside the window; the log says whether the two came out the same (SimEngine.DevTwin)
    //   pick=<slot>=<item>, picker=<slot>, gear=<name>, hover=<slot>, raw=<slot>=<item>   the slots on the left and the
    //                 game's own equipment screen (CombatSimGear.DevGearCommand)
    //   compare=<loadout>|<slot=item id>|<spell>, compare=off, side=a|b, wear=on|off   compare mode (SimCompare.cs)
    //   upgrades=<enemy>, upgrades=slot=|from=|search=|cannot|clear|go|panel, upgrades=stop|back|line=<item>|hover=<item>|log
    //                 Find upgrades and its panel (SimUpgrades.cs)
    //   stance=<part of a stance's name>, stance=#2, stance=game, stance=log, stance=view#2, stance=shot   the Stance box
    //                 of Fights (issue #51, DevStance): a choice in it as the player's click makes it (#2: the second stance
    //                 under As the game has it; game: As the game has it); log only logs; view#2 shows the second scan of
    //                 History; shot takes the pictures of the box, of its list and of Details' stance line, and reads their
    //                 pixels back
    // A line that is none of these and has no "|" ends the request. After its last line, DevPutBack.
    bool DevWindowCommand(string command)
    {
        switch (command)
        {
            case "minimise":
            case "close":
                _devShotAt = _devPickerAt = -1;
                Button? button = _window?.Root.FindChildren("*", "Button", true, false).OfType<Button>()
                    .FirstOrDefault(b => command == "close" ? b.TooltipText == "Close"
                        : b.TooltipText is "Fold down to the title" or "Open the window again");
                DevClick(button, command);
                break;
            case "button":
                _devShotAt = _devPickerAt = -1;
                DevClick(_stripButton, command);
                break;
            case "cancel": _engine.Cancel(); break;
            case "select=first": DevClick(_view?.DevFirstRow, "first fight"); break;
            case "scale=min":
            case "scale=max":
                _settings.Scale = command == "scale=min" ? GameWindow.MinScale : LargestScale(FooterHeight());
                Build();
                _window!.Visible = true;
                break;
            default:
                if (command.StartsWith("sort=", StringComparison.Ordinal)) _view?.DevOpenPicker(command);
                else if (command.StartsWith("shot=", StringComparison.Ordinal)) DevShot(command[5..]);
                else if (command.StartsWith("sure=", StringComparison.Ordinal)) DevSure(command[5..]);
                else if (command.StartsWith("twice=", StringComparison.Ordinal)) DevTwice(command[6..].Trim());
                else if (command.StartsWith("compare=", StringComparison.Ordinal)) DevCompare(command[8..].Trim());
                else if (command.StartsWith("side=", StringComparison.Ordinal)) DevSide(command[5..].Trim());
                else if (command.StartsWith("wear=", StringComparison.Ordinal)) DevTickWear(command[5..].Trim());
                else if (command.StartsWith("upgrades=", StringComparison.Ordinal)) DevUpgradesCommand(command[9..].Trim());
                else if (command.StartsWith("stance=", StringComparison.Ordinal)) DevStance(command[7..].Trim());
                else if (!DevGearCommand(command)) return false;   // pick=, picker=, gear=, hover=, raw= (CombatSimGear.cs)
                break;
        }
        return true;
    }

    void DevSure(string size)
    {
        if (!double.TryParse(size, out double percent) || !SimState.SureSizes.Contains(percent))
        {
            _ctx.Log($"Dev: sure={size} is not one of the sizes {string.Join(", ", SimState.SureSizes)}.");
            return;
        }
        _state.Sure = percent;
        Open();
        Build();
        _window!.Visible = true;
        _ctx.Log($"Dev: Go on until set to {SimView.SureItem(percent)}{(_state.Unlimited ? "" : ", which From bank does not use")}.");
    }

    // The same fight twice, from one snapshot, side by side: the run is not the window's (no _run), so it neither
    // shows in the list nor goes into the history. Both results are logged, and whether they came out the same.
    void DevTwice(string name)
    {
        if (_game == null || _engine.Running) { _ctx.Log("Dev: twice: no character, or a run is going on."); return; }
        if (_state.Catalog.Count == 0) BuildCatalog();
        SimTarget? target = _state.ToSimulate().FirstOrDefault(t => name == "first" || t.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (target == null) { _ctx.Log($"Dev: twice: no fight {name} where the simulator is set to fight."); return; }
        SimOptions options = RunOptions();
        if (!_engine.Start(_game, new[] { target, target }, options, 2, out string error)) { _ctx.Log("Dev: twice: " + error); return; }
        _ctx.Log($"Dev: twice: {target.Name} {target.StanceName}, {options.Hours:0.##} h, with {options.SetupLabel}, two copies side by side from one snapshot.");
    }

    void DevShot(string name)
    {
        if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            _ctx.Log("Dev: picture names take letters, numbers, hyphens and underscores.");
            return;
        }
        GameWindow? window = _window;
        void Capture()
        {
            RenderingServer.FramePostDraw -= Capture;
            string file = $"shot-{name}.png";
            if (window == null || window != _window || !window.IsAlive)
            {
                _ctx.Log($"Dev: {file} not taken: the window was built again, or the character changed, before it was drawn.");
                return;
            }
            try
            {
                using Image image = _ctx.Tree.Root.GetTexture().GetImage();
                Error saved = image.SavePng(Path.Combine(_ctx.Directory, file));
                if (saved != Error.Ok) { _ctx.Log($"Dev: could not save {file}: {saved}"); return; }
                LogFold(file);
                // The window alone, as Item Encyclopedia's crop-*.png, with a panel that is open beside it (as the
                // run request's "Dev: rect" line takes them): the picture is in the screen's pixels, the window in the
                // game's own, which the game stretches.
                Rect2 drawn = window.Root.GetGlobalRect();
                foreach (Node child in window.Layer.GetChildren())
                    if (child is PanelContainer { Visible: true } panel && panel != window.Root) drawn = drawn.Merge(panel.GetGlobalRect());
                Rect2 frame = _ctx.Tree.Root.GetFinalTransform() * drawn.Grow(10);
                var whole = new Rect2I(0, 0, image.GetWidth(), image.GetHeight());
                Rect2I part = new Rect2I((int)frame.Position.X, (int)frame.Position.Y, (int)frame.Size.X, (int)frame.Size.Y).Intersection(whole);
                if (window.Visible && part.Size.X > 0 && part.Size.Y > 0)
                {
                    using Image crop = image.GetRegion(part);
                    crop.SavePng(Path.Combine(_ctx.Directory, $"crop-{name}.png"));
                    _ctx.Log($"Dev: crop-{name}.png is {part.Position.X},{part.Position.Y} {part.Size.X}x{part.Size.Y} of {image.GetWidth()}x{image.GetHeight()}");
                }
            }
            catch (Exception e) { _ctx.Log($"Dev: could not capture {file}: {e.Message}"); }
        }
        RenderingServer.FramePostDraw += Capture;
    }

    // A request leaves the player's simulator as it was: settings.json (the window's fold, place and size, also
    // after scale=min|max) and the Setup choices it holds go back to what they were before the first line. History
    // loses the request's own scans only (SimHistory.DevPutBack). The current results, the equipment and spell
    // chosen, the selected line and the sort go back too, unless the player ran a scan of his own meanwhile.
    void DevPutBack()
    {
        Settings before = _devSettings!;
        _devSettings = null;
        _ctx.Log($"Dev: request done. It left the window at {_settings.X},{_settings.Y}, scale {_settings.Scale}, folded {_settings.Minimised}, " +
                 $"folded right {_settings.FoldedRight}; put back to {before.X},{before.Y}, scale {before.Scale}, folded {before.Minimised}, folded right {before.FoldedRight}.");
        if (_devSession is { } session)
        {
            _devSession = null;
            _devStarted.Clear();
            string history = _history.DevPutBack(session.History, session.Made);
            if (session.PlayerMade.Count > 0)
                // The player simulated meanwhile: the window shows his own run, which is not the request's to undo.
                _ctx.Log($"Dev: also put back: {history}. Current results and setup: left as they are, the player's own scan " +
                         $"({string.Join(", ", session.PlayerMade)}) ended during the request.");
            else PutBackWindow(session, history);
        }
        _settings = before;
        WriteSettings();
        // As OnLoad reads them.
        _state.FightAreas = _settings.Areas.ToHashSet();
        _state.BestAreas = _settings.BestAreas.ToHashSet();
        _state.Duration = _settings.Duration;
        _state.Sure = SimState.SureSizes.Contains(_settings.Sure) ? _settings.Sure : 0;
        _state.Unlimited = _settings.Unlimited;
        _state.BestMode = _settings.BestMode;
        _state.TrainSkillId = _settings.TrainSkill;
        _state.ShowEnemyCards = _settings.ShowEnemyCards;
        _state.Columns = SimColumns.Read(_settings.Columns);
        ReadUpgradeChoice();
        // The tab too, which stance=best and stance=fights click (SimViewStance.cs).
        _ctx.Log($"Dev: the choice of Find upgrades put back to {DevChoice(_state.UpgradeChoice)}, and the tab to {(_state.BestMode ? "Best for" : "Fights")}, as settings.json has it.");
        _state.WearSetup = _settings.WearSetupFirst;
        _state.Workers = _settings.Workers > 0 ? Math.Min(_settings.Workers, 8) : SimEngine.DefaultWorkers;
        if (_window is { IsAlive: true } window)
        {
            bool shown = window.Visible;
            Build();
            _window!.Visible = shown;
            SyncStripButton();
        }
    }

    // The current results, the line about the last run, the setup, the selection and the sort, as they were before
    // the request.
    void PutBackWindow(DevSession session, string history)
    {
        int results = _state.Results.Count;
        string setup = _state.SetupName, compare = DevCompareState(), stance = _state.StanceId == "" ? "as the game has it" : _state.StanceId;
        _state.Results.Clear();
        foreach (var (key, result) in session.Results) _state.Results[key] = result;
        (_state.Done, _state.Total, _state.Message, _state.Current) = (session.Done, session.Total, session.Message, "");
        _state.LoadoutId = session.LoadoutId;
        _state.Overrides.Clear();
        foreach (var (slot, item) in session.Overrides) _state.Overrides[slot] = item;
        _state.SpellId = session.SpellId;
        _state.StanceId = session.StanceId;
        _state.Selected = session.Selected;
        _state.Pinned = session.Pinned;
        _state.Viewing = session.Viewing != null && _history.Scans.Contains(session.Viewing) ? session.Viewing : null;
        _state.SortKey = session.SortKey;
        _state.SortDescending = session.SortDescending;
        (_state.Compare, _state.ShowingB, _state.Other) = session.Compare;
        string upgrades = DevPutBackUpgrades();   // SimUpgrades.cs
        BuildGear();
        _ctx.Log($"Dev: also put back: {history}. Current results: {results} put back to the {_state.Results.Count} from before. " +
                 $"Setup: {setup} put back to {_state.SetupName}; stance {stance} put back to {(_state.StanceId == "" ? "as the game has it" : _state.StanceId)}; " +
                 $"selected {_state.Selected ?? "-"}, sorted by {_state.SortKey}, list {_state.Viewing?.Title ?? "current"}. " +
                 $"Compare: {compare}; put back to {DevCompareState()}. Upgrades: {upgrades}.");
    }

    // cards.request (EnemyCards) photographs the game's enemy cards, which this window would cover. It closes
    // the window as its X does (Close: the window keeps its place, fold and size, and no setting is written) and,
    // at the end, opens it again as it was.
    bool _devCardsClosed;

    string DevCardsAside()
    {
        if (_window is not { IsAlive: true, Visible: true } window) return "";
        string before = DevCardsWindow(window);
        Close();
        _devCardsClosed = true;
        return $"closed the simulator window for the pictures, as its X does ({before}; now visible {window.Visible})";
    }

    string DevCardsBack()
    {
        if (!_devCardsClosed) return "";
        _devCardsClosed = false;
        if (_window is not { IsAlive: true } window) return "the simulator window was closed for the pictures and has been taken down since; it stays closed";
        window.Visible = true;
        SyncStripButton();
        return $"opened the simulator window again ({DevCardsWindow(window)}; settings.json says at {_settings.X},{_settings.Y}, folded {_settings.Minimised})";
    }

    static string DevCardsWindow(GameWindow window) =>
        $"folded {window.Minimised}, at {window.Position.X:0},{window.Position.Y:0}, {window.Root.Size.X:0}x{window.Root.Size.Y:0}, visible {window.Visible}";

    void DevClick(Button? button, string name)
    {
        if (!GodotObject.IsInstanceValid(button) || !button!.IsVisibleInTree())
        {
            _ctx.Log($"Dev: no visible {name} button");
            return;
        }
        Vector2 spot = _ctx.Tree.Root.GetFinalTransform() * button.GetGlobalRect().GetCenter();
        Input.ParseInputEvent(new InputEventMouseMotion { Position = spot, GlobalPosition = spot });
        Input.ParseInputEvent(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
        Input.ParseInputEvent(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = false });
    }
#endif
}
