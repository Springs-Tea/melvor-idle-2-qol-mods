using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MelvorMods.Shared;
#if DEVTOOLS
using System.IO;
using GamesByMalcs.Melvor2.Components.Equipment;
using GamesByMalcs.Melvor2.Data.Items;
#endif

namespace CombatSim;

/// <summary>
/// The attack stance in the simulator's window (issue #51), the state being SimState.StanceId (SimView.cs).
/// <list type="bullet">
/// <item>Left: in Fights, the Stance box over the Spell box, made as the Spell box is: its caption beside the game's
/// drop-down, the choices in the game's own names and pictures of the weapon's stances (WeaponType.Stances,
/// AttackStance.DisplayName and Icon), first the one the game has selected, "As now: Accurate" as the Spell box says
/// it, or "As the game has it" when that cannot be read (SimState.StanceNow). A stance chosen makes a setup of its own,
/// as a spell does (SimState.KeyWith); a weapon of another kind brings its own stances (CombatSimMod.BuildGear). Best
/// for tries every stance and has no box.</item>
/// <item>Details: under the setup's name, the stance the result was fought in, with the game's picture of it and ", as
/// the game has it" when none was asked for; under "Compared with", the other setup's in the same look when it is
/// another one, or the same one asked for on one side only; in Compare, under each setup's name. The line's tooltip
/// names it first.</item>
/// <item>Fight, and the words of "Wear this setup first", switch to a stance only when one was asked for (<see cref="FightAs"/>).</item>
/// </list>
/// </summary>
sealed partial class SimView
{
    const string AsTheGameHasIt = "As the game has it", AsGame = "as the game has it";
    const string StanceTip = "The attack stance to fight in: it decides which skills get the experience. The first choice is\n" +
                             "the one the game has selected for the weapon. Simulate two of them to compare, or tick Compare\n" +
                             "to run two at once. Best for tries every stance of the weapon.";

    OptionButton? _stance;
    Label? _stanceCaption, _spellCaption;
    Control _stanceHolder = null!;

    // The Stance and the Spell box, which both belong to the weapon in the slots.
    void FillChoices()
    {
        FillStances();
        FillSpells();
        LineUpChoices();
    }

    // Only in Fights, and only when the weapon of the setup has more than one attack stance: which one to fight in.
    void FillStances()
    {
        foreach (Node old in _stanceHolder.GetChildren()) old.QueueFree();
        _stance = null;
        _stanceCaption = null;
        _stanceHolder.Visible = !_state.BestMode && _state.Stances.Count > 1;
        if (!_stanceHolder.Visible) return;

        var row = _s.Spaced(new HBoxContainer(), 8);
        row.AddChild(_stanceCaption = Caption("Stance"));
        OptionButton stance = _stance = _s.Dropdown();
        stance.ClipText = true;
        stance.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        stance.TooltipText = StanceTip;
        stance.AddThemeConstantOverride("icon_max_width", _s.Px(16));
        stance.GetPopup().AddThemeConstantOverride("icon_max_width", _s.Px(20));
        stance.AddItem(_state.StanceNow is { } now ? "As now: " + now.Name : AsTheGameHasIt);
        stance.SetItemMetadata(0, "");
        foreach (var (id, name) in _state.Stances)
        {
            if (_lookup.StanceIcon(id) is { } icon) stance.AddIconItem(icon, name); else stance.AddItem(name);
            stance.SetItemMetadata(stance.ItemCount - 1, id);
        }
        for (int i = 0; i < stance.ItemCount; i++)
            if (stance.GetItemMetadata(i).AsString() == _state.StanceId) stance.Selected = i;
        stance.ItemSelected += index =>
        {
            _state.StanceId = stance.GetItemMetadata((int)index).AsString();
            ShowCurrent();
            Refresh();
        };
        stance.Disabled = _state.Running;
        row.AddChild(stance);
        _stanceHolder.AddChild(row);
    }

    // Each drop-down as wide as its choices need beside its caption, and never wider than its row (FitBeside). The two
    // boxes one over the other: both captions as wide as the wider one, so the two drop-downs start and end in line. The
    // Spell box alone (Best for, a weapon of one stance) is fitted the same way: with Godot's own fit to its longest choice
    // it made the Setup column, and the window, 54 px wider at size 0.6 and 13 px at 1.10 (issue #44).
    void LineUpChoices()
    {
        bool spell = _spell != null && _spellCaption != null;
        if (_stance != null && _stanceCaption != null)
        {
            if (spell)
            {
                float wide = MathF.Ceiling(Widest(GameStyle.SemiBold, _s.Px(12), _stanceCaption.Text, _spellCaption!.Text));
                _stanceCaption.CustomMinimumSize = _spellCaption.CustomMinimumSize = new Vector2(wide, 0);
            }
            FitBeside(_stance, _stanceCaption);
        }
        if (!spell) return;
        OptionButton box = _spell!;
        FitBeside(box, _spellCaption!);
        ShowWhole(box, i => SpellFaces(box.GetItemText(i), box.GetItemIcon(i), i == 0 ? _state.SpellNow : null));
    }

    // The words on the box itself are whole at every size (issue #44). FitBeside keeps the box within its row, so the choice
    // it shows can be wider than the box: "As now: Dracolith Flameburst" asked 101 px of the 93 the Spell box has for words
    // at size 0.6, and 186 of 181 at 1.10. Then the box shows the first of the choice's shorter faces that fits (faces,
    // from the choice's own words down), and its tooltip names the choice in full first. The list keeps every choice's own
    // words and picture: it is as wide as its longest.
    void ShowWhole(OptionButton box, Func<int, IEnumerable<(string Words, Texture2D? Picture)>> faces)
    {
        string tip = box.TooltipText;
        void Fit()
        {
            if (!GodotObject.IsInstanceValid(box) || box.Size.X <= 0 || box.Selected < 0) return;
            string own = box.GetItemText(box.Selected);
            var (words, picture, _) = Face(box, faces(box.Selected));
            if (box.Text != words) box.Text = words;
            if (box.Icon != picture) box.Icon = picture;
            box.TooltipText = words == own ? tip : $"{own}\n{tip}";
        }
        Fit();
        box.Resized += Fit;
        box.ThemeChanged += Fit;
        // A choice made puts its own words and picture on the box (OptionButton.Select).
        box.ItemSelected += _ => Fit();
    }

    // The faces of a Spell box choice, from its own words down, each naming the spell whole: "As now: Air Strike" (now is
    // the spell it stands for), then that spell's picture and name, then its name alone; a spell's picture and name, then
    // its name alone.
    static IEnumerable<(string Words, Texture2D? Picture)> SpellFaces(string words, Texture2D? picture, (string Name, Texture2D? Icon)? now)
    {
        yield return (words, picture);
        if (now is { } spell)
        {
            if (spell.Icon != null) yield return (spell.Name, spell.Icon);
            yield return (spell.Name, null);
        }
        else if (picture != null) yield return (words, null);
    }

    // The first face that fits the box's room for words (its width less its margins and arrow, AroundText), or else the
    // last, which the box cuts (ClipText).
    static (string Words, Texture2D? Picture, bool Whole) Face(OptionButton box, IEnumerable<(string Words, Texture2D? Picture)> faces)
    {
        float room = box.Size.X - AroundText(box);
        (string Words, Texture2D? Picture) last = ("", null);
        foreach (var face in faces)
        {
            last = face;
            if (ChoiceWidth(box, face.Words, face.Picture != null) <= room + 0.01f) return (face.Words, face.Picture, true);
        }
        return (last.Words, last.Picture, false);
    }

    // How wide a choice's words are on the box, in its own font and size, with the picture beside them and the spacing
    // Godot keeps between the two.
    static float ChoiceWidth(OptionButton box, string words, bool picture) =>
        box.GetThemeFont("font").GetStringSize(words, HorizontalAlignment.Left, -1, box.GetThemeFontSize("font_size")).X
        + (picture ? box.GetThemeConstant("icon_max_width") + Math.Max(0, box.GetThemeConstant("h_separation")) : 0);

    // The Setup column gets no wider for a box, at any size (CombatSimMod.FullWidth). Godot's own fit to the longest choice
    // asks about 30 px more than the words (SimViewRanges.FitChoices), and the margins and arrow of the game's drop-down
    // keep their size while the words shrink with the window. So the box asks for its longest choice, with the picture
    // beside it, its margins and its arrow, measured as FitChoices measures them, and never for more than its row has
    // beside the caption, as the loadout's drop-down beside A and B does (SimViewCompare.LoadoutRow): a longer name is
    // cut, where the column would otherwise grow (the Spell box shows shorter words instead, ShowWhole).
    void FitBeside(OptionButton box, Label caption)
    {
        box.FitToLongestItem = false;
        void Fit()
        {
            if (!GodotObject.IsInstanceValid(box) || !GodotObject.IsInstanceValid(caption) || caption.GetParent() is not BoxContainer row) return;
            Font font = box.GetThemeFont("font");
            int size = box.GetThemeFontSize("font_size");
            float picture = box.GetThemeConstant("icon_max_width") + Math.Max(0, box.GetThemeConstant("h_separation"));
            float longest = Enumerable.Range(0, box.ItemCount).Where(i => !box.IsItemSeparator(i))
                .Select(i => font.GetStringSize(box.GetItemText(i), HorizontalAlignment.Left, -1, size).X + (box.GetItemIcon(i) != null ? picture : 0))
                .DefaultIfEmpty(0).Max();
            float room = _s.Px(SetupWidth - 20) - caption.GetCombinedMinimumSize().X - row.GetThemeConstant("separation");
            box.CustomMinimumSize = new Vector2(Math.Max(0, Math.Min(MathF.Ceiling(longest + AroundText(box)), MathF.Floor(room))), 0);
        }
        Fit();
        // In the window its theme is the one it is drawn with.
        box.ThemeChanged += Fit;
    }

    // ---- what a result says of its stance -------------------------------------------------------------------

    // Under a setup's name in Details: "[picture] Defensive stance", the stance the result was fought in, with the game's
    // own picture of it, in the look of the setup's line over it; "[picture] Accurate stance, as the game has it" when none
    // was asked for, so that two setups told apart by that alone read apart (asGame: the caller says whether to add it).
    // Its words are whole at every size: "as the game has it" goes under the stance's name when the two do not fit on
    // one line, as "Accurate stance, as the game has it" does not beside a head's picture at the window's default size,
    // and the picture stays beside the name. Its tooltip says why it was that one, and for the result Fight is for
    // (fight), what Fight does with it. None for a result of a scan made before the stance was kept, which names no stance.
    Control? StanceLine(SimResult r, bool fight = true, bool? asGame = null)
    {
        string name = r.StanceShown;
        if (name == "") return null;
        var line = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass, TooltipText = StanceWhy(r, fight) }, 4);
        if (r.StanceIdShown is { } id && _lookup.StanceIcon(id) is { } icon)
        {
            TextureRect picture = _s.Picture(icon, 16);
            picture.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
            line.AddChild(picture);
        }
        Label words = (asGame ?? !r.StanceAsAsked)
            ? OneOrTwo(_s.Text("", GameStyle.TextDim, 12), $"{name} stance, {AsGame}", $"{name} stance,\n{AsGame}")
            : Whole(_s.Text(name + " stance", GameStyle.TextDim, 12));
        words.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        words.MouseFilter = Control.MouseFilterEnum.Ignore;
        line.AddChild(words);
#if DEVTOOLS
        if (fight) _devStanceLine = line;
        _devStanceLines.RemoveAll(l => !GodotObject.IsInstanceValid(l));
        _devStanceLines.Add(line);
#endif
        return line;
    }

    // Why a result was fought in its stance, and for the result Fight is for, what Fight does with the stance (FightAs).
    static string StanceWhy(SimResult r, bool fight)
    {
        string why = r.StanceMissing != null ? TipLines($"{r.StanceMissing} was asked for, but this weapon does not have it: it fought in the stance the game had selected for the weapon.")
            : r.Target.StanceId != null ? "Best for tries every stance of the weapon: this line is the one in this stance."
            : r.StanceChosen ? "Chosen in the Stance box on the left."
            : "As the game has it: the stance the game had selected for the weapon.";
        if (!fight) return why;
        return why + (FightAs(r).StanceName is { Length: > 0 } stance ? $"\nFight switches to the {stance} stance, if your weapon has it." : "\nFight leaves your stance as it is.");
    }

    // The two results fought in the same stance, asked for or as the game has it alike: their stance does not tell them apart.
    static bool SameStance(SimResult a, SimResult b) => a.StanceIdShown == b.StanceIdShown && a.StanceAsAsked == b.StanceAsAsked;

    // Under the name of another setup in "Compared with": its stance, in the look of this one's under the name above, when
    // the stance tells the two apart: another one, or the same one asked for on one side and as the game has it on the
    // other (two setups of their own, each with its own luck outside Compare). Null when it does not, or when its scan did
    // not keep it.
    Control? StanceAgainst(SimResult mine, SimResult other) =>
        other.StanceShown == "" || SameStance(mine, other) ? null : StanceLine(other, fight: false);

    // A result's stance in a line of words: "Accurate", or "Accurate, as the game has it" when none was asked for.
    static string StanceWords(SimResult r) => r.StanceAsAsked ? r.StanceShown : $"{r.StanceShown}, {AsGame}";

    // The first line of a line's tooltip: "Stance: Accurate", with ", as the game has it" when none was asked for, and the
    // stance asked for that the weapon did not have.
    static string? StanceTipLine(SimResult r) =>
        r.StanceShown == "" ? null : $"Stance: {StanceWords(r)}{(r.StanceMissing != null ? $" (no {r.StanceMissing} with this weapon)" : "")}";

    // The fight as Fight starts it: the line's own, with the stance its result was put in when that was chosen in the
    // Stance box, as the target of a Best for line names its own. Fight switches to the stance of such a target, and the
    // words above it and those of "Wear this setup first" say so (FightSetup.Say); a result fought as the game has it
    // leaves the stance as it is. The line keeps its own target: the selection and Find upgrades go by that one.
    static SimTarget FightAs(SimResult r) =>
        r.Target.StanceId == null && r.StanceChosen && r.StanceId != null ? r.Target with { StanceId = r.StanceId, StanceName = r.Stance ?? "" } : r.Target;

#if DEVTOOLS
    // Details' stance line under the name of the result Fight is for, and every stance line made (the other setups' under
    // "Compared with", those of Find upgrades), for the development requests.
    Control? _devStanceLine;
    readonly List<Control> _devStanceLines = new();

    /// <summary>Development: the Fights or the Best for button, for a click as the player's.</summary>
    public Button DevTabButton(bool best) => best ? _modeBest : _modeFights;

    /// <summary>
    /// Development: a choice in the Stance box as the player's click on its item makes it (the drop-down's own signal);
    /// "game" is the first choice (As the game has it), "now" the stance that choice says it stands for ("As now: X"),
    /// "#2" the second stance under it, anything else a part of a stance's name. False, with one line, when there is no
    /// box on show or no such stance.
    /// </summary>
    public bool DevChooseStance(string name)
    {
        if (_stance is not { } box || !GodotObject.IsInstanceValid(box) || !box.IsVisibleInTree())
        {
            DevLog?.Invoke($"Dev: stance: no Stance box on show ({(_state.BestMode ? "Best for has none" : $"the weapon has {_state.Stances.Count} stances")}).");
            return false;
        }
        bool now = name.Equals("now", StringComparison.OrdinalIgnoreCase);
        if (now && _state.StanceNow == null)
        {
            DevLog?.Invoke($"Dev: stance: the first choice is '{box.GetItemText(0)}': the game's stance for this setup cannot be read, so there is no 'now' to choose.");
            return false;
        }
        int? at = name.StartsWith('#') && int.TryParse(name[1..], out int n) ? n : null;
        for (int i = 0; i < box.ItemCount; i++)
        {
            string id = box.GetItemMetadata(i).AsString();
            bool wanted = name.Equals("game", StringComparison.OrdinalIgnoreCase) ? id == ""
                : now ? id != "" && id == _state.StanceNow?.Id
                : at != null ? id != "" && i == at
                : id != "" && box.GetItemText(i).Contains(name, StringComparison.OrdinalIgnoreCase);
            if (wanted)
            {
                box.Select(i);
                box.EmitSignal(OptionButton.SignalName.ItemSelected, (long)i);
                return true;
            }
        }
        DevLog?.Invoke($"Dev: stance: the Stance box has no '{name}'; its choices are {string.Join(", ", Enumerable.Range(0, box.ItemCount).Select(box.GetItemText))}.");
        return false;
    }

    /// <summary>
    /// Development (stance=view#2): shows the earlier scan at that place in History (1, the newest), as a click on its row
    /// does, to see what it kept of the stance; a scan made before the stance was kept names none. False, with one line,
    /// when History has no such scan.
    /// </summary>
    public bool DevShowScan(int place)
    {
        if (place < 1 || place > _state.History.Count)
        {
            DevLog?.Invoke($"Dev: stance: History has {_state.History.Count} scans, no number {place}.");
            return false;
        }
        Scan scan = _state.History[place - 1];
        Show(scan);
        int kept = scan.Results.Count(r => r.Stance != null);
        DevLog?.Invoke($"Dev: stance: showing scan {place} of History, {scan.Id} '{scan.Title}' with {scan.Setups}: {kept} of {scan.Results.Count} results keep the stance they fought in" +
                       $"{(kept == 0 ? " (made before it was kept)" : "")}.");
        return true;
    }

    /// <summary>Development: the Stance box on show, or null.</summary>
    public OptionButton? DevStanceBox => _stance is { } box && GodotObject.IsInstanceValid(box) && box.IsVisibleInTree() ? box : null;

    /// <summary>Development: Details' stance line on show, or null.</summary>
    public Control? DevStanceDetails => _devStanceLine is { } line && GodotObject.IsInstanceValid(line) && line.IsVisibleInTree() ? line : null;

    /// <summary>
    /// Development (stance=log): what the window shows of the stance. The Setup column: the box, its choices and their
    /// pictures, what it shows, its width against its row and the column, the two drop-downs in line or not, and what each
    /// column needs (DevLogThreeColumns). The list and Details: the setup's name, the selected line's tooltip, Details'
    /// lines under the name and under "Compared with", and the words above Fight with the fight Fight would start.
    /// </summary>
    public void DevLogStance()
    {
        if (!_window.IsAlive) return;
        const string head = "Dev: stance";
        string state = _state.StanceId == "" ? "as the game has it" : _state.StanceId;
        DevLog?.Invoke($"{head}: {(_state.BestMode ? "Best for" : "Fights")}, the weapon's stances {string.Join(", ", _state.Stances.Select(s => $"{s.Name} ({s.Id})")).Or("none")}; " +
                       $"the first choice stands for {(_state.StanceNow is { } now ? $"{now.Name} ({now.Id}), the game's now" : "what the game has, which it cannot tell here")}; " +
                       $"chosen {state}, for a run {_state.ChosenStance?.Id ?? "none"}; setup '{_state.SetupName}' (key {_state.SetupKey}); above the list '{_showing.Text.Replace("\n", " | ")}'");
        if (DevStanceBox is not { } box)
            DevLog?.Invoke($"{head}: no Stance box on show ({(_state.BestMode ? "Best for has none" : $"the weapon has {_state.Stances.Count} stances")})");
        else
        {
            var choices = Enumerable.Range(0, box.ItemCount).Select(i => $"'{box.GetItemText(i)}' [{(box.GetItemIcon(i) is { } icon ? icon.ResourcePath is { Length: > 0 } path ? path : $"a picture {icon.GetWidth()}x{icon.GetHeight()}" : "no picture")}]");
            DevChunks(head + " choices", choices.Prepend($"shows '{box.Text}' (choice {box.Selected}), {(box.Disabled ? "greyed" : "can be used")}, {box.ItemCount} choices"));
            Font font = box.GetThemeFont("font");
            float words = font.GetStringSize(box.Text, HorizontalAlignment.Left, -1, box.GetThemeFontSize("font_size")).X;
            float picture = box.Icon != null ? box.GetThemeConstant("icon_max_width") + Math.Max(0, box.GetThemeConstant("h_separation")) : 0;
            float room = box.Size.X - AroundText(box) - picture, column = _s.Px(SetupWidth - 20);
            Control row = (Control)box.GetParent();
            DevLog?.Invoke($"{head}: Setup: the caption '{_stanceCaption?.Text}' {_stanceCaption?.Size.X:0} px; the box {box.Size.X:0}x{box.Size.Y:0}, asks {box.GetCombinedMinimumSize().X:0} " +
                           $"(Godot's own fit would ask {DevGodotFit(box):0}); '{box.Text}' {words:0.#} wide in {room:0.#}: {(words > room + 0.01f ? "CUT" : "whole")}; " +
                           $"its row {row.Size.X:0} asks {row.GetCombinedMinimumSize().X:0} of the column's {column:0}: {(row.GetCombinedMinimumSize().X > column + 0.5f ? "WIDER THAN THE COLUMN" : "within the column")}");
            if (_spell is { } spell && GodotObject.IsInstanceValid(spell) && spell.IsVisibleInTree())
            {
                Rect2 a = box.GetGlobalRect(), b = spell.GetGlobalRect();
                bool inLine = Math.Abs(a.Position.X - b.Position.X) < 0.5f && Math.Abs(a.End.X - b.End.X) < 0.5f;
                DevLog?.Invoke($"{head}: Setup: the Spell box under it shows '{spell.Text}', {spell.Size.X:0}x{spell.Size.Y:0} from x {b.Position.X:0} to {b.End.X:0}, asks {spell.GetCombinedMinimumSize().X:0}; " +
                               $"the Stance box from x {a.Position.X:0} to {a.End.X:0}; captions {_stanceCaption?.Size.X:0} and {_spellCaption?.Size.X:0} px: {(inLine ? "the two drop-downs are in line" : "THE TWO DROP-DOWNS ARE NOT IN LINE")}");
            }
        }

        // The selected line, as one setup's list and Details show it.
        if (_rows.GetChildren().OfType<Button>().FirstOrDefault(b => b.ButtonPressed && !b.IsQueuedForDeletion()) is { } line)
            DevLog?.Invoke($"{head}: the selected line {_state.Selected ?? "-"}: its tooltip '{line.TooltipText.Replace("\n", " | ")}'");
        var said = new List<string>();
        foreach (Node node in _details.GetChildren().Where(n => !n.IsQueuedForDeletion()).Take(3))
        {
            var labels = node.FindChildren("*", "Label", true, false).OfType<Label>().ToList();
            if (node is Label alone) labels.Insert(0, alone);
            var words = labels.Where(l => l.Text != "").Select(l => l.Text.Replace("\n", " / ")).ToList();
            if (words.Count > 0) said.Add(string.Join(" ~ ", words));
        }
        DevChunks(head + " details", said);
        // Every stance line Details shows: under the name of the result Fight is for, and under another setup's name.
        // Its words, whole or cut by the room the line has (DevFit), its picture and its tooltip.
        static string Words(Control line)
        {
            Label? label = line.FindChildren("*", "Label", true, false).OfType<Label>().FirstOrDefault();
            return $"{(label == null ? "no words" : DevFit(label).Said)}, with " +
                   $"{(line.GetChildren().OfType<TextureRect>().FirstOrDefault() is { Texture: { } t } picture ? $"a picture {t.GetWidth()}x{t.GetHeight()} drawn {picture.Size.X:0}x{picture.Size.Y:0} at y {picture.Position.Y:0}" : "no picture")}, " +
                   $"the line {line.Size.Y:0} high; its tooltip '{line.TooltipText.Replace("\n", " | ")}'";
        }
        // The name a stance line is under, the label before it in its column, and that label's height, for a line as high.
        static string Under(Control line) =>
            line.GetParent() is Node parent && line.GetIndex() > 0 && parent.GetChild(line.GetIndex() - 1) is Label name ? $"{name.Text.Replace("\n", " / ")}' ({name.Size.Y:0} high)" : "-'";
        var others = _devStanceLines.Where(l => GodotObject.IsInstanceValid(l) && l != _devStanceLine && l.IsVisibleInTree() && !l.IsQueuedForDeletion())
            .Select(l => $"under '{Under(l)}: {Words(l)}").ToList();
        DevLog?.Invoke($"{head}: Details: the stance line {(DevStanceDetails is { } stanceLine ? $"under '{Under(stanceLine)}: {Words(stanceLine)}" : "not on show")}; " +
                       $"other stance lines: {(others.Count == 0 ? "none" : string.Join(" || ", others))}");
        DevLogDetailsLines(head);
        DevLog?.Invoke($"{head}: Fight '{_fightButton.Text}' {(_fightButton.Visible ? "shown" : "hidden")} starts {_fightAs?.Key ?? "-"} (the line's {_fightTarget?.Key ?? "-"}), " +
                       $"stance {(_fightAs?.StanceName is { Length: > 0 } s ? s : "left as it is")}; above it '{_fightNote.Text}'; Wear this setup first {(_wearBox.ButtonPressed ? "ticked" : "not ticked")}");
        // Compare's words, which offer the stance only in Fights (Best for has no Stance box).
        if (_state.Compare)
            DevLog?.Invoke($"{head}: Compare's words in {(_state.BestMode ? "Best for" : "Fights")}: the box '{_compareBox.TooltipText.Replace("\n", " | ")}'; " +
                           $"A '{_sideA?.TooltipText.Replace("\n", " | ")}'; B '{_sideB?.TooltipText.Replace("\n", " | ")}'; above Simulate '{_estimate.Text}'; " +
                           $"the empty list {(_emptyBox.Visible ? $"'{_empty.Text.Replace("\n", " | ")}'" : "not on show")}; " +
                           $"{(_state.SameSetups ? "A AND B ARE THE SAME SETUP in this tab" : "A and B differ in this tab")}");
        DevLogThreeColumns(head);
    }

    // A label's words against the room it has: its widest line, in how many lines, and whole, or CUT where a line is wider
    // than the room (for a line that wraps, where one word alone is, which the wrap would break) or a line does not show in
    // the label's height.
    static (bool Whole, string Said) DevFit(Label label)
    {
        Font font = label.GetThemeFont("font");
        int size = label.GetThemeFontSize("font_size");
        float Wide(string s) => font.GetStringSize(s, HorizontalAlignment.Left, -1, size).X;
        float room = label.Size.X - label.GetThemeStylebox("normal").GetMinimumSize().X;
        bool wraps = label.AutowrapMode != TextServer.AutowrapMode.Off;
        string[] given = label.Text.Split('\n');
        float widest = given.Select(Wide).DefaultIfEmpty(0).Max();
        float word = label.Text.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(Wide).DefaultIfEmpty(0).Max();
        int lines = label.GetLineCount(), shown = label.GetVisibleLineCount();
        bool whole = shown >= lines && (wraps ? word : widest) <= room + 0.5f;
        return (whole, $"'{label.Text.Replace("\n", " / ")}' {widest:0.#} wide in {room:0.#}, {lines} {(lines == 1 ? "line" : "lines")}" +
                       $"{(lines > given.Length ? ", wrapped" : "")}{(wraps ? "" : ", never wraps")}, {shown} shown: {(whole ? "whole" : "CUT")}");
    }

    // Details' lines, for the request that measures them (stance=log): every line of its head, every line written straight
    // into the column (a setup's name under "Compared with", a note) and every stance line, each whole or CUT (DevFit) and
    // within the column or PAST ITS EDGE, and the head's lines one under the other, none over the one before it. The lines
    // inside its panels (a number's name and note) are counted, and named only when one is cut.
    void DevLogDetailsLines(string head)
    {
        var shown = _details.GetChildren().OfType<Control>().Where(c => !c.IsQueuedForDeletion() && c.Visible).ToList();
        if (shown.Count == 0) { DevLog?.Invoke($"{head}: Details' lines: none"); return; }
        float right = _details.GetGlobalRect().End.X;
        Control? top = shown[0] as HBoxContainer;   // the head: the picture and the lines beside it
        var listed = new List<string>();
        var faults = new List<string>();
        int inPanels = 0;
        foreach (Control c in shown)
        {
            bool named = c == top || c is Label || _devStanceLines.Contains(c);
            var labels = c is Label alone ? new List<Label> { alone } : c.FindChildren("*", "Label", true, false).OfType<Label>().ToList();
            foreach (Label label in labels.Where(l => l.IsVisibleInTree() && l.Text != ""))
            {
                var (whole, said) = DevFit(label);
                float end = label.GetGlobalRect().End.X;
                if (end > right + 0.5f) said += $", PAST THE COLUMN'S EDGE ({end:0} > {right:0})";
                if (!whole || end > right + 0.5f) faults.Add(said);
                if (named) listed.Add((c == top ? "head " : "") + said);
                else inPanels++;
            }
        }
        string order = "";
        if (top?.GetChildren().OfType<VBoxContainer>().FirstOrDefault() is { } title)
        {
            var lines = title.GetChildren().OfType<Control>().Where(l => l.Visible).ToList();
            int over = lines.Skip(1).Where((l, i) => l.Position.Y < lines[i].Position.Y + lines[i].Size.Y - 0.5f).Count();
            string picture = top.GetChildren().OfType<TextureRect>().FirstOrDefault() is { } p ? $"its picture {p.Size.X:0}x{p.Size.Y:0} at y {p.Position.Y:0}" : "no picture";
            order = $"; the head's {lines.Count} lines {title.Size.X:0}x{title.Size.Y:0} beside {picture}, " + (over == 0 ? "none over another" : $"{over} LINES OVER THE ONE BEFORE");
        }
        DevChunks(head + " Details' lines", listed);
        DevLog?.Invoke($"{head}: Details' lines: {listed.Count} above and {inPanels} in its panels, the column {_details.Size.X:0} wide: " +
                       (faults.Count == 0 ? "every line whole and within the column" : $"{faults.Count} CUT OR PAST THE EDGE: {string.Join(" || ", faults)}") + order);
    }

    // What Godot's own fit to the longest choice would ask for this box, for the log: its width with FitToLongestItem on.
    static float DevGodotFit(OptionButton box)
    {
        Vector2 kept = box.CustomMinimumSize;
        box.FitToLongestItem = true;
        box.CustomMinimumSize = Vector2.Zero;
        float asks = box.GetCombinedMinimumSize().X;
        box.FitToLongestItem = false;
        box.CustomMinimumSize = kept;
        return asks;
    }
#endif
}

#if DEVTOOLS
public partial class CombatSimMod
{
    // Development, the stance= lines of sim.request (DevWindowCommand); the choice is put back with the setup at the end
    // of the request (DevSession.StanceId):
    //   stance=<part of a stance's name>, stance=#2, stance=game, stance=now   a choice in the Stance box as the player's
    //                 click makes it (#2: the second stance under the first choice; game: the first choice; now: the stance
    //                 the first choice says it stands for, "As now: X"); then stance=log
    //   stance=log    a moment later, what the window shows of the stance (SimView.DevLogStance)
    //   stance=view, stance=view#3   the newest earlier scan of History, or the third, as a click on its row shows it; then
    //                 stance=log. Put back with the rest: the list goes back to what it showed before the request
    //   stance=shot   the pictures (DevStanceShot)
    //   stance=best, stance=fights   a click on the Best for or the Fights button; put back with the settings
    //   stance=weapon, stance=weapon+spell   a pick in the Weapon slot, as pick= makes it, of a weapon of another kind
    //                 with more than one stance (DevStanceWeapon); then stance=log
    void DevStance(string what)
    {
        if (_window is not { IsAlive: true, Visible: true }) Open();
        _window?.SetMinimised(false);
        if (_view == null) { _ctx.Log("Dev: stance: the simulator could not be opened."); return; }
        if (what.Equals("shot", StringComparison.OrdinalIgnoreCase)) { DevStanceShot(); return; }
        if (what is "best" or "fights")
        {
            string before = _state.BestMode ? "Best for" : "Fights";
            DevClick(_view.DevTabButton(what == "best"), what == "best" ? "Best for" : "Fights");
            // The click reaches the button on a later frame, and the window is built again.
            _ctx.Tree.CreateTimer(0.3).Timeout += () => _ctx.Log($"Dev: stance: a click on {(what == "best" ? "Best for" : "Fights")}: {before} became {(_state.BestMode ? "Best for" : "Fights")}.");
        }
        else if (what.StartsWith("weapon", StringComparison.OrdinalIgnoreCase))
        {
            if (!DevStanceWeapon(what.EndsWith("+spell", StringComparison.OrdinalIgnoreCase))) { _ctx.Log("Dev: stance: the rest of the request is dropped."); _devCommands.Clear(); return; }
        }
        else if (what.StartsWith("view", StringComparison.OrdinalIgnoreCase))
        {
            int place = what.Length > 5 && what[4] == '#' && int.TryParse(what[5..], out int n) ? n : 1;
            if (!_view.DevShowScan(place)) { _ctx.Log("Dev: stance: the rest of the request is dropped."); _devCommands.Clear(); return; }
        }
        else if (!what.Equals("log", StringComparison.OrdinalIgnoreCase))
        {
            string before = _state.StanceId == "" ? "as the game has it" : _state.StanceId;
            if (!_view.DevChooseStance(what))
            {
                _ctx.Log("Dev: stance: nothing was chosen; the rest of the request is dropped.");
                _devCommands.Clear();
                return;
            }
            _ctx.Log($"Dev: stance: chose '{what}' in the Stance box: {before} became {(_state.StanceId == "" ? "as the game has it" : _state.StanceId)}; setup '{_state.SetupName}'.");
        }
        // The choice reaches the window on a later frame.
        _ctx.Tree.CreateTimer(0.3).Timeout += () =>
        {
            _ctx.Log(DevGameStance());
            _view?.DevLogStance();
        };
    }

    // The character's own stance, for the first choice of the Stance box ("As now: X"): the kind of weapon it holds, the
    // stance the game has selected for that kind, and the one its combat uses (CharacterStats.AttackStance, as the enemy
    // cards read it). Read only.
    string DevGameStance()
    {
        try
        {
            WeaponType? kind = WornWeaponKind();
            return $"Dev: stance: the character holds a weapon of the kind {kind?.Id ?? "-"}; the game has selected {(kind == null ? "-" : SelectedStance(_combat, kind)?.Id ?? "-")} for it, " +
                   $"its combat's stance is {_combat?.Player.Stats.AttackStance.Value?.Id ?? "-"}";
        }
        catch (Exception e) { return "Dev: stance: the character's stance could not be read: " + e.Message; }
    }

    // stance=weapon: a pick in the Weapon slot, as pick= makes it (DevPick, the picker's own row), of a weapon of another
    // kind than the one the slots show, with more than one stance, that the character can wear: owned ones first, then by
    // name, as the picker lists them. With +spell, first one whose Spell box shows (a spell of its elements that the
    // character can cast, DevCastable), for a picture of the Stance and the Spell box together and for Best for's tallest
    // state (issue #44): an element alone is not enough, as no spell of it may be unlocked. False, with one line, when
    // there is none.
    bool DevStanceWeapon(bool spells)
    {
        if (_game == null || _state.Gear.FirstOrDefault(g => g.SlotName.Equals("Weapon", StringComparison.OrdinalIgnoreCase)) is not { } slot)
        {
            _ctx.Log("Dev: stance: weapon: no Weapon slot on the left.");
            return false;
        }
        WeaponCapability? Of(string? itemId) =>
            itemId != null && _game.Data.GetDataRegistryOutput<Item>().TryGetObjectById(itemId, out Item? item) && item.Capabilities.TryGet(out WeaponCapability? weapon) ? weapon : null;
        string? kindNow = (Of(slot.ItemId)?.Type ?? _combat?.Equipment.DefaultWeapon?.Weapon?.Type)?.Id;
        var found = ((ISimLookup)this).ChoicesFor(slot)
            .Where(c => c.CanWear)
            .Select(c => (Choice: c, Weapon: Of(c.ItemId)))
            .Where(x => x.Weapon?.Type is { } type && type.Id != kindNow && type.Stances.Count > 1)
            .Select(x => (x.Choice, x.Weapon, Castable: DevCastable(x.Weapon!)))
            .OrderByDescending(x => spells && x.Castable > 0)
            .ThenByDescending(x => spells && x.Weapon!.Elements.Count > 0)
            .ThenByDescending(x => x.Choice.Owned > 0)
            .ThenBy(x => x.Choice.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (found.Count == 0)
        {
            _ctx.Log($"Dev: stance: weapon: no weapon the character can wear is of another kind than {kindNow ?? "-"} and has more than one stance.");
            return false;
        }
        var (choice, wielded, castable) = found[0];
        if (spells)
        {
            var showing = found.Where(x => x.Castable > 0).Select(x => $"{x.Choice.Name} ({x.Castable})").ToList();
            _ctx.Log($"Dev: stance: weapon: {showing.Count} weapons of several stances whose Spell box would show (spells the character can cast): " +
                     $"{(showing.Count == 0 ? "NONE" : string.Join(", ", showing.Take(12)) + (showing.Count > 12 ? ", ..." : ""))}; " +
                     $"with an element but no spell to cast: {found.Count(x => x.Castable == 0 && x.Weapon!.Elements.Count > 0)}.");
        }
        _ctx.Log($"Dev: stance: weapon: {found.Count} weapons of another kind than {kindNow ?? "-"} with more than one stance; picking {choice.Name} ({choice.ItemId}), " +
                 $"of the kind {wielded!.Type!.Id} with the stances {string.Join(", ", wielded.Type.Stances.Select(s => s.DisplayName.Text))}, " +
                 $"{(wielded.Elements.Count > 0 ? $"casts spells ({castable} the character can cast)" : "casts no spells")}, {(choice.Owned > 0 ? $"{choice.Owned} owned" : "not owned")}" +
                 $"{(spells && castable == 0 ? "; NONE OF THEM HAS A SPELL THE CHARACTER CAN CAST: the Spell box will not show" : "")}.");
        DevPick($"Weapon={choice.ItemId}");
        // The slots are made again from the pick: the Weapon slot shows it when it happened.
        if (_state.Gear.FirstOrDefault(g => g.SlotId == slot.SlotId)?.ItemId == choice.ItemId) return true;
        _ctx.Log($"Dev: stance: weapon: the Weapon slot does not show {choice.Name} after the pick.");
        return false;
    }

    // How many spells the Spell box would list for this weapon: those of its elements, of the books that are unlocked, that
    // the character can cast, by the rule CombatSimMod.BuildGear lists them with. Read only.
    int DevCastable(WeaponCapability weapon)
    {
        if (_combat == null || weapon.Elements.Count == 0) return 0;
        try
        {
            return _combat.MagicSpells.ActiveSpellBooks.Values.Where(book => book.Data.Requirements is not { AreMet: false })
                .Sum(book => book.Spells.Count(spell => weapon.Elements.Contains(spell.Element) && spell.UnlockRequirements is not { AreMet: false }));
        }
        catch (Exception e)
        {
            if (_reported.Add("dev castable")) _ctx.Log("Dev: stance: weapon: the spells could not be counted: " + e.Message);
            return 0;
        }
    }

    // The pictures of stance=shot under way: what is called after every drawn frame until they are taken, and since when.
    Action? _devStanceCapture;
    long _devStanceShotSince;

    // While stance=shot takes its pictures the request waits (DevRequests): the rest of its lines, and its put back, which
    // builds the window again. A picture that is not taken in five seconds is given up, with the list closed again.
    bool DevStanceShooting()
    {
        if (_devStanceCapture is not { } capture) return false;
        if ((long)Time.GetTicksMsec() - _devStanceShotSince < 5000) return true;
        DevStanceShotDone(capture, "Dev: stance: the pictures were given up: no frame was drawn for them in five seconds.");
        return false;
    }

    void DevStanceShotDone(Action capture, string said)
    {
        RenderingServer.FramePostDraw -= capture;
        _devStanceCapture = null;
        if (_view?.DevStanceBox?.GetPopup() is { Visible: true } open) open.Hide();
        _ctx.Log(said);
    }

    // stance=shot: shot-stance.png and crop-stance.png of the window as it is, then the Stance box's list opened as a click
    // on the box opens it, crop-stance-list.png, and the list closed again. Each picture is taken two drawn frames after
    // what it shows was put on show (the picture is of the frame drawn last), and the pixels where the box, Details' stance
    // line and the list are drawn are read back: a part of the picture where nothing is drawn says so.
    void DevStanceShot()
    {
        GameWindow? window = _window;
        if (window is not { IsAlive: true, Visible: true } || _view == null) { _ctx.Log("Dev: stance: no window to take a picture of."); return; }
        long from = (long)Engine.GetFramesDrawn();
        bool listed = false;
        void Capture()
        {
            if ((long)Engine.GetFramesDrawn() < from + 2) return;
            if (window != _window || !window.IsAlive || _view == null)
            {
                DevStanceShotDone(Capture, "Dev: stance: no picture: the window was built again, or the character changed, before it was drawn.");
                return;
            }
            try
            {
                using Image image = _ctx.Tree.Root.GetTexture().GetImage();
                Transform2D screen = _ctx.Tree.Root.GetFinalTransform();
                if (!listed)
                {
                    image.SavePng(Path.Combine(_ctx.Directory, "shot-stance.png"));
                    DevCrop(image, screen * window.Root.GetGlobalRect().Grow(10), "crop-stance.png");
                    _ctx.Log("Dev: stance: shot-stance.png and crop-stance.png; " + DevDrawn(image, screen, "the Stance box", _view.DevStanceBox?.GetGlobalRect()) + "; " +
                             DevDrawn(image, screen, "Details' stance line", _view.DevStanceDetails?.GetGlobalRect()));
                    if (_view.DevStanceBox is not { } box)
                    {
                        DevStanceShotDone(Capture, "Dev: stance: no Stance box, so no picture of its list.");
                        return;
                    }
                    box.ShowPopup();
                    listed = true;
                    from = (long)Engine.GetFramesDrawn();
                    return;
                }
                if (_view.DevStanceBox is not { } shown)
                {
                    DevStanceShotDone(Capture, "Dev: stance: the Stance box went away before its list was drawn.");
                    return;
                }
                PopupMenu list = shown.GetPopup();
                Rect2 drawn = new(list.Position, list.Size);
                DevCrop(image, screen * drawn.Grow(6), "crop-stance-list.png");
                _ctx.Log($"Dev: stance: crop-stance-list.png; the list {(list.Visible ? "is open" : "IS NOT OPEN")}, {list.ItemCount} rows, at {drawn}, " +
                         $"embedded {_ctx.Tree.Root.GuiEmbedSubwindows}; " + DevDrawn(image, screen, "the list", drawn));
                DevStanceShotDone(Capture, $"Dev: stance: put back: the list is closed again; the choice stays '{shown.Text}' until the request's put back.");
            }
            catch (Exception e) { DevStanceShotDone(Capture, "Dev: stance: the picture could not be taken: " + e.Message); }
        }
        _devStanceCapture = Capture;
        _devStanceShotSince = (long)Time.GetTicksMsec();
        RenderingServer.FramePostDraw += Capture;
    }

    void DevCrop(Image image, Rect2 frame, string file)
    {
        Rect2I part = new Rect2I((int)frame.Position.X, (int)frame.Position.Y, (int)frame.Size.X, (int)frame.Size.Y).Intersection(new Rect2I(0, 0, image.GetWidth(), image.GetHeight()));
        if (part.Size.X <= 0 || part.Size.Y <= 0) { _ctx.Log($"Dev: stance: {file} not saved: its part is off the picture."); return; }
        using Image crop = image.GetRegion(part);
        crop.SavePng(Path.Combine(_ctx.Directory, file));
    }

    // What the picture holds where something should be drawn: how many colours, and how many pixels stand out from the
    // most common one (the words and the frame of a drop-down, a picture). One colour, or almost nothing standing out, is
    // nothing drawn there.
    static string DevDrawn(Image image, Transform2D screen, string what, Rect2? where)
    {
        if (where is not { } rect) return $"{what}: not on show";
        Rect2 frame = screen * rect;
        Rect2I part = new Rect2I((int)frame.Position.X, (int)frame.Position.Y, (int)frame.Size.X, (int)frame.Size.Y).Intersection(new Rect2I(0, 0, image.GetWidth(), image.GetHeight()));
        if (part.Size.X <= 0 || part.Size.Y <= 0) return $"{what}: off the picture";
        var counts = new Dictionary<int, int>();
        for (int y = part.Position.Y; y < part.End.Y; y++)
            for (int x = part.Position.X; x < part.End.X; x++)
            {
                Color c = image.GetPixel(x, y);
                int key = ((int)(c.R * 31) << 10) | ((int)(c.G * 31) << 5) | (int)(c.B * 31);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        int all = part.Size.X * part.Size.Y, common = counts.Values.Max(), standing = all - common;
        bool drawn = counts.Count > 8 && standing > all / 20;
        return $"{what} at {part.Position.X},{part.Position.Y} {part.Size.X}x{part.Size.Y} px: {counts.Count} colours, {standing} of {all} pixels apart from the most common one: " +
               (drawn ? "drawn" : "NOTHING DRAWN THERE");
    }
}
#endif
