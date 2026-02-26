using System.Runtime.CompilerServices;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Generic autocomplete provider that queries child nodes from the mesh catalog.
/// Uses the hub's address as the parent path to find children.
/// </summary>
public class MeshNodeAutocompleteProvider(IMeshCatalog meshCatalog, IMessageHub hub) : IAutocompleteProvider
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

        // Query for child nodes and yield results
        await foreach (var node in meshCatalog.QueryAsync(parentPath, query, DefaultMaxResults, ct))
        {
            yield return new AutocompleteItem(
                Label: $"@{node.Path}/",
                InsertText: $"@{node.Path}/",
                Description: node.Name ?? node.NodeType,
                Category: node.NodeType ?? "Nodes",
                Priority: 1000 - (node.Order ?? 0),
                Kind: AutocompleteKind.Other
            );
        }
    }
}
