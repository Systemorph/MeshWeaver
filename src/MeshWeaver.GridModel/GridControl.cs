using MeshWeaver.Layout;

namespace MeshWeaver.GridModel;

public record GridControl(object Data) : UiControl<GridControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);

public record PivotGridControl(object Data, PivotConfiguration Configuration) : UiControl<PivotGridControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    public bool? ShowPager { get; init; }
    public int? PageSize { get; init; }
}
