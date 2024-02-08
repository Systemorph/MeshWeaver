namespace OpenSmc.Layout.Composition;

public record SpinnerControl()
    : UiControl<SpinnerControl, GenericUiControlPlugin<SpinnerControl>>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion,
        null)
{
    public virtual bool Equals(SpinnerControl other)
    {
        return other != null;
    }

    public override int GetHashCode()
    {
        return 1;
    }
}
