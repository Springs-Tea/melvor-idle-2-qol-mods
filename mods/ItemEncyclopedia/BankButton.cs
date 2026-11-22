using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Entities.Bank;
using Godot;
using MelvorMods.Shared;

namespace ItemEncyclopedia;

/// <summary>
/// The bank's own button for the game's item stats window ("View Item Stats", under a selected weapon or piece of
/// armour), made again for Details from the game's own data: the properties the bank's scene gives that button,
/// read once from the scene file without making the page (BankActiveView.ScenePath, then the page's
/// SelectedItemView, its EquipOptions and their ViewStatsButton, each the property the game reads it through).
/// So the button has the bank's word, look and size whether or not the bank has been opened, and keeps them for
/// the whole session.
/// </summary>
static class BankButton
{
    // As the game showed them (0.11.6-alpha.1, the bank's button read at run time): for when the scene cannot be read.
    const string ShownWord = "View Item Stats", ShownLook = "TexturedButton4x";

    static Dictionary<string, Variant>? s_scene;
    static bool s_read;

    /// <summary>What the button says, as the bank's scene writes it (the game translates it when it draws it).</summary>
    public static string Word => s_scene is { } scene && scene.TryGetValue("text", out Variant text) && text.AsString() is { Length: > 0 } word ? word : ShownWord;

    /// <summary>Reads the bank's scene, once for the whole session. A scene that cannot be read is one line in the log.</summary>
    public static void Read(Action<string> log)
    {
        if (s_read) return;
        s_read = true;
#if DEVTOOLS
        var clock = System.Diagnostics.Stopwatch.StartNew();
#endif
        try { s_scene = ReadScene(); }
        catch (Exception e)
        {
            log($"The bank's button for the item stats window could not be read from the game's scene (the game was updated?): Details shows \"{ShownWord}\" in the look it had. {e.Message}");
        }
#if DEVTOOLS
        log($"Dev: the bank's button read from its scene in {clock.Elapsed.TotalMilliseconds:0.0} ms: {DevScene}");
#endif
    }

    /// <summary>
    /// The button for Details, made as the bank's scene makes its own: the same word, theme, size flags and
    /// overrides, with every length at the window's size. Its font size is set once it is in the window.
    /// </summary>
    public static Button Make(GameStyle style)
    {
        var button = new Button { Text = ShownWord, ThemeTypeVariation = ShownLook };
        if (s_scene == null) return button;
        float scale = style.Scale;
        foreach ((string name, Variant value) in s_scene)
        {
            if (name == "custom_minimum_size" && value.VariantType == Variant.Type.Vector2)
                button.CustomMinimumSize = (value.AsVector2() * scale).Round();
            else if (name.StartsWith("theme_override_font_sizes/", StringComparison.Ordinal))
                button.Set(name, style.Px(value.AsInt32()));
            else if (name.StartsWith("theme_override_constants/", StringComparison.Ordinal))
                button.Set(name, (int)MathF.Round(value.AsInt32() * scale));
            else if (name.StartsWith("theme_override_", StringComparison.Ordinal) || Copied.Contains(name))
                button.Set(name, value);
        }
        return button;
    }

    /// <summary>
    /// A font size the bank's scene leaves to the game's theme is the theme's, at the window's size. Asked once the
    /// button is in the window, where it finds the game's theme the way the bank's button does.
    /// </summary>
    public static void FitFont(Button button, GameStyle style)
    {
        if (s_scene?.ContainsKey("theme_override_font_sizes/font_size") == true) return;
        button.AddThemeFontSizeOverride("font_size", style.Px(button.GetThemeFontSize("font_size")));
    }

    // What makes the button look and sit as it does. Its place in the bank (anchors, offsets) and what it does when
    // pressed (signals, script) are left out; so is its focus: TAB belongs to the game's window once it is open.
    static readonly HashSet<string> Copied = new(StringComparer.Ordinal)
    {
        "text", "theme_type_variation", "size_flags_horizontal", "size_flags_vertical", "size_flags_stretch_ratio",
        "alignment", "text_overrun_behavior", "clip_text", "autowrap_mode", "flat", "icon", "icon_alignment",
        "vertical_icon_alignment", "expand_icon", "mouse_default_cursor_shape", "tooltip_text",
    };

    // ---- reading the game's scene (mods/Shared/SceneData.cs) ---------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    static Dictionary<string, Variant> ReadScene()
    {
        string path = BankActiveView.ScenePath;
        PackedScene bank = GameStyle.Load<PackedScene>(path) ?? throw new InvalidOperationException($"the bank's scene '{path}' was not found");
        SceneData.Spot spot = SceneData.Root(bank);
        foreach (string property in new[] { "SelectedItemView", "EquipOptions", "ViewStatsButton" })
            spot = SceneData.Follow(spot, property);
        if (spot.State.GetNodeType(spot.Node).ToString() is { Length: > 0 } type && type != "Button")
            throw new InvalidOperationException($"the bank's ViewStatsButton is a {type}");
        return SceneData.Properties(spot);
    }

#if DEVTOOLS
    /// <summary>Development: what was read from the bank's scene, to compare with the button on the screen.</summary>
    public static string DevScene => s_scene == null ? "not read; the button keeps the word and look the game showed"
        : string.Join(", ", s_scene.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} = {p.Value}"));
#endif
}
