using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Helper for deep-copying a mesh node tree (source node + all descendants) to a target namespace.
/// </summary>
public static class NodeCopyHelper
{
    /// <summary>Default per-batch concurrency for the parallel
    /// <see cref="CreateOrUpdateNodeRequest"/> fan-out below. Bounded so a
    /// large subtree doesn't open every per-node hub at once on the
    /// receiving side.</summary>
    public const int DefaultBatchSize = 16;

    /// <summary>
    /// Copies a node and all its descendants to a target namespace. The
    /// source node's Id is preserved; paths are rewritten under the target
    /// namespace. Returns an <see cref="IObservable{T}"/> that emits the
    /// total count of upserted nodes (each <see cref="CreateOrUpdateNodeRequest"/>
    /// that succeeds counts as 1, regardless of create-vs-update).
    ///
    /// <para><b>Reactive end-to-end</b> — no <c>await</c>, no
    /// <c>Task.FromAsync</c>, no <c>ToTask</c>. Per-node upsert observables
    /// are merged with concurrency <paramref name="batchSize"/> so
    /// independent writes can run in parallel without exhausting the
    /// receiving side. Permission checks live inside the
    /// <see cref="CreateOrUpdateNodeRequest"/> handler — the helper just
    /// dispatches.</para>
    ///
    /// <para><b>Routing</b> — every per-node request is targeted at the root mesh
    /// hub (<c>hub.GetMeshHub()</c>), where the node-operation handlers are
    /// guaranteed to be registered. <paramref name="hub"/> may therefore be ANY
    /// hub (an MCP/REST session hub, a per-node UI hub, the mesh hub itself); it
    /// only supplies the caller identity and the reply address.</para>
    ///
    /// <para><b>force semantics</b> are encoded in the upsert request: when
    /// the target path already exists, the upsert handler applies the change
    /// via <c>stream.Update</c> (requires Update permission) when
    /// <paramref name="force"/> is <c>true</c> and skips otherwise. The
    /// helper never deletes a target — that race against the per-node hub's
    /// disposal was the cause of the previous "GetNode returns null after
    /// force-overwrite" bug.</para>
    /// </summary>
    public static IObservable<int> CopyNodeTree(
        IMeshService meshQuery,
        IMeshService nodeFactory,
        IMessageHub hub,
        string sourcePath,
        string targetNamespace,
        bool force,
        ILogger? logger = null,
        int batchSize = DefaultBatchSize)
    {
        logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.NodeCopyHelper");

        // 🚨 Capture the caller's identity EAGERLY, at invocation (the click action / MCP tool /
        // handler thread where AsyncLocal is still correct). The per-node CreateNodeRequest posts
        // below fire from SelectMany continuations on the subtree-query's emission scheduler, where
        // the AsyncLocal AccessContext is WIPED — so an un-stamped post would carry no identity and
        // the PostPipeline would fail closed (only the root landing; recursive children erroring
        // with "AccessContext must never be null … message=CreateNodeRequest" — the cross-partition
        // copy bug). Stamp the captured context explicitly on every post, exactly as
        // FanOutDeleteSubtree does for recursive deletes.
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var callerAccessContext = accessService?.Context ?? accessService?.CircuitContext;

        // 🚨 Target the per-node create/upsert requests at the ROOT mesh hub — the hub where
        // WithNodeOperationHandlers registers the CreateNodeRequest/CreateOrUpdateNodeRequest
        // handlers (MeshBuilder.AddMesh). An un-targeted Observe delivers to the CALLING hub,
        // which only works when that hub happens to register the node-op handlers itself
        // (per-node UI hubs do, via AddMeshDataSource). The MCP/REST session hub
        // (portal/mcp-{user}-{session}, SessionHubResolver) registers only AddData — every
        // per-node post bounced with "No handler found for message type CreateNodeRequest"
        // (memex 2026-07-13, MCP copy tool). Same routing contract as MeshService.MeshAddress,
        // whose comment pins the identical 2026-05-23 incident for un-walked child-hub writes.
        var meshAddress = hub.GetMeshHub().Address;

        // Shared post options for both verbs: route to the mesh hub and stamp the
        // eagerly-captured caller identity (mirrors MeshService.ConfigurePost).
        PostOptions ConfigurePost(PostOptions o)
        {
            o = o.WithTarget(meshAddress);
            return callerAccessContext is null ? o : o.WithAccessContext(callerAccessContext);
        }

        // Single Query over the source subtree — emits source + every
        // descendant in one go. Take(1) snapshots the listing for the copy
        // operation; subsequent edits to the source don't follow.
        var sourceSubtree = meshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{sourcePath} scope:subtree"))
            .Take(1)
            .Select(c => c.Items
                .Where(n => !string.IsNullOrEmpty(n.Path))
                .ToArray());

        return sourceSubtree.SelectMany(allNodes =>
        {
            var sourceNode = allNodes.FirstOrDefault(n =>
                string.Equals(n.Path, sourcePath, StringComparison.Ordinal));
            if (sourceNode == null)
                return Observable.Throw<int>(
                    new InvalidOperationException($"Source node not found: {sourcePath}"));

            var sourceNamespace = sourceNode.Namespace ?? "";

            // Per-node copy observable factory. Two verbs by design:
            //
            //   force=false → CreateNodeRequest. Handler fails with
            //                 NodeAlreadyExists when target is populated;
            //                 helper maps that rejection to count=0 (skip).
            //   force=true  → CreateOrUpdateNodeRequest. Handler always
            //                 writes — Create on missing, Update on existing.
            //
            // CreateOrUpdateNodeRequest is the single upsert verb; "skip on
            // exists" semantics live in the create-only path. No flag to
            // mix the two — keeps each verb single-purpose.
            IObservable<int> CopyOne(MeshNode node)
            {
                var newPath = RemapPath(node.Path, sourceNamespace, targetNamespace);
                var copiedNode = MeshNode.FromPath(newPath) with
                {
                    Name = node.Name,
                    NodeType = node.NodeType,
                    Icon = node.Icon,
                    Category = node.Category,
                    Content = node.Content,
                    State = MeshNodeState.Active,
                    PreRenderedHtml = node.PreRenderedHtml,
                };
                if (force)
                {
                    return hub
                        .Observe<CreateOrUpdateNodeResponse>(
                            new CreateOrUpdateNodeRequest(copiedNode),
                            ConfigurePost)
                        .FirstAsync()
                        .Select(d => d.Message)
                        .SelectMany(resp =>
                        {
                            if (resp.Success)
                            {
                                logger?.LogInformation(
                                    "Copied {SourcePath} -> {TargetPath} ({Mode})",
                                    node.Path, newPath, resp.WasCreated ? "created" : "updated");
                                return Observable.Return(1);
                            }
                            return Observable.Throw<int>(new InvalidOperationException(
                                $"Force-upsert of '{newPath}' failed: {resp.Error}"));
                        });
                }
                return hub
                    .Observe<CreateNodeResponse>(new CreateNodeRequest(copiedNode),
                        ConfigurePost)
                    .FirstAsync()
                    .Select(d => d.Message)
                    .SelectMany(resp =>
                    {
                        if (resp.Success)
                        {
                            logger?.LogInformation(
                                "Copied node {SourcePath} -> {TargetPath}", node.Path, newPath);
                            return Observable.Return(1);
                        }
                        if (resp.RejectionReason == NodeCreationRejectionReason.NodeAlreadyExists)
                        {
                            logger?.LogInformation(
                                "Skipping existing node at {TargetPath}", newPath);
                            return Observable.Return(0);
                        }
                        return Observable.Throw<int>(new InvalidOperationException(
                            $"Copy of '{node.Path}' to '{newPath}' failed: {resp.Error}"));
                    });
            }

            // Parallel batch with bounded concurrency. allNodes -> per-node
            // observable -> Merge(batchSize) caps in-flight count.
            return allNodes
                .Select(CopyOne)
                .ToObservable()
                .Merge(batchSize)
                .Sum();
        });
    }

    private static string RemapPath(string path, string sourceNamespace, string targetNamespace)
    {
        string relativePart;
        if (string.IsNullOrEmpty(sourceNamespace))
        {
            relativePart = path;
        }
        else if (path.StartsWith(sourceNamespace + "/", StringComparison.Ordinal))
        {
            relativePart = path[(sourceNamespace.Length + 1)..];
        }
        else if (path == sourceNamespace)
        {
            relativePart = path;
        }
        else
        {
            relativePart = path;
        }

        return string.IsNullOrEmpty(targetNamespace)
            ? relativePart
            : $"{targetNamespace}/{relativePart}";
    }
}
