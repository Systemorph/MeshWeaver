using System.Collections.Immutable;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Decides whether a committed change can affect a live query's result, from the query's
/// <c>nodeType:</c> constraint and the node types the change touched. Pure.
///
/// <para><b>Why this exists.</b> A live query in <see cref="StorageAdapterMeshQueryProvider"/>
/// re-runs its whole read on every change under its scope, and the scope test
/// (<see cref="PathMatcher.ShouldNotify"/>) looks at the PATH alone. The security fold keeps
/// three such queries open for the life of the mesh: every partition's
/// <c>path:{p} scope:descendants nodeType:AccessAssignment</c> and <c>… id:_Policy
/// nodeType:PartitionAccessPolicy</c>, and the mesh-wide <c>nodeType:GroupMembership
/// partitions:all scope:subtree</c>. So EVERY write re-walked the whole mesh at least once, even
/// a Markdown node that none of them can ever return, and the cost of one write grew with the
/// size of the mesh. The plugin gate's in-memory mesh measured it: 0.003 s per node for the first
/// package and 0.7–1.1 s per node for the last, which put a late-installed <c>Hosting</c> past the
/// installer's 600 s bound (Doc/Architecture/LiveQueryRequeryCost).</para>
///
/// <para><b>The rule.</b> A change CANNOT affect the result when every query of the request
/// requires a node type in a fixed set (a conjunctive <c>nodeType:</c> equality or membership),
/// and neither the type the path now holds nor the type it held before is in that set: the node
/// was not a result row before the change and is not one after it. Anything the rule cannot prove
/// — an unconstrained query, a disjunction, an unknown prior state, an unknown type, the
/// frontier scope, a joined change feed — is RELEVANT, so the worst case is the re-query that
/// already happens today, never a missed change.</para>
///
/// <para>🚨 Only for a read that is filtered by nothing: the RAW surface (<c>IMeshQueryCore</c>), and a
/// SECURED read answered as System in a mesh whose every Read validator admits System unconditionally
/// (<see cref="ISystemReadTransparentNodeValidator"/>), which is the raw read by construction. Any
/// other row-level-security filtered query's result also depends on grants: an
/// <c>AccessAssignment</c> written under the scope can make OTHER rows visible, so its type says
/// nothing about whether the filtered result moved.</para>
/// </summary>
public static class NodeTypeChangeRelevance
{
    /// <summary>
    /// The node types <paramref name="query"/> REQUIRES — the values of a <c>nodeType:</c> equality
    /// or membership that every result row must satisfy — or <see langword="null"/> when the query
    /// does not confine rows to a fixed set of types.
    /// </summary>
    /// <param name="query">The parsed query.</param>
    /// <returns>The required types, or null when the query admits any type.</returns>
    public static ImmutableHashSet<string>? RequiredNodeTypes(ParsedQuery query)
        => query.Filter is null ? null : Required(query.Filter);

    private static ImmutableHashSet<string>? Required(QueryNode node) => node switch
    {
        QueryComparison c when c.Condition.Selector.Equals("nodeType", StringComparison.OrdinalIgnoreCase)
            && c.Condition.Operator is QueryOperator.Equal or QueryOperator.In
            && c.Condition.Values is { Length: > 0 } values
            && values.All(v => !string.IsNullOrEmpty(v) && !v.Contains('*'))
            => values.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
        // A conjunction confines rows as soon as ONE conjunct does. A disjunction (or anything
        // else) is not reasoned about: the query is treated as admitting any type.
        QueryAnd and => and.Children.Select(Required).FirstOrDefault(set => set is not null),
        _ => null,
    };

    /// <summary>
    /// The combined type set of a multi-query request, or <see langword="null"/> when ANY of the
    /// queries is unconstrained, the frontier scope, or not a plain node read — a change then
    /// always counts as relevant.
    /// </summary>
    /// <param name="queries">The request's parsed queries.</param>
    /// <returns>The union of the required types, or null.</returns>
    public static ImmutableHashSet<string>? RequiredNodeTypes(IEnumerable<ParsedQuery> queries)
    {
        var union = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in queries)
        {
            // NextLevel's frontier depends on which levels are POPULATED, by any node of any type;
            // a joined change feed's rows are decided by its satellite, not by the main node's type.
            if (query.Scope == QueryScope.NextLevel || query.Source != QuerySource.Default)
                return null;
            if (RequiredNodeTypes(query) is not { } required)
                return null;
            union = union.Union(required);
        }
        return union.IsEmpty ? null : union;
    }

    /// <summary>
    /// Whether <paramref name="change"/> can affect the result of a query that requires a node
    /// type in <paramref name="requiredNodeTypes"/>. True whenever it cannot be proven otherwise.
    /// </summary>
    /// <param name="change">The committed change.</param>
    /// <param name="requiredNodeTypes">From <see cref="RequiredNodeTypes(IEnumerable{ParsedQuery})"/>;
    /// null means the query admits any type.</param>
    /// <returns>False only when the change provably touches no row the query could return.</returns>
    public static bool CanAffect(DataChangeNotification change, ImmutableHashSet<string>? requiredNodeTypes)
    {
        if (requiredNodeTypes is null || !change.PriorStateKnown)
            return true;
        // The type the path holds AFTER the change. A delete leaves nothing behind, so only the
        // prior state matters; any other change must carry the type it arrives with.
        if (change.Kind != DataChangeKind.Deleted)
        {
            if (string.IsNullOrEmpty(change.NodeType))
                return true;
            if (requiredNodeTypes.Contains(change.NodeType))
                return true;
        }
        return change.PreviousNodeType is { } previous && requiredNodeTypes.Contains(previous);
    }
}
