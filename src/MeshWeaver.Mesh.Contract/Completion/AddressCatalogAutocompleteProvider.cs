#nullable enable

using MeshWeaver.Data.Completion;

namespace MeshWeaver.Mesh.Completion;

/// <summary>
/// Provides autocomplete items for known address IDs within an address type.
/// Returns items like "@pricing/MS-2024/" based on the IAddressCatalogService.
/// </summary>
public class AddressCatalogAutocompleteProvider(IAddressCatalogService? addressCatalog) : IAutocompleteProvider
{
    private const int AddressIdCategoryPriority = 1600;

    /// <inheritdoc />
    public async Task<IEnumerable<AutocompleteItem>> GetItemsAsync(string query, CancellationToken ct = default)
    {
        if (addressCatalog == null)
            return [];

        // Extract address type from query (e.g., "@pricing/MS" → "pricing")
        if (!query.StartsWith("@"))
            return [];

        var pathPart = query.TrimStart('@');
        var segments = pathPart.Split('/');
        if (segments.Length < 1)
            return [];

        var addressType = segments[0];
        if (string.IsNullOrEmpty(addressType))
            return [];

        var addressIds = await addressCatalog.GetAddressIdsAsync(addressType, ct);

        return addressIds.Select(id => new AutocompleteItem(
            Label: $"@{addressType}/{id}/",
            InsertText: $"@{addressType}/{id}/",
            Description: $"{addressType} address",
            Category: addressType,
            Priority: AddressIdCategoryPriority,
            Kind: AutocompleteKind.Other
        ));
    }
}
