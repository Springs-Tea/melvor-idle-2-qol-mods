using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Data;
using Godot;

namespace FiremakingRates;

/// <summary>
/// The pictures of the logs as the list shows them: the game's own, and never the grey stand-in the game draws where a
/// picture is missing from its data. A log without a picture of its own shows none. Copied from Fishing Rates
/// (mods/FishingRates/GamePictures.cs), as Woodcutting Rates copied it, which tells the stand-in apart the way Skill
/// Bonuses does (mods/SkillBonuses/Pictures.cs, IsStandIn). One per character.
/// </summary>
sealed class GamePictures(DataManager data, Action<string> report)
{
    // The picture the game's data manager gives everything whose own picture is not there. Private: one that is gone
    // after a game update throws when it is first used; the lines then show no pictures at all, as Skill Bonuses'
    // window does, since the stand-in can no longer be told from a log.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_DefaultTexture")]
    static extern Texture2D? DefaultTexture(DataManager data);

    readonly Dictionary<Texture2D, bool> _known = new(ReferenceEqualityComparer.Instance);
    Texture2D? _standIn;
    bool _read, _failed;

    /// <summary>A picture of the game to show, or null when it is the grey stand-in.</summary>
    public Texture2D? Shown(Texture2D? picture)
    {
        if (_failed || picture == null) return null;
        try { return IsStandIn(picture) ? null : picture; }
        catch (Exception e)
        {
            _failed = true;
            report("The game's grey picture for a missing one could not be read (the game was updated?); the lines show no log pictures. " + e.Message);
            return null;
        }
    }

    /// <summary>The picture is the game's grey stand-in for a missing one.</summary>
    public bool IsStandIn(Texture2D picture)
    {
        if (StandIn() is not { } standIn) return false;
        if (_known.TryGetValue(picture, out bool known)) return known;
        bool same = ReferenceEquals(picture, standIn) || (picture.ResourcePath is { Length: > 0 } path && path == standIn.ResourcePath);
        _known[picture] = same;
        return same;
    }

    // In a method of its own: a member that is gone in a game version fails when the method is first run.
    [MethodImpl(MethodImplOptions.NoInlining)]
    Texture2D? StandIn()
    {
        if (!_read)
        {
            _standIn = DefaultTexture(data);
            _read = true;
        }
        return _standIn;
    }

#if DEVTOOLS
    /// <summary>Development: the game's grey picture for a missing one, as it was read.</summary>
    public Texture2D? DevStandIn => StandIn();
#endif
}
