using System.Reactive.Linq;
using FluentAssertions;
using OpenSmc.Activities;
using OpenSmc.Data;
using OpenSmc.Data.TestDomain;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Import.Test;

public class ImportMappingTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(
        MessageHubConfiguration configuration
    ) =>
        base.ConfigureHost(configuration)
            .AddData(data =>
                data.FromConfigurableDataSource(
                    nameof(GenericDataSource),
                    source => source.ConfigureCategory(TestDomain.TestRecordsDomain)
                )
            )
            .WithHostedHub(
                new TestDomain.ImportAddress(configuration.Address),
                config =>
                    config
                        .AddData(data =>
                            data.FromHub(
                                configuration.Address,
                                source => source.ConfigureCategory(TestDomain.TestRecordsDomain)
                            )
                        )
                        .AddImport(import =>
                            import
                                .WithFormat(
                                    "Test",
                                    format => format.WithImportFunction(CustomImportFunction)
                                )
                                .WithFormat(
                                    "Test2",
                                    format =>
                                        format
                                            .WithImportFunction(CustomImportFunction)
                                            .WithAutoMappings()
                                )
                        )
            );

    private ImportFormat.ImportFunction CustomImportFunction = null;

    private async Task<IMessageHub> DoImport(string content, string format = ImportFormat.Default)
    {
        var client = GetClient();
        var importRequest = new ImportRequest(content) { Format = format };
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new TestDomain.ImportAddress(new HostAddress()))
        );
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Succeeded);
        return client;
    }

    [Fact]
    public async Task DefaultMappingsTest()
    {
        const string content =
            @"@@MyRecord
SystemName,DisplayName,Number,StringsArray0,StringsArray1,StringsArray2,StringsList0,StringsList1,StringsList2,IntArray0,IntArray1,IntArray2,IntList0,IntList1,IntList2
SystemName,DisplayName,2,null,,"""",null,,"""",1,,"""",1,,""""";

        _ = await DoImport(content);
        var host = GetHost();
        var workspace = host.GetHostedHub(new TestDomain.ImportAddress(new HostAddress()))
            .GetWorkspace();
        var ret2 = await workspace.GetObservable<MyRecord2>().FirstAsync();

        ret2.Should().BeNull();

        var ret = await workspace.GetObservable<MyRecord>().FirstAsync();

        ret.Should().HaveCount(1);

        var resRecord = ret.Should().ContainSingle().Which;

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
        _ = await DoImport(string.Empty);

        var host = GetHost();
        var workspace = host.GetHostedHub(new TestDomain.ImportAddress(new HostAddress()))
            .GetWorkspace();
        var ret = await workspace.GetObservable<MyRecord>().FirstAsync();

        ret.Should().BeNull();
    }

    const string ThreeTablesContent =
        @"@@MyRecord
SystemName,DisplayName
OldName,OldName
@@MyRecord2
SystemName,DisplayName
Record2SystemName,Record2DisplayName
@@UnmappedRecord3
SystemName,DisplayName
Record3SystemName,Record3DisplayName";

    [Fact]
    public async Task SingleTableMappingTest()
    {
        CustomImportFunction = (request, set, hub, workspace) =>
        {
            return set.Tables[nameof(MyRecord)]
                .Rows.Select(dsRow => new MyRecord()
                {
                    SystemName = dsRow[nameof(MyRecord.SystemName)]
                        .ToString()
                        ?.Replace("Old", "New"),
                    DisplayName = "test"
                });
        };

        _ = await DoImport(ThreeTablesContent, "Test");

        //Check that didn't appeared what we don't import
        var host = GetHost();
        var workspace = host.GetHostedHub(new TestDomain.ImportAddress(new HostAddress()))
            .GetWorkspace();
        var ret2 = await workspace.GetObservable<MyRecord2>().FirstAsync();

        ret2.Should().BeNull();

        var ret = await workspace.GetObservable<MyRecord>().FirstAsync();

        ret.Should().HaveCount(1);

        var resRecord = ret.Should().ContainSingle().Which;

        resRecord.Should().NotBeNull();
        resRecord.DisplayName.Should().Contain("test");
        resRecord.SystemName.Should().Contain("New");
        resRecord.IntArray.Should().BeNull();
        resRecord.IntList.Should().BeNull();
        resRecord.StringsArray.Should().BeNull();
        resRecord.StringsList.Should().BeNull();
        resRecord.Number.Should().Be(0);
    }

    [Fact]
    public async Task TwoTablesMappingTest()
    {
        CustomImportFunction = (request, set, hub, workspace) =>
        {
            return set.Tables[nameof(MyRecord)]
                .Rows.Select(dsRow => new MyRecord()
                {
                    SystemName = dsRow[nameof(MyRecord.SystemName)]
                        .ToString()
                        ?.Replace("Old", "New"),
                    DisplayName = "test"
                });
        };

        _ = await DoImport(ThreeTablesContent, "Test2");
        var host = GetHost();
        var workspace = host.GetHostedHub(new TestDomain.ImportAddress(new HostAddress()))
            .GetWorkspace();
        var ret2 = await workspace.GetObservable<MyRecord2>().FirstAsync();

        ret2.Should().HaveCount(1);
        var ret = await workspace.GetObservable<MyRecord>().FirstAsync();

        ret.Should().HaveCount(2);

        var resRecord = ret.First(x => x.DisplayName == "test");

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
