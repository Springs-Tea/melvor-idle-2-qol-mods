using System;
using System.Collections.Generic;
using System.IO;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.GameMenus;
using GamesByMalcs.Melvor2.GameMenus.Pages;
using GamesByMalcs.Melvor2.InfoLabels;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace SkillBonuses;

/// <summary>
/// Where a skill's bonuses come from. On a skill page, a round button in the game's strip on the right edge of
/// the screen opens a window with every stat of the page's stats panel (doubling, preservation, XP, interval...),
/// its total as the page shows it, and under it each source the game lists for it: an item, a pet, a shop
/// purchase, a constellation. The numbers are the game's own stat nodes, the very ones the page shows; the mod
/// only reads them. Nothing on the game's cards is touched, so no click on the mod can start or select anything.
/// </summary>
public partial class SkillBonusesMod : IMod
{
    const string Title = "Skill Bonuses";

    sealed class Settings
    {
        public float? X { get; set; }
        public float? Y { get; set; }
        /// <summary>The size the player dragged the window to; until then full size, or less on a small screen.</summary>
        public float? Scale { get; set; }
        /// <summary>The window is folded down to its title.</summary>
        public bool Minimised { get; set; }
    }

    readonly Tempo _look = new(500);
    ModContext _ctx = null!;
    Settings _settings = new();
    GameView? _gameView;
    Control? _footer;
    Game? _game;
    /// <summary>The skill page on screen, or null.</summary>
    EntityPage? _page;
    List<Choice> _choices = new();
    /// <summary>The recipe the player picked in the window on this page; null follows what the skill is doing.</summary>
    string? _chosen;
    readonly Dictionary<EntityPage, string> _chosenOn = new();
    /// <summary>The player opened the window and has not closed it. It is shown on every page that has bonuses.</summary>
    bool _wanted;
    GameWindow? _window;
    BonusList? _list;
    EntityPage? _builtFor;
    PanelCopy? _copy;
    Button? _stripButton;
    bool _stopped, _stripMissingLogged, _footerMissingLogged, _copyFailed;

    string SettingsFile => Path.Combine(_ctx.Directory, "settings.json");

    public void OnLoad(ModContext ctx)
    {
        _ctx = ctx;
        _settings = ModFiles.ReadJson<Settings>(SettingsFile, ctx.Log);
        ctx.Frame += OnFrame;
        ctx.Log("Ready. On a skill page, the round button on the right edge of the screen opens the window.");
    }

    void OnFrame()
    {
        if (_stopped) return;
        try
        {
            if (_look.Due()) Look();
#if DEVTOOLS
            if (_game != null) DevRequests();
#endif
        }
        catch (Exception e) { Stop(e); }
    }

    // Twice a second: which page is on screen, what on it has bonuses, and the window's numbers.
    void Look()
    {
        if (!GodotObject.IsInstanceValid(_gameView) || !_gameView!.IsInsideTree()) { _gameView = GameAccess.FindGameView(); _footer = null; }
        Game? game = _gameView == null ? null : SkillStats.GameOf(_gameView);
        if (game is not { IsSaveLoaded: true }) game = null;
        // CurrentGame is null on the main menu and a new object for every character that is opened.
        if (!ReferenceEquals(game, _game)) { Clear(); _game = game; }
        if (game == null) return;

        EntityPage? page = Visible(_gameView!.EntityViewContainer) ? SkillStats.Page(_gameView)?.Item1 as EntityPage : null;
        if (page != null && page.Entity is not ILeveledEntity<ExperienceComponent>) page = null;
        if (!ReferenceEquals(page, _page))
        {
            if (_page != null) { if (_chosen != null) _chosenOn[_page] = _chosen; else _chosenOn.Remove(_page); }
            _page = page;
            _chosen = page != null && _chosenOn.TryGetValue(page, out string? name) ? name : null;
        }
        _choices = page == null ? new List<Choice>() : SkillStats.Find(_gameView.EntityViewContainer, page.Entity.DisplayName.Text);
        UpdateStripButton();
        Sync();
    }

    static bool Visible(Control? control) => GodotObject.IsInstanceValid(control) && !control!.IsQueuedForDeletion() && control.IsVisibleInTree();

    // ---- the window ---------------------------------------------------------------------------------------------

    bool Wanted => _wanted && _page != null && _choices.Count > 0;

    /// <summary>Shows the window when it is wanted and the page has bonuses, hides it otherwise.</summary>
    void Sync()
    {
        if (!Wanted)
        {
            if (_window is { Visible: true }) _window.Visible = false;
            ReleaseCopy();
            SyncStripButton();
            return;
        }
        if (_window is not { IsAlive: true } || !ReferenceEquals(_builtFor, _page)) Build();
        _window!.Visible = true;
        if (!_window.Minimised) Fill();
        SyncStripButton();
    }

    // Opening, closing and choosing are also asked for by a click, outside of OnFrame: a game member that is gone
    // must end in the same one line there.
    void SetWanted(bool wanted)
    {
        if (_stopped || GameAccess.IsComputingOffline) return;
        try { _wanted = wanted; Sync(); }
        catch (Exception e) { Stop(e); }
    }

    void Choose(int index)
    {
        if (_stopped || index < 0 || index >= _choices.Count) return;
        try { _chosen = _choices[index].Name; Fill(); }
        catch (Exception e) { Stop(e); }
    }

    // The recipe the player picked; else the tree, rock or target the skill is working on; else the first.
    int Selected()
    {
        int index = _chosen == null ? -1 : _choices.FindIndex(c => c.Name == _chosen);
        if (index < 0) index = _choices.FindIndex(SkillStats.InProgress);
        return Math.Max(0, index);
    }

    void Fill()
    {
        if (_list == null || _choices.Count == 0) return;
        int index = Selected();
        Choice choice = _choices[index];
        _list.SetChoices(_choices, index);
        var labels = LabelsFor(choice, out bool copied);
        _list.Show(labels, choice.Panel, pageText: !copied, labels.Count == 0
            ? "The game has not filled in these stats yet. Rest the pointer on the card's stats icon once, and they are shown here."
            : null);
        _list.Fit(_ctx.Tree.Root.GetVisibleRect().Size.Y - FooterHeight());
    }

    // The game's own panel when it is bound; for a card whose hover panel the game has not filled yet, a private
    // copy of that panel bound to the same card (see PanelCopy).
    List<ModifierStatLabel> LabelsFor(Choice choice, out bool copied)
    {
        copied = false;
        var labels = SkillStats.Labels(choice.Panel);
        if (labels.Count > 0 || choice.Card == null) { ReleaseCopy(); return labels; }
        if (_copyFailed || _list == null) return labels;
        if (_copy == null || !ReferenceEquals(_copy.For.Model, choice.Model) || _copy.For.Name != choice.Name || !GodotObject.IsInstanceValid(_copy.View))
        {
            ReleaseCopy();
            try { _copy = PanelCopy.Make(choice, _list.Holder); }
            catch (Exception e)
            {
                // The rest of the mod goes on: pages with a panel of their own, and cards the game has filled.
                _copyFailed = true;
                _ctx.Log("The stats of a card could not be read before the game fills them in (the game was updated?): " + e.Message);
                return labels;
            }
        }
        copied = true;
        return SkillStats.Labels(_copy.View);
    }

    void ReleaseCopy()
    {
        _copy?.Release(_ctx.Log);
        _copy = null;
    }

    void Build()
    {
        float footer = FooterHeight();
        Create(ShownScale(GameWindow.MaxScale), footer);
        // Never larger than the room above the game's bottom bar, whatever size was chosen on another screen.
        // Measured on the open window: folded, it is measured when it is opened up again.
        if (_window!.Minimised) return;
        Fill();
        float fits = ShownScale(LargestScale(footer));
        if (fits < _window.Style.Scale - 0.001f) Create(fits, footer);
    }

    void Create(float scale, float footer)
    {
        ReleaseCopy();
        _window?.Free();
        Vector2? at = _settings is { X: { } x, Y: { } y } ? new Vector2(x, y) : null;
        // The game's bar along the bottom stays in sight: the window is placed, and kept, above it.
        var window = _window = new GameWindow(_ctx.Tree, Title, scale, at, canMinimise: true, minimised: _settings.Minimised, bottomInset: footer);
        _list = new BonusList(window, _game!, _page!.Entity.DisplayName.Text, Choose, _ctx.Log);
        _builtFor = _page;
        window.CloseRequested += () => SetWanted(false);
        window.Moved += position => { _settings.X = position.X; _settings.Y = position.Y; Save(); };
        window.MinimisedChanged += minimised =>
        {
            _settings.Minimised = minimised;
            Save();
            if (minimised) return;
            try
            {
                // Opened up again: the numbers may have changed while only the title was showing.
                Fill();
                float fits = ShownScale(LargestScale(FooterHeight()));
                if (fits < window.Style.Scale - 0.001f) { Create(fits, FooterHeight()); Sync(); }
            }
            catch (Exception e) { Stop(e); }
        };
        window.ResizeRequested += size =>
        {
            if (_stopped || GameAccess.IsComputingOffline) return;
            try
            {
                // The same window again at another size.
                _settings.Scale = Math.Min(size, LargestScale(FooterHeight()));
                _settings.X = window.Position.X;
                _settings.Y = window.Position.Y;
                Save();
                Create(ShownScale(GameWindow.MaxScale), FooterHeight());
                Sync();
            }
            catch (Exception e) { Stop(e); }
        };
    }

    // The largest size, in the corner's steps of 5%, at which the window's width and its fixed parts (the title,
    // the paper tag, the recipe and one line of the list) fit above the bottom bar. The list itself scrolls in
    // whatever height is left. Measured, so long names in another language are counted too.
    float LargestScale(float footer)
    {
        if (_window is not { IsAlive: true, Minimised: false } || _list == null) return GameWindow.MaxScale;
        Vector2 screen = _ctx.Tree.Root.GetVisibleRect().Size;
        Vector2 full = _list.Fixed / _window.Style.Scale;
        if (full.X <= 0 || full.Y <= 0) return GameWindow.MaxScale;
        float fits = Math.Min(screen.X / full.X, (screen.Y - footer) / full.Y);
        return Math.Clamp(MathF.Floor(fits * 20f) / 20f, GameWindow.MinScale, GameWindow.MaxScale);
    }

    float ShownScale(float limit) => Math.Clamp(_settings.Scale ?? 1f, GameWindow.MinScale, Math.Max(GameWindow.MinScale, limit));

    // How high the game's bar along the bottom of the screen is (levels, rates, the music button). Found the way
    // the Combat Simulator finds it (CombatSimMod.FooterHeight), once per screen.
    float FooterHeight()
    {
        if (!GodotObject.IsInstanceValid(_footer) && !_footerMissingLogged) _footer = _gameView?.FindChild("Footer", true, false) as Control;
        if (GodotObject.IsInstanceValid(_footer)) return _footer!.IsVisibleInTree() ? _footer.GetGlobalRect().Size.Y : 0;
        // Looked for once: walking the whole screen twice a second for a bar that is not there helps nobody.
        if (!_footerMissingLogged) _ctx.Log("The game's bottom bar was not found (the game was updated?): the window may lie over it.");
        _footerMissingLogged = true;
        return 0;
    }

    void Save() => ModFiles.WriteJson(SettingsFile, _settings, _ctx.Log);

    // ---- the button in the game's strip of round buttons on the right edge --------------------------------------

    // Copied from the Item Encyclopedia (ItemEncyclopediaMod.UpdateStripButton), and shown only where it has
    // something to open, the way Combat Goals shows its own only in combat.
    void UpdateStripButton()
    {
        if (_stripButton == null || !GodotObject.IsInstanceValid(_stripButton))
        {
            _stripButton = null;
            if (_gameView?.FindChild("FavouritesContainer", true, false) is not BoxContainer strip)
            {
                if (!_stripMissingLogged) _ctx.Log("The game's widget strip was not found (the game was updated?): the window cannot be opened.");
                _stripMissingLogged = true;
                return;
            }
            // The strip may be the one of the character before: its button goes, this one takes its place.
            if (strip.GetNodeOrNull("SkillBonusesButton") is { } stale) { stale.Name = "SkillBonusesButtonGone"; stale.QueueFree(); }
            _stripButton = new Button
            {
                Name = "SkillBonusesButton",
                ThemeTypeVariation = "SidebarCategoryButton",
                CustomMinimumSize = new Vector2(36, 36),
                Icon = GameStyle.Icon("upgrade") ?? GameStyle.Icon("SkillIcon") ?? GameStyle.Icon("Statistics"),
                ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center,
                ToggleMode = true,
                TooltipText = "Skill Bonuses: every bonus of this skill page, and where each one comes from",
                // The words on the game's own dark tooltip panel, as in the mods' windows.
                Theme = new GameStyle().TooltipTheme(),
            };
            _stripButton.Toggled += open => SetWanted(open);
            strip.AddChild(_stripButton);
            // The style's own "pressed" look is a wider circle that is cut off at this size: keep the resting shape.
            if (_stripButton.GetThemeStylebox("normal") is StyleBoxFlat resting)
            {
                var open = (StyleBoxFlat)resting.Duplicate();
                open.BgColor = new Color("275124");
                _stripButton.AddThemeStyleboxOverride("pressed", open);
                _stripButton.AddThemeStyleboxOverride("hover_pressed", open);
            }
        }
        _stripButton.Visible = _page != null && _choices.Count > 0;
        SyncStripButton();
    }

    void SyncStripButton()
    {
        if (_stripButton != null && GodotObject.IsInstanceValid(_stripButton)) _stripButton.SetPressedNoSignal(_window is { IsAlive: true, Visible: true });
    }

    // ---- another character, or a game member that is gone --------------------------------------------------------

    void Clear()
    {
        ReleaseCopy();
        _window?.Free();
        _window = null;
        _list = null;
        _builtFor = null;
        _stripButton = null;   // it lived in the old character's screen
        _footer = null;
        _page = null;
        _chosen = null;
        _chosenOn.Clear();
        _choices = new List<Choice>();
        _wanted = false;
    }

    // One line, and the mod is out of the way: the window and the button go, the game is left as it is.
    void Stop(Exception e)
    {
        if (_stopped) return;
        _stopped = true;
        _ctx.Log("Stopped (the game was updated?): " + e);
        try
        {
            ReleaseCopy();
            _window?.Free();
            if (_stripButton != null && GodotObject.IsInstanceValid(_stripButton)) _stripButton.QueueFree();
        }
        catch { }
    }
}
