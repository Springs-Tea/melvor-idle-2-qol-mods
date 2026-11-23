using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Entities.Mining;
using GamesByMalcs.Melvor2.InfoLabels;
using GamesByMalcs.Melvor2.Stats.Nodes;
using GamesByMalcs.Melvor2.Utils;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace MiningRates;

/// <summary>
/// The game's own names of the bonuses the mod does not count (shatter, more ore), read from the Mining page's list of
/// bonuses (MiningActionStatsView), where each row (ModifierStatLabel) shows one stat under the name the game gives it.
/// Fishing Rates reads its page's rows the same way (mods/FishingRates/BonusNames.cs), as Skill Bonuses does
/// (mods/SkillBonuses/SkillStats.cs). Until a name has been read, or when the game no longer has these rows, the tooltip
/// says only what kind of bonus it is.
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

    /// <summary>What a bonus is, when the game's name for it has not been read.</summary>
    public static string Kind(string key) => key switch
    {
        RockRates.Shatter => "a chance to shatter a rock",
        _ => "a chance of more ore",
    };

    /// <summary>The game's name for this bonus, or null.</summary>
    public string? Of(string key) => _names.GetValueOrDefault(key);

    /// <summary>
    /// Reads the names from the page when a bonus is still without one, every few seconds at most: the rows are there
    /// only while the page shows a rock.
    /// </summary>
    public void Read(Node page, MiningActionComponent action, IEnumerable<RockRates> rocks, Action<string> report)
    {
        if (_failed) return;
        bool wanted = false;
        foreach (RockRates rock in rocks)
            foreach (RockRates.Bonus bonus in rock.NotCounted)
                wanted |= !_names.ContainsKey(bonus.Key);
        if (!wanted || !_again.Due()) return;
        try { ReadRows(page, action); }
        catch (Exception e)
        {
            _failed = true;
            report("The game's names of the bonuses on the Mining page could not be read (the game was updated?); the tooltip describes them instead. " + e.Message);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void ReadRows(Node page, MiningActionComponent action)
    {
        if (GameAccess.FindNode<MiningActionStatsView>(page) is not { } list) return;
        // A row is bound to the stats of the rock the list shows; every rock's stats are asked which bonus they are.
        var keys = new Dictionary<ModifierStatNodes, string>(ReferenceEqualityComparer.Instance);
        foreach (MiningRock rock in action.Rocks.Values)
        {
            MiningActionStats stats = rock.Stats;
            if (stats.ShatterChance != null) keys.TryAdd(stats.ShatterChance, RockRates.Shatter);
            if (stats.AdditionalProductQuantityChance != null) keys.TryAdd(stats.AdditionalProductQuantityChance, RockRates.MoreOre);
        }
        void Walk(Node node)
        {
            for (int i = 0; i < node.GetChildCount(); i++)
            {
                Node child = node.GetChild(i);
                if (child.IsQueuedForDeletion()) continue;
                if (child is ModifierStatLabel row)
                {
                    if (Nodes(row) is { } nodes && keys.TryGetValue(nodes, out string? key) &&
                        StringUtils.StripBBCode(row.StatName ?? "").Trim() is { Length: > 0 } name)
                        _names[key] = name;
                }
                else Walk(child);
            }
        }
        Walk(list);
    }
}
