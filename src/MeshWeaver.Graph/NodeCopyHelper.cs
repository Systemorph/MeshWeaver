using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
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
    /// <summary>
    /// Copies a node and all its descendants to a target namespace.
    /// The source node's Id is preserved; paths are rewritten under the target namespace.
    /// Returns an IObservable that emits the count of copied nodes when the operation completes.
    /// </summary>
    public static IObservable<int> CopyNodeTree(
        IMeshService meshQuery,
        IMeshService nodeFactory,
        IMessageHub hub,
        string sourcePath,
        string targetNamespace,
        bool force,
        ILogger? logger = null)
    {
        logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.NodeCopyHelper");

        // Source node: authoritative one-shot read via GetDataRequest on the per-node
        // MeshNodeReference reducer (NEVER ObserveQuery/QueryAsync for a single node's
        // content, and NEVER GetMeshNodeStream(...).Take(1) which pays for a stream
        // subscription it immediately unsubscribes; see Doc/Architecture/AsynchronousCalls.md).
        // Returns null if the cold hub never activates (path doesn't exist).
        var source = hub.GetMeshNode(sourcePath, TimeSpan.FromSeconds(15));

        // Descendants is a listing — ObserveQuery is the correct primitive for
        // namespace/predicate sets.
        var descendants = meshQuery.ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{sourcePath} scope:descendants"))
            .Take(1)
            .Select(c => c.Items.ToArray());

        return source
            .SelectMany(sourceNode => descendants.Select(desc =>
            {
                if (sourceNode == null)
                    throw new InvalidOperationException($"Source node not found: {sourcePath}");
                return new { sourceNode, descendants = desc };
            }))
            .SelectMany(pair =>
            {
                var sourceNamespace = pair.sourceNode.Namespace ?? "";
                var nodesToCopy = new[] { pair.sourceNode }.Concat(pair.descendants);

                var copyOps = nodesToCopy.Select(node =>
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

                    IObservable<int> create = nodeFactory.CreateNode(copiedNode)
                        .Select(_ =>
                        {
                            logger?.LogInformation("Copied node {SourcePath} -> {TargetPath}", node.Path, newPath);
                            return 1;
                        });

                    if (force)
                    {
                        // Overwrite semantics: existence check, then delete-then-create when
                        // present. Skipping the existence check would either error on a
                        // non-existent target (delete fails NotFound) or require a broad
                        // catch that would also swallow auth failures — both undesirable.
                        return hub.GetMeshNode(newPath, TimeSpan.FromSeconds(5))
                            .SelectMany(existing => existing == null
                                ? create
                                : nodeFactory.DeleteNode(newPath).SelectMany(_ => create));
                    }

                    // Existence-check via one-shot GetDataRequest. Routing returns
                    // NotFound (DeliveryFailure) when no per-node hub exists at newPath
                    // — no ancestor fallback. hub.GetMeshNode emits null in that case.
                    // create is wrapped in MeshService.CreateNode's Observable.Defer, so
                    // it only posts when subscribed (i.e. only when existence == null).
                    return hub.GetMeshNode(newPath, TimeSpan.FromSeconds(5))
                        .SelectMany(existing =>
                        {
                            if (existing != null)
                            {
                                logger?.LogInformation("Skipping existing node at {TargetPath}", newPath);
                                return Observable.Return(0);
                            }
                            return create;
                        });
                });

                return Observable.Concat(copyOps).Aggregate(0, (sum, v) => sum + v);
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
