#nullable enable

using System.Runtime.CompilerServices;
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
    public IObservable<AutocompleteItem> GetItems(string query, string? contextPath = null) =>
        AutocompleteProviderObservable.FromAsyncEnumerable(ct => EnumerateAsync(query, ct));

    private async IAsyncEnumerable<AutocompleteItem> EnumerateAsync(
        string query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (addressCatalog == null)
            yield break;

        // Extract address type from query (e.g., "@pricing/MS" → "pricing")
        if (!query.StartsWith("@"))
            yield break;

        var pathPart = query.TrimStart('@');
        var segments = pathPart.Split('/');
        if (segments.Length < 1)
            yield break;

        var addressType = segments[0];
        if (string.IsNullOrEmpty(addressType))
            yield break;

        var addressIds = await addressCatalog.GetAddressIdsAsync(addressType, ct);

        foreach (var id in addressIds)
        {
            yield return new AutocompleteItem(
                Label: $"@{addressType}/{id}/",
                InsertText: $"@{addressType}/{id}/",
                Description: $"{addressType} address",
                Category: addressType,
                Priority: AddressIdCategoryPriority,
                Kind: AutocompleteKind.Other
            );
        }
    }
}
