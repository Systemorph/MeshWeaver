using FluentAssertions;
using OpenSmc.Data;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using Xunit.Abstractions;
using static OpenSmc.Data.TestDomain.TestDomain;

namespace OpenSmc.Hub.Data.Test;

public class DataSynchronizationTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data => data.FromConfigurableDataSource("ReferenceData", dataSource => dataSource
                .ConfigureReferenceData()
            ));
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddData(
                data => data
                    .FromHub(new HostAddress(),
                        dataSource => dataSource
                            .WithType<BusinessUnit>()
                            .WithType<LineOfBusiness>()
                    )
            );
    }

    private const string NewName = nameof(NewName);

    [HubFact]
    public async Task TestBasicSynchronization()
    {
        var client = GetClient();
        var businessUnitResponse = 
            await client.AwaitResponse(new GetManyRequest<BusinessUnit>());
        businessUnitResponse.Message.Items.Should().HaveCountGreaterThan(1);

        var businessUnit = businessUnitResponse.Message.Items.First();
        businessUnit = businessUnit with { DisplayName = NewName };
        client.Post(new UpdateDataRequest(businessUnit));

        // get the data from the client again
        var getRequest = new GetRequest<BusinessUnit> { Id = businessUnit.SystemName };
        var loadedInstance = await client.AwaitResponse(getRequest);
        loadedInstance.Message.Should().Be(businessUnit);

        // data sync is happening async in order not to block the client ==> we need to give it
        // some grace time for the sync to happen
        await Task.Delay(100);

        
        loadedInstance = await client.AwaitResponse(getRequest, 
            o => o.WithTarget(new HostAddress())); // we query directly the host to see that data sync worked
        loadedInstance.Message.Should().Be(businessUnit);
    }
}