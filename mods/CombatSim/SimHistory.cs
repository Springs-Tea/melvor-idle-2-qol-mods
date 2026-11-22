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
    }

    /// <summary>Keeps the scan, and drops the oldest ones beyond <see cref="Kept"/>.</summary>
    public void Add(Scan scan)
    {
        _byEnemy = null;
        Scans.Insert(0, scan);
        var dropped = Scans.Skip(Kept).ToList();
        if (dropped.Count > 0) Scans.RemoveRange(Kept, dropped.Count);
        if (_folder is not { } folder) return;
        // Off the game's thread: a scan of every area is a large file.
        Task.Run(() =>
        {
            lock (_disk)
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    ModFiles.WriteWhole(FileOf(folder, scan), JsonSerializer.Serialize(scan, Json));
                    foreach (Scan old in dropped) File.Delete(FileOf(folder, old));
                }
                catch (Exception e) { _log("The scan could not be saved: " + e.Message); }
            }
        });
    }

    public void Clear()
    {
        _byEnemy = null;
        var all = Scans.ToList();
        Scans.Clear();
        if (_folder is not { } folder) return;
        Task.Run(() =>
        {
            lock (_disk)
            {
                try { foreach (Scan scan in all) File.Delete(FileOf(folder, scan)); }
                catch (Exception e) { _log("The earlier scans could not be removed: " + e.Message); }
            }
        });
    }

    static string FileOf(string folder, Scan scan) => Path.Combine(folder, scan.Id + ".json");

    static string SafeName(string name)
    {
        char[] bad = Path.GetInvalidFileNameChars();
        string safe = new(name.Select(c => bad.Contains(c) || c == '.' ? '_' : c).ToArray());
        return safe.Trim() == "" ? "_" : safe.Trim();
    }
}
