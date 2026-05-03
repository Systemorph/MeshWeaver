using System.Reactive.Linq;

namespace MeshWeaver.Data.Completion;

/// <summary>
/// Provides autocomplete items for data collections.
/// Returns all registered collections from DataContext.
/// </summary>
public class DataAutocompleteProvider(IWorkspace workspace) : IAutocompleteProvider
{
    /// <inheritdoc />
    public string? Prefix => "data";

    /// <inheritdoc />
    public IObservable<AutocompleteItem> GetItems(string query, string? contextPath = null)
    {
        // Pure in-memory enumeration of registered TypeSources — no async I/O.
        var dataContext = workspace.DataContext;
        var address = workspace.Hub.Address;
        var addressStr = address.ToString();

        return dataContext.TypeSources.Keys
            .Select(collectionName =>
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

                return new AutocompleteItem(
                    Label: collectionName,
                    InsertText: $"@{address}/data/{collectionName} ",
                    Description: $"Data collection: {collectionName}",
                    Category: "Data Collections",
                    Priority: priority,
                    Kind: AutocompleteKind.Other);
            })
            .ToObservable();
    }
}
