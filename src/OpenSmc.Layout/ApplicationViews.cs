using OpenSmc.Layout.Composition;
using OpenSmc.Layout.Domain;

namespace OpenSmc.Layout;

public static class ApplicationViews
{
    public const string Catalog = nameof(Catalog);
    public const string File = nameof(File);
    public const string Markdown = nameof(Markdown);
    public const string NavMenu = nameof(NavMenu);
    public const string Source =nameof(Source);

    public static LayoutDefinition ConfigureApplication(
        this LayoutDefinition layout,
        Func<ApplicationBuilder, ApplicationBuilder> configuration
    ) =>
        configuration.Invoke(new(layout)).Build();

    public static ApplicationBuilder DefaultViews(this ApplicationBuilder layout)
        => layout.WithCatalog();
    public static string ToHref(this LayoutAreaReference reference, object address)
    {
        var ret = $"{address}/{Uri.EscapeDataString(reference.Area)}";
        if (reference.Id?.ToString() is { } s)
            ret = $"{ret}/{Uri.EscapeDataString(s)}";
        if (reference.Options.Any())
            ret = $"ret?{string.Join('&',
                reference.Options
                    .Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value?.ToString() ?? "")}"))}";
        return ret;
    }
}
