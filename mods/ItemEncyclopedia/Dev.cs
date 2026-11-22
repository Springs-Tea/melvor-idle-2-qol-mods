#if DEVTOOLS
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Localization;
using Godot;
using MelvorMods.Shared;

namespace ItemEncyclopedia;

/// <summary>
/// Development only (-p:DevTools=true, see tools/dev-cycle.ps1): a look at the game's item data as it really
/// is, written into the mod's folder. Reflection on purpose: it must show what is there, not what the mod
/// expects to be there.
/// </summary>
static class Dev
{
    /// <summary>"items-dump.txt": every item with its capabilities and bank tags, and the game's lists of types.</summary>
    public static void DumpItems(Game game, ItemIndex? index, ItemLines lines, string folder, Action<string> log)
    {
        var sb = new StringBuilder();
        foreach (var (type, registry) in game.Data.Database.DataClassRegistries.OrderBy(r => r.Key.FullName))
        {
            if (registry.GetType().GetMethod("GetAllObjects", Type.EmptyTypes)?.Invoke(registry, null) is not IEnumerable objects) continue;
            var all = objects.Cast<object>().ToList();
            sb.AppendLine($"== registry {type.FullName}: {all.Count}");
            if (type == typeof(Item) || all.Count > 60) continue;
            foreach (object o in all) sb.AppendLine("   " + Short(o) + "  " + Props(o, 1));
        }

        sb.AppendLine().AppendLine("== items");
        foreach (Item item in game.Data.GetDataRegistryOutput<Item>().GetAllObjects())
        {
            sb.AppendLine($"{item.Id} | {item.DisplayName.GetPluralText(1)} | sells {item.SellsFor?.Quantity} {item.SellsFor?.Currency?.Id} | ignoreCompletion {item.IgnoreCompletion}");
            sb.AppendLine("   desc: " + item.Description?.Text?.Replace("\n", "\\n"));
            sb.AppendLine("   tags: " + string.Join(", ", item.BankTags.Select(t => $"{t.BankTag.DisplayName.Text}{(t.IsProduct ? " P" : "")}{(t.IsIngredient ? " I" : "")}")));
            foreach (ItemCapability capability in item.Capabilities)
                sb.AppendLine($"   cap {capability.GetType().Name}: {Props(capability, 2)}");
            if (index?.For(item) is { } entry)
            {
                sb.AppendLine("   found: " + string.Join(" | ", entry.Sources.Select(lines.Describe).Select(l => $"[{l.Group}] {l.Name} {l.Quantity} {ItemLines.Percent(l.Chance)} <{l.Ref.Owner.GetType().Name}:{l.Ref.Path}>")));
                sb.AppendLine("   used:  " + string.Join(" | ", entry.Uses.Select(lines.Describe).Select(l => $"[{l.Group}] {l.Name} {l.Quantity} <{l.Ref.Owner.GetType().Name}:{l.Ref.Path}>")));
            }
        }
        File.WriteAllText(Path.Combine(folder, "items-dump.txt"), sb.ToString());
        log("Dev: wrote items-dump.txt");
    }

    /// <summary>"icons-dump.txt": the names of the game's icons, and what the game calls its capabilities.</summary>
    public static void DumpIcons(string folder, Action<string> log)
    {
        var sb = new StringBuilder();
        foreach (Type type in typeof(Item).Assembly.GetTypes().Where(t => typeof(ItemCapability).IsAssignableFrom(t) && !t.IsAbstract))
        {
            string? Static(string name) { try { return type.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string; } catch { return "?"; } }
            sb.AppendLine($"capability {type.Name}: id={Static("TypeId")} name={Static("TypeDisplayName")} description={Static("TypeDescription")}");
        }
        foreach (string dir in new[] { "res://Assets/Icons", "res://Assets" })
        {
            sb.AppendLine("== " + dir);
            using DirAccess? access = DirAccess.Open(dir);
            if (access == null) { sb.AppendLine("(cannot be listed)"); continue; }
            sb.AppendLine(string.Join(", ", access.GetFiles().Select(f => f.Replace(".import", "").Replace(".remap", "")).Distinct().OrderBy(f => f)));
            sb.AppendLine("dirs: " + string.Join(", ", access.GetDirectories()));
        }
        File.WriteAllText(Path.Combine(folder, "icons-dump.txt"), sb.ToString());
        log("Dev: wrote icons-dump.txt");
    }

    static string Short(object? o)
    {
        if (o == null) return "null";
        if (o is string or bool || o.GetType().IsPrimitive || o.GetType().IsEnum) return o.ToString()!;
        object? name = o.GetType().GetProperty("DisplayName")?.GetValue(o);
        string? text = name switch { IPluralString p => p.GetPluralText(1), ILocalizedString l => l.Text, string s => s, _ => null };
        string? id = (o as INamespacedObject)?.Id;
        if (text != null || id != null) return $"{text}<{id}>";
        return o.GetType().Name;
    }

    static string Props(object o, int depth)
    {
        var parts = new List<string>();
        foreach (PropertyInfo p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || p.Name is "Id" or "LocalId" or "Namespace" or "DisplayName" or "SupportsScoreSearch") continue;
            if (typeof(GodotObject).IsAssignableFrom(p.PropertyType)) continue;
            object? v;
            try { v = p.GetValue(o); } catch { continue; }
            if (v == null) continue;
            if (v is string or bool or ILocalizedString || v.GetType().IsPrimitive || v.GetType().IsEnum) { parts.Add($"{p.Name}={(v is ILocalizedString ls ? ls.Text : v)}"); continue; }
            if (v is IEnumerable list)
            {
                var items = list.Cast<object>().Take(12).Select(e => Short(e) is var s && s == e.GetType().Name && depth > 0 ? "{" + Props(e, depth - 1) + "}" : s).ToList();
                if (items.Count > 0) parts.Add($"{p.Name}=[{string.Join("; ", items)}]");
                continue;
            }
            string shown = Short(v);
            parts.Add(shown == v.GetType().Name && depth > 0 ? $"{p.Name}={{{Props(v, depth - 1)}}}" : $"{p.Name}={shown}");
        }
        return string.Join(", ", parts);
    }
}
#endif
