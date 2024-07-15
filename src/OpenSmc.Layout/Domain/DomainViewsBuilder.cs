using System.Reactive.Linq;
using OpenSmc.Data;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.DataGrid;

namespace OpenSmc.Layout.Domain
{
    public record DomainViewsBuilder(LayoutDefinition Layout)
    {
        public const string Type = nameof(Type);

        public DomainViewsBuilder All()
            => WithMenu(menu =>
                menu
                    .WithTypesCatalog()
            ).WithCatalogView();

        public DomainViewsBuilder WithMenu(Func<DomainMenuBuilder, DomainMenuBuilder> menuConfig, string areaName = nameof(DomainViews.NavMenu))
            // ReSharper disable once WithExpressionModifiesAllMembers
            => this with { Layout = menuConfig.Invoke(new(areaName, Layout)).Build() };

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

        public LayoutDefinition Build() => Layout;

    }
}
