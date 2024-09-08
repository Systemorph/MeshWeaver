using MeshWeaver.Catalog.Layout;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Catalog.Views;

public static class LayoutClientExtensions
{
    public static LayoutClientConfiguration AddCatalogViews(this LayoutClientConfiguration config)
    {
        config.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>()
            .WithTypes([typeof(CatalogItemData)]);

        return config
            .WithView<CatalogItemControl, CatalogItemView>();
    }
}
