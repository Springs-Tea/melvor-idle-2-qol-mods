using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Data.Items;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace ItemSources;

public partial class ItemSourcesMod
{
    // A step is laid out like a line of "Found in" (Build): its 20 px picture, then the name 8 px further.
    const int PictureSize = 20, RowGap = 8;

    Item? _makeItem;
    MakeRecipes? _recipes;
    MakeInputs? _inputs;
    Button? _makeButton;
    SpinBox? _quantity;
    ScrollContainer? _makeScroll;
    VBoxContainer? _rows;
    // The line, the note and the column headings above the steps: made once, so the tooltip of a heading stays
    // up while the steps under it are drawn again. They are the first _headCount children of _rows.
    Control[]? _headCells;
    int _headCount;
    // The game's requirement views in the plan; each listens to its conditions until it is unbound.
    readonly List<Control> _requirementViews = new();
    Control? _footer;
    int _lastQuantity;
    bool _makeFailed, _footerReported;

    void RefreshMakeBox()
    {
        if (_makeFailed || _index is not { } index || _makeItem is not { } item || _pinCard == null) return;
        try { ShowMakePlan(index, item); }
        catch (Exception e) { SwitchMakeOff(e); }
    }

    void SwitchMakeOff(Exception e)
    {
        _makeFailed = true;
        _ctx.Log("Make is switched off (the game was updated?): " + e.Message);
        _makeButton?.Hide();
        _quantity?.Hide();
        _makeScroll?.Hide();
        _pinCard?.ResetSize();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void ShowMakePlan(ItemIndex index, Item item)
    {
        if (_makeButton == null)
        {
            if (index.For(item)?.Sources.Any(MakeRecipes.MakesItem) != true) { _makeItem = null; return; }
            var column = (VBoxContainer)_pinPanel!.GetParent();
            var hint = (Label)column.GetChild(-1);
            var header = _style.Spaced(new HBoxContainer(), 8);
            column.AddChild(header);
            hint.Reparent(header, false);
            hint.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            hint.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            _makeButton = _style.LinkTag(null, "Make");
            _makeButton.ToggleMode = true;
            header.AddChild(_makeButton);
            header.MoveChild(_makeButton, 0);
            _pinCard!.Theme = _style.TooltipTheme();
            // A click opens or folds the plan at once, not on the next look round.
            _makeButton.Toggled += _ => RefreshMakeBox();
        }

        if (_footer == null || !GodotObject.IsInstanceValid(_footer))
        {
            _footer = GameAccess.FindGameView()?.Footer;
            if (_footer == null && !_footerReported)
            {
                _footerReported = true;
                _ctx.Log("The game's bottom bar was not found. The pinned plan may lie over it.");
            }
        }
        if (!_makeButton.ButtonPressed)
        {
            if (_makeScroll is { Visible: true })
            {
                _quantity!.Hide();
                _makeScroll.Hide();
                _inputs = null;
                _pinCard!.ResetSize();
            }
            return;
        }
        if (_makeScroll == null) BuildMakeBox();
        _quantity!.Show();
        _makeScroll!.Show();
        _recipes ??= new(_game!, index, _lines!);
        _recipes.BeginRead(_footer);
        int requested = (int)_quantity.Value;
        if (_inputs == null || _lastQuantity != requested || _inputs.Changed)
        {
            _inputs = new MakeInputs();
            _lastQuantity = requested;
            // Release the old steps from the layout at once, keeping the headings and the scroll position.
            var scrollAt = _makeScroll.ScrollVertical;
#if DEVTOOLS
            _stepSpots.Clear();
#endif
            ReleaseRequirementViews();
            for (int i = _rows!.GetChildCount() - 1; i >= _headCount; i--)
            {
                Node child = _rows.GetChild(i);
                _rows.RemoveChild(child);
                child.QueueFree();
            }
            try
            {
                var plan = new MakePlan(_recipes, item, requested, _inputs);
                DrawPlan(plan);
#if DEVTOOLS
                LogPlan(item, plan);
#endif
            }
            catch (OverflowException)
            {
                _rows.AddChild(Note("This quantity is too large to plan. Enter a smaller number.", GameStyle.TextPaper, 13));
            }
            _makeScroll.ScrollVertical = scrollAt;
            Callable.From(FitAfterLayout).CallDeferred();
        }
        FitMakeBox();
    }

    void BuildMakeBox()
    {
        var header = (HBoxContainer)_makeButton!.GetParent();
        _quantity = _style.GoalBox();
        _quantity.MinValue = 1;
        _quantity.MaxValue = int.MaxValue;
        _quantity.Value = 100;
        _quantity.TooltipText = "How many you want in the bank. Press Enter to update.";
        _quantity.GetLineEdit().TooltipText = _quantity.TooltipText;
        header.AddChild(_quantity);
        header.MoveChild(_quantity, 1);
        // Enter (or an arrow of the box) shows the new plan at once.
        _quantity.ValueChanged += _ => RefreshMakeBox();

        _makeScroll = new ScrollContainer();
        ((VBoxContainer)header.GetParent()).AddChild(_makeScroll);
        _rows = _style.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 4);
        // EncyclopediaView.BuildFilters leaves a margin between its contents and the game's scroll bar.
        var room = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        room.AddThemeConstantOverride("margin_right", _style.Px(10));
        room.AddChild(_rows);
        _makeScroll.AddChild(room);

        // What the counts leave out is said once, above the table; where a time comes from, in its heading.
        _rows.AddChild(_style.FadingLine());
        _rows.AddChild(Note("Chances to save materials, double items or skip steps are not counted, so it may take less."));
        var head = TableRow(_style.Text("Item", GameStyle.TextDim, 13), GameStyle.TextDim, "Needed", "In the bank", "Still to make or get", "Time");
        var time = (Control)head.GetChild(-1);
        time.TooltipText = "How long each step takes at your current rate. For an item you gather or find, the rate the bottom bar shows for it.";
        time.MouseFilter = Control.MouseFilterEnum.Pass;
        _rows.AddChild(head);
        _headCells = head.GetChildren().Cast<Control>().ToArray();
        _headCount = _rows.GetChildCount();
    }

    void DrawPlan(MakePlan plan)
    {
        // Every row gets the same column widths, measured on the real font, names, counts, times and headings.
        var columns = new List<Control[]> { _headCells! };
        foreach (var cell in _headCells!) cell.CustomMinimumSize = Vector2.Zero;
        string? group = null;
        foreach (MakePlan.Row step in plan.Rows)
        {
            if (group != step.Step.Group)
            {
                _rows!.AddChild(GroupTitle(step.Step.Group, GameStyle.Icon(step.Step.Group.Replace(" ", "")), group == null));
                group = step.Step.Group;
            }
            // A step the bank already covers is done: in the quiet grey, with nothing left and no time.
            var name = _style.Spaced(new HBoxContainer(), RowGap);
            name.AddChild(_style.Picture(step.Material is Item i ? i.Icon : ((Currency)step.Material).Icon, PictureSize));
            name.AddChild(_style.Text(step.Name, step.Covered ? GameStyle.TextDim : null));
            var row = TableRow(name, step.Covered ? GameStyle.TextDim : GameStyle.TextGrey, $"{step.Needed:N0}", $"{step.Bank:N0}",
                step.Covered ? MakePlan.Nothing : $"{step.Left:N0}", step.Time);
            _rows!.AddChild(row);
            columns.Add(row.GetChildren().Cast<Control>().ToArray());
#if DEVTOOLS
            if (step.Material is Currency) _stepSpots.TryAdd("currency", row);
            if (step.Step.FooterRate > 0 && step.Left > 0) _stepSpots.TryAdd("rate", row);
#endif
            if (step.Covered) continue;

            foreach (MakeNote note in step.Step.Notes)
            {
                if (note.Requirements is not { } requirements) { AddUnder(Note(note.Text)); continue; }
                Control view = AddRequirements(requirements);
#if DEVTOOLS
                if (step.Step.Locked) _stepSpots.TryAdd("locked", view);
#endif
            }
            if (step.Cycle)
            {
                var cycle = Note("This chain uses the item again. Get the remainder separately.", GameStyle.TextPaper, 13);
                AddUnder(cycle);
#if DEVTOOLS
                _stepSpots.TryAdd("cycle", cycle);
#endif
            }
            if (step.SharedWith is { } other)
                AddUnder(Note($"Comes from the same recipe as {other.Name}: the time is not counted twice."));
        }

        for (int col = 0; col < columns[0].Length; col++)
        {
            float width = columns.Max(cells => cells[col].GetCombinedMinimumSize().X);
            foreach (var cells in columns) cells[col].CustomMinimumSize = new Vector2(width, 0);
        }
    }

    // One line of the table: the name, then the numbers on the right ("-" where there is nothing, dimmed).
    HBoxContainer TableRow(Control name, Color tint, params string[] values)
    {
        var row = _style.Spaced(new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin }, RowGap);
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(name);
        foreach (var value in values)
        {
            var cell = _style.Text(value, value == MakePlan.Nothing ? GameStyle.TextDim : tint, 13);
            cell.HorizontalAlignment = HorizontalAlignment.Right;
            row.AddChild(cell);
        }
        return row;
    }

    // Words of the plan, wrapped to the table's width instead of widening the card (as SimView's notes are).
    Label Note(string text, Color? color = null, int size = 12)
    {
        Label label = _style.Text(text, color ?? GameStyle.TextDim, size);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        return label;
    }

    // Under its step, in line with the step's name.
    void AddUnder(Control note)
    {
        var indent = new MarginContainer();
        indent.AddThemeConstantOverride("margin_left", _style.Px(PictureSize + RowGap));
        indent.AddChild(note);
        _rows!.AddChild(indent);
    }

    // The game's own view of a requirement group, the one its recipe buttons show a locked recipe's needs in
    // (CraftingRecipeButton.RequirementsContainer): the game's words, icons, "or" and colours for met and not met.
    [MethodImpl(MethodImplOptions.NoInlining)]
    Control AddRequirements(ConditionGroup group)
    {
        var view = new ConditionGroupView { ColourConditions = true };
        AddUnder(view);
        _requirementViews.Add(view);
        // Bound once it is in the scene, like the copy of the item panel.
        view.Bind(group);
        foreach (Node node in view.FindChildren("*", "Control", true, false))
        {
            // As tall as its words in a list that gives it no height of its own (no change where it already is).
            if (node is RichTextLabel words) words.FitContent = true;
            // Its lines must not stop the wheel that scrolls the plan; what they do with the pointer stays theirs.
            if (node is Control { MouseFilter: Control.MouseFilterEnum.Stop } line) line.MouseFilter = Control.MouseFilterEnum.Pass;
        }
        return view;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void ReleaseRequirementViews()
    {
        foreach (var view in _requirementViews)
            if (GodotObject.IsInstanceValid(view)) { try { ((ConditionGroupView)view).Unbind(); } catch { } }
        _requirementViews.Clear();
    }

    float FooterHeight() => _footer != null && GodotObject.IsInstanceValid(_footer) && _footer.IsVisibleInTree()
        ? Math.Max(0, _pinCard!.GetViewportRect().End.Y - _footer.GetGlobalRect().Position.Y) : 0;

    // Wrapped words and the game's requirement lines know their height once the plan has its width, after the
    // layout of this frame: the card is fitted again then.
    void FitAfterLayout()
    {
        if (_makeFailed || _makeScroll is not { Visible: true } || _pinCard == null || !GodotObject.IsInstanceValid(_pinCard)) return;
        try { FitMakeBox(); }
        catch (Exception e) { SwitchMakeOff(e); }
    }

    void FitMakeBox()
    {
        var card = _pinCard!;
        var scroll = _makeScroll!;
        var column = (VBoxContainer)scroll.GetParent();
        var content = ((Control)_rows!.GetParent()).GetCombinedMinimumSize();
        var screen = card.GetViewportRect().Size - card.GetThemeStylebox("panel").GetMinimumSize();
        float fixedHeight = column.GetChildren().OfType<Control>().Where(c => c != scroll && c.Visible)
            .Sum(c => c.GetCombinedMinimumSize().Y + column.GetThemeConstant("separation"));
        float room = Math.Max(0, screen.Y - FooterHeight() - fixedHeight);
        float width = content.X + (content.Y > room ? scroll.GetVScrollBar().GetCombinedMinimumSize().X : 0);
        float height = content.Y + (width > screen.X ? scroll.GetHScrollBar().GetCombinedMinimumSize().Y : 0);
        var size = new Vector2(Math.Min(width, screen.X), Math.Min(height, room));
        if (scroll.CustomMinimumSize == size) return;
        scroll.CustomMinimumSize = size;
        card.ResetSize();
    }
}
