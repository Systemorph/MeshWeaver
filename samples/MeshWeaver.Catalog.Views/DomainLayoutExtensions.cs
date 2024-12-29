using MeshWeaver.Catalog.Layout;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Client;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Catalog.Views;

public static class DomainLayoutExtensions
{
    public static LayoutClientConfiguration AddCatalogViews(this LayoutClientConfiguration config)
    {
        config.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>()
            .WithTypes([typeof(CatalogItemData)]);

        return config
            .WithView<CatalogItemControl, CatalogItemView>();
    }


}
