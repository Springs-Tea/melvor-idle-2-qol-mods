#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using GamesByMalcs.Melvor2.Entities.Hunting;
using GamesByMalcs.Melvor2.Tooltips;
using Godot;
using MelvorMods.Shared;

namespace HuntingRates;

// Development: the game's own words on the parts of an animal's card and of the page's panel of the animal selected,
// as a player reads them by resting the pointer on them (Woodcutting Rates' step "the card's stats", mods/WoodcuttingRates/
// Dev.cs DevAims and DevAimStep, from which this is ported; Mining Rates' break rule was settled this way). The pointer
// only rests: nothing is pressed, and no button is aimed at (the panel's button that starts a hunt is never under it).
// Every tooltip of the game under the card and under the panel is found with the controls it shows for
// (Tooltip.Targets, read by reflection as Woodcutting Rates reads it), and every control there with Godot's own tooltip
// text; the pointer rests on each, in sight, until the game shows it filled, and its words are logged word for word, with
// every number in them named against the animal's numbers ("Dev: card stats"). A tooltip not shown in 20 s is one line,
// and the request goes on. These are the words the rules of the hour (AnimalRates.HuntRule, XpRule, OutputRule, UseRule)
// may be settled by.
public partial class HuntingRatesMod
{
    // The most parts the pointer rests on in one request, how long it rests on one for the game's tooltip, and how long the
    // whole step may take before it goes on with the parts left unread said in one line: about a minute in all, since he
    // plays meanwhile (review 2).
    const int MostAims = 10;
    const ulong AimWait = 6000, AimsMost = 60000;

    // One part the pointer rests on for the game's tooltip, and what is kept from tick to tick.
    sealed class DevAim(string what, ActiveHuntingAnimal animal, Control aim, string aimed, Tooltip? tooltip, Func<string> words, string? shot)
    {
        public readonly string What = what, Aimed = aimed;
        public readonly ActiveHuntingAnimal Animal = animal;
        public readonly Control Aim = aim;
        public readonly Tooltip? Tooltip = tooltip;
        public readonly Func<string> Words = words;
        public readonly string? Shot = shot;
        public Image? Before;
        public Vector2 At;
        public string? First;
    }

    // The parts to rest the pointer on: those of the card of the animal the panel shows when its card is wholly in sight,
    // else of the first card of an animal discovered and unlocked wholly in sight, and those of the panel when it shows an
    // animal. Every word each shows is logged once, with each tooltip and its targets.
    List<DevAim> DevAims(out string why)
    {
        why = "";
        var aims = new List<DevAim>();
        var action = _hunting!.Action;
        var cards = Native.Cards(_page!).Where(c => c.IsVisibleInTree() && Native.AnimalOf(c) is { } a && a.JournalEntry?.IsDiscovered != false &&
            !DevNative.CardLocked(c).IsVisibleInTree() && Native.Place(c, Cover(), out _) == CardSight.Clear).ToList();
        HuntingAnimalTrackingView? card = cards.FirstOrDefault(c => ReferenceEquals(Native.AnimalOf(c), action.SelectedAnimal)) ?? cards.FirstOrDefault();
        if (card != null) aims.AddRange(DevAimsUnder(card, $"the card of {Native.AnimalOf(card)!.Animal.Id}", Native.AnimalOf(card)!, null));
        else why = "no card of an animal discovered and unlocked is wholly in sight";
        if (Native.TrackingView(_page!) is { } view && Native.Panel(view) is { } panel && panel.IsVisibleInTree() &&
            DevNative.PanelShown(panel).IsVisibleInTree() && action.SelectedAnimal is { } selected)
            aims.AddRange(DevAimsUnder(panel, $"the panel of {selected.Animal.Id}", selected, "panel-tooltip"));
        else why += (why.Length > 0 ? "; " : "") + "the page's panel shows no animal selected";
        if (aims.Count > MostAims)
        {
            DevLog($"card stats | {aims.Count} parts found; the first {MostAims} are pointed at: [{string.Join(", ", aims.Skip(MostAims).Select(a => a.What))}] are not");
            aims = aims.Take(MostAims).ToList();
        }
        return aims;
    }

    List<DevAim> DevAimsUnder(Control part, string where, ActiveHuntingAnimal animal, string? shot)
    {
        var aims = new List<DevAim>();
        var tips = part.FindChildren("*", "", true, false).OfType<Tooltip>().ToList();
        DevLog($"card stats | {where}: every word it shows [{string.Join(" ", part.FindChildren("*", "", true, false).OfType<Control>()
            .Where(c => c.IsVisibleInTree() && (c is Label { Text.Length: > 0 } || c is RichTextLabel { Text.Length: > 0 }))
            .Select(c => $"'{One(c is Label l ? l.Text : DevWordsOf(((RichTextLabel)c).Text))}'"))}] | its tooltips: " +
            string.Join("; ", tips.Select(t => $"{DevName(t)} for [{string.Join(", ", DevNative.Targets(t).Select(DevName))}]")));
        var aimed = new HashSet<Control>();
        foreach (Tooltip t in tips)
        {
            // A target of the tooltip that is a part of this one in sight and no button: the pointer only rests.
            Control? target = DevNative.Targets(t).FirstOrDefault(c => (c == part || part.IsAncestorOf(c)) && c is not BaseButton && c.IsVisibleInTree() && DevSight(c).HasArea());
            if (target == null || !aimed.Add(target)) continue;
            Tooltip tooltip = t;
            aims.Add(new DevAim($"{where}: {DevName(target)}", animal, target, $"the tooltip's target {DevName(target)}", tooltip, () => DevTipWords(tooltip),
                aims.Count == 0 ? shot : null));
        }
        foreach (Control c in part.FindChildren("*", "", true, false).OfType<Control>())
        {
            if (c is BaseButton || c.TooltipText.Length == 0 || !c.IsVisibleInTree() || !DevSight(c).HasArea() || !aimed.Add(c)) continue;
            Control target = c;
            aims.Add(new DevAim($"{where}: {DevName(c)}", animal, c, $"{DevName(c)} with Godot's own tooltip text", null, () => target.TooltipText, null));
        }
        return aims;
    }

    // The words of a game's tooltip: a text tooltip's title and content, or every word it draws.
    static string DevTipWords(Tooltip? tooltip)
    {
        if (tooltip == null) return "";
        if (tooltip is TextTooltip text)
        {
            string title = "";
            try { title = DevWordsOf(DevNative.TooltipTitle(text).Text ?? ""); } catch (Exception) { }
            string content = DevWordsOf(string.Join("\n", DevNative.Content(text) ?? []));
            return title.Length > 0 && !content.StartsWith(title, StringComparison.Ordinal) ? title + "\n" + content : content;
        }
        return string.Join("\n", tooltip.FindChildren("*", "", true, false).OfType<Control>().Where(c => c.IsVisibleInTree())
            .Select(c => c is Label l ? l.Text : c is RichTextLabel r ? DevWordsOf(r.Text) : "").Where(w => w.Length > 0));
    }

    // The game says the pointer is on the control, or on a control around it at the spot it was put.
    bool DevPointerOn(Control aim, Vector2 at)
    {
        Control? over = _ctx.Tree.Root.GuiGetHoveredControl();
        if (over == null || !GodotObject.IsInstanceValid(aim)) return false;
        return over == aim || aim.IsAncestorOf(over) || (over.IsAncestorOf(aim) && aim.GetGlobalRect().HasPoint(at));
    }

    // The game's tooltip says it is shown, and not for another control (Tooltip.IsShown and CurrentTarget).
    static bool DevShownFor(Tooltip tooltip, Control aim, Vector2 at, out string said)
    {
        // The game may free a tooltip and build a new one while the step waits for the player's hand (check-14).
        if (!GodotObject.IsInstanceValid(tooltip) || !GodotObject.IsInstanceValid(aim))
        {
            said = "the tooltip or its part was freed by the game";
            return false;
        }
        try
        {
            bool shown = tooltip.IsShown || tooltip.IsVisibleInTree();
            Control? target = tooltip.CurrentTarget;
            bool other = target != null && GodotObject.IsInstanceValid(target) && target != aim && !target.IsAncestorOf(aim) && !aim.IsAncestorOf(target) &&
                !target.GetGlobalRect().HasPoint(at);
            said = $"shown {tooltip.IsShown}, on the screen {tooltip.IsVisibleInTree()}, for {(target == null ? "no target" : DevName(target))}{(other ? "  ANOTHER CONTROL" : "")}";
            return shown && !other;
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            said = $"Tooltip.IsShown or CurrentTarget not read ({e.GetType().Name}), on the screen {tooltip.IsVisibleInTree()}";
            return tooltip.IsVisibleInTree();
        }
    }

    // Rests the pointer on one part until the game shows its tooltip for it, filled, then reads it two drawn frames later,
    // only while the pointer is on it, and pictures the first of the panel's. True when this part is done.
    bool DevAimStep(DevAim a)
    {
        if (DevHandAtMouse(a.What)) { _devTipSince = 0; a.First = null; return false; }
        if (_devTipSince == 0)
        {
            _devTipSince = Time.GetTicksMsec();
            a.Before ??= _ctx.Tree.Root.GetTexture().GetImage();
        }
        string content;
        try { content = DevWordsOf(a.Words()); }
        catch (Exception e) { content = ""; DevLog($"card stats | {a.What}: its words could not be read ({e.GetType().Name}: {e.Message})"); }
        bool on = DevPointerOn(a.Aim, a.At);
        string state;
        bool shown;
        if (a.Tooltip != null) shown = DevShownFor(a.Tooltip, a.Aim, a.At, out state);
        else
        {
            string up = GameStyle.DevTooltips(_ctx.Tree.Root);
            string first = a.Aim.TooltipText.Split('\n')[0];
            shown = first.Length > 0 && up.Contains(first);
            state = shown ? "Godot's tooltip shown" : "Godot's tooltip not shown";
        }
        if (!(on && shown && content.Length > 0))
        {
            a.First = null;
            if (Time.GetTicksMsec() - _devTipSince >= AimWait)
            {
                DevLog($"card stats | {a.What}: its tooltip was not shown filled in {AimWait / 1000} s with the pointer on {DevName(_ctx.Tree.Root.GuiGetHoveredControl())}, " +
                    $"aimed at {a.Aimed} (the tooltip: {state}; it holds '{content}'): not read this time");
                DevPointerBack();
                return true;
            }
            if (!on)
            {
                Rect2 rect = DevSight(a.Aim);
                if (!rect.HasArea())
                {
                    DevLog($"card stats | {a.What} went out of sight: not read this time");
                    return true;
                }
                a.At = rect.GetCenter();
                DevPointer(a.At);
            }
            return false;
        }
        if (a.First == null)
        {
            a.First = content;
            _devFrameMark = 0;
        }
        if (!DevFramesDrawn()) return false;
        DevLog($"card stats | {a.What}, the pointer on {a.Aimed}, the tooltip {state}: '{content}'" +
            (content == a.First ? "" : $"  ITS WORDS CHANGED while read (first seen: '{a.First}')") + $" | {DevNumbersIn(content, a.Animal)}");
        if (a.Shot != null)
        {
            Shot(a.Shot);
            if (a.Before != null && a.Tooltip != null)
            {
                using var shot = _ctx.Tree.Root.GetTexture().GetImage();
                // The rectangle read is its labels': a dark tooltip over the dark page changes only its letters' pixels
                // there (check-3: "Skill XP" drawn, 13% of the label's pixels changed, NOT VISIBLE by the window's
                // quarter). Words are proved drawn from DevWordsChanged's share up.
                DevLog($"shot-hunting-{a.Shot}.png | " + (DevDrawn(a.Tooltip, out string where) is { } drawn
                    ? DevChangedPixels(a.Before, shot, drawn, $"the game's tooltip: {where}", "VISIBLE", "NOT VISIBLE", DevWordsChanged)
                    : $"NOT PROVED: where the game's tooltip is drawn is not known ({where})"));
            }
        }
        DevPointerBack();
        return true;
    }

    // Every number in a tooltip's words, named against the animal's numbers, so that words stating how a hunt's steps join
    // can be read for what their numbers are (Woodcutting Rates' DevNumbersIn).
    string DevNumbersIn(string words, ActiveHuntingAnimal animal)
    {
        AnimalRates t = ReadAnimal(animal);
        var known = new List<(string Name, double Value)>
        {
            ("the time of a survey in seconds", t.SurveySeconds), ("the most surveys the page writes", t.SurveysMost ?? double.NaN), ("the location's insights chance", t.Insights), ("the location's base XP of a survey", t.SurveyXpBase),
            ("the fewest tracking steps", t.StepsMin), ("the most tracking steps", t.StepsMax), ("the shortest step in seconds", t.StepMinSeconds),
            ("the longest step in seconds", t.StepMaxSeconds), ("the tracking success chance", t.TrackChance), ("the skip chance", t.SkipChance),
            ("the shortest trap try in seconds", t.TrapMinSeconds), ("the longest trap try in seconds", t.TrapMaxSeconds), ("a try's success chance", t.TrapChance),
            ("the reattempt chance", t.ReattemptChance), ("the rare species chance", t.RareChance), ("the doubling chance", t.Doubling),
            ("the preservation chance", t.Preservation), ("the XP before its range", t.XpEach), ("the XP range's low end", t.XpRange is { } r1 ? t.XpEach * r1.Low : double.NaN),
            ("the XP range's high end", t.XpRange is { } r2 ? t.XpEach * r2.High : double.NaN), ("the journal XP of tracking", t.JournalTrackingXp),
            ("the journal XP of trapping", t.JournalTrappingXp),
        };
        var said = new List<string>();
        foreach (Match m in Regex.Matches(words, @"-?\d[\d,]*(?:\.\d+)?\s*%?"))
        {
            string raw = m.Value.Trim();
            bool percent = raw.EndsWith('%');
            if (!double.TryParse(raw.TrimEnd('%').Trim().Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) continue;
            var names = known.Where(k => double.IsFinite(k.Value) && (Math.Abs(k.Value - n) <= 1e-6 + 0.0005 * Math.Abs(n) || (percent && Math.Abs(k.Value * 100 - n) <= 0.006)))
                .Select(k => k.Name).Distinct().ToList();
            said.Add($"{raw}: {(names.Count == 0 ? "none of the animal's numbers" : string.Join(" = ", names))}");
        }
        return said.Count == 0 ? "no number in it" : "its numbers [" + string.Join("; ", said) + "]";
    }

    // The share of a label's pixels that its letters change: a word's strokes cover about a tenth to a quarter of its line.
    const double DevWordsChanged = 0.05;

    // Where the words of a game's tooltip are drawn, in the picture's own pixels (Woodcutting Rates' DevDrawn).
    static Rect2? DevDrawn(BaseTooltip tooltip, out string where)
    {
        Rect2? drawn = null;
        int parts = 0;
        foreach (Control c in tooltip.FindChildren("*", "", true, false).OfType<Control>())
        {
            if (c is not (RichTextLabel or Label) || !c.IsVisibleInTree() || c.Size.X < 1 || c.Size.Y < 1) continue;
            Rect2 at = c.GetViewport().GetScreenTransform() * c.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, c.Size);
            drawn = drawn is { } so ? so.Merge(at) : at;
            parts++;
        }
        string content;
        try { content = $"its content at {tooltip.LastContentPosition}"; }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException) { content = "its content's place not read"; }
        where = $"{parts} of its labels drawn at {drawn?.ToString() ?? "-"} in the picture's pixels; its own place {tooltip.GetGlobalRect()}, {content}";
        return drawn;
    }
}
#endif
