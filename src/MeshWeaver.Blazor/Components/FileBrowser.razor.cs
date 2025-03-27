using MeshWeaver.Articles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace MeshWeaver.Blazor.Components;

public partial class FileBrowser
{
    [Inject] private IArticleService ArticleService { get; set; }
    [Parameter] public string CollectionName { get; set; }
    [Parameter] public string CurrentPath { get; set; } = "/";

    private IReadOnlyCollection<FolderInfo> Folders { get; set; } = [];
    private IReadOnlyCollection<FileDetails> Files { get; set; } = [];

    private IQueryable<FolderGridItem> FolderItems => Folders
        .Select(f => new FolderGridItem(f.Path, f.Name, f.ItemCount))
        .AsQueryable();

    private IQueryable<FileGridItem> FileItems => Files
        .Select(f => new FileGridItem(f.Path, f.Name, f.LastModified))
        .AsQueryable();

    private IEnumerable<FolderGridItem> SelectedFolders { get; set; } = [];
    private IEnumerable<FileGridItem> SelectedFiles { get; set; } = [];

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        Collection = CollectionName is null ? null : ArticleService.GetCollection(CollectionName);
        await RefreshContent();
    }

    private Task NavigateToRoot()
    {
        return NavigateToBreadcrumb("/");
    }

    private const string Root = "/";
    private async Task NavigateToBreadcrumb(string folderName)
    {
        var path = Root;
        var parts = CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == folderName)
            {
                path += string.Join("/", parts.Take(i + 1));
                break;
            }
            path += parts[i] + "/";
        }

        CurrentPath = path;
        await RefreshContent();
    }

    private async Task RefreshContent()
    {
        CurrentPath ??= "/";
        if (Collection is null)
            return;

        Folders = await Collection.GetFoldersAsync(CurrentPath);
        Files = await Collection.GetFilesAsync(CurrentPath);

        // Reset selections when navigating
        SelectedFolders = [];
        SelectedFiles = [];
    }

    private async Task NavigateToFolder(string folderName)
    {
        CurrentPath = CurrentPath.EndsWith("/")
            ? $"{CurrentPath}{folderName}"
            : $"{CurrentPath}/{folderName}";

        await RefreshContent();
    }

    private async Task CreateFolderRequested()
    {
        if (Collection is null)
            return;
        // Add implementation for creating a folder
    }

    private async Task HandleFileUpload(InputFileChangeEventArgs e)
    {
        if (Collection is null)
            return;
        foreach (var file in e.GetMultipleFiles())
        {
            await Collection.SaveFileAsync(CurrentPath, file.Name, file.OpenReadStream());
        }
        await RefreshContent();
    }

    private async Task CollectionChanged(string collection)
    {
        if (collection == CollectionName)
            return;
        CollectionName = collection;

        Collection = CollectionName is null ? null : ArticleService.GetCollection(CollectionName);
        await RefreshContent();
        await InvokeAsync(StateHasChanged);
    }

    private async Task DeleteSelectedItems()
    {
        if (Collection is null)
            return;

        // Implementation for deleting selected items would go here
        // For example:
        // foreach (var folder in SelectedFolders)
        // {
        //     await Collection.DeleteFolderAsync(folder.Path);
        // }

        // foreach (var file in SelectedFiles)
        // {
        //     await Collection.DeleteFileAsync(file.Path);
        // }

        // Clear selections after deletion
        SelectedFolders = [];
        SelectedFiles = [];

        // Refresh the view
        await RefreshContent();
    }

    private ArticleCollection Collection { get; set; }

    // Grid Item Models
    public record FolderGridItem(string Path, string Name, int ItemCount);
    public record FileGridItem(string Path, string Name, DateTime LastModified);
}
