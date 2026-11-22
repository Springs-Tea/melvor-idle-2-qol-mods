using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Entities.Astrology;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace AstrologyOverview;

/// <summary>
/// One list of every constellation the game has revealed: which has a node choice ready, which is closest to
/// its next unlock by study time, what that unlock needs and what every minor node gives, and what a locked one
/// is missing. A round button in the strip on the right edge opens it on the Astrology page. It only shows what
/// the game knows; a click on a row opens that constellation the way the player's own clicks on the game's list
/// would, and nothing else: no study is started, stopped or switched, no node is chosen.
/// </summary>
public partial class AstrologyOverviewMod : IMod
{
    const string Title = "Astrology Overview", ButtonName = "AstrologyOverviewButton";
    // The list is the size of the Item Encyclopedia's details column (EncyclopediaView.DetailsWidth, Height).
    const float ListWidth = 440, ListHeight = 620;
    readonly Tempo _tick = new(500), _search = new(1000);
    readonly Dictionary<AstrologyConstellation, OverviewRowView> _rows = new();
    readonly Dictionary<AstrologyConstellation, string?> _lockWords = new();
    readonly HashSet<string> _reported = new();
    ModContext _ctx = null!;
    Game? _game;
    AstrologyEntity? _astrology;
    AstrologyActiveView? _page;
    CurrentConstellationView? _current;
    ConstellationSelectView? _picker;
    HFlowContainer? _cards;
    GameWords _words = GameWords.Plain;
    Button? _opener;
    GameWindow? _window;
    Control? _column;
    ScrollContainer? _scroll;
    VBoxContainer? _list;
    Label? _empty;
    // The size the window was dragged to and where it was put, kept while the game runs (there is no settings
    // file); and the window's size at 100%, measured from the window itself.
    float _scale = 1f;
    Vector2? _position;
    Vector2 _fullSize;
    bool _stopped;

    public void OnLoad(ModContext ctx)
    {
        _ctx = ctx;
        ctx.Frame += OnFrame;
        ctx.Log("Ready. On the Astrology page, a round button on the right edge of the screen opens the overview.");
    }

    void OnFrame()
    {
        if (!_tick.Due()) return;
        try
        {
            Game? game = GameAccess.CurrentGame;
            if (game is not { IsSaveLoaded: true }) game = null;
            if (!ReferenceEquals(_game, game))
            {
                Detach();
                _game = game;
                _astrology = game?.Entities.GetAllObjects().OfType<AstrologyEntity>().FirstOrDefault();
#if DEVTOOLS
                DevReset();
#endif
            }
            if (_astrology == null) return;
            if (_page != null && (!GodotObject.IsInstanceValid(_page) || !_page.IsInsideTree())) Detach();
            if (_page == null && _search.Due() && GameAccess.FindGameView() is { } view &&
                GameAccess.FindNode<AstrologyActiveView>(view) is { } page)
                Attach(page);
            if (_page != null) UpdateStripButton();
            if (IsOpen) Refresh();
#if DEVTOOLS
            DevRequest();
#endif
        }
        catch (Exception e) { Stop(e); }
    }

    void Attach(AstrologyActiveView page)
    {
        _page = page;
        _current = GameAccess.FindNode<CurrentConstellationView>(page)
            ?? throw new InvalidOperationException("The current constellation view is missing.");
        _picker = GameAccess.FindNode<ConstellationSelectView>(page)
            ?? throw new InvalidOperationException("The constellation picker is missing.");
        _cards = Native.Cards(_picker);
        // The words are a nicety: if the screen changed in an update, plain ones do and the overview still works.
        try { _words = Native.ReadWords(_current); }
        catch (Exception e)
        {
            _words = GameWords.Plain;
            Report("The Astrology screen's own words could not be read (the game was updated?): plain ones are used. " + e.Message);
        }
        page.VisibilityChanged += PageVisibilityChanged;
        UpdateStripButton();
    }

    void PageVisibilityChanged()
    {
        try
        {
            if (_opener != null && GodotObject.IsInstanceValid(_opener)) _opener.Visible = _page!.IsVisibleInTree();
            if (_page?.IsVisibleInTree() != true) Close();
        }
        catch (Exception e) { Stop(e); }
    }

    // ---- the button in the game's strip of round buttons on the right edge (as ItemEncyclopediaMod.UpdateStripButton)

    void UpdateStripButton()
    {
        if (_opener != null && GodotObject.IsInstanceValid(_opener))
        {
            _opener.Visible = _page!.IsVisibleInTree();
            SyncStripButton();
            return;
        }
        _opener = null;
        if (GameAccess.FindGameView()?.FindChild("FavouritesContainer", true, false) is not BoxContainer strip)
        {
            Report("The game's widget strip was not found (the game was updated?): the overview has no button.");
            return;
        }
        // The strip may be the one of the character before: its button goes, this one takes its place.
        if (strip.GetNodeOrNull(ButtonName) is { } stale) { stale.Name = ButtonName + "Gone"; stale.QueueFree(); }
        _opener = new Button
        {
            Name = ButtonName,
            ThemeTypeVariation = "SidebarCategoryButton",
            CustomMinimumSize = new Vector2(36, 36),
            Theme = new GameStyle().TooltipTheme(),
            // The star the constellation screen draws a major node with: not the Astrology icon, which the game's
            // own buttons use.
            Icon = _words.Star ?? _astrology!.Icon,
            ExpandIcon = true,
            IconAlignment = HorizontalAlignment.Center,
            ToggleMode = true,
            TooltipText = "Astrology Overview: which constellation is closest to its next unlock",
        };
        _opener.Toggled += open => Click(() =>
        {
            if (open && !IsOpen) Open();
            else if (!open && IsOpen) Close();
            SyncStripButton();
        });
        strip.AddChild(_opener);
        // The style's own "pressed" look is a wider circle that is cut off at this size: keep the resting shape.
        if (_opener.GetThemeStylebox("normal") is StyleBoxFlat resting)
        {
            var lit = (StyleBoxFlat)resting.Duplicate();
            lit.BgColor = new Color("275124");
            _opener.AddThemeStyleboxOverride("pressed", lit);
            _opener.AddThemeStyleboxOverride("hover_pressed", lit);
        }
        _opener.Visible = _page!.IsVisibleInTree();
        SyncStripButton();
    }

    void SyncStripButton()
    {
        if (_opener != null && GodotObject.IsInstanceValid(_opener)) _opener.SetPressedNoSignal(IsOpen);
    }

    // ---- the window ------------------------------------------------------------------------------------------------

    bool IsOpen => _window is { IsAlive: true, Visible: true };

    void Open()
    {
        if (_page?.IsVisibleInTree() != true) { SyncStripButton(); return; }
        if (_window is not { IsAlive: true }) BuildWindow();
        _window!.Visible = true;
        Refresh();
        SyncStripButton();
    }

    void Close()
    {
        if (_window is { IsAlive: true }) _window.Visible = false;
        SyncStripButton();
    }

    // Built at the size the player chose, never larger than fits above the game's bottom bar (the house rule of
    // CombatSimMod.Build and ItemEncyclopediaMod.ShownScale): when the size asked for is too large, the window is
    // built once more at the largest that fits.
    void BuildWindow()
    {
        ReleaseRows();
        _window?.Free();
        float footer = FooterHeight();
        float scale = _scale;
        for (int pass = 0; ; pass++)
        {
            _window = new GameWindow(_ctx.Tree, Title, scale, _position, bottomInset: footer);
            BuildList(_window);
            _fullSize = _window.Root.GetCombinedMinimumSize() / _window.Style.Scale;
            float largest = LargestScale(footer);
            if (pass > 0 || _window.Style.Scale <= largest + 0.001f) break;
            _window.Free();
            scale = largest;
        }
        var window = _window;
        window.CloseRequested += Close;
        window.Moved += position => _position = position;
        window.ResizeRequested += size => Click(() => Resize(size));
        // The resize grip is a sibling of the frame and has its own tooltip.
        foreach (var control in window.Layer.GetChildren().OfType<Control>()) control.Theme = window.Root.Theme;

        // A screen too small even for the smallest size: the list gets shorter, so the X stays in reach.
        float room = _ctx.Tree.Root.GetVisibleRect().Size.Y - footer, over = window.Root.GetCombinedMinimumSize().Y - room;
        if (over > 0) _column!.CustomMinimumSize = new Vector2(_column.CustomMinimumSize.X, Math.Max(0, _column.CustomMinimumSize.Y - over));
    }

    void BuildList(GameWindow window)
    {
        var style = window.Style;
        // A titled column like the Item Encyclopedia's "Items" (EncyclopediaView.BuildItems), with room for the
        // scroll bar the way its filters have it (BuildFilters).
        _column = style.Column("Constellations", out VBoxContainer body);
        _column.CustomMinimumSize = new Vector2(style.Px(ListWidth), style.Px(ListHeight));
        _column.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        window.Body.AddChild(_column);
        _empty = style.Text("No constellations revealed yet.", GameStyle.TextGrey);
        _empty.Hide();
        body.AddChild(_empty);
        _scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        body.AddChild(_scroll);
        var room = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        room.AddThemeConstantOverride("margin_right", style.Px(10));
        _scroll.AddChild(room);
        _list = style.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 4);
        room.AddChild(_list);
    }

    // The corner was let go: the same window again at that size, never larger than fits.
    void Resize(float size)
    {
        _scale = Math.Min(size, LargestScale(FooterHeight()));
        if (_window is { IsAlive: true }) _position = _window.Position;
        int scroll = _scroll?.ScrollVertical ?? 0;
        BuildWindow();
        Open();
        _scroll!.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, scroll);
    }

    // How high the game's bar along the bottom of the screen is (CombatSimMod.FooterHeight).
    float FooterHeight()
    {
        if (GameAccess.FindGameView()?.FindChild("Footer", true, false) is Control footer)
            return footer.IsVisibleInTree() ? footer.GetGlobalRect().Size.Y : 0;
        Report("The game's bottom bar was not found (the game was updated?): the overview may lie over it.");
        return 0;
    }

    // The largest size that fits the screen above the bottom bar, in the steps of 5% the corner resizes in
    // (CombatSimMod.LargestScale), from the size the window measured at 100%.
    float LargestScale(float footer)
    {
        if (_fullSize.X <= 0 || _fullSize.Y <= 0) return GameWindow.MaxScale;
        Vector2 screen = _ctx.Tree.Root.GetVisibleRect().Size;
        float fits = Math.Min(screen.X / _fullSize.X, (screen.Y - footer) / _fullSize.Y);
        return Math.Clamp(MathF.Floor(fits * 20f) / 20f, GameWindow.MinScale, GameWindow.MaxScale);
    }

    // ---- the rows ----------------------------------------------------------------------------------------------------

    IEnumerable<ConstellationRow> ReadRows() => _astrology!.Config.Constellations
        .Where(c => c.DisplayRequirements?.AreMet != false)
        .Select(c => ConstellationRow.Read(c, _astrology.Action))
        .OrderBy(r => r.Status).ThenBy(r => r.Status == UnlockStatus.Study ? r.Milliseconds : 0);

    // The game's cards, which stay in its list while a constellation is being looked at.
    Dictionary<AstrologyConstellation, SelectConstellationView> NativeCards()
    {
        var cards = new Dictionary<AstrologyConstellation, SelectConstellationView>();
        foreach (Node child in _cards!.GetChildren())
            if (child is SelectConstellationView card && Native.BoundTo(card) is { } active)
                cards[active.Constellation] = card;
        return cards;
    }

    static bool CanPress(BaseButton button) => button.IsVisibleInTree() && !button.Disabled;

    // Shown in the game's list, whether that list is on the screen right now or not.
    bool InList(Control control)
    {
        for (Node? node = control; node != null && node != _picker; node = node.GetParent())
            if (node is CanvasItem { Visible: false }) return false;
        return true;
    }

    // A row opens its constellation when the game's own clicks can: its card is in the list, unlocked and
    // selectable, and the list is open or the constellation screen's button opens it.
    bool CanOpen(ConstellationRow row, SelectConstellationView card) =>
        row.Status != UnlockStatus.Locked && InList(card) && !InList(Native.LockPanel(card)) &&
        Native.Select(card) is { Disabled: false } select && InList(select) &&
        (_picker!.IsVisibleInTree() || CanPress(_current!.SelectConstellationButton));

    string? LockWords(AstrologyConstellation c, SelectConstellationView card)
    {
        if (!_lockWords.TryGetValue(c, out string? words)) _lockWords[c] = words = Native.LockWords(card);
        return words;
    }

    void Refresh()
    {
        var cards = NativeCards();
        var remaining = _rows.Keys.ToHashSet();
        ConstellationRow? first = null;
        int index = 0;
        foreach (var row in ReadRows())
        {
            var c = row.Constellation;
            remaining.Remove(c);
            if (!_rows.TryGetValue(c, out var view))
            {
                view = new OverviewRowView(_window!.Style, c, _words, Native.MinorCost(_current!));
                view.Button.Pressed += () => Click(() => Select(c));
                _list!.AddChild(view.Root);
                _rows.Add(c, view);
            }
            first ??= row;
            bool best = row.Status == first.Status && (row.Status == UnlockStatus.Ready ||
                row.Status == UnlockStatus.Study && row.Milliseconds == first.Milliseconds);
            cards.TryGetValue(c, out var card);
            bool canOpen = card != null && CanOpen(row, card);
            string? lockWords = row.Status == UnlockStatus.Locked && card != null ? LockWords(c, card) : null;
            view.Update(row, best, canOpen, lockWords);
            if (view.Root.GetIndex() != index) _list!.MoveChild(view.Root, index);
            index++;
        }
        foreach (var c in remaining)
        {
            _rows[c].Release();
            _rows[c].Root.QueueFree();
            _rows.Remove(c);
        }
        _empty!.Visible = index == 0;
        _scroll!.Visible = index > 0;
    }

    void ReleaseRows()
    {
        foreach (var view in _rows.Values) view.Release();
        _rows.Clear();
    }

    // ---- a click on a row ---------------------------------------------------------------------------------------

    // The player's own two clicks: the constellation screen's button back to the game's list, when the list is not
    // open, then the constellation's card. Whatever happens, the overview is not left open over an empty list.
    void Select(AstrologyConstellation c)
    {
        var cards = NativeCards();
        if (!cards.TryGetValue(c, out var card) || !CanOpen(ConstellationRow.Read(c, _astrology!.Action), card)) return;
        if (!_picker!.IsVisibleInTree())
        {
            var shown = _astrology.Action.SelectedConstellation?.Constellation;
            _current!.SelectConstellationButton.EmitSignal(BaseButton.SignalName.Pressed);
            if (!CanOpen(ConstellationRow.Read(c, _astrology.Action), card))
            {
                // Back to the constellation that was on the screen, as a click on its card would.
                if (shown != null && cards.TryGetValue(shown, out var back) && !InList(Native.LockPanel(back)) &&
                    Native.Select(back) is { Disabled: false } select)
                    select.EmitSignal(BaseButton.SignalName.Pressed);
                Close();
                Report("A constellation could not be opened from the overview: the game's list did not offer it.");
                return;
            }
        }
        Native.Select(card).EmitSignal(BaseButton.SignalName.Pressed);
        Close();
    }

    // ---- staying out of the way ---------------------------------------------------------------------------------

    // Clicks arrive outside OnFrame: a game member that is gone ends in the same one line there.
    void Click(System.Action action)
    {
        if (_stopped || GameAccess.IsComputingOffline) return;
        try
        {
            if (ReferenceEquals(GameAccess.CurrentGame, _game)) action();
        }
        catch (Exception e) { Stop(e); }
    }

    // Something the player should know about once, not on every frame.
    void Report(string message)
    {
        if (_reported.Add(message)) _ctx.Log(message);
    }

    void Detach()
    {
        if (GodotObject.IsInstanceValid(_page)) _page!.VisibilityChanged -= PageVisibilityChanged;
        Close();
        ReleaseRows();
        _window?.Free();
        if (GodotObject.IsInstanceValid(_opener)) _opener!.QueueFree();
        _window = null;
        _opener = null;
        _page = null;
        _picker = null;
        _current = null;
        _cards = null;
        _column = null;
        _list = null;
        _scroll = null;
        _empty = null;
        _lockWords.Clear();
    }

    // One line, and the mod is out of the way: the window and the button go, the game is left as it is.
    void Stop(Exception e)
    {
        if (_stopped) return;
        _stopped = true;
        _ctx.Frame -= OnFrame;
        _ctx.Log("Stopped (the game was updated?): " + e);
        try { Detach(); } catch { }
    }
}
