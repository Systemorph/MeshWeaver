namespace MeshWeaver.Layout;

/// <summary>
/// Represents a select control with customizable properties.
/// </summary>
/// <param name="Data">The data associated with the select control.</param>
public record SelectControl(object Data) : ListControlBase<SelectControl>(Data), IListControl
{
    /// <summary>
    /// Gets or initializes the position of the select control.
    /// </summary>
    public SelectPosition? Position { get; init; }

    /// <summary>
    /// Sets the position of the select control.
    /// </summary>
    /// <param name="position">The position to set.</param>
    /// <returns>A new <see cref="SelectControl"/> instance with the specified position.</returns>
    public SelectControl WithPosition(SelectPosition position) => this with { Position = position };
}
