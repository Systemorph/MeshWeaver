using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data.TestDomain;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using Xunit.Abstractions;

namespace OpenSmc.Data.Test;

public class DataSynchronizationTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data.FromConfigurableDataSource(
                    "ReferenceData",
                    dataSource =>
                        dataSource
                            .WithType<LineOfBusiness>(t =>
                                t.WithInitialData(TestData.LinesOfBusiness)
                            )
                            .WithType<BusinessUnit>(t => t.WithInitialData(TestData.BusinessUnits))
                )
            );
    }

    protected override MessageHubConfiguration ConfigureClient(
        MessageHubConfiguration configuration
    )
    {
        return base.ConfigureClient(configuration)
            .AddData(data =>
                data.FromHub(
                    new HostAddress(),
                    dataSource => dataSource.WithType<BusinessUnit>().WithType<LineOfBusiness>()
                )
            );
    }

    private const string NewName = nameof(NewName);

    [HubFact]
    public async Task TestBasicSynchronization()
    {
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();
        var businessUnits = await workspace.GetObservable<BusinessUnit>().FirstAsync();
        businessUnits.Should().HaveCountGreaterThan(1);

        var businessUnit = businessUnits.First();
        var oldName = businessUnit.DisplayName;
        businessUnit = businessUnit with { DisplayName = NewName };
        client.Post(new UpdateDataRequest(new[] { businessUnit }));

        // get the data from the client again
        var loadedInstance = await workspace
            .GetObservable<BusinessUnit>(businessUnit.SystemName)
            .FirstAsync(x => x.DisplayName != oldName);
        loadedInstance.Should().Be(businessUnit);

        // data sync is happening async in order not to block the client ==> we need to give it
        // some grace time for the sync to happen
        await Task.Delay(100);

        var hostWorkspace = GetHost().ServiceProvider.GetRequiredService<IWorkspace>();
        loadedInstance = await hostWorkspace
            .GetObservable<BusinessUnit>(businessUnit.SystemName)
            .FirstAsync(); // we query directly the host to see that data sync worked
        loadedInstance.Should().Be(businessUnit);
    }
}
