using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data;
using OpenSmc.DataStorage.EntityFramework;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Data_EntityFramework.Test
{
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
                    .WithDataSource(SqlServer, 
                            dataSource => dataSource
                                // here, all the entity types need to be listed as exposed to framework
                                // if desired, we can add mapping logic between data source and data hub, please request on GitHub.
                                .WithType<MyDataRecord>(),
                            ds =>
                            ds.GetEntityFrameworkStorage(database => database.UseSqlServer(ConnectionString))
                        )
                );
        }


        [Fact]
        public async Task TestBasicCrud()
        {
            var client = GetClient();
            var myDataRecord = new MyDataRecord("1", "BU1", "Lob1", 10);
            var dataChanged = 
                await client.AwaitResponse(new UpdateDataRequest(new [] { myDataRecord }),
                    o => o.WithTarget(new HostAddress()));

            dataChanged.Message.Version.Should().Be(1);


            // this is code which emulates execution on server side, as we are working with physical services.
            // when doing this, make sure you are a plugin inside the clock of the server you are accessing.
            var workspace = GetHost().ServiceProvider.GetRequiredService<IWorkspace>();
            var dataSource = workspace.Context.GetDataSource(SqlServer);
            var storage = ((DataSourceWithStorage)dataSource).Storage;


            // this is usually not to be written ==> just test code.
            await using var transaction = await storage.StartTransactionAsync();
            var inStorage = await storage.Query<MyDataRecord>().ToArrayAsync();
            inStorage.Should().ContainSingle()
                .Which.Should().Be(myDataRecord);


        }
    }
}
