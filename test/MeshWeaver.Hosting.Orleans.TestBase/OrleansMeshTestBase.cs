using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// The ONE base for a test that needs an Orleans cluster — the Orleans counterpart of
/// <see cref="MonolithMeshTestBase"/>, and the class that replaced <c>OrleansTestBase&lt;T&gt;</c>
/// and <c>OrleansSharedTestBase</c>.
///
/// <para>🚨 WHY ONE. The two it replaced were not two designs, they were the same machinery written
/// twice: both deployed a <see cref="TestCluster"/> through <c>OrleansTestCluster.DeployAsync</c>,
/// both created a client hub with a seeded <c>AccessContext</c> and a routing-stream registration,
/// both tracked those hubs and joined their teardown before the silos went down. What actually
/// differed was three VALUES — which silo configurator, how many silos, and whether the cluster is
/// leased from <see cref="OrleansMeshPool"/> or dedicated to the class. Values do not need a second
/// base class, and paying for one meant every fix to the shared half had to be made twice or drift
/// (measured: 23 subclasses across this repo, 20 of which overrode nothing at all).</para>
///
/// <para><b>HOW a suite says what it wants</b> — the same <see cref="IMeshBootstrap"/> seam
/// <see cref="MonolithMeshTestBase"/> uses, so "which mesh do I boot" reads identically on both
/// sides:
/// <code>
/// protected override IMeshBootstrap Bootstrap => MeshBootstrap.Orleans(o => o.WithSilos(2));
/// protected override Type SiloConfiguratorType => typeof(MySiloConfigurator);
/// </code></para>
///
/// <para><b>What it does NOT do:</b> it does not make an Orleans suite a
/// <see cref="MonolithMeshTestBase"/> suite. A cluster is deployed ASYNCHRONOUSLY, before any
/// <see cref="MeshBuilder"/> exists, and 18 of these suites assert on <see cref="Cluster"/> or
/// <see cref="SiloServices"/> — surfaces a base that must not reference Orleans cannot expose. The
/// seam unifies the DECLARATION; the two bases still own their own boot.</para>
/// </summary>
public abstract class OrleansMeshTestBase(ITestOutputHelper output) : TestBase(output)
{
    /// <summary>
    /// HOW this suite's cluster is stood up. Defaults to the localhost, in-memory, single-silo
    /// cluster — what every suite wants unless it is specifically about distribution.
    /// </summary>
    protected virtual IMeshBootstrap Bootstrap => MeshBootstrap.Orleans();

    /// <summary>
    /// The silo configurator Orleans <c>new()</c>s for every silo. Named by <see cref="Type"/>
    /// rather than by a generic parameter: Orleans constructs it itself, so a type argument bought
    /// nothing but a generic base class in every signature that mentioned it.
    /// </summary>
    protected virtual Type SiloConfiguratorType => typeof(SharedSiloConfigurator);

    /// <summary>
    /// The Orleans CLIENT host's configurator. Separate from the silo's on purpose — the client
    /// mesh is its own hub and needs its own registrations (see
    /// <see cref="TestClientConfigurator.MeshExtra"/>).
    /// </summary>
    protected virtual Type ClientConfiguratorType => typeof(TestClientConfigurator);

    /// <summary>
    /// Whether the Orleans client borrows the SILO's <see cref="InMemoryStorageAdapter"/>, making
    /// the two hosts one logical store — prod's "several adapter instances, one PG database".
    /// False for a configurator that brings its own durable backend (FileSystem, PostgreSQL):
    /// those claim at Priority 100 while the in-memory catch-all sits at 0, so joining the stores
    /// would let a client-side mirror answer a read the durable backend owns.
    /// </summary>
    protected virtual bool ClientSharesSiloStore =>
        SiloConfiguratorType.IsAssignableTo(typeof(TestSiloConfigurator))
        || SiloConfiguratorType.IsAssignableTo(typeof(SharedSiloConfigurator));

    /// <summary>
    /// Whether this class LEASES a running cluster from <see cref="OrleansMeshPool"/> instead of
    /// booting its own. A lease is exclusive, and the pool keys on the whole
    /// <see cref="OrleansClusterShape"/>, so a lease can never hand a class a cluster built by
    /// someone else's configurator.
    ///
    /// <para>The default reproduces what the two retired bases did: a suite on the stock
    /// configurator pools (that was <c>OrleansSharedTestBase</c>), a suite that brings its own
    /// silo wiring gets a dedicated cluster (that was <c>OrleansTestBase&lt;T&gt;</c>). Override to
    /// <c>false</c> for a class that mutates CLUSTER-WIDE state destructively — kills silos,
    /// asserts global counters.</para>
    /// </summary>
    protected virtual bool UsePooledMesh => SiloConfiguratorType == typeof(SharedSiloConfigurator);

    /// <summary>
    /// Extra configuration for each hub <see cref="GetClient"/> creates. The mesh data source and
    /// the layout client are added for every caller; chain through this for anything else.
    /// </summary>
    protected virtual MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => configuration;

    /// <summary>
    /// The fixture that carries the cluster. A repo that ships its own rig returns a DERIVED
    /// fixture here (the AI one in MeshWeaver.Plugins does); everyone else gets the stock fixture
    /// built to this suite's <see cref="ClusterShape"/>.
    /// </summary>
    protected virtual SharedOrleansFixture CreateFixture() => new(ClusterShape);

    /// <summary>The cluster this suite asked for, resolved from <see cref="Bootstrap"/>.</summary>
    protected OrleansClusterShape ClusterShape => new(
        SiloConfiguratorType,
        ClientConfiguratorType,
        BootstrapOptions.Silos,
        ClientSharesSiloStore);

    /// <summary>
    /// The resolved Orleans options — REFUSING, by name, a suite that overrode
    /// <see cref="Bootstrap"/> with a non-Orleans one. A monolith bootstrap on an Orleans base is a
    /// statement that cannot be honoured, and saying so here beats a silo failing minutes later
    /// with a message about a connection.
    /// </summary>
    private OrleansBootstrapOptions BootstrapOptions => Bootstrap is OrleansBootstrap orleans
        ? orleans.Options
        : throw new InvalidOperationException(
            $"{GetType().Name} is an {nameof(OrleansMeshTestBase)}, so its Bootstrap must be a "
            + $"MeshBootstrap.Orleans(...); it is '{Bootstrap.Name}'. A suite that wants an "
            + $"in-process mesh derives from {nameof(MonolithMeshTestBase)} instead.");

    /// <summary>The cluster fixture — kept public-ish for suites that read it directly.</summary>
    protected SharedOrleansFixture Fixture { get; private set; } = null!;

    /// <summary>The Orleans test cluster.</summary>
    protected TestCluster Cluster => Fixture.Cluster;

    /// <summary>
    /// The Orleans client host's services. <c>Cluster.Client</c> is null on these clusters — the
    /// client host belongs to <c>OrleansTestClusterHost</c> so it can be handed per-cluster
    /// instances through a closure.
    /// </summary>
    protected IServiceProvider ClientServices => Fixture.ClientServices;

    /// <summary>The client-side mesh hub.</summary>
    protected IMessageHub ClientMesh => Fixture.ClientMesh;

    /// <summary>The Orleans cluster client (grain factory).</summary>
    protected IClusterClient ClusterClient => Fixture.ClusterClient;

    /// <summary>A silo's container, for a suite that registered its own silo-side services.</summary>
    protected IServiceProvider SiloServices(int index = 0) => Cluster.SiloServices(index);

    private readonly ConcurrentBag<IMessageHub> clientHubs = new();
    private bool leasedFromPool;

    /// <inheritdoc />
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        // Pool first (an exclusive lease of a RUNNING cluster of this exact shape), a dedicated
        // cluster otherwise — for opted-out classes, for a custom silo configurator, and for an
        // assembly that does not register the pool fixture at all.
        if (UsePooledMesh && OrleansMeshPool.Current is { } pool)
        {
            Fixture = await pool.LeaseAsync(CreateFixture);
            leasedFromPool = true;
        }
        else
        {
            Fixture = CreateFixture();
            await Fixture.InitializeAsync();
        }
    }

    /// <summary>
    /// Creates a participating client mesh hub at <c>client/{clientId}</c>, seeds its per-circuit
    /// <c>AccessContext</c> with <paramref name="userId"/>, and registers the address with the
    /// client's AND the silo's <see cref="Mesh.Services.IRoutingService"/> so replies route back.
    /// The hub is tracked and torn down in <see cref="DisposeAsync"/>; do not dispose it yourself.
    ///
    /// <para><paramref name="clientId"/> is unique-per-call when omitted — see
    /// <c>MonolithMeshTestBase.CreateClientAddress</c> for the routing-table partitioning
    /// rationale (a leaked server-side sync stream from a prior test's client hub flooding the
    /// latest <c>client/1</c>'s action block).</para>
    /// </summary>
    protected IMessageHub GetClient(string? clientId = null, string userId = "TestUser")
    {
        var client = Fixture.GetClient(
            clientId ?? Guid.NewGuid().ToString("N")[..12],
            userId,
            ConfigureClient);
        clientHubs.Add(client);
        return client;
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        // Fixture is null only when InitializeAsync FAULTED before it was assigned — in which case
        // no client can exist either, because GetClient goes through it. Checked once, at the top,
        // so that path is stated rather than merely implied by an empty collection.
        if (Fixture is not null)
        {
            foreach (var hub in clientHubs)
                await Fixture.CleanupClientAsync(hub);

            if (leasedFromPool && OrleansMeshPool.Current is { } pool)
                pool.Return(Fixture);
            else
                await Fixture.DisposeAsync();
        }
        await base.DisposeAsync();
    }
}

/// <summary>
/// Registers the applicator <c>MeshBootstrap.Orleans(...)</c> needs in order to mean anything.
///
/// <para>🚨 WHY IT IS A STATIC HOOK AND NOT A METHOD. <see cref="MeshWeaver.Hosting.Monolith.TestBase"/>
/// defines the seam and must NOT reference Orleans — 23 in-process suites would otherwise drag a
/// cluster's worth of packages to boot a mesh that has no silos. So the fluent API and its
/// validation live there, pure, and the half that needs Orleans is registered HERE, by the assembly
/// that already has it. Until something registers one, <c>OrleansBootstrap.Bootstrap</c> throws
/// NAMING the missing applicator rather than failing minutes later inside a silo.</para>
/// </summary>
public static class OrleansBootstrapRegistration
{
    /// <summary>
    /// Armed when this assembly loads, which is exactly when an Orleans cluster becomes possible.
    /// </summary>
    [ModuleInitializer]
    internal static void Register() => OrleansBootstrap.Applicator = Apply;

    /// <summary>
    /// Applies the MESH-level half of an Orleans bootstrap. The cluster half — how many silos,
    /// where membership lives — is topology, consumed by <see cref="OrleansMeshTestBase"/> when it
    /// deploys the <see cref="TestCluster"/>; a <see cref="MeshBuilder"/> cannot express it.
    ///
    /// <para>🚨 A shape the in-process test cluster cannot stand up is REFUSED here, naming the
    /// provider. The alternative is the failure this whole seam exists to prevent: a silo that
    /// cannot reach its membership table fails minutes later with a message about a connection
    /// string and nothing about the test that asked for it.</para>
    /// </summary>
    internal static MeshBuilder Apply(OrleansBootstrapOptions options, MeshBuilder builder)
        => options is { Clustering: ClusterProvider.Localhost, Storage: StorageProvider.Memory }
            ? builder.AddPartitionedInMemoryPersistence()
            : throw new NotSupportedException(
                $"{options.Describe()} cannot be stood up by the in-process Orleans test cluster: "
                + $"it supports {ClusterProvider.Localhost} membership and {StorageProvider.Memory} "
                + "grain storage only. A test about a real membership table or grain store belongs "
                + "on a cluster that has one — it is not something this rig can fake.");
}
