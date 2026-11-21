#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using MelvorModLoader;

namespace CombatGoals;

/// <summary>
/// Development only (not compiled into Release builds). Writes what the game looks like into the mod's folder:
/// a dump of its themes, a dump of the scene tree, and screenshots on request (create a file "shot.request").
/// </summary>
static class Dev
{
    static ModContext s_ctx = null!;
    static ulong s_started, s_lastCheck, s_gameAt;
    static bool s_menuDone, s_gameDone, s_loadPressed, s_welcomeConfirmed;

    public static void Start(ModContext ctx)
    {
        s_ctx = ctx;
        s_started = Time.GetTicksMsec();
        ctx.Frame += OnFrame;
    }

    static void OnFrame()
    {
        ulong now = Time.GetTicksMsec();
        if (now - s_lastCheck < 1000) return;
        s_lastCheck = now;
        try
        {
            if (!s_menuDone && now - s_started > 6000)
            {
                s_menuDone = true;
                DumpThemes("theme-dump-menu.txt");
                DumpTree("tree-dump-menu.txt");
                Shot("shot-menu.png");
            }
            if (!s_gameDone && GameAccess.CurrentGame is { IsSaveLoaded: true })
            {
                s_gameDone = true;
                s_gameAt = now;
                DumpThemes("theme-dump-game.txt");
            }
            // A while after the character opened, so the screens have settled.
            if (s_gameAt != 0 && now - s_gameAt > 30000)
            {
                s_gameAt = 0;
                DumpTree("tree-dump-game.txt");
                Shot("shot.png");
            }
            // On the character selection screen: press "Load" when there is exactly one character, so a
            // restart during development ends up in the game without a click.
            if (!s_loadPressed && GameAccess.CurrentGame == null && now - s_started > 8000)
            {
                var loads = s_ctx.Tree.Root.FindChildren("*", "Button", true, false)
                    .OfType<Button>().Where(b => b.Text == "Load" && b.IsVisibleInTree() && !b.Disabled).ToList();
                if (loads.Count == 1)
                {
                    s_loadPressed = true;
                    s_ctx.Log("Dev: pressing Load on the only character.");
                    loads[0].EmitSignal(BaseButton.SignalName.Pressed);
                }
            }
            // The "Welcome Back" summary after a restart dims the whole screen: confirm it, so pictures taken
            // during development show the game.
            if (!s_welcomeConfirmed && s_gameDone && now - s_gameAt < 20000)
            {
                var confirm = s_ctx.Tree.Root.FindChildren("*", "Button", true, false)
                    .OfType<Button>().FirstOrDefault(b => b.Text == "Confirm" && b.IsVisibleInTree() && !b.Disabled);
                if (confirm != null)
                {
                    s_welcomeConfirmed = true;
                    s_ctx.Log("Dev: confirming the Welcome Back summary.");
                    confirm.EmitSignal(BaseButton.SignalName.Pressed);
                }
            }
            AreasRequest(now);
            ProbeRequest();
            string request = Path.Combine(s_ctx.Directory, "shot.request");
            if (File.Exists(request))
            {
                File.Delete(request);
                Shot("shot.png");
                DumpTree("tree-dump-game.txt");
            }
        }
        catch (Exception e) { s_ctx.Log("Dev: " + e); }
    }

    // A file "areas.request" opens the combat areas menu on the first category that has a dungeon, with that
    // dungeon selected, then writes "tree-dump-areas.txt" and "shot-areas.png" and closes the menu again.
    static ulong s_areasAt;
    static Control? s_areasMenu;

    static void AreasRequest(ulong now)
    {
        string request = Path.Combine(s_ctx.Directory, "areas.request");
        if (s_areasAt == 0 && File.Exists(request))
        {
            File.Delete(request);
            if (GameAccess.CurrentGame is not { IsSaveLoaded: true } game) return;
            if (!game.Entities.TryGetObjectById("MelvorBase:Combat", out var e) || e is not GamesByMalcs.Melvor2.Entities.Combat.CombatEntity combat) return;
            s_areasMenu = GameAccess.FindNode<GamesByMalcs.Melvor2.Components.CombatAreas.CombatAreasMenu>(GameAccess.FindGameView());
            if (s_areasMenu == null) { s_ctx.Log("Dev: the areas menu was not found."); return; }
            foreach (var category in combat.Areas.ActiveCategories.Values)
            {
                var dungeon = category.Areas.FirstOrDefault(a => a.Area is GamesByMalcs.Melvor2.Components.CombatAreas.Dungeon);
                if (dungeon == null) continue;
                s_areasMenu.Visible = true;
                combat.Areas.VisibleCategory = category.Category;
                combat.Areas.VisibleArea = dungeon;
                s_ctx.Log($"Dev: areas menu opened on {category.Category.DisplayName.Text} / {dungeon.DisplayName.Text}; " +
                          $"completed {combat.Statistics.DungeonCompleted.GetValue((GamesByMalcs.Melvor2.Components.CombatAreas.Dungeon)dungeon.Area)} times.");
                s_areasAt = now;
                return;
            }
            s_ctx.Log("Dev: no category has a dungeon.");
        }
        else if (s_areasAt != 0 && now - s_areasAt >= 2000)
        {
            s_areasAt = 0;
            DumpTree("tree-dump-areas.txt");
            Shot("shot-areas.png");
            if (File.Exists(Path.Combine(s_ctx.Directory, "areas.keep"))) return;
            if (s_areasMenu != null && GodotObject.IsInstanceValid(s_areasMenu)) s_areasMenu.Visible = false;
        }
    }

    // A file "probe.request" writes "probe.txt": for every skill that creates things from recipes, the real
    // numbers (intervals, steps, XP, costs, products) of its first few recipes and of the one being made.
    // Reflection on purpose: this is a look around, it must not break when a skill has its own stats class.
    static void ProbeRequest()
    {
        string request = Path.Combine(s_ctx.Directory, "probe.request");
        if (!File.Exists(request)) return;
        File.Delete(request);
        if (GameAccess.CurrentGame is not { IsSaveLoaded: true } game) return;
        var sb = new StringBuilder();
        foreach (var entity in game.Entities.GetAllObjects())
            foreach (var (name, component) in entity.Components)
            {
                if (component.GetType().GetProperty("ActiveRecipes")?.GetValue(component) is not System.Collections.IEnumerable recipes) continue;
                object? creating = component.GetType().GetProperty("CreatingRecipe")?.GetValue(component);
                sb.AppendLine($"=== {entity.Id} / {name} ({component.GetType().Name}) creating={Describe(creating)}");
                int n = 0;
                foreach (object pair in recipes)
                {
                    object? active = pair.GetType().GetProperty("Value")?.GetValue(pair);
                    if (active == null) continue;
                    if (n++ >= 3 && !ReferenceEquals(active, creating)) continue;
                    sb.AppendLine($"  -- {Describe(active)} [{active.GetType().Name}]");
                    Members(active, sb, "     ", 0);
                }
            }
        File.WriteAllText(Path.Combine(s_ctx.Directory, "probe.txt"), sb.ToString());
        s_ctx.Log("Dev: wrote probe.txt");
    }

    static string Describe(object? o)
    {
        if (o == null) return "null";
        object? name = o.GetType().GetProperty("DisplayName")?.GetValue(o);
        string? text = name?.GetType().GetProperty("Text")?.GetValue(name) as string;
        object? id = o.GetType().GetProperty("Id")?.GetValue(o);
        return text ?? id?.ToString() ?? o.ToString() ?? "?";
    }

    static void Members(object o, StringBuilder sb, string indent, int depth)
    {
        foreach (var p in o.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            object? v;
            try { v = p.GetValue(o); } catch { continue; }
            if (v == null) continue;
            Type t = v.GetType();
            if (t.IsPrimitive || t.IsEnum || v is string) { sb.AppendLine($"{indent}{p.Name} = {v}"); continue; }
            string tn = t.Name;
            // The nodes are subclasses (ComputedValueStatNode, CombineStatNode...): look up the chain.
            bool isValue = false, isValues = false;
            for (Type? b = t; b != null; b = b.BaseType)
            {
                isValue |= b.Name.StartsWith("ValueStatNode");
                isValues |= b.Name.StartsWith("ValuesStatNode");
            }
            if (isValue)
            {
                object? value = t.GetProperty("Value")?.GetValue(v);
                sb.AppendLine($"{indent}{p.Name} = {Value(value)}");
            }
            else if (isValues)
            {
                var list = (t.GetProperty("Values")?.GetValue(v) as System.Collections.IEnumerable)?.Cast<object>()
                    .Select(node => Value(node.GetType().GetProperty("Value")?.GetValue(node)));
                sb.AppendLine($"{indent}{p.Name} = [{string.Join(", ", list ?? [])}]");
            }
            else if (depth < 2 && (tn.Contains("Stats") || tn.Contains("StatNodes") || tn.Contains("Recipe") || tn.Contains("Health")) && t.Namespace?.StartsWith("GamesByMalcs") == true)
            {
                sb.AppendLine($"{indent}{p.Name}: [{tn}] {(tn.Contains("Recipe") ? Describe(v) : "")}");
                Members(v, sb, indent + "   ", depth + 1);
            }
            else if (v is System.Collections.IEnumerable items && depth < 2 && (p.Name.Contains("Product") || p.Name.Contains("Cost")))
                sb.AppendLine($"{indent}{p.Name} = [{string.Join(", ", items.Cast<object>().Select(Value))}]");
        }
    }

    static string Value(object? v)
    {
        if (v == null) return "null";
        Type t = v.GetType();
        if (t.IsPrimitive || t.IsEnum || v is string) return v.ToString()!;
        object? item = t.GetProperty("Item")?.GetValue(v), currency = t.GetProperty("Currency")?.GetValue(v);
        object? quantity = t.GetProperty("Quantity")?.GetValue(v), chance = t.GetProperty("Chance")?.GetValue(v);
        if (item != null || currency != null)
            return $"{Describe(item ?? currency)} x{quantity}{(chance != null ? $" @{chance}" : "")}";
        if (t.IsGenericType && t.Name.StartsWith("ValueTuple")) return v.ToString()!;
        return $"<{t.Name}> {Describe(v)}";
    }

    static void Shot(string file)
    {
        Image image = s_ctx.Tree.Root.GetTexture().GetImage();
        image.SavePng(Path.Combine(s_ctx.Directory, file));
        s_ctx.Log($"Dev: saved {file} ({image.GetWidth()}x{image.GetHeight()})");
    }

    static void DumpTree(string file)
    {
        var sb = new StringBuilder();
        Walk(s_ctx.Tree.Root, 0, sb);
        File.WriteAllText(Path.Combine(s_ctx.Directory, file), sb.ToString());
    }

    static void Walk(Node node, int depth, StringBuilder sb)
    {
        sb.Append(' ', depth * 2).Append(node.Name).Append(" (").Append(node.GetType().Name).Append(')');
        if (node is Control c)
        {
            if (!c.IsVisibleInTree()) sb.Append(" hidden");
            if (c.ThemeTypeVariation != "") sb.Append(" variation=").Append(c.ThemeTypeVariation);
            if (c.Theme != null) sb.Append(" THEME=").Append(c.Theme.ResourcePath);
            sb.Append($" pos={c.GlobalPosition.X:0},{c.GlobalPosition.Y:0} size={c.Size.X:0}x{c.Size.Y:0}");
            if (c.Modulate != Colors.White) sb.Append(" modulate=#").Append(c.Modulate.ToHtml());
            if (c.SelfModulate != Colors.White) sb.Append(" selfmodulate=#").Append(c.SelfModulate.ToHtml());
            if (c.CustomMinimumSize != Vector2.Zero) sb.Append($" min={c.CustomMinimumSize.X:0}x{c.CustomMinimumSize.Y:0}");
            switch (node)
            {
                case Label l:
                    sb.Append(" text=\"").Append(l.Text.Replace("\n", "\\n")).Append('"')
                      .Append($" font={FontName(l.GetThemeFont("font"))} fs={l.GetThemeFontSize("font_size")} color=#{l.GetThemeColor("font_color").ToHtml()}");
                    break;
                case RichTextLabel r:
                    sb.Append(" rich=\"").Append(r.Text.Replace("\n", "\\n")).Append('"')
                      .Append($" font={FontName(r.GetThemeFont("normal_font"))} fs={r.GetThemeFontSize("normal_font_size")} color=#{r.GetThemeColor("default_color").ToHtml()}");
                    break;
                case SpinBox s:
                    sb.Append($" up={s.GetThemeIcon("up")?.ResourcePath} down={s.GetThemeIcon("down")?.ResourcePath} updown={s.GetThemeIcon("updown")?.ResourcePath}")
                      .Append($" line.normal={Box(s.GetLineEdit().GetThemeStylebox("normal"))}");
                    break;
                case BaseButton b:
                    if (b is Button bb) sb.Append(" text=\"").Append(bb.Text).Append('"').Append($" icon={bb.Icon?.ResourcePath} font={FontName(bb.GetThemeFont("font"))} fs={bb.GetThemeFontSize("font_size")}");
                    if (b is TextureButton tb) sb.Append($" tex={tb.TextureNormal?.ResourcePath}");
                    sb.Append($" normal={Box(b.GetThemeStylebox("normal"))}");
                    break;
                case PanelContainer or Panel:
                    sb.Append($" panel={Box(c.GetThemeStylebox("panel"))}");
                    break;
                case TextureRect t:
                    sb.Append($" tex={t.Texture?.ResourcePath}");
                    break;
                case ProgressBar p:
                    sb.Append($" bg={Box(p.GetThemeStylebox("background"))} fill={Box(p.GetThemeStylebox("fill"))}");
                    break;
                case LineEdit e:
                    sb.Append($" normal={Box(e.GetThemeStylebox("normal"))} color=#{e.GetThemeColor("font_color").ToHtml()}");
                    break;
                case MarginContainer m:
                    sb.Append($" margins={m.GetThemeConstant("margin_left")},{m.GetThemeConstant("margin_top")},{m.GetThemeConstant("margin_right")},{m.GetThemeConstant("margin_bottom")}");
                    break;
                case BoxContainer bc:
                    sb.Append($" sep={bc.GetThemeConstant("separation")}");
                    break;
                case Separator sp:
                    sb.Append($" sepbox={Box(sp.GetThemeStylebox("separator"))}");
                    break;
            }
        }
        sb.AppendLine();
        foreach (Node child in node.GetChildren()) Walk(child, depth + 1, sb);
    }

    static void DumpThemes(string file)
    {
        var sb = new StringBuilder();
        sb.AppendLine("== project theme ==");
        Describe(ThemeDB.GetProjectTheme(), sb);
        foreach (Theme theme in Find(s_ctx.Tree.Root).Distinct())
        {
            sb.AppendLine().AppendLine("== theme on a node: " + theme.ResourcePath + " ==");
            Describe(theme, sb);
        }
        File.WriteAllText(Path.Combine(s_ctx.Directory, file), sb.ToString());
        s_ctx.Log("Dev: wrote " + file);
    }

    static System.Collections.Generic.IEnumerable<Theme> Find(Node node)
    {
        if (node is Control { Theme: not null } c) yield return c.Theme;
        if (node is Window { Theme: not null } w) yield return w.Theme;
        foreach (Node child in node.GetChildren())
            foreach (Theme t in Find(child)) yield return t;
    }

    static void Describe(Theme? theme, StringBuilder sb)
    {
        if (theme == null) { sb.AppendLine("(none)"); return; }
        sb.AppendLine($"default font: {theme.DefaultFont?.ResourcePath} size {theme.DefaultFontSize} scale {theme.DefaultBaseScale}");
        foreach (string type in theme.GetTypeList().OrderBy(t => t))
        {
            string baseType = theme.GetTypeVariationBase(type);
            sb.AppendLine($"[{type}]" + (baseType != "" ? $" (variation of {baseType})" : ""));
            foreach (string name in theme.GetStyleboxList(type)) sb.AppendLine($"  stylebox {name}: {Box(theme.GetStylebox(name, type))}");
            foreach (string name in theme.GetColorList(type)) sb.AppendLine($"  color {name}: #{theme.GetColor(name, type).ToHtml()}");
            foreach (string name in theme.GetFontList(type)) sb.AppendLine($"  font {name}: {theme.GetFont(name, type)?.ResourcePath}");
            foreach (string name in theme.GetFontSizeList(type)) sb.AppendLine($"  font_size {name}: {theme.GetFontSize(name, type)}");
            foreach (string name in theme.GetConstantList(type)) sb.AppendLine($"  constant {name}: {theme.GetConstant(name, type)}");
            foreach (string name in theme.GetIconList(type)) sb.AppendLine($"  icon {name}: {theme.GetIcon(name, type)?.ResourcePath}");
        }
    }

    static string FontName(Font? font) => font switch
    {
        null => "null",
        FontVariation v => $"variation({v.BaseFont?.ResourcePath} embolden={v.VariationEmbolden})",
        _ => Path.GetFileName(font.ResourcePath),
    };

    static string Box(StyleBox? box) => box switch
    {
        null => "null",
        StyleBoxTexture t => $"Texture {t.Texture?.ResourcePath} texmargin={t.TextureMarginLeft},{t.TextureMarginTop},{t.TextureMarginRight},{t.TextureMarginBottom} " +
                             $"margin={t.ContentMarginLeft},{t.ContentMarginTop},{t.ContentMarginRight},{t.ContentMarginBottom} modulate=#{t.ModulateColor.ToHtml()} center={t.DrawCenter} res={t.ResourcePath}",
        StyleBoxLine ln => $"Line color=#{ln.Color.ToHtml()} thickness={ln.Thickness} vertical={ln.Vertical}",
        StyleBoxFlat f => $"Flat bg=#{f.BgColor.ToHtml()} border=#{f.BorderColor.ToHtml()} bw={f.BorderWidthLeft},{f.BorderWidthTop},{f.BorderWidthRight},{f.BorderWidthBottom} " +
                          $"radius={f.CornerRadiusTopLeft},{f.CornerRadiusTopRight},{f.CornerRadiusBottomRight},{f.CornerRadiusBottomLeft} " +
                          $"margin={f.ContentMarginLeft},{f.ContentMarginTop},{f.ContentMarginRight},{f.ContentMarginBottom} shadow={f.ShadowSize} #{f.ShadowColor.ToHtml()}",
        StyleBoxEmpty e => $"Empty margin={e.ContentMarginLeft},{e.ContentMarginTop},{e.ContentMarginRight},{e.ContentMarginBottom}",
        _ => box.GetType().Name,
    };
}
#endif
