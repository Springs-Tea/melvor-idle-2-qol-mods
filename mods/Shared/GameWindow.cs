using System;
using Godot;

namespace MelvorMods.Shared;

/// <summary>
/// A window that looks like the game's own large pop-ups: the heavy frame, the title on a strip of paper, a
/// red X to close. It can be dragged from any empty spot and resized from its bottom right corner.
///
/// A size is a rebuild, not a stretch: while the corner is dragged the window is only scaled (cheap, a little
/// soft); on release <see cref="ResizeRequested"/> is raised and the owner builds a new window at that scale,
/// so text and icons are sharp at every size.
/// </summary>
public sealed class GameWindow
{
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

    readonly Control _grip;
    readonly Vector2? _startAt;
    bool _dragging, _resizing, _placed;
    Vector2 _resizeFrom;
    float _resizeTo;

    public GameWindow(SceneTree tree, string title, float scale, Vector2? position, int layer = 95)
    {
        Style = new GameStyle(Math.Clamp(scale, MinScale, MaxScale));
        _startAt = position;
        _resizeTo = Style.Scale;
        Layer = new CanvasLayer { Layer = layer, Visible = false };

        Root = new PanelContainer { MouseDefaultCursorShape = Control.CursorShape.Move };
        Root.AddThemeStyleboxOverride("panel", Style.ModalPanel());
        Root.GuiInput += OnRootInput;
        Root.Resized += OnResized;
        Root.ItemRectChanged += PlaceGrip;
        Layer.AddChild(Root);

        var column = Style.Spaced(new VBoxContainer(), 10);
        Root.AddChild(column);

        // [ (space as wide as the X) ......... [ title on paper ] ......... [X] ]
        var bar = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        int closeSize = Style.Px(32);
        bar.AddChild(new Control { CustomMinimumSize = new Vector2(closeSize, 0), MouseFilter = Control.MouseFilterEnum.Ignore });
        var centre = new CenterContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        centre.AddChild(Style.PaperTag(title, 18, 220));
        bar.AddChild(centre);
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
        tree.Root.AddChild(Layer);
    }

    public bool Visible
    {
        get => GodotObject.IsInstanceValid(Layer) && Layer.Visible;
        set { if (GodotObject.IsInstanceValid(Layer)) Layer.Visible = value; }
    }

    public Vector2 Position => Root.Position;
    public bool IsAlive => GodotObject.IsInstanceValid(Layer);

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
            Root.Position = _startAt ?? ((screen - Root.Size) / 2).Round();
        }
        KeepOnScreen();
    }

    public void KeepOnScreen()
    {
        Vector2 screen = Root.GetViewportRect().Size, size = Root.Size * Root.Scale;
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
            _resizeTo = Math.Clamp(Style.Scale * factor, MinScale, MaxScale);
            Root.Scale = Vector2.One * (_resizeTo / Style.Scale);
            PlaceGrip();
        }
    }
}
