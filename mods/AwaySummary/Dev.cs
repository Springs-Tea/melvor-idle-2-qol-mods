#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GamesByMalcs.Melvor2.Entities.OfflineProgress;
using Godot;
using MelvorMods.Shared;

namespace AwaySummary;

public sealed partial class AwaySummaryMod
{
    // From the game folder with a character training in the background:
    //   Set-Content .\mods\AwaySummary\away.request 60
    // A positive number is the capture time in seconds (an empty file means one minute). Sample the
    // same balances and events as a focus loss/return without touching the player's last summary.
    // Save the real, first-open, unchanged and many-item views at both scales, then write away.done.
    // Each picture logs the window's size against the size asked for, the order of the headings with the
    // game's priority of each, and how the game's heading scene was sized at that scale.
    sealed record Picture(string File, Summary? Summary, float Scale, bool CheckScrolling);

    readonly Queue<Picture> _pictures = new();
    readonly List<string> _layoutIssues = new();
    AwayPeriod? _sample;
    TimeSpan _sampleTime;
    bool _requestActive, _restoreOpen;
    (Rect2 Frame, Vector2 Rows)? _lastLayout;

    void DevRequest()
    {
        try
        {
            if (!_requestActive)
            {
                string request = Path.Combine(_ctx.Directory, "away.request");
                if (!File.Exists(request)) return;
                string seconds = File.ReadAllText(request).Trim();
                File.Delete(request);
                _requestActive = true;
                _restoreOpen = _window?.Visible == true;
                File.Delete(Path.Combine(_ctx.Directory, "away.done"));
                _sampleTime = seconds.Length == 0 ? TimeSpan.FromMinutes(1) :
                    TimeSpan.FromSeconds(int.Parse(seconds, CultureInfo.InvariantCulture));
                if (_sampleTime <= TimeSpan.Zero || _sampleTime > TimeSpan.FromDays(1))
                    throw new InvalidDataException("Use a positive capture time of at most one day, in seconds.");
                if (OfflineSummaryUp()) throw new InvalidOperationException("The game's offline summary is open.");
                _sample = new AwayPeriod(_game!, _bank!, _combat!, _gold!)
                {
                    Readings = line => _ctx.Log("Dev: " + line),
                };
                _ctx.Log($"Dev: started an away sample of {_sampleTime.TotalSeconds} seconds.");
                return;
            }

            if (_sample != null)
            {
                if (_sample.Elapsed < _sampleTime) return;
                var summary = _sample.Finish();
                // Enough actual item names and icons to fill even the smallest font on this viewport.
                int lines = (int)Math.Ceiling(_ctx.Tree.Root.GetVisibleRect().Size.Y /
                    new GameStyle(GameWindow.MinScale).Px(14)) + 1;
                var longList = _sample.LayoutSample(summary.Duration, lines);
                var unchanged = new Summary(summary.Duration, [], []);
                _sample = null;
                foreach (var (name, view) in new (string, Summary?)[]
                    { ("real", summary), ("empty", null), ("unchanged", unchanged), ("long", longList) })
                    foreach (var (size, scale) in new[] { ("min", GameWindow.MinScale), ("max", GameWindow.MaxScale) })
                        _pictures.Enqueue(new Picture($"away-{name}-{size}.png", view, scale, name == "long"));
                _ctx.Log($"Dev: real recap: {summary.Changes.Count} changes, {summary.Events.Count} events. " +
                    $"Long layout sample only: {longList.Changes.Count} item rows.");
                foreach (var change in summary.Changes)
                    _ctx.Log($"Dev: {change.Category.DisplayName.Text}, {change.Name}, {change.Amount:R}, " +
                        $"shown as '{(change.Message is { } m ? m.GetGainText() : "(plain row)")}'");
                foreach (var note in summary.Events) _ctx.Log($"Dev: {note.At:c} {note.Title} / {note.Detail}");
                LogHeadingOrder("real", summary);
                LogHeadingOrder("long", longList);
                ShowPicture();
                return;
            }

            if (!_shownPreview || _window?.Visible != true)
                throw new InvalidOperationException("The preview window was replaced or closed.");
            // Wait until the window has been measured after its layout, and has stayed the same for a tick.
            if (_settleSince != 0) return;
            var rect = _window.Root.GetGlobalRect();
            var layout = (rect, _rows?.GetCombinedMinimumSize() ?? Vector2.Zero);
            if (_lastLayout != layout)
            {
                _lastLayout = layout;
                return;
            }
            Picture picture = _pictures.Dequeue();
            RenderingServer.ForceDraw(false);
            using Image image = _ctx.Tree.Root.GetTexture().GetImage();
            if (image.IsEmpty()) throw new IOException("The viewport returned an empty picture.");
            Error result = image.SavePng(Path.Combine(_ctx.Directory, picture.File));
            if (result != Error.Ok) throw new IOException($"{picture.File}: {result}");
            float scaleNow = _window.Style.Scale, largest = LargestScale();
            _ctx.Log($"Dev: {picture.File}: window {rect.Size} at {rect.Position}, scale {scaleNow:0.##} (asked {picture.Scale:0.##}), " +
                $"full size {rect.Size / scaleNow}, fixed minimum at full size {_fullMinimum}, largest {largest:0.##}, " +
                $"screen {_ctx.Tree.Root.GetVisibleRect().Size}, footer {FooterHeight()}, bottom {rect.End.Y}, " +
                $"rows {layout.Item2.Y}, scroll height {_scroll?.Size.Y ?? 0}.");
            LogHeading(picture.File);
            if (rect.Position.X < 0 || rect.Position.Y < 0 || rect.End.X > _available.X || rect.End.Y > _available.Y)
                _layoutIssues.Add($"{picture.File} extends outside the space above the footer.");
            // A window left at the height of its first layout instead of what it shows (SizeScroll).
            float content = _window.Root.GetCombinedMinimumSize().Y;
            if (rect.Size.Y > content + 0.5f)
                _layoutIssues.Add($"{picture.File} is {rect.Size.Y:0.#} high, taller than the {content:0.#} it shows.");
            if (scaleNow < Math.Min(picture.Scale, largest) - 0.001f)
                _layoutIssues.Add($"{picture.File} opened at {scaleNow:0.##}, smaller than the {Math.Min(picture.Scale, largest):0.##} that fits.");
            if (picture.CheckScrolling && (_scroll == null || !_scroll.GetVScrollBar().IsVisibleInTree()))
                _layoutIssues.Add($"{picture.File} has no scrollbar for the long list.");
            if (_pictures.Count != 0) ShowPicture();
            else EndRequest(_layoutIssues.Count == 0 ? "OK" : string.Join(" ", _layoutIssues));
        }
        catch (Exception e)
        {
            if (_requestActive) EndRequest($"Failed: {e.Message}");
            else ReportOnce("request", $"Could not read or finish the development request: {e.Message}");
        }
    }

    // The headings in the order the summary shows them, each with the game's OfflineSnapshotCategory.Priority:
    // to compare with the order of the game's own summary after a restart.
    void LogHeadingOrder(string name, Summary summary)
    {
        var order = new List<string>();
        foreach (var change in summary.Changes)
        {
            string entry = $"{change.Category.DisplayName.Text} ({change.Category.Priority})";
            if (order.Count == 0 || order[^1] != entry) order.Add(entry);
        }
        _ctx.Log($"Dev: {name} headings in order: {(order.Count == 0 ? "none" : string.Join(", ", order))}" +
            (summary.Events.Count != 0 ? $", then {EventsHeading}" : ""));
    }

    // How the game's heading scene came out at this scale: each control's type, name, minimum and real size, and
    // where a label's font size comes from.
    void LogHeading(string file)
    {
        if (_rows?.GetChildren().OfType<OfflineSnapshotCategoryLabel>().FirstOrDefault() is not { } heading)
        {
            _ctx.Log($"Dev: {file}: no game heading in this picture.");
            return;
        }
        var parts = new List<string>();
        foreach (Control control in new List<Control> { heading }.Concat(heading.FindChildren("*", "Control", true, false).OfType<Control>()))
        {
            string font = control is Label label ? label.LabelSettings is { } settings
                ? $", font {settings.FontSize} from LabelSettings" : $", font {label.GetThemeFontSize("font_size")} from the theme" : "";
            parts.Add($"{control.GetType().Name} {control.Name} min {control.CustomMinimumSize} size {control.Size}{font}");
        }
        _ctx.Log($"Dev: {file}: heading at scale {_window!.Style.Scale:0.##}: {string.Join("; ", parts)}.");
    }

    void ShowPicture()
    {
        var next = _pictures.Peek();
        Show(next.Summary, next.Scale, folded: false, preview: true);
        _lastLayout = null;
    }

    void EndRequest(string result)
    {
        if (!_requestActive) return;
        _requestActive = false;
        _sample = null;
        _pictures.Clear();
        _layoutIssues.Clear();
        if (_shownPreview)
        {
            ReleaseWindow();
            _shownPreview = false;
            if (_restoreOpen && CanShow && result == "OK") Open();
            else Close();
        }
        _ctx.Log("Dev: away.request: " + result);
        ModFiles.WriteWhole(Path.Combine(_ctx.Directory, "away.done"), result);
    }
}
#endif
