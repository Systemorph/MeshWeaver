using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

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
                    CreateHostAddress(),
                    dataSource => dataSource.WithType<BusinessUnit>().WithType<LineOfBusiness>()
                )
            );
    }

    private const string NewName = nameof(NewName);

    /// <summary>
    /// Tests basic data synchronization from client to host workspace
    /// </summary>
    [HubFact]
    public void TestBasicSynchronization()
    {
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();
        var businessUnits = workspace.GetObservable<BusinessUnit>().Should().Within(3.Seconds()).Emit();
        businessUnits.Should().HaveCountGreaterThan(1);

        var businessUnit = businessUnits.First();
        var oldName = businessUnit.DisplayName;
        businessUnit = businessUnit with { DisplayName = NewName };
        client.Post(new DataChangeRequest { Updates = [businessUnit] });

        // get the data from the client again
        var loadedInstance = workspace
            .GetObservable<BusinessUnit>(businessUnit.SystemName)
            .Should().Within(3.Seconds())
            .Match(x => x!.DisplayName != oldName);
        loadedInstance.Should().Be(businessUnit);

        // The host workspace's stream surfaces the synced update reactively —
        // Match(predicate) with a 3 s timeout waits for the DisplayName
        // change to land. No grace-period Task.Delay needed (was a holdover
        // from a non-reactive era).
        var hostWorkspace = GetHost().ServiceProvider.GetRequiredService<IWorkspace>();
        loadedInstance = hostWorkspace
            .GetObservable<BusinessUnit>(businessUnit.SystemName)
            .Should().Within(3.Seconds())
            .Match(x => x!.DisplayName != oldName);
        loadedInstance.Should().Be(businessUnit);
    }
    /// <summary>
    /// Tests data reduction functionality after updates are applied
    /// </summary>
    [HubFact]
    public void ReduceAfterUpdate()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var businessUnits = workspace.GetStream<BusinessUnit>()!.Should().Within(3.Seconds()).Emit();
        businessUnits.Should().HaveCountGreaterThan(1);

        var businessUnit = businessUnits!.First();
        var oldName = businessUnit.DisplayName;
        businessUnit = businessUnit with { DisplayName = NewName };
        host.Post(new DataChangeRequest { Updates = [businessUnit] });

        // get the data from the client again
        var loadedInstance = workspace
            .GetObservable<BusinessUnit>(businessUnit.SystemName)
            .Should().Within(3.Seconds())
            .Match(x => x!.DisplayName != oldName);
        loadedInstance.Should().Be(businessUnit);

        var linesOfBusiness = workspace.GetStream<LineOfBusiness>()!.Should().Within(3.Seconds()).Emit();
        linesOfBusiness.Should().NotBeEmpty();
    }
}
