using FluentAssertions;
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
        var client = GetClient();
        var importRequest = new ImportRequest(VanillaDistributedCsv) { Format = TestHubSetup.CashflowImportFormat, };
        var importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new ImportAddress(2024, new HostAddress())));
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Succeeded);

        var transactionalItems = await client.AwaitResponse(new GetManyRequest<TransactionalData>(),
            o => o.WithTarget(new TransactionalDataAddress(2024, "1", new HostAddress())));
        var computedItems = await client.AwaitResponse(new GetManyRequest<ComputedData>(),
            o => o.WithTarget(new ComputedDataAddress(2024, "1", new HostAddress())));


        transactionalItems.Message.Total.Should().Be(2);
        computedItems.Message.Total.Should().Be(2);
        computedItems.Message.Items.First().Value.Should().Be(2*transactionalItems.Message.Items.First().Value);

        // TODO V10: complete test, add asserts, etc. (27.02.2024, Roland Bürgi)
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