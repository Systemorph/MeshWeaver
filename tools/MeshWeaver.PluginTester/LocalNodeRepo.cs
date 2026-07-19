using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.PluginCatalog;

namespace MeshWeaver.PluginTester;

/// <summary>
/// Reads a node-repo checkout (the <c>MeshWeaver.Plugins</c> working tree) from the local
/// filesystem into GitSync's <see cref="RepoSnapshot"/> shape, discovers the installable
/// packages (top-level folders whose <c>index.json</c> declares a root node), and orders them
/// by their cross-package source dependencies (a package whose NodeTypes pull shared sources
/// from a sibling package — e.g. Edu sharing Store sources — installs AFTER that sibling, so
/// every referenced Code node has landed before the dependent type compiles).
/// </summary>
public static class LocalNodeRepo
{
    /// <summary>Directories that never carry mesh nodes (VCS/CI/build internals).</summary>
    private static readonly ImmutableHashSet<string> ExcludedDirectories =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
            ".git", ".github", ".claude", "node_modules", "bin", "obj");

    /// <summary>
    /// Sweeps <paramref name="repoRoot"/> into a <see cref="RepoSnapshot"/> on the file I/O
    /// pool (the filesystem walk is a sync-blocking leaf — never run it on a hub scheduler).
    /// Valid-UTF-8 files ride as text, everything else as binary bytes — the same
    /// classification GitSync's GitHub fetch applies.
    /// </summary>
    public static IObservable<RepoSnapshot> Load(string repoRoot, IIoPool pool) =>
        pool.InvokeBlocking(_ =>
        {
            var root = Path.GetFullPath(repoRoot);
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException($"Repo root '{root}' does not exist.");

            var files = new List<RepoFile>();
            Sweep(root, root, files);
            return new RepoSnapshot("local", files
                .OrderBy(f => f.Path, StringComparer.Ordinal)
                .ToImmutableList());
        });

    private static void Sweep(string root, string directory, List<RepoFile> files)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            var bytes = File.ReadAllBytes(file);
            files.Add(TryDecodeUtf8(bytes, out var text)
                ? new RepoFile(relative, text)
                : new RepoFile(relative, string.Empty, bytes));
        }
        foreach (var sub in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(sub);
            if (ExcludedDirectories.Contains(name))
                continue;
            Sweep(root, sub, files);
        }
    }

    // Strict UTF-8 probe (throw-on-invalid decoder) so an arbitrary byte stream — a video, a
    // font — is classified binary rather than lossily decoded. Mirrors OctokitGitHubRepoClient.
    private static bool TryDecodeUtf8(byte[] bytes, out string text)
    {
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Discovers every installable package in the snapshot: the roots
    /// <see cref="NodeRepoPackageSource"/> lists (Space / Store-typed <c>&lt;Pkg&gt;/index.json</c>)
    /// PLUS any remaining top-level folder whose <c>index.json</c> declares a node type the
    /// listing does not recognise (e.g. a self-typed root whose type ships in the same package)
    /// — the gate must exercise every node-repo folder, not only the storefront-listed ones.
    /// </summary>
    public static IObservable<IReadOnlyList<PackageManifest>> DiscoverPackages(RepoSnapshot snapshot)
    {
        var source = new NodeRepoPackageSource(
            (_, _, _, _) => Observable.Return(snapshot), repoUrl: "local");
        return source.ListPackages("HEAD").Select(listed =>
        {
            var known = listed.Select(m => m.Id).ToImmutableHashSet(StringComparer.Ordinal);
            var extra = snapshot.Files
                .Where(f =>
                {
                    var slash = f.Path.IndexOf('/');
                    return slash > 0
                        && f.Path.IndexOf('/', slash + 1) < 0
                        && f.Path.AsSpan(slash + 1).Equals("index.json", StringComparison.OrdinalIgnoreCase)
                        && !known.Contains(f.Path[..slash])
                        && DeclaresNodeType(f.Content);
                })
                .Select(f =>
                {
                    var id = f.Path[..f.Path.IndexOf('/')];
                    return new PackageManifest
                    {
                        Id = id,
                        Name = id,
                        Kind = PackageKind.NodeRepo,
                        TargetPartition = id,
                        SourceFolder = id,
                        Version = snapshot.CommitSha,
                    };
                });
            return (IReadOnlyList<PackageManifest>)listed.Concat(extra)
                .OrderBy(m => m.Id, StringComparer.Ordinal)
                .ToImmutableList();
        });
    }

    private static bool DeclaresNodeType(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("nodeType", out var nt)
                && nt.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(nt.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Orders the packages so that every package installs AFTER the packages it references.
    /// Dependencies come from three places: each NodeType node's
    /// <c>content.sources</c>/<c>content.tests</c> query entries (an <c>@Other/…</c> shorthand
    /// or a multi-segment <c>namespace:Other/…</c> whose first segment is another package id),
    /// every node's <c>nodeType</c> when it is a path into another package (the plugin roots are
    /// typed <c>Store/Plugin</c> — the Store package must land first so the type exists), and
    /// the advisory <see cref="PackageManifest.Requires"/> list. Cycles fall back to
    /// alphabetical order among the remaining packages.
    /// </summary>
    public static IReadOnlyList<PackageManifest> OrderByDependencies(
        IReadOnlyList<PackageManifest> packages, RepoSnapshot snapshot)
    {
        var ids = packages.Select(p => p.Id).ToImmutableHashSet(StringComparer.Ordinal);
        var dependencies = packages.ToDictionary(
            p => p.Id,
            p => CollectDependencies(p, snapshot, ids),
            StringComparer.Ordinal);

        // Kahn topological sort, alphabetical among the ready set for determinism.
        var result = new List<PackageManifest>();
        var remaining = packages.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        while (remaining.Count > 0)
        {
            var placed = result.Select(r => r.Id).ToImmutableHashSet(StringComparer.Ordinal);
            var next = remaining.FirstOrDefault(p => dependencies[p.Id].All(placed.Contains));
            if (next is null)
            {
                // Dependency cycle — install the rest alphabetically; compiles that need a
                // later sibling settle once its sources land (the sources watcher re-marks
                // the type dirty), and the gate reports the terminal status either way.
                result.AddRange(remaining);
                break;
            }
            result.Add(next);
            remaining.Remove(next);
        }
        return result;
    }

    private static ImmutableHashSet<string> CollectDependencies(
        PackageManifest package, RepoSnapshot snapshot, ImmutableHashSet<string> packageIds)
    {
        var deps = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var required in package.Requires)
            if (packageIds.Contains(required) && required != package.Id)
                deps.Add(required);

        var prefix = (package.SourceFolder ?? package.Id) + "/";
        foreach (var file in snapshot.Files)
        {
            if (!file.Path.StartsWith(prefix, StringComparison.Ordinal)
                || !file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var entry in ReadSourceQueries(file.Content))
            {
                var referenced = FirstSegmentOf(entry);
                if (referenced is not null
                    && referenced != package.Id
                    && packageIds.Contains(referenced))
                    deps.Add(referenced);
            }
            // A node typed by a PATH into another package (e.g. every plugin root's
            // nodeType Store/Plugin) needs that package's type node to exist first.
            var nodeType = ReadNodeType(file.Content);
            if (nodeType is not null && nodeType.Contains('/', StringComparison.Ordinal))
            {
                var typePackage = nodeType[..nodeType.IndexOf('/')];
                if (typePackage != package.Id && packageIds.Contains(typePackage))
                    deps.Add(typePackage);
            }
        }
        return deps.ToImmutable();
    }

    private static string? ReadNodeType(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("nodeType", out var nt)
                && nt.ValueKind == JsonValueKind.String
                    ? nt.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The sources/tests query entries of a NodeType node's content; empty for any other file.
    private static IEnumerable<string> ReadSourceQueries(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            yield break;
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Object
                || !content.TryGetProperty("$type", out var type)
                || type.GetString() != nameof(NodeTypeDefinition))
                yield break;
            foreach (var property in new[] { "sources", "tests" })
            {
                if (!content.TryGetProperty(property, out var list)
                    || list.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var element in list.EnumerateArray())
                    if (element.ValueKind == JsonValueKind.String
                        && element.GetString() is { Length: > 0 } entry)
                        yield return entry;
            }
        }
    }

    /// <summary>
    /// The top-level segment a sources/tests query entry points at, or null when the entry is
    /// package-local (a bare <c>namespace:Source</c> is rebased onto the owning type). Handles
    /// the <c>name=</c> display prefix, the <c>@path</c>/<c>@@path</c> shorthand, and
    /// <c>namespace:</c>/<c>path:</c> query tokens.
    /// </summary>
    private static string? FirstSegmentOf(string entry)
    {
        // Strip the optional `name=` display prefix (e.g. "shared=@Edu/InstallManifest/Source"):
        // present when an '=' occurs before any whitespace or query-token colon.
        var value = entry;
        var eq = value.IndexOf('=');
        if (eq > 0)
        {
            var head = value[..eq];
            if (!head.Contains(' ', StringComparison.Ordinal)
                && !head.Contains(':', StringComparison.Ordinal))
                value = value[(eq + 1)..];
        }

        if (value.StartsWith("@@", StringComparison.Ordinal))
            return TopSegment(value[2..]);
        if (value.StartsWith('@'))
            return TopSegment(value[1..]);

        foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("namespace:", StringComparison.Ordinal))
                return MultiSegmentTop(token["namespace:".Length..]);
            if (token.StartsWith("path:", StringComparison.Ordinal))
                return MultiSegmentTop(token["path:".Length..]);
        }
        return null;

        // A single-segment namespace (no '/') is rebased onto the owning NodeType — local.
        static string? MultiSegmentTop(string path) =>
            path.Contains('/', StringComparison.Ordinal) ? TopSegment(path) : null;

        static string? TopSegment(string path)
        {
            var slash = path.IndexOf('/');
            var segment = slash < 0 ? path : path[..slash];
            return segment.Length == 0 || segment.StartsWith('$') ? null : segment;
        }
    }
}
