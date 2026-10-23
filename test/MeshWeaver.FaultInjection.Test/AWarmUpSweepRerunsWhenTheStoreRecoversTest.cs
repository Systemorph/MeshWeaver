using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.FaultInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// 🚨 <b>A warm-up sweep that faulted on a TRANSIENT store refusal is re-run on the SAME pod once the
/// store is reachable again, and the <c>nodetype_bake</c> gate then grants readiness</b>
/// (MeshWeaver#7066, refs #7068).
///
/// <para><b>The incident.</b> memex, pod <c>85cf49759f-pnbwl</c>, 2026-10-22: a managed-database
/// restart refused connections from about 21:22:51Z to 21:23:34Z. The pod's sweep faulted at
/// 21:22:55Z, the gate recorded <see cref="BakePhase.Faulted"/>, and nothing ran the sweep again —
/// <c>nodetype_bake</c> answered Unhealthy every ten seconds until the pod was replaced at 22:37Z, and
/// the roll could not converge until then.</para>
///
/// <para><b>The reproduction is the real mesh and the real hosted service.</b>
/// <see cref="DynamicTypePreWarmerHostedService"/> runs exactly as a portal runs it — armed gate, real
/// sweep, real query fan-in, real invalidation feed. Two pieces are injected, each at the extension
/// point production plugs into: a query provider that REFUSES the sweep's catalog read with the
/// incident's shape (a transient <see cref="DbException"/> over <c>SocketException (111) Connection
/// refused</c>) until the test lets the store back, and the store of record's
/// <see cref="FaultInjectingStorageAdapter.DeliverChangeFeedGap"/> — what the PostgreSQL listener
/// declares when it re-opens its session after the database comes back. The re-run policy allows NO
/// timed re-run, so the only thing that can re-run the sweep is that recovery signal.</para>
///
/// <para><b>The controls.</b>
/// <see cref="NegativeControl_TheSweepAsItWasHeldBefore_StaysFaulted_AfterTheStoreRecovers"/> drives the
/// SAME fault through the sweep the way the hosted service held it before the fix (one subscription,
/// fault ⇒ Faulted) and proves the gate stays unready after the store is back and the signal is
/// declared — while a FRESH sweep on that same mesh then passes, so what was missing was only the
/// re-run. <see cref="ANonTransientFault_IsNotReRun_AndStaysANamedFailure"/> proves the re-run is
/// TYPED: a defect is not repeated on the store's signal.</para>
/// </summary>
public class AWarmUpSweepRerunsWhenTheStoreRecoversTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>How long the negative arms watch for a recovery that must not come.</summary>
    private static readonly TimeSpan StaysFaultedWindow = TimeSpan.FromSeconds(5);

    private readonly RefusingCatalog catalog = new();

    private FaultInjectingStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<FaultInjectingStorageAdapter>();
    private NodeTypeBakeGateState Gate => Mesh.ServiceProvider.GetRequiredService<NodeTypeBakeGateState>();

    /// <summary>The injector goes in BEFORE the base adds in-memory persistence.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.AddFaultInjectingStorage())
            .ConfigureServices(services =>
            {
                services.AddSingleton<IMeshQueryProvider>(catalog);
                // The portal's wiring: the gate is ARMED (a readiness probe consumes it).
                services.AddNodeTypeBakeGate(gatesReadiness: true);
                // No clock may re-run the sweep — only the store's own recovery signal.
                services.AddSingleton(new StoreRecoveryRerunPolicy { MaxTimedReruns = 0 });
                // Layer the sweep switch ONTO the host configuration (replacing it would take every
                // other key with it).
                var configured = services.LastOrDefault(d => d.ServiceType == typeof(IConfiguration));
                if (configured is not null)
                    services.Remove(configured);
                return services.AddSingleton<IConfiguration>(sp =>
                {
                    var configuration = new ConfigurationBuilder();
                    if (Materialise(configured, sp) is { } host)
                        configuration.AddConfiguration(host);
                    return configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [DynamicTypePreWarmerHostedService.EnabledConfigKey] = "true",
                    }).Build();
                });
            });

    private static IConfiguration? Materialise(ServiceDescriptor? descriptor, IServiceProvider sp)
        => descriptor is null ? null
            : descriptor.ImplementationFactory is { } factory ? (IConfiguration)factory(sp)
            : descriptor.ImplementationInstance is IConfiguration instance ? instance
            : throw new InvalidOperationException(
                "The IConfiguration registration is neither a factory nor an instance, so this test "
                + "cannot layer onto it.");

    [HubFact]
    public async Task ATransientStoreRefusal_FaultsTheSweep_AndTheStoresRecovery_ReRunsIt_OnTheSamePod()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = Gate;
        catalog.Refuse(Refused);
        using var lifetime = new StartedLifetime();
        using var service = new DynamicTypePreWarmerHostedService(
            Mesh.ServiceProvider, lifetime, Mesh.ServiceProvider.GetRequiredService<ILogger<DynamicTypePreWarmerHostedService>>());
        await service.StartAsync(ct);
        lifetime.Start();

        await catalog.Refusals.Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the sweep's catalog read must MEET the refusal — without it nothing below tests anything", ct);
        var faulted = await When(gate, g => g.Phase == BakePhase.Faulted)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a sweep whose store refused it has verified nothing, and the gate must say so", ct);
        faulted.ReadinessGranted.Should().BeFalse("a first boot of an unadmitted image keeps the strict reading");
        faulted.Detail.Should().Contain("TRANSIENT").And.Contain("NOT final",
            "the health payload must say the fault is being recovered from, not read as final");

        // The store is back: connections are accepted again, and the listener declares it.
        catalog.Recover();
        Storage.DeliverChangeFeedGap(new ChangeFeedGap(
            "test:store-restart", DateTimeOffset.UtcNow.AddSeconds(-40), DateTimeOffset.UtcNow, "connection refused"));

        var ready = await When(gate, g => g.ReadinessGranted)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("once the store is reachable again the sweep re-runs ON THIS POD and the gate grants "
                  + "readiness — the incident's pod waited 74 minutes to be replaced instead", ct);
        ReferenceEquals(ready, Gate).Should().BeTrue("the SAME gate instance recovered: no new pod, no restart");
        ready.Phase.Should().Be(BakePhase.Complete,
            "the re-run measured what the faulted attempt could not, so the verdict is a completed bake, "
            + "not an unproven one waved through");
    }

    [HubFact]
    public async Task NegativeControl_TheSweepAsItWasHeldBefore_StaysFaulted_AfterTheStoreRecovers()
    {
        var ct = TestContext.Current.CancellationToken;
        // A gate of its own, held exactly as the hosted service held it before the fix: ONE
        // subscription of the sweep, and a fault marks it Faulted for good.
        var gate = new NodeTypeBakeGateState { GatesReadiness = true };
        gate.MarkRunning("enumerating dynamic NodeTypes");
        catalog.Refuse(Refused);

        using var once = DynamicTypePreWarmer
            .WarmDynamicTypes(Mesh, NullLogger.Instance, batchBake: true, buildProtocol: true)
            .Subscribe(
                _ => { },
                _ => gate.MarkFaulted("the warm-up stream faulted before the sweep could finish"),
                () => gate.MarkComplete("baked"));

        await When(gate, g => g.Phase == BakePhase.Faulted)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the injected refusal must REACH the sweep and fault it — the latch #7066 reports. "
                  + "Without this the fixed arm could pass because no fault ever arrived", ct);

        catalog.Recover();
        Storage.DeliverChangeFeedGap(new ChangeFeedGap(
            "test:store-restart", DateTimeOffset.UtcNow.AddSeconds(-40), DateTimeOffset.UtcNow, "connection refused"));

        await When(gate, g => g.ReadinessGranted)
            .Should().NotEmit(StaysFaultedWindow,
                "held the old way, nothing re-runs the sweep: the store is back and has said so, and the "
                + "gate still refuses — the 74-minute stall", ct);

        // …while the store really IS back: a fresh sweep on the same mesh now completes.
        await DynamicTypePreWarmer
            .WarmDynamicTypes(Mesh, NullLogger.Instance, batchBake: true, buildProtocol: true)
            .ToList()
            .Should().Within(TestTimeouts.Convergence)
            .Emit("after the recovery a sweep passes — what the faulted gate lacked was only a re-run", ct);
    }

    [HubFact]
    public async Task ANonTransientFault_IsNotReRun_AndStaysANamedFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = Gate;
        catalog.Refuse(() => new InvalidOperationException("a defect in the catalog read, not the store"));
        using var lifetime = new StartedLifetime();
        using var service = new DynamicTypePreWarmerHostedService(
            Mesh.ServiceProvider, lifetime, Mesh.ServiceProvider.GetRequiredService<ILogger<DynamicTypePreWarmerHostedService>>());
        await service.StartAsync(ct);
        lifetime.Start();

        var faulted = await When(gate, g => g.Phase == BakePhase.Faulted)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the defect faults the sweep", ct);
        faulted.Detail.Should().NotContain("NOT final",
            "a defect is final and must not be reported as being recovered from");

        catalog.Recover();
        Storage.DeliverChangeFeedGap(new ChangeFeedGap(
            "test:store-restart", DateTimeOffset.UtcNow.AddSeconds(-40), DateTimeOffset.UtcNow, "connection refused"));

        await When(gate, g => g.Phase != BakePhase.Faulted)
            .Should().NotEmit(StaysFaultedWindow,
                "only a TRANSIENT store fault is re-run — the store's signal must not repeat a defect", ct);
    }

    /// <summary>The incident's shape: the driver's connect failure over a refused socket.</summary>
    private static Exception Refused() =>
        new StoreRefusedException("Failed to connect to 10.0.0.1:5432", new SocketException(111));

    /// <summary>A gate reading, re-read until <paramref name="predicate"/> holds (the gate is a
    /// plain state object a readiness probe pulls, so it is sampled exactly the way the probe samples it).</summary>
    private static IObservable<NodeTypeBakeGateState> When(
        NodeTypeBakeGateState gate, Func<NodeTypeBakeGateState, bool> predicate)
        => Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => gate)
            .Where(predicate)
            .Take(1);

    /// <summary>Core never references a driver, so the driver exception is a <see cref="DbException"/>
    /// carrying the same transient classification Npgsql gives a refused connection.</summary>
    private sealed class StoreRefusedException(string message, Exception inner) : DbException(message, inner)
    {
        public override bool IsTransient => true;
    }

    /// <summary>The host's lifetime, started by the test (ApplicationStarted is the sweep's kick).</summary>
    private sealed class StartedLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource started = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();

        public CancellationToken ApplicationStarted => started.Token;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => stopped.Token;
        public void StopApplication() { }
        public void Start() => started.Cancel();

        public void Dispose()
        {
            started.Dispose();
            stopping.Dispose();
            stopped.Dispose();
        }
    }

    /// <summary>
    /// The store as the sweep's catalog read meets it: while refusing, every catalog query
    /// (<c>nodeType:NodeType</c>) fails BEFORE its Initial with the configured fault — a database that
    /// will not accept the connection; once recovered it answers an empty slice and the mesh's real
    /// providers supply the records. Every other query is answered empty at once.
    /// </summary>
    private sealed class RefusingCatalog : IMeshQueryProvider
    {
        private Func<Exception>? refusal;
        private readonly System.Reactive.Subjects.ReplaySubject<string> refusals = new();

        /// <summary>Each catalog read the refusal met (replayed) — the positive signal that the fault arrived.</summary>
        public IObservable<string> Refusals => refusals.AsObservable();

        public void Refuse(Func<Exception> fault) => Volatile.Write(ref refusal, fault);

        public void Recover() => Volatile.Write(ref refusal, null);

        public string Name => nameof(RefusingCatalog);

        public bool Matches(IReadOnlyList<string> queryNamespaces) => false;

        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options) =>
            Observable.Defer(() =>
            {
                var catalogRead = request.EffectiveQueries.Any(q =>
                    q.Contains($"nodeType:{MeshNode.NodeTypePath}", StringComparison.OrdinalIgnoreCase));
                if (catalogRead && Volatile.Read(ref refusal) is { } fault)
                {
                    refusals.OnNext(string.Join(" | ", request.EffectiveQueries));
                    return Observable.Throw<QueryResultChange<T>>(fault());
                }
                return Observable.Return(new QueryResultChange<T>
                {
                    ChangeType = QueryChangeType.Initial,
                    Items = Array.Empty<T>(),
                    Timestamp = DateTimeOffset.UtcNow,
                });
            });

        public IObservable<IReadOnlyCollection<QueryResult>> Query(MeshQueryRequest request, JsonSerializerOptions options)
            => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
            string basePath, string prefix, JsonSerializerOptions options,
            AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
            string? contextPath = null, string? context = null)
            => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
            => Observable.Return<T?>(default);
    }
}
