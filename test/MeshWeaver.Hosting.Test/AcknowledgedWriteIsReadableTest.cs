using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// MeshWeaver#2899 — <b>a write that is acknowledged, versioned, and permanently unreadable</b>.
///
/// <para>The invariant these tests pin is one sentence, and the platform had no test for it:
/// <b>a write the storage layer acknowledged must be readable back through the seam readers
/// use.</b> Everything downstream of a write treats the acknowledgement as proof the node
/// landed — an import manifest records the file as done, a credential mint hands out a key, a
/// reactive wait completes — so an acknowledgement that asserts nothing about readability is the
/// most expensive defect shape the platform has. See
/// <c>Doc/Architecture/DurableButUnreadable</c>.</para>
///
/// <para><b>The concrete leak these tests were written against.</b> Six seams answer "which node
/// is served at this path", and every one of them honours <see cref="MeshNode.IsDefinitionOnly"/>
/// — the marker <c>serveFromPartition</c> stamps on a static entry to say *Postgres owns the row
/// at this path, I am only the type definition*:
/// <c>FindServedStaticNode</c>, <c>MeshDataSource.WithMeshNodes</c>,
/// <c>MessageHubGrain.TryResolveStaticNode</c>, the <c>CreateNode</c> existing-node probe,
/// <c>PartitionWriteGuardValidator</c>, and <c>StaticNodeQueryProvider</c>.
/// <see cref="StaticNodeStorageAdapter"/> — the STORAGE read seam — did not, and it is the one
/// <see cref="PersistenceService"/> consults. Because
/// <see cref="StaticNodePartitionStorageProvider"/> carries a fixed namespace it sorts into
/// <see cref="PersistenceService"/>'s FIRST band, ahead of every wildcard durable backend, while
/// being read-only and therefore absent from the write chain. So on a DB-synced static partition
/// the write landed in the durable store, was acknowledged, got its version-history row — and
/// every subsequent read returned the in-memory definition node instead. Durable, versioned,
/// unreadable.</para>
/// </summary>
public class AcknowledgedWriteIsReadableTest
{
    private const string Partition = "Doc";
    private const string NodePath = "Doc/Architecture";
    private static readonly JsonSerializerOptions Options = new();
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>A static repo contributor — the shape <c>AddMeshNodes</c> / a platform module registers.</summary>
    private sealed class ListProvider(params MeshNode[] nodes) : IStaticNodeProvider
    {
        public IEnumerable<MeshNode> GetStaticNodes() => nodes;
    }

    /// <summary>
    /// The version store, recording exactly what <c>VersionWritingStorageAdapter</c> hands it.
    /// Instance state only — a static dictionary would bleed across tests (see
    /// <c>Doc/Architecture/NoStaticState</c>).
    /// </summary>
    private sealed class RecordingVersionStore : IVersionQuery
    {
        private readonly ConcurrentDictionary<string, MeshNode> _written = new(StringComparer.OrdinalIgnoreCase);

        public bool HasVersionFor(string path) => _written.ContainsKey(path);

        public IObservable<MeshNodeVersion> GetVersions(string path) =>
            _written.TryGetValue(path, out var n)
                ? Observable.Return(new MeshNodeVersion(n.Path, n.Version, n.LastModified, n.LastModifiedBy, n.Name, n.NodeType))
                : Observable.Empty<MeshNodeVersion>();

        public IObservable<MeshNode?> GetVersion(string path, long version, JsonSerializerOptions options) =>
            Observable.Return(_written.GetValueOrDefault(path));

        public IObservable<MeshNode?> GetVersionBefore(string path, long beforeVersion, JsonSerializerOptions options) =>
            Observable.Return<MeshNode?>(null);

        public IObservable<MeshNode> WriteVersion(MeshNode node, JsonSerializerOptions options)
        {
            _written[node.Path] = node;
            return Observable.Return(node);
        }
    }

    private static MeshNode Static(string name, bool definitionOnly) =>
        MeshNode.FromPath(NodePath) with
        {
            NodeType = "Markdown",
            Name = name,
            IsDefinitionOnly = definitionOnly,
        };

    private static MeshNode Durable(string name) =>
        MeshNode.FromPath(NodePath) with
        {
            NodeType = "Markdown",
            Name = name,
            State = MeshNodeState.Active,
            Version = 1,
        };

    /// <summary>
    /// The PRODUCTION decorator chain, in the production order — the one
    /// <c>PersistenceExtensions.DecorateWithVersionWriting</c> builds:
    /// <c>SubtreeDeletionGuard → MonotonicWriteGuard → VersionWriting → PersistenceService</c>,
    /// over a static (read-only, fixed-namespace) provider and a writable wildcard one. The
    /// provider ordering is production's too: a fixed-namespace provider sorts ahead of every
    /// wildcard, so the static adapter answers reads first.
    /// </summary>
    private static (IStorageAdapter Adapter, RecordingVersionStore Versions) BuildStack(MeshNode staticNode)
    {
        var versions = new RecordingVersionStore();
        var providers = new IPartitionStorageProvider[]
        {
            new StaticNodePartitionStorageProvider(Partition, new ListProvider(staticNode)),
            new InMemoryPartitionStorageProvider(new InMemoryStorageAdapter()),
        };

        IStorageAdapter adapter = new SubtreeDeletionGuardStorageAdapter(
            new MonotonicWriteGuardStorageAdapter(
                new VersionWritingStorageAdapter(new PersistenceService(providers), versions)),
            registry: null);

        return (adapter, versions);
    }

    /// <summary>
    /// 🚨 THE GUARD. A write the storage layer acknowledged — a non-null emission from
    /// <see cref="IStorageAdapter.Write"/>, which is precisely what makes
    /// <c>VersionWritingStorageAdapter</c> record a version row and what
    /// <c>RequireClaimedWrite</c> lets through as <c>CreateNodeResponse.Ok</c> — MUST be
    /// readable back.
    ///
    /// <para>This is the #2899 signature expressed as an invariant: on the failing code the
    /// acknowledgement arrived, the version row was written, and <c>Read</c> answered with a
    /// different node forever. Nothing threw, nothing logged, nothing timed out.</para>
    /// </summary>
    [Fact]
    public async Task An_acknowledged_write_is_readable_back_through_the_same_adapter()
    {
        // serveFromPartition: the host declared this path DB-backed, so the static entry is a
        // DEFINITION and the durable row owns the path.
        var (adapter, versions) = BuildStack(Static("in-memory type definition", definitionOnly: true));

        var acknowledged = await adapter.Write(Durable("the durable row"), Options).Timeout(Budget);

        acknowledged.Should().NotBeNull(
            "a non-null emission is the try-then-claim ACCEPT sentinel — the write was acknowledged");
        versions.HasVersionFor(NodePath).Should().BeTrue(
            "the version-history row is chained off that same acknowledgement, so a caller that "
            + "checks history sees the write as landed");

        var readBack = await adapter.Read(NodePath, Options).Timeout(Budget);

        readBack.Should().NotBeNull("a write that was acknowledged must be readable back");
        readBack!.Name.Should().Be("the durable row",
            "the acknowledgement came from the durable store, so the durable row is what every "
            + "reader must see — a definition-only static entry is NOT the node at this path, and "
            + "shadowing the row here is exactly MeshWeaver#2899: acknowledged, versioned, invisible");
    }

    /// <summary>
    /// The same rule for the OTHER serve surfaces on the storage seam. A half-fix that excluded
    /// definition-only entries from <see cref="IStorageAdapter.Read"/> alone would leave
    /// <see cref="IStorageAdapter.Exists"/> answering <c>true</c> for a path with no durable row
    /// (the create handler's NodeType-existence probe reads it) and
    /// <c>ListChildPaths</c> / <c>FindBestPrefixMatch</c> still serving the definition — three
    /// readers disagreeing about one path, which is the defect class this repo keeps paying for.
    /// </summary>
    [Fact]
    public async Task A_definition_only_entry_is_served_by_NO_storage_surface()
    {
        var adapter = new StaticNodeStorageAdapter([Static("in-memory type definition", definitionOnly: true)]);

        (await adapter.Read(NodePath, Options).Timeout(Budget))
            .Should().BeNull("the durable row owns this path; the definition does not serve it");
        (await adapter.Exists(NodePath).Timeout(Budget))
            .Should().BeFalse("Exists must agree with Read — a true here makes the create path "
                              + "refuse a path that has no durable row");
        var (nodePaths, _) = await adapter.ListChildPaths(Partition).Timeout(Budget);
        nodePaths.Should().BeEmpty("a definition is not a child node of its partition");
        var (prefixNode, matched) = await adapter.FindBestPrefixMatch(NodePath, Options).Timeout(Budget);
        prefixNode.Should().BeNull("prefix routing must not resolve onto a definition-only entry");
        matched.Should().Be(0);
    }

    /// <summary>
    /// The negative half — the fix must not disarm the case it is NOT about. A static entry the
    /// host genuinely SERVES (no <c>serveFromPartition</c>, so not definition-only) still owns its
    /// path on every seam. That collision with a durable write is a *configuration* error the
    /// create path already refuses loudly by name
    /// (<c>StaticNodeProviderExtensions.DescribeStaticServeCollision</c>, #1209); it must keep
    /// behaving exactly as before.
    /// </summary>
    [Fact]
    public async Task A_genuinely_SERVED_static_node_still_owns_its_path()
    {
        var adapter = new StaticNodeStorageAdapter([Static("served static node", definitionOnly: false)]);

        var served = await adapter.Read(NodePath, Options).Timeout(Budget);

        served.Should().NotBeNull("a served static node is the node at its path");
        served!.Name.Should().Be("served static node");
        (await adapter.Exists(NodePath).Timeout(Budget)).Should().BeTrue();
    }
}
