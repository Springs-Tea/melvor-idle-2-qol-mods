#if DEVTOOLS
using System;
using System.Linq;
using System.Threading;
using GamesByMalcs.Melvor2.Audio;
using GamesByMalcs.Melvor2.Notifications;

namespace CombatSim;

// Created in Start on the game's thread, before any workers load a copy.
sealed class MusicScan
{
    readonly Action<string> _log;
    readonly int _mainThread = Environment.CurrentManagedThreadId;
    readonly object _gate = new();
    string _before = "";
    bool _listening;
    int _events, _fromOtherThreads, _copies, _cut, _loggedSubscribers;
    static int s_loggedUnavailable;

    public MusicScan(Action<string> log)
    {
        _log = log;
        try { Subscribe(); }
        catch (Exception e) { Unavailable(e); }
    }

    void Subscribe()
    {
        lock (_gate)
        {
            _before = TrackName();
            AudioManager.MusicTrackChanged += TrackChanged;
            _listening = true;
        }
    }

    static string TrackName() => AudioManager.CurrentTrack?.TrackName ?? "(none)";

    void TrackChanged(object? sender, MusicNotificationEventArgs e)
    {
        lock (_gate)
        {
            if (!_listening) return;
            _events++;
            if (Environment.CurrentManagedThreadId != _mainThread) _fromOtherThreads++;
        }
    }

    public void CountCopy() => Interlocked.Increment(ref _copies);
    public void CountCut() => Interlocked.Increment(ref _cut);

    public void LogSubscribers(Delegate[] handlers, object action)
    {
        if (Interlocked.Exchange(ref _loggedSubscribers, 1) != 0) return;
        var names = handlers.Select(h => $"{h.Method.DeclaringType?.Name}.{h.Method.Name}" +
            (ReferenceEquals(h.Target, action) ? " (copy's action)" : ""));
        _log("Dev: StateChanged subscribers on the first inspected copy: " +
            (handlers.Length == 0 ? "(none)" : string.Join(", ", names)));
    }

    public void Finish()
    {
        try { if (_listening) Unsubscribe(); }
        catch (Exception e) { Unavailable(e); }
        _log($"Dev: copies cut off from music: {_cut} of {_copies}");
    }

    void Unsubscribe()
    {
        lock (_gate)
        {
            _listening = false;
            AudioManager.MusicTrackChanged -= TrackChanged;
            _log($"Dev: music events during the scan: {_events}, from other threads: {_fromOtherThreads}, " +
                $"track before: {_before}, track after: {TrackName()}");
        }
    }

    void Unavailable(Exception e)
    {
        if (Interlocked.Exchange(ref s_loggedUnavailable, 1) == 0)
            _log("Dev: music event measurement unavailable: " + e.Message);
    }
}
#endif
