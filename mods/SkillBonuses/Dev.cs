#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Mining;
using GamesByMalcs.Melvor2.Entities.Woodcutting;
using GamesByMalcs.Melvor2.GameMenus.Pages;
using GamesByMalcs.Melvor2.InfoLabels;
using GamesByMalcs.Melvor2.Tooltips;
using Godot;
using MelvorMods.Shared;

namespace SkillBonuses;

// Development: a file "bonuses.request" in the mod's folder drives the mod with nobody at the keyboard, one
// command per line, or the single word "tour" for the whole game check (see Tour below):
//   page=Woodcutting   opens that skill page the way the sidebar does (the skill's local id)
//   probe              logs every stats panel of the page, read from the game's panels alone, without the mod's
//                      window: where it sits, whether the game has bound it, and for each stat its page text,
//                      total, the sum of its sources, every source with its kind, and the words of the game's
//                      own tooltip for that stat
//   list               logs what the mod found on the page (the recipes of the drop-down) and whether the round
//                      button is shown
//   pages              opens every skill page in turn and lists each: the pages without the button
//   hover | unhover    rests the pointer on the first stat row of a panel on screen, or on the first card's
//                      stats icon, so the game shows its own tooltip; then moves it away
//   press              presses the mod's round button through the game's input, as a click does
//   click=picker|row|heading|fold|close   clicks one of the mod's own controls through the game's input
//   key=escape         presses Escape (closes an open drop-down)
//   choose=2 | choose=Oak Tree   picks a recipe in the window's drop-down (the second, or by name)
//   scale=0.6          builds the window at that size, as a drag of its corner does
//   fold | unfold | open | close | wait
//   shot=name          saves shot-name.png (the whole screen) and, with the window open, crop-name.png
//   restore            back to the page, size and fold of before the request
// Every press and click logs the game's running actions, the page's selected recipe and the card in progress
// before and after it: "same" means the click reached nothing of the game's. When the last line is done,
// "bonuses.done" says "ok", or "failed: why".
public partial class SkillBonusesMod
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Tooltip")]
    static extern TextTooltip? StatTooltip(ModifierStatLabel label);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_contentText")]
    static extern ref List<string> Content(TextTooltip tooltip);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Text")]
    static extern RichTextLabel? TooltipText(TextTooltip tooltip);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Title")]
    static extern Label? TooltipTitle(TextTooltip tooltip);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_WoodcuttingStatsIcon")]
    static extern TextureRect? StatsIcon(WoodcuttingTreeSelectView card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_MiningStatsIcon")]
    static extern TextureRect? StatsIcon(MiningRockSelectView card);

    Queue<string> _devQueue = new();
    readonly Tempo _devLook = new(300);
    ulong _devNext;
    string _devBefore = "";
    (EntityPage? Page, float? Scale, bool Minimised, bool Wanted) _devStart;

    string DevDone => Path.Combine(_ctx.Directory, "bonuses.done");

    // A request that goes wrong is the helper's fault, not the mod's: it is logged and dropped, and the mod goes on.
    void DevRequests()
    {
        try
        {
            if (_devQueue.Count == 0)
            {
                string request = Path.Combine(_ctx.Directory, "bonuses.request");
                if (!_devLook.Due() || !File.Exists(request)) return;
                var lines = File.ReadAllLines(request).Select(l => l.Trim()).Where(l => l != "").ToList();
                File.Delete(request);
                File.Delete(DevDone);
                if (lines is ["tour"]) lines = Tour();
                foreach (string line in lines) _devQueue.Enqueue(line);
                _devStart = (_page, _settings.Scale, _settings.Minimised, _wanted);
                _ctx.Log($"Dev: {lines.Count} commands; the mod's button icon is {_stripButton?.Icon?.ResourcePath ?? "none"}.");
                _devNext = Time.GetTicksMsec() + 200;
                return;
            }
            if (Time.GetTicksMsec() < _devNext) return;
            string command = _devQueue.Dequeue();
            _devNext = Time.GetTicksMsec() + (ulong)DevStep(command);
            if (_devQueue.Count == 0) File.WriteAllText(DevDone, "ok");
        }
        catch (Exception e)
        {
            _ctx.Log("Dev: the request failed and the rest of it was dropped: " + e);
            _devQueue.Clear();
            try { File.WriteAllText(DevDone, "failed: " + e.Message); } catch { }
        }
    }

    // One round that covers every kind of page: a page of cards (Woodcutting), a page with one panel (Fishing,
    // Smithing), Farming, Thieving, Agility, Astrology; the game's own tooltip next to the window; the window at
    // its smallest and largest sizes and folded. About a minute. "pages" is a request of its own (every skill
    // page takes two seconds), for the list of the pages without the button.
    static List<string> Tour() =>
    [
        "page=Woodcutting", "probe", "list", "shot=page-woodcutting", "hover", "wait", "shot=native-woodcutting", "unhover",
        "press", "wait", "shot=woodcutting", "click=heading", "click=row", "click=picker", "wait", "shot=woodcutting-picker",
        "key=escape", "choose=2", "wait", "shot=woodcutting-second", "choose=1",
        "click=fold", "wait", "shot=woodcutting-folded", "click=fold", "wait",
        "scale=0.6", "wait", "shot=woodcutting-smallest", "scale=1.6", "wait", "shot=woodcutting-largest", "scale=1",
        "page=Fishing", "probe", "list", "hover", "wait", "shot=native-fishing", "unhover", "wait", "shot=fishing",
        "page=Smithing", "probe", "list", "wait", "shot=smithing", "click=row",
        "page=Farming", "probe", "list", "wait", "shot=farming",
        "page=Thieving", "probe", "list", "wait", "shot=thieving", "click=row",
        "page=Agility", "probe", "list", "wait", "shot=agility",
        "page=Astrology", "probe", "list", "wait", "shot=astrology",
        "click=close", "wait", "shot=closed", "restore",
    ];

    // Runs one command; returns how long to wait before the next one, in milliseconds.
    int DevStep(string command)
    {
        int cut = command.IndexOf('=');
        string verb = cut < 0 ? command : command[..cut], value = cut < 0 ? "" : command[(cut + 1)..];
        switch (verb)
        {
            case "page":
                if (SkillPages().FirstOrDefault(p => p.Entity.LocalId.Equals(value, StringComparison.OrdinalIgnoreCase)) is { } page)
                {
                    _gameView!.SetCurrentPage(page);
                    _ctx.Log($"Dev: page {page.Entity.LocalId} opened.");
                }
                else _ctx.Log($"Dev: no skill page '{value}'; the skill pages are {string.Join(", ", SkillPages().Select(p => p.Entity.LocalId))}.");
                return 1500;
            case "probe": Probe(); return 300;
            case "list": _ctx.Log("Dev: " + Found()); return 200;
            case "pages":
                var steps = SkillPages().SelectMany(p => new[] { "page=" + p.Entity.LocalId, "list" }).ToList();
                _devQueue = new Queue<string>(steps.Concat(_devQueue));
                return 0;
            case "hover":
                if (HoverTarget() is { } target)
                {
                    Pointer(target.GetGlobalRect().GetCenter());
                    _devQueue = new Queue<string>(_devQueue.Prepend("_hovered=" + target.GetType().Name));
                    return 1300;
                }
                _ctx.Log("Dev: nothing of the game's to hover on this page.");
                return 200;
            case "_hovered": _ctx.Log($"Dev: hovering {value}: {GameTooltips()}; Godot tooltips: {GameStyle.DevTooltips(_ctx.Tree.Root)}"); return 200;
            case "unhover": Pointer(new Vector2(-20, -20)); return 300;
            case "press":
                if (_stripButton == null || !GodotObject.IsInstanceValid(_stripButton) || !_stripButton.IsVisibleInTree()) { _ctx.Log("Dev: the round button is not shown."); return 200; }
                Click(_stripButton, "the round button");
                return 600;
            case "click":
                Control? control = value switch
                {
                    "fold" => _window?.Root.FindChildren("*", "Button", true, false).OfType<Button>().FirstOrDefault(b => b.TooltipText is "Fold down to the title" or "Open the window again"),
                    "close" => _window?.Root.FindChildren("*", "Button", true, false).OfType<Button>().FirstOrDefault(b => b.Text == "X"),
                    _ => _list?.DevControl(value),
                };
                if (control == null || !control.IsVisibleInTree()) { _ctx.Log($"Dev: no {value} to click."); return 200; }
                Click(control, value);
                return 600;
            case "_after":
                string after = Actions();
                _ctx.Log($"Dev: {value}: {(after == _devBefore ? "same" : "CHANGED")}. Before: {_devBefore}. After: {after}. Window open {_window is { Visible: true }}, folded {_window?.Minimised}.");
                return 200;
            case "key":
                Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = true });
                Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = false });
                return 300;
            case "choose":
                int index = int.TryParse(value, out int n) ? n - 1 : _choices.FindIndex(c => c.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
                if (index < 0 || index >= _choices.Count) { _ctx.Log($"Dev: no choice '{value}' among {_choices.Count}."); return 200; }
                Choose(index);
                _ctx.Log($"Dev: chose {_choices[index].Name}.");
                return 400;
            case "scale":
                float scale = float.Parse(value, CultureInfo.InvariantCulture);
                _settings.Scale = Math.Clamp(Math.Min(scale, LargestScale(FooterHeight())), GameWindow.MinScale, GameWindow.MaxScale);
                if (_window is { IsAlive: true }) { Create(ShownScale(GameWindow.MaxScale), FooterHeight()); Sync(); }
                _ctx.Log($"Dev: scale {value} asked, {_window?.Style.Scale:0.##} built (largest {LargestScale(FooterHeight()):0.##}).");
                return 700;
            case "fold": _window?.SetMinimised(true); return 500;
            case "unfold": _window?.SetMinimised(false); return 500;
            case "open": SetWanted(true); return 600;
            case "close": SetWanted(false); return 400;
            case "wait": return 900;
            case "shot": Shot(value); return 300;
            case "restore":
                _settings.Scale = _devStart.Scale;
                _settings.Minimised = _devStart.Minimised;
                Save();
                if (_devStart.Page != null && !ReferenceEquals(_devStart.Page, _page)) _gameView?.SetCurrentPage(_devStart.Page);
                _wanted = _devStart.Wanted;
                _chosen = null;
                _chosenOn.Clear();
                ReleaseCopy();
                if (_window is { IsAlive: true }) { _window.Free(); _window = null; _list = null; _builtFor = null; }
                _ctx.Log("Dev: page, size and fold restored.");
                return 600;
            default: _ctx.Log("Dev: unknown command " + command); return 200;
        }
    }

    IEnumerable<EntityPage> SkillPages() => _game == null ? [] : _game.Data.Database.GetDataClassRegistry<EntityPage>().GetAllObjects()
        .Where(p => p.Entity is ILeveledEntity<ExperienceComponent>);

    string Found() =>
        $"page {_page?.Entity.LocalId ?? "-"}: {_choices.Count} with bonuses ({_choices.Count(c => c.Card != null)} cards, {_choices.Count(c => c.Card == null)} panels), " +
        $"round button {(_stripButton is { } b && GodotObject.IsInstanceValid(b) && b.Visible ? "shown" : "hidden")}: " +
        string.Join("; ", _choices.Select(c => $"{c.Name} [{(c.Card?.GetType().Name ?? c.Panel.GetType().Name)}, {SkillStats.Labels(c.Panel).Count} rows bound by the game{(SkillStats.InProgress(c) ? ", in progress" : "")}]"));

    // What a click of the mod could change if it reached the game: the running actions, the recipe a panel is
    // showing (the game's selection), the card in progress.
    string Actions()
    {
        if (_game == null) return "-";
        string running = string.Join(", ", _game.ActionManager.ActiveActions.Select(a =>
            $"{a.Entity.LocalId}:{a.CurrentDescription?.DisplayName.Text ?? "-"}{(a.IsStarted ? "" : " (not started)")}"));
        var found = _page == null || _gameView == null ? new List<Choice>() : SkillStats.Find(_gameView.EntityViewContainer, _page.Entity.DisplayName.Text);
        return $"running [{running}], panels show [{string.Join(", ", found.Where(c => c.Card == null).Select(c => c.Name))}], " +
               $"in progress [{string.Join(", ", found.Where(SkillStats.InProgress).Select(c => c.Name))}]";
    }

    void Click(Control control, string what)
    {
        _devBefore = Actions();
        Vector2 spot = _ctx.Tree.Root.GetFinalTransform() * control.GetGlobalRect().GetCenter();
        Input.ParseInputEvent(new InputEventMouseMotion { Position = spot, GlobalPosition = spot });
        Input.ParseInputEvent(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
        Input.ParseInputEvent(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = false });
        _devQueue = new Queue<string>(_devQueue.Prepend("_after=click on " + what));
    }

    // The pointer comes to rest there, and nothing is pressed: what a tooltip waits for.
    void Pointer(Vector2 at)
    {
        Vector2 spot = _ctx.Tree.Root.GetFinalTransform() * at;
        Input.ParseInputEvent(new InputEventMouseMotion { Position = spot, GlobalPosition = spot });
    }

    Control? HoverTarget()
    {
        foreach (Choice choice in _choices.Where(c => c.Card == null))
            if (SkillStats.Labels(choice.Panel).FirstOrDefault(l => l.IsVisibleInTree()) is { } label) return (Control?)SkillStats.Name(label) ?? label;
        foreach (Choice choice in _choices)
        {
            TextureRect? icon = choice.Card switch { WoodcuttingTreeSelectView w => StatsIcon(w), MiningRockSelectView m => StatsIcon(m), _ => null };
            if (icon != null && icon.IsVisibleInTree()) return icon;
        }
        return null;
    }

    // The game's own tooltips that are up, and what they say.
    string GameTooltips()
    {
        var up = new List<string>();
        void Walk(Node node)
        {
            if (node is Tooltip { IsShown: true } tooltip)
            {
                string words = tooltip is TextTooltip text ? $"'{TooltipTitle(text)?.Text}' '{TooltipText(text)?.GetParsedText()}'" : "";
                var panel = tooltip.FindChildren("*", "", true, false).OfType<ModifierStatView>().FirstOrDefault();
                up.Add($"{tooltip.GetType().Name} {words}{(panel != null ? $" with a {panel.GetType().Name} of {SkillStats.Labels(panel).Count} rows" : "")}");
            }
            foreach (Node child in node.GetChildren()) Walk(child);
        }
        Walk(_ctx.Tree.Root);
        return up.Count == 0 ? "no tooltip of the game's is up" : string.Join(" / ", up);
    }

    // Everything the page's own panels hold, without the mod's window or button.
    void Probe()
    {
        if (_gameView == null) return;
        _ctx.Log($"Dev: probe of page {_page?.Entity.LocalId ?? "-"} (the game's panels only):");
        int panels = 0;
        void Walk(Node node, string where)
        {
            if (node is ModifierStatView panel)
            {
                panels++;
                LogPanel(panel, where);
                return;
            }
            string here = SkillStats.IsCard(node) ? node.GetType().Name : node is Tooltip ? where + " > tooltip" : where;
            if (node is WoodcuttingTreeSelectView w && panels == 0) _ctx.Log($"Dev:   the cards' stats icon is {StatsIcon(w)?.Texture?.ResourcePath ?? "-"}");
            foreach (Node child in node.GetChildren()) Walk(child, here);
        }
        Walk(_gameView.EntityViewContainer, "page");
        _ctx.Log($"Dev: probe done, {panels} panels.");
    }

    void LogPanel(ModifierStatView panel, string where)
    {
        var labels = SkillStats.Labels(panel);
        string shows = SkillStats.Describe(panel, labels)?.Name ?? "-";
        _ctx.Log($"Dev:   {panel.GetType().Name} in {where}, {(panel.IsVisibleInTree() ? "on screen" : "not on screen")}, scene '{panel.SceneFilePath}', " +
                 $"{labels.Count} rows bound, shows {shows}");
        foreach (ModifierStatLabel label in labels)
        {
            var nodes = SkillStats.Nodes(label)!;
            var sources = nodes.Sources.Value ?? new HashSet<GamesByMalcs.Melvor2.Modifiers.ModifierEntry>();
            TextTooltip? tooltip = StatTooltip(label);
            _ctx.Log($"Dev:     {SkillStats.Name(label)?.Text} ({label.StatName}, {label.ValueFormat}): page '{SkillStats.Number(label)?.Text}', " +
                     $"total {nodes.Total.Value.ToString("R", CultureInfo.InvariantCulture)}, sum {sources.Sum(s => s.Value).ToString("R", CultureInfo.InvariantCulture)}; " +
                     string.Join(", ", sources.Select(s => $"{s.Source?.DisplayName.Text} [{s.Source?.GetType().Name}] {s.Value.ToString("R", CultureInfo.InvariantCulture)}")));
            _ctx.Log($"Dev:       its own tooltip: content '{label.StatTooltipContent}', text '{(tooltip == null ? "" : string.Join(" ", Content(tooltip) ?? []))}', " +
                     $"shown as '{(tooltip == null ? "" : TooltipText(tooltip)?.GetParsedText())}'");
        }
    }

    void Shot(string name)
    {
        Image screen = _ctx.Tree.Root.GetTexture().GetImage();
        Error saved = screen.SavePng(Path.Combine(_ctx.Directory, $"shot-{name}.png"));
        if (saved != Error.Ok) throw new IOException($"shot-{name}.png: {saved}");
        string about = $"page {_page?.Entity.LocalId ?? "-"}";
        if (_window is { IsAlive: true, Visible: true })
        {
            // The picture is in the screen's pixels, the window in the game's own, which the game stretches.
            Rect2 frame = _ctx.Tree.Root.GetFinalTransform() * _window.Root.GetGlobalRect().Grow(10);
            var whole = new Rect2I(0, 0, screen.GetWidth(), screen.GetHeight());
            Rect2I part = new Rect2I((int)frame.Position.X, (int)frame.Position.Y, (int)frame.Size.X, (int)frame.Size.Y).Intersection(whole);
            if (part.Size.X > 0 && part.Size.Y > 0) screen.GetRegion(part).SavePng(Path.Combine(_ctx.Directory, $"crop-{name}.png"));
            int index = _choices.Count == 0 ? -1 : Selected();
            about += $", showing {(index < 0 ? "-" : _choices[index].Name)} ({(index >= 0 && _copy != null && ReferenceEquals(_copy.For.Model, _choices[index].Model) ? "copy of the card's panel" : "the game's panel")}), " +
                     $"window {_window.Root.GetGlobalRect()} at size {_window.Style.Scale:0.##} (largest {LargestScale(FooterHeight()):0.##}), folded {_window.Minimised}, " +
                     $"bottom bar {FooterHeight():0}, screen {_ctx.Tree.Root.GetVisibleRect().Size}; lines: {string.Join(" | ", _list?.Shown ?? [])}";
        }
        else about += ", window closed";
        _ctx.Log($"Dev: saved shot-{name}.png ({screen.GetWidth()}x{screen.GetHeight()}): {about}");
    }
}
#endif
