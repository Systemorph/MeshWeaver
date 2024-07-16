using OpenSmc.Layout.Composition;
using OpenSmc.Layout.Domain;

namespace OpenSmc.Layout;

public static class DomainViews
{
    public const string Catalog = nameof(Catalog);
    public static LayoutDefinition AddDomainViews(
        this LayoutDefinition layout,
        Func<DomainViewsBuilder, DomainViewsBuilder> configuration
    ) =>
        configuration.Invoke(new(layout)).Build();

}
