using System;
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
using Microsoft.Extensions.Logging;
using MeshWeaver.Data;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Import.Test;

public class ImportTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureRouter(
        MessageHubConfiguration configuration)
    {
        return base.ConfigureRouter(configuration)
            .WithRoutes(forward =>
                forward
                    .RouteAddressToHostedHub<ReferenceDataAddress>(c => c.ConfigureReferenceDataModel())
                    .RouteAddressToHostedHub<TransactionalDataAddress>(c => c.ConfigureTransactionalModel((TransactionalDataAddress)c.Address))
                    .RouteAddressToHostedHub<ComputedDataAddress>(c => c.ConfigureComputedModel())
                    .RouteAddressToHostedHub<ImportAddress>(c => c.ConfigureImportHub())
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
        var timeout = 20.Seconds();
        var importRequest = new ImportRequest(VanillaDistributedCsv)
        {
            Format = TestHubSetup.CashflowImportFormat,
        }
        .WithTimeout(30.Seconds()); // Add timeout for bulk test scenarios

        // Add debug logging for hanging detection
        var testId = Guid.NewGuid().ToString("N")[..8];
        Logger.LogInformation("Starting DistributedImportTest {TestId} at {Timestamp}", testId, DateTime.UtcNow);

        // act
        Logger.LogInformation("DistributedImportTest {TestId}: Sending import request with {Timeout}s timeout", testId, importRequest.Timeout?.TotalSeconds);
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new ImportAddress(2024)),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(30.Seconds()).Token
            ).Token
        );
        Logger.LogInformation("DistributedImportTest {TestId}: Import response received with status {Status}", testId, importResponse.Message.Log.Status);

        // assert
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);

        Logger.LogInformation("DistributedImportTest {TestId}: Getting transactional workspace", testId);
        var transactionalItems1 = await (await GetWorkspace(
                Router.GetHostedHub(new TransactionalDataAddress(2024, "1"))
            ))
            .GetObservable<TransactionalData>()
            .Timeout(timeout)
            .FirstAsync(x => x.Count > 1);
        Logger.LogInformation("DistributedImportTest {TestId}: Got {Count} transactional items", testId, transactionalItems1.Count);

        Logger.LogInformation("DistributedImportTest {TestId}: Getting computed workspace", testId);
        var computedItems1 = await (await GetWorkspace(
                Router.GetHostedHub(new ComputedDataAddress(2024, "1"))
            ))
            .GetObservable<ComputedData>()
            .Timeout(timeout)
            .FirstAsync(x => x is { Count: > 0 });
        Logger.LogInformation("DistributedImportTest {TestId}: Got {Count} computed items", testId, computedItems1.Count);

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

    private async Task<IWorkspace> GetWorkspace(IMessageHub hub)
    {
        await hub.Started;
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
        var importRequest = new ImportRequest(VanillaCsv); // Add timeout for bulk test scenarios
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new ImportAddress(2024)),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken
                , new CancellationTokenSource(10.Seconds()).Token
            ).Token
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);
        var workspace = await GetWorkspace(
            Router.GetHostedHub(new ReferenceDataAddress(), null!)
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
        var importRequest = new ImportRequest(MultipleTypesCsv)
            .WithTimeout(15.Seconds()); // Add timeout for bulk test scenarios
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new ImportAddress(2024)),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(15.Seconds()).Token
            ).Token
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);
        await Task.Delay(100, CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken,
            new CancellationTokenSource(5.Seconds()).Token
        ).Token);
        var workspace = await GetWorkspace(
            Router.GetHostedHub(new ReferenceDataAddress(), null!)
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
    public static EquivalencyOptions<ComputedData> WithoutId(
        this EquivalencyOptions<ComputedData> options
    ) => options.Excluding(x => x.Id);
}
