// <meshweaver>
// Id: Currency
// DisplayName: Currency Reference Data
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Currency dimension for monetary values in profitability reporting.
/// </summary>
public record Currency : INamed
{
    /// <summary>
    /// ISO 4217 currency code (e.g. USD, EUR).
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Full currency name (e.g. US Dollar).
    /// </summary>
    [Required]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Currency symbol for display (e.g. $, &euro;).
    /// </summary>
    public string? Symbol { get; init; }

    /// <summary>
    /// Number of decimal places for formatting amounts.
    /// </summary>
    public int DecimalPlaces { get; init; } = 2;

    /// <summary>
    /// Sort order for display.
    /// </summary>
    public int Order { get; init; }

    /// <inheritdoc />
    string INamed.DisplayName => Name;
}
