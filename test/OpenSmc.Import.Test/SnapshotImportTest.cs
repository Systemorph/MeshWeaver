using FluentAssertions;
using OpenSmc.Activities;
using OpenSmc.Data;
using OpenSmc.DataStructures;
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
                    data => data.WithDataSource
                    (
                        nameof(DataSource),
                        source => source
                            .ConfigureCategory(ImportTestDomain.TestRecordsDomain)
                    )
                )
                .AddImport(import => import
                )
            ;
    }

    private ImportFormat.ImportFunction CustomImportFunction = null;

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

}


//        [Fact]
//        public async void SnapshotImportOnPartitionedData_ZeroInstancesTest()
//        {
//            await InitialImport();

//            await Workspace.Partition.SetAsync<string>("A", ByCompany);

//            var zeroInstances = @"@@PartitionedRecordValueType
//Value,Company";

//            //Act
//            await ImportVariable.FromString(zeroInstances)
//                                .WithType<PartitionedRecordValueType>()
//                                .SnapshotMode()
//                                .ExecuteAsync();

//            //Assert
//            await Workspace.Partition.SetAsync<string>("A", ByCompany);
//            var ret = await Workspace.Query<PartitionedRecordValueType>().ToListAsync();
//            ret.Should().BeEmpty();

//            await Workspace.Partition.SetAsync<string>("B", ByCompany);
//            ret = await Workspace.Query<PartitionedRecordValueType>().ToListAsync();
//            ret.Should().HaveCount(2);
//        }

//        private async Task InitialImport()
//        {
//            var records = @"@@PartitionedRecordValueType
//Value,Company
//1,A
//2,A,
//3,B
//4,B";
//            await ImportVariable.FromString(records)
//                                .WithType<PartitionedRecordValueType>()
//                                .ExecuteAsync();
//            //Assert
//            await Workspace.Partition.SetAsync<string>("A", ByCompany);
//            var ret = await Workspace.Query<PartitionedRecordValueType>().ToListAsync();
//            ret.Should().HaveCount(2);

//            await Workspace.Partition.SetAsync<string>("B", ByCompany);
//            ret = await Workspace.Query<PartitionedRecordValueType>().ToListAsync();
//            ret.Should().HaveCount(2);
//        }


//        [Fact]
//        public async void SnapshotImportOnPartitionedData_WithDelegatingDataToTargetTest()
//        {
//            //Arrange
//            await InitialImport();

//            //Act
//            await Workspace.CommitToTargetAsync(targetWorkspace);

//            //Assert
//            await targetWorkspace.Partition.SetAsync<string>("A", ByCompany);
//            var ret = await targetWorkspace.Query<PartitionedRecordValueType>().ToListAsync();
//            ret.Should().HaveCount(2);

//            await targetWorkspace.Partition.SetAsync<string>("B", ByCompany);
//            ret = await targetWorkspace.Query<PartitionedRecordValueType>().ToListAsync();
//            ret.Should().HaveCount(2);

//            var recordsForCompanyA = @"@@PartitionedRecordValueType
//Value,Company
//5,A";

//            //Act2
//            await ImportVariable.FromString(recordsForCompanyA)
//                                .WithType<PartitionedRecordValueType>()
//                                .SnapshotMode()
//                                .ExecuteAsync();

//            //Assert2
//            await Workspace.Partition.SetAsync<string>("A", ByCompany);
//            ret = await Workspace.Query<PartitionedRecordValueType>().ToListAsync();
//            ret.Should().ContainSingle().Which.Value.Should().Be(5);


//            await Workspace.Partition.SetAsync<string>("B", ByCompany);
//            ret = await Workspace.Query<PartitionedRecordValueType>().ToListAsync();
//            ret.Should().HaveCount(2);

//            //Act3
//            await Workspace.CommitToTargetAsync(targetWorkspace);

//            //Assert3
//            await targetWorkspace.Partition.SetAsync<string>("A", ByCompany);
//            ret = await targetWorkspace.Query<PartitionedRecordValueType>().ToListAsync();
//            ret.Should().ContainSingle().Which.Value.Should().Be(5);

//            await targetWorkspace.Partition.SetAsync<string>("B", ByCompany);
//            ret = await targetWorkspace.Query<PartitionedRecordValueType>().ToListAsync();
//            ret.Should().HaveCount(2);
//        }

