using MeshWeaver.Application.Styles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Views;
using MeshWeaver.Messaging;

namespace MeshWeaver.Portal.Shared.Web;

public static class PortalLayoutExtensions
{
    internal static LayoutDefinition AddNavMenu(this LayoutDefinition layout)
        => layout.WithNavMenu((menu, _, _) => menu.NavMenu());


    private static NavMenuControl NavMenu(this NavMenuControl menu) =>
        menu.WithNavLink("Articles", "articles", FluentIcons.News())
            .WithNavLink("Documentation Areas", $"app/Documentation/{LayoutAreaCatalogArea.LayoutAreas}", FluentIcons.SlideLayout())
            .WithNavLink("Northwind Areas", $"app/Northwind/{LayoutAreaCatalogArea.LayoutAreas}", FluentIcons.BuildingShop())
    ;

}
