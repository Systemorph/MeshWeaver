using System.Collections.Immutable;
using MeshWeaver.Mesh;

namespace MeshWeaver.Compiler;

/// <summary>
/// An INDEXED, IN-MEMORY set of <see cref="MeshNode"/>s that answers the compile path's source
/// queries WITHOUT a mesh — the build-process half of issue #1763 ("bake must be compiler-driven,
/// not mesh-driven").
///
/// <para><b>Why this lives inside the toolchain assembly.</b> Which Code nodes a NodeType compiles
/// against is part of the GENERATED INPUT of that compile, exactly like the skeleton generator and
/// the join order — so the rule has to sit inside the full-MVID identity boundary
/// (<see cref="FrameworkBuildIdentity.FullMvidAssemblies"/>). A resolver that lived outside it
/// could change which sources a bake consumes without moving the identity, and every portal would
/// adopt the changed bytes as if nothing had happened.</para>
///
/// <para><b>The equivalence obligation.</b> At runtime the mesh performs this resolution
/// (<c>NodeSources.GetSources</c> → <c>workspace.GetQuery</c> → the storage adapters). This class
/// is a SECOND implementation of the same rule, and a resolver that answers even slightly
/// differently emits assemblies that are subtly not what the mesh would have built — with no error
/// anywhere until a page renders empty. Two things keep that honest:</para>
/// <list type="bullet">
///   <item>Query EXPANSION is not re-implemented: <see cref="CodeQueryResolver.ExpandAll"/> is the
///     same call the runtime makes, so <c>$self</c>, the <c>name=</c> prefix, the <c>@</c>/<c>@@</c>
///     shorthand, the bare-namespace rebase and the implicit <c>nodeType:Code</c> filter can never
///     fork.</item>
///   <item>Query EVALUATION refuses what it does not understand. Only the selectors the expansion
///     actually produces (<c>path:</c>, <c>namespace:</c>, <c>scope:</c>, <c>nodeType:</c>) are
///     supported; anything else makes the resolution UNESTABLISHED and the caller must refuse to
///     compile — the same fail-loud direction <see cref="SourceSnapshot"/> enforces at runtime, and
///     for the same reason (#1218: a short source set produces completely genuine-looking CS0246s
///     about code that is fine).</item>
/// </list>
/// </summary>
public sealed class NodeSet
{
    private readonly ImmutableArray<MeshNode> nodes;
    private readonly ImmutableDictionary<string, MeshNode> byPath;

    private NodeSet(ImmutableArray<MeshNode> nodes, ImmutableDictionary<string, MeshNode> byPath)
    {
        this.nodes = nodes;
        this.byPath = byPath;
    }

    /// <summary>Every node in the set, ordinal by path.</summary>
    public ImmutableArray<MeshNode> Nodes => nodes;

    /// <summary>
    /// Indexes <paramref name="source"/>. Nodes without a path are dropped (the mesh's synced
    /// query drops them too); a duplicate path keeps the LAST occurrence, mirroring the
    /// last-write-wins fold the runtime's <c>ImmutableDictionary</c> accumulation performs.
    /// </summary>
    public static NodeSet Create(IEnumerable<MeshNode> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var map = ImmutableDictionary.CreateBuilder<string, MeshNode>(StringComparer.Ordinal);
        foreach (var node in source)
        {
            if (node is null || string.IsNullOrEmpty(node.Path))
                continue;
            map[node.Path] = node;
        }
        var indexed = map.ToImmutable();
        return new NodeSet(
            [.. indexed.Values.OrderBy(n => n.Path, StringComparer.Ordinal)],
            indexed);
    }

    /// <summary>The node at <paramref name="path"/>, or null. Exact, ordinal — the same lookup the
    /// runtime's <c>GetMeshNodeStream(path)</c> performs.</summary>
    public MeshNode? Find(string? path)
        => string.IsNullOrEmpty(path) ? null : byPath.GetValueOrDefault(path);

    /// <summary>
    /// Resolves the source+test Code node set for the NodeType at <paramref name="selfPath"/>,
    /// exactly as <c>NodeSources.GetSources</c> does at runtime: expand
    /// <paramref name="sources"/> (falling back to <see cref="CodeQueryResolver.DefaultSources"/>)
    /// then <paramref name="tests"/> (falling back to <see cref="CodeQueryResolver.DefaultTests"/>),
    /// and union the matches.
    ///
    /// <para>🚨 The order is DETERMINISTIC here and is NOT at runtime. The mesh folds every query's
    /// results into an <c>ImmutableDictionary&lt;string, MeshNode&gt;</c> and emits
    /// <c>dict.Values</c> — hash-bucket order over per-process-randomised string hashes — and
    /// <see cref="NodeCompileShaping.CombineSources"/> then joins the files in that order. So the
    /// mesh-driven bake is not reproducible run to run, while this one is (query order, then
    /// ordinal by path). The difference is confined to the concatenation order of independent
    /// top-level declarations, which C# is insensitive to; it is pinned by the bake-equivalence
    /// test rather than assumed.</para>
    /// </summary>
    /// <param name="sources">The NodeType's declared <c>Sources</c> queries, or null for the default.</param>
    /// <param name="tests">The NodeType's declared <c>Tests</c> queries, or null for the default.</param>
    /// <param name="selfPath">The NodeType's mesh path — the <c>$self</c> / rebase anchor.</param>
    public NodeSetSourceResolution ResolveSources(
        IReadOnlyList<string>? sources, IReadOnlyList<string>? tests, string selfPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(selfPath);

        var expanded = CodeQueryResolver
            .ExpandAll(sources, CodeQueryResolver.DefaultSources, selfPath)
            .Concat(CodeQueryResolver.ExpandAll(tests, CodeQueryResolver.DefaultTests, selfPath))
            .ToImmutableArray();

        var unsupported = ImmutableArray.CreateBuilder<string>();
        var matched = ImmutableArray.CreateBuilder<MeshNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var query in expanded)
        {
            if (!NodeSetQuery.TryParse(query, out var predicate, out var reason))
            {
                unsupported.Add($"query '{query}' → {reason}");
                continue;
            }
            // Ordinal within a query, queries in declaration order — see the remarks above.
            foreach (var node in nodes)
            {
                if (!predicate.Matches(node) || !seen.Add(node.Path))
                    continue;
                matched.Add(node);
            }
        }

        return new NodeSetSourceResolution(
            matched.ToImmutable(),
            expanded,
            unsupported.ToImmutable());
    }
}

/// <summary>
/// One NodeType's tree-resolved source set, TOGETHER with whether every declared query could
/// actually be evaluated — the mesh-free analogue of <see cref="SourceSnapshot"/>, and carrying the
/// same non-negotiable rule: <b>a compile whose source set could not be established is not a
/// verdict about the code</b>. A caller that compiles anyway hands Roslyn a short set and gets a
/// completely genuine-looking CS0246 about source that is fine.
/// </summary>
/// <param name="Sources">The matched nodes, deduplicated by path, in deterministic order.</param>
/// <param name="ExpandedQueries">Every query that was expanded (diagnostics + provenance).</param>
/// <param name="UnsupportedQueries">Per-query reasons for the queries this evaluator could not
/// answer. Empty ⇒ <see cref="IsEstablished"/>.</param>
public sealed record NodeSetSourceResolution(
    ImmutableArray<MeshNode> Sources,
    ImmutableArray<string> ExpandedQueries,
    ImmutableArray<string> UnsupportedQueries)
{
    /// <summary>True when every expanded query was evaluated — the only state in which the
    /// matched set may be compiled.</summary>
    public bool IsEstablished => UnsupportedQueries.IsEmpty;

    /// <summary>Why the set is not established, or null when it is.</summary>
    public string? UnestablishedReason =>
        IsEstablished ? null : string.Join("; ", UnsupportedQueries);
}
