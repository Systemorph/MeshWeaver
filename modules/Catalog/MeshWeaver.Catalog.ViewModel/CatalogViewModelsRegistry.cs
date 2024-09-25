using MeshWeaver.Catalog.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Messaging;

namespace MeshWeaver.Catalog.ViewModel;

public static class CatalogViewModels
{
    public static MessageHubConfiguration AddCatalogViewModels(
        this MessageHubConfiguration configuration
    )
        => configuration
            .AddCatalogData()
            .AddLayout(layout => layout
                .WithPageLayout()
                .AddCatalogAssistant()
                .AddCatalog()
            )
            ;
}
