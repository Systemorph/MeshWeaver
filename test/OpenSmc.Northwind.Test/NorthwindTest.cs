using System.Reactive.Linq;
using OpenSmc.Data;
using OpenSmc.Data.Persistence;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Northwind.Test;

public class NorthwindTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return configuration.AddNorthwindReferenceDataFromFile();
    }

    protected override MessageHubConfiguration ConfigureClient(
        MessageHubConfiguration configuration
    ) =>
        base.ConfigureClient(configuration)
            .AddData(data =>
                data.FromHub(
                    new HostAddress(),
                    dataSource => dataSource.AddNorthwindReferenceData()
                )
            );

    [Fact]
    public async Task DataInitialization()
    {
        var client = GetClient();
        var response = await client.GetWorkspace().GetObservable<Category>().FirstAsync();
        Assert.NotNull(response);
    }
}
