using MeshWeaver.ContentCollections;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor.FileExplorer;

public partial class FileBrowser
{
    [Inject] private IDialogService DialogService { get; set; } = null!;
    private IContentService ContentService => Hub.ServiceProvider.GetRequiredService<IContentService>();
    [Inject] private IToastService ToastService { get; set; } = null!;
    [Inject] private IMessageHub Hub { get; set; } = null!;
    [Inject] private IJSRuntime JSRuntime { get; set; } = null!;
    [Parameter] public string? CollectionName { get; set; } = "";
    [Parameter] public string? CurrentPath { get; set; } = "/";
    [Parameter] public string TopLevelPath { get; set; } = "";
    [Parameter] public bool Embed { get; set; }
    [Parameter] public bool CreatePath { get; set; }
    [Parameter] public bool ShowNewArticle { get; set; }
    [Parameter] public ContentCollectionConfig? CollectionConfiguration { get; set; }
    [Parameter] public Address? Address { get; set; }
    [Parameter] public bool IsReadOnly { get; set; }
    private IReadOnlyCollection<CollectionItem> CollectionItems { get; set; } = [];
    FluentInputFile myFileByStream = default!;
    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();

        // Initialize collection if configuration is provided and collection doesn't exist
        if (CollectionConfiguration is not null)
            ContentService.AddConfiguration(CollectionConfiguration);

        // Try to get the collection
        Collection = CollectionName is null ? null : await ContentService.GetCollectionAsync(CollectionName);

        if (CreatePath && Collection is not null && CurrentPath is not null)
            await Collection.CreateFolderAsync(CurrentPath);
        await RefreshContentAsync();
    }



    private const string Root = "/";

    private async Task RefreshContentAsync()
    {
        CurrentPath ??= "/";
        if (Collection is null)
            return;

        CollectionItems = await Collection.GetCollectionItemsAsync(CurrentPath!);
        SelectedItems = [];
    }




    private async Task CollectionChanged(string collection)
    {
        if (collection == CollectionName)
            return;
        CollectionName = collection;

        Collection = CollectionName is null ? null : await ContentService.GetCollectionAsync(CollectionName);
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
        var dialog = await DialogService.ShowDialogAsync<CreateFolderDialog, CreateFolderModel>(new CreateFolderModel(Collection!, CurrentPath!, CollectionItems), parameters);
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
        var deleteModel = new DeleteModel(Collection!, SelectedItems);
        var dialog = await DialogService.ShowDialogAsync<DeleteDialog, DeleteModel>(deleteModel, parameters);
        var result = await dialog.Result;
        if (!result.Cancelled)
        {
            await RefreshContentAsync();

            // Show errors if any occurred
            if (deleteModel.Errors.Any())
            {
                foreach (var error in deleteModel.Errors)
                {
                    ToastService.ShowError(error);
                }
            }
            else
            {
                var itemCount = deleteModel.ItemsToDelete.Count();
                ToastService.ShowSuccess($"Successfully deleted {itemCount} item{(itemCount > 1 ? "s" : "")}.");
            }
        }
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
        // Use full address string (e.g., "Cornerstone/Microsoft/2026") for URL generation
        // Both /static and /content endpoints use IMeshCatalog.ResolvePathAsync for dynamic resolution
        var addressString = Address?.ToString();

        // Encode slashes in collection name as ~ to avoid URL path parsing issues
        // e.g., "Submissions@Microsoft/2026" becomes "Submissions@Microsoft~2026"
        var encodedCollection = EncodeCollectionName(CollectionName ?? "");

        if (ShouldDownload(item.Name))
        {
            // Download files: /static/{address}/{encodedCollection}{item.Path}?download
            return $"/static/{addressString}/{encodedCollection}{item.Path}?download";
        }

        // Previewable files: /content/{address}/{encodedCollection}{item.Path}
        return $"/content/{addressString}/{encodedCollection}{item.Path}";
    }

    /// <summary>
    /// Encodes a collection name for use in URLs by replacing '/' with '~'.
    /// This avoids issues with ASP.NET Core URL-decoding %2F before route matching.
    /// </summary>
    private static string EncodeCollectionName(string collectionName) => collectionName.Replace("/", "~");

    /// <summary>
    /// Decodes a collection name from a URL by replacing '~' back to '/'.
    /// </summary>
    internal static string DecodeCollectionName(string encodedName) => encodedName.Replace("~", "/");

    private static bool ShouldDownload(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".xlsx" or ".xls" or ".docx" or ".doc" or ".pptx" or ".ppt" or ".zip" => true,
            _ => false
        };
    }

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
                await Collection.SaveFileAsync(CurrentPath!, file.Name, file.Stream!);
                progressPercent = 100;
                ToastService.ShowSuccess($"File {file.Name} successfully uploaded.");
            }
        }
        catch (Exception e)
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

    private async Task HandleFileClick(FileItem file)
    {
        // For files that should be downloaded, trigger download via JavaScript
        if (ShouldDownload(file.Name))
        {
            var downloadUrl = GetLink(file);
            await JSRuntime.InvokeVoidAsync("open", downloadUrl, "_blank");
        }
        // For displayable files, let the href handle navigation naturally
        // (preventDefault is false, so navigation happens)
    }

    private async Task DownloadSelectedAsync()
    {
        var filesToDownload = SelectedItems.OfType<FileItem>().ToList();

        if (!filesToDownload.Any())
        {
            ToastService.ShowWarning("Please select at least one file to download.");
            return;
        }

        try
        {
            // Use full address string for URL generation
            var addressString = Address?.ToString();

            // Encode slashes in collection name as ~ to avoid URL path parsing issues
            var encodedCollection = EncodeCollectionName(CollectionName ?? "");

            // Download each file by opening it in a new window/tab
            // The browser will handle the download based on the content-disposition header
            foreach (var file in filesToDownload)
            {
                var downloadUrl = $"/static/{addressString}/{encodedCollection}{file.Path}?download";
                await JSRuntime.InvokeVoidAsync("open", downloadUrl, "_blank");
                // Add a small delay between downloads to avoid browser blocking multiple downloads
                await Task.Delay(100);
            }

            ToastService.ShowSuccess($"Downloading {filesToDownload.Count} file{(filesToDownload.Count > 1 ? "s" : "")}...");

            // Clear selection after download
            SelectedItems = [];
        }
        catch (Exception ex)
        {
            ToastService.ShowError($"Error downloading files: {ex.Message}");
        }
    }
}
