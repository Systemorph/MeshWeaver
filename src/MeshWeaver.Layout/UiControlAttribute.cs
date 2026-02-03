namespace MeshWeaver.Layout;

[AttributeUsage(AttributeTargets.Property)]
public class UiControlAttribute(Type? displayControl = null, Type? editControl = null) : Attribute
{
    /// <summary>
    /// Control type for edit mode.
    /// </summary>
    public Type? EditControlType { get; } = editControl;

    /// <summary>
    /// Control type for display/read mode.
    /// </summary>
    public Type? DisplayControlType { get; } = displayControl;

    /// <summary>
    /// Optional configuration options for the control.
    /// </summary>
    public object? Options { get; set; }

    /// <summary>
    /// When true, this field has its own edit button rather than inline editing.
    /// </summary>
    public bool SeparateEditView { get; set; }

    /// <summary>
    /// CSS style to apply to the control container.
    /// Example: "width: 100%;" for full-width fields.
    /// </summary>
    public string? Style { get; set; }

    /// <summary>
    /// Backward compatibility property. Returns EditControlType or DisplayControlType.
    /// </summary>
    public Type ControlType => EditControlType ?? DisplayControlType
        ?? throw new InvalidOperationException("At least one control type must be specified");
}

/// <summary>
/// Generic attribute for specifying a single control type (backward compatible).
/// </summary>
public class UiControlAttribute<TControl>() : UiControlAttribute(null, typeof(TControl))
    where TControl : UiControl
{
}

/// <summary>
/// Generic attribute for specifying separate edit and display control types.
/// </summary>
public class UiControlAttribute<TEditControl, TDisplayControl>()
    : UiControlAttribute(typeof(TDisplayControl), typeof(TEditControl))
    where TEditControl : UiControl
    where TDisplayControl : UiControl
{
}

