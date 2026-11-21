using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Combat;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace DungeonCompletions;

/// <summary>
/// Shows how many times each dungeon has been completed, in a small tag next to the dungeon's name: on every
/// dungeon in the combat areas menu, and on the panel of the one that is selected there. The number is the
/// game's own statistic.
/// </summary>
public class DungeonCompletionsMod : IMod
{
    const string TagName = "DungeonCompletionsTag";

    // The area a button of the menu stands for, and the name labels, are private to the game's views.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Area")]
    static extern ActiveCombatArea? AreaOf(CombatAreaButton button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NameLabel")]
    static extern Label? NameLabelOf(CombatAreaButton button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NameLabel")]
    static extern Label? NameLabelOf(CombatAreaInfoPanel panel);

    ModContext _ctx = null!;
    readonly GameStyle _style = new();
    Game? _game;
    CombatEntity? _combat;
    CombatAreasMenu? _menu;
    int _frame;
    bool _addedLogged;

    public void OnLoad(ModContext ctx)
    {
        _ctx = ctx;
        ctx.Frame += OnFrame;
        ctx.Log("Ready.");
    }

    void OnFrame()
    {
        if (++_frame % 15 != 0) return;   // four times a second is plenty
        Game? game = GameAccess.CurrentGame;
        if (game is not { IsSaveLoaded: true }) { _game = null; _combat = null; _menu = null; return; }
        if (!ReferenceEquals(game, _game))
        {
            _game = game;
            _combat = game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? e) ? e as CombatEntity : null;
            _menu = null;
        }
        if (_combat == null) return;
        try { Update(_combat); }
        catch (Exception e)
        {
            // A private member that is gone after a game update ends up here.
            _ctx.Log("Stopped (the game was updated?): " + e);
            _combat = null;
        }
    }

    void Update(CombatEntity combat)
    {
        // The menu is part of the combat screen, which is built when it is first opened. Looking for it means
        // walking the whole screen, so do that only once a second.
        if (_menu == null || !GodotObject.IsInstanceValid(_menu))
        {
            _menu = null;
            if (_frame % 60 != 0) return;
            _menu = GameAccess.FindNode<CombatAreasMenu>(GameAccess.FindGameView());
            if (_menu == null) return;
        }
        if (!_menu.IsVisibleInTree()) return;

        var buttons = new List<CombatAreaButton>();
        CombatAreaInfoPanel? info = null;
        Collect(_menu, buttons, ref info);

        // The menu reuses its buttons for other areas, so every tag is set again each time.
        foreach (CombatAreaButton button in buttons)
            Show(combat, NameLabelOf(button), AreaOf(button));
        if (info != null)
            Show(combat, NameLabelOf(info), combat.Areas.VisibleArea);
    }

    static void Collect(Node node, List<CombatAreaButton> buttons, ref CombatAreaInfoPanel? info)
    {
        if (node is CombatAreaButton button) { buttons.Add(button); return; }
        if (node is CombatAreaInfoPanel panel) { info = panel; return; }
        foreach (Node child in node.GetChildren()) Collect(child, buttons, ref info);
    }

    void Show(CombatEntity combat, Label? name, ActiveCombatArea? area)
    {
        if (name == null || !GodotObject.IsInstanceValid(name)) return;
        // Our tag is either the label's child or its next neighbour in a row: see GameStyle.AttachAfterText.
        Node? holder = name.GetParent();
        var tag = (name.GetNodeOrNull(TagName) ?? holder?.GetNodeOrNull(TagName)) as PanelContainer;

        if (area?.Area is not Dungeon dungeon)
        {
            if (tag != null) tag.Visible = false;
            return;
        }
        if (tag == null)
        {
            tag = _style.CountTag(GameStyle.Icon("CompletionLog"), out _);
            tag.Name = TagName;
            GameStyle.AttachAfterText(name, tag);
            if (!_addedLogged) { _addedLogged = true; _ctx.Log("Tags added to the dungeons in the combat areas menu."); }
        }

        double completed = combat.Statistics.DungeonCompleted.GetValue(dungeon);
        if (tag.FindChildren("*", "Label", true, false).OfType<Label>().FirstOrDefault() is { } count)
            count.Text = completed.ToString("N0");
        tag.TooltipText = $"{dungeon.DisplayName.Text}: completed {completed:N0} times";
        tag.Visible = true;
        GameStyle.PlaceAfterText(name, tag);
    }
}
