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
}
