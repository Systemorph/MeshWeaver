using MeshWeaver.Application.Styles;
using MeshWeaver.Domain;
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
        menu
            .Collapse()
            .WithNavLink("Articles", "articles", FluentIcons.Book(IconSize.Size48, IconVariant.Filled))
            .WithNavLink("Documentation Areas", $"app/Documentation/{LayoutAreaCatalogArea.LayoutAreas}", FluentIcons.AppGeneric(IconSize.Size48, IconVariant.Filled))
            .WithNavLink("Northwind Areas", $"app/Northwind/{LayoutAreaCatalogArea.LayoutAreas}", FluentIcons.ShoppingBag(IconSize.Size48, IconVariant.Filled))
    ;

}
