using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace MelvorMods.Shared;

/// <summary>
/// What a scene of the game gives its nodes, read from the scene file without making the scene: the values the game's
/// code is handed through the properties the scene sets (a colour, a texture, another scene, a node of the same scene).
/// So a mod can use the game's own values for a screen the player has not opened yet: the Item Encyclopedia reads the
/// bank's "View Item Stats" button through it (BankButton), the Combat Simulator the look of the game's equipment slots
/// (CombatSimMod.FilledLook). A step that finds nothing throws an InvalidOperationException that says what was missing,
/// for the caller's one line in the log.
/// </summary>
public static class SceneData
{
    /// <summary>A node of a scene: the scene's data and the node's number in it.</summary>
    public readonly record struct Spot(SceneState State, int Node);

    /// <summary>The root node of the game's scene at this path.</summary>
    public static Spot Root(string path) =>
        Root(GameStyle.Load<PackedScene>(path) ?? throw new InvalidOperationException($"the scene '{path}' was not found"));

    public static Spot Root(PackedScene scene) => new(scene.GetState(), 0);

    /// <summary>A property as the node has it: set on the node itself, or else in the scene it is made from.</summary>
    public static bool TryProperty(Spot spot, string name, out Variant value) => TryProperty(spot, name, out value, out _);

    /// <summary>The node a property points to (a node the game's code is handed by the scene, stored as a path).</summary>
    public static Spot Follow(Spot spot, string property)
    {
        if (!TryProperty(spot, property, out Variant value, out Spot owner) || value.VariantType != Variant.Type.NodePath)
            throw new InvalidOperationException($"the scene gives no {property}");
        // The path starts at the node that holds it, in that node's own scene.
        List<string> names = Names(owner.State.GetNodePath(owner.Node));
        foreach (string step in Names(value.AsNodePath()))
        {
            if (step != "..") names.Add(step);
            else if (names.Count > 0) names.RemoveAt(names.Count - 1);
            else throw new InvalidOperationException($"{property} points above its scene");
        }
        return Find(owner.State, names) ?? throw new InvalidOperationException($"{property} ({value.AsNodePath()}) is not in the scene");
    }

    /// <summary>The root of the scene a property holds (a PackedScene the game's code makes nodes from).</summary>
    public static Spot Scene(Spot spot, string property) =>
        TryProperty(spot, property, out Variant value) && value.VariantType == Variant.Type.Object && value.AsGodotObject() is PackedScene scene
            ? Root(scene) : throw new InvalidOperationException($"the scene gives no {property}");

    /// <summary>Every property the scenes give the node: those of the scene it is made from first, its own over them.</summary>
    public static Dictionary<string, Variant> Properties(Spot spot)
    {
        var chain = new List<Spot>();
        for (Spot? at = spot; at is { } s; at = Inner(s)) chain.Add(s);
        var all = new Dictionary<string, Variant>(StringComparer.Ordinal);
        for (int c = chain.Count - 1; c >= 0; c--)
            for (int p = 0; p < chain[c].State.GetNodePropertyCount(chain[c].Node); p++)
                all[chain[c].State.GetNodePropertyName(chain[c].Node, p).ToString()] = chain[c].State.GetNodePropertyValue(chain[c].Node, p);
        return all;
    }

    // The scene a node is made from, at its root; null for a node made in its own scene.
    static Spot? Inner(Spot spot) => spot.State.GetNodeInstance(spot.Node) is { } scene ? new Spot(scene.GetState(), 0) : null;

    static bool TryProperty(Spot spot, string name, out Variant value, out Spot owner)
    {
        for (Spot? at = spot; at is { } s; at = Inner(s))
            for (int p = 0; p < s.State.GetNodePropertyCount(s.Node); p++)
                if (s.State.GetNodePropertyName(s.Node, p).ToString() == name)
                {
                    value = s.State.GetNodePropertyValue(s.Node, p);
                    owner = s;
                    return true;
                }
        value = default;
        owner = spot;
        return false;
    }

    static List<string> Names(NodePath path)
    {
        var names = new List<string>();
        for (int i = 0; i < path.GetNameCount(); i++)
            if (path.GetName(i).ToString() is var name && name != ".") names.Add(name);
        return names;
    }

    // The node at a path from a scene's root. A path that goes into a node made from another scene goes on in that
    // scene, from the deepest such node on the way.
    static Spot? Find(SceneState state, List<string> names)
    {
        int deepest = -1, depth = -1;
        for (int i = 0; i < state.GetNodeCount(); i++)
        {
            List<string> at = Names(state.GetNodePath(i));
            if (at.Count > names.Count || !at.SequenceEqual(names.Take(at.Count))) continue;
            if (at.Count == names.Count) return new Spot(state, i);
            if (state.GetNodeInstance(i) != null && at.Count > depth) { deepest = i; depth = at.Count; }
        }
        return deepest < 0 ? null : Find(state.GetNodeInstance(deepest).GetState(), names.Skip(depth).ToList());
    }
}
