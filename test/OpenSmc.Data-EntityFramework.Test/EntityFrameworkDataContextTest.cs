using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data;
using OpenSmc.DataStorage.EntityFramework;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Data_EntityFramework.Test;

public class EntityFrameworkDataContextTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string ConnectionString = $"Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;Database=Testing";
    public record MyDataRecord([property: Key] string Id, string BusinessUnit, string LoB, double Value);


    private const string SqlServer = nameof(SqlServer);
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base
            .ConfigureHost(configuration)
                    
            .AddData(data => data
                .FromEntityFramework(SqlServer, 
                    database => database.UseSqlServer(ConnectionString),
                    dataSource => dataSource

                        // here, all the entity types need to be listed as exposed to framework
                        // if desired, we can add mapping logic between data source and data hub, please request on GitHub.
                        .WithType<MyDataRecord>()
                )
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
        var hostAddress = new HostAddress();
        var dataChanged = 
            await client.AwaitResponse(new UpdateDataRequest(new [] { myDataRecord }),
                o => o.WithTarget(hostAddress));

        dataChanged.Message.Version.Should().Be(1);

        // check on data hub
        var items = await GetItems(client, hostAddress, myDataRecord);
        items.Should().ContainSingle().Which.Should().Be(myDataRecord);

        await DisposeAsync();
        await InitializeAsync();

        client = GetClient();
        items = await GetItems(client, hostAddress, myDataRecord);
        items.Should().ContainSingle().Which.Should().Be(myDataRecord);
    }

    


        
    private static async Task<IReadOnlyCollection<MyDataRecord>> GetItems(IMessageHub client, object address, MyDataRecord myDataRecord)
    {
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();
        var items = await workspace.GetObservable<MyDataRecord>().FirstAsync();

        return items;
    }
}

