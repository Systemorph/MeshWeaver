using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Read-only <see cref="IStorageAdapter"/> backed by embedded
/// resources in an assembly. Mirrors the path-shape contract of
/// <see cref="FileSystemStorageAdapter"/>: a manifest resource name
/// like <c>"MeshWeaver.Documentation.Data.Architecture.AccessControl.md"</c>
/// (with prefix <c>"MeshWeaver.Documentation.Data."</c>) maps to the
/// node path <c>"Architecture/AccessControl"</c>.
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
    private readonly FileFormatParserRegistry _parserRegistry;
    private readonly Dictionary<string, ResourceEntry> _entriesByPath;
    private readonly Dictionary<string, MeshNode> _seedNodes;

    public EmbeddedResourceStorageAdapter(
        Assembly assembly,
        string prefix,
        IEnumerable<MeshNode>? seedNodes = null)
    {
        _assembly = assembly;
        _prefix = prefix.EndsWith('.') ? prefix : prefix + ".";
        _parserRegistry = new FileFormatParserRegistry();
        _entriesByPath = BuildIndex(assembly, _prefix);
        _seedNodes = (seedNodes ?? [])
            .ToDictionary(n => n.Path.Trim('/'), StringComparer.OrdinalIgnoreCase);
    }

    private record ResourceEntry(string ResourceName, string Path, string Extension);

    private static Dictionary<string, ResourceEntry> BuildIndex(Assembly assembly, string prefix)
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
            var path = rawPath.Replace('.', '/');
            map[path] = new ResourceEntry(name, path, ext);
        }
        return map;
    }

    public async Task<MeshNode?> ReadAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var normalized = path.Trim('/');
        if (_seedNodes.TryGetValue(normalized, out var seed))
            return seed;
        if (!_entriesByPath.TryGetValue(normalized, out var entry))
            return null;

        await using var stream = _assembly.GetManifestResourceStream(entry.ResourceName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(ct);

        var node = await _parserRegistry.TryParseAsync(entry.Extension, entry.ResourceName, content, normalized, ct);
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
        => throw new NotSupportedException(
            $"EmbeddedResourceStorageAdapter is read-only; cannot save '{node.Path}'.");

    public Task DeleteAsync(string path, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"EmbeddedResourceStorageAdapter is read-only; cannot delete '{path}'.");

    public Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsync(
        string? parentPath, CancellationToken ct = default)
    {
        var prefix = string.IsNullOrEmpty(parentPath) ? "" : parentPath.Trim('/') + "/";
        var nodePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _entriesByPath.Values)
        {
            var path = entry.Path;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var remainder = path[prefix.Length..];
            if (remainder.Length == 0)
                continue;

            var firstSlash = remainder.IndexOf('/');
            if (firstSlash < 0)
            {
                // Direct file child
                nodePaths.Add(prefix + remainder);
            }
            else
            {
                // Sub-directory — emit it as a directory path so the routing
                // layer can recurse.
                directoryPaths.Add(prefix + remainder[..firstSlash]);
            }
        }

        foreach (var seedPath in _seedNodes.Keys)
        {
            if (!seedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var remainder = seedPath[prefix.Length..];
            if (remainder.Length == 0)
                continue;

            var firstSlash = remainder.IndexOf('/');
            if (firstSlash < 0)
                nodePaths.Add(prefix + remainder);
            else
                directoryPaths.Add(prefix + remainder[..firstSlash]);
        }

        // Filter out directory paths that are actually node paths (have an
        // index-style file at the directory level).
        directoryPaths.ExceptWith(nodePaths);

        return Task.FromResult<(IEnumerable<string>, IEnumerable<string>)>((nodePaths, directoryPaths));
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var normalized = path.Trim('/');
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
