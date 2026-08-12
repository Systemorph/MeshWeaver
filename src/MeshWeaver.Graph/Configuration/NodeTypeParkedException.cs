namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// A NodeType is in <see cref="NodeTypeCompileParkRegistry"/>'s terminal PARKED state: its compile
/// failed on a real source error and is deliberately not being retried until the source changes.
///
/// <para>Raised by the enrichment wait INSTEAD of sitting out a no-progress budget that cannot
/// possibly be beaten — parking is exactly the guarantee that no further compile will be
/// dispatched. Carrying its own type matters for the operator-facing copy: a parked type is a
/// <see cref="NodeTypeEnrichmentHelpers.OverlayCause.CompileFailed"/>, where a code fix IS the
/// remedy, whereas a budget expiry maps to "no code change needed, it will self-heal". Those two
/// messages send an operator in opposite directions, and before this existed the parked case
/// rendered the wrong one.</para>
/// </summary>
public sealed class NodeTypeParkedException(string nodeTypePath, string? parkedError)
    : InvalidOperationException(
        $"NodeType '{nodeTypePath}' is PARKED after a compile failure and is not being retried "
        + $"until its source changes. {parkedError ?? "(no error text recorded)"}")
{
    /// <summary>Path of the parked NodeType.</summary>
    public string NodeTypePath { get; } = nodeTypePath;

    /// <summary>The compile error recorded when the type parked, when one was captured.</summary>
    public string? ParkedError { get; } = parkedError;
}
