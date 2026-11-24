using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Entities.Firemaking;
using GamesByMalcs.Melvor2.InfoLabels;
using GamesByMalcs.Melvor2.Stats.Nodes;
using GamesByMalcs.Melvor2.Utils;
using Godot;
using MelvorMods.Shared;

namespace FiremakingRates;

/// <summary>
/// The game's own names of a log's doubling and preservation chances, which the tooltip names where it says what they
/// are counted in: read from the Firemaking page's list of the selected log's bonuses (FiremakingActionStatsView), where
/// each row (ModifierStatLabel) shows one stat under the name the game gives it. Copied from Woodcutting Rates
/// (mods/WoodcuttingRates/BonusNames.cs), itself Fishing Rates' (mods/FishingRates/BonusNames.cs), which reads its rows
/// the way Skill Bonuses does (mods/SkillBonuses/SkillStats.cs). Until a name has been read (the page's list is filled
/// once it shows a log with that chance), or when the game no longer has these rows, the tooltip says what kind of chance
/// it is.
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
    public static string Kind(string key) => key == LogRates.Doubling ? "The doubling chance" : "The preservation chance";

    /// <summary>The game's name for this chance, or null.</summary>
    public string? Of(string key) => _names.GetValueOrDefault(key);

    /// <summary>
    /// Reads the names from the page when a chance is still without one, every few seconds at most: the rows are there
    /// only once the page's list has been filled.
    /// </summary>
    public void Read(Node page, IEnumerable<LogRates> logs, Action<string> report)
    {
        if (_failed) return;
        bool wanted = false;
        foreach (LogRates log in logs)
            foreach (LogRates.Bonus bonus in log.Chances)
                wanted |= !_names.ContainsKey(bonus.Key);
        if (!wanted || !_again.Due()) return;
        try { ReadRows(page, logs); }
        catch (Exception e)
        {
            _failed = true;
            report("The game's names of a log's chances on the Firemaking page could not be read (the game was updated?); the tooltip describes them instead. " + e.Message);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void ReadRows(Node page, IEnumerable<LogRates> logs)
    {
        // A row is bound to the stats of one log; every listed log's stats are asked which chance they are.
        var keys = new Dictionary<ModifierStatNodes, string>(ReferenceEqualityComparer.Instance);
        foreach (LogRates log in logs)
            foreach (LogRates.Bonus bonus in log.Chances)
                keys.TryAdd(bonus.Nodes, bonus.Key);
        // Every list of a log's bonuses on the page, and in each its rows; a row's own parts are not looked through.
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
                else Walk(child, inList || child is FiremakingActionStatsView);
            }
        }
        Walk(page, false);
    }
}
