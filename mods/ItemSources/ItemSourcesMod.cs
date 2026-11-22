using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Bank;
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
    // The item a panel shows, and its two rows of icons, are private to the game's view. A panel is given
    // either an item (tooltips) or a stack of them (the bank's selected item): only one of the two is set.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Item")]
    static extern Item? BoundItem(ItemInfoPanel panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ItemStack")]
    static extern IReadonlyItemStack? BoundStack(ItemInfoPanel panel);

    static Item? ItemOf(ItemInfoPanel panel) => BoundItem(panel) ?? BoundStack(panel)?.Item;

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Inventory")]
    static extern IInventory? InventoryOf(ItemInfoPanel panel);

    // The scene of the game's item panel: the copy that Alt keeps open is made from it.
    const string ItemPanelScene = "res://Entities/Bank/item_info_panel.tscn";
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_BankTagProductContainer")]
    static extern HBoxContainer? FoundRowOf(ItemInfoPanel panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_BankTagIngredientContainer")]
    static extern HBoxContainer? UsedRowOf(ItemInfoPanel panel);

    // A long group (an item fifteen enemies drop) must not push the others off the tooltip.
    const int MaxLines = 24, MaxPerGroup = 8;
    const int TipGap = 8;
    // The way from "Found in" to its list, for a pointer that has stopped moving along it: how long the list
    // stays up over it, and (much shorter) how long a row that lies in the way is passed over.
    const int WayOpenMs = 700, WayAcrossMs = 150;

    ModContext _ctx = null!;
    readonly GameStyle _style = new();
    Game? _game;
    volatile ItemIndex? _index;
    bool _stopped;
    int _frame;
    readonly List<ItemInfoPanel> _panels = new();
    // Names what holds an item; looks the areas and the skills up when the first tooltip needs them.
    ItemLines? _lines;

    CanvasLayer? _layer;
    PanelContainer? _tip;
    string _shown = "";
    // The list that is up has chances to rest the pointer on: the panel whose "Found in" row opened it, so the
    // pointer can leave that row for the list; where between the two the pointer was last (0 at the row, 1 at
    // the list), and when it last moved along that way.
    bool _tipHasMore;
    ItemInfoPanel? _listPanel;
    float _wayAt;
    ulong _wayMovedAt;

    enum Way { None, Moving, Resting, OnList }

    // The game's one item tooltip (it follows the pointer everywhere), and the copy of it that Alt pins.
    Control? _gameTip;
    ItemInfoPanel? _gameTipPanel;
    Label? _gameTipHint;
    CanvasLayer? _pinLayer;
    PanelContainer? _pinCard;
    ItemInfoPanel? _pinPanel;
    bool _altWasDown, _altAlone, _clickWasDown, _pinFailed, _indexFailed;
    readonly Tempo _lookForPanels = new(350), _lookForTooltip = new(500);

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
            if (game is not { IsSaveLoaded: true }) { _game = null; Hide(); Unpin(); return; }
            if (!ReferenceEquals(game, _game)) Open(game);
            if (_lookForPanels.Due()) FindPanels();
            Pinning();
#if DEVTOOLS
            DevRequests();
            if (_devItem != null) return;   // the tooltip is being shown for a picture
#endif
            Hover();
        }
        catch (Exception e) when (e is MissingMethodException or MissingFieldException)
        {
            _ctx.Log("Stopped (the game was updated?): the item panel changed. " + e.Message);
            _stopped = true;
            Hide();
            Unpin();
        }
        catch (Exception e)
        {
            _ctx.Log("Stopped (the game was updated?): " + e);
            _stopped = true;
            Hide();
            Unpin();
        }
    }

    void Open(Game game)
    {
        _game = game;
        _index = null;
        _indexFailed = false;
        _lines = new ItemLines(game);
        _panels.Clear();
        _gameTip = null;
        _gameTipPanel = null;
        _gameTipHint = null;
        Unpin();
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
#if DEVTOOLS
                _ctx.Log("Dev: neither given nor taken: " + string.Join(" | ", index.Unclassified.OrderBy(x => x).Take(80)));
#endif
            }
            catch (Exception e)
            {
                _ctx.Log("Could not read the game's data (the game was updated?): " + e);
                if (ReferenceEquals(game, _game)) _indexFailed = true;
            }
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
        // A list the pointer rests on comes first: it lies on top of whatever is under it.
        Way way = WayToTheList();
        if (way == Way.OnList) return;

        // The pinned copy first: it lies on top of whatever else is there.
        IEnumerable<ItemInfoPanel> panels = _pinPanel != null && GodotObject.IsInstanceValid(_pinPanel) ? _panels.Prepend(_pinPanel) : _panels;
        foreach (ItemInfoPanel panel in panels)
        {
            if (!GodotObject.IsInstanceValid(panel) || !panel.IsVisibleInTree() || ItemOf(panel) is not { } item) continue;
            Vector2 mouse = Pointer(panel);
#if DEVTOOLS
            // Development: as if the pointer were on the row, to take a picture of the real thing.
            if (_devHoverUntil > _frame && (_devHoverFound ? FoundRowOf(panel) : UsedRowOf(panel)) is { } pretend)
                mouse = pretend.GetGlobalRect().GetCenter();
#endif
            foreach (var (row, found) in new[] { (FoundRowOf(panel), true), (UsedRowOf(panel), false) })
            {
                if (row == null || !row.IsVisibleInTree() || !row.GetGlobalRect().HasPoint(mouse)) continue;
                // "Used in" lies right under "Found in": a pointer going from that row down to a line of its list
                // crosses it. While it keeps moving, the row it crosses waits.
                bool own = found && ReferenceEquals(panel, _listPanel);
                if (!own && way == Way.Moving) return;
                Rect2 beside = ReferenceEquals(panel, _pinPanel) && _pinCard != null ? _pinCard.GetGlobalRect() : panel.GetGlobalRect();
                Rect2 line = row.GetGlobalRect();
                Show(item, found, new Rect2(beside.Position.X, line.Position.Y, beside.Size.X, line.Size.Y));
                // The game's own tooltip follows the pointer: its list can never be reached, and stays as it was.
                _listPanel = found && _tipHasMore && !ReferenceEquals(panel, _gameTipPanel) ? panel : null;
                _wayMovedAt = Time.GetTicksMsec();
                return;
            }
        }
        if (way == Way.None) Hide();
    }

    // Where the pointer is, for a list that can be reached: on it; on the way between its "Found in" row and it
    // (anywhere a straight line from the row to a line of the list can pass), moving or come to rest; or
    // neither, which also is a pointer that rested on the way for too long.
    Way WayToTheList()
    {
        if (_listPanel == null || !GodotObject.IsInstanceValid(_listPanel) || !_listPanel.IsVisibleInTree()
            || _tip == null || !GodotObject.IsInstanceValid(_tip) || _layer is not { Visible: true }
            || ItemOf(_listPanel) is not { } item || !_shown.StartsWith(item.Id + "<", StringComparison.Ordinal)
            || FoundRowOf(_listPanel) is not { } found) return Way.None;
        Vector2 mouse = Pointer(_tip);
        Rect2 list = _tip.GetGlobalRect(), row = found.GetGlobalRect();
        ulong now = Time.GetTicksMsec();
        if (list.HasPoint(mouse)) { _wayMovedAt = now; return Way.OnList; }
        // From the far end of the row to the near side of the list, as high as the row there and as the list here.
        bool listOnTheLeft = list.GetCenter().X < row.GetCenter().X;
        float from = listOnTheLeft ? row.End.X : row.Position.X, to = listOnTheLeft ? list.End.X : list.Position.X;
        if (Math.Abs(to - from) < 1) return Way.None;
        float along = (mouse.X - from) / (to - from);
        if (along is < 0 or > 1) return Way.None;
        if (mouse.Y < Mathf.Lerp(row.Position.Y, list.Position.Y, along) - 2 || mouse.Y > Mathf.Lerp(row.End.Y, list.End.Y, along) + 2) return Way.None;
        if (Math.Abs(along - _wayAt) > 0.01f) { _wayAt = along; _wayMovedAt = now; }
        ulong still = now - _wayMovedAt;
        return still <= WayAcrossMs ? Way.Moving : still <= WayOpenMs ? Way.Resting : Way.None;
    }

    Vector2 Pointer(Control seenFrom)
    {
#if DEVTOOLS
        if (_devPointer is { } made) return made;
#endif
        return seenFrom.GetGlobalMousePosition();
    }

    // ---- the tooltip ------------------------------------------------------------------------------------------

    void Hide()
    {
        _listPanel = null;
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
        float x = near.Position.X - size.X - TipGap;
        if (x < 0) x = Math.Min(near.End.X + TipGap, screen.X - size.X);
        float y = Math.Clamp(near.Position.Y - 12, 0, Math.Max(0, screen.Y - size.Y));
        _tip.Position = new Vector2(MathF.Round(Math.Max(0, x)), MathF.Round(y));
    }

    // Laid out like the game's own item tooltip: the name, a small tag, the fading line, then the lines.
    PanelContainer Build(Item item, bool found, ItemIndex? index)
    {
        _tipHasMore = false;
        var tip = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(_style.Px(280), 0) };
        tip.AddThemeStyleboxOverride("panel", _style.TooltipPanel());
        var column = _style.Spaced(new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 4);
        tip.AddChild(column);

        var head = _style.Spaced(new HBoxContainer(), 8);
        head.AddChild(_style.Text(item.DisplayName.GetPluralText(1), GameStyle.TextWhite, 16, GameStyle.Bold));
        head.AddChild(_style.NoteTag(found ? "Found in" : "Used in"));
        column.AddChild(head);
        column.AddChild(_style.FadingLine());

        if (index == null)
        {
            column.AddChild(_style.Text(_indexFailed ? "The game's data could not be read: see MelvorModLoader.log." : "Reading the game's data...", GameStyle.TextGrey, 14));
            return tip;
        }
        List<ItemRef> refs = (found ? index.For(item)?.Sources : index.For(item)?.Uses) ?? new List<ItemRef>();
        if (refs.Count == 0)
        {
            column.AddChild(_style.Text(found ? "Nothing in the game's data gives this item." : "Nothing in the game's data uses this item.", GameStyle.TextGrey, 14));
            return tip;
        }

        // Combat first (what people ask about), then the rest by name; the likeliest on top inside a group.
        var lines = refs.Select(_lines!.Describe)
            .OrderBy(l => l.Group == "Combat" ? 0 : 1).ThenBy(l => l.Group, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(l => l.Chance).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
        bool chances = lines.Any(l => l.Chance < 0.9995), quantities = lines.Any(l => l.Quantity != "");
        var counts = lines.GroupBy(l => l.Group).ToDictionary(g => g.Key, g => g.Count());
        string group = "";
        Texture2D? groupIcon = null;
        int inGroup = 0, written = 0, leftOut = 0;
        foreach (ItemLine line in lines)
        {
            if (written >= MaxLines) { leftOut++; continue; }
            if (line.Group != group)
            {
                group = line.Group;
                inGroup = 0;
                int total = counts[group];
                // "[skill] Thieving", in the grey of the game's own "Found in:".
                groupIcon = GameStyle.Icon(group.Replace(" ", ""));
                var title = _style.Spaced(new HBoxContainer { CustomMinimumSize = new Vector2(0, _style.Px(written == 0 ? 20 : 26)), Alignment = BoxContainer.AlignmentMode.Begin }, 6);
                if (groupIcon != null)
                {
                    TextureRect picture = _style.Picture(groupIcon, 16);
                    picture.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
                    title.AddChild(picture);
                }
                Label name = _style.Text(total > MaxPerGroup ? $"{group}  ({total})" : group, GameStyle.TextDim, 14, GameStyle.Bold);
                name.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
                title.AddChild(name);
                column.AddChild(title);
            }
            if (++inGroup > MaxPerGroup)
            {
                if (inGroup == MaxPerGroup + 1) column.AddChild(_style.Text($"and {counts[group] - MaxPerGroup} more", GameStyle.TextDim, 12));
                continue;
            }
            written++;
            var row = _style.Spaced(new HBoxContainer(), 8);
            row.AddChild(_style.Picture(line.Icon ?? groupIcon, 20));
            Label what = _style.Text(line.Name);
            what.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(what);
            if (quantities)
            {
                Label quantity = _style.Text(line.Quantity, GameStyle.TextGrey, 13);
                quantity.HorizontalAlignment = HorizontalAlignment.Right;
                quantity.CustomMinimumSize = new Vector2(_style.Px(36), 0);
                row.AddChild(quantity);
            }
            if (chances)
            {
                bool always = line.Chance >= 0.9995;
                Label chance = _style.Text(ItemLines.Percent(line.Chance), always ? GameStyle.TextDim : GameStyle.TextWhite, always ? 13 : 14, always ? null : GameStyle.Bold);
                chance.HorizontalAlignment = HorizontalAlignment.Right;
                chance.CustomMinimumSize = new Vector2(_style.Px(52), 0);
                if (found && ItemLines.Luck(line) is string luck)
                {
                    chance.TooltipText = luck;
                    chance.MouseFilter = Control.MouseFilterEnum.Pass;
                    _tipHasMore = true;
                }
                row.AddChild(chance);
            }
            column.AddChild(row);
        }
        if (leftOut > 0) column.AddChild(_style.Text($"and {leftOut} more elsewhere", GameStyle.TextDim, 12));
        // A list the pointer can rest on is in the way of what lies under it, like any window.
        if (_tipHasMore)
        {
            tip.MouseFilter = Control.MouseFilterEnum.Stop;
            tip.Theme = _style.TooltipTheme();
        }
        return tip;
    }

    // ---- pinning the game's item tooltip --------------------------------------------------------------------

    // The game shows one tooltip for whatever item the pointer is on, anywhere: the bank, a recipe's materials,
    // a shop, loot. It follows the pointer, so its "Found in" and "Used in" rows cannot be reached. Alt puts a
    // copy of it where it is; the copy stays, and its two rows work like the ones in the bank.
    void Pinning()
    {
        // Alt by itself, when it is let go: Alt+Tab, Alt+Enter and AltGr (which is Ctrl+Alt) are not this key.
        bool alt = Input.IsKeyPressed(Key.Alt), click = Input.IsMouseButtonPressed(MouseButton.Left);
        if (alt && !_altWasDown) _altAlone = true;
        if (alt && (Input.IsKeyPressed(Key.Ctrl) || Input.IsKeyPressed(Key.Tab) || Input.IsKeyPressed(Key.Enter) || Input.IsKeyPressed(Key.F4)
                    || !DisplayServer.WindowIsFocused())) _altAlone = false;
        bool altPressed = !alt && _altWasDown && _altAlone, clicked = click && !_clickWasDown;
        _altWasDown = alt;
        _clickWasDown = click;

        bool hovering = GameTipItem(out Item? item, out Control? card);
        bool pinned = _pinCard != null && GodotObject.IsInstanceValid(_pinCard);
        if (_gameTipHint != null && GodotObject.IsInstanceValid(_gameTipHint)) _gameTipHint.Visible = !_pinFailed;
        if (altPressed)
        {
            if (hovering) Pin(item!, card!);
            else if (pinned) Unpin();
            return;
        }
        if (!pinned) return;
        // A click on the list that belongs to the kept tooltip is not a click somewhere else.
        Vector2 mouse = Pointer(_pinCard!);
        bool onList = ReferenceEquals(_listPanel, _pinPanel) && _tip != null && GodotObject.IsInstanceValid(_tip) && _layer is { Visible: true } && _tip.GetGlobalRect().HasPoint(mouse);
        if (Input.IsKeyPressed(Key.Escape) || (clicked && !onList && !_pinCard!.GetGlobalRect().HasPoint(mouse))) Unpin();
        else KeepOnScreen(_pinCard!);
    }

    /// <summary>The item the game's tooltip is showing right now, and the card it is drawn on.</summary>
    bool GameTipItem(out Item? item, out Control? card)
    {
        item = null;
        card = null;
        if (_gameTip == null || !GodotObject.IsInstanceValid(_gameTip) || _gameTipPanel == null || !GodotObject.IsInstanceValid(_gameTipPanel))
        {
            if (!_lookForTooltip.Due()) return false;   // not there yet: looked for now and then
            _gameTip = GameAccess.FindGameView()?.GetNodeOrNull<Control>("ItemTooltip");
            _gameTipPanel = null;
            if (_gameTip == null) return false;
            var inside = new List<ItemInfoPanel>();
            void Walk(Node node) { if (node is ItemInfoPanel p) inside.Add(p); else foreach (Node child in node.GetChildren()) Walk(child); }
            Walk(_gameTip);
            _gameTipPanel = inside.FirstOrDefault();
            if (_gameTipPanel == null) return false;
            // One quiet line at the foot of the game's tooltip, so the key can be found.
            if (_gameTipPanel.GetParent() is BoxContainer box && box.GetNodeOrNull("ItemSourcesHint") == null)
            {
                _gameTipHint = _style.Text("Alt: keep this open", GameStyle.TextDim, 11);
                _gameTipHint.Name = "ItemSourcesHint";
                _gameTipHint.HorizontalAlignment = HorizontalAlignment.Right;
                _gameTipHint.MouseFilter = Control.MouseFilterEnum.Ignore;
                box.AddChild(_gameTipHint);
            }
        }
        if (!_gameTipPanel.IsVisibleInTree()) return false;
        item = ItemOf(_gameTipPanel);
        card = GameTipCard();
        return item != null;
    }

    /// <summary>The dark card the game's tooltip draws its item panel on.</summary>
    Control GameTipCard()
    {
        for (Node? up = _gameTipPanel!.GetParent(); up != null && up != _gameTip!.GetParent(); up = up.GetParent())
            if (up is PanelContainer panel) return panel;
        return _gameTipPanel;
    }

    void Pin(Item item, Control gameCard)
    {
        Unpin();
        if (_pinFailed || _gameTipPanel == null) return;
        // The copy touches parts of the game's panel nothing else here does. Should one of them be gone in a
        // game version, that shows when PinCopy is first run, and only this feature is switched off.
        try { PinCopy(item, gameCard, _gameTipPanel); }
        catch (Exception e)
        {
            _pinFailed = true;
            _ctx.Log("Keeping a tooltip open with Alt is switched off (the game was updated?): " + e.Message);
            Unpin();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void PinCopy(Item item, Control gameCard, ItemInfoPanel original)
    {
        if (GameStyle.Load<PackedScene>(ItemPanelScene)?.Instantiate() is not ItemInfoPanel copy) throw new InvalidOperationException("the item panel's scene was not found");
        if (_pinLayer == null || !GodotObject.IsInstanceValid(_pinLayer))
        {
            _pinLayer = new CanvasLayer { Layer = 118 };
            _ctx.Tree.Root.AddChild(_pinLayer);
        }
        // The same card, the same panel, set up the way the game's tooltip sets its own.
        var card = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        card.AddThemeStyleboxOverride("panel", gameCard is PanelContainer ? gameCard.GetThemeStylebox("panel") : _style.TooltipPanel());
        var column = _style.Spaced(new VBoxContainer(), 8);
        card.AddChild(column);
        copy.ShowIcon = original.ShowIcon;
        copy.IconSize = original.IconSize;
        copy.ShowOpenableCapability = original.ShowOpenableCapability;
        copy.CustomMinimumSize = original.CustomMinimumSize;
        column.AddChild(copy);
        Label hint = _style.Text("Kept open  ·  Alt or Esc closes", GameStyle.TextDim, 11);
        hint.HorizontalAlignment = HorizontalAlignment.Right;
        hint.MouseFilter = Control.MouseFilterEnum.Ignore;
        column.AddChild(hint);
        _pinLayer.AddChild(card);
        // Known to Unpin from here on, so nothing is left on the screen if binding fails.
        _pinCard = card;
        _pinPanel = copy;
        card.Position = gameCard.GlobalPosition;
        // Bound once it is in the scene: the panel builds itself when it is ready.
        if (InventoryOf(original) is { } inventory) copy.Bind(item, inventory); else copy.Bind(item);
    }

    void Unpin()
    {
        if (_pinPanel != null && GodotObject.IsInstanceValid(_pinPanel)) { try { _pinPanel.Unbind(); } catch { } }
        if (_pinCard != null && GodotObject.IsInstanceValid(_pinCard)) _pinCard.QueueFree();
        _pinCard = null;
        _pinPanel = null;
    }

    static void KeepOnScreen(Control card)
    {
        Vector2 screen = card.GetViewportRect().Size, size = card.Size;
        Vector2 at = new(Math.Clamp(card.Position.X, 0, Math.Max(0, screen.X - size.X)), Math.Clamp(card.Position.Y, 0, Math.Max(0, screen.Y - size.Y)));
        if (at != card.Position) card.Position = at;
    }

#if DEVTOOLS
    // Development: a file "item.dump" writes every item panel of the scene to the log, shown or not, with the
    // two rows this mod watches and what sits around them.
    void DevDump()
    {
        var all = new List<ItemInfoPanel>();
        void Walk(Node node) { if (node is ItemInfoPanel p) all.Add(p); for (int i = 0, n = node.GetChildCount(); i < n; i++) Walk(node.GetChild(i)); }
        Walk(_ctx.Tree.Root);
        Node? view = GameAccess.FindGameView();
        if (view?.GetNodeOrNull("ItemTooltip") is { } gameTip)
        {
            _ctx.Log($"Dev: the game's item tooltip is a {gameTip.GetType().FullName}, layer/parent {gameTip.GetParent()?.GetType().Name}:{gameTip.GetParent()?.Name}, top level {(gameTip as Control)?.TopLevel}, z {(gameTip as CanvasItem)?.ZIndex}");
            int budget = 90;
            DevTree(gameTip, 0, ref budget);
        }
        _ctx.Log($"Dev: {all.Count} item panels in the scene, {_panels.Count} watched; mouse {_ctx.Tree.Root.GetMousePosition()}; game view {view?.GetPath()}");
        foreach (ItemInfoPanel panel in all)
        {
            _ctx.Log($"Dev: panel {panel.GetPath()} shown={panel.IsVisibleInTree()} inView={(view != null && view.IsAncestorOf(panel))} item={ItemOf(panel)?.DisplayName.GetPluralText(1) ?? "-"} rect={panel.GetGlobalRect()}");
            foreach (var (row, name) in new[] { (FoundRowOf(panel), "found"), (UsedRowOf(panel), "used") })
            {
                if (row == null) { _ctx.Log($"Dev:   {name}: no row"); continue; }
                _ctx.Log($"Dev:   {name}: {panel.GetPathTo(row)} shown={row.IsVisibleInTree()} rect={row.GetGlobalRect()} children={row.GetChildCount()}");
                for (Node? up = row.GetParent(); up != null && up != panel.GetParent(); up = up.GetParent())
                {
                    string kids = string.Join(", ", up.GetChildren().Select(c => $"{c.GetType().Name}:{c.Name}{(c is Label l ? "='" + l.Text + "'" : "")}{(c is CanvasItem { Visible: false } ? "(hidden)" : "")}"));
                    _ctx.Log($"Dev:     up {up.GetType().Name}:{up.Name} rect={(up as Control)?.GetGlobalRect()} mouse={(up as Control)?.MouseFilter} [{kids}]");
                    if (up == panel) break;
                }
            }
        }
    }

    // Development: the look of a part of the game's scene, node by node: styles, fonts, sizes, colours.
    void DevTree(Node node, int depth, ref int budget)
    {
        if (budget-- <= 0 || depth > 9) return;
        string line = new string(' ', depth * 2) + $"{node.GetType().Name}:{node.Name}";
        if (node.SceneFilePath != "") line += $" scene={node.SceneFilePath}";
        if (node is CanvasItem { Visible: false }) line += " (hidden)";
        if (node is Control control)
        {
            line += $" size={control.Size} min={control.CustomMinimumSize} mouse={control.MouseFilter}";
            if (control.ThemeTypeVariation != "") line += $" variation={control.ThemeTypeVariation}";
            if (control.Modulate != Colors.White) line += $" modulate={control.Modulate.ToHtml()}";
            if (control.SelfModulate != Colors.White) line += $" selfModulate={control.SelfModulate.ToHtml()}";
        }
        string Box(StyleBox? box) => box switch
        {
            StyleBoxTexture t => $"texture {t.Texture?.ResourcePath} size={t.Texture?.GetSize()} texMargins={t.TextureMarginLeft},{t.TextureMarginTop},{t.TextureMarginRight},{t.TextureMarginBottom} content={t.ContentMarginLeft},{t.ContentMarginTop},{t.ContentMarginRight},{t.ContentMarginBottom} expand={t.ExpandMarginLeft},{t.ExpandMarginTop},{t.ExpandMarginRight},{t.ExpandMarginBottom} stretch={t.AxisStretchHorizontal}/{t.AxisStretchVertical} modulate={t.ModulateColor.ToHtml()} region={t.RegionRect} center={t.DrawCenter} path={t.ResourcePath}",
            StyleBoxFlat f => $"flat bg={f.BgColor.ToHtml()} border={f.BorderColor.ToHtml()} widths={f.BorderWidthLeft},{f.BorderWidthTop},{f.BorderWidthRight},{f.BorderWidthBottom} radius={f.CornerRadiusTopLeft} content={f.ContentMarginLeft},{f.ContentMarginTop},{f.ContentMarginRight},{f.ContentMarginBottom} path={f.ResourcePath}",
            StyleBoxLine l => $"line color={l.Color.ToHtml()} thickness={l.Thickness} grow={l.GrowBegin},{l.GrowEnd} path={l.ResourcePath}",
            null => "none",
            _ => box.GetType().Name + " " + box.ResourcePath,
        };
        switch (node)
        {
            case PanelContainer panel: line += " panel=[" + Box(panel.GetThemeStylebox("panel")) + "]"; break;
            case Panel panel: line += " panel=[" + Box(panel.GetThemeStylebox("panel")) + "]"; break;
            case Label label:
                line += $" text='{label.Text}' font={(label.LabelSettings?.Font ?? label.GetThemeFont("font"))?.ResourcePath} size={label.LabelSettings?.FontSize ?? label.GetThemeFontSize("font_size")} color={(label.LabelSettings?.FontColor ?? label.GetThemeColor("font_color")).ToHtml()} settings={label.LabelSettings?.ResourcePath} wrap={label.AutowrapMode}";
                break;
            case TextureRect picture: line += $" texture={picture.Texture?.ResourcePath} expand={picture.ExpandMode} stretch={picture.StretchMode}"; break;
            case Separator separator: line += " separator=[" + Box(separator.GetThemeStylebox("separator")) + $"] separation={separator.GetThemeConstant("separation")}"; break;
            case BoxContainer box: line += $" separation={box.GetThemeConstant("separation")} align={box.Alignment}"; break;
            case MarginContainer margin: line += $" margins={margin.GetThemeConstant("margin_left")},{margin.GetThemeConstant("margin_top")},{margin.GetThemeConstant("margin_right")},{margin.GetThemeConstant("margin_bottom")}"; break;
            case FlowContainer flow: line += $" hsep={flow.GetThemeConstant("h_separation")} vsep={flow.GetThemeConstant("v_separation")}"; break;
        }
        _ctx.Log("Dev: " + line);
        foreach (Node child in node.GetChildren()) DevTree(child, depth + 1, ref budget);
    }

    // Development: a file "item.request" holding part of an item's name (and optionally "|used") writes that
    // item's lines to the log and shows its tooltip in the middle of the screen for a picture.
    int _devShotAt = -1, _devHoverUntil = -1;
    bool _devHoverFound = true;
    Item? _devItem;
    bool _devFound = true;

    // Development: a file "item.steps", one command per line, with a pointer that is made, not moved: the mod
    // reads it instead of the real one, and the game gets a mouse motion at the same spot, so its own tooltips
    // (the words on a chance) follow.
    //   pin=part of an item's name   (as "item.pin"; "pin=Bones@0.02,0.3" puts it there, in parts of the screen) | unpin
    //   bank   (the game's bank, for the panel of its selected item) | alt   (the Alt key, down and up, as a player's)
    //   point=found | used | list | chance1 (the first chance that says more; chance0 is the last) | between | away | 640,360
    //   glide=the same   (there in small moves, a hand crossing; the log says where the list was up on the way)
    //   click=the same   (the left button, down and up, there: selects an item in the bank)
    //   wait=600 | shot=name   (shot-name.png, and to the log: the list, what the pointer is on, the tooltip's words)
    //   free   (the real pointer again; it also comes back after the last line)
    // "item.done" is written after the last line.
    Vector2? _devPointer;
    Queue<string> _devSteps = new();
    ulong _devStepAt;
    string _devGlide = "";

    Vector2? DevSpot(string what)
    {
        ItemInfoPanel? panel = _pinPanel != null && GodotObject.IsInstanceValid(_pinPanel) ? _pinPanel
            : _panels.FirstOrDefault(p => GodotObject.IsInstanceValid(p) && p.IsVisibleInTree() && !ReferenceEquals(p, _gameTipPanel) && ItemOf(p) != null);
        Rect2? found = panel != null && FoundRowOf(panel) is { } f && f.IsVisibleInTree() ? f.GetGlobalRect() : null;
        Rect2? list = _tip != null && GodotObject.IsInstanceValid(_tip) && _layer is { Visible: true } ? _tip.GetGlobalRect() : null;
        Vector2 screen = _ctx.Tree.Root.GetVisibleRect().Size;
        if (what == "found") return found?.GetCenter();
        if (what == "used") return panel != null && UsedRowOf(panel) is { } u && u.IsVisibleInTree() ? u.GetGlobalRect().GetCenter() : null;
        if (what == "list") return list?.GetCenter();
        if (what == "away") return new Vector2(screen.X * 0.04f, screen.Y * 0.92f);
        if (what == "between" && found is { } r && list is { } l)
            return new Vector2((l.GetCenter().X < r.GetCenter().X ? l.End.X + r.Position.X : l.Position.X + r.End.X) / 2, r.GetCenter().Y);
        if (what.StartsWith("chance", StringComparison.Ordinal) && int.TryParse(what[6..], out int n) && list != null)
        {
            var chances = _tip!.FindChildren("*", "Label", true, false).OfType<Label>().Where(c => c.TooltipText != "").ToList();
            return chances.Count == 0 ? null : (n <= 0 || n > chances.Count ? chances[^1] : chances[n - 1]).GetGlobalRect().GetCenter();
        }
        string[] xy = what.Split(',');
        return xy.Length == 2 && float.TryParse(xy[0], System.Globalization.CultureInfo.InvariantCulture, out float x) && float.TryParse(xy[1], System.Globalization.CultureInfo.InvariantCulture, out float y)
            ? new Vector2(x, y) : null;
    }

    void DevPoint(Vector2 at)
    {
        _devPointer = at;
        Vector2 spot = _ctx.Tree.Root.GetFinalTransform() * at;
        Input.ParseInputEvent(new InputEventMouseMotion { Position = spot, GlobalPosition = spot });
    }

    void DevSteps()
    {
        string steps = Path.Combine(_ctx.Directory, "item.steps");
        if (_devSteps.Count == 0)
        {
            if (_frame % 20 != 0 || !File.Exists(steps)) return;
            foreach (string line in File.ReadAllLines(steps)) if (line.Trim() != "") _devSteps.Enqueue(line.Trim());
            File.Delete(steps);
            _devStepAt = Time.GetTicksMsec() + 200;
            return;
        }
        if (Time.GetTicksMsec() < _devStepAt) return;
        string command = _devSteps.Dequeue();
        int cut = command.IndexOf('=');
        string verb = cut < 0 ? command : command[..cut], value = cut < 0 ? "" : command[(cut + 1)..];
        _devStepAt = Time.GetTicksMsec() + (ulong)(verb == "wait" && int.TryParse(value, out int ms) ? ms : verb == "_to" ? 35 : 300);
        bool listUp = _layer is { Visible: true };
        switch (verb)
        {
            case "pin": File.WriteAllText(Path.Combine(_ctx.Directory, "item.pin"), value); _devStepAt += 900; break;
            case "unpin": Unpin(); break;
            case "alt":
                Input.ParseInputEvent(new InputEventKey { Keycode = Key.Alt, PhysicalKeycode = Key.Alt, Pressed = true });
                _devSteps = new Queue<string>(_devSteps.Prepend("_altup"));
                break;
            case "_altup":
                Input.ParseInputEvent(new InputEventKey { Keycode = Key.Alt, PhysicalKeycode = Key.Alt, Pressed = false });
                _devSteps = new Queue<string>(_devSteps.Prepend("_alted"));
                break;
            case "_alted":
                _ctx.Log($"Dev: Alt pressed -> a tooltip is kept open = {_pinCard != null && GodotObject.IsInstanceValid(_pinCard)}; the game's window has the keyboard = {DisplayServer.WindowIsFocused()}; " +
                         $"the game's tooltip shows {(GameTipItem(out Item? shown, out _) ? shown!.DisplayName.GetPluralText(1) : "nothing")}");
                break;
            case "bank": GameAccess.FindGameView()?.OpenBank(); break;
            case "free": _devPointer = null; break;
            case "wait": break;
            case "point":
                if (DevSpot(value) is { } spot) { DevPoint(spot); _ctx.Log($"Dev: pointer on {value} at {spot}"); }
                else _ctx.Log($"Dev: nothing called '{value}' to point at");
                break;
            case "click":
                if (DevSpot(value) is { } press)
                {
                    DevPoint(press);
                    Vector2 there = _ctx.Tree.Root.GetFinalTransform() * press;
                    Input.ParseInputEvent(new InputEventMouseButton { Position = there, GlobalPosition = there, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
                    Input.ParseInputEvent(new InputEventMouseButton { Position = there, GlobalPosition = there, ButtonIndex = MouseButton.Left, Pressed = false });
                    _ctx.Log($"Dev: clicked {value} at {press}");
                }
                else _ctx.Log($"Dev: nothing called '{value}' to click");
                break;
            case "glide":
                if (DevSpot(value) is { } target)
                {
                    Vector2 from = _devPointer ?? target;
                    const int moves = 14;
                    var way = Enumerable.Range(1, moves).Select(i => FormattableString.Invariant($"_to={from.X + (target.X - from.X) * i / moves},{from.Y + (target.Y - from.Y) * i / moves}"));
                    _devSteps = new Queue<string>(way.Append("_glided=" + value).Concat(_devSteps));
                    _devGlide = "";
                    _ctx.Log($"Dev: gliding from {from} to {value} at {target}");
                }
                else _ctx.Log($"Dev: nothing called '{value}' to glide to");
                break;
            case "_to":
                _devGlide += listUp ? "1" : "0";
                if (DevSpot(value) is { } next) DevPoint(next);
                break;
            case "_glided":
                _ctx.Log($"Dev: on the way to {value} the list was {_devGlide} (1 up, 0 gone), and is {(listUp ? "up" : "gone")} now");
                break;
            case "shot":
                _ctx.Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(_ctx.Directory, $"shot-{value}.png"));
                Control? over = _ctx.Tree.Root.GuiGetHoveredControl();
                Rect2? card = _pinCard != null && GodotObject.IsInstanceValid(_pinCard) ? _pinCard.GetGlobalRect() : null;
                _ctx.Log($"Dev: shot-{value}.png; pointer {_devPointer}; list {(listUp ? "up" : "gone")} for '{_shown}' at {(listUp && _tip != null ? _tip.GetGlobalRect() : default)}; kept tooltip at {card}; " +
                         $"the pointer is on {(over == null ? "nothing" : over.GetType().Name + ":" + over.Name + (over is Label label ? " '" + label.Text + "'" : ""))}; tooltip: {GameStyle.DevTooltips(_ctx.Tree.Root)}");
                break;
            default: _ctx.Log("Dev: unknown step " + command); break;
        }
        if (_devSteps.Count == 0)
        {
            _devPointer = null;
            File.WriteAllText(Path.Combine(_ctx.Directory, "item.done"), DateTime.Now.ToString("O"));
        }
    }

    void DevRequests()
    {
        DevSteps();
        string request = Path.Combine(_ctx.Directory, "item.request");
        string dump = Path.Combine(_ctx.Directory, "item.dump");
        // "item.pin" holding part of an item's name: as if Alt were pressed with the pointer on that item.
        string pin = Path.Combine(_ctx.Directory, "item.pin");
        if (_frame % 30 == 0 && File.Exists(pin) && _game != null)
        {
            string[] asked = File.ReadAllText(pin).Trim().Split('@');
            string wanted = asked[0].Trim();
            float[] place = asked.Length > 1 ? asked[1].Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray() : new[] { 0.45f, 0.3f };
            File.Delete(pin);
            GameTipItem(out _, out _);
            Item? pick = _game.Data.GetDataRegistryOutput<Item>().GetAllObjects().FirstOrDefault(i => i.DisplayName.GetPluralText(1).Contains(wanted, StringComparison.OrdinalIgnoreCase));
            _ctx.Log($"Dev: pin '{wanted}' -> {pick?.Id}; game tooltip {(_gameTip == null ? "not found" : _gameTip.GetPath())}, panel {(_gameTipPanel == null ? "not found" : "found")}");
            if (pick != null && _gameTipPanel != null)
            {
                Vector2 screen = _ctx.Tree.Root.GetVisibleRect().Size;
                Pin(pick, GameTipCard());
                if (_pinCard != null) _pinCard.Position = new Vector2(screen.X * place[0], screen.Y * place[1]);
                _ctx.Log($"Dev: pinned = {_pinCard != null}, failed = {_pinFailed}");
            }
        }
        string hover = Path.Combine(_ctx.Directory, "item.hover");
        if (_frame % 30 == 0 && File.Exists(hover))
        {
            _devHoverFound = !File.ReadAllText(hover).Trim().Equals("used", StringComparison.OrdinalIgnoreCase);
            File.Delete(hover);
            _devHoverUntil = _frame + 90;
        }
        if (_devHoverUntil > 0 && _frame == _devHoverUntil - 1)
        {
            _ctx.Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(_ctx.Directory, "shot-hover.png"));
            _ctx.Log($"Dev: saved shot-hover.png, tooltip shown for '{_shown}', layer visible {_layer is { Visible: true }}");
        }
        if (_frame % 30 == 0 && File.Exists(dump))
        {
            File.Delete(dump);
            DevDump();
        }
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
                    : string.Join(" | ", refs.Select(_lines!.Describe).OrderByDescending(l => l.Chance).Take(40).Select(l => $"[{l.Group}] {l.Name} {l.Quantity} {ItemLines.Percent(l.Chance)}"))));
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
