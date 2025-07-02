#nullable enable
using MeshWeaver.Layout;

namespace MeshWeaver.Charting;

public record ChartControl(object Data)
    : UiControl<ChartControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);
