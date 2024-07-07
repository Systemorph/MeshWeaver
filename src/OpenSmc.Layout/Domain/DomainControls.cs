using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Reflection;
using OpenSmc.Data;
using OpenSmc.Data.Persistence;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.DataGrid;
using OpenSmc.Utils;

namespace OpenSmc.Layout.Domain;

public static class DomainControls
{
    public static LayoutDefinition AddViewsMenu(this LayoutDefinition layout, string area,
        Func<DomainViewsBuilder, DomainViewsBuilder> configuration)
        => configuration.Invoke(new(layout)).Layout;
    public static LayoutDefinition AddDataTypesMenu(this LayoutDefinition layout, string area,
        Func<DomainViewsBuilder, DomainViewsBuilder> configuration)
        => configuration.Invoke(new(layout)).Layout;
}

public record DomainViewsBuilder(LayoutDefinition Layout)
{
    public DomainViewsBuilder WithMenu(string areaName, Func<DomainMenuBuilder, DomainMenuBuilder> menuConfig)
        => this with { Layout = menuConfig.Invoke(new(areaName, Layout)).Build() };

    public DomainViewsBuilder WithDataTypeCatalog() => this with { Layout = Layout.WithView(nameof(Catalog), Catalog) };

    public IObservable<object> Catalog(LayoutAreaHost area, RenderingContext ctx)
    {
        var collection = area.Stream.Reference.Id;
        if (collection == null)
            throw new InvalidOperationException("No type specified for catalog.");
        var typeSource = area.Workspace.DataContext.GetTypeSource(collection);
        if (typeSource == null)
            throw new DataSourceConfigurationException(
                $"Collection {collection} is not mapped in Address {Layout.Hub.Address}.");
        return area.Workspace
            .Stream
            .Reduce(new CollectionReference(collection), area.Stream.Subscriber)
            .Select(instances => instances.ToDataGrid(typeSource.ElementType, x => x.AutoMapColumns()));
    }
}

public record DomainMenuBuilder(string Area, LayoutDefinition Layout)
{
    internal NavMenuControl Menu { get; init; } = new();

    public DomainMenuBuilder WithNavLink(object title, object href, Func<NavLinkControl, NavLinkControl> config)
        => this with { Menu = Menu.WithNavLink(title, href, config) };
    public DomainMenuBuilder WithNavGroup(object title, Func<NavGroupControl, NavGroupControl> config)
        => this with { Menu = Menu.WithNavGroup(title, config) };

    public DomainMenuBuilder WithTypesCatalog()
        => this with
        {
            Layout = Layout.WithView(Area,
                Layout
                    .Workspace
                    .DataContext
                    .TypeSources
                    .Values
                    .Aggregate(
                        Menu.WithGroup("Data Types", x => x),
                        (menu, t) =>
                            menu.WithNavLink(t.DisplayName, $"/{Layout.Hub.Address}/Catalog/{t.CollectionName}")
                    )
            )
        };

    private LayoutDefinition MapToCatalogMenu<TResult>(LayoutDefinition layout, Type arg1, int arg2)
    {
        throw new NotImplementedException();
    }

    public LayoutDefinition Build() => Layout.WithView(Area, Menu);
}
