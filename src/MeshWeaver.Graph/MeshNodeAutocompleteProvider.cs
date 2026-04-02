using System.Runtime.CompilerServices;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Generic autocomplete provider that queries child nodes from the mesh query service.
/// Uses the hub's address as the parent path to find children.
/// Returns relative paths when contextPath matches the hub address, absolute otherwise.
/// </summary>
internal class MeshNodeAutocompleteProvider(IMeshService meshQuery, IMessageHub hub) : IAutocompleteProvider
{
    private const int DefaultMaxResults = 20;

    /// <inheritdoc />
    public async IAsyncEnumerable<AutocompleteItem> GetItemsAsync(
        string query,
        string? contextPath = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Use the hub's address as the parent path
        var parentPath = hub.Address.ToString();

        var queryString = $"namespace:{parentPath}";
        if (!string.IsNullOrWhiteSpace(query))
            queryString += $" name:{query}";

        // Query for child nodes and yield results
        var count = 0;
        await foreach (var node in meshQuery.QueryAsync<MeshNode>(queryString).WithCancellation(ct))
        {
            if (count >= DefaultMaxResults) break;
            count++;

            // Use relative name when context matches, absolute path otherwise
            var insertText = $"@{node.Path}/";
            if (!string.IsNullOrEmpty(contextPath) &&
                node.Path.StartsWith(contextPath + "/", StringComparison.OrdinalIgnoreCase))
            {
                var relativeName = node.Path[(contextPath.Length + 1)..];
                insertText = $"@{relativeName}/";
            }

            yield return new AutocompleteItem(
                Label: $"@{node.Path}/",
                InsertText: insertText,
                Description: node.Name ?? node.NodeType,
                Category: node.NodeType ?? "Nodes",
                Priority: 1000 - (node.Order ?? 0),
                Kind: AutocompleteKind.Other
            );
        }
    }
}
