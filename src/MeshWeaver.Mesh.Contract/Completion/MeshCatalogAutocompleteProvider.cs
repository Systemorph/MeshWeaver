
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Mesh.Completion;

/// <summary>
/// Provides autocomplete items for registered mesh nodes.
/// Returns items like "@app/", "@pricing/" based on registered MeshNodes
/// plus reserved prefixes (agent, model).
/// </summary>
public class MeshCatalogAutocompleteProvider(IMeshCatalog? meshCatalog) : IAutocompleteProvider
{
    private const int PrefixCategoryPriority = 1800;

    /// <inheritdoc />
    public Task<IEnumerable<AutocompleteItem>> GetItemsAsync(string query, CancellationToken ct = default)
    {
        var items = new List<AutocompleteItem>();

        // Get nodes from mesh catalog - use top-level nodes (single segment) for autocomplete
        if (meshCatalog != null)
        {
            var topLevelNodes = meshCatalog.Configuration.Nodes.Values
                .Where(n => n.Segments.Count == 1)
                .OrderBy(n => n.DisplayOrder ?? int.MaxValue)
                .ThenBy(n => n.Name);

            foreach (var node in topLevelNodes)
            {
                items.Add(new AutocompleteItem(
                    Label: $"@{node.Path}/",
                    InsertText: $"@{node.Path}/",
                    Description: node.Description ?? node.Name,
                    Category: "Prefixes",
                    Priority: PrefixCategoryPriority - (node.DisplayOrder ?? 0),
                    Kind: AutocompleteKind.Other
                ));
            }
        }

        // Add reserved prefixes (agent, model) if not already present
        if (!items.Any(i => i.Label == "@agent/"))
        {
            items.Add(new AutocompleteItem(
                Label: "@agent/",
                InsertText: "@agent/",
                Description: "Select an AI agent",
                Category: "Prefixes",
                Priority: PrefixCategoryPriority,
                Kind: AutocompleteKind.Agent
            ));
        }

        if (!items.Any(i => i.Label == "@model/"))
        {
            items.Add(new AutocompleteItem(
                Label: "@model/",
                InsertText: "@model/",
                Description: "Select an AI model",
                Category: "Prefixes",
                Priority: PrefixCategoryPriority,
                Kind: AutocompleteKind.Other
            ));
        }

        return Task.FromResult<IEnumerable<AutocompleteItem>>(items);
    }
}
