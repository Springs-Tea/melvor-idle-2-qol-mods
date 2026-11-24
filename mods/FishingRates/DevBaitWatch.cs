#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Fishing;
using Godot;
using MelvorModLoader;

namespace FishingRates;

// Development, issue #64: does preserved bait stay in the bank? A file "baitwatch.request" in the mod's folder, holding a
// number of minutes (15 when it is empty), starts a watch that only listens. It opens nothing, clicks nothing, changes
// nothing and never waits for the mouse, so it runs while the player plays, beside the check of Dev.cs or alone.
//   pwsh .claude\codex\check-mod.ps1 -Worktree <worktree> -Mod FishingRates -Request baitwatch.request
//        -Produces baitwatch.done -Text 15 -Seconds 1200 -Out jobs\fishing-bait\check-<n>
// It listens to the game's catches (FishingActionComponent.RecipeFished, SpecialItemFished), its bait events
// (FishingBaitComponent.BaitConsumed, BaitPreserved, SelectedBaitChanged) and the bank's changes of every bait
// (BankInventoryComponent.ItemQuantityChanged), and logs each as one "Dev: baitwatch | ..." line with the bank's count of
// the selected bait at that moment. The events of one game frame are one catch: the bank's count of its bait before the
// catch (as read on the tick before its first event) and after it (on the first tick a second after its last event, or
// at the next catch's first event) make the catch's line, with its events in the order they came.
// When the time is up, or when the character has not been fishing for a minute, it writes "baitwatch.done": one summary
// line (catches, bait used and preserved events, the bank's drop over the watch, every preserved catch with the bank's
// count before and after it) ending in one verdict:
//   PRESERVED BAIT STAYS IN THE BANK              the bank did not go down on any preserved catch;
//   PRESERVED BAIT IS USED UP ANYWAY              it went down on every preserved catch;
//   NOT DECIDED: NO PRESERVED CATCH               no catch raised BaitPreserved;
//   NOT DECIDED: THE PRESERVED CATCHES DISAGREE   it went down on some preserved catches and not on others.
// A second line, also the log's last, says that it stopped listening (read back from the game's events) and that,
// having changed nothing, it put nothing back.
public partial class FishingRatesMod
{
    const string BaitWatchRequest = "baitwatch.request", BaitWatchDone = "baitwatch.done";
    const double BaitWatchMinutes = 15, BaitWatchMostMinutes = 24 * 60;
    // The brief's: the watch ends once the character has not been fishing for a minute.
    const ulong BaitWatchIdle = 60000;
    // A catch is complete on the first tick at least this long after its last event: the game raises a catch's events
    // in one frame, and the next catch comes seconds later. A next catch that comes sooner completes it at once.
    const ulong BaitCatchSettle = 1000;

    // What listening leaves on the game's events, read back when the watch ends.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "RecipeFished")]
    static extern ref EventHandler<RecipeFishedEventArgs>? RecipeFishedHandlers(FishingActionComponent action);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "SpecialItemFished")]
    static extern ref EventHandler<SpecialItemFishedEventArgs>? SpecialItemHandlers(FishingActionComponent action);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "BaitConsumed")]
    static extern ref EventHandler<BaitConsumedEventArgs>? BaitConsumedHandlers(FishingBaitComponent bait);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "BaitPreserved")]
    static extern ref EventHandler<BaitConsumedEventArgs>? BaitPreservedHandlers(FishingBaitComponent bait);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "SelectedBaitChanged")]
    static extern ref EventHandler<ValueChangedEventArgs<Item>>? SelectedBaitHandlers(FishingBaitComponent bait);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "ItemQuantityChanged")]
    static extern ref EventHandler<ItemQuantityChangedEventArgs>? BankChangeHandlers(BankInventoryComponent bank);

    // The events of one game frame, and the bank's count of every bait before and after them.
    sealed class DevBaitCatch
    {
        public int Number, Catches, Specials, Used, Kept;
        public ulong Frame, LastAt;
        public double At;
        public Item? Bait;
        public string Closed = "";
        public readonly Dictionary<Item, int> Before = new(), After = new();
        public readonly List<string> Events = new(), Spots = new();

        // How far the bank's count of the catch's bait went down: 1 for one bait taken, 0 for none.
        public int Drop => Bait != null && Before.TryGetValue(Bait, out int before) && After.TryGetValue(Bait, out int after) ? before - after : 0;

        public string Bank => Bait == null ? "no bait"
            : $"the bank's {Bait.Id} {(Before.TryGetValue(Bait, out int b) ? b : -1)} before, {(After.TryGetValue(Bait, out int a) ? a : -1)} after";

        public string When => $"catch {Number} at {At.ToString("0", CultureInfo.InvariantCulture)} s";
    }

    sealed class DevBaitWatch
    {
        public ulong Since, Until, NotFishingSince;
        public double Minutes;
        public FishingActionComponent Action = null!;
        public FishingBaitComponent Bait = null!;
        public BankInventoryComponent Bank = null!;
        public Item? StartBait;
        public readonly List<Item> Baits = new();
        public Dictionary<Item, int> AtStart = null!, LastTick = null!;
        public EventHandler<RecipeFishedEventArgs> OnFished = null!;
        public EventHandler<SpecialItemFishedEventArgs> OnSpecial = null!;
        public EventHandler<BaitConsumedEventArgs> OnUsed = null!, OnKept = null!;
        public EventHandler<ValueChangedEventArgs<Item>> OnSelected = null!;
        public EventHandler<ItemQuantityChangedEventArgs> OnBank = null!;
        public DevBaitCatch? Open;
        public readonly List<DevBaitCatch> Done = new();
        public int Catches, Specials, UsedEvents, UsedBait, KeptEvents, KeptBait, Offline;
        public readonly HashSet<string> Spots = new();
        public string? Failed;
    }

    DevBaitWatch? _devBait;

    // Every tick (DevRequest): a request starts the watch; a catch whose events are all in is completed; the watch ends
    // when its time is up or the character has not been fishing for a minute.
    void DevBaitWatchTick()
    {
        try
        {
            if (_devBait is not { } w)
            {
                DevBaitWatchStart();
                return;
            }
            if (w.Failed != null)
            {
                DevBaitWatchEnd("failed: " + w.Failed);
                return;
            }
            ulong now = Time.GetTicksMsec();
            if (w.Open is { } open && Engine.GetProcessFrames() != open.Frame && now - open.LastAt >= BaitCatchSettle)
                DevBaitClose(w, DevBaitCounts(w, null), "complete");
            if (w.Open == null) w.LastTick = DevBaitCounts(w, null);
            if (DevFishingNow())
            {
                if (w.NotFishingSince != 0) DevLog("baitwatch | fishing again");
                w.NotFishingSince = 0;
            }
            else if (w.NotFishingSince == 0)
            {
                w.NotFishingSince = now;
                DevLog($"baitwatch | the character is not fishing: the watch ends after {BaitWatchIdle / 1000} s of that");
            }
            if (now >= w.Until) DevBaitWatchEnd($"the {Minutes(w.Minutes)} minutes are up");
            else if (w.NotFishingSince != 0 && now - w.NotFishingSince >= BaitWatchIdle)
                DevBaitWatchEnd($"the character was not fishing for {BaitWatchIdle / 1000} s");
        }
        catch (Exception e)
        {
            try { DevBaitWatchEnd($"failed: {e.GetType().Name}: {e.Message}"); }
            catch (Exception again) { _devBait = null; DevLog("baitwatch | could not end: " + again.Message); }
        }
    }

    static string Minutes(double minutes) => minutes.ToString("0.##", CultureInfo.InvariantCulture);

    void DevBaitWatchStart()
    {
        string path = Path.Combine(_ctx.Directory, BaitWatchRequest);
        if (!File.Exists(path)) return;
        string asked = File.ReadAllText(path).Trim();
        File.Delete(path);
        double minutes = BaitWatchMinutes;
        if (asked.Length > 0)
        {
            if (double.TryParse(asked, NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m > 0 && double.IsFinite(m))
                minutes = Math.Min(m, BaitWatchMostMinutes);
            else DevLog($"baitwatch | the request '{One(asked)}' is not a number of minutes: it watches for {Minutes(BaitWatchMinutes)}");
        }
        if (_bank == null)
        {
            DevBaitWrite("baitwatch | failed before it began: the bank was not found", "baitwatch | nothing was listened to and nothing was changed, so nothing was put back");
            return;
        }
        FishingBaitComponent baits = _fishing!.BaitComponent;
        ulong now = Time.GetTicksMsec();
        var w = new DevBaitWatch
        {
            Since = now, Until = now + (ulong)(minutes * 60000), Minutes = minutes,
            Action = _fishing.Action, Bait = baits, Bank = _bank.Inventory, StartBait = baits.SelectedBait,
        };
        w.Baits.AddRange(baits.BaitItems);
        if (baits.SelectedBait is { } selected && !w.Baits.Contains(selected)) w.Baits.Add(selected);
        w.AtStart = DevBaitCounts(w, null);
        w.LastTick = w.AtStart;
        w.OnFished = (_, e) => DevBaitEvent(w, $"catch {e.Recipe?.Recipe.Id}: {e.Item?.Id} x{e.Quantity}", null, null, c =>
        {
            c.Catches++;
            w.Catches++;
            if (e.Recipe?.Recipe.Id is { } spot) { c.Spots.Add(spot); w.Spots.Add(spot); }
        });
        w.OnSpecial = (_, e) => DevBaitEvent(w, $"special item {e.SpecialItem?.Item?.Id} x{e.SpecialItem?.Quantity}", null, null, c =>
        {
            c.Specials++;
            w.Specials++;
        });
        w.OnUsed = (_, e) => DevBaitEvent(w, $"bait {e.Item?.Id} used x{e.Quantity}", e.Item, null, c =>
        {
            c.Used += e.Quantity;
            w.UsedEvents++;
            w.UsedBait += e.Quantity;
        });
        w.OnKept = (_, e) => DevBaitEvent(w, $"bait {e.Item?.Id} preserved x{e.Quantity}", e.Item, null, c =>
        {
            c.Kept += e.Quantity;
            w.KeptEvents++;
            w.KeptBait += e.Quantity;
        });
        w.OnSelected = (_, e) => DevBaitEvent(w, $"THE SELECTED BAIT CHANGED: {e.OldValue?.Id ?? "none"} -> {e.NewValue?.Id ?? "none"}", null, null, c => { });
        w.OnBank = (_, e) =>
        {
            if (e.Item == null || !w.Baits.Contains(e.Item)) return;
            DevBaitEvent(w, $"the bank's {e.Item.Id} {e.OldValue} -> {e.NewValue}", null, e, c => { });
        };
        w.Action.RecipeFished += w.OnFished;
        w.Action.SpecialItemFished += w.OnSpecial;
        w.Bait.BaitConsumed += w.OnUsed;
        w.Bait.BaitPreserved += w.OnKept;
        w.Bait.SelectedBaitChanged += w.OnSelected;
        w.Bank.ItemQuantityChanged += w.OnBank;
        _devBait = w;
        DevLog($"baitwatch | started for {Minutes(minutes)} minutes, only listening: the game's catches (RecipeFished, SpecialItemFished), its bait " +
            "events (BaitConsumed, BaitPreserved, SelectedBaitChanged) and the bank's changes of every bait (ItemQuantityChanged); it opens, clicks " +
            $"and changes nothing and does not wait for the mouse | fishing now {DevFishingNow()}, the spot fished {w.Action.CurrentRecipe?.Recipe.Id ?? "none"}, " +
            $"the selected bait {w.StartBait?.Id ?? "none"}, preservation {R(baits.Stats?.PreservationChance?.Total.Value ?? double.NaN)} | " +
            $"the bank's bait [{DevBaitWords(w, w.AtStart)}] | it ends when the time is up or after {BaitWatchIdle / 1000} s without fishing");
    }

    // One event of the game. A new frame begins a new catch: the one before is complete, with the bank as it is now (as
    // it was before this event, when the event is the bank's own change).
    void DevBaitEvent(DevBaitWatch w, string what, Item? bait, ItemQuantityChangedEventArgs? change, Action<DevBaitCatch> count)
    {
        if (!ReferenceEquals(_devBait, w) || w.Failed != null) return;
        // An event here must never break the game's catch: anything that goes wrong ends the watch on its next tick.
        try
        {
            // The game catching up after the computer slept is many catches at once: counted, not watched.
            if (GameAccess.IsComputingOffline)
            {
                w.Offline++;
                return;
            }
            ulong frame = Engine.GetProcessFrames(), now = Time.GetTicksMsec();
            if (w.Open is { } previous && previous.Frame != frame) DevBaitClose(w, DevBaitCounts(w, change), "complete at the next event");
            if (w.Open == null)
            {
                var opened = new DevBaitCatch
                {
                    Number = w.Done.Count + 1, Frame = frame, At = (now - w.Since) / 1000.0, Bait = w.Bait.SelectedBait,
                };
                // The bank before it is the one read on the tick before this frame; if it already reads otherwise at
                // this first event without an event of the bank, that is said.
                foreach (var (item, quantity) in w.LastTick) opened.Before[item] = quantity;
                var first = DevBaitCounts(w, change);
                if (w.Baits.Any(b => first[b] != w.LastTick[b]))
                    opened.Events.Add($"THE BANK MOVED WITHOUT AN EVENT before this one: [{DevBaitWords(w, w.LastTick)}] on the tick before, [{DevBaitWords(w, first)}] now");
                w.Open = opened;
            }
            DevBaitCatch c = w.Open;
            c.LastAt = now;
            if (bait != null) c.Bait = bait;
            else if (change != null && c.Bait == null) c.Bait = change.Item;
            count(c);
            c.Events.Add(what);
            Item? selected = w.Bait.SelectedBait;
            DevLog($"baitwatch | catch {c.Number} at {c.At.ToString("0.0", CultureInfo.InvariantCulture)} s, frame {frame} | {what} | the bank's {selected?.Id ?? "(no bait selected)"} now " +
                $"{(selected == null ? "-" : w.Bank.GetQuantity(selected).ToString(CultureInfo.InvariantCulture))}");
        }
        catch (Exception e) { w.Failed = $"{e.GetType().Name}: {e.Message}"; }
    }

    // A catch is complete: its line, with the bank's count of its bait before and after it.
    void DevBaitClose(DevBaitWatch w, Dictionary<Item, int> after, string how)
    {
        if (w.Open is not { } c) return;
        w.Open = null;
        foreach (var (item, quantity) in after) c.After[item] = quantity;
        w.LastTick = after;
        c.Closed = how;
        w.Done.Add(c);
        string kind = c.Catches == 1 ? c.Kept > 0 ? "PRESERVED CATCH" : "catch" : c.Catches == 0 ? "no catch" : $"{c.Catches} CATCHES IN ONE FRAME";
        var others = w.Baits.Where(b => !Equals(b, c.Bait) && c.Before[b] != c.After[b]).Select(b => $"{b.Id} {c.Before[b]} -> {c.After[b]}").ToList();
        DevLog($"baitwatch | {kind} {c.Number} at {c.At.ToString("0.0", CultureInfo.InvariantCulture)} s{(c.Spots.Count > 0 ? " at " + string.Join(", ", c.Spots) : "")} | used {c.Used}, preserved {c.Kept} | " +
            $"{c.Bank}: {(c.Drop == 0 ? "the same" : c.Drop > 0 ? $"down {c.Drop}" : $"UP {-c.Drop}")}" +
            (others.Count > 0 ? $"; ANOTHER BAIT CHANGED TOO: {string.Join(", ", others)}" : "") +
            $" | {how} | in order: [{string.Join(", ", c.Events)}]");
    }

    // The bank's count of every bait now; for the bait of a change of the bank that is under way, its count before it.
    static Dictionary<Item, int> DevBaitCounts(DevBaitWatch w, ItemQuantityChangedEventArgs? change)
    {
        var counts = new Dictionary<Item, int>();
        foreach (Item bait in w.Baits) counts[bait] = change != null && Equals(change.Item, bait) ? change.OldValue : w.Bank.GetQuantity(bait);
        return counts;
    }

    static string DevBaitWords(DevBaitWatch w, Dictionary<Item, int> counts) => string.Join(", ", w.Baits.Select(b => $"{b.Id} {counts[b]}"));

    // The end: the listening is taken off and read back, the last catch completed, the summary and the last line logged and
    // written to baitwatch.done. Nothing is put back: the watch changed nothing.
    void DevBaitWatchEnd(string why)
    {
        if (_devBait is not { } w) return;
        _devBait = null;
        w.Action.RecipeFished -= w.OnFished;
        w.Action.SpecialItemFished -= w.OnSpecial;
        w.Bait.BaitConsumed -= w.OnUsed;
        w.Bait.BaitPreserved -= w.OnKept;
        w.Bait.SelectedBaitChanged -= w.OnSelected;
        w.Bank.ItemQuantityChanged -= w.OnBank;
        string summary;
        try
        {
            if (w.Open != null) DevBaitClose(w, DevBaitCounts(w, null), "complete at the end of the watch");
            summary = DevBaitSummary(w, why);
        }
        catch (Exception e) { summary = $"baitwatch | over: {why}; the summary could not be made: {e.GetType().Name}: {e.Message}"; }
        DevBaitWrite(summary, $"baitwatch | stopped listening to the game: {DevBaitListening(w)}; it opened, clicked and changed nothing, so it put nothing back");
    }

    void DevBaitWrite(string summary, string last)
    {
        DevLog(summary);
        DevLog(last);
        try { File.WriteAllText(Path.Combine(_ctx.Directory, BaitWatchDone), One(summary) + "\n" + One(last)); }
        catch (Exception e) { DevLog($"baitwatch | {BaitWatchDone} could not be written: {e.Message}"); }
    }

    // Read back from the game's own events that none of the watch's handlers is on them any more.
    static string DevBaitListening(DevBaitWatch w)
    {
        try
        {
            static bool On(Delegate? handlers, Delegate ours) => handlers?.GetInvocationList().Contains(ours) == true;
            var still = new List<string>();
            if (On(RecipeFishedHandlers(w.Action), w.OnFished)) still.Add("RecipeFished");
            if (On(SpecialItemHandlers(w.Action), w.OnSpecial)) still.Add("SpecialItemFished");
            if (On(BaitConsumedHandlers(w.Bait), w.OnUsed)) still.Add("BaitConsumed");
            if (On(BaitPreservedHandlers(w.Bait), w.OnKept)) still.Add("BaitPreserved");
            if (On(SelectedBaitHandlers(w.Bait), w.OnSelected)) still.Add("SelectedBaitChanged");
            if (On(BankChangeHandlers(w.Bank), w.OnBank)) still.Add("the bank's ItemQuantityChanged");
            return still.Count == 0 ? "none of its 6 handlers is on the game's events any more" : $"STILL LISTENING TO {string.Join(", ", still)}";
        }
        catch (Exception e) { return $"its 6 handlers taken off, which could not be read back ({e.GetType().Name}: {e.Message})"; }
    }

    // One line: what came, the bank's drop, every preserved catch with the bank before and after it, and the verdict.
    string DevBaitSummary(DevBaitWatch w, string why)
    {
        double minutes = (Time.GetTicksMsec() - w.Since) / 60000.0;
        var end = DevBaitCounts(w, null);
        var single = w.Done.Where(c => c.Catches == 1).ToList();
        var preserved = single.Where(c => c.Kept > 0).ToList();
        var plain = single.Where(c => c.Kept == 0).ToList();
        var odd = plain.Where(c => c.Drop != 1).ToList();
        int several = w.Done.Count(c => c.Catches > 1), outside = w.Done.Count(c => c.Catches == 0 && w.Baits.Any(b => c.Before[b] != c.After[b]));
        // Bait events in a frame without a catch would mean the frames do not hold whole catches, and the verdict
        // below would miss preserved ones.
        int apart = w.Done.Count(c => c.Catches == 0 && c.Used + c.Kept > 0);
        int stays = preserved.Count(c => c.Drop <= 0), usedUp = preserved.Count(c => c.Drop > 0);
        string verdict = preserved.Count == 0 ? "NOT DECIDED: NO PRESERVED CATCH" : usedUp == 0 ? "PRESERVED BAIT STAYS IN THE BANK"
            : stays == 0 ? "PRESERVED BAIT IS USED UP ANYWAY" : "NOT DECIDED: THE PRESERVED CATCHES DISAGREE";
        var drops = w.Baits.Where(b => w.AtStart[b] != end[b] || Equals(b, w.StartBait))
            .Select(b => $"{b.Id} {w.AtStart[b]} -> {end[b]}, down {w.AtStart[b] - end[b]}").ToList();
        static string Some(IEnumerable<string> lines, int most)
        {
            var list = lines.ToList();
            return string.Join(", ", list.Take(most)) + (list.Count > most ? $" and {list.Count - most} more" : "");
        }
        return $"baitwatch | over after {minutes.ToString("0.0", CultureInfo.InvariantCulture)} minutes: {why} | {w.Catches} catches at [{string.Join(", ", w.Spots)}], {w.Specials} special items" +
            (w.Offline > 0 ? $", {w.Offline} events while the game caught up offline left out" : "") +
            $" | bait used events {w.UsedEvents} ({w.UsedBait} bait), bait preserved events {w.KeptEvents} ({w.KeptBait} bait)" +
            $" | the bank's drop over the watch: {(drops.Count == 0 ? "no bait" : string.Join(", ", drops))}; the used events add up to {w.UsedBait}, " +
            $"less the preserved ones to {w.UsedBait - w.KeptBait}" +
            $" | catches without a preserved event {plain.Count}, the bank down by one on {plain.Count - odd.Count}" +
            (odd.Count > 0 ? $" (NOT ON {Some(odd.Select(c => $"{c.When}, {c.Bank}"), 5)})" : "") +
            (several > 0 ? $" | {several} frames with more than one catch, left out" : "") +
            (apart > 0 ? $" | BAIT EVENTS APART FROM A CATCH in {apart} frames: the verdict may miss preserved catches" : "") +
            (outside > 0 ? $" | the bank's bait changed {outside} times outside a catch (in the lines 'no catch')" : "") +
            $" | preserved catches {preserved.Count}, the bank down on {usedUp}: [{Some(preserved.Select(c => $"{c.When}, {c.Bank}"), 40)}]" +
            $" | {verdict}";
    }
}
#endif
