using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
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
    /// <param name="persistence">Persistence service for reading/writing nodes</param>
    /// <param name="sourcePath">Full path of the source node (e.g. "org/Acme")</param>
    /// <param name="targetNamespace">Target namespace (e.g. "myWorkspace"); empty string for root</param>
    /// <param name="force">If true, overwrite existing nodes at target paths</param>
    /// <param name="logger">Optional logger</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Count of nodes copied</returns>
    public static async Task<int> CopyNodeTreeAsync(
        IPersistenceService persistence,
        string sourcePath,
        string targetNamespace,
        bool force,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var sourceNode = await persistence.GetNodeAsync(sourcePath, ct);
        if (sourceNode == null)
            throw new InvalidOperationException($"Source node not found: {sourcePath}");

        // Collect source node + all descendants
        var nodesToCopy = new List<MeshNode> { sourceNode };
        await foreach (var descendant in persistence.GetDescendantsAsync(sourcePath).WithCancellation(ct))
        {
            nodesToCopy.Add(descendant);
        }

        // Compute the prefix to replace: the source node's namespace
        // e.g. source "org/Acme" has namespace "org", id "Acme"
        // descendants like "org/Acme/Team1" should become "targetNamespace/Acme/Team1"
        var sourceNamespace = sourceNode.Namespace ?? "";

        var nodesCopied = 0;
        foreach (var node in nodesToCopy)
        {
            var newPath = RemapPath(node.Path, sourceNamespace, targetNamespace);

            if (!force && await persistence.ExistsAsync(newPath, ct))
            {
                logger?.LogInformation("Skipping existing node at {TargetPath}", newPath);
                continue;
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

            await persistence.SaveNodeAsync(copiedNode, ct);
            nodesCopied++;
            logger?.LogInformation("Copied node {SourcePath} -> {TargetPath}", node.Path, newPath);
        }

        return nodesCopied;
    }

    /// <summary>
    /// Remaps a node path from source namespace to target namespace.
    /// E.g. RemapPath("org/Acme/Team1", "org", "workspace") => "workspace/Acme/Team1"
    /// </summary>
    private static string RemapPath(string path, string sourceNamespace, string targetNamespace)
    {
        // Strip the source namespace prefix to get the relative part
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
            // Edge case: source node IS at the namespace root
            relativePart = path;
        }
        else
        {
            relativePart = path;
        }

        // Build new path under target namespace
        return string.IsNullOrEmpty(targetNamespace)
            ? relativePart
            : $"{targetNamespace}/{relativePart}";
    }
}
