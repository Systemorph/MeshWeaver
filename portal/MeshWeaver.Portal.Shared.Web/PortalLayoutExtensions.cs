using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Views;
using MeshWeaver.Messaging;

namespace MeshWeaver.Portal.Shared.Web;

public static class PortalLayoutExtensions
{
    internal static PortalApplicationAddress GetPortalLayoutAddress(this Address address)
    => new(address.Id);

    internal record PortalApplicationAddress(string Id) : Address(AddressType, Id)
    {
        public const string AddressType = "portal";
    }

    internal static MessageHubConfiguration ConfigurePortalApplication(this MessageHubConfiguration config)
        => config
            .WithType<PortalApplicationAddress>(PortalApplicationAddress.AddressType)
            .WithHostedHub(new PortalApplicationAddress(config.Address.Id), ConfigurePortalLayout);

    internal static MessageHubConfiguration ConfigurePortalLayout(this MessageHubConfiguration config)
        => config.AddLayout(PortalLayouts);

    private static LayoutDefinition PortalLayouts(LayoutDefinition layout)
        => layout.WithNavMenu((menu, _, _) => menu.NavMenu());


    private static NavMenuControl NavMenu(this NavMenuControl menu) =>
        menu.WithNavGroup(
            "Documentation",
            group => group.WithUrl("article/Documentation/Overview")
                .WithNavLink("Articles", "articles/Documentation")
                .WithNavLink("Areas", $"app/Documentation/{LayoutAreaCatalogArea.LayoutAreas}")
            //.WithNavLink("Data Model", $"app/Documentation/Model")
        ).WithNavGroup(
            "Northwind",
            group => group.WithUrl("article/Northwind/Overview")
                .WithNavLink("Articles", "articles/Northwind")
                .WithNavLink("Areas", $"app/Northwind/{LayoutAreaCatalogArea.LayoutAreas}")
                .WithNavLink("Data Model", $"app/Northwind/DataModel")

        );

}
