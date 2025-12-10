#nullable enable

using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Mesh.Completion;

/// <summary>
/// Provides autocomplete items for registered mesh namespaces (prefixes).
/// Returns items like "@pricing/", "@agent/", "@data/" based on registered MeshNamespaces.
/// </summary>
public class MeshCatalogAutocompleteProvider(IMeshCatalog meshCatalog) : IAutocompleteProvider
{
    private const int PrefixCategoryPriority = 1800;

    /// <inheritdoc />
    public async Task<IEnumerable<AutocompleteItem>> GetItemsAsync(string query, CancellationToken ct = default)
    {
        var namespaces = await meshCatalog.GetNamespacesAsync(ct);

        return namespaces.Select(ns => new AutocompleteItem(
            Label: $"@{ns.AddressType}/",
            InsertText: $"@{ns.AddressType}/",
            Description: ns.Description ?? ns.Name,
            Category: "Prefixes",
            Priority: PrefixCategoryPriority - ns.DisplayOrder,
            Kind: AutocompleteKind.Other
        ));
    }
}
