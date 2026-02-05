// <meshweaver>
// Id: Currency
// DisplayName: Currency Reference Data
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;


/// <summary>
/// Currency dimension for monetary values.
/// </summary>
public record Currency : INamed
{
    /// <summary>
    /// Currency code (ISO 4217).
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Full currency name.
    /// </summary>
    [Required]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Currency symbol.
    /// </summary>
    public string? Symbol { get; init; }

    /// <summary>
    /// Number of decimal places typically used.
    /// </summary>
    public int DecimalPlaces { get; init; } = 2;

    string INamed.DisplayName => Name;

    public static readonly Currency USD = new()
    {
        Id = "USD",
        Name = "US Dollar",
        Symbol = "$",
        DecimalPlaces = 2
    };

    public static readonly Currency EUR = new()
    {
        Id = "EUR",
        Name = "Euro",
        Symbol = "\u20ac",
        DecimalPlaces = 2
    };

    public static readonly Currency GBP = new()
    {
        Id = "GBP",
        Name = "British Pound",
        Symbol = "\u00a3",
        DecimalPlaces = 2
    };

    public static readonly Currency JPY = new()
    {
        Id = "JPY",
        Name = "Japanese Yen",
        Symbol = "\u00a5",
        DecimalPlaces = 0
    };

    public static readonly Currency CHF = new()
    {
        Id = "CHF",
        Name = "Swiss Franc",
        Symbol = "CHF",
        DecimalPlaces = 2
    };

    public static readonly Currency AUD = new()
    {
        Id = "AUD",
        Name = "Australian Dollar",
        Symbol = "A$",
        DecimalPlaces = 2
    };

    public static readonly Currency CAD = new()
    {
        Id = "CAD",
        Name = "Canadian Dollar",
        Symbol = "C$",
        DecimalPlaces = 2
    };

    public static readonly Currency[] All = [USD, EUR, GBP, JPY, CHF, AUD, CAD];

    public static Currency GetById(string? id) =>
        All.FirstOrDefault(c => c.Id == id) ?? USD;
}
