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
    /// Both are emitted — never one instead of the other — for the same reason
    /// <c>NodeCompileShaping.ResolveCodeIncludes</c> tries the anchored include path FIRST and
    /// keeps the authored one as a fallback: which spelling resolves is a property of the mount,
    /// not of the entry, and a query that matches nothing costs a query and changes no result.
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
    /// <para><b>What it deliberately does NOT touch.</b> Anchoring is a no-op unless the value's
    /// first segment appears in <paramref name="selfPath"/> BELOW the root, so: a root-mounted type
    /// keeps behaving exactly as before (the whole reason the samples work in the Monolith); a
    /// genuinely cross-partition reference stays verbatim (<c>shared=@Store/Core/Source</c> read
    /// from <c>rbuergi/OperationRequest</c> — the #3903 shape — has no <c>Store</c> segment to
    /// anchor to); and an already-absolute reference is never double-prefixed, because its first
    /// segment is the mount root itself, which sits at index 0 and is excluded.</para>
    /// </summary>
    /// <param name="query">One fully expanded query, as <see cref="Expand"/> emits it.</param>
    /// <param name="selfPath">The owning NodeType's mesh path — the mount anchor.</param>
    internal static string? AnchorToMount(string query, string selfPath)
    {
        if (string.IsNullOrEmpty(selfPath)) return null;

        var rewritten = query;
        foreach (var key in MountAnchoredKeys)
        {
            if (TryGetTokenValue(rewritten, key) is not { } value) continue;
            var anchored = NodeCompileShaping.AnchorIncludePath(value, selfPath);
            if (string.Equals(anchored, value, System.StringComparison.Ordinal)) continue;
            rewritten = ReplaceTokenValue(rewritten, key, anchored);
        }

        return string.Equals(rewritten, query, System.StringComparison.Ordinal) ? null : rewritten;
    }

    /// <summary>The query tokens whose value names a mesh location and is therefore mount-relative.
    /// An immutable lookup initialised once and never written — <c>NoStaticState.md</c>'s sanctioned
    /// <c>static readonly</c>.</summary>
    private static readonly string[] MountAnchoredKeys = ["path:", "namespace:"];

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
                TryGetCommonNamespaceRoot(expandedByName[name])));
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
    /// </summary>
    private static string? TryGetCommonNamespaceRoot(IReadOnlyList<string> expandedQueries)
    {
        string? root = null;
        foreach (var query in expandedQueries)
        {
            var ns = TryGetTokenValue(query, "namespace:");
            if (ns is null) continue;
            if (root is null) { root = ns; continue; }
            if (string.Equals(root, ns, System.StringComparison.Ordinal)) continue;
            if (IsMountAnchoredForm(ns, root)) { root = ns; continue; }
            if (IsMountAnchoredForm(root, ns)) continue;
            return null;
        }
        return root;
    }

    /// <summary>
    /// <c>true</c> when <paramref name="candidate"/> is <paramref name="authored"/> prefixed by a
    /// mount — i.e. the authored value with whole segments in front of it. The segment-boundary
    /// check is what keeps <c>Northwind/Source</c> from reading as the anchored form of
    /// <c>wind/Source</c>.
    /// </summary>
    private static bool IsMountAnchoredForm(string candidate, string authored) =>
        candidate.Length > authored.Length + 1
        && candidate.EndsWith(authored, System.StringComparison.Ordinal)
        && candidate[candidate.Length - authored.Length - 1] == '/';

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
