using MeshWeaver.Layout;

namespace MeshWeaver.GridModel;

public record GridControl(object Data) : UiControl<GridControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);
