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
B4,B,4";

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
";

        importRequest = new ImportRequest(content2) {SnapshotMode = true};
        importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new HostAddress()));
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Succeeded);

        ret = await client.AwaitResponse(new GetManyRequest<MyRecord>(),
            o => o.WithTarget(new HostAddress()));

        ret.Message.Items.Should().HaveCount(1);
    }
}

//    [Fact]
//        public async void SnapshotImportOnPartitionedData_SimpleTest()
//        {
//            //Arrange
//            await InitialImport();

//            var recordsForCompanyA = @"@@PartitionedRecordValueType
//Value,Company
//5,A";

//            //Act
//            await ImportVariable.FromString(recordsForCompanyA)
//                                .WithType<PartitionedRecordValueType>()
//                                .SnapshotMode()
//                                .ExecuteAsync();

//            //Assert
//            await Workspace.Partition.SetAsync<string>("A", ByCompany);
//            var ret = Workspace.GetItems<PartitionedRecordValueType>();
//            ret.Should().ContainSingle().Which.Value.Should().Be(5);


//            await Workspace.Partition.SetAsync<string>("B", ByCompany);
//            ret = Workspace.GetItems<PartitionedRecordValueType>();
//            ret.Should().HaveCount(2);
//        }

//        [Fact]
//        public async void SnapshotImportUnPartitionedData_SimpleTest()
//        {
//            //Arrange
//            var records = @"@@CompanyPartition
//Company
//C,
//D";
//            await ImportVariable.FromString(records)
//                                .WithType<CompanyPartition>()
//                                .ExecuteAsync();


//            records = @"@@CompanyPartition
//Company
//E,
//F";

//            //Act
//            await ImportVariable.FromString(records)
//                                .WithType<CompanyPartition>()
//                                .SnapshotMode()
//                                .ExecuteAsync();

//            //Assert
//            var ret = Workspace.GetItems<CompanyPartition>();
//            ret.Should().HaveCount(2);
//            ret.Select(x => x.Company).Should().BeEquivalentTo("E", "F");
//        }

//        [Fact]
//        public async void SnapshotImportOnPartitionedData_ForSpecifiedType_Test()
//        {
//            //Arrange
//            await InitialImport();

//            //import CompanyPartition
//            var records = @"@@CompanyPartition
//Company
//C,
//D";
//            await ImportVariable.FromString(records)
//                                .WithType<CompanyPartition>()
//                                .ExecuteAsync();

//            var recordsForCompanyA = @"@@PartitionedRecordValueType
//Value,Company
//5,A
//@@CompanyPartition
//Company
//E,
//F";

//            //Act
//            await ImportVariable.FromString(recordsForCompanyA)
//                                .WithType<PartitionedRecordValueType>(x=>x.SnapshotMode())
//                                .WithType<CompanyPartition>()
//                                .ExecuteAsync();

//            //Assert
//            await Workspace.Partition.SetAsync<string>("A", ByCompany);
//            var ret = Workspace.GetItems<PartitionedRecordValueType>();
//            ret.Should().ContainSingle().Which.Value.Should().Be(5);


//            await Workspace.Partition.SetAsync<string>("B", ByCompany);
//            ret = Workspace.GetItems<PartitionedRecordValueType>();
//            ret.Should().HaveCount(2);

//            //Assert
//            var ret2 = Workspace.GetItems<CompanyPartition>();
//            ret2.Should().HaveCount(4);
//            ret2.Select(x => x.Company).Should().BeEquivalentTo("C", "D", "E", "F");
//        }

//        [Fact]
//        public async void SnapshotImportOnPartitionedData_AndThenRegularImportTest()
//        {
//            //Arrange
//            await InitialImport();

//            var recordsForCompanyA = @"@@PartitionedRecordValueType
//Value,Company
//5,A";

//            var recordsForCompanyA2 = @"@@PartitionedRecordValueType
//Value,Company
//6,A";

//            //Act
//            //snapshot 
//            await ImportVariable.FromString(recordsForCompanyA)
//                                .WithType<PartitionedRecordValueType>()
//                                .SnapshotMode()
//                                .ExecuteAsync();
//            //not snapshot
//            await ImportVariable.FromString(recordsForCompanyA2)
//                                .WithType<PartitionedRecordValueType>()
//                                .ExecuteAsync();

//            //Assert
//            await Workspace.Partition.SetAsync<string>("A", ByCompany);
//            var ret = Workspace.GetItems<PartitionedRecordValueType>();
//            ret.Should().HaveCount(2);
//            ret.Select(x => x.Value).Should().BeEquivalentTo(new[] { 5, 6 });


//            await Workspace.Partition.SetAsync<string>("B", ByCompany);
//            ret = Workspace.GetItems<PartitionedRecordValueType>();
//            ret.Should().HaveCount(2);
//        }

//        [Fact]
//        public async void SnapshotImportOnPartitionedData_ManyPartitionsTest()
//        {
//            await InitialImport();

//            var recordsForCompanies = @"@@PartitionedRecordValueType
//Value,Company
//5,A
//6,B";

//            //Act
//            await ImportVariable.FromString(recordsForCompanies)
//                                .WithType<PartitionedRecordValueType>()
//                                .SnapshotMode()
//                                .ExecuteAsync();

//            //Assert
//            await Workspace.Partition.SetAsync<string>("A", ByCompany);
//            var ret = Workspace.GetItems<PartitionedRecordValueType>();
//            ret.Should().ContainSingle().Which.Value.Should().Be(5);


//            await Workspace.Partition.SetAsync<string>("B", ByCompany);
//            ret = Workspace.GetItems<PartitionedRecordValueType>();
//            ret.Should().ContainSingle().Which.Value.Should().Be(6);
//        }

//        [Fact]
//        public async void SnapshotImportOnPartitionedData_NoPartitionInInputTest()
//        {
//            await InitialImport();

//            await Workspace.Partition.SetAsync<string>("A", ByCompany);

//            var zeroInstances = @"@@PartitionedRecordValueType
//Value
//5";

//            //Act
//            await ImportVariable.FromString(zeroInstances)
//                                .WithType<PartitionedRecordValueType>()
//                                .SnapshotMode()
//                                .ExecuteAsync();

//            //Assert
//            await Workspace.Partition.SetAsync<string>("A", ByCompany);
//            var ret = Workspace.GetItems<PartitionedRecordValueType>();
//            ret.Should().ContainSingle().Which.Value.Should().Be(5);

//            await Workspace.Partition.SetAsync<string>("B", ByCompany);
//            ret = Workspace.GetItems<PartitionedRecordValueType>();
//            ret.Should().HaveCount(2);
//        }

//        [Fact]
//        public async void SnapshotImportOnPartitionedData_NoPartitionInInput_NoPartitionSetTest()
//        {
//            await InitialImport();


//            var zeroInstances = @"@@PartitionedRecordValueType
//Value
//5";

//            await Workspace.Partition.SetAsync<string>(null, ByCompany);

//            //Act
//            var log = await ImportVariable.FromString(zeroInstances)
//                                .WithType<PartitionedRecordValueType>()
//                                .SnapshotMode()
//                                .ExecuteAsync();

//            log.Status.Should().Be(ActivityLogStatus.Failed);
//            log.Errors().Should().ContainSingle().Which.Should().BeOfType<LogMessage>().Which.Message.Should().Contain("Partition key must be set.");

//            //Assert
//            await Workspace.Partition.SetAsync<string>("A", ByCompany);
//            var ret = Workspace.GetItems<PartitionedRecordValueType>()t.Should().HaveCount(2);

//            await Workspace.Partition.SetAsync<string>("B", ByCompany);
//            ret = await Workspace.Query<PartitionedRecordValueType>().ToListAsync();
//            ret.Should().HaveCount(2);
//        }

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
    
