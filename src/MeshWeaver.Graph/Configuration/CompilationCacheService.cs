using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
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
    private Assembly? _loadedAssembly;
    private volatile bool _disposed;

    /// <summary>
    /// Gets the node name associated with this context.
    /// </summary>
    public string NodeName => _nodeName;

    /// <summary>
    /// Gets the loaded assembly, or null if not yet loaded.
    /// </summary>
    public Assembly? LoadedAssembly => _loadedAssembly;

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
    }

    /// <summary>
    /// Loads the node's assembly from disk.
    /// </summary>
    public Assembly? LoadNodeAssembly()
    {
        if (_disposed)
            throw new ObjectDisposedException(Name, "Cannot load assembly from disposed context");

        // Fast path: already loaded
        var loaded = _loadedAssembly;
        if (loaded != null)
            return loaded;

        lock (_loadLock)
        {
            // Double-check after acquiring lock
            if (_disposed)
                throw new ObjectDisposedException(Name, "Cannot load assembly from disposed context");

            loaded = _loadedAssembly;
            if (loaded != null)
                return loaded;

            if (string.IsNullOrEmpty(_dllPath) || !File.Exists(_dllPath))
            {
                _logger?.LogDebug("DLL not found at {DllPath}", _dllPath);
                return null;
            }

            // Load the assembly into this isolated context
            _loadedAssembly = LoadFromAssemblyPath(_dllPath);
            _logger?.LogDebug("Loaded assembly {AssemblyName} into context {ContextName}",
                _loadedAssembly.GetName().Name, Name);

            return _loadedAssembly;
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
        var loaded = _loadedAssembly;
        if (loaded != null)
            return loaded;

        lock (_loadLock)
        {
            // Double-check after acquiring lock
            if (_disposed)
                throw new ObjectDisposedException(Name, "Cannot load assembly from disposed context");

            loaded = _loadedAssembly;
            if (loaded != null)
                return loaded;

            using var assemblyStream = new MemoryStream(assemblyBytes);
            using var pdbStream = pdbBytes != null ? new MemoryStream(pdbBytes) : null;

            _loadedAssembly = LoadFromStream(assemblyStream, pdbStream);
            _logger?.LogDebug("Loaded assembly {AssemblyName} from bytes into context {ContextName}",
                _loadedAssembly.GetName().Name, Name);

            return _loadedAssembly;
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // For dependencies, delegate to the default context
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
    private readonly string _absoluteCacheDirectory = ResolveAbsolutePath(options.Value?.CacheDirectory ?? ".mesh-cache");
    private readonly Lazy<DateTimeOffset> _frameworkTimestamp = new(ComputeFrameworkTimestamp);
    private readonly object _disposeLock = new();
    private volatile bool _disposed;

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

        return Path.GetFullPath(Path.Combine(assemblyDirectory, cacheDirectory));
    }

    private static DateTimeOffset ComputeFrameworkTimestamp()
    {
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
    {
        if (!_options.EnableCompilationCache)
            return false;

        var dllPath = GetDllPath(nodeName);
        var pdbPath = GetPdbPath(nodeName);
        var sourcePath = GetSourcePath(nodeName);

        // All files must exist
        if (!File.Exists(dllPath))
        {
            logger.LogDebug("Cache miss for {NodeName}: DLL not found at {DllPath}", nodeName, dllPath);
            return false;
        }

        if (!File.Exists(pdbPath))
        {
            logger.LogDebug("Cache miss for {NodeName}: PDB not found at {PdbPath}", nodeName, pdbPath);
            return false;
        }

        if (_options.EnableSourceDebugging && !File.Exists(sourcePath))
        {
            logger.LogDebug("Cache miss for {NodeName}: Source not found at {SourcePath}", nodeName, sourcePath);
            return false;
        }

        // Check if DLL is newer than the node's LastModified
        var dllLastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(dllPath), TimeSpan.Zero);

        // Check 1: DLL must be newer than the partition's newest modification
        if (dllLastWrite < lastModified)
        {
            logger.LogDebug(
                "Cache stale for {NodeName}: DLL modified at {DllTime}, partition modified at {PartitionTime}",
                nodeName, dllLastWrite, lastModified);
            return false;
        }

        // Check 2: DLL must be newer than the framework DLL (MeshWeaver.Graph.dll)
        var frameworkTime = GetFrameworkTimestamp();
        if (dllLastWrite < frameworkTime)
        {
            logger.LogDebug(
                "Cache stale for {NodeName}: DLL modified at {DllTime}, framework modified at {FrameworkTime}",
                nodeName, dllLastWrite, frameworkTime);
            return false;
        }

        logger.LogDebug("Cache hit for {NodeName}", nodeName);
        return true;
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

        // First, unload the AssemblyLoadContext to release the file lock
        UnloadContext(nodeName);

        // Small delay to allow GC to collect the unloaded context
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var filesToDelete = new[]
        {
            GetDllPath(nodeName),
            GetPdbPath(nodeName),
            GetSourcePath(nodeName),
            GetXmlDocPath(nodeName)
        };

        foreach (var file in filesToDelete)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    logger.LogDebug("Deleted cached file: {FilePath}", file);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete cached file: {FilePath}", file);
            }
        }
    }

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
            return new NodeAssemblyLoadContext(name, dllPath, logger);
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
