namespace MeshWeaver.Layout;

public abstract record FormComponentBase <TControl>(object Data)
    : UiControl<TControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion), IFormComponent
    where TControl : FormComponentBase<TControl>, IFormComponent
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




}
