using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;
using MeshWeaver.Data;
using MeshWeaver.Data.Documentation;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using static MeshWeaver.Layout.Controls;

namespace MeshWeaver.Layout.Documentation;

public static class DocumentationLayout
{
    public const string Documentation = nameof(Documentation);
    public const string MainContent = "$" + nameof(MainContent);

    private static Func<RenderingContext, bool> IsDocs => ctx => ctx.Layout == Documentation;

    public static LayoutDefinition AddDocumentation(this LayoutDefinition layout)
        => layout
            .WithDocumentation(
                _ => true,
                (tabs, host, ctx) => tabs.WithView(NamedArea(host.Reference.Area), ctx.DisplayName)
                )
            .WithView(nameof(Doc), (Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<UiControl>>)Doc);



    //private static TabsControl
    //    RenderDocs(
    //        LayoutAreaHost host,
    //        RenderingContext context
    //    )
    //    =>
    //        Tabs.WithTab(context.DisplayName, NamedArea(context.Area));

    public static LayoutDefinition WithDocumentation(this LayoutDefinition layout,
        Func<RenderingContext, bool> contextFilter,
        Func<TabsControl, LayoutAreaHost, RenderingContext, TabsControl> viewDefinition)
        => layout.WithRenderer(ctx => IsDocs(ctx) && contextFilter(ctx),
            (host, context, store) =>
                host.ConfigBasedRenderer(
                    context,
                    store,
                    Documentation,
                    () => new TabsControl(),
                    viewDefinition)
        );

    public static LayoutDefinition WithSourcesForType(
        this LayoutDefinition layout,
        Func<RenderingContext, bool> contextFilter,
        params Type[] types
    )
    {
        var documentationService = layout.Hub.GetDocumentationService();
        var sources = types
            .Select(t => t.Assembly)
            .Distinct()
            .Select(a => new
            {
                Assembly = a,
                Source = documentationService.GetSource(PdbDocumentationSource.Pdb, a.GetName().Name!)
                        as PdbDocumentationSource
            }
            )
            .Where(a => a.Source != null)
            .ToDictionary(a => a.Assembly, a => a.Source);


        return layout.WithDocumentation(contextFilter,
            (tabs, _, _) =>
                types
                .Select(type => sources.TryGetValue(type.Assembly, out var source)
                ? new
                {
                    Control =
                        Controls.LayoutArea(layout.Hub.Address,
                    new(nameof(Doc)) { Id = source!.GetPath(type.FullName!) })
                    ,
                    TabName = source!.GetDocumentName(type.FullName!),
                }
                    : null)
                .Where(x => x is { Control.Reference.Id: not null })
                .Aggregate(tabs, (t, s) =>
                    t.WithView(s!.Control.WithProgressMessage(s.TabName), s.TabName)));
    }
    public static LayoutDefinition WithEmbeddedDocument(
        this LayoutDefinition layout,
        Func<RenderingContext, bool> contextFilter,
        Assembly assembly,
        string name
    )
    {
        var source = layout.Hub.GetDocumentationService().GetSource(EmbeddedDocumentationSource.Embedded, assembly.GetName().Name!);
        return layout.WithDocumentation(contextFilter,
                (tabs, _, _) => tabs.WithView(
                    Controls.LayoutArea(layout.Hub.Address, new(nameof(Doc)) { Id = source!.GetPath(name) })
                        .WithProgressMessage(name),
                    name
                )
            )
            ;
    }

    public static LayoutAreaReference GetLayoutAreaReferenceForEmbeddedResource(this Assembly assembly, string resource)
        => new(nameof(Doc)) { Id = $"Embedded/{assembly.GetName().Name!}/{resource}" };

    private const string ReadPattern = @"^(?<sourceType>[^@]+)/(?<sourceId>[^@]+)/(?<documentId>[^@]+)$";

    public static async Task<UiControl> Doc(LayoutAreaHost area, RenderingContext context, CancellationToken cancellationToken)
    {
        if (area.Reference.Id is not string path)
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
            return new MarkdownControl(Format(await reader.ReadToEndAsync(), extension));
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
        new($":x: **File not found**: {path}");
}
