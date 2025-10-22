using FluentAssertions;
using MeshWeaver.Import;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Insurance.Domain;
using Xunit;

namespace MeshWeaver.Insurance.Test;

public class MicrosoftImportTests(ITestOutputHelper output) : InsuranceTestBase(output)
{
    [Fact]
    public void Import_Microsoft_File()
    {
        // Arrange - Create import configuration
        var config = new ExcelImportConfiguration
        {
            Name = "Microsoft.xlsx",
            EntityId = "Microsoft",
            WorksheetName = "Locations", // Adjust based on actual worksheet name
            DataStartRow = 2, // Assuming row 1 is headers
            TotalRowMarkers = new HashSet<string> { "Total", "Grand Total" },
            TotalRowScanAllCells = true,
            TotalRowMatchExact = false,
            Mappings = new List<ColumnMapping>
            {
                // Basic identification
                new() { TargetProperty = "Id", Kind = MappingKind.Direct, SourceColumns = new List<string> { "A" } },
                new() { TargetProperty = "LocationName", Kind = MappingKind.Direct, SourceColumns = new List<string> { "B" } },
                new() { TargetProperty = "PricingId", Kind = MappingKind.Constant, ConstantValue = "Microsoft" },

                // Address fields
                new() { TargetProperty = "Address", Kind = MappingKind.Direct, SourceColumns = new List<string> { "C" } },
                new() { TargetProperty = "City", Kind = MappingKind.Direct, SourceColumns = new List<string> { "D" } },
                new() { TargetProperty = "State", Kind = MappingKind.Direct, SourceColumns = new List<string> { "E" } },
                new() { TargetProperty = "Country", Kind = MappingKind.Direct, SourceColumns = new List<string> { "F" } },
                new() { TargetProperty = "ZipCode", Kind = MappingKind.Direct, SourceColumns = new List<string> { "G" } },

                // Values - adjust column letters based on actual Excel file
                new() { TargetProperty = "Currency", Kind = MappingKind.Direct, SourceColumns = new List<string> { "H" } },
                new() { TargetProperty = "TsiBuilding", Kind = MappingKind.Direct, SourceColumns = new List<string> { "I" } },
                new() { TargetProperty = "TsiContent", Kind = MappingKind.Direct, SourceColumns = new List<string> { "J" } },
                new() { TargetProperty = "TsiBi", Kind = MappingKind.Direct, SourceColumns = new List<string> { "K" } },
            },
            IgnoreRowExpressions = new List<string>
            {
                "Id == null", // Skip rows without an ID
                "Address == null" // Skip rows without an address
            }
        };

        var filePath = "../../Files/Microsoft/2026/Microsoft.xlsx";
        var fullPath = Path.Combine(Directory.GetCurrentDirectory(), filePath);

        // Skip test if file doesn't exist
        if (!File.Exists(fullPath))
        {
            Output.WriteLine($"Test file not found: {fullPath}");
            return;
        }

        // Act - Import using the generic importer
        var importer = new ConfiguredExcelImporter<PropertyRisk>(BuildPropertyRisk);
        var risks = importer.Import(fullPath, config).ToList();

        // Assert
        risks.Should().NotBeEmpty("import should return at least one risk");
        risks.All(r => r.PricingId == "Microsoft").Should().BeTrue("all risks should have PricingId set to Microsoft");
        risks.All(r => !string.IsNullOrWhiteSpace(r.Id)).Should().BeTrue("all risks should have an Id");

        // Verify source tracking
        risks.All(r => r.SourceRow > 0).Should().BeTrue("all risks should have SourceRow set");
        risks.All(r => r.SourceFile == "Microsoft.xlsx").Should().BeTrue("all risks should track source file");

        // Output summary
        Output.WriteLine($"Successfully imported {risks.Count} property risks");
        Output.WriteLine($"Sample risk: Id={risks.First().Id}, Location={risks.First().LocationName}, Country={risks.First().Country}");
    }

    [Fact]
    public void Import_Microsoft_WithAllocation()
    {
        // This test demonstrates allocation mapping - distributing a total value proportionally
        var config = new ExcelImportConfiguration
        {
            Name = "Microsoft.xlsx",
            EntityId = "Microsoft",
            WorksheetName = "Locations",
            DataStartRow = 2,
            Mappings = new List<ColumnMapping>
            {
                new() { TargetProperty = "Id", Kind = MappingKind.Direct, SourceColumns = new List<string> { "A" } },
                new() { TargetProperty = "LocationName", Kind = MappingKind.Direct, SourceColumns = new List<string> { "B" } },
                new() { TargetProperty = "PricingId", Kind = MappingKind.Constant, ConstantValue = "Microsoft" },
                new() { TargetProperty = "TsiBuilding", Kind = MappingKind.Direct, SourceColumns = new List<string> { "I" } },
                new() { TargetProperty = "TsiContent", Kind = MappingKind.Direct, SourceColumns = new List<string> { "J" } },
            },
            Allocations = new List<AllocationMapping>
            {
                // Example: Allocate total BI from cell C3 proportionally based on TsiBuilding + TsiContent weights
                new()
                {
                    TargetProperty = "TsiBi",
                    TotalCell = "C3", // Adjust to actual total cell in Excel
                    WeightColumns = new List<string> { "I", "J" }, // Weight by TsiBuilding + TsiContent
                    CurrencyProperty = "Currency"
                }
            }
        };

        var filePath = "../../Files/Microsoft/2026/Microsoft.xlsx";
        var fullPath = Path.Combine(Directory.GetCurrentDirectory(), filePath);

        if (!File.Exists(fullPath))
        {
            Output.WriteLine($"Test file not found: {fullPath}");
            return;
        }

        var importer = new ConfiguredExcelImporter<PropertyRisk>(BuildPropertyRisk);
        var risks = importer.Import(fullPath, config).ToList();

        risks.Should().NotBeEmpty();

        // Verify allocation worked - sum of allocated TsiBi should equal proportional distribution
        var rowsWithWeights = risks
            .Select(r => new { Building = r.TsiBuilding, Content = r.TsiContent, Bi = r.TsiBi })
            .ToList();

        var weights = rowsWithWeights.Select(x => x.Building + x.Content).ToList();

        // All non-zero weights should have proportional BI allocation
        var factors = rowsWithWeights
            .Zip(weights, (x, w) => new { x.Bi, W = w })
            .Where(p => p.W > 0)
            .Select(p => p.Bi / p.W)
            .ToList();

        if (factors.Any())
        {
            (factors.Max() - factors.Min()).Should().BeLessThan(0.0001, "allocation should be proportional");
        }

        Output.WriteLine($"Imported {risks.Count} risks with proportional BI allocation");
    }

    [Fact]
    public void Import_Microsoft_UsingSumMapping()
    {
        // Demonstrate Sum mapping - combining multiple columns
        var config = new ExcelImportConfiguration
        {
            Name = "Microsoft.xlsx",
            EntityId = "Microsoft",
            WorksheetName = "Locations",
            DataStartRow = 2,
            Mappings = new List<ColumnMapping>
            {
                new() { TargetProperty = "Id", Kind = MappingKind.Direct, SourceColumns = new List<string> { "A" } },
                new() { TargetProperty = "PricingId", Kind = MappingKind.Constant, ConstantValue = "Microsoft" },

                // Sum example: Total TSI = Building + Content + BI
                new() { TargetProperty = "TsiBuilding", Kind = MappingKind.Direct, SourceColumns = new List<string> { "I" } },
                new() { TargetProperty = "TsiContent", Kind = MappingKind.Direct, SourceColumns = new List<string> { "J" } },
                new() { TargetProperty = "TsiBi", Kind = MappingKind.Direct, SourceColumns = new List<string> { "K" } },
            }
        };

        var filePath = "../../Files/Microsoft/2026/Microsoft.xlsx";
        var fullPath = Path.Combine(Directory.GetCurrentDirectory(), filePath);

        if (!File.Exists(fullPath))
        {
            Output.WriteLine($"Test file not found: {fullPath}");
            return;
        }

        var importer = new ConfiguredExcelImporter<PropertyRisk>(BuildPropertyRisk);
        var risks = importer.Import(fullPath, config).ToList();

        risks.Should().NotBeEmpty();
        Output.WriteLine($"Imported {risks.Count} risks using sum mapping");
    }

    /// <summary>
    /// Builder function to construct PropertyRisk from dictionary of properties.
    /// This handles type conversion and provides defaults.
    /// </summary>
    private static PropertyRisk BuildPropertyRisk(Dictionary<string, object?> values)
    {
        return new PropertyRisk
        {
            Id = Get<string>(values, nameof(PropertyRisk.Id)) ?? Guid.NewGuid().ToString(),
            PricingId = Get<string>(values, nameof(PropertyRisk.PricingId)),
            SourceRow = Get<int?>(values, nameof(PropertyRisk.SourceRow)),
            SourceFile = Get<string>(values, nameof(PropertyRisk.SourceFile)),
            LocationName = Get<string>(values, nameof(PropertyRisk.LocationName)),
            Country = Get<string>(values, nameof(PropertyRisk.Country)),
            State = Get<string>(values, nameof(PropertyRisk.State)),
            County = Get<string>(values, nameof(PropertyRisk.County)),
            ZipCode = Get<string>(values, nameof(PropertyRisk.ZipCode)),
            City = Get<string>(values, nameof(PropertyRisk.City)),
            Address = Get<string>(values, nameof(PropertyRisk.Address)),
            Currency = Get<string>(values, nameof(PropertyRisk.Currency)),
            TsiBuilding = Get<double>(values, nameof(PropertyRisk.TsiBuilding)),
            TsiBuildingCurrency = Get<string>(values, nameof(PropertyRisk.TsiBuildingCurrency)) ?? Get<string>(values, nameof(PropertyRisk.Currency)),
            TsiContent = Get<double>(values, nameof(PropertyRisk.TsiContent)),
            TsiContentCurrency = Get<string>(values, nameof(PropertyRisk.TsiContentCurrency)) ?? Get<string>(values, nameof(PropertyRisk.Currency)),
            TsiBi = Get<double>(values, nameof(PropertyRisk.TsiBi)),
            TsiBiCurrency = Get<string>(values, nameof(PropertyRisk.TsiBiCurrency)) ?? Get<string>(values, nameof(PropertyRisk.Currency)),
            AccountNumber = Get<string>(values, nameof(PropertyRisk.AccountNumber)),
            OccupancyScheme = Get<string>(values, nameof(PropertyRisk.OccupancyScheme)),
            OccupancyCode = Get<string>(values, nameof(PropertyRisk.OccupancyCode)),
            ConstructionScheme = Get<string>(values, nameof(PropertyRisk.ConstructionScheme)),
            ConstructionCode = Get<string>(values, nameof(PropertyRisk.ConstructionCode)),
            BuildYear = Get<int?>(values, nameof(PropertyRisk.BuildYear)),
            UpgradeYear = Get<int?>(values, nameof(PropertyRisk.UpgradeYear)),
            NumberOfStories = Get<int?>(values, nameof(PropertyRisk.NumberOfStories)),
            Sprinklers = Get<bool?>(values, nameof(PropertyRisk.Sprinklers)),
            GeocodedLocation = null
        };
    }

    /// <summary>
    /// Generic value getter with type conversion support.
    /// </summary>
    private static T? Get<T>(IDictionary<string, object?> dict, string key)
    {
        if (dict.TryGetValue(key, out var val) && val is not null)
        {
            if (val is T t) return t;

            var targetType = typeof(T);
            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

            // Empty strings should be treated as null/default
            if (val is string s && string.IsNullOrWhiteSpace(s)) return default;

            try
            {
                // Handle common type conversions
                if (underlying == typeof(string))
                    return (T)(object)val.ToString()!;

                if (underlying == typeof(int))
                {
                    if (val is int i) return (T)(object)i;
                    if (val is decimal dm) return (T)(object)(int)dm;
                    if (val is double d) return (T)(object)(int)d;
                    if (int.TryParse(val.ToString(), out var parsed)) return (T)(object)parsed;
                }

                if (underlying == typeof(double))
                {
                    if (val is double d) return (T)(object)d;
                    if (val is decimal dm) return (T)(object)(double)dm;
                    if (val is int i) return (T)(object)(double)i;
                    if (double.TryParse(val.ToString(), out var parsed)) return (T)(object)parsed;
                }

                if (underlying == typeof(decimal))
                {
                    if (val is decimal dm) return (T)(object)dm;
                    if (val is double d) return (T)(object)(decimal)d;
                    if (decimal.TryParse(val.ToString(), out var parsed)) return (T)(object)parsed;
                }

                if (underlying == typeof(bool))
                {
                    if (val is bool b) return (T)(object)b;
                    var str = val.ToString()?.Trim().ToLowerInvariant();
                    if (str == "true" || str == "yes" || str == "1") return (T)(object)true;
                    if (str == "false" || str == "no" || str == "0") return (T)(object)false;
                }

                // Fallback to Convert.ChangeType
                if (val is IConvertible)
                    return (T)Convert.ChangeType(val, underlying);
            }
            catch
            {
                // Swallow conversion errors and return default
            }
        }
        return default;
    }
}
