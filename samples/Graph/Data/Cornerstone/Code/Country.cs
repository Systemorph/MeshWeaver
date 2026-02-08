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

    /// <summary>
    /// Display order in lists.
    /// </summary>
    public int Order { get; init; }

    string INamed.DisplayName => Name;

    public static readonly Country UnitedStates = new()
    {
        Id = "US",
        Name = "United States",
        Alpha3Code = "USA",
        Region = "North America",
        Order = 0
    };

    public static readonly Country UnitedKingdom = new()
    {
        Id = "GB",
        Name = "United Kingdom",
        Alpha3Code = "GBR",
        Region = "Europe",
        Order = 1
    };

    public static readonly Country Germany = new()
    {
        Id = "DE",
        Name = "Germany",
        Alpha3Code = "DEU",
        Region = "Europe",
        Order = 2
    };

    public static readonly Country France = new()
    {
        Id = "FR",
        Name = "France",
        Alpha3Code = "FRA",
        Region = "Europe",
        Order = 3
    };

    public static readonly Country Japan = new()
    {
        Id = "JP",
        Name = "Japan",
        Alpha3Code = "JPN",
        Region = "Asia",
        Order = 4
    };

    public static readonly Country China = new()
    {
        Id = "CN",
        Name = "China",
        Alpha3Code = "CHN",
        Region = "Asia",
        Order = 5
    };

    public static readonly Country Australia = new()
    {
        Id = "AU",
        Name = "Australia",
        Alpha3Code = "AUS",
        Region = "Oceania",
        Order = 6
    };

    public static readonly Country Canada = new()
    {
        Id = "CA",
        Name = "Canada",
        Alpha3Code = "CAN",
        Region = "North America",
        Order = 7
    };

    public static readonly Country Switzerland = new()
    {
        Id = "CH",
        Name = "Switzerland",
        Alpha3Code = "CHE",
        Region = "Europe",
        Order = 8
    };

    public static readonly Country Singapore = new()
    {
        Id = "SG",
        Name = "Singapore",
        Alpha3Code = "SGP",
        Region = "Asia",
        Order = 9
    };

    public static readonly Country Ireland = new()
    {
        Id = "IE",
        Name = "Ireland",
        Alpha3Code = "IRL",
        Region = "Europe",
        Order = 10
    };

    public static readonly Country India = new()
    {
        Id = "IN",
        Name = "India",
        Alpha3Code = "IND",
        Region = "Asia",
        Order = 11
    };

    public static readonly Country[] All =
    [
        UnitedStates, UnitedKingdom, Germany, France, Japan,
        China, Australia, Canada, Switzerland, Singapore, Ireland, India
    ];

    public static Country GetById(string? id) =>
        All.FirstOrDefault(c => c.Id == id) ?? UnitedStates;
}
