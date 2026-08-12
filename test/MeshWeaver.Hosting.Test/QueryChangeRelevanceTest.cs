using System.Collections.Generic;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The reactive change-feed RELEVANCE test — "does this CRUD broadcast event fit the query, so we should
/// re-run it?" (<see cref="PathMatcher.ShouldNotifyForQuery"/>). Pins the fix for the "open threads"
/// catalog not refreshing on delete.
///
/// <para><b>The bug.</b> The catalog queries by NAMESPACE — <c>namespace:{owner}/*_Thread</c> with NO
/// <c>path:</c> term — so its base path is empty and the old path-only <see cref="PathMatcher.ShouldNotify"/>
/// degraded to "direct children of root". A thread deleted three levels deep at
/// <c>{owner}/_Thread/{id}</c> was judged out of scope → no re-query → the catalog kept showing the
/// deleted thread. <see cref="PathMatcher.ShouldNotifyForQuery"/> also matches the changed node's
/// namespace against the query namespaces, so create / update / delete of a thread all refresh it.</para>
///
/// <para>Pure logic (path + parsed query), so every CRUD case is a unit test — no Postgres, no change
/// feed. The PG provider only supplies the path in its broadcast events (pg_notify carries the key, not
/// the row), which is exactly what this test drives.</para>
/// </summary>
public class QueryChangeRelevanceTest
{
    private static readonly QueryParser Parser = new();
    private const string Owner = "rbuergi";
    private const string ThreadPath = Owner + "/_Thread/do-you-have-guardrails-6b4c";

    // The exact "open threads" catalog query (UserActivityLayoutAreas.BuildOpenThreads).
    private static (string BasePath, QueryScope Scope, IReadOnlyList<string> Namespaces) CatalogFilter()
    {
        var pq = Parser.Parse($"namespace:{Owner}/*_Thread nodeType:Thread -content.status:Done sort:LastModified-desc");
        // Namespace-only query → no path: term → empty base path (mirrors PostgreSqlMeshQuery's derivation).
        return (pq.Path ?? "", pq.Scope, pq.ExtractNamespacePatterns());
    }

    private static bool InScope(DataChangeNotification change)
    {
        var (basePath, scope, namespaces) = CatalogFilter();
        return PathMatcher.ShouldNotifyForQuery(change.Path, basePath, scope, namespaces);
    }

    // ── All CRUD, in-scope (a thread under {owner}/_Thread) → must refresh the catalog ──

    [Fact]
    public void Created_thread_refreshes_catalog()
        => Assert.True(InScope(DataChangeNotification.Created(ThreadPath, entity: null)));

    [Fact]
    public void Updated_thread_refreshes_catalog() // e.g. status → Done: re-query then drops it (leave-scope)
        => Assert.True(InScope(DataChangeNotification.Updated(ThreadPath, entity: null)));

    [Fact]
    public void Deleted_thread_refreshes_catalog() // ← the reported bug
        => Assert.True(InScope(DataChangeNotification.Deleted(ThreadPath)));

    // ── Out of scope → must NOT trigger a redundant re-query ──

    [Fact]
    public void Change_to_non_thread_node_same_partition_is_out_of_scope()
        => Assert.False(InScope(DataChangeNotification.Updated($"{Owner}/Docs/readme", entity: null)));

    [Fact]
    public void Delete_of_non_thread_node_same_partition_is_out_of_scope()
        => Assert.False(InScope(DataChangeNotification.Deleted($"{Owner}/Docs/readme")));

    [Fact]
    public void Change_in_a_different_partition_is_out_of_scope()
        => Assert.False(InScope(DataChangeNotification.Deleted("acme/_Thread/other")));

    // ── The namespace glob directly ──

    [Fact]
    public void Namespace_glob_matches_satellite_thread_namespace()
        => Assert.True(PathMatcher.NamespaceInScope($"{Owner}/_Thread", new[] { $"{Owner}/*_Thread" }));

    [Fact]
    public void Namespace_glob_rejects_non_matching_namespace()
        => Assert.False(PathMatcher.NamespaceInScope($"{Owner}/Docs", new[] { $"{Owner}/*_Thread" }));

    [Fact]
    public void NamespaceOf_strips_the_last_segment()
        => Assert.Equal($"{Owner}/_Thread", PathMatcher.NamespaceOf(ThreadPath));

    /// <summary>
    /// 🚨 THE VOCABULARY CONTRACT (#1235), pinned as a ROUND TRIP rather than a hardcoded string:
    /// whatever <see cref="QueryParser"/> emits for a wildcard namespace, this matcher must
    /// understand. Asserting the pattern's spelling and the matcher's behaviour separately is what
    /// let them diverge — this used to read
    /// <c>NamespaceInScope("{Owner}/_Thread", ["{Owner}/%_Thread"])</c> with the comment "parser
    /// rewrites * → SQL-LIKE %", so it kept passing on a `%` the parser no longer produces while
    /// the real query path matched nothing.
    /// </summary>
    [Fact]
    public void Namespace_glob_matches_whatever_the_parser_actually_emits()
    {
        var patterns = CatalogFilter().Namespaces;

        Assert.Contains($"{Owner}/*_Thread", patterns); // `*` is the one vocabulary — never SQL's `%`
        Assert.True(PathMatcher.NamespaceInScope($"{Owner}/_Thread", patterns));
    }

    /// <summary>
    /// <c>%</c> is a LITERAL here, not a wildcard. The matcher used to accept both spellings to
    /// compensate for the parser's rewrite; tolerating both is precisely what hid the fork, so a
    /// re-introduced `%` must fail loudly instead of quietly working in memory only.
    /// </summary>
    [Fact]
    public void Namespace_glob_does_not_treat_percent_as_a_wildcard()
        => Assert.False(PathMatcher.NamespaceInScope($"{Owner}/_Thread", new[] { $"{Owner}/%_Thread" }));

    /// <summary>
    /// The multi-wildcard pattern <c>scope:subtree</c> produces (#1232) must reach a namespace
    /// nested below the matched one — the arm that silently never fired while <c>GlobMatch</c>
    /// split on the first wildcard only.
    /// </summary>
    [Fact]
    public void Namespace_glob_subtree_pair_reaches_a_nested_namespace()
    {
        var patterns = Parser.Parse("namespace:*/Source scope:subtree nodeType:Code")
            .ExtractNamespacePatterns();

        Assert.True(PathMatcher.NamespaceInScope("acme/SampleData/Source", patterns));
        Assert.True(PathMatcher.NamespaceInScope("acme/SampleData/Source/Fixtures", patterns));
        Assert.False(PathMatcher.NamespaceInScope("acme/SampleData/Other", patterns));
    }
}
