using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Entities.Woodcutting;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace WoodcuttingRates;

/// <summary>
/// Which tree gives what per hour. On the Woodcutting page a round button in the strip on the right edge of the screen
/// opens a window with one line per tree the page shows: its picture, its category, and per hour the time per log, the
/// logs, Woodcutting XP and gold, from the game's own numbers for each tree (TreeRates); what the game gives no number
/// for is "-". The best number of each column is gold, a click on a column's name sorts by it, the tree being cut has a
/// lit outline, and a line's tooltip has the hour as far as it is counted. A click on a line shows that tree on the
/// Woodcutting page, as the player's own clicks there would (ShowOnPage); nothing is started or stopped from it.
/// Made the way Fishing Rates is (mods/FishingRates/FishingRatesMod.cs); its patterns are copied here.
/// </summary>
public partial class WoodcuttingRatesMod : IMod
{
    const string Title = "Woodcutting Rates", ButtonName = "WoodcuttingRatesButton";
    // The fill of the game's toggled green button (button_toggled_green.tres) as Fishing Rates read it: only used if the
    // game no longer has that style.
    static readonly Color LitGreen = new("275124");

    sealed class Settings
    {
        public float? X { get; set; }
        public float? Y { get; set; }
        /// <summary>The size the player dragged the window to; until then full size, or less on a small screen.</summary>
        public float? Scale { get; set; }
        /// <summary>The window is folded down to its title.</summary>
        public bool Minimised { get; set; }
        /// <summary>The column the list is sorted by (RateColumn.Key), "name" for the order of the Woodcutting page.</summary>
        public string Sort { get; set; } = RatesList.NameKey;
        public bool Descending { get; set; }
    }

    readonly Tempo _tick = new(500), _search = new(1000);
    readonly HashSet<string> _reported = new();
    ModContext _ctx = null!;
    Settings _settings = new();
    Game? _game;
    WoodcuttingEntity? _woodcutting;
    WoodcuttingActiveView? _page;
    Control? _footer;
    Button? _opener;
    GameWindow? _window;
    RatesList? _list;
    GamePictures? _pictures;
    readonly BonusNames _bonusNames = new();
    // The window's size at 100%, measured from the window itself (AstrologyOverviewMod.BuildWindow).
    Vector2 _fullSize;
    // The player opened the window and has not closed it: it is there whenever the Woodcutting page is.
    bool _wanted, _fitPending, _stopped;
    // The page's cards could not be read (the game was updated?): a click on a line only sets the page's category.
    bool _cardsOff;
    // The card a click on a line brings into sight, once the page has laid out its list again (ScrollToCard).
    WoodcuttingTreeSelectView? _scrollTo;
    long _scrollFrom;

    string SettingsFile => Path.Combine(_ctx.Directory, "settings.json");

    public void OnLoad(ModContext ctx)
    {
        _ctx = ctx;
        _settings = ModFiles.ReadJson<Settings>(SettingsFile, ctx.Log);
        _settings.Sort ??= RatesList.NameKey;   // "Sort": null in a hand-edited file
        ctx.Frame += OnFrame;
        ctx.Log("Ready. On the Woodcutting page, a round button on the right edge of the screen opens the window.");
    }

    // Twice a second: which character, whether the Woodcutting page is on the screen, and the window's numbers.
    void OnFrame()
    {
        try
        {
            if (_scrollTo != null) ScrollToCard();
            if (!_tick.Due()) return;
            Game? game = GameAccess.CurrentGame;
            if (game is not { IsSaveLoaded: true }) game = null;
            if (!ReferenceEquals(_game, game))
            {
                Detach();
                _game = game;
                _woodcutting = game?.Entities.GetAllObjects().OfType<WoodcuttingEntity>().FirstOrDefault();
                _pictures = game != null ? new GamePictures(game.Data, Report) : null;
                if (game != null && _woodcutting == null) Report("The game has no Woodcutting skill (the game was updated?): the mod stands still.");
#if DEVTOOLS
                DevReset();
#endif
            }
            if (_woodcutting == null) return;
            if (_page != null && (!GodotObject.IsInstanceValid(_page) || !_page.IsInsideTree())) LetGoOfPage();
            if (_page == null && _search.Due() && GameAccess.FindGameView() is { } view &&
                GameAccess.FindNode<WoodcuttingActiveView>(view) is { } page)
                Attach(page);
            if (_page != null)
            {
                UpdateStripButton();
                Sync();
            }
#if DEVTOOLS
            DevRequest();
            DevWatchRequest();
#endif
        }
        catch (Exception e) { Stop(e); }
    }

    void Attach(WoodcuttingActiveView page)
    {
        _page = page;
        page.VisibilityChanged += PageVisibilityChanged;
        UpdateStripButton();
    }

    // The page was freed: the window steps aside until it is found again, as it does on another page.
    void LetGoOfPage()
    {
        if (GodotObject.IsInstanceValid(_page)) _page!.VisibilityChanged -= PageVisibilityChanged;
        _page = null;
        _scrollTo = null;
        if (_window is { IsAlive: true }) _window.Visible = false;
        if (GodotObject.IsInstanceValid(_opener)) _opener!.Visible = false;
    }

    bool PageShown => _page != null && GodotObject.IsInstanceValid(_page) && _page.IsVisibleInTree();

    // The page came or went: the button and the window follow at once, not on the next tick.
    void PageVisibilityChanged()
    {
        if (_stopped || GameAccess.IsComputingOffline) return;
        try
        {
            UpdateStripButton();
            Sync();
        }
        catch (Exception e) { Stop(e); }
    }

    // ---- the button in the game's strip of round buttons on the right edge (FishingRatesMod.UpdateStripButton) ----------

    void UpdateStripButton()
    {
        if (_opener != null && GodotObject.IsInstanceValid(_opener))
        {
            _opener.Visible = PageShown;
            SyncStripButton();
            return;
        }
        _opener = null;
        if (GameAccess.FindGameView()?.FindChild("FavouritesContainer", true, false) is not BoxContainer strip)
        {
            Report("The game's widget strip was not found (the game was updated?): the window has no button.");
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
            // The game's own Woodcutting icon, as Fishing Rates' button has the game's Fishing icon.
            Icon = (_woodcutting!.IconPath is { Length: > 0 } path ? GameStyle.Load<Texture2D>(path) : null) ?? GameStyle.Icon("Woodcutting"),
            ExpandIcon = true,
            IconAlignment = HorizontalAlignment.Center,
            ToggleMode = true,
            TooltipText = "Woodcutting Rates: what an hour at each tree gives",
        };
        _opener.Pressed += () => Click(Toggle);
        strip.AddChild(_opener);
        // The style's own "pressed" look is a wider circle that is cut off at this size: keep the resting shape, in the
        // green of the game's toggled buttons, as Away Summary's button does (SummaryWindow.EnsureButton).
        if (_opener.GetThemeStylebox("normal") is StyleBoxFlat resting)
        {
            var lit = (StyleBoxFlat)resting.Duplicate();
            lit.BgColor = GameStyle.Load<StyleBoxFlat>("res://Assets/Styles/Button/button_toggled_green.tres")?.BgColor ?? LitGreen;
            _opener.AddThemeStyleboxOverride("pressed", lit);
            _opener.AddThemeStyleboxOverride("hover_pressed", lit);
        }
        _opener.Visible = PageShown;
        SyncStripButton();
    }

    // Lit while the window is open, not while it is closed or folded down to its title (CombatSimMod.SyncStripButton).
    void SyncStripButton()
    {
        if (_opener != null && GodotObject.IsInstanceValid(_opener)) _opener.SetPressedNoSignal(IsOpen && !_window!.Minimised);
    }

    // ---- the window --------------------------------------------------------------------------------------------------

    bool IsOpen => _wanted && _window is { IsAlive: true, Visible: true };

    // The round button: a closed window opens, a folded one opens up, an open one closes (CombatSimMod.ToggleWindow).
    void Toggle()
    {
        if (!IsOpen) Open();
        else if (_window!.Minimised) _window.SetMinimised(false);
        else Close();
        SyncStripButton();
    }

    void Open()
    {
        if (!PageShown) return;
        _wanted = true;
        Sync();
    }

    void Close()
    {
        _wanted = false;
        if (_window is { IsAlive: true }) _window.Visible = false;
        SyncStripButton();
    }

    // The window is on the screen while it is wanted and the Woodcutting page is; away from the page it steps aside, as
    // Fishing Rates' does, and comes back as it was. Its numbers are read again on every tick while it is open and not
    // folded, and never while it is put away.
    void Sync()
    {
        if (!_wanted || !PageShown)
        {
            if (_window is { IsAlive: true, Visible: true }) _window.Visible = false;
            SyncStripButton();
            return;
        }
        if (_window is not { IsAlive: true }) Build();
        var window = _window!;
        window.Visible = true;
        window.KeepAbove(FooterHeight());
        if (!window.Minimised && !window.Resizing)
        {
            if (_fitPending) { _fitPending = false; Fit(); }
            Refresh();
        }
        SyncStripButton();
    }

    // Every tree the page lists, in the order of the game's list of trees, the order the page shows them in (the
    // development request compares the two).
    List<TreeRates> ReadTrees()
    {
        var action = _woodcutting!.Action;
        var cards = Cards();
        var trees = new List<TreeRates>();
        foreach (WoodcuttingRecipe recipe in action.Config.Recipes)
        {
            if (recipe == null || !action.Trees.TryGetValue(recipe, out WoodcuttingTree? tree) || tree == null || !TreeRates.Listed(tree, action)) continue;
            trees.Add(TreeRates.Read(tree, _woodcutting, CardOf(tree, cards), Shown));
        }
        return trees;
    }

    void Refresh()
    {
        if (_window is not { IsAlive: true } window || _list == null) return;
        var trees = ReadTrees();
        if (_page != null) _bonusNames.Read(_page, trees, Report);
        // A line more or less in sight (a tree the game reveals after a level up, one the player hid), or the note under
        // the list come or gone: the window is fitted to it (it is in no container and only grows by itself), then
        // measured and, if it no longer fits above the bottom bar, built again at the largest size that does, on the
        // next tick, as when it is opened up (Fit).
        if (_list.Show(trees, Words.Note(trees)))
        {
            window.Root.ResetSize();
            window.KeepOnScreen();
            _fitPending = true;
        }
    }

    // Built at the size the player chose, never larger than fits above the game's bottom bar (the house rule of
    // AstrologyOverviewMod.BuildWindow): when the size asked for is too large, the window is built once more at the
    // largest that fits. A window that opens folded is measured when it is opened up again (Fit).
    void Build()
    {
        _window?.Free();
        float footer = FooterHeight();
        var trees = ReadTrees();
        float scale = ShownScale(GameWindow.MaxScale);
        Vector2? at = _settings is { X: { } x, Y: { } y } ? new Vector2(x, y) : null;
        for (int pass = 0; ; pass++)
        {
            _window = new GameWindow(_ctx.Tree, Title, scale, at, canMinimise: true, minimised: _settings.Minimised, bottomInset: footer);
            _list = new RatesList(_window.Style, RateColumn.For(), _settings.Sort, _settings.Descending,
                _bonusNames.Of, (key, descending) => Click(() => Sorted(key, descending)), tree => Click(() => ShowOnPage(tree)));
            _window.Body.AddChild(_list.Root);
            _list.Show(trees, Words.Note(trees));
            if (_window.Minimised) { _fitPending = true; break; }
            _fullSize = _window.Root.GetCombinedMinimumSize() / _window.Style.Scale;
            float largest = LargestScale(footer);
            if (pass > 0 || _window.Style.Scale <= largest + 0.001f) break;
            _window.Free();
            scale = largest;
        }
        var window = _window;
        window.CloseRequested += () => Click(Close);
        window.MinimisedChanged += folded => Click(() =>
        {
            _settings.Minimised = folded;
            Save();
            // Opened up again: measured, and fitted, on the next tick, once the fold has put it in its place.
            if (!folded) _fitPending = true;
            SyncStripButton();
        });
        window.Moved += position => Click(() =>
        {
            _settings.X = position.X;
            _settings.Y = position.Y;
            Save();
        });
        window.ResizeRequested += size => Click(() => Resize(size));
        // The resize grip is a sibling of the frame and has its own tooltip.
        foreach (Control control in window.Layer.GetChildren().OfType<Control>()) control.Theme = window.Root.Theme;
    }

    // The corner was let go: the same window again at that size, never larger than fits.
    void Resize(float size)
    {
        _settings.Scale = Math.Min(size, LargestScale(FooterHeight()));
        if (_window is { IsAlive: true }) { _settings.X = _window.Position.X; _settings.Y = _window.Position.Y; }
        Save();
        int scroll = _list?.ScrollAt ?? 0;
        Build();
        Sync();
        _list!.ScrollAt = scroll;
    }

    // An open window larger than the room above the bottom bar is built again at the largest size that fits there.
    void Fit()
    {
        if (_window is not { IsAlive: true, Minimised: false } window) return;
        _fullSize = window.Root.GetCombinedMinimumSize() / window.Style.Scale;
        float largest = LargestScale(FooterHeight());
        if (window.Style.Scale <= largest + 0.001f) return;
        _settings.X = window.Position.X;
        _settings.Y = window.Position.Y;
        int scroll = _list?.ScrollAt ?? 0;
        Build();
        _window!.Visible = true;
        _list!.ScrollAt = scroll;
    }

    // ---- a click on a line ---------------------------------------------------------------------------------------------

    // The page's card of every tree. Private (WoodcuttingActiveView.TreeViews): one that is gone after a game update
    // throws when it is first used; a click on a line then only sets the page's category filter and never scrolls, with
    // one line in the log.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TreeViews")]
    static extern Dictionary<WoodcuttingTree, WoodcuttingTreeSelectView>? TreeViews(WoodcuttingActiveView page);

    Dictionary<WoodcuttingTree, WoodcuttingTreeSelectView>? Cards()
    {
        if (_cardsOff || _page == null || !GodotObject.IsInstanceValid(_page)) return null;
        try { return ReadCards(_page); }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException or InvalidCastException)
        {
            _cardsOff = true;
            Report("The Woodcutting page's tree cards could not be read (the game was updated?): a click on a line only sets the page's category, and does not scroll. " + e.Message);
            return null;
        }
    }

    // In a method of its own: a member that is gone in a game version fails when the method is first run.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Dictionary<WoodcuttingTree, WoodcuttingTreeSelectView>? ReadCards(WoodcuttingActiveView page) => TreeViews(page);

    // What the page shows of a tree's card: shown under its category filter, and wholly in sight in its list.
    static TreeRates.Card? CardOf(WoodcuttingTree tree, Dictionary<WoodcuttingTree, WoodcuttingTreeSelectView>? cards)
    {
        if (cards == null || !cards.TryGetValue(tree, out WoodcuttingTreeSelectView? card) || !GodotObject.IsInstanceValid(card) || card.IsQueuedForDeletion())
            return null;
        bool shown = card.IsVisibleInTree();
        return new TreeRates.Card(shown, shown && ListOf(card) is { } list && list.GetGlobalRect().Grow(1).Encloses(card.GetGlobalRect()));
    }

    // The page's list of trees a card is in: the nearest scroll container around it on the page.
    static ScrollContainer? ListOf(Control card)
    {
        for (Node? node = card.GetParent(); node != null && node is not WoodcuttingActiveView; node = node.GetParent())
            if (node is ScrollContainer list) return list;
        return null;
    }

    // Asked for in issue #71, the way Fishing Rates opens a spot (FishingRatesMod.OpenOnPage, allowed by the maintainer,
    // 22 Nov 2026): a click on a line shows that tree on the Woodcutting page and does what the player's own clicks there
    // would, nothing more. The page has no tree that is selected without being cut, so "shows" means: the page's
    // category filter on the tree's category, as its own category buttons set it (the public
    // WoodcuttingActionComponent.SelectedCategory; the development request compares it with the page's own button),
    // when the tree's card is not shown under the filter now; and the page's list of trees scrolled until the card is in
    // sight, as the player's scrolling would. Nothing that cuts, selects, hides or changes a tree is called, and the
    // page's "Show hidden" is left as it is. The window stays open; the lit line follows the page.
    void ShowOnPage(TreeRates line)
    {
        if (!PageShown || _woodcutting == null) return;
        var action = _woodcutting.Action;
        WoodcuttingTree tree = line.Tree;
        if (ReferenceEquals(action.CuttingTree, tree)) return;
        var cards = Cards();
        // As it is now, not as it was at the last tick.
        TreeRates.Opens opens = TreeRates.ClickOpens(tree, action, CardOf(tree, cards));
        if (opens == TreeRates.Opens.Nothing) return;
        if (opens == TreeRates.Opens.Category) action.SelectedCategory = tree.Category;
        if (cards != null && cards.TryGetValue(tree, out WoodcuttingTreeSelectView? card))
        {
            _scrollTo = card;
            _scrollFrom = Engine.GetFramesDrawn();
        }
        Refresh();
    }

    // The card a click brings into sight, once the page has laid out its list: a card the filter has just shown has its
    // place only after the page's containers have sorted their children, which Godot does at the end of a frame
    // (ScrollContainer.EnsureControlVisible "will not work on a node that was just added during the same frame"); two
    // drawn frames later it has it. Once, not on a clock: nothing is walked.
    void ScrollToCard()
    {
        if (Engine.GetFramesDrawn() < _scrollFrom + 2) return;
        WoodcuttingTreeSelectView? card = _scrollTo;
        _scrollTo = null;
        if (card == null || !GodotObject.IsInstanceValid(card) || !card.IsVisibleInTree() || !PageShown) return;
        if (ListOf(card) is { } list)
        {
            list.EnsureControlVisible(card);
            Refresh();
        }
    }

    void Sorted(string key, bool descending)
    {
        _settings.Sort = key;
        _settings.Descending = descending;
        Save();
        Refresh();
    }

    // A picture of the game as the list shows it: none for the grey stand-in (GamePictures).
    Texture2D? Shown(Texture2D? picture) => _pictures?.Shown(picture);

    float ShownScale(float limit) => Math.Clamp(_settings.Scale ?? 1f, GameWindow.MinScale, Math.Max(GameWindow.MinScale, limit));

    // How high the game's bar along the bottom of the screen is (CombatSimMod.FooterHeight), looked for once per
    // screen as Skill Bonuses does: not the whole screen walked twice a second.
    float FooterHeight()
    {
        if (!GodotObject.IsInstanceValid(_footer)) _footer = GameAccess.FindGameView()?.FindChild("Footer", true, false) as Control;
        if (GodotObject.IsInstanceValid(_footer)) return _footer!.IsVisibleInTree() ? _footer.GetGlobalRect().Size.Y : 0;
        Report("The game's bottom bar was not found (the game was updated?): the window may lie over it.");
        return 0;
    }

    // The largest size that fits the screen above the bottom bar, in the steps of 5% the corner resizes in
    // (AstrologyOverviewMod.LargestScale), from the size the window measured at 100%.
    float LargestScale(float footer)
    {
        if (_fullSize.X <= 0 || _fullSize.Y <= 0) return GameWindow.MaxScale;
        Vector2 screen = _ctx.Tree.Root.GetVisibleRect().Size;
        float fits = Math.Min(screen.X / _fullSize.X, (screen.Y - footer) / _fullSize.Y);
        return Math.Clamp(MathF.Floor(fits * 20f) / 20f, GameWindow.MinScale, GameWindow.MaxScale);
    }

    void Save() => ModFiles.WriteJson(SettingsFile, _settings, _ctx.Log);

    // ---- staying out of the way --------------------------------------------------------------------------------------

    // Clicks arrive outside OnFrame: a game member that is gone ends in the same one line there. A click that comes
    // after another character was opened, before the next tick has seen it, does nothing (AstrologyOverviewMod.Click).
    void Click(Action action)
    {
        if (_stopped || GameAccess.IsComputingOffline) return;
        try
        {
            if (ReferenceEquals(GameAccess.CurrentGame, _game)) action();
        }
        catch (Exception e) { Stop(e); }
    }

    // Something the player should know about once, not on every tick.
    void Report(string message)
    {
        if (_reported.Add(message)) _ctx.Log(message);
    }

    // Another character, or none: nothing of the one before is kept but the settings.
    void Detach()
    {
#if DEVTOOLS
        try { DevUnlisten(); } catch { }
#endif
        LetGoOfPage();
        _window?.Free();
        if (GodotObject.IsInstanceValid(_opener)) _opener!.QueueFree();
        _window = null;
        _list = null;
        _opener = null;
        _footer = null;
        _wanted = _fitPending = false;
        _woodcutting = null;
        _pictures = null;
    }

    // One line, and the mod is out of the way: the window and the button go, the game is left as it is.
    void Stop(Exception e)
    {
        if (_stopped) return;
        _stopped = true;
        _ctx.Frame -= OnFrame;
        _ctx.Log("Stopped (the game was updated?): " + e);
#if DEVTOOLS
        try { DevAbandon(); } catch { }
#endif
        try { Detach(); } catch { }
    }
}
