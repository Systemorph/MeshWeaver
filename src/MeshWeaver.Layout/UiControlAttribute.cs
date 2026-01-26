namespace MeshWeaver.Layout;

[AttributeUsage(AttributeTargets.Property)]
public class UiControlAttribute : Attribute
{
    /// <summary>
    /// Control type for edit mode.
    /// </summary>
    public Type? EditControlType { get; }

    /// <summary>
    /// Control type for display/read mode.
    /// </summary>
    public Type? DisplayControlType { get; }

    /// <summary>
    /// Optional configuration options for the control.
    /// </summary>
    public object? Options { get; set; }

    /// <summary>
    /// When true, this field has its own edit button rather than inline editing.
    /// </summary>
    public bool SeparateEditView { get; set; }

    public UiControlAttribute(Type? editControl = null, Type? displayControl = null)
    {
        EditControlType = editControl;
        DisplayControlType = displayControl;
    }

    /// <summary>
    /// Backward compatibility property. Returns EditControlType or DisplayControlType.
    /// </summary>
    public Type ControlType => EditControlType ?? DisplayControlType
        ?? throw new InvalidOperationException("At least one control type must be specified");
}

/// <summary>
/// Generic attribute for specifying a single control type (backward compatible).
/// </summary>
public class UiControlAttribute<TControl>() : UiControlAttribute(typeof(TControl), null)
    where TControl : UiControl
{
}

/// <summary>
/// Generic attribute for specifying separate edit and display control types.
/// </summary>
public class UiControlAttribute<TEditControl, TDisplayControl>()
    : UiControlAttribute(typeof(TEditControl), typeof(TDisplayControl))
    where TEditControl : UiControl
    where TDisplayControl : UiControl
{
}

