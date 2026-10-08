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
    /// Stretches each child across the container's cross axis (<c>align-items: stretch</c>). This is
    /// the default for a vertical <see cref="StackControl"/>: a child is as wide as its column, so a
    /// markdown table wider than the column wraps or scrolls inside it instead of being clipped.
    /// Serialised by NAME, so it reaches the client's own <c>Stretch</c> member. On the main axis of
    /// a horizontal stack it behaves as start.
    /// </summary>
    Stretch
}
