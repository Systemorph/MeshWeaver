namespace MeshWeaver.AI;

/// <summary>
/// Reference to a thread message cell, stored in the Thread hub's DataSource.
/// Used to track which message nodes exist and their order.
/// The layout area subscribes to GetStream&lt;ThreadCellReference&gt;() and maps
/// these to LayoutAreaControl cells for rendering.
/// </summary>
public record ThreadCellReference
{
    /// <summary>Full mesh path of the referenced thread message node.</summary>
    public required string Path { get; init; }
    /// <summary>Zero-based position of this cell within the thread, used for ordered rendering.</summary>
    public int Order { get; init; }
}
