using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Entities.Woodcutting;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace WoodcuttingRates;

/// <summary>
/// Which tree gives what per hour. On the Woodcutting page a round button in the strip on the right edge of the screen
/// opens a window with one line per tree the page shows: its picture, its category, the time of a log, and the logs,
/// Woodcutting XP and gold of an hour, from the game's own numbers for each tree (TreeRates); what rests on a rule not
/// settled yet (TreeRates.LogsRule, HitsRule) is "-", and a note under the list says why. The best number of each column
/// is gold, a click on a column's name sorts by it, the tree the Woodcutting page shows has a lit outline, and a line's
/// tooltip has the hour as far as it is counted. A click on a line shows that tree on the Woodcutting page, clear of the
/// window, as the player's own clicks there would (ShowOnPage); nothing is started or stopped from it. Built the way
/// Mining Rates is (mods/MiningRates/MiningRatesMod.cs), itself Fishing Rates' (mods/FishingRates).
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
    bool _wanted, _fitPending, _clicksOff, _stopped;
    // The page's category button did not set its category: a click on a line no longer changes the filter.
    bool _filterOff;
    // A click on a line: the tree whose card the page's list scrolls to once the page has laid out its cards again
    // (two drawn frames after the click), and the frame the click came in.
    WoodcuttingTree? _scrollTo;
    long _scrollAfter;
    // The page's category filter, looked for once per page: not in the page's tree on every tick, and when it is not
    // there (a game update), that is remembered until the page is found again (_filterLooked).
    WoodcuttingCategoryFilterView? _filter;
    bool _filterLooked;

    string SettingsFile => Path.Combine(_ctx.Directory, "settings.json");

    public void OnLoad(ModContext ctx)
    {
        _ctx = ctx;
        _settings = ModFiles.ReadJson<Settings>(SettingsFile, ctx.Log);
        _settings.Sort ??= RatesList.NameKey;   // "Sort": null in a hand-edited file
        ctx.Frame += OnFrame;
        ctx.Log("Ready. On the Woodcutting page, a round button on the right edge of the screen opens the window.");
    }

    // Twice a second: which character, whether the Woodcutting page is on the screen, and the window's numbers. A click
    // on a line that waits for the page to lay out its cards is the only thing looked at on other frames.
    void OnFrame()
    {
        try
        {
            if (_scrollTo != null && (long)Engine.GetFramesDrawn() >= _scrollAfter) ScrollToCard();
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
        _filter = null;
        _filterLooked = false;
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

    // Every tree the game shows, in the order of the Woodcutting page: the order of the skill's trees in the game's data
    // (WoodcuttingEntityConfig.Recipes), each with its tree (WoodcuttingActionComponent.Trees); the development request
    // compares the two orders.
    List<TreeRates> ReadTrees()
    {
        var action = _woodcutting!.Action;
        var trees = new List<TreeRates>();
        foreach (WoodcuttingRecipe recipe in action.Config.Recipes)
        {
            if (recipe == null || !action.Trees.TryGetValue(recipe, out WoodcuttingTree? tree) || tree == null || !TreeRates.Listed(tree, action)) continue;
            TreeRates t = TreeRates.Read(tree, _woodcutting, Shown);
            t.Clickable = ClickChanges(tree, out CardSight? sight);
            t.Sight = sight;
            trees.Add(t);
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

    // Asked for in issue #71, as the maintainer allowed it for Fishing Rates and Thieving Overview (22 Nov 2026) and for
    // the four rate windows of #70 to #73 (23 Nov 2026): a click on a line shows that tree on the Woodcutting page, and
    // does what the player's own clicks there would, nothing more. The page has no tree that is selected without being
    // cut (a card's only button starts cutting: WoodcuttingTreeSelectView.CutButton), so "shows" is, as in Mining Rates
    // (MiningRatesMod.ShowOnPage): the page's category filter on the tree's category when it is on another one, as the
    // page's own category button sets it (CategoryFilter), and the page's list of trees scrolled so that the tree's whole
    // card is in sight clear of this window, beside it (Mining Rates' review 4: a card under the window is not seen),
    // once the page has laid its cards out again (ScrollToCard). Woodcutting is not started or stopped, no tree is
    // selected, hidden or shown, the page's "Show hidden" is left as it is, and the window stays open and where the
    // player put it. The tree being cut is no exception: its card can be out of sight too, and the click then only
    // scrolls to it. The development request compares the click with the page's own category button.
    void ShowOnPage(TreeRates line)
    {
        if (!PageShown || _clicksOff) return;
        WoodcuttingTree tree = line.Tree;
        var action = _woodcutting!.Action;
        // As it is now, not as it was at the last tick.
        if (!ClickChanges(tree, out _)) return;
        try
        {
            if (Native.Card(_page!, tree) is not { } card) return;
            if (!card.Visible && !OnFilter(action, tree) && !CategoryFilter(tree.Category)) return;
            _scrollTo = tree;
            _scrollAfter = (long)Engine.GetFramesDrawn() + 2;
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            ClicksOff(e);
            return;
        }
        Refresh();
    }

    // The page's filter shows this tree's category: all of them, or its own.
    static bool OnFilter(WoodcuttingActionComponent action, WoodcuttingTree tree) =>
        action.SelectedCategory == null || ReferenceEquals(action.SelectedCategory, tree.Category);

    // A click on the tree's line would change what the page shows. A card the page shows: when a scroll of its list shows
    // the whole card clear of this window and it is not so now (Native.Place); a card under another category: when the
    // page's own category button can bring it. Read on every tick, for the line's look and its tooltip, and again at the
    // click. sight: how a card the page shows stands (null for one it does not show), which the tooltip says when no
    // scroll shows it clear. A card in its list but under the window does not count as in sight.
    bool ClickChanges(WoodcuttingTree tree, out CardSight? sight)
    {
        sight = null;
        if (_clicksOff || !PageShown) return false;
        try
        {
            if (Native.Card(_page!, tree) is not { } card) return false;
            if (card.Visible)
            {
                if (!card.IsVisibleInTree()) return false;
                sight = Native.Place(card, Cover(), out _);
                return sight == CardSight.Scroll;
            }
            return !OnFilter(_woodcutting!.Action, tree) && CanPress(CategoryButton(tree.Category));
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            ClicksOff(e);
            return false;
        }
    }

    // The part of the screen this window covers, read from the window as it is drawn now (its frame, at the size it is
    // shown, wherever the player dragged it); null while it is not on the screen.
    Rect2? Cover() => _window is { IsAlive: true, Visible: true } window ? Native.Drawn(window.Root) : null;

    // The page's own button of a category (WoodcuttingCategoryFilterView), or null when the game has none. The filter is
    // looked for once per page; when it is not there, that is said once and remembered until the page is found again,
    // so a hidden card does not walk the page's tree twice a second.
    Button? CategoryButton(WoodcuttingRecipeCategory? category)
    {
        if (category == null || _filterOff) return null;
        if (!GodotObject.IsInstanceValid(_filter))
        {
            // Looked for when the page is new, or when the filter it had was freed: never twice for one that is not there.
            if (_filter == null && _filterLooked) return null;
            _filter = GameAccess.FindNode<WoodcuttingCategoryFilterView>(_page!);
            _filterLooked = true;
            if (_filter == null)
            {
                Report("The Woodcutting page's category filter was not found (the game was updated?): a click on a line no longer changes it.");
                return null;
            }
        }
        return Native.CategoryButtons(_filter!) is { } buttons && buttons.TryGetValue(category, out Button? button) && GodotObject.IsInstanceValid(button) ? button : null;
    }

    // A category button the player could click now: there, shown and not disabled.
    static bool CanPress(Button? button) => button != null && !button.Disabled && button.IsVisibleInTree();

    // The category filter on a category, the way the player's click on its button sets it: that very button pressed (a
    // toggle button first goes down, which lets the others of its group go, as a click does), as Mining Rates presses it
    // (MiningRatesMod.CategoryFilter) and Thieving Overview presses the page's own Select button. The filter is never set
    // behind the page's buttons (WoodcuttingActionComponent.SelectedCategory has a setter, which turn 1 used): the page's
    // buttons would then say another category than its cards show (Mining Rates' code review of turn 1). If the button
    // did not set it, one line in the log, and from then on a click on a line no longer changes the filter (_filterOff):
    // it only scrolls to a card the page shows. False when the filter is not on the category.
    bool CategoryFilter(WoodcuttingRecipeCategory? category)
    {
        if (category == null) return false;
        var action = _woodcutting!.Action;
        Button? button = CategoryButton(category);
        if (!CanPress(button)) return false;
        // How the buttons of its group are drawn now: put back as they were when the filter does not follow, so that no
        // button is left drawn pressed for a category the page does not show (the code review of turn 3, fault 2).
        var group = button!.ButtonGroup?.GetButtons().OfType<BaseButton>().ToList() ?? new List<BaseButton>();
        if (!group.Contains(button)) group.Add(button);
        var drawn = group.Select(b => (Button: b, Pressed: b.ButtonPressed)).ToList();
        if (button.ToggleMode && !button.ButtonPressed) button.ButtonPressed = true;
        button.EmitSignal(BaseButton.SignalName.Pressed);
        if (ReferenceEquals(action.SelectedCategory, category)) return true;
        foreach (var (b, pressed) in drawn)
            if (GodotObject.IsInstanceValid(b) && b.ButtonPressed != pressed) b.SetPressedNoSignal(pressed);
        _filterOff = true;
        Report("The Woodcutting page's category button did not set its category when pressed (the game was updated?): a click on a line no longer changes the filter.");
        return false;
    }

    // Two drawn frames after a click: the page has laid its cards out for the filter, and the list is scrolled as little
    // as it takes to show the whole card clear of this window (Native.Place): above or below the window when the window
    // lies across the card's column. A card the page has no room for there can only come after a click that changed the
    // filter (the line of a card the page shows is clickable only when there is room): it is brought into its list as far
    // as the list can scroll (ScrollContainer.EnsureControlVisible), and from the next tick on its line's tooltip says
    // that the window is in the way.
    void ScrollToCard()
    {
        WoodcuttingTree? tree = _scrollTo;
        _scrollTo = null;
        if (tree == null || !PageShown || _clicksOff) return;
        try
        {
            if (Native.Card(_page!, tree) is not { } card || !card.IsVisibleInTree() || Native.ScrollOf(card) is not { } scroll) return;
            CardSight sight = Native.Place(card, Cover(), out int to);
            if (sight == CardSight.Scroll) scroll.ScrollVertical = to;
            else if (sight != CardSight.Clear) scroll.EnsureControlVisible(card);
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            ClicksOff(e);
        }
    }

    // A part of the page the click needs is gone after a game update (WoodcuttingActiveView.TreeViews, or
    // WoodcuttingCategoryFilterView._categoryButtons, both private): the lines show the numbers and open nothing.
    void ClicksOff(Exception e)
    {
        _clicksOff = true;
        _scrollTo = null;
        Report($"A click on a line shows nothing from now on: the Woodcutting page has changed (the game was updated?): {e.Message}");
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
        // A development request's listeners on the game are taken off with the character they were hung on.
        try { DevUnwatchAll(); } catch { }
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
        // A request that runs ends with its done file, so the check does not wait its whole time for it.
        try { DevStopped(); } catch { }
#endif
        try { Detach(); } catch { }
    }
}
