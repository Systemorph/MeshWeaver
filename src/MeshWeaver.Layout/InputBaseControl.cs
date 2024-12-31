namespace MeshWeaver.Layout;
/// <summary>
/// Interface for input controls.
/// </summary>
/// <remarks>
/// For more information, visit the 
/// <a href="https://www.fluentui-blazor.net/input">Fluent UI Blazor Input documentation</a>.
/// </remarks>

public interface IInputControl : IFormComponent
{
    /// <summary>
    /// Gets or initializes the data associated with the input control.
    /// </summary>
    /// <summary>
    /// Gets or initializes the placeholder text for the input control.
    /// </summary>
    object Placeholder { get; init; }
     /// <summary>
    /// Gets or initializes the autofocus state of the input control.
    /// </summary>
    object AutoFocus { get; init; }
    object Disabled { get; init; }
    object ReadOnly { get; init; }
    object Immediate { get; init; }
    object ImmediateDelay { get; init; }
}

public abstract record InputBaseControl<TControl>(object Data)
    : FormComponentBase<TControl>(Data)
    where TControl : InputBaseControl<TControl>, IInputControl
{
    public object AutoFocus { get; init; }
/// <summary>
    /// Gets or initializes the disabled state of the input control.
    /// </summary>
    public object Disabled { get; init; }
/// <summary>
    /// Gets or initializes the read-only state of the input control.
    /// </summary>
    public object ReadOnly { get; init; }

    public object Placeholder { get; init; }
/// <summary>
    /// Gets or initializes the immediate update state of the input control.
    /// </summary>
    public object Immediate { get; init; }
/// <summary>
    /// Gets or initializes the delay for immediate updates of the input control.
    /// </summary>
    public object ImmediateDelay { get; init; }

    public TControl WithAutoFocus(object autoFocus) => (TControl) this with { AutoFocus = autoFocus };

    public TControl WithDisabled(object disabled) => (TControl) this with { Disabled = disabled };

    public TControl WithReadOnly(object readOnly) => (TControl) this with { ReadOnly = readOnly };

    public TControl WithPlaceholder(object placeholder) => (TControl) this with { Placeholder = placeholder };

    public TControl WithImmediate(object immediate) => (TControl) this with { Immediate = immediate };

    public TControl WithImmediateDelay(object immediateDelay) => (TControl) this with { ImmediateDelay = immediateDelay };
}
