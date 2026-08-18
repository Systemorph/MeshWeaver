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
internal static class PluginSurfaceProbe
{
    /// <summary>How long a probe waits for the index to answer before reporting "not here".</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// Emits exactly once: <c>true</c> the moment <paramref name="path"/> is seen, else
    /// <c>false</c> when the budget expires — so an uninstalled package costs a page one index
    /// query and never more than <see cref="Budget"/>.
    ///
    /// <para>🚨 Genuinely time-bounded, which a <c>Throttle</c> is NOT: a debounce only fires
    /// after a quiet window, so a query stream that keeps emitting faster than the window never
    /// releases it and the caller waits forever. That distinction did not matter while this ran on
    /// one page behind a query string; it matters now that every markdown page probes on render.
    /// The timeout's fallback EMITS <c>false</c> rather than completing empty — an empty completion
    /// is how a bounded wait still hangs its subscriber (the paywall-chain lesson).</para>
    /// </summary>
    internal static IObservable<bool> Exists(IMeshService? mesh, string path)
        => mesh is null
            ? Observable.Return(false)
            : mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}"))
                .Where(change => change.Items?.Any(node => node.Path == path) ?? false)
                .Select(_ => true)
                .Take(1)
                .Timeout(Budget, Observable.Return(false))
                // A faulting index query is a "not here" answer, never an exception thrown at a
                // render path that has nothing to do with the package.
                .Catch(Observable.Return(false));
}
