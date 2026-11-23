using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Entities.Woodcutting;
using GamesByMalcs.Melvor2.InfoLabels;
using GamesByMalcs.Melvor2.Stats.Nodes;
using GamesByMalcs.Melvor2.Utils;
using Godot;
using MelvorMods.Shared;

namespace WoodcuttingRates;

/// <summary>
/// The game's own names of a tree's chances that change the hits a cut takes (double damage, weak spot, instant cut),
/// which the window does not count: read from the Woodcutting page's lists of a tree's bonuses
/// (WoodcuttingActionStatsView, in the big panel and in each card's stats tooltip), where each row (ModifierStatLabel)
/// shows one stat under the name the game gives it. Copied from Fishing Rates (mods/FishingRates/BonusNames.cs), which
/// reads its rows the way Skill Bonuses does (mods/SkillBonuses/SkillStats.cs). Until a name has been read (a list is
/// filled once the page shows its tree, or its tooltip has been open), or when the game no longer has these rows, the
/// tooltip says only what kind of chance it is.
/// </summary>
sealed class BonusNames
{
    // Which stat a row of the game's list shows. Private: one that is gone after a game update throws when it is first
    // used, and the names are then given up for good, with one line in the log.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Stats")]
    static extern ModifierStatNodes? Nodes(ModifierStatLabel label);

    readonly Dictionary<string, string> _names = new();
    readonly Tempo _again = new(5000);
    bool _failed;

    /// <summary>What a chance is, when the game's name for it has not been read: the start of a tooltip's line.</summary>
    public static string Kind(string key) => key switch
    {
        TreeRates.DoubleDamage => "Chance of double damage",
        TreeRates.WeakSpot => "Chance to hit a weak spot",
        _ => "Chance of an instant cut",
    };

    /// <summary>The game's name for this chance, or null.</summary>
    public string? Of(string key) => _names.GetValueOrDefault(key);

    /// <summary>
    /// Reads the names from the page when a chance is still without one, every few seconds at most: the rows are there
    /// only once a list of the page has been filled.
    /// </summary>
    public void Read(Node page, IEnumerable<TreeRates> trees, Action<string> report)
    {
        if (_failed) return;
        bool wanted = false;
        foreach (TreeRates tree in trees)
            foreach (TreeRates.Bonus bonus in tree.HitChances)
                wanted |= !_names.ContainsKey(bonus.Key);
        if (!wanted || !_again.Due()) return;
        try { ReadRows(page, trees); }
        catch (Exception e)
        {
            _failed = true;
            report("The game's names of a tree's chances on the Woodcutting page could not be read (the game was updated?); the tooltip describes them instead. " + e.Message);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void ReadRows(Node page, IEnumerable<TreeRates> trees)
    {
        // A row is bound to the stats of one tree; every listed tree's stats are asked which chance they are.
        var keys = new Dictionary<ModifierStatNodes, string>(ReferenceEqualityComparer.Instance);
        foreach (TreeRates tree in trees)
        {
            WoodcuttingActionStats stats = tree.Tree.Stats;
            if (stats.DoubleDamageChance != null) keys.TryAdd(stats.DoubleDamageChance, TreeRates.DoubleDamage);
            if (stats.WeakSpotChance != null) keys.TryAdd(stats.WeakSpotChance, TreeRates.WeakSpot);
            if (stats.InstantCutChance != null) keys.TryAdd(stats.InstantCutChance, TreeRates.InstantCut);
        }
        // Every list of a tree's bonuses on the page, the big panel's and those in the cards' stats tooltips, and in each
        // its rows; a row's own parts are not looked through.
        void Walk(Node node, bool inList)
        {
            for (int i = 0; i < node.GetChildCount(); i++)
            {
                Node child = node.GetChild(i);
                if (child.IsQueuedForDeletion()) continue;
                if (inList && child is ModifierStatLabel row)
                {
                    if (Nodes(row) is { } nodes && keys.TryGetValue(nodes, out string? key) &&
                        StringUtils.StripBBCode(row.StatName ?? "").Trim() is { Length: > 0 } name)
                        _names[key] = name;
                }
                else Walk(child, inList || child is WoodcuttingActionStatsView);
            }
        }
        Walk(page, false);
    }
}
