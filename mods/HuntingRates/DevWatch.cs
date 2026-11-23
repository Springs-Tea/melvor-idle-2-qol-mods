#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Hunting;
using Godot;
using MelvorModLoader;

namespace HuntingRates;

// Development: a file "huntingwatch.request" in the mod's folder starts a silent watch of the character's hunting, and
// "huntingwatch.done" says how it ended. The file holds a number of minutes (15 when it is empty), which is logged. It
// opens nothing, clicks nothing, does not wait on the mouse and changes nothing: it only listens to the game's own events,
// as Woodcutting Rates' watch listens to its hits (mods/WoodcuttingRates/DevWatch.cs): every animal's StateChanged,
// TracksChanged, CurrentStepChanged, RequiredStepsChanged, TrapFailCountChanged, AnimalTrapped and AnimalEscaped; the
// action's CurrentTrapChanged, CurrentAnimalChanged and ItemAdded; the Hunting XP (ExperienceComponent.XpChanged) and the
// hunted animal's journal XP (HuntingAnimalJournalEntry.Experience.XpChanged). The items, traps and bait are the bank's
// counts of every item an animal can give or use, read after each frame with an event and four times a second between,
// as Woodcutting Rates' watch reads them (the action's ItemAdded was never raised there; here it is logged beside the
// counts as a witness). If the character is not hunting when it starts, it says so in one line and ends. It runs alone:
// a light check sent meanwhile waits for it to end.
//   pwsh .claude\codex\check-mod.ps1 -Worktree <worktree> -Mod HuntingRates -Request huntingwatch.request -Produces huntingwatch.done -Text 15 -Seconds 1200 -Out <folder>
// Every frame with an event is one "Dev: hunt" line, with the time since the line before: the animal, its state before
// and after, its tracks, steps and trap fails, a catch or an escape, the XP and the journal XP, the items by the bank's
// counts. It ends when the minutes are up or after two minutes without an event of a hunt, with one line per question
// for every animal hunted, each with a verdict in capitals; NOT DECIDED below MostCatches catches. The questions are the
// links of a catch the window's hour rests on (AnimalRates.ChainRule, UseRule, the XP of a catch): how many surveys a
// hunt takes, what a failed tracking step costs, what a skipped step does, whether a failed trap is tried again, what
// an escape leads to, what the XP and the journal XP come with, the XP of a catch against the page's range, when traps
// and bait are used up, the items of a catch against ours, and the time of a catch against ours by every reading of the
// chain. Then the game's own tracker (HuntingEntity.Tracker) over its periods beside ours. Its last line says it stopped
// listening and changed nothing. Every listener runs inside a try, and all are taken off when it ends, on Stop (with the
// done file, DevStopped) and on another character (DevUnwatchAll).
public partial class HuntingRatesMod
{
    const string WatchRequestFile = "huntingwatch.request", WatchDoneFile = "huntingwatch.done";
    // Two minutes without an event of a hunt end the watch (the brief: a hunt's steps take seconds, a survey can take long).
    const ulong WatchIdle = 120000;
    // Fewer catches than this decide nothing about the rates of a hunt (Woodcutting Rates' floor of about 30 was for hits;
    // a catch takes tens of seconds, so fifteen minutes give tens of catches at most).
    const int MostCatches = 10;
    const ulong BankReadEvery = 250;

    // What the game did in one frame.
    sealed class DevMoment
    {
        public long Frame;
        public ulong At;
        public ActiveHuntingAnimal? Animal;
        public readonly List<string> Order = new();
        public (HuntState From, HuntState To)? State;
        public readonly List<(int From, int To)> Steps = new();
        public int? RequiredSteps, Tracks, TrapFails;
        public bool Trapped, Escaped;
        public double Xp, JournalXp;
        public int XpChanges, JournalXpChanges;
        public readonly List<(Item Item, int Quantity)> Items = new();
        public readonly List<(Item Item, int Quantity)> Added = new();
        public readonly List<string> Other = new();
    }

    // One hunt: from a survey's start (or the watch's) to a catch or an escape.
    sealed class DevHuntSeen
    {
        public ActiveHuntingAnimal Animal = null!;
        public ulong Start;
        public readonly Dictionary<HuntState, double> Seconds = new();
        public int? Required;
        public int StepUps, StepBacks, MostStep, TrapFails, SurveyXps, TrackXps, TrapXps;
        public double SurveyXp, TrackXp, TrapXp, CatchXp, JournalXp;
        public bool Caught, Escaped, Whole;
        public HuntState? EscapedIn;
        public HuntState? After;
        public readonly Dictionary<Item, int> Items = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<Item, int> CatchItems = new(ReferenceEqualityComparer.Instance);
    }

    sealed class DevHuntWatch
    {
        public ulong Since, LastEvent, Length, StateSince;
        public HuntingActionComponent Action = null!;
        public readonly List<Action> Off = new();
        public readonly List<DevMoment> Moments = new();
        public readonly List<DevHuntSeen> Hunts = new();
        public DevHuntSeen? Now;
        public HuntState? StateNow;
        public DevMoment? Open;
        public ulong LastLine;
        public bool Failed;
        public GamesByMalcs.Melvor2.Entities.Bank.BankEntity? Bank;
        public readonly List<Item> Watched = new();
        public readonly Dictionary<Item, int> StartCounts = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<Item, int> Counts = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<Item, int> Outside = new(ReferenceEqualityComparer.Instance);
        public ulong LastRead;
        public readonly Dictionary<ActiveHuntingAnimal, string> Start = new(ReferenceEqualityComparer.Instance);
    }

    DevHuntWatch? _devHunts;

    void DevWatchRequest()
    {
        string path = Path.Combine(_ctx.Directory, WatchRequestFile);
        if (!File.Exists(path)) return;
        string asked = File.ReadAllText(path).Trim();
        File.Delete(path);
        int minutes = 15;
        if (asked.Length > 0 && !(int.TryParse(asked, NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes) && minutes is >= 1 and <= 60))
        {
            DevLog($"watch | the request's '{asked}' is not a number of minutes from 1 to 60: it watches 15");
            minutes = 15;
        }
        var action = _hunting!.Action;
        DevLog($"watch | {minutes} minutes asked{(asked.Length == 0 ? " (the request was empty)" : $" (the request said '{asked}')")}, or until two minutes without an event " +
            $"of a hunt; only listening, nothing is changed; {DevHunt()}");
        if (!action.IsStarted || !DevHuntingNow() || action.CurrentAnimal == null)
        {
            const string not = "not hunting when the watch began: nothing was listened to, nothing changed. Start hunting an animal by hand and send the request again.";
            DevLog("watch | " + not);
            File.WriteAllText(Path.Combine(_ctx.Directory, WatchDoneFile), not);
            return;
        }
        var w = new DevHuntWatch { Action = action, Since = Time.GetTicksMsec(), Length = (ulong)minutes * 60000 };
        w.LastEvent = w.LastLine = w.StateSince = w.Since;
        w.StateNow = action.CurrentAnimal.State;
        foreach (ActiveHuntingAnimal animal in action.Animals.Values)
            if (animal != null) w.Start[animal] = DevAnimalStats(animal);
        DevBankStart(w);
        _devHunts = w;
        DevWatchListen(w);
        ActiveHuntingAnimal hunted = action.CurrentAnimal;
        // The hunt in progress when the watch begins is not whole: it is counted from its next start. The page's cards are
        // read (only read) for the XP range of a catch, when the Hunting page is on the screen.
        w.Now = new DevHuntSeen { Animal = hunted, Start = w.Since, Whole = false };
        ReadPage();
        AnimalRates ours = ReadAnimal(hunted);
        DevLog($"watch | listening; the animal hunted {hunted.Animal.Id} at {hunted.Location?.Location?.Id}, in state {hunted.State}, step {hunted.CurrentStep} of {hunted.RequiredSteps}, " +
            $"tracks {hunted.Tracks}, trap fails {hunted.TrapFailCount}, survey fails {DevSurveyFails(hunted)}; its numbers {w.Start[hunted]}; the XP range of a catch " +
            $"{(ours.XpRange is { } range ? $"{R(range.Low)}..{R(range.High)}" : "NOT READ (the Hunting page was not on the screen with this animal's location)")}; " +
            (w.Bank == null ? "the bank NOT FOUND: the items, traps and bait are not read"
                : $"read from the bank's counts: {w.Watched.Count} items, this animal's trap {ours.Trap?.Id ?? "none"} {(ours.Trap != null ? w.StartCounts.GetValueOrDefault(ours.Trap) : 0)}, " +
                  $"bait {ours.Bait?.Id ?? "none"} ({(ours.UseBait ? "used" : "not used")}) {(ours.Bait != null ? w.StartCounts.GetValueOrDefault(ours.Bait) : 0)}"));
        DevTracker("at the start", null);
    }

    static int? DevSurveyFails(ActiveHuntingAnimal animal)
    {
        try { return DevNative.SurveyFails(animal); }
        catch (Exception) { return null; }
    }

    // The bank and every item an animal can give or use (its products, survey finds, rare extras, its trap and its bait),
    // with its count now (Woodcutting Rates' DevBankStart).
    void DevBankStart(DevHuntWatch w)
    {
        w.Bank = _game != null && _game.Entities.TryGetObjectById("MelvorBase:Bank", out GamesByMalcs.Melvor2.Entities.GameEntity? bank)
            ? bank as GamesByMalcs.Melvor2.Entities.Bank.BankEntity : null;
        if (w.Bank == null) return;
        foreach (ActiveHuntingAnimal animal in w.Action.Animals.Values)
        {
            if (animal == null) continue;
            AnimalRates a = ReadAnimal(animal);
            var items = a.Products.Select(d => d.Item).Concat(a.SurveyFinds.Select(d => d.Item)).Concat(a.Rare.SelectMany(s => s.Extras).Select(d => d.Item))
                .Concat(animal.CompatibleTraps ?? (IReadOnlyList<Item>)Array.Empty<Item>()).Append(a.Trap).Append(a.Bait);
            foreach (Item? item in items)
                if (item != null && !w.Counts.ContainsKey(item))
                {
                    int count = w.Bank.Inventory.GetQuantity(item);
                    w.Watched.Add(item);
                    w.Counts[item] = w.StartCounts[item] = count;
                }
        }
        w.LastRead = Time.GetTicksMsec();
    }

    static void DevBankRead(DevHuntWatch w, DevMoment? m)
    {
        if (w.Bank == null) return;
        w.LastRead = Time.GetTicksMsec();
        foreach (Item item in w.Watched)
        {
            int now = w.Bank.Inventory.GetQuantity(item), was = w.Counts[item];
            if (now == was) continue;
            w.Counts[item] = now;
            if (m != null) m.Items.Add((item, now - was));
            else w.Outside[item] = w.Outside.GetValueOrDefault(item) + now - was;
        }
    }

    static string DevAnimalStats(ActiveHuntingAnimal a)
    {
        HuntingActionStats s = a.Stats;
        return $"survey {R(s.SurveyInterval.Value)} ms, steps {s.TrackingSteps.Value}, step {s.TrackingInterval.Value} ms at {R(s.TrackingSuccessChance.Value)}, " +
            $"skip {R(s.SkipTrackingStepChance?.Total.Value ?? 0)}, trap {s.TrappingInterval.Value} ms at {R(s.TrappingSuccessChance.Value)}, reattempt " +
            $"{R(s.TrappingReattemptChance?.Total.Value ?? 0)}, rare {R(s.RareSpeciesChance.Value)}, XP {R(s.Experience.XpToAdd.Value)}, doubling " +
            $"{R(s.DoublingChance?.Total.Value ?? 0)}, preservation {R(s.PreservationChance?.Total.Value ?? 0)}, trap {a.SelectedTrap?.Id ?? "none"}, bait {a.UseBait}, trapless {a.TraplessEnabled}";
    }

    void DevWatchListen(DevHuntWatch w)
    {
        var action = w.Action;
        ExperienceComponent experience = _hunting!.Experience;
        void Safe(Action listen)
        {
            if (w.Failed || !ReferenceEquals(_devHunts, w) || GameAccess.IsComputingOffline) return;
            try { listen(); }
            catch (Exception e)
            {
                w.Failed = true;
                DevLog("watch | a listener failed, the watch ends: " + e.Message);
            }
        }
        foreach (ActiveHuntingAnimal animal in action.Animals.Values)
        {
            if (animal == null) continue;
            ActiveHuntingAnimal a = animal;
            EventHandler<ValueChangedEventArgs<HuntState>> state = (_, e) => Safe(() =>
            {
                DevMoment m = DevMomentNow(w, a);
                m.State = (m.State?.From ?? e.OldValue, e.NewValue);
                m.Order.Add("state");
                DevStateChanged(w, a, e.OldValue, e.NewValue);
            });
            EventHandler<ValueChangedEventArgs<int>> step = (_, e) => Safe(() =>
            {
                DevMoment m = DevMomentNow(w, a);
                m.Steps.Add((e.OldValue, e.NewValue));
                m.Order.Add("step");
                if (w.Now is { } h && ReferenceEquals(h.Animal, a))
                {
                    if (e.NewValue > e.OldValue) h.StepUps += e.NewValue - e.OldValue;
                    else if (e.NewValue < e.OldValue) h.StepBacks++;
                    h.MostStep = Math.Max(h.MostStep, e.NewValue);
                }
            });
            EventHandler<ValueChangedEventArgs<int>> required = (_, e) => Safe(() =>
            {
                DevMoment m = DevMomentNow(w, a);
                m.RequiredSteps = e.NewValue;
                m.Order.Add("required");
                if (w.Now is { } h && ReferenceEquals(h.Animal, a) && e.NewValue > 0) h.Required = e.NewValue;
            });
            EventHandler<ValueChangedEventArgs<int>> tracks = (_, e) => Safe(() =>
            {
                DevMoment m = DevMomentNow(w, a);
                m.Tracks = e.NewValue;
                m.Order.Add("tracks");
            });
            EventHandler<ValueChangedEventArgs<int>> fails = (_, e) => Safe(() =>
            {
                DevMoment m = DevMomentNow(w, a);
                m.TrapFails = e.NewValue;
                m.Order.Add("trap fails");
                if (w.Now is { } h && ReferenceEquals(h.Animal, a) && e.NewValue > e.OldValue) h.TrapFails += e.NewValue - e.OldValue;
            });
            EventHandler<HuntingAnimalEventArgs> trapped = (_, _) => Safe(() =>
            {
                DevMoment m = DevMomentNow(w, a);
                m.Trapped = true;
                m.Order.Add("TRAPPED");
                if (w.Now is { } h && ReferenceEquals(h.Animal, a)) h.Caught = true;
            });
            EventHandler<HuntingAnimalEventArgs> escaped = (_, _) => Safe(() =>
            {
                DevMoment m = DevMomentNow(w, a);
                m.Escaped = true;
                m.Order.Add("ESCAPED");
                if (w.Now is { } h && ReferenceEquals(h.Animal, a)) { h.Escaped = true; h.EscapedIn = a.State; }
            });
            a.StateChanged += state;
            a.CurrentStepChanged += step;
            a.RequiredStepsChanged += required;
            a.TracksChanged += tracks;
            a.TrapFailCountChanged += fails;
            a.AnimalTrapped += trapped;
            a.AnimalEscaped += escaped;
            w.Off.Add(() =>
            {
                a.StateChanged -= state;
                a.CurrentStepChanged -= step;
                a.RequiredStepsChanged -= required;
                a.TracksChanged -= tracks;
                a.TrapFailCountChanged -= fails;
                a.AnimalTrapped -= trapped;
                a.AnimalEscaped -= escaped;
            });
            if (a.JournalEntry?.Experience is { } journal)
            {
                EventHandler<ValueChangedEventArgs<double>> journalXp = (_, e) => Safe(() =>
                {
                    DevMoment m = DevMomentNow(w, a);
                    m.JournalXp += e.NewValue - e.OldValue;
                    m.JournalXpChanges++;
                    m.Order.Add("journal xp");
                });
                journal.XpChanged += journalXp;
                w.Off.Add(() => journal.XpChanged -= journalXp);
            }
        }
        EventHandler<ValueChangedEventArgs<double>> xp = (_, e) => Safe(() =>
        {
            DevMoment m = DevMomentNow(w, action.CurrentAnimal);
            double gained = e.NewValue - e.OldValue;
            m.Xp += gained;
            m.XpChanges++;
            m.Order.Add("xp");
        });
        EventHandler<ItemAddedEventArgs> item = (_, e) => Safe(() =>
        {
            if (e.Item == null) return;
            DevMoment m = DevMomentNow(w, action.CurrentAnimal);
            m.Added.Add((e.Item, e.Quantity));
            m.Order.Add("item");
        });
        EventHandler<ValueChangedEventArgs<ActiveHuntingAnimal>> hunted = (_, e) => Safe(() =>
        {
            DevMoment m = DevMomentNow(w, e.NewValue);
            m.Other.Add($"the animal hunted {e.NewValue?.Animal.Id ?? "none"}");
            m.Order.Add("animal");
        });
        EventHandler<ValueChangedEventArgs<Item>> trap = (_, e) => Safe(() =>
        {
            DevMoment m = DevMomentNow(w, action.CurrentAnimal);
            m.Other.Add($"the trap in use {e.NewValue?.Id ?? "none"}");
            m.Order.Add("trap");
        });
        experience.XpChanged += xp;
        action.ItemAdded += item;
        action.CurrentAnimalChanged += hunted;
        action.CurrentTrapChanged += trap;
        w.Off.Add(() =>
        {
            experience.XpChanged -= xp;
            action.ItemAdded -= item;
            action.CurrentAnimalChanged -= hunted;
            action.CurrentTrapChanged -= trap;
        });
    }

    // A hunt's state changed: the time of the state before goes to the hunt; a new survey after a catch or an escape (or
    // from the state the hunt was in when it stopped) starts the next hunt, which is whole.
    void DevStateChanged(DevHuntWatch w, ActiveHuntingAnimal a, HuntState from, HuntState to)
    {
        ulong now = Time.GetTicksMsec();
        if (w.Now is { } h && ReferenceEquals(h.Animal, a))
        {
            h.Seconds[from] = h.Seconds.GetValueOrDefault(from) + (now - w.StateSince) / 1000.0;
            if ((h.Caught || h.Escaped) && h.After == null) h.After = to;
            if (to == HuntState.Surveying && from != HuntState.Surveying)
            {
                w.Hunts.Add(h);
                w.Now = new DevHuntSeen { Animal = a, Start = now, Whole = true };
            }
        }
        else if (to == HuntState.Surveying) w.Now = new DevHuntSeen { Animal = a, Start = now, Whole = true };
        w.StateSince = now;
        w.StateNow = to;
    }

    DevMoment DevMomentNow(DevHuntWatch w, ActiveHuntingAnimal? animal)
    {
        long frame = (long)Engine.GetProcessFrames();
        if (w.Open is { } open && open.Frame == frame) { open.Animal ??= animal; return open; }
        DevFlushMoment(w);
        w.LastEvent = Time.GetTicksMsec();
        return w.Open = new DevMoment { Frame = frame, At = Time.GetTicksMsec(), Animal = animal };
    }

    void DevFlushMoment(DevHuntWatch w)
    {
        if (w.Open is not { } m) return;
        w.Open = null;
        DevBankRead(w, m);
        if (w.Moments.Count < 20000) w.Moments.Add(m);
        // What the frame brought goes to its hunt: the one caught or escaped in it, even when the frame's own change of state
        // has started the next hunt already; the XP by the state the frame began in, a catch's apart.
        DevHuntSeen? h = (m.Trapped || m.Escaped) && w.Now is not { Caught: true } && w.Now is not { Escaped: true } ? w.Hunts.LastOrDefault() ?? w.Now : w.Now;
        if (h != null)
        {
            foreach (var (item, quantity) in m.Items)
            {
                h.Items[item] = h.Items.GetValueOrDefault(item) + quantity;
                if (m.Trapped) h.CatchItems[item] = h.CatchItems.GetValueOrDefault(item) + quantity;
            }
            h.JournalXp += m.JournalXp;
            if (m.Trapped) h.CatchXp += m.Xp;
            else if (m.XpChanges > 0)
                switch (m.State?.From ?? w.StateNow)
                {
                    case HuntState.Surveying: h.SurveyXp += m.Xp; h.SurveyXps += m.XpChanges; break;
                    case HuntState.Tracking: h.TrackXp += m.Xp; h.TrackXps += m.XpChanges; break;
                    default: h.TrapXp += m.Xp; h.TrapXps += m.XpChanges; break;
                }
        }
        double since = (m.At - w.LastLine) / 1000.0;
        w.LastLine = m.At;
        ActiveHuntingAnimal? a = m.Animal;
        DevLog($"hunt | +{since:0.###} s | {a?.Animal.LocalId ?? "?"}" + (m.State is { } s ? $" | state {s.From} -> {s.To}" : "") +
            (m.Steps.Count > 0 ? $" | step {string.Join(", ", m.Steps.Select(x => $"{x.From} -> {x.To}"))}" : "") + (m.RequiredSteps is { } r ? $" | steps needed {r}" : "") +
            (m.Tracks is { } t ? $" | tracks {t}" : "") + (m.TrapFails is { } f ? $" | trap fails {f}" : "") + (m.Trapped ? " | CAUGHT" : "") + (m.Escaped ? " | ESCAPED" : "") +
            $" | events [{string.Join(" ", m.Order)}]" + (m.XpChanges > 0 ? $" | XP +{R(m.Xp)} in {m.XpChanges}" : "") + (m.JournalXpChanges > 0 ? $" | journal XP +{R(m.JournalXp)}" : "") +
            (m.Items.Count > 0 ? $" | the bank's counts [{string.Join(", ", m.Items.Select(i => $"{i.Item.Id} {i.Quantity:+0;-0}, now {w.Counts.GetValueOrDefault(i.Item)}"))}]" : "") +
            (m.Added.Count > 0 ? $" | ItemAdded [{string.Join(", ", m.Added.Select(i => $"{i.Item.Id} x{i.Quantity}"))}]" : "") +
            (m.Other.Count > 0 ? " | " + string.Join(", ", m.Other) : "") + (a != null ? $" | survey fails {DevSurveyFails(a)?.ToString() ?? "-"}" : ""));
    }

    void DevWatchTick()
    {
        if (_devHunts is not { } w) return;
        if (w.Open != null && w.Open.Frame != (long)Engine.GetProcessFrames()) DevFlushMoment(w);
        ulong now = Time.GetTicksMsec();
        if (w.Open == null && now - w.LastRead >= BankReadEvery) DevBankRead(w, null);
        if (w.Failed) DevWatchEnd("a listener failed");
        else if (now - w.Since >= w.Length) DevWatchEnd($"the {w.Length / 60000} minutes are up");
        else if (now - w.LastEvent >= WatchIdle) DevWatchEnd($"two minutes without an event of a hunt (hunting {(w.Action.IsStarted ? "started" : "stopped")})");
    }

    void DevUnwatchHunts()
    {
        if (_devHunts is not { } w) return;
        foreach (Action off in w.Off)
        {
            try { off(); }
            catch (Exception) { }
        }
        w.Off.Clear();
    }

    // The summary: for every animal hunted, one line per question with its verdict; then the tracker, then the last line.
    void DevWatchEnd(string why, bool summarise = true)
    {
        if (_devHunts is not { } w) return;
        DevFlushMoment(w);
        DevUnwatchHunts();
        _devHunts = null;
        double seconds = (Time.GetTicksMsec() - w.Since) / 1000.0;
        var verdicts = new List<string>();
        try
        {
            var whole = w.Hunts.Where(h => h.Whole).ToList();
            DevLog($"watch | ended after {seconds:0} s: {why}; {w.Moments.Count} frames with an event, {w.Hunts.Count + (w.Now != null ? 1 : 0)} hunts seen, {whole.Count} whole " +
                $"(from a survey's start to the next), {whole.Count(h => h.Caught)} caught and {whole.Count(h => h.Escaped)} escaped among them");
            DevItemsSummary(w);
            if (!summarise || _hunting == null) verdicts.Add("NO SUMMARY");
            else
            {
                ReadPage();
                foreach (var group in whole.GroupBy(h => h.Animal, ReferenceEqualityComparer.Instance).OrderByDescending(g => g.Count()))
                {
                    var animal = (ActiveHuntingAnimal)group.Key!;
                    AnimalRates ours = ReadAnimal(animal);
                    var said = DevHuntSummary(group.ToList(), ours, w.Start.GetValueOrDefault(animal, "not read"));
                    verdicts.Add($"{animal.Animal.LocalId}: {string.Join(", ", said)}");
                }
            }
            DevTracker("at the end", w);
        }
        catch (Exception e) { verdicts.Add($"the summary failed: {e.GetType().Name}: {e.Message}"); }
        string result = $"watched {seconds:0} s ({why}); {string.Join(" | ", verdicts)}; stopped listening, nothing changed";
        DevLog("watch | " + result);
        File.WriteAllText(Path.Combine(_ctx.Directory, WatchDoneFile), result);
    }

    // Every question about one animal's whole hunts, each with its verdict in capitals; NOT DECIDED below MostCatches.
    List<string> DevHuntSummary(List<DevHuntSeen> hunts, AnimalRates ours, string start)
    {
        var verdicts = new List<string>();
        string id = ours.Animal.LocalId;
        int caught = hunts.Count(h => h.Caught), escaped = hunts.Count(h => h.Escaped);
        bool enough = caught >= MostCatches;
        double Mean(IEnumerable<double> values) { var list = values.ToList(); return list.Count == 0 ? double.NaN : list.Average(); }
        void Say(string question, string verdict, string detail)
        {
            DevLog($"watch | {id} | {question} | {verdict} | {detail}");
            verdicts.Add(verdict);
        }
        DevLog($"watch | {id} | {hunts.Count} whole hunts, {caught} caught, {escaped} escaped; its numbers at the start {start}, now {DevAnimalStats(ours.Active)}");

        // Surveying: its time a hunt against one survey, and against the location's chance by the name of insights.
        double survey = Mean(hunts.Select(h => h.Seconds.GetValueOrDefault(HuntState.Surveying)));
        double surveys = survey / ours.SurveySeconds;
        double insights = ours.Insights > 0 ? 1 / ours.Insights : double.NaN;
        Say("surveys a hunt", !enough ? "NOT DECIDED" : Math.Abs(surveys - 1) <= 0.15 ? "ONE SURVEY FINDS THE TRACKS"
            : double.IsFinite(insights) && Math.Abs(surveys - insights) <= 0.15 * insights ? "EVERY SURVEY FINDS THEM AT THE INSIGHTS CHANCE" : "NEITHER READING",
            $"surveying took {R(Math.Round(survey, 3))} s a hunt on average, {R(Math.Round(surveys, 3))} surveys of {R(ours.SurveySeconds)} s; one survey would be 1, the insights " +
            $"chance {R(ours.Insights)} would be {R(Math.Round(insights, 3))}; XP while surveying {R(Mean(hunts.Select(h => h.SurveyXp)))} a hunt in {R(Mean(hunts.Select(h => (double)h.SurveyXps)))} changes " +
            $"(the location's base {R(ours.SurveyXpBase)} a survey)");

        // Tracking: the steps a hunt needed against the game's range, steps that went back, and its time against the step's.
        var tracked = hunts.Where(h => h.Seconds.ContainsKey(HuntState.Tracking)).ToList();
        double tracking = Mean(tracked.Select(h => h.Seconds[HuntState.Tracking]));
        double needed = Mean(tracked.Where(h => h.Required != null).Select(h => (double)h.Required!.Value));
        int backs = tracked.Sum(h => h.StepBacks);
        int endedInTracking = hunts.Count(h => h.Escaped && h.EscapedIn == HuntState.Tracking) +
            hunts.Count(h => !h.Caught && !h.Escaped && !h.Seconds.ContainsKey(HuntState.Trapping) && h.Seconds.ContainsKey(HuntState.Tracking));
        double attempts = tracking / ours.StepSeconds, perStep = needed > 0 ? attempts / needed : double.NaN;
        double repeats = ours.TrackChance > 0 ? (1 - ours.SkipChance) / ours.TrackChance : double.NaN;
        Say("a failed tracking step", !enough ? "NOT DECIDED" : endedInTracking > 0 ? "A FAILED STEP ENDS THE HUNT"
            : double.IsFinite(repeats) && Math.Abs(perStep - repeats) <= 0.2 * repeats ? "A FAILED STEP REPEATS" : "NOT DECIDED",
            $"{tracked.Count} hunts tracked, {R(Math.Round(tracking, 3))} s each, {R(Math.Round(needed, 3))} steps needed (the game's range {ours.StepsMin}..{ours.StepsMax}, " +
            $"middle {(ours.StepsMin + ours.StepsMax) / 2.0}); {R(Math.Round(attempts, 3))} intervals of {R(ours.StepSeconds)} s, {R(Math.Round(perStep, 3))} a step, against " +
            $"{R(Math.Round(repeats, 3))} if a failed step repeats (success {R(ours.TrackChance)}, skip {R(ours.SkipChance)}); a step went back {backs} times; " +
            $"{endedInTracking} hunts ended in tracking; XP while tracking {R(Mean(hunts.Select(h => h.TrackXp)))} a hunt");
        int jumps = hunts.Sum(h => h.StepUps) - hunts.Sum(h => h.MostStep);
        Say("a skipped step", ours.SkipChance <= 0 ? "NOT DECIDED (NO SKIP CHANCE)" : !enough ? "NOT DECIDED" : jumps > 0 ? "A STEP CAN MOVE ON MORE THAN ONE" : "NO STEP MOVED ON MORE THAN ONE",
            $"skip chance {R(ours.SkipChance)}; the steps moved on {hunts.Sum(h => h.StepUps)} in all, the highest steps reached {hunts.Sum(h => h.MostStep)}");

        // Trapping: tries against the trap's time, fails, re-attempts and escapes.
        var trapping = hunts.Where(h => h.Seconds.ContainsKey(HuntState.Trapping)).ToList();
        double trapTime = Mean(trapping.Select(h => h.Seconds[HuntState.Trapping]));
        double fails = Mean(trapping.Select(h => (double)h.TrapFails));
        int escapedAtTrap = hunts.Count(h => h.Escaped && h.EscapedIn == HuntState.Trapping);
        int reattempted = trapping.Count(h => h.TrapFails > 0 && h.Caught);
        Say("a failed trap", !enough ? "NOT DECIDED" : reattempted > 0 ? "A FAILED TRAP IS TRIED AGAIN" : trapping.Any(h => h.TrapFails > 0) ? "A FAILED TRAP ENDS THE HUNT" : "NOT DECIDED",
            $"{trapping.Count} hunts trapped, {R(Math.Round(trapTime, 3))} s each ({R(Math.Round(trapTime / ours.TrapSeconds, 3))} tries of {R(ours.TrapSeconds)} s), " +
            $"{R(Math.Round(fails, 3))} fails each; {reattempted} caught after a fail, {escapedAtTrap} escaped at the trap (success {R(ours.TrapChance)}, reattempt {R(ours.ReattemptChance)})");
        var afterEscape = hunts.Where(h => h.Escaped && h.After != null).Select(h => h.After!.Value).Distinct().ToList();
        Say("an escape", escaped == 0 ? "NOT DECIDED (NO ESCAPE)" : afterEscape.SequenceEqual(new[] { HuntState.Surveying }) ? "AN ESCAPE STARTS OVER" : "NOT DECIDED",
            $"{escaped} escapes, in [{string.Join(", ", hunts.Where(h => h.Escaped).Select(h => h.EscapedIn?.ToString() ?? "?").Distinct())}], followed by [{string.Join(", ", afterEscape)}]");

        // XP: with a catch only, or in other states too; a catch's XP against the page's range.
        var catches = hunts.Where(h => h.Caught).ToList();
        double catchXp = Mean(catches.Select(h => h.CatchXp)), otherXp = Mean(hunts.Select(h => h.SurveyXp + h.TrackXp + h.TrapXp));
        string range = ours.XpRange is { } xr ? $"{R(ours.XpEach * xr.Low)}..{R(ours.XpEach * xr.High)}, middle {R(ours.XpPerCatch)}" : "NOT READ";
        double low = catches.Count == 0 ? double.NaN : catches.Min(h => h.CatchXp), high = catches.Count == 0 ? double.NaN : catches.Max(h => h.CatchXp);
        Say("XP", !enough ? "NOT DECIDED" : otherXp <= 1e-9 ? "XP ON A CATCH ONLY" : "XP OUTSIDE A CATCH TOO",
            $"a catch {R(Math.Round(catchXp, 3))} on average ({R(low)} to {R(high)}), ours {range}; XP outside a catch {R(Math.Round(otherXp, 3))} a hunt");
        Say("journal XP", !enough ? "NOT DECIDED" : "MEASURED",
            $"{R(Mean(hunts.Select(h => h.JournalXp)))} a hunt, {R(Mean(catches.Select(h => h.JournalXp)))} a hunt that caught; the game's base {ours.JournalTrackingXp} tracking, " +
            $"{ours.JournalTrappingXp} trapping");

        // Traps and bait by the bank's counts: what a catch, an escape and a fail used up.
        string Used(Item? item, string what)
        {
            if (item == null) return $"no {what}";
            double perCatch = Mean(catches.Select(h => -(double)h.Items.GetValueOrDefault(item)));
            double perEscape = Mean(hunts.Where(h => h.Escaped).Select(h => -(double)h.Items.GetValueOrDefault(item)));
            double perFail = trapping.Sum(h => h.TrapFails) is var f && f > 0 ? -trapping.Sum(h => (double)h.Items.GetValueOrDefault(item)) / f : double.NaN;
            return $"{item.Id} {R(Math.Round(perCatch, 3))} a hunt that caught, {R(Math.Round(perEscape, 3))} a hunt that escaped, {R(Math.Round(perFail, 3))} a fail of the trap";
        }
        double trapsPerCatch = ours.Trap == null ? double.NaN : Mean(catches.Select(h => -(double)h.Items.GetValueOrDefault(ours.Trap)));
        Say("traps", ours.Trapless ? "NOT DECIDED (HUNTED WITHOUT A TRAP)" : !enough || ours.Trap == null ? "NOT DECIDED"
            : Math.Abs(trapsPerCatch) < 0.05 ? "NO TRAP USED UP ON A CATCH" : Math.Abs(trapsPerCatch - 1) <= 0.15 ? "A TRAP ON EVERY CATCH" : "NOT DECIDED",
            Used(ours.Trap, "trap") + $"; preservation {R(ours.Preservation)}");
        double baitPerCatch = ours.Bait == null ? double.NaN : Mean(catches.Select(h => -(double)h.Items.GetValueOrDefault(ours.Bait)));
        double baitPerHunt = ours.Bait == null ? double.NaN : Mean(hunts.Select(h => -(double)h.Items.GetValueOrDefault(ours.Bait)));
        Say("bait", !ours.UseBait ? "NOT DECIDED (NO BAIT USED)" : !enough ? "NOT DECIDED"
            : Math.Abs(baitPerHunt - 1) <= 0.15 && escaped > 0 ? "BAIT ONCE A HUNT" : Math.Abs(baitPerCatch - 1) <= 0.15 ? "BAIT ON EVERY CATCH" : "NOT DECIDED",
            Used(ours.Bait, "bait") + $", {R(Math.Round(baitPerHunt, 3))} a hunt; preservation {R(ours.Preservation)}");

        // The items of a catch against ours (products with doubling, rare extras).
        var expected = new Dictionary<Item, double>(ReferenceEqualityComparer.Instance);
        foreach (AnimalRates.Drop d in ours.Products.Concat(ours.Rare.SelectMany(s => s.Extras)).Where(d => !d.Collectable))
            expected[d.Item] = expected.GetValueOrDefault(d.Item) + d.Chance * d.Each;
        var seen = new Dictionary<Item, double>(ReferenceEqualityComparer.Instance);
        foreach (var (item, quantity) in catches.SelectMany(h => h.CatchItems).Where(i => i.Value > 0))
            seen[item] = seen.GetValueOrDefault(item) + (double)quantity / Math.Max(1, catches.Count);
        bool far = expected.Any(e => Math.Abs(seen.GetValueOrDefault(e.Key) - e.Value) > Math.Max(0.25, 0.25 * e.Value));
        Say("items of a catch", !enough ? "NOT DECIDED" : far ? "FAR FROM OURS" : "AS OURS",
            $"seen a catch [{string.Join(", ", seen.Select(s => $"{s.Key.Id} {R(Math.Round(s.Value, 3))}"))}], ours [{string.Join(", ", expected.Select(e => $"{e.Key.Id} {R(Math.Round(e.Value, 3))}"))}]");

        // The time of a catch against ours by every reading of the chain.
        double hunted = hunts.Sum(h => h.Seconds.Values.Sum());
        double perCatch = caught > 0 ? hunted / caught : double.NaN;
        var readings = AnimalRates.Readings.Select(rule => (Rule: rule, Time: AnimalRates.Chain(rule, ours)?.Seconds)).Where(x => x.Time != null)
            .OrderBy(x => Math.Abs(x.Time!.Value - perCatch)).ToList();
        string nearest = readings.Count == 0 ? "none" : $"{DevReadingName(readings[0].Rule)} {R(Math.Round(readings[0].Time!.Value, 3))} s";
        Say("time of a catch", !enough ? "NOT DECIDED" : readings.Count > 0 && Math.Abs(readings[0].Time!.Value - perCatch) <= 0.1 * perCatch ? "A READING FITS" : "NO READING FITS",
            $"measured {R(Math.Round(perCatch, 3))} s a catch over {R(Math.Round(hunted, 1))} s of whole hunts; the nearest reading {nearest}; all: " +
            string.Join(", ", readings.Select(x => $"{DevReadingName(x.Rule)} {R(Math.Round(x.Time!.Value, 3))}")));
        return verdicts;
    }

    // Each watched item's count from the start to the end, what moved in frames with an event and outside them.
    void DevItemsSummary(DevHuntWatch w)
    {
        if (w.Bank == null) { DevLog("watch | the items | the bank was not found: nothing read"); return; }
        var moved = w.Watched.Where(i => w.Counts[i] != w.StartCounts[i] || w.Outside.ContainsKey(i)).ToList();
        int added = w.Moments.Sum(m => m.Added.Count);
        DevLog($"watch | the items | {w.Watched.Count} watched; moved: [{string.Join(", ", moved.Select(i => $"{i.Id} {w.StartCounts[i]} -> {w.Counts[i]} " +
            $"(in frames with an event {w.Moments.Sum(m => m.Items.Where(x => ReferenceEquals(x.Item, i)).Sum(x => x.Quantity)):+0;-0}, outside {w.Outside.GetValueOrDefault(i):+0;-0})"))}]; " +
            $"the action's ItemAdded came {added} times");
    }

    void DevTracker(string when, DevHuntWatch? w)
    {
        try
        {
            var tracker = _hunting!.Tracker;
            ExperienceComponent experience = _hunting.Experience;
            var items = w?.Moments.SelectMany(m => m.Items.Select(i => i.Item)).Distinct().ToList() ?? new List<Item>();
            AnimalRates? ours = _hunting.Action.CurrentAnimal is { } a ? ReadAnimal(a) : null;
            int i = 0;
            foreach (var period in tracker.Periods)
            {
                double xp = period.Experience.TryGetValue(experience, out var list) ? list.Total : 0;
                double covered = Math.Min(period.CycleDuration, period.Experience.RecordedCount * period.SamplePeriod);
                string perHour = covered > 0 ? R(xp / covered * 3600) : "-";
                DevLog($"tracker | {when} | period {i++}: cycle {R(period.CycleDuration)}, {period.SampleCount} samples of {R(period.SamplePeriod)}, " +
                    $"{period.Experience.RecordedCount} recorded | Hunting XP {R(xp)}, an hour by it (if in seconds) {perHour}, ours {(ours == null ? "-" : R(ours.XpPerHour))} | " +
                    $"items [{string.Join(", ", items.Select(item => $"{item.Id} {(period.Items.TryGetValue(item, out var l) ? l.Total : 0)}"))}]");
            }
            if (i == 0) DevLog($"tracker | {when} | the tracker has no periods");
        }
        catch (Exception e) { DevLog($"tracker | {when} | could not be read: {e.Message}"); }
    }
}
#endif
