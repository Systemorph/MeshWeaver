using MeshWeaver.Layout;

namespace MeshWeaver.Catalog.Layout;

public record CatalogItemControl(object Data) : 
    UiControl<CatalogItemControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
}
