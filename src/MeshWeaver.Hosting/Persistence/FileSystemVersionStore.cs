using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// File system implementation of <see cref="IVersionQuery"/>. Writes versioned
/// snapshots to a <c>.versions</c> folder alongside the data directory; file
/// naming is <c>{namespace}/{id}_{version}.json</c>.
///
/// <para>All public methods return <see cref="IObservable{T}"/> — see
/// <c>Doc/Architecture/AsynchronousCalls.md</c>. The async file I/O is bridged
/// to <see cref="IObservable{T}"/> via the <c>FileSystem</c> <see cref="IIoPool"/>
/// (<c>IoPoolExtensions.Run</c>), which runs the leaf entirely on the pool with
/// <c>ConfigureAwait(false)</c> behind a concurrency gate — never a bare
/// <c>Observable.FromAsync</c>, which deadlocks under a blocking subscriber.
/// Callers compose with <c>.SelectMany</c> / <c>.Subscribe</c> inside
/// hub-reachable code without bridging to a Task.</para>
/// </summary>
public class FileSystemVersionStore : IVersionQuery
{
    private readonly string _versionsDirectory;
    private readonly Func<JsonSerializerOptions, JsonSerializerOptions>? _writeOptionsModifier;
    // Versioned-snapshot file I/O (WriteVersion / GetVersion) is bridged to
    // IObservable through this pool — never via a bare Observable.FromAsync,
    // which deadlocks under a blocking subscriber. See IoPoolExtensions and
    // Doc/Architecture/AsynchronousCalls.md.
    private readonly IIoPool _ioPool;

    public FileSystemVersionStore(
        string baseDirectory,
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier = null,
        IoPoolRegistry? ioPoolRegistry = null)
    {
        _versionsDirectory = Path.Combine(baseDirectory, ".versions");
        _writeOptionsModifier = writeOptionsModifier;
        _ioPool = ioPoolRegistry?.Get(IoPoolNames.FileSystem) ?? IoPool.Unbounded;
    }

    /// <summary>
    /// Writes a versioned snapshot. Every save gets its own snapshot file
    /// keyed on the post-save Version — no dedup based on content. The
    /// previous content-equality dedup interacted badly with the dual save
    /// path (PersistenceService.SaveNode and MeshNodeTypeSource's persister)
    /// where two saves with the same Name but different counter Versions
    /// would race: the second save's WriteVersion saw the first's
    /// _lastWrittenContent and skipped writing — leaving a version gap in
    /// the snapshot history that <c>VersionQuery_GetVersionBefore</c> caught.
    /// </summary>
    public IObservable<MeshNode> WriteVersion(MeshNode node, JsonSerializerOptions options)
        => _ioPool.Run(async ct =>
        {
            if (node.Version <= 0) return node;

            var writeOptions = _writeOptionsModifier != null
                ? _writeOptionsModifier(options)
                : options;

            var ns = node.Namespace ?? "";
            var dir = string.IsNullOrEmpty(ns)
                ? _versionsDirectory
                : Path.Combine(_versionsDirectory, ns.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(dir);

            var fileName = $"{node.Id}_{node.Version}.json";
            var filePath = Path.Combine(dir, fileName);

            var json = JsonSerializer.Serialize(node, writeOptions);
            await File.WriteAllTextAsync(filePath, json, ct).ConfigureAwait(false);
            return node;
        });

    public IObservable<MeshNodeVersion> GetVersions(string path)
        => Observable.Defer(() =>
        {
            var (ns, id) = SplitPath(path);
            var dir = string.IsNullOrEmpty(ns)
                ? _versionsDirectory
                : Path.Combine(_versionsDirectory, ns.Replace('/', Path.DirectorySeparatorChar));

            if (!Directory.Exists(dir))
                return Observable.Empty<MeshNodeVersion>();

            var pattern = $"{id}_*.json";
            var versions = Directory.GetFiles(dir, pattern)
                .Select(f =>
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    var versionStr = name[(id.Length + 1)..];
                    return long.TryParse(versionStr, out var v) ? (File: f, Version: v) : default;
                })
                .Where(x => x.File != null)
                .OrderByDescending(x => x.Version)
                .Select(x => new MeshNodeVersion(
                    path, x.Version, new FileInfo(x.File).LastWriteTimeUtc,
                    null, null, null));

            return versions.ToObservable();
        });

    public IObservable<MeshNode?> GetVersion(string path, long version, JsonSerializerOptions options)
        => _ioPool.Run(async ct =>
        {
            var filePath = GetVersionFilePath(path, version);
            if (filePath == null || !File.Exists(filePath))
                return (MeshNode?)null;

            var json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<MeshNode>(json, options);
        });

    public IObservable<MeshNode?> GetVersionBefore(string path, long beforeVersion, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            var (ns, id) = SplitPath(path);
            var dir = string.IsNullOrEmpty(ns)
                ? _versionsDirectory
                : Path.Combine(_versionsDirectory, ns.Replace('/', Path.DirectorySeparatorChar));

            if (!Directory.Exists(dir))
                return Observable.Return<MeshNode?>(null);

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
                return Observable.Return<MeshNode?>(null);

            return GetVersion(path, bestVersion, options);
        });

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
