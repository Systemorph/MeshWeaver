namespace MeshWeaver.Layout.Views;

public record ArticleCatalogItemControl(object Article)
    : UiControl<ArticleCatalogItemControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);
