using MeshWeaver.Articles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace MeshWeaver.Blazor.Components;

public partial class FileBrowser
{
    [Inject] private IArticleService ArticleService { get; set; }
    [Parameter] public string CollectionName { get; set; }
    [Parameter] public string CurrentPath { get; set; } = "/";
    private IReadOnlyCollection<string> Folders { get; set; } = [];
    private IReadOnlyCollection<string> Files { get; set; } = [];

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
        if(Collection is null)
            return;
        Folders = await Collection.GetFoldersAsync(CurrentPath);
        Files = await Collection.GetFilesAsync(CurrentPath);

    }
    private async Task NavigateToFolder(string folderName)
    {
        CurrentPath = CurrentPath.EndsWith("/")
            ? $"{CurrentPath}{folderName}"
            : $"{CurrentPath}/{folderName}";

        await RefreshContent();
    }

    private HashSet<string> selectedFiles = new();
    private void SelectFile(string fileName)
    {
        if (!selectedFiles.Add(fileName))
            selectedFiles.Remove(fileName);
    }

    private async Task CreateFolderRequested()
    {
        if(Collection is null)
            return;
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

    private ArticleCollection Collection { get; set; }
}
