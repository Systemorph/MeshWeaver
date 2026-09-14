using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data.Persistence;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// 🚨 <b>Starting a data source mints exactly the <c>sync/</c> hubs of the streams its
/// <c>Initialize()</c> opens — and no spare.</b> Systemorph/MeshWeaver#4300, the partitioned half.
///
/// <para>Every <c>Initialize()</c> override used to call <c>GetStream(reference)</c> for the side
/// effect of opening a primary stream and DISCARD the reduced stream it returns — an uncached
/// configured reduce, hence a second <c>SynchronizationStream</c> and a second hosted <c>sync/</c>
/// hub per stream opened, reachable by nobody and alive until the owning hub dies.
/// <c>ReadPathStreamMintingTest.ADataSourceStart_MintsOnlyItsPrimaryStream</c> pins the
/// unpartitioned type-source path; the three overrides that path does not reach are pinned here,
/// each by an EXACT count taken after <c>Started</c>, which settles only once the init turn has
/// run every <c>Initialize()</c>:</para>
/// <list type="bullet">
/// <item><description><c>TypeSourceBasedPartitionedDataSource.Initialize</c> opens ONE stream (the
/// null partition) — measured beside an unpartitioned source on the same host, so the host's
/// population is the number of sources.</description></item>
/// <item><description><c>PartitionedHubDataSource.Initialize</c> opens one remote mirror PER
/// INITIALIZED PARTITION — two partitions, two hubs on the client.</description></item>
/// <item><description><c>UnpartitionedHubDataSource.Initialize</c> is the same shape with one
/// partition; it is reached by every fixture in this project that calls <c>AddHubSource</c>, and
/// its count is the client-side reading of <c>EvictedUnleasedStreamRetentionTest</c>'s
/// baseline.</description></item>
/// </list>
/// <para>Before the fix each count read DOUBLE (2 → 4, 2 → 4): one primary plus one discarded
/// reduce per stream opened. A start that opens N streams and leaves N hubs is the whole claim.</para>
/// </summary>
public class DataSourceStartMintingTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>A partitioned in-memory type-source data source — the abstract generic needs a
    /// concrete self type, exactly as production sources declare one.</summary>
    private record PartitionedLinesOfBusiness(object Id, IWorkspace Workspace)
        : GenericPartitionedDataSource<PartitionedLinesOfBusiness, string>(Id, Workspace)
    {
        // The abstract generic leaves the stream to its implementer; this is the unpartitioned
        // override's shape (type-source-driven initialization), so the null-partition stream
        // reaches Started the same way.
        protected override ISynchronizationStream<EntityStore> CreateStream(StreamIdentity identity)
            => CreateStream(identity, c => c.WithInitialization(GetInitialValue));
    }

    /// <summary>The two owners the client's partitioned hub source mirrors — both are hosts the
    /// router builds on demand with <see cref="ConfigureHost"/>, so both serve BusinessUnit.</summary>
    private static readonly Address[] Partitions = [CreateHostAddress("1"), CreateHostAddress("2")];

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data
                .AddSource(src => src
                    .WithType<BusinessUnit>(t => t.WithInitialData(TestData.BusinessUnits)))
                // Captures data.Workspace exactly as AddSource does: the builder runs INSIDE the
                // workspace's constructor, so resolving IWorkspace from the hub here recurses.
                .WithDataSource(_ => new PartitionedLinesOfBusiness(
                        DataExtensions.DefaultId, data.Workspace)
                    .WithType<LineOfBusiness>(
                        lob => lob.SystemName,
                        ts => (IPartitionedTypeSource)((ITypeSource)ts).WithInitialData(
                            _ => Observable.Return(TestData.LinesOfBusiness.Cast<object>())))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddData(data => data.AddPartitionedHubSource<Address>(ds => ds
                .WithType<BusinessUnit>(_ => Partitions[0])
                .InitializingPartitions(Partitions.Cast<object>())));

    [HubFact]
    public async Task APartitionedTypeSourceStart_MintsOnlyItsPrimaryStream()
    {
        var host = GetHost();
        await host.Started.WaitAsync(TestTimeouts.Convergence, TestContext.Current.CancellationToken);

        var sources = host.GetWorkspace().DataContext.DataSources.ToArray();
        var live = LiveSyncHubs(host);
        Output.WriteLine($"DIAG partitioned-start: dataSources={sources.Length} ownerSyncHubs={live}");

        sources.Should().HaveCount(2, "one unpartitioned and one partitioned type-source data source");
        sources.OfType<PartitionedLinesOfBusiness>().Should().HaveCount(1,
            "the partitioned override is the one under test, so the fixture must actually carry it");
        live.Should().Be(sources.Length,
            "each start opens its ONE primary stream — the partitioned source's null-partition "
            + "stream — and the full-reference reduce Initialize() used to discard is gone");
    }

    [HubFact]
    public async Task APartitionedHubSourceStart_MintsOneMirrorPerInitializedPartition()
    {
        var client = GetClient();
        await client.Started.WaitAsync(TestTimeouts.Convergence, TestContext.Current.CancellationToken);

        var live = LiveSyncHubs(client);
        Output.WriteLine($"DIAG partitioned-hub-start: partitions={Partitions.Length} clientSyncHubs={live}");

        live.Should().Be(Partitions.Length,
            "PartitionedHubDataSource.Initialize opens one remote mirror per initialized partition "
            + "and nothing else — before the fix each partition also left a discarded reduce's hub");
    }

    /// <summary>Same metric as <c>ReadPathStreamMintingTest</c>: one hosted <c>sync/{ClientId}</c>
    /// hub per <c>SynchronizationStream</c>, so the hosted-hub collection filtered to
    /// <see cref="SynchronizationAddress.AddressType"/> IS the count of streams a hub keeps alive.</summary>
    private static int LiveSyncHubs(IMessageHub hub) =>
        hub.ServiceProvider.GetRequiredService<HostedHubsCollection>()
            .Hubs.Count(h => h.Address.Type == SynchronizationAddress.AddressType);
}
