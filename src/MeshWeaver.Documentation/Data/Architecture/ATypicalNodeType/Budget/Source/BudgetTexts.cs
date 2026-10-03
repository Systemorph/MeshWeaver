// <meshweaver>
// Id: BudgetTexts
// DisplayName: Budget Texts — the view's chrome in English and German
// </meshweaver>
#nullable enable
using System;
using System.Globalization;

/// <summary>
/// The CHROME of the budget views in the VIEWER's language — a module text table, so the strings
/// ship, translate and compile with the NodeType instead of waiting on a core catalog roll.
/// Every member is <c>required</c>, which makes English/German parity a COMPILE error rather than
/// a missing string found in production.
/// </summary>
public sealed record BudgetTexts
{
    /// <summary>The resolved language tag these texts are written in.</summary>
    public required string Locale { get; init; }

    /// <summary>The label of the planned amount.</summary>
    public required string Planned { get; init; }

    /// <summary>The label of the spent amount.</summary>
    public required string Spent { get; init; }

    /// <summary>The label of the remaining amount.</summary>
    public required string Remaining { get; init; }

    /// <summary>The label of the utilisation bar.</summary>
    public required string Utilisation { get; init; }

    /// <summary>The label in front of the health.</summary>
    public required string Health { get; init; }

    /// <summary>Shown when the node carries no budget content yet.</summary>
    public required string NoBudget { get; init; }

    /// <summary><see cref="BudgetHealth.OnTrack"/>, spelled for a reader.</summary>
    public required string OnTrack { get; init; }

    /// <summary><see cref="BudgetHealth.AtRisk"/>, spelled for a reader.</summary>
    public required string AtRisk { get; init; }

    /// <summary><see cref="BudgetHealth.Overspent"/>, spelled for a reader.</summary>
    public required string Overspent { get; init; }

    /// <summary><see cref="BudgetHealth.Unplanned"/>, spelled for a reader.</summary>
    public required string Unplanned { get; init; }

    /// <summary>English.</summary>
    public static readonly BudgetTexts English = new()
    {
        Locale = "en",
        Planned = "Planned",
        Spent = "Spent",
        Remaining = "Remaining",
        Utilisation = "Utilisation",
        Health = "Health",
        NoBudget = "No budget has been recorded on this node yet.",
        OnTrack = "On track",
        AtRisk = "At risk",
        Overspent = "Overspent",
        Unplanned = "Nothing planned",
    };

    /// <summary>German.</summary>
    public static readonly BudgetTexts German = new()
    {
        Locale = "de",
        Planned = "Geplant",
        Spent = "Ausgegeben",
        Remaining = "Verbleibend",
        Utilisation = "Ausschöpfung",
        Health = "Zustand",
        NoBudget = "Auf diesem Knoten ist noch kein Budget erfasst.",
        OnTrack = "Im Plan",
        AtRisk = "Gefährdet",
        Overspent = "Überschritten",
        Unplanned = "Nichts geplant",
    };

    /// <summary>
    /// The table for a viewer. The tag comes from the viewer's access context
    /// (<c>host.ViewerLocale()</c>) — never from <c>CultureInfo.CurrentUICulture</c>, which does
    /// not survive the hub's scheduler hops.
    /// </summary>
    /// <param name="locale">The viewer's resolved language tag; anything but German reads English.</param>
    /// <returns>The texts.</returns>
    public static BudgetTexts For(string? locale) =>
        locale is not null && locale.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? German : English;

    /// <summary>The culture amounts are formatted in — derived from <see cref="Locale"/>, never ambient.</summary>
    public CultureInfo Culture => CultureInfo.GetCultureInfo(Locale);

    /// <summary>
    /// A health value spelled for the reader. An UNKNOWN value (the vocabulary is open) is shown
    /// as itself — never mapped onto one of the known labels.
    /// </summary>
    /// <param name="health">A <see cref="BudgetHealth"/> value, or any other string.</param>
    /// <returns>The label.</returns>
    public string HealthLabel(string health) => health switch
    {
        BudgetHealth.OnTrack => OnTrack,
        BudgetHealth.AtRisk => AtRisk,
        BudgetHealth.Overspent => Overspent,
        BudgetHealth.Unplanned => Unplanned,
        _ => health,
    };
}
