using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// <see cref="NodeTypeDefinition.KeepsHistory"/> = <c>false</c> — a type whose content is transient
/// and must not outlive its current value (a name-check request carries submitted source lines until
/// it is answered). Before the opt-out existed, every write of every node was snapshotted by
/// <c>VersionWritingStorageAdapter</c> and nothing ever removed a snapshot, so a request's lines
/// stayed in history after the watcher had cleared them from the node.
///
/// <para>Exercised over the REAL version store (<see cref="FileSystemVersionStore"/> on a temp
/// directory) behind the real decorator, over a real <see cref="InMemoryStorageAdapter"/> — the
/// store's own <c>GetVersions</c> is the instrument, so a purge that did not happen cannot read as
/// one.</para>
/// </summary>
public sealed class NodeTypesThatKeepNoHistoryTest : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private const string EphemeralType = "Governance/Ephemeral";
    private const string OrdinaryType = "Governance/Ordinary";
    private const string BuiltInEphemeralType = "EphemeralBuiltIn";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "nohistory-" + Guid.NewGuid().ToString("N"));
    private readonly IoPoolRegistry pools = new();
    private readonly InMemoryStorageAdapter store = new();
    private readonly FileSystemVersionStore versions;
    private readonly IStorageAdapter adapter;

    public NodeTypesThatKeepNoHistoryTest()
    {
        versions = new FileSystemVersionStore(directory, pools);
        // A C#-registered (static) definition, the shape AddMeshNodes seeds.
        var builtIn = new MeshNode(BuiltInEphemeralType)
        {
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition { KeepsHistory = false },
        };
        adapter = new VersionWritingStorageAdapter(
            store, versions,
            staticNodeLookup: path => string.Equals(path, BuiltInEphemeralType, StringComparison.OrdinalIgnoreCase) ? builtIn : null,
            readOptions: () => Options);
    }

    public void Dispose()
    {
        pools.Dispose();
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    /// <summary>An in-mesh NodeType row, its content as JSON — exactly how a definition synced from a
    /// node repo (<c>"keepsHistory": false</c> in the NodeType's <c>.json</c>) reaches the store.</summary>
    private IObservable<MeshNode?> DeclareInMeshType(string path, bool? keepsHistory)
    {
        var json = keepsHistory is { } keeps
            ? $$"""{ "keepsHistory": {{(keeps ? "true" : "false")}} }"""
            : "{ }";
        return store.Write(MeshNode.FromPath(path) with
        {
            NodeType = MeshNode.NodeTypePath,
            Version = 1,
            Content = JsonDocument.Parse(json).RootElement.Clone(),
        }, Options);
    }

    private static MeshNode Instance(string path, string nodeType, long version, string text) =>
        MeshNode.FromPath(path) with { NodeType = nodeType, Version = version, Name = text, Content = text };

    private async Task<IList<MeshNodeVersion>> History(string path) =>
        await versions.GetVersions(path).ToList().Timeout(Budget).Await();

    [Fact]
    public async Task A_write_of_an_opted_out_in_mesh_type_records_no_version()
    {
        await DeclareInMeshType(EphemeralType, keepsHistory: false).Timeout(Budget).Await();
        const string path = "Governance/Requests/r1";

        var saved = await adapter.Write(Instance(path, EphemeralType, 1, "secret line"), Options).Timeout(Budget).Await();
        await adapter.Write(Instance(path, EphemeralType, 2, "cleared"), Options).Timeout(Budget).Await();

        saved.Should().NotBeNull("the primary write is unaffected by the history opt-out");
        (await History(path)).Should().BeEmpty(
            "a type declaring keepsHistory:false must leave no snapshot of any write — the submitted "
            + "lines would otherwise outlive the node clearing them");
    }

    [Fact]
    public async Task A_write_of_an_opted_out_static_type_records_no_version()
    {
        const string path = "Governance/Requests/r2";

        await adapter.Write(Instance(path, BuiltInEphemeralType, 1, "secret line"), Options).Timeout(Budget).Await();

        (await History(path)).Should().BeEmpty("a C#-registered definition's KeepsHistory:false is honoured the same way");
    }

    /// <summary>
    /// The lookup is paid once per TYPE, not once per write: MeshWeaver#5886 resolved the definition
    /// on every write, and the platform bake's <c>Hosting/FleetConsole</c> gate (three sequential
    /// writes inside a 10 s budget) went red on the first two sets carrying it.
    /// </summary>
    [Fact]
    public async Task The_definition_is_resolved_once_per_type_not_per_write()
    {
        var lookups = 0;
        var builtIn = new MeshNode("Counted") { NodeType = MeshNode.NodeTypePath, Content = new NodeTypeDefinition() };
        var counting = new VersionWritingStorageAdapter(
            store, versions,
            staticNodeLookup: path =>
            {
                if (string.Equals(path, "Counted", StringComparison.OrdinalIgnoreCase))
                    Interlocked.Increment(ref lookups);
                return string.Equals(path, "Counted", StringComparison.OrdinalIgnoreCase) ? builtIn : null;
            },
            readOptions: () => Options);

        for (var v = 1; v <= 5; v++)
            await counting.Write(Instance("Governance/Counted/c1", "Counted", v, $"v{v}"), Options).Timeout(Budget).Await();

        lookups.Should().Be(1, "five writes of one type must resolve its definition once");
        (await History("Governance/Counted/c1")).Should().HaveCount(5, "and the type still keeps its history");
    }

    /// <summary>The in-mesh branch — the routed storage read that was the FleetConsole cost — is also
    /// paid once per type; a definition change reaching the store's change feed makes the next write
    /// read it again (review finding on #5899).</summary>
    [Fact]
    public async Task An_in_mesh_definition_is_read_once_per_type_and_again_after_it_changes()
    {
        await DeclareInMeshType(OrdinaryType, keepsHistory: null).Timeout(Budget).Await();
        var counting = new CountingReads(store, OrdinaryType);
        var adapter2 = new VersionWritingStorageAdapter(counting, versions, readOptions: () => Options);
        const string path = "Governance/Counted/d1";

        for (var v = 1; v <= 4; v++)
            await adapter2.Write(Instance(path, OrdinaryType, v, $"v{v}"), Options).Timeout(Budget).Await();
        counting.DefinitionReads.Should().Be(1, "four writes of one in-mesh type read its definition once");

        // The definition changes through the store (another replica's shape) — the feed invalidates.
        await DeclareInMeshType(OrdinaryType, keepsHistory: false).Timeout(Budget).Await();
        await adapter2.Write(Instance(path, OrdinaryType, 5, "cleared"), Options).Timeout(Budget).Await();

        counting.DefinitionReads.Should().Be(2, "the changed definition is read again");
        (await History(path)).Should().BeEmpty("and its new verdict applies — the node's history is purged");
    }

    private sealed class CountingReads(InMemoryStorageAdapter inner, string countedPath) : IStorageAdapter
    {
        private int definitionReads;
        public int DefinitionReads => Volatile.Read(ref definitionReads);

        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        {
            if (string.Equals(path, countedPath, StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref definitionReads);
            return inner.Read(path, options);
        }

        public IObservable<DataChangeNotification> Changes => inner.Changes;
        public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
            => ((IStorageAdapter)inner).ReadMany(paths, options);
        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
            ListChildPaths(string? parentPath) => inner.ListChildPaths(parentPath);
        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options) => inner.Write(node, options);
        public IObservable<string> Delete(string path) => inner.Delete(path);
        public IObservable<bool> Exists(string path) => inner.Exists(path);
        public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
            => inner.GetPartitionObjects(nodePath, subPath, options);
        public IObservable<Unit> SavePartitionObjects(
            string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
            => inner.SavePartitionObjects(nodePath, subPath, objects, options);
        public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => inner.DeletePartitionObjects(nodePath, subPath);
        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
            => inner.GetPartitionMaxTimestamp(nodePath, subPath);
    }

    [Fact]
    public async Task An_ordinary_type_still_records_every_version()
    {
        await DeclareInMeshType(OrdinaryType, keepsHistory: null).Timeout(Budget).Await();
        const string path = "Governance/Docs/d1";

        await adapter.Write(Instance(path, OrdinaryType, 1, "v1"), Options).Timeout(Budget).Await();
        await adapter.Write(Instance(path, OrdinaryType, 2, "v2"), Options).Timeout(Budget).Await();

        (await History(path)).Select(v => v.Version).OrderBy(v => v).ToArray().Should().Equal([1L, 2L],
            "the default is to keep history — the anti-vacuity anchor for the two arms above");
    }

    [Fact]
    public async Task A_write_purges_history_recorded_before_the_type_opted_out()
    {
        const string path = "Governance/Requests/r3";
        await DeclareInMeshType(EphemeralType, keepsHistory: true).Timeout(Budget).Await();
        await adapter.Write(Instance(path, EphemeralType, 1, "secret line"), Options).Timeout(Budget).Await();
        (await History(path)).Should().HaveCount(1, "precondition: history was kept while the type allowed it");

        await DeclareInMeshType(EphemeralType, keepsHistory: false).Timeout(Budget).Await();
        await adapter.Write(Instance(path, EphemeralType, 2, "cleared"), Options).Timeout(Budget).Await();

        (await History(path)).Should().BeEmpty(
            "the first write after the opt-out removes what an earlier regime (or a store's own "
            + "trigger) recorded — keepsHistory:false means NO snapshot remains, not 'no new ones'");
    }

    [Fact]
    public async Task Deleting_a_node_of_an_opted_out_type_leaves_no_history()
    {
        const string path = "Governance/Requests/r4";
        await DeclareInMeshType(EphemeralType, keepsHistory: true).Timeout(Budget).Await();
        await adapter.Write(Instance(path, EphemeralType, 1, "secret line"), Options).Timeout(Budget).Await();
        await adapter.Write(Instance(path, EphemeralType, 2, "still secret"), Options).Timeout(Budget).Await();
        await DeclareInMeshType(EphemeralType, keepsHistory: false).Timeout(Budget).Await();
        (await History(path)).Should().HaveCount(2, "precondition: two versions exist before the delete");

        await adapter.Delete(path).Timeout(Budget).Await();

        (await History(path)).Should().BeEmpty("deleting a node of a no-history type purges its history");
    }

    [Fact]
    public async Task Deleting_an_ordinary_node_keeps_its_history()
    {
        const string path = "Governance/Docs/d2";
        await DeclareInMeshType(OrdinaryType, keepsHistory: null).Timeout(Budget).Await();
        await adapter.Write(Instance(path, OrdinaryType, 1, "v1"), Options).Timeout(Budget).Await();

        await adapter.DeleteMany([path]).Timeout(Budget).Await();

        (await History(path)).Should().HaveCount(1,
            "history outlives an ordinary delete — point-in-time restore depends on it");
    }

    /// <summary>Review finding on #5886: a null NodeType used to throw inside the delete's
    /// pre-read (StringComparer.Ordinal.GetHashCode(null), from a selector, past the Catch), so the
    /// delete itself failed. An untyped node keeps history and deletes normally.</summary>
    [Fact]
    public async Task Deleting_an_untyped_node_deletes_it_and_keeps_its_history()
    {
        const string path = "Governance/Loose/u1";
        await adapter.Write(MeshNode.FromPath(path) with { NodeType = null, Version = 1, Content = "x" }, Options)
            .Timeout(Budget).Await();

        await adapter.DeleteMany([path]).Timeout(Budget).Await();

        (await store.Read(path, Options).Timeout(Budget).Await()).Should().BeNull("the delete must run");
        (await History(path)).Should().HaveCount(1, "an untyped node keeps its history");
    }

    /// <summary>A node type is an unvalidated string: the node at that path may hold content of any
    /// shape. That must keep history, never fault a committed write.</summary>
    [Fact]
    public async Task A_type_path_naming_a_non_definition_node_keeps_history_and_does_not_fault()
    {
        const string notADefinition = "Governance/NotAType";
        await store.Write(MeshNode.FromPath(notADefinition) with
        {
            NodeType = "Markdown",
            Version = 1,
            Content = JsonDocument.Parse("[1, 2, 3]").RootElement.Clone(),
        }, Options).Timeout(Budget).Await();
        const string path = "Governance/Odd/o1";

        var saved = await adapter.Write(Instance(path, notADefinition, 1, "v1"), Options).Timeout(Budget).Await();

        saved.Should().NotBeNull();
        (await History(path)).Should().HaveCount(1);
    }

    [Fact]
    public async Task The_purge_touches_only_the_node_it_names()
    {
        await DeclareInMeshType(EphemeralType, keepsHistory: true).Timeout(Budget).Await();
        await adapter.Write(Instance("Governance/Requests/a", EphemeralType, 1, "a"), Options).Timeout(Budget).Await();
        await adapter.Write(Instance("Governance/Requests/a_b", EphemeralType, 1, "a_b"), Options).Timeout(Budget).Await();

        var purged = await versions.PurgeVersions("Governance/Requests/a").Timeout(Budget).Await();

        purged.Should().BeTrue();
        (await History("Governance/Requests/a")).Should().BeEmpty();
        (await History("Governance/Requests/a_b")).Should().HaveCount(1,
            "a sibling whose id merely starts with the purged id keeps its history");
    }

    [Fact]
    public async Task A_retaining_store_that_cannot_purge_never_claims_it_did()
    {
        IVersionQuery retaining = new RetainingWithoutPurge();
        IVersionQuery none = new NoOpVersionQuery();

        (await retaining.PurgeVersions("x").Timeout(Budget).Await()).Should().BeFalse(
            "the interface default must not report a purge a retaining store never performed");
        (await none.PurgeVersions("x").Timeout(Budget).Await()).Should().BeTrue("a store that retains nothing holds nothing to purge");
    }

    private sealed class RetainingWithoutPurge : IVersionQuery
    {
        public IObservable<MeshNodeVersion> GetVersions(string path) => Observable.Empty<MeshNodeVersion>();
        public IObservable<MeshNode?> GetVersion(string path, long version, JsonSerializerOptions options) => Observable.Return<MeshNode?>(null);
        public IObservable<MeshNode?> GetVersionBefore(string path, long beforeVersion, JsonSerializerOptions options) => Observable.Return<MeshNode?>(null);
    }
}
