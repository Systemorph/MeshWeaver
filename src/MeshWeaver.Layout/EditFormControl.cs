namespace MeshWeaver.Layout;

public record EditFormControl()
    : ContainerControl<EditFormControl, EditFormSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new());

public record EditFormSkin : Skin<EditFormSkin>
{
}
