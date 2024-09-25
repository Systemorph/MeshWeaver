using MeshWeaver.Messaging;

namespace MeshWeaver.Catalog.Data;

public static class CatalogDataConfiguration
{
    public static MessageHubConfiguration AddCatalogData(
        this MessageHubConfiguration configuration
    ) => configuration.AddCatalogMockData();

}
