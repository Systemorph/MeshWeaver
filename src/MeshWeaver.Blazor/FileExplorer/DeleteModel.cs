using MeshWeaver.ContentCollections;

namespace MeshWeaver.Blazor.FileExplorer;

/// <summary>
/// Model for confirming and executing the deletion of one or more collection items.
/// </summary>
/// <param name="collection">The content collection to delete items from.</param>
/// <param name="itemsToDelete">The items selected for deletion.</param>
public class DeleteModel(ContentCollection collection, IEnumerable<CollectionItem> itemsToDelete)
{
    /// <summary>The collection items that will be deleted.</summary>
    public IEnumerable<CollectionItem> ItemsToDelete { get; } = itemsToDelete;

    /// <summary>Number of items selected for deletion.</summary>
    public int Count => ItemsToDelete.Count();

    /// <summary>True when at least one item is selected for deletion.</summary>
    public bool HasItems => Count > 0;

    /// <summary>User-facing confirmation message, tailored for a single item or a bulk selection.</summary>
    public string ConfirmationMessage => Count == 1
        ? $"Are you sure you want to delete '{ItemsToDelete.First().Name}'?"
        : $"Are you sure you want to delete {Count} items?";

    /// <summary>Errors accumulated during <c>DeleteAsync</c>; one entry per item that failed to delete.</summary>
    public List<string> Errors { get; } = new();

    /// <summary>
    /// Iterates over <c>ItemsToDelete</c> and removes each via the collection's delete API.
    /// Failures are captured in <c>Errors</c> rather than propagated so partial success is visible.
    /// </summary>
    public async Task DeleteAsync()
    {
        Errors.Clear();
        foreach (var item in ItemsToDelete)
        {
            try
            {
                if (item is FolderItem folder)
                {
                    await collection.DeleteFolderAsync(folder.Path);
                }
                else if (item is FileItem file)
                {
                    await collection.DeleteFileAsync(file.Path);
                }
            }
            catch (Exception ex)
            {
                Errors.Add($"Failed to delete '{item.Name}': {ex.Message}");
            }
        }
    }
}
