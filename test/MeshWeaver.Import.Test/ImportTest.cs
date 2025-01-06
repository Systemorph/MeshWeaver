using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Equivalency;
using FluentAssertions.Execution;
using FluentAssertions.Extensions;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Activities;
using MeshWeaver.Data;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Import.Test;

public class ImportTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureRouter(
        MessageHubConfiguration configuration
    )
    {
        return base.ConfigureRouter(configuration)
            .WithRoutes(forward =>
                forward
                    .RouteAddressToHostedHub<ReferenceDataAddress>(c => c.ConfigureReferenceDataModel())
                    .RouteAddressToHostedHub<TransactionalDataAddress>(c => c.ConfigureTransactionalModel((TransactionalDataAddress)c.Address))
                    .RouteAddressToHostedHub<ComputedDataAddress>(c => c.ConfigureComputedModel((ComputedDataAddress)c.Address))
                    .RouteAddressToHostedHub<ImportAddress>(c => c.ConfigureImportHub((ImportAddress)c.Address))
            );
    }


    [Fact]
    public void SerializeTransactionalData()
    {
        var client = GetClient();
        var transactionalData = new TransactionalData2("1", 2014, "lob", "bu", 1.23);
        var serialized = JsonSerializer.Serialize(transactionalData, client.JsonSerializerOptions);
        var deserialized = JsonSerializer.Deserialize<TransactionalData2>(
            serialized,
            client.JsonSerializerOptions
        );

        deserialized.Should().Be(transactionalData);
    }

    [Fact]
    public async Task DistributedImportTest()
    {
        // arrange
        var client = GetClient();
        var timeout = 10.Seconds();
        var importRequest = new ImportRequest(VanillaDistributedCsv)
        {
            Format = TestHubSetup.CashflowImportFormat,
        };

        // act
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new ImportAddress(2024))
        );

        // assert
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);
        var transactionalItems1 = await GetWorkspace(
                Router.GetHostedHub(new TransactionalDataAddress(2024, "1"))
            )
            .GetObservable<TransactionalData>()
            .Timeout(timeout)
            .FirstAsync(x => x.Count > 1);

        var computedItems1 = await GetWorkspace(
                Router.GetHostedHub(new ComputedDataAddress(2024, "1"))
            )
            .GetObservable<ComputedData>()
            .Timeout(timeout)
            .FirstAsync(x => x is { Count: > 0 });

        using (new AssertionScope())
        {
            transactionalItems1.Should().HaveCount(2);
            var expectedComputedItems1 = transactionalItems1
                .Select(x => new ComputedData("", 2024, x.LoB, "1", 2 * x.Value))
                .ToArray();
            computedItems1.Should().HaveCount(2);
            computedItems1
                .Select(x => x.Value)
                .Should()
                .BeEquivalentTo(expectedComputedItems1.Select(x => x.Value));
        }
    }

    private IWorkspace GetWorkspace(IMessageHub hub)
    {
        return hub.ServiceProvider.GetRequiredService<IWorkspace>();
    }

    private const string VanillaDistributedCsv =
        @"@@TransactionalData
Id,Year,LoB,BusinessUnit,Value
1,2024,1,1,1.5
2,2024,1,2,2
3,2024,2,1,3
4,2024,2,2,4
";

    [Fact]
    public async Task TestVanilla()
    {
        var client = GetClient();
        var importRequest = new ImportRequest(VanillaCsv);
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new ImportAddress(2024)),
            new CancellationTokenSource(5.Seconds()).Token
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);
        var workspace = GetWorkspace(
            Router.GetHostedHub(new ReferenceDataAddress(), null)
        );
        var items = await workspace
            .GetObservable<LineOfBusiness>()
            .Timeout(10.Seconds())
            .FirstAsync(x => x.FirstOrDefault()?.DisplayName.StartsWith("LoB") ?? false);
        var expectedLoBs = new[]
        {
            new LineOfBusiness("1", "LoB_one"),
            new LineOfBusiness("2", "LoB_two"),
        };

        items.Should().HaveSameCount(expectedLoBs).And.BeEquivalentTo(expectedLoBs);
    }

    private const string VanillaCsv =
        @"@@LineOfBusiness
SystemName,DisplayName
1,LoB_one
2,LoB_two
";

    [Fact]
    public async Task MultipleTypes()
    {
        var client = GetClient();
        var importRequest = new ImportRequest(MultipleTypesCsv);
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new ImportAddress(2024))
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);
        await Task.Delay(100);
        var workspace = GetWorkspace(
            Router.GetHostedHub(new ReferenceDataAddress(), null)
        );
        var actualLoBs = await workspace.GetObservable<LineOfBusiness>().FirstAsync(x => x.First().DisplayName.StartsWith("LoB"));
        var actualBUs = await workspace.GetObservable<BusinessUnit>().FirstAsync(x => x.Count > 2);
        var expectedLoBs = new[]
        {
            new LineOfBusiness("1", "LoB_one"),
            new LineOfBusiness("2", "LoB_two"),
        };
        var expectedBUs = new[]
        {
            new BusinessUnit("1", "1"),
            new BusinessUnit("2", "BU_two"),
            new BusinessUnit("BU1", "BU_one"),
        };

        using (new AssertionScope())
        {
            actualLoBs.Should().HaveSameCount(expectedLoBs).And.BeEquivalentTo(expectedLoBs);
            actualBUs.Should().HaveSameCount(expectedBUs).And.BeEquivalentTo(expectedBUs);
        }
    }

    private const string MultipleTypesCsv =
        $@"{VanillaCsv}
@@BusinessUnit
SystemName,DisplayName
BU1,BU_one
2,BU_two
";
}

public static class ComputedDataEquivalencyExtensions
{
    public static EquivalencyAssertionOptions<ComputedData> WithoutId(
        this EquivalencyAssertionOptions<ComputedData> options
    ) => options.Excluding(x => x.Id);
}
