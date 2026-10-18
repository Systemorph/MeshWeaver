namespace MeshWeaver.Mesh;

/// <summary>
/// <c>sort:subtreeLastModified</c> — order nodes by the newest change ANYWHERE BELOW them ("last
/// activity"), so a declared query can list containers (client partition roots, project folders)
/// most-recently-worked-on first.
///
/// <para><b>The key</b> of a node <c>N</c> is the <see cref="MeshNode.LastModified"/> of the newest
/// row the SAME caller gets back from <c>namespace:N scope:descendants sort:lastModified-desc</c>
/// — its strict descendants, as that caller's own descendants query returns them. When the caller
/// sees no descendant, the key falls back to <c>N</c>'s own <see cref="MeshNode.LastModified"/>, so
/// an empty container ranks by when it was itself last touched rather than as never touched. A
/// node with neither (an unset stamp) has no key and sorts LAST in both directions, like every
/// other <c>sort:</c> selector.</para>
///
/// <para><b>Why strict descendants, not the node itself.</b> A container's own stamp is moved by
/// the platform as often as by people — a retype, a provisioning pass or a sync re-stamps a root
/// without anyone working under it — so a root re-stamped yesterday would outrank a client worked
/// on this morning. Its own stamp is used only when there is nothing under it to read.</para>
///
/// <para><b>Access.</b> The descendants read runs under the caller's identity through the normal
/// query path, so every backend applies its own row-level security to it: a timestamp can only
/// come from a row the caller could have found with that query themselves. Nothing is read as
/// System on the caller's behalf.</para>
///
/// <para>It is computed by the query engine (<c>MeshQuery</c>), not by any one storage backend: the
/// engine enumerates the candidates WITHOUT the sort and the window, reads one key per candidate
/// (bounded concurrency), orders by <c>QuerySortKey</c>'s contract, and only then applies
/// <c>skip</c>/<c>limit</c>. Cost is one indexed <c>limit:1</c> read per candidate, which is why
/// it is meant for listing containers and refuses a candidate set larger than
/// <c>MeshOperationOptions.SubtreeSortMaxCandidates</c>. Full account:
/// <c>Doc/DataMesh/QuerySyntax</c> → <c>sort</c>.</para>
/// </summary>
public static class SubtreeSort
{
    /// <summary>The selector: <c>sort:subtreeLastModified</c> / <c>sort:subtreeLastModified-desc</c>.</summary>
    public const string SubtreeLastModified = "subtreeLastModified";

    /// <summary>Whether <paramref name="orderBy"/> asks for the subtree key.</summary>
    /// <param name="orderBy">The parsed ordering, or null.</param>
    /// <returns>True for <see cref="SubtreeLastModified"/> in either direction (case-insensitive).</returns>
    public static bool Applies(OrderByClause? orderBy)
        => orderBy is not null
           && string.Equals(orderBy.Property, SubtreeLastModified, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="query"/> without ANY <c>sort:</c> term — the CANDIDATE query. No backend can
    /// order by the key, so it must not clip by an order it cannot compute; the engine orders.
    ///
    /// <para>🚨 Cut by the parser's own tokenizer (<see cref="QueryParser.RemoveQualifier"/>), never
    /// a pattern: <c>sort:"subtreeLastModified-desc"</c> parses to the same ordering, and a regex
    /// that missed the quoted spelling left the candidate query asking for the subtree sort again —
    /// an engine that re-entered itself until the stack overflowed. Every <c>sort:</c> goes, so the
    /// candidate query provably has no ordering of its own. Every other byte is kept verbatim
    /// (<c>name:"A  B"</c> keeps both spaces).</para>
    /// </summary>
    /// <param name="query">A query string.</param>
    /// <returns>The same query with its <c>sort:</c> terms removed.</returns>
    public static string WithoutSort(string query)
        => new QueryParser().RemoveQualifier(query, "sort");

    /// <summary>
    /// The read whose newest row is the key of <paramref name="path"/>: its strict descendants,
    /// newest first, one row.
    /// </summary>
    /// <param name="path">The candidate node's path.</param>
    /// <returns>The descendants query string.</returns>
    /// <remarks>The path is written as a quoted LITERAL (<see cref="QueryParser.QuoteLiteral"/>):
    /// spliced bare, <c>acme/Project Files</c> split into a namespace and a free-text term, and a
    /// <c>|</c> or <c>*</c> in a path turned the read into an alternation or a glob — a key read over
    /// a different set of rows than the node's own descendants.</remarks>
    public static string NewestDescendantQuery(string path)
        => $"namespace:{QueryParser.QuoteLiteral(path)} scope:descendants sort:lastModified-desc limit:1";
}
