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
/// level you enter per skill. Only skills that are gaining XP are listed. Drag the window anywhere, drag its
/// corner for another size; F10 hides it.
/// </summary>
public class CombatGoalsMod : IMod
{
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
    const float MinScale = 0.7f, MaxScale = 2f;
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
    CanvasLayer? _layer;
    PanelContainer? _window;
    Control? _grip;
    Label? _empty;
    Button? _stripButton;
    bool _wasDown, _settingUp, _dragging, _placed, _resizing, _inCombat, _stripMissingLogged, _stopped, _unsaved, _guessing;
    Vector2 _resizeFrom;
    float _resizeTo = 1f;
    readonly Tempo _sampling = new(SampleEveryMs);
    Control? _footer;
    float _bottomInset;
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
        ctx.Log("Ready. F10 shows/hides the combat goals window.");
    }

    // Resolve optional game types inside the guarded call, even if a game update removes a type entirely.
    [MethodImpl(MethodImplOptions.NoInlining)]
    void StartSkilling() => SkillingAction.Start(_ctx);

    void OnFrame()
    {
        if (_stopped) return;
        try { Step(); }
        catch (Exception e)
        {
            // One line, then nothing: an error on every frame would bury the log and help nobody.
            _stopped = true;
            _ctx.Log("Stopped (the game was updated?): " + e);
            FreeControl(_stripButton);
            DropWindow();
        }
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
        if (down && !_wasDown) SetHidden(!_settings.Hidden);
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
#if DEVTOOLS
        // Development: a file "scale.request" holding a number rebuilds the window at that size.
        string request = Path.Combine(_ctx.Directory, "scale.request");
        if (File.Exists(request))
        {
            string text = File.ReadAllText(request).Trim();
            File.Delete(request);
            if (float.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out float scale))
            {
                _settings.Scale = Math.Clamp(scale, MinScale, MaxScale);
                SavePosition();
                Rebuild();
            }
        }
#endif
    }

#if DEVTOOLS
    // A development picture's size. It is never written to settings.json, and null puts the window back at
    // the size the player chose.
    float? _pictureScale;
    float WindowScale => _pictureScale ?? _settings.Scale;

    internal bool WindowUp => _layer?.Visible == true && _window != null;

    /// <summary>A positive number: the largest size; a negative one: the smallest; null: the player's.</summary>
    internal void SetPictureScale(float? largest)
    {
        float? scale = largest is { } big ? (big > 0 ? MaxScale : MinScale) : null;
        if (scale == _pictureScale) return;
        _pictureScale = scale;
        Rebuild();
    }

    internal string WindowReading => _window == null ? "no combat window"
        : $"combat rows={_shown}, scale={_style.Scale:P0} (saved {_settings.Scale:P0}), window={_window.GetGlobalRect()}, " +
          $"screen={_window.GetViewportRect().Size}, bottom bar height={_bottomInset}";
#else
    float WindowScale => _settings.Scale;
#endif

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
        _bottomInset = 0;
        DropWindow();
        if (now == null) return;
        try
        {
            _footer = GameAccess.FindGameView()?.FindChild("Footer", true, false) as Control;
            if (_footer == null) _ctx.Log("The game's bottom bar was not found. The combat window may overlap it.");
            BuildRows(now);
            BuildWindow(now);
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

    void BuildWindow(Game game)
    {
        _settingUp = true;
        _placed = false;
        _shown = -1;
        _style = new GameStyle(Math.Clamp(WindowScale, MinScale, MaxScale));
        _layer = new CanvasLayer { Layer = 100, Visible = false };

        _window = new PanelContainer { MouseDefaultCursorShape = Control.CursorShape.Move };
        _window.AddThemeStyleboxOverride("panel", _style.WindowPanel());
        _window.Theme = _style.TooltipTheme();
        _window.GuiInput += OnWindowInput;
        _window.Resized += KeepOnScreen;
        _layer.AddChild(_window);

        var column = _style.Spaced(new VBoxContainer(), 8);
        _window.AddChild(column);
        column.AddChild(BuildTitleBar());

        var inner = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        inner.AddThemeStyleboxOverride("panel", _style.InnerPanel());
        column.AddChild(inner);
        var list = _style.Spaced(new VBoxContainer { CustomMinimumSize = new Vector2(_style.Px(430), 0) }, 2);
        inner.AddChild(list);

        _empty = _style.Text("Waiting for combat XP...", GameStyle.TextGrey);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.CustomMinimumSize = new Vector2(0, _style.Px(28));
        list.AddChild(_empty);

        var saved = _settings.Targets.TryGetValue(game.CharacterName, out var mine) ? mine : new();
        foreach (Row row in _rows) list.AddChild(BuildRow(row, saved));

        _layer.AddChild(BuildGrip());
        _window.ItemRectChanged += PlaceGrip;
        _ctx.Tree.Root.AddChild(_layer);
        _settingUp = false;
    }

    void DropWindow()
    {
        FreeControl(_layer);
        _layer = null; _window = null; _grip = null;
        _dragging = _resizing = false;
    }

    // The same window again at another size. What was measured so far is kept.
    void Rebuild()
    {
        if (_game == null) return;
        DropWindow();
        BuildWindow(_game);
        Refresh(Time.GetTicksMsec());
    }

    Control BuildTitleBar()
    {
        var bar = _style.Spaced(new HBoxContainer(), 6);
        bar.AddChild(_style.Picture(GameStyle.Icon("Combat"), 20));
        var title = _style.Text("Combat Goals", GameStyle.TextWhite, 16, GameStyle.Bold);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        bar.AddChild(title);

        bar.AddChild(_style.Picture(GameStyle.Icon("TimeInterval_48"), 16));
        OptionButton period = _style.Dropdown();
        period.TooltipText = "XP rates are averaged over this much recent play";
        foreach (var p in Periods) period.AddItem(p.Label);
        period.Selected = Math.Clamp(_settings.Period, 0, Periods.Length - 1);
        period.ItemSelected += index =>
        {
            _settings.Period = (int)index;
            WriteSettings();
            Refresh(Time.GetTicksMsec());
        };
        bar.AddChild(period);

        var hide = new TextureButton
        {
            TextureNormal = GameStyle.Icon("eye"),
            IgnoreTextureSize = true,
            StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(_style.Px(18), _style.Px(18)),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            TooltipText = "Hide (F10 shows it again)",
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            Modulate = new Color(1, 1, 1, 0.75f),
        };
        hide.Pressed += () => SetHidden(true);
        bar.AddChild(hide);
        return bar;
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
            row.UserSetTarget = true;
            SaveTargets();
            Refresh(Time.GetTicksMsec());
        };
        line.AddChild(row.Target);

        row.Bar = _style.LevelBar();
        column.AddChild(row.Bar);
        return panel;
    }

    // Drag from anywhere on the window that is not a control of its own.
    void OnWindowInput(InputEvent e)
    {
        if (_window == null) return;
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            _dragging = button.Pressed;
            if (!_dragging) SavePosition();
        }
        else if (e is InputEventMouseMotion motion && _dragging)
        {
            _window.Position += motion.Relative;
            KeepOnScreen();
        }
    }

    void SavePosition()
    {
        if (_window == null) return;
        _settings.X = _window.Position.X;
        _settings.Y = _window.Position.Y;
        WriteSettings();
    }

    // The corner to drag for another size. While dragging, the window is only stretched (cheap, slightly
    // soft); on release it is built again at the new size, so text and icons are sharp at every size.
    Control BuildGrip()
    {
        int size = _style.Px(14);
        var grip = new Control
        {
            CustomMinimumSize = new Vector2(size, size), Size = new Vector2(size, size),
            MouseDefaultCursorShape = Control.CursorShape.Fdiagsize,
            TooltipText = "Drag to resize",
        };
        grip.Theme = _window!.Theme;   // the grip is a sibling of the window
        grip.Draw += () =>
        {
            var color = new Color(GameStyle.TextGrey, 0.7f);
            float width = Math.Max(1f, _style.Scale);
            for (int i = 1; i <= 3; i++)
            {
                float d = size * i / 4f;
                grip.DrawLine(new Vector2(size - d, size), new Vector2(size, size - d), color, width);
            }
        };
        grip.GuiInput += OnGripInput;
        return _grip = grip;
    }

    void PlaceGrip()
    {
        if (_window == null || _grip == null || !GodotObject.IsInstanceValid(_grip)) return;
        float inset = _style.Px(4) * _window.Scale.X;
        _grip.Scale = _window.Scale;
        _grip.Position = _window.Position + _window.Size * _window.Scale - _grip.Size * _grip.Scale - new Vector2(inset, inset);
    }

    void OnGripInput(InputEvent e)
    {
        if (_window == null) return;
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            if (button.Pressed)
            {
                _resizing = true;
                _resizeFrom = button.GlobalPosition;
                _resizeTo = _style.Scale;
            }
            else if (_resizing)
            {
                _resizing = false;
                _settings.Scale = MathF.Round(_resizeTo * 20f) / 20f;   // steps of 5%
                SavePosition();
                Rebuild();
            }
        }
        else if (e is InputEventMouseMotion motion && _resizing)
        {
            // Follow whichever direction was dragged further, keeping the window's proportions.
            Vector2 moved = motion.GlobalPosition - _resizeFrom, size = _window.Size;
            float factor = Math.Max((size.X + moved.X) / size.X, (size.Y + moved.Y) / size.Y);
            _resizeTo = Math.Clamp(_style.Scale * factor, MinScale, MaxScale);
            _window.Scale = Vector2.One * (_resizeTo / _style.Scale);
            PlaceGrip();
        }
    }

    void KeepOnScreen()
    {
        if (_window == null || !_placed || !_window.IsInsideTree()) return;
        Vector2 screen = _window.GetViewportRect().Size, size = _window.Size * _window.Scale;
        _window.Position = new Vector2(
            Math.Clamp(_window.Position.X, 0, Math.Max(0, screen.X - size.X)),
            Math.Clamp(_window.Position.Y, 0, Math.Max(0, screen.Y - _bottomInset - size.Y)));
        PlaceGrip();
    }

    void SetHidden(bool hidden)
    {
        _settings.Hidden = hidden;
        WriteSettings();
        Refresh(Time.GetTicksMsec());
        UpdateStripButton();
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
            _stripButton.Toggled += open => { if (open == _settings.Hidden) SetHidden(!open); };
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
        _stripButton.SetPressedNoSignal(!_settings.Hidden);
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
        if (_layer == null || _window == null || !GodotObject.IsInstanceValid(_layer) || _resizing) return;
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
        _layer.Visible = WindowWanted;
        float footer = GodotObject.IsInstanceValid(_footer) && _footer!.IsVisibleInTree() ? _footer.GetGlobalRect().Size.Y : 0;
        bool footerChanged = _bottomInset != footer;
        _bottomInset = footer;
        if (resized || !_placed)
        {
            _window.ResetSize();   // shrink to the rows that are left
            Place();
        }
        else if (footerChanged) KeepOnScreen();
    }

    // Where the player left it; the first time, the bottom right corner, clear of the game's footer.
    void Place()
    {
        if (_window == null) return;
        if (!_placed)
        {
            _placed = true;
            Vector2 screen = _window.GetViewportRect().Size, size = _window.GetCombinedMinimumSize();
            _window.Position = _settings is { X: { } x, Y: { } y }
                ? new Vector2(x, y)
                : new Vector2(screen.X - size.X - FirstPlaceGap, screen.Y - size.Y - _bottomInset - FirstPlaceGap);
        }
        KeepOnScreen();
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
