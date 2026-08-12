namespace MeshWeaver.Markdown.Export.Pdf;

/// <summary>
/// The PDF print-document templates, embedded in the assembly next to the code that fills them.
/// Markup and stylesheet live in real <c>.html</c> / <c>.css</c> files — never as interpolated
/// strings in C# — so they stay readable, diffable and reviewable as the page furniture they are.
/// Same convention as <see cref="Pixel.SlidePrintTemplates"/> and the export <c>.csx</c> templates.
/// </summary>
internal static class DocumentPrintTemplates
{
    private static readonly Lazy<string> LazyDocument = new(() => Read("DocumentPrint.html"));
    private static readonly Lazy<string> LazyStyles = new(() => Read("DocumentPrint.css"));

    /// <summary>The document skeleton, with its title / styles / cover / toc / body placeholders.</summary>
    public static string Document => LazyDocument.Value;

    /// <summary>The print stylesheet, with its page-size and branding placeholders.</summary>
    public static string Styles => LazyStyles.Value;

    private static string Read(string fileName)
    {
        var assembly = typeof(DocumentPrintTemplates).Assembly;
        var fullName = $"{assembly.GetName().Name}.Pdf.{fileName}";
        using var stream = assembly.GetManifestResourceStream(fullName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{fullName}' not found. Ensure it is included as <EmbeddedResource> in the .csproj.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
