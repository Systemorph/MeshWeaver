namespace OpenSmc.Layout;

public interface IInputControl : IUiControl
{
    object Placeholder { get; init; }
    object AutoFocus { get; init; }
    object Disabled { get; init; }
    object ReadOnly { get; init; }
    object Immediate { get; init; }
    object ImmediateDelay { get; init; }
}

public abstract record InputBaseControl<TControl>(object Data)
    : UiControl<TControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data)
    where TControl : InputBaseControl<TControl>, IInputControl
{
    public object AutoFocus { get; init; }

    public object Disabled { get; init; }

    public object ReadOnly { get; init; }

    public object Placeholder { get; init; }

    public object Immediate { get; init; }

    public object ImmediateDelay { get; init; }
    
    public TControl WithAutoFocus(object autoFocus) => (TControl) this with { AutoFocus = autoFocus };

    public TControl WithDisabled(object disabled) => (TControl) this with { Disabled = disabled };

    public TControl WithReadOnly(object readOnly) => (TControl) this with { ReadOnly = readOnly };

    public TControl WithPlaceholder(object placeholder) => (TControl) this with { Placeholder = placeholder };

    public TControl WithImmediate(object immediate) => (TControl) this with { Immediate = immediate };

    public TControl WithImmediateDelay(object immediateDelay) => (TControl) this with { ImmediateDelay = immediateDelay };
}
