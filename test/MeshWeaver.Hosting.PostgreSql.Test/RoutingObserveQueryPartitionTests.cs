using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Verifies that <see cref="RoutingMeshQueryProvider.ObserveQuery{T}"/> discovers new
/// partitions via <see cref="RoutingPersistenceServiceCore.DiscoverNewProviders"/> at
/// subscription time and includes their nodes in the combined Initial result.
///
/// Uses a real PostgreSQL container (shared via the "PostgreSql" collection fixture) and
/// a <see cref="FilteredPartitionedStoreFactory"/> to limit discovery to schemas created
/// by each test, preventing cross-test contamination.
/// </summary>
[Collection("PostgreSql")]
public class RoutingObserveQueryPartitionTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    // nodeType unique to this test class — avoids picking up data from other tests.
    private const string TestNodeType = "TestRoutingPartition";

    public RoutingObserveQueryPartitionTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Two PostgreSQL schemas are created, each containing one node.
    /// ObserveQuery with no path filter should fan out to both via
    /// DiscoverNewProviders and emit a single combined Initial that
    /// contains both nodes.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ObserveQuery_GlobalFanOut_IncludesNodesFromBothDiscoveredPartitions()
    {
        var ct = TestContext.Current.CancellationToken;

        var (dsP1, adapterP1) = await _fixture.CreateSchemaAdapterAsync("rout_p1", null, ct);
        var (dsP2, adapterP2) = await _fixture.CreateSchemaAdapterAsync("rout_p2", null, ct);

        await adapterP1.WriteAsync(new MeshNode("alpha-node", "RoutP1")
        {
            Name = "Alpha", NodeType = TestNodeType, State = MeshNodeState.Active
        }, _options, ct);

        await adapterP2.WriteAsync(new MeshNode("beta-node", "RoutP2")
        {
            Name = "Beta", NodeType = TestNodeType, State = MeshNodeState.Active
        }, _options, ct);

        var factory = new FilteredPartitionedStoreFactory(
            new PostgreSqlPartitionedStoreFactory(
                _fixture.DataSource, _fixture.ConnectionString, _fixture.Options),
            new HashSet<string>(["rout_p1", "rout_p2"], StringComparer.OrdinalIgnoreCase));

        var router   = new RoutingPersistenceServiceCore(factory);
        var provider = new RoutingMeshQueryProvider(router);

        var changes = new List<QueryResultChange<MeshNode>>();
        // WellKnownUsers.System → GetEffectiveUserId returns "" → SQL access-control clause skipped
        var request = MeshQueryRequest.FromQuery($"nodeType:{TestNodeType}", WellKnownUsers.System);

        using var sub = provider.ObserveQuery<MeshNode>(request, _options)
            .Subscribe(c => changes.Add(c));

        await WaitForInitial(changes, ct);

        var initial = changes.Where(c => c.ChangeType == QueryChangeType.Initial).ToList();
        initial.Should().ContainSingle("fan-out combines all partition Initials into one emission");
        initial[0].Items.Should().Contain(n => n.Id == "alpha-node", "rout_p1 node must be in Initial");
        initial[0].Items.Should().Contain(n => n.Id == "beta-node",  "rout_p2 node must be in Initial");

        await dsP1.DisposeAsync();
        await dsP2.DisposeAsync();
    }

    /// <summary>
    /// A schema is created and populated with data before the subscription.
    /// The partition is NOT pre-seeded in QueryProviders — it is discovered lazily by
    /// DiscoverNewProviders at subscription time. The Initial result must contain the node.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ObserveQuery_NewPartitionNotYetProvisioned_DiscoveredAndIncludedInInitial()
    {
        var ct = TestContext.Current.CancellationToken;

        var (dsP3, adapterP3) = await _fixture.CreateSchemaAdapterAsync("rout_p3", null, ct);
        await adapterP3.WriteAsync(new MeshNode("gamma-node", "RoutP3")
        {
            Name = "Gamma", NodeType = TestNodeType, State = MeshNodeState.Active
        }, _options, ct);

        // Router starts with an empty QueryProviders dictionary
        var factory = new FilteredPartitionedStoreFactory(
            new PostgreSqlPartitionedStoreFactory(
                _fixture.DataSource, _fixture.ConnectionString, _fixture.Options),
            new HashSet<string>(["rout_p3"], StringComparer.OrdinalIgnoreCase));

        var router = new RoutingPersistenceServiceCore(factory);
        router.QueryProviders.Should().BeEmpty("no partition has been provisioned yet");

        var provider = new RoutingMeshQueryProvider(router);
        var changes = new List<QueryResultChange<MeshNode>>();
        var request = MeshQueryRequest.FromQuery($"nodeType:{TestNodeType}", WellKnownUsers.System);

        using var sub = provider.ObserveQuery<MeshNode>(request, _options)
            .Subscribe(c => changes.Add(c));

        await WaitForInitial(changes, ct);

        var initial = changes.Where(c => c.ChangeType == QueryChangeType.Initial).ToList();
        initial.Should().ContainSingle();
        initial[0].Items.Should().Contain(n => n.Id == "gamma-node",
            "DiscoverNewProviders must provision rout_p3 at subscription time and include its node");

        await dsP3.DisposeAsync();
    }

    /// <summary>
    /// When the query has a path that resolves to a specific partition,
    /// ObserveQuery routes directly to that provider without fan-out.
    /// Only nodes from the matching partition are returned.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ObserveQuery_PathScopedToSinglePartition_ReturnsOnlyThatPartitionsNodes()
    {
        var ct = TestContext.Current.CancellationToken;

        var (dsP4, adapterP4) = await _fixture.CreateSchemaAdapterAsync("rout_p4", null, ct);
        var (dsP5, adapterP5) = await _fixture.CreateSchemaAdapterAsync("rout_p5", null, ct);

        await adapterP4.WriteAsync(new MeshNode("delta-node", "RoutP4")
        {
            Name = "Delta", NodeType = TestNodeType, State = MeshNodeState.Active
        }, _options, ct);

        await adapterP5.WriteAsync(new MeshNode("epsilon-node", "RoutP5")
        {
            Name = "Epsilon", NodeType = TestNodeType, State = MeshNodeState.Active
        }, _options, ct);

        var factory = new FilteredPartitionedStoreFactory(
            new PostgreSqlPartitionedStoreFactory(
                _fixture.DataSource, _fixture.ConnectionString, _fixture.Options),
            new HashSet<string>(["rout_p4", "rout_p5"], StringComparer.OrdinalIgnoreCase));

        // Pre-provision both partitions so routing can resolve "RoutP4" → provider
        var router = new RoutingPersistenceServiceCore(factory);
        await router.InitializeAsync(ct);

        var provider = new RoutingMeshQueryProvider(router);
        var changes = new List<QueryResultChange<MeshNode>>();

        // Scope query to RoutP4 only
        var request = MeshQueryRequest.FromQuery(
            $"namespace:RoutP4 nodeType:{TestNodeType}", WellKnownUsers.System);

        using var sub = provider.ObserveQuery<MeshNode>(request, _options)
            .Subscribe(c => changes.Add(c));

        await WaitForInitial(changes, ct);

        var initial = changes.Where(c => c.ChangeType == QueryChangeType.Initial).ToList();
        initial.Should().ContainSingle();
        initial[0].Items.Should().Contain(n => n.Id == "delta-node",   "delta-node is in RoutP4");
        initial[0].Items.Should().NotContain(n => n.Id == "epsilon-node", "epsilon-node is in RoutP5, out of scope");

        await dsP4.DisposeAsync();
        await dsP5.DisposeAsync();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task WaitForInitial(
        List<QueryResultChange<MeshNode>> changes,
        CancellationToken ct,
        int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!changes.Any(c => c.ChangeType == QueryChangeType.Initial) && DateTime.UtcNow < deadline)
            await Task.Delay(100, ct);
        await Task.Delay(200, ct); // settle
    }

    /// <summary>
    /// Wraps an inner factory and restricts partition discovery to a specific set of schemas.
    /// Prevents cross-test contamination when other test methods have left schemas behind.
    /// </summary>
    private sealed class FilteredPartitionedStoreFactory(
        IPartitionedStoreFactory inner,
        HashSet<string> allowed) : IPartitionedStoreFactory
    {
        public Task<PartitionedStore> CreateStoreAsync(string firstSegment, CancellationToken ct = default)
            => inner.CreateStoreAsync(firstSegment, ct);

        public async Task<IReadOnlyList<string>> DiscoverPartitionsAsync(CancellationToken ct = default)
        {
            var all = await inner.DiscoverPartitionsAsync(ct);
            return all.Where(s => allowed.Contains(s)).ToList();
        }
    }
}
