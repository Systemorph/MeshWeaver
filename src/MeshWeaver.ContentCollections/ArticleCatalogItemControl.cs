using MeshWeaver.Layout;

namespace MeshWeaver.ContentCollections;

public record ArticleCatalogItemControl(object Article)
    : UiControl<ArticleCatalogItemControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);

public record ArticleCatalogSkin : Skin<ArticleCatalogSkin>;
