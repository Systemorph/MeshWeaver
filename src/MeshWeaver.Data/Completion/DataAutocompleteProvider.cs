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

        // Get all collection names from TypeSources
        // Format: addressType/addressId/data/collectionName
        foreach (var collectionName in dataContext.TypeSources.Keys)
        {
            yield return new AutocompleteItem(
                Label: collectionName,
                InsertText: $"@{address}/data/{collectionName} ",
                Description: $"Data collection: {collectionName}",
                Category: "Data Collections",
                Priority: 0,
                Kind: AutocompleteKind.Other
            );
        }
    }
}
