namespace MeshWeaver.Mesh.Query;

/// <summary>
/// RSQL/FIQL comparison operators.
/// </summary>
public enum RsqlOperator
{
    /// <summary>Equals (==)</summary>
    Equal,

    /// <summary>Not equals (!=)</summary>
    NotEqual,

    /// <summary>Greater than (=gt=)</summary>
    GreaterThan,

    /// <summary>Less than (=lt=)</summary>
    LessThan,

    /// <summary>Greater than or equal (=ge=)</summary>
    GreaterOrEqual,

    /// <summary>Less than or equal (=le=)</summary>
    LessOrEqual,

    /// <summary>In list (=in=)</summary>
    In,

    /// <summary>Not in list (=out=)</summary>
    NotIn,

    /// <summary>Contains/wildcard pattern (=like=)</summary>
    Like
}
