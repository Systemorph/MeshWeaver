// <meshweaver>
// Id: CountryData
// DisplayName: Country Data
// </meshweaver>

/// <summary>
/// Static reference data for countries.
/// </summary>
public static class CountryData
{
    public static readonly Country UnitedStates = new()
    {
        Id = "US", Name = "United States", Alpha3Code = "USA", Region = "Americas", Order = 0
    };

    public static readonly Country UnitedKingdom = new()
    {
        Id = "GB", Name = "United Kingdom", Alpha3Code = "GBR", Region = "EMEA", Order = 1
    };

    public static readonly Country Germany = new()
    {
        Id = "DE", Name = "Germany", Alpha3Code = "DEU", Region = "EMEA", Order = 2
    };

    public static readonly Country France = new()
    {
        Id = "FR", Name = "France", Alpha3Code = "FRA", Region = "EMEA", Order = 3
    };

    public static readonly Country Switzerland = new()
    {
        Id = "CH", Name = "Switzerland", Alpha3Code = "CHE", Region = "EMEA", Order = 4
    };

    public static readonly Country Japan = new()
    {
        Id = "JP", Name = "Japan", Alpha3Code = "JPN", Region = "Asia", Order = 5
    };

    public static readonly Country Brazil = new()
    {
        Id = "BR", Name = "Brazil", Alpha3Code = "BRA", Region = "Americas", Order = 6
    };

    public static readonly Country Australia = new()
    {
        Id = "AU", Name = "Australia", Alpha3Code = "AUS", Region = "Asia-Pacific", Order = 7
    };

    public static readonly Country[] All =
    [
        UnitedStates, UnitedKingdom, Germany, France,
        Switzerland, Japan, Brazil, Australia
    ];

    /// <summary>
    /// Looks up a country by ISO alpha-2 code. Returns UnitedStates as fallback.
    /// </summary>
    public static Country GetById(string? id) =>
        All.FirstOrDefault(c => c.Id == id) ?? UnitedStates;
}
