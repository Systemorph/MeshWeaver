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
}
