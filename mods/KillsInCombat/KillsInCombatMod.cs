using System;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Combat;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace KillsInCombat;

/// <summary>
/// Shows how many times you have killed the current enemy, in a small tag next to its name on the combat
/// screen. The number is the game's own statistic (the one the Completion Log counts with), so it is right
/// after offline progress too.
/// </summary>
public class KillsInCombatMod : IMod
{
    ModContext _ctx = null!;
    readonly GameStyle _style = new();
    Game? _game;
    CombatEntity? _combat;
    Label? _name;            // the game's enemy name label
    PanelContainer? _tag;    // our tag, a child of it
    Label? _count;
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
        if (game is not { IsSaveLoaded: true }) { _game = null; _combat = null; return; }
        if (!ReferenceEquals(game, _game))
        {
            _game = game;
            _combat = game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? e) ? e as CombatEntity : null;
            _name = null;
        }
        if (_combat == null) return;
        try { Update(_combat); }
        catch (Exception e)
        {
            _ctx.Log("Stopped: " + e);
            _combat = null;
        }
    }

    void Update(CombatEntity combat)
    {
        if (!EnsureTag()) return;
        Enemy? enemy = combat.Enemy.Enemy;
        if (enemy == null || !_name!.IsVisibleInTree()) { _tag!.Visible = false; return; }

        double kills = combat.Statistics.EnemyKilled.GetValue(enemy);
        _count!.Text = kills.ToString("N0");
        _tag!.TooltipText = $"{enemy.DisplayName.Text}: killed {kills:N0} times";
        _tag.Visible = true;
        GameStyle.PlaceAfterText(_name, _tag);
    }

    // The combat screen is built when it is first opened and may be rebuilt later: find the name label again
    // whenever ours is gone.
    bool EnsureTag()
    {
        if (_tag != null && GodotObject.IsInstanceValid(_tag) && _name != null && GodotObject.IsInstanceValid(_name)) return true;
        _tag = null;
        _name = GameAccess.FindNode<EnemyNamePanel>(GameAccess.FindGameView())?.FindChild("NameLabel", true, false) as Label;
        if (_name == null) return false;   // normal until the combat screen has been opened once

        _tag = _style.CountTag(GameStyle.Icon("Combat"), out _count);
        _tag.Name = "KillsInCombatTag";
        _name.AddChild(_tag);   // laid over the big centred name, just past its text
        if (!_addedLogged) { _addedLogged = true; _ctx.Log("Tag added next to the enemy's name."); }
        return true;
    }
}
