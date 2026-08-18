using System;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph;

/// <summary>
/// The one way a PLATFORM page asks "is that plugin surface on this mesh?" before delegating a
/// section of itself to it — the shape core #737 established for the Versions page and the
/// markdown overview's approvals section now shares.
///
/// <para>🚨 It is a bounded existence probe over the QUERY INDEX, never a hub probe of the node
/// itself: subscribing to a node that does not exist costs the caller the full activation timeout
/// (60s of a blank page), which is exactly the cost a "is it installed?" question must not
/// have.</para>
/// </summary>
public static class PluginSurfaceProbe
{
    /// <summary>
    /// Emits once: whether <paramref name="path"/> exists on this mesh. Answers <c>false</c>
    /// quickly when it does not (the throttle bounds the wait), so an uninstalled package costs a
    /// page nothing beyond one index query.
    /// </summary>
    public static IObservable<bool> Exists(IMeshService? mesh, string path)
        => mesh is null
            ? Observable.Return(false)
            : mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}"))
                .Scan(false, (found, change) =>
                    found || (change.Items?.Any(node => node.Path == path) ?? false))
                .StartWith(false)
                .Throttle(TimeSpan.FromMilliseconds(800))
                .Take(1);
}
