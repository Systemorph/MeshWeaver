// <meshweaver>
// Id: MicrosoftSampleData
// DisplayName: Microsoft Sample Data
// </meshweaver>

using MeshWeaver.Import.Configuration;


/// <summary>
/// Sample data for Microsoft/2026 pricing demonstration.
/// Includes property risks with geocoded locations, reinsurance structure, and import configuration.
/// </summary>
public static class MicrosoftSampleData
{
    public const string PricingId = "2026";

    /// <summary>
    /// 15 key Microsoft locations globally with geocoded coordinates.
    /// </summary>
    public static readonly PropertyRisk[] PropertyRisks =
    [
        new()
        {
            Id = "MSFT-HQ",
            PricingId = PricingId,
            LocationName = "Microsoft Campus - Redmond",
            Address = "One Microsoft Way",
            City = "Redmond",
            State = "WA",
            ZipCode = "98052",
            Country = "US",
            Currency = "USD",
            TsiBuilding = 500_000_000,
            TsiContent = 150_000_000,
            TsiBi = 200_000_000,
            GeocodedLocation = new GeocodedLocation { Latitude = 47.6405, Longitude = -122.1265, Status = "OK" }
        },
        new()
        {
            Id = "MSFT-SV",
            PricingId = PricingId,
            LocationName = "Microsoft Silicon Valley",
            Address = "1065 La Avenida Street",
            City = "Mountain View",
            State = "CA",
            ZipCode = "94043",
            Country = "US",
            Currency = "USD",
            TsiBuilding = 85_000_000,
            TsiContent = 25_000_000,
            TsiBi = 40_000_000,
            GeocodedLocation = new GeocodedLocation { Latitude = 37.4030, Longitude = -122.0326, Status = "OK" }
        },
        new()
        {
            Id = "MSFT-NY",
            PricingId = PricingId,
            LocationName = "Microsoft New York",
            Address = "11 Times Square",
            City = "New York",
            State = "NY",
            ZipCode = "10036",
            Country = "US",
            Currency = "USD",
            TsiBuilding = 120_000_000,
            TsiContent = 35_000_000,
            TsiBi = 50_000_000,
            GeocodedLocation = new GeocodedLocation { Latitude = 40.7566, Longitude = -73.9897, Status = "OK" }
        },
        new()
        {
            Id = "MSFT-UK",
            PricingId = PricingId,
            LocationName = "Microsoft UK Headquarters",
            Address = "2 Kingdom Street",
            City = "London",
            Country = "GB",
            ZipCode = "W2 6BD",
            Currency = "GBP",
            TsiBuilding = 150_000_000,
            TsiContent = 45_000_000,
            TsiBi = 60_000_000,
            GeocodedLocation = new GeocodedLocation { Latitude = 51.5194, Longitude = -0.1753, Status = "OK" }
        },
        new()
        {
            Id = "MSFT-IE",
            PricingId = PricingId,
            LocationName = "Microsoft Ireland",
            Address = "One Microsoft Place",
            City = "Dublin",
            Country = "IE",
            Currency = "EUR",
            TsiBuilding = 78_000_000,
            TsiContent = 22_000_000,
            TsiBi = 35_000_000,
            GeocodedLocation = new GeocodedLocation { Latitude = 53.2734, Longitude = -6.1879, Status = "OK" }
        },
        new()
        {
            Id = "MSFT-DE",
            PricingId = PricingId,
            LocationName = "Microsoft Germany",
            Address = "Walter-Gropius-Strasse 5",
            City = "Munich",
            Country = "DE",
            ZipCode = "80807",
            Currency = "EUR",
            TsiBuilding = 92_000_000,
            TsiContent = 28_000_000,
            TsiBi = 38_000_000,
            GeocodedLocation = new GeocodedLocation { Latitude = 48.1082, Longitude = 11.5950, Status = "OK" }
        },
        new()
        {
            Id = "MSFT-JP",
            PricingId = PricingId,
            LocationName = "Microsoft Japan",
            Address = "Shinagawa Grand Central Tower",
            City = "Tokyo",
            Country = "JP",
            Currency = "JPY",
            TsiBuilding = 98_000_000,
            TsiContent = 30_000_000,
            TsiBi = 42_000_000,
            GeocodedLocation = new GeocodedLocation { Latitude = 35.6284, Longitude = 139.7387, Status = "OK" }
        },
        new()
        {
            Id = "MSFT-SG",
            PricingId = PricingId,
            LocationName = "Microsoft Singapore",
            Address = "1 Marina Boulevard",
            City = "Singapore",
            Country = "SG",
            Currency = "SGD",
            TsiBuilding = 54_000_000,
            TsiContent = 16_000_000,
            TsiBi = 22_000_000,
            GeocodedLocation = new GeocodedLocation { Latitude = 1.2789, Longitude = 103.8536, Status = "OK" }
        },
        new()
        {
            Id = "MSFT-IN",
            PricingId = PricingId,
            LocationName = "Microsoft India - Bangalore",
            Address = "Embassy Golf Links Business Park",
            City = "Bangalore",
            Country = "IN",
            Currency = "INR",
            TsiBuilding = 160_000_000,
            TsiContent = 48_000_000,
            TsiBi = 65_000_000,
            GeocodedLocation = new GeocodedLocation { Latitude = 12.9352, Longitude = 77.6245, Status = "OK" }
        },
        new()
        {
            Id = "MSFT-CN",
            PricingId = PricingId,
            LocationName = "Microsoft China - Beijing",
            Address = "No. 5 Danling Street",
            City = "Beijing",
            Country = "CN",
            Currency = "CNY",
            TsiBuilding = 180_000_000,
            TsiContent = 55_000_000,
            TsiBi = 72_000_000,
            GeocodedLocation = new GeocodedLocation { Latitude = 39.9815, Longitude = 116.3057, Status = "OK" }
        },
        new()
        {
            Id = "MSFT-AU",
            PricingId = PricingId,
            LocationName = "Microsoft Australia - Sydney",
            Address = "1 Epping Road",
            City = "Sydney",
            State = "NSW",
            Country = "AU",
            Currency = "AUD",
            TsiBuilding = 62_000_000,
            TsiContent = 18_000_000,
            TsiBi = 25_000_000,
            GeocodedLocation = new GeocodedLocation { Latitude = -33.8167, Longitude = 151.1836, Status = "OK" }
        },
        new()
        {
            Id = "MSFT-DC-IA",
            PricingId = PricingId,
            LocationName = "Iowa Data Center",
            Address = "1234 Innovation Way",
            City = "West Des Moines",
            State = "IA",
            Country = "US",
            Currency = "USD",
            TsiBuilding = 78_000_000,
            TsiContent = 350_000_000,
            TsiBi = 120_000_000,
            GeocodedLocation = new GeocodedLocation { Latitude = 41.5628, Longitude = -93.7971, Status = "OK" }
        },
        new()
        {
            Id = "MSFT-DC-VA",
            PricingId = PricingId,
            LocationName = "Virginia Data Center",
            Address = "44060 Digital Loudoun Plaza",
            City = "Ashburn",
            State = "VA",
            Country = "US",
            Currency = "USD",
            TsiBuilding = 98_000_000,
            TsiContent = 420_000_000,
            TsiBi = 150_000_000,
            GeocodedLocation = new GeocodedLocation { Latitude = 39.0438, Longitude = -77.4874, Status = "OK" }
        },
        new()
        {
            Id = "MSFT-DC-IE",
            PricingId = PricingId,
            LocationName = "Dublin Data Center",
            Address = "Profile Park",
            City = "Dublin",
            Country = "IE",
            Currency = "EUR",
            TsiBuilding = 68_000_000,
            TsiContent = 280_000_000,
            TsiBi = 95_000_000,
            GeocodedLocation = new GeocodedLocation { Latitude = 53.2934, Longitude = -6.3635, Status = "OK" }
        },
        new()
        {
            Id = "MSFT-DC-SG",
            PricingId = PricingId,
            LocationName = "Singapore Data Center",
            Address = "26A Ayer Rajah Crescent",
            City = "Singapore",
            Country = "SG",
            Currency = "SGD",
            TsiBuilding = 72_000_000,
            TsiContent = 310_000_000,
            TsiBi = 105_000_000,
            GeocodedLocation = new GeocodedLocation { Latitude = 1.3521, Longitude = 103.8198, Status = "OK" }
        }
    ];

    /// <summary>
    /// 3 reinsurance acceptance layers based on Slip.md structure.
    /// EPI: USD 200,000,000 total, split across 3 layers.
    /// </summary>
    public static readonly ReinsuranceAcceptance[] Acceptances =
    [
        new()
        {
            Id = "L1",
            PricingId = PricingId,
            Name = "Layer 1 - Primary",
            EPI = 66_666_667,
            Rate = 0.00033,
            Brokerage = 0.10,
            Commission = 0.05
        },
        new()
        {
            Id = "L2",
            PricingId = PricingId,
            Name = "Layer 2 - First Excess",
            EPI = 66_666_667,
            Rate = 0.00033,
            Brokerage = 0.10,
            Commission = 0.05
        },
        new()
        {
            Id = "L3",
            PricingId = PricingId,
            Name = "Layer 3 - Second Excess",
            EPI = 66_666_667,
            Rate = 0.00033,
            Brokerage = 0.10,
            Commission = 0.05
        }
    ];

    /// <summary>
    /// 9 reinsurance sections (3 coverages x 3 layers) based on Slip.md.
    /// Coverages: Fire Damage, Natural Catastrophe, Business Interruption.
    /// </summary>
    public static readonly ReinsuranceSection[] Sections =
    [
        // Layer 1 Sections
        new()
        {
            Id = "L1-FIRE",
            AcceptanceId = "L1",
            Name = "Fire Damage - Layer 1",
            LineOfBusiness = "PROP",
            Attach = 5_000_000m,
            Limit = 100_000_000m,
            AggAttach = 25_000_000m,
            AggLimit = 300_000_000m
        },
        new()
        {
            Id = "L1-NAT",
            AcceptanceId = "L1",
            Name = "Natural Catastrophe - Layer 1",
            LineOfBusiness = "PROP",
            Attach = 5_000_000m,
            Limit = 100_000_000m,
            AggAttach = 25_000_000m,
            AggLimit = 300_000_000m
        },
        new()
        {
            Id = "L1-BI",
            AcceptanceId = "L1",
            Name = "Business Interruption - Layer 1",
            LineOfBusiness = "PROP",
            Attach = 5_000_000m,
            Limit = 100_000_000m,
            AggAttach = 25_000_000m,
            AggLimit = 300_000_000m
        },
        // Layer 2 Sections
        new()
        {
            Id = "L2-FIRE",
            AcceptanceId = "L2",
            Name = "Fire Damage - Layer 2",
            LineOfBusiness = "PROP",
            Attach = 105_000_000m,
            Limit = 145_000_000m,
            AggLimit = 435_000_000m
        },
        new()
        {
            Id = "L2-NAT",
            AcceptanceId = "L2",
            Name = "Natural Catastrophe - Layer 2",
            LineOfBusiness = "PROP",
            Attach = 105_000_000m,
            Limit = 145_000_000m,
            AggLimit = 435_000_000m
        },
        new()
        {
            Id = "L2-BI",
            AcceptanceId = "L2",
            Name = "Business Interruption - Layer 2",
            LineOfBusiness = "PROP",
            Attach = 105_000_000m,
            Limit = 145_000_000m,
            AggLimit = 435_000_000m
        },
        // Layer 3 Sections
        new()
        {
            Id = "L3-FIRE",
            AcceptanceId = "L3",
            Name = "Fire Damage - Layer 3",
            LineOfBusiness = "PROP",
            Attach = 250_000_000m,
            Limit = 250_000_000m,
            AggLimit = 750_000_000m
        },
        new()
        {
            Id = "L3-NAT",
            AcceptanceId = "L3",
            Name = "Natural Catastrophe - Layer 3",
            LineOfBusiness = "PROP",
            Attach = 250_000_000m,
            Limit = 250_000_000m,
            AggLimit = 750_000_000m
        },
        new()
        {
            Id = "L3-BI",
            AcceptanceId = "L3",
            Name = "Business Interruption - Layer 3",
            LineOfBusiness = "PROP",
            Attach = 250_000_000m,
            Limit = 250_000_000m,
            AggLimit = 750_000_000m
        }
    ];

    /// <summary>
    /// Excel import configuration for Microsoft.xlsx.
    /// </summary>
    public static readonly ExcelImportConfiguration[] ImportConfigs =
    [
        new()
        {
            Name = "Microsoft.xlsx",
            Address = "pricing/Microsoft/2026",
            TypeName = nameof(PropertyRisk),
            DataStartRow = 7,
            TotalRowMarkers = ["Total", "Grand Total"],
            TotalRowScanAllCells = true,
            TotalRowMatchExact = false,
            Mappings =
            [
                new() { TargetProperty = "Id", Kind = MappingKind.Direct, SourceColumns = ["C"] },
                new() { TargetProperty = "LocationName", Kind = MappingKind.Direct, SourceColumns = ["D"] },
                new() { TargetProperty = "PricingId", Kind = MappingKind.Constant, ConstantValue = PricingId },
                new() { TargetProperty = "Address", Kind = MappingKind.Direct, SourceColumns = ["E"] },
                new() { TargetProperty = "Country", Kind = MappingKind.Direct, SourceColumns = ["B"] },
                new() { TargetProperty = "TsiBuilding", Kind = MappingKind.Direct, SourceColumns = ["H"] },
                new() { TargetProperty = "TsiContent", Kind = MappingKind.Sum, SourceColumns = ["G", "I", "J", "K", "L", "M", "N", "O", "P"] }
            ],
            Allocations = [new() { TargetProperty = "TsiBi", WeightColumns = ["Q"] }],
            IgnoreRowExpressions = ["Id == null", "Address == null"]
        }
    ];
}
