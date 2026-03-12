using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
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
    /// </summary>
    internal static async Task<int> CopyNodeTreeAsync(
        IMeshService meshQuery,
        IMeshService nodeFactory,
        IMessageHub hub,
        string sourcePath,
        string targetNamespace,
        bool force,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var sourceNode = await meshQuery.QueryAsync<MeshNode>($"path:{sourcePath}").FirstOrDefaultAsync(ct);
        if (sourceNode == null)
            throw new InvalidOperationException($"Source node not found: {sourcePath}");

        // Collect source node + all descendants
        var nodesToCopy = new List<MeshNode> { sourceNode };
        await foreach (var descendant in meshQuery.QueryAsync<MeshNode>($"path:{sourcePath} scope:descendants").WithCancellation(ct))
        {
            nodesToCopy.Add(descendant);
        }

        // Compute the prefix to replace
        var sourceNamespace = sourceNode.Namespace ?? "";

        var nodesCopied = 0;
        foreach (var node in nodesToCopy)
        {
            var newPath = RemapPath(node.Path, sourceNamespace, targetNamespace);

            if (!force)
            {
                var existing = await meshQuery.QueryAsync<MeshNode>($"path:{newPath}").FirstOrDefaultAsync(ct);
                if (existing != null)
                {
                    logger?.LogInformation("Skipping existing node at {TargetPath}", newPath);
                    continue;
                }
            }

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

            await nodeFactory.CreateNodeAsync(copiedNode, ct: ct);
            nodesCopied++;
            logger?.LogInformation("Copied node {SourcePath} -> {TargetPath}", node.Path, newPath);
        }

        return nodesCopied;
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
