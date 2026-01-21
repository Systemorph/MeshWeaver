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
    /// <param name="contextPath">Optional context path for proximity-based ordering.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of autocomplete items.</returns>
    IAsyncEnumerable<AutocompleteItem> GetItemsAsync(string query, string? contextPath = null, CancellationToken ct = default);
}
