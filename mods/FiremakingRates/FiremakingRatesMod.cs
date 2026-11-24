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
    // drawn frames after the click), and the frame the click came in; and the log to select then on its own button in
    // that list (null: none, while a log burns or for a locked log).
    ActiveFiremakingRecipe? _scrollTo, _selectTo;
    long _scrollAfter;
    // The page's own category button did not take its category, or a log's own button did not select its log (the game
    // was updated?): from then on a click no longer selects that part, said once.
    bool _categoryOff, _logButtonOff;
    // Where the window was put when it opened without a place of the player's (FirstPlace), or null, and how that place
    // and the first one tried scored, for the development request's "Dev: room".
    Vector2? _firstPlace;
    internal sealed record PlaceScore(Vector2 At, int Room, int Buttons, int MainClear, int Main, int OtherClear, int Other, string Covered);
    PlaceScore? _firstPlaceWon, _firstPlaceFirst;

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
        _scrollTo = _selectTo = null;
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
    // - While nothing burns: through the page's own controls, as the player's own clicks there select (the orchestrator's
    //   review 3: selecting through FiremakingActionComponent.SelectCategory left two category buttons drawn pressed):
    //   the log's category with the page's own button of it (CategoryPress, Woodcutting Rates' CategoryFilter: the toggle
    //   down, its signal, the buttons drawn again as they were when it does not take), then, once the page has laid out
    //   that category's list, the log with its own button in that list (SelectLog). Like the player's own click there,
    //   the selected log is then what the page's bonfire part shows and what its Start button would start; nothing is
    //   started. A locked log opens its category only, and a log whose category is locked nothing (LogRates.ClickOpens).
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
                if (_categoryOff || Native.CategoryButton(_page!, category) is not { } button || !CanPress(button)) return;
                if (!CategoryPress(button, category))
                {
                    _categoryOff = true;
                    Report($"The Firemaking page's own button of the category {category.Data.Id} did not open it (the game was updated?): a click on a line no longer opens another category.");
                    return;
                }
            }
            // The log on its own button, once the page has laid out the category's list (ScrollToCard).
            _selectTo = opens == Opens.Log && !ReferenceEquals(action.SelectedRecipe, active) ? active : null;
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
            // Another category: opened with the page's own button of it, which the player could press now.
            if (!categoryShown)
                return !_categoryOff && active.Category != null && action.ActiveCategories.TryGetValue(active.Category, out ActiveFiremakingCategory? category) &&
                    category != null && Native.CategoryButton(_page!, category) is { } button && CanPress(button);
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
    // 4: in the middle of the screen the window lay over the page's cards, and nearly no line could be clicked). Never
    // over the strip of round buttons (its right edge at most as far left of the strip as the strip is from the screen's
    // edge) nor over the bottom bar. The places tried: at the right side level with the page's list (Woodcutting's, the
    // first), then every place whose left or right edge meets an edge of the page's list or of a part of the page where a
    // click shows its result (Native.Parts), and whose top or bottom meets the top or bottom of one of them. Of those, by
    // the orchestrator's reviews 2 and 3:
    //  1. the most of the logs' buttons the list shows that can be shown whole beside it (Native.Place: in sight, or after
    //     a scroll): the lines must first of all stay clickable;
    //  2. then the most of the selected log's Input and Output left untouched, which a click selects the log to show;
    //  3. then the most of the burning part (its Start button) and the bonfire left untouched, where it can;
    //  4. then the first tried.
    // Everything is measured from the page as it is drawn now. Null when the list shows no button: the window then opens
    // in the middle. The winner and the first place's scores are kept for "Dev: room".
    Vector2? FirstPlace(Vector2 size)
    {
        _firstPlaceWon = _firstPlaceFirst = null;
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
            List<Native.Part> parts;
            try { parts = Native.Parts(_page!); }
            catch (Exception e) when (e is MissingMemberException or TypeLoadException)
            {
                parts = new List<Native.Part>();
                Report("The Firemaking page's Input, Output or burning part was not found (the game was updated?): the window may open over them. " + e.Message);
            }
            float bottom = screen.End.Y - FooterHeight();
            float leftmost = screen.Position.X, rightmost = Math.Max(leftmost, right - size.X), top = screen.Position.Y, lowest = Math.Max(top, bottom - size.Y);
            var edges = parts.Select(p => p.At).Prepend(page).ToList();
            var xs = new List<float> { rightmost };
            var ys = new List<float> { Math.Clamp(page.Position.Y, top, lowest) };
            foreach (Rect2 r in edges)
            {
                xs.AddRange([r.Position.X - size.X, r.End.X, r.Position.X, r.End.X - size.X]);
                ys.AddRange([r.Position.Y - size.Y, r.End.Y, r.Position.Y, Math.Min(r.End.Y, bottom) - size.Y]);
            }
            xs = xs.Select(x => (float)Math.Round(Math.Clamp(x, leftmost, rightmost))).Distinct().ToList();
            ys = ys.Select(y => (float)Math.Round(Math.Clamp(y, top, lowest))).Distinct().ToList();
            int mains = parts.Count(p => p.Main), others = parts.Count - mains;
            PlaceScore? best = null;
            foreach (float y in ys)
                foreach (float x in xs)
                {
                    var cover = new Rect2(x, y, size);
                    int room = cards.Count(c => Native.Place(c, cover, out _) is CardSight.Clear or CardSight.Scroll);
                    var hit = parts.Where(p => p.At.Grow(-1).Intersects(cover)).ToList();
                    var score = new PlaceScore(new Vector2(x, y), room, cards.Count, mains - hit.Count(p => p.Main), mains, others - hit.Count(p => !p.Main), others,
                        string.Join(", ", hit.Select(p => p.Name)));
                    _firstPlaceFirst ??= score;
                    if (best == null || (score.Room, score.MainClear, score.OtherClear).CompareTo((best.Room, best.MainClear, best.OtherClear)) > 0) best = score;
                }
            _firstPlaceWon = best;
            return best?.At;
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            ClicksOff(e);
            return null;
        }
    }

    // Two drawn frames after a click: the page has laid its list out for the category. The log is selected with its own
    // button (SelectLog), and the list is scrolled as little as it takes to show the log's whole button clear of this
    // window (Native.Place): above or below the window when the window lies across the button's column. A button the list
    // has no room for there is brought into the list as far as it can scroll (ScrollContainer.EnsureControlVisible), and
    // from the next tick on its line's tooltip says that the window is in the way.
    void ScrollToCard()
    {
        ActiveFiremakingRecipe? log = _scrollTo, select = _selectTo;
        _scrollTo = _selectTo = null;
        if (log == null || !PageShown || _clicksOff) return;
        try
        {
            if (select != null) SelectLog(select);
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

    // A category button the player could click now: there, shown and not disabled (Woodcutting Rates' CanPress).
    static bool CanPress(BaseButton button) => !button.Disabled && button.IsVisibleInTree();

    // The page's own button of a category pressed the way the player's click presses it (Woodcutting Rates'
    // CategoryFilter): a toggle button first goes down, which lets the others of its group go, as a click does; its pressed
    // signal then, unless the toggle alone opened the category. True when the page shows the category. When it does not,
    // every button of its group is drawn again as it was, so that no button is left drawn pressed for a category the page
    // does not show. Also the development request's put-back of a category (Dev.cs, DevSelectionBack).
    bool CategoryPress(BaseButton button, ActiveFiremakingCategory category)
    {
        var action = _firemaking!.Action;
        var group = button.ButtonGroup?.GetButtons().OfType<BaseButton>().ToList() ?? new List<BaseButton>();
        if (!group.Contains(button)) group.Add(button);
        var drawn = group.Select(b => (Button: b, Pressed: b.ButtonPressed)).ToList();
        if (button.ToggleMode && !button.ButtonPressed) button.ButtonPressed = true;
        if (!ReferenceEquals(action.SelectedCategory, category)) button.EmitSignal(BaseButton.SignalName.Pressed);
        if (ReferenceEquals(action.SelectedCategory, category)) return true;
        foreach (var (b, pressed) in drawn)
            if (GodotObject.IsInstanceValid(b) && b.ButtonPressed != pressed) b.SetPressedNoSignal(pressed);
        return false;
    }

    // The log selected with its own button in the page's list, the way the player's click on it selects it
    // (Native.PressLogButton), never while a log burns (read again here, lesson 56), and only on a button the player
    // could press now. A log whose button the list does not show now (a folded group), or one whose button did not select
    // it, is selected through the game's member the page follows (FiremakingActionComponent.SelectRecipe), which the
    // page drew right in check-3 (its list marked the log, its panel showed it); said once when the button failed.
    void SelectLog(ActiveFiremakingRecipe log)
    {
        var action = _firemaking!.Action;
        if (LogRates.IsBurning(_firemaking, _game) || !ReferenceEquals(action.SelectedCategory?.Data, log.Category) || ReferenceEquals(action.SelectedRecipe, log)) return;
        if (!_logButtonOff && Native.Card(_page!, log) is { } button && !button.Disabled && button.IsVisibleInTree())
        {
            // The button's own click handlers are private (BaseButtonContainer.Pressed): gone after a game update, they only
            // cost the button, not the click.
            try { Native.PressLogButton(button); }
            catch (Exception e) when (e is MissingMemberException or TypeLoadException) { Report("A log's button on the Firemaking page could not be pressed: " + e.Message); }
            if (ReferenceEquals(action.SelectedRecipe, log)) return;
            _logButtonOff = true;
            Report($"The Firemaking page's own button of the log {log.Recipe.Id} did not select it (the game was updated?): a click on a line selects a log through the game's own member from now on.");
        }
        action.SelectRecipe(log);
    }

    // A part of the page the click needs is gone after a game update (FiremakingActiveView.ActionView,
    // FiremakingActionView.RecipeSelect or _categoryButtons, CraftingRecipeSelect.Buttons or SubcategoryViews,
    // BaseButtonContainer.Pressed, all private; or the public SelectRecipe): the lines show the numbers and open nothing.
    void ClicksOff(Exception e)
    {
        _clicksOff = true;
        _scrollTo = _selectTo = null;
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
