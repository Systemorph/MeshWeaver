namespace MeshWeaver.Layout;

public record NumberFieldControl(object Data, string Type)
    : UiControl<NumberFieldControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion); 
