using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A deleted partition must not come back — and the seam it used to come back through is NOT
/// a node write</b> (Systemorph/MeshWeaver#3451, the remaining half of #3436).
///
/// <para><b>Why the existing exclusion could not see it.</b> The delete pipeline already holds
/// <c>RecentlyDeletedRegistry.BeginSubtreeDeletion</c> across the WHOLE operation — plan, drain,
/// the partition drop (step 5 runs inside the scope) and the success response — and
/// <c>SubtreeDeletionGuardStorageAdapter</c> refuses every in-process write at or under the root
/// while it is open. Both of those guard <see cref="IStorageAdapter"/> WRITES. The resurrection did
/// not happen through a write: <c>EnsurePartitionBootstrap</c> → <c>HealPartitionRoot</c> opened
/// with <see cref="IPartitionStorageProvider.EnsurePartitionProvisioned"/> — the API whose own
/// contract calls it "the ONLY trigger for partition creation" — which is DDL and crosses no storage
/// adapter at all. Nothing in the registry, the tombstone or the guard was ever consulted for it.</para>
///
/// <para><b>Why a probe was never going to close it.</b> #3436 gated the heal on "does the backing
/// store still exist?". The drop is step 5, AFTER the drain, so a probe taken while the delete was
/// draining answered a truthful <c>true</c> — and authorised a <c>CREATE SCHEMA</c> that ran after
/// the <c>DROP</c>. That is the whole race, and it is what
/// <see cref="PartitionStore.StaleExistenceProbeFor"/> models below: one property, no thread
/// choreography, and the same input the real race produces. A test that had to interleave two
/// pipelines to see the bug would be measuring its own timing; this measures the invariant —
/// <b>a stale <c>true</c> must not be able to bring a partition back.</b></para>
///
/// <para>The fixture's root carries a NodeType declared HERE, for the same reason
/// <c>PartitionTeardownCoverageTest</c> does: the real victims were <c>Store/Plugin</c> roots, a
/// type that lives in mesh CONTENT and sets no <c>OwnsPartition</c>, so anything keyed on
/// <c>src/</c> knowledge would be green while the actual shape stayed uncovered.</para>
/// </summary>
public class PartitionResurrectionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The <c>Store/Plugin</c> stand-in — a partition-root NodeType that does NOT declare
    /// <c>OwnsPartition</c>, because what provisioned its partition was an installer.</summary>
    private const string PluginLikeNodeType = "TestStore/PluginLike";

    /// <summary>
    /// A provider that MODELS a per-partition backing store — the Postgres schema, in miniature.
    /// Not a mock of a core interface: <see cref="IPartitionStorageProvider"/> IS the extension
    /// point a storage backend implements, and provisioning / probing / dropping a partition is
    /// exactly its contract. Read-only, so it never joins the write chain and the in-memory store
    /// stays the store of record.
    /// </summary>
    private sealed class PartitionStore(IStorageAdapter adapter) : IPartitionStorageProvider
    {
        private readonly ConcurrentDictionary<string, byte> provisioned =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> events = new();

        /// <inheritdoc />
        public string Name => "test-partition-store";

        /// <inheritdoc />
        public bool IsReadOnly => true;

        /// <inheritdoc />
        public IStorageAdapter Adapter => adapter;

        /// <summary>
        /// The partition whose existence probe answers the PRE-DROP truth (<c>true</c>) no matter
        /// what the store actually holds — a probe that was read before step 5's
        /// <c>DROP SCHEMA</c> and acted on after it. Null (the default) means every probe answers
        /// honestly.
        /// </summary>
        public string? StaleExistenceProbeFor { get; set; }

        /// <summary>The ordered ledger of provisioning / drop calls, for the assertions.</summary>
        public IReadOnlyList<string> Events => events.ToArray();

        /// <summary>Does this partition's backing store exist right now?</summary>
        public bool IsProvisioned(string @namespace) => provisioned.ContainsKey(@namespace);

        /// <summary>Provisions a partition the way <c>PackageInstaller</c> does — the fixture's
        /// stand-in for an install, and the call the bootstrap must never make.</summary>
        public IObservable<System.Reactive.Unit> EnsurePartitionProvisioned(string @namespace)
            => Observable.Defer(() =>
            {
                provisioned[@namespace] = 1;
                events.Enqueue($"provision:{@namespace}");
                return Observable.Return(System.Reactive.Unit.Default);
            });

        /// <inheritdoc />
        public IObservable<bool?> PartitionExists(string @namespace)
            => Observable.Defer(() => Observable.Return<bool?>(
                string.Equals(@namespace, StaleExistenceProbeFor, StringComparison.OrdinalIgnoreCase)
                    ? true
                    : provisioned.ContainsKey(@namespace)));

        /// <inheritdoc />
        public IObservable<System.Reactive.Unit> DeletePartition(string @namespace)
            => Observable.Defer(() =>
            {
                provisioned.TryRemove(@namespace, out _);
                events.Enqueue($"drop:{@namespace}");
                return Observable.Return(System.Reactive.Unit.Default);
            });
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(new MeshNode(PluginLikeNodeType)
            {
                Name = "Plugin-like root",
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition { DefaultNamespace = "", RestrictedToNamespaces = [""] },
            })
            .ConfigureServices(services => services
                .AddSingleton<IPartitionStorageProvider>(sp =>
                    new PartitionStore(sp.GetRequiredService<IStorageAdapter>())));

    private PartitionStore Store => Mesh.ServiceProvider
        .GetServices<IPartitionStorageProvider>()
        .OfType<PartitionStore>()
        .Single();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private IStorageAdapter Persistence => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private static string NewPartition() => "resurrect" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Installs a partition the way the package installer does: provision the backing store first
    /// (the installer's own <c>EnsurePartitionsProvisioned</c>, NOT
    /// <c>OwnsPartitionProvisioningValidator</c> — this root type declares no
    /// <c>OwnsPartition</c>), then write the root and one child as SYSTEM.
    /// </summary>
    private async Task<string> InstallPartition(bool withRoot = true)
    {
        var partition = NewPartition();
        await Store.EnsurePartitionProvisioned(partition)
            .Timeout(TestTimeouts.Quick).Await();

        if (withRoot)
            await CreateAsSystem(new MeshNode(partition)
            {
                Name = "A plugin-like partition root",
                NodeType = PluginLikeNodeType,
                State = MeshNodeState.Active,
            });

        Output.WriteLine($"installed partition '{partition}' (root={withRoot})");
        return partition;
    }

    private async Task<CreateNodeResponse> CreateAsSystem(MeshNode node)
    {
        var response = await Access
            .RunAsSystem(() => ObserveNodeOperation(new CreateNodeRequest(node)))
            .FirstAsync()
            .Select(d => d.Message)
            .Timeout(TestTimeouts.Convergence)
            .Await();
        Output.WriteLine($"create {node.Path} success={response.Success} error={response.Error}");
        return response;
    }

    private static MeshNode Child(string partition, string id) =>
        new(id, partition)
        {
            Name = id,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = $"# {id}" },
        };

    private async Task DeletePartitionAsSystem(string path)
    {
        var response = await Access
            .RunAsSystem(() => ObserveNodeOperation(
                new DeleteNodeRequest(path)
                {
                    Recursive = true,
                    IncludeSatellites = true,
                    ConfirmWarnings = true,
                    DeletedBy = WellKnownUsers.System,
                }))
            .FirstAsync()
            .Select(d => d.Message)
            .Timeout(TestTimeouts.CrossSilo)
            .Await();
        Output.WriteLine($"delete {path} success={response.Success} error={response.Error}");
        response.Success.Should().BeTrue($"the delete itself must succeed: {response.Error}");
    }

    /// <summary>
    /// 🚨 <b>THE FALSIFIER for the DDL side-channel.</b> On the pre-fix code the bootstrap opens with
    /// <c>EnsurePartitionProvisioned</c> on every provider — so a stale <c>true</c> from the
    /// existence probe puts the partition's backing store straight back, AFTER the delete dropped
    /// it. No storage guard sees that call: it is not a write.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task AStaleExistenceProbe_DoesNotReProvisionADeletedPartitionsStore()
    {
        var partition = await InstallPartition();
        await CreateAsSystem(Child(partition, "Page"));

        await DeletePartitionAsSystem(partition);
        Store.IsProvisioned(partition).Should().BeFalse(
            "the delete must drop the partition's backing store first — otherwise this test would "
            + "be asserting about a partition that was never torn down (#3436 is the fix that makes "
            + "this line pass)");

        // The delete-window race, stated as its input: the probe was read BEFORE step 5's drop, so
        // it truthfully answers `true`, and the bootstrap acts on it afterwards.
        Store.StaleExistenceProbeFor = partition;

        await CreateAsSystem(Child(partition, "Recovered"));

        Store.IsProvisioned(partition).Should().BeFalse(
            "a child write must never re-provision a partition's backing store. The bootstrap is a "
            + "REPAIR, and a repair that can call EnsurePartitionProvisioned is a SECOND trigger for "
            + "partition creation behind OwnsPartitionProvisioningValidator's back — the one seam "
            + "neither the subtree-deletion scope nor SubtreeDeletionGuardStorageAdapter can see, "
            + "because provisioning is DDL and crosses no storage adapter (#3451). "
            + $"Provider ledger: {string.Join(", ", Store.Events)}");
    }

    /// <summary>
    /// The visible half of the incident: a bare <c>Space</c> shell over a partition that was
    /// deleted. The exclusion that refuses it is the delete's own tombstone — marked synchronously
    /// at the delete source for every path INCLUDING the root, and cleared only by a real re-create
    /// — so it OUTLIVES the drop, which is exactly what #3436 asked for and what the subtree scope
    /// (released with the operation) could not provide.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task AStaleExistenceProbe_DoesNotResurrectADeletedPartitionsRoot()
    {
        var partition = await InstallPartition();
        await CreateAsSystem(Child(partition, "Page"));

        await DeletePartitionAsSystem(partition);
        (await Persistence.Exists(partition).FirstAsync().Timeout(TestTimeouts.Convergence).Await())
            .Should().BeFalse("the delete removed the root — that is the state the heal runs over");

        Store.StaleExistenceProbeFor = partition;

        await CreateAsSystem(Child(partition, "Recovered"));

        (await Persistence.Exists(partition).FirstAsync().Timeout(TestTimeouts.Convergence).Await())
            .Should().BeFalse(
                $"'{partition}' was deleted, so no child write may heal a root over it. That is how "
                + "four deleted partitions came back on the systemorph staff portal as bare Space "
                + "shells with a fresh _Policy 67 ms later — a policy granting Delete to nobody who "
                + "could have deleted the original, so the shell was undeletable through the "
                + "ordinary API (#3436/#3451)");
    }

    /// <summary>
    /// The in-flight window, driven through the REAL exclusion rather than a simulated one: a
    /// <c>BeginSubtreeDeletion</c> scope is open on the partition (exactly what
    /// <c>HandleDeleteNodeRequest</c> holds from before the plan until after the drop), the root row
    /// is already gone, and the probe still answers <c>true</c> because the drop has not run yet.
    /// Pre-fix the bootstrap provisions the store from inside that window and only THEN has its row
    /// write refused by the storage guard — so the guard was closing the visible half while the
    /// invisible half sailed through.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task WhileTheDeletionScopeIsOpen_TheHealNeitherProvisionsNorRoots()
    {
        // A partition mid-teardown: nodes drained, store about to be dropped, no root row.
        var partition = await InstallPartition(withRoot: false);
        await Store.DeletePartition(partition).Timeout(TestTimeouts.Quick).Await();
        Store.StaleExistenceProbeFor = partition;

        var registry = Mesh.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>();
        using (registry.BeginSubtreeDeletion(partition))
        {
            registry.IsUnderActiveDeletion(partition, out _).Should().BeTrue(
                "the scope must actually be open, or this test asserts nothing");

            await CreateAsSystem(Child(partition, "MidFlight"));
        }

        Store.IsProvisioned(partition).Should().BeFalse(
            "a write racing a delete must not re-provision the partition's backing store — the "
            + "subtree-deletion scope was open for the whole create, and provisioning is the one "
            + "effect that scope never reached (#3451). "
            + $"Provider ledger: {string.Join(", ", Store.Events)}");
        (await Persistence.Exists(partition).FirstAsync().Timeout(TestTimeouts.Convergence).Await())
            .Should().BeFalse("nor may a Space root be minted for a subtree that is being deleted");
    }

    /// <summary>
    /// 🚨 <b>The positive control — the case that could falsify the fix.</b> The bootstrap exists to
    /// repair a LIVE partition whose root row went missing (#638/#902: a create that wrote the row
    /// and then failed leaves the bare partition address unroutable, and the first child write is
    /// what heals it). Nothing here was deleted, so the heal must still run — a fix that simply
    /// stopped healing would pass every assertion above while breaking the feature.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task AMissingRootOverALivePartition_IsStillHealed()
    {
        var partition = await InstallPartition(withRoot: false);

        var response = await CreateAsSystem(Child(partition, "First"));
        response.Success.Should().BeTrue($"the child write must succeed: {response.Error}");

        (await Persistence.Exists(partition).FirstAsync().Timeout(TestTimeouts.Convergence).Await())
            .Should().BeTrue(
                "the partition's backing store is there and nothing deleted it, so the missing root "
                + "is a half-completed create the bootstrap must repair — that repair is the whole "
                + "reason EnsurePartitionBootstrap exists, and it must survive #3451");
        Store.IsProvisioned(partition).Should().BeTrue(
            "and the store it healed over is the one the installer provisioned — untouched");
    }
}
