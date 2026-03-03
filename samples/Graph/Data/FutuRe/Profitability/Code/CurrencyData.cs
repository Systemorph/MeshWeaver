// <meshweaver>
// Id: CurrencyData
// DisplayName: Currency Data
// </meshweaver>

/// <summary>
/// Static reference data for currencies.
/// </summary>
public static class CurrencyData
{
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

    /// <summary>
    /// Looks up a currency by ISO code. Returns USD as fallback.
    /// </summary>
    public static Currency GetById(string? id) =>
        All.FirstOrDefault(c => c.Id == id) ?? USD;
}
