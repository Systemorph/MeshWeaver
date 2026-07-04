using System.Net.Http;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.InstanceSync.Test;

/// <summary>
/// The sync registry lifecycle: adding a syncing party materializes its config node under
/// <c>{space}/_Sync</c>, the coordinator starts/stops workers on registry events, and the
/// partition-admin provider describes sources. Plus the pure path/filter helpers.
/// </summary>
public class InstanceSyncRegistryTest(ITestOutputHelper output) : InstanceSyncTestBase(output)
{
    [Fact]
    public async Task AddSyncSource_creates_registry_node_and_starts_worker()
    {
        await CreateSpace("regspace");

        var node = await Sync.AddSyncSource("regspace", "partner").Timeout(30.Seconds()).ToTask();
        node.Path.Should().Be("regspace/_Sync/partner");
        node.NodeType.Should().Be(InstanceSyncService.ConfigNodeType);

        var listed = await Sync.WatchConfigNodes("regspace")
            .Where(nodes => nodes.Count == 1)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        listed[0].Path.Should().Be("regspace/_Sync/partner");

        // The coordinator reacts to the registry create event and starts the worker.
        await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .Where(_ => Coordinator.Workers.Any(w =>
                w.SpacePath == "regspace" && w.SourceId == "partner"))
            .FirstAsync().Timeout(30.Seconds()).ToTask();

        // Unconfigured source settles on NotConfigured — visible in the GUI.
        var cfg = await WaitForConfig("regspace", "partner",
            c => c.Status == InstanceSyncStatus.NotConfigured);
        cfg.IsConfigured.Should().BeFalse();

        var provider = new InstanceSyncPartitionSyncSourceProvider(Sync);
        provider.Describe(listed[0]).Should().Be("not configured");
        provider.CanRemove("regspace", listed[0]).Should().BeTrue();
    }

    [Fact]
    public async Task RemoveSyncSource_deletes_registration_and_stops_worker()
    {
        await CreateSpace("rmspace");
        await Sync.AddSyncSource("rmspace", "partner").Timeout(30.Seconds()).ToTask();
        await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .Where(_ => Coordinator.Workers.Any(w => w.SpacePath == "rmspace"))
            .FirstAsync().Timeout(30.Seconds()).ToTask();

        await Sync.RemoveSyncSource("rmspace", "partner").Timeout(30.Seconds()).ToTask();

        // The delete event stops (cancels) the worker and the registry lists empty.
        await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .Where(_ => Coordinator.Workers.All(w => w.SpacePath != "rmspace"))
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        var listed = await Sync.WatchConfigNodes("rmspace").FirstAsync().Timeout(30.Seconds()).ToTask();
        listed.Should().BeEmpty();
    }

    [Fact]
    public void RemapPath_maps_root_and_children_symmetrically()
    {
        InstanceSyncService.RemapPath("alpha", "alpha", "beta").Should().Be("beta");
        InstanceSyncService.RemapPath("alpha/doc/x", "alpha", "beta").Should().Be("beta/doc/x");
        InstanceSyncService.RemapPath("beta/doc/x", "beta", "alpha").Should().Be("alpha/doc/x");
    }

    [Fact]
    public void IsSyncablePath_excludes_satellites_and_foreign_paths()
    {
        InstanceSyncService.IsSyncablePath("alpha", "alpha").Should().BeTrue();
        InstanceSyncService.IsSyncablePath("alpha/doc", "alpha").Should().BeTrue();
        InstanceSyncService.IsSyncablePath("alpha/_Sync/p", "alpha").Should().BeFalse();
        InstanceSyncService.IsSyncablePath("alpha/doc/_Comment/1", "alpha").Should().BeFalse();
        InstanceSyncService.IsSyncablePath("other/doc", "alpha").Should().BeFalse();
        InstanceSyncService.IsSyncablePath("alphaville/doc", "alpha").Should().BeFalse();
    }

    [Fact]
    public void FilterSyncable_orders_parents_first_and_honours_sync_behavior()
    {
        var nodes = new List<MeshNode>
        {
            MeshNode.FromPath("s/a/deep"),
            MeshNode.FromPath("s"),
            MeshNode.FromPath("s/a"),
            MeshNode.FromPath("s/_Sync/cfg"),
            MeshNode.FromPath("s/excluded") with { SyncBehavior = SyncBehavior.ExcludeThisAndChildren },
            MeshNode.FromPath("s/excluded/child"),
        };
        var filtered = InstanceSyncService.FilterSyncable(nodes, "s");
        filtered.Select(n => n.Path).Should().Equal("s", "s/a", "s/a/deep");
    }

    [Fact]
    public void IsConnectivityError_classifies_transport_failures_only()
    {
        InstanceSyncWorker.IsConnectivityError(new HttpRequestException("refused")).Should().BeTrue();
        InstanceSyncWorker.IsConnectivityError(
            new InvalidOperationException("outer", new SocketException())).Should().BeTrue();
        InstanceSyncWorker.IsConnectivityError(new TimeoutException()).Should().BeTrue();
        InstanceSyncWorker.IsConnectivityError(new InvalidOperationException("node rejected")).Should().BeFalse();
        InstanceSyncWorker.IsConnectivityError(null).Should().BeFalse();
    }
}
