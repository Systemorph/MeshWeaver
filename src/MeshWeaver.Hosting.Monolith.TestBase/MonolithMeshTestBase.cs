using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.TestBase;

public abstract class MonolithMeshTestBase : Fixture.TestBase
{
    protected static Address CreateClientAddress() => new("client", "1");

    /// <summary>
    /// Base mesh configuration without access control setup.
    /// Security tests can call this directly instead of base.ConfigureMesh().
    /// </summary>
    /// <summary>
    /// Default test partition name. Tests can create nodes under this path
    /// (e.g., "TestData/mynode") and they'll have proper mesh node hubs.
    /// Registered as a Markdown node so the hub gets AddMeshDataSource + WithNodeOperationHandlers.
    /// </summary>
    public const string TestPartition = "TestData";

    /// <summary>
    /// Quiescing budget for test-mesh hubs.
    /// <para>
    /// 500 ms is comfortably above the natural reply latency observed in tests
    /// (peak ~100 ms locally) so legitimate handlers drain within the budget.
    /// </para>
    /// <para>
    /// On timeout, the hub flips <see cref="IMessageHub.AnyHubQuiescingTimedOut"/>
    /// and the test base fails the test class — a pending callback at dispose
    /// is a leaked Observe subscription, which is always a real bug. The cost
    /// of being strict: if a handler genuinely needs &gt;500 ms to reply, that
    /// test must override this with <c>WithQuiesceTimeout(...)</c>.
    /// </para>
    /// </summary>
    protected static readonly TimeSpan TestQuiesceTimeout = TimeSpan.FromMilliseconds(500);

    protected MeshBuilder ConfigureMeshBase(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddInMemoryPersistence()
            .AddRowLevelSecurity()
            .AddGraph()
            .AddMeshNodes(new MeshNode(TestPartition) { Name = "Test Data", NodeType = "Markdown" })
            .ConfigureHub(c => c.WithQuiesceTimeout(TestQuiesceTimeout));

    /// <summary>
    /// Default mesh configuration with PublicAdminAccess for in-memory tests.
    /// File-system tests should override and omit PublicAdminAccess (access comes from _Access/ files).
    /// Security tests should call ConfigureMeshBase() instead.
    /// </summary>
    protected virtual MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(TestUsers.PublicAdminAccess());

    protected MonolithMeshTestBase(ITestOutputHelper output) : base(output)
    {
        var builder = ConfigureMesh(
            new(
                c => c.Invoke(Services),
                AddressExtensions.CreateMeshAddress()
            )
        );
        Services.AddSingleton(builder.BuildHub);
        TestPhaseTrace(GetType().Name, "CTOR");
    }

    /// <summary>
    /// Cross-process per-test phase trace. Single line per event into a fixed
    /// file so a developer can `tail -f` it during a hung suite run and spot the
    /// stuck test class without waiting for the run to finish.
    /// </summary>
    private static readonly string TestTraceLogPath =
        Path.Combine(Path.GetTempPath(), "meshweaver-test-trace.log");
    private static readonly object TestTraceLogLock = new();

    private static void TestPhaseTrace(string testClass, string phase, long? elapsedMs = null, string? extra = null)
    {
        try
        {
            var line = $"{DateTime.UtcNow:HH:mm:ss.fff} [{testClass}] {phase}"
                + (elapsedMs.HasValue ? $" elapsed={elapsedMs}ms" : "")
                + (extra is null ? "" : $" {extra}");
            lock (TestTraceLogLock)
                File.AppendAllText(TestTraceLogPath, line + Environment.NewLine);
        }
        catch
        {
            // Tracing must never throw out of the test pipeline.
        }
    }

    // Per-process baseline so MEM lines carry process-lifetime deltas, not just
    // the last-class delta. The leak hunter wants to see "class X added 200 MiB
    // and never gave it back" across the whole run.
    private static long _lastManagedHeapBytes;
    private static long _lastWorkingSetBytes;
    private static readonly object _memLock = new();

    /// <summary>
    /// Soft warning threshold. Classes whose RSS pushes past this are at risk of
    /// the OOM that was fingerprinted on CI's 7 GB ubuntu-latest runner. Sized
    /// well below the cap so the WATCHDOG line lands BEFORE swap-thrashing
    /// silences xUnit output (the symptom that disguises OOM as "fixture-init
    /// hang" past the 6 m wallclock cap).
    /// </summary>
    private const long MemPressureBytes = 4L * 1024 * 1024 * 1024; // 4 GiB

    /// <summary>
    /// Hard warning. Anything past this on a 7 GB runner is moments from SIGKILL.
    /// </summary>
    private const long MemCriticalBytes = 6L * 1024 * 1024 * 1024; // 6 GiB

    /// <summary>
    /// Cadence for the watchdog poll — small enough to land at least one MEM_*
    /// line per test class even for the fast ones (median class ~1-3 s), large
    /// enough to add no measurable runtime overhead.
    /// </summary>
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Tracks whether we already emitted the one-shot CRITICAL line so we don't
    /// spam the trace once memory parks above the threshold (which it will if
    /// the leak is permanent — every subsequent class would re-fire otherwise).
    /// </summary>
    private static int _criticalEmitted;

    /// <summary>
    /// Read selected fields out of <c>/proc/self/status</c> so we can tell native
    /// heap growth from mmap'd-file growth from total virtual size. Linux-only —
    /// returns empty string on Windows / macOS (Process Manager / Activity Monitor
    /// can do equivalent introspection there if needed).
    /// <list type="bullet">
    ///   <item><c>VmSize</c> — total virtual address space.</item>
    ///   <item><c>RssAnon</c> — anonymous pages (native heap, JIT code, stacks).</item>
    ///   <item><c>RssFile</c> — file-backed pages (mmap'd .dll/.so files — including
    ///     ALC-loaded assemblies). Growing RssFile across INIT_MEM lines is the
    ///     fingerprint of the ALC-not-unloading leak we're hunting.</item>
    /// </list>
    /// </summary>
    private static string ReadProcSelfStatus()
    {
        try
        {
            if (!File.Exists("/proc/self/status")) return string.Empty;
            long vmSize = 0, rssAnon = 0, rssFile = 0;
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("VmSize:", StringComparison.Ordinal)) vmSize = ParseKb(line);
                else if (line.StartsWith("RssAnon:", StringComparison.Ordinal)) rssAnon = ParseKb(line);
                else if (line.StartsWith("RssFile:", StringComparison.Ordinal)) rssFile = ParseKb(line);
            }
            return $"vmsz={vmSize / 1024}MiB rssAnon={rssAnon / 1024}MiB rssFile={rssFile / 1024}MiB";
        }
        catch
        {
            return string.Empty;
        }

        static long ParseKb(string line)
        {
            // Format: "VmSize:    12345 kB"
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], out var kb) ? kb : 0L;
        }
    }

    private static readonly Timer _memWatchdog = new(_ =>
    {
        try
        {
            var managed = GC.GetTotalMemory(forceFullCollection: false);
            long rss;
            try { rss = Process.GetCurrentProcess().WorkingSet64; }
            catch { return; }

            // Always emit a low-noise heartbeat so that even when the per-class
            // INIT_MEM/DISPOSE_MEM lines stop arriving (e.g. the ctor of the
            // *next* class is hanging mid-build) we still see the memory
            // trajectory in the trace file and can correlate it with the last
            // CTOR/INIT_START line above it.
            var line = $"managed={managed / 1024 / 1024}MiB rss={rss / 1024 / 1024}MiB";
            TestPhaseTrace("watchdog", "MEM_WATCHDOG", extra: line);

            if (rss >= MemCriticalBytes && Interlocked.Exchange(ref _criticalEmitted, 1) == 0)
            {
                TestPhaseTrace("watchdog", "MEM_CRITICAL",
                    extra: $"rss={rss / 1024 / 1024}MiB threshold={MemCriticalBytes / 1024 / 1024}MiB " +
                           "— OOM imminent. The class active at this line (see preceding " +
                           "INIT_START or CTOR) is the one driving the leak.");
            }
            else if (rss >= MemPressureBytes)
            {
                TestPhaseTrace("watchdog", "MEM_PRESSURE",
                    extra: $"rss={rss / 1024 / 1024}MiB threshold={MemPressureBytes / 1024 / 1024}MiB");
            }
        }
        catch
        {
            // Watchdog must never throw — it's hosted on the .NET timer queue
            // and an unhandled exception here would kill the process.
        }
    }, state: null, dueTime: WatchdogInterval, period: WatchdogInterval);

    /// <summary>
    /// Append one MEM line: managed heap, process RSS, GC counts, and deltas vs
    /// the previous MEM line. Called at end of INIT and end of DISPOSE for every
    /// test class that goes through this base. With CI's 7 GB cap, the test class
    /// that hangs CI is the one whose post-DISPOSE managed-heap delta stays
    /// positive instead of returning to ~baseline.
    ///
    /// <para><c>forceGc:true</c> at dispose forces a full collection so retained
    /// allocations stand out from in-flight collectible garbage. Skip the GC at
    /// init (post-init memory naturally includes mesh + hosted hubs that should
    /// be live).</para>
    /// </summary>
    private static void TestMemTrace(string testClass, string phase, bool forceGc)
    {
        try
        {
            if (forceGc)
            {
                // Two passes — finalizers may queue more work the first time round.
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }

            var managed = GC.GetTotalMemory(forceFullCollection: false);
            long rss;
            try { rss = Process.GetCurrentProcess().WorkingSet64; }
            catch { rss = 0; }

            long managedDelta, rssDelta;
            lock (_memLock)
            {
                managedDelta = _lastManagedHeapBytes == 0 ? 0 : managed - _lastManagedHeapBytes;
                rssDelta = _lastWorkingSetBytes == 0 ? 0 : rss - _lastWorkingSetBytes;
                _lastManagedHeapBytes = managed;
                _lastWorkingSetBytes = rss;
            }

            // Native-memory breakdown so we can tell ALC pin (RssFile growth from mmap'd
            // assembly .dll files) from native heap (RssAnon growth) from raw VmSize
            // expansion. On Windows this returns blanks — only Linux exposes this in
            // /proc/self/status, which is exactly the platform that's leaking.
            var nativeBreakdown = ReadProcSelfStatus();

            // ALC count and loaded-assembly count — if ALCs grow monotonically across
            // INIT_MEM lines, the leak is unloadable assembly-load contexts retaining
            // their native code/metadata pages. Each Roslyn compile or per-test
            // assembly load that doesn't unload bumps these counters.
            int alcCount = 0;
            try { foreach (var _ in AssemblyLoadContext.All) alcCount++; }
            catch { /* All is rarely-throwing but tolerate */ }
            int asmCount = 0;
            try { asmCount = AppDomain.CurrentDomain.GetAssemblies().Length; }
            catch { /* same */ }

            var extra =
                $"managed={managed / 1024 / 1024}MiB Δ{(managedDelta >= 0 ? "+" : "")}{managedDelta / 1024 / 1024}MiB"
                + $" rss={rss / 1024 / 1024}MiB Δ{(rssDelta >= 0 ? "+" : "")}{rssDelta / 1024 / 1024}MiB"
                + (string.IsNullOrEmpty(nativeBreakdown) ? "" : $" {nativeBreakdown}")
                + $" alc={alcCount} asm={asmCount}"
                + $" gc0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} gc2={GC.CollectionCount(2)}";
            TestPhaseTrace(testClass, phase, extra: extra);
        }
        catch
        {
            // Memory tracing must never throw out of the test pipeline.
        }
    }

    /// <summary>
    /// Called after ServiceProvider is built. Logs in the default admin user (DevLogin),
    /// pre-warms NodeType hubs that runtime CreateNode calls would otherwise try to
    /// auto-create (and recurse on), and sets up access rights so that access control
    /// allows operations in tests.
    /// </summary>
    public override async ValueTask InitializeAsync()
    {
        var sw = Stopwatch.StartNew();
        var name = GetType().Name;
        TestPhaseTrace(name, "INIT_START");
        try
        {
            await base.InitializeAsync();
            TestPhaseTrace(name, "INIT_BASE_DONE", sw.ElapsedMilliseconds);

            // Pre-warm BEFORE first Mesh access — DevLogin would otherwise trigger
            // Mesh construction which can hit the NodeType-hub recursion before
            // PreWarmNodeTypeHubs gets a chance to populate the cache.
            PreWarmNodeTypeHubs();
            TestPhaseTrace(name, "INIT_PREWARM_DONE", sw.ElapsedMilliseconds);

            TestUsers.DevLogin(Mesh);
            TestPhaseTrace(name, "INIT_DEVLOGIN_DONE", sw.ElapsedMilliseconds);

            await SetupAccessRightsAsync();
            TestPhaseTrace(name, "INIT_DONE", sw.ElapsedMilliseconds);
            TestMemTrace(name, "INIT_MEM", forceGc: false);
        }
        catch (Exception ex)
        {
            TestPhaseTrace(name, "INIT_ERROR", sw.ElapsedMilliseconds,
                $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Pre-creates the NodeType-definition hubs for built-in types whose
    /// instances are likely to be created at test runtime
    /// (<c>AccessAssignment</c>, <c>PartitionAccessPolicy</c>, …). Without
    /// this, the first runtime <c>IMeshService.CreateNode(node)</c> for one
    /// of those types triggers a chicken-and-egg recursion:
    /// CreateNodeRequest → mesh hub posts <c>GetCompilationPathRequest</c> to
    /// the type hub → routing creates the type hub → construction triggers
    /// another <c>GetCompilationPathRequest</c> → … stack overflow.
    /// Pre-warming forces the type hub into the
    /// <c>HostedHubsCollection</c> cache once so the next
    /// <c>GetCompilationPathRequest</c> finds it without re-creating.
    /// </summary>
    protected virtual void PreWarmNodeTypeHubs()
    {
        var meshConfig = Mesh.ServiceProvider.GetService<MeshConfiguration>();
        if (meshConfig is null) return;
        foreach (var nodeTypePath in new[] { "AccessAssignment", "PartitionAccessPolicy" })
        {
            if (meshConfig.Nodes.TryGetValue(nodeTypePath, out var typeNode)
                && typeNode.HubConfiguration is { } config)
            {
                _ = Mesh.GetHostedHub(new Address(nodeTypePath), config);
            }
        }
    }

    /// <summary>
    /// Sets up access rights for tests. Default is a no-op since PublicAdminAccess
    /// is added as a configuration node in ConfigureMesh (never persisted to disk).
    /// Override to set up custom permissions for security tests.
    /// </summary>
    protected virtual Task SetupAccessRightsAsync() => Task.CompletedTask;

    protected IMessageHub Mesh => ServiceProvider.GetRequiredService<IMessageHub>();
    protected IRoutingService RoutingService => ServiceProvider.GetRequiredService<IRoutingService>();

    /// <summary>
    /// Public API for creating nodes in tests.
    /// Prefer seeding data via <see cref="ConfigureMesh"/> + <c>builder.AddMeshNodes(...)</c>
    /// for static test data that is known at setup time.
    /// </summary>
    protected IMeshService NodeFactory => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// Public API for querying nodes in tests.
    /// </summary>
    protected IMeshService MeshQuery => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// Public API for resolving URL paths to hub addresses in tests.
    /// </summary>
    protected IPathResolver PathResolver => Mesh.ServiceProvider.GetRequiredService<IPathResolver>();

    /// <summary>
    /// Creates a test node using the public IMeshService API.
    /// Use this for dynamic test data. For static test data known at setup time,
    /// override <see cref="ConfigureMesh"/> and use <c>builder.AddMeshNodes(...)</c> instead.
    /// </summary>
    protected Task<MeshNode> CreateNodeAsync(MeshNode node, CancellationToken ct = default)
        => NodeFactory.CreateNode(node).ToTask(ct);

    /// <summary>
    /// Test-only Task wrapper around <see cref="MessageHubExtensions.Observe{TResponse}"/>:
    /// posts <paramref name="request"/> via <paramref name="hub"/> (defaults to <see cref="Mesh"/>)
    /// and awaits the typed response, propagating <see cref="DeliveryFailureException"/> /
    /// <see cref="TimeoutException"/> as the awaited Task's exception.
    /// <para>
    /// Use ONLY in test code — production hub handlers / click actions / services MUST stay
    /// on the observable form (<c>hub.Observe(request).Subscribe(...)</c>). The Task return
    /// is a deliberate test-ergonomics affordance, not a sanctioned production pattern.
    /// </para>
    /// <para>
    /// Default cancellation = <see cref="TestContext.Current"/>'s
    /// <see cref="ITestContext.CancellationToken"/> — never pass <c>default</c>.
    /// </para>
    /// </summary>
    protected Task<IMessageDelivery<TResponse>> AwaitResponseAsync<TResponse>(
        IRequest<TResponse> request,
        Func<PostOptions, PostOptions>? options = null,
        IMessageHub? hub = null,
        CancellationToken? ct = null)
        => (hub ?? Mesh).Observe(request, options)
            .FirstAsync()
            .ToTask(ct ?? TestContext.Current.CancellationToken);

    /// <summary>
    /// Canonical CQRS-correct read primitive for tests: the per-node hub's
    /// <see cref="MeshNodeReference"/> reducer, surfaced as
    /// <see cref="IObservable{MeshNode}"/> via
    /// <see cref="MeshNodeStreamExtensions.GetMeshNodeStream(IWorkspace, string)"/>.
    /// </summary>
    protected IObservable<MeshNode> ReadNode(string path)
        => Mesh.GetWorkspace().GetMeshNodeStream(path);

    private static readonly Address ReadHubAddress = new("test-reader", "shared");

    /// <summary>
    /// Convenience: targets the per-node hub's <see cref="MeshNodeReference"/>
    /// reducer via <see cref="GetDataRequest"/>, dispatched from a dedicated
    /// hosted reader hub so the response delivery never races the calling pump.
    /// Cancelled by <see cref="TestContext.Current"/>'s
    /// <see cref="ITestContext.CancellationToken"/> — every test inherits the
    /// same token automatically; never pass <c>default</c>.
    /// <para>
    /// Returns <c>null</c> only when the routing service reports
    /// <see cref="ErrorType.NotFound"/> (no per-node hub for this path — i.e.,
    /// the node was deleted or never existed). All other failures (timeout,
    /// cancellation, generic delivery failures) propagate so a hung lookup or a
    /// real bug surfaces as a test failure rather than a silent <c>null</c>.
    /// </para>
    /// <para>
    /// Replaces <c>await MeshQuery.QueryAsync&lt;MeshNode&gt;($"path:{X}").FirstOrDefaultAsync()</c>
    /// — see <c>Doc/Architecture/CqrsAndContentAccess.md</c>.
    /// </para>
    /// </summary>
    protected Task<MeshNode?> ReadNodeAsync(string path)
        => ReadNodeAsync(path, TestContext.Current.CancellationToken);

    /// <summary>
    /// Default upper bound for a single-node read in tests. Bounded so a misrouted
    /// request fails the test loudly with a <see cref="TimeoutException"/> instead
    /// of hanging the whole CI run until the inactivity guard aborts. 30 seconds
    /// is generous — typical per-node-hub activation + persistence load is sub-second.
    /// </summary>
    protected static readonly TimeSpan ReadNodeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Same as <see cref="ReadNodeAsync(string)"/> with an explicit token for
    /// tests that compose their own cancellation source on top of the
    /// test-context token. Composes the explicit token with a
    /// <see cref="ReadNodeTimeout"/> watchdog so a hung lookup surfaces quickly.
    /// </summary>
    protected async Task<MeshNode?> ReadNodeAsync(string path, CancellationToken ct)
    {
        var reader = Mesh.GetHostedHub(ReadHubAddress, c => c);
        // Wall-clock-bound the wait via Task.WhenAny — routing failures on a path
        // with no per-node hub don't always cancel cleanly through the inner observable.
        var requestTask = reader.Observe(new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(path)))
            .FirstAsync().ToTask(ct);
        var winner = await Task.WhenAny(requestTask, Task.Delay(ReadNodeTimeout, ct));
        if (winner != requestTask)
        {
            throw new TimeoutException(
                $"ReadNodeAsync('{path}') exceeded {ReadNodeTimeout.TotalSeconds:F0}s. " +
                $"Likely cause: per-node hub for '{path}' never activated (the node " +
                $"was never created, or routing has no entry for the address), or its " +
                $"MeshDataSource never emitted on the MeshNodeReference reducer.");
        }

        IMessageDelivery<GetDataResponse> response;
        try
        {
            response = await requestTask;
        }
        catch (Exception ex) when (IsNotFoundFailure(ex))
        {
            // Routing reports NotFound (no per-node hub, or no GetDataRequest
            // handler on the hub that does exist). Treat as absence.
            return null;
        }

        var node = response.Message.Data as MeshNode;
        if (node == null && response.Message.Data is System.Text.Json.JsonElement je)
            node = je.Deserialize<MeshNode>(Mesh.JsonSerializerOptions);

        return node;
    }

    /// <summary>
    /// Subscribes to <c>ObserveQuery&lt;MeshNode&gt;</c> for <paramref name="query"/>
    /// and folds the live deltas (Initial / Reset / Added / Updated / Removed) into
    /// a running path set. Returns the path set the moment <paramref name="predicate"/>
    /// is satisfied. Wall-clock-bounded by <see cref="ReadNodeTimeout"/>.
    /// <para>
    /// Use this when a write changed the catalog state (Active ↔ Deleted, hard
    /// delete, etc.) and a follow-up <see cref="QueryAsync"/> would race the
    /// catalog update. Lossless replacement for poll-loops on stale snapshots.
    /// </para>
    /// </summary>
    protected async Task<IReadOnlySet<string>> WaitForQueryPathSetAsync(
        string query,
        Func<IReadOnlySet<string>, bool> predicate,
        CancellationToken ct)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var observable = MeshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Scan(paths, (acc, change) =>
            {
                if (change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                {
                    acc.Clear();
                    foreach (var n in change.Items) if (n.Path is { } p) acc.Add(p);
                }
                else if (change.ChangeType is QueryChangeType.Added or QueryChangeType.Updated)
                {
                    foreach (var n in change.Items) if (n.Path is { } p) acc.Add(p);
                }
                else if (change.ChangeType is QueryChangeType.Removed)
                {
                    foreach (var n in change.Items) if (n.Path is { } p) acc.Remove(p);
                }
                return acc;
            })
            .Where(predicate);

        var set = await Task.WhenAny(
            observable.FirstAsync().ToTask(ct),
            Task.Delay(ReadNodeTimeout, ct).ContinueWith<IReadOnlySet<string>>(_ =>
                throw new TimeoutException(
                    $"WaitForQueryPathSetAsync('{query}') exceeded {ReadNodeTimeout.TotalSeconds:F0}s. " +
                    $"Likely cause: a write completed but the query catalog never reflected the change. " +
                    $"Current path set ({paths.Count}): [{string.Join(", ", paths)}]"), ct));
        return await set;
    }

    /// <summary>
    /// Recognise the two routing-failure flavours that mean "this path has no
    /// readable MeshNode" so the helper can return <c>null</c> instead of
    /// surfacing a noisy exception:
    /// <list type="bullet">
    ///   <item>"No node found for address X" — the path has no per-node hub at all
    ///     (deleted or never existed).</item>
    ///   <item>"No handler found for message type GetDataRequest" — the per-node
    ///     hub exists but doesn't register the data layer (e.g., a test hub
    ///     configured without <c>AddMeshDataSource</c>); semantically still
    ///     "no MeshNode to read" from the test's POV.</item>
    /// </list>
    /// Everything else (timeouts, validation failures, generic delivery failures
    /// with a different message) propagates so real bugs surface.
    /// </summary>
    private static bool IsNotFoundFailure(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is DeliveryFailureException dfe)
            {
                var msg = dfe.Message;
                // Routing's no-fallback failure message (current format).
                if (msg.StartsWith("No node found at ", StringComparison.Ordinal))
                    return true;
                // Older "No node found for address ..." prefix kept for back-compat
                // with tests that still match the previous routing wording.
                if (msg.StartsWith("No node found for address ", StringComparison.Ordinal))
                    return true;
                if (msg.StartsWith("No handler found for message type GetDataRequest", StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    protected IMessageHub GetClient(Func<MessageHubConfiguration, MessageHubConfiguration>? config = null)
    {
        return Mesh.ServiceProvider.CreateMessageHub(CreateClientAddress(), config ?? ConfigureClient)!;
    }

    protected virtual MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.WithType(typeof(MeshNodeReference), nameof(MeshNodeReference));
        // Pre-resolve RoutingService to avoid re-entrant DI resolution deadlock
        // during client hub's BuildupAction (which runs on a thread pool thread)
        var routingService = RoutingService;
        return configuration
            .AddMeshTypes()
            // Test client hubs accumulate the most leaked Observe subscriptions
            // (27 / 34 QUIESCE_TIMEOUTs measured on Hosting.Monolith.Test). Cap
            // their drain budget tight — the rest of the suite runs ~50 s faster.
            .WithQuiesceTimeout(TestQuiesceTimeout)
            .WithInitialization((h, _) => routingService.RegisterStreamAsync(h));
    }

    /// <summary>
    /// Wall-clock cap on test-class dispose. Anything longer is a hung handler /
    /// re-posting message loop / un-drained buffer — surface it as a loud
    /// <see cref="TimeoutException"/> with hub diagnostics rather than swallowing it.
    /// Budget breakdown (post-Quiescing-phase introduction):
    /// <list type="bullet">
    ///   <item>10 s for the new <see cref="MessageHubRunLevel.Quiescing"/> drain.</item>
    ///   <item>10 s for hostedHubs.Disposal (HostedHubsCollection's own cap).</item>
    ///   <item>~2 s for buffer drain.</item>
    /// </list>
    /// 30 s gives clean disposes ample headroom while still firing fast on a *real*
    /// callback / cascade leak. MessageHub itself has a 25 s safety-net force-
    /// completion path inside Dispose() that is also aligned with this budget.
    /// </summary>
    public static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Cadence at which we snapshot the hub's <see cref="IMessageHub.GetDisposalDiagnostics"/>
    /// while waiting for <see cref="IMessageHub.Disposal"/> — every tick lands in
    /// <see cref="TestBase.FileOutput"/> (xUnit test output) so a slow dispose shows
    /// progress incrementally instead of producing one giant snapshot at the timeout.
    /// </summary>
    private static readonly TimeSpan DisposeProgressInterval = TimeSpan.FromSeconds(3);

    public override async ValueTask DisposeAsync()
    {
        var testName = GetType().Name;
        var sw = Stopwatch.StartNew();
        Exception? disposeException = null;
        TestPhaseTrace(testName, "DISPOSE_START");
        try
        {
            FileOutput.WriteLine($"[DISPOSE] {testName}: Mesh.Dispose() invoking on {Mesh.Address}");
            Mesh.Dispose();
            TestPhaseTrace(testName, "DISPOSE_INVOKED", sw.ElapsedMilliseconds);

            using var cts = new CancellationTokenSource(DisposeTimeout);
            await WaitWithProgressAsync(testName, sw, cts.Token);
            FileOutput.WriteLine($"[DISPOSE] {testName}: Mesh.Disposal completed in {sw.ElapsedMilliseconds}ms");
            TestPhaseTrace(testName, "DISPOSE_DONE", sw.ElapsedMilliseconds);

            // Fail the test class' dispose if any hub hit Quiescing timeout. A leaked
            // Observe subscription that never received its reply is a real bug —
            // letting the suite continue silently turns it into a flaky timeout that
            // surfaces unpredictably in CI. Surfacing here makes the offending test
            // class fail loud with the offending request type / target / age.
            if (Mesh.AnyHubQuiescingTimedOut())
            {
                var summary = Mesh.GetQuiescingTimeoutSummary();
                TestPhaseTrace(testName, "DISPOSE_QUIESCE_LEAK", sw.ElapsedMilliseconds, summary);
                disposeException = new InvalidOperationException(
                    $"{testName} left Observe subscriptions pending past the Quiescing budget. " +
                    $"This is a leaked callback — the test posted a request and never received " +
                    $"(or never awaited) its reply. Pending callbacks at dispose:{Environment.NewLine}{summary}");
            }
        }
        catch (OperationCanceledException)
        {
            // The previous 30s silent-swallow hid this in a per-machine trace file.
            // Surface a loud TimeoutException with the hub's pending-state diagnostics
            // so the failure message identifies which hub / queue is still draining
            // and which handler (if any) is wedged on the action block.
            var diagnostics = SafeGetDiagnostics();
            FileOutput.WriteLine($"[DISPOSE] {testName}: TIMEOUT after {sw.ElapsedMilliseconds}ms");
            FileOutput.WriteLine(diagnostics);
            TestPhaseTrace(testName, "DISPOSE_TIMEOUT", sw.ElapsedMilliseconds, diagnostics);
            disposeException = new TimeoutException(
                $"{testName} dispose timed out after {DisposeTimeout.TotalSeconds:F0}s " +
                $"({sw.ElapsedMilliseconds}ms elapsed). Hub state at timeout:{Environment.NewLine}{diagnostics}");
        }
        catch (Exception ex)
        {
            var diagnostics = SafeGetDiagnostics();
            FileOutput.WriteLine($"[DISPOSE] {testName}: ERROR after {sw.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
            FileOutput.WriteLine(diagnostics);
            TestPhaseTrace(testName, "DISPOSE_ERROR", sw.ElapsedMilliseconds,
                $"{ex.GetType().Name}: {ex.Message}");
            disposeException = new InvalidOperationException(
                $"{testName} dispose failed after {sw.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}." +
                $" Hub state:{Environment.NewLine}{diagnostics}", ex);
        }
        finally
        {
            await base.DisposeAsync();
            // Force a full GC + finalizers + a second collection so any short-lived
            // garbage is gone and the MEM line shows what actually survived this
            // class. Across the run, look for classes whose post-DISPOSE managed
            // delta stays positive — those are the leaks driving CI's OOM.
            TestMemTrace(testName, "DISPOSE_MEM", forceGc: true);
        }

        if (disposeException != null)
            throw disposeException;
    }

    /// <summary>
    /// Awaits <see cref="IMessageHub.Disposal"/> with periodic progress snapshots.
    /// Every <see cref="DisposeProgressInterval"/>, dumps
    /// <see cref="IMessageHub.GetDisposalDiagnostics"/> to <see cref="TestBase.FileOutput"/>
    /// so a hang shows up as a stream of snapshots converging on the offending hub
    /// — instead of one big snapshot at the timeout.
    /// </summary>
    private async Task WaitWithProgressAsync(string testName, Stopwatch sw, CancellationToken ct)
    {
        var disposal = Mesh.Disposal!;
        while (!disposal.IsCompleted)
        {
            using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            progressCts.CancelAfter(DisposeProgressInterval);
            try
            {
                await disposal.WaitAsync(progressCts.Token);
                return; // disposal completed
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Progress tick fired before disposal completed — log a snapshot and loop.
                FileOutput.WriteLine(
                    $"[DISPOSE] {testName}: still waiting after {sw.ElapsedMilliseconds}ms — snapshot:");
                FileOutput.WriteLine(SafeGetDiagnostics());
            }
        }
    }

    private string SafeGetDiagnostics()
    {
        try { return Mesh.GetDisposalDiagnostics(); }
        catch (Exception diagEx) { return $"<failed to gather diagnostics: {diagEx.Message}>"; }
    }
}
