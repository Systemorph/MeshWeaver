using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Activities;
using MeshWeaver.Data;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Import.Test;

public class SnapshotImportTest(ITestOutputHelper output) : HubTestBase(output)
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
            )
            .WithHostedHub(
                new TestDomain.ImportAddress(),
                config =>
                    config
                        .AddData(data =>
                            data.FromHub(
                                configuration.Address,
                                source => source.ConfigureCategory(TestDomain.TestRecordsDomain)
                            )
                        )
                        .AddImport()
            );

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
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new TestDomain.ImportAddress())
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);

        var host = GetHost();
        var workspace = host.GetHostedHub(new TestDomain.ImportAddress())
            .GetWorkspace();
        var ret = await workspace.GetObservable<MyRecord>().FirstAsync();

        ret.Should().HaveCount(4);

        const string content2 =
            @"@@MyRecord
SystemName,DisplayName,Number
A5,A,5
@@MyRecord2
SystemName,DisplayName
";

        importRequest = new ImportRequest(content2) { UpdateOptions = new() { Snapshot = true } };
        importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new TestDomain.ImportAddress())
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);

        await Task.Delay(100);

        ret = await workspace.GetObservable<MyRecord>().FirstAsync();

        ret.Should().HaveCount(1);
        ret.Should().ContainSingle().Which.Number.Equals(5);
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
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new TestDomain.ImportAddress())
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);
        var host = GetHost();
        var workspace = host.GetHostedHub(new TestDomain.ImportAddress())
            .GetWorkspace();
        var ret = await workspace.GetObservable<MyRecord>()
            .Timeout(3.Seconds())
            .FirstAsync(x => x.Any());

        ret.Should().HaveCount(4);

        const string content2 =
            @"@@MyRecord
SystemName,DisplayName,Number
A5,A,5
@@MyRecord2
SystemName,DisplayName
";

        //snapshot
        importRequest = new ImportRequest(content2) { UpdateOptions = new(){Snapshot = true} };
        importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new TestDomain.ImportAddress())
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);

        await Task.Delay(100);

        ret = await workspace.GetObservable<MyRecord>().FirstAsync();

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
        importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new TestDomain.ImportAddress())
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);

        ret = await workspace.GetObservable<MyRecord>().FirstAsync();

        ret.Should().HaveCount(2);
    }

    [Fact(Skip = "Illegal case in current implementation")]
    public async Task SnapshotImport_ZeroInstancesTest()
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
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new TestDomain.ImportAddress())
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);

        var host = GetHost();
        var workspace = host.GetHostedHub(new TestDomain.ImportAddress())
            .GetWorkspace();
        var ret = await workspace.GetObservable<MyRecord>().FirstAsync();

        ret.Should().HaveCount(4);

        const string content2 =
            @"@@MyRecord
SystemName,DisplayName,Number
";

        importRequest = new ImportRequest(content2) { UpdateOptions = new() { Snapshot = true } };
        importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new TestDomain.ImportAddress())
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);

        await Task.Delay(100);

        ret = await workspace.GetObservable<MyRecord>().FirstAsync();

        ret.Should().BeEmpty();
    }
}
