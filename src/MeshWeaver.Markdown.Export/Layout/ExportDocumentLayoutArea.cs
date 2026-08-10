using System.ComponentModel;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown.Export.Pixel;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Markdown.Export.Layout;

/// <summary>
/// Layout areas that render the export dialog for a markdown node.
/// Two entry points: <c>ExportPdf</c> and <c>ExportDocx</c>. Both render the same
/// <see cref="ExportDocumentControl"/>; the Blazor view decides the default format.
/// Uses the reactive <c>GetDataRequest</c> + <c>hub.Observe(...)</c> pattern — never
/// <c>await</c> inside the hub — so it can't deadlock the message pump.
/// </summary>
[Browsable(false)]
public static class ExportDocumentLayoutArea
{
    /// <summary>Area name for the PDF export dialog.</summary>
    public const string PdfArea = "ExportPdf";
    /// <summary>Area name for the DOCX export dialog.</summary>
    public const string DocxArea = "ExportDocx";

    /// <summary>
    /// Renders the export dialog with PDF selected as the default format.
    /// </summary>
    /// <param name="host">The layout area host providing hub and workspace access.</param>
    /// <param name="_">The rendering context (unused).</param>
    /// <returns>An observable stream of the export dialog control.</returns>
    [Browsable(false)]
    public static IObservable<UiControl?> RenderPdf(LayoutAreaHost host, RenderingContext _) =>
        RenderExport(host, defaultFormat: "pdf");

    /// <summary>
    /// Renders the export dialog with DOCX selected as the default format.
    /// </summary>
    /// <param name="host">The layout area host providing hub and workspace access.</param>
    /// <param name="_">The rendering context (unused).</param>
    /// <returns>An observable stream of the export dialog control.</returns>
    [Browsable(false)]
    public static IObservable<UiControl?> RenderDocx(LayoutAreaHost host, RenderingContext _) =>
        RenderExport(host, defaultFormat: "docx");

    private static IObservable<UiControl?> RenderExport(LayoutAreaHost host, string defaultFormat)
    {
        var hubPath = host.Hub.Address.ToString();

        // Seed the observable with a partial control (SourcePath + DefaultFormat always known).
        // The hub then fires a GetDataRequest via hub.Observe(...) — NO await — and when
        // the response arrives the observable emits an enriched control with NodeName set.
        var seed = (UiControl?)new ExportDocumentControl
        {
            SourcePath = hubPath,
            DefaultFormat = defaultFormat,
            HasDescendants = false
        };

        var subject = new BehaviorSubject<UiControl?>(seed);

        var nodeId = hubPath.Contains('/')
            ? hubPath[(hubPath.LastIndexOf('/') + 1)..]
            : hubPath;
        var delivery = host.Hub.Post(
            new GetDataRequest(new EntityReference(nameof(MeshNode), nodeId)),
            o => o.WithTarget(host.Hub.Address));
        if (delivery is null)
            return subject.AsObservable();
        // Use Observe → Subscribe so DeliveryFailure flows through OnError without throwing.
        host.Hub.Observe(delivery)
            .Subscribe(
                d =>
                {
                    if (d.Message is GetDataResponse resp && resp.Data is MeshNode node)
                    {
                        // Pixel fidelity is a DECK feature and needs a browser on this deployment.
                        // Probe reactively (promise-cached in the renderer) and enrich the control
                        // when the answer lands — the dialog is already usable meanwhile, it just
                        // shows no fidelity picker until (and unless) the capability is confirmed.
                        var enriched = new ExportDocumentControl
                        {
                            SourcePath = hubPath,
                            NodeName = node.Name,
                            DefaultFormat = defaultFormat,
                            HasDescendants = false
                        };
                        subject.OnNext(enriched);

                        if (node.NodeType != DeckNodeType.NodeType)
                            return;

                        var renderer = host.Hub.ServiceProvider.GetService<IPixelPdfRenderer>();
                        renderer?.Probe()
                            .Take(1)
                            .Subscribe(
                                executable => subject.OnNext(
                                    enriched with { PixelFidelityAvailable = executable is not null }),
                                _ => { /* no browser resolvable ⇒ capability stays off */ });
                    }
                },
                _ => { /* leave seed control in place on failure */ });

        return subject.AsObservable();
    }
}
