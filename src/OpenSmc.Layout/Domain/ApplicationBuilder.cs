using System.Reactive.Linq;
using OpenSmc.Data;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.DataGrid;

namespace OpenSmc.Layout.Domain;

public record ApplicationBuilder
{
    public ApplicationBuilder(LayoutDefinition Layout)
    {
        this.Layout = Layout;
        MainLayout = DefaultLayoutViewElement
            ;

    }

    private ViewElement DefaultLayoutViewElement(ViewElement view, NavMenuControl navMenu)
        => new ViewElementWithView(view.Area, DefaultLayoutControl(view, navMenu), view.Options);

    public const string Type = nameof(Type);



    private object DefaultLayoutControl(ViewElement view, NavMenuControl navMenu)
    {
        if (navMenu == null)
            return Controls.Body(view);
        return Controls.Stack()
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithView(navMenu)
            .WithView(Controls.Body(view));
    }

    // ReSharper disable once WithExpressionModifiesAllMembers
    public ApplicationBuilder WithCatalog(string area = nameof(Catalog)) => this with { Layout = Layout.WithView(area, Catalog, options => options.WithMenuOrder(-1)) };

    public object Catalog(LayoutAreaHost area, RenderingContext ctx)
    {
        if (area.Stream.Reference.Id is not string collection)
            throw new InvalidOperationException("No type specified for catalog.");
        var typeSource = area.Workspace.DataContext.GetTypeSource(collection);
        if (typeSource == null)
            throw new DataSourceConfigurationException(
                $"Collection {collection} is not mapped in Address {Layout.Hub.Address}.");
        return
            Controls.Stack()
                .WithView(Controls.Title(typeSource.DisplayName, 1))
                .WithView(Controls.Html(typeSource.Description))
                .WithView((a, _) => a
                    .Workspace
                    .Stream
                    .Reduce(new CollectionReference(collection), area.Stream.Subscriber)
                    .Select(changeItem =>
                        area.ToDataGrid(
                            changeItem
                                .Value
                                .Instances
                                .Values,
                            typeSource.ElementType,
                            x => x.AutoMapColumns()
                        )
                    ),
                    x => x
                )
            ;
    }


    public LayoutDefinition Build() => Layout with
    {
        MainLayout = MainLayout,
        NavMenu = MenuConfig == null ? null : reference => MenuConfig.Invoke(new(MenuArea, Layout, reference)).Build()
    };

    public ApplicationBuilder WithMainLayout(Func<ViewElement, NavMenuControl, ViewElement> configuration)
        =>  this with
        {
            MainLayout = configuration
        };

    private Func<ViewElement, NavMenuControl, ViewElement> MainLayout { get; init; }


    public ApplicationBuilder WithMenu(Func<ApplicationMenuBuilder, ApplicationMenuBuilder> menuConfig, string areaName = nameof(ApplicationViews.NavMenu))
        // ReSharper disable once WithExpressionModifiesAllMembers
        => this with { MenuConfig = menuConfig, MenuArea = areaName};


    private Func<ApplicationMenuBuilder, ApplicationMenuBuilder> MenuConfig { get; init; }

    private string MenuArea { get; init; }

    public LayoutDefinition Layout { get; init; }

}

public static class FileSource
{
    public const string EmbeddedResource = nameof(EmbeddedResource);
}
