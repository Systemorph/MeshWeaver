using System.Collections.Concurrent;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Default <see cref="INodeTypeStreamCache"/> — wraps
/// <c>workspace.GetMeshNodeStream(path)</c> with <c>Replay(1).RefCount()</c>
/// per path so multiple subscribers share one upstream subscription and new
/// subscribers receive the cached snapshot instantly.
///
/// <para>The subscription is opened on the mesh hub's workspace. That is
/// safe because <c>GetMeshNodeStream</c> for a non-own path returns an
/// <c>ISynchronizationStream</c> — which runs on its OWN hub/scheduler, not
/// the caller's. The requesting workspace's hub only dispatches the initial
/// <c>SubscribeRequest</c>; the ongoing stream never blocks it. So a
/// dedicated "node-type service hub" buys nothing here — the sync stream is
/// already its own hub.</para>
///
/// <para>Side-channel <see cref="MaybeKickCompile"/> on every emission: if the
/// emitted MeshNode is a NodeType definition that has neither a
/// <c>LatestReleasePath</c> nor an <c>AssemblyLocation</c> and isn't already
/// pending/compiling, post a <c>CreateReleaseRequest</c> to its per-NodeType
/// hub. <c>HandleCreateRelease</c> runs Roslyn, writes the Release MeshNode,
/// and updates the NodeType's <c>LatestReleasePath</c> +
/// <c>AssemblyLocation</c>. The cache then re-emits through the same
/// observable so subscribers see the update without a resubscribe.</para>
/// </summary>
internal sealed class NodeTypeStreamCache : INodeTypeStreamCache
{
    private readonly IMessageHub meshHub;
    private readonly ILogger<NodeTypeStreamCache> logger;
    private readonly ConcurrentDictionary<string, IObservable<MeshNode>> _streams = new();

    public NodeTypeStreamCache(IMessageHub meshHub, ILogger<NodeTypeStreamCache> logger)
    {
        this.meshHub = meshHub;
        this.logger = logger;
    }

    public IObservable<MeshNode> GetStream(string path) =>
        _streams.GetOrAdd(path, p =>
            meshHub.GetWorkspace()
                .GetMeshNodeStream(p)
                .Do(node => MaybeKickCompile(p, node))
                .Replay(1)
                .RefCount());

    /// <summary>
    /// First touch of a NodeType MeshNode that has no usable artefact — kick
    /// off compilation. Repeated emissions where the node is already pending,
    /// compiling, or has a release are no-ops.
    /// </summary>
    private void MaybeKickCompile(string path, MeshNode node)
    {
        // Only NodeType definitions need compilation. Any other content type
        // (Activity, Release, Markdown, etc.) is just observed.
        if (node.NodeType != MeshNode.NodeTypePath) return;

        // Already has an assembly path — built-in NodeTypes set this at
        // registration; dynamic NodeTypes get it after a successful release.
        // No need to kick.
        if (!string.IsNullOrEmpty(node.AssemblyLocation)) return;

        if (node.Content is not Graph.Configuration.NodeTypeDefinition def)
            return;

        if (!string.IsNullOrEmpty(def.LatestReleasePath)) return;
        if (def.CompilationStatus is CompilationStatus.Pending or CompilationStatus.Compiling) return;

        // Don't auto-compile error-state NodeTypes — the user has to fix the
        // source and click Create Release explicitly. Auto-flipping Pending
        // on every emission would create a tight retry loop.
        if (def.CompilationStatus == CompilationStatus.Error) return;

        try
        {
            logger.LogInformation(
                "First touch of NodeType {Path} with no release — sending CreateReleaseRequest",
                path);
            // Post CreateReleaseRequest to the per-NodeType hub. HandleCreateRelease
            // runs the compile machinery (StartCompile) and writes back Ok +
            // AssemblyLocation. Replaces the CompilationStatus = Pending flip that
            // relied on InstallCompileWatcher (removed in 86b34707d when compile
            // became explicit-only). meshHub.Post is fire-and-forget — the response
            // arrives later but the only thing this code path cares about is that
            // the request reaches the per-NodeType hub.
            meshHub.Post(new CreateReleaseRequest(), o => o.WithTarget(new Address(path)));
        }
        catch (Exception ex)
        {
            // Best-effort — failing to kick the compile is observability, not
            // correctness. The next emission will retry, and the explicit
            // Create-Release click path still works regardless.
            logger.LogWarning(ex,
                "MaybeKickCompile failed for NodeType {Path} (best-effort, ignored)", path);
        }
    }
}
