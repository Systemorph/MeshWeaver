using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Markdown.Export.Layout;

/// <summary>
/// Layout areas for the markdown export dialog. Each entry point renders the
/// <see cref="ExportDocumentControl"/> — the Blazor view posts the request, receives
/// the rendered bytes in <see cref="MeshWeaver.Markdown.Export.Messaging.ExportDocumentResponse.Content"/>,
/// and hands them to the browser as a download stream (no server-side storage).
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
            var node = await meshService.QueryAsync<Mesh.MeshNode>($"path:{hubPath}").FirstOrDefaultAsync();
            var hasDescendants = await meshService
                .QueryAsync<Mesh.MeshNode>($"path:{hubPath} scope:descendants")
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
