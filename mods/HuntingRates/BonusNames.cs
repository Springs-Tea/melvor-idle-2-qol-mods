using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Entities.Hunting;
using GamesByMalcs.Melvor2.InfoLabels;
using GamesByMalcs.Melvor2.Stats.Nodes;
using GamesByMalcs.Melvor2.Utils;
using Godot;
using MelvorMods.Shared;

namespace HuntingRates;

/// <summary>
/// The game's own names of an animal's chances that the tooltip names (the chance to skip a tracking step, to reattempt
/// a trap, to double the output, to preserve): read from the Hunting page's lists of an animal's bonuses
/// (HuntingActionStatsView, in the panel of the selected animal and of the hunt in progress), where each row
/// (ModifierStatLabel) shows one stat under the name the game gives it. Copied from Woodcutting Rates
/// (mods/WoodcuttingRates/BonusNames.cs), itself Fishing Rates'. Until a name has been read (a list is filled once the
/// page shows an animal), or when the game no longer has these rows, the tooltip says only what kind of chance it is.
/// </summary>
sealed class BonusNames
{
    // Which stat a row of the game's list shows. Private: one that is gone after a game update throws when it is first
    // used, and the names are then given up for good, with one line in the log.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Stats")]
    static extern ModifierStatNodes? Nodes(ModifierStatLabel label);

    public const string Skip = "SkipTrackingStepChance", Reattempt = "TrappingReattemptChance", Doubling = "DoublingChance",
        Preservation = "PreservationChance";
    static readonly string[] Keys = [Skip, Reattempt, Doubling, Preservation];

    readonly Dictionary<string, string> _names = new();
    readonly Tempo _again = new(5000);
    bool _failed;

    /// <summary>What a chance is, when the game's name for it has not been read.</summary>
    public static string Kind(string key) => key switch
    {
        Skip => "Chance to skip a tracking step",
        Reattempt => "Chance to try a trap again",
        Doubling => "Doubling chance",
        _ => "Preservation chance",
    };

    /// <summary>The game's name for this chance, or what kind of chance it is while that has not been read.</summary>
    public string Of(string key) => _names.GetValueOrDefault(key) ?? Kind(key);

    /// <summary>The game's name for this chance when it has been read, or null.</summary>
    public string? Read(string key) => _names.GetValueOrDefault(key);

    /// <summary>
    /// Reads the names from the page when a chance is still without one, every few seconds at most: the rows are there
    /// only once a list of the page has been filled.
    /// </summary>
    public void Read(Node page, IEnumerable<AnimalRates> animals, Action<string> report)
    {
        if (_failed || _names.Count == Keys.Length || !_again.Due()) return;
        try { ReadRows(page, animals); }
        catch (Exception e)
        {
            _failed = true;
            report("The game's names of an animal's chances on the Hunting page could not be read (the game was updated?); the tooltip describes them instead. " + e.Message);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void ReadRows(Node page, IEnumerable<AnimalRates> animals)
    {
        // A row is bound to the stats of one animal; every listed animal's stats are asked which chance they are.
        var keys = new Dictionary<ModifierStatNodes, string>(ReferenceEqualityComparer.Instance);
        foreach (AnimalRates animal in animals)
        {
            HuntingActionStats stats = animal.Active.Stats;
            if (stats.SkipTrackingStepChance != null) keys.TryAdd(stats.SkipTrackingStepChance, Skip);
            if (stats.TrappingReattemptChance != null) keys.TryAdd(stats.TrappingReattemptChance, Reattempt);
            if (stats.DoublingChance != null) keys.TryAdd(stats.DoublingChance, Doubling);
            if (stats.PreservationChance != null) keys.TryAdd(stats.PreservationChance, Preservation);
        }
        // Every list of an animal's bonuses on the page, and in each its rows; a row's own parts are not looked through.
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
                else Walk(child, inList || child is HuntingActionStatsView);
            }
        }
        Walk(page, false);
    }
}
