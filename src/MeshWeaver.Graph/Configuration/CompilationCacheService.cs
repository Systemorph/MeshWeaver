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
    /// </summary>
    /// <param name="nodeName">Sanitized node name used as the assembly base name.</param>
    /// <param name="lastModified">The last modified timestamp of the source MeshNode.</param>
    /// <returns>True if cache is valid and can be used, false if recompilation is needed.</returns>
    bool IsCacheValid(string nodeName, DateTimeOffset lastModified);

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
    /// Loads an assembly into the node's isolated load context.
    /// </summary>
    /// <param name="nodeName">Sanitized node name.</param>
    /// <returns>The loaded assembly, or null if the DLL doesn't exist.</returns>
    Assembly? LoadAssembly(string nodeName);

    /// <summary>
    /// Unloads the AssemblyLoadContext for a node, allowing the DLL to be regenerated.
    /// </summary>
    /// <param name="nodeName">Sanitized node name.</param>
    void UnloadContext(string nodeName);
}

/// <summary>
/// Collectible AssemblyLoadContext for dynamically compiled node assemblies.
/// This context can be unloaded to allow recompilation of the assembly.
/// </summary>
internal sealed class NodeAssemblyLoadContext : AssemblyLoadContext, IDisposable
{
    private readonly string _nodeName;
    private readonly ILogger? _logger;
    private readonly string _dllPath;
    private Assembly? _loadedAssembly;
    private bool _disposed;

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

    public NodeAssemblyLoadContext(string nodeName, string dllPath, ILogger? logger = null)
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

        if (_loadedAssembly != null)
            return _loadedAssembly;

        if (!File.Exists(_dllPath))
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

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // For dependencies, delegate to the default context
        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _loadedAssembly = null;

        _logger?.LogDebug("Unloading AssemblyLoadContext {ContextName}", Name);

        // Initiate unload - the context will be collected when all references are released
        Unload();
    }
}

/// <summary>
/// Service for managing the dynamic compilation cache.
/// Handles cache validation, path generation, cache invalidation,
/// and AssemblyLoadContext management for dynamic assembly loading/unloading.
/// </summary>
internal class CompilationCacheService(
    IOptions<CompilationCacheOptions> options,
    ILogger<CompilationCacheService> logger)
    : ICompilationCacheService, IDisposable
{
    private readonly CompilationCacheOptions _options = options.Value ?? new CompilationCacheOptions();
    private readonly ConcurrentDictionary<string, NodeAssemblyLoadContext> _loadContexts = new();
    private readonly string _absoluteCacheDirectory = ResolveAbsolutePath(options.Value?.CacheDirectory ?? ".mesh-cache");
    private bool _disposed;

    private static string ResolveAbsolutePath(string cacheDirectory) =>
        Path.IsPathRooted(cacheDirectory)
            ? cacheDirectory
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), cacheDirectory));

    /// <inheritdoc />
    public string CacheDirectory => _absoluteCacheDirectory;

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
        var isValid = dllLastWrite >= lastModified;

        if (!isValid)
        {
            logger.LogDebug(
                "Cache stale for {NodeName}: DLL modified at {DllTime}, node modified at {NodeTime}",
                nodeName, dllLastWrite, lastModified);
        }
        else
        {
            logger.LogDebug("Cache hit for {NodeName}", nodeName);
        }

        return isValid;
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
        if (_disposed)
            return;

        _disposed = true;

        logger.LogDebug("Disposing CompilationCacheService, unloading all {Count} contexts", _loadContexts.Count);

        foreach (var kvp in _loadContexts)
        {
            try
            {
                kvp.Value.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to dispose context for {NodeName}", kvp.Key);
            }
        }

        _loadContexts.Clear();
    }
}
