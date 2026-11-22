#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using GamesByMalcs.Melvor2.Components.ActionUntilX;
using GamesByMalcs.Melvor2.Components.Trackers;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Core.TickTimer;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Goals;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.Settings;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace CombatGoals;

static partial class Dev
{
    // "stop-shot.request" holds one word (letters, digits and "-"). The request only looks, takes pictures and
    // moves the pointer: it never starts or stops an action, sets a stop or changes a setting. Set the game up by
    // hand first (an action with a stop, the footer open or folded, the interface setting that hides the
    // footer's tracker) and wait for the stop's time ("the tag" below: the clock, the time and the words).
    // - Any name: shot-stop-<name>.png and a log line with what the footer and the game's stop panel show next to
    //   what the tag shows, what the footer's formatter writes at 0 and past its longest time, and where the
    //   footer's parts are. The tag is then hidden, and on the very next frame a line says which of the footer's
    //   parts moved because of that. A second later shot-stop-<name>-without.png and its log line.
    // - "watch": the tag's time once a second for half a minute, with how often it followed the footer's own
    //   layout, then shot-stop-watch.png.
    // - "hover": the pointer onto the tag, then shot-stop-hover.png with its tooltip and what is drawn behind it,
    //   then the pointer back where the real pointer is, so the tooltip is not left up for later pictures.
    // - "combat-min" and "combat-max": the combat window at 70% and at 200% for one picture, then back at the
    //   player's size; they need a fight going with the window open. The size is never saved.
    // Every line says where the tag is, where the footer's content ends and what part ends it, and every change of
    // the tag's place since the line before, with what changed it.
    static string? s_shotName;
    static int s_shotStep;
    static Dictionary<string, (Rect2 At, string Text)>? s_footerBefore;
    static bool s_tagWasShown, s_pointerPushed;

    const int WatchSeconds = 30;

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "FormatDepletionTime")]
    static extern string FormatDepletionTime(GainLossInfoView? type, double hours);

    static void StopShotRequest()
    {
        string path = Path.Combine(s_ctx.Directory, "stop-shot.request");
        if (File.Exists(path))
        {
            string name = File.ReadAllText(path).Trim();
            File.Delete(path);
            EndShot();   // a new request replaces one that is still going
            if (name == "" || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            {
                s_ctx.Log("Dev: stop-shot.request needs one name of letters, digits and \"-\".");
                return;
            }
            s_shotName = name;
            s_shotStep = 0;
        }
        if (s_shotName is not { } shot) return;
        try
        {
            switch (shot)
            {
                case "combat-min" or "combat-max": CombatShot(shot); break;
                case "watch": WatchShot(); break;
                case "hover": HoverShot(); break;
                default: FooterShot(shot); break;
            }
        }
        catch
        {
            EndShot();
            throw;
        }
    }

    // One step a second: picture, hide the tag, (next frame: what moved), picture again, show it.
    static void FooterShot(string shot)
    {
        switch (s_shotStep++)
        {
            case 0:
                Shot($"shot-stop-{shot}.png");
                s_ctx.Log($"Dev: shot-stop-{shot}.png " + DescribeStop());
                s_tagWasShown = SkillingAction.Current?.Tag?.IsVisibleInTree() == true;
                s_footerBefore = FooterParts();
                SkillingAction.HideForPicture = true;
                SkillingAction.Current?.Tag?.Hide();
                break;
            default:
                Shot($"shot-stop-{shot}-without.png");
                s_ctx.Log($"Dev: shot-stop-{shot}-without.png " + DescribeStop());
                EndShot();
                break;
        }
    }

    // Called on every frame, before the once-a-second work: on the frame after a picture hid the tag, the game
    // has laid the footer out again without it. Every part of the footer is compared with the frame before.
    static void FooterMovedCheck()
    {
        if (s_footerBefore is not { } before) return;
        s_footerBefore = null;
        try { s_ctx.Log("Dev: " + FooterMoved(before, FooterParts(), s_tagWasShown)); }
        catch (Exception e) { s_ctx.Log("Dev: " + e); }
    }

    static void WatchShot()
    {
        int second = ++s_shotStep;
        var part = SkillingAction.Current;
        PanelContainer? tag = part?.Tag;
        s_ctx.Log($"Dev: watch {second}/{WatchSeconds} tag=[{part?.TimeLabel?.Text}] [{part?.WordsLabel?.Text}] shown={tag?.IsVisibleInTree() == true} " +
                  $"x={tag?.GetGlobalRect().Position.X:0} width={tag?.Size.X:0}; place: {part?.PlaceNow}; " +
                  $"place changes since the last line: {part?.TakePlaceChanges()}; " +
                  $"layouts followed={part?.Follows} looks={part?.FullLays} place changes={part?.PlaceChanges} {part?.Reading}");
        if (second < WatchSeconds) return;
        Shot("shot-stop-watch.png");
        s_ctx.Log("Dev: shot-stop-watch.png " + PlaceLine());
        EndShot();
    }

    // Where the tag is, where the footer's content ends, and what moved the tag since the line before.
    static string PlaceLine()
    {
        var part = SkillingAction.Current;
        return part == null ? "the skilling part is not running"
            : $"tag place: {part.PlaceNow}; place changes since the last line: {part.TakePlaceChanges()}";
    }

    // The game's tooltips wait a moment under a still pointer: the picture is taken two seconds later.
    static void HoverShot()
    {
        switch (s_shotStep++)
        {
            case 0:
                PanelContainer? tag = SkillingAction.Current?.Tag;
                if (tag == null || !tag.IsVisibleInTree())
                {
                    s_ctx.Log("Dev: hover needs the tag on the screen.");
                    EndShot();
                    return;
                }
                Vector2 spot = s_ctx.Tree.Root.GetFinalTransform() * tag.GetGlobalRect().GetCenter();
                Input.ParseInputEvent(new InputEventMouseMotion { Position = spot, GlobalPosition = spot });
                s_pointerPushed = true;
                break;
            case 1:
                break;
            default:
                Shot("shot-stop-hover.png");
                s_ctx.Log($"Dev: shot-stop-hover.png tag tooltip=[{SkillingAction.Current?.Tag?.TooltipText.Replace("\n", " / ")}]; " +
                          $"on the screen: {GameStyle.DevTooltips(s_ctx.Tree.Root)}; {PlaceLine()}");
                EndShot();
                break;
        }
    }

    // The game follows the pushed pointer until the real one moves. It goes back to where the real pointer is
    // (outside the window, if it is there), so the tag's tooltip is not left up in later pictures. The real pointer
    // is not moved.
    static void PointerBack()
    {
        if (!s_pointerPushed) return;
        s_pointerPushed = false;
        Vector2 real = DisplayServer.MouseGetPosition() - DisplayServer.WindowGetPosition();
        Input.ParseInputEvent(new InputEventMouseMotion { Position = real, GlobalPosition = real });
        Rect2? tag = SkillingAction.Current?.Tag is { } shown ? s_ctx.Tree.Root.GetFinalTransform() * shown.GetGlobalRect() : null;
        s_ctx.Log($"Dev: hover: the pointer was put back at {real} in the window, where the real pointer is" +
                  (tag is { } over && over.HasPoint(real) ? " (that is on the tag: its tooltip stays while the real pointer is there)." : "."));
    }

    static void CombatShot(string shot)
    {
        switch (s_shotStep++)
        {
            case 0:
                if (!s_mod.WindowUp)
                {
                    s_ctx.Log($"Dev: {shot} needs a fight going and the combat window open (F10).");
                    EndShot();
                    return;
                }
                s_mod.SetPictureScale(shot == "combat-max" ? 1 : -1);
                break;
            default:
                Shot($"shot-{shot}.png");
                s_ctx.Log($"Dev: shot-{shot}.png {s_mod.WindowReading}; {PlaceLine()}");
                EndShot();
                break;
        }
    }

    static void EndShot()
    {
        s_mod.SetPictureScale(null);   // nothing happens when the window is at the player's size
        s_shotName = null;
        s_shotStep = 0;
        SkillingAction.HideForPicture = false;
        PointerBack();
    }

    // Once per character: which skills the game offers a stop in, for the guides.
    static void LogSkillsWithStops()
    {
        if (GameAccess.CurrentGame is not { IsSaveLoaded: true } game) return;
        var names = game.Entities.GetAllObjects()
            .Where(e => e.Components.Values.OfType<ActionUntilXComponent>().Any())
            .Select(e => (e as INamedObject)?.DisplayName.Text ?? e.Id);
        s_ctx.Log("Dev: skills with a stop: " + string.Join(", ", names));
    }

    static string DescribeStop()
    {
        var part = SkillingAction.Current;
        var sb = new StringBuilder(part?.Reading ?? "the skilling part is not running");
        var footer = part?.Footer;
        Label? name = footer == null ? null : SkillingAction.ActionNameOf(footer);

        if (footer != null)
        {
            var row = Member<Control>(footer, "CurrentActionContainer");
            var toggle = Member<BaseButton>(footer, "ToggleVisibilityButton");
            var settings = Member<InterfaceSettings>(footer, "Settings");
            sb.Append($"; footer: name=[{name?.Text}] name visible={name?.IsVisibleInTree()} action row visible={row?.IsVisibleInTree()} " +
                      $"fold button pressed={toggle?.ButtonPressed} HideFooterTracker={settings?.HideFooterTracker}");

            var options = Member<OptionButton>(footer, "TimePeriodOptions");
            sb.Append($"; footer period: shown=[{(options is { Selected: >= 0 } o ? o.GetItemText(o.Selected) : "")}]");
            if (SkillingAction.TrackerOf(footer) is { } tracker && tracker.SelectedPeriodIndex >= 0 && tracker.SelectedPeriodIndex < tracker.Periods.Count)
            {
                var period = tracker.Periods[tracker.SelectedPeriodIndex];
                sb.Append($" index={tracker.SelectedPeriodIndex}/{tracker.Periods.Count} CycleTicks={period.CycleTicks} CycleDuration={period.CycleDuration:R} " +
                          $"SamplePeriod={period.SamplePeriod:R} SampleCount={period.SampleCount} TicksToMS(1)={TickTimer.TicksToMS(1):R} " +
                          $"seconds used={SkillingAction.FooterPeriodSeconds(tracker):R}");
            }

            var xp = Member<Node>(footer, "ExperienceView");
            var bank = Member<Node>(footer, "BankView");
            sb.Append($"; footer xp: level=[{TextOf(xp, "CurrentLevel")}] rate=[{TextOf(xp, "ExperienceRate")}] " +
                      $"goal={Member<SpinBox>(xp, "LevelSpinBox")?.Value} time=[{TextOf(xp, "TimeToLevel")}] time raw=[{Member<RichTextLabel>(xp, "TimeToLevel")?.Text}]; " +
                      $"footer bank: depletion=[{TextOf(bank, "TimeUntilDepletionLabel")}] item={Member<INamespacedObject>(bank, "ItemBeingDepleted")?.Id} " +
                      $"currency={Member<INamespacedObject>(bank, "CurrencyBeingDepleted")?.Id}");
        }

        PanelContainer? tag = part?.Tag;
        string tooltip = tag?.TooltipText ?? "";
        sb.Append($"; tag: shown={tag?.IsVisibleInTree() == true} text=[{part?.TimeLabel?.Text}] words=[{part?.WordsLabel?.Text}] " +
                  $"tooltip=[{tooltip.Replace("\n", " / ")}] rect={tag?.GetGlobalRect()} parent={tag?.GetParent()?.Name}; " +
                  $"tag time: the footer's formatter gave [{part?.Formatted}] for the time left in milliseconds, the tag shows [{part?.TimeLabel?.Text}]; " +
                  $"the formatter at the edges (what the tag would show): {Edges()}; " +
                  $"tag place: {part?.PlaceReading}; place changes since the last line: {part?.TakePlaceChanges() ?? "none"}; tag look: {part?.LookReading}");

        ActionUntilXStopView? panel = part?.Panel;
        RichTextLabel? stopText = panel == null ? null : SkillingAction.StopTextOf(panel);
        string parsed = stopText?.GetParsedText() ?? "";
        string plain = SkillingAction.Plain(parsed);
        var bar = panel == null ? null : Member<ProgressBar>(panel, "StopProgress");
        var pictures = panel?.FindChildren("*", "TextureRect", true, false).OfType<TextureRect>()
            .Select(t => t.Texture?.ResourcePath).Where(p => !string.IsNullOrEmpty(p));
        sb.Append($"; stop panel: found={panel != null} visible={panel?.IsVisibleInTree()} raw=[{stopText?.Text}] " +
                  $"parsed=[{parsed}] (characters {string.Join(" ", parsed.Where(c => c is not (>= ' ' and <= '~')).Select(c => $"U+{(int)c:X4}").Distinct())}) " +
                  $"plain=[{plain}] same as the tooltip's first line={plain != "" && tooltip.Split('\n')[0] == plain} bar={bar?.Value}/{bar?.MaxValue} " +
                  $"pictures=[{string.Join(", ", pictures ?? [])}]; game progress: {ProgressOf(part?.Stop)}");

        sb.Append($"; one hour through FormatTimeToLevel: as hours=[{Format(1)}] as seconds=[{Format(3600)}] as ms=[{Format(3600000)}] " +
                  $"(the tag gives milliseconds: {SkillingAction.FormatterUnitsPerSecond} per second), " +
                  $"FormatDepletionTime(1 hour)=[{Depletion(1)}], footer time strings: {TimeStrings()}");
        sb.Append("; layout: " + Layout(name, part?.Bar));
        return sb.ToString();
    }

    static string Format(double value)
    {
        try { return SkillingAction.FormatTimeToLevel(null, value); }
        catch (Exception e) { return e.GetType().Name; }
    }

    static string Depletion(double hours)
    {
        try { return FormatDepletionTime(null, hours); }
        catch (Exception e) { return e.GetType().Name; }
    }

    // The formatter's output where the countdown ends (0, and half a second) and an hour past the longest time the
    // footer writes (MaxTimeToLevelHours, a value of the game's read at run time), each with what TimeOnly keeps
    // of it. The part itself never formats 0: it hides when its estimate has run out.
    static string Edges()
    {
        object? hours = typeof(TrackerExperienceView).GetField("MaxTimeToLevelHours", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        string longest = hours is double h
            ? $"MaxTimeToLevelHours={h:R}, one hour past it {Edge((h + 1) * 3600 * SkillingAction.FormatterUnitsPerSecond)}"
            : "MaxTimeToLevelHours not found";
        return $"0 ms {Edge(0)}, 500 ms {Edge(500)}, {longest}";

        static string Edge(double ms)
        {
            string formatted = Format(ms);
            return $"[{formatted}] -> {(SkillingAction.TimeOnly(formatted) is { } time ? $"[{time}]" : "nothing, not understood")}";
        }
    }

    static string ProgressOf(object? stop) => stop switch
    {
        IActiveGoal g => $"goal {g.CurrentProgress:R}/{g.MaxProgress:R} ({g.ProgressPercent:R})",
        Condition c => $"condition {c.ProgressPercent:R} met={c.IsMet}",
        _ => "none",
    };

    // The localized time strings of the footer's XP part, as the game holds them now.
    static string TimeStrings()
    {
        var parts = new List<string>();
        foreach (string field in new[] { "s_emptyTime", "s_emptyTimeFormat", "s_daysTimeFormat", "s_hoursTimeFormat", "s_minutesTimeFormat", "s_secondsTimeFormat", "s_maxTimeFormat" })
        {
            object? value = typeof(TrackerExperienceView).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            parts.Add($"{field}=[{(value as ILocalizedString)?.Message}]");
        }
        return string.Join(" ", parts);
    }

    // Every box from the name's row up to the whole footer, with each visible part's left edge and width ("*" when
    // it takes the room that is left, "clip" when it cuts off what it holds).
    static string Layout(Label? name, Control? bar)
    {
        var parts = new List<string>();
        Node? at = name?.GetParent();
        for (int level = 0; at is Control box && level < 16; level++, at = box.GetParent())
        {
            var children = box.GetChildren().OfType<Control>().Where(c => c.Visible)
                .Select(c => $"{c.Name}@{c.GlobalPosition.X:0}+{c.Size.X:0}{(c.SizeFlagsHorizontal.HasFlag(Control.SizeFlags.Expand) ? "*" : "")}{(c.ClipContents ? " clip" : "")}");
            parts.Add($"{box.Name} ({box.GetType().Name}): {string.Join(" ", children)}");
            if (box == bar) break;
        }
        return string.Join(" | ", parts);
    }

    // Every part of the footer that is shown, but the tag: where it is and the words it holds.
    static Dictionary<string, (Rect2 At, string Text)> FooterParts()
    {
        var parts = new Dictionary<string, (Rect2, string)>();
        if (SkillingAction.Current?.Bar is { } footer) Walk(footer, footer);
        return parts;

        void Walk(Control bar, Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is not Control { Visible: true } control || control.Name == SkillingAction.TagName) continue;
                string words = control switch { Label l => l.Text, RichTextLabel r => r.Text, _ => "" };
                parts[bar.GetPathTo(control).ToString()] = (control.GetGlobalRect(), words);
                Walk(bar, control);
            }
        }
    }

    static string FooterMoved(Dictionary<string, (Rect2 At, string Text)> before, Dictionary<string, (Rect2 At, string Text)> after, bool tagWasShown)
    {
        var changes = new List<string>();
        int compared = 0;
        foreach (var (path, then) in before)
        {
            if (!after.TryGetValue(path, out var now))
            {
                changes.Add($"{Short(path)} gone");
                continue;
            }
            compared++;
            if (Same(then.At, now.At)) continue;
            changes.Add($"{Short(path)} {then.At.Position.X:0},{then.At.Position.Y:0}+{then.At.Size.X:0}x{then.At.Size.Y:0} -> " +
                        $"{now.At.Position.X:0},{now.At.Position.Y:0}+{now.At.Size.X:0}x{now.At.Size.Y:0}{(then.Text != now.Text ? " (its words changed)" : "")}");
        }
        changes.AddRange(after.Keys.Except(before.Keys).Select(path => $"{Short(path)} appeared"));
        string what = tagWasShown ? "when the tag was hidden" : "in one frame (the tag was not shown, so this only shows the footer's own changes)";
        return changes.Count == 0
            ? $"footer moved {what}: nothing ({compared} parts compared, one frame apart)"
            : $"footer moved {what}: {changes.Count} of {compared} parts: {string.Join("; ", changes.Take(16))}";

        static bool Same(Rect2 a, Rect2 b) =>
            Math.Abs(a.Position.X - b.Position.X) < 0.5f && Math.Abs(a.Position.Y - b.Position.Y) < 0.5f &&
            Math.Abs(a.Size.X - b.Size.X) < 0.5f && Math.Abs(a.Size.Y - b.Size.Y) < 0.5f;

        static string Short(string path)
        {
            string[] names = path.Split('/');
            return string.Join("/", names.Skip(Math.Max(0, names.Length - 2)));
        }
    }

    // A private property of one of the game's views, by its name, for this log only.
    static T? Member<T>(object? view, string name) where T : class =>
        view?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(view) as T;

    static string TextOf(object? view, string name) => Member<Control>(view, name) switch
    {
        RichTextLabel r => r.GetParsedText().Trim(),
        Label l => l.Text,
        null => "(not found)",
        var other => other.GetType().Name,
    };
}
#endif
