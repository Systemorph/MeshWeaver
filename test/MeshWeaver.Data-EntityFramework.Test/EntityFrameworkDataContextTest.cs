using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Data;
using MeshWeaver.DataStorage.EntityFramework;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Data_EntityFramework.Test;

public class EntityFrameworkDataContextTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string ConnectionString =
        $"Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;Database=Testing";

    public record MyDataRecord(
        [property: Key] string Id,
        string BusinessUnit,
        string LoB,
        double Value
    );

    private const string SqlServer = nameof(SqlServer);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data.FromEntityFramework(
                    SqlServer,
                    database => database.UseSqlServer(ConnectionString),
                    dataSource =>
                        dataSource
                        // here, all the entity types need to be listed as exposed to framework
                        // if desired, we can add mapping logic between data source and data hub, please request on GitHub.
                        .WithType<MyDataRecord>()
                )
            );
    }

    protected override MessageHubConfiguration ConfigureClient(
        MessageHubConfiguration configuration
    )
    {
        return base.ConfigureClient(configuration)
            .AddData(data =>
                data.FromHub(new HostAddress(), dataSource => dataSource.WithType<MyDataRecord>())
            );
    }

#if CIRun
    [Fact(Skip = "Hangs")]
#else
    [Fact(Timeout = 5000)]
#endif
    public async Task TestBasicCrud()
    {
        var client = GetClient();
        var myDataRecord = new MyDataRecord("1", "BU1", "Lob1", 10);

        var workspace = client.GetWorkspace();

        var dataChanged = await client.AwaitResponse(
            new UpdateDataRequest(new[] { myDataRecord }),
            o => o.WithTarget(new ClientAddress())
        );


        // check on data hub
        var hostWorkspace = GetHost().GetWorkspace();
        var items = await hostWorkspace.GetObservable<MyDataRecord>().FirstAsync();
        ;
        items.Should().ContainSingle().Which.Should().Be(myDataRecord);

        await DisposeAsync();
        await InitializeAsync();

        client = GetClient();
        items = await client.GetWorkspace().GetObservable<MyDataRecord>().FirstAsync();
        items.Should().ContainSingle().Which.Should().Be(myDataRecord);
    }
}
