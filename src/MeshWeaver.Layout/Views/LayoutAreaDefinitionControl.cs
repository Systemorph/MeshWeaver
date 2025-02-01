namespace MeshWeaver.Layout.Views;

public record LayoutAreaDefinitionControl(LayoutAreaDefinition Definition)
    : UiControl<LayoutAreaDefinitionControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);
