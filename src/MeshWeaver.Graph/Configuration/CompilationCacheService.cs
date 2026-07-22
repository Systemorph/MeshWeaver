using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Internal interface for managing the dynamic compilation cache.
/// </summary>
internal interface ICompilationCacheService
{
    /// <summary>
    /// Gets the absolute path to the cache directory.
    /// </summary>
    string CacheDirectory { get; }

    /// <summary>
    /// Checks if the cached assembly is valid (exists and is newer than lastModified).
    /// Also checks that the cached DLL is newer than the MeshWeaver.Graph.dll framework DLL.
    /// </summary>
    /// <param name="nodeName">Sanitized node name used as the assembly base name.</param>
    /// <param name="lastModified">The last modified timestamp of the source MeshNode.</param>
    /// <returns>True if cache is valid and can be used, false if recompilation is needed.</returns>
    bool IsCacheValid(string nodeName, DateTimeOffset lastModified);

    /// <summary>
    /// Returns the path of the newest cached DLL for <paramref name="nodeName"/>
    /// whose write-time satisfies both the source-modified deadline and the
    /// framework-DLL deadline. Returns null when no valid cache entry exists.
    /// Use when you need the actual DLL path (not just whether the cache is
    /// valid) — e.g., to feed an ALC LoadContext without re-running Roslyn.
    /// </summary>
    string? TryGetLatestCachedDllPath(string nodeName, DateTimeOffset lastModified);

    /// <summary>
    /// Gets the modification timestamp of the MeshWeaver.Graph.dll framework assembly.
    /// Used for cache invalidation when the framework is updated.
    /// </summary>
    DateTimeOffset GetFrameworkTimestamp();

    /// <summary>
    /// Gets the path to the DLL file for a node.
    /// </summary>
    string GetDllPath(string nodeName);

    /// <summary>
    /// Gets the path to the PDB file for a node.
    /// </summary>
    string GetPdbPath(string nodeName);

    /// <summary>
    /// Gets the path to the source file for a node.
    /// </summary>
    string GetSourcePath(string nodeName);

    /// <summary>
    /// Gets the path to the XML documentation file for a node.
    /// </summary>
    string GetXmlDocPath(string nodeName);

    /// <summary>
    /// Invalidates (deletes) the cache for a node.
    /// Unloads the associated AssemblyLoadContext if one exists.
    /// </summary>
    void InvalidateCache(string nodeName);

    /// <summary>
    /// Clears the sticky-invalidation flag for a node after a fresh compile has
    /// successfully written new cache artifacts. Without this call, a recompile
    /// triggered by <see cref="InvalidateCache"/> would force the next
    /// <see cref="IsCacheValid"/> to return false even though the just-written
    /// DLL is already fresh — causing an unnecessary second compile.
    /// </summary>
    void MarkCacheFresh(string nodeName);

    /// <summary>
    /// Gets all cached assembly paths (DLLs) in the cache directory.
    /// </summary>
    IEnumerable<string> GetAllCachedAssemblyPaths();

    /// <summary>
    /// Ensures the cache directory exists.
    /// </summary>
    void EnsureCacheDirectoryExists();

    /// <summary>
    /// Sanitizes a node path to a valid file name.
    /// </summary>
    string SanitizeNodeName(string nodePath);

    /// <summary>
    /// Gets or creates an AssemblyLoadContext for a node.
    /// The context is collectible and can be unloaded when the node is regenerated.
    /// </summary>
    /// <param name="nodeName">Sanitized node name.</param>
    /// <returns>A NodeAssemblyLoadContext that can load and unload the node's assembly.</returns>
    NodeAssemblyLoadContext GetOrCreateLoadContext(string nodeName);

    /// <summary>
    /// Loads an assembly into the node's isolated load context from disk.
    /// </summary>
    /// <param name="nodeName">Sanitized node name.</param>
    /// <returns>The loaded assembly, or null if the DLL doesn't exist.</returns>
    Assembly? LoadAssembly(string nodeName);

    /// <summary>
    /// Loads an assembly from byte arrays into the node's isolated load context.
    /// Used for in-memory compilation when disk caching is disabled.
    /// </summary>
    /// <param name="nodeName">Sanitized node name.</param>
    /// <param name="assemblyBytes">The compiled assembly bytes.</param>
    /// <param name="pdbBytes">The PDB bytes for debugging (optional).</param>
    /// <returns>The loaded assembly.</returns>
    Assembly LoadAssemblyFromBytes(string nodeName, byte[] assemblyBytes, byte[]? pdbBytes);

    /// <summary>
    /// Gets whether disk caching is enabled.
    /// </summary>
    bool IsDiskCacheEnabled { get; }

    /// <summary>
    /// Unloads the AssemblyLoadContext for a node, allowing the DLL to be regenerated.
    /// </summary>
    /// <param name="nodeName">Sanitized node name.</param>
    void UnloadContext(string nodeName);

    /// <summary>
    /// 🚨 Memory reclaim on node-hub disposal. Unloads EVERY collectible
    /// <c>NodeAssemblyLoadContext</c> that loaded code for this NodeType — keyed by
    /// sanitized node name, by full DLL path, or by release path — so the compiled
    /// assembly (and its native footprint) is GC-collectable the moment the owning
    /// node hub goes away. Unlike <see cref="InvalidateCache"/> this does NOT set the
    /// sticky "invalidated" flag and does NOT touch on-disk DLLs: the durable release
    /// artifacts stay on the shared cache mount, so a later reactivation reloads them
    /// without recompiling. This is the per-node-lifetime ALC ownership the top-level
    /// singleton otherwise can't provide (its root container is never disposed — see
    /// CompilationCacheService registration + MeshDataSource.SubscribeToOwnDeletion).
    /// </summary>
    /// <param name="nodeName">Sanitized node name (e.g. <c>SanitizeNodeName(node.Path)</c>).</param>
    void UnloadNodeContexts(string nodeName);

    /// <summary>
    /// Gets the release folder path for a node and release key.
    /// Release folders are immutable once created.
    /// </summary>
    /// <param name="nodeName">Sanitized node name.</param>
    /// <param name="releaseKey">The computed release key (hash of compilation inputs).</param>
    /// <returns>Absolute path to the release folder.</returns>
    string GetReleaseFolderPath(string nodeName, string releaseKey);

    /// <summary>
    /// Checks if a release folder contains a valid compiled assembly.
    /// </summary>
    /// <param name="releaseFolder">Absolute path to the release folder.</param>
    /// <returns>True if the release folder exists and contains a DLL.</returns>
    bool IsReleaseValid(string releaseFolder);

    /// <summary>
    /// Gets the directory path for compilation locks.
    /// </summary>
    string GetLockDirectory();

    /// <summary>
    /// Gets or creates an AssemblyLoadContext for a release folder.
    /// </summary>
    /// <param name="release">The NodeTypeRelease containing path information.</param>
    /// <param name="releaseFolder">Absolute path to the release folder.</param>
    /// <returns>A NodeAssemblyLoadContext that can load the release's assembly.</returns>
    NodeAssemblyLoadContext GetOrCreateLoadContextForRelease(NodeTypeRelease release, string releaseFolder);

    /// <summary>
    /// Loads an assembly from a release folder.
    /// </summary>
    /// <param name="release">The NodeTypeRelease containing path information.</param>
    /// <param name="releaseFolder">Absolute path to the release folder.</param>
    /// <returns>The loaded assembly, or null if the DLL doesn't exist.</returns>
    System.Reflection.Assembly? LoadAssemblyFromRelease(NodeTypeRelease release, string releaseFolder);

    /// <summary>
    /// Gets the release folder path for a NodeTypeRelease.
    /// </summary>
    /// <param name="release">The NodeTypeRelease.</param>
    /// <returns>Absolute path to the release folder.</returns>
    string GetReleaseFolderPath(NodeTypeRelease release);

    /// <summary>
    /// Gets or creates an AssemblyLoadContext keyed by the exact DLL path.
    /// Used for release-per-compile: each unique compiled DLL lives in its own
    /// ALC so V1 and V2 assemblies coexist without overwriting each other.
    /// </summary>
    NodeAssemblyLoadContext GetOrCreateLoadContextForPath(string nodeName, string dllPath);

    /// <summary>
    /// Registers NuGet probing directories for a node. The node's AssemblyLoadContext
    /// consults these directories during Resolving events so transitive dependencies
    /// of resolved NuGet packages can be loaded at runtime.
    /// </summary>
    void RegisterProbingDirectories(string nodeName, System.Collections.Generic.IReadOnlyList<string> directories);
}

/// <summary>
/// Collectible AssemblyLoadContext for dynamically compiled node assemblies.
/// This context can be unloaded to allow recompilation of the assembly.
/// Supports both file-based and in-memory assembly loading.
/// </summary>
internal sealed class NodeAssemblyLoadContext : AssemblyLoadContext, IDisposable
{
    private readonly string _nodeName;
    private readonly ILogger? _logger;
    private readonly string? _dllPath;
    private readonly object _loadLock = new();
    // 🚨 Weak, never a strong Assembly field: a collectible context that strongly references
    // its own assembly forms a cycle THROUGH the runtime's LoaderAllocator GC handle
    // (LoaderAllocator → handle → context → Assembly → LoaderAllocator) that the GC can never
    // break — a context dropped WITHOUT Dispose would be immortal garbage. Weak is lossless
    // here: a live context roots its own assemblies natively, so the target only dies when the
    // whole context does.
    private WeakReference<Assembly>? _loadedAssembly;
    private volatile bool _disposed;
    private ImmutableArray<string> _probingDirs = ImmutableArray<string>.Empty;

    // Opt-in diagnostic (env MESHWEAVER_ALC_UNLOAD_GC_PROBE=1) — OFF by default, so zero cost in
    // prod and normal CI. When on, Dispose drives a synchronous full GC right after Unload so a
    // use-after-unload dangling NATIVE pointer into this now-collectible assembly faults HERE
    // (pinning the culprit node in the log + a corruption-time dump) instead of as a delayed
    // background-GC SIGSEGV. Immutable config constant read once at type init — never written.
    private static readonly bool UnloadGcProbe =
        Environment.GetEnvironmentVariable("MESHWEAVER_ALC_UNLOAD_GC_PROBE") == "1";

    public void SetProbingDirectories(System.Collections.Generic.IReadOnlyList<string> dirs)
    {
        _probingDirs = dirs.ToImmutableArray();
    }

    /// <summary>
    /// Gets the node name associated with this context.
    /// </summary>
    public string NodeName => _nodeName;

    /// <summary>
    /// Gets the loaded assembly, or null if not yet loaded.
    /// </summary>
    public Assembly? LoadedAssembly =>
        _loadedAssembly is { } weak && weak.TryGetTarget(out var assembly) ? assembly : null;

    /// <summary>
    /// Gets whether this context has been disposed/unloaded.
    /// </summary>
    public bool IsDisposed => _disposed;

    public NodeAssemblyLoadContext(string nodeName, string? dllPath, ILogger? logger = null)
        : base(name: $"DynamicNode_{nodeName}", isCollectible: true)
    {
        _nodeName = nodeName;
        _dllPath = dllPath;
        _logger = logger;

        // Purge Autofac's process-static reflection cache of this context's assemblies the instant
        // it starts unloading — while the metadata the predicate walks is still valid. A cached
        // ConstructorInfo/Assembly key otherwise (1) roots this collectible context so it can never
        // be collected, and (2) makes a later, unrelated concurrent GetOrAdd bucket-probe compare
        // against the freed key → AccessViolationException/SIGSEGV under concurrent hub construction.
        // Autofac does this automatically for BeginLoadContextLifetimeScope; we manage the context
        // by hand, so we mirror it. Static handler ⇒ no self-reference that would defeat collection.
        Unloading += ReflectionCacheEviction.EvictFor;
    }

    /// <summary>
    /// Loads the node's assembly from disk.
    /// </summary>
    public Assembly? LoadNodeAssembly()
    {
        if (_disposed)
            throw new ObjectDisposedException(Name, "Cannot load assembly from disposed context");

        // Fast path: already loaded
        if (LoadedAssembly is { } fast)
            return fast;

        lock (_loadLock)
        {
            // Double-check after acquiring lock
            if (_disposed)
                throw new ObjectDisposedException(Name, "Cannot load assembly from disposed context");

            if (LoadedAssembly is { } loaded)
                return loaded;

            if (string.IsNullOrEmpty(_dllPath) || !File.Exists(_dllPath))
            {
                _logger?.LogDebug("DLL not found at {DllPath}", _dllPath);
                return null;
            }

            // Check if cached DLL is older than the framework DLL (code generator)
            var dllLastWrite = File.GetLastWriteTimeUtc(_dllPath);
            var frameworkLocation = typeof(CompilationCacheService).Assembly.Location;
            if (!string.IsNullOrEmpty(frameworkLocation) && File.Exists(frameworkLocation))
            {
                var frameworkLastWrite = File.GetLastWriteTimeUtc(frameworkLocation);
                if (dllLastWrite < frameworkLastWrite)
                {
                    _logger?.LogInformation("Cached assembly at {DllPath} is older than framework, deleting for regeneration", _dllPath);
                    try
                    {
                        File.Delete(_dllPath);
                        var pdbPath = Path.ChangeExtension(_dllPath, ".pdb");
                        if (File.Exists(pdbPath))
                            File.Delete(pdbPath);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to delete stale assembly at {DllPath}", _dllPath);
                    }
                    return null;
                }
            }

            // Load the assembly into this isolated context
            try
            {
                var assembly = LoadFromAssemblyPath(_dllPath);
                _loadedAssembly = new WeakReference<Assembly>(assembly);
                _logger?.LogDebug("Loaded assembly {AssemblyName} into context {ContextName}",
                    assembly.GetName().Name, Name);

                return assembly;
            }
            catch (BadImageFormatException ex)
            {
                _logger?.LogWarning(ex, "Corrupted assembly at {DllPath}, deleting for regeneration", _dllPath);
                try
                {
                    File.Delete(_dllPath);
                    // Also delete PDB if it exists
                    var pdbPath = Path.ChangeExtension(_dllPath, ".pdb");
                    if (File.Exists(pdbPath))
                        File.Delete(pdbPath);
                }
                catch (Exception deleteEx)
                {
                    _logger?.LogWarning(deleteEx, "Failed to delete corrupted assembly at {DllPath}", _dllPath);
                }
                return null;
            }
        }
    }

    /// <summary>
    /// Loads an assembly from byte arrays (for in-memory compilation).
    /// </summary>  
    public Assembly LoadFromBytes(byte[] assemblyBytes, byte[]? pdbBytes)
    {
        if (_disposed)
            throw new ObjectDisposedException(Name, "Cannot load assembly from disposed context");

        // Fast path: already loaded
        if (LoadedAssembly is { } fast)
            return fast;

        lock (_loadLock)
        {
            // Double-check after acquiring lock
            if (_disposed)
                throw new ObjectDisposedException(Name, "Cannot load assembly from disposed context");

            if (LoadedAssembly is { } loaded)
                return loaded;

            using var assemblyStream = new MemoryStream(assemblyBytes);
            using var pdbStream = pdbBytes != null ? new MemoryStream(pdbBytes) : null;

            var assembly = LoadFromStream(assemblyStream, pdbStream);
            _loadedAssembly = new WeakReference<Assembly>(assembly);
            _logger?.LogDebug("Loaded assembly {AssemblyName} from bytes into context {ContextName}",
                assembly.GetName().Name, Name);

            return assembly;
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Probe registered NuGet package directories for transitive dependencies.
        var name = assemblyName.Name;
        if (!string.IsNullOrEmpty(name) && !_probingDirs.IsDefaultOrEmpty)
        {
            foreach (var dir in _probingDirs)
            {
                var candidate = Path.Combine(dir, name + ".dll");
                if (File.Exists(candidate))
                {
                    try
                    {
                        return LoadFromAssemblyPath(candidate);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Probing load failed for {Candidate}", candidate);
                    }
                }
            }
        }
        // For other dependencies, delegate to the default context
        return null;
    }

    public void Dispose()
    {
        lock (_loadLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _loadedAssembly = null;

            _logger?.LogDebug("Unloading AssemblyLoadContext {ContextName}", Name);
        }

        // Initiate unload outside the lock - the context will be collected when all references are released
        Unload();

        // Diagnostic probe (opt-in, off by default): drive the collection synchronously so a
        // use-after-unload dangling native pointer trips at THIS unload — naming the culprit node
        // and yielding a corruption-time dump — instead of a delayed background-GC SIGSEGV. Used
        // by the alc-unload-probe workflow to pin the reflection/serialization cache that retains
        // an accessor into a collectible node assembly (the exit=139 teardown crash).
        if (UnloadGcProbe)
        {
            // Write to Console DIRECTLY, not via _logger: this runs INSIDE ServiceProvider.Dispose()
            // where the ILogger (XUnitFileLogger.GetMinLogLevel) resolves from the already-disposed
            // container and throws ObjectDisposedException BEFORE writing — so the probe never named
            // the culprit. Console is the only sink alive during teardown.
            Console.Error.WriteLine($"ALC_UNLOAD_PROBE forcing GC after unloading {Name}");
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}

/// <summary>
/// Service for managing the dynamic compilation cache.
/// Handles cache validation, path generation, cache invalidation,
/// and AssemblyLoadContext management for dynamic assembly loading/unloading.
/// Supports both disk-based and in-memory caching.
/// </summary>
internal class CompilationCacheService(
    IOptions<CompilationCacheOptions> options,
    ILogger<CompilationCacheService> logger)
    : ICompilationCacheService, IDisposable
{
    private readonly CompilationCacheOptions _options = options.Value ?? new CompilationCacheOptions();
    private readonly ConcurrentDictionary<string, NodeAssemblyLoadContext> _loadContexts = new();
    private readonly ConcurrentDictionary<string, System.Collections.Immutable.ImmutableArray<string>> _probingDirs = new();
    // Sticky invalidation set: a node name in here forces IsCacheValid to return false
    // on the NEXT call (and then clears itself). Needed because InvalidateCache's
    // File.Delete can silently fail when the DLL is still mapped by a live ALC — in
    // that case the stale DLL sits on disk with a newer timestamp than the NodeType's
    // LastModified, so the ordinary timestamp-based IsCacheValid would reuse it.
    private readonly ConcurrentDictionary<string, byte> _invalidated = new(StringComparer.Ordinal);
    private readonly string _absoluteCacheDirectory = ResolveAbsolutePath(options.Value?.CacheDirectory ?? ".mesh-cache");
    private readonly Lazy<DateTimeOffset> _frameworkTimestamp = new(ComputeFrameworkTimestamp);
    private readonly object _disposeLock = new();
    private volatile bool _disposed;

    /// <inheritdoc />
    public void RegisterProbingDirectories(string nodeName, System.Collections.Generic.IReadOnlyList<string> directories)
    {
        if (directories.Count == 0) return;
        _probingDirs[nodeName] = directories.ToImmutableArray();
        if (_loadContexts.TryGetValue(nodeName, out var ctx))
            ctx.SetProbingDirectories(directories);
    }

    /// <inheritdoc />
    public bool IsDiskCacheEnabled => _options.EnableDiskCache;

    private static string ResolveAbsolutePath(string cacheDirectory)
    {
        if (Path.IsPathRooted(cacheDirectory))
            return cacheDirectory;

        // Resolve relative to the executing assembly location (bin\Debug\...)
        var assemblyLocation = typeof(CompilationCacheService).Assembly.Location;
        var assemblyDirectory = string.IsNullOrEmpty(assemblyLocation)
            ? Directory.GetCurrentDirectory()
            : Path.GetDirectoryName(assemblyLocation) ?? Directory.GetCurrentDirectory();

        var resolved = Path.GetFullPath(Path.Combine(assemblyDirectory, cacheDirectory));

        // Container deployments (Docker/Aspire) ship a read-only /app/ — writing the cache
        // there fails with UnauthorizedAccessException on the first compile and breaks every
        // dynamic NodeType. Probe the assembly directory; if it's not writable, fall back to
        // a tmp-rooted path so dynamic compilation still works.
        if (!IsDirectoryWritable(assemblyDirectory))
            resolved = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MeshWeaver", cacheDirectory));

        return resolved;
    }

    private static bool IsDirectoryWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".mesh-cache-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DateTimeOffset ComputeFrameworkTimestamp()
    {
        // This value's ONLY consumer is the wall-clock freshness comparison in
        // TryGetLatestCachedDllPath ("a cached node DLL written BEFORE the framework was built is
        // ABI-stale → recompile"), so it MUST be a real framework-production timestamp — the
        // MeshWeaver.Graph DLL's file last-write time. It must NOT be derived from the assembly MVID:
        // projecting the 128-bit MVID into ticks yields a uniformly-random date (frequently in the
        // FUTURE), which makes the `dllLastWrite < frameworkTime` check reject every fresh DLL →
        // permanent cache miss / recompile storm (the regression that broke
        // CompilationCacheServiceTest 2026-06-20). It also mirrors the parallel file-time check in
        // NodeAssemblyLoadContext.LoadNodeAssembly, which was never MVID-keyed.
        //
        // 🚨 The framework's per-image CONTENT identity (for cross-image / cross-silo cache keying —
        // the atioz BadImageFormatException-on-deploy fix) is the Graph assembly MVID, but it is
        // carried as a STRING by NodeTypeCompilationHelpers.FrameworkVersion → stamped into
        // CompiledFrameworkVersion (HasUsableBuild equality) and baked into the FileSystemAssemblyStore
        // filename/glob. Those are the mechanisms that defeat reproducible-build timestamp
        // normalization — NOT this wall-clock value. Do not fold the MVID in here.
        var assemblyLocation = typeof(CompilationCacheService).Assembly.Location;
        if (string.IsNullOrEmpty(assemblyLocation) || !File.Exists(assemblyLocation))
        {
            // Running in-memory (e.g., single-file deployment) - use current time
            return DateTimeOffset.UtcNow;
        }

        var lastWrite = File.GetLastWriteTimeUtc(assemblyLocation);
        return new DateTimeOffset(lastWrite, TimeSpan.Zero);
    }

    /// <inheritdoc />
    public string CacheDirectory => _absoluteCacheDirectory;

    /// <inheritdoc />
    public DateTimeOffset GetFrameworkTimestamp() => _frameworkTimestamp.Value;

    /// <inheritdoc />
    public bool IsCacheValid(string nodeName, DateTimeOffset lastModified)
        => TryGetLatestCachedDllPath(nodeName, lastModified) is not null;

    /// <summary>
    /// Returns the path of the newest cached DLL for <paramref name="nodeName"/>
    /// whose write-time satisfies both the source-modified deadline and the
    /// framework-DLL deadline. Returns null when no valid cache entry exists.
    ///
    /// <para>Layout: <c>CompileToDiskAsync</c> writes to
    /// <c>{cacheDir}/{nodeName}_{ticks_hex}/{nodeName}.dll</c> so V1 and V2
    /// historical compiles coexist (ALC-safety: each subdir is a unique load
    /// context). The newest subdir's DLL is the live one; older subdirs are
    /// holdover release artifacts. This method enumerates the subdirs, picks
    /// the newest by write-time, and validates against the source/framework
    /// timestamps — same logic the old flat layout used, lifted to the
    /// timestamped-subdir reality.</para>
    ///
    /// <para>Without this method the flat lookup
    /// (<c>{cacheDir}/{nodeName}.dll</c>) always misses, every test recompiles
    /// from cold (9-15 s for non-trivial NodeTypes), and tests with a 10 s
    /// timeout that touch dynamic NodeTypes time out reliably.</para>
    /// </summary>
    public string? TryGetLatestCachedDllPath(string nodeName, DateTimeOffset lastModified)
    {
        if (!_options.EnableCompilationCache)
            return null;

        // Sticky invalidation — honored once and then cleared, so the next compile
        // can write fresh artifacts and subsequent calls go back to the
        // timestamp check.
        if (_invalidated.TryRemove(nodeName, out _))
        {
            logger.LogDebug("Cache forced-stale for {NodeName} by prior InvalidateCache", nodeName);
            return null;
        }

        // Find the newest {nodeName}_*/{nodeName}.dll subdir. Direct
        // EnumerateDirectories with a glob keeps the work O(historical-compiles)
        // — typically 1-3 entries even on the hottest NodeTypes.
        if (!Directory.Exists(_absoluteCacheDirectory))
            return null;

        DirectoryInfo? newest = null;
        foreach (var dir in new DirectoryInfo(_absoluteCacheDirectory)
                     .EnumerateDirectories($"{nodeName}_*"))
        {
            var candidateDll = Path.Combine(dir.FullName, $"{nodeName}.dll");
            if (!File.Exists(candidateDll))
                continue;
            if (newest is null || dir.LastWriteTimeUtc > newest.LastWriteTimeUtc)
                newest = dir;
        }
        if (newest is null)
        {
            logger.LogDebug("Cache miss for {NodeName}: no subdir matching {NodeName}_* contains a DLL", nodeName, nodeName);
            return null;
        }

        var dllPath = Path.Combine(newest.FullName, $"{nodeName}.dll");
        var pdbPath = Path.Combine(newest.FullName, $"{nodeName}.pdb");
        var sourcePath = GetSourcePath(nodeName);

        if (!File.Exists(pdbPath))
        {
            logger.LogDebug("Cache miss for {NodeName}: PDB not found at {PdbPath}", nodeName, pdbPath);
            return null;
        }

        if (_options.EnableSourceDebugging && !File.Exists(sourcePath))
        {
            logger.LogDebug("Cache miss for {NodeName}: Source not found at {SourcePath}", nodeName, sourcePath);
            return null;
        }

        // Check if DLL is newer than the node's LastModified
        var dllLastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(dllPath), TimeSpan.Zero);

        // Check 1: DLL must be newer than the partition's newest modification
        if (dllLastWrite < lastModified)
        {
            logger.LogDebug(
                "Cache stale for {NodeName}: DLL modified at {DllTime}, partition modified at {PartitionTime}",
                nodeName, dllLastWrite, lastModified);
            return null;
        }

        // Check 2: DLL must be newer than the framework DLL (MeshWeaver.Graph.dll)
        var frameworkTime = GetFrameworkTimestamp();
        if (dllLastWrite < frameworkTime)
        {
            logger.LogDebug(
                "Cache stale for {NodeName}: DLL modified at {DllTime}, framework modified at {FrameworkTime}",
                nodeName, dllLastWrite, frameworkTime);
            return null;
        }

        logger.LogDebug("Cache hit for {NodeName} at {DllPath}", nodeName, dllPath);
        return dllPath;
    }

    /// <inheritdoc />
    public string GetDllPath(string nodeName)
        => Path.Combine(_absoluteCacheDirectory, $"{nodeName}.dll");

    /// <inheritdoc />
    public string GetPdbPath(string nodeName)
        => Path.Combine(_absoluteCacheDirectory, $"{nodeName}.pdb");

    /// <inheritdoc />
    public string GetSourcePath(string nodeName)
        => Path.Combine(_absoluteCacheDirectory, $"{nodeName}.cs");

    /// <inheritdoc />
    /// <remarks>
    /// The XML file name uses the "DynamicNode_" prefix to match the assembly name,
    /// which is required for Namotion.Reflection to find the documentation at runtime.
    /// </remarks>
    public string GetXmlDocPath(string nodeName)
        => Path.Combine(_absoluteCacheDirectory, $"DynamicNode_{nodeName}.xml");

    /// <inheritdoc />
    public void InvalidateCache(string nodeName)
    {
        logger.LogDebug("Invalidating cache for {NodeName}", nodeName);

        // Mark invalidated so the next IsCacheValid returns false. Combined
        // with releases-as-MeshNodes, "invalidated" really just means "the
        // currently-loaded ALC is no longer authoritative for this NodeType
        // — the next request must consult the latest Release". We don't need
        // to delete anything on disk; release DLLs are content-keyed and
        // accumulate. See Doc/Architecture/Postmortems/NodeTypeReleaseRedesign.md.
        _invalidated[nodeName] = 0;

        // Unload every ALC that was loading code for THIS NodeType, regardless
        // of how it was keyed. Release-based loads register under
        // release.Path (e.g. "Type/Foo@hash"); legacy loads register under the
        // sanitized nodeName itself. Match on NodeAssemblyLoadContext.NodeName
        // so both flavours flush.
        var keysToUnload = _loadContexts
            .Where(kvp => string.Equals(kvp.Key, nodeName, StringComparison.Ordinal)
                       || string.Equals(kvp.Value.NodeName, nodeName, StringComparison.Ordinal))
            .Select(kvp => kvp.Key)
            .ToList();

        if (keysToUnload.Count > 0)
        {
            foreach (var key in keysToUnload)
                UnloadContext(key);

            // Per-NodeType ALC unload only — no GC.Collect. Release DLLs
            // stay on disk, so we never need the file lock to drop. Other
            // NodeTypes' ALCs continue running undisturbed.
        }

        // NB: we deliberately do NOT delete DLLs / PDBs / source caches.
        //   - Release-based caching: each release lives in its own
        //     {cacheDir}/{nodeName}_{releaseHash}/ folder. Deleting old
        //     releases is a separate, opt-in concern (TTL / "keep last N"),
        //     not part of invalidation.
        //   - Legacy single-DLL caching: File.Delete races the still-mapped
        //     ALC on Windows and produces UnauthorizedAccessException
        //     (CodeEditRecompileTest's failure mode). Better to leave the
        //     stale DLL on disk and let the next compile overwrite it after
        //     the ALC has fully unloaded.
    }

    /// <inheritdoc />
    public void MarkCacheFresh(string nodeName) => _invalidated.TryRemove(nodeName, out _);

    /// <inheritdoc />
    public IEnumerable<string> GetAllCachedAssemblyPaths()
    {
        if (!Directory.Exists(_absoluteCacheDirectory))
            return [];

        return Directory.GetFiles(_absoluteCacheDirectory, "*.dll");
    }

    /// <inheritdoc />
    public void EnsureCacheDirectoryExists()
    {
        if (!Directory.Exists(_absoluteCacheDirectory))
        {
            Directory.CreateDirectory(_absoluteCacheDirectory);
            logger.LogInformation("Created compilation cache directory: {CacheDirectory}", _absoluteCacheDirectory);
        }
    }

    /// <inheritdoc />
    public string SanitizeNodeName(string nodePath)
    {
        // Replace path separators and invalid characters with underscores
        var sanitized = nodePath
            .Replace('/', '_')
            .Replace('\\', '_')
            .Replace(':', '_')
            .Replace('*', '_')
            .Replace('?', '_')
            .Replace('"', '_')
            .Replace('<', '_')
            .Replace('>', '_')
            .Replace('|', '_')
            .Replace(' ', '_');

        // Remove leading/trailing underscores and collapse multiple underscores
        while (sanitized.Contains("__"))
            sanitized = sanitized.Replace("__", "_");

        sanitized = sanitized.Trim('_');

        // Ensure it starts with a letter (for valid assembly names)
        if (sanitized.Length > 0 && !char.IsLetter(sanitized[0]))
            sanitized = "Node_" + sanitized;

        return sanitized;
    }

    /// <inheritdoc />
    public NodeAssemblyLoadContext GetOrCreateLoadContext(string nodeName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _loadContexts.GetOrAdd(nodeName, name =>
        {
            var dllPath = GetDllPath(name);
            logger.LogDebug("Creating new AssemblyLoadContext for {NodeName}", name);
            var ctx = new NodeAssemblyLoadContext(name, dllPath, logger);
            if (_probingDirs.TryGetValue(name, out var dirs) && !dirs.IsDefaultOrEmpty)
                ctx.SetProbingDirectories(dirs);
            return ctx;
        });
    }

    /// <inheritdoc />
    public Assembly? LoadAssembly(string nodeName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var context = GetOrCreateLoadContext(nodeName);
        return context.LoadNodeAssembly();
    }

    /// <inheritdoc />
    public NodeAssemblyLoadContext GetOrCreateLoadContextForPath(string nodeName, string dllPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _loadContexts.GetOrAdd(dllPath, path =>
        {
            logger.LogDebug("Creating new path-keyed AssemblyLoadContext for {NodeName} at {DllPath}", nodeName, path);
            var ctx = new NodeAssemblyLoadContext(nodeName, path, logger);
            if (_probingDirs.TryGetValue(nodeName, out var dirs) && !dirs.IsDefaultOrEmpty)
                ctx.SetProbingDirectories(dirs);
            return ctx;
        });
    }

    /// <inheritdoc />
    public Assembly LoadAssemblyFromBytes(string nodeName, byte[] assemblyBytes, byte[]? pdbBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // For in-memory loading, create context without dll path
        var context = _loadContexts.GetOrAdd(nodeName, name =>
        {
            logger.LogDebug("Creating new in-memory AssemblyLoadContext for {NodeName}", name);
            return new NodeAssemblyLoadContext(name, null, logger);
        });

        return context.LoadFromBytes(assemblyBytes, pdbBytes);
    }

    /// <inheritdoc />
    public void UnloadContext(string nodeName)
    {
        if (_loadContexts.TryRemove(nodeName, out var context))
        {
            logger.LogDebug("Unloading AssemblyLoadContext for {NodeName}", nodeName);
            context.Dispose();
        }
    }

    /// <inheritdoc />
    public void UnloadNodeContexts(string nodeName)
    {
        if (_disposed)
            return;

        // Match the same set InvalidateCache flushes — but WITHOUT the sticky
        // _invalidated flag (no forced recompile on reactivation) and WITHOUT
        // deleting disk artifacts. Release- and path-keyed contexts register their
        // owning NodeType under NodeAssemblyLoadContext.NodeName; the dictionary key
        // may instead be the sanitized name, a full DLL path, or a release path, so
        // match on either.
        var keysToUnload = _loadContexts
            .Where(kvp => string.Equals(kvp.Key, nodeName, StringComparison.Ordinal)
                       || string.Equals(kvp.Value.NodeName, nodeName, StringComparison.Ordinal))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToUnload)
            UnloadContext(key);

        if (keysToUnload.Count > 0)
            logger.LogDebug("Unloaded {Count} AssemblyLoadContext(s) for disposed node {NodeName}",
                keysToUnload.Count, nodeName);
    }

    /// <inheritdoc />
    public string GetReleaseFolderPath(string nodeName, string releaseKey)
        => Path.Combine(_absoluteCacheDirectory, $"{nodeName}_{releaseKey}");

    /// <inheritdoc />
    public bool IsReleaseValid(string releaseFolder)
    {
        if (!Directory.Exists(releaseFolder))
            return false;

        // Check that at least one DLL exists in the folder
        return Directory.GetFiles(releaseFolder, "*.dll").Length > 0;
    }

    /// <inheritdoc />
    public string GetLockDirectory()
        => Path.Combine(_absoluteCacheDirectory, ".locks");

    /// <inheritdoc />
    public NodeAssemblyLoadContext GetOrCreateLoadContextForRelease(NodeTypeRelease release, string releaseFolder)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Use release path as context key (already unique due to release hash)
        var contextKey = release.Path;
        var sanitizedPath = release.GetSanitizedPath();

        return _loadContexts.GetOrAdd(contextKey, _ =>
        {
            var dllPath = Path.Combine(releaseFolder, $"{sanitizedPath}.dll");
            logger.LogDebug("Creating new AssemblyLoadContext for {ReleasePath} from release {ReleaseFolder}", release.Path, releaseFolder);
            var ctx = new NodeAssemblyLoadContext(sanitizedPath, dllPath, logger);

            // Restore persisted NuGet probing directories so transitive deps resolve.
            var probingPath = Path.Combine(releaseFolder, "probing.json");
            if (File.Exists(probingPath))
            {
                try
                {
                    var json = File.ReadAllText(probingPath);
                    var dirs = System.Text.Json.JsonSerializer.Deserialize<string[]>(json);
                    if (dirs is { Length: > 0 })
                        ctx.SetProbingDirectories(dirs);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to read probing directories from {ProbingPath}", probingPath);
                }
            }

            return ctx;
        });
    }

    /// <inheritdoc />
    public Assembly? LoadAssemblyFromRelease(NodeTypeRelease release, string releaseFolder)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var context = GetOrCreateLoadContextForRelease(release, releaseFolder);
        return context.LoadNodeAssembly();
    }

    /// <inheritdoc />
    public string GetReleaseFolderPath(NodeTypeRelease release)
        => Path.Combine(_absoluteCacheDirectory, release.GetSanitizedPath());

    /// <summary>
    /// Disposes all load contexts and releases resources.
    /// </summary>
    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        logger.LogDebug("Disposing CompilationCacheService, unloading all {Count} contexts", _loadContexts.Count);

        // Drain the dictionary by removing and disposing each context
        foreach (var key in _loadContexts.Keys.ToList())
        {
            if (_loadContexts.TryRemove(key, out var context))
            {
                try
                {
                    context.Dispose();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to dispose context for {NodeName}", key);
                }
            }
        }
    }
}
