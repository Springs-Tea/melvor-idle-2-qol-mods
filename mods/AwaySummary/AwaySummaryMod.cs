using System;
using System.IO;
using System.Linq;
using System.Threading;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Components.Toolbelt;
using GamesByMalcs.Melvor2.Components.ToolbeltEquipping;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Entities.OfflineProgress;
using GamesByMalcs.Melvor2.EventLog;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;
using EquipmentUse = GamesByMalcs.Melvor2.Components.Equipment.ItemConsumedEventArgs;
using BeltUse = GamesByMalcs.Melvor2.Components.Toolbelt.ItemConsumedEventArgs;

namespace AwaySummary;

public sealed partial class AwaySummaryMod : IMod
{
    static readonly int MaxMinutes = (int)TimeSpan.FromDays(1).TotalMinutes;

    sealed class Settings
    {
        public bool Enabled { get; set; } = true;
        public int MinutesAway { get; set; } = 15;
        public float Scale { get; set; } = 1;
        public float? X { get; set; }
        public float? Y { get; set; }
        public bool Folded { get; set; }
    }

    ModContext _ctx = null!;
    Settings _settings = new();
    string SettingsFile => Path.Combine(_ctx.Directory, "settings.json");
    // As ItemEncyclopediaMod: a change is written a little later, not on every step of the number box.
    readonly Tempo _look = new(500), _save = new(2000);
    bool _unsaved;
    Game? _game;
    BankEntity? _bank;
    Currency? _gold;
    CombatEntity? _combat;
    (ToolbeltComponent Belt, EventHandler<BeltUse> Heard)[] _belts = [];
    AwayPeriod? _away;
    Summary? _pending, _last;
    bool _focused, _stopped;
    int _mainThread, _interrupted;

    public void OnLoad(ModContext ctx)
    {
        _ctx = ctx;
        _mainThread = System.Environment.CurrentManagedThreadId;
        _settings = ModFiles.ReadJson<Settings>(SettingsFile, ctx.Log);
        _settings.MinutesAway = Math.Clamp(_settings.MinutesAway, 1, MaxMinutes);
        ctx.Tree.Root.FocusEntered += FocusChanged;
        ctx.Tree.Root.FocusExited += FocusChanged;
        ctx.Frame += Frame;
    }

    void Remember() => _unsaved = true;

    void SaveSettings()
    {
        if (!_unsaved) return;
        _unsaved = false;
        ModFiles.WriteJson(SettingsFile, _settings, _ctx.Log);
    }

    static bool GameHasFocus() => DisplayServer.GetWindowList().Any(id => DisplayServer.WindowIsFocused(id)
        && DisplayServer.WindowGetMode(id) != DisplayServer.WindowMode.Minimized);

    void FocusChanged() => Guard(UpdateFocus);

    void UpdateFocus()
    {
        bool focused = GameHasFocus();
        if (_focused == focused) return;
        _focused = focused;
        if (!CanShow || !_settings.Enabled || !ReferenceEquals(GameAccess.CurrentGame, _game))
        {
            _away = null;
            _pending = null;
            return;
        }
        if (!focused) _away = OfflineSummaryUp() ? null : new AwayPeriod(_game!, _bank!, _combat!, _gold!);
        else
        {
            if (_away != null && _away.Elapsed.TotalMinutes >= _settings.MinutesAway) _pending = _away.Finish();
            _away = null;
        }
    }

    bool CanShow => !GameAccess.IsComputingOffline && _game is { IsSaveLoaded: true, Flags.IsOffline: false }
        && Volatile.Read(ref _interrupted) == 0;

    // Also catches a Welcome Back window already on screen when the character's events were attached.
    static bool OfflineSummaryUp() => GameAccess.FindNode<OfflineSnapshotModal>() is { Visible: true };

    // OfflineRequested can come from the sleep detector outside the main thread.
    void Interrupt(object? sender, EventArgs args) => Interlocked.Exchange(ref _interrupted, 1);

    void Frame()
    {
        if (_stopped) return;
        // A window that was just built is measured on the frames after it was laid out (SummaryWindow.Settle).
        if (_settleSince != 0) Guard(Settle);
        if (_stopped || !_look.Due()) return;
        Guard(() =>
        {
            if (_unsaved && _save.Due()) SaveSettings();
            Game? now = GameAccess.CurrentGame;
            if (now is not { IsSaveLoaded: true }) now = null;
            if (!ReferenceEquals(now, _game)) SetCharacter(now);
            if (_game == null) return;

            if (Interlocked.Exchange(ref _interrupted, 0) != 0 || !CanShow)
            {
                _away = null;
                _pending = null;
                _focused = GameHasFocus();
#if DEVTOOLS
                EndRequest("Interrupted by offline progress.");
#endif
                Close();
                return;
            }
            EnsureButton();
            UpdateFocus();
            if (_pending != null && _focused)
            {
                Open(_pending);
                _pending = null;
            }
            FitToViewport();
#if DEVTOOLS
            DevRequest();
#endif
        });
    }

    void SetCharacter(Game? game)
    {
#if DEVTOOLS
        EndRequest("Character changed.");
#endif
        _away = null;
        _pending = null;
        _last = null;
        if (_game != null)
        {
            _game.OfflineRequested -= Interrupt;
            _game.OfflineProgress.SnapshotModalRequested -= Interrupt;
            _game.ActionManager.ActionStopped -= ActionStopped;
            _game.EventLog.EntryAdded -= EntryAdded;
        }
        if (_combat != null)
        {
            _combat.Action.PlayerDied -= PlayerDied;
            _combat.Equipment.AmmoConsumed -= EquipmentConsumed;
            _combat.Equipment.ItemConsumed -= EquipmentConsumed;
        }
        foreach (var (belt, heard) in _belts) belt.ItemConsumed -= heard;
        _belts = [];
        if (_bank != null) _bank.Inventory.ItemQuantityChanged -= ItemChanged;
        DropControls();
        _game = game;
        _bank = null;
        _combat = null;
        _gold = null;
        if (game == null) return;
        if (!game.Entities.TryGetObjectById("MelvorBase:Bank", out var bank) || bank is not BankEntity bankEntity ||
            !game.Entities.TryGetObjectById("MelvorBase:Combat", out var combat) || combat is not CombatEntity combatEntity ||
            !game.Data.GetDataRegistryOutput<Currency>().TryGetObjectById("MelvorBase:GoldPieces", out _gold))
            throw new InvalidOperationException("The bank, combat or gold data was unavailable.");
        _bank = bankEntity;
        _combat = combatEntity;
        // Each belt is heard on its own, so a use is listed under that belt's heading.
        _belts = game.Entities.GetAllObjects().OfType<IToolbeltEntity>().Select(e => e.Toolbelt).Distinct()
            .Select(belt => (belt, (EventHandler<BeltUse>)((_, e) => BeltConsumed(belt, e)))).ToArray();
        _focused = GameHasFocus();
        game.OfflineRequested += Interrupt;
        game.OfflineProgress.SnapshotModalRequested += Interrupt;
        game.ActionManager.ActionStopped += ActionStopped;
        game.EventLog.EntryAdded += EntryAdded;
        _combat.Action.PlayerDied += PlayerDied;
        _combat.Equipment.AmmoConsumed += EquipmentConsumed;
        _combat.Equipment.ItemConsumed += EquipmentConsumed;
        foreach (var (belt, heard) in _belts) belt.ItemConsumed += heard;
        _bank.Inventory.ItemQuantityChanged += ItemChanged;
    }

    bool Recording => System.Environment.CurrentManagedThreadId == _mainThread && !_stopped && (_away != null
#if DEVTOOLS
        || _sample != null
#endif
        ) && CanShow;

    void Record(Action<AwayPeriod> change) => Guard(() =>
    {
        if (_away != null) change(_away);
#if DEVTOOLS
        if (_sample != null) change(_sample);
#endif
    });

    void ItemChanged(object? sender, ItemQuantityChangedEventArgs e)
    {
        if (Recording) Record(p => p.BankChanged(e.Item, e.QuantityChange));
    }

    void EquipmentConsumed(object? sender, EquipmentUse e)
    {
        if (Recording) Record(p => p.WornUsed(e.Item));
    }

    void BeltConsumed(ToolbeltComponent belt, BeltUse e)
    {
        if (Recording) Record(p => p.BeltUsed(belt, e.Item));
    }

    void ActionStopped(object? sender, ActionStoppedEventArgs e)
    {
        if (Recording) Record(p => p.Stopped(e.Action));
    }

    void EntryAdded(object? sender, ItemAddedEventArgs<IEventLogEntry> e)
    {
        if (Recording && e.Item is ActionStoppedWithReasonEventLogEntry reason) Record(p => p.Reason(reason));
    }

    void PlayerDied(object? sender, PlayerDiedEventArgs e)
    {
        if (Recording) Record(p => p.Died(e.LostItem));
    }

    void Guard(Action action)
    {
        if (_stopped) return;
        try { action(); }
        catch (Exception e)
        {
            _stopped = true;
            _ctx.Log($"Stopped (the game was updated?): {e.Message}");
            _ctx.Tree.Root.FocusEntered -= FocusChanged;
            _ctx.Tree.Root.FocusExited -= FocusChanged;
            _ctx.Frame -= Frame;
            try { SetCharacter(null); }
            catch { DropControls(); }
        }
    }
}
