using MeshWeaver.Articles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.FileExplorer;

public partial class FileBrowser
{
    [Inject] private IDialogService DialogService { get; set; }
    [Inject] private IArticleService ArticleService { get; set; }
    [Inject] private NavigationManager NavigationManager { get; set; }
    [Inject] private IToastService ToastService { get; set; }
    [Parameter] public string CollectionName { get; set; }
    [Parameter] public string CurrentPath { get; set; } = "/";

    private IReadOnlyCollection<CollectionItem> CollectionItems { get; set; } = [];
    
    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();
        Collection = CollectionName is null ? null : ArticleService.GetCollection(CollectionName);
        await RefreshContentAsync();
    }


    private const string Root = "/";

    private async Task RefreshContentAsync()
    {
        CurrentPath ??= "/";
        if (Collection is null)
            return;

        CollectionItems = await Collection.GetCollectionItemsAsync(CurrentPath);
        SelectedItems = [];
    }

    private void NavigateToFolder(string folderName)
    {
        var newPath = CurrentPath.EndsWith("/")
            ? $"{CurrentPath}{folderName}"
            : $"{CurrentPath}/{folderName}";

        NavigationManager.NavigateTo($"/collections/{CollectionName}{newPath}");
    }



    private async Task CollectionChanged(string collection)
    {
        if (collection == CollectionName)
            return;
        CollectionName = collection;

        Collection = CollectionName is null ? null : ArticleService.GetCollection(CollectionName);
        await RefreshContentAsync();
        await InvokeAsync(StateHasChanged);
    }


    private ArticleCollection Collection { get; set; }
    private IEnumerable<CollectionItem> SelectedItems { get; set; } = new List<CollectionItem>();


    private async Task AddFolderAsync()
    {
        DialogParameters<CreateFolderModel> parameters = new()
        {
            Title = $"Create Folder",
            PrimaryAction = "Create",
            PrimaryActionEnabled = false,
            SecondaryAction = "Cancel",
            Width = "500px",
            TrapFocus = true,
            Modal = true,
            PreventScroll = true,
        };
        var dialog = await DialogService.ShowDialogAsync<CreateFolderDialog, CreateFolderModel>(new CreateFolderModel(Collection, CollectionItems), parameters);
        var result = await dialog.Result;
        if (!result.Cancelled)
            await RefreshContentAsync();
    }
    private Task NewArticleAsync(MouseEventArgs arg)
    {
        throw new NotImplementedException();
    }

    private async Task DeleteAsync()
    {
        DialogParameters<DeleteDialog> parameters = new()
        {
            Title = $"Create Folder",
            PrimaryAction = "Create",
            PrimaryActionEnabled = false,
            SecondaryAction = "Cancel",
            Width = "500px",
            TrapFocus = true,
            Modal = true,
            PreventScroll = true,
        };
        var dialog = await DialogService.ShowDialogAsync<DeleteDialog, DeleteModel>(new DeleteModel(Collection, SelectedItems), parameters);
        var result = await dialog.Result;
        if (!result.Cancelled)
            await RefreshContentAsync();
    }

    private string GetLink(CollectionItem item)
    {
        return item is FolderItem folder
            ? $"/collections/{CollectionName}{folder.Path}"
            : $"/content/{CollectionName}{item.Path}";
    }

    FluentInputFile myFileByStream = default!;
    int progressPercent;
    string progressTitle;


    private async Task OnFileUploadedAsync(FluentInputFileEventArgs file)
    {
        progressPercent = file.ProgressPercent;
        progressTitle = file.ProgressTitle;
        try
        {
            await Collection.SaveFileAsync(CurrentPath, file.Name, file.Stream);
            progressPercent = 100;
            ToastService.ShowSuccess($"File {file.Name} successfully uploaded.");
        }
        catch(Exception e)
        {
            ToastService.ShowError($"Error uploading {file.Name}: {e.Message}");
        }

    }

    private async Task OnCompleted(IEnumerable<FluentInputFileEventArgs> files)
    {
        progressPercent = 0;
        progressTitle = null;
        await RefreshContentAsync();
    }

}
