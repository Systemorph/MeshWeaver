using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area that renders the node import UI.
/// Returns an existing <see cref="NodeImportControl"/> which is rendered by NodeImportView in Blazor.
/// </summary>
public static class ImportLayoutArea
{
    public static IObservable<UiControl?> ImportMeshNodes(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return Observable.Return<UiControl?>(new NodeImportControl { TargetPath = hubPath });
    }
}
