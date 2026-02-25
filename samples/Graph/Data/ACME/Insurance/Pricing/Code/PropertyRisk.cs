// <meshweaver>
// Id: PropertyRisk
// DisplayName: Property Risk Data Model
// </meshweaver>

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;


/// <summary>
/// Represents a property risk within an insurance pricing.
/// Contains location details, values, and dimensions for property insurance underwriting.
/// </summary>
public record PropertyRisk
{
    /// <summary>
    /// Unique identifier for the property risk record.
    /// Synonyms: Plant code, Plant ID, Site Code, Asset ID, Code.
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Associated pricing header id.
    /// </summary>
    [Browsable(false)]
    public string? PricingId { get; init; }

    /// <summary>
    /// Source row number in the worksheet (1-based).
    /// </summary>
    [DisplayName("Source Row")]
    public int? SourceRow { get; init; }

    /// <summary>
    /// Originating file name of the import.
    /// </summary>
    [DisplayName("Source File")]
    public string? SourceFile { get; init; }

    /// <summary>
    /// Resolved geocoding for the address.
    /// </summary>
    [Browsable(false)]
    public GeocodedLocation? GeocodedLocation { get; init; }

    /// <summary>
    /// Human-friendly site or facility name.
    /// Synonyms: Plant Description, Site Name, Location Name.
    /// </summary>
    [DisplayName("Location Name")]
    [Description("Human-friendly name for the site or facility (e.g., 'Main Manufacturing Plant')")]
    public string? LocationName { get; init; }

    /// <summary>
    /// Country; typically ISO code or name (dimension).
    /// Synonyms: Country Code, Country.
    /// </summary>
    [Dimension<Country>]
    [Description("Country where the property is located")]
    public string? Country { get; init; }

    /// <summary>
    /// Street address.
    /// Synonyms: Property Address, Address.
    /// </summary>
    [Description("Full street address of the property location")]
    public string? Address { get; init; }

    /// <summary>
    /// State/region/province.
    /// Synonyms: State/Province, Region.
    /// </summary>
    [Description("State, province, or region where the property is located")]
    public string? State { get; init; }

    /// <summary>
    /// County/district.
    /// Synonyms: District, County.
    /// </summary>
    [Description("County or administrative district")]
    public string? County { get; init; }

    /// <summary>
    /// Postal/ZIP code.
    /// Synonyms: ZIP, Postcode.
    /// </summary>
    [DisplayName("ZIP Code")]
    [Description("Postal or ZIP code for the property address")]
    public string? ZipCode { get; init; }

    /// <summary>
    /// City or town.
    /// </summary>
    [Description("City or town where the property is located")]
    public string? City { get; init; }

    /// <summary>
    /// Base currency for the risk.
    /// Synonyms: Currency, Curr., Curr, CCY.
    /// </summary>
    [Dimension<Currency>]
    [Description("Base currency for all monetary values at this location")]
    public string? Currency { get; init; }

    /// <summary>
    /// Sum insured for buildings.
    /// Synonyms: Buildings, Building Value, TSI Building(s).
    /// </summary>
    [DisplayName("TSI Building")]
    [Description("Total Sum Insured for building structures at this location")]
    [Range(0, double.MaxValue, ErrorMessage = "TSI Building must be non-negative")]
    public double TsiBuilding { get; init; }

    /// <summary>
    /// Currency for building TSI when provided per-row.
    /// </summary>
    [DisplayName("TSI Building Currency")]
    public string? TsiBuildingCurrency { get; init; }

    /// <summary>
    /// Sum insured for contents.
    /// Synonyms: Stock, Fixtures, Fittings, IT Equipment, Equipment.
    /// </summary>
    [DisplayName("TSI Content")]
    [Description("Total Sum Insured for contents (stock, fixtures, equipment)")]
    [Range(0, double.MaxValue, ErrorMessage = "TSI Content must be non-negative")]
    public double TsiContent { get; init; }

    /// <summary>
    /// Currency for contents TSI when provided per-row.
    /// </summary>
    [DisplayName("TSI Content Currency")]
    public string? TsiContentCurrency { get; init; }

    /// <summary>
    /// Business Interruption TSI.
    /// Synonyms: BI, Business Interruption, Gross Profit.
    /// </summary>
    [DisplayName("TSI BI")]
    [Description("Total Sum Insured for Business Interruption coverage")]
    [Range(0, double.MaxValue, ErrorMessage = "TSI BI must be non-negative")]
    public double TsiBi { get; init; }

    /// <summary>
    /// Currency for BI TSI when provided per-row.
    /// </summary>
    [DisplayName("TSI BI Currency")]
    public string? TsiBiCurrency { get; init; }

    /// <summary>
    /// Account identifier.
    /// Synonyms: Account #, Account No.
    /// </summary>
    [DisplayName("Account Number")]
    public string? AccountNumber { get; init; }

    /// <summary>
    /// Occupancy scheme / taxonomy name.
    /// </summary>
    [DisplayName("Occupancy Scheme")]
    [Description("Occupancy classification scheme (e.g., ISO, AIR, RMS)")]
    public string? OccupancyScheme { get; init; }

    /// <summary>
    /// Occupancy code within the scheme.
    /// </summary>
    [DisplayName("Occupancy Code")]
    [Description("Specific occupancy code within the selected scheme")]
    public string? OccupancyCode { get; init; }

    /// <summary>
    /// Construction classification scheme.
    /// </summary>
    [DisplayName("Construction Scheme")]
    [Description("Construction classification scheme (e.g., ISO, AIR, RMS)")]
    public string? ConstructionScheme { get; init; }

    /// <summary>
    /// Construction code within scheme.
    /// </summary>
    [DisplayName("Construction Code")]
    [Description("Specific construction code within the selected scheme")]
    public string? ConstructionCode { get; init; }

    /// <summary>
    /// Original build year (YYYY).
    /// </summary>
    [DisplayName("Build Year")]
    [Description("Year the building was originally constructed")]
    [Range(1800, 2100, ErrorMessage = "Build Year must be between 1800 and 2100")]
    public int? BuildYear { get; init; }

    /// <summary>
    /// Major upgrade/renovation year (YYYY).
    /// </summary>
    [DisplayName("Upgrade Year")]
    [Description("Year of last major renovation or upgrade")]
    [Range(1800, 2100, ErrorMessage = "Upgrade Year must be between 1800 and 2100")]
    public int? UpgradeYear { get; init; }

    /// <summary>
    /// Number of above-ground stories/floors.
    /// </summary>
    [DisplayName("Number of Stories")]
    [Description("Number of above-ground floors in the building")]
    [Range(1, 200, ErrorMessage = "Number of Stories must be between 1 and 200")]
    public int? NumberOfStories { get; init; }

    /// <summary>
    /// Automatic sprinkler protection present.
    /// </summary>
    [Description("Whether automatic sprinkler fire protection is installed")]
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
