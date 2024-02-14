using DocumentFormat.OpenXml.Spreadsheet;
using FluentAssertions;
using OpenSmc.Activities;
using OpenSmc.Data;
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
                .AddData(
                    data => data.WithDataSource
                    (
                        nameof(DataSource),
                        source => source
                        .ConfigureTransactionalData()
                        .ConfigureComputedData()
                        .ConfigureReferenceData()
                        .ConfigureCategory(ImportTestDomain.TestRecordsDomain)
                    )
                )
                .AddImport(import => import
                    .WithFormat(Cashflows, format => format
                        .WithAutoMappings()
                        .WithImportFunction(MapCashflows) 
                    )
                    .WithFormat("Test", format => format
                        .WithImportFunction(CustomImportFunction)
                    )
                )
            ;
    }

    private ImportFormat.ImportFunction CustomImportFunction = MapCashflows;

    private static IEnumerable<object> MapCashflows(ImportRequest request, IDataSet dataSet, IMessageHub hub, IWorkspace workspace)
    {
        var importedInstance = workspace.GetData<ImportTestDomain.TransactionalData>().ToArray();
        return importedInstance.Select(i => new ImportTestDomain.ComputedData(i.Id, i.LoB, i.BusinessUnit, 2 * i.Value));
    }

    private async Task<IMessageHub> DoImport(string content, string format = ImportFormat.Default)
    {
        var client = GetClient();
        var importRequest = new ImportRequest(content) { Format = format };
        var importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new HostAddress()));
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Succeeded);
        return client;
    }

    [Fact]
    public async Task TestVanilla()
    {
        var client = await DoImport(VanillaCsv);

        var items = await client.AwaitResponse(new GetManyRequest<ImportTestDomain.TransactionalData>(),
            o => o.WithTarget(new HostAddress()));
        items.Message.Items.Should().HaveCount(4)
            .And.ContainSingle(i => i.Id == "1")
            .Which.Value.Should().Be(1); // we started with 7....
        items.Message.Items.Should().ContainSingle(i => i.Id == "2").Which.LoB.Should().Be("1");
    }

    private const string VanillaCsv =
@"@@TransactionalData
Id,LoB,BusinessUnit,Value
1,1,1,1
2,1,2,2
3,2,1,3
4,2,2,4
";

    [Fact]
    public async Task TestCashflows()
    {
        var client = await DoImport(VanillaCsv, Cashflows);

        var items = await client.AwaitResponse(new GetManyRequest<ImportTestDomain.ComputedData>(),
            o => o.WithTarget(new HostAddress()));
        items.Message.Items.Should().HaveCount(4)
            .And.ContainSingle(i => i.Id == "1")
            .Which.Value.Should().Be(2); // computed as 2*1
    }

    [Fact]
    public async Task DefaultMappingsTest()
    {
        const string content = @"@@MyRecord
SystemName,DisplayName,Number,StringsArray0,StringsArray1,StringsArray2,StringsList0,StringsList1,StringsList2,IntArray0,IntArray1,IntArray2,IntList0,IntList1,IntList2
SystemName,DisplayName,2,null,,"""",null,,"""",1,,"""",1,,""""";

        var client = await DoImport(content);

        var ret = await client.AwaitResponse(new GetManyRequest<MyRecord>(),
        o => o.WithTarget(new HostAddress()));

        ret.Message.Items.Should().HaveCount(1);
        
        var resRecord = ret.Message.Items.Should().ContainSingle().Which;

        resRecord.Should().NotBeNull();
        resRecord.SystemName.Should().Be("SystemName");
        resRecord.DisplayName.Should().Be("DisplayName");
        resRecord.Number.Should().Be(2);
        resRecord.StringsArray.Should().HaveCount(1);
        resRecord.StringsArray[0].Should().Be("null");
        resRecord.StringsList.Should().HaveCount(1);
        resRecord.StringsList[0].Should().Be("null");
        resRecord.IntArray.Should().HaveCount(1);
        resRecord.IntArray[0].Should().Be(1);
        resRecord.IntList.Should().HaveCount(1);
        resRecord.IntList[0].Should().Be(1);
    }

    [Fact]
    public async Task EmptyDataSetImportTest()
    {
        var client = await DoImport(string.Empty);

        var ret = await client.AwaitResponse(new GetManyRequest<MyRecord>(),
            o => o.WithTarget(new HostAddress()));

        ret.Message.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task SingleTableMappingTest()
    {
        const string content = @"@@MyRecord
SystemName,DisplayName
OldName,OldName
@@MyRecord2
SystemName,DisplayName
Record2SystemName,Record2DisplayName
@@UnmappedRecord3
SystemName,DisplayName
Record3SystemName,Record3DisplayName";

        CustomImportFunction = (request, set, hub, workspace) =>
        {
            return set.Tables[nameof(MyRecord)].Rows.Select(dsRow => new MyRecord()
                {
                    SystemName = dsRow[nameof(MyRecord.SystemName)].ToString()?.Replace("Old", "New"),
                    DisplayName = "test"
                }
            );
        };

        var client = await DoImport(content, "Test");

        var ret = await client.AwaitResponse(new GetManyRequest<MyRecord>(),
            o => o.WithTarget(new HostAddress()));


        ret.Message.Items.Should().HaveCount(1);

        var resRecord = ret.Message.Items.Should().ContainSingle().Which;

        resRecord.Should().NotBeNull();
        resRecord.DisplayName.Should().Contain("test");
        resRecord.SystemName.Should().Contain("New");
        resRecord.IntArray.Should().BeNull();
        resRecord.IntList.Should().BeNull();
        resRecord.StringsArray.Should().BeNull();
        resRecord.StringsList.Should().BeNull();
        resRecord.Number.Should().Be(0);
    }

}