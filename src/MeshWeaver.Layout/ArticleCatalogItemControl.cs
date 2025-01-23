namespace MeshWeaver.Layout;

public record ArticleCatalogItemControl(object Article)
    : UiControl<ArticleCatalogItemControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);
