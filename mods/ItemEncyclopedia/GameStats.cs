using System;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Components.Equipment;
using GamesByMalcs.Melvor2.CustomControls;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Views;
using GamesByMalcs.Melvor2.GameMenus;
using Godot;
using MelvorModLoader;

namespace ItemEncyclopedia;

/// <summary>
/// The game's own item stats window ("Comparing to: ... (Cycle Loadouts with TAB)"), for any item that can be
/// worn, also one the player has never had. It is asked for the way the game's own screens ask for it: the
/// bank's selected item and the skill pages' selected recipe raise ISharedModalRequestor.ViewItemStatsRequested
/// with an EquipmentItem (the item and its equipment data, no stack and no quantity), and the game's view, which
/// listens to every screen handed to GameView.HandleModalRequests, binds its ViewItemStatsModal to that item and
/// opens it. The window is the game's: its layout, its numbers, its comparison with what is worn. It has no
/// button that equips or changes anything.
/// </summary>
sealed class GameStats
{
    // The window the game opens, kept by its view (GameView.ViewItemStats). Private to the game: read here only.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ViewItemStats")]
    static extern ViewItemStatsModal? WindowOf(GameView view);

    // The timer a window of the game waits on when it is opened a moment later (PanelContainerPopup.OpenWithDelay).
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_openDelayTimer")]
    static extern ref Timer? DelayTimerOf(PanelContainerPopup popup);

    public enum Asked { Open, Waiting, NotOpened }

    readonly Action<string> _log;
    // Kept as Godot's types: only the methods below name the game's, so a game update that renamed them fails
    // inside a try of this class.
    Node? _view;
    Control? _window;
    // The window whose Opened and Closed this listens to.
    Control? _heard;
    bool _deaf, _noDelayTimer, _noClose;
    // An ask the game has not answered yet: the item, whether the page on screen was asked as well, and the frame.
    Item? _waitingFor;
    bool _askedPage;
    ulong _askedFrame;

    public GameStats(Action<string> log) => _log = log;

    /// <summary>The game says its window opened (PanelContainerPopup.Opened), inside its own call.</summary>
    public event Action? Opened;

    /// <summary>The game says its window closed (PanelContainerPopup.Closed), inside its own call.</summary>
    public event Action? Closed;

    /// <summary>A part of the game this needs is gone, or the game did not open its window: Details no longer offers it.</summary>
    public bool Gone { get; private set; }

    /// <summary>The game's window is open. Cheap: it looks at one node.</summary>
    public bool IsUp => _window != null && GodotObject.IsInstanceValid(_window) && _window.IsVisibleInTree();

    /// <summary>The canvas layer the game draws its window on; 0 is the game's own screen.</summary>
    public int Layer => _window != null && GodotObject.IsInstanceValid(_window) ? _window.GetCanvasLayerNode()?.Layer ?? 0 : 0;

    /// <summary>An ask the game has not answered yet: <see cref="Follow"/> is asked on the next frames.</summary>
    public bool Waiting => _waitingFor != null;

    /// <summary>Another character, or the mod stops: the view and the window are let go, and so are their events.</summary>
    public void Forget()
    {
        Hear(null);
        _view = null;
        _window = null;
        _waitingFor = null;
    }

    /// <summary>
    /// Finds the game's window, so it can be watched while the encyclopedia is open, and reads the bank's button
    /// for it from the game's scene (once a session). Called when the encyclopedia is opened, not on every frame.
    /// </summary>
    public void Look()
    {
        BankButton.Read(_log);
        if (Gone) return;
        try { Find(); }
        catch (Exception e) { TurnOff("was not found", e); }
    }

    /// <summary>
    /// Asks the game to open its window for the item. Open: it is up. Waiting: the game may open it a moment later,
    /// <see cref="Follow"/> says. NotOpened: it did not open, the log says why and Details no longer offers it.
    /// </summary>
    public Asked Open(Item item)
    {
        _waitingFor = null;
        if (Gone) return Asked.NotOpened;
        try
        {
            Find();
            if (_view == null) return Fail(item, "the game's view was not found");
            if (!AskView(item)) return Fail(item, "it cannot be worn");
            // A window the view did not have before the ask may be there now.
            if (_window == null) Find();
            if (_window == null) return Fail(item, "the game's view keeps no item stats window");
            return Answer(item, page: false);
        }
        catch (Exception e) { TurnOff("could not be opened", e); return Asked.NotOpened; }
    }

    /// <summary>
    /// Whether an ask that was not answered at once has been now. Until the frame of the ask is over (what the game
    /// put off to the end of that frame has then been done) and while the window's own opening timer runs, it waits.
    /// Then, once, the page on screen is asked the same, as the bank asks it; after that it did not open.
    /// </summary>
    public Asked Follow()
    {
        if (_waitingFor is not { } item) return IsUp ? Asked.Open : Asked.NotOpened;
        try
        {
            if (IsUp)
            {
                _waitingFor = null;
#if DEVTOOLS
                _log($"Dev: the game's item stats window opened for {item.Id} a moment after it was asked{(_askedPage ? ", through the page on screen" : "")}");
#endif
                return Asked.Open;
            }
            if (Engine.GetProcessFrames() <= _askedFrame || DelayRunning()) return Asked.Waiting;
            if (!_askedPage && AskPage(item))
            {
#if DEVTOOLS
                _log($"Dev: the game did not open its item stats window for {item.Id}; the page on screen was asked the same");
#endif
                return Answer(item, page: true);
            }
            return Fail(item, _askedPage ? "asked by the game's view and by the page on screen" : "asked by the game's view; no page on screen to ask");
        }
        catch (Exception e) { _waitingFor = null; TurnOff("could not be opened", e); return Asked.NotOpened; }
    }

    /// <summary>
    /// Closes the game's window with its own public PanelContainerPopup.Close, for a player who asked for the
    /// encyclopedia while it is open (F9, the round button). Its Closed, or the twice-a-second look, then brings the
    /// encyclopedia back. False when it could not be asked: one line in the log, once, and the window stays open for
    /// its own X and Esc.
    /// </summary>
    public bool CloseWindow()
    {
        if (_noClose || !IsUp) return false;
        try
        {
            CloseNow(_window!);
            return true;
        }
        catch (Exception e)
        {
            _noClose = true;
            _log($"The game's item stats window can no longer be closed by the encyclopedia (the game was updated?): F9 waits until it is closed with its X. {e.Message}");
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void CloseNow(Control window) => ((PanelContainerPopup)window).Close();

    Asked Answer(Item item, bool page)
    {
#if DEVTOOLS
        _log($"Dev: asked {(page ? "the page on screen" : "the game's view")} for its item stats window for {item.Id}; the window is {(IsUp ? "open" : "not open yet")}, on canvas layer {Layer}");
#endif
        if (IsUp) { _waitingFor = null; return Asked.Open; }
        _waitingFor = item;
        _askedPage = page;
        _askedFrame = Engine.GetProcessFrames();
        return Asked.Waiting;
    }

    Asked Fail(Item item, string why)
    {
        _waitingFor = null;
        TurnOff($"did not open for {item.Id}: {why}", null);
        return Asked.NotOpened;
    }

    // In methods of their own: a member of the game that is gone fails when they are first run, inside the tries.
    [MethodImpl(MethodImplOptions.NoInlining)]
    bool AskView(Item item)
    {
        if (_view is not GameView view || !item.Capabilities.TryGet(out EquipmentCapability? worn) || worn == null) return false;
        // Handed to the game's view only for the moment of asking, so nothing of the mod stays in the game.
        var asking = new Requestor();
        view.HandleModalRequests(asking);
        try { asking.Ask(new EquipmentItem(item, worn)); }
        finally { view.UnhandleModalRequests(asking); }
        return true;
    }

    // The page on screen (the bank, a skill, combat) is one of the game's own requestors: it asks the same way.
    [MethodImpl(MethodImplOptions.NoInlining)]
    bool AskPage(Item item)
    {
        if (_view is not GameView view || !item.Capabilities.TryGet(out EquipmentCapability? worn) || worn == null) return false;
        if (GameAccess.FindNode<BaseActiveEntityView>(view.EntityViewContainer) is not { } page) return false;
        page.RequestViewItemStatsModal(new EquipmentItemEventArgs(new EquipmentItem(item, worn)));
        return true;
    }

    // The game's own timer for a window it opens a moment later. Without it (gone after an update), no wait.
    bool DelayRunning()
    {
        if (_noDelayTimer || _window == null || !GodotObject.IsInstanceValid(_window)) return false;
        try { return DelayRunningNow(_window); }
        catch (Exception e)
        {
            _noDelayTimer = true;
            _log($"The game's item stats window no longer shows its opening timer (the game was updated?): a press waits one frame for it. {e.Message}");
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool DelayRunningNow(Control window) =>
        window is PanelContainerPopup popup && DelayTimerOf(popup) is { } timer && GodotObject.IsInstanceValid(timer) && !timer.IsStopped();

    // The view is a new one for every character the game opens; the window is the one it keeps. A view without its
    // window is not there yet; a member that is gone throws, and the feature is off.
    [MethodImpl(MethodImplOptions.NoInlining)]
    void Find()
    {
        if (_view != null && GodotObject.IsInstanceValid(_view) && _window != null && GodotObject.IsInstanceValid(_window)) return;
        GameView? view = GameAccess.FindGameView();
        _view = view;
        _window = view == null ? null : WindowOf(view);
        Hear(_window);
#if DEVTOOLS
        _log(_window != null ? $"Dev: the game's item stats window is {_window.GetType().Name} at {_window.GetPath()}, canvas layer {Layer}"
            : $"Dev: {(view == null ? "no game view" : "the game's view has no item stats window")} yet");
#endif
    }

    // Listens to the window's Opened and Closed. Without them (gone after an update) the encyclopedia still looks
    // twice a second whether the window is up.
    void Hear(Control? window)
    {
        if (ReferenceEquals(window, _heard) || _deaf) return;
        try
        {
            if (_heard != null && GodotObject.IsInstanceValid(_heard)) Listen(_heard, false);
            _heard = null;
            if (window != null) Listen(window, true);
            _heard = window;
        }
        catch (Exception e)
        {
            _deaf = true;
            _heard = null;
            _log($"The game's item stats window no longer says when it opens and closes (the game was updated?): the encyclopedia looks twice a second instead. {e.Message}");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void Listen(Control window, bool on)
    {
        var popup = (PanelContainerPopup)window;
        if (on) { popup.Opened += GameOpened; popup.Closed += GameClosed; }
        else { popup.Opened -= GameOpened; popup.Closed -= GameClosed; }
    }

    void GameOpened(object? sender, EventArgs e) => Opened?.Invoke();
    void GameClosed(object? sender, EventArgs e) => Closed?.Invoke();

    void TurnOff(string what, Exception? e)
    {
        if (Gone) return;
        Gone = true;
        _log($"The game's item stats window {what}; Details no longer offers it (the game was updated?).{(e == null ? "" : " " + e.Message)}");
    }

    /// <summary>
    /// A screen that asks the game for its item stats window, as the bank and the skill pages do. It never asks
    /// the game to close its other windows.
    /// </summary>
    sealed class Requestor : ISharedModalRequestor
    {
        public event EventHandler<EquipmentItemEventArgs>? ViewItemStatsRequested;

        public event EventHandler? CloseExclusiveModalsRequested
        {
            add { }
            remove { }
        }

        public void Ask(EquipmentItem item) => ViewItemStatsRequested?.Invoke(this, new EquipmentItemEventArgs(item));
    }

#if DEVTOOLS
    // Development only: what the window and the bank show, read back to compare with the pictures.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_StatsPanel")]
    static extern ItemStatsPanel? DevPanelOf(ViewItemStatsModal window);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ComparedItemName")]
    static extern Label? DevComparedNameOf(ItemStatsPanel panel);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_comparisonLoadout")]
    static extern ref GamesByMalcs.Melvor2.Components.Loadouts.LoadoutSlot? DevComparisonOf(ItemStatsPanel panel);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ViewStatsButton")]
    static extern Button? DevBankButtonOf(GamesByMalcs.Melvor2.Components.ItemEquipping.EquipItemOptions options);

    /// <summary>Development: the game's window, to read back what it shows.</summary>
    public Control? DevWindow => _window != null && GodotObject.IsInstanceValid(_window) ? _window : null;

    /// <summary>Development: the game's own X on its window (PanelContainerPopup.CloseButton).</summary>
    public Button? DevCloseButton => DevWindow is PanelContainerPopup popup ? popup.CloseButton : null;

    public string DevPath => DevWindow?.GetPath().ToString() ?? "-";

    /// <summary>Development: whether the game closes its window with Esc, and with a click beside it.</summary>
    public string DevCloses => DevWindow is PanelContainerPopup popup ? $"Esc {popup.AllowEscToClose}, a click outside {popup.AllowClickOutsideToClose}" : "-";

    public bool DevClickOutsideCloses => DevWindow is PanelContainerPopup { AllowClickOutsideToClose: true };

    /// <summary>Development: the loadout the window compares with, and the name it shows after "Comparing to:".</summary>
    public string DevComparison()
    {
        if (DevWindow is not ViewItemStatsModal window || DevPanelOf(window) is not { } panel) return "-";
        GamesByMalcs.Melvor2.Components.Loadouts.LoadoutSlot? loadout = DevComparisonOf(panel);
        return $"loadout {loadout?.Id ?? "none"} '{DevComparedNameOf(panel)?.Text}'";
    }

    /// <summary>Development: what the character wears, and the loadouts the window can compare with.</summary>
    public static string DevWorn(GamesByMalcs.Melvor2.Game game, out int loadouts)
    {
        loadouts = 0;
        if (!game.Entities.TryGetObjectById("MelvorBase:Combat", out GamesByMalcs.Melvor2.Entities.GameEntity? entity)
            || entity is not GamesByMalcs.Melvor2.Entities.Combat.CombatEntity combat) return "-";
        EquipmentComponent equipment = combat.Equipment;
        var compared = new System.Collections.Generic.List<string>();
        foreach (var slot in equipment.AvailableComparisonLoadouts) compared.Add(slot.Id);
        loadouts = compared.Count;
        return $"wears [{string.Join(", ", System.Linq.Enumerable.Select(equipment.EquippedItems, i => i.Id))}]; loadouts to compare [{string.Join(", ", compared)}]";
    }

    /// <summary>Development: the bank's own button for this window, when the bank shows a piece of equipment.</summary>
    public Button? DevBankButton()
    {
        if (_view is not GameView view) return null;
        var options = GameAccess.FindNode<GamesByMalcs.Melvor2.Components.ItemEquipping.EquipItemOptions>(view.EntityViewContainer);
        return options == null ? null : DevBankButtonOf(options);
    }

    /// <summary>Development: the bank's item buttons on screen, with their items, and whether each is selected.</summary>
    public System.Collections.Generic.List<(Control Button, Item Item, bool Selected)> DevBankItems()
    {
        var found = new System.Collections.Generic.List<(Control, Item, bool)>();
        if (_view is not GameView view) return found;
        void Walk(Node node)
        {
            if (node is GamesByMalcs.Melvor2.Components.Inventories.ItemStackButton { Stack: { Item: { } item } stack } button && button.IsVisibleInTree())
                found.Add((button, item, stack.IsSelected));
            foreach (Node child in node.GetChildren()) Walk(child);
        }
        Walk(view.EntityViewContainer);
        return found;
    }

    public static bool DevWearable(Item item) => item.Capabilities.TryGet(out EquipmentCapability? worn) && worn != null;
#endif
}
