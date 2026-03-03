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
    [Key]
    public string Id { get; init; } = string.Empty;

    [Required]
    public string Name { get; init; } = string.Empty;

    public string? Symbol { get; init; }

    public int DecimalPlaces { get; init; } = 2;

    public int Order { get; init; }

    string INamed.DisplayName => Name;

    public static readonly Currency USD = new()
    {
        Id = "USD", Name = "US Dollar", Symbol = "$", DecimalPlaces = 2, Order = 0
    };

    public static readonly Currency EUR = new()
    {
        Id = "EUR", Name = "Euro", Symbol = "\u20ac", DecimalPlaces = 2, Order = 1
    };

    public static readonly Currency GBP = new()
    {
        Id = "GBP", Name = "British Pound", Symbol = "\u00a3", DecimalPlaces = 2, Order = 2
    };

    public static readonly Currency CHF = new()
    {
        Id = "CHF", Name = "Swiss Franc", Symbol = "CHF", DecimalPlaces = 2, Order = 3
    };

    public static readonly Currency JPY = new()
    {
        Id = "JPY", Name = "Japanese Yen", Symbol = "\u00a5", DecimalPlaces = 0, Order = 4
    };

    public static readonly Currency[] All = [USD, EUR, GBP, CHF, JPY];

    public static Currency GetById(string? id) =>
        All.FirstOrDefault(c => c.Id == id) ?? USD;
}
