#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace CombatGoals;

static partial class Dev
{
    // "window.request" (what it holds is not read) takes pictures of the combat window's frame and compares its title
    // bar with the Item Encyclopedia's and the Combat Simulator's. It needs a fight going and nothing else: a closed or
    // folded window is opened with what F10 and the round button do (ToggleWindow), and everything is put back at the
    // end. One step a second; every picture is shot-window-<name>.png (the screen) and crop-window-<name>.png (the
    // window), with a log line of what the window is and what its title bar is made of:
    //   open (with every canvas layer and the layer of each of the game's tooltips); folded (the fold button clicked);
    //   opened (clicked again: the same place and size?); readme (at 100%, never saved: the picture for the guides,
    //   and the period row beside the footer's own period part, crop-period-goals.png and crop-period-footer.png from
    //   the same picture, compared number by number); min and max (70% and 200%, never saved); first (built as if no
    //   place were saved: the first place, 60 from the screen's right edge and above the bottom bar, never saved; read
    //   back against where the place rule has it with the lines it has, and when it is not there, why, as far as can
    //   be told; built once more when a hand was at the mouse meanwhile); then for the Item Encyclopedia and for the
    //   Combat Simulator, each opened with its own round button when it is closed: the combat window at that window's
    //   size and fold, beside it, shot-window-beside-<other>.png, crop-title-goals-<other>.png and
    //   crop-title-<other>.png, and the two title bars compared number by number.
    // A hand at the mouse: the request's clicks are pushed into the game and never move the real pointer, so a real
    // pointer that moved, or a mouse button held down, means somebody is at the game. Then no step runs until the
    // pointer has been still for 5 seconds; after 90 seconds the request stops and puts everything back. A picture
    // taken after a hand was at the mouse says so in its line.
    // The fold button is clicked through the game's input as a hand would (the real pointer is not moved, and the
    // game's idea of it is put back at once); when another window lies over the button, the button's own press is
    // sent instead, and the log says so. Put back at the end: the combat window closed, folded or open as it was, at
    // its place and the player's size, with the same values in settings.json. The Combat Simulator is closed again with
    // its own X. The Item Encyclopedia's X and round button run its Close, which also notes the current search among
    // its recent ones and writes its settings: its window is hidden instead, which is the rest of what that Close
    // does (its round button follows within half a second). Both are closed only when the request opened them. The
    // last line compares the three mods' settings.json with what they were. "window.done" says how it ended.

    // A window of another mod to compare the title bar with: found by its round button's name and by its title.
    sealed record OtherWindow(string Name, string Short, string Button, string Folder, bool CloseWithX, Func<string, bool> IsTitle);

    static readonly OtherWindow[] s_others =
    {
        new("the Item Encyclopedia", "encyclopedia", "ItemEncyclopediaButton", "ItemEncyclopedia", false, t => t == "Item Encyclopedia"),
        // Folded during a scan its title is the scan's count ("Simulator · 4 of 38").
        new("the Combat Simulator", "simulator", "CombatSimButton", "CombatSim", true,
            t => t == "Combat Simulator" || t.StartsWith("Simulator · ", StringComparison.Ordinal)),
    };

    static bool IsOurs(string title) => title == CombatGoalsMod.Title;

    sealed record WindowBefore(CombatGoalsMod.DevState Goals, Dictionary<string, string?> Files);

    const int MaxWait = 6;
    const string FoldTip = "Fold down to the title", OpenTip = "Open the window again";
    // A hand at the mouse: how long the pointer must be still before a step runs, and how long the request waits for it.
    const ulong StillFor = 5000, GiveUpAfter = 90000;

    static int s_windowStep = -1, s_windowWait, s_other, s_linesOpen;
    static string? s_next;
    static WindowBefore? s_windowBefore;
    static bool s_openedOther, s_goalsPutBack, s_firstAgain;
    static Rect2 s_windowOpen;
    static (float? X, float? Y) s_savedAtFirst;
    static Vector2I s_pointer;
    static bool s_pointerSeen, s_handSinceStep;
    static ulong s_handAt, s_guardSince;

    // One step a second, in this order. A step gives false to be run again a second later (it waits for something,
    // MaxWait times at most), sets s_next to go on elsewhere, and ends the request itself when it cannot go on.
    static readonly (string Name, Func<bool> Run)[] s_windowSteps =
    {
        ("open", OpenForPictures),
        ("picture open", PictureOpen),
        ("fold", PressFold),
        ("picture folded", PictureFolded),
        ("open again", PressFold),
        ("picture opened", PictureOpened),
        ("game size", () => { s_mod.SetPicture(1f); return true; }),
        ("picture for the guides", PictureReadme),
        ("smallest", () => { s_mod.SetPicture(CombatGoalsMod.MinScale); return true; }),
        ("picture smallest", () => Picture("min", CombatGoalsMod.MinScale) != null),
        ("largest", () => { s_mod.SetPicture(CombatGoalsMod.MaxScale); return true; }),
        ("picture largest", () => Picture("max", CombatGoalsMod.MaxScale) != null),
        ("first place", BuildAtFirstPlace),
        ("picture first place", PictureFirstPlace),
        ("other: open", OpenOther),
        ("other: same size", MatchOther),
        ("other: beside", PlaceBesideOther),
        ("other: picture", PictureBesideOther),
        ("put back", PutBack),
        ("wait for the files", () => true),
        ("wait for the files again", () => true),
        ("compare the files", CompareFiles),
    };

    static void WindowRequest()
    {
        WatchHand();
        string path = Path.Combine(s_ctx.Directory, "window.request");
        if (File.Exists(path))
        {
            File.Delete(path);
            if (s_windowStep >= 0) WindowDone("replaced by a new window.request");
            s_windowStep = 0;
            s_windowWait = 0;
            s_handSinceStep = false;
            s_firstAgain = false;
        }
        if (s_windowStep < 0) return;
        try { WindowStep(); }
        catch (Exception e) { WindowDone("failed: " + e); }
    }

    static void WindowStep()
    {
        var (name, run) = s_windowSteps[s_windowStep];
        if (HandAtMouse(name)) return;
        bool done = run();
        s_handSinceStep = false;
        if (s_windowStep < 0) return;   // the step ended the request
        if (!done)
        {
            if (++s_windowWait >= MaxWait) WindowDone($"stopped: step '{name}' waited {MaxWait} seconds");
            return;
        }
        s_windowWait = 0;
        int next = s_next is { } jump ? Array.FindIndex(s_windowSteps, s => s.Name == jump) : s_windowStep + 1;
        s_next = null;
        if (next < 0 || next >= s_windowSteps.Length) WindowDone("done");
        else s_windowStep = next;
    }

    // Once a second, whether a request runs or not: where the real pointer is on the screen, and whether a mouse
    // button is down. Both are the system's own, which the request's pushed clicks do not change.
    static void WatchHand()
    {
        Vector2I pointer = DisplayServer.MouseGetPosition();
        if ((s_pointerSeen && pointer != s_pointer) || DisplayServer.MouseGetButtonState() != default)
        {
            s_handAt = Time.GetTicksMsec();
            s_handSinceStep = true;
        }
        s_pointer = pointer;
        s_pointerSeen = true;
    }

    // True while the next step waits for a hand to leave the mouse. After GiveUpAfter the request ends, which puts
    // everything back.
    static bool HandAtMouse(string step)
    {
        ulong now = Time.GetTicksMsec();
        if (s_handAt == 0 || now - s_handAt >= StillFor)
        {
            if (s_guardSince != 0)
                s_ctx.Log($"Dev: window request: the real pointer has been still for {StillFor / 1000} s; step '{step}' goes on after {(now - s_guardSince) / 1000.0:0} s.");
            s_guardSince = 0;
            return false;
        }
        if (s_guardSince == 0)
        {
            s_guardSince = now;
            s_ctx.Log($"Dev: window request: the real pointer moves or a mouse button is down, somebody is at the mouse; step '{step}' waits " +
                      $"until the pointer has been still for {StillFor / 1000} s.");
        }
        else if (now - s_guardSince > GiveUpAfter) WindowDone($"stopped: somebody was at the mouse for {GiveUpAfter / 1000} s, step '{step}' did not run");
        return true;
    }

    // Said in the line of a picture taken after a hand was at the mouse.
    static string HandNote => s_handSinceStep ? "; A HAND WAS AT THE MOUSE since the step before: this picture may show what it did" : "";

    // ---- the combat window ----------------------------------------------------------------------------------------

    static bool OpenForPictures()
    {
        if (!s_mod.InFight) { WindowDone("stopped: it needs a fight going (the combat window is only there in combat)"); return false; }
        if (s_mod.Window is not { } window) { WindowDone("stopped: there is no combat window (no character open?)"); return false; }
        if (s_windowBefore == null)
        {
            s_goalsPutBack = false;
            s_windowBefore = new WindowBefore(s_mod.DevNow, SettingsFiles());
            s_ctx.Log($"Dev: window request: the combat window is {(s_windowBefore.Goals.Hidden ? "closed" : window.Minimised ? "folded" : "open")}; {s_mod.WindowReading}");
        }
        if (window.Visible && !window.Minimised) return true;
        // As F10 or the round button: a closed window opens, and a second later a folded one opens up.
        s_mod.DevToggle();
        s_ctx.Log($"Dev: did what F10 and the round button do: visible={window.Visible}, folded={window.Minimised}");
        return false;
    }

    static bool PictureOpen()
    {
        if (Picture("open") == null) return false;
        GameWindow window = s_mod.Window!;
        s_windowOpen = window.Root.GetGlobalRect();
        s_linesOpen = s_mod.Lines;
        s_ctx.Log("Dev: layers: " + Layers(window));
        return true;
    }

    static bool PressFold()
    {
        if (s_mod.Window is not { Visible: true } window || FoldButton(window) is not { } fold)
        {
            WindowDone("stopped: the fold button is not on the screen");
            return false;
        }
        if (Covered(window, fold) is { } over)
        {
            // A pushed click would reach that window instead: the button's own press, as a click sends it.
            fold.EmitSignal(BaseButton.SignalName.Pressed);
            s_ctx.Log($"Dev: {over} lies over the fold button ('{fold.TooltipText}'): its press was sent instead of a click");
            return true;
        }
        Press(fold);
        s_ctx.Log($"Dev: clicked the fold button ('{fold.TooltipText}') at {fold.GetGlobalRect()}");
        // At once, so its tooltip is not in the next picture.
        PointerBack();
        return true;
    }

    static bool PictureFolded()
    {
        if (s_mod.Window is not { Minimised: true } window) { WindowDone("stopped: the fold button did not fold the window"); return false; }
        if (Picture("folded") == null) return false;
        Rect2 now = window.Root.GetGlobalRect();
        s_ctx.Log($"Dev: folded: right edge {now.End.X:0} (open: {s_windowOpen.End.X:0}), top {now.Position.Y:0} (open: {s_windowOpen.Position.Y:0}), " +
                  $"fold button says '{FoldButton(window)?.TooltipText}', round button lit: {s_mod.StripButtonLit}");
        return true;
    }

    static bool PictureOpened()
    {
        if (s_mod.Window is not { Minimised: false } window) { WindowDone("stopped: the fold button did not open the window again"); return false; }
        if (Picture("opened") == null) return false;
        Rect2 now = window.Root.GetGlobalRect();
        bool same = now.Position.DistanceTo(s_windowOpen.Position) < 1 && now.Size.DistanceTo(s_windowOpen.Size) < 1;
        s_ctx.Log($"Dev: opened again: {now}, before folding {s_windowOpen}: {(same ? "the same place and size" : "NOT the same")}" +
                  (s_mod.Lines != s_linesOpen ? $" ({s_linesOpen} lines then, {s_mod.Lines} now: the height follows the lines)" : "") +
                  $", round button lit: {s_mod.StripButtonLit}");
        return true;
    }

    // At 100%, the size of the game's own screens: the picture for the guides, and the period row beside the footer's.
    static bool PictureReadme()
    {
        if (Picture("readme", 1f) is not { } screen) return false;
        s_ctx.Log("Dev: period row: " + ComparePeriod(s_mod.Window!, screen));
        return true;
    }

    // As if no place were saved. A second try first builds the player's window again, so this one is built anew.
    static bool BuildAtFirstPlace()
    {
        if (s_firstAgain) s_mod.SetPicture();
        s_mod.SetPicture(firstPlace: true);
        s_savedAtFirst = (s_mod.DevNow.X, s_mod.DevNow.Y);
        return true;
    }

    // Read back: where the place rule has the window with the lines it has now, and whether it is there.
    static bool PictureFirstPlace()
    {
        if (Picture("first") == null) return false;
        Vector2 rule = s_mod.DevRulePlace(), at = s_mod.Window!.Position;
        bool there = at.DistanceTo(rule) < 1;
        s_ctx.Log($"Dev: first place: {s_mod.DevBuildReading}; with {s_mod.Lines} lines the place rule has it at {rule} (where it was put, " +
                  $"pushed up and left only as far as the screen and the bottom bar have needed since); it is at {at}: " +
                  (there ? "it is there" : $"it is NOT THERE, {at.X - rule.X:0.#} across and {at.Y - rule.Y:0.#} down from it; {WhyNotThere(at)}"));
        if (!there && s_handSinceStep && !s_firstAgain)
        {
            s_firstAgain = true;
            s_ctx.Log("Dev: first place: built once more, now that the pointer is still, and the picture taken again");
            s_next = "first place";
        }
        return true;
    }

    // What can be told of why the window is not where the place rule has it. Between building it and its picture the
    // request moves nothing.
    static string WhyNotThere(Vector2 at)
    {
        var why = new List<string>();
        if (s_handSinceStep) why.Add("a hand was at the mouse since it was built: it may have dragged the window");
        (float? X, float? Y) saved = (s_mod.DevNow.X, s_mod.DevNow.Y);
        if (saved != s_savedAtFirst)
            why.Add($"the saved place changed from {s_savedAtFirst} to {saved}: the window was let go somewhere (Moved), which a drag or the fold " +
                    "button does, and the request did neither");
        if (Math.Abs(at.X - MathF.Round(at.X)) > 0.01f || Math.Abs(at.Y - MathF.Round(at.Y)) > 0.01f)
            why.Add("its place has fractions: the rule puts it on whole pixels or against an edge, a drag moves it by fractions");
        return why.Count == 0 ? "no reason found" : string.Join("; ", why);
    }

    // A picture of the window as it is on the screen (the screen is given back), or one line and the end of the request
    // when it is not there. "asked": the size the step asked for, read back against the size the window was built at.
    static Image? Picture(string name, float? asked = null)
    {
        if (s_mod.Window is not { Visible: true } window)
        {
            WindowDone($"stopped before shot-window-{name}.png: the combat window is not on the screen (the fight ended?)");
            return null;
        }
        string size = asked is not { } scale ? ""
            : $"asked {scale:P0}, built {window.Style.Scale:P0}{(Math.Abs(window.Style.Scale - scale) < 0.001f ? "" : " (NOT AS ASKED)")}; ";
        Image screen = Screen($"shot-window-{name}.png");
        Crop(screen, window.Root.GetGlobalRect(), $"crop-window-{name}.png");
        s_ctx.Log($"Dev: shot-window-{name}.png {size}{s_mod.WindowReading}; title bar: {TitleBar(window.Root, IsOurs)?.ToString() ?? "TITLE BAR NOT FOUND"}{HandNote}");
        return screen;
    }

    static Button? FoldButton(GameWindow window) =>
        window.Root.FindChildren("*", "Button", true, false).OfType<Button>().FirstOrDefault(b => b.TooltipText is FoldTip or OpenTip);

    // A press and a release where the control is, through the game's input as a hand's would be; the real pointer
    // is not moved (PointerBack puts the game's idea of it back).
    static void Press(Control control)
    {
        Vector2 spot = s_ctx.Tree.Root.GetFinalTransform() * control.GetGlobalRect().GetCenter();
        Input.ParseInputEvent(new InputEventMouseMotion { Position = spot, GlobalPosition = spot });
        Input.ParseInputEvent(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
        Input.ParseInputEvent(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = false });
        s_pointerPushed = true;
    }

    // What lies over a control of the window: another canvas layer's panel drawn above the window at that spot
    // (a higher layer, or the same layer added later), which a pushed click would reach instead.
    static string? Covered(GameWindow window, Control control)
    {
        Vector2 spot = control.GetGlobalRect().GetCenter();
        var layers = s_ctx.Tree.Root.GetChildren().OfType<CanvasLayer>().ToList();
        int ours = layers.IndexOf(window.Layer);
        foreach (CanvasLayer layer in layers)
        {
            if (layer == window.Layer || !layer.Visible) continue;
            if (layer.Layer < window.Layer.Layer || (layer.Layer == window.Layer.Layer && layers.IndexOf(layer) < ours)) continue;
            if (layer.GetChildren().OfType<Control>().FirstOrDefault(c => c.Visible && c.GetGlobalRect().HasPoint(spot)) is { } over)
                return $"{layer.Name} (layer {layer.Layer}, {over.GetType().Name} at {over.GetGlobalRect()})";
        }
        return null;
    }

    // ---- the other two windows ------------------------------------------------------------------------------------

    static bool OpenOther()
    {
        OtherWindow other = s_others[s_other];
        if (s_windowWait == 0)
        {
            s_mod.SetPicture();   // the player's size and place again before it is matched to the other window
            s_openedOther = false;
            if (FindOther(other) != null)
            {
                s_ctx.Log($"Dev: {other.Name} is already open");
                return true;
            }
            if (StripButton(other.Button) is not { } button)
            {
                s_ctx.Log($"Dev: {other.Name} left out: its round button is not in the strip (is the mod there?)");
                s_next = NextOther();
                return true;
            }
            // A click on a round button that toggles: it flips, then says it was pressed (BaseButton's own order).
            button.ButtonPressed = !button.ButtonPressed;
            button.EmitSignal(BaseButton.SignalName.Pressed);
            s_openedOther = true;
            s_ctx.Log($"Dev: {other.Name} is closed: its round button was pressed");
        }
        // Its first opening for a character lists every item or fight: a moment.
        return FindOther(other) != null;
    }

    static bool MatchOther()
    {
        OtherWindow other = s_others[s_other];
        if (FindOther(other) is not { } theirs) { WindowDone($"stopped: {other.Name}'s window is not on the screen"); return false; }
        bool folded = theirs.Fold?.TooltipText == OpenTip;
        s_mod.SetPicture(theirs.Scale, folded);
        s_ctx.Log($"Dev: {other.Name}'s window is at {theirs.Scale:P0}{(folded ? ", folded" : "")} (its X is made {theirs.Close.CustomMinimumSize.X:0} wide, " +
                  "32 at 100%): the combat window is built so too");
        return true;
    }

    // The two title bars side by side at the same height: to the right of the other window, else to its left, else
    // under it (kept on the screen, it may then lie over the other's lower part). Never saved: MoveTo raises no Moved.
    static bool PlaceBesideOther()
    {
        OtherWindow other = s_others[s_other];
        if (FindOther(other) is not { } theirs || s_mod.Window is not { Visible: true } window)
        {
            WindowDone($"stopped: the combat window and {other.Name}'s are not both on the screen");
            return false;
        }
        const float gap = 12;
        Rect2 there = theirs.Root.GetGlobalRect();
        Vector2 size = window.Root.Size, screen = window.Root.GetViewportRect().Size;
        Vector2 at = there.End.X + gap + size.X <= screen.X ? new Vector2(there.End.X + gap, there.Position.Y)
            : there.Position.X - gap - size.X >= 0 ? new Vector2(there.Position.X - gap - size.X, there.Position.Y)
            : new Vector2(there.Position.X, there.End.Y + gap);
        s_mod.DevMoveTo(at);
        return true;
    }

    static bool PictureBesideOther()
    {
        OtherWindow other = s_others[s_other];
        if (s_mod.Window is not { Visible: true } window || TitleBar(window.Root, IsOurs) is not { } ours || FindOther(other) is not { } theirs)
        {
            WindowDone($"stopped: the two title bars are not both on the screen for the picture beside {other.Name}");
            return false;
        }
        Image screen = Screen($"shot-window-beside-{other.Short}.png");
        Crop(screen, ours.Bar, $"crop-title-goals-{other.Short}.png");
        Crop(screen, theirs.Bar, $"crop-title-{other.Short}.png");
        s_ctx.Log($"Dev: shot-window-beside-{other.Short}.png; combat goals: {ours}; {other.Name}: {theirs}; " +
                  $"the windows overlap: {window.Root.GetGlobalRect().Intersects(theirs.Root.GetGlobalRect())}; " +
                  $"compared at the same size: {Compare(ours, theirs)}");
        CloseOther(other, theirs);
        s_next = NextOther();
        return true;
    }

    static string NextOther() => ++s_other < s_others.Length ? "other: open" : "put back";

    // Only a window the request opened is closed again (see the top of this file for how).
    static void CloseOther(OtherWindow other, TitleParts theirs)
    {
        if (!s_openedOther) return;
        s_openedOther = false;
        if (other.CloseWithX) theirs.Close.EmitSignal(BaseButton.SignalName.Pressed);
        else if (theirs.Root.GetParent() is CanvasLayer layer) layer.Visible = false;
        s_ctx.Log($"Dev: {other.Name} closed again ({(other.CloseWithX ? "its own X" : "its window hidden")})");
    }

    static Button? StripButton(string name) =>
        GameAccess.FindGameView()?.FindChild("FavouritesContainer", true, false)?.GetNodeOrNull<Button>(name);

    // Another mod's window, found by what it shows: a canvas layer on the screen whose frame holds that title.
    static TitleParts? FindOther(OtherWindow other)
    {
        foreach (CanvasLayer layer in s_ctx.Tree.Root.GetChildren().OfType<CanvasLayer>())
            if (layer.Visible && layer.GetChildren().OfType<PanelContainer>().FirstOrDefault() is { } root && TitleBar(root, other.IsTitle) is { } parts)
                return parts;
        return null;
    }

    // ---- the period row and the footer's own period part ------------------------------------------------------------

    // The footer's period box (TimePeriodOptions) and the row it sits in with its clock.
    static Container? FooterPeriod() =>
        (GameAccess.FindGameView()?.FindChild("Footer", true, false)?.FindChild("TimePeriodOptions", true, false) as OptionButton)?.GetParent() as Container;

    // Both cropped from the same picture and compared number by number, in the game's pixels: at 100% the window's row
    // should be the footer's one to one.
    static string ComparePeriod(GameWindow window, Image screen)
    {
        if (window.Body.GetNodeOrNull<Container>("Period") is not { } ours) return "the window's period row NOT FOUND";
        if (FooterPeriod() is not { } theirs) return "the footer's period box was not found (the game was updated?)";
        if (!theirs.IsVisibleInTree()) return "the footer's period box is not on the screen";
        Crop(screen, PartsRect(ours), "crop-period-goals.png");
        Crop(screen, PartsRect(theirs), "crop-period-footer.png");
        Dictionary<string, string> a = PeriodNumbers(ours), b = PeriodNumbers(theirs);
        var different = a.Keys.Where(k => a[k] != b[k]).Select(k => $"{k}: {a[k]} here, {b[k]} in the footer").ToList();
        return $"at {window.Style.Scale:P0}, crop-period-goals.png and crop-period-footer.png; " +
               (different.Count == 0 ? $"all {a.Count} the same as the footer's" : $"{different.Count} of {a.Count} differ: {string.Join("; ", different)}") +
               $"; the footer's: {string.Join(", ", b.Select(n => $"{n.Key} {n.Value}"))}";
    }

    // From the clock's left edge to the box's right one: the window's row is as wide as the window, its parts at its end.
    static Rect2 PartsRect(Container row)
    {
        var parts = row.GetChildren().OfType<Control>().Where(c => c.Visible && c is TextureRect or OptionButton).Select(c => c.GetGlobalRect()).ToList();
        return parts.Count == 0 ? row.GetGlobalRect() : parts.Aggregate((a, b) => a.Merge(b));
    }

    // What a period row is made of: the clock and the box, how far apart, and how the box is drawn.
    static Dictionary<string, string> PeriodNumbers(Container row)
    {
        var shown = row.GetChildren().OfType<Control>().Where(c => c.Visible).ToList();
        TextureRect? clock = shown.OfType<TextureRect>().FirstOrDefault();
        OptionButton? box = shown.OfType<OptionButton>().FirstOrDefault();
        Rect2 clockAt = clock?.GetGlobalRect() ?? default, boxAt = box?.GetGlobalRect() ?? default;
        return new Dictionary<string, string>
        {
            ["parts"] = string.Join(" ", shown.Select(c => c.GetType().Name)),
            ["separation"] = row is BoxContainer ? $"{row.GetThemeConstant("separation")}" : $"none ({row.GetType().Name})",
            ["clock"] = $"{Path.GetFileName(clock?.Texture?.ResourcePath ?? "none")} {clockAt.Size.X:0}x{clockAt.Size.Y:0}, " +
                        $"modulate {clock?.Modulate.ToHtml()}/{clock?.SelfModulate.ToHtml()}",
            ["clock to box"] = $"{boxAt.Position.X - clockAt.End.X:0}",
            ["box centre below the clock's"] = $"{boxAt.GetCenter().Y - clockAt.GetCenter().Y:0}",
            ["box size"] = $"{boxAt.Size.X:0}x{boxAt.Size.Y:0}",
            ["box shows"] = box is { Selected: >= 0 } shows ? shows.GetItemText(shows.Selected) : "nothing",
            ["box variation"] = box?.ThemeTypeVariation ?? "no box",
            ["box normal"] = Box(box?.GetThemeStylebox("normal")),
            ["box hover"] = Box(box?.GetThemeStylebox("hover")),
            ["box font"] = $"{FontName(box?.GetThemeFont("font"))} {box?.GetThemeFontSize("font_size")}, colour {box?.GetThemeColor("font_color").ToHtml()}",
            ["box arrow"] = box?.GetThemeIcon("arrow") is { } arrow ? $"{arrow.GetWidth()}x{arrow.GetHeight()}" : "none",
        };
    }

    // ---- putting back -----------------------------------------------------------------------------------------------

    static bool PutBack()
    {
        PutBackGoals();
        s_ctx.Log($"Dev: put back: {s_mod.WindowReading}");
        return true;
    }

    static void PutBackGoals()
    {
        if (s_windowBefore is not { } before || s_goalsPutBack) return;
        s_goalsPutBack = true;
        s_mod.DevPutBack(before.Goals);
    }

    // Two seconds after the put back: this mod's settings.json is written on its next one-second tick, the Item
    // Encyclopedia's within two seconds of a change.
    static bool CompareFiles()
    {
        Dictionary<string, string?> now = SettingsFiles();
        var lines = s_windowBefore!.Files.Select(f => $"{f.Key} {SameFile(f.Key, f.Value, now.GetValueOrDefault(f.Key))}");
        WindowDone("done; settings.json after the request: " + string.Join(", ", lines));
        return true;
    }

    static string SameFile(string mod, string? before, string? after)
    {
        if (before == null && after == null) return "no file, as before";
        // This mod's own file may have been written again with its keys in another layout: its values are compared.
        if (mod == "CombatGoals")
        {
            string then = CombatGoalsMod.DevSettingsValues(before), now = CombatGoalsMod.DevSettingsValues(after);
            return then == now ? "the same values" : $"OTHER VALUES: before {then}, now {now}";
        }
        return before == after ? "unchanged" : $"CHANGED: before {before ?? "no file"}, now {after ?? "no file"}";
    }

    // The settings.json of this mod and of the two whose windows are compared, as text (null: no file). Read only.
    static Dictionary<string, string?> SettingsFiles()
    {
        string own = Path.GetFullPath(s_ctx.Directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string mods = Path.GetDirectoryName(own) ?? own;
        var files = new Dictionary<string, string?>();
        foreach (var (name, folder) in new[] { (Name: "CombatGoals", Folder: own) }.Concat(s_others.Select(o => (Name: o.Folder, Folder: Path.Combine(mods, o.Folder)))))
        {
            string file = Path.Combine(folder, "settings.json");
            files[name] = File.Exists(file) ? File.ReadAllText(file) : null;
        }
        return files;
    }

    // Puts back what the request changed (an other window it opened, the combat window as it was), and the game's idea
    // of the pointer where the real one is.
    static void WindowDone(string how)
    {
        s_windowStep = -1;
        try
        {
            if (s_openedOther && s_other < s_others.Length && FindOther(s_others[s_other]) is { } theirs) CloseOther(s_others[s_other], theirs);
            PutBackGoals();
        }
        finally
        {
            s_windowBefore = null;
            s_openedOther = false;
            s_other = 0;
            s_next = null;
            s_guardSince = 0;
            PointerBack();
            s_ctx.Log($"Dev: window request {how}. Now: {s_mod.WindowReading}");
            File.WriteAllText(Path.Combine(s_ctx.Directory, "window.done"), how);
        }
    }

    // ---- pictures and title bars --------------------------------------------------------------------------------------

    static Image Screen(string file)
    {
        Image image = s_ctx.Tree.Root.GetTexture().GetImage();
        Error saved = image.SavePng(Path.Combine(s_ctx.Directory, file));
        if (saved != Error.Ok) throw new IOException($"Could not save {file} ({saved}).");
        return image;
    }

    // The picture is in the screen's pixels, the windows in the game's own, which the game stretches.
    static void Crop(Image screen, Rect2 rect, string file)
    {
        Rect2 frame = s_ctx.Tree.Root.GetFinalTransform() * rect.Grow(10);
        var whole = new Rect2I(0, 0, screen.GetWidth(), screen.GetHeight());
        Rect2I part = new Rect2I((int)frame.Position.X, (int)frame.Position.Y, (int)frame.Size.X, (int)frame.Size.Y).Intersection(whole);
        if (part.Size.X > 0 && part.Size.Y > 0) screen.GetRegion(part).SavePng(Path.Combine(s_ctx.Directory, file));
    }

    // The parts of a GameWindow's title bar, found by what they show: the paper with its title, the fold button and
    // the X. Each is given from the frame's top left (the X and the fold button from its right edge), in pixels.
    sealed record TitleParts(Control Root, PanelContainer Paper, Label Title, Button? Fold, Button Close)
    {
        /// <summary>The window's size, read off the width its X is made at (GameWindow: 32 at 100%), in the corner's steps of 5%.</summary>
        public float Scale => MathF.Round(Close.CustomMinimumSize.X / 32f * 20f) / 20f;

        /// <summary>The frame's top part, down to the X's lower edge (a crop adds its margin).</summary>
        public Rect2 Bar
        {
            get
            {
                Rect2 root = Root.GetGlobalRect();
                return new Rect2(root.Position, new Vector2(root.Size.X, Close.GetGlobalRect().End.Y - root.Position.Y));
            }
        }

        public Dictionary<string, string> Numbers()
        {
            Rect2 root = Root.GetGlobalRect(), paper = Paper.GetGlobalRect(), close = Close.GetGlobalRect();
            var numbers = new Dictionary<string, string>
            {
                ["frame"] = (Root.GetThemeStylebox("panel") as StyleBoxTexture)?.Texture?.ResourcePath ?? Root.GetThemeStylebox("panel")?.GetType().Name ?? "none",
                ["folded"] = $"{Fold?.TooltipText == OpenTip}",
                ["paper"] = (Paper.GetThemeStylebox("panel") as StyleBoxTexture)?.Texture?.ResourcePath ?? "none",
                ["paper size"] = $"{paper.Size.X:0}x{paper.Size.Y:0}",
                ["paper top"] = $"{paper.Position.Y - root.Position.Y:0}",
                ["paper centre from the frame's centre"] = $"{paper.GetCenter().X - root.GetCenter().X:0}",
                ["title font"] = Path.GetFileName(Title.GetThemeFont("font")?.ResourcePath ?? ""),
                ["title size"] = $"{Title.GetThemeFontSize("font_size")}",
                ["title colour"] = Title.GetThemeColor("font_color").ToHtml(),
                ["X"] = $"{close.Size.X:0}x{close.Size.Y:0} {Close.ThemeTypeVariation} font {Close.GetThemeFontSize("font_size")}",
                ["X top"] = $"{close.Position.Y - root.Position.Y:0}",
                ["X from the right edge"] = $"{root.End.X - close.End.X:0}",
            };
            if (Fold is { } fold)
            {
                Rect2 at = fold.GetGlobalRect();
                numbers["fold"] = $"{at.Size.X:0}x{at.Size.Y:0} {fold.ThemeTypeVariation}";
                numbers["fold top"] = $"{at.Position.Y - root.Position.Y:0}";
                numbers["fold to X"] = $"{close.Position.X - at.End.X:0}";
            }
            else numbers["fold"] = "none";
            return numbers;
        }

        public override string ToString() =>
            $"at {Scale:P0}, '{Title.Text}': " + string.Join(", ", Numbers().Select(n => $"{n.Key} {n.Value}"));
    }

    static TitleParts? TitleBar(Control root, Func<string, bool> isTitle)
    {
        Label? label = root.FindChildren("*", "Label", true, false).OfType<Label>().FirstOrDefault(l => isTitle(l.Text) && l.GetParent() is PanelContainer);
        var buttons = root.FindChildren("*", "Button", true, false).OfType<Button>().ToList();
        Button? close = buttons.FirstOrDefault(b => b.Text == "X");
        if (label == null || close == null) return null;
        return new TitleParts(root, (PanelContainer)label.GetParent(), label, buttons.FirstOrDefault(b => b.TooltipText is FoldTip or OpenTip), close);
    }

    static string Compare(TitleParts ours, TitleParts theirs)
    {
        if (Math.Abs(ours.Scale - theirs.Scale) > 0.001f) return $"NOT at the same size ({ours.Scale:P0} and {theirs.Scale:P0})";
        Dictionary<string, string> a = ours.Numbers(), b = theirs.Numbers();
        var different = a.Keys.Union(b.Keys).Where(k => a.GetValueOrDefault(k) != b.GetValueOrDefault(k))
            .Select(k => $"{k}: {a.GetValueOrDefault(k) ?? "-"} here, {b.GetValueOrDefault(k) ?? "-"} there").ToList();
        return different.Count == 0 ? $"all {a.Count} the same" : $"{different.Count} of {a.Count} differ: {string.Join("; ", different)}";
    }

    // Every canvas layer, and the layer of each kind of the game's tooltips (shown or not): a tooltip on a lower
    // layer than the window's is drawn under it.
    static string Layers(GameWindow window)
    {
        var layers = new List<string>();
        var tips = new List<string>();
        void Walk(Node node)
        {
            if (node is CanvasLayer layer) layers.Add($"{layer.Name}={layer.Layer}{(layer.Visible ? "" : " (hidden)")}");
            if (node is GamesByMalcs.Melvor2.Tooltips.BaseTooltip tip) tips.Add($"{tip.GetType().Name} on {LayerOf(tip)}");
            foreach (Node child in node.GetChildren()) Walk(child);
        }
        Walk(s_ctx.Tree.Root);
        string kinds = string.Join(", ", tips.GroupBy(t => t).Select(g => g.Count() == 1 ? g.Key : $"{g.Key} (x{g.Count()})"));
        return $"the combat window is on layer {window.Layer.Layer}; canvas layers: {string.Join(", ", layers)}; the game's tooltips: {(kinds == "" ? "none found" : kinds)}";
    }

    static string LayerOf(Node node)
    {
        for (Node? at = node.GetParent(); at != null; at = at.GetParent())
        {
            if (at is CanvasLayer layer) return $"layer {layer.Layer} ({layer.Name})";
            if (at is Window own && own != s_ctx.Tree.Root) return $"a window of its own ({own.Name}), drawn over every layer";
        }
        return "the game's own canvas, layer 0";
    }
}
#endif
