using System;
using System.Collections.Generic;
using Godot;

namespace MelvorMods.Shared;

/// <summary>
/// A window that looks like the game's own large pop-ups: the heavy frame, the title on a strip of paper, a
/// red X to close. It can be dragged from any empty spot and resized from its bottom right corner. A window
/// that asks for it also has a button beside the X that folds it down to its title, to be put aside without
/// closing it.
///
/// A size is a rebuild, not a stretch: while the corner is dragged the window is only scaled (cheap, a little
/// soft); on release <see cref="ResizeRequested"/> is raised and the owner builds a new window at that scale,
/// so text and icons are sharp at every size.
/// </summary>
public sealed class GameWindow
{
    /// <summary>The sizes the corner reaches, unless a window is made with others.</summary>
    public const float MinScale = 0.6f, MaxScale = 1.6f;

    public GameStyle Style { get; }
    public CanvasLayer Layer { get; }
    public PanelContainer Root { get; }
    /// <summary>Where the owner puts the window's content.</summary>
    public VBoxContainer Body { get; }

    /// <summary>The X was pressed.</summary>
    public event Action? CloseRequested;
    /// <summary>The window was dropped somewhere else.</summary>
    public event Action<Vector2>? Moved;
    /// <summary>The corner was released at this scale (already in steps of 5%).</summary>
    public event Action<float>? ResizeRequested;
    /// <summary>The window was folded down to its title (true) or opened up again.</summary>
    public event Action<bool>? MinimisedChanged;
    /// <summary>Only the title is showing.</summary>
    public bool Minimised { get; private set; }
    /// <summary>
    /// The corner is held: until it is let go the window is only scaled, from the size it had when the corner was
    /// taken. An owner that changes what the window holds waits until then.
    /// </summary>
    public bool Resizing => _resizing;

    readonly Control _grip, _balance;
    readonly Label _title;
    readonly PanelContainer _paper;
    readonly float _paperWidth;
    readonly Button? _fold;
    readonly Vector2? _startAt;
    readonly float _minScale, _maxScale;
    float _bottomInset;
    bool _dragging, _resizing, _placed;
    Vector2 _resizeFrom;
    float _resizeTo;
    // Only for a window whose title changes while it is folded (SetTitle, KeepRoomForTitles): the right edge of
    // the folded strip, where its buttons are, and the width its paper is held at.
    float? _foldedRight;
    float _titleRoom;

    // foldedRight: for a window that opens folded and whose title changes, the right edge its strip was left at.
    // minScale, maxScale: for a window whose corner reaches other sizes than MinScale to MaxScale.
    public GameWindow(SceneTree tree, string title, float scale, Vector2? position, int layer = 105, bool canMinimise = false, bool minimised = false,
        float bottomInset = 0, float? foldedRight = null, float minScale = MinScale, float maxScale = MaxScale)
    {
        _minScale = minScale;
        _maxScale = maxScale;
        Style = new GameStyle(Math.Clamp(scale, minScale, maxScale));
        _startAt = position;
        _foldedRight = canMinimise && minimised ? foldedRight : null;
        _bottomInset = bottomInset;
        _resizeTo = Style.Scale;
        Layer = new CanvasLayer { Layer = layer, Visible = false };

        Root = new PanelContainer { MouseDefaultCursorShape = Control.CursorShape.Move };
        Root.AddThemeStyleboxOverride("panel", Style.ModalPanel());
        Root.Theme = Style.TooltipTheme();
        Root.GuiInput += OnRootInput;
        Root.Resized += OnResized;
        Root.MinimumSizeChanged += () =>
        {
            // Godot reports a new minimum size a moment after the change (deferred): fitted again from the same
            // right edge, which leaves a strip already fitted where it is.
            if (Minimised && _foldedRight is { } right) FitFolded(right);
        };
        Root.ItemRectChanged += PlaceGrip;
        Layer.AddChild(Root);

        var column = Style.Spaced(new VBoxContainer(), 10);
        Root.AddChild(column);

        // [ (space as wide as the buttons) ......... [ title on paper ] ......... [_] [X] ]
        var bar = Style.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass }, 4);
        int closeSize = Style.Px(32);
        _balance = new Control { CustomMinimumSize = new Vector2(canMinimise ? closeSize * 2 + Style.Px(4) : closeSize, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        bar.AddChild(_balance);
        var centre = new CenterContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        _paper = Style.PaperTag(title, out _title, 18, 220);
        _paperWidth = _paper.CustomMinimumSize.X;
        centre.AddChild(_paper);
        bar.AddChild(centre);
        if (canMinimise)
        {
            // Its sign is drawn, not written: a line for "down to the title", a box for "open again".
            Button fold = _fold = new Button
            {
                ThemeTypeVariation = "TexturedButton4x",
                CustomMinimumSize = new Vector2(closeSize, closeSize),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                FocusMode = Control.FocusModeEnum.None,
            };
            fold.Draw += () =>
            {
                float width = Math.Max(2f, MathF.Round(2 * Style.Scale)), left = fold.Size.X * 0.3f, right = fold.Size.X * 0.7f;
                if (Minimised) fold.DrawRect(new Rect2(left, fold.Size.Y * 0.3f, right - left, fold.Size.Y * 0.36f), GameStyle.TextPaper, false, width);
                else fold.DrawLine(new Vector2(left, fold.Size.Y * 0.64f), new Vector2(right, fold.Size.Y * 0.64f), GameStyle.TextPaper, width);
            };
            fold.Pressed += () => SetMinimised(!Minimised);
            bar.AddChild(fold);
        }
        var close = new Button
        {
            Text = "X", ThemeTypeVariation = "TexturedButtonCancel2x",
            CustomMinimumSize = new Vector2(closeSize, closeSize),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            TooltipText = "Close",
        };
        close.AddThemeFontSizeOverride("font_size", Style.Px(18));
        close.Pressed += () => CloseRequested?.Invoke();
        bar.AddChild(close);
        column.AddChild(bar);

        Body = Style.Spaced(new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill }, 8);
        column.AddChild(Body);

        _grip = BuildGrip();
        Layer.AddChild(_grip);
        if (canMinimise) Fold(minimised);
        tree.Root.AddChild(Layer);
    }

    /// <summary>
    /// Changes the title on the paper. Folded, the strip is fitted to the new title with its right edge, where
    /// the buttons are, kept in place.
    /// </summary>
    public void SetTitle(string title)
    {
        if (_title.Text == title) return;
        float? right = HoldFoldedRight();
        _title.Text = title;
        if (right is { } edge) FitFolded(edge);
    }

    /// <summary>
    /// Holds the folded title's paper as wide as the widest of these titles, so the strip and its buttons stay
    /// where they are while the title changes among them. With no titles the paper fits its title again.
    /// </summary>
    public void KeepRoomForTitles(IEnumerable<string> titles)
    {
        Font font = _title.GetThemeFont("font");
        int size = _title.GetThemeFontSize("font_size");
        // What the label and the paper add around the text, as their own minimum sizes do.
        float margins = _title.GetThemeStylebox("normal").GetMinimumSize().X + _paper.GetThemeStylebox("panel").GetMinimumSize().X;
        float widest = 0;
        foreach (string text in titles)
            widest = Math.Max(widest, MathF.Ceiling(font.GetStringSize(text, fontSize: size).X + margins));
        if (widest == _titleRoom) return;
        float? right = HoldFoldedRight();
        _titleRoom = widest;
        SizePaper();
        if (right is { } edge) FitFolded(edge);
    }

    /// <summary>
    /// Folds the window down to its title, or opens it up again. Its top right corner, where the button is,
    /// stays where it is, so the same spot opens it again.
    /// </summary>
    public void SetMinimised(bool minimised)
    {
        if (_fold == null || minimised == Minimised) return;
        // A folded strip whose title changed opens from the edge it held (HoldFoldedRight).
        float right = _foldedRight ?? Root.Position.X + Root.Size.X;
        _foldedRight = null;
        // Where it goes is decided here from now on, not by where it was first put.
        _placed = true;
        Fold(minimised);
        Root.ResetSize();
        Root.Position = new Vector2(MathF.Round(right - Root.Size.X), Root.Position.Y);
        KeepOnScreen();
        MinimisedChanged?.Invoke(minimised);
        Moved?.Invoke(Root.Position);
    }

    // Folded, the first change of title fixes the strip's right edge; every later fit starts from that same
    // edge, so rounding the position to whole pixels cannot add up.
    float? HoldFoldedRight() => Minimised ? _foldedRight ??= Root.Position.X + Root.Size.X : null;

    void FitFolded(float right)
    {
        Root.ResetSize();
        Root.Position = new Vector2(MathF.Round(right - Root.Size.X), Root.Position.Y);
        KeepOnScreen();
    }

    // Folded, the paper may be held wider than its title (KeepRoomForTitles); open, it is as it was made.
    void SizePaper() => _paper.CustomMinimumSize = new Vector2(Minimised ? Math.Max(_paperWidth, _titleRoom) : _paperWidth, 0);

    void Fold(bool minimised)
    {
        Minimised = minimised;
        SizePaper();
        Body.Visible = !minimised;
        // Folded, the title needs nothing to balance the buttons: the bar is as short as it can be.
        _balance.Visible = !minimised;
        _grip.Visible = !minimised;
        _fold!.TooltipText = minimised ? "Open the window again" : "Fold down to the title";
        _fold.QueueRedraw();
    }

    public bool Visible
    {
        get => GodotObject.IsInstanceValid(Layer) && Layer.Visible;
        set { if (GodotObject.IsInstanceValid(Layer)) Layer.Visible = value; }
    }

    public Vector2 Position => Root.Position;
    public bool IsAlive => GodotObject.IsInstanceValid(Layer);

#if DEVTOOLS
    internal string DevTitleLayout => $"paper = {_paper.GetGlobalRect()}, held at = {_titleRoom}, text = {_title.GetMinimumSize()}, " +
        $"folded right = {_foldedRight?.ToString() ?? "-"}, fold button = {_fold?.GetGlobalRect()}";
#endif

    public void Free()
    {
        if (GodotObject.IsInstanceValid(Layer)) Layer.QueueFree();
    }

    // The window gets its size in steps while its content is laid out. Until the player has moved it, it
    // goes where it was left, or stays centred, after every one of them.
    void OnResized()
    {
        if (_dragging || _resizing || Root.Size.X < 50) return;
        if (!_placed)
        {
            Vector2 screen = Root.GetViewportRect().Size;
            if (_bottomInset > 0) screen.Y -= _bottomInset;
            Root.Position = _startAt ?? ((screen - Root.Size) / 2).Round();
        }
        if (Minimised && _foldedRight is { } right)
            Root.Position = new Vector2(MathF.Round(right - Root.Size.X), Root.Position.Y);
        KeepOnScreen();
    }

    /// <summary>
    /// Puts the window here, as if it had been dragged here: from now on it stays where it is while its content
    /// grows and shrinks, only kept on the screen, instead of going back to the place it was made with. A folded
    /// strip that holds its right edge (foldedRight) holds it where it is now, as after a drag; that edge is read
    /// from the strip's laid-out width, which arrives a moment after the window is made, so move such a strip only
    /// once it is laid out, never straight after the constructor. Moved is not raised.
    /// </summary>
    public void MoveTo(Vector2 position)
    {
        _placed = true;
        Root.Position = position.Round();
        KeepOnScreen();
        if (Minimised && _foldedRight.HasValue) _foldedRight = Root.Position.X + Root.Size.X;
    }

    /// <summary>The game's bar along the bottom of the screen has a new height: the window is kept above it.</summary>
    public void KeepAbove(float bottomInset)
    {
        if (bottomInset == _bottomInset) return;
        _bottomInset = bottomInset;
        KeepOnScreen();
    }

    public void KeepOnScreen()
    {
        Vector2 screen = Root.GetViewportRect().Size, size = Root.Size * Root.Scale;
        if (_bottomInset > 0) screen.Y -= _bottomInset;
        // Entirely on the screen when it fits; a window larger than the screen keeps its title bar reachable.
        Root.Position = new Vector2(
            Math.Clamp(Root.Position.X, Math.Min(0, screen.X - size.X), Math.Max(0, screen.X - size.X)),
            Math.Clamp(Root.Position.Y, 0, Math.Max(0, screen.Y - size.Y)));
        PlaceGrip();
    }

    void OnRootInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            _dragging = button.Pressed;
            if (!_dragging) { _placed = true; Moved?.Invoke(Root.Position); }
        }
        else if (e is InputEventMouseMotion motion && _dragging)
        {
            Root.Position += motion.Relative;
            KeepOnScreen();
            if (Minimised && _foldedRight.HasValue) _foldedRight = Root.Position.X + Root.Size.X;
        }
    }

    Control BuildGrip()
    {
        int size = Style.Px(16);
        var grip = new Control
        {
            CustomMinimumSize = new Vector2(size, size), Size = new Vector2(size, size),
            MouseDefaultCursorShape = Control.CursorShape.Fdiagsize,
            TooltipText = "Drag to resize",
        };
        grip.Draw += () =>
        {
            var color = new Color(GameStyle.TextGrey, 0.7f);
            float width = Math.Max(1f, Style.Scale);
            for (int i = 1; i <= 3; i++)
            {
                float d = size * i / 4f;
                grip.DrawLine(new Vector2(size - d, size), new Vector2(size, size - d), color, width);
            }
        };
        grip.GuiInput += OnGripInput;
        return grip;
    }

    void PlaceGrip()
    {
        if (!GodotObject.IsInstanceValid(_grip)) return;
        float inset = Style.Px(8) * Root.Scale.X;
        _grip.Scale = Root.Scale;
        _grip.Position = Root.Position + Root.Size * Root.Scale - _grip.Size * _grip.Scale - new Vector2(inset, inset * 1.6f);
    }

    void OnGripInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            if (button.Pressed)
            {
                _resizing = true;
                _resizeFrom = button.GlobalPosition;
                _resizeTo = Style.Scale;
            }
            else if (_resizing)
            {
                _resizing = false;
                float scale = MathF.Round(_resizeTo * 20f) / 20f;
                if (Math.Abs(scale - Style.Scale) > 0.001f) ResizeRequested?.Invoke(scale);
                else { Root.Scale = Vector2.One; PlaceGrip(); }
            }
        }
        else if (e is InputEventMouseMotion motion && _resizing)
        {
            // Follow whichever direction was dragged further, keeping the window's proportions.
            Vector2 moved = motion.GlobalPosition - _resizeFrom, size = Root.Size;
            float factor = Math.Max((size.X + moved.X) / size.X, (size.Y + moved.Y) / size.Y);
            _resizeTo = Math.Clamp(Style.Scale * factor, _minScale, _maxScale);
            Root.Scale = Vector2.One * (_resizeTo / Style.Scale);
            PlaceGrip();
        }
    }
}
