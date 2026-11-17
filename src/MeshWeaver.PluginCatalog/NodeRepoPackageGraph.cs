using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The node-repo PACKAGE GRAPH of a tree: which packages a <see cref="RepoSnapshot"/> holds, and which
/// of them each one needs. The one reader of those edges for every caller — the plugin tester's
/// install order, shard mounts and dependency reconciliation (through its <c>LocalNodeRepo</c>,
/// which forwards here), and the combo gate's sealed-bundle reuse (MeshWeaver#4413), which needed
/// the same edges from a tool that does not reference the tester.
/// </summary>
public static class NodeRepoPackageGraph
{
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
        var dependencies = DependencyMap(packages, snapshot);

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

    /// <summary>
    /// The package-id → direct-dependency-ids map <see cref="OrderByDependencies"/> sorts by, and
    /// the ONE place a caller may ask "what does this package need present".
    ///
    /// <para>🚨 It is exposed rather than recomputed because a second reader of these three edge
    /// kinds is how an edge silently disappears: MeshWeaver.Plugins' own
    /// <c>scripts/affected-modules.py</c> is already a second implementation of exactly this
    /// contract, and 51 of its 52 <c>requires</c> edges were dead for a while because it compared
    /// the raw entry (<c>"Store@^1.0.0"</c>) against a package id. the tester's <c>GateShardPlan</c>
    /// — which decides what a shard must MOUNT so its installs resolve — reads this map, never a
    /// copy of the rules.</para>
    /// </summary>
    public static ImmutableDictionary<string, ImmutableHashSet<string>> DependencyMap(
        IReadOnlyList<PackageManifest> packages, RepoSnapshot snapshot)
    {
        var ids = packages.Select(p => p.Id).ToImmutableHashSet(StringComparer.Ordinal);
        return packages.ToImmutableDictionary(
            p => p.Id,
            p => CollectDependencies(p, snapshot, ids, sourcesOnly: false),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The COMPILE-INPUT edges alone: package-id → the packages whose nodes this package's
    /// NodeTypes read as <c>content.sources</c>/<c>content.tests</c> (the same query entries
    /// <see cref="DependencyMap"/> reads, by the same reader). A <c>requires</c> entry and a
    /// <c>nodeType</c> path order an INSTALL; they put no byte into a compiled assembly, so they are
    /// not here.
    ///
    /// <para>🚨 Why a second map, and not a filter a caller applies to the first: a caller asking
    /// "can this package's PREBUILT bytes stand while that package changed?" (the combo gate's
    /// sealed-bundle reuse, MeshWeaver#4413) needs exactly the edges along which a change in one
    /// package changes another's compiled bytes. Deriving that from the merged map would need the
    /// edge KIND, which the merged map deliberately does not carry — and re-reading the query
    /// entries in the caller would be the second reader this class exists to prevent.</para>
    /// </summary>
    /// <param name="packages">The discovered packages.</param>
    /// <param name="snapshot">The tree they were discovered in.</param>
    public static ImmutableDictionary<string, ImmutableHashSet<string>> SourceDependencyMap(
        IReadOnlyList<PackageManifest> packages, RepoSnapshot snapshot)
    {
        var ids = packages.Select(p => p.Id).ToImmutableHashSet(StringComparer.Ordinal);
        return packages.ToImmutableDictionary(
            p => p.Id,
            p => CollectDependencies(p, snapshot, ids, sourcesOnly: true),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="package"/> declares at least one NodeType of its own: a <c>.json</c>
    /// node under its folder whose content is a <see cref="NodeTypeDefinition"/> (the same <c>$type</c> test
    /// <see cref="SourceDependencyMap"/> applies to read the compile-input edges).
    ///
    /// <para>🚨 A package that declares none has nothing a bake can compile, so no bake ever writes a
    /// bundle for it (<c>TreeBake.WriteBundles</c> skips a package with no compiled entry). A caller
    /// deciding whether a sealed bundle "stands" for a package (the combo gate's reuse,
    /// MeshWeaver#4413) must therefore ask this first: "no seal carries a bundle" is the normal state
    /// of such a package, not evidence that it changed.</para>
    /// </summary>
    /// <param name="package">The package.</param>
    /// <param name="snapshot">The tree it was discovered in.</param>
    public static bool DefinesNodeTypes(PackageManifest package, RepoSnapshot snapshot)
    {
        var prefix = (package.SourceFolder ?? package.Id) + "/";
        return snapshot.Files.Any(file =>
            file.Path.StartsWith(prefix, StringComparison.Ordinal)
            && file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && IsNodeTypeDefinition(file.Content));
    }

    private static bool IsNodeTypeDefinition(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Object
                && content.TryGetProperty("$type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == nameof(NodeTypeDefinition);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ImmutableHashSet<string> CollectDependencies(
        PackageManifest package, RepoSnapshot snapshot, ImmutableHashSet<string> packageIds,
        bool sourcesOnly)
    {
        var deps = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        // 🚨 THROUGH DependencyId, never the raw entry. A `requires` entry carries an optional
        // version range — `"Store@^1.0.0"` — and comparing the whole string against a package ID
        // matches nothing, so the edge silently disappears. Measured 2026-08-21 across
        // MeshWeaver.Plugins: 51 of its 52 declared `requires` edges were dead, including every
        // one of the eleven modules `Essentials` declares, so an `Mcp` change ordered (and, once
        // the bake reads this same graph, REBUILT) without `Essentials`. `PackageDependencyGraph`
        // has parsed it correctly all along; this reader is the one that drifted.
        if (!sourcesOnly)
            foreach (var required in package.Requires.Select(PackageDependencyGraph.DependencyId))
                if (required.Length > 0 && packageIds.Contains(required) && required != package.Id)
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
            if (sourcesOnly)
                continue;
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
