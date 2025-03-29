using MeshWeaver.Articles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.FileExplorer;

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
        await RefreshContentAsync();
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

    private async Task DeleteSelectedItems()
    {
        if (Collection is null)
            return;

       

        // Refresh the view
        await RefreshContentAsync();
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
            : $"/file/{CollectionName}{item.Path}";
    }

    FluentInputFile myFileByStream = default!;
    int progressPercent;
    string progressTitle;

    List<string> Files = new();

    private async Task OnFileUploadedAsync(FluentInputFileEventArgs file)
    {
        progressPercent = file.ProgressPercent;
        progressTitle = file.ProgressTitle;

        var localFile = Path.GetTempFileName() + file.Name;
        Files.Add(localFile);

        // Write to the FileStream
        // See other samples: https://docs.microsoft.com/en-us/aspnet/core/blazor/file-uploads
        await using FileStream fs = new(localFile, FileMode.Create);

        await file.Stream!.CopyToAsync(fs);
        await file.Stream!.DisposeAsync();
    }

    private readonly List<string> uploadMessages = new();
    private async Task OnCompleted(IEnumerable<FluentInputFileEventArgs> files)
    {
        foreach (var file in files)
        {
            try
            {
                await Collection.SaveFileAsync(CurrentPath, file.Name, file.Stream);
                uploadMessages.Add($"File '{file.Name}' uploaded successfully.");
            }
            catch (Exception ex)
            {
                uploadMessages.Add($"Upload of file '{file.Name}' failed: {ex.Message}");
            }
        }
    }

}
