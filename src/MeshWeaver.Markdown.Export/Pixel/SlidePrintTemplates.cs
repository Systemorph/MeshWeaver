namespace MeshWeaver.Markdown.Export.Pixel;

/// <summary>
/// The print-document templates, embedded in the assembly next to the code that fills them.
/// Markup and stylesheet live in real <c>.html</c> / <c>.css</c> files — never as interpolated
/// strings in C# — so they stay readable, diffable, and impossible to confuse with the framework's
/// control-based rendering. Same convention the export <c>.csx</c> templates already use.
/// </summary>
internal static class SlidePrintTemplates
{
    private static readonly Lazy<string> LazyDocument = new(() => Read("SlidePrint.html"));
    private static readonly Lazy<string> LazyStyles = new(() => Read("SlidePrint.css"));
    private static readonly Lazy<string> LazySection = new(() => Read("SlidePrintSection.html"));

    /// <summary>The document skeleton, with <c>{{TITLE}}</c> / <c>{{STYLES}}</c> / <c>{{SLIDES}}</c>.</summary>
    public static string Document => LazyDocument.Value;

    /// <summary>The stage stylesheet, with <c>{{THEME_TOKENS}}</c>.</summary>
    public static string Styles => LazyStyles.Value;

    /// <summary>One slide section, with <c>{{BACKGROUND}}</c> / <c>{{BODY}}</c>.</summary>
    public static string Section => LazySection.Value;

    private static string Read(string fileName)
    {
        var assembly = typeof(SlidePrintTemplates).Assembly;
        var fullName = $"{assembly.GetName().Name}.Pixel.{fileName}";
        using var stream = assembly.GetManifestResourceStream(fullName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{fullName}' not found. Ensure it is included as <EmbeddedResource> in the .csproj.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
