using System.Reactive.Linq;
using OpenSmc.Data;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.DataGrid;

namespace OpenSmc.Layout.Domain
{
    public record DomainViewsBuilder
    {
        public DomainViewsBuilder(LayoutDefinition Layout)
        {
            this.Layout = Layout;
            MainLayout = DefaultLayout
;

        }

        public const string Type = nameof(Type);


        private ViewElement DefaultLayout(ViewElement view) => new ViewElementWithView("_Layout", DefaultLayout(view, NavMenu));

        private object DefaultLayout(ViewElement view, NavMenuControl navMenu)
        {
            if (navMenu == null)
                return Controls.Body(view);
            return Controls.Stack()
                .WithClass("main")
                .WithOrientation(Orientation.Horizontal)
                .WithWidth("100%")
                .WithView(NavMenu)
                .WithView(Controls.Body(view));
        }

        // ReSharper disable once WithExpressionModifiesAllMembers
        public DomainViewsBuilder WithCatalogView(string area = nameof(Catalog)) => this with { Layout = Layout.WithView(area, Catalog) };

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
                            )
                    )
                ;
        }

        public LayoutDefinition Build() => Layout with
        {
            MainLayout = MainLayout
        };

        public DomainViewsBuilder WithMainLayout(Func<ViewElement, ViewElement> configuration)
            =>  this with
            {
                MainLayout = configuration
            };

        private Func<ViewElement, ViewElement> MainLayout { get; init; }


        public DomainViewsBuilder WithMenu(Func<DomainMenuBuilder, DomainMenuBuilder> menuConfig, string areaName = nameof(DomainViews.NavMenu))
            // ReSharper disable once WithExpressionModifiesAllMembers
            => this with { MenuConfig = menuConfig, MenuArea = areaName};

        private Func<DomainMenuBuilder, DomainMenuBuilder> MenuConfig { get; init; }

        private string MenuArea { get; init; }

        public NavMenuControl NavMenu => MenuConfig?.Invoke(new(MenuArea, Layout)).Build();
        public LayoutDefinition Layout { get; init; }

    }

}
