using System;
using System.Collections.Generic;
using GamesByMalcs.Melvor2.Data.Items;
using Godot;
using MelvorMods.Shared;

namespace ThievingOverview;

/// <summary>Which way a number is better. The first click on its column puts the best targets on top.</summary>
enum Better { More, Less, Neither }

/// <summary>
/// One number of the list, in the shape of the Combat Simulator's columns (CombatSim's SimColumn): its short
/// title, what it is, its width at full size, the number it sorts by (null: the target has no such number, it
/// goes last whichever way the list is sorted), how it is written and coloured, and when it shows "-".
/// </summary>
/// <param name="Marks">The best of the column is marked in gold, as Recipe Rates marks the best recipe.</param>
/// <param name="Dash">When the column shows "-", as the simulator's columns say it (SimColumn.Dash).</param>
sealed record Column(string Key, string Title, string Tip, float Width, Func<TargetRates, double?> Value,
    Func<TargetRates, string> Text, Func<TargetRates, Color> Tint, Better Better, bool Marks = true, string Dash = "")
{
    /// <summary>What the column's title says under the pointer: what the number is, which way is better, and its "-".</summary>
    public string Help => Tip + "\n" + Better switch
    {
        Better.More => "More is better",
        Better.Less => "Less is better",
        _ => "Neither more nor less is better",
    } + (Dash == "" ? "" : "\nShows - " + Dash);
}

static class Columns
{
    public const string NameKey = "name", ItemKey = "item";

    const string Unproven = "until the Thieving screen has shown a success chance (choose a place there)";
    const string NotCounted = "for a heist, and where the items are not counted: the line's tooltip says why";

    // "-" for no number, dim; the number in white otherwise, as every number of the Combat Simulator's and Fishing
    // Rates' lists (SimColumns.Number); the best of the column is gold (OverviewView.Update).
    static Column Number(string key, string title, string tip, float width, Func<TargetRates, double?> value, Func<double, string> text,
        Better better, string dash)
    {
        double? Known(TargetRates r) => value(r) is { } v && double.IsFinite(v) ? v : null;
        return new(key, title, tip, width, Known, r => Known(r) is { } v ? text(v) : "-",
            r => Known(r) != null ? GameStyle.TextWhite : GameStyle.TextDim, better, Dash: dash);
    }

    /// <summary>
    /// The columns of the list, from left to right; in Best for, the chosen item's first. Each is as wide as the
    /// Combat Simulator's column of the same kind (SimColumns.All): a percent as its Low HP (62), a time as its Kill
    /// time and Run time (66), a number per hour as its XP and Gold (66), Food (52) and Safe (58) as its own.
    /// </summary>
    public static List<Column> For(Item? item)
    {
        var columns = new List<Column>();
        if (item != null)
            columns.Add(Number(ItemKey, item.DisplayName.GetPluralText(1),
                $"{item.DisplayName.GetPluralText(2)} per hour from this target, its common and rare rewards together. Under one an hour, " +
                "the time between two, as the Rare column writes it", ItemWidth, r => r.PerHourOf(item), TargetRates.Rate, Better.More, NotCounted));
        columns.AddRange(new[]
        {
            Number("success", "Success", "The chance that one try succeeds, the Thieving screen's own: your stealth against the target's perception",
                62, r => r.SuccessKnown ? r.Success : null, TargetRates.Percent, Better.More, Unproven),
            Number("time", "Time", "Seconds per try: the try itself and, on average, the stun after a failure", 66,
                r => r.Valid ? r.SecondsPerTry : null, TargetRates.Seconds, Better.Less, Unproven),
            Number("xp", "XP", "Thieving XP per hour", 66, r => r.XpPerHour, TargetRates.Fmt, Better.More, "for a heist, and " + Unproven),
            Number("gold", "Gold", "Coins plus what the items sell for, per hour", 66, r => r.GoldPerHour, TargetRates.Fmt, Better.More, NotCounted),
            Number("rare", "Rare", "How long until a rare item, on average: the time between two tries that give one", 66,
                r => r.RarePerHour is > 0 and var perHour ? 3600 / perHour : null, TargetRates.Clock, Better.Less,
                "for a target with no rare rewards, for a heist, and where the items are not counted"),
            Number("food", "Food", "Food eaten per hour with the food chosen for Thieving: every failure that hurts counted at the target's " +
                "biggest hit, less the hits you avoid and the food you keep", 52,
                r => r.FoodPerHour, TargetRates.Fmt, Better.Less, "without food chosen for Thieving, with food that heals nothing, and " + Unproven),
            new("safe", "Safe", "Safe: no failure can take you from the point where auto eat steps in to 0 HP, with the food chosen " +
                "for Thieving, as the Combat Simulator's Safe column says for a fight", 58,
                r => r.Safe is { } safe ? safe ? 1 : 0 : null,
                r => r.Safe is { } safe ? safe ? "Safe" : "Risky" : "-",
                r => r.Safe is { } safe ? safe ? GameStyle.Good : GameStyle.Bad : GameStyle.TextDim, Better.More, Marks: false,
                Dash: "when where auto eat steps in cannot be told"),
        });
        return columns;
    }

    /// <summary>The Best for item's column: as wide as a number per hour (66).</summary>
    public const float ItemWidth = 66;
}
