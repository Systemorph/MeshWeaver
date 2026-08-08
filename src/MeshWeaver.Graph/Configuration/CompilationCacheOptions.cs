namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Configuration options for the dynamic compilation cache.
/// </summary>
public class CompilationCacheOptions
{
    /// <summary>
    /// Cache directory path. Can be absolute or relative to solution/working directory.
    /// Default: ".mesh-cache"
    /// </summary>
    public string CacheDirectory { get; set; } = ".mesh-cache";

    /// <summary>
    /// Enable compilation caching. If false, recompiles on every request (but still caches in-memory).
    /// Default: true
    /// </summary>
    public bool EnableCompilationCache { get; set; } = true;

    /// <summary>
    /// Enable disk-based caching of compiled assemblies. If false, compiles to memory only
    /// (no files written to disk). Useful for tests to avoid file locking issues.
    /// Default: true
    /// </summary>
    public bool EnableDiskCache { get; set; } = true;

    /// <summary>
    /// Write .cs source files alongside DLLs for debugger source linking.
    /// When true, the debugger can step into dynamically compiled code.
    /// Only applies when EnableDiskCache is true.
    /// Default: true
    /// </summary>
    public bool EnableSourceDebugging { get; set; } = true;

    /// <summary>
    /// Maximum time to wait when acquiring a compilation lock.
    /// Used for multi-process synchronization when multiple processes
    /// try to compile the same node type simultaneously.
    /// Default: 2 minutes
    /// </summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Initial delay when retrying lock acquisition.
    /// Uses exponential backoff up to LockMaxRetryDelayMs.
    /// Default: 50ms
    /// </summary>
    public int LockRetryDelayMs { get; set; } = 50;

    /// <summary>
    /// Maximum delay between lock acquisition retries.
    /// Default: 2000ms
    /// </summary>
    public int LockMaxRetryDelayMs { get; set; } = 2000;

    /// <summary>
    /// 🚨 Bound on the compile pipeline's ONE-SHOT source-snapshot reads
    /// (<c>ResolveSources(...).Take(1)</c> over the shared
    /// <c>NodeSources.GetSources</c> synced query). In every healthy state the
    /// snapshot arrives instantly (the query is <c>Replay(1)</c>-cached) or after a
    /// single cold storage read — so this bound only trips when the query's Initial
    /// is genuinely lost (a synced-query subscription that raced a source-update
    /// burst and never received its Initial: memex-cloud 2026-07-20, Store/Plugin).
    /// Without the bound that lost Initial parked the compile FOREVER at
    /// <c>CompilationStatus=Compiling</c> with no error and no recovery path — the
    /// absorbing wedge (the compile watcher needs a Pending transition, the release
    /// watcher gates on a settled status, the recovery kickoff is activation-one-shot).
    /// On timeout the compile FAILS terminally (Status=Error with a loud message
    /// naming the dead source query) so the state machine settles and a fresh
    /// trigger / the Compile button can retry.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan SourceSnapshotTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 🚨 Bound on the compile pipeline's ROSLYN leg — everything inside
    /// <c>CompileCore</c>'s <c>CompileAsync</c> call: the NuGet restore for any
    /// <c>#r "nuget:…"</c> the sources declare (network IO), source-generator
    /// execution, and Roslyn's <c>Emit</c> plus the disk write. Sibling of
    /// <see cref="SourceSnapshotTimeout"/> and there for the same reason: this
    /// subscription is the ONLY component that can settle the
    /// <c>CompilationStatus = Compiling</c> the dispatcher just flipped, so a leg that
    /// never answers strands the NodeType at Compiling for the life of the activation —
    /// and single-flight then ABSORBS every fresh trigger against it (no competing run,
    /// but also no recovery). The realistic non-completion is the NuGet leaf: an
    /// unreachable/hanging feed has no timeout of its own.
    /// On expiry the leg is CANCELLED (its <c>CancellationToken</c> is tripped when the
    /// bound unsubscribes, so Roslyn/NuGet stop rather than leak a compile thread) and
    /// the compile fails TERMINALLY with an error naming the leg.
    /// Generously above any legitimate run — a cold NuGet restore plus a large Roslyn
    /// emit is seconds-to-a-minute, never five.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan RoslynCompileTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 🚨 Bound on the compile pipeline's ASSEMBLY-LOAD leg — loading the freshly emitted
    /// assembly into its <c>AssemblyLoadContext</c>, <c>GetTypes()</c>, the
    /// <c>MeshNodeProviderAttribute</c> reflection scan and the configuration
    /// instantiation it performs. That work runs USER code (an attribute's constructor, a
    /// type initializer), so "it always returns" is not a guarantee the framework can make.
    /// Unbounded, a single blocking static ctor pins the type at Compiling forever.
    /// On expiry the compile fails TERMINALLY with an error naming the leg.
    /// The leg is pure local CPU + a file read — sub-second in every healthy compile.
    /// Default: 2 minutes.
    /// </summary>
    public TimeSpan AssemblyLoadTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 🚨 Bound on the compile pipeline's ASSEMBLY-STORE UPLOAD leg
    /// (<c>IAssemblyStore.PutWithLocation</c> — blob/HTTP IO). Unlike the two legs above,
    /// expiry here is NOT terminal-Error: an upload failure has never failed a compile
    /// (the assembly is usable in the producing silo; only cross-silo activation needs the
    /// store), and a bound must not silently change that contract. The timeout falls into
    /// the leg's existing failure path — a warning on the compile's ActivityLog naming the
    /// leg, and the un-stamped result passed through — so the compile SETTLES (Ok) instead
    /// of hanging at Compiling on a wedged blob endpoint.
    /// Default: 2 minutes.
    /// </summary>
    public TimeSpan AssemblyStoreUploadTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
