using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Activities;
using MeshWeaver.Data;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Import.Test;

public class ImportMappingTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(
        MessageHubConfiguration configuration
    ) =>
        base.ConfigureHost(configuration)
            .AddData(data =>
                data.FromConfigurableDataSource(
                    nameof(GenericUnpartitionedDataSource),
                    source => source.ConfigureCategory(TestDomain.TestRecordsDomain)
                )
            );

    protected override MessageHubConfiguration ConfigureRouter(MessageHubConfiguration conf)
    {
        return base.ConfigureRouter(conf)
            .WithHostedHub(
                new TestDomain.ImportAddress(),
                config =>
                    config
                        .AddData(data =>
                            data.FromHub(
                                new HostAddress(),
                                source => source.ConfigureCategory(TestDomain.TestRecordsDomain)
                            )
                        )
                        .AddImport(import =>
                            import
                                .WithFormat(
                                    "Test",
                                    format => format.WithImportFunction(customImportFunction)
                                )
                                .WithFormat(
                                    "Test2",
                                    format =>
                                        format
                                            .WithImportFunction(customImportFunction)
                                            .WithAutoMappings()
                                )
                        )
            );
            ;
    }

    private ImportFormat.ImportFunction customImportFunction = null;

    private async Task<IMessageHub> DoImport(string content, string format = ImportFormat.Default)
    {
        var client = GetClient();
        var importRequest = new ImportRequest(content) { Format = format };
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new TestDomain.ImportAddress())
            , new CancellationTokenSource(3.Seconds()).Token
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);
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
        var workspace = Router.GetHostedHub(new TestDomain.ImportAddress())
            .GetWorkspace();
        var ret2 = await workspace.GetObservable<MyRecord2>()
            .Timeout(3.Seconds())
            .FirstAsync();

        ret2.Should().BeEmpty();

        var ret = await workspace.GetObservable<MyRecord>()
            .Timeout(3.Seconds())
            .FirstAsync(x => x.Any());

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

        var workspace = Router.GetHostedHub(new TestDomain.ImportAddress())
            .GetWorkspace();
        var ret = await workspace
            .GetObservable<MyRecord>()
            .Timeout(3.Seconds())
            .FirstAsync();

        ret.Should().BeEmpty();
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
        customImportFunction = (_, set, ws, store) =>
        {
            var instances = set.Tables[nameof(MyRecord)]
                .Rows.Select(dsRow => new MyRecord()
                {
                    SystemName = dsRow[nameof(MyRecord.SystemName)]
                        .ToString()
                        ?.Replace("Old", "New"),
                    DisplayName = "test"
                }).ToArray();
            return Task.FromResult(ws.AddInstances(store, instances));
        };

        _ = await DoImport(ThreeTablesContent, "Test");

        //Check that didn't appeared what we don't import
        var workspace = Router.GetHostedHub(new TestDomain.ImportAddress())
            .GetWorkspace();

        var ret = await workspace.GetObservable<MyRecord>()
            .Timeout(3.Seconds())
            .FirstAsync(x => x.Any());

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
        customImportFunction = (_, set, ws,store) =>
        {
            var instances = set.Tables[nameof(MyRecord)]
                .Rows.Select(dsRow => new MyRecord()
                {
                    SystemName = dsRow[nameof(MyRecord.SystemName)]
                        .ToString()
                        ?.Replace("Old", "New"),
                    DisplayName = "test"
                }).ToArray();
            return Task.FromResult(ws.AddInstances(store, instances));
        };

        _ = await DoImport(ThreeTablesContent, "Test2");
        var workspace = Router.GetHostedHub(new TestDomain.ImportAddress())
            .GetWorkspace();
        var ret2 = await workspace
            .GetObservable<MyRecord2>()
            .Timeout(3.Seconds())
            .FirstAsync(x => x.Any());

        ret2.Should().HaveCount(1);
        var ret = await workspace
            .GetObservable<MyRecord>()
            .Timeout(3.Seconds())
            .FirstAsync(x => x.Any());

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
