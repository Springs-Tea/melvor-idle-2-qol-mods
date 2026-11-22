#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Farming;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace ItemSources;

public partial class ItemSourcesMod
{
    IEnumerator<KeyValuePair<Item, ItemEntry>>? _caseItems;
    string _caseName = "";
    bool _checkLong;
    readonly Dictionary<string, Control> _stepSpots = new();

    void LogPlan(Item item, MakePlan plan)
    {
        _ctx.Log($"Dev: Make target {item.Id}, quantity={_quantity!.Value:N0}");
        foreach (var r in plan.Rows)
        {
            string notes = string.Join(" / ", r.Step.Notes.Select(n => n.Requirements is { } group
                ? "[requires: " + string.Join(", ", MakeRecipes.Words(group)) + "]" : n.Text));
            _ctx.Log($"Dev: Make step {r.Material.Id}, recipe={r.Step.Recipe?.Id}, yield={r.Step.Yield}, " +
                $"needed={r.Needed}, bank={r.Bank}, left={r.Left}, actions={r.Actions}, seconds={r.Seconds}, time='{r.Time}', " +
                $"covered={r.Covered}, cycle={r.Cycle}, sharedWith={r.SharedWith?.Material.Id}, choices={r.Step.Choices}, " +
                $"fromBar={r.Step.FromBar}. {notes}");
            if (r.Step.Costs == null && r.Step.Yield == 1)
            {
                double? planned = TimeSpan.SecondsPerHour / r.Step.Seconds;
                _ctx.Log($"Dev: Make rate {r.Material.Id}: plan/hour={planned?.ToString("G17") ?? "no rate"}, footer/hour={r.Step.FooterRate?.ToString("G17") ?? "no rate"}");
            }
        }
        LogRequirementViews();
    }

    // The plan's requirement views beside the game's own (on its screens, and in the copy of its item panel),
    // to compare their settings, sizes and words with a picture of the game.
    void LogRequirementViews()
    {
        if (_requirementViews.Count == 0) return;
        foreach (var view in _requirementViews) _ctx.Log("Dev: Make requirement view " + DescribeView((ConditionGroupView)view));
        int found = 0;
        void Walk(Node node)
        {
            if (found >= 6) return;
            if (node is ConditionGroupView game && !_requirementViews.Contains(game))
            {
                found++;
                _ctx.Log($"Dev: the game's requirement view in {node.GetParent()?.GetType().Name}:{node.GetParent()?.Name}: {DescribeView(game)}");
            }
            foreach (Node child in node.GetChildren()) Walk(child);
        }
        foreach (Node? root in new Node?[] { GameAccess.FindGameView(), _pinPanel })
            if (root != null && GodotObject.IsInstanceValid(root)) Walk(root);
        if (found == 0) _ctx.Log("Dev: no requirement view of the game's to compare (a skill screen with a locked recipe has one).");
    }

    static string DescribeView(ConditionGroupView view)
    {
        var lines = view.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>().Take(4)
            .Select(l => $"'{l.Text}' size={l.GetThemeFontSize("normal_font_size")} wrap={l.AutowrapMode} fit={l.FitContent} mouse={l.MouseFilter}");
        return $"centered={view.CenterConditions}, coloured={view.ColourConditions}, shown={view.IsVisibleInTree()}, size={view.Size}: " +
            string.Join(" | ", lines);
    }

    void DevMake(string value)
    {
        if (value == "closed" && _makeButton != null)
        {
            _makeButton.ButtonPressed = false;
            RefreshMakeBox();
            return;
        }
        if (!int.TryParse(value, out int count) || count < 1 || _makeButton == null)
        {
            EndMakeRequest("No Make button or invalid quantity: " + value);
            return;
        }
        _makeButton.ButtonPressed = true;
        RefreshMakeBox();
        if (_quantity == null || _makeFailed) { EndMakeRequest("Could not open Make."); return; }
        _quantity.Value = count;
        RefreshMakeBox();
    }

    void DevScroll(string value)
    {
        if (_makeScroll is not { Visible: true } scroll) { EndMakeRequest("No open plan to scroll."); return; }
        if (value == "bottom") scroll.ScrollVertical = (int)scroll.GetVScrollBar().MaxValue;
        else if (value == "top") scroll.ScrollVertical = 0;
        else if (_stepSpots.TryGetValue(value, out var spot)) scroll.EnsureControlVisible(spot);
        else EndMakeRequest("No matching row to scroll to: " + value);
    }

    void EndMakeRequest(string why)
    {
        _ctx.Log("Dev: " + why + " Remaining pictures skipped.");
        _devSteps.Clear();
        _devPointer = null;
        File.WriteAllText(Path.Combine(_ctx.Directory, "item.done"), why);
        FinishMakeCase();
    }

    void FinishMakeCase()
    {
        _caseItems?.Dispose();
        _caseItems = null;
        _checkLong = false;
    }

    void StartMakeCase(string name)
    {
        FinishMakeCase();
        if (_index == null || _game == null || _makeFailed)
        {
            EndMakeRequest("The index or planner is unavailable.");
            return;
        }
        _caseName = name;
        _caseItems = _index.Entries.GetEnumerator();
        _recipes ??= new(_game, _index, _lines!);
        _recipes.BeginRead(GameAccess.FindGameView()?.Footer);
    }

    // Search the index incrementally, one candidate per frame and only on an explicit picture request.
    // Every case goes through the real pin and planner. A missing case never produces a substitute shot.
    bool AdvanceMakeCase()
    {
        if (_caseItems == null) return false;
        try
        {
            if (_checkLong)
            {
                if (Time.GetTicksMsec() < _devStepAt) return true;
                _checkLong = false;
                if (_makeScroll is { Visible: true } s && s.GetVScrollBar().Visible)
                {
                    FinishMakeCase();
                    return true;
                }
            }
            if (!_caseItems.MoveNext())
            {
                EndMakeRequest("No matching item on this character for case=" + _caseName + ".");
                return true;
            }
            var (item, entry) = _caseItems.Current;
            bool plain = _caseName == "plain";
            if (entry.Sources.Any(MakeRecipes.MakesItem) == plain) return true;
            var recipes = _recipes!;
            int count = _caseName switch { "covered" => 1, "wide" => 1_000_000, "overflow" => int.MaxValue, _ => 100 };
            if (_caseName == "covered" && recipes.InBank(item) < count) return true;
            if (_caseName == "arrow")
            {
                string name = item.DisplayName.GetPluralText(1);
                if (!name.EndsWith("arrow", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith("arrows", StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (!plain)
            {
                try
                {
                    var rows = new MakePlan(recipes, item, count, new MakeInputs()).Rows.ToList();
                    bool match = _caseName switch
                    {
                        "covered" => rows.Count == 1 && rows[0].Left == 0 && rows[0].Step.Costs is { Count: > 0 },
                        "wide" => rows.Count > 1 && rows.Any(r => r.Left >= 1_000_000),
                        "rate" => rows.Any(r => r.Left > 0 && r.Step.FooterRate > 0),
                        "arrow" or "long" => rows.Count > 1,
                        "locked" => rows.Any(r => r.Material != item && r.Left > 0 && r.Step.Locked),
                        "multiple" => rows[^1].Step.Choices > 1,
                        "empty" => rows.Count > 1 && rows.All(r => r.Bank == 0),
                        "currency" => rows.Any(r => r.Material is Currency),
                        "cycle" => rows.Any(r => r.Cycle && r.Left > 0),
                        "crop" => rows.Any(r => r.Left > 0 && r.Step.Recipe is FarmingRecipe),
                        _ => false,
                    };
                    if (!match) return true;
                }
                catch (OverflowException) { if (_caseName != "overflow") return true; }
            }

            GameTipItem(out _, out _);
            if (_gameTipPanel == null) { EndMakeRequest("The game tooltip is unavailable."); return true; }
            Pin(item, GameTipCard());
            if (_pinCard == null) { EndMakeRequest("Could not pin " + item.Id); return true; }
            // Start at the bottom edge so the same placement as a real pinned card moves it above the footer.
            _pinCard.Position = _ctx.Tree.Root.GetVisibleRect().Size - _pinCard.Size;
            if (!plain) DevMake(count.ToString());
            if (_caseItems == null) return true;
            _ctx.Log($"Dev: case={_caseName}, item={item.Id}, target={count}. Replay pin: pin={item.DisplayName.GetPluralText(1)}@1,1");
            _devStepAt = Time.GetTicksMsec() + WayOpenMs;
            if (_caseName == "long" && !_makeFailed)
                _checkLong = true;
            else FinishMakeCase();
        }
        catch (Exception e) { EndMakeRequest(e.Message); }
        return true;
    }
}
#endif
