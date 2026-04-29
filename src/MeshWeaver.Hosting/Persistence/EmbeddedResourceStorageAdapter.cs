using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Overlay <see cref="IStorageAdapter"/> backed by embedded
/// resources in an assembly. Mirrors the path-shape contract of
/// <see cref="FileSystemStorageAdapter"/>: a manifest resource name
/// like <c>"MeshWeaver.Documentation.Data.Architecture.AccessControl.md"</c>
/// (with prefix <c>"MeshWeaver.Documentation.Data."</c>) maps to the
/// node path <c>"Architecture/AccessControl"</c>.
///
/// <para>Reads check an in-memory write-overlay first, then seed nodes,
/// then embedded resources. Writes / deletes mutate the overlay only —
/// the embedded resources themselves are immutable. This lets satellite
/// content under a read-only namespace (e.g. comments at
/// <c>Doc/X/_Comment/Y</c> on top of an embedded <c>Doc/X.md</c>) be
/// created without rejecting the SaveNodeAsync. The overlay survives
/// for the lifetime of the adapter — typically the partition's lifetime
/// in <see cref="RoutingPersistenceServiceCore"/> — and intentionally
/// does NOT persist across hub restarts; the embedded-resource layer
/// represents authored content only.</para>
///
/// <para>Used by <see cref="EmbeddedResourcePartitionStorageProvider"/>
/// to surface assembly-bundled documentation, agent definitions and
/// node-type templates as a partition (<c>Doc</c>, <c>Agent</c>, …)
/// without going through the legacy <see cref="IStaticNodeProvider"/>
/// path. Static-provider enumeration during <see cref="MeshDataSource.WithMeshNodes"/>
/// could re-enter the <c>IMessageHub</c> singleton factory and stack-
/// overflow; this adapter sits behind <see cref="RoutingPersistenceServiceCore"/>
/// instead and is touched only on first read.</para>
/// </summary>
public sealed class EmbeddedResourceStorageAdapter : IStorageAdapter
{
    private static readonly string[] SupportedExtensions = [".md", ".cs", ".json"];

    private readonly Assembly _assembly;
    private readonly string _prefix;
    private readonly string _partitionPrefix;
    // Parser registry is rebuilt the first time ReadAsync receives non-default JsonSerializerOptions
    // (i.e. the hub's polymorphism-aware options). Construction-time JsonSerializerOptions aren't
    // available because the adapter is wired into the partition provider before the hub is built;
    // without rebuilding here, the JsonFileParser slot stays empty and any .json embedded resource
    // (e.g. sample _Comment/c1.json) silently returns null on Read.
    private FileFormatParserRegistry _parserRegistry;
    private JsonSerializerOptions? _parserRegistryOptions;
    private readonly Lock _parserRegistryLock = new();
    private readonly Dictionary<string, ResourceEntry> _entriesByPath;
    private readonly Dictionary<string, MeshNode> _seedNodes;
    // Overlay for writes — comments, replies, and other satellite content created
    // under a read-only embedded-resource namespace land here. A null-valued entry
    // tombstones a deleted path so it stops resolving from the embedded layer below
    // (matching the FileSystem adapter's "deleted" semantics).
    private readonly ConcurrentDictionary<string, MeshNode?> _writeOverlay =
        new(StringComparer.OrdinalIgnoreCase);

    public EmbeddedResourceStorageAdapter(
        Assembly assembly,
        string prefix,
        IEnumerable<MeshNode>? seedNodes = null,
        string? partitionNamespace = null)
    {
        _assembly = assembly;
        _prefix = prefix.EndsWith('.') ? prefix : prefix + ".";
        // Routing layer hands us paths *with* the partition namespace
        // ("Doc/Architecture/BusinessRules"); resource names strip it
        // ("Architecture.BusinessRules.md"). Index entries with the partition
        // prefix included so lookups by full path match without per-call
        // string surgery.
        _partitionPrefix = string.IsNullOrEmpty(partitionNamespace)
            ? string.Empty
            : partitionNamespace.Trim('/') + "/";
        // Initial registry has no JsonSerializerOptions → no JsonFileParser. Replaced in
        // GetParserRegistry() on the first ReadAsync that supplies real options.
        _parserRegistry = new FileFormatParserRegistry();
        _entriesByPath = BuildIndex(assembly, _prefix, _partitionPrefix);
        _seedNodes = (seedNodes ?? [])
            .ToDictionary(n => n.Path.Trim('/'), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns a parser registry that knows how to deserialize <c>.json</c> resources
    /// using the hub's polymorphism-aware <see cref="JsonSerializerOptions"/>. Cheap to
    /// recompute, but we cache by reference because the hub's options object is stable
    /// for the hub's lifetime.
    /// </summary>
    private FileFormatParserRegistry GetParserRegistry(JsonSerializerOptions options)
    {
        if (ReferenceEquals(_parserRegistryOptions, options))
            return _parserRegistry;
        lock (_parserRegistryLock)
        {
            if (!ReferenceEquals(_parserRegistryOptions, options))
            {
                _parserRegistry = new FileFormatParserRegistry(options);
                _parserRegistryOptions = options;
            }
            return _parserRegistry;
        }
    }

    private record ResourceEntry(string ResourceName, string Path, string Extension);

    private static Dictionary<string, ResourceEntry> BuildIndex(
        Assembly assembly, string prefix, string partitionPrefix)
    {
        var map = new Dictionary<string, ResourceEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var withoutPrefix = name[prefix.Length..];
            var lastDot = withoutPrefix.LastIndexOf('.');
            if (lastDot <= 0) continue;

            var ext = withoutPrefix[lastDot..];
            if (!SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                continue;

            // Resource names use '.' as both folder separator and extension separator.
            // The last '.' is the extension; everything before is the path with '.' separators.
            var rawPath = withoutPrefix[..lastDot];
            var path = partitionPrefix + rawPath.Replace('.', '/');
            map[path] = new ResourceEntry(name, path, ext);
        }
        return map;
    }

    public async Task<MeshNode?> ReadAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var normalized = path.Trim('/');
        // Overlay precedence: a write or tombstone overrides the embedded layer.
        if (_writeOverlay.TryGetValue(normalized, out var overlaid))
            return overlaid; // may be null = tombstoned
        if (_seedNodes.TryGetValue(normalized, out var seed))
            return seed;
        if (!_entriesByPath.TryGetValue(normalized, out var entry))
            return null;

        await using var stream = _assembly.GetManifestResourceStream(entry.ResourceName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(ct);

        var registry = GetParserRegistry(options);
        var node = await registry.TryParseAsync(entry.Extension, entry.ResourceName, content, normalized, ct);
        if (node == null) return null;

        // Same path-source-of-truth normalization as FileSystemStorageAdapter.
        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash > 0)
        {
            var expectedNamespace = normalized[..lastSlash];
            var expectedId = normalized[(lastSlash + 1)..];
            if (node.Namespace != expectedNamespace)
                node = node with { Namespace = expectedNamespace };
            if (node.Id != expectedId)
                node = node with { Id = expectedId };
        }
        else if (node.Id != normalized)
        {
            node = node with { Id = normalized };
        }

        return node;
    }

    public Task WriteAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
    {
        // Mutate the in-memory overlay only — embedded resources stay immutable.
        _writeOverlay[node.Path.Trim('/')] = node;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var normalized = path.Trim('/');
        // Tombstone (null entry) so a deleted overlay write or shadowed embedded
        // entry stops resolving — this matches FileSystem adapter semantics where
        // a delete is observable as "no node at path".
        _writeOverlay[normalized] = null;
        return Task.CompletedTask;
    }

    public Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsync(
        string? parentPath, CancellationToken ct = default)
    {
        var prefix = string.IsNullOrEmpty(parentPath) ? "" : parentPath.Trim('/') + "/";
        var nodePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _entriesByPath.Values)
        {
            // Skip embedded entries that have been tombstoned by a delete.
            if (_writeOverlay.TryGetValue(entry.Path, out var ov) && ov is null)
                continue;
            AddIfMatchesPrefix(entry.Path, prefix, nodePaths, directoryPaths);
        }

        foreach (var seedPath in _seedNodes.Keys)
        {
            if (_writeOverlay.TryGetValue(seedPath, out var ov) && ov is null)
                continue;
            AddIfMatchesPrefix(seedPath, prefix, nodePaths, directoryPaths);
        }

        // Overlay entries (live writes — non-null) participate in listing too so
        // a comment created at runtime is browseable alongside the embedded nodes.
        foreach (var (overlayPath, value) in _writeOverlay)
        {
            if (value is null) continue;
            AddIfMatchesPrefix(overlayPath, prefix, nodePaths, directoryPaths);
        }

        // Filter out directory paths that are actually node paths (have an
        // index-style file at the directory level).
        directoryPaths.ExceptWith(nodePaths);

        return Task.FromResult<(IEnumerable<string>, IEnumerable<string>)>((nodePaths, directoryPaths));
    }

    private static void AddIfMatchesPrefix(
        string path, string prefix,
        HashSet<string> nodePaths, HashSet<string> directoryPaths)
    {
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return;
        var remainder = path[prefix.Length..];
        if (remainder.Length == 0)
            return;

        var firstSlash = remainder.IndexOf('/');
        if (firstSlash < 0)
            nodePaths.Add(prefix + remainder);
        else
            directoryPaths.Add(prefix + remainder[..firstSlash]);
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var normalized = path.Trim('/');
        // Overlay tombstone wins over embedded entry; live overlay write also wins.
        if (_writeOverlay.TryGetValue(normalized, out var ov))
            return Task.FromResult(ov is not null);
        return Task.FromResult(_seedNodes.ContainsKey(normalized) || _entriesByPath.ContainsKey(normalized));
    }

#pragma warning disable CS1998 // async without await — yields without awaits, kept for IAsyncEnumerable signature
    public async IAsyncEnumerable<object> GetPartitionObjectsAsync(
        string nodePath,
        string? subPath,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Embedded resources do not host partition sub-objects (no .json
        // collections under a node directory). Yield nothing.
        yield break;
    }
#pragma warning restore CS1998

    public Task SavePartitionObjectsAsync(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects,
        JsonSerializerOptions options, CancellationToken ct = default)
        => throw new NotSupportedException("EmbeddedResourceStorageAdapter is read-only.");

    public Task DeletePartitionObjectsAsync(
        string nodePath, string? subPath = null, CancellationToken ct = default)
        => throw new NotSupportedException("EmbeddedResourceStorageAdapter is read-only.");

    public Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(
        string nodePath, string? subPath = null, CancellationToken ct = default)
        => Task.FromResult<DateTimeOffset?>(null);
}
