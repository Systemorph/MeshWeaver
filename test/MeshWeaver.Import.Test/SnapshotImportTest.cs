using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using System.Reactive.Threading.Tasks;
namespace MeshWeaver.Import.Test;

public class SnapshotImportTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly Address ImportAddress = TestDomain.TestImportAddress.Create();
    protected override MessageHubConfiguration ConfigureHost(
        MessageHubConfiguration configuration
    ) =>
        base.ConfigureHost(configuration)
            .AddData(data =>
                data.AddSource(
                    source => source.ConfigureCategory(TestDomain.TestRecordsDomain)
                )
            );

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration configuration)
    {
        return base.ConfigureMesh(configuration)
            .WithHostedHub(
                TestDomain.TestImportAddress.Create(),
                config =>
                    config
                        .AddData(data =>
                            data.AddHubSource(
                                CreateHostAddress(),
                                source => source.ConfigureCategory(TestDomain.TestRecordsDomain)
                            )
                        )
                        .AddImport()
            );
    }


    [Fact]
    public async Task SnapshotImport_SimpleTest()
    {
        const string content =
            @"@@MyRecord
SystemName,DisplayName,Number
A1,A,1
A2,A,2
B3,B,3
B4,B,4
";

        var client = GetClient();
        var importRequest = new ImportRequest(content);
        var importResponse = await client.Observe(importRequest, o => o.WithTarget(TestDomain.TestImportAddress.Create()))
            .Should().Within(10.Seconds()).Emit();
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);

        var ret = await GetData<MyRecord>(ImportAddress, x => x.Count >= 4);

        ret.Should().HaveCount(4);

        const string content2 =
            @"@@MyRecord
SystemName,DisplayName,Number
A5,A,5
@@MyRecord2
SystemName,DisplayName
";

        importRequest = new ImportRequest(content2) { UpdateOptions = new() { Snapshot = true } };
        importResponse = await client.Observe(importRequest, o => o.WithTarget(TestDomain.TestImportAddress.Create()))
            .Should().Within(10.Seconds()).Emit();
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);


        ret = await GetData<MyRecord>(ImportAddress, x => x.Count == 1);

        ret.Should().HaveCount(1);
        ret.Should().ContainSingle().Which.Number.Should().Be(5);

        var ret2 = await GetData<MyRecord>(CreateHostAddress(), x => x.Count == 1);
        ret2.Should().HaveCount(1);
        ret2.Should().ContainSingle().Which.Number.Should().Be(5);
    }

    [Fact]
    public async Task SnapshotImport_AndThenRegularImportTest()
    {
        const string content1 =
            @"@@MyRecord
SystemName,DisplayName,Number
A1,A,1
A2,A,2
B3,B,3
B4,B,4
";

        var client = GetClient();
        var importRequest = new ImportRequest(content1);
        var importResponse = await client.Observe(importRequest, o => o.WithTarget(TestDomain.TestImportAddress.Create()))
            .Should().Within(10.Seconds()).Emit();
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);
        var ret = await GetData<MyRecord>(ImportAddress, x => x.Count >= 4);

        ret.Should().HaveCount(4);

        const string content2 =
            @"@@MyRecord
SystemName,DisplayName,Number
A5,A,5
@@MyRecord2
SystemName,DisplayName
";

        //snapshot
        importRequest = new ImportRequest(content2) { UpdateOptions = new() { Snapshot = true } };
        importResponse = await client.Observe(importRequest, o => o.WithTarget(TestDomain.TestImportAddress.Create()))
            .Should().Within(10.Seconds()).Emit();
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);

        ret = await GetData<MyRecord>(ImportAddress, x => x.Count == 1);

        ret.Should().HaveCount(1);
        ret.Should().ContainSingle().Which.Number.Equals(5);

        const string content3 =
            @"@@MyRecord
SystemName,DisplayName,Number
A6,A,6
@@MyRecord2
SystemName2,DisplayName2
";

        //not snapshot
        importRequest = new ImportRequest(content3);
        importResponse = await client.Observe(importRequest, o => o.WithTarget(TestDomain.TestImportAddress.Create()))
            .Should().Within(10.Seconds()).Emit();
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);

        ret = await GetData<MyRecord>(ImportAddress, x => x.Count >= 2);

        ret.Should().HaveCount(2);
    }

    private async Task<IReadOnlyCollection<TData>> GetData<TData>(
        Address address,
        System.Func<IReadOnlyCollection<TData>, bool> predicate,
        TimeSpan? timeout = null)
    {
        timeout ??= 10.Seconds();
        var hub = Mesh.GetHostedHub(address);
        var workspace = hub.ServiceProvider.GetRequiredService<IWorkspace>();
        return await workspace
            .GetObservable<TData>()
            .Should().Within(timeout.Value).Match(predicate);
    }

}
