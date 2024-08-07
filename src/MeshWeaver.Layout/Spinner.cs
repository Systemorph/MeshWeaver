namespace MeshWeaver.Layout;

public record SpinnerControl()
    : UiControl<SpinnerControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion,
        null)
{
    public virtual bool Equals(SpinnerControl other)
    {
        return other != null;
    }

    public override bool IsUpToDate(object other)
    {
        return other is SpinnerControl;
    }

    public override int GetHashCode()
    {
        return 1;
    }
}
