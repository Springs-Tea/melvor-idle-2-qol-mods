using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.ArtisanAction;
using GamesByMalcs.Melvor2.Components.CraftingAction;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Cooking;
using GamesByMalcs.Melvor2.Entities.Firemaking;
using GamesByMalcs.Melvor2.Entities.Herblore;
using GamesByMalcs.Melvor2.Tooltips;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace RecipeRates;

/// <summary>
/// Shows what a recipe gives per hour before you make it: the XP per hour under the "+13" of every recipe in
/// a skill's list (the best one in gold), and in that tag's tooltip the full hour: XP, items made, items
/// used, what the products sell for and how long the materials in the bank last.
/// Works for every skill that makes things from a recipe list: Smithing, Crafting, Fletching, Runecrafting,
/// Beastcrafting, Cooking, Firemaking and Herblore.
/// </summary>
public class RecipeRatesMod : IMod
{
    const string LabelName = "RecipeRatesLabel";
    // Our block in a tooltip starts with this, which is how it is found again.
    const string Marker = "[color=#b5b5b5]Per hour (estimate)";
    // The game's own colours for gains, losses and highlighted words in its tooltips and footer.
    const string Gain = "green", Loss = "#ff4500", Highlight = "#FFD16D", Quiet = "#b5b5b5";
    static readonly Color BestColor = new("ffd16d");

    // The game's "+13" tag keeps the XP stats it shows, and its tooltip, to itself.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Experience")]
    static extern ExperienceStats? ExperienceOf(SkillXPPanel panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_XPTooltip")]
    static extern TextTooltip? TooltipOf(SkillXPPanel panel);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_contentText")]
    static extern ref List<string> ContentOf(TextTooltip tooltip);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "UpdateContent")]
    static extern void Redraw(TextTooltip tooltip);

    ModContext _ctx = null!;
    readonly GameStyle _style = new();
    Game? _game;
    BankEntity? _bank;
    // Which recipe a "+13" tag belongs to: the tag shows the recipe's own XP stats object.
    readonly Dictionary<ExperienceStats, IActiveCraftingRecipe> _recipes = new(ReferenceEqualityComparer.Instance);
    readonly List<SkillXPPanel> _panels = new();
    readonly List<(SkillXPPanel Panel, Rates Rates, bool InList)> _shown = new();
    int _frame;
    bool _stopped, _tooltips = true, _firstLogged;

    public void OnLoad(ModContext ctx)
    {
        _ctx = ctx;
        ctx.Frame += OnFrame;
        ctx.Log("Ready.");
    }

    void OnFrame()
    {
        if (_stopped || ++_frame % 30 != 0) return;   // twice a second
        Game? game = GameAccess.CurrentGame;
        if (game is not { IsSaveLoaded: true }) { _game = null; return; }
        try
        {
            if (!ReferenceEquals(game, _game)) { _game = game; MapRecipes(game); }
            Update();
        }
        catch (Exception e)
        {
            // A private member that is gone after a game update ends up here.
            _ctx.Log("Stopped (the game was updated?): " + e);
            _stopped = true;
        }
    }

    void MapRecipes(Game game)
    {
        _recipes.Clear();
        _bank = game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? bank) ? bank as BankEntity : null;
        foreach (var entity in game.Entities.GetAllObjects())
            foreach (var component in entity.Components.Values)
            {
                IEnumerable<IActiveCraftingRecipe>? recipes = component switch
                {
                    ArtisanActionComponent a => a.ActiveRecipes.Values,
                    HerbloreActionComponent h => h.ActiveRecipes.Values,
                    CookingActionComponent c => c.ActiveRecipes.Values,
                    FiremakingActionComponent f => f.ActiveRecipes.Values,
                    _ => null,
                };
                if (recipes == null) continue;
                foreach (IActiveCraftingRecipe recipe in recipes)
                    _recipes[recipe.Stats.Experience] = recipe;
            }
        _ctx.Log($"Character opened: {_recipes.Count} recipes known.");
    }

    void Update()
    {
        _panels.Clear();
        _shown.Clear();
        if (GameAccess.FindGameView() is { } view) Collect(view, _panels);

        foreach (SkillXPPanel panel in _panels)
        {
            ExperienceStats? xp = ExperienceOf(panel);
            if (xp == null || !_recipes.TryGetValue(xp, out IActiveCraftingRecipe? recipe) || Rates.For(recipe) is not { } rates)
            {
                // A reused tag that now shows something else.
                if (panel.FindChild(LabelName, true, false) is Control stale) stale.Visible = false;
                continue;
            }
            _shown.Add((panel, rates, InRecipeList(panel)));
        }

        // The recipe with the most XP per hour among those listed stands out, when there is a choice.
        double best = 0;
        int listed = 0;
        foreach (var (_, rates, inList) in _shown)
            if (inList) { listed++; best = Math.Max(best, rates.XpPerHour); }

        foreach (var (panel, rates, inList) in _shown)
        {
            bool isBest = inList && listed > 1 && best > 0 && rates.XpPerHour >= best - 0.5;
            if (inList) ShowInList(panel, rates, isBest);
            if (_tooltips) ShowInTooltip(panel, rates, isBest);
        }

        if (!_firstLogged && _shown.Count > 0)
        {
            _firstLogged = true;
            _ctx.Log($"Rates shown on {_shown.Count} recipes ({listed} in a list).");
        }
#if DEBUG
        DevCapture();
#endif
    }

    // Only what is on screen: a hidden page costs nothing.
    static void Collect(Node node, List<SkillXPPanel> panels)
    {
        if (node is CanvasItem { Visible: false }) return;
        if (node is SkillXPPanel panel) { panels.Add(panel); return; }
        for (int i = 0, n = node.GetChildCount(); i < n; i++) Collect(node.GetChild(i), panels);
    }

    static CraftingRecipeButton? RecipeButtonOf(Node node)
    {
        for (Node? n = node.GetParent(); n != null; n = n.GetParent())
            if (n is CraftingRecipeButton button) return button;
        return null;
    }

    static bool InRecipeList(Node node) => RecipeButtonOf(node) != null;

    // ---- the list: "5,760 /hr" right under the game's "+8" tag --------------------------------------------

    void ShowInList(SkillXPPanel panel, Rates rates, bool isBest)
    {
        // Our line hangs on the tag's own label: a label does not arrange its children, so nothing in the
        // game's layout moves.
        if (panel.FindChild("SkillXPLabel", true, false) is not Label anchor) return;
        var line = anchor.GetNodeOrNull<HBoxContainer>(LabelName);
        if (line == null)
        {
            // A step smaller than the game's own number, so it reads as its footnote at any UI size.
            int size = anchor.LabelSettings?.FontSize ?? anchor.GetThemeFontSize("font_size");
            line = _style.Spaced(new HBoxContainer { Name = LabelName, MouseFilter = Control.MouseFilterEnum.Ignore }, 2);
            Label number = _style.Text("", GameStyle.TextPaper, Math.Max(10, size - 2), GameStyle.SemiBold);
            number.Name = "Number";
            number.MouseFilter = Control.MouseFilterEnum.Ignore;
            Label unit = _style.Text("/hr", GameStyle.TextDim, Math.Max(9, size - 3));
            unit.Name = "Unit";
            unit.MouseFilter = Control.MouseFilterEnum.Ignore;
            line.AddChild(number);
            line.AddChild(unit);
            anchor.AddChild(line);
        }
        Label value = line.GetNode<Label>("Number"), suffix = line.GetNode<Label>("Unit");
        value.AddThemeColorOverride("font_color", isBest ? BestColor : GameStyle.TextPaper);
        line.Visible = true;

        // The space under the tag is free unless the recipe has a long row of ingredients: then fall back
        // to shorter forms rather than write over the last one.
        float right = panel.GlobalPosition.X + panel.Size.X - 2, top = panel.GlobalPosition.Y + panel.Size.Y + 1;
        float taken = IngredientsEnd(panel, top);
        (string Number, bool Unit)[] forms =
        {
            (Rates.Number(rates.XpPerHour), true),
            (Rates.Compact(rates.XpPerHour), true),
            (Rates.Compact(rates.XpPerHour), false),
        };
        foreach (var (number, unit) in forms)
        {
            if (value.Text != number) value.Text = number;
            suffix.Visible = unit;
            line.Size = line.GetCombinedMinimumSize();
            if (right - line.Size.X >= taken + 4) break;
        }
        line.GlobalPosition = new Vector2(MathF.Round(right - line.Size.X), MathF.Round(top));
    }

    // Where the ingredient icons that share our line's height end, or far left when there are none.
    static float IngredientsEnd(SkillXPPanel panel, float top)
    {
        float end = float.MinValue;
        if (RecipeButtonOf(panel)?.FindChild("RecipeCostContainer", true, false) is not Control costs) return end;
        for (int i = 0, n = costs.GetChildCount(); i < n; i++)
        {
            if (costs.GetChild(i) is not Control { Visible: true } icon) continue;
            if (icon.GlobalPosition.Y > top + 14 || icon.GlobalPosition.Y + icon.Size.Y < top) continue;   // another row
            end = Math.Max(end, icon.GlobalPosition.X + icon.Size.X);
        }
        return end;
    }

    // ---- the tooltip: the whole hour ----------------------------------------------------------------------

    // The game rewrites the tooltip whenever its numbers change, which drops our block: put it back, and
    // replace it in place when only our own numbers moved.
    void ShowInTooltip(SkillXPPanel panel, Rates rates, bool isBest)
    {
        try
        {
            if (TooltipOf(panel) is not { } tooltip) return;
            List<string> content = ContentOf(tooltip);
            string block = TooltipBlock(rates, isBest);
            int at = content.FindIndex(line => line.Contains(Marker));
            if (at < 0)
            {
                tooltip.AddContentNewline();
                tooltip.AppendContent(block);
            }
            else if (content[at] != block)
            {
                content[at] = block;
                Redraw(tooltip);
            }
        }
        catch (Exception e) when (e is MissingMethodException or MissingFieldException)
        {
            _tooltips = false;
            _ctx.Log("The XP tooltip changed in this game version: the hourly breakdown is left out. " + e.Message);
        }
    }

    string TooltipBlock(Rates rates, bool isBest)
    {
        double perHour = rates.RewardsPerHour;
        var sb = new StringBuilder();
        sb.Append('\n').Append(Marker).Append("[/color]");
        sb.Append($"\nSkill XP: [color={Gain}]{Rates.Number(rates.XpPerHour)}[/color]");
        foreach (Rates.Line made in rates.Made)
            sb.Append($"\n{made.Name}: [color={Gain}]+{Rates.Number(made.PerReward * perHour)}[/color]");
        foreach (Rates.Line used in rates.Used)
            sb.Append($"\n{used.Name}: [color={Loss}]{Rates.Number(used.PerReward * perHour)}[/color]");
        if (rates.SellValuePerHour >= 1)
            sb.Append($"\nSell value: [color={Highlight}]{Rates.Number(rates.SellValuePerHour)} {rates.SellCurrency}[/color]");

        sb.Append($"\n\n[color={Quiet}]One every {rates.SecondsPerReward:0.##}s");
        if (rates.Success < 0.9995) sb.Append($", {rates.Success:P0} success");
        sb.Append("[/color]");

        // Whole minutes: the bank changes with every item made, and a line that ticks would redraw the
        // tooltip all the time.
        if (_bank != null && rates.SecondsOfMaterials(_bank) is { } seconds)
            sb.Append(seconds <= 0 ? $"\n[color={Loss}]Not enough materials[/color]"
                    : seconds < 60 ? $"\n[color={Quiet}]Materials for less than a minute[/color]"
                    : $"\n[color={Quiet}]Materials for {Whole(seconds)}[/color]");
        if (isBest) sb.Append($"\n[color={Highlight}]Most XP per hour in this list[/color]");
        return sb.ToString();
    }

    static string Whole(double seconds)
    {
        if (seconds >= 100 * 3600) return $"{seconds / 3600:N0}h";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m";
    }

#if DEBUG
    // Development: each time a recipe list comes on screen, and each time one of the XP tooltips is opened,
    // save a picture of the game into the mod's folder; log what was worked out the first time.
    int _listScans, _tipScans;
    bool _logged;

    void DevCapture()
    {
        _listScans = _shown.Any(s => s.InList) ? _listScans + 1 : 0;
        if (_listScans == 3)
        {
            Save("shot-list.png");
            if (!_logged)
            {
                _logged = true;
                foreach (var (panel, rates, inList) in _shown.Take(10))
                    _ctx.Log($"Dev: {(inList ? "list" : "other")} {rates.XpPerHour:N0} XP/hr, {rates.SecondsPerReward:0.###}s each, sell {rates.SellValuePerHour:N0}, " +
                             string.Join(", ", rates.Made.Concat(rates.Used).Select(i => $"{i.Name} {i.PerReward * rates.RewardsPerHour:+0.#;-0.#}")));
            }
        }
        bool open = _shown.Any(s => TooltipOf(s.Panel) is { IsShown: true });
        _tipScans = open ? _tipScans + 1 : 0;
        if (_tipScans == 2) Save("shot-tooltip.png");
    }

    void Save(string file)
    {
        _ctx.Tree.Root.GetTexture().GetImage().SavePng(System.IO.Path.Combine(_ctx.Directory, file));
        _ctx.Log("Dev: saved " + file);
    }
#endif
}
