#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GamesByMalcs.Melvor2.CustomControls;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace CombatSim;

/// <summary>
/// Development (issue #44): the simulator's window measured against its size rule (LargestScale, WidthAt, HeightAt) and
/// the game's bars, and a pop-up of the game measured beside it. Lines of sim.request (DevWindowCommand):
/// <list type="bullet">
/// <item>size=log: the "Dev: window" line (DevLogWindow), without a run or a picture, once the window has been drawn twice
/// since it was last built (3 s at most).</item>
/// <item>size=every: the window built at every size from the smallest to the largest this screen allows, in the corner's
/// steps of 5%, with size=log after each.</item>
/// <item>scale=1.05: the window built at that size, as scale=min and scale=max build it; put back with the settings.</item>
/// <item>popup=statistics: the game's own pop-up behind a button of its header (HeaderStatisticsButton, and so on for
/// milestones or masteryguild), opened by a click on that button as a player's, measured, and closed by its own X.</item>
/// <item>spellnow=longest: the Spell box's first choice made to read "As now: " and the widest spell name of the game's
/// books, in the simulator's own words only, for its picture and its words (SimView.DevSpellNowLongest); put back from the
/// game at the end of the request.</item>
/// </list>
/// </summary>
public partial class CombatSimMod
{
    // The frame count and the clock when the window was last built (Build), for size=log.
    (long Frame, long Ms) _devBuiltAt;
    bool DevDrawnSinceBuilt => (long)Engine.GetFramesDrawn() >= _devBuiltAt.Frame + 2;

    // The request waits at a size=log line until the window was drawn twice since it was last built, or for 3 s, so that a
    // game that draws nothing (minimised) does not hold the request and its put back for ever.
    bool DevSizeWaits(string command, long now) => command == "size=log" && !DevDrawnSinceBuilt && now < _devBuiltAt.Ms + 3000;

    // The size= and scale=<size> lines, and popup=. False for a line that is none of them.
    bool DevSizeCommand(string command)
    {
        if (command == "size=log")
        {
            if (_window is not { IsAlive: true, Visible: true, Minimised: false })
            {
                _ctx.Log("Dev: size=log: the simulator's window is not open (closed, folded or not made yet); nothing was measured.");
                return true;
            }
            if (!DevDrawnSinceBuilt) _ctx.Log("Dev: size=log: the window was not drawn twice in 3 s since it was built; the next line is its laid-out size.");
            DevLogWindow("size=log");
            return true;
        }
        if (command == "size=every")
        {
            // In front of the lines still to come.
            float largest = LargestScale(FooterHeight());
            var rest = _devCommands.ToList();
            _devCommands.Clear();
            for (int step = (int)MathF.Round(GameWindow.MinScale * 20); step <= (int)MathF.Round(largest * 20); step++)
            {
                _devCommands.Enqueue($"scale={step / 20f:0.00}".Replace(',', '.'));
                _devCommands.Enqueue("size=log");
            }
            foreach (string line in rest) _devCommands.Enqueue(line);
            _ctx.Log($"Dev: size=every: the window at every size from {GameWindow.MinScale:0.##} to {largest:0.##}, the largest on this screen.");
            return true;
        }
        if (command.StartsWith("scale=", StringComparison.Ordinal))
        {
            if (!float.TryParse(command[6..], NumberStyles.Float, CultureInfo.InvariantCulture, out float wanted))
            {
                _ctx.Log($"Dev: {command}: a size is min, max or a number such as 1.05.");
                return true;
            }
            float size = Math.Clamp(MathF.Round(wanted * 20) / 20, GameWindow.MinScale, GameWindow.MaxScale), largest = LargestScale(FooterHeight());
            if (size > largest + 0.001f) _ctx.Log($"Dev: {command}: larger than {largest:0.##}, the largest size on this screen; the window is built at that.");
            _settings.Scale = size;
            Build();
            _window!.Visible = true;
            return true;
        }
        if (command.StartsWith("popup=", StringComparison.Ordinal))
        {
            DevPopupStart(command[6..].Trim());
            return true;
        }
        if (command.StartsWith("spellnow=", StringComparison.Ordinal))
        {
            DevSpellNow(command[9..].Trim());
            return true;
        }
        return false;
    }

    // The window against the size rule and the game's bottom bar, with what the Setup column shows, so the line can be read
    // alone. The run request, size=log and fight.request (FightSetup.DevLayout) log this same line. The rule's sizes must
    // cover the window as it really is at every size (WidthAt, HeightAt), and at the largest size its bottom is at or above
    // the top of the bar. "Needs" is what the rule would need at full size to hold this window, its kept parts
    // (KeptWidth, KeptHeight) as they are. "why" says which request logged it.
    void DevLogWindow(string why)
    {
        GameWindow window = _window!;
        Vector2 size = window.Root.Size, screen = _ctx.Tree.Root.GetVisibleRect().Size;
        float scale = window.Style.Scale, wide = WidthAt(scale), high = HeightAt(scale);
        Rect2 frame = window.Root.GetGlobalRect();
        static float Needs(float length, float scale, float kept) => scale >= 1 ? length / scale : (length - kept) / scale + kept;
        string fits = size.X > wide + 0.5f || size.Y > high + 0.5f ? "  LARGER THAN LargestScale ALLOWS" : ", it fits";
        _ctx.Log($"Dev: window {size.X:0}x{size.Y:0} at size {scale:0.##} (chosen {_settings.Scale:0.##}, largest {LargestScale(FooterHeight()):0.##}), {size.X / scale:0}x{size.Y / scale:0} at full size; " +
                 $"LargestScale allows {wide:0.#}x{high:0.#} at this size{fits}; this window needs FullWidth {Needs(size.X, scale, KeptWidth):0.#} and FullHeight {Needs(size.Y, scale, KeptHeight):0.#} " +
                 $"(now {FullWidth:0} and {FullHeight:0}); screen {screen.X:0}x{screen.Y:0}, {DevBottomBar(screen, out float barTop)}, window top at {frame.Position.Y:0}, window bottom at {frame.End.Y:0}, " +
                 (frame.End.Y <= barTop + 0.5f ? $"above the bottom bar by {Math.Max(0, barTop - frame.End.Y):0} px" : $"OVER THE BOTTOM BAR by {frame.End.Y - barTop:0} px") +
                 $"; Setup: {_view?.DevSetupShows(DevGameSpells()) ?? "(no view)"} ({why})");
    }

    // Where the game's bottom bar is, read from the bar itself: FooterHeight reads only its height, and the rule takes the
    // bar as lying along the bottom edge of the screen. A bar that does not shows here.
    string DevBottomBar(Vector2 screen, out float top)
    {
        if (GameAccess.FindGameView()?.FindChild("Footer", true, false) is not Control { } footer || !footer.IsVisibleInTree())
        {
            top = screen.Y;
            return "no bottom bar on show";
        }
        Rect2 bar = footer.GetGlobalRect();
        top = bar.Position.Y;
        return $"the bottom bar {bar.Size.Y:0} high starts at {bar.Position.Y:0} and ends at {bar.End.Y:0}" +
               (Math.Abs(bar.End.Y - screen.Y) > 0.5f ? $" (NOT ALONG THE BOTTOM EDGE OF THE SCREEN, at {screen.Y:0})" : "");
    }

    // The names and pictures of every spell of the game's books, for the Spell box's words (SimView.DevSpellWords). Read only.
    List<(string Name, Texture2D? Icon)> DevGameSpells()
    {
        try
        {
            return _combat?.MagicSpells.ActiveSpellBooks.Values.SelectMany(book => book.Spells)
                .Select(spell => (Name: spell.DisplayName.Text, Icon: (Texture2D?)spell.Icon)).DistinctBy(spell => spell.Name).ToList() ?? new();
        }
        catch (Exception e)
        {
            if (_reported.Add("dev spells")) _ctx.Log("Dev: the game's spells could not be read: " + e.Message);
            return new();
        }
    }

    // spellnow=longest (SimView.DevSpellNowLongest) changed the words of the Spell box's first choice; the put back makes
    // them again from the game.
    bool _devSpellNowSet;

    void DevSpellNow(string what)
    {
        if (what != "longest")
        {
            _ctx.Log($"Dev: spellnow={what}: the line is spellnow=longest.");
            return;
        }
        string? name = _view?.DevSpellNowLongest(DevGameSpells());
        if (name == null)
        {
            _ctx.Log("Dev: spellnow=longest: the Spell box is not on show, or the game's spells could not be read; nothing changed.");
            return;
        }
        _devSpellNowSet = true;
        Build();
        _window!.Visible = true;
        _ctx.Log($"Dev: spellnow=longest: the Spell box's first choice reads 'As now: {name}', in the simulator's own words only; " +
                 $"the game's selected spell, {_combat?.MagicSpells.SelectedSpell?.DisplayName.Text ?? "none"}, is not touched.");
    }

    // For DevPutBack: the first choice's words as the game has them (BuildGear), after spellnow=longest.
    void DevSpellNowBack()
    {
        if (!_devSpellNowSet) return;
        _devSpellNowSet = false;
        BuildGear();
        _ctx.Log($"Dev: also put back: the Spell box's first choice made again from the game: '{_state.SpellDefault}'.");
    }

    // ---- popup=: a pop-up of the game, beside the window ------------------------------------------------------------

    // Under way: the game's button, where it was pressed and where the pointer was before; the pop-ups on show before the
    // click; the one that opened, its frame as last seen and for how many updates it has held still; the stage (0 the
    // pointer is on the button, 1 waiting for the pop-up, 2 waiting for it to hold still, 3 waiting for it to close), since
    // when; and whether the simulator's window was put out of the way for it.
    Control? _devPopupButton;
    PanelContainerPopup? _devPopup;
    readonly HashSet<PanelContainerPopup> _devPopupsBefore = new();
    Vector2 _devPopupSpot, _devPopupPointer;
    Rect2 _devPopupFrame;
    int _devPopupStage, _devPopupStill;
    long _devPopupSince;
    bool _devPopupAside;

    // A button of the game's header whose kind is named like the word (HeaderStatisticsButton for "statistics"): its
    // pop-up is opened the way a player opens it, by a click on it. The simulator's window is put out of the way first
    // (hidden, as its X hides it), so the click cannot land on it. Nothing else of the game is touched.
    void DevPopupStart(string word)
    {
        if (GameAccess.FindGameView() is not { } view) { _ctx.Log("Dev: popup: the game's view was not found."); return; }
        var buttons = DevAll<Control>(view).Where(c => c.IsVisibleInTree() && c.GetType().Name.StartsWith("Header", StringComparison.Ordinal)
                                                     && c.GetType().Name.EndsWith("Button", StringComparison.Ordinal)).ToList();
        Control? button = word == "" ? null : buttons.FirstOrDefault(b => b.GetType().Name.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (button == null)
        {
            _ctx.Log($"Dev: popup: no button of the game's header named like '{word}' is on show; those on show: {string.Join(", ", buttons.Select(b => b.GetType().Name)).Or("none")}.");
            return;
        }
        _devPopupsBefore.Clear();
        foreach (PanelContainerPopup open in DevAll<PanelContainerPopup>(_ctx.Tree.Root).Where(p => p.IsVisibleInTree())) _devPopupsBefore.Add(open);
        _devPopupAside = _window is { IsAlive: true, Visible: true };
        if (_devPopupAside) { _window!.Visible = false; SyncStripButton(); }
        _devPopupButton = button;
        _devPopup = null;
        _devPopupPointer = _ctx.Tree.Root.GetMousePosition();
        _devPopupSpot = button.GetGlobalRect().Intersection(_ctx.Tree.Root.GetVisibleRect()).GetCenter();
        DevPopupPointer(_devPopupSpot);
        _devPopupStage = 0;
        _devPopupSince = (long)Time.GetTicksMsec();
        _ctx.Log($"Dev: popup: the pointer onto the game's {button.GetType().Name} at {button.GetGlobalRect()}{(_devPopupAside ? "; the simulator's window put out of the way" : "")}; " +
                 $"pop-ups on show before: {string.Join(", ", _devPopupsBefore.Select(p => p.GetType().Name)).Or("none")}.");
    }

    // One update of popup=; true while it goes on (the request waits meanwhile, DevRequests).
    bool DevPopupMeasuring()
    {
        if (_devPopupButton == null) return false;
        long now = (long)Time.GetTicksMsec();
        switch (_devPopupStage)
        {
            case 0:
            {
                // As EnemyCards.DevPress clicks a button of the game: only when what is under the pointer is that button,
                // or a part of it that passes the click on.
                Control button = _devPopupButton;
                Control? over = GodotObject.IsInstanceValid(button) ? button.GetViewport().GuiGetHoveredControl() : null;
                if (!GodotObject.IsInstanceValid(button) || !EnemyCards.DevPassesOn(over, button))
                {
                    if (now - _devPopupSince < 1000) return true;
                    return DevPopupDone($"Dev: popup: not clicked: the pointer over the button is on {EnemyCards.DevName(over)}, which would not pass a click on to it.");
                }
                Vector2 spot = _ctx.Tree.Root.GetFinalTransform() * _devPopupSpot;
                Input.ParseInputEvent(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
                Input.ParseInputEvent(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = false });
                DevPopupPointer(_devPopupPointer);
                (_devPopupStage, _devPopupSince) = (1, now);
                return true;
            }
            case 1:
                _devPopup = DevAll<PanelContainerPopup>(_ctx.Tree.Root).FirstOrDefault(p => p.IsVisibleInTree() && !_devPopupsBefore.Contains(p));
                if (_devPopup != null) (_devPopupStage, _devPopupSince, _devPopupStill, _devPopupFrame) = (2, now, 0, _devPopup.GetGlobalRect());
                else if (now - _devPopupSince > 3000) return DevPopupDone($"Dev: popup: the click on the game's {_devPopupButton.GetType().Name} opened no pop-up in 3 s.");
                return true;
            case 2:
            {
                PanelContainerPopup popup = _devPopup!;
                if (!GodotObject.IsInstanceValid(popup) || !popup.IsVisibleInTree()) return DevPopupDone("Dev: popup: the pop-up closed before it was measured.");
                // It opens with an animation: measured once its frame has held still for five updates (3 s at most).
                Rect2 frame = popup.GetGlobalRect();
                _devPopupStill = frame == _devPopupFrame ? _devPopupStill + 1 : 0;
                _devPopupFrame = frame;
                if (_devPopupStill < 5 && now - _devPopupSince < 3000) return true;
                DevLogPopup(popup, frame);
                if (popup.CloseButton is { } close && GodotObject.IsInstanceValid(close) && close.IsVisibleInTree())
                {
                    DevClick(close, "the pop-up's X");
                    DevPopupPointer(_devPopupPointer);
                }
                else
                {
                    _ctx.Log($"Dev: popup: the {popup.GetType().Name} has no X on show; it is closed with its own Close().");
                    popup.Close();
                }
                (_devPopupStage, _devPopupSince) = (3, now);
                return true;
            }
            default:
            {
                PanelContainerPopup? popup = _devPopup;
                if (popup == null || !GodotObject.IsInstanceValid(popup) || !popup.IsVisibleInTree())
                    return DevPopupDone($"Dev: popup: put back: the {popup?.GetType().Name ?? "pop-up"} closed again{(_devPopupAside ? ", and the simulator's window shown again" : "")}.");
                if (now - _devPopupSince < 3000) return true;
                // Its X did not close it: the game's own way of closing it.
                popup.Close();
                return DevPopupDone($"Dev: popup: its X did not close the {popup.GetType().Name} in 3 s; closed with its own Close(){(_devPopupAside ? ", and the simulator's window shown again" : "")}.");
            }
        }
    }

    // Where the pop-up is against the game's header and bottom bar, and the largest size the simulator's rule would give if
    // its window, too, were kept between the two.
    void DevLogPopup(PanelContainerPopup popup, Rect2 frame)
    {
        Vector2 screen = _ctx.Tree.Root.GetVisibleRect().Size;
        string bottom = DevBottomBar(screen, out float barTop);
        // The header: the outermost control around the button that starts at the top of the screen and is a strip of it.
        Control? button = GodotObject.IsInstanceValid(_devPopupButton) ? _devPopupButton : null, header = null;
        for (Node? node = button; node != null; node = node.GetParent())
            if (node is Control c && c.IsVisibleInTree() && c.GetGlobalRect() is var r && r.Position.Y <= 1 && r.Size.Y < screen.Y * 0.2f && r.Size.X >= screen.X * 0.4f)
                header = c;
        float headerEnd = header?.GetGlobalRect().End.Y ?? button?.GetGlobalRect().End.Y ?? 0;
        float room = barTop - headerEnd;
        // As LargestScale, with this room.
        float kept = Math.Clamp(MathF.Floor(Math.Min(Fits(screen.X, FullWidth, KeptWidth), Fits(room, FullHeight, KeptHeight)) * 20f) / 20f, GameWindow.MinScale, GameWindow.MaxScale);
        _ctx.Log($"Dev: popup: the game's {popup.GetType().Name}, opened by a click on its {_devPopupButton!.GetType().Name}: from x {frame.Position.X:0} to {frame.End.X:0}, " +
                 $"from y {frame.Position.Y:0} to {frame.End.Y:0} ({frame.Size.X:0}x{frame.Size.Y:0}); " +
                 $"the game's header {(header == null ? "not found as a strip; its button ends" : $"{header.Name} ({header.GetType().Name}) {header.GetGlobalRect()}, ends")} at y {headerEnd:0}; " +
                 $"{bottom}; the pop-up's top {(frame.Position.Y >= headerEnd - 0.5f ? "BELOW THE HEADER" : "OVER THE HEADER")}, its bottom {(frame.End.Y <= barTop + 0.5f ? "above the bottom bar" : "OVER THE BOTTOM BAR")}; " +
                 $"between the header and the bottom bar {room:0} px, where the simulator's rule gives size {kept:0.##} (with the room above the bottom bar alone {LargestScale(FooterHeight()):0.##}).");
    }

    bool DevPopupDone(string said)
    {
        _ctx.Log(said);
        if (_devPopupAside && _window is { IsAlive: true } window) { window.Visible = true; SyncStripButton(); }
        _devPopupButton = null;
        _devPopup = null;
        _devPopupsBefore.Clear();
        _devPopupAside = false;
        return false;
    }

    void DevPopupPointer(Vector2 at)
    {
        Vector2 spot = _ctx.Tree.Root.GetFinalTransform() * at;
        Input.ParseInputEvent(new InputEventMouseMotion { Position = spot, GlobalPosition = spot });
    }

    // Every node of a kind under this one.
    static IEnumerable<T> DevAll<T>(Node node) where T : Node
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is T found) yield return found;
            foreach (T inner in DevAll<T>(child)) yield return inner;
        }
    }
}
#endif
