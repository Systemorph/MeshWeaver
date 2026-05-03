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
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.Configuration;
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
        // In shared-mesh mode, ConfigureMesh runs only on the FIRST instance of
        // this test class — the SP is cached statically and re-used by every
        // subsequent [Fact]. Skip the BuildHub registration on later instances
        // so their per-instance Services don't try to re-register the singleton
        // (and the wasted ConfigureMesh + builder allocation is avoided).
        if (!ShareMeshAcrossTests || !_sharedProviders.ContainsKey(GetType()))
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
    /// rss=… rssAnon=… unmanaged=… shared=…</c>. Cannot use <see cref="ILogger"/>
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

    /// <summary>
    /// Static caches reflected at runtime so a growing one shows up in the trace
    /// without us having to recompile. Each entry is a tuple of (label, lambda
    /// returning current count). Lambdas use reflection to avoid creating a
    /// runtime dependency from the test base on every project that owns a
    /// suspect static. Add new suspects here when you find them — the cost is
    /// one reflection lookup per MEM line and a string append.
    /// </summary>
    private static readonly (string Label, Func<int?> Reader)[] KnownStaticCaches =
    {
        ("agentCache", () => GetStaticDictCount("MeshWeaver.AI.ThreadExecution, MeshWeaver.AI", "AgentCache")),
        ("execCancels", () => GetStaticDictCount("MeshWeaver.AI.ThreadExecution, MeshWeaver.AI", "ExecutionCancellations")),
        ("complCallbk", () => GetStaticDictCount("MeshWeaver.AI.ThreadExecution, MeshWeaver.AI", "CompletionCallbacks")),
        ("nodeTypeReg", () => GetStaticDictCount("MeshWeaver.Graph.Configuration.NodeTypeRegistry, MeshWeaver.Graph", "Nodes")),
        ("dynTypeCache", () => GetStaticDictCount("MeshWeaver.Blazor.DynamicTypeGenerator, MeshWeaver.Blazor", "TypeCache")),
        ("nonVirtThunks", () => GetStaticDictCount("MeshWeaver.BusinessRules.DefaultImplementationOfInterfacesExtensions, MeshWeaver.BusinessRules", "NonVirtualInvocationThunks")),
        ("storageSnaps", () => GetStaticDictCount("MeshWeaver.Hosting.Persistence.CachingStorageAdapter, MeshWeaver.Hosting", "SharedSnapshots")),
        ("aclAttrCache", () => GetStaticDictCount("MeshWeaver.Hosting.Security.AccessControlPipeline, MeshWeaver.Hosting", "AttributeCache")),
        ("apiTokenVal", () => GetStaticDictCount("MeshWeaver.Graph.Configuration.ApiTokenNodeType, MeshWeaver.Graph", "ValidationCache")),
        ("xunitOutHelpers", () => GetStaticDictCount("MeshWeaver.Fixture.XUnitFileOutputRegistry, MeshWeaver.Fixture", "_activeOutputHelpers")),
        ("testAccessNodes", () => GetStaticDictCount("MeshWeaver.Hosting.Monolith.TestBase.TestAccessNodeProvider, MeshWeaver.Hosting.Monolith.TestBase", "Nodes")),
    };

    /// <summary>
    /// Reads <c>Count</c> off a private static <c>ConcurrentDictionary</c> /
    /// <c>ConcurrentBag</c> / <c>Dictionary</c> field by reflection. Returns null
    /// if the type or field can't be resolved (project not loaded, field renamed,
    /// not a counted collection) — the trace just omits that label rather than
    /// throwing out of the test pipeline.
    /// </summary>
    private static int? GetStaticDictCount(string assemblyQualifiedTypeName, string fieldName)
    {
        try
        {
            var type = Type.GetType(assemblyQualifiedTypeName, throwOnError: false);
            if (type is null) return null;
            var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (field is null) return null;
            var value = field.GetValue(null);
            if (value is null) return null;
            // ConcurrentDictionary, Dictionary, ConcurrentBag, HashSet all expose Count.
            var countProp = value.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            if (countProp is null) return null;
            return (int?)countProp.GetValue(value);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// One-line snapshot of all known static-cache sizes. Format: <c>cache=N</c>
    /// for each, with unresolved entries omitted. A leak shows as monotonically
    /// growing values across INIT_MEM lines.
    /// </summary>
    private static string SnapshotKnownStaticCaches()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (label, reader) in KnownStaticCaches)
        {
            try
            {
                var count = reader();
                if (count is null) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(label).Append('=').Append(count);
            }
            catch { /* skip this label */ }
        }
        return sb.ToString();
    }

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
                    (string.IsNullOrEmpty(nativeBreakdown) ? "" : nativeBreakdown + " ") +
                    $"{SnapshotKnownStaticCaches()}";

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
                + $" gc0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} gc2={GC.CollectionCount(2)}"
                + $" {SnapshotKnownStaticCaches()}";
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

            // Snapshot for the per-instance DELTA line written in DisposeAsync.
            _instanceInitManagedBytes = GC.GetTotalMemory(forceFullCollection: false);
            try { _instanceInitRssBytes = Process.GetCurrentProcess().WorkingSet64; }
            catch { _instanceInitRssBytes = 0; }
            _instanceInitRssAnonBytes = ReadRssAnonBytes();
        }
        catch (Exception ex)
        {
            TestPhaseTrace(name, "INIT_ERROR", sw.ElapsedMilliseconds,
                $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Override <see cref="ServiceSetup.Initialize"/> so that test classes
    /// opting in via <see cref="ShareMeshAcrossTests"/> reuse a cached
    /// <see cref="IServiceProvider"/> across every <c>[Fact]</c>. The first
    /// instance of the class builds the SP normally and stores it in the
    /// static cache; every subsequent instance grabs that same SP and skips
    /// <see cref="ServiceSetup.BuildServiceProvider"/> entirely.
    ///
    /// <para><c>Buildup(this)</c> still runs per-instance because <c>[Inject]</c>
    /// fields/properties live on the test instance, not the SP.</para>
    /// </summary>
    protected override void Initialize()
    {
        if (ShareMeshAcrossTests)
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

        // Shared-mesh classes never dispose the Mesh per-test — that's the entire
        // point of opting in (avoid rebuilding the Autofac container's compiled
        // factories for every [Fact]). The mesh outlives the testhost and
        // process exit reclaims it. Per-test base teardown (FileOutput unregister
        // etc.) still runs.
        if (ShareMeshAcrossTests)
        {
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
