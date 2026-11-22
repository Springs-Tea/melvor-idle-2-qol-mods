using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Core.TickTimer;
using GamesByMalcs.Melvor2.Entities.Astrology;
using GamesByMalcs.Melvor2.Modifiers;
using GamesByMalcs.Melvor2.Utils;

namespace AstrologyOverview;

internal enum UnlockStatus { Ready, Study, Waiting, Locked, Complete }

/// <summary>What one row says about a constellation, read from the game's own state for it.</summary>
internal sealed record ConstellationRow(AstrologyConstellation Constellation, UnlockStatus Status,
    int MinorChoices, int MajorChoices, int? KnowledgeGoal, int? MinorNodesToMajor, double Milliseconds)
{
    public static bool Locked(AstrologyConstellation c) =>
        c.Requirements?.AreMet == false || c.SecondaryRequirements?.AreMet == false;

    public static ConstellationRow Read(AstrologyConstellation c, AstrologyActionComponent action)
    {
        if (!action.ActiveConstellations.TryGetValue(c, out var active) || Locked(c))
            return new(c, UnlockStatus.Locked, 0, 0, null, null, double.PositiveInfinity);

        // Choices earned and not spent yet: the game's running count of nodes made available (it only grows, as
        // knowledge does) less the nodes unlocked. These are the numbers on the constellation screen's
        // AvailableMinorNodes and AvailableMajorNodes labels; Dev.cs logs both side by side for every
        // constellation it opens.
        bool minorDone = active.MinorNodeUnlockCount >= c.MinorNodes.Count, majorDone = active.MajorNodeUnlockCount >= c.MajorNodes.Count;
        int minor = minorDone ? 0 : Math.Max(0, active.AvailableMinorNodeCount - active.MinorNodeUnlockCount);
        int major = majorDone ? 0 : Math.Max(0, active.AvailableMajorNodeCount - active.MajorNodeUnlockCount);

        int? goal = minorDone ? null : NextKnowledgeGoal(c, active);
        // One major node for every MinorNodesPerMajorNode minor nodes unlocked, while major nodes are left to be
        // made available. Worded with the game's own s_minorNodesForMajorNode, and compared by Dev.cs with the
        // constellation screen's NextMajorNodeCost label.
        int perMajor = c.MinorNodesPerMajorNode;
        int? toMajor = null;
        if (perMajor > 0 && active.AvailableMajorNodeCount < c.MajorNodes.Count)
        {
            int left = perMajor - active.MinorNodeUnlockCount % perMajor;
            if (active.MinorNodeUnlockCount + left <= c.MinorNodes.Count) toMajor = left;
        }

        double ms = goal > active.Knowledge ? StudyTime(active, action, goal.Value) : double.PositiveInfinity;
        UnlockStatus status;
        if (minor > 0 || major > 0) status = UnlockStatus.Ready;
        else if (minorDone && majorDone) status = UnlockStatus.Complete;
        else status = double.IsFinite(ms) ? UnlockStatus.Study : UnlockStatus.Waiting;
        return new(c, status, minor, major, goal, toMajor, ms);
    }

    /// <summary>
    /// The knowledge of the next minor node unlock: the entry of the constellation's own MinorNodeUnlockKnowledge
    /// table after the ones its knowledge has reached, counted by the game's ComputeAvailableMinorNodeCount. It is
    /// the number on the constellation screen's NextMinorNodeCost label ("Next unlock at ..."); Dev.cs logs the
    /// two next to each other for every constellation it opens.
    /// </summary>
    static int? NextKnowledgeGoal(AstrologyConstellation c, ActiveAstrologyConstellation active)
    {
        int reached = c.ComputeAvailableMinorNodeCount(active.Knowledge);
        return reached >= 0 && reached < c.MinorNodeUnlockKnowledge.Count ? c.MinorNodeUnlockKnowledge[reached] : null;
    }

    // Whole studies at the knowledge one study gives now, each as long as the game's interval in whole ticks; the
    // one being studied first finishes the study that is running.
    static double StudyTime(ActiveAstrologyConstellation active, AstrologyActionComponent action, int goal)
    {
        double gain = active.Stats.Knowledge.Value, interval = active.Stats.Interval.Value;
        if (!double.IsFinite(gain) || gain <= 0 || !double.IsFinite(interval) || interval <= 0)
            return double.PositiveInfinity;
        double cycle = TickTimer.TicksToMS(TickTimer.MSToTicks(interval));
        if (cycle <= 0) return double.PositiveInfinity;
        double studies = Math.Ceiling((goal - active.Knowledge) / gain);
        double first = ReferenceEquals(active, action.StudiedConstellation) && action.ActionTimer.IsRunning
            ? TickTimer.TicksToMS(action.ActionTimer.TicksLeft) : cycle;
        double ms = first + (studies - 1) * cycle;
        return ms < TimeSpan.MaxValue.TotalMilliseconds ? ms : double.PositiveInfinity;
    }

    /// <summary>The study time in the game's own short format ("6h 28m", "4m 44s").</summary>
    public string Time() => Status == UnlockStatus.Study
        ? TimeFormatUtils.FormatMostSignificantUnits(TimeSpan.FromMilliseconds(Milliseconds), 2, TimeUnits.Seconds, true, true, false)
        : "";

    /// <summary>"Minor Nodes: 1 node available to unlock", with the game's tab name and its own words.</summary>
    public string Ready(GameWords words)
    {
        var lines = new List<string>();
        if (MinorChoices > 0) lines.Add(GameWords.Lead(words.Minor, Native.Available(null!).Format(MinorChoices, MinorChoices)));
        if (MajorChoices > 0) lines.Add(GameWords.Lead(words.Major, Native.Available(null!).Format(MajorChoices, MajorChoices)));
        return string.Join("\n", lines);
    }

    public string MajorCost => MinorNodesToMajor is int n && Status is not (UnlockStatus.Locked or UnlockStatus.Complete)
        ? StringUtils.StripBBCode(Native.MajorCostWords(null!).Format(n, n)) : "";

    /// <summary>The game's descriptions of a set of bonuses, on one line.</summary>
    public static string Bonuses(ModifierValues? values, GameWords words)
    {
        if (values == null) return "";
        string WithCondition(string text, Condition? condition, bool enemy) =>
            (enemy ? GameWords.Lead(words.Enemy, "") : "") + text +
            (condition == null ? "" : " · " + condition.GetDescription(false, false));
        var modifiers = values.Global.Cast<IModifierValue>().Concat(values.Component);
        var text = modifiers.Select(v => WithCondition(v.GetDescription(1), v.Condition, v.IsEnemy))
            .Concat(values.CombatEffect.Select(v => WithCondition(v.Modifier.Trigger.Description + " · " +
                v.Modifier.Effect.GetDescription(v.Modifier.AppliesTo, v.Chance), v.Condition, v.IsEnemy)));
        return string.Join(", ", text.Select(StringUtils.StripBBCode));
    }
}
