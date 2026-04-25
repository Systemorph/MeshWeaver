using System.ComponentModel;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Markdown.Export.Layout;

/// <summary>
/// Layout areas that render the export dialog for a markdown node.
/// Two entry points: <c>ExportPdf</c> and <c>ExportDocx</c>. Both render the same
/// <see cref="ExportDocumentControl"/>; the Blazor view decides the default format.
/// Uses the reactive <c>GetDataRequest</c> + <c>RegisterCallback</c> pattern — never
/// <c>await</c> inside the hub — so it can't deadlock the message pump.
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

        // Seed the observable with a partial control (SourcePath + DefaultFormat always known).
        // The hub then fires a GetDataRequest via Post + RegisterCallback — NO await — and when
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
                        subject.OnNext(new ExportDocumentControl
                        {
                            SourcePath = hubPath,
                            NodeName = node.Name,
                            DefaultFormat = defaultFormat,
                            HasDescendants = false
                        });
                    }
                },
                _ => { /* leave seed control in place on failure */ });

        return subject.AsObservable();
    }
}
