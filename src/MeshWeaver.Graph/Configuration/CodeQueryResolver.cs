using System.Collections.Generic;
using System.Text.RegularExpressions;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Shared query-expansion helpers used by both the compiler (to find which Code
/// nodes should be compiled with a NodeType) and the NodeType side menu (to list
/// those same files as "Sources" / "Tests"). Centralised here so the listing the
/// user sees in the UI is guaranteed to match the files the runtime actually
/// pulls into the NodeType's assembly.
/// <para>
/// Rules (mirrored from <see cref="NodeTypeDefinition.Sources"/>):
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
///   <item>Every emitted query is ANDed with <c>nodeType:Code</c> so non-code
///     children never leak in.</item>
/// </list>
/// </summary>
public static class CodeQueryResolver
{
    /// <summary>
    /// Default Source query when a NodeType doesn't declare <see cref="NodeTypeDefinition.Sources"/>.
    /// Resolves to <c>{NodeTypePath}/Source</c> subtree.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSources =
        [$"namespace:{CodeNodeType.SourceSubNamespace} scope:subtree"];

    /// <summary>
    /// Default Test query when a NodeType doesn't declare <see cref="NodeTypeDefinition.Tests"/>.
    /// Resolves to <c>{NodeTypePath}/Test</c> subtree. Mirrors <see cref="DefaultSources"/>
    /// so the side-menu Sources/Tests split matches the convention.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultTests =
        [$"namespace:{CodeNodeType.TestSubNamespace} scope:subtree"];

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
    /// Expands one raw query entry from <see cref="NodeTypeDefinition.Sources"/> or
    /// <see cref="NodeTypeDefinition.Tests"/> into one-or-more concrete mesh query
    /// strings ready for <c>IMeshService.QueryAsync&lt;MeshNode&gt;</c>. An optional
    /// <c>name=</c> prefix is stripped first — the compiler ignores grouping.
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
                yield return WithCodeTypeFilter(stripped);
                yield break;
            }
            yield return WithCodeTypeFilter($"path:{stripped}");
            yield return WithCodeTypeFilter($"namespace:{stripped} scope:subtree");
            yield break;
        }

        yield return WithCodeTypeFilter(RebaseRelativeNamespace(expanded, selfPath));
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
            : $"{query} nodeType:{CodeNodeType.NodeType}";

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
    /// </summary>
    private static string? TryGetCommonNamespaceRoot(IReadOnlyList<string> expandedQueries)
    {
        string? root = null;
        foreach (var query in expandedQueries)
        {
            var ns = TryGetTokenValue(query, "namespace:");
            if (ns is null) continue;
            if (root is null) root = ns;
            else if (!string.Equals(root, ns, System.StringComparison.Ordinal)) return null;
        }
        return root;
    }

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
