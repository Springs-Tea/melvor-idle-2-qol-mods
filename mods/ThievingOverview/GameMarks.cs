using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2.Components.DropTables;
using GamesByMalcs.Melvor2.Components.ItemOpening;
using GamesByMalcs.Melvor2.Entities.Thieving;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.Stats.Nodes;
using Godot;
using MelvorMods.Shared;

namespace ThievingOverview;

/// <summary>
/// The two things this mod adds to the game's own Thieving screen; nothing of the game's is moved or changed.
/// In View Possible Rewards, how many of each reward the target gives per hour at the character's numbers, in a
/// tooltip on the game's tooltip panel over the quantity of every row and of the coins. The window has no room beside
/// its chances for a column of numbers (its tables end at its edges), and Item Sources already puts its tooltip on the
/// chances themselves (ItemSources/DropWindows.ShowChance, laid over the same way). On the card of the target being
/// stolen from, while that target can knock the character out, the Combat Simulator's "Risky" tag of the enemy cards
/// (EnemyCards.Add), laid over the card just past the target's name as Kills in Combat lays its tag past an enemy's
/// name (KillsInCombatMod.EnsureTag). The row of the HP bar has no free room: the bar, the Eat button, the food and
/// Loot History fill it. A tag that would cover something of the game's or pass the card's edge is left out, with one
/// line in the log. Both are children of the game's nodes they lie on and go with them.
/// </summary>
sealed class GameMarks(GameStyle style, Texture2D? icon, Action<string> report)
{
    const string RateName = "ThievingOverviewRate", WarningName = "ThievingOverviewWarning";

    // The tooltip targets over the quantities of View Possible Rewards, by the label they lie on.
    readonly Dictionary<Label, Control> _rates = new();
    readonly HashSet<Label> _used = new();
    readonly Tempo _recheck = new(1000), _findCard = new(1000);
    Theme? _theme;
    bool _toldRoom, _toldQuantity;
    PanelContainer? _warning;
    Label? _verdict, _rest;
    // The card the tag is on, and what the tag was placed for: the name's text, the tag's place on the screen, and what
    // of the game's it would cover there (null: nothing, it is shown). Looked over again once a second while it is
    // wanted, for what the card shows later.
    ThievingRecipeView? _card;
    (string Text, Rect2 At, string? Covers)? _placed;

#if DEVTOOLS
    /// <summary>Development: shows the tag for this target, whatever the character is doing (a picture of it).</summary>
    public TargetRates? DevPreview;
    public readonly List<string> DevRates = new();
    public string DevWarning = "";
    /// <summary>Development: how often an open View Possible Rewards was looked at.</summary>
    public int DevLooks;
    /// <summary>Development: the tooltip targets that are on the window now, by the label they lie on.</summary>
    public IReadOnlyDictionary<Label, Control> DevTargets => _rates;
    /// <summary>Development: the card the tag is on, if any.</summary>
    public ThievingRecipeView? DevCard => _card;
    /// <summary>Development: the tag, and whether its place on the card has been worked out.</summary>
    public PanelContainer? DevTag => _warning != null && GodotObject.IsInstanceValid(_warning) ? _warning : null;
    public bool DevPlaced => _placed != null;
    /// <summary>Development: the tag is kept off the screen while this is true, for a picture of the card without it.</summary>
    public bool DevHeld { get; private set; }

    public void DevHold(bool hold)
    {
        DevHeld = hold;
        if (hold && DevTag is { } tag) tag.Visible = false;
    }
#endif

    /// <param name="rates">A target's hour: with View Possible Rewards as the game last showed it (true), or from the
    /// game's tables alone (false, only for the development check's comparison).</param>
    public void Update(ThievingActiveView page, ThievingActionComponent action, Func<ActiveThievingRecipe, bool, TargetRates> rates, Units units,
        Dictionary<ThievingRecipe, RewardsReading> readings)
    {
        UpdateRewards(Native.Rewards(page), rates, units, readings);
        UpdateWarning(Native.ActionView(page), action, current => rates(current, true));
    }

    /// <summary>Takes everything away, as when the Thieving page is left or the mod stops.</summary>
    public void Clear()
    {
        _used.Clear();
        RemoveUnused();
        if (_warning != null && GodotObject.IsInstanceValid(_warning)) { _warning.Hide(); _warning.QueueFree(); }
        _warning = null;
        _verdict = _rest = null;
        _card = null;
        _placed = null;
    }

    // ---- View Possible Rewards ----------------------------------------------------------------------------------

    void UpdateRewards(ViewThievingDropsModal window, Func<ActiveThievingRecipe, bool, TargetRates> rates, Units units,
        Dictionary<ThievingRecipe, RewardsReading> readings)
    {
        _used.Clear();
#if DEVTOOLS
        DevRates.Clear();
#endif
        if (window.IsVisibleInTree() && Native.Active(window) is { } active)
        {
#if DEVTOOLS
            DevLooks++;
#endif
            ThievingActionStats s = active.Stats;
            // The window writes the section chances through the game's own formats: proven shares while the numbers
            // it writes are the game's for this target (ItemSources' DropWindows.ShareOf).
            bool common = Shown(Native.CommonChance(window), s.CommonLootChance, "common"), rare = Shown(Native.RareChance(window), s.RareLootChance, "rare"),
                coins = Shown(Native.CoinChance(window), s.CurrencyLootChance, "coin");
            double commonChance = Math.Clamp(s.CommonLootChance.Value, 0, 1), rareChance = Math.Clamp(s.RareLootChance.Value, 0, 1);
            var commonRows = common ? Read(Native.CommonTable(window), commonChance, "common", active.Recipe) : null;
            var rareRows = rare ? Read(Native.RareTable(window), rareChance, "rare", active.Recipe) : null;
            // What the window shows, kept, so the overview counts the same chances while the fortune stays the same.
            bool hasCommon = active.Recipe.CommonDropTable is { Items.Count: > 0 }, hasRare = active.Recipe.RareDropTable is { Items.Count: > 0 };
            if ((!hasCommon || commonRows != null) && (!hasRare || rareRows != null))
            {
                var reading = new RewardsReading { Fortune = s.Fortune.Value };
                if (commonRows != null) reading.Tables["Common"] = commonRows.Select(x => x.Row).ToList();
                if (rareRows != null) reading.Tables["Rare"] = rareRows.Select(x => x.Row).ToList();
                readings[active.Recipe] = reading;
            }
            TargetRates r = rates(active, true);
            string target = active.Recipe.DisplayName.Text;
            // A heist's rewards are not counted (TargetRates.IsHeist): its rows get no tooltip.
            if (r is { Valid: true, IsHeist: false } && units.LootProven)
            {
#if DEVTOOLS
                TargetRates data = rates(active, false);
#endif
                foreach (var (table, rows, chance) in new[] { ("Common", commonRows, commonChance), ("Rare", rareRows, rareChance) })
                    foreach (var (row, cells) in rows ?? [])
                    {
                        double perHour = r.PerHour(chance * row.Share, row.Min, row.Max);
                        string words = Words(row.Item.DisplayName.GetPluralText(2), perHour, target, r.Doubling);
                        Tip(cells.Quantity, words);
#if DEVTOOLS
                        double? Of(TargetRates t) => t.ItemsKnown ? t.Drops.Where(d => ReferenceEquals(d.Item, row.Item) && d.Table == table).Sum(d => d.PerHour) : null;
                        double? listed = Of(r), fromData = Of(data);
                        bool Differs(double? other) => other is { } o && Math.Abs(o - perHour) > 1e-6 * Math.Max(1, perHour);
                        DevRates.Add($"{table} {row.Item.Id}: chance '{cells.Chance.Text}' ({cells.Chance.Value:R}) = {row.Share:R} of the table x {chance:R}, quantity '{cells.Quantity.Text}' read as {row.Min}-{row.Max}, " +
                            $"{r.SuccessesPerHour:0.###} successes/h -> {perHour:0.####}/h; {DevTip(cells.Quantity)}; the window's list {listed:0.####}/h" +
                            (Differs(listed) ? " MISMATCH window" : "") + $", the game's tables alone {fromData?.ToString("0.####") ?? $"not counted ({data.ItemsWhy})"}" +
                            (r.Fortune == 0 && Differs(fromData) ? " MISMATCH tables" : r.Fortune != 0 ? $" (fortune {r.Fortune:0.##}: the window counts what this window shows)" : ""));
#endif
                    }
                if (coins && r.Coin != null && r.CoinsPerHour is > 0 and var coinsPerHour)
                {
                    Label quantity = Native.CoinQuantity(window);
                    Tip(quantity, Words(r.Coin.DisplayName.Text, coinsPerHour, target, 0));
#if DEVTOOLS
                    DevRates.Add($"coins {r.Coin.Id}: chance '{Native.CoinChance(window).Text}' ({s.CurrencyLootChance.Value:R}), quantity '{quantity.Text}', reward {s.CurrencyReward.Value?.MinQuantity}-" +
                        $"{s.CurrencyReward.Value?.MaxQuantity} -> {coinsPerHour:0.###}/h; {DevTip(quantity)}");
#endif
                }
            }
#if DEVTOOLS
            else DevRates.Add($"no numbers: {(r.IsHeist ? "a heist" : !r.Valid ? "no time for a try (" + r.ItemsWhy + ")" : "the loot chances' unit is not proven (" + units.LootWhy + ")")}");
#endif
        }
        RemoveUnused();
    }

    // "Steel Arrows: 155 per hour from Bandit, at your numbers now", in the window's own words for a number per hour
    // (TargetRates.RateWords: under one an hour, "one every 2h 19m").
    static string Words(string what, double perHour, string target, double doubling) =>
        $"{what}: {TargetRates.RateWords(perHour)} from {target}, at your numbers now" +
        (doubling > 0 ? $"\nYour chance to double ({TargetRates.Percent(doubling)}) is counted." : "");

    // The section's chance as the window writes it is the game's own number for this target, in a percent format.
    bool Shown(DoubleFormattedLabel label, ValueStatNode<double> stat, string which)
    {
        if (!Native.WritesShare(label.FormatString))
        {
            report($"View Possible Rewards gets no numbers per hour for the {which} chance: the window does not write it as a percent (its format is '{label.FormatString}').");
            return false;
        }
        if (label.Value != stat.Value)
        {
            report($"View Possible Rewards gets no numbers per hour for the {which} chance: the window shows {label.Value:R} where the game keeps {stat.Value:R}.");
            return false;
        }
        return true;
    }

    // The rows show either their share of the table, and then they add up to 1, or their chance per roll, adding
    // up to the table's chance: ItemSources' DropWindows.Group tells the two apart the same way. A table whose rows
    // add up to neither, or whose items cannot be told, is not read.
    List<(RewardsReading.Row Row, (PercentLabel Chance, Label Quantity) Cells)>? Read(DropTableView table, double tableChance, string which, ThievingRecipe recipe)
    {
        if (!table.IsVisibleInTree()) return null;
        var rows = Native.Rows(table).Where(row => row.IsVisibleInTree() && !Native.Chance(row).IsEmpty).ToList();
        if (rows.Count == 0) return null;
        double sum = rows.Sum(row => Native.Chance(row).Value);
        bool shares = Math.Abs(sum - 1) <= 0.001, perRoll = !shares && tableChance > 0 && Math.Abs(sum - tableChance) <= 0.001 * tableChance;
        if (!shares && !perRoll)
        {
            report($"The {which} rewards of {recipe.Id} get no numbers per hour: their {rows.Count} chances add up to {sum:0.####}, for a table chance of {tableChance:0.####}.");
            return null;
        }
        var read = new List<(RewardsReading.Row, (PercentLabel, Label))>();
        foreach (ItemDropView row in rows)
        {
            if (row.ItemInfoTooltip?.Item is not { } item)
            {
                report($"The {which} rewards of {recipe.Id} get no numbers per hour: a row's item cannot be told.");
                return null;
            }
            PercentLabel chance = Native.Chance(row);
            int least = Math.Max(0, Native.QuantityMin(row)), most = Math.Max(least, Native.QuantityMax(row));
            read.Add((new RewardsReading.Row(item, shares ? chance.Value : chance.Value / tableChance, least, most), (chance, Native.Quantity(row))));
        }
        return read;
    }

    // A tooltip over the whole of a quantity's label, on the game's tooltip panel, as Item Sources lays its tooltip over
    // a chance (DropWindows.ShowChance): an invisible part that stops the pointer, so the row's own item tooltip (the
    // game's) gives way to it there and the two never lie on each other. It draws nothing: the window looks as the game
    // made it. A quantity that is not shown gets none, with one line in the log.
    void Tip(Label quantity, string words)
    {
        if (!quantity.IsVisibleInTree())
        {
            if (!_toldQuantity) report("A reward in View Possible Rewards shows no quantity: its number per hour has no place.");
            _toldQuantity = true;
            return;
        }
        if (!_rates.TryGetValue(quantity, out Control? target) || !GodotObject.IsInstanceValid(target))
        {
            target = new Control { Name = RateName, MouseFilter = Control.MouseFilterEnum.Stop, Theme = _theme ??= style.TooltipTheme() };
            quantity.AddChild(target);
            _rates[quantity] = target;
        }
        _used.Add(quantity);
        var cell = new Rect2(Vector2.Zero, quantity.Size);
        if (target.Position != cell.Position) target.Position = cell.Position;
        if (target.Size != cell.Size) target.Size = cell.Size;
        if (target.TooltipText != words) target.TooltipText = words;
    }

    void RemoveUnused()
    {
        foreach (Label label in _rates.Keys.Where(l => !_used.Contains(l)).ToArray())
        {
            Control target = _rates[label];
            if (GodotObject.IsInstanceValid(target)) { target.Hide(); target.QueueFree(); }
            _rates.Remove(label);
        }
    }

    // ---- the Risky tag on the card of the target being stolen from -----------------------------------------------

    void UpdateWarning(ThievingActionView view, ThievingActionComponent action, Func<ActiveThievingRecipe, TargetRates> rates)
    {
        TargetRates? r = action.IsStarted && action.CurrentRecipe is { } current ? rates(current) : null;
#if DEVTOOLS
        if (DevPreview != null) r = DevPreview;
        bool risky = r is { Safe: false } || DevPreview != null;
#else
        bool risky = r is { Safe: false };
#endif
        ThievingRecipeView? card = risky ? CardOf(view, r!.Active) : null;
        if (card == null)
        {
            if (_warning != null && GodotObject.IsInstanceValid(_warning)) _warning.Visible = false;
            return;
        }
        Label name = Native.CardName(card);
        if (_warning == null || !GodotObject.IsInstanceValid(_warning) || _warning.GetParent() != name)
        {
            if (_warning != null && GodotObject.IsInstanceValid(_warning)) _warning.QueueFree();
            // The tag of the Combat Simulator's enemy cards, with this mod's picture and the verdict where its
            // number is (EnemyCards.Add).
            _warning = style.CountTag(icon, out Label verdict);
            _warning.Name = WarningName;
            _warning.Theme = _theme ??= style.TooltipTheme();   // its tooltip on the game's tooltip panel
            _rest = style.Text("", GameStyle.TextPaper, 12, GameStyle.SemiBold);
            _rest.MouseFilter = Control.MouseFilterEnum.Ignore;
            verdict.GetParent().AddChild(_rest);
            _verdict = verdict;
            _warning.Visible = false;
            name.AddChild(_warning);   // laid over the card past the name's text: no row of the game's makes room for it
            _placed = null;
        }
        Set(_verdict!, "Risky", GameStyle.Bad);
        Set(_rest!, "can knock you out", GameStyle.TextPaper);
        string tip = $"Thieving Overview: {r!.Recipe.DisplayName.Text}\n{r.SafeWords}" +
            (r.MaxHitFromBar ? "\nThe biggest hit is the one the HP bar marks." : "");
        if (_warning.TooltipText != tip) _warning.TooltipText = tip;
        Place(card, name, _warning);
    }

    // The card that shows this target, while it is on the screen. Kept, and looked for again at most once a second
    // when it is gone or shows another target: the cards are not walked four times a second.
    ThievingRecipeView? CardOf(ThievingActionView view, ActiveThievingRecipe active)
    {
        if (_card != null && GodotObject.IsInstanceValid(_card) && _card.IsVisibleInTree() && ReferenceEquals(Native.Active(_card), active)) return _card;
        _card = null;
        if (!_findCard.Due()) return null;
        _card = Native.Cards(view).FirstOrDefault(c => c.IsVisibleInTree() && ReferenceEquals(Native.Active(c), active));
        return _card;
    }

    static void Set(Label label, string text, Color color)
    {
        if (label.Text != text) label.Text = text;
        if (label.GetThemeColor("font_color") != color) label.AddThemeColorOverride("font_color", color);
    }

    // Just past the end of the name's text (GameStyle.PlaceAfterText), when it lies on nothing of the game's that is
    // drawn and stays inside the card; else it is left out, with one line in the log.
    void Place(ThievingRecipeView card, Label name, PanelContainer tag)
    {
        GameStyle.PlaceAfterText(name, tag);
        // A card that has only just been made is not laid out yet: it is looked at again on the next round.
        if (name.Size.X < 1) { tag.Visible = false; return; }
        Rect2 at = tag.GetGlobalRect();
        if (_placed is not { } placed || placed.Text != name.Text || placed.At != at || _recheck.Due())
        {
            string? covers = Covers(card, name, tag, at);
            if (covers != null && !_toldRoom)
            {
                _toldRoom = true;
                report($"The Risky tag on the card of the target you steal from is left out: past the name it would cover {covers}.");
            }
#if DEVTOOLS
            if (_placed?.Covers != covers || _placed?.At != at || DevWarning == "")
                DevWarning = $"card of {Native.Active(card)?.Recipe.Id} {card.GetGlobalRect()}, its name '{name.Text}' {name.GetGlobalRect()} ({name.HorizontalAlignment}, in " +
                    $"{name.GetParent()?.GetType().Name}:{name.GetParent()?.Name}), the tag {tag.Size} at {at}: " + (covers == null ? "shown" : "LEFT OUT, it would cover " + covers);
#endif
            _placed = (name.Text, at, covers);
        }
        bool show = _placed?.Covers == null;
#if DEVTOOLS
        show &= !DevHeld;
#endif
        tag.Visible = show;
    }

    // ---- what the tag would lie on ------------------------------------------------------------------------------

    /// <summary>
    /// What of the game's the tag at <paramref name="at"/> would lie on, or null: something drawn (words where their
    /// text is, a picture, a button, a bar, a panel's background, any part of the game's that draws itself) other than
    /// the name and a background behind the whole name; or the edge of the card, or of a part that cuts it off
    /// (ItemSources' DropWindows.Covers, with the name itself counted as a part that may cut its child off).
    /// </summary>
    static string? Covers(Control card, Label name, Control tag, Rect2 at)
    {
        Rect2 text = TextRect(name);
        if (text.Position.X < -1 || text.End.X > name.Size.X + 1) return "the name itself (it is cut off)";
        // Inside what the card holds (its child that holds the name), so never on the card's frame.
        Control? content = null;
        for (Node? up = name; up != null && up != card; up = up.GetParent())
        {
            if (up is not Control control) continue;
            if (control.ClipContents && !control.GetGlobalRect().Encloses(at)) return $"the edge of {control.GetType().Name} '{control.Name}'";
            if (control != name) content = control;
        }
        if (card.ClipContents && !card.GetGlobalRect().Encloses(at)) return "the edge of the card";
        if (!(content ?? card).GetGlobalRect().Encloses(at)) return "the edge of the card";
        Rect2 behind = name.GetGlobalRect();
        return Find(card);

        string? Find(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child == tag || child.IsQueuedForDeletion() || child is not Control { Visible: true } control) continue;
                bool holdsName = child == name || control.IsAncestorOf(name);
                if (!holdsName && Drawn(control) is { } drawn && drawn.Intersects(at) && !control.GetGlobalRect().Encloses(behind))
                    return $"{control.GetType().Name} '{control.Name}'";
                if ((holdsName || control is not Container || control.GetGlobalRect().Intersects(at)) && Find(control) is { } inside) return inside;
            }
            return null;
        }
    }

    // Where a control draws something, on the screen; null when it draws nothing of its own. Besides words, pictures,
    // buttons, bars and panels, a part of the game's own kind that holds nothing (a bar drawn in pieces, a picture of
    // its own making) counts as drawn all over; one that holds other parts is looked through like a container, and a
    // plain Control, which the game uses to hold and place other parts, draws nothing.
    static Rect2? Drawn(Control control) => control switch
    {
        Label words => words.Text == "" ? null : words.GetThemeStylebox("normal") is null or StyleBoxEmpty ? words.GetGlobalTransform() * TextRect(words) : words.GetGlobalRect(),
        TextureRect picture => picture.Texture != null ? picture.GetGlobalRect() : null,
        BaseButton or Godot.Range or LineEdit or RichTextLabel or NinePatchRect or ColorRect => control.GetGlobalRect(),
        PanelContainer or Panel => control.GetThemeStylebox("panel") is null or StyleBoxEmpty ? null : control.GetGlobalRect(),
        Container => null,
        _ when control.GetType() == typeof(Control) => null,
        _ => control.GetChildren().OfType<Control>().Any(c => c.Visible) ? null : control.GetGlobalRect(),
    };

    // Where a label's one line of text lies inside it, measured as GameStyle.PlaceAfterText measures it.
    static Rect2 TextRect(Label label)
    {
        LabelSettings? settings = label.LabelSettings;
        Font font = settings?.Font ?? label.GetThemeFont("font");
        int size = settings?.FontSize ?? label.GetThemeFontSize("font_size");
        float width = font.GetStringSize(label.Text, HorizontalAlignment.Left, -1, size).X;
        float x = label.HorizontalAlignment switch
        {
            HorizontalAlignment.Center => (label.Size.X - width) / 2,
            HorizontalAlignment.Right => label.Size.X - width,
            _ => 0,
        };
        return new Rect2(x, 0, width, label.Size.Y);
    }

#if DEVTOOLS
    // Where a quantity's tooltip lies, against the quantity's own cell, and what it says.
    string DevTip(Label quantity) => _rates.TryGetValue(quantity, out Control? target) && GodotObject.IsInstanceValid(target)
        ? $"tooltip over the quantity at {target.GetGlobalRect()} (the quantity {quantity.GetGlobalRect()}, in {quantity.GetParent()?.GetType().Name}:{quantity.GetParent()?.Name}" +
          $"{(quantity.GetParent() is Control cell ? " " + cell.GetGlobalRect() : "")}), mouse {target.MouseFilter}: '{target.TooltipText}'"
        : "NO TOOLTIP";
#endif
}
