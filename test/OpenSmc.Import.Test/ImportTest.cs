using FluentAssertions;
using FluentAssertions.Equivalency;
using FluentAssertions.Execution;
using OpenSmc.Activities;
using OpenSmc.Data;
using OpenSmc.Data.TestDomain;
using OpenSmc.DataStructures;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Import.Test;

public class ImportTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Cashflows = nameof(Cashflows);
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {

        return base.ConfigureHost(configuration)
                .ConfigureReferenceDataModel()
                .ConfigureTransactionalModel(2024, "1", "2")
                .ConfigureComputedModel(2024, "1", "2")
                .ConfigureImportHub(2024, "1", "2")
            ;
    }

    private static IEnumerable<object> MapCashflows(ImportRequest request, IDataSet dataSet, IMessageHub hub, IWorkspace workspace)
    {
        var importedInstance = workspace.GetData<TransactionalData>().ToArray();
        return importedInstance.Select(i => new ComputedData(i.Id, 2024, i.LoB, i.BusinessUnit, 2 * i.Value));
    }

    [Fact]
    public async Task DistributedImportTest()
    {
        // arrange
        var client = GetClient();
        var importRequest = new ImportRequest(VanillaDistributedCsv) { Format = TestHubSetup.CashflowImportFormat, };

        // act
        var importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new ImportAddress(2024, new HostAddress())));

        // assert
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Succeeded);

        var transactionalItems1 = await client.AwaitResponse(new GetManyRequest<TransactionalData>(),
            o => o.WithTarget(new TransactionalDataAddress(2024, "1", new HostAddress())));
        var computedItems1 = await client.AwaitResponse(new GetManyRequest<ComputedData>(),
            o => o.WithTarget(new ComputedDataAddress(2024, "1", new HostAddress())));
        var transactionalItems2 = await client.AwaitResponse(new GetManyRequest<TransactionalData>(),
            o => o.WithTarget(new TransactionalDataAddress(2024, "2", new HostAddress())));
        var computedItems2 = await client.AwaitResponse(new GetManyRequest<ComputedData>(),
            o => o.WithTarget(new ComputedDataAddress(2024, "2", new HostAddress())));

        using (new AssertionScope())
        {
            transactionalItems1.Message.Total.Should().Be(2);
            var expectedComputedItems1 = transactionalItems1.Message.Items.Select(x => new ComputedData("", 2024, x.LoB, "1", 2 * x.Value));
            computedItems1.Message.Total.Should().Be(2);
            computedItems1.Message.Items.Select(x => x.Value).Should().BeEquivalentTo(expectedComputedItems1.Select(x => x.Value));
            computedItems1.Message.Items.Should().HaveCount(2).And.BeEquivalentTo(expectedComputedItems1, c => c.WithoutId());

            transactionalItems2.Message.Total.Should().Be(2);
            var expectedComputedItems2 = transactionalItems2.Message.Items.Select(x => new ComputedData("", 2024, x.LoB, "2", 2 * x.Value));
            computedItems2.Message.Total.Should().Be(2);
            computedItems2.Message.Items.Should().HaveCount(2).And.BeEquivalentTo(expectedComputedItems2, c => c.WithoutId());
        }
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
        var importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new HostAddress()));
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Succeeded);

        var items = await client.AwaitResponse(new GetManyRequest<TransactionalData>(),
            o => o.WithTarget(new HostAddress()));
        items.Message.Items.Should().HaveCount(4)
            .And.ContainSingle(i => i.Id == "1")
            .Which.Value.Should().BeApproximately(1.5d, 1e-5); // we started with 7....
        items.Message.Items.Should().ContainSingle(i => i.Id == "2").Which.LoB.Should().Be("1");
    }

    private const string VanillaCsv =
@"@@TransactionalData
Id,LoB,BusinessUnit,Value
1,1,1,1.5
2,1,2,2
3,2,1,3
4,2,2,4
";

    [Fact]
    public async Task TestCashflows()
    {
        var client = GetClient();
        var importRequest = new ImportRequest(VanillaCsv) { Format = Cashflows };
        var importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new HostAddress()));
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Succeeded);
        
        var items = await client.AwaitResponse(new GetManyRequest<ComputedData>(),
            o => o.WithTarget(new HostAddress()));
        items.Message.Items.Should().HaveCount(4)
            .And.ContainSingle(i => i.Id == "1")
            .Which.Value.Should().BeApproximately(3.0d, 1e-5); // computed as 2*1.5
    }
}
public static class ComputedDataEquivalencyExtensions
{
    public static EquivalencyAssertionOptions<ComputedData> WithoutId(this EquivalencyAssertionOptions<ComputedData> options) => options.Excluding(x => x.Id);
}
