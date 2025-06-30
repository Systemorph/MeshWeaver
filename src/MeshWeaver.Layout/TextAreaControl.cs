namespace MeshWeaver.Layout;

/// <summary>
/// Represents a text area control with customizable properties.
/// </summary>
public record TextAreaControl : InputFormControlBase<TextAreaControl>
{
    /// <summary>
    /// Represents a text area control with customizable properties.
    /// </summary>
    /// <param name="Data">The data associated with the text area control.</param>
    public TextAreaControl(object Data) : base(Data)
    {
        Style = "width:100%;";
    }

    /// <summary>
    /// Gets or initializes the number of rows for the text area control.
    /// </summary>
    public object Rows { get; init; }

    /// <summary>
    /// Gets or initializes the number of columns for the text area control.
    /// </summary>
    public object Cols { get; init; }

    /// <summary>
    /// Use TextAreaResize for values. Controls the resizability of text areas
    /// </summary>
    public object Resize { get; init; }


    /// <summary>
    /// Sets the number of rows for the text area control.
    /// </summary>
    /// <param name="rows">The number of rows to set.</param>
    /// <returns>A new <see cref="TextAreaControl"/> instance with the specified number of rows.</returns>
    public TextAreaControl WithRows(object rows) => this with { Rows = rows };

    /// <summary>
    /// Sets the number of columns for the text area control.
    /// </summary>
    /// <param name="cols">The number of columns to set.</param>
    /// <returns>A new <see cref="TextAreaControl"/> instance with the specified number of columns.</returns>
    public TextAreaControl WithCols(object cols) => this with { Cols = cols };

    /// <summary>
    /// Sets whether the text area should automatically resize to fit content.
    /// </summary>
    /// <param name="autoresize">Whether to enable auto-resize.</param>
    /// <returns>A new <see cref="TextAreaControl"/> instance with the specified auto-resize setting.</returns>
    public TextAreaControl WithAutoresize(object autoresize) => this with { Resize = autoresize };

}

public enum TextAreaResize
{
    Horizontal,
    Vertical,
    Both
}
