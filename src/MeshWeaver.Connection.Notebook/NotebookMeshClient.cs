using System.Collections.Concurrent;
using MeshWeaver.Connection.SignalR;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.CSharp;

namespace MeshWeaver.Connection.Notebook;

public class NotebookMeshClient(string url, object address) : SignalRMeshClientBase<NotebookMeshClient>(url, address)
{
    protected override IMessageHub BuildHub()
    {
        var notebookMeshClient =
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

        return hub;
    }
}
