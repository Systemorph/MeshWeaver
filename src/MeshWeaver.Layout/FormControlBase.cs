namespace MeshWeaver.Layout;

public abstract record FormControlBase <TControl>(object Data)
    : UiControl<TControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion), IFormControl
    where TControl : FormControlBase<TControl>, IFormControl
{    
    /// <summary>
    /// The label bound to this control
    /// </summary>
    public object Label { get; init; }


    /// <summary>
    /// Sets the label.
    /// </summary>
    /// <param name="label"></param>
    /// <returns></returns>
    public TControl WithLabel(object label)
        => This with { Label = label };

    IFormControl IFormControl.WithLabel(object label)
        => WithLabel(label);


}
