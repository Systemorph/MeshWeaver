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
                            .Aggregate(tabs, (t,s) =>
                                t.WithTab(new LayoutAreaControl(layout.Hub.Address, new(Docs) { Id = s }))))
            ]);


    private const string Pattern = @"^(?<sourceType>[^@]+)/(?<sourceId>[^@]+)/(?<documentId>[^@]+)$";

    private static async Task<object> Doc(LayoutAreaHost area, RenderingContext context, CancellationToken cancellationToken)
    {
        if (area.Stream.Reference.Id is not string path)
            throw new InvalidOperationException("No file name specified.");



        var documentationService = area.Hub.GetDocumentationService();

        var match = Regex.Match(path, Pattern);

        if (!match.Success)
            return FileNotFound(path);

        var sourceType = match.Groups["sourceType"].Value;
        var sourceId = match.Groups["sourceId"].Value;
        var documentId = match.Groups["documentId"].Value;

        await using var stream = documentationService.GetStream(sourceType, sourceId, documentId);
        if (stream != null)
        {
            using var reader = new StreamReader(stream);
            return new MarkdownControl(await reader.ReadToEndAsync(cancellationToken));
        }
        else
            // Resource not found, return a warning control/message instead
            return FileNotFound(path);
    }

    private static MarkdownControl FileNotFound(string path) => 
        new($":error: **File not found**: {path}");
}
