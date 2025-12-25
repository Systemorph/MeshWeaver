namespace MeshWeaver.Mesh.Query;

/// <summary>
/// Abstract base for RSQL AST nodes.
/// </summary>
public abstract record RsqlNode;

/// <summary>
/// A comparison node (leaf node in the AST).
/// </summary>
public record RsqlComparison(RsqlCondition Condition) : RsqlNode;

/// <summary>
/// Logical AND node combining multiple conditions.
/// </summary>
public record RsqlAnd(IReadOnlyList<RsqlNode> Children) : RsqlNode
{
    public RsqlAnd(params RsqlNode[] children) : this((IReadOnlyList<RsqlNode>)children) { }
}

/// <summary>
/// Logical OR node combining multiple conditions.
/// </summary>
public record RsqlOr(IReadOnlyList<RsqlNode> Children) : RsqlNode
{
    public RsqlOr(params RsqlNode[] children) : this((IReadOnlyList<RsqlNode>)children) { }
}
