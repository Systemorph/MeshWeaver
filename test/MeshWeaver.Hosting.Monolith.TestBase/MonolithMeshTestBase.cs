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
using MeshWeaver.Testing.Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

using MeshWeaver.Compiler;
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
    /// Filesystem root of this test class's isolated <see cref="FileSystemAssemblyStore"/> —
    /// exposed so bake tests can stage the "share lost its bytes" states (clear the store and
    /// watch the level-triggered probe re-bake) without touching any NodeType record. Deleting
    /// under this root is safe: the store re-creates directories on the next Put.
    /// </summary>
    protected string AssemblyStoreRoot => _assemblyStoreRoot;

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
            .AddMeshNodes(TestUsers.DevLoginAdminAccess())
            // Real STATIC root Admin grant for the DevLogin identity (Roland).
            // Claim roles no longer grant node permissions (the paywall fix —
            // PermissionEvaluator): the login context's Roles=["Admin"] is a platform
            // capability, not data access, so without a real grant every test-body
            // CreateNode under the DevLogin circuit is denied. Chained HERE, in the base
            // every suite funnels through, because these are the harness's own identities;
            // Public, Anonymous, groups and per-test subjects are untouched, so security
            // assertions about them stay meaningful. A static grant also keeps the
            // evaluator's synchronous fast path alive where tests deliberately stall the
            // synced queries (CompileSourceSnapshotWedgeTest).
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
        if (!SharesMeshAcrossTests || !TestCollectionScope.Current!.Contains(GetType()))
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
    /// Effective sharing decision: a class's <see cref="ShareMeshAcrossTests"/> opt-in takes effect
    /// only while the test is running inside a <see cref="TestCollectionScope"/> — i.e. only in an
    /// assembly that declares
    /// <c>[assembly: TestFramework(typeof(MeshWeaver.Testing.Xunit.MeshTestFramework))]</c>.
    ///
    /// <para>🚨 <b>This used to be a hard-coded <c>false</c>, and the reason is the whole point of
    /// the scope.</b> The only sharing mechanism available was a <c>static</c> dictionary keyed by
    /// test class and never cleared. A static cache is not a lifetime: it pinned each class's mesh
    /// (and every hosted hub, subscription and <c>MemoryCache</c> timer it owns) for the entire
    /// testhost, where it went on interfering with later classes' per-test meshes — concretely, the
    /// Acme bulk <c>UpdateNodeRequest@…/DefinePersona</c> stopped receiving its reply once a shared
    /// <c>AcmeSearchTest</c> mesh stayed live alongside the Todo meshes (passed in isolation, hung
    /// in bulk). So the ~72 classes that asked to share were all ignored.</para>
    ///
    /// <para>A <see cref="TestCollectionScope"/> supplies the missing half: the shared provider is
    /// disposed when the COLLECTION ends, so it can no longer outlive the tests it was built for.
    /// An assembly that has not opted in has no scope, this stays <c>false</c>, and its behaviour is
    /// bit-for-bit what it was — every test gets a fresh, per-test-disposed mesh.</para>
    /// </summary>
    private bool SharesMeshAcrossTests => ShareMeshAcrossTests && TestCollectionScope.Current is not null;

    /// <summary>
    /// Keeps the shared <see cref="IServiceProvider"/> disposable by the collection scope, tearing
    /// it down exactly the way the per-test path does — <c>(sp as IDisposable)?.Dispose()</c>, with
    /// a failure traced rather than thrown, so a teardown fault cannot red a suite that passed.
    /// </summary>
    private sealed class SharedMeshProvider(IServiceProvider serviceProvider, string testClassName)
        : IDisposable
    {
        /// <summary>The provider every test of the class shares.</summary>
        public IServiceProvider ServiceProvider { get; } = serviceProvider;

        /// <inheritdoc/>
        public void Dispose()
        {
            try { (ServiceProvider as IDisposable)?.Dispose(); }
            catch (Exception ex)
            {
                Fixture.TestTraceLog.AppendPhase(
                    testClassName, "DISPOSE_SHARED_SP_ERROR", 0, $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

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
    /// Cross-process per-test phase trace. Single line per event into a fixed
    /// file so a developer can `tail -f` it during a hung suite run and spot the
    /// stuck test class without waiting for the run to finish.
    ///
    /// <para>The path and its write lock live on <see cref="Fixture.TestTraceLog"/>
    /// because this is no longer the only writer — <c>XUnitFileLogger</c> appends
    /// fault records (exception type + stack) to the same file. Two writers with two
    /// locks would interleave mid-line and corrupt both.</para>
    /// </summary>
    private static string TestTraceLogPath => Fixture.TestTraceLog.Path;

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
            // pid on every line. The file is shared by EVERY test host in a CI shard (one
            // process per project, all appending to the same fixed path), so without it the
            // tail of the log belongs to whichever project finished last — not necessarily
            // the one that crashed. A core dump is named `dotnet-<pid>.dmp`, so `grep pid=<n>`
            // is what ties the dump to the trace. Diagnosing the 2026-08-04 FutuRe SIGSEGV
            // started by reading another project's lines for exactly this reason.
            Fixture.TestTraceLog.AppendPhase(testClass, phase, elapsedMs, extra);
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
                    Fixture.TestTraceLog.Touch();
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

    // The exact IHostedService instances InitializeAsync started, so DisposeAsync can stop
    // THEM (not a fresh DI resolution) in reverse order BEFORE Mesh.Dispose() — mirroring the
    // generic host, which stops hosted services before container teardown. Without this stop,
    // a hosted service with an in-flight mesh request at test end (e.g. an InstanceSyncWorker
    // drain holding a GetMeshNode callback) races disposal and trips the Quiescing
    // leaked-callback guard (CI run 29197199611, InstanceSyncPushTest).
    private readonly List<Microsoft.Extensions.Hosting.IHostedService> _startedHostedServices = new();

    // Watchdog: track when the test method actually started so DisposeAsync
    // can fail loudly on silent deadlocks. xUnit v3's [Fact(Timeout=N)] is
    // cooperative cancellation — if a test ignores the ct, the await blocks
    // past the deadline and xUnit eventually reports Passed with the actual
    // (multi-minute) duration. The watchdog below catches that uniformly.
    private DateTimeOffset _testMethodStartedAt;
    // Monotonic twin of the wall-clock stamp above (#679): wall-clock keeps counting
    // through host suspension (laptop sleep, frozen runner) — a window in which the
    // test could not have made progress — and the sweep of 2026-07 recorded "hangs" of
    // 5523 s and 7288 s against a 720 s cap that all passed under caffeinate. Stopwatch
    // does not advance across suspension on Linux (CLOCK_MONOTONIC) and Intel macOS
    // (mach_absolute_time), so deadlines are enforced on it; on Apple-Silicon macOS the
    // OS monotonic clock may tick through sleep, making the divergence report below
    // best-effort there — but enforcement can never be WORSE than the wall clock it
    // replaces.
    private Stopwatch? _testMethodStopwatch;
    /// <summary>Soft cap — anything above this gets a warning in the test log.</summary>
    protected virtual TimeSpan TestSoftDeadline => TimeSpan.FromSeconds(30);
    /// <summary>
    /// Hard cap — anything above this throws at DisposeAsync, failing the test class.
    ///
    /// <para>🚨 MUST stay strictly ABOVE every in-test operation budget, above all
    /// <see cref="ReadNodeTimeout"/> (60 s). It used to be EQUAL to it, and that collision
    /// hid the real cause of a whole class of failures: a read that burned its full budget
    /// tripped this watchdog at the same instant, so the only error the author ever saw was
    /// the generic "you probably ignored your CancellationToken" — while the actual fault was
    /// a mesh read that never got its reply (ThreadAgentIntegrationTest, CI 2026-07-26). The
    /// operation's own loud, specific timeout must win the race; this watchdog is the
    /// backstop for hangs that have NO budget of their own, not a competitor to the ones
    /// that do. Widening it does not weaken any assertion — the test still fails, it just
    /// fails naming the operation instead of the harness.</para>
    /// </summary>
    protected virtual TimeSpan TestHardDeadline => DefaultHardDeadline;

    /// <summary>
    /// The default hard deadline: 90 s, overridable via the <c>MESHWEAVER_TEST_HARD_DEADLINE_SECONDS</c>
    /// environment variable.
    ///
    /// <para>Configurable rather than a literal because a baked timeout is a value people edit to
    /// get through a debugging session — which AGENTS.md forbids, since the committed number is a
    /// contract (CI is sized for a cold Roslyn compile on a fresh runner; a laptop is not). An env
    /// var lets a local run dial it without touching the tree, and lets a fixture that is TESTING
    /// the watchdog run against a small floor instead of burning the real one in wall clock.</para>
    ///
    /// <para>🚨 <b>static readonly, read ONCE</b> — and it must stay that way. This is not a cache
    /// (which the no-static-state rule bans); it is an immutable constant resolved at type init.
    /// It cannot become instance state or a DI lookup: <c>HardDeadlineHonoursFactTimeoutTest</c>'s
    /// static guard reads <see cref="TestHardDeadline"/> off a
    /// <c>RuntimeHelpers.GetUninitializedObject</c> instance — no constructor, no fields, no
    /// ServiceProvider — so anything instance-bound would throw or read garbage there.</para>
    ///
    /// <para>Malformed or non-positive values fall back to 90 s rather than failing: a typo in an
    /// env var must not turn every test in the assembly red with a harness error.</para>
    /// </summary>
    private static readonly TimeSpan DefaultHardDeadline =
        double.TryParse(
            Environment.GetEnvironmentVariable("MESHWEAVER_TEST_HARD_DEADLINE_SECONDS"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var configuredSeconds) && configuredSeconds > 0
            ? TimeSpan.FromSeconds(configuredSeconds)
            : TimeSpan.FromSeconds(90);

    /// <summary>
    /// Headroom the watchdog keeps ABOVE a test's own declared <c>[Fact(Timeout)]</c>, so the
    /// operation's specific failure always wins the race against this generic backstop.
    /// </summary>
    private static readonly TimeSpan HardDeadlineMargin = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The hard deadline actually enforced: <see cref="TestHardDeadline"/>, raised when the
    /// RUNNING test declares a larger <c>[Fact(Timeout)]</c> than it allows for.
    ///
    /// <para>Why derive instead of trusting the property: the invariant above —
    /// <i>"MUST stay strictly ABOVE every in-test operation budget"</i> — was previously per-class
    /// discipline, and roughly a dozen classes in Hosting.Monolith.Test silently broke it by
    /// declaring <c>[Fact(Timeout = 120_000 … 300_000)]</c> while inheriting the 90 s default.
    /// Those budgets were fiction: the watchdog killed the test at 90 s mid-wait and blamed the
    /// author's CancellationToken handling, hiding whatever actually ran long. The failures were
    /// CI-only because they only bite when an operation genuinely uses its budget — e.g. a cold
    /// Roslyn NodeType compile on a fresh runner (NodeTypeCompileParkTest, DynamicTypePreWarmerTest,
    /// CompileLeafStabilityTest).</para>
    ///
    /// <para>Reading the running test's own attribute makes the invariant STRUCTURAL: a class can
    /// no longer declare a budget the watchdog won't honour, and no future test can reintroduce
    /// the contradiction. An explicit <see cref="TestHardDeadline"/> override still applies as a
    /// FLOOR, so classes that deliberately widen it keep working.</para>
    /// </summary>
    private TimeSpan EffectiveHardDeadline
    {
        get
        {
            var floor = TestHardDeadline;
            if (CurrentFactTimeout() is not { } declared)
                return floor;
            var needed = declared + HardDeadlineMargin;
            return needed > floor ? needed : floor;
        }
    }

    /// <summary>
    /// The <c>[Fact(Timeout)]</c> of the test currently executing, or null when it declares none
    /// (or the context is unavailable — the watchdog then falls back to <see cref="TestHardDeadline"/>).
    /// </summary>
    private static TimeSpan? CurrentFactTimeout()
    {
        try
        {
            var testMethod = TestContext.Current?.TestMethod;
            if (testMethod?.GetType().GetProperty("Method")?.GetValue(testMethod)
                is not MethodInfo method)
                return null;
            var timeout = method.GetCustomAttribute<FactAttribute>()?.Timeout ?? 0;
            return timeout > 0 ? TimeSpan.FromMilliseconds(timeout) : null;
        }
        catch
        {
            // The watchdog must never be the reason a test fails to tear down.
            return null;
        }
    }

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
                _startedHostedServices.Add(hosted);
            }
            TestPhaseTrace(name, "INIT_HOSTED_SERVICES_STARTED", sw.ElapsedMilliseconds);

            // Access-rights provisioning is a SYSTEM act, in tests exactly as in production
            // (PluginGate / SystemInstall write grants under the System identity). It cannot run
            // under the DevLogin circuit: claim roles no longer grant node permissions (the
            // paywall fix — see PermissionEvaluator/PaywallRealGateShapeTests), so creating the
            // very first `_Access` grant would require the permission that grant confers.
            {
                var accessService = Mesh.ServiceProvider.GetService<AccessService>();
                using (accessService?.ImpersonateAsSystem()
                       ?? System.Reactive.Disposables.Disposable.Empty)
                {
                    await SetupAccessRightsAsync();
                }
            }
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
            _testMethodStopwatch = Stopwatch.StartNew();
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
            ServiceProvider = TestCollectionScope.Current!.GetOrCreate(GetType(), () =>
            {
                base.Initialize();              // builds SP from this instance's Services
                return new SharedMeshProvider(ServiceProvider, GetType().Name);
            }).ServiceProvider;
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
        //
        // 🚨 And it is NOT an Rx→Task bridge either (maintainer, 2026-08-30: "no ToTask
        // ever"). Rx's own bridge completes its TaskCompletionSource from INSIDE the
        // pipeline, without RunContinuationsAsynchronously, so the awaiting test resumes
        // INLINE on the signalling thread — still inside Rx's trampoline — and everything
        // the test then does inherits that. ReactiveCompletion.ObserveCompletion is the
        // sanctioned wait: it subscribes, keeps its error arm attached for a late fault,
        // and queues the continuation instead of running it on the signaller's thread.
        // 🚨 RunAsSystem, not a raw Observable.Using(access.ImpersonateAsSystem, …) — the SEALED
        // impersonation boundary (ImpersonationScopeExtensions). The raw shape opens the
        // AsyncLocal scope on the SUBSCRIBING thread and disposes it when the inner observable
        // terminates, which can be a DIFFERENT thread — latching System onto the subscriber or
        // restoring the previous identity onto the terminating thread. RunAsSystem enters at
        // Subscribe and leaves on the way out of that same subscription. This was the last
        // entry MonolithMeshTestBase held in ImpersonationScopeSites.allow; retiring it here
        // (Copilot review, #2748). The scope still spans the whole create, so the write
        // authorises as the platform provisioner.
        return access.RunAsSystem(() => NodeFactory.CreateNode(node))
            // Subscribe off the test's single-threaded async sync-context (see
            // ObservableAssertions): keeps the create round-trip on the thread pool
            // instead of funnelling its continuations onto the one xUnit thread.
            .SubscribeOn(TaskPoolScheduler.Default)
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync()
            .ObserveCompletion(
                ex => FileOutput.WriteLine(
                    $"[SEED] SeedTopLevel({node.Id}): create faulted AFTER the wait settled — "
                    + $"reported, not orphaned: {ex.GetType().Name}: {ex.Message}"))!;
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
    /// posts <paramref name="request"/> via <paramref name="hub"/> (defaults to
    /// <see cref="RequestHub"/>, NOT <see cref="Mesh"/> — see that property for why the router must
    /// not be an end of the delivery) and awaits the typed response, propagating
    /// <see cref="DeliveryFailureException"/> / <see cref="TimeoutException"/> as the awaited Task's
    /// exception.
    /// <para>
    /// 🚨 The default hub is a CLIENT, so a TARGET-LESS request is delivered to that client, which
    /// has no handlers. Node CRUD must therefore name its target —
    /// <c>o =&gt; o.WithTarget(RequestHub.NodeOperationTarget())</c>, or use
    /// <see cref="ObserveNodeOperation{TResponse}"/>. Before #2423 the default was <see cref="Mesh"/>
    /// and a target-less request silently EXECUTED on the router's action block, which is the
    /// starvation shape the <c>ROUTER_TRAFFIC</c> detector exists to surface.
    /// </para>
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
            // 🚨 ObserveCompletion, never an Rx→Task bridge (maintainer, 2026-08-30: "no
            // ToTask ever"). This is THE request/response wait the whole suite runs, so the
            // scheduler it resumes on shapes every test that uses it: Rx's own bridge
            // completes its TaskCompletionSource from inside the pipeline without
            // RunContinuationsAsynchronously, resuming the test INLINE on the responding
            // hub's action-block thread and leaving the rest of the test body on it.
            return (await (hub ?? RequestHub).Observe(request, options)
                .FirstAsync()
                .ObserveCompletion(
                    ex => FileOutput.WriteLine(
                        $"[REQUEST] {request.GetType().Name}: response faulted AFTER the wait "
                        + $"settled — reported, not orphaned: {ex.GetType().Name}: {ex.Message}"),
                    ct ?? TestContext.Current.CancellationToken))!;
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
    /// node, or <c>null</c> when the routing service reports NotFound. Assert reactively:
    /// <c>ReadNode(path).Should().Emit()</c> / <c>.Match(...)</c>. Never bridge back to a Task.
    /// (Replaced the old cache-stream ReadNode + the deleted ReadNodeAsync.)
    ///
    /// <para>🚨 A read that exceeds <see cref="ReadNodeTimeout"/> FAULTS with a
    /// <see cref="TimeoutException"/> naming the path and the reading hub's in-flight state — it
    /// does NOT emit null. Mapping the timeout to null (as this helper used to) meant a stalled
    /// mesh read was indistinguishable from a deleted node, so a test could burn its whole budget,
    /// silently assert against a null it never expected, PASS, and then die in DisposeAsync
    /// blaming the CancellationToken. A test that legitimately expects "absent" still gets null
    /// from the NotFound path.</para>
    /// </summary>
    protected IObservable<MeshNode?> ReadNode(string path)
        => ReadHub.GetMeshNode(path, ReadNodeTimeout)
            .Select(n => (MeshNode?)n)
            .Catch((Exception ex) =>
                IsNotFoundFailure(ex)
                    ? Observable.Return<MeshNode?>(null)
                    : Observable.Throw<MeshNode?>(ex));

    /// <summary>
    /// 🚨 Reads are issued HERE, never on <see cref="Mesh"/>. `Mesh` resolves the ROOT
    /// <c>mesh/{id}</c> hub, and a call routed through the root mesh hub always faults — it is
    /// transient routing infrastructure, not a call target. Every API surface (REST, MCP, gRPC,
    /// CLI) uses the portal hub for exactly this reason; this stable hosted hub is the test-side
    /// equivalent, and <see cref="ReadHubAddress"/> is deliberately CONSTANT so every read reuses
    /// one hub rather than minting a fresh one per call.
    ///
    /// <para>The symptom this cures is a <c>GetDataRequest</c> that never gets an answer, surfacing
    /// 60 s later as <c>GetMeshNode('…') timed out … the owning per-node hub never answered</c>.
    /// The diagnostic's <c>Hub mesh/…</c> field names the root hub as the caller — that field IS
    /// the diagnosis, and misreading it as "caller healthy, target silent" sends you hunting a
    /// load-dependent race that does not exist (ThreadAgentIntegrationTest, which never reproduced
    /// locally at any load).</para>
    /// </summary>
    private IMessageHub ReadHub => Mesh.GetHostedHub(ReadHubAddress, c => c.AddData());

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

    private IMessageHub? _requestHub;
    private readonly object _requestHubLock = new();

    /// <summary>
    /// 🚨 The DEFAULT request origin for a test that addresses a NODE — use this, never
    /// <see cref="Mesh"/>. One lazily-created <see cref="GetClient"/> client hub per test method,
    /// disposed with the rest at teardown.
    ///
    /// <para><see cref="Mesh"/> resolves the ROOT <c>mesh/{id}</c> hub — the mesh's ROUTER.
    /// <c>Mesh.Observe(request, o =&gt; o.WithTarget(new Address(nodePath)))</c> therefore makes the
    /// router an END of the delivery in BOTH directions: the request goes out stamped
    /// <c>Sender = mesh/{id}</c> and its response is addressed straight back at <c>mesh/{id}</c>.
    /// That is exactly what the <c>ROUTER_TRAFFIC</c> detector reports
    /// (<c>RouterTrafficRule.RoleOf</c>), and because the detector fires once per
    /// role+message-type it costs every affected test class two <c>[Error]</c> lines — errors that
    /// everyone learns to ignore, which is how a real router-starvation report gets missed (#2423).
    /// It is also the wrong SHAPE: no production caller drives the mesh from the router. Requests
    /// come from an MCP session hub, the Blazor portal hub, a layout-area hub, a per-node hub — a
    /// hub whose traffic the router merely FORWARDS. This client hub is the test-side equivalent,
    /// and the router is then only a hop, which the detector deliberately does not report.</para>
    ///
    /// <para>Two shapes do NOT use this property directly. A test whose subject IS the router —
    /// the mesh hub's own drain, or its callback bookkeeping — keeps using <see cref="Mesh"/>, and
    /// says so at the site. Node CRUD uses <see cref="ObserveNodeOperation{TResponse}"/>: it needs
    /// an explicit target as well as a non-router origin, because a target-less delivery is handled
    /// by the POSTING hub and this one is a client with no handlers.</para>
    ///
    /// <para>Sibling of the private <c>ReadHub</c>, which makes the same argument for single-node
    /// reads. A test that needs a client with extra wiring (<c>AddData()</c>, a layout client, a
    /// second isolated identity) still calls <see cref="GetClient"/> directly — this property is
    /// the default, not the only option; classes that need it wired differently override
    /// <see cref="ConfigureClient"/>.</para>
    /// </summary>
    protected IMessageHub RequestHub
    {
        get
        {
            lock (_requestHubLock)
                return _requestHub ??= GetClient();
        }
    }

    /// <summary>
    /// Issues a NODE-CRUD request (<c>CreateNodeRequest</c>, <c>CreateNodesRequest</c>,
    /// <c>CreateOrUpdateNodeRequest</c>, <c>DeleteNodeRequest</c>, <c>MoveNodeRequest</c>,
    /// <c>CopyNodeRequest</c>) exactly as production does: posted from a client hub and ADDRESSED at
    /// <see cref="MeshExtensions.NodeOperationTarget"/> — the mesh's dedicated
    /// <c>portal/nodeops-{meshId}</c> execution hub.
    ///
    /// <para>🚨 Node CRUD is the one shape where "not the router" is not enough. A test used to
    /// either aim it at <c>Mesh.Address</c> or leave it TARGET-LESS, and a target-less delivery is
    /// handled by the POSTING hub — both of which ran the write on the router's single-threaded
    /// action block. A burst there starves the routing the mesh hub exists to do (prod 2026-06-11:
    /// "11× CreateOrUpdateNodeRequest + 3× CreateNodeRequest@mesh/&lt;self&gt; stale &gt;60s while
    /// real user SubscribeRequests starved"). <c>MeshService</c> stopped doing that; this helper is
    /// how a test that posts the raw request stops too. Both ends of the resulting delivery are the
    /// nodeops hub, so nothing about it touches the router — the shape
    /// <c>NodeOperationOriginTest</c> pins by reading <c>RouterTrafficRule.RoleOf</c> over a real
    /// delivery.</para>
    ///
    /// <para>Cold: the underlying <c>Observe</c> posts on CALL, so the request is issued when this
    /// method runs, and the returned observable replays the response to any later subscriber.</para>
    /// </summary>
    /// <typeparam name="TResponse">The response type declared by <paramref name="request"/>.</typeparam>
    /// <param name="request">The node-operation request.</param>
    /// <param name="options">
    /// Optional further delivery configuration (an explicit <c>AccessContext</c>, properties). It is
    /// applied AFTER the target, so a caller that genuinely needs a different target can still
    /// override it.
    /// </param>
    /// <returns>The response delivery observable.</returns>
    protected IObservable<IMessageDelivery<TResponse>> ObserveNodeOperation<TResponse>(
        IRequest<TResponse> request,
        Func<PostOptions, PostOptions>? options = null)
    {
        var hub = RequestHub;
        var target = hub.NodeOperationTarget();
        return hub.Observe(request, o =>
        {
            o = o.WithTarget(target);
            return options is null ? o : options(o);
        });
    }

    /// <summary>
    /// Disposes every client hub this test created AND JOINS each one's teardown before returning.
    ///
    /// <para>🚨 The join is not tidiness. On the <see cref="SharesMeshAcrossTests"/> path this method
    /// is the LAST thing that touches the mesh — <c>DisposeAsync</c> returns straight after it and
    /// the next <c>[Fact]</c> begins — so a bare <c>Dispose()</c> here starts the client's teardown
    /// (action-block drain, sync-stream unregistration, registrant callbacks) and then hands the
    /// mesh to the next test while all of that is still running on other threads. The observed
    /// result is a burst of <c>[SYNC_STREAM] Not setting … — stream is disposed</c> followed by a
    /// use-after-dispose that takes the host down with exit 139, naming no test. On the non-shared
    /// path the very next statements stop the hosted services and dispose the Mesh, which is the
    /// same race one level up.</para>
    ///
    /// <para>Bounded per client and LOUD on expiry: a wedged client must fail visibly, not hang the
    /// suite. The waits are sequential on purpose — a client that will not finish is named
    /// individually, which is what the trace file needs to be attributable.</para>
    /// </summary>
    private async Task DisposeTestClientsAsync(string testName, Stopwatch sw)
    {
        lock (_requestHubLock) _requestHub = null;
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
            await client.DisposeAndJoinAsync(
                message => FileOutput.WriteLine($"[DISPOSE] {testName}: {message}"),
                DisposeTimeout);
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
        TimeSpan? wallElapsed = _testMethodStartedAt == default
            ? null
            : DateTimeOffset.UtcNow - _testMethodStartedAt;
        var monotonicElapsed = _testMethodStopwatch?.Elapsed;
        // Enforce on the monotonic clock (#679): the wall clock includes host-suspension
        // windows in which the test could not have made progress — failing it for that
        // time misattributes a sleep/stall to the test's CancellationToken handling.
        var testMethodElapsed = monotonicElapsed ?? wallElapsed;
        if (wallElapsed is { } wall && monotonicElapsed is { } mono &&
            wall - mono > TimeSpan.FromSeconds(10))
        {
            FileOutput.WriteLine(
                $"[WATCHDOG-SUSPEND] {testName}: wall-clock elapsed {wall.TotalSeconds:F1}s vs " +
                $"monotonic {mono.TotalSeconds:F1}s — the host was suspended ~" +
                $"{(wall - mono).TotalSeconds:F0}s during this test (laptop sleep / runner stall). " +
                "Deadlines are enforced on the monotonic clock, so the suspension window alone " +
                "cannot fail the test.");
        }
        if (testMethodElapsed is { } elapsed)
        {
            var hardDeadline = EffectiveHardDeadline;
            if (elapsed > hardDeadline)
            {
                var msg = $"{testName} ran {elapsed.TotalSeconds:F1}s — exceeded HARD deadline " +
                    $"({hardDeadline.TotalSeconds:F0}s). xUnit's [Fact(Timeout=...)] is " +
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
        // factories for every [Fact]). It is torn down when the COLLECTION ends:
        // TestCollectionScope disposes the SharedMeshProvider it cached, which is the
        // lifetime the old static dictionary never had (it leaked to process exit and
        // interfered with later classes' meshes). Per-test base teardown (FileOutput
        // unregister etc.) still runs. Gated by SharesMeshAcrossTests — an assembly
        // with no collection scope never takes this branch.
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
            await DisposeTestClientsAsync(testName, sw);
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
        await DisposeTestClientsAsync(testName, sw);

        try
        {
            // Stop the hosted services InitializeAsync started — in reverse order, BEFORE
            // Mesh.Dispose(), exactly like the generic host stops hosted services before
            // container teardown. A still-running hosted service (change-feed listener,
            // InstanceSync worker mid-drain, …) can hold an in-flight Observe callback on a
            // hub; disposing the mesh underneath it leaves that callback pending and the
            // Quiescing leaked-callback guard below fails the test for a lifecycle race the
            // production host can never hit. Per-service catch: a failing StopAsync must not
            // mask the disposal diagnostics that follow.
            using (var stopCts = new CancellationTokenSource(DisposeTimeout))
            {
                for (var i = _startedHostedServices.Count - 1; i >= 0; i--)
                {
                    var hosted = _startedHostedServices[i];
                    // WaitAsync enforces the budget at the await site: a StopAsync that
                    // ignores its cancellation token would otherwise hang DisposeAsync
                    // forever, before the DisposeTimeout-guarded diagnostics below.
                    try { await hosted.StopAsync(stopCts.Token).WaitAsync(stopCts.Token); }
                    catch (Exception ex)
                    {
                        TestPhaseTrace(testName, "DISPOSE_HOSTED_STOP_ERROR", sw.ElapsedMilliseconds,
                            $"{hosted.GetType().Name}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                _startedHostedServices.Clear();
            }
            TestPhaseTrace(testName, "DISPOSE_HOSTED_SERVICES_STOPPED", sw.ElapsedMilliseconds);

            FileOutput.WriteLine($"[DISPOSE] {testName}: Mesh.Dispose() invoking on {Mesh.Address}");
            // Capture the mesh-scoped teardown services BEFORE disposal — once Dispose()
            // begins, resolving DI races the scope teardown. We drain them after
            // DisposalCompleted (below) so the service scope isn't torn down while
            // offloaded ThreadPool I/O is still running or async cleanup is still
            // enqueued. See MeshTeardownExtensions.
            var ioPools = Mesh.ServiceProvider.GetService<IoPoolRegistry>();
            var asyncDisposeQueue = Mesh.ServiceProvider.GetService<AsyncDisposeQueue>();
            var teardownSignal = Mesh.ServiceProvider.GetService<MeshTeardownSignal>();
            Mesh.Dispose();
            TestPhaseTrace(testName, "DISPOSE_INVOKED", sw.ElapsedMilliseconds);

            using var cts = new CancellationTokenSource(DisposeTimeout);
            await WaitWithProgressAsync(testName, sw, cts.Token);
            // 🚨 The three post-dispose phases are traced INDIVIDUALLY, because the gap between
            // DISPOSE_INVOKED and DISPOSE_DONE was the one stretch of teardown that could hang
            // with nothing at all written to the one file CI keeps: the hub-side wait above ends
            // in a DISPOSE_TIMEOUT trace, but DrainAll() below is synchronous, unlogged and (until
            // #2394) unbounded — so a wedge there presented as an 8&#160;min whole-assembly
            // exit=124 naming no test and no phase. These markers make that phase attributable
            // from the trace file alone, which is all a killed host leaves behind.
            TestPhaseTrace(testName, "DISPOSE_IOPOOL_DRAIN_START", sw.ElapsedMilliseconds);
            // DisposalCompleted only drains the action blocks + message round-trips.
            // Offloaded I/O (IIoPool) runs on the ThreadPool independently and is NOT
            // covered — CANCEL + JOIN it here, BEFORE base.DisposeAsync tears down the
            // service scope, so no continuation resolves a disposed Autofac scope AND no
            // ThreadPool leaf dereferences a collectible node ALC's freed metadata after
            // unload (the teardown use-after-unload SIGSEGV). A live change-feed leaf never
            // completes on its own, so a WAIT-only drain would time out and let the scope
            // dispose under it; DrainAll() cancels the leaf so it stops, then joins.
            // 🚨 The residual must NAME ITS POOL, and it must do so through TestPhaseTrace rather
            // than an ILogger. IoPoolRegistry.DrainAll already logs a warning naming the pool, and
            // that warning is structurally invisible here: the mesh's log sink stops capturing at
            // Mesh.Dispose(), so of 294 dispose windows in the #2616 shard-2 trx, ZERO carry an
            // ILogger line at any level. TestPhaseTrace writes the file CI keeps and is the only
            // thing that survives both dispose and an exit=124 host kill. Without the name a
            // residual reads as an anonymous "1" — which is how #2578 and #2616 both ended with
            // nothing to act on. (Query=1 and Compile=1 are different bugs.)
            IReadOnlyList<IoPoolRegistry.PoolResidual> residualByPool = [];
            var leakedIoLeaves = ioPools is null ? 0 : ioPools.DrainAll(out residualByPool);
            TestPhaseTrace(testName, "DISPOSE_IOPOOL_DRAIN_DONE", sw.ElapsedMilliseconds,
                $"leakedIoLeaves={leakedIoLeaves}"
                + (residualByPool.Count > 0 ? $" pools=[{string.Join(", ", residualByPool)}]" : ""));
            // After all the sync stuff is disposed (and everyone has enqueued their async
            // cleanup), quiesce the async dispose queue before the scope closes below.
            var asyncDisposeClean = asyncDisposeQueue is null
                || await asyncDisposeQueue.DrainAsync(DisposeTimeout);
            TestPhaseTrace(testName, "DISPOSE_ASYNC_QUEUE_DRAINED", sw.ElapsedMilliseconds,
                $"clean={asyncDisposeClean}");

            // The terminal signal — DISPOSE_DONE is only true when this report is CLEAN. Fire it
            // before the scope disposes below so any subscriber ordering on "all is done" (ALC
            // unload hooks, diagnostics) observes the truthful terminal state, dirty or not.
            var teardownReport = new TeardownReport(leakedIoLeaves, asyncDisposeClean);
            teardownSignal?.SignalCompleted(teardownReport);
            FileOutput.WriteLine($"[DISPOSE] {testName}: Mesh.Disposal completed in {sw.ElapsedMilliseconds}ms — {teardownReport}");
            TestPhaseTrace(testName, "DISPOSE_DONE", sw.ElapsedMilliseconds, teardownReport.ToString());

            // 🚨 A dirty teardown FAILS the class, exactly like the quiesce-leak below. The old
            // code logged "I/O pools still report N in-flight" to a per-machine file and carried
            // on — and the surviving leaf then dereferenced an unloading node ALC 8 ms into the
            // NEXT test's INIT, killing the whole test host with a SIGSEGV that nothing could
            // attribute (FutuRe.Test, dump dotnet-3029). Failing HERE names the class that
            // leaked, while the evidence still exists.
            if (!teardownReport.Clean)
            {
                TestPhaseTrace(testName, "DISPOSE_DIRTY_TEARDOWN", sw.ElapsedMilliseconds, teardownReport.ToString());
                disposeException = new InvalidOperationException(
                    $"{testName} teardown left work RUNNING: {teardownReport}. " +
                    "A pooled I/O leaf or async cleanup ignored its cancellation token; the service " +
                    "scope (and any collectible node ALC) is about to be torn down over live code — " +
                    "the use-after-unload SIGSEGV. Fix the leaf that will not cancel; do not widen the drain budget.");
            }

            // Fail the test class' dispose if any hub hit Quiescing timeout. A leaked
            // Observe subscription that never received its reply is a real bug —
            // letting the suite continue silently turns it into a flaky timeout that
            // surfaces unpredictably in CI. Surfacing here makes the offending test
            // class fail loud with the offending request type / target / age.
            if (Mesh.AnyHubQuiescingTimedOut())
            {
                var summary = Mesh.GetQuiescingTimeoutSummary();
                TestPhaseTrace(testName, "DISPOSE_QUIESCE_LEAK", sw.ElapsedMilliseconds, summary);
                var quiesceLeak = new InvalidOperationException(
                    $"{testName} left Observe subscriptions pending past the Quiescing budget. " +
                    $"This is a leaked callback — the test posted a request and never received " +
                    $"(or never awaited) its reply. Pending callbacks at dispose:{Environment.NewLine}{summary}");
                // Never clobber a dirty-teardown failure recorded above — both findings matter.
                disposeException = disposeException is null
                    ? quiesceLeak
                    : new AggregateException(disposeException, quiesceLeak);
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
    /// Waits for <see cref="IMessageHub.DisposalCompleted"/> with periodic progress snapshots.
    /// Every <see cref="DisposeProgressInterval"/>, dumps
    /// <see cref="IMessageHub.GetDisposalDiagnostics"/> to <see cref="MeshWeaver.Fixture.TestBase.FileOutput"/>
    /// so a hang shows up as a stream of snapshots converging on the offending hub
    /// — instead of one big snapshot at the timeout.
    ///
    /// <para>🚨 <b>Both halves are SUBSCRIPTIONS now (#2301, #2488 site 3).</b> This used to bridge
    /// the reactive signal to a Task with <c>Catch(…).FirstOrDefaultAsync().ToTask()</c> and then
    /// POLL that Task in a <c>while (!disposal.IsCompleted)</c> loop — the canonical
    /// "wait for disposal" every test in the suite runs, so it shaped what teardown failures look
    /// like everywhere.</para>
    ///
    /// <para>The first defect was DEADLOCK, not the discarded fault. Rx completes a
    /// <c>ToTask()</c> <c>TaskCompletionSource</c> from inside the pipeline WITHOUT
    /// <c>RunContinuationsAsynchronously</c>, so <c>DisposeAsync</c> resumed INLINE on the thread
    /// that signalled disposal — the mesh hub's own — and then ran the rest of teardown there,
    /// including <c>ioPools.DrainAll()</c>, a SYNCHRONOUS JOIN of every pooled I/O leaf. The
    /// second was that the <c>Catch</c> discarded a disposal fault outright, and the Task could
    /// observe nothing once it settled. The progress ticker is now an <c>Observable.Interval</c>
    /// that stops on the signal's own terminal, and the wait is one <c>Subscribe</c> whose task
    /// completes asynchronously, with an error arm.</para>
    /// </summary>
    private async Task WaitWithProgressAsync(string testName, Stopwatch sw, CancellationToken ct)
    {
        var disposal = Mesh.DisposalCompleted;

        // Progress ticker: stops on the signal's terminal, whatever it is. Materialize() so a
        // FAULTED disposal ends the ticker as data rather than pushing an error into a
        // subscription that has no arm for it.
        using var progress = Observable.Interval(DisposeProgressInterval)
            .TakeUntil(disposal.Materialize())
            .Subscribe(
                _ =>
                {
                    FileOutput.WriteLine(
                        $"[DISPOSE] {testName}: still waiting after {sw.ElapsedMilliseconds}ms — snapshot:");
                    FileOutput.WriteLine(SafeGetDiagnostics());
                },
                ex => FileOutput.WriteLine(
                    $"[DISPOSE] {testName}: progress ticker faulted: {ex.GetType().Name}: {ex.Message}"));

        try
        {
            // ConfigureAwait(false): belt and braces on top of ObserveCompletion's
            // RunContinuationsAsynchronously. `await` captures TaskScheduler.Current when there is
            // no SynchronizationContext, so a teardown entered from a hub scheduler would
            // otherwise route the rest of DisposeAsync — including the synchronous
            // ioPools.DrainAll() join — back onto it. (Copilot review, #2527.)
            await disposal.ObserveCompletion(
                ex => FileOutput.WriteLine(
                    $"[DISPOSE] {testName}: disposal faulted AFTER the wait settled — reported, "
                    + $"not orphaned: {ex.GetType().Name}: {ex.Message}"),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The hang path — unchanged. DisposeAsync turns this into a loud TimeoutException
            // carrying the hub diagnostics.
            throw;
        }
        catch (Exception ex)
        {
            // A disposal FAULT arriving as the answer. Teardown still waits for "done", not "why"
            // — but the fault is now REPORTED into the per-test output and the phase trace instead
            // of being discarded by a .Catch nobody could see. (Whether it should also FAIL the
            // class is an escalation decision of its own; see TeardownReport.DisposalFault.)
            FileOutput.WriteLine(
                $"[DISPOSE] {testName}: disposal FAULTED after {sw.ElapsedMilliseconds}ms: "
                + $"{ex.GetType().Name}: {ex.Message}");
            TestPhaseTrace(testName, "DISPOSE_FAULTED", sw.ElapsedMilliseconds,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private string SafeGetDiagnostics()
    {
        try { return Mesh.GetDisposalDiagnostics(); }
        catch (Exception diagEx) { return $"<failed to gather diagnostics: {diagEx.Message}>"; }
    }
}
