using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Fishing;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace FishingRates;

/// <summary>
/// Which fishing spot gives what per hour. On the Fishing page a round button in the strip on the right edge of
/// the screen opens a window with one line per spot the game shows: the pictures of its fish, the time of a catch,
/// catches, Fishing XP, the gold the catches sell for and the bait used, per hour, from the game's own numbers for
/// each spot (SpotRates). The best number of each column is gold, a click on a column's name sorts by it, the spot
/// the Fishing page shows has a lit outline, and a line's tooltip has the whole hour. A click on a line opens that spot
/// on the Fishing page, as the player's own clicks there would (OpenOnPage); nothing is started or stopped from it.
/// </summary>
public partial class FishingRatesMod : IMod
{
    const string Title = "Fishing Rates", ButtonName = "FishingRatesButton";
    // The fill of the game's toggled green button (button_toggled_green.tres) as it was read: only used if the game
    // no longer has that style.
    static readonly Color LitGreen = new("275124");

    sealed class Settings
    {
        public float? X { get; set; }
        public float? Y { get; set; }
        /// <summary>The size the player dragged the window to; until then full size, or less on a small screen.</summary>
        public float? Scale { get; set; }
        /// <summary>The window is folded down to its title.</summary>
        public bool Minimised { get; set; }
        /// <summary>The column the list is sorted by (RateColumn.Key), "name" for the order of the Fishing page.</summary>
        public string Sort { get; set; } = RatesList.NameKey;
        public bool Descending { get; set; }
    }

    readonly Tempo _tick = new(500), _search = new(1000);
    readonly HashSet<string> _reported = new();
    ModContext _ctx = null!;
    Settings _settings = new();
    Game? _game;
    FishingEntity? _fishing;
    BankEntity? _bank;
    FishingActiveView? _page;
    Control? _footer;
    Button? _opener;
    GameWindow? _window;
    RatesList? _list;
    GamePictures? _pictures;
    readonly BonusNames _bonusNames = new();
    // The bait the window's columns were made for: another bait, or none, is another set of columns.
    string? _builtFor;
    // The window's size at 100%, measured from the window itself (AstrologyOverviewMod.BuildWindow).
    Vector2 _fullSize;
    // The player opened the window and has not closed it: it is there whenever the Fishing page is.
    bool _wanted, _fitPending, _stopped;

    string SettingsFile => Path.Combine(_ctx.Directory, "settings.json");

    public void OnLoad(ModContext ctx)
    {
        _ctx = ctx;
        _settings = ModFiles.ReadJson<Settings>(SettingsFile, ctx.Log);
        _settings.Sort ??= RatesList.NameKey;   // "Sort": null in a hand-edited file
        ctx.Frame += OnFrame;
        ctx.Log("Ready. On the Fishing page, a round button on the right edge of the screen opens the window.");
    }

    // Twice a second: which character, whether the Fishing page is on the screen, and the window's numbers.
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
                _fishing = game?.Entities.GetAllObjects().OfType<FishingEntity>().FirstOrDefault();
                _bank = game != null && game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? bank) ? bank as BankEntity : null;
                _pictures = game != null ? new GamePictures(game.Data, Report) : null;
                if (game != null && _fishing == null) Report("The game has no Fishing skill (the game was updated?): the mod stands still.");
#if DEVTOOLS
                DevReset();
#endif
            }
            if (_fishing == null) return;
            if (_page != null && (!GodotObject.IsInstanceValid(_page) || !_page.IsInsideTree())) LetGoOfPage();
            if (_page == null && _search.Due() && GameAccess.FindGameView() is { } view &&
                GameAccess.FindNode<FishingActiveView>(view) is { } page)
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

    void Attach(FishingActiveView page)
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

    // ---- the button in the game's strip of round buttons on the right edge (AstrologyOverviewMod.UpdateStripButton)

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
            // The game's own Fishing icon, as Combat Goals' button has the game's Combat icon.
            Icon = (_fishing!.IconPath is { Length: > 0 } path ? GameStyle.Load<Texture2D>(path) : null) ?? GameStyle.Icon("Fishing"),
            ExpandIcon = true,
            IconAlignment = HorizontalAlignment.Center,
            ToggleMode = true,
            TooltipText = "Fishing Rates: what an hour at each fishing spot gives",
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

    // The window is on the screen while it is wanted and the Fishing page is; away from the page it steps aside, as
    // Skill Bonuses' window does on a page without bonuses, and comes back as it was. Its numbers are read again on
    // every tick while it is open and not folded, and never while it is put away.
    void Sync()
    {
        if (!_wanted || !PageShown)
        {
            if (_window is { IsAlive: true, Visible: true }) _window.Visible = false;
            SyncStripButton();
            return;
        }
        if (_window is not { IsAlive: true } || _builtFor != BaitName()) Build();
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

    string? BaitName() => _fishing!.BaitComponent.SelectedBait?.DisplayName.GetPluralText(2);

    // Every spot the game shows, in the order of the Fishing page: its locations, and in each its spots.
    List<SpotRates> ReadSpots()
    {
        var action = _fishing!.Action;
        ActiveFishingRecipe? onPage = SpotRates.ShownOnPage(_fishing);
        var spots = new List<SpotRates>();
        foreach (FishingLocation location in _fishing.Config.Locations)
        {
            if (!action.Locations.TryGetValue(location, out ActiveFishingLocation? active)) continue;
            foreach (ActiveFishingRecipe spot in active.Recipes)
                if (SpotRates.Revealed(spot)) spots.Add(SpotRates.Read(spot, _fishing, _bank, onPage, Shown));
        }
        return spots;
    }

    void Refresh()
    {
        if (_window is not { IsAlive: true } window || _list == null) return;
        var spots = ReadSpots();
        if (_page != null) _bonusNames.Read(_page, _fishing!.Action, spots, Report);
        // A line more or less in sight (a spot the game reveals after a level up): the window is fitted to it (it
        // is in no container and only grows by itself), then measured and, if it no longer fits above the bottom
        // bar, built again at the largest size that does, on the next tick, as when it is opened up (Fit).
        if (_list.Show(spots))
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
        string? bait = BaitName();
        var spots = ReadSpots();
        float scale = ShownScale(GameWindow.MaxScale);
        Vector2? at = _settings is { X: { } x, Y: { } y } ? new Vector2(x, y) : null;
        for (int pass = 0; ; pass++)
        {
            _window = new GameWindow(_ctx.Tree, Title, scale, at, canMinimise: true, minimised: _settings.Minimised, bottomInset: footer);
            _list = new RatesList(_window.Style, RateColumn.For(bait), FishPictures(spots), _settings.Sort, _settings.Descending,
                _bonusNames.Of, (key, descending) => Click(() => Sorted(key, descending)), spot => Click(() => OpenOnPage(spot)));
            _window.Body.AddChild(_list.Root);
            _list.Show(spots);
            if (_window.Minimised) { _fitPending = true; break; }
            _fullSize = _window.Root.GetCombinedMinimumSize() / _window.Style.Scale;
            float largest = LargestScale(footer);
            if (pass > 0 || _window.Style.Scale <= largest + 0.001f) break;
            _window.Free();
            scale = largest;
        }
        _builtFor = bait;
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

    // ---- a click on a line -------------------------------------------------------------------------------------------

    // Asked for by the maintainer (22 Nov 2026): a click on a line takes the Fishing page to that spot, and does what
    // the player's own two clicks there would, nothing more. Through the game's public members that set what the page
    // shows: the spot's location, as the page's location buttons open one (FishingActionComponent.SetCurrentLocation),
    // then the spot, as its spot list selects one (ActiveFishingLocation.SelectRecipe). The development request
    // compares them with the game's own buttons. Fishing is not started or stopped. A locked spot opens its location
    // only, and a spot whose location is locked nothing (SpotRates.ClickOpens). The window stays open; the lit line
    // follows the page.
    void OpenOnPage(SpotRates spot)
    {
        if (!PageShown) return;
        ActiveFishingRecipe active = spot.Active;
        // As it is now, not as it was at the last tick.
        SpotRates.Opens opens = SpotRates.ClickOpens(active);
        var action = _fishing!.Action;
        if (opens == SpotRates.Opens.Nothing || !action.Locations.TryGetValue(active.Location, out ActiveFishingLocation? location)) return;
        if (!ReferenceEquals(action.CurrentLocation, location)) action.SetCurrentLocation(location);
        if (opens == SpotRates.Opens.Spot && !ReferenceEquals(location.SelectedRecipe, active)) location.SelectRecipe(active);
        Refresh();
    }

    void Sorted(string key, bool descending)
    {
        _settings.Sort = key;
        _settings.Descending = descending;
        Save();
        Refresh();
    }

    // Room at the start of a line for the most fish a spot has; a spot revealed later with more shows its first ones.
    static int FishPictures(List<SpotRates> spots) => spots.Count == 0 ? 1 : spots.Max(s => s.Pictures.Count);

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
        LetGoOfPage();
        _window?.Free();
        if (GodotObject.IsInstanceValid(_opener)) _opener!.QueueFree();
        _window = null;
        _list = null;
        _opener = null;
        _footer = null;
        _builtFor = null;
        _wanted = _fitPending = false;
        _fishing = null;
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
        try { DevUnwatch(); } catch { }
#endif
        try { Detach(); } catch { }
    }
}
