using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

namespace MeshWeaver.Insurance.Domain;

/// <summary>
/// Represents a property risk within an insurance pricing.
/// Contains location details, values, and dimensions for property insurance underwriting.
/// </summary>
public record PropertyRisk
{
    /// <summary>
    /// Unique identifier for the property risk record.
    /// Synonyms: "Plant code", "Plant ID", "Site Code", "Asset ID", "Code".
    /// </summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>
    /// Associated pricing header id.
    /// </summary>
    [Browsable(false)]
    public string? PricingId { get; init; }

    /// <summary>
    /// Source row number in the worksheet (1-based).
    /// </summary>
    public int? SourceRow { get; init; }

    /// <summary>
    /// Originating file name of the import.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// Resolved geocoding for the address.
    /// </summary>
    [Browsable(false)]
    public GeocodedLocation? GeocodedLocation { get; init; }

    /// <summary>
    /// Human-friendly site or facility name.
    /// Synonyms: "Plant Description", "Site Name", "Location Name".
    /// </summary>
    public string? LocationName { get; init; }

    /// <summary>
    /// Country; typically ISO code or name (dimension).
    /// Synonyms: "Country Code", "Country".
    /// </summary>
    [Dimension<Country>]
    public string? Country { get; init; }

    /// <summary>
    /// Street address.
    /// Synonyms: "Property Address", "Address".
    /// </summary>
    public string? Address { get; init; }

    /// <summary>
    /// State/region/province.
    /// Synonyms: "State/Province", "Region".
    /// </summary>
    public string? State { get; init; }

    /// <summary>
    /// County/district.
    /// Synonyms: "District", "County".
    /// </summary>
    public string? County { get; init; }

    /// <summary>
    /// Postal/ZIP code.
    /// Synonyms: "ZIP", "Postcode".
    /// </summary>
    public string? ZipCode { get; init; }

    /// <summary>
    /// City or town.
    /// </summary>
    public string? City { get; init; }

    /// <summary>
    /// Base currency for the risk.
    /// Synonyms: "Currency", "Curr.", "Curr", "CCY".
    /// </summary>
    [Dimension<Currency>]
    public string? Currency { get; init; }

    /// <summary>
    /// Sum insured for buildings.
    /// Synonyms: "Buildings", "Building Value", "TSI Building(s)".
    /// </summary>
    public double TsiBuilding { get; init; }

    /// <summary>
    /// Currency for building TSI when provided per-row.
    /// </summary>
    public string? TsiBuildingCurrency { get; init; }

    /// <summary>
    /// Sum insured for contents.
    /// Synonyms: "Stock", "Fixtures &amp; Fittings", "IT Equipment", "Equipment".
    /// </summary>
    public double TsiContent { get; init; }

    /// <summary>
    /// Currency for contents TSI when provided per-row.
    /// </summary>
    public string? TsiContentCurrency { get; init; }

    /// <summary>
    /// Business Interruption TSI.
    /// Synonyms: "BI", "Business Interruption", "Gross Profit".
    /// </summary>
    public double TsiBi { get; init; }

    /// <summary>
    /// Currency for BI TSI when provided per-row.
    /// </summary>
    public string? TsiBiCurrency { get; init; }

    /// <summary>
    /// Account identifier.
    /// Synonyms: "Account #", "Account No".
    /// </summary>
    public string? AccountNumber { get; init; }

    /// <summary>
    /// Occupancy scheme / taxonomy name.
    /// </summary>
    public string? OccupancyScheme { get; init; }

    /// <summary>
    /// Occupancy code within the scheme.
    /// </summary>
    public string? OccupancyCode { get; init; }

    /// <summary>
    /// Construction classification scheme.
    /// </summary>
    public string? ConstructionScheme { get; init; }

    /// <summary>
    /// Construction code within scheme.
    /// </summary>
    public string? ConstructionCode { get; init; }

    /// <summary>
    /// Original build year (YYYY).
    /// </summary>
    public int? BuildYear { get; init; }

    /// <summary>
    /// Major upgrade/renovation year (YYYY).
    /// </summary>
    public int? UpgradeYear { get; init; }

    /// <summary>
    /// Number of above-ground stories/floors.
    /// </summary>
    public int? NumberOfStories { get; init; }

    /// <summary>
    /// Automatic sprinkler protection present.
    /// </summary>
    public bool? Sprinklers { get; init; }
}

/// <summary>
/// Geocoded location information for a property risk.
/// </summary>
public record GeocodedLocation
{
    /// <summary>
    /// Latitude coordinate.
    /// </summary>
    public double? Latitude { get; init; }

    /// <summary>
    /// Longitude coordinate.
    /// </summary>
    public double? Longitude { get; init; }

    /// <summary>
    /// Formatted address from geocoding service.
    /// </summary>
    public string? FormattedAddress { get; init; }

    /// <summary>
    /// Place ID from geocoding service.
    /// </summary>
    public string? PlaceId { get; init; }

    /// <summary>
    /// Status of geocoding operation.
    /// </summary>
    public string? Status { get; init; }
}
