namespace MeshWeaver.Mesh.Services;

/// <summary>
/// What <see cref="NodeTypeResolution.Resolve"/> established: whether the type resolves, and —
/// when it does not because something ELSE is sitting at its path — what that something is.
///
/// <para>🚨 <b>The two negatives are deliberately distinct.</b> "Nothing is there" is fixed by
/// creating the type; "the path is taken" is not, and telling a caller to create a node that is
/// already sitting at that path is how Systemorph/MeshWeaver#2231 read as a lookup-ordering puzzle
/// for a month. A boundary that collapses them hands the reader the wrong remedy.</para>
/// </summary>
/// <param name="Resolves">True when a NodeType is registered at the path.</param>
/// <param name="Occupant">The node in the way, as
/// <see cref="INodeTypeDeclarationProbe.DescribeNonDeclaration"/> describes it; always
/// <c>null</c> when <paramref name="Resolves"/> is true.</param>
public readonly record struct NodeTypeVerdict(bool Resolves, string? Occupant)
{
    /// <summary>A NodeType is registered at the path.</summary>
    public static readonly NodeTypeVerdict Registered = new(true, null);

    /// <summary>Nothing at all is at the path — creating the type is the remedy.</summary>
    public static readonly NodeTypeVerdict Absent = new(false, null);
}
