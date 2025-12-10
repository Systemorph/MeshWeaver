#nullable enable

namespace MeshWeaver.Data.Completion;

/// <summary>
/// Interface for providers that contribute autocomplete items.
/// Providers are registered in hub DI and aggregated when handling AutocompleteRequest.
/// </summary>
public interface IAutocompleteProvider
{
    /// <summary>
    /// Gets autocomplete items from this provider.
    /// </summary>
    /// <param name="query">The search query (text being typed after the prefix).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of autocomplete items.</returns>
    Task<IEnumerable<AutocompleteItem>> GetItemsAsync(string query, CancellationToken ct = default);
}
