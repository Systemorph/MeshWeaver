namespace MeshWeaver.Data;

/// <summary>
/// Options controlling how a data update is applied to a workspace.
/// </summary>
public record UpdateOptions
{
    /// <summary>The default options (no snapshot).</summary>
    public static UpdateOptions Default { get; } = new();
    /// <summary>
    /// When true, the update replaces the collection's contents entirely (snapshot semantics)
    /// rather than merging into existing instances.
    /// </summary>
    public bool Snapshot { get; init; }
    /// <summary>Returns a copy with <see cref="Snapshot"/> set to the given value.</summary>
    /// <param name="snapshot">Whether to enable snapshot semantics.</param>
    /// <returns>The updated options.</returns>
    public UpdateOptions EnableSnapshot(bool snapshot = true) => this with {Snapshot = snapshot};
}
