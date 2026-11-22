using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MelvorMods.Shared;

namespace CombatSim;

/// <summary>
/// The last scans of a character, kept in the mod's folder (history/&lt;character&gt;/&lt;scan&gt;.json) so they
/// can be looked at again after the game was closed, without simulating anything.
/// </summary>
sealed class SimHistory
{
    public const int Kept = 10;

    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    readonly string _root;
    readonly Action<string> _log;
    readonly object _disk = new();
    // The writes and deletes, one after another in the order they were asked for, so a later one never runs first.
    Task _writing = Task.CompletedTask;
    string? _folder;

    /// <summary>Newest first.</summary>
    public readonly List<Scan> Scans = new();

    // For the enemy cards: the results of each enemy of an area, newest scan first and, within a scan, the
    // stance it ranked best first. Made on first use after the scans changed.
    Dictionary<(string Area, string Enemy), List<(Scan Scan, SimResult Result)>>? _byEnemy;

    /// <summary>
    /// The answer for an enemy's card: the newest result made with this setup, or else the newest result of
    /// all (<c>Same</c> false). Failed fights and results without the card's readings do not count.
    /// </summary>
    public (Scan Scan, SimResult Result, bool Same)? ForCard(string areaId, string enemyId, string setup)
    {
        if (_byEnemy == null)
        {
            _byEnemy = new();
            foreach (Scan scan in Scans)
                foreach (SimResult r in scan.Results.Where(x => !x.Failed && x.Setup != null && x.Target.EnemyId != null).OrderByDescending(scan.Rank))
                {
                    var key = (r.Target.AreaId, r.Target.EnemyId!);
                    if (!_byEnemy.TryGetValue(key, out var list)) _byEnemy[key] = list = new();
                    list.Add((scan, r));
                }
        }
        if (!_byEnemy.TryGetValue((areaId, enemyId), out var results)) return null;
        foreach (var (scan, result) in results)
            if (result.Setup == setup) return (scan, result, true);
        return (results[0].Scan, results[0].Result, false);
    }

    public SimHistory(string modDirectory, Action<string> log)
    {
        _root = Path.Combine(modDirectory, "history");
        _log = log;
    }

    /// <summary>Reads what is on disk for this character. A file that cannot be read is skipped.</summary>
    public void Open(string character)
    {
        _byEnemy = null;
        Scans.Clear();
        _folder = Path.Combine(_root, SafeName(character));
        try
        {
            if (!Directory.Exists(_folder)) return;
            foreach (string file in Directory.GetFiles(_folder, "*.json"))
            {
                try
                {
                    // The id becomes a file name when the scan is dropped: only the digits this mod writes.
                    if (JsonSerializer.Deserialize<Scan>(File.ReadAllText(file), Json) is { Results.Count: > 0 } scan
                        && scan.Id.Length is > 0 and <= 20 && scan.Id.All(char.IsAsciiDigit) && scan.Results.All(r => r.Target != null))
                        Scans.Add(scan);
                }
                catch (Exception e) { _log($"An earlier scan could not be read ({Path.GetFileName(file)}): {e.Message}"); }
            }
            Scans.Sort((a, b) => b.At.CompareTo(a.At));
        }
        catch (Exception e) { _log("Earlier scans could not be read: " + e.Message); }
    }

    public void Close()
    {
        _byEnemy = null;
        Scans.Clear();
        _folder = null;
#if DEVTOOLS
        _devAdded = null;
#endif
    }

    /// <summary>Keeps the scan, and drops the oldest ones beyond <see cref="Kept"/>.</summary>
    public void Add(Scan scan)
    {
        _byEnemy = null;
        Scans.Insert(0, scan);
#if DEVTOOLS
        _devAdded?.Insert(0, scan);
#endif
        var dropped = Scans.Skip(Kept).ToList();
        if (dropped.Count > 0) Scans.RemoveRange(Kept, dropped.Count);
        if (_folder is not { } folder) return;
        // Off the game's thread: a scan of every area is a large file.
        OnDisk(() =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                ModFiles.WriteWhole(FileOf(folder, scan), JsonSerializer.Serialize(scan, Json));
                foreach (Scan old in dropped) File.Delete(FileOf(folder, old));
            }
            catch (Exception e) { _log("The scan could not be saved: " + e.Message); }
        });
    }

    public void Clear()
    {
        _byEnemy = null;
        var all = Scans.ToList();
        Scans.Clear();
#if DEVTOOLS
        // What was cleared, the player's scans included, is gone for good: the put back brings none of it back.
        if (_devAdded != null) { _devAdded.Clear(); _devCleared = true; }
#endif
        if (_folder is not { } folder) return;
        OnDisk(() =>
        {
            try { foreach (Scan scan in all) File.Delete(FileOf(folder, scan)); }
            catch (Exception e) { _log("The earlier scans could not be removed: " + e.Message); }
        });
    }

    // Off the game's thread, after everything asked for before it.
    void OnDisk(Action work)
    {
        lock (_disk) _writing = _writing.ContinueWith(_ => work(), TaskScheduler.Default);
    }

#if DEVTOOLS
    // Development, between DevKeep and DevPutBack: every scan added, newest first, and whether History was cleared.
    List<Scan>? _devAdded;
    bool _devCleared;

    /// <summary>Development: the scans as they are now, to put back with <see cref="DevPutBack"/>.</summary>
    public (string? Folder, List<Scan> Scans) DevKeep()
    {
        _devAdded = new();
        _devCleared = false;
        return (_folder, Scans.ToList());
    }

    /// <summary>
    /// Development: History as it would be had the request made no scan. Only the scans the request's own runs made
    /// (<paramref name="requestMade"/>, by id) are taken out, in memory and on disk. A scan the player made meanwhile,
    /// also one that was running when the request began, stays; so does what his scan pushed out of the ten. What the
    /// request's scans pushed out is written back. What was done, for the log.
    /// </summary>
    public string DevPutBack((string? Folder, List<Scan> Scans) kept, IReadOnlyCollection<string> requestMade)
    {
        List<Scan> added = _devAdded ?? new();
        bool cleared = _devCleared;
        _devAdded = null;
        if (kept.Folder != _folder) return "History: left alone, it is another character's now";
        var players = added.Where(s => !requestMade.Contains(s.Id)).ToList();
        // The player's scans of meanwhile, newest first, then those from before unless he cleared them: the ten newest.
        var wanted = (cleared ? players : players.Concat(kept.Scans)).DistinctBy(s => s.Id).Take(Kept).ToList();
        var takeOut = Scans.Where(s => requestMade.Contains(s.Id)).ToList();
        var writeBack = wanted.Where(s => Scans.All(now => now.Id != s.Id)).ToList();
        var left = players.Where(s => Scans.Any(now => now.Id == s.Id)).ToList();
        string Ids(List<Scan> scans) => scans.Count == 0 ? "" : $" ({string.Join(", ", scans.Select(s => s.Id))})";
        string Said() =>
            $"History: took out the {takeOut.Count} scans the request made{Ids(takeOut)}; " +
            $"left in place the {left.Count} the player made meanwhile{Ids(left)}; " +
            $"wrote back the {writeBack.Count} the request's scans had pushed out{Ids(writeBack)}" +
            (cleared ? "; the player cleared History meanwhile, so nothing from before came back" : "") +
            $"; {Scans.Count} scans" +
            (left.Count == 0 && !cleared && Scans.Select(s => s.Id).SequenceEqual(kept.Scans.Select(s => s.Id)) ? ", as before" : "");
        if (takeOut.Count == 0 && writeBack.Count == 0) return Said();
        _byEnemy = null;
        Scans.Clear();
        Scans.AddRange(wanted);
        if (_folder is { } folder)
            OnDisk(() =>
            {
                try
                {
                    foreach (Scan scan in takeOut)
                    {
                        // Its size first, for the check of how much the ranges add to a file.
                        var file = new FileInfo(FileOf(folder, scan));
                        if (file.Exists)
                            _log($"Dev: history: took out {file.Name}, {file.Length:N0} bytes for {scan.Results.Count} results, {file.Length / Math.Max(1, scan.Results.Count):N0} a result.");
                        file.Delete();
                    }
                    foreach (Scan scan in writeBack) ModFiles.WriteWhole(FileOf(folder, scan), JsonSerializer.Serialize(scan, Json));
                }
                catch (Exception e) { _log("Dev: the history could not be put back: " + e.Message); }
            });
        return Said();
    }
#endif

    static string FileOf(string folder, Scan scan) => Path.Combine(folder, scan.Id + ".json");

    static string SafeName(string name)
    {
        char[] bad = Path.GetInvalidFileNameChars();
        string safe = new(name.Select(c => bad.Contains(c) || c == '.' ? '_' : c).ToArray());
        return safe.Trim() == "" ? "_" : safe.Trim();
    }
}
