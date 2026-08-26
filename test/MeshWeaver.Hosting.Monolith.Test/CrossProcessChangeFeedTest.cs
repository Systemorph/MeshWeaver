using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
        var live = await own.FirstAsync().ToTask();

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
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Warning",
                ["Logging:LogLevel:MeshWeaver.Graph.MeshNodeTypeSource"] = "Debug",
            })
            .Build());
        services.AddLogging(l =>
        {
            l.ClearProviders();
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
