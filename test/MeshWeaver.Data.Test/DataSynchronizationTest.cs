using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit.Abstractions;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Tests for data synchronization between host and client workspaces
/// </summary>
public class DataSynchronizationTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Configures the host with test data sources for LineOfBusiness and BusinessUnit
    /// </summary>
    /// <param name="configuration">The configuration to modify</param>
    /// <returns>The modified configuration</returns>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data.AddSource(
                    dataSource =>
                        dataSource
                            .WithType<LineOfBusiness>(t =>
                                t.WithInitialData(TestData.LinesOfBusiness)
                            )
                            .WithType<BusinessUnit>(t => t.WithInitialData(TestData.BusinessUnits))
                )
            );
    }

    /// <summary>
    /// Configures the client to connect to host data sources
    /// </summary>
    /// <param name="configuration">The configuration to modify</param>
    /// <returns>The modified configuration</returns>
    protected override MessageHubConfiguration ConfigureClient(
        MessageHubConfiguration configuration
    )
    {
        return base.ConfigureClient(configuration)
            .AddData(data =>
                data.AddHubSource(
                    new HostAddress(),
                    dataSource => dataSource.WithType<BusinessUnit>().WithType<LineOfBusiness>()
                )
            );
    }

    private const string NewName = nameof(NewName);

    /// <summary>
    /// Tests basic data synchronization from client to host workspace
    /// </summary>
    [HubFact]
    public async Task TestBasicSynchronization()
    {
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();
        var businessUnits = await workspace.GetObservable<BusinessUnit>().Timeout(3.Seconds()).FirstAsync();
        businessUnits.Should().HaveCountGreaterThan(1);

        var businessUnit = businessUnits.First();
        var oldName = businessUnit.DisplayName;
        businessUnit = businessUnit with { DisplayName = NewName };
        client.Post(new DataChangeRequest { Updates = [businessUnit] });

        // get the data from the client again
        var loadedInstance = await workspace
            .GetObservable<BusinessUnit>(businessUnit.SystemName)
            .Timeout(3.Seconds())
            .FirstAsync(x => x!.DisplayName != oldName);
        loadedInstance.Should().Be(businessUnit);

        // data sync is happening async in order not to block the client ==> we need to give it
        // some grace time for the sync to happen
        await Task.Delay(100);

        var hostWorkspace = GetHost().ServiceProvider.GetRequiredService<IWorkspace>();
        loadedInstance = await hostWorkspace
            .GetObservable<BusinessUnit>(businessUnit.SystemName)
            .Timeout(3.Seconds())
            .FirstAsync(); // we query directly the host to see that data sync worked
        loadedInstance.Should().Be(businessUnit);
    }
    /// <summary>
    /// Tests data reduction functionality after updates are applied
    /// </summary>
    [HubFact]
    public async Task ReduceAfterUpdate()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var businessUnits = await workspace.GetStream<BusinessUnit>()!.Timeout(3.Seconds()).FirstAsync();
        businessUnits.Should().HaveCountGreaterThan(1);

        var businessUnit = businessUnits.First();
        var oldName = businessUnit.DisplayName;
        businessUnit = businessUnit with { DisplayName = NewName };
        host.Post(new DataChangeRequest { Updates = [businessUnit] });

        // get the data from the client again
        var loadedInstance = await workspace
            .GetObservable<BusinessUnit>(businessUnit.SystemName)
            .Timeout(3.Seconds())
            .FirstAsync(x => x!.DisplayName != oldName);
        loadedInstance.Should().Be(businessUnit);

        var linesOfBusiness = await workspace.GetStream<LineOfBusiness>()!.Timeout(3.Seconds()).FirstAsync();
        linesOfBusiness.Should().NotBeNullOrEmpty();
    }
}
