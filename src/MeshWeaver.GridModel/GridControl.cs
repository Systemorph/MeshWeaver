using MeshWeaver.Layout;

namespace MeshWeaver.GridModel;

/// <summary>
/// A layout UI control that renders a data grid; the <see cref="Data"/> payload typically carries
/// the <see cref="GridOptions"/> describing columns and rows.
/// </summary>
/// <param name="Data">The grid data/options payload to render.</param>
public record GridControl(object Data) : UiControl<GridControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);
