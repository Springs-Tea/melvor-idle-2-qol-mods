using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MelvorMods.Shared;

namespace CombatSim;

/// <summary>
/// Find upgrades (issue #4) in the simulator's window; the search is the mod's (SimUpgrades.cs).
/// <list type="bullet">
/// <item>Right: the button "Find upgrades" over "Wear this setup first", for the fight Fight would start (the game's
/// TexturedButton4x, as "Undo changes" and History are). It opens the panel where the items to try are chosen
/// (SimViewUpgradeChoice.cs), which starts the search. While it runs the button says Stop; it says Show upgrades when the
/// last search of that fight, with the setup on the left, is not on show.</item>
/// <item>Middle: the items in the list of the fights' own rows, header and sideways numbers (BuildRow, HeaderButton,
/// NumbersStrip), the way an earlier scan takes the list's place (the line above it, "Back to the fights" where "Back to
/// now" is). Every column of the list says what the item changes in it, against the setup as it is, in Compare's words
/// for how much ("+14%", "×2.5") and by the simulator's one rule for better, worse and could be luck (PairVerdict:
/// the ranges of issue #5, a real 0 included, and as Compare judges B against A the bar lowered by the luck the item and
/// the setup as it is met alike, issue #63; a number without a range only where the deaths, kills or runs, or the game's
/// own numbers, decide it, issue #67); the same is "no better". A cell keeps its words, as Compare's first cell
/// does; the ± is in Details and the line's tooltip. At the end, where the item comes from, as Item
/// Sources says it. Items the character cannot wear yet, when the panel lists them, come after the others, in grey with
/// what they need beside their name.</item>
/// <item>Details: the line clicked, against the setup as it is, column by column in Stat lines, with the change's ± where
/// the same dice decided it, and where it comes from in the Item Encyclopedia's "Found in" look.</item>
/// </list>
/// </summary>
sealed partial class SimView
{
    // The cells of the items tried: a column's key and this, for what the item changes in it; "from" and this, for where
    // it comes from.
    const string Gain = "@up", FromKey = "from";
    /// <summary>What an item goes on in place of when its slot is empty (UpgradeTry.InPlaceOf).</summary>
    internal const string EmptySlot = "in an empty slot";

    // What the item does to a column's number: its words and colour, the verdict, and where it goes in the list's order.
    // Where the same dice decided the verdict (issue #63): the change as Details writes it beside its ± (Change, in percent
    // where the list says "×2.5"; null where the words are the item's own, as SameDiceNote has it for a setup's own number),
    // and the ± (Plus). What decided the verdict (By, issue #67), for Details' words and its foot.
    readonly record struct Gained(string Text, Color Tint, Verdict? Verdict, double? Sort, string? Change = null, string? Plus = null,
        Ground By = Ground.None);

    IUpgradeLookup? Finder => _lookup as IUpgradeLookup;
    Button _upgradeButton = null!;
    Action? _upgradeAction;
    bool _headerUpgrades;
    // The lines of the items as the list has them, and the one Details shows.
    List<SimResult> _upgradeOrdered = new();
    string? _upgradeShownKey;
    // Worked out once a line and column, against one result of the setup as it is and one trained skill.
    readonly Dictionary<(SimResult Line, string Column), Gained> _gains = new();
    (SimResult? Baseline, string Skill) _gainsFor;
#if DEVTOOLS
    readonly System.Diagnostics.Stopwatch _devBuild = new();
    int _devBuilt, _devBuiltRows;
    double _devBuildMs;
#endif

    /// <summary>
    /// The mod has new lines for the list, or knows where items come from: the list is made again, and the panel counts
    /// again. Details follows its own signature, which covers what can change in it.
    /// </summary>
    public void UpgradesChanged()
    {
        _tableSignature = "";
        SyncUpgradeChoice();
    }

    // ---- right: the button ----------------------------------------------------------------------------------

    Control BuildUpgradeButton()
    {
        _upgradeButton = _s.GameButton("Find upgrades", "TexturedButton4x", 13);
        _upgradeButton.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        _upgradeButton.FocusMode = Control.FocusModeEnum.None;
        _upgradeButton.Visible = false;
        _upgradeButton.Pressed += () => _upgradeAction?.Invoke();
        return _upgradeButton;
    }

    // From FillFight: the button follows the fight Fight would start, as Fight's own words do.
    void FillUpgradeButton()
    {
        if (Finder is not { } finder || finder.UpgradesOff) { _upgradeButton.Visible = false; return; }
        UpgradeSearch? search = _state.Upgrades;
        if (search is { Running: true })
        {
            _upgradeButton.Visible = true;
            _upgradeButton.Disabled = false;
            if (_state.ShowingUpgrades)
            {
                _upgradeButton.Text = "Stop";
                _upgradeButton.TooltipText = "Stop trying items: the ones tried so far stay in the list. Cancel on the left stops it too.";
                _upgradeAction = finder.StopUpgrades;
            }
            else
            {
                _upgradeButton.Text = "Show upgrades";
                _upgradeButton.TooltipText = $"Back to the items being tried for {search.Target.Name}";
                _upgradeAction = ShowUpgrades;
            }
            return;
        }
        _upgradeButton.Visible = _fightTarget != null;
        if (_fightTarget is not { } target)
        {
            if (_pickerKind == UpgradePanel) ClosePicker();
            return;
        }
        // The panel is for one fight: another one selected meanwhile closes it.
        if (_pickerKind == UpgradePanel && _choiceTarget?.Key != target.Key) ClosePicker();
        // The last search of this fight with the setup on the left, while it is not on show: one click brings it back.
        if (search != null && !_state.ShowingUpgrades && _state.Viewing == null && search.Target.Key == target.Key
            && search.BestMode == _state.BestMode && search.Matches(_state))
        {
            _upgradeButton.Text = "Show upgrades";
            _upgradeButton.Disabled = false;
            _upgradeButton.TooltipText = $"Back to the {search.Answered} items tried for {target.Name}, {Ago(search.At)}.\n" +
                                         "Over them, Find upgrades chooses other items to try.";
            _upgradeAction = ShowUpgrades;
            return;
        }
        _upgradeButton.Text = "Find upgrades";
        string off = _state.Viewing != null ? "An earlier scan is on show: the equipment on the left is not the one it was made with. Press Back to now first."
            : _state.Running ? "A scan is running: Find upgrades waits until it has ended."
            : "";
        _upgradeButton.Disabled = off != "";
        string stance = target.StanceName != "" ? $" in the {target.StanceName} stance" : "";
        string what = $"Choose items to try against {target.Name}{stance}, on top of {_state.SetupName}:\n" +
                      "by slot, by where they come from, or by name. Each is one fight; the panel says how many\n" +
                      $"and about how long before anything runs. At most {UpgradeSearch.MaxItems} a search. Nothing is put on.";
        _upgradeButton.TooltipText = off != "" ? TipLines(off) + "\n\n" + what : what;
        _upgradeAction = () => OpenUpgradeChoice(target);
        SyncUpgradeChoice();   // a scan that started or ended meanwhile, when the panel is open
    }

    // The list of the last search, in the middle again.
    void ShowUpgrades()
    {
        ClosePicker();
        _state.Viewing = null;
        _state.UpgradesShown = true;
        UpgradesChanged();
        Refresh();
    }

    /// <summary>From ShowCurrent: off the items, back to the fights with the one they were tried for selected. False when they were not on show.</summary>
    bool LeaveUpgrades()
    {
        if (!_state.UpgradesShown) return false;
        bool shown = _state.ShowingUpgrades;
        _state.UpgradesShown = _state.UpgradePinned = false;
        _state.UpgradeSelected = null;
        if (!shown) return false;
        _state.Selected = _state.Upgrades!.Target.Key;
        _state.Pinned = true;
        _tableSignature = _detailsSignature = "";
        return true;
    }

    // ---- middle: the cells ----------------------------------------------------------------------------------

    List<SimColumn>? _upgradeCatalog;
    /// <summary>What an item changes in each column, and where it comes from (<see cref="ColumnOf"/> finds them by their keys).</summary>
    List<SimColumn> UpgradeCatalog => _upgradeCatalog ??= Catalog.Select(GainCell).Append(FromCell()).ToList();

    /// <summary>The cells of the items for the columns chosen, from left to right, and where it comes from at the end.</summary>
    List<SimColumn> UpgradeCells(List<SimColumn> shown) =>
        shown.Select(c => ColumnOf(c.Key + Gain)).OfType<SimColumn>().Append(ColumnOf(FromKey + Gain)!).ToList();

    // Every word a cell can show at its widest, measured as Compare measures its first cells (BetterWidth).
    static readonly string[] GainWords = { "no better", "the same", "+100%", "-100%", "+9.9%", "×999+", "×9.9", "+99.99M", "+999.9K" };
    float? _gainWidth;
    float GainWidth => _gainWidth ??= (MathF.Ceiling(Widest(GameStyle.SemiBold, _s.Px(14), GainWords)) + 1) / _s.Scale;

    const string ChangeWords = "\"+14%\": 14% more with it; \"-23%\": 23% less; \"×2.5\": two and a half times as much; \"+7,300\": that much where there was none";

    // What the header of an item's cell says it is: a change for every number, the item's own words where the column has
    // words (Safe, a drop's name, a first death against none). How a number without a range is coloured follows in the
    // cell's tooltip from its column (SimColumn.NoRangeTip, issue #67).
    static string GainTip(SimColumn c) => c.Key switch
    {
        "safe" => "Safe or risky with the item: its own words, not a change",
        "bestDrop" => "The most valuable drop with the item: its name, not a change",
        "firstDeath" => "First death: what the item changes. \"+20%\": a first death 20% later with it. \"none\": with it you never die; " +
                        "a time: you die then with it, and never did without",
        "lowHp" => $"Lowest hitpoints: what the item changes, against the setup as it is. {ChangeWords}. " +
                   "Where you fell to 0 without it, the lowest share with it, as a number of its own",
        "netGold" => "Gold after supplies: how much more or less with the item, per hour. \"+1,200\": that much more; \"-300\": that much less",
        _ => $"{c.Name}: what the item changes, against the setup as it is. {ChangeWords}",
    };

    // What the item changes in a column, under the column's own name; the list sorts by it with what adds most on top.
    SimColumn GainCell(SimColumn c) => c with
    {
        Key = c.Key + Gain,
        Tip = GainTip(c) + (c.Better == Better.Neither ? "\nNo colour: neither more nor less is better"
            : "\nGreen where it is better, red where it is worse, grey where that could be luck; \"no better\" where it is the same"),
        Width = Math.Max(c.Width, GainWidth),
        Value = line => GainOf(c, line)?.Sort,
        // A line without numbers says why beside the item's name (BuildUpgradeRow), its cells stay empty.
        Text = line => UsableTry(line) == null ? "" : GainOf(c, line)?.Text ?? "-",
        Tint = line => GainOf(c, line)?.Tint ?? GameStyle.TextDim,
        Better = Better.More,
        Percent = false,
        Trim = true,
        Dash = "when the item or the setup as it is has no number for the fight",
        Way = "The first click puts what adds most on top: clear gains by how much, then gains that could be luck, then no better, " +
              "then losses that could be luck, then what makes it worse",
    };

    SimColumn FromCell() => new(FromKey + Gain, "", "Comes from", "Where it comes from",
        "Where it comes from, as Item Sources says it: put the pointer on it for its headings, click the line for every place. " +
        "In your bank, or Worn now, when you have one", 110,
        FromRank, line => FromOf(line).Text, line => FromOf(line).Tint, Better.More, Percent: false, Trim: true,
        Way: "The first click puts what you have on top");

    Gained? GainOf(SimColumn c, SimResult line)
    {
        if (_state.Upgrades is not { Baseline: { Failed: false, WearNote: "" } baseline } || UsableTry(line) is not { } trial) return null;
        if (!ReferenceEquals(_gainsFor.Baseline, baseline) || _gainsFor.Skill != _state.ShownSkillId)
        {
            _gains.Clear();
            _gainsFor = (baseline, _state.ShownSkillId);
        }
        if (!_gains.TryGetValue((line, c.Key), out Gained gained)) _gains[(line, c.Key)] = gained = Adds(c, trial, baseline);
        return gained;
    }

    // Words of the item's own: the column has words, a number is the worst there is or stands for "never" (a first death
    // that never came), or there is nothing to take a share of in a column whose own number is a share (Low HP at 0).
    static bool OwnWords(SimColumn c, double t, double n) =>
        c.Key is "safe" or "bestDrop" || !double.IsFinite(t) || !double.IsFinite(n) || t == double.MaxValue || n == double.MaxValue
        || (n == 0 && !c.Percent);

    // The item's number against the setup as it is, in Compare's words for how much (Lead: SimRanges.Percent, Times) and
    // by its rule (PairVerdict): a share of the number as it is ("+14%", "-23%", "×2.5"); "+7,300" where there was none,
    // as "Compared with" gives a number against 0; how much more or less where the number as it is is below 0 (Net gold).
    // Words stay the item's own (OwnWords), coloured by the rule as a change is: a number without a range only where the
    // deaths, kills or runs, or the game's own numbers, decide it, otherwise grey (issue #67).
    // Where the verdict went by the same dice (issue #63, SimRanges.Apart, which PairVerdict's Judge took), the change for
    // Details and its ±, both on the base of the change, so that a change is green or red exactly when it is larger than
    // its ±: a share of the number as it is by SimRanges.Percents, "+64.4%" and "± 64.1%" (SameDiceNote, as "Compared
    // with" writes a change), twice as much or more too: "+150%" and "± 160%" where the list says "×2.5", as "Compared
    // with" writes it; a "×2.5" over "± 1.6" of the number as it is would be grey though 2.5 is more than 1.6 (review of
    // turn 1). In the column's own format, "+300" and "± 2,000"; beside the item's own words, the difference with its ±,
    // "+1,234 ± 2,000" (SameDiceNote). The list's cell keeps its words, as Compare's first cell does.
    Gained Adds(SimColumn c, SimResult trial, SimResult now)
    {
        if (c.Value(trial) is not { } t || c.Value(now) is not { } n) return new Gained("-", GameStyle.TextDim, null, null);
        var (verdict, by) = PairJudged(c, trial, now, t, n);
        if (verdict == Verdict.Same) return new Gained(c.Better == Better.Neither ? "the same" : "no better", GameStyle.TextDim, verdict, 0);
        Color tint = verdict switch
        {
            Verdict.Better => Good,
            Verdict.Worse => Bad,
            Verdict.Luck => GameStyle.TextGrey,
            _ => GameStyle.TextWhite,
        };
        string Amount(double v) => (c.Format ?? Fmt)(v);
        // The reach of luck the verdict went by, where the item and the setup as it is were paired (PairVerdict judges two
        // finite numbers by SimRanges.Judge, which takes it whenever there is one).
        SimLuck? luck = double.IsFinite(t) && double.IsFinite(n) && c.Rate?.Invoke() is { } rate ? SimRanges.Apart(trial, now, rate) : null;
        string text;
        string? change = null, plus = null;
        // How far the item is ahead, the way the column is better.
        double ahead;
        if (OwnWords(c, t, n))
        {
            text = c.Text(trial);
            bool finite = double.IsFinite(t) && double.IsFinite(n);
            // Which way the words point: the verdict's way, and for words that could be luck (Safe or Risky, a drop, a first
            // death, issue #67) the way the item is ahead or behind, so that they sort among the gains or the losses that
            // could be luck, as a grey change does, never with "no better". Neither way better: none.
            int way = verdict switch
            {
                Verdict.Better => 1,
                Verdict.Worse => -1,
                Verdict.Luck when c.Better != Better.Neither => Ahead(c.Better, t, n) ? 1 : -1,
                _ => 0,
            };
            ahead = way == 0 || finite ? way : way * double.PositiveInfinity;
            if (luck is { } own) plus = SameDiceNote(c, t, n, own, null, percent: false).Note;
        }
        else
        {
            double share;
            if (n > 0 && t >= 0)
            {
                bool times = t / n >= 2;
                text = times ? "×" + Times(t / n) : t > n ? "+" + SimRanges.Percent(t / n - 1) : "-" + SimRanges.Percent(1 - t / n);
                share = (t - n) / n;
                if (luck is { } l) (change, plus) = SameDiceNote(c, t, n, l, null, percent: true);
            }
            else if (n == 0)
            {
                text = (t >= 0 ? "+" : "-") + Amount(Math.Abs(t));
                share = t > 0 ? double.PositiveInfinity : double.NegativeInfinity;
                if (luck is { } l) (change, plus) = (text, "± " + Amount(l.Reach));
            }
            else
            {
                text = (t >= n ? "+" : "-") + Amount(Math.Abs(t - n));
                share = (t - n) / Math.Abs(n);
                if (luck is { } l) (change, plus) = (text, "± " + Amount(l.Reach));
            }
            ahead = c.Better == Better.Less ? -share : share;
        }
        // Folded below 1 after the verdict's tier, so that a clear gain sorts above any that could be luck, those above no
        // better, and the clear losses last (as Compare's Matters folds them).
        double folded = double.IsInfinity(ahead) ? Math.Sign(ahead) : ahead / (1 + Math.Abs(ahead));
        double tier = verdict == Verdict.Better ? 2 : verdict == Verdict.Worse ? -2 : 0;
        return new Gained(text, tint, verdict, tier + folded, change, plus, by);
    }

    /// <summary>The result of an item that was tried and fought with what it was asked to wear; null for any other line.</summary>
    SimResult? UsableTry(SimResult line) => _state.TryOf(line) is { Tried: true, Result: { Failed: false, WearNote: "" } r } ? r : null;

    /// <summary>A line of the items without numbers: not tried, failed, or the copy wore something else.</summary>
    bool UpgradeLineFailed(SimResult line) => UsableTry(line) == null;

    /// <summary>The name a line is sorted by: the enemy's, or among the items tried the item's.</summary>
    string LineName(SimResult line) => _state.ShowingUpgrades && _state.TryOf(line) is { } t ? t.ItemName : line.Target.Name;

    UpgradeSource Source(string itemId) =>
        Finder?.SourceOf(itemId) ?? new UpgradeSource("-", "", Array.Empty<ItemLine>(), "Where it comes from could not be read.");

    // "In your bank" for what you have, as the issue says it, in the warm colour of "x3" beside the name: a fact, not a
    // verdict. Else the headings of Item Sources' list ("Combat, Smithing").
    (string Text, Color Tint) FromOf(SimResult line)
    {
        if (_state.TryOf(line) is not { } t) return ("-", GameStyle.TextDim);
        if (t.InBank > 0) return ("In your bank", GameStyle.TextPaper);
        if (t.Worn > 0) return ("Worn now", GameStyle.TextPaper);
        UpgradeSource from = Source(t.ItemId);
        return (from.Short, from.Lines.Count > 0 ? GameStyle.TextGrey : GameStyle.TextDim);
    }

    double? FromRank(SimResult line) => _state.TryOf(line) is not { } t ? null
        : t.InBank > 0 ? 3 : t.Worn > 0 ? 2 : Source(t.ItemId).Lines.Count > 0 ? 1 : 0;

    // The tooltip of a line's "Comes from": what you have of it, then the headings of Item Sources' list with how many
    // places each; Details has every place (FoundIn).
    string FromTip(UpgradeTry t)
    {
        UpgradeSource from = Source(t.ItemId);
        var said = new List<string>();
        if (t.InBank > 0) said.Add($"{Fmt(t.InBank)} in your bank");
        else if (t.Worn > 0) said.Add("You wear it now");
        said.Add(from.Tip != "" ? from.Tip : from.Say);
        return string.Join("\n\n", said.Where(s => s != ""));
    }

    // ---- middle: the list -----------------------------------------------------------------------------------

    // Before the list is filled, as MatchCompare: the header's cells for the kind of list on show, the words of the line
    // above it and of the button beside it, and the line Details follows.
    void MatchUpgrades(List<SimResult> lines)
    {
        bool on = _state.ShowingUpgrades;
        if (_headerUpgrades != on)
        {
            _headerUpgrades = on;
            _offset = 0;
            ResultsWidth();
            FillHeader();
            _tableSignature = "";
        }
        if (_headers.FirstOrDefault(h => GodotObject.IsInstanceValid(h) && h.GetMeta("column").AsString() == NameKey) is { } name)
            name.Text = on ? "Item" : "Enemy";
        _back.Text = on ? "Back to the fights" : "Back to now";
        _back.TooltipText = on ? "Leave the items tried and show the fights again. Show upgrades, under Details, brings them back."
            : "Leave this earlier scan and show the current results";
        _upgradeOrdered = on ? lines : new List<SimResult>();
        if (!on) return;
        // Details follows the top line until a line is clicked, as with the fights (SimState.Pinned).
        string? key = _state.UpgradePinned && lines.Any(l => l.Key == _state.UpgradeSelected) ? _state.UpgradeSelected
            : lines.FirstOrDefault(l => !LineFailed(l))?.Key ?? lines.FirstOrDefault()?.Key;
        if (key != _upgradeShownKey)
        {
            _upgradeShownKey = key;
            _tableSignature = _detailsSignature = "";
        }
#if DEVTOOLS
        _devBuilt = 0;
#endif
    }

    // In place of "With: ...": the fight, the setup the items go on, and how far the search is; in grey, as the current
    // list's line. Where they do not fit on one line, how far the search is goes on a line of its own (ShowWords). Laid out
    // by its widest words from the first item tried to "Stopped: ..." or "2 items tried, yesterday 03:27", every count as
    // wide as its digits can be (WidestCount), the line keeps its lines from the start of the search to its end.
    void FillUpgradeShowing(List<SimResult> lines)
    {
        UpgradeSearch u = _state.Upgrades!;
        string stance = u.Target.StanceName != "" ? $" ({u.Target.StanceName})" : "";
        static string Trying(string answered, string asked) => $"{answered} of {asked} items tried";
        static string Stopped(string answered, string asked) => "Stopped: " + Trying(answered, asked);
        static string Tried(string asked, string ago) => $"{asked} items tried, {ago}";
        string how = u.Running ? Trying($"{u.Answered}", $"{u.Asked}") : u.Stopped ? Stopped($"{u.Answered}", $"{u.Asked}") : Tried($"{u.Asked}", Ago(u.At));
        string what = $"Upgrades for {u.Target.Name}{stance}, with: {u.SetupLabel}", most = WidestCount(u.Asked);
        var widest = new[] { Trying(most, most), Stopped(most, most) }
            .Concat(AgoWords(u.Running ? DateTime.UtcNow : u.At).Select(ago => Tried(most, ago)));
        ShowWords(new ShownPart(what + Dot + how, what + "\n" + how, widest.Select(words => (what + Dot + words, what + "\n" + words)).ToArray()));
        // What is left, by the estimate the panel gave: "About 1 min left."
        string left = u.Running ? $"\n{Capital(SearchEstimate(Math.Max(1, u.Asked - u.Answered + (u.Baseline == null ? 1 : 0)), u.Options.Hours))} left." : "";
        string now = u.Baseline is not { } b ? "The setup as it is: not simulated yet."
            : b.Failed || b.WearNote != "" ? "The setup as it is could not be simulated here: " + b.Note
            : $"The setup as it is, per hour: {Fmt(b.KillsPerHour)} kills, {Fmt(_state.Xp(b))} {(_state.ShownBest && _state.ShownSkillName != "" ? _state.ShownSkillName + " XP" : "XP")}, " +
              $"{Fmt(b.GoldPerHour)} gold, {(b.Deaths == 0 ? "no deaths" : Fmt(b.DeathsPerHour) + " deaths")}.";
        int cannot = u.Tries.Count(t => !t.Tried);
        _showing.TooltipText = $"Tried: {u.Chosen}, one item at a time on top of {u.SetupLabel}, against {u.Target.Name}{stance},\n" +
                               $"each from the same moment of your character and with the same luck as the setup as it is.\n" +
                               (cannot > 0 ? $"Also listed: {cannot} you cannot wear yet, which are not tried.\n" : "") + now + left;
        if (lines.Count > 0) return;
        _empty.Text = u.Running ? "Trying the setup as it is, then each item you chose, one at a time..."
            : u.Stopped && u.Answered == 0 ? "Stopped before the first item was tried: nothing to list.\n\nFind upgrades, under Details, starts again."
            : "Nothing to list.";
    }

    // A line of the items, as BuildRow makes a fight's: its picture and name, how many you have ("x3", as the item picker
    // says it), its slot where the enemy's level is, and its cells. A line without numbers says why beside the name, where
    // it stays in sight when the numbers move sideways: what the item needs in grey (the issue: "show that level in grey"),
    // the game's reason the copy could not wear it in gold as a run's note, a fight that failed in red.
    Control BuildUpgradeRow(SimResult line, ButtonGroup group)
    {
#if DEVTOOLS
        if (_devBuilt++ == 0) _devBuild.Restart();
#endif
        UpgradeTry? t = _state.TryOf(line);
        var row = new Button
        {
            ThemeTypeVariation = "OutlineToggleButton", ToggleMode = true, ButtonGroup = group,
            ButtonPressed = line.Key == _upgradeShownKey,
            CustomMinimumSize = new Vector2(0, _s.Px(30)), FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            TooltipText = t == null ? "" : UpgradeTip(t, line),
        };
        row.Pressed += () =>
        {
            _state.UpgradeSelected = _upgradeShownKey = line.Key;
            _state.UpgradePinned = true;
            FillDetails();
        };

        var box = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 6);
        box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.Minsize, _s.Px(8));
        row.AddChild(box);
        box.AddChild(_s.Picture(t?.Icon, 20));
        Label name = _s.Text(t?.ItemName ?? "", UsableTry(line) != null ? GameStyle.TextWhite : GameStyle.TextDim);
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        name.MouseFilter = Control.MouseFilterEnum.Ignore;
        // "..." where the line has no room for it; the line's tooltip starts with the item's name already (UpgradeTip).
        ShortWithTip(name, row);
        box.AddChild(name);
        if (WhyNoNumbers(t, line) is { } why)
        {
            Label words = _s.Text(why.Words, why.Tint, 12);
            words.ClipText = true;
            words.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            words.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            words.MouseFilter = Control.MouseFilterEnum.Ignore;
            box.AddChild(words);
        }
        if (t is { InBank: > 0 })
        {
            Label have = _s.Text("x" + Fmt(t.InBank), GameStyle.TextPaper, 12);
            have.MouseFilter = Control.MouseFilterEnum.Ignore;
            box.AddChild(have);
        }
        Label slot = _s.Text(t?.SlotName ?? "", GameStyle.TextDim, 12);
        slot.CustomMinimumSize = new Vector2(_s.Px(36), 0);
        slot.HorizontalAlignment = HorizontalAlignment.Right;
        slot.MouseFilter = Control.MouseFilterEnum.Ignore;
        box.AddChild(slot);
        box.AddChild(UpgradeCellsOf(line, t, row));
#if DEVTOOLS
        _devBuildMs = _devBuild.Elapsed.TotalMilliseconds;
        _devBuiltRows = _devBuilt;
#endif
        return row;
    }

    // Why a line has no numbers, and its colour; null when it has them.
    (string Words, Color Tint)? WhyNoNumbers(UpgradeTry? t, SimResult line) => t == null ? null
        : !t.Tried ? (t.Needs != "" ? Capital(t.Needs) : "Requirements not met", GameStyle.TextDim)
        : line.Failed ? (line.Note, Bad)
        : line.WearNote != "" ? (line.WearNote, GameStyle.TextGold)
        : null;

    // The cells of a line, in the strip that moves sideways (RowCells); "Comes from" has its own tooltip. A cell cut short
    // with "..." (the best drop's name) starts the line's tooltip whole, as in RowCells.
    Control UpgradeCellsOf(SimResult line, UpgradeTry? t, Control row)
    {
        Control strip = NumbersStrip(out HBoxContainer cells);
        List<SimColumn> shown = Shown();
        foreach (SimColumn column in shown.Take(shown.Count - 1))
        {
            Label cell = Cell(column, line);
            if (column.Trim) ShortWithTip(cell, row, column.Name);
            cells.AddChild(cell);
        }
        Label from = Cell(shown[^1], line);
        // Its own tooltip, the headings of Item Sources' list; a click still reaches the line.
        if (t != null)
        {
            from.TooltipText = FromTip(t);
            from.MouseFilter = Control.MouseFilterEnum.Pass;
        }
        cells.AddChild(from);
        _rowCells.Add(cells);
        return strip;
    }

    // One cell, as RowCells makes them.
    Label Cell(SimColumn column, SimResult line)
    {
        Label cell = _s.Text(column.Text(line), column.Tint(line), 14, GameStyle.SemiBold);
        cell.HorizontalAlignment = HorizontalAlignment.Right;
        cell.CustomMinimumSize = new Vector2(_s.Px(column.Width), 0);
        cell.MouseFilter = Control.MouseFilterEnum.Ignore;
        if (column.Trim)
        {
            cell.ClipText = true;
            cell.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        }
        return cell;
    }

    static string Capital(string words) => words == "" ? words : char.ToUpperInvariant(words[0]) + words[1..];

    // "You do not meet its requirements: Level 40 Attack." with the game's words, the sentence alone without them.
    static string NotMet(UpgradeTry t, string its) =>
        t.Needs != "" ? $"You do not meet {its} requirements: {t.Needs}." : $"You do not meet {its} requirements.";

    // The tooltip of a line: the item, its slot and what it goes on in place of, what you have of it, the stance it fought
    // in when that is not the one of the setup as it is, then why it has no numbers or the note of its fight, the 95%
    // ranges of its numbers and, where the same dice decided (issue #63), each change with its ±, three to a line, as
    // Compare's line tooltip gives each lead (PairTip). Without "could be luck": the grey of the cell says it.
    //   The change ± luck, with the same dice
    //   Kills +14% ± 9.1%  ·  XP +12% ± 8.6%  ·  Gold +6.2% ± 13%
    string UpgradeTip(UpgradeTry t, SimResult line)
    {
        var said = new List<string>
        {
            !t.Tried ? $"{t.ItemName}, for the {t.SlotName} slot"
            : t.InPlaceOf == EmptySlot ? $"{t.ItemName} in the empty {t.SlotName} slot" : $"{t.ItemName} in the {t.SlotName} slot, {t.InPlaceOf}",
        };
        if (t.InBank > 0) said.Add($"{Fmt(t.InBank)} in your bank");
        else if (t.Worn > 0) said.Add("You wear it now");
        if (!t.Tried) said.Add(TipLines(NotMet(t, "its") + " The game does not let the copy wear it, so it is not tried."));
        else
        {
            if (OtherStance(line, _state.Upgrades?.Baseline) is { } stance) said.Add(stance);
            if (line.Note != "") said.Add(TipLines(line.Note));
            if (UsableTry(line) is { } r) said.AddRange(RangeLines(r, "95% ranges with it"));
            var changes = BaseShown().Select(c => GainOf(c, line) is { Plus: { } plus } g ? $"{TitleOf(c)} {(g.Change != null ? g.Change + " " : "")}{plus}" : null)
                .OfType<string>().ToList();
            if (changes.Count > 0)
            {
                said.Add("The change ± luck, with the same dice");
                said.AddRange(changes.Chunk(3).Select(three => string.Join("  ·  ", three)));
            }
        }
        return string.Join("\n", said);
    }

    // An item that is a weapon of another kind fights in another stance than the setup as it is (issue #51): the stance it
    // fought in, and the setup's. Null when it is the same one, before the setup as it is has a result, or when the item's
    // result does not know its stance.
    static string? OtherStance(SimResult line, SimResult? now) =>
        now == null || line.StanceShown == "" || SameStance(line, now) ? null
        : StanceTipLine(line) + (now.StanceShown != "" ? $"\nThe setup as it is: {StanceWords(now)}" : "");

    // ---- right: the line clicked ----------------------------------------------------------------------------

    /// <summary>
    /// From FillDetails: the item of the line Details follows, against the setup as it is, and where it comes from. False
    /// when the items are not on show or there is no line yet: Details then shows the fight.
    /// </summary>
    bool FillUpgradeDetails()
    {
        if (!_state.ShowingUpgrades || _state.Upgrades is not { } u) return false;
        SimResult? line = _upgradeOrdered.FirstOrDefault(l => l.Key == _upgradeShownKey);
        if (line == null || _state.TryOf(line) is not { } t) return false;
        // Fight stays the fight the items are tried for, with what the left shows, as from the list of the fights.
        FillFight(_state.Results.GetValueOrDefault(SimResult.MakeKey(_state.SetupKey, u.Target.Key)) ?? u.Baseline);
        UpgradeSource from = Source(t.ItemId);
        string signature = $"up#{u.Id}#{line.Key}{line.RealMilliseconds}#{u.Baseline?.RealMilliseconds}#{from.Short}#{_state.ShownSkillId}#{string.Join(",", _state.Columns)}";
        if (signature == _detailsSignature) return true;
        _detailsSignature = signature;
        foreach (Node old in _details.GetChildren()) old.QueueFree();

        // What, where it goes, and for which fight: the head of a fight's Details, with the item's picture. Every line whole:
        // an item's name, what it goes on in place of and the setup's name with its stance can be long, and a line that
        // does not fit parts at its dot, the slot over what the item goes on in place of, the fight over the setup (OneOrTwo).
        var head = _s.Spaced(new HBoxContainer(), 10);
        head.AddChild(HeadPicture(t.Icon));
        var title = _s.Spaced(new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, 0);
        title.AddChild(Whole(_s.Text(t.ItemName, GameStyle.TextWhite, 16, GameStyle.Bold)));
        string stance = u.Target.StanceName != "" ? $"{u.Target.StanceName}  ·  " : "";
        foreach (var (first, then, tint) in new[]
                 {
                     (t.SlotName, t.Tried ? t.InPlaceOf : "", GameStyle.TextGrey),
                     (u.Target.Name, stance + u.SetupLabel, GameStyle.TextDim),
                 })
        {
            string words = then == "" ? $"{first} slot" : $"{first}  ·  {then}";
            Label small = then == "" ? Whole(_s.Text(words, tint, 12)) : OneOrTwo(_s.Text("", tint, 12), words, $"{first}\n{then}");
            small.TooltipText = words;
            small.MouseFilter = Control.MouseFilterEnum.Pass;
            title.AddChild(small);
        }
        // Under the setup, the stance the item fought in when it is not the setup's own: a weapon of another kind (issue #51).
        bool otherStance = t.Tried && OtherStance(line, u.Baseline) != null;
        if (otherStance && StanceLine(line, fight: false) is { } itsStance) title.AddChild(itsStance);
        head.AddChild(title);
        _details.AddChild(head);

        SimResult? trial = UsableTry(line), now = u.Baseline is { Failed: false, WearNote: "" } b ? b : null;
        // Why it has no numbers, or the note of its fight: as a fight's note under its name; what it needs in grey, as in
        // the list.
        if (!t.Tried) _details.AddChild(Wrapped(NotMet(t, "this item's") + " The game does not let the copy wear it, so it is not tried.", 250, GameStyle.TextGrey, 12));
        else if (line.Note != "") _details.AddChild(Wrapped(line.Note, 250, line.Failed ? Bad : GameStyle.TextGold, 12));
        if (t.Tried && now == null && u.Baseline != null) _details.AddChild(Wrapped("The setup as it is could not be simulated here: " + u.Baseline.Note, 250, Bad, 12));

        // Column by column, as the line says it, with the two numbers under it.
#if DEVTOOLS
        _devRowNotes.Clear();
#endif
        if (trial != null && now != null)
        {
            _details.AddChild(_s.Section("Compared with"));
            // Its name, with its stance, under the words when the two do not fit on one line (OneOrTwo).
            Label versus = OneOrTwo(_s.Text("", GameStyle.TextGrey, 12), $"The setup as it is: {u.SetupLabel}", $"The setup as it is:\n{u.SetupLabel}");
            // Its numbers here are the search's own fight of it, not its line in the list of fights.
            versus.TooltipText = versus.Text + "\nFought again for this search, with the same luck as every item: its numbers can differ\n" +
                                 "a little from its line in the list of fights, which had dice of its own.";
            versus.MouseFilter = Control.MouseFilterEnum.Pass;
            _details.AddChild(versus);
            // Its stance under its name, as the item's is under the item's, when the two fought in different ones.
            if (otherStance && StanceLine(now, fight: false) is { } nowStance) _details.AddChild(nowStance);
            foreach (SimColumn c in BaseShown())
            {
                if (GainOf(c, line) is not { } g) continue;
                // Under the two numbers, where the same dice decided (issue #63), how far luck could move the change, after
                // "could be luck" where it is that, as "Compared with" writes it (SameDiceNote): "± 9.1%" under "+14%"; for a
                // number without a range why it could be luck, "could be luck, no range", and for a change green or red by
                // anything but the ranges or the same dice what decided it, "by the deaths" (issue #67, Decided). Where the
                // line does not fit, the two numbers over what decided (SmallLine parts it at its dot).
                string? said = g.Verdict is { } v ? Decided(v, g.By) : null;
                string? judged = g.Plus is { } plus ? (said != null ? $"{said}, {plus}" : plus) : said;
                string note = $"{c.Text(trial)} with it, {c.Text(now)} now" + (judged != null ? Dot + judged : "");
                _details.AddChild(Stat(NameOf(c), g.Change ?? g.Text, g.Tint, note));
#if DEVTOOLS
                _devRowNotes[NameOf(c)] = note;
#endif
            }
        }

        // "Found in", as the Item Encyclopedia's Details and Item Sources' tooltip call it.
        _details.AddChild(_s.Section("Found in"));
        if (t.InBank > 0) _details.AddChild(Stat("In your bank", "x" + Fmt(t.InBank), GameStyle.TextPaper));
        else if (t.Worn > 0) _details.AddChild(Stat("You wear it now", "x" + Fmt(t.Worn), GameStyle.TextPaper));
        if (from.Lines.Count == 0)
        {
            Label say = Wrapped(from.Say, 250, GameStyle.TextGrey, 13);
            say.HorizontalAlignment = HorizontalAlignment.Center;
            _details.AddChild(say);
        }
        else FoundIn(from.Lines);

        if (trial != null)
        {
            // Both ways a change is judged (SimRanges.Judged), the second only where the item and the setup as it is were
            // paired (issue #63), as Compare's foot names them; and where a line above is green or red by anything else
            // (issue #67), that the line says what decided it, which it does under its numbers (Decided, ColourFoot), so that
            // the foot is true of every colour above it. A line here has the verdict's colour alone (Adds), where Compare's two
            // numbers are green beside red. Grey is not said, in either foot: its lines say "could be luck" themselves, and why
            // where they have no range.
            bool lineSays = BaseShown().Any(c => GainOf(c, line) is { Verdict: Verdict.Better or Verdict.Worse } g && Reason(g.By) != null);
            string rule = now is { Spread: not null } && trial.Spread != null
                ? " " + ColourFoot("Green and red", "the change", SimRanges.SameDice(trial, now), lineSays)
                : "";
            Label foot = Wrapped($"{Clock(trial.Hours * 3600)} of fighting simulated with it, from the same moment and with the same luck as the setup as it is, put on as a pick in the {t.SlotName} slot puts it on.{rule}",
                250, GameStyle.TextDim, 11);
            foot.HorizontalAlignment = HorizontalAlignment.Center;
            _details.AddChild(new Control { CustomMinimumSize = new Vector2(0, _s.Px(4)), MouseFilter = Control.MouseFilterEnum.Ignore });
            _details.AddChild(foot);
#if DEVTOOLS
            _devFoot = foot;
            _devFootLines = BaseShown().Select(c => (Name: NameOf(c), Gain: GainOf(c, line))).Where(x => x.Gain is { Verdict: not null })
                .Select(x => (x.Name, x.Gain!.Value.Verdict!.Value, x.Gain.Value.By, _devRowNotes.GetValueOrDefault(x.Name))).ToList();
#endif
        }
        return true;
    }

    // Item Sources' answer in the Item Encyclopedia's "Found in" (EncyclopediaView.Lines and LineRow): a heading per group
    // with its picture, and a stat line per place with its picture, name, how many and how likely; as many as Item
    // Sources' tooltip shows (ItemLines.MaxLines, MaxPerGroup).
    void FoundIn(IReadOnlyList<ItemLine> lines)
    {
        bool chances = lines.Any(l => l.Chance < 0.9995);
        int written = 0, leftOut = 0;
        foreach (var group in lines.GroupBy(l => l.Group))
        {
            var members = group.ToList();
            if (written >= ItemLines.MaxLines) { leftOut += members.Count; continue; }
            string plain = group.Key.Replace(" ", "");
            Texture2D? icon = GameStyle.Icon(plain) ?? (GroupIcons.TryGetValue(plain, out string? borrowed) ? GameStyle.Icon(borrowed) : null);
            var heading = _s.Spaced(new HBoxContainer { CustomMinimumSize = new Vector2(0, _s.Px(24)) }, 6);
            if (icon != null) heading.AddChild(_s.Picture(icon, 16));
            heading.AddChild(_s.Text(members.Count > 1 ? $"{group.Key}  ({members.Count})" : group.Key, GameStyle.TextDim, 14, GameStyle.Bold));
            _details.AddChild(heading);
            var box = _s.Spaced(new VBoxContainer(), 2);
            _details.AddChild(box);
            int shown = Math.Min(members.Count, Math.Min(ItemLines.MaxPerGroup, ItemLines.MaxLines - written));
            // The rows of the heading that can part at their dot take one form together (FitParted).
            var parted = new List<PartedRow>();
            foreach (ItemLine line in members.Take(shown)) box.AddChild(SourceRow(line, chances, icon, parted));
            written += shown;
            if (members.Count > shown) box.AddChild(_s.Text($"and {members.Count - shown} more", GameStyle.TextDim, 12));
        }
        if (leftOut > 0) _details.AddChild(_s.Text($"and {leftOut} more elsewhere", GameStyle.TextDim, 12));
    }

    // The headings that are not skills borrow a fitting picture, as the Item Encyclopedia's do (EncyclopediaView.GroupIcons).
    static readonly Dictionary<string, string> GroupIcons = new()
    {
        ["Opening"] = "OpenChest256", ["Upgrading"] = "upgrade", ["ItemUpgrade"] = "upgrade", ["Items"] = "Bank",
        ["MagicSpell"] = "Magic", ["Imbuement"] = "MagicImbuements", ["ArtisanRecipe"] = "SkillIcon",
    };

    // "[icon] Name  ·  where              5-10   22%"
    // Whole at every size (issue #59): where the name and the place after its dot do not fit on one line, the name keeps
    // the row and the place goes on a small grey line under it, as the Item Encyclopedia's "Found in" has an enemy's areas
    // (EncyclopediaView.LineRow and Small), and so do the other rows of its heading (FitParted); a name still too wide
    // wraps on (Whole). The words are Item Sources' own (ItemLines.Describe), whole in the tooltip.
    Control SourceRow(ItemLine line, bool chances, Texture2D? groupIcon, List<PartedRow> heading)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        panel.AddThemeStyleboxOverride("panel", _s.StatLine());
        var rows = _s.Spaced(new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass }, 2);
        panel.AddChild(rows);
        var top = _s.Spaced(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass }, 6);
        rows.AddChild(top);
        top.AddChild(_s.Picture(line.Icon ?? groupIcon, 20));
        Label what = Whole(_s.Text(line.Name));
        what.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        what.TooltipText = line.Name;
        what.MouseFilter = Control.MouseFilterEnum.Pass;
        top.AddChild(what);
        Label? where = null;
        if (Parted(line) is (string name, string place))
        {
            // Under the name: past the picture and the gap after it, as the Encyclopedia's small line is.
            var under = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Pass, Visible = false };
            under.AddThemeConstantOverride("margin_left", _s.Px(20) + top.GetThemeConstant("separation"));
            where = Whole(_s.Text(place, GameStyle.TextGrey, 12));
            where.TooltipText = line.Name;
            where.MouseFilter = Control.MouseFilterEnum.Pass;
            under.AddChild(where);
            rows.AddChild(under);
            heading.Add(new PartedRow(line, what, under, name));
            what.Resized += () => FitParted(heading);
            what.ThemeChanged += () => FitParted(heading);
        }
#if DEVTOOLS
        _devSourceRows.RemoveAll(r => !GodotObject.IsInstanceValid(r.Name));
        _devSourceRows.Add((line, what, where));
#endif
        if (line.Quantity != "") top.AddChild(_s.Text(line.Quantity, GameStyle.TextGrey, 13));
        if (chances)
        {
            bool always = line.Chance >= 0.9995;
            Label chance = _s.Text(ItemLines.Percent(line.Chance), always ? GameStyle.TextDim : GameStyle.TextWhite, always ? 13 : 14, always ? null : GameStyle.Bold);
            chance.HorizontalAlignment = HorizontalAlignment.Right;
            chance.CustomMinimumSize = new Vector2(_s.Px(54), 0);
            if (ItemLines.Luck(line) is string luck)
            {
                chance.TooltipText = luck;
                chance.MouseFilter = Control.MouseFilterEnum.Pass;
            }
            top.AddChild(chance);
        }
        return panel;
    }

    // A "Found in" row that can part at its dot: its line, the label of its name, the small line under it, and the name alone.
    sealed record PartedRow(ItemLine Line, Label Name, Control Under, string Alone);

    // One form for every row of a heading that can part, as the Item Encyclopedia gives every enemy row of its "Found in"
    // the same: the place after the dot on the row while every one of them fits there, and on a small grey line under the
    // name on every one as soon as one does not. Measured again whenever a row's width changes; the rows are in Details,
    // which scrolls, so nothing else moves for it.
    static void FitParted(List<PartedRow> rows)
    {
        rows.RemoveAll(r => !GodotObject.IsInstanceValid(r.Name) || !GodotObject.IsInstanceValid(r.Under));
        if (rows.Count == 0 || rows.Any(r => r.Name.Size.X <= 0)) return;
        static bool Fits(PartedRow r) =>
            r.Name.GetThemeFont("font").GetStringSize(r.Line.Name, HorizontalAlignment.Left, -1, r.Name.GetThemeFontSize("font_size")).X
            <= r.Name.Size.X - r.Name.GetThemeStylebox("normal").GetMinimumSize().X;
        bool one = rows.All(Fits);
        foreach (PartedRow r in rows)
        {
            string words = one ? r.Line.Name : r.Alone;
            if (r.Name.Text != words) r.Name.Text = words;
            if (r.Under.Visible == one) r.Under.Visible = !one;
        }
    }

    // An enemy's or a dungeon's line in two parts at its dot, "Black Slime" and "Slime Forest", "Hobgolbin Camp" and
    // "Dungeon reward" (the words after the dot start with a capital on a line of their own); null for any other line,
    // which has no dot.
    static (string Name, string Place)? Parted(ItemLine line)
    {
        int dot = line.Group == "Combat" ? line.Name.IndexOf(Dot, StringComparison.Ordinal) : -1;
        return dot > 0 && dot + Dot.Length < line.Name.Length ? (line.Name[..dot], Capital(line.Name[(dot + Dot.Length)..])) : null;
    }

#if DEVTOOLS
    // ---- development ----------------------------------------------------------------------------------------

    public Button DevUpgradeButton => _upgradeButton;

    /// <summary>Development: the estimate the panel gives for that many fights, at the duration chosen.</summary>
    public string DevUpgradeEstimate(int fights) =>
        SearchEstimate(fights, SimState.Durations[Math.Clamp(_state.Duration, 0, SimState.Durations.Length - 1)].Hours);

    // In the picker field of sim.request: "upgrades" logs what the list, its header and Details show, a moment later;
    // "uphover=<part of an item name>" rests the pointer on that line's "Comes from" (DevUpgradeLine), so that the
    // picture of the request, a second later, has its tooltip.
    bool DevUpgrades(string request)
    {
        if (request == "upgrades") _window.Root.GetTree().CreateTimer(0.3).Timeout += DevLogUpgrades;
        else if (request.StartsWith("uphover=", StringComparison.Ordinal)) DevLog?.Invoke("Dev: upgrades: " + DevUpgradeLine("hover=" + request[8..]));
        else return false;
        return true;
    }

    /// <summary>Development: a press of the button (find, stop) or of Back to the fights, as a click does.</summary>
    public string DevUpgradePress(string what)
    {
        Button? button = what switch
        {
            "find" or "stop" => _upgradeButton,
            "back" => _back,
            _ => null,
        };
        string before = button == null ? "" : $"'{button.Text}' ({(button.Visible ? "shown" : "hidden")}, {(button.Disabled ? "greyed" : "can be pressed")})";
        bool wanted = what switch
        {
            "find" => _upgradeButton.Text == "Find upgrades" || _upgradeButton.Text == "Show upgrades",
            "stop" => _upgradeButton.Text == "Stop",
            "back" => _back.Visible,
            _ => false,
        };
        if (button == null || !wanted || !button.IsVisibleInTree() || button.Disabled)
            return $"{what}: not pressed, the button is {before} (tooltip '{button?.TooltipText.Replace("\n", " | ")}')";
        button.EmitSignal(BaseButton.SignalName.Pressed);
        return $"{what}: pressed {before}; now '{button.Text}'; {(_state.Upgrades is { } u ? $"search {u.Id} {(u.Running ? "running" : "not running")}" : "no search")}, " +
               $"list {(_state.ShowingUpgrades ? "on show" : "not on show")}, panel {(_pickerKind == UpgradePanel ? "open" : "closed")}";
    }

    /// <summary>Development: "line=<item>" presses that line; "hover=<item>" rests the pointer on its "Comes from" and logs the tooltip a moment later.</summary>
    public string DevUpgradeLine(string what)
    {
        bool hover = what.StartsWith("hover=", StringComparison.Ordinal);
        string item = what[(what.IndexOf('=') + 1)..].Trim();
        Button? row = _rows.GetChildren().OfType<Button>().Where(b => !b.IsQueuedForDeletion())
            .FirstOrDefault(b => b.FindChildren("*", "Label", true, false).OfType<Label>().FirstOrDefault()?.Text.Contains(item, StringComparison.OrdinalIgnoreCase) == true);
        if (row == null || !_state.ShowingUpgrades) return $"{what}: no line of an item called {item} in the list on show";
        if (!hover)
        {
            row.EmitSignal(BaseButton.SignalName.Pressed);
            row.ButtonPressed = true;
            return $"{what}: pressed the line of {item}; Details follows {_upgradeShownKey}";
        }
        Label? from = row.FindChildren("*", "Label", true, false).OfType<Label>().LastOrDefault();
        if (from == null || !from.IsVisibleInTree()) return $"{what}: the line of {item} has no Comes from";
        // "Comes from" is the last column: the numbers move sideways to their end, as the bar under them would.
        _offset = _cellsWidth;
        ApplyOffset();
        string words = from.Text, tip = from.TooltipText;
        // Once the numbers are drawn where they were moved to, as a player's pointer; the tooltip comes up after its delay.
        _window.Root.GetTree().CreateTimer(0.2).Timeout += () =>
        {
            if (!_window.IsAlive || !GodotObject.IsInstanceValid(from)) return;
            Vector2 spot = _window.Root.GetTree().Root.GetFinalTransform() * from.GetGlobalRect().GetCenter();
            Input.ParseInputEvent(new InputEventMouseMotion { Position = spot, GlobalPosition = spot });
            DevLog?.Invoke($"Dev: upgrades: the pointer is on '{words}' at {from.GetGlobalRect()}, the numbers moved {_offset:0} of {MostOffset:0}");
        };
        _window.Root.GetTree().CreateTimer(1.2).Timeout += () =>
        {
            if (_window.IsAlive) DevLog?.Invoke("Dev: upgrades: tooltip up: " + GameStyle.DevTooltips(_window.Root.GetTree().Root).Replace("\n", " | "));
        };
        return $"{what}: the pointer goes onto '{words}' of {item}; its tooltip says: {tip.Replace("\n", " | ")}";
    }

    /// <summary>
    /// Development, at the end of a search: the setup as it is against its two checks (UpgradeSearch.DevAgain, DevLuck).
    /// "Again" number for number: with the same dice it must be the very same, Low HP included; and as it is paired with
    /// the setup as it is as an item is (issue #63), SAME DICE, a share of luck left of 0 in every column the same dice
    /// judge (the bar lowered by 100%), and column by column the words the list would give it ("no better", "the same").
    /// "Luck" by the list's one rule in every column of the list, those without a rate too (issue #67): a number of one
    /// moment is judged as the window judges it, so its difference must read as could be luck, with what decided it
    /// (DevNoRange). Its dice are its own, so nothing pairs it: its rates are judged by the two own ranges, as before.
    /// </summary>
    public string DevUpgradeTwins(UpgradeSearch u)
    {
        if (u.Baseline is not { Failed: false, WearNote: "" } b) return "the setup as it is has NO RESULT to check" + (u.Baseline != null ? ": " + u.Baseline.Note : "");
        static string Numbers(SimResult r) =>
            $"kills {r.Kills}, deaths {r.Deaths}, food {r.FoodEaten}, damage {r.DamageTaken}, healed {r.Healed}, lowest HP {r.LowestHp}, " +
            $"xp {r.Xp.Values.Sum():0.##}, gold {r.GoldPerHour * r.Hours:0.##}, {r.Hours:0.####} h";
        static string Without(SimResult? r) => r == null ? "NO RESULT" : r.Failed ? "FAILED " + r.Note : "NOT WORN AS ASKED: " + r.WearNote;
        static string F(double v) => v.ToString("0.###");
        var said = new List<string>();
        if (u.DevAgain is { Failed: false, WearNote: "" } again)
        {
            string mine = Numbers(again), now = Numbers(b);
            said.Add($"again ({u.DevAgainWhat}), with the dice of the setup as it is: " + (mine == now
                ? $"{mine}; THE SAME NUMBERS: the items are fought with the same luck as the setup as it is"
                : $"{mine} against {now}; DIFFERENT: an item is not fought with the same luck as the setup as it is (compare their 'Dev: dice of' and 'Dev: result' lines)"));
            // Paired as an item is: what the list would show for it, column by column, and what each went by.
            var words = BaseShown().Select(c =>
            {
                Gained g = Adds(c, again, b);
                string rule = c.Value(again) is { } x && c.Value(b) is { } y && DevNoRange(c, again, b, x, y) is { } unranged ? unranged
                    : c.Rate?.Invoke() is { } rate
                        ? SimRanges.Apart(again, b, rate) is { } shared ? SimRanges.DevBar(shared, F) : $"RULE AS BEFORE ({SimRanges.DevWhy(again, b, rate).Or("-")})"
                        : "no rate";
                return $"{c.Key} '{g.Text}' ({g.Verdict?.ToString() ?? "-"}; {rule})";
            });
            said.Add($"again, paired with the setup as it is as an item is: {(SimRanges.SameDice(again, b) ? "SAME DICE" : "NOT PAIRED")}; " + string.Join(", ", words));
        }
        else said.Add("again: " + Without(u.DevAgain));
        if (u.DevLuck is { Failed: false, WearNote: "" } luck)
        {
            // Every column with a number on both sides, the worst there is included, by the rule the window uses.
            var judged = BaseShown().Where(c => c.Value(luck) != null && c.Value(b) != null)
                .Select(c => (Column: c, X: c.Value(luck)!.Value, Y: c.Value(b)!.Value)).Select(v => (v.Column, v.X, v.Y, Verdict: PairVerdict(v.Column, luck, b, v.X, v.Y))).ToList();
            var off = judged.Where(v => v.Verdict is Verdict.Better or Verdict.Worse).Select(v => v.Column.Key).ToList();
            var neither = judged.Where(v => v.Verdict == Verdict.Differs).Select(v => v.Column.Key).ToList();
            // Its dice are its own: nothing pairs it, so its rates are judged by the two own ranges, as before.
            said.Add("luck, the setup as it is with dice of its own, kept in step, " +
                     (SimRanges.SameDice(luck, b) ? "PAIRED WITH THE SETUP AS IT IS THOUGH ITS DICE ARE ITS OWN" : "not paired, so judged by the two own ranges as before") + ": " +
                     string.Join(", ", judged.Select(v => $"{v.Column.Key} {v.Verdict} ({v.X:0.####} vs {v.Y:0.####}" +
                                                          (DevNoRange(v.Column, luck, b, v.X, v.Y) is { } unranged ? "; " + unranged : "") + ")")) + "; " +
                     (off.Count == 0 ? "THE SAME OR COULD BE LUCK in every column" + (neither.Count > 0 ? $" (neither way better in {string.Join(", ", neither)})" : "")
                         : $"NOT LUCK in {string.Join(", ", off)}"));
        }
        else said.Add("luck: " + Without(u.DevLuck));
        return string.Join(" || ", said);
    }

    /// <summary>Development: what the list of the items, its header, the line above it and the button show, and Details.</summary>
    public void DevLogUpgrades()
    {
        if (!_window.IsAlive) return;
        const string head = "Dev: upgrades";
        UpgradeSearch? u = _state.Upgrades;
        DevLog?.Invoke($"{head}: the list {(_state.ShowingUpgrades ? "is on show" : "is NOT on show")} (shown {_state.UpgradesShown}, earlier scan {_state.Viewing?.Id ?? "-"}); " +
                       (u == null ? "no search" : $"search {u.Id} for {u.Target.Name} [{u.Target.Key}] with {u.SetupLabel}, chosen {u.Chosen}: {u.Answered} of {u.Asked} items, {u.Tries.Count(t => !t.Tried)} listed without trying, " +
                                                  $"{(u.Running ? "running" : u.Stopped ? "stopped" : "done")}; the setup as it is {(u.Baseline == null ? "not yet" : u.Baseline.Failed ? "FAILED" : $"xp {u.Baseline.XpPerHour:N0}, kills {u.Baseline.KillsPerHour:N0}")}"));
        // Its words by their widest line: two lines where they do not fit on one (ShowWords).
        float showingWords = _showing.Text.Split('\n').Max(line => _showing.GetThemeFont("font").GetStringSize(line, HorizontalAlignment.Left, -1, _showing.GetThemeFontSize("font_size")).X);
        DevLog?.Invoke($"{head}: above the list '{_showing.Text.Replace("\n", " | ")}' ({_showing.Size.X:0} px wide, its words {showingWords:0}, in {_showing.GetLineCount()} {(_showing.GetLineCount() == 1 ? "line" : "lines")}); " +
                       $"its tooltip '{_showing.TooltipText.Replace("\n", " | ")}'; the empty list says '{(_emptyBox.Visible ? _empty.Text.Replace("\n", " | ") : "-")}'");
        DevLog?.Invoke($"{head}: '{_back.Text}' {(_back.Visible ? "shown" : "hidden")}; " +
                       $"the button '{_upgradeButton.Text}' {(_upgradeButton.Visible ? "shown" : "hidden")}, {(_upgradeButton.Disabled ? "greyed" : "can be pressed")}, {_upgradeButton.Size.X:0}x{_upgradeButton.Size.Y:0}; " +
                       $"its tooltip '{_upgradeButton.TooltipText.Replace("\n", " | ")}'");
        List<SimResult> lines = Sorted(_state.Lines());
        DevLog?.Invoke($"{head}: {lines.Count} lines on the list, sorted by {SortedBy() ?? "-"}, {(SortedDescending ? "most" : "least")} first; Details follows {_upgradeShownKey ?? "-"}; " +
                       $"the last {_devBuiltRows} lines were built in {_devBuildMs:0.0} ms");
        // The rows as built, in the list's order (FillRows), for each line's tooltip as it reads.
        var rows = _rows.GetChildren().OfType<Button>().Where(b => !b.IsQueuedForDeletion()).ToList();
        for (int i = 0; i < Math.Min(lines.Count, 40); i++)
        {
            SimResult line = lines[i];
            UpgradeTry? t = _state.TryOf(line);
            var cells = new List<string> { $"{t?.ItemName} [{t?.SlotName}]{(t is { InBank: > 0 } ? " x" + t.InBank : "")}{(WhyNoNumbers(t, line) is { } why ? $" NO NUMBERS: '{why.Words}' ({DevTint(why.Tint)})" : "")}" +
                                            $"; paired with the setup as it is: {(UsableTry(line) is { } r && u?.Baseline is { } b ? (SimRanges.SameDice(r, b) ? "SAME DICE" : "NOT PAIRED") : "-")}" };
            if (UsableTry(line) is { } trial && u?.Baseline is { Failed: false } now)
                foreach (SimColumn c in BaseShown())
                    if (GainOf(c, line) is { } g)
                    {
                        // What the cell shows, what Details writes for it with its ±, and what the verdict went by (DevRuleOf,
                        // with BOTH READ where SimRanges.Percent alone would write the change and its ± alike; for a number
                        // without a range, or the worst there is, what decided it, issue #67).
                        double? tv = c.Value(trial), nv = c.Value(now);
                        string rule = tv is { } x && nv is { } y ? DevRuleOf(c, trial, now, x, y, g.Change?.EndsWith('%') == true) : "no two numbers";
                        cells.Add($"{c.Key}: '{g.Text}' ({DevTint(g.Tint)}, {g.Verdict?.ToString() ?? "-"}; {tv:0.####} vs {nv:0.####}, sorts as {g.Sort:0.####}); " +
                                  $"Details '{g.Change ?? g.Text}'{(g.Plus != null ? $" over '{g.Plus}'" : ", no ±")}{(g.Change != null && g.Change != g.Text ? $", where the cell writes '{g.Text}'" : "")}; {rule}");
                    }
            var (fromText, fromTint) = FromOf(line);
            cells.Add($"from '{fromText}' ({DevTint(fromTint)}); its tooltip '{(t == null ? "" : FromTip(t).Replace("\n", " / "))}'");
            DevChunks(head + " line", cells);
            DevLog?.Invoke($"{head} line tooltip: {t?.ItemName}: " + (rows.Count == lines.Count ? $"'{rows[i].TooltipText.Replace("\n", " | ")}'" : $"the {rows.Count} rows on show are NOT THE {lines.Count} LINES"));
        }
        // The widest words of a cell against the cell, in this window's font and pixels.
        if (GameStyle.SemiBold is { } semi)
        {
            var widest = lines.Where(l => !LineFailed(l)).SelectMany(l => Shown().Select(c => (Column: c, Words: c.Text(l))))
                .Select(x => (x.Column, x.Words, Width: semi.GetStringSize(x.Words, HorizontalAlignment.Left, -1, _s.Px(14)).X))
                .OrderByDescending(x => x.Width - _s.Px(x.Column.Width)).FirstOrDefault();
            DevLog?.Invoke($"{head}: cells {GainWidth:0.#} wide at full size ({_s.Px(GainWidth)} px here) for the widest words they can show; " +
                           (widest.Column == null ? "no line with numbers" : $"the tightest is '{widest.Words}' {widest.Width:0.#} px in {_s.Px(widest.Column.Width)} px of {widest.Column.Key}: " +
                           (widest.Width > _s.Px(widest.Column.Width) + 0.01f ? (widest.Column.Trim ? "CUT with ..." : "CUT") : "whole")));
        }
        var said = new List<string>();
        foreach (Node node in _details.GetChildren().Where(n => !n.IsQueuedForDeletion()))
        {
            var labels = node.FindChildren("*", "Label", true, false).OfType<Label>().ToList();
            if (node is Label alone) labels.Insert(0, alone);
            string Said(Label l)
            {
                string tint = l.HasThemeColorOverride("font_color") ? DevTint(l.GetThemeColor("font_color")) : "white";
                return DevPlain(l.Text) + (tint is "white" or "grey" ? "" : $" [{tint}]");
            }
            var words = labels.Where(l => l.Text != "").Select(Said).ToList();
            if (words.Count > 0) said.Add(string.Join(" ~ ", words));
        }
        DevChunks(head + " details", said);
        // Details' "Compared with" as it is drawn (each Stat's name, number and small line, read from the labels), beside the
        // verdict each went by: what the item on show in Details changes, against the setup as it is.
        if (_upgradeOrdered.FirstOrDefault(l => l.Key == _upgradeShownKey) is { } shown && UsableTry(shown) is { } item
            && u?.Baseline is { Failed: false, WearNote: "" } setup)
        {
            var stats = _details.GetChildren().Where(n => !n.IsQueuedForDeletion()).OfType<PanelContainer>()
                .Select(p => p.FindChildren("*", "Label", true, false).OfType<Label>().ToList()).Where(l => l.Count >= 2).ToList();
            var compared = new List<string>();
            foreach (SimColumn c in BaseShown())
            {
                if (GainOf(c, shown) is not { } g) continue;
                var drawn = stats.FirstOrDefault(l => l[0].Text == NameOf(c));
                // The small line as it is laid out, its lines parted by " / " (SmallLine).
                string words = drawn == null ? "NOT DRAWN"
                    : $"shows '{drawn[1].Text}' ({DevTint(drawn[1].GetThemeColor("font_color"))})" + (drawn.Count > 2 ? $" over '{DevPlain(drawn[2].Text)}'" : "");
                string why = c.Value(item) is { } x && c.Value(setup) is { } y ? DevRuleOf(c, item, setup, x, y, g.Change?.EndsWith('%') == true) : "no two numbers";
                // What the drawn small line says of its verdict (issue #67): what decided it, or why it could be luck.
                string says = g.Verdict is { } v && drawn != null ? DevSaid(v, g.By, drawn.Count > 2 ? drawn[2].Text : null) : "";
                compared.Add($"{NameOf(c)}: {g.Verdict?.ToString() ?? "-"}, {words} ({why}){(says != "" ? "; " + says : "")}");
            }
            DevChunks($"{head} details compared ({_state.TryOf(shown)?.ItemName} against the setup as it is)", compared);
            DevLogFoot(head + " details");
        }
        DevLogNoRangeTips(head);
        DevLog?.Invoke($"{head}: Fight '{_fightButton.Text}' {(_fightButton.Visible ? "shown" : "hidden")} for {_fightTarget?.Key ?? "-"}; above it '{_fightNote.Text}'");
        DevLogColumns("upgrades");
    }
#endif
}
