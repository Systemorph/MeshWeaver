using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Testing.FaultInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// "Start two processes and transfer from one to the other": an Orleans cluster of
/// <see cref="SiloCount"/> in-process silos that share ONE store of record, each silo's store
/// wrapped in its own <see cref="FaultInjectingStorageAdapter"/>, and a
/// <see cref="CrossProcessChangeRelay"/> between them that delivers every commit to every other silo
/// the way PostgreSQL LISTEN/NOTIFY does in production. On top of that, the silo faults:
///
/// <list type="bullet">
///   <item><see cref="Kill"/> — the SIGKILL / SIGSEGV shape: no graceful stop, no deactivation.</item>
///   <item><see cref="Drain"/> — the rolling-restart shape: SIGTERM, then a graceful stop.</item>
///   <item><see cref="Linger"/> — the roll at its worst moment: the silo has been told to stop
///     (every grain on it now refuses deliveries as <c>ShuttingDown</c> and hands its address off)
///     and STAYS in the cluster until the returned handle is disposed.</item>
/// </list>
///
/// <para>Silo indices are stable for the life of the fixture: index 1 names the same silo after
/// index 0 has been killed. A test that removes a silo leaves the cluster changed, so each such
/// test class takes its own instance (<c>IClassFixture</c>), never a pooled mesh.</para>
///
/// <para>Death detection is fast and the held-stream heartbeat short (<see cref="Heartbeat"/>), so a
/// hand-off completes well inside a test budget; both are production mechanisms at test cadence.</para>
/// </summary>
public class FaultInjectionCluster : IAsyncLifetime
{
    /// <summary>The held sync stream's heartbeat cadence in this cluster.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(1);

    private readonly OrleansTestBackingStore _backingStore = new();
    private OrleansTestClusterHost? _host;
    private ImmutableList<SiloHandle> _silos = ImmutableList<SiloHandle>.Empty;

    /// <summary>How many silos the cluster starts with. Two unless a derived fixture says otherwise.</summary>
    protected virtual int SiloCount => 2;

    /// <summary>The configurator every silo is built with; a module-owning repo supplies a derived one.</summary>
    protected virtual Type SiloConfiguratorType => typeof(FaultInjectionSiloConfigurator);

    /// <summary>The Orleans test cluster.</summary>
    public TestCluster Cluster => (_host ?? throw new InvalidOperationException(
        "The fault-injection cluster is not deployed yet — InitializeAsync has not run.")).Cluster;

    /// <summary>The cross-process change relay (the LISTEN/NOTIFY model), on by default.</summary>
    public CrossProcessChangeRelay Relay { get; } = new();

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        _host = await OrleansTestCluster.DeployAsync(
            builder =>
            {
                builder.Options.InitialSilosCount = (short)SiloCount;
                builder.Options.SiloBuilderConfiguratorTypes.Add(SiloConfiguratorType.AssemblyQualifiedName
                    ?? throw new InvalidOperationException($"{SiloConfiguratorType} has no assembly-qualified name."));
                // TestCluster's only silo-host hook; see TwoSiloCacheUpdateFixture for why the
                // shared store must be handed in here rather than registered by the configurator.
                builder.CreateSiloAsync = async (siloName, configuration) =>
                    await InProcessSiloHandle.CreateAsync(
                        siloName,
                        configuration,
                        hostBuilder => hostBuilder.ConfigureServices(services =>
                        {
                            _backingStore.Register(services);
                            WrapTheStore(services, siloName);
                        }));
            },
            withClient: false);
        _silos = Cluster.Silos.ToImmutableList();
        // Resolve each silo's injector now, so every silo has joined the relay before the test's
        // first write — a silo that joined late would miss the commits made before it did.
        // And prove the injector is actually UNDER the silo's store — a fixture whose wrapping
        // silently missed would pass every case having injected nothing.
        for (var i = 0; i < _silos.Count; i++)
        {
            var injector = Storage(i);
            Silo(i).GetServices<IPartitionStorageProvider>()
                .Count(p => ReferenceEquals(p.Adapter, injector))
                .Should().Be(1, $"silo {i}'s writable in-memory store must be served through its fault injector");
            // And the silo's IStorageAdapter — under the platform's guard decorators — is the
            // partition router over those providers, never the raw in-memory adapter, which would
            // reach the store around every injected fault.
            Silo(i).GetRawStorageAdapter<PersistenceService>().Should().NotBeNull(
                $"silo {i}'s IStorageAdapter must route through its partition providers, where the injector sits");
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Relay.Dispose();
        if (_host is not null)
            OrleansClusterDisposal.DisposeInBackground(_host);
        return ValueTask.CompletedTask;
    }

    /// <summary>The services of silo <paramref name="index"/> (stable across kills).</summary>
    /// <param name="index">The silo's index at deploy time.</param>
    public IServiceProvider Silo(int index) => ((InProcessSiloHandle)_silos[index]).SiloHost.Services;

    /// <summary>The mesh hub of silo <paramref name="index"/>.</summary>
    /// <param name="index">The silo's index at deploy time.</param>
    public IMessageHub Hub(int index) => Silo(index).GetRequiredService<IMessageHub>();

    /// <summary>Silo <paramref name="index"/>'s storage injector.</summary>
    /// <param name="index">The silo's index at deploy time.</param>
    public FaultInjectingStorageAdapter Storage(int index) => Silo(index).GetRequiredService<FaultInjectingStorageAdapter>();

    /// <summary>What the shared store of record holds for <paramref name="path"/>, bypassing every silo.</summary>
    /// <param name="path">The node path.</param>
    public MeshNode? Stored(string path) => _backingStore.TryGetNode(path, out var node) ? node : null;

    /// <summary>Kills silo <paramref name="index"/>: no graceful stop, no grain deactivation.</summary>
    /// <param name="index">The silo's index at deploy time.</param>
    public Task Kill(int index) => Cluster.KillSiloAsync(_silos[index]);

    /// <summary>
    /// Drains silo <paramref name="index"/> the way a rolling restart does: the host is told to stop
    /// (SIGTERM → <c>StopApplication</c>), then the silo stops gracefully and its grains deactivate.
    /// </summary>
    /// <param name="index">The silo's index at deploy time.</param>
    public async Task Drain(int index)
    {
        Silo(index).GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await Cluster.StopSiloAsync(_silos[index]);
    }

    /// <summary>
    /// Tells silo <paramref name="index"/> to stop and KEEPS it in the cluster, lingering, until the
    /// returned handle is disposed — which stops it. Dispose it in a <c>finally</c> (<c>await using</c>).
    /// </summary>
    /// <param name="index">The silo's index at deploy time.</param>
    public LingeringSilo Linger(int index)
    {
        Silo(index).GetRequiredService<IHostApplicationLifetime>().StopApplication();
        return new LingeringSilo(() => Cluster.StopSiloAsync(_silos[index]), $"silo {index}");
    }

    /// <summary>
    /// The silo a grain for <paramref name="path"/> on the LINGERING silo <paramref name="leaving"/>
    /// hands its address off to (<c>MessageHubGrain.AnotherActiveSilo</c>: the other active silos,
    /// ordered by address, indexed by the key's ordinal string hash — which is per-PROCESS, and every
    /// silo of this cluster shares the test process). A case uses it to choose a path whose hand-off
    /// lands where the case needs it, and asserts the landing afterwards, so a change to the
    /// production rule shows up as a failed precondition rather than as a case that silently measures
    /// something else.
    /// </summary>
    /// <param name="path">The grain key (the node path).</param>
    /// <param name="leaving">The lingering silo's index.</param>
    /// <param name="active">The indices of the silos still active.</param>
    public int HandOffTarget(string path, int leaving, IReadOnlyCollection<int> active)
    {
        var candidates = active.Where(i => i != leaving)
            .Select(i => (Index: i, Address: Silo(i).GetRequiredService<global::Orleans.Runtime.ILocalSiloDetails>()
                .SiloAddress.ToParsableString()))
            .OrderBy(c => c.Address, StringComparer.Ordinal)
            .ToArray();
        var spread = (uint)StringComparer.Ordinal.GetHashCode(path);
        return candidates[spread % (uint)candidates.Length].Index;
    }

    /// <summary>
    /// Replaces the silo's in-memory partition provider with one whose adapter is this silo's
    /// <see cref="FaultInjectingStorageAdapter"/> over the shared store. The provider registrations
    /// are factories, so each one is wrapped rather than removed: whatever it builds is kept, except
    /// that the writable in-memory catch-all is handed the injector as its adapter.
    /// </summary>
    private void WrapTheStore(IServiceCollection services, string siloName)
    {
        services.AddSingleton(sp =>
        {
            var injector = new FaultInjectingStorageAdapter(sp.GetRequiredService<InMemoryStorageAdapter>(), siloName);
            Relay.Join(injector);
            return injector;
        });
        var providers = services.Where(d => d.ServiceType == typeof(IPartitionStorageProvider)).ToList();
        foreach (var descriptor in providers)
        {
            services.Remove(descriptor);
            services.AddSingleton<IPartitionStorageProvider>(sp =>
            {
                var built = Build(descriptor, sp);
                return built is InMemoryPartitionStorageProvider { IsReadOnly: false } inMemory
                       && ReferenceEquals(inMemory.Adapter, sp.GetRequiredService<InMemoryStorageAdapter>())
                    ? new FaultInjectingPartitionStorageProvider(inMemory, sp.GetRequiredService<FaultInjectingStorageAdapter>())
                    : built;
            });
        }
    }

    private static IPartitionStorageProvider Build(ServiceDescriptor descriptor, IServiceProvider sp)
        => (IPartitionStorageProvider)(descriptor.ImplementationInstance
            ?? descriptor.ImplementationFactory?.Invoke(sp)
            ?? ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType
                ?? throw new InvalidOperationException($"A partition provider registration has no implementation: {descriptor}")));
}

/// <summary>
/// A silo that has been told to stop and is still in the cluster. Disposing it completes the stop;
/// until then every grain on it refuses deliveries as <c>ShuttingDown</c> and hands its address off.
/// </summary>
public sealed class LingeringSilo : IAsyncDisposable
{
    private readonly Func<Task> _stop;
    private int _stopped;

    internal LingeringSilo(Func<Task> stop, string name)
    {
        _stop = stop;
        Name = name;
    }

    /// <summary>Which silo lingers.</summary>
    public string Name { get; }

    /// <summary>Completes the stop. Idempotent.</summary>
    public ValueTask DisposeAsync()
        => System.Threading.Interlocked.Exchange(ref _stopped, 1) == 0
            ? new ValueTask(_stop())
            : ValueTask.CompletedTask;
}

/// <summary>An in-memory partition provider whose adapter is the silo's fault injector.</summary>
internal sealed class FaultInjectingPartitionStorageProvider(
    IPartitionStorageProvider inner, IStorageAdapter injector) : IPartitionStorageProvider
{
    public string Name => inner.Name;
    public bool IsReadOnly => inner.IsReadOnly;
    public IStorageAdapter Adapter => injector;
    public PartitionDefinition? PartitionDefinition => inner.PartitionDefinition;
    public int Priority => inner.Priority;
    public ImmutableHashSet<string> Contexts => inner.Contexts;
    public IStorageAdapter CreateAdapterForTable(PartitionDefinition def, string table) => injector;
    public IObservable<System.Reactive.Unit> EnsurePartitionProvisioned(string @namespace) => inner.EnsurePartitionProvisioned(@namespace);
    public IObservable<bool?> PartitionExists(string @namespace) => inner.PartitionExists(@namespace);
    public IObservable<System.Reactive.Unit> DeletePartition(string @namespace) => inner.DeletePartition(@namespace);
}

/// <summary>
/// The silo configurator of a <see cref="FaultInjectionCluster"/>: the portal mesh, fast death
/// detection and a short held-stream heartbeat, so a hand-off completes inside a test budget.
/// </summary>
public class FaultInjectionSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    /// <summary>Registrations a module-owning repo adds to every silo.</summary>
    /// <param name="builder">The mesh builder.</param>
    protected virtual MeshBuilder ConfigureAdditional(MeshBuilder builder) => builder;

    /// <inheritdoc />
    public virtual void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault()
            .ConfigureLogging(logging => logging.AddXUnitLogger());
        siloBuilder.Configure<ClusterMembershipOptions>(o =>
        {
            o.ProbeTimeout = TimeSpan.FromSeconds(1);
            o.NumMissedProbesLimit = 2;
            o.NumVotesForDeathDeclaration = 1;
            o.IAmAliveTablePublishTimeout = TimeSpan.FromSeconds(5);
        });
    }

    /// <inheritdoc />
    public virtual void Configure(IHostBuilder hostBuilder)
    {
        var configured = hostBuilder.UseOrleansMeshServer()
            .ConfigurePortalMesh()
            .AddGraph()
            .ConfigureServices(services => services.Configure<SyncStreamOptions>(o =>
            {
                o.HeartbeatInterval = FaultInjectionCluster.Heartbeat;
                o.FirstHeartbeat = FaultInjectionCluster.Heartbeat;
            }));
        ConfigureAdditional(configured)
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }
}
