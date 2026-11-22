using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.MasteryGuild;
using GamesByMalcs.Melvor2.Components.Pets;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Agility;
using GamesByMalcs.Melvor2.Entities.Firemaking;
using GamesByMalcs.Melvor2.Entities.Ranching;
using GamesByMalcs.Melvor2.Entities.Shop;
using GamesByMalcs.Melvor2.Entities.Thieving;
using GamesByMalcs.Melvor2.Entities.Woodcutting;
using Godot;

namespace SkillBonuses;

/// <summary>
/// The pictures of the window: for each source of a bonus the one the game has for it, and never the grey
/// stand-in the game draws where a picture is missing from its data. Where the game has none, the window leaves
/// the place of the picture empty. One per window; what it has found is kept.
/// </summary>
sealed class Pictures(Game game, Action<string> log)
{
    // The picture the game's data manager gives everything whose own picture is not there: the grey box.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_DefaultTexture")]
    static extern Texture2D? StandIn(DataManager data);

    readonly Dictionary<INamedObject, Texture2D?> _known = new(ReferenceEqualityComparer.Instance);
    Dictionary<MasteryGuildBonus, Texture2D?>? _guilds;
    Texture2D? _standIn;
    bool _standInRead;
    static bool s_failed;

    /// <summary>The picture of a source of a bonus, or null where the game has none.</summary>
    public Texture2D? Of(INamedObject source)
    {
        if (s_failed) return null;
        if (_known.TryGetValue(source, out Texture2D? known)) return known;
        (Texture2D? picture, string why) found;
        try { found = Find(source); }
        catch (Exception e) { Fail(e); return null; }
        _known[source] = found.picture;
#if DEVTOOLS
        log($"Dev: picture of '{source.DisplayName.Text}' [{source.GetType().Name}]: {File(found.picture)} ({found.why}).");
#endif
        return found.picture;
    }

    /// <summary>A picture the game shows (a stat's, a recipe's), or null when it is the game's grey stand-in.</summary>
    public Texture2D? Shown(Texture2D? picture)
    {
        if (s_failed || picture == null) return null;
        try { return IsStandIn(picture) ? null : picture; }
        catch (Exception e) { Fail(e); return null; }
    }

    void Fail(Exception e)
    {
        s_failed = true;
        log("The pictures of the bonuses could not be read (the game was updated?); the names are shown without them. " + e.Message);
    }

    // In methods of their own: a member that is gone in a game version fails when the method is first run, and
    // is caught above.
    [MethodImpl(MethodImplOptions.NoInlining)]
    (Texture2D?, string) Find(INamedObject source)
    {
        // A rank of a mastery guild ("Woodcutting Rank 3") has no picture of its own. The game's views of a guild
        // show it with its skill's picture (MasteryGuildCompletionBreakdownView.SkillIcon beside the skill's
        // name, MasteryGuildSelectButton.Icon).
        if (source is MasteryGuildBonus bonus)
        {
            Texture2D? skill = GuildPicture(bonus);
            return skill == null ? (null, "a mastery guild rank that no skill's guild lists, or its skill has no picture")
                : IsStandIn(skill) ? (null, "a mastery guild rank, and its skill's picture is the grey stand-in")
                : (skill, "a mastery guild rank: its skill's picture");
        }
        if (Own(source) is not { } own) return (null, "the game has no picture for it");
        if (IsStandIn(own)) return (null, "its own picture is the grey stand-in " + File(own));
        return (own, "its own");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static Texture2D? Own(INamedObject source) => source switch
    {
        IDescribedObject described => described.Icon,   // items, constellations, prayers, recipes, courses
        Pet pet => pet.Icon,
        ShopPurchase purchase => purchase.Icon,
        AgilityTechnique technique => technique.Icon,
        FiremakingBonfire bonfire => bonfire.Icon,
        RanchingEnclosureUpgrade upgrade => upgrade.Icon,
        OvergrowthEffect effect => effect.Icon,
        ThievingDistraction distraction => distraction.LinkedShopPurchase?.Icon,
        _ => null,
    };

    // Which skill's mastery guild a rank belongs to, asked from the guilds themselves once per window.
    [MethodImpl(MethodImplOptions.NoInlining)]
    Texture2D? GuildPicture(MasteryGuildBonus bonus)
    {
        if (_guilds == null)
        {
            var guilds = new Dictionary<MasteryGuildBonus, Texture2D?>(ReferenceEqualityComparer.Instance);
            foreach (IMasteryGuildEntity skill in game.MasteryGuildEntities)
                if (skill.MasteryGuild?.Bonuses is { } bonuses)
                    foreach (MasteryGuildBonus rank in bonuses) guilds.TryAdd(rank, skill.Icon);
            _guilds = guilds;
        }
        return _guilds.GetValueOrDefault(bonus);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    bool IsStandIn(Texture2D picture)
    {
        if (!_standInRead)
        {
            _standIn = StandIn(game.Data);
            _standInRead = true;
#if DEVTOOLS
            log($"Dev: the game's grey stand-in for a missing picture is {File(_standIn)}.");
#endif
        }
        if (_standIn == null) return false;
        return ReferenceEquals(picture, _standIn) || (picture.ResourcePath is { Length: > 0 } path && path == _standIn.ResourcePath);
    }

    // For the development log: which picture it is.
    static string File(Texture2D? picture) => picture == null ? "none"
        : picture.ResourcePath is { Length: > 0 } path ? path : $"a {picture.GetType().Name} of {picture.GetWidth()}x{picture.GetHeight()} without a file";
}
