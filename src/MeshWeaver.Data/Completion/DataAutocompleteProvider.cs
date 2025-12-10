namespace MeshWeaver.Data.Completion;

/// <summary>
/// Provides autocomplete items for data collections.
/// Returns all registered collections from DataContext.
/// </summary>
public class DataAutocompleteProvider(IWorkspace workspace) : IAutocompleteProvider
{
    /// <inheritdoc />
    public Task<IEnumerable<AutocompleteItem>> GetItemsAsync(string query, CancellationToken ct = default)
    {
        var dataContext = workspace.DataContext;

        // Get all collection names from TypeSources
        var items = dataContext.TypeSources.Keys
            .Select(collectionName => new AutocompleteItem(
                Label: collectionName,
                InsertText: $"@data/{collectionName} ",
                Description: $"Data collection: {collectionName}",
                Category: "Data Collections",
                Priority: 0,
                Kind: AutocompleteKind.Other
            ));

        return Task.FromResult(items);
    }
}
