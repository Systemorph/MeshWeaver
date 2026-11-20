using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Persistence;
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

    /// <summary>
    /// Creates a version store rooted at a <c>.versions</c> folder under the
    /// given data directory.
    /// </summary>
    /// <param name="baseDirectory">Data directory whose <c>.versions</c> subfolder holds the snapshots.</param>
    /// <param name="ioPoolRegistry">
    /// REQUIRED mesh-scoped registry; the <c>FileSystem</c> pool bridges async file I/O to
    /// <c>IObservable</c>. No <see cref="IoPool.Unbounded"/> fallback — the unbounded pool is
    /// ledgerless (<c>CurrentInFlight</c> is unconditionally 0), so a version write routed onto it
    /// is invisible to the teardown drain (the issue #613 straggler source). See
    /// <see cref="FileSystemStorageAdapter"/>'s constructor for the full rationale.
    /// </param>
    /// <param name="writeOptionsModifier">Optional modifier for JsonSerializerOptions when writing snapshots (e.g., to enable WriteIndented).</param>
    public FileSystemVersionStore(
        string baseDirectory,
        IoPoolRegistry ioPoolRegistry,
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier = null)
    {
        ArgumentNullException.ThrowIfNull(ioPoolRegistry);
        _versionsDirectory = Path.Combine(baseDirectory, ".versions");
        _writeOptionsModifier = writeOptionsModifier;
        _ioPool = ioPoolRegistry.Get(IoPoolNames.FileSystem);
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
            // 🚨 Atomic publication, never File.WriteAllTextAsync on filePath: GetVersions
            // discovers history by globbing "{id}_*.json", so creating the target first and
            // filling it afterwards publishes an EMPTY version that a caller can list and then
            // fail to deserialize. See AtomicFileWrite.
            await AtomicFileWrite.WriteAllTextAsync(filePath, json, ct).ConfigureAwait(false);
            return node;
        });

    /// <inheritdoc />
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

            return versions.ToInlineObservable();
        });

    /// <inheritdoc />
    public IObservable<MeshNode?> GetVersion(string path, long version, JsonSerializerOptions options)
        => _ioPool.Run(async ct =>
        {
            var filePath = GetVersionFilePath(path, version);
            if (filePath == null || !File.Exists(filePath))
                return (MeshNode?)null;

            var json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<MeshNode>(json, options);
        });

    /// <inheritdoc />
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

    /// <inheritdoc />
    /// <remarks>Deletes every <c>{id}_{version}.json</c> snapshot of the node on the
    /// <c>FileSystem</c> pool. The glob is the same one <see cref="GetVersions"/> discovers history
    /// by, filtered to names whose suffix parses as a version, so a sibling whose id merely starts
    /// with this one (<c>a</c> vs <c>a_b</c>) is never touched.</remarks>
    public IObservable<bool> PurgeVersions(string path)
        // Cold (Defer): a purge is a destructive act and must happen on Subscribe, never on call.
        => Observable.Defer(() => _ioPool.RunBlocking(ct =>
        {
            var (ns, id) = SplitPath(path);
            var dir = string.IsNullOrEmpty(ns)
                ? _versionsDirectory
                : Path.Combine(_versionsDirectory, ns.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir))
                return true;

            foreach (var file in Directory.GetFiles(dir, $"{id}_*.json"))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(file);
                if (long.TryParse(name[(id.Length + 1)..], out _))
                    File.Delete(file);
            }
            return true;
        }));

    /// <inheritdoc />
    /// <remarks>Walks the partition's snapshot folder (<c>.versions/{partition}/**</c>, plus the
    /// partition root's own <c>{partition}_{version}.json</c> files at the top of
    /// <c>.versions</c>) on the <c>FileSystem</c> pool, reads each snapshot's write time as its
    /// <see cref="VersionHistoryRow.LastModified"/> (the same clock <see cref="GetVersions"/>
    /// reports), and deletes exactly the files <see cref="VersionHistoryPrune.Select"/> selects.</remarks>
    public IObservable<VersionHistoryPruneOutcome> PruneHistory(string partition, VersionHistoryPrune prune)
        // Cold (Defer): a prune is a destructive act and must happen on Subscribe, never on call.
        => Observable.Defer(() => _ioPool.RunBlocking(ct =>
            DeleteSelected(CollectPartitionRows(partition, ct), prune.Select, ct)));

    /// <inheritdoc />
    /// <remarks>Walks the partition's snapshots exactly like <see cref="PruneHistory"/>, reads the
    /// node type of each node's NEWEST snapshot (the only row <see cref="VersionHistoryDrain"/>
    /// classifies a node by — one file read per node, not per version), and deletes exactly the
    /// files <see cref="VersionHistoryDrain.Select"/> selects.</remarks>
    public IObservable<VersionHistoryPruneOutcome> DrainHistory(string partition, VersionHistoryDrain drain)
        // Cold (Defer): a drain is a destructive act and must happen on Subscribe, never on call.
        => Observable.Defer(() => _ioPool.RunBlocking(ct =>
        {
            var rows = CollectPartitionRows(partition, ct);
            if (drain.MaxRows <= 0 || drain.CutoffByType.IsEmpty)
                return VersionHistoryPruneOutcome.Pruned(0);
            var typed = rows
                .GroupBy(r => r.Row.Path, StringComparer.Ordinal)
                .SelectMany(node =>
                {
                    var newest = node.MaxBy(r => r.Row.Version);
                    return node.Select(r => ReferenceEquals(r.File, newest.File)
                        ? (r.Row with { NodeType = ReadNodeType(r.File) }, r.File)
                        : r);
                })
                .ToList();
            return DeleteSelected(typed, drain.Select, ct);
        }));

    /// <inheritdoc />
    /// <remarks>Deletes the node's <paramref name="maxRows"/> highest-numbered
    /// <c>{id}_{version}.json</c> snapshots, matched like <see cref="PurgeVersions"/> (a sibling whose
    /// id merely starts with this one is never touched).</remarks>
    public IObservable<bool> DiscardNewestVersions(string path, int maxRows)
        // Cold (Defer): a discard is a destructive act and must happen on Subscribe, never on call.
        => Observable.Defer(() => _ioPool.RunBlocking(ct =>
        {
            var (ns, id) = SplitPath(path);
            var dir = string.IsNullOrEmpty(ns)
                ? _versionsDirectory
                : Path.Combine(_versionsDirectory, ns.Replace('/', Path.DirectorySeparatorChar));
            if (maxRows <= 0 || !Directory.Exists(dir))
                return true;

            var newest = Directory.GetFiles(dir, $"{id}_*.json")
                .Select(file => (File: file, Parsed: long.TryParse(Path.GetFileNameWithoutExtension(file)[(id.Length + 1)..], out var v), Version: v))
                .Where(f => f.Parsed)
                .OrderByDescending(f => f.Version)
                .Take(maxRows);
            foreach (var file in newest)
            {
                ct.ThrowIfCancellationRequested();
                File.Delete(file.File);
            }
            return true;
        }));

    /// <summary>The node type recorded in one snapshot file, or null when it cannot be read (a
    /// node whose newest snapshot is unreadable is then not drained: fail closed).</summary>
    private static string? ReadNodeType(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.EnumerateObject()
                       .FirstOrDefault(p => string.Equals(p.Name, "nodeType", StringComparison.OrdinalIgnoreCase))
                       .Value is { ValueKind: JsonValueKind.String } nodeType
                ? nodeType.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Deletes the files of the rows <paramref name="select"/> picks; emits how many went.</summary>
    private static VersionHistoryPruneOutcome DeleteSelected(
        IReadOnlyList<(VersionHistoryRow Row, string File)> rows,
        Func<IEnumerable<VersionHistoryRow>, System.Collections.Immutable.ImmutableList<VersionHistoryRow>> select,
        CancellationToken ct)
    {
        var files = rows.ToLookup(r => (r.Row.Path, r.Row.Version), r => r.File);
        var removed = 0;
        foreach (var row in select(rows.Select(r => r.Row)))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var file in files[(row.Path, row.Version)])
            {
                if (!File.Exists(file))
                    continue;
                File.Delete(file);
                removed++;
            }
        }
        return VersionHistoryPruneOutcome.Pruned(removed);
    }

    /// <summary>
    /// Every snapshot of <paramref name="partition"/> with its file: <c>.versions/{partition}/**</c>,
    /// plus the partition root's own <c>{partition}_{version}.json</c> files at the top of
    /// <c>.versions</c>. Refuses a name that is not one partition token or that resolves outside the
    /// store, because the caller deletes under what this walks.
    /// </summary>
    private List<(VersionHistoryRow Row, string File)> CollectPartitionRows(string partition, CancellationToken ct)
    {
        var root = partition.Trim();
        // A single path token only: '.', '..', separators and query grammar would walk (and
        // delete under) a directory that is not this partition's.
        if (!VersionHistoryPrune.IsPartitionToken(root))
            throw new ArgumentException(
                $"'{partition}' is not a single partition token; refusing to prune its history", nameof(partition));
        var partitionDirectory = Path.GetFullPath(Path.Combine(_versionsDirectory, root));
        var versionsRoot = Path.GetFullPath(_versionsDirectory);
        if (!partitionDirectory.StartsWith(versionsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException(
                $"'{partition}' resolves outside the version store; refusing to prune its history", nameof(partition));

        var rows = new List<(VersionHistoryRow Row, string File)>();
        void Collect(string dir, string ns, SearchOption option, Func<string, bool> accept)
        {
            if (!Directory.Exists(dir))
                return;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json", option))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(file);
                var underscore = name.LastIndexOf('_');
                if (underscore <= 0 || !long.TryParse(name[(underscore + 1)..], out var version))
                    continue;
                var id = name[..underscore];
                if (!accept(id))
                    continue;
                // A file EnumerateFiles yields always has a directory; a null here is a file
                // we cannot place under a node, so it is not a row this prune may touch.
                if (Path.GetDirectoryName(file) is not { } fileDirectory)
                    continue;
                var relativeDir = Path.GetRelativePath(_versionsDirectory, fileDirectory)
                    .Replace(Path.DirectorySeparatorChar, '/');
                var nodeNs = relativeDir == "." ? ns : relativeDir;
                var path = string.IsNullOrEmpty(nodeNs) ? id : $"{nodeNs}/{id}";
                rows.Add((new VersionHistoryRow(path, version, File.GetLastWriteTimeUtc(file)), file));
            }
        }

        // The partition root node itself (namespace "") and everything below it.
        Collect(_versionsDirectory, "", SearchOption.TopDirectoryOnly,
            id => string.Equals(id, root, StringComparison.OrdinalIgnoreCase));
        Collect(partitionDirectory, root, SearchOption.AllDirectories, _ => true);
        return rows;
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
