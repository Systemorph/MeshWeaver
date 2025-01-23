namespace MeshWeaver.Layout;

/// <summary>
/// Represents a Group of radio buttons.
/// For more information, see <see href="https://www.fluentui-blazor.net/RadioGroup"/>.
/// </summary>
public record RadioGroupControl : ListControlBase<RadioGroupControl>
{

    /// <summary>
    /// Represents a Group of radio buttons.
    /// For more information, see <see href="https://www.fluentui-blazor.net/RadioGroup"/>.
    /// </summary>
    public RadioGroupControl(object Data, object Options, object Type) : base(Data, Options)
    {
        this.Type = Type;
        this.Options = Options;
    }

    /// <summary>
    /// Type of the property of the radio control group.
    /// </summary>
    public object Type { get; init; }

    /// <summary>
    /// Gets or initializes the name of the radio group.
    /// </summary>
    public object Name { get; init; }

    /// <summary>
    /// Sets the name of the radio group.
    /// </summary>
    /// <param name="name">The name to set.</param>
    /// <returns>A new instance of the skin with the specified name.</returns>
    public RadioGroupControl WithName(object name) => this with { Name = name };
    /// <summary>
    /// Gets or sets the orientation of the toolbar.
    /// </summary>
    public object Orientation { get; init; } = Layout.Orientation.Horizontal;

    /// <summary>
    /// Sets the orientation of the toolbar.
    /// </summary>
    /// <param name="orientation">The orientation to set.</param>
    public RadioGroupControl WithOrientation(object orientation) => this with { Orientation = orientation };


}


