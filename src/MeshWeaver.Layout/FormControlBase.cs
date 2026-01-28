namespace MeshWeaver.Layout;

public abstract record FormControlBase <TControl>(object Data)
    : UiControl<TControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion), IFormControl
    where TControl : FormControlBase<TControl>, IFormControl
{    
    /// <summary>
    /// The label bound to this control
    /// </summary>
    public object? Label { get; init; }


    /// <summary>
    /// Sets the label.
    /// </summary>
    /// <param name="label"></param>
    /// <returns></returns>
    public TControl WithLabel(object label)
        => This with { Label = label };

    IFormControl IFormControl.WithLabel(object label)
        => WithLabel(label);

    /// <summary>
    /// Whether the number field is disabled.
    /// </summary>
    public object? Disabled { get; init; }


    /// <summary>
    /// Whether the number field is required.
    /// </summary>
    public object? Required { get; init; }

    public object? AutoFocus { get; init; }
    /// <summary>
    /// Gets or initializes the immediate update state of the input control.
    /// </summary>
    public object? Immediate { get; init; }
    /// <summary>
    /// Gets or initializes the delay for immediate updates of the input control.
    /// </summary>
    public object? ImmediateDelay { get; init; }

    /// <summary>
    /// Gets or initializes the start icon of the text field control.
    /// </summary>
    public object? IconStart { get; init; }

    /// <summary>
    /// Gets or initializes the end icon of the text field control.
    /// </summary>
    public object? IconEnd { get; init; }

    /// <summary>
    /// Placeholder to be put in the control.
    /// </summary>
    public object? Placeholder { get; init; }

    private readonly Func<UiActionContext, Task>? blurAction;

    /// <summary>
    /// Whether the control has a blur action.
    /// </summary>
    public bool IsBlurable { get; init; }

    /// <summary>
    /// Action to invoke when the control loses focus.
    /// Internal to prevent serialization.
    /// </summary>
    internal Func<UiActionContext, Task>? BlurAction
    {
        get => blurAction;
        init
        {
            blurAction = value;
            IsBlurable = value != null;
        }
    }

    object? IFormControl.OnBlur => BlurAction;

    /// <summary>
    /// Sets the blur action for the control.
    /// </summary>
    /// <param name="onBlur">The blur action to set.</param>
    /// <returns>A new instance with the specified blur action.</returns>
    public TControl WithBlurAction(Func<UiActionContext, Task> onBlur) =>
        This with { BlurAction = onBlur };

    /// <summary>
    /// Sets the start icon of the text field control.
    /// </summary>
    /// <param name="icon">The start icon to set.</param>
    /// <returns>A new <see cref="TextFieldControl"/> instance with the specified start icon.</returns>
    public TControl WithIconStart(object icon) => This with { IconStart = icon };

    /// <summary>
    /// Sets the end icon of the text field control.
    /// </summary>
    /// <param name="icon">The end icon to set.</param>
    /// <returns>A new <see cref="TextFieldControl"/> instance with the specified end icon.</returns>
    public TControl WithIconEnd(object icon) => This with { IconEnd = icon };



    public TControl WithAutoFocus(object autoFocus) => (TControl)this with { AutoFocus = autoFocus };

    public TControl WithDisabled(object disabled) => (TControl)this with { Disabled = disabled };

    public TControl WithPlaceholder(object placeholder) => (TControl)this with { Placeholder = placeholder };

    public TControl WithImmediate(object immediate) => (TControl)this with { Immediate = immediate };

    public TControl WithImmediateDelay(object immediateDelay) => (TControl)this with { ImmediateDelay = immediateDelay };

}
