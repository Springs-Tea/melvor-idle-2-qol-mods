using System;
using System.Collections.Generic;

namespace CombatGoals;

// A value looked at once a second (a combat skill's XP, or progress towards a skilling stop) and its average
// rate over a recent period.
sealed class RateHistory
{
    readonly Queue<(ulong Ms, double Value)> _samples = new();
    double _latest;
    ulong _lastMs;

    public void Clear() => _samples.Clear();

    public void Add(ulong now, double value, double longestSeconds)
    {
        _latest = value;
        _lastMs = now;
        _samples.Enqueue((now, value));
        while (now - _samples.Peek().Ms > longestSeconds * 1000)
            _samples.Dequeue();
    }

    public double PerSecond(ulong now, double period)
    {
        foreach (var first in _samples)
        {
            if (now - first.Ms > period * 1000) continue;
            // Rewards arrive in bursts. Give a new measurement at least ten seconds to settle.
            double seconds = (_lastMs - first.Ms) / 1000.0;
            return seconds < 10 ? 0 : Math.Max(0, (_latest - first.Value) / seconds);
        }
        return 0;
    }
}
