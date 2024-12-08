using System.Collections.Concurrent;
using MeshWeaver.Connection.SignalR;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.DotNet.Interactive.Formatting;

namespace MeshWeaver.Connection.Notebook;

public class NotebookMeshClient(string url, object address) : SignalRMeshClientBase<NotebookMeshClient>(url, address)
{
    protected override IMessageHub BuildHub()
    {
        ConfigureHub(config => config
            .AddLayout(layout =>
                layout.WithView(ctx =>
                        areas.ContainsKey(ctx.Area),
                    (_, ctx) => areas.GetValueOrDefault(ctx.Area)
                )
            )
        );

        return base.BuildHub();
    }

    private string LayoutAreaUrl { get; set; } = ReplaceLastSegmentWithArea(url);
    public NotebookMeshClient WithLayoutAreaUrl(string url)
    {
        LayoutAreaUrl = ReplaceLastSegmentWithArea(url);
        return This;
    }

    private readonly ConcurrentDictionary<string, UiControl> areas = new();
    public IMessageHub Connect(CancellationToken ct = default)
    {
        var hub = BuildHub();

        var kernel = Kernel.Current;
        if (kernel is not CSharpKernel cSharpKernel)
            throw new NotSupportedException("Usage of Mesh Weaver is currently only supported in C#");

        cSharpKernel.AddAssemblyReferences(
            [
                typeof(MessageHub).Assembly.Location,
                            typeof(UiControl).Assembly.Location,
                            typeof(ApplicationAddress).Assembly.Location
            ]
        );

        var addressType = hub.ServiceProvider
            .GetTypeRegistry()
            .GetCollectionName(hub.Address.GetType())
            ;

        var addressId = hub.Address.ToString();


        Formatter.Register<UiControl>(
            (control, writer) =>
            {
                var id = Guid.NewGuid().AsString();
                areas[id] = control;
                writer.Write($"<iframe src='{LayoutAreaUrl}{addressType}/{addressId}/{id}'></iframe>");
            }, HtmlFormatter.MimeType);

        return hub;
    }

    private static string ReplaceLastSegmentWithArea(string url)
    {
        var uri = new Uri(url);
        var segments = uri.Segments;
        segments[^1] = "area/";
        return new Uri(uri, string.Join("", segments)).ToString();
    }
}
