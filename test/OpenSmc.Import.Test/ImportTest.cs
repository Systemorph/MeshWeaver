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

public class ImportTest(ITestOutputHelper output) : HubTestBase(output)
{
    public record TransactionalData([property: Key] string Id, string LoB, string BusinessUnit, double Value);

    public record LineOfBusiness(string SystemName, string DisplayName);
    public record BusinessUnit(string SystemName, string DisplayName);
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {

        return base.ConfigureHost(configuration)
                .AddData(
                    data => data.WithDataSource
                    (
                        nameof(DataSource),
                        source => source
                            .WithType<TransactionalData>(type => type
                                .WithBackingCollection(
                                    new Dictionary<object, TransactionalData>
                                    {
                                        { "1", new TransactionalData("1", "1", "1", 7) }

                                    })
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
        var importResponse = await client.AwaitResponse(new ImportRequest(VanillaCsv), o => o.WithTarget(new HostAddress()));
        importResponse.Message.Log.Status.Should().Be(ActivityLogStatus.Succeeded);

        var items = await client.AwaitResponse(new GetManyRequest<TransactionalData>(),
            o => o.WithTarget(new HostAddress()));
        items.Message.Items.Should().HaveCount(4)
            .And.ContainSingle(i => i.Id == "1")
            .Which.Value.Should().Be(1); // we started with 7....

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