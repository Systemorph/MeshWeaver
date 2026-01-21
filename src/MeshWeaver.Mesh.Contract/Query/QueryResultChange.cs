namespace MeshWeaver.Mesh;

/// <summary>
/// Represents a change in query results for observable queries.
/// </summary>
/// <typeparam name="T">The type of items in the result set.</typeparam>
public record QueryResultChange<T>
{
    /// <summary>
    /// The type of change that occurred.
    /// </summary>
    public QueryChangeType ChangeType { get; init; }

    /// <summary>
    /// The items affected by this change.
    /// For Initial/Reset: all matching items.
    /// For Added/Updated/Removed: only the changed items.
    /// </summary>
    public IReadOnlyList<T> Items { get; init; } = [];

    /// <summary>
    /// The original query that produced this change.
    /// </summary>
    public ParsedQuery Query { get; init; } = null!;

    /// <summary>
    /// Monotonically increasing version number for ordering changes.
    /// </summary>
    public long Version { get; init; }

    /// <summary>
    /// Timestamp when this change was detected.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Types of changes that can occur in a query result set.
/// </summary>
public enum QueryChangeType
{
    /// <summary>Initial result set when subscription starts.</summary>
    Initial,

    /// <summary>New items were added that match the query.</summary>
    Added,

    /// <summary>Existing items were updated (still match the query).</summary>
    Updated,

    /// <summary>Items were removed or no longer match the query.</summary>
    Removed,

    /// <summary>Full reset - treat as new initial result set (e.g., after reconnection).</summary>
    Reset
}
