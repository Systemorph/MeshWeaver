using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using OpenSmc.Activities;
using OpenSmc.Data;
using OpenSmc.Hub.Fixture;
using OpenSmc.Import;
using OpenSmc.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Import.Test;


// this is production code!
public static class ImportTestExtensions
{
    public static IEnumerable<Type> ReferenceDataDomain()
        =>
        [
            typeof(ImportTest.LineOfBusiness),
            typeof(ImportTest.BusinessUnit),
        ];

    public static IEnumerable<Type> TransactionalDataDomain()
        =>
        [
            typeof(ImportTest.TransactionalData),
        ];

    public static DataSource ConfigureReferenceData(this DataSource dataSource)
        => ReferenceDataDomain().Aggregate(dataSource, (ds, t) => ds.WithType(t));
    public static DataSource ConfigureTransactionalData(this DataSource dataSource)
        => TransactionalDataDomain().Aggregate(dataSource, (ds, t) => ds.WithType(t));

}


public class ImportTest(ITestOutputHelper output) : HubTestBase(output)
{
    public record TransactionalData([property: Key] string Id, string LoB, string BusinessUnit, double Value);

    public record LineOfBusiness(string SystemName, string DisplayName);
    public record BusinessUnit(string SystemName, string DisplayName);

    private static readonly TransactionalData[] InitialTransactionalData =
    {
        new("1", "1", "1", 7),
        new("2", "1", "3", 2),
    };

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {

        return base.ConfigureHost(configuration)
                .AddData(
                    data => data.WithDataSource
                    (
                        nameof(DataSource),
                        source => source
                    //.ConfigureTransactionalData()
                    .WithType<TransactionalData>(type => type
                        .WithBackingCollection(InitialTransactionalData.ToDictionary(x => (object)x.Id))
                    )
                    )
                )
                .AddImport(import => import)
            ;
    }

    [Fact]
    public async Task TestVanilla()
    {
        var client = GetClient();
        var importRequest = new ImportRequest(VanillaCsv); 
        var importResponse = await client.AwaitResponse(importRequest, o => o.WithTarget(new HostAddress()));
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Succeeded);

        var items = await client.AwaitResponse(new GetManyRequest<TransactionalData>(),
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
}