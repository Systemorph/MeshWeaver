using System.Collections.Generic;
using System.Text.RegularExpressions;
using MeshWeaver.Mesh;

namespace MeshWeaver.Compiler;

/// <summary>
/// Shared query-expansion helpers used by both the compiler (to find which Code
/// nodes should be compiled with a NodeType) and the NodeType side menu (to list
/// those same files as "Sources" / "Tests"). Centralised here so the listing the
/// user sees in the UI is guaranteed to match the files the runtime actually
/// pulls into the NodeType's assembly.
/// <para>
/// Rules (mirrored from <c>NodeTypeDefinition.Sources</c>):
/// </para>
/// <list type="bullet">
///   <item>An optional <c>name=</c> prefix (e.g. <c>"shared=@Lib/Common"</c>)
///     names the query — the GUI groups the resolved files under that name.
///     Unnamed entries fall into the default group (<see cref="DefaultSourceGroupName"/> /
///     <see cref="DefaultTestGroupName"/>). The name never reaches the mesh query.</item>
///   <item><c>$self</c> expands to the owning NodeType's path.</item>
///   <item>A leading <c>@@</c> or <c>@</c> is a shorthand that yields both a
///     <c>path:X</c> exact match and a <c>namespace:X scope:subtree</c> folder
///     match (de-duplicated downstream by the caller).</item>
///   <item>A <c>namespace:X</c> value with no <c>/</c> (e.g. bare <c>Source</c>
///     or <c>Test</c>) is rebased onto the NodeType's own path so the defaults
///     read as "my own Source / Test folder".</item>
///   <item>A query that does not already name a node type is ANDed with
///     <c>nodeType:Code</c>, so non-code children never leak in. One that names its
///     own (<c>nodeType:Scope</c>, say) is left as authored — the filter is a
///     DEFAULT, not an override.</item>
/// </list>
/// </summary>
public static class CodeQueryResolver
{
    /// <summary>
    /// Default Source query when a NodeType doesn't declare <c>NodeTypeDefinition.Sources</c>.
    /// Resolves to <c>{NodeTypePath}/Source</c> subtree.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSources =
        [$"namespace:{CodeConventions.SourceSubNamespace} scope:subtree"];

    /// <summary>
    /// Default Test query when a NodeType doesn't declare <c>NodeTypeDefinition.Tests</c>.
    /// Resolves to <c>{NodeTypePath}/Test</c> subtree. Mirrors <see cref="DefaultSources"/>
    /// so the side-menu Sources/Tests split matches the convention.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultTests =
        [$"namespace:{CodeConventions.TestSubNamespace} scope:subtree"];

    /// <summary>Group name for unnamed Source query entries.</summary>
    public const string DefaultSourceGroupName = "src";

    /// <summary>Group name for unnamed Test query entries.</summary>
    public const string DefaultTestGroupName = "test";

    /// <summary>
    /// A query name is a single identifier-ish token — anything else (whitespace,
    /// <c>:</c>, <c>/</c>, <c>@</c>) means the <c>=</c> belongs to the query body,
    /// not a name prefix.
    /// </summary>
    private static readonly Regex NamePattern =
        new(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]*$", RegexOptions.Compiled);

    /// <summary>
    /// Splits the optional <c>name=</c> prefix off one raw Sources/Tests entry.
    /// Returns <c>(null, query)</c> when the entry carries no (valid) name.
    /// </summary>
    public static (string? Name, string Query) ParseName(string rawEntry)
    {
        var trimmed = rawEntry.Trim();
        var eq = trimmed.IndexOf('=');
        if (eq > 0)
        {
            var candidate = trimmed[..eq].TrimEnd();
            var rest = trimmed[(eq + 1)..].Trim();
            if (rest.Length > 0 && NamePattern.IsMatch(candidate))
                return (candidate, rest);
        }
        return (null, trimmed);
    }

    /// <summary>
    /// Expands one raw query entry from <c>NodeTypeDefinition.Sources</c> or
    /// <c>NodeTypeDefinition.Tests</c> into one-or-more concrete mesh query
    /// strings ready for <c>IMeshService.Query&lt;MeshNode&gt;</c>. An optional
    /// <c>name=</c> prefix is stripped first — the compiler ignores grouping.
    ///
    /// <para>Every expansion that names an absolute location is followed by its MOUNT-ANCHORED
    /// sibling when the two differ — see <see cref="AnchorToMount"/> for why, and for the
    /// <c>@@</c>-include precedent it shares its rule with.</para>
    /// </summary>
    public static IEnumerable<string> Expand(string rawQuery, string selfPath)
    {
        var (_, query) = ParseName(rawQuery);
        var expanded = query.Replace("$self", selfPath).Trim();

        var isAt = expanded.StartsWith("@@") || expanded.StartsWith("@");
        if (isAt)
        {
            var stripped = expanded.TrimStart('@').TrimStart();
            if (stripped.Length == 0) yield break;
            if (stripped.Contains(':'))
            {
                foreach (var q in WithMountAnchoredSibling(WithCodeTypeFilter(stripped), selfPath))
                    yield return q;
                yield break;
            }
            foreach (var q in WithMountAnchoredSibling(
                         WithCodeTypeFilter($"path:{stripped}"), selfPath))
                yield return q;
            foreach (var q in WithMountAnchoredSibling(
                         WithCodeTypeFilter($"namespace:{stripped} scope:subtree"), selfPath))
                yield return q;
            yield break;
        }

        foreach (var q in WithMountAnchoredSibling(
                     WithCodeTypeFilter(RebaseRelativeNamespace(expanded, selfPath)), selfPath))
            yield return q;
    }

    /// <summary>
    /// <paramref name="query"/>, followed by its mount-anchored form when anchoring changes it.
    /// Both are emitted — never one instead of the other — because which spelling resolves is a
    /// property of the MOUNT, not of the entry, and offline nothing here can know which.
    ///
    /// <para>🚨 <b>This is a UNION, not a fallback, and the difference matters.</b> Every consumer
    /// treats the query list as a union (<c>workspace.GetQuery</c> unions its queries;
    /// <c>NodeSet.ResolveSources</c> adds each query's matches), so there is no precedence step to
    /// make the anchored spelling win — unlike
    /// <c>NodeCompileShaping.ResolveCodeIncludes</c>, which reads ONE node per include and therefore
    /// really can try anchored first and fall back. Raised in review on this change; an earlier
    /// version of this comment claimed fallback semantics and was wrong.</para>
    ///
    /// <para><b>What the union costs, stated rather than assumed.</b> A query naming nothing
    /// contributes nothing: the probe folds matches into a dictionary keyed by node path
    /// (<see cref="NodeCompileShaping.ApplyQueryChange"/>) and merges legs with
    /// <c>GroupBy(n =&gt; n.Path)</c>, so the SAME node reached by both spellings is deduplicated.
    /// The residual case is a mesh that holds the same content at BOTH mounts as two distinct sets
    /// of nodes; those would then both enter the compilation and could collide (<c>CS0101</c>) or mix
    /// revisions. That state is already pathological — it is two installations of one library — and
    /// the alternative is worse: emitting ONLY the anchored spelling would silently break a
    /// genuinely absolute cross-partition reference whose first segment happens to appear deeper in
    /// the owning path. Precedence that is safe in both directions needs a real resolution step in
    /// the runtime AND in the bake, which is a larger change than this fix and is not smuggled in
    /// here.</para>
    /// </summary>
    private static IEnumerable<string> WithMountAnchoredSibling(string query, string selfPath)
    {
        yield return query;
        if (AnchorToMount(query, selfPath) is { } anchored)
            yield return anchored;
    }

    /// <summary>
    /// 🚨 Rebases the <c>path:</c> / <c>namespace:</c> value of an expanded query onto the mount
    /// prefix the OWNING NodeType lives under — or <c>null</c> when that changes nothing.
    ///
    /// <para><b>Why this exists.</b> A cross-type source reference is authored MOUNT-RELATIVE,
    /// exactly like an <c>@@</c> include: <c>samples/Graph/Data/Northwind/Product.json</c> declares
    /// <c>shared=@Northwind/AnalyticsCatalog/Source/Supplier</c>, which is the right spelling at a
    /// root mount and the wrong one in a statically-imported partition, where the nodes are served
    /// from a prefix (<c>MeshWeaver/samples/Graph/Data/Northwind/…</c>). Resolved verbatim there the
    /// entry matches NOTHING — and because the type's OWN <c>namespace:Source scope:subtree</c> is
    /// rebased on <c>$self</c> and does match, the merged set is non-empty, so neither
    /// <see cref="SourceSnapshot"/>'s emptiness check nor <c>PreWarmStatus.NoSources</c> fires.
    /// Roslyn is handed a set SHORT of the sibling entity sources and reports a completely
    /// genuine-looking <c>CS0246: The type or namespace name 'Supplier' could not be found</c>
    /// about code that is fine — issue #4813, measured on memex-cloud where
    /// <c>MeshWeaver/samples/Graph/Data/Northwind/Product</c> failed that way on every pod that
    /// attempted it, with source discovery reporting 2 matched Code nodes.</para>
    ///
    /// <para><b>The include half of this defect was already closed and this is the same rule.</b>
    /// <c>NodeCompileShaping.AnchorIncludePath</c> exists for the identical failure on <c>@@</c>
    /// include paths (an unresolved include is left verbatim, so Roslyn parses the <c>@@</c> line
    /// and reports on path segments as if they were symbols). Calling THAT function rather than a
    /// second copy of the rule is deliberate: a source query and an include that name the same node
    /// must not disagree about where it lives.</para>
    ///
    /// <para><b>What it deliberately does NOT touch.</b> Two guards, and both are load-bearing:</para>
    /// <list type="number">
    ///   <item>🚨 <b>A value ALREADY ROOTED AT THIS MOUNT is never touched</b> — decided by
    ///     <see cref="SharesMountRoot"/>, i.e. the value's first segment equals
    ///     <paramref name="selfPath"/>'s. Without it, <c>NodeCompileShaping.AnchorIncludePath</c>'s
    ///     deepest-segment walk DOUBLE-PREFIXES a perfectly good absolute path whenever the mount
    ///     root repeats further down: for <c>selfPath = Space/Source/Nested/Space/Type</c> the
    ///     already-rebased own-source value <c>Space/Source/Nested/Space/Type/Source</c> anchors on
    ///     the SECOND <c>Space</c> and comes out as
    ///     <c>Space/Source/Nested/Space/Source/Nested/Space/Type/Source</c> — a path that exists
    ///     nowhere. It would not lose the correct query (that one is emitted too), but a query
    ///     naming nothing is not free here: it is one more leg in
    ///     <c>MeshNodeCompilationService.DirectSourceProbe</c>'s <c>CombineLatest</c>, and a leg
    ///     that ERRORS makes the whole snapshot UNESTABLISHED, which refuses the compile
    ///     (<see cref="SourceSnapshot"/>). Raised in review on this change.</item>
    ///   <item>A genuinely cross-partition reference stays verbatim: <c>shared=@Store/Core/Source</c>
    ///     read from <c>rbuergi/OperationRequest</c> — the #3903 shape — has no <c>Store</c> segment
    ///     to anchor to, so <c>NodeCompileShaping.AnchorIncludePath</c> returns it unchanged and this method
    ///     answers <c>null</c>.</item>
    /// </list>
    ///
    /// <para>Between them, a root-mounted type keeps behaving exactly as before — which is the whole
    /// reason the samples work in the Monolith.</para>
    /// </summary>
    /// <param name="query">One fully expanded query, as <see cref="Expand"/> emits it.</param>
    /// <param name="selfPath">The owning NodeType's mesh path — the mount anchor.</param>
    internal static string? AnchorToMount(string query, string selfPath)
    {
        if (string.IsNullOrEmpty(selfPath)) return null;

        // The two tokens whose value names a mesh location. Spelled as two calls rather than held
        // in a static array: a `static` collection is forbidden outright (NoStaticState.md), and a
        // `string[]` is not immutable however it is initialised. Raised in review on this change.
        var rewritten = AnchorTokenValue(query, "path:", selfPath);
        rewritten = AnchorTokenValue(rewritten, "namespace:", selfPath);

        return string.Equals(rewritten, query, System.StringComparison.Ordinal) ? null : rewritten;
    }

    /// <summary>
    /// <paramref name="query"/> with the value of <paramref name="key"/> anchored onto
    /// <paramref name="selfPath"/>'s mount — or unchanged when the token is absent, already rooted
    /// at that mount, or has nothing to anchor to.
    /// </summary>
    private static string AnchorTokenValue(string query, string key, string selfPath)
    {
        if (TryGetTokenValue(query, key) is not { } value) return query;
        if (SharesMountRoot(value, selfPath)) return query;
        var anchored = NodeCompileShaping.AnchorIncludePath(value, selfPath);
        return string.Equals(anchored, value, System.StringComparison.Ordinal)
            ? query
            : ReplaceTokenValue(query, key, anchored);
    }

    /// <summary>
    /// <c>true</c> when <paramref name="value"/> already begins at the same mount root as
    /// <paramref name="selfPath"/> — their first path segments are equal — so it is already absolute
    /// HERE and anchoring it could only duplicate the prefix.
    /// </summary>
    private static bool SharesMountRoot(string value, string selfPath)
    {
        var a = FirstSegment(value);
        var b = FirstSegment(selfPath);
        return a.Length > 0 && a.Equals(b, System.StringComparison.Ordinal);
    }

    /// <summary>The part of <paramref name="path"/> before the first <c>/</c>, or all of it.</summary>
    private static string FirstSegment(string path)
    {
        var slash = path.IndexOf('/');
        return slash < 0 ? path : path[..slash];
    }

    /// <summary>Replaces the value of <paramref name="key"/> in <paramref name="query"/>, keeping
    /// every other token — including the trailing <c>scope:</c> / <c>nodeType:</c> filters — exactly
    /// as it was.</summary>
    private static string ReplaceTokenValue(string query, string key, string newValue)
    {
        var idx = query.IndexOf(key, System.StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return query;
        var valueStart = idx + key.Length;
        var valueEnd = valueStart;
        while (valueEnd < query.Length && !char.IsWhiteSpace(query[valueEnd]))
            valueEnd++;
        return query[..valueStart] + newValue + query[valueEnd..];
    }

    /// <summary>
    /// Expands a list of raw query entries (falling back to <paramref name="defaults"/>
    /// when the list is null or empty). Centralises the "use declared or fall back"
    /// decision so callers don't re-implement it.
    /// </summary>
    public static IEnumerable<string> ExpandAll(IReadOnlyList<string>? rawQueries, IReadOnlyList<string> defaults, string selfPath)
    {
        var source = rawQueries is { Count: > 0 } ? rawQueries : defaults;
        foreach (var raw in source)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            foreach (var final in Expand(raw, selfPath))
                yield return final;
        }
    }

    private static string RebaseRelativeNamespace(string query, string selfPath)
    {
        const string nsKey = "namespace:";
        var idx = query.IndexOf(nsKey, System.StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return query;

        var valueStart = idx + nsKey.Length;
        var valueEnd = valueStart;
        while (valueEnd < query.Length && !char.IsWhiteSpace(query[valueEnd]))
            valueEnd++;
        var value = query.Substring(valueStart, valueEnd - valueStart);

        if (value.Length == 0 || value.Contains('/')) return query;

        var rebased = $"{selfPath}/{value}";
        return query.Substring(0, valueStart) + rebased + query.Substring(valueEnd);
    }

    private static string WithCodeTypeFilter(string query) =>
        query.Contains("nodeType:", System.StringComparison.OrdinalIgnoreCase)
            ? query
            : $"{query} nodeType:{CodeConventions.CodeNodeType}";

    /// <summary>
    /// Groups the raw Sources/Tests entries by their <c>name=</c> prefix (falling back
    /// to <paramref name="defaults"/> when null/empty and to <paramref name="defaultName"/>
    /// for unnamed entries). Groups keep first-appearance order; each carries its raw
    /// queries, the fully expanded mesh queries, and — when all its namespace queries
    /// agree on one root — the <see cref="CodeQueryGroup.BaseNamespace"/> the GUI
    /// relativises file paths against.
    /// </summary>
    public static IReadOnlyList<CodeQueryGroup> GroupAll(
        IReadOnlyList<string>? rawQueries,
        IReadOnlyList<string> defaults,
        string selfPath,
        string defaultName)
    {
        var source = rawQueries is { Count: > 0 } ? rawQueries : defaults;
        var order = new List<string>();
        var rawByName = new Dictionary<string, List<string>>(System.StringComparer.Ordinal);
        var expandedByName = new Dictionary<string, List<string>>(System.StringComparer.Ordinal);

        foreach (var raw in source)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var (name, query) = ParseName(raw);
            var groupName = name ?? defaultName;
            if (!rawByName.TryGetValue(groupName, out var rawList))
            {
                order.Add(groupName);
                rawByName[groupName] = rawList = new List<string>();
                expandedByName[groupName] = new List<string>();
            }
            rawList.Add(query);
            expandedByName[groupName].AddRange(Expand(query, selfPath));
        }

        var groups = new List<CodeQueryGroup>(order.Count);
        foreach (var name in order)
            groups.Add(new CodeQueryGroup(
                name,
                rawByName[name],
                expandedByName[name],
                TryGetCommonNamespaceRoot(expandedByName[name], selfPath)));
        return groups;
    }

    /// <summary>
    /// Extracts the one namespace root all of a group's <c>namespace:</c> queries share
    /// — or null when they disagree (mixed roots can't be relativised consistently).
    /// <c>path:</c>-only queries contribute nothing.
    ///
    /// <para>🚨 A root and its MOUNT-ANCHORED form are the SAME root, resolved two ways, so they do
    /// not count as a disagreement — the anchored spelling wins. Without this, an entry that
    /// <see cref="AnchorToMount"/> anchors would hand the GUI a group whose root is null and whose
    /// files therefore render as full paths, purely because the resolver now also asks for the
    /// spelling that actually resolves.</para>
    ///
    /// <para>🚨 The equivalence is the ACTUAL anchoring relationship — <c>anchored ==
    /// AnchorIncludePath(authored, selfPath)</c> — never "one is a suffix of the other". A suffix
    /// test folds roots that have nothing to do with each other: a root-mounted group holding
    /// <c>@A/B</c> and <c>@B</c> would see <c>A/B</c> as the anchored form of the distinct root
    /// <c>B</c>, report a single base, and relativise a genuinely mixed group against it. That is
    /// why <paramref name="selfPath"/> has to reach this method at all. Raised in review on this
    /// change.</para>
    /// </summary>
    /// <param name="expandedQueries">The group's fully expanded queries.</param>
    /// <param name="selfPath">The owning NodeType's path — the anchor the relationship is decided
    /// against.</param>
    private static string? TryGetCommonNamespaceRoot(
        IReadOnlyList<string> expandedQueries, string selfPath)
    {
        string? root = null;
        foreach (var query in expandedQueries)
        {
            var ns = TryGetTokenValue(query, "namespace:");
            if (ns is null) continue;
            if (root is null) { root = ns; continue; }
            if (string.Equals(root, ns, System.StringComparison.Ordinal)) continue;
            if (IsAnchoredFormOf(ns, root, selfPath)) { root = ns; continue; }
            if (IsAnchoredFormOf(root, ns, selfPath)) continue;
            return null;
        }
        return root;
    }

    /// <summary>
    /// <c>true</c> when <paramref name="candidate"/> is exactly what anchoring
    /// <paramref name="authored"/> onto <paramref name="selfPath"/>'s mount produces — the one
    /// relationship under which two spellings name the same root.
    /// </summary>
    private static bool IsAnchoredFormOf(string candidate, string authored, string selfPath) =>
        !string.IsNullOrEmpty(selfPath)
        && !SharesMountRoot(authored, selfPath)
        && string.Equals(
            candidate,
            NodeCompileShaping.AnchorIncludePath(authored, selfPath),
            System.StringComparison.Ordinal);

    /// <summary>
    /// Heuristic path-level matcher over expanded queries: <c>path:X</c> matches the
    /// exact path; <c>namespace:X</c> matches direct children, or the whole subtree
    /// when the query carries <c>scope:subtree</c>/<c>scope:descendants</c>. Used to
    /// classify compiled source paths into source vs. test buckets at release time —
    /// free-text queries don't classify and simply never match.
    /// </summary>
    public static bool Matches(string path, IEnumerable<string> expandedQueries)
    {
        foreach (var query in expandedQueries)
        {
            var exact = TryGetTokenValue(query, "path:");
            if (exact is not null && string.Equals(path, exact, System.StringComparison.Ordinal))
                return true;

            var ns = TryGetTokenValue(query, "namespace:");
            if (ns is null || !path.StartsWith(ns + "/", System.StringComparison.Ordinal))
                continue;
            var subtree = query.Contains("scope:subtree", System.StringComparison.OrdinalIgnoreCase)
                || query.Contains("scope:descendants", System.StringComparison.OrdinalIgnoreCase);
            if (subtree || !path[(ns.Length + 1)..].Contains('/'))
                return true;
        }
        return false;
    }

    private static string? TryGetTokenValue(string query, string key)
    {
        var idx = query.IndexOf(key, System.StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var valueStart = idx + key.Length;
        var valueEnd = valueStart;
        while (valueEnd < query.Length && !char.IsWhiteSpace(query[valueEnd]))
            valueEnd++;
        var value = query[valueStart..valueEnd];
        return value.Length == 0 ? null : value;
    }
}

/// <summary>
/// One named group of Source/Test queries — the unit the GUI renders as a folder in
/// the side-menu source tree. <paramref name="Name"/> comes from the <c>name=</c>
/// prefix (or the default group name); <paramref name="BaseNamespace"/> is the
/// namespace root file paths are shown relative to, when one is determinable.
/// </summary>
public sealed record CodeQueryGroup(
    string Name,
    IReadOnlyList<string> RawQueries,
    IReadOnlyList<string> ExpandedQueries,
    string? BaseNamespace);
