using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Import.Test;

public class ImportMappingTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly Address ImportAddress = new TestDomain.ImportAddress();
    protected override MessageHubConfiguration ConfigureHost(
        MessageHubConfiguration configuration
    ) =>
        base.ConfigureHost(configuration)
            .AddData(data =>
                data.AddSource(
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
                            data.AddHubSource(
                                new HostAddress(),
                                source => source.ConfigureCategory(TestDomain.TestRecordsDomain)
                            )
                        )
                        .AddImport(import =>
                            import
                                .WithFormat(
                                    "Test",
                                    format => format.WithImportFunction(customImportFunctionAsync!)
                                )
                                .WithFormat(
                                    "Test2",
                                    format =>
                                        format
                                            .WithImportFunction(customImportFunctionAsync!)
                                            .WithAutoMappings()
                                )
                        )
            );
    }

    private ImportFormat.ImportFunctionAsync? customImportFunctionAsync;

    private async Task<ActivityLog> DoImport(string content, string format = ImportFormat.Default)
    {
        var importRequest = new ImportRequest(content) { Format = format };
        var importResponse = await GetClient().AwaitResponse(
            importRequest,
            o => o.WithTarget(new TestDomain.ImportAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken
                //, new CancellationTokenSource(10.Seconds()).Token
            ).Token
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);
        return importResponse.Message.Log;
    }

    [Fact]
    public async Task DefaultMappingsTest()
    {
        // Test debug message for MeshWeaver.Import namespace 
        Logger.LogDebug("This is a debug message from MeshWeaver.Import.Test - should appear if Debug config works");
        
        const string content =
            @"@@MyRecord
SystemName,DisplayName,Number,StringsArray0,StringsArray1,StringsArray2,StringsList0,StringsList1,StringsList2,IntArray0,IntArray1,IntArray2,IntList0,IntList1,IntList2
SystemName,DisplayName,2,null,,"""",null,,"""",1,,"""",1,,""""";

        var log = await DoImport(content);
        log.Status.Should().Be(ActivityStatus.Succeeded);
        Logger.LogInformation("*** Import Finished ***");

        var ret2 = await GetDataAsync<MyRecord2>(ImportAddress, x => true);

        ret2.Should().BeEmpty();

        var ret = await GetDataAsync<MyRecord>(ImportAddress, x => x.Count > 0);

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

        var ret = await GetDataAsync<MyRecord>(ImportAddress, x => true);

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
        customImportFunctionAsync = (_, set, ws, store, _) =>
        {
            var instances = set.Tables[nameof(MyRecord)]!
                .Rows.Select(dsRow => new MyRecord()
                {
                    SystemName = dsRow[nameof(MyRecord.SystemName)]!
                        .ToString()!
                        .Replace("Old", "New"),
                    DisplayName = "test"
                }).ToArray();
            return Task.FromResult(ws.AddInstances(store, instances));
        };

        var log = await DoImport(ThreeTablesContent, "Test");
        log.Status.Should().Be(ActivityStatus.Succeeded);
        //Check that didn't appeared what we don't import

        Logger.LogInformation("*** Import Finished ***");

        var ret = await GetDataAsync<MyRecord>(ImportAddress, x => x.Count > 0);

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
        customImportFunctionAsync = (_, set, ws, store, _) =>
        {
            var instances = set.Tables[nameof(MyRecord)]!
                .Rows.Select(dsRow => new MyRecord()
                {
                    SystemName = dsRow[nameof(MyRecord.SystemName)]!
                        .ToString()!
                        .Replace("Old", "New"),
                    DisplayName = "test"
                }).ToArray();
            return Task.FromResult(ws.AddInstances(store, instances));
        };

        var log = await DoImport(ThreeTablesContent, "Test2");
        log.Status.Should().Be(ActivityStatus.Succeeded);

        Logger.LogInformation("*** Import Finished ***");
        var address = new TestDomain.ImportAddress();
        var ret2 = await GetDataAsync<MyRecord2>(address, x => x.Count > 0);

        ret2.Should().HaveCount(1);
        var ret = await GetDataAsync<MyRecord>(address, x => x.Count > 1); 

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

    private async Task<IReadOnlyCollection<TData>?> GetDataAsync<TData>(
        Address address,
        System.Func<IReadOnlyCollection<TData>, bool> predicate,
        TimeSpan? timeout = null)
    {
        timeout ??= 10.Seconds();
        var hub = Router.GetHostedHub(address);
        var workspace = hub.ServiceProvider.GetRequiredService<IWorkspace>();
        return await workspace
            .GetObservable<TData>()
            .Where(predicate)
            .Timeout(timeout.Value)
            .FirstAsync();
    }
}
