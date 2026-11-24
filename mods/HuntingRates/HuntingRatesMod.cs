using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.CustomControls;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Hunting;
using GamesByMalcs.Melvor2.Settings;
using GamesByMalcs.Melvor2.Utils;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace HuntingRates;

/// <summary>
/// Which animal gives what per hour. On the Hunting page a round button in the strip on the right edge of the screen
/// opens a window with one line per animal the page can show: its picture, its name, its location, the time of a catch,
/// and the catches, Hunting XP, gold and traps of an hour, from the game's own numbers for each animal (AnimalRates), by
/// the way a hunt's links join as the silent watch measured it (AnimalRates.HuntRule); what rests on a number not known
/// for an animal (the steps an escape adds there, the page's surveying range, the XP range the page writes) is "-", and
/// a note under the list says why. The best number of each column is gold, a click on a column's name sorts by it, the
/// animal the Hunting page's panel shows has a lit outline, and a line's tooltip has the whole hour, link by link. A
/// click on a line opens that animal on the Hunting page, as the player's own clicks there would (ShowOnPage); nothing
/// is started or stopped from it. Built the way Woodcutting Rates is (mods/WoodcuttingRates/WoodcuttingRatesMod.cs),
/// with Fishing Rates' click (mods/FishingRates, OpenOnPage).
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
    // A page control a click on a line goes through did not do what its own click does (the view did not follow, the
    // location or the animal was not selected): from then on a click no longer presses it, said once.
    bool _huntViewOff, _locationOff, _cardOff;
    // A click on a line in steps, each once the page has laid itself out after the step before (two process frames: the
    // layout needs no drawing, so a minimised game does not hold it up): the hunt shown instead of the journal, then the
    // location, then the animal's card (_clickStep); the view judged once it settled (_viewCheck); the page's list
    // scrolled to the card (_scrollTo).
    sealed record ClickStep(ActiveHuntingAnimal Animal, AnimalRates.Opens Opens, int Stage, long After);
    ClickStep? _clickStep;
    (long After, bool Hunt, bool Journal)? _viewCheck;
    ActiveHuntingAnimal? _scrollTo;
    long _scrollAfter;
    // Where the window was put when it opened without a place of the player's (FirstPlace), or null.
    Vector2? _firstPlace;

    // What the list reads from the page's own cards, kept per character, for as long as the window's mod runs: how the page
    // draws an animal not discovered yet (its name and picture as the card shows them), the colour every card draws its
    // picture in (an animal never trapped is a black shape on the page), the range each card writes its animal's XP in
    // (the multipliers its XP view is bound with: the game keeps them in no member an animal has, only in the view,
    // ExperienceStatsXPView.Bind), and the picture the page shows for a spider while the game's spider setting is on.
    // Read while the page shows the cards (the animals of the location it shows); _looksOff once a member is gone.
    readonly Dictionary<ActiveHuntingAnimal, (string Name, Texture2D? Picture, Color Tint)> _looks = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<ActiveHuntingAnimal, (Color Tint, bool NeverTrapped)> _tints = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<ActiveHuntingAnimal, (double Low, double High)> _xpRanges = new(ReferenceEqualityComparer.Instance);
    // The most surveys a hunt takes as the page's panel writes it for an animal (its surveying range, high end over low
    // end), kept per animal and only ever used for that animal (review 6, fault 2). _panelAnimal: the animal selected at
    // the last read, so a range is taken only once the same animal has stayed selected from one read to the next.
    readonly Dictionary<ActiveHuntingAnimal, double> _surveysMost = new(ReferenceEqualityComparer.Instance);
    ActiveHuntingAnimal? _panelAnimal;
    // A trap's bonuses after a failed try could not be read once: said in one line, and no catch is counted with a trap
    // that has them from then on (FailBonuses).
    bool _failBonusesOff;
    // The tool as each card writes it (HuntingAnimalTrackingView.TrackingTool), for the tooltip (review 1, fault 10).
    readonly Dictionary<ActiveHuntingAnimal, string> _toolWords = new(ReferenceEqualityComparer.Instance);
    Texture2D? _spiderPicture;
    // _spiderUnread: the game's spider setting could not be read once, and counts as on from then (no exception per tick).
    bool _looksOff, _spiderUnread;
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
            long frame = (long)Engine.GetProcessFrames();
            if (_viewCheck is { } check && frame >= check.After) ViewChecked();
#if DEVTOOLS
            // A development request's put-back of the animal, two process frames after its location's, as the click's own
            // steps (DevAnimalBackSoon).
            if (_devAnimalBackFrame >= 0 && frame >= _devAnimalBackFrame) DevAnimalBackSoon();
            // The silent watch closes a frame with an event as soon as it is past, and reads the game's interval between
            // events (DevWatchFrame); nothing while no watch runs.
            if (_devHunts != null && !GameAccess.IsComputingOffline) DevWatchFrame(frame);
#endif
            if (_clickStep is { } step && frame >= step.After) ClickNext();
            if (_scrollTo != null && frame >= _scrollAfter) ScrollToCard();
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
        _clickStep = null;
        _viewCheck = null;
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
                a.ToolWords = _toolWords.GetValueOrDefault(animal);
                a.Clickable = ClickChanges(a);
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
                    // The colour the card draws the picture in, with whether the animal had been trapped then (Look).
                    _tints[animal] = (icon.Modulate * icon.SelfModulate, NeverTrapped(animal));
                    // The picture the game's own control shows for a spider with the setting on: the one this list shows.
                    if (animal.Animal.IsScarySpider && SpiderSetting() && icon.Texture != null && !ReferenceEquals(icon.Texture, animal.Animal.Icon))
                        _spiderPicture = icon.Texture;
                    else if (_spiderPicture == null && icon.IsArachnophobiaModeIcon && SpiderPath(icon) is { Length: > 0 } path)
                        _spiderPicture = GameStyle.Load<Texture2D>(path);
                }
                if (Native.XpViewOf(card) is { } xp) ReadXpRange(xp, animal);
                if (Native.ToolOf(card)?.Text is { Length: > 0 } tool) _toolWords[animal] = StringUtils.StripBBCode(tool).Trim();
            }
            ActiveHuntingAnimal? selected = _hunting!.Action.SelectedAnimal;
            if (Native.TrackingView(_page!) is { } view && Native.Panel(view) is { } panel && panel.IsVisibleInTree() && selected != null)
            {
                if (Native.XpViewOf(panel) is { } panelXp) ReadXpRange(panelXp, selected);
                // Only once the same animal has stayed selected since the read before (half a second): the panel has been
                // drawn for it by then. Its low end being this animal's survey time cannot tell animals apart, since all
                // of them survey alike, so it only guards against a panel not filled in.
                if (ReferenceEquals(selected, _panelAnimal) && Native.SurveyOf(panel) is { } survey) ReadSurveyRange(survey, selected);
            }
            _panelAnimal = selected;
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            _looksOff = true;
            Report("The Hunting page's list of animals could not be read (the game was updated?): an animal not discovered yet shows no " +
                $"picture, and the XP range of a catch and the surveys of a hunt are not read. {e.Message}");
        }
    }

    // The panel's surveying range, kept for the animal it shows (ReadPage reads it only once that animal has stayed
    // selected from one read to the next), when its low end is that animal's time of a survey and the range is whole.
    void ReadSurveyRange(GamesByMalcs.Melvor2.StatDisplays.IntervalRangeDisplay survey, ActiveHuntingAnimal animal)
    {
        double low = survey.MinSeconds, high = survey.MaxSeconds, one = animal.Stats.SurveyInterval.Value / 1000;
        if (!double.IsFinite(low) || !double.IsFinite(high) || low <= 0.01 || high < low || Math.Abs(low - one) > 0.005) return;
        _surveysMost[animal] = high / low;
    }

    // The most surveys a hunt takes for an animal, as the page wrote it for that animal; null when it has not (review 6:
    // another animal's never stands for it, and no member of the game's gives it).
    double? SurveysFor(ActiveHuntingAnimal animal) => _surveysMost.TryGetValue(animal, out double most) ? most : null;

    // Whether a trap has bonuses after a failed try (Native.FailTemplate, HasValues); when their values cannot be read, one
    // line in the log (AGENTS.md rule 4) and null from then on for a trap that has such a template, which counts no catch
    // with it. A trap without one is read as before.
    bool? FailBonuses(Item trap)
    {
        if (Native.FailTemplate(trap) is not { } fail) return false;
        if (_failBonusesOff) return null;
        bool? has = Native.HasValues(fail);
        if (has == null)
        {
            _failBonusesOff = true;
            Report("A trap's bonuses after a failed try could not be read (the game was updated?): no catch is counted with a trap that has them.");
        }
        return has;
    }

    // An animal the journal counts no catch of (HuntingAnimalJournalEntry.TimesTrapped 0). The page's cards drew Green Butterfly
    // and Wild Boar as black shapes in check-6, both discovered: the window's reading is that this is why, and the list draws
    // such an animal so until the page has drawn its card (Look). The light check logs every card's colour beside it.
    static bool NeverTrapped(ActiveHuntingAnimal animal) => animal.JournalEntry is { TimesTrapped: 0 };

    // The multipliers an XP view of the page is bound with, kept for the animal whose stats it shows.
    void ReadXpRange(GamesByMalcs.Melvor2.Components.Experience.ExperienceStatsXPView view, ActiveHuntingAnimal animal)
    {
        if (!ReferenceEquals(Native.XpOf(view), animal.Stats.Experience)) return;
        if (Native.XpLow(view) is { } low && Native.XpHigh(view) is { } high && double.IsFinite(low) && double.IsFinite(high) && high >= low && low >= 0)
            _xpRanges[animal] = (low, high);
    }

    // The path of the picture the game's own control shows for a spider (its exported _arachnophobiaModeIconPath), read
    // as a value through Godot; "" when it is not there.
    static string SpiderPath(SettingsIcon icon)
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
        if (_spiderUnread) return true;
        try { return LocalSettingsManager.Settings?.Interface?.ArachnophobiaMode != false; }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException or NullReferenceException)
        {
            _spiderUnread = true;
            Report("The game's spider setting could not be read (the game was updated?): spiders are shown without their picture.");
            return true;
        }
    }

    // The name and picture a line shows, and the colour the picture is drawn in. An animal not discovered yet is named
    // and pictured as the page's own card draws it, once the page has shown that card; before, by the game's own word for
    // something not found yet (the journal's, AnimalSpeciesView.HiddenText) and no picture. A spider, while the game's
    // spider setting is on, has the picture the page shows instead, or none: the list never shows a spider then. A
    // discovered animal's picture is drawn in the colour its card drew it in, as long as it has not been trapped since (a
    // black shape for one never trapped); before the page has drawn its card, black while it has never been trapped
    // (NeverTrapped), otherwise as it is.
    (string Name, Texture2D? Picture, Color Tint) Look(ActiveHuntingAnimal active)
    {
        if (active.JournalEntry?.IsDiscovered == false)
            return _looks.TryGetValue(active, out var seen) ? (seen.Name, Shown(seen.Picture), seen.Tint) : (_hiddenWord ?? NotFound, null, Colors.White);
        HuntingAnimal animal = active.Animal;
        Texture2D? picture = animal.IsScarySpider && SpiderSetting() ? Shown(_spiderPicture) : Shown(animal.Icon);
        bool never = NeverTrapped(active);
        Color tint = _tints.TryGetValue(active, out var drawn) && drawn.NeverTrapped == never ? drawn.Tint : never ? Colors.Black : Colors.White;
        return (animal.DisplayName.Text, picture, tint);
    }

    // An animal's hour, with what the list reads from the page for it.
    AnimalRates ReadAnimal(ActiveHuntingAnimal animal) =>
        AnimalRates.Read(animal, _hunting!, Look(animal), _xpRanges.TryGetValue(animal, out var range) ? range : null, SurveysFor(animal), FailBonuses,
            InBank, _hiddenWord ?? NotFound, HuntRuns());

    // A hunt runs, by the one test the window and the development request share (AnimalRates.IsHunting).
    bool HuntRuns() => AnimalRates.IsHunting(_hunting!, _game);

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
    // what the player's own clicks there would, nothing more. By the rule of lesson 56 (the brief template's "Only quality
    // of life", and review 1 of this job):
    // - It selects only through the page's own controls, pressed as a player's click presses them: the animal's location
    //   by the page's button of that location (PressLocation), then, once the page has laid out that location's cards, the
    //   animal by its card's own button (PressCard). Never through the members behind them.
    // - It selects only while no hunt runs (AnimalRates.IsHunting: started, among the game's running actions, or the hunted
    //   animal in a state other than idle; read at the click and again before every
    //   step): a selection may change the running hunt, and no check on the maintainer's character is to find out.
    // - While a hunt runs, a click only shows: the hunt instead of the journal, by the page's own button (HuntView), and
    //   the page's list scrolled to the animal's card when the page shows its location (ScrollToCard). Any other line
    //   then does nothing, and its tooltip says so (AnimalRates.HuntBlocks).
    // - A locked animal opens its location only, and its card is scrolled to, where the page says what it needs; a line
    //   whose location is locked opens nothing (AnimalRates.ClickOpens). A card whose own button the page keeps
    //   disabled is not selected (AnimalRates.CardBlocked).
    // The buttons that start or stop a hunt, and the trap, bait and trapless controls, are never pressed; the window stays
    // open where the player put it.
    void ShowOnPage(AnimalRates line)
    {
        if (!PageShown || _clicksOff || _clickStep != null) return;
        ActiveHuntingAnimal active = line.Active;
        // As it is now, not as it was at the last tick.
        AnimalRates.Opens opens = AnimalRates.ClickOpens(active);
        if (opens == AnimalRates.Opens.Nothing || active.Location == null) return;
        try
        {
            if (JournalShown() && !_huntViewOff)
            {
                // The hunt first: the location buttons and the cards are part of it. The rest once the view has settled.
                HuntView();
                _clickStep = new ClickStep(active, opens, 1, (long)Engine.GetProcessFrames() + 2);
            }
            else ClickStepNow(new ClickStep(active, opens, 1, 0));
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            ClicksOff(e);
            return;
        }
        Refresh();
    }

    // The steps after the first, two process frames after the one before.
    void ClickNext()
    {
        ClickStep step = _clickStep!;
        _clickStep = null;
        if (!PageShown || _clicksOff) return;
        try { ClickStepNow(step); }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            ClicksOff(e);
            return;
        }
        if (_window is { IsAlive: true, Visible: true }) Refresh();
    }

    // Stage 1: the location, by its own button, while no hunt runs; stage 2: the animal, by its card's own button, while
    // no hunt runs and the page still shows the location the click opened. Then the scroll to the card, which only shows.
    void ClickStepNow(ClickStep step)
    {
        var action = _hunting!.Action;
        ActiveHuntingAnimal active = step.Animal;
#if DEVTOOLS
        // The game's events raised while the click's own presses run are the click's (DevClickEvents).
        _devInClick = true;
#endif
        try
        {
            bool here = ReferenceEquals(action.SelectedLocation, active.Location);
            if (step.Stage == 1 && !here)
            {
                if (HuntRuns() || !PressLocation(active.Location!)) return;
                _clickStep = step with { Stage = 2, After = (long)Engine.GetProcessFrames() + 2 };
                return;
            }
            // The player went to another location meanwhile: his, and the click lets it be.
            if (!here) return;
            if (step.Opens == AnimalRates.Opens.Animal && !HuntRuns() && !ReferenceEquals(action.SelectedAnimal, active)) PressCard(active);
            _scrollTo = active;
            _scrollAfter = (long)Engine.GetProcessFrames() + 2;
        }
        finally
        {
#if DEVTOOLS
            _devInClick = false;
#endif
        }
    }

    // A control of the page's a player could click now: there, shown and not disabled.
    static bool CanPress(Control? control) => control != null && GodotObject.IsInstanceValid(control) && control.IsVisibleInTree() &&
        control switch { BaseButton b => !b.Disabled, BaseButtonContainer c => !c.Disabled, _ => false };

    // The page's own button of a location, pressed as a player's click presses it: a toggle button goes down, then its
    // Pressed (a C# event of the game's button container, raised as its click raises it). True when the page then shows
    // that location. When it does not, the button is drawn again as it was, one line in the log, and from then on a click
    // no longer changes the location (Woodcutting Rates' CategoryFilter, for the Hunting page's location buttons).
    bool PressLocation(ActiveHuntingLocation location)
    {
        if (_locationOff) return false;
        HuntingLocationButton? button = Native.LocationButton(_page!, location.Location);
        if (!CanPress(button)) return false;
        bool drawn = button!.ButtonPressed;
        if (button.ToggleMode && !button.ButtonPressed) button.ButtonPressed = true;
        Native.PressedEvent(button)?.Invoke();
        if (ReferenceEquals(_hunting!.Action.SelectedLocation, location)) return true;
        if (GodotObject.IsInstanceValid(button) && button.ButtonPressed != drawn) button.SetPressedNoSignal(drawn);
        _locationOff = true;
        Report("The Hunting page's own location button did not open its location when pressed (the game was updated?): a click on a line no longer changes the location.");
        return false;
    }

    // The animal's card's own button, pressed as a player's click presses it (a toggle button goes down, then its pressed
    // signal), only when the page shows it and does not keep it disabled. When the page then does not select the animal,
    // the buttons of its group are drawn again as they were, one line, and from then on a click no longer selects.
    bool PressCard(ActiveHuntingAnimal animal)
    {
        if (_cardOff || Native.Card(_page!, animal) is not { } card || Native.CardButton(card) is not { } button || !CanPress(button)) return false;
        var group = button.ButtonGroup?.GetButtons().OfType<BaseButton>().ToList() ?? new List<BaseButton>();
        if (!group.Contains(button)) group.Add(button);
        var drawn = group.Select(b => (Button: b, Pressed: b.ButtonPressed)).ToList();
        if (button.ToggleMode && !button.ButtonPressed) button.ButtonPressed = true;
        button.EmitSignal(BaseButton.SignalName.Pressed);
        if (ReferenceEquals(_hunting!.Action.SelectedAnimal, animal)) return true;
        foreach (var (b, pressed) in drawn)
            if (GodotObject.IsInstanceValid(b) && b.ButtonPressed != pressed) b.SetPressedNoSignal(pressed);
        _cardOff = true;
        Report("The Hunting page's own card button did not select its animal when pressed (the game was updated?): a click on a line no longer selects an animal.");
        return false;
    }

    // The page shows its journal instead of the hunt (HuntingTrackingView.JournalView on the screen).
    bool JournalShown() => PageShown && Native.TrackingView(_page!) is { } view && Native.Journal(view) is { } journal && journal.IsVisibleInTree();

    // The page's own button that shows the hunt instead of the journal, pressed the way a player's click presses it (a
    // toggle button goes down, which lets the journal's button of its group go, then its pressed signal). Whether the page
    // followed is judged once it has settled (ViewChecked), never in the same call.
    void HuntView()
    {
        if (_huntViewOff || Native.TrackingView(_page!) is not { } view || Native.HuntButton(view) is not { } button || !CanPress(button)) return;
        Button? journal = Native.JournalButton(view);
        _viewCheck = ((long)Engine.GetProcessFrames() + 2, button.ButtonPressed, journal?.ButtonPressed ?? false);
        if (button.ToggleMode && !button.ButtonPressed) button.ButtonPressed = true;
        button.EmitSignal(BaseButton.SignalName.Pressed);
    }

    // Two process frames after the hunt's view button was pressed: when the page still shows the journal, both view
    // buttons are drawn again as they were, so that neither stays drawn for a view the page does not show, one line, and
    // from then on a click no longer presses it.
    void ViewChecked()
    {
        var check = _viewCheck!.Value;
        _viewCheck = null;
        if (!JournalShown() || Native.TrackingView(_page!) is not { } view) return;
        if (Native.HuntButton(view) is { } hunt && GodotObject.IsInstanceValid(hunt) && hunt.ButtonPressed != check.Hunt) hunt.SetPressedNoSignal(check.Hunt);
        if (Native.JournalButton(view) is { } journal && GodotObject.IsInstanceValid(journal) && journal.ButtonPressed != check.Journal) journal.SetPressedNoSignal(check.Journal);
        _huntViewOff = true;
        _clickStep = null;
        Report("The Hunting page's own button that shows the hunt did not hide its journal (the game was updated?): a click on a line no longer presses it.");
    }

    // A click on the animal's line would change what the Hunting page shows, by the rule of ShowOnPage: the hunt instead of
    // the journal, its card scrolled into sight clear of this window (Native.Place) when the page shows its location,
    // and, while no hunt runs, its location or the animal selected through the page's own controls. Read on every tick,
    // for the line's look and its tooltip, and again at the click. Sets on the line what its tooltip says when a click
    // cannot serve it: the hunt that runs, the card's button the page keeps disabled, the card with no room.
    bool ClickChanges(AnimalRates a)
    {
        if (_clicksOff || !PageShown) return false;
        try
        {
            var action = _hunting!.Action;
            bool hunting = a.HuntRuns, here = ReferenceEquals(action.SelectedLocation, a.Active.Location);
            bool journal = JournalShown() && !_huntViewOff, selected = ReferenceEquals(action.SelectedAnimal, a.Active);
            HuntingAnimalTrackingView? card = here ? Native.Card(_page!, a.Active) : null;
            if (card != null && card.IsVisibleInTree()) a.Sight = Native.Place(card, Cover(), out _);
            bool scroll = a.Sight == CardSight.Scroll;
            bool canLocation = !here && !_locationOff && CanPress(Native.LocationButton(_page!, a.Active.Location?.Location));
            Button? cardButton = card != null ? Native.CardButton(card) : null;
            bool canCard = here && !selected && !_cardOff && CanPress(cardButton);
            if (a.Click == AnimalRates.Opens.Animal && here && !selected && cardButton is { Disabled: true }) a.CardBlocked = true;
            bool selects = a.Click switch
            {
                AnimalRates.Opens.Location => !here,
                AnimalRates.Opens.Animal => !here || !selected,
                _ => false,
            };
            if (hunting && selects) a.HuntBlocks = true;
            return a.Click switch
            {
                AnimalRates.Opens.Nothing => false,
                _ when hunting => journal || (here && scroll),
                AnimalRates.Opens.Location => canLocation || journal || (here && scroll),
                _ => canLocation || canCard || journal || scroll,
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
    // level with its top, or with its bottom above the game's bottom bar), ranked as Woodcutting and Firemaking Rates rank
    // theirs (review 3 of this job: the first check opened it over the locations and the cards, with 1 card of 4 in
    // sight): first the cards of the page's list whole beside it (Native.Place: in sight, or after a scroll), which is what
    // the player must see after a click; then the location buttons whole beside it (a click presses them by their event,
    // so they come after the cards and can never outweigh them: the code review of turn 4, fault 3); then the panel of
    // the animal selected left uncovered, then the panel of the hunt in progress; the right top when they are equal. The window may cover the panel of the hunt in progress when
    // nothing else keeps the cards clear. Everything is measured from the page as it is drawn now. Null when the page's
    // parts are not there: the window then opens in the middle.
    Vector2? FirstPlace(Vector2 size)
    {
        if (!PageShown || _clicksOff || size.X <= 0 || size.Y <= 0) return null;
        try
        {
            if (Native.TrackingView(_page!) is not { } view || !view.IsVisibleInTree()) return null;
            var cards = Native.Cards(_page!).Where(c => c.IsVisibleInTree()).Cast<Control>().ToList();
            var places = Native.LocationSelect(view) is { } select && select.IsVisibleInTree() && Native.LocationButtons(select) is { } buttons
                ? buttons.Values.Where(b => GodotObject.IsInstanceValid(b) && b.IsVisibleInTree()).Cast<Control>().ToList() : new List<Control>();
            Control? selected = Native.Panel(view) is { } p && p.IsVisibleInTree() ? p : null;
            Control? current = Native.Current(view) is { } c && c.IsVisibleInTree() ? c : null;
            if (cards.Count == 0 && places.Count == 0 && selected == null) return null;
            Rect2 screen = _ctx.Tree.Root.GetVisibleRect(), page = Native.Drawn(view);
            float right = screen.End.X;
            if (GodotObject.IsInstanceValid(_opener) && _opener!.GetParent() is Control strip && strip.IsVisibleInTree())
            {
                Rect2 strips = Native.Drawn(strip);
                right = strips.Position.X - Math.Max(0, screen.End.X - strips.End.X);
            }
            float bottom = screen.End.Y - FooterHeight();
            float lowest = Math.Max(screen.Position.Y, bottom - size.Y), widest = Math.Max(screen.Position.X, right - size.X);
            Vector2? best = null;
            (int Cards, int Buttons, int Selected, int Current) most = (-1, -1, -1, -1);
            var ranked = new List<string>();
            foreach (float left in new[] { right - size.X, page.Position.X })
                foreach (float top in new[] { page.Position.Y, Math.Min(page.End.Y, bottom) - size.Y })
                {
                    float x = Math.Clamp(left, screen.Position.X, widest), y = Math.Clamp(top, screen.Position.Y, lowest);
                    var cover = new Rect2(x, y, size);
                    bool Whole(Control control) => Native.Place(control, cover, out _) is CardSight.Clear or CardSight.Scroll;
                    var rank = (Cards: cards.Count(Whole), Buttons: places.Count(Whole), Selected: selected != null && !Native.Covered(selected, cover) ? 1 : 0,
                        Current: current != null && !Native.Covered(current, cover) ? 1 : 0);
                    ranked.Add($"({x:0}, {y:0}): {cards.Count(Whole)} of {cards.Count} cards and {places.Count(Whole)} of {places.Count} location buttons whole beside it, " +
                        $"the selected animal's panel {(rank.Selected == 1 ? "clear" : selected == null ? "not shown" : "covered")}, the hunt's panel {(rank.Current == 1 ? "clear" : current == null ? "not shown" : "covered")}");
                    if (rank.CompareTo(most) > 0) { most = rank; best = new Vector2(x, y); }
                }
#if DEVTOOLS
            _devFirstPlaceWords = $"the first place {best?.Round().ToString() ?? "none"} won of [{string.Join("; ", ranked)}]";
#endif
            return best?.Round();
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            ClicksOff(e);
            return null;
        }
    }

    // Two process frames after the click's last step: the page has laid out its list for the location, and the list is
    // scrolled as little as it takes to show the animal's whole card clear of this window (Native.Place), as Woodcutting
    // Rates scrolls to a tree's card (WoodcuttingRatesMod.ScrollToCard). A card the page has no room for there is brought
    // into its list as far as the list can scroll (ScrollContainer.EnsureControlVisible), and from the next tick on its
    // line's tooltip says that the window is in the way. A card in no scrolling list is left where it is. Only shows.
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
    // HuntingTrackingView, HuntingLocationSelect.Buttons, a button container's Pressed, a card's Animal or ToggleButton,
    // all private): the lines show the numbers and open nothing.
    void ClicksOff(Exception e)
    {
        _clicksOff = true;
        _scrollTo = null;
        _clickStep = null;
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
        _surveysMost.Clear();
        _panelAnimal = null;
        _tints.Clear();
        _toolWords.Clear();
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
