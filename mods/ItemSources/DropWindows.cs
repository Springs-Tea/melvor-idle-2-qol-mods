using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.CombatLoot;
using GamesByMalcs.Melvor2.Components.DropTables;
using GamesByMalcs.Melvor2.Components.ItemOpening;
using GamesByMalcs.Melvor2.CustomControls;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Entities.Combat.Statistics;
using GamesByMalcs.Melvor2.Entities.Thieving;
using GamesByMalcs.Melvor2.Entities.Thieving.Statistics;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.Stats.Nodes;
using Godot;
using MelvorMods.Shared;
#if DEVTOOLS
using System.Reflection;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Data.DropTables;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.GameMenus;
using GamesByMalcs.Melvor2.GameMenus.Pages;
using MelvorModLoader;
#endif

namespace ItemSources;

/// <summary>
/// The game's own windows that list a target's drops with their chances: View Enemy Stats (its Drops), the
/// enemy drop window, and a thieving target's possible rewards. The game owns the windows and every number on
/// them; this adds two things and moves nothing of the game's. Over the target's name, Kills in Combat's count
/// tag with the game's statistic: the kills, or the successful tries. On each chance, a tooltip worded like the
/// one under "Found in": what that chance means in kills or tries.
/// </summary>
sealed class DropWindows(Game game, GameStyle style, Action<string> log)
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_EnemyName")]
    static extern Label EnemyName(ViewEnemyDropsModal window);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_EnemyIcon")]
    static extern TextureRect EnemyIcon(ViewEnemyDropsModal window);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Drops")]
    static extern CombatDropsContainer Drops(ViewEnemyDropsModal window);
    // View Enemy Stats keeps the enemy it was opened for, also when the player looks at another one mid-fight.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_StatsPanel")]
    static extern ViewEnemyStatsPanel StatsPanel(ViewEnemyStatsModal window);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Enemy")]
    static extern Enemy? BoundEnemy(ViewEnemyStatsPanel panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NameLabel")]
    static extern Label EnemyName(ViewEnemyStatsPanel panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Drops")]
    static extern CombatDropsContainer Drops(ViewEnemyStatsPanel panel);
    // The chance of one "One of the following" table, as its container was given it (SetTable).
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_chance")]
    static extern ref double TableChance(ItemDropTableChanceContainer table);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_DropChance")]
    static extern PercentLabel Chance(ItemDropView row);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Active")]
    static extern ActiveThievingRecipe? Active(ViewThievingDropsModal window);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RecipeName")]
    static extern Label RecipeName(ViewThievingDropsModal window);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CommonDropTableView")]
    static extern DropTableView CommonTable(ViewThievingDropsModal window);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RareDropTableView")]
    static extern DropTableView RareTable(ViewThievingDropsModal window);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CompletionDropTableView")]
    static extern DropTableView CompletionTable(ViewThievingDropsModal window);
    // The section's chance above each table, as the window writes it: "Common (15.00%)".
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CommonDoubleLabel")]
    static extern DoubleFormattedLabel CommonChance(ViewThievingDropsModal window);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RareDoubleLabel")]
    static extern DoubleFormattedLabel RareChance(ViewThievingDropsModal window);

    // What this has put on the game's labels: a tag on a name, a tooltip target on a chance. Each goes away as
    // soon as its label is no longer shown or no longer needs it.
    readonly Dictionary<Label, Control> _added = new();
    readonly HashSet<Label> _used = new();
    // The name each tag was placed for, where, and what of the game's it would have covered there.
    readonly Dictionary<Label, (string Text, Rect2 At, string? Covers)> _placed = new();
    readonly HashSet<string> _told = new();
    readonly HashSet<object> _unreadable = new(ReferenceEqualityComparer.Instance);
    Theme? _theme;
    string? _lastName;
    Texture2D? _lastIcon;
    Enemy? _enemy;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Update(List<PanelContainerPopup> windows, ItemIndex? index)
    {
#if DEVTOOLS
        _index = index;
        _devLooks++;
        _devLooked.Clear();
        _devTables.Clear();
        _devUsed.Clear();
#endif
        _used.Clear();
        foreach (PanelContainerPopup window in windows)
        {
#if DEVTOOLS
            _devLooked.Add(window);
#endif
            switch (window)
            {
                case ViewEnemyStatsModal stats:
                    ViewEnemyStatsPanel panel = StatsPanel(stats);
                    if (BoundEnemy(panel) is { } bound) Safely(bound, () => ShowEnemy(stats, EnemyName(panel), bound, Drops(panel)));
                    break;
                case ViewEnemyDropsModal drops:
                    Label name = EnemyName(drops);
                    if (FindEnemy(name.Text, EnemyIcon(drops).Texture) is { } enemy) Safely(enemy, () => ShowEnemy(drops, name, enemy, Drops(drops)));
                    break;
                case ViewThievingDropsModal thieving:
                    if (Active(thieving) is { } active) Safely(active.Recipe, () => ShowThieving(thieving, active));
                    break;
            }
        }
        RemoveUnused();
    }

    public void Clear()
    {
        _used.Clear();
        RemoveUnused();
    }

    /// <summary>
    /// Every frame, and cheap (the one or two tags there are, no walk): a window that switched to another target
    /// would show the old count until the next look, so a tag hides as soon as its name has changed.
    /// </summary>
    public void HideStale()
    {
        foreach (var (name, placed) in _placed)
            if (_added.TryGetValue(name, out Control? tag) && GodotObject.IsInstanceValid(tag) && tag.Visible
                && (!GodotObject.IsInstanceValid(name) || name.Text != placed.Text)) tag.Visible = false;
    }

    // A window this cannot make sense of is left as the game made it, with one line in the log, and the others go
    // on. A member that is gone after a game update is not caught here: it switches the whole feature off.
    void Safely(INamespacedObject target, Action show)
    {
        if (_unreadable.Contains(target)) return;
        try { show(); }
        catch (Exception e) when (e is not (MissingMemberException or TypeLoadException))
        {
            _unreadable.Add(target);
            log($"The drop window of {target.Id} is left as the game shows it: {e.Message}");
        }
    }

    void Tell(string key, string message)
    {
        if (_told.Add(key)) log(message);
    }

    void RemoveUnused()
    {
        foreach (Label label in _added.Keys.Where(label => !_used.Contains(label)).ToArray())
        {
            Control added = _added[label];
            if (GodotObject.IsInstanceValid(added)) { added.Hide(); added.QueueFree(); }
            _added.Remove(label);
            _placed.Remove(label);
        }
#if DEVTOOLS
        foreach (object key in _lastRead.Keys.Where(k => !GodotObject.IsInstanceValid((GodotObject)k)).ToArray())
            _lastRead.Remove(key);
#endif
    }

    // The enemy drop window keeps no enemy, only its name and picture: the one enemy that has both.
    Enemy? FindEnemy(string name, Texture2D? icon)
    {
        if (name == _lastName && icon == _lastIcon) return _enemy;
        _lastName = name;
        _lastIcon = icon;
        _enemy = null;
        if (icon == null || name.Length == 0) return null;
        var matches = game.Data.GetDataRegistryOutput<Enemy>().GetAllObjects().Where(e =>
            e.DisplayName.Text == name && (e.Sprite == icon || ((IDescribedObject)e).Icon == icon
                || ((IArachnophobiaIcon)e).Icon == icon)).ToList();
        if (matches.Count == 1) _enemy = matches[0];
        else Tell("enemy:" + name, $"The drop window of {name} is left as the game shows it: {matches.Count} enemies have its name and picture.");
        return _enemy;
    }

    void ShowEnemy(Control window, Label name, Enemy enemy, CombatDropsContainer drops)
    {
        if (!game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? entity) || entity is not CombatEntity combat)
        {
            Tell("combat", "Enemy drop windows get no counts or tooltips: the game's Combat was not found.");
            return;
        }
        // The kills as Kills in Combat reads them (KillsInCombatMod.Update), named as the game names the statistic.
        EnemyKilledStatistic killed = combat.Statistics.EnemyKilled;
        double kills = killed.GetValue(enemy);
        Tag(window, name, "Combat", kills, Named(killed.DisplayName, killed.GetFormattedValue(enemy)) ?? $"{enemy.DisplayName.Text}: killed {GameStyle.Times(kills)}", pastRow: true);
        if (!drops.IsVisibleInTree()) return;
        Walk(drops);

        void Walk(Node node)
        {
            if (node.IsQueuedForDeletion() || node is CanvasItem { Visible: false }) return;
            switch (node)
            {
                case ItemDropTableChanceContainer table:
                    Group(enemy, table, Rows(table).ToList(), TableChance(table), "kill", "kills");
                    return;
                case ItemDropView row:
                    // A drop outside a table (always, or a fixed item) shows its own chance per kill.
                    ShowChance(enemy, row, 1, "kill", "kills");
                    return;
            }
            foreach (Node child in node.GetChildren()) Walk(child);
        }
    }

    void ShowThieving(ViewThievingDropsModal window, ActiveThievingRecipe active)
    {
        ThievingRecipe recipe = active.Recipe;
        var thieving = (ThievingEntity)active.Component.Entity;
        var statistics = ((IThievingStatisticsConfig)thieving.Statistics.Config).ThievingRecipeStatistics;
        if (statistics.OfType<SuccessfulThievingActionStatistic>().FirstOrDefault() is { } successful)
        {
            double tries = successful.GetValue(recipe);
            Tag(window, RecipeName(window), "Thieving", tries, Named(successful.DisplayName, successful.GetFormattedValue(recipe))
                ?? $"{recipe.DisplayName.Text}: {tries:N0} successful tries");
        }
        else Tell("successes", "Thieving rewards get no count: the game's statistic of successful tries was not found.");

        // The rows show a chance inside their table (DropTableView.SetDropTable is given no chance). The tables'
        // own chances, with the player's bonuses, are the game's stats, which the window shows above them.
        Section(recipe, CommonTable(window), ShareOf(active.Stats.CommonLootChance, CommonChance(window), "common"));
        Section(recipe, RareTable(window), ShareOf(active.Stats.RareLootChance, RareChance(window), "rare"));
        // What a heist gives once all its steps are done, counted in completions.
        Section(recipe, CompletionTable(window), 1, "completion", "completions");
#if DEVTOOLS
        DevThieving(window, active);
#endif
    }

    void Section(ThievingRecipe recipe, DropTableView table, double? chance, string one = "successful try", string many = "successful tries")
    {
        if (chance is { } c && table.IsVisibleInTree()) Group(recipe, table, Rows(table).ToList(), c, one, many);
#if DEVTOOLS
        else _devTables[table] = table.IsVisibleInTree() ? "no tooltips, its chance is not known as a share (the line 'Thieving rewards: ...' says why)" : "not shown";
#endif
    }

    // A section's chance is the game's stat, with the player's bonuses. Its unit is the one the window writes it
    // in: the label above the section shows the stat through the game's own format, and a .NET percent format
    // ("Common ({0:P2})" writes 0.15 as "15.00%") is given a share of 1. Without that proof, no tooltips.
    double? ShareOf(ValueStatNode<double> stat, DoubleFormattedLabel shown, string which)
    {
        string? format = NumberFormat(shown.FormatString);
        if (format is not ['P' or 'p', ..] || !format[1..].All(char.IsAsciiDigit))
        {
            Tell("unit:" + which, $"Thieving rewards: the {which} chances get no tooltip, the unit of the game's {which} loot chance is not known (the window does not write it as a percent, its format is '{shown.FormatString}').");
            return null;
        }
        // The format proves the unit of the number on the label, so of the stat only while the label shows it.
        if (shown.Value != stat.Value)
        {
            Tell("shown:" + which, $"Thieving rewards: the {which} chances get no tooltip, the window shows {shown.Value:R} for the game's {which} loot chance of {stat.Value:R}.");
            return null;
        }
        // More than 100% always rolls, read as ItemIndex.Walk reads a table's chance.
        return Math.Clamp(stat.Value, 0, 1);
    }

    // The format a label gives its number, read by .NET's own composite formatting: "P2" in "Common ({0:P2})", ""
    // for a plain "{0}". Null when the label writes no number or its format cannot be read.
    static string? NumberFormat(string? composite)
    {
        if (string.IsNullOrEmpty(composite)) return null;
        var seen = new FormatSeen();
        try { _ = string.Format(seen, composite, 0d); }
        catch (FormatException) { return null; }
        return seen.Format;
    }

    sealed class FormatSeen : IFormatProvider, ICustomFormatter
    {
        public string? Format;
        public object? GetFormat(Type? type) => type == typeof(ICustomFormatter) ? this : null;
        string ICustomFormatter.Format(string? format, object? arg, IFormatProvider? provider)
        {
            Format ??= format ?? "";
            return "";
        }
    }

    // One table's rows show either their chance inside the table, and then they add up to 1 as a weighted table's
    // own GetItemChance does, or their chance per roll, adding up to the table's chance. Either way the count is
    // made with the chance per kill or per try. A table whose rows add up to neither is left alone.
    void Group(INamespacedObject owner, Node table, List<ItemDropView> rows, double tableChance, string one, string many)
    {
#if DEVTOOLS
        _devTables[table] = "no rows";
#endif
        if (rows.Count == 0) return;
        double sum = rows.Sum(row => Chance(row).Value);
        double? factor = tableChance is not (>= 0 and <= 1) ? null
            : Math.Abs(sum - 1) <= 0.001 ? tableChance
            : Math.Abs(sum - tableChance) <= 0.001 * tableChance ? 1
            : null;
#if DEVTOOLS
        _devTables[table] = $"{rows.Count} rows add up to {sum:0.####} for a table chance of {tableChance:0.####}, "
            + (factor == null ? "left alone" : factor == 1 ? "each the chance per roll" : "each the chance inside the table");
        DevLog(table, $"Dev: loot table of {owner.Id} ({table.GetType().Name} '{DevWords(table is DropTableView ? table.GetParent() : table)}'): {rows.Count} rows add up to {sum:R}, "
            + $"the table's chance {tableChance:R} -> {(factor == null ? "left alone" : factor == 1 ? "the rows show the chance per roll" : "the rows show the chance inside the table")}; "
            + $"the game's item tooltip opens on {DevTargets(rows[0])}");
#endif
        if (factor is not { } f)
        {
            Tell($"table:{owner.Id}:{rows.Count}:{tableChance:R}", $"A drop table of {owner.Id} gets no tooltips: its {rows.Count} chances add up to {sum:0.####}, for a table chance of {tableChance:0.####}.");
            return;
        }
        foreach (ItemDropView row in rows) ShowChance(owner, row, f, one, many);
    }

    static IEnumerable<ItemDropView> Rows(Node table)
    {
        foreach (Node node in table.GetChildren())
        {
            if (node.IsQueuedForDeletion()) continue;
            if (node is ItemDropView row) yield return row;
            else foreach (ItemDropView inside in Rows(node)) yield return inside;
        }
    }

    void ShowChance(INamespacedObject owner, ItemDropView row, double factor, string one, string many)
    {
        PercentLabel label = Chance(row);
        if (!label.IsVisibleInTree() || label.IsEmpty) return;
        double used = Math.Clamp(label.Value * factor, 0, 1);
#if DEVTOOLS
        _devUsed[label] = used;
#endif
        // The same words as under "Found in" for the same chance, and nothing for a sure drop. When the window
        // shows the chance inside a table, the count is for another number than the one under the pointer: the
        // tooltip says it first.
        string? luck = ItemLines.Luck(used, one, many);
        string? words = luck == null ? null : factor == 1 ? luck : $"{ItemLines.Percent(used)} per {one}.\n{luck}";
#if DEVTOOLS
        Item? item = row.ItemInfoTooltip?.Item;
        ItemRef? indexed = item == null ? null : _index?.For(item)?.Sources.Find(r => ReferenceEquals(r.Owner, owner));
        DevLog(label, $"Dev: loot chance {owner.Id} / {item?.Id}: label '{label.Text}' shown={label.Value:R} factor={factor:R} used={used:R} per {one}, "
            + $"Found in={(_index == null ? "reading" : indexed?.Chance.ToString("R") ?? "no source")}; tooltip: {words?.Replace('\n', ' ') ?? "none"}");
#endif
        if (words == null) return;
        if (!_added.TryGetValue(label, out Control? target) || !GodotObject.IsInstanceValid(target))
        {
            // Stop: while the pointer is on the number, the row's own item tooltip (the game's) gives way to this
            // one, so the two never lie on each other. The mouse wheel still reaches the list under it.
            target = new Control { MouseFilter = Control.MouseFilterEnum.Stop, Theme = _theme ??= style.TooltipTheme() };
            label.AddChild(target);
            _added[label] = target;
        }
        _used.Add(label);
        // Over the number only, not the label's whole width, so the rest of the row is the game's as before.
        Rect2 text = TextRect(label).Intersection(new Rect2(Vector2.Zero, label.Size));
        if (target.Position != text.Position) target.Position = text.Position;
        if (target.Size != text.Size) target.Size = text.Size;
        if (target.TooltipText != words) target.TooltipText = words;
    }

    // "Enemies Killed: 1,204" in the game's own words and number format, when the game names its statistic.
    static string? Named(string? statistic, string value) =>
        string.IsNullOrWhiteSpace(statistic) || statistic.Contains('{') ? null : $"{statistic}: {value}";

    // Kills in Combat's tag (GameStyle.CountTag), laid over the name just past its text as Kills in Combat lays it
    // (KillsInCombatMod.EnsureTag), so no row of the game makes room for it and nothing of the game's moves.
    // Where it would cover something of the game's or reach past the window, it is left out. In an enemy window
    // (pastRow) the name shares its row with the game's own controls, the combat level badge in View Enemy Stats:
    // there the tag goes past the last of them, and is left out where its rectangle touches any control of the
    // game's.
    void Tag(Control window, Label name, string icon, double count, string words, bool pastRow = false)
    {
        if (!_added.TryGetValue(name, out Control? tag) || !GodotObject.IsInstanceValid(tag))
        {
            tag = style.CountTag(GameStyle.Icon(icon), out _);
            tag.Theme = _theme ??= style.TooltipTheme();
            tag.Visible = false;
            name.AddChild(tag);
            _added[name] = tag;
            _placed.Remove(name);
        }
        _used.Add(name);
        GameStyle.CountOf(tag)!.Text = count.ToString("N0");
        if (tag.TooltipText != words) tag.TooltipText = words;
        GameStyle.PlaceAfterText(name, tag);
        // A window that has only just opened is not laid out yet: it is looked at again on the next round.
        if (name.Size.X < 1) { tag.Visible = false; return; }
        if (pastRow) PastRow(name, tag, RowOf(name, out _));
        Rect2 at = tag.GetGlobalRect();
        string text = name.Text;
        if (!_placed.TryGetValue(name, out var placed) || placed.Text != text || placed.At != at)
        {
            List<Control>? under = pastRow ? Under(window, name, tag, at) : null;
            placed = (text, at, (under is [Control first, ..] ? $"{first.GetType().Name} '{first.Name}'" : null) ?? Covers(window, name, tag, at));
            _placed[name] = placed;
            if (placed.Covers != null)
                Tell("room:" + window.GetType().Name, $"The count beside the name in {window.GetType().Name} is left out where it would cover {placed.Covers}.");
#if DEVTOOLS
            Node? holder = name.GetParent();
            log($"Dev: loot tag in {window.GetType().Name}: laid over the name '{text}' ({name.HorizontalAlignment}, {name.Size.X:0}x{name.Size.Y:0}, in {holder?.GetType().Name}:{holder?.Name}) "
                + $"at {at}: {(placed.Covers == null ? "shown" : "left out, it would cover " + placed.Covers)}; tooltip '{words}'; {DevBeside(window, name, tag, at, under, pastRow)}");
#endif
        }
        tag.Visible = placed.Covers == null;
    }

    // The game's other controls on the name's row: the items of the horizontal box that holds the name, and of each
    // horizontal box that holds that one in turn (the outermost is the row). In View Enemy Stats, the level badge.
    static List<Control> RowOf(Label name, out BoxContainer? row)
    {
        var items = new List<Control>();
        row = null;
        Node inside = name;
        while (inside.GetParent() is BoxContainer { Vertical: false } box)
        {
            row = box;
            foreach (Node child in box.GetChildren())
                if (child != inside && child is Control { Visible: true } item && !item.IsQueuedForDeletion()) items.Add(item);
            inside = box;
        }
        return items;
    }

    // Past the right edge of the last of them, by the gap PlaceAfterText leaves after the text, at the same height.
    static void PastRow(Label name, Control tag, List<Control> row)
    {
        float textEnd = TextRect(name).End.X, last = textEnd;
        Transform2D toName = name.GetGlobalTransform().AffineInverse();
        foreach (Control item in row)
        {
            float edge = RightEdge(item);
            if (float.IsFinite(edge)) last = Math.Max(last, (toName * new Vector2(edge, 0)).X);
        }
        if (last > textEnd) tag.Position = new Vector2(MathF.Round(tag.Position.X + last - textEnd), tag.Position.Y);
    }

    // Where a control and what it holds end on the right, on the screen; a part with no size draws nothing.
    static float RightEdge(Control control)
    {
        Rect2 rect = control.GetGlobalRect();
        float end = rect.Area > 0 ? rect.End.X : float.NegativeInfinity;
        foreach (Node child in control.GetChildren())
            if (child is Control { Visible: true } inner) end = Math.Max(end, RightEdge(inner));
        return end;
    }

    // Every control of the window's that the tag's rectangle touches, whether it draws something or not: all that is
    // shown, but the name, the parts that hold it, a background behind the whole name, and what this mod added.
    // A plain node in between is looked through (ViewEnemyStatsPanel, for one, is a Node and no Control).
    List<Control> Under(Control window, Label name, Control tag, Rect2 at)
    {
        var under = new List<Control>();
        Rect2 behind = name.GetGlobalRect();
        Walk(window);
        return under;

        void Walk(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child == tag || child == name || child.IsQueuedForDeletion() || child is CanvasItem { Visible: false }) continue;
                if (child is Control control)
                {
                    if (_added.ContainsValue(control)) continue;
                    Rect2 rect = control.GetGlobalRect();
                    if (rect.Area > 0 && rect.Intersects(at) && !rect.Encloses(behind) && !control.IsAncestorOf(name)) under.Add(control);
                }
                Walk(child);
            }
        }
    }

    // What of the game's the tag would lie on: something drawn (words, a picture, a button, a bar) other than the
    // name itself or a background behind the whole name; or the edge of the window or of a part that cuts it off.
    static string? Covers(Control window, Label name, Control tag, Rect2 at)
    {
        Rect2 text = TextRect(name);
        if (text.Position.X < -1 || text.End.X > name.Size.X + 1) return "the name itself (it is cut off or wrapped)";
        // Inside what the window holds (its child that holds the name), so never on the window's frame.
        Control? content = null;
        for (Node? up = name.GetParent(); up != null && up != window; up = up.GetParent())
        {
            if (up is not Control control) continue;
            if (control.ClipContents && !control.GetGlobalRect().Encloses(at)) return $"the edge of {control.GetType().Name} '{control.Name}'";
            content = control;
        }
        if (!(content ?? window).GetGlobalRect().Encloses(at)) return "the edge of the window";
        Rect2 behind = name.GetGlobalRect();
        return Find(window);

        string? Find(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child == tag || child is not Control { Visible: true } control) continue;
                bool holdsName = child == name || control.IsAncestorOf(name);
                Rect2 rect = control.GetGlobalRect();
                bool drawn = control switch
                {
                    Label words => words.Text != "",
                    TextureRect picture => picture.Texture != null,
                    BaseButton or Godot.Range or LineEdit or RichTextLabel => true,
                    _ => false,
                };
                if (drawn && !holdsName && rect.Intersects(at) && !rect.Encloses(behind)) return $"{control.GetType().Name} '{control.Name}'";
                if ((holdsName || control is not Container || rect.Intersects(at)) && Find(control) is { } inside) return inside;
            }
            return null;
        }
    }

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
    // Development: what each window was read as, written once per change, so it can be compared with its picture.
    // The development requests (DevPage and the others) are listed with the other item.steps in ItemSourcesMod.
    // They open the windows as a player does, with the game's own buttons, so the game binds each window itself.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Active")]
    static extern ActiveThievingRecipe? Active(ThievingRecipeView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ViewRewardsButton")]
    static extern Button RewardsButton(ThievingRecipeView view);
    // The way a player opens View Enemy Stats: the Combat page's areas button, a category and an area in that
    // menu, and an enemy's View Stats button there (CombatActiveView.AreaMenuViewStatsPressedHandler opens it).
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ViewAreasButton")]
    static extern PanelButtonContainer AreasButton(CombatActiveView page);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_AreaMenu")]
    static extern CombatAreasMenu AreasMenu(CombatActiveView page);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ViewEnemyStats")]
    static extern ViewEnemyStatsModal StatsWindow(CombatActiveView page);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ActiveCategory")]
    static extern ActiveCombatAreaCategory? CategoryOf(CombatAreaCategoryButton button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Area")]
    static extern ActiveCombatArea? AreaOf(CombatAreaButton button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Enemy")]
    static extern Enemy? EnemyOf(EnemyOptionsPanel panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ViewStatsButton")]
    static extern Button ViewStatsButton(EnemyOptionsPanel panel);

    /// <summary>A development step that cannot do what it was asked: the request stops there, with this line.</summary>
    public sealed class DevFailed(string message) : Exception(message) { }

    /// <summary>A development step whose window is not there yet (still opening, or laid out on the next frame).</summary>
    public sealed class DevNotYet(string message) : Exception(message) { }

    /// <summary>
    /// What a development step did. Press is the game's control a player's click must press next (at Spot, inside
    /// it), for the game's buttons that have no signal to press them with; Then are the steps that follow it.
    /// </summary>
    public readonly record struct DevStep(string Said, Control? Press = null, Rect2 Spot = default, string[]? Then = null);

    ItemIndex? _index;
    CombatAreasMenu? _devMenu;
    string? _devDoing;
    readonly Dictionary<object, string> _lastRead = new(ReferenceEqualityComparer.Instance);
    // What the last look made of the windows, for the steps that pick a drop (DevRow) and say why a chance has no
    // tooltip: the windows it looked at, what it made of each table, and the chance it counted for each label.
    readonly HashSet<PanelContainerPopup> _devLooked = new();
    readonly Dictionary<Node, string> _devTables = new();
    readonly Dictionary<Label, double> _devUsed = new();
    // The looks so far, and the one during which "first" first found no tooltip.
    int _devLooks;
    int? _devMissedAt;

    void DevLog(object key, string message)
    {
        if (_lastRead.GetValueOrDefault(key) == message) return;
        _lastRead[key] = message;
        log(message);
    }

    void DevThieving(ViewThievingDropsModal window, ActiveThievingRecipe active)
    {
        string Shown(DoubleFormattedLabel label) => $"'{label.Text}' (value {label.Value:R}, format '{label.FormatString}', its number's format '{NumberFormat(label.FormatString) ?? "none"}')";
        DevLog(window, $"Dev: loot thieving {active.Recipe.Id}: common {Shown(CommonChance(window))}, stat {active.Stats.CommonLootChance.Value:R}, base {active.Recipe.BaseCommonLootChance:R}; "
            + $"rare {Shown(RareChance(window))}, stat {active.Stats.RareLootChance.Value:R}, base {active.Recipe.BaseRareLootChance:R}; "
            + $"completion '{DevWords(CompletionTable(window).GetParent())}' shown {CompletionTable(window).IsVisibleInTree()}; "
            + $"combat tables' data {ItemDropTableChance.ChanceConstraint}");
    }

    // The words of a part of the window that are not in its rows: a table's title, a section's heading.
    static string DevWords(Node? node)
    {
        var words = new List<string>();
        void Walk(Node n)
        {
            if (n is ItemDropView) return;
            if (n is Label { Text: not "" } l && l.IsVisibleInTree()) words.Add(l.Text);
            foreach (Node child in n.GetChildren()) Walk(child);
        }
        if (node != null) Walk(node);
        return string.Join(" | ", words);
    }

    // The tag's neighbours, each with its rectangle on the screen: the name, the other controls on its row, and what
    // the tag lies on. In the thieving window this is only told: there the tag keeps its place and its own check.
    string DevBeside(Control window, Label name, Control tag, Rect2 at, List<Control>? under, bool pastRow)
    {
        List<Control> row = RowOf(name, out BoxContainer? box);
        under ??= Under(window, name, tag, at);
        static string Rect(Control c)
        {
            string words = DevWords(c);
            return $"{c.GetType().Name}:{c.Name} {c.GetGlobalRect()}{(words == "" ? "" : $" '{(words.Length > 60 ? words[..60] + "..." : words)}'")}";
        }
        string beside = box == null ? $"the name is in no row, its parent is {name.GetParent()?.GetType().Name}:{name.GetParent()?.Name}"
            : $"its row {box.GetType().Name}:{box.Name} {box.GetGlobalRect()} holds besides it " + (row.Count == 0 ? "nothing" : string.Join(", ", row.Select(Rect)));
        string where = pastRow ? "the tag goes after the last of the name's text and these" : "the tag keeps its place after the name's text";
        return $"the name {name.GetGlobalRect()}; {beside}; {where}; overlap: {(under.Count == 0 ? "none" : string.Join(", ", under.Select(Rect)))}";
    }

    // Which controls open the game's own item tooltip for a row (ServiceLocatedItemTooltip keeps them privately).
    static string DevTargets(ItemDropView row)
    {
        PercentLabel chance = Chance(row);
        string own = $"; the chance's own tooltip '{chance.TooltipText}', mouse {chance.MouseFilter}";
        if (row.ItemInfoTooltip is not { } tooltip
            || tooltip.GetType().GetProperty("Targets", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(tooltip) is not System.Collections.IDictionary targets)
            return "(not readable)" + own;
        return string.Join(", ", targets.Keys.OfType<Control>().Select(c =>
            $"{c.GetType().Name}:{c.Name} mouse {c.MouseFilter}{(c == row ? " (the row)" : c == chance ? " (the chance)" : "")}")) + own;
    }

    static List<T> DevFind<T>(Node? root) where T : Node
    {
        var found = new List<T>();
        void Walk(Node node)
        {
            if (node is T hit) found.Add(hit);
            foreach (Node child in node.GetChildren()) Walk(child);
        }
        if (root != null) Walk(root);
        return found;
    }

    static string DevItem(ItemDropView row) => row.ItemInfoTooltip?.Item?.DisplayName.GetPluralText(1) ?? row.Name;

    static Rect2 Whole(Control control) => new(Vector2.Zero, control.Size);

    // A list that scrolls is scrolled to the control, as a player's mouse wheel would.
    static void DevScrollTo(Control control)
    {
        for (Node? up = control.GetParent(); up != null; up = up.GetParent())
            if (up is ScrollContainer list) { list.EnsureControlVisible(control); return; }
    }

    // The drop windows that are open where the mod reads them (under the game view).
    static List<PanelContainerPopup> DevDropWindows() =>
        DevFind<PanelContainerPopup>(GameAccess.FindGameView())
            .Where(w => w is ViewEnemyStatsModal or ViewEnemyDropsModal or ViewThievingDropsModal && w.IsVisibleInTree()).ToList();

    // Every open View Enemy Stats, wherever it lies, to say which one the game opened.
    static List<ViewEnemyStatsModal> DevStatsWindows() =>
        DevFind<ViewEnemyStatsModal>(((SceneTree)Engine.GetMainLoop()).Root).Where(w => w.IsVisibleInTree()).ToList();

    // A drop in the open windows, enemy and thieving alike, picked from what the last look made of them: an item's
    // id or part of its name; "first", the first chance with this mod's tooltip, or the first chance when the
    // windows have none (one line then says why); "sure", the first chance this mod counted as always;
    // "completion", the first reward of a heist's completion table with this mod's tooltip, else its first.
    ItemDropView DevRow(string what)
    {
        List<PanelContainerPopup> windows = DevDropWindows();
        if (windows.Count == 0) throw new DevNotYet("no drop window is open");
        var rows = windows.SelectMany(w => Rows(w)).Where(r => r.IsVisibleInTree() && r.ItemInfoTooltip?.Item != null).ToList();
        var completion = windows.OfType<ViewThievingDropsModal>().SelectMany(w => Rows(CompletionTable(w))).Where(rows.Contains).ToList();
        ItemDropView? row = what switch
        {
            "first" => rows.FirstOrDefault(DevTipped),
            "sure" => rows.FirstOrDefault(DevSure),
            "completion" => completion.FirstOrDefault(DevTipped),
            _ => rows.FirstOrDefault(r => r.ItemInfoTooltip!.Item!.Id == what || DevItem(r).Contains(what, StringComparison.OrdinalIgnoreCase)),
        };
        if (row != null)
        {
            _devMissedAt = null;
            return row;
        }
        // Windows this mod has looked at and given no tooltip there: the first chance, so the picture shows what is
        // there, with one line that says why. Only once a whole look has gone by since the first miss, so that
        // what is on the screen now (a tab just shown, a window just opened) has been read.
        ItemDropView? plain = what switch
        {
            "first" => rows.FirstOrDefault(r => !Chance(r).IsEmpty),
            "completion" => completion.FirstOrDefault(r => !Chance(r).IsEmpty),
            _ => null,
        };
        if (plain != null && windows.All(_devLooked.Contains))
        {
            _devMissedAt ??= _devLooks;
            if (_devLooks > _devMissedAt)
            {
                _devMissedAt = null;
                DevLog(plain, $"Dev: {what}: no {(what == "first" ? "chance" : "completion reward")} in the open drop windows has this mod's tooltip, so the first is taken, "
                    + $"{DevItem(plain)} '{Chance(plain).Text}'. Why: {DevWhy(windows)}");
                return plain;
            }
        }
        throw new DevNotYet($"no drop '{what}' in the open drop windows; they show: "
            + string.Join(", ", rows.Take(40).Select(r => $"{DevItem(r)} '{Chance(r).Text}'{(DevTipped(r) ? " (tooltip)" : "")}")) + ". " + DevWhy(windows));
    }

    bool DevTipped(ItemDropView row) => _added.ContainsKey(Chance(row));

    // Sure as the last look counted the row: the chance it used is "always" in Found in's words, so no tooltip.
    // In the thieving window a row's 100% is inside its table, which is not sure: what counts is the chance used.
    bool DevSure(ItemDropView row) => _devUsed.TryGetValue(Chance(row), out double used) && ItemLines.Percent(used) == "always";

    string DevCounted(ItemDropView row) => DevTipped(row) ? "with this mod's tooltip"
        : _devUsed.TryGetValue(Chance(row), out double used) ? $"counted at {used:R}, no tooltip" : "not counted by this mod";

    // What the last look made of each open drop window, table by table: why its chances have this mod's tooltip
    // or not.
    string DevWhy(List<PanelContainerPopup> windows) => string.Join(" ", windows.Select(window =>
    {
        INamespacedObject? owner = window switch
        {
            ViewEnemyStatsModal stats => BoundEnemy(StatsPanel(stats)),
            ViewThievingDropsModal thieving => Active(thieving)?.Recipe,
            _ => null,
        };
        string name = window.GetType().Name + (owner != null ? " of " + owner.Id : window is ViewEnemyDropsModal drops ? $" '{EnemyName(drops).Text}'" : "");
        if (!_devLooked.Contains(window)) return $"{name}: not looked at yet.";
        if (owner != null && _unreadable.Contains(owner))
            return $"{name}: left as the game shows it after an error (the line 'The drop window of {owner.Id} is left as the game shows it' says which).";
        var shown = Rows(window).Where(r => r.IsVisibleInTree() && !Chance(r).IsEmpty).ToList();
        var parts = new List<string> { $"{shown.Count} chances shown, {shown.Count(r => _devUsed.ContainsKey(Chance(r)))} counted, {shown.Count(DevTipped)} with a tooltip" };
        IEnumerable<(string What, Control Table)> tables = window is ViewThievingDropsModal rewards
            ? new (string, Control)[] { ("common", CommonTable(rewards)), ("rare", RareTable(rewards)), ("completion", CompletionTable(rewards)) }
            : DevFind<ItemDropTableChanceContainer>(window).Where(t => t.IsVisibleInTree()).Select((t, i) => ($"table {i + 1}", (Control)t));
        foreach (var (what, table) in tables)
            parts.Add($"{what}: {(_devTables.TryGetValue(table, out string? made) ? made : table.IsVisibleInTree() ? "not counted" : "not shown")}");
        if (shown.FirstOrDefault(r => !DevTipped(r)) is { } plain) parts.Add($"{DevItem(plain)} '{Chance(plain).Text}' {DevCounted(plain)}");
        return $"{name}: {string.Join("; ", parts)}.";
    }));

    // The middle of a label's text on the screen, if it can be seen there (not scrolled away).
    static Vector2? DevSpot(Control control, Rect2 local)
    {
        Vector2 inCanvas = control.GetGlobalTransform() * local.GetCenter();
        Vector2 onScreen = control.GetGlobalTransformWithCanvas() * local.GetCenter();
        if (!control.GetViewportRect().HasPoint(onScreen)) return null;
        for (Node? up = control.GetParent(); up != null; up = up.GetParent())
            if (up is Control { ClipContents: true } clip && !clip.GetGlobalRect().HasPoint(inCanvas)) return null;
        return onScreen;
    }

    public Vector2 DevChancePosition(string what)
    {
        ItemDropView row = DevRow(what);
        return DevSpot(Chance(row), TextRect(Chance(row)))
            ?? throw new DevFailed($"the chance of {DevItem(row)} is out of sight: put show={what} before this step");
    }

    // The count beside the name in an open drop window, enemy or thieving; when there is none, why.
    public Vector2 DevTagPosition()
    {
        List<PanelContainerPopup> windows = DevDropWindows();
        if (windows.Count == 0) throw new DevNotYet("no drop window is open");
        var tags = _added.Where(p => p.Value is PanelContainer && GodotObject.IsInstanceValid(p.Key) && GodotObject.IsInstanceValid(p.Value)
            && windows.Any(w => w.IsAncestorOf(p.Key))).ToList();
        if (tags.FirstOrDefault(p => p.Value.IsVisibleInTree()).Value is { } tag)
            return DevSpot(tag, Whole(tag)) ?? throw new DevFailed("the count beside the name is out of sight");
        throw new DevNotYet("no count is shown beside a name in the open drop windows: " + (tags.Count == 0
            ? "this mod put none there on its last look" + (_told.Contains("successes") ? " (the game's statistic of successful tries was not found)" : "")
                + (_told.Contains("combat") ? " (the game's Combat was not found)" : "")
            : string.Join("; ", tags.Select(p => $"'{p.Key.Text}' " + (_placed.TryGetValue(p.Key, out var placed)
                ? placed.Covers is { } covers ? "is left out, it would cover " + covers : "is hidden, its name changed since"
                : "is not laid out yet")))));
    }

    public DevStep DevShow(string what)
    {
        ItemDropView row = DevRow(what);
        string said = $"{DevItem(row)} '{Chance(row).Text}', {DevCounted(row)}";
        for (Node? up = row.GetParent(); up != null; up = up.GetParent())
            if (up is ScrollContainer list)
            {
                list.EnsureControlVisible(row);
                return new DevStep($"scrolled the list to {said}");
            }
        return new DevStep($"{said}, is in a list that does not scroll");
    }

    // The game's page of that name, as its sidebar opens it.
    public DevStep DevPage(string name)
    {
        var options = game.Data.GetDataRegistryOutput<PageNavigationOption>().GetAllObjects().ToList();
        PageNavigationOption? option = options.FirstOrDefault(o => o.DisplayName.Text.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (option?.Page == null) throw new DevFailed($"no page '{name}' among: {string.Join(", ", options.Select(o => o.DisplayName.Text))}");
        GameView view = GameAccess.FindGameView() ?? throw new DevFailed("the game view was not found");
        view.SetCurrentPage(option.Page);
        return new DevStep($"opened the page {option.Id}");
    }

    CombatEntity DevCombat() =>
        game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? entity) && entity is CombatEntity combat ? combat
            : throw new DevFailed("the game's Combat was not found");

    static CombatActiveView DevCombatPage() =>
        GameAccess.FindNode<CombatActiveView>(GameAccess.FindGameView()) is { } page && page.IsVisibleInTree() ? page
            : throw new DevNotYet("the Combat page is not shown: put page=Combat before this step");

    CombatAreasMenu DevAreasMenu()
    {
        CombatAreasMenu menu = AreasMenu(DevCombatPage());
        return menu.IsVisibleInTree() ? menu : throw new DevNotYet("the areas menu of the Combat page is not open");
    }

    // What the character does and where it fights. A request only looks: if this changes while it runs, it stops
    // and says so. enemy= and rewards= note it, so a request for thieving alone is watched as well.
    string DevDoingNow()
    {
        CombatAreasComponent areas = DevCombat().Areas;
        // The actions under way, read as CombatGoals' InCombat reads them; thieving with its target.
        string actions = string.Join(", ", game.ActionManager.ActiveActions.Select(a =>
            a is ThievingActionComponent thieving ? $"{a.Entity.Id} {thieving.CurrentRecipe?.Recipe.Id ?? "no target"}" : a.Entity.Id));
        // In a dungeon the enemy changes by itself as the run goes on.
        return $"actions {(actions == "" ? "none" : actions)}; area {areas.CurrentArea?.Area.Id ?? "none"}"
            + (areas.CurrentArea?.Area is Dungeon ? "" : $", enemy {areas.SelectedEnemy?.Id ?? "none"}");
    }

    // A new item.steps file: what an earlier request saw or opened is not this one's.
    public void DevNewRequest()
    {
        _devDoing = null;
        _devMenu = null;
        _devMissedAt = null;
    }

    void DevSameDoing()
    {
        if (_devDoing == null) return;
        string now = DevDoingNow();
        if (now == _devDoing) return;
        string before = _devDoing;
        _devDoing = null;
        throw new DevFailed($"what the character does changed while the request ran ({before} -> {now}): look at the game by hand");
    }

    // enemy=: View Enemy Stats, opened as a player opens it. "first" is the first enemy the areas menu shows,
    // "longest" the enemy with the longest name in an area this character can open, else an id or (part of) a name.
    public DevStep DevEnemy(string what)
    {
        DevCombatPage();
        CombatAreasComponent areas = DevCombat().Areas;
        _devDoing = DevDoingNow();
        var then = new List<string> { "_areas" };
        if (what == "first")
        {
            then.Add("_stats=first");
            return new DevStep($"the first enemy the areas menu shows; the character: {_devDoing}; then {string.Join(", ", then)}", Then: then.ToArray());
        }
        var places = new ItemLines(game);
        var fought = game.Data.GetDataRegistryOutput<Enemy>().GetAllObjects().Where(e => places.PlacesOf(e).Count > 0).ToList();
        bool Open(BaseCombatArea area) => areas.ActiveAreas.TryGetValue(area, out ActiveCombatArea? active)
            && active.DisplayRequirements?.AreMet != false && active.UnlockRequirements?.AreMet != false;
        Enemy? enemy = what == "longest" ? fought.Where(e => places.PlacesOf(e).Any(Open)).MaxBy(e => e.DisplayName.Text.Length)
            : fought.FirstOrDefault(e => e.Id == what || e.DisplayName.Text.Equals(what, StringComparison.OrdinalIgnoreCase))
              ?? fought.FirstOrDefault(e => e.DisplayName.Text.Contains(what, StringComparison.OrdinalIgnoreCase));
        if (enemy == null) throw new DevFailed($"no enemy '{what}' is fought in an area or a dungeon");
        // The area the menu shows already, else one the character can open, an area before a dungeon.
        BaseCombatArea place = places.PlacesOf(enemy).OrderBy(a => a == areas.VisibleArea?.Area ? 0 : !Open(a) ? 3 : a is CombatArea ? 1 : 2).First();
        if (!areas.ActiveAreas.TryGetValue(place, out ActiveCombatArea? where)) throw new DevFailed($"{place.Id} is not among the game's areas");
        then.Add("_category=" + where.Category.Id);
        then.Add("_area=" + place.Id);
        then.Add("_stats=" + enemy.Id);
        return new DevStep($"{enemy.Id} '{enemy.DisplayName.Text}' ({enemy.DisplayName.Text.Length} letters), fought in {place.Id} of {where.Category.Id}"
            + $"{(Open(place) ? "" : ", which this character cannot open")}; the character: {_devDoing}; then {string.Join(", ", then)}", Then: then.ToArray());
    }

    public DevStep DevAreasButton()
    {
        CombatActiveView page = DevCombatPage();
        CombatAreasMenu menu = AreasMenu(page);
        if (menu.IsVisibleInTree()) return new DevStep("the areas menu is open already");
        PanelButtonContainer button = AreasButton(page);
        if (!button.IsVisibleInTree() || button.Disabled) throw new DevFailed($"the Combat page's areas button {button.GetPath()} is not shown or cannot be pressed");
        _devMenu = menu;
        return new DevStep($"the Combat page's areas button {button.Name}", button, Whole(button));
    }

    public DevStep DevCategoryButton(string id)
    {
        CombatAreasMenu menu = DevAreasMenu();
        if (DevCombat().Areas.VisibleCategory?.Id == id) return new DevStep($"the areas menu shows the category {id} already");
        var buttons = DevFind<CombatAreaCategoryButton>(menu).Where(b => b.IsVisibleInTree() && CategoryOf(b) != null).ToList();
        CombatAreaCategoryButton button = buttons.FirstOrDefault(b => CategoryOf(b)!.Category.Id == id)
            ?? throw new DevFailed($"the areas menu has no category button {id}; it has: {string.Join(", ", buttons.Select(b => CategoryOf(b)!.Category.Id))}");
        if (button.Disabled) throw new DevFailed($"the category button {id} cannot be pressed");
        DevScrollTo(button);
        return new DevStep($"the category button {id} '{CategoryOf(button)!.Category.DisplayName.Text}'", button, Whole(button), ["_shows=category:" + id]);
    }

    public DevStep DevAreaButton(string id)
    {
        CombatAreasMenu menu = DevAreasMenu();
        if (DevCombat().Areas.VisibleArea?.Area.Id == id) return new DevStep($"the areas menu shows {id} already");
        var buttons = DevFind<CombatAreaButton>(menu).Where(b => b.IsVisibleInTree() && AreaOf(b) != null).ToList();
        CombatAreaButton button = buttons.FirstOrDefault(b => AreaOf(b)!.Area.Id == id)
            ?? throw new DevNotYet($"the areas menu has no button for {id}; it shows: {string.Join(", ", buttons.Select(b => AreaOf(b)!.Area.Id))}");
        if (button.Disabled) throw new DevFailed($"the area button {id} cannot be pressed");
        DevScrollTo(button);
        return new DevStep($"the area button {id} '{AreaOf(button)!.DisplayName.Text}'", button, Whole(button), ["_shows=area:" + id]);
    }

    // An enemy's View Stats button in the areas menu, pressed as CombatGoals' Dev presses the game's buttons.
    public DevStep DevViewStats(string id)
    {
        CombatAreasMenu menu = DevAreasMenu();
        var shown = DevFind<EnemyOptionsPanel>(menu).Where(p => p.IsVisibleInTree() && EnemyOf(p) != null).ToList();
        bool Pressable(EnemyOptionsPanel panel) => ViewStatsButton(panel) is { } b && b.IsVisibleInTree() && !b.Disabled;
        string all = string.Join(", ", shown.Select(p => EnemyOf(p)!.DisplayName.Text + (Pressable(p) ? "" : " (no View Stats button)")));
        EnemyOptionsPanel panel = (id == "first" ? shown.FirstOrDefault(Pressable) : shown.FirstOrDefault(p => EnemyOf(p)!.Id == id))
            ?? throw new DevNotYet(shown.Count == 0 ? "the areas menu shows no enemy" : $"the areas menu does not show {id}; it shows: {all}");
        Enemy enemy = EnemyOf(panel)!;
        if (!Pressable(panel)) throw new DevFailed($"the View Stats button of {enemy.Id} is not shown or cannot be pressed; the areas menu shows: {all}");
        Button button = ViewStatsButton(panel);
        button.EmitSignal(BaseButton.SignalName.Pressed);
        return new DevStep($"pressed '{button.Text}' of {enemy.Id} '{enemy.DisplayName.Text}' ({enemy.DisplayName.Text.Length} letters); the areas menu shows: {all}",
            Then: ["_shows=stats:" + enemy.Id]);
    }

    // drops: the drop list of the open View Enemy Stats, shown as a player shows it, with a click on its tab.
    public DevStep DevDropsTab()
    {
        ViewEnemyStatsModal window = DevStatsWindows().FirstOrDefault(w => BoundEnemy(StatsPanel(w)) != null)
            ?? throw new DevFailed("no View Enemy Stats with an enemy is open: put enemy=... before this step");
        CombatDropsContainer list = Drops(StatsPanel(window));
        var chain = new List<string>();
        for (Node? up = list; up != null && up != window; up = up.GetParent())
        {
            chain.Add($"{up.GetType().Name}:{up.Name}");
            if (up.GetParent() is not TabContainer tabs || up is not Control page) continue;
            int tab = tabs.GetTabIdxFromControl(page);
            string title = tabs.GetTabTitle(tab);
            string where = $"tab {tab} of {tabs.GetTabCount()}, '{title}' (the tabs: {string.Join(" | ", Enumerable.Range(0, tabs.GetTabCount()).Select(tabs.GetTabTitle))})";
            if (tabs.CurrentTab == tab) return new DevStep($"the drop list is {where}, shown already", Then: ["_shows=drops"]);
            TabBar bar = tabs.GetTabBar();
            if (tabs.TabsVisible && bar.IsVisibleInTree()) return new DevStep($"the tab of the drop list, {where}", bar, bar.GetTabRect(tab), ["_shows=drops"]);
            return DevPressWords(window, title, $"the drop list is {where}, whose tabs are hidden");
        }
        if (list.IsVisibleInTree()) return new DevStep($"the drop list has no tab and is shown: {string.Join(" < ", chain)}");
        throw new DevFailed($"the drop list is hidden and no tab holds it: {string.Join(" < ", chain)}; the window's tabs and buttons: {DevButtons(window)}");
    }

    // The game's own button in a window that carries these words: a Godot button is pressed with its signal, one
    // of the game's button containers (which have no signal) with a player's click.
    DevStep DevPressWords(Control window, string words, string why)
    {
        foreach (Control control in DevFind<Control>(window))
        {
            if (!control.IsVisibleInTree()) continue;
            if (control is Button { Disabled: false } button && button.Text.Equals(words, StringComparison.OrdinalIgnoreCase))
            {
                button.EmitSignal(BaseButton.SignalName.Pressed);
                return new DevStep($"{why}: pressed the button '{button.Text}' {button.GetPath()}", Then: ["_shows=drops"]);
            }
            if (control is BaseButtonContainer { Disabled: false } container
                && DevFind<Label>(container).Any(l => l.IsVisibleInTree() && l.Text.Equals(words, StringComparison.OrdinalIgnoreCase)))
                return new DevStep($"{why}: the button '{words}' {container.GetPath()}", container, Whole(container), ["_shows=drops"]);
        }
        throw new DevFailed($"{why}, and no button says '{words}'; the window's tabs and buttons: {DevButtons(window)}");
    }

    static string DevButtons(Control window) => string.Join(", ", DevFind<Control>(window).Where(c => c.IsVisibleInTree()).Select(c => c switch
    {
        TabBar bar => "tabs " + string.Join(" | ", Enumerable.Range(0, bar.TabCount).Select(bar.GetTabTitle)),
        Button button => $"button '{button.Text}'",
        BaseButtonContainer container => $"{container.GetType().Name} '{string.Join(" ", DevFind<Label>(container).Select(l => l.Text))}'",
        _ => null,
    }).OfType<string>());

    // _shows=: whether the game now shows what the step before asked for (asked again for a moment if not yet).
    public DevStep DevShows(string what)
    {
        DevSameDoing();
        int cut = what.IndexOf(':');
        string kind = cut < 0 ? what : what[..cut], id = cut < 0 ? "" : what[(cut + 1)..];
        switch (kind)
        {
            case "category":
                string? category = DevCombat().Areas.VisibleCategory?.Id;
                return category == id ? new DevStep($"the areas menu shows the category {id}")
                    : throw new DevNotYet($"the areas menu shows the category {category ?? "none"}, not {id}");
            case "area":
                string? area = DevCombat().Areas.VisibleArea?.Area.Id;
                return area == id ? new DevStep($"the areas menu shows {id}") : throw new DevNotYet($"the areas menu shows {area ?? "no area"}, not {id}");
            case "stats":
                List<ViewEnemyStatsModal> open = DevStatsWindows();
                ViewEnemyStatsModal window = open.FirstOrDefault(w => BoundEnemy(StatsPanel(w))?.Id == id)
                    ?? throw new DevNotYet(open.Count == 0 ? $"View Enemy Stats did not open for {id}"
                        : $"View Enemy Stats is open for {string.Join(", ", open.Select(w => BoundEnemy(StatsPanel(w))?.Id ?? "no enemy"))}, not {id}");
                return new DevStep($"View Enemy Stats is open for {id}, bound by the game: '{EnemyName(StatsPanel(window)).Text}' at {window.GetPath()}, "
                    + $"the Combat page's own {window == StatsWindow(DevCombatPage())}, under the game view {GameAccess.FindGameView()?.IsAncestorOf(window)}");
            case "drops":
                ViewEnemyStatsModal? stats = DevStatsWindows().FirstOrDefault(w => BoundEnemy(StatsPanel(w)) != null);
                return stats != null && Drops(StatsPanel(stats)).IsVisibleInTree()
                    ? new DevStep($"the drop list of {BoundEnemy(StatsPanel(stats))!.Id} is shown")
                    : throw new DevNotYet("the drop list of View Enemy Stats is not shown");
            case "rewards":
                ViewThievingDropsModal? rewards = DevDropWindows().OfType<ViewThievingDropsModal>().FirstOrDefault(w => Active(w)?.Recipe.Id == id);
                return rewards != null ? new DevStep($"the rewards of {id} are open, bound by the game: '{RecipeName(rewards).Text}' at {rewards.GetPath()}")
                    : throw new DevNotYet($"the rewards of {id} did not open under the game view");
        }
        throw new DevFailed("no check called " + what);
    }

    // rewards=: a thieving target's rewards, through its own rewards button, among the targets on the screen.
    // "first" is the first whose rewards can be opened, "heist" the first heist, "longest" the longest name.
    public DevStep DevOpenRewards(string what)
    {
        var views = DevFind<ThievingRecipeView>(GameAccess.FindGameView()).Where(v => v.IsVisibleInTree() && Active(v) != null).ToList();
        if (views.Count == 0) throw new DevNotYet("no thieving target is on the screen: put page=Thieving before this step");
        ThievingRecipe Of(ThievingRecipeView view) => Active(view)!.Recipe;
        bool Heist(ThievingRecipe recipe) => recipe.CompletionDropTable?.Items.Count > 0;
        bool Pressable(ThievingRecipeView view) => RewardsButton(view) is { } b && b.IsVisibleInTree() && !b.Disabled;
        string all = string.Join(", ", views.Select(v => Of(v).DisplayName.Text + (Heist(Of(v)) ? " (heist)" : "") + (Pressable(v) ? "" : " (no rewards button)")));
        ThievingRecipeView? pick = what switch
        {
            "first" => views.FirstOrDefault(Pressable),
            "heist" => views.FirstOrDefault(v => Heist(Of(v)) && Pressable(v)),
            "longest" => views.Where(Pressable).MaxBy(v => Of(v).DisplayName.Text.Length),
            _ => views.FirstOrDefault(v => Of(v).Id == what || Of(v).DisplayName.Text.Contains(what, StringComparison.OrdinalIgnoreCase)),
        };
        if (pick == null)
            throw new DevFailed(what switch
            {
                "heist" => "no heist on the screen whose rewards can be opened",
                "first" or "longest" => "no thieving target on the screen whose rewards can be opened",
                _ => $"no thieving target '{what}' on the screen",
            } + "; the targets on the screen: " + all);
        if (!Pressable(pick)) throw new DevFailed($"the rewards button of {Of(pick).Id} is not shown or cannot be pressed; the targets on the screen: {all}");
        _devDoing = DevDoingNow();
        Button button = RewardsButton(pick);
        button.EmitSignal(BaseButton.SignalName.Pressed);
        return new DevStep($"pressed '{button.Text}' of {Of(pick).Id} '{Of(pick).DisplayName.Text}'; the character: {_devDoing}; the targets on the screen: {all}",
            Then: ["_shows=rewards:" + Of(pick).Id]);
    }

    // close: the open drop windows, and the areas menu when a request opened it, each with its own close button.
    public DevStep DevClose()
    {
        List<PanelContainerPopup> windows = DevDropWindows();
        if (_devMenu != null && GodotObject.IsInstanceValid(_devMenu) && _devMenu.IsVisibleInTree()) windows.Add(_devMenu);
        _devMenu = null;
        foreach (PanelContainerPopup window in windows)
        {
            if (window.CloseButton is { Disabled: false } close && GodotObject.IsInstanceValid(close) && close.IsVisibleInTree()) close.EmitSignal(BaseButton.SignalName.Pressed);
            else window.Close();
        }
        DevSameDoing();
        return new DevStep(windows.Count == 0 ? "no window to close" : "closed " + string.Join(", ", windows.Select(w => w.GetType().Name)));
    }
#endif
}
