using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2.Entities.OfflineProgress;
using GamesByMalcs.Melvor2.Localization;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace AwaySummary;

public sealed partial class AwaySummaryMod
{
    // The column's own title. Not the game's "Welcome Back!": that is the game's window after a restart, and
    // the guides tell the player the two apart.
    const string Heading = "While You Were Away";
    // A heading of the mod's own over the stops and defeats, which the game keeps in its event log.
    const string EventsHeading = "Event Log";
    // How long a new window may take to be laid out before it is measured anyway (see Settle).
    const ulong SettleLimitMs = 1000;

    Button? _button;
    bool _matchedStrip;
    GameWindow? _window;
    // What the window shows, so it can be built again at another size.
    Summary? _shown;
    bool _shownPreview;
    float _requested, _inset;
    Control? _footer;
    ScrollContainer? _scroll;
    VBoxContainer? _rows;
    Vector2 _fullMinimum, _available;
    ulong _settleSince;
    Vector2? _lastMeasure;
    readonly HashSet<string> _reported = new();

    void ReportOnce(string key, string message)
    {
        if (_reported.Add(key)) _ctx.Log(message);
    }

    void EnsureButton()
    {
        if (GodotObject.IsInstanceValid(_button))
        {
            if (!_matchedStrip && _button!.GetParent() is BoxContainer buttons) MatchStrip(buttons);
            return;
        }
        _button = null;
        if (GameAccess.FindGameView()?.FindChild("FavouritesContainer", true, false) is not BoxContainer strip)
        {
            ReportOnce("strip", "The game's widget strip was not found. Away summaries can still open automatically.");
            return;
        }
        _button = new Button
        {
            Name = "AwaySummaryButton",
            ThemeTypeVariation = "SidebarCategoryButton",
            CustomMinimumSize = new Vector2(36, 36),
            Icon = GameStyle.Icon("history") ?? GameStyle.Icon("GuideBook"),
            ExpandIcon = true,
            IconAlignment = HorizontalAlignment.Center,
            TooltipText = "Away Summary: what changed while you were away",
            ToggleMode = true,
        };
        _button.Toggled += open => Guard(() => { if (open) Open(); else Close(); });
        strip.AddChild(_button);
        StyleBox resting = _button.GetThemeStylebox("normal");
        var pressed = (StyleBox)resting.Duplicate();
        if (pressed is StyleBoxFlat flat &&
            GameStyle.Load<StyleBoxFlat>("res://Assets/Styles/Button/button_toggled_green.tres") is { } green)
            flat.BgColor = green.BgColor;
        _button.AddThemeStyleboxOverride("pressed", pressed);
        _button.AddThemeStyleboxOverride("hover_pressed", pressed);
        _matchedStrip = false;
        MatchStrip(strip);
        _button.SetPressedNoSignal(_window?.Visible == true);
    }

    static readonly string[] Neighbours = ["ItemEncyclopediaButton", "CombatSimButton", "CombatGoalsButton"];

    // The other mods add their overrides after entering the strip, in no particular load order.
    void MatchStrip(BoxContainer strip)
    {
        foreach (string name in Neighbours)
        {
            if (strip.GetNodeOrNull<Button>(name) is not { } neighbour || !neighbour.HasThemeStyleboxOverride("pressed"))
                continue;
            StyleBox open = neighbour.GetThemeStylebox("pressed");
            _button!.AddThemeStyleboxOverride("pressed", open);
            _button.AddThemeStyleboxOverride("hover_pressed", open);
            _matchedStrip = true;
            return;
        }
    }

    float FooterHeight()
    {
        if (!GodotObject.IsInstanceValid(_footer))
            _footer = GameAccess.FindGameView()?.FindChild("Footer", true, false) as Control;
        if (_footer != null) return _footer.IsVisibleInTree() ? _footer.GetGlobalRect().Size.Y : 0;
        ReportOnce("footer", "The game's bottom bar was not found. The summary may cover it.");
        return 0;
    }

    Vector2 Available(float footer) => _ctx.Tree.Root.GetVisibleRect().Size - new Vector2(0, footer);

    // The fixed part is measured with this window's own title, settings and one summary row at the
    // current font size. Further rows can scroll. GameWindow's resize handle moves in steps of 5%.
    float LargestScale()
    {
        if (_fullMinimum.X <= 0 || _fullMinimum.Y <= 0) return GameWindow.MaxScale;
        float fits = Math.Min(_available.X / _fullMinimum.X, _available.Y / _fullMinimum.Y);
        return Math.Clamp(MathF.Floor(fits * 20) / 20, GameWindow.MinScale, GameWindow.MaxScale);
    }

    void Open(Summary? arrived = null)
    {
        if (!CanShow || OfflineSummaryUp())
        {
            Close();
            return;
        }
        if (arrived != null) _last = arrived;
        Show(_last, _settings.Scale, _settings.Folded);
    }

    // At the size asked for. Only once the window has been laid out (Settle) is it known whether that size
    // fits above the bottom bar; if it does not, the window is built again at the largest size that does.
    void Show(Summary? summary, float requested, bool folded, bool preview = false)
    {
        _shown = summary;
        _shownPreview = preview;
        _requested = Math.Clamp(requested, GameWindow.MinScale, GameWindow.MaxScale);
        Build(_requested, folded);
    }

    void Build(float scale, bool folded)
    {
        _inset = FooterHeight();
        _available = Available(_inset);
        Vector2? at = !_shownPreview && _settings is { X: { } x, Y: { } y } ? new Vector2(x, y) : null;
        ReleaseWindow();
        // Folded or open from the start, as ItemEncyclopediaMod.Build does.
        var window = _window = new GameWindow(_ctx.Tree, "Away Summary", scale, at,
            canMinimise: true, minimised: folded, bottomInset: _inset);
        BuildBody(window, _shown, _shownPreview);
        window.CloseRequested += Close;
        window.MinimisedChanged += value =>
        {
            if (!_shownPreview)
            {
                _settings.Folded = value;
                Remember();
            }
            // Opened up again: the list was not laid out while only the title showed.
            if (!value) StartSettling(seeThrough: false);
        };
        if (!_shownPreview)
        {
            window.Moved += position =>
            {
                _settings.X = position.X;
                _settings.Y = position.Y;
                Remember();
            };
            window.ResizeRequested += value => Guard(() =>
            {
                _settings.Scale = Math.Min(value, LargestScale());
                _settings.X = window.Position.X;
                _settings.Y = window.Position.Y;
                Remember();
                Open();
            });
        }
        window.Visible = true;
        if (!folded) StartSettling(seeThrough: true);
        if (GodotObject.IsInstanceValid(_button)) _button!.SetPressedNoSignal(true);
    }

    void StartSettling(bool seeThrough)
    {
        _settleSince = Math.Max(1, Time.GetTicksMsec());
        _lastMeasure = null;
        if (seeThrough) SetSeeThrough(true);
    }

    // Before its containers have laid it out, a wrapping text reports one word per line, so a window measured
    // at once looks far taller than it is and would be made smaller than asked. It is measured on the frames
    // after it was built, once two frames in a row agree (or after SettleLimitMs at the most). Until then it
    // is drawn see-through, so its first, unfinished size is never seen.
    void Settle()
    {
        if (_window?.Visible != true || _window.Minimised)
        {
            EndSettling();
            return;
        }
        Vector2 measured = Measure(_window);
        bool steady = _lastMeasure == measured;
        _lastMeasure = measured;
        ulong waited = Time.GetTicksMsec() - _settleSince;
        if (!steady && waited < SettleLimitMs) return;
        _fullMinimum = measured;
        EndSettling();
        float largest = LargestScale();
#if DEVTOOLS
        if (_requestActive)
            _ctx.Log($"Dev: laid out after {waited} ms{(steady ? "" : " (not steady)")}: scale {_window.Style.Scale:0.##}, " +
                $"asked {_requested:0.##}, fixed minimum at full size {measured}, largest {largest:0.##}.");
#endif
        if (_window.Style.Scale > largest + 0.001f) Build(largest, folded: false);
        else SizeScroll();
    }

    void EndSettling()
    {
        _settleSince = 0;
        SetSeeThrough(false);
    }

    void SetSeeThrough(bool on)
    {
        if (_window is not { IsAlive: true }) return;
        // The window's frame and its resize corner are the layer's children.
        foreach (Node node in _window.Layer.GetChildren())
            if (node is CanvasItem item) item.Modulate = on ? Colors.Transparent : Colors.White;
    }

    // On the clock: the screen or the bottom bar may have changed size since the window was built.
    void FitToViewport()
    {
        if (_window?.Visible != true || _settleSince != 0) return;
        if (!_window.Minimised) _fullMinimum = Measure(_window);
        var available = Available(FooterHeight());
        if (available != _available)
        {
            _available = available;
            Build(Math.Min(_requested, LargestScale()), _window.Minimised);
        }
        else if (!_window.Minimised && _window.Style.Scale > LargestScale() + 0.001f) Build(LargestScale(), folded: false);
        else SizeScroll();
    }

    Vector2 Measure(GameWindow window)
    {
        Vector2 minimum = window.Root.GetCombinedMinimumSize();
        if (_scroll != null && _rows != null)
        {
            minimum.Y -= _scroll.CustomMinimumSize.Y;
            // Include a value row beneath its heading, or the first event when there are only events.
            if (_rows.GetChildCount() != 0)
            {
                var first = _rows.GetChild<Control>(0);
                minimum.Y += first.GetCombinedMinimumSize().Y;
                if (first is not PanelContainer && _rows.GetChildCount() > 1)
                    minimum.Y += _rows.GetThemeConstant("separation") + _rows.GetChild<Control>(1).GetCombinedMinimumSize().Y;
            }
        }
        return minimum / window.Style.Scale;
    }

    // The list gets the height that is left above the bottom bar, and the window is brought down to what it
    // shows. The window is in no container, so it only ever grows by itself: without the reset it keeps the
    // height of its first, unfinished layout (see Settle), also when there is no list to size.
    void SizeScroll()
    {
        if (_window == null || _window.Minimised) return;
        if (_scroll != null && _rows != null)
        {
            float fixedHeight = _window.Root.GetCombinedMinimumSize().Y - _scroll.CustomMinimumSize.Y;
            float height = Math.Clamp(_rows.GetCombinedMinimumSize().Y, 0, Math.Max(0, _available.Y - fixedHeight));
            if (_scroll.CustomMinimumSize.Y != height) _scroll.CustomMinimumSize = new Vector2(0, height);
        }
        if (_window.Root.Size == _window.Root.GetCombinedMinimumSize()) return;
        _window.Root.ResetSize();
        _window.KeepOnScreen();
    }

    void BuildBody(GameWindow window, Summary? summary, bool preview)
    {
        GameStyle style = window.Style;
        window.Body.AddChild(style.Column(Heading, out var body));
        if (summary == null) body.AddChild(style.Text("No away summary yet.", GameStyle.TextGrey));
        else
        {
            body.AddChild(Stat(style, style.Picture(GameStyle.Icon("TimeInterval_48"), 16),
                Rich(style, TimeText(summary.Duration, sentence: true), wrap: true)));
            if (summary.Changes.Count != 0 || summary.Events.Count != 0)
            {
                _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
                body.AddChild(_scroll);
                _rows = style.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 4);
                _scroll.AddChild(_rows);
                // The changes come in the game's order of its headings: one heading over each run of them.
                string? heading = null;
                foreach (Change line in summary.Changes)
                {
                    string title = line.Category.DisplayName.Text;
                    if (title != heading) AddCategory(style, _rows, heading = title);
                    _rows.AddChild(ChangeRow(style, line));
                }
                if (summary.Events.Count != 0)
                {
                    if (summary.Changes.Count != 0) AddCategory(style, _rows, EventsHeading);
                    foreach (var note in summary.Events) _rows.AddChild(EventRow(style, note));
                }
            }
        }

        window.Body.AddChild(style.Column("Settings", out var settings));
        var enabled = style.TickBox("Away summaries", _settings.Enabled);
        settings.AddChild(enabled);
        var minutes = style.GoalBox();
        minutes.MinValue = 1;
        minutes.MaxValue = MaxMinutes;
        minutes.Value = _settings.MinutesAway;
        var caption = style.Text("Minutes away");
        caption.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        settings.AddChild(Stat(style, caption, minutes));
        if (preview) return;
        enabled.Toggled += on =>
        {
            _settings.Enabled = on;
            _away = null;
            _pending = null;
            Remember();
        };
        minutes.ValueChanged += value => { _settings.MinutesAway = (int)value; Remember(); };
    }

    string TimeText(TimeSpan elapsed, bool sentence = false)
    {
        try
        {
            // OfflineSnapshotModal formats milliseconds through the game's localized numbertime formatter.
            string format = sentence ? "[center][b]You were away for {0:numbertime(ms):auto}[/b][/center]"
                : "{0:numbertime(ms):auto}";
            return new LocalizedString(format).Format(elapsed.TotalMilliseconds);
        }
        catch (Exception e)
        {
            ReportOnce("time", $"Using plain elapsed times: {e.Message}");
            return elapsed.ToString("c");
        }
    }

    void AddCategory(GameStyle style, VBoxContainer rows, string title)
    {
        OfflineSnapshotCategoryLabel? heading = null;
        try
        {
            var scene = GameStyle.Load<PackedScene>(OfflineSnapshotCategoryLabel.ScenePath);
            heading = scene?.Instantiate<OfflineSnapshotCategoryLabel>()
                ?? throw new InvalidOperationException("The summary heading scene is missing.");
            rows.AddChild(heading);
            heading.SetTitle(title);
            ScaleScene(heading, style);
        }
        catch (Exception e)
        {
            heading?.Free();
            ReportOnce("heading", $"Using plain category headings: {e.Message}");
            rows.AddChild(style.Section(title));
        }
    }

    // The game's scene is made for 100%. Its fonts, gaps and minimum sizes are brought to this window's size,
    // as GameStyle.Px does for the mod's own controls. A label's font can come from LabelSettings, which wins
    // over the theme (GameStyle.PlaceAfterText notes the same).
    static void ScaleScene(Control scene, GameStyle style)
    {
        if (Math.Abs(style.Scale - 1f) < 0.001f) return;
        int Scaled(int value) => (int)MathF.Round(value * style.Scale);
        var controls = new List<Control> { scene };
        controls.AddRange(scene.FindChildren("*", "Control", true, false).OfType<Control>());
        foreach (Control control in controls)
        {
            if (control.CustomMinimumSize != Vector2.Zero) control.CustomMinimumSize = (control.CustomMinimumSize * style.Scale).Round();
            switch (control)
            {
                case Label { LabelSettings: { } settings } label:
                    var sized = (LabelSettings)settings.Duplicate();
                    sized.FontSize = style.Px(settings.FontSize);
                    sized.OutlineSize = Scaled(settings.OutlineSize);
                    sized.ShadowSize = Scaled(settings.ShadowSize);
                    sized.ShadowOffset = (settings.ShadowOffset * style.Scale).Round();
                    sized.LineSpacing = MathF.Round(settings.LineSpacing * style.Scale);
                    label.LabelSettings = sized;
                    break;
                case Label label:
                    label.AddThemeFontSizeOverride("font_size", style.Px(label.GetThemeFontSize("font_size")));
                    break;
                case BoxContainer box:
                    box.AddThemeConstantOverride("separation", Scaled(box.GetThemeConstant("separation")));
                    break;
                case MarginContainer margin:
                    foreach (string side in new[] { "margin_left", "margin_top", "margin_right", "margin_bottom" })
                        margin.AddThemeConstantOverride(side, Scaled(margin.GetThemeConstant(side)));
                    break;
            }
        }
    }

    Control ChangeRow(GameStyle style, Change change)
    {
        if (change.Message is { } message)
        {
            try
            {
                // The native center text includes the game's icon and localized name. Keep its markup,
                // but let the Details-style row align the name at the left and the total at the right.
                // The total is the game's own text, sign and colour as they are ("+46", "-23").
                string name = message.GetCenterText().Replace("[right]", "").Replace("[/right]", "")
                    .Replace("[center]", "").Replace("[/center]", "");
                return Stat(style, Rich(style, name, wrap: true), Rich(style, message.GetGainText()));
            }
            catch (Exception e) { ReportOnce("row", $"Using plain summary rows: {e.Message}"); }
        }
        var label = style.Text(change.Name);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        // The same sign as the game's rows: its "+{0:N0}" for a gain, and {0:N0} of the negative number for a loss.
        string amount = (change.Amount > 0 ? "+" : "") + change.Amount.ToString("N0");
        return Stat(style, style.Picture(change.Icon, 20), label, style.Text(amount, GameStyle.TextPaper, 14, GameStyle.Bold));
    }

    // A stop or a defeat as the game's event log has it: its title, its description under it, then when it
    // happened. The small grey lines are indented under the title, as EncyclopediaView.Small does.
    Control EventRow(GameStyle style, AwayEvent note)
    {
        var lines = style.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 2);
        var top = style.Spaced(new HBoxContainer(), 6);
        top.AddChild(style.Picture(note.Icon, 20));
        top.AddChild(Wrapped(style, style.Text(note.Title)));
        lines.AddChild(top);
        if (note.Detail is { } detail) lines.AddChild(Small(style, detail));
        lines.AddChild(Small(style, $"{TimeText(note.At)} after you left"));
        return Stat(style, lines);
    }

    static Label Wrapped(GameStyle style, Label label)
    {
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(style.Px(120), 0);
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        return label;
    }

    static Control Small(GameStyle style, string text)
    {
        var indent = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        indent.AddThemeConstantOverride("margin_left", style.Px(26));
        indent.AddChild(Wrapped(style, style.Text(text, GameStyle.TextGrey, 12)));
        return indent;
    }

    static RichTextLabel Rich(GameStyle style, string text, bool wrap = false)
    {
        var label = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false,
            AutowrapMode = wrap ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off,
            SizeFlagsHorizontal = wrap ? Control.SizeFlags.ExpandFill : Control.SizeFlags.ShrinkEnd,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("normal_font_size", style.Px(14));
        label.AddThemeFontSizeOverride("bold_font_size", style.Px(14));
        if (GameStyle.Bold is { } bold) label.AddThemeFontOverride("bold_font", bold);
        label.Text = text;
        return label;
    }

    static Control Stat(GameStyle style, params Control[] cells)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        panel.AddThemeStyleboxOverride("panel", style.StatLine());
        var row = style.Spaced(new HBoxContainer(), 6);
        panel.AddChild(row);
        foreach (Control cell in cells) row.AddChild(cell);
        return panel;
    }

    void Close()
    {
        if (_window != null) _window.Visible = false;
        if (GodotObject.IsInstanceValid(_button)) _button!.SetPressedNoSignal(false);
        SaveSettings();
    }

    void ReleaseWindow()
    {
        if (_window != null) _window.Visible = false;
        _window?.Free();
        _window = null;
        _scroll = null;
        _rows = null;
        _settleSince = 0;
    }

    void DropControls()
    {
        Close();
        ReleaseWindow();
        if (GodotObject.IsInstanceValid(_button)) _button!.QueueFree();
        _button = null;
        _footer = null;
    }
}
