using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Hunting;
using GamesByMalcs.Melvor2.Settings;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace HuntingRates;

/// <summary>
/// Which animal gives what per hour. On the Hunting page a round button in the strip on the right edge of the screen
/// opens a window with one line per animal the page can show: its picture, its name, its location, the time of a catch,
/// and the catches, Hunting XP, gold and traps of an hour, from the game's own numbers for each animal (AnimalRates);
/// what rests on a rule not settled (AnimalRates.ChainRule, UseRule, the XP range the page writes) is "-", and a note
/// under the list says why. The best number of each column is gold, a click on a column's name sorts by it, the animal
/// the Hunting page's panel shows has a lit outline, and a line's tooltip has the game's numbers of every step of a hunt
/// and what a catch brings. A click on a line opens that animal on the Hunting page, as the player's own clicks there
/// would (ShowOnPage); nothing is started or stopped from it. Built the way Woodcutting Rates is
/// (mods/WoodcuttingRates/WoodcuttingRatesMod.cs), with Fishing Rates' click (mods/FishingRates, OpenOnPage).
/// </summary>
public partial class HuntingRatesMod : IMod
{
    const string Title = "Hunting Rates", ButtonName = "HuntingRatesButton";
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
        /// <summary>The column the list is sorted by (RateColumn.Key), "name" for the order of the Hunting page.</summary>
        public string Sort { get; set; } = RatesList.NameKey;
        public bool Descending { get; set; }
    }

    readonly Tempo _tick = new(500), _search = new(1000);
    readonly HashSet<string> _reported = new();
    ModContext _ctx = null!;
    Settings _settings = new();
    Game? _game;
    HuntingEntity? _hunting;
    BankEntity? _bank;
    HuntingActiveView? _page;
    Control? _footer;
    Button? _opener;
    GameWindow? _window;
    RatesList? _list;
    GamePictures? _pictures;
    readonly BonusNames _bonusNames = new();
    // The window's size at 100%, measured from the window itself (AstrologyOverviewMod.BuildWindow).
    Vector2 _fullSize;
    // The player opened the window and has not closed it: it is there whenever the Hunting page is.
    bool _wanted, _fitPending, _clicksOff, _stopped;
    // The page's own button that shows the hunt instead of the journal did not do so: a click no longer presses it.
    bool _huntViewOff;
    // A click on a line: the animal whose card the page's list shows clear of the window once the page has laid it out
    // (two drawn frames after the click), and the frame the click came in.
    ActiveHuntingAnimal? _scrollTo;
    long _scrollAfter;
    // Where the window was put when it opened without a place of the player's (FirstPlace), or null.
    Vector2? _firstPlace;

    // What the list reads from the page's own cards, kept per character: how the page draws an animal not discovered yet
    // (its name and picture as the card shows them), the range the page writes each animal's XP in (the multipliers its
    // XP view is bound with), and the picture the page shows for a spider while the game's spider setting is on. Read
    // while the page shows the cards (the animals of the location it shows); _looksOff once a member is gone.
    readonly Dictionary<ActiveHuntingAnimal, (string Name, Texture2D? Picture, Color Tint)> _looks = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<ActiveHuntingAnimal, (double Low, double High)> _xpRanges = new(ReferenceEqualityComparer.Instance);
    Texture2D? _spiderPicture;
    bool _looksOff;
    // The page's words for an animal that needs no tool, and the game's word for something not found yet, read once.
    string? _noTool, _hiddenWord;
    bool _wordsRead;

    string SettingsFile => Path.Combine(_ctx.Directory, "settings.json");

    public void OnLoad(ModContext ctx)
    {
        _ctx = ctx;
        _settings = ModFiles.ReadJson<Settings>(SettingsFile, ctx.Log);
        _settings.Sort ??= RatesList.NameKey;   // "Sort": null in a hand-edited file
        ctx.Frame += OnFrame;
        ctx.Log("Ready. On the Hunting page, a round button on the right edge of the screen opens the window.");
    }

    // Twice a second: which character, whether the Hunting page is on the screen, and the window's numbers. A click on a
    // line that waits for the page to lay out its cards is the only thing looked at on other frames.
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
                _hunting = game?.Entities.GetAllObjects().OfType<HuntingEntity>().FirstOrDefault();
                _bank = game != null && game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? bank) ? bank as BankEntity : null;
                _pictures = game != null ? new GamePictures(game.Data, Report) : null;
                if (game != null && _hunting == null) Report("The game has no Hunting skill (the game was updated?): the mod stands still.");
#if DEVTOOLS
                DevReset();
#endif
            }
            if (_hunting == null) return;
            if (_page != null && (!GodotObject.IsInstanceValid(_page) || !_page.IsInsideTree())) LetGoOfPage();
            if (_page == null && _search.Due() && GameAccess.FindGameView() is { } view &&
                GameAccess.FindNode<HuntingActiveView>(view) is { } page)
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

    void Attach(HuntingActiveView page)
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
            // The game's own Hunting icon, as Fishing Rates' button has the game's Fishing icon.
            Icon = (_hunting!.IconPath is { Length: > 0 } path ? GameStyle.Load<Texture2D>(path) : null) ?? GameStyle.Icon("Hunting"),
            ExpandIcon = true,
            IconAlignment = HorizontalAlignment.Center,
            ToggleMode = true,
            TooltipText = "Hunting Rates: what an hour at each animal gives",
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

    // The window is on the screen while it is wanted and the Hunting page is; away from the page it steps aside, as
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

    // Every animal the game shows, in the order of the Hunting page: its locations in the order of the game's data
    // (HuntingEntityConfig.Locations), and in each its animals (ActiveHuntingLocation.Animals); the development request
    // compares the two orders with the page's own location buttons and list.
    List<AnimalRates> ReadAnimals()
    {
        var action = _hunting!.Action;
        ReadPage();
        bool panelCovered = PanelCovered();
        var animals = new List<AnimalRates>();
        foreach (HuntingLocation location in _hunting.Config.Locations)
        {
            if (location == null || !action.Locations.TryGetValue(location, out ActiveHuntingLocation? active) || !AnimalRates.LocationListed(active)) continue;
            foreach (ActiveHuntingAnimal animal in active.Animals)
            {
                if (animal == null || !AnimalRates.Listed(animal)) continue;
                AnimalRates a = ReadAnimal(animal);
                a.Clickable = ClickChanges(a, out CardSight? sight);
                a.Sight = sight;
                a.PanelCovered = panelCovered;
                animals.Add(a);
            }
        }
        return animals;
    }

    void Refresh()
    {
        if (_window is not { IsAlive: true } window || _list == null) return;
        var animals = ReadAnimals();
        if (_page != null) _bonusNames.Read(_page, animals, Report);
        // A line more or less in sight (an animal the game reveals after a level up), or the note under the list come or
        // gone: the window is fitted to it (it is in no container and only grows by itself), then measured and, if it no
        // longer fits above the bottom bar, built again at the largest size that does, on the next tick, as when it is
        // opened up (Fit).
        if (_list.Show(animals, Words.Note(animals)))
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
        var animals = ReadAnimals();
        float scale = ShownScale(GameWindow.MaxScale);
        Vector2? at = _settings is { X: { } x, Y: { } y } ? new Vector2(x, y) : null;
        for (int pass = 0; ; pass++)
        {
            _window = new GameWindow(_ctx.Tree, Title, scale, at, canMinimise: true, minimised: _settings.Minimised, bottomInset: footer);
            _list = new RatesList(_window.Style, RateColumn.For(), _settings.Sort, _settings.Descending, _bonusNames, () => _noTool,
                (key, descending) => Click(() => Sorted(key, descending)), animal => Click(() => ShowOnPage(animal)));
            _window.Body.AddChild(_list.Root);
            _list.Show(animals, Words.Note(animals));
            if (_window.Minimised) { _fitPending = true; break; }
            _fullSize = _window.Root.GetCombinedMinimumSize() / _window.Style.Scale;
            float largest = LargestScale(footer);
            if (pass > 0 || _window.Style.Scale <= largest + 0.001f) break;
            _window.Free();
            scale = largest;
        }
        var window = _window;
        // While the player has not placed the window himself, it opens where the page's animals and its panel of the
        // animal selected stay in sight beside it (FirstPlace), once its size is measured; as if dragged there, but
        // nothing is saved, so his own drag is still what is kept (Woodcutting Rates' first place).
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

    // ---- what the list reads from the page -------------------------------------------------------------------------------

    // The cards the page's list holds now: how it draws an animal not discovered yet, the range it writes each animal's XP
    // in, and the picture it shows for a spider while the game's spider setting is on. Read from the page's own cards and
    // panel through their members (Native), never by walking the page's tree. Given up with one line when a member is gone.
    void ReadPage()
    {
        if (!_wordsRead)
        {
            _wordsRead = true;
            try { _noTool = Native.NoToolWords(null).Text is { Length: > 0 } words ? words.Trim() : null; }
            catch (Exception e) when (e is MissingMemberException or TypeLoadException or NullReferenceException) { _noTool = null; }
            _hiddenWord = Native.HiddenWord();
        }
        if (_looksOff || !PageShown) return;
        try
        {
            foreach (HuntingAnimalTrackingView card in Native.Cards(_page!))
            {
                if (Native.AnimalOf(card) is not { } animal) continue;
                if (Native.IconOf(card) is { } icon)
                {
                    if (animal.JournalEntry?.IsDiscovered == false)
                        _looks[animal] = (Native.NameOf(card)?.Text is { Length: > 0 } name ? name : _hiddenWord ?? NotFound,
                            icon.IsVisibleInTree() ? icon.Texture : null, icon.Modulate * icon.SelfModulate);
                    else _looks.Remove(animal);
                    // The picture the game's own control shows for a spider with the setting on: the one this list shows.
                    if (animal.Animal.IsScarySpider && SpiderSetting() && icon.Texture != null && !ReferenceEquals(icon.Texture, animal.Animal.Icon))
                        _spiderPicture = icon.Texture;
                    else if (_spiderPicture == null && icon.IsArachnophobiaModeIcon && SpiderPath(icon) is { Length: > 0 } path)
                        _spiderPicture = GameStyle.Load<Texture2D>(path);
                }
                if (Native.XpViewOf(card) is { } xp) ReadXpRange(xp, animal);
            }
            if (Native.TrackingView(_page!) is { } view && Native.Panel(view) is { } panel && panel.IsVisibleInTree() &&
                Native.XpViewOf(panel) is { } panelXp && _hunting!.Action.SelectedAnimal is { } selected)
                ReadXpRange(panelXp, selected);
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            _looksOff = true;
            Report("The Hunting page's list of animals could not be read (the game was updated?): an animal not discovered yet shows no " +
                $"picture, and the XP range of a catch is not read. {e.Message}");
        }
    }

    // The multipliers an XP view of the page is bound with, kept for the animal whose stats it shows.
    void ReadXpRange(GamesByMalcs.Melvor2.Components.Experience.ExperienceStatsXPView view, ActiveHuntingAnimal animal)
    {
        if (!ReferenceEquals(Native.XpOf(view), animal.Stats.Experience)) return;
        if (Native.XpLow(view) is { } low && Native.XpHigh(view) is { } high && double.IsFinite(low) && double.IsFinite(high) && high >= low && low >= 0)
            _xpRanges[animal] = (low, high);
    }

    // The path of the picture the game's own control shows for a spider (its exported _arachnophobiaModeIconPath), read
    // as a value through Godot; "" when it is not there.
    static string SpiderPath(GamesByMalcs.Melvor2.CustomControls.SettingsIcon icon)
    {
        try
        {
            Variant path = icon.Get("_arachnophobiaModeIconPath");
            return path.VariantType == Variant.Type.String ? path.AsString() : "";
        }
        catch (Exception) { return ""; }
    }

    // The game's setting for players who do not want to see spiders (InterfaceSettings.ArachnophobiaMode). When it cannot
    // be read it counts as on: no spider is ever shown by mistake.
    bool SpiderSetting()
    {
        try { return LocalSettingsManager.Settings?.Interface?.ArachnophobiaMode != false; }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException or NullReferenceException)
        {
            Report("The game's spider setting could not be read (the game was updated?): spiders are shown without their picture.");
            return true;
        }
    }

    // The name and picture a line shows, and the colour the picture is drawn in. An animal not discovered yet is named
    // and pictured as the page's own card draws it, once the page has shown that card; before, by the game's own word for
    // something not found yet (the journal's, AnimalSpeciesView.HiddenText) and no picture. A spider, while the game's
    // spider setting is on, has the picture the page shows instead, or none: the list never shows a spider then.
    (string Name, Texture2D? Picture, Color Tint) Look(ActiveHuntingAnimal active)
    {
        if (active.JournalEntry?.IsDiscovered == false)
            return _looks.TryGetValue(active, out var seen) ? (seen.Name, Shown(seen.Picture), seen.Tint) : (_hiddenWord ?? NotFound, null, Colors.White);
        HuntingAnimal animal = active.Animal;
        Texture2D? picture = animal.IsScarySpider && SpiderSetting() ? Shown(_spiderPicture) : Shown(animal.Icon);
        return (animal.DisplayName.Text, picture, Colors.White);
    }

    // An animal's hour, with what the list reads from the page for it.
    AnimalRates ReadAnimal(ActiveHuntingAnimal animal) =>
        AnimalRates.Read(animal, _hunting!, Look(animal), _xpRanges.TryGetValue(animal, out var range) ? range : null, InBank, _hiddenWord ?? NotFound);

    // The window's words for something not found yet, when the game's own (AnimalSpeciesView.HiddenText) cannot be read.
    const string NotFound = "Not found yet";

    long InBank(Item item)
    {
        try { return _bank?.Inventory.GetQuantity(item) ?? 0; }
        catch (Exception e) when (e is not (MissingMemberException or TypeLoadException)) { return 0; }
    }

    // ---- a click on a line ---------------------------------------------------------------------------------------------

    // Asked for in issue #72, as the maintainer allowed it for Fishing Rates and Thieving Overview (22 Nov 2026) and for
    // the four rate windows of #70 to #73 (23 Nov 2026): a click on a line opens that animal on the Hunting page, and does
    // what the player's own clicks there would, nothing more. The Hunting page has a selection that starts nothing: its
    // location buttons and its animal cards select, and the animal then shows in the page's panel for the animal
    // selected, whose own button starts the hunt (HuntingSelectedRecipeContainer.CreateRecipeButton, never pressed here).
    // So, as Fishing Rates' click selects a spot (FishingRatesMod.OpenOnPage): through the game's public members that set
    // what the page shows, the animal's location (HuntingActionComponent.SelectLocation), then the animal (SelectAnimal);
    // when the page shows the journal, first its own button that shows the hunt (HuntView); then the page's list scrolled
    // so that the animal's card is in sight clear of this window, once it is laid out (ScrollToCard). A locked animal
    // opens its location only, and an animal whose location is locked nothing (AnimalRates.ClickOpens). The game keeps
    // the animal selected apart from the one hunted (SelectedAnimal and CurrentAnimal, each with its own event), so the
    // click selects also while a hunt runs; the development request reads the hunt in progress before and after every
    // click (HUNT CHANGED), and compares the click with the game's own buttons. Nothing is started or stopped, the trap,
    // the bait and hunting without a trap are left as they are, and the window stays open where the player put it.
    void ShowOnPage(AnimalRates line)
    {
        if (!PageShown || _clicksOff) return;
        ActiveHuntingAnimal active = line.Active;
        var action = _hunting!.Action;
        // As it is now, not as it was at the last tick.
        AnimalRates.Opens opens = AnimalRates.ClickOpens(active);
        if (opens == AnimalRates.Opens.Nothing || active.Location == null) return;
#if DEVTOOLS
        // The game's events raised while the click runs are the click's own (DevClickEvents).
        _devInClick = true;
#endif
        try
        {
            if (JournalShown()) HuntView();
            if (!ReferenceEquals(action.SelectedLocation, active.Location)) action.SelectLocation(active.Location);
            if (opens == AnimalRates.Opens.Animal)
            {
                if (!ReferenceEquals(action.SelectedAnimal, active)) action.SelectAnimal(active);
                _scrollTo = active;
                _scrollAfter = (long)Engine.GetFramesDrawn() + 2;
            }
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            ClicksOff(e);
            return;
        }
        finally
        {
#if DEVTOOLS
            _devInClick = false;
#endif
        }
        Refresh();
    }

    // The page shows its journal instead of the hunt (HuntingTrackingView.JournalView on the screen).
    bool JournalShown() => PageShown && Native.TrackingView(_page!) is { } view && Native.Journal(view) is { } journal && journal.IsVisibleInTree();

    // The page's own button that shows the hunt instead of the journal, pressed the way a player's click presses it (a
    // toggle button goes down, which lets the journal's button of its group go, then its pressed signal), as Woodcutting
    // Rates presses the page's own category button (WoodcuttingRatesMod.CategoryFilter). When the page still shows the
    // journal, one line in the log, and from then on a click no longer presses it.
    void HuntView()
    {
        if (_huntViewOff || Native.TrackingView(_page!) is not { } view || Native.HuntButton(view) is not { } button ||
            button.Disabled || !button.IsVisibleInTree()) return;
        if (button.ToggleMode && !button.ButtonPressed) button.ButtonPressed = true;
        button.EmitSignal(BaseButton.SignalName.Pressed);
        if (!JournalShown()) return;
        _huntViewOff = true;
        Report("The Hunting page's own button that shows the hunt did not hide its journal (the game was updated?): a click on a line no longer presses it.");
    }

    // A click on the animal's line would change what the Hunting page shows: its location or the animal selected, the
    // hunt shown instead of the journal, or, for the animal selected already, a scroll of the page's list that shows its
    // card whole clear of this window (Native.Place). Read on every tick, for the line's look and its tooltip, and again
    // at the click. sight: how the card stands while the page shows the animal's location (null otherwise), which the
    // tooltip says when no scroll shows it clear.
    bool ClickChanges(AnimalRates a, out CardSight? sight)
    {
        sight = null;
        if (_clicksOff || !PageShown) return false;
        try
        {
            var action = _hunting!.Action;
            bool here = ReferenceEquals(action.SelectedLocation, a.Active.Location), journal = JournalShown() && !_huntViewOff;
            if (here && Native.Card(_page!, a.Active) is { } card && card.IsVisibleInTree()) sight = Native.Place(card, Cover(), out _);
            return a.Click switch
            {
                AnimalRates.Opens.Location => !here || journal,
                AnimalRates.Opens.Animal => !here || journal || !ReferenceEquals(action.SelectedAnimal, a.Active) || sight == CardSight.Scroll,
                _ => false,
            };
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

    // The page's panel of the animal selected, where a click's animal shows, lies under this window.
    bool PanelCovered()
    {
        if (_clicksOff || !PageShown || Cover() is not { } cover) return false;
        try { return Native.TrackingView(_page!) is { } view && Native.Panel(view) is { } panel && panel.IsVisibleInTree() && Native.Covered(panel, cover); }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            ClicksOff(e);
            return false;
        }
    }

    // Where the window opens while the player has not placed it himself (Woodcutting Rates' review 4: in the middle of the
    // page it left almost no line clickable). Of the four corners of the page's own view (its right side as far from the
    // strip of round buttons as the strip is from the screen's edge, so it never covers a round button; its left side;
    // level with its top, or with its bottom above the game's bottom bar), the one that leaves the page's panel of the
    // animal selected uncovered, then its location buttons, then the most of the cards of its list whole beside it
    // (Native.Place: in sight, or after a scroll); the right top when they are equal. Everything is measured from the
    // page as it is drawn now. Null when the page's parts are not there: the window then opens in the middle.
    Vector2? FirstPlace(Vector2 size)
    {
        if (!PageShown || _clicksOff || size.X <= 0 || size.Y <= 0) return null;
        try
        {
            if (Native.TrackingView(_page!) is not { } view || !view.IsVisibleInTree()) return null;
            var cards = Native.Cards(_page!).Where(c => c.IsVisibleInTree()).ToList();
            Control? panel = Native.Panel(view) is { } p && p.IsVisibleInTree() ? p : null;
            Control? places = Native.LocationSelect(view) is { } l && l.IsVisibleInTree() ? l : null;
            if (cards.Count == 0 && panel == null) return null;
            Rect2 screen = _ctx.Tree.Root.GetVisibleRect(), page = Native.Drawn(view);
            float right = screen.End.X;
            if (GodotObject.IsInstanceValid(_opener) && _opener!.GetParent() is Control strip && strip.IsVisibleInTree())
            {
                Rect2 buttons = Native.Drawn(strip);
                right = buttons.Position.X - Math.Max(0, screen.End.X - buttons.End.X);
            }
            float bottom = screen.End.Y - FooterHeight();
            float lowest = Math.Max(screen.Position.Y, bottom - size.Y), widest = Math.Max(screen.Position.X, right - size.X);
            Vector2? best = null;
            int most = -1;
            foreach (float left in new[] { right - size.X, page.Position.X })
                foreach (float top in new[] { page.Position.Y, Math.Min(page.End.Y, bottom) - size.Y })
                {
                    float x = Math.Clamp(left, screen.Position.X, widest), y = Math.Clamp(top, screen.Position.Y, lowest);
                    var cover = new Rect2(x, y, size);
                    int room = (panel != null && !Native.Covered(panel, cover) ? 10_000 : 0) + (places != null && !Native.Covered(places, cover) ? 1_000 : 0) +
                        cards.Count(c => Native.Place(c, cover, out _) is CardSight.Clear or CardSight.Scroll);
                    if (room > most) { most = room; best = new Vector2(x, y); }
                }
            return best?.Round();
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            ClicksOff(e);
            return null;
        }
    }

    // Two drawn frames after a click: the page has laid out its list for the location, and the list is scrolled as little
    // as it takes to show the animal's whole card clear of this window (Native.Place), as Woodcutting Rates scrolls to a
    // tree's card (WoodcuttingRatesMod.ScrollToCard). A card the page has no room for there is brought into its list as far
    // as the list can scroll (ScrollContainer.EnsureControlVisible), and from the next tick on its line's tooltip says that
    // the window is in the way. A card in no scrolling list is left where it is.
    void ScrollToCard()
    {
        ActiveHuntingAnimal? animal = _scrollTo;
        _scrollTo = null;
        if (animal == null || !PageShown || _clicksOff) return;
        try
        {
            if (Native.Card(_page!, animal) is not { } card || !card.IsVisibleInTree() || Native.ScrollOf(card) is not { } scroll) return;
            CardSight sight = Native.Place(card, Cover(), out int to);
            if (sight == CardSight.Scroll) scroll.ScrollVertical = to;
            else if (sight != CardSight.Clear) scroll.EnsureControlVisible(card);
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            ClicksOff(e);
        }
    }

    // A part of the page the click needs is gone after a game update (HuntingActiveView.TrackingView, the parts of
    // HuntingTrackingView, HuntingAnimalTrackingView.Animal, all private, or the public SelectLocation and SelectAnimal):
    // the lines show the numbers and open nothing.
    void ClicksOff(Exception e)
    {
        _clicksOff = true;
        _scrollTo = null;
        Report($"A click on a line opens nothing from now on: the Hunting page has changed (the game was updated?): {e.Message}");
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
        _hunting = null;
        _bank = null;
        _pictures = null;
        _looks.Clear();
        _xpRanges.Clear();
        _spiderPicture = null;
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
