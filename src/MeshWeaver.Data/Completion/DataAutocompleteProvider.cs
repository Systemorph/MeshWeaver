using System.Runtime.CompilerServices;

namespace MeshWeaver.Data.Completion;

/// <summary>
/// Provides autocomplete items for data collections.
/// Returns all registered collections from DataContext.
/// </summary>
public class DataAutocompleteProvider(IWorkspace workspace) : IAutocompleteProvider
{
    /// <inheritdoc />
    public async IAsyncEnumerable<AutocompleteItem> GetItemsAsync(
        string query,
        string? contextPath = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask; // Satisfy async requirement

        var dataContext = workspace.DataContext;
        var address = workspace.Hub.Address;
        var addressStr = address.ToString();

        // Get all collection names from TypeSources
        // Format: addressType/addressId/data/collectionName
        foreach (var collectionName in dataContext.TypeSources.Keys)
        {
            var priority = 500; // base priority for data collections

            // Proximity boost: if contextPath is within the same address
            if (!string.IsNullOrEmpty(contextPath) &&
                !string.IsNullOrEmpty(addressStr) &&
                (contextPath.Equals(addressStr, StringComparison.OrdinalIgnoreCase) ||
                 contextPath.StartsWith(addressStr + "/", StringComparison.OrdinalIgnoreCase)))
            {
                priority += 1000; // local data collection
            }

            yield return new AutocompleteItem(
                Label: collectionName,
                InsertText: $"@{address}/data/{collectionName} ",
                Description: $"Data collection: {collectionName}",
                Category: "Data Collections",
                Priority: priority,
                Kind: AutocompleteKind.Other
            );
        }
    }
}
