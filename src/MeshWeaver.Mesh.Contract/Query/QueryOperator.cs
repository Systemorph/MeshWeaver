namespace MeshWeaver.Mesh.Query;

/// <summary>
/// Query comparison operators (GitHub-style syntax).
/// </summary>
public enum QueryOperator
{
    /// <summary>Equals (field:value)</summary>
    Equal,

    /// <summary>Not equals (-field:value)</summary>
    NotEqual,

    /// <summary>Greater than (field:>value)</summary>
    GreaterThan,

    /// <summary>Less than (field:&lt;value)</summary>
    LessThan,

    /// <summary>Greater than or equal (field:>=value)</summary>
    GreaterOrEqual,

    /// <summary>Less than or equal (field:&lt;=value)</summary>
    LessOrEqual,

    /// <summary>In list (field:(A OR B OR C))</summary>
    In,

    /// <summary>Not in list (-field:(A OR B))</summary>
    NotIn,

    /// <summary>Wildcard pattern (field:*value*)</summary>
    Like
}
