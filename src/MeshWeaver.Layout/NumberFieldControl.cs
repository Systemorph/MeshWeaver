namespace MeshWeaver.Layout;

public record NumberFieldControl(object Data, string Type)
    : UiControl<NumberFieldControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion), IFormComponent
{
    public object HideStep { get; init; }
    public object DataList { get; init; }
    public object MaxLength { get; init; }
    public object MinLength { get; init; }
    public object Size { get; init; }
    public object Step { get; init; }
    public object Min { get; init; }
    public object Max { get; init; }
    public object Appearance { get; init; }
    public object ParsingErrorMessage { get; init; }
}
