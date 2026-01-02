namespace MeshWeaver.Mesh.Query;

/// <summary>
/// Abstract base for query AST nodes.
/// </summary>
public abstract record QueryNode;

/// <summary>
/// A comparison node (leaf node in the AST).
/// </summary>
public record QueryComparison(QueryCondition Condition) : QueryNode;

/// <summary>
/// Logical AND node combining multiple conditions (space-separated in GitHub syntax).
/// </summary>
public record QueryAnd(IReadOnlyList<QueryNode> Children) : QueryNode
{
    public QueryAnd(params QueryNode[] children) : this((IReadOnlyList<QueryNode>)children) { }
}

/// <summary>
/// Logical OR node combining multiple conditions (OR keyword in GitHub syntax).
/// </summary>
public record QueryOr(IReadOnlyList<QueryNode> Children) : QueryNode
{
    public QueryOr(params QueryNode[] children) : this((IReadOnlyList<QueryNode>)children) { }
}
