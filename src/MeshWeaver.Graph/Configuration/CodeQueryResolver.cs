using System.Collections.Generic;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Shared query-expansion helpers used by both the compiler (to find which Code
/// nodes should be compiled with a NodeType) and the NodeType Configuration side
/// menu (to list those same files as "Sources" / "Tests"). Centralised here so
/// the listing the user sees in the UI is guaranteed to match the files the
/// runtime actually pulls into the NodeType's assembly.
/// <para>
/// Rules (mirrored from <see cref="NodeTypeDefinition.Sources"/>):
/// </para>
/// <list type="bullet">
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

    /// <summary>
    /// Expands one raw query entry from <see cref="NodeTypeDefinition.Sources"/> or
    /// <see cref="NodeTypeDefinition.Tests"/> into one-or-more concrete mesh query
    /// strings ready for <c>IMeshService.QueryAsync&lt;MeshNode&gt;</c>.
    /// </summary>
    public static IEnumerable<string> Expand(string rawQuery, string selfPath)
    {
        var expanded = rawQuery.Replace("$self", selfPath).Trim();

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
}
