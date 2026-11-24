using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Firemaking;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace FiremakingRates;

/// <summary>
/// Which log gives what per hour. On the Firemaking page a round button in the strip on the right edge of the screen
/// opens a window with one line per log the page shows: its picture, its category, the time of a fire, and the logs
/// burnt, Firemaking XP, cost and gold of an hour, from the game's own numbers for each log (LogRates), counted the way
/// Recipe Rates counts the same log. The best number of XP and Gold is gold, of Time the least, a click on a column's
/// name sorts by it, the log the Firemaking page shows has a lit outline, and a line's tooltip has the whole hour. While
/// nothing burns, a click on a line opens that log on the Firemaking page, as the player's own clicks there would; while
/// a log burns, it selects nothing and only scrolls the page's list (ShowOnPage). Nothing is started or stopped from it.
/// Built the way Woodcutting Rates is (mods/WoodcuttingRates/WoodcuttingRatesMod.cs), itself Mining Rates' and Fishing
/// Rates' (mods/FishingRates).
/// </summary>
public partial class FiremakingRatesMod : IMod
{
    const string Title = "Firemaking Rates", ButtonName = "FiremakingRatesButton";
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
        /// <summary>The column the list is sorted by (RateColumn.Key), "name" for the order of the Firemaking page.</summary>
        public string Sort { get; set; } = RatesList.NameKey;
        public bool Descending { get; set; }
    }

    readonly Tempo _tick = new(500), _search = new(1000);
    readonly HashSet<string> _reported = new();
    ModContext _ctx = null!;
    Settings _settings = new();
    Game? _game;
    FiremakingEntity? _firemaking;
    BankEntity? _bank;
    FiremakingActiveView? _page;
    Control? _footer;
    Button? _opener;
    GameWindow? _window;
    RatesList? _list;
    GamePictures? _pictures;
    readonly BonusNames _bonusNames = new();
    // The window's size at 100%, measured from the window itself (AstrologyOverviewMod.BuildWindow).
    Vector2 _fullSize;
    // The player opened the window and has not closed it: it is there whenever the Firemaking page is.
    bool _wanted, _fitPending, _clicksOff, _stopped;
    // A click on a line: the log whose button the page's list scrolls to once the page has laid out the list again (two
    // drawn frames after the click), and the frame the click came in.
    ActiveFiremakingRecipe? _scrollTo;
    long _scrollAfter;
    // Where the window was put when it opened without a place of the player's (FirstPlace), or null.
    Vector2? _firstPlace;

    string SettingsFile => Path.Combine(_ctx.Directory, "settings.json");

    public void OnLoad(ModContext ctx)
    {
        _ctx = ctx;
        _settings = ModFiles.ReadJson<Settings>(SettingsFile, ctx.Log);
        _settings.Sort ??= RatesList.NameKey;   // "Sort": null in a hand-edited file
        ctx.Frame += OnFrame;
        ctx.Log("Ready. On the Firemaking page, a round button on the right edge of the screen opens the window.");
    }

    // Twice a second: which character, whether the Firemaking page is on the screen, and the window's numbers. A click on
    // a line that waits for the page to lay out its list is the only thing looked at on other frames.
    void OnFrame()
    {
        try
        {
            if (_scrollTo != null && (long)Engine.GetFramesDrawn() >= _scrollAfter) ScrollToCard();
#if DEVTOOLS
            // What a development request's own press left on the page is read on the frame it settles, not a tick later.
            if (_devSettle != null) DevSettleNow();
#endif
            if (!_tick.Due()) return;
            Game? game = GameAccess.CurrentGame;
            if (game is not { IsSaveLoaded: true }) game = null;
            if (!ReferenceEquals(_game, game))
            {
                Detach();
                _game = game;
                _firemaking = game?.Entities.GetAllObjects().OfType<FiremakingEntity>().FirstOrDefault();
                _bank = game != null && game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? bank) ? bank as BankEntity : null;
                _pictures = game != null ? new GamePictures(game.Data, Report) : null;
                if (game != null && _firemaking == null) Report("The game has no Firemaking skill (the game was updated?): the mod stands still.");
#if DEVTOOLS
                DevReset();
#endif
            }
            if (_firemaking == null) return;
            if (_page != null && (!GodotObject.IsInstanceValid(_page) || !_page.IsInsideTree())) LetGoOfPage();
            if (_page == null && _search.Due() && GameAccess.FindGameView() is { } view &&
                GameAccess.FindNode<FiremakingActiveView>(view) is { } page)
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

    void Attach(FiremakingActiveView page)
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
            // The game's own Firemaking icon, as Fishing Rates' button has the game's Fishing icon.
            Icon = (_firemaking!.IconPath is { Length: > 0 } path ? GameStyle.Load<Texture2D>(path) : null) ?? GameStyle.Icon("Firemaking"),
            ExpandIcon = true,
            IconAlignment = HorizontalAlignment.Center,
            ToggleMode = true,
            TooltipText = "Firemaking Rates: what an hour of burning each log gives",
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

    // The window is on the screen while it is wanted and the Firemaking page is; away from the page it steps aside, as
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

    // Every log the page shows, in the order of the Firemaking page: its categories in the order of the game's data
    // (FiremakingEntityConfig.Categories), as its category buttons stand, and in each the logs of that category in the
    // order the page's list binds them (ActiveFiremakingCategory.Recipes, CraftingRecipeSelect.Bind); the development
    // request compares both orders with the page's own.
    List<LogRates> ReadLogs()
    {
        var action = _firemaking!.Action;
        var listed = new List<ActiveFiremakingRecipe>();
        foreach (var category in action.Config.Categories)
        {
            if (category == null || !action.ActiveCategories.TryGetValue(category, out ActiveFiremakingCategory? active) || active == null) continue;
            foreach (ActiveFiremakingRecipe recipe in active.Recipes)
                if (recipe != null && LogRates.Listed(recipe, active) && !listed.Contains(recipe)) listed.Add(recipe);
        }
        bool categories = listed.Select(r => r.Category).Distinct(ReferenceEqualityComparer.Instance).Count() > 1;
        // Whether a log burns, read once for every line: what a click may do follows it (LogRates.ClickOpens).
        bool burning = LogRates.IsBurning(_firemaking, _game);
        var logs = new List<LogRates>();
        foreach (ActiveFiremakingRecipe recipe in listed)
        {
            LogRates log = LogRates.Read(recipe, _firemaking, _bank, Shown, categories, burning);
            log.Clickable = ClickChanges(recipe, burning, out CardSight? sight);
            log.Sight = sight;
            logs.Add(log);
        }
        return logs;
    }

    void Refresh()
    {
        if (_window is not { IsAlive: true } window || _list == null) return;
        var logs = ReadLogs();
        if (_page != null) _bonusNames.Read(_page, logs, Report);
        // A line more or less in sight (a log the game reveals after a level up): the window is fitted to it (it is in no
        // container and only grows by itself), then measured and, if it no longer fits above the bottom bar, built again
        // at the largest size that does, on the next tick, as when it is opened up (Fit).
        if (_list.Show(logs))
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
        var logs = ReadLogs();
        float scale = ShownScale(GameWindow.MaxScale);
        Vector2? at = _settings is { X: { } x, Y: { } y } ? new Vector2(x, y) : null;
        for (int pass = 0; ; pass++)
        {
            _window = new GameWindow(_ctx.Tree, Title, scale, at, canMinimise: true, minimised: _settings.Minimised, bottomInset: footer);
            _list = new RatesList(_window.Style, RateColumn.For(), _settings.Sort, _settings.Descending,
                _bonusNames.Of, (key, descending) => Click(() => Sorted(key, descending)), log => Click(() => ShowOnPage(log)));
            _window.Body.AddChild(_list.Root);
            _list.Show(logs);
            if (_window.Minimised) { _fitPending = true; break; }
            _fullSize = _window.Root.GetCombinedMinimumSize() / _window.Style.Scale;
            float largest = LargestScale(footer);
            if (pass > 0 || _window.Style.Scale <= largest + 0.001f) break;
            _window.Free();
            scale = largest;
        }
        var window = _window;
        // While the player has not placed the window himself, it opens where the logs' buttons in the page's list have the
        // most room beside it (FirstPlace), once its size is measured; as if dragged there, but nothing is saved, so his
        // own drag is still what is kept (Woodcutting Rates' review 4).
        _firstPlace = at == null && !window.Minimised ? FirstPlace(window.Root.GetCombinedMinimumSize()) : null;
        if (_firstPlace is { } first) window.MoveTo(first);
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

    // Asked for in issue #73, as the maintainer allowed it for Fishing Rates and Thieving Overview (22 Nov 2026) and for
    // the four rate windows of #70 to #73 (23 Nov 2026), within the rule of lesson 56 (the orchestrator's review of turn
    // 1): a click on a line opens that log on the Firemaking page, and does what the player's own clicks there would,
    // nothing more, and only while nothing burns. Whether a log burns is read again at the click itself, by the widest
    // test the members allow (LogRates.IsBurning).
    // - While nothing burns: through the game's public members that set what the page shows, as Fishing Rates' click does
    //   (FishingRatesMod.OpenOnPage): the log's category, as the page's category buttons open one
    //   (FiremakingActionComponent.SelectCategory), then the log, as its list of logs selects one (SelectRecipe); the page
    //   follows the component's own events for both (FiremakingActionView's handlers of SelectedCategoryChanged and
    //   SelectedRecipeChanged). Like the player's own click there, the selected log is then what the page's bonfire part
    //   shows and what its Start button would start; nothing is started. A locked log opens its category only, and a log
    //   whose category is locked nothing (LogRates.ClickOpens).
    // - While a log burns: no selecting member is called at all, since a selection may change the running fire (the code
    //   review of turn 1). A line of the category the page shows only scrolls the page's list to its log's button; any
    //   other line does nothing.
    // Then, once the page has laid its list out again, the list is scrolled so that the log's whole button is in sight
    // clear of this window (Woodcutting Rates' review: a button under the window is not seen; ScrollToCard). The page's
    // Start button and every control of the bonfire are never touched. The window stays open and where the player put it;
    // the lit line follows the page.
    void ShowOnPage(LogRates line)
    {
        if (!PageShown || _clicksOff) return;
        ActiveFiremakingRecipe active = line.Active;
        var action = _firemaking!.Action;
        // As it is now, not as it was at the last tick.
        bool burning = LogRates.IsBurning(_firemaking, _game);
        if (!ClickChanges(active, burning, out _)) return;
        try
        {
            Opens opens = LogRates.ClickOpens(active, action, burning);
            if (opens == Opens.Scroll)
            {
                // A log burns: the page's list is only scrolled to the log's button, nothing is selected.
                _scrollTo = active;
                _scrollAfter = (long)Engine.GetFramesDrawn() + 2;
                return;
            }
            if (opens == Opens.Nothing || active.Category == null || !action.ActiveCategories.TryGetValue(active.Category, out ActiveFiremakingCategory? category) || category == null) return;
            if (!ReferenceEquals(action.SelectedCategory, category))
            {
                action.SelectCategory(category);
                if (!ReferenceEquals(action.SelectedCategory, category))
                {
                    Report($"The Firemaking page did not take the category {category.Data.Id} of a clicked line (the game was updated?): the click opened nothing.");
                    return;
                }
            }
            if (opens == Opens.Log && !ReferenceEquals(action.SelectedRecipe, active))
            {
                action.SelectRecipe(active);
                if (!ReferenceEquals(action.SelectedRecipe, active))
                    Report($"The Firemaking page did not take the log {active.Recipe.Id} of a clicked line (the game was updated?): it shows its category only.");
            }
            _scrollTo = active;
            _scrollAfter = (long)Engine.GetFramesDrawn() + 2;
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            ClicksOff(e);
            return;
        }
        Refresh();
    }

    // A click on the log's line would change what the Firemaking page shows. While nothing burns: its category, when the
    // page shows another one; the log, when the page shows another log of the category; or, for the log the page shows
    // already (or a locked one of the category the page shows), the scroll of the page's list, when a scroll shows the
    // log's whole button clear of this window and it is not so now (Native.Place). While a log burns, only that scroll.
    // Read on every tick, for the line's look and its tooltip, and again at the click. sight: how the log's button stands
    // when the page shows its category (null when it does not), which the tooltip says when no scroll shows it clear.
    bool ClickChanges(ActiveFiremakingRecipe active, bool burning, out CardSight? sight)
    {
        sight = null;
        if (_clicksOff || !PageShown) return false;
        try
        {
            var action = _firemaking!.Action;
            bool categoryShown = ReferenceEquals(action.SelectedCategory?.Data, active.Category);
            if (Native.Card(_page!, active) is { } card && card.IsVisibleInTree()) sight = Native.Place(card, Cover(), out _);
            else if (categoryShown) sight = CardSight.NotShown;
            Opens opens = LogRates.ClickOpens(active, action, burning);
            if (opens == Opens.Nothing) return false;
            if (opens == Opens.Scroll) return sight == CardSight.Scroll;
            if (!categoryShown) return active.Category != null && action.ActiveCategories.ContainsKey(active.Category);
            if (opens == Opens.Log && !ReferenceEquals(action.SelectedRecipe, active)) return true;
            return sight == CardSight.Scroll;
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

    // Where the window opens while the player has not placed it himself (Woodcutting Rates' FirstPlace, from its review
    // 4: in the middle of the screen the window lay over the page's cards, and nearly no line could be clicked), keeping
    // clear, where that costs no line its room, the parts of the page where a click shows its result: the part with the
    // selected log and the bonfire's part (Native.Panels; the code review of turn 1, polish). Never over the strip of
    // round buttons: its right edge at most as far left of the strip as the strip is from the screen's edge. The places
    // tried, in this order: at the right side (as Woodcutting's), then with its right edge at the left edge of each such
    // part; at each, the two heights the page's list leaves for it (level with the list's top, or with its bottom above
    // the game's bottom bar). Of those, first the one where the most of the logs' buttons the list shows can be shown
    // whole beside it (Native.Place: in sight, or after a scroll), as Woodcutting's review 4 decided: the window must
    // first of all leave the lines clickable (the code review of turn 2, fault 2); of those equal in that, the one that
    // leaves the most of those parts untouched; then the first. Everything is measured from the page as it is drawn now.
    // Null when the list shows no button: the window then opens in the middle.
    Vector2? FirstPlace(Vector2 size)
    {
        if (!PageShown || _clicksOff || size.X <= 0 || size.Y <= 0) return null;
        try
        {
            var cards = Native.Cards(_page!).Select(c => c.Button).Where(c => c.IsVisibleInTree()).ToList();
            if (cards.Count == 0 || Native.ScrollOf(cards[0]) is not { } list) return null;
            Rect2 screen = _ctx.Tree.Root.GetVisibleRect(), page = Native.Drawn(list);
            float right = screen.End.X;
            if (GodotObject.IsInstanceValid(_opener) && _opener!.GetParent() is Control strip && strip.IsVisibleInTree())
            {
                Rect2 buttons = Native.Drawn(strip);
                right = buttons.Position.X - Math.Max(0, screen.End.X - buttons.End.X);
            }
            // Where a click shows its result. A game update that took these parts away only loses this preference.
            List<Rect2> panels;
            try { panels = Native.Panels(_page!); }
            catch (Exception e) when (e is MissingMemberException or TypeLoadException)
            {
                panels = new List<Rect2>();
                Report("The Firemaking page's part with the selected log or its bonfire's part was not found (the game was updated?): the window may open over them. " + e.Message);
            }
            float bottom = screen.End.Y - FooterHeight();
            float leftmost = screen.Position.X, rightmost = Math.Max(leftmost, right - size.X), lowest = Math.Max(screen.Position.Y, bottom - size.Y);
            var xs = new List<float> { rightmost };
            foreach (Rect2 panel in panels)
                if (panel.Position.X - size.X >= leftmost) xs.Add(Math.Min(rightmost, panel.Position.X - size.X));
            Vector2? best = null;
            (int Room, int Clear) most = (-1, -1);
            foreach (float x in xs)
                foreach (float top in new[] { page.Position.Y, Math.Min(page.End.Y, bottom) - size.Y })
                {
                    float y = Math.Clamp(top, screen.Position.Y, lowest);
                    var cover = new Rect2(x, y, size);
                    int clear = panels.Count(p => !p.Grow(-1).Intersects(cover));
                    int room = cards.Count(c => Native.Place(c, cover, out _) is CardSight.Clear or CardSight.Scroll);
                    if (room > most.Room || (room == most.Room && clear > most.Clear)) { most = (room, clear); best = new Vector2(x, y); }
                }
            return best?.Round();
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            ClicksOff(e);
            return null;
        }
    }

    // Two drawn frames after a click: the page has laid its list out for the category, and the list is scrolled as little
    // as it takes to show the log's whole button clear of this window (Native.Place): above or below the window when the
    // window lies across the button's column. A button the list has no room for there is brought into the list as far as
    // it can scroll (ScrollContainer.EnsureControlVisible), and from the next tick on its line's tooltip says that the
    // window is in the way.
    void ScrollToCard()
    {
        ActiveFiremakingRecipe? log = _scrollTo;
        _scrollTo = null;
        if (log == null || !PageShown || _clicksOff) return;
        try
        {
            if (Native.Card(_page!, log) is not { } card || !card.IsVisibleInTree() || Native.ScrollOf(card) is not { } scroll) return;
            CardSight sight = Native.Place(card, Cover(), out int to);
            if (sight == CardSight.Scroll) scroll.ScrollVertical = to;
            else if (sight != CardSight.Clear) scroll.EnsureControlVisible(card);
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            ClicksOff(e);
        }
    }

    // A part of the page the click needs is gone after a game update (FiremakingActiveView.ActionView,
    // FiremakingActionView.RecipeSelect, CraftingRecipeSelect.Buttons or SubcategoryViews, all private; or the public
    // SelectCategory and SelectRecipe): the lines show the numbers and open nothing.
    void ClicksOff(Exception e)
    {
        _clicksOff = true;
        _scrollTo = null;
        Report($"A click on a line opens nothing from now on: the Firemaking page has changed (the game was updated?): {e.Message}");
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
        _firemaking = null;
        _bank = null;
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
