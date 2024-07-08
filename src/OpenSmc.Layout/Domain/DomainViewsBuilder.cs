using System.Reactive.Linq;
using OpenSmc.Data;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.DataGrid;

namespace OpenSmc.Layout.Domain
{
    public record DomainViewsBuilder(LayoutDefinition Layout)
    {
        public DomainViewsBuilder All()
            => WithMenu(menu =>
                menu
                    .WithTypesCatalog()
            ).WithCatalogView();

        public DomainViewsBuilder WithMenu(Func<DomainMenuBuilder, DomainMenuBuilder> menuConfig, string areaName = nameof(DomainViews.DomainMenu))
            // ReSharper disable once WithExpressionModifiesAllMembers
            => this with { Layout = menuConfig.Invoke(new(areaName, Layout)).Build() };

        // ReSharper disable once WithExpressionModifiesAllMembers
        public DomainViewsBuilder WithCatalogView(string area = nameof(Catalog)) => this with { Layout = Layout.WithView(area, Catalog) };

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
                .Select(instances => DataGridControlExtensions.ToDataGrid(instances, typeSource.ElementType, x => x.AutoMapColumns()));
        }

        public LayoutDefinition Build() => Layout;

    }
}
