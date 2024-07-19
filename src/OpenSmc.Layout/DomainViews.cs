using OpenSmc.Layout.Composition;
using OpenSmc.Layout.Domain;

namespace OpenSmc.Layout;

public static class DomainViews
{
    public const string Catalog = nameof(Catalog);
    public const string File = nameof(File);
    public const string Markdown = nameof(Markdown);
    public const string NavMenu = nameof(NavMenu);
    public static LayoutDefinition AddDomainViews(
        this LayoutDefinition layout,
        Func<DomainViewsBuilder, DomainViewsBuilder> configuration
    ) =>
        configuration.Invoke(new(layout)).Build();

    public static DomainViewsBuilder DefaultViews(this DomainViewsBuilder layout)
        => layout.WithCatalog();
    public static string GenerateHref(this ApplicationMenuBuilder builder, LayoutAreaReference reference)
    {
        var ret = $"{builder.Layout.Hub.Address}/{Uri.EscapeDataString(reference.Area)}";
        if (reference.Id?.ToString() is { } s)
            ret = $"{ret}/{Uri.EscapeDataString(s)}";
        if (reference.Options.Any())
            ret = $"ret?{string.Join('&',
                reference.Options
                    .Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value?.ToString() ?? "")}"))}";
        return ret;
    }

}
