using System.Diagnostics;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.TestBase;

/// <summary>
/// Base class for integration tests that run against a full in-process monolith mesh
/// (persistence, messaging, DI, routing). Builds a per-test-class mesh, exposes the mesh hub
/// and routing service, hands out isolated client hubs, and enforces quiescing/dispose deadlines.
/// </summary>
public abstract class MonolithMeshTestBase : Fixture.TestBase
{
    // Unique-per-call. Prior versions returned the fixed `client/1` address;
    // when a test class uses ShareMeshAcrossTests, every test's GetClient()
    // overwrote streams[client/1] in RoutingService, and server-side sync
    // streams paired with the previous client/1 kept emitting DataChangedEvents
    // addressed to that slot — those events queued on the latest client/1's
    // action block ahead of new SubscribeAcks + initial-state emissions, blowing
    // through stream FirstAsync timeouts. Unique addresses partition the routing
    // table so leaked traffic from a prior test lands at a dead slot and is
    // dropped harmlessly. PageLoadingTest.ConcurrentRequests + the AI/Threading
    // suite hangs both traced to this.
    /// <summary>
    /// Creates a unique <c>client/{guid}</c> address. Each call returns a distinct address so leaked
    /// traffic from a prior test lands at a dead routing slot and is dropped harmlessly (see the note
    /// above for the shared-mesh hang this prevents).
    /// </summary>
    /// <returns>A fresh, process-unique client address.</returns>
    protected static Address CreateClientAddress() => new("client", Guid.NewGuid().ToString("N")[..12]);

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

    /// <summary>
    /// Per-mesh-instance filesystem root for the <see cref="FileSystemAssemblyStore"/>
    /// the monolith test base registers below. A fresh GUID per ConfigureMeshBase call
    /// means each test-class mesh gets its own store — so two test classes both
    /// compiling the same NodeType path (e.g., several LinkedIn tests all using
    /// <c>Systemorph/LinkedInProfile</c>) don't collide on the AssemblyStore key
    /// <c>(path, version)</c> and serve each other's compiled bytes. Process-pid
    /// scoping wasn't enough because all test classes share one xUnit process.
    /// Under the temp directory so OS cleanup reclaims at reboot.
    /// <para>🚨 This isolation only takes effect because <see cref="ConfigureMeshBase"/>
    /// REPLACES (RemoveAll + AddSingleton) the <see cref="IAssemblyStore"/> registration
    /// rather than <c>TryAdd</c>-ing it. <c>AddInMemoryPersistence</c> →
    /// <c>RegisterDefaultAssemblyStore</c> already <c>TryAddSingleton</c>s a PROCESS-PID-scoped
    /// store (<c>MeshWeaver-AssemblyStore-pid{pid}</c>) FIRST, so a second <c>TryAdd</c> here was a
    /// no-op and every test class shared that one pid store — two classes compiling the same path
    /// at a colliding version then served each other's bytes (the bulk-only "compiles but renders
    /// the wrong/empty area" failure: LinkedInProfile ↔ LinkedInTelemetry both use
    /// <c>Systemorph/LinkedInProfile</c>; the victim was whichever ran second).</para>
    /// </summary>
    // Assigned in the constructor — the per-CLASS path needs GetType().Name (the most-derived test
    // class), which a field initializer can't reference. See _compilationCacheDir.
    private readonly string _assemblyStoreRoot;

    /// <summary>
    /// Per-test-CLASS compilation cache directory (ProcessId + test-class name). SHARED across every
    /// <c>[Fact]</c> of one class, so a NodeType compiled by the first test is a cache HIT for the
    /// rest (1 compile + N hits — the within-class speedup that the old per-Guid path threw away),
    /// but ISOLATED across classes, so two classes that reuse a node path/name with different source
    /// (LinkedInProfile ↔ LinkedInTelemetry, both <c>Systemorph/LinkedInProfile</c>) never serve each
    /// other's bytes (the cross-class contamination). A cache HIT loads the existing DLL (no
    /// rewrite), so there is no write-vs-lingering-ALC lock contention either. Assigned in the
    /// constructor (needs the derived type name).
    /// </summary>
    private readonly string _compilationCacheDir;

    /// <summary>
    /// Base mesh configuration shared by every test: monolith mesh, in-memory persistence, row-level
    /// security, Graph + Space node types, the <c>TestData</c> partition, an isolated assembly store and
    /// compilation cache, and the test quiesce/request timeouts. Security tests call this directly to
    /// opt out of the default public-admin access added by <c>ConfigureMesh</c>.
    /// </summary>
    /// <param name="builder">The mesh builder to configure.</param>
    /// <returns>The same builder, configured, for fluent chaining.</returns>
    protected MeshBuilder ConfigureMeshBase(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddInMemoryPersistence()
            .AddRowLevelSecurity()
            .AddGraph()
            // Space is a core partition-owning NodeType (relocated from Blazor.Portal).
            // Register it by default so every Monolith test can create legitimate
            // top-level Space fixtures — the partition-write guard rejects top-level
            // creates of any non-partition-owning type. AddSpaceType is idempotent,
            // so tests that also call it explicitly are unaffected.
            .AddSpaceType()
            .AddMeshNodes(new MeshNode(TestPartition) { Name = "Test Data", NodeType = "Markdown" })
            // 🚨 REPLACE, don't TryAdd. AddInMemoryPersistence already TryAddSingleton'd the
            // pid-scoped default IAssemblyStore, so a TryAdd here would be a no-op and every test
            // class would share ONE process store (cross-class DLL contamination — see
            // _assemblyStoreRoot). RemoveAll the default first, then register the per-class store.
            .ConfigureServices(s =>
            {
                s.RemoveAll<IAssemblyStore>();
                return s.AddFileSystemAssemblyStore(_assemblyStoreRoot);
            })
            // Isolate the legacy CompilationCacheService disk cache to a
            // per-test-class directory. See _compilationCacheDir for the
            // file-lock contention this prevents.
            .ConfigureServices(s => s.Configure<CompilationCacheOptions>(o =>
                o.CacheDirectory = _compilationCacheDir))
            // Match the 60s RequestTimeout we apply to client hubs in
            // ConfigureClient — without this the mesh hub still defaults to 30s,
            // so any test that does Mesh.Observe(req, target=...) and waits for
            // the response hits a hub-level Timeout on CI cold starts long
            // before the per-node hub actually replies (CompilationPending /
            // CreateRelease symptom).
            .ConfigureHub(c => c
                .WithQuiesceTimeout(TestQuiesceTimeout)
                .WithRequestTimeout(TimeSpan.FromSeconds(60)));

    /// <summary>
    /// Default mesh configuration with PublicAdminAccess for in-memory tests.
    /// File-system tests should override and omit PublicAdminAccess (access comes from _Access/ files).
    /// Security tests should call ConfigureMeshBase() instead.
    /// </summary>
    protected virtual MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(TestUsers.PublicAdminAccess());

    /// <summary>
    /// Initializes the test base, wiring xUnit output and building (or reusing, in shared-mesh mode)
    /// the per-test-class mesh.
    /// </summary>
    /// <param name="output">xUnit output helper for the running test.</param>
    protected MonolithMeshTestBase(ITestOutputHelper output) : base(output)
    {
        // Per-test-CLASS cache paths (see _compilationCacheDir): shared across this class's [Fact]s,
        // isolated across classes. GetType().Name is the most-derived test class (not available in a
        // field initializer). Must be set BEFORE ConfigureMesh below, which reads both.
        var classCacheTag = $"{Environment.ProcessId}-{GetType().Name}";
        _assemblyStoreRoot = Path.Combine(Path.GetTempPath(), $"meshweaver-test-assembly-store-{classCacheTag}");
        _compilationCacheDir = Path.Combine(Path.GetTempPath(), $"meshweaver-test-mesh-cache-{classCacheTag}");

        // In shared-mesh mode, ConfigureMesh runs only on the FIRST instance of
        // this test class — the SP is cached statically and re-used by every
        // subsequent [Fact]. Skip the BuildHub registration on later instances
        // so their per-instance Services don't try to re-register the singleton
        // (and the wasted ConfigureMesh + builder allocation is avoided).
        if (!SharesMeshAcrossTests || !_sharedProviders.ContainsKey(GetType()))
        {
            var builder = ConfigureMesh(
                new(
                    c => c.Invoke(Services),
                    AddressExtensions.CreateMeshAddress()
                )
            );
            Services.AddSingleton(builder.BuildHub);
        }
        TestPhaseTrace(GetType().Name, "CTOR");
    }

    /// <summary>
    /// Opt-in: when overridden to <c>true</c>, the test class's
    /// <see cref="IServiceProvider"/> + <see cref="IMessageHub"/> are built once
    /// for the whole test class and reused for every <c>[Fact]</c>. This avoids
    /// the ~190 MiB native-heap leak per test method that otherwise piles up
    /// from Autofac's per-container Reflection.Emit-compiled service factories.
    ///
    /// <para><strong>Trade-off:</strong> tests in a class that opts in see
    /// shared mesh state — nodes/threads created in one test are visible in the
    /// next. Tests must use unique paths per test (Guids in node names is the
    /// typical pattern) and must not assume a clean slate. Tests that mutate
    /// shared state in incompatible ways must keep this off.</para>
    ///
    /// <para>Default <c>false</c>: existing tests get a fresh mesh per
    /// <c>[Fact]</c> as before. Opt in by overriding to <c>true</c> on the
    /// derived class.</para>
    /// </summary>
    protected virtual bool ShareMeshAcrossTests => false;

    /// <summary>
    /// 🚧 Master kill-switch for the shared-mesh cluster — currently <c>false</c>.
    ///
    /// <para>Keeping a per-class <see cref="IServiceProvider"/> alive in the static
    /// <c>_sharedProviders</c> pinned the mesh (and every hosted hub + subscription +
    /// MemoryCache timer it owns) for the whole testhost. A pinned class's mesh then
    /// interfered with later classes' per-test meshes — concretely the Acme bulk
    /// <c>UpdateNodeRequest@…/DefinePersona</c> never received its reply once the
    /// shared <c>AcmeSearchTest</c> mesh stayed live alongside the Todo meshes
    /// (passes in isolation, hangs in bulk).</para>
    ///
    /// <para>While this is <c>false</c> the ~60 <see cref="ShareMeshAcrossTests"/>
    /// overrides are IGNORED and every test gets a fresh, per-test-disposed mesh.
    /// Flip back to <c>true</c> (and restore the proper per-class lifetime via an
    /// <c>IClassFixture</c>) to re-enable the optimisation. "for now" — we will see
    /// what the memory/runtime cost is without it.</para>
    /// </summary>
    private bool ShareMeshClusterEnabled => false;

    /// <summary>Effective sharing decision: a class's opt-in only takes effect while
    /// the cluster kill-switch is on.</summary>
    private bool SharesMeshAcrossTests => ShareMeshAcrossTests && ShareMeshClusterEnabled;

    /// <summary>
    /// Opt-in: when overridden to <c>true</c>, <see cref="DisposeAsync"/> disposes
    /// this test's <see cref="MeshWeaver.Fixture.ServiceSetup.ServiceProvider"/> at the
    /// very END of teardown — after the mesh and every hosted hub have disposed. That
    /// tears down the Autofac container and every <see cref="IDisposable"/> singleton
    /// (the compilation cache, Roslyn metadata/workspaces, the TypeRegistry, the whole
    /// per-<c>[Fact]</c> DI graph) instead of letting it survive for the entire testhost
    /// process. For compile-heavy classes that is the dominant per-test managed retention.
    ///
    /// <para><strong>Default <c>false</c>.</strong> Disposing the SP eagerly once broke
    /// ~40 test classes that read a singleton handle AFTER teardown (their own
    /// <c>IDisposable.Dispose</c>, a fixture DI pattern, or a hub-disposal callback that
    /// re-resolves a service). A class may opt in ONLY once verified not to touch the SP
    /// post-teardown. Mutually exclusive with <see cref="ShareMeshAcrossTests"/> (the
    /// shared SP is cached statically and reused across the class's <c>[Fact]</c>s, so it
    /// must never be disposed per-test) — the dispose runs only on the non-shared path.</para>
    /// </summary>
    protected virtual bool DisposeServiceProviderOnTeardown => true;

    /// <summary>
    /// Cached <see cref="IServiceProvider"/> per test-class type, populated on
    /// the first instance of a class that opts in via
    /// <see cref="ShareMeshAcrossTests"/>. Never cleared during the testhost
    /// process — the cached SP outlives every test instance, which is exactly
    /// what avoids the per-test JIT compilation leak.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, IServiceProvider> _sharedProviders = new();

    /// <summary>
    /// Cross-process per-test phase trace. Single line per event into a fixed
    /// file so a developer can `tail -f` it during a hung suite run and spot the
    /// stuck test class without waiting for the run to finish.
    /// </summary>
    private static readonly string TestTraceLogPath =
        Path.Combine(Path.GetTempPath(), "meshweaver-test-trace.log");
    private static readonly object TestTraceLogLock = new();

    /// <summary>
    /// Cross-process per-class memory delta summary — one line per test class
    /// covering INIT_MEM → DISPOSE_MEM (after forced full GC). Surfaces leaks
    /// without forcing a developer to grep through the much busier per-event
    /// <see cref="TestTraceLogPath"/>. Path-stable so the workflow's
    /// "Collect test logs for artifact" step can always find it.
    /// </summary>
    private static readonly string MemoryDeltaLogPath =
        Path.Combine(Path.GetTempPath(), "meshweaver-memory-delta.log");
    private static readonly object MemoryDeltaLogLock = new();

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

    /// <summary>
    /// Writes one structured line to <see cref="MemoryDeltaLogPath"/> capturing
    /// the per-test-instance INIT_MEM → DISPOSE_MEM delta after a forced full
    /// GC. Format is grep-friendly: <c>HH:mm:ss.fff [TestClass] DELTA managed=…
    /// rss=… rssAnon=… unmanaged=… shared=…</c>. Cannot use <see cref="Microsoft.Extensions.Logging.ILogger"/>
    /// here — DisposeAsync runs after the test's logging scope has been torn down.
    /// <para>
    /// <c>rssAnon</c> = anonymous resident pages (native heap + JIT code + stacks)
    /// — Linux only, from <c>/proc/self/status</c>. This is where Autofac's
    /// Reflection.Emit factories pin memory that managed-heap GC can't touch and
    /// is the leak metric the user actually cares about.
    /// </para>
    /// <para>
    /// <c>unmanaged</c> = <c>rss − managed</c> — a portable approximation of native
    /// memory cost (works on Windows + macOS where rssAnon is not exposed). Includes
    /// JIT code, native heap, mapped files, and the kernel's per-process bookkeeping;
    /// noisier than rssAnon on Linux but the only signal available off-Linux.
    /// </para>
    /// </summary>
    private static void TestMemoryDelta(
        string testClass,
        long managedDelta,
        long rssDelta,
        long rssAnonDelta,
        long unmanagedDelta,
        bool shared)
    {
        try
        {
            var line = $"{DateTime.UtcNow:HH:mm:ss.fff} [{testClass}] DELTA "
                + $"managed={managedDelta / 1024 / 1024}MiB "
                + $"rss={rssDelta / 1024 / 1024}MiB "
                + $"rssAnon={rssAnonDelta / 1024 / 1024}MiB "
                + $"unmanaged={unmanagedDelta / 1024 / 1024}MiB "
                + $"shared={(shared ? 1 : 0)}";
            lock (MemoryDeltaLogLock)
                File.AppendAllText(MemoryDeltaLogPath, line + Environment.NewLine);
        }
        catch
        {
            // Tracing must never throw out of the test pipeline.
        }
    }

    /// <summary>
    /// Reads <c>RssAnon</c> (anonymous resident KB → bytes) from
    /// <c>/proc/self/status</c>. Linux only; returns 0 on Windows/macOS so
    /// callers compute <c>delta=0</c> there and rely on the <c>unmanaged</c>
    /// (rss − managed) metric instead.
    /// </summary>
    private static long ReadRssAnonBytes()
    {
        try
        {
            if (!File.Exists("/proc/self/status")) return 0;
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("RssAnon:", StringComparison.Ordinal))
                {
                    var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
                        return kb * 1024L;
                    return 0;
                }
            }
            return 0;
        }
        catch
        {
            return 0;
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

    // The static-cache size watchdog (KnownStaticCaches / GetStaticDictCount /
    // SnapshotKnownStaticCaches) was removed once the static-cache burn-down landed:
    // the "🚨 No static collections — ever" rule is now enforced at build time by
    // NoStaticCollectionsTest (MeshWeaver.PathResolution.Test), which fails on any
    // static mutable-collection field outside its classified allow-list — strictly
    // stronger than trending a hand-maintained list at runtime. See NoStaticState.md.

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
                // Final diagnostic — always log so dev machines with plenty of
                // RAM still surface the cumulative leak in the trace file
                // instead of letting it grow silently.
                var nativeBreakdown = ReadProcSelfStatus();
                var diag =
                    $"rss={rss / 1024 / 1024}MiB threshold={MemCriticalBytes / 1024 / 1024}MiB " +
                    $"managed={managed / 1024 / 1024}MiB " +
                    (string.IsNullOrEmpty(nativeBreakdown) ? "" : nativeBreakdown + " ");

                // Only FailFast on CI — a 7 GB ubuntu-latest runner is moments
                // from SIGKILL at this point, and the FailFast turns the silent
                // 6 m wallclock TIMEOUT into a loud "MEM_CRITICAL exceeded"
                // exit. Local dev machines with 32+ GB RAM should keep running
                // (the watchdog still logs the breach so the leak is visible).
                var onCi =
                    Environment.GetEnvironmentVariable("CI") is { Length: > 0 }
                    || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is { Length: > 0 };

                TestPhaseTrace("watchdog", onCi ? "MEM_CRITICAL_FAILFAST" : "MEM_CRITICAL",
                    extra: diag + (onCi
                        ? " — aborting testhost (CI). The class active at this line " +
                          "(see preceding INIT_START or CTOR) is the one driving the leak."
                        : " — local dev: continuing. The class active at this line " +
                          "(see preceding INIT_START or CTOR) is the one driving the leak."));

                if (onCi)
                {
                    // Force a flush so the trace lines reach disk before exit.
                    try { File.AppendAllText(TestTraceLogPath, string.Empty); } catch { }
                    try { File.AppendAllText(MemoryDeltaLogPath, string.Empty); } catch { }

                    Environment.FailFast(
                        $"MeshWeaver test infrastructure aborted: process RSS exceeded {MemCriticalBytes / 1024 / 1024} MiB " +
                        $"({rss / 1024 / 1024} MiB observed). This is the cumulative Autofac Reflection.Emit factory " +
                        $"leak from non-shared MonolithMeshTestBase classes. Diagnostic: {diag}");
                }
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
    // Forced full-GC at dispose is opt-in. Default OFF — across 200+ tests the
    // 2× GC2 + WaitForPendingFinalizers added ~1.5s per test (5+ minutes of
    // pure GC suite-wide). Enable when chasing a leak:
    //     MESHWEAVER_TEST_FORCE_GC=1
    private static readonly bool ForceGcAtDispose =
        Environment.GetEnvironmentVariable("MESHWEAVER_TEST_FORCE_GC") is { Length: > 0 } v
        && (v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));

    private static void TestMemTrace(string testClass, string phase, bool forceGc)
    {
        try
        {
            if (forceGc && ForceGcAtDispose)
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
    /// <summary>
    /// Per-instance INIT memory snapshot — captured immediately after
    /// <see cref="InitializeAsync"/> finishes so DisposeAsync can compute
    /// the post-cycle delta and write a single DELTA line per test instance
    /// to <see cref="MemoryDeltaLogPath"/>. Tracks managed heap, full RSS,
    /// AND unmanaged-as-anonymous (rssAnon on Linux) — the last is where
    /// the Autofac Reflection.Emit factory pin lives, plus any unmanaged
    /// allocations from native libraries the test pulls in.
    /// </summary>
    private long _instanceInitManagedBytes;
    private long _instanceInitRssBytes;
    private long _instanceInitRssAnonBytes;

    // Watchdog: track when the test method actually started so DisposeAsync
    // can fail loudly on silent deadlocks. xUnit v3's [Fact(Timeout=N)] is
    // cooperative cancellation — if a test ignores the ct, the await blocks
    // past the deadline and xUnit eventually reports Passed with the actual
    // (multi-minute) duration. The watchdog below catches that uniformly.
    private DateTimeOffset _testMethodStartedAt;
    /// <summary>Soft cap — anything above this gets a warning in the test log.</summary>
    protected virtual TimeSpan TestSoftDeadline => TimeSpan.FromSeconds(30);
    /// <summary>Hard cap — anything above this throws at DisposeAsync, failing the test class.</summary>
    protected virtual TimeSpan TestHardDeadline => TimeSpan.FromSeconds(60);

    /// <summary>
    /// xUnit async lifecycle hook run before each test: records the start time and performs any
    /// per-test setup (including access-rights setup) before the test body executes.
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

            // Start any IHostedService registered by ConfigureMesh. Tests don't
            // run a full Host (no IHostedLifecycleService machinery), so without
            // an explicit StartAsync sweep here, hosted services registered via
            // AddPartitionedPostgreSqlPersistence (PostgreSqlChangeListener) etc.
            // are constructed by DI but never activated — pg_notify never
            // reaches IDataChangeNotifier and synced queries freeze at Initial.
            foreach (var hosted in Mesh.ServiceProvider
                .GetServices<Microsoft.Extensions.Hosting.IHostedService>())
            {
                await hosted.StartAsync(TestContext.Current.CancellationToken);
            }
            TestPhaseTrace(name, "INIT_HOSTED_SERVICES_STARTED", sw.ElapsedMilliseconds);

            await SetupAccessRightsAsync();
            TestPhaseTrace(name, "INIT_DONE", sw.ElapsedMilliseconds);
            TestMemTrace(name, "INIT_MEM", forceGc: false);

            // Snapshot for the per-instance DELTA line written in DisposeAsync.
            _instanceInitManagedBytes = GC.GetTotalMemory(forceFullCollection: false);
            try { _instanceInitRssBytes = Process.GetCurrentProcess().WorkingSet64; }
            catch { _instanceInitRssBytes = 0; }
            _instanceInitRssAnonBytes = ReadRssAnonBytes();

            // Mark "test method about to run" — DisposeAsync uses this to
            // compute actual test-method duration and apply the soft/hard
            // deadlines (see TestSoftDeadline / TestHardDeadline).
            _testMethodStartedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            TestPhaseTrace(name, "INIT_ERROR", sw.ElapsedMilliseconds,
                $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Override <see cref="MeshWeaver.Fixture.ServiceSetup.Initialize()"/> so that test classes
    /// opting in via <see cref="ShareMeshAcrossTests"/> reuse a cached
    /// <see cref="IServiceProvider"/> across every <c>[Fact]</c>. The first
    /// instance of the class builds the SP normally and stores it in the
    /// static cache; every subsequent instance grabs that same SP and skips
    /// <see cref="MeshWeaver.Fixture.ServiceSetup.BuildServiceProvider()"/> entirely.
    ///
    /// <para><c>Buildup(this)</c> still runs per-instance because <c>[Inject]</c>
    /// fields/properties live on the test instance, not the SP.</para>
    /// </summary>
    protected override void Initialize()
    {
        if (SharesMeshAcrossTests)
        {
            ServiceProvider = _sharedProviders.GetOrAdd(GetType(), _ =>
            {
                base.Initialize();              // builds SP from this instance's Services
                return ServiceProvider;          // cache the result
            });
            // Per-instance buildup of [Inject] members on `this` — even when SP
            // is shared, the test instance's fields need filling.
            Configuration = ServiceProvider.GetRequiredService<IConfiguration>();
            ServiceProvider.Buildup(this);
            return;
        }
        base.Initialize();
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
        foreach (var nodeTypePath in new[] { "AccessAssignment", "PartitionAccessPolicy" })
        {
            var typeNode = Mesh.ServiceProvider.FindStaticNode(nodeTypePath);
            if (typeNode?.HubConfiguration is { } config)
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

    /// <summary>The mesh hub for the test-class mesh (the server-side message hub under test).</summary>
    protected IMessageHub Mesh => ServiceProvider.GetRequiredService<IMessageHub>();
    /// <summary>The mesh routing service, used to inspect or address per-node hubs in tests.</summary>
    protected IRoutingService RoutingService => ServiceProvider.GetRequiredService<IRoutingService>();

    /// <summary>
    /// Public API for creating nodes in tests.
    /// Prefer seeding data via <see cref="ConfigureMesh"/> + <c>builder.AddMeshNodes(...)</c>
    /// for static test data that is known at setup time.
    /// </summary>
    protected IMeshService NodeFactory => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// Seeds a TOP-LEVEL partition-root fixture under the System (platform) identity. A
    /// top-level node (empty namespace) IS a partition root, so
    /// <c>PartitionWriteGuardValidator</c> rejects a NON-System caller creating one whose
    /// NodeType does not own a partition (only <c>User</c>/<c>Space</c> do) — see
    /// <c>OrleansTopLevelPartitionGuardTest</c> / <c>McpFailureSurfacingTest</c>. Tests that
    /// need an ad-hoc top-level "org"/namespace of an ordinary type (Group/Code/Markdown/
    /// NodeType) must seed it this way: System is the legitimate partition provisioner
    /// (exactly as onboarding/migration do in production), so it bypasses the guard. Only the
    /// partition ROOT needs this — nested children create normally under the caller identity.
    /// </summary>
    protected Task<MeshNode> SeedTopLevel(MeshNode node)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        // NON-BLOCKING: never `.Wait()` here. A blocking wait runs on the caller's
        // thread, and under xUnit's single-threaded async sync-context
        // (maxParallelThreads:1) that thread IS the only one that can pump the
        // CreateNode completion — so `.Wait()` from an `async Task` test deadlocks
        // (the test then dies at its [Fact(Timeout=…)] cap with an empty message).
        // Bridge at the test edge instead: `.FirstAsync().ToTask()` and the caller
        // `await`s. Observable.Using opens the ImpersonateAsSystem scope on Subscribe
        // (here, synchronously, when .ToTask() subscribes) and keeps the System
        // AsyncLocal alive for the lifetime of the create — so the write authorises
        // as the platform provisioner (see PathResolutionService for the same shape).
        return Observable.Using(
                () => access.ImpersonateAsSystem(),
                _ => NodeFactory.CreateNode(node))
            // Subscribe off the test's single-threaded async sync-context (see
            // ObservableAssertions): keeps the create round-trip on the thread pool
            // instead of funnelling its continuations onto the one xUnit thread.
            .SubscribeOn(TaskPoolScheduler.Default)
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync()
            .ToTask();
    }

    /// <summary>
    /// Public API for querying nodes in tests.
    /// </summary>
    protected IMeshService MeshQuery => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// Public API for resolving URL paths to hub addresses in tests.
    /// </summary>
    protected IPathResolver PathResolver => Mesh.ServiceProvider.GetRequiredService<IPathResolver>();

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
    /// <para>
    /// Teardown-safety: when the hub is disposed before the response arrives,
    /// <c>MessageHub.CancelCallbacks</c> pushes <see cref="ObjectDisposedException"/>
    /// ("Hub … was disposed before the response arrived …") into the pending Observe
    /// subject. If a test ABANDONED this task (an earlier assertion threw before the
    /// await), a <em>faulted</em> task detonates at GC as an
    /// <c>UnobservedTaskException</c> → xUnit v3 "Catastrophic failure" poisoning the
    /// next test class (#228's capture). Rethrowing the disposal as an
    /// <see cref="OperationCanceledException"/> makes the async state machine's task
    /// CANCELED instead of faulted — canceled tasks never raise
    /// UnobservedTaskException, and an awaiting test still fails loudly with the
    /// original disposal exception attached as the cause. Real response errors
    /// (DeliveryFailure, Timeout) keep faulting the task unchanged.
    /// </para>
    /// </summary>
    protected async Task<IMessageDelivery<TResponse>> AwaitResponseAsync<TResponse>(
        IRequest<TResponse> request,
        Func<PostOptions, PostOptions>? options = null,
        IMessageHub? hub = null,
        CancellationToken? ct = null)
    {
        try
        {
            return await (hub ?? Mesh).Observe(request, options)
                .FirstAsync()
                .ToTask(ct ?? TestContext.Current.CancellationToken);
        }
        catch (ObjectDisposedException disposed)
        {
            // Hub teardown beat the response — cancellation, not failure (see remarks).
            throw new TaskCanceledException(
                $"Hub torn down before the response to {request.GetType().Name} arrived.", disposed);
        }
    }

    /// <summary>
    /// Canonical CQRS-correct read primitive for tests: the per-node hub's
    /// <see cref="MeshNodeReference"/> reducer, surfaced as
    /// <see cref="IObservable{MeshNode}"/> via
    /// <see cref="MeshNodeStreamExtensions.GetMeshNodeStream(IWorkspace, string)"/>.
    /// </summary>
    /// <summary>
    /// Authoritative single-node read as an <see cref="IObservable{T}"/>: the owner-hub round-trip via
    /// <c>Mesh.GetMeshNode</c> — NOT the cache stream (which can serve a stale Replay(1) buffer). Emits the
    /// node, or <c>null</c> when the routing service reports NotFound or the read exceeds
    /// <see cref="ReadNodeTimeout"/>. Assert reactively: <c>ReadNode(path).Should().Emit()</c> /
    /// <c>.Match(...)</c>. Never bridge back to a Task. (Replaced the old cache-stream ReadNode +
    /// the deleted ReadNodeAsync.)
    /// </summary>
    protected IObservable<MeshNode?> ReadNode(string path)
        => Mesh.GetMeshNode(path, ReadNodeTimeout)
            .Select(n => (MeshNode?)n)
            .Catch((Exception ex) =>
                ex is TimeoutException || IsNotFoundFailure(ex)
                    ? Observable.Return<MeshNode?>(null)
                    : Observable.Throw<MeshNode?>(ex));

    private static readonly Address ReadHubAddress = new("test-reader", "shared");

    /// <summary>
    /// Default upper bound for a single-node read in tests. Bounded so a misrouted
    /// request fails the test loudly with a <see cref="TimeoutException"/> instead
    /// of hanging the whole CI run until the inactivity guard aborts. 30 seconds
    /// is generous — typical per-node-hub activation + persistence load is sub-second.
    /// </summary>
    // Wall-clock cap on ReadNodeAsync. 60s matches the mesh hub's RequestTimeout
    // bump (ConfigureMeshBase) — keeps the watchdog above the underlying
    // hub-level Timeout so a slow-but-successful activation finishes inside
    // the budget on CI cold starts (Linux runners commonly take 35-45s for
    // the first per-node hub activation; the prior 30s tripped before the
    // hub responded — symptom: FullFlow_CreateThread + similar AI tests).
    protected static readonly TimeSpan ReadNodeTimeout = TimeSpan.FromSeconds(60);

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

    /// <summary>
    /// Per-test-method client tracking. Every GetClient() is appended; DisposeAsync
    /// disposes them all (including the shared-mesh path). Critical for
    /// ShareMeshAcrossTests classes: without it the prior test's client hub stays
    /// alive on the mesh, its server-side LayoutAreaReference / MeshNodeReference
    /// sync streams keep emitting DataChangedEvents to the (now-abandoned) client
    /// address, and the action block backs up across tests. Per-test dispose
    /// signals each client to RegisterForDisposal its routing-stream registration
    /// and its workspace, drops it from streams[address], and the server-side
    /// sync streams complete cleanly.
    /// </summary>
    private readonly List<IMessageHub> _clientsCreated = new();

    /// <summary>
    /// Creates a fresh client hub connected to the test mesh at a unique address, tracked for
    /// deterministic teardown at dispose. This is the standard way a test obtains a client.
    /// </summary>
    /// <param name="config">Optional override of the client hub configuration; defaults to <c>ConfigureClient</c>.</param>
    /// <returns>The newly created client message hub.</returns>
    protected IMessageHub GetClient(Func<MessageHubConfiguration, MessageHubConfiguration>? config = null)
    {
        var client = Mesh.ServiceProvider.CreateMessageHub(CreateClientAddress(), config ?? ConfigureClient)!;
        lock (_clientsCreated) _clientsCreated.Add(client);
        return client;
    }

    private void DisposeTestClients(string testName, Stopwatch sw)
    {
        IMessageHub[] snapshot;
        lock (_clientsCreated)
        {
            snapshot = _clientsCreated.ToArray();
            _clientsCreated.Clear();
        }
        if (snapshot.Length == 0) return;

        TestPhaseTrace(testName, "DISPOSE_CLIENTS_START", sw.ElapsedMilliseconds, $"count={snapshot.Length}");
        foreach (var client in snapshot)
        {
            try { client.Dispose(); }
            catch (Exception ex)
            {
                FileOutput.WriteLine(
                    $"[DISPOSE] {testName}: client {client.Address} dispose failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        TestPhaseTrace(testName, "DISPOSE_CLIENTS_DONE", sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Default client-hub configuration: registers the types a test client needs in its type registry
    /// and applies the test request timeout. Override to customize a test's client hub.
    /// </summary>
    /// <param name="configuration">The client hub configuration to mutate.</param>
    /// <returns>The configured client hub configuration.</returns>
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
            // Bump RequestTimeout to 60s — the framework default is 30s, which
            // a cold-cache NodeType compile on a slow CI runner can blow past
            // (e.g. FutuRe activation: ~17 s local, 35–40 s on GitHub-hosted
            // runners). The corresponding [Fact(Timeout = ...)] cap is what
            // bounds a genuinely hung test; longer RequestTimeout just stops
            // legitimate-but-slow activations from looking like missing-target
            // delivery failures.
            .WithRequestTimeout(TimeSpan.FromSeconds(60))
            .WithInitialization(h => h.RegisterForDisposal(routingService.RegisterStream(h)));
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
    /// while waiting for <see cref="IMessageHub.DisposalCompleted"/> — every tick lands in
    /// <see cref="MeshWeaver.Fixture.TestBase.FileOutput"/> (xUnit test output) so a slow dispose shows
    /// progress incrementally instead of producing one giant snapshot at the timeout.
    /// </summary>
    private static readonly TimeSpan DisposeProgressInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// xUnit async lifecycle hook run after each test: disposes the clients created during the test,
    /// tears down the mesh (unless shared), and enforces the dispose/quiesce deadlines — failing the
    /// test class on a leaked subscription or an over-budget dispose.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        var testName = GetType().Name;
        var sw = Stopwatch.StartNew();
        Exception? disposeException = null;
        TestPhaseTrace(testName, "DISPOSE_START");

        // Watchdog: compute actual test-method duration. xUnit v3's
        // [Fact(Timeout=N)] is cooperative — a test that ignores the ct
        // happily blocks past its declared timeout and gets reported as
        // Passed. We catch every such silent deadlock here, uniformly.
        TimeSpan? testMethodElapsed = _testMethodStartedAt == default
            ? null
            : DateTimeOffset.UtcNow - _testMethodStartedAt;
        if (testMethodElapsed is { } elapsed)
        {
            if (elapsed > TestHardDeadline)
            {
                var msg = $"{testName} ran {elapsed.TotalSeconds:F1}s — exceeded HARD deadline " +
                    $"({TestHardDeadline.TotalSeconds:F0}s). xUnit's [Fact(Timeout=...)] is " +
                    $"cooperative; this test almost certainly ignored its CancellationToken " +
                    $"and silently hung past its declared timeout. Fix: thread the test's " +
                    $"CancellationToken through every async call.";
                FileOutput.WriteLine("[WATCHDOG-HARD] " + msg);
                disposeException = new TimeoutException(msg);
            }
            else if (elapsed > TestSoftDeadline)
            {
                FileOutput.WriteLine(
                    $"[WATCHDOG-SOFT] {testName} ran {elapsed.TotalSeconds:F1}s — exceeded soft " +
                    $"deadline ({TestSoftDeadline.TotalSeconds:F0}s). Investigate the slow path.");
            }
        }

        // Shared-mesh classes never dispose the Mesh per-test — that's the entire
        // point of opting in (avoid rebuilding the Autofac container's compiled
        // factories for every [Fact]). The mesh outlives the testhost and
        // process exit reclaims it. Per-test base teardown (FileOutput unregister
        // etc.) still runs. (Gated by the cluster kill-switch — while disabled this
        // branch is never taken and every class falls through to per-test disposal.)
        if (SharesMeshAcrossTests)
        {
            // Drop the per-test client hubs FIRST — the shared mesh stays alive
            // for the rest of the class, but every client hub the test created
            // must be disposed here. Otherwise streams[client/<guid>] never
            // unregisters, server-side sync streams keep emitting to the
            // dropped client, and the per-class TestQuiesceTimeout fires only
            // when the suite is teardown (which by then is too late — the
            // intermediate tests were already slow from the action-block
            // congestion). See ConcurrentRequests deadlock (commit 02dd88f37)
            // and the AI/Threading suite 6-min CI timeout.
            DisposeTestClients(testName, sw);
            TestPhaseTrace(testName, "DISPOSE_SHARED_SKIP", sw.ElapsedMilliseconds);
            try { await base.DisposeAsync(); }
            catch (Exception ex)
            {
                TestPhaseTrace(testName, "DISPOSE_BASE_ERROR", sw.ElapsedMilliseconds,
                    $"{ex.GetType().Name}: {ex.Message}");
                throw;
            }
            TestMemTrace(testName, "DISPOSE_MEM", forceGc: true);
            WriteInstanceMemoryDelta(testName, shared: true);
            return;
        }

        // Non-shared path also benefits — Mesh.Dispose disposes all hosted hubs
        // including clients, but doing it via the tracked list is faster and
        // more deterministic (no race against the Mesh's own dispose).
        DisposeTestClients(testName, sw);

        try
        {
            FileOutput.WriteLine($"[DISPOSE] {testName}: Mesh.Dispose() invoking on {Mesh.Address}");
            // Capture the mesh-scoped teardown services BEFORE disposal — once Dispose()
            // begins, resolving DI races the scope teardown. We drain them after
            // DisposalCompleted (below) so the service scope isn't torn down while
            // offloaded ThreadPool I/O is still running or async cleanup is still
            // enqueued. See MeshTeardownExtensions.
            var ioPools = Mesh.ServiceProvider.GetService<IoPoolRegistry>();
            var asyncDisposeQueue = Mesh.ServiceProvider.GetService<AsyncDisposeQueue>();
            Mesh.Dispose();
            TestPhaseTrace(testName, "DISPOSE_INVOKED", sw.ElapsedMilliseconds);

            using var cts = new CancellationTokenSource(DisposeTimeout);
            await WaitWithProgressAsync(testName, sw, cts.Token);
            // DisposalCompleted only drains the action blocks + message round-trips.
            // Offloaded I/O (IIoPool) runs on the ThreadPool independently and is NOT
            // covered — wait for it here, BEFORE base.DisposeAsync tears down the
            // service scope, so no continuation resolves a disposed Autofac scope
            // (the unobserved "catastrophic ObjectDisposedException" class).
            if (ioPools is not null)
            {
                await ioPools.WhenDrained(DisposeTimeout).FirstAsync().ToTask();
                if (ioPools.TotalInFlight > 0)
                    FileOutput.WriteLine(
                        $"[DISPOSE] {testName}: I/O pools still report {ioPools.TotalInFlight} in-flight after drain wait");
            }
            // After all the sync stuff is disposed (and everyone has enqueued their async
            // cleanup), quiesce the async dispose queue before the scope closes below.
            if (asyncDisposeQueue is not null)
                await asyncDisposeQueue.DrainAsync(DisposeTimeout);
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

            // Opt-in (non-shared path only): release the per-[Fact] ServiceProvider so
            // its Autofac container + every IDisposable singleton (compilation cache,
            // Roslyn workspaces, TypeRegistry, the whole mesh DI graph) is torn down now
            // instead of surviving for the whole testhost process. The mesh + all hubs
            // have already disposed at this point (base.DisposeAsync above), so the ALC
            // unload hook and every RegisterForDisposal callback that re-resolves a
            // service have already run — nothing should read the SP after this.
            if (DisposeServiceProviderOnTeardown)
            {
                try { (ServiceProvider as IDisposable)?.Dispose(); }
                catch (Exception ex)
                {
                    TestPhaseTrace(testName, "DISPOSE_SP_ERROR", sw.ElapsedMilliseconds,
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }

            // Force a full GC + finalizers + a second collection so any short-lived
            // garbage is gone and the MEM line shows what actually survived this
            // class. Across the run, look for classes whose post-DISPOSE managed
            // delta stays positive — those are the leaks driving CI's OOM.
            TestMemTrace(testName, "DISPOSE_MEM", forceGc: true);
            WriteInstanceMemoryDelta(testName, shared: false);
        }

        if (disposeException != null)
            throw disposeException;
    }

    /// <summary>
    /// Computes the per-instance INIT → DISPOSE delta in managed heap, full RSS,
    /// rssAnon (Linux), and unmanaged-as-(rss−managed) and writes it as one line
    /// to <see cref="MemoryDeltaLogPath"/>. Called at the very end of
    /// <see cref="DisposeAsync"/> after GC has run.
    /// </summary>
    private void WriteInstanceMemoryDelta(string testName, bool shared)
    {
        try
        {
            // _instanceInitManagedBytes==0 means InitializeAsync never completed
            // (test threw before snapshot). Skip — no meaningful delta.
            if (_instanceInitManagedBytes == 0 && _instanceInitRssBytes == 0)
                return;

            var managedAfter = GC.GetTotalMemory(forceFullCollection: false);
            long rssAfter;
            try { rssAfter = Process.GetCurrentProcess().WorkingSet64; }
            catch { rssAfter = 0; }
            var rssAnonAfter = ReadRssAnonBytes();

            var managedDelta = managedAfter - _instanceInitManagedBytes;
            var rssDelta = _instanceInitRssBytes == 0 ? 0 : rssAfter - _instanceInitRssBytes;
            var rssAnonDelta = _instanceInitRssAnonBytes == 0 ? 0 : rssAnonAfter - _instanceInitRssAnonBytes;
            // unmanaged ≈ rss − managed: portable native-cost approximation that works
            // on Windows and macOS where rssAnon isn't exposed.
            var unmanagedBefore = _instanceInitRssBytes - _instanceInitManagedBytes;
            var unmanagedAfter = rssAfter - managedAfter;
            var unmanagedDelta = unmanagedAfter - unmanagedBefore;

            TestMemoryDelta(testName, managedDelta, rssDelta, rssAnonDelta, unmanagedDelta, shared);
        }
        catch
        {
            // Memory tracing must never throw out of the test pipeline.
        }
    }

    /// <summary>
    /// Awaits <see cref="IMessageHub.DisposalCompleted"/> with periodic progress snapshots.
    /// Every <see cref="DisposeProgressInterval"/>, dumps
    /// <see cref="IMessageHub.GetDisposalDiagnostics"/> to <see cref="MeshWeaver.Fixture.TestBase.FileOutput"/>
    /// so a hang shows up as a stream of snapshots converging on the offending hub
    /// — instead of one big snapshot at the timeout.
    /// </summary>
    private async Task WaitWithProgressAsync(string testName, Stopwatch sw, CancellationToken ct)
    {
        // Bridge the reactive completion to a Task once, at this test-teardown edge. Catch folds
        // a disposal fault into completion (teardown only waits for "done", not why).
        var disposal = Mesh.DisposalCompleted
            .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
            .FirstOrDefaultAsync()
            .ToTask();
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
