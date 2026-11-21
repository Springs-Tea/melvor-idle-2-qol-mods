using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Localization;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace ItemSources;

/// <summary>
/// The game's item panel says "Found in" and "Used in" with a few skill icons. Hover either row and this
/// lists what is behind them: which enemy drops the item, where that enemy lives, how many and how likely;
/// which recipe makes it; which chest holds it; and every recipe, upgrade or purchase that uses it.
/// </summary>
public class ItemSourcesMod : IMod
{
    // The item a panel shows, and its two rows of icons, are private to the game's view.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Item")]
    static extern Item? ItemOf(ItemInfoPanel panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_BankTagProductContainer")]
    static extern HBoxContainer? FoundRowOf(ItemInfoPanel panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_BankTagIngredientContainer")]
    static extern HBoxContainer? UsedRowOf(ItemInfoPanel panel);

    // A long group (an item fifteen enemies drop) must not push the others off the tooltip.
    const int MaxLines = 24, MaxPerGroup = 8;

    sealed record Line(string Group, string Name, Texture2D? Icon, string Quantity, double Chance);

    ModContext _ctx = null!;
    readonly GameStyle _style = new();
    Game? _game;
    volatile ItemIndex? _index;
    bool _stopped;
    int _frame;
    readonly List<ItemInfoPanel> _panels = new();
    // Looked up once per character, on the game's thread, when the first tooltip needs them.
    Dictionary<object, string>? _skillOfRecipe;
    Dictionary<Enemy, List<string>>? _areasOfEnemy;

    CanvasLayer? _layer;
    PanelContainer? _tip;
    string _shown = "";

    public void OnLoad(ModContext ctx)
    {
        _ctx = ctx;
        ctx.Frame += OnFrame;
        ctx.Log("Ready.");
    }

    void OnFrame()
    {
        if (_stopped) return;
        _frame++;
        try
        {
            Game? game = GameAccess.CurrentGame;
            if (game is not { IsSaveLoaded: true }) { _game = null; Hide(); return; }
            if (!ReferenceEquals(game, _game)) Open(game);
            if (_frame % 20 == 0) FindPanels();
#if DEBUG
            DevRequests();
            if (_devItem != null) return;   // the tooltip is being shown for a picture
#endif
            Hover();
        }
        catch (Exception e) when (e is MissingMethodException or MissingFieldException)
        {
            _ctx.Log("Stopped: the item panel changed in this game version. " + e.Message);
            _stopped = true;
            Hide();
        }
        catch (Exception e)
        {
            _ctx.Log("Stopped: " + e);
            _stopped = true;
            Hide();
        }
    }

    void Open(Game game)
    {
        _game = game;
        _index = null;
        _skillOfRecipe = null;
        _areasOfEnemy = null;
        _panels.Clear();
        Hide();
        // Reading the whole of the game's data takes a moment: beside the game, not in its way.
        Task.Run(() =>
        {
            try
            {
                var clock = Stopwatch.StartNew();
                ItemIndex index = ItemIndex.Build(game);
                if (!ReferenceEquals(game, _game)) return;
                _index = index;
                _ctx.Log($"Read {index.Owners:N0} game objects in {clock.ElapsedMilliseconds:N0} ms: sources or uses known for {index.Count:N0} items.");
#if DEBUG
                lock (index.Unclassified)
                    _ctx.Log("Dev: neither given nor taken: " + string.Join(" | ", index.Unclassified.OrderBy(x => x).Take(80)));
#endif
            }
            catch (Exception e) { _ctx.Log("Could not read the game's data: " + e); }
        });
    }

    // ---- which panel, which row -----------------------------------------------------------------------------

    void FindPanels()
    {
        _panels.Clear();
        if (GameAccess.FindGameView() is { } view) Collect(view, _panels);
    }

    static void Collect(Node node, List<ItemInfoPanel> panels)
    {
        if (node is CanvasItem { Visible: false }) return;
        if (node is ItemInfoPanel panel) { panels.Add(panel); return; }
        for (int i = 0, n = node.GetChildCount(); i < n; i++) Collect(node.GetChild(i), panels);
    }

    void Hover()
    {
        foreach (ItemInfoPanel panel in _panels)
        {
            if (!GodotObject.IsInstanceValid(panel) || !panel.IsVisibleInTree() || ItemOf(panel) is not { } item) continue;
            Vector2 mouse = panel.GetGlobalMousePosition();
            foreach (var (row, found) in new[] { (FoundRowOf(panel), true), (UsedRowOf(panel), false) })
            {
                if (row == null || !row.IsVisibleInTree() || !row.GetGlobalRect().HasPoint(mouse)) continue;
                Show(item, found, row.GetGlobalRect());
                return;
            }
        }
        Hide();
    }

    // ---- the tooltip ------------------------------------------------------------------------------------------

    void Hide()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer)) _layer.Visible = false;
        _shown = "";
    }

    void Show(Item item, bool found, Rect2 near)
    {
        ItemIndex? index = _index;
        string key = item.Id + (found ? "<" : ">") + (index == null ? "?" : "");
        if (_layer == null || !GodotObject.IsInstanceValid(_layer))
        {
            _layer = new CanvasLayer { Layer = 120 };
            _ctx.Tree.Root.AddChild(_layer);
        }
        if (key != _shown)
        {
            _shown = key;
            if (_tip != null && GodotObject.IsInstanceValid(_tip)) _tip.QueueFree();
            _tip = Build(item, found, index);
            _layer.AddChild(_tip);
        }
        _layer.Visible = true;
        Place(near);
    }

    // Beside the row, on the side that has room, never off the screen.
    void Place(Rect2 near)
    {
        if (_tip == null) return;
        Vector2 size = _tip.GetCombinedMinimumSize(), screen = _tip.GetViewportRect().Size;
        _tip.Size = size;
        float x = near.Position.X - size.X - 12;
        if (x < 0) x = Math.Min(near.End.X + 12, screen.X - size.X);
        float y = Math.Clamp(near.Position.Y - 12, 0, Math.Max(0, screen.Y - size.Y));
        _tip.Position = new Vector2(MathF.Round(Math.Max(0, x)), MathF.Round(y));
    }

    PanelContainer Build(Item item, bool found, ItemIndex? index)
    {
        var tip = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        tip.AddThemeStyleboxOverride("panel", _style.TooltipPanel());
        var column = _style.Spaced(new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 3);
        tip.AddChild(column);

        var head = _style.Spaced(new HBoxContainer(), 8);
        head.AddChild(_style.Picture(item.Icon, 24));
        head.AddChild(_style.Text(item.DisplayName.GetPluralText(1), GameStyle.TextWhite, 16, GameStyle.Bold));
        Label what = _style.Text(found ? "is found in" : "is used in", GameStyle.TextGrey, 13);
        what.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
        head.AddChild(what);
        column.AddChild(head);

        if (index == null) { column.AddChild(_style.Text("Reading the game's data...", GameStyle.TextGrey, 13)); return tip; }
        List<ItemRef> refs = (found ? index.For(item)?.Sources : index.For(item)?.Uses) ?? new List<ItemRef>();
        if (refs.Count == 0)
        {
            column.AddChild(_style.Text(found ? "Nothing in the game's data gives this item." : "Nothing in the game's data uses this item.", GameStyle.TextGrey, 13));
            return tip;
        }

        // Combat first (what people ask about), then the rest by name; the likeliest on top inside a group.
        var lines = refs.Select(Describe)
            .OrderBy(l => l.Group == "Combat" ? 0 : 1).ThenBy(l => l.Group, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(l => l.Chance).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
        bool chances = lines.Any(l => l.Chance < 0.9995);
        var counts = lines.GroupBy(l => l.Group).ToDictionary(g => g.Key, g => g.Count());
        string group = "";
        int inGroup = 0, written = 0;
        foreach (Line line in lines)
        {
            if (line.Group != group)
            {
                group = line.Group;
                inGroup = 0;
                int total = counts[group];
                Label title = _style.Text(total > MaxPerGroup ? $"{group}  ({total})" : group, new Color("ffd16d"), 12, GameStyle.SemiBold);
                title.CustomMinimumSize = new Vector2(0, _style.Px(20));
                title.VerticalAlignment = VerticalAlignment.Bottom;
                column.AddChild(title);
            }
            if (++inGroup > MaxPerGroup || written >= MaxLines)
            {
                if (inGroup == MaxPerGroup + 1) column.AddChild(_style.Text($"and {counts[group] - MaxPerGroup} more", GameStyle.TextDim, 12));
                continue;
            }
            written++;
            var row = _style.Spaced(new HBoxContainer(), 8);
            row.AddChild(_style.Picture(line.Icon, 20));
            Label name = _style.Text(line.Name);
            name.CustomMinimumSize = new Vector2(_style.Px(230), 0);
            name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(name);
            Label quantity = _style.Text(line.Quantity, GameStyle.TextGrey, 13);
            quantity.HorizontalAlignment = HorizontalAlignment.Right;
            quantity.CustomMinimumSize = new Vector2(_style.Px(52), 0);
            row.AddChild(quantity);
            if (chances)
            {
                Label chance = _style.Text(Percent(line.Chance), line.Chance >= 0.9995 ? GameStyle.TextGrey : GameStyle.TextWhite, 14, GameStyle.Bold);
                chance.HorizontalAlignment = HorizontalAlignment.Right;
                chance.CustomMinimumSize = new Vector2(_style.Px(64), 0);
                row.AddChild(chance);
            }
            column.AddChild(row);
        }
        return tip;
    }

    static string Percent(double chance) =>
        chance >= 0.9995 ? "always" : chance >= 0.1 ? chance.ToString("0.#%") : chance >= 0.001 ? chance.ToString("0.##%")
        : chance > 0 ? $"1 in {1 / chance:N0}" : "-";

    // ---- naming what holds the item -------------------------------------------------------------------------

    Line Describe(ItemRef r)
    {
        string quantity = r.Max > r.Min ? $"{r.Min:N0}-{r.Max:N0}" : r.Min > 1 ? $"x{r.Min:N0}" : "";
        switch (r.Owner)
        {
            case Enemy enemy:
                List<string> areas = AreasOf(enemy);
                string where = areas.Count == 0 ? "" : "  ·  " + areas[0] + (areas.Count > 1 ? $" +{areas.Count - 1}" : "");
                return new Line("Combat", enemy.DisplayName.Text + where, ((IDescribedObject)enemy).Icon, quantity, r.Chance);
            case Dungeon dungeon:
                return new Line("Combat", dungeon.DisplayName.Text + "  ·  dungeon reward", dungeon.Icon, quantity, r.Chance);
            case Item holder:
                string kind = r.Path.Contains("Openable", StringComparison.OrdinalIgnoreCase) || r.Path.Contains("DropTable", StringComparison.OrdinalIgnoreCase) ? "Opening"
                            : r.Path.Contains("Upgrad", StringComparison.OrdinalIgnoreCase) ? "Upgrading" : "Items";
                return new Line(kind, holder.DisplayName.GetPluralText(1), holder.Icon, quantity, r.Chance);
            default:
                return new Line(SkillOf(r.Owner), NameOf(r.Owner), (r.Owner as IDescribedObject)?.Icon, quantity, r.Chance);
        }
    }

    static string NameOf(object owner)
    {
        object? name = owner.GetType().GetProperty("DisplayName")?.GetValue(owner);
        string? text = name switch
        {
            IPluralString plural => plural.GetPluralText(1),
            ILocalizedString localized => localized.Text,
            string s => s,
            _ => null,
        };
        return !string.IsNullOrWhiteSpace(text) ? text : (owner as INamespacedObject)?.Id.Split(':').Last() ?? owner.GetType().Name;
    }

    // The skill a recipe belongs to: asked from the skills themselves where they list their recipes, read
    // from the class's place in the game's code otherwise ("...Entities.Thieving.ThievingRecipe").
    string SkillOf(object owner)
    {
        if (_skillOfRecipe == null)
        {
            _skillOfRecipe = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
            foreach (GameEntity entity in _game!.Entities.GetAllObjects())
                foreach (var component in entity.Components.Values)
                {
                    if (component.GetType().GetProperty("ActiveRecipes")?.GetValue(component) is not IDictionary recipes) continue;
                    string skill = NameOf(entity);
                    foreach (object recipe in recipes.Keys) _skillOfRecipe.TryAdd(recipe, skill);
                }
        }
        if (_skillOfRecipe.TryGetValue(owner, out string? known)) return known;
        Type type = owner.GetType();
        Match inEntities = Regex.Match(type.Namespace ?? "", @"\.Entities\.(\w+)");
        if (inEntities.Success) return inEntities.Groups[1].Value;
        // "ItemUpgrade" -> "Item Upgrade"
        return Regex.Replace(type.Name, "(?<=[a-z])(?=[A-Z])", " ");
    }

    List<string> AreasOf(Enemy enemy)
    {
        if (_areasOfEnemy == null)
        {
            _areasOfEnemy = new Dictionary<Enemy, List<string>>(ReferenceEqualityComparer.Instance);
            void Put(Enemy e, string area)
            {
                if (!_areasOfEnemy.TryGetValue(e, out List<string>? list)) _areasOfEnemy[e] = list = new List<string>();
                if (!list.Contains(area)) list.Add(area);
            }
            if (_game!.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? c) && c is CombatEntity combat)
                foreach (BaseCombatArea area in combat.Areas.ActiveAreas.Keys)
                {
                    string name = area.DisplayName.Text;
                    if (area is CombatArea open) foreach (Enemy e in open.Enemies) Put(e, name);
                    else if (area is Dungeon dungeon)
                        foreach (DungeonEnemies wave in dungeon.Enemies)
                            foreach (WeightedEnemy weighted in wave.Enemies.Items) Put(weighted.Enemy, name);
                }
        }
        return _areasOfEnemy.GetValueOrDefault(enemy) ?? new List<string>();
    }

#if DEBUG
    // Development: a file "item.request" holding part of an item's name (and optionally "|used") writes that
    // item's lines to the log and shows its tooltip in the middle of the screen for a picture.
    int _devShotAt = -1;
    Item? _devItem;
    bool _devFound = true;

    void DevRequests()
    {
        string request = Path.Combine(_ctx.Directory, "item.request");
        if (_frame % 30 == 0 && File.Exists(request) && _index != null && _game != null)
        {
            string[] parts = File.ReadAllText(request).Trim().Split('|');
            File.Delete(request);
            _devItem = _game.Data.GetDataRegistryOutput<Item>().GetAllObjects()
                .FirstOrDefault(i => i.DisplayName.GetPluralText(1).Equals(parts[0].Trim(), StringComparison.OrdinalIgnoreCase))
                ?? _game.Data.GetDataRegistryOutput<Item>().GetAllObjects()
                .FirstOrDefault(i => i.DisplayName.GetPluralText(1).Contains(parts[0].Trim(), StringComparison.OrdinalIgnoreCase));
            _devFound = parts.Length < 2 || !parts[1].Trim().Equals("used", StringComparison.OrdinalIgnoreCase);
            if (_devItem == null) { _ctx.Log("Dev: no such item."); return; }
            ItemEntry? entry = _index.For(_devItem);
            foreach (var (title, refs) in new[] { ("found in", entry?.Sources), ("used in", entry?.Uses) })
                _ctx.Log($"Dev: {_devItem.DisplayName.GetPluralText(1)} {title}: " + (refs == null || refs.Count == 0 ? "nothing"
                    : string.Join(" | ", refs.Select(Describe).OrderByDescending(l => l.Chance).Take(40).Select(l => $"[{l.Group}] {l.Name} {l.Quantity} {Percent(l.Chance)}"))));
            _devShotAt = _frame + 60;
        }
        if (_devItem != null && _devShotAt > 0)
        {
            Vector2 screen = _ctx.Tree.Root.GetVisibleRect().Size;
            Show(_devItem, _devFound, new Rect2(screen.X * 0.62f, screen.Y * 0.25f, 10, 10));
            if (_frame >= _devShotAt)
            {
                _ctx.Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(_ctx.Directory, "shot-item.png"));
                _ctx.Log("Dev: saved shot-item.png");
                _devShotAt = -1;
                _devItem = null;
            }
        }
    }
#endif
}
