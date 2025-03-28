using MeshWeaver.Articles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Components;

public partial class FileBrowser
{
    [Inject] private IDialogService DialogService { get; set; }
    [Inject] private IArticleService ArticleService { get; set; }
    [Inject] private NavigationManager NavigationManager { get; set; }
    [Parameter] public string CollectionName { get; set; }
    [Parameter] public string CurrentPath { get; set; } = "/";

    private IReadOnlyCollection<CollectionItem> CollectionItems { get; set; } = [];



    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
    }


    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();
        Collection = CollectionName is null ? null : ArticleService.GetCollection(CollectionName);
        await RefreshContent();
    }


    private const string Root = "/";

    private async Task RefreshContent()
    {
        CurrentPath ??= "/";
        if (Collection is null)
            return;

        CollectionItems = await Collection.GetCollectionItemsAsync(CurrentPath);

    }

    private async Task NavigateToFolder(string folderName)
    {
        var newPath = CurrentPath.EndsWith("/")
            ? $"{CurrentPath}{folderName}"
            : $"{CurrentPath}/{folderName}";

        NavigationManager.NavigateTo($"/collections/{CollectionName}{newPath}");
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

        

        // Refresh the view
        await RefreshContent();
    }

    private ArticleCollection Collection { get; set; }
    private IEnumerable<CollectionItem> SelectedItems { get; set; } = new List<CollectionItem>();

    private async Task RowClicked(FluentDataGridRow<CollectionItem> row)
    {
        if (row.Item is FolderItem folder)
            await NavigateToFolder(row.Item.Name);
        else
        {
            //await DialogService.ShowDialogAsync<FilePreviewDialog>(new DialogParameters());
        }
    }
}
