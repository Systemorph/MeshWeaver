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
    /// Enable compilation caching to disk. If false, compiles in-memory only (no caching).
    /// Default: true
    /// </summary>
    public bool EnableCompilationCache { get; set; } = true;

    /// <summary>
    /// Write .cs source files alongside DLLs for debugger source linking.
    /// When true, the debugger can step into dynamically compiled code.
    /// Default: true
    /// </summary>
    public bool EnableSourceDebugging { get; set; } = true;
}
