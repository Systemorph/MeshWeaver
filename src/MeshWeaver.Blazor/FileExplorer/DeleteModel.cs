using MeshWeaver.Articles;

namespace MeshWeaver.Blazor.FileExplorer;

public class DeleteModel(ArticleCollection collection, IEnumerable<CollectionItem> itemsToDelete)
{
    public IEnumerable<CollectionItem> ItemsToDelete { get; } = itemsToDelete;

    public int Count => ItemsToDelete.Count();

    public bool HasItems => Count > 0;

    public string ConfirmationMessage => Count == 1
        ? $"Are you sure you want to delete '{ItemsToDelete.First().Name}'?"
        : $"Are you sure you want to delete {Count} items?";

    public async Task DeleteAsync()
    {
        foreach (var item in ItemsToDelete)
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
    }
}
