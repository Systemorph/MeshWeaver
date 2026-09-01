using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Blazor")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Blazor.Test")]
[assembly: InternalsVisibleTo("MeshWeaver.Blazor.Portal")]
[assembly: InternalsVisibleTo("MeshWeaver.Graph")]
[assembly: InternalsVisibleTo("MeshWeaver.Graph.Contract")]
[assembly: InternalsVisibleTo("MeshWeaver.Compiler")]
[assembly: InternalsVisibleTo("MeshWeaver.Compiler.Pipeline")]
[assembly: InternalsVisibleTo("MeshWeaver.AI")]
[assembly: InternalsVisibleTo("Memex.Portal.Shared")]
// The notification triage watcher observes ALL users' Notification nodes and their rules — a
// system-level watch with no ambient user context, so it needs the unfiltered core (the same
// reason it had this grant while it lived in Memex.Portal.Shared).
[assembly: InternalsVisibleTo("MeshWeaver.Notifications.Channels")]
// SyncedQueryInitialGateTest decorates the REAL IMeshQueryCore with a
// subscription-delaying wrapper to pin the pre-Initial emission gate.
[assembly: InternalsVisibleTo("MeshWeaver.Query.Test")]
// OverlayReEvaluationReadTest substitutes the core to pin that the compilation-overlay
// re-evaluation reads STORAGE rather than a cached stream (issue #1814) — a wiring only a
// test over the real seam can prove, since every shape test passes against a read that never
// matches anything.
[assembly: InternalsVisibleTo("MeshWeaver.Graph.Test")]
// Castle.DynamicProxy (used by NSubstitute) generates proxies in this assembly;
// without InternalsVisibleTo it can't implement the internal IMeshQueryCore.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Infrastructure query interface without access control.
/// Used by infrastructure code (login, NodeTypeService, compilation,
/// SecurityService's own AccessAssignment lookup) that needs raw queries
/// without user context. Must not be exposed to application code.
///
/// <para>Decouples consumers from <see cref="IMeshQueryProvider"/> which
/// pulls in <c>SecurityService</c> as a constructor dependency
/// — the cycle source for SecurityService → workspace.GetQuery →
/// SyncedQueryMeshNodes → IMeshQueryProvider → StorageAdapterMeshQueryProvider →
/// SecurityService.</para>
/// </summary>
internal interface IMeshQueryCore
{
    /// <summary>
    /// Observe nodes matching a query without access control filtering.
    /// Emits Initial / Added / Updated / Removed deltas as the underlying
    /// data changes. Same shape as
    /// <see cref="IMeshQueryProvider.Query{T}(MeshQueryRequest, JsonSerializerOptions)"/>
    /// — minus the security filter on the result set.
    /// </summary>
    IObservable<QueryResultChange<T>> Query<T>(
        MeshQueryRequest request,
        JsonSerializerOptions options);
}
