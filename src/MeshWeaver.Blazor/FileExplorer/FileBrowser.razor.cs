using MeshWeaver.ContentCollections;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.FileExplorer;

public partial class FileBrowser
{
    [Inject] private IDialogService DialogService { get; set; } = null!;
    [Inject] private IContentService ContentService { get; set; } = null!;
    [Inject] private NavigationManager NavigationManager { get; set; } = null!;
    [Inject] private IToastService ToastService { get; set; } = null!;
    [Parameter] public string CollectionName { get; set; } = "";
    [Parameter] public string CurrentPath { get; set; } = "/";
    [Parameter] public string TopLevelPath { get; set; } = "";
    [Parameter] public bool Embed { get; set; }
    [Parameter]public bool CreatePath { get; set; }
    [Parameter] public bool ShowNewArticle { get; set; }
    [Parameter] public bool ShowCollectionSelection { get; set; }
    private IReadOnlyCollection<CollectionItem> CollectionItems { get; set; } = [];
    
    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();
        Collection = CollectionName is null ? null : ContentService.GetCollection(CollectionName);
        if (CreatePath && Collection is not null)
            await Collection.CreateFolderAsync(CurrentPath);
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

        Collection = CollectionName is null ? null : ContentService.GetCollection(CollectionName);
        await RefreshContentAsync();
        await InvokeAsync(StateHasChanged);
    }


    private ContentCollection? Collection { get; set; }
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
        var dialog = await DialogService.ShowDialogAsync<CreateFolderDialog, CreateFolderModel>(new CreateFolderModel(Collection, CurrentPath, CollectionItems), parameters);
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
        return item switch
        {
            FolderItem folder => GetLink(folder),
            FileItem file => GetLink(file),
            _ => ""
        };
    }

    private string GetLink(FolderItem item)
    {
        return $"/collections/{CollectionName}{item.Path}";
    }
    private string GetLink(FileItem item)
    {
        return $"/content/{CollectionName}{item.Path}";
    }

    FluentInputFile myFileByStream = default!;
    int progressPercent;
    string progressTitle = "";


    private async Task OnFileUploadedAsync(FluentInputFileEventArgs file)
    {
        progressPercent = file.ProgressPercent;
        progressTitle = file.ProgressTitle;
        try
        {
            if (Collection != null)
            {
                await Collection.SaveFileAsync(CurrentPath, file.Name, file.Stream);
                progressPercent = 100;
                ToastService.ShowSuccess($"File {file.Name} successfully uploaded.");
            }
        }
        catch(Exception e)
        {
            ToastService.ShowError($"Error uploading {file.Name}: {e.Message}");
        }

    }

    private async Task OnCompleted(IEnumerable<FluentInputFileEventArgs> files)
    {
        progressPercent = 0;
        progressTitle = "";
        await RefreshContentAsync();
    }

    private async Task ChangePath(string path)
    {
        CurrentPath = path;
        await RefreshContentAsync();
    }
}
