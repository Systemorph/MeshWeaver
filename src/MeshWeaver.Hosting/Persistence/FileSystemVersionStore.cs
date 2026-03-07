using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// File system implementation of IVersionQuery.
/// Writes versioned snapshots to a Versions folder alongside the data directory.
/// File naming: {namespace}/{id}_{version}.json
/// Skips ISatelliteContent nodes.
/// </summary>
public class FileSystemVersionStore : IVersionQuery
{
    private readonly string _versionsDirectory;
    private readonly Func<JsonSerializerOptions, JsonSerializerOptions>? _writeOptionsModifier;
    private readonly ConcurrentDictionary<string, string> _lastWrittenContent = new();

    public FileSystemVersionStore(
        string baseDirectory,
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier = null)
    {
        _versionsDirectory = Path.Combine(baseDirectory, ".versions");
        _writeOptionsModifier = writeOptionsModifier;
    }

    /// <summary>
    /// Writes a versioned copy of a node. Called after save.
    /// Skips satellite content nodes.
    /// Deduplicates: skips write if serialized content is identical to last write for this path.
    /// </summary>
    public async Task WriteVersionAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
    {
        if (node.Version <= 0) return;
        if (node.Content is ISatelliteContent) return;

        var writeOptions = _writeOptionsModifier != null
            ? _writeOptionsModifier(options)
            : options;

        // Deduplicate: compare content (excluding version/timestamp) to last write for this path
        var normalizedNode = node with { Version = 0, LastModified = default };
        var normalizedJson = JsonSerializer.Serialize(normalizedNode, writeOptions);
        var nodePath = node.Path ?? "";

        if (_lastWrittenContent.TryGetValue(nodePath, out var lastContent) && lastContent == normalizedJson)
            return; // Same content as last write, skip duplicate
        _lastWrittenContent[nodePath] = normalizedJson;

        var ns = node.Namespace ?? "";
        var dir = string.IsNullOrEmpty(ns)
            ? _versionsDirectory
            : Path.Combine(_versionsDirectory, ns.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);

        var fileName = $"{node.Id}_{node.Version}.json";
        var filePath = Path.Combine(dir, fileName);

        var json = JsonSerializer.Serialize(node, writeOptions);
        await File.WriteAllTextAsync(filePath, json, ct);
    }

    public async IAsyncEnumerable<MeshNodeVersion> GetVersionsAsync(
        string path, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (ns, id) = SplitPath(path);
        var dir = string.IsNullOrEmpty(ns)
            ? _versionsDirectory
            : Path.Combine(_versionsDirectory, ns.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(dir))
            yield break;

        var pattern = $"{id}_*.json";
        var files = Directory.GetFiles(dir, pattern)
            .Select(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var versionStr = name[(id.Length + 1)..];
                return long.TryParse(versionStr, out var v) ? (File: f, Version: v) : default;
            })
            .Where(x => x.File != null)
            .OrderByDescending(x => x.Version);

        foreach (var (file, version) in files)
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            yield return new MeshNodeVersion(
                path, version, info.LastWriteTimeUtc,
                null, null, null);
        }

        await Task.CompletedTask; // Async enumerable requires at least one await
    }

    public async Task<MeshNode?> GetVersionAsync(
        string path, long version, JsonSerializerOptions options,
        CancellationToken ct = default)
    {
        var filePath = GetVersionFilePath(path, version);
        if (filePath == null || !File.Exists(filePath))
            return null;

        var json = await File.ReadAllTextAsync(filePath, ct);
        return JsonSerializer.Deserialize<MeshNode>(json, options);
    }

    public async Task<MeshNode?> GetVersionBeforeAsync(
        string path, long beforeVersion, JsonSerializerOptions options,
        CancellationToken ct = default)
    {
        var (ns, id) = SplitPath(path);
        var dir = string.IsNullOrEmpty(ns)
            ? _versionsDirectory
            : Path.Combine(_versionsDirectory, ns.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(dir))
            return null;

        var pattern = $"{id}_*.json";
        var bestVersion = Directory.GetFiles(dir, pattern)
            .Select(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var versionStr = name[(id.Length + 1)..];
                return long.TryParse(versionStr, out var v) ? v : -1;
            })
            .Where(v => v > 0 && v < beforeVersion)
            .OrderByDescending(v => v)
            .FirstOrDefault();

        if (bestVersion <= 0)
            return null;

        return await GetVersionAsync(path, bestVersion, options, ct);
    }

    private string? GetVersionFilePath(string path, long version)
    {
        var (ns, id) = SplitPath(path);
        var dir = string.IsNullOrEmpty(ns)
            ? _versionsDirectory
            : Path.Combine(_versionsDirectory, ns.Replace('/', Path.DirectorySeparatorChar));
        var fileName = $"{id}_{version}.json";
        return Path.Combine(dir, fileName);
    }

    private static (string Namespace, string Id) SplitPath(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        var ns = lastSlash > 0 ? path[..lastSlash] : "";
        var id = lastSlash > 0 ? path[(lastSlash + 1)..] : path;
        return (ns, id);
    }
}
