#nullable enable

using System.Reactive.Linq;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh.Threading;

namespace MeshWeaver.Mesh.Completion;

/// <summary>
/// Provides autocomplete items for known address IDs within an address type.
/// Returns items like "@pricing/MS-2024/" based on the IAddressCatalogService.
/// <para>Fully reactive — the single catalog round-trip bridges through the
/// <see cref="IIoPool"/> (no bare <c>Observable.FromAsync</c>, no async-enumerable);
/// the ids fan out via <c>SelectMany</c>.</para>
/// </summary>
public class AddressCatalogAutocompleteProvider(IAddressCatalogService? addressCatalog) : IAutocompleteProvider
{
    private const int AddressIdCategoryPriority = 1600;

    /// <inheritdoc />
    public IObservable<AutocompleteItem> GetItems(string query, string? contextPath = null)
    {
        if (addressCatalog is null || !query.StartsWith('@'))
            return Observable.Empty<AutocompleteItem>();

        // Extract address type from query (e.g., "@pricing/MS" → "pricing").
        var addressType = query.TrimStart('@').Split('/')[0];
        if (string.IsNullOrEmpty(addressType))
            return Observable.Empty<AutocompleteItem>();

        return IoPool.Unbounded.Run(ct => addressCatalog.GetAddressIdsAsync(addressType, ct))
            .SelectMany(ids => ids.ToObservable())
            .Select(id => new AutocompleteItem(
                Label: $"@{addressType}/{id}/",
                InsertText: $"@{addressType}/{id}/",
                Description: $"{addressType} address",
                Category: addressType,
                Priority: AddressIdCategoryPriority,
                Kind: AutocompleteKind.Other));
    }
}
