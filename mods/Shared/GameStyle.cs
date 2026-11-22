using System;
using Godot;

namespace MelvorMods.Shared;

/// <summary>
/// The game's own look, for mod windows: its textures, style boxes, fonts and colours, loaded from the game's
/// package at runtime (nothing is copied into the mod). Every value here was read from the running game
/// (0.11.6-alpha.1). When a resource is gone after a game update, a plain flat style of the same colour is
/// used instead, so a window still looks close and never breaks.
///
/// One instance per window size: every length goes through <see cref="Px"/>, so a window built at scale 1.5
/// has real 21 px text and 30 px icons instead of a stretched, blurry copy of the small one.
/// </summary>
public sealed class GameStyle(float scale = 1f)
{
    public float Scale { get; } = scale;

    /// <summary>A length of the game's design (at 100%) in pixels of this window.</summary>
    public int Px(float designPixels) => Math.Max(1, (int)MathF.Round(designPixels * Scale));

    // Colours the game uses for text.
    public static readonly Color TextWhite = new("ffffff");
    public static readonly Color TextGrey = new("b5b5b5");     // secondary labels
    public static readonly Color TextDim = new("969696");      // units, hints
    public static readonly Color TextPaper = new("e9dfc1");    // warm value text
    public static readonly Color TextGold = new("edbf59");
    public static readonly Color Highlight = new("ffd16d");    // the brighter gold of a frame or a best value

    const string Fonts = "res://Assets/Fonts/Default/Static/";
    public static Font? Bold => Load<Font>(Fonts + "Default_Bold.ttf");
    public static Font? SemiBold => Load<Font>(Fonts + "Default_SemiBold.ttf");

    // "as", not a cast: a resource that became another kind in a game update is treated as gone.
    public static T? Load<T>(string path) where T : Resource =>
        ResourceLoader.Exists(path) ? ResourceLoader.Load(path) as T : null;

    /// <summary>An icon by its file name in the game's Icons folder. Some only exist as "Name_64".</summary>
    public static Texture2D? Icon(string name) =>
        Load<Texture2D>($"res://Assets/Icons/{name}.png") ?? Load<Texture2D>($"res://Assets/Icons/{name}_64.png");

    /// <summary>The heavy outer frame of the combat screen's panels.</summary>
    public StyleBox WindowPanel() =>
        Textured("res://Assets/UI/Textures/Combat/TEX_Combat_Panel.png", 12, 10, 12, 12, Colors.White, new Color("1d1916"), new Color("524943"));

    /// <summary>The darker box inside a panel that holds its content.</summary>
    public StyleBox InnerPanel() =>
        Textured("res://Assets/UI/Textures/Combat/TEX_Combat_22.png", 6, 6, 6, 6, Colors.White, new Color("111110"), new Color("50483d"));

    /// <summary>The dark panel of the game's own tooltips, with the game's own margins.</summary>
    public StyleBox TooltipPanel() =>
        Textured("res://Assets/UI/Textures/Tooltips/TEX_Tooltip_Dark_Large.png", 12, 8, 12, 16, Colors.White, new Color("2f2314"), new Color("524943"));

    /// <summary>
    /// The look of Godot's small tooltips (a control's TooltipText) for everything under the control this is
    /// set on. The game draws its own tooltips and leaves these bare: words with nothing behind them, on top
    /// of whatever is there. This puts them on the dark panel of the game's tooltips.
    /// </summary>
    public Theme TooltipTheme()
    {
        var theme = new Theme();
        theme.SetStylebox("panel", "TooltipPanel", TooltipPanel());
        theme.SetColor("font_color", "TooltipLabel", TextWhite);
        theme.SetFontSize("font_size", "TooltipLabel", Px(13));
        return theme;
    }

    /// <summary>A small dark tag with a thin grey border: the one the item tooltip shows a price in.</summary>
    public PanelContainer NoteTag(string text)
    {
        var tag = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        var box = new StyleBoxFlat { BgColor = new Color("141310"), BorderColor = new Color("5c5c5c") };
        box.SetBorderWidthAll(1);
        box.SetCornerRadiusAll(2);
        box.ContentMarginLeft = box.ContentMarginRight = Px(6);
        box.ContentMarginTop = box.ContentMarginBottom = Px(2);
        tag.AddThemeStyleboxOverride("panel", box);
        tag.AddChild(Text(text, TextPaper, 12, SemiBold));
        return tag;
    }

    /// <summary>
    /// The same small dark tag as a button: a picture and a few words that lead somewhere when clicked (an
    /// ingredient that opens its own page). Its border lights up under the pointer.
    /// </summary>
    public Button LinkTag(Texture2D? icon, string text)
    {
        var tag = new Button
        {
            Text = text, Icon = icon, FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        foreach (string state in new[] { "normal", "hover", "pressed", "disabled", "focus" })
        {
            var box = new StyleBoxFlat { BgColor = new Color(state == "pressed" ? "2a241c" : "141310"), BorderColor = state is "hover" or "pressed" ? Highlight : new Color("5c5c5c") };
            box.SetBorderWidthAll(1);
            box.SetCornerRadiusAll(2);
            box.ContentMarginLeft = box.ContentMarginRight = Px(5);
            box.ContentMarginTop = box.ContentMarginBottom = Px(1);
            tag.AddThemeStyleboxOverride(state, box);
        }
        foreach (string color in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color", "font_disabled_color" })
            tag.AddThemeColorOverride(color, color == "font_disabled_color" ? TextDim : TextPaper);
        if (SemiBold is { } font) tag.AddThemeFontOverride("font", font);
        tag.AddThemeFontSizeOverride("font_size", Px(12));
        tag.AddThemeConstantOverride("icon_max_width", Px(16));
        tag.AddThemeConstantOverride("h_separation", Px(4));
        return tag;
    }

    /// <summary>The small arrow of the game's drop-downs.</summary>
    public static Texture2D? DropArrow
    {
        get
        {
            // The drop-downs' own theme first; it may leave the arrow to the game's main theme.
            foreach (Theme? theme in new[] { Load<Theme>("res://Assets/Styles/Themes/custom_dropdown_1.tres"), ThemeDB.GetProjectTheme(), ThemeDB.GetDefaultTheme() })
                if (theme != null && theme.HasIcon("arrow", "OptionButton")) return theme.GetIcon("arrow", "OptionButton");
            return null;
        }
    }

    /// <summary>An option that is on or off, with the game's own tick box in front of its name.</summary>
    public Button TickBox(string text, bool on)
    {
        // Copies at the size they are shown: the box is thin lines, which a picture shrunk on the fly loses.
        Texture2D? empty = Sized(Icon("EmptyTickbox_64Artboard 1"), 18), ticked = Sized(Icon("GreenTickbox"), 18);
        var box = new Button
        {
            Text = text, ToggleMode = true, ButtonPressed = on, Icon = on ? ticked : empty, Alignment = HorizontalAlignment.Left,
            FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" })
            box.AddThemeStyleboxOverride(state, new StyleBoxEmpty { ContentMarginLeft = Px(2), ContentMarginRight = Px(2), ContentMarginTop = Px(2), ContentMarginBottom = Px(2) });
        foreach (string color in new[] { "font_color", "font_pressed_color", "font_focus_color", "font_hover_color", "font_hover_pressed_color" })
            box.AddThemeColorOverride(color, color.Contains("hover") ? TextWhite : TextPaper);
        box.AddThemeFontSizeOverride("font_size", Px(13));
        box.AddThemeConstantOverride("icon_max_width", Px(18));
        box.AddThemeConstantOverride("h_separation", Px(6));
        box.Toggled += now => box.Icon = now ? ticked : empty;
        return box;
    }

    /// <summary>The thin line under the name in the item tooltip: light on the left, gone on the right.</summary>
    public Panel FadingLine()
    {
        var line = new Panel { CustomMinimumSize = new Vector2(0, Px(2)), MouseFilter = Control.MouseFilterEnum.Ignore };
        var gradient = new Gradient { Offsets = new[] { 0f, 1f }, Colors = new[] { new Color(1, 1, 1, 0.55f), new Color(1, 1, 1, 0) } };
        line.AddThemeStyleboxOverride("panel", new StyleBoxTexture { Texture = new GradientTexture1D { Gradient = gradient, Width = 256 } });
        return line;
    }

    /// <summary>The small dark box the game's item panel shows an item's picture in.</summary>
    public StyleBox ItemFrame(bool lit = false)
    {
        var box = new StyleBoxFlat { BgColor = new Color("191108"), BorderColor = lit ? Highlight : new Color("3e2f1c") };
        box.SetBorderWidthAll(1);
        box.SetCornerRadiusAll(4);
        box.ContentMarginLeft = box.ContentMarginRight = box.ContentMarginTop = box.ContentMarginBottom = Px(4);
        return box;
    }

    /// <summary>
    /// A box to type a search into, like the bank's: the looking glass at its right end while it is empty,
    /// the cross that empties it once something is typed.
    /// </summary>
    public LineEdit SearchBox(string placeholder)
    {
        var box = new LineEdit { PlaceholderText = placeholder, ClearButtonEnabled = true, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        box.AddThemeFontSizeOverride("font_size", Px(14));
        box.RightIcon = Sized(Icon("search"), 16);
        return box;
    }

    /// <summary>
    /// A copy of a picture at a size of this window, for the places that draw a picture as large as it is
    /// (the end of a text box) instead of fitting it. The picture itself when it cannot be copied.
    /// </summary>
    public Texture2D? Sized(Texture2D? picture, float designPixels)
    {
        if (picture == null) return null;
        try
        {
            Image? image = picture.GetImage();
            if (image == null || image.IsEmpty()) return picture;
            if (image.IsCompressed()) image.Decompress();
            image.Resize(Px(designPixels), Px(designPixels), Image.Interpolation.Lanczos);
            return ImageTexture.CreateFromImage(image);
        }
        catch { return picture; }
    }

    /// <summary>The frame of the game's large pop-up windows (the Mastery Guild, the combat areas menu).</summary>
    public StyleBox ModalPanel() =>
        Textured("res://Assets/UI/Textures/Combat/TEX_Combat_21.png", 16, 14, 16, 26, Colors.White, new Color("1d1916"), new Color("524943"));

    /// <summary>A column inside such a window. Its top margin leaves room for the paper tag that names it.</summary>
    public StyleBox ColumnPanel() =>
        Textured("res://Assets/UI/Textures/Combat/TEX_Combat_22.png", 10, 22, 10, 10, Colors.White, new Color("111110"), new Color("50483d"));

    /// <summary>The torn paper strip the game writes its titles on.</summary>
    public StyleBox Paper(float sideMargin = 16) =>
        Textured("res://Assets/UI/Textures/Crafting/TEX_Crafting_0.png", sideMargin, 5, sideMargin, 6, Colors.White, new Color("e2dbcd"), new Color("8a8170"));

    /// <summary>A title on paper: dark bold text, like "Task Categories" over a column.</summary>
    public PanelContainer PaperTag(string text, int size = 14, float minWidth = 150)
    {
        var tag = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(Px(minWidth), 0) };
        tag.AddThemeStyleboxOverride("panel", Paper());
        Label label = Text(text, new Color("111110"), size, Bold);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        tag.AddChild(label);
        return tag;
    }

    /// <summary>
    /// A titled column: the paper tag sits on the panel's top edge. Put the content into
    /// <paramref name="body"/>.
    /// </summary>
    public Control Column(string title, out VBoxContainer body, float minWidth = 0)
    {
        var column = new VBoxContainer { CustomMinimumSize = new Vector2(Px(minWidth), 0), MouseFilter = Control.MouseFilterEnum.Pass };
        column.AddThemeConstantOverride("separation", -Px(14));   // the tag overlaps the panel
        var centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = 1 };
        centre.AddChild(PaperTag(title));
        column.AddChild(centre);
        var panel = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass };
        panel.AddThemeStyleboxOverride("panel", ColumnPanel());
        body = Spaced(new VBoxContainer(), 6);
        panel.AddChild(body);
        column.AddChild(panel);
        return column;
    }

    /// <summary>A heading inside a column: a short grey title between two thin lines.</summary>
    public Control Section(string title)
    {
        var row = Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 8);
        row.AddChild(new HSeparator { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore });
        row.AddChild(Text(title, TextGrey));
        row.AddChild(new HSeparator { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore });
        return row;
    }

    /// <summary>
    /// A button with one of the game's own looks: "TexturedButton8x" (default), "TexturedActionButtonGreen8x"
    /// (the main action), "TexturedButtonCancel8x" (stop), "OutlineToggleButton" (on/off, rows).
    /// </summary>
    public Button GameButton(string text, string variation = "TexturedButton8x", int size = 14)
    {
        var button = new Button { Text = text, ThemeTypeVariation = variation, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        button.AddThemeFontSizeOverride("font_size", Px(size));
        return button;
    }

    /// <summary>One line in a list of stats.</summary>
    public StyleBox StatLine() =>
        Textured("res://Assets/UI/Textures/Combat/TEX_Combat_StatLine_0.png", 6, 4, 8, 4, new Color(1, 1, 1, 0.7f), new Color(0, 0, 0, 0.35f), new Color(0, 0, 0, 0));

    StyleBox Textured(string path, float left, float top, float right, float bottom, Color modulate, Color fallbackBg, Color fallbackBorder)
    {
        StyleBox box;
        if (Load<Texture2D>(path) is { } texture)
            box = new StyleBoxTexture { Texture = texture, ModulateColor = modulate };
        else
        {
            var flat = new StyleBoxFlat { BgColor = fallbackBg, BorderColor = fallbackBorder };
            flat.SetBorderWidthAll(1);
            box = flat;
        }
        box.ContentMarginLeft = Px(left); box.ContentMarginTop = Px(top); box.ContentMarginRight = Px(right); box.ContentMarginBottom = Px(bottom);
        return box;
    }

    public Label Text(string text, Color? color = null, int size = 14, Font? font = null)
    {
        var label = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
        if (color is { } c) label.AddThemeColorOverride("font_color", c);
        label.AddThemeFontSizeOverride("font_size", Px(size));
        if (font != null) label.AddThemeFontOverride("font", font);
        return label;
    }

    public TextureRect Picture(Texture2D? texture, int size) => new()
    {
        Texture = texture,
        CustomMinimumSize = new Vector2(Px(size), Px(size)),
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    /// <summary>
    /// A small dark tag with a thin light border, an icon and a number: the look of the game's own little
    /// tags ("Normal Attack", key hints). <paramref name="count"/> is the label to write the number into.
    /// </summary>
    public PanelContainer CountTag(Texture2D? icon, out Label count)
    {
        // Pass: the tag shows its tooltip, and a click on it still reaches the button it sits on.
        var tag = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Pass, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        var box = new StyleBoxFlat { BgColor = new Color("141310"), BorderColor = new Color(new Color("e2dbcd"), 0.55f) };
        box.SetBorderWidthAll(1);
        box.SetCornerRadiusAll(2);
        box.ContentMarginLeft = Px(6); box.ContentMarginRight = Px(7); box.ContentMarginTop = Px(1); box.ContentMarginBottom = Px(1);
        tag.AddThemeStyleboxOverride("panel", box);
        var row = Spaced(new HBoxContainer { Name = "Row" }, 4);
        row.AddChild(Picture(icon, 14));
        count = Text("0", TextPaper, 14, Bold);
        count.Name = "Count";
        row.AddChild(count);
        tag.AddChild(row);
        return tag;
    }

    /// <summary>The number label of a tag made by <see cref="CountTag"/>, for a tag that was found again in the scene.</summary>
    public static Label? CountOf(Node tag) => tag.GetNodeOrNull<Label>("Row/Count");

    /// <summary>"once", "twice", "1,204 times".</summary>
    public static string Times(double count) => count == 1 ? "once" : count == 2 ? "twice" : $"{count:N0} times";

    /// <summary>
    /// Puts <paramref name="tag"/> right after the text of <paramref name="label"/>. In a row (a box
    /// container) it becomes the label's next neighbour, so the row makes room for it; anywhere else it is
    /// laid over the label, just past the end of its text.
    /// </summary>
    public static void AttachAfterText(Label label, Control tag)
    {
        if (label.GetParent() is BoxContainer { Vertical: false } row)
        {
            row.AddChild(tag);
            row.MoveChild(tag, label.GetIndex() + 1);
        }
        else label.AddChild(tag);
    }

    /// <summary>Call after the label's text or size may have changed. Does nothing for a tag in a row.</summary>
    public static void PlaceAfterText(Label label, Control tag)
    {
        if (tag.GetParent() != label) return;
        // A label's font can come from LabelSettings, which wins over the theme.
        LabelSettings? settings = label.LabelSettings;
        Font font = settings?.Font ?? label.GetThemeFont("font");
        int fontSize = settings?.FontSize ?? label.GetThemeFontSize("font_size");
        float textWidth = font.GetStringSize(label.Text, HorizontalAlignment.Left, -1, fontSize).X;
        float textEnd = label.HorizontalAlignment switch
        {
            HorizontalAlignment.Center => (label.Size.X + textWidth) / 2,
            HorizontalAlignment.Right => label.Size.X,
            _ => textWidth,
        };
        tag.ResetSize();
        tag.Position = new Vector2(MathF.Round(textEnd + 12), MathF.Round((label.Size.Y - tag.Size.Y) / 2 + 2));
    }

    public T Spaced<T>(T box, float separation) where T : BoxContainer
    {
        box.AddThemeConstantOverride("separation", Px(separation));
        return box;
    }

    /// <summary>The thin level bar from the game's footer.</summary>
    public ProgressBar LevelBar()
    {
        var bar = new ProgressBar
        {
            ShowPercentage = false, MinValue = 0, MaxValue = 1, Step = 0,
            CustomMinimumSize = new Vector2(0, Px(4)),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color("545c66") });
        bar.AddThemeStyleboxOverride("fill",
            Load<StyleBox>("res://Assets/Styles/Panel/level_progress_gradient1d.tres") ?? new StyleBoxFlat { BgColor = new Color("2baae1") });
        return bar;
    }

    /// <summary>A number box that looks like the footer's level goal.</summary>
    public SpinBox GoalBox()
    {
        var box = new SpinBox { Step = 1, Rounded = true, CustomMinimumSize = new Vector2(Px(80), 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        if (Load<Theme>("res://Assets/Styles/Themes/footer_custom_spin_box.tres") is { } theme) box.Theme = theme;
        box.GetLineEdit().AddThemeFontSizeOverride("font_size", Px(12));
        ScaleIcons(box, "updown", "up", "down", "up_hover", "down_hover", "up_pressed", "down_pressed", "up_disabled", "down_disabled");
        return box;
    }

    /// <summary>A drop-down that looks like the footer's time period list.</summary>
    public OptionButton Dropdown()
    {
        var button = new OptionButton { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        if (Load<Theme>("res://Assets/Styles/Themes/custom_dropdown_1.tres") is { } theme) button.Theme = theme;
        if (SemiBold is { } font) button.AddThemeFontOverride("font", font);
        button.AddThemeFontSizeOverride("font_size", Px(12));
        button.GetPopup().AddThemeFontSizeOverride("font_size", Px(14));
        ScaleIcons(button, "arrow");
        return button;
    }

    // The arrows of number boxes and drop-downs are fixed-size pictures: give the control resized copies.
    void ScaleIcons(Control control, params string[] names)
    {
        if (Math.Abs(Scale - 1f) < 0.01f) return;
        foreach (string name in names)
        {
            try
            {
                if (!control.HasThemeIcon(name) || control.GetThemeIcon(name) is not { } icon) continue;
                Image? image = icon.GetImage();
                if (image == null || image.IsEmpty()) continue;
                if (image.IsCompressed()) image.Decompress();
                image.Resize(Px(icon.GetWidth()), Px(icon.GetHeight()), Image.Interpolation.Lanczos);
                control.AddThemeIconOverride(name, ImageTexture.CreateFromImage(image));
            }
            catch { }   // keeps the unscaled arrow
        }
    }

#if DEVTOOLS
    /// <summary>Development: the words of every Godot tooltip that is up, what is drawn behind them, and where.</summary>
    public static string DevTooltips(Node root)
    {
        var up = new System.Collections.Generic.List<string>();
        void Walk(Node node)
        {
            if (node is PopupPanel { Visible: true } popup)
            {
                var words = new System.Collections.Generic.List<string>();
                foreach (Node label in popup.FindChildren("*", "Label", true, false)) words.Add(((Label)label).Text);
                StyleBox? box = popup.GetThemeStylebox("panel");
                up.Add($"'{string.Join(" ", words)}' on {box?.GetType().Name ?? "nothing"} {(box as StyleBoxTexture)?.Texture?.ResourcePath}, {popup.Size.X}x{popup.Size.Y} at {popup.Position}");
            }
            foreach (Node child in node.GetChildren(true)) Walk(child);
        }
        Walk(root);
        Theme? game = ThemeDB.GetProjectTheme();
        string bare = game == null ? "no project theme" : game.HasStylebox("panel", "TooltipPanel") ? "the game's theme draws them on a " + game.GetStylebox("panel", "TooltipPanel").GetType().Name : "the game's theme has no panel for them";
        return (up.Count == 0 ? "(no tooltip)" : string.Join(" / ", up)) + $"  [{bare}]";
    }
#endif
}
