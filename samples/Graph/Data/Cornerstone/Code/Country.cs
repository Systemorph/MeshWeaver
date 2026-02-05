// <meshweaver>
// Id: Country
// DisplayName: Country Reference Data
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;


/// <summary>
/// Country dimension for geographic classification.
/// </summary>
public record Country : INamed
{
    /// <summary>
    /// Country code (typically ISO 3166-1 alpha-2).
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Full country name.
    /// </summary>
    [Required]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// ISO 3166-1 alpha-3 code.
    /// </summary>
    public string? Alpha3Code { get; init; }

    /// <summary>
    /// Geographic region.
    /// </summary>
    public string? Region { get; init; }

    string INamed.DisplayName => Name;

    public static readonly Country UnitedStates = new()
    {
        Id = "US",
        Name = "United States",
        Alpha3Code = "USA",
        Region = "North America"
    };

    public static readonly Country UnitedKingdom = new()
    {
        Id = "GB",
        Name = "United Kingdom",
        Alpha3Code = "GBR",
        Region = "Europe"
    };

    public static readonly Country Germany = new()
    {
        Id = "DE",
        Name = "Germany",
        Alpha3Code = "DEU",
        Region = "Europe"
    };

    public static readonly Country France = new()
    {
        Id = "FR",
        Name = "France",
        Alpha3Code = "FRA",
        Region = "Europe"
    };

    public static readonly Country Japan = new()
    {
        Id = "JP",
        Name = "Japan",
        Alpha3Code = "JPN",
        Region = "Asia"
    };

    public static readonly Country China = new()
    {
        Id = "CN",
        Name = "China",
        Alpha3Code = "CHN",
        Region = "Asia"
    };

    public static readonly Country Australia = new()
    {
        Id = "AU",
        Name = "Australia",
        Alpha3Code = "AUS",
        Region = "Oceania"
    };

    public static readonly Country Canada = new()
    {
        Id = "CA",
        Name = "Canada",
        Alpha3Code = "CAN",
        Region = "North America"
    };

    public static readonly Country Switzerland = new()
    {
        Id = "CH",
        Name = "Switzerland",
        Alpha3Code = "CHE",
        Region = "Europe"
    };

    public static readonly Country Singapore = new()
    {
        Id = "SG",
        Name = "Singapore",
        Alpha3Code = "SGP",
        Region = "Asia"
    };

    public static readonly Country[] All =
    [
        UnitedStates, UnitedKingdom, Germany, France, Japan,
        China, Australia, Canada, Switzerland, Singapore
    ];

    public static Country GetById(string? id) =>
        All.FirstOrDefault(c => c.Id == id) ?? UnitedStates;
}
