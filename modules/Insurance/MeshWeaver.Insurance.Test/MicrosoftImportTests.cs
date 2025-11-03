using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Import;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Insurance.Domain;
using MeshWeaver.Insurance.Domain.Services;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Insurance.Test;

public class MicrosoftImportTests(ITestOutputHelper output) : InsuranceTestBase(output)
{
    private readonly string _testFilesPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Files", "Microsoft", "2026");
    private const string MicrosoftPricingId = "Microsoft-2026";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // Ensure test directory exists
        Directory.CreateDirectory(_testFilesPath);

        return base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                .AddSingleton<IPricingService, InMemoryPricingService>()
            )
            .ConfigureHub(c => c
                .AddContentCollections()
                .AddFileSystemContentCollection($"Submissions-{MicrosoftPricingId}", _ => _testFilesPath)
                .AddImport()
                .AddData(data => data.AddSource(source => source.WithType<PropertyRisk>()))
            );
    }
    private static readonly ExcelImportConfiguration Config = new()
    {
        Name = "Microsoft.xlsx",
        EntityId = MicrosoftPricingId,
        TypeName = nameof(PropertyRisk), // Auto-generate entity builder for PropertyRisk
        //WorksheetName = "Locations", // Adjust based on actual worksheet name
        DataStartRow = 7, // Assuming row 1 is headers
        TotalRowMarkers = ["Total", "Grand Total"],
        TotalRowScanAllCells = true,
        TotalRowMatchExact = false,
        Mappings =
            [
                new () { TargetProperty = "Id", Kind = MappingKind.Direct, SourceColumns = new List<string> { "C" } },
                new()
                {
                    TargetProperty = "LocationName",
                    Kind = MappingKind.Direct,
                    SourceColumns = ["D"]
                },
                new() { TargetProperty = "PricingId", Kind = MappingKind.Constant, ConstantValue = MicrosoftPricingId },

                // Address fields
                new()
                {
                    TargetProperty = "Address",
                    Kind = MappingKind.Direct,
                    SourceColumns = ["E"]
                },
                new() { TargetProperty = "Country", Kind = MappingKind.Direct, SourceColumns = new List<string> { "B" } },
                new()
                {
                    TargetProperty = "TsiBuilding",
                    Kind = MappingKind.Direct,
                    SourceColumns = ["H"]
                },
                new()
                {
                    TargetProperty = "TsiContent",
                    Kind = MappingKind.Direct,
                    SourceColumns = ["G", "I", "J", "K", "L", "M", "N", "O", "P"]
                },
            ],
        Allocations = [new() { TargetProperty = "TsiBi", WeightColumns = ["Q"] }],
        IgnoreRowExpressions =
        [
            "Id == null", // Skip rows without an ID
            "Address == null"
        ]
    };

    [Fact]
    public async Task Import_Microsoft_File_WithConfiguration()
    {
        // Skip test if file doesn't exist
        var fullPath = Path.Combine(_testFilesPath, "Microsoft.xlsx");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException(fullPath);

        // Arrange - Create import configuration with TypeName

        // Act - Import using ImportRequest with Configuration
        var importRequest = new ImportRequest(new CollectionSource($"Submissions-{MicrosoftPricingId}", "Microsoft.xlsx"))
        {
            Configuration = Config
        };

        var importResponse = await Mesh.AwaitResponse(
            importRequest,
            o => o.WithTarget(Mesh.Address),
            TestContext.Current.CancellationToken
        );

        // Assert
        importResponse.Should().NotBeNull();
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded,
            $"import should succeed. Errors: {string.Join(", ", importResponse.Message.Log.Errors().Select(e => e.Message))}");

        // Verify data was imported by querying the workspace
        var workspace = Mesh.ServiceProvider.GetRequiredService<IWorkspace>();
        var risks = await workspace.GetObservable<PropertyRisk>()
            .Timeout(10.Seconds())
            .FirstAsync(x => x.Count > 0);

        risks.Should().NotBeEmpty("import should return at least one risk");
        risks.All(r => r.PricingId == MicrosoftPricingId).Should().BeTrue("all risks should have PricingId set to Microsoft-2026");
        risks.All(r => !string.IsNullOrWhiteSpace(r.Id)).Should().BeTrue("all risks should have an Id");

        // Verify source tracking
        risks.All(r => r.SourceRow > 0).Should().BeTrue("all risks should have SourceRow set");
        risks.All(r => r.SourceFile == "Microsoft.xlsx").Should().BeTrue("all risks should track source file");

        // Output summary
        Output.WriteLine($"Successfully imported {risks.Count} property risks");
        if (risks.Any())
        {
            var first = risks.First();
            Output.WriteLine($"Sample risk: Id={first.Id}, Location={first.LocationName}, Country={first.Country}");
        }
    }

    [Fact]
    public async Task Import_Microsoft_WithAllocation()
    {
        // Skip test if file doesn't exist
        var fullPath = Path.Combine(_testFilesPath, "Microsoft.xlsx");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException(fullPath);

        // Act - Import using ImportRequest with Configuration
        var importRequest = new ImportRequest(new CollectionSource($"Submissions-{MicrosoftPricingId}", "Microsoft.xlsx"))
        {
            Configuration = Config
        };

        var importResponse = await Mesh.AwaitResponse(
            importRequest,
            o => o.WithTarget(Mesh.Address),
            TestContext.Current.CancellationToken
        );

        // Assert
        importResponse.Should().NotBeNull();
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);

        // Verify data was imported
        var workspace = Mesh.ServiceProvider.GetRequiredService<IWorkspace>();
        var risks = await workspace.GetObservable<PropertyRisk>()
            .Timeout(10.Seconds())
            .FirstAsync(x => x.Count > 0);
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
    public async Task Import_Microsoft_UsingSumMapping()
    {
        // Skip test if file doesn't exist
        var fullPath = Path.Combine(_testFilesPath, "Microsoft.xlsx");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException(fullPath);

        // Demonstrate Sum mapping - combining multiple columns

        // Act - Import using ImportRequest with Configuration
        var importRequest = new ImportRequest(new CollectionSource($"Submissions-{MicrosoftPricingId}", "Microsoft.xlsx"))
        {
            Configuration = Config
        };

        var importResponse = await Mesh.AwaitResponse(
            importRequest,
            o => o.WithTarget(Mesh.Address),
            TestContext.Current.CancellationToken
        );

        // Assert
        importResponse.Should().NotBeNull();
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);

        // Verify data was imported
        var workspace = Mesh.ServiceProvider.GetRequiredService<IWorkspace>();
        var risks = await workspace.GetObservable<PropertyRisk>()
            .Timeout(10.Seconds())
            .FirstAsync(x => x.Count > 0);
        risks.Should().NotBeEmpty();
        Output.WriteLine($"Imported {risks.Count} risks using direct mapping");
    }
}
