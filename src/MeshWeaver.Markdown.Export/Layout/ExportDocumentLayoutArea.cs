using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Markdown.Export.Layout;

/// <summary>
/// Layout areas that render the export dialog for a markdown node.
/// Two entry points: <c>ExportPdf</c> and <c>ExportDocx</c>. Both render the same
/// <see cref="ExportDocumentControl"/>; the Blazor view decides the default format.
/// </summary>
[Browsable(false)]
public static class ExportDocumentLayoutArea
{
    public const string PdfArea = "ExportPdf";
    public const string DocxArea = "ExportDocx";

    [Browsable(false)]
    public static IObservable<UiControl?> RenderPdf(LayoutAreaHost host, RenderingContext _) =>
        RenderExport(host, defaultFormat: "pdf");

    [Browsable(false)]
    public static IObservable<UiControl?> RenderDocx(LayoutAreaHost host, RenderingContext _) =>
        RenderExport(host, defaultFormat: "docx");

    private static IObservable<UiControl?> RenderExport(LayoutAreaHost host, string defaultFormat)
    {
        var hubPath = host.Hub.Address.ToString();
        return Observable.FromAsync(async () =>
        {
            var meshService = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
            var node = await meshService.QueryAsync<MeshNode>($"path:{hubPath}").FirstOrDefaultAsync();
            var hasDescendants = await meshService
                .QueryAsync<MeshNode>($"path:{hubPath} scope:descendants")
                .FirstOrDefaultAsync() is not null;

            return (UiControl?)new ExportDocumentControl
            {
                SourcePath = hubPath,
                NodeName = node?.Name,
                DefaultFormat = defaultFormat,
                HasDescendants = hasDescendants
            };
        });
    }
}
