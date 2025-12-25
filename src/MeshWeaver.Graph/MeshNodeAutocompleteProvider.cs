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
    public async Task<IEnumerable<AutocompleteItem>> GetItemsAsync(string query, CancellationToken ct = default)
    {
        // Use the hub's address as the parent path
        var parentPath = hub.Address.ToString();

        // Query for child nodes and collect results
        var items = new List<AutocompleteItem>();
        await foreach (var node in meshCatalog.QueryAsync(parentPath, query, DefaultMaxResults, ct))
        {
            items.Add(new AutocompleteItem(
                Label: $"@{node.Path}/",
                InsertText: $"@{node.Path}/",
                Description: node.Name ?? node.Description ?? node.NodeType,
                Category: node.NodeType ?? "Nodes",
                Priority: 1000 - node.DisplayOrder,
                Kind: AutocompleteKind.Other
            ));
        }

        return items;
    }
}
