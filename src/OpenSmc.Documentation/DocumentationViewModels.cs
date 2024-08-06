using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using OpenSmc.Data;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.Domain;
using static OpenSmc.Layout.Controls;

namespace OpenSmc.Documentation;

public static class DocumentationViewModels
{
    public const string Docs = nameof(Docs);
    public const string MainContent = "$" + nameof(MainContent);

    private static Func<RenderingContext, bool> IsDocs => ctx => ctx.Layout == Docs;

    public static LayoutDefinition AddDocumentation(this LayoutDefinition layout)
        => layout
            .WithRenderer(IsDocs, RenderDocs)
            .WithView(nameof(Doc), (Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<object>>)Doc);



    private static IEnumerable<Func<EntityStore, EntityStore>>
        RenderDocs(
            LayoutAreaHost host,
            RenderingContext context
        )
        =>
        [
            store => store.UpdateControl(
                Docs,
                Tabs.WithTab(NamedArea(context.Area).WithSkin(Skins.Tab(context.DisplayName)))
            )

        ];


    public static LayoutDefinition WithSources(this LayoutDefinition layout,
        Func<RenderingContext, bool> contextFilter, params string[] sources)
        => layout.WithRenderer(ctx => IsDocs(ctx) && contextFilter(ctx),
            (h, config) =>
            [
                store => h.ConfigBasedRenderer(
                    config,
                    store,
                    Docs,
                    () => new TabsControl(),
                    (tabs, _) =>
                        sources.Select(s => new LayoutAreaControl(layout.Hub.Address, new(Docs) { Id = s }))
                            .Aggregate(tabs, (t, s) =>
                                t.WithTab(new LayoutAreaControl(layout.Hub.Address, new(Docs) { Id = s }))))
            ]);


    private const string ReadPattern = @"^(?<sourceType>[^@]+)/(?<sourceId>[^@]+)/(?<documentId>[^@]+)$";

    public static async Task<object> Doc(LayoutAreaHost area, RenderingContext context,
        CancellationToken cancellationToken)
    {
        if (area.Stream.Reference.Id is not string path)
            throw new InvalidOperationException("No file name specified.");

        var documentationService = area.Hub.GetDocumentationService();

        var match = Regex.Match(path, ReadPattern);

        if (!match.Success)
            return FileNotFound(path);

        var sourceType = match.Groups["sourceType"].Value;
        var sourceId = match.Groups["sourceId"].Value;
        var documentId = match.Groups["documentId"].Value;
        await using var stream = documentationService.GetStream(sourceType, sourceId, documentId);
        if (stream != null)
        {
            var extension = documentId.Split('.').Last();
            using var reader = new StreamReader(stream);
            return new MarkdownControl(Format(await reader.ReadToEndAsync(cancellationToken), extension));
        }

        // Resource not found, return a warning control/message instead
        return FileNotFound(path);
    }

    private static string Format(string content, string extension)
    {
        extension = extension.ToLower();
        // TODO V10: Need to think how to output MD source. (05.08.2024, Roland Bürgi)
        if (extension == "md")
            return content;
        if (SupportedLanguages.TryGetValue(extension, out var lang))
            return $"```{lang}\n{content}\n```";
        return content;
    }

    private static readonly ImmutableDictionary<string, string> SupportedLanguages =
        ImmutableDictionary<string, string>.Empty
            .Add("md", "markdown")
            .Add("cs", "csharp")
            .Add("js", "javascript")
            .Add("html", "html")
            .Add("css", "css")
            .Add("json", "json")
            .Add("xml", "xml")
            .Add("sql", "sql")
            .Add("py", "python")
            .Add("java", "java")
            .Add("cpp", "cpp")
            .Add("ts", "typescript");
    private static MarkdownControl FileNotFound(string path) => 
        new($":error: **File not found**: {path}");
}
