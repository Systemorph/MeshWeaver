#nullable enable

using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Mesh.Completion;

/// <summary>
/// Provides autocomplete items for registered mesh namespaces (prefixes).
/// Returns items like "@pricing/", "@agent/", "@model/" based on registered MeshNamespaces
/// plus reserved prefixes (agent, model).
/// </summary>
public class MeshCatalogAutocompleteProvider(IMeshCatalog? meshCatalog) : IAutocompleteProvider
{
    private const int PrefixCategoryPriority = 1800;

    /// <inheritdoc />
    public async Task<IEnumerable<AutocompleteItem>> GetItemsAsync(string query, CancellationToken ct = default)
    {
        var items = new List<AutocompleteItem>();

        // Get namespaces from mesh catalog
        if (meshCatalog != null)
        {
            var namespaces = await meshCatalog.GetNamespacesAsync(ct);
            foreach (var ns in namespaces)
            {
                items.Add(new AutocompleteItem(
                    Label: $"@{ns.AddressType}/",
                    InsertText: $"@{ns.AddressType}/",
                    Description: ns.Description ?? ns.Name,
                    Category: "Prefixes",
                    Priority: PrefixCategoryPriority - ns.DisplayOrder,
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

        return items;
    }
}
