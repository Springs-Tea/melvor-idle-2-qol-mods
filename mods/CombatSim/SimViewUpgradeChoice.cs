using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MelvorMods.Shared;

namespace CombatSim;

/// <summary>
/// The panel of Find upgrades (issue #4): which items a search tries, chosen before anything runs, and how many fights
/// that is and about how long, beside the button that starts it. It is made like the simulator's own floating panels
/// (StartPicker and PlacePicker, as "Where to fight", the item picker and Columns), and opens where History and Columns
/// do, under the buttons over the list, so it covers none of the controls of Details: the item picker's search box, the
/// gold headings of "Where to fight" and Columns, the game's tick boxes of Columns (GameStyle.TickBox), and a foot with
/// its line and a TexturedButton4x, as "Where to fight" has. Where items come from is chosen in the Item Encyclopedia's
/// words for its "Comes from" filter (ItemLines.Kinds), with "Owned" and "Drops in" the fight's area beside them. The
/// choice is kept in settings.json (CombatSimMod.WriteUpgradeChoice).
/// </summary>
sealed partial class SimView
{
    const string UpgradePanel = "upgrades";
    // The fight the open panel chooses for, and its controls, for its counts and the development requests.
    SimTarget? _choiceTarget;
    LineEdit? _choiceSearch;
    readonly List<(Button Tick, bool Slot, string Id, string Name)> _choiceTicks = new();
    Button? _choiceCannot, _choiceGo;
    Label? _choiceSummary;
    // Where items come from was known (or could not be read) when the panel was made: its kinds are offered.
    bool _choiceKnown;
    bool _choiceSyncing;

    /// <summary>How long a search of that many fights takes, by the rule of the line above Simulate, at the search's own pace.</summary>
    string SearchEstimate(int fights, double hours) => "about " + Took(fights, hours, UpgradeSearch.Workers);

    void OpenUpgradeChoice(SimTarget target)
    {
        bool wasOpen = _pickerKind == UpgradePanel;
        ClosePicker();
        if (wasOpen || Finder is not { } finder) return;   // the button is a switch, as History's is
        const float width = 340, height = 560;
        VBoxContainer column = StartPicker(UpgradePanel, "Find upgrades", width, height);
        _choiceTarget = target;
        _choiceTicks.Clear();
        // Asking starts reading where items come from, once a character.
        UpgradePick pick = finder.PickFor(target);
        _choiceKnown = pick.Known || pick.Failed;
        UpgradeChoice choice = _state.UpgradeChoice;

        string stance = target.StanceName != "" ? $" ({target.StanceName})" : "";
        string length = SimState.Durations[Math.Clamp(_state.Duration, 0, SimState.Durations.Length - 1)].Label;
        column.AddChild(Wrapped($"For {target.Name}{stance}, on top of {_state.SetupName}. Each item is one fight of {length}, " +
                                "from the same moment of your character and with the same luck as the setup as it is.", width - 30, GameStyle.TextGrey, 12));

        var search = _choiceSearch = new LineEdit { PlaceholderText = "Search items...", ClearButtonEnabled = true, Text = choice.Search };
        search.AddThemeFontSizeOverride("font_size", _s.Px(14));
        search.TooltipText = "Only the items with these words in their name";
        column.AddChild(search);

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var list = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 2);
        scroll.AddChild(list);
        column.AddChild(scroll);

        // The slots of the setup on the left, in the game's order; none ticked is every slot.
        list.AddChild(ChoiceHeading("Slots", "Untick every slot: with none ticked, every slot is tried", choice.Slots));
        foreach (GearSlot slot in _state.Gear.GroupBy(g => g.SlotId).Select(g => g.First()))
        {
            string holds = slot.ItemId == null ? "it is empty now" : slot.FilledFrom != "" ? $"{slot.FilledFromName}'s {slot.ItemName} fills it now" : $"in place of {slot.ItemName}";
            AddChoiceTick(list, true, slot.SlotId, slot.SlotName, $"Items for the {slot.SlotName} slot: {holds}");
        }

        // Where they come from; none ticked is every item. Until it has been read, only what you have can be told.
        list.AddChild(ChoiceHeading("Comes from", "Untick every kind: with none ticked, every item is tried, wherever it comes from", choice.Kinds));
        foreach (var (id, name, tip) in pick.Kinds) AddChoiceTick(list, false, id, name, tip);
        if (!pick.Known)
            list.AddChild(Wrapped(pick.Failed ? "Where items come from could not be read: see MelvorModLoader.log. Only Owned can be chosen."
                : "Reading where items come from...", width - 40, GameStyle.TextDim, 12));

        _choiceCannot = _s.TickBox("List the items I cannot wear yet", choice.CannotWear);
        _choiceCannot.TooltipText = "Items whose requirements you do not meet, such as a level, at the end of the list in grey with\n" +
                                    "what they need. The simulator cannot try them: the game does not let the copy wear them\n" +
                                    $"either, so they cost no fight. At most {UpgradeSearch.MaxListed} are listed.";
        _choiceCannot.Toggled += on =>
        {
            if (_choiceSyncing) return;
            choice.CannotWear = on;
            ChoiceChanged();
        };
        column.AddChild(_choiceCannot);

        // As "Where to fight": what the choice comes to, and the button that runs it.
        var foot = _s.Spaced(new HBoxContainer(), 8);
        _choiceSummary = Wrapped("", width - 130, GameStyle.TextGrey, 12);
        _choiceSummary.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        foot.AddChild(_choiceSummary);
        _choiceGo = _s.GameButton("Simulate", "TexturedButton4x", 13);
        _choiceGo.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        _choiceGo.FocusMode = Control.FocusModeEnum.None;
        _choiceGo.Pressed += () =>
        {
            if (_choiceTarget is not { } fight || Finder is not { } find) return;
            ClosePicker();
            find.FindUpgrades(fight);
            Refresh();
        };
        foot.AddChild(_choiceGo);
        column.AddChild(foot);

        search.TextChanged += text =>
        {
            choice.Search = text;
            ChoiceChanged();
        };
        SyncUpgradeChoice();
        // Over the list, where History and Columns open: placed beside the button, it covered "Wear this setup first" and
        // Fight (game check 2).
        PlacePicker(_historyButton, width, height, below: true);
        search.GrabFocus();
    }

    // A tick of the panel, its count put in by SyncUpgradeChoice.
    void AddChoiceTick(VBoxContainer list, bool slot, string id, string name, string tip)
    {
        HashSet<string> chosen = slot ? _state.UpgradeChoice.Slots : _state.UpgradeChoice.Kinds;
        Button tick = _s.TickBox(name, chosen.Contains(id));
        tick.TooltipText = tip;
        tick.Toggled += on =>
        {
            if (_choiceSyncing) return;
            if (on) chosen.Add(id); else chosen.Remove(id);
            ChoiceChanged();
        };
        list.AddChild(tick);
        _choiceTicks.Add((tick, slot, id, name));
    }

    // The headings of "Where to fight" and Columns: gold, the game's semi-bold font; a click unticks the whole group.
    Button ChoiceHeading(string title, string tip, HashSet<string> group)
    {
        var heading = new Button
        {
            Text = title, Flat = true, Alignment = HorizontalAlignment.Left, FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, TooltipText = tip,
        };
        heading.AddThemeFontSizeOverride("font_size", _s.Px(12));
        if (GameStyle.SemiBold is { } font) heading.AddThemeFontOverride("font", font);
        foreach (string name in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
            heading.AddThemeColorOverride(name, name == "font_hover_color" ? GameStyle.TextWhite : GameStyle.TextGold);
        heading.Pressed += () =>
        {
            if (group.Count == 0) return;
            group.Clear();
            ChoiceChanged();
        };
        return heading;
    }

    // The choice is kept as it is made, and the panel counts again.
    void ChoiceChanged()
    {
        _changed();
        SyncUpgradeChoice();
    }

    /// <summary>The open panel's ticks, counts, line and button, for the choice as it is now; nothing when it is not open.</summary>
    void SyncUpgradeChoice()
    {
        if (_pickerKind != UpgradePanel || _choiceSummary == null || !GodotObject.IsInstanceValid(_choiceSummary)
            || _choiceTarget is not { } target || Finder is not { } finder) return;
        UpgradePick pick = finder.PickFor(target);
        // Where items come from has been read since the panel was made: made again, with its kinds.
        if ((pick.Known || pick.Failed) != _choiceKnown)
        {
            ClosePicker();
            OpenUpgradeChoice(target);
            return;
        }
        UpgradeChoice choice = _state.UpgradeChoice;
        _choiceSyncing = true;
        foreach (var (tick, slot, id, name) in _choiceTicks)
        {
            if (!GodotObject.IsInstanceValid(tick)) continue;
            bool on = (slot ? choice.Slots : choice.Kinds).Contains(id);
            if (tick.ButtonPressed != on) tick.ButtonPressed = on;   // its own Toggled swaps the box's picture
            tick.Text = $"{name} ({(slot ? pick.PerSlot.GetValueOrDefault(id) : pick.PerKind.GetValueOrDefault(id))})";
        }
        if (_choiceCannot != null && GodotObject.IsInstanceValid(_choiceCannot))
        {
            if (_choiceCannot.ButtonPressed != choice.CannotWear) _choiceCannot.ButtonPressed = choice.CannotWear;
            _choiceCannot.Text = $"List the items I cannot wear yet ({pick.CannotWear.Count})";
        }
        _choiceSyncing = false;

        int n = pick.ToTry.Count;
        double hours = SimState.Durations[Math.Clamp(_state.Duration, 0, SimState.Durations.Length - 1)].Hours;
        string busy = _state.Running ? "A simulation is running: Find upgrades waits until it has ended." : "";
        var (words, tint, can) = pick.Waiting ? ("Reading where items come from...", GameStyle.TextGrey, false)
            : n == 0 ? (pick.CannotWear.Count > 0 ? "You cannot wear any of these yet: nothing to try." : "No item fits this choice.", GameStyle.TextGold, false)
            : pick.TooMany ? ($"{n} items: at most {UpgradeSearch.MaxItems} a search. Choose fewer: tick a slot, or where they come from.", GameStyle.TextGold, false)
            : ($"{n} {(n == 1 ? "item" : "items")}  ·  {n + 1} fights  ·  {SearchEstimate(n + 1, hours)}", GameStyle.TextGrey, busy == "");
        if (choice.CannotWear && pick.CannotWear.Count > 0)
            words += pick.TooManyToList ? $"\n{pick.CannotWear.Count} you cannot wear yet: too many to list, at most {UpgradeSearch.MaxListed}."
                : $"\nand {pick.CannotWear.Count} you cannot wear yet, listed";
        _choiceSummary.Text = words;
        _choiceSummary.AddThemeColorOverride("font_color", tint);
        if (_choiceGo != null && GodotObject.IsInstanceValid(_choiceGo))
        {
            _choiceGo.Disabled = !can;
            string stance = target.StanceName != "" ? $" in the {target.StanceName} stance" : "";
            _choiceGo.TooltipText = can ? $"Try these {n} items against {target.Name}{stance}, one fight each, one at a time while you play.\n" +
                                          "The setup as it is is fought once more beside them, with the same luck. Nothing is put on."
                : TipLines(busy != "" && n > 0 && !pick.TooMany && !pick.Waiting ? busy : words.Replace("\n", " "));
        }
    }

#if DEVTOOLS
    /// <summary>
    /// Development: the panel's requests (CombatSimMod.DevUpgradesCommand). "slot=" and "from=" click a tick whose name has
    /// those words, "cannot" the tick of the items that cannot be worn yet, "search=" types into the search box, "clear"
    /// unticks everything and empties the box, "go" presses Simulate (after upgrades=go=&lt;most&gt; has read the count back,
    /// <see cref="DevChoiceCount"/>), "panel" says what the panel shows.
    /// </summary>
    public string DevUpgradeChoice(string what)
    {
        if (_pickerKind != UpgradePanel || _choiceSummary == null || !GodotObject.IsInstanceValid(_choiceSummary)) return $"{what}: the panel is not open";
        if (what == "panel") return "panel: " + DevChoicePanel();
        if (what == "go")
        {
            if (_choiceGo is not { Disabled: false } go) return $"go: not pressed, Simulate is greyed: {DevChoicePanel()}";
            go.EmitSignal(BaseButton.SignalName.Pressed);
            return $"go: pressed Simulate; {(_state.Upgrades is { } u ? $"search {u.Id} of {u.Asked} items for {u.Target.Name} {(u.Running ? "running" : "not running")}" : "no search")}";
        }
        if (what == "cannot")
        {
            _choiceCannot!.ButtonPressed = !_choiceCannot.ButtonPressed;   // its own Toggled, as a click
            return $"cannot: {(_choiceCannot.ButtonPressed ? "ticked" : "unticked")}; {DevChoicePanel()}";
        }
        if (what == "clear")
        {
            _state.UpgradeChoice.Slots.Clear();
            _state.UpgradeChoice.Kinds.Clear();
            _choiceSearch!.Text = "";
            _state.UpgradeChoice.Search = "";
            ChoiceChanged();
            return "clear: " + DevChoicePanel();
        }
        if (what.StartsWith("search=", StringComparison.Ordinal))
        {
            _choiceSearch!.Text = what[7..];
            _choiceSearch.EmitSignal(LineEdit.SignalName.TextChanged, _choiceSearch.Text);
            return $"search: '{_choiceSearch.Text}'; {DevChoicePanel()}";
        }
        bool slot = what.StartsWith("slot=", StringComparison.Ordinal);
        string name = what[(what.IndexOf('=') + 1)..].Trim();
        var found = _choiceTicks.FirstOrDefault(t => t.Slot == slot && GodotObject.IsInstanceValid(t.Tick) && t.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (found.Tick == null) return $"{what}: no tick called {name} among the {(slot ? "slots" : "kinds of source")} ({string.Join(", ", _choiceTicks.Where(t => t.Slot == slot).Select(t => t.Name))})";
        found.Tick.ButtonPressed = !found.Tick.ButtonPressed;   // its own Toggled, as a click
        return $"{what}: '{found.Tick.Text}' {(found.Tick.ButtonPressed ? "ticked" : "unticked")}; {DevChoicePanel()}";
    }

    /// <summary>
    /// Development: how many items the open panel's own line says a press tries, read back from its words ("3 items  ·  4
    /// fights  ·  about 5s", "41 items: at most 40 a search..."); null when the panel is not open or its line gives no count.
    /// </summary>
    public int? DevChoiceCount()
    {
        if (_pickerKind != UpgradePanel || _choiceSummary is not { } line || !GodotObject.IsInstanceValid(line)) return null;
        string text = line.Text;
        int digits = text.TakeWhile(char.IsAsciiDigit).Count();
        return digits > 0 && text[digits..].StartsWith(" item", StringComparison.Ordinal) && int.TryParse(text[..digits], out int n) ? n : null;
    }

    string DevChoicePanel()
    {
        string Ticks(bool slot) => string.Join(", ", _choiceTicks.Where(t => t.Slot == slot && GodotObject.IsInstanceValid(t.Tick))
            .Select(t => t.Tick.Text + (t.Tick.ButtonPressed ? " [x]" : "")));
        return $"at {_picker?.GetGlobalRect()}; for {_choiceTarget?.Name} [{_choiceTarget?.Key}]; search '{_choiceSearch?.Text}'; slots: {Ticks(true)}; " +
               $"comes from: {Ticks(false)}; '{_choiceCannot?.Text}' {(_choiceCannot?.ButtonPressed == true ? "ticked" : "unticked")}; " +
               $"the line '{_choiceSummary?.Text.Replace("\n", " | ")}' ({(_choiceSummary == null ? "-" : DevTint(_choiceSummary.GetThemeColor("font_color")))}); " +
               $"Simulate {(_choiceGo?.Disabled == true ? "greyed" : "can be pressed")}, its tooltip '{_choiceGo?.TooltipText.Replace("\n", " | ")}'";
    }
#endif
}
