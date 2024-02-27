using FluentAssertions;
using OpenSmc.Activities;
using OpenSmc.Data;
using OpenSmc.Data.TestDomain;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Import.Test;

public class SnapshotImportTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {

        return base.ConfigureHost(configuration)
                .AddData(
                    data => data.FromConfigurableDataSource
                    (
                        nameof(DataSource),
                        source => source
                            .ConfigureCategory(TestDomain.TestRecordsDomain)
                    )
                )
                .AddImport(
                    data => data.FromHub(configuration.Address,
                        source => source.ConfigureCategory(TestDomain.TestRecordsDomain)
                    ),
                    import => import
                )
            ;
    }

    [Fact]
    public async Task SnapshotImport_SimpleTest()
    {
        const string content = @"@@MyRecord
SystemName,DisplayName,Number
A1,A,1
A2,A,2
B3,B,3
B4,B,4
";

        var client = GetClient();
        var importRequest = new ImportRequest(content);
        var importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new HostAddress()));
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Succeeded);

        var ret = await client.AwaitResponse(new GetManyRequest<MyRecord>(),
            o => o.WithTarget(new HostAddress()));

        ret.Message.Items.Should().HaveCount(4);

        const string content2 = @"@@MyRecord
SystemName,DisplayName,Number
A5,A,5
@@MyRecord2
SystemName,DisplayName
";

        importRequest = new ImportRequest(content2) {SnapshotMode = true};
        importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new HostAddress()));
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Succeeded);

        ret = await client.AwaitResponse(new GetManyRequest<MyRecord>(),
            o => o.WithTarget(new HostAddress()));

        ret.Message.Items.Should().HaveCount(1);
        ret.Message.Items.Should().ContainSingle().Which.Number.Equals(5);
    }

    [Fact]
    public async Task SnapshotImport_AndThenRegularImportTest()
    {
        const string content1 = @"@@MyRecord
SystemName,DisplayName,Number
A1,A,1
A2,A,2
B3,B,3
B4,B,4
";

        var client = GetClient();
        var importRequest = new ImportRequest(content1);
        var importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new HostAddress()));
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Succeeded);

        var ret = await client.AwaitResponse(new GetManyRequest<MyRecord>(),
            o => o.WithTarget(new HostAddress()));

        ret.Message.Items.Should().HaveCount(4);

        const string content2 = @"@@MyRecord
SystemName,DisplayName,Number
A5,A,5
@@MyRecord2
SystemName,DisplayName
";

        //snapshot
        importRequest = new ImportRequest(content2) { SnapshotMode = true };
        importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new HostAddress()));
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Succeeded);

        ret = await client.AwaitResponse(new GetManyRequest<MyRecord>(),
            o => o.WithTarget(new HostAddress()));

        ret.Message.Items.Should().HaveCount(1);
        ret.Message.Items.Should().ContainSingle().Which.Number.Equals(5);

        const string content3 = @"@@MyRecord
SystemName,DisplayName,Number
A6,A,6
@@MyRecord2
SystemName2,DisplayName2
";

        //not snapshot
        importRequest = new ImportRequest(content3);
        importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new HostAddress()));
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Succeeded);

        ret = await client.AwaitResponse(new GetManyRequest<MyRecord>(),
            o => o.WithTarget(new HostAddress()));

        ret.Message.Items.Should().HaveCount(2);

    }
    
    [Fact]
    public async Task SnapshotImport_ZeroInstancesTest()
    {
        const string content = @"@@MyRecord
SystemName,DisplayName,Number
A1,A,1
A2,A,2
B3,B,3
B4,B,4
";

        var client = GetClient();
        var importRequest = new ImportRequest(content);
        var importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new HostAddress()));
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Succeeded);

        var ret = await client.AwaitResponse(new GetManyRequest<MyRecord>(),
            o => o.WithTarget(new HostAddress()));

        ret.Message.Items.Should().HaveCount(4);

        const string content2 = @"@@MyRecord
SystemName,DisplayName,Number
";

        importRequest = new ImportRequest(content2) { SnapshotMode = true };
        importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new HostAddress()));
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Succeeded);

        ret = await client.AwaitResponse(new GetManyRequest<MyRecord>(),
            o => o.WithTarget(new HostAddress()));

        ret.Message.Items.Should().BeEmpty();
    }

}
