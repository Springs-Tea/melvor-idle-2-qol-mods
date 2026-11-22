using System.Collections.Generic;
using Godot;
using MelvorMods.Shared;

namespace CombatSim;

/// <summary>
/// What Fight would do with "Wear this setup first" ticked, in words (<paramref name="Note"/>), and why the box puts
/// nothing on now (<paramref name="Off"/>, "" when it puts something on). <paramref name="Whole"/>: the note is the whole
/// text above Fight (what goes on, or why nothing does, and the rest of what Fight does); otherwise it is a short line
/// put before the words Fight has without the box ("" for none).
/// </summary>
sealed record WearForecast(string Note, string Off, bool Whole = false);

// The tick box "Wear this setup first" above the Fight button (issue #3), the words above the button that go with it
// (every step Fight will take) and the gold line of what the last click really did. Putting the setup on is
// CombatSimMod's (FightSetup.cs).
sealed partial class SimView
{
    const string WearTip = "Fight first puts on the setup simulated here, through the game's own actions. It changes to that\n" +
                           "loadout as the game's loadout buttons do, with what the game keeps in it (its food, prayers, attack\n" +
                           "stance and spells), puts on each item you swapped in from your bank (ammunition: as many as the bank\n" +
                           "holds) and selects the spell you chose. What an item replaces goes back to the bank.\n" +
                           "An item that is not in your bank, that you cannot wear yet, or whose replaced piece your bank has no\n" +
                           "room for is skipped: that slot stays exactly as it is, and so does a slot you emptied here.\n" +
                           "The words above Fight name every step first. Nothing is bought or made, and nothing is put back.\n" +
                           "Off until you tick it; it stays as you leave it.";
    const string WearViewingOff = "An earlier scan is on show: the equipment on the left is not the one it was made with. " +
                                  "Fight starts with what you are wearing now.";

    Button _wearBox = null!;
    Label _wearReport = null!;
    Color _wearWords, _wearLit;
    string _wearOff = "";

    // The game's tick box as the Item Encyclopedia and History use it (GameStyle.TickBox), in the middle like the
    // words and the button under it; under it, the gold line of what the last Fight did.
    Control BuildWearBox(float width)
    {
        var foot = _s.Spaced(new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 6);
        _wearBox = _s.TickBox("Wear this setup first", _state.WearSetup);
        _wearBox.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        _wearWords = _wearBox.GetThemeColor("font_color");
        _wearLit = _wearBox.GetThemeColor("font_hover_color");
        _wearBox.Toggled += on =>
        {
            _state.WearSetup = on;
            _state.FightReport = "";
            _changed();
            FillDetails();
        };
        foot.AddChild(_wearBox);
        // A gold line is a note from what happened, as the notes of a run are.
        _wearReport = Wrapped("", width, GameStyle.TextGold, 11);
        _wearReport.HorizontalAlignment = HorizontalAlignment.Center;
        _wearReport.Visible = false;
        foot.AddChild(_wearReport);
        return foot;
    }

    // From FillDetails, once the Fight button and its words follow the selection.
    void FillWear()
    {
        _wearBox.Visible = _fightTarget != null;
        _wearReport.Visible = false;
        if (_fightTarget == null) return;
        var (note, off, whole) = _state.Viewing != null ? new WearForecast("", WearViewingOff) : _lookup.ForecastWear(_fightTarget);
        _wearOff = off;
        Dim(off != "");
        _wearBox.TooltipText = off != "" ? TipLines(off) + "\n\n" + WearTip : WearTip;
        // Ticked, the words are one statement of what Fight will do: every step, or why nothing goes on and what Fight
        // still does. When the box cannot tell what to put on, the reason goes before the words Fight has without it.
        if (_state.WearSetup && note != "") _fightNote.Text = whole || _fightNote.Text == "" ? note : note + " " + _fightNote.Text;
        _fightNote.Visible = _fightNote.Text != "";

        // What the last Fight did, as long as this fight with this setup is selected.
        bool report = _state.FightReport != "" && _state.Viewing == null && _state.FightReportKey == ReportKey(_state, _fightTarget);
        if (!report) _state.FightReport = "";
        _wearReport.Text = _state.FightReport;
        _wearReport.Visible = report;
    }

    // A box that puts nothing on here is greyed in every state the pointer can leave it in: its words in TextDim (TextGrey
    // under the pointer), as GameStyle.LinkTag greys its own, and its picture tinted with the colour the game's theme
    // gives the picture of a button that cannot be used (icon_disabled_color), as Godot draws a disabled button. It is
    // never disabled itself: it is the setting for every fight, so it can always be ticked and unticked where it is seen.
    void Dim(bool idle)
    {
        foreach (string colour in new[] { "font_color", "font_pressed_color", "font_focus_color" })
            _wearBox.AddThemeColorOverride(colour, idle ? GameStyle.TextDim : _wearWords);
        foreach (string colour in new[] { "font_hover_color", "font_hover_pressed_color" })
            _wearBox.AddThemeColorOverride(colour, idle ? GameStyle.TextGrey : _wearLit);
        Color greyed = _wearBox.GetThemeColor("icon_disabled_color");
        foreach (string colour in new[] { "icon_normal_color", "icon_pressed_color", "icon_focus_color", "icon_hover_color", "icon_hover_pressed_color" })
        {
            if (idle) _wearBox.AddThemeColorOverride(colour, greyed);
            else _wearBox.RemoveThemeColorOverride(colour);
        }
    }

    // A reason in the tooltip broken into lines as long as those of WearTip, so the tooltip stays as narrow.
    static string TipLines(string text, int width = 100)
    {
        var lines = new List<string>();
        string line = "";
        foreach (string word in text.Split(' '))
        {
            if (line != "" && line.Length + 1 + word.Length > width)
            {
                lines.Add(line);
                line = word;
            }
            else line = line == "" ? word : line + " " + word;
        }
        if (line != "") lines.Add(line);
        return string.Join("\n", lines);
    }

    /// <summary>The fight and the setup a report after Fight belongs to.</summary>
    internal static string ReportKey(SimState state, SimTarget target) => target.Key + "#" + state.SetupKey;

#if DEVTOOLS
    public Button DevFightButton => _fightButton;
    public Button DevWearBox => _wearBox;
    public Label DevFightNote => _fightNote;
    public Label DevWearReport => _wearReport;
    /// <summary>Why the box puts nothing on for the selected fight; "" when it puts something on.</summary>
    public string DevWearOff => _wearOff;
    public Control DevDetailsList => (Control)_details.GetParent();

    /// <summary>How the box is drawn right now: its state, the colours of its words and picture, and where they come from.</summary>
    public string DevWearLook()
    {
        BaseButton.DrawMode mode = _wearBox.GetDrawMode();
        var (font, icon) = mode switch
        {
            BaseButton.DrawMode.Pressed => ("font_pressed_color", "icon_pressed_color"),
            BaseButton.DrawMode.Hover => ("font_hover_color", "icon_hover_color"),
            BaseButton.DrawMode.HoverPressed => ("font_hover_pressed_color", "icon_hover_pressed_color"),
            BaseButton.DrawMode.Disabled => ("font_disabled_color", "icon_disabled_color"),
            _ => ("font_color", "icon_normal_color"),
        };
        Theme? game = ThemeDB.GetProjectTheme();
        string From(string name) => _wearBox.HasThemeColorOverride(name) ? "set on the box"
            : game != null && game.HasColor(name, "Button") ? "the game's theme" : "Godot's default theme (the game's theme has none)";
        return $"greyed {_wearOff != ""}, ticked {_wearBox.ButtonPressed}, drawn as {mode}: words #{_wearBox.GetThemeColor(font).ToHtml()} ({font}, {From(font)}), " +
               $"picture tinted #{_wearBox.GetThemeColor(icon).ToHtml()} ({icon}, {From(icon)}); the greyed tint, icon_disabled_color, is " +
               $"#{_wearBox.GetThemeColor("icon_disabled_color").ToHtml()} from {From("icon_disabled_color")}";
    }

    /// <summary>What each of the three columns needs, at this size and at full size (DevLogThreeColumns): the tallest sets the window's height.</summary>
    public void DevThreeColumns(string head) => DevLogThreeColumns(head);
#endif
}
