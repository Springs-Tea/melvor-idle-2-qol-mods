using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Data.DropTables;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Thieving;
using GamesByMalcs.Melvor2.Entities.Thieving.Statistics;
using GamesByMalcs.Melvor2.Random;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace ThievingOverview;

/// <summary>What the window remembers, in settings.json in the mod's folder.</summary>
sealed class Settings
{
    public float Scale { get; set; } = 1;
    public float? X { get; set; }
    public float? Y { get; set; }
    public bool Minimised { get; set; }
    public float? FoldedRight { get; set; }
    public bool AllPlaces { get; set; }
    public bool BestFor { get; set; }
    public string Item { get; set; } = "";
    /// <summary>The order of Targets.</summary>
    public string Sort { get; set; } = "xp";
    public bool Descending { get; set; } = true;
    /// <summary>The order of Best for, kept apart so each question keeps its own.</summary>
    public string BestSort { get; set; } = Columns.ItemKey;
    public bool BestDescending { get; set; } = true;
}

/// <summary>
/// Thieving at a glance: one list of every target of every place the character has found, with what an hour of
/// stealing there gives (success, time, XP, gold, rare items, food) and whether a failure can knock the
/// character out; the targets that give an item, by how many per hour; and the same numbers in the game's own
/// View Possible Rewards, with a Risky tag on the card of the target being stolen from while it is risky. A round
/// button in the strip on the right edge opens the window on the Thieving page; away from the page the window
/// steps aside and comes back as it was (Fishing Rates does the same). It shows what the game knows, and does one
/// thing only, which the maintainer allowed: a click on a line shows that target's place on the Thieving page, as the
/// page's own Select Location does (OpenPlace). Nothing is started, stopped, chosen to steal from or bought.
/// </summary>
public partial class ThievingOverviewMod : IMod
{
    const string Title = "Thieving Overview", ButtonName = "ThievingOverviewButton";
    // The fill of the game's toggled green button (button_toggled_green.tres) as it was read: only used if the game
    // no longer has that style.
    static readonly Color LitGreen = new("275124");
    // Four times a second the marks on the game's screen (Kills in Combat's tag); twice a second the open window's
    // numbers (Astrology Overview, Recipe Rates); once a second, until they are found, the Thieving page, the strip,
    // the bottom bar and the units of the game's numbers (AstrologyOverviewMod).
    readonly Tempo _tick = new(250), _refresh = new(500), _search = new(1000), _prove = new(1000), _stripSearch = new(1000), _footerSearch = new(1000);
    readonly HashSet<string> _reported = new();
    readonly GameStyle _markStyle = new();
    // View Possible Rewards as the game last showed it, per target, for this character.
    readonly Dictionary<ThievingRecipe, RewardsReading> _readings = new(ReferenceEqualityComparer.Instance);
    ModContext _ctx = null!;
    Game? _game;
    ThievingEntity? _thieving;
    BankEntity? _bank;
    ThievingActiveView? _page;
    Button? _opener;
    Control? _footer;
    GameWindow? _window;
    OverviewView? _view;
    GameMarks? _marks;
    Settings _settings = new();
    Units _units = new();
    // The window's size at 100%, measured from the window itself (AstrologyOverviewMod.BuildWindow).
    Vector2 _fullSize;
    // The items Best for was made with: when the character has found more meanwhile, the window is made again.
    string _itemsBuilt = "";
    // The player opened the window and has not closed it: it is there whenever the Thieving page is.
    bool _wanted, _marksOff, _barGone, _clicksOff, _unitsGone, _stopped;

    public void OnLoad(ModContext ctx)
    {
        _ctx = ctx;
        _settings = ModFiles.ReadJson<Settings>(SettingsFile, ctx.Log);
        _settings.Sort ??= "xp";            // null in a hand-edited file
        _settings.BestSort ??= Columns.ItemKey;
        _settings.Item ??= "";
        ctx.Frame += OnFrame;
        ctx.Log("Ready. On the Thieving page, a round button on the right edge of the screen opens the overview.");
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
                _thieving = game?.Entities.GetAllObjects().OfType<ThievingEntity>().FirstOrDefault();
                _bank = game != null && game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? bank) ? bank as BankEntity : null;
                _units = new Units();
                _readings.Clear();
                _marksOff = _barGone = _clicksOff = _unitsGone = false;
                if (game != null && _thieving == null) Report("The game has no Thieving skill (the game was updated?): the mod stands still.");
#if DEVTOOLS
                DevReset();
#endif
            }
            if (_thieving == null) return;
            if (_page != null && (!GodotObject.IsInstanceValid(_page) || !_page.IsInsideTree())) LetGoOfPage();
            if (_page == null && _search.Due() && GameAccess.FindGameView() is { } view &&
                GameAccess.FindNode<ThievingActiveView>(view) is { } page)
                Attach(page);
            if (_page != null)
            {
                UpdateStripButton();
                if (PageShown)
                {
                    if ((!_units.SuccessProven || !_units.LootProven) && !_unitsGone && _prove.Due()) ProveUnitsSafely();
                    Marks();
                }
                Sync();
            }
#if DEVTOOLS
            DevRequest();
#endif
        }
        catch (Exception e) { Stop(e); }
    }

    void Attach(ThievingActiveView page)
    {
        _page = page;
        _marks = new GameMarks(_markStyle, StripIcon, Report);
        page.VisibilityChanged += PageVisibilityChanged;
        UpdateStripButton();
    }

    // The page was freed: the marks went with it, the window steps aside until the page is found again.
    void LetGoOfPage()
    {
        if (GodotObject.IsInstanceValid(_page)) _page!.VisibilityChanged -= PageVisibilityChanged;
        _page = null;
        try { _marks?.Clear(); } catch { }
        _marks = null;
        if (_window is { IsAlive: true }) _window.Visible = false;
        if (GodotObject.IsInstanceValid(_opener)) _opener!.Visible = false;
    }

    bool PageShown => _page != null && GodotObject.IsInstanceValid(_page) && _page.IsVisibleInTree();

    // The page came or went: the button, the window and the marks follow at once, not on the next tick.
    void PageVisibilityChanged()
    {
        if (_stopped || GameAccess.IsComputingOffline) return;
        try
        {
            UpdateStripButton();
            if (!PageShown) _marks?.Clear();
            Sync();
        }
        catch (Exception e) { Stop(e); }
    }

    // The numbers in View Possible Rewards and the Risky tag on a target's card. Something here that fails (a member
    // of the game's screen gone after an update) switches the marks off with one line; the window goes on.
    void Marks()
    {
        if (_marksOff || _marks == null) return;
        try
        {
            var food = ThievingFood.Read(_thieving!, _bank);
            _marks.Update(_page!, _thieving!.Action, (active, withReadings) => RatesOf(active, food, withReadings), _units, _readings);
        }
        catch (Exception e)
        {
            _marksOff = true;
            try { _marks.Clear(); } catch { }
            _ctx.Log($"The numbers on the Thieving screen are switched off, the window goes on (in {Where(e)}: a game update, or a fault of this mod): {e.GetType().Name}: {e.Message}");
        }
    }

    // ---- the numbers --------------------------------------------------------------------------------------------

    TargetRates RatesOf(ActiveThievingRecipe active, ThievingFood food, bool withReadings = true)
    {
        ThievingLocation place = active.Recipe.Category;
        var action = _thieving!.Action;
        int? barMaxHit = action.IsStarted && ReferenceEquals(active, action.CurrentRecipe) ? BarMaxHit() : null;
        RewardsReading? reading = withReadings && _readings.TryGetValue(active.Recipe, out RewardsReading? read) ? read : null;
        return TargetRates.Read(active, place, place.Requirements?.AreMet != false, food, _units, reading, barMaxHit);
    }

    // The max hit the HP bar marks for the target being stolen from (its own damage watcher), so the window, the
    // Risky tag and the bar never say two things. Null when the bar has none; the target's Damage is used then.
    int? BarMaxHit()
    {
        if (_barGone || _page == null) return null;
        try
        {
            var bar = Native.ActionView(_page).PlayerHealthProgressBar;
            return bar != null && GodotObject.IsInstanceValid(bar) && Native.BarDamage(bar) is { Maximum: > 0 } damage ? damage.Maximum : null;
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            _barGone = true;
            Report("The HP bar's max hit could not be read (the game was updated?): the target's own max hit is used.");
            return null;
        }
    }

    /// <summary>Every target of every place the game shows the character, in the game's order.</summary>
    List<TargetRates> ReadAll(ThievingFood food)
    {
        var list = new List<TargetRates>();
        var action = _thieving!.Action;
        foreach (ThievingLocation place in _thieving.Config.Categories)
        {
            if (place.DisplayRequirements?.AreMet == false || !action.ActiveLocations.TryGetValue(place, out ActiveThievingLocation? active)) continue;
            foreach (ActiveThievingRecipe recipe in active.Recipes)
                if (recipe.Recipe.DisplayRequirements?.AreMet != false)
                    list.Add(RatesOf(recipe, food));
        }
        return list;
    }

    // What Best for can be asked about: every item the targets of the list give, by name.
    List<Item> BestForItems()
    {
        var items = new HashSet<Item>(ReferenceEqualityComparer.Instance);
        void From(IReadOnlyWeightedTable<WeightedItemDrop>? table)
        {
            if (table == null) return;
            foreach (WeightedItemDrop drop in table.Items)
                if (drop.Item != null) items.Add(drop.Item);
        }
        var action = _thieving!.Action;
        foreach (ThievingLocation place in _thieving.Config.Categories)
        {
            if (place.DisplayRequirements?.AreMet == false || !action.ActiveLocations.TryGetValue(place, out ActiveThievingLocation? active)) continue;
            foreach (ActiveThievingRecipe recipe in active.Recipes.Where(r => r.Recipe.DisplayRequirements?.AreMet != false))
            {
                From(recipe.Recipe.CommonDropTable);
                From(recipe.Recipe.RareDropTable);
                From(recipe.Recipe.CompletionDropTable);
            }
        }
        return items.OrderBy(i => i.DisplayName.GetPluralText(1), StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string ItemsKey(List<Item> items) => string.Join("|", items.Select(i => i.Id));

    // The character's own record at a target, as the game's statistics name and write it: the successes, the
    // failures and the damage taken there.
    string? Record(ThievingRecipe recipe)
    {
        if (_thieving?.Statistics.Config is not IThievingStatisticsConfig config) return null;
        var lines = new List<string>();
        void Add<T>() where T : ThievingRecipeStatistic
        {
            if (config.ThievingRecipeStatistics.OfType<T>().FirstOrDefault() is not { } statistic) return;
            string name = statistic.DisplayName;
            if (!string.IsNullOrWhiteSpace(name) && !name.Contains('{')) lines.Add($"{name}: {statistic.GetFormattedValue(recipe)}");
        }
        Add<SuccessfulThievingActionStatistic>();
        Add<FailedThievingActionStatistic>();
        Add<ThievingDamageTakenStatistic>();
        return lines.Count == 0 ? null : "Your record here:\n" + string.Join("\n", lines);
    }

    // A label of the game's that is gone after an update leaves its numbers unproven, with one line: the window goes on
    // with "-" where they are needed.
    void ProveUnitsSafely()
    {
        try { ProveUnits(); }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            _unitsGone = true;
            if (!_units.SuccessProven) _units.SuccessWhy = "the Thieving screen has changed (the game was updated?)";
            if (!_units.LootProven) _units.LootWhy = "View Possible Rewards has changed (the game was updated?)";
            Report($"The units of the game's Thieving numbers could not be read: {e.Message}");
        }
    }

    // The units of the game's numbers, read the way Item Sources reads the unit of the loot chances
    // (DropWindows.ShareOf): a label of the game's that writes the number through a .NET percent format ("{0:P2}"
    // writes 0.6006 as "60.06%") proves a share of 1, and the proof holds while the label shows that very number.
    // The success chance: a target's card on the Thieving screen. The loot and coin chances: View Possible Rewards,
    // whose labels have their formats while the window is closed too, and are checked against the game's numbers
    // each time it is open (GameMarks.Shown). Until proven, what is made from a number shows "-".
    void ProveUnits()
    {
        if (!_units.SuccessProven && Cards(Native.ActionView(_page!)).FirstOrDefault(c => c.IsVisibleInTree() && Native.Active(c) != null) is { } card)
        {
            var label = Native.SuccessChance(card);
            double stat = Native.Active(card)!.Stats.SuccessChance.Value;
            if (label.Value == stat)
            {
                if (Native.WritesShare(label.FormatString)) _units.SuccessProven = true;
                else
                {
                    _units.SuccessWhy = $"the Thieving screen does not write the success chance as a percent (its format is '{label.FormatString}')";
                    Report($"Success, time and the numbers per hour show -: {_units.SuccessWhy}.");
                }
#if DEVTOOLS
                DevLog($"success chance unit | {(_units.SuccessProven ? "a share of 1" : "NOT PROVEN")} from '{label.GetParsedText()}' (value {label.Value:R}, format '{label.FormatString}', number format '{Native.NumberFormat(label.FormatString) ?? "none"}')");
#endif
            }
        }
        if (!_units.LootProven)
        {
            var rewards = Native.Rewards(_page!);
            string?[] formats = [Native.CommonChance(rewards).FormatString, Native.RareChance(rewards).FormatString, Native.CoinChance(rewards).FormatString];
            if (formats.All(Native.WritesShare)) _units.LootProven = true;
            else if (formats.Any(string.IsNullOrEmpty))
                _units.LootWhy = "the unit of the game's loot chances is known once View Possible Rewards has been open";
            else
            {
                _units.LootWhy = $"View Possible Rewards does not write the loot chances as percents (its formats are '{string.Join("', '", formats)}')";
                Report($"Coins, items and gold show -: {_units.LootWhy}.");
            }
#if DEVTOOLS
            if (_units.LootProven || _devLootTold++ == 0)
                DevLog($"loot chance units | {(_units.LootProven ? "shares of 1" : "NOT PROVEN: " + _units.LootWhy)} from View Possible Rewards' formats common '{formats[0]}', rare '{formats[1]}', coins '{formats[2]}'");
#endif
        }
    }

    // ---- a click on a line: the target's place on the Thieving page --------------------------------------------------

    // The one thing the window does in the game, allowed by the maintainer (22 Nov 2026): a click on a line shows that
    // target's place on the Thieving page, as the page's own Select Location does when the player clicks the place's
    // Select button there; that very button is pressed (ThievingLocationSelectView.SelectButton). Nothing is started,
    // stopped or chosen to steal from. A place the player cannot open yet, or the place already shown: nothing happens.
    void OpenPlace(ThievingLocation place)
    {
        if (_clicksOff || _page == null || !PageShown || _thieving == null || !CanOpen(place)) return;
        var action = _thieving.Action;
        if (!action.ActiveLocations.TryGetValue(place, out ActiveThievingLocation? active) || ReferenceEquals(action.SelectedCategory, active)) return;
        try
        {
            if (PlaceButton(active) is not { } select)
            {
                Report($"A click on a line did not open {place.Id}: the Thieving page's Select Location has no Select button for it that can be pressed.");
                return;
            }
            select.EmitSignal(BaseButton.SignalName.Pressed);
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            _clicksOff = true;
            Report($"A click on a line opens nothing from now on: the Thieving page's Select Location has changed (the game was updated?): {e.Message}");
            return;
        }
        Refresh();
    }

    // A place the player can open on the Thieving page: shown there, and its requirements met.
    static bool CanOpen(ThievingLocation place) => place.DisplayRequirements?.AreMet != false && place.Requirements?.AreMet != false;

    // The Select button of a place in the page's own Select Location, when it can be pressed.
    Button? PlaceButton(ActiveThievingLocation active)
    {
        var select = Native.LocationSelect(Native.ActionView(_page!));
        if (select == null || !GodotObject.IsInstanceValid(select) || Native.PlaceViews(select) is not { } views) return null;
        return views.TryGetValue(active, out ThievingLocationSelectView? view) && GodotObject.IsInstanceValid(view) &&
            view.SelectButton is { Disabled: false } button && GodotObject.IsInstanceValid(button) ? button : null;
    }

    // Why whole columns of the window show "-" for now.
    string Note() =>
        !_units.SuccessProven ? $"Success, time and the numbers per hour show - until the unit of the game's success chance is known: {_units.SuccessWhy}. Choose a place on the Thieving screen."
        : !_units.LootProven ? $"Coins, items, gold and rare items show - for now: {_units.LootWhy}."
        : "";

    static IEnumerable<ThievingRecipeView> Cards(Node root) => Native.Cards(root);

    // ---- the button in the game's strip of round buttons on the right edge (AstrologyOverviewMod.UpdateStripButton)

    Texture2D? StripIcon => _thieving?.Icon ?? GameStyle.Icon("Thieving");

    void UpdateStripButton()
    {
        if (_opener != null && GodotObject.IsInstanceValid(_opener))
        {
            _opener.Visible = PageShown;
            SyncStripButton();
            return;
        }
        _opener = null;
        if (!_stripSearch.Due()) return;
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
            Icon = StripIcon,
            ExpandIcon = true,
            IconAlignment = HorizontalAlignment.Center,
            ToggleMode = true,
            TooltipText = "Thieving Overview: what an hour at every target gives, and whether it can knock you out",
        };
        _opener.Pressed += () => Click(Toggle);
        strip.AddChild(_opener);
        // The style's own "pressed" look is a wider circle that is cut off at this size: keep the resting shape, in the
        // green of the game's toggled buttons, as Away Summary's button does (SummaryWindow.UpdateButton).
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

    // ---- the window -----------------------------------------------------------------------------------------------

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
        // Made again: what the character has found may have changed since it was last open.
        _window?.Free();
        _window = null;
        _view = null;
        Sync();
    }

    void Close()
    {
        _wanted = false;
        if (_window is { IsAlive: true }) _window.Visible = false;
        SyncStripButton();
    }

    // The window is on the screen while it is wanted and the Thieving page is; away from the page it steps aside, as
    // Skill Bonuses' window does on a page without bonuses (Fishing Rates does the same), and comes back as it was.
    // Its numbers are read again twice a second while it is open and not folded, and never while it is put away.
    void Sync()
    {
        if (!_wanted || !PageShown)
        {
            if (_window is { IsAlive: true, Visible: true }) _window.Visible = false;
            SyncStripButton();
            return;
        }
        bool made = _window is not { IsAlive: true };
        if (made) Build();
        var window = _window!;
        bool cameBack = made || !window.Visible;
        if (cameBack)
        {
            window.Visible = true;
            // Back on the page: Best for is made again if the character has found other items meanwhile.
            if (_settings.BestFor && ItemsKey(BestForItems()) != _itemsBuilt)
            {
                Rebuild();
                return;
            }
        }
        window.KeepAbove(FooterHeight());
        if (!window.Minimised && !window.Resizing && (cameBack || _refresh.Due())) Refresh();
        SyncStripButton();
    }

    // Built at the size the player chose, never larger than fits above the game's bottom bar: when the size asked
    // for is too large, the window is built once more at the largest that fits (AstrologyOverviewMod.BuildWindow).
    void Build()
    {
        float footer = FooterHeight();
        float scale = _settings.Scale;
        Vector2? at = _settings is { X: { } x, Y: { } y } ? new Vector2(x, y) : null;
        List<Item> items = BestForItems();
        _itemsBuilt = ItemsKey(items);
        void Make(float size, bool folded)
        {
            _window?.Free();
            _window = new GameWindow(_ctx.Tree, Title, size, at, canMinimise: true, minimised: folded,
                bottomInset: footer, foldedRight: _settings.FoldedRight);
            _view = new OverviewView(_window, _settings, items, Record, SaveSettings, () => Click(Refresh), () => Click(Rebuild), Click,
                place => !_clicksOff && CanOpen(place), place => Click(() => OpenPlace(place)));
        }
        // Measured open, as it is when it is unfolded; folded, it is made once more as the player left it.
        Make(scale, false);
        _fullSize = _window!.Root.GetCombinedMinimumSize() / _window.Style.Scale;
        float largest = LargestScale(footer);
        if (_settings.Minimised || _window.Style.Scale > largest + 0.001f) Make(Math.Min(_window.Style.Scale, largest), _settings.Minimised);
        var window = _window!;
        window.CloseRequested += () => Click(Close);
        window.MinimisedChanged += folded =>
        {
            // GameWindow raises Moved next, which saves the fold and its matching position together.
            _settings.Minimised = folded;
            SyncStripButton();
            if (!folded) Click(Refresh);
        };
        window.Moved += position =>
        {
            _settings.X = position.X;
            _settings.Y = position.Y;
            _settings.FoldedRight = window.Minimised ? window.Root.GetRect().End.X : null;
            SaveSettings();
        };
        window.ResizeRequested += size => Click(() =>
        {
            _settings.Scale = Math.Min(size, LargestScale(FooterHeight()));
            _settings.X = window.Position.X;
            _settings.Y = window.Position.Y;
            SaveSettings();
            Rebuild();
        });
        // The resize grip is a sibling of the frame and has its own tooltip.
        foreach (var control in window.Layer.GetChildren().OfType<Control>()) control.Theme = window.Root.Theme;
    }

    // The same window again, at its place, after a change of size or of what it shows.
    void Rebuild()
    {
        if (_window is { IsAlive: true }) { _settings.X = _window.Position.X; _settings.Y = _window.Position.Y; }
        int scroll = _view?.Scroll is { } list && GodotObject.IsInstanceValid(list) ? list.ScrollVertical : 0;
        Build();
        _window!.Visible = _wanted && PageShown;
        if (_window.Visible) Refresh();
        _view!.Scroll.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, scroll);
        SyncStripButton();
    }

    void Refresh()
    {
        if (_view == null || _thieving == null || !IsOpen || _window!.Minimised || _window.Resizing) return;
        var food = ThievingFood.Read(_thieving, _bank);
        var action = _thieving.Action;
        _view.Show(ReadAll(food), food, action.SelectedCategory?.Data, action.IsStarted ? action.CurrentRecipe : null, Note());
    }

    // How high the game's bar along the bottom of the screen is (CombatSimMod.FooterHeight), looked for once and kept.
    float FooterHeight()
    {
        if (_footer == null || !GodotObject.IsInstanceValid(_footer))
        {
            _footer = _footerSearch.Due() ? GameAccess.FindGameView()?.FindChild("Footer", true, false) as Control : null;
            if (_footer == null)
            {
                Report("The game's bottom bar was not found (the game was updated?): the overview may lie over it.");
                return 0;
            }
        }
        return _footer.IsVisibleInTree() ? _footer.GetGlobalRect().Size.Y : 0;
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

    // ---- settings.json in the mod's folder --------------------------------------------------------------------

    string SettingsFile => Path.Combine(_ctx.Directory, "settings.json");

    void SaveSettings() => ModFiles.WriteJson(SettingsFile, _settings, _ctx.Log);

    // ---- staying out of the way -----------------------------------------------------------------------------------

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

    // Something the player should know about once, not on every frame.
    void Report(string message)
    {
        if (_reported.Add(message)) _ctx.Log(message);
    }

    void Detach()
    {
#if DEVTOOLS
        try { DevListen(false); } catch { }
#endif
        if (_page != null && GodotObject.IsInstanceValid(_page)) _page.VisibilityChanged -= PageVisibilityChanged;
        try { _marks?.Clear(); } catch { }
        _window?.Free();
        if (_opener != null && GodotObject.IsInstanceValid(_opener)) _opener.QueueFree();
        _window = null;
        _view = null;
        _opener = null;
        _footer = null;
        _page = null;
        _marks = null;
        _wanted = false;
    }

    // One line, and the mod is out of the way: the window, the button and the marks go, the game is left as it is.
    // The line names the mod's method it happened in, so a player's log tells where.
    void Stop(Exception e)
    {
        if (_stopped) return;
        _stopped = true;
        _ctx.Frame -= OnFrame;
        _ctx.Log($"Stopped in {Where(e)} (the game was updated, or a fault of this mod: please report this line): {e}");
#if DEVTOOLS
        try { DevAbandon(); } catch { }
#endif
        try { Detach(); } catch { }
    }

    /// <summary>
    /// The mod's own method an exception came through, as "OverviewView.Show", and the method it started in when
    /// that is another (the game's, or .NET's).
    /// </summary>
    static string Where(Exception e)
    {
        string? ours = null, first = null;
        foreach (StackFrame frame in new StackTrace(e, false).GetFrames())
        {
            if (frame.GetMethod() is not { } method) continue;
            Type? type = method.DeclaringType;
            // Lambdas and local functions live in classes the compiler makes inside the one they were written in.
            while (type is { DeclaringType: not null } && type.Name.StartsWith('<')) type = type.DeclaringType;
            string name = $"{type?.Name}.{Plain(method.Name)}";
            first ??= name;
            if (type?.Namespace == typeof(ThievingOverviewMod).Namespace) { ours = name; break; }
        }
        return ours == null ? first ?? "an unknown place" : first != null && first != ours ? $"{ours} (at {first})" : ours;

        static string Plain(string name) => name.StartsWith('<') && name.IndexOf('>') is > 1 and var end ? name[1..end] : name;
    }
}
