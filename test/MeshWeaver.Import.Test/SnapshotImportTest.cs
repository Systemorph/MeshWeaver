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
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Import.Test;

public class SnapshotImportTest(ITestOutputHelper output) : HubTestBase(output)
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

    protected override MessageHubConfiguration ConfigureRouter(MessageHubConfiguration configuration)
    {
        return base.ConfigureRouter(configuration)
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
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new TestDomain.ImportAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken
                , new CancellationTokenSource(10.Seconds()).Token
            ).Token
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);

        var ret = await GetDataAsync<MyRecord>(ImportAddress);

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
            o => o.WithTarget(new TestDomain.ImportAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(10.Seconds()).Token
            ).Token
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);


        ret = await GetDataAsync<MyRecord>(ImportAddress);

        ret.Should().HaveCount(1);
        ret.Should().ContainSingle().Which.Number.Should().Be(5);

        var ret2 = await GetDataAsync<MyRecord>(new HostAddress());
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
        var importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new TestDomain.ImportAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken
                //, new CancellationTokenSource(10.Seconds()).Token
            ).Token
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);
        var ret = await GetDataAsync<MyRecord>(ImportAddress);

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
        importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new TestDomain.ImportAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(10.Seconds()).Token
            ).Token
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);

        await Task.Delay(100, CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken,
            new CancellationTokenSource(5.Seconds()).Token
        ).Token);

        ret = await GetDataAsync<MyRecord>(ImportAddress);

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
            o => o.WithTarget(new TestDomain.ImportAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(10.Seconds()).Token
            ).Token
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);

        await Task.Delay(100, CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken,
            new CancellationTokenSource(5.Seconds()).Token
        ).Token);
        ret = await GetDataAsync<MyRecord>(ImportAddress);

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
            o => o.WithTarget(new TestDomain.ImportAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(10.Seconds()).Token
            ).Token
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);

        var ret = await GetDataAsync<MyRecord>(new HostAddress());

        ret.Should().HaveCount(4);

        const string content2 =
            @"@@MyRecord
SystemName,DisplayName,Number
";

        importRequest = new ImportRequest(content2) { UpdateOptions = new() { Snapshot = true } };
        importResponse = await client.AwaitResponse(
            importRequest,
            o => o.WithTarget(new TestDomain.ImportAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(10.Seconds()).Token
            ).Token
        );
        importResponse.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);

        await Task.Delay(100, CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken,
            new CancellationTokenSource(5.Seconds()).Token
        ).Token);

        ret = await GetDataAsync<MyRecord>(new HostAddress());

        ret.Should().BeEmpty();
    }

    private async Task<IReadOnlyCollection<TData>?> GetDataAsync<TData>(Address address)
    {
        var response = await GetClient().AwaitResponse(new GetDataRequest(new CollectionReference(typeof(TData).Name)),
            opt => opt.WithTarget(address), new CancellationTokenSource(10.Seconds()).Token);
        return ((InstanceCollection?)response.Message.Data)?.Instances.Values.Cast<TData>().ToArray();
    }

}
