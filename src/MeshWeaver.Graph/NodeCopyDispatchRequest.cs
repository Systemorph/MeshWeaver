using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Security;

namespace MeshWeaver.Graph;

/// <summary>
/// Public surface for the NodeCopy operation. The handler at the mesh hub
/// (registered via <c>AddGraph</c>) <b>starts</b> a script-driven activity
/// at <c>Templates/Import/NodeCopy</c> via <c>ScriptDispatch.StartScript</c>
/// and posts back a <see cref="NodeCopyDispatchResponse"/> carrying the
/// activity path. The caller subscribes to that activity for live progress
/// and the script's terminal return value (the count of copied nodes).
///
/// <para>The handler does NOT wait for the activity to finish — that would
/// block the mesh hub's action block under load while the script does
/// cross-hub <c>CreateNode</c> traffic. See
/// <c>Doc/Architecture/AsynchronousCalls.md</c> → "🚨 NOTHING ASYNC EVER".</para>
///
/// <para>The legacy <c>CopyNodeRequest</c> (in <c>MeshWeaver.Mesh.Contract</c>)
/// remains for low-level / single-node copy paths used by
/// <c>MoveNodeRequest</c> and AI <c>MeshOperations</c>; this dispatch
/// request is the high-level "deep-copy a subtree" UX surface.</para>
/// </summary>
[RequiresPermission(Permission.Create)]
public sealed record NodeCopyDispatchRequest(string SourcePath, string TargetNamespace)
    : IRequest<NodeCopyDispatchResponse>
{
    /// <summary>If true, overwrite (delete + recreate) any node already at the target path.</summary>
    public bool Force { get; init; }
}

/// <summary>
/// Start-acknowledgement for <see cref="NodeCopyDispatchRequest"/>. Subscribe
/// to <c>workspace.GetMeshNodeStream(ActivityPath)</c> for live progress and
/// the script's terminal return value (a JSON dictionary including the
/// <c>count</c> of copied nodes).
/// </summary>
/// <param name="ActivityPath">Mesh path of the running <c>Activity</c> MeshNode.
/// Empty when <see cref="Error"/> is set.</param>
/// <param name="Error">Dispatch error (e.g. permission denied, template not
/// found). <c>null</c> on successful start.</param>
public sealed record NodeCopyDispatchResponse(string ActivityPath, string? Error = null)
{
    /// <summary>True when the dispatch acknowledged a running activity.</summary>
    public bool Success => string.IsNullOrEmpty(Error);
}
