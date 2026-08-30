using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The end-to-end property of the cross-process change feed: <b>a write made in one process becomes
/// visible to a LIVE mirror in another process — with no recycle, and without waiting out any heal
/// timer.</b>
///
/// <para><b>What "another process" is here.</b> Two independent meshes, each with its own hub tree,
/// its own workspace mirrors and its own storage adapter, over ONE shared durable store — the same
/// shape <see cref="CrossClusterBuildClaimTest"/> uses for two Orleans clusters over one Postgres
/// database. The one addition is the wire that was missing in production: each adapter's commits are
/// bridged to BOTH adapters as ENTITY-LESS notifications, exactly what
/// <c>PostgreSqlChangeListener</c> publishes from a <c>pg_notify</c> payload of <c>{path, op}</c>
/// (and what the Cosmos change feed and the Snowflake poller publish). Including the writer's own
/// echo is faithful too: a process's LISTEN session receives its own NOTIFY.</para>
///
/// <para><b>The defect this pins (#1440 → #1814).</b> The per-node hub's reconcile
/// (<c>MeshDataSourceExtensions.SubscribeToOwnDeletion</c>) used to give up on any notification
/// whose entity was absent — <c>if (notification.Entity is not MeshNode newNode) return;</c> — which
/// is EVERY cross-process notification there is. So a mirror could be arbitrarily far behind a
/// rival's write and would never be told: the staleness lived in the snapshot, not in the
/// activation, which is why the cross-hub write conflict behind the 2026-08-17 outage was
/// deterministic rather than a race that a retry could eventually win. Measured then: at two
/// replicas the covers failed for two hours; at one replica (no rival mirror) the same page rendered
/// in 2.6 s.</para>
///
/// <para>🚨 The fix is a RE-READ, never a supplemented entity. Populating <c>Entity</c> from the
/// payload is the RLS bypass #1250 removed, and it races row visibility besides — so these tests
/// deliver notifications with no entity and assert the mirror converges anyway.</para>
/// </summary>
public class CrossProcessChangeFeedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>ONE durable store. Two adapter instances over it = two processes over one database.</summary>
    private readonly ConcurrentDictionary<string, MeshNode> _rows = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, List<object>> _partitionObjects =
        new(StringComparer.OrdinalIgnoreCase);

    private CrossProcessFeedAdapter AdapterA
        => (CrossProcessFeedAdapter)Mesh.ServiceProvider.GetRequiredService<CrossProcessFeedAdapter>();

    protected override TimeSpan TestHardDeadline => TimeSpan.FromSeconds(120);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        // Registered BEFORE AddInMemoryPersistence's TryAddSingleton so the write-integrity
        // decorators (SubtreeDeletionGuard → MonotonicWriteGuard → VersionWriting) wrap THIS adapter
        // — it then sits exactly where the durable backend sits, underneath every guard.
        => base.ConfigureMesh(builder.ConfigureServices(UseSharedStore));

    private IServiceCollection UseSharedStore(IServiceCollection services)
    {
        services.AddSingleton(sp => new CrossProcessFeedAdapter(new InMemoryStorageAdapter(
            _rows, _partitionObjects, sp.GetService<ILogger<InMemoryStorageAdapter>>())));
        services.AddSingleton<IStorageAdapter>(sp => sp.GetRequiredService<CrossProcessFeedAdapter>());
        return services;
    }

    [Fact(Timeout = 90_000)]
    public async Task AWriteInOneProcess_ReachesTheOtherProcessesLiveMirror_WithoutARecycle()
    {
        var id = $"cross-proc-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";

        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
            { Name = "v1", NodeType = "Markdown", State = MeshNodeState.Active })
            .Should().Within(30.Seconds()).Emit();
        await WaitDurable(path, n => n.Name == "v1");

        using var processB = BuildSecondProcess();
        using var wire = Bridge(processB);

        // Process B's mirror goes LIVE and stays live: Replay(1)+RefCount with a keepAlive, so the
        // per-node hub is never torn down and re-seeded between the two assertions. Without the
        // keepAlive the second read could pass by re-activating a cold hub, which would prove
        // nothing about the feed.
        var mirror = processB.Hub.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).Replay(1).RefCount();
        using var keepAlive = mirror.Subscribe();
        var seeded = await mirror.Should().Within(30.Seconds()).Match(n => n.Name == "v1");
        Output.WriteLine($"[B seeded] name={seeded.Name} version={seeded.Version}");
        var adapterB = processB.ServiceProvider.GetRequiredService<CrossProcessFeedAdapter>();
        // Trace EVERY emission, not just the one asserted on: against `main` this test fails with
        // the mirror holding v3 while the store holds v2 — content that converged by accident,
        // through a read-seeded mint of a revision that exists nowhere. Without the trace that
        // reads as an off-by-one instead of naming the #1432 phantom it is.
        using var trace = mirror.Subscribe(n => Output.WriteLine(
            $"[B emit] name={n.Name} v={n.Version} writesB={adapterB.WriteCount(path)} readsB={adapterB.ReadCount(path)}"));

        // The write happens in process A, through the hub that OWNS the node.
        Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Update(n => n with { Name = "written-in-A" })
            .Subscribe(_ => { }, ex => Output.WriteLine($"[A write error] {ex.Message}"));
        var durable = await WaitDurable(path, n => n.Name == "written-in-A");
        Output.WriteLine($"[A durable] version={durable.Version}");

        var converged = await mirror.Should().Within(30.Seconds())
            .Match(n => n.Name == "written-in-A",
                "a live mirror in another process must learn of the write from the cross-process "
                + "feed — the entity-less notification is the ONLY thing that crosses, and before "
                + "this change it was discarded unread");
        Output.WriteLine($"[store] v={_rows[path].Version} name={_rows[path].Name} writesB={adapterB.WriteCount(path)} readsB={adapterB.ReadCount(path)}");
        converged.Version.Should().Be(durable.Version,
            "the mirror ADOPTS the durable version verbatim. Against `main` this is where the test "
            + "fails: the mirror ends at durable+1 — a revision the store never held (#1432) — "
            + "because the notification was dropped and the content only arrived later, through a "
            + "read-seeded mint. Converging on content by accident, at a version that exists "
            + "nowhere, is not a mirror learning of a write");
    }

    /// <summary>
    /// The ROOT of #2008, as a deterministic assertion rather than the race it surfaces as:
    /// <b>a process that only MIRRORS a node must never write that node</b> — not even the
    /// activation seed it just read out of the same store.
    ///
    /// <para>Against the unfixed framework this fails on every run: process B's per-node hub reads
    /// the durable row as its activation seed, and the persistence sampler dispatches that read
    /// straight back to storage as if it were a local edit. The sampler's echo suppression is a
    /// reference comparison against <c>OwnNodeCache.PersistedSnapshot</c>, which is ONE slot, while
    /// <c>MeshNodeTypeSource.Initialize</c> builds TWO collections back-to-back (the durable seed,
    /// then the routing-supplied leg) before the workspace hands either to the sampler — so the
    /// slot holds the routing instance and the SEED emission is not recognised as a load.</para>
    ///
    /// <para><b>Why that write is the version defect and not merely a wasted round-trip.</b> It is
    /// harmless exactly while the row has not moved. When the other process's real write lands
    /// first — which is the whole point of a cross-process test, and what CI hits at ~1.3% — the
    /// seed write is a strict version regression: <c>MonotonicWriteGuardStorageAdapter</c> refuses
    /// it, and <c>AdoptDurableTruth</c> then correctly (for a hub that has something of its own to
    /// save) rebases this owner at <c>durable + 1</c>. So a mirror that never edited anything ends
    /// up holding a revision the store never held, carrying the right content — "converging on
    /// content by accident, at a version that exists nowhere" (#1432), which is exactly what
    /// <see cref="AWriteInOneProcess_ReachesTheOtherProcessesLiveMirror_WithoutARecycle"/> reports
    /// when it fails. That test asserts the SYMPTOM under a race; this one asserts the CAUSE.</para>
    ///
    /// <para>🚨 <b>Two nodes, and the reason is the whole point.</b> The mirrored node under test
    /// (<c>seed</c>) is NEVER written by anyone after B activates, so nothing can rescue B's seed
    /// echo: the only thing that suppresses it on the unfixed framework is B adopting a HIGHER
    /// durable version for that same path first (<c>AdoptPersisted</c> records the adopted version
    /// on <c>PostCommitFlushRegistry</c>, which <c>HandleSaveMeshNode</c> then drops the stale save
    /// against). Assert on a path A also writes and that rescue fires often enough to turn the red
    /// into a coin flip — the same coin flip #2008 IS. The second node (<c>clock</c>) supplies the
    /// wait instead: B converging on a write to a DIFFERENT path proves B ran a full
    /// notification → re-read → adopt cycle, which begins after activation and ends past the 200 ms
    /// persistence-sample window that dispatches the seed echo. Load stretches that cycle while the
    /// sample window stays a wall-clock 200 ms from activation, so a busy runner makes this red
    /// MORE reliable, not less.</para>
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task AMirroringProcess_NeverWritesTheNode_NotEvenItsActivationSeed()
    {
        var seedId = $"seed-echo-{Guid.NewGuid():N}";
        var seedPath = $"{TestPartition}/{seedId}";
        var clockId = $"seed-clock-{Guid.NewGuid():N}";
        var clockPath = $"{TestPartition}/{clockId}";

        await NodeFactory.CreateNode(new MeshNode(seedId, TestPartition)
            { Name = "v1", NodeType = "Markdown", State = MeshNodeState.Active })
            .Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode(clockId, TestPartition)
            { Name = "v1", NodeType = "Markdown", State = MeshNodeState.Active })
            .Should().Within(30.Seconds()).Emit();
        var durableSeed = await WaitDurable(seedPath, n => n.Name == "v1");
        await WaitDurable(clockPath, n => n.Name == "v1");

        using var processB = BuildSecondProcess();
        using var wire = Bridge(processB);
        var adapterB = processB.ServiceProvider.GetRequiredService<CrossProcessFeedAdapter>();

        // Both mirrors go live — this is what activates B's per-node hubs and runs the durable
        // activation-seed read whose echo is under test. keepAlive holds them open, so the sampler
        // window belongs to LIVE hubs rather than ones already tearing down.
        var mirror = processB.Hub.GetWorkspace().GetMeshNodeStream(seedPath)
            .Where(n => n is not null).Replay(1).RefCount();
        using var keepAlive = mirror.Subscribe();
        var clockMirror = processB.Hub.GetWorkspace().GetMeshNodeStream(clockPath)
            .Where(n => n is not null).Replay(1).RefCount();
        using var clockAlive = clockMirror.Subscribe();
        await mirror.Should().Within(30.Seconds()).Match(n => n.Name == "v1");
        await clockMirror.Should().Within(30.Seconds()).Match(n => n.Name == "v1");

        // The clock: A writes the OTHER node and we wait for B to learn of it. Never a bare sleep,
        // and never "the counter is still 0" — that would pass before the sampler had even fired.
        Mesh.GetWorkspace().GetMeshNodeStream(clockPath)
            .Update(n => n with { Name = "written-in-A" })
            .Subscribe(_ => { }, ex => Output.WriteLine($"[A clock write error] {ex.Message}"));
        await WaitDurable(clockPath, n => n.Name == "written-in-A");
        await clockMirror.Should().Within(30.Seconds()).Match(n => n.Name == "written-in-A");

        var writesSeed = await Settle(() => adapterB.WriteCount(seedPath));
        Output.WriteLine($"[B writes] seed={writesSeed} clock={adapterB.WriteCount(clockPath)} "
            + $"(store seed v={_rows[seedPath].Version})");

        writesSeed.Should().Be(0,
            "a mirroring process READS this node and never edits it, so nothing it holds may reach "
            + "storage. The activation seed IS the durable row — writing it back is an observation "
            + "entering the write path, and the moment another process's write gets there first "
            + "that echo becomes a version REGRESSION the monotonic guard refuses, after which the "
            + "refusal rebase leaves this mirror at durable+1 (#1432/#2008)");

        // …and the store is untouched: the seed row still sits at exactly the version A created it
        // at, no rewrite of any kind.
        _rows[seedPath].Version.Should().Be(durableSeed.Version,
            "nobody wrote this node after it was created, so its durable revision must not have "
            + "moved — a store that advanced has taken a write from a process that only mirrors");
    }

    [Fact(Timeout = 90_000)]
    public async Task AnEntitylessEchoOfTheOwnWrite_IsSuppressed_AndWritesNothingBack()
    {
        var id = $"self-echo-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";

        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
            { Name = "v1", NodeType = "Markdown", State = MeshNodeState.Active })
            .Should().Within(30.Seconds()).Emit();
        await WaitDurable(path, n => n.Name == "v1");

        // Only process A exists; the wire feeds A its OWN commits back, entity-less — precisely what
        // a LISTEN session does with the NOTIFY its own write fired.
        using var selfWire = AdapterA.LocalChanges.Subscribe(AdapterA.PublishExternal);

        var own = Mesh.GetWorkspace().GetMeshNodeStream(path).Where(n => n is not null)
            .Replay(1).RefCount();
        using var keepAlive = own.Subscribe();
        await own.Should().Within(30.Seconds()).Match(n => n.Name == "v1");

        Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Update(n => n with { Name = "own-write" })
            .Subscribe(_ => { }, ex => Output.WriteLine($"[own write error] {ex.Message}"));
        var durable = await WaitDurable(path, n => n.Name == "own-write");

        // Give the echo, the coalescing window and the re-read time to run — then assert nothing
        // moved. A phantom mint (#1432) or a re-adoption loop (#223) would show up here as a
        // version above the durable one, or as writes that keep arriving.
        var writesAfterEcho = await Settle(() => AdapterA.WriteCount(path));
        var live = await own.FirstAsync().Await();

        live.Version.Should().Be(durable.Version,
            "the echo of our own write must be suppressed — adopting it would mint durable+1, a "
            + "revision that exists nowhere (#1432)");
        _rows[path].Version.Should().Be(durable.Version,
            "nothing on the re-read path writes, so the store must not move");
        (await Settle(() => AdapterA.WriteCount(path))).Should().Be(writesAfterEcho,
            "a reconcile that fed itself would keep writing — the #223 write-storm shape");
    }

    [Fact(Timeout = 90_000)]
    public async Task AnEntitylessBurst_ReReadsTheOwnPathAFewTimes_AndAnUnownedPathNotAtAll()
    {
        var id = $"burst-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";

        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
            { Name = "v1", NodeType = "Markdown", State = MeshNodeState.Active })
            .Should().Within(30.Seconds()).Emit();
        await WaitDurable(path, n => n.Name == "v1");

        var own = Mesh.GetWorkspace().GetMeshNodeStream(path).Where(n => n is not null)
            .Replay(1).RefCount();
        using var keepAlive = own.Subscribe();
        await own.Should().Within(30.Seconds()).Match(n => n.Name == "v1");

        // 🚨 A DIFFERENTIAL measurement, because the adapter's read counter is not a private line
        // to the own-node reconcile: every live synced query in the mesh re-runs on every matching
        // notification (deliberately, at zero debounce — "the notification is a trigger, never a
        // result row"), and each re-run reads the nodes in scope. Counting raw reads would measure
        // that pre-existing behaviour, not this pipeline. So run the SAME burst twice — once
        // addressed to a sibling path no hub owns, once to the mirror's own path — and take the
        // difference. What is left is exactly the own-node re-reads — and the control run is
        // itself the "a path this hub does not own is discarded cheaply" measurement: the hub adds
        // NOTHING to it, so 200 unowned notifications cost the same as the query layer alone.
        var baselineBefore = await Settle(() => AdapterA.ReadCount(path));
        for (var i = 0; i < 200; i++)
            AdapterA.PublishExternal(DataChangeNotification.Updated($"{TestPartition}/sibling", null));
        var queryCost = (await Settle(() => AdapterA.ReadCount(path))) - baselineBefore;

        var burstBefore = await Settle(() => AdapterA.ReadCount(path));
        for (var i = 0; i < 200; i++)
            AdapterA.PublishExternal(DataChangeNotification.Updated(path, null));
        var withOwnPath = (await Settle(() => AdapterA.ReadCount(path))) - burstBefore;

        var reReads = withOwnPath - queryCost;
        Output.WriteLine(
            $"[burst] 200 notifications → own-path reads {withOwnPath}, query-layer baseline "
            + $"{queryCost}, re-reads attributable to the reconcile {reReads}");
        withOwnPath.Should().BeGreaterThan(queryCost,
            "the LAST notification of a burst must still produce a re-read — that read is the one "
            + "that converges the mirror, and dropping it would restore the defect");
        reReads.Should().BeLessThan(20,
            "a notification storm must not become a read storm: the trigger is coalesced per path "
            + "(a 50 ms window) and the reads are serialised, so 200 notifications must cost a "
            + "handful of reads, not 200");
    }

    /// <summary>
    /// Polls the durable store until <paramref name="predicate"/> holds. Bounded: a store that never
    /// gets there fails the test rather than hanging it.
    /// </summary>
    private async Task<MeshNode> WaitDurable(string path, Func<MeshNode, bool> predicate)
        => await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => _rows.TryGetValue(path, out var n) ? n : null)
            .Where(n => n is not null && predicate(n))
            .Select(n => n!)
            .Should().Within(30.Seconds()).Emit();

    /// <summary>
    /// Reads a counter until it stops moving for a full quiet window, then returns it. This is the
    /// bounded stand-in for "everything the notifications were going to do has happened" — never a
    /// fixed sleep, which would either flake or hide a late read.
    /// </summary>
    private static async Task<int> Settle(Func<int> counter)
    {
        var last = counter();
        var stable = 0;
        for (var i = 0; i < 60 && stable < 5; i++)
        {
            await Task.Delay(50);
            var now = counter();
            stable = now == last ? stable + 1 : 0;
            last = now;
        }
        return last;
    }

    /// <summary>
    /// The wire that was missing in production: every commit made by either process is delivered to
    /// BOTH processes' feeds, entity-less. Bridging <c>LocalChanges</c> (the adapter's OWN commits)
    /// and never <c>Changes</c> is what keeps it a wire rather than a loop — and matches Postgres,
    /// where the NOTIFY comes off the row trigger on a write, not off receiving one.
    /// </summary>
    private IDisposable Bridge(SecondProcess processB)
    {
        var adapterB = processB.ServiceProvider.GetRequiredService<CrossProcessFeedAdapter>();
        var wire = new System.Reactive.Disposables.CompositeDisposable
        {
            AdapterA.LocalChanges.Subscribe(n =>
            {
                AdapterA.PublishExternal(n);
                adapterB.PublishExternal(n);
            }),
            adapterB.LocalChanges.Subscribe(n =>
            {
                AdapterA.PublishExternal(n);
                adapterB.PublishExternal(n);
            }),
        };
        return wire;
    }

    /// <summary>
    /// A second, fully independent mesh over the SAME backing store — the stand-in for the other
    /// pod. Its recipe mirrors <c>MonolithMeshTestBase.ConfigureMeshBase</c>; only the durable store
    /// is shared, and the change feeds are separate instances exactly as two processes' are.
    /// </summary>
    private SecondProcess BuildSecondProcess()
    {
        var services = new ServiceCollection();
        // 🚨 Wire the SAME XUnitFileLoggerProvider process A gets from TestBase, pointed at the
        // SAME FileOutput — and grant MeshNodeTypeSource (this test's own file has the matching
        // "MeshWeaver.Graph.MeshNodeTypeSource": "Debug" override) Debug level via an in-memory
        // config, since this process builds its OWN IConfiguration rather than loading
        // appsettings.json. Without this, process B — the mirror this test asserts on, and the
        // hub #2008's own-node-versioning hypothesis is about — had ZERO logging providers: every
        // MeshNodeTypeSource.LogDebug/LogWarning call from its own-node reconcile (adds/updates/
        // deletes counts, durable-seed reads) went nowhere, not xUnit output, not the log file, on
        // every run, including a failing one. See #2008.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Warning",
                ["Logging:LogLevel:MeshWeaver.Graph.MeshNodeTypeSource"] = "Debug",
            })
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(l =>
        {
            l.ClearProviders();
            // AddConfiguration is what makes the override above REAL, and it is the half #2348
            // missed. Registering a provider only wires WHERE a record goes; the level deciding
            // whether it is emitted at all lives in LoggerFilterOptions, which ILoggerFactory
            // consults BEFORE any provider and which defaults to Information. So process B
            // delivered its Warning/Error records (already a strict improvement on the
            // ClearProviders-with-nothing-added-back state, which delivered none) while every
            // LogDebug the same change added — including the MeshNodeTypeSource own-node
            // reconcile lines the exercise was for — was still dropped by the factory, and the
            // Debug lines that then showed up in CI were process A's. Process A gets this wiring
            // from ServiceSetup.CreateServiceCollection; B builds its own container, so B must do
            // it itself. Verified by reading ILogger<MeshNodeTypeSource>.IsEnabled(Debug) in both
            // processes: A true, B false.
            l.AddConfiguration(configuration.GetSection("Logging"));
            l.Services.AddSingleton<ILoggerProvider>(sp => new XUnitFileLoggerProvider(() => FileOutput, sp));
        });
        services.AddOptions();

        var builder = new MeshBuilder(c => c.Invoke(services), AddressExtensions.CreateMeshAddress())
            .ConfigureServices(UseSharedStore)
            .UseMonolithMesh()
            .AddInMemoryPersistence()
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType()
            .AddMeshNodes(new MeshNode(TestPartition) { Name = "Test Data", NodeType = "Markdown" })
            .AddMeshNodes(TestUsers.DevLoginAdminAccess())
            .AddMeshNodes(TestUsers.PublicAdminAccess())
            .ConfigureHub(c => c.WithRequestTimeout(TimeSpan.FromSeconds(60)));

        services.AddSingleton(builder.BuildHub);
        var serviceProvider = services.CreateMeshWeaverServiceProvider();
        var hub = serviceProvider.GetRequiredService<IMessageHub>();
        TestUsers.DevLogin(hub);
        return new SecondProcess(serviceProvider, hub);
    }

    private sealed record SecondProcess(IServiceProvider ServiceProvider, IMessageHub Hub) : IDisposable
    {
        public void Dispose()
        {
            Hub.Dispose();
            (ServiceProvider as IDisposable)?.Dispose();
        }
    }
}
