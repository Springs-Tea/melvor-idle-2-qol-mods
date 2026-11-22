using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Combat;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace CombatGoals;

/// <summary>
/// Level goals for combat skills. The game's footer shows "time left to level N" for one skill; combat trains
/// several at once, so this window measures each combat skill's XP rate and shows the time left to a target
/// level you enter per skill. Only skills that are gaining XP are listed. The window has the frame of the other
/// mods' windows (GameWindow): drag it anywhere, drag its corner for another size, fold it down to its title or
/// close it with its X; F10 and a round button by the game's widgets open it again.
/// </summary>
public class CombatGoalsMod : IMod
{
    internal const string Title = "Combat Goals";

    static readonly string[] SkillIds =
    {
        "MelvorBase:Attack", "MelvorBase:Strength", "MelvorBase:Defence", "MelvorBase:Hitpoints",
        "MelvorBase:Ranged", "MelvorBase:Magic", "MelvorBase:Prayer", "MelvorBase:Slayer",
    };

    // The XP rate is the average over this much recent play, like the footer's own time period.
    static readonly (string Label, double Seconds)[] Periods = { ("1 minute", 60), ("5 minutes", 300), ("10 minutes", 600), ("30 minutes", 1800) };
    static readonly double LongestPeriod = Periods[^1].Seconds;
    internal const ulong SampleEveryMs = 1000;
    // A longer gap between samples means the game was paused or computed offline progress: that XP did not
    // arrive at a rate we can measure, so the history starts again.
    internal const ulong GapResetMs = 5000;
    // The sizes its corner reaches: this window's own, kept from before it had the shared frame.
    internal const float MinScale = 0.7f, MaxScale = 2f;
    // The first time, the window keeps this far from the screen's right edge and from the game's bottom bar.
    const float FirstPlaceGap = 60;

    sealed class Row
    {
        public string Id = "", Name = "";
        public ExperienceComponent Experience = null!;
        public readonly RateHistory Samples = new();
        public Control Root = null!;
        public Label LevelLabel = null!, RateLabel = null!, TimeLabel = null!;
        public SpinBox Target = null!;
        public ProgressBar Bar = null!;
        public bool UserSetTarget;
    }

    sealed class Settings
    {
        public float? X { get; set; }
        public float? Y { get; set; }
        public bool Hidden { get; set; }
        /// <summary>The window is folded down to its title.</summary>
        public bool Minimised { get; set; }
        public int Period { get; set; } = 2;
        /// <summary>Window size: 1 is the game's own size.</summary>
        public float Scale { get; set; } = 1f;
        /// <summary>Character name -> skill id -> target level.</summary>
        public Dictionary<string, Dictionary<string, int>> Targets { get; set; } = new();
    }

    ModContext _ctx = null!;
    Settings _settings = new();
    Game? _game, _failed;
    readonly List<Row> _rows = new();
    GameStyle _style = new();
    GameWindow? _window;
    Label? _empty;
    Button? _stripButton;
    bool _wasDown, _settingUp, _inCombat, _stripMissingLogged, _stopped, _unsaved, _guessing;
    readonly Tempo _sampling = new(SampleEveryMs);
    Control? _footer;
    ulong _lastSample;
    int _shown = -1;
    object? _fight;

    public void OnLoad(ModContext ctx)
    {
        _ctx = ctx;
        _settings = ReadSettings();
        ctx.Frame += OnFrame;
        try { StartSkilling(); }
        catch (Exception e) { ctx.Log("Skilling stop estimates disabled (the game was updated?): " + e); }
#if DEVTOOLS
        Dev.Start(ctx, this);
#endif
        ctx.Log("Ready. F10 opens and closes the combat goals window.");
    }

    // Resolve optional game types inside the guarded call, even if a game update removes a type entirely.
    [MethodImpl(MethodImplOptions.NoInlining)]
    void StartSkilling() => SkillingAction.Start(_ctx);

    void OnFrame()
    {
        if (_stopped) return;
        try { Step(); }
        catch (Exception e) { Stop(e); }
    }

    // One line, then nothing: an error on every frame would bury the log and help nobody.
    void Stop(Exception e)
    {
        if (_stopped) return;
        _stopped = true;
        _ctx.Log("Stopped (the game was updated?): " + e);
        FreeControl(_stripButton);
        DropWindow();
    }

    // A click on the window or the round button is handled outside of OnFrame: a game member that is gone must
    // end in the same one line there.
    void Click(Action action)
    {
        if (_stopped) return;
        try { action(); }
        catch (Exception e) { Stop(e); }
    }

    // Cleanup still attempts the other controls if one is already being destroyed.
    static void FreeControl(Node? node)
    {
        try { if (GodotObject.IsInstanceValid(node)) node!.QueueFree(); }
        catch (Exception) { }
    }

    void Step()
    {
        bool down = Input.IsKeyPressed(Key.F10);
        if (down && !_wasDown) ToggleWindow();
        _wasDown = down;

        if (!_sampling.Due()) return;
        TrackCharacter();
        if (_game == null) return;

        ulong now = Time.GetTicksMsec();
        bool gap = _lastSample != 0 && now - _lastSample > GapResetMs;
        _lastSample = now;
        FlushSettings();
        _inCombat = InCombat(_game);
        // The rate is "this fight, lately": time spent on other skills, or on another enemy, is not part of
        // it. Leaving combat or changing what is fought starts the history again.
        object? fight = _inCombat ? CurrentFight(_game) : null;
        if (!Equals(fight, _fight))
        {
            _fight = fight;
            foreach (Row row in _rows) row.Samples.Clear();
        }
        if (_inCombat) Sample(now, gap);
        Refresh(now);
        UpdateStripButton();
    }

#if DEVTOOLS
    // How a development picture builds the window: at another size, folded or not, at the first place as if no
    // place were saved. None of it is ever written to settings.json; SetPicture() with nothing builds the window
    // as the player has it again.
    float? _pictureScale;
    bool? _pictureFolded;
    bool _pictureFirstPlace, _devLogBuild;
    string _devBuild = "-";
    ulong _devBuiltAt;

    float WindowScale => _pictureScale ?? _settings.Scale;
    // A picture beside a window at 60% or 65% needs this one at that size too; the player's corner stops at 70%.
    float SmallestScale => Math.Min(MinScale, _pictureScale ?? MinScale);
    bool WindowFolded => _pictureFolded ?? _settings.Minimised;
    Vector2? SavedPlace => _pictureFirstPlace ? null : PlaceIn(_settings);

    internal GameWindow? Window => _window is { IsAlive: true } ? _window : null;
    internal bool WindowUp => _window is { IsAlive: true, Visible: true };
    internal bool InFight => _inCombat;
    internal int Lines => _shown;
    internal bool? StripButtonLit => GodotObject.IsInstanceValid(_stripButton) ? _stripButton!.ButtonPressed : null;

    /// <summary>What F10 and the round button do: a closed window opens, a folded one opens up, an open one closes.</summary>
    internal void DevToggle() => Click(ToggleWindow);

    /// <summary>The window built again for a picture; nothing given: as the player has it.</summary>
    internal void SetPicture(float? scale = null, bool? folded = null, bool firstPlace = false)
    {
        if (scale == _pictureScale && folded == _pictureFolded && firstPlace == _pictureFirstPlace) return;
        (_pictureScale, _pictureFolded, _pictureFirstPlace) = (scale, folded, firstPlace);
        Rebuild();
    }

    /// <summary>The window put somewhere else for a picture. MoveTo raises no Moved: the place is not saved.</summary>
    internal void DevMoveTo(Vector2 position) => Window?.MoveTo(position);

    /// <summary>What a development request may change of this mod, to put it back exactly.</summary>
    internal readonly record struct DevState(bool Hidden, bool Minimised, float? X, float? Y, Vector2? OnScreen);

    internal DevState DevNow => new(_settings.Hidden, _settings.Minimised, _settings.X, _settings.Y, Window?.Position);

    /// <summary>Closed or open, folded or not and the place as they were, in settings.json too, at the player's size.</summary>
    internal void DevPutBack(DevState before)
    {
        (_pictureScale, _pictureFolded, _pictureFirstPlace) = (null, null, false);
        (_settings.Hidden, _settings.Minimised, _settings.X, _settings.Y) = (before.Hidden, before.Minimised, before.X, before.Y);
        WriteSettings();
        Rebuild();
        // Where it stood on the screen, which lines may have pushed away from the saved place.
        if (before.OnScreen is { } at) Window?.MoveTo(at);
        UpdateStripButton();
    }

    /// <summary>The values a settings.json holds, whatever its layout and whichever keys it already had.</summary>
    internal static string DevSettingsValues(string? json) =>
        JsonSerializer.Serialize(json == null ? new Settings() : JsonSerializer.Deserialize<Settings>(json) ?? new Settings());

    /// <summary>How the window was last built: its size with no lines and where it was put.</summary>
    internal string DevBuildReading => _devBuild;

    internal string WindowReading => _window is not { IsAlive: true } window ? "no combat window"
        : $"combat rows={_shown}, scale={window.Style.Scale:P0} (saved {_settings.Scale:P0}), folded={window.Minimised} (saved {_settings.Minimised}), " +
          $"closed={_settings.Hidden}, visible={window.Visible}, window={window.Root.GetGlobalRect()}, {Gaps(window)}, " +
          $"screen={window.Root.GetViewportRect().Size}, bottom bar height={FooterHeight()}, layer={window.Layer.Layer}, " +
          $"saved place={_settings.X},{_settings.Y}, round button lit={StripButtonLit?.ToString() ?? "no round button"}";

    // How far the window is from the screen's right edge and from the top of the bottom bar.
    string Gaps(GameWindow window)
    {
        Rect2 at = window.Root.GetGlobalRect();
        Vector2 screen = window.Root.GetViewportRect().Size;
        return $"{screen.X - at.End.X:0} from the right edge, {screen.Y - FooterHeight() - at.End.Y:0} above the bottom bar";
    }

    // Where the window was put when it was built, and the least room it has had since: the screen less its size, and
    // less the bottom bar under it. From the two, where the place rule has it now (DevRulePlace).
    Vector2 _devPut, _devRoom;

    /// <summary>
    /// Where the place rule has the window now: where it was put when it was built, pushed up and left only as far as
    /// the screen and the bottom bar have needed since (KeepOnScreen clamps, and a clamp never lets it back down). A
    /// window anywhere else was moved by something else: a drag, or the fold button, which keeps its right edge.
    /// </summary>
    internal Vector2 DevRulePlace()
    {
        if (Window is { } window) NoteRoom(window);
        return new Vector2(Math.Clamp(_devPut.X, Math.Min(0, _devRoom.X), Math.Max(0, _devRoom.X)),
            Math.Clamp(_devPut.Y, 0, Math.Max(0, _devRoom.Y)));
    }

    void NoteRoom(GameWindow window)
    {
        if (window.Resizing) return;   // only scaled until the corner is let go; then it is built again
        Vector2 room = window.Root.GetViewportRect().Size - window.Root.Size - new Vector2(0, FooterHeight());
        _devRoom = new Vector2(Math.Min(_devRoom.X, room.X), Math.Min(_devRoom.Y, room.Y));
    }

    // A second after the window was built at a character's load: what it was built as next to what it is now, and
    // whether it is where the place rule has it.
    void LogBuild(GameWindow window, ulong now, int shown)
    {
        if (!_devLogBuild || now < _devBuiltAt + 1000) return;
        _devLogBuild = false;
        Vector2 rule = DevRulePlace(), at = window.Position;
        _ctx.Log($"Dev: combat window at the character's load: {_devBuild}; a second later: {window.Root.GetGlobalRect()}, {Gaps(window)}, " +
                 $"{shown} lines, waiting text shown={_empty?.Visible}, visible={window.Visible}; the place rule has it at {rule}: " +
                 (at.DistanceTo(rule) < 1 ? "it is there" : $"it is NOT THERE, {at.X - rule.X:0.#} across and {at.Y - rule.Y:0.#} down from it"));
    }
#else
    float WindowScale => _settings.Scale;
    float SmallestScale => MinScale;
    bool WindowFolded => _settings.Minimised;
    Vector2? SavedPlace => PlaceIn(_settings);
#endif

    static Vector2? PlaceIn(Settings settings) => settings is { X: { } x, Y: { } y } ? new Vector2(x, y) : null;

    // CurrentGame is null on the main menu and a new object for every character that is opened.
    void TrackCharacter()
    {
        Game? now = GameAccess.CurrentGame;
        if (now is not { IsSaveLoaded: true }) now = null;
        if (ReferenceEquals(now, _game) || (now != null && ReferenceEquals(now, _failed))) return;
        FlushSettings();
        _game = now;
        _failed = null;
        _rows.Clear();
        _lastSample = 0;
        _fight = null;
        _inCombat = false;
        FreeControl(_stripButton);
        _stripButton = null;
        _footer = null;
        DropWindow();
        if (now == null) return;
        try
        {
            _footer = GameAccess.FindGameView()?.FindChild("Footer", true, false) as Control;
            if (_footer == null) _ctx.Log("The game's bottom bar was not found. The combat window may overlap it.");
            BuildRows(now);
            BuildWindow(now);
#if DEVTOOLS
            _devLogBuild = true;
#endif
            _ctx.Log($"Character opened, tracking {_rows.Count} combat skills.");
        }
        catch (Exception e)
        {
            // Not tried again for this character: it would fail, and log, twice a second.
            _ctx.Log("Could not set up for this character (the game was updated?): " + e);
            _failed = now;
            _game = null;
            _rows.Clear();
            DropWindow();
        }
    }

    void BuildRows(Game game)
    {
        foreach (string id in SkillIds)
        {
            var skill = game.LeveledEntities.FirstOrDefault(s => s.Id == id);
            if (skill == null) { _ctx.Log($"Skill not found, skipped: {id}"); continue; }
            _rows.Add(new Row { Id = id, Name = skill.DisplayName.Text, Experience = skill.Experience });
        }
    }

    // ---- the window -------------------------------------------------------------------------------------

    // The frame, title bar, fold button, X and resize corner are the shared window's (GameWindow), as in the Item
    // Encyclopedia and the Combat Simulator; this mod fills its body.
    void BuildWindow(Game game)
    {
        _settingUp = true;
        _shown = -1;
        try
        {
            // The game's bar along the bottom stays in sight: the window is placed, and kept, above it.
            float footer = FooterHeight();
            // At the size the player chose, from 70% to 200% as before the shared frame. A window larger than the
            // room above the bottom bar keeps its title bar on the screen (GameWindow.KeepOnScreen), as before.
            var window = _window = new GameWindow(_ctx.Tree, Title, WindowScale, null, canMinimise: true, minimised: WindowFolded,
                bottomInset: footer, minScale: SmallestScale, maxScale: MaxScale);
            _style = window.Style;
            BuildBody(window, game);

            window.CloseRequested += () => Click(() => SetHidden(true));
            window.MinimisedChanged += folded => Click(() => Folded(folded));
            window.Moved += position => Click(() =>
            {
                _settings.X = position.X;
                _settings.Y = position.Y;
                WriteSettings();
            });
            window.ResizeRequested += size => Click(() => Resize(window, size));
            // The resize corner is the frame's sibling: its tooltip on the game's panel too, as AstrologyOverviewMod does.
            foreach (Control control in window.Layer.GetChildren().OfType<Control>()) control.Theme = window.Root.Theme;
            // Where the player left it, or the first time the bottom right corner. From there it stays where it is,
            // as it did before the shared frame: lines that would reach the bottom bar push it up, and it stays there
            // when they go.
            Vector2? saved = SavedPlace;
            window.MoveTo(saved ?? FirstPlace(window, footer));
#if DEVTOOLS
            Vector2 built = window.Root.GetCombinedMinimumSize();
            _devBuild = $"built at {window.Style.Scale:P0}{(window.Minimised ? ", folded" : "")}, {built.X:0}x{built.Y:0} with no lines, put at " +
                        (saved is { } place ? $"the saved place {place}" : $"the first place, {FirstPlaceGap} from the right edge and above the bottom bar ({footer:0} high)") +
                        $": {window.Position}";
            _devBuiltAt = Time.GetTicksMsec();
            _devPut = window.Position;
            _devRoom = window.Root.GetViewportRect().Size - built - new Vector2(0, footer);
#endif
        }
        finally { _settingUp = false; }
    }

    // Under the title bar, at the top right where the time period has always been: the footer's own period part, its
    // clock and its box as the footer draws them (its TimePeriodContainer: the clock at 16, then the box, 8 apart), with
    // no words of ours. The other windows' title bars hold only the title and their two buttons.
    void BuildBody(GameWindow window, Game game)
    {
        var top = _style.Spaced(new HBoxContainer { Name = "Period", Alignment = BoxContainer.AlignmentMode.End, MouseFilter = Control.MouseFilterEnum.Pass }, 8);
        top.AddChild(_style.Picture(GameStyle.Icon("TimeInterval_48"), 16));
        OptionButton period = _style.Dropdown();
        period.TooltipText = "XP rates are averaged over this much recent play";
        foreach (var p in Periods) period.AddItem(p.Label);
        period.Selected = Math.Clamp(_settings.Period, 0, Periods.Length - 1);
        period.ItemSelected += index => Click(() =>
        {
            _settings.Period = (int)index;
            WriteSettings();
            Refresh(Time.GetTicksMsec());
        });
        top.AddChild(period);
        window.Body.AddChild(top);

        var inner = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        inner.AddThemeStyleboxOverride("panel", _style.InnerPanel());
        window.Body.AddChild(inner);
        var list = _style.Spaced(new VBoxContainer { CustomMinimumSize = new Vector2(_style.Px(430), 0) }, 2);
        inner.AddChild(list);

        _empty = _style.Text("Waiting for combat XP...", GameStyle.TextGrey);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.CustomMinimumSize = new Vector2(0, _style.Px(28));
        list.AddChild(_empty);

        var saved = _settings.Targets.TryGetValue(game.CharacterName, out var mine) ? mine : new();
        foreach (Row row in _rows) list.AddChild(BuildRow(row, saved));
    }

    void DropWindow()
    {
        _window?.Free();
        _window = null;
    }

    // The same window again at another size. What was measured so far is kept.
    void Rebuild()
    {
        if (_game == null) return;
        DropWindow();
        BuildWindow(_game);
        Refresh(Time.GetTicksMsec());
    }

    // The first time, the bottom right corner, clear of the screen's edge and of the game's bottom bar, measured
    // with no lines ("Waiting for combat XP...").
    Vector2 FirstPlace(GameWindow window, float footer)
    {
        Vector2 screen = window.Root.GetViewportRect().Size, size = window.Root.GetCombinedMinimumSize();
        return new Vector2(screen.X - size.X - FirstPlaceGap, screen.Y - size.Y - footer - FirstPlaceGap);
    }

    // How high the game's bar along the bottom of the screen is (CombatSimMod.FooterHeight). The bar is looked
    // for once per character, in TrackCharacter.
    float FooterHeight() => GodotObject.IsInstanceValid(_footer) && _footer!.IsVisibleInTree() ? _footer.GetGlobalRect().Size.Y : 0;

    // GameWindow raises Moved next, which saves the place the fold left the window at.
    void Folded(bool folded)
    {
        _settings.Minimised = folded;
        WriteSettings();
        UpdateStripButton();
    }

    // The corner was let go: the same window again at that size (GameWindow gives it in steps of 5%, from 70% to 200%).
    void Resize(GameWindow window, float size)
    {
        _settings.Scale = size;
        _settings.X = window.Position.X;
        _settings.Y = window.Position.Y;
        WriteSettings();
        Rebuild();
    }

    // [icon] Hitpoints  28      1,271 /hr              1m 56s  left to [ 29 ]
    // [=============------------------------------------------------------]
    Control BuildRow(Row row, Dictionary<string, int> saved)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Pass, Visible = false };
        panel.AddThemeStyleboxOverride("panel", _style.StatLine());
        row.Root = panel;
        var column = _style.Spaced(new VBoxContainer(), 3);
        panel.AddChild(column);
        var line = _style.Spaced(new HBoxContainer(), 6);
        column.AddChild(line);

        string icon = row.Id[(row.Id.IndexOf(':') + 1)..];
        line.AddChild(_style.Picture(GameStyle.Icon(icon), 20));
        Label nameLabel = _style.Text(row.Name);
        nameLabel.CustomMinimumSize = new Vector2(_style.Px(72), 0);
        line.AddChild(nameLabel);
        row.LevelLabel = _style.Text("", GameStyle.TextGrey, 12, GameStyle.SemiBold);
        row.LevelLabel.CustomMinimumSize = new Vector2(_style.Px(22), 0);
        row.LevelLabel.TooltipText = "Current level";
        row.LevelLabel.MouseFilter = Control.MouseFilterEnum.Pass;
        line.AddChild(row.LevelLabel);

        row.RateLabel = _style.Text("", GameStyle.TextWhite, 14, GameStyle.Bold);
        row.RateLabel.HorizontalAlignment = HorizontalAlignment.Right;
        row.RateLabel.CustomMinimumSize = new Vector2(_style.Px(56), 0);
        line.AddChild(row.RateLabel);
        line.AddChild(_style.Text("/hr", GameStyle.TextWhite, 12));

        row.TimeLabel = _style.Text("", GameStyle.TextPaper, 14, GameStyle.SemiBold);
        row.TimeLabel.HorizontalAlignment = HorizontalAlignment.Right;
        row.TimeLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        line.AddChild(row.TimeLabel);
        line.AddChild(_style.Text("left to", GameStyle.TextGrey, 12));

        int level = row.Experience.Level, cap = row.Experience.LevelCap;
        row.Target = _style.GoalBox();
        row.Target.MinValue = 1;
        row.Target.MaxValue = Math.Max(cap, 1);
        row.Target.TooltipText = "Level goal";
        if (saved.TryGetValue(row.Id, out int target) && target > level)
        {
            row.Target.Value = Math.Min(target, cap);
            row.UserSetTarget = true;
        }
        else
        {
            row.Target.Value = Math.Min(level + 1, cap);
            row.UserSetTarget = false;
        }
        row.Target.ValueChanged += _ =>
        {
            if (_settingUp) return;
            Click(() =>
            {
                row.UserSetTarget = true;
                SaveTargets();
                Refresh(Time.GetTicksMsec());
            });
        };
        line.AddChild(row.Target);

        row.Bar = _style.LevelBar();
        column.AddChild(row.Bar);
        return panel;
    }

    void SetHidden(bool hidden)
    {
        _settings.Hidden = hidden;
        WriteSettings();
        Refresh(Time.GetTicksMsec());
        UpdateStripButton();
    }

    // F10 and the round button: a closed window opens, a folded one opens up, an open one closes, as the Combat
    // Simulator's ToggleWindow and the Item Encyclopedia's key do. Decided by what the window is, not by whether it is
    // on the screen: outside combat, where it is not shown, F10 opens a folded window up for the next fight instead of
    // closing it.
    void ToggleWindow()
    {
        if (_settings.Hidden) SetHidden(false);
        else if (_window is { IsAlive: true, Minimised: true }) _window.SetMinimised(false);
        else SetHidden(true);
    }

    // The window is for combat: outside it, it stays out of the way.
    bool WindowWanted => !_settings.Hidden && _inCombat;

    bool InCombat(Game game)
    {
        try { return game.ActionManager.ActiveActions.Any(a => a.Entity.Id == "MelvorBase:Combat"); }
        catch (Exception e) { Guessing(e); return true; }   // cannot tell: behave as if always in combat
    }

    // After a game update the mod may no longer be able to tell what is being fought. It goes on with a
    // reasonable guess, and says so once.
    void Guessing(Exception e)
    {
        if (_guessing) return;
        _guessing = true;
        _ctx.Log("Cannot tell what is being fought in this game version; rates are measured over all of combat. " + e.Message);
    }

    // What is being fought: the area, and in an ordinary area also the enemy. A dungeon is one fight from its
    // first enemy to its last, so there only the dungeon counts.
    object? CurrentFight(Game game)
    {
        try
        {
            if (!game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? e) || e is not CombatEntity combat) return "combat";
            ActiveCombatArea? area = combat.Areas.CurrentArea;
            return area?.Area is Dungeon ? (area, null) : (area, combat.Areas.SelectedEnemy);
        }
        catch (Exception e) { Guessing(e); return "combat"; }   // one fight for as long as combat runs
    }

    // ---- the button in the game's strip of round buttons on the right edge -------------------------------

    // That strip is the game's "FavouritesContainer"; a button added there with the game's own style
    // variation looks and behaves like its neighbours (lit green while the window is open).
    void UpdateStripButton()
    {
        if (_game == null) return;   // the strip belongs to a character's screen
        if (_stripButton == null || !GodotObject.IsInstanceValid(_stripButton))
        {
            _stripButton = null;
            if (GameAccess.FindGameView()?.FindChild("FavouritesContainer", true, false) is not BoxContainer strip)
            {
                if (!_stripMissingLogged) _ctx.Log("The game's widget strip was not found (the game was updated?). F10 still works.");
                _stripMissingLogged = true;
                return;
            }
            // The strip may be the one of the character before: its button goes, this one takes its place.
            if (strip.GetNodeOrNull("CombatGoalsButton") is { } stale) { stale.Name = "CombatGoalsButtonGone"; stale.QueueFree(); }
            _stripButton = new Button
            {
                Name = "CombatGoalsButton",
                ThemeTypeVariation = "SidebarCategoryButton",
                CustomMinimumSize = new Vector2(36, 36),
                Icon = GameStyle.Icon("Combat"),
                ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Center,
                ToggleMode = true,
                // Its tooltip at the game's size, as the strip is: the window's own follows the window's size.
                Theme = new GameStyle().TooltipTheme(),
                TooltipText = "Combat Goals: time left to a level goal for each combat skill (F10)",
            };
            _stripButton.Pressed += () => Click(ToggleWindow);
            strip.AddChild(_stripButton);
            // The style's own "pressed" look is a wider circle that is cut off at this size. While the window
            // is open, use the resting shape (same size and roundness as the neighbours) in the game's green.
            if (_stripButton.GetThemeStylebox("normal") is StyleBoxFlat resting)
            {
                var open = (StyleBoxFlat)resting.Duplicate();
                open.BgColor = new Color("275124");   // the fill only: a coloured border would draw a wider circle
                _stripButton.AddThemeStyleboxOverride("pressed", open);
                _stripButton.AddThemeStyleboxOverride("hover_pressed", open);
            }
        }
        _stripButton.Visible = _inCombat;
        // Lit while the window is open, not while it is closed or folded down to its title (CombatSimMod.SyncStripButton).
        _stripButton.SetPressedNoSignal(!_settings.Hidden && _window?.Minimised != true);
    }

    // ---- measuring --------------------------------------------------------------------------------------

    void Sample(ulong now, bool gap)
    {
        foreach (Row row in _rows)
        {
            if (gap) row.Samples.Clear();
            row.Samples.Add(now, row.Experience.Xp, LongestPeriod);
        }
    }

    double SelectedPeriod => Periods[Math.Clamp(_settings.Period, 0, Periods.Length - 1)].Seconds;

    void Refresh(ulong now)
    {
        // While its corner is held the window is only scaled from the size it had: lines that come or go wait until
        // it is let go, as before the shared frame.
        if (_window is not { IsAlive: true, Resizing: false } window) return;
        _settingUp = true;
        int shown = 0;
        try
        {
            foreach (Row row in _rows)
            {
                double rate = row.Samples.PerSecond(now, SelectedPeriod);
                int level = row.Experience.Level, cap = row.Experience.LevelCap;
                row.Target.MaxValue = Math.Max(cap, 1);   // the cap can rise while the window is up
                // Only skills that are gaining XP, and can still gain levels, are listed.
                row.Root.Visible = rate > 0 && level < cap;
                if (!row.Root.Visible) continue;
                shown++;

                // A goal that was reached moves on to the next level.
                if (row.Target.Value <= level)
                {
                    row.Target.Value = level + 1;
                    if (row.UserSetTarget) { row.UserSetTarget = false; SaveTargets(); }
                }

                var table = row.Experience.Table;
                double xp = row.Experience.Xp, from = table.GetXpForLevel(level), goal = table.GetXpForLevel((int)row.Target.Value);
                row.LevelLabel.Text = level.ToString();
                row.RateLabel.Text = (rate * 3600).ToString("N0");
                row.TimeLabel.Text = goal <= xp ? "now" : FormatTime((goal - xp) / rate);
                row.Bar.Value = goal > from ? Math.Clamp((xp - from) / (goal - from), 0, 1) : 1;
            }
        }
        finally { _settingUp = false; }

        _empty!.Visible = shown == 0;
        bool resized = shown != _shown;
        _shown = shown;
        window.Visible = WindowWanted;
        // The bottom bar may have been folded or opened since the last look.
        window.KeepAbove(FooterHeight());
        if (resized)
        {
            // Shrunk to the lines that are left: the window is in no container, so it only ever grows by itself.
            window.Root.ResetSize();
            window.KeepOnScreen();
        }
#if DEVTOOLS
        NoteRoom(window);
        LogBuild(window, now, shown);
#endif
    }

    static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds > 3650.0 * 86400) return "years";
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
        if (t.TotalHours >= 1) return $"{t.Hours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }

    // ---- settings.json in the mod's folder --------------------------------------------------------------

    string SettingsFile => Path.Combine(_ctx.Directory, "settings.json");

    Settings ReadSettings()
    {
        Settings settings = ModFiles.ReadJson<Settings>(SettingsFile, _ctx.Log);
        settings.Targets ??= new();   // "Targets": null in a hand-edited file
        return settings;
    }

    // Dragging, a click and every step of a goal box all change a setting: they only mark it, and the file is
    // written once, on the next one-second tick.
    void WriteSettings() => _unsaved = true;

    void FlushSettings()
    {
        if (!_unsaved) return;
        _unsaved = false;
        // Two copies of the game can run at once: keep the other characters' goals as they are on disk.
        if (File.Exists(SettingsFile))
            foreach (var (name, targets) in ReadSettings().Targets)
                if (name != _game?.CharacterName) _settings.Targets[name] = targets;
        ModFiles.WriteJson(SettingsFile, _settings, _ctx.Log);
    }

    void SaveTargets()
    {
        if (_game == null) return;
        _settings.Targets[_game.CharacterName] = _rows.Where(r => r.UserSetTarget).ToDictionary(r => r.Id, r => (int)r.Target.Value);
        WriteSettings();
    }
}
