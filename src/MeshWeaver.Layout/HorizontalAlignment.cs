namespace MeshWeaver.Layout;

/// <summary>
/// Specifies the horizontal alignment options for a control.
/// </summary>
public enum HorizontalAlignment
{
    /// <summary>
    /// Aligns the content to the left.
    /// </summary>
    Left,

    /// <summary>
    /// Centers the content.
    /// </summary>
    Center,

    /// <summary>
    /// Aligns the content to the right.
    /// </summary>
    Right,

    /// <summary>
    /// Aligns the content to the start of the container.
    /// </summary>
    Start,

    /// <summary>
    /// Aligns the content to the end of the container.
    /// </summary>
    End,

    /// <summary>
    /// Stretches each child across the container's cross axis (<c>align-items: stretch</c>): in a
    /// vertical <see cref="StackControl"/> every child is as wide as the column. An explicit opt-in —
    /// a stack's unset alignment stays the client default (start). Use it where a child has no width
    /// of its own to fall back on, e.g. a markdown body whose table is wider than the column, which
    /// under start alignment takes the table's max-content width and is clipped (#6036). Serialised
    /// by NAME, so it reaches the client's own <c>Stretch</c> member. On the main axis of a horizontal
    /// stack it behaves as start.
    /// </summary>
    Stretch
}
